"""
HSV Range Calibrator for Green Marker / Laser
==============================================
Opens a camera feed with trackbars that let you interactively tune the HSV
lower/upper bounds until only your green marker is highlighted in the mask.

Usage:
    python hsv_calibrator.py                  # default camera 0
    python hsv_calibrator.py --camera 1       # use camera index 1

Once you're happy with the values, copy them into laser_tracking_server.py
as GREEN_LOWER and GREEN_UPPER.
"""

import argparse
import cv2
import numpy as np


def nothing(_):
    """Trackbar callback (no-op)."""
    pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-c", "--camera", type=int, default=0,
                    help="Camera index (default: 0)")
    args = ap.parse_args()

    cap = cv2.VideoCapture(args.camera)
    if not cap.isOpened():
        print(f"[ERROR] Could not open camera {args.camera}")
        return

    cv2.namedWindow("Trackbars")
    cv2.resizeWindow("Trackbars", 400, 350)

    # Default values for a generic "red" (Upper range)
    cv2.createTrackbar("H Low",  "Trackbars", 160, 179, nothing)
    cv2.createTrackbar("S Low",  "Trackbars", 100, 255, nothing)
    cv2.createTrackbar("V Low",  "Trackbars", 100, 255, nothing)
    cv2.createTrackbar("H High", "Trackbars", 179, 179, nothing)
    cv2.createTrackbar("S High", "Trackbars", 255, 255, nothing)
    cv2.createTrackbar("V High", "Trackbars", 255, 255, nothing)

    print("[*] Adjust the trackbars until ONLY the green marker is white.")
    print("[*] Press 'q' to quit and print the final values.\n")

    while True:
        ret, frame = cap.read()
        if not ret:
            break

        frame = cv2.resize(frame, (640, 480))
        blurred = cv2.GaussianBlur(frame, (11, 11), 0)
        hsv = cv2.cvtColor(blurred, cv2.COLOR_BGR2HSV)

        h_lo = cv2.getTrackbarPos("H Low",  "Trackbars")
        s_lo = cv2.getTrackbarPos("S Low",  "Trackbars")
        v_lo = cv2.getTrackbarPos("V Low",  "Trackbars")
        h_hi = cv2.getTrackbarPos("H High", "Trackbars")
        s_hi = cv2.getTrackbarPos("S High", "Trackbars")
        v_hi = cv2.getTrackbarPos("V High", "Trackbars")

        lower = np.array([h_lo, s_lo, v_lo])
        upper = np.array([h_hi, s_hi, v_hi])

        mask = cv2.inRange(hsv, lower, upper)
        mask = cv2.erode(mask, None, iterations=2)
        mask = cv2.dilate(mask, None, iterations=2)

        result = cv2.bitwise_and(frame, frame, mask=mask)

        # Find contours and draw them
        cnts, _ = cv2.findContours(mask.copy(), cv2.RETR_EXTERNAL,
                                   cv2.CHAIN_APPROX_SIMPLE)
        if cnts:
            c = max(cnts, key=cv2.contourArea)
            ((cx, cy), radius) = cv2.minEnclosingCircle(c)
            if radius > 5:
                cv2.circle(frame, (int(cx), int(cy)), int(radius),
                           (0, 255, 255), 2)

        # HUD overlay with current values
        info = f"Lower: ({h_lo},{s_lo},{v_lo})  Upper: ({h_hi},{s_hi},{v_hi})"
        cv2.putText(frame, info, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.55,
                    (0, 255, 0), 2)

        # Stack original + mask side by side
        mask_bgr = cv2.cvtColor(mask, cv2.COLOR_GRAY2BGR)
        top_row = np.hstack([frame, mask_bgr])
        cv2.imshow("Camera | Mask", top_row)

        if cv2.waitKey(1) & 0xFF == ord("q"):
            break

    cap.release()
    cv2.destroyAllWindows()

    # Print final values for easy copy-paste
    print("=" * 50)
    print("Copy these into laser_tracking_server.py:")
    print(f'GREEN_LOWER = ({h_lo}, {s_lo}, {v_lo})')
    print(f'GREEN_UPPER = ({h_hi}, {s_hi}, {v_hi})')
    print("=" * 50)


if __name__ == "__main__":
    main()
