using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;
using DesktopAICompanion.Tools.ShimejiConvert;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;
using DesktopAICompanion.Tools.ShimejiConvert.Shimeji;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// The studio window. Built in code rather than XAML to match the host's own WPF panes, and because a
    /// module with a .xaml would need its own build/resource plumbing for one window.
    ///
    /// Layout is one split view: the pet's XML on the left (editable, and the source of truth for preview /
    /// install / save), and on the right a compact report, a colour-coded reachability map of every animation,
    /// and a detail panel that shows the selected animation's sprite frames and where it can go next. Nothing
    /// here decides anything — analysis is PetAnalyzer's job and every pet operation goes through ICompanionManager,
    /// so this file is layout plus wiring.
    /// </summary>
    internal sealed class PetStudioWindow : Window
    {
        private readonly IHost _host;
        private readonly ICompanionManager _pets;
        private readonly IModuleSettings _settings;
        // Built in the constructor, not here: it needs _host, and a field initializer runs BEFORE the
        // constructor body assigns it.
        private readonly PetStudioTheme _theme;

        // Top / bottom bars.
        private readonly TextBlock _path = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        // A dropdown of installed pets, so an author can analyze one the user already has without hunting down
        // its animations.xml. Filled from ICompanionManager.InstalledTypes(); the XML comes back via
        // ICompanionManager.TryReadTypeXml (host 1.8.0+), which reaches the bundled + built-in pets a module cannot.
        private readonly ComboBox _installedPicker = new ComboBox { MinWidth = 190, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private bool _suppressPickerEvent;
        private readonly TextBox _installId = new TextBox { Width = 150, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _installButton = new Button { Content = "Install this companion…", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _previewButton = new Button { Content = "Preview on my desktop", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
        // Play the animation currently selected in the map ON the live preview, so an author can watch a
        // jump or a fall happen on the desktop instead of waiting for the pet's own transitions to choose it.
        // Needs BOTH a live preview and a selection, which is why it is disabled by default and re-evaluated
        // from one place (RefreshPlayOnPreview) rather than at each of the four sites that can change either.
        private readonly Button _playOnPreviewButton = new Button { Content = "Preview highlighted action", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _removeButton = new Button { Content = "Remove preview", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

        // Left: the editable XML, plus its actions.
        private readonly TextBox _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            IsInactiveSelectionHighlightEnabled = true,
        };
        private readonly Button _reanalyzeButton = new Button { Content = "Re-analyze", Padding = new Thickness(10, 3, 10, 3) };
        private readonly Button _saveButton = new Button { Content = "Save", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };

        // Right: report / map / detail.
        private readonly TextBlock _reportText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        // Shimeji import: a residue/"what didn't convert" readout, shown only after an import.
        private readonly TextBlock _importLossText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private UIElement _importLossSection;
        private const string LastSkinDirKey = "lastSkinDir";
        private string _extractedTemp;   // a .zip skin extracted here for the session; deleted on close
        private readonly WrapPanel _map = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _detailTitle = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detailStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        // What the selected animation DOES, in prose. The reachability verdict above it says whether it can
        // play; this says what happens when it does.
        private readonly TextBlock _capabilityText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _framesInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly Image _frameImage = new Image { Stretch = Stretch.Uniform, Height = 96, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Button _playButton = new Button { Content = "▶ Play", Padding = new Thickness(8, 2, 8, 2), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _frameStrip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel _transitions = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        private Border _framePreviewBox;

        // A one-line tally of what the pet's animations DO, under the reachability legend.
        private readonly TextBlock _census = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 11 };

        private readonly Dictionary<int, AnimNode> _nodesById = new Dictionary<int, AnimNode>();
        // Capability per animation id, recomputed whole on each analyze. Not derivable per node: see
        // CapabilityOf.
        private Dictionary<int, AnimCapability> _capabilities = new Dictionary<int, AnimCapability>();
        private readonly Dictionary<int, Border> _chipsById = new Dictionary<int, Border>();
        private Border _selectedChip;

        // Clickable legend filters: each colour category can be shown or hidden in the map.
        private FrameworkElement _swatchRoot, _swatchReachable, _swatchDead;
        private bool _showRoot = true, _showReachable = true, _showDead = true;

        private readonly DispatcherTimer _reanalyzeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        private bool _suppressReanalyze;

        // BUG-012 (F155): the analysis runs on a pool thread. Same shape as AiBrainModule.BeginVramProbe and
        // FortunesModule.RebuildEngineAsync: an Interlocked single-flight gate, Task.Run, and a generation
        // the continuation compares so a result a newer request overtook is dropped unrendered. One addition
        // those two do not need: a request that arrives while one is in flight is REMEMBERED and run when
        // the flight lands, because the text it describes is newer than the one being analyzed and nothing
        // else would analyze it (the probes re-run on their own schedule; this runs when the author stops
        // typing). Both rerun fields are touched on the UI thread only.
        private int _analyzeGeneration;
        private int _analyzeInFlight;
        private bool _analyzeRerun;
        private string _analyzeRerunPrefix;

        private readonly DispatcherTimer _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        private readonly List<BitmapSource> _playFrames = new List<BitmapSource>();
        private int _playIndex;

        private string _openedPath;
        private PetSprite _sprite;
        private string _spriteKey;
        private ICompanionPreview _preview;
        // The map node the author last clicked, kept so "Preview highlighted action" knows WHAT to play.
        // The detail pane already renders this node, but it rendered it straight into TextBlocks and kept
        // no reference, so the selection was visible and not readable.
        private AnimNode _selectedNode;
        // The behaviour debugger. Built in the constructor because it needs the theme and callbacks into this
        // window, and read by MakeChip (which makes every map chip a drag source for it).
        private TimelinePane _timeline;

        internal PetStudioWindow(IHost host)
        {
            _host = host;
            _pets = host != null ? host.GetCompanionManager("petstudio") : null;
            _settings = host != null ? host.GetSettings("petstudio") : null;
            // Ask the host which theme it is presenting; it owns the light/dark/system preference.
            _theme = PetStudioTheme.Current(host);

            // Muted text (path / status / detail status) tracks the theme so it stays readable on dark chrome.
            _path.Foreground = _theme.Muted;
            _status.Foreground = _theme.Muted;
            _detailStatus.Foreground = _theme.Muted;
            _framesInfo.Foreground = _theme.Muted;

            Title = "Companion Studio";
            Width = 1240;
            Height = 720;
            MinWidth = 900;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _reanalyzeButton.Click += delegate { Analyze(); };
            _saveButton.Click += delegate { Save(); };
            _editor.TextChanged += delegate { OnEditorChanged(); };
            _reanalyzeTimer.Tick += delegate { _reanalyzeTimer.Stop(); Analyze(); };
            _playTimer.Tick += delegate { StepPlay(); };
            _playButton.Click += delegate { TogglePlay(); };

            _timeline = new TimelinePane(
                _theme,
                delegate { return _nodesById; },
                delegate { return _editor.Text ?? ""; },
                SetStatus,
                RunDebugPet,
                RemovePreview);

            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };
            UIElement topBar = BuildTopBar();
            UIElement bottomBar = BuildBottomBar();
            DockPanel.SetDock(topBar, Dock.Top);
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            root.Children.Add(topBar);
            root.Children.Add(bottomBar);
            // The timeline is docked BELOW the split and above the status bar, which is where a timeline
            // belongs and also what keeps it out of the three resizable columns: it is per-pet, not per-column.
            DockPanel.SetDock(_timeline.Root, Dock.Bottom);
            root.Children.Add(_timeline.Root);
            root.Children.Add(BuildSplit());
            Content = root;
            _theme.Apply(this);   // paint to match the host; a theme change takes effect on the next open

            ResetDetail();
            if (_pets == null)
                SetStatus("No companion service available — Companion Studio needs the Companions permission.");
            else
                SetStatus("Open a pet's animations.xml to begin.");

            // The tree an earlier window deferred (see ForgetExtractedWithoutDeleting) is collected by
            // nothing else, so the sweep runs when a window opens and again on every load path (F160).
            BeginOrphanSweep();

            // The delete is CONDITIONAL here, and that is the same hazard the second-Import guard in
            // ImportShimejiZip exists for: it is a recursive Directory.Delete of the tree a background
            // extraction or conversion may still be writing or reading. Since 1.1.9 the window stays
            // responsive during a conversion, and a responsive window is one you can close as well as
            // click again -- and PetStudioModule.Shutdown closes it too, so app exit during an import
            // arrives here as well. Since 1.1.18 the zip path holds _importing through its extraction
            // too (F158), so a close mid-extraction takes the Forget branch as a close mid-conversion did.
            //
            // Deliberately LEAVES the directory rather than deleting it under the converter. That is
            // the safe side of an uncertainty I did not reproduce: the cost of not deleting is a temp
            // directory the next window's sweep picks up, while the cost of deleting is an opaque IO
            // failure in the conversion the user is waiting on. The delete itself runs on a pool
            // thread (F159); at app exit it may not finish, which lands on the same safe side.
            Closed += delegate
            {
                _playTimer.Stop();
                _reanalyzeTimer.Stop();
                RemovePreview();
                if (_importing) ForgetExtractedWithoutDeleting();
                else BeginDeleteExtracted();
            };
        }

        // ---- layout ----

        private UIElement _topBar;
        private UIElement BuildTopBar()
        {
            if (_topBar != null) return _topBar;
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock { Text = "install as:", VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.Muted });
            right.Children.Add(_installId);
            _installButton.Click += delegate { Install(); };
            right.Children.Add(_installButton);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var openButton = new Button { Content = "Open animations.xml…", Padding = new Thickness(10, 3, 10, 3) };
            openButton.Click += delegate { OpenFile(); };
            left.Children.Add(openButton);
            var importButton = new Button { Content = "Import skin folder…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            importButton.Click += delegate { ImportShimeji(); };
            left.Children.Add(importButton);
            var importZipButton = new Button { Content = "Import .zip…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            importZipButton.Click += delegate { ImportShimejiZip(); };
            left.Children.Add(importZipButton);
            _installedPicker.DropDownOpened += delegate { PopulateInstalledPicker(); };
            _installedPicker.SelectionChanged += delegate { OnInstalledPicked(); };
            left.Children.Add(_installedPicker);
            PopulateInstalledPicker();
            left.Children.Add(new TextBlock { Text = "  ", Width = 8 });
            left.Children.Add(_path);
            bar.Children.Add(left);

            _topBar = bar;
            return bar;
        }

        private UIElement _bottomBar;
        private UIElement BuildBottomBar()
        {
            if (_bottomBar != null) return _bottomBar;
            var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _previewButton.Click += delegate { Preview(); };
            _playOnPreviewButton.Click += delegate { PlaySelectedOnPreview(); };
            _removeButton.Click += delegate { RemovePreview(); };
            buttons.Children.Add(_previewButton);
            buttons.Children.Add(_playOnPreviewButton);
            buttons.Children.Add(_removeButton);
            DockPanel.SetDock(buttons, Dock.Left);
            bar.Children.Add(buttons);

            _status.Margin = new Thickness(16, 0, 0, 0);
            bar.Children.Add(_status);

            _bottomBar = bar;
            return bar;
        }

        private UIElement BuildSplit()
        {
            // Three resizable columns: the XML, the report + reachability map, and the selected animation.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 240 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });

            AddColumn(grid, 0, Section("Companion XML", BuildEditorPane()), 0);
            Grid.SetColumn(ColumnSplitter(grid, 1), 1);
            AddColumn(grid, 2, BuildReportMapColumn(), 10);
            Grid.SetColumn(ColumnSplitter(grid, 3), 3);
            AddColumn(grid, 4, Section("Selected animation", BuildDetail()), 10);

            return grid;
        }

        private static void AddColumn(Grid grid, int column, UIElement pane, double leftMargin)
        {
            var fe = pane as FrameworkElement;
            if (fe != null) fe.Margin = new Thickness(leftMargin, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            Grid.SetColumn(pane, column);
            grid.Children.Add(pane);
        }

        private GridSplitter ColumnSplitter(Grid grid, int column)
        {
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent };
            grid.Children.Add(splitter);
            return splitter;
        }

        private UIElement BuildReportMapColumn()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // report
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // import loss (import only)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // map

            _reportText.Text = "No companion loaded yet.";
            var report = Section("Report", _reportText);
            Grid.SetRow(report, 0);
            grid.Children.Add(report);

            var importLossScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200, Content = _importLossText };
            _importLossSection = Section("Import loss (what didn't convert)", importLossScroll);
            _importLossSection.Visibility = Visibility.Collapsed;
            Grid.SetRow(_importLossSection, 1);
            grid.Children.Add(_importLossSection);

            var mapArea = new DockPanel { LastChildFill = true };
            var legend = BuildLegend();
            DockPanel.SetDock(legend, Dock.Bottom);
            mapArea.Children.Add(legend);
            var mapScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _map };
            mapArea.Children.Add(mapScroll);
            var mapSection = Section("Reachability map", mapArea);
            Grid.SetRow(mapSection, 2);
            grid.Children.Add(mapSection);

            return grid;
        }

        private UIElement BuildEditorPane()
        {
            var dock = new DockPanel { LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            actions.Children.Add(_reanalyzeButton);
            actions.Children.Add(_saveButton);
            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);
            dock.Children.Add(_editor);
            return dock;
        }

        private UIElement BuildDetail()
        {
            var panel = new StackPanel();
            panel.Children.Add(_detailTitle);
            panel.Children.Add(_detailStatus);
            panel.Children.Add(_capabilityText);
            panel.Children.Add(_framesInfo);

            RenderOptions.SetBitmapScalingMode(_frameImage, BitmapScalingMode.NearestNeighbor);
            _framePreviewBox = new Border
            {
                Background = _theme.PreviewBg,
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
                Child = _frameImage,
            };
            var previewRow = new StackPanel { Orientation = Orientation.Horizontal };
            previewRow.Children.Add(_framePreviewBox);
            _playButton.Margin = new Thickness(8, 6, 0, 0);
            _playButton.VerticalAlignment = VerticalAlignment.Bottom;
            previewRow.Children.Add(_playButton);
            panel.Children.Add(previewRow);

            var stripScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 6, 0, 0),
                Content = _frameStrip,
            };
            panel.Children.Add(stripScroll);
            panel.Children.Add(_transitions);

            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private UIElement BuildLegend()
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _swatchRoot = Swatch(_theme.RootFill, _theme.RootStroke, "root",
                "Entry animations the engine can start in directly (drag / fall / kill / sync, or a spawn). Click to show/hide.",
                () => ToggleFilter("root"));
            _swatchReachable = Swatch(_theme.LiveFill, _theme.LiveStroke, "reachable",
                "Reached by a transition from a root. Click to show/hide.", () => ToggleFilter("reachable"));
            _swatchDead = Swatch(_theme.DeadFill, _theme.DeadStroke, "never plays",
                "Unreachable — nothing leads here. Click to show/hide.", () => ToggleFilter("dead"));
            legend.Children.Add(_swatchRoot);
            legend.Children.Add(_swatchReachable);
            legend.Children.Add(_swatchDead);
            legend.Children.Add(new TextBlock { Text = "(click to filter)", Foreground = _theme.Muted, FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center });

            // The census sits under the colour legend rather than beside it: the colours say whether an
            // animation CAN play, the badges say what it does, and conflating the two rows would suggest they
            // are the same axis.
            var stack = new StackPanel();
            stack.Children.Add(legend);
            stack.Children.Add(_census);
            return stack;
        }

        /// <summary>
        /// What one animation does, from the classification computed for the WHOLE pet.
        ///
        /// Per-pet rather than per-node because the answer is not local: a jump and a wall climb are
        /// indistinguishable by velocity, and only the graph knows which border edge put the pet there.
        /// Recomputed on each analyze and cached, so a chip and the detail panel cannot disagree.
        /// </summary>
        private AnimCapability CapabilityOf(AnimNode node)
        {
            AnimCapability capability;
            if (node != null && _capabilities.TryGetValue(node.Id, out capability)) return capability;
            return AnimCapability.Idle;
        }

        /// <summary>Fill the badge census under the map, so "which of these 31 is the jump" is answerable at a
        /// glance instead of by reading every chip.</summary>
        private void RenderCensus(PetReport report)
        {
            _census.Inlines.Clear();
            if (report == null || report.Nodes.Count == 0) return;
            _census.Inlines.Add(new System.Windows.Documents.Run("what they do:  ") { Foreground = _theme.Muted });
            bool first = true;
            // _capabilities was computed by RenderMap immediately before this call, so the census reuses
            // it rather than classifying the whole pet a second time on every 750 ms debounce.
            foreach (KeyValuePair<AnimCapability, int> kv in AnimCapabilities.Census(report.Nodes, _capabilities))
            {
                string badge = AnimCapabilities.Badge(kv.Key);
                string text = (badge.Length > 0 ? badge : "in place") + " " + kv.Value;
                _census.Inlines.Add(new System.Windows.Documents.Run((first ? "" : "  ·  ") + text)
                {
                    Foreground = badge.Length > 0 ? _theme.Text : _theme.Muted,
                    FontWeight = badge.Length > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                });
                first = false;
            }
        }

        private FrameworkElement Swatch(Brush fill, Brush stroke, string label, string tooltip, Action onClick)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 14, 0),
                Background = Brushes.Transparent,   // makes the whole row hit-testable, not just its children
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tooltip,
            };
            row.Children.Add(new Border { Width = 12, Height = 12, Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = " " + label, Foreground = _theme.Muted, VerticalAlignment = VerticalAlignment.Center });
            row.MouseLeftButtonUp += delegate { onClick(); };
            return row;
        }

        /// <summary>Toggle a colour category's visibility in the map, dimming its legend swatch when hidden.</summary>
        private void ToggleFilter(string category)
        {
            if (category == "root") _showRoot = !_showRoot;
            else if (category == "reachable") _showReachable = !_showReachable;
            else _showDead = !_showDead;

            if (_swatchRoot != null) _swatchRoot.Opacity = _showRoot ? 1.0 : 0.35;
            if (_swatchReachable != null) _swatchReachable.Opacity = _showReachable ? 1.0 : 0.35;
            if (_swatchDead != null) _swatchDead.Opacity = _showDead ? 1.0 : 0.35;

            ApplyMapFilter();
        }

        /// <summary>Show or hide each chip per the current category filters. Chips keep their identity, so a
        /// selection and its detail survive a filter change.</summary>
        private void ApplyMapFilter()
        {
            foreach (KeyValuePair<int, Border> kv in _chipsById)
            {
                AnimNode node;
                if (!_nodesById.TryGetValue(kv.Key, out node)) continue;
                bool show = !node.IsReachable ? _showDead : (node.IsRoot ? _showRoot : _showReachable);
                kv.Value.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>A titled block: a bold caption over its content, filling the rest.</summary>
        private static UIElement Section(string title, UIElement content)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var caption = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(caption, Dock.Top);
            dock.Children.Add(caption);
            dock.Children.Add(content);
            return dock;
        }

        // ---- open / edit / save ----

        private void OpenFile()
        {
            // Refused like a second Import (F163): the conversion in flight lands in the editor when it
            // finishes, and whatever was opened meanwhile would be replaced without a word.
            if (_importing) { SetStatus(StillConverting); return; }
            BeginOrphanSweep();
            try
            {
                // Own the dialog here rather than call host.PickFilesToOpen: Companion Studio is the one module
                // that already carries a UI framework, and only a self-owned dialog lets it set the starting
                // directory to the author's pet library (or wherever they last worked).
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Open a pet's animations.xml",
                    Filter = "Pet XML (*.xml)|*.xml|All files (*.*)|*.*",
                    CheckFileExists = true,
                    InitialDirectory = InitialOpenDir(),
                };
                if (dialog.ShowDialog(this) != true) return;

                string path = dialog.FileName;
                // READ FIRST, then adopt. These four assignments used to run before File.ReadAllText, so a
                // file that could not be opened (another process holding it with FileShare.None) left the
                // editor showing the PREVIOUS file's content while _openedPath pointed at the new one. The
                // next Save then wrote the old content over the file it had just failed to read, and said it
                // had saved. The catch below only writes a status line, which is what made it survivable
                // enough to ship.
                string text = File.ReadAllText(path);
                _openedPath = path;
                _path.Text = path;
                _installId.Text = SuggestId(path);
                _saveButton.IsEnabled = true;
                HideImportLoss();               // the loss readout belongs to an import, not an opened file
                RememberOpenDir(path);
                SetEditorText(text);
                Analyze();
            }
            catch (Exception ex)
            {
                _reportText.Text = "";
                SetStatus("Couldn't read that file: " + ex.Message);
            }
        }

        /// <summary>Set the editor text without kicking off the debounced re-analyze (the caller analyzes).</summary>
        private void SetEditorText(string text)
        {
            _suppressReanalyze = true;
            try { _editor.Text = text ?? ""; }
            finally { _suppressReanalyze = false; }
        }

        // Fill the installed-pet dropdown from the host. A leading placeholder keeps "nothing chosen" distinct
        // from a real pet; the picker is disabled when the Companions permission (hence the pet service) is absent or
        // nothing is installed. Called at build time and again whenever the list drops open, so a pet installed
        // while the window is up shows up without a reopen.
        private void PopulateInstalledPicker()
        {
            _suppressPickerEvent = true;
            try
            {
                _installedPicker.Items.Clear();
                _installedPicker.Items.Add(new ComboBoxItem { Content = "Analyze installed companion…", Tag = null });
                _installedPicker.SelectedIndex = 0;
                IReadOnlyList<CompanionTypeInfo> types = null;
                if (_pets != null) { try { types = _pets.InstalledTypes(); } catch { types = null; } }
                if (types != null)
                    foreach (CompanionTypeInfo t in types)
                    {
                        if (t == null || string.IsNullOrEmpty(t.TypeId)) continue;
                        string label = string.IsNullOrWhiteSpace(t.DisplayName) ? t.TypeId : t.DisplayName;
                        if (t.IsBuiltIn) label += " (built-in)";
                        _installedPicker.Items.Add(new ComboBoxItem { Content = label, Tag = t.TypeId });
                    }
                _installedPicker.IsEnabled = _installedPicker.Items.Count > 1;
            }
            finally { _suppressPickerEvent = false; }
        }

        // Load the chosen installed pet's animations.xml into the editor and analyze it. Reading goes through the
        // host (TryReadTypeXml), so a bundled or built-in pet the module cannot open on disk still works.
        private void OnInstalledPicked()
        {
            if (_suppressPickerEvent) return;
            ComboBoxItem item = _installedPicker.SelectedItem as ComboBoxItem;
            string id = item != null ? item.Tag as string : null;
            if (string.IsNullOrEmpty(id) || _pets == null) return;
            if (_importing)
            {
                // Refused like a second Import (F163), and the dropdown is put back so it does not show
                // a pet the editor does not hold.
                SetStatus(StillConverting);
                _suppressPickerEvent = true;
                try { _installedPicker.SelectedIndex = 0; }
                finally { _suppressPickerEvent = false; }
                return;
            }
            BeginOrphanSweep();
            string xml, error;
            if (!_pets.TryReadTypeXml(id, out xml, out error) || string.IsNullOrWhiteSpace(xml))
            {
                SetStatus("Couldn't read '" + id + "': " +
                    (string.IsNullOrWhiteSpace(error) ? "not found." : error));
                return;
            }
            HideImportLoss();               // an installed pet carries no import-loss report
            // The editor no longer holds the file that was open, so neither may Save. Without this the path
            // survived the swap and Save wrote the INSTALLED pet's XML over the author's own file, atomically,
            // reporting the victim's path back as a success -- with no partial file left to recover from.
            // LoadConvertedIntoEditor already does exactly this for the import path, and says why.
            _openedPath = null;
            _installId.Text = SafeId(id);
            _path.Text = "Installed: " + id;
            SetEditorText(xml);
            Analyze();
        }

        private void OnEditorChanged()
        {
            if (_suppressReanalyze) return;
            _reanalyzeTimer.Stop();
            _reanalyzeTimer.Start();   // re-analyze once the typing settles
        }

        private void Save()
        {
            string path = _openedPath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save animations.xml",
                    Filter = "Pet XML (*.xml)|*.xml|All files (*.*)|*.*",
                    FileName = "animations.xml",
                    InitialDirectory = InitialOpenDir(),
                };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }
            try
            {
                // ModuleKit's durable write: temp file in the same directory, flushed through, then swapped
                // over the destination, so a crash mid-save can never truncate the author's pet.
                if (!AtomicFile.TryWriteAllText(path, _editor.Text ?? "", null))
                    throw new IOException("The file could not be written.");
                _openedPath = path;
                _path.Text = path;
                _saveButton.IsEnabled = true;
                RememberOpenDir(path);
                SetStatus("Saved to " + path);
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't save: " + ex.Message);
            }
        }

        /// <summary>Where the Open dialog should start: the folder last browsed to, else the pet library,
        /// else Documents. The policy itself lives in PetStudioPaths so the self-test can pin it.</summary>
        private string InitialOpenDir()
        {
            string saved = _settings != null ? _settings.Get(PetStudioPaths.LastOpenDirKey, "") : "";
            string pets = _pets != null ? _pets.CompanionsDirectory : "";
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return PetStudioPaths.ResolveInitialDir(saved, pets, docs, Directory.Exists);
        }

        /// <summary>Remember the folder this file came from, so the next Open defaults back to it.</summary>
        private void RememberOpenDir(string path)
        {
            if (_settings == null) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(dir)) return;
                _settings.Set(PetStudioPaths.LastOpenDirKey, dir);
                _settings.Save();
            }
            catch { }
        }

        // ---- import a Shimeji skin ----

        /// <summary>Public entry so the host (e.g. the Pets pane, or a catalog hand-off) can open Companion Studio
        /// straight into the Shimeji import flow.</summary>
        internal void BeginImport() { ImportShimeji(); }

        /// <summary>
        /// True while an import is extracting or converting. The heavy work runs off the UI thread now,
        /// which means the window stays responsive -- and a responsive window is one the user can click
        /// Import on again, or Open, or the installed picker. Two conversions writing the editor and the
        /// import-loss panel at once is not a state this window has any answer for, so the second click
        /// is refused rather than queued, and so are Open and the picker (F163).
        ///
        /// OWNED BY THE ENTRY POINT, for the whole import. Until 1.1.18 it was first set inside
        /// ImportSkinFromRootAsync, i.e. AFTER the zip path's extraction await, so for the seconds a
        /// multi-MB skin took to extract the guards tested a flag nothing had set yet (F158).
        /// </summary>
        private bool _importing;
        private const string StillConverting = "Still converting the last skin…";

        private async void ImportShimeji()
        {
            // BEFORE the dialog. Refusing after it would open a file picker only to throw the
            // answer away, which reads as the app ignoring the click.
            if (_importing) { SetStatus(StillConverting); return; }
            string root;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose a Shimeji skin folder (or a folder that contains skins)";
                string start = InitialSkinDir();
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.SelectedPath = start;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                root = dlg.SelectedPath;
            }
            // The folder the AUTHOR chose, remembered here where it is known (F162); the core it is handed
            // to remembers nothing, because the root it sees may be a temp tree.
            RememberSkinDir(root);
            BeginOrphanSweep();
            await ImportSkinFromRootAsync(root);
        }

        private async void ImportShimejiZip()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a Shimeji skin .zip",
                Filter = "Shimeji skin zip (*.zip)|*.zip|All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = InitialSkinDir(),
            };
            if (dlg.ShowDialog(this) != true) return;
            if (_importing) { SetStatus(StillConverting); return; }
            // OWNED FROM HERE, through the extraction, to the end of the conversion (F158). The 1.1.11
            // guard above ran "BEFORE CleanupExtracted, and that ordering is the whole point" -- and it
            // did, and it tested a flag that ImportSkinFromRootAsync set only after the extraction
            // await, so for the seconds a multi-MB skin took to extract a second click passed it,
            // deleted the tree the first extraction was writing into, and then had its own conversion
            // refused with "Still converting the last skin"; the Closed handler took its deleting branch
            // under the extractor the same way. Setting the flag here, before any filesystem work, and
            // clearing it in the finally is what makes both guards mean what they say.
            _importing = true;
            try
            {
                RememberSkinDir(Path.GetDirectoryName(dlg.FileName));   // the zip's own folder (F162), never the extraction tree
                BeginOrphanSweep();
                string previous = _extractedTemp;   // captured BEFORE the field moves on, so the pool thread deletes the right tree
                string zipPath = dlg.FileName;
                string destination = Path.Combine(Path.GetTempPath(), "petstudio-shimeji-" + Guid.NewGuid().ToString("N"));
                _extractedTemp = destination;
                // OFF THE UI THREAD, all of it: the previous skin's recursive delete (hundreds of PNGs; it
                // ran on the dispatcher in this handler until 1.1.18, F159) and the new directory, then the
                // extraction (tens of MB; inline, it froze the window before conversion had even started).
                // Two hops rather than one on purpose: the source invariant that guards the extraction's
                // wrapping names the extraction statement's exact shape, and it keeps guarding it this way.
                await Task.Run(delegate
                {
                    DeleteTree(previous);
                    Directory.CreateDirectory(destination);
                });
                await Task.Run(delegate { ZipFile.ExtractToDirectory(zipPath, destination); });
                await ImportSkinFromRootCoreAsync(destination);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read that .zip: " + ex.Message);
            }
            finally
            {
                _importing = false;
            }
        }

        /// <summary>
        /// Drop this window's extraction tree, on a pool thread (F159: the recursive delete of a skin's
        /// hundreds of PNGs ran on the dispatcher, in the zip click handler and in Closed). The field is
        /// cleared first so nothing finds the path again. Best effort: a delete that does not finish -- at
        /// app exit PetStudioModule.Shutdown closes this window and the process may not wait for the pool
        /// -- leaves a petstudio-shimeji-* tree that the next window's sweep collects, the same trade
        /// ForgetExtractedWithoutDeleting makes on purpose.
        /// </summary>
        private void BeginDeleteExtracted()
        {
            string path = _extractedTemp;
            _extractedTemp = null;
            if (string.IsNullOrEmpty(path)) return;
            Task.Run(delegate { DeleteTree(path); });
        }

        /// <summary>One recursive delete, swallowing everything: a tree another process still holds open is
        /// left for the sweep rather than failing the import or the close in front of it.</summary>
        private static void DeleteTree(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        /// <summary>
        /// Delete extraction trees this module left behind, which happens when the window is closed
        /// while an extraction or conversion is still using one (see ForgetExtractedWithoutDeleting).
        ///
        /// Three narrowings, because this deletes directories:
        ///   * ONLY %TEMP% plus this module's own "petstudio-shimeji-" prefix, and the name after it
        ///     must parse as the GUID this module generates -- so a folder somebody else happened to
        ///     name that way is not ours to remove;
        ///   * only trees older than SweepAgeHours, so a SECOND instance mid-conversion never has its
        ///     live tree taken away. A conversion takes seconds; six hours is not a race -- and it is
        ///     what makes running the sweep on every load path (BeginOrphanSweep) safe for a live tree;
        ///   * best-effort per directory, because one another process still holds open must not
        ///     abort the import this is running in front of.
        /// </summary>
        private const int SweepAgeHours = 6;

        // At most one sweep in flight, ever: the Interlocked gate of AiBrainModule.BeginVramProbe. Static
        // because the sweep is, and because it guards %TEMP%, which every window of this module shares.
        private static int _sweepInFlight;

        /// <summary>
        /// Run <see cref="SweepOrphanedExtractions"/> on a pool thread, at most once at a time. The shape
        /// is AiBrainModule.BeginVramProbe's minus the result, since there is nothing to marshal back.
        /// Called at construction and from every load path -- Open, the installed picker, both imports --
        /// because the tree ForgetExtractedWithoutDeleting defers is collected by NOTHING else, and until
        /// 1.1.18 the only caller was the zip import, so an author who closed mid-conversion and then
        /// only imported folders kept the tree until Windows cleaned %TEMP% (F160). The enumerate and
        /// the deletes leave the UI thread with it (F159).
        /// </summary>
        private static void BeginOrphanSweep()
        {
            if (Interlocked.CompareExchange(ref _sweepInFlight, 1, 0) != 0) return;
            Task.Run(delegate
            {
                try { SweepOrphanedExtractions(); }
                finally { Interlocked.Exchange(ref _sweepInFlight, 0); }
            });
        }

        private static void SweepOrphanedExtractions()
        {
            try
            {
                string temp = Path.GetTempPath();
                DateTime cutoff = DateTime.UtcNow.AddHours(-SweepAgeHours);
                foreach (string dir in Directory.EnumerateDirectories(temp, "petstudio-shimeji-*"))
                {
                    try
                    {
                        string suffix = Path.GetFileName(dir);
                        if (suffix == null || suffix.Length != "petstudio-shimeji-".Length + 32) continue;
                        Guid ignored;
                        if (!Guid.TryParseExact(suffix.Substring("petstudio-shimeji-".Length), "N", out ignored)) continue;
                        if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                        Directory.Delete(dir, true);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Drop the reference WITHOUT deleting, for the one case where deleting is the bug: the window
        /// is closing while an extraction or a conversion is still using that tree on a pool thread.
        ///
        /// The directory is left in %TEMP% under the petstudio-shimeji-* prefix, which is what
        /// SweepOrphanedExtractions looks for from the next window's construction and every load path,
        /// so this DEFERS the cleanup rather than abandoning it. Nulling the field matters as much as not
        /// deleting: it stops a later delete on the same instance from finding the path again.
        /// </summary>
        private void ForgetExtractedWithoutDeleting()
        {
            _extractedTemp = null;
        }

        /// <summary>Convert the first skin under <paramref name="root"/> into the editor, owning the import
        /// guard for the duration. The folder dialog's path, and the entry for a future catalog hand-off
        /// that downloads a raw skin to a temp folder; the zip path holds the guard itself, through its
        /// extraction, and calls the core directly (F158).</summary>
        internal async Task ImportSkinFromRootAsync(string root)
        {
            if (_importing) { SetStatus(StillConverting); return; }
            _importing = true;
            try { await ImportSkinFromRootCoreAsync(root); }
            finally
            {
                // In a finally, so a conversion that throws does not leave the window refusing every
                // later import with "Still converting the last skin".
                _importing = false;
            }
        }

        /// <summary>The conversion itself; the caller owns <c>_importing</c>. It remembers no folder: the
        /// root it sees may be this module's own extraction tree, and a catalog hand-off's download folder
        /// has the same shape, so the folder the AUTHOR chose is remembered by the entry point that knows
        /// it (F162: until 1.1.18 the zip path's remembered folder was overwritten here with %TEMP%).</summary>
        private async Task ImportSkinFromRootCoreAsync(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { SetStatus("No such folder."); return; }

                // Android JSON+WebP bundle (manifest.json + animation.json + sprites/*.webp)? Convert that path.
                // The bundle can sit one level down inside a zip, so search for it before the classic layout.
                // OFF THE UI THREAD, like its three siblings. FindBundleRoot is a recursive
                // EnumerateDirectories over the whole extracted tree with two File.Exists per hit, and
                // it sat BEFORE the first await in this method -- so it ran synchronously on the click
                // while the zip extraction, SkinLayout.Detect and both converters had all been moved
                // off in 1.1.9, each with a comment saying why. This one was missed.
                //
                // ReadBundleName rides along: same class of work, and it sits between this call and
                // the next await, so leaving it behind would only move the stall a line down. Both are
                // private static and touch no UI, so this is a straight lift. No ConfigureAwait(false)
                // anywhere in this method: everything after the awaits is UI-affine and depends on
                // resuming on the captured context.
                string bundleRoot = await Task.Run(() => FindBundleRoot(root));
                if (bundleRoot != null)
                {
                    string bundleName = await Task.Run(() => ReadBundleName(bundleRoot));
                    // Task.Run, with the `out` captured into a local: BundleConverter walks every sprite,
                    // composites a sheet and base64-encodes it, which is seconds of work, not milliseconds.
                    SetStatus("Converting…");
                    string bundleError = null;
                    ConversionResult bundleResult = await Task.Run(delegate
                    {
                        string e;
                        ConversionResult r = BundleConverter.ConvertBundle(bundleRoot, bundleName, out e);
                        bundleError = e;
                        return r;
                    });
                    if (bundleResult == null) { HideImportLoss(); SetStatus("Bundle conversion failed: " + bundleError); return; }
                    LoadConvertedIntoEditor(bundleResult, string.IsNullOrWhiteSpace(bundleName) ? "Shimeji" : bundleName.Trim(), "");
                    return;
                }

                // Detection walks the skin tree, so it goes with the rest of the file work.
                string note = null;
                var skins = await Task.Run(delegate
                {
                    string n;
                    var found = SkinLayout.Detect(root, out n);
                    note = n;
                    return found;
                });
                if (skins == null || skins.Count == 0)
                {
                    HideImportLoss();
                    SetStatus("No convertible Shimeji skin found here." + (string.IsNullOrEmpty(note) ? "" : " " + note));
                    return;
                }

                DetectedSkin skin = skins[0];
                string extra = skins.Count > 1
                    ? " (found " + skins.Count + " skins; converted the first, '" + skin.Name + "')"
                    : "";

                // The big one. SpriteSheetBuilder.Build can run up to 8 full composite + PNG-encode +
                // base64 passes over a sheet as large as 4096x4096 before giving up on the 12 MiB budget,
                // and with ffmpeg on PATH SoundBaker spawns one ffmpeg per unique clip (30s cap each, up to
                // 64) plus a recursive EnumerateFiles of the skin root per distinct clip name. All of that
                // ran inline from a click handler.
                SetStatus("Converting…");
                string error = null;
                DetectedSkin converting = skin;
                ConversionResult result = await Task.Run(delegate
                {
                    string e;
                    ConversionResult r = ShimejiEngine.ConvertSkin(
                        converting.ConfDir, converting.ImgDir, converting.Name, out e);
                    error = e;
                    return r;
                });
                if (result == null)
                {
                    HideImportLoss();
                    SetStatus("Conversion failed: " + error);
                    return;
                }
                LoadConvertedIntoEditor(result, skin.Name, extra);
            }
            catch (Exception ex)
            {
                SetStatus("Import failed: " + ex.Message);
            }
        }

        /// <summary>Put a freshly converted skin (desktop or Android bundle) into the editor, analysis, and
        /// import-loss panel, and report acceptance. Shared by both import paths.</summary>
        private void LoadConvertedIntoEditor(ConversionResult result, string name, string extra)
        {
            _openedPath = null;                     // imported, not opened from a file: Save will prompt for a path
            _path.Text = "Imported: " + name;
            _installId.Text = SafeId(name);
            _saveButton.IsEnabled = true;
            SetEditorText(result.EmittedXml);
            ShowImportLoss(result, name);
            // The verdict is the analysis's to state, and it lands when the analysis does (BUG-012 moved the
            // analysis off the UI thread). This used to say "but the host would reject it" whenever the
            // CONVERTER's acceptance bar failed, which is stricter than the host's: a valid pet with one
            // unreachable animation was announced as rejected by a host that had accepted it (F429).
            BeginAnalyze(ImportedStatusPrefix(name, result.Valid, result.RoundTrips, extra));
        }

        /// <summary>
        /// What an import says ahead of the analysis verdict. It names the one converter fact the analysis
        /// cannot see (an emitted pet that does not round-trip through the host's own serializer) and leaves
        /// the rest to the verdict, whose "would reject" means the validator refused the XML and whose
        /// "will never play" is the unreachable count the converter's Accepted also folded in. Pure so the
        /// module self-test can pin the wording.
        /// </summary>
        internal static string ImportedStatusPrefix(string name, bool valid, bool roundTrips, string extra)
        {
            string prefix = "Imported '" + name + "'" + (extra ?? "");
            if (valid && !roundTrips)
                prefix += ", though its XML does not round-trip through the host's serializer";
            return prefix + ". ";
        }

        /// <summary>Find the Android Shimeji bundle (manifest.json + animation.json) at or under
        /// <paramref name="root"/>, so a zip that wraps the bundle one level down still resolves. Null if none.</summary>
        private static string FindBundleRoot(string root)
        {
            try
            {
                if (BundleConverter.IsBundle(root)) return root;
                foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    if (BundleConverter.IsBundle(dir)) return dir;
            }
            catch { }
            return null;
        }

        /// <summary>Read the display name from an Android bundle's manifest.json, or null.</summary>
        private static string ReadBundleName(string bundleRoot)
        {
            try
            {
                BundleInfo info;
                BundleParser.Parse(bundleRoot, out info);
                return info != null ? info.Name : null;
            }
            catch { return null; }
        }

        private void ShowImportLoss(ConversionResult result, string skinName)
        {
            if (result == null || result.Residue == null) { HideImportLoss(); return; }
            _importLossText.Text = result.Residue.ToText(skinName);
            if (_importLossSection != null) _importLossSection.Visibility = Visibility.Visible;
        }

        private void HideImportLoss()
        {
            _importLossText.Text = "";
            if (_importLossSection != null) _importLossSection.Visibility = Visibility.Collapsed;
        }

        private string InitialSkinDir()
        {
            string saved = _settings != null ? _settings.Get(LastSkinDirKey, "") : "";
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved)) return saved;
            return InitialOpenDir();
        }

        private void RememberSkinDir(string dir)
        {
            if (_settings == null || string.IsNullOrWhiteSpace(dir)) return;
            try { _settings.Set(LastSkinDirKey, dir); _settings.Save(); }
            catch { }
        }

        private static string SafeId(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            string id = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(id) ? "shimeji-pet" : id;
        }

        // ---- analysis + rendering ----

        private void Analyze() { BeginAnalyze(""); }

        /// <summary>
        /// Analyze the editor's text on a pool thread and render the result on this one.
        ///
        /// <paramref name="statusPrefix"/> is what the loader that asked for this analysis wants said ahead
        /// of the verdict ("Imported 'X'. "), because the verdict IS the status. Until 1.1.18 the import path
        /// wrote its own status after a synchronous Analyze had written the verdict, and the timeline's
        /// "Dropped N step(s)" note was written by Resync and overwritten by the verdict a few statements
        /// later in the same call, so it never rendered (F165). Everything that writes the status for an
        /// analysis now goes through the one continuation in this method.
        /// </summary>
        private async void BeginAnalyze(string statusPrefix)
        {
            _reanalyzeTimer.Stop();
            int generation = Interlocked.Increment(ref _analyzeGeneration);
            if (Interlocked.CompareExchange(ref _analyzeInFlight, 1, 0) != 0)
            {
                // Remembered, not dropped: see the field comment.
                _analyzeRerun = true;
                _analyzeRerunPrefix = statusPrefix;
                return;
            }
            string xml = _editor.Text ?? "";
            SetStatus(statusPrefix + "Analyzing…");
            PetReport report = null;
            try
            {
                // PetAnalyzer.Analyze is UI-free by its own header -- the host's parse, validation and graph
                // walk, no WPF -- so it is the whole of the pool-thread work. The WPF sheet decode in
                // RenderAnalysis stays on this thread: a BitmapSource is thread-affine until frozen, and
                // the SpriteKey cache already runs it once per sheet rather than once per analyze. No
                // ConfigureAwait(false): everything after the await is UI-affine.
                report = await Task.Run(delegate { return PetAnalyzer.Analyze(xml); });
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _analyzeGeneration) == generation)
                    SetStatus(statusPrefix + "Analysis failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _analyzeInFlight, 0);
            }
            if (Volatile.Read(ref _analyzeGeneration) == generation && report != null) RenderAnalysis(report, statusPrefix);
            if (_analyzeRerun)
            {
                // The text changed while this ran; analyze what is there now.
                _analyzeRerun = false;
                string prefix = _analyzeRerunPrefix ?? "";
                _analyzeRerunPrefix = null;
                BeginAnalyze(prefix);
            }
        }

        /// <summary>The UI half of an analysis: the sheet (once per sheet), the report, the map, the two
        /// gated buttons and the status. Runs on the dispatcher, for a report the generation check accepted.</summary>
        private void RenderAnalysis(PetReport report, string statusPrefix)
        {
            // Decode the sprite sheet only when the <image> actually changed. Editing re-analyzes every ~750ms,
            // and the sheet decode is by far the window's largest allocation, so re-decoding it on every
            // keystroke-settle would spike memory continuously for an image the edit never touched.
            string key = SpriteKey(report);
            if (!string.Equals(key, _spriteKey, StringComparison.Ordinal))
            {
                _sprite = PetSprite.TryDecode(report.SpritePngBase64, report.TilesX, report.TilesY, report.TransparencyColor);
                _spriteKey = key;
            }

            RenderReport(report);
            int droppedSteps = RenderMap(report);
            ResetDetail();

            // Preview and install are gated on the host ACCEPTING the pet, not on it being warning-free: an
            // unreachable animation is worth telling the author about, but it does not stop the pet running.
            _previewButton.IsEnabled = report.IsValid && _pets != null;
            _installButton.IsEnabled = report.IsValid && _pets != null;

            SetStatus(statusPrefix + AnalysisStatus(report.IsValid, report.UnreachableAnimations.Count, droppedSteps));
        }

        /// <summary>
        /// The verdict sentence, plus the timeline's dropped-step note when there is one. A pure function so
        /// the module self-test can pin it (F165: the note used to be written and then overwritten inside one
        /// Analyze, so no author ever read it). Worded for the edit that deletes an animation and for the
        /// open that swaps the whole pet alike.
        /// </summary>
        internal static string AnalysisStatus(bool isValid, int unreachable, int droppedSteps)
        {
            string verdict = isValid
                ? (unreachable == 0
                    ? "This companion is good to go."
                    : "This companion runs, but " + unreachable + " animation(s) will never play.")
                : "The host would reject this companion.";
            if (droppedSteps > 0)
                verdict += " Dropped " + droppedSteps + " timeline step(s) this companion does not have.";
            return verdict;
        }

        /// <summary>A cheap fingerprint of the sprite inputs — tiles, transparency, and the base64 length plus
        /// its head/tail — so a re-analyze can tell whether the sheet changed without comparing megabytes.</summary>
        private static string SpriteKey(PetReport r)
        {
            string b = r.SpritePngBase64 ?? "";
            string ends = b.Length > 64 ? b.Substring(0, 32) + b.Substring(b.Length - 32) : b;
            return r.TilesX + "x" + r.TilesY + "|" + r.TransparencyColor + "|" + b.Length + "|" + ends;
        }

        private void RenderReport(PetReport report)
        {
            if (!report.IsValid)
            {
                _reportText.Text = "REJECTED — this pet would not load:\n" + report.Error;
                return;
            }
            string who = "Valid companion" +
                (report.PetName.Length > 0 ? " — " + report.PetName : "") +
                (report.Author.Length > 0 ? " by " + report.Author : "");
            _reportText.Text = who + "\n" +
                report.AnimationCount + " animations · " + report.SpawnCount + " spawns · " +
                report.ChildCount + " children · " + report.UnreachableAnimations.Count + " never play";
        }

        /// <summary>Rebuild the map for a report. Returns the number of timeline steps the resync dropped, so
        /// the status the analysis writes can say so (F165).</summary>
        private int RenderMap(PetReport report)
        {
            _map.Children.Clear();
            _nodesById.Clear();
            _chipsById.Clear();
            _selectedChip = null;
            // Whole-pet, and BEFORE the chips are built: each chip's badge reads from it.
            _capabilities = AnimCapabilities.ClassifyAll(report.Nodes);
            foreach (AnimNode node in report.Nodes)
            {
                _nodesById[node.Id] = node;
                Border chip = MakeChip(node);
                _chipsById[node.Id] = chip;
                _map.Children.Add(chip);
            }
            ApplyMapFilter();   // honour any active legend filters for the newly built chips
            RenderCensus(report);
            // The timeline holds animation IDs, and an edit can delete one. Resync recolours every join
            // against the new graph and drops steps the pet no longer has, and hands back how many.
            return _timeline != null ? _timeline.Resync() : 0;
        }

        private Border MakeChip(AnimNode node)
        {
            Brush fill, stroke;
            if (!node.IsReachable) { fill = _theme.DeadFill; stroke = _theme.DeadStroke; }
            else if (node.IsRoot) { fill = _theme.RootFill; stroke = _theme.RootStroke; }
            else { fill = _theme.LiveFill; stroke = _theme.LiveStroke; }

            string label = "#" + node.Id;
            if (!string.IsNullOrEmpty(node.Name))
                label += " " + (node.Name.Length > 14 ? node.Name.Substring(0, 13) + "…" : node.Name);

            // WHAT it does, next to what it is called. Without this, finding a converted pet's jump meant
            // knowing that a Hollow Knight skin calls it "Grapple4" -- the names are the source skin's, and
            // across the corpus a jump is variously jump_up_left, jumping, PullUpShimeji2, Launching,
            // Lay an Egg2 and 引っこ抜く2. Idle carries no badge on purpose, so the map stays quiet and the
            // handful of interesting animations are the ones that stand out.
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label, Foreground = _theme.ChipText, VerticalAlignment = VerticalAlignment.Center });
            string badge = AnimCapabilities.Badge(CapabilityOf(node));
            if (badge.Length > 0)
                content.Children.Add(new Border
                {
                    Background = _theme.HintFill,
                    BorderBrush = _theme.HintStroke,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = badge,
                        Foreground = _theme.ChipText,
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                    },
                });

            var chip = new Border
            {
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = stroke,   // remembered so the selection highlight can be undone
                ToolTip = "#" + node.Id + (string.IsNullOrEmpty(node.Name) ? "" : " " + node.Name) +
                          "\n" + AnimCapabilities.Describe(node, CapabilityOf(node)),
                Child = content,
            };
            chip.MouseLeftButtonUp += delegate { SelectNode(node.Id); };
            // Also a drag source for the behaviour timeline, so the map stays the ONE list of animations.
            // A second list in the timeline pane could disagree with this one after an edit.
            if (_timeline != null) _timeline.MakeDragSource(chip, node.Id);
            return chip;
        }

        // ---- selection detail ----

        private void SelectNode(int id)
        {
            AnimNode node;
            if (!_nodesById.TryGetValue(id, out node)) return;

            _selectedNode = node;
            RefreshPlayOnPreview();
            HighlightChip(id);

            _detailTitle.Text = "#" + node.Id + (string.IsNullOrEmpty(node.Name) ? "" : "  \"" + node.Name + "\"");

            string status;
            if (!node.IsReachable)
            {
                // Explain WHY it never plays — the common case is an animation that is fully built (has frames
                // and its own exits) but that nothing transitions INTO, i.e. it was authored but never wired up.
                status = "Never played — no transition, spawn, or entry point (drag/fall/kill/sync) leads into it.";
                if (node.Frames.Length > 0 || node.Edges.Count > 0)
                    status += " It has " + node.Frames.Length + " frame(s) and " + node.Edges.Count +
                        " exit(s), so it looks complete but was never hooked up.";
            }
            else
            {
                status = (node.IsRoot ? "Entry animation (the engine can start here). " : "") + "Reachable.";
            }
            if (!string.IsNullOrEmpty(node.Action) && node.Action != "none") status += "  Action: " + node.Action + ".";
            _detailStatus.Text = status;
            // The physics in prose, which is the thing the map's name and colour cannot say.
            _capabilityText.Text = AnimCapabilities.Describe(node, CapabilityOf(node));

            RenderFrames(node);
            RenderTransitions(node);
        }

        private void HighlightChip(int id)
        {
            if (_selectedChip != null)
            {
                _selectedChip.BorderBrush = (Brush)_selectedChip.Tag;
                _selectedChip.BorderThickness = new Thickness(1);
            }
            Border chip;
            if (_chipsById.TryGetValue(id, out chip))
            {
                chip.BorderBrush = _theme.Text;
                chip.BorderThickness = new Thickness(2);
                chip.BringIntoView();
                _selectedChip = chip;
            }
        }

        private void RenderFrames(AnimNode node)
        {
            StopPlay();
            _playFrames.Clear();
            _frameStrip.Children.Clear();
            _playIndex = 0;

            bool anyDecoded = false, allBlank = true;
            if (_sprite != null && node.Frames != null)
                foreach (int frameIndex in node.Frames)
                {
                    BitmapSource bmp = _sprite.Frame(frameIndex);
                    if (bmp == null) continue;
                    anyDecoded = true;
                    if (!_sprite.IsBlank(frameIndex)) allBlank = false;
                    _playFrames.Add(bmp);
                    _frameStrip.Children.Add(MakeStripThumb(bmp, _playFrames.Count - 1));
                }

            // Say which tiles play, and — the answer to "why does this show nothing?" — flag a frame that is a
            // fully transparent tile, which the sheet uses to make the pet invisible during a state.
            if (node.Frames == null || node.Frames.Length == 0)
                _framesInfo.Text = "No sprite frames — this animation draws nothing on screen.";
            else
            {
                string list = string.Join(", ", System.Linq.Enumerable.Take(node.Frames, 24));
                if (node.Frames.Length > 24) list += ", …";
                _framesInfo.Text = "Frames: " + list +
                    (anyDecoded && allBlank ? "  — a blank (transparent) tile: the companion is invisible here, so nothing shows." : "");
            }

            if (_playFrames.Count > 0)
            {
                _frameImage.Source = _playFrames[0];
                _framePreviewBox.Visibility = Visibility.Visible;
                _playButton.IsEnabled = _playFrames.Count > 1;
            }
            else
            {
                _frameImage.Source = null;
                _framePreviewBox.Visibility = Visibility.Collapsed;
                _playButton.IsEnabled = false;
            }
        }

        private UIElement MakeStripThumb(BitmapSource bmp, int frameSlot)
        {
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Height = 44 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            var border = new Border
            {
                Background = _theme.PreviewBg,
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = img,
            };
            border.MouseLeftButtonUp += delegate { StopPlay(); _playIndex = frameSlot; _frameImage.Source = bmp; };
            return border;
        }

        private void RenderTransitions(AnimNode node)
        {
            _transitions.Children.Clear();
            if (node.Edges.Count == 0)
            {
                _transitions.Children.Add(new TextBlock { Text = "No outgoing transitions.", Foreground = _theme.Muted });
                return;
            }
            _transitions.Children.Add(new TextBlock { Text = "Goes to:", Foreground = _theme.Muted, Margin = new Thickness(0, 0, 0, 2) });
            foreach (AnimEdge edge in node.Edges)
                _transitions.Children.Add(MakeTransitionRow(edge));
        }

        private UIElement MakeTransitionRow(AnimEdge edge)
        {
            AnimNode target;
            bool known = _nodesById.TryGetValue(edge.To, out target);
            string name = known && !string.IsNullOrEmpty(target.Name) ? " " + target.Name : "";
            string kind = edge.Kind == "sequence" ? "" : "  [" + edge.Kind + "]";
            string prob = edge.Probability <= 0 ? "  never" : "  " + edge.Probability + "%";
            string text = "→ #" + edge.To + name + prob + kind;

            var row = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = edge.Probability <= 0 ? _theme.Muted : _theme.Text,
                Margin = new Thickness(8, 1, 0, 1),
            };
            if (known)
            {
                row.Cursor = System.Windows.Input.Cursors.Hand;
                int to = edge.To;
                row.MouseLeftButtonUp += delegate { SelectNode(to); };
            }
            return row;
        }

        private void ResetDetail()
        {
            StopPlay();
            _selectedChip = null;
            _selectedNode = null;
            RefreshPlayOnPreview();
            _detailTitle.Text = "Nothing selected";
            _detailStatus.Text = "Click a node in the map to inspect it.";
            _capabilityText.Text = "";
            _framesInfo.Text = "";
            _frameStrip.Children.Clear();
            _transitions.Children.Clear();
            _frameImage.Source = null;
            _framePreviewBox.Visibility = Visibility.Collapsed;
            _playButton.IsEnabled = false;
        }

        // ---- frame playback ----

        private void TogglePlay()
        {
            if (_playTimer.IsEnabled) StopPlay();
            else if (_playFrames.Count > 1) { _playTimer.Start(); _playButton.Content = "⏸ Stop"; }
        }

        private void StepPlay()
        {
            if (_playFrames.Count == 0) { StopPlay(); return; }
            _playIndex = (_playIndex + 1) % _playFrames.Count;
            _frameImage.Source = _playFrames[_playIndex];
        }

        private void StopPlay()
        {
            _playTimer.Stop();
            _playButton.Content = "▶ Play";
        }

        // ---- preview / install ----

        private void Preview()
        {
            RemovePreview();
            string error;
            _preview = _pets.SpawnPreview(_editor.Text ?? "", out error);
            if (_preview == null)
            {
                SetStatus("Preview refused: " + error);
                return;
            }
            _removeButton.IsEnabled = true;
            RefreshPlayOnPreview();
            SetStatus("Previewing on your desktop. It is temporary: not saved, not in your companion mix, and gone " +
                "when you close this window. Click an animation in the map and use “Preview highlighted " +
                "action” to make it play that one now.");
        }

        private void RemovePreview()
        {
            ICompanionPreview preview = _preview;
            _preview = null;
            _removeButton.IsEnabled = false;
            RefreshPlayOnPreview();
            if (_timeline != null) _timeline.RunFinished();
            if (preview == null) return;
            try { preview.Remove(); } catch { }
        }

        /// <summary>
        /// Enable "Preview highlighted action" only when it can actually do something: a preview alive on the
        /// desktop, a node selected, and that node NAMED.
        ///
        /// The name is the load-bearing condition and it is easy to miss. IHost.TryPlayAnimation resolves by
        /// name against the running pet's own XML, so an animation the author never named cannot be asked for
        /// at all however clearly the map shows it. Disabling with a reason beats a button that silently does
        /// nothing on one node out of thirteen.
        /// </summary>
        /// <summary>
        /// The enablement RULE on its own, so it can be asserted without a window. Static and parameterised
        /// rather than inlined into RefreshPlayOnPreview, because that method reads four pieces of live WPF
        /// state and a headless self-test cannot reach any of them; the rule is the part worth testing and
        /// the part that would silently invert.
        /// </summary>
        internal static bool CanPlayOnPreview(bool hostPresent, bool previewAlive, string selectedName)
        {
            return hostPresent && previewAlive && !string.IsNullOrWhiteSpace(selectedName);
        }

        private void RefreshPlayOnPreview()
        {
            bool live = _preview != null && _preview.IsAlive && _preview.Pet != null;
            bool named = _selectedNode != null && !string.IsNullOrWhiteSpace(_selectedNode.Name);
            _playOnPreviewButton.IsEnabled = CanPlayOnPreview(_host != null, live, named ? _selectedNode.Name : null);
            _playOnPreviewButton.ToolTip =
                _host == null ? "No host service available." :
                !live ? "Put a preview on your desktop first." :
                _selectedNode == null ? "Click an animation in the map to choose one." :
                !named ? "This animation has no name, so the engine cannot be asked for it by name." :
                "Play “" + _selectedNode.Name + "” on the preview companion now.";
        }

        /// <summary>
        /// Ask the LIVE preview to play the selected animation now, rather than waiting for the pet's own
        /// transition weights to choose it. This pet reaches `jump` on 4 of 43 hub picks, so watching one
        /// happen by chance takes a while, and a fall or a climb needs the pet to be in the right place first.
        ///
        /// The engine takes over again immediately afterwards: this sets the CURRENT animation, and what
        /// follows is whatever that animation's own transitions say. That is the honest behaviour to describe,
        /// because an author who expects the pet to freeze on the pose will otherwise read the handover as a bug.
        /// </summary>
        private void PlaySelectedOnPreview()
        {
            if (_host == null || _selectedNode == null) return;
            if (_preview == null || !_preview.IsAlive || _preview.Pet == null)
            {
                // Reachable by the preview dying between the click and here (the host removes every preview at
                // shutdown), so it refuses rather than dereferencing a stale handle.
                RefreshPlayOnPreview();
                SetStatus("The preview is gone. Put one on your desktop first.");
                return;
            }
            string name = (_selectedNode.Name ?? "").Trim();
            if (name.Length == 0) { SetStatus("That animation has no name, so it cannot be requested by name."); return; }

            bool played;
            try { played = _host.TryPlayAnimation(_preview.Pet, name); }
            catch { played = false; }

            if (!played)
            {
                SetStatus("The preview would not play “" + name + "”. The running companion is built from the " +
                    "XML as it was when you pressed Preview, so re-preview if you have edited it since.");
                return;
            }
            SetStatus("Playing “" + name + "” on the preview. The engine takes over again when it ends, so what " +
                "happens next is whatever that animation's own transitions say.");
        }

        /// <summary>
        /// Run a behaviour chain: spawn the compiled debug pet as the preview.
        ///
        /// It goes through the SAME single preview slot as "Preview on my desktop" on purpose. Two preview
        /// owners would be two things that can each leave a pet behind, and the window's Closed handler can
        /// only clean up one of them.
        /// </summary>
        private bool RunDebugPet(string debugXml)
        {
            if (_pets == null)
            {
                SetStatus("No companion service available — running a chain needs the Companions permission.");
                return false;
            }
            RemovePreview();
            string error;
            _preview = _pets.SpawnPreview(debugXml, out error);
            if (_preview == null)
            {
                SetStatus("The chain would not spawn: " + error);
                return false;
            }
            _removeButton.IsEnabled = true;
            // The chain pet is a DIFFERENT XML (clones wired nose-to-tail), so the map's node names do not
            // exist on it. Refresh anyway rather than leaving the button enabled from a previous preview:
            // the handle it would have used is already gone.
            RefreshPlayOnPreview();
            SetStatus("Running the chain on a temporary companion. Its animations are clones wired nose-to-tail, so " +
                "the engine runs the chain with its own timing and physics — nothing here is simulated.");
            return true;
        }

        private void Install()
        {
            string id = (_installId.Text ?? "").Trim();
            if (id.Length == 0)
            {
                SetStatus("Give the companion an id to install it under (letters, digits, - and _).");
                return;
            }
            string error;
            if (_pets.InstallType(id, _editor.Text ?? "", out error))
            {
                SetStatus("Installed as '" + id + "'. It is now in Options, Companions.");
                return;
            }
            SetStatus("Couldn't install: " + error);
        }

        private void SetStatus(string text)
        {
            _status.Text = text ?? "";
        }

        /// <summary>A sensible default install id: the pet folder's name, which is how pets are laid out on
        /// disk (…\pets\&lt;id&gt;\animations.xml).</summary>
        private static string SuggestId(string path)
        {
            try
            {
                string folder = Path.GetFileName(Path.GetDirectoryName(path));
                return string.IsNullOrWhiteSpace(folder) ? "" : folder;
            }
            catch { return ""; }
        }
    }
}
