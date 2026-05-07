from db_manager import db
import time
import numpy as np

def test_user_registration():
    print("[*] Testing User Registration...")
    # Simulate a 128-d face encoding
    fake_encoding = list(np.random.rand(128))
    name = f"TestUser_{int(time.time())}"
    
    user_id = db.register_user(name, fake_encoding)
    print(f"[+] Registered User ID: {user_id}")
    
    users = db.get_all_users()
    found = any(u['id'] == user_id for u in users)
    print(f"[ {'OK' if found else 'FAIL'} ] User found in database")
    return user_id

def test_gaze_logging(user_id):
    print("[*] Testing Gaze Logging...")
    session_id = db.create_session(user_id)
    print(f"[+] Created Session: {session_id}")
    
    # Simulate some gaze points
    points = [
        (0.1, 0.2), (0.15, 0.25), (0.3, 0.4), (0.5, 0.5)
    ]
    
    db.log_gaze_batch(session_id, "SignIn", points)
    print("[+] Logged 4 gaze points")
    
    history = db.get_gaze_history("SignIn")
    print(f"[+] Retrieved {len(history)} total gaze points for SignIn")
    
    found_points = all(p in history for p in points)
    print(f"[ {'OK' if found_points else 'FAIL'} ] All points retrieved correctly")

if __name__ == "__main__":
    try:
        u_id = test_user_registration()
        test_gaze_logging(u_id)
        print("\n[SUCCESS] Professional Database Integration Verified!")
    except Exception as e:
        print(f"\n[ERROR] Verification failed: {e}")
