using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// Host-built Modules manager for the WPF settings window (S6): a row per installed module (name/version
    /// from its live <see cref="ModuleInfo"/> when loaded, or "pending restart" when just installed) with an
    /// Uninstall action, plus a "Check for modules online" footer that fetches the same HTTPS-trusted,
    /// SHA-256-verified catalog Pets/Fortunes already use (<see cref="RemoteCatalogClient"/>), diffs it
    /// against what's on disk, and offers the rest as install cards. Exists even with zero modules installed
    /// — it is how a lean host ever gets any. Installing or removing a module restarts the app (modules only
    /// load at startup today); the restart plumbing (<see cref="Program.RequestRestart"/>) reopens Settings
    /// back on this pane afterward.
    ///
    /// A fetched catalog also drives UPDATES: an installed row whose live version is older than the catalog's
    /// grows an "Update to vX.Y.Z" button. Without it a module bugfix could never reach anyone who already had
    /// the module — the install list is diffed by id, so an installed module simply disappears from it, and the
    /// only route left was Uninstall (which deletes the module's settings) followed by a fresh install.
    ///
    /// No row offers a build whose MinHostVersion this host cannot satisfy (it says what the build needs
    /// instead), and a row whose update is already staged says so rather than offering it again. With two or
    /// more offers left, one "Update all (N)" button beside "Check for modules online" takes every one of them
    /// through the row's own path (consent, verified download, staging, the pending-update marker) and asks to
    /// restart ONCE at the end (asked for by the owner 2026-10-02; the rows' buttons stay).
    /// </summary>
    internal sealed class ModulesPaneControl : ContentControl, IBusyPane
    {
        /// <summary>Installs and updates in flight. Read by the shell through <see cref="IBusyPane"/>, so a
        /// redirect by title is refused while one runs instead of cancelling it with the pane (RA-328). An
        /// Update all run counts from the press to its last prompt.</summary>
        private int _downloadsInFlight;
        public bool IsBusy { get { return _downloadsInFlight > 0 || _updatingAll; } }

        /// <summary>True from an Update all press until its run has finished. Every row's Update, Reinstall
        /// and Uninstall, every Install and the Check button render disabled meanwhile: an uninstall of a
        /// module mid-update, a second press, an install asking for its own restart halfway through the run,
        /// or a Check cancelling the run's download through the shared token would each break the one run.</summary>
        private bool _updatingAll;

        /// <summary>A Check press in flight. Update all will not start under one: the check re-renders every
        /// row when it lands, and a run must not have its rows rebuilt and re-enabled beneath it.</summary>
        private bool _checkInFlight;

        /// <summary>The pane's reach beyond its window; <see cref="ModulesPaneSeams.Live"/> unless a self-test
        /// built this pane over fakes.</summary>
        private readonly ModulesPaneSeams _seams;

        private readonly StackPanel _installedList = new StackPanel { Margin = new Thickness(4) };
        private readonly TextBlock _availableHeader = new TextBlock
        {
            Text = "Available to install",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 10, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        private readonly StackPanel _availableList = new StackPanel { Margin = new Thickness(4), Visibility = Visibility.Collapsed };
        private readonly Button _checkButton = new Button
        {
            Content = "Check for modules online",
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 0, 0, 4),
        };
        // "Update all (N)", shown by Reload only while two or more rows offer an update it would take. It sits
        // in the footer beside the Check button, the pane-wide actions' row (the Companions pane's footer is the
        // precedent), rather than on any one module's row; the status line under both says what each did.
        private readonly Button _updateAllButton = new Button
        {
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 4, 0, 6), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

        // THE PROBLEM PANEL (the owner's pick, mockup M2, 2026-10-06; feature/catalog-insight). Above the module list
        // and present only while something about the catalog is wrong: red when nothing could be read, amber when
        // entries were skipped and the rest works. It names the case, the entry and the rule, when, and whose fault
        // it is ("Published wrong, not your install"), with Try again and Copy details. Built by ShowProblem from a
        // CatalogProblem, whose words CatalogText owns, so --catalog-selftest tests them without a window.
        private readonly Border _problemPanel = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 6, 8, 4),
            Visibility = Visibility.Collapsed,
        };
        private CatalogProblem _shownProblem;

        // How the When row ends: the occasion of the fetch it describes. The on-open fetch is the default; the Check
        // button and Try again say which was pressed.
        private string _occasion = OpenedOccasion;
        private const string OpenedOccasion = "when this pane opened";

        // The most recent successful catalog fetch, so an install can re-diff locally without re-fetching.
        private RemoteCatalog _lastCatalog;
        private CancellationTokenSource _netCts;

        public ModulesPaneControl() : this(ModulesPaneSeams.Live()) { }

        /// <summary>The pane over <paramref name="seams"/>. Only --wpf-options-selftest passes anything but
        /// <see cref="ModulesPaneSeams.Live"/>; the shell builds the pane with the parameterless constructor.</summary>
        internal ModulesPaneControl(ModulesPaneSeams seams)
        {
            _seams = seams ?? ModulesPaneSeams.Live();
            var root = new DockPanel { LastChildFill = true };

            var header = new StackPanel { Margin = new Thickness(4) };
            header.Children.Add(new TextBlock { Text = "Modules", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            header.Children.Add(new TextBlock
            {
                Text = "Optional features, installed on demand. New modules and updates for what you already " +
                       "have are listed when this pane opens; the button below checks again now. Installing, " +
                       "updating or removing one restarts the app.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            footerButtons.Children.Add(_checkButton);
            footerButtons.Children.Add(_updateAllButton);
            footer.Children.Add(footerButtons);
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scrollContent = new StackPanel();
            scrollContent.Children.Add(_problemPanel);
            scrollContent.Children.Add(_installedList);
            scrollContent.Children.Add(_availableHeader);
            scrollContent.Children.Add(_availableList);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = scrollContent });
            Content = root;

            _checkButton.Click += CheckButton_Click;
            _updateAllButton.Click += async delegate { await UpdateAllAsync(); };
            Unloaded += delegate { try { if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); _netCts = null; } } catch { } };

            Reload();
            // DEFERRED TO Loaded, NOT CALLED FROM THE CONSTRUCTOR.
            //
            // RemoteCatalogClient.FetchSharedAsync returns the cached catalog from inside a lock with
            // no await executed, so on a WARM cache the task it hands back is already complete and
            // `await ... ConfigureAwait(true)` resumes SYNCHRONOUSLY on this very stack -- still
            // inside the constructor. The first line after the await guards on IsLoaded, which a
            // constructor guarantees is false, so the catalog was dropped and no update offer ever
            // rendered. Only the FIRST open in any 90-second window did a real round trip, whose
            // continuation was posted to the dispatcher and landed after Loaded: the method worked
            // exactly once and then went quiet, which is the hardest shape of bug to notice.
            //
            // Guarded, because Loaded fires again if the control is re-parented, and re-fetching on
            // every re-parent is what the shared 90-second cache exists to avoid.
            bool catalogRefreshRequested = false;
            Loaded += delegate
            {
                if (catalogRefreshRequested) return;
                catalogRefreshRequested = true;
                RefreshCatalogOnOpen();
            };
        }

        /// <summary>
        /// Fetch the catalog when the pane opens, so an available update is already showing.
        ///
        /// This control is rebuilt on every pane selection, so the constructor IS "on open" and _lastCatalog
        /// is always null here -- which is why, before this, no update could ever appear until the button was
        /// pressed. Fire-and-forget.
        ///
        /// A FAILURE IS SHOWN, in the problem panel above the list (feature/catalog-insight). This used to be
        /// failure-silent, on the reasoning that a pane which cannot reach the network should look as it did
        /// before. On 2026-10-06 that meant a pane showing nothing at all while every installed app refused the
        /// published catalog (BUG-014): no update, no install list, no word of why, and the owner's report was
        /// exactly that "the Modules interface shows nothing". The panel says which case it was, when, and whose
        /// fault, and goes again once a fetch succeeds.
        /// </summary>
        private async void RefreshCatalogOnOpen()
        {
            CancellationToken token = CancellationToken.None;
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                token = _netCts.Token;
                RemoteCatalog catalog = await RemoteCatalogClient.FetchSharedAsync(token).ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsLoaded) return;
                _lastCatalog = catalog;
                // SAYS IT LANDED. Whether this fetch reached the pane was invisible from outside:
                // with every modules already current there is no "Update to v..." button either way, so
                // the pane looked identical when the result was being silently discarded -- which it
                // was, on every open but the first in each 90-second cache window, for as long as
                // this ran from the constructor. One line per open, in a category that is off by
                // default for most users, is a cheap price for a fetch that can be seen to happen.
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                    "[module] modules pane: catalog in hand on open");
                ShowCatalog(catalog);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // A Check press landing meanwhile owns the status line; only this fetch's own failure is said.
                if (!token.IsCancellationRequested && IsLoaded && !_checkInFlight) ShowFetchFailure(ex);
            }
        }

        /// <summary>
        /// Render the pane against a catalog in hand: the installed rows with their update offers (Update all
        /// among them) and the install list. What the on-open fetch does once it lands, and the one door
        /// --wpf-options-selftest hands its fake catalog through, so the test renders exactly what a fetch
        /// would.
        /// </summary>
        internal void ShowCatalog(RemoteCatalog catalog)
        {
            _lastCatalog = catalog;
            Reload();
            // The install list too, as the Companions pane renders new pets on open (RA-317). This
            // rendered only the update buttons, so a lean host's first visit read "No modules installed
            // yet." with nothing to install until the button was found and pressed, while the same
            // catalog was already in hand.
            RenderAvailable(DiffNew());
            // ...and the module entries the read REFUSED, in the problem panel, by name and rule and whose fault.
            // A refused entry is in none of the lists above, so without the panel its module simply is not there:
            // no install card, no update, nothing to say an entry was published wrong (BUG-014). A clean read
            // takes the panel away, which is how Try again clears it. Only module entries: a refused pack or
            // companion is not this pane's business.
            ShowProblem(catalog == null ? null : CatalogText.ProblemForRefusals(
                catalog.RefusedOf(CatalogRejection.Module), catalog.ReadAt, _occasion, InstalledVersion));
            // "Checked today at 14:02." in front of the counts, as the mockup has it, on open as well as on Check:
            // the line answers "did it even check?" without a panel.
            int updates = CountAvailableUpdates();
            _status.Text = "Checked today at " + (catalog != null ? catalog.ReadAt : DateTime.Now).ToString(
                               "t", System.Globalization.CultureInfo.CurrentCulture) + ". " +
                           Describe(DiffNew().Count, "available to install") +
                           (updates > 0 ? "  " + Describe(updates, "with an update") : "");
        }

        /// <summary>The running version of an installed module, or null: what the panel says keeps running.</summary>
        private string InstalledVersion(string id)
        {
            ModuleInfo info = LoadedInfo(id);
            return info != null ? info.Version : null;
        }

        /// <summary>The catalog's refused entry for module <paramref name="id"/>, or null.</summary>
        private CatalogRejection RefusedEntry(string id)
        {
            if (_lastCatalog == null || string.IsNullOrEmpty(id)) return null;
            foreach (CatalogRejection refused in _lastCatalog.RefusedOf(CatalogRejection.Module))
                if (string.Equals(refused.Id, id, StringComparison.OrdinalIgnoreCase)) return refused;
            return null;
        }

        /// <summary>
        /// Say that a fetch failed, in the problem panel and the log: the case it was, why, when and whose fault.
        /// A catalog that was reached and REFUSED is not "couldn't reach" (BUG-014). The on-open fetch, the Check
        /// button and Try again all come here, and --wpf-options-selftest hands its failures in through this door.
        /// </summary>
        internal void ShowFetchFailure(Exception failure)
        {
            DateTime now = DateTime.Now;
            ShowProblem(CatalogText.ProblemForFailure(failure, now, _occasion));
            _status.Text = "Checked today at " + now.ToString("t", System.Globalization.CultureInfo.CurrentCulture) +
                           ". See the panel above.";
        }

        /// <summary>
        /// Show <paramref name="problem"/> in the panel, or take the panel away for null. Logged as shown, in the
        /// words Copy details copies, so the log and a pasted report say the same thing.
        /// </summary>
        private void ShowProblem(CatalogProblem problem)
        {
            _shownProblem = problem;
            if (problem == null)
            {
                _problemPanel.Child = null;
                _problemPanel.Visibility = Visibility.Collapsed;
                return;
            }
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = problem.Title,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
            var rows = new Grid();
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < problem.Rows.Count; i++)
            {
                rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = problem.Rows[i].Key, Foreground = Brushes.Gray, Margin = new Thickness(0, 1, 10, 1) };
                var value = new TextBlock { Text = problem.Rows[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                rows.Children.Add(label);
                rows.Children.Add(value);
            }
            body.Children.Add(rows);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            // Try again is the Check button's own press, and held with it: never beside a check or an Update all.
            var retry = new Button
            {
                Content = "Try again",
                Padding = new Thickness(12, 2, 12, 2),
                Margin = new Thickness(0, 0, 6, 0),
                IsEnabled = !_updatingAll && !_checkInFlight,
            };
            retry.Click += delegate (object sender, RoutedEventArgs e)
            {
                if (_updatingAll || _checkInFlight) return;
                CheckButton_Click(sender, e);
            };
            var copy = new Button { Content = "Copy details", Padding = new Thickness(12, 2, 12, 2) };
            copy.Click += delegate { CopyDetails(); };
            buttons.Children.Add(retry);
            buttons.Children.Add(copy);
            body.Children.Add(buttons);
            // The coloured bar down the left edge, inside the grey frame: red, or amber while the rest works.
            _problemPanel.Child = new Border
            {
                BorderBrush = problem.Warning ? Brushes.Goldenrod : Brushes.Salmon,
                BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Child = body,
            };
            _problemPanel.Visibility = Visibility.Visible;
            StartUp.AddDebugInfo(problem.Warning ? StartUp.DEBUG_TYPE.info : StartUp.DEBUG_TYPE.warning,
                "[module] modules pane: " + problem.ForLog());
        }

        /// <summary>Copy details: the panel's words, through the seam (the clipboard, shipped).</summary>
        private void CopyDetails()
        {
            if (_shownProblem == null) return;
            try
            {
                _seams.CopyText(_shownProblem.ForCopy());
                _status.Text = "Copied the details.";
            }
            catch (Exception ex) { _status.Text = "Couldn't copy the details: " + PaneText.Short(ex.Message); }
        }

        /// <summary>The panel as shown, for --wpf-options-selftest; null while it is hidden.</summary>
        internal Border ProblemPanel { get { return _problemPanel.Visibility == Visibility.Visible ? _problemPanel : null; } }

        /// <summary>The problem the panel shows, for --wpf-options-selftest; null while it is hidden.</summary>
        internal CatalogProblem ShownProblem { get { return _shownProblem; } }

        private const string CheckButtonText = "Check for modules online";

        /// <summary>The status line's text, for --wpf-options-selftest: the only channel the pane reports a
        /// press through.</summary>
        internal string StatusText { get { return _status.Text; } }

        /// <summary>Whether the pane is still up to be written to after an await. A pane built headless by
        /// --wpf-options-selftest is never Loaded, so its seams say to treat it as up; the shipped seams never
        /// do, which leaves this exactly IsLoaded.</summary>
        private bool IsUp { get { return IsLoaded || _seams.CountsAsLoaded; } }

        private void Reload()
        {
            _installedList.Children.Clear();
            try
            {
                bool any = false;
                foreach (string id in EnumerateInstalledIds())
                {
                    _installedList.Children.Add(BuildInstalledRow(id));
                    any = true;
                }
                if (!any)
                    _installedList.Children.Add(new TextBlock
                    {
                        Text = "No modules installed yet.",
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(6, 4, 0, 4),
                    });
            }
            catch (Exception ex) { _status.Text = "Couldn't list modules: " + ex.Message; }
            // HERE, with the rows: Reload runs after the on-open fetch, after Check and after an install, so
            // the button's count is worked out from the very offers the rows just rendered.
            RefreshUpdateAllButton();
        }

        // <baseDir>\modules for the shipped pane (ModulesPaneSeams.Live); a throwaway folder under a self-test.
        private string ModulesRoot() { return _seams.ModulesRoot; }

        private IEnumerable<string> EnumerateInstalledIds()
        {
            string modulesDir = ModulesRoot();
            if (!Directory.Exists(modulesDir)) yield break;
            foreach (string dir in Directory.GetDirectories(modulesDir))
            {
                string id = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(id)) yield return id;
            }
        }

        private ModuleInfo LoadedInfo(string id) { return _seams.LoadedInfo(id); }

        // The live ModuleInfo for a currently-loaded module id, or null when it's on disk but not (yet)
        // loaded -- e.g. just installed, still waiting on the restart prompt. The shipped seam for LoadedInfo.
        internal static ModuleInfo LiveLoadedInfo(string id)
        {
            try
            {
                if (Program.Mainthread == null) return null;
                foreach (IModule m in Program.Mainthread.LoadedModules)
                    if (m != null && m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase))
                        return m.Info;
            }
            catch { }
            return null;
        }

        // Why a module on disk is not running, or null when it loaded fine (or the host isn't up). Without
        // this a broken module reads as "installed — restart to activate" forever, and the only way out is
        // Uninstall, which deletes settings and keys the user never meant to lose.
        private static DesktopAICompanion.Plugins.ModuleLoadFailure LoadFailure(string id)
        {
            try
            {
                if (Program.Mainthread == null) return null;
                foreach (DesktopAICompanion.Plugins.ModuleLoadFailure f in Program.Mainthread.ModuleFailures)
                    if (f != null && string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase))
                        return f;
            }
            catch { }
            return null;
        }

        private FrameworkElement BuildInstalledRow(string id)
        {
            ModuleInfo info = LoadedInfo(id);
            DesktopAICompanion.Plugins.ModuleLoadFailure failure = info == null ? LoadFailure(id) : null;
            var row = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 260 };
            nameStack.Children.Add(new TextBlock { Text = info != null ? info.Name : id, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

            string versionText;
            Brush versionBrush = Brushes.Gray;
            if (info != null) versionText = "v" + info.Version;
            else if (failure == null) versionText = "installed — restart to activate";
            else if (failure.NeedsNewerHost)
            {
                // The module is fine and this app is behind it. Say so: reinstalling would achieve nothing.
                versionText = "needs a newer app — " + failure.Reason;
                versionBrush = Brushes.Goldenrod;
            }
            else
            {
                versionText = "failed to load — " + failure.Reason;
                versionBrush = Brushes.Salmon;
            }
            nameStack.Children.Add(new TextBlock
            {
                Text = versionText,
                FontSize = 11,
                Foreground = versionBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = failure != null ? failure.Reason : null,
            });
            sp.Children.Add(nameStack);

            // A broken install has one non-destructive way out: replace the install folder from the catalog.
            // The existing install flow already does exactly that and leaves the module's data alone, so a
            // "Reinstall" is the same call as a first install -- it just needs the catalog fetched first.
            if (failure != null && !failure.NeedsNewerHost)
            {
                CatalogModule offered = CatalogEntry(id);
                var repair = new Button
                {
                    Content = offered != null ? "Reinstall v" + offered.Version : "Reinstall",
                    MinWidth = 120,
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = offered != null && !_updatingAll,
                    ToolTip = offered != null
                        ? "Download this module again and replace the installed copy. Your settings are kept."
                        : "Use “Check for modules online” first, so there is a copy to reinstall from.",
                };
                if (offered != null)
                    repair.Click += async delegate { await InstallModuleAsync(offered, repair); };
                sp.Children.Add(repair);
            }

            // An update offer needs the module's LIVE version, so it only appears for a loaded module (a
            // just-installed one pending restart reports no version yet) and only once the catalog is in
            // hand. The pane now fetches that itself when it opens, so this no longer waits on a button.
            // UpdateOfferFor decides it, for this row and for Update all alike.
            UpdateOffer offer = UpdateOfferFor(id, info);
            if (offer != null && offer.Staged)
            {
                // Staged by Update all, or by this row with the restart declined: the next start applies it, so
                // it is neither offered nor downloaded again. The row used to keep the old version beside a
                // live Update button, which invited the same download twice.
                nameStack.Children.Add(new TextBlock
                {
                    Text = "An update is staged and applies at the next restart.",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (offer != null && offer.NeedsNewerApp != null)
            {
                // NOT OFFERED: the row asks the loader's question too. No update path asked it, so a build whose
                // MinHostVersion this host cannot satisfy was downloaded, swapped in over a working copy, and
                // refused by the loader at the next start, leaving the module unloadable with nothing to roll
                // back to. The row names the build and what it needs instead.
                nameStack.Children.Add(new TextBlock
                {
                    Text = "v" + offer.Module.Version + " needs a newer app: " + offer.NeedsNewerApp,
                    FontSize = 11,
                    Foreground = Brushes.Goldenrod,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (offer != null)
            {
                CatalogModule newer = offer.Module;
                var update = new Button
                {
                    Content = "Update to v" + newer.Version,
                    MinWidth = 120,
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = !_updatingAll,   // Update all is staging this module, or another, right now
                };
                ModuleInfo installedInfo = info;
                update.Click += async delegate { await UpdateModuleAsync(newer, update, installedInfo); };
                sp.Children.Add(update);
            }

            // THE ROW SAYS WHY ITS UPDATE IS MISSING (mockup M2): the catalog's entry for this module was refused, so
            // no offer can exist, and without this line the row reads as up to date. Said only for a refused version
            // newer than the running one, or one the entry did not give; an older or equal one offers nothing anyway.
            CatalogRejection refusedEntry = offer == null && info != null ? RefusedEntry(id) : null;
            if (refusedEntry != null &&
                (refusedEntry.Version.Length == 0 || AppUpdateCheck.IsNewer(refusedEntry.Version, info.Version)))
            {
                nameStack.Children.Add(new TextBlock
                {
                    Text = refusedEntry.Version.Length > 0
                        ? "v" + refusedEntry.Version + " is in the catalog but its entry is invalid, so it is not offered (see above)."
                        : "Its catalog entry is invalid, so no update can be offered (see above).",
                    FontSize = 11,
                    Foreground = Brushes.Goldenrod,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            // Not while Update all runs: the run's own MarkForUpdate of the same module forgets a pending removal
            // (F352), so an uninstall pressed mid-run would be silently undone, after asking for a restart of
            // its own halfway through the run.
            var uninstall = new Button { Content = "Uninstall", Width = 80, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_updatingAll };
            uninstall.Click += delegate { UninstallModule(id, info != null ? info.Name : id); };
            sp.Children.Add(uninstall);

            row.Child = sp;
            return row;
        }

        /// <summary>
        /// The catalog entry for <paramref name="id"/> when it is strictly newer than what is installed, else
        /// null. The version rule itself lives in <see cref="DesktopAICompanion.Plugins.ModuleUpdateScan"/> so this
        /// button and the weekly background check cannot drift apart on what counts as an update.
        /// </summary>
        private CatalogModule FindCatalogUpdate(string id, ModuleInfo info)
        {
            if (info == null) return null;
            return DesktopAICompanion.Plugins.ModuleUpdateScan.FindUpdate(_lastCatalog, id, info.Version);
        }

        /// <summary>The catalog's entry for an id whatever its version, for repairing a broken install. This
        /// deliberately does NOT compare versions like <see cref="FindCatalogUpdate"/> does: a module that
        /// failed to load reports no live version to compare against, and reinstalling the same version is
        /// exactly the point.</summary>
        private CatalogModule CatalogEntry(string id)
        {
            if (_lastCatalog == null || _lastCatalog.Modules == null) return null;
            foreach (CatalogModule m in _lastCatalog.Modules)
                if (m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                    return m;
            return null;
        }

        /// <summary>
        /// Update in place, keeping the module's data. The payload cannot be written over the install folder
        /// from here (this process has the module's DLL loaded and locked), so it is verified, unpacked into a
        /// staging folder, and swapped in by the next launch -- see <see cref="PendingModuleUpdates"/>. The
        /// module's data directory is deliberately untouched, unlike an uninstall: settings, keys and history
        /// surviving an update is the whole point.
        ///
        /// The consent and the download-and-stage are <see cref="ConfirmUpdatePermissions"/> and
        /// <see cref="StageUpdateAsync"/>, which Update all takes for every module it fetches, so the row's
        /// button and the run cannot drift on what a module is asked or how its payload reaches the next launch.
        /// </summary>
        private async Task UpdateModuleAsync(CatalogModule module, Button update, ModuleInfo installed)
        {
            if (module == null) return;

            // Consent BEFORE the download, and before anything is staged (ConfirmUpdatePermissions says why).
            ModulePermissions added;
            if (!ConfirmUpdatePermissions(module, installed, out added))
            {
                _status.Text = "Left " + (module.Name ?? module.Id) + " as it is. It was asking for: "
                               + DesktopAICompanion.Plugins.ModulePermissionConsent.Describe(added) + ".";
                return;
            }

            update.IsEnabled = false;
            _status.Text = "Downloading " + module.Name + " v" + module.Version + "…";
            // The staging folder this attempt owns until MarkForUpdate hands it to the next launch; the
            // catches delete it (F366), because nothing else ever would have.
            string stagedHere = null;
            _downloadsInFlight++;
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                stagedHere = await StageUpdateAsync(module, _netCts.Token);
                if (stagedHere == null) return;   // the pane went away during the download; nothing is staged
                // The restart goes through the tested save-then-restart gate (RA-248, RA-249): the marker write
                // is the save. A failed write THROWS past the gate (RA-296) into the catch below, which discards
                // the staged copy and says so, and no restart that would apply nothing is asked for.
                Program.TryRequestRestartAfterSave(
                    delegate { _seams.MarkForUpdate(module.Id); return true; },
                    delegate
                    {
                        stagedHere = null;   // marked: the next launch owns it now
                        _status.Text = module.Name + " v" + module.Version + " is ready to apply. Your settings are kept.";
                        RestartToApply();
                    });
                // Restart declined: the row now says the update is staged, rather than offering it again.
                if (IsUp) Reload();
            }
            // Said out loud, as the install below has been since its own silent swallow was found (F366):
            // the Check button cancels this token, so the status line read the check's result over an
            // update the user never learned had stopped. The staged folder goes whether or not the pane is
            // still loaded, because the Unloaded cancel strands it exactly the same way.
            catch (OperationCanceledException)
            {
                DiscardStaged(stagedHere);
                if (IsLoaded) _status.Text = "Stopped updating " + module.Name + ".";
            }
            catch (Exception ex)
            {
                DiscardStaged(stagedHere);
                if (IsLoaded) _status.Text = "Couldn't update " + module.Name + ": " + PaneText.Short(ex.Message);
            }
            finally
            {
                _downloadsInFlight--;
                if (IsLoaded) update.IsEnabled = true;
            }
        }

        /// <summary>
        /// The consent an update needs before a byte of it is fetched, put the same way whichever button
        /// asked: the row's Update and Update all both come through here. True to go ahead; false when the
        /// user said No, with <paramref name="added"/> naming what the update was asking for.
        /// </summary>
        private bool ConfirmUpdatePermissions(CatalogModule module, ModuleInfo installed, out ModulePermissions added)
        {
            // ModulePermissions' own doc block promises that "a module that later widens its set re-prompts
            // rather than widening silently", which is the justification for the whole disclosure model --
            // and until 2026-09-17 it had no implementation anywhere: the "wants: ..." line was rendered only
            // on the pre-install row, and neither the update path nor the background scan compared the sets.
            // An update could go from "wants: Speech, Storage" to adding AgentTranscripts, the most sensitive
            // read in the application, with no more ceremony than a version bump.
            //
            // Silent when nothing widened, which is almost every update: a prompt on each one trains the user
            // to click through it, and then the prompt that matters is clicked through too.
            added = DesktopAICompanion.Plugins.ModulePermissionConsent.NewlyRequested(
                installed != null ? installed.Permissions : ModulePermissions.None, module.Permissions);
            if (added == ModulePermissions.None) return true;
            return _seams.AskYesNo(
                DesktopAICompanion.Plugins.ModulePermissionConsent.PromptText(module.Name, module.Version, added),
                "Update " + (module.Name ?? module.Id) + "?",
                MessageBoxImage.Warning);
        }

        /// <summary>
        /// One update's verified download and its unpack into staging, taken by the row's Update and by every
        /// module Update all fetches. Returns the staged folder, which the caller owns until its marker write
        /// hands it to the next launch; null when the pane went away during the download, with nothing staged.
        /// A failure part-way deletes what this call staged (F366) and rethrows, so the caller's catch has
        /// nothing of this call's left to clean up.
        /// </summary>
        private async Task<string> StageUpdateAsync(CatalogModule module, CancellationToken token)
        {
            string installDir = SafeModuleDir(module.Id);   // validates the id, and where it will land
            if (!Directory.Exists(installDir))
                throw new InvalidDataException(module.Name + " is not installed.");

            // The catalog's SHA-256 is checked inside the download (RemoteCatalogClient.DownloadVerifiedAsync,
            // behind the shipped seam), so a payload that does not match never reaches the staging folder.
            byte[] bytes = await _seams.DownloadVerified(module, token);
            if (!IsUp) return null;

            string staged = DesktopAICompanion.Plugins.PendingModuleUpdates.PrepareStagingDirectory(module.Id, _seams.StagingRoot);
            try
            {
                // Awaited, not synchronous: fortunes.zip is ~31 MB and unpacking it on the UI thread froze the
                // settings window mid-update. Same extraction implementation, so .NET still rejects any entry
                // that would escape the target directory.
                using (var zipStream = new MemoryStream(bytes))
                    await ZipFile.ExtractToDirectoryAsync(zipStream, staged, true, token);
            }
            catch
            {
                DiscardStaged(staged);
                throw;
            }
            return staged;
        }

        // ---- Update all ---------------------------------------------------------------------

        /// <summary>One installed module's update as the pane sees it. <see cref="Staged"/>: the next start
        /// applies an update already, and <see cref="Module"/> is null. Otherwise the catalog entry, the live
        /// info it would replace, the loader's reason when this host cannot run the offered build (null when it
        /// can), and whether an uninstall of the module waits for the next start.</summary>
        private sealed class UpdateOffer
        {
            public CatalogModule Module;
            public ModuleInfo Installed;
            public bool Staged;
            public string NeedsNewerApp;
            public bool BeingRemoved;

            /// <summary>What the row's own Update button is offered for: not staged, and runnable here.</summary>
            public bool Offerable { get { return !Staged && Module != null && NeedsNewerApp == null; } }
        }

        /// <summary>
        /// What the pane does with one installed module's update, decided in ONE place for the row's button and
        /// for Update all so the two cannot disagree about a module: null when nothing is staged and the catalog
        /// offers nothing newer; otherwise staged, needing a newer app, or offered (being uninstalled or not).
        /// The host question is the install row's, with its host version (ModuleHostRequirement, as the loader
        /// asks it), and a staged update outranks any newer offer: one update waits for the start already.
        /// </summary>
        private UpdateOffer UpdateOfferFor(string id, ModuleInfo info)
        {
            if (info == null) return null;   // only a loaded module reports a version to update from
            if (_seams.IsStaged(id)) return new UpdateOffer { Installed = info, Staged = true };
            CatalogModule newer = FindCatalogUpdate(id, info);
            if (newer == null) return null;
            string requirement;
            bool runnable = Plugins.ModuleHostRequirement.IsSatisfied(
                System.Windows.Forms.Application.ProductVersion, newer.MinHostVersion, out requirement);
            return new UpdateOffer
            {
                Module = newer,
                Installed = info,
                NeedsNewerApp = runnable ? null
                    : (string.IsNullOrWhiteSpace(requirement) ? "this module needs a newer version of the app" : requirement),
                BeingRemoved = _seams.IsBeingRemoved(id),
            };
        }

        /// <summary>Every installed module's update that is not already staged, in row order: what Update all
        /// counts, takes, or names as left out.</summary>
        private List<UpdateOffer> CollectUpdateOffers()
        {
            var offers = new List<UpdateOffer>();
            foreach (string id in EnumerateInstalledIds())
            {
                UpdateOffer offer = UpdateOfferFor(id, LoadedInfo(id));
                if (offer != null && !offer.Staged) offers.Add(offer);
            }
            return offers;
        }

        /// <summary>
        /// Show "Update all (N)" while two or more updates are ones a press would take: offered on their row
        /// (not staged, runnable here) and not waiting behind an uninstall. N counts exactly those, so a list
        /// whose second offer needs a newer app shows no Update all at all: the one update left already has its
        /// own button. Held disabled while a run goes, like every other action in the pane.
        /// </summary>
        private void RefreshUpdateAllButton()
        {
            var taken = new List<DesktopAICompanion.Plugins.ModuleUpdateOffer>();
            var leftOut = new List<string>();
            try
            {
                foreach (UpdateOffer offer in CollectUpdateOffers())
                {
                    if (offer.Offerable && !offer.BeingRemoved)
                        taken.Add(new DesktopAICompanion.Plugins.ModuleUpdateOffer { Offered = offer.Module, InstalledVersion = offer.Installed.Version });
                    else
                        leftOut.Add(offer.Module.Name ?? offer.Module.Id);
                }
            }
            catch { taken.Clear(); }   // a listing that failed shows no Update all; Reload has said why
            _updateAllButton.IsEnabled = !_updatingAll;
            if (taken.Count < 2)
            {
                _updateAllButton.Visibility = Visibility.Collapsed;
                return;
            }
            _updateAllButton.Content = "Update all (" + taken.Count + ")";
            _updateAllButton.ToolTip = "Updates " + DesktopAICompanion.Plugins.ModuleUpdateScan.Describe(taken) +
                ", then asks once to restart. Any that asks for new permissions asks you first. Your settings are kept." +
                (leftOut.Count == 0 ? "" : " Left out: " + string.Join(", ", leftOut.ToArray()) + ".");
            _updateAllButton.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Update every module the pane offers an Update for and this host can run, each through the row's own
        /// path (<see cref="ConfirmUpdatePermissions"/>, <see cref="StageUpdateAsync"/>, the pending-update
        /// marker), then ask to restart ONCE for all of them. A module left out (its update needs a newer app,
        /// an uninstall of it waits for the next start, or the user declined what it asks for) and one whose
        /// download or check fails are each named in the result and stop nothing else; the restart is asked for
        /// only when at least one update was staged. An update already staged is not offered, so not taken.
        ///
        /// Every consent comes first, before the first byte of any module is fetched: the user answers every
        /// question at the press and the run then needs nobody, and no payload is on disk before its own
        /// consent, the order the row's button keeps. The marker is written per module, right after its
        /// staging, so a failure later in the run cannot take an earlier staged module with it; the marker
        /// already holds any number of ids, and the next start swaps each one in (PendingModuleUpdates).
        /// </summary>
        private async Task UpdateAllAsync()
        {
            if (_updatingAll) return;   // a second press while the run is going
            // Never beside a row's own download, an install or a check. A run that included a module whose
            // own Update is mid-download would stage it twice into its one staging folder, the second
            // PrepareStagingDirectory deleting the first's half-unpacked payload under it; an install would
            // ask for a restart of its own halfway through the run; a check landing rebuilds the rows under it.
            if (_downloadsInFlight > 0 || _checkInFlight)
            {
                _status.Text = "Update all waits for the download or check already running. Press it again when that finishes.";
                return;
            }

            _updatingAll = true;
            _checkButton.IsEnabled = false;
            var staged = new List<string>();
            var report = new List<string>();
            int offered = 0;
            try
            {
                Reload();   // every row's buttons, and Update all itself, render disabled from here on
                RenderAvailable(DiffNew());
                List<UpdateOffer> offers = CollectUpdateOffers();
                offered = offers.Count;
                var lines = new string[offers.Count];
                var toFetch = new List<int>();
                for (int i = 0; i < offers.Count; i++)
                {
                    UpdateOffer offer = offers[i];
                    string name = offer.Module.Name ?? offer.Module.Id;
                    if (offer.NeedsNewerApp != null)
                    {
                        lines[i] = "✗ Left " + name + " as it is. Needs a newer app: " + offer.NeedsNewerApp + ".";
                        continue;
                    }
                    // Its MarkForUpdate would Unmark the removal (F352), and the run would quietly undo an
                    // uninstall the user asked for. The row's own button may still update it: there the
                    // user chose that module, and the update winning is the documented outcome.
                    if (offer.BeingRemoved)
                    {
                        lines[i] = "✗ Left " + name + " as it is. It is set to be uninstalled at the next start.";
                        continue;
                    }
                    ModulePermissions added;
                    if (!ConfirmUpdatePermissions(offer.Module, offer.Installed, out added))
                    {
                        lines[i] = "✗ Left " + name + " as it is. It was asking for: "
                                   + DesktopAICompanion.Plugins.ModulePermissionConsent.Describe(added) + ".";
                        continue;
                    }
                    toFetch.Add(i);
                }

                if (toFetch.Count > 0)
                {
                    if (_netCts == null) _netCts = new CancellationTokenSource();
                    CancellationToken token = _netCts.Token;
                    _downloadsInFlight++;
                    try
                    {
                        for (int n = 0; n < toFetch.Count; n++)
                        {
                            CatalogModule module = offers[toFetch[n]].Module;
                            _status.Text = "Downloading " + module.Name + " v" + module.Version +
                                           " (" + (n + 1) + " of " + toFetch.Count + ")…";
                            string stagedHere = null;
                            try
                            {
                                stagedHere = await StageUpdateAsync(module, token);
                                if (stagedHere == null) return;   // the pane went away during the download
                                _seams.MarkForUpdate(module.Id);
                                stagedHere = null;   // marked: the next launch owns it now
                                staged.Add(module.Id);
                                lines[toFetch[n]] = "✓ " + module.Name + " v" + module.Version + " is ready to apply.";
                            }
                            catch (OperationCanceledException)
                            {
                                // The pane is being torn down (Unloaded cancels the token, and the Check button,
                                // the other canceller, is held for the run). What is already marked stays marked.
                                DiscardStaged(stagedHere);
                                lines[toFetch[n]] = "✗ Stopped updating " + module.Name + ".";
                                for (int rest = n + 1; rest < toFetch.Count; rest++)
                                    lines[toFetch[rest]] = "✗ Stopped before updating " + offers[toFetch[rest]].Module.Name + ".";
                                break;
                            }
                            catch (Exception ex)
                            {
                                // This module's line, and the run goes on to the next one.
                                DiscardStaged(stagedHere);
                                lines[toFetch[n]] = "✗ Couldn't update " + module.Name + ": " + PaneText.Short(ex.Message);
                            }
                        }
                    }
                    finally { _downloadsInFlight--; }
                }
                foreach (string line in lines)
                    if (!string.IsNullOrEmpty(line)) report.Add(line);
            }
            catch (Exception ex)
            {
                // Listing the offers or asking a question failed: nothing past what is already marked was staged.
                report.Add("✗ Couldn't update all: " + PaneText.Short(ex.Message));
            }
            finally
            {
                _updatingAll = false;
                if (IsUp)
                {
                    _checkButton.IsEnabled = true;
                    // The buttons come back, worked out afresh. Caught here, because nothing above an async
                    // click handler would catch it: an escaping exception ends the process.
                    try
                    {
                        Reload();
                        RenderAvailable(DiffNew());
                    }
                    catch (Exception ex) { report.Add("✗ Couldn't list the modules again: " + PaneText.Short(ex.Message)); }
                }
            }
            if (!IsUp) return;

            report.Add(staged.Count == 0
                ? "Nothing was updated."
                : staged.Count + " of " + offered + " updates " + (staged.Count == 1 ? "is" : "are") +
                  " ready to apply. Your settings are kept.");
            _status.Text = string.Join(Environment.NewLine, report.ToArray());
            try
            {
                // ONE restart for every module staged, through the row's tested save-then-restart gate (RA-248,
                // RA-249). The save here is "at least one marker write succeeded", so a run that staged nothing
                // asks for no restart that would apply nothing.
                Program.TryRequestRestartAfterSave(
                    delegate { return staged.Count > 0; },
                    delegate { RestartToApply(staged.Count); });
            }
            catch (Exception ex)
            {
                // The row's restart sits inside its own catch for the same reason; what is marked stays marked
                // and applies at the next start whichever way the app is restarted.
                _status.Text += Environment.NewLine + "✗ Couldn't restart: " + PaneText.Short(ex.Message);
            }
        }

        /// <summary>
        /// Remove a staging folder an interrupted install or update left behind (F366, F367). A cancel
        /// from the Check button or from the pane being torn down, or an extraction error, lands between
        /// PrepareStagingDirectory and the hand-off that would have made the folder somebody's business;
        /// PendingModuleUpdates.ProcessPending visits only marked ids, so an unmarked payload -- up to
        /// ~31 MB -- sat there until the same module was updated again. Null means nothing is owned.
        /// </summary>
        private static void DiscardStaged(string staged)
        {
            if (string.IsNullOrEmpty(staged)) return;
            try { if (Directory.Exists(staged)) Directory.Delete(staged, true); } catch { }
        }

        private void UninstallModule(string id, string displayName)
        {
            var choice = System.Windows.MessageBox.Show(
                "Uninstall " + displayName + " and its settings? DesktopAICompanion needs to restart to finish.",
                "Uninstall module",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (choice != System.Windows.MessageBoxResult.Yes) return;
            try
            {
                // Can't delete the install folder here directly -- its DLL is locked while loaded in THIS
                // process. Mark it; the next launch (which never loads it) deletes it before ModuleHost ever
                // gets a chance to re-lock it (see PendingModuleRemovals).
                // Through the save-then-restart gate (RA-248, RA-249): a marker write that throws (RA-296) never
                // reaches the restart, and the catch below names the failure.
                Program.TryRequestRestartAfterSave(
                    delegate { DesktopAICompanion.Plugins.PendingModuleRemovals.MarkForRemoval(id); return true; },
                    RestartToApply);
            }
            catch (Exception ex) { _status.Text = "Couldn't uninstall " + displayName + ": " + PaneText.Short(ex.Message); }
        }

        // ---- Check for modules online (catalog) ----------------------------------

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            _checkButton.IsEnabled = false;
            _checkInFlight = true;   // Update all will not start under it (see UpdateAllAsync)
            // The problem panel's Try again comes here too; its When row says which was pressed.
            _occasion = ReferenceEquals(sender, _checkButton)
                ? "when you pressed “" + CheckButtonText + "”"
                : "when you pressed Try again";
            _status.Text = "Checking for modules online…";
            try
            {
                if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); }
                _netCts = new CancellationTokenSource();
                // Pressing the button is an explicit "check NOW", so the shared copy is dropped and
                // REFILLED (F286): see the Companions pane's Check for why both halves matter. Through the
                // seam, whose shipped wiring is RemoteCatalogClient.RefreshSharedAsync, so the panel's Try
                // again can be pressed by --wpf-options-selftest with no network.
                _lastCatalog = await _seams.RefreshCatalog(_netCts.Token);
                if (!IsUp) return;
                // The rows, the install list, the panel (gone for a clean read) and the "Checked" line: the
                // catalog's whole rendering, the one the on-open fetch does.
                ShowCatalog(_lastCatalog);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsUp) ShowFetchFailure(ex); }
            finally
            {
                _checkInFlight = false;
                if (IsUp) _checkButton.IsEnabled = true;
            }
        }

        // The rows showing an Update button, by the rule they are rendered with: an update already staged or
        // one this host cannot run is no longer "with an update" in the Check result, since nothing offers it.
        private int CountAvailableUpdates()
        {
            int count = 0;
            try
            {
                foreach (string id in EnumerateInstalledIds())
                {
                    UpdateOffer offer = UpdateOfferFor(id, LoadedInfo(id));
                    if (offer != null && offer.Offerable) count++;
                }
            }
            catch { }
            return count;
        }

        private static string Describe(int count, string tail)
        {
            if (count == 0) return tail == "available to install" ? "No new modules right now." : "";
            return count + (count == 1 ? " module " : " modules ") + tail + ".";
        }

        // Catalog modules not already present on disk.
        private List<CatalogModule> DiffNew()
        {
            var result = new List<CatalogModule>();
            if (_lastCatalog == null) return result;
            var local = new HashSet<string>(EnumerateInstalledIds(), StringComparer.OrdinalIgnoreCase);
            foreach (CatalogModule m in _lastCatalog.Modules)
                if (!local.Contains(m.Id)) result.Add(m);
            return result;
        }

        private void RenderAvailable(List<CatalogModule> modules)
        {
            _availableList.Children.Clear();
            bool any = modules.Count > 0;
            _availableHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _availableList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            foreach (CatalogModule m in modules)
                _availableList.Children.Add(BuildAvailableRow(m));
        }

        private FrameworkElement BuildAvailableRow(CatalogModule module)
        {
            var row = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6) };
            var sp = new StackPanel();

            var nameStack = new StackPanel();
            nameStack.Children.Add(new TextBlock { Text = module.Name + "  v" + module.Version, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(module.Description))
                nameStack.Children.Add(new TextBlock { Text = module.Description, FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            // Shown BEFORE install, per its own declared permissions -- a consent signal, not a hard gate.
            // The tooltip says which half of the set the app enforces, because the line reads as a control
            // (the register records ModulePermissions.Animation as declarative for exactly this reason): the
            // host gates its own Audio, Network, Voice and Companions verbs on the flags; the rest, Animation
            // among them, are the module's statement of what it does.
            string permsText = module.Permissions == ModulePermissions.None
                ? "no special permissions"
                : "wants: " + PermissionsText(module.Permissions);
            nameStack.Children.Add(new TextBlock
            {
                Text = permsText,
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray,
                ToolTip = "What the module declares it does. The app enforces Audio, Network, Voice and Companions on its own " +
                          "verbs; the other flags (Animation, Speech, ScreenContext, Storage, Hotkey, LaunchProcess, " +
                          "AgentTranscripts) are statements the module makes, not restrictions the app applies.",
            });
            sp.Children.Add(nameStack);

            // ASK THE SAME QUESTION THE LOADER WILL. ModuleHost refuses a module whose MinHostVersion
            // this host cannot satisfy, and until 2026-09-27 the catalog dropped that field entirely, so
            // the pane could not know: it offered agentflow (needs 1.2.0) to every user still on 1.1.x,
            // downloaded ~1 MB of payload, installed it, and the loader then refused it with nothing here
            // to say why. Using ModuleHostRequirement rather than comparing versions locally means the
            // pane and the loader cannot drift into disagreeing about the same module.
            string requirement;
            bool runnable = Plugins.ModuleHostRequirement.IsSatisfied(
                System.Windows.Forms.Application.ProductVersion, module.MinHostVersion, out requirement);

            var install = new Button { Content = "Install", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            // Held while Update all runs as well: an install asks for a restart of its own, halfway through the run.
            install.IsEnabled = runnable && !_updatingAll;
            install.Click += async delegate { await InstallModuleAsync(module, install); };
            sp.Children.Add(install);
            if (!runnable)
            {
                // The reason, not just a greyed button: "needs host 1.2.0 or newer (this host is 1.1.4)"
                // tells the user the action to take. A disabled control with no explanation reads as a bug.
                sp.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(requirement)
                        ? "This module needs a newer version of the app."
                        : "Needs a newer app: " + requirement,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            row.Child = sp;
            return row;
        }

        private static string PermissionsText(ModulePermissions permissions)
        {
            var parts = new List<string>();
            foreach (ModulePermissions flag in Enum.GetValues(typeof(ModulePermissions)))
                if (flag != ModulePermissions.None && (permissions & flag) == flag) parts.Add(flag.ToString());
            return parts.Count > 0 ? string.Join(", ", parts) : "none";
        }

        private async Task InstallModuleAsync(CatalogModule module, Button install)
        {
            if (module == null) return;
            install.IsEnabled = false;
            _status.Text = "Downloading " + module.Name + "…";
            // See UpdateModuleAsync: the staging folder this attempt owns, deleted by the catches.
            string stagedHere = null;
            _downloadsInFlight++;
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                byte[] bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                    module.Url, module.Sha256, RemoteCatalogClient.MaximumModuleBytes, _netCts.Token);
                if (!IsLoaded) return;

                string installDir = SafeModuleDir(module.Id);

                // A REINSTALL NEVER DELETES IN PLACE. This used to open with
                // Directory.Delete(installDir, true), and the Reinstall button is offered only for a
                // module that is already on disk and FAILED to load -- which is exactly when this
                // process may still hold its assembly. ModuleHost.LoadFrom calls
                // alc.LoadFromAssemblyPath BEFORE it records the failure, and AssemblyLoadContext.Unload
                // is a request rather than a synchronous unload: a module whose Init left a timer, a
                // thread or a host subscription behind keeps its DLL memory-mapped for the process
                // lifetime. The recursive delete would then remove deps.json, assets and the contracts
                // DLL, hit the locked file, throw, and leave a half-deleted folder with no rollback.
                //
                // The neighbouring paths already know this. UpdateModuleAsync stages and swaps on the
                // next launch (its own doc says the payload "cannot be written over the install folder
                // from here"), and UninstallModule defers deletion for the same reason. So does this
                // now, through the same machinery, which also gives it PendingModuleUpdates' move-aside
                // rollback for free.
                if (Directory.Exists(installDir))
                {
                    stagedHere = DesktopAICompanion.Plugins.PendingModuleUpdates.PrepareStagingDirectory(module.Id);
                    using (var zipStream = new MemoryStream(bytes))
                        await ZipFile.ExtractToDirectoryAsync(zipStream, stagedHere, true, _netCts.Token);
                    Program.TryRequestRestartAfterSave(
                        delegate { DesktopAICompanion.Plugins.PendingModuleUpdates.MarkForUpdate(module.Id); return true; },
                        delegate
                        {
                            stagedHere = null;   // marked: the next launch owns it now
                            _status.Text = module.Name + " is ready to reinstall. Your settings are kept.";
                            RestartToApply();
                        });
                    return;
                }

                // A genuinely NEW module: nothing is loaded, nothing is locked -- and it is still NOT
                // unpacked in place (F367). ExtractToDirectoryAsync honours the token between entries, so a
                // cancel mid-unpack (the Check button, or leaving the pane or closing Settings, which
                // cancels through Unloaded) left whatever entries had landed under modules/<id>. The pane
                // then listed the module as "installed — restart to activate", DiffNew dropped the id from
                // the install list so Install could not simply be pressed again, and the next launch's
                // loader reported a folder with no DLL as "failed to load". So it unpacks into the same
                // staging folder an update uses and is MOVED into place once whole: both roots sit under
                // AppContext.BaseDirectory, so the move is one same-volume rename and modules/<id> is
                // there whole or not at all. A process killed mid-unpack strands only a staging folder,
                // beside modules/ where the loader never looks, and PrepareStagingDirectory replaces it
                // on the next attempt.
                stagedHere = DesktopAICompanion.Plugins.PendingModuleUpdates.PrepareStagingDirectory(module.Id);
                // Awaited: see the update path. A synchronous unpack of a 31 MB module froze the window.
                using (var zipStream = new MemoryStream(bytes))
                    await ZipFile.ExtractToDirectoryAsync(zipStream, stagedHere, true, _netCts.Token);
                Directory.CreateDirectory(ModulesRoot());
                Directory.Move(stagedHere, installDir);
                stagedHere = null;   // it is the install folder now
                // A removal of this id that never finished (its data folder was locked, say) would otherwise
                // delete the module just installed on the next launch (F352).
                //
                // A failed marker write THROWS since RA-296, and this catch is what keeps a stale marker from
                // turning a successful install into "Couldn't install": the module is in place; only the
                // housekeeping failed, and the status says exactly that and what to do if it bites.
                string unmarkWarning = "";
                try { DesktopAICompanion.Plugins.PendingModuleRemovals.Unmark(module.Id); }
                catch (Exception ex)
                {
                    unmarkWarning = " A pending-uninstall marker could not be updated (" + PaneText.Short(ex.Message) +
                                    "); if the module is missing after the restart, install it again.";
                }

                _status.Text = module.Name + " installed." + unmarkWarning;
                Reload();
                RenderAvailable(DiffNew());
                // The restart is asked for only when the folder just written holds a module DLL the loader will
                // find (RA-248, RA-249): a payload that unpacked to nothing loadable gets no restart that would
                // apply nothing, and the status says what was missing.
                bool installLoadable = Program.TryRequestRestartAfterSave(
                    delegate { return DesktopAICompanion.Plugins.ModuleHost.FindModuleDll(installDir) != null; },
                    RestartToApply);
                if (!installLoadable)
                    _status.Text = module.Name + " was unpacked, but the payload holds no module DLL the loader would find, so it will not be activated at the next start.";
            }
            // Said out loud. A silent swallow here meant that pressing "Check for modules online"
            // mid-extract (which cancels this token) left the status line reading "Checking for modules
            // online" over a folder that had been emptied. The staged folder is removed whether or not the
            // pane is still loaded, since the Unloaded cancel is one of the two ways here (F366, F367).
            catch (OperationCanceledException)
            {
                DiscardStaged(stagedHere);
                if (IsLoaded) _status.Text = "Stopped installing " + module.Name + "; nothing was left behind.";
            }
            catch (Exception ex)
            {
                DiscardStaged(stagedHere);
                if (IsLoaded) _status.Text = "Couldn't install " + module.Name + ": " + PaneText.Short(ex.Message);
            }
            finally
            {
                _downloadsInFlight--;
                if (IsLoaded) install.IsEnabled = true;
            }
        }

        private string SafeModuleDir(string id)
        {
            if (!SecureDownload.IsSafeId(id)) throw new InvalidDataException("Unsafe module id.");
            string root = Path.GetFullPath(ModulesRoot())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(root, id));
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Module path escapes the modules folder.");
            return directory;
        }

        // Modules only load at startup (S6 phase 1 -- no hot-load), so any install/uninstall needs a real
        // restart to take effect. Reuses the dormant Program.RequestRestart/CompleteInstanceLifecycle chain
        // (release the instance lease -> relaunch DesktopAICompanion.exe) and asks the relaunch to reopen Settings
        // back on this pane via --reopen-options=Modules.
        private void RestartToApply()
        {
            RestartToApply(1);
        }

        /// <summary>The restart question for <paramref name="changes"/> staged changes. Update all asks it ONCE
        /// for every module it staged, in the plural when there are several; every other site asks for one.</summary>
        private void RestartToApply(int changes)
        {
            bool several = changes > 1;
            if (!_seams.AskYesNo(
                    several
                        ? "DesktopAICompanion needs to restart to apply these updates. Restart now?"
                        : "DesktopAICompanion needs to restart to apply this change. Restart now?",
                    "Restart required",
                    MessageBoxImage.Question))
            {
                _status.Text += several ? " Restart when you're ready to apply them." : " Restart when you're ready to apply it.";
                return;
            }
            Program.RequestRestart("Modules");
            Window ownerWindow = Window.GetWindow(this);
            if (ownerWindow != null) ownerWindow.Close();
            System.Windows.Forms.Application.Exit();
        }
    }

    /// <summary>
    /// What the Modules pane reaches beyond its own window for on the update path, gathered so
    /// --wpf-options-selftest can press its Update and Update all buttons against fakes: the folder it lists
    /// modules from, the staging root and the pending-update marker an update writes, whether an update is
    /// staged or an uninstall pending, the live module infos, the catalog's verified download, the yes/no
    /// questions it puts to the user, and whether a pane built headless (never Loaded) still counts as up.
    /// <see cref="Live"/> is the shipped wiring, the very calls the pane made directly before 2026-10-02, and
    /// nothing but that self-test builds another. Install, Reinstall and Uninstall are driven by no self-test
    /// and keep their own direct calls. The check-now fetch and the clipboard joined on 2026-10-06
    /// (feature/catalog-insight), so the problem panel's Try again and Copy details can be pressed there too.
    /// </summary>
    internal sealed class ModulesPaneSeams
    {
        internal string ModulesRoot;
        internal string StagingRoot;
        internal Action<string> MarkForUpdate;
        internal Func<string, bool> IsStaged;
        internal Func<string, bool> IsBeingRemoved;
        internal Func<string, ModuleInfo> LoadedInfo;
        internal Func<CatalogModule, CancellationToken, Task<byte[]>> DownloadVerified;
        internal Func<string, string, MessageBoxImage, bool> AskYesNo;
        internal bool CountsAsLoaded;
        /// <summary>The Check button's (and the problem panel's Try again) check-now fetch (feature/catalog-insight).</summary>
        internal Func<CancellationToken, Task<RemoteCatalog>> RefreshCatalog;
        /// <summary>Where the problem panel's Copy details puts its text: the clipboard, shipped.</summary>
        internal Action<string> CopyText;

        internal static ModulesPaneSeams Live()
        {
            return new ModulesPaneSeams
            {
                ModulesRoot = Path.Combine(AppContext.BaseDirectory, "modules"),
                StagingRoot = DesktopAICompanion.Plugins.PendingModuleUpdates.DefaultStagingRoot,
                MarkForUpdate = DesktopAICompanion.Plugins.PendingModuleUpdates.MarkForUpdate,
                IsStaged = DesktopAICompanion.Plugins.PendingModuleUpdates.IsStaged,
                IsBeingRemoved = DesktopAICompanion.Plugins.PendingModuleRemovals.IsMarked,
                LoadedInfo = ModulesPaneControl.LiveLoadedInfo,
                DownloadVerified = delegate (CatalogModule m, CancellationToken t)
                {
                    return RemoteCatalogClient.DownloadVerifiedAsync(m.Url, m.Sha256, RemoteCatalogClient.MaximumModuleBytes, t);
                },
                AskYesNo = delegate (string text, string caption, MessageBoxImage icon)
                {
                    return MessageBox.Show(text, caption, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;
                },
                CountsAsLoaded = false,
                // Pressing Check is an explicit "check NOW": the shared copy is dropped and REFILLED (F286).
                RefreshCatalog = delegate (CancellationToken token) { return RemoteCatalogClient.RefreshSharedAsync(token); },
                CopyText = Clipboard.SetText,
            };
        }
    }
}
