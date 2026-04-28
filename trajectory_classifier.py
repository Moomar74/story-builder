"""
Trajectory Classifier - $1 Recognizer for AR Scenario
====================================================
Classifies trajectories from three sources in an AR scenario:
  1. SKELETON - Human body/limb movement trajectories (hand, wrist, head)
  2. OBJECT - Physical object trajectories (tracked via YOLO/object detection)
  3. LASER - Laser pointer trajectories (laser dot movement)

Uses the $1 Unistroke Recognizer (DollarPy) for gesture classification.

Messages sent to client:
  TRAJECTORY:<type>:<gesture>:<confidence>  - Trajectory recognized
  TRAJECTORY_START:<type>:<id>               - Trajectory recording started
  TRAJECTORY_END:<type>:<id>:<result>        - Trajectory recording ended

Scenario Types:
  - skeleton: Uses MediaPipe pose landmarks for body tracking
  - object: Uses YOLO object detection + tracking (CSRT/KCF)
  - laser: Uses color/blob detection for laser pointer tracking

Controls:
  '1' - Toggle skeleton trajectory tracking
  '2' - Toggle object trajectory tracking  
  '3' - Toggle laser trajectory tracking
  'r' - Record new template for current mode
  's' - Save recorded template
  'q' - Quit
"""

import json
import math
import os
import pickle
import socket
import threading
import time
from enum import Enum
from dataclasses import dataclass, field
from typing import List, Dict, Tuple, Optional
from collections import deque

import cv2
import numpy as np
from dollarpy import Recognizer, Template, Point

# MediaPipe for skeleton tracking
import mediapipe as mp
from mediapipe.tasks.python import vision, core

BaseOptions = core.base_options.BaseOptions
HolisticLandmarker = vision.HolisticLandmarker
HolisticLandmarkerOptions = vision.HolisticLandmarkerOptions
VisionRunningMode = vision.RunningMode
mp_drawing = vision.drawing_utils
mp_drawing_styles = vision.drawing_styles

# ───────────────────────────────────────────────────────────────────────────────
# Model Download Utility
# ───────────────────────────────────────────────────────────────────────────────

MODEL_URL = "https://storage.googleapis.com/mediapipe-models/holistic_landmarker/holistic_landmarker/float16/latest/holistic_landmarker.task"
MODEL_FILENAME = "holistic_landmarker.task"


def download_model():
    """Download the MediaPipe Holistic Landmarker model if not present."""
    if os.path.exists(MODEL_FILENAME):
        return True
    
    print(f"[*] Downloading {MODEL_FILENAME}...")
    try:
        import urllib.request
        urllib.request.urlretrieve(MODEL_URL, MODEL_FILENAME)
        print(f"[+] Downloaded {MODEL_FILENAME}")
        return True
    except Exception as e:
        print(f"[!] Failed to download model: {e}")
        print("[!] Please download manually from:")
        print(f"    {MODEL_URL}")
        return False


# ───────────────────────────────────────────────────────────────────────────────
# Configuration & Constants
# ───────────────────────────────────────────────────────────────────────────────

TCP_PORT = 5002  # Separate port for trajectory classifier
CAMERA_INDEX = 1

# Trajectory types
class TrajectoryType(Enum):
    SKELETON = "skeleton"
    OBJECT = "object"
    LASER = "laser"

# Tracking configuration
TRAJECTORY_BUFFER_SIZE = 60  # Number of frames to keep
MIN_TRAJECTORY_POINTS = 15   # Minimum points for recognition
RECOGNITION_INTERVAL = 10    # Check every N frames
CONFIDENCE_THRESHOLD = 0.50  # Minimum confidence for recognition

# Laser detection HSV range (red laser)
LASER_HSV_LOWER = np.array([0, 100, 200])
LASER_HSV_UPPER = np.array([10, 255, 255])

# Object tracking
OBJECT_CONFIDENCE_THRESHOLD = 0.5


# ───────────────────────────────────────────────────────────────────────────────
# Data Classes
# ───────────────────────────────────────────────────────────────────────────────

@dataclass
class Trajectory:
    """Represents a single trajectory with metadata."""
    traj_type: TrajectoryType
    points: List[Point] = field(default_factory=list)
    start_time: float = field(default_factory=time.time)
    id: str = field(default_factory=lambda: str(int(time.time() * 1000))[-6:])
    is_recording: bool = False
    
    def add_point(self, x: float, y: float, stroke_id: int = 1):
        """Add a normalized point (0-1 range)."""
        self.points.append(Point(x, y, stroke_id))
        # Keep buffer size limited
        if len(self.points) > TRAJECTORY_BUFFER_SIZE:
            self.points.pop(0)
    
    def clear(self):
        """Clear all points."""
        self.points.clear()
        self.start_time = time.time()
        self.id = str(int(time.time() * 1000))[-6:]
    
    def get_path_length(self) -> float:
        """Calculate total path length."""
        total = 0.0
        for i in range(1, len(self.points)):
            dx = self.points[i].x - self.points[i-1].x
            dy = self.points[i].y - self.points[i-1].y
            total += math.hypot(dx, dy)
        return total
    
    def get_duration(self) -> float:
        """Get trajectory duration in seconds."""
        return time.time() - self.start_time


@dataclass
class TrackedObject:
    """Represents a tracked physical object."""
    id: int
    bbox: Tuple[int, int, int, int]  # x, y, w, h
    center: Tuple[float, float]  # normalized center
    confidence: float
    class_name: str
    tracker = None
    trajectory: Trajectory = field(default_factory=lambda: Trajectory(TrajectoryType.OBJECT))
    last_seen: float = field(default_factory=time.time)


# ───────────────────────────────────────────────────────────────────────────────
# TCP Server for Client Communication
# ───────────────────────────────────────────────────────────────────────────────

connected_clients: List[socket.socket] = []


def broadcast(message: str):
    """Send message to all connected clients."""
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
    print(f"[+] Trajectory client connected: {addr}")
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
        print(f"[-] Trajectory client disconnected: {addr}")


def start_tcp_server():
    """Start TCP server for client communication."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", TCP_PORT))
    server.listen(5)
    print(f"[*] Trajectory TCP Server on port {TCP_PORT}")
    while True:
        conn, addr = server.accept()
        t = threading.Thread(target=handle_client, args=(conn, addr), daemon=True)
        t.start()


# ───────────────────────────────────────────────────────────────────────────────
# $1 Recognizer Templates
# ───────────────────────────────────────────────────────────────────────────────

TEMPLATES_FILE = "trajectory_templates.pkl"


def load_templates() -> List[Template]:
    """Load saved templates from disk."""
    if os.path.exists(TEMPLATES_FILE):
        with open(TEMPLATES_FILE, 'rb') as f:
            return pickle.load(f)
    return []


def save_templates(templates: List[Template]):
    """Save templates to disk."""
    with open(TEMPLATES_FILE, 'wb') as f:
        pickle.dump(templates, f)


def generate_templates_for_type(traj_type: TrajectoryType) -> List[Template]:
    """Generate default templates for a trajectory type."""
    templates = []
    
    # Common gestures for all types
    def circle(n=64):
        pts = []
        for i in range(n):
            angle = 2 * math.pi * i / n
            x = 0.5 + 0.3 * math.cos(angle)
            y = 0.5 + 0.3 * math.sin(angle)
            pts.append(Point(x, y, 1))
        return pts
    
    def swipe_right(n=32):
        pts = []
        for i in range(n):
            x = 0.2 + 0.6 * (i / n)
            y = 0.5 + 0.02 * math.sin(i * 0.3)
            pts.append(Point(x, y, 1))
        return pts
    
    def swipe_left(n=32):
        pts = []
        for i in range(n):
            x = 0.8 - 0.6 * (i / n)
            y = 0.5 + 0.02 * math.sin(i * 0.3)
            pts.append(Point(x, y, 1))
        return pts
    
    def swipe_up(n=32):
        pts = []
        for i in range(n):
            x = 0.5 + 0.02 * math.sin(i * 0.3)
            y = 0.8 - 0.6 * (i / n)
            pts.append(Point(x, y, 1))
        return pts
    
    def swipe_down(n=32):
        pts = []
        for i in range(n):
            x = 0.5 + 0.02 * math.sin(i * 0.3)
            y = 0.2 + 0.6 * (i / n)
            pts.append(Point(x, y, 1))
        return pts
    
    def wave(n=64):
        pts = []
        for i in range(n):
            x = 0.3 + 0.4 * abs(math.sin(3 * math.pi * i / n))
            y = 0.4 + 0.1 * math.sin(6 * math.pi * i / n)
            pts.append(Point(x, y, 1))
        return pts
    
    def zigzag(n=48):
        pts = []
        for i in range(n):
            t = i / n
            x = 0.2 + 0.6 * t
            y = 0.5 + 0.2 * (1 if int(t * 8) % 2 == 0 else -1) * math.sin(t * 4 * math.pi)
            pts.append(Point(x, y, 1))
        return pts
    
    def line_horizontal(n=32):
        pts = []
        for i in range(n):
            x = 0.2 + 0.6 * (i / n)
            y = 0.5
            pts.append(Point(x, y, 1))
        return pts
    
    def line_vertical(n=32):
        pts = []
        for i in range(n):
            x = 0.5
            y = 0.2 + 0.6 * (i / n)
            pts.append(Point(x, y, 1))
        return pts
    
    def diagonal_down(n=32):
        pts = []
        for i in range(n):
            x = 0.2 + 0.6 * (i / n)
            y = 0.2 + 0.6 * (i / n)
            pts.append(Point(x, y, 1))
        return pts
    
    def diagonal_up(n=32):
        pts = []
        for i in range(n):
            x = 0.2 + 0.6 * (i / n)
            y = 0.8 - 0.6 * (i / n)
            pts.append(Point(x, y, 1))
        return pts
    
    def spiral(n=64):
        pts = []
        for i in range(n):
            t = i / n
            r = 0.3 * t
            angle = 4 * math.pi * t
            x = 0.5 + r * math.cos(angle)
            y = 0.5 + r * math.sin(angle)
            pts.append(Point(x, y, 1))
        return pts
    
    # Type-specific templates
    if traj_type == TrajectoryType.SKELETON:
        # Skeleton-specific: punch, grab, push
        def punch(n=24):
            pts = []
            for i in range(n):
                t = i / n
                x = 0.3 + 0.4 * t
                y = 0.5
                pts.append(Point(x, y, 1))
            return pts
        
        def grab(n=32):
            pts = []
            for i in range(n):
                t = i / n
                r = 0.2 * (1 - t)
                angle = 3 * math.pi * t
                x = 0.5 + r * math.cos(angle)
                y = 0.5 + r * math.sin(angle)
                pts.append(Point(x, y, 1))
            return pts
        
        templates = [
            Template("circle", circle()),
            Template("swipe_right", swipe_right()),
            Template("swipe_left", swipe_left()),
            Template("swipe_up", swipe_up()),
            Template("swipe_down", swipe_down()),
            Template("wave", wave()),
            Template("zigzag", zigzag()),
            Template("punch", punch()),
            Template("grab", grab()),
            Template("spiral", spiral()),
        ]
    
    elif traj_type == TrajectoryType.OBJECT:
        # Object-specific: slide, toss, roll
        def toss_arc(n=48):
            pts = []
            for i in range(n):
                t = i / n
                x = 0.2 + 0.6 * t
                y = 0.7 - 0.5 * math.sin(math.pi * t)
                pts.append(Point(x, y, 1))
            return pts
        
        def roll(n=64):
            pts = []
            for i in range(n):
                angle = 2 * math.pi * i / n
                x = 0.5 + 0.3 * math.cos(angle)
                y = 0.5 + 0.15 * math.sin(angle)
                pts.append(Point(x, y, 1))
            return pts
        
        def figure_eight(n=64):
            pts = []
            for i in range(n):
                t = i / n
                x = 0.5 + 0.3 * math.sin(2 * math.pi * t)
                y = 0.5 + 0.2 * math.sin(4 * math.pi * t)
                pts.append(Point(x, y, 1))
            return pts
        
        templates = [
            Template("circle", circle()),
            Template("swipe_right", swipe_right()),
            Template("swipe_left", swipe_left()),
            Template("swipe_up", swipe_up()),
            Template("swipe_down", swipe_down()),
            Template("line_horizontal", line_horizontal()),
            Template("line_vertical", line_vertical()),
            Template("diagonal_down", diagonal_down()),
            Template("diagonal_up", diagonal_up()),
            Template("toss_arc", toss_arc()),
            Template("roll", roll()),
            Template("figure_eight", figure_eight()),
        ]
    
    elif traj_type == TrajectoryType.LASER:
        # Laser-specific: precise pointing patterns
        def rectangle(n=64):
            pts = []
            sides = [(0.3, 0.3), (0.7, 0.3), (0.7, 0.7), (0.3, 0.7)]
            pts_per_side = n // 4
            for (x1, y1), (x2, y2) in zip(sides, sides[1:] + [sides[0]]):
                for j in range(pts_per_side):
                    t = j / pts_per_side
                    x = x1 + (x2 - x1) * t
                    y = y1 + (y2 - y1) * t
                    pts.append(Point(x, y, 1))
            return pts
        
        def triangle(n=48):
            pts = []
            vertices = [(0.5, 0.2), (0.8, 0.7), (0.2, 0.7)]
            pts_per_side = n // 3
            for (x1, y1), (x2, y2) in zip(vertices, vertices[1:] + [vertices[0]]):
                for j in range(pts_per_side):
                    t = j / pts_per_side
                    x = x1 + (x2 - x1) * t
                    y = y1 + (y2 - y1) * t
                    pts.append(Point(x, y, 1))
            return pts
        
        def cross(n=40):
            pts = []
            # Horizontal line
            for i in range(20):
                x = 0.2 + 0.6 * (i / 20)
                pts.append(Point(x, 0.5, 1))
            # Vertical line
            for i in range(20):
                y = 0.2 + 0.6 * (i / 20)
                pts.append(Point(0.5, y, 1))
            return pts
        
        def check_mark(n=24):
            pts = []
            for i in range(12):
                x = 0.3 + 0.2 * (i / 12)
                y = 0.5 + 0.3 * (i / 12)
                pts.append(Point(x, y, 1))
            for i in range(12):
                x = 0.5 + 0.3 * (i / 12)
                y = 0.8 - 0.4 * (i / 12)
                pts.append(Point(x, y, 1))
            return pts
        
        def infinity(n=64):
            pts = []
            for i in range(n):
                t = i / n
                x = 0.5 + 0.3 * math.sin(2 * math.pi * t)
                y = 0.5 + 0.2 * math.sin(4 * math.pi * t)
                pts.append(Point(x, y, 1))
            return pts
        
        templates = [
            Template("circle", circle()),
            Template("rectangle", rectangle()),
            Template("triangle", triangle()),
            Template("cross", cross()),
            Template("check_mark", check_mark()),
            Template("infinity", infinity()),
            Template("line_horizontal", line_horizontal()),
            Template("line_vertical", line_vertical()),
            Template("spiral", spiral()),
            Template("zigzag", zigzag()),
        ]
    
    return templates


# ───────────────────────────────────────────────────────────────────────────────
# Detection & Tracking Classes
# ───────────────────────────────────────────────────────────────────────────────

class SkeletonTracker:
    """Tracks skeleton joints using MediaPipe Holistic."""
    
    def __init__(self):
        self.landmarker = self._create_landmarker()
        self.target_joint = "rw"  # right wrist
        self.joint_indices = {
            "rw": 16,  # right wrist
            "lw": 15,  # left wrist
            "rs": 12,  # right shoulder
            "ls": 11,  # left shoulder
            "re": 14,  # right elbow
            "le": 13,  # left elbow
            "n": 0,    # nose
        }
        self.ema_x = 0.5
        self.ema_y = 0.5
        self.ema_alpha = 0.3
    
    def _create_landmarker(self):
        download_model()  # Ensure model is available
        options = HolisticLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=MODEL_FILENAME),
            running_mode=VisionRunningMode.IMAGE,
            min_face_detection_confidence=0.5,
            min_pose_detection_confidence=0.5,
            min_hand_landmarks_confidence=0.5
        )
        return HolisticLandmarker.create_from_options(options)
    
    def detect(self, frame: np.ndarray) -> Optional[Tuple[float, float, Dict]]:
        """Detect skeleton and return joint position."""
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
        results = self.landmarker.detect(mp_image)
        
        if not results or not results.pose_landmarks:
            return None
        
        lm = results.pose_landmarks
        joint_idx = self.joint_indices.get(self.target_joint, 16)
        
        if joint_idx >= len(lm):
            return None
        
        # Get raw position
        raw_x, raw_y = lm[joint_idx].x, lm[joint_idx].y
        
        # Apply EMA smoothing
        self.ema_x = self.ema_alpha * raw_x + (1 - self.ema_alpha) * self.ema_x
        self.ema_y = self.ema_alpha * raw_y + (1 - self.ema_alpha) * self.ema_y
        
        # Collect skeleton data
        skeleton_data = {
            "ls": [round(lm[11].x, 3), round(lm[11].y, 3)],
            "rs": [round(lm[12].x, 3), round(lm[12].y, 3)],
            "le": [round(lm[13].x, 3), round(lm[13].y, 3)],
            "re": [round(lm[14].x, 3), round(lm[14].y, 3)],
            "lw": [round(lm[15].x, 3), round(lm[15].y, 3)],
            "rw": [round(self.ema_x, 3), round(self.ema_y, 3)],
            "lh": [round(lm[23].x, 3), round(lm[23].y, 3)],
            "rh": [round(lm[24].x, 3), round(lm[24].y, 3)],
            "n": [round(lm[0].x, 3), round(lm[0].y, 3)],
        }
        
        return (self.ema_x, self.ema_y, skeleton_data)
    
    def draw(self, frame: np.ndarray, results):
        """Draw skeleton landmarks on frame."""
        pass  # Drawing handled in main loop


class ObjectTracker:
    """Tracks physical objects using OpenCV trackers."""
    
    def __init__(self):
        self.tracked_objects: Dict[int, TrackedObject] = {}
        self.next_id = 0
        self.detection_interval = 30  # Run detection every N frames
        self.frame_count = 0
    
    def detect_and_track(self, frame: np.ndarray) -> List[TrackedObject]:
        """Detect objects and update trackers."""
        self.frame_count += 1
        h, w = frame.shape[:2]
        
        # Simple blob detection for demonstration
        # In production, integrate with YOLO
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        blurred = cv2.GaussianBlur(gray, (5, 5), 0)
        _, thresh = cv2.threshold(blurred, 100, 255, cv2.THRESH_BINARY_INV)
        
        contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        current_objects = []
        for cnt in contours:
            area = cv2.contourArea(cnt)
            if area < 500 or area > 50000:
                continue
            
            x, y, bw, bh = cv2.boundingRect(cnt)
            cx = (x + bw / 2) / w
            cy = (y + bh / 2) / h
            
            # Check if matches existing tracked object
            matched = False
            for obj_id, obj in list(self.tracked_objects.items()):
                ox, oy = obj.center
                distance = math.hypot(cx - ox, cy - oy)
                if distance < 0.1:  # Threshold for matching
                    obj.center = (cx, cy)
                    obj.bbox = (x, y, bw, bh)
                    obj.last_seen = time.time()
                    obj.trajectory.add_point(cx, cy)
                    current_objects.append(obj)
                    matched = True
                    break
            
            if not matched and len(self.tracked_objects) < 5:
                # Create new tracked object
                new_obj = TrackedObject(
                    id=self.next_id,
                    bbox=(x, y, bw, bh),
                    center=(cx, cy),
                    confidence=0.8,
                    class_name="object"
                )
                new_obj.trajectory = Trajectory(TrajectoryType.OBJECT)
                new_obj.trajectory.add_point(cx, cy)
                self.tracked_objects[self.next_id] = new_obj
                current_objects.append(new_obj)
                self.next_id += 1
        
        # Remove stale objects
        now = time.time()
        stale_ids = [oid for oid, obj in self.tracked_objects.items() if now - obj.last_seen > 2.0]
        for oid in stale_ids:
            del self.tracked_objects[oid]
        
        return current_objects


class LaserTracker:
    """Tracks laser pointer using color detection."""
    
    def __init__(self):
        self.position: Optional[Tuple[float, float]] = None
        self.trajectory = Trajectory(TrajectoryType.LASER)
        self.hsv_lower = LASER_HSV_LOWER
        self.hsv_upper = LASER_HSV_UPPER
        self.min_contour_area = 10
        self.max_contour_area = 500
    
    def detect(self, frame: np.ndarray) -> Optional[Tuple[float, float]]:
        """Detect laser position in frame."""
        h, w = frame.shape[:2]
        
        # Convert to HSV for color detection
        hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
        
        # Create mask for laser color
        mask = cv2.inRange(hsv, self.hsv_lower, self.hsv_upper)
        
        # Morphological operations to clean up
        kernel = np.ones((3, 3), np.uint8)
        mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
        
        # Find contours
        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        if not contours:
            self.position = None
            return None
        
        # Find largest contour (laser dot)
        largest = max(contours, key=cv2.contourArea)
        area = cv2.contourArea(largest)
        
        if area < self.min_contour_area or area > self.max_contour_area:
            self.position = None
            return None
        
        # Calculate center
        M = cv2.moments(largest)
        if M["m00"] == 0:
            self.position = None
            return None
        
        cx = int(M["m10"] / M["m00"])
        cy = int(M["m01"] / M["m00"])
        
        # Normalize
        norm_x = cx / w
        norm_y = cy / h
        
        self.position = (norm_x, norm_y)
        self.trajectory.add_point(norm_x, norm_y)
        
        return self.position
    
    def draw_mask(self, frame: np.ndarray) -> np.ndarray:
        """Draw laser detection mask overlay."""
        hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
        mask = cv2.inRange(hsv, self.hsv_lower, self.hsv_upper)
        
        # Create colored overlay
        overlay = np.zeros_like(frame)
        overlay[mask > 0] = (0, 0, 255)  # Red for laser
        
        return overlay


# ───────────────────────────────────────────────────────────────────────────────
# Main Trajectory Classifier
# ───────────────────────────────────────────────────────────────────────────────

class TrajectoryClassifier:
    """Main class for trajectory classification in AR scenario."""
    
    def __init__(self):
        self.skeleton_tracker = SkeletonTracker()
        self.object_tracker = ObjectTracker()
        self.laser_tracker = LaserTracker()
        
        # Trackers for each type
        self.skeleton_trajectory = Trajectory(TrajectoryType.SKELETON)
        
        # Recognition state
        self.recognizers: Dict[TrajectoryType, Recognizer] = {}
        self.saved_templates: Dict[TrajectoryType, List[Template]] = {}
        self.active_types: Dict[TrajectoryType, bool] = {
            TrajectoryType.SKELETON: True,
            TrajectoryType.OBJECT: False,
            TrajectoryType.LASER: False,
        }
        
        # Template recording
        self.recording = False
        self.record_type: Optional[TrajectoryType] = None
        self.record_label = ""
        self.record_points: List[Point] = []
        
        # Cooldown for recognition
        self.last_recognition_time: Dict[TrajectoryType, float] = {
            TrajectoryType.SKELETON: 0,
            TrajectoryType.OBJECT: 0,
            TrajectoryType.LASER: 0,
        }
        self.recognition_cooldown = 1.5
        
        self._load_recognizers()
    
    def _load_recognizers(self):
        """Load or create recognizers for each trajectory type."""
        for traj_type in TrajectoryType:
            default = generate_templates_for_type(traj_type)
            saved = load_templates()
            # Filter saved templates by type
            type_saved = [t for t in saved if hasattr(t, '_type') and t._type == traj_type.value]
            all_templates = default + type_saved
            self.recognizers[traj_type] = Recognizer(all_templates)
            self.saved_templates[traj_type] = type_saved
            print(f"[*] Loaded {len(all_templates)} templates for {traj_type.value}")
    
    def recognize_trajectory(self, traj_type: TrajectoryType, trajectory: Trajectory) -> Optional[Tuple[str, float]]:
        """Recognize a trajectory using $1 recognizer."""
        if len(trajectory.points) < MIN_TRAJECTORY_POINTS:
            return None
        
        now = time.time()
        if now - self.last_recognition_time[traj_type] < self.recognition_cooldown:
            return None
        
        # Check path length
        path_len = trajectory.get_path_length()
        if path_len < 0.08:  # Minimum movement threshold
            return None
        
        try:
            recognizer = self.recognizers[traj_type]
            result = recognizer.recognize(trajectory.points)
            
            if result and result[1] is not None and result[1] > CONFIDENCE_THRESHOLD:
                gesture_name = result[0]
                confidence = result[1]
                self.last_recognition_time[traj_type] = now
                return (gesture_name, confidence)
        except Exception as e:
            print(f"[!] Recognition error: {e}")
        
        return None
    
    def start_recording(self, traj_type: TrajectoryType, label: str):
        """Start recording a new template."""
        self.recording = True
        self.record_type = traj_type
        self.record_label = label
        self.record_points = []
        print(f"[*] Recording template for '{label}' ({traj_type.value})")
    
    def stop_recording(self) -> bool:
        """Stop recording and save template."""
        if not self.recording or len(self.record_points) < 10:
            print("[!] Not enough points to save template")
            self.recording = False
            return False
        
        # Create and save template
        new_template = Template(self.record_label, self.record_points)
        new_template._type = self.record_type.value  # Mark type
        
        self.saved_templates[self.record_type].append(new_template)
        
        # Rebuild recognizer
        default = generate_templates_for_type(self.record_type)
        all_templates = default + self.saved_templates[self.record_type]
        self.recognizers[self.record_type] = Recognizer(all_templates)
        
        # Save to disk
        all_saved = []
        for t in TrajectoryType:
            all_saved.extend(self.saved_templates[t])
        save_templates(all_saved)
        
        print(f"[+] Saved template '{self.record_label}' ({len(self.record_points)} points)")
        self.recording = False
        return True
    
    def process_frame(self, frame: np.ndarray, frame_count: int) -> np.ndarray:
        """Process a single frame and return annotated frame."""
        h, w = frame.shape[:2]
        
        # ── Skeleton Tracking ──
        if self.active_types[TrajectoryType.SKELETON]:
            result = self.skeleton_tracker.detect(frame)
            if result:
                x, y, skeleton_data = result
                
                # Draw skeleton (simplified)
                for key, pos in skeleton_data.items():
                    px, py = int(pos[0] * w), int(pos[1] * h)
                    color = (0, 255, 0) if key == "rw" else (255, 0, 0)
                    cv2.circle(frame, (px, py), 5, color, -1)
                
                # Add to trajectory
                if not self.recording or self.record_type == TrajectoryType.SKELETON:
                    self.skeleton_trajectory.add_point(x, y)
                
                if self.recording and self.record_type == TrajectoryType.SKELETON:
                    self.record_points.append(Point(x, y, 1))
                
                # Try recognition
                if frame_count % RECOGNITION_INTERVAL == 0 and not self.recording:
                    result = self.recognize_trajectory(TrajectoryType.SKELETON, self.skeleton_trajectory)
                    if result:
                        gesture, conf = result
                        broadcast(f"TRAJECTORY:skeleton:{gesture}:{conf:.2f}")
                        print(f"[SKELETON] Recognized: {gesture} (conf={conf:.2f})")
                        self.skeleton_trajectory.clear()
        
        # ── Object Tracking ──
        if self.active_types[TrajectoryType.OBJECT]:
            objects = self.object_tracker.detect_and_track(frame)
            for obj in objects:
                x, y = obj.center
                bx, by, bw, bh = obj.bbox
                
                # Draw bounding box
                cv2.rectangle(frame, (bx, by), (bx + bw, by + bh), (255, 165, 0), 2)
                cv2.putText(frame, f"Obj {obj.id}", (bx, by - 5),
                           cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 165, 0), 1)
                
                # Draw trajectory
                pts = obj.trajectory.points
                for i in range(1, len(pts)):
                    x1, y1 = int(pts[i-1].x * w), int(pts[i-1].y * h)
                    x2, y2 = int(pts[i].x * w), int(pts[i].y * h)
                    cv2.line(frame, (x1, y1), (x2, y2), (0, 255, 255), 2)
                
                # Try recognition
                if frame_count % RECOGNITION_INTERVAL == 0:
                    result = self.recognize_trajectory(TrajectoryType.OBJECT, obj.trajectory)
                    if result:
                        gesture, conf = result
                        broadcast(f"TRAJECTORY:object:{obj.id}:{gesture}:{conf:.2f}")
                        print(f"[OBJECT {obj.id}] Recognized: {gesture} (conf={conf:.2f})")
                        obj.trajectory.clear()
        
        # ── Laser Tracking ──
        if self.active_types[TrajectoryType.LASER]:
            pos = self.laser_tracker.detect(frame)
            
            # Draw laser overlay
            laser_overlay = self.laser_tracker.draw_mask(frame)
            frame = cv2.addWeighted(frame, 1.0, laser_overlay, 0.5, 0)
            
            if pos:
                lx, ly = pos
                px, py = int(lx * w), int(ly * h)
                
                # Draw laser point
                cv2.circle(frame, (px, py), 10, (0, 0, 255), -1)
                cv2.circle(frame, (px, py), 12, (255, 255, 255), 2)
                
                if not self.recording or self.record_type == TrajectoryType.LASER:
                    self.laser_tracker.trajectory.add_point(lx, ly)
                
                if self.recording and self.record_type == TrajectoryType.LASER:
                    self.record_points.append(Point(lx, ly, 1))
                
                # Draw trajectory
                pts = self.laser_tracker.trajectory.points
                for i in range(1, len(pts)):
                    x1, y1 = int(pts[i-1].x * w), int(pts[i-1].y * h)
                    x2, y2 = int(pts[i].x * w), int(pts[i].y * h)
                    cv2.line(frame, (x1, y1), (x2, y2), (0, 0, 255), 2)
                
                # Try recognition
                if frame_count % RECOGNITION_INTERVAL == 0 and not self.recording:
                    result = self.recognize_trajectory(TrajectoryType.LASER, self.laser_tracker.trajectory)
                    if result:
                        gesture, conf = result
                        broadcast(f"TRAJECTORY:laser:{gesture}:{conf:.2f}")
                        print(f"[LASER] Recognized: {gesture} (conf={conf:.2f})")
                        self.laser_tracker.trajectory.clear()
            else:
                # No laser detected - optionally clear trajectory after delay
                pass
        
        # ── Recording UI ──
        if self.recording:
            # Recording indicator
            cv2.circle(frame, (30, 30), 15, (0, 0, 255), -1)
            cv2.putText(frame, f"REC: {self.record_label} ({len(self.record_points)} pts)",
                       (55, 38), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)
            
            # Draw recorded trajectory
            for i in range(1, len(self.record_points)):
                x1 = int(self.record_points[i-1].x * w)
                y1 = int(self.record_points[i-1].y * h)
                x2 = int(self.record_points[i].x * w)
                y2 = int(self.record_points[i].y * h)
                cv2.line(frame, (x1, y1), (x2, y2), (0, 0, 255), 3)
        
        # ── Status HUD ──
        self._draw_hud(frame)
        
        return frame
    
    def _draw_hud(self, frame: np.ndarray):
        """Draw heads-up display."""
        h, w = frame.shape[:2]
        
        # Status panel background
        panel_w = 280
        panel_h = 120
        overlay = frame.copy()
        cv2.rectangle(overlay, (10, 10), (10 + panel_w, 10 + panel_h), (0, 0, 0), -1)
        frame[:] = cv2.addWeighted(overlay, 0.7, frame, 0.3, 0)
        
        # Title
        cv2.putText(frame, "Trajectory Classifier ($1)", (15, 35),
                   cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
        
        # Active types
        y_offset = 60
        for i, (traj_type, active) in enumerate(self.active_types.items()):
            color = (0, 255, 0) if active else (100, 100, 100)
            symbol = "ON" if active else "OFF"
            cv2.putText(frame, f"[{i+1}] {traj_type.value.upper()}: {symbol}",
                       (15, y_offset + i * 22), cv2.FONT_HERSHEY_SIMPLEX,
                       0.5, color, 1)
        
        # Clients count
        cv2.putText(frame, f"Clients: {len(connected_clients)}",
                   (w - 150, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 200), 1)
        
        # Controls hint
        cv2.putText(frame, "1/2/3: Toggle  |  R: Record  |  S: Save  |  Q: Quit",
                   (10, h - 20), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 200), 1)


# ───────────────────────────────────────────────────────────────────────────────
# Main Loop
# ───────────────────────────────────────────────────────────────────────────────

def main():
    # Download model if needed
    if not download_model():
        print("[!] Cannot proceed without model file")
        return
    
    # Start TCP server
    tcp_thread = threading.Thread(target=start_tcp_server, daemon=True)
    tcp_thread.start()
    
    # Initialize classifier
    classifier = TrajectoryClassifier()
    
    # Camera setup - try camera 0 first, then fall back to CAMERA_INDEX
    cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        cap = cv2.VideoCapture(CAMERA_INDEX)
    if not cap.isOpened():
        print("[!] Error: Could not open camera (tried index 0 and 1)")
        print("[*] Please check your camera connection and try again.")
        return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    
    print("[*] Trajectory Classifier started")
    print("[*] Controls:")
    print("    1 - Toggle skeleton tracking")
    print("    2 - Toggle object tracking")
    print("    3 - Toggle laser tracking")
    print("    r - Record new template")
    print("    s - Save recorded template")
    print("    q - Quit")
    
    frame_count = 0
    
    try:
        while cap.isOpened():
            ret, frame = cap.read()
            if not ret:
                break
            
            frame = cv2.flip(frame, 1)
            frame_count += 1
            
            # Process frame
            annotated = classifier.process_frame(frame, frame_count)
            
            cv2.imshow("Trajectory Classifier - $1 Recognizer", annotated)
            
            # Handle keyboard input
            key = cv2.waitKey(5) & 0xFF
            
            if key == ord('q'):
                break
            
            elif key == ord('1'):
                classifier.active_types[TrajectoryType.SKELETON] = not classifier.active_types[TrajectoryType.SKELETON]
                print(f"[*] Skeleton tracking: {'ON' if classifier.active_types[TrajectoryType.SKELETON] else 'OFF'}")
            
            elif key == ord('2'):
                classifier.active_types[TrajectoryType.OBJECT] = not classifier.active_types[TrajectoryType.OBJECT]
                print(f"[*] Object tracking: {'ON' if classifier.active_types[TrajectoryType.OBJECT] else 'OFF'}")
            
            elif key == ord('3'):
                classifier.active_types[TrajectoryType.LASER] = not classifier.active_types[TrajectoryType.LASER]
                print(f"[*] Laser tracking: {'ON' if classifier.active_types[TrajectoryType.LASER] else 'OFF'}")
            
            elif key == ord('r'):
                if not classifier.recording:
                    # Ask for type and label
                    print("\nSelect trajectory type:")
                    print("  1 - Skeleton")
                    print("  2 - Object")
                    print("  3 - Laser")
                    type_input = input("Type (1-3): ").strip()
                    
                    traj_type_map = {
                        '1': TrajectoryType.SKELETON,
                        '2': TrajectoryType.OBJECT,
                        '3': TrajectoryType.LASER
                    }
                    
                    if type_input in traj_type_map:
                        label = input("Enter gesture name: ").strip()
                        if label:
                            classifier.start_recording(traj_type_map[type_input], label)
                            # Enable the type
                            classifier.active_types[traj_type_map[type_input]] = True
            
            elif key == ord('s'):
                if classifier.recording:
                    classifier.stop_recording()
    
    finally:
        classifier.skeleton_tracker.landmarker.close()
    
    cap.release()
    cv2.destroyAllWindows()
    print("[*] Trajectory classifier stopped.")


if __name__ == "__main__":
    main()
