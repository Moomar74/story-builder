import sys
try:
    import cv2
    print("cv2 imported OK")
except Exception as e:
    print(f"Error importing cv2: {e}")
    
try:
    import mediapipe
    print("mediapipe imported OK")
except Exception as e:
    print(f"Error importing mediapipe: {e}")
    
try:
    import dollarpy
    print("dollarpy imported OK")
except Exception as e:
    print(f"Error importing dollarpy: {e}")
    
print(f"Python: {sys.executable}")
print(f"Version: {sys.version}")
