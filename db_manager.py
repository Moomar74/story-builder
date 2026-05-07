import os
import json
import time
from datetime import datetime
from typing import List, Dict, Optional, Tuple
import numpy as np

try:
    from supabase import create_client, Client
    SUPABASE_AVAILABLE = True
except ImportError:
    SUPABASE_AVAILABLE = False

class DBManager:
    """
    Professional Database Manager for Story Builder.
    Uses Supabase SDK (HTTPS) with local JSON fallback.
    """
    
    def __init__(self):
        # Configuration from your provided credentials
        self.url = "https://dgnjmuvdsjbatlbdwtvp.supabase.co"
        self.key = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImRnbmptdXZkc2piYXRsYmR3dHZwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzc5ODc5MjMsImV4cCI6MjA5MzU2MzkyM30.NRNFAw-JeJBhziNVkoH94EKt-dan0lwVfTORp_xxY3U"
        
        self.client: Optional[Client] = None
        self.is_connected = False
        
        if SUPABASE_AVAILABLE:
            try:
                self.client = create_client(self.url, self.key)
                # Quick connectivity check
                self.client.table("pages").select("id").limit(1).execute()
                self.is_connected = True
                print("[+] Connected to Supabase via HTTPS SDK")
            except Exception as e:
                print(f"[!] Supabase connection failed: {e}")
                self.is_connected = False
        else:
            print("[!] 'supabase' library not found. Run: pip install supabase")
            print("[*] Running in Local Mode (Fallback to JSON)")

        # Local fallback data
        self.local_db_path = "storybuilder_local.json"
        self._init_local_db()

    def _init_local_db(self):
        """Initialize local JSON database if not exists."""
        if not os.path.exists(self.local_db_path):
            initial_data = {
                "users": [],
                "sessions": [],
                "gaze_data": []
            }
            with open(self.local_db_path, 'w') as f:
                json.dump(initial_data, f, indent=2)

    # --- User Management ---

    def register_user(self, name: str, encoding: List[float]) -> str:
        """Register a new user with face encoding."""
        if self.is_connected:
            try:
                data = {
                    "name": name,
                    "face_encoding": encoding, # SDK handles JSON conversion
                    "created_at": datetime.now().isoformat()
                }
                response = self.client.table("users").insert(data).execute()
                if response.data:
                    return str(response.data[0]['id'])
            except Exception as e:
                print(f"[!] DB Error registering user: {e}")
        
        # Fallback to local
        user_id = f"local_{int(time.time() * 1000)}"
        with open(self.local_db_path, 'r+') as f:
            db = json.load(f)
            db['users'].append({
                "id": user_id,
                "name": name,
                "face_encoding": encoding,
                "created_at": datetime.now().isoformat()
            })
            f.seek(0)
            json.dump(db, f, indent=2)
            f.truncate()
        return user_id

    def get_all_users(self) -> List[Dict]:
        """Fetch all registered users."""
        if self.is_connected:
            try:
                response = self.client.table("users").select("*").execute()
                return response.data
            except Exception as e:
                print(f"[!] DB Error fetching users: {e}")
        
        # Fallback to local
        with open(self.local_db_path, 'r') as f:
            db = json.load(f)
            return db['users']

    # --- Gaze & Heatmap Analytics ---

    def create_session(self, user_id: str) -> str:
        """Start a new interaction session."""
        if self.is_connected:
            try:
                # Handle UUID vs local string ID
                is_local = user_id.startswith("local_") or user_id == "guest_user"
                uid = None if is_local else user_id
                
                data = {"user_id": uid, "start_time": datetime.now().isoformat()}
                response = self.client.table("sessions").insert(data).execute()
                if response.data:
                    return str(response.data[0]['id'])
            except Exception as e:
                print(f"[!] DB Error creating session: {e}")
        
        return f"sess_{int(time.time() * 1000)}"

    def log_gaze_batch(self, session_id: str, page_name: str, points: List[Tuple[float, float]]):
        """Batch log gaze points."""
        if not points: return
        page_id = self._get_page_id(page_name)

        if self.is_connected and not session_id.startswith("sess_"):
            try:
                batch = [
                    {"session_id": session_id, "page_id": page_id, "x": x, "y": y}
                    for x, y in points
                ]
                self.client.table("gaze_data").insert(batch).execute()
                return
            except Exception as e:
                print(f"[!] DB Error logging gaze batch: {e}")

        # Fallback to local
        with open(self.local_db_path, 'r+') as f:
            db = json.load(f)
            for x, y in points:
                db['gaze_data'].append({
                    "session_id": session_id,
                    "page_id": page_id,
                    "x": x,
                    "y": y,
                    "timestamp": datetime.now().isoformat()
                })
            f.seek(0)
            json.dump(db, f, indent=2)
            f.truncate()

    def _get_page_id(self, page_name: str) -> int:
        mapping = {"SignIn": 1, "SignUp": 2, "StorySelection": 3, "StoryPlayer": 4, "StoryBuilder": 5}
        return mapping.get(page_name, 1)

    def get_gaze_history(self, page_name: str) -> List[Tuple[float, float]]:
        """Retrieve gaze points for heatmap generation."""
        page_id = self._get_page_id(page_name)
        
        if self.is_connected:
            try:
                response = self.client.table("gaze_data").select("x, y").eq("page_id", page_id).execute()
                return [(p['x'], p['y']) for p in response.data]
            except Exception as e:
                print(f"[!] DB Error fetching gaze history: {e}")
        
        # Fallback to local
        with open(self.local_db_path, 'r') as f:
            db = json.load(f)
            return [(p['x'], p['y']) for p in db['gaze_data'] if p['page_id'] == page_id]

    # --- Per-User Heatmap Aggregation & Adaptive Menu ---

    GRID_COLS = 16  # Density grid resolution
    GRID_ROWS = 10
    MIN_POINTS_FOR_ADAPTIVE = 50  # Minimum gaze points before activating adaptive menu

    def get_user_gaze_history(self, user_id: str, page_name: str) -> List[Tuple[float, float]]:
        """Fetch RECENT gaze history (last 30 seconds) for a specific user and page."""
        page_id = self._get_page_id(page_name)
        is_local = user_id.startswith("local_") or user_id == "guest_user"

        if self.is_connected and not is_local:
            try:
                # 1. Find all session IDs for this user
                sessions_resp = self.client.table("sessions").select("id").eq("user_id", user_id).execute()
                if not sessions_resp.data:
                    return []
                session_ids = [s['id'] for s in sessions_resp.data]
                
                # 2. Fetch gaze data for those sessions within the last 30 seconds
                from datetime import timedelta
                cutoff = (datetime.now() - timedelta(seconds=30)).isoformat()
                
                all_points = []
                batch_size = 20
                for i in range(0, len(session_ids), batch_size):
                    batch = session_ids[i:i+batch_size]
                    resp = (self.client.table("gaze_data")
                            .select("x, y")
                            .eq("page_id", page_id)
                            .in_("session_id", batch)
                            .gt("timestamp", cutoff)
                            .execute())
                    if resp.data:
                        all_points.extend([(p['x'], p['y']) for p in resp.data])
                return all_points
            except Exception as e:
                print(f"[!] DB Error fetching gaze history: {e}")
        
        # Fallback to local (no time filter for local yet)
        with open(self.local_db_path, 'r') as f:
            db = json.load(f)
            # Find local session IDs for this user
            u_sessions = [s['id'] for s in db['sessions'] if s['user_id'] == user_id]
            return [(p['x'], p['y']) for p in db['gaze_data'] 
                    if p['page_id'] == page_id and p['session_id'] in u_sessions]

    def _build_density_grid(self, points: List[Tuple[float, float]]) -> List[List[int]]:
        """Build a GRID_ROWS x GRID_COLS density grid from normalized gaze points."""
        grid = [[0] * self.GRID_COLS for _ in range(self.GRID_ROWS)]
        for x, y in points:
            col = min(int(x * self.GRID_COLS), self.GRID_COLS - 1)
            row = min(int(y * self.GRID_ROWS), self.GRID_ROWS - 1)
            col = max(0, col)
            row = max(0, row)
            grid[row][col] += 1
        return grid

    def _find_hotspot(self, grid: List[List[int]]) -> Tuple[float, float]:
        """Find the normalized (x, y) center of the highest-density cell.
        Uses a 3x3 weighted average around the peak for sub-cell precision.
        """
        max_val = 0
        max_r, max_c = 0, 0
        for r in range(self.GRID_ROWS):
            for c in range(self.GRID_COLS):
                if grid[r][c] > max_val:
                    max_val = grid[r][c]
                    max_r, max_c = r, c

        if max_val == 0:
            return (0.5, 0.5)

        # Weighted centroid around the peak (3x3 neighborhood)
        total_weight = 0.0
        weighted_c = 0.0
        weighted_r = 0.0
        for dr in range(-1, 2):
            for dc in range(-1, 2):
                nr, nc = max_r + dr, max_c + dc
                if 0 <= nr < self.GRID_ROWS and 0 <= nc < self.GRID_COLS:
                    w = grid[nr][nc]
                    weighted_r += nr * w
                    weighted_c += nc * w
                    total_weight += w

        if total_weight > 0:
            avg_r = weighted_r / total_weight
            avg_c = weighted_c / total_weight
        else:
            avg_r, avg_c = max_r, max_c

        # Convert grid cell to normalized coords (center of cell)
        hotspot_x = (avg_c + 0.5) / self.GRID_COLS
        hotspot_y = (avg_r + 0.5) / self.GRID_ROWS
        return (round(hotspot_x, 4), round(hotspot_y, 4))

    def compute_and_store_heatmap(self, user_id: str, page_name: str) -> Optional[Dict]:
        """Aggregate all gaze data for a user+page into a density grid,
        compute the hotspot, and upsert into user_heatmaps table.
        Returns the heatmap record or None.
        """
        points = self.get_user_gaze_history(user_id, page_name)
        if not points:
            return None

        grid = self._build_density_grid(points)
        hotspot_x, hotspot_y = self._find_hotspot(grid)
        page_id = self._get_page_id(page_name)
        is_local = user_id.startswith("local_") or user_id == "guest_user"

        record = {
            "grid_data": grid,
            "hotspot_x": hotspot_x,
            "hotspot_y": hotspot_y,
            "total_points": len(points),
        }

        if self.is_connected and not is_local:
            try:
                # Upsert: update if exists, insert if not
                data = {
                    "user_id": user_id,
                    "page_id": page_id,
                    "grid_data": json.dumps(grid),
                    "hotspot_x": hotspot_x,
                    "hotspot_y": hotspot_y,
                    "total_points": len(points),
                    "updated_at": datetime.now().isoformat()
                }
                self.client.table("user_heatmaps").upsert(
                    data, on_conflict="user_id,page_id"
                ).execute()
                print(f"[+] Heatmap stored for user {user_id[:8]}… on {page_name}: "
                      f"hotspot=({hotspot_x:.2f}, {hotspot_y:.2f}), points={len(points)}")
                return record
            except Exception as e:
                print(f"[!] DB Error storing heatmap: {e}")

        # Local fallback
        record["user_id"] = user_id
        record["page_id"] = page_id
        return record

    def get_user_hotspot(self, user_id: str, page_name: str) -> Optional[Tuple[float, float]]:
        """Return the pre-computed hotspot (x, y) for a user on a page.
        Returns None if no heatmap data exists.
        """
        page_id = self._get_page_id(page_name)
        is_local = user_id.startswith("local_") or user_id == "guest_user"

        if self.is_connected and not is_local:
            try:
                resp = (self.client.table("user_heatmaps")
                        .select("hotspot_x, hotspot_y, total_points")
                        .eq("user_id", user_id)
                        .eq("page_id", page_id)
                        .execute())
                if resp.data and len(resp.data) > 0:
                    row = resp.data[0]
                    if row.get('total_points', 0) >= self.MIN_POINTS_FOR_ADAPTIVE:
                        return (row['hotspot_x'], row['hotspot_y'])
            except Exception as e:
                print(f"[!] DB Error fetching hotspot: {e}")
        return None

    def compute_adaptive_menu_position(self, user_id: str, page_name: str) -> Optional[Dict]:
        """Analyze the hotspot and compute optimal menu position.
        The menu is placed NEAR the hotspot but offset so it doesn't occlude
        the area the user focuses on. Returns dict with menu_x, menu_y,
        menu_quadrant, confidence.
        """
        page_id = self._get_page_id(page_name)
        is_local = user_id.startswith("local_") or user_id == "guest_user"

        # First, ensure heatmap is computed
        heatmap = self.compute_and_store_heatmap(user_id, page_name)
        if not heatmap:
            return None

        hotspot_x = heatmap['hotspot_x']
        hotspot_y = heatmap['hotspot_y']
        total_points = heatmap['total_points']

        # Confidence: scales from 0.0 (no data) to 1.0 (lots of data)
        # Sigmoid-like ramp: 50 points → ~0.5, 200 points → ~0.9
        confidence = min(1.0, total_points / 250.0)
        if total_points < self.MIN_POINTS_FOR_ADAPTIVE:
            confidence = 0.0

        # Determine which quadrant the user is looking at (Gaze Quadrant)
        gaze_h = "left" if hotspot_x <= 0.5 else "right"
        gaze_v = "top" if hotspot_y <= 0.5 else "bottom"

        # Strategy: Snap menu to the CORNER of the quadrant OPPOSITE to where the user is looking
        # (e.g. if user looks Top-Left, put menu in Bottom-Right corner)
        menu_h = "right" if gaze_h == "left" else "left"
        menu_v = "bottom" if gaze_v == "top" else "top"

        # SNAP TO CORNERS: using 0.1 and 0.9 as safe padding from screen edges
        menu_x = 0.1 if menu_h == "left" else 0.9
        menu_y = 0.1 if menu_v == "top" else 0.9

        quadrant = f"{menu_v}-{menu_h}"

        result = {
            "menu_x": round(menu_x, 4),
            "menu_y": round(menu_y, 4),
            "menu_quadrant": quadrant,
            "confidence": round(confidence, 4)
        }

        # Store in database
        if self.is_connected and not is_local:
            try:
                data = {
                    "user_id": user_id,
                    "page_id": page_id,
                    "menu_x": result['menu_x'],
                    "menu_y": result['menu_y'],
                    "menu_quadrant": quadrant,
                    "confidence": result['confidence'],
                    "updated_at": datetime.now().isoformat()
                }
                self.client.table("adaptive_menu_config").upsert(
                    data, on_conflict="user_id,page_id"
                ).execute()
                print(f"[+] Adaptive menu config stored: {quadrant} "
                      f"({result['menu_x']:.2f}, {result['menu_y']:.2f}) "
                      f"confidence={result['confidence']:.2f}")
            except Exception as e:
                print(f"[!] DB Error storing menu config: {e}")

        return result

    def get_adaptive_menu_config(self, user_id: str, page_name: str) -> Optional[Dict]:
        """Retrieve the stored adaptive menu position for a user+page.
        Returns dict with menu_x, menu_y, menu_quadrant, confidence or None.
        """
        page_id = self._get_page_id(page_name)
        is_local = user_id.startswith("local_") or user_id == "guest_user"

        if self.is_connected and not is_local:
            try:
                resp = (self.client.table("adaptive_menu_config")
                        .select("menu_x, menu_y, menu_quadrant, confidence")
                        .eq("user_id", user_id)
                        .eq("page_id", page_id)
                        .execute())
                if resp.data and len(resp.data) > 0:
                    return resp.data[0]
            except Exception as e:
                print(f"[!] DB Error fetching menu config: {e}")

        return None

# Singleton instance
db = DBManager()
