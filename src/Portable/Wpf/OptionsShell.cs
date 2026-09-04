using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// Assembles the settings panes for the WPF window (S5b): a core Preferences pane (backed by LocalData)
    /// plus every module-contributed <see cref="OptionsPane"/> collected by the plugin host. Opened from the
    /// tray; coexists with the classic FormOptions dialog during the transition (FormOptions retires in a
    /// later S5 step). Kept tiny + separate so the pane assembly is unit-testable (--wpf-options-selftest).
    /// </summary>
    internal static class OptionsShell
    {
        public static void Open() { Open(null); }

        /// <summary>Open the settings window, optionally landing on a specific pane by title (case-insensitive;
        /// unmatched or null falls back to the first pane) — used after a module-install restart to reopen
        /// straight back onto the Modules pane.</summary>
        public static void Open(string initialPaneTitle)
        {
            try
            {
                var window = new OptionsWindow(CollectPanes(), initialPaneTitle);
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "WPF settings window failed: " + ex.Message);
            }
        }

        /// <summary>Open the themed WPF About window (the modernization blurb, the usage/help section folded in
        /// from the former Help dialog, the Original/Legacy credits, and the active pet's author/title/version/
        /// info at the bottom). The tray's single "About / Help" entry calls this; mirrors <see cref="Open"/>.</summary>
        public static void OpenAbout(string author, string title, string version, string info)
        {
            try
            {
                var window = new AboutWindow(author, title, version, info);
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "WPF About window failed: " + ex.Message);
            }
        }

        /// <summary>The window's sections: core Preferences fixed first, the Modules manager fixed second
        /// (S6 — it must exist even with zero modules installed), then the host Pets gallery (custom control)
        /// and every module's contributed schema pane sorted alphabetically by title. Alphabetizing the tail
        /// (rather than load order) means installing a module places it predictably rather than wherever the
        /// loader happened to enumerate its folder.</summary>
        internal static IReadOnlyList<ShellPane> CollectPanes()
        {
            var panes = new List<ShellPane>
            {
                new SchemaShellPane(BuildPreferencesPane()),
                new CustomShellPane("Modules", delegate { return new ModulesPaneControl(); }),
            };

            var rest = new List<ShellPane>
            {
                new CustomShellPane("Companions", delegate { return new CompanionsPaneControl(); }),
            };
            DesktopAICompanion.Plugins.CompanionHost host = Program.Mainthread != null ? Program.Mainthread.Host : null;
            if (host != null && host.OptionsPanes != null)
            {
                foreach (OptionsPane p in host.OptionsPanes)
                    if (p != null) rest.Add(new SchemaShellPane(p));
            }
            rest.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            panes.AddRange(rest);
            return panes;
        }

        /// <summary>The core Preferences pane, rendered by the same schema mechanism as module panes and
        /// persisted through LocalData. A minimal safe subset for the first cut (S5b-1); the fuller
        /// preferences move over as FormOptions is retired.</summary>
        /// <summary>
        /// Group name for the random-drop settings. A constant rather than four copies of the same string:
        /// the filter below matches on it, so a typo in any one field would silently leave that field on
        /// screen while the rest vanished.
        /// </summary>
        private const string DropSettingsGroup = "Fortune / insight drop";

        /// <summary>
        /// Drop the random-drop settings when nothing is listening for a drop tick.
        ///
        /// The base never speaks on a drop by itself -- the tick exists purely to cue a module, and with no
        /// drop responder registered the timer fires into nothing. Showing "Randomly drop a fortune /
        /// insight" on a machine with neither Fortunes nor AI Brain installed offers a switch that cannot do
        /// anything, and the user reasonably reads the resulting silence as a bug.
        ///
        /// Asks the host a CAPABILITY question rather than checking for those two module ids. They are the
        /// only responders today, but the responder chain exists precisely so the host does not need to know
        /// which module answers, and an id list would hide these settings from a third module that registers
        /// one. Absent host (previews, self-tests) means no responders, so the settings stay hidden.
        /// </summary>
        private static List<SettingField> WithoutDeadDropSettings(List<SettingField> schema)
        {
            try
            {
                DesktopAICompanion.Plugins.CompanionHost host =
                    Program.Mainthread != null ? Program.Mainthread.Host : null;
                if (host != null && host.HasDropResponder) return schema;
            }
            catch { /* a settings pane must open even if the host is mid-teardown */ }
            schema.RemoveAll(f => f != null &&
                string.Equals(f.Group, DropSettingsGroup, StringComparison.Ordinal));
            return schema;
        }

        internal static OptionsPane BuildPreferencesPane()
        {
            // Audio output devices for the picker (enumerated fresh each open; first entry = default device).
            // Display names are de-duplicated so the enum options are unique; each maps back to its GUID.
            var devices = AudioOutput.EnumerateDevices();
            var deviceNames = new List<string>();
            var nameToGuid = new Dictionary<string, string>(StringComparer.Ordinal);
            var guidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in devices)
            {
                string baseName = string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value;
                string display = baseName;
                int suffix = 2;
                while (nameToGuid.ContainsKey(display)) display = baseName + " (" + (suffix++) + ")";
                deviceNames.Add(display);
                nameToGuid[display] = kv.Key;
                if (!guidToName.ContainsKey(kv.Key)) guidToName[kv.Key] = display;
            }

            // "Trigger Speech": which installed module speaks the pet's first right-click. Built from the
            // live poke-responder registrations, so a freshly-installed module appears here with no base
            // change and an uninstalled one disappears. Always offers "Default & Random" (= let any of them
            // win, including none), so with zero modules installed this is a single harmless entry.
            List<string> speechSourceLabels;
            Dictionary<string, string> speechLabelToModule;
            Dictionary<string, string> speechModuleToLabel;
            BuildTriggerSpeechOptions(out speechSourceLabels, out speechLabelToModule, out speechModuleToLabel);

            // The speaker list is rebuilt inside Load() as well, because the pets on screen change while the
            // app runs; this first build is only what the field is constructed with.
            List<string> speakerLabels;
            Dictionary<string, string> speakerLabelToType, speakerTypeToLabel;
            BuildSpeakerOptions(out speakerLabels, out speakerLabelToType, out speakerTypeToLabel);
            // Held by reference so Load() can refresh its Options in place before the field is rendered.
            var speakerField = new SettingField
            {
                Id = "defaultSpeakingPet",
                Label = "Companion that speaks for the app (reminders, fortunes)",
                Kind = SettingKind.Enum,
                Options = speakerLabels.ToArray(),
                Group = "Speech",
            };

            return new OptionsPane
            {
                Title = "Preferences",
                Schema = WithoutDeadDropSettings(new List<SettingField>
                {
                    new SettingField { Id = "runAtStartup", Label = "Run at Windows startup", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "windowForeground", Label = "Bring collided window to front", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "stealFocus", Label = "Keep companion above the taskbar", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "multiscreen", Label = "Let companions spawn on any screen (they stay on the one they appear on)", Kind = SettingKind.Bool, Group = "Startup & window" },
                    // Says what it actually governs. BuildStartupSpawnPlan uses the saved pet MIX whenever there
                    // is one and only falls back to this count, so the old bare "Companions at startup" label claimed
                    // authority it does not have: set it to 2 with a six-pet mix and you still get six.
                    new SettingField { Id = "petsAtStartup", Label = "Companions at startup (only when you haven't picked specific companions)", Kind = SettingKind.Int, Min = 1, Max = 16, Group = "Startup & window" },
                    // Per-pet size lives in the Pets module now (the size cycle on each pet card); the global
                    // scale stays only as the internal fallback for pets without an override, so it's no longer
                    // a Preferences field.
                    new SettingField { Id = "volume", Label = "Volume (0-10, 0 = mute)", Kind = SettingKind.Int, Min = 0, Max = 10, Group = "Sound" },
                    new SettingField { Id = "audioDevice", Label = "Sound output device", Kind = SettingKind.Enum, Options = deviceNames.ToArray(), Group = "Sound" },
                    new SettingField { Id = "petSounds", Label = "Play companion sounds (a companion's own sound effects)", Kind = SettingKind.Bool, Group = "Sound" },
                    new SettingField { Id = "notificationSounds", Label = "Play notification sounds (module chimes, e.g. reminders)", Kind = SettingKind.Bool, Group = "Sound" },
                    new SettingField { Id = "speech", Label = "Enable speech bubbles", Kind = SettingKind.Bool, Group = "Speech" },
                    new SettingField { Id = "speechSeconds", Label = "Speech duration (seconds)", Kind = SettingKind.Int, Min = 2, Max = 30, Group = "Speech" },
                    new SettingField { Id = "noRepeat", Label = "Don't repeat the same message twice in a row", Kind = SettingKind.Bool, Group = "Speech" },
                    new SettingField { Id = "triggerSpeech", Label = "Trigger Speech", Kind = SettingKind.Enum, Options = speechSourceLabels.ToArray(), Group = "Speech" },
                    // Which pet speaks a message addressed to nobody in particular (a reminder, a fortune, the
                    // speech test). Every pet used to say it at the same instant, which read as a bug. Its
                    // Options are rebuilt on each open from the pets actually on screen, which works because
                    // OptionsWindow.PaneView.Build() calls Load() BEFORE it reads Schema.
                    speakerField,
                    new SettingField { Id = "randomDrop", Label = "Randomly drop a fortune / insight", Kind = SettingKind.Bool, Group = DropSettingsGroup },
                    new SettingField { Id = "randomDropMinutes", Label = "…every (minutes)", Kind = SettingKind.Int, Min = 1, Max = 9999, Group = DropSettingsGroup },
                    new SettingField { Id = "randomDropJitter", Label = "…plus or minus (minutes)", Kind = SettingKind.Int, Min = 0, Max = 9998, Group = DropSettingsGroup },
                    // These three are the only things here that reach the network unprompted, so each says so
                    // on its label and each can be turned off. All notify-only: nothing downloads or installs
                    // without the user clicking Update.
                    //
                    // The stored KEY stays monthlyModuleUpdateCheck even though the cadence is now weekly.
                    // Renaming it would drag a settings migration through three files to change a string
                    // nobody sees; the label is the part the user reads.
                    new SettingField { Id = "monthlyModuleUpdateCheck", Label = "Check weekly for module updates (tells you; never installs on its own)", Kind = SettingKind.Bool, Group = "Modules" },
                    new SettingField { Id = "petUpdateCheck", Label = "Check weekly for companion updates (tells you; never installs on its own)", Kind = SettingKind.Bool, Group = "Modules" },
                    // Hourly rather than weekly, deliberately: missing a new app version for an hour matters
                    // because a user restarts expecting to be told, whereas content updates are not urgent.
                    new SettingField { Id = "appUpdateCheck", Label = "Check weekly for a new app version (tells you; never installs on its own)", Kind = SettingKind.Bool, Group = "Modules" },
                }),
                Load = delegate
                {
                    var d = new Dictionary<string, string>(StringComparer.Ordinal);
                    LocalData data = Program.MyData;
                    if (data != null)
                    {
                        d["volume"] = ((int)Math.Round(data.GetVolume() * 10.0)).ToString(CultureInfo.InvariantCulture);
                        d["windowForeground"] = data.GetWindowForeground() ? "true" : "false";
                        d["stealFocus"] = data.GetStealTaskbarFocus() ? "true" : "false";
                        d["multiscreen"] = data.GetMultiscreen() ? "true" : "false";
                        d["petsAtStartup"] = data.GetAutoStartPets().ToString(CultureInfo.InvariantCulture);
                        d["petSounds"] = data.GetPetSoundsEnabled() ? "true" : "false";
                        d["notificationSounds"] = data.GetNotificationSoundsEnabled() ? "true" : "false";
                        d["speech"] = data.GetSpeechEnabled() ? "true" : "false";
                        d["speechSeconds"] = data.GetSpeechDuration().ToString(CultureInfo.InvariantCulture);
                        d["noRepeat"] = data.GetSuppressRepeats() ? "true" : "false";
                        // "" (default & random) and an id whose module is no longer installed both fall
                        // back to the default label, without clearing the stored choice (a module can be
                        // reinstalled later and its preference should survive that round trip).
                        string savedModule = data.GetTriggerSpeechModule("");
                        string savedLabel;
                        d["triggerSpeech"] = speechModuleToLabel.TryGetValue(savedModule ?? "", out savedLabel)
                            ? savedLabel
                            : TriggerSpeechDefaultLabel;
                        string savedGuid = data.GetAudioDeviceId();
                        if (string.IsNullOrEmpty(savedGuid)) savedGuid = Guid.Empty.ToString();
                        string curName;
                        d["audioDevice"] = guidToName.TryGetValue(savedGuid, out curName)
                            ? curName
                            : (deviceNames.Count > 0 ? deviceNames[0] : "");
                    }
                    d["runAtStartup"] = StartupRegistration.IsEnabled() ? "true" : "false";
                    if (data != null)
                    {
                        d["randomDrop"] = data.GetRandomDropEnabled() ? "true" : "false";
                        d["randomDropMinutes"] = data.GetRandomDropMinutes().ToString(CultureInfo.InvariantCulture);
                        d["randomDropJitter"] = data.GetRandomDropJitterMinutes().ToString(CultureInfo.InvariantCulture);
                        d["monthlyModuleUpdateCheck"] = data.GetMonthlyModuleUpdateCheck() ? "true" : "false";
                        d["petUpdateCheck"] = data.GetPetUpdateCheck() ? "true" : "false";
                        d["appUpdateCheck"] = data.GetAppUpdateCheck() ? "true" : "false";

                        // Rebuild the speaker list from the pets on screen RIGHT NOW and refresh the field's
                        // Options in place. Safe because Build() calls this before it reads Schema; if that
                        // order ever changed the dropdown would silently freeze at its construction-time list,
                        // which is why a source invariant pins it.
                        BuildSpeakerOptions(out speakerLabels, out speakerLabelToType, out speakerTypeToLabel);
                        speakerField.Options = speakerLabels.ToArray();
                        // An unset choice, or one whose pet is no longer out, both show the default label
                        // WITHOUT clearing the stored id: that pet may come back and the preference should
                        // survive the round trip (same rule as Trigger Speech above).
                        string savedSpeaker = data.GetDefaultSpeakingPet();
                        string speakerLabel;
                        d["defaultSpeakingPet"] = speakerTypeToLabel.TryGetValue(savedSpeaker ?? "", out speakerLabel)
                            ? speakerLabel
                            : SpeakerDefaultLabel;
                    }
                    return d;
                },
                Save = delegate(IReadOnlyDictionary<string, string> values)
                {
                    LocalData data = Program.MyData;
                    if (data == null || values == null) return false;
                    bool ok = true;
                    string s; int n; bool b;
                    if (values.TryGetValue("runAtStartup", out s) && bool.TryParse(s, out b)) StartupRegistration.Set(b);
                    if (values.TryGetValue("volume", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetVolume(Math.Max(0, Math.Min(10, n)) / 10.0);
                    if (values.TryGetValue("windowForeground", out s) && bool.TryParse(s, out b)) ok &= data.SetWindowForeground(b);
                    if (values.TryGetValue("stealFocus", out s) && bool.TryParse(s, out b)) ok &= data.SetStealTaskbarFocus(b);
                    if (values.TryGetValue("multiscreen", out s) && bool.TryParse(s, out b)) ok &= data.SetMultiscreen(b);
                    if (values.TryGetValue("petsAtStartup", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetAutoStartPets(Math.Max(1, Math.Min(16, n)));
                    if (values.TryGetValue("petSounds", out s) && bool.TryParse(s, out b)) ok &= data.SetPetSoundsEnabled(b);
                    if (values.TryGetValue("notificationSounds", out s) && bool.TryParse(s, out b))
                    {
                        ok &= data.SetNotificationSoundsEnabled(b);
                        // Turning notification sounds off cuts a chime that is mid-play too, same as speech off.
                        if (!b) { try { if (Program.Mainthread != null) Program.Mainthread.StopAllModuleSound(); } catch { } }
                    }
                    if (values.TryGetValue("speech", out s) && bool.TryParse(s, out b))
                    {
                        ok &= data.SetSpeechEnabled(b);
                        // Switching speech OFF must also silence a module that is mid-utterance. There is no
                        // settings-changed event on IHost, so a voice module cannot notice on its own and
                        // would keep talking for the rest of the line the user just tried to stop.
                        if (!b) { try { if (Program.Mainthread != null) Program.Mainthread.StopAllModuleSound(); } catch { } }
                    }
                    if (values.TryGetValue("speechSeconds", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetSpeechDuration(Math.Max(2, Math.Min(30, n)));
                    if (values.TryGetValue("noRepeat", out s) && bool.TryParse(s, out b)) ok &= data.SetSuppressRepeats(b);
                    if (values.TryGetValue("monthlyModuleUpdateCheck", out s) && bool.TryParse(s, out b)) ok &= data.SetMonthlyModuleUpdateCheck(b);
                    if (values.TryGetValue("petUpdateCheck", out s) && bool.TryParse(s, out b)) ok &= data.SetPetUpdateCheck(b);
                    if (values.TryGetValue("appUpdateCheck", out s) && bool.TryParse(s, out b)) ok &= data.SetAppUpdateCheck(b);
                    if (values.TryGetValue("defaultSpeakingPet", out s))
                    {
                        string chosenType;
                        // An unrecognized label (the pet was removed while the window was open) leaves the
                        // saved choice alone rather than silently rewriting it to the default.
                        if (speakerLabelToType.TryGetValue(s ?? "", out chosenType))
                            ok &= data.SetDefaultSpeakingPet(chosenType);
                    }
                    if (values.TryGetValue("triggerSpeech", out s))
                    {
                        string chosenModule;
                        // An unrecognized label (a module uninstalled while the window was open) leaves the
                        // saved choice alone rather than silently rewriting it to the default.
                        if (speechLabelToModule.TryGetValue(s ?? "", out chosenModule))
                            ok &= data.SetTriggerSpeechModule("", chosenModule);
                    }
                    string devGuid;
                    if (values.TryGetValue("audioDevice", out s) && nameToGuid.TryGetValue(s, out devGuid))
                    {
                        Guid gg;
                        // Store "" for the default device so it keeps following the default across device changes.
                        string toStore = (Guid.TryParse(devGuid, out gg) && gg == Guid.Empty) ? "" : devGuid;
                        ok &= data.SetAudioDeviceId(toStore);
                        try { if (Program.Mainthread != null) Program.Mainthread.ApplyAudioDevice(toStore); } catch { }
                    }

                    // Random-drop cadence lives in settings.json now (S5c); edit the three fields as a set
                    // then nudge the running pet to re-arm its drop timer.
                    bool rdEnabled = data.GetRandomDropEnabled();
                    int rdMinutes = data.GetRandomDropMinutes();
                    int rdJitter = data.GetRandomDropJitterMinutes();
                    if (values.TryGetValue("randomDrop", out s) && bool.TryParse(s, out b)) rdEnabled = b;
                    if (values.TryGetValue("randomDropMinutes", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) rdMinutes = n;
                    if (values.TryGetValue("randomDropJitter", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) rdJitter = n;
                    ok &= data.SetRandomDrop(rdEnabled, rdMinutes, rdJitter);

                    try { if (Program.Mainthread != null) ((DesktopAICompanion.Options.ICompanionRuntime)Program.Mainthread).ReloadAiSettings(); } catch { }
                    try { ContextMenus.RefreshSpeechMenuItem(); } catch { }
                    return ok;
                },
                Actions = BuildPreferencesActions(),
            };
        }

        internal const string TriggerSpeechDefaultLabel = "Default & Random";
        internal const string SpeakerDefaultLabel = "First companion on screen";

        /// <summary>
        /// Build the "companion that speaks for the app" dropdown from the pets ACTUALLY ON SCREEN, so it never
        /// offers a pet that cannot answer. The label is the pet's catalog display name where one is known,
        /// else its type id; the stored value is always the TYPE id, so a catalog rename cannot invalidate a
        /// saved choice. <see cref="SpeakerDefaultLabel"/> is always first and maps to "" (= the oldest pet
        /// out), which is also the runtime fallback when the chosen type is not currently on screen.
        ///
        /// Rebuilt on every pane open rather than once at construction, which is only possible because
        /// PaneView.Build() calls Load() BEFORE it reads Schema -- see the invariant covering that order.
        /// </summary>
        internal static void BuildSpeakerOptions(
            out List<string> labels,
            out Dictionary<string, string> labelToType,
            out Dictionary<string, string> typeToLabel)
        {
            labels = new List<string> { SpeakerDefaultLabel };
            labelToType = new Dictionary<string, string>(StringComparer.Ordinal) { { SpeakerDefaultLabel, "" } };
            typeToLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "", SpeakerDefaultLabel } };

            if (Program.Mainthread == null) return;
            List<CompanionCountEntry> mix = null;
            try { mix = Program.Mainthread.OnScreenMix(); } catch { }
            if (mix == null) return;

            foreach (CompanionCountEntry entry in mix)
            {
                if (entry == null) continue;
                string typeId = entry.Id ?? "";
                // "" in the mix means the active/default type; resolve it so it is a real, matchable id.
                if (typeId.Length == 0)
                    typeId = Program.MyData != null ? (Program.MyData.GetActivePetId() ?? "") : "";
                if (typeId.Length == 0 || typeToLabel.ContainsKey(typeId)) continue;

                string label = ContextMenus.TrayPetName(typeId);
                if (string.IsNullOrWhiteSpace(label)) label = typeId;
                // Keep labels unique so the closed Enum dropdown can round-trip them unambiguously.
                if (labelToType.ContainsKey(label)) label = label + " (" + typeId + ")";
                if (labelToType.ContainsKey(label)) continue;
                labels.Add(label);
                labelToType[label] = typeId;
                typeToLabel[typeId] = label;
            }
        }

        /// <summary>
        /// Build the "Trigger Speech" dropdown's options from the modules that actually registered a poke
        /// responder this run. The label shown is the module's own display name (from its ModuleInfo) where
        /// one is known, else its id; the stored value is always the module id, so a rename of the display
        /// name never invalidates a saved setting. Always includes <see cref="TriggerSpeechDefaultLabel"/>
        /// first, mapping to "" — with no modules installed that's the only entry.
        /// </summary>
        internal static void BuildTriggerSpeechOptions(
            out List<string> labels,
            out Dictionary<string, string> labelToModule,
            out Dictionary<string, string> moduleToLabel)
        {
            labels = new List<string> { TriggerSpeechDefaultLabel };
            labelToModule = new Dictionary<string, string>(StringComparer.Ordinal) { { TriggerSpeechDefaultLabel, "" } };
            moduleToLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "", TriggerSpeechDefaultLabel } };

            DesktopAICompanion.Plugins.CompanionHost host = Program.Mainthread != null ? Program.Mainthread.Host : null;
            if (host == null) return;

            // Module id -> display name, for the modules currently loaded.
            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DesktopAICompanion.Modules.IModule m in Program.Mainthread.LoadedModules)
                    if (m != null && m.Info != null && !string.IsNullOrEmpty(m.Info.Id))
                        displayNames[m.Info.Id] = string.IsNullOrWhiteSpace(m.Info.Name) ? m.Info.Id : m.Info.Name;
            }
            catch { }

            foreach (string moduleId in host.PokeResponderModuleIds)
            {
                if (string.IsNullOrEmpty(moduleId) || moduleToLabel.ContainsKey(moduleId)) continue;
                string name;
                string label = displayNames.TryGetValue(moduleId, out name) ? name : moduleId;
                // Keep labels unique so the closed Enum dropdown can round-trip them unambiguously.
                if (labelToModule.ContainsKey(label)) label = label + " (" + moduleId + ")";
                if (labelToModule.ContainsKey(label)) continue;
                labels.Add(label);
                labelToModule[label] = moduleId;
                moduleToLabel[moduleId] = label;
            }
        }

        /// <summary>The Preferences pane's action buttons: a sound test, and a reset that restores the
        /// preferences on this page to their defaults (behind a confirmation).</summary>
        private static List<PaneAction> BuildPreferencesActions()
        {
            PaneAction reset = new PaneAction { Label = "Reset to default settings" };
            reset.InvokeAsync = delegate
            {
                var choice = System.Windows.MessageBox.Show(
                    "Reset all preferences on this page to their defaults?\n\n" +
                    "This restores the startup, window, sound, speech, and fortune-drop settings shown here. " +
                    "It does not remove any companions, per-companion sizes, or the AI Brain module's settings.",
                    "Reset settings",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                if (choice != System.Windows.MessageBoxResult.Yes)
                {
                    reset.ReloadPaneAfter = false;
                    return System.Threading.Tasks.Task.FromResult("Cancelled — nothing was reset.");
                }
                string status = ResetToDefaultSettings();
                // Rebuild the pane so the fields visibly snap to their defaults (the reset is already saved).
                reset.ReloadPaneAfter = true;
                return System.Threading.Tasks.Task.FromResult(status);
            };
            return new List<PaneAction>
            {
                new PaneAction { Label = "Test sound", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(TestSound()); }, Group = "Sound" },
                reset,
            };
        }

        /// <summary>Play a short test tone through the current output device (the "Test sound" button).</summary>
        private static string TestSound()
        {
            try
            {
                if (Program.Mainthread == null) return "No running companion to play through.";
                Program.Mainthread.PlayTestSound();
                return "Played a test tone on the selected output.";
            }
            catch (Exception ex) { return "Couldn't play: " + ex.Message; }
        }

        /// <summary>Restore the preferences shown on this page to their defaults. Scoped on purpose: the
        /// pet payload (loaded pet XML/images), per-pet sizes/mutes, and the AI Brain module's own settings
        /// are left alone — only the core preference fields + the fortune-drop cadence shown here are reset,
        /// then persisted. The pane is rebuilt afterward so the new values show.</summary>
        private static string ResetToDefaultSettings()
        {
            try
            {
                LocalData data = Program.MyData;
                if (data == null) return "Settings are unavailable.";

                // Core preferences: pull each default from a fresh document, apply via the validated setters
                // (so nothing outside the preference fields — pet XML, pet mix, etc. — is touched).
                AppSettingsDocument def = AppSettingsDocument.CreateDefault();
                data.SetVolume(def.Volume);
                data.SetWindowForeground(def.WindowForeground);
                data.SetStealTaskbarFocus(def.StealTaskbarFocus);
                data.SetMultiscreen(def.MultiScreen);
                data.SetAutoStartPets(def.AutoStartPets);
                data.SetScale(def.ScaleLevel);                 // the internal size fallback
                data.SetPetSoundsEnabled(def.PetSoundsEnabled ?? true);
                data.SetNotificationSoundsEnabled(def.NotificationSoundsEnabled ?? true);
                data.SetSpeechEnabled(def.SpeechEnabled);
                data.SetSpeechDuration(def.SpeechDurationSeconds);
                data.SetSuppressRepeats(def.SuppressRepeats ?? true);
                data.SetThemeMode(def.ThemeMode);
                data.SetAudioDeviceId(def.AudioDeviceId);

                // Run-at-startup lives in the registry, not the settings doc; default is off.
                try { StartupRegistration.Set(false); } catch { }
                // Poke speaker back to "Default & Random" (the global entry; per-pet entries, when they
                // exist, are pet configuration rather than a preference on this page).
                try { data.SetTriggerSpeechModule("", ""); } catch { }
                // Apply the reset output device to the running pet right away (theme applies on next open).
                try { if (Program.Mainthread != null) Program.Mainthread.ApplyAudioDevice(def.AudioDeviceId ?? ""); } catch { }

                // Fortune/insight drop cadence (settings.json, S5c): reset the three drop fields shown on
                // this page to their defaults and re-arm the running pet's drop timer.
                try
                {
                    data.SetRandomDrop(def.RandomDropEnabled ?? false, def.RandomDropMinutes ?? 15, def.RandomDropJitterMinutes ?? 3);
                    if (Program.Mainthread != null) ((DesktopAICompanion.Options.ICompanionRuntime)Program.Mainthread).ReloadAiSettings();
                }
                catch { }

                try { ContextMenus.RefreshSpeechMenuItem(); } catch { }
                return "";   // no status text needed: the pane rebuild shows the restored values
            }
            catch (Exception ex) { return "Reset failed: " + ex.Message; }
        }
    }
}
