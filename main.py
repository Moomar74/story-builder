import cv2
import socket
import threading
import struct
import time

CAMERA_INDEX = 1        # Mobile Camera (Camo)
BROADCAST_PORT = 6000   # Port for vision workers to receive frames
FRAME_QUALITY = 70      # Reduced JPEG quality
TARGET_FPS = 15         # Reduced frame rate to save memory/CPU

# --- State ---
worker_clients = []
state_lock = threading.Lock()

def handle_worker(conn, addr):
    """Handles a worker client that wants to receive frames."""
    print(f"[+] Worker connected for frames: {addr}")
    with state_lock:
        worker_clients.append(conn)
    
    try:
        # Just keep the connection open until it's closed by the worker
        while True:
            # We check if connection is alive by trying to receive heartbeat or just waiting
            data = conn.recv(1024)
            if not data:
                break
    except Exception:
        pass
    finally:
        with state_lock:
            if conn in worker_clients:
                worker_clients.remove(conn)
        conn.close()
        print(f"[-] Worker disconnected from frames: {addr}")

def broadcast_server():
    """TCP server to accept worker connections."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", BROADCAST_PORT))
    server.listen(10)
    print(f"[*] Frame Broadcaster: Listening on port {BROADCAST_PORT}")
    
    while True:
        conn, addr = server.accept()
        threading.Thread(target=handle_worker, args=(conn, addr), daemon=True).start()

def main():
    print(">>> Frame Broadcaster Starting...")
    print("=== Story Builder Frame Broadcaster ===")
    
    # Start the broadcast server thread
    threading.Thread(target=broadcast_server, daemon=True).start()
    
    # Open camera
    cap = cv2.VideoCapture(CAMERA_INDEX)
    if not cap.isOpened():
        print(f"[ERROR] Could not open camera {CAMERA_INDEX}")
        return

    print(f"[*] Capturing from camera {CAMERA_INDEX}...")
    
    try:
        while True:
            ret, frame = cap.read()
            if not ret:
                print("[!] Failed to capture frame")
                time.sleep(0.1)
                continue
            
            # Resize for workers (reduces memory load and network traffic)
            frame_resized = cv2.resize(frame, (640, 480))
            
            # Encode frame to JPEG
            result, encoded_img = cv2.imencode('.jpg', frame_resized, [int(cv2.IMWRITE_JPEG_QUALITY), FRAME_QUALITY])
            if not result:
                continue
            
            # Prepare message: [Length (4 bytes)] + [JPEG Data]
            data = encoded_img.tobytes()
            size = len(data)
            header = struct.pack(">L", size)
            
            # Send to all connected workers
            with state_lock:
                for conn in list(worker_clients):
                    try:
                        conn.sendall(header + data)
                    except Exception:
                        if conn in worker_clients:
                            worker_clients.remove(conn)
            
            # Control frame rate
            time.sleep(1.0 / TARGET_FPS)
            
    except KeyboardInterrupt:
        print("\n[*] Broadcaster stopping...")
    finally:
        cap.release()

if __name__ == "__main__":
    main()
