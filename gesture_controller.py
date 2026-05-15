"""
Gesture Controller - MediaPipe Hands Full Game Controller
==========================================================
Connects to Frame Broadcaster (port 6000) for camera frames and
Interaction Hub (port 6001) to relay gesture commands to the C# client.

This replaces the old standalone gesture_server.py with a distributed
worker that follows the same pattern as gaze_tracking_server.py.

GESTURE MAP (All Game Screens):
────────────────────────────────────────────────────────────
  SignIn Screen:
    ✋ Open Palm (hold 2s)     → Auto-login as guest
    ✌️ Peace Sign (hold 2s)    → Navigate to Sign Up

  SignUp Screen:
    👈 Point Left              → Backspace last letter
    ✊ Fist (hold 1s)          → Confirm / Submit name
    🖐️ Open Palm               → Cancel (go back to Sign In)

  StorySelection Screen:
    👉 Swipe Right             → Next story
    👈 Swipe Left              → Previous story
    ✊ Fist Click              → Select highlighted story
    🖐️ Open Palm (left hand)   → Open circular menu

  StoryPlayer Screen:
    👉 Swipe Right             → Next scene
    👈 Swipe Left              → Previous scene
    👆 Swipe Up                → Skip dialogue
    🤟 Wave / Circle / Push    → Complete challenge
    ✊ Fist Click              → Advance dialogue / Click
    🖐️ Open Palm (left hand)   → Open circular menu

  Universal:
    ☝️ Index Finger Up         → Cursor control (moves pointer)
    ✊ Fist                    → Click / Select
    🖐️ Left Open Palm Hold     → Toggle circular menu

MESSAGES SENT TO HUB (→ C# Client):
    GESTURE:<name>             → swipe_right, swipe_left, swipe_up, swipe_down,
                                 click, wave, circle, push, grab
    SKELETON:{key:[x y],...}   → Pose/hand skeleton for C# rendering
    MENU_SHOW / MENU_HIDE      → Circular menu visibility
    MENU_HOVER:<idx>:<name>    → Hover state on menu item
    MENU_SELECT:<idx>:<name>   → Menu item selected via fist
    HAND_CURSOR:<x>,<y>        → Smoothed hand cursor position (normalized)
    HAND_STATE:<state>         → open, fist, point, peace, thumbs_up
"""

import math
import os
import socket
import struct
import threading
import time
from collections import deque

os.environ['PROTOCOL_BUFFERS_PYTHON_IMPLEMENTATION'] = 'python'

import cv2
import mediapipe as mp
import numpy as np
from mediapipe.tasks.python import vision
from mediapipe.tasks.python import core

# ── MediaPipe Tasks Setup ────────────────────────────────────
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

# ── Network Config ───────────────────────────────────────────
BROADCASTER_IP = "127.0.0.1"
BROADCASTER_PORT = 6000
HUB_IP = "127.0.0.1"
HUB_PORT = 6001

# ── Gesture Config ───────────────────────────────────────────
SWIPE_THRESHOLD = 0.14        # Minimum normalized distance for swipe
SWIPE_FRAMES = 12             # Frames to analyze for swipe detection
GESTURE_COOLDOWN = 1.5        # Seconds between gesture triggers
FIST_HOLD_MS = 600            # Milliseconds to hold fist for "click"
PALM_HOLD_MS = 1500           # Milliseconds to hold palm for login
EMA_ALPHA = 0.35              # Smoothing factor for cursor
SENSITIVITY = 1.6             # Cursor sensitivity multiplier

# ── Menu Config ──────────────────────────────────────────────
MENU_ITEMS = ["Story 1", "Story 2", "Story 3", "Story 4", "Back"]
MENU_CENTER = (0.5, 0.5)
MENU_RADIUS = 0.15

# ── State ────────────────────────────────────────────────────
hub_conn = None
current_page = "SignIn"


# ==============================================================
#  NETWORK HELPERS
# ==============================================================
def connect_to_hub():
    """Connect to the Interaction Hub on port 6001."""
    global hub_conn
    while True:
        try:
            hub_conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            hub_conn.connect((HUB_IP, HUB_PORT))
            print("[+] Gesture Controller: Connected to Hub (port 6001).")
            break
        except Exception:
            print("[.] Gesture Controller: Waiting for Hub...")
            time.sleep(2)


def hub_listener():
    """Listen for commands relayed from C# client via Hub."""
    global current_page
    while True:
        try:
            data = hub_conn.recv(1024)
            if not data:
                break
            for line in data.decode('utf-8').strip().split('\n'):
                line = line.strip()
                if line.startswith("PAGE:"):
                    current_page = line[5:]
                    print(f"[*] Page changed to: {current_page}")
        except Exception:
            break
    print("[!] Hub listener disconnected.")


def send_to_hub(msg):
    """Send a message to the Interaction Hub."""
    if hub_conn:
        try:
            hub_conn.sendall((msg + "\n").encode('utf-8'))
        except Exception:
            pass


def recv_all(sock, n):
    """Receive exactly n bytes from a socket."""
    data = b''
    while len(data) < n:
        packet = sock.recv(n - len(data))
        if not packet:
            return None
        data += packet
    return data


# ==============================================================
#  HAND GESTURE RECOGNITION
# ==============================================================
class HandState:
    """Tracks the state of a single detected hand."""
    OPEN = "open"
    FIST = "fist"
    POINT = "point"
    PEACE = "peace"
    THUMBS_UP = "thumbs_up"
    UNKNOWN = "unknown"


def classify_hand_state(lm):
    """
    Classify hand gesture from a list of landmarks (HolisticLandmarker returns a plain list).
    Returns one of: open, fist, point, peace, thumbs_up, unknown
    """
    # Finger tip and pip indices
    tips = [8, 12, 16, 20]   # index, middle, ring, pinky tips
    pips = [6, 10, 14, 18]   # index, middle, ring, pinky PIPs

    fingers_up = [lm[t].y < lm[p].y for t, p in zip(tips, pips)]
    thumb_up = abs(lm[4].x - lm[2].x) > 0.05 and lm[4].y < lm[3].y
    num_fingers = sum(fingers_up)

    if num_fingers == 0 and not thumb_up:
        return HandState.FIST
    if num_fingers >= 3 and fingers_up[0] and fingers_up[1]:
        return HandState.OPEN
    if fingers_up[0] and num_fingers == 1:
        return HandState.POINT
    if fingers_up[0] and fingers_up[1] and not fingers_up[2] and not fingers_up[3]:
        return HandState.PEACE
    if thumb_up and num_fingers == 0:
        return HandState.THUMBS_UP
    return HandState.UNKNOWN


def get_index_tip(lm):
    """Get the index finger tip position (normalized 0-1)."""
    return lm[8].x, lm[8].y


def get_wrist(lm):
    """Get the wrist position (normalized 0-1)."""
    return lm[0].x, lm[0].y


def check_menu_selection(hand_x, hand_y):
    """Check if hand cursor is hovering over a circular menu item."""
    dx = hand_x - MENU_CENTER[0]
    dy = hand_y - MENU_CENTER[1]
    dist = math.sqrt(dx * dx + dy * dy)

    if dist < 0.08:
        return 4  # "Back" (center)
    if dist > MENU_RADIUS + 0.12:
        return -1

    angle_deg = math.degrees(math.atan2(dy, dx))

    if -90 <= angle_deg < 0:
        return 0
    elif 0 <= angle_deg < 90:
        return 1
    elif 90 <= angle_deg <= 180:
        return 2
    else:
        return 3


# ==============================================================
#  SWIPE DETECTION
# ==============================================================
def detect_swipe(trail):
    """
    Detect swipe gesture from a trail of (x, y) positions.
    Returns gesture name or None.
    """
    n = len(trail)
    if n < 15:
        return None

    quarter = max(5, n // 4)
    start_x = sum(p[0] for p in trail[:quarter]) / quarter
    start_y = sum(p[1] for p in trail[:quarter]) / quarter
    end_x = sum(p[0] for p in trail[-quarter:]) / quarter
    end_y = sum(p[1] for p in trail[-quarter:]) / quarter

    dx = end_x - start_x
    dy = end_y - start_y

    straight_dist = math.hypot(dx, dy)
    total_path = sum(
        math.hypot(trail[i][0] - trail[i - 1][0], trail[i][1] - trail[i - 1][1])
        for i in range(1, n)
    )
    linearity = straight_dist / max(total_path, 0.001)

    if linearity < 0.3:
        return None

    if abs(dx) > SWIPE_THRESHOLD and abs(dx) > abs(dy) * 2.0:
        return "swipe_right" if dx > 0 else "swipe_left"
    elif abs(dy) > SWIPE_THRESHOLD and abs(dy) > abs(dx) * 2.0:
        return "swipe_down" if dy > 0 else "swipe_up"

    return None


# ==============================================================
#  BUILD SKELETON MESSAGE (compatible with C# parser)
# ==============================================================
def build_skeleton_msg(right_hand, left_hand, cursor_x, cursor_y):
    """Build SKELETON message matching what the C# client expects."""
    parts = {}

    if right_hand:
        lm = right_hand  # plain list of landmarks
        parts["rw"] = [round(cursor_x, 3), round(cursor_y, 3)]
        parts["re"] = [round(lm[5].x, 3), round(lm[5].y, 3)]
        parts["rs"] = [round(lm[0].x, 3), round(max(0.0, lm[0].y - 0.15), 3)]

    if left_hand:
        lm = left_hand
        parts["lw"] = [round(lm[0].x, 3), round(lm[0].y, 3)]
        parts["le"] = [round(lm[5].x, 3), round(lm[5].y, 3)]
        parts["ls"] = [round(lm[0].x, 3), round(max(0.0, lm[0].y - 0.15), 3)]

    if not parts:
        return None

    formatted = ",".join(f'{k}:[{v[0]} {v[1]}]' for k, v in parts.items())
    return f"SKELETON:{{{formatted}}}"


# ==============================================================
#  MAIN LOOP
# ==============================================================
def main():
    connect_to_hub()
    threading.Thread(target=hub_listener, daemon=True).start()

    # Connect to Frame Broadcaster
    frame_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    while True:
        try:
            frame_sock.connect((BROADCASTER_IP, BROADCASTER_PORT))
            print("[+] Gesture Controller: Connected to Frame Broadcaster (port 6000).")
            break
        except Exception:
            print("[.] Gesture Controller: Waiting for Frame Broadcaster...")
            time.sleep(2)

    # Initialize MediaPipe Holistic
    landmarker = create_holistic_landmarker()

    # ── State Variables ──────────────────────────────────────
    ema_x, ema_y = 0.5, 0.5
    trail = deque(maxlen=50)           # Right hand cursor trail for swipe
    last_gesture = ""
    last_gesture_time = 0.0
    frame_count = 0

    # Fist click tracking
    fist_start_time = 0.0
    fist_active = False
    fist_click_fired = False

    # Palm hold tracking (for login)
    palm_start_time = 0.0
    palm_active = False
    palm_fired = False

    # Peace hold tracking (for signup nav)
    peace_start_time = 0.0
    peace_active = False
    peace_fired = False

    # Menu state
    show_menu = False
    menu_hover = -1
    menu_drop_counter = 0
    left_palm_counter = 0

    print("[*] Gesture Controller running. Waiting for frames...")

    try:
        while True:
            # 1. Receive frame from Broadcaster
            header = recv_all(frame_sock, 4)
            if not header:
                break
            size = struct.unpack(">L", header)[0]
            img_data = recv_all(frame_sock, size)
            if not img_data:
                break

            frame_count += 1

            # Decode JPEG
            nparr = np.frombuffer(img_data, np.uint8)
            frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if frame is None:
                continue

            h, w, _ = frame.shape
            
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)

            # 2. Process with MediaPipe Holistic
            results = landmarker.detect(mp_image)

            now = time.time()
            right_hand = results.right_hand_landmarks
            left_hand = results.left_hand_landmarks
            right_state = HandState.UNKNOWN
            left_state = HandState.UNKNOWN

            if right_hand:
                right_state = classify_hand_state(right_hand)
                mp_drawing.draw_landmarks(frame, right_hand, vision.HandLandmarksConnections.HAND_CONNECTIONS)

            if left_hand:
                left_state = classify_hand_state(left_hand)
                mp_drawing.draw_landmarks(frame, left_hand, vision.HandLandmarksConnections.HAND_CONNECTIONS)

            if results.pose_landmarks:
                mp_drawing.draw_landmarks(frame, results.pose_landmarks, vision.PoseLandmarksConnections.POSE_LANDMARKS,
                    landmark_drawing_spec=mp_drawing_styles.get_default_pose_landmarks_style())

            # 3. Cursor tracking (right hand index finger)
            cursor_x, cursor_y = 0.5, 0.5
            if right_hand:
                raw_x, raw_y = get_index_tip(right_hand)

                # EMA smoothing
                ema_x = EMA_ALPHA * raw_x + (1 - EMA_ALPHA) * ema_x
                ema_y = EMA_ALPHA * raw_y + (1 - EMA_ALPHA) * ema_y

                # Sensitivity amplification
                cursor_x = (ema_x - 0.5) * SENSITIVITY + 0.5
                cursor_y = (ema_y - 0.5) * SENSITIVITY + 0.5
                cursor_x = max(0.0, min(1.0, cursor_x))
                cursor_y = max(0.0, min(1.0, cursor_y))

                # Add to swipe trail
                trail.append((cursor_x, cursor_y))

                # Send cursor position
                send_to_hub(f"HAND_CURSOR:{cursor_x:.4f},{cursor_y:.4f}")
            else:
                trail.clear()
                fist_active = False
                fist_click_fired = False

            # 4. Send skeleton data
            if frame_count % 3 == 0:
                skel_msg = build_skeleton_msg(right_hand, left_hand, cursor_x, cursor_y)
                if skel_msg:
                    send_to_hub(skel_msg)

            # 5. Send hand state
            send_to_hub(f"HAND_STATE:{right_state}")

            # ── FIST CLICK Detection ─────────────────────────
            if right_state == HandState.FIST:
                if not fist_active:
                    fist_active = True
                    fist_start_time = now
                    fist_click_fired = False
                elif not fist_click_fired and (now - fist_start_time) * 1000 >= FIST_HOLD_MS:
                    if now - last_gesture_time > GESTURE_COOLDOWN:
                        if show_menu and menu_hover >= 0:
                            send_to_hub(f"MENU_SELECT:{menu_hover}:{MENU_ITEMS[menu_hover]}")
                            print(f"[*] Menu selected: {MENU_ITEMS[menu_hover]}")
                            show_menu = False
                            send_to_hub("MENU_HIDE")
                        else:
                            send_to_hub("GESTURE:click")
                            print("[*] Fist Click!")
                        last_gesture_time = now
                        fist_click_fired = True
            else:
                fist_active = False
                fist_click_fired = False

            # ── OPEN PALM Hold (SignIn → auto-login) ─────────
            if current_page == "SignIn" and right_state == HandState.OPEN:
                if not palm_active:
                    palm_active = True
                    palm_start_time = now
                    palm_fired = False
                elif not palm_fired and (now - palm_start_time) * 1000 >= PALM_HOLD_MS:
                    send_to_hub("GESTURE:palm_login")
                    print("[*] Palm Login Triggered!")
                    palm_fired = True
                    last_gesture_time = now
            else:
                palm_active = False
                palm_fired = False

            # ── PEACE Sign Hold (SignIn → SignUp navigation) ──
            if current_page == "SignIn" and right_state == HandState.PEACE:
                if not peace_active:
                    peace_active = True
                    peace_start_time = now
                    peace_fired = False
                elif not peace_fired and (now - peace_start_time) * 1000 >= PALM_HOLD_MS:
                    send_to_hub("GESTURE:peace_signup")
                    print("[*] Peace → Sign Up Triggered!")
                    peace_fired = True
                    last_gesture_time = now
            else:
                peace_active = False
                peace_fired = False

            # ── LEFT HAND Open Palm → Circular Menu ──────────
            is_left_open = (left_state == HandState.OPEN)
            if is_left_open:
                left_palm_counter += 1
                menu_drop_counter = 0
                if left_palm_counter > 8 and not show_menu:
                    show_menu = True
                    send_to_hub("MENU_SHOW")
                    print("[*] Circular Menu OPENED (Left Palm)")
            else:
                left_palm_counter = 0
                if show_menu:
                    menu_drop_counter += 1
                    if menu_drop_counter > 15:
                        show_menu = False
                        send_to_hub("MENU_HIDE")
                        print("[*] Circular Menu CLOSED")

            # ── Menu Hover ───────────────────────────────────
            if show_menu and right_hand:
                menu_hover = check_menu_selection(cursor_x, cursor_y)
                if menu_hover >= 0:
                    send_to_hub(f"MENU_HOVER:{menu_hover}:{MENU_ITEMS[menu_hover]}")

            # ── SWIPE Detection ──────────────────────────────
            if (right_state == HandState.POINT or right_state == HandState.OPEN) \
                    and len(trail) >= 20 and frame_count % 8 == 0:
                if now - last_gesture_time > GESTURE_COOLDOWN:
                    swipe = detect_swipe(list(trail))
                    if swipe:
                        if not show_menu:
                            send_to_hub(f"GESTURE:{swipe}")
                        print(f"[G] {swipe}")
                        last_gesture = swipe
                        last_gesture_time = now
                        trail.clear()

            # ── VISUAL FEEDBACK (Flipped for Mirror) ─────────
            display_frame = cv2.flip(frame, 1)
            
            # Draw cursor on display_frame
            if right_hand:
                cx_px, cy_px = int((1.0 - cursor_x) * w), int(cursor_y * h)
                cv2.circle(display_frame, (cx_px, cy_px), 12, (0, 255, 0), -1)
                cv2.circle(display_frame, (cx_px, cy_px), 14, (255, 255, 255), 2)

            cv2.putText(display_frame, f"R: {right_state}  L: {left_state}", (10, 30),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
            cv2.putText(display_frame, f"Page: {current_page}", (10, h - 20),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (200, 200, 200), 1)

            cv2.imshow("Gesture Controller - MediaPipe Hands", display_frame)
            if cv2.waitKey(1) & 0xFF == ord('q'):
                break

    except Exception as e:
        print(f"[!] Gesture Controller Error: {e}")
        import traceback
        traceback.print_exc()
    finally:
        landmarker.close()
        frame_sock.close()
        if hub_conn:
            hub_conn.close()
        cv2.destroyAllWindows()
        print("[*] Gesture Controller stopped.")


if __name__ == "__main__":
    main()
