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
    /// </summary>
    internal sealed class ModulesPaneControl : ContentControl
    {
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
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 4, 0, 6), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

        // The most recent successful catalog fetch, so an install can re-diff locally without re-fetching.
        private RemoteCatalog _lastCatalog;
        private CancellationTokenSource _netCts;

        public ModulesPaneControl()
        {
            var root = new DockPanel { LastChildFill = true };

            var header = new StackPanel { Margin = new Thickness(4) };
            header.Children.Add(new TextBlock { Text = "Modules", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            header.Children.Add(new TextBlock
            {
                Text = "Optional features, installed on demand. Check online to see updates for what you " +
                       "already have. Installing, updating or removing one restarts the app.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            footer.Children.Add(_checkButton);
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scrollContent = new StackPanel();
            scrollContent.Children.Add(_installedList);
            scrollContent.Children.Add(_availableHeader);
            scrollContent.Children.Add(_availableList);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = scrollContent });
            Content = root;

            _checkButton.Click += CheckButton_Click;
            Unloaded += delegate { try { if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); _netCts = null; } } catch { } };

            Reload();
            RefreshCatalogOnOpen();
        }

        /// <summary>
        /// Fetch the catalog when the pane opens, so an available update is already showing.
        ///
        /// This control is rebuilt on every pane selection, so the constructor IS "on open" and _lastCatalog
        /// is always null here -- which is why, before this, no update could ever appear until the button was
        /// pressed. Fire-and-forget and failure-silent: a pane that cannot reach the network should render
        /// exactly as it did before, not show an error the user did not ask for.
        /// </summary>
        private async void RefreshCatalogOnOpen()
        {
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                CancellationToken token = _netCts.Token;
                RemoteCatalog catalog = await RemoteCatalogClient.FetchSharedAsync(token).ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsLoaded) return;
                _lastCatalog = catalog;
                Reload();
            }
            catch { }
        }

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
        }

        private static string ModulesRoot() { return Path.Combine(AppContext.BaseDirectory, "modules"); }

        private static IEnumerable<string> EnumerateInstalledIds()
        {
            string modulesDir = ModulesRoot();
            if (!Directory.Exists(modulesDir)) yield break;
            foreach (string dir in Directory.GetDirectories(modulesDir))
            {
                string id = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(id)) yield return id;
            }
        }

        // The live ModuleInfo for a currently-loaded module id, or null when it's on disk but not (yet)
        // loaded -- e.g. just installed, still waiting on the restart prompt.
        private static ModuleInfo LoadedInfo(string id)
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
                    IsEnabled = offered != null,
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
            CatalogModule newer = FindCatalogUpdate(id, info);
            if (newer != null)
            {
                var update = new Button
                {
                    Content = "Update to v" + newer.Version,
                    MinWidth = 120,
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                update.Click += async delegate { await UpdateModuleAsync(newer, update); };
                sp.Children.Add(update);
            }

            var uninstall = new Button { Content = "Uninstall", Width = 80, VerticalAlignment = VerticalAlignment.Center };
            uninstall.Click += delegate { UninstallModule(id, info != null ? info.Name : id); };
            sp.Children.Add(uninstall);

            row.Child = sp;
            return row;
        }

        /// <summary>
        /// The catalog entry for <paramref name="id"/> when it is strictly newer than what is installed, else
        /// null. The version rule itself lives in <see cref="DesktopAICompanion.Plugins.ModuleUpdateScan"/> so this
        /// button and the monthly background check cannot drift apart on what counts as an update.
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
        /// </summary>
        private async Task UpdateModuleAsync(CatalogModule module, Button update)
        {
            if (module == null) return;
            update.IsEnabled = false;
            _status.Text = "Downloading " + module.Name + " v" + module.Version + "…";
            try
            {
                string installDir = SafeModuleDir(module.Id);   // validates the id, and where it will land
                if (!Directory.Exists(installDir))
                    throw new InvalidDataException(module.Name + " is not installed.");

                if (_netCts == null) _netCts = new CancellationTokenSource();
                byte[] bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                    module.Url, module.Sha256, RemoteCatalogClient.MaximumModuleBytes, _netCts.Token);
                if (!IsLoaded) return;

                string staged = DesktopAICompanion.Plugins.PendingModuleUpdates.PrepareStagingDirectory(module.Id);
                // Awaited, not synchronous: fortunes.zip is ~31 MB and unpacking it on the UI thread froze the
                // settings window mid-update. Same extraction implementation, so .NET still rejects any entry
                // that would escape the target directory.
                using (var zipStream = new MemoryStream(bytes))
                    await ZipFile.ExtractToDirectoryAsync(zipStream, staged, true, _netCts.Token);
                DesktopAICompanion.Plugins.PendingModuleUpdates.MarkForUpdate(module.Id);

                _status.Text = module.Name + " v" + module.Version + " is ready to apply. Your settings are kept.";
                RestartToApply();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't update " + module.Name + ": " + Short(ex.Message); }
            finally { if (IsLoaded) update.IsEnabled = true; }
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
                DesktopAICompanion.Plugins.PendingModuleRemovals.MarkForRemoval(id);
                RestartToApply();
            }
            catch (Exception ex) { _status.Text = "Couldn't uninstall " + displayName + ": " + Short(ex.Message); }
        }

        // ---- Check for modules online (catalog) ----------------------------------

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            _checkButton.IsEnabled = false;
            _status.Text = "Checking for modules online…";
            try
            {
                if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); }
                _netCts = new CancellationTokenSource();
                // Pressing the button is an explicit "check NOW", so drop the shared copy first:
                // reusing an answer from seconds ago would make the button look like it did nothing.
                RemoteCatalogClient.InvalidateShared();
                _lastCatalog = await RemoteCatalogClient.FetchAsync(_netCts.Token);
                if (!IsLoaded) return;
                List<CatalogModule> available = DiffNew();
                RenderAvailable(available);
                Reload();   // installed rows can now offer updates against the catalog we just fetched
                int updates = CountAvailableUpdates();
                _status.Text = Describe(available.Count, "available to install") +
                    (updates > 0 ? "  " + Describe(updates, "with an update") : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't reach the catalog: " + Short(ex.Message); }
            finally { if (IsLoaded) _checkButton.IsEnabled = true; }
        }

        private int CountAvailableUpdates()
        {
            int count = 0;
            try
            {
                foreach (string id in EnumerateInstalledIds())
                    if (FindCatalogUpdate(id, LoadedInfo(id)) != null) count++;
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
            string permsText = module.Permissions == ModulePermissions.None
                ? "no special permissions"
                : "wants: " + PermissionsText(module.Permissions);
            nameStack.Children.Add(new TextBlock { Text = permsText, FontSize = 10, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
            sp.Children.Add(nameStack);

            var install = new Button { Content = "Install", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            install.Click += async delegate { await InstallModuleAsync(module, install); };
            sp.Children.Add(install);

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
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                byte[] bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                    module.Url, module.Sha256, RemoteCatalogClient.MaximumModuleBytes, _netCts.Token);
                if (!IsLoaded) return;

                string installDir = SafeModuleDir(module.Id);
                if (Directory.Exists(installDir)) Directory.Delete(installDir, true);   // clean reinstall/update
                Directory.CreateDirectory(installDir);
                // Awaited: see the update path. A synchronous unpack of a 31 MB module froze the window.
                using (var zipStream = new MemoryStream(bytes))
                    await ZipFile.ExtractToDirectoryAsync(zipStream, installDir, true, _netCts.Token);

                _status.Text = module.Name + " installed.";
                Reload();
                RenderAvailable(DiffNew());
                RestartToApply();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't install " + module.Name + ": " + Short(ex.Message); }
            finally { if (IsLoaded) install.IsEnabled = true; }
        }

        private static string SafeModuleDir(string id)
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
            var choice = System.Windows.MessageBox.Show(
                "DesktopAICompanion needs to restart to apply this change. Restart now?",
                "Restart required",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (choice != System.Windows.MessageBoxResult.Yes)
            {
                _status.Text += " Restart when you're ready to apply it.";
                return;
            }
            Program.RequestRestart("Modules");
            Window ownerWindow = Window.GetWindow(this);
            if (ownerWindow != null) ownerWindow.Close();
            System.Windows.Forms.Application.Exit();
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 200 ? message.Substring(0, 200) + "…" : message;
        }
    }
}
