# Face Identification Scenario for AR

## What It Does

The **Face Identifier** is a real-time face recognition system that:

1. **Detects Faces**: Uses computer vision to find faces in the camera feed
2. **Identifies Users**: Matches faces against a registered database
3. **Tracks Sessions**: Maintains consistent identity across frames
4. **Communicates Events**: Sends face detection events to AR clients via TCP

### Key Features

- **Real-time Recognition**: Identifies users within milliseconds
- **Face Registration**: Enroll new users with a simple capture
- **Persistent Database**: JSON-based storage of face encodings
- **Multi-face Tracking**: Handles multiple people simultaneously
- **Confidence Scoring**: Provides recognition confidence levels

## Benefits for AR Applications

| Benefit | Description |
|---------|-------------|
| **Personalized Content** | Show different AR content based on who's viewing |
| **Secure Access** | Restrict sensitive AR scenarios to authorized users |
| **Multi-user Support** | Multiple users see different content simultaneously |
| **Attention Tracking** | Monitor user engagement and focus |
| **Attendance Logging** | Track participation in educational/training AR |
| **User Analytics** | Collect per-user interaction data |

## How It Works

### Architecture

```
Camera Feed
    ↓
Face Detection (face_recognition library)
    ↓
Face Encoding (128-dimensional vector)
    ↓
Database Matching (compare with registered faces)
    ↓
Identity Assignment (known vs unknown)
    ↓
TCP Broadcast (notify AR clients)
```

### Data Flow

1. **Frame Processing**: Each camera frame is analyzed for faces
2. **Face Encoding**: Detected faces are converted to 128-d numerical embeddings
3. **Database Comparison**: Encodings are compared to registered users
4. **Identity Resolution**: Best match above threshold is assigned
5. **Event Broadcasting**: Recognition events sent to AR clients via TCP

## TCP Messages

### Server → Client (Broadcasts)

| Message Format | Description |
|----------------|-------------|
| `FACE_DETECTED:<id>:<name>:<confidence>` | Known face recognized |
| `FACE_REGISTERED:<id>:<name>` | New face enrolled |
| `FACE_UNKNOWN:<session_id>` | Unknown face detected |
| `FACE_LEFT:<id>` | Face no longer in frame |

### Client → Server (Commands)

| Command | Description |
|---------|-------------|
| `REGISTER:<name>` | Request face registration |
| `LIST_FACES` | Request list of registered faces |

## Usage

### Running the Server

```bash
python face_identifier.py
```

### Controls

| Key | Action |
|-----|--------|
| `r` | Start face registration (enter name) |
| `c` | Capture face during registration |
| `d` | Delete a registered face |
| `l` | List all registered faces |
| `s` | Save database to disk |
| `q` | Quit |

### Registration Workflow

1. Press `r` and enter a name
2. Face the camera
3. Press `c` to capture (or auto-capture)
4. Face is enrolled and saved

## Integration with AR Client

### C# Client Example

```csharp
using System.Net.Sockets;
using System.IO;

public class FaceIDClient
{
    TcpClient client;
    StreamReader reader;
    
    public void Connect()
    {
        client = new TcpClient("localhost", 5003);
        reader = new StreamReader(client.GetStream());
        
        // Start listening for face events
        Task.Run(ListenForFaces);
    }
    
    void ListenForFaces()
    {
        while (true)
        {
            string msg = reader.ReadLine();
            if (msg.StartsWith("FACE_DETECTED:"))
            {
                var parts = msg.Split(':');
                string faceId = parts[1];
                string name = parts[2];
                float confidence = float.Parse(parts[3]);
                
                // Adapt AR content based on recognized user
                LoadUserContent(name);
            }
        }
    }
    
    void LoadUserContent(string userName)
    {
        // Load personalized AR content for this user
        Debug.Log($"Welcome back, {userName}!");
    }
}
```

## Technical Details

### Face Recognition Algorithm

- **Library**: `face_recognition` (built on dlib)
- **Model**: HOG-based face detection + 128-d face encoding
- **Matching**: Euclidean distance between encodings
- **Tolerance**: 0.6 (configurable, lower = stricter)

### Performance

- **Processing Scale**: 0.25x (downscaled for speed)
- **Detection Rate**: Every frame
- **Recognition Rate**: Every frame (with cooldown)
- **Typical FPS**: 15-30 fps on modern hardware

### Database Format

```json
[
  {
    "id": "face_1699123456789",
    "name": "John Doe",
    "encoding": [0.123, -0.456, ...],  // 128 values
    "created_at": "2024-11-04T10:30:00",
    "last_seen": 1699123500.0,
    "recognition_count": 42
  }
]
```

## Requirements

```
opencv-python
face-recognition
numpy
```

Note: `face-recognition` requires dlib which may need Visual C++ build tools on Windows.

## Files

| File | Description |
|------|-------------|
| `face_identifier.py` | Main server script |
| `face_database.json` | Registered faces database |
| `FACE_ID_README.md` | This documentation |

## Integration with Other Modules

The Face Identifier works alongside:

- **Trajectory Classifier** (`trajectory_classifier.py`): Combine face ID + gesture recognition
- **Gesture Server** (`gesture_server.py`): Multi-modal interaction (face + hands)
- **Main Server** (`server.py`): User presence and authentication

### Example Multi-Modal Scenario

```
User approaches AR display
    ↓
Face Identifier: "Welcome back, Alice!"
    ↓
Trajectory Classifier: Alice waves hand
    ↓
System: "Hello Alice, opening your personalized dashboard..."
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "No faces detected" | Ensure good lighting, face camera directly |
| Low recognition accuracy | Re-register face with better lighting |
| Slow performance | Reduce `FACE_DETECTION_SCALE` in config |
| dlib install fails | Install Visual C++ Build Tools first |

## Future Enhancements

- [ ] Age/Gender estimation
- [ ] Emotion recognition integration
- [ ] Liveness detection (prevent photo spoofing)
- [ ] Face clustering for unsupervised grouping
- [ ] Cloud-based face database sync
