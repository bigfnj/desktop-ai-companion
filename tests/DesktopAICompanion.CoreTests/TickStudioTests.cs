using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>
    /// The tick-clock walk (src/dotNet/TickReachability.cs) that Companion Studio's analyzer, the host's
    /// AnimationReachability and the converter's PetGraph hand a tick-clock document to. Built from in-memory DTOs: the
    /// walk reads the parsed graph only, so no sheet, schema or validator is involved. Each group pins one rule, and each
    /// fixture carries a node that only that rule reaches (or keeps out).
    /// </summary>
    internal static partial class Program
    {
        private static void TestTickStudioStructuralEdges()
        {
            // 1 route -> Stand (2) and, at weight 0, Petted (4). Stand runs a composite (3) whose child Idle (5) has a
            // hotspot entering Petted, which runs PetAct (6). Orphan (7) is reached by nothing.
            XmlData.RootNode doc = TsDoc(
                TsRoute(1, TsEdge(2, 1), TsEdge(4, 0)),
                TsBehaviour(2, "Stand", "StandAct"),
                TsAct(3, "StandAct", "Sequence", null, new[] { TsChild("Idle") }),
                TsAct(5, "Idle", "Stay", null, null, "Petted"),
                TsBehaviour(4, "Petted", "PetAct"),
                TsAct(6, "PetAct", "Animate", null, null),
                TsAct(7, "Orphan", "Stay", null, null));
            TickGraph graph = TickReachability.Build(doc);
            AssertEqual("[7]", TsIds(TickReachability.FindUnreachable(doc)), "only the orphan action is unreachable");
            AssertTrue(TsHasEdge(graph, 1, 2, TickReachability.RouteEdge), "the route leads to its root behaviour");
            AssertTrue(TsHasEdge(graph, 2, 3, TickReachability.RunsEdge), "a behaviour leads to the action it runs");
            AssertTrue(TsHasEdge(graph, 3, 5, TickReachability.ChildEdge), "a composite leads to its child");
            AssertTrue(TsHasEdge(graph, 5, 4, TickReachability.HotspotEdge), "a hotspot leads to the behaviour it enters");
            AssertEqual("[1]", TsIds(graph.Roots), "the route is the one root here");
        }

        private static void TestTickStudioZeroWeight()
        {
            // Rare (3) is on the route and in Stand's next list, both at weight 0: listed, never selected.
            XmlData.RootNode doc = TsDoc(
                TsRoute(1, TsEdge(2, 1), TsEdge(3, 0)),
                TsBehaviour(2, "Stand", "A", TsEdge(3, 0)),
                TsBehaviour(3, "Rare", "B"),
                TsAct(4, "A", "Stay", null, null),
                TsAct(5, "B", "Stay", null, null));
            TickGraph graph = TickReachability.Build(doc);
            AssertEqual("[3,5]", TsIds(TickReachability.FindUnreachable(doc)), "a zero-weight selection is not followed");
            AssertTrue(TsHasEdge(graph, 1, 3, TickReachability.RouteEdge), "WITNESS the zero-weight route entry is listed");
            AssertTrue(TsHasEdge(graph, 2, 3, TickReachability.NextEdge), "WITNESS the zero-weight next entry is listed");
        }

        private static void TestTickStudioNextContinuation()
        {
            // Stand (2) selects an off-route entry named Sit (5). When it ends the runtime looks up the ROOT Sit (3) by
            // name and offers ITS list, which leads to Nap (4). Root Sit itself is never selected (weight 0, no other
            // way in), so it and the action only it runs (11) are dead; Nap and NapAct (12) are alive.
            XmlData.RootNode doc = TsDoc(
                TsRoute(1, TsEdge(2, 1), TsEdge(3, 0), TsEdge(4, 0)),
                TsBehaviour(2, "Stand", "A", TsEdge(5, 1)),
                TsBehaviour(5, "Sit", "A"),
                TsBehaviour(3, "Sit", "SitAct", TsEdge(4, 1)),
                TsBehaviour(4, "Nap", "NapAct"),
                TsAct(10, "A", "Stay", null, null),
                TsAct(11, "SitAct", "Stay", null, null),
                TsAct(12, "NapAct", "Stay", null, null));
            TickGraph graph = TickReachability.Build(doc);
            AssertEqual("[3,11]", TsIds(TickReachability.FindUnreachable(doc)),
                "an off-route entry continues with its root namesake's list");
            AssertTrue(TsHasEdge(graph, 5, 4, TickReachability.NextEdge), "the off-route Sit's next edges are root Sit's");
        }

        private static void TestTickStudioEngineRoots()
        {
            XmlData.RootNode doc = TsEngineDoc("Alpha");
            TickGraph graph = TickReachability.Build(doc);
            AssertEqual("[7]", TsIds(TickReachability.FindUnreachable(doc)),
                "Fall, Dragged, Thrown and the startup behaviour are entered without a selection");
            AssertEqual("[1,3,4,5,6]", TsIds(graph.Roots), "the roots are the route, the engine's three, then the startup");
            var engine = new List<int>(graph.EngineEntries);
            engine.Sort();
            AssertEqual("[3,4,5]", TsIds(engine), "the engine entries are Fall, Dragged and Thrown");
            // WITNESS: a startup naming ANOTHER member's behaviour roots nothing here.
            AssertEqual("[6,7]", TsIds(TickReachability.FindUnreachable(TsEngineDoc("Egg"))),
                "a startup behaviour of another member is not this member's root");
        }

        private static void TestTickStudioNamedBehaviours()
        {
            // Stand runs Seq (14): Breeder (Breed, BornBehavior=Born), Scanner (ScanMove, Behavior=Partner, a SCRIPTED
            // TargetBehavior whose text happens to be a behaviour's name), Away (Breed into ANOTHER member: BornMascot=Egg,
            // BornBehavior=Elsewhere), Plain (Look, a class that never reads Behavior=Ignored), and Scanner again bound
            // with TargetBehavior=Bound. Every named behaviour is weight 0 on the route.
            XmlData.RootNode doc = TsDoc(
                TsRoute(1, TsEdge(2, 1), TsEdge(3, 0), TsEdge(4, 0), TsEdge(5, 0), TsEdge(6, 0), TsEdge(8, 0), TsEdge(15, 0)),
                TsBehaviour(2, "Stand", "Seq"),
                TsBehaviour(3, "Born", "A"),
                TsBehaviour(4, "Partner", "A"),
                TsBehaviour(5, "Elsewhere", "A"),
                TsBehaviour(6, "Scripted", "A"),
                TsBehaviour(8, "Bound", "A"),
                TsBehaviour(15, "Ignored", "A"),
                TsAct(9, "A", "Stay", null, null),
                TsAct(14, "Seq", "Sequence", null, new[]
                {
                    TsChild("Breeder"), TsChild("Scanner"), TsChild("Away"), TsChild("Plain"),
                    TsChild("Scanner", new XmlData.BindNode { Name = "TargetBehavior", Value = "Bound", Cache = "none" }),
                }),
                TsEmbedded(10, "Breeder", "Breed", TsParam("BornBehavior", "Born", "none")),
                TsEmbedded(11, "Scanner", "ScanMove", TsParam("Behavior", "Partner", "none"),
                    TsParam("TargetBehavior", "Scripted", "action")),
                TsEmbedded(12, "Away", "Breed", TsParam("BornMascot", "Egg", "none"), TsParam("BornBehavior", "Elsewhere", "none")),
                TsEmbedded(13, "Plain", "Look", TsParam("Behavior", "Ignored", "none")));
            TickGraph graph = TickReachability.Build(doc);
            AssertEqual("[5,6,15]", TsIds(TickReachability.FindUnreachable(doc)),
                "literal Born/Partner/Bound names reach; a scripted name, another member's, and one no class reads do not");
            AssertTrue(TsHasEdge(graph, 10, 3, TickReachability.NamedEdge), "BornBehavior is an edge from the breeding action");
            AssertTrue(TsHasEdge(graph, 11, 4, TickReachability.NamedEdge), "Behavior is an edge from the scanning action");
            AssertTrue(TsHasEdge(graph, 14, 8, TickReachability.NamedEdge), "a child's bound TargetBehavior is an edge from the composite");
        }

        // ---- fixture builders

        private static XmlData.RootNode TsEngineDoc(string startupMember)
        {
            XmlData.RootNode doc = TsDoc(
                TsRoute(1, TsEdge(2, 1), TsEdge(3, 0), TsEdge(4, 0), TsEdge(5, 0), TsEdge(6, 0), TsEdge(7, 0)),
                TsBehaviour(2, "Stand", "A"),
                TsBehaviour(3, "Fall", "A"),
                TsBehaviour(4, "Dragged", "A"),
                TsBehaviour(5, "Thrown", "A"),
                TsBehaviour(6, "Hatch", "A"),
                TsBehaviour(7, "Other", "A"),
                TsAct(10, "A", "Stay", null, null));
            doc.Header.Pack = new XmlData.PackNode { Id = "p", Member = "Alpha", Role = "main" };
            doc.Header.Startup = new XmlData.StartupNode { Member = startupMember, Behaviour = "Hatch" };
            return doc;
        }

        private static XmlData.RootNode TsDoc(params XmlData.AnimationNode[] nodes)
        {
            return new XmlData.RootNode
            {
                Header = new XmlData.HeaderNode { Clock = "tick", Features = "tick actions behaviours" },
                Animations = new XmlData.AnimationsNode { Animation = nodes },
            };
        }

        private static XmlData.NextNode TsEdge(int to, int probability)
        {
            return new XmlData.NextNode { Value = to, Probability = probability };
        }

        private static XmlData.AnimationNode TsRoute(int id, params XmlData.NextNode[] next)
        {
            return new XmlData.AnimationNode
            {
                Id = id, Name = "route", Route = new XmlData.RouteNode(),
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = next },
            };
        }

        private static XmlData.AnimationNode TsBehaviour(int id, string name, string runs, params XmlData.NextNode[] next)
        {
            return new XmlData.AnimationNode
            {
                Id = id, Name = "beh:" + name,
                Behaviour = new XmlData.BehaviourNode { Name = name, Runs = runs, Frequency = 1 },
                Sequence = new XmlData.SequenceNode { RepeatCount = "0", Next = next },
            };
        }

        private static XmlData.AnimationNode TsAct(int id, string name, string type, XmlData.ParamNode[] parameters,
            XmlData.CompositeChildNode[] children, string hotspotBehaviour = null)
        {
            var variant = new XmlData.VariantNode { Pose = new[] { new XmlData.PoseNode { Frame = 0, Ticks = 1 } } };
            if (hotspotBehaviour != null)
                variant.Hotspots = new XmlData.HotspotsNode
                {
                    Hotspot = new[] { new XmlData.HotspotNode { Origin = "0,0", Size = "4,4", Behaviour = hotspotBehaviour } },
                };
            var act = new XmlData.ActNode { Type = type, Params = parameters };
            if (children != null)
                act.Children = new XmlData.ChildrenNode { Mode = type == "Select" ? "select" : "sequence", Child = children };
            else
                act.Variants = new XmlData.VariantsNode { Variant = new[] { variant } };
            return new XmlData.AnimationNode
            {
                Id = id, Name = name, Act = act,
                Sequence = new XmlData.SequenceNode { RepeatCount = "0" },
            };
        }

        private static XmlData.AnimationNode TsEmbedded(int id, string name, string cls, params XmlData.ParamNode[] parameters)
        {
            XmlData.AnimationNode node = TsAct(id, name, "Embedded", parameters, null);
            node.Act.Class = cls;
            return node;
        }

        private static XmlData.ParamNode TsParam(string name, string value, string cache)
        {
            return new XmlData.ParamNode { Name = name, Value = value, Cache = cache };
        }

        private static XmlData.CompositeChildNode TsChild(string animation, params XmlData.BindNode[] binds)
        {
            return new XmlData.CompositeChildNode { Animation = animation, Bind = binds.Length == 0 ? null : binds };
        }

        private static bool TsHasEdge(TickGraph graph, int from, int to, string kind)
        {
            foreach (TickGraphEdge e in graph.EdgesOf(from))
                if (e.To == to && e.Kind == kind) return true;
            return false;
        }

        private static string TsIds(IEnumerable<int> ids)
        {
            var parts = new List<string>();
            foreach (int id in ids) parts.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }
    }
}
