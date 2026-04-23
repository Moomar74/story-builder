"""
Gesture Server - MediaPipe Holistic + DollarPy Dynamic Gesture Recognition
==========================================================================
Tracks human skeleton using MediaPipe Holistic, records dynamic hand gestures
over time, and classifies them using DollarPy ($1 Unistroke Recognizer).

Sends recognized gestures + skeleton landmarks to the C# client via TCP on port 5001.

Messages sent to client:
  GESTURE:<name>             - A gesture was recognized (e.g. "circle", "swipe_right")
  SKELETON:<json>            - Pose landmarks as JSON (for drawing in C#)
  MENU_SELECT:<index>        - Circular menu item selected via hand position

Controls:
  Press 'r' to start recording a gesture template
  Press 's' to stop recording and save template
  Press 'q' to quit
  Press SPACE to toggle live recognition mode
"""

import json
import math
import os
import pickle
import socket
import threading
import time

# Set env var before importing mediapipe if needed
os.environ['PROTOCOL_BUFFERS_PYTHON_IMPLEMENTATION'] = 'python'

import cv2
import mediapipe as mp
from dollarpy import Recognizer, Template, Point

# ── MediaPipe Tasks Setup ────────────────────────────────────────
from mediapipe.tasks.python import vision
from mediapipe.tasks.python import core

BaseOptions = core.base_options.BaseOptions
HolisticLandmarker = vision.HolisticLandmarker
HolisticLandmarkerOptions = vision.HolisticLandmarkerOptions
VisionRunningMode = vision.RunningMode

mp_drawing = vision.drawing_utils
mp_drawing_styles = vision.drawing_styles

# Helper to load landmarker
def create_holistic_landmarker():
    options = HolisticLandmarkerOptions(
        base_options=BaseOptions(model_asset_path='holistic_landmarker.task'),
        running_mode=VisionRunningMode.IMAGE,
        min_face_detection_confidence=0.5,
        min_pose_detection_confidence=0.5,
        min_hand_landmarks_confidence=0.5
    )
    return HolisticLandmarker.create_from_options(options)

# ── Camera Setup ──────────────────────────────────────────────────
CAMERA_INDEX = 1  # 0 for integrated, 1 for Camo/Virtual Camera

# ── TCP Server ───────────────────────────────────────────────────
TCP_PORT = 5001
connected_clients = []

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
    print(f"[+] Gesture client connected: {addr}")
    connected_clients.append(conn)
    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            msg = data.decode('utf-8').strip()
            print(f"[<] From client: {msg}")
    except Exception:
        pass
    finally:
        try:
            connected_clients.remove(conn)
        except ValueError:
            pass
        conn.close()
        print(f"[-] Gesture client disconnected: {addr}")

def start_tcp():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", TCP_PORT))
    server.listen(5)
    print(f"[*] Gesture TCP Server on port {TCP_PORT}")
    while True:
        conn, addr = server.accept()
        t = threading.Thread(target=handle_client, args=(conn, addr), daemon=True)
        t.start()

# ── Gesture Templates Storage ────────────────────────────────────
TEMPLATES_FILE = "gesture_templates.pkl"

def load_templates():
    """Load saved gesture templates from disk."""
    if os.path.exists(TEMPLATES_FILE):
        with open(TEMPLATES_FILE, 'rb') as f:
            return pickle.load(f)
    return []

def save_templates(templates):
    """Save gesture templates to disk."""
    with open(TEMPLATES_FILE, 'wb') as f:
        pickle.dump(templates, f)

# ── Pre-built gesture templates ──────────────────────────────────
def generate_circle_points(n=64, stroke_id=1):
    """Generate points for a circle gesture."""
    pts = []
    for i in range(n):
        angle = 2 * math.pi * i / n
        x = 0.5 + 0.3 * math.cos(angle)
        y = 0.5 + 0.3 * math.sin(angle)
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_swipe_right_points(n=32, stroke_id=1):
    pts = []
    for i in range(n):
        x = 0.2 + 0.6 * (i / n)
        y = 0.5 + 0.02 * math.sin(i * 0.3)  # slight wobble
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_swipe_left_points(n=32, stroke_id=1):
    pts = []
    for i in range(n):
        x = 0.8 - 0.6 * (i / n)
        y = 0.5 + 0.02 * math.sin(i * 0.3)
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_swipe_up_points(n=32, stroke_id=1):
    pts = []
    for i in range(n):
        x = 0.5 + 0.02 * math.sin(i * 0.3)
        y = 0.8 - 0.6 * (i / n)
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_swipe_down_points(n=32, stroke_id=1):
    pts = []
    for i in range(n):
        x = 0.5 + 0.02 * math.sin(i * 0.3)
        y = 0.2 + 0.6 * (i / n)
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_wave_points(n=64, stroke_id=1):
    """Wave gesture (hand waving side to side)."""
    pts = []
    for i in range(n):
        x = 0.3 + 0.4 * abs(math.sin(3 * math.pi * i / n))
        y = 0.4 + 0.1 * math.sin(6 * math.pi * i / n)
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_push_points(n=32, stroke_id=1):
    """Push forward gesture (hand moves forward = scale up)."""
    pts = []
    for i in range(n):
        t = i / n
        x = 0.5 + 0.1 * math.sin(t * math.pi)
        y = 0.6 - 0.3 * t
        pts.append(Point(x, y, stroke_id))
    return pts

def generate_grab_points(n=48, stroke_id=1):
    """Grab/clench gesture path (converging spiral)."""
    pts = []
    for i in range(n):
        t = i / n
        r = 0.25 * (1 - t)
        angle = 4 * math.pi * t
        x = 0.5 + r * math.cos(angle)
        y = 0.5 + r * math.sin(angle)
        pts.append(Point(x, y, stroke_id))
    return pts

def get_default_templates():
    """Create default gesture templates for the story app."""
    templates = [
        Template("circle", generate_circle_points()),
        Template("swipe_right", generate_swipe_right_points()),
        Template("swipe_left", generate_swipe_left_points()),
        Template("swipe_up", generate_swipe_up_points()),
        Template("swipe_down", generate_swipe_down_points()),
        Template("wave", generate_wave_points()),
        Template("push", generate_push_points()),
        Template("grab", generate_grab_points()),
    ]
    return templates

# ── Circular Menu Logic ──────────────────────────────────────────
MENU_ITEMS = ["Story 1", "Story 2", "Story 3", "Story 4", "Back"]
MENU_CENTER = (0.5, 0.5)  # normalized
MENU_RADIUS = 0.15

def check_menu_selection(hand_x, hand_y):
    """Check if hand is hovering over a circular menu item."""
    dx = hand_x - MENU_CENTER[0]
    dy = hand_y - MENU_CENTER[1]
    dist = math.sqrt(dx * dx + dy * dy)

    if dist < 0.08:  # larger center circle hitbox
        return 4     # "Back"
    if dist > MENU_RADIUS + 0.12:  # expanded outer reach buffer
        return -1

    angle_deg = math.degrees(math.atan2(dy, dx))
    
    # Map directly to C# logic: startAngle = i * 90 - 90
    if -90 <= angle_deg < 0:
        return 0
    elif 0 <= angle_deg < 90:
        return 1
    elif 90 <= angle_deg <= 180:
        return 2
    else: # -180 to -90
        return 3

# ── Main Loop ────────────────────────────────────────────────────
def main():
    # Start TCP server thread
    tcp_thread = threading.Thread(target=start_tcp, daemon=True)
    tcp_thread.start()

    # Load templates
    saved = load_templates()
    default = get_default_templates()
    all_templates = default + saved
    recognizer = Recognizer(all_templates)
    print(f"[*] Loaded {len(all_templates)} gesture templates ({len(default)} default + {len(saved)} custom)")

    # State
    recording = False
    record_label = ""
    record_points = []  # right wrist trajectory
    recognizing = True
    live_points = []    # buffer of recent right wrist positions for live recognition
    LIVE_BUFFER = 50    # frames to buffer for recognition
    last_gesture = ""
    last_gesture_time = 0.0
    gesture_cooldown = 2.0  # seconds between recognition triggers
    show_menu = False
    menu_drop_counter = 0
    menu_hover = -1
    frame_count = 0
    skeleton_send_interval = 2  # send skeleton more frequently for smoother tracking

    # EMA Smoothing
    ema_rw_x = 0.5
    ema_rw_y = 0.5
    ema_alpha = 0.35  # lower = smoother
    
    cap = cv2.VideoCapture(CAMERA_INDEX)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

    print("[*] Starting MediaPipe Holistic... Press 'q' to quit.")
    print("[*] Controls: 'r'=record, 's'=save, SPACE=toggle recognition, 'm'=menu")

    landmarker = create_holistic_landmarker()
    try:

        while cap.isOpened():
            ret, frame = cap.read()
            if not ret:
                break

            frame = cv2.flip(frame, 1)
            h, w, _ = frame.shape
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            results = landmarker.detect(mp_image)
            if not results:
                continue
            rgb.flags.writeable = True
            image = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)

            # Draw landmarks
            if results.right_hand_landmarks:
                mp_drawing.draw_landmarks(image, results.right_hand_landmarks, vision.HandLandmarksConnections.HAND_CONNECTIONS)
            if results.left_hand_landmarks:
                mp_drawing.draw_landmarks(image, results.left_hand_landmarks, vision.HandLandmarksConnections.HAND_CONNECTIONS)
            if results.pose_landmarks:
                mp_drawing.draw_landmarks(image, results.pose_landmarks, vision.PoseLandmarksConnections.POSE_LANDMARKS,
                    landmark_drawing_spec=mp_drawing_styles.get_default_pose_landmarks_style())

            # ── Extract key landmarks ────────────────────────────
            rw_x, rw_y = 0.5, 0.5  # right wrist default
            skeleton_data = {}

            if results.pose_landmarks:
                lm = results.pose_landmarks
                # Key body points
                skeleton_data = {
                    "ls": [round(lm[11].x, 3), round(lm[11].y, 3)],  # left shoulder
                    "rs": [round(lm[12].x, 3), round(lm[12].y, 3)],  # right shoulder
                    "le": [round(lm[13].x, 3), round(lm[13].y, 3)],  # left elbow
                    "re": [round(lm[14].x, 3), round(lm[14].y, 3)],  # right elbow
                    "lw": [round(lm[15].x, 3), round(lm[15].y, 3)],  # left wrist
                    "rw": [round(lm[16].x, 3), round(lm[16].y, 3)],  # right wrist
                    "lh": [round(lm[23].x, 3), round(lm[23].y, 3)],  # left hip
                    "rh": [round(lm[24].x, 3), round(lm[24].y, 3)],  # right hip
                    "n":  [round(lm[0].x, 3),  round(lm[0].y, 3)],   # nose
                }
                
                # ── Choose Pointer (Index Finger preferred) ───────────
                raw_x, raw_y = lm[16].x, lm[16].y  # Fallback: Right Wrist
                
                if len(lm) > 20 and lm[20].visibility > 0.2:
                    raw_x, raw_y = lm[20].x, lm[20].y  # Better Fallback: Pose Right Index
                    
                is_right_clicking = False
                if results.right_hand_landmarks:
                    hand_lm = results.right_hand_landmarks
                    if len(hand_lm) > 20:
                        raw_x, raw_y = hand_lm[8].x, hand_lm[8].y  # Perfect: Hand Index Tip
                        
                        # Detect "Fist Click" (all 4 fingers folded down)
                        # Hand folded means tip y is strictly greater than pip y (pointing down relative to wrist)
                        fingers_closed = (
                            hand_lm[8].y > hand_lm[6].y and
                            hand_lm[12].y > hand_lm[10].y and
                            hand_lm[16].y > hand_lm[14].y and
                            hand_lm[20].y > hand_lm[18].y
                        )
                        if fingers_closed:
                            is_right_clicking = True
                            # Visible feedback for fist
                            cv2.circle(image, (int(raw_x * w), int(raw_y * h)), 20, (0, 0, 255), -1)
                
                # Apply EMA smoothing & 1.6x Sensitivity Amplification
                ema_rw_x = ema_alpha * raw_x + (1 - ema_alpha) * ema_rw_x
                ema_rw_y = ema_alpha * raw_y + (1 - ema_alpha) * ema_rw_y
                
                rw_x = (ema_rw_x - 0.5) * 1.6 + 0.5
                rw_y = (ema_rw_y - 0.5) * 1.6 + 0.5
                rw_x = max(0.0, min(1.0, rw_x))
                rw_y = max(0.0, min(1.0, rw_y))
                
                # Update skeleton with smoothed coords for C# rendering
                skeleton_data["rw"] = [round(rw_x, 3), round(rw_y, 3)]

                # Send skeleton periodically
                frame_count += 1
                if frame_count % skeleton_send_interval == 0:
                    # Format: "key:[x y]" pairs separated by commas.
                    # C# parser splits on comma then on colon, then on space for x/y.
                    # Arrays use space-separated values (NOT comma) so comma-split works.
                    parts = ",".join(
                        f'{k}:[{v[0]} {v[1]}]' for k, v in skeleton_data.items()
                    )
                    broadcast(f"SKELETON:{{{parts}}}")

                # ── Posture Detection for "Air Click" ────────────────
                now = time.time()
                if is_right_clicking and (now - last_gesture_time > gesture_cooldown):
                    if show_menu and menu_hover != -1:
                        broadcast(f"MENU_SELECT:{menu_hover}:{MENU_ITEMS[menu_hover]}")
                        print(f"[*] Click-selected: {MENU_ITEMS[menu_hover]}")
                        show_menu = False
                        last_gesture_time = now
                        # Auto-switch to IDLE after selecting a story
                        # This prevents false swipes during story playback
                        if menu_hover < 4:  # Story selected (not "Back")
                            recognizing = False
                            live_points.clear()
                            print("[*] Auto-switched to IDLE mode (story selected)")
                    elif not show_menu:
                        broadcast("GESTURE:click")
                        print("[*] Air Click Triggered!")
                        last_gesture_time = now

                # ── Posture Detection for Menu (Left Hand Open Palm Hold) ─────
                is_left_open = False
                if results.left_hand_landmarks:
                    hlm = results.left_hand_landmarks
                    # Check if 4 fingers are straight up
                    fingers_open = (
                        hlm[8].y < hlm[6].y and hlm[8].y < hlm[5].y and
                        hlm[12].y < hlm[10].y and hlm[12].y < hlm[9].y and
                        hlm[16].y < hlm[14].y and hlm[16].y < hlm[13].y and
                        hlm[20].y < hlm[18].y and hlm[20].y < hlm[17].y
                    )
                    # Left thumb points outwards to the right side of flipped camera image
                    thumb_out = hlm[4].x > hlm[2].x
                    
                    if fingers_open and thumb_out:
                        is_left_open = True

                if is_left_open:
                    menu_drop_counter = 0
                    if not show_menu:
                        show_menu = True
                        broadcast("MENU_SHOW")
                        # Auto re-enable recognition when menu opens (no keyboard needed)
                        if not recognizing:
                            recognizing = True
                            live_points.clear()
                            print("[*] Auto-switched to RECOGNIZING mode (menu opened)")
                        print("[*] Circular menu OPENED (Left Palm Hold)")
                else:
                    if show_menu:
                        menu_drop_counter += 1
                        if menu_drop_counter > 15:  # small buffer so it doesn't flicker on tracking drops
                            show_menu = False
                            broadcast("MENU_HIDE")
                            print("[*] Circular menu CLOSED (Left Palm Dropped)")

            # ── Recording mode ───────────────────────────────────
            if recording and results.pose_landmarks:
                record_points.append(Point(rw_x, rw_y, 1))
                # Draw recording indicator
                cv2.circle(image, (30, 30), 15, (0, 0, 255), -1)
                cv2.putText(image, f"REC: {record_label} ({len(record_points)} pts)",
                            (55, 38), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)
                # Draw trajectory on screen
                for i in range(1, len(record_points)):
                    x1 = int(record_points[i-1].x * w)
                    y1 = int(record_points[i-1].y * h)
                    x2 = int(record_points[i].x * w)
                    y2 = int(record_points[i].y * h)
                    cv2.line(image, (x1, y1), (x2, y2), (0, 0, 255), 2)

            # ── Live recognition ─────────────────────────────────
            elif recognizing and results.pose_landmarks:
                live_points.append(Point(rw_x, rw_y, 1))
                if len(live_points) > LIVE_BUFFER:
                    live_points.pop(0)

                # Draw live trail
                for i in range(1, len(live_points)):
                    x1 = int(live_points[i-1].x * w)
                    y1 = int(live_points[i-1].y * h)
                    x2 = int(live_points[i].x * w)
                    y2 = int(live_points[i].y * h)
                    cv2.line(image, (x1, y1), (x2, y2), (0, 255, 0), 2)

                # Try to recognize every 12 frames
                if len(live_points) >= 25 and frame_count % 12 == 0:
                    now = time.time()
                    if now - last_gesture_time > gesture_cooldown:
                        
                        # ── VELOCITY-BASED SWIPE DETECTION (tuned for accuracy) ──
                        swipe_detected = None
                        n_pts = len(live_points)
                        dx = 0
                        dy = 0
                        if n_pts >= 25:
                            # Use first quarter vs last quarter for stable detection
                            quarter = max(5, n_pts // 4)
                            start_x = sum(p.x for p in live_points[:quarter]) / quarter
                            start_y = sum(p.y for p in live_points[:quarter]) / quarter
                            end_x = sum(p.x for p in live_points[-quarter:]) / quarter
                            end_y = sum(p.y for p in live_points[-quarter:]) / quarter
                            
                            dx = end_x - start_x
                            dy = end_y - start_y
                            
                            SWIPE_THRESHOLD = 0.14  # must move at least 14% of screen
                            
                            # Check linearity: a real swipe should be mostly in one direction
                            # The straight-line distance vs total path length ratio
                            straight_dist = math.hypot(dx, dy)
                            total_path = sum(math.hypot(live_points[i].x - live_points[i-1].x, 
                                                        live_points[i].y - live_points[i-1].y) 
                                             for i in range(1, n_pts))
                            linearity = straight_dist / max(total_path, 0.001)
                            
                            # Linearity > 0.3 means the path is reasonably straight (not random jitter)
                            if linearity > 0.3:
                                # Horizontal swipe: dx is dominant and large enough
                                if abs(dx) > SWIPE_THRESHOLD and abs(dx) > abs(dy) * 2.0:
                                    if dx > 0:
                                        swipe_detected = "swipe_right"
                                    else:
                                        swipe_detected = "swipe_left"
                                # Vertical swipe: dy is dominant and large enough
                                elif abs(dy) > SWIPE_THRESHOLD and abs(dy) > abs(dx) * 2.0:
                                    if dy > 0:
                                        swipe_detected = "swipe_down"
                                    else:
                                        swipe_detected = "swipe_up"
                        
                        if swipe_detected:
                            print(f"[G] Swipe Detected: {swipe_detected} (dx={dx:.3f}, dy={dy:.3f}, lin={linearity:.2f})")
                            if not show_menu:
                                broadcast(f"GESTURE:{swipe_detected}")
                            last_gesture = swipe_detected
                            last_gesture_time = now
                            live_points.clear()
                        else:
                            # ── DOLLARPY for complex gestures (circle, wave, etc.) ──
                            path_len = sum(math.hypot(live_points[i].x - live_points[i-1].x, 
                                                      live_points[i].y - live_points[i-1].y) 
                                           for i in range(1, len(live_points)))
                            if path_len >= 0.15:
                                try:
                                    result = recognizer.recognize(live_points)
                                    if result and result[1] is not None and result[1] > 0.45:
                                        gesture_name = result[0]
                                        confidence = result[1]
                                        # Skip if DollarPy says swipe but velocity didn't detect it
                                        if "swipe" not in gesture_name:
                                            print(f"[G] Recognized: {gesture_name} (conf={confidence:.2f})")
                                            if not show_menu:
                                                broadcast(f"GESTURE:{gesture_name}")
                                            last_gesture = gesture_name
                                            last_gesture_time = now
                                            live_points.clear()
                                except Exception:
                                    pass

            # ── Circular Menu ────────────────────────────────────
            if show_menu:
                cx, cy = int(MENU_CENTER[0] * w), int(MENU_CENTER[1] * h)
                r = int(MENU_RADIUS * w) + 20
                menu_hover = check_menu_selection(rw_x, rw_y)

                # Draw slices using overlay for transparency
                overlay = image.copy()
                for i in range(4):
                    is_hover = (i == menu_hover)
                    start_angle = i * 90 - 90
                    end_angle = start_angle + 90
                    
                    if is_hover:
                        color = (0, 255, 255) # Yellowish select
                        a_r = r + 10
                    else:
                        color = (80, 50, 50) # Dark base
                        a_r = r
                        
                    cv2.ellipse(overlay, (cx, cy), (a_r, a_r), 0, start_angle, end_angle, color, -1)
                    
                    # Text
                    txt_angle = math.radians(start_angle + 45)
                    tx = int(cx + (a_r * 0.6) * math.cos(txt_angle))
                    ty = int(cy + (a_r * 0.6) * math.sin(txt_angle))
                    text_color = (0,0,0) if is_hover else (255,255,255)
                    cv2.putText(overlay, f"Story {i + 1}", (tx - 30, ty + 5), 
                                cv2.FONT_HERSHEY_SIMPLEX, 0.5, text_color, 2)

                cv2.addWeighted(overlay, 0.7, image, 0.3, 0, image)

                # Redraw solid outlines
                for i in range(4):
                    is_hover = (i == menu_hover)
                    a_r = r + 10 if is_hover else r
                    start_angle = i * 90 - 90
                    end_angle = start_angle + 90
                    cv2.ellipse(image, (cx, cy), (a_r, a_r), 0, start_angle, end_angle, (255, 255, 255), 2)

                # Center Back button
                is_back_hover = (menu_hover == 4)
                back_r = 45 if is_back_hover else 35
                back_color = (0, 255, 255) if is_back_hover else (30, 30, 30)
                cv2.circle(image, (cx, cy), back_r, back_color, -1)
                cv2.circle(image, (cx, cy), back_r, (255, 255, 255), 2)
                cv2.putText(image, "Back", (cx - 20, cy + 5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, 
                            (0,0,0) if is_back_hover else (255,255,255), 2)

                # Send menu hover
                if menu_hover >= 0:
                    broadcast(f"MENU_HOVER:{menu_hover}:{MENU_ITEMS[menu_hover]}")

                # Draw hand cursor
                hx = int(rw_x * w)
                hy = int(rw_y * h)
                cv2.circle(image, (hx, hy), 10, (0, 255, 0), -1)
                cv2.circle(image, (hx, hy), 12, (255, 255, 255), 2)

            # ── HUD ──────────────────────────────────────────────
            status = "RECOGNIZING" if recognizing else "IDLE"
            if recording:
                status = "RECORDING"
            cv2.putText(image, f"Mode: {status}", (10, h - 60),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 0), 2)

            if last_gesture:
                elapsed = time.time() - last_gesture_time
                if elapsed < 3.0:
                    cv2.putText(image, f"Gesture: {last_gesture}",
                                (10, h - 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8,
                                (0, 255, 0), 2)

            clients_str = f"Clients: {len(connected_clients)}"
            cv2.putText(image, clients_str, (w - 150, 30),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 200), 1)

            cv2.imshow("Gesture Tracker - MediaPipe + DollarPy", image)

            # ── Keyboard Input ───────────────────────────────────
            key = cv2.waitKey(5) & 0xFF

            if key == ord('q'):
                break

            elif key == ord('r'):
                if not recording:
                    record_label = input("[?] Enter gesture name: ").strip()
                    if record_label:
                        recording = True
                        record_points = []
                        print(f"[*] Recording gesture: '{record_label}'. Press 's' to save.")

            elif key == ord('s'):
                if recording and len(record_points) > 10:
                    new_template = Template(record_label, record_points)
                    all_templates.append(new_template)
                    saved.append(new_template)
                    save_templates(saved)
                    recognizer = Recognizer(all_templates)
                    print(f"[+] Saved template '{record_label}' ({len(record_points)} points). Total: {len(all_templates)}")
                    recording = False
                    record_points = []
                elif recording:
                    print("[!] Not enough points. Keep recording.")

            elif key == ord(' '):
                recognizing = not recognizing
                live_points.clear()
                print(f"[*] Recognition: {'ON' if recognizing else 'OFF'}")

            elif key == ord('m'):
                show_menu = not show_menu
                if show_menu:
                    broadcast("MENU_SHOW")
                    print("[*] Circular menu ON")
                else:
                    broadcast("MENU_HIDE")
                    print("[*] Circular menu OFF")

            elif key == 13:  # Enter - confirm menu selection
                if show_menu and menu_hover >= 0:
                    broadcast(f"MENU_SELECT:{menu_hover}:{MENU_ITEMS[menu_hover]}")
                    print(f"[*] Menu selected: {MENU_ITEMS[menu_hover]}")

    finally:
        landmarker.close()
    
    cap.release()
    cv2.destroyAllWindows()
    print("[*] Gesture server stopped.")

if __name__ == "__main__":
    main()
