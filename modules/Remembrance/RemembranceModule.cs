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
            Version = "1.0.0",   // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
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
            Permissions = ModulePermissions.Microphone | ModulePermissions.SystemAudio
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

            try { host.HostShutdown += OnHostShutdown; } catch { }
        }

        public void Shutdown()
        {
            try { if (_recording) StopRecording(); } catch { }
            DisposeHotkeys();
            if (_purgeTimer != null)
            {
                try { _purgeTimer.Stop(); if (_purgeHandler != null) _purgeTimer.Tick -= _purgeHandler; _purgeTimer.Dispose(); }
                catch { }
                _purgeTimer = null;
            }
        }

        private void OnHostShutdown() { try { if (_recording) StopRecording(); } catch { } }

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
            try { IDisposable h = _host.RegisterHotkey(combo.Trim(), onPressed); if (h != null) _hotkeys.Add(h); }
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

        private void StopRecording()
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

        private void RunPurge()
        {
            string root = _settings.Get("storageLocation", CaptureStore.DefaultRoot());
            bool perCapture = _settings.GetBool("folderPerCapture", true);
            Task.Run(() => { try { new CaptureStore(root, perCapture).Purge(); } catch { } });
        }

        // --- options ---------------------------------------------------------------------------------

        private SettingField[] BuildOptionsPane_Schema()
        {
            string[] renderNames = AudioDevices.RenderDevices().Select(d => d.Name).ToArray();
            string[] micNames = AudioDevices.CaptureDevices().Select(d => d.Name).ToArray();
            return new[]
            {
                new SettingField { Id = "sysEnabled", Label = "Record system audio (what you hear)", Kind = SettingKind.Bool, Group = "Sources" },
                new SettingField { Id = "sysDevice", Label = "System output device", Kind = SettingKind.Enum, Options = renderNames, Group = "Sources" },
                new SettingField { Id = "micEnabled", Label = "Record microphone", Kind = SettingKind.Bool, Group = "Sources" },
                new SettingField { Id = "micDevice", Label = "Microphone device", Kind = SettingKind.Enum, Options = micNames, Group = "Sources" },

                new SettingField { Id = "recordHotkey", Label = "Start/stop hotkey (e.g. Ctrl+Alt+R)", Kind = SettingKind.Text, Group = "Hotkeys" },
                new SettingField { Id = "snapshotHotkey", Label = "Snapshot hotkey (e.g. Ctrl+Alt+S)", Kind = SettingKind.Text, Group = "Hotkeys" },

                new SettingField { Id = "storageLocation", Label = "Where recordings are stored (blank = Documents\\Remembrance)", Kind = SettingKind.Text, Group = "Storage" },
                new SettingField { Id = "folderPerCapture", Label = "Create a folder per capture", Kind = SettingKind.Bool, Group = "Storage" },

                new SettingField { Id = "whisperExe", Label = "whisper-cli path (offline transcription)", Kind = SettingKind.Text, Group = "Transcription" },
                new SettingField { Id = "whisperModel", Label = "Whisper model file (e.g. ggml-base.en.bin)", Kind = SettingKind.Text, Group = "Transcription" },
                new SettingField { Id = "whisperModelChoice", Label = "Model to fetch if you use the automatic setup", Kind = SettingKind.Enum,
                    Options = WhisperInstaller.Models.Select(m => m.Display).ToArray(), Group = "Transcription" },

                // Off by default: it is an extra dependency (a local Ollama) and an extra pass over the
                // recording, so it should be a choice rather than a surprise.
                new SettingField { Id = "summaryOn", Label = "Also write an AI summary next to the transcript", Kind = SettingKind.Bool, Group = "Summary (local AI)" },
                new SettingField { Id = "ollamaEndpoint", Label = "Local Ollama address", Kind = SettingKind.Text, Group = "Summary (local AI)" },
                new SettingField { Id = "summaryModel", Label = "Summary model", Kind = SettingKind.Enum,
                    Options = SummaryModelOptions(), Group = "Summary (local AI)" },

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
                    new PaneAction { Label = "Browse for whisper-cli…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperExe", "whisper-cli", new[] { "exe" })) },
                    new PaneAction { Label = "Browse for a model…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperModel", "Whisper model", new[] { "bin" })) },
                    new PaneAction { Label = "Transcribe a WAV file…", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(TranscribeExisting()) },

                    new PaneAction { Label = "Find local summary models", Group = "Summary (local AI)", ReloadPaneAfter = true,
                        InvokeAsync = RefreshSummaryModelsAsync },
                    new PaneAction { Label = "Test the summarizer", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = TestSummarizerAsync },
                    new PaneAction { Label = "Summarize a transcript…", Group = "Summary (local AI)", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(SummarizeExisting()) },
                },
                Load = () => new Dictionary<string, string>
                {
                    ["sysEnabled"] = _settings.GetBool("sysEnabled", true) ? "true" : "false",
                    ["sysDevice"] = _settings.Get("sysDevice", ""),
                    ["micEnabled"] = _settings.GetBool("micEnabled", true) ? "true" : "false",
                    ["micDevice"] = _settings.Get("micDevice", ""),
                    ["recordHotkey"] = _settings.Get("recordHotkey", DefaultRecordHotkey),
                    ["snapshotHotkey"] = _settings.Get("snapshotHotkey", DefaultSnapshotHotkey),
                    ["storageLocation"] = _settings.Get("storageLocation", ""),
                    ["folderPerCapture"] = _settings.GetBool("folderPerCapture", true) ? "true" : "false",
                    ["whisperExe"] = _settings.Get("whisperExe", ""),
                    ["whisperModel"] = _settings.Get("whisperModel", ""),
                    ["whisperModelChoice"] = ModelChoiceDisplay(),
                    ["summaryOn"] = _settings.GetBool("summaryOn", false) ? "true" : "false",
                    ["ollamaEndpoint"] = _settings.Get("ollamaEndpoint", OllamaSummarizer.DefaultEndpoint),
                    ["summaryModel"] = _settings.Get("summaryModel", ""),
                    ["status"] = StatusLine(),
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
                    if (values.TryGetValue("summaryModel", out v)) _settings.Set("summaryModel", (v ?? "").Trim());
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

            if (options.Count == 0) options.Add("");   // an empty closed dropdown cannot be rendered
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
            check("the summary file header names the model",
                OllamaSummarizer.FileHeader("Standup", "dolphin3").Contains("dolphin3"));

            detail = sb.ToString();
            return ok;
        }
    }
}
