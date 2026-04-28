import subprocess
import sys

deps = ['opencv-python', 'mediapipe', 'dollarpy', 'numpy']

for dep in deps:
    print(f"Installing {dep}...")
    try:
        result = subprocess.run(
            [sys.executable, '-m', 'pip', 'install', dep, '--user'],
            capture_output=True,
            text=True,
            timeout=120
        )
        print(f"stdout: {result.stdout}")
        print(f"stderr: {result.stderr}")
        print(f"Return code: {result.returncode}")
    except Exception as e:
        print(f"Error: {e}")
