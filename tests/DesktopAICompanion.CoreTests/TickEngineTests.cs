using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The tick-clock engine (src/dotNet/Shimeji, grimoire/06 sections 4.3 and 4.5): each group pins one source rule the
    /// parity comparison depends on, with companions built in code, a scripted desktop and a recording observer, so a
    /// draw, an evaluation or an event that should not happen shows up, not only a wrong position.
    /// </summary>
    internal static partial class Program
    {
        // ---- fakes

        private sealed class TickDesk : ITickEnvironment
        {
            private readonly TickArea _screen = new TickArea(), _work = new TickArea(), _window = new TickArea { Visible = false };
            private readonly List<TickArea> _screens = new List<TickArea>();
            public TickDesk() { _screens.Add(_screen); }

            /// <summary>Pointer events to deliver, by the tick they arrive at.</summary>
            public readonly Dictionary<int, List<TickInput>> Script = new Dictionary<int, List<TickInput>>();
            private List<TickInput> _now = new List<TickInput>();
            public IReadOnlyList<TickInput> Inputs { get { return _now; } }

            public void Tick(int tick)
            {
                _screen.Set(0, 0, 1280, 720);
                _work.Set(0, 0, 1280, 680);
                if (!Script.TryGetValue(tick, out _now)) _now = new List<TickInput>();
            }
            public TickArea Screen { get { return _screen; } }
            public TickArea WorkArea { get { return _work; } }
            public IReadOnlyList<TickArea> Screens { get { return _screens; } }
            public TickArea ActiveWindow { get { return _window; } }
            public TickPoint Cursor { get { return new TickPoint(100, 100); } }
            public TickPoint CursorDelta { get { return new TickPoint(0, 0); } }

            // This desk has no window, so nothing is ever moved or minimised.
            public bool MoveActiveWindow(TickPoint topLeft, out TickRect from, out TickRect to)
            {
                from = to = default(TickRect);
                return false;
            }
            public bool MinimizeActiveWindow() { return false; }
        }

        private sealed class TickTape : ITickRandom
        {
            public readonly Queue<double> Values = new Queue<double>();
            public double Next() { return Values.Count > 0 ? Values.Dequeue() : 0.5; }
        }

        private sealed class TickSwitches : ITickSettings
        {
            public readonly Dictionary<string, bool> Off = new Dictionary<string, bool>(StringComparer.Ordinal);
            /// <summary>The source's Multiscreen setting, read with no image set; off unless a test turns it on.</summary>
            public bool Multiscreen;
            public bool Get(string setting, string imageSet)
            {
                bool v;
                if (imageSet != null && Off.TryGetValue(imageSet + ":" + setting, out v)) return v;
                return setting == "Multiscreen" ? Multiscreen : true;
            }
            /// <summary>Behaviours the user turned off, by name; every other behaviour is on.</summary>
            public readonly HashSet<string> BehavioursOff = new HashSet<string>(StringComparer.Ordinal);
            public bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet) { return !BehavioursOff.Contains(behaviour.Name); }
        }

        private sealed class TickLog : ITickObserver
        {
            public readonly List<string> Events = new List<string>();
            public readonly List<string> Draws = new List<string>();
            public readonly List<string> Faults = new List<string>();
            public void BehaviourEntered(TickMascot m, string from, string to, string cause) { Events.Add(m.Sid + " behaviour " + (from ?? "-") + ">" + to + " " + cause); }
            public void BirthRequested(TickMascot p, TickMascot c, int ordinal, int attempt) { Events.Add(p.Sid + " birth " + c.Sid + " #" + ordinal + " try" + attempt + " at " + c.Anchor); }
            public void BirthRefused(TickMascot p, int attempt, string reason) { Events.Add(p.Sid + " refused try" + attempt + " " + reason); Order.Add("refused " + reason); }
            public void BirthActivated(TickMascot c) { Events.Add(c.Sid + " activated"); }
            public void Transformed(TickMascot m, string from, string to, string b) { Events.Add(m.Sid + " transform " + from + ">" + to + " " + b); }
            public void DisposeRequested(TickMascot m, string reason) { Events.Add(m.Sid + " dispose-request " + reason); }
            public void Disposed(TickMascot m) { Events.Add(m.Sid + " disposed"); }
            public void RandomDrawn(TickMascot m, string site, double value, string purpose) { Draws.Add(site); }
            public void Fault(TickMascot m, string message) { Faults.Add(m.Sid + ": " + message); Order.Add("fault"); }
            /// <summary>Faults and birth refusals in the order they were reported (the two go to separate lists above).</summary>
            public readonly List<string> Order = new List<string>();
            public void Muted(TickMascot m, string clip) { Events.Add(m.Sid + " mute " + (clip ?? "-")); }
            public void Sound(TickMascot m, string clip, string op) { Events.Add(m.Sid + " sound " + clip + " " + op); }
            public void ScanTargeted(TickMascot m, string affordance, TickMascot target) { Events.Add(m.Sid + " scan-target " + affordance + " " + (target == null ? "-" : target.Sid)); }
            public void Interacted(TickMascot m, IReadOnlyList<TickMascot> with)
            {
                var sids = new List<string>();
                foreach (TickMascot o in with) sids.Add(o.Sid);
                Events.Add(m.Sid + " interact " + string.Join(",", sids));
            }
            public void WindowMoved(TickMascot m, TickRect from, TickRect to) { Events.Add(m.Sid + " window-move " + Ltrb(from) + ">" + Ltrb(to)); }
            public void WindowMinimized(TickMascot m) { Events.Add(m.Sid + " window-minimize"); }
            public void Dragged(TickMascot m, string op) { Events.Add(m.Sid + " drag " + op); }
            public void Hotspot(TickMascot m, string op, string behaviour) { Events.Add(m.Sid + " hotspot " + op + " " + (behaviour ?? "-")); }

            /// <summary>A rectangle as the contract writes one: [left,top,right,bottom].</summary>
            public static string Ltrb(TickRect r) { return "[" + r.X + "," + r.Y + "," + (r.X + r.Width) + "," + (r.Y + r.Height) + "]"; }
        }

        // ---- building companions in code (validated documents compile the same way)

        private static string TickSheetPng()
        {
            // Only the PNG signature and IHDR size fields are read: a 256x64 sheet of four 64x64 cells.
            byte[] head =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
                0, 0, 1, 0, 0, 0, 0, 64, 8, 6, 0, 0, 0,
            };
            return Convert.ToBase64String(head);
        }

        private static XmlData.RootNode TickDoc(params XmlData.AnimationNode[] nodes)
        {
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Title = "t", Features = "tick actions behaviours expressions lifecycle", Clock = "tick" },
                Image = new XmlData.ImageNode { TilesX = 4, TilesY = 1, Png = TickSheetPng(), AnchorX = 32, AnchorXSpecified = true, AnchorY = 64, AnchorYSpecified = true },
                Animations = new XmlData.AnimationsNode { Animation = nodes },
            };
        }

        private static XmlData.PoseNode TPose(int frame, int ticks, int dx = 0)
        {
            return new XmlData.PoseNode { Frame = frame, Ticks = ticks, DX = dx };
        }

        private static XmlData.AnimationNode TAct(int id, string name, string type, string cls, XmlData.ParamNode[] ps, params XmlData.PoseNode[] poses)
        {
            return new XmlData.AnimationNode
            {
                Id = id,
                Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode
                {
                    Type = type,
                    Class = cls,
                    Border = "Floor",
                    Params = ps ?? new XmlData.ParamNode[0],
                    Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = poses } } },
                },
            };
        }

        private static XmlData.ParamNode TParam(string name, string value, string cache = "none")
        {
            return new XmlData.ParamNode { Name = name, Value = value, Cache = cache };
        }

        private static XmlData.AnimationNode TBeh(int id, string name, string runs, int frequency, bool? add, params XmlData.NextNode[] next)
        {
            var b = new XmlData.BehaviourNode { Name = name, Runs = runs, Frequency = frequency };
            if (add.HasValue) { b.Add = add.Value; b.AddSpecified = true; }
            return new XmlData.AnimationNode
            {
                Id = id, Name = "beh:" + name, Behaviour = b,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = next },
            };
        }

        private static XmlData.NextNode TEdge(int to, int weight, string when = null)
        {
            return new XmlData.NextNode { Value = to, Probability = weight, When = when };
        }

        private static XmlData.AnimationNode TRoute(params XmlData.NextNode[] edges)
        {
            return new XmlData.AnimationNode
            {
                Id = 1, Name = "route", Route = new XmlData.RouteNode(),
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = edges },
            };
        }

        private static TickWorld TickWorldOf(TickLog log, TickTape tape, params TickType[] types)
        {
            var world = new TickWorld(new TickDesk(), tape ?? new TickTape(), new TickSwitches(), log);
            foreach (TickType t in types) world.AddType(t);
            world.Start();
            return world;
        }

        // ---- groups

        private static void TestTickSelection()
        {
            // Route: A (weight 1) and B (weight 0, transition-only), each with a condition that draws. C replaces the
            // root list (add=false) with D. Z is a zero-weight replacing list, so it drops the companion in.
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1, "Math.random() < 2"), TEdge(11, 0, "Math.random() < 2"), TEdge(12, 0), TEdge(13, 0), TEdge(14, 0), TEdge(15, 0)),
                TBeh(10, "A", "Stand", 1, null),
                TBeh(11, "B", "Stand", 0, null),
                TBeh(12, "C", "Short", 0, false, TEdge(16, 1)),
                TBeh(13, "Z", "Short", 0, false, TEdge(16, 0)),
                TBeh(14, "Fall", "Stand", 0, null),
                TBeh(15, "D", "Stand", 0, null),
                TBeh(16, "D", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "100") }, TPose(0, 1)),
                TAct(31, "Short", "Stay", null, new[] { TParam("Duration", "1") }, TPose(0, 1))));

            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "C");
            w.StepOnce();   // Short plays its one tick
            w.StepOnce();   // its second check fails: C ends and the next behaviour is chosen
            AssertEqual("script:Alpha#when:A#1|script:Alpha#when:B#1|builtin:behavior-select", string.Join("|", log.Draws),
                "Selection did not evaluate every root condition (even ones a replacing list discards) and then draw ONCE.");
            AssertTrue(log.Events.Contains("m1 behaviour C>D next"), "The replacing list's entry was not entered as a 'next' choice: " + string.Join("; ", log.Events));

            var log2 = new TickLog();
            TickWorld w2 = TickWorldOf(log2, null, t);
            TickMascot z = w2.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Z");
            w2.StepOnce();
            w2.StepOnce();
            AssertTrue(log2.Draws.Contains("builtin:no-candidate-drop-x") && !log2.Draws.Contains("builtin:behavior-select"),
                "A list that weighs 0 did not drop the companion in without a selection draw: " + string.Join("|", log2.Draws));
            AssertTrue(log2.Events.Contains("m1 behaviour Z>Fall fallback"), "A list that weighs 0 did not enter Fall as a fallback.");
            AssertEqual(-256, z.Anchor.Y, "The drop-in did not start 256 px above the work area's top.");
        }

        private static void TestTickCaches()
        {
            // P1 is cached for the activation (${}), P2 per step (#{}); the guard reads both. P3 is always null, so it
            // is never kept and draws at every read.
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Watch", "Watch", 1, null),
                new XmlData.AnimationNode
                {
                    Id = 30, Name = "Watch",
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                    Act = new XmlData.ActNode
                    {
                        Type = "Stay", Border = "Floor",
                        Params = new[] { TParam("P1", "Math.random()", "action"), TParam("P2", "Math.random()", "frame"),
                                         TParam("P3", "Math.random() < 0 ? 1 : null", "action") },
                        Guard = new XmlData.GuardNode { When = "P1 < 2 && P2 < 2 && P3 == null" },
                        Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { TPose(0, 1) } } } },
                    },
                }));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Watch");
            // Init checks the action once (UserBehavior.init): P1, P2 and P3 each draw once.
            AssertEqual(3, log.Draws.Count, "Initialising the action did not evaluate each lazy parameter exactly once.");
            int before = log.Draws.Count;
            w.StepOnce();
            // First check: P1 and P2 kept (P2 from init), P3 again = 1. The step clears P2. Second check: P2 and P3 = 2.
            AssertEqual(3, log.Draws.Count - before, "A step did not keep ${} for the activation, clear #{} at the step, and re-read a null every time.");
            before = log.Draws.Count;
            w.StepOnce();
            // Next step's first check sees the frame cache the previous second check left: only P3 draws, then 2 again.
            AssertEqual(3, log.Draws.Count - before, "The first check of a step did not reuse the #{} value the previous step's second check cached.");
        }

        private static void TestTickActionClock()
        {
            // Two behaviours of one tick each, with two-pose animations: the start behaviour plays pose 0 at age 0; a
            // behaviour chosen at the end of a step first plays at age 1, so pose 1.
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 0), TEdge(11, 1)),
                TBeh(10, "X", "X", 0, false, TEdge(11, 1)),
                TBeh(11, "Y", "Y", 1, null),
                TAct(30, "X", "Stay", null, new[] { TParam("Duration", "1") }, TPose(0, 1), TPose(1, 1)),
                TAct(31, "Y", "Stay", null, new[] { TParam("Duration", "5") }, TPose(2, 1), TPose(3, 1))));
            TickWorld w = TickWorldOf(new TickLog(), null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "X");
            w.StepOnce();
            AssertTrue(m.Pose != null, "The start behaviour played no pose in its first step (its age was not 0).");
            AssertEqual(0, m.Pose.Frame, "The start behaviour did not play its first pose at age 0.");
            w.StepOnce();
            AssertEqual("Y", m.Behaviour.Name, "X did not end after its one tick.");
            w.StepOnce();
            AssertEqual(3, m.Pose.Frame, "A behaviour chosen at the end of a step did not first play at age 1 (its second pose).");
        }

        private static void TestTickBreed()
        {
            TickType alpha = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Throw", "Throw", 1, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TAct(30, "Throw", "Embedded", "Breed",
                    new[] { TParam("BornX", "40"), TParam("BornY", "0"), TParam("BornMascot", "Alpha"), TParam("BornBehavior", "Stand"), TParam("BornCount", "2") },
                    TPose(0, 1), TPose(1, 1)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, alpha);
            w.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Throw");
            AssertTrue(w.Mascots.Count == 0 && w.Pending.Count == 1,
                "A start companion was active at tick 0; the source queues it (Manager.add) until step 1.");
            w.StepOnce();   // age 0
            AssertTrue(w.Mascots.Count == 1 && !log.Events.Exists(e => e.StartsWith("m1 activated")),
                "A start companion was not active from step 1, or its activation was reported as a birth.");
            AssertEqual(0, w.Pending.Count, "Breed fired before its penultimate tick.");
            w.StepOnce();   // age 1 = duration - 1: two births
            AssertEqual(2, w.Pending.Count, "Breed did not queue BornCount children on its penultimate tick.");
            AssertTrue(log.Events.Contains("m1 birth m1.b1 #1 try1 at (680, 680)") && log.Events.Contains("m1 birth m1.b2 #2 try2 at (680, 680)"),
                "Births are not named by the parent's lineage or not placed BornX in front of a left-facing parent: " + string.Join("; ", log.Events));
            AssertEqual(1, w.Mascots.Count, "A pending birth was active in the step that requested it.");
            w.StepOnce();
            AssertEqual(3, w.Mascots.Count, "The births did not activate at the next step.");
            AssertEqual("m1.b1", w.Mascots[1].Sid, "Activated births are not in add-queue order.");

            var capped = new TickLog();
            TickWorld c = TickWorldOf(capped, null, alpha);
            c.MaximumCompanions = 2;
            c.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Throw");
            c.StepOnce();
            c.StepOnce();
            AssertTrue(capped.Events.Contains("m1 refused try2 cap") && c.Pending.Count == 1,
                "At the cap the second birth was not refused (policy:birth-cap): " + string.Join("; ", capped.Events));
        }

        private static void TestTickTransform()
        {
            TickType beta = TickType.Compile("Beta", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(2, 1))));
            TickType egg = TickType.Compile("Egg", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", "Stand") },
                    TPose(0, 1), TPose(1, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, egg, beta);
            TickMascot m = w.CreateAtStart("m1", egg, new TickPoint(640, 680), false, "Laid");
            m.Variables["mood"] = ScriptValue.FromNumber(3);
            w.StepOnce();
            AssertEqual("Egg", m.Type.Name, "Transform fired before its penultimate tick.");
            w.StepOnce();
            AssertEqual("Beta", m.Type.Name, "Transform did not change the type on its penultimate tick.");
            AssertEqual("Stand", m.Behaviour.Name, "Transform did not enter the target's behaviour.");
            AssertTrue(ReferenceEquals(m, w.Mascots[0]) && m.Sid == "m1" && m.Variables["mood"].Number == 3,
                "Transform did not keep the companion, its sid and its variables.");
            AssertTrue(log.Events.Contains("m1 transform Egg>Beta Stand"), "No transform event.");
            AssertEqual("Egg", m.PoseType.Name, "Right after a Transform the image shown is not still the old type's (it is until the new type's first pose).");
            w.StepOnce();
            AssertEqual("Beta", m.PoseType.Name, "The new type's first pose did not replace the old type's image.");

            // A blank TransformBehavior cannot be built (Transform.java:51-65 catches it): the failure is reported and the
            // companion stays, as the new type, in the behaviour it was in, rather than being ended.
            TickType blankEgg = TickType.Compile("Egg", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Laid", "Laid", 1, null),
                TAct(30, "Laid", "Embedded", "Transform", new[] { TParam("TransformMascot", "Beta"), TParam("TransformBehavior", "") },
                    TPose(0, 1), TPose(1, 1))));
            var log2 = new TickLog();
            TickWorld v = TickWorldOf(log2, null, blankEgg, beta);
            TickMascot b = v.CreateAtStart("m1", blankEgg, new TickPoint(640, 680), false, "Laid");
            v.StepOnce();
            v.StepOnce();
            AssertTrue(!b.Disposing && v.Mascots.Count == 1 && b.Type.Name == "Beta" && b.Behaviour.Name == "Laid",
                "A Transform whose behaviour could not be built did not keep the companion, as the new type, in its behaviour ("
                + (b.Disposing ? "disposing" : b.Type.Name + " / " + b.Behaviour.Name) + ").");
            AssertTrue(log2.Faults.Count == 1 && log2.Faults[0].Contains("Transform into Beta"),
                "The failed Transform was not reported: " + string.Join("; ", log2.Faults));
            // The kept behaviour is the old type's, and so are the poses it plays: the source draws a pose's own image
            // (Pose.java:70-75), so the companion goes on showing the Egg's sheet while its type is Beta.
            // The step after the failure ends Laid and enters it again (from the Egg's own graph), and the steps after play
            // its poses while the companion is a Beta. PoseType is cleared before each step so that a value after it proves
            // a pose was applied in that step (ApplyPose sets it; nothing else does); it must be the Egg's sheet.
            int applied = 0;
            for (int i = 0; i < 3; i++)
            {
                b.PoseType = null;
                v.StepOnce();
                if (b.PoseType == null) continue;
                applied++;
                AssertTrue(b.Type.Name == "Beta" && ReferenceEquals(b.PoseType, blankEgg),
                    "After a failed Transform the kept behaviour's pose was drawn from the new type's sheet (" + b.PoseType.Name + ").");
            }
            AssertTrue(applied > 0, "No pose was applied in the three steps after the failed Transform, so the sheet check saw nothing: "
                + string.Join("; ", log2.Events));
        }

        private static void TestTickBirthErrorOrder()
        {
            // A birth whose behaviour cannot be entered: the source shows the error and then disposes the child it made
            // (Breed.java:104-105, :110-111), so the fault is reported before the refusal.
            TickType alpha = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Throw", "Throw", 1, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TAct(30, "Throw", "Embedded", "Breed",
                    new[] { TParam("BornMascot", "Alpha"), TParam("BornBehavior", "NoSuchBehaviour") },
                    TPose(0, 1), TPose(1, 1)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, alpha);
            w.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Throw");
            w.StepOnce();
            w.StepOnce();   // age 1 = duration - 1: the birth is attempted and fails
            AssertTrue(log.Order.Count >= 2 && log.Order[0] == "fault" && log.Order[1] == "refused error" && w.Pending.Count == 0,
                "A failed birth was not reported as its fault and then its refusal: " + string.Join(", ", log.Order));
        }

        private static void TestTickMove()
        {
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Walk", "Walk", 1, false, TEdge(11, 1)),
                TBeh(11, "Stand", "Stand", 0, null),
                TAct(30, "Walk", "Move", null, new[] { TParam("TargetX", "600") }, TPose(0, 1, -3)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            TickWorld w = TickWorldOf(new TickLog(), null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Walk");
            var xs = new List<int>();
            for (int i = 0; i < 16; i++) { w.StepOnce(); xs.Add(m.Anchor.X); }
            AssertEqual(637, xs[0], "A Move pose's velocity was not applied (dx -3 facing left).");
            AssertTrue(xs.Contains(600) && !xs.Exists(x => x < 600), "Move overshot its target instead of clamping to it: " + string.Join(",", xs));
            AssertEqual("Stand", m.Behaviour.Name, "Move did not end on reaching its target.");
        }

        private static void TestTickInternalFault()
        {
            // m1's Affordance is a script that yields null: reading its length is the NullReferenceException the source
            // also dies of (ActionBase.java:95). m1 ends, reported; m2, after it in manager order, still steps.
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Bad", "Bad", 1, null),
                TBeh(11, "Good", "Good", 0, null),
                TAct(30, "Bad", "Stay", null, new[] { TParam("Duration", "100"), TParam("Affordance", "null", "action") }, TPose(0, 1)),
                TAct(31, "Good", "Stay", null, new[] { TParam("Duration", "100") }, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            TickMascot bad = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Bad");
            TickMascot good = w.CreateAtStart("m2", t, new TickPoint(700, 680), false, "Good");
            w.StepOnce();
            AssertTrue(bad.Disposing && log.Faults.Count == 1 && log.Faults[0].StartsWith("m1: internal fault: NullReferenceException", StringComparison.Ordinal),
                "A fault that is not CantBeAlive did not end its companion with a report: " + string.Join("; ", log.Faults));
            AssertTrue(!good.Disposing && good.Time == 1, "The step did not go on for the companion after the faulty one.");
        }

        private static void TestTickStartupPriming()
        {
            // CONTRACT section 4: start companions are all initialised (counts 0), then added WITHOUT a step: active at
            // tick 0, none pending, no time or step advanced, and no birth event.
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "100") }, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            TickMascot a = w.CreateAtStart("m1", t, new TickPoint(600, 680), false, "Stand");
            TickMascot b = w.CreateAtStart("m2", t, new TickPoint(700, 680), false, "Stand");
            AssertTrue(w.Mascots.Count == 0 && w.Pending.Count == 2, "Start companions were not queued while being initialised.");
            w.ActivateStartup();
            AssertTrue(w.Mascots.Count == 2 && w.Pending.Count == 0 && ReferenceEquals(w.Mascots[0], a) && ReferenceEquals(w.Mascots[1], b),
                "Priming did not make the start companions active, in creation order.");
            AssertTrue(w.Step == 0 && a.Time == 0 && b.Time == 0 && !log.Events.Exists(e => e.Contains("activated")),
                "Priming advanced the clock or wrote a birth event: " + string.Join("; ", log.Events));
        }

        /// <summary>Main.createMascot reports a first behaviour that cannot be built and disposes the mascot: CreateAtStart
        /// returns it disposed and never queued, and nothing is thrown at the caller.</summary>
        private static void AssertStartRefused(TickWorld w, Func<TickMascot> create, string message)
        {
            TickMascot m;
            try { m = create(); }
            catch (Exception e) { throw new InvalidOperationException(message + " (it threw " + e.GetType().Name + " instead of being refused)"); }
            bool queued = false;
            foreach (TickMascot p in w.Pending) if (ReferenceEquals(p, m)) queued = true;
            AssertTrue(m != null && m.Disposing && !queued, message);
        }

        private static void TestTickStartCannotBuild()
        {
            // MoveWithTurn with one animation: its constructor refuses the definition, as the source's does, so the first
            // behaviour cannot be built. Main.createMascot reports and disposes such a mascot; it never joins the manager.
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Bad", "Bad", 1, null),
                TBeh(11, "Fall", "Stand", 0, null),
                TAct(30, "Bad", "Embedded", "MoveWithTurn", new[] { TParam("TargetX", "100") }, TPose(0, 1, -2)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "100") }, TPose(0, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            TickMascot m = null;
            string escaped = null;
            try { m = w.CreateAtStart("m1", t, new TickPoint(600, 680), false, "Bad"); }
            catch (Exception e) { escaped = e.GetType().Name + ": " + e.Message; }
            AssertTrue(escaped == null, "A first behaviour that cannot be built escaped CreateAtStart: " + escaped);
            AssertTrue(m != null && m.Disposing && w.Pending.Count == 0, "The companion was not disposed, or was queued anyway.");
            AssertTrue(log.Faults.Count == 1 && log.Faults[0].Contains("the first behaviour could not be built"),
                "The failure was not reported: " + string.Join("; ", log.Faults));
            w.StepOnce();
            AssertEqual(0, w.Mascots.Count, "A companion whose first behaviour could not be built became active.");
        }

        /// <summary>The times a pacer allows a step when it is asked at each of the given whole-millisecond times (a
        /// timer's firings).</summary>
        private static List<long> PacedSteps(IEnumerable<long> firings)
        {
            var pacer = new TickPacer(0);
            var steps = new List<long>();
            foreach (long now in firings) if (pacer.Due(now)) steps.Add(now);
            return steps;
        }

        private static IEnumerable<long> Firings(double period, long wallMs)
        {
            for (int i = 1; i * period <= wallMs; i++) yield return (long)Math.Floor(i * period);
        }

        private static void TestTickClockPacer()
        {
            // The host's timer polls every PollMilliseconds, and Windows fires a WinForms timer on the system timer's
            // 15.625 ms tick (measured here: 10 ms fires every 15.7 ms, 40 ms every 46.9 ms). Over ten seconds the pacer
            // must step 25 times a second, as the source's Manager does, not once per firing.
            const double systemTick = 15.625;
            double period = Math.Ceiling(TickPacer.PollMilliseconds / systemTick) * systemTick;
            List<long> steps = PacedSteps(Firings(period, 10000));
            AssertTrue(steps.Count >= 249 && steps.Count <= 250,
                "On the system timer's tick the pacer stepped " + steps.Count + " times in 10 s, not the source's 250.");

            // Polled every millisecond, as the source's thread sleeps, the steps fall on the 40 ms slots exactly.
            var everyMs = new List<long>();
            for (long t = 1; t <= 200; t++) everyMs.Add(t);
            AssertEqual("40,80,120,160,200", string.Join(",", PacedSteps(everyMs)), "The steps left the source's 40 ms slots.");

            // Up to two steps behind, the next step comes at the next slot (a short catch-up); further behind, the slot
            // moves to now, so a stall is never made up in a burst.
            AssertEqual("40,110,120,160", string.Join(",", PacedSteps(new long[] { 40, 110, 119, 120, 159, 160 })),
                "A step less than two ticks late did not catch up at the next slot.");
            AssertEqual("40,300,340", string.Join(",", PacedSteps(new long[] { 40, 300, 301, 320, 339, 340 })),
                "A stall was made up in a burst, or the clock did not restart from the late step.");
        }

        private static void TestTickOfferBlank()
        {
            // Java's trim keeps a no-break space (U+00A0 is above U+0020), so that offer is real; a tab-and-space one is
            // blank and offered by no one.
            string nbsp = ((char)0xA0).ToString();
            XmlData.AnimationNode offering = TAct(30, "Offer", "Stay", null, new[] { TParam("Duration", "100") }, TPose(0, 1));
            offering.Act.Offers = new XmlData.OffersNode
                { Offer = new[] { new XmlData.OfferNode { Value = nbsp }, new XmlData.OfferNode { Value = "\t " } } };
            TickType t = TickType.Compile("Alpha", TickDoc(TRoute(TEdge(10, 1)), TBeh(10, "Offer", "Offer", 1, null), offering));
            TickWorld w = TickWorldOf(new TickLog(), null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Offer");
            w.StepOnce();
            AssertEqual(1, m.Affordances.Count, "Offers were not judged blank as Java's trim judges them.");
            AssertEqual(nbsp, m.Affordances.Count == 1 ? m.Affordances[0] : null, "The no-break-space offer was not kept as written.");
        }

        private static void TestTickLostGround()
        {
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Stand", "Stand", 1, null),
                TBeh(11, "Fall", "Hang", 0, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1)),
                new XmlData.AnimationNode
                {
                    Id = 31, Name = "Hang",
                    Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                    Act = new XmlData.ActNode { Type = "Stay", Border = "", Params = new[] { TParam("Duration", "50") },
                        Variants = new XmlData.VariantsNode { Variant = new[] { new XmlData.VariantNode { Pose = new[] { TPose(1, 1) } } } } },
                }));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 600), false, "Stand");
            w.StepOnce();
            AssertTrue(log.Events.Contains("m1 behaviour Stand>Fall forced"), "A floor action started off the floor did not lose the ground and enter Fall: " + string.Join("; ", log.Events));
            AssertEqual(640, m.Anchor.X, "Losing the ground moved the companion.");
        }
    }
}
