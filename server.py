import socket
import threading
import time

# --- Configuration ---
INTERNAL_PORT = 6001    # For Python Services (Gaze, Face, BT, etc.)
CLIENT_PORT = 5003      # For C# Client

# --- State ---
python_services = []    # List of sockets for internal services
client_connections = [] # List of sockets for C# clients
state_lock = threading.Lock()

def broadcast_to_client(message):
    """Sends a message to all connected C# clients."""
    if not message.endswith('\n'):
        message += '\n'
    msg_bytes = message.encode('utf-8')
    
    with state_lock:
        for conn in list(client_connections):
            try:
                conn.sendall(msg_bytes)
            except Exception:
                if conn in client_connections:
                    client_connections.remove(conn)

def broadcast_to_services(message):
    """Sends a command to all connected Python services."""
    if not message.endswith('\n'):
        message += '\n'
    msg_bytes = message.encode('utf-8')
    
    with state_lock:
        for conn in list(python_services):
            try:
                conn.sendall(msg_bytes)
            except Exception:
                if conn in python_services:
                    python_services.remove(conn)

def handle_internal_service(conn, addr):
    """Handles data from Python services (Gaze, Face, BT, etc.)."""
    print(f"[+] Internal Service connected: {addr}")
    with state_lock:
        python_services.append(conn)
    
    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            
            # Relay message from service to C# clients
            msg = data.decode('utf-8').strip()
            if msg:
                print(f"[RELAY] Service -> Client: {msg}")
                broadcast_to_client(msg)
                
    except Exception as e:
        print(f"[!] Internal Service error {addr}: {e}")
    finally:
        with state_lock:
            if conn in python_services:
                python_services.remove(conn)
        conn.close()
        print(f"[-] Internal Service disconnected: {addr}")

def handle_client(conn, addr):
    """Handles data from C# clients."""
    print(f"[+] C# Client connected: {addr}")
    with state_lock:
        client_connections.append(conn)
    
    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            
            # Relay command from Client to Python services
            msg = data.decode('utf-8').strip()
            if msg:
                print(f"[RELAY] Client -> Services: {msg}")
                broadcast_to_services(msg)
                
    except Exception as e:
        print(f"[!] C# Client error {addr}: {e}")
    finally:
        with state_lock:
            if conn in client_connections:
                client_connections.remove(conn)
        conn.close()
        print(f"[-] C# Client disconnected: {addr}")

def internal_server_loop():
    """Server for Python services."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", INTERNAL_PORT))
    server.listen(10)
    print(f"[*] Hub: Listening for Services on port {INTERNAL_PORT}")
    
    while True:
        conn, addr = server.accept()
        threading.Thread(target=handle_internal_service, args=(conn, addr), daemon=True).start()

def client_server_loop():
    """Server for C# clients."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", CLIENT_PORT))
    server.listen(5)
    print(f"[*] Hub: Listening for C# Client on port {CLIENT_PORT}")
    
    while True:
        conn, addr = server.accept()
        threading.Thread(target=handle_client, args=(conn, addr), daemon=True).start()

def main():
    print(">>> Interaction Hub Starting...")
    print("=== Story Builder Interaction Hub ===")
    
    # Start both server loops
    t1 = threading.Thread(target=internal_server_loop, daemon=True)
    t2 = threading.Thread(target=client_server_loop, daemon=True)
    
    t1.start()
    t2.start()
    
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[*] Hub stopping...")

if __name__ == "__main__":
    main()
