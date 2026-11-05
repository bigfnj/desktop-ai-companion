using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The instant family (Look, Offset, Mute, BringToFront) and SelfDestruct. Instants apply when a composite reaches
    /// them, at initialisation, and never step; so a Sequence of an instant and a Stay shows the instant's effect at
    /// tick 0 already.
    /// </summary>
    internal static partial class Program
    {
        private static XmlData.AnimationNode TInstant(int id, string name, string cls, params XmlData.ParamNode[] ps)
        {
            return new XmlData.AnimationNode
            {
                Id = id, Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode { Type = "Embedded", Class = cls, Params = ps },
            };
        }

        private static XmlData.AnimationNode TSequence(int id, string name, params string[] children)
        {
            var kids = new List<XmlData.CompositeChildNode>();
            foreach (string c in children) kids.Add(new XmlData.CompositeChildNode { Animation = c });
            return new XmlData.AnimationNode
            {
                Id = id, Name = name,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode { Type = "Sequence", Children = new XmlData.ChildrenNode { Mode = "sequence", Child = kids.ToArray() } },
            };
        }

        /// <summary>One behaviour, Go, running the sequence [instant, Stand].</summary>
        private static TickType TInstantType(XmlData.AnimationNode instant)
        {
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Go", "Go", 1, null),
                TSequence(20, "Go", instant.Name, "Stand"),
                instant,
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
        }

        private static void TestTickLookAndOffset()
        {
            TickType flip = TInstantType(TInstant(30, "Turn", "Look"));
            TickWorld w = TickWorldOf(new TickLog(), null, flip);
            TickMascot m = w.CreateAtStart("m1", flip, new TickPoint(640, 680), false, "Go");
            AssertTrue(m.LookRight, "Look with no LookRight did not flip a left-facing companion at initialisation.");

            // LookRight=true on a companion already facing right: the default (flip) would turn it left, so only reading
            // the parameter keeps it right.
            TickType right = TInstantType(TInstant(30, "Face", "Look", TParam("LookRight", "true")));
            TickWorld w2 = TickWorldOf(new TickLog(), null, right);
            TickMascot r = w2.CreateAtStart("m1", right, new TickPoint(640, 680), true, "Go");
            AssertTrue(r.LookRight, "Look did not take an explicit LookRight=true (it flipped instead).");

            // The Stand after the Offset is a floor action: (645, 677) is off the floor, so the companion will lose
            // the ground at its first step; the offset itself is applied at initialisation, before any step.
            TickType shift = TInstantType(TInstant(30, "Nudge", "Offset", TParam("X", "5"), TParam("Y", "-3")));
            TickWorld w3 = TickWorldOf(new TickLog(), null, shift);
            TickMascot o = w3.CreateAtStart("m1", shift, new TickPoint(640, 680), false, "Go");
            AssertEqual(645, o.Anchor.X, "Offset did not add X at initialisation.");
            AssertEqual(677, o.Anchor.Y, "Offset did not add Y at initialisation.");
        }

        private static void TestTickMuteAndFront()
        {
            var named = new TickLog();
            TickType clip = TInstantType(TInstant(30, "Hush", "Mute", TParam("Sound", "bell.wav")));
            TickWorldOf(named, null, clip).CreateAtStart("m1", clip, new TickPoint(640, 680), false, "Go");
            AssertTrue(named.Events.Contains("m1 mute bell.wav"), "Mute with a Sound did not ask for that clip to stop.");

            var all = new TickLog();
            TickType every = TInstantType(TInstant(30, "Hush", "Mute"));
            TickWorldOf(all, null, every).CreateAtStart("m1", every, new TickPoint(640, 680), false, "Go");
            AssertTrue(all.Events.Contains("m1 mute -"), "Mute with no Sound did not stop every clip while sounds are on.");

            var quiet = new TickLog();
            var switches = new TickSwitches();
            switches.Off["Alpha:Sounds"] = false;
            var silent = new TickWorld(new TickDesk(), new TickTape(), switches, quiet);
            silent.AddType(every);
            silent.Start();
            silent.CreateAtStart("m1", every, new TickPoint(640, 680), false, "Go");
            AssertFalse(quiet.Events.Exists(e => e.Contains(" mute ")), "Mute with no Sound acted while this companion's sounds were off.");

            TickType front = TInstantType(TInstant(30, "Up", "BringToFront"));
            TickMascot f = TickWorldOf(new TickLog(), null, front).CreateAtStart("m1", front, new TickPoint(640, 680), false, "Go");
            AssertTrue(f.BringToFrontRequested, "BringToFront did not ask for the window to be raised.");
        }

        /// <summary>The Scaling setting, applied once at load: anchors and hotspots round(v * s) with Java's rounding,
        /// pose moves the same except that a non-zero move under one pixel stays one pixel.</summary>
        private static void TestTickScale()
        {
            XmlData.AnimationNode walk = TAct(30, "Walk", "Stay", null, new[] { TParam("Duration", "50") },
                TPose(0, 1, -3), TPose(1, 1, -7), TPose(2, 1, 0), TPose(3, 1, 1));
            walk.Act.Variants.Variant[0].Hotspots = new XmlData.HotspotsNode { Hotspot = new[] { THotspot("Walk") } };
            Func<double, TickType> make = s =>
            {
                TickType t = TickType.Compile("Alpha", TickDoc(TRoute(TEdge(10, 1)), TBeh(10, "Walk", "Walk", 1, null), walk));
                t.ApplyScale(s, TickScriptContext.JavaInt(64 * s), TickScriptContext.JavaInt(64 * s));
                return t;
            };
            TickType half = make(0.5);
            TickPoseDef[] hp = half.Actions["Walk"].Variants[0].Poses;
            AssertEqual("-1,-3,0,1", hp[0].Dx + "," + hp[1].Dx + "," + hp[2].Dx + "," + hp[3].Dx,
                "Half scale did not scale moves as the source (-3 -> round(-1.5) = -1, -7 -> round(-3.5) = -3 with halves up, 0 stays 0, 1 -> 0.5 kept at 1).");
            AssertTrue(half.AnchorX == 16 && half.AnchorY == 32 && half.CellWidth == 32, "Half scale did not scale the anchor and cell.");
            TickType quarter = make(0.25);
            TickPoseDef[] qp = quarter.Actions["Walk"].Variants[0].Poses;
            // A move of 1 at a quarter is 0.25, which rounds to 0: only the one-pixel rule keeps it moving.
            AssertTrue(qp[0].Dx == -1 && qp[3].Dx == 1, "A non-zero move scaled under one pixel did not stay one pixel in its direction.");
            TickHotspotDef h = make(1.5).Actions["Walk"].Variants[0].Hotspots[0];
            AssertTrue(h.Width == 30 && h.Height == 30 && h.X == 0, "Hotspot size was not scaled with the type.");

            // Java 8's Math.round: exact, halves up. floor(x + 0.5) gets 0.49999999999999994 wrong (the sum rounds to 1).
            AssertEqual(0, TickType.JavaRound(0.49999999999999994), "JavaRound(0.49999999999999994) is not 0, as Java 8 rounds it.");
            AssertEqual(-3, TickType.JavaRound(-3.5), "JavaRound(-3.5) is not -3 (halves go up).");
            AssertEqual(3, TickType.JavaRound(2.5), "JavaRound(2.5) is not 3.");

            // Breed's BornX is scaled by the PARENT type's scale (the source's global Scaling, kept per type here).
            TickType breeder = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Throw", "Throw", 1, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TAct(30, "Throw", "Embedded", "Breed", new[] { TParam("BornX", "40"), TParam("BornBehavior", "Stand") }, TPose(0, 1), TPose(1, 1)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            breeder.ApplyScale(2.0, 128, 128);
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, breeder);
            w.CreateAtStart("m1", breeder, new TickPoint(640, 680), false, "Throw");
            w.StepOnce();
            w.StepOnce();
            AssertTrue(log.Events.Exists(e => e.StartsWith("m1 birth m1.b1 #1 try1 at (720, 680)")),
                "A birth's BornX was not scaled by its parent type's scale (40 at 2x is 80): " + string.Join("; ", log.Events));
        }

        /// <summary>Single-screen mode keeps an interactive window that only TOUCHES the companion's work area
        /// (MascotEnvironment.java:82 tests intersects), so a companion can stand on a window hanging off the edge.</summary>
        private static void TestTickWindowIntersects()
        {
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Stand", "Stand", 1, null),
                TAct(30, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            var desk = new MotionDesk { Window = new[] { 1200, 500, 200, 100 } };   // x 1200..1400: past the work area's right edge
            var w = new TickWorld(desk, new TickTape(), new TickSwitches(), new TickLog());
            w.AddType(t);
            w.Start();
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(1250, 500), false, "Stand");
            m.Environment.GetWorkArea();   // remembers the work area, so the single-screen window test applies
            ITickBorder floor = m.Environment.GetFloor();
            AssertTrue(floor.IsOn(m.Anchor), "A window partly outside the work area was dropped in single-screen mode; the source keeps any window that intersects it.");
        }

        private static void TestTickSelfDestruct()
        {
            TickType t = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1)),
                TBeh(10, "Pop", "Pop", 1, null),
                TAct(30, "Pop", "Embedded", "SelfDestruct", null, TPose(0, 1), TPose(1, 1))));
            var log = new TickLog();
            TickWorld w = TickWorldOf(log, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Pop");
            w.StepOnce();   // age 0
            AssertFalse(m.Disposing, "SelfDestruct fired before its penultimate tick.");
            w.StepOnce();   // age 1 = duration - 1
            AssertTrue(m.Disposing && !m.Visible && log.Events.Contains("m1 dispose-request self-destruct"),
                "SelfDestruct did not dispose the companion on its penultimate tick: " + string.Join("; ", log.Events));
            AssertEqual(1, w.Mascots.Count, "A disposed companion left the manager before the next step.");
            w.StepOnce();
            AssertTrue(w.Mascots.Count == 0 && log.Events.Contains("m1 disposed"), "The disposed companion was not removed at the next step.");
        }
    }
}
