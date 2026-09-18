using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.ModuleKit.Testing;
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
        private Action<ICompanion> _spawnHandler;

        /// <summary>
        /// Companions this module has seen spawn, so it can tell whether anything is on screen to
        /// speak for it.
        ///
        /// This exists because of a defect found by watching the real app rather than by any test.
        /// IHost.SayAll routes through StartUp.DefaultSpeaker(), which returns null when no
        /// companion is out, and the host then drops the line SILENTLY. The module's first poll
        /// runs inside Init, which is BEFORE startup has put a companion on screen -- so the very
        /// first notification after launch was spoken into nothing, while the budget recorded it as
        /// announced and the one-shot then suppressed it forever. The log said "notified" because
        /// the line was written after the SayAll call returned, which it always does.
        ///
        /// A user would have seen precisely nothing and had no way to tell why.
        /// </summary>
        private readonly List<ICompanion> _companions = new List<ICompanion>();

        /// <summary>
        /// Sessions this run has already explained itself about, so the stand-down is stated ONCE
        /// per session rather than on every poll.
        ///
        /// This exists because of what the first run against 764 real transcripts looked like: the
        /// module correctly said nothing for 45 seconds, and the diagnostic log therefore contained
        /// nothing either. A working run and a completely broken one were byte-identical. The
        /// standing rule is that a control which stands down must SAY so, and silence is the one
        /// outcome that cannot be told apart from failure.
        /// </summary>
        private readonly HashSet<string> _explained = new HashSet<string>(StringComparer.Ordinal);

        // Written on the UI thread only, read by the tray's DynamicText on the UI thread.
        private string _status = "no agents seen yet";

        // Guards against overlapping scans when a poll outlives its interval.
        private int _scanning;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "agentflow",
            Name = "AgentFlow",
            Version = "1.0.2",   // 1.0.2: the approve half. Option classifier + the argv.json setup.
                                 // 1.0.1: logs what the rules APPROVE, not only what would block.
                                 // 1.0.0: first version. Notify half only, observe-only by decision.
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
            // Speech for the bubble, Animation for the attention wiggle, Storage for its own
            // settings, AgentTranscripts for the read that is the whole feature. Nothing else: no
            // Network (it never makes a request), no ScreenContext (it does not look at the screen),
            // no Hotkey, no Audio.
            //
            // Animation was MISSING until 2026-09-17 while IHost.PlayAnimationAll was already being
            // called, and the comment above enumerated four flags it does not need without noticing
            // the one it does. Reminder's declaration is the worked example: it declares Animation
            // for exactly this call.
            Permissions = ModulePermissions.Speech
                          | ModulePermissions.Animation
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
                new TrayItem
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
                },
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
                        Label = "Watch Codex (reads it, but cannot act on it yet)",
                        Kind = SettingKind.Bool,
                        Group = "Which agents",
                    },
                    new SettingField
                    {
                        Id = "aboutCodex",
                        Label = "Codex's transcript does not record a permission mode, and AgentFlow "
                                + "only acts in default mode, so a watched Codex session can only "
                                + "ever stand down. The reading half works, so this becomes useful "
                                + "the day that format carries a mode.",
                        Kind = SettingKind.Info,
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
                        Id = SettingAutoApprove,
                        Label = "Approve the prompt for me (one call at a time)",
                        Kind = SettingKind.Bool,
                        Group = "What it will and will not press",
                    },
                    new SettingField
                    {
                        Id = "aboutSetup",
                        // STATIC on purpose, and this is the correction of a real defect rather
                        // than a preference. SettingField.Label is a string and the ABI has no
                        // Func<string> for it, so whatever goes here is evaluated ONCE, when Init
                        // builds the schema. The first version called SetupStatusLine() here and
                        // therefore showed the state at APP START for the life of the process: the
                        // port came up fifteen minutes later and the row still said "not set up",
                        // which is worse than saying nothing. The live state is only available
                        // through an action's return value, so that is where it now lives.
                        Label = "Approving needs VS Code started with a debugging port. Close VS "
                                + "Code, press \u201cEnable approving\u201d, reopen it, then press "
                                + "\u201cCheck now\u201d -- that is the only thing here that "
                                + "reports the LIVE state.",
                        Kind = SettingKind.Info,
                        Group = "What it will and will not press",
                    },
                    new SettingField
                    {
                        Id = "aboutAnswering",
                        Label = "AgentFlow only ever presses the option that approves THIS ONE "
                                + "CALL. It never presses “don’t ask again”, never "
                                + "“allow all edits this session”, and never anything "
                                + "that changes your permission mode. Every comparable tool was "
                                + "read at source level and all four press wider than they "
                                + "advertise. If it cannot recognise even one option on a prompt, "
                                + "it touches nothing.",
                        Kind = SettingKind.Info,
                        Group = "What it will and will not press",
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
                    // The setup group. Separate from "Check now" because these WRITE, and to
                    // another application's configuration at that.
                    new PaneAction
                    {
                        Label = "Enable approving (edits VS Code)",
                        InvokeAsync = EnableCdpAsync,
                        Group = "Approving",
                        // NO ReloadPaneAfter. OptionsWindow writes this action's result next to the
                        // button and then, if the flag is set, rebuilds the pane -- which destroys
                        // the TextBlock holding it. Every one of these three buttons appeared to do
                        // nothing for exactly that reason, while "Check now" worked because it is
                        // the one action that never asked for a reload.
                    },
                    new PaneAction
                    {
                        Label = "Disable approving",
                        InvokeAsync = DisableCdpAsync,
                        Group = "Approving",
                        // NO ReloadPaneAfter. OptionsWindow writes this action's result next to the
                        // button and then, if the flag is set, rebuilds the pane -- which destroys
                        // the TextBlock holding it. Every one of these three buttons appeared to do
                        // nothing for exactly that reason, while "Check now" worked because it is
                        // the one action that never asked for a reload.
                    },
                    new PaneAction
                    {
                        Label = "Find argv.json...",
                        InvokeAsync = BrowseForArgvAsync,
                        Group = "Approving",
                        // NO ReloadPaneAfter. OptionsWindow writes this action's result next to the
                        // button and then, if the flag is set, rebuilds the pane -- which destroys
                        // the TextBlock holding it. Every one of these three buttons appeared to do
                        // nothing for exactly that reason, while "Check now" worked because it is
                        // the one action that never asked for a reload.
                    },
                },
            });

            _spawnHandler = OnCompanionSpawned;
            host.CompanionSpawned += _spawnHandler;

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
            if (_host != null && _spawnHandler != null)
                _host.CompanionSpawned -= _spawnHandler;
            _spawnHandler = null;
            _companions.Clear();
            _explained.Clear();
            _tickHandler = null;
            _budget = null;
            _ui = null;
            _host = null;
        }

        private void OnCompanionSpawned(ICompanion companion)
        {
            if (companion != null) _companions.Add(companion);
        }

        /// <summary>
        /// Is there a companion on screen that could actually show a bubble?
        ///
        /// There is no CompanionRemoved event, so the list only ever grows; IsCompanionAlive is
        /// what makes it truthful, and dead entries are pruned here rather than accumulating for
        /// the life of the session.
        /// </summary>
        private bool AnyCompanionCanSpeak()
        {
            IHost host = _host;
            if (host == null) return false;
            for (int index = _companions.Count - 1; index >= 0; index--)
            {
                bool alive;
                try { alive = host.IsCompanionAlive(_companions[index]); }
                catch (Exception) { alive = false; }
                if (!alive) _companions.RemoveAt(index);
            }
            return _companions.Count > 0;
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
            bool autoApprove = AutoApprove;
            int cdpPort = CdpPort;

            // Reading and parsing transcripts is file IO plus JSON, so it never runs on the tick.
            // Nothing inside this task touches _host.
            Task.Run(() =>
            {
                List<Detection> results = null;
                Dictionary<string, int> approved = null;
                try
                {
                    results = Scan(watchClaude, watchCodex, threshold, _approvalsCounted,
                                   out approved);
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
                // Refresh the cached port state on the same beat, so the tray can show whether
                // approving could actually happen without probing a socket on menu open.
                bool answering = false;
                try
                {
                    if (autoApprove)
                        answering = VsCodeSetup.Probe(cdpPort, 200);
                }
                catch { answering = false; }

                if (results != null)
                {
                    Dictionary<string, int> forUi = approved;
                    bool portUp = answering;
                    PostToUi(() =>
                    {
                        _portAnswering = portUp;
                        Apply(results);
                        LogApprovals(forUi);
                    });
                }
            });
        }

        /// <summary>
        /// Read every live transcript and evaluate it. Pure enough to call from a worker: no host,
        /// no UI, no module state beyond the settings snapshot handed in.
        /// </summary>
        internal static List<Detection> Scan(bool watchClaude, bool watchCodex, double threshold)
        {
            Dictionary<string, int> ignored;
            return Scan(watchClaude, watchCodex, threshold, null, out ignored);
        }

        /// <summary>
        /// As <see cref="Scan(bool,bool,double)"/>, and also tally what the rules APPROVED.
        ///
        /// `approvalsCounted` is the set of call ids already accounted for, consumed and updated in
        /// place so a call is counted once however often the transcript is re-read. Pass null to
        /// skip the audit entirely, which the self-tests do when they only care about detection.
        /// </summary>
        internal static List<Detection> Scan(bool watchClaude, bool watchCodex, double threshold,
                                             HashSet<string> approvalsCounted,
                                             out Dictionary<string, int> approved)
        {
            int sources;
            RuleSet rules = RuleLoader.Load(RuleLoader.DefaultPaths(), out sources);
            DateTime now = DateTime.UtcNow;
            var results = new List<Detection>();
            approved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var live = new HashSet<string>(StringComparer.Ordinal);

            if (watchClaude)
            {
                foreach (string path in TranscriptReader.ActiveTranscripts(
                             TranscriptReader.ClaudeRoot, ActiveWindowSeconds, "subagents"))
                {
                    AgentSession session = TranscriptReader.ReadClaude(path);
                    Detection detection = BlockedDetector.Evaluate(session, rules, threshold, now);
                    if (detection != null) results.Add(detection);
                    Tally(session, rules, approvalsCounted, approved, live);
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
                    Tally(session, rules, approvalsCounted, approved, live);
                }
            }

            // Drop ids no live transcript mentions any more, so the counted set is bounded by what
            // is on disk in the active window rather than by how long the app has been running.
            // NotifyBudget.Retain solves the same problem for the one-shot and this mirrors it.
            if (approvalsCounted != null)
            {
                var stale = new List<string>();
                foreach (string key in approvalsCounted) if (!live.Contains(key)) stale.Add(key);
                foreach (string key in stale) approvalsCounted.Remove(key);
            }
            return results;
        }

        /// <summary>Fold one session's newly-approved calls into the running tally.</summary>
        private static void Tally(AgentSession session, RuleSet rules,
                                  HashSet<string> approvalsCounted,
                                  Dictionary<string, int> approved, HashSet<string> live)
        {
            if (session == null) return;
            if (session.Completed != null)
                foreach (OutstandingCall call in session.Completed)
                    if (call != null && !string.IsNullOrEmpty(call.Id))
                        live.Add(session.SessionId + "/" + call.Id);
            if (approvalsCounted == null) return;
            Dictionary<string, int> tally =
                BlockedDetector.ApprovedSince(session, rules, approvalsCounted);
            foreach (KeyValuePair<string, int> entry in tally)
            {
                int current;
                approved.TryGetValue(entry.Key, out current);
                approved[entry.Key] = current + entry.Value;
            }
        }

        /// <summary>UI thread: speak at most one line, and refresh the tray label.</summary>
        internal void Apply(List<Detection> results)
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
                    Explain(detection, "standing down for session " + Short(detection.Session)
                                       + ": " + detection.Reason);
                }
                else if (detection.Outcome == DetectionOutcome.NotDecidable)
                {
                    Explain(detection, "not acting on session " + Short(detection.Session)
                                       + ": " + detection.Reason);
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

            // Both of these are checked BEFORE the budget is consumed, because a notification the
            // user cannot possibly have seen must remain pending rather than being spent. The
            // companion check is the one that was missing and swallowed the first notice after
            // every launch; SpeechEnabled has the same shape, so it is treated the same way.
            if (!_host.SpeechEnabled)
            {
                Log("deferred a notice about " + (speakThis.ToolName ?? "?")
                    + ": speech is switched off");
                return;
            }
            if (!AnyCompanionCanSpeak())
            {
                Log("deferred a notice about " + (speakThis.ToolName ?? "?")
                    + ": no companion on screen to say it");
                return;
            }

            // SayAll, not Say: this is a message to the USER, not a companion reacting to
            // something. The host routes it to exactly one companion, so several on screen do
            // not chant it in unison.
            _host.SayAll(line);
            if (Animate)
            {
                _host.PlayAnimationAll(new List<string> { "boing", "jump", "run" });
            }
            _budget.Record(speakThis, now);
            // "spoke" rather than "notified", and only on the path where a companion was on screen
            // and speech was on. The previous wording was a log line that could not fail: it was
            // written after SayAll returned, which it does whether or not anything was shown.
            Log("spoke about " + (speakThis.ToolName ?? "?") + " waiting "
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

        /// <summary>Log a per-session explanation once per run. Carries no command text.</summary>
        private void Explain(Detection detection, string message)
        {
            string key = (detection.Session != null ? detection.Session.SessionId : "?")
                         + "/" + detection.Outcome;
            if (!_explained.Add(key)) return;
            Log(message);
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
        /// <summary>
        /// OFF by default, and the reason is not caution. Codex's rollout transcript records no
        /// permission mode at all, so every Codex session resolves as `unknown`, and the
        /// stand-down is an allow-list of exactly `default` -- so a watched Codex session can only
        /// ever stand down. Found against real data 2026-09-17: a live `rollout-` session logged
        /// "unknown mode: standing down" on every poll.
        ///
        /// Leaving it ON by default would ship a switch that cannot do anything, which reads to a
        /// user as the module being broken rather than as Codex being unsupported. It stays as a
        /// setting because the reader half genuinely works, so the day Codex's format carries a
        /// mode this becomes useful without an ABI change.
        /// </summary>
        private bool WatchCodex { get { return _settings != null && _settings.GetBool(SettingWatchCodex, false); } }
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
                // Without this line the checkbox rendered from the schema but read nothing: it
                // showed UNCHECKED however the setting actually stood, and because SavePaneValues
                // writes back every key the pane hands it, closing the settings window would then
                // store "false" and silently switch auto-approve off again. Two controls for one
                // setting means Load has to carry it, not just Save.
                { SettingAutoApprove, AutoApprove ? "true" : "false" },
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
            // A changed cooldown has to reach the live budget, or the setting appears to do nothing
            // until the next launch. Mutate it IN PLACE: replacing the object here also replaced
            // _announced, so saving the pane re-armed every prompt the module had already spoken
            // about and the companion said it all again. Turning the cooldown up -- what a user
            // does when it is too chatty -- made it briefly chattier.
            if (_budget != null) _budget.SetCooldownSeconds(CooldownSeconds);
            return ok;
        }

        /// <summary>
        /// Call ids whose approval has already been written to the log, so a poll reports only what
        /// is NEW. Pruned inside Scan against what the live transcripts still mention.
        ///
        /// Not in NotifyBudget: this is not a budget. The notify one-shot exists to stop the
        /// companion repeating itself out loud; this exists so the audit line is a delta rather
        /// than a running total, and conflating them would tie the audit to the speech cooldown.
        /// </summary>
        private readonly HashSet<string> _approvalsCounted = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Write what the rules approved since the last poll. UI thread, because IHost.Log is.
        ///
        /// This is the half an approval module owes the user and did not have. The detector reports
        /// what it would have STOPPED; nothing reported what went through, and on this machine that
        /// is thousands of commands a day nobody sees. It also makes the module verifiable in auto
        /// mode, where the notify half stands down by design: the log fills up immediately, so
        /// "is it working?" has an answer that does not require waiting for a blocked prompt.
        ///
        /// Silent when nothing new was approved. A line per poll saying "approved 0" would bury the
        /// log it exists to make readable, which LogCategory's own doc warns about.
        /// </summary>
        private void LogApprovals(Dictionary<string, int> approved)
        {
            if (approved == null || approved.Count == 0) return;
            string line = BlockedDetector.DescribeApprovals(approved, ApprovalNamesPerLine);
            if (!string.IsNullOrEmpty(line)) Log(line);
        }

        /// <summary>How many distinct executables one audit line names before it says "+N more".</summary>
        private const int ApprovalNamesPerLine = 8;

        // ---- setting up the approve half -------------------------------------------------

        /// <summary>Where the user pointed us, when the usual places did not have argv.json.</summary>
        /// <summary>
        /// Whether the user has asked for prompts to be approved. OFF by default and it stays off
        /// until asked: this is the one setting in the module that presses a button on the user's
        /// behalf, and a default-on switch for that would be indefensible.
        /// </summary>
        private const string SettingAutoApprove = "autoApprove";

        private const string SettingArgvPath = "argvPath";
        /// <summary>The port to ask VS Code for. Stored so a collision can be moved off.</summary>
        private const string SettingCdpPort = "cdpPort";

        /// <summary>The user's INTENT. Separate from whether the module CAN act, deliberately.</summary>
        private bool AutoApprove
        {
            get { return _settings != null && _settings.GetBool(SettingAutoApprove, false); }
        }

        /// <summary>
        /// Whether the debugging port answered at the last poll.
        ///
        /// CACHED, and the reason is the tray menu. Tray labels are rebuilt on every menu open, so
        /// probing the port there would put a blocking TCP connect with a timeout in the path of
        /// the user's right-click -- on a dead port that is a visible stall every time they open
        /// the menu. The poll already runs on its own timer, so it refreshes this and the menu
        /// reads a field.
        /// </summary>
        private volatile bool _portAnswering;

        private string ArgvPath
        {
            get { return _settings == null ? "" : _settings.Get(SettingArgvPath, ""); }
        }

        private int CdpPort
        {
            get
            {
                int stored = _settings == null
                    ? VsCodeSetup.DefaultPort
                    : _settings.GetInt(SettingCdpPort, VsCodeSetup.DefaultPort);
                return stored > 0 && stored <= 65535 ? stored : VsCodeSetup.DefaultPort;
            }
        }

        /// <summary>
        /// One line for the pane saying whether approving can work, and it is allowed to say NO.
        ///
        /// The states are deliberately four and not two. "argv.json asks for the port" and "the
        /// port answers" are different facts: VS Code applies the key at LAUNCH, so between the
        /// edit and the restart the first is true and the second is false, and a status that
        /// collapsed them would report success for a setup that cannot work yet.
        /// </summary>
        private string SetupStatusLine()
        {
            SetupReport report = VsCodeSetup.Inspect(ArgvPath, 250);
            switch (report.State)
            {
                case SetupState.Listening:
                    return "Approving can work: port " + report.Port + " is answering.";
                case SetupState.On:
                    return "Waiting for a VS Code restart. " + report.Detail;
                case SetupState.Off:
                    return "Not set up. Press \u201cEnable approving\u201d, then restart VS Code.";
                case SetupState.Unreadable:
                    return "Cannot use " + (report.Path ?? "argv.json") + ": " + report.Detail;
                default:
                    return report.Detail ?? "No argv.json found.";
            }
        }

        /// <summary>
        /// Write the port into VS Code's argv.json, or explain why not.
        ///
        /// It refuses while VS Code is RUNNING, and the reason is not file contention: VS Code
        /// rewrites argv.json itself, and the restart is what applies the key. Writing under a live
        /// editor risks the edit being overwritten and guarantees the user thinks it took effect
        /// when it did not.
        ///
        /// It never claims success. The last thing it says is what has to happen next, because the
        /// write is not the outcome -- the port is, and the port does not exist until a restart.
        /// </summary>
        private Task<string> EnableCdpAsync()
        {
            string overridePath = ArgvPath;
            int port = CdpPort;
            return Task.Run(() =>
            {
                if (VsCodeSetup.IsVsCodeRunning())
                {
                    return "\u2717 VS Code is running. Close it first: it rewrites this file itself, and "
                           + "the change only takes effect at launch, so editing it now would look "
                           + "like it worked and would not.";
                }
                SetupReport report = VsCodeSetup.Inspect(overridePath, 250);
                if (report.State == SetupState.NotFound)
                    return "\u2717 " + report.Detail + " Use \u201cFind argv.json\u201d to point at it.";
                if (report.State == SetupState.Unreadable)
                    return "\u2717 Not touching it: " + report.Detail;

                string original;
                try { original = System.IO.File.ReadAllText(report.Path); }
                catch (Exception exception)
                {
                    return "\u2717 Could not read " + report.Path + ": " + exception.Message;
                }
                string updated = VsCodeSetup.WithPort(original, port);
                if (updated == null)
                    return "\u2717 Refusing to edit " + report.Path + ": it is not a file this can change "
                           + "safely.";
                if (string.Equals(updated, original, StringComparison.Ordinal))
                    return "\u2713 Already asking for port " + port + ". Restart VS Code if it is not "
                           + "answering yet.";
                // Atomic, and with a BACKUP, because a half-written argv.json stops VS Code
                // reading ANY of it -- the user would lose their crash-reporter id and every
                // comment along with the port. TryWriteAllText returns false rather than throwing,
                // which is the shape a module is supposed to degrade with.
                if (!DesktopAICompanion.ModuleKit.AtomicFile.TryWriteAllText(
                        report.Path, updated, report.Path + ".agentflow-backup"))
                {
                    return "\u2717 Could not write " + report.Path + ". Nothing was changed.";
                }
                // Re-inspect and report the state AFTER the write, because the write is not the
                // outcome: the port appears at the next VS Code launch, so the honest answer here
                // is "written, not yet live" and the button is the only place that can say it.
                SetupReport after = VsCodeSetup.Inspect(overridePath, 250);
                return "\u2713 Wrote port " + port + " into " + report.Path
                       + ". Now START VS CODE, then press \u201cCheck now\u201d. Current state: "
                       + after.Detail
                       + " The port opens on every launch until you press \u201cDisable "
                       + "approving\u201d.";
            });
        }

        /// <summary>Take the key out again, and say so. As reachable as Enable, on purpose.</summary>
        private Task<string> DisableCdpAsync()
        {
            string overridePath = ArgvPath;
            return Task.Run(() =>
            {
                if (VsCodeSetup.IsVsCodeRunning())
                    return "\u2717 VS Code is running. Close it first, for the same reason as enabling.";
                SetupReport report = VsCodeSetup.Inspect(overridePath, 250);
                if (report.State == SetupState.NotFound || report.State == SetupState.Unreadable)
                    return "\u2717 " + report.Detail;
                string original;
                try { original = System.IO.File.ReadAllText(report.Path); }
                catch (Exception exception) { return "\u2717 Could not read it: " + exception.Message; }
                string updated = VsCodeSetup.WithoutPort(original);
                if (string.Equals(updated, original, StringComparison.Ordinal))
                    return "\u2713 It was not asking for a port, so nothing to remove.";
                if (!DesktopAICompanion.ModuleKit.AtomicFile.TryWriteAllText(
                        report.Path, updated, report.Path + ".agentflow-backup"))
                {
                    return "\u2717 Could not write it. Nothing was changed.";
                }
                return "\u2713 Removed the debugging port from " + report.Path
                       + ". It stops opening after the next VS Code restart.";
            });
        }

        /// <summary>
        /// Let the user point at argv.json when it is somewhere this does not look.
        ///
        /// A portable install puts it under VSCODE_PORTABLE and a dev build under .vscode-dev, and
        /// both are checked -- but guessing is not the same as knowing, and an unfound file is a
        /// worse answer than a file picker.
        /// </summary>
        private Task<string> BrowseForArgvAsync()
        {
            IHost host = _host;
            if (host == null || _settings == null)
                return Task.FromResult("\u2717 Not ready.");
            IReadOnlyList<string> picked = host.PickFilesToOpen(
                "Find VS Code's argv.json", "VS Code runtime arguments", new[] { ".json" });
            if (picked == null || picked.Count == 0)
                return Task.FromResult("\u2717 No file chosen; nothing changed.");
            string path = picked[0];
            SetupReport report = VsCodeSetup.Inspect(path, 250);
            if (report.State == SetupState.Unreadable)
                return Task.FromResult("\u2717 That file cannot be used: " + report.Detail);
            _settings.Set(SettingArgvPath, path);
            _settings.Save();
            return Task.FromResult("\u2713 Using " + path + ". " + report.Detail);
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

                // How many of the stood-down sessions WOULD have been flagged. A user pressing
                // "Check now" has asked a direct question, and "2 in auto mode" is not an answer to
                // it -- it is unfalsifiable from the outside, and on a machine that never leaves
                // auto it made the module impossible to judge. The stand-down still governs whether
                // the companion SPEAKS; this only governs what the pane says when asked.
                int wouldHave = 0;
                foreach (Detection detection in results)
                    if (detection.Outcome == DetectionOutcome.StoodDownAutoMode
                        && detection.WouldHaveBeen == DetectionOutcome.Blocked)
                        wouldHave++;

                // The SETUP state goes here too, because this is the only live channel a pane
                // has: an Info row's Label is a string evaluated once at Init, so it cannot report
                // whether the port is answering. This action can, and it is the one the user
                // already presses.
                string setup = SetupStatusLine();
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} session(s): {1} waiting for you, {2} working, {3} idle, {4} in auto mode{5}. {6}",
                    results.Count, blocked, working, idle, stoodDown,
                    wouldHave > 0
                        ? " (" + wouldHave.ToString(CultureInfo.InvariantCulture)
                          + " of them would have been flagged in default mode)"
                        : "",
                    setup);
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
                new TrayItem
                {
                    // Its own GROUP, so the host draws a separator between watching and
                    // PRESSING. Those are different kinds of decision and the menu should not
                    // read as a list of peers.
                    Label = AutoApproveTrayLabel(),
                    Group = 1,
                    Order = 0,
                    Click = ToggleAutoApproveFromTray,
                },
            };
        }

        /// <summary>
        /// THREE states, not two, and the middle one is the whole point.
        ///
        /// A red/green pair can only report what the user ASKED FOR, and this module has a
        /// second fact that matters just as much: whether it CAN act. Auto-approve switched on
        /// with no debugging port answering does nothing whatsoever, and a green dot there
        /// would be the same lie the pane status row told on 2026-09-18 -- reporting intent as
        /// capability, which is the defect that made the user think nothing had happened.
        ///
        ///   red     off
        ///   amber   on, but the port is not answering, so nothing will be pressed
        ///   green   on, and able to press
        ///
        /// A coloured GLYPH rather than a coloured menu item because a WinForms menu label is
        /// plain text: tinting one needs owner-draw, and the dot reads the same at a glance.
        /// </summary>
        private string AutoApproveTrayLabel()
        {
            if (!AutoApprove) return "🔴 Auto-approve: off";
            return _portAnswering
                ? "🟢 Auto-approve: on"
                : "🟡 Auto-approve: on, waiting for the debugging port";
        }

        /// <summary>Flip the user's intent from the tray, and say what happened out loud.</summary>
        internal void ToggleAutoApproveFromTray()
        {
            if (_settings == null) return;
            bool next = !AutoApprove;
            _settings.Set(SettingAutoApprove, next ? "true" : "false");
            _settings.Save();
            Log("auto-approve turned " + (next ? "ON" : "OFF") + " from the tray");

            // Forget what the last probe said. Whatever was true before the switch moved is
            // not evidence about now: the port is only probed while auto-approve is ON, so a
            // true left over from an earlier spell would paint the tray GREEN the instant the
            // switch came back on, claiming a reachable editor nobody has checked for. Amber
            // until a probe earns it, which takes at most one tick.
            _portAnswering = false;

            // Said out loud, because pressing buttons on someone's behalf is not a silent
            // setting and the tray label is only visible while the menu is open. INTENT only:
            // capability is the tray dot's job and the pane's, and one line of speech cannot
            // report a fact that was just invalidated on the line above.
            if (_host != null && _host.SpeechEnabled && AnyCompanionCanSpeak())
            {
                _host.SayAll(next
                    ? "Auto-approve is on. I will only press single-call approvals."
                    : "I will stop approving prompts.");
            }
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
                              && SelfCheckPrivacy(probe)
                              && SelfCheckApprovals(probe)
                              && SelfCheckPromptOptions(probe)
                              && SelfCheckVsCodeSetup(probe)
                              && SelfCheckAutoApprove(probe)
                              && SelfCheckCacheBound(probe);
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

                    // WITNESS, and it is the defect the two Save calls above were already
                    // triggering with nothing watching. SavePaneValues used to REPLACE _budget so a
                    // changed cooldown took effect at once, which discarded the announced set with
                    // it -- so pressing Save let an already-announced prompt speak again. Turning
                    // the cooldown up, which is what a user does when the companion is too chatty,
                    // made it briefly chattier. The assertion has to span a save: every one-shot
                    // test in SelfCheckBudget passes either way, because none of them press Save.
                    var announcedRules = new RuleSet();
                    announcedRules.Ask.Add("Bash(curl *)");
                    DateTime paneNow = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
                    Detection announced = BlockedDetector.Evaluate(
                        Session(paneNow.AddSeconds(-600), paneNow.AddSeconds(-600),
                                "curl https://x", "default"),
                        announcedRules, 30, paneNow);
                    module._budget.Record(announced, paneNow);
                    probe.Check("a recorded prompt is announced", module._budget.WasAnnounced(announced));
                    pane.Save(new Dictionary<string, string>
                    {
                        { SettingEnabled, "true" }, { SettingThreshold, "45" },
                        { SettingCooldown, "300" }, { SettingWatchClaude, "true" },
                        { SettingWatchCodex, "true" }, { SettingAnimate, "false" },
                    });
                    probe.Check("WITNESS saving the pane does not re-arm an announced prompt",
                        module._budget.WasAnnounced(announced));
                    // ...and the reason the replacement existed in the first place still holds: the
                    // new cooldown has to reach the live budget, not wait for the next launch.
                    probe.Check("...while the new cooldown still reaches the live budget",
                        module._budget.CooldownSeconds == 300.0);

                    // ---- the defect found by watching the real app, not by a test --------------
                    //
                    // The module's first poll runs inside Init, BEFORE startup has put a companion
                    // on screen. IHost.SayAll then finds no speaker and the host drops the line
                    // silently, so the first notification after every launch was spoken into
                    // nothing -- and because the budget had already recorded it, the one-shot
                    // suppressed that prompt forever. The user saw nothing and had no way to know.
                    //
                    // Found by capturing frames of the real app and looking: the log said
                    // "notified" while no bubble ever appeared, because the log line was written
                    // after SayAll returned, which it always does.
                    var freshHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    using (var freshStorage =
                               new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow2"))
                    {
                        freshHost.UseStorage("agentflow", freshStorage);
                        var second = new AgentFlowModule();
                        second.Init(freshHost);

                        List<Detection> blockedNow = OneBlockedDetection();
                        second.Apply(blockedNow);
                        probe.Check("WITNESS says nothing when no companion is on screen",
                            freshHost.SaidLines.Count == 0 && freshHost.BroadcastLines.Count == 0);

                        // ...and the notification must still be PENDING, not spent. This is the
                        // half that made the bug permanent rather than merely late.
                        freshHost.RaiseCompanionSpawned(new FakeCompanion());
                        second.Apply(OneBlockedDetection());
                        probe.Check("WITNESS the held notice is still delivered once a companion appears",
                            freshHost.BroadcastLines.Count == 1);

                        // A second identical poll must NOT repeat it, or the fix trades one bug
                        // for the repetition the one-shot exists to prevent.
                        second.Apply(OneBlockedDetection());
                        probe.Check("and is not repeated afterwards",
                            freshHost.BroadcastLines.Count == 1);

                        // Same shape for speech being switched off: held, not spent.
                        var mutedHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                        mutedHost.UseStorage("agentflow", freshStorage);
                        mutedHost.SpeechEnabled = false;
                        var third = new AgentFlowModule();
                        third.Init(mutedHost);
                        mutedHost.RaiseCompanionSpawned(new FakeCompanion());
                        third.Apply(OneBlockedDetection());
                        probe.Check("WITNESS says nothing while speech is switched off",
                            mutedHost.BroadcastLines.Count == 0);
                        mutedHost.SpeechEnabled = true;
                        third.Apply(OneBlockedDetection());
                        probe.Check("...and delivers it once speech is switched back on",
                            mutedHost.BroadcastLines.Count == 1);
                        third.Shutdown();
                        second.Shutdown();
                    }

                    module.Shutdown();
                    probe.Check("shutdown clears the host reference", module._host == null);
                    probe.Check("shutdown disposes the timer", module._timer == null);
                    probe.Check("shutdown forgets the companions it was tracking",
                        module._companions.Count == 0);
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
                PermissionRules.EvaluateCall("Bash", "echo hello", null, rules) == RuleVerdict.WouldAllow);
            probe.Check("ask prompts",
                PermissionRules.EvaluateCall("Bash", "curl https://x", null, rules) == RuleVerdict.WouldPrompt);
            probe.Check("deny denies",
                PermissionRules.EvaluateCall("Bash", "rm x", null, rules) == RuleVerdict.WouldDeny);
            probe.Check("nothing matched still prompts",
                PermissionRules.EvaluateCall("Bash", "jq .", null, rules) == RuleVerdict.WouldPrompt);
            // WITNESS: the reason the splitter matters to the verdict at all.
            probe.Check("WITNESS the most restrictive part of a chain wins",
                PermissionRules.EvaluateCall("Bash", "echo a && curl https://x", null, rules)
                    == RuleVerdict.WouldPrompt);
            probe.Check("a VAR=value prefix is stripped before matching",
                PermissionRules.EvaluateCall("Bash", "FOO=1 echo hello", null, rules)
                    == RuleVerdict.WouldAllow);

            // A managed ask must beat a user allow, which is what the deny/ask/allow ORDER buys.
            var ordered = new RuleSet();
            ordered.Allow.Add("Bash(curl *)");
            ordered.Ask.Add("Bash(curl *)");
            probe.Check("WITNESS an ask rule beats an allow rule for the same command",
                PermissionRules.EvaluateCall("Bash", "curl https://x", null, ordered)
                    == RuleVerdict.WouldPrompt);
            return true;
        }

        /// <summary>
        /// The rule-match caches are bounded, and `CacheStats` says of itself that the bound "can
        /// be asserted rather than assumed". Nothing asserted it until 2026-09-17: the accessor
        /// existed, the eviction path had never executed once, and a wholesale `.Clear()` of both
        /// caches is exactly the kind of thing that is either fine or corrupts every later verdict.
        ///
        /// Runs last, because filling the cache to its cap empties it for every other caller.
        /// </summary>
        /// <summary>
        /// The approval audit: what the rules let through, which is the half an approval module
        /// owes the user and did not have until 2026-09-18.
        ///
        /// The privacy assertion here is the load-bearing one. This writes to the diagnostic log,
        /// and SUPPORT.md invites users to attach that log to a GitHub issue, so a full command
        /// would put arguments, paths and occasionally a token into a file destined for a public
        /// tracker. The rule is the same one the spoken half holds and the same one AiBrain holds
        /// by logging endpoint HOSTS rather than URLs: the executable NAME, nothing else.
        /// </summary>
        private static bool SelfCheckApprovals(SelfTestProbe probe)
        {
            var rules = new RuleSet();
            rules.Allow.Add("Bash(git *)");
            rules.Allow.Add("Bash(rg *)");
            rules.Ask.Add("Bash(curl *)");

            var session = new AgentSession { SessionId = "s1", Agent = TranscriptReader.AgentClaude };
            session.NoteCompleted(Completed("c1", "git status"));
            session.NoteCompleted(Completed("c2", "git push --force-with-lease"));
            session.NoteCompleted(Completed("c3", "rg needle src"));
            session.NoteCompleted(Completed("c4", "curl https://example.invalid"));
            session.NoteCompleted(Completed("c5", "jq ."));

            var counted = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, int> tally = BlockedDetector.ApprovedSince(session, rules, counted);

            probe.Check("WITNESS approvals are counted per executable, not per call",
                tally.Count == 2 && tally.ContainsKey("git") && tally["git"] == 2
                && tally.ContainsKey("rg") && tally["rg"] == 1);
            // The two the rules do NOT allow must be absent. A call that would have prompted and
            // completed anyway was answered by a human or auto-accepted by the agent's mode, and
            // neither is the RULES approving it -- counting those would turn this into "everything
            // that ran", which is not an approval record.
            probe.Check("WITNESS a call the rules would prompt for is NOT counted as approved",
                !tally.ContainsKey("curl"));
            probe.Check("...nor is one no rule matches at all",
                !tally.ContainsKey("jq"));

            // Counted once, however often the transcript is re-read. The module re-reads on every
            // poll, so without this the log would repeat the same approvals every few seconds.
            Dictionary<string, int> again = BlockedDetector.ApprovedSince(session, rules, counted);
            probe.Check("WITNESS a second read of the same transcript counts nothing again",
                again.Count == 0);

            // PRIVACY. The command text is what the rules match on and must never reach the log.
            string line = BlockedDetector.DescribeApprovals(tally, 8);
            probe.Check("WITNESS the approval line carries no command text",
                line != null && line.IndexOf("--force-with-lease", StringComparison.Ordinal) < 0
                && line.IndexOf("needle", StringComparison.Ordinal) < 0
                && line.IndexOf("status", StringComparison.Ordinal) < 0);
            probe.Check("...while still naming what was approved, or it is not an audit",
                line.IndexOf("git", StringComparison.Ordinal) >= 0
                && line.IndexOf("rg", StringComparison.Ordinal) >= 0
                && line.IndexOf("3", StringComparison.Ordinal) >= 0);
            // A path in argv[0] would disclose a directory layout.
            var pathy = new AgentSession { SessionId = "s2", Agent = TranscriptReader.AgentClaude };
            pathy.NoteCompleted(Completed("p1", "/usr/local/secret-dir/git status"));
            var pathRules = new RuleSet();
            pathRules.Allow.Add("Bash(/usr/local/secret-dir/git *)");
            Dictionary<string, int> pathTally =
                BlockedDetector.ApprovedSince(pathy, pathRules, new HashSet<string>(StringComparer.Ordinal));
            probe.Check("WITNESS an executable given by PATH is logged as its leaf only",
                pathTally.ContainsKey("git") && !pathTally.ContainsKey("/usr/local/secret-dir/git"));

            probe.Check("nothing approved produces no line at all, rather than 'approved 0'",
                BlockedDetector.DescribeApprovals(
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 8) == null);

            // The completed list is bounded, because a long session pairs thousands of calls and
            // this is re-read from disk every poll.
            var big = new AgentSession { SessionId = "s3", Agent = TranscriptReader.AgentClaude };
            for (int i = 0; i < AgentSession.CompletedCap + 50; i++)
                big.NoteCompleted(Completed("b" + i.ToString(CultureInfo.InvariantCulture), "git status"));
            probe.Check("WITNESS the completed-call list is bounded and keeps the most recent",
                big.Completed.Count == AgentSession.CompletedCap
                && big.Completed[big.Completed.Count - 1].Id
                    == "b" + (AgentSession.CompletedCap + 49).ToString(CultureInfo.InvariantCulture));

            // And the shadow verdict, which is what makes the module judgeable in auto mode.
            DateTime now = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
            var askRules = new RuleSet();
            askRules.Ask.Add("Bash(curl *)");
            Detection stoodDown = BlockedDetector.Evaluate(
                Session(now.AddSeconds(-600), now.AddSeconds(-600), "curl https://x", "auto"),
                askRules, 30, now);
            probe.Check("WITNESS an auto-mode session still stands down",
                stoodDown.Outcome == DetectionOutcome.StoodDownAutoMode);
            probe.Check("WITNESS ...but records that it WOULD have been flagged",
                stoodDown.WouldHaveBeen == DetectionOutcome.Blocked);
            // The negative case has to be an EXPLICITLY ALLOWED command, and the first draft of
            // this assertion got it wrong: it used `echo hi` against a rule set with no allow
            // entry, and nothing-matched PROMPTS by design, so the shadow verdict was Blocked and
            // correct. The assertion failed and the code was right -- which is the outcome a
            // non-degenerate negative is supposed to produce when it is written badly.
            askRules.Allow.Add("Bash(echo *)");
            Detection quiet = BlockedDetector.Evaluate(
                Session(now.AddSeconds(-600), now.AddSeconds(-600), "echo hi", "auto"),
                askRules, 30, now);
            probe.Check("...and does not claim it would have, when the rules allow the call",
                quiet.Outcome == DetectionOutcome.StoodDownAutoMode
                && quiet.WouldHaveBeen == DetectionOutcome.StalledButAllowed);
            return true;
        }

        /// <summary>A finished call, for the approval assertions above.</summary>
        private static OutstandingCall Completed(string id, string command)
        {
            return new OutstandingCall
            {
                Id = id,
                Tool = "Bash",
                Command = command,
                StartedUtc = new DateTime(2026, 9, 18, 11, 0, 0, DateTimeKind.Utc),
                Mode = "auto",
            };
        }

        /// <summary>
        /// The prompt-option classifier, which is the safety mechanism for pressing anything.
        ///
        /// Every case below is also run through the Python reference by
        /// tests/difftest-prompt-options.py, which parses this table out of this file, so the two
        /// implementations cannot drift. The reference carries the bundle transcription and an
        /// --audit mode that re-derives the option set from whatever Claude Code is installed.
        ///
        /// The cases are chosen so that a WRONG implementation fails at least one of them. A
        /// classifier that answered ApproveOnce for everything, or Unknown for everything, or that
        /// prefix-matched "yes", fails here.
        /// </summary>
        private static bool SelfCheckPromptOptions(SelfTestProbe probe)
        {
            // ---- the three that mean "approve this one call" -----------------------------
            probe.Check("WITNESS a bare Yes is approve-once",
                KindOf("Yes") == OptionKind.ApproveOnce);
            probe.Check("WITNESS a rendered template is approve-once",
                KindOf("Yes, allow access to example.com") == OptionKind.ApproveOnce
                && KindOf("Yes, allow curl") == OptionKind.ApproveOnce);

            // ---- the seven that are WIDER or change the mode -----------------------------
            // Every one of these is a string the bundle really ships, and pressing any of them is
            // the failure all four public tools were measured committing.
            probe.Check("WITNESS 'and don't ask again' is a WIDER grant, never pressed",
                KindOf("Yes, and don't ask again") == OptionKind.ApproveWider);
            probe.Check("WITNESS 'all edits this session' is a WIDER grant",
                KindOf("Yes, allow all edits this session") == OptionKind.ApproveWider);
            probe.Check("WITNESS 'set auto mode as my default' is a MODE change",
                KindOf("Yes, set auto mode as my default") == OptionKind.ModeChange);
            probe.Check("WITNESS the other three mode changes are mode changes",
                KindOf("Yes, and auto-accept") == OptionKind.ModeChange
                && KindOf("Yes, and manually approve edits") == OptionKind.ModeChange
                && KindOf("Yes, return to normal mode") == OptionKind.ModeChange);

            // ---- normalization, and the one that decides a permanent grant ---------------
            // A capture renders an apostrophe as U+2019 about as often as U+0027, and this is the
            // string where that decides whether a WIDER grant is recognised or silently Unknown.
            probe.Check("WITNESS a curly apostrophe still reads as the wider grant",
                KindOf("Yes, and don\u2019t ask again") == OptionKind.ApproveWider);
            probe.Check("list chrome and casing are stripped",
                KindOf("  2. YES  ") == OptionKind.ApproveOnce
                && KindOf("\u276F Yes") == OptionKind.ApproveOnce);
            probe.Check("a trailing ellipsis is decoration",
                KindOf("Other\u2026") == OptionKind.FreeText);

            // ---- the allowlist: anything else refuses ------------------------------------
            probe.Check("WITNESS an unseen 'Yes, ...' variant is UNKNOWN, not approve",
                KindOf("Yes, and trust this publisher forever") == OptionKind.Unknown);
            probe.Check("empty and null are UNKNOWN",
                KindOf("") == OptionKind.Unknown && KindOf(null) == OptionKind.Unknown);
            // A prefix match on the bare word would make this approve-once, and it is exactly how a
            // prefix implementation ends up pressing "set auto mode as my default".
            probe.Check("WITNESS 'yes' is EXACT, so a longer unknown string starting with it refuses",
                KindOf("Yes please") == OptionKind.Unknown);

            // ---- Choose: the row actually pressed ----------------------------------------
            var real = new List<string>
            {
                "Yes",
                "Yes, and don't ask again",
                "No, and tell Claude what to do differently",
            };
            PromptDecision chosen = PromptOptions.Choose(real);
            probe.Check("WITNESS a real prompt presses the approve-once row",
                chosen.WillPress && chosen.Index == 0);
            probe.Check("...and says what it declined",
                chosen.Reason != null && chosen.Reason.IndexOf("declined 1", StringComparison.Ordinal) >= 0);

            // ONE UNKNOWN POISONS THE PROMPT. This is the rule that makes text matching safe: a
            // misread capture or a new bundle string stops the module rather than being ignored.
            var poisoned = new List<string> { "Yes", "Yes, and something new", "No" };
            PromptDecision refused = PromptOptions.Choose(poisoned);
            probe.Check("WITNESS one unrecognised option refuses the WHOLE prompt",
                !refused.WillPress && refused.Reason.IndexOf("unrecognised", StringComparison.Ordinal) >= 0);

            probe.Check("a prompt with no approve-once row is refused",
                !PromptOptions.Choose(new List<string> { "Yes, and don't ask again", "No" }).WillPress);
            probe.Check("WITNESS two approve-once rows are refused as ambiguous rather than guessed",
                !PromptOptions.Choose(new List<string> { "Yes", "Allow", "No" }).WillPress);
            probe.Check("no options at all is refused",
                !PromptOptions.Choose(new List<string>()).WillPress
                && !PromptOptions.Choose(null).WillPress);

            // The mode is never ours to change, asserted directly rather than left as a
            // consequence of the filter: no option set may ever produce a mode-change press.
            var everyModeChange = new List<string>
            {
                "Yes, and auto-accept", "Yes, and manually approve edits",
                "Yes, return to normal mode", "Yes, set auto mode as my default",
            };
            PromptDecision never = PromptOptions.Choose(everyModeChange);
            probe.Check("WITNESS a prompt of ONLY mode changes presses nothing",
                !never.WillPress);
            return true;
        }

        /// <summary>Classification of one option, for the assertions above.</summary>
        private static OptionKind KindOf(string text)
        {
            string matched;
            return PromptOptions.Classify(text, out matched);
        }

        // Assembled rather than written as literals so the assertions below stay readable.
        private const string NL_ = "\n";
        private const string TAB_ = "\t";
        private const string QT_ = "\"";

        /// <summary>
        /// Editing VS Code's argv.json without destroying it.
        ///
        /// The fixture below is the REAL shape: the file VS Code ships carries fourteen comment
        /// lines, including its own "PLEASE DO NOT CHANGE WITHOUT UNDERSTANDING THE IMPACT", and a
        /// strict JSON parse of it FAILS. So every assertion here is about a text edit that leaves
        /// those comments alone, and the first one is the one that matters: parse-then-reserialize
        /// would have silently deleted Microsoft's documentation out of the user's config.
        /// </summary>
        private static bool SelfCheckVsCodeSetup(SelfTestProbe probe)
        {
            // The shipped file, comments and tabs and all.
            string shipped =
                "// This configuration file allows you to pass permanent command line arguments." + NL_ +
                "//" + NL_ +
                "// PLEASE DO NOT CHANGE WITHOUT UNDERSTANDING THE IMPACT" + NL_ +
                "{" + NL_ +
                TAB_ + "// Allows to disable crash reporting." + NL_ +
                TAB_ + QT_ + "enable-crash-reporter" + QT_ + ": true," + NL_ +
                TAB_ + QT_ + "crash-reporter-id" + QT_ + ": " + QT_ + "abc" + QT_ + NL_ +
                "}" + NL_;

            probe.Check("WITNESS the shipped file is JSONC, so the module must not parse it",
                VsCodeSetup.StripLineComments(shipped).IndexOf("PLEASE DO NOT", StringComparison.Ordinal) < 0
                && shipped.IndexOf("PLEASE DO NOT", StringComparison.Ordinal) >= 0);

            string enabled = VsCodeSetup.WithPort(shipped, 9321);
            probe.Check("the port is added as a STRING, because a number is silently ignored",
                enabled != null
                && enabled.IndexOf(QT_ + "remote-debugging-port" + QT_ + ": " + QT_ + "9321" + QT_,
                                   StringComparison.Ordinal) >= 0);
            // The whole point of a text edit. Every comment line survives, and so do the other keys.
            probe.Check("WITNESS every comment line survives the edit",
                CountOccurrences(enabled, "//") == CountOccurrences(shipped, "//")
                && enabled.IndexOf("PLEASE DO NOT CHANGE", StringComparison.Ordinal) >= 0);
            probe.Check("...and so do the keys that were already there",
                enabled.IndexOf("enable-crash-reporter", StringComparison.Ordinal) >= 0
                && enabled.IndexOf("crash-reporter-id", StringComparison.Ordinal) >= 0);
            probe.Check("the result reads back as the port that was written",
                VsCodeSetup.ReadPort(enabled) == 9321);

            // Idempotent: enabling twice must not add a second key.
            string twice = VsCodeSetup.WithPort(enabled, 9321);
            probe.Check("WITNESS enabling twice does not add a second key",
                CountOccurrences(twice, "remote-debugging-port") == 1);
            string moved = VsCodeSetup.WithPort(enabled, 9999);
            probe.Check("changing the port replaces the value rather than appending",
                VsCodeSetup.ReadPort(moved) == 9999
                && CountOccurrences(moved, "remote-debugging-port") == 1);

            // Disable must leave the file valid, which means no dangling comma.
            string off = VsCodeSetup.WithoutPort(enabled);
            probe.Check("WITNESS disabling removes the key",
                VsCodeSetup.ReadPort(off) == 0
                && off.IndexOf("remote-debugging-port", StringComparison.Ordinal) < 0);
            probe.Check("...and leaves the comments and the other keys alone",
                CountOccurrences(off, "//") == CountOccurrences(shipped, "//")
                && off.IndexOf("crash-reporter-id", StringComparison.Ordinal) >= 0);

            // An empty object is the other real shape: VS Code writes one on first run.
            string bare = "{" + NL_ + "}" + NL_;
            string bareOn = VsCodeSetup.WithPort(bare, 9321);
            probe.Check("an argv.json with no members gets the key with NO leading comma",
                bareOn != null && VsCodeSetup.ReadPort(bareOn) == 9321
                && bareOn.IndexOf(",", StringComparison.Ordinal) < 0);

            // A `//` inside a string value must not be treated as a comment, or a Windows path in
            // some other key would truncate the line and hide a real setting.
            string pathy = "{" + NL_ + TAB_ + QT_ + "proxy-bypass-list" + QT_ + ": "
                           + QT_ + "http://x" + QT_ + "," + NL_
                           + TAB_ + QT_ + "remote-debugging-port" + QT_ + ": " + QT_ + "7777" + QT_
                           + NL_ + "}" + NL_;
            probe.Check("WITNESS a // inside a string value is not mistaken for a comment",
                VsCodeSetup.ReadPort(pathy) == 7777);

            // REFUSALS. A file that is not an argv.json must not be overwritten with a fresh one.
            probe.Check("WITNESS a file with no top-level object is refused, not replaced",
                VsCodeSetup.WithPort("this is not json at all", 9321) == null);
            probe.Check("an out-of-range port is refused",
                VsCodeSetup.WithPort(shipped, 0) == null
                && VsCodeSetup.WithPort(shipped, 70000) == null);

            // CRLF must survive, or the whole file shows as modified in a diff.
            string crlf = "{\r\n}\r\n";
            string crlfOn = VsCodeSetup.WithPort(crlf, 9321);
            probe.Check("CRLF line endings are preserved",
                crlfOn != null && crlfOn.IndexOf("\r\n", StringComparison.Ordinal) >= 0);

            // The port default must not be the one every Chromium tool grabs.
            probe.Check("WITNESS the default port is not 9222, which Chrome and Edge take",
                VsCodeSetup.DefaultPort != 9222);

            // Probing is the only thing that means "working", and it must say NO for a dead port.
            probe.Check("WITNESS a port nothing listens on probes as closed",
                !VsCodeSetup.Probe(1, 250) && !VsCodeSetup.Probe(0, 250));

            // THE ROUND TRIP, AGAINST THE REAL FILE ON THIS MACHINE. Read-only: the assertion is
            // that enabling and then disabling returns the user's argv.json BYTE FOR BYTE, which is
            // the property that says the edit is surgical rather than a rewrite. The fixtures above
            // cannot prove it, because I wrote them; this file I did not.
            //
            // DEGRADED and said out loud when there is no argv.json, rather than skipped: VS Code
            // writes one on first run, so its absence is a legitimate state on a machine that has
            // never opened it.
            string realPath = null;
            foreach (string candidate in VsCodeSetup.CandidatePaths())
            {
                try { if (System.IO.File.Exists(candidate)) { realPath = candidate; break; } }
                catch { }
            }
            if (realPath == null)
            {
                probe.Note("DEGRADED: no argv.json on this machine, so the round trip against a "
                           + "file this module did not write was NOT exercised");
            }
            else
            {
                string original = null;
                try { original = System.IO.File.ReadAllText(realPath); }
                catch { }
                if (original == null)
                {
                    probe.Note("DEGRADED: argv.json could not be read, so the round trip was NOT "
                               + "exercised");
                }
                else
                {
                    int had = VsCodeSetup.ReadPort(original);
                    string on = VsCodeSetup.WithPort(original, 9321);
                    probe.Check("WITNESS the real argv.json accepts the key (" + realPath + ")",
                        on != null && VsCodeSetup.ReadPort(on) == 9321);
                    if (on != null)
                    {
                        probe.Check("WITNESS every comment in the REAL file survives",
                            CountOccurrences(on, "//") == CountOccurrences(original, "//"));
                        // Only a round trip from a file that did NOT already have the key can come
                        // back identical; if it had one, restoring it is the comparison instead.
                        string restored = VsCodeSetup.WithoutPort(on);
                        if (had <= 0)
                        {
                            probe.Check("WITNESS enable-then-disable returns the real file BYTE FOR BYTE",
                                string.Equals(restored, original, StringComparison.Ordinal));
                        }
                        else
                        {
                            probe.Check("the real file already asks for a port, so the round trip "
                                        + "restores that value rather than removing it",
                                VsCodeSetup.ReadPort(VsCodeSetup.WithPort(restored, had)) == had);
                        }
                    }
                }
            }

            // Inspect on a path that does not exist reports NotFound with a reason, never throws.
            SetupReport missing = VsCodeSetup.Inspect(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dp-no-such-argv.json"), 100);
            probe.Check("a missing argv.json reports NotFound and says what to do",
                missing.State == SetupState.NotFound
                && !string.IsNullOrEmpty(missing.Detail));
            return true;
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Auto-approve: the switch, the tray row, and the two controls agreeing.
        ///
        /// The first assertion is the one that matters. This is the only setting in the module
        /// that presses a button on the user's behalf, so a default of ON would be
        /// indefensible -- and "it defaults off" is exactly the kind of claim that stops being
        /// true when someone later adds a convenience.
        ///
        /// The tray label is asserted in all THREE states because the middle one is the design:
        /// on-but-unable must not look like on-and-working. Reporting intent as capability is
        /// the defect the pane status row already shipped once.
        ///
        /// The pane/tray agreement checks are here because this setting has TWO controls, and
        /// the first version of it failed exactly that way: LoadPaneValues did not carry the
        /// key, so the checkbox read unchecked while the tray said on, and saving the pane
        /// wrote the checkbox's lie back over the setting.
        /// </summary>
        private static bool SelfCheckAutoApprove(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-aa"))
            {
                host.UseStorage("agentflow", storage);
                var module = new AgentFlowModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes[0];

                probe.Check("WITNESS auto-approve is OFF until it is asked for",
                    !module.AutoApprove);
                probe.Check("WITNESS the pane agrees it is off, rather than not saying",
                    pane.Load()[SettingAutoApprove] == "false");
                probe.Check("the tray says off, in red",
                    module.AutoApproveTrayLabel().StartsWith("🔴", StringComparison.Ordinal)
                    && module.AutoApproveTrayLabel().IndexOf("off", StringComparison.Ordinal) >= 0);

                // On, with nothing answering: AMBER, and it has to SAY it cannot act yet.
                module.ToggleAutoApproveFromTray();
                probe.Check("the tray toggle turns it on", module.AutoApprove);
                probe.Check("WITNESS on-but-unreachable is amber and says what it waits for",
                    module.AutoApproveTrayLabel().StartsWith("🟡", StringComparison.Ordinal)
                    && module.AutoApproveTrayLabel().IndexOf("waiting", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS the pane reports what the tray just did",
                    pane.Load()[SettingAutoApprove] == "true");

                module._portAnswering = true;
                probe.Check("WITNESS green means on AND reachable, nothing less",
                    module.AutoApproveTrayLabel().StartsWith("🟢", StringComparison.Ordinal));
                module._portAnswering = false;

                // The other direction: the pane must be able to switch it off again.
                pane.Save(new Dictionary<string, string> { { SettingAutoApprove, "false" } });
                probe.Check("WITNESS saving the pane switches it off",
                    !module.AutoApprove);
                probe.Check("...and the tray goes back to red",
                    module.AutoApproveTrayLabel().StartsWith("🔴", StringComparison.Ordinal));

                // Turning it back ON must not inherit the green it had before. The port is
                // only probed while the switch is on, so a leftover true is not evidence --
                // and this is the one path where the tray could claim a capability nobody
                // ever checked for.
                module._portAnswering = true;
                module.ToggleAutoApproveFromTray();
                probe.Check("WITNESS switching it on discards the last probe, so green is earned",
                    module.AutoApproveTrayLabel().StartsWith("🟡", StringComparison.Ordinal));
                module.ToggleAutoApproveFromTray();

                // The tray has to OFFER it, carry the same text as the helper, and sit in its
                // own group so the host draws a separator between watching and PRESSING.
                TrayItem row = null;
                foreach (TrayItem item in host.TrayItems[0].BuildChildren())
                {
                    if (item.Label != null
                        && item.Label.IndexOf("Auto-approve", StringComparison.Ordinal) >= 0)
                        row = item;
                }
                probe.Check("WITNESS the tray menu offers auto-approve at all", row != null);
                probe.Check("the tray row shows the same state the helper reports",
                    row != null && row.Label == module.AutoApproveTrayLabel());
                probe.Check("...in its own group, so pressing is separated from watching",
                    row != null && row.Group != 0 && row.Click != null);

                module.Shutdown();
            }
            return true;
        }

        private static bool SelfCheckCacheBound(SelfTestProbe probe)
        {
            int normalized, compiled, limit;
            PermissionRules.CacheStats(out normalized, out compiled, out limit);
            probe.Check("the match cache reports a bound", limit > 0);
            if (limit <= 0) return false;

            // limit + 10 distinct rules, so the cap is crossed and then refilled a little. Each
            // iteration inserts two normalize keys (the rule and the permission) and one compiled
            // key, so both caches are pushed past the cap.
            for (int i = 0; i < limit + 10; i++)
            {
                string tag = "cachebound" + i.ToString(CultureInfo.InvariantCulture);
                PermissionRules.RuleMatches("Bash(" + tag + " *)", "Bash(" + tag + ")");
            }
            int afterNormalized, afterCompiled, sameLimit;
            PermissionRules.CacheStats(out afterNormalized, out afterCompiled, out sameLimit);

            // This is the assertion that fails if the eviction is removed: without the clear, the
            // counts would be limit + 10 compiled and 2 * (limit + 10) normalized. It is not
            // vacuous, because more distinct keys than the cap were just pushed through.
            probe.Check("WITNESS the match caches evict instead of growing without bound ("
                        + afterNormalized + " normalized, " + afterCompiled + " compiled, cap "
                        + sameLimit + ")",
                afterNormalized <= limit && afterCompiled <= limit
                && afterNormalized > 0 && afterCompiled > 0);

            // And the clear must be invisible to callers: a verdict computed after eviction has to
            // match the one computed before it. These four are the same rules SelfCheckRules
            // asserts against a warm cache.
            var rules = new RuleSet();
            rules.Allow.Add("Bash(echo *)");
            rules.Ask.Add("Bash(curl *)");
            rules.Deny.Add("Bash(rm *)");
            probe.Check("verdicts are unchanged after the cache was evicted",
                PermissionRules.EvaluateCall("Bash", "echo hello", null, rules) == RuleVerdict.WouldAllow
                && PermissionRules.EvaluateCall("Bash", "curl https://x", null, rules) == RuleVerdict.WouldPrompt
                && PermissionRules.EvaluateCall("Bash", "rm x", null, rules) == RuleVerdict.WouldDeny
                && PermissionRules.RuleMatches("Bash(git:*)", "Bash(git status)"));
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

            // WITNESS, and it is a defect found on REAL data: this used to key on `auto` alone, so a
            // session in acceptEdits fired. Measured precision is ~0.4% in auto, acceptEdits AND
            // plan alike; only `default` differs. The stand-down is now an allow-list of one.
            foreach (string other in new[] { "acceptEdits", "plan", "bypassPermissions", "" })
            {
                AgentSession notDefault = Session(now.AddSeconds(-600), now.AddSeconds(-600),
                                                  "curl https://x", other);
                probe.Check("WITNESS '" + (other.Length == 0 ? "<unset>" : other)
                            + "' mode stands down, not just auto",
                    BlockedDetector.Evaluate(notDefault, rules, 30, now).Outcome
                        == DetectionOutcome.StoodDownAutoMode);
            }
            probe.Check("...while default mode is still acted on",
                BlockedDetector.Evaluate(
                    Session(now.AddSeconds(-600), now.AddSeconds(-600), "curl https://x", "default"),
                    rules, 30, now).Outcome == DetectionOutcome.Blocked);

            // WITNESS, the second real-data defect. An `Agent` call carries no file path, URL or
            // glob, so the rules have nothing to address; evaluating `Agent()` returned WouldPrompt
            // purely because nothing matched, which is a verdict that cannot come out any other way.
            // Three long-running subagents were reported as blocked prompts on the live machine.
            var noArgument = new AgentSession
            {
                SessionId = "s", SawAnyCall = true, LastWriteUtc = now.AddSeconds(-600),
                Mode = "default",
            };
            noArgument.Outstanding.Add(new OutstandingCall
            {
                Id = "c", Tool = "Agent", Command = null, Argument = null,
                StartedUtc = now.AddSeconds(-600), Mode = "default",
            });
            probe.Check("WITNESS a call the rules cannot address is NOT reported as blocked",
                BlockedDetector.Evaluate(noArgument, rules, 30, now).Outcome
                    == DetectionOutcome.NotDecidable);

            // ...but a non-shell tool that DOES carry an addressable argument must still be judged,
            // or the fix above would have quietly disabled every tool except Bash and PowerShell.
            var withArgument = new AgentSession
            {
                SessionId = "s", SawAnyCall = true, LastWriteUtc = now.AddSeconds(-600),
                Mode = "default",
            };
            withArgument.Outstanding.Add(new OutstandingCall
            {
                Id = "c", Tool = "Edit", Command = null, Argument = @"D:\repo\x.ps1",
                StartedUtc = now.AddSeconds(-600), Mode = "default",
            });
            probe.Check("WITNESS a tool WITH an addressable argument is still judged",
                BlockedDetector.Evaluate(withArgument, rules, 30, now).Outcome
                    == DetectionOutcome.Blocked);
            var allowEdit = new RuleSet();
            allowEdit.Allow.Add(@"Edit(D:\repo\x.ps1)");
            probe.Check("...and an allow rule on that argument suppresses it",
                BlockedDetector.Evaluate(withArgument, allowEdit, 30, now).Outcome
                    == DetectionOutcome.StalledButAllowed);

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

        /// <summary>One freshly-built BLOCKED detection, rebuilt per call so nothing carries over.</summary>
        private static List<Detection> OneBlockedDetection()
        {
            DateTime now = DateTime.UtcNow;
            var rules = new RuleSet();
            AgentSession session = Session(now.AddSeconds(-300), now.AddSeconds(-300),
                                           "curl https://example.com", "default");
            session.Cwd = @"D:\work\demo";
            return new List<Detection>
            {
                BlockedDetector.Evaluate(session, rules, 30, now),
            };
        }

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
