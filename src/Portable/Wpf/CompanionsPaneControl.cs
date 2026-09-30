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
using System.Windows.Controls.Primitives;   // Thumb.DragStarted/DragCompleted (size-slider write coalescing)
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
    internal sealed class CompanionsPaneControl : ContentControl, IBusyPane
    {
        /// <summary>Downloads in flight (FetchPetAsync). Read by the shell through <see cref="IBusyPane"/>, so
        /// a redirect by title is refused while one runs instead of cancelling it with the pane (RA-328).</summary>
        private int _downloadsInFlight;
        public bool IsBusy { get { return _downloadsInFlight > 0; } }

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
                List<StalePet> stale = await Task
                    .Run(delegate { return DiffStale(catalog); }, token)
                    .ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsLoaded) return;
                _lastCatalog = catalog;
                // Says it landed; see the Modules pane for why an invisible fetch was the problem.
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info,
                    "[module] companions pane: catalog in hand on open");
                RenderAvailable(DiffNew());
                RenderUpdates(stale);
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
                            : " Companions on screen keep the old version for now: " + PaneText.Short(reloadError);
                    default:
                        return "";
                }
            }
            catch (Exception ex)
            {
                // A failed reload must never turn a SUCCESSFUL download into an error: the file is written,
                // the pet is updated on disk, and the worst case is that it takes effect on the next spawn.
                return " Companions on screen keep the old version for now: " + PaneText.Short(ex.Message);
            }
        }

        // StaleFromIds was removed 2026-09-17. It mapped ids back to catalog entries, which was only
        // needed because the off-thread diff returned ids and threw its classifications away --
        // leaving each card to hash the pet again, on the UI thread. DiffStale now returns both.

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
            if (img == null && row.IsBuiltIn) img = LoadAppIconCached();   // the default eSheep isn't in the thumbnail zip
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
            if (!row.IsActive && !row.IsBuiltIn && CompanionProvenance.IsInLibrary(addId))
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
                    bool wanted = !enabled;
                    try { if (Program.Mainthread != null) Program.Mainthread.SetPetSound(addId, wanted); } catch { }
                    // Read back: IsPetSoundEnabled is the same question the toggle asked above.
                    bool stored = wanted;
                    try { if (Program.MyData != null) stored = Program.MyData.IsPetSoundEnabled(addId); } catch { }
                    // The link follows the STORE, not the click (F363). It used to flip before the write
                    // and stay flipped when the write failed, so the card read "sound off" for a pet that
                    // kept making sounds, the status line saying "unchanged" beside it was the transient
                    // one, and the next click "changed" it back to the value it had never left.
                    enabled = stored;
                    soundRun.Text = enabled ? "sound on" : "sound off";
                    _status.Text = stored != wanted
                        ? "Couldn't save the sound setting for " + displayName + "; it is unchanged."
                        : displayName + (enabled ? " sounds on." : " sounds muted.");
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

            // ONE write per drag, not one per tick.
            //
            // settings.json embeds the active pet's animations.xml as base64, so on this box the real
            // document is 1,167,948 bytes. SetPetScalePercent persists synchronously on the UI thread
            // through AtomicFile (WriteThrough + Flush(true) + File.Replace), measured at a median
            // 129.7 ms for the durable-write half alone. The slider snaps every 25 from 25 to 400, so
            // one drag across the range crossed 15 positions: roughly two seconds of frozen UI and
            // ~35 MB of write-through traffic for a single gesture.
            //
            // Nothing visual depends on the write. The tooltip says it plainly -- the size "applies the
            // next time you Add it" -- so the readout and the status line can track the thumb live
            // while the STORE hears about it once, when the drag ends.
            bool dragging = false;
            int pendingPercent = startPercent;
            // What the last persist read back: true until a write is seen to fail (F364).
            bool storeTookIt = true;
            // True while persistPending moves the thumb back to the stored value, so the ValueChanged that
            // programmatic move raises is not taken for a new request and re-run (F363).
            bool reverting = false;
            Action persistPending = delegate
            {
                try { if (Program.Mainthread != null) Program.Mainthread.SetPetScalePercent(addId, pendingPercent); }
                catch { }
                // Announced only if it actually landed. The status line below this row says "size
                // N%" from the value the user dragged to, which is a claim about the STORE, and the
                // setter's return was discarded. GetEffectivePetScalePercent answers the same
                // question the setter was asked.
                int storedPercent = pendingPercent;
                try
                {
                    if (Program.MyData != null)
                        storedPercent = Program.MyData.GetEffectivePetScalePercent(addId);
                }
                catch { }
                storeTookIt = storedPercent == pendingPercent;
                if (storeTookIt) return;
                _status.Text = "Couldn't save the size for " + displayName + "; it is unchanged.";
                // ...and the thumb and readout follow the store too (F363): a slider left at the value
                // that was not saved is a control disagreeing with the status line beside it, and the
                // status line is the transient one.
                reverting = true;
                try
                {
                    pendingPercent = storedPercent;
                    slider.Value = storedPercent;
                    readout.Text = storedPercent + "%";
                }
                finally { reverting = false; }
            };

            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate { dragging = true; }));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate
            {
                dragging = false;
                persistPending();
            }));
            // A pane rebuilt mid-drag would otherwise drop the value the user was choosing: the pane is
            // reconstructed on every selection and after every button press, and DragCompleted never
            // arrives for a control that has gone away.
            slider.Unloaded += delegate { if (dragging) { dragging = false; persistPending(); } };

            slider.ValueChanged += delegate
            {
                if (reverting) return;
                int pct = (int)Math.Round(slider.Value);
                pendingPercent = pct;
                readout.Text = pct + "%";
                storeTookIt = true;
                // Keyboard, a click on the track, or a programmatic set: no drag is in progress, so
                // there is nothing to coalesce and the old immediate behaviour is exactly right.
                if (!dragging) persistPending();
                // Only when the store took it (F364). This line is a claim about the STORE, and on the
                // keyboard and track-click path it was written one statement after persistPending's
                // "Couldn't save the size", so the read-back's verdict lived for exactly one statement
                // and a success-shaped sentence replaced it. The drag path was never affected: it
                // persists from DragCompleted, after the last ValueChanged.
                if (!storeTookIt) return;
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

            // True while the failure branch below puts the combo back on the stored screen, so the
            // SelectionChanged that raises is not taken for a new choice and written again (F363).
            bool syncingBox = false;
            box.SelectionChanged += delegate
            {
                if (syncingBox) return;
                int choice = box.SelectedIndex - 1;             // -1 == Any
                // READ BACK, do not echo the input. The setter returns false for a failed durable
                // write AND rolls the in-memory value back with it, and that bool was discarded --
                // so with the store read-only, or holding a future-schema document, this line
                // announced a pin that had not happened. The setter's false is ambiguous on its own
                // ("no change" looks the same), which is why the check is a read rather than the bool.
                try { if (Program.MyData != null) Program.MyData.SetPetMonitor(addId, choice); } catch { }
                int storedChoice = choice;
                try
                {
                    if (Program.MyData != null)
                        storedChoice = Program.MyData.GetPetMonitor(addId, System.Windows.Forms.Screen.AllScreens.Length);
                }
                catch { }
                if (storedChoice != choice)
                {
                    _status.Text = "Couldn't save the screen for " + displayName + "; it is unchanged.";
                    // The combo follows the store (F363): it showed the pin that did not take.
                    syncingBox = true;
                    try { box.SelectedIndex = storedChoice >= 0 ? storedChoice + 1 : 0; }
                    finally { syncingBox = false; }
                }
                else
                {
                    _status.Text = choice < 0
                        ? displayName + " can use any screen again. Add " + displayName + " (or restart) to apply."
                        : displayName + " is pinned to screen " + (choice + 1) + ". Add " + displayName +
                          " (or restart) to apply; it will hide rather than move if a fullscreen app takes that screen.";
                }
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
                // Pressing the button is an explicit "check NOW", so the shared copy is dropped and
                // REFILLED (F286): reusing an answer from seconds ago would make the button look like it
                // did nothing, and dropping it without refilling made the next pane fetch again.
                _lastCatalog = await RemoteCatalogClient.RefreshSharedAsync(_netCts.Token);
                if (!IsLoaded) return;
                List<CatalogCompanion> newPets = DiffNew();
                // Off the UI thread, for the reason RefreshCatalogOnOpen gives: this SHA-256s every
                // installed catalog companion. A deliberate button press made it tolerable, not
                // free, and the window froze for the duration on a large library.
                RemoteCatalog fetched = _lastCatalog;
                List<StalePet> stalePets = await Task
                    .Run(delegate { return DiffStale(fetched); }, _netCts.Token)
                    .ConfigureAwait(true);
                if (!IsLoaded) return;
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
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't reach the catalog: " + PaneText.Short(ex.Message); }
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
            // WHAT IT CONTAINS, before you commit to downloading it. An installed card has carried
            // "N animations  ·  M sounds" since the gallery was built, and an available card showed only a
            // size, so the one number a user actually chooses on was the one missing from the cards they
            // were choosing between. The app cannot count this for itself here: GetStats reads the installed
            // animations.xml, which is the file that has not been downloaded yet, so the counts ride in the
            // catalog and are produced by New-ContentCatalog with the same two patterns GetStats uses.
            //
            // No sound TOGGLE, unlike the installed card: that preference is per installed pet and there is
            // nothing yet to apply it to. Absent counts mean an older catalog, and then this renders the
            // size line by itself exactly as before.
            string contains = "";
            if (pet.Animations > 0)
            {
                contains = pet.Animations + (pet.Animations == 1 ? " animation" : " animations");
                if (pet.Sounds > 0)
                    contains += "  ·  " + pet.Sounds + (pet.Sounds == 1 ? " sound" : " sounds");
            }
            if (contains.Length > 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = contains,
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
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
            _downloadsInFlight++;
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
                    _status.Text = display + " failed validation: " + PaneText.Short(validationError);
                    return;
                }

                string directory = CompanionProvenance.SafeLibraryDirectory(pet.Id);
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
                RemoteCatalog cached = _lastCatalog;
                List<StalePet> restale = await Task
                    .Run(delegate { return DiffStale(cached); }).ConfigureAwait(true);
                if (!IsLoaded) return;
                RenderUpdates(restale);
            }
            // Said out loud, as the Modules pane's install already does (F366). The Check button cancels
            // this token, so a silent swallow left the status line reading whatever the check wrote, the
            // button re-enabled, and the user never told the download had been stopped.
            catch (OperationCanceledException)
            {
                if (IsLoaded) _status.Text = "Stopped " + (isUpdate ? "updating " : "downloading ") + display + ".";
            }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't " + (isUpdate ? "update " : "download ") + display + ": " + PaneText.Short(ex.Message); }
            finally
            {
                _downloadsInFlight--;
                if (IsLoaded && trigger != null) trigger.IsEnabled = true;
            }
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
        /// <summary>A stale companion together with the classification that made it stale, so the
        /// card can describe it without hashing the file a second time.</summary>
        private struct StalePet
        {
            public CatalogCompanion Pet;
            public CompanionFreshness Freshness;
        }

        /// <summary>
        /// Takes the catalog as a PARAMETER rather than reading _lastCatalog, because every caller
        /// now runs this on a thread-pool thread: a field the UI thread can reassign mid-hash would
        /// be a race, and passing the snapshot the caller already has removes it.
        /// </summary>
        private static List<StalePet> DiffStale(RemoteCatalog catalog)
        {
            var result = new List<StalePet>();
            if (catalog == null || catalog.Pets == null) return result;
            Dictionary<string, CompanionFreshness> stale = CompanionProvenance.StaleInstalled(catalog);
            foreach (CatalogCompanion pet in catalog.Pets)
            {
                CompanionFreshness freshness;
                if (pet == null || string.IsNullOrEmpty(pet.Id)) continue;
                if (!stale.TryGetValue(pet.Id, out freshness)) continue;
                result.Add(new StalePet { Pet = pet, Freshness = freshness });
            }
            return result;
        }

        private void RenderUpdates(List<StalePet> pets)
        {
            _updatesGrid.Children.Clear();
            bool any = pets.Count > 0;
            _updatesHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _updatesGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            foreach (StalePet entry in pets)
                _updatesGrid.Children.Add(BuildUpdateCard(entry));
        }

        private FrameworkElement BuildUpdateCard(StalePet entry)
        {
            // The freshness travels with the entry. This used to call FreshnessOf(pet), which
            // re-hashed a pet DiffStale had just classified -- pure duplicated I/O, once per card,
            // on the UI thread.
            CatalogCompanion pet = entry.Pet;
            CompanionFreshness freshness = entry.Freshness;
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

        // EnumerateLocalIds, not EnumerateLocal: this method throws every DisplayName away, and
        // producing them costs a file open and a 32768-char decoded read per installed companion --
        // ~54 opens and ~1.7 MB over the shipped corpus, on the UI thread. Called from pane open, the
        // Check button, after every download and after every uninstall.
        private static HashSet<string> LocalPetIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in CompanionCatalog.EnumerateLocalIds()) ids.Add(id);
            return ids;
        }

        // SafeLibraryDir and Short used to sit here. The containment check is CompanionProvenance.SafeLibraryDirectory
        // (one copy for this pane and CompanionHost) and the status-text trim is PaneText.Short, shared with the
        // Modules pane (F337).

        // Animation + sound counts read from the pet's XML, cached per id (the sheep XMLs are large).
        private sealed class CompanionStats { public int Animations; public int Sounds; }
        private static readonly Dictionary<string, CompanionStats> _statsCache = new Dictionary<string, CompanionStats>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Header icons, cached for the same reason and with the same lifetime as <see cref="_statsCache"/>
        /// beside it.
        ///
        /// CompanionThumbnails.GetPng caches the bundled zip, but its MISS path did not, and every imported
        /// Shimeji skin, converted pet and locally authored pet misses. Each miss cost a File.ReadAllText
        /// plus a full XDocument.Parse of animations.xml -- 406 KB for hornet, 158 KB for esheep64 -- on the
        /// UI thread, once per CARD, and Reload() runs from the constructor and after every
        /// Use/Add/Remove/Download/Uninstall, with the control rebuilt on every pane selection.
        ///
        /// MISSES ARE CACHED TOO (a null value is a real entry). A pet whose XML carries no icon, or whose
        /// folder is gone, would otherwise re-parse its whole animations.xml on every rebuild forever --
        /// the expensive case, cached for nothing.
        /// </summary>
        private static readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pane's caches follow the catalog's Forget: any writer that rewrites or deletes a pet
        /// file calls CompanionCatalog.Forget, and this is what makes that one call reach the stats and icon
        /// caches too, without src/dotNet having to know about a WPF class (F336).</summary>
        static CompanionsPaneControl()
        {
            CompanionCatalog.Forgotten += ForgetStats;
        }

        /// <summary>
        /// Forget one pet's cached counts, because its animations.xml has just been rewritten. Paired with
        /// <see cref="CompanionCatalog.Forget"/>: both caches are process-lifetime and keyed by id, so an in-process
        /// update leaves the card showing the OLD pet's name and the OLD animation count without them.
        /// </summary>
        internal static void ForgetStats(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_statsCache) { _statsCache.Remove(id); }
            // The icon comes out of the same rewritten animations.xml, so it goes stale at exactly the same
            // moment. Forgetting one without the other would leave the card showing the new name and counts
            // beside the old picture.
            lock (_iconCache) { _iconCache.Remove(id); }
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
            if (string.IsNullOrWhiteSpace(id)) return null;
            // CACHED HERE, not one level down. The first version of this cache sat on the header-icon
            // read (ReadPetHeaderIcon), which is only the MISS path -- so the common case, a bundled
            // thumbnail out of the zip, still cloned up to 256 KB of PNG bytes and ran a full
            // BitmapImage decode per card, per rebuild, on the UI thread. That is once per installed
            // companion (56 of them ship) every time the pane is selected and after every Use, Add,
            // Remove, Download and Uninstall.
            //
            // Both sources produce a FROZEN ImageSource, so one instance is safe to hand to every
            // card, and ForgetStats already drops this entry when a pet's files are rewritten.
            lock (_iconCache)
            {
                ImageSource hit;
                if (_iconCache.TryGetValue(id, out hit)) return hit;
            }
            ImageSource thumb = ReadThumb(id);
            lock (_iconCache) { _iconCache[id] = thumb; }
            return thumb;
        }

        /// <summary>The uncached read, bundled thumbnail first. Separated so the caching above has
        /// exactly one thing to cache.</summary>
        private static ImageSource ReadThumb(string id)
        {
            try
            {
                byte[] png = CompanionThumbnails.GetPng(id);
                if (png != null) return FromPng(png);
                // No bundled thumbnail: installed / converted / authored pets aren't in the zip. Fall back to
                // the pet's OWN header icon so its gallery card isn't blank (every animations.xml carries one).
                // Called directly: a LoadPetHeaderIcon pass-through sat between the two until 2026-09-30,
                // repeating LoadThumb's null check under a comment explaining that it did nothing (F374).
                return ReadPetHeaderIcon(id);
            }
            catch { return null; }
        }

        /// <summary>Decode the &lt;header&gt;&lt;icon&gt; ICO from an installed or bundled pet's animations.xml.
        /// WPF's decoder handles the PNG-in-ICO the Shimeji importer emits as well as ordinary icons; returns
        /// null when there is no such pet folder or no icon. Uncached: LoadThumb owns the cache and has already
        /// missed on this id before calling down.</summary>
        private static ImageSource ReadPetHeaderIcon(string id)
        {
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
        // bundled pets ship with the app and are not the user's to remove. The test is CompanionProvenance.IsInLibrary.

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
                // The file is gone, so the name, icon and counts cached under this id are for a pet that no
                // longer exists; a later reinstall under the same id would have shown the old ones (F249).
                CompanionCatalog.Forget(id);
                _status.Text = "Uninstalled " + name + ".";
            }
            catch (Exception ex) { _status.Text = "Couldn't uninstall: " + ex.Message; }
            Reload();
            // Mirror the download path: an uninstalled catalog pet (one that isn't also bundled) re-appears
            // under "available for download" immediately, instead of only after the next "Check for new companions".
            RenderAvailable(DiffNew());
        }

        /// <summary>
        /// The built-in card's icon, cached under <see cref="CompanionCatalog.BuiltInPetId"/> (F365).
        /// LoadThumb caches its MISS for that id -- the thumbnail zip has no esheep.png -- which is right
        /// for a library pet and meant this card alone re-ran LoadAppIcon on every rebuild: one fresh Icon
        /// (a live HICON, left to the finalizer), one PNG encode and one decode per pane visit and per
        /// Use/Add/Remove press. A hit REPLACES the null entry; FromPng returns a frozen image, so sharing
        /// one instance across cards is safe, and ForgetStats never targets the built-in id.
        /// </summary>
        private static ImageSource LoadAppIconCached()
        {
            lock (_iconCache)
            {
                ImageSource hit;
                if (_iconCache.TryGetValue(CompanionCatalog.BuiltInPetId, out hit) && hit != null) return hit;
            }
            ImageSource icon = LoadAppIcon();
            if (icon != null) lock (_iconCache) { _iconCache[CompanionCatalog.BuiltInPetId] = icon; }
            return icon;
        }

        /// <summary>Fallback thumbnail for the built-in eSheep (not present in the thumbnail zip): the app
        /// icon. The resource accessor materialises a NEW Icon on every read (ResourceManager caches only
        /// primitive-typed resources), so it is disposed here rather than left for the finalizer (F365).</summary>
        private static ImageSource LoadAppIcon()
        {
            try
            {
                using (System.Drawing.Icon icon = DesktopAICompanion.Properties.Resources.icon)
                using (var bmp = icon.ToBitmap())
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
