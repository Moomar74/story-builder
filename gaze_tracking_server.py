import json
import math
import os
import socket
import threading
import time
import struct
from collections import deque
from datetime import datetime
from db_manager import db

import cv2
import mediapipe as mp
from mediapipe.tasks.python import vision
from mediapipe.tasks.python import core
import numpy as np

# --- Configuration ---
BROADCASTER_IP = "127.0.0.1"
BROADCASTER_PORT = 6000
HUB_IP = "127.0.0.1"
HUB_PORT = 6001

MODEL_PATH = "face_landmarker.task"
LEFT_EYE_INDICES = [33, 160, 158, 133, 153, 144]
RIGHT_EYE_INDICES = [362, 385, 387, 263, 373, 380]

GAZE_SENSITIVITY_X = 3.0
GAZE_SENSITIVITY_Y = 6.0
GAZE_VERTICAL_OFFSET = -0.2
REVERSE_VERTICAL = True

# --- State ---
current_page = "SignIn"
current_user_id = "guest_user"
current_session_id = db.create_session(current_user_id)
gaze_history = {
    "SignIn": deque(maxlen=1000), "SignUp": deque(maxlen=1000),
    "StorySelection": deque(maxlen=1000), "StoryPlayer": deque(maxlen=1000),
    "StoryBuilder": deque(maxlen=1000)
}
gaze_batch_buffer = []
LAST_DB_FLUSH = time.time()
DB_FLUSH_INTERVAL = 10.0

hub_conn = None

def connect_to_hub():
    global hub_conn
    while True:
        try:
            hub_conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            hub_conn.connect((HUB_IP, HUB_PORT))
            print("[+] Gaze Worker: Connected to Hub.")
            break
        except:
            time.sleep(2)

def hub_listener():
    """Listens for commands from the Hub (relayed from C#)."""
    global current_page, current_user_id, current_session_id
    while True:
        try:
            data = hub_conn.recv(1024)
            if not data: break
            msg = data.decode('utf-8').strip()
            if msg.startswith("PAGE:"):
                # 1. Flush current buffer before switching
                if gaze_batch_buffer:
                    db.log_gaze_batch(current_session_id, current_page, gaze_batch_buffer)
                    gaze_batch_buffer.clear()
                
                # 2. Compute heatmap for the page we are LEAVING
                if current_user_id and current_user_id != "guest_user":
                    threading.Thread(target=db.compute_and_store_heatmap, args=(current_user_id, current_page), daemon=True).start()

                current_page = msg[5:]
                print(f"[*] Page changed to: {current_page}")
            elif msg.startswith("USER:"):
                # Flush for previous user
                if gaze_batch_buffer:
                    db.log_gaze_batch(current_session_id, current_page, gaze_batch_buffer)
                    gaze_batch_buffer.clear()
                
                current_user_id = msg[5:]
                current_session_id = db.create_session(current_user_id)
                print(f"[*] User session started: {current_user_id}")
            elif msg.startswith("GET_MENU_POS:"):
                # C# is requesting the optimal menu placement based on heatmaps
                if current_user_id and current_user_id != "guest_user":
                    page = msg[13:]
                    config = db.compute_adaptive_menu_position(current_user_id, page)
                    if config:
                        # Format: MENU_POS:x,y,quadrant,confidence
                        resp = f"MENU_POS:{config['menu_x']},{config['menu_y']},{config['menu_quadrant']},{config['confidence']}"
                        send_to_hub(resp)
        except: break

def send_to_hub(msg):
    if hub_conn:
        try:
            hub_conn.sendall((msg + "\n").encode('utf-8'))
        except: pass

def recv_all(sock, n):
    data = b''
    while len(data) < n:
        packet = sock.recv(n - len(data))
        if not packet: return None
        data += packet
    return data

def main():
    connect_to_hub()
    threading.Thread(target=hub_listener, daemon=True).start()
    
    # Initialize MediaPipe
    BaseOptions = core.base_options.BaseOptions
    FaceLandmarker = vision.FaceLandmarker
    FaceLandmarkerOptions = vision.FaceLandmarkerOptions
    options = FaceLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=MODEL_PATH),
        running_mode=vision.RunningMode.IMAGE,
        num_faces=1
    )
    landmarker = FaceLandmarker.create_from_options(options)
    
    # Connect to Frame Broadcaster
    frame_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    while True:
        try:
            frame_sock.connect((BROADCASTER_IP, BROADCASTER_PORT))
            print("[+] Gaze Worker: Connected to Frame Broadcaster.")
            break
        except:
            print("[.] Gaze Worker: Waiting for Frame Broadcaster...")
            time.sleep(2)
    
    last_gaze_x, last_gaze_y = 0.5, 0.5
    gaze_ema_alpha = 0.3
    frame_counter = 0
    
    try:
        while True:
            # 1. Receive frame from Broadcaster
            header = recv_all(frame_sock, 4)
            if not header: break
            size = struct.unpack(">L", header)[0]
            img_data = recv_all(frame_sock, size)
            if not img_data: break
            
            frame_counter += 1
            if frame_counter % 2 != 0: # Process every 2nd frame
                continue
            
            # Decode JPEG
            nparr = np.frombuffer(img_data, np.uint8)
            frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if frame is None: continue
            
            h, w, _ = frame.shape
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            
            # 2. Process Gaze
            results = landmarker.detect(mp_image)
            if not results.face_landmarks:
                send_to_hub("GAZE_STATUS:lost")
            else:
                send_to_hub("GAZE_STATUS:tracking")
                landmarks = results.face_landmarks[0]
                
                # Simple Pupil Center Calculation (Normalized)
                left_pupil = (landmarks[468].x, landmarks[468].y)
                right_pupil = (landmarks[473].x, landmarks[473].y)
                avg_pupil_x = (left_pupil[0] + right_pupil[0]) / 2
                avg_pupil_y = (left_pupil[1] + right_pupil[1]) / 2
                
                # Head turn compensation
                head_turn = (landmarks[1].x - 0.5) * 2.0
                raw_gaze_x = avg_pupil_x + head_turn * 0.4
                raw_gaze_y = avg_pupil_y
                
                # Scaling
                raw_gaze_x = (raw_gaze_x - 0.5) * GAZE_SENSITIVITY_X + 0.5
                if REVERSE_VERTICAL: raw_gaze_y = 1.0 - raw_gaze_y
                raw_gaze_y = (raw_gaze_y - 0.5) * GAZE_SENSITIVITY_Y + 0.5 + GAZE_VERTICAL_OFFSET
                
                # Smooth
                gaze_x = gaze_ema_alpha * raw_gaze_x + (1 - gaze_ema_alpha) * last_gaze_x
                gaze_y = gaze_ema_alpha * raw_gaze_y + (1 - gaze_ema_alpha) * last_gaze_y
                last_gaze_x, last_gaze_y = gaze_x, gaze_y
                
                gaze_x = max(0.0, min(1.0, gaze_x))
                gaze_y = max(0.0, min(1.0, gaze_y))
                
                # Report to Hub (Real-time for C#)
                send_to_hub(f"GAZE:{gaze_x:.4f},{gaze_y:.4f}")
                
                # Log to DB (for Heatmaps)
                gaze_batch_buffer.append((gaze_x, gaze_y))
                
                # Periodic Flush to Supabase
                global LAST_DB_FLUSH
                if time.time() - LAST_DB_FLUSH > DB_FLUSH_INTERVAL:
                    if gaze_batch_buffer:
                        db.log_gaze_batch(current_session_id, current_page, gaze_batch_buffer)
                        gaze_batch_buffer.clear()
                    LAST_DB_FLUSH = time.time()

                # Local Visual
                cv2.circle(frame, (int(gaze_x*w), int(gaze_y*h)), 10, (0, 255, 255), 2)
                cv2.imshow("Gaze Worker (Distributed)", frame)
                if cv2.waitKey(1) & 0xFF == ord('q'): break

    except Exception as e:
        print(f"[!] Gaze Worker Error: {e}")
    finally:
        frame_sock.close()
        hub_conn.close()
        cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
