using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    /// <summary>One real application window, as the desktop currently presents it.</summary>
    internal sealed class DesktopWindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string ProcessName;
        /// <summary>Visual bounds. DWM extended frame bounds where available, because
        /// <c>GetWindowRect</c> includes an invisible resize border (~7px a side on Windows 10/11) and a
        /// capture sized from it gets a band of whatever is behind the window.</summary>
        public Rectangle Bounds;
        /// <summary>Index into <see cref="Screen.AllScreens"/>, or -1 when it overlaps none.</summary>
        public int MonitorIndex;
        public bool IsForeground;
        /// <summary>0 is frontmost. <c>EnumWindows</c> yields top-down z-order, which is where this
        /// comes from; it is the whole reason a caller can say "the front app, but I know what is
        /// behind it".</summary>
        public int ZOrder;
    }

    /// <summary>
    /// A snapshot of which application windows are on which monitor, in z-order.
    ///
    /// This is a generalisation of <see cref="FullscreenScan"/>, which already walks the z-order per
    /// monitor and already carries the filters that make such a walk trustworthy. Those filters are the
    /// hard part and are not obvious: without the <c>DWMWA_CLOAKED</c> test a plain <c>EnumWindows</c>
    /// returns dozens of invisible UWP and other-virtual-desktop windows and every consumer draws the
    /// wrong conclusion. Reusing that hard-won filter set is the point of putting this next to it rather
    /// than writing a second walk.
    ///
    /// Two things it deliberately does differently:
    ///   * It never stops early. <c>BlockedMonitors</c> quits once every monitor is decided because it
    ///     only needs the topmost window per monitor; a caller asking "what is open" needs all of them.
    ///   * Monitor assignment is by LARGEST INTERSECTION, not by "the window contains the monitor's
    ///     centre". Centre-containment is right for detecting a fullscreen window and wrong for a normal
    ///     one: a 900px window sitting in a corner contains no monitor centre at all and would be
    ///     reported as being on no monitor.
    ///
    /// Cheap enough to call on demand (one <c>EnumWindows</c> pass, bounded), so it needs no cache and
    /// no timer. Never throws: a hostile or racing handle yields a shorter list, never an exception,
    /// matching <see cref="FullscreenScan"/>'s stance that the desktop is not worth crashing a companion
    /// over.
    /// </summary>
    internal static class DesktopWindows
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

        private const int DWMWA_CLOAKED = 14;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;

        /// <summary>A pathological desktop must not turn into an unbounded list or an unbounded prompt.
        /// Well past any real session; a machine with more than this open has bigger problems.</summary>
        private const int MaximumWindows = 64;

        /// <summary>Longest title kept. Titles reach the AI and the watcher, so they are bounded here
        /// rather than at each consumer.</summary>
        private const int MaximumTitleLength = 200;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// Every ordinary application window, frontmost first. <paramref name="petHandles"/> are
        /// excluded so a companion never reports itself as something the user is looking at, exactly as
        /// <see cref="FullscreenScan"/> excludes them.
        /// </summary>
        public static List<DesktopWindowInfo> Snapshot(ICollection<IntPtr> petHandles)
        {
            var result = new List<DesktopWindowInfo>();
            try
            {
                Screen[] screens = Screen.AllScreens;
                var monitors = new Rectangle[screens.Length];
                for (int i = 0; i < screens.Length; i++) monitors[i] = screens[i].Bounds;

                IntPtr foreground = GetForegroundWindow();
                // One process lookup per pid, not per window: a browser with eight windows would
                // otherwise open eight process handles to learn the same string.
                var processNames = new Dictionary<int, string>();
                int z = 0;

                EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    if (result.Count >= MaximumWindows) return false;
                    if (!IsInteresting(hWnd, petHandles, foreground)) return true;

                    Rectangle bounds = VisualBounds(hWnd);
                    if (bounds.Width <= 0 || bounds.Height <= 0) return true;

                    result.Add(new DesktopWindowInfo
                    {
                        Handle = hWnd,
                        Title = Title(hWnd),
                        ProcessName = ProcessNameFor(hWnd, processNames),
                        Bounds = bounds,
                        MonitorIndex = MonitorIndexFor(bounds, monitors),
                        IsForeground = hWnd == foreground,
                        ZOrder = z++,
                    });
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // Same stance as FullscreenScan: report what was gathered, never throw at the caller.
            }
            return result;
        }

        /// <summary>The frontmost ordinary window, or null. Convenience over <see cref="Snapshot"/> for
        /// the common "what is the user actually looking at" question.</summary>
        public static DesktopWindowInfo Foreground(ICollection<IntPtr> petHandles)
        {
            List<DesktopWindowInfo> all = Snapshot(petHandles);
            for (int i = 0; i < all.Count; i++) if (all[i].IsForeground) return all[i];
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// Which monitor a window is "on", by largest overlapping area. Pure, so the awkward cases are
        /// testable without arranging real monitors: a window straddling two screens belongs to the one
        /// showing more of it, and a window entirely offscreen belongs to none (-1) rather than
        /// defaulting to the primary and quietly capturing the wrong display.
        /// </summary>
        public static int MonitorIndexFor(Rectangle bounds, IList<Rectangle> monitors)
        {
            if (monitors == null) return -1;
            int best = -1;
            long bestArea = 0;
            for (int i = 0; i < monitors.Count; i++)
            {
                Rectangle hit = Rectangle.Intersect(bounds, monitors[i]);
                if (hit.Width <= 0 || hit.Height <= 0) continue;
                long area = (long)hit.Width * hit.Height;
                if (area > bestArea) { bestArea = area; best = i; }
            }
            return best;
        }

        // ---- filters -------------------------------------------------------------------------------

        private static bool IsInteresting(IntPtr hWnd, ICollection<IntPtr> petHandles, IntPtr foreground)
        {
            if (petHandles != null && petHandles.Contains(hWnd)) return false;
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return false;
            if (IsCloaked(hWnd) || IsShell(hWnd)) return false;
            // A tool window is a palette or a floating strip, not something the user "has open".
            if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
            // An owned window is a dialog or popup belonging to something already in the list. Keep it
            // only while it holds focus, because then it IS what the user is looking at.
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero && hWnd != foreground) return false;
            // A titleless top-level window is almost always a helper surface. The foreground window is
            // exempt: a fullscreen game or video player legitimately has no title.
            if (hWnd != foreground && GetWindowTextLength(hWnd) <= 0) return false;
            return true;
        }

        private static bool IsCloaked(IntPtr hWnd)
        {
            try
            {
                return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                    && cloaked != 0;
            }
            catch { return false; }
        }

        private static bool IsShell(IntPtr hWnd)
        {
            var name = new StringBuilder(64);
            if (GetClassName(hWnd, name, name.Capacity) <= 0) return false;
            switch (name.ToString())
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "SysListView32":
                    return true;
                default:
                    return false;
            }
        }

        // ---- readers -------------------------------------------------------------------------------

        /// <summary>Visual bounds, preferring DWM's extended frame over <c>GetWindowRect</c>. The
        /// difference is the invisible resize border, and it is the difference between a window capture
        /// that is just the window and one framed by a strip of the wallpaper behind it.</summary>
        private static Rectangle VisualBounds(IntPtr hWnd)
        {
            try
            {
                if (DwmGetWindowAttribute(
                        hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    Rectangle dwm = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
                    if (dwm.Width > 0 && dwm.Height > 0) return dwm;
                }
            }
            catch { }
            try
            {
                if (GetWindowRect(hWnd, out RECT r))
                    return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch { }
            return Rectangle.Empty;
        }

        private static string Title(IntPtr hWnd)
        {
            try
            {
                int length = GetWindowTextLength(hWnd);
                if (length <= 0) return "";
                var text = new StringBuilder(length + 1);
                if (GetWindowText(hWnd, text, text.Capacity) <= 0) return "";
                string s = text.ToString();
                return s.Length > MaximumTitleLength ? s.Substring(0, MaximumTitleLength) : s;
            }
            catch { return ""; }
        }

        private static string ProcessNameFor(IntPtr hWnd, Dictionary<int, string> cache)
        {
            try
            {
                if (GetWindowThreadProcessId(hWnd, out int pid) == 0 || pid <= 0) return "";
                string cached;
                if (cache.TryGetValue(pid, out cached)) return cached;
                string name = "";
                try
                {
                    using (Process p = Process.GetProcessById(pid)) name = p.ProcessName ?? "";
                }
                catch
                {
                    // A protected or already-exited process is nameless, not fatal.
                }
                cache[pid] = name;
                return name;
            }
            catch { return ""; }
        }

        // ---- diagnostic ---------------------------------------------------------

        /// <summary>
        /// Monitor assignment is pure, so it is asserted against arranged rectangles rather than real
        /// hardware; the live walk is only smoke-checked, because what it returns depends on whatever
        /// the machine happens to have open. Same split as <see cref="FullscreenScan.SelfTest"/>.
        /// </summary>
        public static bool SelfTest()
        {
            var sb = new StringBuilder();
            bool ok = true;

            var mons = new List<Rectangle>
            {
                new Rectangle(0, 0, 2560, 1440),
                new Rectangle(2560, 0, 1920, 1080),
            };

            ok = Expect(sb, "wholly on the primary", 0,
                MonitorIndexFor(new Rectangle(100, 100, 800, 600), mons)) && ok;
            ok = Expect(sb, "wholly on the second", 1,
                MonitorIndexFor(new Rectangle(2600, 100, 800, 600), mons)) && ok;
            // The case centre-containment gets wrong: a small window in a corner contains no monitor
            // centre at all, and must still be attributed to the monitor showing it.
            ok = Expect(sb, "small corner window still lands on a monitor", 0,
                MonitorIndexFor(new Rectangle(0, 0, 200, 120), mons)) && ok;
            // Straddling: 1560 px of it is on the primary, 400 on the second.
            ok = Expect(sb, "straddling goes to the larger share", 0,
                MonitorIndexFor(new Rectangle(1000, 100, 1960, 600), mons)) && ok;
            // ...and the reverse, so the comparison is real and not an ordering artefact.
            ok = Expect(sb, "straddling the other way goes to the second", 1,
                MonitorIndexFor(new Rectangle(2400, 100, 1500, 600), mons)) && ok;
            ok = Expect(sb, "entirely offscreen belongs to no monitor", -1,
                MonitorIndexFor(new Rectangle(-5000, -5000, 100, 100), mons)) && ok;
            ok = Expect(sb, "a degenerate rect belongs to no monitor", -1,
                MonitorIndexFor(new Rectangle(100, 100, 0, 0), mons)) && ok;
            ok = Expect(sb, "no monitors at all is handled", -1,
                MonitorIndexFor(new Rectangle(0, 0, 100, 100), new List<Rectangle>())) && ok;

            // Live walk: bounded, self-consistent, and never containing our own windows.
            try
            {
                List<DesktopWindowInfo> live = Snapshot(new HashSet<IntPtr>());
                sb.AppendLine("live windows=" + live.Count);
                if (live.Count > MaximumWindows)
                {
                    ok = false;
                    sb.AppendLine("FAIL live walk exceeded its cap");
                }
                int foregroundCount = 0;
                for (int i = 0; i < live.Count; i++)
                {
                    DesktopWindowInfo w = live[i];
                    if (w.IsForeground) foregroundCount++;
                    if (w.ZOrder != i)
                    {
                        ok = false;
                        sb.AppendLine("FAIL z-order is not the list order at " + i);
                        break;
                    }
                    if (w.Bounds.Width <= 0 || w.Bounds.Height <= 0)
                    {
                        ok = false;
                        sb.AppendLine("FAIL a degenerate window survived the filters: " + w.Title);
                        break;
                    }
                    if (w.Title != null && w.Title.Length > MaximumTitleLength)
                    {
                        ok = false;
                        sb.AppendLine("FAIL an unbounded title survived: " + w.Title.Length);
                        break;
                    }
                }
                if (foregroundCount > 1)
                {
                    ok = false;
                    sb.AppendLine("FAIL more than one window claims foreground: " + foregroundCount);
                }
                for (int i = 0; i < live.Count && i < 8; i++)
                {
                    DesktopWindowInfo w = live[i];
                    sb.AppendLine("  z=" + w.ZOrder + " mon=" + w.MonitorIndex +
                                  (w.IsForeground ? " [front]" : "        ") +
                                  " " + w.Bounds.Width + "x" + w.Bounds.Height +
                                  " " + w.ProcessName + " :: " + w.Title);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("FAIL live walk threw: " + ex.Message);
            }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            Console.Out.Write(sb.ToString());
            Console.Out.Flush();
            return ok;
        }

        private static bool Expect(StringBuilder sb, string what, int expected, int actual)
        {
            bool ok = expected == actual;
            sb.AppendLine((ok ? "PASS: " : "FAIL: ") + what +
                          (ok ? "" : " (expected " + expected + ", got " + actual + ")"));
            return ok;
        }
    }
}
