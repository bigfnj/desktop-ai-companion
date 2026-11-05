using System;

namespace DesktopAICompanion
{
    /// <summary>
    /// Packs for tick-clock companions (grimoire/06 section 9): the tick host stages a member on demand through
    /// <see cref="ResolveExtraType"/> (validated, sized by the member's own size setting, reference-counted in the
    /// registry), a main member starts as its startup says, and "Arrive as an egg" (LocalData.GetArriveAsEgg, stored in
    /// settings.json's companionsNoEggArrival) decides whether an egg startup is used.
    /// </summary>
    public sealed partial class StartUp
    {
        private Shimeji.TickPackBinding tickPacks;

        private void AttachTickPacks(Shimeji.TickHost host)
        {
            tickPacks = new Shimeji.TickPackBinding(host, registry, ResolveExtraType, Shimeji.TickStaging.Of,
                CompanionCatalog.PackIndexLocal,
                id => Program.MyData == null || Program.MyData.GetArriveAsEgg(id),
                line => AddDebugInfo(DEBUG_TYPE.warning, "[tick] " + line));
            // A staged member's windows (a birth of it) find its registry entry the way a spawned type's do.
            Func<Shimeji.TickType, string, Shimeji.TickHostType> stage = host.StageMember;
            host.StageMember = delegate (Shimeji.TickType from, string key)
            {
                Shimeji.TickHostType t = stage(from, key);
                if (t != null) tickEntries[t.Type.Name] = t.Owner as CompanionTypeRegistry.Entry;
                return t;
            };
        }

        /// <summary>A spawned tick type's pack scope: a preview's members resolve only in its own namespace.</summary>
        private static void ScopeTickType(Shimeji.TickType type, CompanionTypeRegistry.Entry entry)
        {
            if (entry != null && entry.IsTransient) Shimeji.TickPackBinding.ScopeForPreview(type, entry.Id);
        }

        /// <summary>The type name a new companion of <paramref name="type"/> starts as, and its entry behaviour (null: the
        /// route chooses).</summary>
        private string PlanTickStart(Shimeji.TickType type, string typeId, out string behaviour)
        {
            behaviour = null;
            if (tickPacks == null) return type.Name;
            return tickPacks.PlanStart(type, typeId, out behaviour) ?? type.Name;
        }

        /// <summary>The user's "Arrive as an egg" for a main member's type id, and whether this type offers it at all
        /// (its startup member's role is egg). For the Pets pane.</summary>
        internal bool OffersEggArrival(string typeId)
        {
            if (string.IsNullOrEmpty(typeId) || !SecureDownload.IsSafeId(typeId)) return false;
            string path = null;
            foreach (string root in new[] { AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = System.IO.Path.Combine(root, typeId, "animations.xml");
                if (System.IO.File.Exists(candidate)) { path = candidate; break; }
            }
            if (path == null) return false;
            PackHeaderFacts facts = CompanionCatalog.ReadPackFacts(path);
            if (facts == null || facts.Role != Shimeji.TickPack.RoleMain || string.IsNullOrEmpty(facts.StartupMember)
                || facts.StartupMember == facts.Member)
                return false;
            PackIndexEntry start = CompanionCatalog.PackIndexLocal().Find(facts.PackId, facts.StartupMember);
            return start != null && start.Facts.Role == Shimeji.TickPack.RoleEgg;
        }

        internal bool SetArriveAsEgg(string typeId, bool enabled)
        {
            return Program.MyData != null && Program.MyData.SetArriveAsEgg(typeId, enabled);
        }
    }
}
