using System;
using System.Collections.Generic;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>One installed module the catalog offers a newer build of.</summary>
    internal sealed class ModuleUpdateOffer
    {
        public CatalogModule Offered;
        public string InstalledVersion;

        public string Name { get { return Offered != null ? (Offered.Name ?? Offered.Id) : ""; } }
        public string OfferedVersion { get { return Offered != null ? Offered.Version : ""; } }
        /// <summary>The catalog id. Name is for prose and can be null or duplicated; this is the key.</summary>
        public string Id { get { return Offered != null ? (Offered.Id ?? "") : ""; } }
    }

    /// <summary>
    /// Decides whether the catalog is offering something newer than what is installed. One implementation so
    /// the Modules pane's Update button and the monthly background check can never disagree about what counts
    /// as an update — a version rule that differs between "the badge" and "the check" is how a user ends up
    /// being told about an update they cannot see, or vice versa.
    /// </summary>
    internal static class ModuleUpdateScan
    {
        /// <summary>
        /// The catalog entry for <paramref name="moduleId"/> when it is strictly newer than
        /// <paramref name="installedVersion"/>, else null. BOTH versions must parse: an unparseable version on
        /// either side offers nothing rather than guessing, because the failure mode of guessing is an update
        /// offer that never goes away no matter how often the user accepts it.
        /// </summary>
        internal static CatalogModule FindUpdate(RemoteCatalog catalog, string moduleId, string installedVersion)
        {
            if (catalog == null || catalog.Modules == null || string.IsNullOrWhiteSpace(moduleId)) return null;
            Version installed;
            if (!Version.TryParse((installedVersion ?? "").Trim(), out installed)) return null;
            foreach (CatalogModule m in catalog.Modules)
            {
                if (m == null || !string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase)) continue;
                Version offered;
                if (!Version.TryParse((m.Version ?? "").Trim(), out offered)) return null;
                return offered > installed ? m : null;
            }
            return null;
        }

        /// <summary>
        /// Every loaded module the catalog can upgrade. Loaded, not on-disk: the comparison needs the version a
        /// module actually REPORTS, and a folder sitting there pending a restart has no reportable version yet.
        /// </summary>
        internal static List<ModuleUpdateOffer> FindUpdates(RemoteCatalog catalog, IEnumerable<IModule> loadedModules)
        {
            var offers = new List<ModuleUpdateOffer>();
            if (catalog == null || loadedModules == null) return offers;
            foreach (IModule module in loadedModules)
            {
                if (module == null || module.Info == null) continue;
                CatalogModule newer = FindUpdate(catalog, module.Info.Id, module.Info.Version);
                if (newer != null)
                    offers.Add(new ModuleUpdateOffer { Offered = newer, InstalledVersion = module.Info.Version });
            }
            return offers;
        }

        /// <summary>
        /// "aibrain=1.4.1;fortunes=1.2.8" — the machine-readable form, for caching the last answer so a pane
        /// can render it without a network round trip. Paired with <see cref="Decode"/>.
        ///
        /// Ids, not display names: <see cref="Describe"/> produces prose for a balloon and is not
        /// round-trippable. Separators are stripped from both halves rather than escaped, because an id that
        /// contains one cannot exist (SecureDownload.IsSafeId) and a version that does is not a version.
        /// </summary>
        internal static string Encode(IList<ModuleUpdateOffer> offers)
        {
            if (offers == null || offers.Count == 0) return "";
            var parts = new List<string>(offers.Count);
            foreach (ModuleUpdateOffer o in offers)
            {
                if (o == null) continue;
                string id = (o.Id ?? "").Replace(";", "").Replace("=", "");
                string version = (o.OfferedVersion ?? "").Replace(";", "").Replace("=", "");
                if (id.Length == 0 || version.Length == 0) continue;
                parts.Add(id + "=" + version);
            }
            return string.Join(";", parts.ToArray());
        }

        /// <summary>Read back what <see cref="Encode"/> wrote: id -> offered version. A malformed entry is
        /// skipped rather than thrown on; this is a cache, and the cost of ignoring it is one more check.</summary>
        internal static Dictionary<string, string> Decode(string encoded)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(encoded)) return map;
            foreach (string entry in encoded.Split(';'))
            {
                if (entry.Length == 0) continue;
                int split = entry.IndexOf('=');
                if (split <= 0 || split == entry.Length - 1) continue;
                map[entry.Substring(0, split)] = entry.Substring(split + 1);
            }
            return map;
        }

        /// <summary>"AI Brain 1.1.1" / "AI Brain 1.1.1 and Fortunes 1.2.0" — for a notification line.</summary>
        internal static string Describe(IList<ModuleUpdateOffer> offers)
        {
            if (offers == null || offers.Count == 0) return "";
            var parts = new List<string>(offers.Count);
            foreach (ModuleUpdateOffer o in offers) parts.Add(o.Name + " " + o.OfferedVersion);
            if (parts.Count == 1) return parts[0];
            if (parts.Count == 2) return parts[0] + " and " + parts[1];
            return string.Join(", ", parts.ToArray(), 0, parts.Count - 1) + " and " + parts[parts.Count - 1];
        }
    }
}
