using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion
{
    /// <summary>
    /// --wpf-options-selftest (S5b): proves the WPF settings shell's schema renderer without showing a
    /// window. Asserts OptionsShell assembles the core Preferences pane, and that PaneView renders all five
    /// SettingKind controls and round-trips values Load -> controls -> Collect (with Secret staying
    /// write-only: a blank secret box is omitted on collect). Runs on the process's STA main thread (WPF
    /// control construction requires STA); creates a WPF Application if none exists so theme resources resolve.
    /// </summary>
    internal static class WpfOptionsSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    try { new System.Windows.Application(); } catch { }
                }

                // 1) OptionsShell assembles the window sections: Preferences fixed first, Modules fixed
                // second (S6 -- it must exist even with zero modules installed), then every remaining pane
                // (the Companions custom control today, plus any module-contributed schema panes) alphabetized.
                IReadOnlyList<DesktopAICompanion.Wpf.ShellPane> panes = DesktopAICompanion.Wpf.OptionsShell.CollectPanes();
                ok &= Check(sb, "collect yields Preferences first (schema pane, has Apply)",
                    panes != null && panes.Count >= 1 && panes[0] != null && panes[0].Title == "Preferences" && panes[0].HasApply);
                ok &= Check(sb, "collect yields Modules second (custom control, no Apply)",
                    panes != null && panes.Count >= 2 && panes[1] != null && panes[1].Title == "Modules" && !panes[1].HasApply);

                // The random-drop settings are dead UI with no module listening for a drop tick: the base
                // never speaks on one itself. This self-test runs with no host and therefore no responders,
                // which is exactly the "neither Fortunes nor AI Brain installed" case, so the three fields
                // must be absent. Asserting the FIELDS rather than the group heading, because the heading is
                // rendered from whatever fields survive.
                var prefs = DesktopAICompanion.Wpf.OptionsShell.BuildPreferencesPane();
                var dropIds = new System.Collections.Generic.List<string>();
                if (prefs != null && prefs.Schema != null)
                    foreach (var f in prefs.Schema)
                        if (f != null && f.Id != null && f.Id.StartsWith("randomDrop", StringComparison.Ordinal))
                            dropIds.Add(f.Id);
                ok &= Check(sb, "no drop responder hides the fortune/insight drop settings",
                    dropIds.Count == 0);
                // ...and the rest of the pane is untouched, so the filter cannot pass by emptying it.
                bool keptOthers = false;
                if (prefs != null && prefs.Schema != null)
                    foreach (var f in prefs.Schema)
                        if (f != null && f.Id == "speech") keptOthers = true;
                ok &= Check(sb, "...without removing anything else from Preferences", keptOthers);

                // --- diagnostic-log pane ---
                // The category and module toggles are GENERATED, and the settings store them INVERTED
                // (muted, so absent means log everything). That inversion is the part that breaks silently:
                // get it backwards and a fresh install logs nothing while every checkbox reads ticked.
                var diagIds = new System.Collections.Generic.List<string>();
                foreach (var f in prefs.Schema)
                    if (f != null && f.Id != null && f.Id.StartsWith("diag", StringComparison.Ordinal))
                        diagIds.Add(f.Id);
                ok &= Check(sb, "the pane offers the log switch and both rotation settings",
                    diagIds.Contains("diagLog") && diagIds.Contains("diagLogKb") && diagIds.Contains("diagLogKeep"));
                // One per LogCategory, generated from the enum, so a new category cannot be forgotten here.
                int categoryFields = 0;
                foreach (var f in prefs.Schema)
                    if (f != null && f.Id != null && f.Id.StartsWith("diagCat_", StringComparison.Ordinal))
                        categoryFields++;
                ok &= Check(sb, "one toggle per LogCategory, generated from the enum",
                    categoryFields == Enum.GetValues(typeof(LogCategory)).Length);

                // Polarity, both directions, against the pure helper rather than the Load dictionary: Load
                // needs a LocalData this headless self-test does not have, so asserting through it would
                // test "settings are absent" and pass for the wrong reason.
                ok &= Check(sb, "a category absent from the muted list reads as ON",
                    DesktopAICompanion.Wpf.OptionsShell.IsCategoryLogged("", LogCategory.Tray));
                ok &= Check(sb, "Animation defaults to OFF (it is per-frame churn)",
                    !DesktopAICompanion.Wpf.OptionsShell.IsCategoryLogged("", LogCategory.Animation));
                ok &= Check(sb, "a muted category reads as OFF",
                    !DesktopAICompanion.Wpf.OptionsShell.IsCategoryLogged("Tray", LogCategory.Tray));
                ok &= Check(sb, "Animation opted back in reads as ON",
                    DesktopAICompanion.Wpf.OptionsShell.IsCategoryLogged("-Animation", LogCategory.Animation));
                ok &= Check(sb, "a module absent from the muted list reads as ON",
                    DesktopAICompanion.Wpf.OptionsShell.IsModuleLogged("", "aibrain"));
                ok &= Check(sb, "a muted module reads as OFF",
                    !DesktopAICompanion.Wpf.OptionsShell.IsModuleLogged("aibrain", "aibrain"));

                // And the log itself must honour the muted list it is handed. Empty list first: that is a
                // fresh install, where everything must be recorded EXCEPT the per-frame animation churn --
                // the one default that is not "absent means on", so it is the one that can silently drift.
                DiagnosticLog.Configure(true, 512, 2, "", "");
                ok &= Check(sb, "an empty muted list records an ordinary category",
                    DiagnosticLog.IsEnabled(LogCategory.Tray, null));
                ok &= Check(sb, "...but Animation is still muted by default",
                    !DiagnosticLog.IsEnabled(LogCategory.Animation, null));
                DiagnosticLog.Configure(true, 512, 2, "Tray", "");
                ok &= Check(sb, "a muted category stops being recorded",
                    !DiagnosticLog.IsEnabled(LogCategory.Tray, null));
                ok &= Check(sb, "...while the others still are",
                    DiagnosticLog.IsEnabled(LogCategory.App, null));
                DiagnosticLog.Configure(true, 512, 2, "-Animation", "");
                ok &= Check(sb, "Animation can be opted back IN without inverting the whole list",
                    DiagnosticLog.IsEnabled(LogCategory.Animation, null));
                DiagnosticLog.Configure(true, 512, 2, "", "aibrain");
                ok &= Check(sb, "a muted module's own lines stop being recorded",
                    !DiagnosticLog.IsEnabled(LogCategory.Modules, "aibrain"));
                ok &= Check(sb, "...while another module's are kept",
                    DiagnosticLog.IsEnabled(LogCategory.Modules, "fortunes"));
                DiagnosticLog.Configure(false, 512, 2, "", "");
                ok &= Check(sb, "the master switch turns everything off",
                    !DiagnosticLog.IsEnabled(LogCategory.App, null));
                // --- the CLASSIFIER, which had no coverage at all until a real log was read ---
                // The filter above was mutation-tested to death and every case fired, while Infer() was
                // quietly putting 43 of 53 lines from an actual startup into App -- including all 35
                // sound-staging lines and every animation-graph line. Testing the filter proves nothing
                // about the routing. These are the VERBATIM strings from that log, so the measurement that
                // found the bug is the thing standing guard over it.
                ok &= Check(sb, "sound staging is Audio, not App",
                    DiagnosticLog.Infer("adding sound (ani.61)") == LogCategory.Audio);
                ok &= Check(sb, "a dead-end in the animation graph is Animation",
                    DiagnosticLog.Infer("no next animation found") == LogCategory.Animation);
                ok &= Check(sb, "child teardown is Animation, matching child setup",
                    DiagnosticLog.Infer("removing child") == LogCategory.Animation);
                ok &= Check(sb, "frame staging is Animation",
                    DiagnosticLog.Infer("304 shared frames ready") == LogCategory.Animation);
                ok &= Check(sb, "spawning a companion is Companions, not App",
                    DiagnosticLog.Infer("new pet...") == LogCategory.Companions);
                ok &= Check(sb, "the tray outcome line is still Tray",
                    DiagnosticLog.Infer("tray icon set: success=True") == LogCategory.Tray);

            // SettingField.Min/Max are HONOURED as of 1.1.5. They had no reader anywhere before
            // that: an author declared bounds, the host rendered a plain TextBox, and the value went
            // through untouched, so two ABI members did nothing. The one module that set them
            // (AgentFlow) was safe only because it clamps again in its own Save.
            //
            // Both ends, plus the three pass-through cases, because a clamp that is too eager is its
            // own defect: it would invent a value the user never typed.
            var boundedField = new SettingField
            {
                Id = "threshold", Kind = SettingKind.Int, Label = "Threshold", Min = 10, Max = 600,
            };
                ok &= Check(sb, "bounds: a value above Max is clamped down",
                    DesktopAICompanion.Wpf.PaneView.ClampIfBounded(boundedField, "9000") == "600");
                ok &= Check(sb, "bounds: a value below Min is clamped up",
                    DesktopAICompanion.Wpf.PaneView.ClampIfBounded(boundedField, "1") == "10");
                ok &= Check(sb, "bounds: WITNESS a value inside the range is untouched",
                    DesktopAICompanion.Wpf.PaneView.ClampIfBounded(boundedField, "45") == "45");
                ok &= Check(sb, "bounds: unparseable text is left for the module's own fallback, not turned into Min",
                    DesktopAICompanion.Wpf.PaneView.ClampIfBounded(boundedField, "abc") == "abc"
                    && DesktopAICompanion.Wpf.PaneView.ClampIfBounded(boundedField, "") == "");
                ok &= Check(sb, "bounds: a field that declares no range is untouched",
                    DesktopAICompanion.Wpf.PaneView.ClampIfBounded(
                    new SettingField { Id = "n", Kind = SettingKind.Int, Min = 0, Max = 0 }, "9000")
                        == "9000");
                ok &= Check(sb, "bounds: a non-Int field is untouched even with a range set",
                    DesktopAICompanion.Wpf.PaneView.ClampIfBounded(
                    new SettingField { Id = "t", Kind = SettingKind.Text, Min = 1, Max = 2 }, "9000")
                        == "9000");
                // ---- a ReloadPaneAfter action must not eat the user's unsaved edits ----
                // Reported from a real install: choose a recording device, click a button in the same
                // pane, and the choice is gone with no error and nothing saved. The rebuild those
                // buttons ask for replaced the whole tree, so every edit on screen went with it -- and
                // the rebuild ends by greying Apply out, so the next click did nothing at all.
                //
                // Precedence is per FIELD, which is what these cases pin down: the action owns what it
                // wrote, the user owns everything else.
                var wasLoaded = new Dictionary<string, string>
                {
                    ["whisperExe"] = "", ["sysDevice"] = "System default output", ["hotkey"] = "Ctrl+Alt+R",
                };
                var editedOnScreen = new Dictionary<string, string>
                {
                    ["whisperExe"] = "", ["sysDevice"] = "Headphones (Astro)", ["hotkey"] = "Ctrl+Alt+R",
                };
                // What Load says after the action wrote the path it found.
                var afterAction = new Dictionary<string, string>
                {
                    ["whisperExe"] = "C:/whisper/whisper-cli.exe", ["sysDevice"] = "System default output",
                    ["hotkey"] = "Ctrl+Alt+R",
                };
                int restored;
                Dictionary<string, string> merged = DesktopAICompanion.Wpf.PaneView.MergeAfterAction(
                    afterAction, wasLoaded, editedOnScreen, out restored);
                ok &= Check(sb, "reload: WITNESS an unsaved edit the action did not touch is put back",
                    merged["sysDevice"] == "Headphones (Astro)");
                ok &= Check(sb, "reload: what the action itself wrote is shown",
                    merged["whisperExe"] == "C:/whisper/whisper-cli.exe");
                ok &= Check(sb, "reload: a field nobody changed is left alone",
                    merged["hotkey"] == "Ctrl+Alt+R");
                ok &= Check(sb, "reload: the restored count is what re-enables Apply", restored == 1);

                // The action wins where the two collide -- that is why "reset to defaults" reloads.
                int clash;
                Dictionary<string, string> contested = DesktopAICompanion.Wpf.PaneView.MergeAfterAction(
                    new Dictionary<string, string> { ["sysDevice"] = "Written by the action" },
                    new Dictionary<string, string> { ["sysDevice"] = "System default output" },
                    new Dictionary<string, string> { ["sysDevice"] = "Picked by the user" },
                    out clash);
                ok &= Check(sb, "reload: WITNESS the action wins a field they both changed",
                    contested["sysDevice"] == "Written by the action" && clash == 0);

                // No edits at all must restore nothing, or every action click would arm Apply.
                int untouched;
                DesktopAICompanion.Wpf.PaneView.MergeAfterAction(
                    new Dictionary<string, string> { ["a"] = "1" },
                    new Dictionary<string, string> { ["a"] = "1" },
                    new Dictionary<string, string> { ["a"] = "1" },
                    out untouched);
                ok &= Check(sb, "reload: an untouched pane restores nothing", untouched == 0);

                // A field the rebuild dropped from the schema cannot be resurrected into the values.
                int gone;
                Dictionary<string, string> dropped = DesktopAICompanion.Wpf.PaneView.MergeAfterAction(
                    new Dictionary<string, string>(),
                    new Dictionary<string, string> { ["vanished"] = "before" },
                    new Dictionary<string, string> { ["vanished"] = "edited" },
                    out gone);
                ok &= Check(sb, "reload: a field the rebuild dropped is not resurrected",
                    !dropped.ContainsKey("vanished") && gone == 0);
                ok &= Check(sb, "a module line is still Modules",
                    DiagnosticLog.Infer("[module] module loaded: aibrain 1.0.0") == LogCategory.Modules);
                ok &= Check(sb, "an unrecognised line still lands in App rather than vanishing",
                    DiagnosticLog.Infer("init application...") == LogCategory.App);

                // THE END-TO-END PROPERTY, and the one that was actually broken. Animation is muted by
                // default; if churn is classified as App it gets written anyway, so the mute silently fails
                // to suppress the only thing it exists for and the cap still evicts the startup record.
                DiagnosticLog.Configure(true, 512, 2, "", "");
                ok &= Check(sb, "with Animation muted, animation churn is genuinely not recorded",
                    !DiagnosticLog.IsEnabled(DiagnosticLog.Infer("no next animation found"), null) &&
                    !DiagnosticLog.IsEnabled(DiagnosticLog.Infer("removing child"), null));
                // ...and the near-miss that must NOT be swept up with it: this one reads like animation
                // churn but explains a companion that never appeared, so muting Animation must not hide it.
                ok &= Check(sb, "...but a companion that has no animations still reports itself",
                    DiagnosticLog.IsEnabled(DiagnosticLog.Infer("No animations for this pet"), null));

                // Leave it as the user would have it, so a later self-test in this process still logs.
                DiagnosticLog.Configure(true, 512, 2, "", "");
                ok &= Check(sb, "collect includes the host Companions pane, alphabetized into the tail",
                    panes != null && panes.Count >= 3 && panes[2] != null && panes[2].Title == "Companions" && !panes[2].HasApply);

                // 1b) "Trigger Speech" options: always offers the default (mapping to ""), and every entry
                // round-trips label -> module id -> label. With no host running (this self-test) that is the
                // single default entry, proving a zero-module install still renders a usable dropdown.
                List<string> speechLabels;
                Dictionary<string, string> labelToModule, moduleToLabel;
                DesktopAICompanion.Wpf.OptionsShell.BuildTriggerSpeechOptions(out speechLabels, out labelToModule, out moduleToLabel);
                bool speechOptionsOk =
                    speechLabels != null && speechLabels.Count >= 1 &&
                    speechLabels[0] == DesktopAICompanion.Wpf.OptionsShell.TriggerSpeechDefaultLabel &&
                    labelToModule[DesktopAICompanion.Wpf.OptionsShell.TriggerSpeechDefaultLabel] == "" &&
                    moduleToLabel[""] == DesktopAICompanion.Wpf.OptionsShell.TriggerSpeechDefaultLabel;
                foreach (string label in speechLabels)
                {
                    string moduleId;
                    if (!labelToModule.TryGetValue(label, out moduleId)) { speechOptionsOk = false; break; }
                    string back;
                    if (!moduleToLabel.TryGetValue(moduleId, out back) || back != label) { speechOptionsOk = false; break; }
                }
                ok &= Check(sb, "trigger-speech options offer the default and round-trip label<->module id", speechOptionsOk);

                // 1c) List-card filtering matches identity (label/group/id) but NOT the generated Detail.
                // Regression guard: Detail holds "964 lines · spicy", and because every row contains the
                // word "lines", including it made a query like "lin" match the entire list.
                var pack = new ListItem
                {
                    Id = "off-linux",
                    Label = "Linux In-Jokes (crude)",
                    Detail = "7 lines · spicy",
                    Group = "NSFW (fortune -o)",
                };
                var unrelated = new ListItem
                {
                    Id = "off-sex",
                    Label = "Sex Jokes",
                    Detail = "592 lines · spicy",
                    Group = "NSFW (fortune -o)",
                };
                ok &= Check(sb, "list filter matches a label substring",
                    DesktopAICompanion.Wpf.PaneView.MatchesFilter(pack, "lin"));
                ok &= Check(sb, "list filter ignores the generated detail text",
                    !DesktopAICompanion.Wpf.PaneView.MatchesFilter(unrelated, "lin") &&
                    !DesktopAICompanion.Wpf.PaneView.MatchesFilter(unrelated, "lines") &&
                    !DesktopAICompanion.Wpf.PaneView.MatchesFilter(unrelated, "spicy"));
                ok &= Check(sb, "list filter matches group and id, and an empty query matches everything",
                    DesktopAICompanion.Wpf.PaneView.MatchesFilter(unrelated, "nsfw") &&
                    DesktopAICompanion.Wpf.PaneView.MatchesFilter(unrelated, "off-sex") &&
                    DesktopAICompanion.Wpf.PaneView.MatchesFilter(unrelated, ""));

                // 2) PaneView renders all five field kinds + round-trips values.
                var saved = new Dictionary<string, string>(StringComparer.Ordinal);
                bool listLoaded = false;
                var pane = new OptionsPane
                {
                    Title = "Probe",
                    Schema = new[]
                    {
                        new SettingField { Id = "b", Label = "Bool", Kind = SettingKind.Bool },
                        new SettingField { Id = "i", Label = "Int", Kind = SettingKind.Int, Min = 0, Max = 100 },
                        new SettingField { Id = "t", Label = "Text", Kind = SettingKind.Text },
                        new SettingField { Id = "e", Label = "Enum", Kind = SettingKind.Enum, Options = new[] { "x", "y", "z" } },
                        new SettingField { Id = "s", Label = "Secret", Kind = SettingKind.Secret },
                    },
                    Load = delegate
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal)
                        { { "b", "true" }, { "i", "42" }, { "t", "hello" }, { "e", "y" }, { "s", "set" } };
                    },
                    Save = delegate(IReadOnlyDictionary<string, string> v)
                    {
                        foreach (KeyValuePair<string, string> kv in v) saved[kv.Key] = kv.Value;
                        return true;
                    },
                    Actions = new[]
                    {
                        new PaneAction { Label = "Probe action", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult("ok"); } },
                    },
                    Lists = new[]
                    {
                        new ListCard
                        {
                            Title = "Probe list",
                            LoadItems = delegate { listLoaded = true; return new[] { new ListItem { Id = "a", Label = "A", Detail = "1 line", Checked = true } }; },
                            SetChecked = delegate { },
                            EmptyHint = "none",
                            Actions = new[] { new PaneAction { Label = "Rescan", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult("ok"); }, ReloadPaneAfter = true } },
                        },
                    },
                };

                var view = new DesktopAICompanion.Wpf.PaneView(pane);
                object element = view.Build();   // constructs the WPF control tree (STA)
                ok &= Check(sb, "PaneView.Build produced content", element != null);
                ok &= Check(sb, "list card LoadItems invoked during Build", listLoaded);

                Dictionary<string, string> collected = view.Collect();
                string val;
                ok &= Check(sb, "bool round-trips from Load", collected.TryGetValue("b", out val) && val == "true");
                ok &= Check(sb, "int round-trips from Load", collected.TryGetValue("i", out val) && val == "42");
                ok &= Check(sb, "text round-trips from Load", collected.TryGetValue("t", out val) && val == "hello");
                ok &= Check(sb, "enum round-trips from Load", collected.TryGetValue("e", out val) && val == "y");
                ok &= Check(sb, "blank secret is omitted from collect (write-only)", !collected.ContainsKey("s"));

                // 3) Save forwards the collected values to the pane's Save (secret still omitted).
                ok &= Check(sb, "PaneView.Save forwards to the pane", view.Save());
                ok &= Check(sb, "saved carries the non-secret fields, not the secret", saved.ContainsKey("b") && !saved.ContainsKey("s"));

                // 4) Pane actions (S5b): the action invokes and returns its status string.
                string actionResult = pane.Actions[0].InvokeAsync().GetAwaiter().GetResult();
                ok &= Check(sb, "pane action invokes + returns a status", actionResult == "ok");

                // 5) Grouped list cards get a whole-group checkbox on the Expander header. Without it,
                // switching off a section (the 19 NSFW fortune packs) is 19 individual clicks. The contract
                // that matters: clicking it must run the card's SetChecked once per item that actually
                // changed -- the module persists on that callback, so a shortcut that only moved the boxes
                // visually would silently drop the user's change.
                var setCalls = new List<string>();
                var grouped = new OptionsPane
                {
                    Title = "Grouped",
                    Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal); },
                    Lists = new[]
                    {
                        new ListCard
                        {
                            Title = "Packs",
                            CollapseGroups = true,
                            LoadItems = delegate
                            {
                                return new[]
                                {
                                    new ListItem { Id = "n1", Label = "N1", Group = "NSFW", Checked = true },
                                    new ListItem { Id = "n2", Label = "N2", Group = "NSFW", Checked = false },
                                    new ListItem { Id = "c1", Label = "C1", Group = "Clean", Checked = true },
                                };
                            },
                            SetChecked = delegate(string id, bool on) { setCalls.Add(id + "=" + (on ? "1" : "0")); },
                        },
                    },
                };
                var groupedView = new DesktopAICompanion.Wpf.PaneView(grouped);
                var groupedRoot = groupedView.Build() as System.Windows.DependencyObject;
                var expanders = new List<System.Windows.Controls.Expander>();
                CollectExpanders(groupedRoot, expanders);
                ok &= Check(sb, "one Expander per group, collapsed by CollapseGroups",
                    expanders.Count == 2 && !expanders[0].IsExpanded);

                var nsfw = expanders.Find(delegate(System.Windows.Controls.Expander e)
                {
                    return HeaderText(e).StartsWith("NSFW", StringComparison.Ordinal);
                });
                System.Windows.Controls.CheckBox groupBox = nsfw == null ? null : HeaderCheck(nsfw);
                ok &= Check(sb, "group header carries a checkbox", groupBox != null);
                if (groupBox != null)
                {
                    ok &= Check(sb, "a partly-checked group reads as indeterminate", groupBox.IsChecked == null);

                    // Simulate the user click: ToggleButton flips IsChecked and then raises Click, and our
                    // handler reads the post-flip value.
                    groupBox.IsChecked = true;
                    groupBox.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    ok &= Check(sb, "checking a group only calls SetChecked for the item that changed",
                        setCalls.Count == 1 && setCalls[0] == "n2=1");
                    ok &= Check(sb, "the group reads as fully checked afterwards", groupBox.IsChecked == true);

                    setCalls.Clear();
                    groupBox.IsChecked = false;
                    groupBox.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    ok &= Check(sb, "unchecking a group calls SetChecked for every item in it",
                        setCalls.Count == 2 && setCalls.Contains("n1=0") && setCalls.Contains("n2=0"));
                    ok &= Check(sb, "the group toggle leaves other groups alone",
                        !setCalls.Contains("c1=0") && !setCalls.Contains("c1=1"));
                }

                // 6) DeferChanges: SetChecked is expensive for some cards (the fortune packs card rebuilds
                // the whole engine), so those cards treat a click as an edit, not a command -- the box moves
                // at once, the pane goes dirty, and the callbacks all run at Apply just before the pane's
                // Save, which is what lets the module commit the batch in one write.
                var log = new List<string>();
                int dirtyCount = 0;
                var deferredCard = new ListCard
                {
                    Title = "Deferred",
                    DeferChanges = true,
                    LoadItems = delegate
                    {
                        return new[]
                        {
                            new ListItem { Id = "d1", Label = "D1", Checked = false },
                            new ListItem { Id = "d2", Label = "D2", Checked = false },
                            new ListItem { Id = "d3", Label = "D3", Checked = true },
                        };
                    },
                    SetChecked = delegate(string id, bool on) { log.Add(id + "=" + (on ? "1" : "0")); },
                };
                var deferredPane = new OptionsPane
                {
                    Title = "Deferred",
                    Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal); },
                    Save = delegate { log.Add("SAVE"); return true; },
                    Lists = new[] { deferredCard },
                };
                var deferredView = new DesktopAICompanion.Wpf.PaneView(deferredPane, null, delegate { dirtyCount++; });
                var deferredRoot = deferredView.Build() as System.Windows.DependencyObject;
                var boxes = new List<System.Windows.Controls.CheckBox>();
                CollectCheckBoxes(deferredRoot, boxes);
                ok &= Check(sb, "deferred card still renders one checkbox per item", boxes.Count == 3);
                if (boxes.Count == 3)
                {
                    boxes[0].IsChecked = true;    // d1: off -> on, a real edit
                    boxes[2].IsChecked = false;   // d3: on -> off, a real edit
                    boxes[1].IsChecked = true;    // d2: off -> on ...
                    boxes[1].IsChecked = false;   // ... and back, so it is not an edit at all
                    ok &= Check(sb, "deferred ticks do no work until Apply", log.Count == 0);
                    ok &= Check(sb, "deferred ticks still mark the pane dirty so Apply lights up", dirtyCount > 0);

                    ok &= Check(sb, "deferred Save succeeds", deferredView.Save());
                    ok &= Check(sb, "Apply replays only the boxes that actually changed, in click order",
                        log.Count == 3 && log[0] == "d1=1" && log[1] == "d3=0");
                    ok &= Check(sb, "the replay lands before the pane's Save, so the module can batch it",
                        log.Count == 3 && log[2] == "SAVE");

                    // A second Apply with no further edits must not re-run the callbacks.
                    log.Clear();
                    ok &= Check(sb, "a second Apply replays nothing", deferredView.Save() && log.Count == 1 && log[0] == "SAVE");
                }

                // ================= the six additive 1.1.6 ABI members, rendered here for the first time =====

                // 7) SettingKind.Radio. The kind exists so three options can be three visible choices
                // instead of a dropdown; what makes it safe to ADOPT is that it stores exactly what Enum
                // stores, so moving a field between the two is a rendering change and not a settings
                // migration. Both kinds are therefore asserted side by side on the same Options and the
                // same stored value, including the stored value that matches no option at all: that is the
                // case where two plausible implementations disagree (nothing selected, or the first one).
                int radioDirty = 0;
                var radioPane = new OptionsPane
                {
                    Title = "Radio",
                    Schema = new[]
                    {
                        new SettingField { Id = "r", Label = "Radio", Kind = SettingKind.Radio, Options = new[] { "alpha", "beta", "gamma" } },
                        new SettingField { Id = "e", Label = "Enum", Kind = SettingKind.Enum, Options = new[] { "alpha", "beta", "gamma" } },
                        new SettingField { Id = "r2", Label = "Radio (stale)", Kind = SettingKind.Radio, Options = new[] { "a", "b" } },
                        new SettingField { Id = "e2", Label = "Enum (stale)", Kind = SettingKind.Enum, Options = new[] { "a", "b" } },
                    },
                    Load = delegate
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal)
                        { { "r", "beta" }, { "e", "beta" }, { "r2", "gone" }, { "e2", "gone" } };
                    },
                };
                var radioView = new DesktopAICompanion.Wpf.PaneView(radioPane, null, delegate { radioDirty++; });
                var radioRoot = radioView.Build() as System.Windows.DependencyObject;
                var radios = new List<System.Windows.Controls.RadioButton>();
                CollectAll(radioRoot, radios);
                ok &= Check(sb, "Radio renders one RadioButton per option", radios.Count == 5);
                Dictionary<string, string> rc = radioView.Collect();
                ok &= Check(sb, "Radio collects the stored option, identically to Enum",
                    rc.ContainsKey("r") && rc["r"] == "beta" && rc["r"] == rc["e"]);
                ok &= Check(sb, "a stored value matching no option collects as empty for BOTH kinds",
                    rc["r2"] == "" && rc["r2"] == rc["e2"]);
                if (radios.Count >= 3)
                {
                    // Read before the ComboBox below is touched. Measured against a running total it
                    // passed with the radio's change handler deleted outright, because the Enum edit two
                    // lines later had already raised the flag.
                    int dirtyBeforeRadio = radioDirty;
                    radios[2].IsChecked = true;   // "gamma"
                    ok &= Check(sb, "a radio click marks the pane dirty, like every other editor",
                        radioDirty > dirtyBeforeRadio);
                    var radioCombos = new List<System.Windows.Controls.ComboBox>();
                    CollectAll(radioRoot, radioCombos);
                    radioCombos[0].SelectedItem = "gamma";
                    rc = radioView.Collect();
                    ok &= Check(sb, "Radio and Enum still agree after the user moves both",
                        rc["r"] == "gamma" && rc["r"] == rc["e"]);
                }

                // 8) SettingKind.Header. Display-only like Info, so the trap is the same one Info has:
                // a heading that collected itself would arrive at the module's Save as a settings value
                // it never declared.
                var headerPane = new OptionsPane
                {
                    Title = "Header",
                    Schema = new[]
                    {
                        new SettingField { Id = "h", Label = "Where your files live", Kind = SettingKind.Header },
                        new SettingField { Id = "t", Label = "Folder", Kind = SettingKind.Text },
                    },
                    Load = delegate
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal)
                        { { "h", "Everything below is written beside the app." }, { "t", "kept" } };
                    },
                    Save = delegate { return true; },
                };
                var headerView = new DesktopAICompanion.Wpf.PaneView(headerPane);
                var headerRoot = headerView.Build() as System.Windows.DependencyObject;
                var headerBlocks = new List<System.Windows.Controls.TextBlock>();
                CollectAll(headerRoot, headerBlocks);
                bool boldHeading = false, plainParagraph = false;
                foreach (System.Windows.Controls.TextBlock tb in headerBlocks)
                {
                    if (tb.Text == "Where your files live" && tb.FontWeight == System.Windows.FontWeights.Bold) boldHeading = true;
                    if (tb.Text == "Everything below is written beside the app." && tb.FontWeight != System.Windows.FontWeights.Bold) plainParagraph = true;
                }
                ok &= Check(sb, "Header renders its Label as a bold heading", boldHeading);
                ok &= Check(sb, "Header renders the loaded value as a plain paragraph beneath it", plainParagraph);
                Dictionary<string, string> hc = headerView.Collect();
                ok &= Check(sb, "Header registers no reader, so Collect never sends it to Save",
                    !hc.ContainsKey("h") && hc.ContainsKey("t") && hc["t"] == "kept");

                // 9) SettingField.EnabledWhen. The grey-out is the visible half; the half that destroys
                // data if it is wrong is invisible, so it is asserted twice over: the value survives
                // Collect AND it survives all the way into the module's Save.
                var gateSaved = new Dictionary<string, string>(StringComparer.Ordinal);
                var gatePane = new OptionsPane
                {
                    Title = "Gate",
                    Schema = new[]
                    {
                        new SettingField { Id = "mode", Label = "Mode", Kind = SettingKind.Enum, Options = new[] { "off", "notify" } },
                        new SettingField { Id = "target", Label = "Notify", Kind = SettingKind.Text, EnabledWhen = "mode=notify" },
                        // Names a field declared AFTER it, and is SATISFIED by that field's loaded value.
                        // Deliberately that way round: a forward reference that quietly read nothing would
                        // also leave the row greyed out, so asserting "greyed out" here would pass for
                        // precisely the reason it is meant to catch.
                        new SettingField { Id = "early", Label = "Early", Kind = SettingKind.Text, EnabledWhen = "late=yes" },
                        new SettingField { Id = "late", Label = "Late", Kind = SettingKind.Enum, Options = new[] { "yes", "no" } },
                        // Gated on a display-only field, which registers no reader at all, so the loaded
                        // value is the only thing that can answer it.
                        new SettingField { Id = "note", Label = "Note", Kind = SettingKind.Info },
                        new SettingField { Id = "gatedOnInfo", Label = "Gated", Kind = SettingKind.Text, EnabledWhen = "note=ready" },
                    },
                    Load = delegate
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            { "mode", "off" }, { "target", "someone@example.invalid" }, { "early", "kept" },
                            { "late", "yes" }, { "note", "ready" }, { "gatedOnInfo", "alsokept" },
                        };
                    },
                    Save = delegate(IReadOnlyDictionary<string, string> v)
                    {
                        foreach (KeyValuePair<string, string> kv in v) gateSaved[kv.Key] = kv.Value;
                        return true;
                    },
                };
                var gateView = new DesktopAICompanion.Wpf.PaneView(gatePane);
                var gateRoot = gateView.Build() as System.Windows.DependencyObject;
                ok &= Check(sb, "an unmet EnabledWhen greys its field out",
                    gateView.RowFor("target") != null && !gateView.RowFor("target").IsEnabled);
                ok &= Check(sb, "EnabledWhen naming a field declared LATER is still evaluated",
                    gateView.RowFor("early") != null && gateView.RowFor("early").IsEnabled);
                ok &= Check(sb, "EnabledWhen can name a display-only field, which has no reader of its own",
                    gateView.RowFor("gatedOnInfo") != null && gateView.RowFor("gatedOnInfo").IsEnabled);
                ok &= Check(sb, "WITNESS a field with no EnabledWhen is left enabled",
                    gateView.RowFor("mode") != null && gateView.RowFor("mode").IsEnabled);
                Dictionary<string, string> gc = gateView.Collect();
                ok &= Check(sb, "a greyed-out field's value is STILL collected, unchanged",
                    gc.ContainsKey("target") && gc["target"] == "someone@example.invalid");
                ok &= Check(sb, "...and still reaches Save, so a disabled editor cannot blank a stored setting",
                    gateView.Save() && gateSaved.ContainsKey("target") && gateSaved["target"] == "someone@example.invalid");
                var gateCombos = new List<System.Windows.Controls.ComboBox>();
                CollectAll(gateRoot, gateCombos);
                if (gateCombos.Count == 2)
                {
                    gateCombos[0].SelectedItem = "notify";
                    bool targetWentLive = gateView.RowFor("target").IsEnabled;
                    ok &= Check(sb, "the grey-out lifts LIVE as the field it depends on changes", targetWentLive);
                    ok &= Check(sb, "...and an unrelated dependent is left alone by that edit",
                        gateView.RowFor("early").IsEnabled);
                    gateCombos[1].SelectedItem = "no";
                    ok &= Check(sb, "the later-declared dependency updates live too",
                        !gateView.RowFor("early").IsEnabled);
                    gateCombos[0].SelectedItem = "off";
                    // Asserted as a TRANSITION, not as a final state. "Ends up greyed out" is also what a
                    // pane with no live refresh at all looks like, so on its own it passed with the whole
                    // per-edit refresh deleted.
                    ok &= Check(sb, "and it greys out again when the value moves away",
                        targetWentLive && !gateView.RowFor("target").IsEnabled);
                }
                else ok &= Check(sb, "gate probe rendered both dropdowns", false);

                // 10) SettingField.FullWidth / PinTop, both card-level and both read from the group's
                // FIRST field. Alpha sets both on its SECOND field, which must change nothing: a card
                // cannot be half wide, and letting any member vote would make the layout depend on schema
                // order in a way the module author never sees.
                var layoutPane = new OptionsPane
                {
                    Title = "Layout",
                    Schema = new[]
                    {
                        new SettingField { Id = "a1", Label = "A1", Kind = SettingKind.Text, Group = "Alpha" },
                        new SettingField { Id = "a2", Label = "A2", Kind = SettingKind.Text, Group = "Alpha", PinTop = true, FullWidth = true },
                        new SettingField { Id = "b1", Label = "B1", Kind = SettingKind.Text, Group = "Beta" },
                        new SettingField { Id = "c1", Label = "C1", Kind = SettingKind.Text, Group = "Gamma", PinTop = true },
                        new SettingField { Id = "d1", Label = "D1", Kind = SettingKind.Text, Group = "Delta", FullWidth = true },
                        new SettingField { Id = "e1", Label = "E1", Kind = SettingKind.Text, Group = "Epsilon", PinTop = true },
                    },
                    Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal); },
                };
                var layoutRoot = new DesktopAICompanion.Wpf.PaneView(layoutPane).Build() as System.Windows.DependencyObject;
                var masonry = new List<DesktopAICompanion.Wpf.MasonryPanel>();
                CollectAll(layoutRoot, masonry);
                var cardTitles = new List<string>();
                var cardSpans = new List<bool>();
                if (masonry.Count == 1)
                    foreach (System.Windows.UIElement card in masonry[0].Children)
                    {
                        cardTitles.Add(CardTitle(card as System.Windows.Controls.Border));
                        cardSpans.Add(DesktopAICompanion.Wpf.MasonryPanel.GetSpanAllColumns(card));
                    }
                ok &= Check(sb, "PinTop on a group's first field moves that card ahead, stably",
                    string.Join(",", cardTitles) == "Gamma,Epsilon,Alpha,Beta,Delta");
                ok &= Check(sb, "FullWidth on a group's first field makes the card span every column",
                    cardTitles.IndexOf("Delta") >= 0 && cardSpans[cardTitles.IndexOf("Delta")]);
                ok &= Check(sb, "PinTop and FullWidth on a field that is NOT the group's first are ignored",
                    cardTitles.IndexOf("Alpha") == 2 && !cardSpans[cardTitles.IndexOf("Alpha")]);

                // ...and the panel genuinely lays a spanning card across the whole width with nothing
                // sliding up beside it. A flag that no layout pass reads would satisfy all three above.
                var probe = new DesktopAICompanion.Wpf.MasonryPanel();
                var probeLeft = new System.Windows.Controls.Border { Width = 360, Height = 100 };
                var probeRight = new System.Windows.Controls.Border { Width = 360, Height = 100 };
                var probeWide = new System.Windows.Controls.Border { Height = 50, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
                var probeAfter = new System.Windows.Controls.Border { Width = 360, Height = 100 };
                DesktopAICompanion.Wpf.MasonryPanel.SetSpanAllColumns(probeWide, true);
                probe.Children.Add(probeLeft);
                probe.Children.Add(probeRight);
                probe.Children.Add(probeWide);
                probe.Children.Add(probeAfter);
                probe.Measure(new System.Windows.Size(1000, double.PositiveInfinity));
                probe.Arrange(new System.Windows.Rect(0, 0, 1000, probe.DesiredSize.Height));
                System.Windows.Rect leftSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(probeLeft);
                System.Windows.Rect rightSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(probeRight);
                System.Windows.Rect wideSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(probeWide);
                System.Windows.Rect afterSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(probeAfter);
                ok &= Check(sb, "a spanning card is laid out across the whole panel from x = 0",
                    wideSlot.X == 0 && wideSlot.Width == 1000);
                ok &= Check(sb, "WITNESS an ordinary card still takes a single column",
                    leftSlot.X == 0 && rightSlot.X == 368 && leftSlot.Width == 360);
                ok &= Check(sb, "nothing is placed beside a spanning card, before it or after it",
                    wideSlot.Y >= leftSlot.Y + leftSlot.Height && afterSlot.Y >= wideSlot.Y + wideSlot.Height);

                // 11) OptionsPane.LoadPending and SettingField.ReloadOnChange. First the unchanged case,
                // which is what every shipped module depends on: no LoadPending means Load, and values are
                // still obtained BEFORE Schema is read (the core Companions pane rebuilds a field's Options
                // from inside Load, so the other order would render the previous open's list).
                int loadOnlyCalls = 0;
                var lateField = new SettingField { Id = "who", Label = "Who", Kind = SettingKind.Enum, Options = new string[0] };
                var loadOnlyPane = new OptionsPane
                {
                    Title = "LoadOnly",
                    Schema = new[] { lateField },
                    Load = delegate
                    {
                        loadOnlyCalls++;
                        lateField.Options = new[] { "chosen-at-load" };
                        return new Dictionary<string, string>(StringComparer.Ordinal) { { "who", "chosen-at-load" } };
                    },
                };
                var loadOnlyView = new DesktopAICompanion.Wpf.PaneView(loadOnlyPane);
                loadOnlyView.Build();
                ok &= Check(sb, "with LoadPending null, Load is still the source of values", loadOnlyCalls == 1);
                ok &= Check(sb, "values are still obtained BEFORE Schema is read",
                    loadOnlyView.Collect()["who"] == "chosen-at-load");

                var petField = new SettingField { Id = "pet", Label = "Pet", Kind = SettingKind.Enum, Options = new[] { "cat", "dog" }, ReloadOnChange = true };
                var animField = new SettingField { Id = "anim", Label = "Animation", Kind = SettingKind.Enum, Options = new string[0] };
                int cascadeLoadCalls = 0;
                IReadOnlyDictionary<string, string> seenPending = null;
                var cascadePane = new OptionsPane
                {
                    Title = "Cascade",
                    Schema = new[] { petField, animField },
                    Load = delegate { cascadeLoadCalls++; return new Dictionary<string, string>(StringComparer.Ordinal) { { "pet", "cat" } }; },
                    LoadPending = delegate(IReadOnlyDictionary<string, string> onScreen)
                    {
                        seenPending = onScreen;
                        string pet;
                        if (onScreen == null || !onScreen.TryGetValue("pet", out pet) || string.IsNullOrEmpty(pet)) pet = "cat";
                        animField.Options = pet == "dog" ? new[] { "fetch", "bark" } : new[] { "purr", "nap" };
                        return new Dictionary<string, string>(StringComparer.Ordinal) { { "pet", pet }, { "anim", animField.Options[0] } };
                    },
                };
                var firstBuild = new DesktopAICompanion.Wpf.PaneView(cascadePane);
                firstBuild.Build();
                ok &= Check(sb, "LoadPending replaces Load on a pane that supplies one", cascadeLoadCalls == 0);
                ok &= Check(sb, "the first build hands LoadPending an empty dictionary (nothing on screen yet)",
                    seenPending != null && seenPending.Count == 0);
                ok &= Check(sb, "...and what it answers drives the controls", firstBuild.Collect()["anim"] == "purr");

                var cascadeEvents = new List<string>();
                DesktopAICompanion.Wpf.PaneView rebuilt = null;
                // Exactly what the window does on RequestReload: throw the view away, build a fresh one.
                Action rebuild = delegate
                {
                    cascadeEvents.Add("rebuild");
                    rebuilt = new DesktopAICompanion.Wpf.PaneView(cascadePane);
                    rebuilt.Build();
                };
                var cascadeView = new DesktopAICompanion.Wpf.PaneView(cascadePane, rebuild, delegate { cascadeEvents.Add("dirty"); });
                var cascadeRoot = cascadeView.Build() as System.Windows.DependencyObject;
                var cascadeCombos = new List<System.Windows.Controls.ComboBox>();
                CollectAll(cascadeRoot, cascadeCombos);
                if (cascadeCombos.Count == 2)
                {
                    cascadeCombos[0].SelectedItem = "dog";
                    ok &= Check(sb, "changing a ReloadOnChange field rebuilds the pane",
                        cascadeEvents.Contains("rebuild") && rebuilt != null);
                    ok &= Check(sb, "the rebuild reaches LoadPending carrying the value just chosen",
                        seenPending != null && seenPending.ContainsKey("pet") && seenPending["pet"] == "dog");
                    ok &= Check(sb, "...so a later field is rebuilt from a choice that is not saved yet",
                        rebuilt != null && rebuilt.Collect()["anim"] == "fetch");
                    // The host greys Apply out at the END of a rebuild, so a signal raised before it is
                    // thrown away and the user is left unable to save the value they just picked.
                    ok &= Check(sb, "the unsaved-edit signal is re-raised AFTER the rebuild, so Apply stays live",
                        cascadeEvents.Count >= 3 &&
                        cascadeEvents[cascadeEvents.Count - 1] == "dirty" &&
                        cascadeEvents[cascadeEvents.Count - 2] == "rebuild");
                    int rebuildsSoFar = cascadeEvents.FindAll(delegate(string e) { return e == "rebuild"; }).Count;
                    cascadeCombos[1].SelectedItem = "nap";
                    ok &= Check(sb, "WITNESS a field without ReloadOnChange rebuilds nothing",
                        cascadeEvents.FindAll(delegate(string e) { return e == "rebuild"; }).Count == rebuildsSoFar);
                    ok &= Check(sb, "Load is never called on a pane that supplies LoadPending", cascadeLoadCalls == 0);
                }
                else ok &= Check(sb, "cascade probe rendered both dropdowns", false);

                // 12) PaneAction.RevealsPath. The refusals are the feature: an unrestricted "open this
                // path" handed to plugins is a shell execution primitive under another name. Every path
                // below lives in TEMP, which is outside the real data root, so the button-level assertions
                // exercise the refusal and this self-test never opens an Explorer window.
                string revealRoot = Path.Combine(Path.GetTempPath(), "dp-wpf-reveal-" + Guid.NewGuid().ToString("N"));
                string revealSibling = revealRoot + "-outside";
                try
                {
                    Directory.CreateDirectory(revealRoot);
                    Directory.CreateDirectory(revealSibling);
                    string insideFile = Path.Combine(revealRoot, "companion.log");
                    File.WriteAllText(insideFile, "x");
                    string outsideFile = Path.Combine(revealSibling, "notes.txt");
                    File.WriteAllText(outsideFile, "x");

                    string refusal;
                    ok &= Check(sb, "WITNESS RevealsPath allows an existing file inside the permitted root",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(insideFile, revealRoot, out refusal) == insideFile && refusal == null);
                    // The sibling's name deliberately STARTS with the root's, which is the containment bug
                    // a bare StartsWith would ship with.
                    ok &= Check(sb, "RevealsPath REFUSES a file outside the permitted roots",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(outsideFile, revealRoot, out refusal) == null &&
                        refusal != null && refusal.StartsWith("✗"));
                    string outsideMessage = refusal;
                    ok &= Check(sb, "...including one reached by climbing out of the root",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(
                            Path.Combine(revealRoot, "..", Path.GetFileName(revealSibling), "notes.txt"), revealRoot, out refusal) == null);
                    ok &= Check(sb, "RevealsPath refuses a path inside the root that is not an existing file",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(Path.Combine(revealRoot, "absent.log"), revealRoot, out refusal) == null);
                    // A directory INSIDE the root, so containment passes and the existence rule is the
                    // only thing left to refuse it. Handing the root itself made this pass off the
                    // containment check with the existence rule deleted.
                    string insideDir = Path.Combine(revealRoot, "nested");
                    Directory.CreateDirectory(insideDir);
                    ok &= Check(sb, "RevealsPath refuses a directory, which is not a file",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(insideDir, revealRoot, out refusal) == null);
                    // Containment is answered BEFORE existence on purpose: the other order turns the
                    // refusal text into a free "does this file exist?" oracle for any path on the disk.
                    DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(
                        Path.Combine(revealSibling, "never-created.txt"), revealRoot, out refusal);
                    ok &= Check(sb, "an outside path is refused identically whether or not it exists",
                        refusal != null && refusal == outsideMessage);

                    // A reparse point inside the root must not become a way out of it.
                    //
                    // Tested with a JUNCTION, not a symbolic link, and that is the whole point. A symlink
                    // needs Developer Mode or elevation, so the symlink test skipped on this account --
                    // and a check that skips is a check the gate cannot enforce. A junction needs
                    // neither, which also makes it the CHEAPER escape and therefore the one that had to
                    // be covered. mklink /J is used because .NET has no junction API.
                    string junction = Path.Combine(revealRoot, "escape");
                    var mk = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        "/c mklink /J \"" + junction + "\" \"" + Path.GetDirectoryName(outsideFile) + "\"")
                    // The encoding is pinned rather than left to the console codepage. This is a
                    // GUI process with no console, so GetConsoleOutputCP() returns 0 and .NET reads
                    // that as CP_ACP -- the repo-wide invariant exists because that silently
                    // mojibake'd every non-ASCII glyph on a redirect written by someone who had not
                    // met the bug. Nothing here reads the output, but the rule is repo-wide on
                    // purpose and an exception "because this one does not matter" is how it returns.
                    { UseShellExecute = false, CreateNoWindow = true,
                      RedirectStandardOutput = true, RedirectStandardError = true,
                      StandardOutputEncoding = System.Text.Encoding.UTF8,
                      StandardErrorEncoding = System.Text.Encoding.UTF8 };
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(mk)) p.WaitForExit();

                    string throughJunction = Path.Combine(junction, Path.GetFileName(outsideFile));
                    ok &= Check(sb, "the junction escape is actually set up (so the next check is not vacuous)",
                        Directory.Exists(junction) && File.Exists(throughJunction));
                    // WITNESS: this is the case File.ResolveLinkTarget could not see, because the reparse
                    // point is part way ALONG the path rather than at the end of it.
                    ok &= Check(sb, "RevealsPath refuses a path that leaves the root through a junction",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(throughJunction, revealRoot, out refusal) == null
                        && refusal != null);

                    // ...and a symlink too, where the OS allows one to be made. No SKIP either way: the
                    // junction above already covers the property, so this is an extra rather than the
                    // only coverage, and a check that vanishes on some machines is not a check.
                    string linkPath = Path.Combine(revealRoot, "shortcut.log");
                    bool linkMade = false;
                    try { File.CreateSymbolicLink(linkPath, outsideFile); linkMade = true; } catch { }
                    ok &= Check(sb, linkMade
                            ? "RevealsPath refuses a symlink inside the root whose target is outside it"
                            : "RevealsPath refuses a symlink (DEGRADED: this account cannot create one, junction case covered it)",
                        !linkMade
                        || DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(linkPath, revealRoot, out refusal) == null);

                    // ...and through the button, which is where the empty / already-a-result pass-throughs
                    // live: an action must still be able to report a failure the ordinary way.
                    var revealPane = new OptionsPane
                    {
                        Title = "Reveal",
                        Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal); },
                        Actions = new[]
                        {
                            new PaneAction { Label = "Show log", RevealsPath = true, InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(outsideFile); } },
                            new PaneAction { Label = "Already fine", RevealsPath = true, InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult("✓ nothing to show"); } },
                            new PaneAction { Label = "Nothing", RevealsPath = true, InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(""); } },
                        },
                    };
                    var revealTree = new DesktopAICompanion.Wpf.PaneView(revealPane).Build() as System.Windows.DependencyObject;
                    var revealButtons = new List<System.Windows.Controls.Button>();
                    CollectAll(revealTree, revealButtons);
                    if (revealButtons.Count == 3)
                    {
                        foreach (System.Windows.Controls.Button b in revealButtons)
                            b.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        ok &= Check(sb, "a RevealsPath action returning a refused path reports the refusal beside the button",
                            StatusOf(revealButtons[0]) != null && StatusOf(revealButtons[0]).Text.StartsWith("✗"));
                        ok &= Check(sb, "a RevealsPath return that already carries a result marker is shown as a message",
                            StatusOf(revealButtons[1]) != null && StatusOf(revealButtons[1]).Text == "✓ nothing to show");
                        ok &= Check(sb, "a RevealsPath action that returns nothing shows nothing",
                            StatusOf(revealButtons[2]) != null && StatusOf(revealButtons[2]).Text == "");
                    }
                    else ok &= Check(sb, "reveal probe rendered three action buttons", false);
                }
                finally
                {
                    try { Directory.Delete(revealRoot, true); } catch { }
                    try { Directory.Delete(revealSibling, true); } catch { }
                }

                // 12b) RevealsPath is scoped to the OWNING MODULE, not the whole data root.
                // Both roots are inside AppPaths.DataRoot, so the old rule allowed every one of these and
                // the interesting assertion is the one that now REFUSES. Nothing is created on disk beyond
                // the two probe files, and neither is ever opened: ResolveRevealTarget is the decision, and
                // the Explorer call is downstream of it.
                string alphaRoot = DesktopAICompanion.Wpf.PaneView.RevealRootFor("alpha");
                string betaRoot = DesktopAICompanion.Wpf.PaneView.RevealRootFor("beta");
                ok &= Check(sb, "a pane with no owning module keeps the data-root-wide rule",
                    DesktopAICompanion.Wpf.PaneView.RevealRootFor(null) == AppPaths.DataRoot &&
                    DesktopAICompanion.Wpf.PaneView.RevealRootFor("") == AppPaths.DataRoot);
                ok &= Check(sb, "an owned pane is scoped to that module's own storage, inside the data root",
                    alphaRoot != AppPaths.DataRoot && alphaRoot.StartsWith(AppPaths.DataRoot, StringComparison.OrdinalIgnoreCase) &&
                    alphaRoot.EndsWith(Path.Combine("modules", "alpha"), StringComparison.OrdinalIgnoreCase));
                ok &= Check(sb, "two modules do not share a reveal root", alphaRoot != betaRoot);
                try
                {
                    Directory.CreateDirectory(alphaRoot);
                    Directory.CreateDirectory(betaRoot);
                    string alphaFile = Path.Combine(alphaRoot, "own.log");
                    string betaFile = Path.Combine(betaRoot, "someone-elses.log");
                    File.WriteAllText(alphaFile, "x");
                    File.WriteAllText(betaFile, "x");
                    string why;
                    ok &= Check(sb, "WITNESS a module may still reveal a file in its OWN storage",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(alphaFile, alphaRoot, out why) == alphaFile);
                    // THE POINT. This is the case the data-root-wide rule allowed.
                    ok &= Check(sb, "a module may NOT reveal a file inside another module's storage",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(betaFile, alphaRoot, out why) == null && why != null);
                    ok &= Check(sb, "WITNESS the old data-root-wide rule DID allow exactly that",
                        DesktopAICompanion.Wpf.PaneView.ResolveRevealTarget(betaFile, AppPaths.DataRoot, out why) == betaFile);
                }
                finally
                {
                    try { Directory.Delete(alphaRoot, true); } catch { }
                    try { Directory.Delete(betaRoot, true); } catch { }
                }

                // 13) PaneAction.InvokeWithPendingAsync. InvokeAsync takes no arguments, so an action
                // could only ever see SAVED settings -- every "preview what I just chose" button in every
                // module worked around that. These cases drive the REAL click handler, because the
                // precedence and the guard widening both live in BuildActionRow rather than in the
                // contract, and calling the delegate directly would exercise neither.
                IReadOnlyDictionary<string, string> sawPending = null;
                int plainInvokes = 0;
                var pendingChoice = new SettingField
                {
                    Id = "flavour",
                    Label = "Flavour",
                    Kind = SettingKind.Enum,
                    Options = new[] { "saved", "just-picked" },
                };
                var pendingSecret = new SettingField { Id = "key", Label = "Key", Kind = SettingKind.Secret };
                var pendingPane = new OptionsPane
                {
                    Title = "Pending",
                    Schema = new[] { pendingChoice, pendingSecret },
                    Load = delegate
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal) { { "flavour", "saved" } };
                    },
                    Actions = new[]
                    {
                        // Only the new delegate. Before the guards were widened this rendered NO BUTTON.
                        new PaneAction
                        {
                            Label = "Preview",
                            InvokeWithPendingAsync = delegate(IReadOnlyDictionary<string, string> onScreen)
                            {
                                sawPending = onScreen;
                                string flavour;
                                if (onScreen == null || !onScreen.TryGetValue("flavour", out flavour)) flavour = "(absent)";
                                return System.Threading.Tasks.Task.FromResult("✓ " + flavour);
                            },
                        },
                        // Both set: the pending-aware one wins, and the other must not run at all.
                        new PaneAction
                        {
                            Label = "Both",
                            InvokeAsync = delegate
                            {
                                plainInvokes++;
                                return System.Threading.Tasks.Task.FromResult("✗ the old delegate ran");
                            },
                            InvokeWithPendingAsync = delegate(IReadOnlyDictionary<string, string> onScreen)
                            {
                                return System.Threading.Tasks.Task.FromResult("✓ pending won");
                            },
                        },
                        // WITNESS: unchanged behaviour for every module that never sets the new member.
                        new PaneAction
                        {
                            Label = "Plain",
                            InvokeAsync = delegate
                            {
                                plainInvokes++;
                                return System.Threading.Tasks.Task.FromResult("✓ plain ran");
                            },
                        },
                    },
                };
                var pendingView = new DesktopAICompanion.Wpf.PaneView(pendingPane);
                var pendingRoot = pendingView.Build() as System.Windows.DependencyObject;
                var pendingButtons = new List<System.Windows.Controls.Button>();
                CollectAll(pendingRoot, pendingButtons);
                ok &= Check(sb, "an action carrying only InvokeWithPendingAsync still renders a button",
                    pendingButtons.Count == 3);
                var pendingCombos = new List<System.Windows.Controls.ComboBox>();
                CollectAll(pendingRoot, pendingCombos);
                if (pendingButtons.Count == 3 && pendingCombos.Count == 1)
                {
                    // An edit the user has NOT applied. This is the whole point: Load said "saved".
                    pendingCombos[0].SelectedItem = "just-picked";
                    foreach (System.Windows.Controls.Button b in pendingButtons)
                        b.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                    ok &= Check(sb, "InvokeWithPendingAsync sees the value just chosen, not the saved one",
                        StatusOf(pendingButtons[0]) != null && StatusOf(pendingButtons[0]).Text == "✓ just-picked");
                    ok &= Check(sb, "...which is the value Load did NOT return",
                        pendingPane.Load()["flavour"] == "saved");
                    // The documented shape of the dictionary, same as Save and LoadPending receive.
                    ok &= Check(sb, "a blank Secret is ABSENT from the pending values, never an empty string",
                        sawPending != null && !sawPending.ContainsKey("key"));
                    ok &= Check(sb, "when both delegates are set the pending-aware one wins",
                        StatusOf(pendingButtons[1]) != null && StatusOf(pendingButtons[1]).Text == "✓ pending won");
                    ok &= Check(sb, "WITNESS an action with no InvokeWithPendingAsync still runs InvokeAsync",
                        StatusOf(pendingButtons[2]) != null && StatusOf(pendingButtons[2]).Text == "✓ plain ran");
                    ok &= Check(sb, "...and the superseded InvokeAsync on the both-set action never ran",
                        plainInvokes == 1);
                }
                else ok &= Check(sb, "pending probe rendered three buttons and one dropdown", false);

                // A ListCard action has its own guard, in a different method from the pane-level one above.
                var listPendingPane = new OptionsPane
                {
                    Title = "ListPending",
                    Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal); },
                    Lists = new[]
                    {
                        new ListCard
                        {
                            Title = "Packs",
                            LoadItems = delegate { return new List<ListItem>(); },
                            Actions = new[]
                            {
                                new PaneAction
                                {
                                    Label = "List preview",
                                    InvokeWithPendingAsync = delegate(IReadOnlyDictionary<string, string> onScreen)
                                    {
                                        return System.Threading.Tasks.Task.FromResult("✓ list pending ran");
                                    },
                                },
                            },
                        },
                    },
                };
                var listPendingRoot = new DesktopAICompanion.Wpf.PaneView(listPendingPane).Build() as System.Windows.DependencyObject;
                var listPendingButtons = new List<System.Windows.Controls.Button>();
                CollectAll(listPendingRoot, listPendingButtons);
                if (listPendingButtons.Count == 1)
                {
                    listPendingButtons[0].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    ok &= Check(sb, "a ListCard action carrying only InvokeWithPendingAsync renders and runs",
                        StatusOf(listPendingButtons[0]) != null && StatusOf(listPendingButtons[0]).Text == "✓ list pending ran");
                }
                else ok &= Check(sb, "list-card pending probe rendered its button", false);

                // ---- AN "AVAILABLE TO DOWNLOAD" CARD SAYS WHAT THE PET CONTAINS ----
                // An installed card has always carried "N animations  ·  M sounds"; a download card carried
                // only a size, so the number a user actually chooses on was missing from the cards they were
                // choosing between. The counts cannot be computed here, because GetStats reads the installed
                // animations.xml and that is the file not yet downloaded, so they ride in the catalog.
                //
                // Reflected rather than driven through the pane, because building the whole gallery needs a
                // live host and a catalog fetch; the card builder is the unit under test.
                try
                {
                    var cardPane = (System.Windows.Controls.ContentControl)Activator.CreateInstance(
                        typeof(DesktopAICompanion.Wpf.OptionsShell).Assembly
                            .GetType("DesktopAICompanion.Wpf.CompanionsPaneControl"),
                        true);
                    System.Reflection.MethodInfo build = cardPane.GetType().GetMethod(
                        "BuildDownloadCard",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Type ccType = typeof(DesktopAICompanion.Wpf.OptionsShell).Assembly
                        .GetType("DesktopAICompanion.CatalogCompanion");
                    ok &= Check(sb, "the download-card builder and catalog type are both reachable",
                        build != null && ccType != null);
                    if (build != null && ccType != null)
                    {
                        object cc = Activator.CreateInstance(ccType);
                        ccType.GetField("Id").SetValue(cc, "blue_sheep");
                        ccType.GetField("Name").SetValue(cc, "Pearl");
                        ccType.GetField("Author").SetValue(cc, "Adriano");
                        ccType.GetField("Bytes").SetValue(cc, 1150320);
                        ccType.GetField("Animations").SetValue(cc, 268);
                        ccType.GetField("Sounds").SetValue(cc, 35);
                        var card = (System.Windows.FrameworkElement)build.Invoke(cardPane, new object[] { cc });
                        string cardText = string.Join(" | ", TextOf(card));
                        ok &= Check(sb, "a download card states the animation count (" + cardText + ")",
                            cardText.IndexOf("268 animations", StringComparison.Ordinal) >= 0);
                        ok &= Check(sb, "...and the sound count beside it",
                            cardText.IndexOf("35 sounds", StringComparison.Ordinal) >= 0);
                        ok &= Check(sb, "...and still states the download size",
                            cardText.IndexOf("download", StringComparison.Ordinal) >= 0);
                        // No per-pet sound TOGGLE: that preference applies to an installed pet and there is
                        // nothing yet to apply it to. Asserted because the installed card's line does have
                        // one, and copying that line wholesale is the obvious way to build this.
                        ok &= Check(sb, "...without offering a sound on/off toggle for a pet you do not have",
                            cardText.IndexOf("sound on", StringComparison.Ordinal) < 0 &&
                            cardText.IndexOf("sound off", StringComparison.Ordinal) < 0);

                        // An OLDER catalog carries neither count. The card must then read exactly as it did
                        // before this existed, rather than announcing "0 animations".
                        object older = Activator.CreateInstance(ccType);
                        ccType.GetField("Id").SetValue(older, "bbunny");
                        ccType.GetField("Name").SetValue(older, "Bbunny");
                        ccType.GetField("Bytes").SetValue(older, 33320);
                        string oldText = string.Join(" | ", TextOf((System.Windows.FrameworkElement)build.Invoke(cardPane, new object[] { older })));
                        ok &= Check(sb, "a pre-counts catalog entry shows no count line at all (" + oldText + ")",
                            oldText.IndexOf("animation", StringComparison.Ordinal) < 0 &&
                            oldText.IndexOf("download", StringComparison.Ordinal) >= 0);

                        // Render it, because the assertions above prove the strings and not the card.
                        try
                        {
                            card.Measure(new System.Windows.Size(240, 400));
                            card.Arrange(new System.Windows.Rect(0, 0, 240, card.DesiredSize.Height));
                            card.UpdateLayout();
                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                240, (int)Math.Max(1, card.DesiredSize.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            rtb.Render(card);
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using (var fs = File.Create(Path.Combine(Path.GetTempPath(), "dp-download-card.png")))
                                enc.Save(fs);
                        }
                        catch (Exception ex) { sb.AppendLine("note: card render skipped (" + ex.GetType().Name + ")"); }
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    sb.AppendLine("FAIL: download-card probe threw " + ex.GetType().Name + ": " + ex.Message);
                }

                // ---- THE SETTINGS WINDOW MUST NOT WAIT ON A SLEEPING NAS ----
                // DescribeNotificationSound runs inside the Preferences pane's Load(), which PaneView.Build()
                // calls on the WPF UI thread. File.Exists against an unreachable UNC path blocks on the SMB
                // connect timeout, so the unbounded version froze the whole window for tens of seconds for a
                // user whose chime lives on a NAS that is asleep.
                //
                // The assertion that matters is the DEADLINE, not the verdict: a bound that still answers
                // true and false correctly but takes 20s to say "did not answer" has fixed nothing. Both are
                // checked, because a probe hard-wired to return null would satisfy the timing one alone.
                string probeFile = Path.Combine(Path.GetTempPath(), "dp-probe-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    File.WriteAllText(probeFile, "x");
                    ok &= Check(sb, "a bounded file probe still finds a file that is there",
                        DesktopAICompanion.Wpf.OptionsShell.FileExistsBounded(probeFile, 2000) == true);
                    ok &= Check(sb, "a bounded file probe still reports a file that is not there",
                        DesktopAICompanion.Wpf.OptionsShell.FileExistsBounded(probeFile + ".gone", 2000) == false);

                    // A probe that is GUARANTEED to block, rather than a real unreachable UNC path. The UNC
                    // version of this test was written first and was worthless: the first call took the full
                    // 400ms and the second returned in 2ms, because Windows caches an unreachable host, so
                    // the unbounded implementation passed it too. Sleeping five seconds cannot be cached.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool? dead = DesktopAICompanion.Wpf.OptionsShell.ProbeBounded(
                        delegate { System.Threading.Thread.Sleep(5000); return true; }, 400);
                    sw.Stop();
                    ok &= Check(sb, "a filesystem call that hangs gives the settings window back anyway (took "
                        + sw.ElapsedMilliseconds + "ms)", sw.ElapsedMilliseconds < 2000);
                    // And it must never turn a timeout into the false accusation: a path we could not reach
                    // is not a path we know is missing.
                    ok &= Check(sb, "a probe that did not answer reports neither present nor missing", dead == null);
                    string desc = DesktopAICompanion.Wpf.OptionsShell.DescribeNotificationSound(probeFile);
                    ok &= Check(sb, "a present chime is described without a missing-file warning",
                        desc != null && !desc.StartsWith("✗"));
                    ok &= Check(sb, "an absent chime is still described as missing",
                        (DesktopAICompanion.Wpf.OptionsShell.DescribeNotificationSound(probeFile + ".gone") ?? "").StartsWith("✗"));
                }
                finally { try { File.Delete(probeFile); } catch { } }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-wpf-options-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        /// <summary>Every TextBlock string in a built card, so an assertion can read what the card actually
        /// says rather than what the builder was asked for.</summary>
        private static List<string> TextOf(System.Windows.DependencyObject root)
        {
            var found = new List<string>();
            if (root == null) return found;
            var tb = root as System.Windows.Controls.TextBlock;
            if (tb != null && !string.IsNullOrWhiteSpace(tb.Text)) found.Add(tb.Text);
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                found.AddRange(TextOf(System.Windows.Media.VisualTreeHelper.GetChild(root, i)));
            if (n == 0)
            {
                var dec = root as System.Windows.Controls.Decorator;
                if (dec != null) found.AddRange(TextOf(dec.Child));
                var panel = root as System.Windows.Controls.Panel;
                if (panel != null) foreach (System.Windows.UIElement c in panel.Children) found.AddRange(TextOf(c));
                var cc2 = root as System.Windows.Controls.ContentControl;
                if (cc2 != null) found.AddRange(TextOf(cc2.Content as System.Windows.DependencyObject));
            }
            return found;
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }

        // Build() returns an un-rendered tree, so the visual tree does not exist yet; walk the LOGICAL tree,
        // which is populated at construction time.
        private static void CollectExpanders(System.Windows.DependencyObject node, List<System.Windows.Controls.Expander> found)
        {
            if (node == null) return;
            var exp = node as System.Windows.Controls.Expander;
            if (exp != null) { found.Add(exp); return; }   // groups never nest
            foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(node))
                CollectExpanders(child as System.Windows.DependencyObject, found);
        }

        // Unlike the two collectors above, this keeps descending PAST a hit: a radio group sits inside a
        // row that also carries the field label, and a pane has several cards each holding several of
        // whatever is being looked for.
        private static void CollectAll<T>(System.Windows.DependencyObject node, List<T> found) where T : class
        {
            if (node == null) return;
            var hit = node as T;
            if (hit != null) found.Add(hit);
            foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(node))
                CollectAll<T>(child as System.Windows.DependencyObject, found);
        }

        // A schema group renders as a Border wrapping a StackPanel whose first TextBlock is the group
        // heading, which is the only thing naming a card once it has been laid out.
        private static string CardTitle(System.Windows.Controls.Border card)
        {
            var panel = card == null ? null : card.Child as System.Windows.Controls.Panel;
            if (panel == null) return "";
            foreach (System.Windows.UIElement child in panel.Children)
            {
                var tb = child as System.Windows.Controls.TextBlock;
                if (tb != null) return tb.Text ?? "";
            }
            return "";
        }

        // An action row is a DockPanel holding the button and its status line. That status line is the
        // only channel a RevealsPath refusal is ever reported through, so it is what has to be read.
        private static System.Windows.Controls.TextBlock StatusOf(System.Windows.Controls.Button button)
        {
            var panel = button == null ? null : button.Parent as System.Windows.Controls.Panel;
            if (panel == null) return null;
            foreach (System.Windows.UIElement child in panel.Children)
            {
                var tb = child as System.Windows.Controls.TextBlock;
                if (tb != null) return tb;
            }
            return null;
        }

        private static void CollectCheckBoxes(System.Windows.DependencyObject node, List<System.Windows.Controls.CheckBox> found)
        {
            if (node == null) return;
            var cb = node as System.Windows.Controls.CheckBox;
            if (cb != null) { found.Add(cb); return; }
            foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(node))
                CollectCheckBoxes(child as System.Windows.DependencyObject, found);
        }

        private static string HeaderText(System.Windows.Controls.Expander e)
        {
            var panel = e.Header as System.Windows.Controls.Panel;
            if (panel == null) return "";
            foreach (System.Windows.UIElement child in panel.Children)
            {
                var tb = child as System.Windows.Controls.TextBlock;
                if (tb != null) return tb.Text ?? "";
            }
            return "";
        }

        private static System.Windows.Controls.CheckBox HeaderCheck(System.Windows.Controls.Expander e)
        {
            var panel = e.Header as System.Windows.Controls.Panel;
            if (panel == null) return null;
            foreach (System.Windows.UIElement child in panel.Children)
            {
                var cb = child as System.Windows.Controls.CheckBox;
                if (cb != null) return cb;
            }
            return null;
        }
    }
}
