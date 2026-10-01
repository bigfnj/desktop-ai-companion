using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>One outgoing transition from an animation: the target id, its probability, and where it comes
    /// from (a sequence end, a border/gravity reaction, or a spawned child). A zero-probability edge is kept
    /// because an author still wants to see it — it is written in the XML but can never be taken.</summary>
    internal sealed class AnimEdge
    {
        public int To;
        public int Probability;
        public string Kind = "";   // sequence | border | gravity | child
        /// <summary>The edge's <c>only=</c> flag, or "" for an unconditional one. Load-bearing for a BORDER
        /// edge and for nothing else: "the companion does this by itself" and "the pet does this when it lands on
        /// the taskbar" are different claims, and a timeline that showed them the same colour would say a
        /// forced chain was natural.</summary>
        public string Only = "";
    }

    /// <summary>One animation as the map draws it and the detail panel inspects it: its id and name, the two
    /// facts that pick its colour (root / reachable), the sprite frames it plays, and where it can go next.</summary>
    internal sealed class AnimNode
    {
        public int Id;
        public string Name = "";
        public bool IsRoot;
        public bool IsReachable;
        public int[] Frames = System.Array.Empty<int>();
        public string Action = "";
        public readonly List<AnimEdge> Edges = new List<AnimEdge>();

        // The physics, which is what decides what an animation actually DOES. The map used to show only a
        // name and a reachability colour, so finding the jump in a converted pet meant knowing that a Hollow
        // Knight skin calls it "Grapple4" -- the names are the source skin's and span five languages.
        public int StartX, StartY, EndX, EndY;
        /// <summary>Whether the animation carries a &lt;gravity&gt; element, which is what makes the engine drop
        /// an unsupported pet. Necessary for holding a surface (a pose that can fall is not clinging) and NOT
        /// sufficient: which poses ARE on a wall or ceiling is decided by AnimCapabilities.SurfacePoses, from the
        /// only="vertical"/"horizontal" border edges that put the pet there and the graph grown from them. The
        /// bundled pet omits gravity on 50 of its 54 animations, floor idles included, so reading the absence as
        /// a cling labelled 41 of them wall poses (F150); this summary said exactly that until 1.1.18 (RA-136).</summary>
        public bool HasGravity;
        /// <summary>The runtime bound this animation as its fall, drag, kill or sync, so the ENGINE starts it
        /// itself: a drop, a mouse drag, a removal, a multi-companion sync. Read from the same staged Animations
        /// the roots come from -- Xml.LoadAnimations binds the LAST exact name (no Trim, no case folding) and
        /// ResolveMagicAnimations then falls back for fall and drag only (the lowest id whose name contains the
        /// word ignoring case, else the lowest id) -- and never re-derived here from the name. A case-insensitive
        /// copy of the rule badged a hand-authored 'Kill', which the host never binds, as ENGINE and cut the
        /// surface growth at it (RA-128); copying the fallback instead is the drift the source-link exists to
        /// prevent (F155).</summary>
        public bool IsEngineEntry;
        /// <summary>A velocity written as an expression (<c>random*...</c>, <c>screenW</c>) rather than a
        /// number. The hand-authored pets use these; a converted pet never does. Flagged rather than silently
        /// read as 0, because 0 means "does not move" and would mislabel the animation.</summary>
        public bool VelocityIsExpression;
    }

    /// <summary>The result of analysing one pet XML: does it load, and what will misbehave if it does.</summary>
    internal sealed class PetReport
    {
        /// <summary>False when the pet would be REJECTED by the host outright (schema, limits, unsafe
        /// expressions). Warnings do not affect this: a pet with dead animations still runs.</summary>
        public bool IsValid;

        /// <summary>Why it was rejected, or "" when it validates.</summary>
        public string Error = "";

        /// <summary>Ids of animations that can never play. Not fatal, but almost always a mistake, and
        /// invisible without walking the graph.</summary>
        public readonly List<int> UnreachableAnimations = new List<int>();

        /// <summary>Every animation, in document order, with the facts the map colours by. Empty when the
        /// pet could not be staged (reachability is advisory — see the catch in Analyze).</summary>
        public readonly List<AnimNode> Nodes = new List<AnimNode>();

        /// <summary>The sprite sheet: a base64 PNG cut into a TilesX×TilesY grid, with one colour keyed out
        /// as transparent. The detail panel decodes this to show an animation's actual frames.</summary>
        public string SpritePngBase64 = "";
        public int TilesX;
        public int TilesY;
        public string TransparencyColor = "";

        public int AnimationCount;
        public int SpawnCount;
        public int ChildCount;
        public string PetName = "";
        public string Author = "";

        /// <summary>
        /// What the reachability stage did, recorded so the module self-test can prove BUG-012 stays fixed
        /// rather than trusting the comment that says so: how many sprite frames the staged Xml decoded (0
        /// is the whole point), and whether the graph it loaded IS the validator's own parse (a second parse
        /// would be a different object). Diagnostics; nothing renders them. -1 until the stage ran.
        /// </summary>
        internal int StagedSpriteFrames = -1;
        internal bool StagedFromParsedGraph;

        /// <summary>A human-readable report, which is also exactly what the self-test asserts on.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            if (!IsValid)
            {
                sb.AppendLine("REJECTED — this companion would not load:");
                sb.AppendLine("  " + Error);
                return sb.ToString();
            }

            sb.AppendLine("Valid companion" +
                (PetName.Length > 0 ? " — " + PetName : "") +
                (Author.Length > 0 ? " by " + Author : ""));
            sb.AppendLine("  " + AnimationCount + " animations, " + SpawnCount + " spawns, " +
                ChildCount + " children");

            if (UnreachableAnimations.Count == 0)
            {
                sb.AppendLine("  every animation is reachable");
                return sb.ToString();
            }

            sb.AppendLine("  " + UnreachableAnimations.Count + " animation(s) can NEVER play:");
            foreach (int id in UnreachableAnimations)
                sb.AppendLine("    animation " + id + " is never reached");
            sb.AppendLine("  (an animation is reachable from drag/fall/kill/sync, from a spawn with a " +
                "non-zero probability, from a transition with a non-zero probability, or from a child whose " +
                "PARENT animation is itself reachable)");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Analyses a pet XML with the HOST's own parser, validator and reachability walk (all source-linked
    /// into this module), so the verdict here is exactly the verdict the pet will get when it runs.
    ///
    /// Deliberately UI-free: the window renders what this returns, and the module self-test drives it
    /// directly. That separation is the lesson from the tool this replaces, whose analysis lived inside a
    /// WinForms form and so could never be tested or reused.
    /// </summary>
    internal static class PetAnalyzer
    {
        internal static PetReport Analyze(string animationsXml)
        {
            var report = new PetReport();
            if (string.IsNullOrWhiteSpace(animationsXml))
            {
                report.Error = "No companion XML was supplied.";
                return report;
            }

            XmlData.RootNode root;
            string error;
            if (!CompanionXmlValidator.TryParse(animationsXml, out root, out error))
            {
                report.Error = string.IsNullOrEmpty(error) ? "The companion XML could not be parsed." : error;
                return report;
            }

            report.IsValid = true;
            if (root.Header != null)
            {
                report.PetName = root.Header.Petname ?? "";
                report.Author = root.Header.Author ?? "";
            }
            if (root.Image != null)
            {
                report.SpritePngBase64 = root.Image.Png ?? "";
                report.TilesX = root.Image.TilesX;
                report.TilesY = root.Image.TilesY;
                report.TransparencyColor = root.Image.Transparency ?? "";
            }
            if (root.Animations != null && root.Animations.Animation != null)
                report.AnimationCount = root.Animations.Animation.Length;
            if (root.Spawns != null && root.Spawns.Spawn != null)
                report.SpawnCount = root.Spawns.Spawn.Length;
            if (root.Childs != null && root.Childs.Child != null)
                report.ChildCount = root.Childs.Child.Length;

            // The reachability walk needs the runtime's own view of the entry animations (drag/fall/kill/
            // sync), which only exists once the graph is loaded into an Xml + Animations pair -- the same
            // LoadAnimations (and, inside it, ResolveMagicAnimations) the host runs before it will run a pet.
            //
            // BUG-012 (F155). Until 1.1.18 this block staged the pet with xml.TryReadXml(animationsXml, ...),
            // which is the host's WHOLE loader: it parsed and XSD-validated the text a second time,
            // base64-decoded the sheet again, decoded it with GDI+, cut it into up to 1,024 tile bitmaps,
            // wrapped them in a SpriteFrameStore and disposed the lot at the end of the using -- once per
            // analyze, i.e. once per 750 ms typing pause, on the dispatcher thread, for a result that read
            // four ints out of `animations`. The backlog had recorded "there is no second decode" from the
            // string copy above this comment; the decode was here. The staged Xml now ADOPTS the graph the
            // validator handed back at the top of this method (one parse, the host's own) and loads the
            // animations from it. Nothing about the sheet is touched, and SpriteCount stays 0. An Xml staged
            // this way carries no frame and must never be handed to a running companion; here it lives for
            // one walk.
            //
            // Rejected: TryReadXml's stageImages:false overload, which the host grew for exactly this. It
            // decodes no tile, but it still parses and validates the same text a second time, and the
            // validator's proof of the sheet is itself a full GDI+ decode (Image.FromStream with
            // validateImageData), so it would have left two sheet decodes per analyze where one is owed.
            // Rejected too: resolving drag/fall/kill/sync in this module. ResolveMagicAnimations has a
            // three-step rule (the exact name, then a name containing the word, then the lowest id), and a
            // copy of it here is exactly the drift that source-linking the host's files exists to prevent.
            try
            {
                using (var xml = new Xml(1))
                using (var animations = new Animations(xml))
                {
                    xml.AnimationXML = root;
                    xml.LoadAnimations(animations);
                    report.StagedSpriteFrames = xml.SpriteCount;
                    report.StagedFromParsedGraph = ReferenceEquals(xml.AnimationXML, root);
                    List<int> dead = AnimationReachability.FindUnreachable(root, animations);
                    report.UnreachableAnimations.AddRange(dead);
                    BuildNodes(report, root, animations, dead);
                }
            }
            catch (Exception)
            {
                // Reachability is advisory. A pet that validates but whose graph cannot be loaded is still
                // reported as valid, because the host's own answer to "will this load" is the validator,
                // not this walk.
            }

            return report;
        }

        /// <summary>Fill report.Nodes with one entry per animation, coloured by root/reachable. The dead set
        /// comes straight from AnimationReachability so the map can never contradict the verdict; roots are
        /// recomputed with that walk's exact seeding rule (drag/fall/kill/sync, plus a spawn target with a
        /// non-zero probability) so a root chip and a reachable chip mean what the runtime means.</summary>
        private static void BuildNodes(PetReport report, XmlData.RootNode root, Animations animations, List<int> dead)
        {
            if (root.Animations == null || root.Animations.Animation == null) return;

            var deadSet = new HashSet<int>(dead);
            var ids = new HashSet<int>();
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
                if (a != null) ids.Add(a.Id);

            // The four engine entries, as the runtime bound them (-1 for kill or sync when no exact name was
            // declared; fall and drag always resolve to something once any animation exists). They are the
            // ENGINE half of the roots, and AnimNode.IsEngineEntry carries them on their own so the capability
            // map can tell "the engine starts this one" from "a spawn enters here" (RA-128).
            var entries = new HashSet<int>();
            if (animations != null)
                foreach (int entry in new[]
                    { animations.AnimationDrag, animations.AnimationFall, animations.AnimationKill, animations.AnimationSync })
                    if (ids.Contains(entry)) entries.Add(entry);
            var roots = new HashSet<int>(entries);
            if (root.Spawns != null && root.Spawns.Spawn != null)
                foreach (XmlData.SpawnNode spawn in root.Spawns.Spawn)
                    if (spawn != null && spawn.Probability > 0 && spawn.Next != null && ids.Contains(spawn.Next.Value))
                        roots.Add(spawn.Next.Value);

            // Children are keyed by their PARENT animation id: when the parent runs, the child spawns.
            var childrenByParent = new Dictionary<int, List<int>>();
            if (root.Childs != null && root.Childs.Child != null)
                foreach (XmlData.ChildNode child in root.Childs.Child)
                {
                    if (child == null || !ids.Contains(child.Id)) continue;
                    List<int> list;
                    if (!childrenByParent.TryGetValue(child.Id, out list))
                        childrenByParent[child.Id] = list = new List<int>();
                    list.Add(child.Next);
                }

            foreach (XmlData.AnimationNode a in root.Animations.Animation)
            {
                if (a == null) continue;
                var node = new AnimNode
                {
                    Id = a.Id,
                    Name = a.Name ?? "",
                    IsRoot = roots.Contains(a.Id),
                    IsEngineEntry = entries.Contains(a.Id),
                    IsReachable = !deadSet.Contains(a.Id),
                    Frames = a.Sequence != null && a.Sequence.Frame != null ? a.Sequence.Frame : System.Array.Empty<int>(),
                    Action = a.Sequence != null ? (a.Sequence.Action ?? "") : "",
                    HasGravity = a.Gravity != null,
                };
                bool numeric = true;
                node.StartX = Velocity(a.Start, true, ref numeric);
                node.StartY = Velocity(a.Start, false, ref numeric);
                node.EndX = Velocity(a.End, true, ref numeric);
                node.EndY = Velocity(a.End, false, ref numeric);
                node.VelocityIsExpression = !numeric;
                AddEdges(node, a.Sequence != null ? a.Sequence.Next : null, "sequence");
                AddEdges(node, a.Border != null ? a.Border.Next : null, "border");
                AddEdges(node, a.Gravity != null ? a.Gravity.Next : null, "gravity");
                List<int> spawned;
                if (childrenByParent.TryGetValue(a.Id, out spawned))
                    foreach (int childNext in spawned)
                        node.Edges.Add(new AnimEdge { To = childNext, Probability = 100, Kind = "child" });
                report.Nodes.Add(node);
            }
        }

        /// <summary>One velocity component, or 0 with <paramref name="numeric"/> cleared when it is an
        /// expression. An absent node reads as 0 and is NOT an expression: no value written means no motion.</summary>
        private static int Velocity(XmlData.MovingNode moving, bool wantX, ref bool numeric)
        {
            string raw = moving == null ? null : (wantX ? moving.X : moving.Y);
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            int parsed;
            if (int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                return parsed;
            numeric = false;
            return 0;
        }

        private static void AddEdges(AnimNode node, XmlData.NextNode[] transitions, string kind)
        {
            if (transitions == null) return;
            foreach (XmlData.NextNode t in transitions)
                if (t != null)
                    node.Edges.Add(new AnimEdge
                    {
                        To = t.Value,
                        Probability = t.Probability,
                        Kind = kind,
                        Only = t.OnlyFlag ?? "",
                    });
        }
    }
}
