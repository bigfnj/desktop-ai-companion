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
    public sealed partial class AgentFlowModule : IModule
    {
        private const string SettingEnabled = "enabled";
        private const string SettingThreshold = "thresholdSeconds";
        private const string SettingWatchClaude = "watchClaude";
        private const string SettingWatchCodex = "watchCodex";
        private const string SettingCooldown = "cooldownSeconds";
        private const string SettingAnimate = "animate";

        /// <summary>The one choice that replaces `enabled` + `autoApprove`. See AgentMode.</summary>
        private const string SettingMode = "mode";
        private const string SettingNotifySound = "notifySound";

        /// <summary>
        /// Press "Yes, allow ... for all projects" instead of the one-call row, when a prompt
        /// offers it. OFF unless the user turns it on, and it only means anything in
        /// auto-approve mode.
        ///
        /// This is the one setting in the module that writes something PERSISTENT on the
        /// user's behalf: the rule is saved to the user settings, outlives the session and
        /// applies in every repository. It exists because the maintainer asked for it after
        /// being shown exactly that, and it unlocks nothing else -- the other four rule
        /// destinations, "don't ask again" and every mode change stay unpressable.
        /// </summary>
        private const string SettingApproveAllProjects = "approveAllProjects";
        private const string SettingNotifySpeak = "notifySpeak";
        private const string SettingAnimPet = "animPet";
        private const string SettingAnimName = "animName";
        private const string FieldApprovals = "aboutApprovals";

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
            Version = "1.1.6",   // 1.1.6: one log line whenever the ability to press CHANGES.
                                 //        Nothing is written when there is nothing to press, so an
                                 //        inert module and a working one were byte-identical in the
                                 //        log; on change only, so the file stays readable. Keyed on
                                 //        the tray sentence, not ApproveState, which folds "waiting
                                 //        for VS Code" and "cannot see the panel" into one value.
                                 // 1.1.5: the pet dropdown lists the pets ON SCREEN, by name.
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
            // 1.2.0 and not lower, because this is a HARD floor rather than a preference: the
            // pane sets Radio/Header/EnabledWhen/FullWidth/PinTop/ReloadOnChange and
            // OptionsPane.LoadPending, and the notify half calls IHost.PlayNotificationSound.
            // A module binds the HOST's shared Contracts, so on an older host those are a
            // MissingMethodException at the first property setter, not a graceful degrade.
            // The load gate is the only thing standing between that and a broken pane.
            MinHostVersion = "1.2.0",
            // Speech for the bubble, Animation for the attention wiggle, Storage for its own
            // settings, AgentTranscripts for the read that is the whole feature. Nothing else: no
            // Network (it never makes a request), no ScreenContext (it does not look at the screen),
            // no Hotkey, no Audio.
            //
            // Animation was MISSING until 2026-09-17 while IHost.PlayAnimationAll was already being
            // called, and the comment above enumerated four flags it does not need without noticing
            // the one it does. Reminder's declaration is the worked example: it declares Animation
            // for exactly this call.
            // InputSynthesis and Network are both here for the approve half, and both are
            // declared even though neither is ENFORCED, because the consent screen is an
            // affirmative claim about what a module does. PluginApi.cs names "any module
            // that automates another application" as InputSynthesis's obvious next holder;
            // this presses buttons in the editor. Network is loopback only -- a debugging
            // port on 127.0.0.1 -- but "makes network requests" is what the flag says and a
            // user deciding whether to install this should not have to know the difference.
            Permissions = ModulePermissions.Speech
                          | ModulePermissions.Animation
                          | ModulePermissions.Storage
                          | ModulePermissions.AgentTranscripts
                          | ModulePermissions.InputSynthesis
                          | ModulePermissions.Network
                          // Reading which pets are installed, and one pet's animation
                          // names out of its own XML, so the animation dropdown can offer
                          // names that pet actually has. GetCompanionManager is gated on
                          // this, so without it the dropdown silently offers nothing.
                          | ModulePermissions.Companions
                          // Audio, because the notify half can play the shared notification
                          // sound. Its absence made that channel inert in the real host --
                          // CompanionHost.PlaySound-style gating refuses an undeclared
                          // module on every call -- while the self-test passed against a
                          // double that did not enforce the gate.
                          | ModulePermissions.Audio,
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
                    IconPng = LoadIconResource("agentflow-orb.png"),
                    // The host re-evaluates this every time the menu opens, which is the only push
                    // channel a module has into the tray. A module cannot update an OPEN menu, so
                    // this is a snapshot by design rather than a live counter.
                    DynamicText = TrayText,
                    BuildChildren = BuildMenu,
                },
            });

            // Built rather than declared, because the schema now VARIES per open: the animation
            // dropdown is populated from the pets actually installed, and from the chosen pet's
            // own XML. The host calls Load before it reads Schema on every build, which is the
            // documented invariant that makes this legal.
            _pane = BuildPane();
            host.AddOptionsPane(_pane);

            _spawnHandler = OnCompanionSpawned;
            host.CompanionSpawned += _spawnHandler;

            // Init is over as far as the HOST is concerned only after this returns, but the
            // module can say so itself -- and must, because every permission-gated verb is
            // refused until ModuleHost adds it to LoadedModules on the line after Init.
            FinishedInitialising();

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
            bool allProjects = ApproveForAllProjects;
            int cdpPort = CdpPort;

            // Reading and parsing transcripts is file IO plus JSON, so it never runs on the tick.
            // Nothing inside this task touches _host.
            Task.Run(() =>
            {
              // The re-entrancy guard covers this ENTIRE body, not just the scan. It used
              // to be released in the scan's own finally, which left the CDP sweep and the
              // click outside it -- and those are several WebSocket round trips with a six
              // second deadline against a ten second tick, so two workers overlapped
              // routinely rather than rarely.
              //
              // What that cost: PressBudget is a bare List<DateTime> documented as
              // poll-thread-only, so two threads meant Prune racing RemoveAt(0), both
              // workers reading Count == 9 and both pressing, and _resetPressBudget losing
              // an update. The guard that exists to stop runaway approving must not itself
              // be the thing that races.
              try
              {
                List<Detection> results = null;
                Dictionary<string, int> approved = null;
                // Collected on the worker, handed to the UI thread, and never touched from
                // both: the feed itself is only ever mutated inside PostToUi.
                var freshApprovals = new List<ApprovalEntry>();
                try
                {
                    results = Scan(watchClaude, watchCodex, threshold, _approvalsCounted,
                                   out approved, freshApprovals);
                }
                catch (Exception)
                {
                    // A scan that throws must not take the app's UI thread down with it, and there
                    // is nothing here worth surfacing to a user: the next tick tries again.
                    results = null;
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

                // Press, if asked to and if there is anything to press. Behind BOTH the
                // user's switch and a port that answered this tick: either one missing and
                // this does not run at all.
                string approvalNote = null;
                bool sawPanel = false;
                if (autoApprove && answering)
                {
                    if (_resetPressBudget) { _resetPressBudget = false; _pressBudget.Reset(); }
                    try { approvalNote = TryApproveOnce(cdpPort, _pressBudget, allProjects, out sawPanel); }
                    catch (Exception) { approvalNote = null; sawPanel = false; }
                    // Nothing found means the last press landed, or there was never
                    // anything there. Either way the prompt in front of it is gone, so the
                    // repeat counter has nothing left to be suspicious about.
                    if (approvalNote == null) _pressBudget.NotePromptCleared();
                }

                // Posted unconditionally, unlike the detections. A scan that threw left
                // results null, and the port state used to be inside that guard -- so one
                // bad scan would freeze the tray dot on whatever it last said, which is the
                // stale-capability report this design exists to avoid.
                {
                    Dictionary<string, int> forUi = approved;
                    List<Detection> forApply = results;
                    bool portUp = answering;
                    bool panelUp = sawPanel;
                    string note = approvalNote;
                    List<ApprovalEntry> forFeed = freshApprovals;
                    PostToUi(() =>
                    {
                        _portAnswering = portUp;
                        _panelReadable = panelUp;
                        // Immediately after the flags and before anything else this tick, so a
                        // capability change is logged ahead of whatever it caused or prevented.
                        LogCapabilityChange();
                        foreach (ApprovalEntry entry in forFeed)
                            _approvalFeed.Record(entry.WhenLocal, entry.Root, entry.Command);
                        if (forApply != null)
                        {
                            Apply(forApply);
                            LogApprovals(forUi);
                        }
                        LogApprovalAttempt(note);
                    });
                }
              }
              finally
              {
                  // PostToUi has already been called by here; it POSTS rather than waits,
                  // so the UI work is not inside the guard and cannot deadlock against it.
                  Volatile.Write(ref _scanning, 0);
              }
            });
        }

        /// <summary>
        /// Look for a pending permission prompt and press the approve-once row, once.
        ///
        /// Returns a line worth logging, or null when there was nothing to do. Null is the
        /// normal answer: a prompt is rare and a line every ten seconds saying "nothing"
        /// would bury the log this module exists to make readable.
        ///
        /// THE DECISION IS NOT MADE HERE. This reads text, hands it to PromptOptions, and does
        /// what it is told; PromptOptions refuses anything it does not recognise, refuses a
        /// prompt with no approve-once row, and refuses one with more than one. There is no
        /// heuristic in this method and there should never be: the module was asked to be
        /// lightweight, and a guess about which button to press is the one kind of wrong this
        /// feature cannot afford.
        /// </summary>
        internal static string TryApproveOnce(int port, PressBudget budget, bool allProjects,
                                             out bool sawPanel)
        {
            return CdpApprover.Sweep(port, view => Decide(port, view, budget, allProjects), 1500,
                                     out sawPanel);
        }

        /// <summary>
        /// What to do about one prompt that was read. Split out from the sweep so it can be
        /// exercised without an editor: everything above it is sockets, and everything in it is
        /// the decision, which is the half worth asserting.
        /// </summary>
        internal static string Decide(int port, PromptView view, PressBudget budget,
                                      bool allProjects)
        {
            if (view == null || view.Options.Count == 0) return null;

            PromptDecision decision = PromptOptions.Choose(view.Options, allProjects);
            if (!decision.WillPress) return decision.Reason;

            // The UI's own disabled flag wins over anything the text says. A row greyed out
            // because the request is already being answered is not an invitation.
            if (decision.Index < view.Disabled.Count && view.Disabled[decision.Index])
                return "refused: the approve-once row is disabled";

            // Last gate before anything is pressed, and deliberately AFTER the classifier:
            // a prompt this refuses to understand must not spend budget, or a screen full
            // of unrecognised options would exhaust the allowance and mask a real loop.
            if (budget != null)
            {
                string refusal;
                if (!budget.TryPress(PressBudget.Signature(view.ToolName, view.Options),
                                     DateTime.UtcNow, out refusal))
                    return refusal;
            }

            string outcome = CdpApprover.Click(port, view.TargetId, decision.Index,
                                               decision.ChosenRaw, 1500);
            return "auto-approve " + outcome + " for " + DescribeSubject(view)
                   + ": " + decision.Reason;
        }

        /// <summary>
        /// The tool name, reduced to something that cannot carry content.
        ///
        /// It is read out of the prompt header, so it is text from the screen even though in
        /// practice it is always a tool identifier like Bash or Edit. Letters, digits, dash and
        /// underscore only, truncated -- a name that fails that is reported as unknown rather
        /// than passed through, because the log is meant to be attachable to a public issue.
        /// </summary>
        /// <summary>
        /// The static half of each prompt header this agent renders, and what it is safe to
        /// call it in a log line.
        ///
        /// Read out of the shipped bundle on 2026-09-18: FIVE shapes, and only the last names
        /// its tool in a &lt;strong&gt;. The other four supply their own renderer --
        /// "Make this edit to &lt;path&gt;?" and friends -- which is why the first real prompt
        /// pressed logged itself as "an unnamed tool".
        ///
        /// An ALLOWLIST, for the same reason PromptOptions is one: a header shape nobody has
        /// seen yet can contain anything, and the safe response to not recognising it is to
        /// say so rather than to echo it into a file meant to be attachable to a public issue.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] KnownHeaders =
        {
            new KeyValuePair<string, string>("make this edit to", "an edit"),
            new KeyValuePair<string, string>("allow reading from", "a file read"),
            new KeyValuePair<string, string>("allow write to", "a file write"),
            new KeyValuePair<string, string>("use skill", "a skill"),
        };

        /// <summary>
        /// What was approved, in terms safe to write down.
        ///
        /// Order matters: a tool named in &lt;strong&gt; is the most precise answer and the
        /// generic header carries nothing else, so it wins. Otherwise the header shape names
        /// the action and the file EXTENSION qualifies it -- ".ps1" says what kind of thing
        /// was touched while naming no person, which a file name cannot promise.
        /// </summary>
        internal static string DescribeSubject(PromptView view)
        {
            if (view == null) return "an unrecognised prompt";

            string tool = SafeToolName(view.ToolName);
            if (view.ToolName != null && view.ToolName.Length > 0) return tool;

            string header = (view.UnsafeHeader ?? "").Trim().ToLowerInvariant();
            foreach (KeyValuePair<string, string> known in KnownHeaders)
            {
                if (!header.StartsWith(known.Key, StringComparison.Ordinal)) continue;
                string extension = SafeExtension(view.PathExtension);
                return extension.Length == 0
                    ? known.Value
                    : known.Value + " (." + extension + ")";
            }
            return "an unrecognised prompt";
        }

        /// <summary>
        /// An extension, or nothing. Letters and digits only, eight at most.
        ///
        /// The filter is not decoration. This value is read off the screen, and the span it
        /// comes from holds a full absolute path on Windows -- upstream splits on "/" to take
        /// the leaf, which splits nothing here. Anything that does not look like an extension
        /// is dropped rather than trimmed, because a half-parsed path is still a path.
        /// </summary>
        internal static string SafeExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension) || extension.Length > 8) return "";
            foreach (char c in extension)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9');
                if (!ok) return "";
            }
            return extension.ToLowerInvariant();
        }

        internal static string SafeToolName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "an unnamed tool";
            if (name.Length > 32) return "an unrecognised tool";
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return "an unrecognised tool";
            }
            return name;
        }

        /// <summary>The last line written, so a standing refusal is not repeated every tick.</summary>
        private string _lastApprovalNote;

        /// <summary>
        /// How much pressing this is still willing to do. Touched ONLY from the poll thread.
        /// </summary>
        private readonly PressBudget _pressBudget = new PressBudget();

        /// <summary>
        /// Set from the UI thread when the switch moves, consumed by the poll.
        ///
        /// A flag rather than calling Reset() from the tray handler, because the budget owns
        /// a List that the poll thread mutates and reaching into it from the UI thread would
        /// be a data race on a type with no locking -- for a reset that can perfectly well
        /// wait one tick.
        /// </summary>
        private volatile bool _resetPressBudget;

        /// <summary>
        /// Write what the approver did, and do it once per distinct outcome.
        ///
        /// The repeat guard matters more than it looks. A prompt the classifier refuses stays
        /// on screen until the user answers it, so without this the same refusal would be
        /// written every ten seconds for as long as they were away from the keyboard.
        /// </summary>
        private void LogApprovalAttempt(string note)
        {
            if (string.IsNullOrEmpty(note)) { _lastApprovalNote = null; return; }
            if (string.Equals(note, _lastApprovalNote, StringComparison.Ordinal)) return;
            _lastApprovalNote = note;
            Log(note);
        }
        /// <summary>
        /// Read every live transcript and evaluate it. Pure enough to call from a worker: no host,
        /// no UI, no module state beyond the settings snapshot handed in.
        /// </summary>
        internal static List<Detection> Scan(bool watchClaude, bool watchCodex, double threshold)
        {
            Dictionary<string, int> ignored;
            return Scan(watchClaude, watchCodex, threshold, null, out ignored, null);
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
                                             out Dictionary<string, int> approved,
                                             IList<ApprovalEntry> recent)
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
                    Tally(session, rules, approvalsCounted, approved, live, recent);
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
                    Tally(session, rules, approvalsCounted, approved, live, recent);
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
                                  Dictionary<string, int> approved, HashSet<string> live,
                                  IList<ApprovalEntry> recent)
        {
            if (session == null) return;
            if (session.Completed != null)
                foreach (OutstandingCall call in session.Completed)
                    if (call != null && !string.IsNullOrEmpty(call.Id))
                        live.Add(session.SessionId + "/" + call.Id);
            if (approvalsCounted == null) return;
            Dictionary<string, int> tally =
                BlockedDetector.ApprovedSince(session, rules, approvalsCounted, recent);
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

            // A quip rather than the one fixed sentence, but carrying the same three facts,
            // so variety costs no information. Describe() is still the fallback: if the
            // detection has nothing worth saying, neither has a quip.
            if (string.IsNullOrEmpty(BlockedDetector.Describe(speakThis))) return;
            string line = _quips.Next(
                BlockedDetector.ShortProject(
                    speakThis.Session != null ? speakThis.Session.Cwd : null),
                speakThis.Call != null ? speakThis.Call.Tool : null,
                DescribeWait(speakThis.IdleSeconds));
            if (string.IsNullOrEmpty(line)) line = BlockedDetector.Describe(speakThis);

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
            // Three independent channels now, which is what the pane offers. A user who
            // wants a chime and no chatter gets exactly that.
            if (NotifySpeakOn && AgentMode.Speaks(Mode)) _host.SayAll(line);
            if (NotifySoundOn) _host.PlayNotificationSound(Info.Id);
            if (Animate)
            {
                // The pet's OWN animation, with the coverage list behind it. The previous
                // list was boing/jump/run, and boing exists on one of the 53 bundled pets,
                // so this silently did nothing on nineteen of them.
                var candidates = new List<string>(PetAnimations.Candidates(
                    StoredAnimPet, _settings == null ? "" : _settings.Get(SettingAnimName, "")));
                _host.PlayAnimationAll(candidates);
            }
            _budget.Record(speakThis, now);
            // "spoke" rather than "notified", and only on the path where a companion was on screen
            // and speech was on. The previous wording was a log line that could not fail: it was
            // written after SayAll returned, which it does whether or not anything was shown.
            Log("spoke about " + (speakThis.ToolName ?? "?") + " waiting "
                + ((int)Math.Round(speakThis.IdleSeconds)).ToString(CultureInfo.InvariantCulture)
                + "s in session " + Short(speakThis.Session));
        }

        /// <summary>Seconds as a person would say them. Mirrors BlockedDetector.Format.</summary>
        internal static string DescribeWait(double seconds)
        {
            if (seconds < 90.0)
                return ((int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + "s";
            return ((int)Math.Round(seconds / 60.0)).ToString(CultureInfo.InvariantCulture) + "m";
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

        /// <summary>
        /// Whether the poll does anything at all. Now derived from the mode, so the dozen
        /// existing `if (!Enabled) return;` guards keep their meaning without being touched.
        /// </summary>
        private bool Enabled { get { return AgentMode.Scans(Mode); } }
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
                // Header rows are display-only too, and their ids do not start with
                // "about". Writing one would put a paragraph of prose into settings.json.
                if (entry.Key.StartsWith("hdr", StringComparison.Ordinal)) continue;
                if (entry.Key == FieldApprovals) continue;

                // The radio hands back the LABEL the user saw, not the stored id. Fortunes
                // does the same for its content level, and for the same reason: the label
                // is what fits in a pane and the id is what survives a reword.
                if (entry.Key == SettingMode)
                {
                    _settings.Set(SettingMode, AgentMode.FromDisplay(entry.Value));
                    continue;
                }
                // Same split: the dropdown shows "Pearl", the setting stores the type id the
                // XML is read by. Storing the display name would break the moment a pet was
                // renamed, and read as "that pet has no animations".
                if (entry.Key == SettingAnimPet)
                {
                    _settings.Set(SettingAnimPet, PetTypeIdFor(entry.Value));
                    continue;
                }
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
        /// The last few approvals, WITH their command text, for the options pane only.
        ///
        /// UI thread only. The scan collects into a throwaway list on the worker and the
        /// UI thread folds it in, so this object is never touched from two threads -- the
        /// same discipline the rest of the poll already follows.
        /// </summary>
        private readonly ApprovalFeed _approvalFeed = new ApprovalFeed();

        /// <summary>Held per module instance, so "do not repeat" spans the whole session.</summary>
        private readonly QuipPicker _quips = new QuipPicker();

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
        /// <summary>
        /// The port to ask VS Code for.
        ///
        /// READ ONLY in code: there is no pane field and no action that writes it, so moving
        /// it off a collision means editing settings.json by hand. The comment here used to
        /// imply a control existed. Kept as a setting because the hand-edit is a real escape
        /// hatch, but said plainly rather than promised.
        /// </summary>
        private const string SettingCdpPort = "cdpPort";

        /// <summary>The user's INTENT. Separate from whether the module CAN act, deliberately.</summary>
        /// <summary>
        /// The mode, migrated from the legacy pair when this install predates it.
        ///
        /// Read through AgentMode.Migrate on EVERY read rather than rewritten once at Init.
        /// A one-time rewrite has to decide what to do when the write fails, and gets it
        /// wrong quietly; deriving it every time is cheap and cannot half-happen. The new
        /// key is written the first time the user touches the pane, and until then the old
        /// keys keep working exactly as they did.
        /// </summary>
        internal string Mode
        {
            get
            {
                if (_settings == null) return AgentMode.Notify;
                return AgentMode.Migrate(
                    _settings.Get(SettingMode, ""),
                    _settings.GetBool(SettingEnabled, true),
                    _settings.GetBool(SettingAutoApprove, false));
            }
        }

        private bool AutoApprove { get { return AgentMode.Presses(Mode); } }

        internal bool ApproveForAllProjects
        {
            get { return _settings != null && _settings.GetBool(SettingApproveAllProjects, false); }
        }

        private bool NotifySoundOn
        {
            get { return _settings != null && _settings.GetBool(SettingNotifySound, false); }
        }

        private bool NotifySpeakOn
        {
            get { return _settings == null || _settings.GetBool(SettingNotifySpeak, true); }
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

        /// <summary>
        /// Whether the last sweep could actually SEE the conversation.
        ///
        /// Separate from _portAnswering because they fail differently and the user can only
        /// fix one of them: the port not answering means VS Code is closed or was started
        /// without the flag, while the port answering and the panel being unreadable means
        /// the editor is there but the approver cannot get at it. Both are orange -- it
        /// cannot press either way -- but conflating them into one flag is what produced a
        /// module that reported "no prompt" while blind.
        /// </summary>
        private volatile bool _panelReadable;

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
                    IconPng = StatusDot.For(AutoApproveState),
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
        internal ApproveState AutoApproveState
        {
            get
            {
                if (!AutoApprove) return ApproveState.Off;
                return _portAnswering && _panelReadable
                    ? ApproveState.Able
                    : ApproveState.CannotSee;
            }
        }

        /// <summary>
        /// The row text. The COLOUR now lives in the icon, not in the string.
        ///
        /// It used to be a coloured emoji, which the tray menu renders in the menu font as a
        /// flat grey ring -- all three states looked the same, so the row could not say the one
        /// thing it exists to say. The words still distinguish them, for anyone reading rather
        /// than glancing and for a screen reader, which an icon tells nothing at all.
        /// </summary>
        internal string AutoApproveTrayLabel()
        {
            switch (AutoApproveState)
            {
                case ApproveState.Able: return "Auto-approve: on";
                case ApproveState.CannotSee:
                    return _portAnswering
                        ? "Auto-approve: on, but cannot see the Claude Code panel"
                        : "Auto-approve: on, waiting for VS Code";
                default: return "Auto-approve: off";
            }
        }

        /// <summary>
        /// The last capability sentence written to the log, so only CHANGES get written.
        /// Null until the first poll, which is why the opening state is always recorded.
        /// </summary>
        private string _lastLoggedCapability;

        /// <summary>
        /// Write one line when the answer to "could this press a button right now?" changes.
        ///
        /// The log had no way to say this at all. Nothing is written when there is nothing to
        /// press -- deliberately, a line every ten seconds would bury the file -- but the
        /// consequence is that an inert module and a working one are BYTE-IDENTICAL in the log.
        /// On 2026-09-21 that turned "was auto-approve working overnight?" into an archaeology
        /// exercise over timestamp gaps in an unrelated subsystem, because silence was equally
        /// consistent with working-and-idle, a dead debug port, an unreadable panel, and no
        /// process at all.
        ///
        /// On CHANGE only, so it stays silent while nothing moves and the log stays readable.
        ///
        /// Keyed on the TRAY SENTENCE rather than on ApproveState, for two reasons. It is finer:
        /// ApproveState folds "waiting for VS Code" and "cannot see the panel" into one
        /// CannotSee, and those are different faults with different fixes, so a move between
        /// them must not read as no change. And it is the same string the tray shows, so the log
        /// and the menu cannot drift into describing one state two ways.
        ///
        /// What this does NOT do is notice the app being gone. A dead process writes nothing.
        /// What it buys there is that the newest line has a timestamp, so "when did it stop"
        /// stops being a question about other subsystems' logging habits.
        /// </summary>
        /// <summary>
        /// Self-test only: stop polling and wait for the poll Init already started.
        ///
        /// Init ENDS with OnTick, so a scan is in flight the moment Init returns -- and with no
        /// SynchronizationContext to post to, PostToUi runs the result INLINE on that worker, which
        /// writes the first capability line. Anything counting log lines straight after Init is
        /// therefore racing a background task it never started.
        ///
        /// That race is not theoretical and not symmetrical. It passed on the maintainer's machine
        /// every time, because the scan there reads thousands of real transcripts and loses; it
        /// failed on CI every time, because a runner has none and the scan finishes first. Green
        /// locally and red on the runner, which is the worst way to find out.
        /// </summary>
        internal void QuiesceForSelfTest()
        {
            if (_timer != null) _timer.Stop();
            for (int i = 0; i < 400 && Volatile.Read(ref _scanning) != 0; i++)
                System.Threading.Thread.Sleep(5);
        }

        /// <summary>Self-test only: forget what was last logged, so the next call records again.</summary>
        internal void ResetCapabilityLogForSelfTest() { _lastLoggedCapability = null; }

        private void LogCapabilityChange()
        {
            string now = AutoApproveTrayLabel();
            if (string.Equals(now, _lastLoggedCapability, StringComparison.Ordinal)) return;
            _lastLoggedCapability = now;
            Log(now);
        }

        /// <summary>Flip the user's intent from the tray, and say what happened out loud.</summary>
        internal void ToggleAutoApproveFromTray()
        {
            if (_settings == null) return;
            bool next = !AutoApprove;
            // Writes the MODE. It used to write the retired autoApprove boolean, which
            // still worked only for as long as no `mode` key existed: AgentMode.Migrate
            // prefers a stored mode over the legacy pair, so the first time the user
            // touched the pane the tray toggle would have gone silently inert. Found by
            // the self-test, not by using it.
            //
            // Turning it OFF lands on Notify rather than Off, because the tray row says
            // "Auto-approve", not "AgentFlow": switching off the pressing should not also
            // switch off the watching the user never asked to stop.
            _settings.Set(SettingMode, next ? AgentMode.AutoApprove : AgentMode.Notify);
            _settings.Save();
            Log("auto-approve turned " + (next ? "ON" : "OFF") + " from the tray");

            // Forget what the last probe said. Whatever was true before the switch moved is
            // not evidence about now: the port is only probed while auto-approve is ON, so a
            // true left over from an earlier spell would paint the tray GREEN the instant the
            // switch came back on, claiming a reachable editor nobody has checked for. Amber
            // until a probe earns it, which takes at most one tick.
            _portAnswering = false;
            _panelReadable = false;
            // The switch moving is the user saying "try again", which clears a stand-down.
            _resetPressBudget = true;

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
            // The MODE, for the same reason ToggleAutoApproveFromTray writes it: a stored
            // mode beats the legacy booleans in AgentMode.Migrate, so writing `enabled`
            // here worked only until the user pressed Apply on the pane once, after which
            // this row ticked and did nothing. The auto-approve row was fixed and this one
            // was not, which is what an audit is for.
            //
            // "Watching" restores Notify rather than whatever mode was last set, because
            // the row offers exactly two states and inventing a third from history would
            // make the tick mean something different depending on the past.
            _settings.Set(SettingMode, enabled ? AgentMode.Notify : AgentMode.Off);
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
                    probe.Check("WITNESS declares that it PRESSES things, and reaches a port",
                        module.Info.Permissions.HasFlag(ModulePermissions.InputSynthesis)
                        && module.Info.Permissions.HasFlag(ModulePermissions.Network));
                    probe.Check("WITNESS declares Companions, which the pet dropdown needs",
                        module.Info.Permissions.HasFlag(ModulePermissions.Companions));
                    probe.Check("declares no permission it does not use",
                        !module.Info.Permissions.HasFlag(ModulePermissions.ScreenContext)
                        && !module.Info.Permissions.HasFlag(ModulePermissions.Hotkey)
                        && !module.Info.Permissions.HasFlag(ModulePermissions.Voice));

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
                              && SelfCheckApprover(probe)
                              && SelfCheckPressBudget(probe)
                              && SelfCheckMode(probe)
                              && SelfCheckQuips(probe)
                              && SelfCheckApprovalFeed(probe)
                              && SelfCheckPetAnimations(probe)
                              && SelfCheckNotifyChannels(probe)
                              && SelfCheckLogPath(probe)
                              && SelfCheckAllProjects(probe)
                              && SelfCheckCapabilityLog(probe)
                              && SelfCheckPetChoices(probe)
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
            Dictionary<string, int> tally = BlockedDetector.ApprovedSince(session, rules, counted, null);

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
            Dictionary<string, int> again = BlockedDetector.ApprovedSince(session, rules, counted, null);
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
                BlockedDetector.ApprovedSince(pathy, pathRules, new HashSet<string>(StringComparer.Ordinal), null);
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
        /// <summary>Capability lines only: the tray toggle logs its own sentence, and counting
        /// that as a transition would hide a logger that never fires on its own.</summary>
        private static int CapabilityLines(DesktopAICompanion.ModuleKit.Testing.RecordingHost host)
        {
            int n = 0;
            foreach (string line in host.LoggedLines)
                if (line != null && line.IndexOf("Auto-approve:", StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        /// <summary>
        /// The log says when the ability to press CHANGES, and says nothing while it does not.
        ///
        /// Both halves are the feature. A logger that never fires leaves the original complaint
        /// intact -- an inert module and a working one writing identical logs -- and one that fires
        /// every poll buries the file this module exists to keep readable, which is why nothing was
        /// logged here in the first place.
        ///
        /// The port-up-panel-down step is the case that decided the implementation. It and
        /// "waiting for VS Code" are the SAME ApproveState, so keying the check on the enum would
        /// silently swallow a move between two different faults with two different fixes.
        /// </summary>
        private static bool SelfCheckCapabilityLog(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-cap"))
            {
                host.UseStorage("agentflow", storage);
                var module = new AgentFlowModule();
                module.Init(host);

                // Init ends with an immediate OnTick, so the opening state is already being
                // recorded by a worker as Init returns. Wait for it rather than assert around it:
                // the previous version of this checked for ZERO lines here, which was a claim
                // about scheduling rather than about behaviour, and it was simply false -- there
                // IS a first poll, inside Init.
                module.QuiesceForSelfTest();
                probe.Check("WITNESS Init's own poll records the opening state, rather than "
                            + "leaving the log silent until something happens to change",
                    CapabilityLines(host) == 1);

                // Back to a known baseline, so the counts below measure THIS sequence.
                host.LoggedLines.Clear();
                module.ResetCapabilityLogForSelfTest();

                module.LogCapabilityChange();
                probe.Check("WITNESS the opening state is recorded, so the log has a baseline",
                    CapabilityLines(host) == 1);

                // The half that keeps the log readable.
                module.LogCapabilityChange();
                module.LogCapabilityChange();
                probe.Check("WITNESS an unchanged state is written once, not once per poll",
                    CapabilityLines(host) == 1);

                module.ToggleAutoApproveFromTray();   // off -> on, nothing answering yet
                module.LogCapabilityChange();
                probe.Check("turning it on is a change, and is recorded",
                    CapabilityLines(host) == 2);
                probe.Check("...naming the fault as the editor, not the panel",
                    host.LoggedLines[host.LoggedLines.Count - 1]
                        .IndexOf("waiting for VS Code", StringComparison.Ordinal) >= 0);

                // Port up, panel still unreadable. SAME ApproveState as the line above.
                module._portAnswering = true;
                module.LogCapabilityChange();
                probe.Check("WITNESS a move between two faults sharing one state is still recorded",
                    CapabilityLines(host) == 3);
                probe.Check("...and now names the panel rather than the editor",
                    host.LoggedLines[host.LoggedLines.Count - 1]
                        .IndexOf("cannot see the Claude Code panel", StringComparison.Ordinal) >= 0);

                module._panelReadable = true;
                module.LogCapabilityChange();
                probe.Check("becoming able to press is recorded", CapabilityLines(host) == 4);

                module._panelReadable = false;
                module.LogCapabilityChange();
                probe.Check("WITNESS losing the panel is recorded too, not only gaining it",
                    CapabilityLines(host) == 5);

                module.Shutdown();
            }
            return true;
        }

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
                    pane.Load()[SettingMode] == AgentMode.ToDisplay(AgentMode.Notify));
                probe.Check("the tray says off, and the dot is the off one",
                    module.AutoApproveState == ApproveState.Off
                    && module.AutoApproveTrayLabel().IndexOf("off", StringComparison.Ordinal) >= 0);

                // On, with nothing answering: AMBER, and it has to SAY it cannot act yet.
                module.ToggleAutoApproveFromTray();
                probe.Check("the tray toggle turns it on", module.AutoApprove);
                probe.Check("WITNESS on-but-unreachable is the orange state, not the green one",
                    module.AutoApproveState == ApproveState.CannotSee
                    && module.AutoApproveTrayLabel().IndexOf("waiting for VS Code",
                        StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS the pane reports what the tray just did",
                    pane.Load()[SettingMode] == AgentMode.ToDisplay(AgentMode.AutoApprove));

                // The port answering is NOT enough on its own, and this is the assertion
                // that says so: the module spent an afternoon with a live port and a panel
                // it could not read, reporting itself healthy the whole time.
                module._portAnswering = true;
                probe.Check("WITNESS a live port with an unreadable panel is still orange",
                    module.AutoApproveState == ApproveState.CannotSee
                    && module.AutoApproveTrayLabel().IndexOf("cannot see",
                        StringComparison.Ordinal) >= 0);
                module._panelReadable = true;
                probe.Check("WITNESS green means the port answered AND the panel was read",
                    module.AutoApproveState == ApproveState.Able);
                probe.Check("the three states get three different dots",
                    !SameBytes(StatusDot.For(ApproveState.Off), StatusDot.For(ApproveState.CannotSee))
                    && !SameBytes(StatusDot.For(ApproveState.CannotSee), StatusDot.For(ApproveState.Able))
                    && !SameBytes(StatusDot.For(ApproveState.Off), StatusDot.For(ApproveState.Able)));
                probe.Check("WITNESS the dot is cached, not redrawn on every menu open",
                    ReferenceEquals(StatusDot.For(ApproveState.Able),
                                    StatusDot.For(ApproveState.Able)));
                module._portAnswering = false;
                module._panelReadable = false;

                // The other direction: the pane must be able to switch it off again.
                pane.Save(new Dictionary<string, string>
                    { { SettingMode, AgentMode.ToDisplay(AgentMode.Notify) } });
                probe.Check("WITNESS saving the pane switches it off",
                    !module.AutoApprove);
                probe.Check("...and the tray goes back to the off state",
                    module.AutoApproveState == ApproveState.Off);

                // Turning it back ON must not inherit the green it had before. The port is
                // only probed while the switch is on, so a leftover true is not evidence --
                // and this is the one path where the tray could claim a capability nobody
                // ever checked for.
                module._portAnswering = true;
                module._panelReadable = true;
                module.ToggleAutoApproveFromTray();
                // BOTH flags, individually. Asserting only the resulting state was too weak: the
                // toggle clears two things, so disabling either clear on its own still left the
                // state correct and a mutation survived. What is being claimed is that nothing
                // learned before the switch moved survives it.
                probe.Check("WITNESS switching it on discards the last probe, so green is earned",
                    !module._portAnswering && !module._panelReadable
                    && module.AutoApproveState == ApproveState.CannotSee);
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

                // The OTHER tray row. It wrote the retired `enabled` boolean, which a stored
                // mode overrides, so "Watching / Off" ticked and did nothing the moment the
                // user pressed Apply once. The auto-approve row had already been fixed; this
                // one was missed, and only an audit comparing the two caught it.
                module.SetEnabledFromTray(false);
                probe.Check("WITNESS turning watching off from the tray actually stops it",
                    !module.Enabled && module.Mode == AgentMode.Off);
                module.SetEnabledFromTray(true);
                probe.Check("...and turning it back on resumes watching",
                    module.Enabled && module.Mode == AgentMode.Notify);
                probe.Check("the tray row shows the same state the helper reports",
                    row != null && row.Label == module.AutoApproveTrayLabel());
                probe.Check("...in its own group, so pressing is separated from watching",
                    row != null && row.Group != 0 && row.Click != null);

                module.Shutdown();
            }
            return true;
        }

        /// <summary>
        /// The approve half, minus the sockets.
        ///
        /// Two things are asserted here and the rest of the file cannot be, without a running
        /// editor showing a real prompt: that the reader turns a CDP reply into a view without
        /// trusting its shape, and that the decision refuses everything it is supposed to
        /// refuse. The press path is exercised against a closed port, which is enough to prove
        /// it composes -- it is NOT evidence that a real prompt is ever found, and nothing here
        /// should be read as though it were.
        /// </summary>
        private static bool SelfCheckApprover(SelfTestProbe probe)
        {
            // ---- reading -------------------------------------------------------------
            PromptView view = CdpApprover.Parse("t1",
                "{\"tool\":\"Bash\",\"options\":[\"Yes\",\"Yes, and don't ask again\",\"No\"],"
                + "\"disabled\":[false,false,false]}");
            probe.Check("reads the options off a CDP reply",
                view != null && view.Options.Count == 3 && view.ToolName == "Bash");

            // A short disabled list must not make a row look enabled by omission, and must not
            // throw on an index lookup either.
            PromptView padded = CdpApprover.Parse("t1",
                "{\"options\":[\"Yes\",\"No\"],\"disabled\":[true]}");
            probe.Check("WITNESS a short disabled list is padded, not left to throw",
                padded != null && padded.Disabled.Count == padded.Options.Count
                && padded.Disabled[0] && !padded.Disabled[1]);

            probe.Check("malformed JSON is no answer, not a crash",
                CdpApprover.Parse("t1", "{not json") == null);
            probe.Check("a reply with no options array is no answer",
                CdpApprover.Parse("t1", "{\"tool\":\"Bash\"}") == null);
            probe.Check("a reply that is not an object is no answer",
                CdpApprover.Parse("t1", "[1,2,3]") == null);

            // ---- "no prompt" must not be the same answer as "cannot see" -------------
            // This is the assertion the module did not have on 2026-09-18, and its absence
            // cost an afternoon. The reader queried the OUTER webview document, which holds
            // eight elements and a nested iframe, so it reported "no prompt" with a prompt
            // plainly on screen -- and "no prompt" is what a healthy idle module says, so
            // nothing looked wrong. Four outcomes, all distinct, asserted by name.
            // Asserting the VALUE, not merely that the two differ. The inequality form passed a
            // mutation that deleted the Unreachable branch entirely -- "unreachable" then fell
            // through to Prompt, which is still different from NoPrompt and still wrong. The
            // mutation harness caught that this assertion was weaker than its own name.
            probe.Check("WITNESS an unreachable panel is not reported as no prompt",
                CdpApprover.Interpret("unreachable") == ReadOutcome.Unreachable
                && CdpApprover.Interpret("none") == ReadOutcome.NoPrompt);
            probe.Check("the four read outcomes are told apart",
                CdpApprover.Interpret(null) == ReadOutcome.NoAnswer
                && CdpApprover.Interpret("unreachable") == ReadOutcome.Unreachable
                && CdpApprover.Interpret("none") == ReadOutcome.NoPrompt
                && CdpApprover.Interpret("{\"options\":[]}") == ReadOutcome.Prompt);

            // ...and the expression has to LOOK in the nested frame, in that order: decide
            // reachability first, because a container query against the outer shell can only
            // ever answer no.
            string expression = CdpApprover.ReadExpressionForSelfTest;
            int descends = expression.IndexOf("contentDocument", StringComparison.Ordinal);
            int gaveUp = expression.IndexOf("unreachable", StringComparison.Ordinal);
            int queried = expression.IndexOf("permissionRequestContainer",
                                             StringComparison.Ordinal);
            probe.Check("WITNESS the reader descends into the nested webview frame",
                descends > 0);
            probe.Check("WITNESS it settles reachability BEFORE it looks for a prompt",
                gaveUp > 0 && queried > 0 && gaveUp < queried && descends < queried);

            // ---- deciding ------------------------------------------------------------
            // Port 1 is closed, so the press path returns without reaching an editor. Every
            // case below that REFUSES never gets that far in the first place.
            const int ClosedPort = 1;

            var budget = new PressBudget();
            string unknown = Decide(ClosedPort, Fake(
                new[] { "Yes", "Do the thing I have not heard of" }), budget, false);
            probe.Check("WITNESS one unrecognised option refuses the whole prompt",
                unknown != null && unknown.StartsWith("refused:", StringComparison.Ordinal));
            // The refusal is the line that gets LOGGED, and the diagnostic log is meant to be
            // attachable to a public issue. An option nobody anticipated can say anything.
            probe.Check("WITNESS the refusal does not quote what it read",
                unknown != null
                && unknown.IndexOf("Do the thing", StringComparison.Ordinal) < 0);

            string noApprove = Decide(ClosedPort, Fake(
                new[] { "Yes, and don't ask again", "Yes, and auto-accept" }), budget, false);
            probe.Check("WITNESS a prompt offering only wider grants is refused",
                noApprove != null && noApprove.IndexOf("no approve-once",
                    StringComparison.Ordinal) >= 0);

            string ambiguous = Decide(ClosedPort, Fake(new[] { "Yes", "Allow" }), budget, false);
            probe.Check("WITNESS two approve-once rows is ambiguous, so nothing is pressed",
                ambiguous != null && ambiguous.IndexOf("ambiguous",
                    StringComparison.Ordinal) >= 0);

            PromptView greyed = Fake(new[] { "Yes", "Yes, and don't ask again" });
            greyed.Disabled[0] = true;
            string disabled = Decide(ClosedPort, greyed, budget, false);
            probe.Check("WITNESS a greyed-out approve row is not pressed",
                disabled != null && disabled.IndexOf("disabled",
                    StringComparison.Ordinal) >= 0);

            // The press path. Against a closed port the click cannot land, and the note says
            // so rather than claiming success -- which is the property that matters, because a
            // note reading "approved" when nothing was pressed would be the log line that
            // cannot fail.
            string pressed = Decide(ClosedPort, Fake(
                new[] { "Yes", "Yes, and don't ask again", "No, and tell Claude what to do differently" }), budget, false);
            probe.Check("WITNESS an unreachable editor is reported, not called success",
                pressed != null && pressed.IndexOf("gone", StringComparison.Ordinal) >= 0
                && pressed.IndexOf("approve-once", StringComparison.Ordinal) >= 0);

            // ---- what got approved, said safely --------------------------------------
            // The first real prompt this module ever pressed logged itself as "an unnamed
            // tool", because four of the five prompt shapes render their own header with no
            // <strong> in it. These assert the replacement, and that it cannot leak a path.
            probe.Check("an edit prompt is named as an edit, with the kind of file",
                DescribeSubject(Header("make this edit to ?", "ps1")) == "an edit (.ps1)");
            probe.Check("the other three header shapes are named too",
                DescribeSubject(Header("allow reading from ?", "md")) == "a file read (.md)"
                && DescribeSubject(Header("allow write to ?", "")) == "a file write"
                && DescribeSubject(Header("use skill ?", "")) == "a skill");
            probe.Check("a tool named in the header still wins, and is not overridden",
                DescribeSubject(Tool("Bash")) == "Bash");

            // The allowlist half: a header shape nobody has seen must not be echoed.
            PromptView novel = Header("Grant everlasting access to the thing at ?", "txt");
            probe.Check("WITNESS an unrecognised header is named, not quoted",
                DescribeSubject(novel) == "an unrecognised prompt");
            probe.Check("WITNESS ...so none of its text reaches the line that gets logged",
                DescribeSubject(novel).IndexOf("everlasting", StringComparison.Ordinal) < 0);

            // The extension is the ONLY thing taken from the path span, and the span holds a
            // full absolute path on Windows: upstream takes the leaf with split("/"), which
            // splits nothing here. Anything not shaped like an extension is dropped whole.
            // Both guards, separately. The first version of this assertion used three inputs that
            // were all LONGER than the cap, so every one of them was rejected on length and the
            // character filter was never exercised at all -- a mutation that made it return the
            // input verbatim survived. The short cases below are the ones that reach it.
            probe.Check("WITNESS a long path in the extension slot is dropped, not trimmed",
                SafeExtension("\\server\\share\\secret.txt") == ""
                && SafeExtension("D:/work/plan.md") == "");
            probe.Check("WITNESS a SHORT value with anything but letters and digits is dropped",
                SafeExtension("a/b") == "" && SafeExtension(".ps1") == ""
                && SafeExtension("p s1") == "" && SafeExtension("a\\b") == "");
            probe.Check("...while a real extension survives, lowercased",
                SafeExtension("PS1") == "ps1" && SafeExtension("cs") == "cs");
            probe.Check("an absurd extension is dropped rather than truncated",
                SafeExtension("abcdefghij") == "");

            // End to end: the whole logged line, for the prompt that actually happened.
            PromptView edit = Header("make this edit to ?", "ps1");
            edit.Options.Add("Yes");
            edit.Options.Add("No");
            edit.Disabled.Add(false);
            edit.Disabled.Add(false);
            string line = Decide(ClosedPort, edit, new PressBudget(), false);
            probe.Check("WITNESS the audit line says WHAT was approved",
                line != null && line.IndexOf("an edit (.ps1)", StringComparison.Ordinal) >= 0);
            probe.Check("...and still carries no path",
                line != null && line.IndexOf(":\\", StringComparison.Ordinal) < 0
                && line.IndexOf("/", StringComparison.Ordinal) < 0);

            // ---- the tool name, which is text off the screen --------------------------
            probe.Check("an ordinary tool name passes through", SafeToolName("Bash") == "Bash");
            probe.Check("WITNESS a tool name carrying anything else is not logged verbatim",
                SafeToolName("Bash(rm -rf /home/someone)") == "an unrecognised tool"
                && SafeToolName("C:\\Users\\someone\\x") == "an unrecognised tool");
            probe.Check("an empty tool name is named, not blank",
                SafeToolName("") == "an unnamed tool");

            // The click re-check embeds a label read off the screen into an expression. A
            // label that broke out of its quotes would be script running in the renderer.
            probe.Check("WITNESS an option label cannot break out of the click expression",
                CdpApprover.JsonEncode("a\" + alert(1) + \"b")
                    .IndexOf("alert(1)", StringComparison.Ordinal) > 0
                && CdpApprover.JsonEncode("a\" + alert(1) + \"b").StartsWith("\"",
                    StringComparison.Ordinal)
                && CountUnescapedQuotes(CdpApprover.JsonEncode("a\" + alert(1) + \"b")) == 2);
            return true;
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null) return left == right;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        /// <summary>A prompt whose header names no tool, the way four of the five shapes do.</summary>
        private static PromptView Header(string header, string extension)
        {
            return new PromptView
            {
                TargetId = "t1", ToolName = "", UnsafeHeader = header, PathExtension = extension,
            };
        }

        /// <summary>A prompt that does name its tool, the way the generic shape does.</summary>
        private static PromptView Tool(string tool)
        {
            return new PromptView { TargetId = "t1", ToolName = tool };
        }

        /// <summary>A prompt view with no editor behind it, for the assertions above.</summary>
        private static PromptView Fake(string[] options)
        {
            var view = new PromptView { TargetId = "t1", ToolName = "Bash" };
            foreach (string option in options) { view.Options.Add(option); view.Disabled.Add(false); }
            return view;
        }

        /// <summary>Quotes not preceded by a backslash: a JSON string should have exactly two.</summary>
        private static int CountUnescapedQuotes(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '"' && (i == 0 || text[i - 1] != '\\')) count++;
            return count;
        }
        /// <summary>
        /// The press budget: the only thing standing between a click that stops landing and a
        /// machine that approved everything asked of it overnight.
        ///
        /// Time is a parameter, so all of this is real coverage rather than a sleep. The two
        /// limits are asserted at their boundaries in BOTH directions -- allowed up to the cap,
        /// refused past it -- because an off-by-one in the permissive direction is the whole
        /// failure this guard exists to prevent and would look identical to working.
        /// </summary>
        private static bool SelfCheckPressBudget(SelfTestProbe probe)
        {
            DateTime t0 = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
            string refusal;

            // ---- the repeat limit, which is the one that catches a broken click ------
            var budget = new PressBudget();
            string same = PressBudget.Signature("Bash", new[] { "Yes", "No" });
            int allowed = 0;
            for (int i = 0; i < PressBudget.MaxIdenticalPresses; i++)
                if (budget.TryPress(same, t0.AddSeconds(i * 10), out refusal)) allowed++;
            probe.Check("the same prompt may be pressed up to the repeat cap",
                allowed == PressBudget.MaxIdenticalPresses);
            probe.Check("WITNESS the same prompt again past the cap is refused",
                !budget.TryPress(same, t0.AddSeconds(40), out refusal)
                && refusal != null
                && refusal.IndexOf("standing down", StringComparison.Ordinal) >= 0);

            // ...and a DIFFERENT prompt is not the loop, so it goes through.
            probe.Check("WITNESS a different prompt is not the loop and is still pressed",
                budget.TryPress(PromptSignature("Edit"), t0.AddSeconds(50), out refusal));

            // The prompt going away is what success looks like; it clears the suspicion.
            var cleared = new PressBudget();
            for (int i = 0; i < PressBudget.MaxIdenticalPresses; i++)
            {
                cleared.TryPress(same, t0.AddSeconds(i), out refusal);
                cleared.NotePromptCleared();
            }
            probe.Check("WITNESS a prompt that goes away each time never trips the repeat cap",
                cleared.TryPress(same, t0.AddSeconds(30), out refusal));

            // ---- the rate limit, the backstop ----------------------------------------
            var rate = new PressBudget();
            int pressed = 0;
            for (int i = 0; i < PressBudget.MaxPressesPerWindow + 5; i++)
            {
                // A fresh signature each time, so ONLY the rate limit can stop this.
                if (rate.TryPress(PromptSignature("tool" + i.ToString(CultureInfo.InvariantCulture)),
                                  t0.AddSeconds(i), out refusal))
                    pressed++;
            }
            probe.Check("WITNESS the rate cap stops an unattended run of distinct prompts",
                pressed == PressBudget.MaxPressesPerWindow);

            // ...and it is a WINDOW, not a lifetime total: past it, work resumes.
            probe.Check("WITNESS the rate cap expires rather than latching forever",
                rate.TryPress(PromptSignature("later"),
                    t0.Add(PressBudget.Window).AddMinutes(1), out refusal));

            // ---- the switch is the way out -------------------------------------------
            var stuck = new PressBudget();
            for (int i = 0; i < PressBudget.MaxIdenticalPresses + 2; i++)
                stuck.TryPress(same, t0.AddSeconds(i), out refusal);
            probe.Check("a stood-down budget stays down", !stuck.TryPress(same, t0.AddSeconds(99), out refusal));
            stuck.Reset();
            probe.Check("WITNESS switching auto-approve off and on clears a stand-down",
                stuck.TryPress(same, t0.AddSeconds(100), out refusal));

            // ---- what counts as the same prompt --------------------------------------
            // Keying on the tool alone would read an ordinary run of shell commands as one
            // prompt repeating, and stand down in the middle of normal work.
            probe.Check("WITNESS two Bash prompts with different options are different prompts",
                PressBudget.Signature("Bash", new[] { "Yes", "Yes, allow Bash(npm test)" })
                != PressBudget.Signature("Bash", new[] { "Yes", "Yes, allow Bash(npm run lint)" }));
            probe.Check("...and the identical prompt is the same prompt",
                PressBudget.Signature("Bash", new[] { "Yes", "No" })
                == PressBudget.Signature("Bash", new[] { "Yes", "No" }));

            // ---- and it has to be WIRED IN, not merely correct -----------------------
            // Everything above passes just as well when Decide never calls it. This is the
            // assertion that fails if the guard is bypassed, which is the only way it ever
            // gets bypassed: by someone deleting four lines that looked defensive.
            var spent = new PressBudget();
            for (int i = 0; i < PressBudget.MaxPressesPerWindow; i++)
                spent.TryPress(PromptSignature("t" + i.ToString(CultureInfo.InvariantCulture)),
                               DateTime.UtcNow, out refusal);
            string blocked = Decide(1, Fake(new[] { "Yes", "Yes, and don't ask again" }), spent, false);
            probe.Check("WITNESS Decide consults the budget before it presses anything",
                blocked != null
                && blocked.IndexOf("standing down", StringComparison.Ordinal) >= 0);
            return true;
        }

        private static string PromptSignature(string tool)
        {
            return PressBudget.Signature(tool, new[] { "Yes", "No" });
        }
        /// <summary>
        /// The mode, and the migration onto it.
        ///
        /// The migration is the half that matters. AgentFlow is published, so the two legacy
        /// booleans are on real installs, and this function is the only thing between an
        /// upgrade and a module that silently reverts to defaults on somebody else's machine.
        /// </summary>
        private static bool SelfCheckMode(SelfTestProbe probe)
        {
            probe.Check("a stored mode is used as-is",
                AgentMode.Migrate(AgentMode.Log, false, false) == AgentMode.Log
                && AgentMode.Migrate(AgentMode.Off, true, true) == AgentMode.Off);

            // WITNESS: the three legacy shapes, which is every combination a 1.0.x install
            // can actually be in. A recognised new value always wins over them.
            probe.Check("WITNESS a disabled 1.0.x install migrates to Off, not to a default",
                AgentMode.Migrate("", false, false) == AgentMode.Off
                && AgentMode.Migrate("", false, true) == AgentMode.Off);
            probe.Check("WITNESS an approving install keeps approving",
                AgentMode.Migrate("", true, true) == AgentMode.AutoApprove);
            probe.Check("WITNESS a plain watching install becomes Notify",
                AgentMode.Migrate("", true, false) == AgentMode.Notify);
            probe.Check("an unrecognised stored value falls back to the booleans",
                AgentMode.Migrate("wat", true, false) == AgentMode.Notify);
            // Nobody is silently moved into a mode that did not exist before.
            probe.Check("WITNESS no legacy state migrates into Log",
                AgentMode.Migrate("", true, true) != AgentMode.Log
                && AgentMode.Migrate("", true, false) != AgentMode.Log
                && AgentMode.Migrate("", false, false) != AgentMode.Log);

            probe.Check("only auto-approve presses anything",
                AgentMode.Presses(AgentMode.AutoApprove)
                && !AgentMode.Presses(AgentMode.Notify)
                && !AgentMode.Presses(AgentMode.Log) && !AgentMode.Presses(AgentMode.Off));
            // Auto-approve still speaks, about what it REFUSED. A prompt it will not touch
            // sits there forever, and silence about it is the failure the notify half exists
            // to prevent.
            probe.Check("WITNESS auto-approve still speaks, so a refusal is not silent",
                AgentMode.Speaks(AgentMode.AutoApprove));
            probe.Check("Log is the quiet one, and Off does not even scan",
                !AgentMode.Speaks(AgentMode.Log) && AgentMode.Scans(AgentMode.Log)
                && !AgentMode.Scans(AgentMode.Off));
            probe.Check("every mode has a label and round-trips through it",
                AgentMode.Displays().Length == AgentMode.All.Length
                && AgentMode.FromDisplay(AgentMode.ToDisplay(AgentMode.Log)) == AgentMode.Log
                && AgentMode.FromDisplay(AgentMode.ToDisplay(AgentMode.Off)) == AgentMode.Off);
            return true;
        }

        /// <summary>
        /// The quips. Asserted at the TABLE level, because the properties that matter are
        /// properties of all thirty-six at once and no amount of sampling Next() finds them.
        /// </summary>
        private static bool SelfCheckQuips(SelfTestProbe probe)
        {
            probe.Check("there are three dozen of them, give or take",
                QuipPicker.Count >= 30);

            // The privacy property, and the only one that could leak. A quip that USES
            // {project} without declaring it would be picked for a session with no project
            // and render the token raw; worse, any placeholder nobody vetted could be added
            // later and filled from anywhere. Declared-vs-used is checked both ways.
            int undeclaredProject = 0, undeclaredTool = 0, unknownToken = 0, unfillable = 0;
            foreach (Quip quip in QuipPicker.Table)
            {
                bool usesProject = quip.Text.IndexOf(QuipPicker.ProjectToken,
                    StringComparison.Ordinal) >= 0;
                bool usesTool = quip.Text.IndexOf(QuipPicker.ToolToken,
                    StringComparison.Ordinal) >= 0;
                if (usesProject != quip.NeedsProject) undeclaredProject++;
                if (usesTool != quip.NeedsTool) undeclaredTool++;

                // Any brace at all that is not one of the three known tokens.
                string stripped = quip.Text
                    .Replace(QuipPicker.ProjectToken, "").Replace(QuipPicker.ToolToken, "")
                    .Replace(QuipPicker.DurationToken, "");
                if (stripped.IndexOf('{') >= 0 || stripped.IndexOf('}') >= 0) unknownToken++;

                if (QuipPicker.Fill(quip.Text, "p", "t", "9m").IndexOf('{',
                        StringComparison.Ordinal) >= 0) unfillable++;
            }
            probe.Check("WITNESS every quip declares exactly the placeholders it uses",
                undeclaredProject == 0 && undeclaredTool == 0);
            probe.Check("WITNESS no quip carries a placeholder nobody vetted",
                unknownToken == 0);
            probe.Check("...and every quip fills completely, leaving no raw token on screen",
                unfillable == 0);

            // The floor. Without an unconditional pool, a session whose project and tool are
            // both unknown gets silence instead of a notice.
            int unconditional = 0;
            foreach (Quip quip in QuipPicker.Table)
                if (!quip.NeedsProject && !quip.NeedsTool) unconditional++;
            probe.Check("WITNESS there is always a pool, even knowing neither project nor tool",
                unconditional >= 5);

            var picker = new QuipPicker(1234);
            probe.Check("a line with neither known still comes back, fully filled",
                IsFilled(picker.Next(null, null, "45s")));
            probe.Check("a line with both known comes back too",
                IsFilled(picker.Next("myproject", "Bash", "9m")));

            // WITNESS: never the same line twice running. The notify budget lets one notice
            // through every couple of minutes, so consecutive draws are the ones a user hears
            // back to back -- and that is the difference between a pet and an alarm.
            var norepeat = new QuipPicker(7);
            string previous = null;
            int repeats = 0;
            for (int i = 0; i < 200; i++)
            {
                string line = norepeat.Next(null, null, "1m");
                if (previous != null && line == previous) repeats++;
                previous = line;
            }
            probe.Check("WITNESS it never says the same thing twice in a row", repeats == 0);

            // ...and it does not just cycle two of them either.
            var spread = new QuipPicker(99);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 200; i++) seen.Add(spread.Next(null, null, "1m"));
            probe.Check("...and it uses the pool rather than alternating two lines",
                seen.Count >= 8);

            // A quip needing a project must never be handed one that is missing.
            var poolOnly = new QuipPicker(3);
            bool leaked = false;
            for (int i = 0; i < 200; i++)
            {
                string line = poolOnly.Next("", "", "30s");
                if (line == null || line.Trim().Length == 0) { leaked = true; break; }
                // An eligible-pool bug shows up as a double space or a stray colon where the
                // empty value was substituted in.
                if (line.IndexOf("  ", StringComparison.Ordinal) >= 0) { leaked = true; break; }
            }
            probe.Check("WITNESS an unknown project never leaves a hole in the sentence",
                !leaked);
            return true;
        }

        private static bool IsFilled(string line)
        {
            return !string.IsNullOrEmpty(line)
                   && line.IndexOf('{') < 0 && line.IndexOf('}') < 0;
        }
        /// <summary>
        /// The approvals feed: the one place in this module that keeps command text.
        ///
        /// Every assertion here is about CONTAINMENT rather than about the list working. The
        /// module's standing rule is that a command never leaves TranscriptReader, and this
        /// is the first thing that holds one, so what has to be proven is that the pane can
        /// see it and nothing else can.
        /// </summary>
        private static bool SelfCheckApprovalFeed(SelfTestProbe probe)
        {
            var feed = new ApprovalFeed();
            probe.Check("an empty feed says so rather than rendering blank",
                feed.Render().IndexOf("Nothing approved", StringComparison.Ordinal) >= 0);

            DateTime t0 = new DateTime(2026, 9, 18, 13, 42, 0, DateTimeKind.Local);
            for (int i = 0; i < ApprovalFeed.Cap + 7; i++)
                feed.Record(t0.AddMinutes(i), "git",
                    "git commit -m " + i.ToString(CultureInfo.InvariantCulture));
            probe.Check("WITNESS the feed is bounded, so it cannot grow for the life of the app",
                feed.Count == ApprovalFeed.Cap);

            IReadOnlyList<ApprovalEntry> recent = feed.Recent();
            probe.Check("WITNESS the newest approval is first, and the oldest fell off",
                recent.Count == ApprovalFeed.Cap
                && recent[0].Command.EndsWith("16", StringComparison.Ordinal)
                && recent[recent.Count - 1].Command.EndsWith("7", StringComparison.Ordinal));

            // The pane IS allowed the command. That is the whole point of the card: a list of
            // bare executables cannot tell two git calls apart, and reviewing approvals is
            // what an approval module is for.
            probe.Check("WITNESS the pane can see the command, which is why the card exists",
                feed.Render().IndexOf("git commit -m 16", StringComparison.Ordinal) >= 0);

            // ...and a single pasted script must not push the other nine off the card.
            var wide = new ApprovalFeed();
            wide.Record(t0, "bash", "line one\nline two\nline three");
            probe.Check("WITNESS a multi-line command is flattened to one row",
                wide.Render().Split(new[] { '\n' }).Length == 1);
            var longOne = new ApprovalFeed();
            longOne.Record(t0, "bash", new string('x', 400));
            probe.Check("WITNESS a very long command is capped, and says it was cut",
                longOne.Render().Length < 200
                && longOne.Render().IndexOf('\u2026') > 0);

            // ---- the containment ------------------------------------------------------
            // A real scan, through the real detector, against a transcript carrying a command
            // that is recognisable if it ever escapes.
            const string Secret = "curl -H authorization:token-abc123 https://example.invalid";
            var collected = new List<ApprovalEntry>();
            AgentSession session = OneApprovedCall(Secret);
            var rules = new RuleSet();
            rules.Allow.Add("Bash(curl *)");
            Dictionary<string, int> tally = BlockedDetector.ApprovedSince(
                session, rules, new HashSet<string>(StringComparer.Ordinal), collected);

            probe.Check("the collector received the approved call",
                collected.Count == 1 && collected[0].Command == Secret);
            // WITNESS: the TALLY -- which is what gets logged -- carries the executable only.
            bool tallyClean = true;
            foreach (string key in tally.Keys)
                if (key.IndexOf("token-abc123", StringComparison.Ordinal) >= 0) tallyClean = false;
            probe.Check("WITNESS the tally the log is built from carries no command text",
                tallyClean);
            string line = BlockedDetector.DescribeApprovals(tally, 8);
            probe.Check("WITNESS the logged approval line carries no command text",
                line != null && line.IndexOf("token-abc123", StringComparison.Ordinal) < 0
                && line.IndexOf("curl -H", StringComparison.Ordinal) < 0);

            // ...and passing no collector keeps the old behaviour exactly: nothing is kept.
            var none = new List<ApprovalEntry>();
            BlockedDetector.ApprovedSince(OneApprovedCall(Secret), rules,
                new HashSet<string>(StringComparer.Ordinal), null);
            probe.Check("WITNESS a caller that asks for nothing is handed nothing",
                none.Count == 0);
            return true;
        }

        /// <summary>A session with exactly one completed call carrying the given command.</summary>
        private static AgentSession OneApprovedCall(string command)
        {
            var session = new AgentSession { SessionId = "feed-test", Cwd = "D:\\work\\proj" };
            session.Completed.Add(new OutstandingCall
            {
                Id = "call-1",
                Tool = "Bash",
                Command = command,
            });
            return session;
        }
        /// <summary>
        /// Reading a pet's own animation names out of its XML.
        ///
        /// This exists because there is no list that works for every pet: measured across the
        /// 53 bundled companions, 709 distinct names and an EMPTY intersection. The four that
        /// come closest are the engine's reserved lifecycle animations, so the "safe" list is
        /// also the one you must not offer.
        /// </summary>
        private static bool SelfCheckPetAnimations(SelfTestProbe probe)
        {
            const string Xml =
                "<animations><animations>"
                + "<animation><name>walk</name></animation>"
                + "<animation><name>sit</name></animation>"
                + "<animation><name>walk_top_corner</name></animation>"
                + "<animation><name>walk_top_corner</name></animation>"
                + "<animation><name></name></animation>"
                + "<animation><id>7</id></animation>"
                + "</animations></animations>";
            List<string> names = PetAnimations.FromXml(Xml);

            probe.Check("reads the names a pet declares",
                names.Contains("walk") && names.Contains("sit"));
            // The seven sheep recolours each declare walk_top_corner TWICE and the engine takes
            // the first match, so listing it twice offers a choice that does not exist.
            probe.Check("WITNESS a duplicated animation is offered once, not twice",
                names.FindAll(delegate(string n) { return n == "walk_top_corner"; }).Count == 1);
            // The schema validates name LENGTH only, 0 to 128, so an empty name is legal XML
            // and would render as a blank dropdown row.
            probe.Check("WITNESS an empty name is legal XML and is skipped anyway",
                !names.Contains("") && names.Count == 3);
            probe.Check("an animation with no name element is skipped",
                names.Count == 3);
            probe.Check("the list is sorted, so the dropdown is not in file order",
                names[0] == "sit");

            probe.Check("WITNESS unreadable pet XML contributes nothing and does not throw",
                PetAnimations.FromXml("<animations><not closed").Count == 0
                && PetAnimations.FromXml("").Count == 0
                && PetAnimations.FromXml(null).Count == 0);

            // The chosen name leads, then the coverage fallbacks -- so a pet that has been
            // swapped since the choice was made degrades instead of doing nothing.
            IReadOnlyList<string> candidates = PetAnimations.Candidates("shimeji-cyn", "wave");
            probe.Check("WITNESS the chosen animation is tried first",
                candidates.Count > 1 && candidates[0] == "wave");
            probe.Check("...and a fallback follows it, so a swapped pet still animates",
                candidates.Count > 1);
            probe.Check("choosing no pet still yields the coverage list",
                PetAnimations.Candidates(PetAnimations.AnyPet, PetAnimations.AnyPet).Count > 1);

            // WITNESS the defect this replaces. The shipped list was boing,jump,run: boing is
            // on ONE of 53 pets, so "Play an animation" was a silent no-op on nineteen of them.
            bool stillBoing = false;
            foreach (string candidate in PetAnimations.AnyPetCandidates)
                if (candidate == "boing") stillBoing = true;
            probe.Check("WITNESS the any-pet list no longer leads with a one-pet animation",
                !stillBoing && PetAnimations.AnyPetCandidates[0] == "walk");
            return true;
        }
        /// <summary>
        /// Sound, speech and animation are THREE switches, not one.
        ///
        /// They used to be one: the module spoke, and a single "animate as well" checkbox
        /// added a wiggle. A user who wants a chime and no chatter could not have it. Each is
        /// asserted on and off independently, because "all three fire together" passes a test
        /// that only ever turns them all on.
        /// </summary>
        private static bool SelfCheckNotifyChannels(SelfTestProbe probe)
        {
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ch"))
            {
                // Speech only.
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                host.UseStorage("agentflow", storage);
                var module = new AgentFlowModule();
                // The double REFUSES an undeclared module now, exactly as CompanionHost
                // does. Without this line the sound assertion below passed while the real
                // host returned false on every call.
                host.Declared = module.Info.Permissions;
                module.Init(host);
                module._settings.Set(SettingNotifySpeak, "true");
                module._settings.Set(SettingNotifySound, "false");
                module._settings.Set(SettingAnimate, "false");
                module._settings.Save();
                host.RaiseCompanionSpawned(new FakeCompanion());
                module.Apply(OneBlockedDetection());
                probe.Check("WITNESS speech alone speaks and makes no sound",
                    host.BroadcastLines.Count == 1 && host.NotificationSoundsPlayed == 0);
                module.Shutdown();

                // Sound only. A fresh storage each time, because the notify budget remembers
                // what it already announced and would suppress the second notice otherwise.
                using (var storage2 =
                           new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ch2"))
                {
                    var host2 = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host2.UseStorage("agentflow", storage2);
                    var module2 = new AgentFlowModule();
                    // The double REFUSES an undeclared module now, exactly as CompanionHost
                    // does. Without this line the sound assertion below passed while the real
                    // host returned false on every call.
                    host2.Declared = module2.Info.Permissions;
                    module2.Init(host2);
                    module2._settings.Set(SettingNotifySpeak, "false");
                    module2._settings.Set(SettingNotifySound, "true");
                    module2._settings.Set(SettingAnimate, "false");
                    module2._settings.Save();
                    host2.RaiseCompanionSpawned(new FakeCompanion());
                    module2.Apply(OneBlockedDetection());
                    probe.Check("WITNESS a chime with no chatter is possible, which it was not before",
                        host2.BroadcastLines.Count == 0 && host2.NotificationSoundsPlayed == 1);
                    probe.Check("WITNESS ...and it only works because Audio is declared",
                        module2.Info.Permissions.HasFlag(ModulePermissions.Audio));
                    module2.Shutdown();
                }

                // Animation only, and NOT with the old hardcoded list.
                using (var storage3 =
                           new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ch3"))
                {
                    var host3 = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host3.UseStorage("agentflow", storage3);
                    var module3 = new AgentFlowModule();
                    // The double REFUSES an undeclared module now, exactly as CompanionHost
                    // does. Without this line the sound assertion below passed while the real
                    // host returned false on every call.
                    host3.Declared = module3.Info.Permissions;
                    module3.Init(host3);
                    module3._settings.Set(SettingNotifySpeak, "false");
                    module3._settings.Set(SettingNotifySound, "false");
                    module3._settings.Set(SettingAnimate, "true");
                    module3._settings.Save();
                    host3.RaiseCompanionSpawned(new FakeCompanion());
                    module3.Apply(OneBlockedDetection());
                    probe.Check("animation alone animates, silently",
                        host3.BroadcastLines.Count == 0 && host3.NotificationSoundsPlayed == 0
                        && host3.PlayedAnimations.Count > 0);
                    // WITNESS the defect this replaces: boing is on ONE of 53 pets.
                    bool boing = host3.PlayedAnimations.Contains("boing");
                    probe.Check("WITNESS the animation played is not the one-pet list any more",
                        !boing);
                    module3.Shutdown();
                }

                // Log mode: the audit trail, and NOTHING else. Asserted as BEHAVIOUR rather
                // than as the AgentMode.Speaks predicate -- the predicate was already asserted
                // and still passed while nothing consulted it, so Log behaved exactly like
                // Notify and the radio offered an option that did nothing.
                using (var storage4 =
                           new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ch4"))
                {
                    var host4 = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    host4.UseStorage("agentflow", storage4);
                    var module4 = new AgentFlowModule();
                    host4.Declared = module4.Info.Permissions;
                    module4.Init(host4);
                    module4._settings.Set(SettingMode, AgentMode.Log);
                    module4._settings.Set(SettingNotifySpeak, "true");
                    module4._settings.Save();
                    host4.RaiseCompanionSpawned(new FakeCompanion());
                    module4.Apply(OneBlockedDetection());
                    probe.Check("WITNESS Log is the quiet one, and says nothing out loud",
                        host4.BroadcastLines.Count == 0 && host4.SaidLines.Count == 0);
                    probe.Check("...while still scanning, which is the point of Log",
                        module4.Enabled);
                    module4.Shutdown();
                }
            }
            return true;
        }
        /// <summary>
        /// Where the "Open the log" button points.
        ///
        /// Asserted because the first version hardcoded %LOCALAPPDATA%\\DesktopAICompanion and
        /// the host refused it -- correctly. A PORTABLE build keeps its data beside the exe, so
        /// the path was genuinely outside the root it asked to reveal from. That is the worst
        /// shape of bug: right on the machine it was written on, wrong everywhere else, and
        /// invisible until someone ran the other build.
        /// </summary>
        private static bool SelfCheckLogPath(SelfTestProbe probe)
        {
            // WITNESS: the log sits beside the modules folder, whatever the root is. Both
            // shapes are checked, because getting this right for one and wrong for the other
            // is exactly what happened.
            probe.Check("WITNESS the log is found under an INSTALLED data root",
                LogPathFrom(new FakeStorage(
                    @"C:\Users\x\AppData\Local\DesktopAICompanion\modules\agentflow"))
                == @"C:\Users\x\AppData\Local\DesktopAICompanion\diagnostics.log");
            probe.Check("WITNESS ...and under a PORTABLE one beside the exe, which is what broke",
                LogPathFrom(new FakeStorage(@"D:\build\x64\data\modules\agentflow"))
                == @"D:\build\x64\data\diagnostics.log");

            probe.Check("a trailing separator does not shift the answer up a level",
                LogPathFrom(new FakeStorage(@"D:\data\modules\agentflow\"))
                == @"D:\data\diagnostics.log");
            probe.Check("no storage means no guess",
                LogPathFrom(null) == null && LogPathFrom(new FakeStorage("")) == null);
            return true;
        }

        /// <summary>A storage handle with nothing behind it, for the path arithmetic above.</summary>
        private sealed class FakeStorage : IModuleStorage
        {
            private readonly string _dir;
            public FakeStorage(string dir) { _dir = dir; }
            public string DataDirectory { get { return _dir; } }
        }
        /// <summary>
        /// "Yes, allow ... for all projects", and the choice to press it.
        ///
        /// The strings here are the REAL ones, read off a live prompt on 2026-09-19, because the
        /// finished label never appears as a literal in the agent's bundle: it is composed from
        /// "Yes, allow " + the rules + " for " + a destination, so it could only be transcribed
        /// from something rendered.
        /// </summary>
        private static bool SelfCheckAllProjects(SelfTestProbe probe)
        {
            // The exact prompt that exposed this. Both rows used to classify approve-once, so the
            // module called it ambiguous and refused -- which is EVERY bash prompt, i.e. most.
            var real = new List<string>
            {
                "Yes",
                "Yes, allow python -c \"import… and python -c ' * for all projects",
                "No",
            };
            string matched;
            probe.Check("WITNESS a for-all-projects row is NOT an approve-once row",
                PromptOptions.Classify(real[1], out matched)
                == OptionKind.ApproveAllProjects);

            // THE MIDDLE IS IRRELEVANT, and that has to be tested rather than asserted in a
            // comment. The rules between "Yes, allow " and the destination are whatever the
            // agent was about to run, so a fixture that only ever says "python" proves nothing
            // about npm, git, or a command with an apostrophe in it. Only the SUFFIX decides.
            string[] middles =
            {
                "python -c \"import os\"",
                "npm run build",
                "Bash(git push --force)",
                "docker compose up -d && echo done",
                "rm -rf ./build",
                "echo 'it’s fine'",
                "a",
            };
            int classifiedSame = 0;
            foreach (string middle in middles)
            {
                if (PromptOptions.Classify("Yes, allow " + middle + " for all projects",
                        out matched) == OptionKind.ApproveAllProjects)
                    classifiedSame++;
            }
            probe.Check("WITNESS the command in the middle does not change the answer",
                classifiedSame == middles.Length);

            // Adversarial: the destination phrase appearing INSIDE the command must not decide
            // it. This is why the match is anchored at the end rather than searched for.
            //
            // The wording is load-bearing and the first attempt got it wrong. Every destination
            // begins with a SPACE, and the first fixture embedded the phrase as
            // echo "for all projects" -- where the character before "for" is a quote, not a
            // space. So Contains and EndsWith agreed on it, the case distinguished nothing, and
            // the mutation swapping one for the other survived. Here the phrase is preceded by a
            // real space, so only an anchored match gets it right.
            const string Embedded =
                "Yes, allow git commit -m \"fix for all projects\" for this session";
            probe.Check("WITNESS the phrase inside the command does not make it all-projects",
                PromptOptions.Classify(Embedded, out matched) == OptionKind.ApproveWider);
            probe.Check("...and such a prompt is not pressed by the all-projects setting",
                PromptOptions.Choose(new List<string> { "Yes", Embedded, "No" }, true).Index == 0);

            PromptDecision byDefault = PromptOptions.Choose(real, false);
            probe.Check("WITNESS by default it presses the one-call row, and no longer refuses",
                byDefault.WillPress && byDefault.Index == 0);

            PromptDecision asked = PromptOptions.Choose(real, true);
            probe.Check("WITNESS with the setting on it presses the for-all-projects row",
                asked.WillPress && asked.Index == 1);
            probe.Check("...and says out loud that it saved a rule",
                asked.Reason != null
                && asked.Reason.IndexOf("ALL PROJECTS", StringComparison.Ordinal) >= 0);

            // The setting unlocks THAT row and nothing else. The other four destinations, the
            // permanent grant and every mode change stay unpressable however it is set.
            // Each of these includes a plain "Yes", so the ONLY thing that can stop the wider
            // row being pressed is the destination check. Without it the assertion passed off
            // the "no approve-once row present" guard instead, and a mutation making every
            // destination count as all-projects survived.
            probe.Check("WITNESS the other rule destinations stay unpressable",
                PromptOptions.Choose(new List<string>
                    { "Yes", "Yes, allow x for this session", "No" }, true).Index == 0
                && PromptOptions.Choose(new List<string>
                    { "Yes", "Yes, allow x for this project (shared)", "No" }, true).Index == 0
                && PromptOptions.Choose(new List<string>
                    { "Yes", "Yes, allow x for this project (just you)", "No" }, true).Index == 0);
            // ...and with no one-call row at all, the user's own choice is still honoured.
            probe.Check("WITNESS a prompt with only the all-projects row is still pressed",
                PromptOptions.Choose(new List<string>
                    { "Yes, allow x for all projects", "No" }, true).Index == 0);
            probe.Check("WITNESS don't-ask-again is still never pressed, setting or not",
                !PromptOptions.Choose(new List<string>
                    { "Yes, and don't ask again", "No" }, true).WillPress);
            probe.Check("WITNESS a mode change is still never pressed, setting or not",
                !PromptOptions.Choose(new List<string>
                    { "Yes, and auto-accept", "No" }, true).WillPress);

            // Two of them is an assumption about someone else's UI being wrong, so press nothing.
            probe.Check("two for-all-projects rows are ambiguous, so nothing is pressed",
                !PromptOptions.Choose(new List<string>
                    { "Yes, allow a for all projects", "Yes, allow b for all projects", "No" },
                    true).WillPress);

            // And it is OFF unless asked for.
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ap"))
            {
                host.UseStorage("agentflow", storage);
                var module = new AgentFlowModule();
                module.Init(host);
                probe.Check("WITNESS saving a rule for all projects is OFF until asked for",
                    !module.ApproveForAllProjects);
                module.Shutdown();
            }
            return true;
        }
        /// <summary>
        /// The pet dropdown: display names on screen, type ids in storage, and nothing asked of
        /// the host before the host will answer.
        ///
        /// The bug this guards shipped invisible. ModuleHost calls Init and registers the module
        /// on the NEXT line, so a permission-gated verb called from Init is refused -- and
        /// GetCompanionManager cached that refusal, so the dropdown offered "(any pet)" and
        /// nothing else for the whole session. Both halves are asserted: that nothing is asked
        /// during Init, and that the id/display mapping round-trips.
        /// </summary>
        private static bool SelfCheckPetChoices(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-pet"))
            {
                host.UseStorage("agentflow", storage);
                var module = new AgentFlowModule();

                // WITNESS the ordering rule. Asking during Init is refused by a host that has not
                // registered the module yet, and the refusal used to be permanent.
                probe.Check("WITNESS the module asks the host nothing while it is initialising",
                    module.AskedHostForPetsDuringInit() == false);

                module.Init(host);
                probe.Check("...and considers itself initialised once Init has run",
                    module.AskedHostForPetsDuringInit() == true);

                // The mapping. A display name is what the user picks; a type id is what the XML
                // is read by, and storing the wrong one reads later as "that pet has no
                // animations" rather than as a mistake.
                probe.Check("a named pet shows its name, not its folder",
                    AgentFlowModule.PetDisplay(new CompanionTypeInfo
                        { TypeId = "esheep64", DisplayName = "Pearl" }) == "Pearl");
                probe.Check("WITNESS an unnamed pet falls back to its id rather than a blank row",
                    AgentFlowModule.PetDisplay(new CompanionTypeInfo
                        { TypeId = "shimeji-cyn", DisplayName = "" }) == "shimeji-cyn");
                probe.Check("a null type contributes nothing",
                    AgentFlowModule.PetDisplay(null) == "");

                // ---- the list is the pets ON SCREEN -------------------------------
                // Reported from a real pane: it offered every installed companion, which on
                // this machine is a scrolling list of folder ids for a choice about the two
                // pets the user is looking at.
                host.CompanionManager = new FakePets(
                    new[] { "pink_sheep", "Pearl", "shimeji-hornet-9b9d1d", "Hornet",
                            "shimeji-brq51bkr", "Jesus Our Lord", "esheep64", "eSheep (default)" },
                    new[] { "pink_sheep", "shimeji-hornet-9b9d1d" });

                var listed = new List<string>(module.PetChoicesForSelfTest());
                probe.Check("WITNESS only the pets on screen are offered, not all installed",
                    listed.Count == 3 && listed.Contains("Pearl") && listed.Contains("Hornet"));
                probe.Check("WITNESS ...so a pet that is merely installed is absent",
                    !listed.Contains("Jesus Our Lord") && !listed.Contains("eSheep (default)"));
                probe.Check("WITNESS they are offered by name, not by folder id",
                    !listed.Contains("pink_sheep") && !listed.Contains("shimeji-hornet-9b9d1d"));
                probe.Check("(any pet) is still first, so the generic choice remains",
                    listed[0] == PetAnimations.AnyPet);

                // A pet chosen earlier and since removed must stay listed, or opening the
                // pane silently changes what the user picked.
                module._settings.Set(SettingAnimPet, "shimeji-brq51bkr");
                module._settings.Save();
                var kept = new List<string>(module.PetChoicesForSelfTest());
                probe.Check("WITNESS a chosen pet that is no longer up is still listed",
                    kept.Contains("Jesus Our Lord"));
                module._settings.Set(SettingAnimPet, "");
                module._settings.Save();

                // Nothing up: fall back to the installed set, or the dropdown cannot be
                // configured before a pet is spawned.
                host.CompanionManager = new FakePets(
                    new[] { "pink_sheep", "Pearl", "esheep64", "eSheep (default)" },
                    new string[0]);
                var none = new List<string>(module.PetChoicesForSelfTest());
                probe.Check("WITNESS with no pet on screen the installed ones are offered",
                    none.Count == 3 && none.Contains("Pearl"));

                module.Shutdown();
            }
            return true;
        }

        /// <summary>Self-test only: has Init finished, i.e. may the host be asked yet?</summary>
        internal bool AskedHostForPetsDuringInit() { return !_initialising; }

        /// <summary>Self-test only: the pet dropdown contents.</summary>
        internal string[] PetChoicesForSelfTest() { return PetChoices(); }

        /// <summary>
        /// A companion manager with a known set of pets, so "only the ones on screen" can be
        /// asserted without spawning anything. Pairs are (typeId, displayName).
        /// </summary>
        private sealed class FakePets : ICompanionManager
        {
            private readonly string[] _pairs;
            private readonly string[] _onScreen;
            public FakePets(string[] pairs, string[] onScreen) { _pairs = pairs; _onScreen = onScreen; }

            public IReadOnlyList<CompanionTypeInfo> InstalledTypes()
            {
                var list = new List<CompanionTypeInfo>();
                for (int i = 0; i + 1 < _pairs.Length; i += 2)
                    list.Add(new CompanionTypeInfo { TypeId = _pairs[i], DisplayName = _pairs[i + 1] });
                return list;
            }

            public IReadOnlyList<CompanionCount> OnScreenMix()
            {
                var list = new List<CompanionCount>();
                foreach (string id in _onScreen) list.Add(new CompanionCount { TypeId = id, Count = 1 });
                return list;
            }

            public string CompanionsDirectory { get { return ""; } }
            public int MaxCompanions { get { return 8; } }
            public bool IsAtMax { get { return false; } }
            public bool TryReadTypeXml(string typeId, out string animationsXml, out string error)
            {
                animationsXml = "<animations><animations><animation><name>walk</name>"
                                + "</animation></animations></animations>";
                error = null;
                return true;
            }
            public bool SpawnOne(string typeId) { return false; }
            public bool RemoveOne(string typeId) { return false; }
            public bool ValidateXml(string animationsXml, out string error) { error = null; return true; }
            public ICompanionPreview SpawnPreview(string animationsXml, out string error)
            { error = null; return null; }
            public bool InstallType(string typeId, string animationsXml, out string error)
            { error = null; return false; }
            public bool UninstallType(string typeId, out string error) { error = null; return false; }
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
