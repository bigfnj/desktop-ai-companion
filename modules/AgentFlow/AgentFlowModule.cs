using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    /// IT DOES PRESS, SINCE 1.2.0, AND ONLY EVER THE NARROWEST ROW. This paragraph used to say
    /// the opposite and was left behind by the feature; it is corrected here rather than deleted,
    /// because the reasoning still governs the design. Four public tools that answer prompts were
    /// read at source level (see docs/agentflow/README.md) and ALL FOUR press a wider grant than
    /// they advertise -- an "always allow", an "accept all", or a blind Enter on whichever row the
    /// cursor happens to rest on. Four authors, four architectures, one destination.
    ///
    /// So the classifier came first and the press came second. PromptOptions.Choose reads the
    /// option LABELS and will only press a once-only row; an unrecognised label refuses the whole
    /// prompt rather than guessing, and nothing is ever pressed by keystroke -- in Codex's panel
    /// Escape is Deny, so a synthetic key is a wrong answer, not a near miss. Auto-approve is off
    /// until a user turns it on, and the modes are layered so that watching does not imply
    /// pressing: Notify reads the same panel and only tells you about it.
    /// </para>
    ///
    /// <para>
    /// WHAT IT READS, AND WHY THAT NEEDS SAYING. It reads the agents' own JSONL transcripts, which
    /// contain every command run, every path touched, and the full text of what the user typed.
    /// Nothing leaves this machine: the only socket it opens is a LOOPBACK one, to the editor's
    /// own debugging port, and there is no outbound call anywhere in the module. (The older
    /// wording here, "no network call at all", stopped being true when CDP arrived.) Nothing from
    /// a transcript or a panel is ever logged or spoken: the bubble names a tool and a project
    /// folder, never a command, an argument or a path. It declares
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
        /// <summary>How many prompts auto-approve may press in five minutes. See
        /// <see cref="PressBudget.DefaultPressLimit"/>; the default is the maximum.</summary>
        private const string SettingPressLimit = "pressLimit";
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
        private const string SettingApproveSimilar = "approveSimilar";
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

        /// <summary>Test seam: a bound nothing observes is not a bound.</summary>
        internal int ExplainedCountForSelfTest { get { return _explained.Count; } }

        // Written on the UI thread only, read by the tray's DynamicText on the UI thread.
        private string _status = "no agents seen yet";

        /// <summary>One cursor per live transcript, carried between polls. This is the state
        /// that makes a tick cost O(new bytes) instead of O(history).</summary>
        private readonly SessionCache _sessions = new SessionCache();

        // The shape of the last scan, so the pane can say whether notifying is doing
        // anything at all right now. Counts rather than the status STRING, because a pane
        // keying on "standing down (auto mode)" would break the day that wording changes.
        private int _lastSessions;
        private int _lastStoodDown;

        // Guards against overlapping scans when a poll outlives its interval.
        private int _scanning;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "agentflow",
            Name = "AgentFlow",
            Version = "1.4.4",   // 1.4.4: the tables were six releases stale, and nothing was
                                 //        checking. agentflow_headers.py is the header-table
                                 //        equivalent of the option audit; on its first run it
                                 //        found a shape the hand-derived table had missed.
                                 //        Then the OPTION audit, pointed at 2.1.280, found
                                 //        "Yes, and use auto mode" and "Send feedback and
                                 //        keep planning" unclassified. Both are classified
                                 //        now; the mode-change row is recognised and still
                                 //        unpressable. That audit could not SEE either
                                 //        label, so it is scoped to the prompt component and
                                 //        widened only inside it -- widening alone gave 174
                                 //        false hits across the bundle.
                                 //        AND a prompt that is simply NOT OURS no longer
                                 //        reads as a fault. A plan prompt offers two mode
                                 //        changes and a decline, so there is nothing here
                                 //        to press and there never was; it now falls back
                                 //        to the plain "a prompt is waiting" notice instead
                                 //        of a refusal that blamed the screen capture.
                                 //        RefusalKind makes the two cases separable by the
                                 //        caller rather than by reading prose.
                                 // 1.4.3: the two things 1.4.2 left open, both about the log
                                 //        telling the truth. A prompt card this build cannot
                                 //        read now reports itself as such -- a fifth
                                 //        ReadOutcome, Blind -- instead of spelling itself
                                 //        'none', which is what an idle editor says; the
                                 //        companion says it out loud, because an approver
                                 //        that has gone blind looks exactly like one with
                                 //        nothing to do. And the header table was re-derived
                                 //        from bundle 2.1.280: it listed 4 of the 14 shapes,
                                 //        so nine logged as "an unrecognised prompt" --
                                 //        including the shell prompt, the commonest of all,
                                 //        which is a template and so was never a row.
                                 //        Matching is longest-wins now, because "allow
                                 //        searching in <path>?" and "allow searching for this
                                 //        query?" are different tools.
                                 // 1.4.2: auto-approve could not see MOST Codex prompts. The
                                 //        reader opened by querying the split button's
                                 //        aria-label and gave up when it was absent -- but Codex
                                 //        renders that dropdown only when it has a wider grant to
                                 //        offer, so every plain two-button card ("Deny" /
                                 //        "Allow once") read as 'none', which is the same answer
                                 //        as an idle editor. Nothing was pressed and nothing was
                                 //        logged. Now anchored on the card's own container name,
                                 //        with the dropdown optional and still excluded from the
                                 //        options when present. Found against a live prompt on
                                 //        2026-09-23; the self-test had pinned the old anchor as
                                 //        correct behaviour.
                                 // 1.4.1: two field reports against v1.2.4. The animation
                                 //        dropdown showed the generic seven for a pet with its
                                 //        own list, curable only by changing the pet and changing
                                 //        back: the memo introduced in 1.3.2 cached a FALLBACK,
                                 //        and the fallback is taken on every launch because
                                 //        Pets() returns null for the whole of Init. Only an
                                 //        authoritative answer is cached now. And an installed
                                 //        pet that was not on screen could not be chosen at all,
                                 //        so a pet you own was invisible while another was up.
                                 // 1.4.0: the approval limit is YOURS. It was a hard-coded 10
                                 //        presses per five minutes, and on 2026-09-22 it stood the
                                 //        module down in the middle of the owner's ordinary work --
                                 //        two agent sessions reach ten in five minutes easily. It
                                 //        is now a pane setting, 1 to 9999, DEFAULTING TO 9999, so
                                 //        on means on and a backstop is something you ask for.
                                 //        Also fixes the repeat guard, which latched the module off
                                 //        after three DIFFERENT compound-command prompts: they all
                                 //        sign as `Bash|Yes|No`, having no wider-grant row to tell
                                 //        them apart, and a compound command can never be
                                 //        wildcarded so it is exactly the kind that always prompts.
                                 //        A confirmed click now clears the repeat counter, which is
                                 //        the direct evidence the prompt went away; the guard still
                                 //        fires when a click genuinely does not land. A self-test
                                 //        had pinned the collision as correct behaviour.
                                 // 1.3.2: the options pane no longer does blocking IO on the UI
                                 //        thread. It used to call VsCodeSetup.Inspect on every
                                 //        open AND every dropdown change: MEASURED 273-285 ms with
                                 //        the debugging port closed, because the probe inside it
                                 //        bounds a connect that takes 2,063 ms to be refused. The
                                 //        poll worker now caches that inspection and the pane
                                 //        renders the cache. The per-pet animation list was also
                                 //        read and parsed from disk TWICE per load, once for the
                                 //        dropdown and once to pick its selection; memoised per
                                 //        pet, dropped on Save.
                                 // 1.3.1: audit fixes. The Notify-mode screen watch 1.2.0 promised
                                 //        was unreachable; a successful press reported "cannot see
                                 //        the panel"; a saved cooldown never reached the budget
                                 //        until the pane was re-saved; the pet dropdown could not
                                 //        affect which pet animated; a paused prompt was announced
                                 //        to nobody and then never again; every Codex session
                                 //        logged as the same id; and three cursor defects (a
                                 //        same-length replacement, a 64-byte head that 104 real
                                 //        transcripts share, a failed stat restarting the idle
                                 //        clock). 7/7 mutations FIRED on the new guards.
                                 // 1.3.0: transcripts are read FORWARD from a cursor instead of
                                 //        re-read whole on every tick. THE FOLD specifically, not
                                 //        the whole tick: measured against this box's real 46.2 MB
                                 //        transcript in fresh interleaved processes, 535 ms became
                                 //        0.3 ms. The tick also sweeps the transcript roots, ~31 ms
                                 //        for Claude and ~22 ms for Codex, which is now the
                                 //        dominant term -- so the honest whole-tick figure is
                                 //        roughly 590 ms to 53 with both watched, not 535 to 0.3.
                                 //        (Those two were first quoted from a Python proxy and were
                                 //        wrong in BOTH directions; re-measured in .NET 2026-09-21
                                 //        by calling ActiveTranscripts directly.)
                                 //        The watch section also now says when
                                 //        auto-approve is already covering prompts, rather than
                                 //        greying boxes that are the only way to see an agent
                                 //        outside the editor.
                                 // 1.2.0: it now tells you about a prompt it can SEE, rather than
                                 //        only about one it predicted. The screen sweep runs
                                 //        whenever the mode scans, not only when it presses, so a
                                 //        prompt left alone -- Notify mode, an unrecognised option,
                                 //        the budget standing down -- gets said out loud instead of
                                 //        only logged. An observation needs no precision figure, and
                                 //        it works for Codex, which has no rule corpus to predict
                                 //        from. MINOR: new behaviour, not a fix.
                                 // 1.1.10: the watch section says what it is and whether it is
                                 //        doing anything. Two checkboxes headed "Which agents"
                                 //        read like they decide whether AgentFlow works with an
                                 //        agent at all; they only pick who gets WATCHED for being
                                 //        stuck, and that is asleep while every session decides
                                 //        for itself. Both could be ticked, everything working,
                                 //        and nothing ever happen. The owner wrote it and was
                                 //        still confused by it, which is the whole argument.
                                 // 1.1.9: a press can no longer land after Shutdown. Stopping the
                                 //        timer never stopped the poll already on a worker, and that
                                 //        poll ends in a CLICK, so the module could act on someone's
                                 //        behalf after they switched it off. Also drops a dead field
                                 //        that cost a process enumeration per pane open, removes an
                                 //        unreachable Load the self-test was the only caller of, and
                                 //        makes the silent inline-dispatch fallback visible.
                                 // 1.1.8: reads Codex's approval policy out of turn_context, where
                                 //        it actually lives. Nothing new FIRES -- precision for Codex
                                 //        is unmeasured -- but the stand-down now names the real
                                 //        policy instead of "unknown mode", and stops quoting a
                                 //        Claude measurement at an agent nobody has measured.
                                 // 1.1.7: Codex. It renders in a VS Code webview on the same debug
                                 //        port as Claude, so the transport already reached it -- the
                                 //        target filter was simply hardcoded to one extension id.
                                 //        Adds its vocabulary ("Allow once", "Allow similar
                                 //        commands", "Deny"), strips the keyboard hint it renders
                                 //        INSIDE the label, and an opt-in for the wider row that
                                 //        mirrors approveAllProjects. Reader and clicker are anchored
                                 //        on aria, and neither may send a keystroke: Escape is Deny
                                 //        in that dialog.
                                 // 1.1.6: one log line whenever the ability to press CHANGES.
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
            // Seed it from what the user actually saved. Constructing with the default and only
            // ever correcting it in Apply meant a cooldown set last week was silently the default
            // on every launch until the pane happened to be saved again -- the setting persisted
            // perfectly and did nothing, which is worse than not persisting.
            _budget.SetCooldownSeconds(CooldownSeconds);
            _pressBudget.SetPressLimit(PressLimit);

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
            BeginShutdown();
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
            // Prune on the way in as well as in AnyCompanionCanSpeak. That was the only pruner,
            // so with notify-speech off nothing ever called it and a long session accumulated a
            // dead handle per despawn for the life of the instance.
            PruneCompanions();
            if (companion != null) _companions.Add(companion);
        }

        /// <summary>Drop handles for pets that have gone away. There is no CompanionRemoved
        /// event, so asking the host is the only way the list can be made truthful.</summary>
        private void PruneCompanions()
        {
            IHost host = _host;
            if (host == null) return;
            for (int index = _companions.Count - 1; index >= 0; index--)
            {
                bool alive;
                try { alive = host.IsCompanionAlive(_companions[index]); }
                catch (Exception) { alive = false; }
                if (!alive) _companions.RemoveAt(index);
            }
        }

        /// <summary>
        /// Play the stored animation, on the stored PET.
        ///
        /// The pet half of that sentence used to be discarded. PetAnimations.Candidates took a
        /// pet argument it never read, and the result went to PlayAnimationAll, which reaches
        /// every live pet by contract -- so the pane's pet dropdown, and the animation list that
        /// cascades off it, could not affect anything a user would see. Picking "shimeji-cyn /
        /// wave" played wave on whichever pets happened to define it.
        ///
        /// A named pet that is NOT on screen falls back to every pet rather than to silence:
        /// the stored choice can outlive the pet it was made for, and the ordered candidate list
        /// exists precisely so that case degrades instead of failing.
        /// </summary>
        private void PlayChosenAnimation()
        {
            IHost host = _host;
            if (host == null) return;
            var candidates = new List<string>(PetAnimations.Candidates(
                _settings == null ? "" : _settings.Get(SettingAnimName, "")));

            string wanted = StoredAnimPet;
            if (!string.IsNullOrEmpty(wanted) && wanted != PetAnimations.AnyPet)
            {
                PruneCompanions();
                foreach (ICompanion companion in _companions)
                {
                    if (!string.Equals(companion.TypeId, wanted, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (string name in candidates)
                    {
                        try { if (host.TryPlayAnimation(companion, name)) return; }
                        catch (Exception) { }
                    }
                    // It is on screen and defines none of these. Stop anyway: the user named a
                    // pet, and animating a DIFFERENT one is not a graceful degradation of that.
                    return;
                }
            }
            host.PlayAnimationAll(candidates);
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
            if (_host == null) return false;
            PruneCompanions();
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
            bool similar = ApproveSimilarCommands;
            int cdpPort = CdpPort;
            // Snapshot on the UI THREAD with the rest of them. Enabled was the one setting the
            // worker still went and fetched for itself, which meant reading the settings
            // Dictionary<string, string> while Apply could be writing it from here. Every other
            // value on this beat was already copied across the boundary; this one was missed
            // because it hides behind two predicates instead of being named inline.
            bool enabledNow = Enabled;

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
                var resetNotes = new List<string>();
                try
                {
                    results = Scan(watchClaude, watchCodex, threshold, _approvalsCounted,
                                   out approved, freshApprovals, _sessions, resetNotes);
                }
                catch (Exception)
                {
                    // A scan that throws must not take the app's UI thread down with it, and there
                    // is nothing here worth surfacing to a user: the next tick tries again.
                    results = null;
                }

                // Refresh the cached port state on the same beat, so the tray can show whether
                // approving could actually happen without probing a socket on menu open.
                //
                // INSPECT, not Probe, and the extra work is the point: Inspect answers both "is
                // the port answering" and "what does argv.json say", and the pane needed the
                // second one. It used to get it by calling Inspect ITSELF, on the UI thread, on
                // every pane open AND every dropdown change.
                //
                // MEASURED 2026-09-21, one call per fresh process: Inspect costs 30.7/30.1/30.7 ms
                // with the port listening, and 273.3/284.6/279.3 ms with it closed -- because the
                // probe inside it has to bound a connect that takes two full seconds to be
                // refused (see VsCodeSetup.Probe). So a user who has not set up the debugging port
                // paid a quarter-second freeze to open the options, and another one per combo box
                // they touched. Doing it here costs this worker one small file read on a beat that
                // was already opening that socket, and costs the UI thread nothing at all.
                bool answering = false;
                try
                {
                    if (ShouldProbePort(enabledNow))
                    {
                        SetupReport report = VsCodeSetup.Inspect(ArgvPath, 200);
                        // Published as a whole, freshly-built instance. Inspect fills it locally
                        // and returns it, so nothing else can observe a half-written report.
                        _setupCache = report;
                        answering = report.State == SetupState.Listening;
                    }
                }
                catch { answering = false; }

                // Press, if asked to and if there is anything to press. Behind BOTH the
                // user's switch and a port that answered this tick: either one missing and
                // this does not run at all.
                string approvalNote = null;
                bool sawPanel = false, sawBlind = false;
                ScreenPrompt seen = null;

                // LOOK whenever the mode does anything at all; PRESS only in auto-approve. These
                // used to be one condition, so a Notify-mode user got the weak signal (predicting
                // from permission rules) while the strong one -- the prompt itself, already on
                // screen and already readable -- went unused.
                bool mayLook = MayLookNow(answering, enabledNow);
                bool mayPress = ShouldPressNow(autoApprove, answering);
                if (mayLook)
                {
                    if (_resetPressBudget) { _resetPressBudget = false; _pressBudget.Reset(); }
                    ScreenPrompt found = null;
                    try
                    {
                        approvalNote = CdpApprover.Sweep(cdpPort, view =>
                        {
                            bool didPress = false;
                            string note = mayPress
                                ? Decide(cdpPort, view, _pressBudget, allProjects, similar, out didPress)
                                : "a prompt is waiting for " + DescribeSubject(view)
                                  + " (auto-approve is off, so it was left alone)";
                            if (!didPress)
                                found = new ScreenPrompt
                                {
                                    Signature = view.Agent + "|" + string.Join("|", view.Options),
                                    Subject = DescribeSubject(view),
                                };
                            return note;
                        }, 1500, out sawPanel, out sawBlind);
                    }
                    catch (Exception)
                    {
                        approvalNote = null; sawPanel = false; sawBlind = false; found = null;
                    }
                    // A card nobody could read is the one case with no PromptView to describe,
                    // and it is the case the user most needs told OUT LOUD rather than left in
                    // a log file: from the outside, an approver that has gone blind and an
                    // approver with nothing to do look exactly alike. Only when nothing else
                    // was found, so a readable prompt on another target still wins the bubble.
                    if (found == null && sawBlind)
                        found = new ScreenPrompt
                        {
                            // Constant, so the once-per-prompt guard holds it to one sentence
                            // for as long as the unreadable card stays on screen.
                            Signature = "blind-card",
                            Subject = "a prompt it cannot read",
                            Notice = "There is a prompt on screen this build cannot read, "
                                     + "so nothing was pressed.",
                        };
                    seen = found;
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
                    List<string> forResets = resetNotes;
                    ScreenPrompt forSpeech = seen;
                    PostToUi(() =>
                    {
                        // The instance may have been torn down between the worker finishing and
                        // this running; with no SynchronizationContext it runs INLINE on that
                        // worker, so there is not even a message pump to drop it.
                        if (_shuttingDown) return;
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
                        // Everything on this channel is a one-off EXCEPT the no-rule-files
                        // note, which describes a state and would otherwise be written every ten
                        // seconds for as long as it held. Deduped here and re-armed below, so it
                        // says it again if the rules vanish a second time.
                        bool noRuleFiles = false;
                        foreach (string resetNote in forResets)
                        {
                            if (resetNote == NoRuleFilesNote)
                            {
                                noRuleFiles = true;
                                if (_saidNoRuleFiles) continue;
                            }
                            Log(resetNote);
                        }
                        _saidNoRuleFiles = noRuleFiles;
                        LogApprovalAttempt(note);
                        AnnounceScreenPrompt(forSpeech);
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

        /// <summary>Four-argument form: no Codex opt-in. Kept so the existing assertions read as
        /// what they are about, rather than each carrying a false argument.</summary>
        internal static string Decide(int port, PromptView view, PressBudget budget,
                                      bool allProjects)
        {
            return Decide(port, view, budget, allProjects, false);
        }

        /// <summary>
        /// What to do about one prompt that was read. Split out from the sweep so it can be
        /// exercised without an editor: everything above it is sockets, and everything in it is
        /// the decision, which is the half worth asserting.
        ///
        /// THERE IS NO HEURISTIC HERE AND THERE SHOULD NEVER BE. This reads labels, hands them
        /// to PromptOptions and does what it is told; PromptOptions refuses anything it does not
        /// recognise, refuses a prompt with no approve-once row, and refuses one with more than
        /// one. A guess about which button to press is the single kind of wrong this feature
        /// cannot afford. (Inherited from TryApproveOnce, removed 2026-09-21 once the tick
        /// called the sweep directly; the reasoning outlived the wrapper.)
        /// </summary>
        internal static string Decide(int port, PromptView view, PressBudget budget,
                                      bool allProjects, bool similar)
        {
            bool ignored;
            return Decide(port, view, budget, allProjects, similar, out ignored);
        }

        /// <summary>
        /// <paramref name="pressed"/> is false when a prompt was READ and left alone: an option
        /// nobody recognised, the budget standing down, a disabled row. Those are the cases the
        /// user most needs telling about, and until now the only trace was a log line.
        /// </summary>
        internal static string Decide(int port, PromptView view, PressBudget budget,
                                      bool allProjects, bool similar, out bool pressed)
        {
            pressed = false;
            if (view == null || view.Options.Count == 0) return null;

            PromptDecision decision = PromptOptions.Choose(view.Options, allProjects, similar);
            if (!decision.WillPress)
            {
                // A prompt whose options are all RECOGNISED and none of which approves a
                // single call is not a malfunction, and must not be described as one. The
                // plan prompt is the ordinary case: ExitPlanMode offers two mode changes and
                // a decline, so there is nothing here this module may ever press, and saying
                // "refused" about it sends the reader looking for a fault that is not there.
                //
                // Fall back to the notification the notify half would have given -- a prompt
                // is waiting, here is what it is about, it is yours to answer. The speech
                // bubble already happens, because `pressed` stays false and the caller builds
                // a ScreenPrompt from that; this is the LOG line catching up with it.
                if (decision.Refusal == RefusalKind.NothingToPress)
                    return "a prompt is waiting for " + DescribeSubject(view)
                           + ", and nothing on it approves a single call -- left for you";
                return decision.Reason;
            }

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
                                               decision.ChosenRaw, 1500, view.Agent);
            pressed = string.Equals(outcome, "clicked", StringComparison.Ordinal);
            // A click the CDP layer CONFIRMED is the direct evidence that this prompt is gone, so
            // the next identical-looking one is a different prompt rather than the same one
            // refusing to close. Without this the repeat guard counted three distinct
            // compound-command prompts -- which all sign as `Bash|Yes|No`, having no wider-grant
            // row to tell them apart -- as one prompt pressed three times, and latched the module
            // off in the middle of ordinary work. See PressBudget.NotePromptCleared.
            if (pressed && budget != null) budget.NotePromptCleared();
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
        /// RE-DERIVED from the shipped bundle on 2026-09-23 (webview/index.js, 2.1.280) by
        /// reading every `permissionRequestHeader` render site. There are FOURTEEN, not the
        /// five this comment used to claim, and only the generic fallback names its tool in a
        /// &lt;strong&gt;; the other thirteen supply their own renderer. Four were listed here,
        /// so nine shapes logged as "an unrecognised prompt" -- which reads like the safety net
        /// firing at the moment it worked perfectly, and which is what the maintainer saw on
        /// every ordinary shell prompt, the single most common shape there is.
        ///
        /// LONGEST MATCH WINS, as in PromptOptions and for the same reason: "allow searching
        /// in &lt;path&gt;?" and "allow searching for this query?" are different tools, and a
        /// first-match-wins scan over a prefix table answers whichever happens to be declared
        /// first.
        ///
        /// An ALLOWLIST, for the same reason PromptOptions is one: a header shape nobody has
        /// seen yet can contain anything, and the safe response to not recognising it is to
        /// say so rather than to echo it into a file meant to be attachable to a public issue.
        /// Every value on the right is written HERE, so none of it comes off the screen.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] KnownHeaders =
        {
            // -- a path span was removed before matching, hence the trailing " ?" shapes ----
            new KeyValuePair<string, string>("make this edit to", "an edit"),
            new KeyValuePair<string, string>("allow reading from", "a file read"),
            new KeyValuePair<string, string>("allow write to", "a file write"),
            new KeyValuePair<string, string>("use skill", "a skill"),
            new KeyValuePair<string, string>("allow glob search in", "a glob search"),
            new KeyValuePair<string, string>("allow grep in", "a grep search"),
            new KeyValuePair<string, string>("allow searching in", "a search"),

            // -- no path, so these render as complete sentences ---------------------------
            new KeyValuePair<string, string>("allow this glob command", "a glob search"),
            new KeyValuePair<string, string>("allow this grep command", "a grep search"),
            new KeyValuePair<string, string>("allow this search", "a search"),
            new KeyValuePair<string, string>("allow searching for this query", "a web search"),
            new KeyValuePair<string, string>("allow fetching this url", "a web fetch"),
            new KeyValuePair<string, string>("allow network connection to this host",
                                             "a network connection"),
            new KeyValuePair<string, string>("accept this plan", "a plan"),
            new KeyValuePair<string, string>("continue planning", "a plan"),

            // The degenerate branch of the question prompt, which renders in the header
            // slot when it was handed no questions. Left out of the hand-derived table on
            // the grounds that it is an error state; put in by agentflow_headers.py, which
            // does not share that opinion and reported it on its first run against the
            // bundle. A shape the table cannot name is a shape the log gets wrong,
            // whatever the reason it renders.
            new KeyValuePair<string, string>("no questions provided", "a question"),
        };

        /// <summary>
        /// The shell prompt, which is a template rather than a fixed string and so cannot be a
        /// table row: the bundle renders `["Allow this ", commandLabel, " command?"]`, and
        /// `commandLabel` is the tool's own word for itself -- "bash", "PowerShell".
        ///
        /// Checked BEFORE the table and anchored at BOTH ends, because the prefix alone also
        /// covers "allow this glob command" and "allow this search", which are different tools
        /// with their own rows. The label between the anchors is never read: it is text from
        /// the screen, and this function's whole job is to answer in words written here.
        ///
        /// This is the shape that sent the most common prompt of all to "an unrecognised
        /// prompt" in the log for as long as the table has existed.
        /// </summary>
        private const string ShellHeaderPrefix = "allow this ";
        private const string ShellHeaderSuffix = " command?";

        internal static bool IsShellHeader(string normalizedHeader)
        {
            if (normalizedHeader == null) return false;
            return normalizedHeader.StartsWith(ShellHeaderPrefix, StringComparison.Ordinal)
                && normalizedHeader.EndsWith(ShellHeaderSuffix, StringComparison.Ordinal)
                && normalizedHeader.Length > ShellHeaderPrefix.Length + ShellHeaderSuffix.Length;
        }

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

            // Codex names neither a tool nor a header -- its card is the command and two buttons.
            // Without this the log said "auto-approve clicked for an unrecognised prompt", which
            // reads like the safety net failed at the exact moment it worked perfectly.
            if (string.Equals(view.Agent, CdpApprover.AgentCodex, StringComparison.Ordinal))
                return "a Codex command";

            // Whitespace collapsed as well as trimmed: the header is assembled from several
            // JSX children and a removed path span leaves a double space behind, so
            // "make this edit to  ?" would miss a table row that a human reading the screen
            // would say matched.
            string header = CollapseSpaces((view.UnsafeHeader ?? "").ToLowerInvariant());
            if (IsShellHeader(header)) return "a shell command";

            string best = MatchHeader(header, KnownHeaders);
            if (best == null) return "an unrecognised prompt";
            string extension = SafeExtension(view.PathExtension);
            return extension.Length == 0 ? best : best + " (." + extension + ")";
        }

        /// <summary>
        /// An extension, or nothing. Letters and digits only, eight at most.
        ///
        /// The filter is not decoration. This value is read off the screen, and the span it
        /// comes from holds a full absolute path on Windows -- upstream splits on "/" to take
        /// the leaf, which splits nothing here. Anything that does not look like an extension
        /// is dropped rather than trimmed, because a half-parsed path is still a path.
        /// </summary>
        /// <summary>
        /// Longest matching prefix in <paramref name="table"/>, or null.
        ///
        /// TAKES THE TABLE rather than reading KnownHeaders directly, and that is the whole
        /// reason it exists as a function. No key in the real table is a prefix of another, so
        /// longest-match and first-match cannot be told apart by any header the shipped bundle
        /// produces -- an assertion against the real table passes whichever strategy is
        /// implemented, which is a test that cannot fail. A synthetic table with a deliberate
        /// prefix collision can tell them apart, and the self-test uses one in BOTH declaration
        /// orders, because a single order lets one of the wrong strategies pass by luck.
        ///
        /// Longest-match is the right rule regardless: it is what PromptOptions does, and the
        /// day someone adds a row that IS a prefix of another, first-match would quietly hand
        /// two different tools the same answer.
        /// </summary>
        internal static string MatchHeader(string header,
                                           KeyValuePair<string, string>[] table)
        {
            if (header == null || table == null) return null;
            string best = null;
            int bestLength = -1;
            foreach (KeyValuePair<string, string> known in table)
            {
                if (!header.StartsWith(known.Key, StringComparison.Ordinal)) continue;
                if (known.Key.Length <= bestLength) continue;
                best = known.Value;
                bestLength = known.Key.Length;
            }
            return best;
        }

        /// <summary>Trim and squeeze runs of whitespace to one space. Not a general
        /// normaliser: it exists so a removed path span cannot leave a gap that defeats a
        /// prefix match.</summary>
        internal static string CollapseSpaces(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var builder = new StringBuilder(value.Length);
            bool pendingSpace = false;
            foreach (char ch in value)
            {
                if (char.IsWhiteSpace(ch)) { pendingSpace = builder.Length > 0; continue; }
                if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
                builder.Append(ch);
            }
            return builder.ToString();
        }

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
        /// Say, once, that a prompt is sitting on screen that nothing is going to press.
        ///
        /// ONCE per prompt, not once per poll. The signature is the agent plus its option labels,
        /// so the same prompt seen ten seconds later is the same prompt; a different one announces,
        /// and the screen going quiet re-arms it. That is simpler than the transcript path's
        /// cooldown budget for a good reason: a prompt is either there or it is not, so there is no
        /// rate to limit, only a repeat to avoid.
        ///
        /// Honours the pause, because a user who silenced the companion meant all of it -- and
        /// CHECKS THE PAUSE BEFORE SPENDING THE ONE-SHOT. Marking the prompt announced and then
        /// returning burned the single announcement this prompt will ever get on a tick that
        /// delivered nothing, so a prompt that appeared during a pause stayed silent for the rest
        /// of its life. A guard that says "already said" has to be set where something was said.
        /// </summary>
        internal void AnnounceScreenPrompt(ScreenPrompt seen)
        {
            if (seen == null) { _announcedScreenPrompt = null; return; }   // screen quiet: re-arm
            if (_host == null || _shuttingDown) return;
            if (string.Equals(seen.Signature, _announcedScreenPrompt, StringComparison.Ordinal)) return;
            if (_budget != null && _budget.IsPaused(DateTime.UtcNow)) return;

            _announcedScreenPrompt = seen.Signature;
            Log(seen.Notice
                ?? ("a prompt is waiting on screen for " + seen.Subject
                    + " and nothing pressed it"));
            if (NotifySpeakOn && AgentMode.Speaks(Mode))
            {
                try
                {
                    _host.SayAll(seen.Notice
                                 ?? ("Something is waiting for you: " + seen.Subject + "."));
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Write what the approver did, and do it once per distinct outcome.
        ///
        /// The repeat guard matters more than it looks. A prompt the classifier refuses stays on
        /// screen until the user answers it, so without this the same refusal would be written
        /// every ten seconds for as long as they were away from the keyboard.
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
            return Scan(watchClaude, watchCodex, threshold, approvalsCounted, out approved,
                        recent, null, null);
        }

        /// <summary>
        /// <paramref name="sessions"/> null means READ EVERY TRANSCRIPT WHOLE -- the cold path,
        /// which is what "Check now" and the assertions want, and what a cursor falls back to
        /// after a reset. Non-null resumes each file from wherever its cursor stopped.
        ///
        /// Keeping both is not indecision. The whole-file reader is the definition the
        /// incremental one is checked against, so leaving it reachable keeps it exercised
        /// rather than letting it rot into dead code that the differential test still trusts.
        /// </summary>
        internal static List<Detection> Scan(bool watchClaude, bool watchCodex, double threshold,
                                             HashSet<string> approvalsCounted,
                                             out Dictionary<string, int> approved,
                                             IList<ApprovalEntry> recent,
                                             SessionCache sessions,
                                             IList<string> resetNotes)
        {
            int sources;
            RuleSet rules = RuleLoader.Load(RuleLoader.DefaultPaths(), out sources);
            // NO RULE FILES AT ALL is a different state from "rules loaded, nothing matched", and
            // until now the difference was thrown away: `sources` was read into this local and
            // never used. Zero means every call reads Undecidable for ever with no explanation,
            // which is exactly what a user whose settings live somewhere unexpected would see.
            if (sources == 0 && resetNotes != null) resetNotes.Add(NoRuleFilesNote);
            DateTime now = DateTime.UtcNow;
            var results = new List<Detection>();
            approved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // SESSIONS, not call ids. The old prune walked every completed call on every tick
            // purely to rebuild this set -- and it was subtly wrong besides: a call ageing out
            // of AgentSession.CompletedCap dropped out of `live`, got pruned from the counted
            // set, and would have been counted a SECOND time if it reappeared. A session id is
            // the right granularity and bounds the set just as tightly.
            var liveSessions = new HashSet<string>(StringComparer.Ordinal);
            var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (watchClaude)
                ScanRoot(TranscriptReader.ClaudeRoot, "subagents", TranscriptReader.AgentClaude,
                         rules, threshold, now, approvalsCounted, approved, recent,
                         sessions, resetNotes, results, liveSessions, livePaths);
            if (watchCodex)
                ScanRoot(TranscriptReader.CodexRoot, null, TranscriptReader.AgentCodex,
                         rules, threshold, now, approvalsCounted, approved, recent,
                         sessions, resetNotes, results, liveSessions, livePaths);

            if (sessions != null) sessions.Retain(livePaths);

            if (approvalsCounted != null)
            {
                var stale = new List<string>();
                foreach (string key in approvalsCounted)
                {
                    int slash = key.IndexOf('/');
                    string owner = slash > 0 ? key.Substring(0, slash) : key;
                    if (!liveSessions.Contains(owner)) stale.Add(key);
                }
                foreach (string key in stale) approvalsCounted.Remove(key);
            }
            return results;
        }

        private static void ScanRoot(string root, string skipDirectory, string agent,
                                     RuleSet rules, double threshold, DateTime now,
                                     HashSet<string> approvalsCounted,
                                     Dictionary<string, int> approved,
                                     IList<ApprovalEntry> recent,
                                     SessionCache sessions, IList<string> resetNotes,
                                     List<Detection> results,
                                     HashSet<string> liveSessions, HashSet<string> livePaths)
        {
            foreach (string path in TranscriptReader.ActiveTranscripts(
                         root, ActiveWindowSeconds, skipDirectory))
            {
                livePaths.Add(path);
                AgentSession session;
                if (sessions == null)
                {
                    session = agent == TranscriptReader.AgentCodex
                        ? TranscriptReader.ReadCodex(path)
                        : TranscriptReader.ReadClaude(path);
                }
                else
                {
                    string reason;
                    session = sessions.For(path, agent).Advance(out reason);
                    // Never silent. A reset means the file changed under us, and "AgentFlow
                    // forgot this session" with no reason on record is a bug report nobody
                    // can answer.
                    if (reason != null && resetNotes != null)
                        resetNotes.Add("re-read " + Short(session) + " from the start: " + reason);
                }
                if (session == null) continue;
                if (!string.IsNullOrEmpty(session.SessionId)) liveSessions.Add(session.SessionId);

                Detection detection = BlockedDetector.Evaluate(session, rules, threshold, now);
                if (detection != null) results.Add(detection);
                Tally(session, rules, approvalsCounted, approved, recent);
            }
        }

        /// <summary>Fold one session's newly-approved calls into the running tally.
        ///
        /// No longer walks Completed to build a live set: the prune keys on the SESSION now, so
        /// the only reason this method ever touched the whole completed list is gone.</summary>
        private static void Tally(AgentSession session, RuleSet rules,
                                  HashSet<string> approvalsCounted,
                                  Dictionary<string, int> approved,
                                  IList<ApprovalEntry> recent)
        {
            if (session == null) return;
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

            // Same bound as every other set in this module: what the current tick can see, not
            // how long the app has run. This one had no prune at all and grew for the life of the
            // instance -- slowly, because it is one key per session per outcome, but without limit.
            var liveSessions = new HashSet<string>(StringComparer.Ordinal);
            foreach (Detection detection in results)
                if (detection.Session != null && detection.Session.SessionId != null)
                    liveSessions.Add(detection.Session.SessionId);
            var forgotten = new List<string>();
            foreach (string key in _explained)
            {
                int slash = key.IndexOf('/');
                if (!liveSessions.Contains(slash > 0 ? key.Substring(0, slash) : key))
                    forgotten.Add(key);
            }
            foreach (string key in forgotten) _explained.Remove(key);

            _status = DescribeStatus(results.Count, blocked, stoodDown);
            _lastSessions = results.Count;
            _lastStoodDown = stoodDown;

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
                PlayChosenAnimation();
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

        /// <summary>
        /// Whether the NOTIFY half is currently doing anything, in words a user can act on.
        ///
        /// This exists because the owner of this module, who wrote it, was confused by his own
        /// settings pane. Two checkboxes headed "Which agents" read like they decide whether
        /// AgentFlow works with an agent at all. They do not: they only pick who gets WATCHED
        /// for being stuck, which is a different job from pressing the button, and one that is
        /// asleep entirely while every session is in a self-deciding mode. Both boxes could be
        /// ticked, everything working exactly as designed, and nothing would ever happen.
        /// </summary>
        internal static string WatchStateLine(int sessions, int stoodDown, bool screenCovers)
        {
            // Said FIRST when it applies, because it is the thing that makes the rest of this
            // section look broken: prompts are already being handled, just not by these boxes.
            string covered = screenCovers
                ? " Prompts on screen are already being handled by auto-approve, which does not "
                  + "use these boxes at all; they are only for agents it cannot see."
                : "";
            if (sessions <= 0) return "Right now: no agent is running." + covered;
            if (stoodDown >= sessions)
                return "Right now: SILENT. Every agent running is in a mode where it decides "
                       + "for itself, so there is nothing to interrupt you about. These boxes "
                       + "only do something in Claude's default mode, where it asks first." + covered;
            return "Right now: active. You will be told when an agent has been waiting." + covered;
        }

        /// <summary>
        /// NOT greyed out, and that is a decision rather than an omission.
        ///
        /// Greying these was asked for to conserve resources, and the cursor removed most of the
        /// resource: the FOLD went from 535 ms per tick to 0.3 ms, measured. What is left is the
        /// directory sweep, which each watch flag does gate. MEASURED 2026-09-21 by calling
        /// ActiveTranscripts itself from a .NET harness, one call per fresh process, interleaved:
        /// 31.7/30.4/30.8 ms for the Claude root (705 files, skipping subagents) and
        /// 21.8/20.4/25.2 ms for the Codex root (247 files), once per ten seconds. Note it is not
        /// linear in file count -- Codex has a third as many files and costs two thirds as much,
        /// because that store nests a directory per day. Around half a percent of one core either
        /// way, so greying would save something, and not much. What it would COST is the larger
        /// number: the transcript watcher is the only thing that can see an
        /// agent running OUTSIDE the VS Code window, so disabling it whenever the screen watcher
        /// is up would trade a capability for a saving that no longer exists. So the pane reports
        /// the overlap instead of enforcing it.
        /// </summary>
        internal string WatchState
        {
            get { return WatchStateLine(_lastSessions, _lastStoodDown, _portAnswering && AutoApprove); }
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

        /// <summary>
        /// Said once when no permission-rule file was found anywhere.
        ///
        /// Carried on the `resetNotes` channel rather than a ninth parameter on Scan, which
        /// already takes eight plus two outs across sixteen call sites. Unlike everything else on
        /// that channel this is a STATE rather than an event, so it would repeat every ten
        /// seconds; the caller dedupes it and re-arms when rules reappear.
        /// </summary>
        internal const string NoRuleFilesNote =
            "no permission-rule file was found, so nothing can predict which calls will prompt; "
            + "the notify half stands down until one appears";

        /// <summary>What every Codex transcript filename begins with. See <see cref="Short"/>.</summary>
        private const string CodexNamePrefix = "rollout-";

        private static string Short(AgentSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.SessionId)) return "?";
            string id = session.SessionId;
            // Codex names its transcripts rollout-<timestamp>-<uuid>, so the leading eight
            // characters are the literal "rollout-" for every session that has ever run: every
            // Codex line in the diagnostic log named the same session, and two running side by
            // side were indistinguishable. The tail of the uuid separates as well as its head.
            if (id.Length > 8 && id.StartsWith(CodexNamePrefix, StringComparison.OrdinalIgnoreCase))
                return id.Substring(id.Length - 8);
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        /// <summary>Log a per-session explanation once per run. Carries no command text.</summary>
        private void Explain(Detection detection, string message)
        {
            string key = (detection.Session != null ? detection.Session.SessionId : "?")
                         + "/" + detection.Outcome;
            if (!_explained.Add(key)) return;
            Log(message);
        }

        /// <summary>Set when PostToUi had no context to post to. Surfaced on the pane rather
        /// than logged, because the discovery happens on a WORKER and IHost is UI-thread-only:
        /// reporting a threading fault by committing another one would be its own joke.</summary>
        private volatile bool _ranInline;

        internal bool RanInline { get { return _ranInline; } }

        /// <summary>Seams for SelfCheckPostToUi, which has to force both branches of PostToUi.
        /// Neither branch is reachable from a self-test otherwise: the only production caller is
        /// the scan worker, and every test that gets this far seeds mode Off so no scan runs.</summary>
        internal void SetUiContextForSelfTest(SynchronizationContext context) { _ui = context; }

        internal void PostToUiForSelfTest(Action action) { PostToUi(action); }

        private void PostToUi(Action action)
        {
            SynchronizationContext ui = _ui;
            if (ui != null) { ui.Post(_ => action(), null); return; }
            // No context was captured at Init, so every IHost call inside the action is about
            // to run on this worker. Correct in the shipped app -- WinForms installs the
            // context before Init -- so this is about the failure being NOTICEABLE rather
            // than about it happening.
            _ranInline = true;
            action();
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

        /// <summary>The user's approval limit, defaulting to the maximum so on means on.</summary>
        private int PressLimit
        {
            get
            {
                return _settings == null
                    ? PressBudget.DefaultPressLimit
                    : _settings.GetInt(SettingPressLimit, PressBudget.DefaultPressLimit);
            }
        }
        /// <summary>
        /// OFF by default, and the reason is OURS, not upstream's.
        ///
        /// This governs the NOTIFY half only. Auto-approve reads the prompt off the screen over
        /// CDP and touches no transcript, so Codex prompts are pressed whatever this says.
        ///
        /// It is off because TranscriptReader.ReadCodex handles three record kinds -- the two call
        /// types and `session_meta`, from which it takes only `cwd` -- and never looks at
        /// `turn_context`. So Mode stays null, every Codex session resolves as `unknown`, and the
        /// stand-down is an allow-list of exactly `default`.
        ///
        /// THE EARLIER CLAIM HERE WAS WRONG and is worth correcting rather than quietly editing:
        /// this said "Codex's rollout transcript records no permission mode at all". It does.
        /// Measured 2026-09-21 across the 25 most recent rollouts on this box, every one carries
        /// `turn_context` with `approval_policy`, and 6 of the 25 were `on-request` -- sessions
        /// that genuinely prompt, and exactly the ones worth watching. The 2026-09-17 measurement
        /// that concluded otherwise almost certainly read `session_meta` and stopped there.
        ///
        /// Closing it means reading `turn_context.approval_policy` and mapping `never` to "cannot
        /// prompt". NOT `collaboration_mode.mode`, which also says "default" while sitting beside
        /// `approval_policy: never`: same word as Claude's default, opposite meaning, and
        /// matching on it would predict prompts in sessions that cannot produce any.
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
            // Drop the memoised animation list. Saving is the one moment a user could have edited
            // a pet in PetStudio and come back here expecting the dropdown to know about it.
            _choicesPet = null;
            _choicesCache = null;
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
            // Same reason as the cooldown above: a limit the user just raised has to reach the
            // live budget now, not at the next launch, because raising it is what someone does
            // the moment it has just stood the module down.
            if (_pressBudget != null) _pressBudget.SetPressLimit(PressLimit);
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

        /// <summary>
        /// Press Codex's "Allow similar commands" instead of its one-call row.
        ///
        /// OFF by default, and for a stronger reason than the all-projects switch: what counts
        /// as SIMILAR is Codex's judgement, not something this module can read off the prompt
        /// or state in the log. A grant whose blast radius the presser cannot describe is not
        /// one it should take on the user's behalf without being told to.
        /// </summary>
        internal bool ApproveSimilarCommands
        {
            get { return _settings != null && _settings.GetBool(SettingApproveSimilar, false); }
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
        /// <summary>
        /// Set FIRST by Shutdown, and read by the worker before it presses anything.
        ///
        /// Stopping the timer does not stop a poll already in flight: it is on a thread-pool
        /// worker, it can be most of a second from finishing, and it ends in a CLICK. So the
        /// module could press a button on the user's behalf after being torn down -- while the
        /// app was closing, or moments after someone switched it off or uninstalled it. Nothing
        /// else in this module has that property; a stale notify is merely noise, a stale press
        /// is an action.
        /// </summary>
        private volatile bool _shuttingDown;

        /// <summary>Whether the no-rule-files note has already been written. See
        /// <see cref="NoRuleFilesNote"/>; cleared as soon as a scan finds a rule file again.</summary>
        private bool _saidNoRuleFiles;

        /// <summary>
        /// The last argv.json + port inspection, written by the poll worker and read by the UI
        /// thread. Volatile because those are different threads and the reference is the handoff;
        /// the instance itself is never mutated after Inspect returns it, so a reader either sees
        /// the previous whole report or the new whole report and never a mixture.
        /// </summary>
        private volatile SetupReport _setupCache;

        /// <summary>
        /// Shutdown's FIRST act, on its own so the guard can be tested in isolation.
        ///
        /// Shutdown also nulls _host, which would make ShouldPressNow false anyway -- so a test
        /// that calls Shutdown and then asks cannot tell the flag from the null, and a mutation
        /// removing the flag SURVIVES it. That is what happened. What the flag actually buys is
        /// the window between here and the end of Shutdown, during which _host is still set, plus
        /// being volatile where _host is not. Calling this alone is the only way to assert it.
        /// </summary>
        internal void BeginShutdown() { _shuttingDown = true; }

        /// <summary>
        /// May a press happen right now? One place, so the worker and the self-test ask the same
        /// question, and so the shutdown case cannot be reintroduced by an inline condition that
        /// forgets it.
        /// </summary>
        internal bool ShouldPressNow(bool autoApprove, bool answering)
        {
            return autoApprove && answering && !_shuttingDown && _host != null;
        }

        /// <summary>
        /// Should this tick probe the debugging port at all?
        ///
        /// TAKES NO autoApprove ARGUMENT, deliberately, and that absence is the guard. The probe
        /// used to sit inside `if (autoApprove)`, so `answering` was false throughout Notify and
        /// Log modes -- which made <see cref="MayLookNow"/> (which requires it) impossible to
        /// satisfy without <see cref="ShouldPressNow"/> also being satisfiable, and left the
        /// Notify branch inside the sweep unreachable from the day it shipped. The 1.2.0 note
        /// promised those users the prompt itself; they got the permission-rule prediction only.
        ///
        /// No assertion can see that defect from outside: it was a value the caller computed,
        /// not a predicate anyone could call. What prevents its return is that reintroducing it
        /// now means adding a parameter here, which is a visible change to a documented decision
        /// rather than a word dropped into a condition.
        /// </summary>
        internal bool ShouldProbePort(bool enabled)
        {
            return enabled && !_shuttingDown && _host != null;
        }

        /// <summary>
        /// May the panel be READ right now? Strictly weaker than <see cref="ShouldPressNow"/>:
        /// looking is what Notify and Log modes want, and pressing is what auto-approve adds.
        ///
        /// Exists as a seam because the two were once inline and the caller had made looking
        /// depend on a value only computed for pressing, which silently reduced this to the
        /// stronger condition. A relationship between two booleans is not visible by reading
        /// either one; it needs an assertion, and an assertion needs something to call.
        /// </summary>
        internal bool MayLookNow(bool answering, bool enabled)
        {
            return answering && !_shuttingDown && _host != null && enabled;
        }

        /// <summary>
        /// A prompt that is ON SCREEN and was not pressed, so the user can be told about it.
        ///
        /// This is an OBSERVATION, not the prediction the transcript watcher makes. That one
        /// asks the permission rules "would this call have prompted?" and is right about 0.4%
        /// of the time outside default mode, which is why it stands down almost everywhere.
        /// Reading the actual prompt off the panel cannot be wrong about whether a prompt is
        /// there, so it needs no precision measurement and works the same for both agents.
        ///
        /// Subject only. Never the command, never an option label: this is spoken aloud and
        /// the same rule applies as to the log.
        /// </summary>
        internal sealed class ScreenPrompt
        {
            public string Signature;   // identity, so it is announced once and not per poll
            public string Subject;     // safe to say out loud
            /// <summary>
            /// A complete sentence that REPLACES both default templates, or null for an
            /// ordinary prompt.
            ///
            /// Exists because the two templates take a subject and read "waiting on screen for
            /// &lt;X&gt;" -- which assumes the subject is a thing the prompt is ABOUT. The
            /// unreadable-card notice is a statement about the reader instead, and forcing it
            /// through the templates produced "waiting on screen for a prompt it cannot read",
            /// which says the wrong thing in the one case that most needs saying clearly.
            /// Written here, so it is as safe to log as any other constant in this file.
            /// </summary>
            public string Notice;
        }

        /// <summary>The prompt last announced from the screen, so a poll every ten seconds does
        /// not become a sentence every ten seconds. Cleared when the screen goes quiet, which is
        /// what re-arms it for the next one.</summary>
        private string _announcedScreenPrompt;

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
        /// <summary>
        /// The argv.json / port status line, rendered from what the POLL last saw.
        ///
        /// Reads no file and opens no socket. This runs from the pane's Load, which the host calls
        /// on every open and again on every dropdown change, and it used to call Inspect directly:
        /// a file read, a JSON parse and a TCP connect on the UI thread, so opening the options
        /// with nothing listening froze the app for the connect timeout, twice over if you then
        /// touched a combo box.
        ///
        /// The cache is refreshed by the tick, which runs on a worker and already opens that
        /// socket. Cold only in the window between Init and the first tick completing, which is
        /// under a second; that one case pays for itself synchronously rather than showing a
        /// user a blank where a status line belongs.
        /// </summary>
        /// <summary>Seams for the assertions about repeat work. Both targets are private and both
        /// properties are about NOT doing something, which is only observable by calling.</summary>
        internal string SetupStatusLineForSelfTest() { return SetupStatusLine(); }

        internal string[] AnimationChoicesForSelfTest(string pet) { return AnimationChoices(pet); }

        private string SetupStatusLine()
        {
            SetupReport report = _setupCache;
            if (report == null)
            {
                report = VsCodeSetup.Inspect(ArgvPath, 250);
                _setupCache = report;
            }
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
                if (RanInline)
                    setup += "  \u26a0 the UI thread was never captured, so host calls are "
                             + "running on a worker; please report this.";
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
                        ? "Auto-approve: on, but cannot see the agent panel"
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
                              && SelfCheckCodexOptions(probe)
                              && SelfCheckCodexMode(probe)
                              && SelfCheckCodexWatch(probe)
                              && SelfCheckExplainedPruned(probe)
                              && SelfCheckTeardown(probe)
                              && SelfCheckFoldEquivalence(probe)
                              && SelfCheckCursorResets(probe)
                              && SelfCheckScanEquivalence(probe)
                              && SelfCheckWatchSection(probe)
                              && SelfCheckScreenPrompt(probe)
                              && SelfCheckSavedSettingsReachInit(probe)
                              && SelfCheckShortSessionId(probe)
                              && SelfCheckAnimatesChosenPet(probe)
                              && SelfCheckPaneDoesNoRepeatWork(probe)
                              && SelfCheckPostToUi(probe)
                              && SelfCheckNoRuleFilesIsSaid(probe)
                              && SelfCheckActiveTranscriptsAgrees(probe)
                              && SelfCheckRefusalPrivacy(probe)
                              && SelfCheckCodexTransport(probe)
                              && SelfCheckCapabilityLog(probe)
                              && SelfCheckPetChoices(probe)
                              && SelfCheckCacheBound(probe);
                    // `ok` is deliberately NOT asserted. It is the && of every group above, and
                    // 38 of the 39 groups end in a literal `return true`, while a group that threw
                    // is caught upstream and never reaches this line -- so `probe.Check("every
                    // logic group ran", ok)`, which used to sit here, was true by construction and
                    // asserted nothing. It was the assertion most likely to be read as "the suite
                    // ran", which is what made it worth removing rather than leaving.
                    //
                    // What IS checkable is the defect that would actually cost coverage: a group
                    // declared and never wired. See DeclaredSelfCheckMethods.
                    probe.Check("WITNESS every SelfCheck group declared on this type is wired "
                                + "into the chain above (" + DeclaredSelfCheckMethods()
                                    .ToString(CultureInfo.InvariantCulture) + " declared, "
                                + SelfCheckGroupCount.ToString(CultureInfo.InvariantCulture)
                                + " expected)",
                        DeclaredSelfCheckMethods() == SelfCheckGroupCount);

                    OptionsPane pane = host.OptionsPanes[0];
                    pane.Save(new Dictionary<string, string>
                    {
                        { SettingEnabled, "true" }, { SettingThreshold, "45" },
                        { SettingCooldown, "90" }, { SettingWatchClaude, "true" },
                        { SettingWatchCodex, "false" }, { SettingAnimate, "true" },
                    });
                    IReadOnlyDictionary<string, string> after = Shown(pane);
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
                    IReadOnlyDictionary<string, string> clamped = Shown(pane);
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
                // 2.1.280. Found by agentflow_classifier.py --audit, not by inspection.
                "Yes, and use auto mode",
            };
            PromptDecision never = PromptOptions.Choose(everyModeChange);
            probe.Check("WITNESS a prompt of ONLY mode changes presses nothing",
                !never.WillPress);

            // ---- the 2.1.280 plan prompt, whole -------------------------------------
            // Its approve row is "Yes, and use auto mode" and its reject row is "Send
            // feedback and keep planning" -- BOTH new, and until they were listed the
            // prompt held two unrecognised options, so auto-approve refused every plan
            // prompt and blamed the capture for it.
            probe.Check("the 2.1.280 plan rows are recognised",
                KindOf("Yes, and use auto mode") == OptionKind.ModeChange
                && KindOf("Send feedback and keep planning") == OptionKind.Reject);
            // Recognised is NOT pressable. There is no approve-once row on this prompt,
            // and the nearest thing to one changes the permission mode permanently.
            probe.Check("WITNESS the plan prompt is understood and still not pressed",
                !PromptOptions.Choose(new List<string>
                    { "Yes, and use auto mode", "Send feedback and keep planning" })
                    .WillPress);

            // ---- a prompt that is not ours is NOT a fault ---------------------------
            // The four refusals are told apart as VALUES, because the caller has to branch
            // on them and a reason string is prose, not a protocol.
            probe.Check("WITNESS the four refusals are distinguishable",
                PromptOptions.Choose(new List<string>()).Refusal == RefusalKind.NoOptions
                && PromptOptions.Choose(new List<string> { "Yes", "Never heard of it" })
                       .Refusal == RefusalKind.Unrecognised
                && PromptOptions.Choose(new List<string> { "Yes", "Allow", "No" })
                       .Refusal == RefusalKind.Ambiguous
                && PromptOptions.Choose(new List<string>
                       { "Yes, and use auto mode", "No, keep planning" })
                       .Refusal == RefusalKind.NothingToPress);
            // A pressed prompt refuses nothing, so the field cannot be left set by accident.
            probe.Check("WITNESS a prompt that IS pressed reports no refusal",
                PromptOptions.Choose(new List<string> { "Yes", "No" }).Refusal
                    == RefusalKind.None);

            // The whole 2.1.280 plan prompt, through Decide, on a closed port so nothing can
            // be pressed even if the logic were wrong.
            const int ClosedPort = 1;
            var planRows = new[]
            {
                "Yes, and use auto mode", "Yes, and manually approve edits",
                "Send feedback and keep planning",
            };
            string planNote = Decide(ClosedPort, Header2("Accept this plan?", planRows),
                                     new PressBudget(), false);
            probe.Check("WITNESS a plan prompt reads as a notification, not as a refusal",
                planNote != null
                && planNote.StartsWith("a prompt is waiting", StringComparison.Ordinal)
                && planNote.IndexOf("refused", StringComparison.Ordinal) < 0);
            probe.Check("...and it still names what is waiting, so the notice is useful",
                planNote.IndexOf("a plan", StringComparison.Ordinal) >= 0);
            // The other half of the split: a REAL fault must still announce itself as one.
            // Without this, softening the benign case could soften everything.
            string faultNote = Decide(ClosedPort,
                Header2("Accept this plan?", new[] { "Yes", "Never heard of it" }),
                new PressBudget(), false);
            probe.Check("WITNESS an unrecognised option is still reported as a refusal",
                faultNote != null
                && faultNote.StartsWith("refused:", StringComparison.Ordinal));

            // "Submit answers" is DELIBERATELY absent from the table: it submits the
            // user's answers to a question rather than approving a call they asked for.
            // Unknown is the intended classification, and one unknown refuses the whole
            // prompt -- so this asserts a decision, not an accident. If a NeverPress kind
            // is ever added, this is the assertion that has to change with it.
            probe.Check("WITNESS the question prompt's submit row is never pressed",
                KindOf("Submit answers") == OptionKind.Unknown
                && !PromptOptions.Choose(new List<string> { "Submit answers", "No" })
                    .WillPress);
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

                // Seeded OFF so Init does NOT scan: OnTick begins "if (!Enabled) return;", so a
                // disabled module starts no worker and this test has no background task to race.
                //
                // That is the third shape of the same mistake. The first version asserted ZERO
                // lines after Init, which was a claim about scheduling: it held here, where the
                // scan reads thousands of transcripts and loses, and failed on CI, where there
                // are none and it wins. The second waited for the scan instead -- correct in
                // principle, and still a timeout, so it went red here the moment the machine was
                // busy enough for the scan to outlast the wait. An assertion about when a
                // background task finishes cannot be made deterministic by choosing a better
                // number; the fix is to not start one.
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);

                var module = new AgentFlowModule();
                module.Init(host);

                probe.Check("WITNESS a module that is not scanning claims nothing about capability",
                    CapabilityLines(host) == 0);

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
                        .IndexOf("cannot see the agent panel", StringComparison.Ordinal) >= 0);

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
                    Shown(pane)[SettingMode] == AgentMode.ToDisplay(AgentMode.Notify));
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
                    Shown(pane)[SettingMode] == AgentMode.ToDisplay(AgentMode.AutoApprove));

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
            probe.Check("the five read outcomes are told apart",
                CdpApprover.Interpret(null) == ReadOutcome.NoAnswer
                && CdpApprover.Interpret("unreachable") == ReadOutcome.Unreachable
                && CdpApprover.Interpret("none") == ReadOutcome.NoPrompt
                && CdpApprover.Interpret("blind") == ReadOutcome.Blind
                && CdpApprover.Interpret("{\"options\":[]}") == ReadOutcome.Prompt);

            // BUG-006's second half. A card on screen that yields no options is NOT the idle
            // case, and until 1.4.2 both spelled themselves 'none' -- so the log said the same
            // nothing whether the module was working or had gone blind.
            probe.Check("WITNESS an unreadable card is not reported as an empty screen",
                CdpApprover.Interpret("blind") != ReadOutcome.NoPrompt
                && CdpApprover.Interpret("blind") != ReadOutcome.Prompt);
            // Both readers have to be able to SAY it, or the outcome exists and never occurs.
            string claudeRead = CdpApprover.ReadExpressionForSelfTest;
            string codexRead = CdpApprover.CodexReadExpressionForSelfTest;
            probe.Check("WITNESS both readers can report a card they could not read",
                claudeRead.IndexOf("'blind'", StringComparison.Ordinal) >= 0
                && codexRead.IndexOf("'blind'", StringComparison.Ordinal) >= 0);
            // The distinction is only worth anything if the TOP-LEVEL miss still says 'none':
            // an editor with no prompt in it must not cry blind every ten seconds.
            probe.Check("WITNESS a panel with no card at all still reports nothing waiting",
                claudeRead.IndexOf("if (!c) return 'none'", StringComparison.Ordinal) >= 0
                && codexRead.IndexOf("if (!card) return 'none'", StringComparison.Ordinal) >= 0);

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

            // Only wider grants on offer, so nothing here approves ONE call.
            //
            // Asserted on the out-param, not on the wording. The wording changed in 1.4.4:
            // this case now reads as a notification rather than as a refusal, because a
            // prompt that is simply not ours to answer is not a malfunction and should not
            // be described as one. The wording was a proxy for the thing that matters and
            // the proxy moved; NOT PRESSED is the thing that matters, so check that.
            bool pressedWider;
            string noApprove = Decide(ClosedPort, Fake(
                new[] { "Yes, and don't ask again", "Yes, and auto-accept" }), budget,
                false, false, out pressedWider);
            probe.Check("WITNESS a prompt offering only wider grants is never pressed",
                !pressedWider && noApprove != null
                && noApprove.IndexOf("approves a single call", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS ...and is reported as waiting rather than as a fault",
                noApprove.IndexOf("refused", StringComparison.Ordinal) < 0);

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

            // THE MOST COMMON PROMPT THERE IS, and it logged as "an unrecognised prompt" for
            // as long as this table has existed: the bundle renders the shell header as
            // `["Allow this ", commandLabel, " command?"]`, which is a template and so was
            // never a row. Observed in the maintainer's own log on 2026-09-23.
            probe.Check("WITNESS a shell prompt is named, not filed as unrecognised",
                DescribeSubject(Header("Allow this bash command?", "")) == "a shell command"
                && DescribeSubject(Header("Allow this PowerShell command?", ""))
                   == "a shell command");
            // ...and the template must not swallow the tools whose headers merely start the
            // same way. These are real rows with their own answers.
            probe.Check("WITNESS the shell template does not swallow its neighbours",
                DescribeSubject(Header("Allow this glob command", "")) == "a glob search"
                && DescribeSubject(Header("Allow this grep command", "")) == "a grep search"
                && DescribeSubject(Header("Allow this search", "")) == "a search");
            probe.Check("the remaining shapes read off bundle 2.1.280 are named",
                DescribeSubject(Header("Allow glob search in ?", "")) == "a glob search"
                && DescribeSubject(Header("Allow grep in ?", "")) == "a grep search"
                && DescribeSubject(Header("Allow fetching this url?", "")) == "a web fetch"
                && DescribeSubject(Header("Allow network connection to this host?", ""))
                   == "a network connection"
                && DescribeSubject(Header("Accept this plan?", "")) == "a plan"
                && DescribeSubject(Header("Continue planning", "")) == "a plan");
            // A REGRESSION GUARD ON THE TABLE, not a proof of the matcher: neither key is a
            // prefix of the other, so first-match answers these identically. Said plainly
            // because this assertion was written as though it proved longest-match, and the
            // mutation that turns longest-match into first-match survives it.
            probe.Check("the two searching headers are different tools",
                DescribeSubject(Header("Allow searching in ?", "")) == "a search"
                && DescribeSubject(Header("Allow searching for this query?", ""))
                   == "a web search");

            // LONGEST-MATCH, pinned properly. No key in the real table is a prefix of another,
            // so the strategy is invisible to every header the bundle can produce; a synthetic
            // collision is the only thing that can separate the candidate rules. BOTH
            // declaration orders, because one order lets first-match pass by luck and the
            // other lets last-match pass by luck.
            var collide = new[]
            {
                new KeyValuePair<string, string>("allow searching", "the short one"),
                new KeyValuePair<string, string>("allow searching in", "the long one"),
            };
            var collideReversed = new[] { collide[1], collide[0] };
            probe.Check("WITNESS the longest matching prefix wins, whatever the row order",
                MatchHeader("allow searching in x?", collide) == "the long one"
                && MatchHeader("allow searching in x?", collideReversed) == "the long one");
            probe.Check("WITNESS ...and a header matching no row is not named",
                MatchHeader("grant everlasting access", collide) == null
                && MatchHeader(null, collide) == null
                && MatchHeader("allow searching", null) == null);
            // A removed path span leaves a double space behind. Without collapsing, the
            // header no longer starts with the row it obviously matches.
            probe.Check("WITNESS the gap a removed path leaves does not defeat the match",
                DescribeSubject(Header("make this edit to   ?", "cs")) == "an edit (.cs)");
            // Found by docs/agentflow/agentflow_headers.py --audit on its first run, after
            // the hand-derived table had been called complete. Kept as an assertion so the
            // row cannot be removed again on the same reasoning that left it out.
            probe.Check("WITNESS even the degenerate question header is named",
                DescribeSubject(Header("No questions provided", "")) == "a question");
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
        /// <summary>A prompt with a header AND options, for the Decide-level assertions.</summary>
        private static PromptView Header2(string header, string[] options)
        {
            var view = new PromptView { TargetId = "t1", ToolName = "", UnsafeHeader = header };
            foreach (string option in options) { view.Options.Add(option); view.Disabled.Add(false); }
            return view;
        }

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

            // ---- the rate limit, which is now the USER'S NUMBER ----------------------
            // Default first, because that is what decides whether the module interferes with
            // ordinary work out of the box. The owner's ruling on 2026-09-22 was that on means
            // on, after a hard-coded cap of 10 per five minutes stood the module down mid-session
            // with two agents running: "it should be unlimited, it is either on or off -- if you
            // want a budget it should be another option".
            probe.Check("WITNESS the default approval limit is the MAXIMUM, so a fresh install "
                        + "never stands itself down on a busy session",
                new PressBudget().PressLimit == PressBudget.MaxPressLimit
                && PressBudget.DefaultPressLimit == PressBudget.MaxPressLimit);

            const int Chosen = 4;
            var rate = new PressBudget();
            rate.SetPressLimit(Chosen);
            int pressed = 0;
            for (int i = 0; i < Chosen + 5; i++)
            {
                // A fresh signature each time, so ONLY the rate limit can stop this.
                if (rate.TryPress(PromptSignature("tool" + i.ToString(CultureInfo.InvariantCulture)),
                                  t0.AddSeconds(i), out refusal))
                    pressed++;
            }
            probe.Check("WITNESS a limit the user set is the limit that applies",
                pressed == Chosen);
            probe.Check("...and the refusal points at the setting rather than at a mystery",
                refusal != null
                && refusal.IndexOf("approval limit", StringComparison.Ordinal) >= 0);

            // Nonsense in settings.json cannot switch the cap off or invert it.
            var clamped = new PressBudget();
            clamped.SetPressLimit(0);
            probe.Check("WITNESS a zero limit clamps up rather than blocking everything",
                clamped.PressLimit == PressBudget.MinPressLimit);
            clamped.SetPressLimit(int.MaxValue);
            probe.Check("...and an absurd one clamps down to the maximum",
                clamped.PressLimit == PressBudget.MaxPressLimit);

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
            // ⚠ THIS PAIR OF LINES USED TO PIN THE BUG AS CORRECT BEHAVIOUR. The second one
            // read "...and the identical prompt is the same prompt" and asserted that
            // Signature("Bash", {Yes, No}) equals itself -- which is both unfalsifiable and, worse,
            // a statement that two DIFFERENT compound commands are one prompt. They sign
            // identically because a compound command gets no wider-grant row to tell it apart,
            // and that is not the rare case: a compound command can never be wildcarded, so it is
            // precisely the kind that always prompts. Three of them in a row latched the module
            // off on 2026-09-22.
            //
            // The signature is still label-based, deliberately -- the reader keeps command text
            // out of PromptView on purpose. What changed is that a CONFIRMED click now clears the
            // repeat counter, so the collision no longer accumulates. That is asserted here.
            var distinct = new PressBudget();
            string compound = PressBudget.Signature("Bash", new[] { "Yes", "No" });
            int through = 0;
            for (int i = 0; i < PressBudget.MaxIdenticalPresses + 4; i++)
            {
                if (distinct.TryPress(compound, t0.AddSeconds(i * 10), out refusal)) through++;
                distinct.NotePromptCleared();   // what a confirmed click now reports
            }
            probe.Check("WITNESS a run of compound-command prompts, which all sign alike, is not "
                        + "mistaken for one stuck prompt once each click is confirmed",
                through == PressBudget.MaxIdenticalPresses + 4);

            // And the guard still works for the thing it is FOR: clicks that never land.
            var stuckForReal = new PressBudget();
            for (int i = 0; i < PressBudget.MaxIdenticalPresses; i++)
                stuckForReal.TryPress(compound, t0.AddSeconds(i * 10), out refusal);
            probe.Check("WITNESS ...while a click that never lands still stands the module down",
                !stuckForReal.TryPress(compound, t0.AddSeconds(99), out refusal)
                && refusal != null
                && refusal.IndexOf("not taking", StringComparison.Ordinal) >= 0);

            // ---- and it has to be WIRED IN, not merely correct -----------------------
            // Everything above passes just as well when Decide never calls it. This is the
            // assertion that fails if the guard is bypassed, which is the only way it ever
            // gets bypassed: by someone deleting four lines that looked defensive.
            var spent = new PressBudget();
            spent.SetPressLimit(PressBudget.MinPressLimit);
            for (int i = 0; i < PressBudget.MinPressLimit; i++)
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

            // ...and passing no collector keeps the old behaviour exactly.
            //
            // CANNOT-FAIL ASSERTION REMOVED HERE. The previous version built an empty
            // List<ApprovalEntry>, passed `null` to ApprovedSince rather than the list, and then
            // asserted the list was empty -- true by construction, unfalsifiable by any change to
            // the method, and it left the actual claim untested. The claim is that a null
            // collector changes nothing about the TALLY, so it is now asserted against the tally
            // the collector run produced.
            Dictionary<string, int> withoutCollector = BlockedDetector.ApprovedSince(
                OneApprovedCall(Secret), rules,
                new HashSet<string>(StringComparer.Ordinal), null);
            bool sameTally = withoutCollector.Count == tally.Count;
            foreach (KeyValuePair<string, int> entry in tally)
            {
                int other;
                if (!withoutCollector.TryGetValue(entry.Key, out other) || other != entry.Value)
                    sameTally = false;
            }
            probe.Check("WITNESS a null collector leaves the tally identical, and the tally is "
                        + "not empty, so neither half of that is vacuous",
                sameTally && tally.Count > 0);
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
            // A DIFFERENT branch, on its own fixture. PetAnimations.cs:84 drops an animation with
            // no <name> child at all; :88 drops one whose name is empty. The previous version
            // asserted `names.Count == 3` twice in a row against the same unmodified list, the
            // second time labelled as though it checked this branch -- so deleting either skip
            // failed the same single assertion and the second label was decoration.
            probe.Check("WITNESS an animation with no name ELEMENT is skipped, which is not the "
                        + "same branch as the empty-name skip",
                PetAnimations.FromXml("<animations><animations>"
                    + "<animation><id>7</id></animation>"
                    + "</animations></animations>").Count == 0);
            probe.Check("the list is sorted, so the dropdown is not in file order",
                names[0] == "sit");

            probe.Check("WITNESS unreadable pet XML contributes nothing and does not throw",
                PetAnimations.FromXml("<animations><not closed").Count == 0
                && PetAnimations.FromXml("").Count == 0
                && PetAnimations.FromXml(null).Count == 0);

            // The chosen name leads, then the coverage fallbacks -- so a pet that has been
            // swapped since the choice was made degrades instead of doing nothing.
            IReadOnlyList<string> candidates = PetAnimations.Candidates("wave");
            probe.Check("WITNESS the chosen animation is tried first",
                candidates.Count > 1 && candidates[0] == "wave");
            probe.Check("...and a fallback follows it, so a swapped pet still animates",
                candidates.Count > 1 && candidates[1] != "wave");
            probe.Check("choosing no animation still yields the coverage list",
                PetAnimations.Candidates(PetAnimations.AnyPet).Count > 1);

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
        /// <summary>
        /// Codex's prompt vocabulary, and the opt-in for its wider row.
        ///
        /// Everything here was read off a LIVE Codex prompt over CDP on 2026-09-21 rather than
        /// transcribed from a bundle: the button renders "Allow once \u23CE" with the keyboard hint
        /// inside the label, the deny row renders "Deny Esc", and the dropdown behind the split
        /// button carries "Allow once" and "Allow similar commands" as menuitems WITHOUT the hint.
        /// Both spellings therefore have to classify the same way, which is what the stripper is
        /// for and the first two checks are about.
        /// </summary>
        /// <summary>
        /// The Codex transport: which targets it reaches, and two rules about the expressions.
        ///
        /// Source-text checks, because a JS string cannot be executed here. They assert the two
        /// decisions that would be silent if reversed, not that the code exists.
        /// </summary>
        private static bool SelfCheckCodexTransport(SelfTestProbe probe)
        {
            probe.Check("WITNESS Codex targets are identified by their own extension, not Claude's",
                CdpApprover.CodexTargetMarker != CdpApprover.ClaudeTargetMarker
                && CdpApprover.CodexTargetMarker.IndexOf("openai", StringComparison.Ordinal) >= 0);

            string read = CdpApprover.CodexReadExpressionForSelfTest;
            string click = CdpApprover.CodexClickExpressionForSelfTest;

            // The guard for the bug that reached a live prompt. The click used to be a
            // string.Format format string with DocumentPrelude concatenated in, and the prelude's
            // braces are single, so EVERY press threw FormatException before it could click. On
            // screen that read as "cannot see the panel" and nothing else, because a sweep that
            // threw was silent -- two defects stacked, and the quiet one hid the loud one.
            string built = CdpApprover.BuildCodexClick(1, "Allow once");
            probe.Check("WITNESS building the Codex click substitutes both tokens",
                built.IndexOf("__INDEX__", StringComparison.Ordinal) < 0
                && built.IndexOf("__LABEL__", StringComparison.Ordinal) < 0);
            probe.Check("...and puts the index and the label where they belong",
                built.IndexOf("var idx = 1;", StringComparison.Ordinal) >= 0
                && built.IndexOf("Allow once", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS no unreplaced format placeholder survives into the expression",
                built.IndexOf("{0}", StringComparison.Ordinal) < 0
                && built.IndexOf("{1}", StringComparison.Ordinal) < 0);

            // BUG-006, and the assertion that used to sit here asserted the bug: "the reader
            // anchors on the split button's aria-label" was true, was the defect, and passed.
            // Codex renders that split button only when it has a wider grant to offer, so the
            // reader answered 'none' -- indistinguishable from an idle editor -- on every
            // two-button prompt. Measured against a live one on 2026-09-23.
            probe.Check("the reader anchors on the approval CARD, not on the optional dropdown",
                read.IndexOf("@container/approval-card", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS a prompt with no dropdown is still READ, not abandoned",
                read.IndexOf("if (!trigger) return 'none'", StringComparison.Ordinal) < 0);
            probe.Check("WITNESS the clicker does not abandon it either",
                click.IndexOf("if (!trigger) return 'gone'", StringComparison.Ordinal) < 0);
            // Read and click must count the same buttons or an index means two different rows.
            probe.Check("WITNESS both expressions anchor on the same element",
                click.IndexOf("@container/approval-card", StringComparison.Ordinal) >= 0);
            probe.Check("...and both scope the options to the card's own form",
                read.IndexOf("card.querySelector('form')", StringComparison.Ordinal) >= 0
                && click.IndexOf("card.querySelector('form')", StringComparison.Ordinal) >= 0);
            // Optional, but still recognised: the split-button shape has to keep working.
            probe.Check("WITNESS the dropdown is still found when the card does render one",
                read.IndexOf("Approval options", StringComparison.Ordinal) >= 0
                && click.IndexOf("Approval options", StringComparison.Ordinal) >= 0);

            // The trigger carries no text. Returned as an option it would classify Unknown, and
            // one unknown option refuses the whole prompt -- so Codex would be permanently
            // unactionable, looking like a classifier fault rather than a reader one.
            probe.Check("WITNESS the reader drops the menu trigger BY IDENTITY, not by blank text",
                read.IndexOf("b === trigger", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS the clicker drops it the same way, or an index means two things",
                click.IndexOf("!== trigger", StringComparison.Ordinal) >= 0);

            // THE HARD RULE, and it is here because it was learned the expensive way. Escape in
            // the Codex dialog is DENY -- the row literally reads "Deny Esc" -- so a keystroke sent
            // to "just close the menu" rejects the user's command. That happened once, by hand,
            // on 2026-09-21. Nothing this module sends to that panel may be a key.
            string[] keyboard = { "KeyboardEvent", "keydown", "keyup", "keypress", "Escape" };
            foreach (string banned in keyboard)
            {
                probe.Check("WITNESS the Codex reader sends no keyboard event (" + banned + ")",
                    read.IndexOf(banned, StringComparison.Ordinal) < 0);
                probe.Check("WITNESS the Codex clicker sends no keyboard event (" + banned + ")",
                    click.IndexOf(banned, StringComparison.Ordinal) < 0);
            }
            return true;
        }

        /// <summary>
        /// Codex's permission policy is READ now, and still not acted on.
        ///
        /// Phase one of closing the watch gap: the reader learns where Codex keeps the field, so
        /// the stand-down can say something true. What does NOT change is what fires -- precision
        /// for Codex is unmeasured, and the allow-list of exactly "default" is the thing standing
        /// between this module and guessing.
        /// </summary>
        /// <summary>
        /// The teardown rule, and the privacy split that had no reader.
        ///
        /// A stale NOTIFY is noise. A stale PRESS is an action taken on someone's behalf after
        /// they switched the thing off, and stopping the timer does not prevent it: the poll is
        /// already on a worker and ends in a click.
        /// </summary>
        /// <summary>What the host would show for this pane: LoadPending with nothing pending.
        ///
        /// The assertions used to call pane.Load(), which the host never calls once a module
        /// supplies LoadPending -- so they were proving things about a code path production does
        /// not take. That reads like coverage and is worse than none.</summary>
        private static IReadOnlyDictionary<string, string> Shown(OptionsPane pane)
        {
            return pane.LoadPending(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        /// <summary>
        /// The watch section says whether it is doing anything, and every Info row it declares
        /// actually has a value.
        ///
        /// An Info field renders its LOAD VALUE, not its Label, so a row declared in the schema
        /// and missing from Load renders EMPTY -- a blank line in the pane with nothing to explain
        /// it. That is the failure this whole section was added to prevent, so it would be a poor
        /// joke to reintroduce it here.
        /// </summary>
        /// <summary>
        /// Telling the user about a prompt that is ON SCREEN and unpressed.
        ///
        /// The point of this path is that it is an observation rather than a prediction, so the
        /// thing worth pinning is that it fires ONCE per prompt and re-arms when the screen goes
        /// quiet. A notifier that repeats every poll is worse than none: it trains the user to
        /// ignore it, and this one speaks out loud.
        /// </summary>
        /// <summary>
        /// Two settings that persisted perfectly and then did nothing, which is worse than not
        /// persisting: the pane shows the saved value back, so there is nothing to notice.
        /// </summary>
        private static bool SelfCheckSavedSettingsReachInit(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-init"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init
                host.SettingsFor("agentflow").Set(SettingCooldown, "300");
                probe.Check("WITNESS the saved cooldown is not the default it would be confused with",
                    300.0 != NotifyBudget.DefaultCooldownSeconds);

                var module = new AgentFlowModule();
                module.Init(host);
                probe.Check("WITNESS a cooldown saved last week is live at the NEXT launch, "
                            + "not only after the pane is saved again",
                    module._budget.CooldownSeconds == 300.0);
                module.Shutdown();
            }
            return true;
        }

        /// <summary>
        /// Session ids in the log have to distinguish sessions, and for Codex they did not.
        /// </summary>
        private static bool SelfCheckShortSessionId(SelfTestProbe probe)
        {
            var codexOne = new AgentSession
            {
                SessionId = "rollout-2026-09-21T08-13-43-01a0c487-b562-7013-aaf8-9f619e2ae5fa",
            };
            var codexTwo = new AgentSession
            {
                SessionId = "rollout-2026-09-21T08-14-27-01a0c488-6153-7eb3-a58d-efa00d079ecd",
            };
            probe.Check("WITNESS two Codex sessions are told apart in the log",
                Short(codexOne) != Short(codexTwo));
            probe.Check("WITNESS ...which the leading characters could not do, being the "
                        + "literal filename prefix every Codex transcript shares",
                codexOne.SessionId.Substring(0, 8) == codexTwo.SessionId.Substring(0, 8));

            var claude = new AgentSession { SessionId = "d5d95e35-1b61-45ed-92e3-76dd5d60d9ab" };
            probe.Check("a Claude session still reads as the head of its uuid",
                Short(claude) == "d5d95e35");
            probe.Check("a short id is passed through whole",
                Short(new AgentSession { SessionId = "abc" }) == "abc");
            probe.Check("no session at all is not an exception", Short(null) == "?");
            return true;
        }

        /// <summary>
        /// The pane's pet dropdown has to change what happens, and for its whole life it could
        /// not: the pet was dropped on the floor and every animation went to every pet.
        ///
        /// The fake makes the two paths tell themselves apart. TryPlayAnimation records ONE name
        /// (the first that plays), PlayAnimationAll records the WHOLE candidate list -- so the
        /// count is the evidence for which one ran, without needing the fake to know about pets.
        /// </summary>
        private static bool SelfCheckAnimatesChosenPet(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-anim"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init
                host.SettingsFor("agentflow").Set(SettingAnimPet, "shimeji-cyn");
                host.SettingsFor("agentflow").Set(SettingAnimName, "wave");
                var module = new AgentFlowModule();
                module.Init(host);

                int coverage = PetAnimations.Candidates("wave").Count;
                probe.Check("WITNESS the candidate list is longer than one, so the two paths "
                            + "cannot be confused by their record count",
                    coverage > 1);

                host.RaiseCompanionSpawned(
                    new DesktopAICompanion.ModuleKit.Testing.FakeCompanion(1, "shimeji-cyn"));
                host.PlayedAnimations.Clear();
                module.PlayChosenAnimation();
                probe.Check("WITNESS the chosen animation goes to the chosen pet ALONE",
                    host.PlayedAnimations.Count == 1 && host.PlayedAnimations[0] == "wave");

                // A pet of a DIFFERENT type on screen must not collect it.
                host.PlayedAnimations.Clear();
                host.SettingsFor("agentflow").Set(SettingAnimPet, "shimeji-nobody");
                module.PlayChosenAnimation();
                probe.Check("WITNESS a choice whose pet is not on screen falls back to every pet "
                            + "rather than to silence",
                    host.PlayedAnimations.Count == coverage);

                // And the explicit any-pet choice still means every pet.
                host.PlayedAnimations.Clear();
                host.SettingsFor("agentflow").Set(SettingAnimPet, PetAnimations.AnyPet);
                module.PlayChosenAnimation();
                probe.Check("choosing any pet still reaches every pet",
                    host.PlayedAnimations.Count == coverage);

                module.Shutdown();
            }
            return true;
        }

        /// <summary>
        /// Neither of the pane's two per-load costs may touch the disk or a socket twice.
        ///
        /// Both were measured, not guessed: Inspect is 273-285 ms against a closed port because
        /// the probe inside it bounds a two-second connect, and it ran on the UI THREAD on every
        /// pane open and every dropdown change. The animation list was read and parsed twice per
        /// load, once for the dropdown's options and once to pick its selected entry.
        ///
        /// Reference equality is the assertion for the memo, deliberately. "Same contents" would
        /// pass against a function that recomputed identical contents, which is the exact thing
        /// being removed.
        /// </summary>
        private static bool SelfCheckPaneDoesNoRepeatWork(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-cache"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init
                var module = new AgentFlowModule();
                module.Init(host);

                // 1. The status line comes from the POLL's cache, not from a fresh Inspect. Seeded
                //    with a state the real machine cannot be in, so a line matching it could only
                //    have come from here.
                module._setupCache = new SetupReport
                {
                    State = SetupState.Unreadable,
                    Path = "seeded-argv.json",
                    Detail = "seeded by the self-test",
                };
                string line = module.SetupStatusLineForSelfTest();
                probe.Check("WITNESS the pane's status line is rendered from the cached inspection",
                    line != null
                    && line.IndexOf("seeded-argv.json", StringComparison.Ordinal) >= 0);

                // 2. ...and a later poll replaces it, so the cache cannot go stale for ever.
                module._setupCache = new SetupReport { State = SetupState.Listening, Port = 65000 };
                probe.Check("WITNESS ...and a fresh inspection replaces it",
                    module.SetupStatusLineForSelfTest().IndexOf(
                        "65000", StringComparison.Ordinal) >= 0);

                // 3. The animation list is computed once per pet, not once per caller.
                string[] first = module.AnimationChoicesForSelfTest(PetAnimations.AnyPet);
                string[] again = module.AnimationChoicesForSelfTest(PetAnimations.AnyPet);
                probe.Check("WITNESS asking twice for one pet's animations does not recompute",
                    ReferenceEquals(first, again));
                probe.Check("...and the list is not empty, so the memo is memoising something",
                    first.Length > 0);

                // 4. A different pet must not be served the previous pet's list.
                string[] other = module.AnimationChoicesForSelfTest("shimeji-cyn");
                probe.Check("WITNESS a different pet recomputes rather than reusing the memo",
                    !ReferenceEquals(first, other));

                // 5. A FAILED LOOKUP IS NOT REMEMBERED. This is the one the original memo got
                //    wrong and this test did not catch, because every case above uses (any
                //    pet), where the coverage list IS the answer and caching it is correct.
                //    With no companion manager the lookup CANNOT succeed, so each call must
                //    recompute -- a cache hit here is the v1.2.4 defect, where the animation
                //    dropdown showed the generic seven for a pet with its own list until you
                //    changed the pet and changed it back.
                string[] failedOnce = module.AnimationChoicesForSelfTest("shimeji-nobody");
                string[] failedTwice = module.AnimationChoicesForSelfTest("shimeji-nobody");
                probe.Check("WITNESS a lookup that could not succeed is not cached, so it is "
                            + "retried rather than pinned for the life of the pane",
                    !ReferenceEquals(failedOnce, failedTwice));
                probe.Check("...and it still answers with the coverage list rather than nothing",
                    failedOnce.Length > 0 && failedTwice.Length == failedOnce.Length);

                module.Shutdown();
            }
            return true;
        }

        /// <summary>
        /// ActiveTranscripts switched from Directory.EnumerateFiles + a stat per file to
        /// DirectoryInfo.EnumerateFiles, which is 2.7x faster because the write time arrives with
        /// the enumeration. Faster is only worth having if it gives the SAME answer, so the old
        /// implementation is kept here as the oracle and the two are compared on a fixture built
        /// to exercise every branch: in-window, out-of-window, a skipped directory, and a nested
        /// directory (the skip must match the immediate parent only).
        ///
        /// A differential test is only as strong as the axes its fixture varies, so the axes are
        /// asserted too -- if every file landed in the window, the cutoff branch would be
        /// untested and this would pass while doing nothing.
        /// </summary>
        private static bool SelfCheckActiveTranscriptsAgrees(SelfTestProbe probe)
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-enum-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                string nested = System.IO.Path.Combine(root, "project-a");
                string skipped = System.IO.Path.Combine(root, "subagents");
                string deepSkipName = System.IO.Path.Combine(nested, "subagents");
                System.IO.Directory.CreateDirectory(nested);
                System.IO.Directory.CreateDirectory(skipped);
                System.IO.Directory.CreateDirectory(deepSkipName);

                DateTime now = DateTime.UtcNow;
                string fresh = System.IO.Path.Combine(nested, "fresh.jsonl");
                string stale = System.IO.Path.Combine(nested, "stale.jsonl");
                string hidden = System.IO.Path.Combine(skipped, "agent.jsonl");
                string deepHidden = System.IO.Path.Combine(deepSkipName, "agent.jsonl");
                string other = System.IO.Path.Combine(root, "loose.txt");
                foreach (string p in new[] { fresh, stale, hidden, deepHidden })
                    System.IO.File.WriteAllBytes(p, utf8.GetBytes("{}" + "\n"));
                System.IO.File.WriteAllBytes(other, utf8.GetBytes("not a transcript"));
                // Out of the 900 s window by a wide margin, set explicitly rather than waited for.
                System.IO.File.SetLastWriteTimeUtc(stale, now.AddSeconds(-5000));

                List<string> actual = TranscriptReader.ActiveTranscripts(root, 900.0, "subagents");
                List<string> oracle = OracleActiveTranscripts(root, 900.0, "subagents");

                probe.Check("WITNESS the fixture varies the window axis, so the cutoff is exercised",
                    System.IO.File.GetLastWriteTimeUtc(stale) < now.AddSeconds(-900.0)
                    && System.IO.File.GetLastWriteTimeUtc(fresh) >= now.AddSeconds(-900.0));
                probe.Check("WITNESS ...and the skip axis, at two different depths",
                    System.IO.File.Exists(hidden) && System.IO.File.Exists(deepHidden));

                probe.Check("WITNESS the fast enumeration agrees with the old one exactly",
                    string.Join("|", actual.ToArray()) == string.Join("|", oracle.ToArray()));
                probe.Check("WITNESS ...and the answer is the one a human would give: "
                            + "the fresh file, and only it",
                    actual.Count == 1
                    && actual[0].EndsWith("fresh.jsonl", StringComparison.OrdinalIgnoreCase));

                // A missing root is not an exception, on either implementation.
                probe.Check("a root that does not exist yields nothing rather than throwing",
                    TranscriptReader.ActiveTranscripts(root + "-nope", 900.0, null).Count == 0);
            }
            catch (Exception ex) { probe.Check("active transcripts: " + ex.Message, false); }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
            return true;
        }

        /// <summary>
        /// The PREVIOUS implementation, kept only as the differential oracle above. Two syscalls
        /// per file, which is exactly why it was replaced; correctness is not in question, which
        /// is exactly why it makes a good oracle.
        /// </summary>
        private static List<string> OracleActiveTranscripts(string root, double windowSeconds,
                                                            string skipDirectoryName)
        {
            var found = new List<KeyValuePair<DateTime, string>>();
            if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
                return new List<string>();
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);
            foreach (string path in System.IO.Directory.EnumerateFiles(
                         root, "*.jsonl", System.IO.SearchOption.AllDirectories))
            {
                if (!string.IsNullOrEmpty(skipDirectoryName))
                {
                    string parent = System.IO.Path.GetFileName(
                        System.IO.Path.GetDirectoryName(path) ?? string.Empty);
                    if (string.Equals(parent, skipDirectoryName, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                DateTime written;
                try { written = System.IO.File.GetLastWriteTimeUtc(path); }
                catch (System.IO.IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (written >= cutoff)
                    found.Add(new KeyValuePair<DateTime, string>(written, path));
            }
            found.Sort((left, right) => right.Key.CompareTo(left.Key));
            var paths = new List<string>(found.Count);
            foreach (KeyValuePair<DateTime, string> entry in found) paths.Add(entry.Value);
            return paths;
        }

        /// <summary>
        /// How many `SelfCheck*` groups the chain in SelfTest is expected to call.
        ///
        /// This constant is the LINK between two things reflection cannot bridge: it can enumerate
        /// the methods declared on this type, but it cannot see which ones the `&&` chain calls.
        /// So the count is asserted against the declarations, and adding a group means wiring it
        /// AND bumping this. Being made to touch it is the mechanism rather than an inconvenience:
        /// a group that is declared and never called looks exactly like coverage and runs never.
        /// </summary>
        private const int SelfCheckGroupCount = 40;

        /// <summary>Count the `SelfCheck*` methods this type declares. `DeclaredOnly` still sees
        /// every part of the partial class, because they compile into one type. Deliberately NOT
        /// named SelfCheck-anything, or it would count itself.</summary>
        private static int DeclaredSelfCheckMethods()
        {
            int found = 0;
            foreach (System.Reflection.MethodInfo method in typeof(AgentFlowModule).GetMethods(
                         System.Reflection.BindingFlags.Static
                         | System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.DeclaredOnly))
                if (method.Name.StartsWith("SelfCheck", StringComparison.Ordinal)) found++;
            return found;
        }

        /// <summary>A SynchronizationContext that runs the callback at once and counts it, so a
        /// test can tell "was POSTED" from "ran inline" without a message loop.</summary>
        private sealed class CountingSyncContext : SynchronizationContext
        {
            internal int Posted;
            public override void Post(SendOrPostCallback d, object state)
            {
                Posted++;
                d(state);
            }
        }

        /// <summary>
        /// Both branches of PostToUi, forced.
        ///
        /// Replaces an assertion that could not fail: `probe.Check("the UI thread was captured, so
        /// nothing ran inline", !module.RanInline)`. `_ranInline` is written in exactly one place,
        /// inside PostToUi, whose only production caller is the scan worker -- and every test that
        /// reached that line seeded mode Off precisely so no scan would start. So `!RanInline` was
        /// true by construction. Worse, the flag's meaning inverts in that context: it is set when
        /// NO SynchronizationContext was captured, which under --module-selftest (no message loop)
        /// is the expected state, so had the line ever been reached it would have failed for a
        /// reason with nothing to do with this module.
        ///
        /// A flag that is only ever asserted false proves nothing until something proves it can go
        /// true. Both halves are here.
        /// </summary>
        private static bool SelfCheckPostToUi(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ui"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init
                var module = new AgentFlowModule();
                module.Init(host);

                // 1. No context: the action must still be delivered, and the flag must SAY so.
                module.SetUiContextForSelfTest(null);
                bool inlineRan = false;
                module.PostToUiForSelfTest(delegate { inlineRan = true; });
                probe.Check("WITNESS with no context captured, PostToUi still delivers the action",
                    inlineRan);
                probe.Check("WITNESS ...and reports that it ran inline, which is the flag's whole "
                            + "job and is what makes asserting it false elsewhere mean anything",
                    module.RanInline);

                // 2. A context: the action is POSTED, and the flag stays clear. A fresh module,
                //    because the flag is one-way by design.
                var second = new AgentFlowModule();
                second.Init(host);
                var context = new CountingSyncContext();
                second.SetUiContextForSelfTest(context);
                bool postedRan = false;
                second.PostToUiForSelfTest(delegate { postedRan = true; });
                probe.Check("WITNESS with a context captured, the action goes through Post",
                    postedRan && context.Posted == 1);
                probe.Check("WITNESS ...and the inline flag stays clear, so the two routes are "
                            + "distinguishable rather than both reporting the same thing",
                    !second.RanInline);

                second.Shutdown();
                module.Shutdown();
            }
            return true;
        }

        /// <summary>
        /// "No rule file anywhere" must be distinguishable from "rules loaded, nothing matched".
        ///
        /// Driven through USERPROFILE rather than a seam, because RuleLoader.DefaultPaths derives
        /// its three paths from the profile directory -- so this exercises the path production
        /// takes, including the file probing, rather than a parallel one built for the test.
        ///
        /// Both directions are asserted. A test that only proved the note appears would pass
        /// against a Scan that emitted it unconditionally, which is the more likely mistake.
        /// </summary>
        private static bool SelfCheckNoRuleFilesIsSaid(SelfTestProbe probe)
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-rules-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            string homeWas = Environment.GetEnvironmentVariable(RuleLoader.HomeVariable);
            string claudeWas = Environment.GetEnvironmentVariable(TranscriptReader.ClaudeRootVariable);
            string codexWas = Environment.GetEnvironmentVariable(TranscriptReader.CodexRootVariable);
            try
            {
                string empty = System.IO.Path.Combine(root, "no-transcripts");
                System.IO.Directory.CreateDirectory(empty);
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, root);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, empty);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, empty);

                // 1. Nothing on disk: the note is emitted.
                var notes = new List<string>();
                Dictionary<string, int> approved;
                Scan(true, false, 30.0, new HashSet<string>(StringComparer.Ordinal),
                     out approved, null, null, notes);
                probe.Check("WITNESS with no settings file anywhere, the scan SAYS so rather "
                            + "than silently predicting nothing",
                    notes.Contains(NoRuleFilesNote));

                // 2. One readable rule file: the note must NOT be emitted. Without this the test
                //    would pass against a Scan that emitted it every time.
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".claude"));
                var utf8 = new System.Text.UTF8Encoding(false);
                System.IO.File.WriteAllBytes(
                    System.IO.Path.Combine(root, ".claude", "settings.json"),
                    utf8.GetBytes("{\"permissions\":{\"allow\":[\"Bash(git status)\"]}}"));
                var quiet = new List<string>();
                Scan(true, false, 30.0, new HashSet<string>(StringComparer.Ordinal),
                     out approved, null, null, quiet);
                probe.Check("WITNESS ...and stays quiet once one exists, so the note tracks the "
                            + "state instead of being unconditional",
                    !quiet.Contains(NoRuleFilesNote));
            }
            catch (Exception ex) { probe.Check("no-rule-files note: " + ex.Message, false); }
            finally
            {
                Environment.SetEnvironmentVariable(RuleLoader.HomeVariable, homeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, claudeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, codexWas);
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
            return true;
        }

        private static bool SelfCheckScreenPrompt(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-sp"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init
                var module = new AgentFlowModule();
                module.Init(host);

                // Off only to keep Init from starting a scan. Speech is gated on the mode too, so
                // switch to Notify now that Init is done -- otherwise this would assert that a
                // switched-off module stays quiet, which is not the property under test.
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Notify);
                host.SettingsFor("agentflow").Set(SettingNotifySpeak, "true");

                int Spoken() { return host.BroadcastLines.Count; }
                var first = new ScreenPrompt { Signature = "codex|Deny|Allow once", Subject = "a Codex command" };

                module.AnnounceScreenPrompt(first);
                int afterFirst = Spoken();
                probe.Check("WITNESS a prompt on screen is announced", afterFirst > 0);

                module.AnnounceScreenPrompt(first);
                module.AnnounceScreenPrompt(first);
                probe.Check("WITNESS the SAME prompt is not announced again on the next poll",
                    Spoken() == afterFirst);

                var second = new ScreenPrompt { Signature = "codex|Deny|Allow once|Allow similar", Subject = "a Codex command" };
                module.AnnounceScreenPrompt(second);
                probe.Check("a DIFFERENT prompt is announced", Spoken() > afterFirst);
                int afterSecond = Spoken();

                // Quiet, then THE SAME prompt that was just announced. It has to be the same one,
                // or the announcement is explained by the signature differing and the re-arm is
                // never exercised -- which is exactly how the first version of this passed while
                // the re-arm was mutated away.
                module.AnnounceScreenPrompt(null);
                module.AnnounceScreenPrompt(second);
                probe.Check("WITNESS the screen going quiet re-arms it, so the SAME prompt "
                            + "returning is announced again",
                    Spoken() > afterSecond);

                // Nothing spoken carries the command or an option label.
                foreach (string line in host.BroadcastLines)
                    probe.Check("WITNESS nothing spoken carries an option label",
                        line.IndexOf("Allow once", StringComparison.Ordinal) < 0
                        && line.IndexOf("Deny", StringComparison.Ordinal) < 0);

                // A PAUSED prompt must survive the pause. The guard used to be set before the
                // pause was checked, so the single announcement this prompt will ever get was
                // spent on a tick that delivered nothing: silenced once, silent for ever.
                var third = new ScreenPrompt { Signature = "codex|Deny|Allow always", Subject = "a Codex command" };
                module._budget.PauseForSelfTest(DateTime.UtcNow.AddMinutes(30));
                int beforePaused = Spoken();
                module.AnnounceScreenPrompt(third);
                probe.Check("WITNESS a paused companion says nothing", Spoken() == beforePaused);

                module._budget.PauseForSelfTest(DateTime.MinValue);
                module.AnnounceScreenPrompt(third);
                probe.Check("WITNESS ...and the SAME prompt is still announced once the pause ends",
                    Spoken() > beforePaused);

                module.Shutdown();
                int afterShutdown = Spoken();
                module.AnnounceScreenPrompt(new ScreenPrompt { Signature = "x", Subject = "y" });
                probe.Check("WITNESS nothing is announced after Shutdown",
                    Spoken() == afterShutdown);
            }
            return true;
        }

        private static bool SelfCheckWatchSection(SelfTestProbe probe)
        {
            probe.Check("WITNESS with every session standing down, it says so plainly",
                WatchStateLine(3, 3, false).IndexOf("SILENT", StringComparison.Ordinal) >= 0);
            probe.Check("...and names the mode that has to change for it to do anything",
                WatchStateLine(3, 3, false).IndexOf("default mode", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS a session that is NOT standing down reads as active",
                WatchStateLine(2, 1, false).IndexOf("active", StringComparison.Ordinal) >= 0
                && WatchStateLine(2, 1, false).IndexOf("SILENT", StringComparison.Ordinal) < 0);
            probe.Check("WITNESS when auto-approve is covering prompts, the section says so",
                WatchStateLine(2, 2, true).IndexOf("already being handled", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS ...and does not claim that when it is not covering them",
                WatchStateLine(2, 2, false).IndexOf("already being handled", StringComparison.Ordinal) < 0);
            probe.Check("no agents at all says that, rather than claiming silence",
                WatchStateLine(0, 0, false).IndexOf("no agent", StringComparison.Ordinal) >= 0);

            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ws"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);
                var module = new AgentFlowModule();
                module.Init(host);
                OptionsPane pane = host.OptionsPanes[0];
                IReadOnlyDictionary<string, string> shown = Shown(pane);

                foreach (SettingField f in pane.Schema)
                {
                    if (f == null || f.Kind != SettingKind.Info) continue;
                    string value;
                    probe.Check("WITNESS the Info row '" + f.Id + "' has text to render",
                        shown.TryGetValue(f.Id, out value) && !string.IsNullOrEmpty(value));
                }
                // A Header renders Label plus an OPTIONAL paragraph, so a missing value degrades to
                // a bold line rather than to nothing. Fine in general; not fine for this one, which
                // is the sentence that stops someone thinking Codex is unsupported.
                probe.Check("WITNESS the Codex note still says its prompts ARE clicked for you",
                    shown["aboutCodex"].IndexOf("clicked for you", StringComparison.Ordinal) >= 0);
                probe.Check("the watch section tells the user it is not auto-approve",
                    shown["watchIntro"].IndexOf("NOT auto-approve", StringComparison.Ordinal) >= 0);
                module.Shutdown();
            }
            return true;
        }

        /// <summary>A comparable rendering of everything a fold produces. Sorted by call id so
        /// dictionary order cannot make two equal states look different.</summary>
        private static string CanonicalFold(string mode, string cwd, bool sawAnyCall,
                                            IEnumerable<OutstandingCall> outstanding,
                                            IEnumerable<OutstandingCall> completed)
        {
            Func<IEnumerable<OutstandingCall>, string> render = calls =>
            {
                var rows = new List<string>();
                foreach (OutstandingCall call in calls)
                    rows.Add(string.Join("|", new[]
                    {
                        call.Id ?? "", call.Tool ?? "", call.Command ?? "", call.Argument ?? "",
                        call.Mode ?? "",
                        call.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                    }));
                rows.Sort(StringComparer.Ordinal);
                return string.Join(";", rows.ToArray());
            };
            return "mode=" + (mode ?? "") + " cwd=" + (cwd ?? "") + " saw=" + sawAnyCall
                   + " out=[" + render(outstanding) + "] done=[" + render(completed) + "]";
        }

        /// <summary>
        /// The incremental fold must agree with a whole-file parse, wherever the stream is cut.
        ///
        /// EXHAUSTIVE, not sampled. The fixture is small enough to split at EVERY byte offset,
        /// which is strictly better than choosing interesting boundaries and hoping the list was
        /// complete. It therefore covers, by exhaustion rather than by intention: a cut inside a
        /// JSON string, inside a multi-byte character (the fixture carries a two-byte e-acute and
        /// a four-byte wrench in both a value and a command), exactly on a newline, on a record
        /// boundary, and the zero-length read at each end.
        ///
        /// Each split gets its OWN path. Reusing one would let Windows file tunnelling hand the
        /// recreated file its predecessor's creation time, or not, and a cursor that saw a
        /// changed creation time would reset and re-read the whole file -- passing the assertion
        /// while testing nothing incremental at all.
        /// </summary>
        /// <summary>
        /// The cursor starts over when the file it was resuming into is no longer that file,
        /// and says why.
        ///
        /// Deliberately separate from the equivalence test, which asserts resets == 0. A reader
        /// that reset on every tick would pass equivalence perfectly and be exactly the design
        /// this replaces, so "it recovers" and "it does not over-recover" are different claims
        /// and need different tests.
        /// </summary>
        /// <summary>A comparable rendering of a whole Scan: what it detected and what it tallied.
        /// Sorted, because neither list has a meaningful order.</summary>
        private static string CanonicalScan(List<Detection> results, Dictionary<string, int> approved)
        {
            var rows = new List<string>();
            foreach (Detection d in results)
                rows.Add("det:" + d.Outcome + "|" + (d.ToolName ?? "") + "|"
                         + (d.Session != null ? d.Session.SessionId : ""));
            foreach (KeyValuePair<string, int> kv in approved)
                rows.Add("app:" + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
            rows.Sort(StringComparer.Ordinal);
            return string.Join(";", rows.ToArray());
        }

        /// <summary>
        /// Scanning through cursors must produce exactly what scanning whole files produces.
        ///
        /// The fold equivalence test proves the PARSER agrees across a split. This proves the
        /// thing built on top of it agrees too -- detections and the approvals tally -- because
        /// the cursor path also changed what `Completed` contains (new completions only) and how
        /// the counted set is pruned (by session, not by call id). Either of those could be
        /// wrong while the parser is perfectly right.
        ///
        /// Drives the real roots through the environment overrides rather than a seam, so what
        /// is exercised is the path production takes, including ActiveTranscripts.
        /// </summary>
        private static bool SelfCheckScanEquivalence(SelfTestProbe probe)
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-scan-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            string empty = root + "-none";
            string claudeWas = Environment.GetEnvironmentVariable(TranscriptReader.ClaudeRootVariable);
            string codexWas = Environment.GetEnvironmentVariable(TranscriptReader.CodexRootVariable);
            try
            {
                System.IO.Directory.CreateDirectory(root);
                System.IO.Directory.CreateDirectory(empty);
                string path = System.IO.Path.Combine(root, "session-alpha.jsonl");
                System.IO.File.WriteAllBytes(path, utf8.GetBytes("{\"cwd\":\"C:\\work\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"s1\",\"name\":\"Bash\",\"input\":{\"command\":\"git status\"}}]}}\n{\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"s1\"}]}}\n{\"cwd\":\"C:\\work\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"s2\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}\n"));

                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, root);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, empty);
                probe.Check("WITNESS the override root is the one being scanned",
                    string.Equals(TranscriptReader.ClaudeRoot, root, StringComparison.OrdinalIgnoreCase));

                var cache = new SessionCache();
                var coldCounted = new HashSet<string>(StringComparer.Ordinal);   // cold path keeps its own
                var warmCounted = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, int> coldApproved, warmApproved;

                List<Detection> cold = Scan(true, false, 30.0, coldCounted, out coldApproved,
                                            null, null, null);
                List<Detection> warm = Scan(true, false, 30.0, warmCounted, out warmApproved,
                                            null, cache, null);
                // Detections only. The approvals tally joins against the MACHINE's permission
                // rules, so asserting it is non-empty would pass here and fail on a runner with
                // no settings.json -- a machine-dependent assertion dressed as a coverage check.
                probe.Check("WITNESS a cold scan of the fixture is not vacuous", cold.Count > 0);
                probe.Check("WITNESS the first cursor scan matches a whole-file scan",
                    CanonicalScan(warm, warmApproved) == CanonicalScan(cold, coldApproved));
                probe.Check("the cache took a cursor for the transcript", cache.Count == 1);

                // Append, and compare again. THIS is the step the design exists for: the cursor
                // reads only the new bytes while the cold path re-reads everything.
                using (var append = new System.IO.FileStream(path, System.IO.FileMode.Append,
                                                             System.IO.FileAccess.Write))
                {
                    byte[] more = utf8.GetBytes("{\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"s2\"}]}}\n{\"cwd\":\"C:\\work\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"s3\",\"name\":\"Read\",\"input\":{\"command\":\"x\"}}]}}\n");
                    append.Write(more, 0, more.Length);
                }

                List<Detection> cold2 = Scan(true, false, 30.0,
                    new HashSet<string>(StringComparer.Ordinal), out coldApproved, null, null, null);
                List<Detection> warm2 = Scan(true, false, 30.0, warmCounted, out warmApproved,
                                             null, cache, null);
                probe.Check("WITNESS after an append the cursor scan still matches a whole-file scan",
                    CanonicalScan(warm2, warmApproved) == CanonicalScan(cold2, coldApproved));

                // Idempotence, stated without reference to the machine's rules: whatever WAS
                // counted is not counted again when nothing has been appended.
                Dictionary<string, int> again;
                Scan(true, false, 30.0, warmCounted, out again, null, cache, null);
                probe.Check("WITNESS re-scanning an unchanged transcript tallies nothing new",
                    again.Count == 0);

                // And the cursor is dropped once its transcript leaves the window.
                System.IO.File.Delete(path);
                Dictionary<string, int> ignored;
                Scan(true, false, 30.0, warmCounted, out ignored, null, cache, null);
                probe.Check("WITNESS a cursor is dropped when its transcript goes away",
                    cache.Count == 0);
                probe.Check("...and the counted set is pruned with it", warmCounted.Count == 0);
            }
            catch (Exception ex) { probe.Check("scan equivalence: " + ex.Message, false); }
            finally
            {
                Environment.SetEnvironmentVariable(TranscriptReader.ClaudeRootVariable, claudeWas);
                Environment.SetEnvironmentVariable(TranscriptReader.CodexRootVariable, codexWas);
                try { System.IO.Directory.Delete(root, true); } catch { }
                try { System.IO.Directory.Delete(empty, true); } catch { }
            }
            return true;
        }

        private static bool SelfCheckCursorResets(SelfTestProbe probe)
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-reset-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".jsonl");
            try
            {
                // 1. Ordinary growth is NOT a reset.
                System.IO.File.WriteAllBytes(path, utf8.GetBytes("{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"a1\",\"name\":\"Bash\"}]}}\n{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"a2\",\"name\":\"Read\"}]}}\n"));
                var cursor = new TranscriptCursor(path, TranscriptReader.AgentClaude);
                string reason;
                AgentSession one = cursor.Advance(out reason);
                probe.Check("a first read is not a reset", reason == null);
                probe.Check("the first read folded both calls", one.Outstanding.Count == 2);
                long afterFirst = cursor.Offset;
                probe.Check("WITNESS the cursor committed to an offset", afterFirst > 0);

                using (var append = new System.IO.FileStream(path, System.IO.FileMode.Append,
                                                             System.IO.FileAccess.Write))
                {
                    byte[] more = utf8.GetBytes("{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"c1\",\"name\":\"Grep\"}]}}\n");
                    append.Write(more, 0, more.Length);
                }
                AgentSession two = cursor.Advance(out reason);
                probe.Check("WITNESS appending is not a reset", reason == null);
                probe.Check("...and the appended call joined the existing two",
                    two.Outstanding.Count == 3);
                probe.Check("...and the cursor moved forward", cursor.Offset > afterFirst);

                // 2. TRUNCATION. Shorter than where the cursor stood.
                System.IO.File.WriteAllBytes(path, utf8.GetBytes("{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"c1\",\"name\":\"Grep\"}]}}\n"));
                AgentSession small = cursor.Advance(out reason);
                probe.Check("WITNESS a truncated file resets the cursor",
                    reason != null && reason.IndexOf("truncated", StringComparison.Ordinal) >= 0);
                probe.Check("...and the reason says where it had got to",
                    reason.IndexOf("cursor was at", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS the state after a reset is the NEW file, not a merge",
                    small.Outstanding.Count == 1 && small.Outstanding[0].Id == "c1");

                // 3. REPLACEMENT that the length and creation time both miss. WriteAllBytes
                //    truncates in place, so creation time is untouched, and this content is
                //    LONGER than the cursor offset -- the exact shape NTFS file tunnelling
                //    would hide. Only the head fingerprint can see it.
                cursor.Advance(out reason);                       // settle at the small file
                long settled = cursor.Offset;
                System.IO.File.WriteAllBytes(path, utf8.GetBytes("{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"b1\",\"name\":\"Edit\"}]}}\n{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"b2\",\"name\":\"Write\"}]}}\n{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"b3\",\"name\":\"Glob\"}]}}\n"));
                AgentSession swapped = cursor.Advance(out reason);
                probe.Check("WITNESS a same-name replacement longer than the offset is caught",
                    reason != null && reason.IndexOf("different file", StringComparison.Ordinal) >= 0);
                probe.Check("...even though it was longer than where the cursor stood",
                    utf8.GetByteCount("{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"b1\",\"name\":\"Edit\"}]}}\n{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"b2\",\"name\":\"Write\"}]}}\n{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"b3\",\"name\":\"Glob\"}]}}\n") > settled);
                probe.Check("WITNESS the replacement is read whole, with none of the old state",
                    swapped.Outstanding.Count == 3
                    && swapped.Outstanding.TrueForAll(c => c.Id != null && c.Id[0] == 'b'));

                // 4. A missing file reports what was already folded rather than inventing empty,
                //    AND KEEPS THE CLOCK. Reporting DateTime.UtcNow from a failed stat restarted
                //    the idle timer, so a genuinely stalled agent read as Working every time its
                //    transcript was briefly unreadable -- the watched-for failure erased by the
                //    act of failing to look.
                DateTime lastSeen = swapped.LastWriteUtc;
                System.IO.File.Delete(path);
                AgentSession gone = cursor.Advance(out reason);
                probe.Check("WITNESS a vanished transcript keeps what it had already folded",
                    gone.Outstanding.Count == 3);
                probe.Check("WITNESS ...and keeps the last write time, so the idle clock runs on",
                    gone.LastWriteUtc == lastSeen);

                // 5. EQUAL-LENGTH replacement, differing only past the first 64 bytes. Nothing
                //    grows, so on the old code nothing opened the file at all and the head check
                //    never ran; and at 64 bytes it could not have separated these anyway.
                //    MEASURED on this machine: 952 real transcripts share only 422 distinct
                //    64-byte heads, one prefix covering 104 files. At 128 bytes all 952 separate.
                string pad = new string('p', 60);
                string first = "{\"pad\":\"" + pad + "\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"d1\",\"name\":\"Bash\"}]}}\n";
                string second = "{\"pad\":\"" + pad + "\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"e1\",\"name\":\"Bash\"}]}}\n";
                probe.Check("WITNESS the fixtures are equal length and differ only after byte 64",
                    utf8.GetByteCount(first) == utf8.GetByteCount(second)
                    && first.Length > 64 && first.Substring(0, 64) == second.Substring(0, 64)
                    && first != second);
                System.IO.File.WriteAllBytes(path, utf8.GetBytes(first));
                var equal = new TranscriptCursor(path, TranscriptReader.AgentClaude);
                AgentSession beforeSwap = equal.Advance(out reason);
                probe.Check("the equal-length fixture folded",
                    beforeSwap.Outstanding.Count == 1 && beforeSwap.Outstanding[0].Id == "d1");

                // Written in place, so length and creation time are both unchanged. The write
                // time is SET rather than trusted: two WriteAllBytes calls this close together
                // can land on one filesystem timestamp, and the test would then pass or fail on
                // scheduling instead of on the behaviour it is about.
                System.IO.File.WriteAllBytes(path, utf8.GetBytes(second));
                System.IO.File.SetLastWriteTimeUtc(
                    path, System.IO.File.GetLastWriteTimeUtc(path).AddSeconds(5));
                AgentSession sameSize = equal.Advance(out reason);
                probe.Check("WITNESS a replacement of EXACTLY the same length is caught",
                    reason != null && reason.IndexOf("different file", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS ...and the new file's state replaced the old one's",
                    sameSize.Outstanding.Count == 1 && sameSize.Outstanding[0].Id == "e1");

                // 6. A tick with nothing written must not be a reset: the design rests on an
                //    unchanged length and write time costing one stat and no open.
                AgentSession quiet = equal.Advance(out reason);
                probe.Check("WITNESS a tick with nothing written is not a reset", reason == null);
                probe.Check("...and holds the state it already had",
                    quiet.Outstanding.Count == 1 && quiet.Outstanding[0].Id == "e1");
            }
            catch (Exception ex) { probe.Check("cursor resets: " + ex.Message, false); }
            finally { try { System.IO.File.Delete(path); } catch { } }
            return true;
        }

        private static bool SelfCheckFoldEquivalence(SelfTestProbe probe)
        {
            const string fixture = "{\"type\":\"permission-mode\",\"permissionMode\":\"default\"}\n{\"cwd\":\"C:\\caf\u00e9\\r\ud83d\udd27\"}\n{\"timestamp\":\"2026-09-21T10:00:00Z\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"c1\",\"name\":\"Bash\",\"input\":{\"command\":\"echo \u00e9\"}}]}}\n{\"timestamp\":\"2026-09-21T10:00:01Z\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"c1\"}]}}\n{\"timestamp\":\"2026-09-21T10:00:02Z\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"c2\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.txt\"}}]}}\n";
            byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(fixture);

            string wholePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-fold-whole-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl");
            string expected;
            try
            {
                System.IO.File.WriteAllBytes(wholePath, bytes);
                AgentSession whole = TranscriptReader.ReadClaude(wholePath);
                expected = CanonicalFold(whole.Mode, whole.Cwd, whole.SawAnyCall,
                                         whole.Outstanding, whole.Completed);
                probe.Check("WITNESS the whole-file parse of the fixture is not vacuous",
                    whole.SawAnyCall && whole.Outstanding.Count == 1
                    && whole.Completed.Count == 1 && whole.Mode == "default");
                probe.Check("WITNESS the fixture really does carry multi-byte characters",
                    bytes.Length > fixture.Length);
            }
            finally { try { System.IO.File.Delete(wholePath); } catch { } }

            int mismatches = 0, resets = 0, firstBad = -1;
            for (int split = 0; split <= bytes.Length; split++)
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "dp-agentflow-fold-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".jsonl");
                try
                {
                    var head = new byte[split];
                    Array.Copy(bytes, head, split);
                    System.IO.File.WriteAllBytes(path, head);

                    var cursor = new TranscriptCursor(path, TranscriptReader.AgentClaude);
                    var seen = new List<OutstandingCall>();
                    string reason;
                    AgentSession first = cursor.Advance(out reason);
                    if (reason != null) resets++;
                    seen.AddRange(first.Completed);

                    using (var append = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write))
                        append.Write(bytes, split, bytes.Length - split);

                    AgentSession second = cursor.Advance(out reason);
                    if (reason != null) resets++;
                    seen.AddRange(second.Completed);

                    string got = CanonicalFold(second.Mode, second.Cwd, second.SawAnyCall,
                                               second.Outstanding, seen);
                    if (!string.Equals(got, expected, StringComparison.Ordinal))
                    {
                        mismatches++;
                        if (firstBad < 0) firstBad = split;
                    }
                }
                catch (Exception ex)
                {
                    mismatches++;
                    if (firstBad < 0) firstBad = split;
                    probe.Note("split " + split + " threw " + ex.GetType().Name + ": " + ex.Message);
                }
                finally { try { System.IO.File.Delete(path); } catch { } }
            }

            probe.Note("fold equivalence: " + (bytes.Length + 1) + " split points over "
                       + bytes.Length + " bytes");
            probe.Check("WITNESS folding in two reads equals one whole-file parse, at every "
                        + "byte offset (first disagreement at " + firstBad + ")",
                mismatches == 0);
            probe.Check("WITNESS the split points were actually exercised, not skipped",
                bytes.Length > 200);
            probe.Check("WITNESS no split provoked a cursor reset, so the increments were real",
                resets == 0);
            return true;
        }

        private static bool SelfCheckTeardown(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-td"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);   // no scan at Init
                var module = new AgentFlowModule();
                module.Init(host);

                // Notify is the mode the next two assertions are ABOUT: it scans, and it never
                // presses. Set after Init so Init itself still starts no scan.
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Notify);

                // LOOKING IS WEAKER THAN PRESSING, and it has to be, or the Notify and Log
                // modes never read the panel at all. The two conditions were once inline and the
                // caller fed `mayLook` a value only computed when auto-approve was on, which
                // quietly made them equal: the Notify branch inside the sweep was unreachable
                // from the day it shipped. A relationship between two predicates is invisible in
                // either one of them, so it is asserted here.
                probe.Check("WITNESS the panel may be READ with auto-approve off",
                    module.MayLookNow(true, true) && !module.ShouldPressNow(false, true));
                probe.Check("WITNESS ...but nothing may be read with no port answering",
                    !module.MayLookNow(false, true));
                probe.Check("WITNESS a Notify-mode tick still probes the port, which is the "
                            + "value the whole look/press split depends on",
                    module.ShouldProbePort(true));
                // The predicates take `enabled` as an argument now, so on its own the line
                // above would pass even if no real mode ever supplied true. This is the
                // other half: Notify is a mode that scans.
                probe.Check("WITNESS ...and Notify really is a mode that scans, so that "
                            + "argument is not hypothetical",
                    AgentMode.Scans(AgentMode.Notify) && !AgentMode.Scans(AgentMode.Off));

                probe.Check("a live module with the switch on and a port answering may press",
                    module.ShouldPressNow(true, true));
                probe.Check("...but not with the switch off",
                    !module.ShouldPressNow(false, true));
                probe.Check("...and not with nothing answering",
                    !module.ShouldPressNow(true, false));

                // The flag ALONE, with the host still attached. Calling full Shutdown here would
                // also null _host, and then this assertion passes whether or not the flag exists.
                module.BeginShutdown();
                probe.Check("WITNESS a press is refused the instant shutdown BEGINS, while the "
                            + "host is still attached and the port still answering",
                    !module.ShouldPressNow(true, true));
                probe.Check("WITNESS ...and so is a LOOK, which is the weaker of the two and so "
                            + "the one a shutdown guard is easiest to forget on",
                    !module.MayLookNow(true, true));

                module.Shutdown();
                probe.Check("...and still refused once Shutdown has finished",
                    !module.ShouldPressNow(true, true));

                // The "nothing ran inline" assertion that used to sit here could not fail and is
                // now SelfCheckPostToUi, which forces both branches. See that method.
            }
            return true;
        }

        /// <summary>
        /// The refusal text goes somewhere that is NEVER logged, and the logged line carries none
        /// of it. UnsafeDetail existed to make that split possible and nothing read it, so the
        /// property it encodes was unenforced; this is the reader.
        /// </summary>
        private static bool SelfCheckRefusalPrivacy(SelfTestProbe probe)
        {
            const string secret = "Yes, and also rm -rf /home/somebody/private";
            PromptDecision d = PromptOptions.Choose(new List<string> { "Yes", secret, "No" });
            probe.Check("an unrecognised option refuses the prompt", !d.WillPress);
            probe.Check("WITNESS the screen text is kept, so a UI could show why",
                d.UnsafeDetail != null && d.UnsafeDetail.IndexOf("rm -rf", StringComparison.Ordinal) >= 0);
            probe.Check("WITNESS the LOGGED reason carries none of it",
                d.Reason.IndexOf("rm -rf", StringComparison.Ordinal) < 0
                && d.Reason.IndexOf("private", StringComparison.Ordinal) < 0);
            probe.Check("...and says how many it did not recognise, which is safe to log",
                d.Reason.IndexOf("1 of 3", StringComparison.Ordinal) >= 0);
            return true;
        }

        /// <summary>
        /// A Codex session that asks before it acts CAN now be watched, on a threshold measured
        /// against a control group rather than borrowed from Claude.
        ///
        /// The control is what makes the number meaningful: sessions running approval_policy
        /// `never` cannot ask a human, so every stall in them is machine-only. Over 18,974 such
        /// calls the longest gap was 121.2 s and nothing exceeded 180 s. Claude's 30 s would have
        /// fired on 946 of them.
        /// </summary>
        /// <summary>
        /// The per-session explanation set is bounded by what is live, not by uptime.
        ///
        /// It had no prune at all: one key per session per outcome, added for ever and cleared
        /// only at Shutdown. Slow growth is still growth, and every other set in this module
        /// already prunes against the current tick.
        /// </summary>
        private static bool SelfCheckExplainedPruned(SelfTestProbe probe)
        {
            var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            using (var storage =
                       new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("agentflow-ex"))
            {
                host.UseStorage("agentflow", storage);
                host.SettingsFor("agentflow").Set(SettingMode, AgentMode.Off);
                var module = new AgentFlowModule();
                module.Init(host);

                Func<string, Detection> stoodDown = id => new Detection
                {
                    Session = new AgentSession { Agent = TranscriptReader.AgentCodex, SessionId = id },
                    Outcome = DetectionOutcome.StoodDownAutoMode,
                    Reason = "because",
                };

                module.Apply(new List<Detection> { stoodDown("alpha") });
                probe.Check("WITNESS a session is remembered so it is not explained twice",
                    module.ExplainedCountForSelfTest == 1);

                module.Apply(new List<Detection> { stoodDown("alpha") });
                probe.Check("...and is not remembered twice", module.ExplainedCountForSelfTest == 1);

                module.Apply(new List<Detection> { stoodDown("beta") });
                probe.Check("WITNESS a session that is no longer live is forgotten",
                    module.ExplainedCountForSelfTest == 1);

                module.Apply(new List<Detection>());
                probe.Check("WITNESS nothing live means nothing remembered",
                    module.ExplainedCountForSelfTest == 0);

                module.Shutdown();
            }
            return true;
        }

        private static bool SelfCheckCodexWatch(SelfTestProbe probe)
        {
            var rules = new RuleSet();
            DateTime start = DateTime.UtcNow.AddHours(-1);

            Func<string, double, Detection> look = (policy, idleSeconds) =>
            {
                var session = new AgentSession
                {
                    Agent = TranscriptReader.AgentCodex,
                    SessionId = "rollout-x",
                    Mode = policy,
                    SawAnyCall = true,
                    LastWriteUtc = start,
                };
                session.Outstanding.Add(new OutstandingCall
                {
                    Id = "k1", Tool = "shell", Command = null, StartedUtc = start, Mode = null,
                });
                return BlockedDetector.Evaluate(session, rules, 30.0,
                                                start.AddSeconds(idleSeconds));
            };

            probe.Check("WITNESS a never-ask session stands down however long it has stalled",
                look("never", 9999).Outcome == DetectionOutcome.StoodDownAutoMode);
            probe.Check("...and says it is not waiting on anyone, not that precision is unmeasured",
                look("never", 9999).Reason.IndexOf("never stops to ask", StringComparison.Ordinal) >= 0);
            probe.Check("an unknown policy also stands down",
                look(null, 9999).Outcome == DetectionOutcome.StoodDownAutoMode);

            probe.Check("WITNESS an on-request session under the threshold reads as working",
                look(BlockedDetector.CodexOnRequest, 120).Outcome == DetectionOutcome.Working);
            probe.Check("WITNESS ...and over it reads as BLOCKED, which is the new capability",
                look(BlockedDetector.CodexOnRequest, 200).Outcome == DetectionOutcome.Blocked);

            // The threshold is the measurement. Borrowing Claude's 30 s would have fired on 5%
            // of calls in sessions that cannot prompt at all.
            probe.Check("WITNESS Codex waits longer than Claude before calling it a person",
                BlockedDetector.CodexStallSeconds > BlockedDetector.DefaultThresholdSeconds * 2);
            probe.Check("WITNESS the threshold clears the longest machine-only gap measured (121.2s)",
                BlockedDetector.CodexStallSeconds > 121.2);

            // Claude is untouched by any of this.
            var claude = new AgentSession
            {
                Agent = TranscriptReader.AgentClaude, SessionId = "c", Mode = "auto",
                SawAnyCall = true, LastWriteUtc = start,
            };
            claude.Outstanding.Add(new OutstandingCall
                { Id = "z", Tool = "Bash", Command = "ls", StartedUtc = start });
            probe.Check("WITNESS a Claude auto-mode session still stands down, unchanged",
                BlockedDetector.Evaluate(claude, rules, 30.0, start.AddSeconds(9999)).Outcome
                    == DetectionOutcome.StoodDownAutoMode);
            return true;
        }

        private static bool SelfCheckCodexMode(SelfTestProbe probe)
        {
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "dp-agentflow-codex-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl");
            try
            {
                System.IO.File.WriteAllText(scratch, "{\"type\":\"turn_context\",\"payload\":{\"approval_policy\":\"on-request\",\"collaboration_mode\":{\"mode\":\"default\"}}}\n{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"c1\",\"name\":\"shell\"}}");
                AgentSession onRequest = TranscriptReader.ReadCodex(scratch);
                probe.Check("WITNESS the approval policy is read out of turn_context",
                    onRequest.Mode == "on-request");
                probe.Check("...and collaboration_mode is NOT what gets read",
                    onRequest.Mode != "default");
                probe.Check("the call still registers as outstanding",
                    onRequest.SawAnyCall && onRequest.Outstanding.Count == 1);

                System.IO.File.WriteAllText(scratch, "{\"type\":\"turn_context\",\"payload\":{\"approval_policy\":\"never\",\"collaboration_mode\":{\"mode\":\"default\"}}}\n{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"c1\",\"name\":\"shell\"}}");
                AgentSession never = TranscriptReader.ReadCodex(scratch);
                probe.Check("a never-ask session reads as never, not as unknown",
                    never.Mode == "never");

                // These asserted that an on-request session still stood down, which was PHASE ONE
                // on purpose: the policy was read but not yet acted on, because precision for
                // Codex was unmeasured. It has since been measured against a control group that
                // cannot prompt, so the behaviour they pinned is deliberately gone. Kept, pointed
                // at the new intent, rather than deleted -- the reading half is still what makes
                // the acting half possible.
                var rules = new RuleSet();
                Detection d = BlockedDetector.Evaluate(onRequest, rules, 30.0,
                    DateTime.UtcNow.AddMinutes(5));
                probe.Check("WITNESS reading the policy is what lets an on-request session act",
                    d != null && d.Outcome == DetectionOutcome.Blocked);
                probe.Check("WITNESS ...and it says the session asks before it acts",
                    d.Reason.IndexOf("asks before it acts", StringComparison.Ordinal) >= 0);
                probe.Check("WITNESS ...and quotes no Claude precision number at a Codex session",
                    d.Reason.IndexOf("0.4%", StringComparison.Ordinal) < 0);

                // A transcript with no turn_context at all is the old shape, and must still be
                // handled rather than throwing.
                System.IO.File.WriteAllText(scratch, "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"c1\",\"name\":\"shell\"}}");
                AgentSession bare = TranscriptReader.ReadCodex(scratch);
                probe.Check("a rollout with no turn_context still reads, with no policy",
                    bare.SawAnyCall && string.IsNullOrEmpty(bare.Mode));
            }
            catch (Exception ex) { probe.Check("codex mode read: " + ex.Message, false); }
            finally { try { System.IO.File.Delete(scratch); } catch { } }
            return true;
        }

        private static bool SelfCheckCodexOptions(SelfTestProbe probe)
        {
            string matched;
            probe.Check("WITNESS the button spelling, keyboard hint and all, approves one call",
                PromptOptions.Classify("Allow once \u23CE", out matched) == OptionKind.ApproveOnce);
            probe.Check("...and the menu spelling of the same row agrees with it",
                PromptOptions.Classify("Allow once", out matched) == OptionKind.ApproveOnce);
            probe.Check("WITNESS the deny row is recognised through its Esc hint",
                PromptOptions.Classify("Deny Esc", out matched) == OptionKind.Reject);
            probe.Check("the similar-commands row is its own kind, not an approve-once",
                PromptOptions.Classify("Allow similar commands", out matched)
                    == OptionKind.ApproveSimilar);

            // The stripper must not eat text that merely ENDS in one of the hint words. A wide
            // trim here would silently widen every entry in the table.
            probe.Check("WITNESS a word merely ending in a hint is left alone",
                PromptOptions.StripKeyboardHint("coalesce") == "coalesce");
            probe.Check("...and so is a label that is only the hint word",
                PromptOptions.StripKeyboardHint("Esc") == "Esc");
            probe.Check("a stacked hint is removed entirely",
                PromptOptions.StripKeyboardHint("Allow once \u23CE Enter") == "Allow once");

            var codex = new List<string> { "Allow once \u23CE", "Deny Esc" };
            PromptDecision plain = PromptOptions.Choose(codex);
                probe.Check("WITNESS a Codex prompt is actionable at all, pressing the one-call row",
                    plain.WillPress && plain.Index == 0);

            // The whole point of the opt-in: the wider row is NOT pressed unless asked for.
            var withSimilar = new List<string>
                { "Allow once", "Allow similar commands", "Deny" };
            PromptDecision narrow = PromptOptions.Choose(withSimilar, false, false);
            probe.Check("WITNESS the similar-commands row is declined by default",
                narrow.WillPress && narrow.Index == 0);

            PromptDecision wider = PromptOptions.Choose(withSimilar, false, true);
            probe.Check("WITNESS opting in presses the similar-commands row instead",
                wider.WillPress && wider.Index == 1);

            // Two of them is an assumption about someone else's UI being wrong.
            probe.Check("two similar rows are ambiguous, so nothing is pressed",
                !PromptOptions.Choose(new List<string>
                    { "Allow similar commands", "Allow similar commands", "Deny" },
                    false, true).WillPress);

            // The Codex opt-in must not reach across to Claude's prompts.
            var claude = new List<string> { "Yes", "Yes, and don't ask again", "No" };
            PromptDecision leak = PromptOptions.Choose(claude, false, true);
            probe.Check("WITNESS the Codex opt-in changes nothing on a Claude prompt",
                leak.WillPress && leak.Index == 0);

            // An unknown row still poisons the prompt, Codex or not.
            probe.Check("an unrecognised Codex row refuses the whole prompt",
                !PromptOptions.Choose(new List<string>
                    { "Allow once", "Allow everything forever", "Deny" }, false, true).WillPress);
            return true;
        }

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

                // ---- ON SCREEN FIRST, THEN EVERY INSTALLED PET ---------------------
                // THIS DECISION REVERSED ON 2026-09-22, and the reversal is the interesting
                // part. The list was narrowed to on-screen-only because the owner reported the
                // opposite defect: "it offered every installed companion, which on this machine
                // is a scrolling list of folder ids for a choice about the two pets the user is
                // looking at." Both reports are real and they contradict each other, so the
                // tie-break is measurement rather than preference.
                //
                // Measured on the reporting machine, 2026-09-22: THREE pets are installed
                // (pink_sheep, shimeji-brq51bkr, shimeji-hornet-9b9d1d), so "every installed"
                // is a four-row dropdown, not a scroll. And every one resolves to a NAME --
                // Pearl, Jesus Our Lord, Hornet -- so the "folder ids" half of the original
                // complaint is about naming, which PetDisplay already fixed, rather than about
                // length. What was left was a pet the owner owns being unpickable: "agentflow
                // pet does not see pearl", with Hornet on screen and Pearl installed.
                //
                // If the installed set ever grows past what a dropdown can carry, the answer is
                // a different control, not hiding pets the user owns.
                host.CompanionManager = new FakePets(
                    new[] { "pink_sheep", "Pearl", "shimeji-hornet-9b9d1d", "Hornet",
                            "shimeji-brq51bkr", "Jesus Our Lord", "esheep64", "eSheep (default)" },
                    new[] { "pink_sheep", "shimeji-hornet-9b9d1d" });

                var listed = new List<string>(module.PetChoicesForSelfTest());
                probe.Check("WITNESS an installed pet that is NOT on screen can still be chosen",
                    listed.Contains("Jesus Our Lord") && listed.Contains("eSheep (default)"));
                probe.Check("WITNESS ...alongside the ones that are up",
                    listed.Contains("Pearl") && listed.Contains("Hornet"));
                probe.Check("WITNESS the on-screen pets come FIRST, so the likely choice is not "
                            + "buried under the library",
                    listed.IndexOf("Pearl") < listed.IndexOf("Jesus Our Lord")
                    && listed.IndexOf("Hornet") < listed.IndexOf("Jesus Our Lord"));
                probe.Check("WITNESS no pet is listed twice when it is both installed and up",
                    listed.FindAll(delegate(string n) { return n == "Pearl"; }).Count == 1);
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
