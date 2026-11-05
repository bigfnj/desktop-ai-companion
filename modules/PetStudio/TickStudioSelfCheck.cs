using System;
using System.Collections.Generic;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert;   // ShimejiEngine.Serialize, the serializer the chain builder writes with

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// Companion Studio's single-companion flow over a tick-clock companion (grimoire/06): it validates with the app's
    /// tokens (StudioFeatures.cs), the analysis walks it with the tick walk and draws its edges, and the behaviour
    /// debugger refuses it by name instead of building a debug pet that has lost its actions. Preview and Install hand
    /// the editor's text to the host untouched, and the host re-validates there with its own class gate.
    ///
    /// The tick companion is built from the fixture's own header, image and icon, so no new art is involved. Run by
    /// PetStudioModule.SelfTest (--module-selftest=petstudio) on the embedded fixture and by the host's
    /// --petstudio-selftest on its bundled pet. Named RunChecks, never SelfTest (see PetStudioModule.SelfTest).
    /// </summary>
    internal static class TickStudioSelfCheck
    {
        // The fixture's node ids: what the walk must reach and the one action it must not.
        private const int RouteId = 1, StandId = 2, StandActId = 3, FallId = 4, IdleId = 5, FallActId = 6, PettedId = 7,
            PetActId = 8, OrphanId = 9;

        internal static bool RunChecks(string fixturePetXml, out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                XmlData.RootNode classic;
                string error;
                if (!Check(sb, "the classic fixture parses, so the tick companion can borrow its image",
                        CompanionXmlValidator.TryParse(fixturePetXml, out classic, out error)))
                {
                    detail = sb.ToString() + "  (" + error + ")";
                    return false;
                }
                string tickXml = ShimejiEngine.Serialize(TickCompanion(classic));

                PetReport report = PetAnalyzer.Analyze(tickXml);
                ok &= Check(sb, "Studio accepts a tick-clock companion that uses only features the app runs"
                                + (report.IsValid ? "" : " (" + report.Error + ")"), report.IsValid);
                ok &= Check(sb, "WITNESS the analysis knows it is a tick-clock companion", report.IsTickClock);
                ok &= Check(sb, "the tick walk reports exactly the orphan action as never running (got "
                                + Ids(report.UnreachableAnimations) + ")",
                    report.UnreachableAnimations.Count == 1 && report.UnreachableAnimations[0] == OrphanId);

                var byId = new Dictionary<int, AnimNode>();
                foreach (AnimNode n in report.Nodes) byId[n.Id] = n;
                ok &= Check(sb, "the map draws the tick edges: route, runs, composite and hotspot",
                    HasEdge(byId, RouteId, StandId, TickReachability.RouteEdge)
                    && HasEdge(byId, StandId, StandActId, TickReachability.RunsEdge)
                    && HasEdge(byId, StandActId, IdleId, TickReachability.ChildEdge)
                    && HasEdge(byId, IdleId, PettedId, TickReachability.HotspotEdge));
                AnimNode fall;
                ok &= Check(sb, "Fall is a root and an engine entry on the map (the runtime enters it by name)",
                    byId.TryGetValue(FallId, out fall) && fall.IsRoot && fall.IsEngineEntry);
                AnimNode idle;
                ok &= Check(sb, "an action's frames are its first variant's poses",
                    byId.TryGetValue(IdleId, out idle) && idle.Frames.Length == 2 && idle.Frames[0] == 0 && idle.Frames[1] == 1);
                ok &= Check(sb, "the report explains a dead node in the Shimeji runtime's terms",
                    report.Describe().IndexOf("on the Shimeji runtime", StringComparison.Ordinal) >= 0);

                // The behaviour debugger: refused by name, before anything is cloned.
                string chainError;
                string built = BehaviourChain.BuildDebugXml(tickXml,
                    new List<ChainStep> { new ChainStep { AnimationId = StandActId, Name = "StandAct", Repeat = 1 } }, false, out chainError);
                ok &= Check(sb, "the behaviour debugger refuses a tick-clock companion with its message (got '" + chainError + "')",
                    built == null && chainError == BehaviourChain.TickClockRefusal);
                PetReport classicReport = PetAnalyzer.Analyze(fixturePetXml);
                string classicError = "";
                string classicBuilt = classicReport.IsValid && classicReport.Nodes.Count > 0
                    ? BehaviourChain.BuildDebugXml(fixturePetXml,
                        new List<ChainStep> { new ChainStep { AnimationId = classicReport.Nodes[0].Id, Name = classicReport.Nodes[0].Name, Repeat = 1 } },
                        false, out classicError)
                    : null;
                ok &= Check(sb, "WITNESS the debugger still builds a classic companion's chain (" + classicError + ")", classicBuilt != null);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("  EXC " + ex.GetType().Name + ": " + ex.Message);
            }
            detail = sb.ToString();
            return ok;
        }

        /// <summary>
        /// A tick-clock companion on the classic fixture's image: the route selects Stand, which runs a composite whose
        /// child Idle carries a hotspot entering Petted; Fall is on the route at weight 0, entered by the engine; Orphan
        /// is an action nothing runs. Features: tick, actions, behaviours, hotspots.
        /// </summary>
        internal static XmlData.RootNode TickCompanion(XmlData.RootNode classic)
        {
            XmlData.RootNode r = XmlData.NodeCopy.Deep(classic);
            r.Header.Features = "tick actions behaviours hotspots";
            r.Header.Clock = "tick";
            r.Animations.Animation = new[]
            {
                Node(RouteId, "route", n => n.Route = new XmlData.RouteNode(),
                    Edge(StandId, 1), Edge(FallId, 0), Edge(PettedId, 0)),
                Node(StandId, "beh:Stand", n => n.Behaviour = new XmlData.BehaviourNode { Name = "Stand", Runs = "StandAct", Frequency = 1 }),
                Node(StandActId, "StandAct", n => n.Act = new XmlData.ActNode
                {
                    Type = "Sequence",
                    Children = new XmlData.ChildrenNode { Mode = "sequence", Child = new[] { new XmlData.CompositeChildNode { Animation = "Idle" } } },
                }),
                Node(FallId, "beh:Fall", n => n.Behaviour = new XmlData.BehaviourNode { Name = "Fall", Runs = "FallAct", Frequency = 0 }),
                Node(IdleId, "Idle", n => n.Act = Stay(new XmlData.HotspotsNode
                {
                    Hotspot = new[] { new XmlData.HotspotNode { Shape = "rect", Origin = "0,0", Size = "8,8", Behaviour = "Petted" } },
                })),
                Node(FallActId, "FallAct", n => n.Act = Stay(null)),
                Node(PettedId, "beh:Petted", n => n.Behaviour = new XmlData.BehaviourNode { Name = "Petted", Runs = "PetAct", Frequency = 0 }),
                Node(PetActId, "PetAct", n => n.Act = Stay(null)),
                Node(OrphanId, "Orphan", n => n.Act = Stay(null)),
            };
            r.Spawns = new XmlData.SpawnsNode
            {
                Spawn = new[] { new XmlData.SpawnNode { Id = 1, Probability = 1, X = "0", Y = "0", Next = new XmlData.NextNode { Value = RouteId, Probability = 100 } } },
            };
            r.Childs = new XmlData.ChildsNode { Child = new XmlData.ChildNode[0] };
            r.Sounds = null;
            return r;
        }

        private static XmlData.AnimationNode Node(int id, string name, Action<XmlData.AnimationNode> kind, params XmlData.NextNode[] next)
        {
            var still = new XmlData.MovingNode { X = "0", Y = "0", Interval = "40" };
            var node = new XmlData.AnimationNode
            {
                Id = id, Name = name, Start = still, End = XmlData.NodeCopy.Deep(still),
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = next.Length == 0 ? null : next },
            };
            kind(node);
            return node;
        }

        private static XmlData.NextNode Edge(int to, int probability)
        {
            return new XmlData.NextNode { Value = to, Probability = probability };
        }

        private static XmlData.ActNode Stay(XmlData.HotspotsNode hotspots)
        {
            return new XmlData.ActNode
            {
                Type = "Stay",
                Variants = new XmlData.VariantsNode
                {
                    Variant = new[]
                    {
                        new XmlData.VariantNode
                        {
                            Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = 5 }, new XmlData.PoseNode { Frame = 1, Ticks = 5 } },
                            Hotspots = hotspots,
                        },
                    },
                },
            };
        }

        private static bool HasEdge(Dictionary<int, AnimNode> byId, int from, int to, string kind)
        {
            AnimNode node;
            if (!byId.TryGetValue(from, out node)) return false;
            foreach (AnimEdge e in node.Edges)
                if (e != null && e.To == to && e.Kind == kind) return true;
            return false;
        }

        private static string Ids(List<int> ids)
        {
            var parts = new List<string>();
            foreach (int id in ids) parts.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return "[" + string.Join(",", parts) + "]";
        }

        private static bool Check(StringBuilder sb, string what, bool pass)
        {
            sb.AppendLine((pass ? "  ok   " : "  FAIL ") + what);
            return pass;
        }
    }
}
