using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The motion family of embedded classes (src/dotNet/Shimeji/TickMotionFamily.cs, grimoire/06 section 5). Each group
    /// pins one source rule with numbers worked by hand from the source's arithmetic (Java int truncation, IEEE doubles),
    /// written beside the assertion that checks them. Companions are built in code on a scripted desktop.
    /// </summary>
    internal static partial class Program
    {
        // ---- a desktop whose cursor, screens and active window a test sets

        private sealed class MotionDesk : ITickEnvironment
        {
            private readonly TickArea _screen = new TickArea(), _work = new TickArea(), _window = new TickArea { Visible = false };
            private readonly List<TickArea> _screens = new List<TickArea>();
            private readonly int[] _screenRect, _workRect;
            private readonly int[][] _screenRects;
            public int[] Window;
            public TickPoint CursorAt = new TickPoint(100, 100);

            public MotionDesk() : this(new[] { 0, 0, 1280, 720 }, new[] { 0, 0, 1280, 680 }, new[] { 0, 0, 1280, 720 }) { }

            public MotionDesk(int[] screen, int[] work, params int[][] screens)
            {
                _screenRect = screen;
                _workRect = work;
                _screenRects = screens;
                foreach (int[] s in screens) _screens.Add(new TickArea());
            }

            public void Tick(int tick)
            {
                _screen.Set(_screenRect[0], _screenRect[1], _screenRect[2], _screenRect[3]);
                for (int i = 0; i < _screens.Count; i++) _screens[i].Set(_screenRects[i][0], _screenRects[i][1], _screenRects[i][2], _screenRects[i][3]);
                _work.Set(_workRect[0], _workRect[1], _workRect[2], _workRect[3]);
                if (Window != null)
                {
                    _window.Visible = true;
                    _window.Set(Window[0], Window[1], Window[2], Window[3]);
                }
            }

            public TickArea Screen { get { return _screen; } }
            public TickArea WorkArea { get { return _work; } }
            public IReadOnlyList<TickArea> Screens { get { return _screens; } }
            public TickArea ActiveWindow { get { return _window; } }
            public TickPoint Cursor { get { return CursorAt; } }
            public TickPoint CursorDelta { get { return new TickPoint(0, 0); } }
        }

        private static TickWorld MotionWorld(ITickEnvironment desk, TickLog log, TickTape tape, TickSwitches switches, params TickType[] types)
        {
            var world = new TickWorld(desk ?? new MotionDesk(), tape ?? new TickTape(), switches ?? new TickSwitches(), log ?? new TickLog());
            foreach (TickType t in types) world.AddType(t);
            world.Start();
            return world;
        }

        private static XmlData.VariantNode MVar(string when, bool turn, params XmlData.PoseNode[] poses)
        {
            return new XmlData.VariantNode { When = when, Turn = turn, TurnSpecified = turn, Pose = poses };
        }

        private static XmlData.AnimationNode MAct(int id, string name, string type, string cls, string border, XmlData.ParamNode[] ps, params XmlData.VariantNode[] variants)
        {
            return new XmlData.AnimationNode
            {
                Id = id,
                Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode
                {
                    Type = type, Class = cls, Border = border,
                    Params = ps ?? new XmlData.ParamNode[0],
                    Variants = new XmlData.VariantsNode { Variant = variants },
                },
            };
        }

        private static XmlData.AnimationNode MSeq(int id, string name, bool loop, params string[] children)
        {
            var refs = new List<XmlData.CompositeChildNode>();
            foreach (string c in children) refs.Add(new XmlData.CompositeChildNode { Animation = c });
            return new XmlData.AnimationNode
            {
                Id = id,
                Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode
                {
                    Type = "Sequence", Class = "", Border = "", Loop = loop, LoopSpecified = true,
                    Params = new XmlData.ParamNode[0],
                    Children = new XmlData.ChildrenNode { Mode = "sequence", Child = refs.ToArray() },
                },
            };
        }

        /// <summary>One subject behaviour (id 10, running act 30) beside the three a test falls back on: Stand (the only
        /// weighted route edge, so an ending subject selects it), Fall (lost ground) and Float (a borderless Stay births
        /// enter).</summary>
        private static TickType MotionType(string behaviour, XmlData.AnimationNode subject, params XmlData.AnimationNode[] extra)
        {
            var nodes = new List<XmlData.AnimationNode>
            {
                TRoute(TEdge(10, 0), TEdge(11, 1), TEdge(12, 0), TEdge(13, 0)),
                TBeh(10, behaviour, subject.Name, 0, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TBeh(12, "Fall", "Falling", 0, null),
                TBeh(13, "Float", "Float", 0, null),
                subject,
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1)),
                MAct(32, "Falling", "Embedded", "Fall", "", null, MVar(null, false, TPose(0, 1))),
                MAct(33, "Float", "Stay", null, "", new[] { TParam("Duration", "50") }, MVar(null, false, TPose(0, 1))),
            };
            nodes.AddRange(extra);
            return TickType.Compile("Alpha", TickDoc(nodes.ToArray()));
        }

        private static ScriptValue Local(TickMascot m, string name) { return m.Behaviour.Action.Leaf.Vars.Get(name, null); }

        private static int Count(List<string> list, string item) { return list.FindAll(s => s == item).Count; }

        // ---- Fall

        private static void TestTickMotionFallArithmetic()
        {
            // Fall.java:81-131 from (640, 300), InitialVX 8, InitialVY -6, RegistanceX .25, RegistanceY .5, Gravity 1.5
            // (all exact in binary). Each tick: vx -= vx*rx; vy = vy - vy*ry + g; mod += v % 1; d = (int)v + (int)mod;
            // mod %= 1; the anchor moves by d.
            //   1: vx 6,       vy -1.5,   modX 0,       modY -0.5,   d (6, -1)  -> (646, 299)
            //   2: vx 4.5,     vy 0.75,   modX 0.5,     modY 0.25,   d (4, 0)   -> (650, 299)
            //   3: vx 3.375,   vy 1.875,  modX 0.875,   modY 1.125, d (3, 1+1) -> (653, 301), modY 0.125
            //   4: vx 2.53125, vy 2.4375, modX 1.40625, modY 0.5625, d (2+1, 2) -> (656, 303), modX 0.40625
            TickType t = MotionType("Drop", MAct(30, "Drop", "Embedded", "Fall", "",
                new[] { TParam("InitialVX", "8"), TParam("InitialVY", "-6"), TParam("RegistanceX", "0.25"), TParam("RegistanceY", "0.5"), TParam("Gravity", "1.5") },
                MVar(null, false, TPose(0, 1))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 300), false, "Drop");
            var expected = new[] { new TickPoint(646, 299), new TickPoint(650, 299), new TickPoint(653, 301), new TickPoint(656, 303) };
            for (int i = 0; i < expected.Length; i++)
            {
                w.StepOnce();
                AssertEqual(expected[i], m.Anchor, "Fall step " + (i + 1) + " did not move by the source's damped velocity plus its carried fraction.");
                if (i == 0) AssertTrue(m.LookRight, "Fall did not face the way its horizontal velocity points.");
            }
            AssertEqual(2.53125, Local(m, "VelocityX").ToNumber(), "Fall did not publish VelocityX after damping.");
            AssertEqual(2.4375, Local(m, "VelocityY").ToNumber(), "Fall did not publish VelocityY after damping and gravity.");
            AssertEqual("Drop", m.Behaviour.Name, "Fall ended in mid-air.");
        }

        private static void TestTickMotionFallStops()
        {
            // A window's top at y 600 (x 400..900), the companion inside it at (640, 640) falling 2 px a tick: the first
            // step's sweep from y-80 up to y finds the floor at j = -40 (Fall.java:113-124) and lands there; the second
            // check sees the floor and the fall ends.
            TickType t = MotionType("Drop", MAct(30, "Drop", "Embedded", "Fall", "",
                new[] { TParam("InitialVY", "2"), TParam("RegistanceY", "0"), TParam("Gravity", "0") },
                MVar(null, false, TPose(0, 1))));
            var desk = new MotionDesk { Window = new[] { 400, 600, 500, 70 } };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 640), false, "Drop");
            w.StepOnce();
            AssertEqual(new TickPoint(640, 600), m.Anchor, "Fall did not catch a floor 40 px above its step (the 80 px sweep).");
            AssertEqual("Stand", m.Behaviour.Name, "Fall did not end once its anchor was on a floor.");

            // From (5, 300) at vx -10 (no damping): dev 10, and step 5 of the sweep reaches x 0, the work area's left
            // wall, which is one screen edge, so it stops there; facing turns left with the velocity.
            TickType u = MotionType("Drop", MAct(30, "Drop", "Embedded", "Fall", "",
                new[] { TParam("InitialVX", "-10"), TParam("RegistanceX", "0"), TParam("RegistanceY", "0"), TParam("Gravity", "0") },
                MVar(null, false, TPose(0, 1))));
            TickWorld v = MotionWorld(new TickDesk(), null, null, null, u);
            TickMascot n = v.CreateAtStart("m1", u, new TickPoint(5, 300), true, "Drop");
            v.StepOnce();
            AssertEqual(new TickPoint(0, 300), n.Anchor, "Fall did not stop its sweep at the wall.");
            AssertFalse(n.LookRight, "Fall did not face left with a negative horizontal velocity.");
            AssertEqual("Stand", n.Behaviour.Name, "Fall did not end against the wall.");
        }

        private static void TestTickMotionFallSeparator()
        {
            // Two stacked screens meet at y 720, which is also the work area's bottom. Falling 20 px a tick from
            // (640, 710), the sweep passes (640, 720), where the separator-ignoring floor lookup counts two screen
            // edges and refuses it (Environment.isScreenTopBottom), so the companion falls on to (640, 730).
            TickType t = MotionType("Drop", MAct(30, "Drop", "Embedded", "Fall", "",
                new[] { TParam("InitialVY", "20"), TParam("RegistanceY", "0"), TParam("Gravity", "0") },
                MVar(null, false, TPose(0, 1))));
            var desk = new MotionDesk(new[] { 0, 0, 1280, 1440 }, new[] { 0, 0, 1280, 720 }, new[] { 0, 0, 1280, 720 }, new[] { 0, 720, 1280, 720 });
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 710), false, "Drop");
            w.StepOnce();
            AssertEqual(new TickPoint(640, 730), m.Anchor, "Fall landed where two stacked screens meet.");
            AssertEqual("Drop", m.Behaviour.Name, "Fall ended on a screen separator.");
        }

        private static void TestTickMotionFallReinit()
        {
            // A looping Sequence re-initialises one Fall instance every tick (Duration 1). InitialVX 1 and RegistanceX .5
            // make vx 0.5 each tick; init resets vx to 1 but NOT the remainder modX, so it reaches 1 every second tick:
            // x 640 (modX .5), 641 (modX 1 -> 0), 641 (.5), 642.
            TickType t = MotionType("Drift", MSeq(30, "Drift", true, "Glide"),
                MAct(34, "Glide", "Embedded", "Fall", "",
                    new[] { TParam("InitialVX", "1"), TParam("RegistanceX", "0.5"), TParam("RegistanceY", "0"), TParam("Gravity", "0"), TParam("Duration", "1") },
                    MVar(null, false, TPose(0, 1))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 300), false, "Drift");
            var xs = new List<int>();
            for (int i = 0; i < 4; i++) { w.StepOnce(); xs.Add(m.Anchor.X); }
            AssertEqual("640,641,641,642", string.Join(",", xs),
                "A re-initialised Fall did not keep its fractional remainder and restart its velocity.");
        }

        // ---- Jump

        private static void TestTickMotionJumpAim()
        {
            // Jump.java:60-91 from (100, 600) to (160, 600), VelocityParam 20, Arc on: dY = 600 - y - |dX|/2.
            //   1: dX 60, dY -30,   dist sqrt(4500)    67.08 -> v (17.89, -8.94) -> (117, 592)
            //   2: dX 43, dY -13.5, dist sqrt(2031.25) 45.07 -> v (19.08, -5.99) -> (136, 587)   (-5.99 truncates to -5)
            //   3: dX 24, dY 1,     dist sqrt(577)     24.02 -> v (19.98, 0.83)  -> (155, 587)
            //   4: dX 5,  dY 10.5,  dist 11.63 <= 20 -> lands on (160, 600); the second check sees dist 0 and ends it.
            TickType t = MotionType("Leap", MAct(30, "Leap", "Embedded", "Jump", "",
                new[] { TParam("TargetX", "160"), TParam("TargetY", "600"), TParam("VelocityParam", "20") },
                MVar(null, false, TPose(0, 1))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(100, 600), false, "Leap");
            var expected = new[] { new TickPoint(117, 592), new TickPoint(136, 587), new TickPoint(155, 587), new TickPoint(160, 600) };
            for (int i = 0; i < expected.Length; i++)
            {
                w.StepOnce();
                AssertEqual(expected[i], m.Anchor, "Jump step " + (i + 1) + " did not re-aim with the arc and truncate its step toward zero.");
                if (i == 0)
                {
                    AssertTrue(m.LookRight, "Jump did not face its target.");
                    AssertEqual(17.88854381999832, Local(m, "VelocityX").ToNumber(), "Jump did not publish the untruncated VelocityX.");
                    AssertEqual(-8.94427190999916, Local(m, "VelocityY").ToNumber(), "Jump did not publish the untruncated VelocityY.");
                }
            }
            AssertEqual("Stand", m.Behaviour.Name, "Jump did not end on reaching its target.");

            // Arc off: dY 0, so 20 px a step: (120, 600), (140, 600), then within 20 it lands on (160, 600).
            TickType u = MotionType("Leap", MAct(30, "Leap", "Embedded", "Jump", "",
                new[] { TParam("TargetX", "160"), TParam("TargetY", "600"), TParam("Arc", "false") },
                MVar(null, false, TPose(0, 1))));
            TickWorld v = MotionWorld(new TickDesk(), null, null, null, u);
            TickMascot n = v.CreateAtStart("m1", u, new TickPoint(100, 600), false, "Leap");
            var xs = new List<string>();
            for (int i = 0; i < 3; i++) { v.StepOnce(); xs.Add(n.Anchor.ToString()); }
            AssertEqual("(120, 600)|(140, 600)|(160, 600)", string.Join("|", xs), "Jump with Arc false did not aim straight at its target.");
        }

        private static void TestTickMotionJumpPose()
        {
            // The same jump with two variants: frame 2 while VelocityY < 0, else frames 0 then 1. The velocity is
            // published before the pose (Jump.java:84-89), so ticks 1 and 2 show frame 2 and tick 3 (VelocityY 0.83, age
            // 2) frame 0. Tick 4 lands with no pose, so frame 0 stays (a pose at age 3 would show frame 1).
            TickType t = MotionType("Leap", MAct(30, "Leap", "Embedded", "Jump", "",
                new[] { TParam("TargetX", "160"), TParam("TargetY", "600") },
                MVar("VelocityY < 0", false, TPose(2, 1)),
                MVar(null, false, TPose(0, 1), TPose(1, 1))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(100, 600), false, "Leap");
            var frames = new List<int>();
            for (int i = 0; i < 4; i++) { w.StepOnce(); frames.Add(m.Pose.Frame); }
            AssertEqual("2,2,0,0", string.Join(",", frames),
                "Jump did not publish its velocity before choosing the pose, or played a pose on the tick it landed.");
        }

        // ---- Dragged

        private static TickType DraggedType(string offsetType)
        {
            var ps = new List<XmlData.ParamNode> { TParam("OffsetX", "10") };
            if (offsetType != null) ps.Add(TParam("OffsetType", offsetType));
            return MotionType("Dragged", MAct(30, "Pinched", "Embedded", "Dragged", "", ps.ToArray(), MVar(null, false, TPose(0, 1))));
        }

        private static void TestTickMotionDraggedFollows()
        {
            // Dragged.java:52-106, OffsetX 10 and the default OffsetY 120, scaling 1. Init: FootX = cursor.x + 10 = 510.
            // Each tick FootDX = (FootDX + (cursor.x - FootX) * .1) * .8, FootX += FootDX; the anchor is cursor + (10, 120).
            //   1: cursor (500, 400): FootDX -0.8,               FootX 509.2     -> (510, 520)
            //   2: cursor (530, 400): FootDX 1.0240000000000011, FootX 510.224   -> (540, 520)
            //   3: cursor (534, 400): FootDX 2.721280000000002,  FootX 512.94528 -> (544, 520)
            var desk = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            TickType t = DraggedType(null);
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), true, "Dragged");
            w.StepOnce();
            AssertEqual(new TickPoint(510, 520), m.Anchor, "Dragged did not hold the companion at the cursor plus its offset.");
            AssertFalse(m.LookRight, "Dragged did not face left.");
            AssertTrue(m.Dragging, "Dragged did not mark the companion as dragged.");
            AssertEqual(-0.8, Local(m, "FootDX").ToNumber(), "Dragged's first foot sway is not the source's.");
            desk.CursorAt = new TickPoint(530, 400);
            w.StepOnce();
            AssertEqual(new TickPoint(540, 520), m.Anchor, "Dragged did not follow the cursor.");
            desk.CursorAt = new TickPoint(534, 400);
            w.StepOnce();
            AssertEqual(new TickPoint(544, 520), m.Anchor, "Dragged did not follow the cursor's small move.");
            AssertEqual(2.721280000000002, Local(m, "FootDX").ToNumber(), "Dragged's FootDX is not damped as the source damps it.");
            AssertEqual(512.94528, Local(m, "FootX").ToNumber(), "Dragged's FootX is not the source's.");
        }

        private static void TestTickMotionDraggedRegist()
        {
            // The same drag: tick 2's 30 px move restarts the clock (|530 - 510 + 10| >= 5); tick 3's 4 px move does not.
            // So age = step - 2 from step 2, and the regist time (250) is reached at age 249, step 251, which draws once
            // (builtin:dragged-regist): .5 >= .1 extends it to 251. Step 252 (age 250) draws .05, no extension. Step
            // 253's first check (251 < 251) fails, and the behaviour ends there.
            var desk = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            var tape = new TickTape();
            tape.Values.Enqueue(0.5);
            tape.Values.Enqueue(0.05);
            var log = new TickLog();
            TickType t = DraggedType(null);
            TickWorld w = MotionWorld(desk, log, tape, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), false, "Dragged");
            w.StepOnce();
            desk.CursorAt = new TickPoint(530, 400);
            w.StepOnce();
            desk.CursorAt = new TickPoint(534, 400);
            int firstDraw = -1, secondDraw = -1, ended = -1;
            for (int step = 3; step <= 260 && ended < 0; step++)
            {
                w.StepOnce();
                int draws = Count(log.Draws, "builtin:dragged-regist");
                if (draws >= 1 && firstDraw < 0) firstDraw = step;
                if (draws >= 2 && secondDraw < 0) secondDraw = step;
                if (m.Behaviour.Name != "Dragged") ended = step;
            }
            AssertEqual(251, firstDraw, "Dragged did not reach its regist time 249 ticks after the last 5 px move (or a 4 px move restarted it).");
            AssertEqual(252, secondDraw, "A drawn .5 did not extend the regist time by one tick.");
            AssertEqual(253, ended, "Dragged did not end once a draw below .1 left the regist time where it was.");
            AssertEqual(2, Count(log.Draws, "builtin:dragged-regist"), "Dragged drew at its regist site on a tick that was not the regist time.");
        }

        private static void TestTickMotionDraggedOrigin()
        {
            // A one-tick borderless Stay shows frame 1 (left-facing, centre (32, 64)), then Dragged with OffsetType Origin:
            // the offset is (-10 + 32, -120 + 64) = (22, -56), so the cursor at (500, 400) holds the anchor at (522, 344).
            TickType t = MotionType("Lift", MSeq(30, "Lift", false, "Settle", "Pinch"),
                MAct(34, "Settle", "Stay", null, "", new[] { TParam("Duration", "1") }, MVar(null, false, TPose(1, 1))),
                MAct(35, "Pinch", "Embedded", "Dragged", "", new[] { TParam("OffsetX", "10"), TParam("OffsetType", "Origin") }, MVar(null, false, TPose(0, 1))));
            var desk = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), false, "Lift");
            w.StepOnce();
            w.StepOnce();
            AssertEqual(new TickPoint(522, 344), m.Anchor, "Dragged with OffsetType Origin did not measure its offset from the image centre.");
        }

        private static void TestTickMotionDraggedWorkArea()
        {
            // Two side-by-side screens, single-screen mode, the environment's work area on the left one. Dragged looks the
            // work area up again each tick with the settings ignored (Dragged.java:74, MascotEnvironment.refreshWorkArea),
            // so once the anchor is on the right screen (after step 2) step 3 adopts it, and the variant that reads
            // workArea.left > 0 plays: frames 0, 0, 2. Without the refresh, single-screen mode keeps the first area.
            TickType t = MotionType("Dragged", MAct(30, "Pinched", "Embedded", "Dragged", "", new[] { TParam("OffsetX", "10") },
                MVar("mascot.environment.workArea.left > 0", false, TPose(2, 1)), MVar(null, false, TPose(0, 1))));
            var desk = new MotionDesk(new[] { 0, 0, 2560, 720 }, new[] { 0, 0, 1280, 680 }, new[] { 0, 0, 1280, 720 }, new[] { 1280, 0, 1280, 720 })
            {
                CursorAt = new TickPoint(500, 400),
            };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), false, "Dragged");
            var frames = new List<int>();
            w.StepOnce();
            frames.Add(m.Pose.Frame);
            desk.CursorAt = new TickPoint(1900, 400);
            w.StepOnce();
            frames.Add(m.Pose.Frame);
            w.StepOnce();
            frames.Add(m.Pose.Frame);
            AssertEqual("0,0,2", string.Join(",", frames), "Dragged did not refresh the companion's work area as it was carried to another screen.");
        }

        // ---- Regist

        private static TickType RegistType()
        {
            return MotionType("Resist", MAct(30, "Resist", "Embedded", "Regist", "", new[] { TParam("OffsetX", "10") },
                MVar(null, false, TPose(0, 2), TPose(1, 2))));
        }

        private static void TestTickMotionRegist()
        {
            // Regist.java:45-74, a four-tick animation, OffsetX 10, the cursor at (500, 400) and the anchor at (510, 520):
            // |500 - 510 + 10| = 0 < 5, so it runs. Ticks 1-3 play frames 0, 0, 1 with the companion marked dragged. Tick 4
            // (age 3, 3 + 1 >= 4) draws its facing (builtin:regist-facing, .3 < .5 faces right) and loses the ground.
            var desk = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            var tape = new TickTape();
            tape.Values.Enqueue(0.3);
            var log = new TickLog();
            TickType t = RegistType();
            TickWorld w = MotionWorld(desk, log, tape, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(510, 520), false, "Resist");
            var frames = new List<int>();
            for (int i = 0; i < 3; i++) { w.StepOnce(); frames.Add(m.Pose.Frame); }
            AssertEqual("0,0,1", string.Join(",", frames), "Regist did not play its animation.");
            AssertTrue(m.Dragging && m.Behaviour.Name == "Resist" && log.Draws.Count == 0, "Regist ended, drew or let go before its last tick.");
            w.StepOnce();
            AssertTrue(log.Events.Contains("m1 behaviour Resist>Fall forced"), "Regist did not lose the ground on its last tick: " + string.Join("; ", log.Events));
            AssertEqual("builtin:regist-facing", string.Join("|", log.Draws), "Regist did not draw its facing once, at its own site.");
            AssertTrue(m.LookRight, "A facing draw of .3 did not face right.");
            AssertFalse(m.Dragging, "Losing the ground did not end the drag.");

            var tape2 = new TickTape();
            tape2.Values.Enqueue(0.7);
            TickWorld w2 = MotionWorld(new MotionDesk { CursorAt = new TickPoint(500, 400) }, null, tape2, null, t);
            TickMascot m2 = w2.CreateAtStart("m1", t, new TickPoint(510, 520), true, "Resist");
            for (int i = 0; i < 4; i++) w2.StepOnce();
            AssertFalse(m2.LookRight, "A facing draw of .7 did not face left.");

            // The cursor 5 px off (|505 - 510 + 10| = 5) fails the check at step 2: the behaviour ends with no draw.
            // At 4 px off it goes on.
            var desk3 = new MotionDesk { CursorAt = new TickPoint(500, 400) };
            var log3 = new TickLog();
            TickWorld w3 = MotionWorld(desk3, log3, null, null, t);
            TickMascot m3 = w3.CreateAtStart("m1", t, new TickPoint(510, 520), false, "Resist");
            w3.StepOnce();
            desk3.CursorAt = new TickPoint(504, 400);
            w3.StepOnce();
            AssertEqual("Resist", m3.Behaviour.Name, "Regist ended with the cursor 4 px away.");
            desk3.CursorAt = new TickPoint(505, 400);
            w3.StepOnce();
            AssertEqual("Stand", m3.Behaviour.Name, "Regist did not end with the cursor 5 px away.");
            AssertEqual(0, Count(log3.Draws, "builtin:regist-facing"), "Regist drew a facing although the cursor ended it.");
        }

        // ---- Turn

        private static void TestTickMotionTurn()
        {
            // Turn.java, LookRight true, a three-tick animation (frame 2 for 2 ticks, frame 3 for 1), on the floor.
            // Facing left, it must turn: frames 2, 2, 3 facing right, then it ends at step 4.
            TickType t = MotionType("Spin", MAct(30, "Spin", "Embedded", "Turn", "Floor", new[] { TParam("LookRight", "true") },
                MVar(null, false, TPose(2, 2), TPose(3, 1))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Spin");
            var frames = new List<int>();
            for (int i = 0; i < 3; i++) { w.StepOnce(); frames.Add(m.Pose.Frame); }
            AssertEqual("2,2,3", string.Join(",", frames), "Turn did not play its animation once.");
            AssertTrue(m.LookRight, "Turn did not face LookRight.");
            w.StepOnce();
            AssertEqual("Stand", m.Behaviour.Name, "Turn did not end with its animation.");

            // Already facing right: nothing to turn, so the first check fails and the next behaviour starts at once.
            TickWorld v = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot n = v.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Spin");
            AssertEqual("Stand", n.Behaviour.Name, "Turn played although the facing did not change.");

            // Off the floor: it faces first (Turn.java:31), then loses the ground.
            TickWorld u = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot o = u.CreateAtStart("m1", t, new TickPoint(640, 600), false, "Spin");
            u.StepOnce();
            AssertTrue(o.Behaviour.Name == "Fall" && o.LookRight, "Turn off its border did not face, then lose the ground.");
        }

        private static void TestTickMotionTurnFlips()
        {
            // No LookRight: its default is the opposite of the facing NOW, read every tick, so the facing flips each tick.
            TickType t = MotionType("Spin", MAct(30, "Spin", "Embedded", "Turn", "Floor", null, MVar(null, false, TPose(2, 3))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Spin");
            var facing = new List<string>();
            for (int i = 0; i < 3; i++) { w.StepOnce(); facing.Add(m.LookRight ? "R" : "L"); }
            AssertEqual("R,L,R", string.Join(",", facing), "Turn without LookRight did not take the opposite of the current facing each tick.");
        }

        private static void TestTickMotionTurnReinit()
        {
            // A looping Sequence of one Turn (LookRight true, two ticks). The turning flag is never cleared, so after the
            // first pass (facing now right) the re-initialised Turn plays again: frames 2, 3, 2, 3, 2, still in Spin.
            TickType t = MotionType("Spin", MSeq(30, "Spin", true, "Twirl"),
                MAct(34, "Twirl", "Embedded", "Turn", "Floor", new[] { TParam("LookRight", "true") }, MVar(null, false, TPose(2, 1), TPose(3, 1))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Spin");
            var frames = new List<int>();
            for (int i = 0; i < 5; i++) { w.StepOnce(); frames.Add(m.Pose.Frame); }
            AssertEqual("2,3,2,3,2", string.Join(",", frames), "A Turn that had turned did not play again when its instance was re-initialised.");
            AssertEqual("Spin", m.Behaviour.Name, "The looping Turn ended.");
        }

        // ---- MoveWithTurn and the Broadcast aliases

        private static void TestTickMotionMoveWithTurn()
        {
            // MoveWithTurn.java: the LAST variant is the turn although its turn flag is false. From (640, 680) facing left
            // to TargetX 700: the direction changes, so ticks 1-2 play the turn (frame 3, two ticks, no motion); at age
            // 2 the turn is over and the walk (frame 0, dx -2, mirrored facing right) moves 2 px a tick.
            TickType t = MotionType("Walk", MAct(30, "Walk", "Embedded", "MoveWithTurn", "Floor", new[] { TParam("TargetX", "700") },
                MVar(null, false, TPose(0, 1, -2)), MVar(null, false, TPose(3, 2))));
            TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Walk");
            var seen = new List<string>();
            for (int i = 0; i < 4; i++) { w.StepOnce(); seen.Add(m.Anchor.X + "/" + m.Pose.Frame); }
            AssertEqual("640/3,640/3,642/0,644/0", string.Join(",", seen), "MoveWithTurn did not play its last variant as the turn, then walk.");
            AssertTrue(m.LookRight, "MoveWithTurn did not face its target.");

            // One variant cannot be a MoveWithTurn: the behaviour cannot be built.
            TickType u = MotionType("Walk", MAct(30, "Walk", "Embedded", "MoveWithTurn", "Floor", new[] { TParam("TargetX", "700") },
                MVar(null, false, TPose(0, 1, -2))));
            TickWorld v = MotionWorld(new TickDesk(), null, null, null, u);
            AssertThrows<TickCantBeAliveException>(() => v.CreateAtStart("m1", u, new TickPoint(640, 680), false, "Walk"),
                "A MoveWithTurn with one variant was built.");
        }

        private static void TestTickMotionBroadcast()
        {
            // The deprecated classes are their bases and nothing more (Broadcast*.java).
            string[] classes = { "Broadcast", "BroadcastStay", "BroadcastMove", "BroadcastJump" };
            Type[] bases = { typeof(TickAnimate), typeof(TickStay), typeof(TickMove), typeof(TickJump) };
            for (int i = 0; i < classes.Length; i++)
            {
                TickType t = MotionType("Shout", MAct(30, "Shout", "Embedded", classes[i], "Floor",
                    new[] { TParam("Affordance", "hug"), TParam("TargetX", "700") }, MVar(null, false, TPose(0, 1))));
                TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
                TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Shout");
                AssertEqual(bases[i], m.Behaviour.Action.GetType(), classes[i] + " does not run as its base class.");
                w.StepOnce();
                AssertTrue(m.Affordances.Contains("hug"), classes[i] + " did not offer its Affordance.");
            }
        }

        // ---- Breed variants

        private static XmlData.ParamNode[] BirthParams(string interval, int bornX, params XmlData.ParamNode[] more)
        {
            var ps = new List<XmlData.ParamNode>
            {
                TParam("BornX", bornX.ToString(System.Globalization.CultureInfo.InvariantCulture)), TParam("BornMascot", "Alpha"), TParam("BornBehavior", "Float"),
            };
            if (interval != null) ps.Add(TParam("BornInterval", interval));
            ps.AddRange(more);
            return ps.ToArray();
        }

        private static void TestTickMotionBreedMove()
        {
            // BreedMove.java: from (640, 680) facing left to TargetX 600 at 4 px a tick, BornInterval 2, BornX 10. It
            // breeds after the step at ages 0, 2 and 4, at the new anchor + 10 (facing left): (646), (638), (630).
            TickType t = MotionType("Walk", MAct(30, "Walk", "Embedded", "BreedMove", "Floor", BirthParams("2", 10, TParam("TargetX", "600")),
                MVar(null, false, TPose(0, 1, -4))));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Walk");
            for (int i = 0; i < 5; i++) w.StepOnce();
            AssertEqual("m1 birth m1.b1 #1 try1 at (646, 680)|m1 birth m1.b2 #2 try2 at (638, 680)|m1 birth m1.b3 #3 try3 at (630, 680)",
                string.Join("|", log.Events.FindAll(e => e.StartsWith("m1 birth", StringComparison.Ordinal))),
                "BreedMove did not breed after each BornInterval step at its new anchor.");

            // With a turn variant and the target behind it, ages 0 and 1 turn (no birth although age 0 is an interval
            // frame); at age 2 the turn is over, it walks 4 px right to 644 and breeds at 644 - 10 = 634.
            TickType u = MotionType("Walk", MAct(30, "Walk", "Embedded", "BreedMove", "Floor", BirthParams("2", 10, TParam("TargetX", "700")),
                MVar(null, false, TPose(0, 1, -4)), MVar(null, true, TPose(3, 2))));
            var log2 = new TickLog();
            TickWorld v = MotionWorld(new TickDesk(), log2, null, null, u);
            v.CreateAtStart("m1", u, new TickPoint(640, 680), false, "Walk");
            for (int i = 0; i < 3; i++) v.StepOnce();
            AssertEqual("m1 birth m1.b1 #1 try1 at (634, 680)",
                string.Join("|", log2.Events.FindAll(e => e.StartsWith("m1 birth", StringComparison.Ordinal))),
                "BreedMove bred while it was turning.");
        }

        private static void TestTickMotionBreedJump()
        {
            // BreedJump.java: the straight jump (Arc false) from (100, 600) to (160, 600) lands at 120, 140, 160. With
            // BornInterval 2 it breeds at ages 0 and 2, at the new anchor - 5 (facing right): (115, 600), (155, 600).
            TickType t = MotionType("Leap", MAct(30, "Leap", "Embedded", "BreedJump", "",
                BirthParams("2", 5, TParam("TargetX", "160"), TParam("TargetY", "600"), TParam("Arc", "false")),
                MVar(null, false, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            w.CreateAtStart("m1", t, new TickPoint(100, 600), false, "Leap");
            for (int i = 0; i < 3; i++) w.StepOnce();
            AssertEqual("m1 birth m1.b1 #1 try1 at (115, 600)|m1 birth m1.b2 #2 try2 at (155, 600)",
                string.Join("|", log.Events.FindAll(e => e.StartsWith("m1 birth", StringComparison.Ordinal))),
                "BreedJump did not breed at its new anchor every BornInterval ticks.");

            // At a cap of 2 (default interval 1): the first birth fits; the second, with it active, is refused.
            TickType u = MotionType("Leap", MAct(30, "Leap", "Embedded", "BreedJump", "",
                BirthParams(null, 5, TParam("TargetX", "160"), TParam("TargetY", "600"), TParam("Arc", "false")),
                MVar(null, false, TPose(0, 1))));
            var capped = new TickLog();
            TickWorld c = MotionWorld(new TickDesk(), capped, null, null, u);
            c.MaximumCompanions = 2;
            c.CreateAtStart("m1", u, new TickPoint(100, 600), false, "Leap");
            c.StepOnce();
            c.StepOnce();
            AssertTrue(capped.Events.Contains("m1 birth m1.b1 #1 try1 at (115, 600)") && capped.Events.Contains("m1 refused try2 cap"),
                "BreedJump's births did not go through Breed's capped birth loop: " + string.Join("; ", capped.Events));
        }

        private static void TestTickMotionBreedFall()
        {
            // BreedFall.java: from (640, 300), Gravity 2 and no resistance, vy 2, 4, 6: y 302, 306, 312. BornInterval 2
            // breeds at ages 0 and 2, at the new anchor + 10 (facing left): (650, 302), (650, 312).
            TickType t = MotionType("Drop", MAct(30, "Drop", "Embedded", "BreedFall", "",
                BirthParams("2", 10, TParam("RegistanceY", "0"), TParam("Gravity", "2")), MVar(null, false, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = MotionWorld(new TickDesk(), log, null, null, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 300), false, "Drop");
            for (int i = 0; i < 3; i++) w.StepOnce();
            AssertEqual("m1 birth m1.b1 #1 try1 at (650, 302)|m1 birth m1.b2 #2 try2 at (650, 312)",
                string.Join("|", log.Events.FindAll(e => e.StartsWith("m1 birth", StringComparison.Ordinal))),
                "BreedFall did not breed as it fell, every BornInterval ticks.");

            // The Breeding toggle off for this image set: no birth at all.
            var off = new TickSwitches();
            off.Off["Alpha:Breeding"] = false;
            var log2 = new TickLog();
            TickWorld v = MotionWorld(new TickDesk(), log2, null, off, t);
            v.CreateAtStart("m1", t, new TickPoint(640, 300), false, "Drop");
            for (int i = 0; i < 3; i++) v.StepOnce();
            AssertFalse(log2.Events.Exists(e => e.Contains(" birth ")), "BreedFall bred with Breeding turned off.");
        }

        private static void TestTickMotionBreedValidation()
        {
            // Each variant validates BornCount and then BornInterval at init (Breed.java:116-126): below 1 the companion
            // cannot start.
            string[] classes = { "BreedMove", "BreedJump", "BreedFall" };
            foreach (string cls in classes)
            {
                foreach (XmlData.ParamNode bad in new[] { TParam("BornInterval", "0"), TParam("BornCount", "0") })
                {
                    TickType t = MotionType("Go", MAct(30, "Go", "Embedded", cls, "Floor",
                        new[] { bad, TParam("TargetX", "700"), TParam("TargetY", "600") }, MVar(null, false, TPose(0, 1))));
                    TickWorld w = MotionWorld(new TickDesk(), null, null, null, t);
                    AssertThrows<TickCantBeAliveException>(() => w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Go"),
                        cls + " started with " + bad.Name + " " + bad.Value + ".");
                }
            }
        }
    }
}
