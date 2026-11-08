using System;
using System.Collections.Generic;

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

        /// <summary>The namespaces of the whole-pack previews on screen (1.10.0): a preview member is staged from here only.</summary>
        private readonly Shimeji.TickPreviewPacks previewPacks = new Shimeji.TickPreviewPacks();

        private void AttachTickPacks(Shimeji.TickHost host)
        {
            tickPacks = BindTickPacks(host, registry, ResolveExtraType, CompanionCatalog.PackIndexLocal,
                id => Program.MyData == null || Program.MyData.GetArriveAsEgg(id), tickEntries,
                line => AddDebugInfo(DEBUG_TYPE.warning, "[tick] " + line));
            tickPacks.StagePreviewMember = (scope, key) => previewPacks.Stage(registry, scope, key, PreviewScaleFactor(),
                line => AddDebugInfo(DEBUG_TYPE.warning, "[tick] " + line));
        }

        /// <summary>
        /// The app's pack binding over what it is handed: members are staged through <paramref name="stageType"/>, a staged
        /// Xml's tick type is read with TickStaging.Of, and every member the world stages is recorded in
        /// <paramref name="entries"/> under its type name. Static so --shimeji-window-selftest drives this wiring and the
        /// recording, not a copy of them (N-audit-1106-304).
        /// </summary>
        internal static Shimeji.TickPackBinding BindTickPacks(Shimeji.TickHost host, CompanionTypeRegistry registry,
            Func<string, CompanionTypeRegistry.Entry> stageType, Func<CompanionPackIndex> index, Func<string, bool> arriveAsEgg,
            System.Collections.Generic.IDictionary<string, CompanionTypeRegistry.Entry> entries, Action<string> log)
        {
            var binding = new Shimeji.TickPackBinding(host, registry, stageType, Shimeji.TickStaging.Of, index, arriveAsEgg, log);
            // A staged member's windows (a birth of it) find its registry entry the way a spawned type's do. This wraps the
            // stager the binding's constructor just set, so it comes AFTER the binding: wrapped before, the constructor
            // would replace it and a born member's window would hold no registry entry.
            Func<Shimeji.TickType, string, Shimeji.TickHostType> stage = host.StageMember;
            host.StageMember = delegate (Shimeji.TickType from, string key)
            {
                Shimeji.TickHostType t = stage(from, key);
                if (t != null) entries[t.Type.Name] = t.Owner as CompanionTypeRegistry.Entry;
                return t;
            };
            return binding;
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

        /// <summary>
        /// A whole-pack preview (ICompanionPackPreview, 1.10.0): every member validated as one pack, opened as ONE preview
        /// namespace, and the main member spawned as a preview pet under the preview's id. Its type is scoped to the preview
        /// (ScopeTickType, as any preview's), so its startup, Transforms and Breeds resolve in this namespace and never in the
        /// installed index. The same caps as <see cref="SpawnPreviewPet"/>, checked before anything is staged; the namespace
        /// opens BEFORE the spawn because the spawn resolves the startup member at once, and it closes again on every way
        /// the spawn can fail. Returns the main member's window and the preview id, or null with a reason.
        /// </summary>
        internal FormCompanion SpawnPackPreviewPet(System.Collections.Generic.IReadOnlyDictionary<string, string> memberXmlByKey,
            string mainKey, out string previewId, out string error)
        {
            previewId = null;
            error = null;
            if (disposed) { error = "The pet runtime is shutting down."; return null; }
            error = PreviewCapRefusal();
            if (error != null) return null;
            Shimeji.TickPreviewPacks.Prepared pack = Shimeji.TickPreviewPacks.Prepare(memberXmlByKey, mainKey, out error);
            if (pack == null) return null;

            Xml stagedXml;
            Animations stagedAnimations;
            if (!TryStageRuntime(pack.MainXml, PreviewScaleFactor(), out stagedXml, out stagedAnimations, out error))
                return null;
            string id = "preview:" + Guid.NewGuid().ToString("N");
            stagedAnimations.PetTypeId = id;
            CompanionTypeRegistry.Entry entry = registry.Add(id, stagedXml, stagedAnimations, true);
            previewPacks.Open(id, pack);

            FormCompanion spawned;
            try { spawned = AddSheepCore(entry.Xml, entry.Animations, entry); }
            catch { previewPacks.Close(id); registry.DropIfUnused(entry); throw; }
            if (spawned == null)
            {
                // A member the startup staged is held by nothing once the spawn is declined; closing the namespace and
                // dropping the main entry leaves the registry as it was.
                previewPacks.Close(id);
                registry.DropIfUnused(entry);
                error = shuttingDown ? "The app is shutting down." : "The pack preview could not be shown.";
                return null;
            }
            previewId = id;
            AddDebugInfo(DEBUG_TYPE.info, "pack preview spawned (" + id + ", pack " + pack.PackId + ", " + pack.Keys.Count + " members)");
            return spawned;
        }

        /// <summary>Remove a whole-pack preview: its namespace closes first, so nothing more is staged for it and a birth
        /// still queued gets no window, then every window that belongs to it goes (the companion that arrived, whatever it
        /// became, and every companion it bred). The count removed.</summary>
        internal int RemovePackPreview(string previewId)
        {
            if (string.IsNullOrEmpty(previewId)) return 0;
            previewPacks.Close(previewId);
            var mine = new System.Collections.Generic.List<FormCompanion>();
            for (int i = 0; i < iSheeps; i++)
                if (BelongsToPreview(sheeps[i], previewId)) mine.Add(sheeps[i]);
            int removed = 0;
            foreach (FormCompanion pet in mine)
                if (RemovePetInstance(pet)) removed++;
            return removed;
        }

        /// <summary>Remove a SINGLE preview (ICompanionManager.SpawnPreview's handle) the way RemovePackPreview removes a
        /// whole-pack one (N-host-pack-abi-01): the preview is retired first, so a birth still queued gets no window, then
        /// the window the handle was given goes with every companion it bred. A tick preview that breeds gives each born
        /// companion the preview's own transient registry entry (OpenBornTickPet), and removing the handed window alone
        /// left those running: not persisted, not in the tray, not reachable by RemoveOne. The count removed.</summary>
        internal int RemoveSinglePreview(FormCompanion pet, string previewId)
        {
            if (!string.IsNullOrEmpty(previewId)) previewPacks.Retire(previewId);
            int removed = 0;
            foreach (FormCompanion p in SinglePreviewWindows(sheeps, iSheeps, petEntries, previewId, pet))
                if (RemovePetInstance(p)) removed++;
            return removed;
        }

        /// <summary>The windows a single preview's Remove takes: the one it was handed, and every other window whose registry
        /// entry is the preview's own transient entry (its id is the preview's). Exact id, not TickPreviewPacks.Owns: a
        /// single preview stages no members. Pure over the arrays given, for --shimeji-window-selftest.</summary>
        internal static List<FormCompanion> SinglePreviewWindows(FormCompanion[] pets, int count,
            IDictionary<FormCompanion, CompanionTypeRegistry.Entry> entries, string previewId, FormCompanion handed)
        {
            var mine = new List<FormCompanion>();
            if (handed != null) mine.Add(handed);
            if (pets == null || entries == null || string.IsNullOrEmpty(previewId)) return mine;
            for (int i = 0; i < count && i < pets.Length; i++)
            {
                FormCompanion p = pets[i];
                CompanionTypeRegistry.Entry entry;
                if (p != null && !ReferenceEquals(p, handed) && entries.TryGetValue(p, out entry) && entry != null && entry.IsTransient
                    && string.Equals(entry.Id, previewId, StringComparison.OrdinalIgnoreCase))
                    mine.Add(p);
            }
            return mine;
        }

        /// <summary>Whether any companion of this preview is still on screen.</summary>
        internal bool IsPackPreviewAlive(string previewId)
        {
            if (string.IsNullOrEmpty(previewId)) return false;
            for (int i = 0; i < iSheeps; i++)
                if (sheeps[i] != null && !sheeps[i].IsDisposed && BelongsToPreview(sheeps[i], previewId)) return true;
            return false;
        }

        /// <summary>A window whose registry entry is the preview's own or one of its members' (TickPreviewPacks.Owns). A
        /// born companion's entry is the type it was born as, and a transformed one keeps the entry of the window it had.</summary>
        private bool BelongsToPreview(FormCompanion pet, string previewId)
        {
            CompanionTypeRegistry.Entry entry;
            return pet != null && petEntries.TryGetValue(pet, out entry) && entry != null && entry.IsTransient
                && Shimeji.TickPreviewPacks.Owns(previewId, entry.Id);
        }
    }
}
