using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace DesktopAICompanion.Shimeji
{
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>
        /// burndown/shimeji-host-1: the host's step around one companion. A companion still running the TickType it was
        /// built with is drawn from that type's own sheet after a reload stages its name again (N-audit-1106-045); one whose
        /// frame cannot be drawn ends, reported, and the step goes on for the others; a companion that ends in its first
        /// tick is given no window (N-audit-1106-307); the tick clock stops with the last companion and starts again with
        /// the next (N-audit-1106-306). Real windows at opacity 0; sheets generated here.
        /// </summary>
        private static void CheckHostStep(Action<bool, string> check, StringBuilder log)
        {
            TickHost host = null;
            Xml first = null, second = null;
            var opened = new List<TickHostType>();
            try
            {
                first = new Xml(GeometryFrames(), 64, 64);   // two frames
                List<Bitmap> one = GeometryFrames();
                one[1].Dispose();
                one.RemoveAt(1);
                second = new Xml(one, 64, 64);               // frame 0 only
                host = new TickHost(t =>
                    {
                        opened.Add(t);
                        var form = new FormCompanion(new Animations(t.Sprites), t.Sprites);
                        form.Show(t.Type.CellWidth, t.Type.CellHeight);
                        return form;
                    },
                    form => form.Close(),
                    message => log.AppendLine("LOG: " + message)) { RenderOpacity = 0.0 };
                host.Environment.SearchesWindows = false;
                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                var floor = new TickPoint(work.Left + work.Width / 2, work.Bottom);

                // Pair facing right shows frame 1, its explicit right image, which the sheet staged later does not have.
                TickType oldType = TickType.Compile("Restage", GeometryDocument());
                oldType.ApplyScale(1.0, 64, 64);
                var oldHostType = new TickHostType("Restage", oldType, first);
                host.AddType(oldHostType);
                TickMascot running = host.Spawn("Restage", floor, true, "Pair");
                host.Step();
                TickType newType = TickType.Compile("Restage", GeometryDocument());
                newType.ApplyScale(1.0, 64, 64);
                host.AddType(new TickHostType("Restage", newType, second));
                string escaped = null;
                try { host.Step(); }
                catch (Exception e) { escaped = e.GetType().Name; }
                check(escaped == null && running != null && !running.Disposing && host.FormOf(running) != null
                      && ReferenceEquals(host.HostTypeOf(running.PoseType ?? running.Type), oldHostType),
                    "host step: a companion still running the type it was built with is drawn from that type's own sheet after its name is staged again"
                    + (escaped == null ? "" : " (Step threw " + escaped + ")"));

                // Its own sheet disposed under it: it cannot be drawn, so it ends, reported, and the other companion steps on.
                TickMascot other = host.Spawn("Restage", new TickPoint(floor.X - 200, floor.Y), false, "Small");
                host.Step();
                int otherTime = other == null ? -1 : other.Time;
                first.Dispose();
                escaped = null;
                try { host.Step(); }
                catch (Exception e) { escaped = e.GetType().Name; }
                check(escaped == null && running != null && running.Disposing && log.ToString().Contains("its frame could not be drawn")
                      && other != null && !other.Disposing && other.Time == otherTime + 1,
                    "host step: a companion whose frame cannot be drawn ends, reported, and the step goes on for the others"
                    + (escaped == null ? "" : " (Step threw " + escaped + ")"));

                // A companion that ends in its first tick (a one-tick SelfDestruct) leaves at the next step: no window first.
                TickType popType = TickType.Compile("PopOnce", PopOnceDocument());
                popType.ApplyScale(1.0, 64, 64);
                host.AddType(new TickHostType("PopOnce", popType, second));
                int openedBefore = opened.Count;
                TickMascot pop = host.Spawn("PopOnce", floor, false, "PopOnce");
                host.Step();
                check(pop != null && pop.Disposing && opened.Count == openedBefore && host.FormOf(pop) == null,
                    "host step: a companion that ends in its first tick is given no window (" + (opened.Count - openedBefore) + " opened)");
                TickMascot lives = host.Spawn("Restage", floor, false, "Small");
                host.Step();
                check(lives != null && !lives.Disposing && opened.Count == openedBefore + 1 && host.FormOf(lives) != null,
                    "host step: WITNESS a companion that lives past its first tick is given a window");

                // The clock: running while any companion is, stopped at the step that removes the last, started by a spawn.
                bool runningWithCompanions = host.ClockRunning;
                foreach (TickMascot m in new List<TickMascot>(host.World.Mascots)) m.Dispose("closed");
                host.Step();
                bool stopped = !host.ClockRunning && host.World.Mascots.Count == 0 && host.World.Pending.Count == 0;
                TickMascot again = host.Spawn("Restage", floor, false, "Small");
                check(runningWithCompanions && stopped && again != null && host.ClockRunning,
                    "host step: the tick clock stops at the step that removes the last companion and a spawn starts it again (running "
                    + runningWithCompanions + ", stopped " + stopped + ", restarted " + host.ClockRunning + ")");
            }
            catch (Exception e)
            {
                check(false, "host step: the check ran without an exception: " + e);
            }
            finally
            {
                if (host != null) host.Dispose();
                if (first != null) first.Dispose();
                if (second != null) second.Dispose();
            }
        }

        /// <summary>GeometryDocument plus PopOnce: a SelfDestruct of one one-tick pose, which ends the companion at its first
        /// tick.</summary>
        private static XmlData.RootNode PopOnceDocument()
        {
            XmlData.RootNode doc = GeometryDocument();
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            var pop = new XmlData.AnimationNode
            {
                Id = 40, Name = "PopOnce", Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Act = new XmlData.ActNode
                {
                    Type = "Embedded", Class = "SelfDestruct", Border = "Floor", Params = new XmlData.ParamNode[0],
                    Variants = new XmlData.VariantsNode
                    {
                        Variant = new[] { new XmlData.VariantNode { Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = 1 } } } },
                    },
                },
            };
            var behaviour = new XmlData.AnimationNode
            {
                Id = 41, Name = "beh:PopOnce", Start = still, End = still,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = new XmlData.NextNode[0] },
                Behaviour = new XmlData.BehaviourNode { Name = "PopOnce", Runs = "PopOnce", Frequency = 0 },
            };
            var nodes = new List<XmlData.AnimationNode>(doc.Animations.Animation);
            XmlData.AnimationNode route = nodes.Find(n => n.Route != null);
            var edges = new List<XmlData.NextNode>(route.Sequence.Next) { new XmlData.NextNode { Value = 41, Probability = 0 } };
            route.Sequence.Next = edges.ToArray();
            nodes.Add(pop);
            nodes.Add(behaviour);
            doc.Animations.Animation = nodes.ToArray();
            return doc;
        }
    }
}
