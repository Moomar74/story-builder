using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
    private enum AppState { SignIn, SignUp, StorySelection, StoryPlayer, StoryBuilder }
    private AppState _state = AppState.SignIn;
    private AppState state 
    { 
        get { return _state; } 
        set 
        { 
            if (_state != value)
            {
                _state = value; 
                SendGazePageState();  // Notify gaze server of page change
            }
        } 
    }

    // =========================================================
    //  TEACHER MODE (PACT: Teacher Persona - Mena)
    // =========================================================
    private bool   isTeacherMode = false;           // True if a teacher is logged in
    private int    teacherMarkerId = 36;             // Marker 36 = Teacher login
    private string teacherName = "Teacher";

    // =========================================================
    //  STUDENT PROGRESS TRACKING (PACT: Evaluate student skill)
    // =========================================================
    private int    studentChallengesCompleted = 0;
    private int    studentScenesCompleted = 0;
    private DateTime studentSessionStart = DateTime.MinValue;
    private string attendanceLogFile;

    // =========================================================
    //  STORY PLAYER state + Animation Engine
    // =========================================================
    private int    spStoryIndex     = -1;
    private int    spSceneIndex     = 0;
    private int    spDialogueIndex  = 0;
    private bool   spInChallenge    = false;
    private bool   spChallengeComplete = false;
    private int    spSuccessTimer   = 0;
    private string spFeedback       = "";
    private float  spChallengeRotateStart = -999f;
    private bool   spChallengeMarkerWasPlaced = false;
    private int    spStorySelectHover = -1;

    // Animation phases
    private enum ScenePhase { WALK_IN, TALK, OBSTACLE, CHALLENGE, OBSTACLE_REMOVE, WALK_CONTINUE, SCENE_END }
    private ScenePhase spPhase = ScenePhase.WALK_IN;

    // Character positions (pixel X on screen, Y is ground-based)
    private float spChar1X = -200f;       // main character X
    private float spChar1TargetX = 200f;  // where char1 is walking to
    private float spChar2X = 900f;        // second character X (walks in from right)
    private float spChar2TargetX = 700f;
    private bool  spChar2Visible = false;
    private int   spCharGroundY;          // Y position (calculated from height)
    private float spCharSpeed = 4.5f;       // pixels per tick

    // Obstacle/door animation
    private float spObstacleX = 500f;     // obstacle position
    private float spObstacleAlpha = 255f; // 255=fully visible, fading to 0
    private bool  spObstacleVisible = true;

    // Walking animation frame
    private int   spWalkFrame = 0;
    private bool  spCharFlipped = false;  // face right by default

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
    private string loggedInUser = null;   // null = Guest, otherwise the user's name from database
    private int loggedInUserId = -1;      // Database ID of logged-in user

    // Database Manager (replaces users.txt)
    private DatabaseManager db;
    private TeacherPanel teacherPanel = null;

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
        { Color.LightGreen, Color.ForestGreen },
        { Color.SandyBrown, Color.Orange },
        { Color.LightSkyBlue, Color.DeepSkyBlue },
        { Color.PowderBlue, Color.SteelBlue },
        { Color.BurlyWood, Color.SaddleBrown },
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

    // -- Story Assets -----------------------------------------
    private string storyAssetsDir;
    private Dictionary<string, Image> storyImageCache = new Dictionary<string, Image>();

    // -- Gesture Server (MediaPipe + DollarPy) ----------------
    private TcpClient gestureSocket;
    private Thread gestureThread;
    private bool gestureConnected = false;

    // Skeleton landmarks (normalized 0-1)
    private Dictionary<string, float[]> skeleton = new Dictionary<string, float[]>();
    private bool skeletonVisible = false;

    // Last recognized gesture
    private string lastGesture = "";
    private DateTime lastGestureTime = DateTime.MinValue;

    // Circular menu
    private bool circMenuVisible = false;
    private string[] circMenuItems = { "Story 1", "Story 2", "Story 3", "Story 4", "Back", "Continue" };
    private int circMenuHover = -1;
    private int circMenuSelected = -1;
    private DateTime circMenuSelectTime = DateTime.MinValue;

    // -- Gaze Tracking Server (dlib + Heatmaps) ---------------
    private TcpClient gazeSocket;
    private Thread gazeThread;
    private bool gazeConnected = false;
    private float gazeX = 0.5f, gazeY = 0.5f;  // Normalized gaze position (0-1)
    private bool gazeTracking = false;
    private bool isBlinking = false;
    private DateTime lastGazeTime = DateTime.MinValue;

    // Heatmap data per page - stores gaze points as normalized coordinates
    private Dictionary<string, List<PointF>> gazeHeatmapData = new Dictionary<string, List<PointF>>
    {
        { "SignIn", new List<PointF>() },
        { "SignUp", new List<PointF>() },
        { "StorySelection", new List<PointF>() },
        { "StoryPlayer", new List<PointF>() },
        { "StoryBuilder", new List<PointF>() }
    };
    private bool heatmapVisible = false;
    private int heatmapGridSize = 20;  // Size of each heatmap cell in pixels
    private Bitmap[] heatmapCache = new Bitmap[5];  // Cached heatmap images for each page
    private string[] heatmapPageNames = { "SignIn", "SignUp", "StorySelection", "StoryPlayer", "StoryBuilder" };

    // =========================================================
    //  Constructor
    // =========================================================
    public TuioDemo(int port)
    {
        animalsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "animals");
        storyAssetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "story_assets");
        attendanceLogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "attendance_log.txt");

        // Initialize Database Manager
        db = new DatabaseManager(AppDomain.CurrentDomain.BaseDirectory);
        // Migrate from old users.txt if it exists
        string oldUsersFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "users.txt");
        if (File.Exists(oldUsersFile))
        {
            db.MigrateFromOldFormat(oldUsersFile);
        }

        this.ClientSize = new Size(width, height);
        this.Text       = "Immersive Story Builder - TUIO";
        this.Name       = "ImmersiveStoryBuilder";
        this.BackColor  = Color.Black;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint            |
            ControlStyles.DoubleBuffer, true);

        this.Closing += new CancelEventHandler(Form_Closing);
        // Note: All interactions are now TUIO-based (markers), no keyboard shortcuts

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

            // Story animation tick
            if (state == AppState.StoryPlayer && spStoryIndex >= 0)
            {
                spCharGroundY = height - 300;
                spWalkFrame = (spWalkFrame + 1) % 20;

                // Move char1 toward target
                if (Math.Abs(spChar1X - spChar1TargetX) > spCharSpeed)
                {
                    spChar1X += (spChar1TargetX > spChar1X) ? spCharSpeed : -spCharSpeed;
                    spCharFlipped = (spChar1TargetX > spChar1X);
                }

                // Move char2 toward target
                if (spChar2Visible && Math.Abs(spChar2X - spChar2TargetX) > spCharSpeed)
                    spChar2X += (spChar2TargetX > spChar2X) ? spCharSpeed : -spCharSpeed;

                // Phase transitions
                if (spPhase == ScenePhase.WALK_IN && Math.Abs(spChar1X - spChar1TargetX) <= spCharSpeed)
                {
                    spChar1X = spChar1TargetX;
                    spPhase = ScenePhase.TALK;
                    spDialogueIndex = 0;
                }
                else if (spPhase == ScenePhase.OBSTACLE_REMOVE)
                {
                    spObstacleAlpha -= 12f;
                    if (spObstacleAlpha <= 0)
                    {
                        spObstacleAlpha = 0;
                        spObstacleVisible = false;
                        spPhase = ScenePhase.WALK_CONTINUE;
                        spChar1TargetX = width + 200; // walk off screen
                        spSuccessTimer = 60; // show success for a bit
                    }
                }
                else if (spPhase == ScenePhase.WALK_CONTINUE)
                {
                    if (spSuccessTimer > 0) spSuccessTimer--;
                    if (spChar1X > width + 150)
                    {
                        spPhase = ScenePhase.SCENE_END;
                        // Auto advance to next scene
                        spSceneIndex++;
                        ResetSceneAnimation();
                    }
                }
            }

            Invalidate();
        };
        animTimer.Start();

        client = new TuioClient(port);
        client.addTuioListener(this);
        client.connect();

        // Start socket listener thread
        socketThread = new Thread(new ThreadStart(StartSocketClient));
        socketThread.IsBackground = true;
        socketThread.Start();

        // Start gesture listener thread
        gestureThread = new Thread(new ThreadStart(StartGestureClient));
        gestureThread.IsBackground = true;
        gestureThread.Start();

        // Start gaze tracking listener thread
        gazeThread = new Thread(new ThreadStart(StartGazeClient));
        gazeThread.IsBackground = true;
        gazeThread.Start();
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
        else if (msg.StartsWith("ATTENDANCE_LOGGED:"))
        {
            // PACT Requirement: Teacher (Mena) wants to track attendance automatically
            int mkr;
            if (int.TryParse(msg.Substring(18).Trim(), out mkr))
            {
                var user = db.GetUserById(mkr);
                string uName = user?.Name ?? "User #" + mkr;
                signInMessage = "✓ Attendance logged for " + uName + "!";
                Invalidate();
            }
        }
        else if (msg.StartsWith("AUTOLOGOUT:"))
        {
            // PACT Scenario Step 12: System logs out automatically when student leaves room
            // Close attendance session in database
            if (currentAttendanceId >= 0)
            {
                db.EndAttendanceSession(currentAttendanceId, studentScenesCompleted, studentChallengesCompleted);
                currentAttendanceId = -1;
            }
            
            // Log the logout to attendance file for teacher records
            try
            {
                string logEntry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | Name: " + (loggedInUser ?? "Guest") + " | Status: Auto-Logout (Left Range)";
                File.AppendAllText(attendanceLogFile, logEntry + Environment.NewLine);
            }
            catch { }

            state = AppState.SignIn;
            signInOk = false;
            loggedInUser = null;
            loggedInUserId = -1;
            isTeacherMode = false;
            signInMessage = "Student out of range. Session closed automatically.";
            Invalidate();
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

    private void StartGestureClient()
    {
        while (isSocketActive)
        {
            try
            {
                gestureSocket = new TcpClient("localhost", 5001);
                gestureConnected = true;
                NetworkStream stream = gestureSocket.GetStream();
                byte[] buffer = new byte[8192];

                while (isSocketActive)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    
                    string rawData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    string[] messages = rawData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string msg in messages)
                    {
                        this.Invoke((MethodInvoker)delegate { HandleGestureMessage(msg); });
                    }
                }
            }
            catch (Exception)
            {
                gestureConnected = false;
                Thread.Sleep(2000); // Retry after 2 seconds
            }
        }
    }

    private void HandleGestureMessage(string msg)
    {
        msg = msg.Trim();
        if (msg.StartsWith("SKELETON:"))
        {
            // Simple string parsing instead of full JSON to avoid dependencies
            string json = msg.Substring(9);
            skeleton.Clear();
            string[] parts = json.Replace("{", "").Replace("}", "").Replace("\"", "").Split(',');
            foreach (string p in parts)
            {
                string[] kv = p.Split(':');
                if (kv.Length == 2)
                {
                    string key = kv[0].Trim();
                    string valArray = kv[1].Trim().Replace("[", "").Replace("]", "");
                    string[] coords = valArray.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (coords.Length == 2)
                    {
                        float x = 0, y = 0;
                        float.TryParse(coords[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
                        float.TryParse(coords[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
                        skeleton[key] = new float[] { x, y };
                    }
                }
            }
            skeletonVisible = true;
            Invalidate();
        }
        else if (msg.StartsWith("GESTURE:"))
        {
            lastGesture = msg.Substring(8);
            lastGestureTime = DateTime.Now;

            // Trigger action based on gesture
            if (state == AppState.StorySelection)
            {
                if (lastGesture == "swipe_right") spStoryIndex = Math.Min(3, spStoryIndex + 1);
                else if (lastGesture == "swipe_left") spStoryIndex = Math.Max(0, spStoryIndex - 1);
                Invalidate();
            }
            else if (state == AppState.StoryPlayer)
            {
                if (lastGesture == "swipe_right")
                {
                    if (spSceneIndex < StoryDatabase.AllStories[spStoryIndex].Scenes.Length - 1)
                    {
                        spSceneIndex++;
                        ResetSceneAnimation();
                        Invalidate();
                    }
                }
                else if (lastGesture == "swipe_left")
                {
                    if (spSceneIndex > 0)
                    {
                        spSceneIndex--;
                        ResetSceneAnimation();
                        Invalidate();
                    }
                }

                if (spPhase == ScenePhase.CHALLENGE && !spChallengeComplete)
                {
                    // Map gesture to challenge success
                    if (StoryDatabase.AllStories[spStoryIndex].Scenes[spSceneIndex].Challenge.RequiredMarkerId > 0)
                    {
                        // Any distinct gesture can "complete" the challenge if interacting via gesture!
                        if (lastGesture == "wave" || lastGesture == "circle" || lastGesture == "push" || lastGesture == "click")
                        {
                            CompleteChallenge();
                        }
                    }
                }
            }
        }
        else if (msg == "MENU_SHOW")
        {
            circMenuVisible = true;
            Invalidate();
        }
        else if (msg == "MENU_HIDE")
        {
            circMenuVisible = false;
            Invalidate();
        }
        else if (msg.StartsWith("MENU_HOVER:"))
        {
            string[] p = msg.Split(':');
            if (p.Length >= 2) int.TryParse(p[1], out circMenuHover);
            Invalidate();
        }
        else if (msg.StartsWith("MENU_SELECT:"))
        {
            string[] p = msg.Split(':');
            if (p.Length >= 2)
            {
                int idx;
                if (int.TryParse(p[1], out idx))
                {
                    circMenuSelected = idx;
                    circMenuSelectTime = DateTime.Now;
                    
                    // Menu actions
                    if (idx < 4) // Stories
                    {
                        spStoryIndex = idx;
                        spSceneIndex = 0;
                        ResetSceneAnimation();
                        state = AppState.StoryPlayer;
                    }
                    else if (idx == 4) // Back
                        state = AppState.StorySelection;
                    
                    circMenuVisible = false; // Auto close
                    Invalidate();
                }
            }
        }
    }

    // =========================================================
    //  Gaze Tracking Client (Port 5002)
    // =========================================================
    private void StartGazeClient()
    {
        while (isSocketActive)
        {
            try
            {
                gazeSocket = new TcpClient("localhost", 5002);
                gazeConnected = true;
                NetworkStream stream = gazeSocket.GetStream();
                byte[] buffer = new byte[8192];

                // Send initial page state
                SendGazePageState();

                while (isSocketActive)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    
                    string rawData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    string[] messages = rawData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string msg in messages)
                    {
                        this.Invoke((MethodInvoker)delegate { HandleGazeMessage(msg); });
                    }
                }
            }
            catch (Exception)
            {
                gazeConnected = false;
                Thread.Sleep(2000); // Retry after 2 seconds
            }
        }
    }

    private void HandleGazeMessage(string msg)
    {
        msg = msg.Trim();
        if (msg.StartsWith("GAZE:"))
        {
            // Parse GAZE:x,y
            string coords = msg.Substring(5);
            string[] parts = coords.Split(',');
            if (parts.Length == 2)
            {
                float x, y;
                if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y))
                {
                    gazeX = x;
                    gazeY = y;
                    lastGazeTime = DateTime.Now;
                    gazeTracking = true;
                    
                    // Store gaze point for current page's heatmap
                    string pageName = state.ToString();
                    if (gazeHeatmapData.ContainsKey(pageName))
                    {
                        gazeHeatmapData[pageName].Add(new PointF(x, y));
                        // Invalidate heatmap cache to regenerate
                        int pageIdx = Array.IndexOf(heatmapPageNames, pageName);
                        if (pageIdx >= 0) heatmapCache[pageIdx] = null;
                    }
                    
                    if (heatmapVisible) Invalidate();
                }
            }
        }
        else if (msg.StartsWith("BLINK:"))
        {
            string blinkStr = msg.Substring(6);
            int blinkVal;
            if (int.TryParse(blinkStr, out blinkVal))
            {
                isBlinking = (blinkVal == 1);
            }
        }
        else if (msg.StartsWith("GAZE_STATUS:"))
        {
            string status = msg.Substring(12);
            gazeTracking = (status == "tracking");
        }
    }

    private void SendGazePageState()
    {
        if (gazeSocket != null && gazeSocket.Connected)
        {
            try
            {
                string pageMsg = "PAGE:" + state.ToString() + "\n";
                byte[] data = Encoding.UTF8.GetBytes(pageMsg);
                gazeSocket.GetStream().Write(data, 0, data.Length);
            }
            catch { }
        }
    }

    // =========================================================
    //  Heatmap Generation and Rendering
    // =========================================================
    private Bitmap GenerateHeatmap(string pageName, int w, int h)
    {
        if (!gazeHeatmapData.ContainsKey(pageName) || gazeHeatmapData[pageName].Count == 0)
            return null;

        Bitmap heatmap = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(heatmap))
        {
            g.Clear(Color.Transparent);
            
            // Create a grid-based density map
            int gridW = w / heatmapGridSize + 1;
            int gridH = h / heatmapGridSize + 1;
            int[,] density = new int[gridH, gridW];
            
            // Count gaze points in each grid cell
            foreach (PointF p in gazeHeatmapData[pageName])
            {
                int gx = (int)(p.X * w) / heatmapGridSize;
                int gy = (int)(p.Y * h) / heatmapGridSize;
                if (gx >= 0 && gx < gridW && gy >= 0 && gy < gridH)
                    density[gy, gx]++;
            }
            
            // Find max density for normalization
            int maxDensity = 0;
            for (int y = 0; y < gridH; y++)
                for (int x = 0; x < gridW; x++)
                    maxDensity = Math.Max(maxDensity, density[y, x]);
            
            if (maxDensity == 0) return heatmap;
            
            // Draw heatmap cells with Gaussian blur effect
            for (int y = 0; y < gridH; y++)
            {
                for (int x = 0; x < gridW; x++)
                {
                    if (density[y, x] > 0)
                    {
                        float intensity = (float)density[y, x] / maxDensity;
                        int alpha = (int)(intensity * 200);  // Max 200 alpha for overlay
                        
                        // Color gradient: blue (low) -> green -> yellow -> red (high)
                        Color heatColor = GetHeatColor(intensity);
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, heatColor)))
                        {
                            // Draw slightly larger than grid for smoothing
                            int spread = 2;
                            g.FillEllipse(brush, 
                                x * heatmapGridSize - spread, 
                                y * heatmapGridSize - spread, 
                                heatmapGridSize + spread * 2, 
                                heatmapGridSize + spread * 2);
                        }
                    }
                }
            }
        }
        return heatmap;
    }

    private Color GetHeatColor(float intensity)
    {
        // Intensity: 0.0 to 1.0
        // Returns color from blue (cold) to red (hot)
        if (intensity < 0.25f)
        {
            // Blue to Cyan
            float t = intensity / 0.25f;
            return Color.FromArgb(0, (int)(255 * t), 255);
        }
        else if (intensity < 0.5f)
        {
            // Cyan to Green
            float t = (intensity - 0.25f) / 0.25f;
            return Color.FromArgb(0, 255, (int)(255 * (1 - t)));
        }
        else if (intensity < 0.75f)
        {
            // Green to Yellow
            float t = (intensity - 0.5f) / 0.25f;
            return Color.FromArgb((int)(255 * t), 255, 0);
        }
        else
        {
            // Yellow to Red
            float t = (intensity - 0.75f) / 0.25f;
            return Color.FromArgb(255, (int)(255 * (1 - t)), 0);
        }
    }

    private void DrawHeatmapOverlay(Graphics g)
    {
        if (!heatmapVisible) return;
        
        string pageName = state.ToString();
        int pageIdx = Array.IndexOf(heatmapPageNames, pageName);
        if (pageIdx < 0) return;
        
        // Generate heatmap if not cached
        if (heatmapCache[pageIdx] == null)
        {
            heatmapCache[pageIdx] = GenerateHeatmap(pageName, width, height);
        }
        
        if (heatmapCache[pageIdx] != null)
        {
            // Draw heatmap with transparency
            g.DrawImage(heatmapCache[pageIdx], 0, 0);
        }
        
        // Draw heatmap stats
        if (gazeHeatmapData.ContainsKey(pageName))
        {
            int count = gazeHeatmapData[pageName].Count;
            string statsText = $"Gaze Points: {count}";
            using (Font fnt = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (Brush bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            {
                SizeF sz = g.MeasureString(statsText, fnt);
                g.FillRectangle(bg, 10, height - 50, sz.Width + 20, 30);
                g.DrawString(statsText, fnt, Brushes.White, 20, height - 45);
            }
        }
    }

    private void ClearHeatmapData()
    {
        foreach (var key in gazeHeatmapData.Keys.ToList())
        {
            gazeHeatmapData[key].Clear();
        }
        for (int i = 0; i < heatmapCache.Length; i++)
            heatmapCache[i] = null;
        
        // Tell server to clear data
        if (gazeSocket != null && gazeSocket.Connected)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes("CLEAR_HEATMAP\n");
                gazeSocket.GetStream().Write(data, 0, data.Length);
            }
            catch { }
        }
        Invalidate();
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
        string fName = "Comic Sans MS";
        fntHuge  = new Font(fName, 36f, FontStyle.Bold);
        fntTitle = new Font(fName, 20f, FontStyle.Bold);
        fntBody  = new Font(fName, 14f, FontStyle.Bold);
        fntSmall = new Font(fName, 10f, FontStyle.Bold);
        fntMono  = new Font("Courier New", 14f, FontStyle.Bold);
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

    /// <summary>Loads an image from story_assets/ folder with caching.</summary>
    private Image LoadStoryImage(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        Image cached;
        if (storyImageCache.TryGetValue(filename, out cached)) return cached;
        string path = Path.Combine(storyAssetsDir, filename);
        if (File.Exists(path))
        {
            try
            {
                Image img = Image.FromFile(path);
                storyImageCache[filename] = img;
                return img;
            }
            catch { }
        }
        return null;
    }

    // =========================================================
    //  Database helpers (replaces users.txt file operations)
    // =========================================================
    /// <summary>Returns a dictionary of ID → Name for all users.</summary>
    private Dictionary<int, string> LoadUsers()
    {
        if (db == null) return new Dictionary<int, string>();
        return db.GetAllUsers().ToDictionary(u => u.Id, u => u.Name);
    }

    /// <summary>Returns Bluetooth address of a user by ID.</summary>
    private string GetUserBtAddress(int id)
    {
        if (db == null) return null;
        var user = db.GetUserById(id);
        return user?.BluetoothAddress;
    }

    /// <summary>Opens the Teacher Management Panel.</summary>
    private void OpenTeacherPanel()
    {
        if (teacherPanel == null || teacherPanel.IsDisposed)
        {
            teacherPanel = new TeacherPanel(db);
        }
        teacherPanel.Show();
        teacherPanel.BringToFront();
    }

    /// <summary>Checks if current user is a teacher.</summary>
    private bool IsCurrentUserTeacher()
    {
        if (db == null || loggedInUserId < 0) return false;
        return db.IsTeacher(loggedInUserId);
    }

    private void RefreshWatchList()
    {
        if (socketClient == null || db == null) return;
        
        List<string> watchlist = new List<string>();
        var users = db.GetAllUsers();
        foreach (var user in users)
        {
            if (!string.IsNullOrEmpty(user.BluetoothAddress))
            {
                // Add Format: BTAddress|ID
                watchlist.Add(user.BluetoothAddress + "|" + user.Id);
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

        try
        {
            var user = db.CreateUser(suName, "Student", selectedBtAddr);
            suAssignedId = user.Id;
            RefreshWatchList();
            suPhase  = SignUpPhase.Done;
            suStatus = ""; // shown separately in Done screen
            // Reset confirm tracking
            suConfirmSessionId = -1;
        }
        catch (Exception ex)
        {
            suStatus = "Error: " + ex.Message;
        }
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
                    state = AppState.StorySelection;
                }
                else if (state == AppState.StoryPlayer)
                {
                    state = AppState.StorySelection;
                }
                else if (state == AppState.StorySelection)
                {
                    state = AppState.SignIn;
                    signInOk = false;
                    signInMarkerPresent = false;
                    signInProgress = 0f;
                    signInMarkerId = -1;
                }
                else if (state == AppState.SignUp)
                {
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

        // ---- Marker 37 = Toggle Fullscreen (replaces F1) ----
        if (o.SymbolID == 37)
        {
            this.Invoke((MethodInvoker)delegate
            {
                ToggleFullscreen();
                Invalidate();
            });
            return;
        }

        // ---- Marker 38 = Toggle Heatmap (replaces F2) ----
        if (o.SymbolID == 38)
        {
            this.Invoke((MethodInvoker)delegate
            {
                heatmapVisible = !heatmapVisible;
                Invalidate();
            });
            return;
        }

        // ---- Marker 39 = Clear Heatmap (replaces F3) ----
        if (o.SymbolID == 39)
        {
            this.Invoke((MethodInvoker)delegate
            {
                ClearHeatmapData();
                Invalidate();
            });
            return;
        }

        // ---- Marker 40 = Open Teacher Panel (replaces F4, Teacher only) ----
        if (o.SymbolID == 40 && isTeacherMode)
        {
            this.Invoke((MethodInvoker)delegate
            {
                OpenTeacherPanel();
            });
            return;
        }

        // ---- Sign-In ----------------------------------------
        if (state == AppState.SignIn && !signInMarkerPresent)
        {
            // Check database for user - supports dynamic user lookup
            bool isGuest = (o.SymbolID == 10);
            bool isTeacher = (o.SymbolID == teacherMarkerId);
            var dbUser = db.GetUserById(o.SymbolID);
            bool isUser = dbUser != null;

            if (isGuest || isUser || isTeacher)
            {
                signInMarkerId      = o.SymbolID;
                signInMarkerPresent = true;
                signInAngleAtPlace  = o.Angle;

                if (isTeacher)
                    signInMessage = "Teacher mode: Place marker 36 and rotate 45° to sign in...";
                else if (isGuest)
                    signInMessage = "Rotate marker 10 by 45° to continue as Guest...";
                else
                    signInMessage = "Hello, " + dbUser.Name + "!  Rotate your marker 45° to sign in...";
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

        // ---- Story Selection: markers 0-3 select a story ----
        if (state == AppState.StorySelection && o.SymbolID >= 0 && o.SymbolID <= 3)
        {
            this.Invoke((MethodInvoker)delegate
            {
                spStoryIndex    = o.SymbolID;
                spSceneIndex    = 0;
                ResetSceneAnimation();
                state = AppState.StoryPlayer;
                Invalidate();
            });
            return;
        }

        // ---- Story Player: marker 35 = advance dialogue / next scene ----
        if (state == AppState.StoryPlayer && o.SymbolID == 35)
        {
            this.Invoke((MethodInvoker)delegate { AdvanceStoryDialogue(); });
            return;
        }

        // ---- Story Player: challenge markers (5-8) ----
        if (state == AppState.StoryPlayer && spInChallenge && !spChallengeComplete)
        {
            Story story = StoryDatabase.AllStories[spStoryIndex];
            StoryScene scene = story.Scenes[spSceneIndex];
            Challenge ch = scene.Challenge;
            if (o.SymbolID == ch.RequiredMarkerId)
            {
                if (ch.Type == ChallengeType.PLACE)
                {
                    this.Invoke((MethodInvoker)delegate { CompleteChallenge(); });
                }
                else if (ch.Type == ChallengeType.REMOVE)
                {
                    spChallengeMarkerWasPlaced = true;
                    spFeedback = "Good! Now REMOVE the marker!";
                }
                else if (ch.Type == ChallengeType.ROTATE)
                {
                    spChallengeRotateStart = o.Angle;
                    spFeedback = "Now ROTATE the marker!";
                }
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

        // ---- Story Player: ROTATE challenge ----
        if (state == AppState.StoryPlayer && spInChallenge && !spChallengeComplete
            && spStoryIndex >= 0 && spChallengeRotateStart > -900f)
        {
            Story st = StoryDatabase.AllStories[spStoryIndex];
            Challenge ch = st.Scenes[spSceneIndex].Challenge;
            if (ch.Type == ChallengeType.ROTATE && o.SymbolID == ch.RequiredMarkerId)
            {
                float delta = Math.Abs(o.Angle - spChallengeRotateStart);
                if (delta > (float)Math.PI) delta = (float)(2 * Math.PI) - delta;
                if (delta >= ch.RotateThreshold)
                {
                    this.Invoke((MethodInvoker)delegate { CompleteChallenge(); });
                }
            }
        }
    }

    private int currentAttendanceId = -1;

    private void DoLogin(int mkrId)
    {
        signInOk = true;
        signInMarkerId = mkrId;

        // PACT: Reset student progress for this session
        studentChallengesCompleted = 0;
        studentScenesCompleted = 0;
        studentSessionStart = DateTime.Now;

        // PACT: Check if this is a teacher login (Marker 36)
        if (mkrId == teacherMarkerId)
        {
            isTeacherMode = true;
            loggedInUser = "Teacher";
            loggedInUserId = -1; // Special teacher marker
            signInMessage = "Welcome, Teacher! Press F4 to open Teacher Panel.";
        }
        else if (mkrId == 10)
        {
            isTeacherMode = false;
            loggedInUser = null; // guest
            loggedInUserId = 0; // Guest ID
            signInMessage = "Continuing as Guest!  Welcome, Storyteller!";
        }
        else
        {
            isTeacherMode = false;
            // Look up user in database by ID
            var user = db.GetUserById(mkrId);
            if (user != null)
            {
                loggedInUser = user.Name;
                loggedInUserId = user.Id;
                db.RecordLogin(user.Id);
                signInMessage = "Hi " + loggedInUser + "! Ready for an adventure?";
            }
            else
            {
                // Unknown marker ID - treat as guest
                loggedInUser = "User #" + mkrId;
                loggedInUserId = mkrId;
                signInMessage = "Hi " + loggedInUser + "! Ready for an adventure?";
            }
        }

        // PACT: Log attendance to database for teacher records
        try
        {
            string sessionType = "Manual";
            if (loggedInUserId >= 0)
                currentAttendanceId = db.StartAttendanceSession(loggedInUserId, sessionType);
            
            // Also log to file for backward compatibility
            string logEntry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | ID: " + mkrId + " | Name: " + (loggedInUser ?? "Guest") + " | Status: Logged In";
            File.AppendAllText(attendanceLogFile, logEntry + Environment.NewLine);
        }
        catch { }

        this.Invoke((MethodInvoker)delegate
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1800;
            t.Tick += (s, e2) => { 
                t.Stop(); 
                state = AppState.StorySelection; 
                Invalidate(); 
            };
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

        // ---- Story Player: REMOVE challenge ----
        if (state == AppState.StoryPlayer && spInChallenge && !spChallengeComplete
            && spChallengeMarkerWasPlaced && spStoryIndex >= 0)
        {
            Story st = StoryDatabase.AllStories[spStoryIndex];
            Challenge ch = st.Scenes[spSceneIndex].Challenge;
            if (ch.Type == ChallengeType.REMOVE && o.SymbolID == ch.RequiredMarkerId)
            {
                this.Invoke((MethodInvoker)delegate { CompleteChallenge(); });
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
        if (width <= 0 || height <= 0) return;
        
        Graphics g = e.Graphics;
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;

        try
        {
            switch (state)
            {
                case AppState.SignIn:         DrawSignIn(g);         break;
                case AppState.SignUp:         DrawSignUp(g);         break;
                case AppState.StorySelection: DrawStorySelection(g); break;
                case AppState.StoryPlayer:    DrawStoryPlayer(g);    break;
                case AppState.StoryBuilder:   DrawStoryBuilder(g);   break;
            }

            DrawSkeletonAndMenu(g);
            DrawHeatmapOverlay(g);  // Draw gaze heatmap overlay if enabled
        }
        catch (Exception ex)
        {
            g.Clear(Color.DarkRed);
            g.DrawString("UI Crash Prevented:\n" + ex.ToString(), new Font("Consolas", 14f), Brushes.White, 50, 50);
        }
    }

    private void DrawSkeletonAndMenu(Graphics g)
    {
        // 1. Draw Skeleton if available
        if (skeletonVisible && skeleton.Count > 0)
        {
            using (Pen bonePen = new Pen(Color.FromArgb(150, 0, 255, 255), 4f))
            {
                bonePen.StartCap = LineCap.Round;
                bonePen.EndCap = LineCap.Round;

                // Draw connecting lines
                Action<string, string> drawBone = (p1, p2) =>
                {
                    if (skeleton.ContainsKey(p1) && skeleton.ContainsKey(p2))
                    {
                        float[] c1 = skeleton[p1];
                        float[] c2 = skeleton[p2];
                        g.DrawLine(bonePen, c1[0] * width, c1[1] * height, c2[0] * width, c2[1] * height);
                    }
                };

                // Torso / Shoulders / Arms
                drawBone("ls", "rs"); // shoulders
                drawBone("ls", "lh"); // left body
                drawBone("rs", "rh"); // right body
                drawBone("lh", "rh"); // hips
                drawBone("ls", "le"); // left arm upper
                drawBone("le", "lw"); // left arm lower
                drawBone("rs", "re"); // right arm upper
                drawBone("re", "rw"); // right arm lower

                // Draw joints
                foreach (var kvp in skeleton)
                {
                    int jx = (int)(kvp.Value[0] * width);
                    int jy = (int)(kvp.Value[1] * height);
                    using (SolidBrush jb = new SolidBrush(Color.Gold))
                        g.FillEllipse(jb, jx - 6, jy - 6, 12, 12);
                    
                    if (kvp.Key == "rw") // Highlight right wrist (primary interaction)
                    {
                        using (SolidBrush jbr = new SolidBrush(Color.Red))
                            g.FillEllipse(jbr, jx - 8, jy - 8, 16, 16);
                    }
                    if (kvp.Key == "lw") // Highlight left wrist 
                    {
                        using (SolidBrush jbl = new SolidBrush(Color.DeepSkyBlue))
                            g.FillEllipse(jbl, jx - 8, jy - 8, 16, 16);
                    }
                }
            }
        }

        // 2. Draw Circular Menu if active
        if (circMenuVisible)
        {
            int cx = width / 2;
            int cy = height / 2;
            int r = (int)(0.15f * width); 
            // Draw background circle
            using (SolidBrush mb = new SolidBrush(Color.FromArgb(160, 20, 20, 40)))
                g.FillEllipse(mb, cx - r - 40, cy - r - 40, (r + 40) * 2, (r + 40) * 2);

            using (Pen mp = new Pen(Color.Gold, 3f))
                g.DrawEllipse(mp, cx - r - 40, cy - r - 40, (r + 40) * 2, (r + 40) * 2);

            int n_outer = Math.Min(4, StoryDatabase.AllStories.Length);
            for (int i = 0; i <= n_outer; i++) // 4 outer slices + 1 center (i=n_outer)
            {
                bool isHover = (i == circMenuHover);
                
                if (i < n_outer)
                {
                    // Outer arc slices
                    float startAngle = i * (360f / n_outer) - 90f;
                    float sweepAngle = 360f / n_outer;
                    
                    Rectangle sliceRect = new Rectangle(cx - r - 20, cy - r - 20, (r + 20) * 2, (r + 20) * 2);
                    
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddPie(sliceRect, startAngle + 1f, sweepAngle - 2f);
                        
                        System.Drawing.Drawing2D.GraphicsState gState = g.Save();
                        g.SetClip(path);

                        // Draw story image inside the slice
                        Image stImg = LoadStoryImage(StoryDatabase.AllStories[i].Scenes[0].BackgroundImage);
                        if (stImg != null)
                        {
                            g.DrawImage(stImg, sliceRect);
                            if (!isHover) // Darken if not hovering
                            {
                                using (SolidBrush dBr = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                                    g.FillPath(dBr, path);
                            }
                        }
                        else
                        {
                            using (SolidBrush fallBr = new SolidBrush(StoryDatabase.AllStories[i].CardColor))
                                g.FillPath(fallBr, path);
                        }
                        
                        g.Restore(gState);
                        
                        // Outline
                        using (Pen sp = new Pen(isHover ? Color.White : Color.FromArgb(100,255,255,255), isHover ? 4f : 2f))
                            g.DrawPath(sp, path);
                    }
                    
                    // Draw story title
                    double midAngle = (startAngle + sweepAngle / 2f) * Math.PI / 180.0;
                    int tx = (int)(cx + (r - 20) * Math.Cos(midAngle));
                    int ty = (int)(cy + (r - 20) * Math.Sin(midAngle));
                    
                    using (Font mf = new Font("Comic Sans MS", 14f, FontStyle.Bold))
                    {
                        StringFormat msf = new StringFormat(); msf.Alignment = StringAlignment.Center; msf.LineAlignment = StringAlignment.Center;
                        g.DrawString(StoryDatabase.AllStories[i].Emoji, mf, isHover ? Brushes.Yellow : Brushes.White, tx, ty - 12, msf);
                    }
                }
                else
                {
                    // Center Circle (Back)
                    int cr = 60; // inner radius obscures the pie center
                    Rectangle centerRect = new Rectangle(cx - cr, cy - cr, cr * 2, cr * 2);
                    using (SolidBrush cBr = new SolidBrush(Color.FromArgb(240, 20, 20, 30)))
                        g.FillEllipse(cBr, centerRect);
                        
                    using (Pen cp = new Pen(isHover ? Color.White : Color.DarkGray, isHover ? 4f : 2f))
                        g.DrawEllipse(cp, centerRect);
                        
                    using (Font mf = new Font("Comic Sans MS", 12f, FontStyle.Bold))
                    {
                        StringFormat msf = new StringFormat(); msf.Alignment = StringAlignment.Center; msf.LineAlignment = StringAlignment.Center;
                        g.DrawString("Back", mf, isHover ? Brushes.Yellow : Brushes.LightGray, cx, cy, msf);
                    }
                }
            }
        }

        // 3. Draw last gesture HUD
        if ((DateTime.Now - lastGestureTime).TotalSeconds < 2.5)
        {
            int gx = 20, gy = height - 80;
            DrawGlassPanel(g, gx, gy, 300, 60, Color.FromArgb(150, 0, 100, 0));
            g.DrawString("Gesture: " + lastGesture, fntTitle, brGold, gx + 15, gy + 15);
        }

        // 4. Draw gaze tracking indicator
        if (gazeTracking && (DateTime.Now - lastGazeTime).TotalSeconds < 0.5)
        {
            int gazeScreenX = (int)(gazeX * width);
            int gazeScreenY = (int)(gazeY * height);
            
            // Draw outer ring
            using (Pen gazePen = new Pen(isBlinking ? Color.Red : Color.Cyan, 3f))
            {
                gazePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                g.DrawEllipse(gazePen, gazeScreenX - 20, gazeScreenY - 20, 40, 40);
            }
            // Draw center dot
            using (SolidBrush gazeBrush = new SolidBrush(isBlinking ? Color.Red : Color.Cyan))
            {
                g.FillEllipse(gazeBrush, gazeScreenX - 5, gazeScreenY - 5, 10, 10);
            }
            
            // Draw gaze status text
            string gazeStatus = isBlinking ? "BLINK" : "GAZE";
            using (Font gazeFont = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (SolidBrush gazeTextBrush = new SolidBrush(isBlinking ? Color.Red : Color.Cyan))
            {
                g.DrawString(gazeStatus, gazeFont, gazeTextBrush, gazeScreenX + 25, gazeScreenY - 10);
            }
        }
        
        // 5. Draw heatmap toggle hint (TUIO markers)
        if ((DateTime.Now - lastGestureTime).TotalSeconds < 5 || heatmapVisible)
        {
            string hint = heatmapVisible ? "Mkr 38: Hide Heatmap | Mkr 39: Clear" : "Mkr 38: Show Heatmap";
            using (Font hintFont = new Font("Segoe UI", 9f))
            using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
            {
                g.DrawString(hint, hintFont, hintBrush, width - 220, 10);
            }
        }
    }

    // =========================================================
    //  SIGN IN
    // =========================================================
    private void DrawSignIn(Graphics g)
    {
        DrawAnimatedBg(g, Color.DeepSkyBlue, Color.MediumPurple);
        DrawParticles(g, Color.FromArgb(120, 255, 255, 255));

        int pw = 740, ph = 580;
        int px = (width - pw) / 2, py = (height - ph) / 2;
        DrawGlassPanel(g, px, py, pw, ph, Color.FromArgb(160, 10, 30, 80));

        DrawCentredText(g, fntHuge,  brGold,  "✨ Magic Story Builder ✨", py + 22);
        DrawCentredText(g, fntTitle, brWhite, "A Fun Adventure for Kids!", py + 82);

        using (Pen pLine = new Pen(Color.FromArgb(255, 215, 0), 3f))
            g.DrawLine(pLine, px + 40, py + 130, px + pw - 40, py + 130);

        // ---- Two login option boxes side by side ----
        int boxW = (pw - 100) / 2;   // width of each option box
        int boxH = 230;
        int boxY = py + 150;
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
        DrawStringCentredIn(g, fntBody,  brGold,  "PLAY AS GUEST 🎈",   guestX, boxY + 100, boxW);
        DrawStringCentredIn(g, fntSmall, brWhite, "Place Marker 10",    guestX, boxY + 138, boxW);
        DrawStringCentredIn(g, fntSmall, new SolidBrush(Color.FromArgb(180,180,255)),
            "Place + rotate 45°", guestX, boxY + 168, boxW);

        // Personal-account box
        int userCount = db.GetAllUsers().Count;
        bool anyUsers    = userCount > 0;
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
        DrawStringCentredIn(g, fntBody,  brGold,  "MY PROFILE 🌟",         personalX, boxY + 100, boxW);
        DrawStringCentredIn(g, fntSmall, brWhite, "Your Magic ID Marker",  personalX, boxY + 138, boxW);
        DrawStringCentredIn(g, fntSmall, new SolidBrush(anyUsers
            ? Color.FromArgb(180, 255, 200, 100)
            : Color.FromArgb(130, 180, 180, 180)),
            anyUsers ? (userCount + " friends registered") : "No accounts yet – Sign Up!",
            personalX, boxY + 168, boxW);

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
    //  STORY SELECTION screen
    // =========================================================
    private void DrawStorySelection(Graphics g)
    {
        DrawAnimatedBg(g, Color.FromArgb(20, 10, 60), Color.FromArgb(80, 30, 120));
        DrawParticles(g, Color.FromArgb(80, 255, 200, 255));

        DrawCentredText(g, fntHuge, brGold, "\u2728 Choose Your Adventure! \u2728", 30);

        string userLabel = loggedInUser != null ? "Hi, " + loggedInUser + "!" : "Hi, Explorer!";
        DrawCentredText(g, fntTitle, brWhite, userLabel, 90);

        // 4 story cards in a row
        int cardW = 260, cardH = 440, gap = 20;
        int totalW = cardW * 4 + gap * 3;
        int startX = (width - totalW) / 2;
        int cardY = 140;

        for (int i = 0; i < 4; i++)
        {
            Story st = StoryDatabase.AllStories[i];
            int cx = startX + i * (cardW + gap);

            // Card background
            Color cardFill = Color.FromArgb(180, st.CardColor.R / 3, st.CardColor.G / 3, st.CardColor.B / 3);
            DrawGlassPanel(g, cx, cardY, cardW, cardH, cardFill);

            // Border
            using (Pen bp = new Pen(st.CardColor, 3f))
                g.DrawRectangle(bp, cx, cardY, cardW, cardH);

            // Main character image
            Image mainCharImg = LoadStoryImage(st.Characters[0].ImageFile);
            if (mainCharImg != null)
                g.DrawImage(mainCharImg, cx + (cardW - 100) / 2, cardY + 15, 100, 100);
            else
            {
                using (Font ef = new Font("Segoe UI Emoji", 52f))
                {
                    SizeF es = g.MeasureString(st.Emoji, ef);
                    g.DrawString(st.Emoji, ef, Brushes.White, cx + (cardW - es.Width) / 2, cardY + 20);
                }
            }

            // Title
            using (Font tf = new Font("Comic Sans MS", 14f, FontStyle.Bold))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                g.DrawString(st.Title, tf, brGold, new RectangleF(cx, cardY + 130, cardW, 60), sf);
            }

            // Description
            using (Font df = new Font("Comic Sans MS", 10f))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                g.DrawString(st.Description, df, brWhite, new RectangleF(cx + 10, cardY + 200, cardW - 20, 60), sf);
            }

            // Characters preview with images
            int charY = cardY + 270;
            g.DrawString("Characters:", fntSmall, brGold, cx + 10, charY);
            for (int c = 0; c < st.Characters.Length; c++)
            {
                Image charImg = LoadStoryImage(st.Characters[c].ImageFile);
                if (charImg != null)
                    g.DrawImage(charImg, cx + 10 + c * 75, charY + 18, 50, 50);
                else
                {
                    using (Font cef = new Font("Segoe UI Emoji", 20f))
                        g.DrawString(st.Characters[c].Emoji, cef, Brushes.White, cx + 10 + c * 75, charY + 22);
                }
                g.DrawString(st.Characters[c].Name, fntSmall,
                    new SolidBrush(st.Characters[c].Color), cx + 10 + c * 75, charY + 70);
            }

            // Marker instruction
            string mkrHint = "Place Marker " + i;
            DrawStringCentredIn(g, fntBody, new SolidBrush(Color.FromArgb(255, 200, 100)),
                mkrHint, cx, cardY + cardH - 60, cardW);
            DrawStringCentredIn(g, fntSmall, brWhite,
                "to play!", cx, cardY + cardH - 32, cardW);
        }

        // Footer
        DrawCentredText(g, fntSmall, new SolidBrush(Color.FromArgb(180, 200, 200, 255)),
            "Marker 34 = Back to Sign In", height - 40);
    }

    // =========================================================
    //  STORY PLAYER
    // =========================================================
    private void DrawStoryPlayer(Graphics g)
    {
        if (spStoryIndex < 0 || spStoryIndex >= StoryDatabase.AllStories.Length) return;
        Story story = StoryDatabase.AllStories[spStoryIndex];
        if (spSceneIndex >= story.Scenes.Length)
        {
            DrawStoryComplete(g, story);
            return;
        }
        StoryScene scene = story.Scenes[spSceneIndex];

        // ---- Background ----
        Image bgImg = LoadStoryImage(scene.BackgroundImage);
        if (bgImg != null)
            g.DrawImage(bgImg, 0, 0, width, height);
        else
        {
            DrawAnimatedBg(g, scene.BgColor1, scene.BgColor2);
            DrawStars(g, spSceneIndex);
        }

        // ---- Top HUD bar ----
        DrawGlassPanel(g, 0, 0, width, 58, Color.FromArgb(160, 0, 0, 0));
        g.DrawString(story.Emoji + " " + story.Title.Replace("\n", " "), fntTitle, brGold, 18, 14);
        // Progress bar
        int pbW = 200, pbH = 18, pbX = width - pbW - 20, pbY = 20;
        using (SolidBrush pbBg = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
            g.FillRectangle(pbBg, pbX, pbY, pbW, pbH);
        float progress = (float)spSceneIndex / story.Scenes.Length;
        if (spPhase == ScenePhase.WALK_CONTINUE || spPhase == ScenePhase.OBSTACLE_REMOVE)
            progress = (spSceneIndex + 0.8f) / story.Scenes.Length;
        int pbFillW = Math.Max(2, (int)(pbW * progress));
        using (LinearGradientBrush pbFill = new LinearGradientBrush(
            new Rectangle(pbX, pbY, pbW, pbH), Color.Lime, Color.Gold, 0f))
            g.FillRectangle(pbFill, pbX, pbY, pbFillW, pbH);
        g.DrawString("Scene " + (spSceneIndex + 1) + "/" + story.Scenes.Length, fntSmall, brWhite, pbX, pbY + pbH + 2);

        // Scene title
        DrawGlassPanel(g, (width - 500) / 2, 66, 500, 34, Color.FromArgb(140, 0, 0, 0));
        DrawCentredText(g, fntBody, brGold, scene.Title, 70);

        // ---- Ground line ----
        int groundY = spCharGroundY + 180;
        using (Pen gp = new Pen(Color.FromArgb(80, 100, 60, 20), 3f))
            g.DrawLine(gp, 0, groundY, width, groundY);

        // ---- Draw obstacle (if visible) ----
        if (spObstacleVisible && (spPhase == ScenePhase.OBSTACLE || spPhase == ScenePhase.CHALLENGE || spPhase == ScenePhase.OBSTACLE_REMOVE))
        {
            int oAlpha = Math.Max(0, Math.Min(255, (int)spObstacleAlpha));
            Image objImg = LoadStoryImage(scene.Challenge.ObjectImage);
            int objSize = 140;
            int objY = groundY - objSize + 10;

            if (objImg != null)
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = oAlpha / 255f;
                ImageAttributes ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(objImg,
                    new Rectangle((int)spObstacleX, objY, objSize, objSize),
                    0, 0, objImg.Width, objImg.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                // Draw a colored block as fallback obstacle
                using (SolidBrush ob = new SolidBrush(Color.FromArgb(oAlpha, 139, 90, 43)))
                    g.FillRectangle(ob, spObstacleX, objY, objSize, objSize);
                using (Pen op = new Pen(Color.FromArgb(oAlpha, 80, 50, 20), 3f))
                    g.DrawRectangle(op, spObstacleX, objY, objSize, objSize);
                using (Font bf = new Font("Comic Sans MS", 11f, FontStyle.Bold))
                    g.DrawString("Obstacle!", bf, new SolidBrush(Color.FromArgb(oAlpha, 255, 255, 255)),
                        spObstacleX + 15, objY + objSize / 2 - 10);
            }
        }

        // ---- Draw characters ----
        int charW = 160, charH = 180;
        bool isWalking = (spPhase == ScenePhase.WALK_IN || spPhase == ScenePhase.WALK_CONTINUE);
        int walkBob = isWalking ? (int)(6 * Math.Sin(spWalkFrame * 0.6)) : 0;

        // Main character (character index 0)
        DrawCharacterInScene(g, story.Characters[0], (int)spChar1X, groundY - charH + walkBob, charW, charH, isWalking);

        // Second character (appears from right if dialogues involve other characters)
        if (spChar2Visible && spPhase != ScenePhase.WALK_CONTINUE)
        {
            int dialogueChar2Idx = 1;
            foreach (var d in scene.Dialogues)
            {
                if (d.CharIndex != 0) { dialogueChar2Idx = d.CharIndex; break; }
            }
            if (dialogueChar2Idx < story.Characters.Length)
                DrawCharacterInScene(g, story.Characters[dialogueChar2Idx], (int)spChar2X, groundY - charH, charW, charH, false);
        }

        // ---- Phase-specific overlays ----
        if (spPhase == ScenePhase.WALK_IN)
        {
            // Walking in - show scene title, character is moving
            int alpha = 120 + (int)(80 * Math.Sin(animTick * 0.8));
            DrawGlassPanel(g, (width - 300) / 2, height - 70, 300, 36, Color.FromArgb(140, 0, 0, 0));
            DrawCentredText(g, fntBody, new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)),
                "Walking in...", height - 65);
        }
        else if (spPhase == ScenePhase.TALK && spDialogueIndex < scene.Dialogues.Count)
        {
            // ---- DIALOGUE ----
            Dialogue dlg = scene.Dialogues[spDialogueIndex];
            StoryCharacter ch = story.Characters[dlg.CharIndex];
            bool isSpeaker1 = (dlg.CharIndex == 0);

            // Speech bubble position depends on which character is speaking
            int bubW = 500, bubH = 120;
            int bubX, bubY = spCharGroundY - 80;
            if (isSpeaker1)
                bubX = (int)spChar1X + charW + 10;
            else
                bubX = (int)spChar2X - bubW - 10;
            bubX = Math.Max(10, Math.Min(bubX, width - bubW - 10));

            // Bubble
            DrawGlassPanel(g, bubX, bubY, bubW, bubH, Color.FromArgb(220, 255, 255, 255));
            using (Pen sp = new Pen(ch.Color, 4f))
                g.DrawRectangle(sp, bubX, bubY, bubW, bubH);

            // Triangle pointer
            int triX = isSpeaker1 ? bubX : bubX + bubW;
            int triDir = isSpeaker1 ? -1 : 1;
            Point[] triangle = {
                new Point(triX - triDir * 20, bubY + bubH / 2),
                new Point(triX, bubY + bubH / 2 - 12),
                new Point(triX, bubY + bubH / 2 + 12)
            };
            using (SolidBrush tbr = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                g.FillPolygon(tbr, triangle);

            // Speaker name
            using (Font nf = new Font("Comic Sans MS", 12f, FontStyle.Bold))
                g.DrawString(ch.Name, nf, new SolidBrush(ch.Color), bubX + 12, bubY + 8);

            // Dialogue text
            using (Font dtf = new Font("Comic Sans MS", 14f, FontStyle.Bold))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(dlg.Text, dtf, new SolidBrush(Color.FromArgb(40, 40, 60)),
                    new RectangleF(bubX + 15, bubY + 30, bubW - 30, bubH - 40), sf);
            }

            // Next hint
            int alpha2 = 120 + (int)(80 * Math.Sin(animTick * 0.8));
            DrawGlassPanel(g, (width - 400) / 2, height - 70, 400, 36, Color.FromArgb(160, 0, 0, 0));
            DrawCentredText(g, fntBody, new SolidBrush(Color.FromArgb(alpha2, 255, 215, 0)),
                "Place Marker 35 \u25B6", height - 65);
        }
        else if (spPhase == ScenePhase.OBSTACLE || spPhase == ScenePhase.CHALLENGE)
        {
            // ---- CHALLENGE ----
            Challenge ch = scene.Challenge;

            // Instruction box at bottom
            int bxW = 680, bxH = 90;
            int bxX = (width - bxW) / 2, bxY = height - 150;
            DrawGlassPanel(g, bxX, bxY, bxW, bxH, Color.FromArgb(210, 60, 0, 0));
            using (Pen ip = new Pen(Color.OrangeRed, 3f))
                g.DrawRectangle(ip, bxX, bxY, bxW, bxH);

            using (Font itf = new Font("Comic Sans MS", 14f, FontStyle.Bold))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(ch.Instruction, itf, brWhite,
                    new RectangleF(bxX + 15, bxY + 5, bxW - 30, bxH - 10), sf);
            }

            // Marker hint (pulsing, top of instruction)
            int pulse = (int)(6 * Math.Sin(animTick * 0.6));
            DrawGlassPanel(g, (width - 250) / 2, bxY - 50 + pulse, 250, 40, Color.FromArgb(200, 0, 0, 0));
            DrawCentredText(g, fntBody, new SolidBrush(Color.FromArgb(255, 255, 180, 50)),
                "\u2B50 Marker " + ch.RequiredMarkerId + " \u2B50", bxY - 44 + pulse);

            // Feedback
            if (spFeedback.Length > 0)
            {
                DrawGlassPanel(g, (width - 400) / 2, bxY + bxH + 8, 400, 30, Color.FromArgb(160, 0, 60, 0));
                DrawCentredText(g, fntBody, new SolidBrush(Color.LightGreen), spFeedback, bxY + bxH + 12);
            }
        }
        else if (spPhase == ScenePhase.OBSTACLE_REMOVE)
        {
            // ---- OBSTACLE FADING AWAY ----
            DrawGlassPanel(g, (width - 500) / 2, height / 2 - 40, 500, 60, Color.FromArgb(180, 0, 60, 0));
            DrawCentredText(g, fntHuge, brGold, "\u2B50 Great Job! \u2B50", height / 2 - 35);

            Challenge ch = scene.Challenge;
            DrawCentredText(g, fntBody, new SolidBrush(Color.LightGreen),
                ch.SuccessMessage, height / 2 + 30);

            // Confetti
            Random rng = new Random(animTick * 3 + spSceneIndex);
            Color[] confColors = { Color.Gold, Color.DeepPink, Color.Cyan, Color.Lime, Color.Orange };
            for (int p = 0; p < 25; p++)
            {
                float cx2 = rng.Next(width);
                float cy2 = rng.Next(height / 2, height - 100);
                using (SolidBrush cb = new SolidBrush(Color.FromArgb(180, confColors[p % confColors.Length])))
                    g.FillEllipse(cb, cx2, cy2, 6 + rng.Next(6), 6 + rng.Next(6));
            }
        }
        else if (spPhase == ScenePhase.WALK_CONTINUE && spSuccessTimer > 0)
        {
            // Show success message while walking off
            Challenge ch = scene.Challenge;
            int a = Math.Min(255, spSuccessTimer * 6);
            DrawCentredText(g, fntTitle, new SolidBrush(Color.FromArgb(a, 100, 255, 100)),
                ch.SuccessMessage, height / 2);
        }

        // Footer
        DrawCentredText(g, fntSmall, new SolidBrush(Color.FromArgb(140, 200, 200, 255)),
            "Marker 34 = Back to Stories", height - 30);
    }

    // -- Draw a character standing/walking in the scene --
    private void DrawCharacterInScene(Graphics g, StoryCharacter ch, int x, int y, int w, int h, bool walking)
    {
        // Shadow
        using (SolidBrush sb = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
            g.FillEllipse(sb, x + 10, y + h - 10, w - 20, 20);

        Image img = LoadStoryImage(ch.ImageFile);
        if (img != null)
        {
            g.DrawImage(img, x, y, w, h);
        }
        else
        {
            // Emoji fallback
            using (Font ef = new Font("Segoe UI Emoji", 60f))
                g.DrawString(ch.Emoji, ef, Brushes.White, x + 10, y + h / 2 - 40);
        }

        // Name label below
        int labelW = w + 20;
        int labelX = x - 10;
        int labelY = y + h + 2;
        DrawGlassPanel(g, labelX, labelY, labelW, 26, Color.FromArgb(180, 0, 0, 0));
        using (Font nf = new Font("Comic Sans MS", 11f, FontStyle.Bold))
        {
            StringFormat nsf = new StringFormat();
            nsf.Alignment = StringAlignment.Center;
            g.DrawString(ch.Name, nf, new SolidBrush(ch.Color),
                new RectangleF(labelX, labelY + 3, labelW, 22), nsf);
        }

        // Walking dust effect
        if (walking)
        {
            Random dr = new Random(spWalkFrame + x);
            for (int d = 0; d < 4; d++)
            {
                int dx = x + w / 2 - 30 + dr.Next(60);
                int dy = y + h - 5 + dr.Next(15);
                int ds = 3 + dr.Next(5);
                using (SolidBrush db = new SolidBrush(Color.FromArgb(100 - d * 20, 180, 160, 120)))
                    g.FillEllipse(db, dx, dy, ds, ds);
            }
        }
    }

    // -- Story Complete screen --
    private void DrawStoryComplete(Graphics g, Story story)
    {
        DrawAnimatedBg(g, Color.Gold, Color.OrangeRed);

        // Confetti
        Random rng = new Random(animTick * 5 + 7);
        Color[] confColors = { Color.White, Color.DeepPink, Color.Cyan, Color.Lime, Color.Gold, Color.MediumPurple };
        for (int p = 0; p < 60; p++)
        {
            float cx2 = rng.Next(width);
            float cy2 = rng.Next(height);
            Color cc = confColors[p % confColors.Length];
            float sz = 4 + rng.Next(10);
            using (SolidBrush cb = new SolidBrush(Color.FromArgb(200, cc)))
                g.FillEllipse(cb, cx2, cy2, sz, sz);
        }

        DrawCentredText(g, fntHuge, brWhite, "\U0001F389 Story Complete! \U0001F389", 100);
        DrawCentredText(g, fntTitle, brWhite, story.Title.Replace("\n", " "), 180);

        // Big emoji
        using (Font ef = new Font("Segoe UI Emoji", 70f))
        {
            SizeF es = g.MeasureString(story.Emoji, ef);
            g.DrawString(story.Emoji, ef, Brushes.White, (width - es.Width) / 2, 220);
        }

        // PACT Persona (Nader): Feel rewarded at the end
        string heroName = loggedInUser != null ? loggedInUser : "Explorer";
        DrawCentredText(g, fntTitle, brGold, "Well done, " + heroName + "! You are a true hero!", 360);

        // PACT: Teacher Evaluation Panel (Scenario Step 7,10: Track & evaluate)
        if (isTeacherMode)
        {
            int evW = 500, evH = 120;
            int evX = (width - evW) / 2, evY = 410;
            DrawGlassPanel(g, evX, evY, evW, evH, Color.FromArgb(180, 0, 60, 0));
            using (Pen ep = new Pen(Color.Gold, 2f))
                g.DrawRectangle(ep, evX, evY, evW, evH);
            g.DrawString("Teacher Report", fntTitle, brGold, evX + 15, evY + 8);
            TimeSpan duration = DateTime.Now - studentSessionStart;
            g.DrawString("Challenges Completed: " + studentChallengesCompleted, fntBody, brWhite, evX + 15, evY + 40);
            g.DrawString("Scenes Finished: " + story.Scenes.Length + "/" + story.Scenes.Length, fntBody, brWhite, evX + 15, evY + 62);
            g.DrawString("Session Time: " + (int)duration.TotalMinutes + " min " + duration.Seconds + " sec", fntBody, brWhite, evX + 15, evY + 84);
        }
        else
        {
            // Student reward stars
            string stars = "";
            for (int i = 0; i < studentChallengesCompleted && i < 5; i++) stars += "⭐ ";
            if (stars.Length > 0)
                DrawCentredText(g, fntHuge, brGold, stars, 400);
        }

        // Footer hint
        int alpha3 = 120 + (int)(80 * Math.Sin(animTick * 0.8));
        DrawCentredText(g, fntBody, new SolidBrush(Color.FromArgb(alpha3, 255, 255, 255)),
            "Place Marker 34 to go back to stories", height - 60);
    }

    // =========================================================
    //  Story Logic Helpers
    // =========================================================
    private void AdvanceStoryDialogue()
    {
        if (spStoryIndex < 0) return;
        Story story = StoryDatabase.AllStories[spStoryIndex];
        if (spSceneIndex >= story.Scenes.Length) return;
        StoryScene scene = story.Scenes[spSceneIndex];

        if (spPhase == ScenePhase.TALK)
        {
            spDialogueIndex++;
            if (spDialogueIndex >= scene.Dialogues.Count)
            {
                // Transition to obstacle/challenge
                spPhase = ScenePhase.OBSTACLE;
                spInChallenge = true;
                spChallengeComplete = false;
                spFeedback = "";
                spChallengeMarkerWasPlaced = false;
                spChallengeRotateStart = -999f;
                spObstacleVisible = true;
                spObstacleAlpha = 255f;
            }
        }
        Invalidate();
    }

    private void CompleteChallenge()
    {
        spChallengeComplete = true;
        spFeedback = "";
        // PACT: Track student progress (Scenario: Evaluate student skill)
        studentChallengesCompleted++;
        // Start obstacle removal animation
        spPhase = ScenePhase.OBSTACLE_REMOVE;
        Invalidate();
    }

    private void ResetSceneAnimation()
    {
        spPhase = ScenePhase.WALK_IN;
        spDialogueIndex = 0;
        spInChallenge = false;
        spChallengeComplete = false;
        spFeedback = "";
        spChallengeMarkerWasPlaced = false;
        spChallengeRotateStart = -999f;
        spObstacleVisible = true;
        spObstacleAlpha = 255f;
        spSuccessTimer = 0;

        // Character start positions
        spChar1X = -200f;
        spChar1TargetX = 250f;  // walk to left-center area
        spCharFlipped = false;

        // Second character comes from right side
        if (spStoryIndex >= 0 && spSceneIndex < StoryDatabase.AllStories[spStoryIndex].Scenes.Length)
        {
            StoryScene scene = StoryDatabase.AllStories[spStoryIndex].Scenes[spSceneIndex];
            spChar2Visible = scene.Dialogues.Count > 0 && scene.Dialogues.Exists(d => d.CharIndex != 0);
            spChar2X = width + 100;
            spChar2TargetX = width - 400;
            spObstacleX = (width / 2f) + 50;
        }
        else
        {
            spChar2Visible = false;
        }
    }

    // =========================================================
    //  STORY BUILDER (legacy free-play mode)
    // =========================================================
    private void DrawStoryBuilder(Graphics g)
    {
        int si = Math.Max(0, Math.Min(sceneIndex, SCENES.Length - 1));
        DrawAnimatedBg(g, SCENE_GRAD[si, 0], SCENE_GRAD[si, 1]);
        DrawStars(g, si);

        // Top bar
        DrawGlassPanel(g, 0, 0, width, 58, Color.FromArgb(120, 0, 0, 0));
        g.DrawString("✨ MAGIC STORY BUILDER ✨", fntTitle, brGold, 18, 14);
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
        Image[] frames = null;
        animalFrames.TryGetValue(animal, out frames);
        Image frame = (frames != null) ? frames[tick % 10] : null;

        GraphicsState gs = g.Save();
        g.TranslateTransform(x + size / 2f, y + size / 2f);
        g.RotateTransform(angleDeg);
        
        if (frame != null)
        {
            g.DrawImage(frame, -size / 2, -size / 2, size, size);
        }
        else
        {
            int idx = Array.IndexOf(ANIMALS, animal);
            string[] emojis = { "🦁", "🦊", "🦅", "🐺", "🐻", "🦄" };
            string emoji = (idx >= 0 && idx < emojis.Length) ? emojis[idx] : "❓";
            
            using (Font ef = new Font("Segoe UI Emoji", size * 0.5f))
            {
                SizeF es = g.MeasureString(emoji, ef);
                g.DrawString(emoji, ef, Brushes.White, -es.Width / 2f, -es.Height / 2f);
            }
        }
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
    //  Fullscreen toggle (now triggered by Marker 37)
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


