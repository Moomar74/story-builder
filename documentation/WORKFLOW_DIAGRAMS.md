# Story Builder - Detailed Workflows

This document visualizes the dynamic interactions and data pipelines within the Story Builder ecosystem.

![Story Builder Workflow Infographic](file:///d:/story-builder/documentation/workflow_infographic.png)

## 1. System-Wide Data Flow
This diagram shows how data originates from the hardware and propagates through the vision processing layers to the final UI adaptation.

```mermaid
sequenceDiagram
    participant Cam as Camera Feed
    participant VS as Vision Servers (Python)
    participant DB as Supabase Backend
    participant Client as C# Application (UI)

    Cam->>VS: Raw Frames
    VS->>VS: Process (MediaPipe/YOLO/dlib)
    
    rect rgb(240, 240, 255)
        Note right of VS: Face Identification Flow
        VS->>Client: FACE_DETECTED: user_id
        Client->>Client: Auto Login User
    end

    rect rgb(240, 255, 240)
        Note right of VS: Gaze & Adaptation Flow
        VS->>Client: GAZE: x, y (Real-time)
        VS->>DB: Log Gaze Points (Every 2s)
        DB-->>VS: Hotspot Analysis (Every 10s)
        VS->>Client: MENU_POS: Optimized Quadrant
        Client->>Client: Smoothly shift UI elements
    end

    rect rgb(255, 240, 240)
        Note right of VS: Physical Object Flow
        VS->>Client: TUIO Object: ID, Pos, Rotation
        Client->>Client: Render 3D Story Asset
    end
```

---

## 2. User Adaptive Lifecycle
This workflow details how the system "learns" from the user's focus and adapts the interface.

```mermaid
stateDiagram-v2
    [*] --> Idle: Camera active
    Idle --> Identification: Face Detected
    Identification --> SessionStart: Match Found in DB
    SessionStart --> Tracking: Real-time Gaze Stream
    
    state Tracking {
        [*] --> DataCollection
        DataCollection --> BatchLogging: Buffer Full
        BatchLogging --> HeatmapCalc: 10s Interval
        HeatmapCalc --> HotspotFound
        HotspotFound --> UI_Reconfiguration: New Quadrant
        UI_Reconfiguration --> DataCollection: Menu Repositioned
    }
    
    Tracking --> SessionEnd: Face Left (3s timeout)
    SessionEnd --> Idle
```

---

## 3. Gesture & Command Pipeline
How air gestures are converted into story progression.

```mermaid
graph LR
    subgraph "Capture"
        H[Hand Movement] --> MP[MediaPipe Landmarks]
    end

    subgraph "Recognition"
        MP --> Vel[Velocity Analysis]
        MP --> Dol[DollarPy $1 Recognizer]
        
        Vel --> S[Swipe Detected]
        Dol --> C[Complex Gesture: Circle/Wave]
    end

    subgraph "Execution"
        S --> TCP[TCP: GESTURE:swipe]
        C --> TCP
        TCP --> Action[C# Client: Flip Page / Trigger Event]
    end

    style Action fill:#f9f,stroke:#333,stroke-width:2px
```

---

## 4. TUIO Object Integration
The physical-to-digital pipeline for story props.

```mermaid
flowchart TD
    A[Physical Toy: e.g., Lion] --> B[YOLOv8 Detection]
    B --> C[Axis/Rotation Analysis]
    C --> D[OSC Packet Encoding]
    D --> E[UDP Broadcast :3333]
    E --> F[C# TUIO Client]
    F --> G[Instantiate 3D Character]
    G --> H[Sync Rotation & Pos]
```
