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
        void Fault(TickMascot mascot, string message);

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

        public void ReportFault(string source, string message) { _world.ReportFault(_mascot, message + " in '" + source + "'"); }

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
            return v.When == null || Truth(Context.Evaluate(v.When, "variant"), "variant condition");
        }

        /// <summary>Animation.next: the pose for this action age moves the companion and sets its image.</summary>
        protected void PlayPose(TickVariantDef variant)
        {
            if (variant == null) throw new TickCantBeAliveException("action '" + Name + "' has no variant whose condition holds");
            TickPoseDef pose = variant.PoseAt(Time);
            Mascot.ApplyPose(pose, variant);
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
            return Truth(Read(name, guard), name);
        }

        protected internal int EvalInt(string name, ScriptProgram guard, int fallback)
        {
            if (!Present(name, guard)) return fallback;
            ScriptValue v = Read(name, guard);
            if (v.Kind != ScriptValueKind.Number)
            {
                World.ReportFault(Mascot, Name + ": " + name + " is not a number (" + v.ToDisplayString() + ")");
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
                World.ReportFault(Mascot, Name + ": " + name + " is not a number (" + v.ToDisplayString() + ")");
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

        protected bool Truth(ScriptValue v, string what)
        {
            if (v.Kind == ScriptValueKind.Boolean) return v.Number != 0;
            // The source casts to Boolean and a non-boolean ends the companion; the engine reports it and reads the
            // value's truth instead (recorded as approximate where a converted pack depends on it).
            World.ReportFault(Mascot, Name + ": " + what + " is not a boolean (" + v.ToDisplayString() + ")");
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
            Border = null;
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
            if (v == null) throw new TickCantBeAliveException("action '" + Name + "' has no variant for turning=" + _turning);
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
                    World.ReportFault(Mascot, Name + ": a looping sequence whose children all end at once");
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
            if (interval == 0) throw new TickCantBeAliveException(_action.Name + ": BornInterval became 0");
            return _action.Time % interval == 0;
        }

        public void ValidateBornCount()
        {
            if (_action.EvalInt("BornCount", 1) < 1) throw new TickCantBeAliveException(_action.Name + ": BornCount must be positive");
        }

        public void ValidateBornInterval()
        {
            if (_action.EvalInt("BornInterval", 1) < 1) throw new TickCantBeAliveException(_action.Name + ": BornInterval must be positive");
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
                if (!_world.HasRoomForBirth())
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
                    _world.Observer.BirthRefused(parent, attempt, "error");
                    _world.ReportFault(parent, "a birth failed: " + e.Message);
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

        /// <summary>UserBehavior.init: init the action; one that cannot even start hands straight to the next.</summary>
        public void Init(TickMascot mascot)
        {
            _mascot = mascot;
            Action.Init(mascot);
            if (!Action.HasNext())
                mascot.SetBehaviour(_world.BuildNextBehaviour(Type, mascot, Name), _world.LastSelectionCause);
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
                            _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, h.Behaviour, _mascot), "forced");
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
                    else
                    {
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
                if (h.Behaviour != null) _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, h.Behaviour, _mascot), "forced");
                break;
            }
            if (!handled) handled = !Action.IsDraggable();
            if (!handled) _mascot.SetBehaviour(_world.BuildBehaviourByName(Type, "Dragged"), "forced");
        }

        /// <summary>UserBehavior.mouseReleased (left button): a held hotspot lets go; a companion being dragged is
        /// thrown. A press and release in one step, before any action set the dragging flag, throws nothing.</summary>
        public void MouseReleased()
        {
            if (_mascot.IsHotspotClicked) _mascot.CursorPosition = null;
            if (_mascot.Dragging)
            {
                _mascot.Dragging = false;
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
        /// first pose, so the image is sized and named by <see cref="PoseType"/>, not <see cref="Type"/>.</summary>
        public TickPoseDef Pose;
        public TickVariantDef PoseVariant;
        public bool PoseRight;
        public TickType PoseType;

        /// <summary>A start companion: queued by Manager.add like a birth, but activated with no birth event.</summary>
        public bool IsStartup;

        /// <summary>The clip the current pose asks for (Mascot.setSound), played by the window layer's apply.</summary>
        public string Sound;

        /// <summary>BringToFront ran since the window layer last raised this companion's window.</summary>
        public bool BringToFrontRequested;

        /// <summary>The companion the running scan action chose (its WeakReference target), or null; written to the
        /// state line's <c>target</c>. Cleared whenever an action initialises.</summary>
        public TickMascot Target;
        public bool Visible;
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

        /// <summary>Pose.next: move by the pose velocity (mirrored when facing right), then take its image.</summary>
        public void ApplyPose(TickPoseDef pose, TickVariantDef variant)
        {
            if (pose == null) return;
            Anchor = new TickPoint(Anchor.X + (LookRight ? -pose.Dx : pose.Dx), Anchor.Y + pose.Dy);
            Pose = pose;
            PoseVariant = variant;
            PoseRight = LookRight;
            PoseType = Type;
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
                _world.ReportFault(this, e.Message);
                Dispose("error");
            }
            catch (Exception e)
            {
                // Anything else is a fault the source would have died of too (a null Affordance at ActionBase.java:95,
                // a cast): this companion ends, reported, and the step goes on for the rest. Escaping here would leave
                // the step half done and, in the app, throw out of the tick timer.
                _world.ReportFault(this, "internal fault: " + e.GetType().Name + ": " + e.Message);
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
        public int MaximumCompanions = 16;
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

        /// <summary>The host's cap counts active and pending companions alike (policy:birth-cap).</summary>
        public bool HasRoomForBirth() { return _mascots.Count + _added.Count < MaximumCompanions; }

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
                m.SetBehaviour(string.IsNullOrEmpty(behaviour) ? BuildNextBehaviour(type, m, null) : BuildBehaviourByName(type, behaviour, m),
                    string.IsNullOrEmpty(behaviour) ? LastSelectionCause : "forced");
            }
            catch (Exception e)
            {
                // Main.createMascot catches a first behaviour that cannot be built (a missing name, a class that refuses
                // its definition), shows the error and disposes the mascot, which never joins the manager. Same here:
                // reported, disposed, not added; the caller sees Disposing.
                ReportFault(m, (e is TickCantBeAliveException ? "" : "internal fault: " + e.GetType().Name + ": ")
                    + "the first behaviour could not be built: " + e.Message);
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

        private void DispatchInputs()
        {
            foreach (TickInput input in Environment.Inputs)
            {
                TickMascot m = null;
                foreach (TickMascot active in _mascots)
                    if (active.Sid == input.Sid) { m = active; break; }
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
            try { mascot.Behaviour.MousePressed(local); }
            catch (TickCantBeAliveException e) { ReportFault(mascot, e.Message); mascot.Dispose("error"); }
        }

        public void Release(TickMascot mascot)
        {
            if (!mascot.Animating || mascot.Behaviour == null) return;
            try { mascot.Behaviour.MouseReleased(); }
            catch (TickCantBeAliveException e) { ReportFault(mascot, e.Message); mascot.Dispose("error"); }
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

        public void ReportFault(TickMascot mascot, string message) { Observer.Fault(mascot, message); }

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
        public TickBehaviour BuildBehaviourByName(TickType type, string name)
        {
            TickBehaviourDef def;
            if (!type.RootByName.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no behaviour named '" + name + "' in " + type.Name);
            return Build(type, def);
        }

        /// <summary>Configuration.buildBehavior(name, mascot): a behaviour turned off for this companion drops it in
        /// and enters Fall instead.</summary>
        public TickBehaviour BuildBehaviourByName(TickType type, string name, TickMascot mascot)
        {
            TickBehaviourDef def;
            if (!type.RootByName.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no behaviour named '" + name + "' in " + type.Name);
            if (Settings.IsBehaviourEnabled(def, mascot.Type.Name)) return Build(type, def);
            DropIn(mascot, "builtin:disabled-behavior-drop-x");
            return BuildBehaviourByName(type, "Fall");
        }

        /// <summary>A born companion's entry behaviour. The source builds it for the PARENT, so a turned-off one
        /// drops the parent in; the host keeps the parent where it is and the child falls from its birth point
        /// (policy:breed-parent-reposition).</summary>
        public void EnterBirthBehaviour(TickMascot child, string name, TickMascot parent)
        {
            TickBehaviourDef def;
            if (!child.Type.RootByName.TryGetValue(name ?? "", out def))
                throw new TickCantBeAliveException("no behaviour named '" + name + "' in " + child.Type.Name);
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
            foreach (TickEdgeDef e in type.Root)
                if (EdgeHolds(e, context, mascot) && Settings.IsBehaviourEnabled(e.Target, type.Name))
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
                    if (EdgeHolds(e, context, mascot) && Settings.IsBehaviourEnabled(e.Target, type.Name))
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
            ReportFault(mascot, "the condition of '" + e.Target.Name + "' is not a boolean (" + v.ToDisplayString() + ")");
            return v.IsTruthy;
        }

        /// <summary>Transform in place: same companion, same sid, variables and offspring kept; the target type's
        /// behaviour is entered by name. A missing target keeps the current type, as the source does.</summary>
        public void TransformInPlace(TickMascot mascot, string targetName, string behaviour)
        {
            TickType target = ResolveReference(mascot.Type, targetName);
            string from = mascot.Type.Name;
            mascot.Type = target;
            Observer.Transformed(mascot, from, target.Name, behaviour);
            mascot.SetBehaviour(BuildBehaviourByName(target, behaviour, mascot), "forced");
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
                throw new TickCantBeAliveException("no action named '" + name + "' in " + type.Name);
            return BuildAction(type, def, incoming);
        }

        private TickAction BuildAction(TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Type)
            {
                case "Stay": return new TickStay(this, def, incoming);
                case "Animate": return new TickAnimate(this, def, incoming);
                case "Move": return new TickMove(this, def, incoming);
                case "Sequence":
                case "Select":
                {
                    // ActionBuilder.createActions: each child is built with only its own reference's parameters.
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
            public void Fault(TickMascot mascot, string message) { }
            public void Muted(TickMascot mascot, string clip) { }
            public void Sound(TickMascot mascot, string clip, string op) { }
            public void ScanTargeted(TickMascot mascot, string affordance, TickMascot target) { }
            public void Interacted(TickMascot mascot, IReadOnlyList<TickMascot> with) { }
            public void WindowMoved(TickMascot mascot, TickRect from, TickRect to) { }
            public void WindowMinimized(TickMascot mascot) { }
        }
    }
}
