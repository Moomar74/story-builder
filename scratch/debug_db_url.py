with open('db_manager.py', 'r') as f:
    lines = f.readlines()
    line23 = lines[22]
    print(f"Line 23: {repr(line23)}")
    print(f"Hex: {line23.encode().hex()}")
