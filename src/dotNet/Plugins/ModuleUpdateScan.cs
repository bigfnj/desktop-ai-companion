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

        // Encode and Decode were REMOVED 2026-09-17, together with the settings key they existed to
        // write. Their own doc comment gave the purpose: "for caching the last answer so a pane can
        // render it without a network round trip" -- and no pane ever read it. Both the key
        // (moduleUpdateOffers) and the getter had zero readers, so the format, its round-trip and its
        // six assertions were all serving a value nothing consumed.
        //
        // "Render the last known offers with no network" is a real FEATURE and it is filed as one in
        // docs/IDEAS.md. If it gets built, these two are in git history at this commit rather than
        // gone -- which is cheaper than keeping a serialisation format alive against the possibility.


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
