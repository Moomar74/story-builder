"""
Face Identification Scenario for AR
====================================
Identifies and recognizes faces in real-time for personalized AR experiences.
Uses dlib for accurate face detection and identification.

Features:
  - Real-time face detection and recognition
  - Face registration system (enroll new users)
  - Persistent face database (JSON + encoded images)
  - TCP server for AR client communication
  - Threading for concurrent processing
  - Confidence scoring for recognition accuracy

Messages sent to client:
  FACE_DETECTED:<face_id>:<name>:<confidence>  - Face recognized
  FACE_REGISTERED:<face_id>:<name>            - New face enrolled
  FACE_UNKNOWN:<session_id>                   - Unknown face detected
  FACE_LEFT:<face_id>                         - Face no longer in frame

Scenario Types:
  - identification: Recognize registered users
  - registration: Enroll new faces
  - monitoring: Track face presence/attention

Controls:
  'r' - Register current face (enter name)
  'd' - Delete registered face
  's' - Save face database
  'l' - List registered faces
  'q' - Quit

Benefits for AR:
  1. Personalized content delivery based on user identity
  2. Secure access control for sensitive AR scenarios
  3. User attention tracking and engagement metrics
  4. Multi-user scenario support (different content per user)
  5. Attendance/logging for educational/training AR apps
"""

import cv2
import numpy as np
import json
import os
import socket
import threading
import time
from dataclasses import dataclass, field
from typing import List, Dict, Tuple, Optional
from collections import deque
from datetime import datetime

# dlib for face detection and recognition
import dlib

# ───────────────────────────────────────────────────────────────────────────────
# Configuration & Constants
# ───────────────────────────────────────────────────────────────────────────────

TCP_PORT = 5003  # Separate port for face identifier
CAMERA_INDEX = 0  # Default camera

# Face detection settings
FACE_DETECTION_SCALE = 0.5  # Scale down for faster processing (0.5 = half size)
FACE_RECOGNITION_TOLERANCE = 0.6  # Lower = stricter matching (0.6 is typical)
MIN_FACE_SIZE = 80  # Minimum face size in pixels

# Database files
FACE_DB_FILE = "face_database.json"
FACE_IMAGES_DIR = "face_images"

# Tracking settings
FACE_LOSS_TIMEOUT = 2.0  # Seconds before considering face lost
RECOGNITION_COOLDOWN = 1.0  # Seconds between recognition events

# Model URLs for dlib
PREDICTOR_URL = "http://dlib.net/files/shape_predictor_68_face_landmarks.dat.bz2"
RECOGNITION_URL = "http://dlib.net/files/dlib_face_recognition_resnet_model_v1.dat.bz2"


# ───────────────────────────────────────────────────────────────────────────────
# Data Classes
# ───────────────────────────────────────────────────────────────────────────────

@dataclass
class FaceIdentity:
    """Represents a registered face identity."""
    id: str
    name: str
    encoding: List[float]
    created_at: str
    last_seen: Optional[float] = None
    recognition_count: int = 0
    
    def to_dict(self) -> dict:
        """Convert to dictionary for JSON serialization."""
        return {
            "id": self.id,
            "name": self.name,
            "encoding": self.encoding,
            "created_at": self.created_at,
            "last_seen": self.last_seen,
            "recognition_count": self.recognition_count
        }
    
    @classmethod
    def from_dict(cls, data: dict) -> 'FaceIdentity':
        """Create from dictionary."""
        return cls(
            id=data["id"],
            name=data["name"],
            encoding=data["encoding"],
            created_at=data["created_at"],
            last_seen=data.get("last_seen"),
            recognition_count=data.get("recognition_count", 0)
        )


@dataclass
class TrackedFace:
    """Represents a face currently being tracked."""
    session_id: str  # Temporary ID for this face session
    rect: dlib.rectangle  # Face rectangle
    encoding: Optional[List[float]] = None
    identity: Optional[FaceIdentity] = None
    confidence: float = 0.0
    first_seen: float = field(default_factory=time.time)
    last_seen: float = field(default_factory=time.time)
    
    @property
    def center(self) -> Tuple[int, int]:
        """Calculate center point of face."""
        return ((self.rect.left() + self.rect.right()) // 2,
                (self.rect.top() + self.rect.bottom()) // 2)
    
    @property
    def is_recognized(self) -> bool:
        """Check if face has been identified."""
        return self.identity is not None


# ───────────────────────────────────────────────────────────────────────────────
# TCP Server for Client Communication
# ───────────────────────────────────────────────────────────────────────────────

connected_clients: List[socket.socket] = []


def broadcast(message: str):
    """Send message to all connected AR clients."""
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


def handle_client(conn: socket.socket, addr: Tuple[str, int]):
    """Handle client connection."""
    print(f"[+] Face ID client connected: {addr}")
    connected_clients.append(conn)
    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            msg = data.decode('utf-8').strip()
            print(f"[<] From client: {msg}")
            
            # Handle client commands
            if msg.startswith("REGISTER:"):
                name = msg.split(":", 1)[1] if ":" in msg else ""
                broadcast(f"CMD_REGISTER:{name}")
            elif msg == "LIST_FACES":
                broadcast(f"CMD_LIST")
    except Exception as e:
        print(f"[!] Client error: {e}")
    finally:
        try:
            connected_clients.remove(conn)
        except ValueError:
            pass
        conn.close()
        print(f"[-] Face ID client disconnected: {addr}")


def start_tcp_server():
    """Start TCP server for client communication."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", TCP_PORT))
    server.listen(5)
    print(f"[*] Face ID TCP Server on port {TCP_PORT}")
    while True:
        conn, addr = server.accept()
        t = threading.Thread(target=handle_client, args=(conn, addr), daemon=True)
        t.start()


# ───────────────────────────────────────────────────────────────────────────────
# Model Download Utility
# ───────────────────────────────────────────────────────────────────────────────

def download_dlib_models():
    """Download dlib models if not present."""
    predictor_path = "shape_predictor_68_face_landmarks.dat"
    recognition_path = "dlib_face_recognition_resnet_model_v1.dat"
    
    # Check if models exist
    if os.path.exists(predictor_path) and os.path.exists(recognition_path):
        return predictor_path, recognition_path
    
    print("[*] dlib models not found. Please download manually:")
    print(f"    1. {PREDICTOR_URL}")
    print(f"    2. {RECOGNITION_URL}")
    print("    Extract .bz2 files and place in this directory")
    print("\n    Or use: pip install face-recognition-models")
    
    # Try alternative: check if face_recognition_models is available
    try:
        import face_recognition_models
        predictor_path = face_recognition_models.pose_predictor_model_location()
        recognition_path = face_recognition_models.face_recognition_model_location()
        print("[+] Using face_recognition_models package")
        return predictor_path, recognition_path
    except ImportError:
        pass
    
    return None, None


# ───────────────────────────────────────────────────────────────────────────────
# Face Database Management
# ───────────────────────────────────────────────────────────────────────────────

class FaceDatabase:
    """Manages persistent face identity storage."""
    
    def __init__(self):
        self.identities: Dict[str, FaceIdentity] = {}
        self._ensure_directories()
        self._load_database()
    
    def _ensure_directories(self):
        """Create necessary directories."""
        if not os.path.exists(FACE_IMAGES_DIR):
            os.makedirs(FACE_IMAGES_DIR)
    
    def _load_database(self):
        """Load face database from disk."""
        if os.path.exists(FACE_DB_FILE):
            try:
                with open(FACE_DB_FILE, 'r') as f:
                    data = json.load(f)
                    for item in data:
                        identity = FaceIdentity.from_dict(item)
                        self.identities[identity.id] = identity
                print(f"[*] Loaded {len(self.identities)} faces from database")
            except Exception as e:
                print(f"[!] Error loading database: {e}")
        else:
            print("[*] No existing face database, starting fresh")
    
    def save(self):
        """Save face database to disk."""
        try:
            data = [identity.to_dict() for identity in self.identities.values()]
            with open(FACE_DB_FILE, 'w') as f:
                json.dump(data, f, indent=2)
            print(f"[+] Saved {len(self.identities)} faces to database")
            return True
        except Exception as e:
            print(f"[!] Error saving database: {e}")
            return False
    
    def register_face(self, name: str, encoding: List[float]) -> FaceIdentity:
        """Register a new face."""
        face_id = f"face_{int(time.time() * 1000)}"
        identity = FaceIdentity(
            id=face_id,
            name=name,
            encoding=encoding,
            created_at=datetime.now().isoformat()
        )
        self.identities[face_id] = identity
        self.save()
        
        # Notify clients
        broadcast(f"FACE_REGISTERED:{face_id}:{name}")
        print(f"[+] Registered new face: {name} (ID: {face_id})")
        
        return identity
    
    def delete_face(self, face_id: str) -> bool:
        """Delete a registered face."""
        if face_id in self.identities:
            name = self.identities[face_id].name
            del self.identities[face_id]
            self.save()
            print(f"[-] Deleted face: {name} (ID: {face_id})")
            return True
        return False
    
    def find_match(self, encoding: List[float]) -> Optional[Tuple[FaceIdentity, float]]:
        """Find matching face in database using Euclidean distance."""
        if not self.identities:
            return None
        
        encoding_array = np.array(encoding)
        best_match = None
        best_distance = float('inf')
        
        for identity in self.identities.values():
            db_encoding = np.array(identity.encoding)
            # Euclidean distance between encodings
            distance = np.linalg.norm(encoding_array - db_encoding)
            
            if distance < best_distance:
                best_distance = distance
                best_match = identity
        
        # Convert distance to confidence (0-1)
        confidence = max(0, 1.0 - best_distance)
        
        if best_distance <= FACE_RECOGNITION_TOLERANCE:
            return (best_match, confidence)
        
        return None
    
    def get_all_faces(self) -> List[FaceIdentity]:
        """Get all registered faces."""
        return list(self.identities.values())
    
    def update_last_seen(self, face_id: str):
        """Update last seen timestamp."""
        if face_id in self.identities:
            self.identities[face_id].last_seen = time.time()
            self.identities[face_id].recognition_count += 1


# ───────────────────────────────────────────────────────────────────────────────
# Face Tracking and Recognition Engine
# ───────────────────────────────────────────────────────────────────────────────

class FaceRecognitionEngine:
    """Main engine for face detection and recognition using dlib."""
    
    def __init__(self):
        self.database = FaceDatabase()
        self.tracked_faces: Dict[str, TrackedFace] = {}
        self.next_session_id = 0
        self.frame_count = 0
        self.recording_mode = False
        self.pending_registration_name = None
        self.lock = threading.Lock()
        
        # Initialize dlib models
        self._init_models()
    
    def _init_models(self):
        """Initialize dlib face detection and recognition models."""
        print("[*] Initializing dlib models...")
        
        # Download/get model paths
        predictor_path, recognition_path = download_dlib_models()
        
        if not predictor_path or not recognition_path:
            print("[!] ERROR: dlib models not available!")
            print("[*] Using OpenCV Haar cascade as fallback for detection only")
            self.detector = None
            self.predictor = None
            self.face_encoder = None
            self.use_opencv_fallback = True
            self.cascade = cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml")
        else:
            # dlib face detector (HOG-based)
            self.detector = dlib.get_frontal_face_detector()
            # 68-point facial landmark predictor
            self.predictor = dlib.shape_predictor(predictor_path)
            # Face recognition model (ResNet-based, 128-d encoding)
            self.face_encoder = dlib.face_recognition_model_v1(recognition_path)
            self.use_opencv_fallback = False
            print("[+] dlib models loaded successfully")
    
    def _get_next_session_id(self) -> str:
        """Generate unique session ID."""
        sid = f"session_{self.next_session_id}"
        self.next_session_id += 1
        return sid
    
    def _rect_to_tuple(self, rect: dlib.rectangle) -> Tuple[int, int, int, int]:
        """Convert dlib rect to (left, top, right, bottom)."""
        return (rect.left(), rect.top(), rect.right(), rect.bottom())
    
    def _get_face_encoding(self, rgb_image: np.ndarray, rect: dlib.rectangle) -> Optional[List[float]]:
        """Get 128-d face encoding from image and face location."""
        if self.use_opencv_fallback or self.predictor is None or self.face_encoder is None:
            return None
        
        try:
            # Get facial landmarks
            shape = self.predictor(rgb_image, rect)
            # Compute face descriptor (128-d encoding)
            face_descriptor = self.face_encoder.compute_face_descriptor(rgb_image, shape)
            return list(face_descriptor)
        except Exception as e:
            print(f"[!] Error computing face encoding: {e}")
            return None
    
    def _detect_faces_opencv(self, gray_image: np.ndarray) -> List[Tuple[int, int, int, int]]:
        """Detect faces using OpenCV Haar cascade (fallback)."""
        if self.cascade is None:
            return []
        
        faces = self.cascade.detectMultiScale(
            gray_image,
            scaleFactor=1.1,
            minNeighbors=5,
            minSize=(MIN_FACE_SIZE, MIN_FACE_SIZE)
        )
        
        # Convert (x, y, w, h) to (left, top, right, bottom)
        results = []
        for (x, y, w, h) in faces:
            results.append((x, y, x + w, y + h))
        return results
    
    def process_frame(self, frame: np.ndarray) -> np.ndarray:
        """Process a single frame for face detection and recognition."""
        self.frame_count += 1
        h, w = frame.shape[:2]
        
        # Create a copy for drawing
        display_frame = frame.copy()
        
        # Convert to RGB for dlib (dlib uses RGB)
        rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        
        # Resize for faster processing
        small_rgb = cv2.resize(rgb_frame, (0, 0), fx=FACE_DETECTION_SCALE, fy=FACE_DETECTION_SCALE)
        
        # Detect faces
        face_rects = []
        if self.use_opencv_fallback:
            # Use OpenCV fallback
            gray = cv2.cvtColor(small_rgb, cv2.COLOR_RGB2GRAY)
            small_faces = self._detect_faces_opencv(gray)
            # Scale back up
            for left, top, right, bottom in small_faces:
                face_rects.append(dlib.rectangle(
                    int(left / FACE_DETECTION_SCALE),
                    int(top / FACE_DETECTION_SCALE),
                    int(right / FACE_DETECTION_SCALE),
                    int(bottom / FACE_DETECTION_SCALE)
                ))
        else:
            # Use dlib detector
            small_rects = self.detector(small_rgb, 1)  # 1 = upsample once for better detection
            # Scale back up
            for rect in small_rects:
                face_rects.append(dlib.rectangle(
                    int(rect.left() / FACE_DETECTION_SCALE),
                    int(rect.top() / FACE_DETECTION_SCALE),
                    int(rect.right() / FACE_DETECTION_SCALE),
                    int(rect.bottom() / FACE_DETECTION_SCALE)
                ))
        
        # Update tracked faces
        current_sessions = set()
        
        with self.lock:
            for rect in face_rects:
                # Get face encoding
                face_encoding = self._get_face_encoding(rgb_frame, rect)
                
                # Check if matches existing tracked face
                matched_session = self._match_existing_face(rect, face_encoding)
                
                if matched_session:
                    # Update existing face
                    tracked = self.tracked_faces[matched_session]
                    tracked.rect = rect
                    if face_encoding:
                        tracked.encoding = face_encoding
                    tracked.last_seen = time.time()
                    current_sessions.add(matched_session)
                    
                    # Try recognition if not already recognized
                    if not tracked.is_recognized and face_encoding:
                        self._attempt_recognition(tracked)
                else:
                    # Create new tracked face
                    session_id = self._get_next_session_id()
                    new_face = TrackedFace(
                        session_id=session_id,
                        rect=rect,
                        encoding=face_encoding
                    )
                    self.tracked_faces[session_id] = new_face
                    current_sessions.add(session_id)
                    
                    # Try immediate recognition
                    if face_encoding:
                        self._attempt_recognition(new_face)
                    
                    # Notify new face detected
                    if not new_face.is_recognized:
                        broadcast(f"FACE_UNKNOWN:{session_id}")
                        print(f"[*] New face session: {session_id}")
            
            # Check for registration mode
            if self.recording_mode and self.pending_registration_name:
                for session_id, tracked in self.tracked_faces.items():
                    if tracked.encoding and not tracked.is_recognized:
                        self._register_tracked_face(tracked, self.pending_registration_name)
                        self.recording_mode = False
                        self.pending_registration_name = None
                        break
            
            # Remove lost faces
            now = time.time()
            lost_sessions = []
            for session_id, tracked in self.tracked_faces.items():
                if session_id not in current_sessions:
                    if now - tracked.last_seen > FACE_LOSS_TIMEOUT:
                        lost_sessions.append(session_id)
            
            for session_id in lost_sessions:
                tracked = self.tracked_faces[session_id]
                if tracked.is_recognized:
                    broadcast(f"FACE_LEFT:{tracked.identity.id}")
                    print(f"[-] Face left: {tracked.identity.name}")
                else:
                    broadcast(f"FACE_LEFT:{session_id}")
                del self.tracked_faces[session_id]
        
        # Draw results on frame
        self._draw_faces(display_frame)
        
        return display_frame
    
    def _match_existing_face(self, rect: dlib.rectangle, 
                            encoding: Optional[List[float]]) -> Optional[str]:
        """Match face to existing tracked face."""
        center = ((rect.left() + rect.right()) // 2, 
                  (rect.top() + rect.bottom()) // 2)
        
        best_match = None
        best_distance = float('inf')
        
        for session_id, tracked in self.tracked_faces.items():
            tracked_center = tracked.center
            
            # Calculate spatial distance
            spatial_dist = ((center[0] - tracked_center[0]) ** 2 + 
                           (center[1] - tracked_center[1]) ** 2) ** 0.5
            
            if spatial_dist < 100:  # Within 100 pixels
                if encoding and tracked.encoding:
                    # Compare face encodings (Euclidean distance)
                    enc_dist = np.linalg.norm(
                        np.array(encoding) - np.array(tracked.encoding)
                    )
                    if enc_dist < best_distance:
                        best_distance = enc_dist
                        best_match = session_id
                elif spatial_dist < best_distance:
                    best_distance = spatial_dist
                    best_match = session_id
        
        return best_match if best_distance < 100 else None
    
    def _attempt_recognition(self, tracked: TrackedFace):
        """Attempt to recognize a tracked face."""
        if not tracked.encoding:
            return
        
        # Check cooldown
        if tracked.is_recognized:
            if time.time() - tracked.last_seen < RECOGNITION_COOLDOWN:
                return
        
        # Search database
        result = self.database.find_match(tracked.encoding)
        
        if result:
            identity, confidence = result
            tracked.identity = identity
            tracked.confidence = confidence
            
            # Update database stats
            self.database.update_last_seen(identity.id)
            
            # Notify clients
            broadcast(f"FACE_DETECTED:{identity.id}:{identity.name}:{confidence:.2f}")
            print(f"[+] Recognized: {identity.name} (conf={confidence:.2f})")
        else:
            tracked.identity = None
            tracked.confidence = 0.0
    
    def _register_tracked_face(self, tracked: TrackedFace, name: str):
        """Register a tracked face to database."""
        if not tracked.encoding:
            print("[!] No encoding available for registration")
            return False
        
        identity = self.database.register_face(name, tracked.encoding)
        tracked.identity = identity
        tracked.confidence = 1.0
        return True
    
    def start_registration(self, name: str):
        """Start registration mode for next detected face."""
        with self.lock:
            self.recording_mode = True
            self.pending_registration_name = name
        print(f"[*] Registration mode active for: {name}")
    
    def cancel_registration(self):
        """Cancel registration mode."""
        with self.lock:
            self.recording_mode = False
            self.pending_registration_name = None
        print("[*] Registration cancelled")
    
    def _draw_faces(self, frame: np.ndarray):
        """Draw face bounding boxes and info."""
        with self.lock:
            for session_id, tracked in self.tracked_faces.items():
                rect = tracked.rect
                left, top, right, bottom = rect.left(), rect.top(), rect.right(), rect.bottom()
                
                # Determine color based on recognition status
                if tracked.is_recognized:
                    color = (0, 255, 0)  # Green for recognized
                    label = f"{tracked.identity.name} ({tracked.confidence:.2f})"
                else:
                    if self.use_opencv_fallback:
                        color = (0, 165, 255)  # Orange for unknown (fallback mode)
                        label = f"Unknown (fallback)"
                    else:
                        color = (0, 165, 255)  # Orange for unknown
                        label = f"Unknown #{session_id[-4:]}"
                
                # Draw bounding box
                cv2.rectangle(frame, (left, top), (right, bottom), color, 2)
                
                # Draw label background
                (text_w, text_h), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.6, 2)
                cv2.rectangle(frame, (left, top - text_h - 10), (left + text_w, top), color, -1)
                
                # Draw label text
                cv2.putText(frame, label, (left, top - 5),
                           cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 2)
                
                # Draw registration indicator
                if self.recording_mode and not tracked.is_recognized:
                    cv2.putText(frame, "[REGISTERING...]", (left, bottom + 20),
                               cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 0), 2)
        
        # Draw status info
        mode_text = "Fallback" if self.use_opencv_fallback else "dlib"
        status_text = f"Mode: {mode_text} | Tracked: {len(self.tracked_faces)} | Registered: {len(self.database.identities)}"
        if self.recording_mode:
            status_text += f" | REGISTERING: {self.pending_registration_name}"
        
        cv2.putText(frame, status_text, (10, 30),
                   cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
        
        # Draw controls
        controls = "r:Register  d:Delete  l:List  s:Save  q:Quit"
        cv2.putText(frame, controls, (10, frame.shape[0] - 10),
                   cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 200), 1)
    
    def list_faces(self):
        """Print list of registered faces."""
        faces = self.database.get_all_faces()
        if not faces:
            print("[*] No registered faces")
            return
        
        print("\n[*] Registered Faces:")
        print("-" * 60)
        for face in faces:
            last_seen = time.strftime('%Y-%m-%d %H:%M:%S', 
                          time.localtime(face.last_seen)) if face.last_seen else "Never"
            print(f"  ID: {face.id}")
            print(f"  Name: {face.name}")
            print(f"  Created: {face.created_at}")
            print(f"  Last Seen: {last_seen}")
            print(f"  Recognitions: {face.recognition_count}")
            print("-" * 60)
    
    def save_database(self):
        """Save database to disk."""
        self.database.save()
    
    def delete_face_interactive(self):
        """Interactive face deletion."""
        faces = self.database.get_all_faces()
        if not faces:
            print("[!] No faces to delete")
            return
        
        print("\n[*] Select face to delete:")
        for i, face in enumerate(faces):
            print(f"  {i+1}. {face.name} (ID: {face.id})")
        
        try:
            choice = input("Enter number (or 0 to cancel): ").strip()
            idx = int(choice) - 1
            if 0 <= idx < len(faces):
                self.database.delete_face(faces[idx].id)
            else:
                print("[*] Cancelled")
        except ValueError:
            print("[!] Invalid input")


# ───────────────────────────────────────────────────────────────────────────────
# Main Entry Point
# ───────────────────────────────────────────────────────────────────────────────

def main():
    """Main function."""
    print("=" * 70)
    print("Face Identification Scenario for AR")
    print("=" * 70)
    print("\nWhat it does:")
    print("  • Detects faces in real-time from camera feed")
    print("  • Recognizes registered users by face matching (128-d encoding)")
    print("  • Assigns persistent identity to detected faces")
    print("  • Communicates face events to AR clients via TCP port 5003")
    print("\nBenefits for AR:")
    print("  [OK] Personalized content based on user identity")
    print("  [OK] Secure access control for sensitive scenarios")
    print("  [OK] Multi-user support (different content per user)")
    print("  [OK] User attention and engagement tracking")
    print("  [OK] Attendance logging for educational AR apps")
    print("=" * 70)
    
    # Start TCP server in background thread
    tcp_thread = threading.Thread(target=start_tcp_server, daemon=True)
    tcp_thread.start()
    
    # Initialize face recognition engine
    engine = FaceRecognitionEngine()
    
    # Camera setup
    cap = cv2.VideoCapture(CAMERA_INDEX)
    if not cap.isOpened():
        cap = cv2.VideoCapture(1)
    if not cap.isOpened():
        print("[!] Error: Could not open camera")
        return
    
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    
    print("\n[*] Face Identifier started")
    print("[*] Controls:")
    print("    r - Register new face")
    print("    d - Delete registered face")
    print("    l - List registered faces")
    print("    s - Save database")
    print("    q - Quit")
    
    try:
        while cap.isOpened():
            ret, frame = cap.read()
            if not ret:
                break
            
            # Mirror frame for natural interaction
            frame = cv2.flip(frame, 1)
            
            # Process frame
            display = engine.process_frame(frame)
            
            # Show result
            cv2.imshow("Face Identification - AR Scenario", display)
            
            # Handle keyboard input
            key = cv2.waitKey(5) & 0xFF
            
            if key == ord('q'):
                break
            
            elif key == ord('r'):
                if not engine.recording_mode:
                    name = input("\nEnter name for new face: ").strip()
                    if name:
                        engine.start_registration(name)
                else:
                    engine.cancel_registration()
            
            elif key == ord('d'):
                engine.delete_face_interactive()
            
            elif key == ord('l'):
                engine.list_faces()
            
            elif key == ord('s'):
                engine.save_database()
    
    finally:
        cap.release()
        cv2.destroyAllWindows()
        engine.save_database()
        print("\n[*] Face identifier stopped.")


if __name__ == "__main__":
    main()
