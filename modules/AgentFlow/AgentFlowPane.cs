using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// The options pane. Split out of AgentFlowModule because the schema is now BUILT rather than
    /// declared, and the builder is longer than the module's whole tray contribution.
    ///
    /// It is built per open for one reason: the animation choice is two cascading dropdowns, and
    /// the second one's contents depend on the first. The host calls Load before it reads Schema
    /// on every build -- an invariant the core pane already relies on and the hardening suite
    /// asserts -- so a pane can hand back a different schema each time it is opened. What made the
    /// CASCADE possible rather than just the variation is OptionsPane.LoadPending, which hands the
    /// builder the value the user just picked instead of only what was last saved.
    /// </summary>
    public sealed partial class AgentFlowModule
    {
        private OptionsPane _pane;

        /// <summary>The modes in which the notify settings are live, for EnabledWhen.</summary>
        private static string WhileItStillSpeaks
        {
            // DERIVED, never written out as a literal. The radio's on-screen value is the display
            // label, so a hand-typed copy here would silently stop matching the day the label is
            // reworded -- and the symptom would be a permanently greyed-out field, which reads as
            // a layout bug rather than as a broken string.
            //
            // BOTH speaking modes, not just Notify. Auto-approve still announces the prompts it
            // REFUSED -- AgentMode.Speaks says so, and Apply honours every one of these settings
            // there -- so greying them in auto mode told the user they were inert while the module
            // was still reading them. Found by an audit comparing the pane against AgentMode's own
            // documentation, which is the sort of disagreement no test was ever going to notice.
            get
            {
                return SettingMode + "=" + AgentMode.ToDisplay(AgentMode.Notify)
                       + "|" + AgentMode.ToDisplay(AgentMode.AutoApprove);
            }
        }

        private OptionsPane BuildPane()
        {
            return new OptionsPane
            {
                Title = "AgentFlow",
                Schema = BuildSchema(null),
                // No Load. The host uses LoadPending INSTEAD of Load whenever a module
                // supplies one, and MinHostVersion is 1.2.0, so every host that can load this
                // module has it. Keeping both meant the self-test was the only caller of
                // LoadPaneValues -- a test exercising a path production does not take, which
                // is worse than no test at all because it reads like coverage.
                LoadPending = LoadPendingValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Show this module's data folder",
                        InvokeAsync = ShowDataFolderAsync,
                        RevealsPath = true,
                        Group = GroupApprovals,
                    },
                    new PaneAction
                    {
                        Label = "Check now",
                        InvokeAsync = CheckNowAsync,
                        Group = GroupAgentFlow,
                    },
                    new PaneAction
                    {
                        Label = "Enable VSCode for AgentFlow",
                        InvokeAsync = EnableCdpAsync,
                        Group = GroupVsCode,
                    },
                    new PaneAction
                    {
                        Label = "Disable (undo changes)",
                        InvokeAsync = DisableCdpAsync,
                        Group = GroupVsCode,
                    },
                    new PaneAction
                    {
                        Label = "Find argv.json...",
                        InvokeAsync = BrowseForArgvAsync,
                        Group = GroupVsCode,
                    },
                },
            };
        }

        private const string GroupApprovals = "Recently auto-approved";
        private const string GroupAgentFlow = "AgentFlow";
        private const string GroupVsCode = "VSCode Enablement";
        private const string GroupWhat = "What AgentFlow does";
        private const string GroupAgents = "Telling you when an agent is stuck";

        /// <summary>
        /// The schema, for a given pending pet choice.
        ///
        /// <paramref name="pendingPet"/> is the pet selected ON SCREEN, which may not be the saved
        /// one: that is the whole point of the cascade. Null means "use whatever is stored".
        /// </summary>
        private IReadOnlyList<SettingField> BuildSchema(string pendingPet)
        {
            string pet = pendingPet ?? StoredAnimPet;
            var fields = new List<SettingField>
            {
                // ---- the approvals card, pinned to the top and spanning the columns ----------
                new SettingField
                {
                    Id = FieldApprovals,
                    Label = "Last ten, newest first",
                    Kind = SettingKind.Header,
                    Group = GroupApprovals,
                    PinTop = true,
                    FullWidth = true,
                },
                // Where the full record lives. The button beneath used to try to reveal the log itself and
                // was refused by the host on every machine (N-host-03), so the path is STATED here, through
                // LogLocationLine, and the button shows this module's own folder instead.
                new SettingField
                {
                    Id = "aboutLog",
                    Label = "The log",
                    Kind = SettingKind.Info,
                    Group = GroupApprovals,
                },

                // ---- what it does about a waiting prompt -------------------------------------
                new SettingField
                {
                    Id = SettingMode,
                    Label = "When a prompt is waiting",
                    Kind = SettingKind.Radio,
                    Options = AgentMode.Displays(),
                    Group = GroupAgentFlow,
                },
                new SettingField
                {
                    Id = SettingApproveAllProjects,
                    Label = "...and save it for all projects, not just this call (Claude)",
                    Kind = SettingKind.Bool,
                    Group = GroupAgentFlow,
                    // Only meaningful while approving, so it greys out in every other mode
                    // rather than sitting there implying it does something.
                    EnabledWhen = SettingMode + "=" + AgentMode.ToDisplay(AgentMode.AutoApprove),
                },
                new SettingField
                {
                    Id = SettingApproveSimilar,
                    Label = "...and allow similar commands, not just this call (Codex)",
                    Kind = SettingKind.Bool,
                    Group = GroupAgentFlow,
                    EnabledWhen = SettingMode + "=" + AgentMode.ToDisplay(AgentMode.AutoApprove),
                },
                new SettingField
                {
                    Id = SettingPressLimit,
                    Label = "...at most this many prompts every 5 minutes",
                    Kind = SettingKind.Int,
                    Min = PressBudget.MinPressLimit,
                    Max = PressBudget.MaxPressLimit,
                    Group = GroupAgentFlow,
                    // Only meaningful while approving, like the two rows above it.
                    EnabledWhen = SettingMode + "=" + AgentMode.ToDisplay(AgentMode.AutoApprove),
                },
                new SettingField
                {
                    Id = SettingThreshold,
                    Label = "Say something after this many seconds of waiting",
                    Kind = SettingKind.Int,
                    Min = 10,
                    Max = 600,
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                },
                new SettingField
                {
                    Id = SettingCooldown,
                    Label = "Leave at least this many seconds between messages",
                    Kind = SettingKind.Int,
                    Min = 30,
                    Max = 3600,
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                },
                new SettingField
                {
                    Id = SettingNotifySound,
                    Label = "Play the notification sound",
                    Kind = SettingKind.Bool,
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                },
                new SettingField
                {
                    Id = SettingNotifySpeak,
                    Label = "Have the companion say something",
                    Kind = SettingKind.Bool,
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                },
                new SettingField
                {
                    Id = SettingAnimate,
                    Label = "Play an animation",
                    Kind = SettingKind.Bool,
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                },
                new SettingField
                {
                    Id = SettingAnimPet,
                    Label = "...on which pet",
                    Kind = SettingKind.Enum,
                    Options = PetChoices(),
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                    // Picking a pet rebuilds the pane so the next dropdown can be that pet's own
                    // animation list. Without this the second dropdown could only be refreshed by
                    // applying and reopening.
                    ReloadOnChange = true,
                },
                new SettingField
                {
                    Id = SettingAnimName,
                    Label = "...and which animation",
                    Kind = SettingKind.Enum,
                    Options = AnimationChoices(pet),
                    Group = GroupAgentFlow,
                    EnabledWhen = WhileItStillSpeaks,
                },

                // ---- setting up the approve half ---------------------------------------------
                new SettingField
                {
                    Id = "aboutSetup",
                    Label = "Status",
                    Kind = SettingKind.Info,
                    Group = GroupVsCode,
                },

                // ---- the two explanations, now one card with real headings -------------------
                new SettingField
                {
                    Id = "hdrReads",
                    Label = "What it reads",
                    Kind = SettingKind.Header,
                    Group = GroupWhat,
                },
                new SettingField
                {
                    Id = "hdrPress",
                    Label = "What it will and will not press",
                    Kind = SettingKind.Header,
                    Group = GroupWhat,
                },
                new SettingField
                {
                    Id = "hdrAuto",
                    Label = "In auto mode",
                    Kind = SettingKind.Header,
                    Group = GroupWhat,
                },

                // ---- who gets WATCHED for being stuck (not who gets approved) -------------------
                new SettingField
                {
                    Id = "watchIntro",
                    Kind = SettingKind.Info,
                    Group = GroupAgents,
                },
                new SettingField
                {
                    Id = "watchState",
                    Kind = SettingKind.Info,
                    Group = GroupAgents,
                },
                new SettingField
                {
                    Id = SettingWatchClaude,
                    Label = "Watch Claude Code",
                    Kind = SettingKind.Bool,
                    Group = GroupAgents,
                },
                new SettingField
                {
                    Id = SettingWatchCodex,
                    Label = "Watch Codex",
                    Kind = SettingKind.Bool,
                    Group = GroupAgents,
                },
                new SettingField
                {
                    Id = "aboutCodex",
                    Label = "About Codex",
                    Kind = SettingKind.Header,
                    Group = GroupAgents,
                },
            };
            return fields;
        }

        /// <summary>The chosen pet TYPE ID, or the one on screen when nothing is chosen.</summary>
        private string StoredAnimPet
        {
            get
            {
                string stored = _settings == null ? "" : _settings.Get(SettingAnimPet, "");
                return string.IsNullOrEmpty(stored) ? DefaultPetTypeId() : stored;
            }
        }

        /// <summary>
        /// The pets the user could animate, "(any pet)" first.
        ///
        /// Installed types rather than the pets currently on screen. A prompt can arrive at any
        /// time and the pet that happens to be up then is not the pet that was up when the setting
        /// was chosen, so offering only the on-screen ones would produce a choice that silently
        /// stops meaning anything the moment the user swaps pets.
        /// </summary>
        /// <summary>
        /// True until Init has returned, because during Init this module IS NOT YET REGISTERED.
        ///
        /// ModuleHost calls Init and adds the module to LoadedModules on the NEXT line, and
        /// IHost.GetCompanionManager answers the permission question from that list -- so a call
        /// made from Init is refused for a reason that stops being true moments later. The host
        /// used to CACHE that refusal, which made it permanent: the pet dropdown offered nothing
        /// but "(any pet)" for the life of the process, however often it was reopened. The host
        /// no longer caches a denial, but not asking too early is the half of the fix that
        /// reaches people without a host upgrade.
        /// </summary>
        private bool _initialising = true;

        internal void FinishedInitialising() { _initialising = false; }

        private ICompanionManager Pets()
        {
            if (_initialising || _host == null) return null;
            try { return _host.GetCompanionManager(Info.Id); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The pets the user can choose, by DISPLAY NAME, with "(any pet)" first.
        ///
        /// THE PETS ON SCREEN FIRST, THEN EVERY INSTALLED ONE. The list was narrowed to on-screen
        /// pets once, because it had offered the whole installed library as a wall of folder ids,
        /// and widened again on 2026-09-22 when a pet the owner owned but had not spawned could
        /// not be chosen at all; the measurement behind the reversal is with SelfCheckPetChoices.
        /// (This paragraph used to quote a count of bundled companions. The bundle is lean now and
        /// the count was stale, so it names none.)
        ///
        /// Display names because that is what the user calls them: "Pearl", not "esheep64".
        /// The TYPE ID is what gets stored and what the XML is read by, so the two are mapped
        /// rather than conflated -- the same split the mode radio makes between a stored id
        /// and a sentence on screen.
        ///
        /// Two things are added beyond what is on screen, and both are about not losing the
        /// user's own choice: a pet they picked earlier that is no longer up stays in the
        /// list (dropping it would silently reset their setting to something else), and when
        /// NOTHING is on screen the installed set is offered instead, because a dropdown
        /// holding only "(any pet)" cannot be configured ahead of spawning a pet.
        /// </summary>
        private string[] PetChoices()
        {
            var choices = new List<string> { PetAnimations.AnyPet };
            foreach (string typeId in OnScreenPetTypeIds())
            {
                string display = PetDisplayFor(typeId);
                if (display.Length > 0 && display != PetAnimations.AnyPet
                    && !choices.Contains(display))
                    choices.Add(display);
            }

            // EVERY INSTALLED PET, not only when none is on screen. This used to be guarded
            // by `choices.Count == 1`, so the moment any companion was up the installed list was
            // never consulted and a pet the user owns but has not spawned could not be chosen at
            // all. Reported against v1.2.4: "agentflow pet does not see pearl", with Hornet on
            // screen. The on-screen ones are added first so the likely choice stays near the top.
            foreach (CompanionTypeInfo type in InstalledPets())
            {
                string display = PetDisplay(type);
                if (display.Length > 0 && display != PetAnimations.AnyPet
                    && !choices.Contains(display))
                    choices.Add(display);
            }

            // Keep a previously chosen pet visible even once it has gone, so opening the pane
            // does not quietly change what the user picked.
            string stored = _settings == null ? "" : _settings.Get(SettingAnimPet, "");
            if (!string.IsNullOrEmpty(stored) && stored != PetAnimations.AnyPet)
            {
                string display = PetDisplayFor(stored);
                if (display.Length > 0 && display != PetAnimations.AnyPet
                    && !choices.Contains(display))
                    choices.Add(display);
            }
            return choices.ToArray();
        }

        /// <summary>
        /// ONE InstalledTypes() and ONE OnScreenMix() per pane build.
        ///
        /// Neither is free. The host answers InstalledTypes by enumerating two directories and
        /// reading a 32 KB header out of every installed pet's animations.xml, with no cache, on
        /// the UI thread -- and one build asked for it four to six times: once per on-screen pet
        /// through PetDisplayFor, once for the installed loop, once for the stored pet, once from
        /// PetTypeIdFor, once from LoadValues and up to twice from DefaultPetTypeId, on every pane
        /// open and every pet-dropdown change, because that dropdown rebuilds the pane (F042). The
        /// comment beside the animation memo below records the authors finding the equivalent
        /// double read of ONE pet's XML; this was the larger sibling. Both memos live for a single
        /// load: set at the top of LoadPendingValues, cleared in its finally, so nothing can go
        /// stale across builds. Outside a load -- Init's first BuildPane, the self-test's direct
        /// calls -- each call fetches, as before.
        /// </summary>
        private IReadOnlyList<CompanionTypeInfo> _installedForLoad;
        private List<string> _onScreenForLoad;

        /// <summary>Type ids of the pets currently on screen, in the order the host reports.</summary>
        private List<string> OnScreenPetTypeIds()
        {
            return _onScreenForLoad ?? FetchOnScreenPetTypeIds();
        }

        private List<string> FetchOnScreenPetTypeIds()
        {
            var live = new List<string>();
            ICompanionManager manager = Pets();
            if (manager == null) return live;
            try
            {
                IReadOnlyList<CompanionCount> mix = manager.OnScreenMix();
                if (mix == null) return live;
                foreach (CompanionCount count in mix)
                    if (count != null && count.Count > 0 && !string.IsNullOrEmpty(count.TypeId)
                        && !live.Contains(count.TypeId))
                        live.Add(count.TypeId);
            }
            catch (Exception) { }
            return live;
        }

        private IReadOnlyList<CompanionTypeInfo> InstalledPets()
        {
            return _installedForLoad ?? FetchInstalledPets();
        }

        private IReadOnlyList<CompanionTypeInfo> FetchInstalledPets()
        {
            ICompanionManager manager = Pets();
            if (manager == null) return new List<CompanionTypeInfo>();
            try { return manager.InstalledTypes() ?? new List<CompanionTypeInfo>(); }
            catch (Exception) { return new List<CompanionTypeInfo>(); }
        }

        internal static string PetDisplay(CompanionTypeInfo type)
        {
            if (type == null) return "";
            return !string.IsNullOrEmpty(type.DisplayName) ? type.DisplayName : (type.TypeId ?? "");
        }

        /// <summary>Display name back to the type id the module stores and reads XML by.</summary>
        private string PetTypeIdFor(string display)
        {
            if (string.IsNullOrEmpty(display) || display == PetAnimations.AnyPet)
                return PetAnimations.AnyPet;
            foreach (CompanionTypeInfo type in InstalledPets())
            {
                if (string.Equals(PetDisplay(type), display, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.TypeId, display, StringComparison.OrdinalIgnoreCase))
                    return type.TypeId ?? PetAnimations.AnyPet;
            }
            return PetAnimations.AnyPet;
        }

        private string PetDisplayFor(string typeId)
        {
            if (string.IsNullOrEmpty(typeId) || typeId == PetAnimations.AnyPet)
                return PetAnimations.AnyPet;
            foreach (CompanionTypeInfo type in InstalledPets())
                if (string.Equals(type.TypeId, typeId, StringComparison.OrdinalIgnoreCase))
                    return PetDisplay(type);
            return PetAnimations.AnyPet;
        }

        /// <summary>
        /// The pet to preselect when the user has chosen none: the one actually ON SCREEN.
        ///
        /// "(any pet)" is a correct default and a useless one. It hands back a generic coverage
        /// list at a moment when the module can see exactly which pet is up and read that pet's
        /// real animation names. Falls back to the first installed type, then to the sentinel.
        /// </summary>
        private string DefaultPetTypeId()
        {
            foreach (string typeId in OnScreenPetTypeIds()) return typeId;
            foreach (CompanionTypeInfo type in InstalledPets())
                if (!string.IsNullOrEmpty(type.TypeId)) return type.TypeId;
            return PetAnimations.AnyPet;
        }
        private string _choicesPet;
        private string[] _choicesCache;

        /// <summary>
        /// The animation names a pet offers, memoised by pet id.
        ///
        /// One LoadValues asked for this TWICE -- once through BuildSchema to fill the dropdown,
        /// once through StoredAnimName to pick which entry is selected -- so every pane open and
        /// every dropdown change read and parsed the pet's XML twice over, on the UI thread. The
        /// memo collapses that to once, and also covers the common case of opening the pane
        /// repeatedly without changing pets.
        ///
        /// CAVEAT, and it is why this is invalidated on Save rather than never: a pet edited in
        /// PetStudio while this pane is open keeps the old list until the pane is saved or the
        /// module reloads. That is a stale dropdown, not a wrong animation -- the name is resolved
        /// against the live XML when it is actually played.
        /// </summary>
        private string[] AnimationChoices(string pet)
        {
            string key = pet ?? "";
            if (_choicesCache != null && string.Equals(_choicesPet, key, StringComparison.Ordinal))
                return _choicesCache;

            bool authoritative;
            string[] computed = ComputeAnimationChoices(pet, out authoritative);

            // A FAILED LOOKUP IS NEVER REMEMBERED, and the first version of this memo did
            // remember one. Reported against v1.2.4: the animation list showed the generic
            // seven for a pet that has its own, and the ONLY way to get the real list was to
            // change the pet and change it back.
            //
            // The chain: Init sets _initialising, BuildPane builds the schema immediately, and
            // Pets() returns null while _initialising is true -- deliberately, see that field.
            // So the very first computation for the stored pet ALWAYS falls back, and caching
            // it pinned the fallback under the right key for the life of the pane. Flipping to
            // another pet evicted the single slot; flipping back recomputed with the manager
            // live and finally succeeded, which is exactly the reported workaround.
            //
            // The irony is recorded six lines above _initialising: the HOST used to cache the
            // permission refusal that this guard exists to avoid, "which made it permanent: the
            // pet dropdown offered nothing but (any pet) for the life of the process". Same bug
            // class, moved into the module and pointed at a different dropdown.
            if (authoritative)
            {
                _choicesPet = key;
                _choicesCache = computed;
            }
            return computed;
        }

        /// <summary>
        /// <paramref name="authoritative"/> separates "this IS the answer" from "we could not
        /// find out". Only the first may be cached: see AnimationChoices. The coverage list is
        /// the real answer for (any pet) and a guess for everything else, and the two are
        /// indistinguishable by value, which is why the flag exists rather than a contents test.
        /// </summary>
        private string[] ComputeAnimationChoices(string pet, out bool authoritative)
        {
            authoritative = true;
            if (string.IsNullOrEmpty(pet) || pet == PetAnimations.AnyPet)
            {
                var generic = new List<string>();
                foreach (string name in PetAnimations.AnyPetCandidates) generic.Add(name);
                return generic.ToArray();
            }
            try
            {
                ICompanionManager manager = Pets();
                if (manager != null)
                {
                    string xml, error;
                    if (manager.TryReadTypeXml(pet, out xml, out error))
                    {
                        List<string> names = PetAnimations.FromXml(xml);
                        if (names.Count > 0) return names.ToArray();
                    }
                }
            }
            catch (Exception) { }
            // A pet whose XML cannot be read falls back to the coverage list rather than to an
            // empty dropdown, which would read as "this pet has no animations". NOT authoritative:
            // the manager is null for the whole of Init, so this path is taken at least once on
            // every launch and is right again moments later.
            authoritative = false;
            var fallback = new List<string>();
            foreach (string name in PetAnimations.AnyPetCandidates) fallback.Add(name);
            return fallback.ToArray();
        }

        private IReadOnlyDictionary<string, string> LoadPendingValues(
            IReadOnlyDictionary<string, string> pending)
        {
            // Both host answers, fetched once for this build. See InstalledPets.
            _installedForLoad = FetchInstalledPets();
            _onScreenForLoad = FetchOnScreenPetTypeIds();
            try
            {
                string pet = null;
                if (pending != null)
                {
                    string chosen;
                    // The pane hands back what the user SAW, which is the display name.
                    if (pending.TryGetValue(SettingAnimPet, out chosen) && !string.IsNullOrEmpty(chosen))
                        pet = PetTypeIdFor(chosen);
                }
                return LoadValues(pet);
            }
            finally
            {
                _installedForLoad = null;
                _onScreenForLoad = null;
            }
        }

        /// <summary>
        /// The values, and the schema that goes with them.
        ///
        /// The schema is assigned here rather than at Init because the host reads Schema AFTER
        /// calling this on every build. That ordering is the documented invariant this cascade
        /// depends on, and the hardening suite asserts it so a reordering cannot pass silently.
        /// </summary>
        private IReadOnlyDictionary<string, string> LoadValues(string pendingPet)
        {
            if (_pane != null) _pane.Schema = BuildSchema(pendingPet);
            string pet = pendingPet ?? StoredAnimPet;

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { SettingMode, AgentMode.ToDisplay(Mode) },
                { SettingThreshold, ((int)ThresholdSeconds).ToString(CultureInfo.InvariantCulture) },
                { SettingCooldown, CooldownSeconds.ToString(CultureInfo.InvariantCulture) },
                { SettingPressLimit, PressLimit.ToString(CultureInfo.InvariantCulture) },
                { SettingWatchClaude, WatchClaude ? "true" : "false" },
                { SettingWatchCodex, WatchCodex ? "true" : "false" },
                { SettingAnimate, Animate ? "true" : "false" },
                { SettingNotifySound, NotifySoundOn ? "true" : "false" },
                { SettingApproveAllProjects, ApproveForAllProjects ? "true" : "false" },
                { SettingApproveSimilar, ApproveSimilarCommands ? "true" : "false" },
                { SettingNotifySpeak, NotifySpeakOn ? "true" : "false" },
                { SettingAnimPet, PetDisplayFor(pet) },
                { SettingAnimName, StoredAnimName(pet) },

                // Display-only rows. These are VALUES, not labels, which is the thing that makes
                // them live: the host renders the Load() value for Info and Header, and Load runs
                // on every pane open. An earlier version put this text in Label and it froze at
                // whatever was true when Init ran.
                { FieldApprovals, _approvalFeed.Render() },
                { "aboutSetup", SetupStatusLine() },
                { "aboutLog", LogLocationLine() },
                { "hdrReads", "It reads the transcript files your coding agent already writes, to "
                              + "see which tool call is waiting. Nothing is sent anywhere, and no "
                              + "command, path or prompt text is ever written to the diagnostic log "
                              + "or shown in a speech bubble." },
                { "hdrPress", "It only ever presses the option that approves THIS ONE CALL. Never "
                              + "“don’t ask again”, never “allow all edits this "
                              + "session”, and never anything that changes your permission "
                              + "mode. Every comparable tool was read at source level and all four "
                              + "press wider than they advertise. If it cannot recognise even one "
                              + "option on a prompt, it touches nothing." },
                { "hdrAuto", "In auto mode the notify half stands down and says so. The permission "
                             + "rules stop predicting which calls will prompt there, so it would be "
                             + "wrong roughly 250 times for every time it was right." },
                { "watchIntro", "This is NOT auto-approve. Auto-approve clicks the button for "
                                + "you and is set above. This section is only about being TOLD "
                                + "when an agent has been sitting there waiting." },
                { "watchState", WatchState },
                { "aboutCodex", "Codex prompts are clicked for you already. Being TOLD a Codex "
                                + "session is stuck is newer and more cautious: it waits three "
                                + "minutes, where Claude waits thirty seconds, because a Codex "
                                + "transcript carries no command to check against your permission "
                                + "rules, so the wait is the only evidence there is. Sessions that "
                                + "never stop to ask are skipped entirely." },
            };
        }

        private string StoredAnimName(string pet)
        {
            string stored = _settings == null ? "" : _settings.Get(SettingAnimName, "");
            string[] available = AnimationChoices(pet);
            foreach (string name in available)
                if (string.Equals(name, stored, StringComparison.OrdinalIgnoreCase)) return name;
            // The stored animation is not one this pet has -- which is the normal case right after
            // switching pets. Fall to the first offered rather than leaving the dropdown blank,
            // because a blank combo reads as broken and a wrong-but-visible one reads as a choice.
            return available.Length > 0 ? available[0] : "";
        }

        /// <summary>
        /// Hand the host a file in this module's OWN folder to reveal, so Explorer opens on that folder.
        ///
        /// This button used to ask for the app's diagnostics log, two levels up from the module's
        /// storage, and the host refused it on every machine from the day PermittedRevealRoot
        /// narrowed an owned pane's reveal to the module's own storage (N-host-03). The containment
        /// rule is right: a module reveals its own files, not the app's and not another module's,
        /// and widening it for one button was declined. So the button asks for something inside the
        /// rule -- the settings file the host writes into the folder it handed out -- and the pane
        /// STATES where the log is (aboutLog, above this button), which is what the button was for.
        /// There is no IHost verb for the app's log, and inventing one for a single button is worse
        /// than a sentence. The path is a request, not an instruction: the host still checks it.
        /// </summary>
        private System.Threading.Tasks.Task<string> ShowDataFolderAsync()
        {
            string path = RevealPathFrom(_host != null ? _host.GetStorage(Info.Id) : null);
            if (path == null)
                return System.Threading.Tasks.Task.FromResult(
                    "\u2717 This host gave the module no data folder.");
            if (!System.IO.File.Exists(path))
                return System.Threading.Tasks.Task.FromResult(
                    "✗ Nothing is saved in this module's folder yet. Press Apply once, then try again.");
            return System.Threading.Tasks.Task.FromResult(path);
        }

        /// <summary>
        /// The file this module asks the host to reveal: its own settings file, which the host writes
        /// into the storage directory it handed out (CompanionHost.GetSettings), so it is inside the
        /// root the host permits an owned pane. Pure and internal so the self-test can assert the
        /// containment without a host.
        /// </summary>
        internal static string RevealPathFrom(IModuleStorage storage)
        {
            if (storage == null || string.IsNullOrEmpty(storage.DataDirectory)) return null;
            return System.IO.Path.Combine(storage.DataDirectory, "settings.json");
        }

        /// <summary>
        /// The containment rule the host applies to an owned pane's reveal (OptionsWindow.RevealRootFor:
        /// the module's own storage directory), mirrored so the self-test can ask it of RevealPathFrom's
        /// answer and of the old answer. Full paths against a separator-terminated root, as the host's
        /// IsUnder compares; the host resolves reparse points on top, which needs a real filesystem.
        /// </summary>
        internal static bool IsInsideStorage(string path, IModuleStorage storage)
        {
            if (path == null || storage == null || string.IsNullOrEmpty(storage.DataDirectory)) return false;
            try
            {
                string root = System.IO.Path.GetFullPath(storage.DataDirectory)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                string full = System.IO.Path.GetFullPath(path);
                return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>The Approvals section's note on where the log is, since the pane can no longer reveal
        /// it: the path a support request needs (SUPPORT.md names the same two locations).</summary>
        private string LogLocationLine()
        {
            string log = LogPathFrom(_host != null ? _host.GetStorage(Info.Id) : null);
            return "Every press and refusal is recorded in the app's diagnostic log"
                   + (log != null ? " at " + log : " (SUPPORT.md says where it lives)")
                   + ". The host lets a module reveal only files in its own folder, so the button below "
                   + "shows this module's folder; the log is two levels up from it.";
        }

        /// <summary>
        /// The host's diagnostic log, from the module's own storage directory, for the pane's note.
        ///
        /// Pure and internal so the self-test can assert the arithmetic without a host: the
        /// bug this replaced was a path that looked right on the machine it was written on
        /// and was wrong everywhere else. GetStorage hands back <dataRoot>\modules\<id>, so the
        /// log is two levels up; there is no ABI for "where is the log".
        /// </summary>
        internal static string LogPathFrom(IModuleStorage storage)
        {
            if (storage == null || string.IsNullOrEmpty(storage.DataDirectory)) return null;
            try
            {
                System.IO.DirectoryInfo modules =
                    System.IO.Directory.GetParent(storage.DataDirectory.TrimEnd(
                        System.IO.Path.DirectorySeparatorChar));
                if (modules == null || modules.Parent == null) return null;
                return System.IO.Path.Combine(modules.Parent.FullName, "diagnostics.log");
            }
            catch (Exception) { return null; }
        }
    }
}
