"""
Laser / Green Marker Tracking Server
=====================================
Detects and tracks a green laser pointer or green marker in the camera feed
using HSV colour-space masking + contour analysis (PyImageSearch ball-tracking
technique). Sends normalised (x, y) coordinates to the Interaction Hub so the
C# client can use them as a pointer.

Protocol messages sent to the Hub (relayed to C# clients):
    LASER:<x>,<y>              – normalised [0..1] pointer position
    LASER_STATUS:tracking      – green dot is visible
    LASER_STATUS:lost          – green dot is NOT visible
    LASER_TRAIL:<x1>,<y1>;...  – recent N trail points (for drawing contrail)

Architecture:
    Frame Broadcaster (port 6000)  -->  [this script]  -->  Hub (port 6001)
"""

import cv2
import numpy as np
import socket
import struct
import threading
import time
from collections import deque

# ── Configuration ────────────────────────────────────────────────────────────
BROADCASTER_IP   = "127.0.0.1"
BROADCASTER_PORT = 6000          # Frame Broadcaster
HUB_IP           = "127.0.0.1"
HUB_PORT         = 6001          # Interaction Hub

# Red usually needs two ranges in HSV: 0-10 and 160-180
LASER_LOWER1 = (0, 100, 100)
LASER_UPPER1 = (10, 255, 255)
LASER_LOWER2 = (160, 100, 100)
LASER_UPPER2 = (179, 255, 255)

# For a bright green *laser* dot you may want a tighter, brighter range:
# GREEN_LOWER = (35, 100, 100)
# GREEN_UPPER = (85, 255, 255)

MIN_RADIUS       = 5            # Ignore contours smaller than this (pixels)
TRAIL_LENGTH      = 64           # Number of past positions to keep
SMOOTHING_ALPHA   = 0.4          # EMA smoothing factor (0 = full smooth, 1 = raw)
PROCESS_EVERY_N   = 1            # Process every Nth frame (1 = every frame)
TRAIL_SEND_EVERY  = 5            # Send full trail every N frames (to save bandwidth)

# ── State ────────────────────────────────────────────────────────────────────
hub_conn: socket.socket | None = None
pts = deque(maxlen=TRAIL_LENGTH)
last_smooth_x, last_smooth_y = 0.5, 0.5


# ── Hub Connection ───────────────────────────────────────────────────────────
def connect_to_hub():
    """Connect to the Interaction Hub with retry."""
    global hub_conn
    while True:
        try:
            hub_conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            hub_conn.connect((HUB_IP, HUB_PORT))
            print("[+] Laser Tracker: Connected to Hub.")
            break
        except Exception:
            print("[.] Laser Tracker: Waiting for Hub...")
            time.sleep(2)


def hub_listener():
    """Listen for commands relayed from the C# client through the Hub."""
    global LASER_LOWER1, LASER_UPPER1, LASER_LOWER2, LASER_UPPER2
    while True:
        try:
            data = hub_conn.recv(1024)
            if not data:
                break
            for msg in data.decode("utf-8").strip().split("\n"):
                msg = msg.strip()
                if not msg:
                    continue
                # Allow runtime HSV recalibration from C# side
                # e.g.  LASER_CAL:160,100,100,179,255,255
                if msg.startswith("LASER_CAL:"):
                    vals = list(map(int, msg.split(":", 1)[1].split(",")))
                    if len(vals) == 6:
                        # For simplicity, calibration command updates Range 2 (the common red/other range)
                        LASER_LOWER2 = tuple(vals[:3])
                        LASER_UPPER2 = tuple(vals[3:])
                        print(f"[*] HSV recalibrated: {LASER_LOWER2} → {LASER_UPPER2}")
        except Exception:
            break


def send_to_hub(msg: str):
    """Send a single newline-terminated message to the Hub."""
    if hub_conn:
        try:
            hub_conn.sendall((msg + "\n").encode("utf-8"))
        except Exception:
            pass


# ── Frame Reception ──────────────────────────────────────────────────────────
def recv_all(sock: socket.socket, n: int) -> bytes | None:
    """Receive exactly *n* bytes from *sock*."""
    data = b""
    while len(data) < n:
        packet = sock.recv(n - len(data))
        if not packet:
            return None
        data += packet
    return data


# ── Main Loop ────────────────────────────────────────────────────────────────
def main():
    global last_smooth_x, last_smooth_y

    connect_to_hub()
    threading.Thread(target=hub_listener, daemon=True).start()

    # Connect to Frame Broadcaster
    frame_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    while True:
        try:
            frame_sock.connect((BROADCASTER_IP, BROADCASTER_PORT))
            print("[+] Laser Tracker: Connected to Frame Broadcaster.")
            break
        except Exception:
            print("[.] Laser Tracker: Waiting for Frame Broadcaster...")
            time.sleep(2)

    frame_counter = 0

    try:
        while True:
            # ── 1. Receive a JPEG frame ──────────────────────────────────
            header = recv_all(frame_sock, 4)
            if not header:
                break
            size = struct.unpack(">L", header)[0]
            img_data = recv_all(frame_sock, size)
            if not img_data:
                break

            frame_counter += 1
            if frame_counter % PROCESS_EVERY_N != 0:
                continue

            # Decode JPEG → numpy array
            nparr = np.frombuffer(img_data, np.uint8)
            frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if frame is None:
                continue

            h, w = frame.shape[:2]

            # ── 2. Pre-process (blur + HSV) ──────────────────────────────
            blurred = cv2.GaussianBlur(frame, (11, 11), 0)
            hsv = cv2.cvtColor(blurred, cv2.COLOR_BGR2HSV)

            # ── 3. Mask for red colour (Dual Range) ──────────────────────
            mask1 = cv2.inRange(hsv, LASER_LOWER1, LASER_UPPER1)
            mask2 = cv2.inRange(hsv, LASER_LOWER2, LASER_UPPER2)
            mask = cv2.addWeighted(mask1, 1.0, mask2, 1.0, 0.0)

            mask = cv2.erode(mask, None, iterations=2)
            mask = cv2.dilate(mask, None, iterations=2)

            # ── 4. Find contours ─────────────────────────────────────────
            # OpenCV 3: (image, contours, hierarchy); OpenCV 4+: (contours, hierarchy)
            _fc = cv2.findContours(
                mask.copy(), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
            )
            if _fc is None:
                cnts = []
            elif len(_fc) == 2:
                cnts, _ = _fc
            elif len(_fc) == 3:
                _, cnts, _ = _fc
            else:
                cnts = []

            center = None

            if len(cnts) > 0:
                # Largest contour → minimum enclosing circle + centroid
                c = max(cnts, key=cv2.contourArea)
                if c is not None and len(c) > 0:
                    me = cv2.minEnclosingCircle(c)
                    # Degenerate contours can yield (None, None) in some OpenCV builds
                    if (
                        me is not None
                        and me[0] is not None
                        and me[1] is not None
                    ):
                        (cx, cy), radius = me
                        M = cv2.moments(c)

                        if M["m00"] > 0 and radius > MIN_RADIUS:
                            center = (
                                int(M["m10"] / M["m00"]),
                                int(M["m01"] / M["m00"]),
                            )

                            # Normalise to [0..1]
                            raw_x = center[0] / w
                            raw_y = center[1] / h

                            # EMA smoothing
                            sx = SMOOTHING_ALPHA * raw_x + (1 - SMOOTHING_ALPHA) * last_smooth_x
                            sy = SMOOTHING_ALPHA * raw_y + (1 - SMOOTHING_ALPHA) * last_smooth_y
                            last_smooth_x, last_smooth_y = sx, sy

                            sx = max(0.0, min(1.0, sx))
                            sy = max(0.0, min(1.0, sy))

                            # Send position
                            send_to_hub(f"LASER:{sx:.4f},{sy:.4f}")
                            send_to_hub("LASER_STATUS:tracking")

                            # Store in trail
                            pts.appendleft((sx, sy))

                            # Periodically send the full trail
                            if frame_counter % TRAIL_SEND_EVERY == 0 and len(pts) > 1:
                                parts = []
                                for pt in pts:
                                    if pt is None:
                                        continue
                                    px, py = pt
                                    parts.append(f"{px:.4f},{py:.4f}")
                                trail_str = ";".join(parts)
                                if trail_str:
                                    send_to_hub(f"LASER_TRAIL:{trail_str}")

                            # ── Local debug visualisation ────────────────────────
                            cv2.circle(frame, (int(cx), int(cy)), int(radius), (0, 255, 255), 2)
                            cv2.circle(frame, center, 5, (0, 0, 255), -1)

                            # Draw contrail
                            for i in range(1, len(pts)):
                                if pts[i - 1] is None or pts[i] is None:
                                    continue
                                p1_px = (int(pts[i - 1][0] * w), int(pts[i - 1][1] * h))
                                p2_px = (int(pts[i][0] * w), int(pts[i][1] * h))
                                thickness = int(np.sqrt(TRAIL_LENGTH / float(i + 1)) * 2.5)
                                cv2.line(frame, p1_px, p2_px, (0, 0, 255), thickness)

            if center is None:
                send_to_hub("LASER_STATUS:lost")
                pts.appendleft(None)

            # Show local debug window
            cv2.imshow("Laser Tracker", frame)
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break

    except Exception as e:
        print(f"[!] Laser Tracker Error: {e}")
    finally:
        frame_sock.close()
        if hub_conn:
            hub_conn.close()
        cv2.destroyAllWindows()
        print("[*] Laser Tracker stopped.")


if __name__ == "__main__":
    main()
