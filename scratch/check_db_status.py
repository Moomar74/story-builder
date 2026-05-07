from db_manager import db
import json

def check_db():
    print("[*] Checking Database Tables...")
    tables = ["users", "pages", "sessions", "gaze_data", "user_heatmaps", "adaptive_menu_config"]
    
    for table in tables:
        try:
            resp = db.client.table(table).select("count", count="exact").limit(1).execute()
            count = resp.count if hasattr(resp, 'count') else "N/A"
            print(f"[OK] Table '{table}' is accessible. Count: {count}")
        except Exception as e:
            print(f"[FAIL] Table '{table}' error: {e}")

if __name__ == "__main__":
    check_db()
