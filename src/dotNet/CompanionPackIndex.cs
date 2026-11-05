using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace DesktopAICompanion
{
    /// <summary>
    /// What a companion's header says about its pack (grimoire/06 section 2), read from the start of its animations.xml
    /// without validating the document: enough to index members and keep hidden ones out of the Add list. Staging still
    /// validates the whole document, and the host checks that the staged type is the member it asked for.
    /// </summary>
    internal sealed class PackHeaderFacts
    {
        public string PackId;
        public string Member;
        public string Role;
        public bool Hidden;
        public string StartupMember;
        public string StartupBehaviour;

        /// <summary>The pack facts in a header's text (the document's first part, up to its &lt;/header&gt;), or null
        /// when it declares no pack or cannot be read.</summary>
        public static PackHeaderFacts FromHeaderText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int start = text.IndexOf("<header", StringComparison.Ordinal);
            int end = start < 0 ? -1 : text.IndexOf("</header>", start, StringComparison.Ordinal);
            if (end < 0) return null;
            string header = text.Substring(start, end - start + "</header>".Length);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            PackHeaderFacts facts = null;
            try
            {
                using (var reader = XmlReader.Create(new StringReader(header), settings))
                {
                    int depth = -1;
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        if (reader.LocalName == "header" && depth < 0) { depth = reader.Depth; continue; }
                        // Only the header's own children: a nested element of the same name is not the header's.
                        if (reader.Depth != depth + 1) continue;
                        if (reader.LocalName == "pack")
                        {
                            if (facts == null) facts = new PackHeaderFacts();
                            facts.PackId = reader.GetAttribute("id");
                            facts.Member = reader.GetAttribute("member");
                            facts.Role = reader.GetAttribute("role");
                            string hidden = reader.GetAttribute("hidden");
                            facts.Hidden = hidden != null && (hidden.Trim() == "true" || hidden.Trim() == "1");
                        }
                        else if (reader.LocalName == "startup")
                        {
                            if (facts == null) facts = new PackHeaderFacts();
                            facts.StartupMember = reader.GetAttribute("member");
                            facts.StartupBehaviour = reader.GetAttribute("behaviour");
                        }
                    }
                }
            }
            catch (XmlException)
            {
                return null;
            }
            return facts != null && !string.IsNullOrEmpty(facts.PackId) && !string.IsNullOrEmpty(facts.Member) ? facts : null;
        }
    }

    /// <summary>One installed companion that declares a pack, as the index sees it.</summary>
    internal sealed class PackIndexEntry
    {
        /// <summary>The installed type id (its folder).</summary>
        public string TypeId;

        /// <summary>From the library (downloaded or installed by Studio) rather than the bundled companions.</summary>
        public bool FromLibrary;

        public PackHeaderFacts Facts;
    }

    /// <summary>
    /// The host's pack index (grimoire/06 section 9): (pack id, member key) to an installed type id. The library comes
    /// first, then the bundled companions; within a root, folders in ordinal case-insensitive order. A duplicate never
    /// replaces the member already indexed, and each one is noted, so the same files always resolve the same way.
    /// Previews are never in it: they resolve only inside their own namespace.
    /// </summary>
    internal sealed class CompanionPackIndex
    {
        private readonly Dictionary<string, PackIndexEntry> _byKey = new Dictionary<string, PackIndexEntry>(StringComparer.Ordinal);

        /// <summary>One line per duplicate that lost, naming the winner, for the diagnostic log.</summary>
        public readonly List<string> Duplicates = new List<string>();

        public int Count { get { return _byKey.Count; } }

        private static string Key(string packId, string member) { return packId + "\n" + member; }

        /// <summary>The installed member, or null. Pack ids and member keys match exactly (case-sensitive).</summary>
        public PackIndexEntry Find(string packId, string member)
        {
            PackIndexEntry e;
            return packId != null && member != null && _byKey.TryGetValue(Key(packId, member), out e) ? e : null;
        }

        /// <summary>Build from every installed companion that declares a pack, in any order: the precedence is applied
        /// here, so it is the same whoever enumerates.</summary>
        public static CompanionPackIndex Build(IEnumerable<PackIndexEntry> candidates)
        {
            var list = new List<PackIndexEntry>();
            foreach (PackIndexEntry c in candidates)
                if (c != null && c.Facts != null && !string.IsNullOrEmpty(c.TypeId)) list.Add(c);
            list.Sort(delegate (PackIndexEntry a, PackIndexEntry b)
            {
                if (a.FromLibrary != b.FromLibrary) return a.FromLibrary ? -1 : 1;
                int byId = StringComparer.OrdinalIgnoreCase.Compare(a.TypeId, b.TypeId);
                return byId != 0 ? byId : StringComparer.Ordinal.Compare(a.TypeId, b.TypeId);
            });
            var index = new CompanionPackIndex();
            foreach (PackIndexEntry c in list)
            {
                string key = Key(c.Facts.PackId, c.Facts.Member);
                PackIndexEntry held;
                if (index._byKey.TryGetValue(key, out held))
                {
                    index.Duplicates.Add("pack '" + c.Facts.PackId + "' member '" + c.Facts.Member + "': '" + c.TypeId
                        + "' (" + (c.FromLibrary ? "library" : "bundled") + ") is ignored; '" + held.TypeId + "' ("
                        + (held.FromLibrary ? "library" : "bundled") + ") is used");
                    continue;
                }
                index._byKey[key] = c;
            }
            return index;
        }
    }
}
