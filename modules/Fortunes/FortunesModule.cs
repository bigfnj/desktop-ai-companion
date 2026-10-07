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
        // Init, SavePaneValues, RescanAsync, ImportPacksAsync, DownloadPacksAsync, the
        // fortunes-folder watcher and the automatic retry, so two can overlap; without this an earlier, slower
        // build could land after a later one and quietly replace a current picker with a stale one.
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
        // whole time a build is in flight, so the status answered "Smart picks are off" with the box ticked
        // while every rebuild ran (F148; the button that showed it went in 1.1.0, the status line stayed).
        private volatile bool _smartWanted;
        private volatile bool _smartBuildFailed;   // the current generation's construction threw (F148)
        // Builds scheduled and not yet ended, whatever became of them: a superseded build is still constructing
        // (a cache.bin parse, a lock file) after the generation moved on, and the probe's storage must not be
        // removed under it (R-027). JoinSmartBuildsForDiagnostics waits on this; production never does.
        private int _smartBuildsInFlight;
        private int _smartConstructions;           // diagnostics: how many SmartFortunes this module constructed
        // The sinks THIS instance installed, so Shutdown can clear its own and leave another owner's alone
        // (RA-097): the probe Inits and Shuts down instances beside the live module, and an unconditional
        // null dropped the live module's sink under it.
        private Action<string> _smartSink;
        private Action<string> _providerSink;
        // Every rebuild, synchronous or not, takes a generation; an asynchronous parse publishes only if no
        // later rebuild has moved it on, so two overlapping pane actions cannot leave the older folder state
        // live (F127). The same shape as the smart build's generation.
        private int _engineGeneration;
        private int _lastParseThread;              // diagnostics: the thread the last asynchronous parse ran on
        // Cancels a pack import in flight when the module shuts down. The importer honours the token
        // throughout staging and once more immediately before its commit; once committing, the short atomic
        // loop runs to completion (it never observes the token again) and rolls back only a commit that
        // FAILED, so a Shutdown mid-commit finishes the batch rather than reverting it (RA-116).
        private readonly System.Threading.CancellationTokenSource _shutdown =
            new System.Threading.CancellationTokenSource();
        private ICompanion _lastPet;               // most-recently-seen pet, for screen-context capture on the drop path
        private IDisposable _dropResponder;
        private IDisposable _pokeResponder;

        // ---- the index maintains itself (1.1.0) ------------------------------------------------------------
        //
        // The owner, 2026-10-06: "'Rebuild smart index' doesn't let me know when it needs to be rebuilt, and if
        // we 'know' when, shouldn't it be automated, i.e. why have a button and not an information block?"
        // Every event that makes the index stale already reached a rebuild except two. A pack file added,
        // removed or edited in the fortunes folder while the app runs was seen only when somebody pressed
        // Rescan or the button, so the folder is WATCHED now. A build that failed stayed failed until a press,
        // so it is RETRIED once on its own. The button became the Selection card's "Smart index" line, which
        // says the index's state; docs/DESIGN-REGISTER.md (feature/fortunes-index) records why no manual control
        // stayed. The shape is the repo's background-work shape: an Interlocked single-flight gate, the work on
        // a pool thread, the engine and smart generations dropping a stale result, and the publish posted to
        // the UI thread Init ran on (Remembrance's PersistOnUi, AgentFlow's _ui).
        private System.Threading.SynchronizationContext _ui;   // Init's; null under a self-test, which then runs inline
        private readonly object _folderLock = new object();    // the watcher's and both timers' lifetimes, against Shutdown
        private bool _folderClosed;                            // Shutdown has taken them (under _folderLock)
        private FileSystemWatcher _folderWatcher;
        private System.Threading.Timer _folderQuiet;           // the quiet window a burst of folder events coalesces in
        private int _folderRebuildQueued;                      // Interlocked single-flight: a folder rebuild is posted or running
        private int _folderChangedAgain;                       // ...and a change arrived meanwhile, so one more pass follows it
        private int _folderWatchBroken;                        // the watcher reported an error other than an overflow
        private int _folderEventsSeen;                         // diagnostics: watcher notifications received
        private int _folderRebuildsRun;                        // diagnostics: folder rebuilds started (one per quiet window)
        private int _folderWatchStarts;                        // diagnostics: watchers started (Init, a restart, a Rescan)
        private bool _folderWatchRooted;                       // Init had a real storage root, so the folder is ours to watch
        private int _ownFolderWrites;                          // Import or Download is writing the folder and rebuilds after
        private volatile string _folderWatchProblem;           // null while a real folder is watched; the cause when it is not
        private long _folderQuietTicks = DefaultFolderQuietTicks;
        // Two seconds of no folder events: long enough for Explorer copying a handful of packs one after another
        // to read as one change, short enough that a dropped pack is live before the user has looked away. A
        // design parameter, not a measurement.
        private const long DefaultFolderQuietTicks = 2 * TimeSpan.TicksPerSecond;
        private System.Threading.Timer _retryTimer;            // the one automatic retry of a failed build (under _folderLock)
        private long _retryDelayTicks = DefaultRetryDelayTicks;
        // A minute: a warm fails today on an out-of-memory or an invariant breach inside the cache (R-031). The
        // first can pass once memory frees; the second fails again, which is why there is ONE retry, not a loop.
        private const long DefaultRetryDelayTicks = 60 * TimeSpan.TicksPerSecond;
        // Under _smartLock, beside the build they describe: what the pane's line reads.
        private IndexChange _indexReason;                      // why the current build was started
        private bool _indexReplaced;                           // ...and whether it replaced an index or an attempt at one
        private long _indexStartedUtcTicks;                    // ...and when
        private int _indexBuildsStarted;                       // diagnostics: builds started past the keep
        private bool _automaticRetryUsed;                      // this failure episode's one automatic retry has run
        private long _retryDueUtcTicks;                        // 0 when no automatic retry is pending
        private string _smartBuildFailure;                     // the category of the construction that threw, for the line
        private bool _shuttingDown;                            // Shutdown has begun: nothing schedules a build any more

        // Pack/genre boxes the user has moved but not yet applied, keyed by settings key ("disabledSources"
        // / "disabledGenres") then by id -> disabled?. Filled by the DeferChanges cards at Apply time and
        // drained by SavePaneValues, so the batch costs one settings write and one engine rebuild.
        private readonly Dictionary<string, Dictionary<string, bool>> _stagedDisabled =
            new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "fortunes",
            Name = "Fortunes",
            Version = "1.1.0",   // 1.1.0: the smart index maintains itself, and the pane says its state. The
                                 //        "Rebuild smart index" button is gone; in its place the Selection
                                 //        card's "Smart index" line says, whenever the pane is built, whether
                                 //        the index is up to date (its count and when it was built), being built
                                 //        or rebuilt and why, or off and why, and fortunes are chosen at random
                                 //        meanwhile exactly as before. A Status card pinned full-width above
                                 //        every other card says the same in one line, with the pool and the
                                 //        content level beside it ("4,457 fortunes from 7 sources | smart
                                 //        index: up to date (built 14:02) | content: Clean + edgy"; the owner's
                                 //        mockup F2). The fortunes folder is watched: a pack file added,
                                 //        removed or edited outside the app rebuilds the pool and the index
                                 //        once the folder has been quiet for two seconds, so a burst of
                                 //        changes is one rebuild, and an Import's or a Download's own writes
                                 //        wait for that action's own rebuild. A folder that cannot be watched
                                 //        says so on the line, and Rescan folder re-reads it and starts the
                                 //        watch again; a watcher that stops is replaced. A failed
                                 //        build is retried once, a minute later, and the line names the time. A
                                 //        Rescan, an Import, a Download or a folder notification under unchanged
                                 //        settings over an unchanged folder builds nothing at all, not even a
                                 //        provider. A rebuild that finishes after Shutdown publishes nothing and
                                 //        starts no smart build (the watcher made that path reachable on its
                                 //        own). MINOR by docs/VERSIONING.md: a behaviour the user can see. The
                                 //        index work alone needs no newer host: SettingKind.Info and every member
                                 //        it uses are 1.0.0.
                                 //        Same version, its own commit: "Check online for packs" words a failed
                                 //        check by its cause (N-catalog-insight-05). A catalog that was reached
                                 //        and refused no longer reads "Couldn't reach the catalog"; it says the
                                 //        published catalog is at fault, apart from no answer (check the
                                 //        connection) and anything else (the check failed).
                                 //        Same version, its own commit: the pane takes the owner's layout F2 on
                                 //        the host 1.4.0 list primitives. Fortune packs, Available online and
                                 //        Genres each open with an "All" row ("All packs", "All genres") that
                                 //        ticks or unticks every item and says how many of them are on, and the
                                 //        group headers count what is ticked ("12 of 18"), so a collapsed list
                                 //        still says what the companion draws from. The six Select all / Select
                                 //        none buttons are gone. A bulk choice on Fortune packs or Genres now
                                 //        waits for Apply like a single tick and is discarded by Cancel like one;
                                 //        the buttons saved at once because the host could not arm Apply for
                                 //        them, and the All row arms it. A failed "Check online for packs" shows
                                 //        inside Available online as a red block (what failed, whose fault it
                                 //        is, when it was checked, and that the button tries again) in place of
                                 //        the line squeezed beside the button, and the list it replaces is
                                 //        emptied. MinHostVersion 1.4.0, for ListCard.MasterToggle.
                                 // 1.0.12: the pane's smart-index status reads the SETTING and the stand-down
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
                                 //         Lane burn/fortunes, same version: "Select all/none" on the pack and
                                 //         genre cards saves at once and rebuilds, so a bulk choice no longer
                                 //         waits on an Apply the host could not arm and no longer outlives a
                                 //         Cancel (RA-121, RA-122). The pane's refused-pack note tells damaged
                                 //         files apart from valid ones the budget had no room for, counts the
                                 //         files past the cap, and rides the empty-pool status too (R-034,
                                 //         RA-125); a folder walk that faults is counted, not cached (RA-107);
                                 //         the source and genre lists are one memo over the folder parse
                                 //         (RA-108); the importer retries only a fault whose Win32 code says
                                 //         transient and puts a torn replace's backup back (RA-102, RA-104); a
                                 //         rejected import names its file (RA-101); a download's content check
                                 //         leaves the UI thread (RA-124); a tagged text column with whitespace
                                 //         around it is admitted trimmed, pinned (R-030). A smart build re-checks
                                 //         its generation before it constructs and before it warms, an empty
                                 //         pool builds no picker, and the publish line says when the warm stood
                                 //         down (RA-118, RA-119, RA-120); a warm that throws stands the index
                                 //         down with WarmFailed and the next Apply rebuilds it (R-031); a
                                 //         missing native onnxruntime is named as the runtime, not the model
                                 //         (R-025); Shutdown clears only the sinks it installed (RA-097); the
                                 //         Rebuild button's currency guard parses the folder only for a complete
                                 //         index (RA-126); the scoring loop no longer re-validates every vector
                                 //         (RA-114); the folder button disposes its Process (RA-123); the preview
                                 //         and the status clip at a code-point boundary, never through a
                                 //         surrogate pair (N-burn-aibrain-01); the probe's completion-line
                                 //         check waits for the warm task to end and judges the line's order
                                 //         in the sink, where the order can be seen (N-burn-fortunes-01).
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
            // 1.4.0 since the layout F2 change (lane feature/layout-fortunes): the three list cards set
            // ListCard.MasterToggle, which host 1.4.0 introduced. Contracts is the host's single shared copy, so
            // on an older host the first setter of that property is a MissingMethodException inside Init, and the
            // load gate is what turns it into a refusal with a reason. The red block a failed catalog check shows
            // is host 1.4.0's coloured EmptyHint and the counted group headers are its list rendering; neither
            // is an ABI member, and on an older host both would only look as they used to. Publish this only
            // once host 1.4.0 has shipped (docs/VERSIONING.md), or the catalog offers users a module their host
            // correctly refuses. Until this change the floor was 1.0.0: the pet-aware responders shipped in the
            // host before the public renumbering (pre-release 1.5.0), and a host below a declared floor refuses
            // the module with a legible reason instead of loading it and broadcasting every fortune.
            MinHostVersion = "1.4.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.ScreenContext | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            // Where a rebuild started by the folder watcher or the automatic retry is posted: the host Inits
            // modules on its UI thread (CompanionHost captures the same context, CompanionHost.cs:157), and
            // IHost's services belong there. Null under the self-tests, whose rebuilds then run inline.
            _ui = System.Threading.SynchronizationContext.Current;
            _welcome = LoadWelcomeCorpus();
            bool rooted = false;

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
                    rooted = true;
                }
            }
            catch { }
            // Wired BEFORE RebuildEngine, because RebuildEngine is what starts the warm task and
            // a sink attached afterwards would miss the first stand-down -- which is the one that
            // matters on a machine where the embedder never loads at all.
            _smartSink = delegate(string line) { Log(line); };
            SmartFortunes.LogSink = _smartSink;
            // The loader's sink too, for the same reason: the first folder parse is the one inside
            // RebuildEngine, and a pack it refuses is refused right there.
            _providerSink = delegate(string line) { Log(line); };
            FortuneProvider.LogSink = _providerSink;
            // WATCHED BEFORE THE FIRST PARSE, so a pack that lands between the two is a change the watcher
            // reports rather than one the parse missed (the fingerprint makes the extra pass free when it was
            // not). Only a real storage root is watched: a host that hands no storage leaves the engine on
            // the TEMP fallback, which nothing writes packs into, and its Init stays one log line.
            _folderWatchRooted = rooted;
            if (rooted) StartFolderWatch();
            RebuildEngine(IndexChange.Startup);

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
        /// <see cref="RebuildEngineAsync"/>, off the UI thread.
        /// This overload is the rebuild after the user's own SELECTION changed (Apply, the "All" rows'
        /// choices included), which is what the pane's line names when it starts an index build.</summary>
        private void RebuildEngine()
        {
            RebuildEngine(IndexChange.Selection);
        }

        /// <param name="why">What changed, for the pane's line. Recorded only when the rebuild starts an index
        /// build: an unchanged pool keeps the index (F147). This parameter was the button's `force` until 1.1.0;
        /// with the button gone nothing needs an unchanged, healthy index rebuilt, and the one state a rebuild
        /// of an unchanged pool must redo, a failed build, ScheduleSmartPicker already treats as not current.</param>
        private void RebuildEngine(IndexChange why)
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
            PublishEngine(settings, provider, why);
        }

        /// <summary>
        /// The rebuild for whatever exists to change the folder: Rescan, Import, Download and the folder
        /// watcher. The settings are read here, on the UI thread where IHost.GetSettings belongs;
        /// the parse runs on a pool thread; the publish runs on the continuation, which is the UI thread in
        /// the app because the host awaits a PaneAction from the button's click handler and nothing here
        /// uses ConfigureAwait(false). The corpus parse used to run inline in that click handler: 1.2-1.4 s
        /// with the catalog installed, measured by the audit (F127). A rebuild that a later one overtook
        /// publishes nothing, since the later one's provider describes the newer folder. Same shape as
        /// AiBrainModule.BeginVramProbe and the smart build above: a generation, Task.Run, a continuation
        /// that drops a stale result.
        ///
        /// <para>UNCHANGED INPUTS BUILD NOTHING (1.1.0). A provider is a pure function of the settings it filters
        /// with and the writable folder it reads (the embedded and bundled tiers never change at runtime), and it
        /// records both. So when the settings read here equal the live provider's and the folder's fingerprint
        /// (file metadata only) equals the one it was parsed under, nothing is built: no provider, no Select pass,
        /// no pool signature, no log line, and the index stays exactly as it is. That is what makes a no-op
        /// Rescan cost nothing, and what lets the watcher be told about every notification without each one
        /// costing a rebuild; the probe holds the witness. Rejected: comparing the new POOL with the old (what
        /// the button's guard did), which needs the very parse this skips.</para>
        /// </summary>
        private async Task RebuildEngineAsync(IndexChange why)
        {
            int generation = System.Threading.Interlocked.Increment(ref _engineGeneration);
            // No try here: LoadFortuneSettings never throws (it swallows GetSettings' fault and SettingsFromStore
            // swallows the store's), so the catch that sat around it was unreachable and read as a report of a
            // failed store that never came (RA-117). A store that throws is defaults, said nowhere.
            FortuneSettings settings = LoadFortuneSettings(_host);
            FortuneProvider current = _provider;
            bool unchangedSelection = current != null && SameSelection(settings, current.BuiltWith);
            FortuneProvider provider;
            try
            {
                provider = await Task.Run(delegate
                {
                    if (unchangedSelection && FolderUnchangedSince(current)) return null;
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
            if (provider == null)
            {
                // Nothing the pool is made of moved. The one thing still worth doing is for an index that FAILED
                // to build: a Rescan, an Import or a Download is the user asking for the state to be made current,
                // and a failed build is the one state a retry can change (R-031), so it is retried the way Apply
                // retries it. A folder notification is not a request, so it retries nothing; the automatic retry
                // has its own timer.
                if (why != IndexChange.Folder && IndexFailed()) ScheduleSmartPicker(_smartWanted, current.PoolEntries(), why);
                return;
            }
            PublishEngine(settings, provider, why);
        }

        /// <summary>The writable folder fingerprints exactly as it did when <paramref name="provider"/> read it.
        /// Metadata only; runs on the rebuild's pool thread.</summary>
        private static bool FolderUnchangedSince(FortuneProvider provider)
        {
            return provider != null &&
                   string.Equals(FortuneProvider.CustomFolderSignatureNow(), provider.CustomSignature, StringComparison.Ordinal);
        }

        /// <summary>Two settings select the same pool: every field the provider reads, lists in order. Pure, so
        /// the comparison behind the no-op skip is asserted. A list in a different order compares unequal,
        /// which costs one rebuild that F147's keep then makes free, never a skipped change.</summary>
        internal static bool SameSelection(FortuneSettings a, FortuneSettings b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.ContentLevel, b.ContentLevel, StringComparison.Ordinal) &&
                   a.NoProfanity == b.NoProfanity &&
                   a.SmartFortunes == b.SmartFortunes &&
                   SameList(a.DisabledSources, b.DisabledSources) &&
                   SameList(a.DisabledGenres, b.DisabledGenres);
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            int na = a == null ? 0 : a.Count, nb = b == null ? 0 : b.Count;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>Smart picks are wanted and the current build failed (its construction threw, or its warm
        /// did): the one unusable state a retry can change.</summary>
        private bool IndexFailed()
        {
            lock (_smartLock)
                return _smartWanted &&
                       (_smartBuildFailed || (_smart != null && _smart.StandDownReason == SmartStandDownReason.WarmFailed));
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
        private void PublishEngine(FortuneSettings settings, FortuneProvider provider, IndexChange why)
        {
            // NOTHING PUBLISHES INTO A MODULE THAT IS SHUTTING DOWN (1.1.0). A Rescan still parsing at Shutdown,
            // or a folder rebuild posted just before it, used to land here afterwards: it put a provider back into
            // a module that had dropped its own, and its ScheduleSmartPicker started a build (a cache.bin parse
            // and an ONNX session) that nothing would ever dispose. The watcher made that path reachable without
            // a click. Shutdown cancels this token first thing.
            if (_shutdown.IsCancellationRequested) return;
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
                ScheduleSmartPicker(settings.SmartFortunes, pool, why);
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
        /// <param name="why">Recorded with the build it starts, for the pane's line ("Rebuilding after a pack
        /// change"). A build started by anything but the automatic retry opens a new failure episode, so it
        /// gets its own one retry if it fails.</param>
        private void ScheduleSmartPicker(bool wanted, List<FortuneEntry> pool, IndexChange why)
        {
            // Recorded before the warm starts: it names the pool being indexed, which is what a later
            // "is this still current?" question compares against.
            string signature = wanted ? PoolSignature(pool) : null;
            // NOTHING TO INDEX BUILDS NOTHING (RA-120). An empty pool used to be scheduled like any other:
            // the build constructed a picker, whose VectorCache.Load parsed and RETAINED the whole cache.bin
            // for the session, called a Warm that started nothing, and logged "warming 0 lines". The pane
            // answers an empty pool before it asks about the index, and _smartWanted keeps the setting.
            bool buildable = wanted && pool != null && pool.Count > 0;
            SmartFortunes old;
            int generation;
            lock (_smartLock)
            {
                bool current = buildable && _smartBuilding && !_smartBuildFailed &&
                               !(_smart != null && _smart.StandDownReason == SmartStandDownReason.WarmFailed) &&
                               string.Equals(signature, _indexedSignature, StringComparison.Ordinal);
                // The WarmFailed clause (R-031): a picker whose warm threw is the one stand-down a retry can
                // change, so the next Apply rebuilds it rather than keeping it the way F147 keeps a healthy
                // or a deterministically stood-down one.
                if (current) return;
                // A rebuild that outlived Shutdown starts nothing (1.1.0): Shutdown sets this under the same lock,
                // after which no build would ever be disposed.
                if (_shuttingDown) return;
                generation = System.Threading.Interlocked.Increment(ref _smartGeneration);
                bool replaced = _smart != null || _smartBuilding || _smartBuildFailed;
                old = _smart;
                _smart = null;
                _indexedSignature = signature;
                _smartBuildFailed = false;
                _smartBuilding = buildable;
                // Whatever retry was pending belonged to the build this one replaces; its timer, if it still
                // fires, finds nothing due. A build for any reason but the retry itself opens a new episode.
                _retryDueUtcTicks = 0;
                _smartBuildFailure = null;
                if (why != IndexChange.Retry) _automaticRetryUsed = false;
                if (buildable)
                {
                    System.Threading.Interlocked.Increment(ref _smartBuildsInFlight);
                    System.Threading.Interlocked.Increment(ref _indexBuildsStarted);
                    _indexReason = why;
                    _indexReplaced = replaced;
                    _indexStartedUtcTicks = DateTime.UtcNow.Ticks;
                }
            }
            if (!buildable)
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
            System.Threading.Tasks.Task.Run(delegate { BuildSmartPickerCounted(generation, old, pool); });
        }

        /// <summary>Pool thread: the build, bracketed by the in-flight count the probe joins on (R-027). The
        /// count is taken in ScheduleSmartPicker under the lock, before the Task.Run, so a join that starts
        /// right after a schedule sees the build it has not yet started.</summary>
        private void BuildSmartPickerCounted(int generation, SmartFortunes old, List<FortuneEntry> pool)
        {
            try { BuildSmartPicker(generation, old, pool); }
            finally { System.Threading.Interlocked.Decrement(ref _smartBuildsInFlight); }
        }

        /// <summary>Whether a later rebuild has moved the generation past <paramref name="generation"/>, read
        /// under the lock the bump takes (F144). Asked twice on the way to a warm (RA-118, RA-119).</summary>
        private bool SmartBuildSuperseded(int generation)
        {
            lock (_smartLock) return System.Threading.Volatile.Read(ref _smartGeneration) != generation;
        }

        /// <summary>Pool thread: dispose the picker being replaced, construct and warm the next one, and
        /// publish it unless the module moved on meanwhile.</summary>
        private void BuildSmartPicker(int generation, SmartFortunes old, List<FortuneEntry> pool)
        {
            // Disposed BEFORE the replacement is constructed, so the peak is one ONNX session.
            if (old != null) { try { old.Dispose(); } catch { } }
            // RE-CHECKED HERE, because that dispose can wait up to 3 s and a rebuild scheduled meanwhile has
            // already moved the generation on: constructing would parse cache.bin (~94 MB at the full catalog)
            // for a picker that can only be dropped (RA-118).
            if (SmartBuildSuperseded(generation)) return;
            SmartFortunes built = null;
            try
            {
                System.Threading.Interlocked.Increment(ref _smartConstructions);
                // The probe's factory builds pickers that stand down or fail on cue, so every trigger can be
                // counted without an ONNX session; production never sets it.
                Func<SmartFortunes> diagnosticFactory = PickerFactoryForDiagnostics;
                if (diagnosticFactory != null) built = diagnosticFactory();
                else built = new SmartFortunes();
                // ...AND BEFORE THE WARM. A build superseded while it was constructing must not start a warm (a
                // session load and an embed) that its successor's dispose can only cancel three seconds later;
                // F143's "the peak stays at one session" held for a published picker only until this check
                // (RA-119). The window left is the cache.bin parse above.
                if (SmartBuildSuperseded(generation))
                {
                    try { built.Dispose(); } catch { }
                    return;
                }
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
                // never became usable. This says the picker object exists and its warm is queued, or that
                // the warm stood down before it started (an oversized pool, the asset absent), which Warm
                // decides synchronously; the warm itself reports completion, cancellation and every later
                // stand-down through the sink. Until 1.0.12 this line read "smart picker ready (N lines
                // indexed)" at this very moment, when nothing had been embedded yet (F145), and until RA-120
                // it said "warming" after a Warm that had started nothing.
                Log(DescribeSmartBuild(pool.Count, built.StandDownReason));
                // The warm's END is the one moment a warm that threw can be seen, and nothing polls for it.
                WatchWarm(built);
            }
            catch (Exception ex)
            {
                if (built != null) { try { built.Dispose(); } catch { } }
                bool retry = false;
                lock (_smartLock)
                {
                    if (System.Threading.Volatile.Read(ref _smartGeneration) == generation)
                    {
                        // Recorded in STATE, so the pane can say "unavailable" instead of "indexing"
                        // for ever (F148); the automatic retry, or the next rebuild, clears it and tries again.
                        _smartBuildFailed = true;
                        _smartBuilding = false;
                        _smartBuildFailure = Categorize(ex);
                        retry = NoteFailureNoLock();
                    }
                }
                Log("smart picker unavailable: " + Categorize(ex) + " (fortunes stay random)");
                if (retry) ArmRetryTimer();
            }
        }

        /// <summary>
        /// Continue on a published picker's warm. A warm that THREW stands its picker down with WarmFailed
        /// (R-031), the one stand-down a retry can change, and that is retried once on its own. Every other
        /// ending needs nothing from here: a completed warm is read by the pane through WarmProgress and
        /// WarmCompletedUtc, a cancelled one was superseded, and the deterministic stand-downs are kept (F147).
        /// </summary>
        private void WatchWarm(SmartFortunes built)
        {
            System.Threading.Tasks.Task warm;
            try { warm = built.WarmTask; }
            catch { return; }
            warm.ContinueWith(delegate { WarmEnded(built); },
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        private void WarmEnded(SmartFortunes built)
        {
            if (built.StandDownReason != SmartStandDownReason.WarmFailed) return;
            bool retry = false;
            lock (_smartLock)
            {
                // Only the LIVE picker's failure counts: one superseded or shut down meanwhile is no longer
                // _smart (both clear or replace the field under this lock), and its successor needs no retry.
                if (ReferenceEquals(_smart, built)) retry = NoteFailureNoLock();
            }
            if (retry) ArmRetryTimer();
        }

        /// <summary>
        /// Under _smartLock, with the failure already recorded: decide whether this episode's one automatic retry
        /// is still to come. True means "arm the timer" (done outside the lock); the due time is set here, in the
        /// same hold as the failure, so the pane never reads a failed build without knowing whether a retry is
        /// pending.
        /// </summary>
        private bool NoteFailureNoLock()
        {
            // (No Shutdown check: both callers ask first whether the failure is the live build's, and Shutdown
            // moves the generation and clears the picker under this lock before anything else.)
            if (_automaticRetryUsed)
            {
                _retryDueUtcTicks = 0;
                return false;
            }
            _retryDueUtcTicks = DateTime.UtcNow.Ticks + System.Threading.Interlocked.Read(ref _retryDelayTicks);
            return true;
        }

        private void ArmRetryTimer()
        {
            long delay = System.Threading.Interlocked.Read(ref _retryDelayTicks);
            lock (_folderLock)
            {
                if (_folderClosed) return;
                if (_retryTimer == null)
                    _retryTimer = new System.Threading.Timer(delegate { PostToUi(RetryIndexNow); }, null,
                        System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _retryTimer.Change(TimeSpan.FromTicks(delay), System.Threading.Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// The automatic retry, on the UI thread (or inline under a self-test). It redoes the failed build over
        /// the pool as it is now, and only while a retry is DUE: every build started for any other reason clears
        /// the due time under _smartLock (ScheduleSmartPicker), so a timer that fires after a newer build, or
        /// twice, finds nothing to do. After Shutdown the provider is gone, and ScheduleSmartPicker refuses
        /// anything that gets further.
        /// </summary>
        private void RetryIndexNow()
        {
            FortuneProvider provider = _provider;
            lock (_smartLock)
            {
                if (_retryDueUtcTicks == 0) return;
                _retryDueUtcTicks = 0;
                _automaticRetryUsed = true;
            }
            if (provider == null) return;
            ScheduleSmartPicker(_smartWanted, provider.PoolEntries(), IndexChange.Retry);
        }

        /// <summary>Run <paramref name="work"/> where IHost's services belong: posted to the UI context Init ran
        /// on, or inline when there is none (the self-tests). Never throws.</summary>
        private void PostToUi(Action work)
        {
            System.Threading.SynchronizationContext ui = _ui;
            try
            {
                if (ui != null) ui.Post(delegate { try { work(); } catch { } }, null);
                else work();
            }
            catch { }
        }

        // ---- the fortunes folder, watched (1.1.0) -----------------------------------------------------------
        //
        // A pack file added, removed or edited outside the app (Explorer, a sync tool, an editor) used to reach
        // the pool only through Rescan or the Rebuild button. Every notification now re-arms one quiet window;
        // when it elapses ONE rebuild runs, and that rebuild builds nothing at all when the folder fingerprints
        // as the live provider read it (RebuildEngineAsync), so a spurious or late notification costs a
        // directory listing. Rejected: polling the fingerprint on a timer, which lists the folder for ever to
        // learn what the watcher is told; and rebuilding per notification, which is nine rebuilds for nine
        // copied packs (more, since one copy raises several notifications).

        /// <summary>Watch the fortunes folder's top-level <c>*.txt</c>, the exact set the loader reads. A
        /// folder that cannot be watched is recorded and logged, and the pane's line says so: a control that can
        /// run degraded must say so, and Rescan folder still re-reads it.</summary>
        private void StartFolderWatch()
        {
            System.Threading.Interlocked.Increment(ref _folderWatchStarts);
            FileSystemWatcher watcher = null;
            string problem = null;
            try
            {
                watcher = new FileSystemWatcher(FortunePaths.FortunesDirPath, "*.txt")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += OnFolderEvent;
                watcher.Changed += OnFolderEvent;
                watcher.Deleted += OnFolderEvent;
                watcher.Renamed += OnFolderRenamed;
                watcher.Error += OnFolderError;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                problem = Categorize(ex);   // the type, never the message: it quotes the path
                if (watcher != null) { try { watcher.Dispose(); } catch { } watcher = null; }
            }
            FileSystemWatcher previous = null;
            bool closed;
            lock (_folderLock)
            {
                closed = _folderClosed;
                if (!closed)
                {
                    previous = _folderWatcher;
                    _folderWatcher = watcher;
                    _folderWatchProblem = problem;
                }
            }
            if (closed) DisposeWatcher(watcher);
            DisposeWatcher(previous);
            if (!closed && problem != null)
                Log("fortunes folder not watched: " + problem +
                    " (a pack file added or removed outside the app is picked up by Rescan folder)");
        }

        /// <summary>
        /// Rescan folder's half of the watch: one that is DOWN (it could not start, or it reported an error that
        /// stopped it) is started again before the folder is re-read. Without this a folder deleted while the app
        /// ran and then made again stayed unwatched until the next start, the line said so, and the action it
        /// named could not fix it. A watch that is up is left alone. Only a folder Init had a storage root for.
        /// </summary>
        private void RestartFolderWatchIfDown()
        {
            bool down;
            lock (_folderLock) down = !_folderClosed && _folderWatchRooted && (_folderWatcher == null || _folderWatchProblem != null);
            if (down || System.Threading.Interlocked.Exchange(ref _folderWatchBroken, 0) == 1) StartFolderWatch();
        }

        private static void DisposeWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
        }

        private void OnFolderEvent(object sender, FileSystemEventArgs e) { NoteFolderChanged(); }
        private void OnFolderRenamed(object sender, RenamedEventArgs e) { NoteFolderChanged(); }

        /// <summary>An overflowed buffer means "something changed, details lost", which the fingerprint settles.
        /// Any other error means the watcher has stopped (the folder deleted, its volume gone), so the next quiet
        /// window starts a new one before it rebuilds.</summary>
        private void OnFolderError(object sender, ErrorEventArgs e)
        {
            Exception ex = null;
            try { ex = e.GetException(); } catch { }
            if (!(ex is InternalBufferOverflowException))
                System.Threading.Interlocked.Exchange(ref _folderWatchBroken, 1);
            NoteFolderChanged();
        }

        private void NoteFolderChanged()
        {
            System.Threading.Interlocked.Increment(ref _folderEventsSeen);
            ArmFolderQuiet();
        }

        /// <summary>(Re)start the quiet window: a trailing debounce, so the rebuild waits for the LAST
        /// notification of a burst.</summary>
        private void ArmFolderQuiet()
        {
            TimeSpan window = TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref _folderQuietTicks));
            lock (_folderLock)
            {
                if (_folderClosed) return;
                if (_folderQuiet == null)
                    _folderQuiet = new System.Threading.Timer(delegate { FolderQuietElapsed(); }, null,
                        System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _folderQuiet.Change(window, System.Threading.Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>The quiet window elapsed (a pool thread): start the one folder rebuild, unless one is already
        /// queued or running, in which case it is marked to run once more after it.</summary>
        private void FolderQuietElapsed()
        {
            if (_shutdown.IsCancellationRequested) return;
            // THE MODULE'S OWN WRITES REBUILD AFTER THEMSELVES. A Download writes one pack per await and then
            // rebuilds once; without this a window elapsing between two packs rebuilt on a half-written batch and
            // the action's own rebuild did it again. Re-armed, not dropped: a change made by someone else
            // meanwhile is still picked up once the action is done (its rebuild usually already has it, and then
            // the pass is free).
            if (System.Threading.Volatile.Read(ref _ownFolderWrites) > 0)
            {
                ArmFolderQuiet();
                return;
            }
            if (System.Threading.Interlocked.Exchange(ref _folderWatchBroken, 0) == 1) StartFolderWatch();
            // SINGLE-FLIGHT: one folder rebuild queued or running at a time. A change that arrives meanwhile may
            // land after that rebuild's parse listed the folder, so it is remembered and one more pass follows.
            if (System.Threading.Interlocked.CompareExchange(ref _folderRebuildQueued, 1, 0) != 0)
            {
                System.Threading.Interlocked.Exchange(ref _folderChangedAgain, 1);
                return;
            }
            PostToUi(RunFolderRebuild);
        }

        /// <summary>On the UI thread (or inline under a self-test): the folder rebuild, and its release of the
        /// single-flight gate when it ends, however it ends.</summary>
        private void RunFolderRebuild()
        {
            System.Threading.Interlocked.Increment(ref _folderRebuildsRun);
            System.Threading.Tasks.Task rebuild;
            try { rebuild = RebuildEngineAsync(IndexChange.Folder); }
            catch { rebuild = System.Threading.Tasks.Task.CompletedTask; }
            rebuild.ContinueWith(delegate
            {
                System.Threading.Interlocked.Exchange(ref _folderRebuildQueued, 0);
                if (System.Threading.Interlocked.Exchange(ref _folderChangedAgain, 0) == 1) ArmFolderQuiet();
            }, System.Threading.CancellationToken.None,
               System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
               System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>Import and Download hold this across their writes AND their own rebuild, so the watcher's
        /// window cannot elapse into a half-written batch (see FolderQuietElapsed). Disposing twice is safe.</summary>
        private IDisposable BeginOwnFolderWrites()
        {
            System.Threading.Interlocked.Increment(ref _ownFolderWrites);
            return new OwnFolderWrites(this);
        }

        private sealed class OwnFolderWrites : IDisposable
        {
            private FortunesModule _owner;
            public OwnFolderWrites(FortunesModule owner) { _owner = owner; }
            public void Dispose()
            {
                FortunesModule owner = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (owner != null) System.Threading.Interlocked.Decrement(ref owner._ownFolderWrites);
            }
        }

        /// <summary>Shutdown's half: the watcher and both timers go first, so nothing new is queued while the rest
        /// comes down. Idempotent.</summary>
        private void CloseFolderWatch()
        {
            FileSystemWatcher watcher;
            System.Threading.Timer quiet, retry;
            lock (_folderLock)
            {
                _folderClosed = true;
                watcher = _folderWatcher; _folderWatcher = null;
                quiet = _folderQuiet; _folderQuiet = null;
                retry = _retryTimer; _retryTimer = null;
            }
            DisposeWatcher(watcher);
            if (quiet != null) { try { quiet.Dispose(); } catch { } }
            if (retry != null) { try { retry.Dispose(); } catch { } }
        }

        /// <summary>The line logged when a picker is published: what is TRUE at that moment. Pure, so the
        /// wording is asserted -- it must not claim readiness the warm has not reached, nor a warm that never
        /// started (RA-120): <paramref name="warmStandDown"/> is what Warm decided synchronously.</summary>
        internal static string DescribeSmartBuild(int lines, SmartStandDownReason warmStandDown)
        {
            if (warmStandDown == SmartStandDownReason.None)
                return "smart picker constructed, warming " + Invariant(lines) + " lines in the background";
            // The stand-down's own line came through the sink a moment earlier; this says the picker exists
            // and that no warm is running, instead of claiming one is.
            return "smart picker constructed, warm stood down: " + warmStandDown + " (fortunes stay random)";
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
                    // THE STATUS CARD, pinned full-width above every other card (owner, 2026-10-06, mockup F2):
                    // one line that says what the companion is drawing from, what the smart index is doing and
                    // what content is admitted, computed at every pane build. PinTop and FullWidth are read from
                    // a group's FIRST field (host 1.1.6, PluginApi.cs:332-339), and this is that field. The
                    // Selection card keeps the full sentence about the index; this is the glance.
                    new SettingField { Id = "status", Label = "Right now", Kind = SettingKind.Info, Group = "Status", FullWidth = true, PinTop = true },
                    new SettingField { Id = "smartFortunes", Label = "Smart, context-aware picks", Kind = SettingKind.Bool, Group = "Selection" },
                    // Display-only, and the replacement for the "Rebuild smart index" button (1.1.0): the index
                    // rebuilds itself on every change that makes it stale, so what the user needs is its STATE,
                    // computed at every pane build the way "Right now" is. The host re-runs Load on open, after
                    // Apply (a pane with an Info field refreshes, OptionsWindow's RefreshAfterApply) and after
                    // every ReloadPaneAfter action; it has no way to repaint an open pane on its own (the known
                    // ABI gap in docs/DESIGN-REGISTER.md), which is why a line about work in progress says when
                    // that work started.
                    new SettingField { Id = "smartIndex", Label = "Smart index", Kind = SettingKind.Info, Group = "Selection" },
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
                    new PaneAction { Label = "Show me 5 examples", InvokeAsync = PreviewFortunesAsync, Group = "Content level" },
                },
                // (pack browse/download buttons live on the Available online card below; the folder ones on
                // Fortune packs, where layout F2 keeps them)
                //
                // LAYOUT F2 (owner, 2026-10-06; host 1.4.0): each list opens with an "All" row
                // (ListCard.MasterToggle) where a Select all / Select none pair used to sit, six buttons in all,
                // and the host counts what is ticked on it and on every group header. The row moves each item's
                // own box, so what reaches the module is exactly what single ticks deliver: on the two
                // DeferChanges cards, SetChecked per changed item at Apply, folded into Apply's one write and one
                // rebuild; on Available online, SetPackSelected per item at once. Why the bulk choice now waits
                // for Apply, and what was rejected, is above SetSourceActive.
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
                        MasterToggle = "All packs",   // the installed packs; replaces Select all / Select none
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
                    // download button owns the actual fetching and reports progress. Kept in a field because a
                    // failed check rewrites its EmptyHint (ShowCatalogFailure), which the host re-reads at
                    // every build.
                    (_availableCard = new ListCard
                    {
                        Title = "Available online",
                        LoadItems = LoadAvailablePackItems,
                        SetChecked = SetPackSelected,
                        Filterable = true,
                        CollapseGroups = true,
                        MasterToggle = "All packs",   // the download basket: every pack the catalog lists that is not installed
                        EmptyHint = AvailableEmptyHint,
                        Actions = new[]
                        {
                            new PaneAction { Label = "Check online for packs", InvokeAsync = CheckPacksOnlineAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Download selected", InvokeAsync = DownloadPacksAsync, ReloadPaneAfter = true },
                        },
                    }),
                    new ListCard
                    {
                        Title = "Genres",
                        LoadItems = LoadGenreItems,
                        SetChecked = SetGenreActive,
                        DeferChanges = true,
                        MasterToggle = "All genres",
                        EmptyHint = "Genres appear here once you add a pack.",
                    },
                },
            };
        }

        // What Available online says before anything has been checked, and again after a check that worked.
        internal const string AvailableEmptyHint = "Click “Check online for packs” to see what the catalog offers.";

        // The Available online card, for the hint a failed check writes into it. UI thread only, like the two
        // collections it lists (_availablePacks, _selectedPacks).
        private ListCard _availableCard;

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

        // ---- tick everything / untick everything: the "All" rows ------------------------------------
        // The pre-1.0.0 Options tab had Select all/none on both of these lists and the rewrite into
        // ListCards dropped them, keeping them only on "Available online". With 158 catalog packs the
        // absence is worst exactly when it matters most: turning the library off to hear one pack meant
        // 158 clicks. Since layout F2 (host 1.4.0) each card's "All" row is that control, and the host
        // drives it through each item's own box: the two methods above run once per CHANGED item at Apply,
        // and SavePaneValues folds the lot into one write and one rebuild, as it does for single ticks.
        //
        // So a bulk choice WAITS FOR APPLY again, which reverses 1.0.12, deliberately. Select all/none
        // saved at once because a bulk choice staged in THIS module had no discard signal and no way to arm
        // Apply: "Select none" followed by Cancel came back unticked in the reopened pane and rode the next
        // Apply for any field (RA-121), and the status asked for an Apply the host had greyed out (RA-122).
        // An All-row click is the HOST's own pending edit: Apply lights up, Cancel and a ReloadPaneAfter
        // rebuild throw it away like any unapplied tick, and the module never holds an unapplied bulk
        // choice at all. Rejected: committing the row's ticks at once (the host calls SetChecked per item,
        // so "at once" would be one write and one rebuild PER PACK, the cost DeferChanges exists to avoid);
        // and keeping the buttons beside the row, two controls for one thing and the six the owner asked to
        // lose.

        /// <summary>
        /// A pending tick, or the saved state when nothing is pending for this id. The host flushes a
        /// DeferChanges card's ticks into this map immediately before Save, so between calls the map holds
        /// something only after a FAILED Apply (retained for the retry, see CommitStagedDisabled), and the
        /// pane the host rebuilds then shows that batch rather than the file: what a retry will save. An "All"
        /// row's ticks arrive the same way, at Apply; until 1.1.0 the Select all/none buttons committed at
        /// once instead (RA-121).
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
                // Disposed, like every other Start site in the tree: with UseShellExecute the returned Process
                // (null when the shell handed back no handle, which a using accepts) otherwise waited for the
                // finalizer, one handle per click (RA-123).
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true })) { }
                return Task.FromResult("Opened the fortunes folder — drop .txt packs there, then Rescan.");
            }
            catch (Exception ex) { return Task.FromResult("Couldn't open the folder: " + ex.Message); }
        }

        /// <summary>"Rescan folder": re-read the folder now. The watcher makes it unnecessary for a change it was
        /// told about; it stays for a folder that cannot be watched (the Smart index line says when that is) and
        /// as the request to retry a failed index, and over an unchanged folder it builds nothing.</summary>
        private async Task<string> RescanAsync()
        {
            try
            {
                RestartFolderWatchIfDown();
                await RebuildEngineAsync(IndexChange.Packs);
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
            IDisposable ownWrites = null;
            try
            {
                IReadOnlyList<string> chosen = host.PickFilesToOpen(
                    "Import fortune packs", "Fortune packs", new[] { "txt" });
                if (chosen == null || chosen.Count == 0) return "";   // cancelled
                // From here to this action's own rebuild, the watcher waits (BeginOwnFolderWrites): the importer
                // stages and renames into the folder, and its rebuild reads the result once.
                ownWrites = BeginOwnFolderWrites();

                // The picker above and the folder path are the UI thread's; the import is not. Its own
                // class comment has said "intended to run on a worker" since it was written, and it ran
                // inline in the click handler: every existing pack re-read for the admission count, every
                // source copied twice and flushed, the folder re-parsed afterwards (F122). Bare await, so
                // the rebuild and the status resume on the UI thread. The shutdown token stops an import
                // that outlives the module before its commit; a commit already running finishes, and the
                // importer rolls back only a commit that failed (RA-116).
                string directory = FortunePaths.FortunesDir;
                System.Threading.CancellationToken token = _shutdown.Token;
                FortuneImportBatchResult result = await Task.Run(delegate
                {
                    return FortuneFileImporter.Import(chosen, directory, null, token);   // no overwrite approved (see summary)
                });

                if (result.ImportedCount > 0) await RebuildEngineAsync(IndexChange.Packs);   // new lines join the pool at once

                string status = "Imported " + result.ImportedCount +
                    (result.ImportedCount == 1 ? " pack." : " packs.");
                if (result.RejectedCount > 0)
                {
                    string firstError = "";
                    foreach (FortuneImportItemResult item in result.Items)
                        if (!item.Imported && !string.IsNullOrWhiteSpace(item.Error))
                        {
                            // The file NAME, never its path: this status is the user's own screen, and the
                            // name is what tells them which of the files they picked to fix (RA-101). The
                            // diagnostic log gets none of it.
                            string name = FileNameOnly(item.SourcePath);
                            firstError = (name.Length > 0 ? name + ": " : "") + Short(item.Error);
                            break;
                        }
                    status += " " + result.RejectedCount + (result.RejectedCount == 1 ? " file" : " files") +
                        " rejected" + (firstError.Length > 0 ? " (" + firstError + ")" : "") + ".";
                }
                return status;
            }
            catch (OperationCanceledException) { return "Import cancelled."; }
            catch (Exception ex) { return "✗ Import failed: " + Short(ex.Message); }
            finally { if (ownWrites != null) ownWrites.Dispose(); }
        }

        // ---- catalog packs (browse + download through the host) -------------------------------------

        // Last browse result (catalog packs not on disk yet) and the subset the user ticked for download.
        // Both are in-memory only: browsing writes nothing, and ticking writes nothing — the download
        // button is the only thing that touches the network or the disk.
        //
        // UI THREAD ONLY. Neither is synchronized, and both are read and written by the pane callbacks
        // (LoadAvailablePackItems, SetPackSelected, which the "All packs" row drives once per pack, and the
        // check's ShowCatalogFailure) which the host always calls on the UI thread. Anything that awaits
        // before touching them must resume there too -- which is why the two awaits in this section have no
        // ConfigureAwait(false).
        private readonly List<CatalogItem> _availablePacks = new List<CatalogItem>();
        private readonly HashSet<string> _selectedPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What the last check threw and when, for Available online's red block; null once a check works (and
        // before any has run). UI thread only, like the two above.
        private Exception _catalogFailure;
        private DateTime _catalogFailedAt;

        private IReadOnlyList<ListItem> LoadAvailablePackItems()
        {
            // The card's hint is worded against the moment the pane is DRAWN, not the moment the check failed:
            // a block left from yesterday must not say "Checked today". The host reads EmptyHint right after
            // this call on every build (OptionsWindow.BuildListCard), which is the only per-build hook a list
            // card has; ShowCatalogFailure words it once as well, so a host that read the hint first would be
            // one build behind on the date and never on the failure.
            RefreshAvailableHint(DateTime.Now);
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

        /// <summary>Fetch the catalog and list the packs that aren't installed yet. Read-only: nothing is
        /// downloaded, written, or selected — the user picks from the list, then hits Download selected.
        /// A fetch that throws empties the list and shows the failure inside the card instead
        /// (<see cref="ShowCatalogFailure"/>), so this answers nothing beside the button then.</summary>
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
                _catalogFailure = null;   // this check worked: the card's hint goes back to the plain one
                RefreshAvailableHint(DateTime.Now);
                int available = CacheMissingPacks(items);
                if (items.Count == 0) return "The catalog lists no fortune packs.";
                return available == 0
                    ? ("You already have every catalog pack (" + items.Count + ").")
                    : (available + (available == 1 ? " pack" : " packs") +
                       " available — tick the ones you want, then “Download selected”.");
            }
            catch (Exception ex)
            {
                // The red block inside the card says it all, directly above this button (layout F2); a second,
                // shorter copy beside it would only repeat it, and the host would carry it across rebuilds.
                ShowCatalogFailure(ex, DateTime.Now);
                return "";
            }
        }

        /// <summary>
        /// A failed check, shown where layout F2 puts it: inside Available online, as the card's own red block.
        /// The list is EMPTIED, because the host draws a card's EmptyHint only over an empty list (ListCard.
        /// EmptyHint in PluginApi.cs) and because what an earlier check listed is no longer known to be on offer:
        /// Download selected reads the same shared catalog fetch (CompanionHost.DownloadCatalogItemAsync), so a
        /// stale list invites a download that fails the same way. The ticks go with it. Rejected: keeping the
        /// list and the ticks beside the failure in the button's line, which is the cramped sentence F2 replaced
        /// (the mockup's F0 draws it wrapping in the narrow column beside the button).
        /// </summary>
        private void ShowCatalogFailure(Exception ex, DateTime checkedAt)
        {
            _catalogFailure = ex;
            _catalogFailedAt = checkedAt;
            _availablePacks.Clear();
            _selectedPacks.Clear();
            RefreshAvailableHint(checkedAt);
        }

        /// <summary>Available online's hint: the red block while the last check failed, the plain line
        /// otherwise. The host re-reads the property at every build.</summary>
        private void RefreshAvailableHint(DateTime now)
        {
            ListCard card = _availableCard;
            if (card == null) return;
            card.EmptyHint = _catalogFailure == null
                ? AvailableEmptyHint
                : CatalogFailureBlock(_catalogFailure, _catalogFailedAt, now);
        }

        /// <summary>Diagnostics: when the failure the red block reports happened, settable so the probe can
        /// age it past midnight without waiting for one.</summary>
        internal DateTime CatalogFailedAtForDiagnostics
        {
            get { return _catalogFailedAt; }
            set { _catalogFailedAt = value; }
        }

        /// <summary>
        /// Available online's red block for a failed "Check online for packs", by CAUSE (N-catalog-insight-05), on
        /// up to four lines the host draws as one tinted box (P7: a hint that starts with ✗ is coloured and
        /// boxed): what failed, the host's own reason, whose fault it is, and when it was checked with the retry. Every
        /// failure used to read "✗ Couldn't reach the catalog: ..." -- including a catalog that WAS reached and
        /// refused, so the owner was told to suspect their connection over "Catalog contains an invalid module
        /// entry." Three cases, in the words the Modules pane's problem panel uses (CatalogText.ProblemForFailure)
        /// and layout F2 drew:
        ///
        /// <para>REFUSED: the catalog was fetched and failed the host's own checks. Host 1.4.0 throws its
        /// CatalogRejectedException; host 1.3.0 threw an InvalidDataException, which can no longer reach this
        /// code (MinHostVersion is 1.4.0) and stays recognised as the data refusal it always meant; a
        /// JsonException is a catalog that came back but does not parse. The fault is the published catalog's,
        /// not the user's.</para>
        ///
        /// <para>UNREACHABLE: no answer (HttpRequestException), or none in time (TimeoutException, and the
        /// TaskCanceledException / OperationCanceledException an HttpClient timeout surfaces as).</para>
        ///
        /// <para>ANYTHING ELSE says the check failed and offers the retry, without guessing at a cause it cannot
        /// name.</para>
        ///
        /// Told apart by the exception's TYPE NAME, never its type: CatalogRejectedException is internal to the
        /// host, so no module can name it in code. The reason is the host's own message, bounded the way every
        /// status here is (Short), with its own closing stop dropped so the line does not end in two; a host with
        /// nothing to say gets no empty reason line. The time is the user's own format, "today" while it is
        /// (<paramref name="now"/> is the moment the pane is drawn). Pure, so each case is asserted.
        /// </summary>
        internal static string CatalogFailureBlock(Exception ex, DateTime checkedAt, DateTime now)
        {
            string reason = Short(ex == null ? "" : ex.Message).TrimEnd('.', ' ');
            string title, reasonLabel, whose;
            switch (ex == null ? "" : ex.GetType().Name)
            {
                case "CatalogRejectedException":
                case "InvalidDataException":
                case "JsonException":
                    title = "✗ The catalog was reached but refused";
                    reasonLabel = "Rule: ";
                    whose = "Published wrong, not your install. Nothing on this PC needs fixing; the next catalog publish clears it.";
                    break;
                case "HttpRequestException":
                case "TimeoutException":
                case "TaskCanceledException":
                case "OperationCanceledException":
                    title = "✗ Couldn't reach the catalog";
                    reasonLabel = "What failed: ";
                    whose = "Your connection or GitHub, not the catalog and not your install. Nothing was changed.";
                    break;
                default:
                    title = "✗ The catalog check failed";
                    reasonLabel = "What failed: ";
                    whose = "";   // no cause it can name, so no fault it can assign
                    break;
            }
            var block = new StringBuilder(title);
            if (reason.Length > 0) block.Append('\n').Append(reasonLabel).Append(reason).Append('.');
            if (whose.Length > 0) block.Append('\n').Append(whose);
            block.Append('\n').Append(checkedAt.Date == now.Date ? "Checked today at " : "Checked at ")
                 .Append(Clock(checkedAt, now)).Append(". “Check online for packs” tries again.");
            return block.ToString();
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
                return "No packs ticked — choose some (or tick “All packs”), then Download selected.";
            // Across every pack's write and the one rebuild after them: N packs are one rebuild, however long
            // the downloads between the writes take (BeginOwnFolderWrites).
            IDisposable ownWrites = BeginOwnFolderWrites();
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
                        //
                        // OFF THE UI THREAD (RA-124). This ran inline between the awaits: a full parse of each
                        // pack, classifier included, on the WPF thread, once per ticked pack. The validator is
                        // static and pure over the bytes, so it runs where the rebuild's parse runs. The rebuild
                        // after the loop parses the new file once more, through the per-file cache's miss for a
                        // file it has not seen: one file per download, on a pool thread; recorded as the
                        // residue in BACKLOG.md rather than fixed by seeding the cache from here.
                        bool loadable = await Task.Run(delegate { return ValidateDownloadedPack(bytes, item.Id); });
                        if (!loadable) { failed++; malformed++; continue; }
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

                await RebuildEngineAsync(IndexChange.Packs);   // the new packs join the pool (and the smart index) right away
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
            finally { ownWrites.Dispose(); }
        }

        /// <summary>The loader's own admission check over a downloaded pack's bytes, on the pool thread the
        /// download awaits it on (RA-124); the thread is recorded so the probe can assert where it ran.</summary>
        private bool ValidateDownloadedPack(byte[] bytes, string id)
        {
            System.Threading.Volatile.Write(ref _lastDownloadValidationThread, Environment.CurrentManagedThreadId);
            int entries;
            string error;
            return FortuneProvider.TryValidateCustomPackBytes(bytes, id, FortunePackLoadPolicy.MaximumEntries,
                out entries, out error);
        }
        private int _lastDownloadValidationThread;   // diagnostics: the thread the last download's validation ran on

        /// <summary>Diagnostics: the thread the last downloaded pack was validated on, 0 before any.</summary>
        internal int LastDownloadValidationThreadForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _lastDownloadValidationThread); }
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
            // The same code-point-safe cut as Ellipsize (N-burn-aibrain-01): a status quoting a rejected
            // pack's reason carries that pack's own characters.
            return message.Length > 160 ? UnicodeTextProgress.TruncateAtCodePointBoundary(message, 160) + "…" : message;
        }

        /// <summary>The leaf of a path, or "" when it has none or cannot be read as a path.</summary>
        private static string FileNameOnly(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path) ?? ""; } catch { return ""; }
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
            d["smartIndex"] = SmartIndexText();
            d["status"] = StatusText();
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
            // The note rides BOTH paths. The empty-pool return used to drop it, on the one path where a
            // refused pack is the whole cause: a user who had unticked the built-in packs to hear their own,
            // and whose own pack the loader had just refused, was told to widen the filters (RA-125).
            FortuneProvider.CustomLoadSkips skips = FortuneProvider.SkippedCustomPackDetail;
            if (lines == 0) return "✗ " + EmptyPoolReason(AnyPacksInstalled()) + SkippedPacksNote(skips);

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

            return PoolStatusFor(lines, packs, total) + SkippedPacksNote(skips);
        }

        /// <summary>
        /// The pane's word about pack files the loader refused. "" when none: a refusal is the exception,
        /// and a note that is always there is wallpaper. The reason it exists: a damaged pack used to be
        /// either recited as prose (F130) or dropped with nothing said anywhere, and the pool count beside
        /// this note looked normal both times. Categories and counts live in the diagnostic log; the pane
        /// says how many and where to look.
        ///
        /// TWO SENTENCES, because the six categories are two kinds of news. A malformed, unreadable or
        /// badly named file is the user's to fix; a VALID file the loader had no room for (over the per-file
        /// or total byte budget, over the row budget, past the file cap) is a budget decision, and telling
        /// its owner it was "malformed or unreadable" sent them to inspect files that parse fine (R-034).
        /// Pure, so the wording is asserted.
        /// </summary>
        internal static string SkippedPacksNote(FortuneProvider.CustomLoadSkips skips)
        {
            int skipped = skips == null ? 0 : skips.Total;
            if (skipped <= 0) return "";
            return " ⚠ " + SkippedPacksSentences(skips) + " See the diagnostic log.";
        }

        private static string SkippedPacksSentences(FortuneProvider.CustomLoadSkips skips)
        {
            int damaged = skips.Damaged;
            int didNotFit = skips.DidNotFit;
            var sb = new StringBuilder();
            if (damaged > 0)
            {
                sb.Append(Invariant(damaged)).Append(" pack file").Append(damaged == 1 ? "" : "s")
                  .Append(" in the fortunes folder ").Append(damaged == 1 ? "was" : "were")
                  .Append(" skipped (malformed rows, an empty or unreadable file, or an unusable name).");
            }
            if (didNotFit > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Invariant(didNotFit)).Append(" valid pack file").Append(didNotFit == 1 ? "" : "s")
                  .Append(" did not fit the loader's budget (")
                  .Append(Invariant(FortunePackLoadPolicy.MaximumFileBytes / (1024 * 1024))).Append(" MB per file, ")
                  .Append(Invariant(FortunePackLoadPolicy.MaximumTotalBytes / (1024 * 1024))).Append(" MB and ")
                  .Append(FortunePackLoadPolicy.MaximumEntries.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" lines in total, ").Append(Invariant(FortunePackLoadPolicy.MaximumFiles))
                  .Append(" files) — disable or remove some packs.");
            }
            return sb.ToString();
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

        /// <summary>One line, clipped to <paramref name="maximum"/> UTF-16 code units at a code-point boundary,
        /// with an ellipsis when clipped. It cut at a bare index, so a preview whose 160th unit fell inside a
        /// surrogate pair (an emoji in a fortune) ended in half a character before the ellipsis, the twin of
        /// AiBrain's RA-057 (N-burn-aibrain-01); ModuleKit's helper backs off one unit when the cut would sever
        /// a pair. Internal so the probe can pin the boundary.</summary>
        internal static string Ellipsize(string value, int maximum)
        {
            string one = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length > maximum ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, maximum) + "…" : one;
        }

        // The "Rebuild smart index" button and its currency guard (F149, RA-126) were here until 1.1.0. What
        // the guard asked, "is the index built for the folder as it is NOW", is the question every rebuild now
        // answers before it builds anything (RebuildEngineAsync's unchanged-inputs skip, which reads the folder's
        // CURRENT fingerprint against the one the live provider parsed under, never the provider against
        // itself); what the button did, rebuilding on request, happens on its own after every change that
        // makes the index stale. docs/DESIGN-REGISTER.md, feature/fortunes-index.

        /// <summary>
        /// A content fingerprint of the indexed pool, so a rebuild can tell an unchanged selection from a
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

        /// <summary>The Selection card's "Smart index" line, from the module's state at THIS moment: read at every
        /// pane build, never cached between builds.</summary>
        private string SmartIndexText()
        {
            return SmartIndexLineFor(GatherSmartIndexFacts(_provider));
        }

        /// <summary>
        /// The Status card's one line (owner, 2026-10-06): what the pool draws from, what the smart index is doing,
        /// and what content is admitted, from the same facts as the Selection card's line and the live provider's
        /// own settings, so the three parts describe one moment.
        /// </summary>
        private string StatusText()
        {
            FortuneProvider provider = _provider;
            SmartIndexFacts facts = GatherSmartIndexFacts(provider);
            FortuneSettings built = provider != null ? provider.BuiltWith : null;
            int sources = provider != null && facts.PoolCount > 0 ? EnabledInstalledSources() : 0;
            return StatusLineFor(facts, sources,
                built != null ? LevelToDisplay(built.ContentLevel) : null,
                built != null && built.NoProfanity);
        }

        /// <summary>Installed packs the saved selection leaves on: the count the "Right now" line uses too.</summary>
        private int EnabledInstalledSources()
        {
            int packs = 0;
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledSources")), StringComparer.OrdinalIgnoreCase);
                foreach (SourceStat st in FortuneProvider.Sources())
                    if (!disabled.Contains(st.Id)) packs++;
            }
            catch { }
            return packs;
        }

        /// <summary>Everything both lines are made of, read under the module's locks at one moment.</summary>
        private SmartIndexFacts GatherSmartIndexFacts(FortuneProvider provider)
        {
            var facts = new SmartIndexFacts();
            SmartFortunes sm;
            long startedTicks, retryTicks;
            string buildFailure;
            lock (_smartLock)
            {
                sm = _smart;
                facts.BuildFailed = _smartBuildFailed;
                buildFailure = _smartBuildFailure;
                facts.Reason = _indexReason;
                facts.Replaced = _indexReplaced;
                facts.RetrySpent = _automaticRetryUsed;
                startedTicks = _indexStartedUtcTicks;
                retryTicks = _retryDueUtcTicks;
            }
            // FROM THE SETTING, not from `sm != null`: the field is null for the whole time a build is in
            // flight, which is exactly when the pane is rebuilt after an Apply that started one (F148).
            facts.SmartWanted = _smartWanted;
            facts.EngineLoaded = provider != null;
            facts.PoolCount = provider != null ? provider.Count : 0;
            facts.FolderWatchProblem = _folderWatchProblem;
            facts.NowLocal = DateTime.Now;
            if (startedTicks > 0) facts.StartedLocal = new DateTime(startedTicks, DateTimeKind.Utc).ToLocalTime();
            if (retryTicks > 0) facts.RetryDueLocal = new DateTime(retryTicks, DateTimeKind.Utc).ToLocalTime();
            if (provider != null && facts.PoolCount == 0) facts.AnyPacksInstalled = AnyPacksInstalled();
            if (sm != null)
            {
                sm.WarmProgress(out facts.Ready, out facts.Complete, out facts.Indexed, out facts.Total);
                facts.StandDown = sm.StandDownReason;
                facts.StandDownDetail = sm.StandDownDetail;
                DateTime built = sm.WarmCompletedUtc;
                if (built != DateTime.MinValue) facts.BuiltLocal = built.ToLocalTime();
            }
            else if (facts.BuildFailed)
            {
                facts.StandDown = SmartStandDownReason.ConstructionFailed;
                facts.StandDownDetail = buildFailure;   // a category, as the log line has it: never a message
            }
            return facts;
        }

        /// <summary>Diagnostics: the pane's "Smart index" line, read without building the pane. The NAME is the
        /// one three host self-tests resolve by reflection (ModuleHostSelfTest.SmartStatusOf), and what it
        /// returns is what the pane shows, so their "Smart picks are off" check reads the live line.</summary>
        internal string SmartStatusTextForDiagnostics() { return SmartIndexText(); }

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

        /// <summary>Diagnostics: a build for the current generation is in flight or published.</summary>
        internal bool SmartBuildingForDiagnostics
        {
            get { lock (_smartLock) return _smartBuilding; }
        }

        /// <summary>Diagnostics: how many pickers this module has constructed. An empty pool constructs none
        /// (RA-120).</summary>
        internal int SmartPickerConstructionsForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _smartConstructions); }
        }

        /// <summary>Diagnostics: builds scheduled and not yet ended, superseded ones included.</summary>
        internal int SmartBuildsInFlightForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _smartBuildsInFlight); }
        }

        /// <summary>
        /// Diagnostics: wait, up to <paramref name="wait"/>, until no smart build is in flight. A build superseded
        /// while constructing outlives Shutdown -- its VectorCache is created under whichever engine root is
        /// current at that instant -- so a probe that restores the root and removes its scratch storage has to
        /// wait for it first, or the storage leaks or the landing is deleted under a live cache (R-027, RA-100).
        /// Bounded, so a build that never ends cannot hang the gate; false says the wait ran out.
        /// </summary>
        internal bool JoinSmartBuildsForDiagnostics(TimeSpan wait)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Volatile.Read(ref _smartBuildsInFlight) > 0)
            {
                if (sw.Elapsed >= wait) return false;
                System.Threading.Thread.Sleep(10);
            }
            return true;
        }

        // ---- diagnostics for the self-maintaining index (1.1.0) ----

        /// <summary>Diagnostics: builds the picker through this instead of <c>new SmartFortunes()</c>, so the probe
        /// can count every trigger's build with pickers that stand down or fail on cue and no ONNX session.
        /// Production never sets it.</summary>
        internal Func<SmartFortunes> PickerFactoryForDiagnostics { get; set; }

        /// <summary>Diagnostics: index builds started past F147's keep, whatever became of them.</summary>
        internal int IndexBuildsStartedForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _indexBuildsStarted); }
        }

        /// <summary>Diagnostics: what the current build was started for.</summary>
        internal IndexChange IndexReasonForDiagnostics
        {
            get { lock (_smartLock) return _indexReason; }
        }

        /// <summary>Diagnostics: when the pending automatic retry is due, MinValue when none is.</summary>
        internal DateTime RetryDueUtcForDiagnostics
        {
            get
            {
                long ticks;
                lock (_smartLock) ticks = _retryDueUtcTicks;
                return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            }
        }

        /// <summary>Diagnostics: the retry delay, so the real timer can be driven in a test.</summary>
        internal TimeSpan RetryDelayForDiagnostics
        {
            set { System.Threading.Interlocked.Exchange(ref _retryDelayTicks, value.Ticks); }
        }

        /// <summary>Diagnostics: the pending automatic retry, run now rather than when its timer fires.</summary>
        internal void RetryIndexNowForDiagnostics() { RetryIndexNow(); }

        /// <summary>Diagnostics: the folder's quiet window, read at each notification.</summary>
        internal TimeSpan FolderQuietWindowForDiagnostics
        {
            set { System.Threading.Interlocked.Exchange(ref _folderQuietTicks, value.Ticks); }
        }

        /// <summary>Diagnostics: the quiet window elapsing now. Disarms the pending window first, as its own
        /// elapse would, then runs exactly what the timer runs.</summary>
        internal void FolderQuietElapsedForDiagnostics()
        {
            lock (_folderLock)
            {
                if (_folderQuiet != null)
                    _folderQuiet.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            FolderQuietElapsed();
        }

        /// <summary>Diagnostics: no folder rebuild is queued or running.</summary>
        internal bool FolderRebuildIdleForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _folderRebuildQueued) == 0; }
        }

        /// <summary>Diagnostics: watcher notifications received.</summary>
        internal int FolderEventsSeenForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _folderEventsSeen); }
        }

        /// <summary>Diagnostics: folder rebuilds started, one per elapsed quiet window that got through.</summary>
        internal int FolderRebuildsRunForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _folderRebuildsRun); }
        }

        /// <summary>Diagnostics: why a real fortunes folder is not watched, null while it is.</summary>
        internal string FolderWatchProblemForDiagnostics { get { return _folderWatchProblem; } }

        /// <summary>Diagnostics: a watcher is running. Not the same as no problem recorded: with no watcher started at
        /// all the problem is null too.</summary>
        internal bool FolderWatchedForDiagnostics
        {
            get { lock (_folderLock) return _folderWatcher != null; }
        }

        /// <summary>Diagnostics: watchers started, the failed attempts included.</summary>
        internal int FolderWatchStartsForDiagnostics
        {
            get { return System.Threading.Volatile.Read(ref _folderWatchStarts); }
        }

        /// <summary>Diagnostics: the watcher's Error event with <paramref name="error"/>, as the watcher raises it,
        /// so both error kinds can be driven without deleting a folder under a live handle.</summary>
        internal void RaiseFolderErrorForDiagnostics(Exception error)
        {
            OnFolderError(this, new ErrorEventArgs(error));
        }

        /// <summary>Diagnostics: the bracket Import and Download hold, so its effect on the watcher can be
        /// driven without a download that waits.</summary>
        internal IDisposable OwnFolderWritesForDiagnostics() { return BeginOwnFolderWrites(); }

        /// <summary>Diagnostics: where the watcher and the retry post their work, so a test can hold it in a
        /// queue and see the single-flight gate (AgentFlow's SetUiContextForSelfTest, the same seam).</summary>
        internal System.Threading.SynchronizationContext UiContextForDiagnostics
        {
            set { _ui = value; }
        }

        /// <summary>Everything the "Smart index" line is made of, gathered under the module's locks by
        /// <see cref="SmartIndexText"/>, so the wording itself is a pure function the probe asserts state by
        /// state. Times are local; <see cref="DateTime.MinValue"/> means "not known" or "none".</summary>
        internal sealed class SmartIndexFacts
        {
            public bool EngineLoaded = true;
            public bool SmartWanted;
            public int PoolCount;
            public bool AnyPacksInstalled;
            public SmartStandDownReason StandDown;
            public string StandDownDetail;
            public bool BuildFailed;
            public bool Ready, Complete;
            public int Indexed, Total;
            public IndexChange Reason;
            public bool Replaced;                // the build replaced an index or an attempt at one
            public DateTime StartedLocal;        // when the current build started
            public DateTime BuiltLocal;          // when its warm completed
            public DateTime RetryDueLocal;       // when the automatic retry runs; MinValue when none is pending
            public bool RetrySpent;              // this failure episode's automatic retry has already run
            public string FolderWatchProblem;    // null while the fortunes folder is watched
            public DateTime NowLocal = DateTime.Now;
        }

        /// <summary>
        /// What to tell the user about the smart index, in the module's voice. Pure so the wording is asserted,
        /// state by state (the probe's SmartIndexLineChecks). Three rules from the history of this line:
        ///
        /// <para>The pool size comes from the provider, never from the index's own counters: Warm() leaves
        /// ready=false / total=0 until its first batch publishes, and a status read from those alone said "no
        /// fortunes" on every rebuild however full the pool (pre-1.0.0).</para>
        ///
        /// <para>A stand-down is answered BEFORE any progress, because it is terminal and progress is not; a
        /// line that cannot fail to look busy said "Indexing N fortunes in the background" for ever on machines
        /// where nothing would ever be indexed (1.0.10, F137). One sentence per reason, each naming the action
        /// that fixes it, since they want different things from the user.</para>
        ///
        /// <para>Work in progress says when it STARTED (1.1.0). The host builds the pane at open, Apply and each
        /// ReloadPaneAfter action and cannot repaint it between, so "Rebuilding..." read ten minutes later is a
        /// snapshot; the start time is what lets the reader tell a snapshot from a stall.</para>
        ///
        /// <para>"Smart picks are off" stays the first words of the off state: three host self-tests read this
        /// line through SmartStatusTextForDiagnostics and assert that prefix after an Init with picks seeded
        /// off.</para>
        /// </summary>
        internal static string SmartIndexLineFor(SmartIndexFacts f)
        {
            if (f == null) return "";
            string line = SmartIndexSentence(f);
            // A control that can run degraded says so on every read: the folder is not watched, so the one change
            // the automation cannot see is a pack file changed outside the app, and Rescan folder is its answer.
            if (f.FolderWatchProblem != null)
                line += " ⚠ The fortunes folder cannot be watched here, so a pack file added or removed outside the " +
                        "app is picked up only by Rescan folder.";
            return line;
        }

        private static string SmartIndexSentence(SmartIndexFacts f)
        {
            if (!f.EngineLoaded) return "✗ The fortune engine isn't loaded — see the diagnostic log.";
            if (!f.SmartWanted) return "Smart picks are off, so fortunes are chosen at random.";
            if (f.PoolCount == 0) return EmptyPoolReason(f.AnyPacksInstalled);
            switch (f.StandDown)
            {
                case SmartStandDownReason.PoolTooLarge:
                    return "✗ Off for this selection, fortunes are chosen at random (" + Count(f.PoolCount) +
                           " fortunes is more than the index can hold, " + Count(VectorCache.MaximumEntries) +
                           "). Disable some packs or narrow the content level, and it rebuilds on its own.";
                case SmartStandDownReason.ModelAbsent:
                    return "✗ Off, fortunes are chosen at random (the text engine's model is missing from the " +
                           "module folder). Reinstall the Fortunes module to restore it.";
                case SmartStandDownReason.EmbedderNotReady:
                    return "✗ Off on this machine, fortunes are chosen at random (the text engine could not start" +
                           DetailAfter(": ", f.StandDownDetail) + "). Everything else works normally.";
                case SmartStandDownReason.ConstructionFailed:
                case SmartStandDownReason.WarmFailed:
                    // The two a retry can change (R-031): retried once on its own, then on the next change.
                    string cause = "the index could not be built" + DetailAfter(": ", f.StandDownDetail);
                    if (f.RetryDueLocal != DateTime.MinValue)
                        return "✗ Off for now, fortunes are chosen at random (" + cause + "). Trying again at " +
                               Clock(f.RetryDueLocal, f.NowLocal) + ".";
                    return "✗ Off, fortunes are chosen at random (" + cause +
                           (f.RetrySpent ? ", and the automatic retry failed too" : "") + "). It is tried again " +
                           "after the next pack or selection change, a Rescan folder, or the next start.";
            }
            if (f.Complete)
            {
                string indexed = f.Indexed == f.Total ? Count(f.Indexed) : Count(f.Indexed) + " of " + Count(f.Total);
                return "✓ Up to date (" + indexed + " fortunes" +
                       (f.BuiltLocal != DateTime.MinValue ? ", built " + Clock(f.BuiltLocal, f.NowLocal) : "") + ").";
            }
            string started = f.StartedLocal != DateTime.MinValue ? ", started " + Clock(f.StartedLocal, f.NowLocal) : "";
            if (f.Ready)
                return BuildingPhrase(f.Reason, f.Replaced) + " (" + Count(f.Indexed) + " of " + Count(f.Total) +
                       " fortunes indexed" + started + "). Smart picks already use the indexed ones.";
            return BuildingPhrase(f.Reason, f.Replaced) + " (" + Count(f.PoolCount) + " fortunes" + started +
                   "). Fortunes are chosen at random until the first batch is indexed.";
        }

        /// <summary>
        /// The Status card's one line (owner, 2026-10-06, mockup F2): "4,457 fortunes from 7 sources | smart
        /// index: up to date (built 14:02) | content: Clean + edgy". Pure, so every state is asserted. The three
        /// parts are the three questions the pane is opened to answer (what am I hearing from, is the smart index
        /// working, what may it say) and each is the short form of a card below it. It opens with ✗ only when the
        /// companion would be SILENT (no engine, an empty pool), the one state that must not read as fine; a
        /// smart index that is off still leaves fortunes coming, so it is said in words, not in red.
        /// </summary>
        internal static string StatusLineFor(SmartIndexFacts f, int enabledSources, string contentDisplay, bool noProfanity)
        {
            if (f == null) return "";
            if (!f.EngineLoaded) return "✗ The fortune engine isn't loaded, so the companion is silent — see the diagnostic log.";
            string content = "content: " + (string.IsNullOrEmpty(contentDisplay) ? LevelCleanDisplay : contentDisplay) +
                             (noProfanity ? ", profanity removed" : "");
            string line;
            if (f.PoolCount == 0)
                line = "✗ " + (f.AnyPacksInstalled ? "No fortunes match these filters, so the companion is silent"
                                                    : "No fortunes yet: add a pack") +
                       " | smart index: nothing to index | " + content;
            else
                line = Count(f.PoolCount) + (f.PoolCount == 1 ? " fortune" : " fortunes") +
                       (enabledSources > 0 ? " from " + Invariant(enabledSources) + (enabledSources == 1 ? " source" : " sources") : "") +
                       " | smart index: " + IndexClause(f) + " | " + content;
            if (f.FolderWatchProblem != null) line += " | ⚠ the fortunes folder is not watched: use Rescan folder after changing it";
            return line;
        }

        /// <summary>The smart-index part of the Status line, lower case, one clause.</summary>
        private static string IndexClause(SmartIndexFacts f)
        {
            if (!f.SmartWanted) return "off, picks are random";
            switch (f.StandDown)
            {
                case SmartStandDownReason.PoolTooLarge: return "off for this selection (too many fortunes), picks are random";
                case SmartStandDownReason.ModelAbsent: return "off, picks are random (the model is missing)";
                case SmartStandDownReason.EmbedderNotReady: return "off on this machine, picks are random";
                case SmartStandDownReason.ConstructionFailed:
                case SmartStandDownReason.WarmFailed:
                    return f.RetryDueLocal != DateTime.MinValue
                        ? "off for now, picks are random (trying again at " + Clock(f.RetryDueLocal, f.NowLocal) + ")"
                        : "off, picks are random (the build failed)";
            }
            if (f.Complete)
                return "up to date" + (f.BuiltLocal != DateTime.MinValue ? " (built " + Clock(f.BuiltLocal, f.NowLocal) + ")" : "");
            string doing = BuildingPhrase(f.Reason, f.Replaced);
            // "smart index: building the index" says it twice; the first build is just "building" here.
            doing = doing == "Building the index" ? "building" : char.ToLowerInvariant(doing[0]) + doing.Substring(1);
            if (f.Ready) return doing + " (" + Count(f.Indexed) + " of " + Count(f.Total) + " indexed, in use)";
            return doing + ", picks are random until it is ready";
        }

        /// <summary>The words for a build in progress, by what started it.</summary>
        private static string BuildingPhrase(IndexChange why, bool replaced)
        {
            if (why == IndexChange.Retry) return "Retrying the index after a failed build";
            if (!replaced || why == IndexChange.Startup) return "Building the index";
            switch (why)
            {
                case IndexChange.Packs: return "Rebuilding after a pack change";
                case IndexChange.Folder: return "Rebuilding after a change in the fortunes folder";
                default: return "Rebuilding after a selection change";
            }
        }

        private static string DetailAfter(string separator, string detail)
        {
            return string.IsNullOrEmpty(detail) ? "" : separator + detail;
        }

        /// <summary>A time of day in the user's own format; the date as well when it is not today.</summary>
        private static string Clock(DateTime local, DateTime nowLocal)
        {
            return local.ToString(local.Date == nowLocal.Date ? "t" : "g", System.Globalization.CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// Why the pool is empty, in the user's terms. Nothing installed is a different problem from
        /// everything filtered out, and telling someone with 129 packs to "add a pack" sends them the
        /// wrong way entirely. The no-packs sentence said "add a pack, then rebuild" until 1.1.0, naming the
        /// button that went; a pack added now loads without one.
        /// </summary>
        internal static string EmptyPoolReason(bool anyPacksInstalled)
        {
            return anyPacksInstalled
                ? "No fortunes match these filters — the companion will stay silent. " +
                  "Widen the content level, or enable more packs below."
                : "No fortunes yet — add a pack and it loads on its own.";
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
            // The folder watch and both timers go before anything else, so nothing new is queued while the rest
            // comes down (1.1.0). A rebuild already running publishes nothing: PublishEngine reads the token
            // cancelled above, and ScheduleSmartPicker the flag set below.
            CloseFolderWatch();
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
                // ...and nothing schedules another (1.1.0): a retry or a rebuild arriving after this line starts
                // no build, so none outlives the module undisposed. ScheduleSmartPicker is the one gate every
                // build passes, so it is the one place this is checked.
                _shuttingDown = true;
                doomed = _smart;
                _smart = null;
                _indexedSignature = null;
                _smartBuilding = false;
            }
            // Inline, here only: process exit is what Dispose's 3 s cap exists for.
            if (doomed != null) { try { doomed.Dispose(); } catch { } }
            // Static, so it outlives the instance unless dropped here. Same contract as
            // AiBrain.LogSink, which is nulled in its own Shutdown for the same reason. Cleared only while
            // still THIS instance's delegate: the probe Inits and Shuts down module instances beside the live
            // one, and an unconditional null here dropped the live module's sink under it, so a stand-down it
            // reported meanwhile reached no log (RA-097).
            if (ReferenceEquals(SmartFortunes.LogSink, _smartSink)) SmartFortunes.LogSink = null;
            if (ReferenceEquals(FortuneProvider.LogSink, _providerSink)) FortuneProvider.LogSink = null;
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

    /// <summary>What started a smart-index build, for the pane's "Smart index" line (fortunes 1.1.0).</summary>
    internal enum IndexChange
    {
        /// <summary>Init: the first build of the session, which also resumes one an exit interrupted (the
        /// vector cache keeps what was embedded) and re-embeds after a model or format change (the cache
        /// refuses a file whose asset fingerprint or format differs).</summary>
        Startup,
        /// <summary>Apply: packs or genres ticked (one at a time or through an "All" row), the content level,
        /// the profanity filter, smart picks turned on.</summary>
        Selection,
        /// <summary>A pane action that changes the folder: Rescan folder, Import, Download selected.</summary>
        Packs,
        /// <summary>The folder watcher: a pack file added, removed or edited outside the app.</summary>
        Folder,
        /// <summary>The one automatic retry of a build that failed.</summary>
        Retry,
    }
}
