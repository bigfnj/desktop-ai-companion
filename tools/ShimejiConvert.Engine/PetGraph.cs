using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Tools.ShimejiConvert
{
    /// <summary>What a graph pass found. Separate from validity: every field here can be populated for XML
    /// the app's validator would reject, because the converter needs a graph report on its own broken
    /// output in order to fix it.</summary>
    public sealed class GraphReport
    {
        public int AnimationCount;
        public int EdgeCount;
        public readonly List<int> Roots = new List<int>();
        public readonly List<int> Unreachable = new List<int>();

        /// <summary>Animations with no outgoing transition at all. INFORMATIONAL, not a defect list:
        /// grimoire/03-pet-xml-format.md section 6 documents that when no next is eligible the selector
        /// returns -1 and the pet respawns from a fresh spawn. Dead ends are how the engine loops a pet, so
        /// counting them as errors would fail most of the pets this repo ships.</summary>
        public readonly List<int> Terminal = new List<int>();

        /// <summary>True when every declared animation can actually be reached from a spawn or a child.</summary>
        public bool IsConnected { get { return Unreachable.Count == 0; } }
    }

    /// <summary>
    /// Reachability over the &lt;next&gt; graph -- the gap in the app's validator.
    ///
    /// CompanionXmlValidator proves referential INTEGRITY (every transition target exists, probabilities are
    /// positive, frames index real tiles). It does not prove REACHABILITY, so a pet can pass validation
    /// with animations no spawn can ever lead to. A hand-authored pet rarely trips that; a converter
    /// emitting a flattened behavior tree trips it constantly, because dropping one unmappable Shimeji
    /// action orphans everything downstream of it. So this is the converter's own acceptance check, and it
    /// deliberately reports rather than throws: the residue is the interesting output.
    /// </summary>
    internal static class PetGraph
    {
        /// <summary>
        /// Animation names the host binds as runtime entry points in src/dotNet/Xml.cs (the loader's
        /// switch on animation name: fall/drag/kill/sync -> AnimationFall/Drag/Kill/Sync). The pet is
        /// thrown into these by gravity, by a mouse drag, by being removed and by multi-pet sync -- never
        /// by a &lt;next&gt; edge, so a graph pass that ignores them calls them orphans.
        ///
        /// This is documented behaviour, not a discovery -- see grimoire/03-pet-xml-format.md section 7,
        /// which calls these four names "magic". What measurement added: with them excluded, 21 of the 22
        /// pets this repo ships looked "disconnected", and every orphan was named fall, drag, kill or sync
        /// (bar two genuinely dead animations shared by the sheep recolours). A converter must therefore
        /// EMIT all four for the pet to have those behaviours at all -- Shimeji's Fall and Dragged map onto
        /// two of them, and kill/sync have no Shimeji equivalent and have to be synthesised.
        /// </summary>
        internal static readonly string[] ReservedEntryPointNames = { "fall", "drag", "kill", "sync" };

        public static GraphReport Analyze(XmlData.RootNode root)
        {
            var report = new GraphReport();
            if (root == null) return report;

            XmlData.AnimationNode[] animations =
                root.Animations != null && root.Animations.Animation != null
                    ? root.Animations.Animation
                    : new XmlData.AnimationNode[0];

            var declared = new HashSet<int>();
            var outgoing = new Dictionary<int, List<int>>();

            foreach (XmlData.AnimationNode animation in animations)
            {
                if (animation == null) continue;
                declared.Add(animation.Id);

                var targets = new List<int>();
                if (animation.Sequence != null) Collect(animation.Sequence.Next, targets);
                if (animation.Border != null) Collect(animation.Border.Next, targets);
                if (animation.Gravity != null) Collect(animation.Gravity.Next, targets);

                // Later duplicate ids are a validator error, not ours; keep the first so the graph stays a
                // function and the duplicate is reported by the validator instead of silently merged here.
                if (!outgoing.ContainsKey(animation.Id)) outgoing.Add(animation.Id, targets);

                report.EdgeCount += targets.Count;
                if (targets.Count == 0) report.Terminal.Add(animation.Id);

                if (IsReservedEntryPoint(animation.Name)) AddRoot(report, animation.Id);
            }

            report.AnimationCount = declared.Count;

            // A pet is entered from a spawn; a child pet is entered from its own <next>. Both are roots.
            if (root.Spawns != null && root.Spawns.Spawn != null)
            {
                foreach (XmlData.SpawnNode spawn in root.Spawns.Spawn)
                {
                    if (spawn == null || spawn.Next == null) continue;
                    AddRoot(report, spawn.Next.Value);
                }
            }

            if (root.Childs != null && root.Childs.Child != null)
            {
                foreach (XmlData.ChildNode child in root.Childs.Child)
                {
                    if (child == null) continue;
                    AddRoot(report, child.Next);
                }
            }

            var reached = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (int rootId in report.Roots)
                if (reached.Add(rootId)) queue.Enqueue(rootId);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                List<int> targets;
                if (!outgoing.TryGetValue(current, out targets)) continue;
                foreach (int target in targets)
                    if (reached.Add(target)) queue.Enqueue(target);
            }

            foreach (int id in declared)
                if (!reached.Contains(id)) report.Unreachable.Add(id);

            report.Unreachable.Sort();
            report.Terminal.Sort();
            return report;
        }

        private static void Collect(XmlData.NextNode[] nextNodes, List<int> targets)
        {
            if (nextNodes == null) return;
            foreach (XmlData.NextNode next in nextNodes)
            {
                if (next == null) continue;
                // Zero-probability transitions are unreachable at runtime, so they are not edges. The
                // validator already rejects a set whose probabilities sum to zero; this only skips the
                // individual dead branch inside an otherwise-live set.
                if (next.Probability <= 0) continue;
                targets.Add(next.Value);
            }
        }

        private static bool IsReservedEntryPoint(string animationName)
        {
            if (string.IsNullOrWhiteSpace(animationName)) return false;
            string trimmed = animationName.Trim();
            foreach (string reserved in ReservedEntryPointNames)
                if (string.Equals(trimmed, reserved, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void AddRoot(GraphReport report, int id)
        {
            if (!report.Roots.Contains(id)) report.Roots.Add(id);
        }
    }
}
