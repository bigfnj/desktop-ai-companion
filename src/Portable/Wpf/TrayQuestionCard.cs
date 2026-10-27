using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// The question card (app 1.6.0, lane feature/tray-questions): what TrayQuestionArbiter shows, just above the clock.
    ///
    /// WPF rather than WinForms because the dark theme exists only on this side now (WpfTheme's implicit button and text
    /// styles), WPF lays text out per monitor DPI and wraps it without hand-painting, and its automation names come from
    /// AutomationProperties rather than from geometry. A NEW window per question, closed when it ends, so a theme change
    /// between two questions is picked up and nothing stale survives.
    ///
    /// IT NEVER TAKES THE KEYBOARD ON ITS OWN. Shown without activation, WS_EX_NOACTIVATE, and WM_MOUSEACTIVATE answered
    /// MA_NOACTIVATE, so a card that appears while the user is typing does not swallow the next keystroke, and a click on
    /// a button answers without moving focus. Clicking the card's BODY is the user asking for the keyboard: the card
    /// activates, its buttons become focusable with the no-answer choice focused (the safe one), Tab and Enter work, and
    /// Esc -- or answering -- hands focus back to the window that had it.
    ///
    /// HIDDEN FROM SCREEN CAPTURE (owner, 2026-10-15). Teams and Zoom screen sharing do not count as presenting to
    /// Windows, so the presenting hold does not cover them, and a meeting title on a card must not appear in a share.
    /// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE): the user sees the card, a capture does not. The cost the owner
    /// accepted: it is missing from their own screenshots and screen recordings too.
    /// </summary>
    internal sealed class TrayQuestionCard : IQuestionCardView
    {
        internal const double WidthDip = 360;

        private readonly Func<DateTimeOffset> _utcNow;
        private Window _window;
        private IntPtr _hwnd;
        private HwndSource _source;
        private QuestionEntry _question;
        private Action<string> _onChoice;
        private TextBlock _countdown;
        private ProgressBar _bar;
        private TextBlock _waiting;
        private TextBlock _text;
        private Border _content;
        private StackPanel _buttonRow;
        private readonly List<Button> _buttons = new List<Button>();
        private bool _keyboard;
        private IntPtr _returnFocusTo;

        internal TrayQuestionCard(Func<DateTimeOffset> utcNow = null)
        {
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>For --trayquestions-selftest: the window on screen, and whether capture exclusion took.</summary>
        internal Window WindowForSelfTest { get { return _window; } }
        internal IntPtr HandleForSelfTest { get { return _hwnd; } }
        internal bool CaptureExcluded { get; private set; }
        internal IReadOnlyList<Button> ButtonsForSelfTest { get { return _buttons.ToArray(); } }
        /// <summary>For --trayquestions-selftest (app 1.7.1): the height Build measured before the window had a presentation
        /// source, and the card's row of buttons, so the test can set what was measured against what was laid out, and the
        /// buttons' bottom against the window's and the work area's.</summary>
        internal double PreShowHeightForSelfTest { get; private set; }
        internal FrameworkElement ButtonRowForSelfTest { get { return _buttonRow; } }

        public void Show(QuestionEntry question, int othersWaiting, Action<string> onChoice)
        {
            Hide();
            if (question == null) return;
            _question = question;
            _onChoice = onChoice;
            Window w = Build(question, othersWaiting);
            _window = w;
            try { System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(w); } catch { }
            // The handle first, hidden: SourceInitialized adds the styles and the hook, and the window is moved onto the
            // tray's monitor BEFORE it is ever shown, so it neither flashes elsewhere nor activates on the way.
            _hwnd = new WindowInteropHelper(w).EnsureHandle();
            Place();
            w.Show();
            Place();
            Announce(question);
        }

        public void Update(QuestionEntry question, int othersWaiting)
        {
            if (_window == null || question == null || !ReferenceEquals(question, _question)) return;
            Refresh(question, othersWaiting);
        }

        public void Hide()
        {
            Window w = _window;
            _window = null;
            _question = null;
            _onChoice = null;
            if (w == null) return;
            if (_keyboard) GiveFocusBack();
            _keyboard = false;
            try { if (_source != null) _source.RemoveHook(WndProc); } catch { }
            _source = null;
            _hwnd = IntPtr.Zero;
            _buttons.Clear();
            _content = null;
            _buttonRow = null;
            try { w.Close(); } catch { }
        }

        public void Activate()
        {
            if (_window != null) TakeKeyboard();
        }

        // ---- building ----

        private Window Build(QuestionEntry q, int othersWaiting)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                SizeToContent = SizeToContent.Manual,
                Width = WidthDip,
                Title = "Question from " + q.ModuleName,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            WpfTheme.Apply(w);
            AutomationProperties.SetName(w, "Question from " + q.ModuleName);
            string mode = "system";
            try { if (Program.MyData != null) mode = Program.MyData.GetThemeMode(); } catch { }
            bool dark = WpfTheme.EffectiveDark(mode);

            // Top, not the default Stretch (app 1.7.1): laid out at its own height, never the card's, so its SizeChanged says
            // when the content needs another height, growing or shrinking. A stack taller than its slot is laid out whole
            // and clipped, which is how the buttons went past the card's edge (FitToContent).
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            stack.Children.Add(new TextBlock { Text = q.ModuleName, FontWeight = FontWeights.SemiBold, FontSize = 12, Opacity = 0.8 });
            if (!string.IsNullOrEmpty(q.Title))
                stack.Children.Add(new TextBlock { Text = q.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            _text = new TextBlock { Text = q.Text, FontSize = 13, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(_text);
            _countdown = new TextBlock { FontSize = 11, Opacity = 0.75, Margin = new Thickness(0, 10, 0, 3) };
            stack.Children.Add(_countdown);
            _bar = new ProgressBar { Height = 3, Minimum = 0, Maximum = 1, IsTabStop = false, Focusable = false };
            AutomationProperties.SetName(_bar, "Time left to answer");
            stack.Children.Add(_bar);
            _waiting = new TextBlock { FontSize = 11, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) };
            stack.Children.Add(_waiting);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            _buttons.Clear();
            foreach (TrayChoice c in q.Choices)
            {
                string id = c.Id;
                // A TextBlock, not a string: a Label/Button string treats '_' as an access-key marker, and a module's
                // "Snooze_1" must be drawn as written.
                var button = new Button
                {
                    Content = new TextBlock { Text = c.Label },
                    MinWidth = 76,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(10, 4, 10, 4),
                    Focusable = false,
                    Tag = id,
                };
                AutomationProperties.SetName(button, c.Label);
                button.Click += delegate
                {
                    Action<string> answer = _onChoice;
                    if (answer != null) answer(id);
                };
                _buttons.Add(button);
                row.Children.Add(button);
            }
            stack.Children.Add(row);
            _buttonRow = row;

            var border = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                BorderThickness = new Thickness(1),
                BorderBrush = dark ? new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x4A)) : SystemColors.ActiveBorderBrush,
                Child = stack,
            };
            _content = border;
            // Clicking the card itself (not a button) is the user asking for the keyboard.
            border.MouseLeftButtonDown += delegate { TakeKeyboard(); };
            w.Content = border;
            w.KeyDown += OnKeyDown;
            w.SourceInitialized += delegate { OnSourceInitialized(w); };
            w.DpiChanged += delegate { try { w.Dispatcher.BeginInvoke(new Action(Place)); } catch { } };
            // THE CARD FOLLOWS ITS CONTENT (app 1.7.1, lane feature/remembrance-next; the owner, 2026-10-27: "the prompt is too
            // low, a refresh puts it in the correct place"). The height below was the window's for its whole life, fixed
            // before it was ever shown, and Place ran only at the show and on a DPI change. Reproduced on the owner's box
            // (one monitor, 2560x1440 at 100%, taskbar at the bottom): a question queued behind the one on screen brings in
            // "+1 more waiting" (Refresh), the content grew from 132 to 150.5 DIP inside a window that stayed 132, and the
            // row of buttons was pushed 5.5 DIP past the card's bottom edge with the bottom padding gone: the card read as
            // sitting under the taskbar, and the next card, measured afresh, was right. So the window takes the content's
            // laid-out height whenever it changes, and every change of the window's size places it again, its bottom kept
            // inside the work area. The pre-show measure stays as the first estimate, for the placement before the show; it
            // has no presentation source, so on a monitor scaled otherwise than the one its text was measured for it can be
            // off, and this corrects that too.
            stack.SizeChanged += delegate { FitToContent(); };
            w.SizeChanged += delegate { Place(); };
            Refresh(q, othersWaiting);
            // Height from the content at the card's width; WPF then scales the window to the monitor's DPI itself.
            border.Measure(new Size(WidthDip, double.PositiveInfinity));
            w.Height = Math.Ceiling(border.DesiredSize.Height);
            PreShowHeightForSelfTest = w.Height;
            return w;
        }

        private void Refresh(QuestionEntry q, int othersWaiting)
        {
            DateTimeOffset now = _utcNow();
            if (_countdown != null) _countdown.Text = TrayQuestionArbiter.CountdownText(q, now);
            if (_bar != null)
            {
                double total = q.Timeout.TotalSeconds;
                double left = q.Remaining.TotalSeconds;
                if (q.Deadline.HasValue)
                {
                    double toDeadline = (q.Deadline.Value - now).TotalSeconds;
                    if (toDeadline < left) left = toDeadline;
                }
                _bar.Value = total > 0 ? Math.Max(0, Math.Min(1, left / total)) : 0;
            }
            if (_waiting != null)
            {
                _waiting.Text = othersWaiting > 0 ? "+" + othersWaiting + " more waiting" : "";
                _waiting.Visibility = othersWaiting > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---- the window's native side ----

        private void OnSourceInitialized(Window w)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                _source = HwndSource.FromHwnd(hwnd);
                if (_source != null) _source.AddHook(WndProc);
                CaptureExcluded = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                // A Windows that predates capture exclusion (before 10 2004) can still black the window out of a capture.
                if (!CaptureExcluded) SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE && !_keyboard)
            {
                handled = true;
                return (IntPtr)MA_NOACTIVATE;
            }
            if (msg == WM_ENABLE && wParam == IntPtr.Zero)
            {
                // A modal WPF dialog (Options, About) disables every window on the thread while it is up, which would
                // leave the card's buttons dead while its countdown ran on. The card belongs to no dialog, so it puts
                // itself back.
                IntPtr me = hwnd;
                try { _window.Dispatcher.BeginInvoke(new Action(delegate { if (_hwnd == me) EnableWindow(me, true); })); } catch { }
            }
            if ((msg == WM_SETTINGCHANGE && wParam == (IntPtr)SPI_SETWORKAREA) || msg == WM_DISPLAYCHANGE)
            {
                // The work area moved under the card (app 1.7.1): the taskbar was resized, moved or set to hide, or a
                // display changed. The card is placed again, after the message, as on a DPI change.
                try { _window.Dispatcher.BeginInvoke(new Action(Place)); } catch { }
            }
            return IntPtr.Zero;
        }

        /// <summary>The window's height to its content's laid-out height (app 1.7.1), whenever the two part: the stack's own
        /// height (laid out at the top, so never the card's) with the card's padding and border round it. The window's
        /// SizeChanged then places it again. Converges in one pass, since the stack's height does not depend on the
        /// window's. Not the border's height: a stack too tall for its border leaves the border at the window's height and
        /// is clipped inside it, which is what the defect looked like from the inside.</summary>
        private void FitToContent()
        {
            Window w = _window;
            Border content = _content;
            FrameworkElement inner = content == null ? null : content.Child as FrameworkElement;
            if (w == null || inner == null) return;
            double needed = Math.Ceiling(inner.ActualHeight + content.Padding.Top + content.Padding.Bottom +
                                         content.BorderThickness.Top + content.BorderThickness.Bottom);
            if (inner.ActualHeight <= 0 || Math.Abs(needed - w.Height) < 0.5) return;
            w.Height = needed;
        }

        private void Place()
        {
            Window w = _window;
            if (w == null || _hwnd == IntPtr.Zero) return;
            try
            {
                TaskbarInfo tb = TrayAnchor.Read();
                double scale = tb.Dpi / 96.0;
                // ActualWidth/Height once laid out; the requested DIP size before.
                double wDip = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
                double hDip = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
                var size = new System.Drawing.Size((int)Math.Ceiling(wDip * scale), (int)Math.Ceiling(hDip * scale));
                System.Drawing.Point at = TrayAnchor.Compute(tb, size);
                SetWindowPos(_hwnd, HWND_TOPMOST, at.X, at.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void TakeKeyboard()
        {
            if (_window == null || _hwnd == IntPtr.Zero) return;
            if (!_keyboard)
            {
                _returnFocusTo = GetForegroundWindow();
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
                _keyboard = true;
                foreach (Button b in _buttons) b.Focusable = true;
            }
            try
            {
                _window.Activate();
                Button safe = null;
                foreach (Button b in _buttons)
                    if (_question != null && string.Equals(b.Tag as string, _question.TimeoutChoiceId, StringComparison.Ordinal)) safe = b;
                if (safe != null) safe.Focus();
            }
            catch { }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !_keyboard) return;
            e.Handled = true;
            GiveFocusBack();
        }

        private void GiveFocusBack()
        {
            IntPtr back = _returnFocusTo;
            _returnFocusTo = IntPtr.Zero;
            if (_hwnd != IntPtr.Zero)
            {
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
                foreach (Button b in _buttons) b.Focusable = false;
            }
            _keyboard = false;
            if (back != IntPtr.Zero) { try { SetForegroundWindow(back); } catch { } }
        }

        /// <summary>Narrator does not announce a window that never activates, so the question is raised as a notification.</summary>
        private void Announce(QuestionEntry q)
        {
            try
            {
                if (_text == null) return;
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(_text);
                if (peer != null)
                    peer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent,
                        q.ModuleName + " asks: " + q.Text, "DesktopAICompanion.Question");
            }
            catch { }
        }

        // ---- interop ----

        private const int GWL_EXSTYLE = -20;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_ENABLE = 0x000A;
        internal const int WM_SETTINGCHANGE = 0x001A;
        internal const int SPI_SETWORKAREA = 0x002F;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int MA_NOACTIVATE = 3;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        internal const uint WDA_MONITOR = 0x00000001;
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        internal static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
