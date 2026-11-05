using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The motion family of embedded classes (grimoire/06 section 5): Fall (and Thrown, which runs Fall), Jump, Dragged,
    /// Regist, Turn, MoveWithTurn, the deprecated Broadcast aliases, and Breed's moving variants. One class per source
    /// class, each method body in the source's order of evaluation, because evaluation has side effects (random draws,
    /// lazy caches, the companion's remembered work area).
    /// </summary>
    internal static class TickMotionFamily
    {
        public static TickAction TryCreate(TickWorld world, TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Class)
            {
                case "Fall": return new TickFall(world, def, incoming);
                case "Jump": return new TickJump(world, def, incoming);
                case "Dragged": return new TickDragged(world, def, incoming);
                case "Regist": return new TickRegist(world, def, incoming);
                case "Turn": return new TickTurn(world, def, incoming);
                case "MoveWithTurn": return new TickMoveWithTurn(world, def, incoming);
                // The deprecated Broadcast classes add nothing to their bases (Broadcast.java:15, BroadcastStay.java:15,
                // BroadcastMove.java:15, BroadcastJump.java:16): the offer they were named for is ActionBase's Affordance.
                case "Broadcast": return new TickAnimate(world, def, incoming);
                case "BroadcastStay": return new TickStay(world, def, incoming);
                case "BroadcastMove": return new TickMove(world, def, incoming);
                case "BroadcastJump": return new TickJump(world, def, incoming);
                case "BreedMove": return new TickBreedMove(world, def, incoming);
                case "BreedJump": return new TickBreedJump(world, def, incoming);
                case "BreedFall": return new TickBreedFall(world, def, incoming);
                default: return null;
            }
        }

        /// <summary>Java's Math.abs(int): Integer.MIN_VALUE stays itself, where Math.Abs throws.</summary>
        internal static int JavaAbs(int v) { return v < 0 ? unchecked(-v) : v; }

        /// <summary>The variant a source <c>getAnimation().getDuration()</c> reads. The source dereferences a null
        /// animation and its manager thread dies; the engine ends the companion instead, as PlayPose does.</summary>
        internal static TickVariantDef Require(TickVariantDef variant, string action)
        {
            if (variant == null) throw new TickCantBeAliveException("action '" + action + "' has no variant whose condition holds");
            return variant;
        }

        /// <summary>The source's <c>getOffsetType().equals(schema "Origin")</c>.</summary>
        internal static bool IsOrigin(string offsetType) { return string.Equals(offsetType, "Origin", StringComparison.Ordinal); }
    }

    /// <summary>
    /// Fall (Fall.java): velocities damped by RegistanceX/Y plus Gravity each tick, the fractional parts accumulated,
    /// and the move swept one pixel step at a time so a floor (searched up to 80 px above each step) or a wall stops it.
    /// It ends once its anchor is on a floor or a wall. Thrown is a behaviour that runs a Fall; nothing more.
    /// </summary>
    internal class TickFall : TickAction
    {
        private double _velocityX;
        private double _velocityY;

        // The remainders are fields the source sets only at construction (Fall.java:49-51; init sets the velocities
        // alone, :59-64), so they carry over when one instance is initialised again (a looping Sequence).
        private double _modX;
        private double _modY;

        public TickFall(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            // InitialVX/VY are read as Number.intValue (Fall.java:134-142): a fraction is dropped.
            _velocityX = EvalInt("InitialVX", 0);
            _velocityY = EvalInt("InitialVY", 0);
        }

        public override bool HasNext()
        {
            TickPoint pos = Mascot.Anchor;
            bool onBorder = false;
            if (Mascot.Environment.GetFloor().IsOn(pos)) onBorder = true;
            if (Mascot.Environment.GetWall().IsOn(pos)) onBorder = true;
            return BaseHasNext() && !onBorder;
        }

        protected override void Tick()
        {
            if (_velocityX != 0) Mascot.LookRight = _velocityX > 0;

            _velocityX = _velocityX - _velocityX * EvalDouble("RegistanceX", 0.05);
            _velocityY = _velocityY - _velocityY * EvalDouble("RegistanceY", 0.1) + EvalDouble("Gravity", 2);

            Vars.Put("VelocityX", ScriptValue.FromNumber(_velocityX));
            Vars.Put("VelocityY", ScriptValue.FromNumber(_velocityY));

            _modX = _modX + _velocityX % 1;
            _modY = _modY + _velocityY % 1;

            int dx = TickScriptContext.JavaInt(_velocityX) + TickScriptContext.JavaInt(_modX);
            int dy = TickScriptContext.JavaInt(_velocityY) + TickScriptContext.JavaInt(_modY);

            _modX = _modX % 1;
            _modY = _modY % 1;

            int dev = Math.Max(1, Math.Max(TickMotionFamily.JavaAbs(dx), TickMotionFamily.JavaAbs(dy)));

            TickPoint start = Mascot.Anchor;
            for (int i = 0; i <= dev; ++i)
            {
                int x = start.X + dx * i / dev;
                int y = start.Y + dy * i / dev;

                Mascot.Anchor = new TickPoint(x, y);
                if (dy > 0)
                {
                    // The source's window workaround: a floor up to 80 px above this step is caught (Fall.java:113-124).
                    // The separator-ignoring lookups let a companion fall through where two screens meet.
                    bool landed = false;
                    for (int j = -80; j <= 0; ++j)
                    {
                        Mascot.Anchor = new TickPoint(x, y + j);
                        if (Mascot.Environment.GetFloor(true).IsOn(Mascot.Anchor))
                        {
                            landed = true;
                            break;
                        }
                    }
                    if (landed) break;
                }
                if (Mascot.Environment.GetWall(true).IsOn(Mascot.Anchor)) break;
            }

            PlayPose(CurrentVariant());
        }
    }

    /// <summary>
    /// Jump (Jump.java): re-aims at TargetX/TargetY every tick, aiming abs(dx)/2 higher when Arc holds, and moves by the
    /// truncated components of VelocityParam along that line, publishing them (untruncated) before the pose plays. Within
    /// one step of the target it lands there with no pose, and the next check ends it.
    /// </summary>
    internal class TickJump : TickAction
    {
        public TickJump(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            int targetX = EvalInt("TargetX", 0);
            int targetY = EvalInt("TargetY", 0);

            double distanceX = targetX - Mascot.Anchor.X;
            double distanceY = targetY - Mascot.Anchor.Y - (EvalBool("Arc", null, true) ? Math.Abs(distanceX) / 2 : 0);

            double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);

            return BaseHasNext() && distance != 0;
        }

        protected override void Tick()
        {
            int targetX = EvalInt("TargetX", 0);
            int targetY = EvalInt("TargetY", 0);

            Mascot.LookRight = Mascot.Anchor.X < targetX;

            double distanceX = targetX - Mascot.Anchor.X;
            double distanceY = targetY - Mascot.Anchor.Y - (EvalBool("Arc", null, true) ? Math.Abs(distanceX) / 2 : 0);

            double distance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);

            double velocity = EvalDouble("VelocityParam", 20.0);

            if (distance <= velocity)
            {
                Mascot.Anchor = new TickPoint(targetX, targetY);
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
    /// Dragged (Dragged.java): held at the cursor plus (OffsetX, OffsetY) scaled, facing left, with the damped FootX/FootDX
    /// sway published for scripts. A horizontal move of 5 px or more restarts the action clock; it ends at the regist
    /// time, 250 ticks, which each tick that reaches it extends by one with a 90% chance (builtin:dragged-regist).
    /// </summary>
    internal sealed class TickDragged : TickAction
    {
        private double _footX;

        // Set only at construction in the source (Dragged.java:41; init sets FootX and the timer, :59-60): the sway
        // velocity carries over when one instance is initialised again.
        private double _footDx;

        private int _timeToRegist;
        private double _scaling;

        public TickDragged(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            _scaling = World.Scaling;
            _footX = World.Environment.Cursor.X + TickBreed.JavaRound(EvalInt("OffsetX", 0) * _scaling);
            _timeToRegist = 250;
        }

        public override bool HasNext()
        {
            return BaseHasNext() && Time < _timeToRegist;
        }

        protected override void Tick()
        {
            Mascot.LookRight = false;
            Mascot.Dragging = true;
            Mascot.Environment.RefreshWorkArea();

            TickPoint cursor = World.Environment.Cursor;

            int offsetX = TickBreed.JavaRound(EvalInt("OffsetX", 0) * _scaling);
            int offsetY = TickBreed.JavaRound(EvalInt("OffsetY", 120) * _scaling);
            if (TickMotionFamily.IsOrigin(EvalString("OffsetType", null, "ImageAnchor")))
            {
                // The image the companion shows now, from the last pose (the source reads getImage(), which is null
                // before any pose; the engine uses the type's cell anchor then).
                TickPoint center = Mascot.ImageCenter;
                offsetX = 0 - offsetX + center.X;
                offsetY = 0 - offsetY + center.Y;
            }

            if (TickMotionFamily.JavaAbs(cursor.X - Mascot.Anchor.X + offsetX) >= 5)
            {
                SetTime(0);
            }

            int newX = cursor.X;

            _footDx = (_footDx + (newX - _footX) * 0.1) * 0.8;
            _footX = _footX + _footDx;

            Vars.Put("FootDX", ScriptValue.FromNumber(_footDx));
            Vars.Put("FootX", ScriptValue.FromNumber(_footX));

            PlayPose(CurrentVariant());

            Mascot.Anchor = new TickPoint(cursor.X + offsetX, cursor.Y + offsetY);

            // Only the tick at the regist time draws (the && short-circuits, Dragged.java:104).
            if (Time == _timeToRegist - 1 && World.Draw(Mascot, "builtin:dragged-regist", "dragged-regist") >= 0.1)
                _timeToRegist++;
        }

        // Dragged.refreshHotspots: the action offers no hotspots.
        protected override void RefreshHotspots() { Mascot.Hotspots.Clear(); }
    }

    /// <summary>
    /// Regist (Regist.java, an ActionBase, not an Animate): the struggle after a drag. It continues while the cursor is
    /// within 5 px horizontally of the anchor less OffsetX (OffsetY is not read); on its animation's last tick it faces
    /// at random (builtin:regist-facing) and loses the ground, which enters Fall.
    /// </summary>
    internal sealed class TickRegist : TickAction
    {
        private double _scaling;

        public TickRegist(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            _scaling = World.Scaling;
        }

        public override bool HasNext()
        {
            int offsetX = TickBreed.JavaRound(EvalInt("OffsetX", 0) * _scaling);
            if (TickMotionFamily.IsOrigin(EvalString("OffsetType", null, "ImageAnchor")))
            {
                offsetX = 0 - offsetX + Mascot.ImageCenter.X;
            }

            bool notMoved = TickMotionFamily.JavaAbs(World.Environment.Cursor.X - Mascot.Anchor.X + offsetX) < 5;

            return BaseHasNext() && notMoved;
        }

        protected override void Tick()
        {
            Mascot.Dragging = true;

            TickVariantDef animation = CurrentVariant();
            PlayPose(animation);

            // The source looks the animation up again here (Regist.java:67).
            if (Time + 1 >= TickMotionFamily.Require(CurrentVariant(), Name).Duration)
            {
                Mascot.LookRight = World.Draw(Mascot, "builtin:regist-facing", "regist-facing") < 0.5;
                throw new TickLostGroundException();
            }
        }

        // Regist.refreshHotspots: the action offers no hotspots.
        protected override void RefreshHotspots() { Mascot.Hotspots.Clear(); }
    }

    /// <summary>
    /// Turn (Turn.java): faces LookRight (default: the opposite of the facing now, read afresh each tick, so without the
    /// parameter it flips every tick) and plays its animation once, but only if the facing had to change.
    /// </summary>
    internal sealed class TickTurn : TickBorderedAction
    {
        // Set false only at construction (Turn.java:21) and never cleared: once an instance has turned, a later
        // activation of it plays again whatever the facing.
        private bool _turning;

        public TickTurn(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Tick()
        {
            Mascot.LookRight = IsLookRight();

            base.Tick();

            CheckGround();

            PlayPose(CurrentVariant());
        }

        public override bool HasNext()
        {
            // || short-circuits: once turning, LookRight is not read here again.
            _turning = _turning || IsLookRight() != Mascot.LookRight;
            TickVariantDef v = CurrentVariant();
            bool intime = v != null && Time < v.Duration;

            return BaseHasNext() && intime && _turning;
        }

        private bool IsLookRight() { return EvalBool("LookRight", null, !Mascot.LookRight); }
    }

    /// <summary>
    /// MoveWithTurn (MoveWithTurn.java, deprecated): a Move whose LAST variant is its turn, whatever its turn flag, and
    /// which always has one. Fewer than two variants cannot be built.
    /// </summary>
    internal sealed class TickMoveWithTurn : TickMove
    {
        public TickMoveWithTurn(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            // The source's constructor throws, ActionBuilder finds no usable constructor, and the behaviour cannot be built.
            if (def.Variants.Length < 2) throw new TickCantBeAliveException("action '" + def.Name + "' (MoveWithTurn) needs at least two variants");
        }

        protected override TickVariantDef CurrentVariant()
        {
            if (IsTurning) return Def.Variants[Def.Variants.Length - 1];
            for (int index = 0; index < Def.Variants.Length - 1; index++)
                if (VariantHolds(Def.Variants[index])) return Def.Variants[index];
            return null;
        }

        protected override bool HasTurningVariant() { return true; }
    }

    /// <summary>BreedMove (BreedMove.java): a Move that breeds after each step whose age is a multiple of BornInterval,
    /// except while it is turning.</summary>
    internal sealed class TickBreedMove : TickMove
    {
        private readonly TickBreedDelegate _breed;

        public TickBreedMove(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            _breed = new TickBreedDelegate(world, this);
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            _breed.ValidateBornCount();
            _breed.ValidateBornInterval();
        }

        protected override void Tick()
        {
            base.Tick();
            if (_breed.IsIntervalFrame() && !IsTurning && _breed.IsEnabled(Mascot))
                _breed.Breed(Mascot);
        }
    }

    /// <summary>BreedJump (BreedJump.java): a Jump that breeds after each step whose age is a multiple of BornInterval.</summary>
    internal sealed class TickBreedJump : TickJump
    {
        private readonly TickBreedDelegate _breed;

        public TickBreedJump(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            _breed = new TickBreedDelegate(world, this);
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            _breed.ValidateBornCount();
            _breed.ValidateBornInterval();
        }

        protected override void Tick()
        {
            base.Tick();
            if (_breed.IsIntervalFrame() && _breed.IsEnabled(Mascot))
                _breed.Breed(Mascot);
        }
    }

    /// <summary>BreedFall (BreedFall.java): a Fall that breeds after each step whose age is a multiple of BornInterval.</summary>
    internal sealed class TickBreedFall : TickFall
    {
        private readonly TickBreedDelegate _breed;

        public TickBreedFall(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming)
        {
            _breed = new TickBreedDelegate(world, this);
        }

        public override void Init(TickMascot mascot)
        {
            base.Init(mascot);
            _breed.ValidateBornCount();
            _breed.ValidateBornInterval();
        }

        protected override void Tick()
        {
            base.Tick();
            if (_breed.IsIntervalFrame() && _breed.IsEnabled(Mascot))
                _breed.Breed(Mascot);
        }
    }
}
