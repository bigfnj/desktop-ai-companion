using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit;   // EmbeddedResources

namespace DesktopAICompanion.FortunesModule
{
    /// <summary>
    /// The Fortunes module (S3). Owns the pet's fortune voice: a personalized welcome on the first spawn,
    /// then a fortune on land / poke (1-2) / the periodic drop — spoken from the module's relocated engine
    /// (dumb random + smart ONNX-semantic pick). It ships NO fortune content, so with no installed pack it's
    /// silent except the welcome; the engine reads packs from the module's own storage. Since S3d it is the
    /// LIVE source (the base no longer speaks fortunes); the poke escalation's ignore/sass/escape stay in the
    /// base engine, which just raises CompanionPoked with the count.
    /// </summary>
    public sealed class FortunesModule : IModule
    {
        private IHost _host;
        private string[] _welcome;
        private readonly Random _rand = new Random();
        private bool _welcomed;

        private FortuneProvider _provider;   // the relocated engine (packs -> filtered pool)
        private SmartFortunes _smart;        // optional ONNX semantic picker (null when disabled/unavailable)
        private string _indexedSignature;    // fingerprint of the pool _smart was warmed on (null = none)
        private ICompanion _lastPet;               // most-recently-seen pet, for screen-context capture on the drop path
        private IDisposable _dropResponder;
        private IDisposable _pokeResponder;

        // Pack/genre boxes the user has moved but not yet applied, keyed by settings key ("disabledSources"
        // / "disabledGenres") then by id -> disabled?. Filled by the DeferChanges cards at Apply time and
        // drained by SavePaneValues, so the batch costs one settings write and one engine rebuild.
        private readonly Dictionary<string, Dictionary<string, bool>> _stagedDisabled =
            new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "fortunes",
            Name = "Fortunes",
            Version = "1.0.0",   // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.2.7: payload refresh only, no behaviour change -- the bundled ModuleKit
                                 //        gained RecordingHost.RaiseFullscreenChanged (host 1.9.9).
                                 // 1.2.6: a COLLAPSED pool now warns instead of ticking. "2,794 fortunes from
                                 //        1 pack" was reported as healthy while 157 of 190 sources were off,
                                 //        which is why the same dad joke kept coming back.
                                 // 1.2.5: decode HTML entities left in scraped pack text, so a fortune reads
                                 //        "me & Dave" rather than "me &amp; Dave". Done at parse time, so it
                                 //        also repairs packs already in a user's fortunes folder.
                                 // 1.2.4: payload refresh only, no behaviour change. The bundled ModuleKit was
                                 //        3 commits stale (styled-speech prefixes, the shared-context
                                 //        RecordingHost, MemoryModuleSettings); the publish-freshness check
                                 //        could not see that until it started watching bundled project
                                 //        references. A version bump is required for the in-app Update button
                                 //        to offer the refreshed payload at all.
                                 // 1.2.3: the smart/contextual picker (the majority speech path) no longer
                                 //        collapses to repeating the same handful once its recent window
                                 //        saturates -- it recycles a spent context, never repeats a line
                                 //        back to back, and both speech paths now share recent history
                                 // 1.2.2: the unmapped-pack fallback group reads "More packs", not the
                                 //        misleading "Your own packs" (it also catches catalog packs newer
                                 //        than this build's collection map, not only user imports)
                                 // 1.2.1: republish carrying the "don't repeat the same fortune so soon" fix,
                                 //        which landed in source but whose payload was never rebuilt, so it
                                 //        never reached the catalog
                                 // 1.2.0: a fortune is spoken by ONE pet -- the one poked, or the one the drop
                                 //        was routed to -- instead of every pet on screen at once
                                 // 1.1.2: helpers come from DesktopAICompanion.ModuleKit instead of local copies
                                 // 1.1.1: Genres filter now applies to downloaded packs (per-source genre)
                                 // 1.1.0: carries the built-in fortune corpus again (it was never embedded here)
            // 1.5.0 is the host that added the pet-aware responders. Declaring it means an older host refuses
            // this module with a legible reason instead of loading it and broadcasting every fortune.
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.ScreenContext | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _welcome = LoadWelcomeCorpus();

            // Point the engine at the module's own storage, then build the pool from the user's packs (empty
            // by default = silent) and warm the smart picker when enabled. All best-effort: a failure here
            // leaves the module welcome-only rather than breaking the host.
            try
            {
                IModuleStorage storage = host.GetStorage("fortunes");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                    FortunePaths.SetRoot(storage.DataDirectory);
            }
            catch { }
            RebuildEngine();

            host.CompanionSpawned += OnPetSpawned;
            host.CompanionLanded += OnPetLanded;
            host.CompanionPoked += OnPetPoked;
            // Pet-aware registrations (host 1.5.0+): the host tells us WHICH pet the reaction is for, so the
            // fortune goes to that pet instead of every pet reciting it in unison.
            _dropResponder = host.RegisterCompanionDropResponder(0, OnDrop);   // lowest priority; the AI brain (S4) outranks
            // Poke 1 of a session: speak a fortune if the user's "Trigger Speech" choice lets us win the
            // arbitration. Same priority ordering as the drop (the AI brain outranks), but that only decides
            // ties when the user hasn't picked a specific source.
            _pokeResponder = host.RegisterCompanionPokeResponder(Info.Id, 0, SpeakFortune);

            // Contribute the fortunes settings as a schema-driven OptionsPane (S5b): the host renders it in
            // the WPF settings window and round-trips values through Load/Save, which persist to the module's
            // own host.GetSettings("fortunes") store and rebuild the live engine so a change takes effect on
            // the running pet at once. The richer sources / genres / packs list is a follow-up (it needs a
            // list-card primitive); this pane covers the selection + content-level toggles.
            host.AddOptionsPane(BuildOptionsPane());
        }

        /// <summary>(Re)build the engine from the current saved settings: rebuild the pool from the user's
        /// packs and, when smart picks are on, (re)warm the semantic index. Called at Init and after the
        /// Options pane saves, so a settings change (or a pack added to the folder) applies without a restart.</summary>
        private void RebuildEngine()
        {
            try
            {
                FortuneSettings settings = LoadFortuneSettings(_host);
                _provider = new FortuneProvider(settings);
                SmartFortunes old = _smart;
                _smart = null;
                _indexedSignature = null;
                if (old != null) { try { old.Dispose(); } catch { } }
                if (settings.SmartFortunes)
                {
                    var sm = new SmartFortunes();
                    List<FortuneEntry> pool = _provider.PoolEntries();
                    // Recorded before the warm starts: it names the pool being indexed, which is what a
                    // later "is this still current?" question compares against.
                    _indexedSignature = PoolSignature(pool);
                    sm.Warm(pool);
                    _smart = sm;
                }
            }
            catch { _provider = null; _smart = null; _indexedSignature = null; }
        }

        // The first pet of the session gets a personalized greeting; later spawns don't re-welcome.
        private void OnPetSpawned(ICompanion pet)
        {
            _lastPet = pet ?? _lastPet;
            if (_welcomed) return;
            _welcomed = true;
            IHost host = _host;
            if (host == null) return;
            string line = PickWelcome(GreetingName(host));
            // SayAll on purpose, and one of the few places it is still right: this is a once-per-session
            // greeting addressed to the USER, not a reaction belonging to a pet, and it fires on the first
            // spawn when there is normally one pet on screen anyway.
            if (!string.IsNullOrEmpty(line)) host.SayAll(line);
        }

        // A landing is that pet arriving, so the greeting belongs to it. This used to speak through every pet
        // on screen, which made adding a fourth pet produce four identical fortunes at once.
        private void OnPetLanded(ICompanion pet) { _lastPet = pet ?? _lastPet; SpeakFortune(pet); }

        // Track the poked pet for screen-context capture; SPEAKING on a poke goes through the arbitrated
        // poke-responder chain instead (see RegisterPokeResponder in Init), so exactly one module wins it.
        private void OnPetPoked(PokeInfo info)
        {
            if (info == null) return;
            _lastPet = info.Pet ?? _lastPet;
        }

        // The periodic drop responder: speak a fortune. Returns true when it actually spoke (handled), so the
        // arbitrated drop chain stops here (fortunes are the lowest-priority default).
        private bool OnDrop(ICompanion pet) { return SpeakFortune(pet); }

        /// <summary>
        /// Speak a fortune — smart/contextual pick when the picker is ready, else random from the pool.
        /// Mirrors the old StartUp.SayFortune. Returns true if a line was spoken.
        ///
        /// <paramref name="subject"/> is the pet the fortune belongs to: the one poked, the one that landed,
        /// or the one the host routed this drop to. The screen context is captured from that pet too, so a
        /// contextual pick describes the window THAT pet is standing on rather than some other pet's.
        /// </summary>
        private bool SpeakFortune(ICompanion subject)
        {
            IHost host = _host;
            FortuneProvider provider = _provider;
            if (host == null || provider == null || !host.SpeechEnabled) return false;

            // Fall back to the last pet we saw only when the host could not name one (a legacy host, or a
            // trigger with no natural subject); a dead handle is dropped rather than guessed at.
            ICompanion pet = subject ?? _lastPet;
            if (pet != null && !host.IsCompanionAlive(pet)) pet = null;

            string f = null;
            // Roughly a third of the time draw from the whole pool even when smart is ready, so a rarely-
            // changing foreground window doesn't lock the pet onto the same handful of context matches.
            bool goRandom = _rand.Next(3) == 0;
            SmartFortunes picker = _smart;
            if (!goRandom && picker != null && picker.Ready)
            {
                try
                {
                    ScreenContext ctx = pet != null ? host.CaptureScreenContext(pet) : null;
                    if (ctx != null) f = picker.Pick(ctx.WindowTitle, ctx.ProcessName);
                }
                catch { f = null; }
            }
            if (string.IsNullOrWhiteSpace(f))
            {
                f = provider.Pick();
                // This line came from the whole-pool random draw, not the smart picker's own recent
                // tracking, so tell the picker about it -- otherwise the two speech paths keep separate
                // histories and can echo each other's last line.
                if (!string.IsNullOrWhiteSpace(f) && picker != null)
                {
                    try { picker.NoteExternallyShown(f); } catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(f)) return false;
            // One pet says it. SayAll only when the host could not name a subject at all, which keeps a
            // legacy host working rather than silently dropping the line.
            if (pet != null) host.Say(pet, f); else host.SayAll(f);
            return true;
        }

        private static FortuneSettings LoadFortuneSettings(IHost host)
        {
            var s = new FortuneSettings();
            try
            {
                IModuleSettings ms = host.GetSettings("fortunes");
                if (ms != null)
                {
                    s.ContentLevel = ReadContentLevel(ms);
                    s.NoProfanity = ms.GetBool("noProfanity", s.NoProfanity);
                    s.SmartFortunes = ms.GetBool("smartFortunes", s.SmartFortunes);
                    s.DisabledSources = SplitList(ms.Get("disabledSources", ""));
                    s.DisabledGenres = SplitList(ms.Get("disabledGenres", ""));
                }
            }
            catch { }
            return s;
        }

        /// <summary>
        /// Read the content level, migrating a pre-collapse settings file on the fly. The old trio meant:
        /// spicy off => tame only; tier "edgy" => general+edgy+nsfw (everything, despite the name); tier
        /// "nsfw" => general+nsfw (dropped edgy, which nobody could have wanted); "skip the tame ones"
        /// removed general from whichever of those applied.
        /// Two old shapes have no exact new equivalent (the ones that admitted nsfw but not edgy); they map
        /// to the nearest level that keeps the user's evident intent — spicy stays on — which adds edgy, a
        /// MILDER tier than the nsfw they already had. Migration deliberately never widens past what the
        /// user already allowed at the top end, and never silently turns spicy content off.
        /// </summary>
        private static string ReadContentLevel(IModuleSettings ms)
        {
            return MigrateContentLevel(
                ms.Get("contentLevel", ""),
                ms.GetBool("spicyFortunes", false),
                ms.GetBool("spicyOnly", false));
        }

        /// <summary>The pure mapping behind <see cref="ReadContentLevel"/>, split out so the migration is
        /// directly testable without faking a settings store. A recognized new value always wins.</summary>
        internal static string MigrateContentLevel(string stored, bool legacySpicy, bool legacySkipTame)
        {
            if (ContentLevels.IsKnown(stored)) return stored;
            if (!legacySpicy) return ContentLevels.Clean;
            return legacySkipTame ? ContentLevels.SpicyOnly : ContentLevels.Everything;
        }

        // ---- Options pane (S5b): selection + content-level toggles ---------------------------------

        // Content level: stored as a ContentLevels id, shown as an ordered plain-language label. The labels
        // say what you GET, in order, so the choice needs no explanation of how tiers combine.
        private const string LevelCleanDisplay = "Clean only";
        private const string LevelCleanEdgyDisplay = "Clean + edgy";
        private const string LevelEverythingDisplay = "Everything (incl. NSFW)";
        private const string LevelSpicyOnlyDisplay = "Spicy only (skip the tame ones)";

        private static string[] ContentLevelDisplays()
        {
            return new[] { LevelCleanDisplay, LevelCleanEdgyDisplay, LevelEverythingDisplay, LevelSpicyOnlyDisplay };
        }
        private static string LevelToDisplay(string level)
        {
            switch (level)
            {
                case ContentLevels.CleanEdgy: return LevelCleanEdgyDisplay;
                case ContentLevels.Everything: return LevelEverythingDisplay;
                case ContentLevels.SpicyOnly: return LevelSpicyOnlyDisplay;
                default: return LevelCleanDisplay;
            }
        }
        private static string DisplayToLevel(string display)
        {
            switch (display)
            {
                case LevelCleanEdgyDisplay: return ContentLevels.CleanEdgy;
                case LevelEverythingDisplay: return ContentLevels.Everything;
                case LevelSpicyOnlyDisplay: return ContentLevels.SpicyOnly;
                default: return ContentLevels.Clean;
            }
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Fortunes",
                Schema = new[]
                {
                    new SettingField { Id = "smartFortunes", Label = "Smart, context-aware picks", Kind = SettingKind.Bool, Group = "Selection" },
                    new SettingField { Id = "contentLevel", Label = "Content level", Kind = SettingKind.Enum, Options = ContentLevelDisplays(), Group = "Content level" },
                    new SettingField { Id = "noProfanity", Label = "Remove profanity / explicit words", Kind = SettingKind.Bool, Group = "Content level" },
                    // Display-only: what the current filters actually leave to draw from. Without this an
                    // over-tight selection empties the pool and the pet just goes quiet with no explanation.
                    new SettingField { Id = "poolStatus", Label = "Right now", Kind = SettingKind.Info, Group = "Content level" },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Rebuild smart index", InvokeAsync = RebuildSmartIndexAsync, Group = "Selection" },
                    new PaneAction { Label = "Show me 5 examples", InvokeAsync = PreviewFortunesAsync, Group = "Content level" },
                },
                // (pack browse/download buttons live on the Fortune packs card below, next to the folder ones)
                Lists = new[]
                {
                    new ListCard
                    {
                        Title = "Fortune packs",
                        LoadItems = LoadSourceItems,
                        SetChecked = SetSourceActive,
                        // Each tick changes what the engine reads, so it takes effect on Apply with the rest
                        // of the pane. Ticking live meant a full rebuild per click.
                        DeferChanges = true,
                        Filterable = true,
                        CollapseGroups = true,
                        EmptyHint = "No fortune packs yet. Use “Available online” below to get them from the " +
                            "catalog, or “Open fortunes folder” to drop your own .txt pack in and Rescan.",
                        Actions = new[]
                        {
                            new PaneAction { Label = "Import your own…", InvokeAsync = ImportPacksAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Open fortunes folder", InvokeAsync = OpenFortunesFolderAsync },
                            new PaneAction { Label = "Rescan folder", InvokeAsync = RescanAsync, ReloadPaneAfter = true },
                        },
                    },
                    // Browse -> tick what you want -> download only those. Ticking is deliberately just an
                    // in-memory mark (SetChecked is synchronous, so it must never do network work); the
                    // download button owns the actual fetching and reports progress.
                    new ListCard
                    {
                        Title = "Available online",
                        LoadItems = LoadAvailablePackItems,
                        SetChecked = SetPackSelected,
                        Filterable = true,
                        CollapseGroups = true,
                        EmptyHint = "Click “Check online for packs” to see what the catalog offers.",
                        Actions = new[]
                        {
                            new PaneAction { Label = "Check online for packs", InvokeAsync = CheckPacksOnlineAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Download selected", InvokeAsync = DownloadPacksAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Select all", InvokeAsync = SelectAllPacksAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Select none", InvokeAsync = SelectNoPacksAsync, ReloadPaneAfter = true },
                        },
                    },
                    new ListCard
                    {
                        Title = "Genres",
                        LoadItems = LoadGenreItems,
                        SetChecked = SetGenreActive,
                        DeferChanges = true,
                        EmptyHint = "Genres appear here once you add a pack.",
                    },
                },
            };
        }

        // ---- list cards: fortune packs (sources) + genres -----------------------------------------

        private IReadOnlyList<ListItem> LoadSourceItems()
        {
            var items = new List<ListItem>();
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledSources")), StringComparer.OrdinalIgnoreCase);
                foreach (SourceStat st in FortuneProvider.Sources())
                {
                    string detail = st.Count + (st.Count == 1 ? " line" : " lines");
                    if (st.HasSpicy) detail += " · spicy";
                    items.Add(new ListItem
                    {
                        Id = st.Id,
                        Label = PrettySource(st.Id),
                        Detail = detail,
                        Checked = !disabled.Contains(st.Id),
                        // The curated map is the only reliable signal for "is this a catalog pack?" --
                        // SourceStat.Custom is true for ANYTHING in the user's fortunes folder, which
                        // includes every catalog pack once downloaded, so it can't tell them apart.
                        Group = CollectionFor(st.Id),
                    });
                }
            }
            catch { }
            return items;
        }

        // ---- pack -> collection map (embedded copy of packs/collections.json) -----------------------

        private static Dictionary<string, string> _collectionBySource;

        /// <summary>The curated collection name for a pack id, or "More packs" when the map has no entry
        /// (a file the user wrote or imported, OR a catalog pack newer than this build's collection map --
        /// hence NOT "Your own packs", which wrongly implied the user added every pack in it). Loaded once,
        /// best-effort: a missing or malformed map just means everything groups under "More packs".</summary>
        private static string CollectionFor(string sourceId)
        {
            Dictionary<string, string> map = _collectionBySource;
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string json = ReadEmbeddedText("collections.json");
                    if (json != null)
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            JsonElement collections;
                            if (doc.RootElement.TryGetProperty("collections", out collections) &&
                                collections.ValueKind == JsonValueKind.Array)
                                foreach (JsonElement c in collections.EnumerateArray())
                                {
                                    JsonElement nameEl, sourcesEl;
                                    if (!c.TryGetProperty("name", out nameEl) ||
                                        !c.TryGetProperty("sources", out sourcesEl) ||
                                        sourcesEl.ValueKind != JsonValueKind.Array) continue;
                                    string name = nameEl.GetString() ?? "";
                                    if (name.Length == 0) continue;
                                    foreach (JsonElement src in sourcesEl.EnumerateArray())
                                    {
                                        string id = src.GetString();
                                        if (!string.IsNullOrEmpty(id)) map[id] = name;
                                    }
                                }
                        }
                }
                catch { }
                _collectionBySource = map;
            }
            string group;
            return map.TryGetValue(sourceId ?? "", out group) ? group : "More packs";
        }

        private IReadOnlyList<ListItem> LoadGenreItems()
        {
            var items = new List<ListItem>();
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledGenres")), StringComparer.OrdinalIgnoreCase);
                foreach (GenreStat g in FortuneProvider.Genres())
                    items.Add(new ListItem { Id = g.Id, Label = g.Id, Detail = g.Count + (g.Count == 1 ? " line" : " lines"), Checked = !disabled.Contains(g.Id) });
            }
            catch { }
            return items;
        }

        private void SetSourceActive(string id, bool active) { StageDisabled("disabledSources", id, !active); }
        private void SetGenreActive(string id, bool active) { StageDisabled("disabledGenres", id, !active); }

        // Both cards are DeferChanges, so these run at Apply, one call per box the user actually moved,
        // immediately before SavePaneValues. Staging them means the settings write and the engine rebuild
        // happen once for the whole batch: turning off a 19-pack group used to cost 19 disk writes and 19
        // full rebuilds (re-reading every pack file and re-warming the smart index each time).
        private void StageDisabled(string key, string id, bool disabled)
        {
            if (string.IsNullOrEmpty(id)) return;
            Dictionary<string, bool> staged;
            if (!_stagedDisabled.TryGetValue(key, out staged))
            {
                staged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                _stagedDisabled[key] = staged;
            }
            staged[id] = disabled;
        }

        // Fold the staged ids into the stored "disabled" lists. Caller owns the Save + rebuild.
        private void CommitStagedDisabled(IModuleSettings ms)
        {
            foreach (KeyValuePair<string, Dictionary<string, bool>> kv in _stagedDisabled)
            {
                if (kv.Value.Count == 0) continue;
                ms.Set(kv.Key, MergeDisabled(ms.Get(kv.Key, ""), kv.Value));
            }
            _stagedDisabled.Clear();
        }

        /// <summary>
        /// Apply a batch of staged toggles to a stored "disabled ids" list. Pure so the fold can be asserted
        /// directly: this decides which packs the engine reads, and a merge that dropped or double-added an
        /// id would quietly change what the pet is allowed to say. Ids the user did not touch keep whatever
        /// was stored; touched ids take the staged state. Matching is case-insensitive, as elsewhere.
        /// </summary>
        internal static string MergeDisabled(string stored, IDictionary<string, bool> staged)
        {
            var kept = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string x in SplitList(stored))
            {
                if (staged != null && staged.ContainsKey(x)) continue;   // re-added below if still disabled
                if (seen.Add(x)) kept.Add(x);
            }
            if (staged != null)
                foreach (KeyValuePair<string, bool> s in staged)
                    if (s.Value && seen.Add(s.Key)) kept.Add(s.Key);
            return string.Join("\n", kept);
        }

        private Task<string> OpenFortunesFolderAsync()
        {
            try
            {
                string dir = FortunePaths.FortunesDir;   // created on access
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Task.FromResult("Opened the fortunes folder — drop .txt packs there, then Rescan.");
            }
            catch (Exception ex) { return Task.FromResult("Couldn't open the folder: " + ex.Message); }
        }

        private Task<string> RescanAsync()
        {
            try
            {
                RebuildEngine();
                int sources = 0;
                try { sources = FortuneProvider.Sources().Count; } catch { }
                return Task.FromResult(sources == 0
                    ? "No packs found yet."
                    : ("Rescanned — " + sources + (sources == 1 ? " pack" : " packs") + " loaded."));
            }
            catch (Exception ex) { return Task.FromResult("Rescan failed: " + ex.Message); }
        }

        /// <summary>
        /// Import the user's own .txt packs through <see cref="FortuneFileImporter"/> — the strict, bounded,
        /// per-file atomic path (size/entry caps, staged writes) rather than a raw copy, so a malformed or
        /// oversized file is rejected instead of poisoning the pool. The host owns the file dialog (modules
        /// carry no UI framework). Existing files are never silently overwritten: nothing is approved for
        /// overwrite here, so a same-named pack is reported as skipped and the user renames or removes it.
        /// </summary>
        private Task<string> ImportPacksAsync()
        {
            IHost host = _host;
            if (host == null) return Task.FromResult("No host.");
            try
            {
                IReadOnlyList<string> chosen = host.PickFilesToOpen(
                    "Import fortune packs", "Fortune packs", new[] { "txt" });
                if (chosen == null || chosen.Count == 0) return Task.FromResult("");   // cancelled

                FortuneImportBatchResult result = FortuneFileImporter.Import(
                    chosen,
                    FortunePaths.FortunesDir,
                    null,                                   // no overwrite approved (see summary)
                    System.Threading.CancellationToken.None);

                if (result.ImportedCount > 0) RebuildEngine();   // new lines join the pool immediately

                string status = "Imported " + result.ImportedCount +
                    (result.ImportedCount == 1 ? " pack." : " packs.");
                if (result.RejectedCount > 0)
                {
                    string firstError = "";
                    foreach (FortuneImportItemResult item in result.Items)
                        if (!item.Imported && !string.IsNullOrWhiteSpace(item.Error)) { firstError = Short(item.Error); break; }
                    status += " " + result.RejectedCount + (result.RejectedCount == 1 ? " file" : " files") +
                        " rejected" + (firstError.Length > 0 ? " (" + firstError + ")" : "") + ".";
                }
                return Task.FromResult(status);
            }
            catch (Exception ex) { return Task.FromResult("✗ Import failed: " + Short(ex.Message)); }
        }

        // ---- catalog packs (browse + download through the host) -------------------------------------

        // Last browse result (catalog packs not on disk yet) and the subset the user ticked for download.
        // Both are in-memory only: browsing writes nothing, and ticking writes nothing — the download
        // button is the only thing that touches the network or the disk.
        private readonly List<CatalogItem> _availablePacks = new List<CatalogItem>();
        private readonly HashSet<string> _selectedPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<ListItem> LoadAvailablePackItems()
        {
            var items = new List<ListItem>();
            foreach (CatalogItem pack in _availablePacks)
            {
                // The group is its own field now (the card renders collapsible sections), so the detail
                // stays the per-pack facts: how much content, and roughly how big.
                string detail = pack.Count > 0
                    ? (pack.Count + (pack.Count == 1 ? " line" : " lines"))
                    : ApproximateSize(pack.Bytes);
                items.Add(new ListItem
                {
                    Id = pack.Id,
                    Label = string.IsNullOrWhiteSpace(pack.Name) ? PrettySource(pack.Id) : pack.Name,
                    Detail = detail,
                    Checked = _selectedPacks.Contains(pack.Id),
                    Group = string.IsNullOrWhiteSpace(pack.Group) ? CollectionFor(pack.Id) : pack.Group.Trim(),
                });
            }
            return items;
        }

        // Ticking a row only marks it — instant, no network (SetChecked is synchronous by contract).
        private void SetPackSelected(string id, bool selected)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (selected) _selectedPacks.Add(id);
            else _selectedPacks.Remove(id);
        }

        private Task<string> SelectAllPacksAsync()
        {
            foreach (CatalogItem pack in _availablePacks)
                if (!string.IsNullOrEmpty(pack.Id)) _selectedPacks.Add(pack.Id);
            return Task.FromResult(_availablePacks.Count == 0
                ? "Nothing listed yet — click “Check online for packs” first."
                : ("Selected all " + _availablePacks.Count + " packs."));
        }

        private Task<string> SelectNoPacksAsync()
        {
            _selectedPacks.Clear();
            return Task.FromResult("Cleared the selection.");
        }

        /// <summary>Fetch the catalog and list the packs that aren't installed yet. Read-only: nothing is
        /// downloaded, written, or selected — the user picks from the list, then hits Download selected.</summary>
        private async Task<string> CheckPacksOnlineAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            try
            {
                IReadOnlyList<CatalogItem> items = await host.FetchCatalogItemsAsync(CatalogKinds.Pack).ConfigureAwait(false);
                int available = CacheMissingPacks(items);
                if (items.Count == 0) return "The catalog lists no fortune packs.";
                return available == 0
                    ? ("You already have every catalog pack (" + items.Count + ").")
                    : (available + (available == 1 ? " pack" : " packs") +
                       " available — tick the ones you want, then “Download selected”.");
            }
            catch (Exception ex) { return "✗ Couldn't reach the catalog: " + Short(ex.Message); }
        }

        /// <summary>Download the ticked packs, then rebuild the engine so they're live immediately. Each
        /// pack's bytes are HTTPS-fetched and SHA-256-verified by the host before they reach us; we only
        /// decide the filename, and reject any id that isn't a plain pack id so a catalog entry can never
        /// steer the write outside the fortunes folder.</summary>
        private async Task<string> DownloadPacksAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            if (_availablePacks.Count == 0)
                return "Nothing listed yet — click “Check online for packs” first.";
            if (_selectedPacks.Count == 0)
                return "No packs ticked — choose some (or “Select all”), then Download selected.";
            try
            {
                string directory = FortunePaths.FortunesDir;   // created on access
                var pending = new List<CatalogItem>();
                foreach (CatalogItem item in _availablePacks)
                    if (item != null && _selectedPacks.Contains(item.Id)) pending.Add(item);

                int installed = 0, failed = 0;
                string lastError = "";
                foreach (CatalogItem item in pending)
                {
                    try
                    {
                        if (!IsPlainPackId(item.Id)) { failed++; continue; }
                        byte[] bytes = await host.DownloadCatalogItemAsync(CatalogKinds.Pack, item.Id).ConfigureAwait(false);
                        if (bytes == null || bytes.Length == 0) { failed++; continue; }
                        File.WriteAllBytes(Path.Combine(directory, item.Id + ".txt"), bytes);
                        _selectedPacks.Remove(item.Id);
                        installed++;
                    }
                    catch (Exception ex) { failed++; lastError = Short(ex.Message); }
                }

                RebuildEngine();   // the new packs join the pool (and the smart index) right away
                // Drop the installed ones from the available list so the card shows what's still missing.
                CacheMissingPacks(_availablePacks);
                string status = "Downloaded " + installed + (installed == 1 ? " pack." : " packs.");
                if (failed > 0)
                    status += " " + failed + (failed == 1 ? " pack" : " packs") + " failed" +
                        (lastError.Length > 0 ? " (" + lastError + ")" : "") + ".";
                return status;
            }
            catch (Exception ex) { return "✗ Download failed: " + Short(ex.Message); }
        }

        /// <summary>Replace the cached browse result with the catalog packs that aren't on disk yet, and
        /// forget any selection for packs that no longer apply. Returns how many are available.</summary>
        private int CacheMissingPacks(IReadOnlyList<CatalogItem> items)
        {
            var source = new List<CatalogItem>(items ?? (IReadOnlyList<CatalogItem>)new List<CatalogItem>());
            _availablePacks.Clear();
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (SourceStat st in FortuneProvider.Sources()) installed.Add(st.Id);
            }
            catch { }
            foreach (CatalogItem item in source)
                if (item != null && !string.IsNullOrEmpty(item.Id) && !installed.Contains(item.Id))
                    _availablePacks.Add(item);
            _selectedPacks.RemoveWhere(id => installed.Contains(id));
            return _availablePacks.Count;
        }

        private static string ApproximateSize(int bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        // A pack id must be a bare file-name stem: no separators, no drive/relative parts. The host already
        // validates catalog ids, but this module writes the file, so it re-checks rather than trusting.
        private static bool IsPlainPackId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
            return id.IndexOf("..", StringComparison.Ordinal) < 0 &&
                !string.Equals(id, ".", StringComparison.Ordinal);
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 160 ? message.Substring(0, 160) + "…" : message;
        }

        private string GetSetting(string key)
        {
            try { IModuleSettings ms = _host != null ? _host.GetSettings("fortunes") : null; return ms != null ? ms.Get(key, "") : ""; }
            catch { return ""; }
        }

        // Persisted disabled-list format: ids joined by '\n' (source ids/genres never contain newlines).
        private static List<string> SplitList(string joined)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(joined)) return list;
            foreach (string part in joined.Split('\n'))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// The label for a pack: its curated display name when known, else a prettified id. Pack ids are
        /// raw file stems ("lwall-quotes", "rfc1925", "off-knghtbrd") that say nothing about what the pack
        /// contains, so the curated map is what makes the picker readable; the prettified fallback keeps a
        /// user's own file (or a catalog pack newer than this build) from showing up blank.
        /// </summary>
        private static string PrettySource(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            string name;
            if (PackNames().TryGetValue(id, out name) && !string.IsNullOrWhiteSpace(name)) return name;
            return id.Replace('-', ' ').Replace('_', ' ');
        }

        private static Dictionary<string, string> _packNames;

        private static Dictionary<string, string> PackNames()
        {
            Dictionary<string, string> map = _packNames;
            if (map != null) return map;
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string json = ReadEmbeddedText("pack-names.json");
                if (json != null)
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        JsonElement names;
                        if (doc.RootElement.TryGetProperty("names", out names) &&
                            names.ValueKind == JsonValueKind.Object)
                            foreach (JsonProperty p in names.EnumerateObject())
                                if (p.Value.ValueKind == JsonValueKind.String)
                                    map[p.Name] = p.Value.GetString() ?? "";
                    }
            }
            catch { }
            _packNames = map;
            return map;
        }

        /// <summary>Read one of the module's embedded JSON maps, or null when absent/unreadable. The lookup
        /// itself is ModuleKit's; only the null-rather-than-empty result is local, because the callers here
        /// branch on null.</summary>
        private static string ReadEmbeddedText(string fileName)
        {
            string text = EmbeddedResources.LoadText(typeof(FortunesModule).Assembly, fileName);
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            FortuneSettings s = LoadFortuneSettings(_host);
            d["smartFortunes"] = s.SmartFortunes ? "true" : "false";
            d["contentLevel"] = LevelToDisplay(s.ContentLevel);
            d["noProfanity"] = s.NoProfanity ? "true" : "false";
            d["poolStatus"] = PoolStatusText();
            return d;
        }

        /// <summary>
        /// What the current filters actually leave to say. An empty pool is a legitimate outcome (every
        /// filter is a hard constraint), but it makes the pet fall silent — so it is reported as a ✗ with
        /// the reason, rather than leaving the user to wonder whether something is broken.
        /// </summary>
        private string PoolStatusText()
        {
            FortuneProvider provider = _provider;
            if (provider == null) return "✗ The fortune engine isn't loaded.";
            int lines = provider.Count;
            if (lines == 0) return "✗ " + EmptyPoolReason(AnyPacksInstalled());

            int packs = 0, total = 0;
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledSources")), StringComparer.OrdinalIgnoreCase);
                foreach (SourceStat st in FortuneProvider.Sources())
                {
                    total++;
                    if (!disabled.Contains(st.Id)) packs++;
                }
            }
            catch { }

            return PoolStatusFor(lines, packs, total);
        }

        /// <summary>
        /// What the current filters actually leave to draw from.
        ///
        /// A COLLAPSED pool used to read as a healthy one. Reporting "2,794 fortunes from 1 pack" with a tick
        /// is technically true and practically misleading: the reason the same dad joke turned up five times
        /// in a day was that 157 of 190 sources were switched off, leaving one pack, and nothing in the UI
        /// treated that as a problem. An empty pool was called out; a nearly-empty one was not.
        ///
        /// So a pool drawing on a small minority of the installed sources now warns instead of ticking, and
        /// says how many are off, because that is the setting the user has to change. Pure, so the wording is
        /// asserted rather than eyeballed.
        /// </summary>
        internal static string PoolStatusFor(int lines, int enabledSources, int totalSources)
        {
            string counted = lines.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            string fortunes = counted + (lines == 1 ? " fortune" : " fortunes");
            if (enabledSources <= 0)
                return "✓ " + fortunes + " available.";

            string from = fortunes + " from " + enabledSources +
                          (enabledSources == 1 ? " source" : " sources");

            // "Most of the corpus is off" is the condition worth surfacing: one source, or under a quarter of
            // them. Below that threshold repeats arrive far sooner than the fortune count suggests.
            bool collapsed = totalSources > 1 &&
                             (enabledSources == 1 || enabledSources * 4 < totalSources);
            if (!collapsed) return "✓ " + from + ".";

            int off = totalSources - enabledSources;
            return "⚠ " + from + " — " + off + " of " + totalSources +
                   " are switched off, so fortunes repeat much sooner than that count suggests. " +
                   "Enable more sources below to widen the pool.";
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IHost host = _host;
            if (host == null || values == null) return false;
            IModuleSettings ms = host.GetSettings("fortunes");
            if (ms == null) return false;
            string v; bool b;
            if (values.TryGetValue("smartFortunes", out v) && bool.TryParse(v, out b)) ms.Set("smartFortunes", b ? "true" : "false");
            if (values.TryGetValue("contentLevel", out v) && !string.IsNullOrEmpty(v))
            {
                ms.Set("contentLevel", DisplayToLevel(v));
                // Drop the superseded trio so a stale value can never be re-migrated over the new one.
                ms.Set("spicyFortunes", "");
                ms.Set("spicyTier", "");
                ms.Set("spicyOnly", "");
            }
            if (values.TryGetValue("noProfanity", out v) && bool.TryParse(v, out b)) ms.Set("noProfanity", b ? "true" : "false");
            // The host has just replayed the pack/genre ticks into the staging map (DeferChanges), so they
            // join this same write rather than each paying for their own.
            CommitStagedDisabled(ms);
            bool ok = ms.Save();
            RebuildEngine();   // re-read + rebuild so the running pet uses the new settings at once
            return ok;
        }

        /// <summary>
        /// "Show me 5 examples": draw five lines the CURRENT selection would actually produce, so a content
        /// level or pack selection can be auditioned before living with it. Reads the saved settings, not
        /// the unapplied edits in the boxes, so it always reflects what the pet would really say — hit Apply
        /// first to preview a change.
        /// </summary>
        private Task<string> PreviewFortunesAsync()
        {
            FortuneProvider provider = _provider;
            if (provider == null) return Task.FromResult("The fortune engine isn't loaded.");
            if (provider.Count == 0)
                return Task.FromResult("✗ Nothing to show — these filters leave no fortunes.");

            // Pick() already avoids repeating the previous line, so a small pool simply yields fewer
            // distinct samples rather than the same one five times.
            var seen = new List<string>();
            for (int attempt = 0; attempt < 25 && seen.Count < 5; attempt++)
            {
                string line = provider.Pick();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!seen.Contains(line)) seen.Add(line);
            }
            if (seen.Count == 0) return Task.FromResult("✗ Nothing to show — these filters leave no fortunes.");

            // Blank line between samples: fortunes are themselves sentence-length and often wrap, so
            // single-spaced bullets run together into a wall of text.
            var sb = new StringBuilder();
            foreach (string line in seen)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("• ").Append(Ellipsize(line, 160));
            }
            return Task.FromResult(sb.ToString());
        }

        private static string Ellipsize(string value, int maximum)
        {
            string one = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length > maximum ? one.Substring(0, maximum) + "…" : one;
        }

        /// <summary>"Rebuild smart index" action: reload packs from disk and (when smart is on) re-warm the
        /// semantic index, then report status. Also the way to pick up a pack dropped straight into the
        /// folder until the sources/packs card lands.</summary>
        private Task<string> RebuildSmartIndexAsync()
        {
            try
            {
                // A finished index over the same pool has nothing to redo, and silently re-warming it looks
                // identical to a broken button. Say so instead.
                SmartFortunes sm = _smart;
                FortuneProvider provider = _provider;
                if (sm != null && provider != null && provider.Count > 0 && _indexedSignature != null)
                {
                    bool ready, complete; int indexed, total;
                    sm.WarmProgress(out ready, out complete, out indexed, out total);
                    if (complete && _indexedSignature == PoolSignature(provider.PoolEntries()))
                        return Task.FromResult("Smart index is already built for these " + Count(indexed) +
                            " fortunes — nothing to rebuild.");
                }
                RebuildEngine();
                return Task.FromResult(SmartStatusText());
            }
            catch (Exception ex) { return Task.FromResult("Rebuild failed: " + ex.Message); }
        }

        /// <summary>
        /// A content fingerprint of the indexed pool, so "rebuild" can tell an unchanged selection from a
        /// real one. The line count alone would miss a swap of one pack for another of the same size.
        /// </summary>
        internal static string PoolSignature(List<FortuneEntry> pool)
        {
            if (pool == null) return "0:" + 0.ToString("x16");
            ulong hash = 14695981039346656037UL;   // FNV-1a 64
            unchecked
            {
                foreach (FortuneEntry e in pool)
                {
                    string text = e.Text ?? "";
                    for (int i = 0; i < text.Length; i++) { hash ^= text[i]; hash *= 1099511628211UL; }
                    hash ^= '\n'; hash *= 1099511628211UL;
                }
            }
            return pool.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + hash.ToString("x16");
        }

        private string SmartStatusText()
        {
            SmartFortunes sm = _smart;
            FortuneProvider provider = _provider;
            bool ready = false, complete = false; int indexed = 0, total = 0;
            if (sm != null) sm.WarmProgress(out ready, out complete, out indexed, out total);
            return SmartStatusFor(sm != null, provider == null ? 0 : provider.Count, AnyPacksInstalled(),
                ready, complete, indexed, total);
        }

        /// <summary>
        /// What to tell the user about the smart index. Pure so the wording can be asserted, because the
        /// obvious reading of the index's own counters is wrong: Warm() runs in the background and leaves
        /// ready=false / total=0 until its first batch publishes, so a status derived from those alone
        /// reported "no fortunes" every single time the Rebuild button was pressed, however full the pool.
        /// The pool size is known synchronously from the provider, so take it from there and let the index's
        /// counters answer only "how far along".
        /// </summary>
        internal static string SmartStatusFor(bool smartEnabled, int poolCount, bool anyPacksInstalled,
            bool ready, bool complete, int indexed, int total)
        {
            if (!smartEnabled) return "Smart picks are off (random selection).";
            if (poolCount == 0) return EmptyPoolReason(anyPacksInstalled);
            if (complete) return "Smart index ready — " + Count(indexed) + " fortunes indexed.";
            if (ready) return "Smart index warming — " + Count(indexed) + " of " + Count(total) + " ready (usable now).";
            return "Indexing " + Count(poolCount) + " fortunes in the background — smart picks switch on as it goes.";
        }

        /// <summary>
        /// Why the pool is empty, in the user's terms. Nothing installed is a different problem from
        /// everything filtered out, and telling someone with 129 packs to "add a pack" sends them the
        /// wrong way entirely.
        /// </summary>
        internal static string EmptyPoolReason(bool anyPacksInstalled)
        {
            return anyPacksInstalled
                ? "No fortunes match these filters — the pet will stay silent. " +
                  "Widen the content level, or enable more packs below."
                : "No fortunes yet — add a pack, then rebuild.";
        }

        private static bool AnyPacksInstalled()
        {
            try { foreach (SourceStat st in FortuneProvider.Sources()) return true; }
            catch { }
            return false;
        }

        private static string Count(int n)
        {
            return n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        }

        /// <summary>Pick a welcome line and substitute the name into its {name} slot (fallback "friend").</summary>
        internal string PickWelcome(string name)
        {
            string[] corpus = _welcome;
            if (corpus == null || corpus.Length == 0) return null;
            string who = string.IsNullOrWhiteSpace(name) ? "friend" : name.Trim();
            string line = corpus[_rand.Next(corpus.Length)];
            return line == null ? null : line.Replace("{name}", who);
        }

        private static string CurrentUserName()
        {
            try
            {
                string u = Environment.UserName;
                return string.IsNullOrWhiteSpace(u) ? "friend" : u;
            }
            catch { return "friend"; }
        }

        // Who to greet: the host-published owner name (set by the AI brain when it's on) wins; otherwise fall
        // back to the Windows user name. Keeps the out-of-box welcome, but lets the configured AI name override.
        private static string GreetingName(IHost host)
        {
            try
            {
                string owner = host != null ? host.OwnerName : null;
                if (!string.IsNullOrWhiteSpace(owner)) return owner.Trim();
            }
            catch { }
            return CurrentUserName();
        }

        public void Shutdown()
        {
            IHost host = _host;
            if (host != null)
            {
                host.CompanionSpawned -= OnPetSpawned;
                host.CompanionLanded -= OnPetLanded;
                host.CompanionPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_pokeResponder != null) { try { _pokeResponder.Dispose(); } catch { } _pokeResponder = null; }
            if (_smart != null) { try { _smart.Dispose(); } catch { } _smart = null; }
            _provider = null;
            _host = null;
        }

        /// <summary>
        /// Load the embedded welcome corpus (a JSON array of "{name}"-templated one-liners). Returns an empty
        /// array on any failure so the module simply stays quiet rather than throwing into the host.
        /// </summary>
        private static string[] LoadWelcomeCorpus()
        {
            return EmbeddedResources.LoadJson<string[]>(typeof(FortunesModule).Assembly, "welcome.json")
                ?? Array.Empty<string>();
        }

        /// <summary>Self-test hook (NOT the ABI): number of welcome lines loaded.</summary>
        public int WelcomeCorpusCount() { return _welcome == null ? 0 : _welcome.Length; }
    }
}
