using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The social embedded classes (grimoire/06 sections 5 and 7): ScanMove, ScanJump, ScanInteract, Interact,
    /// ComplexMove, ComplexJump. One class per source class, on the source's own base (ScanMove, ScanInteract and
    /// ComplexMove are BorderedActions, ScanJump and ComplexJump plain ActionBases, Interact an Animate), each method body
    /// in the source's order of evaluation.
    /// <para>The affordance a class seeks is its Affordance parameter (ActionBase.getAffordance), the same value
    /// ActionBase.next offers; a scanning class clears the companion's offers at init and in every tick, so it never
    /// offers while it seeks. The source holds its target through a WeakReference, so a collected target would read as
    /// gone; the engine holds it strongly, and a disposed target is gone because disposal clears its offers.</para>
    /// </summary>
    internal static class TickSocialFamily
    {
        public static TickAction TryCreate(TickWorld world, TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Class)
            {
                case "ScanMove": return new TickScanMove(world, def, incoming);
                case "ScanJump": return new TickScanJump(world, def, incoming);
                case "ScanInteract": return new TickScanInteract(world, def, incoming);
                case "Interact": return new TickInteract(world, def, incoming);
                case "ComplexMove": return new TickComplexMove(world, def, incoming);
                case "ComplexJump": return new TickComplexJump(world, def, incoming);
                default: return null;
            }
        }

        /// <summary>ActionBase.getAffordance, read at each of the source's call sites.</summary>
        internal static string Affordance(TickAction action) { return action.EvalString("Affordance", null, ""); }

        /// <summary>
        /// Manager.getMascotWithAffordance (Manager.java:356-368): the FIRST active companion in manager order whose
        /// offers contain the affordance exactly. Pending births are not in the manager's list; a dragged companion is
        /// eligible if it offers; nothing is reserved, so several seekers may find the same companion.
        /// </summary>
        internal static TickMascot Scan(TickWorld world, TickMascot seeker, string affordance)
        {
            TickMascot found = null;
            foreach (TickMascot m in world.Mascots)
                if (m.Affordances.Contains(affordance)) { found = m; break; }
            world.Observer.ScanTargeted(seeker, affordance, found);
            return found;
        }

        /// <summary><c>target.get()</c> where the source dereferences it unguarded: a null target is a
        /// NullPointerException that nothing in the step catches, so the engine ends the companion instead.</summary>
        internal static TickMascot Require(TickMascot target, string action)
        {
            if (target == null) throw new TickCantBeAliveException("action '" + action + "' has no scan target");
            return target;
        }

        /// <summary>String.trim().isEmpty() as Java computes it: every character at or below U+0020 is trimmed. A null
        /// value is the source's NullPointerException.</summary>
        internal static bool IsBlank(string value, string action, string parameter)
        {
            if (value == null) throw new TickCantBeAliveException("action '" + action + "': " + parameter + " is null");
            foreach (char c in value)
                if (c > ' ') return false;
            return true;
        }

        /// <summary>
        /// The arrival transitions (ScanMove.java:121-129, ScanJump.java:109-117, ComplexMove.java:182-190,
        /// ComplexJump.java:172-180; ScanInteract.java:116-127 with <paramref name="skipBlankTarget"/>): the seeker's own
        /// Behavior, built by name in its own type; then the target's TargetBehavior, built by name in the TARGET's type
        /// (so a turned-off one drops the target in); then TargetLook turns the target to face the seeker. A missing
        /// name, a companion that cannot start, or a null in between is caught there and shown; whatever had not run
        /// yet does not run. The engine reports it as a fault.
        /// </summary>
        internal static void Arrive(TickWorld world, TickAction action, TickMascot mascot, TickMascot target, bool skipBlankTarget)
        {
            try
            {
                string own = action.EvalString("Behavior", null, "");
                mascot.SetBehaviour(world.BuildBehaviourByName(mascot.Type, own, mascot), "forced");
                if (target == null) throw new TickCantBeAliveException("there is no target to enter a behaviour");
                // ScanInteract reads TargetBehavior twice, once to test it and once to build it (ScanInteract.java:119-121).
                if (!skipBlankTarget || !IsBlank(action.EvalString("TargetBehavior", null, ""), action.Name, "TargetBehavior"))
                    target.SetBehaviour(world.BuildBehaviourByName(target.Type, action.EvalString("TargetBehavior", null, ""), target), "partner");
                if (action.EvalBool("TargetLook", null, false) && target.LookRight == mascot.LookRight)
                    target.LookRight = !mascot.LookRight;
            }
            catch (TickCantBeAliveException e)
            {
                world.ReportFault(mascot, action.Name + ": a partner transition failed: " + e.Message);
            }
        }

        /// <summary>The other active companions whose anchor is this one's, in manager order.</summary>
        internal static List<TickMascot> SharingAnchor(TickWorld world, TickMascot mascot)
        {
            var with = new List<TickMascot>();
            foreach (TickMascot m in world.Mascots)
                if (!ReferenceEquals(m, mascot) && m.Anchor.Equals(mascot.Anchor)) with.Add(m);
            return with;
        }

        /// <summary>ComplexMove.init and ComplexJump.init (ComplexMove.java:71-77, ComplexJump.java:80-86): Characteristics
        /// split on commas, each piece compared EXACTLY with the schema's Breed and Scan (" Scan" is neither). A flag is
        /// only ever turned on; nothing turns it off again. A null value is the source's NullPointerException.</summary>
        internal static void ReadCharacteristics(TickAction action, ref bool breed, ref bool scan)
        {
            string characteristics = action.EvalString("Characteristics", null, "");
            if (characteristics == null) throw new TickCantBeAliveException("action '" + action.Name + "': Characteristics is null");
            foreach (string characteristic in characteristics.Split(','))
            {
                if (string.Equals(characteristic, "Breed", StringComparison.Ordinal)) breed = true;
                if (string.Equals(characteristic, "Scan", StringComparison.Ordinal)) scan = true;
            }
        }

        /// <summary>The variant list's turn flag, looked up once (hasTurningAnimation).</summary>
        internal static bool AnyTurn(TickActDef def)
        {
            foreach (TickVariantDef v in def.Variants) if (v.Turn) return true;
            return false;
        }
    }

    /// <summary>
    /// ScanMove (ScanMove.java, a BorderedAction): at init it scans once for the first companion offering its
    /// Affordance (ScanMove.java:57-58) and publishes the target's anchor as TargetX/TargetY. Each tick it walks there as
    /// Move does, but always on both axes; it goes on while turning or while the target still offers. On EXACT arrival,
    /// not turning, the partner transitions run.
    /// </summary>
    internal sealed class TickScanMove : TickBorderedAction
    {
        private TickMascot _target;

        // Set false only at construction (ScanMove.java:40): a re-initialised instance keeps a turn in progress.
        private bool _turning;
        private bool? _hasTurning;

        public TickScanMove(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            Mascot.Affordances.Clear();
            // With no manager (a start companion or a birth, initialised before it is queued) the source does not
            // scan, and the field keeps whatever a previous activation chose.
            if (Mascot.InManager) _target = TickSocialFamily.Scan(World, Mascot, TickSocialFamily.Affordance(this));
            Mascot.Target = _target;
            Vars.Put("TargetX", _target != null ? ScriptValue.FromNumber(_target.Anchor.X) : ScriptValue.Null);
            Vars.Put("TargetY", _target != null ? ScriptValue.FromNumber(_target.Anchor.Y) : ScriptValue.Null);
        }

        public override bool HasNext()
        {
            if (!Mascot.InManager) return BaseHasNext();
            return BaseHasNext() && (_turning || (_target != null && _target.Affordances.Contains(TickSocialFamily.Affordance(this))));
        }

        protected override void Tick()
        {
            base.Tick();
            Mascot.Affordances.Clear();
            CheckGround();

            TickMascot target = TickSocialFamily.Require(_target, Name);
            int targetX = target.Anchor.X;
            int targetY = target.Anchor.Y;

            Vars.Put("TargetX", ScriptValue.FromNumber(targetX));
            Vars.Put("TargetY", ScriptValue.FromNumber(targetY));

            if (Mascot.Anchor.X != targetX)
            {
                _turning = HasTurningVariant() && (_turning || (Mascot.Anchor.X < targetX) != Mascot.LookRight);
                Mascot.LookRight = Mascot.Anchor.X < targetX;
            }
            bool down = Mascot.Anchor.Y < targetY;

            if (_turning && Time >= TickMotionFamily.Require(CurrentVariant(), Name).Duration) _turning = false;

            PlayPose(CurrentVariant());

            if ((Mascot.LookRight && Mascot.Anchor.X >= targetX) || (!Mascot.LookRight && Mascot.Anchor.X <= targetX))
                Mascot.Anchor = new TickPoint(targetX, Mascot.Anchor.Y);
            if ((down && Mascot.Anchor.Y >= targetY) || (!down && Mascot.Anchor.Y <= targetY))
                Mascot.Anchor = new TickPoint(Mascot.Anchor.X, targetY);

            if (!_turning && Mascot.Anchor.X == targetX && Mascot.Anchor.Y == targetY)
                TickSocialFamily.Arrive(World, this, Mascot, target, false);
        }

        // ScanMove.getAnimation: the first variant whose condition holds AND whose turn flag matches.
        protected override TickVariantDef CurrentVariant()
        {
            foreach (TickVariantDef v in Def.Variants)
                if (VariantHolds(v) && v.Turn == _turning) return v;
            return null;
        }

        private bool HasTurningVariant()
        {
            if (_hasTurning == null) _hasTurning = TickSocialFamily.AnyTurn(Def);
            return _hasTurning.Value;
        }
    }

    /// <summary>
    /// ScanJump (ScanJump.java, an ActionBase, not a Jump): scans once at init like ScanMove, then jumps at the target's
    /// anchor every tick as Jump does, facing it only when the x differs. Within one step it lands there with no pose
    /// and the partner transitions run. It goes on while the target still offers.
    /// </summary>
    internal sealed class TickScanJump : TickAction
    {
        private TickMascot _target;

        public TickScanJump(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            Mascot.Affordances.Clear();
            if (Mascot.InManager) _target = TickSocialFamily.Scan(World, Mascot, TickSocialFamily.Affordance(this));
            Mascot.Target = _target;
            Vars.Put("TargetX", _target != null ? ScriptValue.FromNumber(_target.Anchor.X) : ScriptValue.Null);
            Vars.Put("TargetY", _target != null ? ScriptValue.FromNumber(_target.Anchor.Y) : ScriptValue.Null);
        }

        public override bool HasNext()
        {
            if (!Mascot.InManager) return BaseHasNext();
            return BaseHasNext() && _target != null && _target.Affordances.Contains(TickSocialFamily.Affordance(this));
        }

        protected override void Tick()
        {
            Mascot.Affordances.Clear();

            TickMascot target = TickSocialFamily.Require(_target, Name);
            int targetX = target.Anchor.X;
            int targetY = target.Anchor.Y;

            Vars.Put("TargetX", ScriptValue.FromNumber(targetX));
            Vars.Put("TargetY", ScriptValue.FromNumber(targetY));

            if (Mascot.Anchor.X != targetX) Mascot.LookRight = Mascot.Anchor.X < targetX;

            double distanceX = targetX - Mascot.Anchor.X;
            double distanceY = targetY - Mascot.Anchor.Y - (EvalBool("Arc", null, true) ? Math.Abs(distanceX) / 2 : 0);

            double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);

            double velocity = EvalDouble("VelocityParam", 20.0);

            if (distance <= velocity)
            {
                Mascot.Anchor = new TickPoint(targetX, targetY);
                TickSocialFamily.Arrive(World, this, Mascot, target, false);
            }
            else
            {
                int velocityX = TickScriptContext.JavaInt(velocity * distanceX / distance);
                int velocityY = TickScriptContext.JavaInt(velocity * distanceY / distance);

                Vars.Put("VelocityX", ScriptValue.FromNumber(velocity * distanceX / distance));
                Vars.Put("VelocityY", ScriptValue.FromNumber(velocity * distanceY / distance));

                Mascot.Anchor = new TickPoint(Mascot.Anchor.X + velocityX, Mascot.Anchor.Y + velocityY);
                PlayPose(CurrentVariant());
            }
        }
    }

    /// <summary>
    /// ScanInteract (ScanInteract.java, a BorderedAction that plays its animation once): remote. Each tick, if it has no
    /// target or the target stopped offering, it scans again (ScanInteract.java:87-88). With an offering target it faces
    /// it when Look holds (a turn variant plays first, and the clock restarts after it), plays its animation, and on the
    /// animation's last tick, not turning, a non-blank own Behavior runs the partner transitions, a blank TargetBehavior
    /// leaving the target as it is. A blank own Behavior blocks them all. With no offering target it plays nothing.
    /// </summary>
    internal sealed class TickScanInteract : TickBorderedAction
    {
        // The target and the turning flag are fields the source never resets at init (ScanInteract.java:41-43, 53-62).
        private TickMascot _target;
        private bool _turning;
        private bool? _hasTurning;

        public TickScanInteract(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            Mascot.Affordances.Clear();
            Mascot.Target = _target;
            Vars.Put("TargetX", ScriptValue.Null);
            Vars.Put("TargetY", ScriptValue.Null);
        }

        public override bool HasNext()
        {
            // The source evaluates the animation's duration first, then ActionBase.hasNext (ScanInteract.java:67-69);
            // both always run, in this order.
            bool intime = Time < TickMotionFamily.Require(CurrentVariant(), Name).Duration;
            bool guard = BaseHasNext();
            // policy:scaninteract-precedence: the source returns (guard && turning) || intime, so animation time
            // bypasses Condition and Duration; the host requires the guard: guard && (turning || intime).
            return guard && (_turning || intime);
        }

        protected override void Tick()
        {
            base.Tick();
            Mascot.Affordances.Clear();
            CheckGround();

            if (Mascot.InManager && (_target == null || !_target.Affordances.Contains(TickSocialFamily.Affordance(this))))
                _target = TickSocialFamily.Scan(World, Mascot, TickSocialFamily.Affordance(this));
            Mascot.Target = _target;

            Vars.Put("TargetX", _target != null ? ScriptValue.FromNumber(_target.Anchor.X) : ScriptValue.Null);
            Vars.Put("TargetY", _target != null ? ScriptValue.FromNumber(_target.Anchor.Y) : ScriptValue.Null);

            if (_target != null && _target.Affordances.Contains(TickSocialFamily.Affordance(this)))
            {
                if (EvalBool("Look", null, true))
                {
                    if (Mascot.Anchor.X != _target.Anchor.X)
                    {
                        _turning = HasTurningVariant() && (_turning || (Mascot.Anchor.X < _target.Anchor.X) != Mascot.LookRight);
                        Mascot.LookRight = Mascot.Anchor.X < _target.Anchor.X;
                    }

                    if (_turning && Time >= TickMotionFamily.Require(CurrentVariant(), Name).Duration)
                    {
                        SetTime(Time - TickMotionFamily.Require(CurrentVariant(), Name).Duration);
                        _turning = false;
                    }
                }

                PlayPose(CurrentVariant());

                if (!_turning
                    && (Time == TickMotionFamily.Require(CurrentVariant(), Name).Duration - 1 || TickMotionFamily.Require(CurrentVariant(), Name).Duration == 1)
                    && !TickSocialFamily.IsBlank(EvalString("Behavior", null, ""), Name, "Behavior"))
                    TickSocialFamily.Arrive(World, this, Mascot, _target, true);
            }
        }

        protected override TickVariantDef CurrentVariant()
        {
            foreach (TickVariantDef v in Def.Variants)
                if (VariantHolds(v) && v.Turn == _turning) return v;
            return null;
        }

        private bool HasTurningVariant()
        {
            if (_hasTurning == null) _hasTurning = TickSocialFamily.AnyTurn(Def);
            return _hasTurning.Value;
        }
    }

    /// <summary>
    /// Interact (Interact.java, an Animate): it goes on while two or more active companions, itself included, stand on
    /// its anchor (Manager.hasOverlappingMascotsAtPoint); no partner is remembered. On the animation's last tick a
    /// non-blank Behavior changes this companion only.
    /// </summary>
    internal sealed class TickInteract : TickAnimate
    {
        public TickInteract(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            if (!base.HasNext()) return false;
            // The source asks its manager unguarded (Interact.java:36): a companion with none (a start companion or a
            // birth, initialised before it is queued) is a NullPointerException; the engine ends the companion.
            if (!Mascot.InManager) throw new TickCantBeAliveException("action '" + Name + "' (Interact) ran on a companion with no manager");
            int count = 0;
            foreach (TickMascot m in World.Mascots)
            {
                if (m.Anchor.Equals(Mascot.Anchor)) count++;
                if (count > 1) return true;
            }
            return false;
        }

        protected override void Tick()
        {
            // Observed, not evaluated: anchors are stored state.
            World.Observer.Interacted(Mascot, TickSocialFamily.SharingAnchor(World, Mascot));

            base.Tick();

            if ((Time == TickMotionFamily.Require(CurrentVariant(), Name).Duration - 1 || TickMotionFamily.Require(CurrentVariant(), Name).Duration == 1)
                && !TickSocialFamily.IsBlank(EvalString("Behavior", null, ""), Name, "Behavior"))
            {
                try
                {
                    Mascot.SetBehaviour(World.BuildBehaviourByName(Mascot.Type, EvalString("Behavior", null, ""), Mascot), "forced");
                }
                catch (TickCantBeAliveException e)
                {
                    World.ReportFault(Mascot, Name + ": its Behavior could not be entered: " + e.Message);
                }
            }
        }
    }

    /// <summary>
    /// ComplexMove (ComplexMove.java, a BorderedAction): Characteristics split on commas, each piece compared exactly
    /// with Breed and Scan, turns on breeding (BreedMove's: after the move, each BornInterval tick, never while turning)
    /// and scanning (ScanMove's). Both flags are set only at construction and init, never cleared. Scanning off, it walks
    /// to TargetX/TargetY, facing and descending without Move's default-target guards.
    /// </summary>
    internal sealed class TickComplexMove : TickBorderedAction
    {
        private const int DefaultTarget = int.MaxValue;
        private readonly TickBreedDelegate _breed;
        private TickMascot _target;
        private bool _turning;
        private bool? _hasTurning;
        private bool _breedEnabled;
        private bool _scanEnabled;

        public TickComplexMove(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            _breed = new TickBreedDelegate(world, this);
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);

            TickSocialFamily.ReadCharacteristics(this, ref _breedEnabled, ref _scanEnabled);

            if (_breedEnabled)
            {
                _breed.ValidateBornCount();
                _breed.ValidateBornInterval();
            }
            if (_scanEnabled)
            {
                Mascot.Affordances.Clear();
                if (Mascot.InManager) _target = TickSocialFamily.Scan(World, Mascot, TickSocialFamily.Affordance(this));
                Vars.Put("TargetX", _target != null ? ScriptValue.FromNumber(_target.Anchor.X) : ScriptValue.Null);
                Vars.Put("TargetY", _target != null ? ScriptValue.FromNumber(_target.Anchor.Y) : ScriptValue.Null);
            }
            Mascot.Target = _target;
        }

        public override bool HasNext()
        {
            if (_scanEnabled)
            {
                if (!Mascot.InManager) return BaseHasNext();
                return BaseHasNext() && (_turning || (_target != null && _target.Affordances.Contains(TickSocialFamily.Affordance(this))));
            }
            int targetX = EvalInt("TargetX", DefaultTarget);
            int targetY = EvalInt("TargetY", DefaultTarget);
            // Move's "hasNotReached", which means the opposite: either axis at its target ends the move.
            bool reached = (targetX != int.MinValue && Mascot.Anchor.X == targetX) || (targetY != int.MinValue && Mascot.Anchor.Y == targetY);
            return BaseHasNext() && (!reached || _turning);
        }

        protected override void Tick()
        {
            base.Tick();
            if (_scanEnabled) Mascot.Affordances.Clear();
            CheckGround();

            int targetX = _scanEnabled ? TickSocialFamily.Require(_target, Name).Anchor.X : EvalInt("TargetX", DefaultTarget);
            int targetY = _scanEnabled ? TickSocialFamily.Require(_target, Name).Anchor.Y : EvalInt("TargetY", DefaultTarget);

            if (_scanEnabled)
            {
                Vars.Put("TargetX", ScriptValue.FromNumber(targetX));
                Vars.Put("TargetY", ScriptValue.FromNumber(targetY));
            }

            if (Mascot.Anchor.X != targetX)
            {
                _turning = HasTurningVariant() && (_turning || (Mascot.Anchor.X < targetX) != Mascot.LookRight);
                Mascot.LookRight = Mascot.Anchor.X < targetX;
            }
            bool down = Mascot.Anchor.Y < targetY;

            if (_turning && Time >= TickMotionFamily.Require(CurrentVariant(), Name).Duration) _turning = false;

            PlayPose(CurrentVariant());

            if (targetX != DefaultTarget || _scanEnabled)
            {
                if ((Mascot.LookRight && Mascot.Anchor.X >= targetX) || (!Mascot.LookRight && Mascot.Anchor.X <= targetX))
                    Mascot.Anchor = new TickPoint(targetX, Mascot.Anchor.Y);
            }
            if (targetY != DefaultTarget || _scanEnabled)
            {
                if ((down && Mascot.Anchor.Y >= targetY) || (!down && Mascot.Anchor.Y <= targetY))
                    Mascot.Anchor = new TickPoint(Mascot.Anchor.X, targetY);
            }

            if (_breedEnabled && _breed.IsIntervalFrame() && !_turning && _breed.IsEnabled(Mascot))
                _breed.Breed(Mascot);

            // policy:complexmove-guarded: the source runs this block with scanning off too (ComplexMove.java:180-206),
            // enters the seeker's own Behavior and then dereferences its null target; the host runs it only while
            // scanning, as the source's ComplexJump does (ComplexJump.java:170).
            if (_scanEnabled && !_turning && Mascot.Anchor.X == targetX && Mascot.Anchor.Y == targetY)
                TickSocialFamily.Arrive(World, this, Mascot, _target, false);
        }

        protected override TickVariantDef CurrentVariant()
        {
            foreach (TickVariantDef v in Def.Variants)
                if (VariantHolds(v) && v.Turn == _turning) return v;
            return null;
        }

        private bool HasTurningVariant()
        {
            if (_hasTurning == null) _hasTurning = TickSocialFamily.AnyTurn(Def);
            return _hasTurning.Value;
        }
    }

    /// <summary>
    /// ComplexJump (ComplexJump.java, an ActionBase): Characteristics as ComplexMove's. Scanning, it jumps at the
    /// target as ScanJump does and runs the partner transitions on landing; scanning off, it is a Jump to
    /// TargetX/TargetY. Breeding (BreedJump's) comes after the jump, every BornInterval tick, landing included.
    /// </summary>
    internal sealed class TickComplexJump : TickAction
    {
        private readonly TickBreedDelegate _breed;
        private TickMascot _target;
        private bool _breedEnabled;
        private bool _scanEnabled;

        public TickComplexJump(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            _breed = new TickBreedDelegate(world, this);
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);

            TickSocialFamily.ReadCharacteristics(this, ref _breedEnabled, ref _scanEnabled);

            if (_breedEnabled)
            {
                _breed.ValidateBornCount();
                _breed.ValidateBornInterval();
            }
            if (_scanEnabled)
            {
                Mascot.Affordances.Clear();
                if (Mascot.InManager) _target = TickSocialFamily.Scan(World, Mascot, TickSocialFamily.Affordance(this));
                Vars.Put("TargetX", _target != null ? ScriptValue.FromNumber(_target.Anchor.X) : ScriptValue.Null);
                Vars.Put("TargetY", _target != null ? ScriptValue.FromNumber(_target.Anchor.Y) : ScriptValue.Null);
            }
            Mascot.Target = _target;
        }

        public override bool HasNext()
        {
            if (_scanEnabled)
            {
                if (!Mascot.InManager) return BaseHasNext();
                return BaseHasNext() && _target != null && _target.Affordances.Contains(TickSocialFamily.Affordance(this));
            }
            int targetX = EvalInt("TargetX", 0);
            int targetY = EvalInt("TargetY", 0);

            double distanceX = targetX - Mascot.Anchor.X;
            double distanceY = targetY - Mascot.Anchor.Y - (EvalBool("Arc", null, true) ? Math.Abs(distanceX) / 2 : 0);

            double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);

            return BaseHasNext() && distance != 0;
        }

        protected override void Tick()
        {
            int targetX;
            int targetY;

            if (_scanEnabled)
            {
                Mascot.Affordances.Clear();

                TickMascot target = TickSocialFamily.Require(_target, Name);
                targetX = target.Anchor.X;
                targetY = target.Anchor.Y;

                Vars.Put("TargetX", ScriptValue.FromNumber(targetX));
                Vars.Put("TargetY", ScriptValue.FromNumber(targetY));

                if (Mascot.Anchor.X != targetX) Mascot.LookRight = Mascot.Anchor.X < targetX;
            }
            else
            {
                targetX = EvalInt("TargetX", 0);
                targetY = EvalInt("TargetY", 0);

                Mascot.LookRight = Mascot.Anchor.X < targetX;
            }

            double distanceX = targetX - Mascot.Anchor.X;
            double distanceY = targetY - Mascot.Anchor.Y - (EvalBool("Arc", null, true) ? Math.Abs(distanceX) / 2 : 0);

            double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);

            double velocity = EvalDouble("VelocityParam", 20.0);

            if (distance <= velocity)
            {
                Mascot.Anchor = new TickPoint(targetX, targetY);

                if (_scanEnabled) TickSocialFamily.Arrive(World, this, Mascot, _target, false);
            }
            else
            {
                int velocityX = TickScriptContext.JavaInt(velocity * distanceX / distance);
                int velocityY = TickScriptContext.JavaInt(velocity * distanceY / distance);

                Vars.Put("VelocityX", ScriptValue.FromNumber(velocity * distanceX / distance));
                Vars.Put("VelocityY", ScriptValue.FromNumber(velocity * distanceY / distance));

                Mascot.Anchor = new TickPoint(Mascot.Anchor.X + velocityX, Mascot.Anchor.Y + velocityY);
                PlayPose(CurrentVariant());
            }

            if (_breedEnabled && _breed.IsIntervalFrame() && _breed.IsEnabled(Mascot))
                _breed.Breed(Mascot);
        }
    }
}
