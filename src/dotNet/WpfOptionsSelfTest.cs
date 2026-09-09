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
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-wpf-options-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
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
