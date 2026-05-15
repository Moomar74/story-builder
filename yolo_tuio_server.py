import cv2
import socket
import struct
import time
import threading
from ultralytics import YOLO
import numpy as np
import math

# --- TUIO / OSC Encoding Helpers ---

def osc_string(s):
    """Encodes a string into OSC format (null-terminated, padded to 4 bytes)."""
    b = s.encode('utf-8') + b'\x00'
    while len(b) % 4 != 0:
        b += b'\x00'
    return b

def osc_blob(data):
    """Encodes a blob (byte string) into OSC format."""
    length = len(data)
    b = struct.pack('>i', length) + data
    while len(b) % 4 != 0:
        b += b'\x00'
    return b

def encode_osc_message(address, types, *args):
    """Encodes a single OSC message."""
    packet = osc_string(address)
    packet += osc_string(',' + types)
    for t, arg in zip(types, args):
        if t == 'i':
            packet += struct.pack('>i', arg)
        elif t == 'f':
            packet += struct.pack('>f', float(arg))
        elif t == 's':
            packet += osc_string(arg)
    return packet

def encode_tuio_bundle(messages):
    """Encodes a list of OSC messages into an OSC bundle."""
    # Bundle header
    packet = b'#bundle\x00'
    # Timetag (8 bytes) - 0 means "immediately"
    packet += struct.pack('>Q', 0)
    
    for msg in messages:
        # Each message in a bundle is preceded by its size (4-byte int)
        packet += struct.pack('>i', len(msg))
        packet += msg
    return packet

# --- Configuration ---
BROADCASTER_IP = "127.0.0.1"
BROADCASTER_PORT = 6000
HUB_IP = "127.0.0.1"
HUB_PORT = 6001

# --- TUIO Client Config ---
TUIO_IP = "127.0.0.1" 
TUIO_PORT = 3333
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)

# --- Hub Communication ---
hub_conn = None

def connect_to_hub():
    global hub_conn
    while True:
        try:
            hub_conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            hub_conn.connect((HUB_IP, HUB_PORT))
            print("[+] YOLO Worker: Connected to Hub.")
            break
        except:
            time.sleep(2)

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

# --- YOLO Detection Mapping ---
CLASS_MAP = {
    26: 1,  # Handbag -> Fox
    39: 0,  # Bottle -> Lion
    67: 2,  # Phone -> Eagle
    24: 3,  # Backpack -> Wolf
}

def main():
    print(f"Starting YOLO-TUIO Worker... TUIO -> {TUIO_IP}:{TUIO_PORT}")
    
    # Load YOLOv8 model
    model = YOLO("yolov8n.pt")
    
    # Connect to Hub
    threading.Thread(target=connect_to_hub, daemon=True).start()

    # Connect to Frame Broadcaster
    frame_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    while True:
        try:
            frame_sock.connect((BROADCASTER_IP, BROADCASTER_PORT))
            print("[+] YOLO Worker: Connected to Frame Broadcaster.")
            break
        except:
            print("[.] YOLO Worker: Waiting for Frame Broadcaster...")
            time.sleep(2)

    frame_seq = 0

    try:
        frame_count = 0
        while True:
            # 1. Receive frame from Broadcaster
            header = recv_all(frame_sock, 4)
            if not header: break
            size = struct.unpack(">L", header)[0]
            img_data = recv_all(frame_sock, size)
            if not img_data: break
            
            frame_count += 1
            if frame_count % 3 != 0: # Process every 3rd frame
                continue

            # Decode JPEG
            nparr = np.frombuffer(img_data, np.uint8)
            frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if frame is None: continue
            
            h, w, _ = frame.shape

            # Run YOLO on CPU with reduced image size
            results = model(frame, verbose=False, device='cpu', imgsz=320)[0]
            
            active_objects = []
            
            for box in results.boxes:
                cls_id = int(box.cls[0])
                if cls_id in CLASS_MAP:
                    symbol_id = CLASS_MAP[cls_id]
                    
                    # Box coordinates
                    xyxy = box.xyxy[0].tolist()
                    x1, y1, x2, y2 = map(int, xyxy)
                    
                    # --- REAL ANGLE DETECTION ---
                    angle = 0.0
                    try:
                        margin = 5
                        crop = frame[max(0, y1-margin):min(h, y2+margin), 
                                     max(0, x1-margin):min(w, x2+margin)]
                        
                        if crop.size > 0:
                            gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
                            _, thresh = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
                            contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                            if contours:
                                cnt = max(contours, key=cv2.contourArea)
                                rect = cv2.minAreaRect(cnt)
                                (center, (width_rect, height_rect), angle_deg) = rect
                                if width_rect < height_rect:
                                    angle_rad = -math.radians(angle_deg)
                                else:
                                    angle_rad = -math.radians(angle_deg - 90)
                                angle = angle_rad
                    except Exception:
                        angle = 0.0
                    
                    # Normalized TUIO coords (0.0 to 1.0)
                    cx = (x1 + x2) / 2 / w
                    cy = (y1 + y2) / 2 / h
                        
                    active_objects.append({
                        's_id': cls_id, 
                        'sym_id': symbol_id,
                        'x': cx,
                        'y': cy,
                        'box': (x1, y1, x2, y2),
                        'angle': angle
                    })

            # --- Construct TUIO Messages ---
            # 1. source message
            msg_source = encode_osc_message("/tuio/2Dobj", "s", "source", "yolo_v8")
            sock.sendto(msg_source, (TUIO_IP, TUIO_PORT))
            
            # 2. alive message
            s_ids = [obj['s_id'] for obj in active_objects]
            msg_alive = encode_osc_message("/tuio/2Dobj", "s" + "i" * len(s_ids), "alive", *s_ids)
            sock.sendto(msg_alive, (TUIO_IP, TUIO_PORT))
            
            # 3. set messages
            for obj in active_objects:
                msg_set = encode_osc_message("/tuio/2Dobj", "siiffffffff", 
                    "set", obj['s_id'], obj['sym_id'], 
                    obj['x'], obj['y'], obj['angle'], 
                    0.0, 0.0, 0.0, 0.0, 0.0)
                sock.sendto(msg_set, (TUIO_IP, TUIO_PORT))
            
            # 4. fseq message
            msg_fseq = encode_osc_message("/tuio/2Dobj", "si", "fseq", frame_seq + 1)
            sock.sendto(msg_fseq, (TUIO_IP, TUIO_PORT))
            
            frame_seq += 1

            # Periodic log
            if frame_seq % 60 == 0:
                print(f"Frame {frame_seq}: Active: {len(active_objects)} objects.")

            # --- Visual Feedback ---
            for obj in active_objects:
                x1, y1, x2, y2 = obj['box']
                label = f"Symbol {obj['sym_id']}"
                cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 255, 0), 2)
                cv2.putText(frame, label, (x1, y1 - 10), 
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
                
            cv2.imshow("YOLO Worker (Distributed)", frame)
            if cv2.waitKey(1) & 0xFF == ord('q'):
                break

    finally:
        frame_sock.close()
        cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
