using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// The behaviour timeline: drag animations in from the reachability map, arrange them left to right, then
    /// run the chain on a throwaway pet.
    ///
    /// The connector between two chips is where the colour lives, NOT the chip. "Is this transition one the pet
    /// would make on its own" is a property of the JOIN, and putting it on a chip makes it ambiguous which side
    /// it refers to. Three states, because two would have to lump a border edge in with either the natural or
    /// the forced ones and a jump's landing is a border edge.
    ///
    /// Owns no pet: spawning and removing the preview stay with the window, so there is exactly one preview
    /// owner and closing the window cannot leave one behind.
    /// </summary>
    internal sealed class TimelinePane
    {
        /// <summary>The drag payload: an animation id, as its own format so the timeline cannot be fed a
        /// stray text drop from elsewhere in the window (the XML editor is one drag away).</summary>
        internal const string DragFormat = "PetStudio.AnimationId";

        private readonly PetStudioTheme _theme;
        private readonly Func<IDictionary<int, AnimNode>> _nodes;
        private readonly Func<string> _sourceXml;
        private readonly Action<string> _setStatus;
        private readonly Func<string, bool> _runDebugPet;   // hands the built XML to the window's preview owner
        private readonly Action _stop;

        private readonly List<ChainStep> _steps = new List<ChainStep>();
        private readonly StackPanel _strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _empty;
        private readonly TextBlock _summary;
        private readonly Button _runButton = new Button { Content = "▶ Run chain", Padding = new Thickness(10, 3, 10, 3) };
        private readonly Button _stopButton = new Button { Content = "■ Stop", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly CheckBox _loop = new CheckBox { Content = "loop", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private readonly Button _clearButton = new Button { Content = "Clear", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(12, 0, 0, 0) };

        private Point _dragStart;
        private int _dragFromIndex = -1;

        internal UIElement Root { get; private set; }

        internal TimelinePane(
            PetStudioTheme theme,
            Func<IDictionary<int, AnimNode>> nodes,
            Func<string> sourceXml,
            Action<string> setStatus,
            Func<string, bool> runDebugPet,
            Action stop)
        {
            _theme = theme;
            _nodes = nodes;
            _sourceXml = sourceXml;
            _setStatus = setStatus;
            _runDebugPet = runDebugPet;
            _stop = stop;

            _empty = new TextBlock
            {
                Text = "Drag animations here from the reachability map, then Run. A chip's ×N plays it that many times in a row.",
                Foreground = _theme.Muted,
                FontStyle = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _summary = new TextBlock { Foreground = _theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

            _runButton.Click += delegate { Run(); };
            _stopButton.Click += delegate { StopRun(); };
            _clearButton.Click += delegate { Clear(); };

            Root = Build();
            Refresh();
        }

        private UIElement Build()
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };

            var caption = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_runButton);
            buttons.Children.Add(_stopButton);
            buttons.Children.Add(_loop);
            buttons.Children.Add(_clearButton);
            DockPanel.SetDock(buttons, Dock.Right);
            caption.Children.Add(buttons);

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = "Behaviour timeline", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(_summary);
            caption.Children.Add(title);

            DockPanel.SetDock(caption, Dock.Top);
            dock.Children.Add(caption);
            dock.Children.Add(BuildLegend());

            var surface = new Border
            {
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Background = _theme.Surface,
                MinHeight = 52,
                Padding = new Thickness(6),
                AllowDrop = true,
            };
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _strip,
            };
            surface.Child = scroll;
            surface.DragOver += OnDragOver;
            surface.Drop += OnDrop;
            dock.Children.Add(surface);
            return dock;
        }

        private UIElement BuildLegend()
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(legend, Dock.Bottom);
            legend.Children.Add(Swatch(_theme.LiveStroke, "plays next on its own"));
            legend.Children.Add(Swatch(_theme.HintStroke, "natural, but only on contact"));
            legend.Children.Add(Swatch(_theme.DeadStroke, "forced (no such transition)"));
            return legend;
        }

        private UIElement Swatch(Brush stroke, string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            row.Children.Add(new Border { Width = 14, Height = 3, Background = stroke, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = " " + label, Foreground = _theme.Muted, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        // ---- the map side of the drag ----

        /// <summary>Make a reachability-map chip draggable into the timeline. Called by the window as it builds
        /// each chip, so the map stays the single list of animations rather than the timeline growing a second
        /// one that could disagree with it.</summary>
        internal void MakeDragSource(FrameworkElement chip, int animationId)
        {
            if (chip == null) return;
            chip.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { _dragStart = e.GetPosition(null); };
            chip.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Vector moved = e.GetPosition(null) - _dragStart;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragFromIndex = -1;   // from the map, not a reorder
                var data = new DataObject(DragFormat, animationId);
                try { DragDrop.DoDragDrop(chip, data, DragDropEffects.Copy); } catch { }
            };
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data != null && e.Data.GetDataPresent(DragFormat)
                ? (_dragFromIndex >= 0 ? DragDropEffects.Move : DragDropEffects.Copy)
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DragFormat)) return;
            int id;
            try { id = (int)e.Data.GetData(DragFormat); } catch { return; }

            int insertAt = DropIndex(e.GetPosition(_strip));
            if (_dragFromIndex >= 0 && _dragFromIndex < _steps.Count)
            {
                ChainStep moving = _steps[_dragFromIndex];
                _steps.RemoveAt(_dragFromIndex);
                if (insertAt > _dragFromIndex) insertAt--;
                _steps.Insert(Math.Max(0, Math.Min(_steps.Count, insertAt)), moving);
            }
            else
            {
                Insert(id, insertAt);
            }
            _dragFromIndex = -1;
            e.Handled = true;
            Refresh();
        }

        /// <summary>Where a drop lands: before the first chip whose midpoint is right of the cursor. Measured
        /// against the rendered chips rather than tracked during the drag, so a drop is placed where it looks
        /// like it will be placed even after a reorder has shuffled them.</summary>
        private int DropIndex(Point atStrip)
        {
            int index = 0;
            foreach (UIElement child in _strip.Children)
            {
                var chip = child as FrameworkElement;
                if (chip == null || !(chip.Tag is int)) continue;      // skip the connectors
                Point topLeft = chip.TranslatePoint(new Point(0, 0), _strip);
                if (atStrip.X < topLeft.X + chip.ActualWidth / 2) return index;
                index++;
            }
            return index;
        }

        // ---- the model ----

        internal void Insert(int animationId, int at)
        {
            IDictionary<int, AnimNode> nodes = _nodes();
            AnimNode node;
            if (nodes == null || !nodes.TryGetValue(animationId, out node)) return;
            if (_steps.Count >= BehaviourChain.MaxChainNodes)
            {
                _setStatus("The timeline is full (" + BehaviourChain.MaxChainNodes + " steps).");
                return;
            }
            _steps.Insert(Math.Max(0, Math.Min(_steps.Count, at)),
                new ChainStep { AnimationId = animationId, Name = node.Name ?? "", Repeat = 1 });
        }

        internal void Add(int animationId)
        {
            Insert(animationId, _steps.Count);
            Refresh();
        }

        internal void Clear()
        {
            _steps.Clear();
            Refresh();
        }

        /// <summary>Called after the pet is re-analyzed: drop any step whose animation the pet no longer has,
        /// so an edited XML cannot leave the timeline referring to something that is gone.</summary>
        internal void Resync()
        {
            IDictionary<int, AnimNode> nodes = _nodes();
            int before = _steps.Count;
            if (nodes != null)
                _steps.RemoveAll(delegate(ChainStep s) { return s == null || !nodes.ContainsKey(s.AnimationId); });
            if (_steps.Count != before)
                _setStatus("Dropped " + (before - _steps.Count) + " timeline step(s) the edited companion no longer has.");
            Refresh();
        }

        internal void RunFinished()
        {
            _stopButton.IsEnabled = false;
            _runButton.IsEnabled = _steps.Count > 0;
        }

        // ---- rendering ----

        private void Refresh()
        {
            _strip.Children.Clear();
            IDictionary<int, AnimNode> nodes = _nodes() ?? new Dictionary<int, AnimNode>();
            List<ChainJoin> joins = BehaviourChain.Joins(_steps, nodes);

            if (_steps.Count == 0)
            {
                _strip.Children.Add(_empty);
                _summary.Text = "";
                _runButton.IsEnabled = false;
                return;
            }

            int forced = 0, contact = 0, plays = 0;
            for (int i = 0; i < _steps.Count; i++)
            {
                if (i > 0)
                {
                    _strip.Children.Add(Connector(joins[i]));
                    if (joins[i].Kind == ChainLink.Forced) forced++;
                    else if (joins[i].Kind == ChainLink.Border || joins[i].Kind == ChainLink.Gravity) contact++;
                }
                _strip.Children.Add(Chip(i, nodes));
                plays += Math.Max(1, _steps[i].Repeat);
            }

            _summary.Text = plays + " play(s) across " + _steps.Count + " step(s)" +
                (forced > 0 ? ", " + forced + " forced join(s)" : ", every join is natural") +
                (contact > 0 ? ", " + contact + " needing contact" : "");
            _runButton.IsEnabled = true;
        }

        private FrameworkElement Connector(ChainJoin join)
        {
            Brush stroke = join.Kind == ChainLink.Forced ? _theme.DeadStroke
                         : join.Kind == ChainLink.Sequence || join.Kind == ChainLink.Child ? _theme.LiveStroke
                         : _theme.HintStroke;
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = join.Describe(),
            };
            panel.Children.Add(new Border { Width = 16, Height = 3, Background = stroke, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock
            {
                Text = join.Kind == ChainLink.Forced ? "✕" : "▸",
                Foreground = stroke,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return panel;
        }

        private FrameworkElement Chip(int index, IDictionary<int, AnimNode> nodes)
        {
            ChainStep step = _steps[index];
            AnimNode node;
            nodes.TryGetValue(step.AnimationId, out node);
            ChainJoin repeatJoin = BehaviourChain.RepeatJoin(step, nodes);

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            string label = "#" + step.AnimationId;
            if (!string.IsNullOrEmpty(step.Name))
                label += " " + (step.Name.Length > 16 ? step.Name.Substring(0, 15) + "…" : step.Name);
            content.Children.Add(new TextBlock { Text = label, Foreground = _theme.ChipText, VerticalAlignment = VerticalAlignment.Center });

            content.Children.Add(TinyButton("−", "Play this one fewer time", delegate { Bump(index, -1); }));
            var times = new TextBlock
            {
                Text = "×" + Math.Max(1, step.Repeat),
                Foreground = _theme.ChipText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 1, 0),
                FontWeight = step.Repeat > 1 ? FontWeights.Bold : FontWeights.Normal,
            };
            if (repeatJoin != null)
                times.ToolTip = "Repeat: " + repeatJoin.Describe();
            content.Children.Add(times);
            content.Children.Add(TinyButton("+", "Play this one more time", delegate { Bump(index, +1); }));
            content.Children.Add(TinyButton("✕", "Remove this step", delegate { RemoveAt(index); }));

            // A repeat is a self-transition, so it is colour-coded like any other join: a chip whose ×N is
            // forced is telling you the pet has no way to do that animation twice in a row by itself.
            Brush stroke = node == null ? _theme.DeadStroke
                         : repeatJoin != null && repeatJoin.Kind == ChainLink.Forced ? _theme.HintStroke
                         : _theme.Border;
            var chip = new Border
            {
                Background = node == null ? _theme.DeadFill : _theme.LiveFill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 4, 2),
                Cursor = Cursors.SizeAll,
                Tag = index,   // marks a chip (vs a connector) for DropIndex, and carries its position
                Child = content,
                ToolTip = node == null
                    ? "This animation is no longer in the companion."
                    : "Drag to reorder. " + (repeatJoin != null ? "×" + step.Repeat + ": " + repeatJoin.Describe() : ""),
            };
            chip.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { _dragStart = e.GetPosition(null); };
            chip.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Vector moved = e.GetPosition(null) - _dragStart;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragFromIndex = index;
                try { DragDrop.DoDragDrop(chip, new DataObject(DragFormat, step.AnimationId), DragDropEffects.Move); }
                catch { }
                finally { _dragFromIndex = -1; }
            };
            return chip;
        }

        private FrameworkElement TinyButton(string glyph, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = glyph,
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(3, 0, 0, 0),
                MinWidth = 16,
                FontSize = 10,
                ToolTip = tip,
                VerticalAlignment = VerticalAlignment.Center,
            };
            b.Click += delegate(object s, RoutedEventArgs e) { e.Handled = true; onClick(); };
            return b;
        }

        private void Bump(int index, int delta)
        {
            if (index < 0 || index >= _steps.Count) return;
            int next = Math.Max(1, Math.Min(BehaviourChain.MaxRepeatPerStep, _steps[index].Repeat + delta));
            _steps[index].Repeat = next;
            Refresh();
        }

        private void RemoveAt(int index)
        {
            if (index < 0 || index >= _steps.Count) return;
            _steps.RemoveAt(index);
            Refresh();
        }

        // ---- running ----

        private void Run()
        {
            string error;
            string xml = BehaviourChain.BuildDebugXml(_sourceXml(), _steps, _loop.IsChecked == true, out error);
            if (xml == null)
            {
                _setStatus(string.IsNullOrEmpty(error) ? "Could not build the chain." : error);
                return;
            }
            if (!_runDebugPet(xml)) return;
            _stopButton.IsEnabled = true;
            _runButton.IsEnabled = false;
        }

        private void StopRun()
        {
            _stop();
            RunFinished();
        }
    }
}
