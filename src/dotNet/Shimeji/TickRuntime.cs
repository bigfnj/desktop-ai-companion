using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>The companion left its border during an action step (LostGroundException): it enters Fall.</summary>
    internal sealed class TickLostGroundException : Exception { }

    /// <summary>The companion cannot go on (CantBeAliveException): it is disposed, with the reason reported.</summary>
    internal sealed class TickCantBeAliveException : Exception
    {
        public TickCantBeAliveException(string message) : base(message) { }

        /// <summary>Thrown where a typed cause arises: <paramref name="cause"/> rides the exception to whichever wrapper
        /// catches it, so the wrapper reports the cause the failed lookup or read formed and never forms one itself.</summary>
        public TickCantBeAliveException(string message, TickFaultCause cause) : base(message) { Cause = cause; }

        /// <summary>The typed cause (CONTRACT 10.3.2), or null: unclassified.</summary>
        public readonly TickFaultCause Cause;
    }

    /// <summary>
    /// CONTRACT 10.3.2 and 10.3.3: a fault's typed cause, formed only where the cause arises (the failed lookup or read)
    /// from operands already stored at that site: the member searched and the name it was asked for, or the failing
    /// action's own refKey (its actionStack entry) and the parameter it read. Nothing is evaluated again to form one,
    /// and no message is ever read to choose a code. Each factory answers null when an operand is not available (an
    /// action with no refKey: a pack run without a parity map, as the product runs), and null is reported as
    /// <c>unclassified</c> with a null lookup. Requested names are taken as the runtime looked them up, before any
    /// <c>?? ""</c>: "", " " and null stay three operands (CH6).
    /// </summary>
    internal sealed class TickFaultCause
    {
        public const string BehaviorMissing = "behavior-missing";
        public const string ActionMissing = "action-missing";
        public const string VariantMissing = "variant-missing";
        public const string ParameterInvalid = "parameter-invalid";
        public const string ScanTargetMissing = "scan-target-missing";
        public const string Unclassified = "unclassified";

        public readonly string Code;
        public readonly string ImageSet;
        public readonly string Behavior;
        public readonly string Action;
        public readonly string RefKey;
        public readonly string Parameter;

        private TickFaultCause(string code, string imageSet, string behavior, string action, string refKey, string parameter)
        {
            Code = code;
            ImageSet = imageSet;
            Behavior = behavior;
            Action = action;
            RefKey = refKey;
            Parameter = parameter;
        }

        /// <summary>A behaviour <paramref name="requested"/> by name is not a root behaviour of <paramref name="searched"/>,
        /// the member whose configuration was searched (a birth's child after the BornMascot fallback, a Transform's
        /// target, an arrival target's own member).</summary>
        public static TickFaultCause OfMissingBehaviour(TickType searched, string requested)
        {
            if (searched == null || string.IsNullOrEmpty(searched.Name)) return null;
            return new TickFaultCause(BehaviorMissing, searched.Name, requested, null, null, null);
        }

        /// <summary>An action <paramref name="requested"/> by name that <paramref name="searched"/> does not declare.</summary>
        public static TickFaultCause OfMissingAction(TickType searched, string requested)
        {
            if (searched == null || string.IsNullOrEmpty(searched.Name)) return null;
            return new TickFaultCause(ActionMissing, searched.Name, null, requested, null, null);
        }

        /// <summary>An initialised action with no variant it can play, named by its own stored refKey.</summary>
        public static TickFaultCause OfMissingVariant(TickAction action)
        {
            return action == null || string.IsNullOrEmpty(action.RefKey) ? null : new TickFaultCause(VariantMissing, null, null, null, action.RefKey, null);
        }

        /// <summary>A parameter or guard attribute an initialised action read: <paramref name="parameter"/> is the host's
        /// name for a built-in, which is the source schema's canonical key. A synthetic lowering parameter is never passed
        /// here (its read reports unclassified, CH8).</summary>
        public static TickFaultCause OfParameter(TickAction action, string parameter)
        {
            if (action == null || string.IsNullOrEmpty(action.RefKey) || string.IsNullOrEmpty(parameter)) return null;
            return new TickFaultCause(ParameterInvalid, null, null, null, action.RefKey, parameter);
        }

        /// <summary>An initialised action that needs its scan target and has none.</summary>
        public static TickFaultCause OfMissingScanTarget(TickAction action)
        {
            return action == null || string.IsNullOrEmpty(action.RefKey) ? null : new TickFaultCause(ScanTargetMissing, null, null, null, action.RefKey, null);
        }
    }

    /// <summary>CONTRACT 10.3.4's closed wrapper vocabulary: the seven sites that catch a fault and choose its consequence.
    /// A fault the companion's step catches, or one reported where it arises, has no wrapper (null, written <c>[]</c>).
    /// No wrapper rethrows, so a fault carries at most one (C.2 H10).</summary>
    internal static class TickFaultVia
    {
        public const string Startup = "startup";
        public const string Press = "press";
        public const string Release = "release";
        public const string Birth = "birth";
        public const string Transform = "transform";
        public const string Arrive = "arrive";
        public const string Interact = "interact";
    }

    /// <summary>One random stream for the whole world. The world names each draw's site when it logs it.</summary>
    internal interface ITickRandom
    {
        double Next();
    }

    /// <summary>The settings the source reads per image set (Main.getMascotBooleanSetting, isBehaviorEnabled).</summary>
    internal interface ITickSettings
    {
        /// <summary><paramref name="setting"/> is the source's key: Breeding, Transients, Transformation, Multiscreen,
        /// Throwing, Sounds. <paramref name="imageSet"/> null asks for the global value.</summary>
        bool Get(string setting, string imageSet);

        bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet);
    }

    /// <summary>
    /// What the world reports as it happens. Called only with values the engine already computed at the source's own
    /// evaluation sites, so attaching an observer never changes behaviour (CONTRACT section 5). Null in production.
    /// </summary>
    internal interface ITickObserver
    {
        void BehaviourEntered(TickMascot mascot, string from, string to, string cause);
        void BirthRequested(TickMascot parent, TickMascot child, int ordinal, int attempt);
        void BirthRefused(TickMascot parent, int attempt, string reason);
        void BirthActivated(TickMascot child);
        void Transformed(TickMascot mascot, string fromImageSet, string toImageSet, string behaviour);
        void DisposeRequested(TickMascot mascot, string reason);
        void Disposed(TickMascot mascot);
        void RandomDrawn(TickMascot mascot, string site, double value, string purpose);

        /// <summary>A fault: <paramref name="message"/> in the engine's own words (the product log and its throttle read
        /// it); <paramref name="cause"/> the typed cause formed where it arose, or null for unclassified; <paramref
        /// name="via"/> the wrapper that caught it (<see cref="TickFaultVia"/>), or null when the step caught it or it was
        /// reported where it arose.</summary>
        void Fault(TickMascot mascot, string message, TickFaultCause cause, string via);

        /// <summary>Mute ran: a named clip to stop, or null for every clip (CONTRACT event `mute`).</summary>
        void Muted(TickMascot mascot, string clip);

        /// <summary>A clip started (op "start", at the companion's apply) or a running one was stopped (op "stop", by
        /// this companion's Mute) (CONTRACT event `sound`; <see cref="TickSound"/>).</summary>
        void Sound(TickMascot mascot, string clip, string op);

        /// <summary>A scan ran (Manager.getMascotWithAffordance): the affordance sought and the companion found, or
        /// null (CONTRACT event `scan-target`).</summary>
        void ScanTargeted(TickMascot mascot, string affordance, TickMascot target);

        /// <summary>An Interact step plays: the other active companions sharing this one's anchor, in manager order
        /// (CONTRACT event `interact`).</summary>
        void Interacted(TickMascot mascot, IReadOnlyList<TickMascot> with);
        /// <summary>A window action moved the interactive window (CONTRACT event `window-move`).</summary>
        void WindowMoved(TickMascot mascot, TickRect from, TickRect to);

        /// <summary>A window action minimised the interactive window (CONTRACT event `window-minimize`).</summary>
        void WindowMinimized(TickMascot mascot);

        /// <summary>A press that drags the companion (op "start", UserBehavior.mousePressed's drag branch, written once
        /// Dragged is built, so a press whose Dragged cannot be built writes none) or a release that throws a dragged one
        /// (op "end", mouseReleased, written before Thrown is built) (CONTRACT v1.1 event `drag`). Written before the
        /// behaviour change the input causes.</summary>
        void Dragged(TickMascot mascot, string op);

        /// <summary>A press a hotspot takes (op "press", with the hotspot's behaviour name, never null: a hotspot with no
        /// behaviour is not enabled, so it never takes the press, <see cref="TickWorld.IsBehaviourEnabledByName"/>) or the
        /// release of a held hotspot (op "release", behaviour null: the source's release only clears the hold) (CONTRACT
        /// event `hotspot`). Written before the behaviour change the input causes.</summary>
        void Hotspot(TickMascot mascot, string op, string behaviour);
    }

    // ------------------------------------------------------------------ parameters and scripts

    /// <summary>
    /// One action instance's variables (the source's VariableMap): the action's own parameters overridden by the
    /// incoming ones, each lazily evaluated and cached by its kind, plus the locals an action publishes (putVariable),
    /// which replace a parameter of the same name for the rest of the instance's life, as the source's Constant does.
    /// </summary>
    internal sealed class TickVars
    {
        private readonly Dictionary<string, TickParamDef> _defs;
        private readonly Dictionary<string, ScriptValue> _cache = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptValue> _locals = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        private readonly HashSet<string> _evaluating = new HashSet<string>(StringComparer.Ordinal);

        public TickVars(Dictionary<string, TickParamDef> own, Dictionary<string, TickParamDef> incoming)
        {
            _defs = new Dictionary<string, TickParamDef>(own, StringComparer.Ordinal);
            if (incoming != null)
                foreach (KeyValuePair<string, TickParamDef> p in incoming) _defs[p.Key] = p.Value;
        }

        /// <summary>ActionBase.init: every script value is cleared (Script.init).</summary>
        public void Init() { _cache.Clear(); }

        /// <summary>ActionBase.initFrame: frame-cached (#{}) values are cleared.</summary>
        public void InitFrame()
        {
            if (_cache.Count == 0) return;
            var drop = new List<string>();
            foreach (string key in _cache.Keys)
                if (_defs[key].Cache == TickCache.Frame) drop.Add(key);
            foreach (string key in drop) _cache.Remove(key);
        }

        public bool Has(string name) { return _locals.ContainsKey(name) || _defs.ContainsKey(name); }

        public void Put(string name, ScriptValue value) { _locals[name] = value; }

        public ScriptValue Get(string name, TickScriptContext context)
        {
            ScriptValue v;
            if (_locals.TryGetValue(name, out v)) return v;
            TickParamDef def;
            if (!_defs.TryGetValue(name, out def)) return ScriptValue.Null;
            if (def.Program == null) return def.Literal;
            if (_cache.TryGetValue(name, out v)) return v;
            if (!_evaluating.Add(name))
            {
                context.ReportFault(def.Program.Source, "parameter '" + name + "' refers to itself");
                return ScriptValue.Null;
            }
            try
            {
                v = context.Evaluate(def.Program, "param:" + name);
            }
            finally
            {
                _evaluating.Remove(name);
            }
            // Script.get keeps only a non-null value; a null is evaluated again at its next use.
            if (v.Kind != ScriptValueKind.Null) _cache[name] = v;
            return v;
        }
    }

    /// <summary>
    /// The SafeScript view of one companion: its action's variables (or, for behaviour selection, the type's
    /// constants), its anchor, facing and counts, and the environment as the source's MascotEnvironment reports it.
    /// </summary>
    internal sealed class TickScriptContext : IScriptContext
    {
        private readonly TickWorld _world;
        private readonly TickMascot _mascot;
        private readonly TickVars _vars;
        private readonly Dictionary<string, ScriptValue> _constants;
        private ScriptProgram _program;
        private string _attribute;

        public TickScriptContext(TickWorld world, TickMascot mascot, TickVars vars, Dictionary<string, ScriptValue> constants)
        {
            _world = world;
            _mascot = mascot;
            _vars = vars;
            _constants = constants;
        }

        /// <summary>Evaluate a program, naming the attribute it came from so each random draw gets its site.</summary>
        public ScriptValue Evaluate(ScriptProgram program, string attribute)
        {
            ScriptProgram outerProgram = _program;
            string outerAttribute = _attribute;
            _program = program;
            _attribute = attribute;
            try
            {
                return program.Evaluate(this);
            }
            finally
            {
                _program = outerProgram;
                _attribute = outerAttribute;
            }
        }

        public bool TryGetName(string name, out ScriptValue value)
        {
            if (_vars != null && _vars.Has(name))
            {
                value = _vars.Get(name, this);
                return true;
            }
            if (_constants != null && _constants.TryGetValue(name, out value)) return true;
            value = ScriptValue.Null;
            return false;
        }

        public void GetAnchor(out double x, out double y)
        {
            x = _mascot.Anchor.X;
            y = _mascot.Anchor.Y;
        }

        public void GetCursor(out double x, out double y, out double dx, out double dy)
        {
            TickPoint c = _world.Environment.Cursor, d = _world.Environment.CursorDelta;
            x = c.X; y = c.Y; dx = d.X; dy = d.Y;
        }

        public bool LookRight { get { return _mascot.LookRight; } }
        public bool Dragging { get { return _mascot.Dragging; } }
        public int Count { get { return _mascot.InManager ? _world.CountOf(_mascot.Type) : 0; } }
        public int TotalCount { get { return _mascot.InManager ? _world.ActiveCount : 0; } }
        public double Time { get { return _mascot.Time; } }

        public ScriptArea GetArea(ScriptAreaId area)
        {
            TickArea a;
            switch (area)
            {
                case ScriptAreaId.WorkArea: a = _mascot.Environment.GetWorkArea(); break;
                case ScriptAreaId.Screen:
                case ScriptAreaId.ComplexScreen: a = _world.Environment.Screen; break;
                case ScriptAreaId.ActiveWindow: a = _mascot.Environment.GetActiveWindow(); break;
                default: return new ScriptArea();
            }
            return new ScriptArea(a.Left, a.Top, a.Right, a.Bottom, a.Visible, a.DLeft, a.DTop, a.DRight, a.DBottom);
        }

        public ScriptBorder GetBorder(ScriptBorderId border)
        {
            ITickBorder b = Resolve(border);
            var fc = b as TickFloorCeiling;
            if (fc != null) return new ScriptBorder(0, fc.Y, LeftOf(border, fc), RightOf(border, fc));
            var wall = b as TickWall;
            if (wall != null) return new ScriptBorder(wall.X, 0, TopOf(border), BottomOf(border));
            return new ScriptBorder();
        }

        public bool IsOnBorder(ScriptBorderId border, double x, double y)
        {
            return Resolve(border).IsOn(new TickPoint(JavaInt(x), JavaInt(y)));
        }

        public bool TryGetVariable(string key, out ScriptValue value) { return _mascot.Variables.TryGetValue(key, out value); }
        public void SetVariable(string key, ScriptValue value) { _mascot.Variables[key] = value; }
        public int VariableCount { get { return _mascot.Variables.Count; } }

        /// <summary>A script's Math.random. Its site name is the host's own (image set, attribute label, ordinal), not
        /// CONTRACT v1.1 section 2's <c>script:&lt;refKey&gt;#&lt;attribute&gt;#&lt;k&gt;</c>: map/2 has no table from the
        /// host's (node, attribute, ordinal) to the source site, and conversion merges and moves script expressions, so the
        /// comparer judges a directed comparison with a script draw pending (section 9). When a map revision adds the
        /// table, name the site through the member that owns the program, not _mascot.Type, which a Transform replaces.</summary>
        public double Random(int site)
        {
            int k = 1;
            if (_program != null)
            {
                int i = 0;
                foreach (int offset in _program.RandomSites)
                {
                    i++;
                    if (offset == site) { k = i; break; }
                }
            }
            return _world.Draw(_mascot, "script:" + _mascot.Type.Name + "#" + (_attribute ?? "?") + "#" + k, "script");
        }

        // A script's own fault (a parameter that refers to itself, a script error) has no typed cause: the context does not
        // know which action or parameter is being read, so it is unclassified, reported where it arises.
        public void ReportFault(string source, string message) { _world.ReportFault(_mascot, message + " in '" + source + "'", null, null); }

        private ITickBorder Resolve(ScriptBorderId border)
        {
            TickMascotEnvironment env = _mascot.Environment;
            switch (border)
            {
                case ScriptBorderId.Floor: return env.GetFloor();
                case ScriptBorderId.Ceiling: return env.GetCeiling();
                case ScriptBorderId.Wall: return env.GetWall();
                case ScriptBorderId.WorkAreaLeft: return env.GetWorkArea().LeftBorder;
                case ScriptBorderId.WorkAreaTop: return env.GetWorkArea().TopBorder;
                case ScriptBorderId.WorkAreaRight: return env.GetWorkArea().RightBorder;
                case ScriptBorderId.WorkAreaBottom: return env.GetWorkArea().BottomBorder;
                case ScriptBorderId.ScreenLeft:
                case ScriptBorderId.ComplexScreenLeft: return _world.Environment.Screen.LeftBorder;
                case ScriptBorderId.ScreenTop:
                case ScriptBorderId.ComplexScreenTop: return _world.Environment.Screen.TopBorder;
                case ScriptBorderId.ScreenRight:
                case ScriptBorderId.ComplexScreenRight: return _world.Environment.Screen.RightBorder;
                case ScriptBorderId.ScreenBottom:
                case ScriptBorderId.ComplexScreenBottom: return _world.Environment.Screen.BottomBorder;
                case ScriptBorderId.ActiveWindowLeft: return env.GetActiveWindow().LeftBorder;
                case ScriptBorderId.ActiveWindowTop: return env.GetActiveWindow().TopBorder;
                case ScriptBorderId.ActiveWindowRight: return env.GetActiveWindow().RightBorder;
                case ScriptBorderId.ActiveWindowBottom: return env.GetActiveWindow().BottomBorder;
                default: return TickNotOnBorder.Instance;
            }
        }

        // A border's span is its area's; the area is found again rather than carried on the border.
        private TickArea AreaOf(ScriptBorderId border)
        {
            switch (border)
            {
                case ScriptBorderId.WorkAreaLeft: case ScriptBorderId.WorkAreaTop:
                case ScriptBorderId.WorkAreaRight: case ScriptBorderId.WorkAreaBottom:
                    return _mascot.Environment.GetWorkArea();
                case ScriptBorderId.ActiveWindowLeft: case ScriptBorderId.ActiveWindowTop:
                case ScriptBorderId.ActiveWindowRight: case ScriptBorderId.ActiveWindowBottom:
                    return _mascot.Environment.GetActiveWindow();
                case ScriptBorderId.Floor: case ScriptBorderId.Ceiling: case ScriptBorderId.Wall:
                {
                    ITickBorder b = Resolve(border);
                    TickArea work = _mascot.Environment.GetWorkArea(), active = _mascot.Environment.GetActiveWindow();
                    if (ReferenceEquals(b, active.TopBorder) || ReferenceEquals(b, active.BottomBorder)
                        || ReferenceEquals(b, active.LeftBorder) || ReferenceEquals(b, active.RightBorder)) return active;
                    return work;
                }
                default: return _world.Environment.Screen;
            }
        }

        private double LeftOf(ScriptBorderId border, TickFloorCeiling fc) { return AreaOf(border).Left; }
        private double RightOf(ScriptBorderId border, TickFloorCeiling fc) { return AreaOf(border).Right; }
        private double TopOf(ScriptBorderId border) { return AreaOf(border).Top; }
        private double BottomOf(ScriptBorderId border) { return AreaOf(border).Bottom; }

        /// <summary>Java's double-to-int narrowing: NaN is 0, out-of-range values saturate, the rest truncate.</summary>
        public static int JavaInt(double d)
        {
            if (double.IsNaN(d)) return 0;
            if (d >= int.MaxValue) return int.MaxValue;
            if (d <= int.MinValue) return int.MinValue;
            return (int)d;
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>
    /// The source's ActionBase: parameters, guard (Condition, Duration, Draggable), offers, hotspots, variants, and the
    /// action clock (the companion's time minus the time the action started). Subclasses mirror the source classes
    /// one for one; their method bodies follow the source's order of evaluation, because evaluation has side effects
    /// (random draws, Sequence seeking).
    /// </summary>
    internal abstract class TickAction
    {
        public const int DefaultDuration = int.MaxValue;

        protected readonly TickWorld World;
        public readonly TickActDef Def;
        public readonly TickVars Vars;
        public string RefKey;

        /// <summary>The type whose definition this action was built from (set by TickWorld.BuildAction). A pose the action
        /// plays is that type's image, as a source Pose holds the image its own image set loaded (Pose.java:70-75,
        /// AnimationBuilder.java:99), whatever the companion's type is now: after a Transform that could not enter its
        /// behaviour the companion is the new type in the OLD type's behaviour, and its poses are still the old type's.</summary>
        public TickType OwnerType;
        protected TickMascot Mascot;
        protected TickScriptContext Context;
        private int _startTime;

        protected TickAction(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            World = world;
            Def = def;
            Vars = new TickVars(def.Params, incoming);
            RefKey = def.RefKey;
        }

        public string Name { get { return Def.Name; } }

        public virtual void Init(TickMascot mascot)
        {
            Mascot = mascot;
            // The scan target belongs to the action that chose it; a scanning class sets it again after this.
            mascot.Target = null;
            Context = new TickScriptContext(World, mascot, Vars, null);
            SetTime(0);
            Vars.Init();
        }

        /// <summary>ActionBase.next: clear the frame caches, replace the offers, refresh the hotspots, then tick.</summary>
        public virtual void Next()
        {
            Vars.InitFrame();
            Mascot.Affordances.Clear();
            foreach (string offer in CurrentOffers())
                if (!IsJavaBlank(offer)) Mascot.Affordances.Add(offer);
            RefreshHotspots();
            Tick();
        }

        public virtual bool HasNext() { return BaseHasNext(); }

        /// <summary>ActionBase.hasNext, reachable from a subclass that overrides <see cref="HasNext"/> the way the
        /// source's <c>super.hasNext()</c> reaches it past an intermediate class.</summary>
        protected bool BaseHasNext()
        {
            bool effective = IsEffective();
            bool intime = Time < Duration();
            return effective && intime;
        }

        protected abstract void Tick();

        /// <summary>The leaf that is running: the action itself, or a composite's active child's leaf.</summary>
        public virtual TickAction Leaf { get { return this; } }

        /// <summary>The refKeys from this action down to its leaf.</summary>
        public virtual void AppendStack(List<string> stack) { stack.Add(RefKey); }

        public virtual bool IsDraggable() { return EvalBool("Draggable", Def.GuardDraggable, true); }

        public int Time { get { return Mascot.Time - _startTime; } }
        protected void SetTime(int time) { _startTime = Mascot.Time - time; }

        protected bool IsEffective() { return EvalBool("Condition", Def.GuardWhen, true); }

        protected int Duration() { return EvalInt("Duration", Def.GuardDuration, DefaultDuration); }

        /// <summary>Java's <c>trim().length() == 0</c>: blank when every character is at or below U+0020. .NET's Trim also
        /// strips Unicode spaces (a no-break space, an ideographic space), which Java keeps, so an offer of those is real.</summary>
        internal static bool IsJavaBlank(string s)
        {
            foreach (char c in s) if (c > ' ') return false;
            return true;
        }

        private IEnumerable<string> CurrentOffers()
        {
            if (Def.Offers != null) return Def.Offers;
            string one = EvalString("Affordance", null, "");
            return one.Length > 0 ? new[] { one } : new string[0];
        }

        /// <summary>ActionBase.refreshHotspots: the current variant's hotspots. The source reads getAnimation() twice
        /// here (the null test and the loop), and a variant condition is evaluated each time; kept.</summary>
        protected virtual void RefreshHotspots()
        {
            Mascot.Hotspots.Clear();
            if (CurrentVariant() != null)
            {
                TickVariantDef v = CurrentVariant();
                if (v != null) Mascot.Hotspots.AddRange(v.Hotspots);
            }
        }

        /// <summary>ActionBase.getAnimation: the first variant whose condition holds.</summary>
        protected virtual TickVariantDef CurrentVariant()
        {
            foreach (TickVariantDef v in Def.Variants)
                if (VariantHolds(v)) return v;
            return null;
        }

        protected bool VariantHolds(TickVariantDef v)
        {
            // A variant's condition is no authored parameter: a scripted one is the converter's synthetic
            // AnimationCondition<n>, a literal one a selection condition, so its fault is unclassified (CONTRACT 10.3.3, CH8).
            return v.When == null || Truth(Context.Evaluate(v.When, "variant"), "variant condition", null);
        }

        /// <summary>Animation.next: the pose for this action age moves the companion and sets its image.</summary>
        protected void PlayPose(TickVariantDef variant)
        {
            if (variant == null) throw new TickCantBeAliveException("action '" + Name + "' has no variant whose condition holds", TickFaultCause.OfMissingVariant(this));
            TickPoseDef pose = variant.PoseAt(Time);
            Mascot.ApplyPose(pose, OwnerType);
        }

        // ---- parameter reads: a guard attribute when the document gives one, else the parameter, else the default

        protected ScriptValue Read(string name, ScriptProgram guard)
        {
            if (guard != null) return Context.Evaluate(guard, "guard:" + name);
            return Vars.Has(name) ? Vars.Get(name, Context) : ScriptValue.Null;
        }

        protected bool Present(string name, ScriptProgram guard) { return guard != null || Vars.Has(name); }

        // protected internal: the source's eval is protected, which in Java includes the package, and Breed.Delegate
        // (TickBreedDelegate) reads its action's parameters through it.
        protected internal bool EvalBool(string name, ScriptProgram guard, bool fallback)
        {
            if (!Present(name, guard)) return fallback;
            return Truth(Read(name, guard), name, name);
        }

        // A read that is not of the required type is parameter-invalid where it arises (via []), named by this action's
        // stored refKey and the built-in's canonical key, which is the name read here.
        protected internal int EvalInt(string name, ScriptProgram guard, int fallback)
        {
            if (!Present(name, guard)) return fallback;
            ScriptValue v = Read(name, guard);
            if (v.Kind != ScriptValueKind.Number)
            {
                World.ReportFault(Mascot, Name + ": " + name + " is not a number (" + v.ToDisplayString() + ")", TickFaultCause.OfParameter(this, name), null);
                if (v.Kind == ScriptValueKind.Null) return fallback;
            }
            return TickScriptContext.JavaInt(v.ToNumber());
        }

        protected internal int EvalInt(string name, int fallback) { return EvalInt(name, null, fallback); }

        /// <summary>eval(name, Number.class, fallback).doubleValue(): as <see cref="EvalInt(string, int)"/>, unnarrowed.</summary>
        protected internal double EvalDouble(string name, double fallback)
        {
            if (!Present(name, null)) return fallback;
            ScriptValue v = Read(name, null);
            if (v.Kind != ScriptValueKind.Number)
            {
                World.ReportFault(Mascot, Name + ": " + name + " is not a number (" + v.ToDisplayString() + ")", TickFaultCause.OfParameter(this, name), null);
                if (v.Kind == ScriptValueKind.Null) return fallback;
            }
            return v.ToNumber();
        }

        protected internal string EvalString(string name, ScriptProgram guard, string fallback)
        {
            if (!Present(name, guard)) return fallback;
            ScriptValue v = Read(name, guard);
            return v.Kind == ScriptValueKind.Null ? null : v.ToDisplayString();
        }

        /// <param name="parameter">The authored parameter read, typed parameter-invalid when it is not a boolean; null for
        /// a read that names no authored parameter (a variant condition), which is unclassified.</param>
        protected bool Truth(ScriptValue v, string what, string parameter)
        {
            if (v.Kind == ScriptValueKind.Boolean) return v.Number != 0;
            // The source casts to Boolean and a non-boolean ends the companion; the engine reports it and reads the
            // value's truth instead (recorded as approximate where a converted pack depends on it).
            World.ReportFault(Mascot, Name + ": " + what + " is not a boolean (" + v.ToDisplayString() + ")",
                parameter == null ? null : TickFaultCause.OfParameter(this, parameter), null);
            return v.IsTruthy;
        }
    }

    /// <summary>BorderedAction: takes the border it starts on and is carried by it.</summary>
    internal abstract class TickBorderedAction : TickAction
    {
        protected ITickBorder Border;

        protected TickBorderedAction(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming)
            : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            string type = Vars.Has("BorderType") ? EvalString("BorderType", null, null) : (Def.Border.Length > 0 ? Def.Border : null);
            // Set only when the type names a border, never cleared (BorderedAction.java:39-51): a re-initialised action
            // whose scripted type turns blank keeps the border it had.
            if (type == "Ceiling") Border = Mascot.Environment.GetCeiling();
            else if (type == "Wall") Border = Mascot.Environment.GetWall();
            else if (type == "Floor") Border = Mascot.Environment.GetFloor();
        }

        protected override void Tick()
        {
            if (Border != null) Mascot.Anchor = Border.Move(Mascot.Anchor);
        }

        protected void CheckGround()
        {
            if (Border != null && !Border.IsOn(Mascot.Anchor)) throw new TickLostGroundException();
        }
    }

    internal sealed class TickStay : TickBorderedAction
    {
        public TickStay(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Tick()
        {
            base.Tick();
            CheckGround();
            PlayPose(CurrentVariant());
        }
    }

    /// <summary>Animate: Stay that ends with its animation.</summary>
    internal class TickAnimate : TickBorderedAction
    {
        public TickAnimate(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Tick()
        {
            base.Tick();
            CheckGround();
            PlayPose(CurrentVariant());
        }

        public override bool HasNext()
        {
            // The source evaluates the animation's duration BEFORE super.hasNext(): order kept.
            TickVariantDef v = CurrentVariant();
            bool intime = v != null && Time < v.Duration;
            return BaseHasNext() && intime;
        }

        protected int AnimationDuration()
        {
            TickVariantDef v = CurrentVariant();
            return v == null ? 0 : v.Duration;
        }
    }

    /// <summary>Move: walk to TargetX/TargetY, turning (with an IsTurn variant) when the direction changes. Not sealed:
    /// MoveWithTurn and BreedMove extend it (TickMotionFamily.cs).</summary>
    internal class TickMove : TickBorderedAction
    {
        private const int DefaultTarget = int.MaxValue;
        private bool _turning;
        private bool? _hasTurning;

        public TickMove(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            int targetX = EvalInt("TargetX", DefaultTarget);
            int targetY = EvalInt("TargetY", DefaultTarget);
            // The source names this "hasNotReached" and means the opposite: either axis at its target ends the move.
            bool reached = (targetX != int.MinValue && Mascot.Anchor.X == targetX)
                || (targetY != int.MinValue && Mascot.Anchor.Y == targetY);
            return BaseHasNext() && (!reached || _turning);
        }

        protected override void Tick()
        {
            base.Tick();
            CheckGround();
            int targetX = EvalInt("TargetX", DefaultTarget);
            int targetY = EvalInt("TargetY", DefaultTarget);
            bool down = false;
            if (targetX != DefaultTarget && Mascot.Anchor.X != targetX)
            {
                _turning = HasTurningVariant() && (_turning || (Mascot.Anchor.X < targetX) != Mascot.LookRight);
                Mascot.LookRight = Mascot.Anchor.X < targetX;
            }
            if (targetY != DefaultTarget) down = Mascot.Anchor.Y < targetY;
            if (_turning && Time >= TurnDuration()) _turning = false;
            PlayPose(CurrentVariant());
            if (targetX != DefaultTarget
                && ((Mascot.LookRight && Mascot.Anchor.X >= targetX) || (!Mascot.LookRight && Mascot.Anchor.X <= targetX)))
                Mascot.Anchor = new TickPoint(targetX, Mascot.Anchor.Y);
            if (targetY != DefaultTarget
                && ((down && Mascot.Anchor.Y >= targetY) || (!down && Mascot.Anchor.Y <= targetY)))
                Mascot.Anchor = new TickPoint(Mascot.Anchor.X, targetY);
        }

        // Move.getAnimation: the first variant whose condition holds AND whose turn flag matches.
        protected override TickVariantDef CurrentVariant()
        {
            foreach (TickVariantDef v in Def.Variants)
                if (VariantHolds(v) && v.Turn == _turning) return v;
            return null;
        }

        private int TurnDuration()
        {
            TickVariantDef v = CurrentVariant();
            if (v == null) throw new TickCantBeAliveException("action '" + Name + "' has no variant for turning=" + _turning, TickFaultCause.OfMissingVariant(this));
            return v.Duration;
        }

        /// <summary>Move.isTurning.</summary>
        protected bool IsTurning { get { return _turning; } }

        /// <summary>Move.hasTurningAnimation (MoveWithTurn answers true without looking).</summary>
        protected virtual bool HasTurningVariant()
        {
            if (_hasTurning == null)
            {
                _hasTurning = false;
                foreach (TickVariantDef v in Def.Variants) if (v.Turn) { _hasTurning = true; break; }
            }
            return _hasTurning.Value;
        }
    }

    /// <summary>InstantAction: applies at init when its own condition holds, and never has a next step.</summary>
    internal abstract class TickInstantAction : TickAction
    {
        protected TickInstantAction(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public sealed override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            if (BaseHasNext()) Apply();
        }

        protected abstract void Apply();

        public sealed override bool HasNext()
        {
            // super.hasNext() && false: evaluated for its side effects, then false.
            bool unused = BaseHasNext();
            return false;
        }

        protected sealed override void Tick() { }
    }

    /// <summary>ComplexAction, with Sequence and Select below: the children are built once, with the parent.</summary>
    internal abstract class TickComplexAction : TickAction
    {
        protected readonly TickAction[] Children;
        protected int Current;

        protected TickComplexAction(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming, TickAction[] children)
            : base(world, def, incoming)
        {
            Children = children;
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            if (BaseHasNext())
            {
                SetCurrent(0);
                Seek();
            }
        }

        protected void Seek()
        {
            if (!BaseHasNext()) return;
            // Bounded where the source is not: a looping Sequence whose every child ends at once would seek forever
            // (recorded divergence; such a pack ends the action instead of hanging the companion).
            int guard = 0;
            while (Current < Children.Length)
            {
                if (Children[Current].HasNext()) break;
                if (++guard > Children.Length * 2 + 2)
                {
                    World.ReportFault(Mascot, Name + ": a looping sequence whose children all end at once", null, null);
                    Current = Children.Length;
                    break;
                }
                SetCurrent(Current + 1);
            }
        }

        public override bool HasNext()
        {
            bool inrange = Current < Children.Length;
            return BaseHasNext() && inrange && Children[Current].HasNext();
        }

        protected override void Tick()
        {
            if (Children[Current].HasNext()) Children[Current].Next();
        }

        protected virtual void SetCurrent(int index)
        {
            Current = index;
            if (BaseHasNext() && Current < Children.Length) Children[Current].Init(Mascot);
        }

        public override TickAction Leaf
        {
            get { return Current < Children.Length ? Children[Current].Leaf : this; }
        }

        public override void AppendStack(List<string> stack)
        {
            stack.Add(RefKey);
            if (Current < Children.Length) Children[Current].AppendStack(stack);
        }

        public override bool IsDraggable()
        {
            return Current < Children.Length ? Children[Current].IsDraggable() : true;
        }
    }

    internal sealed class TickSequence : TickComplexAction
    {
        public TickSequence(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming, TickAction[] children)
            : base(world, def, incoming, children) { }

        public override bool HasNext()
        {
            Seek();
            return base.HasNext();
        }

        protected override void SetCurrent(int index)
        {
            base.SetCurrent(IsLoop() && Children.Length > 0 ? index % Children.Length : index);
        }

        private bool IsLoop()
        {
            if (Vars.Has("Loop")) return EvalBool("Loop", null, false);
            return Def.HasLoop && Def.Loop;
        }
    }

    internal sealed class TickSelect : TickComplexAction
    {
        public TickSelect(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming, TickAction[] children)
            : base(world, def, incoming, children) { }
    }

    /// <summary>Breed: an Animate that, on its penultimate tick, creates BornCount companions.</summary>
    internal sealed class TickBreed : TickAnimate
    {
        private readonly TickBreedDelegate _delegate;

        public TickBreed(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            _delegate = new TickBreedDelegate(world, this);
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            _delegate.ValidateBornCount();
        }

        protected override void Tick()
        {
            base.Tick();
            if (Time == AnimationDuration() - 1 && IsEnabled()) Breed();
        }

        private bool IsEnabled() { return _delegate.IsEnabled(Mascot); }

        private void Breed() { _delegate.Breed(Mascot); }

        public static int JavaRound(double d)
        {
            return TickType.JavaRound(d);
        }
    }

    /// <summary>
    /// Breed.Delegate (Breed.java:26-162): the birth parameters and the birth loop, shared by Breed and the classes that
    /// breed as they move (BreedMove, BreedJump, BreedFall). Each parameter is read through its action, at the source's
    /// own evaluation site.
    /// </summary>
    internal sealed class TickBreedDelegate
    {
        private readonly TickWorld _world;
        private readonly TickAction _action;

        public TickBreedDelegate(TickWorld world, TickAction action)
        {
            _world = world;
            _action = action;
        }

        /// <summary>isEnabled: BornTransient picks the toggle (Transients or Breeding), read for the parent's image set.</summary>
        public bool IsEnabled(TickMascot parent)
        {
            bool transient = _action.EvalBool("BornTransient", null, false);
            return _world.Settings.Get(transient ? "Transients" : "Breeding", parent.Type.Name);
        }

        /// <summary>isIntervalFrame: the action's age is a multiple of BornInterval, read afresh each time.</summary>
        public bool IsIntervalFrame()
        {
            int interval = _action.EvalInt("BornInterval", 1);
            // Java's int remainder by 0 throws ArithmeticException, which nothing in the source catches. Init refuses
            // an interval below 1, so only a script that later yields 0 gets here; the companion ends (recorded divergence).
            if (interval == 0) throw new TickCantBeAliveException(_action.Name + ": BornInterval became 0", TickFaultCause.OfParameter(_action, "BornInterval"));
            return _action.Time % interval == 0;
        }

        // Outside the range Breed accepts: parameter-invalid, named by the breeding action's refKey and the key it read.
        public void ValidateBornCount()
        {
            if (_action.EvalInt("BornCount", 1) < 1) throw new TickCantBeAliveException(_action.Name + ": BornCount must be positive", TickFaultCause.OfParameter(_action, "BornCount"));
        }

        public void ValidateBornInterval()
        {
            if (_action.EvalInt("BornInterval", 1) < 1) throw new TickCantBeAliveException(_action.Name + ": BornInterval must be positive", TickFaultCause.OfParameter(_action, "BornInterval"));
        }

        /// <summary>breed: BornCount children of BornMascot (else the parent's type), placed from the parent's anchor.</summary>
        public void Breed(TickMascot parent)
        {
            // The source scales by its one global Scaling; the host keeps a size per type, so the parent type is used.
            double scaling = parent.Type.Scale;
            string bornMascot = _action.EvalString("BornMascot", null, "");
            TickType childType = _world.ResolveReference(parent.Type, bornMascot);
            // The loop bound is evaluated every pass, as the source's `index < getBornCount()`.
            for (int index = 0; index < _action.EvalInt("BornCount", 1); index++)
            {
                int attempt = ++parent.BirthAttempts;
                // One room per attempt: the same captured operands decide the birth and reach the witness hook, which
                // this call site names by tick, parent and attempt (H :158, TickWorld.BirthDecided).
                TickBirthRoom room = _world.BirthRoom();
                Action<TickBirthDecision> decided = _world.BirthDecided;
                if (decided != null) decided(new TickBirthDecision(_world.Step, parent.Sid, attempt, room));
                if (!room.Admits)
                {
                    // policy:birth-cap: refused before anything is evaluated or allocated for it.
                    _world.Observer.BirthRefused(parent, attempt, "cap");
                    continue;
                }
                var child = new TickMascot(_world, parent.Sid + ".b" + (parent.Births + 1), childType);
                int bornX = _action.EvalInt("BornX", 0);
                int bornY = _action.EvalInt("BornY", 0);
                int dx = TickBreed.JavaRound(bornX * scaling), dy = TickBreed.JavaRound(bornY * scaling);
                child.Anchor = parent.LookRight
                    ? new TickPoint(parent.Anchor.X - dx, parent.Anchor.Y + dy)
                    : new TickPoint(parent.Anchor.X + dx, parent.Anchor.Y + dy);
                child.LookRight = parent.LookRight;
                string behaviour = _action.EvalString("BornBehavior", null, "");
                try
                {
                    _world.EnterBirthBehaviour(child, behaviour, parent);
                }
                catch (TickCantBeAliveException e)
                {
                    // The source shows the error first and then disposes the child it made (Breed.java:104-105,
                    // :110-111), so the fault is reported before the refusal, as the oracle's trace has them. It is the
                    // birth wrapper's (CONTRACT 10.3.4), with the cause the failed lookup formed: a blank or unknown
                    // BornBehavior is behavior-missing in the CHILD's member, which the lookup searched.
                    _world.ReportFault(parent, "a birth failed: " + e.Message, e.Cause, TickFaultVia.Birth);
                    _world.Observer.BirthRefused(parent, attempt, "error");
                    continue;
                }
                parent.Births++;
                _world.Observer.BirthRequested(parent, child, parent.Births, attempt);
                _world.Add(child);
            }
        }
    }

    /// <summary>Transform: an Animate that, on its penultimate tick (or its only one), becomes another type in place.</summary>
    internal sealed class TickTransform : TickAnimate
    {
        public TickTransform(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Tick()
        {
            base.Tick();
            int duration = AnimationDuration();
            if ((Time == duration - 1 || duration == 1) && World.Settings.Get("Transformation", Mascot.Type.Name))
                World.TransformInPlace(Mascot, EvalString("TransformMascot", null, ""), EvalString("TransformBehavior", null, ""));
        }
    }

    // ------------------------------------------------------------------ behaviours

    /// <summary>UserBehavior: one behaviour's root action, and the step order of grimoire/06 section 4.5.</summary>
    internal sealed class TickBehaviour
    {
        private readonly TickWorld _world;
        public readonly TickBehaviourDef Def;
        public readonly TickAction Action;

        /// <summary>The configuration this behaviour was built from (UserBehavior's own field). After a Transform the
        /// old behaviour finishes its step with the OLD type's configuration, as the source does.</summary>
        public readonly TickType Type;
        private TickMascot _mascot;

        public TickBehaviour(TickWorld world, TickType type, TickBehaviourDef def, TickAction action)
        {
            _world = world;
            Type = type;
            Def = def;
            Action = action;
        }

        public string Name { get { return Def.Name; } }

        /// <summary>The refKeys from the behaviour's element down to the running leaf action (CONTRACT section 3).</summary>
        public List<string> ActionStack()
        {
            var stack = new List<string> { Def.RefKey };
            Action.AppendStack(stack);
            return stack;
        }

        /// <summary>How many behaviours whose action could not even start may hand on, one inside the other, before the
        /// companion is ended (<see cref="Init"/>).</summary>
        public const int MaximumHandOffs = 256;

        /// <summary>UserBehavior.init: init the action; one that cannot even start hands straight to the next.</summary>
        public void Init(TickMascot mascot)
        {
            _mascot = mascot;
            Action.Init(mascot);
            if (Action.HasNext()) return;
            // Bounded where the source is not (a recorded divergence, as Seek bounds a looping Sequence): each hand-off is
            // a call inside the last one, so a companion whose every candidate cannot start recursed until the UI thread's
            // stack overflowed, which no catch can stop and which ended the whole app with every companion in it. The
            // source dies the same way, of a StackOverflowError on its own thread. Past the bound this companion cannot be
            // alive: TickCantBeAliveException ends it, reported, with 'error' (TickMascot.Tick, CreateAtStart), and the
            // others step on. The count is the hand-offs nested NOW (taken and given back in a finally), so a hand-off
            // that threw cannot leave a count behind for the next selection; one that started its action returned and
            // gave its count back. 256 keeps the stack small (a hand-off nests two frames) and a draw that hands on 256
            // times in a row out of reach of any pack in which something can start: at nine in ten candidates that
            // cannot, 0.9^256 is about 2e-12. The rejected alternative, a small bound such as 16, would end a companion
            // of such a pack at about one selection in five (0.9^16 = 0.19).
            if (mascot.HandOffDepth >= MaximumHandOffs)
                throw new TickCantBeAliveException("no behaviour could start: " + MaximumHandOffs
                    + " behaviours in a row ended as soon as they were entered (the last was '" + Name + "')");
            mascot.HandOffDepth++;
            try
            {
                mascot.SetBehaviour(_world.BuildNextBehaviour(Type, mascot, Name), _world.LastSelectionCause);
            }
            finally
            {
                mascot.HandOffDepth--;
            }
        }

        /// <summary>UserBehavior.next.</summary>
        public void Next()
        {
            try
            {
                if (Action.HasNext()) Action.Next();

                // While a hotspot is held (UserBehavior.next 211-247): the first hotspot containing the held point keeps
                // the hold, and one with a behaviour enters it again, every step; none containing it ends the hold.
                bool hotspotEntered = false;
                if (_mascot.IsHotspotClicked)
                {
                    bool active = false;
                    foreach (TickHotspotDef h in _mascot.Hotspots)
                    {
                        if (!h.Contains(_mascot.CursorPosition.Value, _mascot.LookRight, _mascot.Bounds.Width)) continue;
                        active = true;
                        if (h.Behaviour != null)
                        {
                            hotspotEntered = true;
                            _world.EnterBehaviourByName(_mascot, Type, h.Behaviour, "forced");
                        }
                        break;
                    }
                    if (!active) _mascot.CursorPosition = null;
                }

                if (!hotspotEntered)
                {
                    if (Action.HasNext())
                    {
                        if (IsOffScreen())
                        {
                            _world.DropIn(_mascot, "builtin:out-of-bounds-drop-x");
                            _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, "Fall"), "forced");
                        }
                    }
                    else if (_mascot.ArrivalBehaviour != null && Name != _mascot.ArrivalBehaviour
                        && (!Type.RootByName.ContainsKey(_mascot.ArrivalBehaviour)
                            || _world.IsBehaviourEnabledByName(Type, _mascot.ArrivalBehaviour, _mascot)))
                    {
                        // The arrival has landed: the pack's startup behaviour now (ArrivalBehaviour). It stays pending until
                        // it ends of itself, so one cut short (dragged off, or its floor gone) is entered again when the
                        // companion next lands: an egg's hatch, which has no route frequency, would otherwise never come
                        // and the egg would drop in again and again with nothing to choose. An unknown name cannot live.
                        _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, _mascot.ArrivalBehaviour), "forced");
                    }
                    else
                    {
                        // The route chooses: there is no startup behaviour to come, or it has just ended of itself and is
                        // done, or the user turned it off. A turned-off one is skipped rather than built with the enabled
                        // check: the source's drop-in for a turned-off behaviour is for one it was asked to enter, and here
                        // it would throw a companion that has just landed back above the screen.
                        _mascot.ArrivalBehaviour = null;
                        _mascot.SetBehaviour(_world.BuildNextBehaviour(Type, _mascot, Name), _world.LastSelectionCause);
                    }
                }
            }
            catch (TickLostGroundException)
            {
                _mascot.Dragging = false;
                _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, "Fall"), "forced");
            }
        }

        /// <summary>UserBehavior.mousePressed (left button): a containing hotspot whose behaviour is enabled takes the
        /// press and is held; else an action that refuses dragging takes it; else the companion is dragged.</summary>
        public void MousePressed(TickPoint local)
        {
            bool handled = false;
            foreach (TickHotspotDef h in _mascot.Hotspots)
            {
                if (!h.Contains(local, _mascot.LookRight, _mascot.Bounds.Width) || !_world.IsBehaviourEnabledByName(_mascot.Type, h.Behaviour, _mascot))
                    continue;
                handled = true;
                _mascot.CursorPosition = local;
                _world.Observer.Hotspot(_mascot, "press", h.Behaviour);
                // h.Behaviour is never null here: IsBehaviourEnabledByName refuses a null name, so such a hotspot
                // never takes the press. Built through the holding type with the current type's switches, so in the
                // retained state of a failed Transform it can still be turned off here and fall (EnterBehaviourByName).
                _world.EnterBehaviourByName(_mascot, Type, h.Behaviour, "forced");
                break;
            }
            if (!handled) handled = !Action.IsDraggable();
            if (!handled)
            {
                // CONTRACT v1.1 section 3: drag/start once Dragged is built, then the behaviour change. A pack with no
                // Dragged throws here (TickWorld.Press reports it and disposes the companion), and no drag began, so
                // none is written; the drag event once came first and recorded a drag that never started.
                TickBehaviour dragged = _world.BuildBehaviourByName(Type, "Dragged");
                _world.Observer.Dragged(_mascot, "start");
                _mascot.SetBehaviour(dragged, "forced");
            }
        }

        /// <summary>UserBehavior.mouseReleased (left button): a held hotspot lets go; a companion being dragged is
        /// thrown. A press and release in one step, before any action set the dragging flag, throws nothing.</summary>
        public void MouseReleased()
        {
            if (_mascot.IsHotspotClicked)
            {
                _mascot.CursorPosition = null;
                _world.Observer.Hotspot(_mascot, "release", null);
            }
            if (_mascot.Dragging)
            {
                _mascot.Dragging = false;
                _world.Observer.Dragged(_mascot, "end");
                _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, "Thrown"), "forced");
            }
        }

        private bool IsOffScreen()
        {
            TickRect b = _mascot.Bounds;
            TickArea screen = _world.Environment.Screen;
            return b.X + b.Width <= screen.Left || screen.Right <= b.X || screen.Bottom <= b.Y;
        }
    }

    internal struct TickRect
    {
        public int X, Y, Width, Height;

        public TickRect(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }

    // ------------------------------------------------------------------ companions and the world

    /// <summary>One live companion (the source's Mascot): its type, anchor, facing, time, behaviour and offers.</summary>
    internal sealed class TickMascot
    {
        private readonly TickWorld _world;
        public readonly string Sid;
        public TickType Type;
        public TickPoint Anchor;
        public bool LookRight;
        public int Time;
        public bool Animating = true;
        public bool Dragging;
        public bool Disposing;
        public bool InManager;
        public TickBehaviour Behaviour;
        public readonly List<string> Affordances = new List<string>();
        public readonly List<TickHotspotDef> Hotspots = new List<TickHotspotDef>();

        /// <summary>Where a held hotspot press is, in window-local pixels (Mascot.cursorPosition); null when no hotspot
        /// is held.</summary>
        public TickPoint? CursorPosition;
        public bool IsHotspotClicked { get { return CursorPosition.HasValue; } }
        public readonly Dictionary<string, ScriptValue> Variables = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        public readonly TickMascotEnvironment Environment;
        public int Births;
        public int BirthAttempts;

        /// <summary>The pose last applied, the facing it was applied with, and the type whose sheet it came from
        /// (Mascot.setImage). After a Transform the companion still shows the old type's image until the new type's
        /// first pose, so the image is sized and named by <see cref="PoseType"/>, not <see cref="Type"/>. A parity trace
        /// names the pose's source position through PoseType's map, by the pose object (ParityMap.TryGetPoseIndex).</summary>
        public TickPoseDef Pose;
        public bool PoseRight;
        public TickType PoseType;

        /// <summary>A start companion: queued by Manager.add like a birth, but activated with no birth event.</summary>
        public bool IsStartup;

        /// <summary>Behaviours that could not start handing on, nested now (<see cref="TickBehaviour.Init"/> bounds it).</summary>
        public int HandOffDepth;

        /// <summary>The clip the current pose asks for (Mascot.setSound), played by the window layer's apply.</summary>
        public string Sound;

        /// <summary>BringToFront ran since the window layer last raised this companion's window.</summary>
        public bool BringToFrontRequested;

        /// <summary>The companion the running scan action chose (its WeakReference target), or null; written to the
        /// state line's <c>target</c>. Cleared whenever an action initialises.</summary>
        public TickMascot Target;
        public bool Visible;

        /// <summary>
        /// A pack's startup behaviour still to come (grimoire/06 section 2, `policy:startup-after-arrival`): a companion
        /// created the source's way arrives off screen and its first step drops it in through Fall, which replaces the
        /// behaviour it was created in. The first behaviour that then ends of itself (the drop-in's Fall, or Thrown when it
        /// was caught on the way down) hands over to this one instead of the route, and again after any interruption, until
        /// this one ends of itself; a Transform, or the user turning it off, cancels it. Without it an egg whose hatch
        /// behaviour has no frequency of its own (as a hatch should not) landed and never hatched.
        /// </summary>
        public string ArrivalBehaviour;
        private TickRect _lastWindowBounds;

        public TickMascot(TickWorld world, string sid, TickType type)
        {
            _world = world;
            Sid = sid;
            Type = type;
            Environment = new TickMascotEnvironment(world.Environment, this, delegate { return world.Settings.Get("Multiscreen", null); });
        }

        public bool HasImage { get { return Pose != null && (Pose.Frame >= 0 || (PoseRight && Pose.Right >= 0)); } }

        /// <summary>The image's offset from the anchor (MascotImage.getCenter): the last pose's own anchor, for the facing
        /// it was applied with. Before any pose the source has no image; the engine uses the type's anchor then.</summary>
        public TickPoint ImageCenter
        {
            get
            {
                if (Pose == null) return new TickPoint(Type.AnchorX, Type.AnchorY);
                return Pose.ImageCenter(PoseRight);
            }
        }

        /// <summary>Mascot.getBounds: from the image (the last pose's own size and anchor, Mascot.java:714-722), or the
        /// window's last bounds when there is no image.</summary>
        public TickRect Bounds
        {
            get
            {
                if (!HasImage) return _lastWindowBounds;
                TickPoint c = ImageCenter;
                return new TickRect(Anchor.X - c.X, Anchor.Y - c.Y, Pose.ImageWidth(PoseRight), Pose.ImageHeight(PoseRight));
            }
        }

        /// <summary>Pose.next: move by the pose velocity (mirrored when facing right), then take its image, from the sheet
        /// of the type whose action holds the pose (<paramref name="owner"/>; the companion's own type when not given).</summary>
        public void ApplyPose(TickPoseDef pose, TickType owner = null)
        {
            if (pose == null) return;
            Anchor = new TickPoint(Anchor.X + (LookRight ? -pose.Dx : pose.Dx), Anchor.Y + pose.Dy);
            Pose = pose;
            PoseRight = LookRight;
            PoseType = owner ?? Type;
            Sound = pose.Sound;
        }

        /// <summary>Mascot.setBehavior: set, then init (which may itself hand on to another behaviour).</summary>
        public void SetBehaviour(TickBehaviour behaviour, string cause)
        {
            string from = Behaviour == null ? null : Behaviour.Name;
            Behaviour = behaviour;
            _world.Observer.BehaviourEntered(this, from, behaviour == null ? null : behaviour.Name, cause);
            if (behaviour == null) throw new TickCantBeAliveException("no behaviour could be selected");
            behaviour.Init(this);
        }

        /// <summary>Mascot.tick.</summary>
        public void Tick()
        {
            if (!Animating || Behaviour == null) return;
            try
            {
                Behaviour.Next();
            }
            catch (TickCantBeAliveException e)
            {
                // The step itself caught it: no wrapper (CONTRACT 10.3.4, via []).
                _world.ReportFault(this, e.Message, e.Cause, null);
                Dispose("error");
            }
            catch (Exception e)
            {
                // Anything else is a fault the source would have died of too (a null Affordance at ActionBase.java:95,
                // a cast): this companion ends, reported, and the step goes on for the rest. Escaping here would leave
                // the step half done and, in the app, throw out of the tick timer. Not the host's own fault class, so it
                // carries no typed cause: unclassified, never typed from its message (CH9).
                _world.ReportFault(this, "internal fault: " + e.GetType().Name + ": " + e.Message, null, null);
                Dispose("error");
            }
            Time++;
        }

        /// <summary>A screen point as the window-local point a MouseEvent on this companion's window carries: relative
        /// to where the window was last placed.</summary>
        public TickPoint ToWindowLocal(TickPoint screen)
        {
            return new TickPoint(screen.X - _lastWindowBounds.X, screen.Y - _lastWindowBounds.Y);
        }

        /// <summary>Mascot.apply: show the image, or hide the window for an image-less pose.</summary>
        public void Apply()
        {
            if (!Animating) return;
            if (HasImage)
            {
                _lastWindowBounds = Bounds;
                Visible = true;
            }
            else Visible = false;
            TickSound.Apply(_world, this);
        }

        /// <summary>Mascot.dispose: stops at once, hidden, offers cleared, removed at the next manager step.</summary>
        public void Dispose(string reason)
        {
            if (Disposing) return;
            Disposing = true;
            Animating = false;
            Visible = false;
            Affordances.Clear();
            _world.Observer.DisposeRequested(this, reason);
            _world.Remove(this);
        }
    }

    /// <summary>
    /// Who wrote the birth cap (policy:birth-cap, signed revision H :158): the writer's name, the context it wrote in, and
    /// the budget's base and classic deduction when the writer exposes them separately. An opaque writer, the product's
    /// Func&lt;int&gt; CompanionBudget or a plain assignment, marks both unavailable instead of inferring them. Immutable.
    /// </summary>
    internal sealed class TickLimitWriter
    {
        /// <summary>A plain MaximumCompanions assignment. H :158 asks for a direct, opaque writer here: the caller is
        /// recorded as unknown, never inferred, and the write is never omitted.</summary>
        public static readonly TickLimitWriter Direct = new TickLimitWriter("direct", "a plain MaximumCompanions assignment", false, 0, 0);

        /// <summary>TickHost.Step's writer. CompanionBudget is a Func&lt;int&gt; that hands over only its result, the app's
        /// shared cap less its classic companions, so base and deduction are unavailable (H :158).</summary>
        public static readonly TickLimitWriter CompanionBudget = Opaque("TickHost.CompanionBudget", "the app's shared cap less its classic companions, asked before every step");

        public readonly string Name;
        public readonly string Context;
        /// <summary>True when the writer exposed <see cref="Base"/> and <see cref="Deduction"/>; false marks both unavailable.</summary>
        public readonly bool PartsKnown;
        public readonly int Base;
        public readonly int Deduction;

        private TickLimitWriter(string name, string context, bool partsKnown, int baseLimit, int deduction)
        {
            Name = name ?? "";
            Context = context ?? "";
            PartsKnown = partsKnown;
            Base = partsKnown ? baseLimit : 0;
            Deduction = partsKnown ? deduction : 0;
        }

        /// <summary>A writer that hands over only the limit it computed: base and deduction unavailable.</summary>
        public static TickLimitWriter Opaque(string name, string context) { return new TickLimitWriter(name, context, false, 0, 0); }

        /// <summary>A writer that exposes the budget's base and classic deduction separately; both are recorded.</summary>
        public static TickLimitWriter WithParts(string name, string context, int baseLimit, int deduction) { return new TickLimitWriter(name, context, true, baseLimit, deduction); }

        /// <summary>Every recorded field equal: only then may two writes share a run (<see cref="TickLimitWrites"/>).</summary>
        public bool Matches(TickLimitWriter other)
        {
            return other != null && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(Context, other.Context, StringComparison.Ordinal)
                && PartsKnown == other.PartsKnown && Base == other.Base && Deduction == other.Deduction;
        }

        public override string ToString()
        {
            return Name + " (" + Context + "; " + (PartsKnown ? "base " + Base + " deduction " + Deduction : "base and deduction unavailable") + ")";
        }
    }

    /// <summary>One run of the cap's write record: <see cref="Count"/> consecutive writes of <see cref="Value"/> by one
    /// writer, the first of them the world's <see cref="FirstWrite"/>-th write, counted from 0.</summary>
    internal sealed class TickLimitWrite
    {
        public readonly int Value;
        public readonly TickLimitWriter Writer;
        public readonly long FirstWrite;

        /// <summary>A long: TickHost.Step writes the cap on every product step, so a steady budget's run grows for as
        /// long as the app runs.</summary>
        public long Count { get; internal set; }

        public TickLimitWrite(int value, TickLimitWriter writer, long firstWrite)
        {
            Value = value;
            Writer = writer;
            FirstWrite = firstWrite;
            Count = 1;
        }
    }

    /// <summary>
    /// The birth cap's write record (policy:birth-cap, signed revision H :158, review reading K3 as Codex accepted it):
    /// every write after the world's construction, in order, with its value and writer. It is lossless run-length:
    /// consecutive writes of one value by writers equal in every field share a run with a count, so TickHost.Step's write
    /// on every step costs one counter instead of one entry, while the full logical count, the order, each value and
    /// each writer and context stay exact. Rejected: one entry per write, which grows without bound in the shipping app;
    /// and keeping only the last write, which H :158 forbids ("not ... shortened to its last write"). Nothing clears,
    /// reorders or shortens it, so zero writes and any write stay distinguishable for the run's whole life.
    /// </summary>
    internal sealed class TickLimitWrites
    {
        private readonly List<TickLimitWrite> _runs = new List<TickLimitWrite>();

        /// <summary>Every write recorded, a run of n counting n; 0 only while nothing has written the cap.</summary>
        public long Count { get; private set; }

        /// <summary>The runs, in write order.</summary>
        public IReadOnlyList<TickLimitWrite> Runs { get { return _runs; } }

        internal void Record(int value, TickLimitWriter writer)
        {
            TickLimitWrite last = _runs.Count == 0 ? null : _runs[_runs.Count - 1];
            if (last != null && last.Value == value && last.Writer.Matches(writer)) last.Count++;
            else _runs.Add(new TickLimitWrite(value, writer, Count));
            Count++;
        }
    }

    /// <summary>One birth decision's operands and result, captured once at the comparison (<see cref="TickWorld.BirthRoom"/>):
    /// the limit, the private active count and the private pending count, and whether the birth fits.</summary>
    internal struct TickBirthRoom
    {
        public readonly int Limit;
        public readonly int Active;
        public readonly int Pending;
        /// <summary>The comparison's own result, stored as made, never recomputed from the fields.</summary>
        public readonly bool Admits;

        public TickBirthRoom(int limit, int active, int pending, bool admits)
        {
            Limit = limit;
            Active = active;
            Pending = pending;
            Admits = admits;
        }
    }

    /// <summary>A birth decision as Breed's loop names it (H :158): the tick, the parent's sid and the attempt number,
    /// with the room the comparison used.</summary>
    internal sealed class TickBirthDecision
    {
        public readonly int Tick;
        public readonly string ParentSid;
        public readonly int Attempt;
        public readonly TickBirthRoom Room;

        public TickBirthDecision(int tick, string parentSid, int attempt, TickBirthRoom room)
        {
            Tick = tick;
            ParentSid = parentSid;
            Attempt = attempt;
            Room = room;
        }
    }

    /// <summary>The active and pending sids as the world's private lists held them at one moment, copied.</summary>
    internal sealed class TickMembership
    {
        public readonly string[] Active;
        public readonly string[] Pending;

        public TickMembership(string[] active, string[] pending)
        {
            Active = active;
            Pending = pending;
        }
    }

    /// <summary>
    /// The manager (Manager.java) and the configuration's selection (Configuration.java): stable manager order, the add
    /// and remove queues, and the step order of CONTRACT section 4.
    /// </summary>
    internal sealed partial class TickWorld
    {
        private readonly List<TickMascot> _mascots = new List<TickMascot>();
        private readonly List<TickMascot> _added = new List<TickMascot>();
        private readonly List<TickMascot> _removed = new List<TickMascot>();
        private readonly Dictionary<string, TickType> _types = new Dictionary<string, TickType>(StringComparer.Ordinal);

        public readonly ITickEnvironment Environment;
        public readonly ITickRandom Random;
        public readonly ITickSettings Settings;
        public readonly ITickObserver Observer;

        // policy:birth-cap's compared limit, write-recorded (signed revision H :158). The initializer is the
        // construction default 16, the one write the record exempts; every write after construction goes through
        // SetMaximumCompanions and is recorded. Rejected: the public field this replaces, which any code could assign
        // without a trace, so a run's configuration could not show that nothing overrode the default.
        private int _maximumCompanions = 16;
        private readonly TickLimitWrites _limitWrites = new TickLimitWrites();

        /// <summary>How many companions may exist at once, active and pending together (policy:birth-cap). A plain
        /// assignment is recorded with <see cref="TickLimitWriter.Direct"/>.</summary>
        public int MaximumCompanions
        {
            get { return _maximumCompanions; }
            set { SetMaximumCompanions(value, TickLimitWriter.Direct); }
        }

        /// <summary>Writes the cap and records the write with <paramref name="writer"/>; null records the direct writer,
        /// since H :158 asks for an opaque writer rather than an inferred caller or an omitted write.</summary>
        public void SetMaximumCompanions(int value, TickLimitWriter writer)
        {
            // Recorded before it is applied, and recorded when the value does not change: an equal-value 16 write is a
            // write (H :158). Skipping it would let an override that happens to equal the default pass as none.
            _limitWrites.Record(value, writer ?? TickLimitWriter.Direct);
            _maximumCompanions = value;
        }

        public int Step { get; private set; }

        /// <summary>How the last <see cref="BuildNextBehaviour"/> chose: select, next or fallback.</summary>
        public string LastSelectionCause { get; private set; }

        public TickWorld(ITickEnvironment environment, ITickRandom random, ITickSettings settings, ITickObserver observer)
        {
            Environment = environment;
            Random = random;
            Settings = settings;
            Observer = observer ?? NullObserver.Instance;
        }

        public IReadOnlyList<TickMascot> Mascots { get { return _mascots; } }
        public IReadOnlyList<TickMascot> Pending { get { return _added; } }

        /// <summary>The cap's write record since construction (<see cref="TickLimitWrites"/>).</summary>
        public TickLimitWrites LimitWrites { get { return _limitWrites; } }

        /// <summary>
        /// The active and pending sids, copied from the private lists: a proof's view of membership with no public
        /// getter, filter or observer between it and the lists the manager steps. The trace writer never reads it.
        /// </summary>
        internal TickMembership SnapshotMembership()
        {
            var active = new string[_mascots.Count];
            for (int i = 0; i < active.Length; i++) active[i] = _mascots[i].Sid;
            var pending = new string[_added.Count];
            for (int i = 0; i < pending.Length; i++) pending[i] = _added[i].Sid;
            return new TickMembership(active, pending);
        }

        /// <summary>Every type added to the world (the configurations the source has loaded).</summary>
        public IEnumerable<TickType> Types { get { return _types.Values; } }

        /// <summary>Where clips play (<see cref="TickSound"/>); null plays nothing.</summary>
        public ITickClips Clips { get; set; }
        public int ActiveCount { get { return _mascots.Count; } }

        public void AddType(TickType type)
        {
            _types[type.Name] = type;
            IndexReference(type);
        }

        public TickType FindType(string name)
        {
            TickType t;
            return !string.IsNullOrEmpty(name) && _types.TryGetValue(name, out t) ? t : null;
        }

        public int CountOf(TickType type)
        {
            int n = 0;
            foreach (TickMascot m in _mascots) if (m.Type == type) n++;
            return n;
        }

        /// <summary>
        /// The host's cap counts active and pending companions alike (policy:birth-cap). The limit and both private
        /// counts are read once, here, and the comparison is made from those reads, so the room a witness is handed
        /// holds the operands the decision used (H :158), never a later recount through the public getters.
        /// </summary>
        public TickBirthRoom BirthRoom()
        {
            int limit = _maximumCompanions;
            int active = _mascots.Count;
            int pending = _added.Count;
            return new TickBirthRoom(limit, active, pending, active + pending < limit);
        }

        /// <summary>Startup (CONTRACT section 4): the tick-0 environment first, then companions in order.</summary>
        public void Start() { Environment.Tick(0); }

        /// <summary>A start companion, as Main.createMascot makes one: the state is applied before its behaviour is
        /// initialised (so no initial random placement or facing is drawn), the behaviour is initialised before the
        /// companion has a manager (so its counts read 0), and then it is queued: pending at tick 0, active from step 1,
        /// with no birth event.</summary>
        public TickMascot CreateAtStart(string sid, TickType type, TickPoint anchor, bool lookRight, string behaviour)
        {
            var m = new TickMascot(this, sid, type) { Anchor = anchor, LookRight = lookRight, IsStartup = true };
            try
            {
                if (string.IsNullOrEmpty(behaviour)) m.SetBehaviour(BuildNextBehaviour(type, m, null), LastSelectionCause);
                else EnterBehaviourByName(m, type, behaviour, "forced");
            }
            catch (Exception e)
            {
                // Main.createMascot catches a first behaviour that cannot be built (a missing name, a class that refuses
                // its definition), shows the error and disposes the mascot, which never joins the manager. Same here:
                // reported, disposed, not added; the caller sees Disposing. The startup wrapper's (CONTRACT 10.3.4): the
                // host's own fault class carries the cause it was thrown with, anything else is unclassified.
                var own = e as TickCantBeAliveException;
                ReportFault(m, (own != null ? "" : "internal fault: " + e.GetType().Name + ": ")
                    + "the first behaviour could not be built: " + e.Message, own == null ? null : own.Cause, TickFaultVia.Startup);
                m.Dispose("error");
                return m;
            }
            Add(m);
            return m;
        }

        /// <summary>CONTRACT section 4, startup step 3: the parity driver adds the start companions to the manager
        /// WITHOUT a step (no environment, time, action or render advances, and no birth event), so they are active at
        /// tick 0. Both runtimes normalise the source's queued createMascot this way; the app does not call it.</summary>
        public void ActivateStartup()
        {
            foreach (TickMascot m in _added) _mascots.Add(m);
            _added.Clear();
        }

        /// <summary>Manager.add: queued; active from the next step.</summary>
        public void Add(TickMascot mascot)
        {
            _removed.Remove(mascot);
            if (!_added.Contains(mascot)) _added.Add(mascot);
            mascot.InManager = true;
        }

        /// <summary>Manager.remove: dequeued if pending, else removed at the next step.</summary>
        public void Remove(TickMascot mascot)
        {
            _added.Remove(mascot);
            if (!_removed.Contains(mascot)) _removed.Add(mascot);
            mascot.InManager = false;
        }

        /// <summary>Manager.tick.</summary>
        public void StepOnce()
        {
            Step++;
            Environment.Tick(Step);
            DispatchInputs();
            foreach (TickMascot m in _added)
            {
                _mascots.Add(m);
                if (!m.IsStartup) Observer.BirthActivated(m);
            }
            _added.Clear();
            foreach (TickMascot m in _removed)
            {
                if (_mascots.Remove(m)) Observer.Disposed(m);
            }
            _removed.Clear();
            for (int i = 0; i < _mascots.Count; i++) _mascots[i].Tick();
            for (int i = 0; i < _mascots.Count; i++) _mascots[i].Apply();
        }

        // ---- input (Mascot.mousePressed / mouseReleased / mouseDragged, delivered to one companion's window)

        /// <summary>
        /// Called with a press or release whose companion is not active at dispatch (pending, removed or unknown), before
        /// it is dropped. Null in the product, which drops it silently: TickHost queues real window events, and a window
        /// can close between the queue and the dispatch. A parity run sets it to refuse the scenario (CONTRACT v1.1
        /// section 1: such a left-button edge is scenario-invalid, as the oracle classifies it). A pointer move to an
        /// absent companion is never passed here; right-button entries never reach the engine.
        /// </summary>
        public Action<TickInput> InactiveButtonTarget { get; set; }

        /// <summary>
        /// Called with each birth decision Breed's loop makes, admitted or refused, before the birth or refusal it
        /// decides (policy:birth-cap, H :158). Null in the product. A parity run collects these for its birth witness.
        /// It is a hook, not an observer event, so ITickObserver, every observer and the trace stay as they were.
        /// </summary>
        public Action<TickBirthDecision> BirthDecided { get; set; }

        private void DispatchInputs()
        {
            foreach (TickInput input in Environment.Inputs)
            {
                TickMascot m = null;
                foreach (TickMascot active in _mascots)
                    if (active.Sid == input.Sid) { m = active; break; }
                if (m == null && input.Kind != TickInputKind.Move && InactiveButtonTarget != null) InactiveButtonTarget(input);
                if (m == null) continue;   // a pending or removed companion has no window to receive it
                TickPoint local = m.ToWindowLocal(input.Screen);
                switch (input.Kind)
                {
                    case TickInputKind.Press: Press(m, local); break;
                    case TickInputKind.Release: Release(m); break;
                    case TickInputKind.Move: MovePointer(m, local); break;
                }
            }
        }

        public void Press(TickMascot mascot, TickPoint local)
        {
            if (!mascot.Animating || mascot.Behaviour == null) return;
            // The press wrapper (CONTRACT 10.3.4). Any other exception escapes the step as it always has: the run ends in
            // error and no error event is written (agreed with the oracle side, Part E of the signed text).
            try { mascot.Behaviour.MousePressed(local); }
            catch (TickCantBeAliveException e) { ReportFault(mascot, e.Message, e.Cause, TickFaultVia.Press); mascot.Dispose("error"); }
        }

        public void Release(TickMascot mascot)
        {
            if (!mascot.Animating || mascot.Behaviour == null) return;
            try { mascot.Behaviour.MouseReleased(); }
            catch (TickCantBeAliveException e) { ReportFault(mascot, e.Message, e.Cause, TickFaultVia.Release); mascot.Dispose("error"); }
        }

        /// <summary>Mascot's mouse-motion listener: while a hotspot is held, the held point follows the pointer.</summary>
        public void MovePointer(TickMascot mascot, TickPoint local)
        {
            if (mascot.IsHotspotClicked) mascot.CursorPosition = local;
        }

        /// <summary>Configuration.isBehaviorEnabled(name, mascot): no name, or no such behaviour, is not enabled.</summary>
        public bool IsBehaviourEnabledByName(TickType type, string name, TickMascot mascot)
        {
            TickBehaviourDef def;
            if (name == null || !type.RootByName.TryGetValue(name, out def)) return false;
            return Settings.IsBehaviourEnabled(def, mascot.Type.Name);
        }

        public double Draw(TickMascot mascot, string site, string purpose)
        {
            double value = Random.Next();
            Observer.RandomDrawn(mascot, site, value, purpose);
            return value;
        }

        /// <summary>Every fault goes through here, with its cause and wrapper stated by the caller: the site that formed
        /// the cause, or the wrapper that caught the exception carrying it. Null cause: unclassified; null via: none.</summary>
        public void ReportFault(TickMascot mascot, string message, TickFaultCause cause, string via) { Observer.Fault(mascot, message, cause, via); }

        /// <summary>The source's drop-in: from 256 px above the screen (multiscreen) or work area, at a random x.</summary>
        public void DropIn(TickMascot mascot, string site)
        {
            TickArea area = Settings.Get("Multiscreen", mascot.Type.Name) ? Environment.Screen : mascot.Environment.GetWorkArea();
            double r = Draw(mascot, site, "drop-in");
            mascot.Anchor = new TickPoint(TickScriptContext.JavaInt(r * (area.Right - area.Left)) + area.Left, area.Top - 256);
        }

        // ---- behaviour building (Configuration.java)

        public TickBehaviour Build(TickType type, TickBehaviourDef def)
        {
            return new TickBehaviour(this, type, def, BuildAction(type, def.Runs, def.Binds));
        }

        /// <summary>Configuration.buildBehavior(name): by name, no enabled check; a missing name cannot be built.</summary>
        // Each failed lookup by name types itself here, where it arises: behavior-missing in the member searched, with the
        // name as it was asked for (name, not name ?? "", so "", " " and null stay three operands, CONTRACT 10.3.3).
        public TickBehaviour BuildBehaviourByName(TickType type, string name)
        {
            TickBehaviourDef def;
            if (!type.RootByName.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no behaviour named '" + name + "' in " + type.Name, TickFaultCause.OfMissingBehaviour(type, name));
            return Build(type, def);
        }

        /// <summary>Configuration.buildBehavior(name, mascot): a behaviour turned off for this companion drops it in
        /// and enters Fall instead, and <paramref name="droppedIn"/> says so. Callers enter it through
        /// <see cref="EnterBehaviourByName"/>, which labels that entry.</summary>
        private TickBehaviour BuildBehaviourByName(TickType type, string name, TickMascot mascot, out bool droppedIn)
        {
            TickBehaviourDef def;
            droppedIn = false;
            if (!type.RootByName.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no behaviour named '" + name + "' in " + type.Name, TickFaultCause.OfMissingBehaviour(type, name));
            if (Settings.IsBehaviourEnabled(def, mascot.Type.Name)) return Build(type, def);
            DropIn(mascot, "builtin:disabled-behavior-drop-x");
            droppedIn = true;
            return BuildBehaviourByName(type, "Fall");
        }

        /// <summary>
        /// Builds <paramref name="name"/> in <paramref name="type"/> with the enabled check (Configuration.buildBehavior(name,
        /// mascot)) and enters it with <paramref name="cause"/>; when this companion has it turned off, the Fall it was
        /// dropped in for is entered as `fallback` instead, whatever the caller asked with (N-bd-anchors-02, anchor packet
        /// note D2): CONTRACT section 3 gives `fallback` to the disabled-by-name drop-in, and the oracle marks it on the
        /// drop-in draw ahead of `partner` (V1ScenarioRunner). Every caller of the enabled-check build comes through here:
        /// the hotspot press and its held re-entry, an explicit startup, the Transform, and the social transitions. Each
        /// passed its own "forced" (or "partner") beside the build and so labelled the Fall with it; the build is private
        /// now, so a caller cannot take the behaviour without the label. A birth does not come here
        /// (<see cref="EnterBirthBehaviour"/>, policy:breed-parent-reposition: no drop-in, so its Fall stays forced).
        /// </summary>
        public void EnterBehaviourByName(TickMascot mascot, TickType type, string name, string cause)
        {
            bool droppedIn;
            TickBehaviour behaviour = BuildBehaviourByName(type, name, mascot, out droppedIn);
            mascot.SetBehaviour(behaviour, droppedIn ? "fallback" : cause);
        }

        /// <summary>A born companion's entry behaviour. The source builds it for the PARENT, so a turned-off one
        /// drops the parent in; the host keeps the parent where it is and the child falls from its birth point
        /// (policy:breed-parent-reposition).</summary>
        public void EnterBirthBehaviour(TickMascot child, string name, TickMascot parent)
        {
            TickBehaviourDef def;
            if (!child.Type.RootByName.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no behaviour named '" + name + "' in " + child.Type.Name, TickFaultCause.OfMissingBehaviour(child.Type, name));
            // The enabled check reads the PARENT's image-set settings, because the source asks with the parent.
            TickBehaviour b = Settings.IsBehaviourEnabled(def, parent.Type.Name) ? Build(child.Type, def) : BuildBehaviourByName(child.Type, "Fall");
            child.SetBehaviour(b, "forced");
        }

        /// <summary>
        /// Configuration.buildNextBehavior (grimoire/06 4.3): every root edge evaluated in order even when it is about
        /// to be discarded, the ending behaviour's list cleared or appended, ONE draw, and a drop-in when nothing
        /// weighs anything.
        /// </summary>
        public TickBehaviour BuildNextBehaviour(TickType type, TickMascot mascot, string previousName)
        {
            var context = new TickScriptContext(this, mascot, null, type.Constants);
            var candidates = new List<TickEdgeDef>();
            var fromNext = new List<bool>();
            long total = 0;
            // Each candidate's enabled check: its toggle is the definition's in <paramref name="type"/>, the configuration
            // that holds the ending behaviour, but the switches are the companion's CURRENT type's, because the source asks
            // with the companion and keys the disabled list by its image set (N-bd-anchors-01, anchor packet note D1). The
            // two differ only in the retained state of a failed Transform, where Alpha's behaviour runs on a Beta; keyed by
            // the holding type, a candidate the user turned off for Beta was still chosen there. Same receiver as
            // IsBehaviourEnabledByName and the drop-in below.
            string switchesOf = mascot.Type.Name;
            foreach (TickEdgeDef e in type.Root)
                if (EdgeHolds(e, context, mascot) && Settings.IsBehaviourEnabled(e.Target, switchesOf))
                {
                    candidates.Add(e);
                    fromNext.Add(false);
                    total += e.Weight;
                }
            TickBehaviourDef previous;
            if (previousName != null && type.RootByName.TryGetValue(previousName, out previous))
            {
                if (!previous.Add)
                {
                    total = 0;
                    candidates.Clear();
                    fromNext.Clear();
                }
                foreach (TickEdgeDef e in previous.Next)
                    if (EdgeHolds(e, context, mascot) && Settings.IsBehaviourEnabled(e.Target, switchesOf))
                    {
                        candidates.Add(e);
                        fromNext.Add(true);
                        total += e.Weight;
                    }
            }
            if (total == 0)
            {
                DropIn(mascot, "builtin:no-candidate-drop-x");
                LastSelectionCause = "fallback";
                return BuildBehaviourByName(type, "Fall");
            }
            double random = Draw(mascot, "builtin:behavior-select", "behavior-selection") * total;
            for (int i = 0; i < candidates.Count; i++)
            {
                random -= candidates[i].Weight;
                if (random < 0)
                {
                    LastSelectionCause = fromNext[i] ? "next" : "select";
                    return Build(type, candidates[i].Target);
                }
            }
            LastSelectionCause = "select";
            return null;
        }

        private bool EdgeHolds(TickEdgeDef e, TickScriptContext context, TickMascot mascot)
        {
            if (e.When == null) return true;
            ScriptValue v = context.Evaluate(e.When, "when:" + e.Target.Name);
            if (v.Kind == ScriptValueKind.Boolean) return v.Number != 0;
            // A route edge's condition is no action's parameter: unclassified, reported where it arises.
            ReportFault(mascot, "the condition of '" + e.Target.Name + "' is not a boolean (" + v.ToDisplayString() + ")", null, null);
            return v.IsTruthy;
        }

        /// <summary>Transform in place: same companion, same sid, variables and offspring kept; the target type's
        /// behaviour is entered by name. A missing target keeps the current type, as the source does.</summary>
        public void TransformInPlace(TickMascot mascot, string targetName, string behaviour)
        {
            TickType target = ResolveReference(mascot.Type, targetName);
            string from = mascot.Type.Name;
            mascot.Type = target;
            // A startup behaviour still to come belonged to the type that arrived; the Transform has chosen what this
            // companion does now, and the old name looked up on the new type could end it (policy:startup-after-arrival).
            mascot.ArrivalBehaviour = null;
            Observer.Transformed(mascot, from, target.Name, behaviour);
            // Transform.java:51-65 sets the image set first and catches a behaviour that cannot be built or entered (a
            // blank or unknown TransformBehavior): it reports the failure and the companion carries on as the new type,
            // with nothing rolled back. Which behaviour it carries on in depends on where it failed: a build failure (the
            // name looked up) throws before anything is assigned, so the behaviour it was in stays; an Init failure comes
            // after SetBehaviour has assigned the new behaviour and reported its entry, so the NEW behaviour stays
            // (CONTRACT-v1.2-trace2-consolidated.md, C.2 H2). Letting it escape ended the companion at the step's catch-all
            // instead.
            try
            {
                EnterBehaviourByName(mascot, target, behaviour, "forced");
            }
            catch (TickCantBeAliveException e)
            {
                ReportFault(mascot, "Transform into " + target.Name + ": " + e.Message, e.Cause, TickFaultVia.Transform);
            }
        }

        /// <summary>
        /// Whether this build constructs an embedded class: the per-class half of the feature gate
        /// (CompanionFormatFeatures.RunsClass). Asked of the real construction path rather than a list kept beside it, so
        /// a family that lands a class is accepted by the validator in the same commit and a list cannot fall behind.
        /// The probe's definition is empty, and a constructor may refuse it as the source's does (MoveWithTurn wants two
        /// animations): an exception means a family CLAIMED the class, because a family that does not run a class
        /// returns null without constructing anything. Only that null answers "does not run".
        /// </summary>
        public static bool RunsEmbeddedClass(string embeddedClass)
        {
            if (string.IsNullOrEmpty(embeddedClass)) return false;
            var probe = new TickWorld(null, null, null, null);
            var def = new TickActDef { Name = "probe", Type = "Embedded", Class = embeddedClass };
            try
            {
                return probe.CreateEmbedded(null, def, new Dictionary<string, TickParamDef>(StringComparer.Ordinal)) != null;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>The embedded class's action, or null when no family in this build runs the class.</summary>
        private TickAction CreateEmbedded(TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Class)
            {
                case "Breed": return new TickBreed(this, def, incoming);
                case "Transform": return new TickTransform(this, def, incoming);
            }
            // Each family of embedded classes lives in its own file (grimoire/06 section 5).
            return TickMotionFamily.TryCreate(this, type, def, incoming)
                ?? TickInstantFamily.TryCreate(this, type, def, incoming)
                ?? TickSocialFamily.TryCreate(this, type, def, incoming)
                ?? TickWindowFamily.TryCreate(this, type, def, incoming);
        }

        public TickAction BuildAction(TickType type, string name, Dictionary<string, TickParamDef> incoming)
        {
            TickActDef def;
            if (!type.Actions.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no action named '" + name + "' in " + type.Name, TickFaultCause.OfMissingAction(type, name));
            return BuildAction(type, def, incoming);
        }

        private TickAction BuildAction(TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            TickAction action = BuildActionOfKind(type, def, incoming);
            action.OwnerType = type;
            return action;
        }

        private TickAction BuildActionOfKind(TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Type)
            {
                case "Stay": return new TickStay(this, def, incoming);
                case "Animate": return new TickAnimate(this, def, incoming);
                case "Move": return new TickMove(this, def, incoming);
                case "Sequence":
                case "Select":
                {
                    // ActionBuilder.createActions: each child is built with only its own reference's parameters. The
                    // reference's refKey is applied AFTER the child is built (C.2 H7), so a fault in the child's
                    // constructor cannot know it and stays unclassified; it never takes the declaration's key (CH7).
                    var children = new TickAction[def.Children.Length];
                    for (int i = 0; i < children.Length; i++)
                    {
                        TickChildDef c = def.Children[i];
                        children[i] = BuildAction(type, c.Target, c.Binds);
                        if (c.RefKey != null) children[i].RefKey = c.RefKey;
                    }
                    return def.Type == "Sequence"
                        ? (TickAction)new TickSequence(this, def, incoming, children)
                        : new TickSelect(this, def, incoming, children);
                }
                case "Embedded":
                {
                    TickAction embedded = CreateEmbedded(type, def, incoming);
                    if (embedded != null) return embedded;
                    throw new TickCantBeAliveException("embedded class '" + def.Class + "' does not run in this build yet");
                }
                default:
                    throw new TickCantBeAliveException("action type '" + def.Type + "' is unknown");
            }
        }

        private sealed class NullObserver : ITickObserver
        {
            public static readonly NullObserver Instance = new NullObserver();
            public void BehaviourEntered(TickMascot mascot, string from, string to, string cause) { }
            public void BirthRequested(TickMascot parent, TickMascot child, int ordinal, int attempt) { }
            public void BirthRefused(TickMascot parent, int attempt, string reason) { }
            public void BirthActivated(TickMascot child) { }
            public void Transformed(TickMascot mascot, string fromImageSet, string toImageSet, string behaviour) { }
            public void DisposeRequested(TickMascot mascot, string reason) { }
            public void Disposed(TickMascot mascot) { }
            public void RandomDrawn(TickMascot mascot, string site, double value, string purpose) { }
            public void Fault(TickMascot mascot, string message, TickFaultCause cause, string via) { }
            public void Muted(TickMascot mascot, string clip) { }
            public void Sound(TickMascot mascot, string clip, string op) { }
            public void ScanTargeted(TickMascot mascot, string affordance, TickMascot target) { }
            public void Interacted(TickMascot mascot, IReadOnlyList<TickMascot> with) { }
            public void WindowMoved(TickMascot mascot, TickRect from, TickRect to) { }
            public void WindowMinimized(TickMascot mascot) { }
            public void Dragged(TickMascot mascot, string op) { }
            public void Hotspot(TickMascot mascot, string op, string behaviour) { }
        }
    }
}
