using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Threading;
using System.Text;
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
    private Dictionary<long, TuioBlob> blobList    = new Dictionary<long, TuioBlob>(32);

    // -- Screen -----------------------------------------------
    public static int width  = 1280;
    public static int height = 800;
    private int screenW = Screen.PrimaryScreen.Bounds.Width;
    private int screenH = Screen.PrimaryScreen.Bounds.Height;
    private bool fullscreen = false;
    private int winLeft, winTop, winW = 1280, winH = 800;

    // -- App State --------------------------------------------
    private enum AppState { SignIn, SignUp, StoryBuilder }
    private AppState state = AppState.SignIn;

    // =========================================================
    //  SIGN-IN state
    // =========================================================
    // signInMarkerId:  10 = guest marker, or the user's own saved ID
    private int   signInMarkerId      = -1;       // which marker is currently being used to log in
    private float signInAngleAtPlace  = -999f;
    private bool  signInMarkerPresent = false;
    private bool  signInOk            = false;
    private float signInProgress      = 0f;
    private string signInMessage      = "Place marker 10 (guest) or your personal ID marker and rotate 45°";

    // Logged-in session info
    private string loggedInUser = null;   // null = Guest, otherwise the user's name from users.txt

    // Registered users  (loaded from users.txt at startup and refreshed on each login)
    private Dictionary<int, string> registeredUsers = new Dictionary<int, string>();

    // =========================================================
    //  SIGN-UP state  (TUIO-based, fully redesigned)
    // =========================================================
    //
    //  HOW IT WORKS
    //  ─────────────
    //  Phase 0 – Idle/Show instructions
    //  Phase 1 – Build name:
    //      • Any marker 0-25 that is placed (addTuioObject) appends letter A-Z.
    //      • The marker must then be REMOVED before the next letter is accepted.
    //      • A confirm marker (held on screen for CONFIRM_HOLD_MS ms) submits the name.
    //  Phase 2 – Confirm & save:
    //      • Auto-assign next user ID.
    //      • Write to users.txt.
    //      • Display the assigned ID to the user.
    //
    //  MARKER LEGEND (Sign-Up)
    //  ─────────────────────────────
    //  0-25  → letters A-Z   (place to append letter; remove marker before next)
    //  24    → BACKSPACE     (removes last letter, shared with 'Y')
    //  25    → CONFIRM / DONE (hold for 2 s to submit name; or rotate 45° quickly)
    //  ─────────────────────────────

    private const int CONFIRM_MARKER   = 25;   // marker that confirms the name
    private const int BACKSPACE_MARKER = 24;   // same marker used for letter Y also deletes last char if name > 0

    private enum SignUpPhase { Idle, NameEntry, Done }
    private SignUpPhase suPhase = SignUpPhase.Idle;

    private string suName      = "";          // name being typed
    private int    suAssignedId = -1;         // auto-incremented ID assigned on success
    private string suStatus    = "";          // status / feedback message

    // Confirm-hold tracking
    private long   suConfirmSessionId  = -1;  // session of the confirm marker currently on table
    private DateTime suConfirmPlacedAt = DateTime.MinValue;
    private const int CONFIRM_HOLD_MS  = 2000; // hold 2 s to confirm

    // Prevent re-adding the same marker instance twice without removing it first
    private HashSet<int> suMarkersOnTable = new HashSet<int>(); // by SymbolID

    // Users file
    private readonly string USERS_FILE;

    // -- Animation --------------------------------------------
    private System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer();
    private int   animTick  = 0;
    private float bgParallax = 0f;

    // -- Assets -----------------------------------------------
    private string animalsDir;
    private Dictionary<string, Image[]> animalFrames = new Dictionary<string, Image[]>();
    private readonly string[] ANIMALS      = { "lion", "fox", "eagle", "wolf" };
    private readonly string[] ANIMAL_NAMES = { "The Brave Lion", "The Clever Fox", "The Swift Eagle", "The Wild Wolf" };
    private readonly Color[]  ANIMAL_COLORS =
    {
        Color.FromArgb(210, 132, 26),
        Color.FromArgb(232, 101, 26),
        Color.FromArgb(100, 160, 220),
        Color.FromArgb(120, 120, 142),
        Color.FromArgb(150,  50, 220)
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
        { Color.FromArgb(10, 20,  80),  Color.FromArgb(30,  80, 180)  },
        { Color.FromArgb(10, 10,  40),  Color.FromArgb(30,  30,  90)  },
        { Color.FromArgb(30, 20,  10),  Color.FromArgb(70,  50,  30)  },
    };

    private int    sceneIndex = 0;
    private float  sceneMood  = 0f;
    private string storyText  = "";

    // -- Marker 33 = pointer / mouse for scene selection ------
    private bool  mk33Present = false;
    private float mk33X = 0f, mk33Y = 0f;  // normalized TUIO coords (0-1)
    private int   mk33HoverScene = -1;      // which scene row is being hovered (-1 = none)

    // -- Sockets & Bluetooth --------------------------------
    private SocketClient socketClient;
    private Thread socketThread;
    private bool   isSocketActive = true;
    private string lastSocketMsg  = "";
    private List<string[]> discoveredBtDevices = new List<string[]>(); // string[0]=name, string[1]=address
    private string selectedBtAddr  = null;
    private string selectedBtName  = "None Selected";
    private int    btHoverIndex    = -1;

    // -- Fonts & Brushes --------------------------------------
    private Font  fntTitle, fntBody, fntSmall, fntHuge, fntMono;
    private Brush brWhite, brGold;

    // =========================================================
    //  Constructor
    // =========================================================
    public TuioDemo(int port)
    {
        animalsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "animals");
        USERS_FILE = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "users.txt");

        this.ClientSize = new Size(width, height);
        this.Text       = "Immersive Story Builder - TUIO";
        this.Name       = "ImmersiveStoryBuilder";
        this.BackColor  = Color.Black;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint            |
            ControlStyles.DoubleBuffer, true);

        this.Closing += new CancelEventHandler(Form_Closing);
        this.KeyDown += new KeyEventHandler(Form_KeyDown);

        InitFonts();
        LoadAnimalFrames();

        animTimer.Interval = 80;
        animTimer.Tick += (s, e) =>
        {
            animTick  = (animTick + 1) % 10;
            bgParallax += 0.3f;
            // Check confirm-hold timer each tick
            if (state == AppState.SignUp && suPhase == SignUpPhase.NameEntry &&
                suConfirmSessionId != -1 && suName.Length > 0)
            {
                double held = (DateTime.Now - suConfirmPlacedAt).TotalMilliseconds;
                if (held >= CONFIRM_HOLD_MS)
                    ConfirmSignUp();
            }
            Invalidate();
        };
        animTimer.Start();

        registeredUsers = LoadUsers(); // initial load

        client = new TuioClient(port);
        client.addTuioListener(this);
        client.connect();

        // Start socket listener thread
        socketThread = new Thread(new ThreadStart(StartSocketClient));
        socketThread.IsBackground = true;
        socketThread.Start();
    }

    private void StartSocketClient()
    {
        socketClient = new SocketClient();
        if (socketClient.Connect("localhost", 5000))
        {
            // Tell the server which Bluetooth addresses we care about
            RefreshWatchList();

            while (isSocketActive)
            {
                string rawData = socketClient.ReceiveMessage();
                if (string.IsNullOrEmpty(rawData)) break;
                if (rawData == "q") break;

                // Handle multiple messages in one packet (framed by \n)
                string[] messages = rawData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string msg in messages)
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        HandleSocketMessage(msg);
                    });
                }
            }
        }
    }

    private void HandleSocketMessage(string msg)
    {
        lastSocketMsg = msg;
        string raw = msg;
        msg = msg.Trim();

        if (msg.StartsWith("BT_LIST:"))
        {
            // Format: BT_LIST:Name1|Addr1,Name2|Addr2...
            discoveredBtDevices.Clear();
            string list = msg.Substring(8);
            if (!string.IsNullOrEmpty(list))
            {
                foreach (string item in list.Split(','))
                {
                    string[] parts = item.Split('|');
                    if (parts.Length == 2) discoveredBtDevices.Add(parts);
                }
            }
            Invalidate();
        }
        else if (msg.StartsWith("AUTOLOGIN:"))
        {
            // Format: AUTOLOGIN:MarkerID
            int mkr;
            if (int.TryParse(msg.Substring(10).Trim(), out mkr))
            {
                if (state == AppState.SignIn && !signInOk)
                {
                    signInMessage = "Bluetooth Active! Logging in as User #" + mkr + "...";
                    Invalidate();
                    DoLogin(mkr);
                }
            }
        }
        else 
        {
            msg = msg.ToLower();
            if (msg == "next")
            {
                sceneIndex = (sceneIndex + 1) % SCENES.Length;
                Invalidate();
            }
            else if (msg == "prev" || msg == "previous")
            {
                sceneIndex = (sceneIndex - 1 + SCENES.Length) % SCENES.Length;
                Invalidate();
            }
            else if (msg == "signin" && state == AppState.SignIn)
            {
                DoLogin(10); // Login as guest by default on remote signin command
            }
        }
    }

    private void UpdateBtSelectionFromPointer()
    {
        int sx = (int)(mk33X * width);
        int sy = (int)(mk33Y * height);

        int pw = 1040, ph = 560;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        int btx = px + 680, bty = py + 120, btw = 320;

        btHoverIndex = -1;
        if (sx >= btx + 10 && sx <= btx + btw - 10)
        {
            for (int j = 0; j < Math.Min(6, discoveredBtDevices.Count); j++)
            {
                int rowY = bty + 40 + j * 44;
                if (sy >= rowY && sy <= rowY + 38)
                {
                    btHoverIndex = j;
                    selectedBtAddr = discoveredBtDevices[j][1];
                    selectedBtName = discoveredBtDevices[j][0];
                    break;
                }
            }
        }
    }

    // =========================================================
    //  Font / Asset init
    // =========================================================
    private void InitFonts()
    {
        fntHuge  = new Font("Segoe UI", 34f, FontStyle.Bold);
        fntTitle = new Font("Segoe UI", 17f, FontStyle.Bold);
        fntBody  = new Font("Segoe UI", 12f, FontStyle.Regular);
        fntSmall = new Font("Segoe UI",  9f, FontStyle.Regular);
        fntMono  = new Font("Courier New", 13f, FontStyle.Bold);
        brWhite  = new SolidBrush(Color.White);
        brGold   = new SolidBrush(Color.FromArgb(255, 215, 0));
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
    //  Users file helpers
    // =========================================================
    /// <summary>Reads users.txt and returns a dictionary of ID → Name.</summary>
    private Dictionary<int, string> LoadUsers()
    {
        var dict = new Dictionary<int, string>();
        if (!File.Exists(USERS_FILE)) return dict;
        foreach (string line in File.ReadAllLines(USERS_FILE))
        {
            string[] parts = line.Split('|');
            int id;
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out id))
            {
                dict[id] = parts[1].Trim();
            }
        }
        return dict;
    }

    /// <summary>Returns address of a user by ID from users.txt (ID|Name|BTAddress)</summary>
    private string GetUserBtAddress(int id)
    {
        if (!File.Exists(USERS_FILE)) return null;
        foreach (string line in File.ReadAllLines(USERS_FILE))
        {
            string[] parts = line.Split('|');
            int uid;
            if (parts.Length >= 3 && int.TryParse(parts[0].Trim(), out uid) && uid == id)
                return parts[2].Trim();
        }
        return null;
    }

    /// <summary>Returns the next available user ID (1-based, auto-incremented).</summary>
    private int NextUserId()
    {
        int max = 0;
        if (File.Exists(USERS_FILE))
        {
            foreach (string line in File.ReadAllLines(USERS_FILE))
            {
                // format: ID|Name
                string[] parts = line.Split('|');
                int id;
                if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out id))
                    if (id > max) max = id;
            }
        }
        return max + 1;
    }

    /// <summary>Appends a new user record to users.txt.</summary>
    private void SaveUser(int id, string name, string btAddr)
    {
        using (StreamWriter sw = new StreamWriter(USERS_FILE, true))
            sw.WriteLine(id + "|" + name + "|" + (btAddr ?? "NONE"));
        
        RefreshWatchList();
    }

    private void RefreshWatchList()
    {
        if (socketClient == null) return;
        
        List<string> watchlist = new List<string>();
        if (File.Exists(USERS_FILE))
        {
            foreach (string line in File.ReadAllLines(USERS_FILE))
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 3 && parts[2] != "NONE")
                {
                    // Add Format: BTAddress|ID
                    watchlist.Add(parts[2].Trim() + "|" + parts[0].Trim());
                }
            }
        }
        
        if (watchlist.Count > 0)
        {
            socketClient.SendMessage("WATCH_BT:" + string.Join(",", watchlist));
        }
    }

    // =========================================================
    //  Sign-Up logic helpers
    // =========================================================
    private void AppendLetter(int symbolId)
    {
        // markers 0-25 = A-Z
        char letter = (char)('A' + symbolId);
        suName += letter;
        suStatus = "Letter '" + letter + "' added.  Remove marker before next letter.";
    }

    private void ConfirmSignUp()
    {
        if (suName.Length == 0) { suStatus = "Please enter at least one letter first!"; return; }

        suAssignedId = NextUserId();
        SaveUser(suAssignedId, suName, selectedBtAddr);
        suPhase  = SignUpPhase.Done;
        suStatus = ""; // shown separately in Done screen
        // Reset confirm tracking
        suConfirmSessionId = -1;
    }

    // =========================================================
    //  TUIO Callbacks
    // =========================================================
    public void addTuioObject(TuioObject o)
    {
        lock (objectList) objectList[o.SessionID] = o;

        // ---- Navigation markers  (always checked first) -----
        // Marker 35 = go to Sign Up   (from Sign In screen)
        if (o.SymbolID == 35 && state == AppState.SignIn)
        {
            this.Invoke((MethodInvoker)delegate
            {
                suName             = "";
                suStatus           = "Place markers 0-24 to spell your name.  Marker 25 (hold 2 s) = DONE.";
                suAssignedId       = -1;
                suPhase            = SignUpPhase.NameEntry;
                suConfirmSessionId = -1;
                suMarkersOnTable.Clear();
                state = AppState.SignUp;
                selectedBtAddr = null;
                selectedBtName = "None Selected";
                Invalidate();
            });
            return;
        }

        // Marker 34 = back / Escape
        if (o.SymbolID == 34)
        {
            this.Invoke((MethodInvoker)delegate
            {
                if (state == AppState.StoryBuilder)
                {
                    state = AppState.SignIn;
                }
                else if (state == AppState.SignUp)
                {
                    // Works from both NameEntry and Done phases
                    suName             = "";
                    suStatus           = "";
                    suAssignedId       = -1;
                    suPhase            = SignUpPhase.Idle;
                    suConfirmSessionId = -1;
                    suMarkersOnTable.Clear();
                    state = AppState.SignIn;
                }
                Invalidate();
            });
            return;
        }

        // ---- Sign-In ----------------------------------------
        if (state == AppState.SignIn && !signInMarkerPresent)
        {
            // Reload users so newly registered accounts are found immediately
            registeredUsers = LoadUsers();

            bool isGuest = (o.SymbolID == 10);
            bool isUser  = registeredUsers.ContainsKey(o.SymbolID);

            if (isGuest || isUser)
            {
                signInMarkerId      = o.SymbolID;
                signInMarkerPresent = true;
                signInAngleAtPlace  = o.Angle;

                if (isGuest)
                    signInMessage = "Rotate marker 10 by 45° to continue as Guest...";
                else
                    signInMessage = "Hello, " + registeredUsers[o.SymbolID] + "!  Rotate your marker 45° to sign in...";
                return;
            }
        }

        // ---- Sign-Up ----------------------------------------
        if (state == AppState.SignUp && suPhase == SignUpPhase.NameEntry)
        {
            int sym = o.SymbolID;

            // Confirm marker (25) held tracking
            if (sym == CONFIRM_MARKER)
            {
                suConfirmSessionId  = o.SessionID;
                suConfirmPlacedAt   = DateTime.Now;
                suStatus = suName.Length > 0
                    ? "Hold marker 25 for 2 s to confirm name \"" + suName + "\"..."
                    : "Enter at least one letter first (markers 0-24 = A-Y).";
                return;
            }

            // Letter markers 0-24 (A-Y); marker 24 also acts as backspace
            if (sym >= 0 && sym <= 24)
            {
                // Backspace on marker 24 if name has letters
                if (sym == BACKSPACE_MARKER && suName.Length > 0)
                {
                    suName   = suName.Substring(0, suName.Length - 1);
                    suStatus = "Last letter removed.  Remove marker 24 before next action.";
                    suMarkersOnTable.Add(sym);
                    return;
                }

                // Prevent double-add of same marker without removing first
                if (suMarkersOnTable.Contains(sym))
                {
                    suStatus = "Remove marker " + sym + " first before placing it again.";
                    return;
                }

                suMarkersOnTable.Add(sym);
                AppendLetter(sym);
            }
        }
    }

    public void updateTuioObject(TuioObject o)
    {
        lock (objectList) objectList[o.SessionID] = o;

        // ---- Sign-In  (works for both guest marker 10 and personal user markers) ----
        if (state == AppState.SignIn && signInMarkerPresent && o.SymbolID == signInMarkerId)
        {
            float delta = Math.Abs(o.Angle - signInAngleAtPlace);
            if (delta > (float)Math.PI) delta = (float)(2 * Math.PI) - delta;
            float threshold = (float)(Math.PI / 4); // 45 degrees
            signInProgress = Math.Min(delta / threshold, 1f);

            if (signInProgress >= 1f && !signInOk)
            {
                DoLogin(signInMarkerId);
            }
        }

        // Rotation on story markers 0-3 drives scene
        if (o.SymbolID >= 0 && o.SymbolID <= 3 && state == AppState.StoryBuilder)
        {
            sceneMood  = o.Angle / (float)(2 * Math.PI);
            sceneIndex = (int)(sceneMood * SCENES.Length) % SCENES.Length;
        }

        // Marker 33 = pointer cursor in StoryBuilder
        if (o.SymbolID == 33)
        {
            mk33Present = true;
            mk33X = o.X;
            mk33Y = o.Y;
            if (state == AppState.StoryBuilder) UpdateSceneFromPointer();
            else if (state == AppState.SignUp) UpdateBtSelectionFromPointer();
        }
    }

    private void DoLogin(int mkrId)
    {
        signInOk = true;
        signInMarkerId = mkrId;
        // Resolve who logged in
        if (mkrId == 10)
        {
            loggedInUser = null; // guest
            signInMessage = "Continuing as Guest!  Welcome, Storyteller!";
        }
        else
        {
            registeredUsers = LoadUsers();
            loggedInUser = registeredUsers.ContainsKey(mkrId)
                            ? registeredUsers[mkrId]
                            : "User #" + mkrId;
            signInMessage = "Welcome back, " + loggedInUser + "!";
        }

        this.Invoke((MethodInvoker)delegate
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1800;
            t.Tick += (s, e2) => { t.Stop(); state = AppState.StoryBuilder; Invalidate(); };
            t.Start();
        });
    }

    public void removeTuioObject(TuioObject o)
    {
        lock (objectList) objectList.Remove(o.SessionID);

        // Reset sign-in tracking when the active login marker is removed
        if (state == AppState.SignIn && o.SymbolID == signInMarkerId && !signInOk)
        {
            signInMarkerPresent = false;
            signInProgress      = 0f;
            signInMarkerId      = -1;
            signInMessage       = "Place marker 10 (guest) or your personal ID marker and rotate 45\u00b0";
        }

        // Marker 33 pointer removed
        if (o.SymbolID == 33)
        {
            mk33Present   = false;
            mk33HoverScene = -1;
        }

        // Release marker so it can be placed again for the next letter
        if (state == AppState.SignUp && suPhase == SignUpPhase.NameEntry)
        {
            suMarkersOnTable.Remove(o.SymbolID);

            if (o.SymbolID == CONFIRM_MARKER && suConfirmSessionId == o.SessionID)
            {
                suConfirmSessionId = -1;
                if (suPhase != SignUpPhase.Done)
                    suStatus = suName.Length > 0
                        ? "Name so far: \"" + suName.ToUpper() + "\".  Place markers 0-24 to add letters."
                        : "Place markers 0-24 to spell your name.  Marker 25 (hold) = Done.";
            }
        }
    }

    public void addTuioCursor(TuioCursor c)  { lock (cursorList) cursorList[c.SessionID] = c; }
    public void updateTuioCursor(TuioCursor c){ lock (cursorList) cursorList[c.SessionID] = c; }
    public void removeTuioCursor(TuioCursor c){ lock (cursorList) cursorList.Remove(c.SessionID); }
    public void addTuioBlob(TuioBlob b)  { lock (blobList) blobList[b.SessionID] = b; }
    public void updateTuioBlob(TuioBlob b){ lock (blobList) blobList[b.SessionID] = b; }
    public void removeTuioBlob(TuioBlob b){ lock (blobList) blobList.Remove(b.SessionID); }
    public void refresh(TuioTime t) { Invalidate(); }

    // =========================================================
    //  Paint Dispatch
    // =========================================================
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;

        switch (state)
        {
            case AppState.SignIn:        DrawSignIn(g);       break;
            case AppState.SignUp:        DrawSignUp(g);       break;
            case AppState.StoryBuilder:  DrawStoryBuilder(g); break;
        }
    }

    // =========================================================
    //  SIGN IN
    // =========================================================
    private void DrawSignIn(Graphics g)
    {
        DrawAnimatedBg(g, Color.FromArgb(5, 5, 30), Color.FromArgb(20, 10, 60));
        DrawParticles(g, Color.FromArgb(60, 100, 100, 255));

        int pw = 680, ph = 530;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        DrawGlassPanel(g, px, py, pw, ph, Color.FromArgb(80, 20, 20, 80));

        DrawCentredText(g, fntHuge,  brGold,  "Immersive Story Builder", py + 22);
        DrawCentredText(g, fntBody,  brWhite, "An Interactive TUIO Experience", py + 72);

        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 2f))
            g.DrawLine(pLine, px + 40, py + 104, px + pw - 40, py + 104);

        // ---- Two login option boxes side by side ----
        int boxW = (pw - 100) / 2;   // width of each option box
        int boxH = 200;
        int boxY = py + 118;
        int guestX  = px + 30;
        int personalX = px + 50 + boxW;

        // Guest box
        bool guestActive = signInMarkerPresent && signInMarkerId == 10;
        Color guestFill  = guestActive
            ? Color.FromArgb(100, 30, 60, 130)
            : Color.FromArgb(50,  20, 20, 80);
        DrawGlassPanel(g, guestX, boxY, boxW, boxH, guestFill);
        if (guestActive)
        {
            using (Pen hp = new Pen(Color.FromArgb(255, 215, 0), 2f))
                g.DrawRectangle(hp, guestX, boxY, boxW, boxH);
        }
        using (Pen gp = new Pen(Color.FromArgb(100, 150, 150, 255), 3f))
            g.DrawEllipse(gp, guestX + boxW/2 - 30, boxY + 24, 60, 60);
        g.DrawString("10", fntTitle, brWhite,
            guestX + boxW/2 - 14, boxY + 40);
        DrawStringCentredIn(g, fntBody,  brGold,  "GUEST LOGIN",       guestX, boxY + 100, boxW);
        DrawStringCentredIn(g, fntSmall, brWhite, "Marker 10",         guestX, boxY + 128, boxW);
        DrawStringCentredIn(g, fntSmall, new SolidBrush(Color.FromArgb(180,180,255)),
            "Place + rotate 45°", guestX, boxY + 148, boxW);

        // Personal-account box
        registeredUsers = LoadUsers(); // refresh
        bool anyUsers    = registeredUsers.Count > 0;
        bool userActive  = signInMarkerPresent && signInMarkerId != 10;
        Color userFill   = userActive
            ? Color.FromArgb(100, 20, 80, 40)
            : Color.FromArgb(50,  10, 40, 20);
        DrawGlassPanel(g, personalX, boxY, boxW, boxH, userFill);
        if (userActive)
        {
            using (Pen hp = new Pen(Color.FromArgb(255, 215, 0), 2f))
                g.DrawRectangle(hp, personalX, boxY, boxW, boxH);
        }
        using (Pen up = new Pen(Color.FromArgb(100, 100, 220, 120), 3f))
            g.DrawEllipse(up, personalX + boxW/2 - 30, boxY + 24, 60, 60);
        string idLabel = userActive ? signInMarkerId.ToString() : "ID";
        g.DrawString(idLabel, fntTitle, userActive ? brGold : brWhite,
            personalX + boxW/2 - (idLabel.Length > 1 ? 18 : 10), boxY + 40);
        DrawStringCentredIn(g, fntBody,  brGold,  "ACCOUNT LOGIN",         personalX, boxY + 100, boxW);
        DrawStringCentredIn(g, fntSmall, brWhite, "Your personal marker",  personalX, boxY + 128, boxW);
        DrawStringCentredIn(g, fntSmall, new SolidBrush(anyUsers
            ? Color.FromArgb(180, 255, 200, 100)
            : Color.FromArgb(130, 180, 180, 180)),
            anyUsers ? (registeredUsers.Count + " account(s) registered") : "No accounts yet – Sign Up!",
            personalX, boxY + 148, boxW);

        // ---- Status / message ----
        DrawCentredText(g, fntBody, brWhite, signInMessage, boxY + boxH + 22);

        // ---- Progress ring (shared) ----
        if (signInMarkerPresent)
        {
            int rx = width / 2, ry = boxY + boxH + 80, rr = 34;
            using (Pen pRing = new Pen(Color.FromArgb(60, 255, 255, 255), 6f))
                g.DrawEllipse(pRing, rx - rr, ry - rr, rr * 2, rr * 2);

            if (signInProgress > 0f)
            {
                float arc = signInProgress * 360f;
                using (Pen pArc = new Pen(Color.FromArgb(255, 215, 0), 6f))
                {
                    pArc.StartCap = LineCap.Round;
                    pArc.EndCap   = LineCap.Round;
                    g.DrawArc(pArc, rx - rr, ry - rr, rr * 2, rr * 2, -90f, arc);
                }
            }
            string pct = ((int)(signInProgress * 100)) + "%";
            Brush pctBr = signInProgress >= 1f ? brGold : brWhite;
            DrawCentredText(g, fntBody, pctBr, pct, ry - 10);
            if (signInOk)
                DrawCentredText(g, fntTitle, brGold, "✓  ACCESS GRANTED", ry + 44);
        }

        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(180, 180, 255)),
            "No account?   Marker 35 = Sign Up", py + ph - 26);

        DrawTuioObjects(g);
    }

    // =========================================================
    //  SIGN UP  (fully TUIO-based)
    // =========================================================
    private void DrawSignUp(Graphics g)
    {
        DrawAnimatedBg(g, Color.FromArgb(5, 20, 5), Color.FromArgb(10, 50, 20));
        DrawParticles(g, Color.FromArgb(50, 80, 255, 120));

        if (suPhase == SignUpPhase.Done)
        {
            DrawSignUpDone(g);
            return;
        }

        // ---- Panel ----
        int pw = 1040, ph = 560;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        DrawGlassPanel(g, px, py, pw, ph, Color.FromArgb(80, 10, 40, 20));

        DrawCentredText(g, fntHuge, brGold, "Create Account", py + 22);
        DrawCentredText(g, fntBody, brWhite, "Spell your name & (Optional) Pick your Bluetooth device", py + 72);

        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 2f))
            g.DrawLine(pLine, px + 40, py + 104, px + pw - 40, py + 104);

        // ---- Name display box ----
        int nbx = px + 40, nby = py + 120, nbw = pw - 80, nbh = 70;
        DrawGlassPanel(g, nbx, nby, nbw, nbh, Color.FromArgb(60, 0, 0, 0));
        using (Pen p = new Pen(Color.FromArgb(255, 215, 0), 2f))
            g.DrawRectangle(p, nbx, nby, nbw, nbh);

        g.DrawString("Your Name:", fntSmall,
            new SolidBrush(Color.FromArgb(180, 200, 200, 255)), nbx + 10, nby + 6);

        string displayName = suName.Length > 0 ? suName : "_";
        // Blinking cursor
        string cursor = (animTick % 2 == 0) ? "|" : "";
        g.DrawString(displayName + cursor, fntMono, brGold, nbx + 12, nby + 28);

        // ---- Alphabet reference grid  (5 cols × 6 rows) ----
        int gridTop = py + 210;
        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(180, 180, 255)),
            "TUIO Marker → Letter  (place & remove marker to type, marker 24 = backspace, marker 25 = DONE)", gridTop - 20);

        int cols = 9, cellW = (pw - 80) / cols, cellH = 36;
        for (int i = 0; i <= 25; i++)
        {
            int col = i % cols;
            int row = i / cols;
            int cx  = px + 40 + col * cellW;
            int cy  = gridTop + row * cellH;

            bool onTable = suMarkersOnTable.Contains(i);
            bool isConfirm   = (i == CONFIRM_MARKER);
            bool isBackspace = (i == BACKSPACE_MARKER);

            Color cellCol;
            if (isConfirm)   cellCol = Color.FromArgb(onTable ? 180 : 80, 50, 200, 80);
            else if (isBackspace) cellCol = Color.FromArgb(onTable ? 180 : 80, 200, 100, 50);
            else             cellCol = Color.FromArgb(onTable ? 160 : 50, 50, 100, 180);

            using (SolidBrush cb = new SolidBrush(cellCol))
                g.FillRectangle(cb, cx + 2, cy + 2, cellW - 4, cellH - 4);

            // Border highlight when on table
            if (onTable)
            {
                using (Pen hp = new Pen(Color.FromArgb(255, 215, 0), 2f))
                    g.DrawRectangle(hp, cx + 2, cy + 2, cellW - 4, cellH - 4);
            }

            string idStr  = i.ToString();
            string lblStr;
            if (isConfirm)        lblStr = "✓ DONE";
            else if (isBackspace) lblStr = "← DEL";
            else                  lblStr = ((char)('A' + i)).ToString();

            g.DrawString(idStr,  fntSmall, new SolidBrush(Color.FromArgb(200, 200, 200, 200)), cx + 4,  cy + 3);
            g.DrawString(lblStr, fntBody,   onTable ? brGold : brWhite, cx + 18, cy + 14);
        }

        // ---- Bluetooth Selection (on the right) ----
        int btx = px + 680, bty = py + 120, btw = 320, bth = 330;
        DrawGlassPanel(g, btx, bty, btw, bth, Color.FromArgb(60, 0, 0, 0));
        g.DrawString("Nearby Bluetooth Devices:", fntSmall, brGold, btx + 10, bty + 10);
        
        for (int j = 0; j < Math.Min(6, discoveredBtDevices.Count); j++)
        {
            int rowY = bty + 40 + j * 44;
            bool hovering = (j == btHoverIndex);
            bool selected = (discoveredBtDevices[j][1] == selectedBtAddr);
            
            Color rowCol = selected ? Color.FromArgb(120, 255, 215, 0) : 
                           hovering ? Color.FromArgb(80, 100, 100, 100) : 
                           Color.FromArgb(40, 50, 50, 50);
                           
            using (SolidBrush rb = new SolidBrush(rowCol))
                g.FillRectangle(rb, btx + 10, rowY, btw - 20, 38);
                
            g.DrawString(discoveredBtDevices[j][0], fntSmall, brWhite, btx + 18, rowY + 4);
            g.DrawString(discoveredBtDevices[j][1], fntSmall, Color.FromArgb(150, 200, 200, 255).ToSolidBrush(), btx + 18, rowY + 20);
        }
        
        g.DrawString("Selected: " + selectedBtName, fntSmall, brGold, btx + 10, bty + bth - 30);

        // ---- Confirm-hold progress bar ----
        if (suConfirmSessionId != -1 && suName.Length > 0)
        {
            double held    = (DateTime.Now - suConfirmPlacedAt).TotalMilliseconds;
            float  holdPct = Math.Min((float)(held / CONFIRM_HOLD_MS), 1f);
            int    barY    = py + ph - 110;
            int    barW    = pw - 80;

            DrawCentredText(g, fntBody, brWhite, "Hold marker 25 to confirm…", barY - 22);

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                g.FillRectangle(bg, px + 40, barY, barW, 24);

            int fw = (int)(barW * holdPct);
            if (fw > 4)
            {
                using (LinearGradientBrush br2 = new LinearGradientBrush(
                    new Rectangle(px + 40, barY, barW, 24),
                    Color.FromArgb(80, 200, 80),
                    Color.FromArgb(255, 215, 0), 0f))
                {
                    g.FillRectangle(br2, px + 40, barY, fw, 24);
                }
            }
            string holdLbl = ((int)(holdPct * 100)) + "%";
            DrawCentredText(g, fntSmall, brWhite, holdLbl, barY + 4);
        }

        // ---- Status line ----
        if (suStatus.Length > 0)
        {
            Brush sb = suStatus.StartsWith("Please") || suStatus.StartsWith("Remove")
                ? new SolidBrush(Color.Salmon)
                : new SolidBrush(Color.FromArgb(180, 255, 200, 80));
            DrawCentredText(g, fntBody, sb, suStatus, py + ph - 68);
        }

        // ---- Hint footer ----
        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(140, 180, 180, 255)),
            "Marker 34 = Back   |   Place & remove markers to spell name   |   Marker 25 = DONE (hold 2 s)",
            py + ph - 26);

        DrawTuioObjects(g);
    }

    // -- Done screen ------------------------------------------
    private void DrawSignUpDone(Graphics g)
    {
        int pw = 580, ph = 380;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        DrawGlassPanel(g, px, py, pw, ph, Color.FromArgb(100, 20, 80, 20));

        DrawCentredText(g, fntHuge, brGold, "Account Created!", py + 30);

        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 2f))
            g.DrawLine(pLine, px + 40, py + 90, px + pw - 40, py + 90);

        // Name
        DrawCentredText(g, fntBody, brWhite, "Welcome,", py + 112);
        DrawCentredText(g, fntTitle, brGold, suName.ToUpper(), py + 142);

        // ID box – very prominent
        int ibx = px + 80, iby = py + 198, ibw = pw - 160, ibh = 84;
        using (LinearGradientBrush ibBr = new LinearGradientBrush(
            new Rectangle(ibx, iby, ibw, ibh),
            Color.FromArgb(120, 0, 80, 0),
            Color.FromArgb(120, 0, 160, 40), 90f))
        {
            g.FillRectangle(ibBr, ibx, iby, ibw, ibh);
        }
        using (Pen ibP = new Pen(Color.FromArgb(255, 215, 0), 2.5f))
            g.DrawRectangle(ibP, ibx, iby, ibw, ibh);

        DrawCentredText(g, fntBody, brWhite, "Your TUIO Login ID  (remember this!)", iby + 8);
        DrawCentredText(g, fntHuge, brGold,  suAssignedId.ToString(), iby + 34);

        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(200, 180, 255, 180)),
            "Use your ID marker to sign in next time.", py + ph - 68);
        DrawCentredText(g, fntSmall,
            new SolidBrush(Color.FromArgb(160, 180, 180, 255)),
            "Marker 34 = Return to Sign In", py + ph - 38);
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

        // Show logged-in user name (or Guest) in top-right
        string userLabel = loggedInUser != null
            ? "\u2713  " + loggedInUser
            : "\u2713  Guest";
        Brush userBrush = loggedInUser != null
            ? brGold
            : new SolidBrush(Color.FromArgb(180, 200, 200, 200));
        SizeF ulSz = g.MeasureString(userLabel, fntSmall);
        g.DrawString(userLabel, fntSmall, userBrush, width - ulSz.Width - 12, 8);
        g.DrawString("[F1] Fullscreen   [Esc] Return",
            fntSmall, new SolidBrush(Color.FromArgb(160, 200, 200, 200)), width - 225, 28);

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
            Brush scBr  = active ? brGold : brWhite;
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
            "Marker #33",
            "= scene pointer.",
            "",
            "Marker #34 = Back",
        };
        for (int i = 0; i < hints.Length; i++)
            g.DrawString(hints[i], fntSmall, brWhite, width - 216, 390 + i * 18);

        // Draw marker-33 pointer cursor on top of everything
        if (mk33Present)
            DrawMk33Pointer(g);

        DrawTuioCursors(g);
    }

    // =========================================================
    //  Drawing Helpers
    // =========================================================

    // ---- Marker-33 scene pointer ----------------------------
    /// <summary>
    /// Maps mk33X/Y (0-1 TUIO coords) to screen, checks which scene row
    /// in the right panel is under the pointer, selects it, and draws an
    /// animated crosshair cursor.
    /// </summary>
    private void UpdateSceneFromPointer()
    {
        int sx = (int)(mk33X * width);
        int sy = (int)(mk33Y * height);

        mk33HoverScene = -1;
        // Right panel scene hit zones: x in [width-220, width-6], row y at 106+i*44, height 34
        if (sx >= width - 220 && sx <= width - 6)
        {
            for (int i = 0; i < SCENES.Length; i++)
            {
                int rowY = 106 + i * 44;
                if (sy >= rowY && sy <= rowY + 34)
                {
                    mk33HoverScene = i;
                    sceneIndex     = i;   // immediately select the scene
                    break;
                }
            }
        }
    }

    private void DrawMk33Pointer(Graphics g)
    {
        int cx = (int)(mk33X * width);
        int cy = (int)(mk33Y * height);

        // Glow halo
        using (SolidBrush halo = new SolidBrush(Color.FromArgb(30, 255, 215, 0)))
            g.FillEllipse(halo, cx - 24, cy - 24, 48, 48);

        // Outer ring – pulses every other tick
        int rr = (animTick % 2 == 0) ? 18 : 16;
        using (Pen ring = new Pen(Color.FromArgb(200, 255, 215, 0), 2f))
            g.DrawEllipse(ring, cx - rr, cy - rr, rr * 2, rr * 2);

        // Crosshair lines
        int arm = 12;
        using (Pen cr = new Pen(Color.FromArgb(220, 255, 215, 0), 1.5f))
        {
            g.DrawLine(cr, cx - arm - 4, cy, cx + arm + 4, cy);
            g.DrawLine(cr, cx, cy - arm - 4, cx, cy + arm + 4);
        }

        // Centre dot
        using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 255, 215, 0)))
            g.FillEllipse(dot, cx - 3, cy - 3, 6, 6);

        // Tooltip: show hovered scene name
        if (mk33HoverScene >= 0)
        {
            string tip = "Select: " + SCENES[mk33HoverScene];
            SizeF  tsz = g.MeasureString(tip, fntSmall);
            int tx = cx + 14, ty = cy - 18;
            // keep tooltip on screen
            if (tx + tsz.Width > width - 10) tx = cx - (int)tsz.Width - 14;
            if (ty < 4) ty = cy + 10;

            using (SolidBrush tipBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                g.FillRectangle(tipBg, tx - 4, ty - 2, (int)tsz.Width + 8, (int)tsz.Height + 4);
            g.DrawString(tip, fntSmall, brGold, tx, ty);
        }
        else
        {
            // dim hint when not over a scene row
            string tip = "Mkr 33";
            SizeF  tsz = g.MeasureString(tip, fntSmall);
            g.DrawString(tip, fntSmall,
                new SolidBrush(Color.FromArgb(140, 200, 200, 200)), cx + 14, cy - 8);
        }
    }
    private void DrawAnimatedBg(Graphics g, Color c1, Color c2)
    {
        using (LinearGradientBrush br = new LinearGradientBrush(
            new Rectangle(0, 0, width, height), c1, c2, 135f))
            g.FillRectangle(br, 0, 0, width, height);
    }

    private void DrawParticles(Graphics g, Color col)
    {
        Random rng = new Random(42);
        for (int i = 0; i < 40; i++)
        {
            float x = (rng.Next(width)  + bgParallax * (0.2f + i * 0.01f)) % width;
            float y = (rng.Next(height) + bgParallax * 0.1f * (i % 5))     % height;
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
            int alpha = Math.Max(0, Math.Min(255, (int)(bright * 200)));
            float r = 1 + rng.Next(2);
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(alpha, 255, 255, 220)))
                g.FillEllipse(sb, x - r, y - r, r * 2, r * 2);
        }
    }

    private void DrawGlassPanel(Graphics g, int x, int y, int w, int h, Color fill)
    {
        using (SolidBrush br = new SolidBrush(fill))
            g.FillRectangle(br, x, y, w, h);
        using (Pen p = new Pen(Color.FromArgb(55, 255, 255, 255), 1f))
            g.DrawRectangle(p, x, y, w, h);
    }

    private void DrawCentredText(Graphics g, Font f, Brush b, string text, int y)
    {
        SizeF sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, (width - sz.Width) / 2, y);
    }

    /// <summary>Draws text centred within a box that starts at boxX and is boxW wide.</summary>
    private void DrawStringCentredIn(Graphics g, Font f, Brush b, string text, int boxX, int y, int boxW)
    {
        SizeF sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, boxX + (boxW - sz.Width) / 2, y);
    }

    private void DrawMoodBar(Graphics g, int x, int y, int w, int h, float mood)
    {
        using (SolidBrush bg = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
            g.FillRectangle(bg, x, y, w, h);
        int fw = (int)(w * mood);
        if (fw > 2)
        {
            using (LinearGradientBrush br = new LinearGradientBrush(
                new Rectangle(x, y, w, h),
                Color.FromArgb(100, 60, 255),
                Color.FromArgb(255, 215, 0), 0f))
                g.FillRectangle(br, x, y, fw, h);
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
                int   ox = cx + (int)(obj.X * cw);
                int   oy = cy + (int)(obj.Y * ch);
                float angleDeg = (float)(obj.Angle / Math.PI * 180.0);
                int   sz = 110;

                using (SolidBrush gb = new SolidBrush(Color.FromArgb(40, ANIMAL_COLORS[obj.SymbolID])))
                    g.FillEllipse(gb, ox - sz / 2 - 16, oy - sz / 2 - 16, sz + 32, sz + 32);
                using (Pen gp = new Pen(Color.FromArgb(120, ANIMAL_COLORS[obj.SymbolID]), 3f))
                    g.DrawEllipse(gp, ox - sz / 2 - 16, oy - sz / 2 - 16, sz + 32, sz + 32);

                DrawAnimalAt(g, ANIMALS[obj.SymbolID], ox - sz / 2, oy - sz / 2, sz, animTick, angleDeg);

                string tag = ANIMAL_NAMES[obj.SymbolID] + "  " + (int)angleDeg + " deg";
                SizeF  ts  = g.MeasureString(tag, fntSmall);
                using (SolidBrush tagBg = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    g.FillRectangle(tagBg,
                        (int)(ox - ts.Width / 2 - 6), oy + sz / 2 + 4,
                        (int)ts.Width + 12, 20);
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
                int   ox  = obj.getScreenX(width);
                int   oy  = obj.getScreenY(height);
                float ang = (float)(obj.Angle / Math.PI * 180.0);
                if (obj.SymbolID < ANIMALS.Length)
                {
                    DrawAnimalAt(g, ANIMALS[obj.SymbolID], ox - 40, oy - 40, 80, animTick, ang);
                }
                else
                {
                    using (Pen p = new Pen(Color.White, 2f))
                        g.DrawEllipse(p, ox - 40, oy - 40, 80, 80);
                    g.DrawString(obj.SymbolID.ToString(), fntSmall, brWhite, ox - 10, oy - 10);
                }
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
        string name  = chars[Math.Min(markerId, 3)];
        string verb  = verbs[Math.Min(markerId, 3)];
        string sname = SCENES[Math.Min(scene, SCENES.Length - 1)];
        return name + " " + verb + " " + sname + " in search of an ancient secret...";
    }

    // =========================================================
    //  Keyboard  (F1 only — all navigation is handled by TUIO markers)
    // =========================================================
    private void Form_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F1)
            ToggleFullscreen();

        Invalidate();
    }

    // =========================================================
    //  Fullscreen toggle
    // =========================================================
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
        isSocketActive = false;
        animTimer.Stop();
        client.removeTuioListener(this);
        client.disconnect();
        Environment.Exit(0);
    }
}

// ============================================================
//  Socket Client Helper
// ============================================================
public class SocketClient
{
    private TcpClient client;
    private NetworkStream stream;

    public bool Connect(string host, int port)
    {
        try
        {
            client = new TcpClient(host, port);
            stream = client.GetStream();
            Console.WriteLine("Socket connected to " + host + ":" + port);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Socket connection failed: " + ex.Message);
            return false;
        }
    }

    public string ReceiveMessage()
    {
        try
        {
            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0) return null;
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Socket read error: " + ex.Message);
            return null;
        }
    }

    public void SendMessage(string msg)
    {
        try
        {
            if (stream == null || !client.Connected) return;
            byte[] data = Encoding.UTF8.GetBytes(msg + "\n");
            stream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Socket write error: " + ex.Message);
        }
    }
}

public static class ColorExtensions
{
    public static SolidBrush ToSolidBrush(this Color color)
    {
        return new SolidBrush(color);
    }
}


