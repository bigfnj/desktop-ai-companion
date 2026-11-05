using System;
using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>One edge of a tick-clock companion's graph: where it leads, its weight (100 for a structural edge), and
    /// which of <see cref="TickReachability"/>'s kinds it is.</summary>
    internal sealed class TickGraphEdge
    {
        public int To;
        public int Probability;
        public string Kind = "";
    }

    /// <summary>A tick-clock companion's graph as <see cref="TickReachability.Build"/> reads it.</summary>
    internal sealed class TickGraph
    {
        /// <summary>Where the runtime enters by itself, in document order: the route, then the behaviours it enters by
        /// name on its own events, then the startup behaviour.</summary>
        public readonly List<int> Roots = new List<int>();
        /// <summary>The behaviours the runtime enters by name on its own events (Fall, Dragged, Thrown).</summary>
        public readonly HashSet<int> EngineEntries = new HashSet<int>();
        /// <summary>Every node's outgoing edges, zero-weight ones included (a reader still wants to see them).</summary>
        public readonly Dictionary<int, List<TickGraphEdge>> Edges = new Dictionary<int, List<TickGraphEdge>>();
        public readonly HashSet<int> Reachable = new HashSet<int>();

        public List<TickGraphEdge> EdgesOf(int id)
        {
            List<TickGraphEdge> list;
            return Edges.TryGetValue(id, out list) ? list : new List<TickGraphEdge>();
        }
    }

    /// <summary>
    /// Which nodes of a tick-clock companion (grimoire/06 section 4) can ever run.
    ///
    /// The classic walk follows sequence, border and gravity edges from the spawns and the four magic names, and a
    /// tick-clock document joins its nodes differently: the route lists the root behaviours, a behaviour names the
    /// action it runs, a composite names its children, a hotspot names a behaviour. Walked the classic way every action
    /// and most behaviours of one read as never played. These are the edges the runtime itself follows
    /// (src/dotNet/Shimeji/TickRuntime.cs and TickModel.cs):
    ///
    /// - route -> each root behaviour its list can select (<see cref="RouteEdge"/>);
    /// - behaviour -> each behaviour the NEXT selection can add (<see cref="NextEdge"/>). The runtime looks the list up
    ///   by the ending behaviour's NAME among the route's behaviours (TickWorld.BuildNextBehaviour), so an off-route
    ///   next-list entry continues with its root namesake's list, not with edges of its own;
    /// - behaviour -> the action it runs (<see cref="RunsEdge"/>);
    /// - composite action -> each child (<see cref="ChildEdge"/>);
    /// - action -> the behaviour a hotspot of one of its variants enters (<see cref="HotspotEdge"/>);
    /// - action or binding -> the behaviour a LITERAL Behavior, TargetBehavior, BornBehavior or TransformBehavior names
    ///   (<see cref="NamedEdge"/>); BornBehavior and TransformBehavior only when no BornMascot or TransformMascot sends
    ///   the companion to another pack member. A scripted name cannot be read here and adds no edge.
    ///
    /// Roots are the route, the behaviours the runtime enters by name on its own events (<see cref="EngineBehaviours"/>),
    /// and the startup behaviour when it is this member's. The classic walk's second rule holds: a zero-weight route or
    /// next-list entry can never be selected, so it is listed and not followed. Edge conditions (when) and the user's
    /// behaviour toggles are not evaluated: this answers "can it ever run", as the classic walk does.
    ///
    /// Pure over the parsed DTOs and C# 7.3, so CoreTests compiles it. AnimationReachability, PetGraph and Companion
    /// Studio's analyzer call it for a document whose clock is tick and for nothing else, so no classic result changes.
    /// </summary>
    internal static class TickReachability
    {
        public const string RouteEdge = "route";
        public const string NextEdge = "next";
        public const string RunsEdge = "runs";
        public const string ChildEdge = "composite";
        public const string HotspotEdge = "hotspot";
        public const string NamedEdge = "enters";

        /// <summary>The behaviours the runtime enters by name on its own events: Fall when the ground is lost or the
        /// companion drops in, Dragged on a press, Thrown on the release that ends a drag (TickBehaviour.Next,
        /// MousePressed and MouseReleased; grimoire/06 4.3).</summary>
        public static readonly string[] EngineBehaviours = { "Fall", "Dragged", "Thrown" };

        /// <summary>The embedded classes that read each behaviour-naming parameter (TickSocialFamily.Arrive and
        /// TickInteract, the Breed family through TickBreedDelegate, TickTransform).</summary>
        private static readonly string[] ScanClasses = { "ScanMove", "ScanJump", "ScanInteract", "Interact", "ComplexMove", "ComplexJump" };
        private static readonly string[] BreedClasses = { "Breed", "BreedMove", "BreedJump", "BreedFall", "ComplexMove", "ComplexJump" };
        private static readonly string[] TransformClasses = { "Transform" };

        /// <summary>The ids of nodes that can never run, in document order.</summary>
        public static List<int> FindUnreachable(XmlData.RootNode root)
        {
            var unreachable = new List<int>();
            if (root == null || root.Animations == null || root.Animations.Animation == null) return unreachable;
            TickGraph graph = Build(root);
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
                if (a != null && !graph.Reachable.Contains(a.Id)) unreachable.Add(a.Id);
            return unreachable;
        }

        public static TickGraph Build(XmlData.RootNode root)
        {
            var graph = new TickGraph();
            if (root == null || root.Animations == null || root.Animations.Animation == null) return graph;
            XmlData.AnimationNode[] nodes = root.Animations.Animation;

            var byId = new Dictionary<int, XmlData.AnimationNode>();
            var actionByName = new Dictionary<string, XmlData.AnimationNode>(StringComparer.Ordinal);
            foreach (XmlData.AnimationNode a in nodes)
            {
                if (a == null) continue;
                if (!byId.ContainsKey(a.Id)) byId[a.Id] = a;
                if (a.Act != null && a.Name != null && !actionByName.ContainsKey(a.Name)) actionByName[a.Name] = a;
            }

            // The root behaviours by name, as TickType.Compile builds RootByName: the route's edges in order, zero-weight
            // ones included, the first edge naming a behaviour winning.
            var rootByName = new Dictionary<string, XmlData.AnimationNode>(StringComparer.Ordinal);
            foreach (XmlData.AnimationNode a in nodes)
            {
                if (a == null || a.Route == null) continue;
                foreach (XmlData.NextNode n in SequenceEdges(a))
                {
                    XmlData.AnimationNode target;
                    if (byId.TryGetValue(n.Value, out target) && target.Behaviour != null && target.Behaviour.Name != null
                        && !rootByName.ContainsKey(target.Behaviour.Name))
                        rootByName[target.Behaviour.Name] = target;
                }
            }

            foreach (XmlData.AnimationNode a in nodes)
            {
                if (a == null || graph.Edges.ContainsKey(a.Id)) continue;
                var edges = new List<TickGraphEdge>();
                graph.Edges[a.Id] = edges;
                if (a.Route != null)
                {
                    foreach (XmlData.NextNode n in SequenceEdges(a)) Add(edges, n.Value, n.Probability, RouteEdge);
                }
                else if (a.Behaviour != null)
                {
                    XmlData.AnimationNode listOwner;
                    if (a.Behaviour.Name == null || !rootByName.TryGetValue(a.Behaviour.Name, out listOwner)) listOwner = a;
                    foreach (XmlData.NextNode n in SequenceEdges(listOwner)) Add(edges, n.Value, n.Probability, NextEdge);
                    XmlData.AnimationNode runs;
                    if (a.Behaviour.Runs != null && actionByName.TryGetValue(a.Behaviour.Runs, out runs))
                        Add(edges, runs.Id, 100, RunsEdge);
                    XmlData.AnimationNode bound;
                    actionByName.TryGetValue(a.Behaviour.Runs ?? "", out bound);
                    AddNamedByBinds(edges, a.Behaviour.Bind, bound, rootByName);
                }
                else if (a.Act != null)
                {
                    XmlData.ActNode act = a.Act;
                    if (act.Children != null && act.Children.Child != null)
                        foreach (XmlData.CompositeChildNode c in act.Children.Child)
                        {
                            XmlData.AnimationNode child;
                            if (c == null || c.Animation == null || !actionByName.TryGetValue(c.Animation, out child)) continue;
                            Add(edges, child.Id, 100, ChildEdge);
                            AddNamedByBinds(edges, c.Bind, child, rootByName);
                        }
                    if (act.Variants != null && act.Variants.Variant != null)
                        foreach (XmlData.VariantNode v in act.Variants.Variant)
                            if (v != null && v.Hotspots != null && v.Hotspots.Hotspot != null)
                                foreach (XmlData.HotspotNode h in v.Hotspots.Hotspot)
                                {
                                    XmlData.AnimationNode entered;
                                    if (h != null && h.Behaviour != null && rootByName.TryGetValue(h.Behaviour, out entered))
                                        Add(edges, entered.Id, 100, HotspotEdge);
                                }
                    AddNamedByParams(edges, act, rootByName);
                }
            }

            foreach (XmlData.AnimationNode a in nodes)
                if (a != null && a.Route != null) AddRoot(graph, a.Id);
            foreach (string name in EngineBehaviours)
            {
                XmlData.AnimationNode entry;
                if (!rootByName.TryGetValue(name, out entry)) continue;
                graph.EngineEntries.Add(entry.Id);
                AddRoot(graph, entry.Id);
            }
            XmlData.HeaderNode header = root.Header;
            if (header != null && header.Startup != null && header.Startup.Behaviour != null
                && (header.Pack == null || string.Equals(header.Startup.Member, header.Pack.Member, StringComparison.Ordinal)))
            {
                XmlData.AnimationNode start;
                if (rootByName.TryGetValue(header.Startup.Behaviour, out start)) AddRoot(graph, start.Id);
            }

            var pending = new Queue<int>();
            foreach (int id in graph.Roots)
                if (byId.ContainsKey(id) && graph.Reachable.Add(id)) pending.Enqueue(id);
            while (pending.Count > 0)
            {
                foreach (TickGraphEdge e in graph.EdgesOf(pending.Dequeue()))
                {
                    if (e.Probability <= 0 || !byId.ContainsKey(e.To)) continue;
                    if (graph.Reachable.Add(e.To)) pending.Enqueue(e.To);
                }
            }
            return graph;
        }

        private static IEnumerable<XmlData.NextNode> SequenceEdges(XmlData.AnimationNode a)
        {
            if (a.Sequence == null || a.Sequence.Next == null) yield break;
            foreach (XmlData.NextNode n in a.Sequence.Next) if (n != null) yield return n;
        }

        private static void Add(List<TickGraphEdge> edges, int to, int probability, string kind)
        {
            edges.Add(new TickGraphEdge { To = to, Probability = probability, Kind = kind });
        }

        private static void AddRoot(TickGraph graph, int id)
        {
            if (!graph.Roots.Contains(id)) graph.Roots.Add(id);
        }

        /// <summary>An action's own literal behaviour names, for the classes that read them.</summary>
        private static void AddNamedByParams(List<TickGraphEdge> edges, XmlData.ActNode act, Dictionary<string, XmlData.AnimationNode> rootByName)
        {
            if (act.Params == null || !string.Equals(act.Type, "Embedded", StringComparison.Ordinal)) return;
            string cls = act.Class ?? "";
            foreach (XmlData.ParamNode p in act.Params)
            {
                if (p == null || p.Name == null || IsScripted(p.Cache)) continue;
                bool read =
                    ((p.Name == "Behavior" || p.Name == "TargetBehavior") && Array.IndexOf(ScanClasses, cls) >= 0)
                    || (p.Name == "BornBehavior" && Array.IndexOf(BreedClasses, cls) >= 0 && !HasParam(act, "BornMascot"))
                    || (p.Name == "TransformBehavior" && Array.IndexOf(TransformClasses, cls) >= 0 && !HasParam(act, "TransformMascot"));
                XmlData.AnimationNode entered;
                if (read && p.Value != null && rootByName.TryGetValue(p.Value, out entered)) Add(edges, entered.Id, 100, NamedEdge);
            }
        }

        /// <summary>A binding's literal behaviour names. A binding reaches the action it is written for and, through a
        /// composite, that action's children, so the class is not checked; a binding is written for an action that
        /// reads it.</summary>
        private static void AddNamedByBinds(List<TickGraphEdge> edges, XmlData.BindNode[] binds, XmlData.AnimationNode boundAction,
            Dictionary<string, XmlData.AnimationNode> rootByName)
        {
            if (binds == null) return;
            XmlData.ActNode act = boundAction == null ? null : boundAction.Act;
            foreach (XmlData.BindNode b in binds)
            {
                if (b == null || b.Name == null || IsScripted(b.Cache)) continue;
                bool read = b.Name == "Behavior" || b.Name == "TargetBehavior"
                    || (b.Name == "BornBehavior" && !Binds(binds, "BornMascot") && !(act != null && HasParam(act, "BornMascot")))
                    || (b.Name == "TransformBehavior" && !Binds(binds, "TransformMascot") && !(act != null && HasParam(act, "TransformMascot")));
                XmlData.AnimationNode entered;
                if (read && b.Value != null && rootByName.TryGetValue(b.Value, out entered)) Add(edges, entered.Id, 100, NamedEdge);
            }
        }

        private static bool HasParam(XmlData.ActNode act, string name)
        {
            if (act.Params == null) return false;
            foreach (XmlData.ParamNode p in act.Params)
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool Binds(XmlData.BindNode[] binds, string name)
        {
            foreach (XmlData.BindNode b in binds)
                if (b != null && string.Equals(b.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsScripted(string cache)
        {
            return string.Equals(cache, "action", StringComparison.Ordinal) || string.Equals(cache, "frame", StringComparison.Ordinal);
        }
    }
}
