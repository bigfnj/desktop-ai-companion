using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;
using Timer = System.Windows.Forms.Timer;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// AgentFlow: the companion notices when a coding agent is sitting blocked on a permission
    /// prompt, and says so.
    ///
    /// The framing is presence. A dashboard can tell you an agent is idle; it cannot notice that
    /// your agent has been stuck for nine minutes while you were reading something else. That is
    /// what a pet on your screen is for, and it is the half of this idea nobody else has built --
    /// every comparable tool presses the button and none of them tells a human anything.
    ///
    /// <para>
    /// OBSERVE ONLY, DELIBERATELY. This module never answers a prompt, and that is a standing
    /// decision rather than an unfinished feature. Four public tools that do answer were read at
    /// source level (see docs/agentflow/README.md) and ALL FOUR press a wider grant than the one
    /// they advertise -- an "always allow", an "accept all", or a blind Enter on whichever row the
    /// cursor happens to rest on. Four authors, four architectures, one destination. Pressing
    /// anything is therefore not in this version, and the safety classifier that would be needed
    /// first already exists as a research harness beside that document.
    /// </para>
    ///
    /// <para>
    /// WHAT IT READS, AND WHY THAT NEEDS SAYING. It reads the agents' own JSONL transcripts, which
    /// contain every command run, every path touched, and the full text of what the user typed.
    /// Nothing is sent anywhere -- this module makes no network call at all -- and nothing from a
    /// transcript is ever logged or spoken: the bubble names a tool and a project folder, never a
    /// command, an argument or a path. It declares
    /// <see cref="ModulePermissions.AgentTranscripts"/> so that read is visible in the Modules pane
    /// BEFORE a user installs it.
    /// </para>
    /// </summary>
    public sealed class AgentFlowModule : IModule
    {
        private const string SettingEnabled = "enabled";
        private const string SettingThreshold = "thresholdSeconds";
        private const string SettingWatchClaude = "watchClaude";
        private const string SettingWatchCodex = "watchCodex";
        private const string SettingCooldown = "cooldownSeconds";
        private const string SettingAnimate = "animate";

        /// <summary>How often the transcripts are re-read. Cheap: a few small files, off the UI thread.</summary>
        private const int TickMilliseconds = 10 * 1000;

        /// <summary>Only transcripts written this recently are considered live.</summary>
        private const double ActiveWindowSeconds = 900.0;

        private IHost _host;
        private IModuleSettings _settings;
        private Timer _timer;
        private EventHandler _tickHandler;
        private SynchronizationContext _ui;
        private NotifyBudget _budget;
        private TrayItem _trayEntry;

        // Written on the UI thread only, read by the tray's DynamicText on the UI thread.
        private string _status = "no agents seen yet";
        private int _blockedCount;

        // Guards against overlapping scans when a poll outlives its interval.
        private int _scanning;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "agentflow",
            Name = "AgentFlow",
            Version = "1.0.0",   // 1.0.0: first version. Notify half only, observe-only by decision.
            // 1.0.0 rather than the release that first ships AgentTranscripts, because a module
            // binds the HOST's single shared Contracts.dll and at development time that is this
            // repo's build, which already has the flag.
            //
            // ⚠ RAISE THIS BEFORE THIS MODULE IS EVER PUBLISHED. The catalog offers a module to
            // every installed host, and an older host's Contracts.dll has no name for bit 11, so
            // the Modules pane would render the permission as a bare number instead of disclosing
            // what it does -- which defeats the entire point of adding the flag. The load gate
            // cannot catch that for us: the enum value is a compile-time literal in this
            // assembly's IL, so an old host loads the module happily and just mislabels it.
            MinHostVersion = "1.0.0",
            // Speech for the bubble, Storage for its own settings, AgentTranscripts for the read
            // that is the whole feature. Nothing else: no Network (it never makes a request), no
            // ScreenContext (it does not look at the screen), no Hotkey, no Audio.
            Permissions = ModulePermissions.Speech
                          | ModulePermissions.Storage
                          | ModulePermissions.AgentTranscripts,
        };

        public void Init(IHost host)
        {
            _host = host;
            // Null when the convention self-test host is driving us, and that is not hypothetical:
            // ModuleConventionSelfTest returns null from both GetSettings and GetStorage on purpose.
            _settings = host.GetSettings(Info.Id) ?? new MemoryModuleSettings();
            _budget = new NotifyBudget();

            // Captured here because Init runs on the host's UI thread. The scan runs on a worker
            // and must never touch IHost from there -- every service on that interface is
            // documented UI-thread-only -- so results come back through this.
            _ui = SynchronizationContext.Current;

            host.AddTrayItems(new List<TrayItem>
            {
                (_trayEntry = new TrayItem
                {
                    Label = "AgentFlow",
                    Group = 40,
                    Order = 5,
                    IconPng = LoadIconResource("agentflow.png"),
                    // The host re-evaluates this every time the menu opens, which is the only push
                    // channel a module has into the tray. A module cannot update an OPEN menu, so
                    // this is a snapshot by design rather than a live counter.
                    DynamicText = TrayText,
                    BuildChildren = BuildMenu,
                }),
            });

            host.AddOptionsPane(new OptionsPane
            {
                Title = "AgentFlow",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = SettingEnabled,
                        Label = "Tell me when a coding agent is waiting for an answer",
                        Kind = SettingKind.Bool,
                        Group = "AgentFlow",
                    },
                    new SettingField
                    {
                        Id = SettingThreshold,
                        Label = "Say something after this many seconds of waiting",
                        Kind = SettingKind.Int,
                        Min = 10,
                        Max = 600,
                        Group = "AgentFlow",
                    },
                    new SettingField
                    {
                        Id = SettingCooldown,
                        Label = "Leave at least this many seconds between messages",
                        Kind = SettingKind.Int,
                        Min = 30,
                        Max = 3600,
                        Group = "AgentFlow",
                    },
                    new SettingField
                    {
                        Id = SettingWatchClaude,
                        Label = "Watch Claude Code",
                        Kind = SettingKind.Bool,
                        Group = "Which agents",
                    },
                    new SettingField
                    {
                        Id = SettingWatchCodex,
                        Label = "Watch Codex",
                        Kind = SettingKind.Bool,
                        Group = "Which agents",
                    },
                    new SettingField
                    {
                        Id = SettingAnimate,
                        Label = "Play an animation as well as speaking",
                        Kind = SettingKind.Bool,
                        Group = "AgentFlow",
                    },
                    // Info rather than a switch, and that is the point: this is not a setting.
                    new SettingField
                    {
                        Id = "aboutAnswering",
                        Label = "AgentFlow never answers a prompt for you. It only tells you one "
                                + "is waiting. Every comparable tool that does answer was found to "
                                + "press a wider grant than it advertises, so answering is not "
                                + "offered here and is not a switch you can turn on.",
                        Kind = SettingKind.Info,
                        Group = "What it will not do",
                    },
                    new SettingField
                    {
                        Id = "aboutReading",
                        Label = "It reads the transcript files your coding agent already writes, "
                                + "to see which tool call is waiting. Nothing is sent anywhere, "
                                + "and no command, path or prompt text is ever written to the "
                                + "diagnostic log or shown in a speech bubble.",
                        Kind = SettingKind.Info,
                        Group = "What it reads",
                    },
                    new SettingField
                    {
                        Id = "aboutAutoMode",
                        Label = "In auto mode it stands down and says so. The permission rules stop "
                                + "predicting which calls will prompt there, so it would be wrong "
                                + "roughly 250 times for every time it was right.",
                        Kind = SettingKind.Info,
                        Group = "What it reads",
                    },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Check now",
                        InvokeAsync = CheckNowAsync,
                        Group = "AgentFlow",
                    },
                },
            });

            _tickHandler = OnTick;
            _timer = new Timer { Interval = TickMilliseconds };
            _timer.Tick += _tickHandler;
            _timer.Start();
            OnTick(null, EventArgs.Empty);   // don't make the user wait a full interval after launch
        }

        public void Shutdown()
        {
            if (_timer != null)
            {
                _timer.Stop();
                if (_tickHandler != null) _timer.Tick -= _tickHandler;
                _timer.Dispose();
                _timer = null;
            }
            _tickHandler = null;
            _trayEntry = null;
            _budget = null;
            _ui = null;
            _host = null;
        }

        // ---- polling --------------------------------------------------------

        private void OnTick(object sender, EventArgs args)
        {
            if (!Enabled) return;
            // A poll that outlives its interval must not stack: Interlocked, not a bool, because
            // the completion runs on a worker and the tick on the UI thread.
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;

            double threshold = ThresholdSeconds;
            bool watchClaude = WatchClaude, watchCodex = WatchCodex;

            // Reading and parsing transcripts is file IO plus JSON, so it never runs on the tick.
            // Nothing inside this task touches _host.
            Task.Run(() =>
            {
                List<Detection> results = null;
                try
                {
                    results = Scan(watchClaude, watchCodex, threshold);
                }
                catch (Exception)
                {
                    // A scan that throws must not take the app's UI thread down with it, and there
                    // is nothing here worth surfacing to a user: the next tick tries again.
                    results = null;
                }
                finally
                {
                    Volatile.Write(ref _scanning, 0);
                }
                if (results != null) PostToUi(() => Apply(results));
            });
        }

        /// <summary>
        /// Read every live transcript and evaluate it. Pure enough to call from a worker: no host,
        /// no UI, no module state beyond the settings snapshot handed in.
        /// </summary>
        internal static List<Detection> Scan(bool watchClaude, bool watchCodex, double threshold)
        {
            int sources;
            RuleSet rules = RuleLoader.Load(RuleLoader.DefaultPaths(), out sources);
            DateTime now = DateTime.UtcNow;
            var results = new List<Detection>();

            if (watchClaude)
            {
                foreach (string path in TranscriptReader.ActiveTranscripts(
                             TranscriptReader.ClaudeRoot, ActiveWindowSeconds, "subagents"))
                {
                    AgentSession session = TranscriptReader.ReadClaude(path);
                    Detection detection = BlockedDetector.Evaluate(session, rules, threshold, now);
                    if (detection != null) results.Add(detection);
                }
            }
            if (watchCodex)
            {
                foreach (string path in TranscriptReader.ActiveTranscripts(
                             TranscriptReader.CodexRoot, ActiveWindowSeconds, null))
                {
                    AgentSession session = TranscriptReader.ReadCodex(path);
                    Detection detection = BlockedDetector.Evaluate(session, rules, threshold, now);
                    if (detection != null) results.Add(detection);
                }
            }
            return results;
        }

        /// <summary>UI thread: speak at most one line, and refresh the tray label.</summary>
        private void Apply(List<Detection> results)
        {
            if (_host == null || _budget == null) return;   // shut down while the scan was running

            DateTime now = DateTime.UtcNow;
            var live = new List<string>();
            int blocked = 0, stoodDown = 0;
            Detection speakThis = null;

            foreach (Detection detection in results)
            {
                if (detection.Outcome == DetectionOutcome.Blocked)
                {
                    blocked++;
                    live.Add(NotifyBudget.KeyFor(detection));
                    if (speakThis == null) speakThis = detection;
                }
                else if (detection.Outcome == DetectionOutcome.StoodDownAutoMode)
                {
                    stoodDown++;
                }
                else if (detection.Outcome == DetectionOutcome.AdapterSuspect)
                {
                    Log("transcript parsed with no tool calls in session "
                        + Short(detection.Session) + " -- adapter may be stale");
                }
            }

            // Drop one-shot keys for prompts that are no longer outstanding, so the same session
            // can notify again about a genuinely new prompt without ever repeating an old one.
            _budget.Retain(live);

            _blockedCount = blocked;
            _status = DescribeStatus(results.Count, blocked, stoodDown);

            if (speakThis == null) return;
            string refusal;
            if (!_budget.ShouldAnnounce(speakThis, now, out refusal))
            {
                // Logged, because a notification that did not happen is exactly the thing a user
                // reports as "it didn't tell me" and there would otherwise be no record of the
                // decision. Tool name and reason only; never the command.
                Log("held back a notice about " + (speakThis.ToolName ?? "?") + ": " + refusal);
                return;
            }

            string line = BlockedDetector.Describe(speakThis);
            if (string.IsNullOrEmpty(line)) return;

            if (_host.SpeechEnabled)
            {
                // SayAll, not Say: this is a message to the USER, not a companion reacting to
                // something. The host routes it to exactly one companion, so several on screen do
                // not chant it in unison.
                _host.SayAll(line);
                if (Animate)
                {
                    _host.PlayAnimationAll(new List<string> { "boing", "jump", "run" });
                }
            }
            _budget.Record(speakThis, now);
            Log("notified: " + (speakThis.ToolName ?? "?") + " waiting "
                + ((int)Math.Round(speakThis.IdleSeconds)).ToString(CultureInfo.InvariantCulture)
                + "s in session " + Short(speakThis.Session));
        }

        internal static string DescribeStatus(int sessions, int blocked, int stoodDown)
        {
            if (sessions == 0) return "no agents running";
            if (blocked > 0)
            {
                return blocked == 1 ? "1 agent waiting for you"
                                    : blocked.ToString(CultureInfo.InvariantCulture)
                                      + " agents waiting for you";
            }
            if (stoodDown > 0 && stoodDown == sessions)
                return "standing down (auto mode)";
            return sessions == 1 ? "1 agent, working"
                                 : sessions.ToString(CultureInfo.InvariantCulture)
                                   + " agents, working";
        }

        private static string Short(AgentSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.SessionId)) return "?";
            return session.SessionId.Length <= 8
                ? session.SessionId
                : session.SessionId.Substring(0, 8);
        }

        private void PostToUi(Action action)
        {
            SynchronizationContext ui = _ui;
            if (ui != null) ui.Post(_ => action(), null);
            else action();
        }

        private void Log(string message)
        {
            // Best-effort by contract, and wrapped anyway: a diagnostic that can break the app is
            // worse than no diagnostic.
            try
            {
                IHost host = _host;
                if (host != null) host.Log(Info.Id, message);
            }
            catch (Exception) { }
        }

        // ---- settings -------------------------------------------------------

        private bool Enabled { get { return _settings == null || _settings.GetBool(SettingEnabled, true); } }
        private bool WatchClaude { get { return _settings == null || _settings.GetBool(SettingWatchClaude, true); } }
        private bool WatchCodex { get { return _settings == null || _settings.GetBool(SettingWatchCodex, true); } }
        private bool Animate { get { return _settings != null && _settings.GetBool(SettingAnimate, false); } }

        private double ThresholdSeconds
        {
            get
            {
                int value = _settings == null
                    ? (int)BlockedDetector.DefaultThresholdSeconds
                    : _settings.GetInt(SettingThreshold, (int)BlockedDetector.DefaultThresholdSeconds);
                return Clamp(value, 10, 600);
            }
        }

        private static double Clamp(int value, int low, int high)
        {
            if (value < low) return low;
            if (value > high) return high;
            return value;
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            return new Dictionary<string, string>
            {
                { SettingEnabled, Enabled ? "true" : "false" },
                { SettingThreshold, ((int)ThresholdSeconds).ToString(CultureInfo.InvariantCulture) },
                { SettingCooldown, CooldownSeconds.ToString(CultureInfo.InvariantCulture) },
                { SettingWatchClaude, WatchClaude ? "true" : "false" },
                { SettingWatchCodex, WatchCodex ? "true" : "false" },
                { SettingAnimate, Animate ? "true" : "false" },
            };
        }

        private int CooldownSeconds
        {
            get
            {
                int value = _settings == null
                    ? (int)NotifyBudget.DefaultCooldownSeconds
                    : _settings.GetInt(SettingCooldown, (int)NotifyBudget.DefaultCooldownSeconds);
                return (int)Clamp(value, 30, 3600);
            }
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            if (_settings == null || values == null) return false;
            foreach (KeyValuePair<string, string> entry in values)
            {
                // Info rows have no value to store, and writing them would put paragraphs of
                // prose into settings.json.
                if (entry.Key.StartsWith("about", StringComparison.Ordinal)) continue;
                _settings.Set(entry.Key, entry.Value);
            }
            bool ok = _settings.Save();
            // A changed cooldown has to reach the live budget, or the setting appears to do
            // nothing until the next launch.
            _budget = new NotifyBudget(CooldownSeconds, NotifyBudget.DefaultWindowSeconds,
                                       NotifyBudget.DefaultMaxPerWindow,
                                       NotifyBudget.DefaultPauseSeconds);
            return ok;
        }

        private Task<string> CheckNowAsync()
        {
            bool watchClaude = WatchClaude, watchCodex = WatchCodex;
            double threshold = ThresholdSeconds;
            return Task.Run(() =>
            {
                List<Detection> results;
                try { results = Scan(watchClaude, watchCodex, threshold); }
                catch (Exception exception) { return "Could not read the transcripts: " + exception.Message; }

                int blocked = 0, stoodDown = 0, working = 0, idle = 0;
                foreach (Detection detection in results)
                {
                    switch (detection.Outcome)
                    {
                        case DetectionOutcome.Blocked: blocked++; break;
                        case DetectionOutcome.StoodDownAutoMode: stoodDown++; break;
                        case DetectionOutcome.Working: working++; break;
                        default: idle++; break;
                    }
                }
                if (results.Count == 0) return "No coding agent has written a transcript recently.";
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} session(s): {1} waiting for you, {2} working, {3} idle, {4} in auto mode.",
                    results.Count, blocked, working, idle, stoodDown);
            });
        }

        // ---- tray -----------------------------------------------------------

        private string TrayText()
        {
            if (!Enabled) return "AgentFlow — off";
            return "AgentFlow — " + _status;
        }

        private IEnumerable<TrayItem> BuildMenu()
        {
            bool enabled = Enabled;
            return new List<TrayItem>
            {
                new TrayItem
                {
                    Label = enabled ? "✓ Watching" : "Watching",
                    Group = 0,
                    Order = 0,
                    Click = () => SetEnabledFromTray(true),
                },
                new TrayItem
                {
                    Label = enabled ? "Off" : "✓ Off",
                    Group = 0,
                    Order = 1,
                    Click = () => SetEnabledFromTray(false),
                },
            };
        }

        internal void SetEnabledFromTray(bool enabled)
        {
            if (_settings == null) return;
            _settings.Set(SettingEnabled, enabled ? "true" : "false");
            _settings.Save();
            if (enabled)
            {
                if (_timer != null) _timer.Start();
                OnTick(null, EventArgs.Empty);
            }
            else
            {
                _status = "off";
                _blockedCount = 0;
            }
        }

        private static byte[] LoadIconResource(string fileName)
        {
            return EmbeddedResources.LoadBytes(typeof(AgentFlowModule).Assembly, fileName);
        }

        // ---- self-test ------------------------------------------------------

        /// <summary>
        /// Project convention: every tray entry carries its own icon, and no two entries share one.
        /// </summary>
        internal static bool EveryTrayEntryHasAUniqueIcon(IEnumerable<TrayItem> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (TrayItem item in items)
            {
                if (item == null) return false;
                if (item.IconPng == null || item.IconPng.Length == 0) return false;
                if (!seen.Add(Convert.ToBase64String(item.IconPng))) return false;
            }
            return true;
        }

        /// <summary>
        /// <c>DesktopAICompanion.exe --module-selftest=agentflow</c>.
        ///
        /// Found by REFLECTION, which takes the FIRST method with this signature in the assembly,
        /// so this must be the only <c>SelfTest(out string)</c> here. Helpers are named SelfCheck.
        ///
        /// Deliberately asserts nothing about a real agent. Whether one is running, and whether it
        /// happens to be blocked, depends on the machine and the minute, so any check on that would
        /// be flaky and would fail outright on a CI runner. What IS asserted is everything that can
        /// be wrong without an agent present: the contributions, the settings round-trip, the
        /// detector's decisions against synthetic sessions, the auto-mode stand-down, the
        /// notification budget, and the two privacy properties -- that a spoken line carries no
        /// command text and no full path.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var probe = new SelfTestProbe();
            try
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow"))
                {
                    host.UseStorage("agentflow", storage);
                    var module = new AgentFlowModule();
                    module.Init(host);

                    probe.Check("contributes exactly one tray entry", host.TrayItems.Count == 1);
                    probe.Check("every tray entry has an icon",
                        EveryTrayEntryHasAUniqueIcon(host.TrayItems));
                    probe.Check("the tray entry is a pure submenu, not a button with an arrow",
                        host.TrayItems[0].Click == null && host.TrayItems[0].BuildChildren != null);
                    probe.Check("contributes exactly one settings pane", host.OptionsPanes.Count == 1);

                    probe.Check("declares the permissions it uses",
                        module.Info.Permissions.HasFlag(ModulePermissions.Speech)
                        && module.Info.Permissions.HasFlag(ModulePermissions.Storage)
                        && module.Info.Permissions.HasFlag(ModulePermissions.AgentTranscripts));
                    // WITNESS: the reason the flag was added at all. A module doing this read while
                    // declaring only Speech|Storage would work perfectly and disclose nothing.
                    probe.Check("WITNESS declares AgentTranscripts, which is the whole disclosure",
                        module.Info.Permissions.HasFlag(ModulePermissions.AgentTranscripts));
                    probe.Check("declares no permission it does not use",
                        !module.Info.Permissions.HasFlag(ModulePermissions.Network)
                        && !module.Info.Permissions.HasFlag(ModulePermissions.ScreenContext)
                        && !module.Info.Permissions.HasFlag(ModulePermissions.Hotkey)
                        && !module.Info.Permissions.HasFlag(ModulePermissions.Audio));

                    probe.Check("says nothing at startup", host.SaidLines.Count == 0);
                    probe.Check("broadcasts nothing at startup", host.BroadcastLines.Count == 0);

                    bool ok = SelfCheckSplitter(probe)
                              && SelfCheckRules(probe)
                              && SelfCheckDetector(probe)
                              && SelfCheckBudget(probe)
                              && SelfCheckPrivacy(probe);
                    probe.Check("every logic group ran", ok);

                    OptionsPane pane = host.OptionsPanes[0];
                    pane.Save(new Dictionary<string, string>
                    {
                        { SettingEnabled, "true" }, { SettingThreshold, "45" },
                        { SettingCooldown, "90" }, { SettingWatchClaude, "true" },
                        { SettingWatchCodex, "false" }, { SettingAnimate, "true" },
                    });
                    IReadOnlyDictionary<string, string> after = pane.Load();
                    probe.Check("settings round-trip through the pane",
                        after[SettingThreshold] == "45" && after[SettingCooldown] == "90"
                        && after[SettingWatchCodex] == "false");

                    // An out-of-range threshold must clamp rather than be honoured: a 1-second
                    // threshold would notify on every ordinary tool call.
                    pane.Save(new Dictionary<string, string>
                    {
                        { SettingEnabled, "true" }, { SettingThreshold, "1" },
                        { SettingCooldown, "1" }, { SettingWatchClaude, "true" },
                        { SettingWatchCodex, "true" }, { SettingAnimate, "false" },
                    });
                    IReadOnlyDictionary<string, string> clamped = pane.Load();
                    probe.Check("an absurd threshold clamps to the floor",
                        clamped[SettingThreshold] == "10" && clamped[SettingCooldown] == "30");

                    // An Info row is prose, not a value, and must never reach settings.json.
                    probe.Check("info rows are not persisted",
                        !clamped.ContainsKey("aboutAnswering"));

                    module.Shutdown();
                    probe.Check("shutdown clears the host reference", module._host == null);
                    probe.Check("shutdown disposes the timer", module._timer == null);
                }
            }
            catch (Exception exception)
            {
                probe.Exception(exception);
            }
            return probe.Finish(out detail);
        }

        private static bool SelfCheckSplitter(SelfTestProbe probe)
        {
            // The cases the old regex splitter got wrong. This is the THIRD implementation of one
            // algorithm (JS original, Python harness, this), so these are the anchor that keeps the
            // three from drifting apart silently.
            probe.Check("WITNESS a bare & is a separator in bash",
                Same(CommandSplitter.Split("sleep 1 & echo done", "bash"), "sleep 1", "echo done"));
            probe.Check("WITNESS 2>&1 is a redirection, not a separator",
                Same(CommandSplitter.Split("make 2>&1 | tee log", "bash"), "make 2>&1", "tee log"));
            probe.Check("WITNESS a ; inside single quotes does not split",
                Same(CommandSplitter.Split("echo 'a;b'", "bash"), "echo 'a;b'"));
            probe.Check("WITNESS a heredoc body is never segmented",
                Same(CommandSplitter.Split("cat <<'EOF'\na; b && c\nEOF", "bash"), "cat <<'EOF'"));
            probe.Check("WITNESS a $() substitution is not segmented",
                Same(CommandSplitter.Split("echo $(date; ls)", "bash"), "echo $(date; ls)"));
            probe.Check("&& splits", Same(CommandSplitter.Split("a && b", "bash"), "a", "b"));
            probe.Check("CRLF is one separator",
                Same(CommandSplitter.Split("a\r\nb", "bash"), "a", "b"));
            probe.Check("a PowerShell here-string is opaque",
                Same(CommandSplitter.Split("Set-Content f @'\na; b\n'@", "powershell"),
                     "Set-Content f"));
            probe.Check("a Windows path survives PowerShell splitting",
                Same(CommandSplitter.Split(@"Get-Item C:\temp\x; ls", "powershell"),
                     @"Get-Item C:\temp\x", "ls"));
            probe.Check("empty input yields no segments",
                CommandSplitter.Split("", "bash").Count == 0);
            probe.Check("null input is not a crash",
                CommandSplitter.Split(null, "bash").Count == 0);
            return true;
        }

        private static bool SelfCheckRules(SelfTestProbe probe)
        {
            // WITNESS: 86 rules on the maintainer's own machine use the colon spelling. Without
            // this normalization every one of them matches nothing at all.
            probe.Check("WITNESS Tool(cmd:*) is the same rule as Tool(cmd *)",
                PermissionRules.RuleMatches("Bash(git:*)", "Bash(git status)"));
            // WITNESS: a trailing ' *' also covers the BARE command.
            probe.Check("WITNESS Bash(ls *) matches the bare command ls",
                PermissionRules.RuleMatches("Bash(ls *)", "Bash(ls)"));
            probe.Check("Bash(git log *) matches git log with arguments",
                PermissionRules.RuleMatches("Bash(git log *)", "Bash(git log --oneline)"));
            // ...but only while that * is the ONLY wildcard, and the input here has to be chosen
            // with care. `Bash(git * --force *)` was tried first and is VACUOUS: the probe fails
            // to match it whether or not the wildcard count is checked, so the mutation harness
            // caught the assertion passing for the wrong reason. `Bash(git * *)` discriminates,
            // because the bare-command branch strips the trailing " *" WITHOUT rewriting the `*`
            // left behind -- so the leftover becomes a regex quantifier on the preceding space and
            // `Bash(git)` matches. That leftover is exactly what the count check exists to prevent.
            probe.Check("WITNESS a second wildcard removes the bare-command allowance",
                !PermissionRules.RuleMatches("Bash(git * *)", "Bash(git)"));
            probe.Check("a rule does not match an unrelated command",
                !PermissionRules.RuleMatches("Bash(git *)", "Bash(rm -rf /)"));
            probe.Check("a regex metacharacter in a rule is a literal",
                !PermissionRules.RuleMatches("Bash(a.c)", "Bash(abc)"));

            var rules = new RuleSet();
            rules.Allow.Add("Bash(echo *)");
            rules.Ask.Add("Bash(curl *)");
            rules.Deny.Add("Bash(rm *)");
            probe.Check("allow is allowed",
                PermissionRules.EvaluateCall("Bash", "echo hello", rules) == RuleVerdict.WouldAllow);
            probe.Check("ask prompts",
                PermissionRules.EvaluateCall("Bash", "curl https://x", rules) == RuleVerdict.WouldPrompt);
            probe.Check("deny denies",
                PermissionRules.EvaluateCall("Bash", "rm x", rules) == RuleVerdict.WouldDeny);
            probe.Check("nothing matched still prompts",
                PermissionRules.EvaluateCall("Bash", "jq .", rules) == RuleVerdict.WouldPrompt);
            // WITNESS: the reason the splitter matters to the verdict at all.
            probe.Check("WITNESS the most restrictive part of a chain wins",
                PermissionRules.EvaluateCall("Bash", "echo a && curl https://x", rules)
                    == RuleVerdict.WouldPrompt);
            probe.Check("a VAR=value prefix is stripped before matching",
                PermissionRules.EvaluateCall("Bash", "FOO=1 echo hello", rules)
                    == RuleVerdict.WouldAllow);

            // A managed ask must beat a user allow, which is what the deny/ask/allow ORDER buys.
            var ordered = new RuleSet();
            ordered.Allow.Add("Bash(curl *)");
            ordered.Ask.Add("Bash(curl *)");
            probe.Check("WITNESS an ask rule beats an allow rule for the same command",
                PermissionRules.EvaluateCall("Bash", "curl https://x", ordered)
                    == RuleVerdict.WouldPrompt);
            return true;
        }

        private static bool SelfCheckDetector(SelfTestProbe probe)
        {
            var rules = new RuleSet();
            rules.Allow.Add("Bash(echo *)");
            DateTime now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

            probe.Check("nothing outstanding reads as idle",
                BlockedDetector.Evaluate(Session(now, now, null), rules, 30, now).Outcome
                    == DetectionOutcome.Idle);

            AgentSession fresh = Session(now, now.AddSeconds(-5), "curl https://x", "default");
            probe.Check("an outstanding call under the threshold reads as working",
                BlockedDetector.Evaluate(fresh, rules, 30, now).Outcome
                    == DetectionOutcome.Working);

            AgentSession stalled = Session(now.AddSeconds(-120), now.AddSeconds(-120),
                                           "curl https://x", "default");
            probe.Check("WITNESS stalled plus would-prompt is BLOCKED",
                BlockedDetector.Evaluate(stalled, rules, 30, now).Outcome
                    == DetectionOutcome.Blocked);

            AgentSession slowButAllowed = Session(now.AddSeconds(-120), now.AddSeconds(-120),
                                                  "echo hello", "default");
            probe.Check("WITNESS stalled but allowed is SLOW, not blocked",
                BlockedDetector.Evaluate(slowButAllowed, rules, 30, now).Outcome
                    == DetectionOutcome.StalledButAllowed);

            AgentSession auto = Session(now.AddSeconds(-600), now.AddSeconds(-600),
                                        "curl https://x", "auto");
            probe.Check("WITNESS auto mode stands down however long it has stalled",
                BlockedDetector.Evaluate(auto, rules, 30, now).Outcome
                    == DetectionOutcome.StoodDownAutoMode);

            var empty = new AgentSession { SessionId = "x", SawAnyCall = false, LastWriteUtc = now };
            probe.Check("a transcript with no tool calls is suspect, not idle",
                BlockedDetector.Evaluate(empty, rules, 30, now).Outcome
                    == DetectionOutcome.AdapterSuspect);

            probe.Check("the OLDEST outstanding call is the blocker",
                OldestIsChosen(rules, now));

            probe.Check("status text counts what is waiting",
                DescribeStatus(3, 2, 0) == "2 agents waiting for you"
                && DescribeStatus(0, 0, 0) == "no agents running"
                && DescribeStatus(2, 0, 2) == "standing down (auto mode)");
            return true;
        }

        private static bool OldestIsChosen(RuleSet rules, DateTime now)
        {
            var session = new AgentSession
            {
                SessionId = "s", SawAnyCall = true, LastWriteUtc = now.AddSeconds(-120),
                Mode = "default",
            };
            session.Outstanding.Add(new OutstandingCall
            {
                Id = "new", Tool = "Read", Command = null,
                StartedUtc = now.AddSeconds(-10), Mode = "default",
            });
            session.Outstanding.Add(new OutstandingCall
            {
                Id = "old", Tool = "Bash", Command = "curl https://x",
                StartedUtc = now.AddSeconds(-300), Mode = "default",
            });
            Detection detection = BlockedDetector.Evaluate(session, rules, 30, now);
            return detection.Call != null && detection.Call.Id == "old";
        }

        private static bool SelfCheckBudget(SelfTestProbe probe)
        {
            DateTime now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var rules = new RuleSet();
            AgentSession stalled = Session(now.AddSeconds(-120), now.AddSeconds(-120),
                                           "curl https://x", "default");
            Detection blocked = BlockedDetector.Evaluate(stalled, rules, 30, now);

            var budget = new NotifyBudget(120, 600, 5, 1800);
            string refusal;
            probe.Check("a fresh budget allows the first notice",
                budget.ShouldAnnounce(blocked, now, out refusal));
            budget.Record(blocked, now);
            // WITNESS: the one-shot. A blocked call STAYS blocked, so without this the companion
            // repeats itself on every single poll.
            probe.Check("WITNESS the same prompt never speaks twice",
                !budget.ShouldAnnounce(blocked, now.AddSeconds(600), out refusal));

            var cooldown = new NotifyBudget(120, 600, 5, 1800);
            Detection first = BlockedDetector.Evaluate(
                Session(now.AddSeconds(-120), now.AddSeconds(-120), "curl https://a", "default",
                        "s1", "c1"), rules, 30, now);
            Detection second = BlockedDetector.Evaluate(
                Session(now.AddSeconds(-120), now.AddSeconds(-120), "curl https://b", "default",
                        "s2", "c2"), rules, 30, now);
            cooldown.Record(first, now);
            probe.Check("WITNESS a different session is still held by the cooldown",
                !cooldown.ShouldAnnounce(second, now.AddSeconds(5), out refusal));
            probe.Check("...and allowed once the cooldown has passed",
                cooldown.ShouldAnnounce(second, now.AddSeconds(200), out refusal));

            // WITNESS: the death-loop guard, and specifically that it does NOT auto-resume the way
            // the tool this idea came from does.
            var loop = new NotifyBudget(0, 600, 3, 1800);
            for (int index = 0; index < 3; index++)
            {
                Detection one = BlockedDetector.Evaluate(
                    Session(now.AddSeconds(-120), now.AddSeconds(-120), "curl https://x", "default",
                            "s" + index, "c" + index), rules, 30, now);
                loop.Record(one, now.AddSeconds(index));
            }
            probe.Check("WITNESS the death-loop guard closes at the per-window cap",
                loop.IsPaused(now.AddSeconds(10)));
            probe.Check("WITNESS the guard does NOT auto-resume inside the pause",
                loop.IsPaused(now.AddSeconds(1700)));
            probe.Check("the window count is observable, so the cap can be asserted",
                loop.WindowCount(now.AddSeconds(10)) == 3);
            return true;
        }

        private static bool SelfCheckPrivacy(SelfTestProbe probe)
        {
            DateTime now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var rules = new RuleSet();
            const string secret = "curl -H \"Authorization: Bearer sk-not-a-real-token\" https://x";
            AgentSession session = Session(now.AddSeconds(-300), now.AddSeconds(-300),
                                           secret, "default");
            session.Cwd = @"D:\clients\SomeRealCustomerName\repo";
            Detection detection = BlockedDetector.Evaluate(session, rules, 30, now);
            string line = BlockedDetector.Describe(detection);

            // WITNESS: these two are the reason the module can be trusted with the read at all.
            probe.Check("WITNESS a spoken line carries no command text",
                line != null && line.IndexOf("curl", StringComparison.Ordinal) < 0
                && line.IndexOf("Bearer", StringComparison.Ordinal) < 0
                && line.IndexOf("sk-not-a-real-token", StringComparison.Ordinal) < 0);
            probe.Check("WITNESS a spoken line carries no full path, only the project folder",
                line != null && line.IndexOf(@"D:\clients", StringComparison.Ordinal) < 0
                && line.IndexOf("SomeRealCustomerName", StringComparison.Ordinal) < 0
                && line.IndexOf("repo", StringComparison.Ordinal) >= 0);
            probe.Check("the reason string carries no command text either",
                detection.Reason != null
                && detection.Reason.IndexOf("curl", StringComparison.Ordinal) < 0);
            probe.Check("a blank cwd still produces a usable line",
                BlockedDetector.Describe(BlockedDetector.Evaluate(
                    Session(now.AddSeconds(-300), now.AddSeconds(-300), "curl https://x", "default"),
                    rules, 30, now)) != null);
            probe.Check("the project name is the last path segment only",
                BlockedDetector.ShortProject(@"D:\a\b\myproject") == "myproject"
                && BlockedDetector.ShortProject(@"D:\a\b\myproject\") == "myproject"
                && BlockedDetector.ShortProject(null) == null);
            return true;
        }

        // ---- self-test helpers ----------------------------------------------

        private static AgentSession Session(DateTime written, DateTime started, string command)
        {
            return Session(written, started, command, null, "session", "call");
        }

        private static AgentSession Session(DateTime written, DateTime started, string command,
                                            string mode)
        {
            return Session(written, started, command, mode, "session", "call");
        }

        private static AgentSession Session(DateTime written, DateTime started, string command,
                                            string mode, string sessionId, string callId)
        {
            var session = new AgentSession
            {
                Agent = TranscriptReader.AgentClaude,
                SessionId = sessionId,
                SawAnyCall = true,
                LastWriteUtc = written,
                Mode = mode,
            };
            if (command != null)
            {
                session.Outstanding.Add(new OutstandingCall
                {
                    Id = callId,
                    Tool = "Bash",
                    Command = command,
                    StartedUtc = started,
                    Mode = mode,
                });
            }
            return session;
        }

        private static bool Same(List<string> actual, params string[] expected)
        {
            if (actual == null || actual.Count != expected.Length) return false;
            for (int index = 0; index < expected.Length; index++)
                if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }
}
