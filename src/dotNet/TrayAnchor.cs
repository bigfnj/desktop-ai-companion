using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    // Where the question card goes (app 1.6.0, lane feature/tray-questions): just above the notification area, on whichever
    // edge of whichever monitor the taskbar is. Nothing in the app located the taskbar before this; WorkingArea was only
    // ever used to CLAMP things (FormSpeech, FormCompanion, the options window).
    //
    // READ WITHOUT ASKING EXPLORER. SHAppBarMessage is the documented way to ask where the taskbar is, and it is a
    // synchronous SendMessage to Explorer: when Explorer hangs -- which is exactly when a tray icon goes missing, the
    // situation this app's tray code has the most history with -- the UI thread hangs with it. FindWindow, GetWindowRect
    // and GetMonitorInfo read the window manager's own state and cannot block on another process.
    //
    // The primary taskbar only: the notification area exists only there (a secondary taskbar has a clock at most).

    internal enum TaskbarEdge { Bottom = 0, Top = 1, Left = 2, Right = 3 }

    internal struct TaskbarInfo
    {
        internal bool Found;            // false: no Shell_TrayWnd; the rest describe the primary screen as a fallback
        internal Rectangle Monitor;     // the taskbar's monitor, physical pixels
        internal Rectangle WorkArea;
        internal Rectangle Taskbar;
        internal TaskbarEdge Edge;
        internal bool AutoHide;         // the taskbar reserves no space, so the work area runs under it
        internal bool TrayAtStart;      // the notification area is at the left/top end (a right-to-left layout)
        internal int Dpi;
    }

    internal static class TrayAnchor
    {
        /// <summary>The gap between the card and the taskbar / screen edge, in 96-DPI pixels.</summary>
        internal const int MarginLogical = 12;

        internal static TaskbarInfo Read()
        {
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                RECT r;
                if (tray != IntPtr.Zero && GetWindowRect(tray, out r))
                {
                    IntPtr monitor = MonitorFromWindow(tray, MONITOR_DEFAULTTOPRIMARY);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi))
                    {
                        var info = new TaskbarInfo
                        {
                            Found = true,
                            Monitor = mi.rcMonitor.ToRectangle(),
                            WorkArea = mi.rcWork.ToRectangle(),
                            Taskbar = r.ToRectangle(),
                            Dpi = DpiOf(monitor),
                        };
                        info.Edge = EdgeOf(info.Taskbar, info.Monitor);
                        info.AutoHide = ReservesNothing(info.Edge, info.WorkArea, info.Monitor);
                        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                        RECT n;
                        if (notify != IntPtr.Zero && GetWindowRect(notify, out n))
                            info.TrayAtStart = IsAtStart(info.Edge, info.Taskbar, n.ToRectangle());
                        return info;
                    }
                }
            }
            catch { }
            Screen primary = Screen.PrimaryScreen;
            Rectangle bounds = primary != null ? primary.Bounds : new Rectangle(0, 0, 1920, 1080);
            Rectangle work = primary != null ? primary.WorkingArea : bounds;
            return new TaskbarInfo { Found = false, Monitor = bounds, WorkArea = work, Taskbar = Rectangle.Empty, Edge = TaskbarEdge.Bottom, Dpi = 96 };
        }

        /// <summary>Which edge a taskbar rectangle hugs: a wide one is at the top or bottom, a tall one at a side, and the
        /// half of the monitor its centre is in says which.</summary>
        internal static TaskbarEdge EdgeOf(Rectangle taskbar, Rectangle monitor)
        {
            if (taskbar.IsEmpty || monitor.IsEmpty) return TaskbarEdge.Bottom;
            int cx = taskbar.Left + taskbar.Width / 2, cy = taskbar.Top + taskbar.Height / 2;
            if (taskbar.Width >= taskbar.Height)
                return cy < monitor.Top + monitor.Height / 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
            return cx < monitor.Left + monitor.Width / 2 ? TaskbarEdge.Left : TaskbarEdge.Right;
        }

        /// <summary>An auto-hiding taskbar reserves nothing: the work area reaches the monitor's edge on the taskbar's side.</summary>
        internal static bool ReservesNothing(TaskbarEdge edge, Rectangle work, Rectangle monitor)
        {
            switch (edge)
            {
                case TaskbarEdge.Top: return work.Top <= monitor.Top;
                case TaskbarEdge.Left: return work.Left <= monitor.Left;
                case TaskbarEdge.Right: return work.Right >= monitor.Right;
                default: return work.Bottom >= monitor.Bottom;
            }
        }

        internal static bool IsAtStart(TaskbarEdge edge, Rectangle taskbar, Rectangle notify)
        {
            if (notify.IsEmpty) return false;
            if (edge == TaskbarEdge.Bottom || edge == TaskbarEdge.Top)
                return notify.Left + notify.Width / 2 < taskbar.Left + taskbar.Width / 2;
            return notify.Top + notify.Height / 2 < taskbar.Top + taskbar.Height / 2;
        }

        /// <summary>Where the card may sit: the work area, or for an auto-hiding taskbar the monitor less the taskbar's
        /// thickness on its edge, so the card does not sit under the taskbar when it slides out.</summary>
        internal static Rectangle PlacementArea(TaskbarInfo tb)
        {
            if (!tb.AutoHide || tb.Taskbar.IsEmpty) return tb.WorkArea;
            Rectangle m = tb.Monitor;
            int thick = Math.Min(tb.Taskbar.Width, tb.Taskbar.Height);
            switch (tb.Edge)
            {
                case TaskbarEdge.Top: return new Rectangle(m.Left, m.Top + thick, m.Width, m.Height - thick);
                case TaskbarEdge.Left: return new Rectangle(m.Left + thick, m.Top, m.Width - thick, m.Height);
                case TaskbarEdge.Right: return new Rectangle(m.Left, m.Top, m.Width - thick, m.Height);
                default: return new Rectangle(m.Left, m.Top, m.Width, m.Height - thick);
            }
        }

        /// <summary>
        /// The card's top-left, in physical pixels: in the corner of the placement area nearest the notification area,
        /// a margin in from both edges, and never outside the area. Bottom taskbar: bottom-right (bottom-left when the
        /// tray is at the start). Top: top-right. Left or right: the bottom of that side, beside the tray.
        /// </summary>
        internal static Point Compute(TaskbarInfo tb, Size card)
        {
            Rectangle area = PlacementArea(tb);
            int dpi = tb.Dpi >= 72 && tb.Dpi <= 480 ? tb.Dpi : 96;
            int m = (int)Math.Round(MarginLogical * dpi / 96.0, MidpointRounding.AwayFromZero);
            int w = card.Width, h = card.Height;
            int x, y;
            switch (tb.Edge)
            {
                case TaskbarEdge.Top:
                    x = tb.TrayAtStart ? area.Left + m : area.Right - m - w;
                    y = area.Top + m;
                    break;
                case TaskbarEdge.Left:
                    x = area.Left + m;
                    y = tb.TrayAtStart ? area.Top + m : area.Bottom - m - h;
                    break;
                case TaskbarEdge.Right:
                    x = area.Right - m - w;
                    y = tb.TrayAtStart ? area.Top + m : area.Bottom - m - h;
                    break;
                default:
                    x = tb.TrayAtStart ? area.Left + m : area.Right - m - w;
                    y = area.Bottom - m - h;
                    break;
            }
            x = Math.Max(area.Left, Math.Min(x, area.Right - w));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - h));
            return new Point(x, y);
        }

        /// <summary>The index in Screen.AllScreens of the monitor with these bounds, or -1.</summary>
        internal static int ScreenIndexOf(Rectangle monitorBounds)
        {
            try
            {
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                    if (screens[i].Bounds == monitorBounds) return i;
            }
            catch { }
            return -1;
        }

        private static int DpiOf(IntPtr monitor)
        {
            try
            {
                uint dx, dy;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx >= 72 && dx <= 480) return (int)dx;
            }
            catch { }
            return 96;
        }

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }

    /// <summary>
    /// Whether the question card may be shown now (app 1.6.0). The shell's own notification state first -- the same call
    /// Reminder's PresentationState makes -- then a fullscreen window on the TRAY's monitor, per the owner's per-monitor
    /// rule of 2026-10-07: a game on another monitor does not hold a card that would appear beside the user's work.
    /// </summary>
    internal static class UserPresence
    {
        /// <summary>The shell's answer, mapped. 1 NOT_PRESENT: locked, a screensaver, or another user's session. 2 BUSY: a
        /// fullscreen app or presentation settings. 3 D3D fullscreen. 4 presentation mode. 6 QUIET_TIME: Windows' quiet
        /// period (the first hour after setup or a feature update); NOT the user's Do Not Disturb, which the shell may not
        /// report at all. 7 a fullscreen Store app. 5 accepts notifications, and anything unknown, is no hold.</summary>
        internal static QuestionHold FromNotificationState(int state)
        {
            switch (state)
            {
                case 1: return QuestionHold.Locked;
                case 2: return QuestionHold.Fullscreen;
                case 3: return QuestionHold.Fullscreen;
                case 4: return QuestionHold.Presenting;
                case 6: return QuestionHold.Quiet;
                case 7: return QuestionHold.Fullscreen;
                default: return QuestionHold.None;
            }
        }

        internal static QuestionHold Current(StartUp startUp)
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) == 0)
                {
                    QuestionHold shell = FromNotificationState(state);
                    if (shell != QuestionHold.None) return shell;
                }
            }
            catch { }
            if (startUp == null) return QuestionHold.None;
            try
            {
                bool[] blocked = startUp.BlockedMonitorsForStandDown();
                int index = TrayAnchor.ScreenIndexOf(TrayAnchor.Read().Monitor);
                if (blocked != null && index >= 0 && index < blocked.Length && blocked.Length == Screen.AllScreens.Length)
                    return blocked[index] ? QuestionHold.Fullscreen : QuestionHold.None;
                // The scan and the monitor list disagree (a display plugged in mid-cycle): fall back to "anything
                // fullscreen anywhere", which holds too much rather than showing over a game.
                return startUp.IsFullscreenActive ? QuestionHold.Fullscreen : QuestionHold.None;
            }
            catch { return QuestionHold.None; }
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
    }
}
