import subprocess
import time
import sys
import os

def run_servers():
    print("Starting Story Builder Multi-Server Architecture...")
    
    servers = [
        ("Unified Vision (Face+Gaze)", "unified_vision_server.py"),
        ("YOLO TUIO", "yolo_tuio_server.py")
    ]
    
    processes = []
    
    try:
        for name, script in servers:
            print(f"[*] Launching {name}...")
            # Use 'py' launcher if on Windows, else 'python3'
            launcher = "py" if os.name == "nt" else "python3"
            
            # Start each server in a new terminal window (Windows specific)
            if os.name == "nt":
                p = subprocess.Popen(["start", "cmd", "/k", launcher, "-u", script], shell=True)
            else:
                p = subprocess.Popen([launcher, script])
                
            processes.append(p)
            time.sleep(2) # Brief delay to prevent camera resource conflicts
            
        print("\n[SUCCESS] All interaction servers are launching.")
        print("Now, please open Visual Studio and run the C# Client.")
        print("\nPress Ctrl+C in this terminal to exit (note: this may not close sub-terminals).")
        
        while True:
            time.sleep(1)
            
    except KeyboardInterrupt:
        print("\n[*] Shutting down launcher...")
    except Exception as e:
        print(f"\n[ERROR] Failed to launch servers: {e}")

if __name__ == "__main__":
    run_servers()
