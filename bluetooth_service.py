import socket
import asyncio
import threading
import time
import os
from bleak import BleakScanner

# --- Configuration ---
HUB_IP = "127.0.0.1"
HUB_PORT = 6001 # Internal Port of server.py

# --- Bluetooth State ---
discovered_devices = []
watched_devices = {}
active_sessions = {}

def load_users_to_watch():
    global watched_devices
    paths_to_check = ["users.txt", "Client/users.txt"]
    for p in paths_to_check:
        if os.path.exists(p):
            print(f"[*] Loading user watch list from {p}")
            try:
                with open(p, "r") as f:
                    for line in f:
                        parts = line.strip().split("|")
                        if len(parts) >= 3:
                            mkr_id = parts[0]
                            bt_addr = parts[2].strip().upper()
                            if bt_addr and bt_addr not in ["N/A", "NONE"]:
                                watched_devices[bt_addr] = mkr_id
                break
            except: pass

async def scan_bt(hub_conn):
    global discovered_devices, active_sessions
    print("[*] Bluetooth Service: Scanning started.")
    while True:
        try:
            devices = await BleakScanner.discover(timeout=5.0)
            discovered_devices = devices
            
            # 1. Notify Hub of current list
            dev_strings = [f"{d.name if d.name else 'Unknown'}|{d.address}" for d in devices]
            msg = "BT_LIST:" + ",".join(dev_strings)
            hub_conn.sendall((msg + "\n").encode('utf-8'))
            
            # 2. Handle Auto-Login/Logout
            current_addresses = {d.address.upper() for d in devices}
            for addr, mkr_id in watched_devices.items():
                addr_up = addr.upper()
                if addr_up in current_addresses:
                    if addr_up not in active_sessions:
                        hub_conn.sendall(f"AUTOLOGIN:{mkr_id}\n".encode('utf-8'))
                        active_sessions[addr_up] = mkr_id
                elif addr_up in active_sessions:
                    hub_conn.sendall(f"AUTOLOGOUT:{mkr_id}\n".encode('utf-8'))
                    del active_sessions[addr_up]
                    
        except Exception as e:
            print(f"[!] BT Scan Error: {e}")
        await asyncio.sleep(5)

def main():
    print(">>> Bluetooth Service Starting...")
    print("=== Story Builder Bluetooth Service ===")
    load_users_to_watch()
    
    # Connect to Hub
    try:
        hub_conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        hub_conn.connect((HUB_IP, HUB_PORT))
        print(f"[*] Connected to Hub at {HUB_IP}:{HUB_PORT}")
    except Exception as e:
        print(f"[ERROR] Could not connect to Hub: {e}")
        return

    # Start BT Scanner in asyncio loop
    try:
        asyncio.run(scan_bt(hub_conn))
    except KeyboardInterrupt:
        print("\n[*] BT Service stopping...")
    finally:
        hub_conn.close()

if __name__ == "__main__":
    main()
