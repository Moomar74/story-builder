/*
    IMMERSIVE STORY BUILDER – TUIO Demo  (C# 7.3 compatible)
    HCI Project  |  Semester 8

    Marker Assignments
    ------------------
    ID 0  ->  LION   (Character: The Brave One)
    ID 1  ->  FOX    (Character: The Clever One)
    ID 2  ->  EAGLE  (Character: The Swift One)
    ID 3  ->  WOLF   (Character: The Wild One)
    ID 4  ->  OWL    (SIGN-IN marker – place & rotate >=45 deg to authenticate)
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using TUIO;

// ============================================================
//  Entry point
// ============================================================
public static class Program
{
    [STAThread]
    public static void Main(string[] argv)
    {
        int port = 3333;
        if (argv.Length == 1)
        {
            int p;
            if (int.TryParse(argv[0], out p) && p != 0) port = p;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TuioDemo(port));
    }
}

// ============================================================
//  Main Window
// ============================================================
public class TuioDemo : Form, TuioListener
{
    // -- TUIO -------------------------------------------------
    private TuioClient client;
    private Dictionary<long, TuioObject> objectList = new Dictionary<long, TuioObject>(32);
    private Dictionary<long, TuioCursor> cursorList = new Dictionary<long, TuioCursor>(32);
    private Dictionary<long, TuioBlob> blobList = new Dictionary<long, TuioBlob>(32);

    // -- Screen -----------------------------------------------
    public static int width = 1280;
    public static int height = 800;
    private int screenW = Screen.PrimaryScreen.Bounds.Width;
    private int screenH = Screen.PrimaryScreen.Bounds.Height;
    private bool fullscreen = false;
    private int winLeft, winTop, winW = 1280, winH = 800;

    // -- App State --------------------------------------------
    private enum AppState { SignIn, SignUp, StoryBuilder }
    private AppState state = AppState.SignIn;

    // sign-in
    private float owlAngleAtPlace = -999f;
    private bool owlPresent = false;
    private bool signInOk = false;
    private float signInProgress = 0f;
    private string signInMessage = "Place the OWL marker and rotate it 45 degrees to sign in";

    // sign-up
    private string suName = "";
    private string suEmail = "";
    private string suPass = "";
    private int suFocused = 0;
    private string suStatus = "";

    // story builder
    private string storyText = "";
    private int sceneIndex = 0;
    private float sceneMood = 0f;

    // -- Animation --------------------------------------------
    private System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer();
    private int animTick = 0;
    private float bgParallax = 0f;

    // -- Assets -----------------------------------------------
    private string animalsDir;
    private Dictionary<string, Image[]> animalFrames = new Dictionary<string, Image[]>();
    private readonly string[] ANIMALS = { "lion", "fox", "eagle", "wolf", "owl" };
    private readonly string[] ANIMAL_NAMES = { "The Brave Lion", "The Clever Fox", "The Swift Eagle", "The Wild Wolf", "OWL (Sign-In)" };
    private readonly Color[] ANIMAL_COLORS =
    {
        Color.FromArgb(210, 132, 26),
        Color.FromArgb(232, 101, 26),
        Color.FromArgb(100, 160, 220),
        Color.FromArgb(120, 120, 142),
        Color.FromArgb(150, 50, 220)
    };

    // -- Scenes -----------------------------------------------
    private readonly string[] SCENES =
    {
        "The Enchanted Forest",
        "The Desert Kingdom",
        "The Sky Realm",
        "The Moonlit Tundra",
        "The Ancient Library"
    };
    private readonly Color[,] SCENE_GRAD =
    {
        { Color.FromArgb(10, 40,  20),  Color.FromArgb(30,  80,  50)  },
        { Color.FromArgb(60, 30,  10),  Color.FromArgb(120, 70,  20)  },
        { Color.FromArgb(10, 20,  80),  Color.FromArgb(30,  80,  180) },
        { Color.FromArgb(10, 10,  40),  Color.FromArgb(30,  30,  90)  },
        { Color.FromArgb(30, 20,  10),  Color.FromArgb(70,  50,  30)  },
    };

    // -- Fonts & Brushes --------------------------------------
    private Font fntTitle, fntBody, fntSmall, fntHuge;
    private Brush brWhite, brGold;

    // =========================================================
    //  Constructor
    // =========================================================
    public TuioDemo(int port)
    {
        animalsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "animals");

        this.ClientSize = new Size(width, height);
        this.Text = "Immersive Story Builder - TUIO";
        this.Name = "ImmersiveStoryBuilder";
        this.BackColor = Color.Black;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.DoubleBuffer, true);

        this.Closing += new CancelEventHandler(Form_Closing);
        this.KeyDown += new KeyEventHandler(Form_KeyDown);
        this.KeyPress += new KeyPressEventHandler(Form_KeyPress);

        InitFonts();
        LoadAnimalFrames();

        animTimer.Interval = 80;
        animTimer.Tick += (s, e) =>
        {
            animTick = (animTick + 1) % 10;
            bgParallax += 0.3f;
            Invalidate();
        };
        animTimer.Start();

        client = new TuioClient(port);
        client.addTuioListener(this);
        client.connect();
    }

    private void InitFonts()
    {
        fntHuge = new Font("Segoe UI", 34f, FontStyle.Bold);
        fntTitle = new Font("Segoe UI", 17f, FontStyle.Bold);
        fntBody = new Font("Segoe UI", 12f, FontStyle.Regular);
        fntSmall = new Font("Segoe UI", 9f, FontStyle.Regular);
        brWhite = new SolidBrush(Color.White);
        brGold = new SolidBrush(Color.FromArgb(255, 215, 0));
    }

    private void LoadAnimalFrames()
    {
        foreach (string a in ANIMALS)
        {
            Image[] frames = new Image[10];
            for (int i = 0; i < 10; i++)
            {
                string path = Path.Combine(animalsDir, a + (i + 1) + ".png");
                if (File.Exists(path))
                {
                    try { frames[i] = Image.FromFile(path); }
                    catch { frames[i] = null; }
                }
            }
            animalFrames[a] = frames;
        }
    }

    // =========================================================
    //  TUIO Callbacks
    // =========================================================
    public void addTuioObject(TuioObject o)
    {
        lock (objectList) objectList[o.SessionID] = o;
        if (o.SymbolID == 4 && state == AppState.SignIn)
        {
            owlPresent = true;
            owlAngleAtPlace = o.Angle;
            signInMessage = "Now rotate the OWL marker 45 degrees to authenticate...";
        }
    }

    public void updateTuioObject(TuioObject o)
    {
        lock (objectList) objectList[o.SessionID] = o;

        if (o.SymbolID == 4 && state == AppState.SignIn && owlPresent)
        {
            float delta = Math.Abs(o.Angle - owlAngleAtPlace);
            if (delta > (float)Math.PI) delta = (float)(2 * Math.PI) - delta;
            float threshold = (float)(Math.PI / 4); // 45 degrees
            signInProgress = Math.Min(delta / threshold, 1f);

            if (signInProgress >= 1f && !signInOk)
            {
                signInOk = true;
                signInMessage = "Authenticated! Welcome back, Storyteller!";
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1800;
                t.Tick += (s, e2) => { t.Stop(); state = AppState.StoryBuilder; Invalidate(); };
                t.Start();
            }
        }

        // Rotation on story markers 0-3 drives scene
        if (o.SymbolID >= 0 && o.SymbolID <= 3 && state == AppState.StoryBuilder)
        {
            sceneMood = o.Angle / (float)(2 * Math.PI);
            sceneIndex = (int)(sceneMood * SCENES.Length) % SCENES.Length;
        }
    }

    public void removeTuioObject(TuioObject o)
    {
        lock (objectList) objectList.Remove(o.SessionID);
        if (o.SymbolID == 4) { owlPresent = false; signInProgress = 0f; }
    }

    public void addTuioCursor(TuioCursor c) { lock (cursorList) cursorList[c.SessionID] = c; }
    public void updateTuioCursor(TuioCursor c) { lock (cursorList) cursorList[c.SessionID] = c; }
    public void removeTuioCursor(TuioCursor c) { lock (cursorList) cursorList.Remove(c.SessionID); }
    public void addTuioBlob(TuioBlob b) { lock (blobList) blobList[b.SessionID] = b; }
    public void updateTuioBlob(TuioBlob b) { lock (blobList) blobList[b.SessionID] = b; }
    public void removeTuioBlob(TuioBlob b) { lock (blobList) blobList.Remove(b.SessionID); }
    public void refresh(TuioTime t) { Invalidate(); }

    // =========================================================
    //  Paint Dispatch
    // =========================================================
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        switch (state)
        {
            case AppState.SignIn: DrawSignIn(g); break;
            case AppState.SignUp: DrawSignUp(g); break;
            case AppState.StoryBuilder: DrawStoryBuilder(g); break;
        }
    }

    // =========================================================
    //  SIGN IN
    // =========================================================
    private void DrawSignIn(Graphics g)
    {
        DrawAnimatedBg(g, Color.FromArgb(5, 5, 30), Color.FromArgb(20, 10, 60));
        DrawParticles(g, Color.FromArgb(60, 100, 100, 255));

        int pw = 560, ph = 490;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        DrawGlassPanel(g, px, py, pw, ph, Color.FromArgb(80, 20, 20, 80));

        // Title
        DrawCentredText(g, fntHuge, brGold, "Immersive Story Builder", py + 28);
        DrawCentredText(g, fntBody, brWhite, "An Interactive TUIO Experience", py + 80);

        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 2f))
            g.DrawLine(pLine, px + 40, py + 112, px + pw - 40, py + 112);

        // OWL animation
        DrawAnimalAt(g, "owl", width / 2 - 70, py + 128, 140, animTick, 0f);

        // Status text
        DrawCentredText(g, fntBody, brWhite, signInMessage, py + 290);

        // Progress ring + percentage
        if (owlPresent)
        {
            int rx = width / 2, ry = py + 338;
            int rr = 38;
            using (Pen pRing = new Pen(Color.FromArgb(60, 255, 255, 255), 6f))
                g.DrawEllipse(pRing, rx - rr, ry - rr, rr * 2, rr * 2);

            if (signInProgress > 0f)
            {
                float arc = signInProgress * 360f;
                using (Pen pArc = new Pen(Color.FromArgb(255, 215, 0), 6f))
                {
                    pArc.StartCap = LineCap.Round;
                    pArc.EndCap = LineCap.Round;
                    g.DrawArc(pArc, rx - rr, ry - rr, rr * 2, rr * 2, -90f, arc);
                }
            }
            string pct = ((int)(signInProgress * 100)) + "%";
            Brush pctBr = signInProgress >= 1f ? brGold : brWhite;
            DrawCentredText(g, fntBody, pctBr, pct, ry - 10);
            if (signInOk)
                DrawCentredText(g, fntTitle, brGold, "ACCESS GRANTED", ry + 48);
        }

        // Marker legend
        DrawMarkerLegend(g, px + 20, py + ph - 70);

        // Sign-up hint
        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(180, 180, 255)),
            "Don't have an account?  Press [S] to Sign Up", py + ph - 28);

        DrawTuioObjects(g);
    }

    // =========================================================
    //  SIGN UP
    // =========================================================
    private void DrawSignUp(Graphics g)
    {
        DrawAnimatedBg(g, Color.FromArgb(5, 20, 10), Color.FromArgb(10, 50, 30));
        DrawParticles(g, Color.FromArgb(50, 80, 255, 120));

        int pw = 520, ph = 510;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        DrawGlassPanel(g, px, py, pw, ph, Color.FromArgb(80, 10, 40, 20));

        DrawCentredText(g, fntHuge, brGold, "Create Account", py + 26);
        DrawCentredText(g, fntBody, brWhite, "Join the Story Builder world", py + 78);
        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 2f))
            g.DrawLine(pLine, px + 40, py + 108, px + pw - 40, py + 108);

        DrawField(g, px + 60, py + 128, pw - 120, 52, "Username", suName, suFocused == 0);
        DrawField(g, px + 60, py + 208, pw - 120, 52, "Email", suEmail, suFocused == 1);
        string maskedPass = new string('*', suPass.Length);
        DrawField(g, px + 60, py + 288, pw - 120, 52, "Password", maskedPass, suFocused == 2);

        DrawButton(g, px + 60, py + 368, pw - 120, 52, "REGISTER", Color.FromArgb(40, 180, 80));

        if (suStatus.Length > 0)
        {
            Brush sb = suStatus.Contains("Success")
                ? brGold
                : new SolidBrush(Color.Salmon);
            DrawCentredText(g, fntBody, sb, suStatus, py + 434);
        }

        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(180, 180, 255)),
            "[Tab] next field   [Enter] register   [Esc] back to Sign In", py + ph - 26);
    }

    // =========================================================
    //  STORY BUILDER
    // =========================================================
    private void DrawStoryBuilder(Graphics g)
    {
        int si = Math.Max(0, Math.Min(sceneIndex, SCENES.Length - 1));
        DrawAnimatedBg(g, SCENE_GRAD[si, 0], SCENE_GRAD[si, 1]);
        DrawStars(g, si);

        // Top bar
        DrawGlassPanel(g, 0, 0, width, 58, Color.FromArgb(120, 0, 0, 0));
        g.DrawString("IMMERSIVE STORY BUILDER", fntTitle, brGold, 18, 14);
        string sceneLabel = "Scene: " + SCENES[si];
        SizeF slSz = g.MeasureString(sceneLabel, fntBody);
        g.DrawString(sceneLabel, fntBody, brWhite, (width - slSz.Width) / 2, 16);
        g.DrawString("[F1] Fullscreen   [Esc] Return",
            fntSmall, new SolidBrush(Color.FromArgb(160, 200, 200, 200)), width - 225, 16);

        // Left panel – characters
        DrawGlassPanel(g, 8, 66, 218, height - 76, Color.FromArgb(100, 0, 0, 40));
        g.DrawString("Characters", fntTitle, brGold, 18, 74);
        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 1.5f))
            g.DrawLine(pLine, 18, 100, 218, 100);

        for (int i = 0; i < 4; i++)
        {
            int cy2 = 108 + i * 152;
            DrawAnimalAt(g, ANIMALS[i], 18, cy2, 90, animTick, 0f);
            g.DrawString(ANIMAL_NAMES[i], fntSmall,
                new SolidBrush(ANIMAL_COLORS[i]), 116, cy2 + 28);
            g.DrawString("Marker #" + i, fntSmall, brWhite, 116, cy2 + 48);
        }

        // Centre canvas
        int canX = 236, canY = 66, canW = width - 472, canH = height - 90;
        DrawGlassPanel(g, canX, canY, canW, canH, Color.FromArgb(60, 0, 0, 20));
        DrawTuioStoryMarkers(g, canX, canY, canW, canH);
        DrawMoodBar(g, canX + 16, canY + canH - 48, canW - 32, 28, sceneMood);

        if (storyText.Length > 0)
        {
            DrawGlassPanel(g, canX + 16, canY + canH - 128, canW - 32, 72, Color.FromArgb(180, 0, 0, 0));
            g.DrawString(storyText, fntBody, brWhite,
                new RectangleF(canX + 26, canY + canH - 122, canW - 52, 60));
        }

        // Right panel – scenes
        DrawGlassPanel(g, width - 228, 66, 220, height - 76, Color.FromArgb(100, 0, 0, 40));
        g.DrawString("Scenes", fntTitle, brGold, width - 216, 74);
        using (Pen pLine2 = new Pen(Color.FromArgb(255, 215, 0), 1.5f))
            g.DrawLine(pLine2, width - 216, 100, width - 18, 100);

        for (int i = 0; i < SCENES.Length; i++)
        {
            bool active = (i == si);
            Brush scBr = active ? brGold : brWhite;
            string prefix = active ? "> " : "  ";
            g.DrawString(prefix + SCENES[i], fntSmall, scBr, width - 216, 110 + i * 44);
            if (active)
            {
                using (Pen pSel = new Pen(Color.FromArgb(255, 215, 0), 1.5f))
                    g.DrawRectangle(pSel, width - 220, 106 + i * 44, 214, 34);
            }
        }

        // Instructions
        g.DrawString("Instructions", fntTitle, brGold, width - 216, 356);
        using (Pen pLine3 = new Pen(Color.FromArgb(255, 215, 0), 1.5f))
            g.DrawLine(pLine3, width - 216, 380, width - 18, 380);
        string[] hints =
        {
            "Place markers 0-3",
            "on surface to add",
            "characters.",
            "",
            "Rotate any marker",
            "to switch scenes.",
            "",
            "Marker #4 (OWL)",
            "= Sign In only."
        };
        for (int i = 0; i < hints.Length; i++)
            g.DrawString(hints[i], fntSmall, brWhite, width - 216, 390 + i * 18);

        DrawTuioCursors(g);
    }

    // =========================================================
    //  Drawing Helpers
    // =========================================================
    private void DrawAnimatedBg(Graphics g, Color c1, Color c2)
    {
        using (LinearGradientBrush br = new LinearGradientBrush(
            new Rectangle(0, 0, width, height), c1, c2, 135f))
        {
            g.FillRectangle(br, 0, 0, width, height);
        }
    }

    private void DrawParticles(Graphics g, Color col)
    {
        Random rng = new Random(42);
        for (int i = 0; i < 40; i++)
        {
            float x = (rng.Next(width) + bgParallax * (0.2f + i * 0.01f)) % width;
            float y = (rng.Next(height) + bgParallax * 0.1f * (i % 5)) % height;
            float r = 1.5f + (i % 3);
            using (SolidBrush pb = new SolidBrush(col))
                g.FillEllipse(pb, x - r, y - r, r * 2, r * 2);
        }
    }

    private void DrawStars(Graphics g, int idx)
    {
        Random rng = new Random(idx * 7 + 13);
        for (int i = 0; i < 80; i++)
        {
            float x = rng.Next(width);
            float y = 60 + rng.Next(height - 60);
            double bright = 0.4 + 0.6 * Math.Sin(animTick * 0.6 + i);
            int alpha = (int)(bright * 200);
            float r = 1 + rng.Next(2);
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(alpha, 255, 255, 220)))
                g.FillEllipse(sb, x - r, y - r, r * 2, r * 2);
        }
    }

    private void DrawGlassPanel(Graphics g, int x, int y, int w, int h, Color fill)
    {
        using (SolidBrush br = new SolidBrush(fill))
            GFX.FillRoundedRect(g, br, x, y, w, h, 14);
        using (Pen p = new Pen(Color.FromArgb(55, 255, 255, 255), 1f))
            GFX.DrawRoundedRect(g, p, x, y, w, h, 14);
    }

    private void DrawCentredText(Graphics g, Font f, Brush b, string text, int y)
    {
        SizeF sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, (width - sz.Width) / 2, y);
    }

    private void DrawField(Graphics g, int x, int y, int w, int h,
                           string label, string value, bool focused)
    {
        Color border = focused ? Color.FromArgb(255, 215, 0) : Color.FromArgb(100, 200, 200, 200);
        using (SolidBrush bg = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            GFX.FillRoundedRect(g, bg, x, y, w, h, 8);
        using (Pen p = new Pen(border, focused ? 2f : 1f))
            GFX.DrawRoundedRect(g, p, x, y, w, h, 8);
        g.DrawString(label, fntSmall, new SolidBrush(Color.FromArgb(160, 200, 200, 255)), x + 10, y + 4);
        g.DrawString(value, fntBody, brWhite, x + 10, y + 22);
        if (focused && animTick % 2 == 0)
        {
            SizeF vs = g.MeasureString(value + "|", fntBody);
            g.DrawString("|", fntBody, brGold, x + 10 + vs.Width - 14, y + 22);
        }
    }

    private void DrawButton(Graphics g, int x, int y, int w, int h, string label, Color color)
    {
        using (LinearGradientBrush br = new LinearGradientBrush(
            new Rectangle(x, y, w, h),
            Color.FromArgb(200, color),
            Color.FromArgb(120, color), 90f))
        {
            GFX.FillRoundedRect(g, br, x, y, w, h, 10);
        }
        using (Pen p = new Pen(color, 2f))
            GFX.DrawRoundedRect(g, p, x, y, w, h, 10);
        SizeF ls = g.MeasureString(label, fntTitle);
        g.DrawString(label, fntTitle, brWhite, x + (w - ls.Width) / 2, y + (h - ls.Height) / 2);
    }

    private void DrawMoodBar(Graphics g, int x, int y, int w, int h, float mood)
    {
        using (SolidBrush bg = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
            GFX.FillRoundedRect(g, bg, x, y, w, h, 6);
        int fw = (int)(w * mood);
        if (fw > 2)
        {
            using (LinearGradientBrush br = new LinearGradientBrush(
                new Rectangle(x, y, w, h),
                Color.FromArgb(100, 60, 255),
                Color.FromArgb(255, 215, 0), 0f))
            {
                GFX.FillRoundedRect(g, br, x, y, fw, h, 6);
            }
        }
        g.DrawString("Scene Mood: " + (int)(mood * 100) + "%  (rotate marker to change)",
            fntSmall, brWhite, x + 8, y + 6);
    }

    private void DrawMarkerLegend(Graphics g, int x, int y)
    {
        g.DrawString("Legend:", fntSmall, brGold, x, y);
        for (int i = 0; i < ANIMALS.Length; i++)
        {
            using (SolidBrush b = new SolidBrush(ANIMAL_COLORS[i]))
                g.FillEllipse(b, x + 60 + i * 90, y, 12, 12);
            g.DrawString("#" + i + " " + char.ToUpper(ANIMALS[i][0]),
                fntSmall, brWhite, x + 76 + i * 90, y);
        }
    }

    private void DrawAnimalAt(Graphics g, string animal, int x, int y,
                              int size, int tick, float angleDeg)
    {
        Image[] frames;
        if (!animalFrames.TryGetValue(animal, out frames)) return;
        Image frame = frames[tick % 10];
        if (frame == null) return;

        GraphicsState gs = g.Save();
        g.TranslateTransform(x + size / 2f, y + size / 2f);
        g.RotateTransform(angleDeg);
        g.DrawImage(frame, -size / 2, -size / 2, size, size);
        g.Restore(gs);
    }

    private void DrawTuioStoryMarkers(Graphics g, int cx, int cy, int cw, int ch)
    {
        lock (objectList)
        {
            foreach (TuioObject obj in objectList.Values)
            {
                if (obj.SymbolID > 3) continue;
                int ox = cx + (int)(obj.X * cw);
                int oy = cy + (int)(obj.Y * ch);
                float angleDeg = (float)(obj.Angle / Math.PI * 180.0);
                int sz = 110;

                // Glow ring
                using (SolidBrush gb = new SolidBrush(Color.FromArgb(40, ANIMAL_COLORS[obj.SymbolID])))
                    g.FillEllipse(gb, ox - sz / 2 - 16, oy - sz / 2 - 16, sz + 32, sz + 32);
                using (Pen gp = new Pen(Color.FromArgb(120, ANIMAL_COLORS[obj.SymbolID]), 3f))
                    g.DrawEllipse(gp, ox - sz / 2 - 16, oy - sz / 2 - 16, sz + 32, sz + 32);

                DrawAnimalAt(g, ANIMALS[obj.SymbolID], ox - sz / 2, oy - sz / 2, sz, animTick, angleDeg);

                // Name tag
                string tag = ANIMAL_NAMES[obj.SymbolID] + "  " + (int)angleDeg + " deg";
                SizeF ts = g.MeasureString(tag, fntSmall);
                using (SolidBrush tagBg = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    GFX.FillRoundedRect(g, tagBg,
                        (int)(ox - ts.Width / 2 - 6), oy + sz / 2 + 4,
                        (int)ts.Width + 12, 20, 4);
                g.DrawString(tag, fntSmall,
                    new SolidBrush(ANIMAL_COLORS[obj.SymbolID]),
                    ox - ts.Width / 2, oy + sz / 2 + 6);

                storyText = BuildStorySnippet(obj.SymbolID, sceneIndex);
            }
        }
    }

    private void DrawTuioObjects(Graphics g)
    {
        lock (objectList)
        {
            foreach (TuioObject obj in objectList.Values)
            {
                int ox = obj.getScreenX(width);
                int oy = obj.getScreenY(height);
                float ang = (float)(obj.Angle / Math.PI * 180.0);
                int idx = Math.Min(obj.SymbolID, ANIMALS.Length - 1);
                DrawAnimalAt(g, ANIMALS[idx], ox - 40, oy - 40, 80, animTick, ang);
            }
        }
    }

    private void DrawTuioCursors(Graphics g)
    {
        lock (cursorList)
        {
            foreach (TuioCursor c in cursorList.Values)
            {
                int cx2 = c.getScreenX(width), cy2 = c.getScreenY(height);
                using (SolidBrush cb = new SolidBrush(Color.FromArgb(160, 200, 50, 255)))
                    g.FillEllipse(cb, cx2 - 12, cy2 - 12, 24, 24);
                using (Pen cp = new Pen(Color.FromArgb(180, 140, 255), 2f))
                    g.DrawEllipse(cp, cx2 - 12, cy2 - 12, 24, 24);
                g.DrawString(c.CursorID.ToString(), fntSmall, brWhite, cx2 + 14, cy2 - 8);
            }
        }
    }

    private string BuildStorySnippet(int markerId, int scene)
    {
        string[] chars = { "The Brave Lion", "The Clever Fox", "The Swift Eagle", "The Wild Wolf" };
        string[] verbs = { "roamed", "explored", "soared above", "howled across" };
        string name = chars[Math.Min(markerId, 3)];
        string verb = verbs[Math.Min(markerId, 3)];
        string sname = SCENES[Math.Min(scene, SCENES.Length - 1)];
        return name + " " + verb + " " + sname + " in search of an ancient secret...";
    }

    // =========================================================
    //  Keyboard
    // =========================================================
    private void Form_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F1)
        {
            ToggleFullscreen();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            if (state == AppState.StoryBuilder) state = AppState.SignIn;
            else if (state == AppState.SignUp) state = AppState.SignIn;
            else this.Close();
        }
        else if (e.KeyCode == Keys.S && state == AppState.SignIn)
        {
            state = AppState.SignUp;
        }
        else if (e.KeyCode == Keys.Tab && state == AppState.SignUp)
        {
            suFocused = (suFocused + 1) % 3;
        }
        else if (e.KeyCode == Keys.Back && state == AppState.SignUp)
        {
            if (suFocused == 0 && suName.Length > 0) suName = suName.Substring(0, suName.Length - 1);
            if (suFocused == 1 && suEmail.Length > 0) suEmail = suEmail.Substring(0, suEmail.Length - 1);
            if (suFocused == 2 && suPass.Length > 0) suPass = suPass.Substring(0, suPass.Length - 1);
        }
        else if (e.KeyCode == Keys.Enter && state == AppState.SignUp)
        {
            if (suName.Length > 0 && suEmail.Length > 0 && suPass.Length > 0)
            {
                suStatus = "Success! Welcome, " + suName + "! Please sign in with the OWL marker.";
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 2200;
                t.Tick += (s2, e2) => { t.Stop(); state = AppState.SignIn; Invalidate(); };
                t.Start();
            }
            else
            {
                suStatus = "Please fill in all fields.";
            }
        }
        Invalidate();
    }

    private void Form_KeyPress(object sender, KeyPressEventArgs e)
    {
        if (state != AppState.SignUp) return;
        char c = e.KeyChar;
        if (c < 32) return;
        if (suFocused == 0) suName += c;
        if (suFocused == 1) suEmail += c;
        if (suFocused == 2) suPass += c;
        Invalidate();
    }

    private void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            winLeft = this.Left; winTop = this.Top;
            winW = width; winH = height;
            width = screenW; height = screenH;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Left = 0; this.Top = 0;
            this.Width = screenW; this.Height = screenH;
            fullscreen = true;
        }
        else
        {
            width = winW; height = winH;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.Left = winLeft; this.Top = winTop;
            this.Width = winW; this.Height = winH;
            fullscreen = false;
        }
    }

    private void Form_Closing(object sender, CancelEventArgs e)
    {
        animTimer.Stop();
        client.removeTuioListener(this);
        client.disconnect();
        Environment.Exit(0);
    }
}

// ============================================================
//  GFX: rounded rectangle helpers (C# 7.3 compatible)
// ============================================================
public static class GFX
{
    public static void FillRoundedRect(Graphics g, Brush b, int x, int y, int w, int h, int r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        using (GraphicsPath path = BuildPath(x, y, w, h, r))
            g.FillPath(b, path);
    }

    public static void DrawRoundedRect(Graphics g, Pen p, int x, int y, int w, int h, int r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        using (GraphicsPath path = BuildPath(x, y, w, h, r))
            g.DrawPath(p, path);
    }

    private static GraphicsPath BuildPath(int x, int y, int w, int h, int r)
    {
        GraphicsPath path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
