import cv2
import socket
import struct
import time
from ultralytics import YOLO

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

# --- TUIO Client Config ---
TUIO_IP = "127.0.0.1" # Use localhost
TUIO_PORT = 3333
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
# Enable broadcast just in case
sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)

# --- YOLO Detection Mapping ---
CLASS_MAP = {
    26: 1,  # Handbag -> Fox
    39: 0,  # Bottle -> Lion
    67: 2,  # Phone -> Eagle
    24: 3,  # Backpack -> Wolf
}

def main():
    print(f"Starting YOLO-TUIO Server... Broadcasting to {TUIO_IP}:{TUIO_PORT}")
    
    # Load YOLOv8 model
    model = YOLO("yolov8n.pt")
    
    # Open camera
    cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        print("Error: Could not open camera.")
        return

    frame_seq = 0
    start_time = time.time()

    try:
        while True:
            ret, frame = cap.read()
            if not ret:
                break
            # Mirror frame for intuitive placement
            frame = cv2.flip(frame, 1)
            h, w, _ = frame.shape

            # Run YOLO on CPU
            results = model(frame, verbose=False, device='cpu')[0]
            
            active_objects = []
            
            for box in results.boxes:
                cls_id = int(box.cls[0])
                if cls_id in CLASS_MAP:
                    symbol_id = CLASS_MAP[cls_id]
                    
                    # Box coordinates
                    xyxy = box.xyxy[0].tolist()
                    x1, y1, x2, y2 = map(int, xyxy)
                    
                    # --- REAL ANGLE DETECTION ---
                    # Crop the frame to the bounding box and find the dominant axis
                    angle = 0.0
                    try:
                        # Add a small margin to the crop
                        margin = 5
                        crop = frame[max(0, y1-margin):min(h, y2+margin), 
                                     max(0, x1-margin):min(w, x2+margin)]
                        
                        if crop.size > 0:
                            # Convert to grayscale and threshold to find the object shape
                            gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
                            _, thresh = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
                            
                            # Find contours in the crop
                            contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                            if contours:
                                # Get the largest contour
                                cnt = max(contours, key=cv2.contourArea)
                                # Get the minimum area rectangle (rotated rectangle)
                                rect = cv2.minAreaRect(cnt)
                                (center, (width_rect, height_rect), angle_deg) = rect
                                
                                # Convert minAreaRect angle to standard orientation
                                if width_rect < height_rect:
                                    angle_rad = -math.radians(angle_deg)
                                else:
                                    angle_rad = -math.radians(angle_deg - 90)
                                
                                # Normalize angle to be relative to vertical
                                angle = angle_rad
                    except Exception as e:
                        # Fallback to no tilt if calculation fails
                        angle = 0.0
                    
                    # Normalized TUIO coords (0.0 to 1.0)
                    cx = (x1 + x2) / 2 / w
                    cy = (y1 + y2) / 2 / h
                        
                    active_objects.append({
                        's_id': cls_id, 
                        'sym_id': symbol_id,
                        'x': cx,
                        'y': cy,
                        'angle': angle
                    })

            # --- Construct TUIO Messages ---
            # Send them individually for maximum compatibility
            
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
                print(f"Frame {frame_seq}: Active: {len(active_objects)} objects. Detect Angle: {active_objects[0]['angle'] if active_objects else 0.0:.2f} rad")

            # --- Visual Feedback ---
            for obj in active_objects:
                label = f"Symbol {obj['sym_id']}"
                cv2.rectangle(frame, (int((obj['x']-0.05)*w), int((obj['y']-0.05)*h)), 
                              (int((obj['x']+0.05)*w), int((obj['y']+0.05)*h)), (0, 255, 0), 2)
                cv2.putText(frame, label, (int(obj['x']*w), int(obj['y']*h)), 
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
                
            cv2.imshow("YOLO-TUIO Tracker", frame)
            if cv2.waitKey(1) & 0xFF == ord('q'):
                break

    finally:
        cap.release()
        cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
