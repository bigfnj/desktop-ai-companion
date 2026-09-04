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

        protected override Size MeasureOverride(Size availableSize)
        {
            int cols = ColumnCount(availableSize.Width);
            var colHeights = new double[cols];
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                child.Measure(new Size(ColumnWidth, double.PositiveInfinity));
                int c = ShortestColumn(colHeights);
                colHeights[c] += child.DesiredSize.Height;
            }
            double maxH = 0;
            foreach (double h in colHeights) if (h > maxH) maxH = h;
            double width = double.IsInfinity(availableSize.Width) ? cols * ColumnWidth : availableSize.Width;
            return new Size(width, maxH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols = ColumnCount(finalSize.Width);
            var colHeights = new double[cols];
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
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

        public PaneView(OptionsPane pane, Action requestReload = null, Action notifyDirty = null)
        {
            _pane = pane; _requestReload = requestReload; _notifyDirty = notifyDirty;
        }

        // A genuine user edit to a field; ignored while Build() is populating initial values.
        private void Dirty() { if (!_suppressDirty && _notifyDirty != null) _notifyDirty(); }

        public FrameworkElement Build()
        {
            _readers.Clear();
            _secretIds.Clear();
            _suppressDirty = true;   // populating initial control values below must not mark the pane dirty

            IReadOnlyDictionary<string, string> values = null;
            try { if (_pane != null && _pane.Load != null) values = _pane.Load(); } catch { values = null; }
            if (values == null) values = new Dictionary<string, string>();

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
                cards.Children.Add(NewCard(inner));
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
            _suppressDirty = false;   // initial values are in place; from here real edits mark the pane dirty
            // Own ScrollViewer so the pane scrolls (incl. the mouse wheel) without an outer one to nest in.
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root,
            };
        }

        // The shared titled-card chrome, used by both schema-group cards and dynamic list cards.
        private static Border NewCard(UIElement child)
        {
            return new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4),
                Padding = new Thickness(8),
                Width = 360,
                Child = child,
            };
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

        private FrameworkElement BuildActionRow(PaneAction action)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var btn = new Button { Content = action.Label ?? "Run", Width = 150, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(btn, Dock.Left);
            var status = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            btn.Click += async delegate
            {
                btn.IsEnabled = false;
                status.Text = "working…";
                status.ClearValue(TextBlock.ForegroundProperty);
                string result;
                try { result = await action.InvokeAsync() ?? ""; }
                catch (Exception ex) { result = "failed: " + ex.Message; }
                status.Text = result;
                // Colour a ✓/✗ result green/red (used by Test OCR, Test connection) so pass/fail is obvious.
                if (result.StartsWith("✓")) status.Foreground = Brushes.LimeGreen;
                else if (result.StartsWith("✗")) status.Foreground = Brushes.Salmon;
                else status.ClearValue(TextBlock.ForegroundProperty);
                btn.IsEnabled = true;
                // An action (e.g. reset-to-defaults) can ask the pane to rebuild so it shows the new values.
                if (action.ReloadPaneAfter && _requestReload != null) _requestReload();
            };
            row.Children.Add(btn);
            row.Children.Add(status);
            return row;
        }

        private FrameworkElement BuildRow(SettingField f, string cur)
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
                    cb.Checked += delegate { Dirty(); };
                    cb.Unchecked += delegate { Dirty(); };
                    row.Children.Add(cb);
                    break;
                }
                case SettingKind.Enum:
                {
                    var combo = new ComboBox();
                    if (f.Options != null) foreach (string o in f.Options) combo.Items.Add(o);
                    combo.SelectedItem = cur;
                    _readers[f.Id] = () => combo.SelectedItem as string ?? "";
                    combo.SelectionChanged += delegate { Dirty(); };
                    row.Children.Add(combo);
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
                    var pw = new PasswordBox();
                    bool alreadySet = !string.IsNullOrEmpty(cur);
                    if (alreadySet) pw.ToolTip = "A value is saved. Leave blank to keep it.";
                    _secretIds.Add(f.Id);
                    _readers[f.Id] = () => pw.Password ?? "";
                    pw.PasswordChanged += delegate { Dirty(); };
                    row.Children.Add(pw);
                    break;
                }
                default: // Int + Text both edit as text (Int is validated by the module on Save).
                {
                    var tb = new TextBox { Text = cur };
                    _readers[f.Id] = () => tb.Text ?? "";
                    tb.TextChanged += delegate { Dirty(); };
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
