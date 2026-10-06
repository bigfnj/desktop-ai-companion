using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Records a meeting (a selectable microphone + the system output over WASAPI loopback), transcribes it
    /// offline with a local Whisper, names it from the calendar (the Reminder module's "meeting.current"
    /// shared-context publish, else a timestamp), snapshots the screen on a hotkey, and purges the audio +
    /// snapshots after 72 hours while keeping the transcript. Start/stop and snapshot are on the tray and a
    /// global hotkey each. A visible "recording" indicator (tray text + a spoken cue) shows while capturing.
    /// </summary>
    public sealed class RemembranceModule : IModule
    {
        internal const string Id = "remembrance";
        private const int PurgeIntervalMs = 60 * 60 * 1000;   // hourly
        private const int FirstPurgeDelayMs = 60 * 1000;      // the first tick only; see Init

        private const string DefaultRecordHotkey = "Ctrl+Alt+R";
        private const string DefaultSnapshotHotkey = "Ctrl+Alt+S";

        private IHost _host;
        private IModuleStorage _storage;
        private IModuleSettings _settings;
        private SynchronizationContext _ui;
        private System.Windows.Forms.Timer _purgeTimer;
        private EventHandler _purgeHandler;
        // Held in a field for the same reason _purgeHandler is: you cannot unsubscribe a delegate you did not
        // keep, and an event this module stays subscribed to outlives Shutdown. See Shutdown for what that
        // costs.
        private Action _hostShutdownHandler;
        private readonly List<IDisposable> _hotkeys = new List<IDisposable>();

        private AudioRecorder _recorder;
        private CapturePaths _current;
        private string _currentMeetingName = "";
        private IReadOnlyList<string> _currentAttendees;
        private volatile bool _recording;
        private string _currentBase = "";
        private string _lastStatus = "Idle.";
        // Interlocked single-flight gate for the snapshot's background capture; see TakeSnapshot.
        private int _snapshotInFlight;
        // Interlocked single-flight gate for the Ollama model pull; see StartRecommendedPull (RA-159).
        private int _pullInFlight;
        // Interlocked single-flight gates for the two cards' Validate buttons, the pull's shape (RA-159).
        private int _summaryCheckInFlight;
        private int _whisperCheckInFlight;
        // The running pull's latest progress line, for the answer "Download that model" gives when the pull is
        // still going at its bound (BUG-013). Its own field rather than _lastStatus, which a transcription
        // finishing in the same seconds would overwrite.
        private volatile string _lastPullProgress = "";
        private int _purgesStarted;
        // Cancels a Whisper install still running when the module shuts down (F184).
        private CancellationTokenSource _installCts;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = Id,
            Name = "Remembrance",
            Version = "2.1.0",   // 2.1.0: the summary can run through a coding-agent CLI (owner decision, 2026-10-06),
                                 //        which reverses the shipped "local-only, no cloud summary path, ever": the owner
                                 //        ruled that the choice belongs to the user. "Summary runs on", in a new card,
                                 //        chooses "Local Ollama (default)", "Claude Code CLI" or "Codex CLI"; an install
                                 //        that never chose stays on Ollama, and the choice is this module's own, so it works
                                 //        with AI Brain absent. On a CLI the transcript goes to Anthropic or OpenAI in ONE
                                 //        call, the single-shot prompt on stdin, the summary file's header says where it
                                 //        went, and remembrance.busy is not raised for it, since nothing runs on this
                                 //        machine's GPU; a transcript over the one-call limit takes the local map-reduce,
                                 //        as before. Transcription stays local Whisper (neither CLI accepts audio). The card
                                 //        says which account each CLI is signed into and carries Validate and Update CLI,
                                 //        through the runner AI Brain 1.3.0 uses (shared/CodingAgentCli); the Ollama
                                 //        settings grey while a CLI is chosen. Lane feature/cli-backend.
                                 // 2.0.0: MAJOR, because a setting changes meaning (docs/VERSIONING.md, "dropping a
                                 //        setting or changing its meaning"): "Create a folder per capture" was a
                                 //        checkbox whose OFF state filed every capture flat in the storage root,
                                 //        and it is now a choice between a folder per capture and a folder by
                                 //        date. No new capture is written flat into the root any more.
                                 //        BUG-013: "Download that model" fetched the SAVED choice, not the one on
                                 //        screen, and said only "Downloading ... in the background". Every action
                                 //        that reads a setting the pane edits now reads what is on screen
                                 //        (InvokeWithPendingAsync, so MinHostVersion is 1.2.5); the download waits up
                                 //        to 15 s and answers ✓ selected, ✗ with the reason, or ⚠ with its progress;
                                 //        an Apply that did not touch a field keeps what the module wrote there in
                                 //        the background meanwhile (the pull's selection); and the pull's start and
                                 //        success are logged with the model and the address.
                                 //        Summary card: "Find local summary models" is "Refresh local models", AI
                                 //        Brain's name for the same job, and "Validate" replaces "Test the
                                 //        summarizer": Ollama answers at the address on screen, the model on screen
                                 //        is installed there (an untagged name matches its ":latest"), and it answers
                                 //        one short request, each failure named, the success timed; off the UI
                                 //        thread, one at a time.
                                 //        Transcription card: "Find an installed Whisper" is "Refresh local models"
                                 //        and keeps the pair on screen when both files exist (naming any other model
                                 //        it finds), detecting and adopting only when one is missing; "Validate",
                                 //        right after it, checks that whisper-cli and a real-sized model are there and
                                 //        runs them on a 2-second clip through the install check, off the UI thread,
                                 //        one at a time.
                                 //        Storage: "Folders for new captures" chooses "Create a folder per capture"
                                 //        or "Create a folder by date" (stored as folderLayout = capture / date; an
                                 //        install that never chose reads its old checkbox, OFF meaning by date, and
                                 //        that key is left as it was). By date files each capture in the folder for
                                 //        its local start day under the old flat names. A snapshot taken with nothing
                                 //        recording goes into that day's folder, or into "Snapshots" per capture,
                                 //        never the root. The purge walks both kinds of folder by their own strict
                                 //        names and shapes and removes one only when that same pass emptied it.
                                 //        remembrance.busy: published while a local model runs here (whisper-cli on
                                 //        the stop path, in "Transcribe a WAV file…" and the Transcription Validate;
                                 //        the summary on the stop path, in "Summarize a transcript…" and the Summary
                                 //        Validate), {"phase":...,"at":...} with "at" refreshed at every phase change
                                 //        and before every summary request, overlapping spans counted, cleared in a
                                 //        finally on every path and synchronously by Shutdown and the host's shutdown.
                                 // 1.0.17: stopping a recording at exit no longer waits out 10 s per source.
                                 //         NAudio delivered RecordingStopped through the WinForms
                                 //         SynchronizationContext it captured when the capture was built on
                                 //         the UI thread -- the very thread then blocked waiting for it with
                                 //         no message loop -- so every wait ran to its timeout, and an OS or
                                 //         Restart Manager exit killed the process before the scratch WAVs
                                 //         were finalised (BUG-009). Captures are built with no context
                                 //         current now. Also from the 2026-09-29 audit: the scratch WAVs are
                                 //         purge shapes, a save still in flight is waited for at exit, the
                                 //         loopback track is kept fed with silence so it stays aligned with
                                 //         the microphone, a partial map-reduce says which parts it covers,
                                 //         whisper's time limit follows the recording length, the download
                                 //         button opens the release LIST, the release lookup is bounded,
                                 //         snapshots encode off the UI thread, Init neither enumerates
                                 //         devices nor purges, and LaunchProcess is declared. The self-test
                                 //         resets the fake host's recorded links through ClearOpenedLinks
                                 //         (the ModuleKit fake hands out snapshots now, N-remembrance-01).
                                 //         A download the CALLER cancels (Shutdown mid-install) deletes its
                                 //         .part instead of leaving a stale partial model (N-remembrance-02).
                                 //         Lane fix/deadcode, same version: AudioDevice carries the name the
                                 //         dropdown stores and no unread id; the release-LIST parser is what the
                                 //         self-test's digest check reads; four garbled changelog lines and the
                                 //         purge summary are repaired (F167, F174, F185, F170).
                                 //         Lane burn/remembrance, same version (Phase 8): the purge descends only
                                 //         into capture-named folders and removes one it has emptied; the Ollama
                                 //         pull is bounded on silence, single-flight and cancelled at Shutdown; a
                                 //         Whisper install that fails its run check is marked and not adopted, and
                                 //         "Set up Whisper for me" follows the model dropdown; every normal stop's
                                 //         save is tracked and its outcome said, a timed-out flush is single-shot,
                                 //         a stop that captured nothing says so; two snapshots inside one second
                                 //         keep both files; the transcript header dates the capture's start; the
                                 //         whisper kill message tells the truth about an unreadable length; a
                                 //         failed settings write is answered honestly; a keep-alive or capture
                                 //         that dies mid-recording is logged (RA-150 to RA-168 except RA-162,
                                 //         R-036 to R-042, N-deadcode-01).
                                 // 1.0.16: the 72-hour purge may only delete the file SHAPES this module
                                 //         writes. Three of the four branches were looser than that: any
                                 //         snap*.png inside a capture folder, ANY .wav in the root, and
                                 //         Contains(" - snap") which also matched "holiday - snapshot.png".
                                 //         The .wav one is the widest: storageLocation is free text with a
                                 //         folder picker, so a user who pointed it at a folder holding their
                                 //         own audio lost all of it past the window, with File.Delete and no
                                 //         prompt. Three old fixtures asserted TRUE for names nothing
                                 //         produces, which is what made the loose branches look tested.
                                 // 1.0.15: the options pane no longer freezes ~3s on first open. The model
                                 //         probe was Task.Run + Wait(3s), which moved the HTTP call off the
                                 //         UI thread and then blocked it anyway; a REFUSED localhost
                                 //         connection burns the full deadline. Also: the stub transcript
                                 //         named a "Re-transcribe" button that never existed.
                                 // 1.0.14: snapshots taken with no recording in flight are finally covered by
                                 //         the 72-hour purge. They are written to the storage ROOT as
                                 //         "snap <stamp>.png" and the root filter wanted " - snap", so full
                                 //         screen captures accumulated forever against a promise the module
                                 //         header makes to the user.
                                 // 1.0.13: one WASAPI enumeration per options-pane open instead of four, and
                                 //         the device COUNT in the status line now comes from the same snapshot
                                 //         as the dropdowns, so the two can no longer disagree.
                                 // 1.0.12: the Remote Desktop warning no longer tells the user to restart.
                                 //         The device lists have refreshed on every pane open since
                                 //         RefreshDynamicOptions moved into Load; reopening is enough.
                                 // 1.0.11: closing the app while recording no longer loses the recording. Every
                                 //         part of Stop ran inside a Task.Run nothing waited for, so the process
                                 //         exited mid-AudioRecorder.Stop(): no mixed WAV, no transcript, and two
                                 //         scratch files left with unfinalised RIFF headers, which no purge shape
                                 //         recognised until 1.0.17 (F171). The save is synchronous on shutdown
                                 //         now; transcription is skipped and both the status line and the log
                                 //         say so.
                                 // 1.0.10: a transcription failure says WHICH failure; whisper's pipes drain
                                 //         concurrently so its timeout can fire; and the model-pull
                                 //         continuation no longer writes settings off the UI thread.
                                 // 1.0.9: the hourly purge may now only delete files THIS MODULE wrote.
                                 //        It used to enumerate AllDirectories under the user-chosen
                                 //        storage folder and File.Delete -- not the recycle bin -- every
                                 //        .wav, .mp3 and .png older than 72 hours, whoever wrote it. That
                                 //        folder is free text labelled "Where recordings are stored", so
                                 //        pointing it at Pictures destroyed the photo library on the next
                                 //        Init. Now: this module's own file shapes only, one level deep,
                                 //        and .mp3 is gone entirely because it never wrote one.
                                 // 1.0.8: a button that opens both downloads in the browser,
                                 //        which is the path endpoint protection trusts. The model
                                 //        link follows the dropdown, so what you download is what
                                 //        "Set up Whisper for me" would have fetched rather than
                                 //        one of eleven files in that repository.
                                 // 1.0.7: a blocked download says what to do instead.
                                 //        Diagnosed across two machines: the fetch is terminated
                                 //        by Microsoft Defender Network Protection, which scores
                                 //        the CALLING program and does not recognise an unsigned
                                 //        app asking to download an executable. The machine where
                                 //        it works reads EnableNetworkProtection 0; the one where
                                 //        it fails reads 1, and a signed PowerShell gets HTTP 200
                                 //        on the same URL there. Nothing in the module is wrong,
                                 //        so the fix is to stop being a dead end: recognise the
                                 //        local abort, report whether that policy is on, and
                                 //        point at the two Browse buttons that already work.
                                 // 1.0.6: failures now report the whole exception chain.
                                 //        .NET renders a TLS fault as "The SSL connection could
                                 //        not be established, see inner exception" -- a message
                                 //        that names the information you need and withholds it --
                                 //        and three catch blocks passed only ex.Message through
                                 //        to the pane, so the cause was unknowable from the UI.
                                 // 1.0.5: the summary dropdown fills ITSELF on first open, and
                                 //        preselects. It was only ever filled by the "Find local
                                 //        summary models" button, so until you guessed that a button
                                 //        was a prerequisite rather than a refresh, the control was an
                                 //        empty box. /api/tags answers in 5 ms and carries the
                                 //        capability data, so there was nothing to make anyone click
                                 //        for. Nothing found now reads as a label rather than a blank
                                 //        row, and that label can never be stored as a model tag.
                                 // 1.0.4: the summary model can be fetched from the pane. A curated
                                 //        list of four tags (all checked against the registry, sizes
                                 //        summed from the manifests), a pull over Ollama's /api/pull
                                 //        with progress, and a link to ollama.com for the case where
                                 //        the runtime itself is missing. A pull is a download, not
                                 //        inference: it costs disk, never VRAM.
                                 // 1.0.3: the three discovered dropdowns (both recording devices and the
                                 //        summary model) re-read their contents on every pane open. They
                                 //        were built once in Init, so a closed dropdown could only ever
                                 //        offer what existed at startup: "Find local summary models"
                                 //        saved a model the list could not show, so it rendered blank
                                 //        and the next Apply wrote the blank back over it. Also fills
                                 //        the Whisper paths in from an existing install, once, and names
                                 //        the button that does the download.
                                 // 1.0.1: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded). NO functional change here; the bump exists
                                 //        because the catalog offers an update by VERSION, so without it the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.1.2: payload refresh only, no behaviour change -- the bundled ModuleKit
                                 //        gained RecordingHost.RaiseFullscreenChanged (host 1.9.9).
                                 // 1.1.1: each tray entry gets its own icon (recording / snapshot), per the
                                 //        project convention that no tray row is icon-less.
                                 // 1.1.0: one-click Whisper setup (detect, else fetch from upstream) so a
                                 //        tester no longer has to install a C++ binary and a 141 MB model by
                                 //        hand, plus an optional local-Ollama summary written beside the
                                 //        transcript. Both need Network; nothing else changed.
            // Publishing/reading shared context + the capture permission flags are host 1.9.0.
            // Raised to 1.2.5 on 2026-10-02 (BUG-013): every pane action that reads a setting the pane can edit
            // now sets PaneAction.InvokeWithPendingAsync, which host 1.2.5 introduced, so that it acts on what is
            // ON SCREEN rather than on the last Apply. Contracts is the host's single shared copy, so on an older
            // host the first setter of that property is a MissingMethodException inside Init, and the load gate
            // is what turns it into a refusal with a reason. The shipping host is 1.2.7, so the sequencing rule
            // in docs/VERSIONING.md is already met. The self-test pins the floor against the delegate. The storage
            // choice's SettingKind.Radio arrived in an earlier host, under this floor.
            MinHostVersion = "1.2.5",
            // Network is for two user-initiated local/upstream calls and nothing else: fetching whisper.cpp
            // from its GitHub release + Hugging Face, and talking to a LOOPBACK Ollama for the summary. There
            // is deliberately no cloud transcription or cloud summary path, because a recording can be
            // privileged or consent-regulated audio.
            // Speech was MISSING until 2026-09-17, found by an ABI audit. Announce() is how this
            // module reports everything it does -- "Recording started", "Transcript ready", "Summary
            // ready", "Snapshot saved" -- and it goes through IHost.SayAll from eight call sites. The
            // consent line is an affirmative claim about what a module does, and speech was absent
            // from it while being this module's only user-visible channel.
            // LaunchProcess: this module starts whisper-cli.exe for every transcription (Transcriber.RunWhisper)
            // and runs the binary "Set up Whisper for me" downloaded as its verify probe
            // (WhisperInstaller.TryVerify). The flag shipped in host 1.2.5 naming this module as a holder in
            // its own comment, and no module declared it until 2026-09-29 (F226): the pane's "wants:" line
            // reads as an affirmative list of everything the module does, and it omitted the executable the
            // module spawns. A disclosure, not a gate, like every flag here; an older host drops a name it
            // does not know, so MinHostVersion stays.
            Permissions = ModulePermissions.Speech
                | ModulePermissions.Microphone | ModulePermissions.SystemAudio
                | ModulePermissions.ScreenContext | ModulePermissions.Hotkey | ModulePermissions.Storage
                | ModulePermissions.Network | ModulePermissions.LaunchProcess,
        };

        public void Init(IHost host)
        {
            _host = host;
            _storage = host.GetStorage(Id);
            // A host may legitimately decline to hand out a settings store -- the ABI's own convention is that
            // a refused service degrades (GetCompanionManager returns a refusing instance, RegisterHotkey a no-op
            // handle) rather than throwing into a module, and the app's --module-selftest host returns null
            // for both storage and settings. The options SCHEMA is built here, during Init, and it needs the
            // saved model name for its dropdown, so an unguarded null store took the whole module down with a
            // NullReferenceException at load time. Fall back to an in-memory store: every setting then reads
            // as its default and nothing persists, which is the correct degraded behaviour.
            _settings = host.GetSettings(Id) ?? new ModuleKit.MemoryModuleSettings();
            _ui = SynchronizationContext.Current;
            _uiThreadId = Environment.CurrentManagedThreadId;   // remembrance.busy publishes synchronously from here

            host.AddOptionsPane(BuildOptionsPane());
            host.AddTrayItems(new[] { BuildRecordTrayItem(), BuildSnapshotTrayItem() });
            RegisterHotkeys();

            // NO PURGE FROM INIT. The first purge runs a minute after start, on the timer's first tick, and
            // every later one an hour apart (OnPurgeTick). Init used to purge directly, and Init runs in every
            // headless self-test that loads this module -- the convention loader's own instance, the link
            // block in SelfTest, --module-host-selftest three times, both Fortunes flags -- with a settings
            // store that answers the DEFAULT storage root: the user's real Documents\Remembrance. So every
            // gate run fired a File.Delete pass over the user's own folder from a test process (F180). A
            // process with no message loop never reaches the first tick; the app, which has one, purges 60 s
            // after launch instead of during the StartUp constructor, which against a 72-hour retention is
            // no change at all.
            _purgeTimer = new System.Windows.Forms.Timer { Interval = FirstPurgeDelayMs };
            _purgeHandler = delegate { OnPurgeTick(); };
            _purgeTimer.Tick += _purgeHandler;
            _purgeTimer.Start();

            _installCts = new CancellationTokenSource();

            _hostShutdownHandler = OnHostShutdown;
            try { host.HostShutdown += _hostShutdownHandler; } catch { }
        }

        public void Shutdown()
        {
            // FIRST, synchronously, before _host is nulled at the end: an unloaded or updated module never leaves
            // remembrance.busy set, whatever span is still running (2.0.0).
            CloseBusy();
            try { if (_recording) StopRecording(shuttingDown: true); } catch { }
            FlushPendingSave();
            CancellationTokenSource install = _installCts;
            _installCts = null;
            if (install != null) { try { install.Cancel(); install.Dispose(); } catch { } }
            DisposeHotkeys();
            if (_purgeTimer != null)
            {
                try { _purgeTimer.Stop(); if (_purgeHandler != null) _purgeTimer.Tick -= _purgeHandler; _purgeTimer.Dispose(); }
                catch { }
                _purgeTimer = null;
                _purgeHandler = null;
            }
            // Unsubscribe. Two things go wrong if this module stays wired to a host event past Shutdown, and
            // the first is the one that bites: the handler keeps being CALLED, on an instance whose Init state
            // is already gone. ReminderModule's Shutdown says the same thing about CompanionSpawned.
            // The second is that the host's event field lives in the DEFAULT load context, so the
            // subscription roots this instance and with it the module's collectible AssemblyLoadContext that
            // ModuleHost.ShutdownAll unloads immediately after this returns. Removing this root does not by
            // itself make that unload collect -- CompanionHost.TrayItems and .OptionsPanes hold delegates over
            // this instance and are never cleared -- but it is the only one the module can remove.
            // Guarded because the host may refuse the event as readily as it offered it (the += is in a try too).
            if (_host != null && _hostShutdownHandler != null)
            {
                try { _host.HostShutdown -= _hostShutdownHandler; } catch { }
            }
            _hostShutdownHandler = null;
            _host = null;
        }

        // The host raises this BEFORE it disposes the module host, so this is the last moment at which
        // a recording can still be saved. It asks for the synchronous path for that reason.
        private void OnHostShutdown()
        {
            CloseBusy();   // synchronous and never posted: there is no message loop left (see CloseBusy)
            try { if (_recording) StopRecording(shuttingDown: true); } catch { }
            FlushPendingSave();
        }

        /// <summary>
        /// How long shutdown waits for the saves that NORMAL stops left running. Two minutes: the mix of a
        /// 45-minute two-source capture is tens of seconds, and past this the process is leaving anyway. Not
        /// readonly, so the self-test can shorten it and prove a timed-out wait is single-shot (RA-155).
        /// </summary>
        internal static TimeSpan SaveFlushBound = TimeSpan.FromMinutes(2);

        // EVERY save a normal stop handed to a pool task, not only the latest. This was one Task field
        // overwritten by each stop, so a stop-restart-stop inside the first mix window left the first save
        // untracked: an exit then waited for the second alone and the first mix died with the process,
        // leaving a truncated recording.wav beside its finalised scratch WAVs (R-038). Pruned of completed
        // tasks on every touch; read and written under its own lock, since stops and shutdown hooks meet here.
        private readonly List<Task> _pendingSaves = new List<Task>();
        // Set once a flush has waited out SaveFlushBound, so the OTHER shutdown hook does not wait a second
        // time (RA-155). Both hooks run on one exit -- HostShutdown first, then Shutdown -- and each used to
        // wait the full bound: four minutes in all, with "may be incomplete" logged before the second wait
        // went on to complete the very save the first had given up on.
        private bool _flushGaveUp;

        private void TrackPendingSave(Task save)
        {
            if (save == null) return;
            lock (_pendingSaves)
            {
                _pendingSaves.RemoveAll(t => t.IsCompleted);
                _pendingSaves.Add(save);
            }
        }

        /// <summary>
        /// Wait for the saves normal stops handed to pool tasks, whichever are still running.
        ///
        /// The 1.0.11 fix made the save synchronous only while a recording was still IN FLIGHT at shutdown.
        /// Once the user had pressed stop, the same save -- stop the captures, then read, downmix and resample
        /// both scratch WAVs into recording.wav -- ran on an untracked pool task that nothing at shutdown knew
        /// about, and both shutdown hooks gate on _recording, which the stop had already cleared. Stop a
        /// 45-minute meeting, hear "Saving and transcribing", close the app from the tray a few seconds later:
        /// the process exited mid-mix, recording.wav was truncated with an unfinalised header, no transcript,
        /// and the two scratch WAVs sat beside it, finalised but surfaced by nothing (F176). So the stop path
        /// keeps its task in _pendingSaves, and this waits on them, bounded, saying so in the log either way.
        /// Transcription is deliberately NOT waited for: whisper on a long recording takes minutes, and the
        /// audio is the irreplaceable part; it is saved and can be transcribed later.
        ///
        /// THE LINE SAYS WHAT HAPPENED. The save task carries its outcome (true saved, false nothing captured,
        /// the exception when the stop failed), so a faulted save is logged as failed rather than as finished,
        /// which is what the old catch-all `finished = true` produced (R-038).
        /// </summary>
        private void FlushPendingSave()
        {
            Task[] pending;
            bool gaveUpBefore;
            lock (_pendingSaves)
            {
                _pendingSaves.RemoveAll(t => t.IsCompleted);
                pending = _pendingSaves.ToArray();
                gaveUpBefore = _flushGaveUp;
            }
            if (pending.Length == 0) return;
            if (gaveUpBefore)
            {
                Log("a save is still running and was already waited for once this exit; not waiting again");
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            Log((pending.Length == 1
                    ? "a recording is"
                    : pending.Length.ToString(CultureInfo.InvariantCulture) + " recordings are")
                + " still being saved; waiting up to " +
                ((int)SaveFlushBound.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s before the app closes");
            bool finished;
            string failure = null;
            try { finished = Task.WaitAll(pending, SaveFlushBound); }
            catch (AggregateException ex)
            {
                // Every save completed and at least one faulted. Its own catch logged the detail; this line
                // must not read "finished" over it.
                finished = true;
                failure = ex.InnerExceptions.Count > 0 ? ex.InnerExceptions[0].Message : ex.Message;
            }
            string elapsed = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
            if (!finished)
            {
                lock (_pendingSaves) { _flushGaveUp = true; }
                Log("gave up waiting for the save after " + elapsed +
                    " ms: recording.wav may be incomplete; the finalised scratch WAVs are beside it. Not waited for again this exit");
            }
            else if (failure != null)
            {
                Log("the save failed after " + elapsed + " ms: " + failure);
            }
            else
            {
                Log((pending.Length == 1 ? "the save finished after " : "the saves finished after ") + elapsed + " ms");
            }
        }

        /// <summary>The two mid-capture failures the recorder reports at Stop (RA-150), to the log; the
        /// recording itself is kept either way, so the log is the only place they can be seen.</summary>
        private void LogCaptureTroubles(AudioRecorder recorder)
        {
            if (recorder.KeepAliveEndedEarly != null)
                Log("the silent keep-alive stream beside the system output stopped before the recording did (" +
                    recorder.KeepAliveEndedEarly + "); from that point the system track may run short of the " +
                    "microphone, the way every version before 1.0.17 recorded");
            if (recorder.CaptureFailure != null)
                Log(recorder.CaptureFailure + "; its scratch track holds what arrived before that");
        }

        /// <summary>The host log, tolerating a host that has already gone: the transcription continuation of
        /// a normal stop can outlive Shutdown, and the log is where it reports.</summary>
        private void Log(string message)
        {
            IHost host = _host;
            if (host == null) return;
            try { host.Log(Id, message); } catch { }
        }

        // --- hotkeys ---------------------------------------------------------------------------------

        private void RegisterHotkeys()
        {
            DisposeHotkeys();
            Add(_settings.Get("recordHotkey", DefaultRecordHotkey), ToggleRecording);
            Add(_settings.Get("snapshotHotkey", DefaultSnapshotHotkey), TakeSnapshot);
        }

        private void Add(string combo, Action onPressed)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            // _host is nulled at Shutdown, and a hotkey re-registration is reachable from the options pane's
            // Save, so this cannot assume a host is still there.
            IHost host = _host;
            if (host == null) return;
            try { IDisposable h = host.RegisterHotkey(combo.Trim(), onPressed); if (h != null) _hotkeys.Add(h); }
            catch { }
        }

        private void DisposeHotkeys()
        {
            foreach (IDisposable h in _hotkeys) { try { h.Dispose(); } catch { } }
            _hotkeys.Clear();
        }

        // --- record / stop / snapshot ----------------------------------------------------------------

        private void ToggleRecording()
        {
            if (_recording) StopRecording();
            else StartRecording();
        }

        private void StartRecording()
        {
            if (_recording) return;
            CaptureStore store = null;
            try
            {
                MeetingContext mc = MeetingContext.Parse(_host.ReadContext(MeetingContext.Key));
                _currentMeetingName = mc.Name;
                _currentAttendees = mc.Attendees;

                store = new CaptureStore(_settings.Get("storageLocation", CaptureStore.DefaultRoot()), CurrentFolderLayout());
                _current = store.NewCapture(mc.Name, DateTimeOffset.Now);
                _currentBase = _current.BaseName;
                LastCaptureForSelfTest = _current;

                _recorder = new AudioRecorder();
                _recorder.Start(_current.Audio,
                    _settings.GetBool("sysEnabled", true), _settings.Get("sysDevice", ""),
                    _settings.GetBool("micEnabled", true), _settings.Get("micDevice", ""));
                if (_recorder.KeepAliveFailure != null)
                    Log("the system output is recorded without a silent keep-alive stream (" + _recorder.KeepAliveFailure +
                        "); a gap before the meeting's audio starts will shift the system track early");
                _recording = true;
                _lastStatus = "Recording: " + _currentBase;
                Announce("Recording started: " + _currentBase);
            }
            catch (Exception ex)
            {
                _recording = false;
                _lastStatus = "Could not start: " + ex.Message;
                try { _host.Log(Id, "start failed: " + ex.Message); } catch { }
                Announce("Could not start recording. " + ex.Message);
                try { if (_recorder != null) { _recorder.Dispose(); _recorder = null; } } catch { }
                // NewCapture made the capture folder before any device was opened; with nothing recorded into
                // it there is nothing to keep, and one empty folder per failed attempt is what a Remote
                // Desktop session with no microphone used to leave behind (F171). Only when it IS empty --
                // AudioRecorder has already deleted its own header-only scratch by now -- and only a folder this
                // start CREATED: a day folder that already held other captures, or a user's own empty folder of
                // that name, is not this start's to remove (2.0.0). No layout writes into the root any more.
                if (store != null && _current != null && _current.CreatedDirectory)
                    CaptureStore.TryRemoveEmptyCaptureFolder(_current.Directory);
                _current = null;
            }
        }

        /// <summary>The paths of the most recent capture, for the self-test's stop-path checks.</summary>
        internal CapturePaths LastCaptureForSelfTest { get; private set; }

        private void StopRecording() { StopRecording(false); }

        /// <summary>
        /// Stop, save, and (unless the app is closing) transcribe.
        ///
        /// THE SAVE IS SYNCHRONOUS ON SHUTDOWN, and that is the whole point of the flag. Everything here
        /// used to run inside a Task.Run that nothing waited for, so closing the app mid-recording lost
        /// the recording outright: OnHostShutdown returned in microseconds, StartUp disposed the module
        /// host, Alc.Unload() ran, and the process exited while the background task was still inside
        /// AudioRecorder.Stop(). No mixed WAV, no transcript, and the two scratch files left with
        /// unfinalised RIFF headers -- because the WaveFileWriter.Dispose() that patches the data-chunk
        /// length runs only in the RecordingStopped handler and in DisposeSource. Those scratch files matched
        /// no purge shape until 1.0.17 (F171), so they sat on disk with broken headers for ever; either way a
        /// 45-minute meeting became nothing usable.
        ///
        /// What is NOT waited for on shutdown is transcription: Whisper on a long recording takes
        /// minutes, and the audio is the irreplaceable part. It is saved and can be transcribed later;
        /// the status line and the log both say so rather than implying a transcript exists.
        ///
        /// THE COST IS THE MIX, AND IT IS LOGGED. Stopping the captures is milliseconds now -- it was the
        /// full 10 s bound per source on this path, deterministically, until 2026-09-29, because NAudio
        /// posted RecordingStopped to the UI thread that was doing the waiting (BUG-009; the account is on
        /// AudioRecorder.OpenCapture). MixToWhisperWav then reads both scratch WAVs, downmixes to mono,
        /// resamples to 16 kHz and writes the result -- for a 45-minute capture that is hundreds of millions
        /// of samples, tens of seconds on the UI thread. That is the deliberate trade: the alternative
        /// shipped for months and simply lost the meeting. Both figures go to the log, so the trade can be
        /// seen rather than assumed. It costs nothing on any shutdown where nothing is recording.
        ///
        /// NOTHING IS ANNOUNCED ON THE SHUTDOWN PATH. Application.Run has returned, so there is no loop to
        /// speak through; the Post only ever succeeded because WinForms never disposes its marshalling
        /// control, and it sat BEFORE the save as the one call here whose failure would have skipped the
        /// save (F175). The log line is the record on that path.
        ///
        /// The normal path hands the save to a pool task and keeps that task in _pendingSave, so that an
        /// exit inside the mix window waits for it (FlushPendingSave, F176).
        /// </summary>
        private void StopRecording(bool shuttingDown)
        {
            if (!_recording || _recorder == null) return;
            AudioRecorder recorder = _recorder;
            CapturePaths paths = _current;
            string meetingName = _currentMeetingName;
            IReadOnlyList<string> attendees = _currentAttendees;
            string whisperExe = _settings.Get("whisperExe", "");
            string model = _settings.Get("whisperModel", "");
            bool summaryOn = _settings.GetBool("summaryOn", false);
            string summaryEndpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            string summaryModel = _settings.Get("summaryModel", "");

            _recording = false;   // flips the tray indicator immediately
            _recorder = null;
            _current = null;

            if (shuttingDown)
            {
                _lastStatus = "Saving (app closing): " + paths.BaseName;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    string wav = recorder.Stop();
                    recorder.Dispose();
                    LogCaptureTroubles(recorder);
                    if (wav == null)
                    {
                        _lastStatus = "Nothing was captured (app closed): " + paths.BaseName;
                        Log("stopped on shutdown: nothing was captured for " + paths.BaseName +
                            " (both scratch tracks were empty), no recording.wav written; transcription skipped");
                        return;
                    }
                    _lastStatus = "Saved, not transcribed (app closed): " + paths.BaseName;
                    Log("stopped on shutdown: audio saved as " + paths.BaseName + " in " +
                        stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms (capture stop " +
                        ((long)recorder.LastCaptureStopTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +
                        " ms, mix " + ((long)recorder.LastMixTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +
                        " ms), transcription skipped");
                }
                catch (Exception ex)
                {
                    _lastStatus = "Stop failed: " + ex.Message;
                    Log("stop on shutdown failed: " + ex.Message);
                }
                return;
            }

            _lastStatus = "Saving: " + paths.BaseName;
            Announce("Recording stopped. Saving and transcribing…");

            // Completed the moment the audio is on disk as recording.wav (or the stop has failed), BEFORE
            // whisper starts: that is the part shutdown waits for. It CARRIES THE OUTCOME -- true for a saved
            // recording, false for a stop that captured nothing, the exception for a stop that failed -- so the
            // flush at exit can say which it saw (R-038); the old source was set true in a finally, and a
            // faulted stop then read as "the save finished".
            var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackPendingSave(saved.Task);
            // remembrance.busy (2.0.0): up NOW, synchronously on this UI thread, when whisper will run on this
            // capture, moved to summarizing for the summary, and cleared by the finally below on every way out.
            BusySpan busy = WhisperConfigured(whisperExe, model) ? Busy(BusyTranscribing) : null;
            Task.Run(async () =>
            {
                try
                {
                    string wav;
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        wav = recorder.Stop();
                        recorder.Dispose();
                    }
                    catch (Exception ex)
                    {
                        saved.TrySetException(ex);
                        throw;
                    }
                    saved.TrySetResult(wav != null);
                    LogCaptureTroubles(recorder);
                    if (wav == null)
                    {
                        // Both scratch tracks were header-only: a stop inside the first buffer period, or a
                        // system source that delivered nothing beside a microphone the driver muted. Nothing to
                        // mix and nothing to transcribe, and until 2026-09-30 this branch could not be reached:
                        // a 46-byte header-only scratch passed the mix's size filter, the mix wrote an empty
                        // recording.wav, and the log said "audio saved as" over it (R-038).
                        _lastStatus = "Nothing was captured: " + paths.BaseName;
                        Log("stopped: nothing was captured for " + paths.BaseName +
                            " (both scratch tracks were empty; capture stop " +
                            ((long)recorder.LastCaptureStopTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +
                            " ms), no recording.wav written");
                        Announce("Recording stopped, but nothing was captured.");
                        return;
                    }
                    Log("stopped: audio saved as " + paths.BaseName + " (capture stop " +
                        ((long)recorder.LastCaptureStopTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +
                        " ms, mix " + ((long)recorder.LastMixTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms)");

                    string audio = wav;
                    stopwatch.Restart();
                    bool did;
                    string transcript = TranscribeWav(audio, paths.Transcript, whisperExe, model,
                        meetingName, attendees, paths.StartedAt, out did);
                    if (did)
                    {
                        // Audio length beside wall time, so the factor in Transcriber.WhisperTimeoutFor can be
                        // read off real runs rather than guessed.
                        Log("transcribed " + paths.BaseName + ": " +
                            ((long)Transcriber.TryReadDuration(audio).TotalSeconds).ToString(CultureInfo.InvariantCulture) +
                            " s of audio in " + ((long)stopwatch.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s");
                    }
                    _lastStatus = (did ? "Transcribed: " : "Saved (Whisper not set up): ") + paths.BaseName;
                    Announce(did ? "Transcript ready." : "Recording saved. Set up Whisper to transcribe it.");

                    // Only worth summarizing a transcript Whisper actually produced: the stub text is setup
                    // instructions, and summarizing those would be nonsense dressed up as a meeting summary.
                    if (did && summaryOn && !string.IsNullOrWhiteSpace(summaryModel))
                    {
                        if (busy == null) busy = Busy(BusySummarizing);
                        else busy.Enter(BusySummarizing);
                        bool wrote = await WriteSummaryAsync(summaryEndpoint, summaryModel,
                            string.IsNullOrWhiteSpace(meetingName) ? paths.BaseName : meetingName,
                            transcript, paths.Summary, busy).ConfigureAwait(false);
                        _lastStatus = (wrote ? "Transcript + summary: " : "Transcript (summary failed): ") + paths.BaseName;
                        if (wrote) Announce("Summary ready.");
                    }
                }
                catch (Exception ex)
                {
                    _lastStatus = "Stop failed: " + ex.Message;
                    Log("stop/transcribe failed: " + ex.Message);
                }
                finally { if (busy != null) busy.Dispose(); }
            });
        }

        private void TakeSnapshot()
        {
            try
            {
                string dir, prefix;
                DateTimeOffset taken = SnapshotClock();
                if (_current != null) { dir = _current.Directory; prefix = _current.SnapshotPrefix; }
                else
                {
                    // NOTHING RECORDING: not the storage root any more (2.0.0, the owner's decision), but that day's
                    // folder by date or the "Snapshots" folder per capture, named as before. Both are purge shapes.
                    var store = new CaptureStore(_settings.Get("storageLocation", CaptureStore.DefaultRoot()), CurrentFolderLayout());
                    dir = store.SnapshotDirectory(taken);
                    System.IO.Directory.CreateDirectory(dir);
                    prefix = "snap";
                }
                string stamp = taken.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
                // A second press inside the same wall-clock second lands BESIDE the first, never over it: the
                // stamp is to the second, the encode takes ~0.3 s, and two consecutive slides half a second
                // apart used to leave one file and two "Snapshot saved." announcements (RA-156). The suffix
                // is a purge shape of its own; see CaptureStore.UniqueSnapshotPath.
                string png = CaptureStore.UniqueSnapshotPath(System.IO.Path.Combine(dir, prefix + " " + stamp + ".png"));
                // OFF THE UI THREAD, ONE AT A TIME. The path is decided here, on the caller's thread, because
                // it reads _current and _settings; the capture and the PNG encode of the whole virtual screen
                // -- a 32bpp bitmap of every monitor, 0.27 s measured at 2560x1440 and scaling with the pixel
                // count -- ran here too, on the UI thread the hotkey and the tray deliver on, so every pet
                // froze and the tray queued for the duration (F177, F181). Neither GDI's BitBlt nor the GDI+
                // PNG encoder needs a message loop, and Announce already marshals the result back. Same
                // shape as AiBrainModule.BeginVramProbe: an Interlocked single-flight gate, Task.Run, the
                // gate released in a finally. A second press while one is encoding is told so rather than
                // queued behind it; the next press starts a fresh one.
                if (Interlocked.CompareExchange(ref _snapshotInFlight, 1, 0) != 0)
                {
                    Announce("Still saving the last snapshot.");
                    return;
                }
                Task.Run(delegate
                {
                    bool ok = false;
                    try { ok = SnapshotCapture(png); }
                    catch (Exception) { ok = false; }
                    finally { Interlocked.Exchange(ref _snapshotInFlight, 0); }
                    Announce(ok ? "Snapshot saved." : "Snapshot failed.");
                });
            }
            catch (Exception ex) { try { _host.Log(Id, "snapshot failed: " + ex.Message); } catch { } }
        }

        /// <summary>The capture behind TakeSnapshot. A seam: the self-test swaps it for a probe that records
        /// the thread it ran on and holds the capture open, to prove the hotkey path returns first.</summary>
        internal static Func<string, bool> SnapshotCapture = ScreenSnapshot.Capture;

        /// <summary>The clock the snapshot name is stamped from. A seam: the self-test pins it to one instant,
        /// so two presses provably fall inside the same second (RA-156).</summary>
        internal static Func<DateTimeOffset> SnapshotClock = () => DateTimeOffset.Now;

        /// <summary>Transcriber.Transcribe's shape, for the seam below.</summary>
        internal delegate string TranscribeFile(string wavPath, string transcriptPath, string whisperExe, string modelPath,
            string meetingName, IReadOnlyList<string> attendees, DateTimeOffset? recordedAt, out bool didTranscribe);

        /// <summary>The transcription behind a delegate: the stop path and "Transcribe a WAV file…" both run whisper-cli
        /// through it, so the self-test can hold one open and watch remembrance.busy without spawning a child. Defaults
        /// to the real Transcriber.</summary>
        internal static TranscribeFile TranscribeWav = Transcriber.Transcribe;

        /// <summary>The Ollama pull behind a delegate, so the self-test can hold one open and watch the
        /// single-flight gate and the token it was handed (RA-159, RA-160). Defaults to the real pull.</summary>
        internal static Func<string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.PullResult>>
            PullModel = OllamaSummarizer.PullModelAsync;

        /// <summary>The /api/tags read behind a delegate, so the suite's pane blocks reach no loopback port at
        /// all rather than one that happens to refuse (RA-164). Defaults to the real read.</summary>
        internal static Func<string, CancellationToken, Task<IReadOnlyList<string>>>
            ListModels = OllamaSummarizer.ListModelsAsync;

        /// <summary>The "is anything answering" probe behind a delegate, so the self-test can press "Download that
        /// model" through the pane's own delegate and still open no socket (BUG-013). Defaults to the real probe.</summary>
        internal static Func<string, CancellationToken, Task<bool>> IsReachable = OllamaSummarizer.IsReachableAsync;

        /// <summary>The summary request behind a delegate: the stop path's summary, "Summarize a transcript…" and the
        /// Summary card's Validate all reach Ollama through it, so the self-test can hold one open and answer it with
        /// no server. Defaults to the real map-reduce.</summary>
        internal static Func<string, string, string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.SummaryResult>>
            Summarize = OllamaSummarizer.SummarizeAsync;

        /// <summary>The two-line meeting the Summary card's Validate asks a summary of: the old "Test the summarizer"
        /// button's, so a model that answered that answers this.</summary>
        private const string ValidationTranscript =
            "Alice: we agreed to ship on Friday. Bob: I will write the release notes.";

        /// <summary>The roots the Transcription card's "Refresh local models" detects under, behind a delegate so the
        /// self-test hands it a scratch root and never walks this machine's own Whisper install. Defaults to the real
        /// roots (the module's own install, then DevToolbox).</summary>
        internal static Func<string, IReadOnlyList<string>> WhisperProbeRoots = WhisperInstaller.ProbeRoots;

        /// <summary>The Transcription card's Validate clip: two seconds at 16 kHz, the length its answer names.</summary>
        internal const int WhisperCheckClipSamples = 2 * 16000;

        /// <summary>
        /// whisper-cli run on a generated clip of the given length (samples at 16 kHz), through
        /// WhisperInstaller.TryVerify, which already proves a pair runs and judges by exit code 0 alone (silence may
        /// legitimately transcribe to nothing), in a scratch folder of its own that it deletes, under its five-minute
        /// cap. Null when the run passed, otherwise what it said. A seam because a self-test never spawns a child (the
        /// pipe-drain invariant's comment in tests/runtime-hardening-selftest.ps1 says why). Defaults to the real check.
        /// </summary>
        internal static Func<string, string, int, string> CheckWhisperRun = delegate(string exe, string model, int clipSamples)
        {
            string detail;
            return WhisperInstaller.TryVerify(exe, model, out detail, clipSamples) ? null : detail;
        };

        /// <summary>
        /// How long "Download that model" waits for the pull's outcome before it answers that the download carries
        /// on in the background (BUG-013). A PaneAction reports once, when it returns, and the pane never redraws the
        /// Status line on its own, so an outcome that arrives after the answer is invisible until the pane is
        /// reopened: the one the owner saw was a pull of an already-installed model that "succeeded" in seconds and
        /// changed nothing on screen. Fifteen seconds covers that case and every refusal (a bad tag, a registry
        /// error), and is short enough that a real multi-gigabyte download is not holding the button. Not
        /// readonly, so the self-test can shorten it.
        /// </summary>
        internal static TimeSpan PullAnswerBound = TimeSpan.FromSeconds(15);

        // Marshal a host call to the UI thread (transcription completes on a background task). The Post
        // itself is inside the try: Control.BeginInvoke throws on a disposed marshalling control, and the
        // one call here that sat outside a try was on the shutdown path, directly before the save (F175).
        private void Announce(string text)
        {
            IHost host = _host;
            if (host == null) return;
            try
            {
                if (_ui != null) _ui.Post(delegate { try { host.SayAll(text); } catch { } }, null);
                else host.SayAll(text);
            }
            catch { }
        }

        /// <summary>Settings writes from a background task go through here, for the same reason host calls
        /// go through <see cref="Announce"/>. _settings is ONE shared instance cached at Init, and
        /// CompanionHost.ModuleSettings is a bare unsynchronised Dictionary whose Save serialises it -- which
        /// the pane's Apply does on the UI thread. An unmarshalled Set from a continuation races that
        /// Serialize, throws "Collection was modified" inside Save, which swallows it and returns false, and
        /// the user is told their edits did not save.</summary>
        private void PersistOnUi(Action write)
        {
            if (write == null) return;
            try
            {
                if (_ui != null) _ui.Post(delegate { try { write(); } catch { } }, null);
                else write();
            }
            catch { }
        }

        // ---- remembrance.busy: this module is running a local model ---------------------------------------

        /// <summary>
        /// The shared-context key this module publishes while it runs a local model, so a module that would load one
        /// of its own (AI Brain, the aibrain-standdown lane) can stand down rather than contend for the GPU. The
        /// contract, identical in that lane's brief (addendum 1):
        ///   while busy: {"phase":"transcribing"|"summarizing"|"validating","at":"&lt;UTC, ISO-8601 round-trip&gt;"}
        ///   cleared by publishing "" (ReadContext's "nothing published" answer).
        /// "at" is when the flag was last republished, not when the work began: it is republished at every phase
        /// change and before every Ollama summary request, so a live value is never older than one whisper run (at
        /// most six hours, Transcriber.MaximumWhisperTimeout) or one summary request, and a reader treats one older
        /// than eight hours as stale. Busy covers whisper-cli on the stop path, in "Transcribe a WAV file…" and in
        /// the Transcription Validate, and the Ollama summary on the stop path, in "Summarize a transcript…" and in
        /// the Summary Validate. Not recording, and not a pull: a download loads nothing.
        /// </summary>
        internal const string BusyContextKey = "remembrance.busy";
        internal const string BusyTranscribing = "transcribing";
        internal const string BusySummarizing = "summarizing";
        internal const string BusyValidating = "validating";

        private readonly object _busySync = new object();
        // Every span still open, oldest first; the newest names the phase. A list, not a bool: overlapping spans (a
        // Validate pressed while a recording transcribes) keep the flag up until the last one ends.
        private readonly List<BusySpan> _busySpans = new List<BusySpan>();
        // When the flag was last asked to republish: the "at" it carries, set at the transition itself, so a
        // publish posted to the UI thread says when the change happened rather than when the post ran.
        private DateTime _busyAt;
        // What was last handed to the host, so a clear goes out only over something that was set.
        private string _busyPublished = "";
        // Set by Shutdown and by the host's shutdown, which clear the flag themselves: nothing publishes after it.
        private bool _busyClosed;
        // The thread Init ran on, the UI thread. A transition there publishes synchronously, so ContextChanged is
        // raised on the thread the contract documents (CompanionHost raises it on the publisher's own thread).
        private int _uiThreadId;

        /// <summary>One span of local-model work. Dispose ends it, once; Enter moves it to another phase and Refresh
        /// republishes it with a fresh "at". A using block, or a finally, gives the clear on every path the contract
        /// asks for: success, failure and cancellation alike.</summary>
        private sealed class BusySpan : IDisposable
        {
            private readonly RemembranceModule _owner;
            private int _ended;
            internal string Phase;
            internal BusySpan(RemembranceModule owner, string phase) { _owner = owner; Phase = phase; }
            internal void Enter(string phase) { _owner.ChangeBusy(this, phase); }
            internal void Refresh() { _owner.ChangeBusy(this, null); }
            public void Dispose() { if (Interlocked.Exchange(ref _ended, 1) == 0) _owner.EndBusy(this); }
        }

        private BusySpan Busy(string phase)
        {
            var span = new BusySpan(this, phase);
            lock (_busySync)
            {
                _busySpans.Add(span);
                _busyAt = DateTime.UtcNow;
            }
            PublishBusy();
            return span;
        }

        private void ChangeBusy(BusySpan span, string phase)
        {
            lock (_busySync)
            {
                if (phase != null) span.Phase = phase;
                _busyAt = DateTime.UtcNow;
            }
            PublishBusy();
        }

        private void EndBusy(BusySpan span)
        {
            lock (_busySync)
            {
                _busySpans.Remove(span);
                _busyAt = DateTime.UtcNow;
            }
            PublishBusy();
        }

        /// <summary>
        /// Publish the flag as it stands. On the UI thread (the stop, a pane action's press) synchronously; anywhere
        /// else posted the Announce/PersistOnUi way, and the posted delegate reads the state when it RUNS, so a late
        /// post can never put back a phase that has already ended. Once the flag is closed nothing is published:
        /// PublishBusyNow checks, and the shutdown path itself never posts (CloseBusy).
        /// </summary>
        private void PublishBusy()
        {
            SynchronizationContext ui = _ui;
            if (ui == null || Environment.CurrentManagedThreadId == _uiThreadId) { PublishBusyNow(); return; }
            try { ui.Post(delegate { PublishBusyNow(); }, null); } catch { }
        }

        private void PublishBusyNow()
        {
            IHost host = _host;
            if (host == null) return;
            lock (_busySync)
            {
                if (_busyClosed) return;
                string value = _busySpans.Count == 0 ? "" : BusyValue(_busySpans[_busySpans.Count - 1].Phase, _busyAt);
                if (string.Equals(value, _busyPublished, StringComparison.Ordinal)) return;
                _busyPublished = value;
                try { host.PublishContext(Id, BusyContextKey, value); } catch { }
            }
        }

        /// <summary>The flag's value: {"phase":"...","at":"..."} with "at" in UTC, ISO-8601 round-trip ("o"), which ends
        /// in "Z" and so carries nothing JSON would escape.</summary>
        internal static string BusyValue(string phase, DateTime at)
        {
            return "{\"phase\":\"" + phase + "\",\"at\":\"" +
                   at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\"}";
        }

        /// <summary>Shutdown and the host's shutdown: clear the flag NOW, on this thread and never by a post (the F175
        /// check counts every post made while the host shuts down), before _host goes, and close it so nothing
        /// publishes after, whatever span is still open.</summary>
        private void CloseBusy()
        {
            IHost host = _host;
            lock (_busySync)
            {
                if (_busyClosed) return;
                _busyClosed = true;
                if (host == null || _busyPublished.Length == 0) return;
                _busyPublished = "";
                try { host.PublishContext(Id, BusyContextKey, ""); } catch { }
            }
        }

        /// <summary>Will whisper-cli run for these two paths? The same test StatusLine shows as "configured"; a
        /// transcription that only writes the setup stub runs no model and raises no flag.</summary>
        private static bool WhisperConfigured(string exe, string model)
        {
            return !string.IsNullOrWhiteSpace(exe) && System.IO.File.Exists(exe)
                && !string.IsNullOrWhiteSpace(model) && System.IO.File.Exists(model);
        }

        /// <summary>
        /// The one Save() this module's actions call, and the one place a false from it becomes words.
        ///
        /// Save() reports a failed write -- a locked or read-only settings.json, a full disk, the Apply race
        /// PersistOnUi describes -- by returning false, never by throwing: the host's store swallows the I/O
        /// exception, and so does every other IModuleSettings in the repo. Seven Save() calls in six user-
        /// triggered actions discarded that bool and answered with a tick, so a user who had waited out a
        /// multi-hundred-megabyte Whisper download read "✓ Whisper is ready" over paths that were gone at the next
        /// launch, and only the pane's own Apply reported a failed write (RA-157; the same class F110 fixed in
        /// BlinkingLed). The values are still SET on the shared handle, so the action works for this session --
        /// which is what the sentence says, rather than claiming the value was lost or that it was saved.
        /// </summary>
        private bool TrySaveSettings(string what, out string notPersisted)
        {
            notPersisted = null;
            bool ok;
            try { ok = _settings.Save(); }
            catch { ok = false; }   // a third-party host that throws; every shipped store returns false
            if (ok) return true;
            Log(what + " could not be written to the settings file; it applies to this session only");
            notPersisted = "⚠ " + what + " applies to this session only: the settings file could not be written." +
                           " Check that it is not read-only or locked, then press again.";
            return false;
        }

        /// <summary>The purge timer's tick: the first one a minute after Init, every later one an hour apart.
        /// Setting Interval on a running WinForms timer restarts it, so the switch happens on the first tick
        /// and costs nothing after.</summary>
        private void OnPurgeTick()
        {
            System.Windows.Forms.Timer timer = _purgeTimer;
            if (timer != null && timer.Interval != PurgeIntervalMs) timer.Interval = PurgeIntervalMs;
            RunPurge();
        }

        private void RunPurge()
        {
            Interlocked.Increment(ref _purgesStarted);
            string root = _settings.Get("storageLocation", CaptureStore.DefaultRoot());
            string layout = CurrentFolderLayout();   // the purge walks every folder shape whatever the layout is now
            Task.Run(() => { try { new CaptureStore(root, layout).Purge(); } catch { } });
        }

        /// <summary>The storage layout new captures use: the stored choice, or what the old "Create a folder per
        /// capture" checkbox meant when there is none (FolderLayout.Migrate, read at use; 2.0.0).</summary>
        private string CurrentFolderLayout()
        {
            return FolderLayout.Migrate(_settings.Get(FolderLayout.SettingKey, ""),
                _settings.GetBool(FolderLayout.LegacySettingKey, true));
        }

        // ---- seams for the self-test ------------------------------------------------------------------
        // Private entry points reached by name, so the module's own start, stop, snapshot and purge paths can
        // be driven headless. Each is a one-line forwarder or a read and adds no behaviour.
        internal int PurgesStartedForSelfTest { get { return _purgesStarted; } }
        internal int PurgeIntervalForSelfTest { get { return _purgeTimer != null ? _purgeTimer.Interval : 0; } }
        internal void PurgeTickForSelfTest() { OnPurgeTick(); }
        internal void StartRecordingForSelfTest() { StartRecording(); }
        internal void StopRecordingForSelfTest() { StopRecording(false); }
        internal void TakeSnapshotForSelfTest() { TakeSnapshot(); }
        internal bool IsRecordingForSelfTest { get { return _recording; } }
        internal Task[] PendingSavesForSelfTest { get { lock (_pendingSaves) return _pendingSaves.ToArray(); } }
        internal string StartRecommendedPullForSelfTest(string endpoint, string id) { return StartRecommendedPull(endpoint, id); }
        internal string AdoptWhisperPathsForSelfTest(string exe, string model, string success) { return AdoptWhisperPaths(exe, model, success); }
        internal bool PullInFlightForSelfTest { get { return Interlocked.CompareExchange(ref _pullInFlight, 0, 0) != 0; } }

        // --- options ---------------------------------------------------------------------------------

        // The three dropdowns whose CONTENTS are discovered rather than declared. Held because the
        // schema is built once, in Init, and a closed dropdown can only offer what its Options array
        // held at that moment -- see RefreshDynamicOptions for what that cost.
        private SettingField _sysDeviceField;
        private SettingField _micDeviceField;
        private SettingField _summaryModelField;

        /// <summary>
        /// Re-discover what the three dynamic dropdowns should offer. Called from Load, which the host
        /// re-runs on every pane build and always BEFORE it reads Schema -- the documented seam for
        /// exactly this, and the one the core Preferences pane already uses to fill its "who speaks"
        /// list from the pets on screen.
        ///
        /// Without it the Options array is whatever Init saw and can never change again, which broke
        /// both dropdowns in the same way. The refresh button ("Find local summary models" then, "Refresh
        /// local models" since 2.0.0) would discover the models, save
        /// one, ask for a reload -- and the rebuilt dropdown still offered only the empty string it was
        /// born with, so it rendered BLANK, and because a closed dropdown with no match reads back as
        /// "", the next Apply wrote that blank over the model that had just been found. A recording
        /// device plugged in after startup was invisible for the life of the process for the same
        /// reason.
        /// </summary>
        // Whether this session has already gone looking for an existing Whisper. One probe per run:
        // finding nothing costs two Directory.Exists calls (neither probe root exists on a box with
        // no Whisper), but finding something walks the install tree, and a pane open must stay cheap.
        private bool _whisperProbed;

        /// <summary>
        /// Fill in the two Whisper paths from an install that is already here, once, the first time
        /// the pane is opened with nothing configured.
        ///
        /// Reported as "shouldn't the whisper-cli path browse or auto-populate": two empty text boxes
        /// asking for the absolute path of a C++ binary is the least discoverable thing in this
        /// module, and a box provisioned by scripts-utilities already HAS the binary. The same probe
        /// the buttons run is free when there is nothing to find, so there is no reason to make
        /// someone click for it.
        ///
        /// It writes, which a Load normally must not. Bounded deliberately: only when the setting is
        /// empty, only with paths that exist, and only once per run -- so it can fill a blank in, and
        /// can never overwrite a path the user chose.
        /// </summary>
        private void AutoDetectWhisperOnce()
        {
            if (_whisperProbed) return;
            _whisperProbed = true;
            if (!string.IsNullOrWhiteSpace(_settings.Get("whisperExe", ""))) return;
            try
            {
                string exe, model;
                if (!WhisperInstaller.TryDetect(DataDirectory(), out exe, out model)) return;
                _settings.Set("whisperExe", exe ?? "");
                _settings.Set("whisperModel", model ?? "");
                string notPersisted;
                _lastStatus = TrySaveSettings("the detected Whisper install", out notPersisted) ? "Whisper found." : notPersisted;
            }
            catch (Exception) { }
        }

        /// <summary>Shown when discovery has found nothing. A closed dropdown must offer SOMETHING,
        /// and an empty row reads as a broken control rather than as "no models here".</summary>
        internal const string NoModelsPlaceholder = "(none found - is Ollama running?)";

        private bool _modelsProbed;

        /// <summary>
        /// Ask the local Ollama what it has, once, the first time the pane is opened with nothing
        /// cached.
        ///
        /// Reported as "summary model still shows nothing in the dropdown", and the report was fair:
        /// the list was only ever filled by the "Find local summary models" button (now "Refresh local
        /// models"), so until someone
        /// guessed that a button was a PREREQUISITE rather than a refresh, the control was an empty
        /// box next to a ticked-looking feature. The same argument as the Whisper paths: the probe is
        /// free, so there is no reason to make anyone click for it.
        ///
        /// OFF THE UI THREAD, AND NOT WAITED ON. This used to say "Bounded and off the UI thread",
        /// which was half true and the dangerous half: Task.Run moved the HTTP call, and then
        /// `probe.Wait(3s)` blocked the UI thread anyway, inside Load, during a pane build.
        ///
        /// The old justification was "a loopback answer is milliseconds and a refused connection is
        /// immediate, so the cap only ever bites on something genuinely wrong." Measured 2026-09-27
        /// with the same mechanism (HttpClient, literal 127.0.0.1, so no DNS): server running 5-56 ms,
        /// server REFUSED 2005-2008 ms, host unreachable 2010 ms. A refused localhost connection does
        /// NOT fail fast -- it burns the whole deadline. So the 3 seconds were not the rare
        /// hung-server case; they were what every user without Ollama paid on first opening this pane.
        /// And the gate below is on the models cache only, not on summaryOn, so users who never turned
        /// the summary feature on paid it too.
        ///
        /// Fire and forget now, same shape as AiBrainModule.BeginVramProbe: the answer lands in
        /// settings and the NEXT pane build shows it, which is exactly what happened before whenever
        /// the 3s cap expired -- minus the freeze.
        /// </summary>
        private void AutoDiscoverModelsOnce()
        {
            if (_modelsProbed) return;
            _modelsProbed = true;
            if (!string.IsNullOrWhiteSpace(_settings.Get("summaryModelsCache", ""))) return;
            string endpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            try
            {
                Task.Run(async delegate
                {
                    try
                    {
                        IReadOnlyList<string> models =
                            await ListModels(endpoint, CancellationToken.None).ConfigureAwait(false);
                        if (models == null || models.Count == 0) return;
                        // PersistOnUi, not a bare Set: _settings is touched from the UI thread by the
                        // pane, and this continuation is on a pool thread. The module already routes
                        // background settings writes this way for the "Collection was modified"
                        // reason recorded on that helper.
                        string joined = string.Join("|", models);
                        PersistOnUi(delegate
                        {
                            SetInBackground("summaryModelsCache", joined);   // lands after Load: see BUG-013
                            string notPersisted;
                            TrySaveSettings("the discovered model list", out notPersisted);
                        });
                    }
                    catch (Exception) { }
                });
            }
            catch (Exception) { }
        }

        /// <summary>
        /// What the summary dropdown should be SHOWING: the saved model when it is still installed,
        /// otherwise the recommended one if it is here, otherwise the first that is.
        ///
        /// Preselecting matters because the alternative is a populated dropdown sitting on no
        /// selection, which looks exactly as broken as the empty one did and saves "" on the next
        /// Apply.
        /// </summary>
        private string SummaryModelValue()
        {
            string[] options = _summaryModelField != null ? _summaryModelField.Options : null;
            if (options == null || options.Length == 0) return "";

            string saved = _settings.Get("summaryModel", "").Trim();
            foreach (string o in options)
                if (string.Equals(o, saved, StringComparison.Ordinal) && saved.Length > 0) return o;

            string recommended = OllamaSummarizer.RecommendedIdFromDisplay(
                _settings.Get("recommendedModel", OllamaSummarizer.DefaultRecommendedId));
            foreach (string o in options)
                if (string.Equals(o, recommended, StringComparison.OrdinalIgnoreCase)) return o;
            foreach (string o in options)
                if (o.Length > 0 && o != NoModelsPlaceholder) return o;
            return options[0];
        }

        private void RefreshDynamicOptions()
        {
            // DROP THE DEVICE SNAPSHOT FIRST. AudioDevices caches for 1500 ms so that the four reads
            // in one pane build share a single WASAPI enumeration; this makes each pane OPEN start
            // from a fresh one, which is what a user who just plugged a headset in expects.
            //
            // It also gives ForgetCachedDevices a caller. It had none, while the backlog entry that
            // closed the device-caching work cited it as the escape hatch -- "the window is short on
            // purpose and ForgetCachedDevices() drops it explicitly" -- so a safety property the
            // record claimed was not true of the shipped code. Wiring it here costs nothing: the
            // collapse from four enumerations to one happens within the build, after this line.
            AudioDevices.ForgetCachedDevices();

            if (_sysDeviceField != null)
                _sysDeviceField.Options = DeviceOptions(AudioDevices.RenderDevices(), _settings.Get("sysDevice", ""));
            if (_micDeviceField != null)
                _micDeviceField.Options = DeviceOptions(AudioDevices.CaptureDevices(), _settings.Get("micDevice", ""));
            if (_summaryModelField != null)
                _summaryModelField.Options = SummaryModelOptions();
        }

        /// <summary>
        /// The device names to offer, with a SAVED name that is not currently present kept in the list.
        ///
        /// That union is the same rule SummaryModelOptions relies on and it is load-bearing for the
        /// same reason: the host renders a closed dropdown, so a saved device missing from the options
        /// renders blank and is then written back as blank by the next Apply. Unplugging a headset
        /// would quietly reset the choice rather than waiting for it to come back.
        /// </summary>
        internal static string[] DeviceOptions(IEnumerable<AudioDevice> devices, string saved)
        {
            var names = new List<string>();
            if (devices != null)
                foreach (AudioDevice d in devices)
                    if (d != null && !string.IsNullOrEmpty(d.Name) && !names.Contains(d.Name))
                        names.Add(d.Name);
            string kept = (saved ?? "").Trim();
            if (kept.Length > 0 && !names.Any(n => string.Equals(n, kept, StringComparison.Ordinal)))
                names.Add(kept);
            if (names.Count == 0) names.Add("");   // an empty closed dropdown cannot be rendered
            return names.ToArray();
        }

        /// <summary>The saved device name, or the first option when nothing is saved. Never "": that
        /// matches no option, so the box would open blank on a fresh install.</summary>
        private static string DeviceValue(string saved, string[] options)
        {
            string value = (saved ?? "").Trim();
            if (value.Length > 0 && options != null &&
                options.Any(o => string.Equals(o, value, StringComparison.Ordinal)))
                return value;
            return (options != null && options.Length > 0) ? options[0] : "";
        }

        // ---- what the pane shows, and what Save stores (BUG-013) -------------------------------------------

        /// <summary>
        /// The pane's writable fields. Save and every action's on-screen read map a value through
        /// <see cref="StoredFormOf"/> for these ids, inside <see cref="ApplyPaneValues"/>, so the two cannot read one
        /// label two ways.
        /// </summary>
        private static readonly string[] PaneFieldIds =
        {
            "sysEnabled", "sysDevice", "micEnabled", "micDevice", "recordHotkey", "snapshotHotkey",
            "storageLocation", FolderLayout.SettingKey, "whisperExe", "whisperModel", "whisperModelChoice",
            "summaryOn", "ollamaEndpoint", "summaryModel", "recommendedModel",
        };

        /// <summary>
        /// What Save stores for <paramref name="id"/> when the pane shows <paramref name="shown"/>, or null when it
        /// stores nothing for that value (a checkbox value that does not parse). The dropdowns that show a label map
        /// it to the id behind it here and only here: the Whisper model's "base.en (~142 MB, recommended)" to its
        /// file id, the recommended model's "gemma4:12b (7.0 GB) -- recommended" to its tag, and the no-models
        /// placeholder to "", since storing that sentence would send it to /api/generate as a tag.
        /// </summary>
        private static string StoredFormOf(string id, string shown)
        {
            string value = (shown ?? "").Trim();
            switch (id)
            {
                case "sysEnabled":
                case "micEnabled":
                case "summaryOn":
                    bool flag;
                    return bool.TryParse(value, out flag) ? (flag ? "true" : "false") : null;
                // The Radio row hands back the option's TEXT; the stable id is what is stored, and text that is no
                // option (the "" an unmatched row collects) stores nothing (2.0.0).
                case FolderLayout.SettingKey:
                    return FolderLayout.FromDisplay(value);
                case "whisperModelChoice":
                    return ModelIdFromDisplay(value);
                case "summaryModel":
                    return value == NoModelsPlaceholder ? "" : value;
                case "recommendedModel":
                    return OllamaSummarizer.RecommendedIdFromDisplay(value);
                default:
                    return value;
            }
        }

        /// <summary>
        /// The one place pane values become stored values, in AI Brain's ApplyPaneValues shape: Save applies them to
        /// the live settings, and every action that reads a setting the pane edits applies them to a detached copy
        /// (<see cref="OnScreenSettings"/>). Each field the pane handed over is set in its stored form, except a field
        /// named in <paramref name="keep"/>.
        /// </summary>
        private static void ApplyPaneValues(IModuleSettings target, IReadOnlyDictionary<string, string> values,
            ICollection<string> keep)
        {
            if (target == null || values == null) return;
            foreach (string id in PaneFieldIds)
            {
                string shown;
                if (!values.TryGetValue(id, out shown)) continue;
                if (keep != null && keep.Contains(id)) continue;
                string stored = StoredFormOf(id, shown);
                if (stored != null) target.Set(id, stored);
            }
        }

        /// <summary>
        /// The settings as the pane SHOWS them: a detached copy of the saved values, with what is on screen applied by
        /// the same function Save uses (AI Brain's PendingSettings shape). Every action that reads a setting the pane
        /// edits reads it from here, with the default that read always had.
        ///
        /// BUG-013. Those actions used to read the SAVED settings, because a plain InvokeAsync is handed nothing and
        /// the host applies no pending edit before an action runs. So "Download that model" fetched whatever was last
        /// applied -- gemma4:12b by default -- while the dropdown beside it named another model; that model is often
        /// installed already, so the "download" succeeded in seconds and nothing visible changed. Each of them is now
        /// registered with InvokeWithPendingAsync and reads through this copy. The copy is never saved: an action
        /// writes its own result (a selection, an adopted path) to the live settings itself, and an edit the user has
        /// not applied stays unapplied until Apply.
        /// </summary>
        private IModuleSettings OnScreenSettings(IReadOnlyDictionary<string, string> pending)
        {
            var copy = new ModuleKit.MemoryModuleSettings();
            foreach (string id in PaneFieldIds)
            {
                string saved = _settings.Get(id, null);
                if (saved != null) copy.Set(id, saved);
            }
            ApplyPaneValues(copy, pending, null);
            return copy;
        }

        // Settings this module wrote OUTSIDE the pane since the pane last loaded, key to value: the pull's selection and
        // model list, the first-open model discovery (BUG-013). Save keeps each against an Apply that left it alone, and
        // Load clears it. Written from PersistOnUi delegates, so on the UI thread, and locked for the case with no UI
        // context, where PersistOnUi writes inline on a pool thread.
        private readonly Dictionary<string, string> _writtenSinceLoad = new Dictionary<string, string>(StringComparer.Ordinal);
        // What the pane's last Load showed, by field id: how Save tells a field the user left alone from one they edited.
        private IReadOnlyDictionary<string, string> _loadedValues;

        /// <summary>A settings write this module makes OUTSIDE the pane: set, and recorded, so the pane's next Apply
        /// keeps it when the user did not touch that field (BUG-013). PersistOnUi delegates call it, nothing else.</summary>
        private void SetInBackground(string key, string value)
        {
            _settings.Set(key, value);
            lock (_writtenSinceLoad) _writtenSinceLoad[key] = value ?? "";
        }

        /// <summary>
        /// The fields Save leaves alone: each one this module wrote in the background since the pane loaded whose value
        /// on screen is still what Load showed, after the same mapping. That is BUG-013's third part: the pull's
        /// selection lands on summaryModel while the pane still shows the old model, and an Apply of the untouched old
        /// value used to put it back over the download. A field the user did change is written as always, so their edit
        /// wins.
        /// </summary>
        private HashSet<string> BackgroundWritesToKeep(IReadOnlyDictionary<string, string> values)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyDictionary<string, string> loaded = _loadedValues;
            if (values == null || loaded == null) return keep;
            lock (_writtenSinceLoad)
            {
                foreach (string id in _writtenSinceLoad.Keys)
                {
                    string onScreen, shown;
                    if (!values.TryGetValue(id, out onScreen) || !loaded.TryGetValue(id, out shown)) continue;
                    if (string.Equals(StoredFormOf(id, onScreen), StoredFormOf(id, shown), StringComparison.Ordinal))
                        keep.Add(id);
                }
            }
            return keep;
        }

        private SettingField[] BuildOptionsPane_Schema()
        {
            // NO WASAPI ENUMERATION HERE. This ran two MMDeviceEnumerator walks -- FriendlyName off every
            // endpoint's property store -- on the UI thread inside Init, during the StartUp constructor,
            // before the first pet appeared, and their result was never shown: the host calls Load before it
            // reads Schema on every pane build, and Load's RefreshDynamicOptions drops the device cache and
            // enumerates afresh (F178). The saved name is placeholder enough for a field whose Options are
            // always rebuilt before anyone sees them; the self-test pins both halves.
            string[] renderNames = DeviceOptions(null, _settings.Get("sysDevice", ""));
            string[] micNames = DeviceOptions(null, _settings.Get("micDevice", ""));
            _sysDeviceField = new SettingField { Id = "sysDevice", Label = "System output device", Kind = SettingKind.Enum, Options = renderNames, Group = "Sources" };
            _micDeviceField = new SettingField { Id = "micDevice", Label = "Microphone device", Kind = SettingKind.Enum, Options = micNames, Group = "Sources" };
            _summaryModelField = new SettingField { Id = "summaryModel", Label = "Summary model", Kind = SettingKind.Enum,
                Options = SummaryModelOptions(), Group = "Summary (local AI)" };
            return new[]
            {
                new SettingField { Id = "sysEnabled", Label = "Record system audio (what you hear)", Kind = SettingKind.Bool, Group = "Sources" },
                _sysDeviceField,
                new SettingField { Id = "micEnabled", Label = "Record microphone", Kind = SettingKind.Bool, Group = "Sources" },
                _micDeviceField,

                new SettingField { Id = "recordHotkey", Label = "Start/stop hotkey (e.g. Ctrl+Alt+R)", Kind = SettingKind.Text, Group = "Hotkeys" },
                new SettingField { Id = "snapshotHotkey", Label = "Snapshot hotkey (e.g. Ctrl+Alt+S)", Kind = SettingKind.Text, Group = "Hotkeys" },

                new SettingField { Id = "storageLocation", Label = "Where recordings are stored (blank = Documents\\Remembrance)", Kind = SettingKind.Text, Group = "Storage" },
                // Two options in the owner's words, stored as "capture" / "date" under folderLayout (2.0.0). It was a
                // "Create a folder per capture" checkbox whose OFF state filed captures flat in the root; that key is
                // still read (FolderLayout.Migrate) and never written, so an install that has not chosen keeps its
                // layout's nearest equivalent and settings.json keeps what was there.
                new SettingField { Id = FolderLayout.SettingKey, Label = "Folders for new captures", Kind = SettingKind.Radio,
                    Options = FolderLayout.Displays(), Group = "Storage" },

                new SettingField { Id = "whisperExe", Label = "whisper-cli path (filled in for you if one is found)", Kind = SettingKind.Text, Group = "Transcription" },
                new SettingField { Id = "whisperModel", Label = "Whisper model file (e.g. ggml-base.en.bin)", Kind = SettingKind.Text, Group = "Transcription" },
                new SettingField { Id = "whisperModelChoice", Label = "Model to download, used by \"Set up Whisper for me\" below", Kind = SettingKind.Enum,
                    Options = WhisperInstaller.Models.Select(m => m.Display).ToArray(), Group = "Transcription" },

                // Off by default: it is an extra dependency (a local Ollama) and an extra pass over the
                // recording, so it should be a choice rather than a surprise.
                new SettingField { Id = "summaryOn", Label = "Also write an AI summary next to the transcript", Kind = SettingKind.Bool, Group = "Summary (local AI)" },
                new SettingField { Id = "ollamaEndpoint", Label = "Local Ollama address", Kind = SettingKind.Text, Group = "Summary (local AI)" },
                _summaryModelField,
                new SettingField { Id = "recommendedModel", Label = "Model to download if you have none",
                    Kind = SettingKind.Enum, Options = OllamaSummarizer.RecommendedDisplays(),
                    Group = "Summary (local AI)" },

                new SettingField { Id = "status", Label = "Status", Kind = SettingKind.Info, Group = "Status" },
            };
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Remembrance",
                Schema = BuildOptionsPane_Schema(),
                Actions = new[]
                {
                    new PaneAction { Label = "Browse for a storage folder…", Group = "Storage", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFolder("storageLocation")) },
                    // Listed before the Browse actions on purpose: these two are what a tester should try
                    // first, and typing two paths by hand is the fallback rather than the expected route.
                    //
                    // InvokeWithPendingAsync, ALONE, on every action that reads a setting this pane edits (BUG-013):
                    // the host hands it the values on screen, unapplied edits included, and OnScreen maps them as Save
                    // would. No InvokeAsync beside it: MinHostVersion is 1.2.5, so no host that loads this module
                    // would call the saved-values delegate, and a second entry point is a second thing to keep right.
                    new PaneAction { Label = "Set up Whisper for me…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeWithPendingAsync = SetUpWhisperAsync },
                    // "Refresh local models" keeps the pair on screen when both files exist and detects only to fill in
                    // a missing one; Validate, right after it, proves the pair on screen runs (2.0.0).
                    new PaneAction { Label = "Refresh local models", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeWithPendingAsync = pending => Task.FromResult(RefreshWhisper(pending)) },
                    new PaneAction { Label = "Validate", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeWithPendingAsync = ValidateWhisperAsync },
                    // Between the automatic route and the manual one, because that is the order
                    // a stuck user needs: it failed, here are the files, now point at them.
                    new PaneAction { Label = "Open the download pages…", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeWithPendingAsync = pending => Task.FromResult(OpenWhisperDownloads(pending)) },
                    new PaneAction { Label = "Browse for whisper-cli…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperExe", "whisper-cli", new[] { "exe" })) },
                    new PaneAction { Label = "Browse for a model…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperModel", "Whisper model", new[] { "bin" })) },
                    new PaneAction { Label = "Transcribe a WAV file…", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeWithPendingAsync = pending => Task.FromResult(TranscribeExisting(pending)) },

                    // "Refresh local models", the name of AI Brain's button that does the same job; the Transcription
                    // card has a Refresh and a Validate of the same names, so a reader learns the pair once (2.0.0).
                    new PaneAction { Label = "Refresh local models", Group = "Summary (local AI)", ReloadPaneAfter = true,
                        InvokeWithPendingAsync = RefreshSummaryModelsAsync },
                    // ReloadPaneAfter, so a download that finished inside PullAnswerBound shows its selection in the
                    // Summary model dropdown at once rather than on the next open (BUG-013).
                    new PaneAction { Label = "Download that model", Group = "Summary (local AI)", ReloadPaneAfter = true,
                        InvokeWithPendingAsync = DownloadRecommendedModelAsync },
                    new PaneAction { Label = "Get Ollama (opens the site)", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(OpenOllamaSite()) },
                    new PaneAction { Label = "Validate", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeWithPendingAsync = ValidateSummaryAsync },
                    new PaneAction { Label = "Summarize a transcript…", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeWithPendingAsync = pending => Task.FromResult(SummarizeExisting(pending)) },
                },
                // RefreshDynamicOptions runs HERE, not in the initialiser below, because Load is the
                // only thing the host promises to call before it reads Schema on every build.
                Load = () =>
                {
                    // What this build shows already includes every background write so far (BUG-013).
                    lock (_writtenSinceLoad) _writtenSinceLoad.Clear();
                    AutoDetectWhisperOnce();
                    AutoDiscoverModelsOnce();
                    RefreshDynamicOptions();
                    var shown = new Dictionary<string, string>
                {
                    ["sysEnabled"] = _settings.GetBool("sysEnabled", true) ? "true" : "false",
                    ["sysDevice"] = DeviceValue(_settings.Get("sysDevice", ""), _sysDeviceField.Options),
                    ["micEnabled"] = _settings.GetBool("micEnabled", true) ? "true" : "false",
                    ["micDevice"] = DeviceValue(_settings.Get("micDevice", ""), _micDeviceField.Options),
                    ["recordHotkey"] = _settings.Get("recordHotkey", DefaultRecordHotkey),
                    ["snapshotHotkey"] = _settings.Get("snapshotHotkey", DefaultSnapshotHotkey),
                    ["storageLocation"] = _settings.Get("storageLocation", ""),
                    [FolderLayout.SettingKey] = FolderLayout.ToDisplay(CurrentFolderLayout()),
                    ["whisperExe"] = _settings.Get("whisperExe", ""),
                    ["whisperModel"] = _settings.Get("whisperModel", ""),
                    ["whisperModelChoice"] = ModelChoiceDisplay(),
                    ["summaryOn"] = _settings.GetBool("summaryOn", false) ? "true" : "false",
                    ["ollamaEndpoint"] = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint),
                    ["summaryModel"] = SummaryModelValue(),
                    ["recommendedModel"] = OllamaSummarizer.RecommendedDisplayFor(
                        _settings.Get("recommendedModel", OllamaSummarizer.DefaultRecommendedId)),
                    ["status"] = StatusLine(),
                    };
                    _loadedValues = shown;
                    return shown;
                },
                Save = values =>
                {
                    // Every field is written as it always was, through the one mapping the actions share, except a
                    // field this module wrote in the background since the pane loaded that the user left alone
                    // (BUG-013; BackgroundWritesToKeep says which and why). NOT a diff of every field against what
                    // Load showed, deliberately: the pane shows DERIVED values -- the summary dropdown preselects a
                    // model when none is saved (1.0.5), the device rows show the default entry -- an untouched Apply
                    // has always persisted them, and the stop path reads only the saved summaryModel, so skipping
                    // every untouched field would leave the pane naming a model no summary uses. The register's
                    // feature/remembrance-2 entry records the choice.
                    ApplyPaneValues(_settings, values, BackgroundWritesToKeep(values));
                    bool ok = _settings.Save();
                    RegisterHotkeys();   // a changed combo takes effect without a restart
                    return ok;
                },
            };
        }

        private string StatusLine()
        {
            string root = _settings.Get("storageLocation", "");
            if (string.IsNullOrWhiteSpace(root)) root = CaptureStore.DefaultRoot();
            bool whisper = System.IO.File.Exists(_settings.Get("whisperExe", "")) && System.IO.File.Exists(_settings.Get("whisperModel", ""));
            int outs = Math.Max(0, AudioDevices.RenderDevices().Count - 1);   // minus the "System default" entry
            int mics = Math.Max(0, AudioDevices.CaptureDevices().Count - 1);
            string summary;
            if (!_settings.GetBool("summaryOn", false)) summary = "off";
            else if (string.IsNullOrWhiteSpace(_settings.Get("summaryModel", ""))) summary = "on but no model picked";
            else summary = "on (" + _settings.Get("summaryModel", "") + ")";

            string s = _lastStatus + "  |  devices: " + outs + " output, " + mics + " mic"
                + "  |  storage: " + root
                + "  |  Whisper: " + (whisper ? "configured" : "not set up — use \"Set up Whisper for me…\"")
                + "  |  summary: " + summary;
            if (System.Windows.Forms.SystemInformation.TerminalServerSession)
                s += "  |  ⚠ Remote Desktop session: the machine's real mic and speakers are not presented here, so recording won't work. Run on the machine's own console. (The device lists refresh every time this pane opens, so reopening it on the console is enough; no restart needed.)";
            else if (mics == 0)
                s += "  |  ⚠ no microphone detected.";
            return s;
        }

        private string BrowseFolder(string settingKey)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Choose where recordings are stored";
                    string cur = _settings.Get(settingKey, "");
                    if (!string.IsNullOrWhiteSpace(cur)) { try { dlg.SelectedPath = cur; } catch { } }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return "Unchanged.";
                    _settings.Set(settingKey, dlg.SelectedPath);
                    string notPersisted;
                    if (!TrySaveSettings("the storage folder", out notPersisted)) return notPersisted;
                    return "✓ storage: " + dlg.SelectedPath;
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        private string BrowseFile(string settingKey, string label, string[] extensions)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "Choose " + label;
                    dlg.CheckFileExists = true;
                    string filter = string.Join(";", extensions.Select(e => "*." + e));
                    dlg.Filter = label + " (" + filter + ")|" + filter + "|All files (*.*)|*.*";
                    string cur = _settings.Get(settingKey, "");
                    if (!string.IsNullOrWhiteSpace(cur)) { try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(cur); dlg.FileName = System.IO.Path.GetFileName(cur); } catch { } }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return "Unchanged.";
                    _settings.Set(settingKey, (dlg.FileName ?? "").Trim());
                    string notPersisted;
                    if (!TrySaveSettings("the " + label + " path", out notPersisted)) return notPersisted;
                    return "✓ " + label + ": " + System.IO.Path.GetFileName(dlg.FileName);
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        // Transcribe an existing WAV the user picks (e.g. a kept recording, or one made before Whisper was set
        // up). Writes <name>.transcript.txt beside it. Runs Whisper on a background task so the pane stays live.
        // The paths ON SCREEN (BUG-013): a path typed or browsed but not applied is the one the user means.
        private string TranscribeExisting(IReadOnlyDictionary<string, string> pending)
        {
            IModuleSettings shown = OnScreenSettings(pending);
            string whisperExe = shown.Get("whisperExe", "");
            string model = shown.Get("whisperModel", "");
            if (!System.IO.File.Exists(whisperExe) || !System.IO.File.Exists(model))
                return "✗ Set the whisper-cli path and a model first.";
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "Choose a WAV recording to transcribe";
                    dlg.Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return "Unchanged.";
                    string wav = dlg.FileName;
                    string transcript = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(wav),
                        System.IO.Path.GetFileNameWithoutExtension(wav) + ".transcript.txt");
                    string name = System.IO.Path.GetFileNameWithoutExtension(wav);
                    // remembrance.busy (2.0.0): up synchronously on this UI thread, cleared in the finally.
                    BusySpan busy = Busy(BusyTranscribing);
                    Task.Run(() =>
                    {
                        try
                        {
                            bool did;
                            // No start time: this is a file the user picked, and its own start is not in it
                            // (RA-166); the header says so rather than printing the clock.
                            TranscribeWav(wav, transcript, whisperExe, model, name, null, null, out did);
                            _lastStatus = did ? "Transcribed: " + name : "Transcription failed: " + name;
                            Announce(did ? "Transcript ready." : "Transcription failed.");
                        }
                        catch (Exception ex) { try { _host.Log(Id, "manual transcribe failed: " + ex.Message); } catch { } }
                        finally { busy.Dispose(); }
                    });
                    return "Transcribing " + name + "…";
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        // --- whisper setup ---------------------------------------------------------------------------

        private string ModelChoiceDisplay()
        {
            string id = WhisperInstaller.ResolveModelId(_settings.Get("whisperModelChoice", WhisperInstaller.DefaultModelId));
            WhisperInstaller.ModelChoice choice = WhisperInstaller.Models
                .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            return choice != null ? choice.Display : WhisperInstaller.Models[1].Display;
        }

        private static string ModelIdFromDisplay(string display)
        {
            string value = (display ?? "").Trim();
            WhisperInstaller.ModelChoice choice = WhisperInstaller.Models
                .FirstOrDefault(m => string.Equals(m.Display, value, StringComparison.OrdinalIgnoreCase));
            return choice != null ? choice.Id : WhisperInstaller.ResolveModelId(value);
        }

        /// <summary>
        /// "Refresh local models" in the Transcription card (2.0.0, once "Find an installed Whisper"). The pair ON
        /// SCREEN decides, through <see cref="WhisperInstaller.PlanRefresh"/>: when its two files exist they are kept,
        /// nothing is written (Apply does that), and the answer names any other model detection found, so the user
        /// can Browse to it. Only when either file is missing does it detect, and then it adopts the whole detected
        /// pair through AdoptWhisperPaths, which answers honestly on a failed save. Nothing to adopt names the file
        /// that is missing. Detection is cheap and offline, the roots WhisperProbeRoots gives.
        /// </summary>
        private string RefreshWhisper(IReadOnlyDictionary<string, string> pending)
        {
            try
            {
                IModuleSettings shown = OnScreenSettings(pending);
                string exe = shown.Get("whisperExe", "").Trim();
                string model = shown.Get("whisperModel", "").Trim();
                bool exeExists = exe.Length > 0 && System.IO.File.Exists(exe);
                bool modelExists = model.Length > 0 && System.IO.File.Exists(model);
                IReadOnlyList<string> roots = WhisperProbeRoots(DataDirectory());
                string foundExe = null, foundModel = null;
                bool detected = !(exeExists && modelExists) && WhisperInstaller.TryDetectIn(roots, out foundExe, out foundModel);
                switch (WhisperInstaller.PlanRefresh(exeExists, modelExists, detected))
                {
                    case WhisperInstaller.RefreshStep.KeepOnScreen:
                        return "✓ using " + System.IO.Path.GetFileName(exe) + " + " + System.IO.Path.GetFileName(model)
                             + AlsoFound(roots, model);
                    case WhisperInstaller.RefreshStep.AdoptDetected:
                        return AdoptWhisperPaths(foundExe, foundModel,
                            "✓ found " + System.IO.Path.GetFileName(foundExe) + " + " + System.IO.Path.GetFileName(foundModel));
                    default:
                        return "✗ " + WhatIsMissing(exe, exeExists, model, modelExists) +
                               ", and no other Whisper was found. Use \"Set up Whisper for me…\" to fetch it.";
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>"; also found: ..." naming the models detection finds other than <paramref name="model"/>, full
        /// paths so the user can Browse to one, at most four; "" when there are none.</summary>
        private static string AlsoFound(IEnumerable<string> roots, string model)
        {
            string chosen = "";
            try { chosen = System.IO.Path.GetFullPath(model); } catch { }
            List<string> others = WhisperInstaller.FindModels(roots)
                .Where(m => !string.Equals(m, chosen, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
            return others.Count > 0 ? "; also found: " + string.Join(", ", others) : "";
        }

        /// <summary>Which of the two files on screen is missing, said the way the Validate says it.</summary>
        private static string WhatIsMissing(string exe, bool exeExists, string model, bool modelExists)
        {
            var missing = new List<string>();
            if (!exeExists) missing.Add(exe.Length == 0 ? "the whisper-cli path is empty" : "whisper-cli is not at " + exe);
            if (!modelExists) missing.Add(model.Length == 0 ? "the model path is empty" : "the model file is not at " + model);
            string said = string.Join(" and ", missing);
            return said.StartsWith("the ", StringComparison.Ordinal) ? "T" + said.Substring(1) : said;
        }

        /// <summary>
        /// "Validate" in the Transcription card (2.0.0): does the pair ON SCREEN run? In order: (1) whisper-cli
        /// exists; (2) the model file exists and passes detection's size rule for a real ggml model
        /// (WhisperInstaller.MinimumGgmlBytes); (3) a 2-second test transcription through WhisperInstaller.TryVerify
        /// (the CheckWhisperRun seam), which judges by exit code 0 alone and keeps its five-minute cap. One answer
        /// that names the step that failed, or "✓ whisper-cli ran &lt;model&gt; on a 2-second test clip in N.N s". It
        /// neither writes nor clears the install's check-failed.txt: that marker records the install check's verdict,
        /// and this button checks whatever pair is on screen. Off the UI thread and one at a time, the pull's shape.
        /// </summary>
        private Task<string> ValidateWhisperAsync(IReadOnlyDictionary<string, string> pending)
        {
            IModuleSettings shown = OnScreenSettings(pending);
            string exe = shown.Get("whisperExe", "").Trim();
            string model = shown.Get("whisperModel", "").Trim();
            if (Interlocked.CompareExchange(ref _whisperCheckInFlight, 1, 0) != 0)
                return Task.FromResult("⚠ Whisper is already being checked; wait for that answer.");
            return Task.Run(() => CheckWhisperOnce(exe, model));
        }

        private string CheckWhisperOnce(string exe, string model)
        {
            try { return CheckWhisperSetUp(exe, model); }
            catch (Exception ex) { return "✗ " + ex.Message; }
            finally { Interlocked.Exchange(ref _whisperCheckInFlight, 0); }
        }

        private string CheckWhisperSetUp(string exe, string model)
        {
            if (exe.Length == 0)
                return "✗ The whisper-cli path is empty. Use \"Refresh local models\" or \"Browse for whisper-cli…\".";
            if (!System.IO.File.Exists(exe))
                return "✗ whisper-cli is not at " + exe + ". Use \"Refresh local models\" or \"Browse for whisper-cli…\".";
            if (model.Length == 0)
                return "✗ The model path is empty. Use \"Refresh local models\" or \"Browse for a model…\".";
            if (!System.IO.File.Exists(model))
                return "✗ The model file is not at " + model + ". Use \"Refresh local models\" or \"Browse for a model…\".";
            long bytes = new System.IO.FileInfo(model).Length;
            if (bytes <= WhisperInstaller.MinimumGgmlBytes)
                return "✗ " + System.IO.Path.GetFileName(model) + " is " +
                       (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) +
                       " MB, too small to be a Whisper model (a real ggml model is over 10 MB). It may be a partial" +
                       " download: use \"Set up Whisper for me…\" or \"Browse for a model…\".";
            var stopwatch = Stopwatch.StartNew();
            string failure;
            // remembrance.busy for the run alone (2.0.0): steps 1 and 2 load nothing, so a refusal there never
            // raises the flag. This is a pool thread, so the publish is posted the PersistOnUi way.
            using (Busy(BusyValidating))
                failure = CheckWhisperRun(exe, model, WhisperCheckClipSamples);
            stopwatch.Stop();
            if (failure != null) return "✗ whisper-cli did not run the 2-second test clip: " + failure;
            return "✓ whisper-cli ran " + System.IO.Path.GetFileName(model) + " on a 2-second test clip in " +
                   stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>Set the two Whisper paths and save them, answering <paramref name="success"/> only when the
        /// write landed; a failed write answers with what TrySaveSettings says instead of a tick (RA-157). The
        /// one path RefreshWhisper and SetUpWhisperAsync both adopt through, and the one the self-test drives.</summary>
        private string AdoptWhisperPaths(string exe, string model, string success)
        {
            _settings.Set("whisperExe", exe ?? "");
            _settings.Set("whisperModel", model ?? "");
            string notPersisted;
            if (!TrySaveSettings("the Whisper paths", out notPersisted))
            {
                _lastStatus = notPersisted;
                return notPersisted;
            }
            _lastStatus = "Whisper found.";
            return success;
        }

        /// <summary>
        /// One-click setup: adopt an existing install if there is one, else fetch the CLI and the chosen model
        /// from upstream into this module's own storage and prove the pair actually runs.
        /// </summary>
        private async Task<string> SetUpWhisperAsync(IReadOnlyDictionary<string, string> pending)
        {
            try
            {
                // The model ON SCREEN (BUG-013): its dropdown is labelled as the one this button uses.
                string modelId = WhisperInstaller.ResolveModelId(
                    OnScreenSettings(pending).Get("whisperModelChoice", WhisperInstaller.DefaultModelId));
                string exe, model;
                bool detected = WhisperInstaller.TryDetect(DataDirectory(), out exe, out model);
                // The dropdown is "used by Set up Whisper for me", so a detected pair whose model is not the
                // one chosen fetches the chosen model beside the module's install and keeps the detected exe;
                // only a detected pair that already carries the choice is "already installed" (RA-158).
                WhisperInstaller.SetupStep step = WhisperInstaller.PlanSetup(detected, model, modelId);
                if (step == WhisperInstaller.SetupStep.AdoptDetected)
                {
                    return AdoptWhisperPaths(exe, model,
                        "✓ already installed: " + System.IO.Path.GetFileName(exe) + " + " + System.IO.Path.GetFileName(model));
                }

                string root = WhisperInstaller.InstallRoot(DataDirectory());
                _lastStatus = "Setting up Whisper…";

                // Progress lands on the module status line rather than the pane: a PaneAction reports once,
                // when it returns, so a multi-hundred-megabyte download would otherwise look hung.
                // The module's own token, cancelled at Shutdown, rather than None: with None an install still
                // running when the app closed had nothing to stop it (F184).
                CancellationTokenSource installCts = _installCts;
                CancellationToken token = installCts != null ? installCts.Token : CancellationToken.None;
                WhisperInstaller.InstallResult result = await WhisperInstaller
                    .InstallAsync(root, modelId, p => { _lastStatus = "Whisper setup: " + p; }, token,
                        step == WhisperInstaller.SetupStep.FetchChosenModel ? exe : null)
                    .ConfigureAwait(true);

                if (!result.Ok)
                {
                    _lastStatus = "Whisper setup failed.";
                    try { _host.Log(Id, "whisper setup failed: " + result.Message); } catch { }
                    return "✗ " + result.Message;
                }

                string adopted = AdoptWhisperPaths(result.ExePath, result.ModelPath,
                    "✓ " + result.Message + " Model: " + System.IO.Path.GetFileName(result.ModelPath)
                    + (step == WhisperInstaller.SetupStep.FetchChosenModel
                        ? " (replacing " + System.IO.Path.GetFileName(model) + " in the settings; that file is untouched)"
                        : ""));
                if (adopted.StartsWith("✓", StringComparison.Ordinal)) _lastStatus = "Whisper is ready.";
                return adopted;
            }
            catch (Exception ex)
            {
                _lastStatus = "Whisper setup failed.";
                return "✗ " + ex.Message;
            }
        }

        private string DataDirectory()
        {
            try { return _storage != null ? _storage.DataDirectory : null; }
            catch { return null; }
        }

        // --- summary ---------------------------------------------------------------------------------

        /// <summary>
        /// Options for the summary-model dropdown: whatever the last "Refresh local models" discovered,
        /// UNIONED with the currently-saved model. That union is load-bearing, not cosmetic: the host renders
        /// a closed dropdown, so a saved model missing from the list would be silently blanked the next time
        /// the pane was applied (the same invariant AiBrain's model pickers rely on).
        /// </summary>
        private string[] SummaryModelOptions()
        {
            var options = new List<string>();
            string cached = _settings.Get("summaryModelsCache", "");
            foreach (string name in cached.Split('|'))
                if (!string.IsNullOrWhiteSpace(name)) options.Add(name.Trim());

            string saved = _settings.Get("summaryModel", "");
            if (!string.IsNullOrWhiteSpace(saved) && !options.Any(o => string.Equals(o, saved, StringComparison.OrdinalIgnoreCase)))
                options.Insert(0, saved.Trim());

            if (options.Count == 0) options.Add(NoModelsPlaceholder);
            return options.ToArray();
        }

        private async Task<string> RefreshSummaryModelsAsync(IReadOnlyDictionary<string, string> pending)
        {
            // The address ON SCREEN (BUG-013), through the ListModels seam like every other read of /api/tags.
            IModuleSettings shown = OnScreenSettings(pending);
            string endpoint = shown.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            try
            {
                IReadOnlyList<string> models = await ListModels(endpoint, CancellationToken.None).ConfigureAwait(true);
                if (models == null || models.Count == 0)
                {
                    return "✗ No generation-capable model answered at " + OllamaSummarizer.NormalizeEndpoint(endpoint) +
                           ". Is Ollama running, and has it a non-embedding model pulled?";
                }
                _settings.Set("summaryModelsCache", string.Join("|", models));
                // A first model is chosen only when NOTHING is picked on screen; a pick the user has not applied
                // yet is theirs, and the rebuild puts it back over the dropdown.
                if (string.IsNullOrWhiteSpace(shown.Get("summaryModel", ""))) _settings.Set("summaryModel", models[0]);
                string notPersisted;
                if (!TrySaveSettings("the discovered model list", out notPersisted)) return notPersisted;
                return "✓ found " + models.Count + ": " + string.Join(", ", models.Take(6));
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// Pull the chosen model into the local Ollama.
        ///
        /// A DOWNLOAD, not inference: /api/pull moves bytes to disk and loads nothing onto the GPU,
        /// so this costs no VRAM and cannot evict whatever the user is running. It does cost
        /// gigabytes, which is why the size is on the dropdown label the user picked from rather
        /// than buried in a confirmation nobody reads.
        ///
        /// Reachability is checked FIRST and separately, because "no Ollama installed" and "Ollama
        /// running, no model" need opposite advice and the pull would report them identically.
        ///
        /// THE MODEL AND THE ADDRESS ON SCREEN, AND AN ANSWER IN THE PANE (BUG-013). This read the saved
        /// recommendedModel and endpoint, so a model picked in the dropdown but not applied was never the one
        /// fetched; and it returned "Downloading ... in the background" at once, with the outcome going to a
        /// Status line an open pane never redraws. Now it waits up to <see cref="PullAnswerBound"/>: a pull that
        /// ends inside it is answered here, ✓ selected or ✗ with the reason, and one still running is answered
        /// with its latest progress and carries on. The wait lives here, not in StartRecommendedPull, which still
        /// returns at once and is what the single-flight checks drive.
        /// </summary>
        private async Task<string> DownloadRecommendedModelAsync(IReadOnlyDictionary<string, string> pending)
        {
            IModuleSettings shown = OnScreenSettings(pending);
            string endpoint = shown.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            string id = OllamaSummarizer.RecommendedIdFromDisplay(
                shown.Get("recommendedModel", OllamaSummarizer.DefaultRecommendedId));

            bool reachable = await IsReachable(endpoint, CancellationToken.None).ConfigureAwait(true);
            if (!reachable)
                return "✗ Nothing is answering at " + OllamaSummarizer.NormalizeEndpoint(endpoint) +
                       ". Install Ollama first (there is a button for it below), then try again.";
            Task<string> answer;
            string started = StartRecommendedPull(endpoint, id, out answer);
            if (answer == null) return started;   // the single-flight gate refused: one is already running
            using (var stopWaiting = new CancellationTokenSource())
            {
                Task bound = Task.Delay(PullAnswerBound, stopWaiting.Token);
                Task first = await Task.WhenAny(answer, bound).ConfigureAwait(true);
                stopWaiting.Cancel();
                if (first == answer) return await answer.ConfigureAwait(true);
            }
            return "⚠ " + id + ": " + PullProgressFor(id, _lastPullProgress) +
                   ". The download carries on in the background; reopen this pane to see the Status line.";
        }

        /// <summary>A progress line as the "still running" answer quotes it: without the leading "&lt;model&gt;: "
        /// ProgressLine puts on it, since the answer names the model already.</summary>
        private static string PullProgressFor(string id, string progress)
        {
            string line = (progress ?? "").Trim();
            string prefix = (id ?? "") + ": ";
            if (line.StartsWith(prefix, StringComparison.Ordinal)) line = line.Substring(prefix.Length);
            return line.Length > 0 ? line : "no progress reported yet";
        }

        /// <summary>
        /// The pull itself: started rather than awaited, matching how this module already handles
        /// transcription and summarising, because a PaneAction reports once, when it returns, so awaiting
        /// gigabytes here would leave the button dead and the pane looking hung for an hour. Split from the
        /// reachability probe so the self-test can drive it with no server at all.
        ///
        /// ONE AT A TIME. The host re-enables the button as soon as the probe above returns, while the pull
        /// runs on for minutes, and a second press opened a second /api/pull stream for the same tag: two
        /// pulls' progress lines interleaved on one status line, two announcements, and Ollama serving the
        /// same layers twice (RA-159). The same Interlocked single-flight gate TakeSnapshot uses (the
        /// BeginVramProbe shape), released in a finally. UNDER THE MODULE'S TOKEN: Shutdown cancels
        /// _installCts, which F184 introduced for the Whisper install; the pull ran on CancellationToken.None
        /// outside it (RA-160), so a module unloaded mid-pull kept the HTTP stream and the pool thread alive.
        /// </summary>
        private string StartRecommendedPull(string endpoint, string id)
        {
            Task<string> answer;
            return StartRecommendedPull(endpoint, id, out answer);
        }

        /// <summary>The pull, handing back <paramref name="answer"/>: what the pane should say once the pull has an
        /// outcome (✓ selected, or ✗ with the reason), completed only after the selection's settings write has
        /// landed. Null when the single-flight gate refused the press.</summary>
        private string StartRecommendedPull(string endpoint, string id, out Task<string> answer)
        {
            answer = null;
            if (Interlocked.CompareExchange(ref _pullInFlight, 1, 0) != 0)
                return "⚠ a model download is already running; reopen this pane to watch the Status line.";
            CancellationTokenSource installCts = _installCts;
            CancellationToken token = installCts != null ? installCts.Token : CancellationToken.None;
            var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = outcome.Task;
            _lastStatus = "Downloading " + id + "...";
            _lastPullProgress = "";
            // The start and the success are logged with the model and the address, beside the failure line that
            // was already there, so the next report of a "download that did nothing" can be settled from
            // diagnostics.log: which model was pulled, from where, and whether it finished (BUG-013).
            Log("model pull started: " + id + " from " + OllamaSummarizer.NormalizeEndpoint(endpoint));
            // Discarded on purpose: this is fire-and-report, and awaiting it is the one thing
            // the summary above says not to do.
            _ = Task.Run(async () =>
            {
                try
                {
                    OllamaSummarizer.PullResult pull = await PullModel(
                        endpoint, id, p => { _lastStatus = p; _lastPullProgress = p; }, token).ConfigureAwait(false);
                    if (!pull.Ok)
                    {
                        _lastStatus = "Download failed: " + pull.Message;
                        Log("model pull did not complete: " + pull.Message);
                        Announce("The model download failed.");
                        outcome.TrySetResult("✗ " + pull.Message);
                        return;
                    }
                    Log("model pull finished: " + id + " is installed at " + OllamaSummarizer.NormalizeEndpoint(endpoint));
                    // Select what was just fetched, and put it in the dropdown that offers it, or the
                    // user downloads 7 GB and still has nothing chosen. Marshalled, for the reason spelled
                    // out on PersistOnUi: this is a thread-pool continuation and Apply serialises the same
                    // dictionary on the UI thread. _lastStatus and the answer move inside the write, so neither
                    // can claim "installed and selected" before the write has landed.
                    IReadOnlyList<string> models = await ListModels(endpoint, token).ConfigureAwait(false);
                    // SetInBackground, not a bare Set: the pane may be open on the old model, and its next Apply must
                    // keep this selection unless the user picked another (BUG-013).
                    PersistOnUi(delegate
                    {
                        SetInBackground("summaryModel", id);
                        if (models != null && models.Count > 0) SetInBackground("summaryModelsCache", string.Join("|", models));
                        string notPersisted;
                        bool persisted = TrySaveSettings("the downloaded model's selection", out notPersisted);
                        _lastStatus = persisted ? id + " is installed and selected." : id + " is installed; " + notPersisted;
                        outcome.TrySetResult(persisted
                            ? "✓ " + id + " is installed and selected."
                            : "⚠ " + id + " is installed, but the settings file could not be written, so it is selected" +
                              " for this session only. Check that the file is not read-only or locked, then press again.");
                    });
                    Announce("The summary model is ready.");
                }
                catch (Exception ex)
                {
                    try { _host.Log(Id, "model pull failed: " + ex.Message); } catch { }
                    outcome.TrySetResult("✗ " + ex.Message);
                }
                finally { Interlocked.Exchange(ref _pullInFlight, 0); }
            });
            return "Downloading " + id + " in the background. Reopen this pane to watch the Status line.";
        }

        /// <summary>The self-test's entry to <see cref="OpenWhisperDownloads"/> with nothing on screen, so it reads the
        /// saved choice; the pane's own delegate is pressed with what is on screen.</summary>
        internal string OpenWhisperDownloadsForSelfTest() { return OpenWhisperDownloads(null); }

        /// <summary>
        /// Open both downloads in the browser: the whisper.cpp release page, and the exact model
        /// file the dropdown above is set to.
        ///
        /// THE BROWSER IS THE TRUSTED PATH, and that is the entire point. Diagnosed 2026-09-22:
        /// Defender Network Protection terminates this app's own fetch because it scores the
        /// calling program and this one is unsigned with no prevalence, while the same URL
        /// answers a browser or a signed PowerShell with HTTP 200. Rather than fight that, hand
        /// the two URLs over and let the Browse buttons take it from there.
        ///
        /// The MODEL url follows the dropdown rather than being a fixed link to the directory, so
        /// what downloads is what "Set up Whisper for me" would have fetched. Nothing else on
        /// this pane would tell the user which of the eleven files in that repository they need.
        ///
        /// Both are reported by name even on success, because a browser opening behind the
        /// options window is easy to miss and a silent tick would read as nothing happening.
        /// (This summary and OpenOllamaSite's sat stacked on the seam above until 2026-09-30, RA-161.)
        /// </summary>
        private string OpenWhisperDownloads(IReadOnlyDictionary<string, string> pending)
        {
            // The model ON SCREEN (BUG-013): the dropdown says it is what this button opens.
            string modelId = WhisperInstaller.ResolveModelId(
                OnScreenSettings(pending).Get("whisperModelChoice", WhisperInstaller.DefaultModelId));
            string modelUrl = WhisperInstaller.ModelUrl(modelId);

            var opened = new List<string>();
            var refused = new List<string>();
            foreach (string url in new[] { WhisperInstaller.ReleasesPageUrl, modelUrl })
            {
                bool ok;
                try { ok = _host.OpenLink(Id, url); }
                catch (Exception) { ok = false; }
                (ok ? opened : refused).Add(url);
            }

            if (refused.Count == 0)
                return "✓ opened " + string.Join("  and  ", opened.ToArray())
                     + "  On the releases page take whisper-bin-x64.zip from the newest bXXXX build that lists"
                     + " one (the vX.Y.Z entry GitHub marks Latest carries no Windows zip), save the model file,"
                     + " then use \"Browse for whisper-cli…\" and \"Browse for a model…\".";
            if (opened.Count == 0)
                return "✗ the host would not open " + string.Join(" or ", refused.ToArray());
            return "⚠ opened " + string.Join(", ", opened.ToArray())
                 + " but not " + string.Join(", ", refused.ToArray());
        }

        /// <summary>Open the Ollama download page, for the case where the runtime is missing entirely.</summary>
        private string OpenOllamaSite()
        {
            try
            {
                return _host.OpenLink(Id, OllamaSummarizer.OllamaDownloadUrl)
                    ? "✓ opened " + OllamaSummarizer.OllamaDownloadUrl
                    : "✗ the host would not open " + OllamaSummarizer.OllamaDownloadUrl;
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// "Validate" in the Summary card (2.0.0, replacing "Test the summarizer"): will a summary WORK with the address
        /// and the model ON SCREEN? Three questions, in the order a user can fix them, and one answer that names the
        /// first that failed:
        ///   1. does Ollama answer at that address (the probe "Download that model" uses);
        ///   2. is the model installed there (in /api/tags, allowing for an untagged name's implicit ":latest");
        ///   3. does it answer: one short request, the old Test button's two-line meeting.
        /// Step 3 loads the model, which is the point: "installed" is not "working", and a model too big for the
        /// machine or damaged on disk shows itself only there. That is a model run, at the old Test button's cost.
        /// Off the UI thread and one at a time, the pull's shape: a pane rebuild hands the user a fresh, enabled button
        /// while the first check is still loading the model.
        /// </summary>
        private Task<string> ValidateSummaryAsync(IReadOnlyDictionary<string, string> pending)
        {
            IModuleSettings shown = OnScreenSettings(pending);
            string address = shown.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            string model = shown.Get("summaryModel", "").Trim();
            if (Interlocked.CompareExchange(ref _summaryCheckInFlight, 1, 0) != 0)
                return Task.FromResult("⚠ the summary set-up is already being checked; wait for that answer.");
            return Task.Run(() => CheckSummaryOnceAsync(address, model));
        }

        private async Task<string> CheckSummaryOnceAsync(string endpoint, string model)
        {
            try { return await CheckSummarySetUpAsync(endpoint, model).ConfigureAwait(false); }
            catch (Exception ex) { return "✗ " + ex.Message; }
            finally { Interlocked.Exchange(ref _summaryCheckInFlight, 0); }
        }

        private async Task<string> CheckSummarySetUpAsync(string endpoint, string model)
        {
            bool answering = await IsReachable(endpoint, CancellationToken.None).ConfigureAwait(false);
            if (!answering)
                return "✗ Nothing is answering at " + OllamaSummarizer.NormalizeEndpoint(endpoint) +
                       ". Is Ollama running? If it is not installed, use \"Get Ollama (opens the site)\".";
            if (model.Length == 0) return "✗ Pick a summary model first (\"Refresh local models\").";
            IReadOnlyList<string> installed = await ListModels(endpoint, CancellationToken.None).ConfigureAwait(false);
            if (!IsInstalled(installed, model))
                return "✗ " + model + " is not installed in Ollama. Use \"Download that model\" or \"Refresh local models\".";
            var stopwatch = Stopwatch.StartNew();
            OllamaSummarizer.SummaryResult answer;
            // remembrance.busy for step 3 alone, republished before each request it sends (2.0.0).
            using (BusySpan busy = Busy(BusyValidating))
                answer = await Summarize(endpoint, model, "Connection test",
                    ValidationTranscript, delegate { busy.Refresh(); }, CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            if (answer == null || !answer.Ok)
                return "✗ Ollama answers and " + model + " is installed, but it did not answer the test: " +
                       (answer != null ? answer.Message : "no result came back.");
            string preview = (answer.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > 120) preview = preview.Substring(0, 120) + "…";
            return "✓ Ollama answers, " + model + " is installed, and it answered in " +
                   stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s: " + preview;
        }

        /// <summary>Is <paramref name="model"/> among Ollama's installed names? Ollama lists every model with its tag
        /// and tags an untagged pull ":latest", so a name with no ":" also matches name + ":latest"; a tagged name
        /// matches only itself. Case-insensitive, as the dropdown's own union is. Pure, so the self-test pins it.</summary>
        internal static bool IsInstalled(IEnumerable<string> installed, string model)
        {
            string wanted = (model ?? "").Trim();
            if (wanted.Length == 0 || installed == null) return false;
            string tagged = wanted.IndexOf(':') < 0 ? wanted + ":latest" : null;
            foreach (string name in installed)
            {
                string listed = (name ?? "").Trim();
                if (string.Equals(listed, wanted, StringComparison.OrdinalIgnoreCase)) return true;
                if (tagged != null && string.Equals(listed, tagged, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Summarize a transcript the user picks, writing &lt;name&gt;.summary.txt beside it, with the model
        /// and the address ON SCREEN (BUG-013).</summary>
        private string SummarizeExisting(IReadOnlyDictionary<string, string> pending)
        {
            IModuleSettings shown = OnScreenSettings(pending);
            string model = shown.Get("summaryModel", "");
            if (string.IsNullOrWhiteSpace(model)) return "✗ Pick a summary model first (\"Refresh local models\").";
            try
            {
                IReadOnlyList<string> picked = _host.PickFilesToOpen("Choose a transcript to summarize", "Transcript", new[] { "txt" });
                if (picked == null || picked.Count == 0) return "Unchanged.";
                string transcriptPath = picked[0];
                string name = System.IO.Path.GetFileNameWithoutExtension(transcriptPath);
                if (name.EndsWith(".transcript", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".transcript".Length);
                string summaryPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(transcriptPath), name + ".summary.txt");

                string endpoint = shown.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
                // remembrance.busy (2.0.0): up synchronously on this UI thread, cleared in the finally.
                BusySpan busy = Busy(BusySummarizing);
                Task.Run(async () =>
                {
                    try
                    {
                        string transcript = System.IO.File.ReadAllText(transcriptPath);
                        bool wrote = await WriteSummaryAsync(endpoint, model, name, transcript, summaryPath, busy).ConfigureAwait(false);
                        _lastStatus = wrote ? ("Summarized: " + name) : ("Summary failed: " + name);
                        Announce(wrote ? "Summary ready." : "Could not summarize that transcript.");
                    }
                    catch (Exception ex) { try { _host.Log(Id, "manual summarize failed: " + ex.Message); } catch { } }
                    finally { busy.Dispose(); }
                });
                return "Summarizing " + name + "… it will be saved beside the transcript.";
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>Runs the summarizer and writes the file. Returns false without throwing on any failure:
        /// a summary is an extra, and losing it must never cost the transcript or the audio. <paramref name="busy"/>
        /// is the caller's remembrance.busy span, republished with a fresh "at" each time the map-reduce reports, which
        /// it does before every request it sends.</summary>
        private async Task<bool> WriteSummaryAsync(string endpoint, string model, string meetingName,
            string transcript, string summaryPath, BusySpan busy)
        {
            try
            {
                OllamaSummarizer.SummaryResult result = await Summarize(
                    endpoint, model, meetingName, transcript,
                    p => { _lastStatus = "Summary: " + p; if (busy != null) busy.Refresh(); },
                    CancellationToken.None).ConfigureAwait(false);
                if (!result.Ok || string.IsNullOrWhiteSpace(result.Text))
                {
                    try { _host.Log(Id, "summary failed: " + result.Message); } catch { }
                    return false;
                }
                System.IO.File.WriteAllText(summaryPath,
                    OllamaSummarizer.FileHeader(meetingName, model) + result.Text,
                    new System.Text.UTF8Encoding(false));
                // A successful result can still carry a message: the coverage note of a map-reduce that
                // stopped folding before the last part (F173). The file has it appended; the log gets it too.
                if (!string.IsNullOrWhiteSpace(result.Message))
                    Log("summary written with a coverage note: " + result.Message);
                return true;
            }
            catch (Exception ex)
            {
                try { _host.Log(Id, "summary write failed: " + ex.Message); } catch { }
                return false;
            }
        }

        // --- tray ------------------------------------------------------------------------------------

        // Tray-item icons (TrayItem.IconPng): raw PNG bytes from this module's own embedded resources, so the
        // base renders them without the ABI depending on System.Drawing. Null on any failure, which degrades
        // to an icon-less entry rather than breaking the tray.
        private static byte[] LoadIconResource(string fileName)
        {
            return EmbeddedResources.LoadBytes(typeof(RemembranceModule).Assembly, fileName);
        }

        private TrayItem BuildRecordTrayItem()
        {
            return new TrayItem
            {
                Group = 45,
                Order = 10,
                IconPng = LoadIconResource("recording.png"),
                DynamicText = () => _recording ? ("● Recording: " + _currentBase + " (click to stop)") : "Start recording a meeting",
                Click = ToggleRecording,
            };
        }

        private TrayItem BuildSnapshotTrayItem()
        {
            return new TrayItem
            {
                Group = 45,
                Order = 20,
                IconPng = LoadIconResource("snapshot.png"),
                DynamicText = () => "Snapshot the screen",
                Click = TakeSnapshot,
            };
        }

        // --- self-test -------------------------------------------------------------------------------

        /// <summary>
        /// Run by the app's convention flag: <c>DesktopAICompanion.exe --module-selftest=remembrance</c>, which loads
        /// this module through the REAL loader and calls this by reflection.
        ///
        /// What it covers, in order: capture naming and the purge's shape classification and its walk; the
        /// shared-context parse; whisper asset, model and path selection and the release-list parser; the
        /// summarizer's chunking, parsing and map-reduce coverage; the options pane's round trip; the recorder's
        /// construction, stop, failed-start and nothing-captured paths through fake devices that reproduce
        /// NAudio's threading (RecorderSelfCheck); the module's own stop paths, the shutdown flush, the snapshot
        /// hotkey off the UI thread, whisper's time limit, and the installer's lookup, download and pull bounds
        /// against scripted HTTP handlers; then the pane's actions pressed through their own delegates with what is
        /// on screen, and Apply keeping a write that landed while the pane was open (BUG-013). NO real device and NO
        /// network: device capture cannot run on a CI
        /// runner or under RDP, and a test that reached the network would fail for reasons that are not this
        /// module's fault, so devices and servers are stood in for through the seams the production code
        /// exposes. (This summary promised "the pure decision logic only ... Deliberately NO audio" until
        /// R-039, some 330 lines after that stopped being true.) The live capture and download paths are
        /// verified by hand; this is the regression net around everything that can be checked deterministically.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;

            Action<string, bool> check = (name, condition) =>
            {
                sb.AppendLine((condition ? "PASS: " : "FAIL: ") + name);
                if (!condition) ok = false;
            };

            // ---- CaptureStore: names and what the purge may delete ----
            check("Sanitize strips path separators", CaptureStore.Sanitize("a/b\\c:d") == "a_b_c_d");
            check("Sanitize trims and survives an empty name", CaptureStore.Sanitize("   ") == "");
            check("Sanitize caps very long names", CaptureStore.Sanitize(new string('x', 400)).Length <= 120);
            check("audio is ephemeral", CaptureStore.IsEphemeral(@"c:\x\recording.wav"));
            check("snapshots are ephemeral", CaptureStore.IsEphemeral(@"c:\x\snap 1.png"));
            check("transcripts are NEVER purged", !CaptureStore.IsEphemeral(@"c:\x\recording.transcript.txt"));
            check("summaries are NEVER purged", !CaptureStore.IsEphemeral(@"c:\x\recording.summary.txt"));
            check("an unknown file is left alone", !CaptureStore.IsEphemeral(@"c:\x\notes.docx"));

            // BOTH GATES, not just IsEphemeral. PurgeOneDirectory requires IsEphemeral AND
            // NamesThisModuleWrites, and until 2026-09-27 the second had no assertion anywhere in the
            // repo. That is exactly how the standalone snapshot went unpurged for so long: IsEphemeral
            // says yes to it -- the fixture above even uses "snap 1.png" -- while the root gate said no,
            // so the promise of a 72-hour retention quietly did not cover the ordinary way to use the
            // snapshot hotkey.
            // EVERY POSITIVE HERE IS A NAME THE MODULE ACTUALLY WRITES. Three of these used to be names
            // nothing produces -- "snap 1.png", "sprint review - snap 1.png" and "sprint review.wav" --
            // because "1" is not a timestamp and a flat recording is always "<meeting> - <stamp>.wav".
            // Asserting that the gate says yes to a shape nothing produces is what made three loose
            // branches look tested while they matched any snap*.png, any .wav, and "holiday - snapshot.png".
            check("a root snapshot from the hotkey IS ours to purge",
                CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00.png", false));
            check("a snapshot inside a capture folder IS ours to purge",
                CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00.png", true));
            check("a recording inside a capture folder IS ours to purge",
                CaptureStore.NamesThisModuleWrites("recording.wav", true));
            check("a flat-mode snapshot IS ours to purge",
                CaptureStore.NamesThisModuleWrites(
                    "sprint review - 2026-09-27 12-00-00 - snap 2026-09-27 12-05-00.png", false));
            check("a flat-mode recording IS ours to purge",
                CaptureStore.NamesThisModuleWrites("sprint review - 2026-09-27 12-00-00.wav", false));
            check("a flat-mode recording with no meeting name IS ours to purge",
                CaptureStore.NamesThisModuleWrites("2026-09-27 12-00-00.wav", false));
            // The SCRATCH tracks AudioRecorder writes beside a recording in flight. The normal stop deletes
            // them after the mix; every abnormal end -- a crash, a Restart Manager kill, an exit inside the
            // mix window, a start that failed half-way -- left them for ever, because until 2026-09-29 this
            // gate knew neither name in either mode (F171).
            check("a system scratch track inside a capture folder IS ours to purge",
                CaptureStore.NamesThisModuleWrites("recording.system.wav", true));
            check("a microphone scratch track inside a capture folder IS ours to purge",
                CaptureStore.NamesThisModuleWrites("recording.mic.wav", true));
            check("a flat-mode system scratch track IS ours to purge",
                CaptureStore.NamesThisModuleWrites("sprint review - 2026-09-27 12-00-00.system.wav", false));
            check("a flat-mode microphone scratch track with no meeting name IS ours to purge",
                CaptureStore.NamesThisModuleWrites("2026-09-27 12-00-00.mic.wav", false));
            // THE OTHER DIRECTION, and it is the one that matters most: storageLocation is free text
            // with a folder picker, so this list decides what gets deleted out of a folder the user
            // chose. A near-miss must NOT qualify. Deletion is File.Delete, not the recycle bin, and it
            // runs a minute after Init and then hourly (F180; this said "on Init" until R-039).
            check("a user's own png that merely starts with 'snap' is NOT ours",
                !CaptureStore.NamesThisModuleWrites("snapshot of my cat.png", false));
            check("'snap ' followed by something that is not a timestamp is NOT ours",
                !CaptureStore.NamesThisModuleWrites("snap holiday photo.png", false));
            // ...and the same two INSIDE a capture folder, which had no negative coverage at all. That
            // branch was `StartsWith("snap") && EndsWith(".png")`, so both of these were deleted from
            // every immediate subdirectory of the root.
            check("a user's own 'snapshot...' png in a subfolder is NOT ours",
                !CaptureStore.NamesThisModuleWrites("snapshot of my cat.png", true));
            check("an unstamped 'snap ...' png in a subfolder is NOT ours",
                !CaptureStore.NamesThisModuleWrites("snap holiday photo.png", true));
            // The widest thing the purge ever matched: a bare .wav test in the root. A user who pointed
            // storageLocation at a folder holding their own audio lost all of it past the window.
            check("a user's own wav is NOT ours, extension alone is not a shape",
                !CaptureStore.NamesThisModuleWrites("interview notes.wav", false));
            check("a wav whose trailing digits are not a timestamp is NOT ours",
                !CaptureStore.NamesThisModuleWrites("track 2026-13-45 99-99-99.wav", false));
            check("'<name> - snapshot.png' is NOT ours; the separator is part of the shape",
                !CaptureStore.NamesThisModuleWrites("my holiday - snapshot.png", false));
            check("a bare recording.wav in the ROOT is not a shape this module writes there",
                !CaptureStore.NamesThisModuleWrites("recording.wav", false));
            check("a bare recording.system.wav in the ROOT is not ours either",
                !CaptureStore.NamesThisModuleWrites("recording.system.wav", false));
            check("a user's own '<name>.system.wav' is NOT ours; the suffix alone is not a shape",
                !CaptureStore.NamesThisModuleWrites("my.system.wav", false));
            check("a user's own '<name>.mic.wav' is NOT ours",
                !CaptureStore.NamesThisModuleWrites("holiday.mic.wav", false));
            check("an unstamped '<name>.mic.wav' inside a capture folder is NOT ours",
                !CaptureStore.NamesThisModuleWrites("interview.mic.wav", true));
            check("a transcript is never ours to purge",
                !CaptureStore.NamesThisModuleWrites("sprint review.transcript.txt", false));
            // THE FOLDER IS A SHAPE TOO (RA-151). Inside a capture folder the shapes are the bare "recording.wav"
            // and its scratch siblings, so the purge descends only into a folder NewCapture would have named;
            // Documents\Zoom\recording.wav is somebody else's however old it is.
            check("a folder named '<meeting> - <stamp>' IS one the purge may descend into",
                CaptureStore.IsCaptureFolderName("Sprint Review - 2026-09-27 12-00-00"));
            check("a folder named '<stamp>' alone IS one too",
                CaptureStore.IsCaptureFolderName("2026-09-27 12-00-00"));
            check("a folder named 'Zoom' is NOT descended into, whatever it holds",
                !CaptureStore.IsCaptureFolderName("Zoom"));
            check("a folder whose trailing digits are not a timestamp is NOT descended into",
                !CaptureStore.IsCaptureFolderName("backup 2026-13-45 99-99-99"));
            // A second snapshot inside one second carries " (2)": a parsed shape, so it is ours to purge and a
            // near-miss is not (RA-156).
            check("a collision-suffixed root snapshot IS ours to purge",
                CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00 (2).png", false));
            check("a collision-suffixed snapshot inside a capture folder IS ours to purge",
                CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00 (3).png", true));
            check("a collision-suffixed flat-mode snapshot IS ours to purge",
                CaptureStore.NamesThisModuleWrites(
                    "sprint review - 2026-09-27 12-00-00 - snap 2026-09-27 12-05-00 (2).png", false));
            check("a suffix that is not ' (2)'..' (99)' is NOT ours",
                !CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00 (x).png", false)
                && !CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00 (2)(3).png", false)
                && !CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00 (1).png", false)
                && !CaptureStore.NamesThisModuleWrites("snap 2026-09-27 12-00-00 (100).png", false));

            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // A LOCAL instant, so NewCapture's ToLocalTime is the identity and the expected stamp holds in
                // every zone. As a UTC instant, 09:30Z is 23:30 the previous day at UTC-10 and further west,
                // and both date checks here failed there and only there (F179). The whole stamp is asserted
                // now, not just the date.
                var fixture = new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Local));
                var withName = new CaptureStore(scratch, FolderLayout.PerCapture);
                CapturePaths named = withName.NewCapture("Sprint Review", fixture);
                check("a named capture uses '<meeting> - <stamp>'", named.BaseName == "Sprint Review - 2026-08-27 09-30-00");
                check("folder-per-capture nests the files", named.Audio.Replace('/', '\\').Contains(named.BaseName));
                check("transcript sits beside the audio", named.Transcript.EndsWith(".transcript.txt"));
                check("summary sits beside the transcript", named.Summary.EndsWith(".summary.txt"));

                // The by-date layout replaced the flat one (2.0.0); its files keep the flat names.
                var byDay = new CaptureStore(scratch, FolderLayout.ByDate);
                CapturePaths unnamed = byDay.NewCapture("", fixture);
                check("no meeting name falls back to a timestamp", unnamed.BaseName == "2026-08-27 09-30-00");
                check("by date names its files with the base name, as the flat layout did, not recording.wav",
                    !unnamed.Audio.EndsWith("recording.wav"));

                // The folder a failed start leaves behind (F171): removed only when empty, never otherwise,
                // because it sits in a location the user chose.
                string neverStarted = System.IO.Path.Combine(scratch, "never started - 2026-08-27 09-31-00");
                System.IO.Directory.CreateDirectory(neverStarted);
                check("WITNESS an empty capture folder from a failed start is removed",
                    CaptureStore.TryRemoveEmptyCaptureFolder(neverStarted) && !System.IO.Directory.Exists(neverStarted));
                System.IO.File.WriteAllText(named.Transcript, "kept");
                check("a capture folder with anything in it is kept",
                    !CaptureStore.TryRemoveEmptyCaptureFolder(named.Directory) && System.IO.Directory.Exists(named.Directory));
                check("the capture remembers the instant it started, for the transcript header (RA-166)",
                    named.StartedAt == fixture);

                // ---- two snapshots inside one second get two files (RA-156) ----
                string snapDir = System.IO.Path.Combine(scratch, "snaps");
                System.IO.Directory.CreateDirectory(snapDir);
                string firstSnap = System.IO.Path.Combine(snapDir, "snap 2026-08-27 09-30-00.png");
                check("WITNESS a snapshot path nothing occupies is used as it is",
                    CaptureStore.UniqueSnapshotPath(firstSnap) == firstSnap);
                System.IO.File.WriteAllBytes(firstSnap, new byte[1]);
                string secondSnap = CaptureStore.UniqueSnapshotPath(firstSnap);
                check("a second snapshot in the same second lands beside the first as ' (2)', never over it",
                    secondSnap == System.IO.Path.Combine(snapDir, "snap 2026-08-27 09-30-00 (2).png"));
                System.IO.File.WriteAllBytes(secondSnap, new byte[1]);
                check("...and a third as ' (3)'",
                    CaptureStore.UniqueSnapshotPath(firstSnap) == System.IO.Path.Combine(snapDir, "snap 2026-08-27 09-30-00 (3).png"));
                check("...each of which the purge recognises as this module's, in the root and in a capture folder",
                    CaptureStore.NamesThisModuleWrites(System.IO.Path.GetFileName(secondSnap), false)
                    && CaptureStore.NamesThisModuleWrites(System.IO.Path.GetFileName(secondSnap), true));

                // ---- the purge's walk, end to end (RA-151, R-037) ----
                // A foreign subfolder holding a "recording.wav" older than the window, a capture-named folder
                // holding the same, a capture-named folder holding a scratch and a transcript, one Purge pass.
                // File and folder times are set four days back, the folders' AFTER their files went in, since
                // writing into a folder bumps its own write time.
                string walkRoot = System.IO.Path.Combine(scratch, "walk");
                DateTime fourDaysAgo = DateTime.UtcNow.AddDays(-4);
                string zoom = System.IO.Path.Combine(walkRoot, "Zoom");
                string emptied = System.IO.Path.Combine(walkRoot, "Sprint Review - 2026-08-20 09-30-00");
                string keptFolder = System.IO.Path.Combine(walkRoot, "Sprint Review - 2026-08-21 09-30-00");
                foreach (string d in new[] { zoom, emptied, keptFolder }) System.IO.Directory.CreateDirectory(d);
                string zoomWav = System.IO.Path.Combine(zoom, "recording.wav");
                string emptiedWav = System.IO.Path.Combine(emptied, "recording.wav");
                string keptScratch = System.IO.Path.Combine(keptFolder, "recording.system.wav");
                string keptTranscript = System.IO.Path.Combine(keptFolder, "recording.transcript.txt");
                foreach (string f in new[] { zoomWav, emptiedWav, keptScratch })
                {
                    System.IO.File.WriteAllBytes(f, new byte[64]);
                    System.IO.File.SetLastWriteTimeUtc(f, fourDaysAgo);
                }
                System.IO.File.WriteAllText(keptTranscript, "kept");
                System.IO.File.SetLastWriteTimeUtc(keptTranscript, fourDaysAgo);
                foreach (string d in new[] { zoom, emptied, keptFolder })
                {
                    System.IO.Directory.SetCreationTimeUtc(d, fourDaysAgo);
                    System.IO.Directory.SetLastWriteTimeUtc(d, fourDaysAgo);
                }
                new CaptureStore(walkRoot, FolderLayout.PerCapture).Purge();
                check("a foreign subfolder's recording.wav survives the purge, however old",
                    System.IO.File.Exists(zoomWav));
                check("WITNESS a capture folder's old recording.wav is purged",
                    !System.IO.File.Exists(emptiedWav));
                check("a capture folder the purge has emptied is removed with its last file",
                    !System.IO.Directory.Exists(emptied));
                check("WITNESS a capture folder still holding a transcript stays: scratch purged, transcript kept",
                    System.IO.Directory.Exists(keptFolder) && System.IO.File.Exists(keptTranscript)
                    && !System.IO.File.Exists(keptScratch));
                string young = System.IO.Path.Combine(walkRoot, "Sprint Review - 2026-08-22 09-30-00");
                System.IO.Directory.CreateDirectory(young);
                new CaptureStore(walkRoot, FolderLayout.PerCapture).Purge();
                check("an empty capture folder younger than the window is left alone",
                    System.IO.Directory.Exists(young));
            }
            catch (Exception ex) { check("CaptureStore paths: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }

            // ---- MeetingContext: the Reminder module's shared-context publish ----
            MeetingContext empty = MeetingContext.Parse(null);
            check("no published context yields an empty meeting", empty.Name == "" && empty.Attendees.Count == 0);
            check("malformed JSON does not throw", MeetingContext.Parse("{ not json").Name == "");
            MeetingContext parsed = MeetingContext.Parse(
                "{\"name\":\"Standup\",\"location\":\"Teams\",\"attendees\":[{\"name\":\"Ada\",\"status\":\"accepted\"},{\"name\":\"Bo\"}]}");
            check("meeting name is read", parsed.Name == "Standup");
            check("location is read", parsed.Location == "Teams");
            check("an attendee status is appended", parsed.Attendees.Contains("Ada (accepted)"));
            check("an attendee without a status is bare", parsed.Attendees.Contains("Bo"));

            // ---- WhisperInstaller: selection logic ----
            check("the default model is supported", WhisperInstaller.IsSupportedModel(WhisperInstaller.DefaultModelId));

            // ---- a transcription failure must say WHICH failure ----
            // Six distinct paths used to return the same empty string, so all six printed "Whisper is not
            // configured" -- including the ones where it demonstrably IS configured. A truncated
            // ggml-base.en.bin adopted through "Browse for a model" passes every File.Exists on the way
            // in; whisper-cli is the only thing that knows it is bad, and it says so on stderr, which was
            // read and dropped on the floor.
            check("WITNESS a non-zero exit reports whisper's own reason",
                Transcriber.DescribeExit(1, "whisper_init: loading model\nerror: failed to load model")
                    .IndexOf("failed to load model", StringComparison.Ordinal) >= 0);
            check("...and names the exit code, so a silent tool is still identifiable",
                Transcriber.DescribeExit(3, "").IndexOf("3", StringComparison.Ordinal) >= 0);
            check("a run that failed for its own reasons does not borrow the setup message",
                Transcriber.DescribeExit(1, "  ").IndexOf("not configured", StringComparison.Ordinal) < 0);
            check("CRLF stderr is handled, so a Windows tool's last line is not blank",
                Transcriber.DescribeExit(2, "first\r\nerror: bad model\r\n")
                    .IndexOf("error: bad model", StringComparison.Ordinal) >= 0);
            check("an unknown model falls back to the default",
                WhisperInstaller.ResolveModelId("ggml-nonsense.bin") == WhisperInstaller.DefaultModelId);
            check("the model URL is the whisper.cpp HF repo",
                WhisperInstaller.ModelUrl("ggml-tiny.en.bin") ==
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin");
            check("the exact x64 asset wins",
                WhisperInstaller.PickAssetName(new[] { "whisper-bin-Win32.zip", "whisper-bin-x64.zip", "source.zip" })
                == "whisper-bin-x64.zip");
            check("a renamed bin-x64 zip is still found",
                WhisperInstaller.PickAssetName(new[] { "whisper-v1.2-bin-x64.zip", "source.zip" })
                == "whisper-v1.2-bin-x64.zip");
            check("no x64 asset returns null",
                WhisperInstaller.PickAssetName(new[] { "whisper-bin-Win32.zip", "source.tar.gz" }) == null);
            check("a sha256 digest is parsed",
                WhisperInstaller.ParseSha256("sha256:" + new string('a', 64)) == new string('a', 64));
            check("a non-sha256 digest is ignored", WhisperInstaller.ParseSha256("md5:abc") == null);
            check("a truncated digest is ignored", WhisperInstaller.ParseSha256("sha256:abc") == null);
            check("an absent digest is ignored, not an error", WhisperInstaller.ParseSha256(null) == null);
            // Through the LIST parser, the production path (F185). ParseReleaseJson was the releases/latest era's
            // entry point and these checks were its only readers, so the suite's one digest assertion witnessed a
            // path InstallAsync no longer took; its no-assets and garbage cases are the list parser's below.
            WhisperInstaller.ReleaseAsset release = WhisperInstaller.ParseReleaseListJson(
                "[{\"assets\":[{\"name\":\"whisper-bin-x64.zip\",\"browser_download_url\":\"https://example.invalid/w.zip\"," +
                "\"digest\":\"sha256:" + new string('b', 64) + "\"}]}]");
            check("release JSON yields the asset url", release != null && release.Url == "https://example.invalid/w.zip");
            check("release JSON yields the digest", release != null && release.Digest != null && release.Digest.StartsWith("sha256:"));
            // ---- the release LIST, which is what upstream actually publishes to ----
            // Broke in the field on 2026-09-20. whisper.cpp tags asset-less semantic releases
            // (v1.9.4) alongside the bXXXX builds that carry the Windows zips, and GitHub calls the
            // former "latest" -- so releases/latest answered 200 with an empty assets array and the
            // module told the user it had been rate-limited. Both halves are asserted: that an
            // asset-less release is stepped over rather than ending the search, and that the failure
            // message stops claiming a throttle it has no evidence for.
            WhisperInstaller.ReleaseAsset fromList = WhisperInstaller.ParseReleaseListJson(
                "[{\"tag_name\":\"v1.9.4\",\"assets\":[]},{\"tag_name\":\"b5130\",\"assets\":[{\"name\":\"whisper-bin-Win32.zip\",\"browser_download_url\":\"https://example.invalid/32.zip\"},{\"name\":\"whisper-bin-x64.zip\",\"browser_download_url\":\"https://example.invalid/64.zip\"}]}]");
            check("WITNESS an asset-less release is skipped, not treated as the answer",
                fromList != null && fromList.Url == "https://example.invalid/64.zip");
            check("a list where nothing carries a Windows build yields null",
                WhisperInstaller.ParseReleaseListJson(
                    "[{\"tag_name\":\"v1\",\"assets\":[]},{\"tag_name\":\"v2\",\"assets\":[]}]") == null);
            check("a single release object is not mistaken for a list",
                WhisperInstaller.ParseReleaseListJson("{\"assets\":[]}") == null);
            check("garbage yields null rather than throwing",
                WhisperInstaller.ParseReleaseListJson("nope") == null);

            check("WITNESS a throttle is only claimed when the quota is actually spent",
                WhisperInstaller.DescribeHttpFailure(403, "0").Contains("rate-limiting"));
            check("WITNESS a 403 with quota left does NOT blame rate limiting",
                !WhisperInstaller.DescribeHttpFailure(403, "57").Contains("rate-limiting"));
            check("an unexpected status reports the status it saw",
                WhisperInstaller.DescribeHttpFailure(500, null).Contains("500"));
            check("a missing rate-limit header is not read as a spent quota",
                !WhisperInstaller.DescribeHttpFailure(403, null).Contains("rate-limiting"));
            // ---- the download button opens the right two pages -----------------------
            // The model link has to FOLLOW the dropdown. A fixed link would quietly send someone
            // to a different model than "Set up Whisper for me" would have fetched, and nothing
            // on the pane would contradict it -- the file would simply be the wrong size and the
            // transcription slower or worse than they chose.
            {
                var linkHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var linkStore =
                           new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("remembrance-links"))
                {
                    linkHost.UseStorage("remembrance", linkStore);
                    // Seeded like the other module instances in this method: Init no longer purges (F180),
                    // and this block still must not sit one purge-timer tick from the user's real folder.
                    linkHost.SettingsFor(Id).Set("storageLocation", linkStore.DataDirectory);
                    var linkModule = new RemembranceModule();
                    linkModule.Init(linkHost);

                    linkModule._settings.Set("whisperModelChoice", "ggml-small.en.bin");
                    linkModule._settings.Save();
                    // ClearOpenedLinks, not OpenedLinks.Clear(): the fake hands out snapshots since
                    // 2026-09-30 (N-remembrance-01), so a Clear() on the property cleared a copy.
                    linkHost.ClearOpenedLinks();
                    string said = linkModule.OpenWhisperDownloadsForSelfTest();

                    check("WITNESS the button opens exactly two pages, not one and not a guess",
                        linkHost.OpenedLinks.Count == 2);
                    check("WITNESS one of them is the human releases page, not the JSON API",
                        linkHost.OpenedLinks.Contains(WhisperInstaller.ReleasesPageUrl)
                        && WhisperInstaller.ReleasesPageUrl.IndexOf("api.github.com",
                               StringComparison.OrdinalIgnoreCase) < 0);
                    // The LIST, not /latest. GitHub's "latest" is the newest non-prerelease tag and upstream
                    // marks the builds that carry the Windows zips as pre-releases, so /latest is asset-less
                    // for good; the line above only asked for "not the API", which /latest satisfied (F183).
                    check("the human releases link is the LIST, not /latest, which upstream leaves asset-less",
                        WhisperInstaller.ReleasesPageUrl.EndsWith("/releases", StringComparison.Ordinal)
                        && !WhisperInstaller.ReleasesPageUrl.EndsWith("/latest", StringComparison.Ordinal)
                        && WhisperInstaller.ReleasesPageUrl.IndexOf("/tag/", StringComparison.Ordinal) < 0);
                    check("...and the success text names the file to take from that list",
                        said.IndexOf("whisper-bin-x64.zip", StringComparison.Ordinal) >= 0);
                    check("WITNESS the model link FOLLOWS the dropdown rather than being fixed",
                        linkHost.OpenedLinks.Contains(
                            WhisperInstaller.ModelUrl("ggml-small.en.bin")));

                    // And it changes when the choice changes, which is the half that would still
                    // pass against a hardcoded link if only the line above were asserted.
                    linkModule._settings.Set("whisperModelChoice", "ggml-tiny.en.bin");
                    linkModule._settings.Save();
                    linkHost.ClearOpenedLinks();
                    linkModule.OpenWhisperDownloadsForSelfTest();
                    check("WITNESS ...and it tracks a CHANGED choice",
                        linkHost.OpenedLinks.Contains(WhisperInstaller.ModelUrl("ggml-tiny.en.bin"))
                        && !linkHost.OpenedLinks.Contains(
                               WhisperInstaller.ModelUrl("ggml-small.en.bin")));

                    check("the result names both pages, since a browser opening behind the "
                          + "options window is easy to miss",
                        said.IndexOf("huggingface", StringComparison.OrdinalIgnoreCase) >= 0
                        && said.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0);
                    linkModule.Shutdown();
                }
            }

            // ---- a blocked download must become an instruction ----------------------
            // Diagnosed across two machines 2026-09-22: Defender Network Protection terminates
            // the connection, the module reported a TLS chain, and the pane became a dead end
            // even though the two Browse buttons on it make the feature work anyway.
            var aborted = new System.Net.Http.HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new System.Security.Authentication.AuthenticationException(
                    "Unable to read data from the transport connection.",
                    new System.Net.Sockets.SocketException(10053)));
            check("WITNESS a locally aborted connection is recognised as one",
                WhisperInstaller.IsConnectionAborted(aborted));

            string abortedText = WhisperInstaller.DescribeFetchFailure("Could not reach GitHub: ", aborted);
            check("WITNESS ...and the message names the way out rather than stopping at the error",
                abortedText.Contains("Browse for whisper-cli"));
            check("WITNESS ...and still carries the underlying cause, not just the advice",
                abortedText.Contains("transport connection"));

            // The converse, because advice on every failure is noise rather than help. A 404 is
            // not endpoint protection and must not be dressed up as it.
            var notAborted = new System.Net.Http.HttpRequestException("404 (Not Found)");
            check("WITNESS an ordinary failure is NOT blamed on endpoint protection",
                !WhisperInstaller.IsConnectionAborted(notAborted)
                && !WhisperInstaller.DescribeFetchFailure("", notAborted).Contains("Browse for whisper-cli"));

            // The registry read is machine-dependent, so what is pinned is the MEANING of a value (RA-163). The
            // line this replaces, "answers without throwing", could fail only on a value outside 0..2 already
            // sitting in this machine's registry, and no change to the method reached FAIL; the machine's own
            // value stays as the witness that whatever is there is something the interpreter accepts.
            check("a DWORD 1 reads as Network Protection ON", WhisperInstaller.InterpretNetworkProtectionValue(1) == 1);
            check("the decimal string '2' reads as audit mode", WhisperInstaller.InterpretNetworkProtectionValue("2") == 2);
            check("WITNESS 0 reads as off", WhisperInstaller.InterpretNetworkProtectionValue(0) == 0);
            check("a value outside 0..2 reads as unknown, not as a policy state",
                WhisperInstaller.InterpretNetworkProtectionValue(7) == null
                && WhisperInstaller.InterpretNetworkProtectionValue(-1) == null);
            check("bytes, words and null read as unknown",
                WhisperInstaller.InterpretNetworkProtectionValue(new byte[] { 1 }) == null
                && WhisperInstaller.InterpretNetworkProtectionValue("on") == null
                && WhisperInstaller.InterpretNetworkProtectionValue(null) == null);
            int? np = WhisperInstaller.NetworkProtectionState();
            check("WITNESS this machine's own Network Protection value, if any, is one the interpreter accepts",
                np == null || (np.Value >= 0 && np.Value <= 2));

            check("the install root is under the module's own storage",
                WhisperInstaller.InstallRoot(@"c:\data\remembrance").Replace('/', '\\') == @"c:\data\remembrance\whisper");
            check("probing includes the DevToolbox location",
                WhisperInstaller.ProbeRoots(@"c:\data\remembrance").Any(p => p.IndexOf("DevToolbox", StringComparison.OrdinalIgnoreCase) >= 0));

            // ---- an install that failed its run check is not adopted (RA-167) ----
            // Over an explicit root list, so nothing here walks the machine's own DevToolbox install.
            string detectScratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-detect-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string installRoot = WhisperInstaller.InstallRoot(detectScratch);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(installRoot, "bin"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(installRoot, "models"));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(installRoot, "bin", "whisper-cli.exe"), new byte[16]);
                // FindModel wants more than 10 MB behind a .bin; SetLength is milliseconds.
                using (System.IO.FileStream model = System.IO.File.Create(
                           System.IO.Path.Combine(installRoot, "models", "ggml-base.en.bin")))
                    model.SetLength(11L * 1024 * 1024);
                string foundExe, foundModel;
                check("WITNESS a root holding whisper-cli.exe and a model is detected",
                    WhisperInstaller.TryDetectIn(new[] { installRoot }, out foundExe, out foundModel)
                    && foundExe != null && foundModel != null && foundModel.EndsWith("ggml-base.en.bin", StringComparison.Ordinal));
                WhisperInstaller.MarkUnverified(installRoot, "whisper-cli exited 3 -- simulated");
                check("WITNESS the marker names the failure and what to do about it",
                    System.IO.File.ReadAllText(System.IO.Path.Combine(installRoot, WhisperInstaller.UnverifiedMarkerName))
                        .Contains("exited 3"));
                check("an install that failed its run check is passed over by detection, exe and model both",
                    !WhisperInstaller.TryDetectIn(new[] { installRoot }, out foundExe, out foundModel)
                    && foundExe == null && foundModel == null);
                WhisperInstaller.ClearUnverified(installRoot);
                check("WITNESS ...and is adopted again once a later check has passed and cleared the marker",
                    WhisperInstaller.TryDetectIn(new[] { installRoot }, out foundExe, out foundModel));
            }
            catch (Exception ex) { check("unverified install: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(detectScratch)) System.IO.Directory.Delete(detectScratch, true); } catch { }
            }

            // ---- "Set up Whisper for me" follows the dropdown once something is detected (RA-158) ----
            check("nothing detected: the whole install",
                WhisperInstaller.PlanSetup(false, null, "ggml-base.en.bin") == WhisperInstaller.SetupStep.InstallEverything);
            check("WITNESS a detected pair carrying the chosen model is adopted as already installed",
                WhisperInstaller.PlanSetup(true, @"c:\w\models\ggml-base.en.bin", "ggml-base.en.bin")
                == WhisperInstaller.SetupStep.AdoptDetected);
            check("a detected pair whose model is not the one chosen fetches the chosen model, keeping the exe",
                WhisperInstaller.PlanSetup(true, @"c:\w\models\ggml-base.en.bin", "ggml-small.en.bin")
                == WhisperInstaller.SetupStep.FetchChosenModel);
            check("the model comparison is by file name, case-insensitively",
                WhisperInstaller.PlanSetup(true, @"C:\W\MODELS\GGML-SMALL.EN.BIN", "ggml-small.en.bin")
                == WhisperInstaller.SetupStep.AdoptDetected);

            // ---- OllamaSummarizer: model filtering, chunking, parsing ----
            check("a completion model is offered", OllamaSummarizer.LooksGenerative("dolphin3:latest", new[] { "completion", "tools" }));
            check("an embedding model is refused", !OllamaSummarizer.LooksGenerative("bge-m3:latest", new[] { "embedding" }));
            check("embedding names are refused with no capability data",
                !OllamaSummarizer.LooksGenerative("qwen3-embedding:0.6b", null));
            check("a normal name is offered with no capability data",
                OllamaSummarizer.LooksGenerative("mistral:7b", null));
            check("capabilities outrank the name heuristic",
                OllamaSummarizer.LooksGenerative("embeddinggemma-chat", new[] { "completion" }));
            check("a blank name is refused", !OllamaSummarizer.LooksGenerative("", null));

            IReadOnlyList<string> parsedModels = OllamaSummarizer.ParseModels(
                "{\"models\":[{\"name\":\"a:1\",\"capabilities\":[\"completion\"]}," +
                "{\"name\":\"b:1\",\"capabilities\":[\"embedding\"]}]}");
            check("the tags list keeps only generative models",
                parsedModels.Count == 1 && parsedModels[0] == "a:1");

            check("a short transcript is one chunk", OllamaSummarizer.Chunk("hello there", 6000).Count == 1);
            check("an empty transcript is no chunks", OllamaSummarizer.Chunk("   ", 6000).Count == 0);
            IReadOnlyList<string> many = OllamaSummarizer.Chunk(
                string.Join("\n", Enumerable.Repeat("a line of meeting talk", 600)), 2000);
            check("a long transcript splits into several chunks", many.Count > 1);
            check("no chunk exceeds the limit", many.All(c => c.Length <= 2000));
            check("chunking loses no content",
                many.Sum(c => c.Replace("\n", "").Length) ==
                string.Join("\n", Enumerable.Repeat("a line of meeting talk", 600)).Replace("\n", "").Length);
            // whisper -otxt can emit one enormous line; it must be split, not dropped.
            IReadOnlyList<string> unbroken = OllamaSummarizer.Chunk(new string('x', 5000), 1000);
            check("a single oversized line is hard-split", unbroken.Count >= 5);
            check("the oversized line keeps every character", unbroken.Sum(c => c.Length) == 5000);

            check("the endpoint default is loopback", OllamaSummarizer.NormalizeEndpoint("") == OllamaSummarizer.DefaultEndpoint);
            check("a trailing slash is trimmed", OllamaSummarizer.NormalizeEndpoint("http://host:1/") == "http://host:1");
            check("a generate reply is read", OllamaSummarizer.ExtractResponse("{\"response\":\"done\"}") == "done");
            check("a malformed reply reads as empty", OllamaSummarizer.ExtractResponse("{oops") == "");
            // ---- pulling a model: the recommended list and the progress stream ----
            check("every recommended tag carries a size in its label",
                OllamaSummarizer.RecommendedDisplays().All(d => d.Contains("GB")));
            check("the default recommendation is one of the offered rows",
                OllamaSummarizer.RecommendedDisplays().Any(
                    d => d.StartsWith(OllamaSummarizer.DefaultRecommendedId, StringComparison.Ordinal)));
            check("WITNESS a label round-trips to the tag that gets pulled",
                OllamaSummarizer.RecommendedIdFromDisplay(
                    OllamaSummarizer.RecommendedDisplayFor("gemma3:4b")) == "gemma3:4b");
            check("WITNESS unrecognised text falls back rather than being sent as a model name",
                OllamaSummarizer.RecommendedIdFromDisplay("something the user typed")
                    == OllamaSummarizer.DefaultRecommendedId);
            // Both fallbacks name the SAME model, by lookup: the display side indexed row 2 of a table ordered
            // smallest-first, so a row inserted ahead of the default would have split them (RA-152).
            check("the display fallback is the default recommendation, looked up rather than a row number",
                OllamaSummarizer.RecommendedDisplayFor("nonsense:1b")
                    == OllamaSummarizer.RecommendedDisplayFor(OllamaSummarizer.DefaultRecommendedId)
                && OllamaSummarizer.RecommendedIdFromDisplay(OllamaSummarizer.RecommendedDisplayFor("nonsense:1b"))
                    == OllamaSummarizer.DefaultRecommendedId);
            check("the Ollama link is https", OllamaSummarizer.OllamaDownloadUrl.StartsWith("https://"));

            // Percent is the half that misleads when it is wrong, so it is pinned at both ends.
            check("WITNESS no total means no percentage, not 100%",
                OllamaSummarizer.ProgressLine("m", "pulling manifest", 0, 0) == "m: pulling manifest");
            check("a partial download reports its share",
                OllamaSummarizer.ProgressLine("m", "downloading", 3758096384L, 7516192768L)
                    .Contains("50%"));
            check("the size is shown in GB alongside the percentage",
                OllamaSummarizer.ProgressLine("m", "downloading", 3758096384L, 7516192768L)
                    .Contains("3.5 of 7.0 GB"));
            check("a server overshoot is clamped rather than printing 103%",
                OllamaSummarizer.ProgressLine("m", "downloading", 900L, 800L).Contains("100%"));

            OllamaSummarizer.PullProgress step = OllamaSummarizer.ParsePullLine(
                "{\"status\":\"downloading\",\"completed\":50,\"total\":100}");
            check("a progress line is read",
                step != null && step.Completed == 50 && step.Total == 100);
            check("WITNESS an error line is surfaced, not read as progress",
                OllamaSummarizer.ParsePullLine("{\"error\":\"model not found\"}").Error == "model not found");
            check("a blank line is skipped rather than failing the pull",
                OllamaSummarizer.ParsePullLine("") == null && OllamaSummarizer.ParsePullLine("  ") == null);
            check("a torn line is skipped rather than throwing",
                OllamaSummarizer.ParsePullLine("{\"status\":\"down") == null);
            check("the summary file header names the model",
                OllamaSummarizer.FileHeader("Standup", "dolphin3").Contains("dolphin3"));

            // ---- options pane: a value the user picks must survive Apply ----
            // Reported from a real install: choose a recording device, hit Apply, reopen the pane, and
            // the dropdown is blank again. Nothing here drove Load -> Save -> Load, so a pane that lost
            // a field on the way through looked exactly like a working one. Every id in the schema is
            // round-tripped rather than the two that were reported, because the next one to go is not
            // going to be one of those two.
            Func<IReadOnlyDictionary<string, string>, string, string> valueOf = (d, k) =>
            {
                string got;
                return (d != null && d.TryGetValue(k, out got)) ? (got ?? "") : "";
            };

            string paneScratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-pane-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var paneHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                // Seeded BEFORE Init on purpose. Init itself no longer purges (F180), but the purge timer's
                // first tick is 60 s out and its root is whatever the settings answer -- the user's real
                // Documents\Remembrance by default -- so a test process that lived long enough to pump
                // messages must still not be one tick from deleting there. (This comment said "Init runs a
                // purge" until R-039, which had stopped being true the day F180 landed.)
                paneHost.SettingsFor(Id).Set("storageLocation", paneScratch);
                // A non-empty cache short-circuits the discovery probe in Load. Without this the
                // suite would reach 127.0.0.1:11434 and score differently on a machine with Ollama
                // running than on one without -- and this module's tests are deliberately offline.
                paneHost.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b|beta:7b");
                // A non-empty whisperExe short-circuits AutoDetectWhisperOnce in Load the same way: without it
                // the pane build walked this machine's real whisper roots (CODEX_TOOLBOX / DevToolbox, every
                // file under them) and the round trip's values then carried that machine's paths (RA-164).
                paneHost.SettingsFor(Id).Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                var paneModule = new RemembranceModule();
                // The device cache is dropped first, so an Init that asks for the devices has to WALK them:
                // with the 1500 ms cache still warm from the link block above, a re-introduced enumeration
                // answered from the cache and the walk count below could not move (the mutation survived).
                AudioDevices.ForgetCachedDevices();
                int walksBeforeInit = AudioDevices.EnumerationCount;
                paneModule.Init(paneHost);
                check("the module registers exactly one options pane", paneHost.OptionsPanes.Count == 1);
                // Init used to walk the WASAPI endpoints twice, on the UI thread, for Options arrays that Load
                // rebuilds before anyone sees them (F178); and it used to purge, from every headless test that
                // loaded it, over whatever root the settings answered -- the user's real folder by default
                // (F180). Both are asserted ABSENT here and PRESENT where they belong, just below.
                check("Init enumerates no WASAPI endpoints", AudioDevices.EnumerationCount == walksBeforeInit);
                check("Init starts no purge", paneModule.PurgesStartedForSelfTest == 0);
                check("WITNESS the purge timer's first tick is a minute out, not an hour",
                    paneModule.PurgeIntervalForSelfTest == 60 * 1000);
                paneModule.PurgeTickForSelfTest();
                check("WITNESS the first tick purges and re-arms the timer hourly",
                    paneModule.PurgesStartedForSelfTest == 1 && paneModule.PurgeIntervalForSelfTest == PurgeIntervalMs);
                // LaunchProcess gates nothing at runtime -- the module starts whisper-cli itself -- so this
                // line is the only thing that notices the disclosure gone (F226).
                check("declares the permissions it uses, including the LaunchProcess disclosure for whisper-cli",
                    paneModule.Info.Permissions.HasFlag(ModulePermissions.LaunchProcess)
                    && paneModule.Info.Permissions.HasFlag(ModulePermissions.Microphone)
                    && paneModule.Info.Permissions.HasFlag(ModulePermissions.SystemAudio));

                OptionsPane pane = paneHost.OptionsPanes.Count > 0 ? paneHost.OptionsPanes[0] : null;
                check("the pane persists (it has both a Load and a Save)",
                    pane != null && pane.Load != null && pane.Save != null);

                if (pane != null && pane.Load != null && pane.Save != null)
                {
                    // An edit to every writable field at once, each with a value nothing else could
                    // produce, so a field that comes back holding someone else's value is visible too.
                    var edited = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> kv in pane.Load()) edited[kv.Key] = kv.Value;
                    check("WITNESS a pane build (Load) is what enumerates the endpoints",
                        AudioDevices.EnumerationCount > walksBeforeInit);
                    edited["sysDevice"] = "Test Speakers";
                    edited["micDevice"] = "Test Microphone";
                    edited["recordHotkey"] = "Ctrl+Alt+9";
                    edited["snapshotHotkey"] = "Ctrl+Alt+8";
                    edited["summaryOn"] = "true";
                    edited["summaryModel"] = "test-model:1b";
                    edited["ollamaEndpoint"] = "http://127.0.0.1:99999";
                    edited[FolderLayout.SettingKey] = "Create a folder by date";
                    // The seven the round trip did not reach before 2.0.0 (it asserted eight of fifteen keys).
                    string editedStorage = System.IO.Path.Combine(paneScratch, "edited-root");
                    string tinyDisplay = WhisperInstaller.Models.First(m => m.Id == "ggml-tiny.en.bin").Display;
                    string qwenDisplay = OllamaSummarizer.RecommendedDisplayFor("qwen3:8b");
                    edited["sysEnabled"] = "false";
                    edited["micEnabled"] = "false";
                    edited["storageLocation"] = editedStorage;
                    edited["whisperExe"] = @"c:\edited\whisper-cli.exe";
                    edited["whisperModel"] = @"c:\edited\ggml-tiny.en.bin";
                    edited["whisperModelChoice"] = tinyDisplay;
                    edited["recommendedModel"] = qwenDisplay;
                    check("Apply reports success", pane.Save(edited));

                    IReadOnlyDictionary<string, string> reopened = pane.Load();
                    check("WITNESS a chosen output device survives Apply",
                        valueOf(reopened, "sysDevice") == "Test Speakers");
                    check("WITNESS a chosen microphone survives Apply",
                        valueOf(reopened, "micDevice") == "Test Microphone");
                    check("a typed start/stop hotkey survives Apply",
                        valueOf(reopened, "recordHotkey") == "Ctrl+Alt+9");
                    check("a typed snapshot hotkey survives Apply",
                        valueOf(reopened, "snapshotHotkey") == "Ctrl+Alt+8");
                    check("the summary toggle survives Apply", valueOf(reopened, "summaryOn") == "true");
                    check("WITNESS a chosen summary model survives Apply",
                        valueOf(reopened, "summaryModel") == "test-model:1b");
                    check("a typed Ollama address survives Apply",
                        valueOf(reopened, "ollamaEndpoint") == "http://127.0.0.1:99999");
                    check("choosing Create a folder by date survives Apply, stored under folderLayout as the id \"date\"",
                        valueOf(reopened, FolderLayout.SettingKey) == "Create a folder by date"
                        && paneHost.SettingsFor(Id).Get(FolderLayout.SettingKey, "") == FolderLayout.ByDate);
                    var lost = new List<string>();
                    if (valueOf(reopened, "sysEnabled") != "false") lost.Add("sysEnabled");
                    if (valueOf(reopened, "micEnabled") != "false") lost.Add("micEnabled");
                    if (valueOf(reopened, "storageLocation") != editedStorage) lost.Add("storageLocation");
                    if (valueOf(reopened, "whisperExe") != @"c:\edited\whisper-cli.exe") lost.Add("whisperExe");
                    if (valueOf(reopened, "whisperModel") != @"c:\edited\ggml-tiny.en.bin") lost.Add("whisperModel");
                    if (valueOf(reopened, "whisperModelChoice") != tinyDisplay
                        || paneHost.SettingsFor(Id).Get("whisperModelChoice", "") != "ggml-tiny.en.bin") lost.Add("whisperModelChoice");
                    if (valueOf(reopened, "recommendedModel") != qwenDisplay
                        || paneHost.SettingsFor(Id).Get("recommendedModel", "") != "qwen3:8b") lost.Add("recommendedModel");
                    check("every other writable field survives Apply, each label stored as its id; lost: "
                          + (lost.Count == 0 ? "none" : string.Join(", ", lost)), lost.Count == 0);

                    // A saved model the discovery pass has never seen must still be OFFERED, or the
                    // closed dropdown renders blank and the next Apply writes that blank back.
                    IReadOnlyList<SettingField> schema = pane.Schema;
                    SettingField modelField = null;
                    if (schema != null)
                        foreach (SettingField f in schema)
                            if (f != null && f.Id == "summaryModel") modelField = f;
                    check("WITNESS the saved summary model is one of the offered options",
                        modelField != null && modelField.Options != null &&
                        modelField.Options.Contains("test-model:1b"));
                }

                paneModule.Shutdown();
            }
            catch (Exception ex) { check("options pane round-trip: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(paneScratch)) System.IO.Directory.Delete(paneScratch, true); }
                catch { }
            }

            // ---- nothing discovered: a label, never a blank box, and never a stored model ----
            // Reported as "summary model still shows nothing in the dropdown". An empty closed
            // dropdown reads as a broken control; the placeholder says which of the two it is.
            // The discovery probe Load fires on an empty cache goes through the ListModels seam here and
            // answers "nothing", so this stays an offline test. It used to point the endpoint at loopback
            // port 1 instead, on the comment that a refused port answers "instantly": the module's own
            // measurement (AutoDiscoverModelsOnce) is ~2 s for a refused localhost connect, and the probe
            // ran on past Shutdown on a pool thread (RA-164).
            string emptyScratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-empty-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            try
            {
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string>()); };
                var emptyHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                emptyHost.SettingsFor(Id).Set("storageLocation", emptyScratch);
                emptyHost.SettingsFor(Id).Set("whisperExe", @"c:\seeded\whisper-cli.exe");   // see the pane block above (RA-164)
                var emptyModule = new RemembranceModule();
                emptyModule.Init(emptyHost);
                OptionsPane emptyPane = emptyHost.OptionsPanes[0];

                IReadOnlyDictionary<string, string> shown = emptyPane.Load();
                SettingField modelField = null;
                foreach (SettingField f in emptyPane.Schema)
                    if (f != null && f.Id == "summaryModel") modelField = f;

                check("WITNESS an undiscovered dropdown offers a label, not an empty row",
                    modelField != null && modelField.Options != null &&
                    modelField.Options.Length == 1 && modelField.Options[0] == NoModelsPlaceholder);
                check("...and the pane shows that label rather than nothing",
                    valueOf(shown, "summaryModel") == NoModelsPlaceholder);

                // The trap the placeholder introduces: it is a sentence, and /api/generate would
                // take it for a model tag.
                var applied = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in shown) applied[kv.Key] = kv.Value;
                emptyPane.Save(applied);
                check("WITNESS applying the placeholder stores no model, not the label",
                    emptyHost.SettingsFor(Id).Get("summaryModel", "MISSING") == "");

                emptyModule.Shutdown();
            }
            catch (Exception ex) { check("empty-discovery pane: " + ex.Message, false); }
            finally
            {
                ListModels = savedLister;
                try { if (System.IO.Directory.Exists(emptyScratch)) System.IO.Directory.Delete(emptyScratch, true); }
                catch { }
            }

            // ---- the recorder's stop path, with a fake capture that reproduces NAudio's threading (BUG-009) ----
            string recorderScratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-recorder-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try { RecorderSelfCheck.Run(check, recorderScratch); }
            catch (Exception ex) { check("recorder stop path: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(recorderScratch)) System.IO.Directory.Delete(recorderScratch, true); }
                catch { }
            }

            SelfCheckStopPaths(check);
            SelfCheckStopOutcomes(check);
            SelfCheckFlushSingleShot(check);
            SelfCheckSnapshotOffThread(check);
            SelfCheckSummaryCoverage(check);
            SelfCheckWhisperLimit(check);
            SelfCheckLookupBound(check);
            SelfCheckDownloadCleanup(check);
            SelfCheckDownloadBounds(check);
            SelfCheckPullBounds(check);
            SelfCheckPullGate(check);
            SelfCheckSaveReporting(check);
            SelfCheckPendingActions(check);
            SelfCheckApplyKeepsBackgroundWrites(check);
            SelfCheckSummaryButtons(check);
            SelfCheckWhisperButtons(check);
            SelfCheckFolderLayout(check);
            SelfCheckFolderLayoutInModule(check);
            SelfCheckBusyFlag(check);
            // Lane feature/cli-backend (2.1.0): the coding-agent CLI runner this payload ships, through its fake CLI (no
            // process is started), then the summary's route through it.
            DesktopAICompanion.CodingAgent.CodingAgentCliSelfCheck.Run(check);

            detail = sb.ToString();
            return ok;
        }

        /// <summary>
        /// The module's own stop paths, driven through fake devices: the shutdown save (its timing line, F168;
        /// nothing announced, F175) and the flush of a save that a normal stop left in flight (F176). The test
        /// thread stands in for the UI thread with a queueing context, so every Announce the module posts runs
        /// here, on this thread, when the check drains it.
        /// </summary>
        private static void SelfCheckStopPaths(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-stop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            CapturePaths flushed = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);

                // ---- exit WHILE recording: the save is synchronous, timed, logged, and not announced ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.SettingsFor(Id).Set("storageLocation", scratch);
                    host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    var module = new RemembranceModule();
                    module.Init(host);
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    check("WITNESS a recording starts against the fake devices and is announced",
                        module.IsRecordingForSelfTest
                        && host.SaidLines.Any(l => l.StartsWith("Recording started", StringComparison.Ordinal)));
                    CapturePaths paths = module.LastCaptureForSelfTest;
                    Thread.Sleep(120);   // a few buffers on each track, so the mix has something to write
                    // The keep-alive dies and the microphone's capture thread dies mid-recording (RA-150): both
                    // are stood in for on the fakes, and both must reach the log at the stop.
                    devices.KeepAlives[devices.KeepAlives.Count - 1].Failure = "simulated: AUDCLNT_E_DEVICE_INVALIDATED";
                    devices.Captures.Last(f => !f.Loopback).StopException =
                        new InvalidOperationException("simulated: the capture thread died");
                    int saidBefore = host.SaidLines.Count;
                    host.RaiseHostShutdown();
                    int postedDuringShutdown = ui.Pending;
                    ui.Drain();
                    check("the shutdown save is synchronous: the recording is on disk when the shutdown event returns",
                        !module.IsRecordingForSelfTest && paths != null && System.IO.File.Exists(paths.Audio));
                    check("nothing is announced on the shutdown path, where no message loop is left to speak through",
                        postedDuringShutdown == 0 && host.SaidLines.Count == saidBefore);
                    check("the shutdown save logs how long the capture stop and the mix took",
                        host.LoggedLines.Any(l => l.Contains("stopped on shutdown") && l.Contains("capture stop ")
                                                  && l.Contains(" ms")));
                    check("a keep-alive stream that stopped mid-recording is logged at the stop, with the reason",
                        host.LoggedLines.Any(l => l.Contains("keep-alive stream") && l.Contains("stopped before the recording did")
                                                  && l.Contains("DEVICE_INVALIDATED")));
                    check("a capture whose thread died mid-recording is logged at the stop, naming the source",
                        host.LoggedLines.Any(l => l.Contains("microphone capture stopped with an error")
                                                  && l.Contains("capture thread died")));
                    module.Shutdown();
                }

                // ---- exit inside the save window after NORMAL stops: shutdown waits for EVERY save ----
                // Two stops inside one mix window, the shape R-038 found untracked: stop, start again, stop.
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    devices.StopGate.Reset();   // every fake holds its RecordingStopped until the gate opens
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.SettingsFor(Id).Set("storageLocation", scratch);
                    host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    var module = new RemembranceModule();
                    module.Init(host);
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(60);
                    module.StopRecordingForSelfTest();
                    ui.Drain();
                    CapturePaths firstStop = module.LastCaptureForSelfTest;
                    check("WITNESS the normal stop announces; the shutdown path's silence is the exception",
                        host.SaidLines.Any(l => l.StartsWith("Recording stopped", StringComparison.Ordinal)));
                    Task[] afterOne = module.PendingSavesForSelfTest;
                    check("a normal stop hands the save to a task the module keeps hold of",
                        afterOne.Length == 1 && !afterOne[0].IsCompleted);
                    Thread.Sleep(1100);   // a distinct second, so the second capture gets its own folder
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(60);
                    module.StopRecordingForSelfTest();
                    ui.Drain();
                    flushed = module.LastCaptureForSelfTest;
                    Task[] pending = module.PendingSavesForSelfTest;
                    check("a second stop inside the first save's window is tracked BESIDE the first, not instead of it",
                        pending.Length == 2 && pending.All(t => !t.IsCompleted)
                        && firstStop != null && flushed != null && firstStop.Audio != flushed.Audio);
                    // The captures are released a little after shutdown starts waiting, from another thread.
                    var releaser = new Thread(() => { Thread.Sleep(300); devices.StopGate.Set(); }) { IsBackground = true };
                    releaser.Start();
                    var stopwatch = Stopwatch.StartNew();
                    host.RaiseHostShutdown();
                    stopwatch.Stop();
                    releaser.Join();
                    check("shutdown waits for a save still in flight rather than leaving it to die with the process "
                          + "(waited " + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        pending.All(t => t.IsCompleted) && System.IO.File.Exists(flushed.Audio) && System.IO.File.Exists(firstStop.Audio));
                    check("...and says so in the log, both when it starts waiting and when the saves land",
                        host.LoggedLines.Any(l => l.Contains("2 recordings are still being saved"))
                        && host.LoggedLines.Any(l => l.Contains("the saves finished after")));
                    module.Shutdown();
                }
            }
            catch (Exception ex) { check("stop paths: " + ex.Message, false); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                // The pool task goes on to write the stub transcript after the mix; let it land before the
                // folder goes, so the cleanup does not race it.
                if (flushed != null)
                    SpinWait.SpinUntil(delegate { return System.IO.File.Exists(flushed.Transcript); }, TimeSpan.FromSeconds(5));
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// The two stop outcomes a save can have besides "saved", each said in the log for what it is (R-038):
        /// a stop before any packet arrived captured NOTHING and writes no recording.wav, and a stop whose mix
        /// failed is a FAILED save, not a finished one, when shutdown reports the flush.
        /// </summary>
        private static void SelfCheckStopOutcomes(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-outcome-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);

                // ---- nothing captured ----
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    devices.FirstPacketGate.Reset();   // no fake delivers a packet: both scratch tracks stay header-only
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.SettingsFor(Id).Set("storageLocation", scratch);
                    host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    var module = new RemembranceModule();
                    module.Init(host);
                    module.StartRecordingForSelfTest();
                    module.StopRecordingForSelfTest();
                    Task[] pending = module.PendingSavesForSelfTest;
                    bool landed = pending.Length == 1 && pending[0].Wait(TimeSpan.FromSeconds(10));
                    ui.WaitForPost(TimeSpan.FromSeconds(3));
                    ui.Drain();
                    CapturePaths paths = module.LastCaptureForSelfTest;
                    check("WITNESS the empty stop's save task completes, with 'nothing captured' as its outcome",
                        landed && pending[0].Status == TaskStatus.RanToCompletion && !((Task<bool>)pending[0]).Result);
                    check("a stop that captured nothing says so in the log and writes no recording.wav",
                        host.LoggedLines.Any(l => l.Contains("nothing was captured"))
                        && !host.LoggedLines.Any(l => l.Contains("audio saved as"))
                        && paths != null && !System.IO.File.Exists(paths.Audio));
                    check("...and announces that, not 'Transcript ready'",
                        host.SaidLines.Contains("Recording stopped, but nothing was captured.")
                        && !host.SaidLines.Any(l => l.StartsWith("Transcript", StringComparison.Ordinal))
                        && !host.SaidLines.Any(l => l.StartsWith("Recording saved", StringComparison.Ordinal)));
                    module.Shutdown();
                }

                // ---- the mix fails WHILE shutdown is waiting for it ----
                // The shape R-038 named: recorder.Stop() throws inside the pool task at exit, and the flush
                // used to log "the save finished". The gate holds the save until the flush is waiting; a
                // DIRECTORY sits where recording.wav must be written, so once the captures stop the mix's
                // WaveFileWriter cannot create the file, Stop throws, and the save faults under the flush.
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    devices.StopGate.Reset();
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.SettingsFor(Id).Set("storageLocation", scratch);
                    host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    var module = new RemembranceModule();
                    module.Init(host);
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(60);
                    CapturePaths paths = module.LastCaptureForSelfTest;
                    System.IO.Directory.CreateDirectory(paths.Audio);
                    module.StopRecordingForSelfTest();
                    ui.Drain();
                    Task[] pending = module.PendingSavesForSelfTest;
                    var releaser = new Thread(() => { Thread.Sleep(300); devices.StopGate.Set(); }) { IsBackground = true };
                    releaser.Start();
                    host.RaiseHostShutdown();
                    releaser.Join();
                    ui.Drain();
                    check("WITNESS the save whose mix failed is a faulted task, and the stop logged its own failure",
                        pending.Length == 1 && pending[0].IsFaulted
                        && host.LoggedLines.Any(l => l.Contains("stop/transcribe failed")));
                    check("a save that faults while shutdown waits for it is reported as failed, never as finished",
                        host.LoggedLines.Any(l => l.Contains("the save failed after"))
                        && !host.LoggedLines.Any(l => l.Contains("the save finished after")));
                    module.Shutdown();
                }
            }
            catch (Exception ex) { check("stop outcomes: " + ex.Message, false); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>A flush that waited out SaveFlushBound is single-shot: the other shutdown hook on the same
        /// exit returns at once instead of waiting the whole bound again (RA-155). The bound is shortened
        /// for the check and put back.</summary>
        private static void SelfCheckFlushSingleShot(Action<string, bool> check)
        {
            TimeSpan savedBound = SaveFlushBound;
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-flush-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            Task[] pending = new Task[0];
            CapturePaths paths = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                SaveFlushBound = TimeSpan.FromMilliseconds(300);
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    devices.StopGate.Reset();   // the save cannot complete until the gate opens
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.SettingsFor(Id).Set("storageLocation", scratch);
                    host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    var module = new RemembranceModule();
                    module.Init(host);
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(60);
                    module.StopRecordingForSelfTest();
                    ui.Drain();
                    paths = module.LastCaptureForSelfTest;
                    pending = module.PendingSavesForSelfTest;
                    var first = Stopwatch.StartNew();
                    host.RaiseHostShutdown();
                    first.Stop();
                    check("WITNESS the first shutdown hook waits out the bound and gives up ("
                          + first.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms of a 300 ms bound)",
                        first.ElapsedMilliseconds >= 250
                        && host.LoggedLines.Count(l => l.Contains("gave up waiting for the save")) == 1);
                    var second = Stopwatch.StartNew();
                    module.Shutdown();
                    second.Stop();
                    check("the second hook on the same exit does not wait the bound a second time ("
                          + second.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        second.ElapsedMilliseconds < 150
                        && host.LoggedLines.Count(l => l.Contains("gave up waiting for the save")) == 1
                        && host.LoggedLines.Any(l => l.Contains("not waiting again")));
                    devices.StopGate.Set();
                }
            }
            catch (Exception ex) { check("flush single-shot: " + ex.Message, false); }
            finally
            {
                SaveFlushBound = savedBound;
                SynchronizationContext.SetSynchronizationContext(previous);
                // Let the released save and its stub transcript land before the folder goes.
                try { Task.WaitAll(pending, TimeSpan.FromSeconds(15)); } catch { }
                if (paths != null)
                    SpinWait.SpinUntil(delegate { return System.IO.File.Exists(paths.Transcript); }, TimeSpan.FromSeconds(5));
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>The snapshot hotkey's work leaves the calling thread, one capture at a time (F177, F181).
        /// Same probe shape as Reminder's calendar-read check: the capture is held open on a gate, so the
        /// hotkey must return while it is still running and on a different thread.</summary>
        private static void SelfCheckSnapshotOffThread(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-snap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<string, bool> savedCapture = SnapshotCapture;
            Func<DateTimeOffset> savedClock = SnapshotClock;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            int captureThread = 0;
            int calls = 0;
            string capturedPath = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                SnapshotCapture = delegate(string png)
                {
                    Interlocked.Increment(ref calls);
                    captureThread = Environment.CurrentManagedThreadId;
                    capturedPath = png;
                    started.Set();
                    // THE TIMEOUT IS LOAD-BEARING. Under the F177 mutation (Task.Run inlined) this probe runs on
                    // the hotkey's thread and nothing else ever Sets release; the 5 s is what turns that into a
                    // FAIL below rather than a hang the harness scores BROKEN. Do not tidy it into Wait() (R-040).
                    release.Wait(TimeSpan.FromSeconds(5));
                    return true;
                };
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.SettingsFor(Id).Set("storageLocation", scratch);
                host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                var module = new RemembranceModule();
                module.Init(host);
                int caller = Environment.CurrentManagedThreadId;

                module.TakeSnapshotForSelfTest();
                // `ui.Pending == 0` is the observable: no Announce has reached the caller's queue when the hotkey
                // returns. The `!release.IsSet` conjunct that stood beside it was a tautology -- nothing Sets
                // release before this line -- and carried no information (R-040).
                check("the snapshot hotkey returns before the capture completes (the encode is not on the caller's thread)",
                    ui.Pending == 0);
                bool began = started.Wait(TimeSpan.FromSeconds(3));
                check("WITNESS the background capture did start", began);
                check("the capture ran on a different thread from the hotkey",
                    began && captureThread != 0 && captureThread != caller);
                // The label says what the condition tests and no more: the root and the suffix. Its old wording
                // claimed the path was decided on the hotkey's thread, which nothing here observes (RA-165).
                check("the path handed to the capture lands under the storage root as a .png",
                    capturedPath != null && capturedPath.StartsWith(scratch, StringComparison.OrdinalIgnoreCase)
                    && capturedPath.EndsWith(".png", StringComparison.Ordinal));

                module.TakeSnapshotForSelfTest();
                ui.Drain();
                check("a second press while one is still encoding starts no second capture",
                    Interlocked.CompareExchange(ref calls, 0, 0) == 1);
                check("...and is told so", host.SaidLines.Contains("Still saving the last snapshot."));

                release.Set();
                bool announced = ui.WaitForPost(TimeSpan.FromSeconds(3));
                ui.Drain();
                check("the result is announced once the capture lands",
                    announced && host.SaidLines.Contains("Snapshot saved."));

                module.TakeSnapshotForSelfTest();
                bool again = SpinWait.SpinUntil(
                    delegate { return Interlocked.CompareExchange(ref calls, 0, 0) == 2; }, TimeSpan.FromSeconds(3));
                check("WITNESS once the first has landed, the next press captures again", again);
                ui.WaitForPost(TimeSpan.FromSeconds(3));
                ui.Drain();

                // ---- two presses inside ONE second keep both snapshots (RA-156) ----
                // The clock is pinned so the two stamps provably collide, and the probe writes a file the way
                // Bitmap.Save would, so the second path decision sees the first file on disk.
                var pinned = new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Local));
                SnapshotClock = delegate { return pinned; };
                var written = new System.Collections.Concurrent.ConcurrentQueue<string>();
                SnapshotCapture = delegate(string png)
                {
                    System.IO.File.WriteAllBytes(png, new byte[1]);
                    written.Enqueue(png);
                    return true;
                };
                for (int press = 0; press < 2; press++)
                {
                    int before = written.Count;
                    module.TakeSnapshotForSelfTest();
                    SpinWait.SpinUntil(delegate { return written.Count > before; }, TimeSpan.FromSeconds(3));
                    ui.WaitForPost(TimeSpan.FromSeconds(3));
                    ui.Drain();
                }
                string[] both = written.ToArray();
                check("two presses inside the same second write two files, the second suffixed ' (2)'",
                    both.Length == 2
                    && both[0].EndsWith("snap 2026-08-27 09-30-00.png", StringComparison.Ordinal)
                    && both[1].EndsWith("snap 2026-08-27 09-30-00 (2).png", StringComparison.Ordinal)
                    && System.IO.File.Exists(both[0]) && System.IO.File.Exists(both[1]));
                module.Shutdown();
            }
            catch (Exception ex) { check("snapshot off-thread: " + ex.Message, false); }
            finally
            {
                SnapshotCapture = savedCapture;
                SnapshotClock = savedClock;
                release.Set();
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>A map-reduce that stops folding before the last part says so, in the text and in the
        /// message the caller logs; a model that runs out of time is not reported as the user cancelling
        /// (F173). The model is a delegate here, so no server is involved.</summary>
        private static void SelfCheckSummaryCoverage(Action<string, bool> check)
        {
            try
            {
                string longTranscript = string.Join("\n", Enumerable.Repeat("a line of meeting talk", 8000));
                int total = OllamaSummarizer.Chunk(longTranscript, OllamaSummarizer.MaxChunkCharacters).Count;
                int mapCalls = 0, reduceCalls = 0;
                Func<string, Task<string>> verboseModel = delegate(string prompt)
                {
                    if (prompt.StartsWith("Below are notes", StringComparison.Ordinal))
                    {
                        reduceCalls++;
                        return Task.FromResult("merged summary");
                    }
                    mapCalls++;
                    // 900-character notes fill the 8000-character merge input after nine parts.
                    return Task.FromResult(new string('n', 900));
                };
                OllamaSummarizer.SummaryResult partial = OllamaSummarizer.SummarizeWithAsync(
                    verboseModel, "Standup", longTranscript, null, CancellationToken.None).GetAwaiter().GetResult();
                check("WITNESS the fold stopped before the last part (" + mapCalls.ToString(CultureInfo.InvariantCulture)
                      + " of " + total.ToString(CultureInfo.InvariantCulture) + " parts folded, one merge)",
                    partial.Ok && total > 1 && mapCalls < total && reduceCalls == 1);
                string expected = "Covers parts 1-" + mapCalls.ToString(CultureInfo.InvariantCulture) + " of "
                                  + total.ToString(CultureInfo.InvariantCulture);
                check("a partial fold says which parts it covers, in the summary text",
                    partial.Text != null && partial.Text.Contains(expected));
                check("...and in the message the caller logs",
                    partial.Message != null && partial.Message.Contains(expected));

                int terseMaps = 0;
                Func<string, Task<string>> terseModel = delegate(string prompt)
                {
                    if (!prompt.StartsWith("Below are notes", StringComparison.Ordinal)) terseMaps++;
                    return Task.FromResult("note");
                };
                OllamaSummarizer.SummaryResult whole = OllamaSummarizer.SummarizeWithAsync(
                    terseModel, "Standup", longTranscript, null, CancellationToken.None).GetAwaiter().GetResult();
                check("WITNESS a fold that reached every part carries no coverage note",
                    whole.Ok && terseMaps == total && whole.Text != null && !whole.Text.Contains("Covers parts")
                    && string.IsNullOrEmpty(whole.Message));

                Func<string, Task<string>> stalledModel = delegate(string prompt)
                {
                    return Task.FromException<string>(new TaskCanceledException("simulated: the client's timeout fired"));
                };
                OllamaSummarizer.SummaryResult timedOut = OllamaSummarizer.SummarizeWithAsync(
                    stalledModel, "Standup", "a short transcript", null, CancellationToken.None).GetAwaiter().GetResult();
                check("a model that ran out of time is reported as a timeout, not as the user cancelling",
                    !timedOut.Ok && timedOut.Message != null && timedOut.Message.Contains("did not answer")
                    && !timedOut.Message.Contains("cancelled"));
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    OllamaSummarizer.SummaryResult cancelled = OllamaSummarizer.SummarizeWithAsync(
                        stalledModel, "Standup", "a short transcript", null, cts.Token).GetAwaiter().GetResult();
                    check("WITNESS a cancelled token is reported as cancelled",
                        !cancelled.Ok && cancelled.Message != null && cancelled.Message.Contains("cancelled"));
                }
            }
            catch (Exception ex) { check("summary coverage: " + ex.Message, false); }
        }

        /// <summary>whisper-cli's time limit follows the recording (F182).</summary>
        private static void SelfCheckWhisperLimit(Action<string, bool> check)
        {
            check("a short recording keeps the old 30-minute floor",
                Transcriber.WhisperTimeoutFor(TimeSpan.FromMinutes(5)) == Transcriber.MinimumWhisperTimeout);
            check("an unreadable length is treated as unknown, not as short",
                Transcriber.WhisperTimeoutFor(TimeSpan.Zero) == Transcriber.MinimumWhisperTimeout);
            check("WITNESS an hour of audio gets four hours, not thirty minutes",
                Transcriber.WhisperTimeoutFor(TimeSpan.FromHours(1)) == TimeSpan.FromHours(4));
            check("a two-hour recording is capped at the six-hour ceiling, so a wedged whisper-cli still dies",
                Transcriber.WhisperTimeoutFor(TimeSpan.FromHours(2)) == Transcriber.MaximumWhisperTimeout);

            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-wav-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(scratch);
                string wav = System.IO.Path.Combine(scratch, "two-seconds.wav");
                System.IO.File.WriteAllBytes(wav, ModuleKit.WavAudio.FromPcm(new short[32000], 16000, 1));
                TimeSpan read = Transcriber.TryReadDuration(wav);
                check("the audio length is read off the WAV header ("
                      + read.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s)",
                    Math.Abs(read.TotalSeconds - 2.0) < 0.01);
                check("a file that is not there reads as an unknown length",
                    Transcriber.TryReadDuration(System.IO.Path.Combine(scratch, "missing.wav")) == TimeSpan.Zero);

                // ---- the transcript header says when the recording STARTED (RA-166) ----
                // Whisper is not configured here, so Transcribe writes its stub; the header is the same either way.
                var startedAt = new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Local));
                bool ran;
                string stub = Transcriber.Transcribe(wav, System.IO.Path.Combine(scratch, "two-seconds.transcript.txt"),
                    "", "", "Standup", null, startedAt, out ran);
                check("the transcript header's 'Recorded:' is the capture's start, not the clock at transcription",
                    !ran && stub.Contains("Recorded: " + startedAt.ToString("f")) && stub.Contains("Transcribed: "));
                check("WITNESS a file with no known start says so instead of printing the clock",
                    Transcriber.RecordedLine(null).Contains("unknown")
                    && !Transcriber.RecordedLine(null).Contains(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) { check("wav length: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }

            // ---- the kill message tells the truth about what it knows (R-041) ----
            string unknownKill = Transcriber.DescribeKill(Transcriber.MinimumWhisperTimeout, TimeSpan.Zero);
            check("an unreadable WAV length is reported as unreadable, with the floor that applied, never as '0 minutes long'",
                unknownKill.Contains("could not be read") && unknownKill.Contains("30-minute limit")
                && !unknownKill.Contains("0 minutes long"));
            string floorKill = Transcriber.DescribeKill(Transcriber.MinimumWhisperTimeout, TimeSpan.FromMinutes(5));
            check("WITNESS a short recording's message names its length and says the floor applied",
                floorKill.Contains("5 minutes long") && floorKill.Contains("floor applied"));
            check("a recording under a minute is said in seconds, not rounded to 0 minutes",
                Transcriber.DescribeKill(Transcriber.MinimumWhisperTimeout, TimeSpan.FromSeconds(20)).Contains("20 seconds long"));
            check("WITNESS an hour-long recording's message says 4x applied",
                Transcriber.DescribeKill(TimeSpan.FromHours(4), TimeSpan.FromHours(1)).Contains("4x that."));
            check("a two-hour recording's message says the ceiling applied",
                Transcriber.DescribeKill(Transcriber.MaximumWhisperTimeout, TimeSpan.FromHours(2)).Contains("ceiling applied"));
        }

        /// <summary>A release lookup that gets no answer gives up at its own bound (F184).</summary>
        private static void SelfCheckLookupBound(Action<string, bool> check)
        {
            TimeSpan savedBound = WhisperInstaller.LookupBound;
            try
            {
                WhisperInstaller.LookupBound = TimeSpan.FromMilliseconds(250);
                using (var http = new System.Net.Http.HttpClient(new StalledHandler()))
                {
                    var stopwatch = Stopwatch.StartNew();
                    Task<WhisperInstaller.AssetLookup> lookup = WhisperInstaller.ResolveAssetAsync(http, CancellationToken.None);
                    bool finished = lookup.Wait(TimeSpan.FromSeconds(10));
                    stopwatch.Stop();
                    check("a release lookup that gets no answer gives up at its own bound, not the hour-long client "
                          + "timeout (" + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        finished && lookup.Result.Asset == null && lookup.Result.Failure != null
                        && lookup.Result.Failure.Contains("did not answer"));
                }
                // The converse: the caller's own cancellation is not dressed up as the bound firing.
                WhisperInstaller.LookupBound = TimeSpan.FromSeconds(30);
                using (var http = new System.Net.Http.HttpClient(new StalledHandler()))
                using (var cts = new CancellationTokenSource())
                {
                    Task<WhisperInstaller.AssetLookup> lookup = WhisperInstaller.ResolveAssetAsync(http, cts.Token);
                    cts.Cancel();
                    // The caller's cancellation SURFACES as one, so InstallAsync's own catch says "Setup was
                    // cancelled." The old witness accepted any text that was not the bound's, and the text was
                    // "Could not reach GitHub: A task was canceled." (RA-168).
                    bool ended = false, cancelled = false;
                    try { ended = lookup.Wait(TimeSpan.FromSeconds(10)); }
                    catch (AggregateException ex) { ended = true; cancelled = ex.InnerException is OperationCanceledException; }
                    check("WITNESS the caller's own cancellation surfaces as a cancellation, not as the bound and not as a GitHub failure",
                        ended && cancelled);
                }
            }
            catch (Exception ex) { check("lookup bound: " + ex.Message, false); }
            finally { WhisperInstaller.LookupBound = savedBound; }
        }

        /// <summary>
        /// A download the CALLER cancels leaves no .part behind, and a completed one keeps its file
        /// (N-remembrance-02). Shutdown cancels _installCts mid-install (F184), and until 2026-09-30 only the
        /// download's own idle bound deleted the partial, so a model download the user closed the app on
        /// stayed on disk until the next attempt. Driven against a scripted response body: the first chunk
        /// arrives, the second read blocks until the caller's token fires, so the .part is provably on disk
        /// when the cancel lands.
        /// </summary>
        private static void SelfCheckDownloadCleanup(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-dl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(scratch);
                string destination = System.IO.Path.Combine(scratch, "ggml-probe.bin");
                const string url = "https://example.invalid/ggml-probe.bin";
                var payload = new byte[3 * 65536 + 17];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31);

                // 1. The caller cancels after the first chunk has landed in the .part.
                var stalling = new StallingBody(payload, 65536);
                using (var http = new System.Net.Http.HttpClient(new ScriptedHandler(stalling, payload.Length)))
                using (var cts = new CancellationTokenSource())
                {
                    Task download = WhisperInstaller.DownloadAsync(http, url, destination, delegate { }, cts.Token);
                    bool stalled = stalling.Stalled.Wait(TimeSpan.FromSeconds(10));
                    bool partialOnDisk = System.IO.File.Exists(destination + ".part");
                    cts.Cancel();
                    bool cancelled = false;
                    bool ended;
                    try { ended = download.Wait(TimeSpan.FromSeconds(10)); }
                    catch (AggregateException ex) { ended = true; cancelled = ex.InnerException is OperationCanceledException; }
                    check("WITNESS the partial download was on disk while the download was in flight", stalled && partialOnDisk);
                    check("a download the CALLER cancels surfaces the cancellation, not a stall", ended && cancelled);
                    check("a download the CALLER cancels leaves no .part behind",
                        !System.IO.File.Exists(destination + ".part") && !System.IO.File.Exists(destination));
                }

                // 2. WITNESS: a download that completes keeps its file, byte for byte, and no .part.
                using (var http = new System.Net.Http.HttpClient(new ScriptedHandler(new StallingBody(payload, payload.Length), payload.Length)))
                {
                    var reported = new List<string>();
                    WhisperInstaller.DownloadAsync(http, url, destination, reported.Add, CancellationToken.None)
                        .Wait(TimeSpan.FromSeconds(10));
                    byte[] got = System.IO.File.Exists(destination) ? System.IO.File.ReadAllBytes(destination) : new byte[0];
                    check("WITNESS a completed download keeps its file, byte for byte", got.SequenceEqual(payload));
                    check("WITNESS ...leaves no .part", !System.IO.File.Exists(destination + ".part"));
                    // Progress is reported in 5-point steps, so a report at 95..99 leaves 100 unreported: the
                    // last report is at least 95%, never necessarily 100%.
                    int lastPercent = -1;
                    if (reported.Count > 0)
                    {
                        string last = reported[reported.Count - 1];
                        int colon = last.LastIndexOf(": ", StringComparison.Ordinal);
                        int.TryParse(last.Substring(colon + 2).TrimEnd('%'), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out lastPercent);
                    }
                    check("WITNESS ...and reported its progress against the declared length (last report "
                          + lastPercent.ToString(CultureInfo.InvariantCulture) + "%)",
                        lastPercent >= 95);
                }
            }
            catch (Exception ex) { check("download cleanup: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// DownloadAsync's two bounds fire on SILENCE (F184): no headers within LookupBound, then no bytes within
        /// ReadIdleBound, re-armed per read. Neither had an assertion or a mutation case; only the release
        /// lookup's bound did, so a regression in either was invisible to the suite (R-042). Both shortened for
        /// the check and put back.
        /// </summary>
        private static void SelfCheckDownloadBounds(Action<string, bool> check)
        {
            TimeSpan savedLookup = WhisperInstaller.LookupBound;
            TimeSpan savedIdle = WhisperInstaller.ReadIdleBound;
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-dlbound-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(scratch);
                string destination = System.IO.Path.Combine(scratch, "stalled.bin");
                const string url = "https://example.invalid/stalled.bin";

                // 1. No headers, ever: the header phase is bounded by LookupBound.
                WhisperInstaller.LookupBound = TimeSpan.FromMilliseconds(250);
                WhisperInstaller.ReadIdleBound = TimeSpan.FromSeconds(30);
                using (var http = new System.Net.Http.HttpClient(new StalledHandler()))
                {
                    var stopwatch = Stopwatch.StartNew();
                    Task download = WhisperInstaller.DownloadAsync(http, url, destination, delegate { }, CancellationToken.None);
                    string failure = FailureOf(download, TimeSpan.FromSeconds(10));
                    stopwatch.Stop();
                    check("a download whose headers never arrive gives up at the header bound, in the bound's own words ("
                          + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        failure != null && failure.Contains("stalled") && failure.Contains("no answer")
                        && !System.IO.File.Exists(destination + ".part"));
                }

                // 2. Headers and one chunk, then silence: the body phase is bounded by ReadIdleBound.
                WhisperInstaller.LookupBound = TimeSpan.FromSeconds(30);
                WhisperInstaller.ReadIdleBound = TimeSpan.FromMilliseconds(250);
                var payload = new byte[2 * 65536];
                var stalling = new StallingBody(payload, 65536);
                using (var http = new System.Net.Http.HttpClient(new ScriptedHandler(stalling, payload.Length)))
                {
                    var stopwatch = Stopwatch.StartNew();
                    Task download = WhisperInstaller.DownloadAsync(http, url, destination, delegate { }, CancellationToken.None);
                    string failure = FailureOf(download, TimeSpan.FromSeconds(10));
                    stopwatch.Stop();
                    check("a download that falls silent after its first chunk gives up at the idle bound, not the hour-long client timeout ("
                          + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        failure != null && failure.Contains("stalled") && failure.Contains("no data") && stalling.Stalled.IsSet);
                    check("...and leaves no .part and no destination behind",
                        !System.IO.File.Exists(destination + ".part") && !System.IO.File.Exists(destination));
                }
            }
            catch (Exception ex) { check("download bounds: " + ex.Message, false); }
            finally
            {
                WhisperInstaller.LookupBound = savedLookup;
                WhisperInstaller.ReadIdleBound = savedIdle;
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>The message of the exception a task faulted with inside <paramref name="wait"/>; null when
        /// it completed cleanly or is still running.</summary>
        private static string FailureOf(Task task, TimeSpan wait)
        {
            try { task.Wait(wait); return null; }
            catch (AggregateException ex) { return ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
        }

        /// <summary>
        /// The Ollama pull gives up on SILENCE at its own bounds -- no headers within PullHeaderBound, no
        /// progress line within PullIdleBound -- in words that name the bound, and reports the caller's
        /// cancellation as the caller's (RA-153, RA-160). Against scripted handlers; no server is involved.
        /// </summary>
        private static void SelfCheckPullBounds(Action<string, bool> check)
        {
            TimeSpan savedHeader = OllamaSummarizer.PullHeaderBound;
            TimeSpan savedIdle = OllamaSummarizer.PullIdleBound;
            const string endpoint = "http://127.0.0.1:1";
            try
            {
                // 1. Ollama never answers the POST.
                OllamaSummarizer.PullHeaderBound = TimeSpan.FromMilliseconds(250);
                OllamaSummarizer.PullIdleBound = TimeSpan.FromSeconds(30);
                using (var http = new System.Net.Http.HttpClient(new StalledHandler()))
                {
                    var stopwatch = Stopwatch.StartNew();
                    Task<OllamaSummarizer.PullResult> pull =
                        OllamaSummarizer.PullModelAsync(http, endpoint, "probe:1b", null, CancellationToken.None);
                    bool ended = pull.Wait(TimeSpan.FromSeconds(10));
                    stopwatch.Stop();
                    check("a pull Ollama never answers gives up at the header bound, in the bound's own words ("
                          + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        ended && !pull.Result.Ok && pull.Result.Message.Contains("stalled")
                        && pull.Result.Message.Contains("no answer"));
                }

                // 2. Two progress lines, then silence.
                OllamaSummarizer.PullHeaderBound = TimeSpan.FromSeconds(30);
                OllamaSummarizer.PullIdleBound = TimeSpan.FromMilliseconds(250);
                byte[] lines = System.Text.Encoding.UTF8.GetBytes(
                    "{\"status\":\"pulling manifest\"}\n{\"status\":\"downloading\",\"completed\":1,\"total\":100}\n");
                var payload = new byte[lines.Length + 64];   // more promised than delivered, so the stream stalls
                Array.Copy(lines, payload, lines.Length);
                var reported = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var stalling = new StallingBody(payload, lines.Length);
                using (var http = new System.Net.Http.HttpClient(new ScriptedHandler(stalling, payload.Length)))
                {
                    var stopwatch = Stopwatch.StartNew();
                    Task<OllamaSummarizer.PullResult> pull =
                        OllamaSummarizer.PullModelAsync(http, endpoint, "probe:1b", reported.Enqueue, CancellationToken.None);
                    bool ended = pull.Wait(TimeSpan.FromSeconds(10));
                    stopwatch.Stop();
                    check("WITNESS the progress lines that did arrive were reported before the stall",
                        reported.Any(l => l.Contains("downloading")) && stalling.Stalled.IsSet);
                    check("a pull that falls silent mid-download gives up at the idle bound, not the six-hour client timeout ("
                          + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)",
                        ended && !pull.Result.Ok && pull.Result.Message.Contains("stalled")
                        && pull.Result.Message.Contains("no progress"));
                }

                // 3. The caller cancels mid-stream: the caller's doing, said as such.
                OllamaSummarizer.PullIdleBound = TimeSpan.FromSeconds(30);
                var held = new StallingBody(payload, lines.Length);
                using (var http = new System.Net.Http.HttpClient(new ScriptedHandler(held, payload.Length)))
                using (var cts = new CancellationTokenSource())
                {
                    Task<OllamaSummarizer.PullResult> pull =
                        OllamaSummarizer.PullModelAsync(http, endpoint, "probe:1b", null, cts.Token);
                    bool stalled = held.Stalled.Wait(TimeSpan.FromSeconds(10));
                    cts.Cancel();
                    bool ended = pull.Wait(TimeSpan.FromSeconds(10));
                    check("WITNESS the caller's own cancellation is reported as cancelled, not as a stall",
                        stalled && ended && !pull.Result.Ok && pull.Result.Message.Contains("cancelled")
                        && !pull.Result.Message.Contains("stalled"));
                }
            }
            catch (Exception ex) { check("pull bounds: " + ex.Message, false); }
            finally
            {
                OllamaSummarizer.PullHeaderBound = savedHeader;
                OllamaSummarizer.PullIdleBound = savedIdle;
            }
        }

        /// <summary>
        /// "Download that model" runs ONE pull at a time and under the module's Shutdown-cancelled token (RA-159,
        /// RA-160). The pull is a probe here that reports a line, holds until released or cancelled, and records
        /// the token it was handed; the reachability probe is bypassed through the StartRecommendedPull seam.
        /// </summary>
        private static void SelfCheckPullGate(Action<string, bool> check)
        {
            Func<string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.PullResult>> savedPull = PullModel;
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var hold = new ManualResetEventSlim(false);
            var started = new ManualResetEventSlim(false);
            int calls = 0;
            int cancelledPulls = 0;
            CancellationToken seen = CancellationToken.None;
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-pull-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                PullModel = delegate(string endpoint, string id, Action<string> report, CancellationToken token)
                {
                    Interlocked.Increment(ref calls);
                    seen = token;
                    return Task.Run(delegate
                    {
                        report(id + ": pulling manifest");
                        started.Set();
                        // Held until the check releases it OR the token fires, which is how the real pull ends
                        // under a cancelled token.
                        WaitHandle.WaitAny(new[] { hold.WaitHandle, token.WaitHandle }, TimeSpan.FromSeconds(10));
                        if (token.IsCancellationRequested) Interlocked.Increment(ref cancelledPulls);
                        return new OllamaSummarizer.PullResult
                        {
                            Ok = !token.IsCancellationRequested,
                            Message = token.IsCancellationRequested ? "The download of " + id + " was cancelled." : id + " is installed.",
                        };
                    });
                };
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string> { "probe:1b" }); };

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.SettingsFor(Id).Set("storageLocation", scratch);
                host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                var module = new RemembranceModule();
                module.Init(host);

                string first = module.StartRecommendedPullForSelfTest("http://127.0.0.1:1", "probe:1b");
                bool began = started.Wait(TimeSpan.FromSeconds(5));
                check("WITNESS the first press starts the pull and says so",
                    first.StartsWith("Downloading", StringComparison.Ordinal) && began
                    && Interlocked.CompareExchange(ref calls, 0, 0) == 1);
                string second = module.StartRecommendedPullForSelfTest("http://127.0.0.1:1", "probe:1b");
                // WAITED FOR, not read once. The pull is reached inside the action's Task.Run, so a second
                // press that DID start one increments `calls` a few milliseconds later: reading it straight
                // after the press passed under the mutation that removes the gate, by timing alone, which the
                // RA-159 case caught as WRONG rather than FIRED on 2026-09-30. Two seconds is ample for a
                // queued pool item, so a second pull cannot hide behind the scheduler.
                bool secondStarted = SpinWait.SpinUntil(
                    delegate { return Interlocked.CompareExchange(ref calls, 0, 0) > 1; }, TimeSpan.FromSeconds(2));
                check("a second press while a pull is running starts no second pull",
                    !secondStarted && Interlocked.CompareExchange(ref calls, 0, 0) == 1);
                check("...and is told so", second.Contains("already running"));
                check("the pull runs under the module's own token, not CancellationToken.None", seen.CanBeCanceled);

                hold.Set();
                bool rearmed = SpinWait.SpinUntil(delegate { return !module.PullInFlightForSelfTest; }, TimeSpan.FromSeconds(5));
                ui.Drain();
                check("WITNESS the gate re-arms once the pull lands, and the selection is persisted",
                    rearmed && host.SettingsFor(Id).Get("summaryModel", "") == "probe:1b");
                hold.Reset();
                started.Reset();
                string third = module.StartRecommendedPullForSelfTest("http://127.0.0.1:1", "probe:1b");
                check("WITNESS ...so the next press pulls again",
                    third.StartsWith("Downloading", StringComparison.Ordinal) && started.Wait(TimeSpan.FromSeconds(5))
                    && Interlocked.CompareExchange(ref calls, 0, 0) == 2);

                module.Shutdown();
                bool cancelled = SpinWait.SpinUntil(
                    delegate { return Interlocked.CompareExchange(ref cancelledPulls, 0, 0) == 1; }, TimeSpan.FromSeconds(5));
                check("Shutdown cancels a pull still in flight", cancelled && seen.IsCancellationRequested);
            }
            catch (Exception ex) { check("pull gate: " + ex.Message, false); }
            finally
            {
                hold.Set();
                PullModel = savedPull;
                ListModels = savedLister;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// A pane action whose settings write fails says so instead of answering with a tick (RA-157), through
        /// AdoptWhisperPaths, the path RefreshWhisper and SetUpWhisperAsync both adopt through. The fake settings'
        /// FailSaves reproduces the false every shipped store returns, and puts the values back the way the
        /// host's fresh-from-disk instance shows them (N-blinkingled-02).
        /// </summary>
        private static void SelfCheckSaveReporting(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-save-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                settings.Set("storageLocation", scratch);
                settings.Set("summaryModelsCache", "alpha:1b");
                settings.Set("whisperExe", @"c:\before\whisper-cli.exe");
                settings.Set("whisperModel", @"c:\before\ggml-base.en.bin");
                settings.Save();   // the "disk": what a failed write puts the values back to
                var module = new RemembranceModule();
                module.Init(host);

                settings.FailSaves = true;
                int loggedBefore = host.LoggedLines.Count;
                string answer = module.AdoptWhisperPathsForSelfTest(@"c:\found\whisper-cli.exe", @"c:\found\ggml-small.en.bin",
                    "✓ found whisper-cli.exe + ggml-small.en.bin");
                check("a Whisper adoption whose settings write fails is not answered with a tick",
                    !answer.StartsWith("✓", StringComparison.Ordinal) && answer.Contains("could not be written"));
                List<string> logged = host.LoggedLines;
                check("...and the log says which write did not land",
                    logged.Count == loggedBefore + 1 && logged[loggedBefore].Contains("Whisper paths")
                    && logged[loggedBefore].Contains("could not be written"));
                check("...and the settings still read the paths that were there before the click",
                    settings.Get("whisperExe", "") == @"c:\before\whisper-cli.exe"
                    && settings.Get("whisperModel", "") == @"c:\before\ggml-base.en.bin");

                settings.FailSaves = false;
                string landed = module.AdoptWhisperPathsForSelfTest(@"c:\found\whisper-cli.exe", @"c:\found\ggml-small.en.bin",
                    "✓ found whisper-cli.exe + ggml-small.en.bin");
                check("WITNESS a write that lands is answered with the tick and is what the module reads back",
                    landed.StartsWith("✓", StringComparison.Ordinal)
                    && settings.Get("whisperExe", "") == @"c:\found\whisper-cli.exe"
                    && host.LoggedLines.Count == loggedBefore + 1);
                module.Shutdown();
            }
            catch (Exception ex) { check("save reporting: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        // ---- BUG-013: the pane's actions read what is on screen, and Apply keeps a background write ----------

        /// <summary>The action labelled <paramref name="label"/> in <paramref name="group"/>, or null. The group is
        /// part of the key because two cards can carry an action of the same name.</summary>
        private static PaneAction PaneActionFor(OptionsPane pane, string group, string label)
        {
            if (pane == null || pane.Actions == null) return null;
            foreach (PaneAction a in pane.Actions)
                if (a != null && string.Equals(a.Label, label, StringComparison.Ordinal)
                    && string.Equals(a.Group, group, StringComparison.Ordinal)) return a;
            return null;
        }

        /// <summary>
        /// Press <paramref name="action"/> the way the host does (OptionsWindow.BuildActionRow): the pending-aware
        /// delegate when it is set, handed <paramref name="onScreen"/>, else the saved-values one. The calling thread
        /// stands in for the UI thread, so <paramref name="ui"/> is drained while the action runs: its awaits resume
        /// through it, and so do the settings writes PersistOnUi posts. Null when it has not answered in time.
        /// </summary>
        private static string Press(PaneAction action, IReadOnlyDictionary<string, string> onScreen,
            RecorderSelfCheck.QueueSynchronizationContext ui, TimeSpan timeout)
        {
            if (action == null) return null;
            Task<string> pressed;
            try
            {
                pressed = action.InvokeWithPendingAsync != null
                    ? action.InvokeWithPendingAsync(onScreen)
                    : action.InvokeAsync != null ? action.InvokeAsync() : Task.FromResult("");
            }
            catch (Exception ex) { return "threw: " + ex.Message; }
            var stopwatch = Stopwatch.StartNew();
            while (!pressed.IsCompleted && stopwatch.Elapsed < timeout)
            {
                if (ui != null) ui.Drain();
                Thread.Sleep(5);
            }
            if (ui != null) ui.Drain();
            if (!pressed.IsCompleted) return null;
            if (pressed.IsFaulted)
                return "faulted: " + (pressed.Exception != null ? pressed.Exception.GetBaseException().Message : "");
            return pressed.IsCanceled ? "cancelled" : (pressed.Result ?? "");
        }

        private static Dictionary<string, string> CopyOf(IReadOnlyDictionary<string, string> values)
        {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values != null) foreach (KeyValuePair<string, string> kv in values) copy[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>
        /// BUG-013 through the pane's own delegates, every server stood in for: "Download that model" pulls the model
        /// and asks the address the pane SHOWS, uses the saved value only for an id the pane did not hand over, saves
        /// no unrelated edit, answers a fast outcome in the pane and a slow one with its latest progress inside the
        /// (shortened) bound, and logs its start and its success; "Open the download pages…" opens the model on
        /// screen. The host floor is pinned against the delegate that needs it.
        /// </summary>
        private static void SelfCheckPendingActions(Action<string, bool> check)
        {
            Func<string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.PullResult>> savedPull = PullModel;
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            Func<string, CancellationToken, Task<bool>> savedReachable = IsReachable;
            TimeSpan savedBound = PullAnswerBound;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var hold = new ManualResetEventSlim(true);   // open: a pull ends at once; Reset() holds the next one
            var pulls = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string refusal = null;                        // non-null: the next pull is refused with these words
            bool reachable = true;
            string probed = null;
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-pending-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                IsReachable = delegate(string endpoint, CancellationToken token)
                {
                    probed = endpoint;
                    return Task.FromResult(reachable);
                };
                PullModel = delegate(string endpoint, string id, Action<string> report, CancellationToken token)
                {
                    pulls.Enqueue(id + " from " + endpoint);
                    string refuse = refusal;
                    return Task.Run(delegate
                    {
                        report(OllamaSummarizer.ProgressLine(id, "downloading", 1073741824L, 4294967296L));
                        WaitHandle.WaitAny(new[] { hold.WaitHandle, token.WaitHandle }, TimeSpan.FromSeconds(10));
                        if (refuse != null) return new OllamaSummarizer.PullResult { Ok = false, Message = refuse };
                        return new OllamaSummarizer.PullResult
                        {
                            Ok = !token.IsCancellationRequested,
                            Message = token.IsCancellationRequested ? "The download of " + id + " was cancelled." : id + " is installed.",
                        };
                    });
                };
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string> { "qwen3:8b", "gemma3:4b" }); };

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                settings.Set("storageLocation", scratch);
                settings.Set("summaryModelsCache", "alpha:1b");
                settings.Set("whisperExe", @"c:\seeded\whisper-cli.exe");   // no machine walk in Load (RA-164)
                settings.Set("recommendedModel", "gemma3:4b");               // what was last applied
                settings.Set("ollamaEndpoint", "http://127.0.0.1:9");
                settings.Set("whisperModelChoice", "ggml-tiny.en.bin");
                settings.Save();
                var module = new RemembranceModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes.Count > 0 ? host.OptionsPanes[0] : null;
                PaneAction download = PaneActionFor(pane, "Summary (local AI)", "Download that model");
                PaneAction openPages = PaneActionFor(pane, "Transcription", "Open the download pages…");

                // InvokeWithPendingAsync is host 1.2.5, and on an older host the property's setter does not exist.
                Version floor;
                check("WITNESS Download that model is handed what is on screen (InvokeWithPendingAsync is set)",
                    download != null && download.InvokeWithPendingAsync != null);
                check("the module asks for host 1.2.5 or newer, where InvokeWithPendingAsync arrived; it declares "
                      + module.Info.MinHostVersion,
                    Version.TryParse(module.Info.MinHostVersion, out floor) && floor >= new Version(1, 2, 5));

                Dictionary<string, string> onScreen = CopyOf(pane.Load());
                onScreen["recommendedModel"] = OllamaSummarizer.RecommendedDisplayFor("qwen3:8b");   // picked, not applied
                onScreen["ollamaEndpoint"] = "http://127.0.0.1:7";
                int loggedBefore = host.LoggedLines.Count;

                // ---- the model on screen is the one pulled, and a fast outcome is answered in the pane ----
                string answer = Press(download, onScreen, ui, TimeSpan.FromSeconds(10));
                string pulled;
                pulls.TryDequeue(out pulled);
                check("Download that model pulls the model ON SCREEN, not the saved one (BUG-013); it pulled " + (pulled ?? "nothing"),
                    pulled == "qwen3:8b from http://127.0.0.1:7");
                check("...and asks the address ON SCREEN whether Ollama is there; it asked " + (probed ?? "nothing"),
                    probed == "http://127.0.0.1:7");
                check("a pull that ends inside the bound is answered in the pane, not on a Status line nobody redraws: "
                      + (answer ?? "(no answer)"), answer == "✓ qwen3:8b is installed and selected.");
                check("...and the selection it answers about is what the module reads back",
                    settings.Get("summaryModel", "") == "qwen3:8b");
                check("the press saves no unrelated edit on screen: the saved address and choice are still the saved ones",
                    settings.Get("ollamaEndpoint", "") == "http://127.0.0.1:9" && settings.Get("recommendedModel", "") == "gemma3:4b");
                List<string> logged = host.LoggedLines.Skip(loggedBefore).ToList();
                check("the pull's start and its success are logged, naming the model and the address",
                    logged.Any(l => l.Contains("model pull started: qwen3:8b from http://127.0.0.1:7"))
                    && logged.Any(l => l.Contains("model pull finished: qwen3:8b is installed at http://127.0.0.1:7")));

                // ---- WITNESS: an id the pane did not hand over falls back to the saved value ----
                Dictionary<string, string> without = CopyOf(onScreen);
                without.Remove("recommendedModel");
                without.Remove("ollamaEndpoint");
                Press(download, without, ui, TimeSpan.FromSeconds(10));
                string fallback;
                pulls.TryDequeue(out fallback);
                check("WITNESS with the model and the address absent from what the pane handed over, the saved ones are used; it pulled "
                      + (fallback ?? "nothing"), fallback == "gemma3:4b from http://127.0.0.1:9");

                // ---- a pull still running at the bound: its latest progress, and it carries on ----
                PullAnswerBound = TimeSpan.FromMilliseconds(400);
                hold.Reset();
                var stopwatch = Stopwatch.StartNew();
                string held = Press(download, onScreen, ui, TimeSpan.FromSeconds(10));
                stopwatch.Stop();
                check("a pull still running at the bound is answered with its latest progress and carries on ("
                      + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms of a 400 ms bound): "
                      + (held ?? "(no answer)"),
                    held == "⚠ qwen3:8b: downloading 25% (1.0 of 4.0 GB). The download carries on in the background;"
                            + " reopen this pane to see the Status line."
                    && stopwatch.ElapsedMilliseconds < 5000);
                check("...and that pull is still running when the answer comes", module.PullInFlightForSelfTest);
                hold.Set();
                SpinWait.SpinUntil(delegate { ui.Drain(); return !module.PullInFlightForSelfTest; }, TimeSpan.FromSeconds(5));
                ui.Drain();
                pulls.TryDequeue(out pulled);
                PullAnswerBound = savedBound;

                // ---- a refused pull is answered with its reason ----
                refusal = "pull model manifest: file does not exist";
                string refused = Press(download, onScreen, ui, TimeSpan.FromSeconds(10));
                refusal = null;
                pulls.TryDequeue(out pulled);
                check("a pull Ollama refuses is answered with its reason: " + (refused ?? "(no answer)"),
                    refused == "✗ pull model manifest: file does not exist");

                // ---- nothing answering: said before any pull starts ----
                reachable = false;
                int pullsBefore = pulls.Count;
                string unreachable = Press(download, onScreen, ui, TimeSpan.FromSeconds(10));
                reachable = true;
                check("nothing answering at the address on screen is answered before any pull starts: " + (unreachable ?? "(no answer)"),
                    unreachable != null
                    && unreachable.StartsWith("✗ Nothing is answering at http://127.0.0.1:7", StringComparison.Ordinal)
                    && pulls.Count == pullsBefore);

                // ---- the download pages follow the model on screen ----
                Dictionary<string, string> smallOnScreen = CopyOf(onScreen);
                smallOnScreen["whisperModelChoice"] = WhisperInstaller.Models.First(m => m.Id == "ggml-small.en.bin").Display;
                host.ClearOpenedLinks();
                Press(openPages, smallOnScreen, ui, TimeSpan.FromSeconds(5));
                check("Open the download pages opens the model ON SCREEN, not the saved one",
                    host.OpenedLinks.Contains(WhisperInstaller.ModelUrl("ggml-small.en.bin"))
                    && !host.OpenedLinks.Contains(WhisperInstaller.ModelUrl("ggml-tiny.en.bin")));

                module.Shutdown();
            }
            catch (Exception ex) { check("pending actions: " + ex.Message, false); }
            finally
            {
                hold.Set();
                PullModel = savedPull;
                ListModels = savedLister;
                IsReachable = savedReachable;
                PullAnswerBound = savedBound;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// BUG-013's Apply half, through the module's own background path: the pull lands while the pane is open on
        /// the old model and writes its selection the way it does in the app (PersistOnUi, on this thread, which
        /// stands in for the UI thread). That selection survives an Apply from a pane still showing the old value; a
        /// model the user picked on screen still wins over it; and an untouched Apply still persists what the pane
        /// derives, the preselected summary model, which the stop path reads from the store.
        /// </summary>
        private static void SelfCheckApplyKeepsBackgroundWrites(Action<string, bool> check)
        {
            Func<string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.PullResult>> savedPull = PullModel;
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-apply-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                PullModel = delegate(string endpoint, string id, Action<string> report, CancellationToken token)
                {
                    return Task.FromResult(new OllamaSummarizer.PullResult { Ok = true, Message = id + " is installed." });
                };
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string> { "alpha:1b", "beta:7b" }); };

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                settings.Set("storageLocation", scratch);
                settings.Set("summaryModelsCache", "alpha:1b|beta:7b");
                settings.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                settings.Set("summaryModel", "alpha:1b");
                settings.Set("ollamaEndpoint", "http://127.0.0.1:9");
                settings.Save();
                var module = new RemembranceModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes[0];
                // A pull pressed elsewhere lands: its selection is written on the UI thread once the queue drains.
                Func<string, bool> pullLands = delegate(string model)
                {
                    module.StartRecommendedPullForSelfTest("http://127.0.0.1:9", model);
                    return SpinWait.SpinUntil(delegate
                    {
                        ui.Drain();
                        return !module.PullInFlightForSelfTest && settings.Get("summaryModel", "") == model;
                    }, TimeSpan.FromSeconds(5));
                };

                // 1. The pull's selection lands after the pane loaded; Apply from the unchanged screen.
                IReadOnlyDictionary<string, string> loaded = pane.Load();
                bool landed = pullLands("beta:7b");
                bool applied = pane.Save(CopyOf(loaded));
                check("a model the pull selected while the pane was open survives an Apply from a pane still showing the"
                      + " old one (BUG-013); stored " + settings.Get("summaryModel", ""),
                    landed && applied && settings.Get("summaryModel", "") == "beta:7b");

                // 2. WITNESS: the user's own pick wins over the pull's background selection.
                loaded = pane.Load();
                pullLands("alpha:1b");
                Dictionary<string, string> edited = CopyOf(loaded);
                edited["summaryModel"] = "gamma:3b";
                pane.Save(edited);
                check("WITNESS a model the user picked on screen wins over the pull's background selection; stored "
                      + settings.Get("summaryModel", ""), settings.Get("summaryModel", "") == "gamma:3b");

                // 3. WITNESS: nothing stored, a preselection shown, nothing written in the background: an untouched
                // Apply stores what the pane shows, as it always has.
                settings.Set("summaryModel", "");
                settings.Save();
                loaded = pane.Load();
                string preselected;
                loaded.TryGetValue("summaryModel", out preselected);
                pane.Save(CopyOf(loaded));
                check("WITNESS an untouched Apply still persists the derived preselection, the model the stop path will"
                      + " summarize with; it showed " + (preselected ?? "nothing"),
                    !string.IsNullOrEmpty(preselected) && preselected != NoModelsPlaceholder
                    && settings.Get("summaryModel", "") == preselected);
                module.Shutdown();
            }
            catch (Exception ex) { check("apply keeps background writes: " + ex.Message, false); }
            finally
            {
                PullModel = savedPull;
                ListModels = savedLister;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// The Summary card's Refresh and Validate (2.0.0), pressed through the pane's own delegates with the address
        /// and the model ON SCREEN and every server stood in for. Refresh lists the models at the address shown and
        /// picks a first one only when nothing is picked on screen. Validate names the step that failed (nothing
        /// answering, no model picked, the model not installed, no answer to the test) or answers ✓ with the time the
        /// answer took, off the calling thread and one at a time. Its ":latest" rule is pinned directly.
        /// </summary>
        private static void SelfCheckSummaryButtons(Action<string, bool> check)
        {
            check("a model named without a tag is installed when Ollama lists it as name:latest",
                IsInstalled(new[] { "mistral:latest" }, "mistral") && IsInstalled(new[] { "Mistral:Latest" }, "mistral"));
            check("WITNESS a tagged name matches only itself: mistral:7b is not mistral:latest, nor mistral its :7b",
                IsInstalled(new[] { "mistral:7b" }, "mistral:7b") && !IsInstalled(new[] { "mistral:latest" }, "mistral:7b")
                && !IsInstalled(new[] { "mistral:7b" }, "mistral") && !IsInstalled(new string[0], "mistral"));

            Func<string, CancellationToken, Task<bool>> savedReachable = IsReachable;
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            Func<string, string, string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.SummaryResult>>
                savedSummarize = Summarize;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var answers = new ManualResetEventSlim(true);   // open: the stand-in model answers at once
            bool reachable = true;
            string reachedAt = null, listedAt = null, askedModel = null;
            int answeredOn = 0;
            int requests = 0;
            var answer = new OllamaSummarizer.SummaryResult { Ok = true, Text = "Decisions: ship on Friday.\nBob writes the release notes." };
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-summarybox-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                IsReachable = delegate(string endpoint, CancellationToken token)
                {
                    reachedAt = endpoint;
                    return Task.FromResult(reachable);
                };
                ListModels = delegate(string endpoint, CancellationToken token)
                {
                    listedAt = endpoint;
                    return Task.FromResult((IReadOnlyList<string>)new List<string> { "alpha:1b", "beta:latest" });
                };
                Summarize = delegate(string endpoint, string model, string meetingName, string transcript,
                    Action<string> report, CancellationToken token)
                {
                    Interlocked.Increment(ref requests);
                    askedModel = model;
                    answeredOn = Environment.CurrentManagedThreadId;
                    OllamaSummarizer.SummaryResult result = answer;
                    return Task.Run(delegate
                    {
                        answers.Wait(TimeSpan.FromSeconds(10));
                        return result;
                    });
                };

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                settings.Set("storageLocation", scratch);
                settings.Set("summaryModelsCache", "alpha:1b|beta:latest");
                settings.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                settings.Set("summaryModel", "alpha:1b");
                settings.Set("ollamaEndpoint", "http://127.0.0.1:9");
                settings.Save();
                var module = new RemembranceModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes[0];
                PaneAction refresh = PaneActionFor(pane, "Summary (local AI)", "Refresh local models");
                PaneAction validate = PaneActionFor(pane, "Summary (local AI)", "Validate");
                check("WITNESS the Summary card offers Refresh local models and Validate", refresh != null && validate != null);
                check("...and neither of the buttons they replace: Find local summary models, Test the summarizer",
                    PaneActionFor(pane, "Summary (local AI)", "Find local summary models") == null
                    && PaneActionFor(pane, "Summary (local AI)", "Test the summarizer") == null);

                Dictionary<string, string> onScreen = CopyOf(pane.Load());
                onScreen["ollamaEndpoint"] = "http://127.0.0.1:7";
                onScreen["summaryModel"] = "beta";                    // untagged on screen; Ollama lists beta:latest
                int caller = Environment.CurrentManagedThreadId;

                // ---- every step passes ----
                string passed = Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                check("Validate asks the address and the model ON SCREEN; it asked " + (reachedAt ?? "nothing") + ", listed "
                      + (listedAt ?? "nothing") + " and tested " + (askedModel ?? "nothing"),
                    reachedAt == "http://127.0.0.1:7" && listedAt == "http://127.0.0.1:7" && askedModel == "beta");
                check("a summary set-up that works is answered with all three steps and the time the answer took: "
                      + (passed ?? "(no answer)"),
                    passed != null
                    && passed.StartsWith("✓ Ollama answers, beta is installed, and it answered in ", StringComparison.Ordinal)
                    && passed.EndsWith(" s: Decisions: ship on Friday. Bob writes the release notes.", StringComparison.Ordinal));
                check("the test answer is asked for off the calling thread, so the pane stays live while the model loads",
                    answeredOn != 0 && answeredOn != caller);

                // ---- step 1: nothing answering ----
                reachable = false;
                int before = requests;
                string step1 = Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                reachable = true;
                check("nothing answering is named as the step that failed, at the address on screen, and nothing further"
                      + " is tried: " + (step1 ?? "(no answer)"),
                    step1 != null
                    && step1.StartsWith("✗ Nothing is answering at http://127.0.0.1:7. Is Ollama running?", StringComparison.Ordinal)
                    && requests == before);

                // ---- step 2: no model, then a model Ollama does not have ----
                Dictionary<string, string> none = CopyOf(onScreen);
                none["summaryModel"] = NoModelsPlaceholder;
                string unpicked = Press(validate, none, ui, TimeSpan.FromSeconds(10));
                check("no model picked is named before anything is asked of a model: " + (unpicked ?? "(no answer)"),
                    unpicked == "✗ Pick a summary model first (\"Refresh local models\")." && requests == before);
                Dictionary<string, string> missing = CopyOf(onScreen);
                missing["summaryModel"] = "gamma:3b";
                string notInstalled = Press(validate, missing, ui, TimeSpan.FromSeconds(10));
                check("a model Ollama does not have is named as not installed, with the two buttons that fix it: "
                      + (notInstalled ?? "(no answer)"),
                    notInstalled == "✗ gamma:3b is not installed in Ollama. Use \"Download that model\" or \"Refresh local models\"."
                    && requests == before);

                // ---- step 3: the model does not answer ----
                answer = new OllamaSummarizer.SummaryResult { Ok = false, Message = "The model returned nothing." };
                string mute = Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                answer = new OllamaSummarizer.SummaryResult { Ok = true, Text = "ok" };
                check("a model that does not answer the test is named as the step that failed: " + (mute ?? "(no answer)"),
                    mute == "✗ Ollama answers and beta is installed, but it did not answer the test: The model returned nothing.");

                // ---- one at a time ----
                answers.Reset();
                before = requests;
                Task<string> first = validate.InvokeWithPendingAsync(onScreen);
                SpinWait.SpinUntil(delegate { return requests == before + 1; }, TimeSpan.FromSeconds(5));
                string second = Press(validate, onScreen, ui, TimeSpan.FromSeconds(5));
                check("a second Validate while one is running starts no second check, and says so: " + (second ?? "(no answer)"),
                    second != null && second.StartsWith("⚠", StringComparison.Ordinal) && requests == before + 1);
                answers.Set();
                SpinWait.SpinUntil(delegate { ui.Drain(); return first.IsCompleted; }, TimeSpan.FromSeconds(5));

                // ---- Refresh: the address on screen, and a first pick only when nothing is picked ----
                settings.Set("summaryModel", "");
                settings.Save();
                listedAt = null;
                Dictionary<string, string> picked = CopyOf(onScreen);
                picked["summaryModel"] = "beta:latest";               // picked on screen, not applied
                string refreshed = Press(refresh, picked, ui, TimeSpan.FromSeconds(10));
                check("Refresh local models lists the models at the address ON SCREEN; it listed " + (listedAt ?? "nothing")
                      + ": " + (refreshed ?? "(no answer)"),
                    listedAt == "http://127.0.0.1:7" && refreshed != null && refreshed.StartsWith("✓ found 2", StringComparison.Ordinal));
                check("...and leaves a model picked on screen to Apply, choosing a first one only when nothing is picked",
                    settings.Get("summaryModel", "MISSING") == "");
                Press(refresh, none, ui, TimeSpan.FromSeconds(10));
                check("WITNESS with nothing picked on screen, Refresh chooses the first model it found",
                    settings.Get("summaryModel", "") == "alpha:1b");

                // ---- Summarize a transcript… names the button that exists ----
                string noModel = Press(PaneActionFor(pane, "Summary (local AI)", "Summarize a transcript…"), none, ui,
                    TimeSpan.FromSeconds(5));
                check("Summarize a transcript names the button that exists when no model is picked: " + (noModel ?? "(no answer)"),
                    noModel == "✗ Pick a summary model first (\"Refresh local models\").");
                module.Shutdown();
            }
            catch (Exception ex) { check("summary buttons: " + ex.Message, false); }
            finally
            {
                answers.Set();
                IsReachable = savedReachable;
                ListModels = savedLister;
                Summarize = savedSummarize;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// The Transcription card's Refresh and Validate, through the pane's own delegates with the paths ON SCREEN,
        /// over a scratch probe root (WhisperProbeRoots) and with the whisper-cli run stood in for (CheckWhisperRun): a
        /// self-test never spawns a child. Refresh's decision is pinned as a pure function first, then through the
        /// button: an existing pair on screen is kept and nothing is written, a missing one is replaced by the
        /// detected pair, and nothing found names the missing file. Validate names the step that failed (no exe, no
        /// model, a model too small to be one, a run that failed) or answers ✓ with the time the run took; it runs a
        /// 2-second clip, off the calling thread, one at a time, and leaves the install's check marker alone. The real
        /// install check is then run once on a file that is not a program, which creates no process, to prove its
        /// scratch folder goes even when whisper-cli cannot start.
        /// </summary>
        private static void SelfCheckWhisperButtons(Action<string, bool> check)
        {
            check("Refresh keeps an on-screen pair whose two files exist, whatever detection found",
                WhisperInstaller.PlanRefresh(true, true, true) == WhisperInstaller.RefreshStep.KeepOnScreen
                && WhisperInstaller.PlanRefresh(true, true, false) == WhisperInstaller.RefreshStep.KeepOnScreen);
            check("WITNESS Refresh adopts the detected pair when either file on screen is missing",
                WhisperInstaller.PlanRefresh(true, false, true) == WhisperInstaller.RefreshStep.AdoptDetected
                && WhisperInstaller.PlanRefresh(false, true, true) == WhisperInstaller.RefreshStep.AdoptDetected
                && WhisperInstaller.PlanRefresh(false, false, true) == WhisperInstaller.RefreshStep.AdoptDetected);
            check("Refresh says what is missing when either file on screen is missing and nothing was detected",
                WhisperInstaller.PlanRefresh(true, false, false) == WhisperInstaller.RefreshStep.NothingFound
                && WhisperInstaller.PlanRefresh(false, false, false) == WhisperInstaller.RefreshStep.NothingFound);

            Func<string, IReadOnlyList<string>> savedRoots = WhisperProbeRoots;
            Func<string, string, int, string> savedRun = CheckWhisperRun;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var runs = new ManualResetEventSlim(true);   // open: the stand-in run answers at once
            string runFailure = null;
            string ranExe = null, ranModel = null;
            int ranSamples = 0, ranOn = 0, runCalls = 0;
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-whisperbox-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("remembrance-whisperbox");
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                // The pair on screen: a stand-in exe and an 11 MB "model" (FindModel's rule wants over 10 MB).
                string onScreenDir = System.IO.Path.Combine(scratch, "onscreen");
                System.IO.Directory.CreateDirectory(onScreenDir);
                string screenExe = System.IO.Path.Combine(onScreenDir, "whisper-cli.exe");
                string screenModel = System.IO.Path.Combine(onScreenDir, "ggml-base.en.bin");
                System.IO.File.WriteAllBytes(screenExe, new byte[16]);
                using (System.IO.FileStream f = System.IO.File.Create(screenModel)) f.SetLength(11L * 1024 * 1024);
                string tinyModel = System.IO.Path.Combine(onScreenDir, "ggml-partial.bin");
                System.IO.File.WriteAllBytes(tinyModel, new byte[1024]);
                // The probe root detection walks: a whole install with a different model in it.
                string probeRoot = System.IO.Path.Combine(scratch, "probe");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(probeRoot, "bin"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(probeRoot, "models"));
                string probeExe = System.IO.Path.Combine(probeRoot, "bin", "whisper-cli.exe");
                string probeModel = System.IO.Path.Combine(probeRoot, "models", "ggml-small.en.bin");
                System.IO.File.WriteAllBytes(probeExe, new byte[16]);
                using (System.IO.FileStream f = System.IO.File.Create(probeModel)) f.SetLength(12L * 1024 * 1024);
                string[] roots = { probeRoot };
                WhisperProbeRoots = delegate { return roots; };
                CheckWhisperRun = delegate(string exe, string model, int clipSamples)
                {
                    Interlocked.Increment(ref runCalls);
                    ranExe = exe;
                    ranModel = model;
                    ranSamples = clipSamples;
                    ranOn = Environment.CurrentManagedThreadId;
                    runs.Wait(TimeSpan.FromSeconds(10));
                    return runFailure;
                };

                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.UseStorage(Id, storage);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                settings.Set("storageLocation", scratch);
                settings.Set("summaryModelsCache", "alpha:1b");
                settings.Set("whisperExe", @"c:\saved\whisper-cli.exe");
                settings.Set("whisperModel", @"c:\saved\ggml-base.en.bin");
                settings.Save();
                var module = new RemembranceModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes[0];
                PaneAction refresh = PaneActionFor(pane, "Transcription", "Refresh local models");
                PaneAction validate = PaneActionFor(pane, "Transcription", "Validate");
                check("WITNESS the Transcription card offers Refresh local models, then Validate right after it",
                    refresh != null && validate != null
                    && Array.IndexOf(pane.Actions.ToArray(), validate) == Array.IndexOf(pane.Actions.ToArray(), refresh) + 1);
                check("...and no longer offers Find an installed Whisper",
                    PaneActionFor(pane, "Transcription", "Find an installed Whisper") == null);

                Dictionary<string, string> onScreen = CopyOf(pane.Load());
                onScreen["whisperExe"] = screenExe;
                onScreen["whisperModel"] = screenModel;
                int savesBefore = settings.SaveCount;

                // ---- Refresh: the pair on screen exists, so it is kept and nothing is written ----
                string kept = Press(refresh, onScreen, ui, TimeSpan.FromSeconds(5));
                check("Refresh keeps the pair ON SCREEN when both files exist and writes nothing: " + (kept ?? "(no answer)"),
                    kept != null && kept.StartsWith("✓ using whisper-cli.exe + ggml-base.en.bin", StringComparison.Ordinal)
                    && settings.SaveCount == savesBefore && settings.Get("whisperExe", "") == @"c:\saved\whisper-cli.exe");
                check("...and names the other model detection found, so the user can Browse to it",
                    kept != null && kept.Contains("also found: " + probeModel));

                // ---- Refresh: the pair on screen is missing, so the detected pair is adopted ----
                Dictionary<string, string> gone = CopyOf(onScreen);
                gone["whisperModel"] = System.IO.Path.Combine(onScreenDir, "ggml-deleted.bin");
                string adopted = Press(refresh, gone, ui, TimeSpan.FromSeconds(5));
                check("Refresh adopts the detected pair when a file on screen is missing: " + (adopted ?? "(no answer)"),
                    adopted == "✓ found whisper-cli.exe + ggml-small.en.bin"
                    && settings.Get("whisperExe", "") == probeExe && settings.Get("whisperModel", "") == probeModel);

                // ---- Refresh: nothing to adopt, so the missing file is named ----
                roots = new string[0];
                string nothing = Press(refresh, gone, ui, TimeSpan.FromSeconds(5));
                roots = new[] { probeRoot };
                check("with nothing detected, Refresh names the file on screen that is missing: " + (nothing ?? "(no answer)"),
                    nothing != null && nothing.StartsWith("✗ The model file is not at " + gone["whisperModel"], StringComparison.Ordinal));

                // ---- Validate: every step passes ----
                int caller = Environment.CurrentManagedThreadId;
                string passed = Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                check("Validate runs the pair ON SCREEN on a 2-second clip at 16 kHz; it ran " + (ranModel ?? "nothing")
                      + " with " + ranSamples.ToString(CultureInfo.InvariantCulture) + " samples",
                    ranExe == screenExe && ranModel == screenModel && ranSamples == 2 * 16000);
                check("a pair that runs is answered with the model and the time the run took: " + (passed ?? "(no answer)"),
                    passed != null
                    && passed.StartsWith("✓ whisper-cli ran ggml-base.en.bin on a 2-second test clip in ", StringComparison.Ordinal)
                    && passed.EndsWith(" s", StringComparison.Ordinal));
                check("the test run happens off the calling thread, so the pane stays live while the model loads",
                    ranOn != 0 && ranOn != caller);

                // ---- Validate: each failing step by name ----
                int before = runCalls;
                Dictionary<string, string> noExe = CopyOf(onScreen);
                noExe["whisperExe"] = System.IO.Path.Combine(onScreenDir, "no-such-cli.exe");
                string step1 = Press(validate, noExe, ui, TimeSpan.FromSeconds(5));
                check("a whisper-cli that is not there is named as step 1, and nothing is run: " + (step1 ?? "(no answer)"),
                    step1 != null && step1.StartsWith("✗ whisper-cli is not at " + noExe["whisperExe"], StringComparison.Ordinal)
                    && runCalls == before);
                Dictionary<string, string> noModel = CopyOf(onScreen);
                noModel["whisperModel"] = "";
                string step2 = Press(validate, noModel, ui, TimeSpan.FromSeconds(5));
                check("an empty model path is named as step 2: " + (step2 ?? "(no answer)"),
                    step2 != null && step2.StartsWith("✗ The model path is empty.", StringComparison.Ordinal) && runCalls == before);
                Dictionary<string, string> partial = CopyOf(onScreen);
                partial["whisperModel"] = tinyModel;
                string small = Press(validate, partial, ui, TimeSpan.FromSeconds(5));
                check("a model file under detection's 10 MB rule is named as too small to be a model, and not run: "
                      + (small ?? "(no answer)"),
                    small != null && small.StartsWith("✗ ggml-partial.bin is 0.0 MB, too small to be a Whisper model", StringComparison.Ordinal)
                    && runCalls == before);
                runFailure = "whisper-cli exited 3 -- error: failed to load model";
                string failed = Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                runFailure = null;
                check("a run that fails is named as step 3, with what the install check said: " + (failed ?? "(no answer)"),
                    failed == "✗ whisper-cli did not run the 2-second test clip: whisper-cli exited 3 -- error: failed to load model");

                // ---- Validate: one at a time ----
                runs.Reset();
                before = runCalls;
                Task<string> first = validate.InvokeWithPendingAsync(onScreen);
                SpinWait.SpinUntil(delegate { return runCalls == before + 1; }, TimeSpan.FromSeconds(5));
                string second = Press(validate, onScreen, ui, TimeSpan.FromSeconds(5));
                check("a second Validate while one is running starts no second run, and says so: " + (second ?? "(no answer)"),
                    second != null && second.StartsWith("⚠", StringComparison.Ordinal) && runCalls == before + 1);
                runs.Set();
                SpinWait.SpinUntil(delegate { ui.Drain(); return first.IsCompleted; }, TimeSpan.FromSeconds(5));

                // ---- Validate leaves the install's check marker alone, both ways ----
                string installRoot = WhisperInstaller.InstallRoot(storage.DataDirectory);
                WhisperInstaller.MarkUnverified(installRoot, "simulated: an earlier check failed");
                Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                bool keptMarker = WhisperInstaller.IsMarkedUnverified(installRoot);
                WhisperInstaller.ClearUnverified(installRoot);
                runFailure = "whisper-cli exited 3";
                Press(validate, onScreen, ui, TimeSpan.FromSeconds(10));
                runFailure = null;
                check("Validate neither clears check-failed.txt when its run passes nor writes it when its run fails",
                    keptMarker && !WhisperInstaller.IsMarkedUnverified(installRoot));
                module.Shutdown();

                // ---- the real install check, on a file that is not a program: no process is created ----
                string before2 = string.Join("|", System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "dp-whisper-check-*"));
                string detail;
                bool verified = WhisperInstaller.TryVerify(screenExe, screenModel, out detail, 2 * 16000);
                string after2 = string.Join("|", System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "dp-whisper-check-*"));
                check("the install check deletes its scratch folder even when whisper-cli cannot start (" + (detail ?? "") + ")",
                    !verified && !string.IsNullOrEmpty(detail) && after2 == before2);
            }
            catch (Exception ex) { check("whisper buttons: " + ex.Message, false); }
            finally
            {
                runs.Set();
                WhisperProbeRoots = savedRoots;
                CheckWhisperRun = savedRun;
                SynchronizationContext.SetSynchronizationContext(previous);
                storage.Dispose();
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// The storage choice (2.0.0), pure and on disk: the two options and their ids; the migration from the old
        /// checkbox both ways; the strict day-folder and Snapshots-folder names with their near-misses; NewCapture's
        /// by-date paths (the flat names, in the folder for the capture's local start date); where a snapshot taken
        /// with nothing recording goes in each layout; and the purge over scratch roots with four-day-old files: old
        /// scratch and snapshots go, a transcript stays, a day or Snapshots folder this pass emptied goes, and neither a
        /// user's own empty folder of such a name, a near-miss folder, nor a foreign file in one is touched.
        /// </summary>
        private static void SelfCheckFolderLayout(Action<string, bool> check)
        {
            check("the storage choice offers exactly the owner's two options, in order",
                FolderLayout.Displays().SequenceEqual(new[] { "Create a folder per capture", "Create a folder by date" }));
            check("WITNESS each option maps to its own stable id and back",
                FolderLayout.FromDisplay(FolderLayout.ToDisplay(FolderLayout.PerCapture)) == FolderLayout.PerCapture
                && FolderLayout.FromDisplay(FolderLayout.ToDisplay(FolderLayout.ByDate)) == FolderLayout.ByDate
                && FolderLayout.PerCapture == "capture" && FolderLayout.ByDate == "date");
            check("text that is not one of the options stores nothing rather than a guess",
                FolderLayout.FromDisplay("") == null && FolderLayout.FromDisplay("Create a folder") == null);
            check("migration: the old checkbox OFF, which filed captures flat in the root, becomes a folder by date",
                FolderLayout.Migrate("", false) == FolderLayout.ByDate && FolderLayout.Migrate(null, false) == FolderLayout.ByDate);
            check("WITNESS migration: the old checkbox ON, or never set, stays a folder per capture",
                FolderLayout.Migrate("", true) == FolderLayout.PerCapture);
            check("a stored choice wins over the old checkbox, both ways, and an unknown one falls back to it",
                FolderLayout.Migrate(FolderLayout.PerCapture, false) == FolderLayout.PerCapture
                && FolderLayout.Migrate(FolderLayout.ByDate, true) == FolderLayout.ByDate
                && FolderLayout.Migrate("flat", false) == FolderLayout.ByDate);

            check("a folder named yyyy-MM-dd IS a day folder", CaptureStore.IsDayFolderName("2026-10-02"));
            check("a folder named for a date that does not exist is NOT a day folder (2026-13-45)",
                !CaptureStore.IsDayFolderName("2026-13-45"));
            check("near-misses are not day folders: a one-digit day, trailing text, a capture stamp, a word, nothing",
                !CaptureStore.IsDayFolderName("2026-10-2") && !CaptureStore.IsDayFolderName("2026-10-02 notes")
                && !CaptureStore.IsDayFolderName("2026-10-02 09-30-00") && !CaptureStore.IsDayFolderName("Zoom")
                && !CaptureStore.IsDayFolderName(""));
            check("WITNESS the folder named exactly Snapshots IS the snapshot folder", CaptureStore.IsSnapshotFolderName("Snapshots"));
            check("near-misses are not the snapshot folder: snapshots, Snapshots 2, My Snapshots",
                !CaptureStore.IsSnapshotFolderName("snapshots") && !CaptureStore.IsSnapshotFolderName("Snapshots 2")
                && !CaptureStore.IsSnapshotFolderName("My Snapshots"));

            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-layout-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var fixture = new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Local));
                var byDateStore = new CaptureStore(scratch, FolderLayout.ByDate);
                CapturePaths byDate = byDateStore.NewCapture("Sprint Review", fixture);
                check("by date files a capture in the folder for its local start date",
                    byDate.Directory == System.IO.Path.Combine(scratch, "2026-08-27"));
                check("...under the flat layout's names, so a file moved out of the folder still says what it is",
                    System.IO.Path.GetFileName(byDate.Audio) == "Sprint Review - 2026-08-27 09-30-00.wav"
                    && System.IO.Path.GetFileName(byDate.Transcript) == "Sprint Review - 2026-08-27 09-30-00.transcript.txt"
                    && System.IO.Path.GetFileName(byDate.Summary) == "Sprint Review - 2026-08-27 09-30-00.summary.txt"
                    && byDate.SnapshotPrefix == "Sprint Review - 2026-08-27 09-30-00 - snap");
                string stem = System.IO.Path.GetFileNameWithoutExtension(byDate.Audio);
                check("...each of which the purge reads with the flat rules: audio, both scratch tracks, a snapshot; never the transcript",
                    CaptureStore.NamesThisModuleWrites(System.IO.Path.GetFileName(byDate.Audio), false)
                    && CaptureStore.NamesThisModuleWrites(stem + AudioRecorder.SystemScratchSuffix, false)
                    && CaptureStore.NamesThisModuleWrites(stem + AudioRecorder.MicScratchSuffix, false)
                    && CaptureStore.NamesThisModuleWrites(byDate.SnapshotPrefix + " 2026-08-27 09-31-00.png", false)
                    && !CaptureStore.IsEphemeral(byDate.Transcript));
                var perCaptureStore = new CaptureStore(scratch, FolderLayout.PerCapture);
                CapturePaths perCapture = perCaptureStore.NewCapture("Sprint Review", fixture);
                check("WITNESS per capture still files a capture in its own folder under the bare names",
                    perCapture.Directory == System.IO.Path.Combine(scratch, "Sprint Review - 2026-08-27 09-30-00")
                    && System.IO.Path.GetFileName(perCapture.Audio) == "recording.wav" && perCapture.SnapshotPrefix == "snap");
                check("a snapshot with nothing recording goes into that day's folder by date, and into Snapshots per capture",
                    byDateStore.SnapshotDirectory(fixture) == System.IO.Path.Combine(scratch, "2026-08-27")
                    && perCaptureStore.SnapshotDirectory(fixture) == System.IO.Path.Combine(scratch, "Snapshots"));

                // ---- the purge's walk over day folders and the Snapshots folder ----
                DateTime fourDaysAgo = DateTime.UtcNow.AddDays(-4);
                Action<string> age = delegate(string path)
                {
                    if (System.IO.File.Exists(path)) System.IO.File.SetLastWriteTimeUtc(path, fourDaysAgo);
                    else
                    {
                        System.IO.Directory.SetCreationTimeUtc(path, fourDaysAgo);
                        System.IO.Directory.SetLastWriteTimeUtc(path, fourDaysAgo);
                    }
                };
                Func<string, string> oldFile = delegate(string path)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllBytes(path, new byte[64]);
                    age(path);
                    return path;
                };
                string walk = System.IO.Path.Combine(scratch, "walk");
                const string Base = "Standup - 2026-08-20 09-00-00";
                string keptDay = System.IO.Path.Combine(walk, "2026-08-20");      // old scratch + snapshot + transcript
                string emptiedDay = System.IO.Path.Combine(walk, "2026-08-21");   // old audio only
                string youngDay = System.IO.Path.Combine(walk, "2026-08-22");     // a young scratch
                string usersDay = System.IO.Path.Combine(walk, "2026-08-23");     // the user's own, empty
                string foreignDay = System.IO.Path.Combine(walk, "2026-08-24");   // the user's own files
                string notADay = System.IO.Path.Combine(walk, "2026-13-45");      // a module-shaped wav, wrong folder
                string snaps = System.IO.Path.Combine(walk, "Snapshots");          // old snaps and a foreign png
                string notSnaps = System.IO.Path.Combine(walk, "Snapshots 2");     // an old snap, wrong folder
                string oldScratch = oldFile(System.IO.Path.Combine(keptDay, Base + AudioRecorder.SystemScratchSuffix));
                string oldDaySnap = oldFile(System.IO.Path.Combine(keptDay, Base + " - snap 2026-08-20 09-05-00.png"));
                string oldIdleSnap = oldFile(System.IO.Path.Combine(keptDay, "snap 2026-08-20 18-00-00.png"));
                string transcript = System.IO.Path.Combine(keptDay, Base + ".transcript.txt");
                System.IO.File.WriteAllText(transcript, "kept");
                age(transcript);
                string oldAudio = oldFile(System.IO.Path.Combine(emptiedDay, "2026-08-21 10-00-00.wav"));
                System.IO.Directory.CreateDirectory(youngDay);
                string youngScratch = System.IO.Path.Combine(youngDay, Base + AudioRecorder.MicScratchSuffix);
                System.IO.File.WriteAllBytes(youngScratch, new byte[64]);
                System.IO.Directory.CreateDirectory(usersDay);
                string foreignWav = oldFile(System.IO.Path.Combine(foreignDay, "notes.wav"));
                string foreignPng = oldFile(System.IO.Path.Combine(foreignDay, "holiday.png"));
                string strayWav = oldFile(System.IO.Path.Combine(notADay, "2026-08-25 10-00-00.wav"));
                string oldSnap = oldFile(System.IO.Path.Combine(snaps, "snap 2026-08-20 09-00-00.png"));
                string oldSnap2 = oldFile(System.IO.Path.Combine(snaps, "snap 2026-08-20 09-00-00 (2).png"));
                string foreignInSnaps = oldFile(System.IO.Path.Combine(snaps, "holiday.png"));
                string snapInNearMiss = oldFile(System.IO.Path.Combine(notSnaps, "snap 2026-08-20 09-00-00.png"));
                string legacyRootSnap = oldFile(System.IO.Path.Combine(walk, "snap 2026-08-19 08-00-00.png"));
                foreach (string d in new[] { keptDay, emptiedDay, usersDay, foreignDay, notADay, snaps, notSnaps }) age(d);
                new CaptureStore(walk, FolderLayout.ByDate).Purge();
                check("WITNESS an old scratch track and old snapshots in a day folder are purged, by the flat rules",
                    !System.IO.File.Exists(oldScratch) && !System.IO.File.Exists(oldDaySnap) && !System.IO.File.Exists(oldIdleSnap));
                check("a transcript in a day folder is never purged, and the day folder holding it stays",
                    System.IO.File.Exists(transcript) && System.IO.Directory.Exists(keptDay));
                check("a day folder this pass emptied of this module's files is removed with its last one",
                    !System.IO.File.Exists(oldAudio) && !System.IO.Directory.Exists(emptiedDay));
                check("a young scratch in a day folder stays, and so does its folder",
                    System.IO.File.Exists(youngScratch) && System.IO.Directory.Exists(youngDay));
                check("a user's own empty date-named folder survives the purge, however old: this pass deleted nothing from it",
                    System.IO.Directory.Exists(usersDay));
                check("a foreign file in a day folder is never touched, wav or png, however old",
                    System.IO.File.Exists(foreignWav) && System.IO.File.Exists(foreignPng));
                check("a folder whose name is not a real date is not descended into, whatever it holds (2026-13-45)",
                    System.IO.File.Exists(strayWav));
                check("WITNESS old snapshots in the Snapshots folder are purged, collision suffix included",
                    !System.IO.File.Exists(oldSnap) && !System.IO.File.Exists(oldSnap2));
                check("a foreign png inside Snapshots is never touched, and the folder holding it stays",
                    System.IO.File.Exists(foreignInSnaps) && System.IO.Directory.Exists(snaps));
                check("a folder that only resembles Snapshots is not descended into (Snapshots 2)",
                    System.IO.File.Exists(snapInNearMiss));
                check("a legacy snapshot in the storage root is still purged as before",
                    !System.IO.File.Exists(legacyRootSnap));

                // Roots of their own, because a case-insensitive file system holds one of "Snapshots"/"snapshots".
                string lowerRoot = System.IO.Path.Combine(scratch, "lower");
                string lowerSnap = oldFile(System.IO.Path.Combine(lowerRoot, "snapshots", "snap 2026-08-20 09-00-00.png"));
                age(System.IO.Path.Combine(lowerRoot, "snapshots"));
                string emptiedRoot = System.IO.Path.Combine(scratch, "emptied");
                oldFile(System.IO.Path.Combine(emptiedRoot, "Snapshots", "snap 2026-08-20 09-00-00.png"));
                age(System.IO.Path.Combine(emptiedRoot, "Snapshots"));
                string emptyRoot = System.IO.Path.Combine(scratch, "empty");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(emptyRoot, "Snapshots"));
                age(System.IO.Path.Combine(emptyRoot, "Snapshots"));
                foreach (string root in new[] { lowerRoot, emptiedRoot, emptyRoot })
                    new CaptureStore(root, FolderLayout.PerCapture).Purge();
                check("a user's own lower-case snapshots folder is not descended into, whatever it holds",
                    System.IO.File.Exists(lowerSnap));
                check("a Snapshots folder this pass emptied is removed with its last snapshot",
                    !System.IO.Directory.Exists(System.IO.Path.Combine(emptiedRoot, "Snapshots")));
                check("a Snapshots folder that was already empty survives: this pass deleted nothing from it",
                    System.IO.Directory.Exists(System.IO.Path.Combine(emptyRoot, "Snapshots")));
            }
            catch (Exception ex) { check("folder layout: " + ex.Message, false); }
            finally
            {
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// The storage choice through the module itself, with fake devices and the snapshot capture stood in for: an
        /// install whose old checkbox was OFF and that has no new key yet shows "Create a folder by date", records into
        /// a day folder under the flat names, files a snapshot taken with nothing recording into that day's folder, and
        /// an untouched Apply stores the new key while the old one stays as it was; per capture, such a snapshot goes
        /// into Snapshots. A start that fails removes the day folder it created, never one that was already there.
        /// </summary>
        private static void SelfCheckFolderLayoutInModule(Action<string, bool> check)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-bydate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<string, bool> savedCapture = SnapshotCapture;
            Func<DateTimeOffset> savedClock = SnapshotClock;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var shots = new System.Collections.Concurrent.ConcurrentQueue<string>();
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                var pinned = new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Local));
                SnapshotClock = delegate { return pinned; };
                SnapshotCapture = delegate(string png) { shots.Enqueue(png); return true; };
                Func<RemembranceModule, string> idleSnapshot = delegate(RemembranceModule m)
                {
                    string shot;
                    while (shots.TryDequeue(out shot)) { }
                    m.TakeSnapshotForSelfTest();
                    SpinWait.SpinUntil(delegate { return !shots.IsEmpty; }, TimeSpan.FromSeconds(3));
                    ui.WaitForPost(TimeSpan.FromSeconds(3));
                    ui.Drain();
                    return shots.TryDequeue(out shot) ? shot : null;
                };

                string byDateRoot = System.IO.Path.Combine(scratch, "bydate");
                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                    settings.Set("storageLocation", byDateRoot);
                    settings.Set("summaryModelsCache", "alpha:1b");
                    settings.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                    settings.Set(FolderLayout.LegacySettingKey, "false");   // a 1.0.x install that filed captures flat
                    settings.Save();
                    var module = new RemembranceModule();
                    module.Init(host);
                    OptionsPane pane = host.OptionsPanes[0];
                    IReadOnlyDictionary<string, string> loaded = pane.Load();
                    string shown;
                    loaded.TryGetValue(FolderLayout.SettingKey, out shown);
                    check("an install whose old checkbox was OFF shows Create a folder by date until it chooses; it shows "
                          + (shown ?? "nothing"), shown == "Create a folder by date");

                    string idle = idleSnapshot(module);
                    check("by date, a snapshot taken with nothing recording goes into that day's folder, named as before: "
                          + (idle ?? "none"),
                        idle == System.IO.Path.Combine(byDateRoot, "2026-08-27", "snap 2026-08-27 09-30-00.png"));

                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    CapturePaths paths = module.LastCaptureForSelfTest;
                    Thread.Sleep(80);   // a few buffers, so the save writes a recording
                    host.RaiseHostShutdown();   // the synchronous save
                    ui.Drain();
                    check("...and records into the folder for the day it started, under the flat names",
                        paths != null && CaptureStore.IsDayFolderName(System.IO.Path.GetFileName(paths.Directory))
                        && System.IO.File.Exists(paths.Audio)
                        && System.IO.Path.GetFileName(paths.Audio) == paths.BaseName + ".wav");

                    pane.Save(CopyOf(pane.Load()));
                    check("an untouched Apply stores the new key's id and leaves the old checkbox's key as it was",
                        settings.Get(FolderLayout.SettingKey, "") == FolderLayout.ByDate
                        && settings.Get(FolderLayout.LegacySettingKey, "MISSING") == "false");
                    module.Shutdown();
                }

                string perCaptureRoot = System.IO.Path.Combine(scratch, "percapture");
                {
                    var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host.SettingsFor(Id).Set("storageLocation", perCaptureRoot);
                    host.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b");
                    var module = new RemembranceModule();
                    module.Init(host);
                    string idle = idleSnapshot(module);
                    check("per capture, a snapshot taken with nothing recording goes into Snapshots, not the root: "
                          + (idle ?? "none"),
                        idle == System.IO.Path.Combine(perCaptureRoot, "Snapshots", "snap 2026-08-27 09-30-00.png"));
                    module.Shutdown();
                }

                // ---- a failed start: the day folder it made goes, one that was already there stays ----
                string freshRoot = System.IO.Path.Combine(scratch, "fresh");
                string keptRoot = System.IO.Path.Combine(scratch, "kept");
                string today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(keptRoot, today));   // the user's own, empty
                foreach (string root in new[] { freshRoot, keptRoot })
                {
                    using (var devices = new RecorderSelfCheck.FakeDevices())
                    {
                        devices.MicrophoneRefuses = true;
                        var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                        DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings settings = host.SettingsFor(Id);
                        settings.Set("storageLocation", root);
                        settings.Set("summaryModelsCache", "alpha:1b");
                        settings.Set("whisperExe", @"c:\seeded\whisper-cli.exe");
                        settings.Set(FolderLayout.SettingKey, FolderLayout.ByDate);
                        var module = new RemembranceModule();
                        module.Init(host);
                        module.StartRecordingForSelfTest();
                        ui.Drain();
                        module.Shutdown();
                    }
                }
                check("WITNESS a start that fails removes the empty day folder it created itself",
                    !System.IO.Directory.Exists(System.IO.Path.Combine(freshRoot, today)));
                check("a start that fails leaves an empty day folder that was already there",
                    System.IO.Directory.Exists(System.IO.Path.Combine(keptRoot, today)));
            }
            catch (Exception ex) { check("folder layout in the module: " + ex.Message, false); }
            finally
            {
                SnapshotCapture = savedCapture;
                SnapshotClock = savedClock;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>The flag's value as the host holds it now, "" when nothing was published.</summary>
        private static string BusyNow(DesktopAICompanion.ModuleKit.Testing.RecordingHost host)
        {
            string value;
            return host.PublishedContext.TryGetValue(BusyContextKey, out value) ? (value ?? "") : "";
        }

        /// <summary>The phase in a published value, or "" for a clear or anything that is not the contract's shape.</summary>
        private static string BusyPhaseOf(string value)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(value ?? "",
                "^\\{\"phase\":\"(transcribing|summarizing|validating)\",\"at\":\"[0-9T:.\\-]+Z\"\\}$");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>The "at" in a published value, as a UTC instant, or DateTime.MinValue.</summary>
        private static DateTime BusyAtOf(string value)
        {
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(value ?? "", "\"at\":\"([^\"]+)\"");
            DateTime at;
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "o", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out at) && at.Kind == DateTimeKind.Utc) return at;
            return DateTime.MinValue;
        }

        /// <summary>
        /// remembrance.busy, the flag the aibrain-standdown lane reads (addendum 1's contract), with every model stood in
        /// for so nothing runs. Its exact shape; that recording and a model pull do not raise it; that a stop which will
        /// transcribe raises it synchronously before its background work, moves it to summarizing with a fresh "at",
        /// republishes it before the summary request and clears it when done; that a failed transcription and a failed
        /// summary still clear it; that overlapping spans keep it up until the last ends and name the newest; and that
        /// Shutdown and the host's shutdown clear it synchronously, posting nothing, with a span still open, after which
        /// the span's own end publishes nothing.
        /// </summary>
        private static void SelfCheckBusyFlag(Action<string, bool> check)
        {
            var fixedAt = new DateTime(2026, 10, 2, 16, 18, 44, 593, DateTimeKind.Utc);
            check("the flag's value is {\"phase\":...,\"at\":...} with at in UTC, ISO-8601 round-trip",
                BusyValue(BusyTranscribing, fixedAt) == "{\"phase\":\"transcribing\",\"at\":\"2026-10-02T16:18:44.5930000Z\"}");

            TranscribeFile savedTranscribe = TranscribeWav;
            Func<string, string, string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.SummaryResult>>
                savedSummarize = Summarize;
            Func<string, string, int, string> savedRun = CheckWhisperRun;
            Func<string, CancellationToken, Task<bool>> savedReachable = IsReachable;
            Func<string, CancellationToken, Task<IReadOnlyList<string>>> savedLister = ListModels;
            Func<string, string, Action<string>, CancellationToken, Task<OllamaSummarizer.PullResult>> savedPull = PullModel;
            SynchronizationContext previous = SynchronizationContext.Current;
            var ui = new RecorderSelfCheck.QueueSynchronizationContext();
            var transcribing = new ManualResetEventSlim(false);   // set by the stand-in once it is running
            var transcribeGate = new ManualResetEventSlim(true);  // reset to hold the next transcription
            var summarizing = new ManualResetEventSlim(false);
            var summaryGate = new ManualResetEventSlim(true);
            var validating = new ManualResetEventSlim(false);
            var validateGate = new ManualResetEventSlim(true);
            var running = new ManualResetEventSlim(false);
            var runGate = new ManualResetEventSlim(true);
            var pulling = new ManualResetEventSlim(false);
            var pullGate = new ManualResetEventSlim(true);
            bool transcriptionThrows = false;
            DateTime reportedAt = DateTime.MinValue;
            bool summaryFails = false;
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-busy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Func<Func<bool>, bool> pumpUntil = delegate(Func<bool> condition)
            {
                return SpinWait.SpinUntil(delegate { ui.Drain(); return condition(); }, TimeSpan.FromSeconds(10));
            };
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                System.IO.Directory.CreateDirectory(scratch);
                string exe = System.IO.Path.Combine(scratch, "whisper-cli.exe");
                string model = System.IO.Path.Combine(scratch, "ggml-base.en.bin");
                System.IO.File.WriteAllBytes(exe, new byte[16]);
                using (System.IO.FileStream f = System.IO.File.Create(model)) f.SetLength(11L * 1024 * 1024);

                TranscribeWav = delegate(string wav, string transcriptPath, string whisperExe, string modelPath,
                    string meetingName, IReadOnlyList<string> attendees, DateTimeOffset? recordedAt, out bool did)
                {
                    transcribing.Set();
                    transcribeGate.Wait(TimeSpan.FromSeconds(10));
                    if (transcriptionThrows) throw new InvalidOperationException("simulated: whisper-cli died");
                    did = true;
                    return "Alice: we ship on Friday.";
                };
                Summarize = delegate(string endpoint, string summaryModel, string meetingName, string transcript,
                    Action<string> report, CancellationToken token)
                {
                    bool validation = meetingName == "Connection test";
                    return Task.Run(delegate
                    {
                        // Before the request, as SummarizeWithAsync reports before each one it sends, and a
                        // moment after the phase change, so a flag republished only there carries an earlier "at".
                        Thread.Sleep(30);
                        if (!validation) reportedAt = DateTime.UtcNow;
                        if (report != null) report("summarizing...");
                        (validation ? validating : summarizing).Set();
                        (validation ? validateGate : summaryGate).Wait(TimeSpan.FromSeconds(10));
                        return summaryFails && !validation
                            ? new OllamaSummarizer.SummaryResult { Ok = false, Message = "simulated: the model returned nothing." }
                            : new OllamaSummarizer.SummaryResult { Ok = true, Text = "Decisions: ship on Friday." };
                    });
                };
                CheckWhisperRun = delegate(string runExe, string runModel, int clipSamples)
                {
                    running.Set();
                    runGate.Wait(TimeSpan.FromSeconds(10));
                    return null;
                };
                IsReachable = delegate { return Task.FromResult(true); };
                ListModels = delegate { return Task.FromResult((IReadOnlyList<string>)new List<string> { "alpha:1b" }); };
                PullModel = delegate(string endpoint, string id, Action<string> report, CancellationToken token)
                {
                    return Task.Run(delegate
                    {
                        pulling.Set();
                        WaitHandle.WaitAny(new[] { pullGate.WaitHandle, token.WaitHandle }, TimeSpan.FromSeconds(10));
                        return new OllamaSummarizer.PullResult { Ok = true, Message = id + " is installed." };
                    });
                };

                Func<DesktopAICompanion.ModuleKit.Testing.RecordingHost> newHost = delegate
                {
                    var made = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = made.SettingsFor(Id);
                    s.Set("storageLocation", System.IO.Path.Combine(scratch, "store"));
                    s.Set("summaryModelsCache", "alpha:1b");
                    s.Set("summaryModel", "alpha:1b");
                    s.Set("summaryOn", "true");
                    s.Set("ollamaEndpoint", "http://127.0.0.1:9");
                    s.Set("whisperExe", exe);
                    s.Set("whisperModel", model);
                    return made;
                };

                using (var devices = new RecorderSelfCheck.FakeDevices())
                {
                    var host = newHost();
                    var module = new RemembranceModule();
                    module.Init(host);

                    // ---- recording and a pull raise nothing ----
                    module.StartRecordingForSelfTest();
                    ui.Drain();
                    check("WITNESS recording alone does not raise the flag: it runs no model", BusyNow(host) == "");
                    pullGate.Reset();
                    module.StartRecommendedPullForSelfTest("http://127.0.0.1:9", "alpha:1b");
                    bool pullStarted = pulling.Wait(TimeSpan.FromSeconds(5));
                    ui.Drain();
                    check("a model pull does not raise the flag: a download loads nothing",
                        pullStarted && BusyNow(host) == "");
                    pullGate.Set();
                    pumpUntil(delegate { return !module.PullInFlightForSelfTest; });

                    // ---- a stop that transcribes and summarizes ----
                    Thread.Sleep(80);   // a few buffers, so the save writes a recording
                    transcribeGate.Reset();
                    summaryGate.Reset();
                    module.StopRecordingForSelfTest();
                    string atStop = BusyNow(host);   // read before anything is drained: published synchronously
                    check("a stop that will transcribe raises the flag synchronously, before its background work: " + atStop,
                        BusyPhaseOf(atStop) == BusyTranscribing);
                    bool whisperRan = transcribing.Wait(TimeSpan.FromSeconds(10));
                    ui.Drain();
                    check("...and holds it as transcribing while whisper-cli runs", whisperRan && BusyPhaseOf(BusyNow(host)) == BusyTranscribing);
                    Thread.Sleep(20);   // so a republish carries a later instant
                    transcribeGate.Set();
                    bool summaryAsked = summarizing.Wait(TimeSpan.FromSeconds(10));
                    ui.Drain();
                    string summaryValue = BusyNow(host);
                    check("...moves to summarizing with a fresh at: " + summaryValue,
                        summaryAsked && BusyPhaseOf(summaryValue) == BusySummarizing && BusyAtOf(summaryValue) > BusyAtOf(atStop));
                    check("...republished before the summary request itself, so at is no older than the request",
                        BusyAtOf(summaryValue) >= reportedAt && reportedAt != DateTime.MinValue);
                    summaryGate.Set();
                    bool cleared = pumpUntil(delegate { return BusyNow(host) == ""; });
                    check("...and is cleared once the summary is written", cleared);

                    // ---- a transcription that fails still clears it ----
                    transcribing.Reset();
                    transcriptionThrows = true;
                    Thread.Sleep(1100);   // a distinct second, so the next capture gets its own folder
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(80);
                    module.StopRecordingForSelfTest();
                    transcribing.Wait(TimeSpan.FromSeconds(10));
                    bool clearedAfterThrow = pumpUntil(delegate { return BusyNow(host) == ""; });
                    transcriptionThrows = false;
                    check("a transcription that throws still clears the flag", clearedAfterThrow);

                    // ---- a summary that fails still clears it ----
                    summarizing.Reset();
                    summaryFails = true;
                    Thread.Sleep(1100);
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(80);
                    module.StopRecordingForSelfTest();
                    summarizing.Wait(TimeSpan.FromSeconds(10));
                    bool clearedAfterFail = pumpUntil(delegate { return BusyNow(host) == ""; });
                    summaryFails = false;
                    check("a summary that fails still clears the flag", clearedAfterFail);

                    // ---- overlapping spans: the stop's transcription and the Summary card's Validate ----
                    host.SettingsFor(Id).Set("summaryOn", "false");
                    transcribing.Reset();
                    transcribeGate.Reset();
                    validating.Reset();
                    validateGate.Reset();
                    Thread.Sleep(1100);
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(80);
                    module.StopRecordingForSelfTest();
                    transcribing.Wait(TimeSpan.FromSeconds(10));
                    OptionsPane pane = host.OptionsPanes[0];
                    Task<string> validate = PaneActionFor(pane, "Summary (local AI)", "Validate")
                        .InvokeWithPendingAsync(CopyOf(pane.Load()));
                    validating.Wait(TimeSpan.FromSeconds(10));
                    ui.Drain();
                    check("two spans at once: the flag names the newest, the Summary Validate",
                        BusyPhaseOf(BusyNow(host)) == BusyValidating);
                    validateGate.Set();
                    pumpUntil(delegate { return validate.IsCompleted; });
                    ui.Drain();
                    check("...and stays up when the newer one ends, naming the one still open, a counter not a bool",
                        BusyPhaseOf(BusyNow(host)) == BusyTranscribing);
                    transcribeGate.Set();
                    check("...and clears when the last one ends", pumpUntil(delegate { return BusyNow(host) == ""; }));

                    // ---- "Summarize a transcript..." raises it on the UI thread, before its background work ----
                    string transcriptFile = System.IO.Path.Combine(scratch, "old.transcript.txt");
                    System.IO.File.WriteAllText(transcriptFile, "Alice: we ship on Friday.");
                    host.PickedFiles = new List<string> { transcriptFile };
                    summarizing.Reset();
                    summaryGate.Reset();
                    Press(PaneActionFor(pane, "Summary (local AI)", "Summarize a transcript…"), CopyOf(pane.Load()), null,
                        TimeSpan.FromSeconds(5));
                    string manual = BusyNow(host);   // before anything is drained
                    check("Summarize a transcript raises the flag as summarizing before its background work: " + manual,
                        BusyPhaseOf(manual) == BusySummarizing);
                    summarizing.Wait(TimeSpan.FromSeconds(10));
                    summaryGate.Set();
                    check("...and clears it when the summary is written", pumpUntil(delegate { return BusyNow(host) == ""; }));

                    // ---- the host's shutdown with two spans open ----
                    transcribing.Reset();
                    transcribeGate.Reset();
                    validating.Reset();
                    validateGate.Reset();
                    Thread.Sleep(1100);
                    module.StartRecordingForSelfTest();
                    Thread.Sleep(80);
                    module.StopRecordingForSelfTest();
                    transcribing.Wait(TimeSpan.FromSeconds(10));
                    Task<string> heldValidate = PaneActionFor(pane, "Summary (local AI)", "Validate")
                        .InvokeWithPendingAsync(CopyOf(pane.Load()));
                    validating.Wait(TimeSpan.FromSeconds(10));
                    ui.Drain();
                    bool upBefore = BusyPhaseOf(BusyNow(host)) == BusyValidating;
                    int postsBefore = ui.Pending;
                    host.RaiseHostShutdown();
                    int postedDuringShutdown = ui.Pending - postsBefore;
                    check("the host's shutdown clears the flag synchronously with spans open, posting nothing",
                        upBefore && BusyNow(host) == "" && postedDuringShutdown == 0);
                    validateGate.Set();
                    pumpUntil(delegate { return heldValidate.IsCompleted; });
                    Thread.Sleep(100);
                    ui.Drain();
                    check("...and a span ending after it publishes nothing, though another is still open",
                        BusyNow(host) == "");
                    transcribeGate.Set();
                    Thread.Sleep(300);
                    ui.Drain();
                    module.Shutdown();
                }

                // ---- Shutdown with a span open: the Transcription card's Validate, held ----
                {
                    var host = newHost();
                    var module = new RemembranceModule();
                    module.Init(host);
                    OptionsPane pane = host.OptionsPanes[0];
                    runGate.Reset();
                    running.Reset();
                    Task<string> heldCheck = PaneActionFor(pane, "Transcription", "Validate").InvokeWithPendingAsync(CopyOf(pane.Load()));
                    running.Wait(TimeSpan.FromSeconds(10));
                    ui.Drain();
                    bool upBefore = BusyPhaseOf(BusyNow(host)) == BusyValidating;
                    int postsBefore = ui.Pending;
                    module.Shutdown();
                    check("Shutdown clears the flag before it returns, synchronously, with a span still open",
                        upBefore && BusyNow(host) == "" && ui.Pending == postsBefore);
                    // Not asserted: that the span ending after Shutdown publishes nothing. Two things hold it (the
                    // closed flag and the nulled host), so no single change could make such a check fail; the
                    // host-shutdown case above, which keeps the host, is the one that can.
                    runGate.Set();
                    pumpUntil(delegate { return heldCheck.IsCompleted; });
                    ui.Drain();
                }
            }
            catch (Exception ex) { check("busy flag: " + ex.Message, false); }
            finally
            {
                transcribeGate.Set();
                summaryGate.Set();
                validateGate.Set();
                runGate.Set();
                pullGate.Set();
                TranscribeWav = savedTranscribe;
                Summarize = savedSummarize;
                CheckWhisperRun = savedRun;
                IsReachable = savedReachable;
                ListModels = savedLister;
                PullModel = savedPull;
                SynchronizationContext.SetSynchronizationContext(previous);
                try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>An HttpMessageHandler that answers 200 with <paramref name="body"/> as the content and
        /// the given length in the headers, so the download's progress arithmetic runs.</summary>
        private sealed class ScriptedHandler : System.Net.Http.HttpMessageHandler
        {
            private readonly System.IO.Stream _body;
            private readonly long _length;
            internal ScriptedHandler(System.IO.Stream body, long length) { _body = body; _length = length; }
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StreamContent(_body),
                };
                response.Content.Headers.ContentLength = _length;
                return Task.FromResult(response);
            }
        }

        /// <summary>
        /// A response body that delivers its first <c>firstChunk</c> bytes and then blocks every read until
        /// the read's token is cancelled; with firstChunk equal to the payload length it delivers everything
        /// and ends. Stalled is set when the blocking read begins, which is after the caller has written the
        /// first chunk to its .part.
        /// </summary>
        private sealed class StallingBody : System.IO.Stream
        {
            private readonly byte[] _payload;
            private readonly int _firstChunk;
            private int _position;
            internal readonly ManualResetEventSlim Stalled = new ManualResetEventSlim(false);

            internal StallingBody(byte[] payload, int firstChunk) { _payload = payload; _firstChunk = firstChunk; }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_position < _firstChunk)
                {
                    int n = Math.Min(count, _firstChunk - _position);
                    Array.Copy(_payload, _position, buffer, offset, n);
                    _position += n;
                    return n;
                }
                if (_position >= _payload.Length) return 0;
                Stalled.Set();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // The download reads asynchronously; a synchronous read of the stalled half would hang the
                // suite, so it fails loudly instead.
                if (_position >= _firstChunk && _position < _payload.Length)
                    throw new NotSupportedException("the stalling half is only readable asynchronously");
                return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { return _position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        /// <summary>An HttpMessageHandler that accepts the request and never answers: a black-holed proxy.</summary>
        private sealed class StalledHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var stalled = new TaskCompletionSource<System.Net.Http.HttpResponseMessage>();
                cancellationToken.Register(delegate { stalled.TrySetCanceled(cancellationToken); });
                return stalled.Task;
            }
        }
    }
}
