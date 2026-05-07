import cv2
import numpy as np
import threading
import time
import socket
from db_manager import db
from face_identifier import FaceRecognitionEngine
import mediapipe as mp
from mediapipe.tasks.python import vision
from mediapipe.tasks.python import core
import os

# --- Configuration ---
CAMERA_INDEX = 0
GAZE_PORT = 5002
FACE_PORT = 5003
MODEL_PATH = "face_landmarker.task"

# --- Shared Vision State ---
class VisionState:
    def __init__(self):
        self.frame = None
        self.display_frame = None
        self.running = True
        self.lock = threading.Lock()
        
        # Results
        self.current_user_id = None
        self.current_user_name = "Unknown"
        self.current_session_id = None
        self.last_face_broadcast_time = 0
        self.current_page = "SignIn"
        self.last_page = "SignIn"  # Track page changes for adaptive menu
        self.current_face_encoding = None
        self.gaze_coords = (0.5, 0.5)
        self.gaze_status = "lost"
        self.face_missing_since = time.time()
        
        # Batch logging
        self.gaze_batch = []
        self.batch_lock = threading.Lock()
        
        # Reactive Menu Tracking
        self.last_instant_quadrant = None
        self.quadrant_stable_since = time.time()
        
        # Adaptive menu cache (per page)
        self.adaptive_menu_cache = {}  # page_name -> {menu_x, menu_y, quadrant, confidence}

# --- TCP Broadcaster Class ---
class Broadcaster:
    def __init__(self, port, name, state):
        self.port = port
        self.name = name
        self.state = state
        self.clients = []
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            server.bind(("0.0.0.0", self.port))
            server.listen(5)
            print(f"[*] {self.name} TCP Server on port {self.port}")
        except Exception as e:
            print(f"[!] {self.name} Server Error: {e}")
            return
            
        while True:
            conn, addr = server.accept()
            print(f"[+] {self.name} client connected: {addr}")
            self.clients.append(conn)
            
            # Handle incoming commands from client (C# app)
            def handle_commands(client_conn=conn):
                try:
                    while True:
                        data = client_conn.recv(1024)
                        if not data: break
                        raw = data.decode('utf-8')
                        # Handle multiple messages in one packet
                        for msg in raw.strip().split('\n'):
                            msg = msg.strip()
                            if not msg: continue
                            
                            # 1. Registration Command
                            if msg.startswith("REGISTER:"):
                                name = msg.split(":", 1)[1]
                                global face_engine_ref
                                if face_engine_ref:
                                    face_engine_ref.start_registration(name)
                                    print(f"[*] Remote Registration Started for: {name}")
                            
                            # 2. Page Change Command (Crucial for Heatmaps)
                            elif msg.startswith("SET_PAGE:") or msg.startswith("PAGE:"):
                                page = msg.split(":", 1)[1]
                                old_page = self.state.current_page
                                self.state.current_page = page
                                print(f"[*] Page changed: {old_page} -> {page}")
                                # Auto-send adaptive menu position for new page
                                self._send_menu_pos_for_page(page, client_conn)
                            
                            # 3. Request Adaptive Menu Position
                            elif msg.startswith("GET_MENU_POS:"):
                                page = msg.split(":", 1)[1]
                                self._send_menu_pos_for_page(page, client_conn)
                except: pass
            threading.Thread(target=handle_commands, daemon=True).start()

    def _send_menu_pos_for_page(self, page_name, conn=None):
        """Query DB for adaptive menu config and send to the specific client (or all if conn=None)."""
        user_id = self.state.current_user_id
        if not user_id:
            reply = f"MENU_POS:0.5,0.5,center,0.0\n"
            if conn: conn.sendall(reply.encode('utf-8'))
            else: self.send(reply)
            return
        
        # Check cache first
        cache_key = f"{user_id}:{page_name}"
        config = self.state.adaptive_menu_cache.get(cache_key)
        
        if not config:
            # Query from database
            config = db.get_adaptive_menu_config(user_id, page_name)
        
        if config:
            reply = (f"MENU_POS:{config['menu_x']},{config['menu_y']},"
                     f"{config['menu_quadrant']},{config['confidence']}\n")
            # Update cache
            self.state.adaptive_menu_cache[cache_key] = config
        else:
            reply = f"MENU_POS:0.5,0.5,center,0.0\n"
        
        if conn:
            try: conn.sendall(reply.encode('utf-8'))
            except: pass
        else:
            self.send(reply)
            
        print(f"[*] Sent menu position for {page_name} (User: {user_id[:8]}): {reply.strip()}")

    def send(self, message):
        if not message.endswith('\n'): message += '\n'
        msg_bytes = message.encode('utf-8')
        for conn in list(self.clients):
            try:
                conn.sendall(msg_bytes)
            except:
                if conn in self.clients: self.clients.remove(conn)

# --- Database Logging Thread ---
def db_logger_worker(state):
    print("[*] DB Logger Thread Started")
    while state.running:
        time.sleep(2.0) # Log every 2 seconds
        
        with state.batch_lock:
            if state.gaze_batch and state.current_session_id:
                batch_to_log = list(state.gaze_batch)
                state.gaze_batch = []
                
                # Push to Supabase
                db.log_gaze_batch(state.current_session_id, state.current_page, batch_to_log)
                print(f"[+] Logged {len(batch_to_log)} points to {state.current_page} (User: {state.current_user_name})")

# --- Heatmap Aggregation & Adaptive Menu Worker ---
def heatmap_aggregation_worker(state, gaze_broadcaster):
    """Background thread: every 30 seconds, aggregate gaze data into
    per-user heatmaps and recompute adaptive menu positions.
    """
    print("[*] Heatmap Aggregation Worker Started (10s interval)")
    PAGES = ["SignIn", "SignUp", "StorySelection", "StoryPlayer", "StoryBuilder"]
    
    while state.running:
        time.sleep(10.0)
        
        user_id = state.current_user_id
        if not user_id:
            continue
        
        current_page = state.current_page
        print(f"\n[*] Running heatmap aggregation for {state.current_user_name}...")
        
        try:
            # Compute heatmap and menu position for the CURRENT page
            # (the page the user is actively on gets priority)
            config = db.compute_adaptive_menu_position(user_id, current_page)
            if config:
                cache_key = f"{user_id}:{current_page}"
                state.adaptive_menu_cache[cache_key] = config
                # Broadcast the updated menu position to all gaze clients
                msg = (f"MENU_POS:{config['menu_x']},{config['menu_y']},"
                       f"{config['menu_quadrant']},{config['confidence']}")
                gaze_broadcaster.send(msg)
                print(f"[+] Aggregation complete: {current_page} -> "
                      f"hotspot confidence={config['confidence']:.2f}")
            
            # Also compute for other pages in the background (lower priority)
            for page in PAGES:
                if page != current_page:
                    other_config = db.compute_adaptive_menu_position(user_id, page)
                    if other_config:
                        other_key = f"{user_id}:{page}"
                        state.adaptive_menu_cache[other_key] = other_config
        except Exception as e:
            print(f"[!] Heatmap aggregation error: {e}")

# --- Face ID Worker Thread ---
def face_id_worker(state, engine, broadcaster):
    print("[*] Face ID Thread Started")
    while state.running:
        if state.frame is not None:
            with state.lock:
                local_frame = state.frame.copy()
                encoding = state.current_face_encoding
            
            # Process face
            processed = engine.process_frame(local_frame, external_encoding=encoding)
            
            # Check for identified users with Smoothing (Logic moved below)
            # The identification reset logic is now handled within the smoothing block.

            # Check for identified users with Smoothing
            found_id = None
            found_name = None
            for tid in engine.tracked_faces:
                tracked = engine.tracked_faces[tid]
                if tracked.is_recognized and tracked.identity:
                    found_id = tracked.identity.id
                    found_name = tracked.identity.name
            
            if found_id:
                state.face_missing_since = time.time() # Reset timeout
                
                # Identification Smoothing (Hysteresis)
                if not hasattr(state, 'id_counter'): 
                    state.id_counter = 0
                    state.last_candidate_id = None
                
                if found_id == state.last_candidate_id:
                    state.id_counter += 1
                else:
                    state.id_counter = 0
                    state.last_candidate_id = found_id
                
                # Only switch if we've seen this user for 5 frames (~0.2 sec) for speed
                if state.id_counter > 5 and state.current_user_id != found_id:
                    state.current_user_id = found_id
                    state.current_user_name = found_name
                    state.current_session_id = db.create_session(state.current_user_id)
                    print(f"\n[LOGIN] Verified: {state.current_user_name} (Session Started)")
                    state.id_counter = 0

                # Persistent Broadcast: Send identity every 2 seconds while face is visible
                # This ensures the C# app logs in as soon as it hits the SignIn screen
                if state.current_user_id and time.time() - state.last_face_broadcast_time > 2.0:
                    confidence = 0.95
                    broadcaster.send(f"FACE_DETECTED:{state.current_user_id}:{state.current_user_name}:{confidence:.2f}")
                    state.last_face_broadcast_time = time.time()
                    
                    # Also send adaptive menu position
                    global gaze_broadcaster_ref
                    if gaze_broadcaster_ref:
                        gaze_broadcaster_ref._send_menu_pos_for_page(state.current_page, None)
            
            else:
                # No face found - potentially reset session after timeout
                if not hasattr(state, 'face_missing_since'):
                    state.face_missing_since = time.time()
                
                if state.current_user_id is not None and (time.time() - state.face_missing_since > 3.0):
                    print(f"[-] Face left: {state.current_user_name}")
                    state.current_user_id = None
                    state.current_user_name = "Unknown"
            
            with state.lock:
                state.display_frame = processed
        else:
            time.sleep(0.1)

# --- Gaze Tracking Worker Thread ---
def gaze_worker(state, broadcaster):
    print("[*] Gaze Thread Started")
    try:
        BaseOptions = core.base_options.BaseOptions
        FaceLandmarker = vision.FaceLandmarker
        FaceLandmarkerOptions = vision.FaceLandmarkerOptions
        
        options = FaceLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=MODEL_PATH),
            running_mode=vision.RunningMode.IMAGE,
            num_faces=1
        )
        landmarker = FaceLandmarker.create_from_options(options)
        print("[+] MediaPipe Landmarker Initialized")
    except Exception as e:
        print(f"[!] Gaze Error (Init): {e}")
        return
    
    print("[*] Gaze Loop Started")
    while state.running:
        if state.frame is not None:
            with state.lock: local_frame = state.frame.copy()
            
            rgb = cv2.cvtColor(local_frame, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            results = landmarker.detect(mp_image)
            
            if results.face_landmarks:
                if not hasattr(state, '_gaze_log_tick'): state._gaze_log_tick = 0
                state._gaze_log_tick += 1
                
                landmarks = results.face_landmarks[0]
                # Mirror-corrected head turn ( Nose tip is landmark 1)
                head_turn = (0.5 - landmarks[1].x) * 2.0
                lp, rp = landmarks[468], landmarks[473]
                
                raw_x = (lp.x + rp.x) / 2
                raw_y = (lp.y + rp.y) / 2
                
                # Mirror-corrected Gaze Mapping:
                # 1. Flip X: Since the image is already mirrored, looking RIGHT means eyes move LEFT in the image.
                # 2. Flip Y: MediaPipe Y increases downwards, but screen Y also increases downwards (usually).
                
                # Correct X: (0.5 - raw_x) * sensitivity + 0.5
                gx = max(0.0, min(1.0, (0.5 - raw_x) * 3.0 + 0.5 + head_turn * 0.4))
                # Correct Y: (raw_y - 0.5) * sensitivity + 0.5
                gy = max(0.0, min(1.0, (raw_y - 0.5) * 4.0 + 0.5))
                
                if state._gaze_log_tick % 50 == 0:
                    print(f"[*] Gaze: Raw({raw_x:.2f},{raw_y:.2f}) -> Screen({gx:.2f},{gy:.2f}) | Head: {head_turn:.2f}")

                state.gaze_coords = (gx, gy)
                state.gaze_status = "tracking"
                
                # Add to batch for database
                with state.batch_lock:
                    state.gaze_batch.append((gx, gy))
                
                # Sync landmarks to face engine
                with state.lock:
                    if face_engine_ref:
                        state.current_face_encoding = face_engine_ref.generate_geometry_signature(landmarks)
                
                broadcaster.send(f"GAZE:{gx:.4f},{gy:.4f}")
                
                # --- INSTANT REACTIVE MENU PLACEMENT ---
                # Determine current gaze quadrant
                gaze_h = "left" if gx <= 0.5 else "right"
                gaze_v = "top" if gy <= 0.5 else "bottom"
                current_quad = f"{gaze_v}-{gaze_h}"
                
                if current_quad != state.last_instant_quadrant:
                    state.last_instant_quadrant = current_quad
                    state.quadrant_stable_since = time.time()
                elif time.time() - state.quadrant_stable_since > 0.5: # 0.5s stability
                    # Logic: Move menu to the OPPOSITE corner
                    menu_h = "right" if gaze_h == "left" else "left"
                    menu_v = "bottom" if gaze_v == "top" else "top"
                    mx = 0.1 if menu_h == "left" else 0.9
                    my = 0.1 if menu_v == "top" else 0.9
                    
                    # Only send if it's different from the last sent menu pos
                    menu_msg = f"MENU_POS:{mx:.1f},{my:.1f},{menu_v}-{menu_h},1.0"
                    if not hasattr(state, 'last_sent_menu_msg') or state.last_sent_menu_msg != menu_msg:
                        broadcaster.send(menu_msg)
                        state.last_sent_menu_msg = menu_msg
                        # Reset timer to avoid spamming
                        state.quadrant_stable_since = time.time() + 1000 # Lock until next change

                broadcaster.send("GAZE_STATUS:tracking")
            else:
                state.gaze_status = "lost"
                broadcaster.send("GAZE_STATUS:lost")
            time.sleep(0.01)
        else:
            time.sleep(0.1)

# --- Main Thread ---
face_engine_ref = None
gaze_broadcaster_ref = None

def main():
    global face_engine_ref, gaze_broadcaster_ref
    state = VisionState()
    
    # Initialize Engines
    face_engine = FaceRecognitionEngine()
    face_engine_ref = face_engine
    
    # Networking
    g_broadcaster = Broadcaster(GAZE_PORT, "Gaze", state)
    gaze_broadcaster_ref = g_broadcaster
    f_broadcaster = Broadcaster(FACE_PORT, "FaceID", state)
    
    # Start All Threads
    threads = [
        threading.Thread(target=face_id_worker, args=(state, face_engine, f_broadcaster), daemon=True),
        threading.Thread(target=gaze_worker, args=(state, g_broadcaster), daemon=True),
        threading.Thread(target=db_logger_worker, args=(state,), daemon=True),
        threading.Thread(target=heatmap_aggregation_worker, args=(state, g_broadcaster), daemon=True),
    ]
    for t in threads: t.start()
    
    cap = cv2.VideoCapture(CAMERA_INDEX)
    if not cap.isOpened():
        print(f"[!] ERROR: Could not open camera {CAMERA_INDEX}")
    else:
        print(f"[+] Camera {CAMERA_INDEX} opened successfully")
    
    print("\n[SUCCESS] Unified Vision Server with DB Logging Running!")
    
    try:
        while cap.isOpened():
            ret, frame = cap.read()
            if not ret: break
            frame = cv2.flip(frame, 1)
            h, w = frame.shape[:2]
            
            with state.lock:
                state.frame = frame
                display = state.display_frame.copy() if state.display_frame is not None else frame.copy()
                gx, gy = state.gaze_coords
            
            if state.gaze_status == "tracking":
                cv2.circle(display, (int(gx*w), int(gy*h)), 10, (0, 255, 255), -1)
                cv2.putText(display, f"User: {state.current_user_name} | Page: {state.current_page}", (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
            
            # cv2.imshow("Unified Vision & Analytics Server", display)
            # if cv2.waitKey(1) & 0xFF == ord('q'): break
            time.sleep(0.01) # Small delay to prevent CPU pegging
        print("[!] Camera loop terminated (ret was False or cap not opened)")
    finally:
        state.running = False
        cap.release()
        # cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
