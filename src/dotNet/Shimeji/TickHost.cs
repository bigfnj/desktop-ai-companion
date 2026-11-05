using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The real desktop as the tick world sees it (the source's WindowsEnvironment): the virtual screen, each monitor,
    /// the primary work area (SPI_GETWORKAREA), the cursor with Location's smoothing, and the window events the
    /// companions' forms queued since the last step. No interactive window yet: the source's default title lists admit
    /// none, and window interaction arrives with the window family and its opt-in.
    /// </summary>
    internal sealed class Win32TickEnvironment : ITickEnvironment
    {
        private readonly TickArea _screen = new TickArea();
        private readonly TickArea _work = new TickArea();
        private readonly List<TickArea> _screens = new List<TickArea>();
        private readonly TickArea _window = new TickArea { Visible = false };
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

        /// <summary>A window event, delivered at the next step's start (the forms and the tick timer share the UI
        /// thread, so nothing races).</summary>
        public void Queue(TickInput input) { _queued.Add(input); }

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
    /// The host's per-type settings for tick companions. Sounds and Multiscreen are the app's existing settings; the
    /// lifecycle switches default on and window throwing off (the owner's decision: window actions are opt-in) until
    /// the Pets pane offers them per type.
    /// </summary>
    internal sealed class HostTickSettings : ITickSettings
    {
        private readonly Func<string, string> _typeIdOf;

        public HostTickSettings(Func<string, string> typeIdOf) { _typeIdOf = typeIdOf; }

        public bool Get(string setting, string imageSet)
        {
            LocalData data = Program.MyData;
            switch (setting)
            {
                case "Multiscreen": return data != null && data.GetMultiscreen();
                case "Sounds":
                    return data != null && data.GetPetSoundsEnabled() && (imageSet == null || data.IsPetSoundEnabled(_typeIdOf(imageSet)));
                case "Throwing": return false;
                default: return true;
            }
        }

        public bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet) { return true; }
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

        public TickHostType(string id, TickType type, Xml sprites, Animations animations = null)
        {
            Id = id;
            Type = type;
            Sprites = sprites;
            Animations = animations;
        }
    }

    /// <summary>
    /// Drives every tick companion from one 40 ms clock: steps the world, then gives each companion's window its frame
    /// and position. Births open windows, removals close them. The forms keep their own timers off (FormCompanion tick
    /// mode); their left-button events come back here as tick input.
    /// </summary>
    internal sealed class TickHost : ITickObserver, IDisposable
    {
        public const int TickMilliseconds = 40;

        private readonly TickWorld _world;
        private readonly Win32TickEnvironment _environment;
        private readonly Timer _timer;
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
            _world = new TickWorld(_environment, new HostTickRandom(), new HostTickSettings(TypeIdOf), this);
            _world.Start();
            _timer = new Timer { Interval = TickMilliseconds };
            _timer.Tick += delegate { Step(); };
        }

        public TickWorld World { get { return _world; } }
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
            _world.AddType(type.Type);
        }

        private string TypeIdOf(string imageSet)
        {
            TickHostType t;
            return _typesByName.TryGetValue(imageSet, out t) ? t.Id : imageSet;
        }

        /// <summary>A new companion of a type, placed at <paramref name="anchor"/> facing as given, entering
        /// <paramref name="behaviour"/> (or one chosen from the route when null). It appears at the next step, in
        /// <paramref name="form"/> when one is given (the app creates a spawned companion's window itself, so the window
        /// is in its pet list from the start), else in one opened then.</summary>
        public TickMascot Spawn(string imageSet, TickPoint anchor, bool lookRight, string behaviour, FormCompanion form = null)
        {
            TickType type = _world.FindType(imageSet);
            if (type == null) throw new InvalidOperationException("no tick companion type '" + imageSet + "'");
            TickMascot m = _world.CreateAtStart("t" + (++_nextSid), type, anchor, lookRight, behaviour);
            if (form != null)
            {
                form.AttachTick(this, m);
                _forms[m] = form;
            }
            if (!_timer.Enabled) _timer.Start();
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
            return Spawn(imageSet, new TickPoint(-4000, -4000), lookRight, behaviour, form);
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
            // ImagePairs: facing right shows the explicit right frame, else the left frame mirrored.
            Image frame = m.PoseRight
                ? (m.Pose.Right >= 0 ? t.Sprites.GetSpriteFrame(m.Pose.Right, false) : t.Sprites.GetSpriteFrame(m.Pose.Frame, true))
                : t.Sprites.GetSpriteFrame(m.Pose.Frame, false);
            TickRect b = m.Bounds;
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

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            foreach (FormCompanion form in new List<FormCompanion>(_forms.Values)) _closeForm(form);
            _forms.Clear();
        }
    }
}
