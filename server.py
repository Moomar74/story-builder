import socket
import asyncio
from bleak import BleakScanner
import threading
import os

# Global state
connected_clients = []
discovered_devices = []
# Map of BT_Address -> MarkerID to watch for
# This will be populated from users.txt and updated manually via client messages
watched_devices = {}

def load_users_to_watch():
    """Tries to find users.txt and pre-populate the watch list."""
    global watched_devices
    # Try common locations
    paths_to_check = [
        "users.txt",
        "Client/Client/TUIO11_NET-master/bin/Debug/users.txt",
        "Client/Client/TUIO11_NET-master/users.txt"
    ]
    
    found = False
    for p in paths_to_check:
        if os.path.exists(p):
            print(f"[*] Loading user watch list from {p}")
            try:
                with open(p, "r") as f:
                    for line in f:
                        parts = line.strip().split("|")
                        if len(parts) >= 3:
                            # format: mkr_id|name|bt_addr
                            mkr_id = parts[0]
                            bt_addr = parts[2]
                            if bt_addr and bt_addr != "N/A" and bt_addr != "None":
                                watched_devices[bt_addr] = mkr_id
                found = True
                break
            except Exception as e:
                pass
    
    if found:
        print(f"[*] Initialized watch list with {len(watched_devices)} device(s).")
    else:
        print("[!] users.txt not found. Automatic login will require client to send WATCH_BT.")

async def scanBT():
    """Periodically scans for Bluetooth devices and notifies clients."""
    global discovered_devices
    print("[*] Bluetooth scanner started.")
    while True:
        try:
            print("[.] Scanning for devices...", end='\r')
            devices = await BleakScanner.discover(timeout=5.0)
            discovered_devices = devices
            
            # 1. Prepare BT_LIST message
            dev_strings = []
            seen_addresses = set()
            for d in devices:
                if d.address not in seen_addresses:
                    name = d.name if d.name else "Unknown"
                    dev_strings.append(f"{name}|{d.address}")
                    seen_addresses.add(d.address)
            
            msg = "BT_LIST:" + ",".join(dev_strings)
            broadcast(msg)
            print(f"[*] Scan complete. Found {len(devices)} devices. Notified {len(connected_clients)} client(s).")
            
            # 2. Check for watched devices (Auto-Login)
            current_addresses = {d.address for d in devices}
            for addr, marker_id in list(watched_devices.items()):
                if addr in current_addresses:
                    print(f"[!] Watched device {addr} found! Triggering auto-login for Marker {marker_id}")
                    broadcast(f"AUTOLOGIN:{marker_id}")
                    
        except Exception as e:
            print(f"\n[!] Scan error: {e}")
        
        await asyncio.sleep(8) # Scan every 8 seconds

def broadcast(message):
    """Sends a message to all connected TCP clients."""
    if not message.endswith('\n'):
        message += '\n'
    
    msg_bytes = message.encode('utf-8')
    for conn in list(connected_clients):
        try:
            conn.sendall(msg_bytes)
        except:
            if conn in connected_clients:
                connected_clients.remove(conn)

def handle_client(conn, addr):
    """Handles messages from a client and keeps it in the broadcast list."""
    print(f"[+] Client connected: {addr}")
    # Before adding to broadcast list, tell it about current devices
    # AND check if any of those devices match its watch list already
    
    global discovered_devices
    connected_clients.append(conn)
    
    # 1. Send current device list
    if discovered_devices:
        dev_strings = []
        current_addresses = set()
        for d in discovered_devices:
            name = d.name if d.name else "Unknown"
            dev_strings.append(f"{name}|{d.address}")
            current_addresses.add(d.address)
        
        initial_msg = "BT_LIST:" + ",".join(dev_strings) + "\n"
        try:
            conn.sendall(initial_msg.encode('utf-8'))
            
            # 2. ALSO check if any watched device is already here!
            # If we know the user's BT address from users.txt (which we loaded at start)
            # we can tell the client to log in IMMEDIATELY.
            for bt_addr, mkr_id in watched_devices.items():
                if bt_addr in current_addresses:
                    print(f"[!] Watched device {bt_addr} already detected. Sending instant login for Marker {mkr_id} to new client.")
                    conn.sendall(f"AUTOLOGIN:{mkr_id}\n".encode('utf-8'))
        except:
            pass

    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            
            msg = data.decode('utf-8').strip()
            if not msg: continue
            
            print(f"[<] Message from {addr}: {msg}")
            
            # WATCH_BT:address|marker_id,address|marker_id...
            if msg.startswith("WATCH_BT:"):
                items = msg[9:].split(",")
                for item in items:
                    parts = item.split("|")
                    if len(parts) == 2:
                        watched_devices[parts[0]] = parts[1]
                print(f"[*] Updated watch list: {len(watched_devices)} active items.")
                
    except Exception as e:
        pass
    finally:
        if conn in connected_clients:
            connected_clients.remove(conn)
        conn.close()
        print(f"[-] Client disconnected: {addr}")

def start_tcp_server():
    """Synchronous TCP server loop."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        server.bind(("0.0.0.0", 5000))
        server.listen(5)
        print("[*] TCP Server listening on 0.0.0.0:5000")
    except Exception as e:
        print(f"[!] Server bind error: {e}")
        return

    while True:
        try:
            conn, addr = server.accept()
            t = threading.Thread(target=handle_client, args=(conn, addr))
            t.daemon = True
            t.start()
        except:
            break

async def main():
    # Load users from file first
    load_users_to_watch()
    
    # Start TCP server in background thread
    tcp_thread = threading.Thread(target=start_tcp_server)
    tcp_thread.daemon = True
    tcp_thread.start()
    
    # Run BT scanner
    await scanBT()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[*] Server stopping...")
    except Exception as e:
        print(f"[!] Fatal error: {e}")
