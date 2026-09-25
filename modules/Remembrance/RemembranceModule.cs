using System;
using System.Collections.Generic;
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

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = Id,
            Name = "Remembrance",
            Version = "1.0.11",  // 1.0.11: closing the app while recording no longer loses the recording. Every
                                  //         part of Stop ran inside a Task.Run nothing waited for, so the process
                                  //         exited mid-AudioRecorder.Stop(): no mixed WAV, no transcript, and two
                                  //         scratch files left with unfinalised RIFF headers that Purge deletes
                                  //         after 72 hours. The save is synchronous on shutdown now; transcription
                                  //         is skipped and both the status line and the log say so.
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
                                 // 1.0.7: a blocked download says what to do instead.   // 1.0.7: a blocked download now says what to do instead.
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
                                 // 1.0.6: failures now report the whole exception chain.   // 1.0.6: failures now report the whole exception chain.
                                 //        .NET renders a TLS fault as "The SSL connection could
                                 //        not be established, see inner exception" -- a message
                                 //        that names the information you need and withholds it --
                                 //        and three catch blocks passed only ex.Message through
                                 //        to the pane, so the cause was unknowable from the UI.   // 1.0.5: the summary dropdown fills ITSELF on first open, and
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
            MinHostVersion = "1.0.0",
            // Network is for two user-initiated local/upstream calls and nothing else: fetching whisper.cpp
            // from its GitHub release + Hugging Face, and talking to a LOOPBACK Ollama for the summary. There
            // is deliberately no cloud transcription or cloud summary path, because a recording can be
            // privileged or consent-regulated audio.
            // Speech was MISSING until 2026-09-17, found by an ABI audit. Announce() is how this
            // module reports everything it does -- "Recording started", "Transcript ready", "Summary
            // ready", "Snapshot saved" -- and it goes through IHost.SayAll from eight call sites. The
            // consent line is an affirmative claim about what a module does, and speech was absent
            // from it while being this module's only user-visible channel.
            Permissions = ModulePermissions.Speech
                | ModulePermissions.Microphone | ModulePermissions.SystemAudio
                | ModulePermissions.ScreenContext | ModulePermissions.Hotkey | ModulePermissions.Storage
                | ModulePermissions.Network,
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

            host.AddOptionsPane(BuildOptionsPane());
            host.AddTrayItems(new[] { BuildRecordTrayItem(), BuildSnapshotTrayItem() });
            RegisterHotkeys();

            _purgeTimer = new System.Windows.Forms.Timer { Interval = PurgeIntervalMs };
            _purgeHandler = delegate { RunPurge(); };
            _purgeTimer.Tick += _purgeHandler;
            _purgeTimer.Start();
            RunPurge();

            _hostShutdownHandler = OnHostShutdown;
            try { host.HostShutdown += _hostShutdownHandler; } catch { }
        }

        public void Shutdown()
        {
            try { if (_recording) StopRecording(shuttingDown: true); } catch { }
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
        private void OnHostShutdown() { try { if (_recording) StopRecording(shuttingDown: true); } catch { } }

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
            try
            {
                MeetingContext mc = MeetingContext.Parse(_host.ReadContext(MeetingContext.Key));
                _currentMeetingName = mc.Name;
                _currentAttendees = mc.Attendees;

                var store = new CaptureStore(_settings.Get("storageLocation", CaptureStore.DefaultRoot()),
                    _settings.GetBool("folderPerCapture", true));
                _current = store.NewCapture(mc.Name, DateTimeOffset.Now);
                _currentBase = _current.BaseName;

                _recorder = new AudioRecorder();
                _recorder.Start(_current.Audio,
                    _settings.GetBool("sysEnabled", true), _settings.Get("sysDevice", ""),
                    _settings.GetBool("micEnabled", true), _settings.Get("micDevice", ""));
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
                _current = null;
            }
        }

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
        /// length runs only in the RecordingStopped handler and in DisposeSource. CaptureStore.Purge
        /// then deletes them after 72 hours, so a 45-minute meeting became nothing at all.
        ///
        /// What is NOT waited for on shutdown is transcription: Whisper on a long recording takes
        /// minutes, and the audio is the irreplaceable part. It is saved and can be transcribed later;
        /// the status line and the log both say so rather than implying a transcript exists.
        ///
        /// THE COST IS REAL AND IS NOT A FEW MILLISECONDS. AudioRecorder.Stop waits up to 10 seconds per
        /// source (there are two), and then runs MixToWhisperWav, which reads both scratch WAVs, downmixes
        /// to mono, resamples to 16 kHz and writes the result -- for a 45-minute capture that is hundreds
        /// of millions of samples. So closing the app WHILE RECORDING can block for tens of seconds on the
        /// UI thread. That is the deliberate trade: the alternative shipped for months and simply lost the
        /// meeting. It costs nothing on any shutdown where nothing is recording, which is all of them but
        /// the one that matters.
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
            _lastStatus = "Saving: " + (paths != null ? paths.BaseName : "");
            Announce("Recording stopped. Saving and transcribing…");

            if (shuttingDown)
            {
                try
                {
                    recorder.Stop();
                    recorder.Dispose();
                    _lastStatus = "Saved, not transcribed (app closed): " + paths.BaseName;
                    try { _host.Log(Id, "stopped on shutdown: audio saved as " + paths.BaseName
                                       + ", transcription skipped"); } catch { }
                }
                catch (Exception ex)
                {
                    _lastStatus = "Stop failed: " + ex.Message;
                    try { _host.Log(Id, "stop on shutdown failed: " + ex.Message); } catch { }
                }
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    string wav = recorder.Stop();
                    recorder.Dispose();
                    bool did;
                    string transcript = Transcriber.Transcribe(wav ?? paths.Audio, paths.Transcript, whisperExe, model,
                        meetingName, attendees, out did);
                    _lastStatus = (did ? "Transcribed: " : "Saved (Whisper not set up): ") + paths.BaseName;
                    Announce(did ? "Transcript ready." : "Recording saved. Set up Whisper to transcribe it.");

                    // Only worth summarizing a transcript Whisper actually produced: the stub text is setup
                    // instructions, and summarizing those would be nonsense dressed up as a meeting summary.
                    if (did && summaryOn && !string.IsNullOrWhiteSpace(summaryModel))
                    {
                        bool wrote = await WriteSummaryAsync(summaryEndpoint, summaryModel,
                            string.IsNullOrWhiteSpace(meetingName) ? paths.BaseName : meetingName,
                            transcript, paths.Summary).ConfigureAwait(false);
                        _lastStatus = (wrote ? "Transcript + summary: " : "Transcript (summary failed): ") + paths.BaseName;
                        if (wrote) Announce("Summary ready.");
                    }
                }
                catch (Exception ex)
                {
                    _lastStatus = "Stop failed: " + ex.Message;
                    try { _host.Log(Id, "stop/transcribe failed: " + ex.Message); } catch { }
                }
            });
        }

        private void TakeSnapshot()
        {
            try
            {
                string dir, prefix;
                if (_current != null) { dir = _current.Directory; prefix = _current.SnapshotPrefix; }
                else
                {
                    var store = new CaptureStore(_settings.Get("storageLocation", CaptureStore.DefaultRoot()),
                        _settings.GetBool("folderPerCapture", true));
                    dir = store.Root;
                    System.IO.Directory.CreateDirectory(dir);
                    prefix = "snap";
                }
                string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
                string png = System.IO.Path.Combine(dir, prefix + " " + stamp + ".png");
                Announce(ScreenSnapshot.Capture(png) ? "Snapshot saved." : "Snapshot failed.");
            }
            catch (Exception ex) { try { _host.Log(Id, "snapshot failed: " + ex.Message); } catch { } }
        }

        // Marshal a host call to the UI thread (transcription completes on a background task).
        private void Announce(string text)
        {
            if (_ui != null) _ui.Post(delegate { try { _host.SayAll(text); } catch { } }, null);
            else try { _host.SayAll(text); } catch { }
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
            if (_ui != null) _ui.Post(delegate { try { write(); } catch { } }, null);
            else try { write(); } catch { }
        }

        private void RunPurge()
        {
            string root = _settings.Get("storageLocation", CaptureStore.DefaultRoot());
            bool perCapture = _settings.GetBool("folderPerCapture", true);
            Task.Run(() => { try { new CaptureStore(root, perCapture).Purge(); } catch { } });
        }

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
        /// both dropdowns in the same way. "Find local summary models" would discover the models, save
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
                _settings.Save();
                _lastStatus = "Whisper found.";
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
        /// the list was only ever filled by the "Find local summary models" button, so until someone
        /// guessed that a button was a PREREQUISITE rather than a refresh, the control was an empty
        /// box next to a ticked-looking feature. The same argument as the Whisper paths: the probe is
        /// free, so there is no reason to make anyone click for it.
        ///
        /// Bounded and off the UI thread. This runs inside Load, during a pane build, so a hung port
        /// has to cost a moment rather than the settings window; Task.Run keeps it off the captured
        /// context and the Wait caps it. A loopback answer is milliseconds and a refused connection
        /// is immediate, so the cap only ever bites on something genuinely wrong.
        /// </summary>
        private void AutoDiscoverModelsOnce()
        {
            if (_modelsProbed) return;
            _modelsProbed = true;
            if (!string.IsNullOrWhiteSpace(_settings.Get("summaryModelsCache", ""))) return;
            string endpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            try
            {
                Task<IReadOnlyList<string>> probe = Task.Run(
                    () => OllamaSummarizer.ListModelsAsync(endpoint, CancellationToken.None));
                if (!probe.Wait(TimeSpan.FromSeconds(3))) return;
                IReadOnlyList<string> models = probe.Result;
                if (models == null || models.Count == 0) return;
                _settings.Set("summaryModelsCache", string.Join("|", models));
                _settings.Save();
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

        private SettingField[] BuildOptionsPane_Schema()
        {
            string[] renderNames = DeviceOptions(AudioDevices.RenderDevices(), _settings.Get("sysDevice", ""));
            string[] micNames = DeviceOptions(AudioDevices.CaptureDevices(), _settings.Get("micDevice", ""));
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
                new SettingField { Id = "folderPerCapture", Label = "Create a folder per capture", Kind = SettingKind.Bool, Group = "Storage" },

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
                    new PaneAction { Label = "Set up Whisper for me…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = SetUpWhisperAsync },
                    new PaneAction { Label = "Find an installed Whisper", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(DetectWhisper()) },
                    // Between the automatic route and the manual one, because that is the order
                    // a stuck user needs: it failed, here are the files, now point at them.
                    new PaneAction { Label = "Open the download pages…", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(OpenWhisperDownloads()) },
                    new PaneAction { Label = "Browse for whisper-cli…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperExe", "whisper-cli", new[] { "exe" })) },
                    new PaneAction { Label = "Browse for a model…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperModel", "Whisper model", new[] { "bin" })) },
                    new PaneAction { Label = "Transcribe a WAV file…", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(TranscribeExisting()) },

                    new PaneAction { Label = "Find local summary models", Group = "Summary (local AI)", ReloadPaneAfter = true,
                        InvokeAsync = RefreshSummaryModelsAsync },
                    new PaneAction { Label = "Download that model", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = DownloadRecommendedModelAsync },
                    new PaneAction { Label = "Get Ollama (opens the site)", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(OpenOllamaSite()) },
                    new PaneAction { Label = "Test the summarizer", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = TestSummarizerAsync },
                    new PaneAction { Label = "Summarize a transcript…", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(SummarizeExisting()) },
                },
                // RefreshDynamicOptions runs HERE, not in the initialiser below, because Load is the
                // only thing the host promises to call before it reads Schema on every build.
                Load = () =>
                {
                    AutoDetectWhisperOnce();
                    AutoDiscoverModelsOnce();
                    RefreshDynamicOptions();
                    return new Dictionary<string, string>
                {
                    ["sysEnabled"] = _settings.GetBool("sysEnabled", true) ? "true" : "false",
                    ["sysDevice"] = DeviceValue(_settings.Get("sysDevice", ""), _sysDeviceField.Options),
                    ["micEnabled"] = _settings.GetBool("micEnabled", true) ? "true" : "false",
                    ["micDevice"] = DeviceValue(_settings.Get("micDevice", ""), _micDeviceField.Options),
                    ["recordHotkey"] = _settings.Get("recordHotkey", DefaultRecordHotkey),
                    ["snapshotHotkey"] = _settings.Get("snapshotHotkey", DefaultSnapshotHotkey),
                    ["storageLocation"] = _settings.Get("storageLocation", ""),
                    ["folderPerCapture"] = _settings.GetBool("folderPerCapture", true) ? "true" : "false",
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
                },
                Save = values =>
                {
                    string v;
                    SaveBool(values, "sysEnabled");
                    if (values.TryGetValue("sysDevice", out v)) _settings.Set("sysDevice", (v ?? "").Trim());
                    SaveBool(values, "micEnabled");
                    if (values.TryGetValue("micDevice", out v)) _settings.Set("micDevice", (v ?? "").Trim());
                    if (values.TryGetValue("recordHotkey", out v)) _settings.Set("recordHotkey", (v ?? "").Trim());
                    if (values.TryGetValue("snapshotHotkey", out v)) _settings.Set("snapshotHotkey", (v ?? "").Trim());
                    if (values.TryGetValue("storageLocation", out v)) _settings.Set("storageLocation", (v ?? "").Trim());
                    SaveBool(values, "folderPerCapture");
                    if (values.TryGetValue("whisperExe", out v)) _settings.Set("whisperExe", (v ?? "").Trim());
                    if (values.TryGetValue("whisperModel", out v)) _settings.Set("whisperModel", (v ?? "").Trim());
                    // The dropdown shows a human label ("base.en (~142 MB, recommended)"); store the file id.
                    if (values.TryGetValue("whisperModelChoice", out v)) _settings.Set("whisperModelChoice", ModelIdFromDisplay(v));
                    SaveBool(values, "summaryOn");
                    if (values.TryGetValue("ollamaEndpoint", out v)) _settings.Set("ollamaEndpoint", (v ?? "").Trim());
                    // The placeholder is a label, not a model. Storing it would send "(none found -
                    // is Ollama running?)" to /api/generate as a tag.
                    if (values.TryGetValue("summaryModel", out v))
                    {
                        string picked = (v ?? "").Trim();
                        if (picked == NoModelsPlaceholder) picked = "";
                        _settings.Set("summaryModel", picked);
                    }
                    // The dropdown shows "gemma4:12b (7.0 GB) -- recommended"; store the tag alone.
                    if (values.TryGetValue("recommendedModel", out v))
                        _settings.Set("recommendedModel", OllamaSummarizer.RecommendedIdFromDisplay(v));
                    bool ok = _settings.Save();
                    RegisterHotkeys();   // a changed combo takes effect without a restart
                    return ok;
                },
            };
        }

        private void SaveBool(IReadOnlyDictionary<string, string> values, string key)
        {
            string v; bool b;
            if (values.TryGetValue(key, out v) && bool.TryParse(v, out b)) _settings.Set(key, b ? "true" : "false");
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
                s += "  |  ⚠ Remote Desktop session: the machine's real mic and speakers are not presented here, so recording won't work. Run on the machine's own console. (Device dropdowns are read at startup; restart there to populate them.)";
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
                    _settings.Save();
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
                    _settings.Save();
                    return "✓ " + label + ": " + System.IO.Path.GetFileName(dlg.FileName);
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        // Transcribe an existing WAV the user picks (e.g. a kept recording, or one made before Whisper was set
        // up). Writes <name>.transcript.txt beside it. Runs Whisper on a background task so the pane stays live.
        private string TranscribeExisting()
        {
            string whisperExe = _settings.Get("whisperExe", "");
            string model = _settings.Get("whisperModel", "");
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
                    Task.Run(() =>
                    {
                        try
                        {
                            bool did;
                            Transcriber.Transcribe(wav, transcript, whisperExe, model, name, null, out did);
                            _lastStatus = did ? "Transcribed: " + name : "Transcription failed: " + name;
                            Announce(did ? "Transcript ready." : "Transcription failed.");
                        }
                        catch (Exception ex) { try { _host.Log(Id, "manual transcribe failed: " + ex.Message); } catch { } }
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

        /// <summary>Detect an existing Whisper and adopt its paths. Cheap, offline, and tried before any
        /// download: a box provisioned by scripts-utilities\scripts\install-whisper.ps1 already has one.</summary>
        private string DetectWhisper()
        {
            try
            {
                string exe, model;
                if (!WhisperInstaller.TryDetect(DataDirectory(), out exe, out model))
                {
                    return "✗ No Whisper found. Use \"Set up Whisper for me…\" to fetch it.";
                }
                _settings.Set("whisperExe", exe);
                _settings.Set("whisperModel", model);
                _settings.Save();
                _lastStatus = "Whisper found.";
                return "✓ found " + System.IO.Path.GetFileName(exe) + " + " + System.IO.Path.GetFileName(model);
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>
        /// One-click setup: adopt an existing install if there is one, else fetch the CLI and the chosen model
        /// from upstream into this module's own storage and prove the pair actually runs.
        /// </summary>
        private async Task<string> SetUpWhisperAsync()
        {
            try
            {
                string exe, model;
                if (WhisperInstaller.TryDetect(DataDirectory(), out exe, out model))
                {
                    _settings.Set("whisperExe", exe);
                    _settings.Set("whisperModel", model);
                    _settings.Save();
                    _lastStatus = "Whisper found.";
                    return "✓ already installed: " + System.IO.Path.GetFileName(exe) + " + " + System.IO.Path.GetFileName(model);
                }

                string modelId = WhisperInstaller.ResolveModelId(_settings.Get("whisperModelChoice", WhisperInstaller.DefaultModelId));
                string root = WhisperInstaller.InstallRoot(DataDirectory());
                _lastStatus = "Setting up Whisper…";

                // Progress lands on the module status line rather than the pane: a PaneAction reports once,
                // when it returns, so a multi-hundred-megabyte download would otherwise look hung.
                WhisperInstaller.InstallResult result = await WhisperInstaller
                    .InstallAsync(root, modelId, p => { _lastStatus = "Whisper setup: " + p; }, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!result.Ok)
                {
                    _lastStatus = "Whisper setup failed.";
                    try { _host.Log(Id, "whisper setup failed: " + result.Message); } catch { }
                    return "✗ " + result.Message;
                }

                _settings.Set("whisperExe", result.ExePath);
                _settings.Set("whisperModel", result.ModelPath);
                _settings.Save();
                _lastStatus = "Whisper is ready.";
                return "✓ " + result.Message + " Model: " + System.IO.Path.GetFileName(result.ModelPath);
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
        /// Options for the summary-model dropdown: whatever the last "Find local summary models" discovered,
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

        private async Task<string> RefreshSummaryModelsAsync()
        {
            string endpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            try
            {
                IReadOnlyList<string> models = await OllamaSummarizer
                    .ListModelsAsync(endpoint, CancellationToken.None).ConfigureAwait(true);
                if (models.Count == 0)
                {
                    return "✗ No generation-capable model answered at " + OllamaSummarizer.NormalizeEndpoint(endpoint) +
                           ". Is Ollama running, and has it a non-embedding model pulled?";
                }
                _settings.Set("summaryModelsCache", string.Join("|", models));
                if (string.IsNullOrWhiteSpace(_settings.Get("summaryModel", ""))) _settings.Set("summaryModel", models[0]);
                _settings.Save();
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
        /// </summary>
        private async Task<string> DownloadRecommendedModelAsync()
        {
            string endpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            string id = OllamaSummarizer.RecommendedIdFromDisplay(
                _settings.Get("recommendedModel", OllamaSummarizer.DefaultRecommendedId));

            bool reachable = await OllamaSummarizer
                .IsReachableAsync(endpoint, CancellationToken.None).ConfigureAwait(true);
            if (!reachable)
                return "✗ Nothing is answering at " + OllamaSummarizer.NormalizeEndpoint(endpoint) +
                       ". Install Ollama first (there is a button for it below), then try again.";

            // Started rather than awaited, matching how this module already handles transcription and
            // summarising: a PaneAction reports once, when it returns, so awaiting gigabytes here
            // would leave the button dead and the pane looking hung for an hour.
            _lastStatus = "Downloading " + id + "...";
            // Discarded on purpose: this is fire-and-report, and awaiting it is the one thing
            // the comment above says not to do.
            _ = Task.Run(async () =>
            {
                try
                {
                    OllamaSummarizer.PullResult pull = await OllamaSummarizer.PullModelAsync(
                        endpoint, id, p => { _lastStatus = p; }, CancellationToken.None).ConfigureAwait(false);
                    if (!pull.Ok)
                    {
                        _lastStatus = "Download failed: " + pull.Message;
                        Announce("The model download failed.");
                        return;
                    }
                    // Select what was just fetched, and put it in the dropdown that offers it, or the
                    // user downloads 7 GB and still has nothing chosen. Marshalled, for the reason spelled
                    // out on PersistOnUi: this is a thread-pool continuation and Apply serialises the same
                    // dictionary on the UI thread. _lastStatus moves inside the write so the status line
                    // cannot claim "installed and selected" before the write has landed.
                    IReadOnlyList<string> models = await OllamaSummarizer
                        .ListModelsAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
                    PersistOnUi(delegate
                    {
                        _settings.Set("summaryModel", id);
                        if (models.Count > 0) _settings.Set("summaryModelsCache", string.Join("|", models));
                        _settings.Save();
                        _lastStatus = id + " is installed and selected.";
                    });
                    Announce("The summary model is ready.");
                }
                catch (Exception ex) { try { _host.Log(Id, "model pull failed: " + ex.Message); } catch { } }
            });
            return "Downloading " + id + " in the background. Reopen this pane to watch the Status line.";
        }

        /// <summary>Open the Ollama download page, for the case where the runtime is missing entirely.</summary>
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
        /// </summary>
        internal string OpenWhisperDownloadsForSelfTest() { return OpenWhisperDownloads(); }

        private string OpenWhisperDownloads()
        {
            string modelId = WhisperInstaller.ResolveModelId(
                _settings.Get("whisperModelChoice", WhisperInstaller.DefaultModelId));
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
                     + "  Save both, then use \"Browse for whisper-cli…\" and \"Browse for a model…\".";
            if (opened.Count == 0)
                return "✗ the host would not open " + string.Join(" or ", refused.ToArray());
            return "⚠ opened " + string.Join(", ", opened.ToArray())
                 + " but not " + string.Join(", ", refused.ToArray());
        }

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

        private async Task<string> TestSummarizerAsync()
        {
            string endpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
            string model = _settings.Get("summaryModel", "");
            if (string.IsNullOrWhiteSpace(model)) return "✗ Pick a summary model first (\"Find local summary models\").";
            try
            {
                OllamaSummarizer.SummaryResult result = await OllamaSummarizer.SummarizeAsync(
                    endpoint, model, "Connection test",
                    "Alice: we agreed to ship on Friday. Bob: I will write the release notes.",
                    null, CancellationToken.None).ConfigureAwait(true);
                if (!result.Ok) return "✗ " + result.Message;
                string preview = (result.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (preview.Length > 120) preview = preview.Substring(0, 120) + "…";
                return "✓ " + model + " answered: " + preview;
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>Summarize a transcript the user picks, writing &lt;name&gt;.summary.txt beside it.</summary>
        private string SummarizeExisting()
        {
            string model = _settings.Get("summaryModel", "");
            if (string.IsNullOrWhiteSpace(model)) return "✗ Pick a summary model first (\"Find local summary models\").";
            try
            {
                IReadOnlyList<string> picked = _host.PickFilesToOpen("Choose a transcript to summarize", "Transcript", new[] { "txt" });
                if (picked == null || picked.Count == 0) return "Unchanged.";
                string transcriptPath = picked[0];
                string name = System.IO.Path.GetFileNameWithoutExtension(transcriptPath);
                if (name.EndsWith(".transcript", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".transcript".Length);
                string summaryPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(transcriptPath), name + ".summary.txt");

                string endpoint = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint);
                Task.Run(async () =>
                {
                    try
                    {
                        string transcript = System.IO.File.ReadAllText(transcriptPath);
                        bool wrote = await WriteSummaryAsync(endpoint, model, name, transcript, summaryPath).ConfigureAwait(false);
                        _lastStatus = wrote ? ("Summarized: " + name) : ("Summary failed: " + name);
                        Announce(wrote ? "Summary ready." : "Could not summarize that transcript.");
                    }
                    catch (Exception ex) { try { _host.Log(Id, "manual summarize failed: " + ex.Message); } catch { } }
                });
                return "Summarizing " + name + "… it will be saved beside the transcript.";
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>Runs the summarizer and writes the file. Returns false without throwing on any failure:
        /// a summary is an extra, and losing it must never cost the transcript or the audio.</summary>
        private async Task<bool> WriteSummaryAsync(string endpoint, string model, string meetingName,
            string transcript, string summaryPath)
        {
            try
            {
                OllamaSummarizer.SummaryResult result = await OllamaSummarizer.SummarizeAsync(
                    endpoint, model, meetingName, transcript,
                    p => { _lastStatus = "Summary: " + p; }, CancellationToken.None).ConfigureAwait(false);
                if (!result.Ok || string.IsNullOrWhiteSpace(result.Text))
                {
                    try { _host.Log(Id, "summary failed: " + result.Message); } catch { }
                    return false;
                }
                System.IO.File.WriteAllText(summaryPath,
                    OllamaSummarizer.FileHeader(meetingName, model) + result.Text,
                    new System.Text.UTF8Encoding(false));
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
        /// Covers the pure decision logic only: capture naming and the purge classification, the shared-context
        /// parse, the Whisper asset/model/path selection, and the summarizer's chunking and response parsing.
        /// Deliberately NO audio and NO network -- device capture cannot run on a CI runner or under RDP, and a
        /// test that reached the network would fail for reasons that are not this module's fault. The live
        /// capture and download paths are verified by hand; this is the regression net around everything that
        /// can be checked deterministically.
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

            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var withName = new CaptureStore(scratch, true);
                CapturePaths named = withName.NewCapture("Sprint Review", new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero));
                check("a named capture uses '<meeting> - <stamp>'", named.BaseName.StartsWith("Sprint Review - 2026-08-27"));
                check("folder-per-capture nests the files", named.Audio.Replace('/', '\\').Contains(named.BaseName));
                check("transcript sits beside the audio", named.Transcript.EndsWith(".transcript.txt"));
                check("summary sits beside the transcript", named.Summary.EndsWith(".summary.txt"));

                var flat = new CaptureStore(scratch, false);
                CapturePaths unnamed = flat.NewCapture("", new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero));
                check("no meeting name falls back to a timestamp", unnamed.BaseName.StartsWith("2026-08-27"));
                check("flat mode prefixes files in the root", !unnamed.Audio.EndsWith("recording.wav"));
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
            WhisperInstaller.ReleaseAsset release = WhisperInstaller.ParseReleaseJson(
                "{\"assets\":[{\"name\":\"whisper-bin-x64.zip\",\"browser_download_url\":\"https://example.invalid/w.zip\"," +
                "\"digest\":\"sha256:" + new string('b', 64) + "\"}]}");
            check("release JSON yields the asset url", release != null && release.Url == "https://example.invalid/w.zip");
            check("release JSON yields the digest", release != null && release.Digest.StartsWith("sha256:"));
            check("release JSON with no assets yields null", WhisperInstaller.ParseReleaseJson("{\"assets\":[]}") == null);
            check("garbage release JSON yields null", WhisperInstaller.ParseReleaseJson("nope") == null);
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
                    var linkModule = new RemembranceModule();
                    linkModule.Init(linkHost);

                    linkModule._settings.Set("whisperModelChoice", "ggml-small.en.bin");
                    linkModule._settings.Save();
                    linkHost.OpenedLinks.Clear();
                    string said = linkModule.OpenWhisperDownloadsForSelfTest();

                    check("WITNESS the button opens exactly two pages, not one and not a guess",
                        linkHost.OpenedLinks.Count == 2);
                    check("WITNESS one of them is the human releases page, not the JSON API",
                        linkHost.OpenedLinks.Contains(WhisperInstaller.ReleasesPageUrl)
                        && WhisperInstaller.ReleasesPageUrl.IndexOf("api.github.com",
                               StringComparison.OrdinalIgnoreCase) < 0);
                    check("WITNESS the model link FOLLOWS the dropdown rather than being fixed",
                        linkHost.OpenedLinks.Contains(
                            WhisperInstaller.ModelUrl("ggml-small.en.bin")));

                    // And it changes when the choice changes, which is the half that would still
                    // pass against a hardcoded link if only the line above were asserted.
                    linkModule._settings.Set("whisperModelChoice", "ggml-tiny.en.bin");
                    linkModule._settings.Save();
                    linkHost.OpenedLinks.Clear();
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

            // Machine-dependent by nature, so this asserts only that it answers safely: the value
            // is 0, 1 or 2 when Defender reports one and null when it does not, never a throw.
            int? np = WhisperInstaller.NetworkProtectionState();
            check("reading the Network Protection policy answers without throwing",
                np == null || (np.Value >= 0 && np.Value <= 2));

            check("the install root is under the module's own storage",
                WhisperInstaller.InstallRoot(@"c:\data\remembrance").Replace('/', '\\') == @"c:\data\remembrance\whisper");
            check("probing includes the DevToolbox location",
                WhisperInstaller.ProbeRoots(@"c:\data\remembrance").Any(p => p.IndexOf("DevToolbox", StringComparison.OrdinalIgnoreCase) >= 0));

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
                // Seeded BEFORE Init on purpose: Init runs a purge over whatever storage root is
                // configured, and the default is the user's real Documents\Remembrance. A test must
                // not go deleting from there.
                paneHost.SettingsFor(Id).Set("storageLocation", paneScratch);
                // A non-empty cache short-circuits the discovery probe in Load. Without this the
                // suite would reach 127.0.0.1:11434 and score differently on a machine with Ollama
                // running than on one without -- and this module's tests are deliberately offline.
                paneHost.SettingsFor(Id).Set("summaryModelsCache", "alpha:1b|beta:7b");
                var paneModule = new RemembranceModule();
                paneModule.Init(paneHost);
                check("the module registers exactly one options pane", paneHost.OptionsPanes.Count == 1);

                OptionsPane pane = paneHost.OptionsPanes.Count > 0 ? paneHost.OptionsPanes[0] : null;
                check("the pane persists (it has both a Load and a Save)",
                    pane != null && pane.Load != null && pane.Save != null);

                if (pane != null && pane.Load != null && pane.Save != null)
                {
                    // An edit to every writable field at once, each with a value nothing else could
                    // produce, so a field that comes back holding someone else's value is visible too.
                    var edited = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> kv in pane.Load()) edited[kv.Key] = kv.Value;
                    edited["sysDevice"] = "Test Speakers";
                    edited["micDevice"] = "Test Microphone";
                    edited["recordHotkey"] = "Ctrl+Alt+9";
                    edited["snapshotHotkey"] = "Ctrl+Alt+8";
                    edited["summaryOn"] = "true";
                    edited["summaryModel"] = "test-model:1b";
                    edited["ollamaEndpoint"] = "http://127.0.0.1:99999";
                    edited["folderPerCapture"] = "false";
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
                    check("clearing folder-per-capture survives Apply",
                        valueOf(reopened, "folderPerCapture") == "false");

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
            // Port 1 has nothing listening, so the probe is refused instantly and this stays an
            // offline test rather than one that depends on whether Ollama happens to be up.
            string emptyScratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-remembrance-empty-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var emptyHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                emptyHost.SettingsFor(Id).Set("storageLocation", emptyScratch);
                emptyHost.SettingsFor(Id).Set("ollamaEndpoint", "http://127.0.0.1:1");
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
                try { if (System.IO.Directory.Exists(emptyScratch)) System.IO.Directory.Delete(emptyScratch, true); }
                catch { }
            }
            detail = sb.ToString();
            return ok;
        }
    }
}
