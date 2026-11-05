using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// Pointer input (UserBehavior.mousePressed / mouseReleased / the held-hotspot pass of next): a press starts a
    /// drag unless a hotspot or a non-draggable action takes it; a release throws only a companion actually being
    /// dragged; a held hotspot re-enters its behaviour every step until the pointer leaves it. Points are window-local:
    /// the companion's 64x64 window sits at (608, 616) once drawn, its anchor (640, 680) at cell point (32, 64).
    /// </summary>
    internal static partial class Program
    {
        private static TickType TInputType(bool draggable, XmlData.HotspotNode hotspot)
        {
            XmlData.AnimationNode stand = TAct(30, "Stand", "Stay", null,
                new[] { TParam("Duration", "500"), TParam("Draggable", draggable ? "true" : "false") }, TPose(0, 1));
            if (hotspot != null) stand.Act.Variants.Variant[0].Hotspots = new XmlData.HotspotsNode { Hotspot = new[] { hotspot } };
            return TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0), TEdge(12, 0), TEdge(13, 0)),
                TBeh(10, "Stand", "Stand", 1, null),
                TBeh(11, "Dragged", "Hold", 0, null),
                TBeh(12, "Thrown", "Hold", 0, null),
                TBeh(13, "Petted", "Stand", 0, null),
                stand,
                TAct(31, "Hold", "Stay", null, new[] { TParam("Duration", "500") }, TPose(1, 1))));
        }

        private static XmlData.HotspotNode THotspot(string behaviour)
        {
            return new XmlData.HotspotNode { Shape = "rectangle", Origin = "0,0", Size = "20,20", Behaviour = behaviour };
        }

        /// <summary>A world whose desk delivers <paramref name="inputs"/> (tick, kind, screen x, screen y).</summary>
        private static TickWorld TInputWorld(TickLog log, TickType t, bool lookRight, params object[] inputs)
        {
            var desk = new TickDesk();
            for (int i = 0; i < inputs.Length; i += 4)
            {
                int tick = (int)inputs[i];
                List<TickInput> at;
                if (!desk.Script.TryGetValue(tick, out at)) desk.Script[tick] = at = new List<TickInput>();
                at.Add(new TickInput((TickInputKind)inputs[i + 1], "m1", new TickPoint((int)inputs[i + 2], (int)inputs[i + 3])));
            }
            var w = new TickWorld(desk, new TickTape(), new TickSwitches(), log);
            w.AddType(t);
            w.Start();
            w.CreateAtStart("m1", t, new TickPoint(640, 680), lookRight, "Stand");
            return w;
        }

        private static int TCount(TickLog log, string text)
        {
            int n = 0;
            foreach (string e in log.Events) if (e == text) n++;
            return n;
        }

        private static void TestTickInputDrag()
        {
            var log = new TickLog();
            TickWorld w = TInputWorld(log, TInputType(true, null), false,
                2, TickInputKind.Press, 618, 626);
            TickMascot m = w.Mascots.Count > 0 ? w.Mascots[0] : w.Pending[0];
            w.StepOnce();
            w.StepOnce();
            AssertTrue(log.Events.Contains("m1 behaviour Stand>Dragged forced"), "A press on a draggable companion did not start a drag: " + string.Join("; ", log.Events));
            m.Dragging = true;   // what the Dragged action does on its first tick
            ((TickDesk)w.Environment).Script[3] = new List<TickInput> { new TickInput(TickInputKind.Release, "m1", new TickPoint(618, 626)) };
            w.StepOnce();
            AssertTrue(log.Events.Contains("m1 behaviour Dragged>Thrown forced") && !m.Dragging, "Releasing a dragged companion did not throw it.");
            // CONTRACT event `drag`: start at the press that drags, end at the release that throws, each written before
            // the behaviour change it causes.
            int dragStart = log.Events.IndexOf("m1 drag start"), toDragged = log.Events.IndexOf("m1 behaviour Stand>Dragged forced");
            int dragEnd = log.Events.IndexOf("m1 drag end"), toThrown = log.Events.IndexOf("m1 behaviour Dragged>Thrown forced");
            AssertTrue(dragStart >= 0 && dragStart < toDragged && dragEnd > toDragged && dragEnd < toThrown,
                "The drag events are missing or out of order: " + string.Join("; ", log.Events));

            // Press and release in one step: the dragging flag was never set, so nothing is thrown.
            var quick = new TickLog();
            TickWorld q = TInputWorld(quick, TInputType(true, null), false,
                2, TickInputKind.Press, 618, 626, 2, TickInputKind.Release, 618, 626);
            q.StepOnce();
            q.StepOnce();
            AssertTrue(quick.Events.Contains("m1 behaviour Stand>Dragged forced") && !quick.Events.Exists(e => e.Contains(">Thrown")),
                "A release before any action set the dragging flag threw the companion.");

            var still = new TickLog();
            TickWorld s = TInputWorld(still, TInputType(false, null), false, 2, TickInputKind.Press, 618, 626);
            s.StepOnce();
            s.StepOnce();
            AssertFalse(still.Events.Exists(e => e.Contains(">Dragged")), "A press on an action that refuses dragging still started a drag.");
        }

        private static void TestTickInputHotspot()
        {
            var log = new TickLog();
            TickWorld w = TInputWorld(log, TInputType(true, THotspot("Petted")), false,
                2, TickInputKind.Press, 618, 626, 5, TickInputKind.Move, 1000, 100);
            TickMascot m = w.Pending[0];
            w.StepOnce();
            w.StepOnce();
            AssertTrue(log.Events.Contains("m1 behaviour Stand>Petted forced") && m.IsHotspotClicked,
                "A press inside a hotspot did not enter its behaviour and hold: " + string.Join("; ", log.Events));
            // CONTRACT event `hotspot`: the press, with its behaviour, written before the behaviour change it causes.
            int press = log.Events.IndexOf("m1 hotspot press Petted"), toPetted = log.Events.IndexOf("m1 behaviour Stand>Petted forced");
            AssertTrue(press >= 0 && press < toPetted && !log.Events.Exists(e => e.StartsWith("m1 drag")),
                "The hotspot press event is missing, out of order, or a drag was reported: " + string.Join("; ", log.Events));
            w.StepOnce();
            w.StepOnce();
            AssertEqual(3, TCount(log, "m1 behaviour Petted>Petted forced"), "A held hotspot did not re-enter its behaviour on every step (2, 3, 4).");
            w.StepOnce();   // the pointer left the hotspot at tick 5
            AssertTrue(!m.IsHotspotClicked && TCount(log, "m1 behaviour Petted>Petted forced") == 3,
                "Moving the held pointer out of the hotspot did not end the hold.");

            // The release of a held hotspot is reported with no behaviour (the source's release only clears the hold).
            var released = new TickLog();
            TickWorld rel = TInputWorld(released, TInputType(true, THotspot("Petted")), false,
                2, TickInputKind.Press, 618, 626, 3, TickInputKind.Release, 618, 626);
            rel.StepOnce();
            rel.StepOnce();
            rel.StepOnce();
            AssertTrue(released.Events.Contains("m1 hotspot release -") && rel.Mascots.Count == 1 && !rel.Mascots[0].IsHotspotClicked,
                "Releasing a held hotspot was not reported, or the hold was kept: " + string.Join("; ", released.Events));

            var bare = new TickLog();
            TickWorld b = TInputWorld(bare, TInputType(true, THotspot(null)), false, 2, TickInputKind.Press, 618, 626);
            b.StepOnce();
            b.StepOnce();
            AssertTrue(bare.Events.Contains("m1 behaviour Stand>Dragged forced"), "A hotspot with no behaviour took the press; the source drags instead.");

            // Facing right the point mirrors as width - x: (54, 10) on a 64-wide window is (10, 10), inside.
            var mirrored = new TickLog();
            TickWorld r = TInputWorld(mirrored, TInputType(true, THotspot("Petted")), true, 2, TickInputKind.Press, 662, 626);
            r.StepOnce();
            r.StepOnce();
            AssertTrue(mirrored.Events.Contains("m1 behaviour Stand>Petted forced"), "A right-facing companion's hotspot was not mirrored as width - x.");
        }
    }
}
