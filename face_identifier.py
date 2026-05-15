import cv2
import numpy as np
import os
import socket
import threading
import time
import struct
from db_manager import db
from dataclasses import dataclass, field
from typing import List, Dict, Tuple, Optional
from datetime import datetime

# dlib for face detection and recognition
try:
    import dlib
    DLIB_AVAILABLE = True
except ImportError:
    DLIB_AVAILABLE = False

# --- Configuration ---
BROADCASTER_IP = "127.0.0.1"
BROADCASTER_PORT = 6000
HUB_IP = "127.0.0.1"
HUB_PORT = 6001

FACE_DETECTION_SCALE = 0.5
FACE_RECOGNITION_TOLERANCE = 0.6
FACE_LOSS_TIMEOUT = 2.0

# --- Hub Communication ---
hub_conn = None

def connect_to_hub():
    global hub_conn
    while True:
        try:
            hub_conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            hub_conn.connect((HUB_IP, HUB_PORT))
            print("[+] Face Worker: Connected to Hub.")
            break
        except:
            time.sleep(2)

def send_to_hub(msg):
    if hub_conn:
        try:
            hub_conn.sendall((msg + "\n").encode('utf-8'))
        except: pass

def hub_listener(engine):
    """Listens for commands from the Hub (relayed from C#)."""
    while True:
        try:
            data = hub_conn.recv(1024)
            if not data: break
            msg = data.decode('utf-8').strip()
            if msg.startswith("REGISTER:"):
                name = msg.split(":", 1)[1] if ":" in msg else "Unknown"
                engine.start_registration(name)
        except: break

# --- Data Classes & Engine (Simplified for Distributed) ---
@dataclass
class FaceIdentity:
    id: str; name: str; encoding: List[float]

@dataclass
class TrackedFace:
    session_id: str; rect: any; encoding: Optional[List[float]] = None; identity: Optional[FaceIdentity] = None; last_seen: float = field(default_factory=time.time)

class FaceDatabase:
    def __init__(self):
        self.identities = {}
        self._load()
    def _load(self):
        try:
            for u in db.get_all_users():
                self.identities[u['id']] = FaceIdentity(u['id'], u['name'], u['face_encoding'])
        except: pass
    def find_match(self, encoding):
        if not encoding or not self.identities: return None
        enc_arr = np.array(encoding)
        best_match, best_dist = None, float('inf')
        for iden in self.identities.values():
            dist = np.linalg.norm(enc_arr - np.array(iden.encoding))
            if dist < best_dist: best_dist = dist; best_match = iden
        if best_dist <= FACE_RECOGNITION_TOLERANCE: return best_match, 1.0 - best_dist
        return None

class FaceRecognitionEngine:
    def __init__(self):
        self.db = FaceDatabase()
        self.tracked_faces = {}
        self.detector = dlib.get_frontal_face_detector() if DLIB_AVAILABLE else None
        self.predictor = dlib.shape_predictor("shape_predictor_68_face_landmarks.dat") if DLIB_AVAILABLE else None
        self.encoder = dlib.face_recognition_model_v1("dlib_face_recognition_resnet_model_v1.dat") if DLIB_AVAILABLE else None
        self.recording_mode = False
        self.pending_name = None
        self.next_sid = 0

    def start_registration(self, name):
        self.recording_mode = True; self.pending_name = name
        print(f"[*] Registration mode active for: {name}")

    def process(self, frame):
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        small = cv2.resize(rgb, (0,0), fx=FACE_DETECTION_SCALE, fy=FACE_DETECTION_SCALE)
        rects = self.detector(small, 1) if self.detector else []
        
        current_sids = set()
        for r in rects:
            scaled_rect = dlib.rectangle(int(r.left()/FACE_DETECTION_SCALE), int(r.top()/FACE_DETECTION_SCALE), 
                                         int(r.right()/FACE_DETECTION_SCALE), int(r.bottom()/FACE_DETECTION_SCALE))
            
            shape = self.predictor(rgb, scaled_rect)
            encoding = list(self.encoder.compute_face_descriptor(rgb, shape))
            
            # Simple spatial matching for tracking
            matched_sid = None
            for sid, tf in self.tracked_faces.items():
                if abs(tf.rect.left() - scaled_rect.left()) < 50: matched_sid = sid; break
            
            if matched_sid:
                tf = self.tracked_faces[matched_sid]
                tf.rect = scaled_rect; tf.encoding = encoding; tf.last_seen = time.time()
                current_sids.add(matched_sid)
            else:
                sid = f"face_{self.next_sid}"; self.next_sid += 1
                tf = TrackedFace(sid, scaled_rect, encoding)
                self.tracked_faces[sid] = tf; current_sids.add(sid)
                send_to_hub(f"FACE_UNKNOWN:{sid}")
            
            # Attempt Recognition
            match = self.db.find_match(encoding)
            label = "Unknown"
            color = (0, 0, 255) # Red for unknown
            if match:
                iden, conf = match; tf.identity = iden
                label = f"{iden.name} ({conf:.2f})"
                color = (0, 255, 0) # Green for recognized
                send_to_hub(f"FACE_DETECTED:{iden.id}:{iden.name}:{conf:.2f}")
            
            # Draw on frame for "Monitor" view
            x1, y1, x2, y2 = scaled_rect.left(), scaled_rect.top(), scaled_rect.right(), scaled_rect.bottom()
            cv2.rectangle(frame, (x1, y1), (x2, y2), color, 2)
            cv2.putText(frame, label, (x1, y1 - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

            # Registration
            if self.recording_mode and self.pending_name:
                new_id = db.register_user(self.pending_name, encoding)
                self.db.identities[new_id] = FaceIdentity(new_id, self.pending_name, encoding)
                send_to_hub(f"FACE_REGISTERED:{new_id}:{self.pending_name}")
                self.recording_mode = False; self.pending_name = None
                cv2.putText(frame, "REGISTRATION SUCCESS", (50, 50), cv2.FONT_HERSHEY_SIMPLEX, 1.0, (255, 255, 0), 3)

        # Cleanup lost faces
        lost = [sid for sid, tf in self.tracked_faces.items() if time.time() - tf.last_seen > FACE_LOSS_TIMEOUT]
        for sid in lost:
            tf = self.tracked_faces[sid]
            msg = f"FACE_LEFT:{tf.identity.id if tf.identity else sid}"
            send_to_hub(msg); del self.tracked_faces[sid]

def recv_all(sock, n):
    data = b''
    while len(data) < n:
        packet = sock.recv(n - len(data))
        if not packet: return None
        data += packet
    return data

def download_models():
    """Download dlib models if missing."""
    import urllib.request
    import bz2
    models = {
        "shape_predictor_68_face_landmarks.dat": "http://dlib.net/files/shape_predictor_68_face_landmarks.dat.bz2",
        "dlib_face_recognition_resnet_model_v1.dat": "http://dlib.net/files/dlib_face_recognition_resnet_model_v1.dat.bz2"
    }
    for name, url in models.items():
        if not os.path.exists(name):
            print(f"[*] Downloading {name} (this may take a minute)...")
            bz2_name = name + ".bz2"
            urllib.request.urlretrieve(url, bz2_name)
            print(f"[*] Extracting {name}...")
            with bz2.BZ2File(bz2_name) as fr, open(name, "wb") as fw:
                fw.write(fr.read())
            os.remove(bz2_name)
            print(f"[+] {name} ready.")

def main():
    print(">>> Face Identifier Starting...")
    print("=== Story Builder Face Identifier ===")
    if DLIB_AVAILABLE:
        download_models()
    engine = FaceRecognitionEngine()
    connect_to_hub()
    threading.Thread(target=hub_listener, args=(engine,), daemon=True).start()
    
    frame_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    while True:
        try:
            frame_sock.connect((BROADCASTER_IP, BROADCASTER_PORT))
            print("[+] Face Worker: Connected to Broadcaster.")
            break
        except:
            print("[.] Face Worker: Waiting for Broadcaster...")
            time.sleep(2)
    
    frame_counter = 0
    try:
        while True:
            header = recv_all(frame_sock, 4)
            if not header: break
            size = struct.unpack(">L", header)[0]
            img_data = recv_all(frame_sock, size)
            if not img_data: break
            
            frame_counter += 1
            if frame_counter % 3 != 0: # Process every 3rd frame (Face ID is slow)
                continue
            
            nparr = np.frombuffer(img_data, np.uint8)
            frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if frame is None: continue
            
            engine.process(frame)
            cv2.imshow("Face Worker (Distributed)", frame)
            if cv2.waitKey(1) & 0xFF == ord('q'): break
    finally:
        frame_sock.close(); hub_conn.close(); cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
