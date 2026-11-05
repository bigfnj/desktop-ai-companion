using System;
using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>
    /// The pack-level rules (grimoire/06 section 10, "Pack-level"), over the members' parsed documents, for whoever holds
    /// a whole pack: Studio before it installs one, the host when it indexes one. Each document must already have passed
    /// the per-document validator, which checks what one member can check alone (a startup only on a main member, pack
    /// keys, a startup behaviour of its own). It depends on the document model only, so Studio can link it as it links
    /// the validator.
    ///
    /// Errors make the pack unusable as one unit; warnings are what the runtime survives by falling back to the current
    /// type (a reference to a member that is not there), which Studio reports at import and does not refuse.
    /// </summary>
    internal static class PackValidator
    {
        public const string TransformMascot = "TransformMascot";
        public const string BornMascot = "BornMascot";

        internal sealed class Report
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
        }

        /// <summary>One member: a name to report it by (its file or type id) and its parsed document.</summary>
        internal sealed class Member
        {
            public string Source;
            public XmlData.RootNode Root;

            public Member(string source, XmlData.RootNode root)
            {
                Source = source;
                Root = root;
            }

            public XmlData.PackNode Pack { get { return Root != null && Root.Header != null ? Root.Header.Pack : null; } }
        }

        public static Report Validate(IList<Member> members)
        {
            var report = new Report();
            if (members == null || members.Count == 0)
            {
                report.Errors.Add("A pack needs at least one member.");
                return report;
            }

            // Every member declares the same pack, and its key is unique in it.
            string packId = null;
            var byKey = new Dictionary<string, Member>(StringComparer.Ordinal);
            foreach (Member m in members)
            {
                XmlData.PackNode p = m.Pack;
                if (p == null)
                {
                    report.Errors.Add(m.Source + " has no header/pack, so it is not a member of a pack.");
                    continue;
                }
                if (packId == null) packId = p.Id;
                else if (!string.Equals(packId, p.Id, StringComparison.Ordinal))
                    report.Errors.Add(m.Source + " is in pack '" + p.Id + "', not '" + packId + "'.");
                if (byKey.ContainsKey(p.Member ?? ""))
                    report.Errors.Add("Member key '" + p.Member + "' is used by both " + byKey[p.Member ?? ""].Source + " and " + m.Source + ".");
                else byKey[p.Member ?? ""] = m;
            }

            // Exactly one main.
            var mains = new List<Member>();
            foreach (Member m in members)
                if (m.Pack != null && string.Equals(m.Pack.Role, "main", StringComparison.Ordinal)) mains.Add(m);
            if (mains.Count != 1)
                report.Errors.Add("A pack has exactly one main member; this one has " + mains.Count + ".");

            // One transparency mode: a window keeps the mode it was made with, and a transformed companion draws the
            // target member's frames in it.
            string mode = null;
            Member modeFrom = null;
            foreach (Member m in members)
            {
                if (m.Root == null || m.Root.Image == null) continue;
                string own = TransparencyMode(m.Root.Image.Transparency);
                if (modeFrom == null) { mode = own; modeFrom = m; }
                else if (!string.Equals(mode, own, StringComparison.Ordinal))
                    report.Errors.Add(m.Source + " uses transparency '" + own + "', but " + modeFrom.Source + " uses '" + mode
                        + "': every member of a pack uses the same transparency.");
            }

            // References: each names a member, or it is reported (the runtime keeps the current type).
            var transformsTo = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (Member m in members)
            {
                if (m.Pack == null || m.Root == null || m.Root.Animations == null || m.Root.Animations.Animation == null) continue;
                var targets = new List<string>();
                transformsTo[m.Pack.Member ?? ""] = targets;
                foreach (Reference r in References(m.Root))
                {
                    if (r.Scripted)
                    {
                        report.Warnings.Add(m.Source + " " + r.Where + ": " + r.Name + " is an expression, so the member it names is"
                            + " known only at run time (a missing one keeps the current type).");
                        continue;
                    }
                    if (string.IsNullOrEmpty(r.Value)) continue;   // "" names no member: the current type, as the source
                    if (!byKey.ContainsKey(r.Value))
                        report.Warnings.Add(m.Source + " " + r.Where + ": " + r.Name + " names member '" + r.Value
                            + "', which this pack does not have (the runtime keeps the current type).");
                    if (r.Name == TransformMascot) targets.Add(r.Value);
                }
            }

            // The startup names a member and one of its behaviours; an egg startup must lead back to the main member.
            if (mains.Count == 1 && mains[0].Root.Header.Startup != null)
            {
                Member main = mains[0];
                XmlData.StartupNode s = main.Root.Header.Startup;
                Member start;
                if (!byKey.TryGetValue(s.Member ?? "", out start))
                    report.Errors.Add("header/startup names member '" + s.Member + "', which this pack does not have.");
                else
                {
                    if (!BehaviourNames(start.Root).Contains(s.Behaviour ?? ""))
                        report.Errors.Add("header/startup names behaviour '" + s.Behaviour + "', which member '" + s.Member + "' does not have.");
                    if (!ReferenceEquals(start, main) && string.Equals(start.Pack.Role, "egg", StringComparison.Ordinal)
                        && !TransformsInto(start.Pack.Member, main.Pack.Member, transformsTo))
                        report.Errors.Add("The startup egg '" + s.Member + "' never transforms back into the main member '"
                            + main.Pack.Member + "', so a companion that arrives as an egg would stay one.");
                }
            }
            return report;
        }

        /// <summary>The mode a window is made in: "Alpha" (per-pixel alpha), else the colour key as written.</summary>
        public static string TransparencyMode(string transparency)
        {
            string t = (transparency ?? "").Trim();
            return string.Equals(t, "Alpha", StringComparison.OrdinalIgnoreCase) ? "Alpha" : t.ToLowerInvariant();
        }

        /// <summary>Whether a chain of literal TransformMascot references leads from one member to another.</summary>
        private static bool TransformsInto(string from, string to, Dictionary<string, List<string>> transformsTo)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { from };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                List<string> next;
                if (!transformsTo.TryGetValue(queue.Dequeue(), out next)) continue;
                foreach (string n in next)
                {
                    if (string.Equals(n, to, StringComparison.Ordinal)) return true;
                    if (seen.Add(n)) queue.Enqueue(n);
                }
            }
            return false;
        }

        private static HashSet<string> BehaviourNames(XmlData.RootNode root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root != null && root.Animations != null && root.Animations.Animation != null)
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                    if (a != null && a.Behaviour != null && a.Behaviour.Name != null) names.Add(a.Behaviour.Name);
            return names;
        }

        private struct Reference
        {
            public string Where, Name, Value;
            public bool Scripted;
        }

        /// <summary>Every TransformMascot and BornMascot a document sets: as an action's parameter, a behaviour's binding or
        /// a composite child's binding.</summary>
        private static IEnumerable<Reference> References(XmlData.RootNode root)
        {
            foreach (XmlData.AnimationNode a in root.Animations.Animation)
            {
                if (a == null) continue;
                string where = "animation " + a.Id + " '" + a.Name + "'";
                if (a.Act != null && a.Act.Params != null)
                    foreach (XmlData.ParamNode p in a.Act.Params)
                        if (p != null && IsMemberParameter(p.Name))
                            yield return new Reference { Where = where, Name = p.Name, Value = p.Value, Scripted = IsScripted(p.Cache) };
                if (a.Act != null && a.Act.Children != null && a.Act.Children.Child != null)
                    foreach (XmlData.CompositeChildNode c in a.Act.Children.Child)
                        if (c != null && c.Bind != null)
                            foreach (XmlData.BindNode b in c.Bind)
                                if (b != null && IsMemberParameter(b.Name))
                                    yield return new Reference { Where = where + " -> " + c.Animation, Name = b.Name, Value = b.Value, Scripted = IsScripted(b.Cache) };
                if (a.Behaviour != null && a.Behaviour.Bind != null)
                    foreach (XmlData.BindNode b in a.Behaviour.Bind)
                        if (b != null && IsMemberParameter(b.Name))
                            yield return new Reference { Where = where, Name = b.Name, Value = b.Value, Scripted = IsScripted(b.Cache) };
            }
        }

        private static bool IsMemberParameter(string name)
        {
            return string.Equals(name, TransformMascot, StringComparison.Ordinal) || string.Equals(name, BornMascot, StringComparison.Ordinal);
        }

        private static bool IsScripted(string cache)
        {
            return string.Equals(cache, "action", StringComparison.Ordinal) || string.Equals(cache, "frame", StringComparison.Ordinal);
        }
    }
}
