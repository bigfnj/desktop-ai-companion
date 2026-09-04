using System;
using Screen = System.Windows.Forms.Screen;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;   // Run, Hyperlink (inline clickable size)
using System.Windows.Input;       // Cursors
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopAICompanion.Options;   // CompanionsController, CompanionRow, ICompanionRuntime

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// Host-built Pets gallery for the WPF settings window (S5b-2c): a card per installed pet (thumbnail +
    /// name + Use/Add/Remove + an Active marker), backed by the base <see cref="CompanionsController"/>. A footer
    /// "Check for new companions" button (S5b-2c4) fetches the online catalog, diffs it against the locally present
    /// pets, and offers any new ones as download cards — the same HTTPS-trusted, SHA-256-verified path the
    /// classic Options window used, reused here through <see cref="RemoteCatalogClient"/>. Use/Add apply
    /// immediately through the runtime, so this pane has no separate Apply button.
    /// </summary>
    internal sealed class CompanionsPaneControl : ContentControl
    {
        private readonly CompanionsController _pets;
        private readonly WrapPanel _grid = new WrapPanel { Margin = new Thickness(4) };
        private readonly TextBlock _availableHeader = new TextBlock
        {
            Text = "Available to download",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 10, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        private readonly WrapPanel _availableGrid = new WrapPanel { Margin = new Thickness(4), Visibility = Visibility.Collapsed };
        // A third list, because "available to download" is diffed by ID and so can never surface a pet whose
        // CONTENT changed. Placed above it: an update to something you already use matters more than a pet you
        // have never seen.
        private readonly TextBlock _updatesHeader = new TextBlock
        {
            Text = "Updates available",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 10, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        private readonly WrapPanel _updatesGrid = new WrapPanel { Margin = new Thickness(4), Visibility = Visibility.Collapsed };
        private readonly Button _checkButton = new Button
        {
            // Says "and updates" because it finds both. The pane now also refreshes when it opens, so this
            // button is no longer the only way to reach either -- it is the "check again right now" for
            // someone who has just published something and does not want to wait for the weekly pass.
            Content = "Check for companions and updates",
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 0, 0, 4),
        };
        private readonly Button _importButton = new Button
        {
            Content = "Import Shimeji skin…",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 4),
        };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 4, 0, 6), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

        // The most recent successful catalog fetch, so a download can re-diff locally without re-fetching.
        private RemoteCatalog _lastCatalog;
        private CancellationTokenSource _netCts;

        public CompanionsPaneControl()
        {
            _pets = new CompanionsController(Program.Mainthread as ICompanionRuntime);

            var root = new DockPanel { LastChildFill = true };

            var header = new StackPanel { Margin = new Thickness(4) };
            header.Children.Add(new TextBlock { Text = "Companions", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            header.Children.Add(new TextBlock { Text = "Pick a look for your companion. “Use” replaces the current companion; “Add” spawns one alongside.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            footerButtons.Children.Add(_checkButton);
            footerButtons.Children.Add(_importButton);
            footer.Children.Add(footerButtons);
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scrollContent = new StackPanel();
            scrollContent.Children.Add(_grid);
            scrollContent.Children.Add(_updatesHeader);
            scrollContent.Children.Add(_updatesGrid);
            scrollContent.Children.Add(_availableHeader);
            scrollContent.Children.Add(_availableGrid);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = scrollContent });
            Content = root;

            _checkButton.Click += CheckButton_Click;
            _importButton.Click += ImportShimeji_Click;
            Unloaded += delegate { try { if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); _netCts = null; } } catch { } };

            Reload();
            RefreshCatalogOnOpen();
        }

        /// <summary>
        /// Fetch the catalog when the pane opens, so new pets and updates are already listed.
        ///
        /// This control is rebuilt on every pane selection, so the constructor IS "on open" and _lastCatalog
        /// is always null here -- which is why nothing could appear until the button was pressed. The diff
        /// runs OFF the UI thread: DiffStale hashes every installed catalog pet, which was tolerable behind a
        /// deliberate button press and is not something to pay on every open. Fire-and-forget and
        /// failure-silent: offline should look exactly like it did before, not like an error.
        /// </summary>
        private async void RefreshCatalogOnOpen()
        {
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                CancellationToken token = _netCts.Token;
                RemoteCatalog catalog = await RemoteCatalogClient.FetchSharedAsync(token).ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsLoaded) return;
                List<string> stale = await Task
                    .Run(delegate { return CompanionProvenance.StaleInstalledIds(catalog); }, token)
                    .ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsLoaded) return;
                _lastCatalog = catalog;
                RenderAvailable(DiffNew());
                RenderUpdates(StaleFromIds(catalog, stale));
            }
            catch { }
        }

        /// <summary>
        /// Swap any on-screen copies of this pet onto the definition just written, and describe what
        /// happened in a clause the caller appends to its status line.
        ///
        /// The active pet is a restart rather than a swap, for the reason set out on ReloadPetType: its live
        /// definition lives in settings.json, not the library folder, so nothing short of a restart re-reads
        /// it. Offered, never forced -- the user may have a desktop full of pets they would rather keep.
        /// </summary>
        private string ReloadOnScreen(string id, string display)
        {
            try
            {
                if (Program.Mainthread == null) return "";
                int reloaded;
                string reloadError;
                StartUp.CompanionReloadOutcome outcome = Program.Mainthread.ReloadPetType(id, out reloaded, out reloadError);
                switch (outcome)
                {
                    case StartUp.CompanionReloadOutcome.Reloaded:
                        return reloaded == 1
                            ? " The one on screen was reloaded."
                            : " The " + reloaded + " on screen were reloaded.";
                    case StartUp.CompanionReloadOutcome.NeedsRestart:
                        return " Restart to see the change (this is your default companion).";
                    case StartUp.CompanionReloadOutcome.Deferred:
                        return string.IsNullOrEmpty(reloadError)
                            ? " Companions on screen keep the old version until they respawn."
                            : " Companions on screen keep the old version for now: " + Short(reloadError);
                    default:
                        return "";
                }
            }
            catch (Exception ex)
            {
                // A failed reload must never turn a SUCCESSFUL download into an error: the file is written,
                // the pet is updated on disk, and the worst case is that it takes effect on the next spawn.
                return " Companions on screen keep the old version for now: " + Short(ex.Message);
            }
        }

        /// <summary>Map the ids the background diff produced back to catalog entries, preserving catalog
        /// order so the list does not reshuffle between a cached render and a live one.</summary>
        private static List<CatalogCompanion> StaleFromIds(RemoteCatalog catalog, List<string> ids)
        {
            var result = new List<CatalogCompanion>();
            if (catalog == null || catalog.Pets == null || ids == null || ids.Count == 0) return result;
            var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            foreach (CatalogCompanion pet in catalog.Pets)
                if (pet != null && !string.IsNullOrEmpty(pet.Id) && wanted.Contains(pet.Id)) result.Add(pet);
            return result;
        }

        private void Reload()
        {
            _grid.Children.Clear();
            try
            {
                _pets.Load();
                Dictionary<string, int> mix = BuildMixDict();
                foreach (CompanionRow row in _pets.State.Installed)
                    _grid.Children.Add(BuildCard(row, mix));
            }
            catch (Exception ex) { _status.Text = "Couldn't list companions: " + ex.Message; }
        }

        // Open Companion Studio straight into its Shimeji import flow. Companion Studio owns the converter; the Pets pane
        // only deep-links to it. The module runs in its own load context, so the host cannot cast to
        // PetStudioModule -- find it by id among the loaded modules and invoke its public OpenForImport() by
        // reflection, which keeps the IModule ABI frozen.
        private void ImportShimeji_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IReadOnlyList<DesktopAICompanion.Modules.IModule> modules =
                    Program.Mainthread != null ? Program.Mainthread.LoadedModules : null;
                DesktopAICompanion.Modules.IModule petStudio = null;
                if (modules != null)
                    foreach (DesktopAICompanion.Modules.IModule m in modules)
                        if (m != null && m.Info != null &&
                            string.Equals(m.Info.Id, "petstudio", StringComparison.OrdinalIgnoreCase))
                        { petStudio = m; break; }

                if (petStudio == null)
                {
                    _status.Text = "Companion Studio isn't installed. Add it from Options, Modules to import a Shimeji skin.";
                    return;
                }

                var method = petStudio.GetType().GetMethod(
                    "OpenForImport",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method == null)
                {
                    _status.Text = "This Companion Studio version can't import yet; update it from Options, Modules.";
                    return;
                }
                method.Invoke(petStudio, null);
                _status.Text = "Opening Companion Studio to import a Shimeji skin…";
            }
            catch (Exception ex)
            {
                _status.Text = "Couldn't open the importer: " +
                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

        // The live on-screen mix (id -> count). The active/default type's pets are keyed "" (see StartUp.OnScreenMix).
        private static Dictionary<string, int> BuildMixDict()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var mix = Program.Mainthread != null ? Program.Mainthread.OnScreenMix() : null;
                if (mix != null)
                    foreach (CompanionCountEntry e in mix)
                    {
                        string id = e.Id ?? "";
                        int c; d.TryGetValue(id, out c); d[id] = c + e.Count;
                    }
            }
            catch { }
            return d;
        }

        private FrameworkElement BuildCard(CompanionRow row, Dictionary<string, int> mix)
        {
            string addId = row.IsBuiltIn ? CompanionCatalog.BuiltInPetId : (row.Id ?? "");
            int onScreen = 0, c;
            if (mix.TryGetValue(addId, out c)) onScreen += c;
            int defaultCount = 0;                      // the active type's pets are keyed "" in the mix
            if (row.IsActive && mix.TryGetValue("", out c)) defaultCount = c;
            onScreen += defaultCount;

            var card = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6), Width = 224 };
            var sp = new StackPanel();

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            ImageSource img = LoadThumb(addId);
            if (img == null && row.IsBuiltIn) img = LoadAppIcon();   // the default eSheep isn't in the thumbnail zip
            if (img != null) top.Children.Add(new Image { Source = img, Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0) });
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = row.DisplayName ?? row.Id, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (onScreen > 0)
                nameStack.Children.Add(new TextBlock { Text = (row.IsActive ? "active · " : "") + "on screen: " + onScreen, FontSize = 11, Foreground = Brushes.ForestGreen });
            top.Children.Add(nameStack);
            sp.Children.Add(top);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            if (!row.IsActive)
            {
                var use = new Button { Content = "Use", Width = 48, Margin = new Thickness(0, 0, 5, 0) };
                use.Click += delegate { _status.Text = _pets.UsePet(addId).Ok ? (row.DisplayName + " is now your companion.") : "Couldn't apply that companion."; Reload(); };
                btns.Children.Add(use);
            }
            var add = new Button { Content = "Add", Width = 48, Margin = new Thickness(0, 0, 5, 0) };
            add.Click += delegate { _status.Text = _pets.AddPet(addId).Ok ? ("Added " + row.DisplayName + ".") : "Couldn't add (max companions reached?)."; Reload(); };
            btns.Children.Add(add);
            if (onScreen > 0)
            {
                string removeId = defaultCount > 0 ? "" : addId;   // remove one of this type (active default = "")
                var remove = new Button { Content = "Remove", Width = 66 };
                remove.Click += delegate
                {
                    try { if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(removeId); } catch { }
                    _status.Text = "Removed one " + row.DisplayName + ".";
                    Reload();
                };
                btns.Children.Add(remove);
            }
            // Uninstall: delete an INSTALLED library pet (downloaded / converted / authored). Never offered
            // for the built-in eSheep or the active pet; only when the pet actually lives in the writable
            // library folder. "Remove" above just despawns one instance -- this deletes it for good.
            if (!row.IsActive && !row.IsBuiltIn && LibraryFolderExists(addId))
            {
                var uninstall = new Button { Content = "Uninstall", Width = 78, Margin = new Thickness(5, 0, 0, 0) };
                string display = row.DisplayName ?? row.Id;
                int onScreenCopy = onScreen;
                uninstall.Click += delegate { UninstallPet(addId, display, onScreenCopy); };
                btns.Children.Add(uninstall);
            }
            sp.Children.Add(btns);

            // Description (a unique quip) + animation/sound counts.
            sp.Children.Add(new TextBlock
            {
                Text = CompanionBlurbs.For(addId),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
            sp.Children.Add(BuildStatsLine(addId, row.DisplayName ?? row.Id, GetStats(addId)));
            sp.Children.Add(BuildSizeRow(addId, row.DisplayName ?? row.Id));
            // Null on a single-monitor machine, where the control would offer one choice and change nothing.
            FrameworkElement monitorRow = BuildMonitorRow(addId, row.DisplayName ?? row.Id);
            if (monitorRow != null) sp.Children.Add(monitorRow);

            card.Child = sp;
            return card;
        }

        // The stats line ("N animations · M sounds") plus an inline per-pet sound on/off toggle for pets that
        // have sounds. Size is its own slider row (BuildSizeRow), no longer part of this line.
        private FrameworkElement BuildStatsLine(string addId, string displayName, CompanionStats stats)
        {
            var line = new TextBlock
            {
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            string prefix = stats.Animations + (stats.Animations == 1 ? " animation" : " animations");
            if (stats.Sounds > 0) prefix += "  ·  " + stats.Sounds + (stats.Sounds == 1 ? " sound" : " sounds");
            line.Inlines.Add(new Run(prefix));   // size is its own slider row below (see BuildSizeRow)

            // Per-pet sound toggle (only for pets that have sounds): an inline clickable "sound on/off",
            // same style as the size number. Takes effect on the next sound (the host checks it at play
            // time), no restage. Keyed by the same id, so it works on this pet type wherever it's on screen.
            if (stats.Sounds > 0)
            {
                line.Inlines.Add(new Run("  ·  "));
                bool enabled = true;
                try { if (Program.MyData != null) enabled = Program.MyData.IsPetSoundEnabled(addId); } catch { }
                var soundRun = new Run(enabled ? "sound on" : "sound off");
                var soundLink = new Hyperlink(soundRun)
                {
                    Foreground = Brushes.Gray,
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    ToolTip = "click to mute / unmute this pet's sounds",
                };
                soundLink.Click += delegate
                {
                    enabled = !enabled;
                    soundRun.Text = enabled ? "sound on" : "sound off";
                    try { if (Program.Mainthread != null) Program.Mainthread.SetPetSound(addId, enabled); } catch { }
                    _status.Text = displayName + (enabled ? " sounds on." : " sounds muted.");
                };
                line.Inlines.Add(soundLink);
            }

            return line;
        }

        // A per-pet size slider (25%..400%, snapping to 25% steps). Persisted immediately; like the old size
        // control it is baked in when the pet is next staged, so it applies the next time this pet is Added (or
        // on restart) -- pets of this type already on screen keep their size until then. Seeds from the pet's
        // effective size percent (its own override, else the global size).
        private FrameworkElement BuildSizeRow(string addId, string displayName)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = "size", FontSize = 10, Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });

            int startPercent = 100;
            try { if (Program.Mainthread != null) startPercent = Program.Mainthread.GetPetScalePercent(addId); } catch { }
            startPercent = Math.Max(25, Math.Min(400, startPercent));

            var slider = new Slider
            {
                Minimum = 25, Maximum = 400, Value = startPercent,
                TickFrequency = 25, IsSnapToTickEnabled = true,
                SmallChange = 25, LargeChange = 50,
                Width = 130, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "drag to resize this companion (25% to 400%); applies the next time you Add it",
            };
            var readout = new TextBlock
            {
                Text = startPercent + "%", FontSize = 10, Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), MinWidth = 34,
            };

            slider.ValueChanged += delegate
            {
                int pct = (int)Math.Round(slider.Value);
                readout.Text = pct + "%";
                try { if (Program.Mainthread != null) Program.Mainthread.SetPetScalePercent(addId, pct); } catch { }
                _status.Text = displayName + " size " + pct + "%. Add " + displayName + " (or restart) to see it.";
            };

            row.Children.Add(slider);
            row.Children.Add(readout);
            return row;
        }

        /// <summary>
        /// Pin this pet type to one monitor, or leave it free to use any.
        ///
        /// Only shown with more than one screen attached: on a single monitor the control would offer a
        /// choice with one option and an outcome identical to the default.
        ///
        /// A pin is stronger than "Allow multiple screens" on purpose. That setting only decides whether an
        /// UNPINNED pet spawns on a random screen; naming a monitor here is an explicit instruction, and a
        /// pinned pet HIDES rather than moving when a fullscreen app takes its screen.
        /// </summary>
        private FrameworkElement BuildMonitorRow(string addId, string displayName)
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length < 2) return null;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = "screen", FontSize = 10, Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });

            var box = new ComboBox { Width = 168, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            box.Items.Add("Any screen");
            for (int i = 0; i < screens.Length; i++)
                box.Items.Add(string.Format(
                    CultureInfo.InvariantCulture, "{0}{1} — {2}x{3}",
                    i + 1,
                    screens[i].Primary ? " (main)" : "",
                    screens[i].Bounds.Width, screens[i].Bounds.Height));

            int pinned = -1;
            try { if (Program.MyData != null) pinned = Program.MyData.GetPetMonitor(addId, screens.Length); } catch { }
            box.SelectedIndex = pinned >= 0 ? pinned + 1 : 0;   // index 0 is "Any screen"
            box.ToolTip = "pin " + displayName + " to one screen; it will not wander or be moved off it";

            box.SelectionChanged += delegate
            {
                int choice = box.SelectedIndex - 1;             // -1 == Any
                try { if (Program.MyData != null) Program.MyData.SetPetMonitor(addId, choice); } catch { }
                _status.Text = choice < 0
                    ? displayName + " can use any screen again. Add " + displayName + " (or restart) to apply."
                    : displayName + " is pinned to screen " + (choice + 1) + ". Add " + displayName +
                      " (or restart) to apply; it will hide rather than move if a fullscreen app takes that screen.";
            };

            row.Children.Add(box);
            return row;
        }
        // ---- Check for new pets (online catalog) --------------------------------

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            _checkButton.IsEnabled = false;
            _status.Text = "Checking for companions online…";
            try
            {
                if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); }
                _netCts = new CancellationTokenSource();
                // Pressing the button is an explicit "check NOW", so drop the shared copy first:
                // reusing an answer from seconds ago would make the button look like it did nothing.
                RemoteCatalogClient.InvalidateShared();
                _lastCatalog = await RemoteCatalogClient.FetchAsync(_netCts.Token);
                if (!IsLoaded) return;
                List<CatalogCompanion> newPets = DiffNew();
                List<CatalogCompanion> stalePets = DiffStale();
                RenderAvailable(newPets);
                RenderUpdates(stalePets);
                // Both counts, and never the bare "you already have every available companion" while an update is
                // waiting: that exact sentence is what told users everything was current for as long as this
                // pane diffed by ID alone.
                var parts = new List<string>();
                if (stalePets.Count > 0)
                    parts.Add(stalePets.Count + (stalePets.Count == 1 ? " companion has" : " companions have") + " an update");
                if (newPets.Count > 0)
                    parts.Add(newPets.Count + (newPets.Count == 1 ? " new companion" : " new companions") + " available to download");
                _status.Text = parts.Count > 0
                    ? (string.Join(", ", parts.ToArray()) + ".")
                    : "Every companion you have is up to date, and you already have all of them.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't reach the catalog: " + Short(ex.Message); }
            finally { if (IsLoaded) _checkButton.IsEnabled = true; }
        }

        // Catalog pets that are not already present locally (bundled or downloaded).
        private List<CatalogCompanion> DiffNew()
        {
            var result = new List<CatalogCompanion>();
            if (_lastCatalog == null) return result;
            HashSet<string> local = LocalPetIds();
            foreach (CatalogCompanion pet in _lastCatalog.Pets)
                if (!local.Contains(pet.Id)) result.Add(pet);
            return result;
        }

        private void RenderAvailable(List<CatalogCompanion> pets)
        {
            _availableGrid.Children.Clear();
            bool any = pets.Count > 0;
            _availableHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _availableGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            foreach (CatalogCompanion pet in pets)
                _availableGrid.Children.Add(BuildDownloadCard(pet));
        }

        private FrameworkElement BuildDownloadCard(CatalogCompanion pet)
        {
            var card = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6), Width = 224 };
            var sp = new StackPanel();

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            ImageSource img = LoadThumb(pet.Id);
            if (img != null) top.Children.Add(new Image { Source = img, Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0) });
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = CompanionCatalog.DisplayName(pet.Id, pet.Name), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(pet.Author))
                nameStack.Children.Add(new TextBlock { Text = "by " + pet.Author, FontSize = 11, Foreground = Brushes.Gray });
            top.Children.Add(nameStack);
            sp.Children.Add(top);

            var dl = new Button { Content = "Download", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            dl.Click += async delegate { await DownloadPetAsync(pet, dl); };
            sp.Children.Add(dl);

            // Same blurb the installed card shows (BuildPetCard), so the gallery reads identically before and
            // after a download. Keyed by catalog id, which is the same id on both sides, and CompanionBlurbs falls
            // back to a generic line for an id it does not know.
            sp.Children.Add(new TextBlock
            {
                Text = CompanionBlurbs.For(pet.Id),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
            if (pet.Bytes > 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = FormatBytes(pet.Bytes) + " download",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }

            card.Child = sp;
            return card;
        }

        // Download size for the catalog cards. Deliberately coarse: this is a "how big is this" hint before
        // committing to a download, not an accounting figure.
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        private async Task DownloadPetAsync(CatalogCompanion pet, Button dl)
        {
            await FetchPetAsync(pet, dl, false);
        }

        /// <summary>
        /// Download a catalog pet over whatever is (or is not) already there.
        ///
        /// One method for install and update on purpose: an update IS a download to the same path, and the two
        /// differing would be two places that have to validate, contain the path and stamp provenance. The only
        /// difference is the wording and the confirmation.
        /// </summary>
        private async Task FetchPetAsync(CatalogCompanion pet, Button trigger, bool isUpdate)
        {
            if (pet == null) return;
            string display = CompanionCatalog.DisplayName(pet.Id, pet.Name);

            if (isUpdate)
            {
                // Only ask when there is something to lose. Classify decides that, not this method, so the
                // prompt cannot disagree with the badge the card is showing.
                CompanionFreshness freshness = FreshnessOf(pet);
                if (CompanionProvenance.UpdateWouldDiscardChanges(freshness) &&
                    MessageBox.Show(
                        "Update “" + display + "”?\n\n" + CompanionProvenance.Describe(freshness),
                        "Update companion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            if (trigger != null) trigger.IsEnabled = false;
            _status.Text = (isUpdate ? "Updating " : "Downloading ") + display + "…";
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                byte[] bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                    pet.Url, pet.Sha256, CompanionXmlValidator.MaximumXmlBytes, _netCts.Token);
                if (!IsLoaded) return;

                // A downloaded file is never trusted blindly: validate structure before it lands on disk.
                string xml = SecureDownload.DecodeUtf8(bytes);
                XmlData.RootNode parsed;
                string validationError;
                if (!CompanionXmlValidator.TryParse(xml, out parsed, out validationError))
                {
                    _status.Text = display + " failed validation: " + Short(validationError);
                    return;
                }

                string directory = SafeLibraryDir(pet.Id);
                Directory.CreateDirectory(directory);
                SecureDownload.WriteAllBytesAtomic(Path.Combine(directory, "animations.xml"), bytes);
                // Record what was installed, so a LATER catalog change can be told apart from a local edit.
                // Written from the same bytes the hash was verified against, not by re-reading the file.
                CompanionProvenance.WriteStamp(directory, CompanionProvenance.HashBytes(bytes));

                // The file on disk is now a DIFFERENT pet, so every per-id cache keyed off it is wrong.
                // Both are process-lifetime and neither expires, so this is the one place that can know.
                CompanionCatalog.Forget(pet.Id);
                ForgetStats(pet.Id);

                // An update to a pet that is ON SCREEN should take effect now, not "next time you respawn
                // it". Before this the user had to remove and re-add the pet by hand, and even that did not
                // work: the type registry caches the parse, so a manual remove-then-add brought the OLD
                // skin back. See StartUp.ReloadPetType.
                string outcome = "";
                if (isUpdate) outcome = ReloadOnScreen(pet.Id, display);

                _status.Text = isUpdate
                    ? ("Updated " + display + "." + outcome)
                    : ("Added " + display + " to your companions.");
                Reload();                        // the new pet is now a local card
                RenderAvailable(DiffNew());      // re-diff against the cached catalog (no re-fetch)
                RenderUpdates(DiffStale());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't " + (isUpdate ? "update " : "download ") + display + ": " + Short(ex.Message); }
            finally { if (IsLoaded && trigger != null) trigger.IsEnabled = true; }
        }

        /// <summary>How the installed copy of a catalog pet compares to the catalog. Delegates to
        /// CompanionProvenance so this pane and the weekly background check cannot disagree about what "stale"
        /// means -- two implementations of that is how a badge and a notification drift apart.</summary>
        private static CompanionFreshness FreshnessOf(CatalogCompanion pet)
        {
            if (pet == null) return CompanionFreshness.NotInstalled;
            return CompanionProvenance.FreshnessOfInstalled(pet.Id, pet.Sha256);
        }

        /// <summary>
        /// Catalog pets whose installed copy is no longer the catalog's.
        ///
        /// This is the whole point of the pane's third list. Before it, "Check for new companions" diffed by ID
        /// alone, so a pet you already had was filtered out however much its content had changed -- a
        /// corrected pet reached new downloads only, and an existing user kept the old one for ever with the
        /// pane cheerfully reporting "You already have every available companion".
        ///
        /// Only the writable library is considered. A BUNDLED pet ships inside the app and is replaced by an
        /// app update, not by this.
        /// </summary>
        private List<CatalogCompanion> DiffStale()
        {
            var result = new List<CatalogCompanion>();
            if (_lastCatalog == null) return result;
            foreach (CatalogCompanion pet in _lastCatalog.Pets)
                if (LibraryFolderExists(pet.Id) && CompanionProvenance.IsStale(FreshnessOf(pet)))
                    result.Add(pet);
            return result;
        }

        private void RenderUpdates(List<CatalogCompanion> pets)
        {
            _updatesGrid.Children.Clear();
            bool any = pets.Count > 0;
            _updatesHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _updatesGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            foreach (CatalogCompanion pet in pets)
                _updatesGrid.Children.Add(BuildUpdateCard(pet));
        }

        private FrameworkElement BuildUpdateCard(CatalogCompanion pet)
        {
            CompanionFreshness freshness = FreshnessOf(pet);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = CompanionCatalog.DisplayName(pet.Id, pet.Name),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            sp.Children.Add(new TextBlock
            {
                Text = CompanionProvenance.Describe(freshness),
                TextWrapping = TextWrapping.Wrap,
                Foreground = CompanionProvenance.UpdateWouldDiscardChanges(freshness) ? Brushes.OrangeRed : Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var button = new Button
            {
                Content = "Update",
                Width = 90,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
            };
            button.Click += async delegate { await FetchPetAsync(pet, button, true); };
            sp.Children.Add(button);
            return new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4),
                Padding = new Thickness(6),
                Width = 224,
                Child = sp,
            };
        }

        private static HashSet<string> LocalPetIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CompanionCatalog.CompanionInfo info in CompanionCatalog.EnumerateLocal())
                if (!info.IsBuiltIn && !string.IsNullOrEmpty(info.Id)) ids.Add(info.Id);
            return ids;
        }

        private static string SafeLibraryDir(string id)
        {
            if (!SecureDownload.IsSafeId(id)) throw new InvalidDataException("Unsafe companion id.");
            string root = Path.GetFullPath(AppPaths.LibraryPetsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(root, id));
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Companion path escapes the library.");
            return directory;
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 200 ? message.Substring(0, 200) + "…" : message;
        }

        // Animation + sound counts read from the pet's XML, cached per id (the sheep XMLs are large).
        private sealed class CompanionStats { public int Animations; public int Sounds; }
        private static readonly Dictionary<string, CompanionStats> _statsCache = new Dictionary<string, CompanionStats>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Forget one pet's cached counts, because its animations.xml has just been rewritten. Paired with
        /// <see cref="CompanionCatalog.Forget"/>: both caches are process-lifetime and keyed by id, so an in-process
        /// update leaves the card showing the OLD pet's name and the OLD animation count without them.
        /// </summary>
        internal static void ForgetStats(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_statsCache) { _statsCache.Remove(id); }
        }
        private static CompanionStats GetStats(string id)
        {
            lock (_statsCache) { CompanionStats hit; if (_statsCache.TryGetValue(id, out hit)) return hit; }
            var s = new CompanionStats();
            try
            {
                string xml, err;
                if (CompanionCatalog.TryReadPetXml(id, out xml, out err) && !string.IsNullOrEmpty(xml))
                {
                    s.Animations = System.Text.RegularExpressions.Regex.Matches(xml, "<animation\\s").Count;
                    s.Sounds = System.Text.RegularExpressions.Regex.Matches(xml, "<sound\\b").Count;
                }
            }
            catch { }
            lock (_statsCache) { _statsCache[id] = s; }
            return s;
        }

        private static ImageSource LoadThumb(string id)
        {
            try
            {
                byte[] png = CompanionThumbnails.GetPng(id);
                if (png != null) return FromPng(png);
                // No bundled thumbnail: installed / converted / authored pets aren't in the zip. Fall back to
                // the pet's OWN header icon so its gallery card isn't blank (every animations.xml carries one).
                return LoadPetHeaderIcon(id);
            }
            catch { return null; }
        }

        /// <summary>Decode the &lt;header&gt;&lt;icon&gt; ICO from an installed or bundled pet's animations.xml.
        /// WPF's decoder handles the PNG-in-ICO the Shimeji importer emits as well as ordinary icons; returns
        /// null when there is no such pet folder or no icon.</summary>
        private static ImageSource LoadPetHeaderIcon(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            string xmlPath = FindPetXml(id);
            if (xmlPath == null) return null;
            try
            {
                // Parse from decoded text, not XDocument.Load(path): a converted pet's prolog may declare an
                // encoding that disagrees with its actual bytes (older imports stamped encoding="utf-16" onto a
                // UTF-8 file), and Load honours that declaration and throws. File.ReadAllText detects the real
                // encoding from any BOM (UTF-8 otherwise) and Parse ignores the prolog -- exactly how the app
                // loads pets everywhere else.
                XElement icon = XDocument.Parse(File.ReadAllText(xmlPath)).Descendants().FirstOrDefault(e => e.Name.LocalName == "icon");
                if (icon == null || string.IsNullOrWhiteSpace(icon.Value)) return null;
                byte[] ico = Convert.FromBase64String(icon.Value.Trim());
                using (var ms = new MemoryStream(ico, false))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return null;
                    BitmapFrame frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
            catch { return null; }
        }

        private static string FindPetXml(string id)
        {
            foreach (string dir in new[] { AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory })
            {
                try
                {
                    string p = Path.Combine(dir, id, "animations.xml");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        // Only a pet that actually lives in the writable library can be uninstalled (deleted). Built-in and
        // bundled pets ship with the app and are not the user's to remove.
        private static bool LibraryFolderExists(string id)
        {
            try { return !string.IsNullOrEmpty(id) && File.Exists(Path.Combine(AppPaths.LibraryPetsDirectory, id, "animations.xml")); }
            catch { return false; }
        }

        private void UninstallPet(string id, string name, int onScreen)
        {
            if (MessageBox.Show(
                    "Uninstall “" + name + "”? This deletes it from your companion library.",
                    "Uninstall companion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                // Contain the delete strictly inside the library so a stray id can never escape it.
                string root = Path.GetFullPath(AppPaths.LibraryPetsDirectory);
                string dir = Path.GetFullPath(Path.Combine(root, id ?? ""));
                if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _status.Text = "Refused: that companion id is not inside the library.";
                    return;
                }
                for (int i = 0; i < onScreen; i++)
                    try { if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(id); } catch { }
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                _status.Text = "Uninstalled " + name + ".";
            }
            catch (Exception ex) { _status.Text = "Couldn't uninstall: " + ex.Message; }
            Reload();
            // Mirror the download path: an uninstalled catalog pet (one that isn't also bundled) re-appears
            // under "available for download" immediately, instead of only after the next "Check for new companions".
            RenderAvailable(DiffNew());
        }

        /// <summary>Fallback thumbnail for the built-in eSheep (not present in the thumbnail zip): the app icon.</summary>
        private static ImageSource LoadAppIcon()
        {
            try
            {
                using (var bmp = DesktopAICompanion.Properties.Resources.icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return FromPng(ms.ToArray());
                }
            }
            catch { return null; }
        }

        private static ImageSource FromPng(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png, false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
