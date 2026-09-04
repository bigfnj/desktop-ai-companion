using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    /// <summary>
    /// Detects, per monitor, whether a fullscreen (borderless or exclusive) window occupies it,
    /// independent of which window currently has focus. The foreground-only check in
    /// <c>FormCompanion.CheckFullScreen</c> misses a borderless game the moment the pet (or anything else)
    /// takes focus over it; this walks the z-order instead, ignoring the pet's own windows and the
    /// shell so a sheep sitting on top of a borderless game does not mask the game underneath.
    /// </summary>
    internal static class FullscreenScan
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr hWnd, int attribute, out int value, int size);

        private const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// One flag per <see cref="Screen.AllScreens"/> entry: true when a fullscreen window occupies
        /// that monitor. The first ordinary (non-pet, non-shell, visible, un-cloaked) window covering
        /// a monitor's center — i.e. the topmost real window there — decides that monitor, so a
        /// fullscreen app hidden behind the active window does not count and a normal window on top of
        /// a game does not hide it. <paramref name="petHandles"/> are always excluded.
        /// </summary>
        public static bool[] BlockedMonitors(ICollection<IntPtr> petHandles)
        {
            Screen[] screens = Screen.AllScreens;
            var blocked = new bool[screens.Length];
            if (screens.Length == 0) return blocked;

            var decided = new bool[screens.Length];
            int remaining = screens.Length;

            try
            {
                EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    if (remaining <= 0) return false;           // every monitor decided; stop early
                    if (petHandles != null && petHandles.Contains(hWnd)) return true;
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                    if (IsCloaked(hWnd) || IsShell(hWnd)) return true;
                    if (!GetWindowRect(hWnd, out RECT r)) return true;

                    Rectangle bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    if (bounds.Width <= 0 || bounds.Height <= 0) return true;

                    for (int i = 0; i < screens.Length; i++)
                    {
                        if (decided[i]) continue;
                        Rectangle mon = screens[i].Bounds;
                        if (!bounds.Contains(DesktopGeometry.Center(mon))) continue;
                        decided[i] = true;
                        remaining--;
                        if (DesktopGeometry.IsFullscreenOnMonitor(bounds, mon))
                            blocked[i] = true;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // A hostile or racing window handle must never crash the pet: treat the desktop as clear.
                return new bool[screens.Length];
            }

            return blocked;
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

        // ---- diagnostic ---------------------------------------------------------
        public static bool SelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-fullscreen-selftest.txt");
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                int screenCount = Screen.AllScreens.Length;
                bool[] blocked = BlockedMonitors(new HashSet<IntPtr>());
                sb.AppendLine("screens=" + screenCount + " blocked_len=" + blocked.Length);
                if (blocked.Length != screenCount) ok = false;

                // Decision logic is deterministic; validate it here (BlockedMonitors is environmental).
                var mons = new List<Rectangle>
                {
                    new Rectangle(0, 0, 1920, 1080),
                    new Rectangle(1920, 0, 1920, 1080),
                    new Rectangle(5000, 0, 1920, 1080),
                };
                ok = Expect(sb, "clear-current", -1,
                    DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { false, true, true })) && ok;
                ok = Expect(sb, "nearest-free", 1,
                    DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { true, false, false })) && ok;
                ok = Expect(sb, "nearest-by-center", 1,
                    DesktopGeometry.ChooseRelocationTarget(2, mons, new[] { false, false, true })) && ok;
                ok = Expect(sb, "all-blocked", -1,
                    DesktopGeometry.ChooseRelocationTarget(0, mons, new[] { true, true, true })) && ok;
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(outp, sb.ToString()); }
            catch { return false; }
            return ok;
        }

        private static bool Expect(StringBuilder sb, string label, int expected, int actual)
        {
            bool pass = expected == actual;
            sb.AppendLine(label + " expected=" + expected + " actual=" + actual +
                (pass ? " OK" : " FAIL"));
            return pass;
        }
    }
}
