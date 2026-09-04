using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    internal class FormSpeech : Form
    {
        // Design metrics are expressed in logical (96-DPI) pixels and scaled to the DPI the bubble is
        // actually painted at, read from the window itself (see PaintDpi), so the bubble is sized
        // correctly on any per-monitor scaling — not only at 100% — and stays correct under Remote
        // Desktop, where a screen-point monitor query can report a DPI that differs from the window's
        // real device context. Width shrinks to fit short lines (MinContentWidth..MaxContentWidth)
        // instead of always occupying a fixed column, and height is measured at the same DPI it will
        // be drawn at.
        private const int MaxContentWidth = 196; // widest text column before wrapping (logical px)
        private const int MinContentWidth = 44;  // floor so a one-word line still forms a real bubble
        private const int TailHeight   = 16;
        private const int TextPad      = 12;
        private const int CornerRadius = 14;
        private const int TailInset    = 36; // preferred distance from left/right edge to tail centre
        private const int TailBase     = 11; // half-width of tail at body junction
        private const int BorderWidth  = 4;  // solid black outline thickness

        private string _fullText  = "";
        private int    _displayLen;
        private bool   _dismissed;
        private bool   _faceLeft;
        private int    _tailX;             // tail centre in local bubble coords, computed after clamping
        private bool   _tailOnTop;         // true when the bubble sits below the pet and the tail points up
        private int    _totalH;            // cached bubble height (body + tail); text is fixed while shown
        private int    _bubbleWidth;       // cached bubble width (shrink-to-fit, DPI-scaled)
        private int    _measuredDpi;       // DPI the cached geometry was measured at
        // Metrics scaled into device pixels for _measuredDpi (recomputed whenever the DPI changes):
        private int    _pad, _tailH, _corner, _tailBase, _border, _tailInset;
        private int _lastX = int.MinValue, _lastY = int.MinValue; // last placement, to skip no-op moves

        private readonly Timer _typeTimer    = new Timer { Interval = 25 };
        private readonly Timer _dismissTimer = new Timer();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int  MDT_EFFECTIVE_DPI = 0;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                // No WS_EX_LAYERED: shape comes from Form.Region, not colour-keying.
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        internal FormSpeech()
        {
            FormBorderStyle = FormBorderStyle.None;
            // The bubble is laid out by hand in device pixels and re-scaled per monitor below, so opt
            // out of WinForms font/DPI auto-scaling — it would fight the manual SetBounds geometry.
            AutoScaleMode   = AutoScaleMode.None;
            // Manual placement: SetBounds() runs before the handle exists on the first
            // ShowSpeech call. Without this the first Show() uses CW_USEDEFAULT and lands
            // top-left, ignoring those bounds; every later call repositions fine because
            // the handle already exists. Manual makes the first show honour Location too.
            StartPosition   = FormStartPosition.Manual;
            // BackColor white so any sub-pixel gap between the painted bubble and
            // the clipping Region shows white (matching the bubble), not magenta.
            // The Region — not a TransparencyKey — defines the visible shape.
            BackColor       = Color.White;
            TopMost         = true;
            ShowInTaskbar   = false;
            DoubleBuffered  = true;
            if (Program.ResourceChurnSelfTestActive)
                Opacity = 0d;

            _typeTimer.Tick    += TypeTimer_Tick;
            _dismissTimer.Tick += DismissTimer_Tick;
        }

        // The style for the CURRENT bubble (set by ShowSpeech; null => the default look). Both the measure and
        // the draw build their font from this, so a custom font wraps exactly the way it renders.
        private DesktopAICompanion.Modules.SpeechStyle _style;
        private const string DefaultFontFamily = "Segoe UI";
        private const float DefaultFontSize = 9f;

        private Font CreateTextFont()
        {
            DesktopAICompanion.Modules.SpeechStyle s = _style;
            string family = (s != null && !string.IsNullOrWhiteSpace(s.FontFamily)) ? s.FontFamily.Trim() : DefaultFontFamily;
            float size = (s != null && s.FontSize > 0f) ? s.FontSize : DefaultFontSize;
            if (size < 6f) size = 6f; else if (size > 24f) size = 24f;
            FontStyle fs = FontStyle.Regular;
            if (s != null) { if (s.Bold) fs |= FontStyle.Bold; if (s.Italic) fs |= FontStyle.Italic; if (s.Underline) fs |= FontStyle.Underline; }
            try { return new Font(family, size, fs); }
            catch { try { return new Font(DefaultFontFamily, DefaultFontSize, FontStyle.Regular); } catch { return new Font(FontFamily.GenericSansSerif, DefaultFontSize); } }
        }

        private Brush CreateTextBrush()
        {
            DesktopAICompanion.Modules.SpeechStyle s = _style;
            Color c = Color.Black;
            if (s != null && !string.IsNullOrWhiteSpace(s.TextColor))
            {
                string hex = s.TextColor.Trim();
                try
                {
                    if (hex.StartsWith("#") && hex.Length == 9)
                        c = Color.FromArgb(int.Parse(hex.Substring(1),
                            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
                    else
                        c = ColorTranslator.FromHtml(hex);
                }
                catch { c = Color.Black; }
            }
            return new SolidBrush(c);
        }

        /// <summary>
        /// Show a speech bubble above the pet.
        /// </summary>
        /// <param name="text">Text to display.</param>
        /// <param name="anchorX">Screen X of the pet's mouth (tail tip will point here).</param>
        /// <param name="petTopY">Top edge of the pet window (screen coords).</param>
        /// <param name="petBottomY">Bottom edge of the pet window (screen coords); used to place the bubble below when there's no room above.</param>
        /// <param name="durationSeconds">Seconds before auto-dismiss.</param>
        /// <param name="faceLeft">True when the pet is facing left.</param>
        internal void ShowSpeech(string text, int anchorX, int petTopY, int petBottomY, int durationSeconds, bool faceLeft, DesktopAICompanion.Modules.SpeechStyle style)
        {
            _style      = style;
            _dismissed  = false;
            // Trim stray leading/trailing whitespace so it can never pad the measured box.
            _fullText   = (text ?? "").Trim();
            _displayLen = 0;

            _typeTimer.Stop();
            _dismissTimer.Stop();

            // Text is fixed for the life of this bubble, so measure width+height once here at the DPI
            // this window paints at; Reposition() reuses the result and only re-measures if that DPI
            // later changes (the pet crosses onto a monitor with different scaling, or a Remote
            // Desktop reconnect rescales the session).
            RecomputeGeometry(PaintDpi(anchorX, petTopY));
            _lastX = _lastY = int.MinValue;   // force the first placement to apply
            Reposition(anchorX, petTopY, petBottomY, faceLeft);

            _dismissTimer.Interval = Math.Max(1000, durationSeconds * 1000);
            _typeTimer.Start();

            if (!Visible) Show();
            else          Invalidate();
        }

        /// <summary>True while a bubble is on screen and not yet auto-dismissed.</summary>
        internal bool IsShowing => Visible && !_dismissed;

        /// <summary>
        /// Keep the bubble in the same z-order policy as its pet. The pet polls fullscreen state
        /// even while stationary and propagates transitions here.
        /// </summary>
        internal void SetFullscreenSuppressed(bool suppressed)
        {
            if (IsDisposed) return;
            TopMost = !suppressed;
        }

        /// <summary>
        /// Place (or re-place) the bubble over the pet's mouth. FormCompanion calls this every tick
        /// so the bubble follows the pet as it walks or falls, instead of being orphaned at the
        /// spot where it first spoke. Recomputes position/tail only; leaves the typewriter and
        /// dismiss timers alone. A no-op when the placement hasn't changed.
        /// </summary>
        internal void Reposition(int anchorX, int petTopY, int petBottomY, bool faceLeft)
        {
            if (_dismissed) return;
            _faceLeft = faceLeft;

            // If this window's paint DPI has changed — the pet walked onto a monitor with different
            // scaling, or a Remote Desktop reconnect rescaled the session — re-measure the bubble at
            // the new DPI before placing it. FormCompanion calls this every tick while a bubble is showing,
            // so a DPI change self-heals within one frame instead of leaving a mis-sized bubble.
            int dpi = PaintDpi(anchorX, petTopY);
            if (dpi != _measuredDpi)
            {
                RecomputeGeometry(dpi);
                _lastX = _lastY = int.MinValue;   // geometry changed: force re-apply
            }

            int totalH  = _totalH;
            int bubbleW = _bubbleWidth;

            // Position bubble so the tail tip sits over anchorX
            int tailXLocal = _faceLeft ? _tailInset : bubbleW - _tailInset;
            int x = anchorX - tailXLocal;

            Rectangle wa = Screen.FromPoint(new Point(anchorX, petTopY)).WorkingArea;

            // Prefer above the pet (tail down). Flip below (tail up) only when the
            // bubble wouldn't fit above and does fit below — otherwise keep it above.
            int yAbove = petTopY    - totalH - 4;
            int yBelow = petBottomY + 4;
            bool tailOnTop = yAbove < wa.Top && yBelow + totalH <= wa.Bottom;
            int y = tailOnTop ? yBelow : yAbove;

            x = Math.Max(wa.Left, Math.Min(x, wa.Right  - bubbleW));
            y = Math.Max(wa.Top,  Math.Min(y, wa.Bottom - totalH));

            // After clamping, recalculate tail so it still points at the mouth
            int tailMargin = _corner + _tailBase + 2;
            int tailX = Math.Max(tailMargin, Math.Min(bubbleW - tailMargin, anchorX - x));

            // Skip when nothing moved — avoids churning the window/region every tick while idle.
            if (x == _lastX && y == _lastY && tailX == _tailX && tailOnTop == _tailOnTop) return;

            _tailX = tailX; _tailOnTop = tailOnTop; _lastX = x; _lastY = y;
            SetBounds(x, y, bubbleW, totalH);
            UpdateRegion();
            // Repaint the new shape. FormCompanion calls this every tick as the pet walks, and the tail slides
            // along the edge (or flips top/bottom) without the bubble changing size — a same-size window
            // move just blits the old pixels, so the painted outline would keep the OLD tail while the
            // Region already clips to the NEW one: stale black lines across the moved tail and a leftover
            // notch in the border where it used to be. Invalidating forces OnPaint to redraw the outline to
            // match the new Region. Guarded by the no-op check above, so an idle (unmoved) bubble never churns.
            Invalidate();
        }

        // Measure the bubble for the current text at the given monitor DPI: pick a shrink-to-fit
        // content width, wrap-measure the height at that width, and scale every drawing metric into
        // device pixels. Measuring on a bitmap whose resolution matches the target monitor makes the
        // wrap here agree with DrawString on the real (per-monitor-DPI) window.
        private void RecomputeGeometry(int dpi)
        {
            _measuredDpi = dpi;
            _pad       = Scale(TextPad, dpi);
            _tailH     = Scale(TailHeight, dpi);
            _corner    = Scale(CornerRadius, dpi);
            _tailBase  = Scale(TailBase, dpi);
            _border    = Math.Max(1, Scale(BorderWidth, dpi));
            _tailInset = Scale(TailInset, dpi);

            int maxContent = Scale(MaxContentWidth, dpi);
            int minContent = Scale(MinContentWidth, dpi);

            int contentW = maxContent;
            int contentH;
            using (var bmp = new Bitmap(1, 1))
            {
                bmp.SetResolution(dpi, dpi);
                using (var g = Graphics.FromImage(bmp))
                using (var f = CreateTextFont())
                {
                    // Natural single-line width; a few px of slack avoids a last-word wrap from
                    // rounding when the text nearly fits one line.
                    int natural = (int)Math.Ceiling(g.MeasureString(_fullText, f).Width) + Scale(4, dpi);
                    contentW = Math.Max(minContent, Math.Min(maxContent, natural));
                    contentH = (int)Math.Ceiling(g.MeasureString(_fullText, f, contentW).Height);
                }
            }

            _bubbleWidth = contentW + _pad * 2;
            _totalH      = contentH + _pad * 2 + _tailH;
        }

        private static int Scale(int logical, int dpi)
        {
            return (int)Math.Round(logical * dpi / 96.0, MidpointRounding.AwayFromZero);
        }

        // The DPI this bubble is actually PAINTED at. Once the window exists we read the DPI of the
        // window itself (GetDpiForWindow) rather than GetDpiForMonitor() at a screen point. Under
        // Remote Desktop those two disagree: the session virtualizes DPI, so a monitor-point query
        // can report a different value than the window's own device context (e.g. a monitor query
        // returning 120 while the window still paints at 96 right after a reconnect). Measuring the
        // text wrap at the monitor value while GDI+ draws it at the window value reserved a box for
        // the wrong DPI and left the text floating in whitespace — the oversized bubble. Reading the
        // window's own DPI keeps the measured wrap and the painted wrap on the same DPI by
        // construction. Before the handle exists (the very first ShowSpeech) there is no window yet,
        // so fall back to the monitor under the anchor; the next Reposition tick reconciles.
        private int PaintDpi(int anchorX, int anchorY)
        {
            if (IsHandleCreated)
            {
                uint d = GetDpiForWindow(Handle);
                if (d >= 72 && d <= 480) return (int)d;
            }
            return DpiForPoint(anchorX, anchorY);
        }

        // Effective DPI of the monitor under a screen point (per-monitor aware). Falls back to 96 on
        // any failure so the bubble still renders at 100% rather than throwing.
        private static int DpiForPoint(int x, int y)
        {
            try
            {
                IntPtr h = MonitorFromPoint(new Point(x, y), MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (h != IntPtr.Zero &&
                    GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 &&
                    dx >= 72 && dx <= 480)
                    return (int)dx;
            }
            catch { }
            return 96;
        }

        private void TypeTimer_Tick(object sender, EventArgs e)
        {
            if (_displayLen < _fullText.Length)
            {
                _displayLen = UnicodeTextProgress.NextCodePointBoundary(
                    _fullText,
                    _displayLen);
                Invalidate();
            }
            else
            {
                _typeTimer.Stop();
                _dismissTimer.Start();
            }
        }

        private void DismissTimer_Tick(object sender, EventArgs e)
        {
            _dismissTimer.Stop();
            _dismissed = true;
            Hide();
        }

        // Clip the window to the bubble outline so the OS handles transparency.
        private void UpdateRegion()
        {
            using (GraphicsPath path = BuildBubblePath())
            {
                Region old = Region;
                Region = new Region(path);
                old?.Dispose();
            }
        }

        // One closed path: rounded body with the tail notched into one edge.
        // Tail is on the bottom (pointing down) normally, or on the top (pointing up)
        // when the bubble sits below the pet. Walked clockwise from a top-left corner.
        private GraphicsPath BuildBubblePath()
        {
            int d     = _corner * 2;
            int right = _bubbleWidth - 1;
            var path  = new GraphicsPath();

            if (_tailOnTop)
            {
                int top = _tailH;   // body starts below the up-pointing tail
                int bot = Height - 1;
                path.AddArc(0,         top,       d, d, 180, 90); // top-left corner
                // Top edge → tail (up to tip at y=0) → top edge
                path.AddLine(_tailX - _tailBase, top, _tailX, 0);       // up to tip
                path.AddLine(_tailX, 0, _tailX + _tailBase, top);       // down to right base
                path.AddArc(right - d, top,       d, d, 270, 90); // top-right corner
                path.AddArc(right - d, bot - d,   d, d,   0, 90); // bottom-right
                path.AddArc(0,         bot - d,   d, d,  90, 90); // bottom-left
                path.CloseFigure();
            }
            else
            {
                int bot = Height - _tailH - 1;   // body bottom (bodyH - 1)
                path.AddArc(0,         0,         d, d, 180, 90); // top-left
                path.AddArc(right - d, 0,         d, d, 270, 90); // top-right
                path.AddArc(right - d, bot - d,   d, d,   0, 90); // bottom-right
                // Bottom edge → tail (down to tip) → bottom edge, then bottom-left corner
                path.AddLine(_tailX + _tailBase, bot, _tailX, Height - 1); // down to tip
                path.AddLine(_tailX, Height - 1, _tailX - _tailBase, bot);  // up to left base
                path.AddArc(0,         bot - d,   d, d,  90, 90); // bottom-left
                path.CloseFigure();
            }
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(_fullText) || _dismissed) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int bodyH = Height - _tailH;

            // ── Bubble: white fill + thick solid black outline ──────────────
            using (GraphicsPath path = BuildBubblePath())
            {
                g.FillPath(Brushes.White, path);
                using (var pen = new Pen(Color.Black, _border) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            }

            // ── Text ───────────────────────────────────────────────────────
            // When the tail is on top, the body is shifted down by the tail height.
            float textTop  = (_tailOnTop ? _tailH : 0) + _pad;
            string visible = _fullText.Substring(0, _displayLen);
            var textRect   = new RectangleF(_pad, textTop,
                                            _bubbleWidth - _pad * 2,
                                            bodyH        - _pad * 2);
            // The font is in points, so GDI+ renders it at this window's monitor DPI — the same DPI
            // RecomputeGeometry measured the wrap at, so drawn lines match the reserved height.
            using (var font  = CreateTextFont())
            using (var brush = CreateTextBrush())
            using (var sf    = new StringFormat {
                                  Alignment     = StringAlignment.Near,
                                  LineAlignment = StringAlignment.Near })
                g.DrawString(visible, font, brush, textRect, sf);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _typeTimer.Dispose();
                _dismissTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
