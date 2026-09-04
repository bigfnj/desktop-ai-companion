using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>
    /// Which animations in a pet XML can actually be reached, and therefore which ones will never play.
    ///
    /// This is an authoring-quality check rather than a safety one: an unreachable animation is not
    /// dangerous, it is wasted work by the pet's author, and the only way to notice it is to walk the graph.
    /// The walk lived inside the PetTester tool's WinForms form, fused to the checkboxes and text box it
    /// painted its results into, which is why it was never gated by CI and never available to anything else.
    /// It lives here now as a pure function over the parsed XML, so the host's own --security-selftest can
    /// assert its two subtle rules, and so a future pet-authoring module can source-link this file and give
    /// the author the same report the tool used to.
    ///
    /// The two rules that are easy to get wrong, and that the self-test pins:
    ///
    /// 1. A &lt;child&gt; edge does NOT make its target reachable on its own. A child only spawns once its
    ///    PARENT animation actually runs, so child edges are deferred into a side map and drained only when
    ///    the parent is dequeued. Seeding them as roots would declare half a broken pet reachable.
    /// 2. A transition with probability 0 is NOT an edge. It is written in the XML but can never be taken,
    ///    so following it would mask exactly the dead animation the author wants to hear about.
    /// </summary>
    internal static class AnimationReachability
    {
        /// <summary>
        /// The ids of animations that can never play, in document order. Empty when everything is reachable
        /// (or when the XML has no animations to walk).
        ///
        /// Roots are the four animations the engine can enter directly (drag / fall / kill / sync) plus the
        /// target of every spawn with a non-zero probability -- the same entry points the runtime itself uses.
        /// </summary>
        internal static List<int> FindUnreachable(XmlData.RootNode root, Animations animations)
        {
            var unreachable = new List<int>();
            if (root == null || root.Animations == null || root.Animations.Animation == null) return unreachable;

            var animationIds = new HashSet<int>();
            foreach (XmlData.AnimationNode animation in root.Animations.Animation)
                if (animation != null) animationIds.Add(animation.Id);
            if (animationIds.Count == 0) return unreachable;

            var byId = new Dictionary<int, XmlData.AnimationNode>();
            foreach (XmlData.AnimationNode animation in root.Animations.Animation)
                if (animation != null && !byId.ContainsKey(animation.Id)) byId[animation.Id] = animation;

            var reachable = new HashSet<int>();
            var pending = new Queue<int>();

            if (animations != null)
                foreach (int entry in new[]
                    {
                        animations.AnimationDrag,
                        animations.AnimationFall,
                        animations.AnimationKill,
                        animations.AnimationSync,
                    })
                    if (animationIds.Contains(entry) && reachable.Add(entry)) pending.Enqueue(entry);

            if (root.Spawns != null && root.Spawns.Spawn != null)
                foreach (XmlData.SpawnNode spawn in root.Spawns.Spawn)
                    if (spawn != null && spawn.Probability > 0 && spawn.Next != null &&
                        animationIds.Contains(spawn.Next.Value) && reachable.Add(spawn.Next.Value))
                        pending.Enqueue(spawn.Next.Value);

            // Rule 1: children are parent-gated, so they wait here until their parent is actually reached.
            var childTargets = new Dictionary<int, List<int>>();
            if (root.Childs != null && root.Childs.Child != null)
                foreach (XmlData.ChildNode child in root.Childs.Child)
                {
                    if (child == null) continue;
                    if (!animationIds.Contains(child.Id) || !animationIds.Contains(child.Next)) continue;
                    List<int> targets;
                    if (!childTargets.TryGetValue(child.Id, out targets))
                    {
                        targets = new List<int>();
                        childTargets.Add(child.Id, targets);
                    }
                    targets.Add(child.Next);
                }

            while (pending.Count > 0)
            {
                int id = pending.Dequeue();
                XmlData.AnimationNode animation;
                if (byId.TryGetValue(id, out animation) && animation != null)
                {
                    Follow(animation.Gravity == null ? null : animation.Gravity.Next, reachable, pending);
                    Follow(animation.Border == null ? null : animation.Border.Next, reachable, pending);
                    Follow(animation.Sequence == null ? null : animation.Sequence.Next, reachable, pending);
                }

                List<int> children;
                if (childTargets.TryGetValue(id, out children))
                    foreach (int target in children)
                        if (reachable.Add(target)) pending.Enqueue(target);
            }

            foreach (XmlData.AnimationNode animation in root.Animations.Animation)
                if (animation != null && !reachable.Contains(animation.Id))
                    unreachable.Add(animation.Id);
            return unreachable;
        }

        /// <summary>Rule 2: a zero-probability transition is written but can never be taken, so it is not an
        /// edge. Following it would hide the very animation the author needs to hear about.</summary>
        private static void Follow(XmlData.NextNode[] transitions, HashSet<int> reachable, Queue<int> pending)
        {
            if (transitions == null) return;
            foreach (XmlData.NextNode transition in transitions)
            {
                if (transition == null || transition.Probability <= 0) continue;
                if (reachable.Add(transition.Value)) pending.Enqueue(transition.Value);
            }
        }
    }
}
