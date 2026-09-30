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
        // Which rebuild the picker currently being built belongs to. RebuildEngine is reachable from
        // Init, SavePaneValues, RescanAsync, ImportPacksAsync, DownloadPacksAsync and
        // RebuildSmartIndexAsync, so two can overlap; without this an earlier, slower build could land
        // after a later one and quietly replace a current picker with a stale one.
        private int _smartGeneration;
        // Guards the pair (generation bump, clear _smart) against the pair (generation check, publish). The
        // check and the publish were two unlocked steps, so a build that had just passed the check could
        // publish after RebuildEngine had cleared the field and moved the generation on: a superseded
        // picker went live and the next build overwrote it without disposing it (F144). Held only for the
        // field swaps, never across construction, Warm or Dispose.
        private readonly object _smartLock = new object();
        private bool _smartBuilding;               // a build for the current generation is in flight or published (under _smartLock)
        private int _engineRebuilds;               // diagnostics: how many times a provider was published
        // The SETTING as of the last rebuild, which is what the pane's status has to report as "enabled".
        // It reported `_smart != null`, and since 1.0.6 backgrounded construction the field is null for the
        // whole time a build is in flight, so pressing "Rebuild smart index" answered "Smart picks are off"
        // with the box ticked, on every press that actually rebuilt (F148).
        private volatile bool _smartWanted;
        private volatile bool _smartBuildFailed;   // the current generation's construction threw (F148)
        // Every rebuild, synchronous or not, takes a generation; an asynchronous parse publishes only if no
        // later rebuild has moved it on, so two overlapping pane actions cannot leave the older folder state
        // live (F127). The same shape as the smart build's generation.
        private int _engineGeneration;
        private int _lastParseThread;              // diagnostics: the thread the last asynchronous parse ran on
        // Cancels a pack import in flight when the module shuts down; the importer honours the token
        // throughout staging and once more before it commits, and rolls back what it had committed.
        private readonly System.Threading.CancellationTokenSource _shutdown =
            new System.Threading.CancellationTokenSource();
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
            Version = "1.0.12",  // 1.0.12: the pane's smart-index status reads the SETTING and the stand-down
                                 //         reason instead of whether a picker object exists yet; pack parses
                                 //         and imports leave the UI thread; a damaged undeclared tagged pack is
                                 //         refused rather than recited as prose; the vector cache stops
                                 //         re-reading itself at every checkpoint and drops its raw copy once
                                 //         the warm has saved. (The 2026-09-29 audit's Fortunes findings.)
                                 //         Lane fix/deadcode, same version: a downloaded pack is validated
                                 //         before it is written and a refusal is its own download cause; the
                                 //         engine's and the embedder's sub-reports reach the probe output; the
                                 //         pack policy is compiled from the host's file; a second Warm on one
                                 //         picker is proven to supersede the first (F120, F124, F131, F136).
                                 // 1.0.11: exposes SelfTest on the module class, so --module-selftest runs
                                 //         FortuneEngineProbe through the convention the gate and CI use.
                                 // 1.0.10: the smart-index status no longer reads "Indexing N fortunes in the
                                 //         background" for ever when the text engine could not start. That
                                 //         stand-down was only ever in the diagnostic log; now it is on the
                                 //         pane, and says fortunes are being chosen at random.
                                 // 1.0.9: drops a write-only settings clear. "spicyTier" was set to "" on
                                 //        migration to stop "a stale value being re-migrated"; nothing ever
                                 //        wrote or read that key. No behaviour change.
                                 // 1.0.8: payload refresh only. Deletes SmartFortunes.LastCandidateCount, written on
                                 //        every contextual pick and read nowhere. LastBandCount is the number the
                                 //        margin controls and it keeps its self-test assertion. No behaviour change.
                                 // 1.0.7: a custom pack that fails to read or parse no longer spends its share of the
                                 //        16 MB budget. The bytes were charged from the file length before the strict
                                 //        UTF-8 read, so one bad pack could starve every valid pack that sorted after
                                 //        it, silently -- nothing reports a budget exhaustion.
                                 // 1.0.6: the smart picker is now BUILT off the UI thread, not just warmed
                                 //        there, and logs when it has been constructed. (That line read
                                 //        "ready" until 1.0.12; the warm reports its own completion now.)
                                 // 1.0.5: the smart picker rotated 64 lines out of 7780, so the same
                                 //        fortune came round every third pick in a stable context. The
                                 //        candidate set is now a relevance band, not a fixed count.
                                 // 1.0.4: a failed save no longer discards the staged pack selection, and the
                                 //        engine probe stopped pinning a culture-formatted number.
                                 // 1.0.3: the smart picker now says why it stood down when the model
                                 //        asset is PRESENT but the native onnxruntime did not load. That
                                 //        was a bare return, so the status answered "Smart index
                                 //        warming ..." for ever: a state indistinguishable from progress
                                 //        that never resolves. Reported through a static LogSink, the
                                 //        pattern AiBrain and ScrollLockBlinker already use.
                                 // 1.0.1: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded, which also gives authors symbols the discarded
                                 //        .snupkg never delivered). NO functional change here. The bump exists
                                 //        because the catalog offers an update by VERSION, so at 1.0.0 the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
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
            // The pet-aware responders this module needs shipped in the host before the public renumbering
            // (pre-release 1.5.0), so every public host has them and 1.0.0 is the floor. Declaring a floor at
            // all means a host below it refuses the module with a legible reason instead of loading it and
            // broadcasting every fortune.
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
                {
                    FortunePaths.SetRoot(storage.DataDirectory);
                    // The user's drop folder exists from the first run: "Open fortunes folder" opens
                    // something, and the host's --fortunes-engine-selftest reads its presence under the
                    // storage root as the proof that this redirect took (F338). Reading the folder no
                    // longer creates it, so it is created here, and only here: a host that hands no storage
                    // reaches this line never, and the TEMP fallback root stays empty (N-gates-02).
                    FortunePaths.CreateFortunesDir();
                }
            }
            catch { }
            // Wired BEFORE RebuildEngine, because RebuildEngine is what starts the warm task and
            // a sink attached afterwards would miss the first stand-down -- which is the one that
            // matters on a machine where the embedder never loads at all.
            SmartFortunes.LogSink = delegate(string line) { Log(line); };
            // The loader's sink too, for the same reason: the first folder parse is the one inside
            // RebuildEngine, and a pack it refuses is refused right there.
            FortuneProvider.LogSink = delegate(string line) { Log(line); };
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
        /// Options pane saves, so a settings change (or a pack added to the folder) applies without a restart.
        /// Synchronous, on the caller's thread: Init's contract is a usable module when it returns (the host's
        /// own --fortunes-selftest raises CompanionLanded straight after LoadFrom), and Apply's is a pool
        /// status the pane can read the moment it rebuilds -- and Apply is a cache hit unless the folder
        /// changed. The pane actions that DO change the folder rebuild through
        /// <see cref="RebuildEngineAsync"/>, off the UI thread.</summary>
        private void RebuildEngine()
        {
            RebuildEngine(false);
        }

        /// <param name="force">Rebuild the smart picker even when the pool it indexes is unchanged: the
        /// "Rebuild smart index" button's meaning, so a picker that stood down or is mid-warm is restarted on
        /// request. Every other caller keeps an unchanged index (F147).</param>
        private void RebuildEngine(bool force)
        {
            System.Threading.Interlocked.Increment(ref _engineGeneration);
            FortuneSettings settings;
            FortuneProvider provider;
            try
            {
                settings = LoadFortuneSettings(_host);
                provider = new FortuneProvider(settings);
            }
            catch (Exception ex)
            {
                EngineRebuildFailed(ex);
                return;
            }
            PublishEngine(settings, provider, force);
        }

        /// <summary>
        /// The rebuild for the pane actions whose purpose IS a changed folder: Rescan, Import, Download and
        /// the Rebuild button. The settings are read here, on the UI thread where IHost.GetSettings belongs;
        /// the parse runs on a pool thread; the publish runs on the continuation, which is the UI thread in
        /// the app because the host awaits a PaneAction from the button's click handler and nothing here
        /// uses ConfigureAwait(false). The corpus parse used to run inline in that click handler: 1.2-1.4 s
        /// with the catalog installed, measured by the audit (F127). A rebuild that a later one overtook
        /// publishes nothing, since the later one's provider describes the newer folder. Same shape as
        /// AiBrainModule.BeginVramProbe and the smart build above: a generation, Task.Run, a continuation
        /// that drops a stale result.
        /// </summary>
        private async Task RebuildEngineAsync(bool force)
        {
            int generation = System.Threading.Interlocked.Increment(ref _engineGeneration);
            FortuneSettings settings;
            try { settings = LoadFortuneSettings(_host); }
            catch (Exception ex) { EngineRebuildFailed(ex); return; }
            FortuneProvider provider;
            try
            {
                provider = await Task.Run(delegate
                {
                    System.Threading.Volatile.Write(ref _lastParseThread, Environment.CurrentManagedThreadId);
                    return new FortuneProvider(settings);
                });
            }
            catch (Exception ex)
            {
                if (System.Threading.Volatile.Read(ref _engineGeneration) == generation) EngineRebuildFailed(ex);
                return;
            }
            if (System.Threading.Volatile.Read(ref _engineGeneration) != generation) return;   // overtaken meanwhile
            PublishEngine(settings, provider, force);
        }

        /// <summary>Diagnostics: the thread the last asynchronous rebuild parsed on, 0 before any.</summary>
        internal int LastParseThreadForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _lastParseThread); }
        }

        /// <summary>Diagnostics: whether the live pool holds this exact line.</summary>
        internal bool PoolContainsForDiagnostics(string text)
        {
            FortuneProvider provider = _provider;
            if (provider == null) return false;
            foreach (FortuneEntry e in provider.PoolEntries())
                if (string.Equals(e.Text, text, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// The tail every rebuild shares once its provider exists, on the UI thread (or the self-test's):
        /// publish the provider, decide what to do about the smart picker, then log the engine line.
        /// </summary>
        private void PublishEngine(FortuneSettings settings, FortuneProvider provider, bool force)
        {
            // Gathered inside the try, FORMATTED outside it (see the Log call at the bottom).
            int fortunes = 0, disabledSources = 0;
            string level = null;
            bool smart = false, modelPresent = false;
            List<FortuneEntry> pool = null;
            try
            {
                _provider = provider;
                System.Threading.Interlocked.Increment(ref _engineRebuilds);
                pool = provider.PoolEntries();
                _smartWanted = settings.SmartFortunes;
                ScheduleSmartPicker(settings.SmartFortunes, pool, force);
                fortunes = provider.Count;
                disabledSources = settings.DisabledSources == null ? 0 : settings.DisabledSources.Count;
                level = settings.ContentLevel;
                smart = settings.SmartFortunes;
                modelPresent = smart && Embedder.ModelPresent;
            }
            catch (Exception ex)
            {
                EngineRebuildFailed(ex);
                return;
            }

            // OUTSIDE the try, deliberately. Counting and formatting the line above would put a fault in
            // the DIAGNOSTIC inside the catch that nulls the provider — i.e. a bug in a log line would
            // silence the companion permanently, which is precisely the failure this line exists to
            // report. Nothing here touches the engine; the values are already in hand.
            Log(DescribeEngine(fortunes, CountPoolSources(pool), disabledSources, level, smart, modelPresent));
        }

        private void EngineRebuildFailed(Exception ex)
        {
            _provider = null;
            SmartFortunes doomed;
            lock (_smartLock)
            {
                System.Threading.Interlocked.Increment(ref _smartGeneration);   // orphan any in-flight build
                doomed = _smart;
                _smart = null;
                _indexedSignature = null;
                _smartBuilding = false;
                _smartBuildFailed = false;
            }
            DisposeOffThread(doomed);
            // The total failure, which was the one state nothing anywhere reported: a null provider
            // makes SpeakFortune return false on every land, poke and drop, so the companion simply
            // never says a fortune again and the pane's own status reads "the fortune engine isn't
            // loaded" only if the user happens to open it.
            // ASCII only in a logged string, like every other module's lines: this file is read back
            // by whoever opens the attachment, not rendered by the app.
            Log("engine rebuild failed: " + Categorize(ex) + " (no fortunes will be spoken)");
        }

        /// <summary>
        /// Keep, drop or (re)build the smart picker for a freshly published pool.
        ///
        /// KEEP when nothing it indexes has changed: smart is still on, the pool's signature equals the one
        /// the current picker -- built or still building -- was started on, and that build did not fail.
        /// Apply used to dispose and rebuild the picker every time (F147): an Apply that ticked a box and
        /// unticked it again, or changed only a setting the pool does not depend on, paid a full cache load
        /// and re-centre and lost smart picks for the duration. Otherwise the current picker is superseded
        /// (the generation bumped under the lock, so an in-flight build drops itself) and a new build queued.
        /// </summary>
        private void ScheduleSmartPicker(bool wanted, List<FortuneEntry> pool, bool force)
        {
            // Recorded before the warm starts: it names the pool being indexed, which is what a later
            // "is this still current?" question compares against.
            string signature = wanted ? PoolSignature(pool) : null;
            SmartFortunes old;
            int generation;
            lock (_smartLock)
            {
                bool current = wanted && !force && _smartBuilding && !_smartBuildFailed &&
                               string.Equals(signature, _indexedSignature, StringComparison.Ordinal);
                if (current) return;
                generation = System.Threading.Interlocked.Increment(ref _smartGeneration);
                old = _smart;
                _smart = null;
                _indexedSignature = signature;
                _smartBuildFailed = false;
                _smartBuilding = wanted;
            }
            if (!wanted)
            {
                DisposeOffThread(old);
                return;
            }

            // CONSTRUCTION is backgrounded too, not just the warm -- and so is the DISPOSAL of the picker
            // being replaced.
            //
            // Only Warm was off the UI thread. The SmartFortunes constructor is synchronous and its
            // VectorCache ctor ends in Load(), which takes a Global mutex plus a .lock lease and then
            // deserialises cache.bin, one float[384] per entry. With all 161 catalog packs at "Everything"
            // that file reaches ~94 MB: 59,000 allocations before the pet appears. And Dispose cancels the
            // old picker's warm and then waits, up to 3 s, for whichever uncancellable native call is in
            // flight (the ONNX session load, one inference) to return; that wait ran on the UI thread on
            // every Apply, Rescan, Import and Download (F143).
            //
            // _smart is published only once the picker is usable, and the pick path already snapshots the
            // field into a local before using it, so a null here simply means the next few fortunes come
            // from the whole-pool shuffle bag instead.
            System.Threading.Tasks.Task.Run(delegate { BuildSmartPicker(generation, old, pool); });
        }

        /// <summary>Pool thread: dispose the picker being replaced, construct and warm the next one, and
        /// publish it unless the module moved on meanwhile.</summary>
        private void BuildSmartPicker(int generation, SmartFortunes old, List<FortuneEntry> pool)
        {
            // Disposed BEFORE the replacement is constructed, so the peak is one ONNX session.
            if (old != null) { try { old.Dispose(); } catch { } }
            SmartFortunes built = null;
            try
            {
                built = new SmartFortunes();
                built.Warm(pool);
                bool superseded;
                lock (_smartLock)
                {
                    // CHECK AND PUBLISH UNDER THE ONE LOCK that RebuildEngine and Shutdown take to bump
                    // the generation and clear the field (F144). Superseded while we were building: drop
                    // it rather than overwrite a newer picker with an older one.
                    superseded = System.Threading.Volatile.Read(ref _smartGeneration) != generation;
                    if (!superseded) _smart = built;
                }
                if (superseded)
                {
                    try { built.Dispose(); } catch { }
                    return;
                }
                // The engine line says smart=on from the SETTING, which is true even when the picker
                // never became usable. This says the picker object exists and its warm is queued; the
                // warm itself reports completion, cancellation and every stand-down through the sink.
                // Until 1.0.12 this line read "smart picker ready (N lines indexed)" at this very moment,
                // when nothing had been embedded yet (F145).
                Log(DescribeSmartBuild(pool.Count));
            }
            catch (Exception ex)
            {
                if (built != null) { try { built.Dispose(); } catch { } }
                lock (_smartLock)
                {
                    if (System.Threading.Volatile.Read(ref _smartGeneration) == generation)
                    {
                        // Recorded in STATE, so the pane can say "unavailable" instead of "indexing"
                        // for ever (F148); the next rebuild clears it and tries again.
                        _smartBuildFailed = true;
                        _smartBuilding = false;
                    }
                }
                Log("smart picker unavailable: " + Categorize(ex) + " (fortunes stay random)");
            }
        }

        /// <summary>The line logged when a picker is published: what is TRUE at that moment. Pure, so the
        /// wording is asserted -- it must not claim readiness the warm has not reached.</summary>
        internal static string DescribeSmartBuild(int lines)
        {
            return "smart picker constructed, warming " + Invariant(lines) + " lines in the background";
        }

        /// <summary>A superseded picker's Dispose can wait up to 3 s on a native call; never on the UI
        /// thread (F143). Shutdown is the one caller that disposes inline, at process exit.</summary>
        private static void DisposeOffThread(SmartFortunes doomed)
        {
            if (doomed == null) return;
            System.Threading.Tasks.Task.Run(delegate { try { doomed.Dispose(); } catch { } });
        }

        /// <summary>
        /// The one line that answers the three complaints this module actually gets: "it never says
        /// anything", "the same fortune keeps coming back", and "I downloaded packs and nothing changed".
        /// Pure, so the wording is asserted rather than eyeballed.
        ///
        /// <para>Every field is either a count or a fixed vocabulary word. No pack names, no paths, and no
        /// fortune text ever: this is the module whose whole payload is content a user chose to install, and
        /// the diagnostic log is what SUPPORT.md tells them they can attach to a public issue.</para>
        ///
        /// <para><paramref name="disabledSources"/> rather than a total: the two counts carry what "33 of 190"
        /// carries, and the caller has both without another walk. (This used to cite the cost of
        /// <c>FortuneProvider.Sources()</c> re-reading every pack; every corpus tier is cached now, the folder
        /// on a fingerprint, so that reason is gone and the shape stays for the information, F141.)
        /// <paramref name="modelPresent"/> is the
        /// silent-degradation case — with smart picks ON and the bge-small asset missing from the payload,
        /// <c>SmartFortunes</c> can never become ready and every pick quietly falls back to random. (The
        /// pane used to go on reporting "indexing in the background" in that state; since 1.0.12 it says
        /// the index is unavailable and why.)</para>
        /// </summary>
        internal static string DescribeEngine(int fortunes, int enabledSources, int disabledSources,
            string contentLevel, bool smart, bool modelPresent)
        {
            return "engine: fortunes=" + Invariant(fortunes) +
                   " packs=" + Invariant(enabledSources) +
                   " off=" + Invariant(disabledSources) +
                   " level=" + (string.IsNullOrEmpty(contentLevel) ? "(unset)" : contentLevel) +
                   " smart=" + (smart ? "on model=" + (modelPresent ? "present" : "ABSENT") : "off");
        }

        /// <summary>How many distinct packs survived the filters, counted over the pool that was just built
        /// rather than by re-scanning the folder.</summary>
        private static int CountPoolSources(List<FortuneEntry> pool)
        {
            if (pool == null) return 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FortuneEntry e in pool)
                if (!string.IsNullOrEmpty(e.Source)) seen.Add(e.Source);
            return seen.Count;
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
            IModuleSettings ms = null;
            try { ms = host.GetSettings("fortunes"); } catch { }
            return SettingsFromStore(ms);
        }

        /// <summary>
        /// The engine's settings from a store, or from NO store. A host that hands the module no settings
        /// store cannot persist the user turning the expensive default off, so it does not get the expensive
        /// default: smart picks are OFF when there is nowhere to read the choice from. The real host always
        /// returns a store. The one that returns null is the convention self-test's, whose Init used to start
        /// an unobserved embed of the whole corpus into the TEMP fallback root on every
        /// <c>--module-selftest=fortunes</c> (F121, N-gates-02). Pure, so it is asserted; the decision is in
        /// docs/DESIGN-REGISTER.md.
        /// </summary>
        internal static FortuneSettings SettingsFromStore(IModuleSettings ms)
        {
            var s = new FortuneSettings();
            if (ms == null)
            {
                s.SmartFortunes = false;
                return s;
            }
            try
            {
                s.ContentLevel = ReadContentLevel(ms);
                s.NoProfanity = ms.GetBool("noProfanity", s.NoProfanity);
                s.SmartFortunes = ms.GetBool("smartFortunes", s.SmartFortunes);
                s.DisabledSources = SplitList(ms.Get("disabledSources", ""));
                s.DisabledGenres = SplitList(ms.Get("disabledGenres", ""));
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
                            new PaneAction { Label = "Select all", InvokeAsync = SelectAllSourcesAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Select none", InvokeAsync = SelectNoSourcesAsync, ReloadPaneAfter = true },
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
                        Actions = new[]
                        {
                            new PaneAction { Label = "Select all", InvokeAsync = SelectAllGenresAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Select none", InvokeAsync = SelectNoGenresAsync, ReloadPaneAfter = true },
                        },
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
                        Checked = StagedChecked("disabledSources", st.Id, !disabled.Contains(st.Id)),
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
                    items.Add(new ListItem { Id = g.Id, Label = g.Id, Detail = g.Count + (g.Count == 1 ? " line" : " lines"), Checked = StagedChecked("disabledGenres", g.Id, !disabled.Contains(g.Id)) });
            }
            catch { }
            return items;
        }

        private void SetSourceActive(string id, bool active) { StageDisabled("disabledSources", id, !active); }
        private void SetGenreActive(string id, bool active) { StageDisabled("disabledGenres", id, !active); }

        // ---- tick everything / untick everything ----------------------------------------------------
        // The pre-1.0.0 Options tab had Select all/none on both of these lists and the rewrite into
        // ListCards dropped them, keeping them only on "Available online". With 158 catalog packs the
        // absence is worst exactly when it matters most: turning the library off to hear one pack meant
        // 158 clicks.
        //
        // These stage like an individual tick rather than writing settings directly, so a bulk change
        // costs the same single write and single engine rebuild at Apply that StageDisabled exists to
        // give, and so "Select none" then Cancel leaves the saved state alone like any other tick.
        private Task<string> SelectAllSourcesAsync()  { return Task.FromResult(SetAllSources(true)); }
        private Task<string> SelectNoSourcesAsync()   { return Task.FromResult(SetAllSources(false)); }
        private Task<string> SelectAllGenresAsync()   { return Task.FromResult(SetAllGenres(true)); }
        private Task<string> SelectNoGenresAsync()    { return Task.FromResult(SetAllGenres(false)); }

        private string SetAllSources(bool active)
        {
            int n = 0;
            foreach (SourceStat st in FortuneProvider.Sources())
            {
                if (string.IsNullOrEmpty(st.Id)) continue;
                SetSourceActive(st.Id, active);
                n++;
            }
            if (n == 0) return "No fortune packs to change yet.";
            // Says "Apply" out loud because these cards are DeferChanges: the boxes move immediately but
            // the engine does not, and a bulk action is the one most likely to be trusted as already done.
            return (active ? "Ticked all " : "Unticked all ") + n + (n == 1 ? " pack" : " packs") +
                ". Apply to use it.";
        }

        private string SetAllGenres(bool active)
        {
            int n = 0;
            foreach (GenreStat g in FortuneProvider.Genres())
            {
                if (string.IsNullOrEmpty(g.Id)) continue;
                SetGenreActive(g.Id, active);
                n++;
            }
            if (n == 0) return "No genres to change yet.";
            return (active ? "Ticked all " : "Unticked all ") + n + (n == 1 ? " genre" : " genres") +
                ". Apply to use it.";
        }

        /// <summary>
        /// A pending tick, or the saved state when nothing is pending for this id. Every one of these
        /// cards is DeferChanges, so the staged batch -- not the settings file -- is what the user
        /// currently sees ticked. A ReloadPaneAfter action that read the file directly would redraw all
        /// the boxes from disk and silently throw away whatever was staged, which is precisely what a
        /// "Select none" immediately followed by a repaint would look like: a button that does nothing.
        /// </summary>
        private bool StagedChecked(string key, string id, bool savedChecked)
        {
            Dictionary<string, bool> staged;
            bool disabled;
            if (!string.IsNullOrEmpty(id) &&
                _stagedDisabled.TryGetValue(key, out staged) &&
                staged.TryGetValue(id, out disabled))
                return !disabled;
            return savedChecked;
        }

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

        // Fold the staged ids into the stored "disabled" lists. The caller owns the Save, the rebuild,
        // AND the clear -- because this map is the only record the batch ever had. CompanionHost.GetSettings
        // hands out a FRESH ModuleSettings per call and ModuleSettings.Save swallows its exception and
        // returns false, so clearing here threw away every untick the moment a write failed, and the next
        // Apply then wrote nothing, returned true, and told the user it had saved.
        // MergeDisabled is idempotent against an already-merged stored list, so a retained batch re-applies
        // cleanly on the retry.
        private void CommitStagedDisabled(IModuleSettings ms)
        {
            foreach (KeyValuePair<string, Dictionary<string, bool>> kv in _stagedDisabled)
            {
                if (kv.Value.Count == 0) continue;
                ms.Set(kv.Key, MergeDisabled(ms.Get(kv.Key, ""), kv.Value));
            }
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

        private async Task<string> RescanAsync()
        {
            try
            {
                await RebuildEngineAsync(false);
                int sources = 0;
                try { sources = FortuneProvider.Sources().Count; } catch { }
                return sources == 0
                    ? "No packs found yet."
                    : ("Rescanned — " + sources + (sources == 1 ? " pack" : " packs") + " loaded.");
            }
            catch (Exception ex) { return "Rescan failed: " + ex.Message; }
        }

        /// <summary>
        /// Import the user's own .txt packs through <see cref="FortuneFileImporter"/> — the strict, bounded,
        /// per-file atomic path (size/entry caps, staged writes) rather than a raw copy, so a malformed or
        /// oversized file is rejected instead of poisoning the pool. The host owns the file dialog (modules
        /// carry no UI framework). Existing files are never silently overwritten: nothing is approved for
        /// overwrite here, so a same-named pack is reported as skipped and the user renames or removes it.
        /// </summary>
        private async Task<string> ImportPacksAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            try
            {
                IReadOnlyList<string> chosen = host.PickFilesToOpen(
                    "Import fortune packs", "Fortune packs", new[] { "txt" });
                if (chosen == null || chosen.Count == 0) return "";   // cancelled

                // The picker above and the folder path are the UI thread's; the import is not. Its own
                // class comment has said "intended to run on a worker" since it was written, and it ran
                // inline in the click handler: every existing pack re-read for the admission count, every
                // source copied twice and flushed, the folder re-parsed afterwards (F122). Bare await, so
                // the rebuild and the status resume on the UI thread. The shutdown token stops an import
                // that outlives the module; the importer rolls back what it had committed.
                string directory = FortunePaths.FortunesDir;
                System.Threading.CancellationToken token = _shutdown.Token;
                FortuneImportBatchResult result = await Task.Run(delegate
                {
                    return FortuneFileImporter.Import(chosen, directory, null, token);   // no overwrite approved (see summary)
                });

                if (result.ImportedCount > 0) await RebuildEngineAsync(false);   // new lines join the pool at once

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
                return status;
            }
            catch (OperationCanceledException) { return "Import cancelled."; }
            catch (Exception ex) { return "✗ Import failed: " + Short(ex.Message); }
        }

        // ---- catalog packs (browse + download through the host) -------------------------------------

        // Last browse result (catalog packs not on disk yet) and the subset the user ticked for download.
        // Both are in-memory only: browsing writes nothing, and ticking writes nothing — the download
        // button is the only thing that touches the network or the disk.
        //
        // UI THREAD ONLY. Neither is synchronized, and both are read and written by the pane callbacks
        // (LoadAvailablePackItems, SetPackSelected, SelectAllPacksAsync, SelectNoPacksAsync) which the host
        // always calls on the UI thread. Anything that awaits before touching them must resume there too --
        // which is why the two awaits in this section have no ConfigureAwait(false).
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
                // NO ConfigureAwait(false) here, deliberately, and the same at the download below. The host
                // invokes a PaneAction from a button's Click handler on the UI thread (OptionsWindow
                // .BuildActionRow), so the bare await resumes there -- which is where CacheMissingPacks has to
                // run, because it clears and refills _availablePacks and RemoveWhere's _selectedPacks, and
                // those are the same collections LoadAvailablePackItems / SetPackSelected touch from checkbox
                // clicks. The packs card sets DeferChanges false on purpose, so a tick fires SetPackSelected
                // immediately and can land mid-fetch. The network call itself still runs off the UI thread:
                // that is FetchCatalogItemsAsync's business, and it is the only blocking part.
                IReadOnlyList<CatalogItem> items = await host.FetchCatalogItemsAsync(CatalogKinds.Pack);
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
                // The four failure CAUSES, counted apart. Three of them throw nothing, so a refused id, an
                // empty payload and a pack the loader would refuse all land in the user's "N packs failed"
                // with nothing after it (only the exception branch fills lastError); DescribeDownload's log
                // line is where they are told apart.
                int rejectedId = 0, emptyPayload = 0, malformed = 0, threw = 0;
                string lastError = "";
                string lastCategory = "";
                foreach (CatalogItem item in pending)
                {
                    try
                    {
                        if (!IsPlainPackId(item.Id)) { failed++; rejectedId++; continue; }
                        // Bare awaits (see CheckPacksOnlineAsync): everything after each is UI-thread work.
                        // _selectedPacks.Remove below, and the rebuild + CacheMissingPacks after the loop,
                        // all touch state the checkbox handlers own -- and the rebuild reaches
                        // IHost.GetSettings, which PluginApi's IHost contract requires on the UI thread.
                        byte[] bytes = await host.DownloadCatalogItemAsync(CatalogKinds.Pack, item.Id);
                        if (bytes == null || bytes.Length == 0) { failed++; emptyPayload++; continue; }
                        // The host verified the URL, the hash and the byte cap; nothing had checked the CONTENT.
                        // A pack the folder loader would refuse was written, counted installed and then skipped
                        // at load with only the skip line to say so, so "Downloaded 1 pack" and "no new source"
                        // were both true (F131). Same validator the importer uses: tagged OR plain, the loader's
                        // own limits, and a refusal is its own cause in the log line.
                        if (!FortuneProvider.TryValidateCustomPackBytes(bytes, item.Id,
                                FortunePackLoadPolicy.MaximumEntries, out _, out _))
                        { failed++; malformed++; continue; }
                        // Asynchronous, like the download before it: a synchronous write sat on the UI thread
                        // once per ticked pack (F146; median 23 KB, so the small half of that handler's
                        // stall -- the large half was the re-parse, now off the thread in RebuildEngineAsync).
                        await File.WriteAllBytesAsync(Path.Combine(directory, item.Id + ".txt"), bytes, _shutdown.Token);
                        _selectedPacks.Remove(item.Id);
                        installed++;
                    }
                    catch (Exception ex)
                    {
                        failed++; threw++;
                        lastError = Short(ex.Message);       // the user's own screen, which is not the log
                        lastCategory = Categorize(ex);       // the log gets the bucket, never the message
                    }
                }

                // Logged BEFORE the rebuild, so this line and the engine line that follows it read in the
                // order the work happened.
                Log(DescribeDownload(pending.Count, installed, rejectedId, emptyPayload, malformed, threw, lastCategory));

                await RebuildEngineAsync(false);   // the new packs join the pool (and the smart index) right away
                // Drop the installed ones from the available list so the card shows what's still missing.
                CacheMissingPacks(_availablePacks);
                string status = "Downloaded " + installed + (installed == 1 ? " pack." : " packs.");
                if (failed > 0)
                    status += " " + failed + (failed == 1 ? " pack" : " packs") + " failed" +
                        (lastError.Length > 0 ? " (" + lastError + ")" : "") + ".";
                return status;
            }
            catch (Exception ex)
            {
                // The whole batch fell over (the folder could not be created, the host refused the catalog
                // kind), which the loop's per-item counters never see.
                Log("pack download failed: " + Categorize(ex));
                return "✗ Download failed: " + Short(ex.Message);
            }
        }

        /// <summary>
        /// What a download batch actually did, per CAUSE rather than as one total. Pure, so the wording is
        /// asserted rather than eyeballed.
        ///
        /// <para>The reason this is worth a line: the user is told "3 packs failed" and, for three of the
        /// four ways that happens, nothing else at all. Only the exception branch fills the reason shown
        /// beside that count, so a catalog id this module refuses to write, a payload the host handed
        /// back empty (a hash mismatch, a truncated response) and a pack the loader would refuse (F131)
        /// are indistinguishable from each other and from a disk error — and the status text is gone the
        /// moment the pane closes, while the log is what goes on the issue.</para>
        ///
        /// <para>ONE line per batch, not one per pack: selecting all 158 catalog packs is a normal thing to
        /// do, and a per-pack line would make a single click the largest thing in the file. Pack ids are
        /// deliberately absent too — they are the user's content choices, and the counts are what identify
        /// the fault.</para>
        /// </summary>
        internal static string DescribeDownload(int requested, int installed, int rejectedId,
            int emptyPayload, int malformed, int threw, string lastCategory)
        {
            string line = "pack download: requested=" + Invariant(requested) +
                          " installed=" + Invariant(installed);
            int failed = rejectedId + emptyPayload + malformed + threw;
            if (failed == 0) return line + " failed=0";
            line += " failed=" + Invariant(failed) +
                    " (rejected-id=" + Invariant(rejectedId) +
                    " empty-payload=" + Invariant(emptyPayload) +
                    " malformed=" + Invariant(malformed) +
                    " error=" + Invariant(threw) + ")";
            return string.IsNullOrEmpty(lastCategory) ? line : line + " last=" + lastCategory;
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

        // ---- diagnostics (IHost.Log) -----------------------------------------------------------------

        /// <summary>
        /// One line into the app's diagnostic log. Never throws: the module must not be punished for the
        /// log being unavailable, which is <c>IHost.Log</c>'s own contract.
        /// </summary>
        private void Log(string message)
        {
            IHost host = _host;
            if (host == null || string.IsNullOrEmpty(message)) return;
            try { host.Log(Info.Id, message); } catch { }
        }

        /// <summary>
        /// A swallowed exception as a short, non-identifying category, following
        /// <c>AiBrain.DescribeError</c>. The message is deliberately dropped: this module's IO exceptions
        /// name the pack file they failed on, i.e. a full path inside the user's profile.
        /// </summary>
        private static string Categorize(Exception ex)
        {
            if (ex == null) return "none";
            if (ex is UnauthorizedAccessException) return "access-denied";
            if (ex is System.IO.DirectoryNotFoundException) return "directory-missing";
            if (ex is System.IO.IOException) return "io";
            if (ex is System.Text.Json.JsonException) return "bad-json";
            if (ex is InvalidOperationException) return "invalid-state";
            if (ex is OperationCanceledException) return "cancelled";
            return ex.GetType().Name;
        }

        private static string Invariant(int value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

            return PoolStatusFor(lines, packs, total) + SkippedPacksNote(FortuneProvider.SkippedCustomPacks);
        }

        /// <summary>
        /// The pane's word about pack files the loader refused. "" when none: a refusal is the exception,
        /// and a note that is always there is wallpaper. The reason it exists: a damaged pack used to be
        /// either recited as prose (F130) or dropped with nothing said anywhere, and the pool count beside
        /// this note looked normal both times. Categories and counts live in the diagnostic log; the pane
        /// says how many and where to look.
        /// </summary>
        internal static string SkippedPacksNote(int skipped)
        {
            if (skipped <= 0) return "";
            return " ⚠ " + Invariant(skipped) + " pack file" + (skipped == 1 ? "" : "s") +
                   " in the fortunes folder " + (skipped == 1 ? "was" : "were") +
                   " skipped (malformed rows, or a file the loader could not read) — see the diagnostic log.";
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
                // A `ms.Set("spicyTier", "")` sat here, justified as stopping "a stale value being
                // re-migrated". Nothing ever wrote or read that key: MigrateContentLevel reads only
                // spicyFortunes and spicyOnly, and spicyTier appeared exactly once in the whole repo --
                // at this write. Clearing a key that never existed migrates nothing.
                ms.Set("spicyOnly", "");
            }
            if (values.TryGetValue("noProfanity", out v) && bool.TryParse(v, out b)) ms.Set("noProfanity", b ? "true" : "false");
            // The host has just replayed the pack/genre ticks into the staging map (DeferChanges), so they
            // join this same write rather than each paying for their own.
            CommitStagedDisabled(ms);
            if (!ms.Save())
            {
                // Keep the staged batch for a retry, and rebuild NOTHING: the persisted settings did not
                // change, so a rebuild would recreate the engine from what it already runs (F147).
                return false;
            }
            _stagedDisabled.Clear();
            RebuildEngine();   // re-read + rebuild so the running pet uses the new settings at once
            return true;
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
        /// semantic index, then report status. Also picks up a pack dropped straight into the folder; the
        /// packs card's "Rescan folder" does the same without re-warming the index.</summary>
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
                    // Compared against the pool the CURRENT settings and folder would produce, not the
                    // list already indexed. `provider.PoolEntries()` is the very list _indexedSignature was
                    // computed from -- both are written together in RebuildEngine and the list is never
                    // mutated afterwards -- so that equality could never be false, and once a warm had
                    // completed this button was a no-op that answered "already built for these N fortunes"
                    // after a pack had been dropped into the folder (F149). A fresh provider re-reads the
                    // folder through the fingerprint-cached CustomCorpus: an unchanged folder costs one
                    // Select() pass, a changed one costs what RebuildEngine would have spent anyway.
                    FortuneProvider fresh = new FortuneProvider(LoadFortuneSettings(_host));
                    if (complete && _indexedSignature == PoolSignature(fresh.PoolEntries()))
                        return Task.FromResult("Smart index is already built for these " + Count(indexed) +
                            " fortunes — nothing to rebuild.");
                }
                return RebuildSmartIndexCoreAsync();
            }
            catch (Exception ex) { return Task.FromResult("Rebuild failed: " + ex.Message); }
        }

        // Split from the method above so that one keeps its synchronous signature, which the source
        // invariant for its currency guard slices on; the guard's fresh provider is built on the UI thread
        // (a cache hit unless the folder changed, and then only the changed files are parsed).
        private async Task<string> RebuildSmartIndexCoreAsync()
        {
            try
            {
                await RebuildEngineAsync(true);   // force: this button means "rebuild it", whatever state it is in
                return SmartStatusText();
            }
            catch (Exception ex) { return "Rebuild failed: " + ex.Message; }
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
                    // TOPIC as well as text: Pick's route bonus reads the entry's topic, and Select's dedupe
                    // keeps the first eligible entry per text, so disabling a source can swap which same-text
                    // entry (with a different topic) survives while the text set is unchanged (F147).
                    string topic = e.Topic ?? "";
                    for (int i = 0; i < topic.Length; i++) { hash ^= topic[i]; hash *= 1099511628211UL; }
                    hash ^= '\t'; hash *= 1099511628211UL;
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
            if (provider == null) return "✗ The fortune engine isn't loaded — see the diagnostic log.";
            bool ready = false, complete = false; int indexed = 0, total = 0;
            SmartStandDownReason reason = SmartStandDownReason.None;
            string detail = null;
            if (sm != null)
            {
                sm.WarmProgress(out ready, out complete, out indexed, out total);
                reason = sm.StandDownReason;
                detail = sm.StandDownDetail;
            }
            else if (_smartBuildFailed)
            {
                reason = SmartStandDownReason.ConstructionFailed;
            }
            // FROM THE SETTING, not from `sm != null`: the field is null for the whole time a build is in
            // flight, which is exactly when this runs, right after the button's RebuildEngine (F148).
            return SmartStatusFor(_smartWanted, provider.Count, AnyPacksInstalled(), reason, detail,
                ready, complete, indexed, total);
        }

        /// <summary>Diagnostics: the status line the "Rebuild smart index" button shows, read without
        /// pressing it, so the wiring from the module's state to the wording can be asserted.</summary>
        internal string SmartStatusTextForDiagnostics() { return SmartStatusText(); }

        internal int SmartGenerationForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _smartGeneration); }
        }

        internal int EngineRebuildsForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _engineRebuilds); }
        }

        /// <summary>Diagnostics: the live picker, null while a build is in flight or smart is off.</summary>
        internal SmartFortunes SmartPickerForDiagnostics
        {
            get { lock (_smartLock) return _smart; }
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
            SmartStandDownReason standDown, string standDownDetail,
            bool ready, bool complete, int indexed, int total)
        {
            if (!smartEnabled) return "Smart picks are off (random selection).";
            if (poolCount == 0) return EmptyPoolReason(anyPacksInstalled);
            // BEFORE the progress lines, because a stand-down is terminal and they are not. `ready` and
            // `complete` are both false in this state, so without this branch the last line below was
            // returned -- "Indexing N fortunes in the background" -- for ever, on a machine where nothing
            // was being indexed and nothing ever would be. A status that cannot fail to look busy is
            // worse than no status. One sentence PER REASON, because they want different actions from the
            // user (F137): the same "text engine could not start" for an oversized pool would have traded
            // one wrong message for another.
            switch (standDown)
            {
                case SmartStandDownReason.PoolTooLarge:
                    return "Smart picks are unavailable for this selection — " + Count(poolCount) +
                           " fortunes is more than the smart index can hold (" + Count(VectorCache.MaximumEntries) +
                           "). Disable some packs or narrow the content level to use them; fortunes are chosen " +
                           "at random until then.";
                case SmartStandDownReason.ModelAbsent:
                    return "Smart picks are unavailable — the text engine's model is missing from the module " +
                           "folder, so fortunes are chosen at random. Reinstall the Fortunes module to restore it.";
                case SmartStandDownReason.EmbedderNotReady:
                case SmartStandDownReason.ConstructionFailed:
                    return "Smart picks are unavailable on this machine — the text engine could not start" +
                           (string.IsNullOrEmpty(standDownDetail) ? "" : " (" + standDownDetail + ")") +
                           ", so fortunes are chosen at random. Everything else works normally.";
            }
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
                ? "No fortunes match these filters — the companion will stay silent. " +
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


        /// <summary>
        /// The module's own self-test, for <c>--module-selftest=fortunes</c>.
        ///
        /// A DELEGATION, not a second set of assertions: FortuneEngineProbe.Run already holds them, and the host's
        /// dedicated flag has run it for a long time. What was missing was the entry point ON THE MODULE
        /// TYPE, which is what the convention looks for -- so this module reported "the module exposes
        /// static bool SelfTest(out string detail)" and stopped after four generic checks, while its real
        /// assertions ran only under the other flag.
        ///
        /// Safe to name this SelfTest now. It used not to be: the harness once took the FIRST
        /// <c>bool SelfTest(out string)</c> anywhere in the assembly, so a helper could beat the module's
        /// own entry point (Reminder had six). That was fixed -- ModuleConventionSelfTest resolves it
        /// against the module TYPE -- and the comments elsewhere warning about it are stale.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            // UNDER A SCRATCH ROOT. The convention runner's host hands the module no storage, so the engine's
            // static root was the TEMP fallback, and every run left vectors\cache.bin and its lock there
            // (N-gates-02). The root in effect is restored afterwards, the scratch is removed, and the
            // fallback root is asserted untouched.
            string previousRoot = FortunePaths.RootForDiagnostics;
            string fallbackRoot = FortunePaths.FallbackRootForDiagnostics;
            string scratch = Path.Combine(Path.GetTempPath(), "dp-fortunes-selftest-" + Guid.NewGuid().ToString("N"));
            string[] fallbackBefore = SnapshotTree(fallbackRoot);
            try
            {
                Directory.CreateDirectory(scratch);
                FortunePaths.SetRoot(scratch);
                string probeDetail;
                ok &= FortuneEngineProbe.Run(out probeDetail);
                sb.Append(probeDetail);
                // The writable-folder cache check needs a throwaway CustomDir, which the scratch root now is;
                // until 1.0.12 only the host's --fortunes-selftest could run it, for that reason.
                string cacheDetail;
                ok &= FortuneEngineProbe.CustomCacheSelfTest(out cacheDetail);
                sb.Append(cacheDetail);
                ok &= SelfTestCheck(sb, "the engine's writes during the self-test landed under its scratch root (the redirect took)",
                    Directory.Exists(Path.Combine(scratch, "fortunes")));
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                FortunePaths.SetRoot(previousRoot);
                ok &= SelfTestCheck(sb, "the self-test's scratch root was removed", TryRemoveTree(scratch));
                ok &= SelfTestCheck(sb, "the TEMP fallback root gained nothing from the self-test",
                    SameTree(fallbackBefore, SnapshotTree(fallbackRoot)));
            }
            detail = sb.ToString();
            return ok;
        }

        private static bool SelfTestCheck(StringBuilder sb, string name, bool cond)
        {
            sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name);
            return cond;
        }

        private static string[] SnapshotTree(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new string[0];
                string[] entries = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
                Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
                return entries;
            }
            catch { return new[] { "<unreadable>" }; }
        }

        private static bool SameTree(string[] before, string[] after)
        {
            if (before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
                if (!string.Equals(before[i], after[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // A few retries: the last vector-cache lease or ONNX session may still be letting go of a handle.
        private static bool TryRemoveTree(string root)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    return !Directory.Exists(root);
                }
                catch { System.Threading.Thread.Sleep(100); }
            }
            return !Directory.Exists(root);
        }

        public void Shutdown()
        {
            try { _shutdown.Cancel(); } catch { }
            IHost host = _host;
            if (host != null)
            {
                host.CompanionSpawned -= OnPetSpawned;
                host.CompanionLanded -= OnPetLanded;
                host.CompanionPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_pokeResponder != null) { try { _pokeResponder.Dispose(); } catch { } _pokeResponder = null; }
            SmartFortunes doomed;
            lock (_smartLock)
            {
                // Bumped FIRST, under the lock the publish takes: a build still running must not publish
                // into a module that is shutting down (F144).
                System.Threading.Interlocked.Increment(ref _smartGeneration);
                doomed = _smart;
                _smart = null;
                _indexedSignature = null;
                _smartBuilding = false;
            }
            // Inline, here only: process exit is what Dispose's 3 s cap exists for.
            if (doomed != null) { try { doomed.Dispose(); } catch { } }
            // Static, so it outlives the instance unless dropped here. Same contract as
            // AiBrain.LogSink, which is nulled in its own Shutdown for the same reason.
            SmartFortunes.LogSink = null;
            FortuneProvider.LogSink = null;
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
