using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion
{
    internal static partial class WpfOptionsSelfTest
    {
        /// <summary>
        /// N-audit-1106-033: after a successful Apply on a pane that is not rebuilt (no Info or Header field), the view
        /// measured edits against its PRE-Apply baseline, so a field changed back to what it was before the Apply left
        /// Apply grey, and that value could never be saved. Same for a deferred list box ticked back. Driven through the
        /// window's own Apply on a laid-out window, and the module's Save and SetChecked record what reached it.
        /// </summary>
        private static bool ApplyRebasesTheBaseline(StringBuilder sb)
        {
            bool ok = true;
            IReadOnlyDictionary<string, string> lastSave = null;
            var sent = new List<string>();
            var pane = new OptionsPane
            {
                Title = "Rebase",
                Schema = new[] { new SettingField { Id = "note", Label = "Note", Kind = SettingKind.Text, Group = "Rebase" } },
                Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal) { { "note", "first" } }; },
                Save = delegate(IReadOnlyDictionary<string, string> values) { lastSave = values; return true; },
                Lists = new[]
                {
                    new ListCard
                    {
                        Title = "Deferred",
                        DeferChanges = true,
                        SetChecked = delegate(string id, bool on) { sent.Add(id + "=" + (on ? "on" : "off")); },
                        LoadItems = delegate { return new[] { new ListItem { Id = "a", Label = "Item a", Checked = false } }; },
                    },
                },
            };
            var probe = new LaidOutWindow(pane, ElsewherePane());
            System.Windows.Controls.TextBox note = probe.TextRow(0);
            System.Windows.Controls.CheckBox item = BoxTagged(probe, "a");
            if (note == null || item == null) return Check(sb, "options-ui rebase: the pane rendered its text row and its deferred box", false);

            note.Text = "second";
            probe.Apply();
            string saved;
            ok &= Check(sb, "options-ui rebase: WITNESS Apply saved the edit and greyed Apply",
                lastSave != null && lastSave.TryGetValue("note", out saved) && saved == "second" && !probe.Window.IsDirty);
            note.Text = "first";
            ok &= Check(sb, "options-ui rebase: a field changed back after Apply to its pre-Apply value lights Apply", probe.Window.IsDirty);
            probe.Apply();
            ok &= Check(sb, "options-ui rebase: ...and Apply saves that value",
                lastSave != null && lastSave.TryGetValue("note", out saved) && saved == "first");

            item.IsChecked = true;
            probe.Apply();
            ok &= Check(sb, "options-ui rebase: WITNESS Apply sent the staged tick (" + string.Join(", ", sent) + ")",
                sent.Count == 1 && sent[0] == "a=on" && !probe.Window.IsDirty);
            item.IsChecked = false;
            ok &= Check(sb, "options-ui rebase: a deferred box ticked back after Apply lights Apply", probe.Window.IsDirty);
            probe.Apply();
            ok &= Check(sb, "options-ui rebase: ...and Apply sends the untick (" + string.Join(", ", sent) + ")",
                sent.Count == 2 && sent[1] == "a=off");
            return ok;
        }

        /// <summary>
        /// N-audit-1106-260: Preferences shows the volume rounded to tenths and Apply posts every field, so a stored
        /// volume off the 0.1 grid was rewritten by any unrelated Apply. Driven through the pane's own Load and Save on a
        /// scratch settings file; only the volume key is posted, so the Save touches nothing outside the scratch store
        /// (no Run key: that write happens only when runAtStartup is posted).
        /// </summary>
        private static bool PreferencesKeepsAnOffGridVolume(StringBuilder sb)
        {
            bool ok = true;
            LocalData previous = Program.MyData;
            string scratch = Path.Combine(Path.GetTempPath(), "dp-volume-grid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(scratch);
                Program.MyData = new LocalData(new AppSettingsStore(Path.Combine(scratch, "settings.json"), null));
                Program.MyData.SetVolume(0.37);
                OptionsPane prefs = DesktopAICompanion.Wpf.OptionsShell.BuildPreferencesPane();
                IReadOnlyDictionary<string, string> shown = prefs.Load();
                string tenth;
                bool showsRounded = shown != null && shown.TryGetValue("volume", out tenth) && tenth == "4";
                ok &= Check(sb, "options-ui volume: WITNESS the pane shows a 0.37 volume as the tenth 4", showsRounded);
                if (!showsRounded) return ok;

                bool savedUntouched = prefs.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "volume", "4" } });
                float kept = Program.MyData.GetVolume();
                ok &= Check(sb, "options-ui volume: an Apply that leaves the volume field alone keeps a stored volume off the 0.1 grid (now "
                    + kept.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")",
                    savedUntouched && Math.Abs(kept - 0.37f) < 0.0001f);

                bool savedChanged = prefs.Save(new Dictionary<string, string>(StringComparer.Ordinal) { { "volume", "6" } });
                ok &= Check(sb, "options-ui volume: WITNESS a changed volume field is written",
                    savedChanged && Math.Abs(Program.MyData.GetVolume() - 0.6f) < 0.0001f);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL: options-ui volume threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Program.MyData = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }
            return ok;
        }

        // ---- burndown/w9-host-2 ----

        /// <summary>
        /// N-audit-1106-253: the window contains a pane whose build throws. OptionsWindow.ShowPane catches what
        /// BuildContent throws and shows "This pane failed to load: ..." in the content area, so one broken module pane
        /// cannot take the settings window down with it. ThrowingOptionsSchema was only ever built through
        /// SchemaShellPane.BuildContent directly; here it is the window's FIRST pane, built by the window's own
        /// constructor, which is where a throw would have escaped. WITNESS: the nav still reaches the pane beside it.
        /// </summary>
        private static bool WindowContainsAPaneThatFailsToBuild(StringBuilder sb)
        {
            bool ok = true;
            var failing = new OptionsPane
            {
                Title = "Fails to build",
                Schema = new ThrowingOptionsSchema(),
                Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal); },
            };
            DesktopAICompanion.Wpf.OptionsWindow window = null;
            string threw = null;
            try
            {
                window = new DesktopAICompanion.Wpf.OptionsWindow(new List<DesktopAICompanion.Wpf.ShellPane>
                {
                    new DesktopAICompanion.Wpf.SchemaShellPane(failing),
                    new DesktopAICompanion.Wpf.SchemaShellPane(ElsewherePane()),
                });
            }
            catch (Exception ex) { threw = ex.GetType().Name + ": " + ex.Message; }
            try
            {
                var shown = window != null ? window.CurrentContent as System.Windows.Controls.TextBlock : null;
                string text = shown != null ? (shown.Text ?? "") : (threw != null ? "the window threw " + threw : "no TextBlock in the content area");
                ok &= Check(sb, "pane-contain: a pane whose build throws opens as a line saying it failed to load, and the window opens (" + text + ")",
                    window != null && window.SelectedNavIndex == 0 &&
                    text.StartsWith("This pane failed to load: ", StringComparison.Ordinal) &&
                    text.EndsWith("Synthetic schema failure.", StringComparison.Ordinal));
                ok &= Check(sb, "pane-contain: WITNESS the nav still reaches the pane beside the broken one",
                    window != null && window.ShowPane("Elsewhere") && window.CurrentPaneTitle == "Elsewhere" &&
                    !(window.CurrentContent is System.Windows.Controls.TextBlock));
            }
            finally { if (window != null) window.Close(); }
            return ok;
        }

        /// <summary>
        /// N-audit-1106-250: the CLOSED half of the reload guard (F375, RA-329). A ReloadPaneAfter action's continuation
        /// runs after an await, and the window may have closed in between: OnClosed released the view, and the guard's
        /// _closed term is what declines the rebuild then. Nothing else was built after the click, so the build
        /// generation still matches and only that term stands between the continuation and a rebuild of the pane into
        /// the closed window. The pane-switch and same-pane cases are section 14c's; this one closes the window.
        /// </summary>
        private static bool ReloadAfterTheWindowClosedIsDeclined(StringBuilder sb)
        {
            bool ok = true;
            TaskCompletionSource<string> slow = null;
            var pane = new OptionsPane
            {
                Title = "Slow on close",
                Schema = new[] { new SettingField { Id = "note", Label = "Note", Kind = SettingKind.Text } },
                Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal) { { "note", "" } }; },
                Save = delegate { return true; },
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Slow reset",
                        ReloadPaneAfter = true,
                        InvokeAsync = delegate { slow = new TaskCompletionSource<string>(); return slow.Task; },
                    },
                },
            };
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var window = new DesktopAICompanion.Wpf.OptionsWindow(new List<DesktopAICompanion.Wpf.ShellPane>
            {
                new DesktopAICompanion.Wpf.SchemaShellPane(pane),
            });
            try
            {
                var buttons = new List<System.Windows.Controls.Button>();
                CollectAll(window.CurrentContent as System.Windows.DependencyObject, buttons);
                System.Windows.Controls.Button reset = buttons.Find(delegate(System.Windows.Controls.Button b) { return (b.Content as string) == "Slow reset"; });
                if (reset == null) return Check(sb, "reload-closed: the slow pane rendered its action button", false);
                reset.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                object before = window.CurrentContent;
                window.Close();
                bool running = slow != null && !slow.Task.IsCompleted;
                if (slow != null) slow.SetResult("reset done");
                PumpDispatcher();
                System.Windows.Controls.TextBlock status = StatusOf(reset);
                ok &= Check(sb, "reload-closed: WITNESS the action was still running when the window closed, and finished after it (" +
                    (status != null ? status.Text : "no status line") + ")",
                    running && status != null && status.Text == "reset done");
                ok &= Check(sb, "reload-closed: a ReloadPaneAfter action finishing after the window closed does not rebuild its pane into it",
                    ReferenceEquals(window.CurrentContent, before));
                ok &= Check(sb, "reload-closed: ...and leaves nothing stashed for the next open of that pane",
                    !DesktopAICompanion.Wpf.PaneView.ActionRebuildIsStashed && !DesktopAICompanion.Wpf.PaneView.ViewStateIsStashed);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                window.Close();
            }
            return ok;
        }

        /// <summary>
        /// N-audit-1106-251: the window's refresh after Apply rebuilds a pane holding an Info OR a Header row and leaves
        /// every other pane on the view it had (SchemaShellPane.RefreshAfterApply). Every pane Apply was pressed on held
        /// an Info row, so the Header clause could go, or the rule answer yes for every pane, with every check passing.
        /// Driven through the window's own Apply; the module's Save counts witness that each Apply ran.
        /// </summary>
        private static bool RefreshAfterApplyFollowsInfoOrHeader(StringBuilder sb)
        {
            bool ok = true;
            int headedSaves = 0, plainSaves = 0;
            var headed = new OptionsPane
            {
                Title = "Headed",
                Schema = new[]
                {
                    new SettingField { Id = "about", Label = "What this pane does", Kind = SettingKind.Header },
                    new SettingField { Id = "note", Label = "Note", Kind = SettingKind.Text },
                },
                Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal) { { "about", "Read from the settings." }, { "note", "kept" } }; },
                Save = delegate { headedSaves++; return true; },
            };
            var plain = new OptionsPane
            {
                Title = "Plain",
                Schema = new[] { new SettingField { Id = "note", Label = "Note", Kind = SettingKind.Text } },
                Load = delegate { return new Dictionary<string, string>(StringComparer.Ordinal) { { "note", "kept" } }; },
                Save = delegate { plainSaves++; return true; },
            };
            var headedProbe = new LaidOutWindow(headed);
            var plainProbe = new LaidOutWindow(plain);
            try
            {
                object headedBefore = headedProbe.Window.CurrentContent;
                headedProbe.Apply();
                ok &= Check(sb, "apply-refresh: Apply rebuilds a pane that holds a Header row and no Info row (saves " + headedSaves + ")",
                    headedSaves == 1 && !ReferenceEquals(headedBefore, headedProbe.Window.CurrentContent));
                object plainBefore = plainProbe.Window.CurrentContent;
                plainProbe.Apply();
                ok &= Check(sb, "apply-refresh: Apply leaves a pane with neither an Info nor a Header row on the view it had (saves " + plainSaves + ")",
                    plainSaves == 1 && ReferenceEquals(plainBefore, plainProbe.Window.CurrentContent));
            }
            finally
            {
                headedProbe.Window.Close();
                plainProbe.Window.Close();
            }
            return ok;
        }

        /// <summary>
        /// N-audit-1106-252: the Modules pane once it has gone away during a download. Every other ModulesPaneProbe case
        /// sets CountsAsLoaded, so the pane's IsUp was always true and its "the pane went away" returns ran under no check,
        /// and nothing raised its Unloaded. Here the probe clears CountsAsLoaded once the download has begun (a headless
        /// pane is never Loaded, so IsUp is then false, as it is shipped for a pane that was left or a window that was
        /// closed) and lets the download land: an Install moves nothing into modules/&lt;id&gt;, an Update stages and marks
        /// nothing, and neither writes the status line of the pane it no longer has. A third pane gets Unloaded raised
        /// mid-download, which must cancel the token the download holds.
        /// </summary>
        private static bool ModulesPaneGoneDuringTheDownload(StringBuilder sb)
        {
            bool ok = true;
            string root = DesktopAICompanion.Plugins.SelfTestScratch.Create("pane-gone");
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            try
            {
                // ---- Install: the pane goes away before the download lands ----
                var install = new ModulesPaneProbe(root, "install");
                install.Available("gamma", "Gamma", "1.2.0");
                install.Held["gamma"] = new TaskCompletionSource<byte[]>();
                install.Build();
                ModulesPaneProbe.Press(ModulesPaneProbe.Find(install.Buttons(), "Install"));
                bool installBegan = PumpUntil(delegate { return install.Downloads.Count == 1; }, 20000);
                string installStatus = install.Pane.StatusText;
                install.Seams.CountsAsLoaded = false;
                install.Held["gamma"].SetResult(ModulesPaneProbe.Payload(install.Catalog.Modules[0]));
                bool installDone = PumpUntil(delegate { return !install.Pane.IsBusy; }, 20000);
                ok &= Check(sb, "pane-gone: WITNESS the Install download began and the pane said so (" + installStatus + ")",
                    installBegan && installStatus.StartsWith("Downloading Gamma", StringComparison.Ordinal));
                ok &= Check(sb, "pane-gone: an Install whose pane went away during the download moves nothing into modules/<id> (" +
                    install.Describe() + "; status " + install.Pane.StatusText + ")",
                    installDone && !Directory.Exists(Path.Combine(install.ModulesRoot, "gamma")) &&
                    !Directory.Exists(Path.Combine(install.StagingRoot, "gamma.staged")) && install.RestartQuestions() == 0 &&
                    install.Pane.StatusText == installStatus);

                // ---- Update: the same, through the staging an update takes ----
                var update = new ModulesPaneProbe(root, "update");
                update.Offer("zeta", "Zeta", "1.0.0", "1.1.0");
                update.Held["zeta"] = new TaskCompletionSource<byte[]>();
                update.Build();
                ModulesPaneProbe.Press(ModulesPaneProbe.Find(update.Buttons(), "Update to v1.1.0"));
                bool updateBegan = PumpUntil(delegate { return update.Downloads.Count == 1; }, 20000);
                string updateStatus = update.Pane.StatusText;
                update.Seams.CountsAsLoaded = false;
                update.Held["zeta"].SetResult(ModulesPaneProbe.Payload(update.Catalog.Modules[0]));
                bool updateDone = PumpUntil(delegate { return !update.Pane.IsBusy; }, 20000);
                ok &= Check(sb, "pane-gone: WITNESS the Update download began (" + update.Describe() + ")", updateBegan);
                ok &= Check(sb, "pane-gone: an Update whose pane went away during the download stages and marks nothing (" +
                    update.Describe() + "; status " + update.Pane.StatusText + ")",
                    updateDone && !Directory.Exists(Path.Combine(update.StagingRoot, "zeta.staged")) &&
                    update.MarkedIds().Length == 0 && update.RestartQuestions() == 0 && update.Pane.StatusText == updateStatus);

                // ---- Unloaded: leaving the pane cancels the download it started ----
                var leaving = new ModulesPaneProbe(root, "leaving");
                leaving.Available("eta", "Eta", "1.0.0");
                leaving.Held["eta"] = new TaskCompletionSource<byte[]>();
                leaving.Build();
                ModulesPaneProbe.Press(ModulesPaneProbe.Find(leaving.Buttons(), "Install"));
                bool leavingBegan = PumpUntil(delegate { return leaving.Tokens.Count == 1; }, 20000);
                ok &= Check(sb, "pane-gone: WITNESS the download's token is live while the pane is up",
                    leavingBegan && !leaving.Tokens[0].IsCancellationRequested);
                leaving.Seams.CountsAsLoaded = false;
                leaving.Pane.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent));
                ok &= Check(sb, "pane-gone: leaving the pane (Unloaded) cancels the token its download holds",
                    leavingBegan && leaving.Tokens[0].IsCancellationRequested);
                leaving.Held["eta"].SetCanceled();
                bool leavingDone = PumpUntil(delegate { return !leaving.Pane.IsBusy; }, 20000);
                ok &= Check(sb, "pane-gone: ...and the cancelled Install leaves nothing behind (" + leaving.Describe() + ")",
                    leavingDone && !Directory.Exists(Path.Combine(leaving.ModulesRoot, "eta")) &&
                    !Directory.Exists(Path.Combine(leaving.StagingRoot, "eta.staged")));
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL: the pane-gone probe threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                string releaseDetail;
                if (!DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out releaseDetail))
                    sb.AppendLine("NOTE: pane-gone scratch left for the next sweep (" + releaseDetail + ")");
            }
            return ok;
        }

        /// <summary>
        /// N-audit-1106-247: the exit path's CloseOpenWindow closes the About window too. KillSheeps calls it so that no
        /// modal outlives the exit request, and it closed only the settings window, so an Exit from the tray menu while
        /// About was up left the process behind it. The tray's own OpenAbout is driven with the modal loop stood in for
        /// (OptionsShell.ShowAboutForSelfTest): while the window is "up", the exit's close is asked for, and the window
        /// must close and be forgotten. Nothing reaches the desktop: the window is never shown.
        /// </summary>
        private static bool ExitClosesTheAboutWindow(StringBuilder sb)
        {
            bool ok = true;
            bool tracked = false, closed = false, handed = false;
            try
            {
                DesktopAICompanion.Wpf.OptionsShell.ShowAboutForSelfTest = delegate (System.Windows.Window window)
                {
                    handed = true;
                    tracked = DesktopAICompanion.Wpf.OptionsShell.IsAboutOpen;
                    window.Closed += delegate { closed = true; };
                    DesktopAICompanion.Wpf.OptionsShell.CloseOpenWindow();
                    return null;
                };
                DesktopAICompanion.Wpf.OptionsShell.OpenAbout("Synthetic author", "Synthetic pet", "1.0", "Synthetic info");
                ok &= Check(sb, "about: WITNESS the tray's About window is built and tracked while it is up (" +
                    (handed ? "handed over" : "never shown") + ", tracked " + tracked + ")", handed && tracked);
                ok &= Check(sb, "about: the exit's CloseOpenWindow closes the About window, so no modal outlives the exit (closed " +
                    closed + ")", closed);
                ok &= Check(sb, "about: a closed About window is forgotten (open " + DesktopAICompanion.Wpf.OptionsShell.IsAboutOpen + ")",
                    !DesktopAICompanion.Wpf.OptionsShell.IsAboutOpen);
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL: about: the exit probe threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                DesktopAICompanion.Wpf.OptionsShell.ShowAboutForSelfTest = null;
            }
            return ok;
        }
    }
}
