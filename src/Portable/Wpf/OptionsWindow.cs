using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// Programmatic (no-XAML) WPF settings / module-manager window (S5b). Renders one left-nav section per
    /// <see cref="OptionsPane"/> — the core Preferences pane plus each module's schema-driven pane — with an
    /// Apply that round-trips values through the pane's own Load/Save (so a module persists to its own store).
    /// The pet stays WinForms; this window is shown modally from the WinForms UI thread. No XAML keeps the
    /// build/packaging unchanged (no BAML/resource wiring); the renderer lives in <see cref="PaneView"/>,
    /// which is constructable headlessly (STA) so the schema-render + Load/Save round-trip is self-testable.
    /// </summary>
    internal sealed class OptionsWindow : Window
    {
        private readonly IReadOnlyList<ShellPane> _panes;
        private readonly string _initialPaneTitle;
        private readonly ContentControl _content = new ContentControl();
        private readonly Button _apply;
        private ShellPane _current;
        private bool _dirty;   // schema-pane has unsaved field edits (drives the Apply/Applied button)

        public OptionsWindow(IReadOnlyList<ShellPane> panes, string initialPaneTitle = null)
        {
            _panes = panes ?? new List<ShellPane>();
            _initialPaneTitle = initialPaneTitle;
            Title = "DesktopAICompanion — Settings";
            // Default large enough for the Pets gallery to reflow to 3 cards across and ~4 rows down
            // (the gallery WrapPanel wraps to fewer columns as the window shrinks). Resizable, with a
            // floor that still fits ~2 columns + the nav.
            Width = 1050;
            Height = 820;
            MinWidth = 700;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WpfTheme.Apply(this);   // light/dark/system per the user's preference; installs implicit styles

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nav = new ListBox { Margin = new Thickness(6) };
            foreach (ShellPane p in _panes) nav.Items.Add(p != null ? (p.Title ?? "(untitled)") : "(null)");
            nav.SelectionChanged += (s, e) => ShowPane(nav.SelectedIndex);
            Grid.SetColumn(nav, 0);
            grid.Children.Add(nav);

            var right = new DockPanel { Margin = new Thickness(6), LastChildFill = true };
            // Bottom bar: the running build version at bottom-left (so "which version am I running?" is
            // answerable at a glance — mirrors the old FormOptions stamp), Apply/Close at bottom-right.
            // Version comes from Application.ProductVersion (ProductVersion.props via the build), never hardcoded;
            // muted grey reads as a hint in both light and dark themes.
            var bottomBar = new DockPanel { LastChildFill = false };
            // When the daily check has seen a newer release, the stamp becomes "1.9.7 → 1.9.8" and opens the
            // releases page; otherwise it stays the muted "v1.9.7" hint. Read from the CACHED result, never a
            // request: opening Preferences must not wait on the network, and must work offline.
            string runningVersion = System.Windows.Forms.Application.ProductVersion;
            string latestKnown = Program.MyData != null ? Program.MyData.GetAppUpdateLatestVersion() : "";
            bool offersUpdate = AppUpdateCheck.OffersUpdate(runningVersion, latestKnown);
            var version = new TextBlock
            {
                Text = AppUpdateCheck.FooterText(runningVersion, latestKnown),
                Foreground = new SolidColorBrush(offersUpdate
                    ? Color.FromRgb(0x4D, 0x9B, 0xE8)      // a link, not a hint: there is something to click
                    : Color.FromRgb(0x80, 0x80, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            if (offersUpdate)
            {
                version.Cursor = System.Windows.Input.Cursors.Hand;
                version.ToolTip = "A newer version is available — open the releases page";
                version.MouseLeftButtonUp += delegate
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = AppUpdateCheck.ReleasesUrl,
                            UseShellExecute = true,
                        });
                    }
                    catch { }
                };
            }
            DockPanel.SetDock(version, Dock.Left);
            bottomBar.Children.Add(version);

            // Opening this window is an explicit "tell me": the footer is the ONLY place an update is ever
            // shown, so asking here — rather than only at launch — is what makes a long-running instance able
            // to notice at all. Fire-and-forget on a short floor; the label is rebuilt in place if the answer
            // arrives and it is news. Never blocks the window opening, and a failure changes nothing.
            RefreshUpdateStampAsync(version, runningVersion);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _apply = new Button { Content = "_Apply", Width = 84, Height = 26, Margin = new Thickness(4) };
            _apply.Click += (s, e) => ApplyCurrent();
            var close = new Button { Content = "_Close", Width = 84, Height = 26, Margin = new Thickness(4) };
            close.Click += (s, e) => Close();
            buttons.Children.Add(_apply);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Right);
            bottomBar.Children.Add(buttons);
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            right.Children.Add(bottomBar);
            // Each pane supplies its own ScrollViewer (schema panes via PaneView, Pets via its control),
            // so there is no OUTER ScrollViewer for an inner one to nest inside and swallow the mouse wheel.
            right.Children.Add(_content);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            Content = grid;
            int initialIndex = 0;
            if (!string.IsNullOrEmpty(_initialPaneTitle))
                for (int i = 0; i < _panes.Count; i++)
                    if (_panes[i] != null && string.Equals(_panes[i].Title, _initialPaneTitle, StringComparison.OrdinalIgnoreCase))
                    { initialIndex = i; break; }
            if (_panes.Count > 0) nav.SelectedIndex = initialIndex;
        }

        private void ShowPane(int index)
        {
            if (index < 0 || index >= _panes.Count) { _content.Content = null; _current = null; if (_apply != null) _apply.Visibility = Visibility.Collapsed; return; }
            _current = _panes[index];
            // Let a pane ask to be rebuilt after an action runs (e.g. "reset to defaults" → show new values).
            _current.RequestReload = delegate { ShowPane(index); };
            // A field edit in the pane enables the Apply button (it starts disabled = nothing to apply).
            _current.NotifyDirty = delegate { SetDirty(true); };
            FrameworkElement content;
            try { content = _current.BuildContent(); }
            catch (Exception ex) { content = new TextBlock { Text = "This pane failed to load: " + ex.Message, Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap }; }
            _content.Content = content;
            // Schema panes have an Apply; custom panes (Pets/Fortunes) apply through their own controls.
            if (_apply != null) _apply.Visibility = _current.HasApply ? Visibility.Visible : Visibility.Collapsed;
            SetDirty(false);   // freshly built pane: nothing unsaved, so Apply is greyed out until a change
        }

        // Apply is greyed out until a field changes, and greys out again after a successful Apply.
        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            if (_apply != null) _apply.IsEnabled = dirty;
        }

        /// <summary>
        /// Ask whether a newer version exists and update the footer in place if so. Deliberately not awaited:
        /// the settings window must open at the same speed whether or not GitHub answers.
        /// </summary>
        private static async void RefreshUpdateStampAsync(TextBlock label, string runningVersion)
        {
            try
            {
                if (Program.MyData == null) return;
                bool offers = await AppUpdateCheck.MaybeCheckAsync(
                    Program.MyData, runningVersion, AppUpdateCheck.InteractiveInterval,
                    System.Threading.CancellationToken.None).ConfigureAwait(true);
                if (!offers || label == null) return;
                string latest = Program.MyData.GetAppUpdateLatestVersion();
                if (!AppUpdateCheck.OffersUpdate(runningVersion, latest)) return;
                // Already showing it (the cached answer was current) — nothing to redraw.
                string text = AppUpdateCheck.FooterText(runningVersion, latest);
                if (string.Equals(label.Text, text, StringComparison.Ordinal)) return;
                label.Text = text;
                label.Foreground = new SolidColorBrush(Color.FromRgb(0x4D, 0x9B, 0xE8));
                label.Cursor = System.Windows.Input.Cursors.Hand;
                label.ToolTip = "A newer version is available — open the releases page";
                label.MouseLeftButtonUp += delegate
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = AppUpdateCheck.ReleasesUrl,
                            UseShellExecute = true,
                        });
                    }
                    catch { }
                };
            }
            catch { }
        }

        private void ApplyCurrent()
        {
            if (_current == null || !_current.HasApply) return;
            bool ok;
            try { ok = _current.Apply(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ok)
                MessageBox.Show(this, "These settings could not be saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
            {
                SetDirty(false);   // saved: nothing left to apply, grey Apply out again
                // Rebuild when the pane shows derived status (e.g. the fortune pool count), so the number
                // reflects the settings just saved instead of the ones from when the pane opened.
                if (_current.RefreshAfterApply && _current.RequestReload != null) _current.RequestReload();
            }
        }
    }

    /// <summary>A section in the settings window: either a schema-driven module pane (<see cref="SchemaShellPane"/>)
    /// or a host-built custom control (<see cref="CustomShellPane"/> — the Pets gallery, the Fortunes tree).
    /// Host-only; the plugin ABI stays schema-only + framework-agnostic (no WPF types leak into it).</summary>
    internal abstract class ShellPane
    {
        public abstract string Title { get; }
        public abstract FrameworkElement BuildContent();
        public virtual bool HasApply { get { return false; } }
        public virtual bool Apply() { return true; }
        // True when the pane shows display-only (Info) values derived from the settings being saved — those
        // are stale the moment Apply succeeds, so the window rebuilds the pane to re-run Load(). Panes with
        // no Info field are left alone, so applying doesn't needlessly reset scroll/focus.
        public virtual bool RefreshAfterApply { get { return false; } }
        // Set by the window before BuildContent: invoke to rebuild this pane (refreshes Load() values).
        public Action RequestReload { get; set; }
        // Set by the window before BuildContent: invoke when a field edit makes the pane dirty (enables Apply).
        public Action NotifyDirty { get; set; }
    }

    /// <summary>Wraps a plugin-ABI OptionsPane, rendered by the schema PaneView.</summary>
    internal sealed class SchemaShellPane : ShellPane
    {
        private readonly OptionsPane _pane;
        private PaneView _view;
        public SchemaShellPane(OptionsPane pane) { _pane = pane; }
        public override string Title { get { return _pane != null ? (_pane.Title ?? "(untitled)") : "(null)"; } }
        public override FrameworkElement BuildContent() { _view = new PaneView(_pane, RequestReload, NotifyDirty); return _view.Build(); }
        public override bool HasApply { get { return _pane != null && _pane.Save != null; } }
        public override bool Apply() { return _view != null && _view.Save(); }
        public override bool RefreshAfterApply
        {
            get
            {
                if (_pane == null || _pane.Schema == null) return false;
                foreach (SettingField f in _pane.Schema)
                    if (f != null && f.Kind == SettingKind.Info) return true;
                return false;
            }
        }
    }

    /// <summary>A host-built pane that supplies its own WPF control (applies through its own buttons).</summary>
    internal sealed class CustomShellPane : ShellPane
    {
        private readonly string _title;
        private readonly Func<FrameworkElement> _build;
        public CustomShellPane(string title, Func<FrameworkElement> build) { _title = title; _build = build; }
        public override string Title { get { return _title ?? "(untitled)"; } }
        public override FrameworkElement BuildContent() { return _build != null ? _build() : new TextBlock(); }
    }

    /// <summary>
    /// Renders one <see cref="OptionsPane"/>'s schema into WPF controls and collects/persists edited values
    /// via the pane's Load/Save. Kept separate + headless-constructable (STA) so the render + Load/Save
    /// round-trip is unit-testable without showing a window. Secret fields are write-only: the box starts
    /// empty (a "leave blank to keep the current one" hint when a secret is already set) and only a
    /// non-empty entry is sent back on Save.
    /// </summary>
    /// <summary>
    /// A small masonry (column-balancing) panel: children flow into a responsive number of equal-width
    /// columns, and each child is placed in the currently-shortest column. Unlike a WrapPanel — rigid rows
    /// where a short card sitting next to a tall one stretches into a big empty box — this packs cards of
    /// differing heights so the columns stay roughly level (small setting cards naturally stack together).
    /// Column count is derived from the available width, so it reflows as the window resizes.
    /// </summary>
    internal sealed class MasonryPanel : Panel
    {
        /// <summary>Column pitch = a card's width + inter-column gap; cards are left-aligned in each slot.</summary>
        public double ColumnWidth { get; set; } = 368;

        /// <summary>
        /// Set on a child to make it span the whole panel instead of taking one column
        /// (<see cref="SettingField.FullWidth"/>). Attached rather than a property of the card, because the
        /// panel is the only thing that knows how many columns there are: a card cannot size itself to a
        /// column count it never sees, and the previous "just make it wider" answer produced a card that
        /// overhung its neighbour at every window width except the one it was tuned for.
        /// </summary>
        public static readonly DependencyProperty SpanAllColumnsProperty =
            DependencyProperty.RegisterAttached(
                "SpanAllColumns", typeof(bool), typeof(MasonryPanel),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static void SetSpanAllColumns(UIElement element, bool value)
        {
            if (element != null) element.SetValue(SpanAllColumnsProperty, value);
        }

        public static bool GetSpanAllColumns(UIElement element)
        {
            return element != null && (bool)element.GetValue(SpanAllColumnsProperty);
        }

        private int ColumnCount(double availableWidth)
        {
            if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0) return 1;
            return Math.Max(1, (int)(availableWidth / ColumnWidth));
        }

        private static int ShortestColumn(double[] heights)
        {
            int min = 0;
            for (int i = 1; i < heights.Length; i++) if (heights[i] < heights[min]) min = i;
            return min;
        }

        private static double Tallest(double[] heights)
        {
            double max = 0;
            foreach (double h in heights) if (h > max) max = h;
            return max;
        }

        // A spanning child starts below everything placed so far (Tallest) and leaves every column level
        // with its bottom. Anything else would let a later one-column card slide up beside it and overlap:
        // the columns are tracked as bare running heights, with no notion of a gap to fill.
        protected override Size MeasureOverride(Size availableSize)
        {
            int cols = ColumnCount(availableSize.Width);
            var colHeights = new double[cols];
            // A spanning child is measured against the whole panel, so an infinite width (a measure pass
            // with no constraint) has to resolve to the same nominal width the return value reports, or the
            // card would report a height for a width it is never given.
            double fullWidth = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width) || availableSize.Width <= 0
                ? cols * ColumnWidth
                : availableSize.Width;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                if (GetSpanAllColumns(child))
                {
                    child.Measure(new Size(fullWidth, double.PositiveInfinity));
                    double bottom = Tallest(colHeights) + child.DesiredSize.Height;
                    for (int i = 0; i < cols; i++) colHeights[i] = bottom;
                    continue;
                }
                child.Measure(new Size(ColumnWidth, double.PositiveInfinity));
                int c = ShortestColumn(colHeights);
                colHeights[c] += child.DesiredSize.Height;
            }
            return new Size(fullWidth, Tallest(colHeights));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols = ColumnCount(finalSize.Width);
            var colHeights = new double[cols];
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                if (GetSpanAllColumns(child))
                {
                    double top = Tallest(colHeights);
                    child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
                    double bottom = top + child.DesiredSize.Height;
                    for (int i = 0; i < cols; i++) colHeights[i] = bottom;
                    continue;
                }
                int c = ShortestColumn(colHeights);
                child.Arrange(new Rect(c * ColumnWidth, colHeights[c], child.DesiredSize.Width, child.DesiredSize.Height));
                colHeights[c] += child.DesiredSize.Height;
            }
            return finalSize;
        }
    }

    internal sealed class PaneView
    {
        private readonly OptionsPane _pane;
        private readonly Action _requestReload;
        private readonly Action _notifyDirty;
        private bool _suppressDirty;   // true while Build() sets initial control values (so they don't count as edits)
        private bool _syncingGroup;    // true while a group header checkbox drives its children (stops the feedback loop)
        private readonly PendingCheckSet _pendingChecks = new PendingCheckSet();

        /// <summary>
        /// Checkbox edits on <see cref="ListCard.DeferChanges"/> cards, held until Apply. Insertion-ordered,
        /// so a flush replays the clicks in the order they were made. Lives on the PaneView, which is rebuilt
        /// whenever the pane is — that is what makes closing the window (or a ReloadPaneAfter action) discard
        /// unapplied ticks, the same as it already does for unapplied field edits.
        /// </summary>
        private sealed class PendingCheckSet
        {
            private sealed class Entry { public ListCard Card; public string Id; public bool Value; }
            private readonly List<Entry> _entries = new List<Entry>();

            public void Set(ListCard card, string id, bool value)
            {
                Entry e = Find(card, id);
                if (e != null) { e.Value = value; return; }
                _entries.Add(new Entry { Card = card, Id = id, Value = value });
            }

            public void Remove(ListCard card, string id)
            {
                Entry e = Find(card, id);
                if (e != null) _entries.Remove(e);
            }

            /// <summary>Hand every staged edit to its card, then forget them. Cleared even on a callback
            /// throw, so a failed Apply can't replay the same edit twice on the next one.</summary>
            public void Flush()
            {
                foreach (Entry e in _entries)
                {
                    if (e.Card.SetChecked == null) continue;
                    try { e.Card.SetChecked(e.Id, e.Value); } catch { }
                }
                _entries.Clear();
            }

            internal int Count { get { return _entries.Count; } }

            private Entry Find(ListCard card, string id)
            {
                foreach (Entry e in _entries)
                    if (ReferenceEquals(e.Card, card) && string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
                return null;
            }
        }
        private readonly Dictionary<string, Func<string>> _readers = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _secretIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// One re-evaluation closure per <see cref="SettingField.EnabledWhen"/> dependent. Run once at the
        /// end of Build and again after every edit, rather than wired field-to-field: a dependent is allowed
        /// to name a field declared LATER in the schema, whose reader does not exist yet at the moment the
        /// dependent's row is built, and a module author has no reason to expect declaration order to matter.
        /// </summary>
        private readonly List<Action> _enableUpdaters = new List<Action>();

        /// <summary>Each field's row element by id. EnabledWhen greys the whole row, and this is also the
        /// only handle on a named field the self-test has: the rendered tree carries no field identity, so
        /// "some TextBox somewhere is disabled" would pass for the wrong field.</summary>
        private readonly Dictionary<string, FrameworkElement> _rows = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);

        /// <summary>What Build() started from, so an EnabledWhen can still read a field that has no editor
        /// and therefore no reader (Info, Header, or an id that is not in the schema at all).</summary>
        private IReadOnlyDictionary<string, string> _loaded = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// On-screen values handed from the view being torn down to the one the host builds in its place,
        /// for a <see cref="SettingField.ReloadOnChange"/> cascade. It cannot travel on the instance: the
        /// host answers RequestReload by constructing a FRESH PaneView, so the only thing the two share is
        /// the pane. One slot, keyed by that pane and emptied by the first Build that asks, because a slot
        /// left full would feed one open's unapplied edits into an unrelated later one.
        ///
        /// Deliberately not filled by the other two rebuild paths. A ReloadPaneAfter action (reset to
        /// defaults) rebuilds precisely to show what it just WROTE, and the post-Apply refresh likewise, so
        /// handing either the pre-rebuild screen state would show the user the values they just replaced.
        /// </summary>
        [ThreadStatic] private static OptionsPane _rebuildPane;
        [ThreadStatic] private static Dictionary<string, string> _rebuildValues;

        /// <summary>
        /// What a <see cref="PaneAction.ReloadPaneAfter"/> rebuild has to carry across, because the
        /// rebuild replaces the whole visual tree and the view instance with it.
        ///
        /// Two things were being thrown away, and both looked to the user like the button did nothing.
        /// The RESULT MESSAGE was written to a TextBlock that the reload then discarded, so "Find an
        /// installed Whisper" reported success into a control already on its way out -- the only
        /// channel that action has. And the user's UNSAVED EDITS went with it: pick a recording
        /// device, click any action button, and the choice is gone and Apply is grey again, with no
        /// error and nothing written.
        /// </summary>
        internal sealed class ActionRebuild
        {
            /// <summary>What Load() said when the torn-down view was built.</summary>
            public IReadOnlyDictionary<string, string> Before;
            /// <summary>What was on screen when the action finished, edits and all.</summary>
            public IReadOnlyDictionary<string, string> OnScreen;
            /// <summary>Action label to the message it produced, shown again on the rebuilt row.</summary>
            public Dictionary<string, string> Messages;
        }

        [ThreadStatic] private static OptionsPane _actionPane;
        [ThreadStatic] private static ActionRebuild _actionRebuild;

        /// <summary>Messages to redisplay on this build's action rows; null on an ordinary build.</summary>
        private Dictionary<string, string> _actionMessages;

        public PaneView(OptionsPane pane, Action requestReload = null, Action notifyDirty = null)
        {
            _pane = pane; _requestReload = requestReload; _notifyDirty = notifyDirty;
        }

        private static void StashPendingRebuildValues(OptionsPane pane, Dictionary<string, string> values)
        {
            _rebuildPane = pane; _rebuildValues = values;
        }

        private static Dictionary<string, string> TakePendingRebuildValues(OptionsPane pane)
        {
            OptionsPane stashedFor = _rebuildPane;
            Dictionary<string, string> stashed = _rebuildValues;
            _rebuildPane = null; _rebuildValues = null;   // emptied even on a mismatch, so nothing lingers
            return (pane != null && ReferenceEquals(stashedFor, pane)) ? stashed : null;
        }

        private static void StashActionRebuild(OptionsPane pane, ActionRebuild rebuild)
        {
            _actionPane = pane; _actionRebuild = rebuild;
        }

        private static ActionRebuild TakeActionRebuild(OptionsPane pane)
        {
            OptionsPane stashedFor = _actionPane;
            ActionRebuild stashed = _actionRebuild;
            _actionPane = null; _actionRebuild = null;   // one slot, emptied by the first asker
            return (pane != null && ReferenceEquals(stashedFor, pane)) ? stashed : null;
        }

        /// <summary>
        /// What the rebuilt pane should show after a <see cref="PaneAction.ReloadPaneAfter"/> action:
        /// the action's writes, with the user's unsaved edits put back on top of the fields the
        /// action did not touch.
        ///
        /// The precedence is the whole point, and it is decided per FIELD rather than per pane. An
        /// action that reloads does so precisely to show what it just wrote ("reset to defaults",
        /// "Browse for whisper-cli"), so wherever the fresh Load disagrees with what the torn-down
        /// view started from, the action wrote it and the action wins. Everywhere else a difference
        /// can only have come from the user, and dropping it is silent data loss -- which is what
        /// used to happen to every field on the pane, not just the ones the action cared about.
        ///
        /// <paramref name="restored"/> counts the fields handed back to the user, so the caller can
        /// re-raise the unsaved-edit signal the rebuild is about to clear.
        /// </summary>
        internal static Dictionary<string, string> MergeAfterAction(
            IReadOnlyDictionary<string, string> fresh,
            IReadOnlyDictionary<string, string> before,
            IReadOnlyDictionary<string, string> onScreen,
            out int restored)
        {
            restored = 0;
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fresh != null)
                foreach (KeyValuePair<string, string> kv in fresh) merged[kv.Key] = kv.Value;
            if (before == null || onScreen == null) return merged;

            foreach (KeyValuePair<string, string> kv in onScreen)
            {
                string wasLoaded;
                if (!before.TryGetValue(kv.Key, out wasLoaded)) continue;   // no baseline, no claim
                if (string.Equals(kv.Value ?? "", wasLoaded ?? "", StringComparison.Ordinal))
                    continue;                                                // the user left it alone

                string nowLoaded;
                if (!merged.TryGetValue(kv.Key, out nowLoaded)) continue;    // gone from the schema
                if (!string.Equals(nowLoaded ?? "", wasLoaded ?? "", StringComparison.Ordinal))
                    continue;                                                // the action wrote it too

                merged[kv.Key] = kv.Value;
                restored++;
            }
            return merged;
        }

        /// <summary>Whether anything on screen differs from what Load() supplied for this build.</summary>
        private bool HasUnsavedEdits()
        {
            if (_loaded == null) return false;
            foreach (KeyValuePair<string, string> kv in Collect())
            {
                string wasLoaded;
                if (!_loaded.TryGetValue(kv.Key, out wasLoaded)) continue;
                if (!string.Equals(kv.Value ?? "", wasLoaded ?? "", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // A genuine user edit to a field; ignored while Build() is populating initial values.
        private void Dirty() { if (!_suppressDirty && _notifyDirty != null) _notifyDirty(); }

        /// <summary>
        /// One field's editor changed. Everything that has to happen per edit funnels through here so the
        /// order is fixed in one place, which matters for the reload: the host greys Apply out at the END of
        /// a rebuild, so re-raising the unsaved-edit signal has to happen after RequestReload returns or the
        /// user is left looking at their new value with no way to save it.
        /// </summary>
        private void FieldChanged(SettingField f)
        {
            if (_suppressDirty) return;   // Build() populating initial control values is not an edit
            Dirty();
            RefreshEnabledStates();
            if (f == null || !f.ReloadOnChange || _requestReload == null) return;
            StashPendingRebuildValues(_pane, Collect());
            _requestReload();
            Dirty();
        }

        /// <summary>
        /// The value a field's editor is showing right now. Falls back to what Load supplied for ids with no
        /// reader (Info and Header register none, and a module may name a field it did not declare), so an
        /// EnabledWhen against one of those compares against something real instead of silently reading "".
        /// </summary>
        private string CurrentValueOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            Func<string> reader;
            if (_readers.TryGetValue(id, out reader) && reader != null)
            {
                try { return reader() ?? ""; } catch { return ""; }
            }
            string loaded;
            if (_loaded != null && _loaded.TryGetValue(id, out loaded)) return loaded ?? "";
            return "";
        }

        /// <summary>Whether a field's EnabledWhen (format "otherFieldId=value") is currently satisfied.
        /// Compared case-insensitively: a Bool reader emits lowercase "true"/"false" and an author writes
        /// "True" about as often, and every other kind stores a literal the module chose itself.</summary>
        private bool IsEnabledNow(SettingField f)
        {
            if (f == null || string.IsNullOrEmpty(f.EnabledWhen)) return true;
            int eq = f.EnabledWhen.IndexOf('=');
            if (eq <= 0) return true;   // no id, or no separator: unparseable, so it constrains nothing
            string otherId = f.EnabledWhen.Substring(0, eq).Trim();
            // The value side is TRIMMED too. It was not, while the id side was, so
            // "mode = notify" compared against " notify" and the row stayed greyed for
            // ever -- which reads as a layout bug rather than as a typo, and is exactly
            // the trap an asymmetry like that sets for the next author.
            //
            // A '|' separated SET is accepted, because a dependent field often belongs to
            // more than one state: AgentFlow's notify settings are live in both Notify and
            // Auto-approve mode, and greying them in one of those told the user they were
            // inert when the module was still reading them.
            string wanted = f.EnabledWhen.Substring(eq + 1);
            string actual = CurrentValueOf(otherId);
            foreach (string option in wanted.Split('|'))
                if (string.Equals(actual, option.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void RefreshEnabledStates()
        {
            foreach (Action update in _enableUpdaters)
            {
                try { update(); } catch { }
            }
        }

        /// <summary>A field's rendered row, for the self-test. See <see cref="_rows"/>.</summary>
        internal FrameworkElement RowFor(string fieldId)
        {
            FrameworkElement row;
            return (fieldId != null && _rows.TryGetValue(fieldId, out row)) ? row : null;
        }

        public FrameworkElement Build()
        {
            _readers.Clear();
            _secretIds.Clear();
            _enableUpdaters.Clear();
            _rows.Clear();
            _suppressDirty = true;   // populating initial control values below must not mark the pane dirty

            // Asked for unconditionally, even by a pane that will not use it: see StashPendingRebuildValues.
            Dictionary<string, string> pending = TakePendingRebuildValues(_pane);

            // Whichever of these runs, it runs BEFORE _pane.Schema is read below, and must keep doing so:
            // the core Preferences pane rebuilds a field's Options from inside Load (OptionsShell's "who
            // speaks" dropdown is filled from the pets actually on screen), so reading the schema first
            // would render the previous open's list.
            IReadOnlyDictionary<string, string> values = null;
            if (_pane != null && _pane.LoadPending != null)
            {
                // LoadPending REPLACES Load for a module that supplies one, rather than running alongside
                // it: two sources for the same value would make "which one wins" a per-field accident. A
                // first build has nothing on screen yet and so passes the empty dictionary, which is the
                // same "nothing pending, answer from what is stored" case the module already handles.
                if (pending == null) pending = new Dictionary<string, string>(StringComparer.Ordinal);
                try { values = _pane.LoadPending(pending); } catch { values = null; }
            }
            else
            {
                try { if (_pane != null && _pane.Load != null) values = _pane.Load(); } catch { values = null; }
            }
            if (values == null) values = new Dictionary<string, string>();

            // A ReloadPaneAfter action just ran: put the user's unsaved edits back over the fields it
            // did not itself write, and remember what it reported so the rebuilt row can say it again.
            ActionRebuild afterAction = TakeActionRebuild(_pane);
            _actionMessages = null;
            if (afterAction != null)
            {
                int restored;
                values = MergeAfterAction(values, afterAction.Before, afterAction.OnScreen, out restored);
                _actionMessages = afterAction.Messages;
            }
            _loaded = values;

            // Bucket fields + actions by Group (first-appearance order; null/"" = an untitled default card).
            var order = new List<string>();
            var groupFields = new Dictionary<string, List<SettingField>>(StringComparer.Ordinal);
            var groupActions = new Dictionary<string, List<PaneAction>>(StringComparer.Ordinal);
            IReadOnlyList<SettingField> schema = _pane != null ? _pane.Schema : null;
            if (schema != null)
                foreach (SettingField f in schema)
                {
                    if (f == null || string.IsNullOrEmpty(f.Id)) continue;
                    string g = f.Group ?? "";
                    if (!groupFields.ContainsKey(g)) { groupFields[g] = new List<SettingField>(); groupActions[g] = new List<PaneAction>(); order.Add(g); }
                    groupFields[g].Add(f);
                }
            IReadOnlyList<PaneAction> actions = _pane != null ? _pane.Actions : null;
            if (actions != null)
                foreach (PaneAction a in actions)
                {
                    if (a == null || a.InvokeAsync == null) continue;
                    string g = a.Group ?? "";
                    if (!groupFields.ContainsKey(g)) { groupFields[g] = new List<SettingField>(); groupActions[g] = new List<PaneAction>(); order.Add(g); }
                    groupActions[g].Add(a);
                }

            // FullWidth and PinTop are card-level, and read from the group's FIRST field only. A card cannot
            // be half wide, so letting any member vote would make the answer depend on schema order in a way
            // the module author never sees; the first field is the one they can point at. A group built only
            // from actions has no first field and therefore gets neither.
            //
            // Pinned cards move ahead of the rest as a STABLE partition, so a module that pins two cards
            // still controls which of the two comes first, and the unpinned ones keep the order they were
            // declared in. Dynamic list cards are appended after all of these, as before.
            var pinnedFirst = new List<string>();
            var unpinned = new List<string>();
            foreach (string g in order)
            {
                if (FirstFieldOf(groupFields[g]) != null && FirstFieldOf(groupFields[g]).PinTop) pinnedFirst.Add(g);
                else unpinned.Add(g);
            }
            pinnedFirst.AddRange(unpinned);
            order = pinnedFirst;

            // Each group renders as a titled card; cards flow into responsive columns via a masonry panel
            // (each card drops into the shortest column) so a small card next to a tall one doesn't leave a gap.
            var cards = new MasonryPanel { Margin = new Thickness(4) };
            foreach (string g in order)
            {
                var inner = new StackPanel();
                if (!string.IsNullOrEmpty(g))
                    inner.Children.Add(new TextBlock { Text = g, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
                foreach (SettingField f in groupFields[g])
                {
                    string cur;
                    if (!values.TryGetValue(f.Id, out cur)) cur = "";
                    inner.Children.Add(BuildRow(f, cur ?? ""));
                }
                if (groupActions[g].Count > 0)
                {
                    // Action buttons (S5b): the schema is data-only, so things a module DOES (test a
                    // connection, clear history, ...) render as async buttons with a status line.
                    if (groupFields[g].Count > 0) inner.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 4) });
                    foreach (PaneAction a in groupActions[g]) inner.Children.Add(BuildActionRow(a));
                }
                SettingField lead = FirstFieldOf(groupFields[g]);
                cards.Children.Add(NewCard(inner, lead != null && lead.FullWidth));
            }

            // Dynamic list cards (checkable item lists a flat schema can't express, e.g. fortune packs/genres).
            IReadOnlyList<ListCard> lists = _pane != null ? _pane.Lists : null;
            if (lists != null)
                foreach (ListCard lc in lists)
                    if (lc != null) cards.Children.Add(BuildListCard(lc));

            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(new TextBlock
            {
                Text = _pane != null ? (_pane.Title ?? "") : "",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(4, 0, 0, 6),
            });
            root.Children.Add(cards);
            // Now that every reader exists, settle the EnabledWhen states once. Doing it as each row was
            // built would leave a dependent that names a later field reading nothing and starting enabled.
            RefreshEnabledStates();
            _suppressDirty = false;   // initial values are in place; from here real edits mark the pane dirty
            // Own ScrollViewer so the pane scrolls (incl. the mouse wheel) without an outer one to nest in.
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root,
            };
        }

        /// <summary>The group's first field, or null for a group that is nothing but action buttons. The
        /// card-level flags (<see cref="SettingField.FullWidth"/>, <see cref="SettingField.PinTop"/>) are
        /// read from it and from nowhere else.</summary>
        private static SettingField FirstFieldOf(List<SettingField> fields)
        {
            return (fields != null && fields.Count > 0) ? fields[0] : null;
        }

        // The shared titled-card chrome, used by both schema-group cards and dynamic list cards.
        //
        // A full-width card leaves Width UNSET instead of setting a bigger number. The fixed 360 is half of
        // the story: the other half is the 4px margin on each side, which the masonry panel's 368 column
        // pitch is built around. A card that spans n columns is 368n minus that same gutter, and only the
        // panel knows n, so the card stretches into the slot the panel gives it rather than guessing.
        private static Border NewCard(UIElement child, bool fullWidth = false)
        {
            var card = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4),
                Padding = new Thickness(8),
                Child = child,
            };
            if (fullWidth) card.HorizontalAlignment = HorizontalAlignment.Stretch;
            else card.Width = 360;
            MasonryPanel.SetSpanAllColumns(card, fullWidth);
            return card;
        }

        // Render a ListCard: a titled card with a scrollable list of checkboxes (label + optional detail)
        // that toggle live via SetChecked, plus any card-level action buttons. An empty list shows EmptyHint.
        private Border BuildListCard(ListCard lc)
        {
            var inner = new StackPanel();
            if (!string.IsNullOrEmpty(lc.Title))
                inner.Children.Add(new TextBlock { Text = lc.Title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });

            IReadOnlyList<ListItem> items = null;
            try { if (lc.LoadItems != null) items = lc.LoadItems(); } catch { items = null; }

            if (items == null || items.Count == 0)
            {
                inner.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(lc.EmptyHint) ? "Nothing here yet." : lc.EmptyHint,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                });
            }
            else
            {
                // One checkbox per item, built once and reused by the filter (rebuilding on every keystroke
                // would drop the live checked state the module tracks between pane reloads).
                var rows = new List<KeyValuePair<ListItem, CheckBox>>();
                foreach (ListItem it in items)
                {
                    if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                    string text = it.Label ?? it.Id;
                    if (!string.IsNullOrEmpty(it.Detail)) text += "   " + it.Detail;
                    // Set IsChecked in the initializer (before wiring events) so building the card doesn't
                    // fire SetChecked for the initial state — only genuine user clicks call back.
                    var cb = new CheckBox { Content = text, IsChecked = it.Checked, Margin = new Thickness(0, 2, 0, 2), Tag = it.Id };
                    if (lc.SetChecked != null)
                    {
                        bool wasChecked = it.Checked;
                        Action<bool> set;
                        if (lc.DeferChanges)
                        {
                            // Staged: the box moves now, the module hears about it at Apply. Re-ticking back
                            // to the loaded state drops the entry entirely, so applying never re-does work
                            // for an item the user only passed through.
                            set = delegate(bool v)
                            {
                                if (v == wasChecked) _pendingChecks.Remove(lc, (string)cb.Tag);
                                else _pendingChecks.Set(lc, (string)cb.Tag, v);
                                Dirty();
                            };
                        }
                        else
                        {
                            set = delegate(bool v) { try { lc.SetChecked((string)cb.Tag, v); } catch { } };
                        }
                        cb.Checked += delegate { set(true); };
                        cb.Unchecked += delegate { set(false); };
                    }
                    rows.Add(new KeyValuePair<ListItem, CheckBox>(it, cb));
                }

                bool grouped = false;
                foreach (KeyValuePair<ListItem, CheckBox> r in rows)
                    if (!string.IsNullOrWhiteSpace(r.Key.Group)) { grouped = true; break; }

                var listPanel = new StackPanel();
                // Expanders by group (preserving first-seen group order) so filtering can re-show them.
                var groupExpanders = new List<KeyValuePair<Expander, List<CheckBox>>>();
                if (!grouped)
                {
                    foreach (KeyValuePair<ListItem, CheckBox> r in rows) listPanel.Children.Add(r.Value);
                }
                else
                {
                    var order = new List<string>();
                    var byGroup = new Dictionary<string, List<KeyValuePair<ListItem, CheckBox>>>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<ListItem, CheckBox> r in rows)
                    {
                        string g = string.IsNullOrWhiteSpace(r.Key.Group) ? "Other" : r.Key.Group.Trim();
                        List<KeyValuePair<ListItem, CheckBox>> bucket;
                        if (!byGroup.TryGetValue(g, out bucket))
                        {
                            bucket = new List<KeyValuePair<ListItem, CheckBox>>();
                            byGroup[g] = bucket;
                            order.Add(g);
                        }
                        bucket.Add(r);
                    }
                    order.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string g in order)
                    {
                        List<KeyValuePair<ListItem, CheckBox>> bucket = byGroup[g];
                        var groupPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                        var boxes = new List<CheckBox>();
                        foreach (KeyValuePair<ListItem, CheckBox> r in bucket) { groupPanel.Children.Add(r.Value); boxes.Add(r.Value); }
                        // Header = a whole-group checkbox + the label. Without it, turning off a section
                        // (e.g. 19 NSFW packs) means 19 individual clicks. A plain string header would also
                        // render with Expander's own unthemed foreground, unreadable on the dark card; a
                        // TextBlock picks up the theme's implicit style.
                        var header = new StackPanel { Orientation = Orientation.Horizontal };
                        var groupCheck = new CheckBox
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 0),
                            ToolTip = "Turn this whole group on or off",
                        };
                        header.Children.Add(groupCheck);
                        header.Children.Add(new TextBlock
                        {
                            Text = g + "  (" + bucket.Count + ")",
                            FontWeight = FontWeights.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center,
                        });

                        // Reflect the children: all on = checked, none = unchecked, mixed = indeterminate.
                        // IsThreeState stays FALSE so a user click is a simple on/off (the null state is
                        // only ever set in code); _syncingGroup stops the two directions fighting.
                        List<CheckBox> groupBoxes = boxes;
                        Action refreshGroupCheck = delegate
                        {
                            int on = 0;
                            foreach (CheckBox cb in groupBoxes) if (cb.IsChecked == true) on++;
                            _syncingGroup = true;
                            groupCheck.IsChecked = on == 0 ? (bool?)false : (on == groupBoxes.Count ? (bool?)true : null);
                            _syncingGroup = false;
                        };
                        refreshGroupCheck();
                        foreach (CheckBox cb in groupBoxes)
                        {
                            cb.Checked += delegate { if (!_syncingGroup) refreshGroupCheck(); };
                            cb.Unchecked += delegate { if (!_syncingGroup) refreshGroupCheck(); };
                        }
                        groupCheck.Click += delegate(object sender, RoutedEventArgs e)
                        {
                            // Handled: otherwise the click bubbles to the Expander's toggle and also
                            // expands/collapses the section the user was only trying to tick.
                            e.Handled = true;
                            bool target = groupCheck.IsChecked == true;
                            _syncingGroup = true;
                            // Each child's own Checked/Unchecked still fires, so the module's SetChecked
                            // runs per item exactly as if they had been clicked individually.
                            foreach (CheckBox cb in groupBoxes)
                                if ((cb.IsChecked == true) != target) cb.IsChecked = target;
                            _syncingGroup = false;
                            refreshGroupCheck();
                        };

                        var expander = new Expander
                        {
                            Header = header,
                            IsExpanded = !lc.CollapseGroups,
                            Content = groupPanel,
                            Margin = new Thickness(0, 2, 0, 2),
                        };
                        listPanel.Children.Add(expander);
                        groupExpanders.Add(new KeyValuePair<Expander, List<CheckBox>>(expander, boxes));
                    }
                }

                if (lc.Filterable)
                {
                    var filterBox = new TextBox { Margin = new Thickness(0, 0, 0, 6), Tag = "Filter" };
                    filterBox.TextChanged += delegate
                    {
                        string q = (filterBox.Text ?? "").Trim();
                        foreach (KeyValuePair<ListItem, CheckBox> r in rows)
                            r.Value.Visibility = MatchesFilter(r.Key, q) ? Visibility.Visible : Visibility.Collapsed;
                        // A group whose every row is filtered out hides too, and a search auto-expands the
                        // groups that still have hits so results aren't buried behind a collapsed header.
                        foreach (KeyValuePair<Expander, List<CheckBox>> ge in groupExpanders)
                        {
                            bool anyVisible = false;
                            foreach (CheckBox cb in ge.Value) if (cb.Visibility == Visibility.Visible) { anyVisible = true; break; }
                            ge.Key.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
                            if (anyVisible && q.Length > 0) ge.Key.IsExpanded = true;
                        }
                    };
                    inner.Children.Add(filterBox);
                }

                // Cap height so a long list scrolls inside the card instead of making one giant column.
                inner.Children.Add(new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 260,
                    Content = listPanel,
                });
            }

            if (lc.Actions != null && lc.Actions.Count > 0)
            {
                inner.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 4) });
                foreach (PaneAction a in lc.Actions)
                    if (a != null && a.InvokeAsync != null) inner.Children.Add(BuildActionRow(a));
            }

            return NewCard(inner);
        }

        // Case-insensitive substring match over the item's IDENTITY only: its name, its group, and its id.
        // Detail is deliberately excluded -- it holds generated metadata ("964 lines · spicy"), and the word
        // "lines" appears in every row, so including it made short queries match everything ("lin" hit every
        // pack). Anything genuinely worth filtering on belongs in the label or the group.
        internal static bool MatchesFilter(ListItem item, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return Contains(item.Label, query) || Contains(item.Group, query) || Contains(item.Id, query);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Colour a ✓/✗ result green/red so pass/fail is obvious without reading it.</summary>
        private static void ShowActionStatus(TextBlock status, string result)
        {
            status.Text = result ?? "";
            if (!string.IsNullOrEmpty(result) && result.StartsWith("✓")) status.Foreground = Brushes.LimeGreen;
            else if (!string.IsNullOrEmpty(result) && result.StartsWith("✗")) status.Foreground = Brushes.Salmon;
            else status.ClearValue(TextBlock.ForegroundProperty);
        }

        private FrameworkElement BuildActionRow(PaneAction action)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            // MinWidth, not Width: a fixed 150 clipped every label longer than it, so the Remembrance card
            // offered a button reading "Browse for whisper-cli.." with the rest of the word cut off.
            var btn = new Button { Content = action.Label ?? "Run", MinWidth = 150, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(btn, Dock.Left);
            var status = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            // This row may be the replacement for one whose action reported into a tree that has since
            // been discarded; if so, say it again rather than losing it to the rebuild it asked for.
            string carried;
            if (_actionMessages != null && action.Label != null
                && _actionMessages.TryGetValue(action.Label, out carried))
                ShowActionStatus(status, carried);
            btn.Click += async delegate
            {
                btn.IsEnabled = false;
                status.Text = "working…";
                status.ClearValue(TextBlock.ForegroundProperty);
                string result;
                try { result = await action.InvokeAsync() ?? ""; }
                catch (Exception ex) { result = "failed: " + ex.Message; }
                // RevealsPath: the return value is a file to show in Explorer, not a message. Empty, or
                // already carrying a ✓/✗ marker, means the action chose to report the ordinary way instead
                // (it had no path to give), so it passes through untouched. Anything else is checked and
                // then either shown or refused, and the refusal lands in this same status line: that is the
                // only channel through which a module can learn it was refused at all.
                if (action.RevealsPath && !string.IsNullOrEmpty(result) && !result.StartsWith("✓") && !result.StartsWith("✗"))
                    result = RevealInExplorer(result);
                ShowActionStatus(status, result);
                btn.IsEnabled = true;
                // An action (e.g. reset-to-defaults) can ask the pane to rebuild so it shows the new values.
                if (action.ReloadPaneAfter && _requestReload != null)
                {
                    // Everything this row is holding dies with the rebuild -- the message just written and
                    // every unsaved edit on the pane -- so hand both to the view that replaces it.
                    bool hadUnsavedEdits = HasUnsavedEdits();
                    var messages = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (_actionMessages != null)
                        foreach (KeyValuePair<string, string> kv in _actionMessages) messages[kv.Key] = kv.Value;
                    if (action.Label != null) messages[action.Label] = result;
                    StashActionRebuild(_pane, new ActionRebuild
                    {
                        Before = _loaded,
                        OnScreen = Collect(),
                        Messages = messages,
                    });

                    _requestReload();

                    // After, never before: the host greys Apply out at the END of a rebuild, so a signal
                    // raised any earlier is the one thing the rebuild is guaranteed to erase. Same
                    // ordering, and the same reason, as the ReloadOnChange cascade in FieldChanged.
                    if (hadUnsavedEdits) Dirty();
                }
            };
            row.Children.Add(btn);
            row.Children.Add(status);
            return row;
        }

        /// <summary>
        /// Show a <see cref="PaneAction.RevealsPath"/> file in Explorer, or say why not.
        ///
        /// The data root is the whole containment test, and that is not a shortcut: every module's storage
        /// directory is already inside it, because CompanionHost hands out
        /// <c>AppPaths.DataRoot\modules\{id}</c> and nothing else (CompanionHost.ModuleDataDir). A PaneView
        /// is handed an OptionsPane and no module identity, so it could not name the calling module's own
        /// folder even if that folder lived somewhere else. Written down here because it is the assumption
        /// that would break quietly: a future host that gives a module a directory OUTSIDE the data root
        /// turns this from "exactly the rule" into "too strict", and the symptom would be a refusal nobody
        /// can explain.
        /// </summary>
        private static string RevealInExplorer(string returned)
        {
            string refusal;
            string full = ResolveRevealTarget(returned, AppPaths.DataRoot, out refusal);
            if (full == null) return refusal;
            try
            {
                // Same shell-execute shape as the releases link in the window footer. /select highlights
                // the file inside its folder rather than opening it, so even an allowed path is shown and
                // never run: the verb stays "reveal" even if the file is an .exe the module just wrote.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + full + "\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { return "✗ could not open Explorer: " + ex.Message; }
            return "✓ shown in Explorer";
        }

        /// <summary>
        /// The existence + containment check behind <see cref="PaneAction.RevealsPath"/>, kept apart from
        /// the shell call so a refusal can be asserted without an Explorer window opening. Returns the path
        /// to reveal, or null with the ✗ message to show beside the button.
        ///
        /// Containment is tested BEFORE existence, deliberately. The other order answers "does
        /// C:\Users\someone\taxes.pdf exist?" for any path a module cares to return, one refusal message at
        /// a time, which is a filesystem probe the plugin ABI does not otherwise offer.
        /// </summary>
        internal static string ResolveRevealTarget(string returned, string dataRoot, out string refusal)
        {
            refusal = null;
            // Trimmed of quotes as well as space: a module that built the string for a command line has
            // wrapped it, and refusing that would look like the containment check failing.
            string candidate = (returned ?? "").Trim().Trim('"');
            if (candidate.Length == 0) { refusal = "✗ refused: no path to show."; return null; }

            string full;
            try { full = System.IO.Path.GetFullPath(candidate); }
            catch (Exception ex) { refusal = "✗ refused: that path could not be read (" + ex.Message + ")"; return null; }

            const string outside = "✗ refused: that file is outside this app's data folder.";
            if (!IsUnder(full, dataRoot)) { refusal = outside; return null; }
            if (!System.IO.File.Exists(full)) { refusal = "✗ refused: that is not an existing file."; return null; }

            // A reparse point INSIDE the data root must not become a way to point outside it, and the
            // containment test has to be done on the path the OS will actually open.
            //
            // File.ResolveLinkTarget only resolves a link at the END of the path, which leaves the
            // EASIER escape open: a directory junction part way along one. A symbolic link needs
            // Developer Mode or elevation to create, a junction needs neither, so the cheap attack was
            // the unhandled one. GetFinalPathNameByHandle resolves every reparse point along the path
            // in one call, which is the only answer that is not a partial one.
            //
            // A failure here REFUSES rather than falling through. The previous shape swallowed the
            // exception and returned the unresolved path on the grounds that "nothing was learned" --
            // but not knowing where a path leads is the case this check exists for, and the whole
            // reason the verb is restricted at all is that an unrestricted one is a shell primitive
            // wearing a different name.
            string real;
            try { real = FinalPath(full); }
            catch (Exception ex)
            {
                refusal = "✗ refused: that path could not be resolved (" + ex.Message + ")";
                return null;
            }
            if (!IsUnder(real, dataRoot)) { refusal = outside; return null; }

            return full;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet =
            System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Text.StringBuilder path,
            int count, int flags);

        /// <summary>
        /// Where a path REALLY leads, with every junction and symlink along it resolved.
        ///
        /// Opened with full sharing so a file another process is writing -- the diagnostic log being the
        /// obvious one, since showing it is what this feature is for -- does not fail to resolve.
        /// </summary>
        private static string FinalPath(string full)
        {
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = System.IO.File.OpenHandle(
                       full, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                       System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                var buffer = new System.Text.StringBuilder(1024);
                int written = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                if (written <= 0)
                    throw new System.ComponentModel.Win32Exception(
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                string resolved = buffer.ToString(0, Math.Min(written, buffer.Capacity));
                // The call returns the \\?\ form. Strip it so the comparison is against an ordinary
                // path, and leave the UNC form (\\?\UNC\server\share) alone rather than half-converting
                // it -- a mangled UNC would compare unequal to everything and refuse silently.
                const string Prefix = @"\\?\";
                if (resolved.StartsWith(Prefix, StringComparison.Ordinal)
                    && !resolved.StartsWith(Prefix + "UNC", StringComparison.OrdinalIgnoreCase))
                    resolved = resolved.Substring(Prefix.Length);
                return resolved;
            }
        }

        /// <summary>Is an already-absolute path inside <paramref name="root"/>? The separator is appended
        /// to the root before comparing, so a sibling directory whose name merely starts the same way
        /// ("…\DesktopAICompanion-old\x" against "…\DesktopAICompanion") is not read as being inside it.</summary>
        private static bool IsUnder(string full, string root)
        {
            if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(root)) return false;
            string prefix;
            try { prefix = System.IO.Path.GetFullPath(root); } catch { return false; }
            if (prefix.Length == 0) return false;
            if (prefix[prefix.Length - 1] != System.IO.Path.DirectorySeparatorChar)
                prefix += System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An Int field's declared Min/Max, applied to what the pane hands back.
        ///
        /// Non-Int fields, unparseable text and an unset range (Min == Max) all pass through
        /// untouched. Unparseable rather than clamped on purpose: "" and "abc" are for the module's
        /// own GetInt fallback to resolve, and turning them into Min would silently invent a value
        /// the user never typed.
        ///
        /// Clamped on READ rather than on keystroke, because rejecting characters as they are typed
        /// makes it impossible to replace "300" with "45" -- you would have to pass through "30",
        /// "3" and "" -- and this pane has no per-field error channel to explain a refusal.
        /// </summary>
        internal static string ClampIfBounded(SettingField f, string text)
        {
            if (f == null || f.Kind != SettingKind.Int) return text;
            if (f.Min == f.Max) return text;
            int value;
            if (!int.TryParse((text ?? "").Trim(), out value)) return text;
            int low = Math.Min(f.Min, f.Max);
            int high = Math.Max(f.Min, f.Max);
            if (value < low) value = low;
            if (value > high) value = high;
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private FrameworkElement BuildRow(SettingField f, string cur)
        {
            FrameworkElement row = f.Kind == SettingKind.Header ? BuildHeaderRow(f, cur) : BuildEditorRow(f, cur);
            _rows[f.Id] = row;

            // EnabledWhen greys the WHOLE row, label included. Disabling only the editor leaves a
            // full-contrast label beside a dead control, which reads as a rendering fault rather than as
            // "not applicable right now". What it deliberately does NOT touch is the reader: a disabled
            // field is still collected, because a field the user never saw writing "" over a stored setting
            // would destroy data they had no way to know was at risk.
            if (!string.IsNullOrEmpty(f.EnabledWhen))
            {
                SettingField dependent = f;
                FrameworkElement target = row;
                _enableUpdaters.Add(delegate { target.IsEnabled = IsEnabledNow(dependent); });
            }
            return row;
        }

        /// <summary>
        /// Header: display-only, and the one field kind that does NOT get the fixed-width label column. The
        /// Label IS the heading, so rendering it as a label beside an empty editor would indent it under the
        /// fields it exists to introduce. No reader is registered, exactly as for Info, so a module never
        /// has to defend against its own heading text arriving back as input on Save.
        /// </summary>
        private static FrameworkElement BuildHeaderRow(SettingField f, string cur)
        {
            var block = new StackPanel { Margin = new Thickness(0, 8, 0, 2) };
            block.Children.Add(new TextBlock
            {
                Text = f.Label ?? f.Id,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            });
            // The value is optional. When Load supplies one it becomes the paragraph under the heading,
            // which is what lets a card carry a sentence of explanation and not just a bold line.
            if (!string.IsNullOrEmpty(cur))
                block.Children.Add(new TextBlock
                {
                    Text = cur,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            return block;
        }

        private FrameworkElement BuildEditorRow(SettingField f, string cur)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var label = new TextBlock { Text = f.Label ?? f.Id, Width = 165, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            switch (f.Kind)
            {
                case SettingKind.Bool:
                {
                    var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsChecked = ParseBool(cur) };
                    _readers[f.Id] = () => (cb.IsChecked == true) ? "true" : "false";
                    cb.Checked += delegate { FieldChanged(f); };
                    cb.Unchecked += delegate { FieldChanged(f); };
                    row.Children.Add(cb);
                    break;
                }
                case SettingKind.Enum:
                {
                    var combo = new ComboBox();
                    if (f.Options != null) foreach (string o in f.Options) combo.Items.Add(o);
                    combo.SelectedItem = cur;
                    _readers[f.Id] = () => combo.SelectedItem as string ?? "";
                    combo.SelectionChanged += delegate { FieldChanged(f); };
                    row.Children.Add(combo);
                    break;
                }
                case SettingKind.Radio:
                {
                    // Stores exactly what Enum stores: the chosen option string, or "" when nothing is
                    // selected because the loaded value is not one of the Options. That equivalence is the
                    // entire point of the kind (a field moves between Enum and Radio with no settings
                    // migration), so this reader mirrors the ComboBox one above rather than being written
                    // for whatever is convenient here.
                    //
                    // No GroupName is set, on purpose. WPF's named groups live in a static registry scoped
                    // by VISUAL ROOT, and Build() hands back an unrooted tree, so two panes carrying the
                    // same field id in one process (the self-test does exactly that, and so does closing
                    // and reopening Settings) would share a group and silently unselect each other's
                    // buttons. Unnamed, a RadioButton groups by its logical parent instead, and this
                    // StackPanel belongs to this field alone.
                    var choices = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var buttons = new List<RadioButton>();
                    if (f.Options != null)
                        foreach (string o in f.Options)
                        {
                            // IsChecked in the initializer, before the handler is wired, so restoring the
                            // stored value is not reported as an edit.
                            // A TextBlock, not the bare string. RadioButton.Content given a
                            // string renders it as a single unwrapped line, so an option any
                            // longer than the card is simply cut off -- "Approve prompts for
                            // me (one..." with the rest gone. Nothing clips or ellipsises it,
                            // so the text is not merely hard to read, it is absent.
                            var rb = new RadioButton
                            {
                                Content = new TextBlock { Text = o, TextWrapping = TextWrapping.Wrap },
                                Tag = o,
                                Margin = new Thickness(0, 2, 0, 2),
                                IsChecked = string.Equals(o, cur, StringComparison.Ordinal),
                            };
                            buttons.Add(rb);
                            choices.Children.Add(rb);
                        }
                    _readers[f.Id] = delegate
                    {
                        foreach (RadioButton rb in buttons)
                            if (rb.IsChecked == true) return (rb.Tag as string) ?? "";
                        return "";
                    };
                    // Checked only. Moving to another option raises Unchecked on the old button as well, so
                    // handling both would report one change twice, and a ReloadOnChange field would rebuild
                    // the pane from the half-finished state where nothing is selected.
                    foreach (RadioButton rb in buttons) rb.Checked += delegate { FieldChanged(f); };
                    row.Children.Add(choices);
                    break;
                }
                case SettingKind.Info:
                {
                    // Display-only: no editor and NO reader registered, so Collect() never sends it to Save
                    // (a module must not have to defend against its own status text coming back as input).
                    var info = new TextBlock
                    {
                        Text = cur ?? "",
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    if (!string.IsNullOrEmpty(cur))
                    {
                        if (cur.StartsWith("✓")) info.Foreground = Brushes.LimeGreen;
                        else if (cur.StartsWith("✗")) info.Foreground = Brushes.Salmon;
                    }
                    row.Children.Add(info);
                    break;
                }
                case SettingKind.Secret:
                {
                    // Same stretch-to-row-height issue as the text editor below; no Secret field currently
                    // has a label long enough to wrap, so this is pinned before one does rather than after.
                    var pw = new PasswordBox { VerticalAlignment = VerticalAlignment.Center };
                    bool alreadySet = !string.IsNullOrEmpty(cur);
                    if (alreadySet) pw.ToolTip = "A value is saved. Leave blank to keep it.";
                    _secretIds.Add(f.Id);
                    _readers[f.Id] = () => pw.Password ?? "";
                    pw.PasswordChanged += delegate { FieldChanged(f); };
                    row.Children.Add(pw);
                    break;
                }
                default: // Int + Text both edit as text.
                {
                    // For an Int, Min/Max are now HONOURED here rather than left to the module.
                    // They had no reader anywhere until 2026-09-17: an author set bounds, the host
                    // rendered a plain TextBox, and the value went through untouched -- so the two
                    // properties were an ABI member that did nothing, and the only module using them
                    // (AgentFlow) was safe purely because it clamps again in its own Save.
                    //
                    // Clamped on READ, not on keystroke: rejecting characters as they are typed makes
                    // it impossible to replace "300" with "45" (you would have to pass through "30",
                    // "3", ""), and this pane has no per-field error channel to explain a refusal.
                    // A module that wants to reject rather than clamp still can -- Save returning
                    // false is unchanged -- and one that declares no bounds is untouched, because
                    // Min == Max == 0 means "unset" for a flags-free int.
                    // Center, not the DockPanel's default Stretch. The label beside it is a fixed 165px
                    // and wraps, so a long one makes the row two lines tall and LastChildFill grows the
                    // editor to match -- a one-line value like "512" sitting in a double-height box, out
                    // of line with every single-line field above it. The checkbox and status rows already
                    // pin Center for the same reason.
                    var tb = new TextBox { Text = cur, VerticalAlignment = VerticalAlignment.Center };
                    SettingField bounded = f;
                    _readers[f.Id] = () => ClampIfBounded(bounded, tb.Text ?? "");
                    tb.TextChanged += delegate { FieldChanged(f); };
                    row.Children.Add(tb);
                    break;
                }
            }
            return row;
        }

        /// <summary>Collect the edited values and hand them to the pane's Save. A Secret is included only when
        /// the user typed something (blank keeps the stored value). Returns true if there's nothing to save.</summary>
        public bool Save()
        {
            if (_pane == null || _pane.Save == null) return true;
            // Staged checkbox edits go first: the module records the ids here and commits the whole batch
            // inside its own Save, so a hundred ticks cost one write instead of a hundred.
            _pendingChecks.Flush();
            return _pane.Save(Collect());
        }

        /// <summary>The values that would be sent to Save (also the self-test hook).</summary>
        internal Dictionary<string, string> Collect()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Func<string>> reader in _readers)
            {
                string v;
                try { v = reader.Value() ?? ""; } catch { v = ""; }
                if (_secretIds.Contains(reader.Key) && string.IsNullOrEmpty(v)) continue; // blank secret => keep stored
                values[reader.Key] = v;
            }
            return values;
        }

        private static bool ParseBool(string s) { bool b; return bool.TryParse(s, out b) && b; }
    }
}
