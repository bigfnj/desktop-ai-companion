using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopAICompanion.Ai
{
    internal sealed class ScreenCaptureContext
    {
        public string ActiveWindowTitle { get; private set; }
        public Rectangle MonitorBounds { get; private set; }

        public ScreenCaptureContext(string activeWindowTitle, Rectangle monitorBounds)
        {
            ActiveWindowTitle = activeWindowTitle ?? "";
            MonitorBounds = monitorBounds;
        }
    }

    /// <summary>
    /// Best-effort read of the foreground window's title (backlog 5.1: context awareness).
    /// Lets the brain say things like "busy in Visual Studio?" instead of reacting to raw
    /// screen text alone. Purely observational; never throws.
    /// </summary>
    internal static class ActiveWindow
    {
        private const int MaximumTitleCharacters = 512;

        // The pet's own process id, resolved once. Screen context must never come from one of the
        // pet's OWN windows: the primary pet form is titled "Sheep" and (unlike child sheep) is not
        // WS_EX_NOACTIVATE, so a poke/drag activates it and makes "Sheep" the foreground window --
        // which then routes every following contextual fortune straight into the sheep/wool jokes.
        // Treating our own foreground window as "no context" lets the caller fall back to a plain
        // random fortune instead of describing the pet to itself.
        private static readonly int OwnProcessId = SafeCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rectangle);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>Process name of the current foreground window (e.g. "chrome"), or "" if unavailable
        /// or the foreground window is one of the pet's own (see <see cref="OwnProcessId"/>).</summary>
        public static string ProcessName()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "";
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid <= 0 || pid == OwnProcessId) return "";
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                    return p.ProcessName ?? "";
            }
            catch { return ""; }
        }

        private static int SafeCurrentProcessId()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    return p.Id;
            }
            catch { return -1; }
        }

        /// <summary>True when the window belongs to the pet's own process, so it must not be read as
        /// screen context. Returns false on any failure (fail open to the normal capture path).</summary>
        private static bool IsOwnWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || OwnProcessId < 0) return false;
            try
            {
                int pid;
                GetWindowThreadProcessId(window, out pid);
                return pid == OwnProcessId;
            }
            catch { return false; }
        }

        /// <summary>Title of the current foreground window, or "" if none/unavailable.</summary>
        public static string Title()
        {
            try
            {
                return Title(GetForegroundWindow());
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Snapshot the foreground title and the monitor to capture from the same window handle.
        /// If no usable foreground window exists, fall back to the monitor containing the pet.
        /// </summary>
        public static ScreenCaptureContext CaptureContext(
            Rectangle fallbackMonitorBounds)
        {
            try
            {
                Rectangle fallback = fallbackMonitorBounds;
                if (fallback.Width <= 0 || fallback.Height <= 0)
                    fallback = Screen.PrimaryScreen != null
                        ? Screen.PrimaryScreen.Bounds
                        : new Rectangle(0, 0, 1, 1);

                IntPtr foreground = GetForegroundWindow();
                // Never read one of the pet's own windows (e.g. the "Sheep" pet form after a
                // poke/drag) as context: blank the title so the caller goes random, and ignore its
                // bounds so monitor selection falls back to the pet's own monitor below.
                bool ownForeground = IsOwnWindow(foreground);
                string title = ownForeground ? "" : Title(foreground);
                Rectangle foregroundBounds = Rectangle.Empty;
                if (!ownForeground && foreground != IntPtr.Zero &&
                    GetWindowRect(foreground, out WindowRect rectangle))
                {
                    long width = (long)rectangle.Right - rectangle.Left;
                    long height = (long)rectangle.Bottom - rectangle.Top;
                    if (width > 0 && width <= int.MaxValue &&
                        height > 0 && height <= int.MaxValue)
                    {
                        foregroundBounds = new Rectangle(
                            rectangle.Left,
                            rectangle.Top,
                            (int)width,
                            (int)height);
                    }
                }

                Screen[] screens = Screen.AllScreens;
                Rectangle[] monitors = new Rectangle[screens.Length];
                for (int index = 0; index < screens.Length; index++)
                    monitors[index] = screens[index].Bounds;
                Rectangle selected = DesktopGeometry.SelectCaptureMonitor(
                    foregroundBounds,
                    fallback,
                    monitors);
                return new ScreenCaptureContext(title, selected);
            }
            catch
            {
                Rectangle fallback = fallbackMonitorBounds;
                if (fallback.Width <= 0 || fallback.Height <= 0)
                    fallback = new Rectangle(0, 0, 1, 1);
                return new ScreenCaptureContext("", fallback);
            }
        }

        private static string Title(IntPtr window)
        {
            if (window == IntPtr.Zero) return "";
            int length = GetWindowTextLength(window);
            if (length <= 0) return "";

            // Read one code unit beyond the retained limit so the shared truncator
            // can see and remove a surrogate pair that straddles the boundary.
            length = Math.Min(length, MaximumTitleCharacters + 1);
            StringBuilder text = new StringBuilder(length + 1);
            GetWindowText(window, text, text.Capacity);
            string title = text.ToString().Trim();
            return UnicodeTextProgress.TruncateAtCodePointBoundary(
                title,
                MaximumTitleCharacters);
        }
    }
}
