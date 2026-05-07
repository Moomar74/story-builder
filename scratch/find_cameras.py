import cv2

def find_cameras():
    print("Checking for available cameras...")
    for i in range(5):
        cap = cv2.VideoCapture(i)
        if cap.isOpened():
            ret, frame = cap.read()
            if ret:
                print(f"[+] Camera {i} is AVAILABLE and working.")
            else:
                print(f"[!] Camera {i} is OPENED but failed to read frame.")
            cap.release()
        else:
            print(f"[-] Camera {i} is not available.")

if __name__ == "__main__":
    find_cameras()
