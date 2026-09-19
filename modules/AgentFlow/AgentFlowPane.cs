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
                Load = LoadPaneValues,
                LoadPending = LoadPendingValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Open the log",
                        InvokeAsync = OpenLogAsync,
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
        private const string GroupAgents = "Which agents";

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

                // ---- which agents --------------------------------------------------------------
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
                    Label = "Watch Codex (reads it, but cannot act on it yet)",
                    Kind = SettingKind.Bool,
                    Group = GroupAgents,
                },
                new SettingField
                {
                    Id = "aboutCodex",
                    Label = "Why Codex is off",
                    Kind = SettingKind.Header,
                    Group = GroupAgents,
                },
            };
            return fields;
        }

        private string StoredAnimPet
        {
            get
            {
                return _settings == null
                    ? PetAnimations.AnyPet
                    : _settings.Get(SettingAnimPet, PetAnimations.AnyPet);
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
        private string[] PetChoices()
        {
            var choices = new List<string> { PetAnimations.AnyPet };
            try
            {
                ICompanionManager manager = _host != null ? _host.GetCompanionManager(Info.Id) : null;
                if (manager != null)
                {
                    IReadOnlyList<CompanionTypeInfo> types = manager.InstalledTypes();
                    if (types != null)
                        foreach (CompanionTypeInfo type in types)
                            if (type != null && !string.IsNullOrEmpty(type.TypeId))
                                choices.Add(type.TypeId);
                }
            }
            catch (Exception)
            {
                // A pane that cannot list pets still opens, with the any-pet option only. Throwing
                // here would take the whole settings window down over a dropdown.
            }
            return choices.ToArray();
        }

        /// <summary>
        /// One pet's own animation names, or the coverage list when no specific pet is chosen.
        ///
        /// There is no list that works everywhere: across the 53 bundled companions the set of
        /// animations present on EVERY pet is empty, and the four that come closest are the
        /// engine's reserved lifecycle animations.
        /// </summary>
        private string[] AnimationChoices(string pet)
        {
            if (string.IsNullOrEmpty(pet) || pet == PetAnimations.AnyPet)
            {
                var generic = new List<string>();
                foreach (string name in PetAnimations.AnyPetCandidates) generic.Add(name);
                return generic.ToArray();
            }
            try
            {
                ICompanionManager manager = _host != null ? _host.GetCompanionManager(Info.Id) : null;
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
            // empty dropdown, which would read as "this pet has no animations".
            var fallback = new List<string>();
            foreach (string name in PetAnimations.AnyPetCandidates) fallback.Add(name);
            return fallback.ToArray();
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            return LoadValues(null);
        }

        private IReadOnlyDictionary<string, string> LoadPendingValues(
            IReadOnlyDictionary<string, string> pending)
        {
            string pet = null;
            if (pending != null)
            {
                string chosen;
                if (pending.TryGetValue(SettingAnimPet, out chosen) && !string.IsNullOrEmpty(chosen))
                    pet = chosen;
            }
            return LoadValues(pet);
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
                { SettingWatchClaude, WatchClaude ? "true" : "false" },
                { SettingWatchCodex, WatchCodex ? "true" : "false" },
                { SettingAnimate, Animate ? "true" : "false" },
                { SettingNotifySound, NotifySoundOn ? "true" : "false" },
                { SettingNotifySpeak, NotifySpeakOn ? "true" : "false" },
                { SettingAnimPet, pet },
                { SettingAnimName, StoredAnimName(pet) },

                // Display-only rows. These are VALUES, not labels, which is the thing that makes
                // them live: the host renders the Load() value for Info and Header, and Load runs
                // on every pane open. An earlier version put this text in Label and it froze at
                // whatever was true when Init ran.
                { FieldApprovals, _approvalFeed.Render() },
                { "aboutSetup", SetupStatusLine() },
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
                { "aboutCodex", "Codex's transcript does not record a permission mode, and AgentFlow "
                                + "only acts in default mode, so a watched Codex session can only "
                                + "ever stand down. The reading half works, so this becomes useful "
                                + "the day that format carries a mode." },
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
        /// Hand the host the diagnostic log to reveal.
        ///
        /// The path is the host's, not ours, and the host refuses anything outside its own data
        /// root -- so this is a request rather than an instruction, which is the point of the
        /// RevealsPath contract.
        /// </summary>
        private System.Threading.Tasks.Task<string> OpenLogAsync()
        {
            // DERIVED from this module's own storage directory, never from a guess at where
            // the app keeps its data. The first version hardcoded %LOCALAPPDATA%\DesktopAICompanion
            // and the host refused it -- correctly -- because a PORTABLE build keeps its data
            // in a `data` folder beside the exe, so the path was genuinely outside the root
            // it was being asked to reveal from. The containment check was right and the
            // module was wrong, which is the good version of that argument.
            //
            // GetStorage hands back <dataRoot>\modules\<id>, so the log is two levels up. There is
            // no ABI for "where is the log", and inventing one for a single button is worse
            // than deriving it from a directory the host already gave us.
            string path = LogPathFrom(_host != null ? _host.GetStorage(Info.Id) : null);
            if (path == null)
                return System.Threading.Tasks.Task.FromResult(
                    "\u2717 Cannot work out where the log lives.");
            if (!System.IO.File.Exists(path))
                return System.Threading.Tasks.Task.FromResult(
                    "✗ No diagnostic log yet. Turn logging on in Preferences first.");
            return System.Threading.Tasks.Task.FromResult(path);
        }

        /// <summary>
        /// The host's diagnostic log, from the module's own storage directory.
        ///
        /// Pure and internal so the self-test can assert the arithmetic without a host: the
        /// bug this replaces was a path that looked right on the machine it was written on
        /// and was wrong everywhere else.
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
