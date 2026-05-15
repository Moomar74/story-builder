import subprocess
import time
import os
import sys

def run_servers():
    print("=== Story Builder Distributed Launcher ===")
    
    # Servers to run in order
    # (Name, Script)
    servers = [
        ("Interaction Hub", "server.py"),
        ("Frame Broadcaster", "main.py"),
        ("Bluetooth Service", "bluetooth_service.py"),
        ("Gaze Worker", "gaze_tracking_server.py"),
        ("Face Identifier", "face_identifier.py"),
        ("YOLO TUIO", "yolo_tuio_server.py"),
        ("Laser Tracker", "laser_tracking_server.py"),
        ("Gesture Controller", "gesture_controller.py"),
    ]
    
    processes = []
    python_exe = sys.executable
    
    try:
        for name, script in servers:
            print(f"[*] Launching {name} ({script})...")
            
            # On Windows, use 'start' to open in a new console window
            if os.name == "nt":
                p = subprocess.Popen(["start", "cmd", "/k", python_exe, "-u", script], shell=True)
            else:
                p = subprocess.Popen([python_exe, "-u", script])
                
            processes.append(p)
            time.sleep(2) # Delay to allow sockets to bind
            
        print("\n[SUCCESS] All vision and communication servers are running.")
        print("You can now start your C# Application and connect to Port 5003.")
        print("\nPress Ctrl+C to exit (this will NOT close the opened windows).")
        
        while True:
            time.sleep(1)
            
    except KeyboardInterrupt:
        print("\n[*] Stopping launcher...")
    except Exception as e:
        print(f"\n[ERROR] Launcher failed: {e}")

if __name__ == "__main__":
    run_servers()
