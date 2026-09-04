using System;
using System.Collections.Generic;
using DesktopAICompanion.Tools.ShimejiConvert;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>How a chain gets from one step to the next, in the pet's OWN graph.</summary>
    internal enum ChainLink
    {
        /// <summary>The first step: nothing precedes it.</summary>
        Entry,
        /// <summary>The previous animation lists this one under &lt;next&gt;. The pet does this by itself.</summary>
        Sequence,
        /// <summary>Listed under &lt;border&gt;: natural, but only when the pet reaches a specific edge. A jump
        /// landing is this, and it is why the distinction matters -- a jump ends at its BORDER, long before its
        /// sequence does.</summary>
        Border,
        /// <summary>Listed under &lt;gravity&gt;: natural, but only when nothing is underneath.</summary>
        Gravity,
        /// <summary>A spawned child animation.</summary>
        Child,
        /// <summary>No edge exists. The debugger is inventing this transition.</summary>
        Forced,
    }

    /// <summary>One entry on the timeline: an animation, played <see cref="Repeat"/> times in a row.</summary>
    internal sealed class ChainStep
    {
        public int AnimationId;
        public string Name = "";
        public int Repeat = 1;

        internal ChainStep Copy()
        {
            return new ChainStep { AnimationId = AnimationId, Name = Name, Repeat = Repeat };
        }
    }

    /// <summary>What the connector between two timeline chips says.</summary>
    internal sealed class ChainJoin
    {
        public ChainLink Kind = ChainLink.Forced;
        /// <summary>The matched edge's <c>only=</c> flag, "" when unconditional.</summary>
        public string Only = "";
        /// <summary>The weight the pet's own graph gives this edge, so "natural but 1 in 300" is visible
        /// rather than being reported the same as "natural and always".</summary>
        public int Probability;

        public bool IsNatural { get { return Kind != ChainLink.Forced; } }

        public string Describe()
        {
            switch (Kind)
            {
                case ChainLink.Entry: return "chain entry";
                case ChainLink.Sequence: return "natural: plays next on its own (weight " + Probability + ")";
                case ChainLink.Border:
                    return "natural on contact" + (Only.Length > 0 && Only != "none" ? " (only=" + Only + ")" : "") +
                           ", weight " + Probability;
                case ChainLink.Gravity: return "natural when unsupported (weight " + Probability + ")";
                case ChainLink.Child: return "spawns as a child";
                default: return "FORCED: the companion has no edge here";
            }
        }
    }

    /// <summary>
    /// The behaviour debugger's logic, with no UI in it.
    ///
    /// Two things live here. <see cref="Classify"/> answers what the timeline's connectors say, and
    /// <see cref="BuildDebugXml"/> turns a timeline into a pet the ENGINE can run.
    ///
    /// That second one is the whole design. The obvious way to drive a chain is to fire each step at a live pet
    /// through <c>IHost.TryPlayAnimation</c> and move on after the animation's declared length, and it does not
    /// work: an animation that ends on a BORDER ends early, and by a margin that is not small. Hornet's jump
    /// abandoned 16 of its 28 declared steps, so a duration-based sequencer would start the next step while the
    /// previous one was still on screen and quietly run a different chain than the one being watched. There is
    /// no completion signal in the ABI to wait on instead (<c>CompanionLanded</c> is a one-shot startup event, not
    /// floor contact).
    ///
    /// So the chain is not driven from outside at all. It is COMPILED into a throwaway pet whose animations are
    /// wired nose-to-tail, and handed to <c>ICompanionManager.SpawnPreview</c>. The engine then runs it with its own
    /// timing and its own physics, which is the thing being validated in the first place, and needs no new ABI.
    /// </summary>
    internal static class BehaviourChain
    {
        /// <summary>Timeline chips are cloned into the debug pet, so a chain cannot exceed this. Well above
        /// anything usable by hand, and it bounds the id space and the emitted file.</summary>
        internal const int MaxChainNodes = 64;
        internal const int MaxRepeatPerStep = 32;

        /// <summary>Prefix on every cloned animation's name. It must not collide with the four magic names
        /// (fall / drag / kill / sync): the host resolves those by taking the FIRST animation with that name,
        /// so a clone called "fall" would become the pet's falling animation. The original name is kept as a
        /// suffix so the clone is still identifiable in a report.</summary>
        internal const string ClonePrefix = "dbg";

        /// <summary>
        /// How the pet's own graph gets from <paramref name="from"/> to <paramref name="toId"/>.
        ///
        /// Preference order is sequence, border, gravity, child, and it is not arbitrary: a sequence edge is
        /// the one the pet takes with no help from the world, so if one exists that is the honest answer. Among
        /// edges of one kind, a usable (non-zero) weight wins over a zero-weight one that is written down but
        /// can never be taken.
        /// </summary>
        internal static ChainJoin Classify(AnimNode from, int toId)
        {
            if (from == null) return new ChainJoin { Kind = ChainLink.Forced };
            foreach (string kind in new[] { "sequence", "border", "gravity", "child" })
            {
                AnimEdge best = null;
                foreach (AnimEdge e in from.Edges)
                {
                    if (e == null || e.To != toId || e.Kind != kind) continue;
                    if (best == null || e.Probability > best.Probability) best = e;
                }
                if (best == null) continue;
                return new ChainJoin
                {
                    Kind = kind == "sequence" ? ChainLink.Sequence
                         : kind == "border" ? ChainLink.Border
                         : kind == "gravity" ? ChainLink.Gravity
                         : ChainLink.Child,
                    Only = best.Only ?? "",
                    Probability = best.Probability,
                };
            }
            return new ChainJoin { Kind = ChainLink.Forced };
        }

        /// <summary>The joins for a whole timeline, one per chip (the first is always Entry).</summary>
        internal static List<ChainJoin> Joins(IList<ChainStep> steps, IDictionary<int, AnimNode> nodesById)
        {
            var joins = new List<ChainJoin>();
            if (steps == null) return joins;
            for (int i = 0; i < steps.Count; i++)
            {
                if (i == 0) { joins.Add(new ChainJoin { Kind = ChainLink.Entry }); continue; }
                AnimNode from;
                nodesById.TryGetValue(steps[i - 1].AnimationId, out from);
                joins.Add(Classify(from, steps[i].AnimationId));
            }
            return joins;
        }

        /// <summary>A repeat is a self-transition, so it gets classified too: "does this animation lead back
        /// into itself?" A jump that re-enters itself on landing does, which is exactly what makes
        /// "10x jump back to back" a natural chain rather than a forced one.</summary>
        internal static ChainJoin RepeatJoin(ChainStep step, IDictionary<int, AnimNode> nodesById)
        {
            if (step == null || step.Repeat <= 1) return null;
            AnimNode self;
            nodesById.TryGetValue(step.AnimationId, out self);
            return Classify(self, step.AnimationId);
        }

        /// <summary>
        /// Compile a timeline into a runnable pet.
        ///
        /// Each chip occurrence becomes its own CLONE of the source animation with a fresh id, and every exit
        /// the clone has -- sequence end, border and gravity alike -- is pointed at the next clone. Pointing
        /// all three is what makes the chain deterministic without having to predict which one will fire: an
        /// idle ends at its sequence end, a jump at its border, a walk stepping off a ledge at its gravity
        /// node, and the debugger does not need to know which.
        ///
        /// Cloning rather than rewriting is what keeps the pet honest: the ORIGINAL animations are left exactly
        /// as they are, so the last step can hand back to them and the pet carries on behaving normally, and
        /// nothing being watched has been altered except the joins that were asked for.
        /// </summary>
        internal static string BuildDebugXml(string sourceXml, IList<ChainStep> steps, bool loop, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(sourceXml)) { error = "No companion XML to build from."; return null; }
            if (steps == null || steps.Count == 0) { error = "The timeline is empty."; return null; }

            XmlData.RootNode root;
            string parseError;
            if (!CompanionXmlValidator.TryParse(sourceXml, out root, out parseError))
            {
                error = "The companion XML does not parse: " + parseError;
                return null;
            }
            if (root.Animations == null || root.Animations.Animation == null || root.Animations.Animation.Length == 0)
            {
                error = "The companion has no animations.";
                return null;
            }

            var byId = new Dictionary<int, XmlData.AnimationNode>();
            int maxId = 0;
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
            {
                if (a == null) continue;
                byId[a.Id] = a;
                if (a.Id > maxId) maxId = a.Id;
            }

            // Flatten the timeline: a chip with Repeat = 3 becomes three entries, because a chain of length 3
            // needs three DISTINCT nodes. One node pointed at itself is an infinite loop, not three plays.
            var flat = new List<XmlData.AnimationNode>();
            foreach (ChainStep step in steps)
            {
                XmlData.AnimationNode source;
                if (step == null || !byId.TryGetValue(step.AnimationId, out source))
                {
                    error = "The timeline references animation " + (step == null ? "?" : step.AnimationId.ToString()) +
                            ", which this companion does not have.";
                    return null;
                }
                int repeat = Math.Max(1, Math.Min(MaxRepeatPerStep, step.Repeat));
                for (int i = 0; i < repeat; i++) flat.Add(source);
            }
            if (flat.Count > MaxChainNodes)
            {
                error = "That chain is " + flat.Count + " steps long; the limit is " + MaxChainNodes + ".";
                return null;
            }

            var clones = new List<XmlData.AnimationNode>();
            for (int i = 0; i < flat.Count; i++)
            {
                XmlData.AnimationNode clone = CloneAnimation(flat[i]);
                clone.Id = ++maxId;
                clone.Name = ClonePrefix + (i + 1) + "_" + Sanitize(flat[i].Name);
                clones.Add(clone);
            }

            for (int i = 0; i < clones.Count; i++)
            {
                bool last = i == clones.Count - 1;
                if (!last)
                {
                    PointEveryExitAt(clones[i], clones[i + 1].Id);
                }
                else if (loop)
                {
                    PointEveryExitAt(clones[i], clones[0].Id);
                }
                else
                {
                    // Hand back to the pet's own graph: the last clone keeps the ORIGINAL animation's exits, so
                    // after the chain the pet resumes normal behaviour and what happens next is real behaviour
                    // rather than an artefact of the debug build. It also keeps the original animations
                    // reachable, which a debug pet that dead-ended would not.
                    XmlData.AnimationNode original = flat[i];
                    clones[i].Sequence.Next = CopyEdges(original.Sequence != null ? original.Sequence.Next : null);
                    clones[i].Border = original.Border == null ? null
                        : new XmlData.HitNode { Next = CopyEdges(original.Border.Next) };
                    clones[i].Gravity = original.Gravity == null ? null
                        : new XmlData.HitNode { Next = CopyEdges(original.Gravity.Next) };
                }
            }

            var animations = new List<XmlData.AnimationNode>(root.Animations.Animation);
            animations.AddRange(clones);
            root.Animations.Animation = animations.ToArray();

            // ONE spawn, straight into the first step, standing on the floor. The pet's own spawns are replaced
            // rather than added to: half of them drop the pet in from above, and watching a chain start after a
            // four-second fall is not watching the chain.
            root.Spawns = new XmlData.SpawnsNode
            {
                Spawn = new[]
                {
                    new XmlData.SpawnNode
                    {
                        Id = 1,
                        Probability = 100,
                        X = "random*(screenW-imageW-50)/100+25",
                        Y = "areaH-imageH",
                        Next = new XmlData.NextNode { Value = clones[0].Id, Probability = 100 },
                    },
                },
            };

            string built = ShimejiEngine.Serialize(root);
            XmlData.RootNode reparsed;
            string checkError;
            if (!CompanionXmlValidator.TryParse(built, out reparsed, out checkError))
            {
                error = "The debug companion did not validate: " + checkError;
                return null;
            }
            return built;
        }

        /// <summary>
        /// Route every way out of an animation to one target.
        ///
        /// The gravity node is REPLACED, never removed. Its presence is what makes the engine drop an
        /// unsupported pet, so deleting it to simplify the wiring would leave the pet hanging in mid-air the
        /// moment it walked off something -- and an animation that had none keeps none, because adding one
        /// would make a wall or ceiling pose fall off the wall it is supposed to be clinging to.
        /// </summary>
        private static void PointEveryExitAt(XmlData.AnimationNode node, int targetId)
        {
            XmlData.NextNode[] one = new[] { new XmlData.NextNode { Value = targetId, Probability = 100, OnlyFlag = "none" } };
            if (node.Sequence == null) node.Sequence = new XmlData.SequenceNode { RepeatCount = "0", Frame = new[] { 0 } };
            node.Sequence.Next = one;
            node.Border = new XmlData.HitNode { Next = one };
            if (node.Gravity != null) node.Gravity = new XmlData.HitNode { Next = one };
        }

        private static XmlData.NextNode[] CopyEdges(XmlData.NextNode[] source)
        {
            if (source == null) return new XmlData.NextNode[0];
            var copy = new List<XmlData.NextNode>(source.Length);
            foreach (XmlData.NextNode n in source)
                if (n != null)
                    copy.Add(new XmlData.NextNode { Value = n.Value, Probability = n.Probability, OnlyFlag = n.OnlyFlag });
            return copy.ToArray();
        }

        private static XmlData.AnimationNode CloneAnimation(XmlData.AnimationNode source)
        {
            var clone = new XmlData.AnimationNode
            {
                Id = source.Id,
                Name = source.Name,
                Start = CloneMoving(source.Start),
                End = CloneMoving(source.End),
                Sequence = new XmlData.SequenceNode
                {
                    RepeatFromFrame = source.Sequence != null ? source.Sequence.RepeatFromFrame : 0,
                    RepeatCount = source.Sequence != null ? source.Sequence.RepeatCount : "0",
                    Frame = source.Sequence != null && source.Sequence.Frame != null
                        ? (int[])source.Sequence.Frame.Clone() : new[] { 0 },
                    Action = source.Sequence != null ? source.Sequence.Action : null,
                    Next = new XmlData.NextNode[0],
                },
            };
            if (source.Border != null) clone.Border = new XmlData.HitNode { Next = new XmlData.NextNode[0] };
            if (source.Gravity != null) clone.Gravity = new XmlData.HitNode { Next = new XmlData.NextNode[0] };
            return clone;
        }

        private static XmlData.MovingNode CloneMoving(XmlData.MovingNode source)
        {
            if (source == null) return new XmlData.MovingNode { X = "0", Y = "0", Interval = "200", Opacity = 1.0 };
            return new XmlData.MovingNode
            {
                X = source.X,
                Y = source.Y,
                OffsetY = source.OffsetY,
                Opacity = source.Opacity,
                Interval = source.Interval,
            };
        }

        /// <summary>A clone name the validator will accept: letters, digits and underscores, bounded. Also the
        /// guard against a clone inheriting a magic name, since the prefix is always prepended.</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "anim";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                if (sb.Length >= 24) break;
            }
            string cleaned = sb.ToString().Trim('_');
            return cleaned.Length == 0 ? "anim" : cleaned;
        }
    }
}
