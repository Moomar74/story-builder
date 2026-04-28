# Trajectory Classifier - $1 Recognizer for AR Scenario

This module classifies trajectories from three sources using the $1 Unistroke Recognizer (DollarPy):

1. **SKELETON** - Human body/limb movement trajectories (hand, wrist, head)
2. **OBJECT** - Physical object trajectories (tracked via OpenCV blob detection, YOLO-ready)
3. **LASER** - Laser pointer trajectories (laser dot movement)

## Features

- Real-time trajectory tracking for 3 different input types
- $1 Unistroke Recognizer for gesture classification
- TCP server for AR client communication (port 5002)
- Custom template recording and saving
- Type-specific gesture templates (e.g., punch/grab for skeleton, toss/roll for objects)

## Quick Start

```bash
# Run the trajectory classifier
python trajectory_classifier.py
```

## Controls

| Key | Action |
|-----|--------|
| `1` | Toggle skeleton tracking |
| `2` | Toggle object tracking |
| `3` | Toggle laser tracking |
| `r` | Record new template |
| `s` | Save recorded template |
| `q` | Quit |

## TCP Messages

The server broadcasts these messages to connected clients:

```
TRAJECTORY:<type>:<gesture>:<confidence>   - Recognized trajectory
TRAJECTORY_START:<type>:<id>               - Recording started
TRAJECTORY_END:<type>:<id>:<result>        - Recording ended
```

## Default Templates by Type

### Skeleton
- circle, swipe_right, swipe_left, swipe_up, swipe_down
- wave, zigzag, punch, grab, spiral

### Object
- circle, swipe_right, swipe_left, swipe_up, swipe_down
- line_horizontal, line_vertical, diagonal_down, diagonal_up
- toss_arc, roll, figure_eight

### Laser
- circle, rectangle, triangle, cross
- check_mark, infinity, line_horizontal, line_vertical
- spiral, zigzag

## Recording Custom Templates

1. Press `r`
2. Select trajectory type (1=Skeleton, 2=Object, 3=Laser)
3. Enter gesture name
4. Perform the gesture (movement is recorded)
5. Press `s` to save

Custom templates are saved to `trajectory_templates.pkl`.

## Integration with AR Client

Connect your C# client to `localhost:5002` to receive trajectory events:

```csharp
// Example C# client code
tcpClient = new TcpClient("localhost", 5002);
stream = tcpClient.GetStream();
reader = new StreamReader(stream);

// Read messages
string message = reader.ReadLine();
// TRAJECTORY:skeleton:circle:0.85
```

## Requirements

- Python 3.10+
- OpenCV
- MediaPipe
- DollarPy
- NumPy

All dependencies are in `requirements.txt`.
