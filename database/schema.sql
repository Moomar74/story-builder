-- Professional Database Schema for Story Builder
-- Project: Face ID & Heatmap Analytics
-- Database: PostgreSQL (Supabase Compatible)

-- Enable vector extension for face embeddings if using pgvector
-- CREATE EXTENSION IF NOT EXISTS vector;

-- 1. Users Table
CREATE TABLE IF NOT EXISTS users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tuio_id INT UNIQUE, -- Incremental marker ID for manual login (e.g., 11, 12, 13...)
    name TEXT NOT NULL,
    face_encoding JSONB NOT NULL, -- Storing as JSONB array [0.12, -0.45, ...]
    created_at TIMESTAMPTZ DEFAULT NOW(),
    last_seen TIMESTAMPTZ,
    recognition_count INT DEFAULT 0,
    metadata JSONB DEFAULT '{}'::jsonb
);

-- 2. Pages Table
CREATE TABLE IF NOT EXISTS pages (
    id SERIAL PRIMARY KEY,
    name TEXT UNIQUE NOT NULL, -- e.g., 'SignIn', 'StorySelection'
    description TEXT
);

-- Insert default pages
INSERT INTO pages (name) VALUES 
('SignIn'), ('SignUp'), ('StorySelection'), ('StoryPlayer'), ('StoryBuilder')
ON CONFLICT (name) DO NOTHING;

-- 3. Sessions Table
CREATE TABLE IF NOT EXISTS sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
    start_time TIMESTAMPTZ DEFAULT NOW(),
    end_time TIMESTAMPTZ,
    device_info TEXT
);

-- 4. Gaze Data Table (Optimized for High Frequency)
CREATE TABLE IF NOT EXISTS gaze_data (
    id BIGSERIAL PRIMARY KEY,
    session_id UUID REFERENCES sessions(id) ON DELETE CASCADE,
    page_id INT REFERENCES pages(id),
    x FLOAT NOT NULL, -- Normalized 0.0 - 1.0
    y FLOAT NOT NULL, -- Normalized 0.0 - 1.0
    timestamp TIMESTAMPTZ DEFAULT NOW()
);

-- Create index for fast heatmap generation
CREATE INDEX idx_gaze_page_user ON gaze_data(page_id);
CREATE INDEX idx_gaze_timestamp ON gaze_data(timestamp);

-- 5. Heatmap Snapshots (Optional)
CREATE TABLE IF NOT EXISTS heatmap_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    page_id INT REFERENCES pages(id),
    user_id UUID REFERENCES users(id),
    image_url TEXT, -- Path to stored PNG if uploaded
    generated_at TIMESTAMPTZ DEFAULT NOW()
);

-- 6. Per-User Aggregated Heatmaps (Grid-based density map)
CREATE TABLE IF NOT EXISTS user_heatmaps (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
    page_id INT REFERENCES pages(id),
    grid_data JSONB NOT NULL,         -- 2D density grid (16x10 cells)
    hotspot_x FLOAT,                  -- Normalized x of highest-density zone (0.0-1.0)
    hotspot_y FLOAT,                  -- Normalized y of highest-density zone (0.0-1.0)
    total_points INT DEFAULT 0,
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(user_id, page_id)
);

CREATE INDEX idx_user_heatmaps_user ON user_heatmaps(user_id);

-- 7. Adaptive Menu Placement Preferences
CREATE TABLE IF NOT EXISTS adaptive_menu_config (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
    page_id INT REFERENCES pages(id),
    menu_x FLOAT NOT NULL,            -- Menu anchor X (0.0-1.0)
    menu_y FLOAT NOT NULL,            -- Menu anchor Y (0.0-1.0)
    menu_quadrant TEXT,               -- 'top-left', 'top-right', 'bottom-left', 'bottom-right'
    confidence FLOAT DEFAULT 0.0,     -- Reliability score (0.0=no data, 1.0=strong pattern)
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(user_id, page_id)
);

CREATE INDEX idx_adaptive_menu_user ON adaptive_menu_config(user_id);

-- 8. Row-Level Security (RLS) Policies
-- These allow the vision server (using the anon key) to manage the data.

-- Enable RLS on all tables
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE pages ENABLE ROW LEVEL SECURITY;
ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE gaze_data ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_heatmaps ENABLE ROW LEVEL SECURITY;
ALTER TABLE adaptive_menu_config ENABLE ROW LEVEL SECURITY;

-- Create Policies (Allow anon role to perform all operations for now)
-- Note: In a production app, you would restrict these based on auth.uid()

CREATE POLICY "Allow public select on pages" ON pages FOR SELECT USING (true);

CREATE POLICY "Allow public insert/select on users" ON users FOR ALL TO anon USING (true) WITH CHECK (true);

CREATE POLICY "Allow public insert/select on sessions" ON sessions FOR ALL TO anon USING (true) WITH CHECK (true);

CREATE POLICY "Allow public insert/select on gaze_data" ON gaze_data FOR ALL TO anon USING (true) WITH CHECK (true);

CREATE POLICY "Allow public insert/select on user_heatmaps" ON user_heatmaps FOR ALL TO anon USING (true) WITH CHECK (true);

CREATE POLICY "Allow public insert/select on adaptive_menu_config" ON adaptive_menu_config FOR ALL TO anon USING (true) WITH CHECK (true);
