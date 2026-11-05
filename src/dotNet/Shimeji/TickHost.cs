using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The real desktop as the tick world sees it (the source's WindowsEnvironment): the virtual screen, each monitor,
    /// the primary work area (SPI_GETWORKAREA), the cursor with Location's smoothing, the window events the companions'
    /// forms queued since the last step, and the interactive window (<see cref="Win32InteractiveWindow"/>), which a
    /// companion type that opted in to window actions may move or minimise.
    /// </summary>
    internal sealed class Win32TickEnvironment : ITickEnvironment
    {
        private readonly TickArea _screen = new TickArea();
        private readonly TickArea _work = new TickArea();
        private readonly List<TickArea> _screens = new List<TickArea>();
        private readonly TickArea _window = new TickArea { Visible = false };
        private readonly Win32InteractiveWindow _interactive = new Win32InteractiveWindow();
        private readonly List<TickInput> _queued = new List<TickInput>();
        private List<TickInput> _now = new List<TickInput>();
        private int _x, _y, _dx, _dy;
        private bool _first = true;

        public TickArea Screen { get { return _screen; } }
        public TickArea WorkArea { get { return _work; } }
        public IReadOnlyList<TickArea> Screens { get { return _screens; } }
        public TickArea ActiveWindow { get { return _window; } }
        public TickPoint Cursor { get { return new TickPoint(_x, _y); } }
        public TickPoint CursorDelta { get { return new TickPoint(_dx, _dy); } }
        public IReadOnlyList<TickInput> Inputs { get { return _now; } }

        /// <summary>The interactive window: its title lists, and the session's record of windows moved.</summary>
        public Win32InteractiveWindow Interactive { get { return _interactive; } }

        /// <summary>Whether the desktop's windows are searched for the interactive one. False only in the window self-test,
        /// whose companions must not stand on whatever window the owner has open (a whitelisted Notepad is a floor).</summary>
        public bool SearchesWindows { get; set; } = true;

        /// <summary>A window event, delivered at the next step's start (the forms and the tick timer share the UI
        /// thread, so nothing races).</summary>
        public void Queue(TickInput input) { _queued.Add(input); }

        public bool MoveActiveWindow(TickPoint topLeft, out TickRect from, out TickRect to)
        {
            return _interactive.Move(topLeft, out from, out to);
        }

        public bool MinimizeActiveWindow() { return _interactive.Minimize(); }


        public void Tick(int tick)
        {
            Rectangle v = SystemInformation.VirtualScreen;
            _screen.Set(v.X, v.Y, v.Width, v.Height);
            System.Windows.Forms.Screen[] all = System.Windows.Forms.Screen.AllScreens;
            while (_screens.Count < all.Length) _screens.Add(new TickArea());
            if (_screens.Count > all.Length) _screens.RemoveRange(all.Length, _screens.Count - all.Length);
            for (int i = 0; i < all.Length; i++)
                _screens[i].Set(all[i].Bounds.X, all[i].Bounds.Y, all[i].Bounds.Width, all[i].Bounds.Height);
            Rectangle w = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            _work.Set(w.X, w.Y, w.Width, w.Height);
            // WindowsEnvironment.tick (:292-300): the interactive window found this step, else an empty, invisible area.
            TickRect found;
            if (SearchesWindows && _interactive.Find(new TickRect(v.X, v.Y, v.Width, v.Height), out found))
            {
                _window.Set(found.X, found.Y, found.Width, found.Height);
                _window.Visible = true;
            }
            else
            {
                _window.Set(0, 0, 0, 0);
                _window.Visible = false;
            }
            Point c = System.Windows.Forms.Cursor.Position;
            if (_first) { _x = c.X; _y = c.Y; _first = false; }
            // Location.set: integer division truncating toward zero, as Java's.
            _dx = (_dx + c.X - _x) / 2;
            _dy = (_dy + c.Y - _y) / 2;
            _x = c.X;
            _y = c.Y;
            _now = new List<TickInput>(_queued);
            _queued.Clear();
        }
    }

    /// <summary>One random stream for the real desktop.</summary>
    internal sealed class HostTickRandom : ITickRandom
    {
        private readonly Random _random = new Random();
        public double Next() { return _random.NextDouble(); }
    }

    /// <summary>
    /// The host's per-type settings for tick companions. Sounds and Multiscreen are the app's existing settings.
    /// Breeding, Transients, Transformation, WindowActions and the type's header toggles are the user's choices in the
    /// Pets pane, stored per type id and read afresh at each decision (<see cref="TickTypeSwitches"/>): the lifecycle
    /// switches default on, WindowActions off (the owner's decision), a toggle its own default. Throwing, the source's
    /// own switch, keeps the source's default of on (Main.java:662), so with WindowActions off a window action plays its
    /// animation and moves nothing (policy:window-off-plays) instead of ending at once.
    /// </summary>
    internal sealed class HostTickSettings : ITickSettings
    {
        private readonly Func<string, string> _typeIdOf;
        private readonly ITickSwitchStore _switches;

        /// <param name="typeIdOf">A type's name to the id its settings are stored under.</param>
        /// <param name="switches">Where the user's choices are kept; the app's settings when null.</param>
        public HostTickSettings(Func<string, string> typeIdOf, ITickSwitchStore switches = null)
        {
            _typeIdOf = typeIdOf;
            _switches = switches ?? LocalDataSwitchStore.Instance;
        }

        private string TypeIdOf(string imageSet) { return imageSet == null ? null : _typeIdOf(imageSet); }

        public bool Get(string setting, string imageSet)
        {
            LocalData data = Program.MyData;
            switch (setting)
            {
                case "Multiscreen": return data != null && data.GetMultiscreen();
                case "Sounds":
                    return data != null && data.GetPetSoundsEnabled() && (imageSet == null || data.IsPetSoundEnabled(_typeIdOf(imageSet)));
                case "Throwing": return true;
                // The user's per-type switches. Any setting neither list names is OFF: a misspelt or new switch must
                // not default a behaviour on (window actions would have, before WindowActions had its own case).
                default: return TickTypeSwitches.Get(_switches, TypeIdOf(imageSet), setting);
            }
        }

        public bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet)
        {
            return TickTypeSwitches.IsBehaviourEnabled(_switches, TypeIdOf(imageSet), behaviour);
        }
    }

    /// <summary>A tick companion type as the host draws it: the compiled type and the classic sprite store that holds
    /// its frames (already scaled to the type's size).</summary>
    internal sealed class TickHostType
    {
        public readonly string Id;
        public readonly TickType Type;
        public readonly Xml Sprites;
        /// <summary>The (empty) classic animation set staged with the type: a window takes one, and it carries the
        /// type's id. Null in a self-test that builds its windows itself.</summary>
        public readonly Animations Animations;

        /// <summary>What owns <see cref="Sprites"/> (the app's registry entry; null for a pinned type): the host holds it
        /// while a companion runs or draws as this type (TickHost.RetainType / ReleaseType).</summary>
        public object Owner;

        public TickHostType(string id, TickType type, Xml sprites, Animations animations = null)
        {
            Id = id;
            Type = type;
            Sprites = sprites;
            Animations = animations;
            Clips = TickStaging.ClipsOf(sprites);
        }

        /// <summary>The type's named clips' bytes by name (<see cref="TickStaging.ClipsOf"/>); a self-test sets its own.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, byte[]> Clips { get; set; }
    }

    /// <summary>
    /// Drives every tick companion from one 40 ms clock: steps the world, then gives each companion's window its frame
    /// and position. Births open windows, removals close them. The forms keep their own timers off (FormCompanion tick
    /// mode); their left-button events come back here as tick input.
    /// </summary>
    internal sealed partial class TickHost : ITickObserver, IDisposable
    {
        public const int TickMilliseconds = TickPacer.TickMilliseconds;

        private readonly TickWorld _world;
        private readonly Win32TickEnvironment _environment;
        private readonly Timer _timer;
        // The timer polls; the pacer decides when 40 ms of wall time are due (the source's Manager clock).
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly TickPacer _pacer = new TickPacer(0);
        private readonly Dictionary<string, TickHostType> _typesByName = new Dictionary<string, TickHostType>(StringComparer.Ordinal);
        private readonly Dictionary<TickMascot, FormCompanion> _forms = new Dictionary<TickMascot, FormCompanion>();
        private readonly Func<TickHostType, FormCompanion> _openForm;
        private readonly Action<FormCompanion> _closeForm;
        private readonly Action<string> _log;
        private int _nextSid;

        /// <param name="openForm">Create, size and show (still transparent) a window for a companion of this type.</param>
        /// <param name="closeForm">Close a companion's window.</param>
        /// <param name="log">The diagnostic log, for faults the engine reports.</param>
        public TickHost(Func<TickHostType, FormCompanion> openForm, Action<FormCompanion> closeForm, Action<string> log)
        {
            _openForm = openForm;
            _closeForm = closeForm;
            _log = log ?? delegate { };
            _environment = new Win32TickEnvironment();
            WindowRecord = _environment.Interactive;
            _world = new TickWorld(_environment, new HostTickRandom(), new HostTickSettings(TypeIdOf), this);
            _world.Clips = new HostTickClips(HostTypeOf, delegate { return Audio; });
            AttachPacks();
            _world.Start();
            _timer = new Timer { Interval = TickPacer.PollMilliseconds };
            _timer.Tick += delegate
            {
                TimerFirings++;
                long now = _clock.ElapsedMilliseconds;
                if (!_pacer.Due(now)) return;
                if (StepTimesForSelfTest != null) StepTimesForSelfTest.Add(now);
                Step();
            };
        }

        public TickWorld World { get { return _world; } }

        /// <summary>How many times the polling timer has fired (the window self-test's clock check reports it).</summary>
        internal int TimerFirings { get; private set; }

        /// <summary>When set, the clock time (ms) of every step the timer takes: the window self-test measures the pace
        /// between steps with it. Null in the app.</summary>
        internal List<long> StepTimesForSelfTest;
        public Win32TickEnvironment Environment { get { return _environment; } }
        public int Count { get { return _forms.Count; } }

        /// <summary>How opaque the companions are drawn: 1 normally; a self-test draws at 0, so real windows are
        /// created, placed and drawn without ever appearing on the owner's desktop.</summary>
        public double RenderOpacity { get; set; } = 1.0;

        /// <summary>The window drawing a companion, or null.</summary>
        public FormCompanion FormOf(TickMascot mascot)
        {
            FormCompanion form;
            return _forms.TryGetValue(mascot, out form) ? form : null;
        }

        public void AddType(TickHostType type)
        {
            _typesByName[type.Type.Name] = type;
            _byType[type.Type] = type;
            _world.AddType(type.Type);
        }

        private string TypeIdOf(string imageSet)
        {
            TickHostType t;
            return _typesByName.TryGetValue(imageSet, out t) ? t.Id : imageSet;
        }

        /// <summary>Where the companions' clips play (StartUp gives the app's audio output); null plays none.</summary>
        public ITickAudio Audio { get; set; }

        private TickHostType HostTypeOf(string imageSet)
        {
            TickHostType t;
            return _typesByName.TryGetValue(imageSet, out t) ? t : null;
        }

        /// <summary>A new companion of a type, placed at <paramref name="anchor"/> facing as given, entering
        /// <paramref name="behaviour"/> (or one chosen from the route when null). It appears at the next step, in
        /// <paramref name="form"/> when one is given (the app creates a spawned companion's window itself, so the window
        /// is in its pet list from the start), else in one opened then. Null when its first behaviour could not be
        /// built: the fault is logged and the form is left to the caller.</summary>
        public TickMascot Spawn(string imageSet, TickPoint anchor, bool lookRight, string behaviour, FormCompanion form = null)
        {
            TickType type = _world.FindType(imageSet);
            if (type == null) throw new InvalidOperationException("no tick companion type '" + imageSet + "'");
            TickMascot m = _world.CreateAtStart("t" + (++_nextSid), type, anchor, lookRight, behaviour);
            // Its first behaviour could not be built (reported through the log): it never joins the world, and the
            // caller closes the window it made.
            if (m.Disposing) return null;
            if (form != null)
            {
                form.AttachTick(this, m);
                _forms[m] = form;
            }
            Hold(m);
            if (!_timer.Enabled)
            {
                _pacer.Restart(_clock.ElapsedMilliseconds);
                _timer.Start();
            }
            return m;
        }

        /// <summary>
        /// A new companion placed the way the source's Main.createMascot places one: off screen at (-4000, -4000), facing
        /// right on a draw below one half (site builtin:create-facing), entering <paramref name="behaviour"/> or the
        /// route's choice. Its first step finds it outside the work area and drops it in at a random x, which is how a
        /// Shimeji arrives.
        /// </summary>
        public TickMascot SpawnLikeSource(string imageSet, string behaviour, FormCompanion form)
        {
            bool lookRight = _world.Draw(null, "builtin:create-facing", "facing") < 0.5;
            TickMascot m = Spawn(imageSet, new TickPoint(-4000, -4000), lookRight, behaviour, form);
            // A pack's startup behaviour runs once the arrival has landed, not only at creation, which the drop-in's
            // Fall replaces at the first step (TickMascot.ArrivalBehaviour).
            if (m != null && !string.IsNullOrEmpty(behaviour)) m.ArrivalBehaviour = behaviour;
            return m;
        }

        /// <summary>How many tick companions may exist at once, active and pending together, asked before every step:
        /// the app's shared cap less its classic companions (policy:birth-cap). Null keeps the world's own cap.</summary>
        public Func<int> CompanionBudget { get; set; }

        /// <summary>Births queued for the next step: companions that will need a window the app has not counted yet.</summary>
        public int PendingBirths
        {
            get
            {
                int n = 0;
                foreach (TickMascot m in _world.Pending) if (!_forms.ContainsKey(m)) n++;
                return n;
            }
        }

        /// <summary>One step and one render (also the self-test's way to advance the clock deterministically).</summary>
        public void Step()
        {
            if (CompanionBudget != null) _world.MaximumCompanions = Math.Max(0, CompanionBudget());
            _world.StepOnce();
            foreach (TickMascot m in _world.Mascots)
            {
                FormCompanion form;
                if (!_forms.TryGetValue(m, out form))
                {
                    TickHostType t;
                    if (!_typesByName.TryGetValue(m.Type.Name, out t)) continue;
                    form = _openForm(t);
                    if (form == null)
                    {
                        // The app had no room for another window after all; the companion leaves rather than running
                        // unseen. Disposed at once, so the next step removes it.
                        _log("tick companion " + m.Sid + " (" + m.Type.Name + "): no window could be opened; removed");
                        m.Dispose("no-window");
                        continue;
                    }
                    form.AttachTick(this, m);
                    _forms[m] = form;
                }
                Render(m, form);
            }
            UpdateHolds();
        }

        private void Render(TickMascot m, FormCompanion form)
        {
            if (m.BringToFrontRequested)
            {
                m.BringToFrontRequested = false;
                form.ApplyTopMost(true);
            }
            TickHostType t;
            if (!m.Visible || m.Pose == null || !_typesByName.TryGetValue((m.PoseType ?? m.Type).Name, out t))
            {
                form.RenderTick(null, 0, 0, 0, 0, false);
                return;
            }
            // ImagePairs: facing right shows the explicit right frame, else the left frame mirrored; the window shows only
            // the pose image's region of that cell, at the companion's bounds (TickPoseImage).
            TickRect b;
            Image frame = TickPoseImage.Frame(m, t.Sprites, _log, out b);
            form.RenderTick(frame, b.X, b.Y, b.Width, b.Height, true, RenderOpacity);
        }

        /// <summary>A left-button event on a companion's window, delivered at the next step.</summary>
        public void Pointer(FormCompanion form, TickInputKind kind, Point screen)
        {
            foreach (KeyValuePair<TickMascot, FormCompanion> p in _forms)
                if (ReferenceEquals(p.Value, form))
                {
                    _environment.Queue(new TickInput(kind, p.Key.Sid, new TickPoint(screen.X, screen.Y)));
                    return;
                }
        }

        /// <summary>The window was closed from outside (the user removed it): the companion leaves the world.</summary>
        public void FormClosed(FormCompanion form)
        {
            foreach (KeyValuePair<TickMascot, FormCompanion> p in _forms)
                if (ReferenceEquals(p.Value, form))
                {
                    _forms.Remove(p.Key);
                    p.Key.Dispose("closed");
                    return;
                }
        }

        // ---- ITickObserver: what the engine reports, as it happens

        public void BehaviourEntered(TickMascot mascot, string from, string to, string cause) { }
        public void BirthRequested(TickMascot parent, TickMascot child, int ordinal, int attempt) { }
        public void BirthRefused(TickMascot parent, int attempt, string reason)
        {
            _log("tick companion " + parent.Sid + " (" + parent.Type.Name + "): a birth was refused (" + reason + ")");
        }
        public void BirthActivated(TickMascot child) { }
        public void Transformed(TickMascot mascot, string fromImageSet, string toImageSet, string behaviour) { }
        public void DisposeRequested(TickMascot mascot, string reason) { }

        public void Disposed(TickMascot mascot)
        {
            FormCompanion form;
            if (!_forms.TryGetValue(mascot, out form)) return;
            _forms.Remove(mascot);
            _closeForm(form);
        }

        public void RandomDrawn(TickMascot mascot, string site, double value, string purpose) { }

        public void Fault(TickMascot mascot, string message)
        {
            _log("tick companion " + mascot.Sid + " (" + mascot.Type.Name + "): " + message);
        }

        public void Muted(TickMascot mascot, string clip) { }
        // The engine has already played or stopped the clip through the world's clips (HostTickClips).
        public void Sound(TickMascot mascot, string clip, string op) { }
        public void ScanTargeted(TickMascot mascot, string affordance, TickMascot target) { }
        public void Interacted(TickMascot mascot, IReadOnlyList<TickMascot> with) { }

        // The environment records every window it moved (Win32InteractiveWindow); a move is not logged, as it comes
        // every step while a window is carried. A minimise is rare and visible, so it is.
        public void WindowMoved(TickMascot mascot, TickRect from, TickRect to) { }

        public void WindowMinimized(TickMascot mascot)
        {
            _log("tick companion " + mascot.Sid + " (" + mascot.Type.Name + ") minimised the interactive window");
        }

        public void Dragged(TickMascot mascot, string op) { }

        public void Hotspot(TickMascot mascot, string op, string behaviour) { }

        /// <summary>The session's record of windows acted on, which "Put windows back" goes through: the real desktop's
        /// interactive window, or a self-test's fake.</summary>
        public ITickWindowRecord WindowRecord { get; set; }

        /// <summary>How many windows companions moved or minimised this session and not yet put back.</summary>
        public int WindowsToPutBack { get { return WindowRecord != null ? WindowRecord.RecordedCount : 0; } }

        /// <summary>"Put windows back" (the tray item's action): see <see cref="Win32InteractiveWindow.PutWindowsBack"/>.</summary>
        public int PutWindowsBack() { return WindowRecord != null ? WindowRecord.PutWindowsBack() : 0; }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            foreach (FormCompanion form in new List<FormCompanion>(_forms.Values)) _closeForm(form);
            _forms.Clear();
        }
    }

    /// <summary>
    /// The real desktop's interactive window. It is found as the source finds it (WindowsEnvironment.findActiveIE, the
    /// rules in <see cref="TickWindowRules"/>), and moved or minimised only when <see cref="TickWindowRules.Refusal"/>
    /// names nothing against it, kept inside the work area of the monitor it is on, and always asynchronously
    /// (SetWindowPos with SWP_ASYNCWINDOWPOS, ShowWindowAsync), so a hung application cannot stall the clock. Every
    /// window acted on is recorded the first time, with where it was, and <see cref="PutWindowsBack"/> restores it.
    /// Divergences from the source, each recorded: the area is the visible frame (DWM extended frame bounds), not
    /// GetWindowRect with its invisible resize borders; a move or minimise acts on the window found at the start of the
    /// step, checked again, rather than walking the windows a second time; this app's own windows are passed over.
    /// </summary>
    internal sealed class Win32InteractiveWindow : ITickWindowRecord
    {
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;
        private const uint MoveFlags = SWP_ASYNCWINDOWPOS | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
        private const int SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int GUI_INMOVESIZE = 0x00000002;
        private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const int TitleCapacity = 1024;   // WindowsEnvironment.isIE reads at most 1024 characters

        private readonly int _ownProcess = System.Environment.ProcessId;
        private readonly Dictionary<IntPtr, Saved> _record = new Dictionary<IntPtr, Saved>();
        private int _ownIntegrity = int.MinValue;   // read on first use; -1 when it cannot be read
        private IntPtr _active;

        private struct Saved
        {
            public int X, Y;
            public bool Minimised;
        }

        /// <summary>The InteractiveWindows list, '/'-separated as the source's setting; the source's shipped default.</summary>
        public string[] Whitelist = TickWindowRules.ParseList(
            WhitelistText(AppPaths.IsDataRootOverridden, System.Environment.GetEnvironmentVariable(TestTitlesVariable)));

        /// <summary>A test copy's title list (its own data root only): the real-window smoke confines companions to a
        /// disposable window whose title carries a unique token, because the shipped list's "Chat" and "Friends" match
        /// ordinary windows of the person running it.</summary>
        public const string TestTitlesVariable = "DESKTOP_AI_COMPANION_TICK_WINDOW_TITLES";

        /// <summary>The list text in force: the shipped default, or the test variable's when the data root is a test one.
        /// The installed app, on its real data root, never reads the variable.</summary>
        internal static string WhitelistText(bool testDataRoot, string testTitles)
        {
            return testDataRoot && !string.IsNullOrEmpty(testTitles) ? testTitles : TickWindowRules.DefaultWhitelist;
        }

        /// <summary>The InteractiveWindowsBlacklist list; the source ships none.</summary>
        public string[] Blacklist = TickWindowRules.ParseList(TickWindowRules.DefaultBlacklist);

        /// <summary>Why the last move or minimise did not happen, for diagnostics; null after one that did.</summary>
        public string LastRefusal { get; private set; }

        /// <summary>How many windows this session has moved or minimised and not yet put back.</summary>
        public int RecordedCount { get { return _record.Count; } }

        /// <summary>findActiveIE: the first interactive window in z-order, unless a maximised one comes first.</summary>
        public bool Find(TickRect screen, out TickRect bounds)
        {
            IntPtr found = IntPtr.Zero;
            TickRect foundBounds = default(TickRect);
            try
            {
                DesktopWindows.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    TickWindowFacts facts = WalkFacts(hWnd);
                    switch (TickWindowRules.Classify(facts, screen, Whitelist, Blacklist))
                    {
                        case TickWindowVerdict.Interactive:
                            found = hWnd;
                            foundBounds = facts.Bounds;
                            return false;
                        case TickWindowVerdict.StopWalk:
                            return false;
                        default:
                            return true;
                    }
                }, IntPtr.Zero);
            }
            catch
            {
                // A hostile or racing handle never stops the clock: no interactive window this step.
                found = IntPtr.Zero;
            }
            _active = found;
            bounds = foundBounds;
            return found != IntPtr.Zero;
        }

        /// <summary>moveActiveIE, under the safety rules: the window found this step, its visible frame's top-left
        /// put at <paramref name="topLeft"/> clamped inside its monitor's work area.</summary>
        public bool Move(TickPoint topLeft, out TickRect from, out TickRect to)
        {
            from = to = default(TickRect);
            try
            {
                Rectangle visual, frame;
                System.Windows.Forms.Screen[] screens;
                int monitor;
                if (!Actionable(out visual, out frame, out screens, out monitor)) return false;
                Rectangle wa = screens[monitor].WorkingArea;
                TickPoint at = TickWindowRules.ClampInto(topLeft, visual.Width, visual.Height, new TickRect(wa.X, wa.Y, wa.Width, wa.Height));
                from = new TickRect(visual.X, visual.Y, visual.Width, visual.Height);
                to = new TickRect(at.X, at.Y, visual.Width, visual.Height);
                Remember(_active, frame, false);
                if (at.X != visual.X || at.Y != visual.Y)
                    SetWindowPos(_active, IntPtr.Zero, at.X - (visual.X - frame.X), at.Y - (visual.Y - frame.Y), 0, 0, MoveFlags);
                return true;
            }
            catch (Exception e)
            {
                LastRefusal = "failed: " + e.Message;
                return false;
            }
        }

        /// <summary>minimizeActiveIE, under the safety rules, without activating anything.</summary>
        public bool Minimize()
        {
            try
            {
                Rectangle visual, frame;
                System.Windows.Forms.Screen[] screens;
                int monitor;
                if (!Actionable(out visual, out frame, out screens, out monitor)) return false;
                Remember(_active, frame, true);
                ShowWindowAsync(_active, SW_SHOWMINNOACTIVE);
                return true;
            }
            catch (Exception e)
            {
                LastRefusal = "failed: " + e.Message;
                return false;
            }
        }

        /// <summary>"Put windows back": each recorded window that still exists returns to where it was before the
        /// session first moved it, restored if a companion minimised it, unless it is now maximised, not responding,
        /// higher in integrity, or being moved or resized. The record is emptied.</summary>
        public int PutWindowsBack()
        {
            int restored = 0;
            foreach (KeyValuePair<IntPtr, Saved> p in _record)
            {
                IntPtr h = p.Key;
                try
                {
                    if (!IsWindow(h)) continue;
                    int pid;
                    uint thread = GetWindowThreadProcessId(h, out pid);
                    if (IsZoomed(h) || IsHungAppWindow(h) || IsElevated(pid) || IsMovingOrSizing(h, thread)) continue;
                    if (p.Value.Minimised && DesktopWindows.IsIconic(h)) ShowWindowAsync(h, SW_SHOWNOACTIVATE);
                    SetWindowPos(h, IntPtr.Zero, p.Value.X, p.Value.Y, 0, 0, MoveFlags);
                    restored++;
                }
                catch
                {
                    // One window that cannot be put back does not stop the others.
                }
            }
            _record.Clear();
            return restored;
        }

        // ---- the walk

        /// <summary>The facts the source's walk reads, in its order, stopping where its rules are already decided.</summary>
        private TickWindowFacts WalkFacts(IntPtr h)
        {
            var f = new TickWindowFacts();
            int pid;
            GetWindowThreadProcessId(h, out pid);
            f.OwnProcess = pid == _ownProcess;
            if (f.OwnProcess) return f;
            f.Visible = DesktopWindows.IsWindowVisible(h);
            if (!f.Visible) return f;
            f.Cloaked = DesktopWindows.IsCloaked(h);
            if (f.Cloaked) return f;
            f.Maximised = IsZoomed(h);
            if (f.Maximised) return f;
            f.Title = TitleOf(h);
            if (!TickWindowRules.IsTitleInteractive(f.Title, Whitelist, Blacklist)) return f;
            f.Minimised = DesktopWindows.IsIconic(h);
            if (f.Minimised) return f;
            Rectangle b = DesktopWindows.VisualBounds(h);
            f.Bounds = new TickRect(b.X, b.Y, b.Width, b.Height);
            return f;
        }

        private static string TitleOf(IntPtr h)
        {
            var text = new StringBuilder(TitleCapacity);
            int length = GetWindowTextW(h, text, TitleCapacity);
            return length > 0 ? text.ToString(0, Math.Min(length, text.Length)) : "";
        }

        // ---- acting

        /// <summary>The window found this step, still there and on a monitor, with nothing in the safety rules against
        /// it; <see cref="LastRefusal"/> says why not otherwise.</summary>
        private bool Actionable(out Rectangle visual, out Rectangle frame, out System.Windows.Forms.Screen[] screens, out int monitor)
        {
            visual = frame = Rectangle.Empty;
            screens = null;
            monitor = -1;
            IntPtr h = _active;
            if (h == IntPtr.Zero || !IsWindow(h))
            {
                LastRefusal = "no interactive window";
                return false;
            }
            DesktopWindows.RECT r;
            visual = DesktopWindows.VisualBounds(h);
            if (visual.Width <= 0 || visual.Height <= 0 || !DesktopWindows.GetWindowRect(h, out r))
            {
                LastRefusal = "no bounds";
                return false;
            }
            frame = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            screens = System.Windows.Forms.Screen.AllScreens;
            var monitors = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) monitors[i] = screens[i].Bounds;
            monitor = DesktopWindows.MonitorIndexFor(visual, monitors);
            if (monitor < 0)
            {
                LastRefusal = "on no monitor";
                return false;
            }

            int pid;
            uint thread = GetWindowThreadProcessId(h, out pid);
            var f = new TickWindowFacts
            {
                OwnProcess = pid == _ownProcess,
                Shell = DesktopWindows.IsShell(h),
                Visible = DesktopWindows.IsWindowVisible(h),
                Cloaked = DesktopWindows.IsCloaked(h),
                Minimised = DesktopWindows.IsIconic(h),
                Maximised = IsZoomed(h),
                Fullscreen = DesktopGeometry.IsFullscreenOnMonitor(frame, monitors[monitor]),
                ToolWindow = (DesktopWindows.ExtendedStyle(h) & WS_EX_TOOLWINDOW) != 0,
                Hung = IsHungAppWindow(h),
                MovingOrSizing = IsMovingOrSizing(h, thread),
                Elevated = IsElevated(pid),
            };
            // The fullscreen scan walks every window, so it runs only for a window nothing else has refused.
            if (TickWindowRules.Refusal(f) == null)
            {
                bool[] blocked = FullscreenScan.BlockedMonitors(OwnWindows());
                f.MonitorBlocked = monitor < blocked.Length && blocked[monitor];
            }
            LastRefusal = TickWindowRules.Refusal(f);
            return LastRefusal == null;
        }

        private void Remember(IntPtr h, Rectangle frame, bool minimised)
        {
            Saved saved;
            if (_record.TryGetValue(h, out saved))
            {
                // Where it was before the FIRST action is kept; a later minimise is added to it.
                if (minimised && !saved.Minimised)
                {
                    saved.Minimised = true;
                    _record[h] = saved;
                }
                return;
            }
            _record[h] = new Saved { X = frame.X, Y = frame.Y, Minimised = minimised };
        }

        /// <summary>This process's top-level windows, which never decide a monitor in the fullscreen scan.</summary>
        private HashSet<IntPtr> OwnWindows()
        {
            var own = new HashSet<IntPtr>();
            DesktopWindows.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == _ownProcess) own.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return own;
        }

        /// <summary>The user is moving or resizing it: its thread is in the modal move-size loop, or a mouse button is
        /// held on it as the foreground window (an application that drags itself). Unknown counts as moving.</summary>
        private static bool IsMovingOrSizing(IntPtr h, uint thread)
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            if (!GetGUIThreadInfo(thread, ref info)) return true;
            if ((info.flags & GUI_INMOVESIZE) != 0) return true;
            bool held = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
            return held && GetForegroundWindow() == h;
        }

        /// <summary>The window's process runs at a higher integrity level than this one. Unknown counts as higher.</summary>
        private bool IsElevated(int pid)
        {
            if (_ownIntegrity == int.MinValue) _ownIntegrity = IntegrityOf(GetCurrentProcess());
            if (_ownIntegrity < 0) return true;
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return true;
            try
            {
                int level = IntegrityOf(process);
                return level < 0 || level > _ownIntegrity;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// <summary>The mandatory integrity RID of a process's token, or -1.</summary>
        private static int IntegrityOf(IntPtr process)
        {
            IntPtr token;
            if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return -1;
            try
            {
                int length;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0) return -1;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length)) return -1;
                    IntPtr sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if (count < 1) return -1;
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public DesktopWindows.RECT rcCaret;
        }

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int command);
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll")]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }
}
