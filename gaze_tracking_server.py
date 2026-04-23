"""
Gaze Tracking Server - dlib Facial Landmarks + Heatmap Generation
=================================================================
Tracks eye gaze using dlib's 68-point facial landmark predictor and
calculates screen gaze position using vector math between pupils 
and eye corners.

Sends gaze coordinates to the C# client via TCP on port 5002.
Messages sent to client:
  GAZE:<x>,<y>               - Normalized gaze position (0.0-1.0)
  BLINK:<state>              - Blink detection (0=open, 1=blink)
  GAZE_STATUS:<status>       - Tracking status (tracking/lost)

Controls:
  Press 'q' to quit
  Press 'h' to toggle heatmap display locally
  Press 'c' to clear current heatmap data
"""

import json
import math
import os
import socket
import threading
import time
from collections import deque
from datetime import datetime

import cv2
import mediapipe as mp
from mediapipe.tasks.python import vision
from mediapipe.tasks.python import core
import numpy as np

# ── MediaPipe FaceLandmarker Setup ────────────────────────────────
MODEL_PATH = "face_landmarker.task"

# MediaPipe Face Mesh indices for eyes (approximate mapping to dlib's 68)
# Left eye: [33, 160, 158, 133, 153, 144]
# Right eye: [362, 385, 387, 263, 373, 380]
# Irises: 468 (Left center), 473 (Right center)
LEFT_EYE_INDICES = [33, 160, 158, 133, 153, 144]
RIGHT_EYE_INDICES = [362, 385, 387, 263, 373, 380]

# ── Camera Setup ──────────────────────────────────────────────────
CAMERA_INDEX = 0  # 0 for integrated, 1 for Camo/Virtual Camera
# Increase these to make the gaze reach the edges of the screen
GAZE_SENSITIVITY_X = 3.0
GAZE_SENSITIVITY_Y = 6.0
GAZE_VERTICAL_OFFSET = -0.2
REVERSE_VERTICAL = True

# ── TCP Server ───────────────────────────────────────────────────
TCP_PORT = 5002
connected_clients = []

# ── Gaze Data Storage ────────────────────────────────────────────
# Store gaze points per page (SignIn, SignUp, StorySelection, StoryPlayer, StoryBuilder)
gaze_history = {
    "SignIn": deque(maxlen=1000),
    "SignUp": deque(maxlen=1000),
    "StorySelection": deque(maxlen=1000),
    "StoryPlayer": deque(maxlen=1000),
    "StoryBuilder": deque(maxlen=1000)
}
current_page = "SignIn"

# ── Heatmap Settings ─────────────────────────────────────────────
HEATMAP_WIDTH = 128
HEATMAP_HEIGHT = 80
local_heatmap_visible = False

def broadcast(message):
    """Send message to all connected C# clients."""
    if not message.endswith('\n'):
        message += '\n'
    msg_bytes = message.encode('utf-8')
    for conn in list(connected_clients):
        try:
            conn.sendall(msg_bytes)
        except Exception:
            try:
                connected_clients.remove(conn)
            except ValueError:
                pass

def handle_client(conn, addr):
    """Handle TCP client connection."""
    print(f"[+] Gaze tracking client connected: {addr}")
    connected_clients.append(conn)
    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            msg = data.decode('utf-8').strip()
            if msg.startswith("PAGE:"):
                # Client tells us which page is active
                global current_page
                old_page = current_page
                current_page = msg[5:]
                print(f"[*] Active page changed from {old_page} to: {current_page}")
                # Save previous page's heatmap
                save_heatmap(old_page)
            elif msg == "CLEAR_HEATMAP":
                if current_page in gaze_history:
                    gaze_history[current_page].clear()
                    print(f"[*] Cleared heatmap for {current_page}")
    except Exception:
        pass
    finally:
        try:
            connected_clients.remove(conn)
        except ValueError:
            pass
        conn.close()
        print(f"[-] Gaze tracking client disconnected: {addr}")

def start_tcp():
    """Start TCP server thread."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR,1)
    server.bind(("0.0.0.0", TCP_PORT))
    server.listen(5)
    print(f"[*] Gaze Tracking TCP Server on port {TCP_PORT}")
    while True:
        conn, addr = server.accept()
        t = threading.Thread(target=handle_client, args=(conn, addr), daemon=True)
        t.start()

def download_predictor():
    """Download the face landmarker model if not present."""
    if not os.path.exists(MODEL_PATH):
        print(f"[*] Downloading MediaPipe face landmarker model...")
        import urllib.request
        url = "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task"
        try:
            urllib.request.urlretrieve(url, MODEL_PATH)
            print("[+] Model downloaded.")
        except Exception as e:
            print(f"[!] Failed to download model: {e}")
            return False
    return True

def get_eye_center(landmarks, indices):
    """Calculate center of eye from landmarks."""
    x = sum(landmarks[i].x for i in indices) / len(indices)
    y = sum(landmarks[i].y for i in indices) / len(indices)
    return (x, y)

def get_pupil_position(landmarks, left_indices, right_indices):
    """Estimate pupil position from eye corners."""
    left_corner = (landmarks.part(left_indices[0]).x, landmarks.part(left_indices[0]).y)
    right_corner = (landmarks.part(right_indices[3]).x, landmarks.part(right_indices[3]).y)
    
    # Calculate eye center
    eye_center_x = (left_corner[0] + right_corner[0]) / 2
    eye_center_y = (left_corner[1] + right_corner[1]) / 2
    
    # Calculate inner eye points for better pupil estimation
    inner_x = (landmarks.part(left_indices[3]).x + landmarks.part(right_indices[0]).x) / 2
    inner_y = (landmarks.part(left_indices[3]).y + landmarks.part(right_indices[0]).y) / 2
    
    # Pupil is typically slightly toward the inner corner
    pupil_x = eye_center_x * 0.7 + inner_x * 0.3
    pupil_y = eye_center_y * 0.7 + inner_y * 0.3
    
    return (pupil_x, pupil_y)

def calculate_gaze_ratio(pupil, left_corner, right_corner, top_corner, bottom_corner):
    """Calculate normalized gaze ratio (0.0-1.0) for both axes."""
    # Horizontal ratio: 0 = looking left, 0.5 = center, 1 = looking right
    eye_width = right_corner[0] - left_corner[0]
    if eye_width > 0:
        horiz_ratio = (pupil[0] - left_corner[0]) / eye_width
        # Clamp and normalize to 0-1 range (typical range is 0.2-0.8)
        horiz_ratio = (horiz_ratio - 0.2) / 0.6
        horiz_ratio = max(0.0, min(1.0, horiz_ratio))
    else:
        horiz_ratio = 0.5
    
    # Vertical ratio: 0 = looking up, 0.5 = center, 1 = looking down  
    eye_height = bottom_corner[1] - top_corner[1]
    if eye_height > 0:
        vert_ratio = (pupil[1] - top_corner[1]) / eye_height
        # Clamp and normalize
        vert_ratio = (vert_ratio - 0.2) / 0.6
        vert_ratio = max(0.0, min(1.0, vert_ratio))
    else:
        vert_ratio = 0.5
    
    return horiz_ratio, vert_ratio

def generate_heatmap(page_name, width=320, height=200):
    """Generate heatmap image from gaze history."""
    if page_name not in gaze_history or len(gaze_history[page_name]) == 0:
        return None
    
    # Create blank heatmap
    heatmap = np.zeros((height, width), dtype=np.float32)
    
    # Add gaze points with Gaussian spread
    for x_norm, y_norm in gaze_history[page_name]:
        x = int(x_norm * width)
        y = int(y_norm * height)
        
        # Add Gaussian blob at gaze point
        for dy in range(-10, 11):
            for dx in range(-10, 11):
                py, px = y + dy, x + dx
                if 0 <= py < height and 0 <= px < width:
                    dist_sq = dx*dx + dy*dy
                    weight = math.exp(-dist_sq / 50.0)  # Gaussian kernel
                    heatmap[py, px] += weight
    
    # Normalize to 0-255
    if heatmap.max() > 0:
        heatmap = (heatmap / heatmap.max() * 255).astype(np.uint8)
    
    # Apply colormap
    heatmap_color = cv2.applyColorMap(heatmap, cv2.COLORMAP_JET)
    return heatmap_color

def save_heatmap(page_name):
    """Save the heatmap for a page to a file."""
    if page_name not in gaze_history or len(gaze_history[page_name]) == 0:
        return
    
    print(f"[*] Generating heatmap for {page_name}...")
    heatmap = generate_heatmap(page_name, 1920, 1080) # High res for saving
    if heatmap is not None:
        filename = f"heatmap_{page_name}_{datetime.now().strftime('%Y%m%d_%H%M%S')}.png"
        cv2.imwrite(filename, heatmap)
        print(f"[+] Heatmap saved: {filename}")

def main():
    global local_heatmap_visible, current_page
    
    # Download predictor if needed
    if not download_predictor():
        print("[!] Cannot start without predictor file.")
        return
    
    # Start TCP server
    tcp_thread = threading.Thread(target=start_tcp, daemon=True)
    tcp_thread.start()
    
    # Initialize MediaPipe
    print("[*] Initializing MediaPipe FaceLandmarker...")
    BaseOptions = core.base_options.BaseOptions
    FaceLandmarker = vision.FaceLandmarker
    FaceLandmarkerOptions = vision.FaceLandmarkerOptions
    
    options = FaceLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=MODEL_PATH),
        running_mode=vision.RunningMode.IMAGE,
        num_faces=1
    )
    landmarker = FaceLandmarker.create_from_options(options)
    
    # Initialize camera
    cap = cv2.VideoCapture(CAMERA_INDEX)
    if not cap.isOpened():
        print(f"[!] ERROR: Could not open camera with index {CAMERA_INDEX}")
        return
    
    print(f"[*] Camera {CAMERA_INDEX} opened successfully.")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    
    # Tracking state
    last_gaze_x, last_gaze_y = 0.5, 0.5
    gaze_ema_alpha = 0.3  # Exponential moving average factor
    last_blink_state = False
    blink_counter = 0
    
    # Smoothing buffer for gaze positions
    gaze_buffer = deque(maxlen=10)
    
    print("[*] Gaze tracking started. Press 'q' to quit, 'h' for heatmap, 'c' to clear.")
    
    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            print("[!] ERROR: Failed to grab frame")
            break
            
        # Check for black frame
        if np.mean(frame) < 1.0:
            cv2.putText(frame, "BLACK SCREEN DETECTED - Check Camo Connection", (10, 240), 
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)
        
        # Mirror frame for natural interaction
        frame = cv2.flip(frame, 1)
        h, w, _ = frame.shape
        
        # Convert to RGB for MediaPipe
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
        
        # Detect landmarks
        results = landmarker.detect(mp_image)
        
        tracking_status = "lost"
        gaze_x, gaze_y = last_gaze_x, last_gaze_y
        
        if not results.face_landmarks:
            cv2.putText(frame, "NO FACE DETECTED", (50, 50), cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 0, 255), 2)
            broadcast("GAZE_STATUS:lost")
        else:
            broadcast("GAZE_STATUS:tracking")
            tracking_status = "tracking"
            landmarks = results.face_landmarks[0]
            
            # Get eye centers (normalized)
            left_eye_center = get_eye_center(landmarks, LEFT_EYE_INDICES)
            right_eye_center = get_eye_center(landmarks, RIGHT_EYE_INDICES)
            
            # Get eye corners for ratio calculation
            left_eye_left = (landmarks[33].x, landmarks[33].y)
            left_eye_right = (landmarks[133].x, landmarks[133].y)
            
            right_eye_left = (landmarks[362].x, landmarks[362].y)
            right_eye_right = (landmarks[263].x, landmarks[263].y)
            
            # MediaPipe has dedicated iris landmarks
            left_pupil = (landmarks[468].x, landmarks[468].y)
            right_pupil = (landmarks[473].x, landmarks[473].y)
            
            # Simple head pose estimation using nose and face bounds
            nose_tip = (landmarks[1].x, landmarks[1].y)
            chin = (landmarks[152].x, landmarks[152].y)
            
            # Head turn ratio
            head_turn = (landmarks[1].x - 0.5) * 2.0
            head_turn = max(-1, min(1, head_turn))
            
            # Calculate eye aspect ratio for blink detection
            def eye_aspect_ratio(indices):
                # Vertical distances
                v1 = math.dist((landmarks[indices[1]].x, landmarks[indices[1]].y),
                              (landmarks[indices[5]].x, landmarks[indices[5]].y))
                v2 = math.dist((landmarks[indices[2]].x, landmarks[indices[2]].y),
                              (landmarks[indices[4]].x, landmarks[indices[4]].y))
                # Horizontal distance
                h = math.dist((landmarks[indices[0]].x, landmarks[indices[0]].y),
                             (landmarks[indices[3]].x, landmarks[indices[3]].y))
                return (v1 + v2) / (2.0 * h) if h > 0 else 0
            
            left_ear = eye_aspect_ratio(LEFT_EYE_INDICES)
            right_ear = eye_aspect_ratio(RIGHT_EYE_INDICES)
            avg_ear = (left_ear + right_ear) / 2.0
            
            is_blinking = avg_ear < 0.2
            if is_blinking != last_blink_state:
                blink_counter += 1
                last_blink_state = is_blinking
                broadcast(f"BLINK:{1 if is_blinking else 0}")
            
            # Calculate gaze based on pupil position relative to eye center
            # Combine both eyes for more stable tracking
            left_gaze_x = (left_pupil[0] - left_eye_left[0]) / (left_eye_right[0] - left_eye_left[0] + 1e-6)
            right_gaze_x = (right_pupil[0] - right_eye_left[0]) / (right_eye_right[0] - right_eye_left[0] + 1e-6)
            avg_gaze_x = (left_gaze_x + right_gaze_x) / 2
            
            # Compensate for head position and turn
            # head_turn is -1.0 to 1.0 (relative to image center)
            # raw_gaze_x should be 0.5 when looking straight at the monitor center
            # Compensate for head position and turn
            raw_gaze_x = avg_gaze_x + head_turn * 0.4
            
            # Vertical gaze (normalized)
            left_gaze_y = (left_pupil[1] - min(landmarks[159].y, landmarks[158].y)) / \
                         (max(landmarks[153].y, landmarks[144].y) - min(landmarks[159].y, landmarks[158].y) + 1e-6)
            right_gaze_y = (right_pupil[1] - min(landmarks[386].y, landmarks[385].y)) / \
                          (max(landmarks[373].y, landmarks[380].y) - min(landmarks[386].y, landmarks[385].y) + 1e-6)
            raw_gaze_y = (left_gaze_y + right_gaze_y) / 2

            # Scale the gaze to reach screen edges
            raw_gaze_x = (raw_gaze_x - 0.5) * GAZE_SENSITIVITY_X + 0.5
            
            if REVERSE_VERTICAL:
                raw_gaze_y = 1.0 - raw_gaze_y
            raw_gaze_y = (raw_gaze_y - 0.5) * GAZE_SENSITIVITY_Y + 0.5 + GAZE_VERTICAL_OFFSET
            
            # No inversion needed as frame is already flipped for mirror effect
            raw_gaze_x = raw_gaze_x
            
            # Apply EMA smoothing
            gaze_x = gaze_ema_alpha * raw_gaze_x + (1 - gaze_ema_alpha) * last_gaze_x
            gaze_y = gaze_ema_alpha * raw_gaze_y + (1 - gaze_ema_alpha) * last_gaze_y
            
            last_gaze_x, last_gaze_y = gaze_x, gaze_y
            
            # Add to buffer for additional smoothing
            gaze_buffer.append((gaze_x, gaze_y))
            if len(gaze_buffer) > 0:
                avg_x = sum(p[0] for p in gaze_buffer) / len(gaze_buffer)
                avg_y = sum(p[1] for p in gaze_buffer) / len(gaze_buffer)
                gaze_x, gaze_y = avg_x, avg_y
            
            # Clamp to valid range
            gaze_x = max(0.0, min(1.0, gaze_x))
            gaze_y = max(0.0, min(1.0, gaze_y))
            
            # Store in history for current page
            gaze_history[current_page].append((gaze_x, gaze_y))
            
            # Broadcast gaze position
            broadcast(f"GAZE:{gaze_x:.4f},{gaze_y:.4f}")
            
            # Draw on frame
            # Draw eye landmarks
            for i in LEFT_EYE_INDICES + RIGHT_EYE_INDICES:
                x, y = int(landmarks[i].x * w), int(landmarks[i].y * h)
                cv2.circle(frame, (x, y), 2, (0, 0, 255), -1)
            
            # Draw pupil estimates
            cv2.circle(frame, (int(left_pupil[0] * w), int(left_pupil[1] * h)), 4, (255, 0, 0), -1)
            cv2.circle(frame, (int(right_pupil[0] * w), int(right_pupil[1] * h)), 4, (255, 0, 0), -1)
            
            # Draw gaze point on frame (small preview)
            gaze_screen_x = int(gaze_x * w)
            gaze_screen_y = int(gaze_y * h)
            cv2.circle(frame, (gaze_screen_x, gaze_screen_y), 10, (0, 255, 255), 2)
            
            # Draw connecting line between eyes and gaze point
            eye_mid_x = int((left_eye_center[0] + right_eye_center[0]) / 2 * w)
            eye_mid_y = int((left_eye_center[1] + right_eye_center[1]) / 2 * h)
            cv2.line(frame, (eye_mid_x, eye_mid_y), (gaze_screen_x, gaze_screen_y), (0, 255, 255), 2)
        
        # Broadcast status
        broadcast(f"GAZE_STATUS:{tracking_status}")
        
        # Draw HUD
        status_text = f"Status: {tracking_status.upper()}"
        cv2.putText(frame, status_text, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0) if tracking_status == "tracking" else (0, 0, 255), 2)
        
        gaze_text = f"Gaze: ({gaze_x:.2f}, {gaze_y:.2f})"
        cv2.putText(frame, gaze_text, (10, 60), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
        
        page_text = f"Page: {current_page}"
        cv2.putText(frame, page_text, (10, 90), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 0), 2)
        
        clients_text = f"Clients: {len(connected_clients)}"
        cv2.putText(frame, clients_text, (10, 120), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (200, 200, 200), 1)
        
        # Draw heatmap overlay if enabled
        if local_heatmap_visible:
            heatmap = generate_heatmap(current_page, w // 4, h // 4)
            if heatmap is not None:
                heatmap_resized = cv2.resize(heatmap, (w, h))
                # Blend with frame
                frame = cv2.addWeighted(frame, 0.6, heatmap_resized, 0.4, 0)
        
        # Show frame
        cv2.imshow("Gaze Tracking Server - dlib", frame)
        
        # Handle key presses
        key = cv2.waitKey(1) & 0xFF
        if key == ord('q'):
            break
        elif key == ord('h'):
            local_heatmap_visible = not local_heatmap_visible
            print(f"[*] Local heatmap display: {'ON' if local_heatmap_visible else 'OFF'}")
        elif key == ord('c'):
            if current_page in gaze_history:
                gaze_history[current_page].clear()
                print(f"[*] Cleared heatmap for {current_page}")
    
    landmarker.close()
    cap.release()
    cv2.destroyAllWindows()
    
    # Save the final page heatmap before exiting
    save_heatmap(current_page)
    print("[*] Gaze tracking server stopped.")

if __name__ == "__main__":
    main()
