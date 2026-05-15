# Story Builder - System Documentation

## Project Overview
**Story Builder** is an immersive, interactive storytelling platform that combines computer vision, augmented reality (AR) concepts, and gaze-driven adaptive interfaces. The system tracks users, objects, and gestures in real-time to create a personalized and responsive experience.

The architecture follows a **Multi-Server, Multi-Modal** approach:
- **Vision Layer**: Multiple Python servers handle specific CV tasks (Face ID, Gaze Tracking, Object Recognition, Gestures).
- **Communication Layer**: Real-time data is streamed via **TCP** (custom protocols) and **UDP** (TUIO/OSC) to the client.
- **Data Layer**: A professional database (Supabase/PostgreSQL) stores user profiles, analytics, and heatmap data for adaptive UI.
- **Client Layer**: A C# (TUIO-enabled) application provides the user interface and story-playing environment.

---

## Technology Stack

### Backend & Vision (Python)
- **OpenCV**: Core image processing and camera handling.
- **MediaPipe**: High-performance face landmarker, iris tracking, and holistic skeleton tracking.
- **dlib**: ResNet-based face recognition and HOG face detection.
- **YOLOv8 (Ultralytics)**: Real-time object detection.
- **DollarPy**: $1 Unistroke Recognizer for gesture classification.
- **Supabase SDK**: Cloud database integration (PostgreSQL via HTTPS).
- **Socket / Threading**: Custom TCP/UDP server implementation for low-latency streaming.

### Database
- **Supabase**: Primary cloud database (PostgreSQL).
- **JSON Fallback**: Local storage (`storybuilder_local.json`) for offline mode.

### Client (C#)
- **.NET / C#**: Main application logic.
- **TUIO**: Table-top User Interface Objects protocol for multi-touch and object tracking.
- **Newtonsoft.Json**: JSON serialization for database and communication.

---

## Workflow & Pipeline Diagram

```mermaid
graph TD
    subgraph "Hardware Layer"
        Cam[Webcam / Camera]
        Mic[Microphone (Optional)]
    end

    subgraph "Vision Servers (Python)"
        UVS[Unified Vision Server]
        YOLO[YOLO TUIO Server]
        GS[Gesture Server]
        TC[Trajectory Classifier]
    end

    subgraph "Data Management"
        DBM[DB Manager]
        Supa[(Supabase Cloud)]
        Local[(Local JSON DB)]
    end

    subgraph "Client Application (C#)"
        UI[User Interface]
        SM[Story Manager]
        AM[Adaptive Menu Engine]
        TUIO_Client[TUIO Client]
    end

    Cam --> UVS
    Cam --> YOLO
    Cam --> GS
    Cam --> TC

    UVS <--> DBM
    GS <--> DBM
    DBM <--> Supa
    DBM <--> Local

    %% Communication Protocols
    UVS -- "TCP (Gaze, FaceID, MenuPos)" --> AM
    YOLO -- "UDP (TUIO Objects)" --> TUIO_Client
    GS -- "TCP (Gestures, Skeleton)" --> UI
    
    UI --> SM
    AM --> UI
    SM -- "TCP (Page Changes)" --> UVS
```

---

## File Documentation

### Core Servers
#### [unified_vision_server.py](file:///d:/story-builder/unified_vision_server.py)
The central "Brain" of the vision system.
- **Purpose**: Orchestrates Face Identification and Gaze Tracking simultaneously.
- **Responsibilities**:
    - Runs threads for Face recognition, Gaze estimation, and DB logging.
    - Streams gaze coordinates (0.0 - 1.0) to the client.
    - Identifies users and starts/stops interaction sessions.
    - Computes **Adaptive Menu Positions** by moving UI elements away from user hotspots.
- **TCP Port**: 5002 (Gaze), 5003 (FaceID).

#### [yolo_tuio_server.py](file:///d:/story-builder/yolo_tuio_server.py)
Object recognition and spatial tracking.
- **Purpose**: Detects physical objects (props) and converts them to TUIO messages.
- **Responsibilities**:
    - Uses YOLOv8 to identify objects (Fox, Lion, Eagle, Wolf).
    - Detects the **rotation angle** of objects for oriented rendering.
    - Broadcasts TUIO messages (set, alive, fseq) via UDP.
- **Protocol**: UDP Port 3333 (TUIO).

#### [gesture_server.py](file:///d:/story-builder/gesture_server.py)
Human-computer interaction through body movement.
- **Purpose**: Recognizes hand gestures and tracks the human skeleton.
- **Responsibilities**:
    - Uses MediaPipe Holistic for body/hand landmarks.
    - Implements a **Circular Menu** that appears on the user's left palm.
    - Recognizes swipes, circles, waves, and "Air Clicks" (fist gesture).
    - Streams skeleton data for real-time visualization.
- **TCP Port**: 5001.

#### [gaze_tracking_server.py](file:///d:/story-builder/gaze_tracking_server.py)
Specialized gaze and eye tracking.
- **Purpose**: Tracks where the user is looking on the screen using facial landmarks.
- **Responsibilities**:
    - Calculates the eye aspect ratio (EAR) for **Blink Detection**.
    - Uses MediaPipe iris landmarks for sub-pixel gaze estimation.
    - Generates **Heatmaps** (Gaussian-blurred gaze densities) and saves them as PNG files.
    - Communicates with the client to track "Active Pages" (e.g., SignIn, StoryPlayer).
- **Protocol**: TCP Port 5002.

#### [trajectory_classifier.py](file:///d:/story-builder/trajectory_classifier.py)
General-purpose gesture recognizer.
- **Purpose**: Classifies paths from different sources (Skeleton, Object, Laser).
- **Responsibilities**:
    - Implements the $1 Unistroke Recognizer algorithm.
    - Allows recording and saving new gesture templates.

### Database & Logic
#### [db_manager.py](file:///d:/story-builder/db_manager.py)
Centralized data access layer.
- **Purpose**: Manages communication with Supabase and local JSON fallback.
- **Responsibilities**:
    - User registration (saving names and face geometry signatures).
    - Session tracking (start/end times).
    - **Gaze Logging**: Batch inserts gaze points for heatmap generation.
    - **Analytics**: Computes user hotspots and optimal menu quadrants.

#### [face_identifier.py](file:///d:/story-builder/face_identifier.py)
Face recognition logic.
- **Purpose**: Detects and identifies faces.
- **Responsibilities**:
    - Uses `dlib` ResNet for high-accuracy recognition.
    - Provides a "Geometry Signature" fallback (10-dimensional vector) if `dlib` is unavailable.
    - Manages the local `face_database.json`.

#### [server.py](file:///d:/story-builder/server.py)
A legacy or helper server for general connectivity.

### Client Layer (C# / TUIO)
The Client is located in the `Client/` directory and is a TUIO-compliant application.
- **TUIO11_NET**: A library for handling TUIO/OSC messages.
- **TUIO_DEMO**: The main application entry point.
- **Responsibilities**:
    - Renders the interactive story scenes.
    - Manages the local JSON database (`storybuilder.db.json`) for attendance and student records.
    - Responds to `GAZE`, `GESTURE`, and `FACE_DETECTED` messages to trigger UI animations and logic.
    - Handles "Marker Controls" (e.g., Marker 10 for Guest Login, Marker 36 for Teacher Login).

### Utility & Setup
#### [run_all_servers.py](file:///d:/story-builder/run_all_servers.py)
Convenience script to launch the multi-server architecture.
- **Purpose**: Spawns `unified_vision_server.py` and `yolo_tuio_server.py` in separate terminals.

#### [install_deps.py](file:///d:/story-builder/install_deps.py)
Automates the installation of required Python libraries.

#### [test_db_integration.py](file:///d:/story-builder/test_db_integration.py)
Validation script for Supabase connectivity and gaze logging.

### Other Utility Scripts
- **[list_cameras.py](file:///d:/story-builder/list_cameras.py)**: Scans and lists available camera indices to help with configuration.
- **[test_import.py](file:///d:/story-builder/test_import.py)**: Checks if all critical libraries (cv2, mediapipe, dlib) are correctly installed.
- **[main.py](file:///d:/story-builder/main.py)**: Entry point placeholder.

---

## Detailed Pipeline & Data Flow

### 1. User Authentication (Face ID)
1. User stands in front of the camera.
2. `unified_vision_server.py` captures frames and sends them to the `FaceRecognitionEngine`.
3. If the face matches a database record, it broadcasts `FACE_DETECTED:id:name`.
4. The C# Client receives this and automatically logs the user in.

### 2. Interaction & Gaze Tracking
1. While logged in, MediaPipe tracks the user's irises and head pose.
2. `unified_vision_server.py` sends normalized `(x, y)` coordinates to the client.
3. Every 2 seconds, the `db_logger_worker` flushes the gaze points to the `gaze_data` table in Supabase.
4. The C# Client uses the real-time gaze for UI highlighting or game triggers.

### 3. Adaptive UI (Heatmap Aggregation)
1. Every 10 seconds, the `heatmap_aggregation_worker` triggers.
2. It queries the database for the user's recent gaze history on the current page.
3. It builds a density grid and finds the "Hotspot" (where the user looks most).
4. It calculates the **Optimal Menu Quadrant** (the corner furthest from the hotspot).
5. It sends a `MENU_POS` message to the client, which smoothly repositions its UI elements.

### 4. Object & Gesture Interaction
1. Placing a tracked prop (e.g., a "Fox" toy) on the table triggers `yolo_tuio_server.py`.
2. The client receives the TUIO message and spawns a 3D model at the prop's location.
3. Swiping the hand in the air triggers `gesture_server.py`, which sends a `GESTURE:swipe_right` command to flip a story page.

---

## Database Schema (Supabase)

| Table | Purpose |
|-------|---------|
| `users` | Stores user IDs, names, and face encoding vectors. |
| `sessions` | Interaction logs (user_id, start_time, end_time). |
| `pages` | Metadata for different app screens (SignIn, StoryPlayer, etc.). |
| `gaze_data` | Raw gaze points (session_id, page_id, x, y, timestamp). |
| `user_heatmaps` | Aggregated density grids and hotspots per user/page. |
| `adaptive_menu_config` | Calculated menu positions (menu_x, menu_y, quadrant). |

---

## Support & Maintenance
- **Adding a User**: Use the registration command via C# client or press 'r' in the Face ID server window.
- **New Gestures**: Use `gesture_server.py` to record and save new templates to `gesture_templates.pkl`.
- **Database Backup**: Regular exports of the Supabase tables are recommended. Local fallback is automatic.
