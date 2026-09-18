// End-to-end check of the fullscreen stand-down in the REAL app, measured through the window
// manager rather than by looking at a screenshot.
//
// THE INVARIANT IS PER MONITOR, and stating it as "the companion hides" is wrong -- that was the
// first version of this file and a second display being plugged in mid-session exposed it. What
// the app promises is: no companion is VISIBLE ON A BLOCKED MONITOR. With one monitor free the
// correct behaviour is to RELOCATE there, not to disappear; only when every monitor is blocked
// must it hide. So the probe runs both cases:
//
//   ONE   cover the primary monitor only -> every tracked companion must be either not visible,
//         or standing on a monitor that is not blocked
//   ALL   cover every monitor -> every tracked companion must be not visible, because there is
//         nowhere left to go
//
// Visibility is read with IsWindowVisible on the app's own HWNDs, captured once up front, so the
// verdict is the window's own state and not an image diff or a guess from its shape.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class StandDown
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy,
                                            uint flags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(128);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    // A companion window: a real top-level WinForms Form (the ".Window.8." classes; ".Window.0."
    // are controls) owned by the app and NOT in the -32000 parking slot WinForms uses for hidden
    // helpers, which is where the tray host and the tray watcher sit.
    //
    // Deliberately NOT filtered by size. The first version required 24x24 and passed the check for
    // the wrong reason: a companion walking off the top edge is clipped to a 40x2 window, so "no
    // companion is visible" was true because the sprite was thin while IsWindowVisible still said
    // VIS. The point is to read the window's own visibility, so nothing else may decide it.
    private static bool LooksLikeCompanion(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        if (r.Left <= -30000 || r.Top <= -30000) return false;
        return ClassOf(h).StartsWith("WindowsForms10.Window.8", StringComparison.Ordinal);
    }

    private static List<IntPtr> CompanionWindows(uint pid)
    {
        var found = new List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h) && LooksLikeCompanion(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int MonitorOf(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return -1;
        var centre = new Point(r.Left + (r.Right - r.Left) / 2, r.Top + (r.Bottom - r.Top) / 2);
        Screen[] all = Screen.AllScreens;
        for (int i = 0; i < all.Length; i++) if (all[i].Bounds.Contains(centre)) return i;
        // Straddling or off-desktop: fall back to nearest, the same choice Screen.FromRectangle makes.
        Screen nearest = Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        for (int i = 0; i < all.Length; i++) if (all[i].DeviceName == nearest.DeviceName) return i;
        return -1;
    }

    // The invariant, evaluated for one companion: not visible, or visible on a monitor that is not
    // blocked.
    private static bool StoodDown(IntPtr h, bool[] blocked)
    {
        if (!IsWindowVisible(h)) return true;
        int m = MonitorOf(h);
        if (m < 0 || m >= blocked.Length) return true;   // cannot place it; not evidence of a fault
        return !blocked[m];
    }

    // The STRONGER form, and the one that makes this probe worth running: with one monitor covered
    // and another free, an unpinned companion must RELOCATE to the free one -- "moves to a free
    // monitor rather than vanishing", which is what CheckFullScreen promises and what
    // ChooseRelocationTarget implements.
    //
    // StoodDown alone cannot see this half: a cache that collapsed the per-monitor array to "a game
    // is running somewhere" would hide the companion, and hiding satisfies "not on a blocked
    // monitor". Since the collapse is the plausible optimisation that re-ships a shipped bug, the
    // probe has to require the companion to be VISIBLE and ELSEWHERE, not merely gone.
    private static bool RelocatedRatherThanVanished(IntPtr h, bool[] blocked)
    {
        if (!IsWindowVisible(h)) return false;
        int m = MonitorOf(h);
        return m >= 0 && m < blocked.Length && !blocked[m];
    }

    private static string Report(List<IntPtr> tracked, bool[] blocked)
    {
        var sb = new System.Text.StringBuilder();
        foreach (IntPtr h in tracked)
        {
            RECT r;
            GetWindowRect(h, out r);
            int m = MonitorOf(h);
            sb.Append(" ['" + TitleOf(h) + "' " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
                " @" + r.Left + "," + r.Top + " mon" + m +
                (IsWindowVisible(h) ? " VIS" : " hid") +
                (StoodDown(h, blocked) ? " ok" : " OVER-A-GAME") + "]");
        }
        return sb.ToString();
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs, List<Form> hold,
                               out double elapsedMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            // Hold the probe windows at the top. Another window coming to the front makes the
            // desktop genuinely un-blocked and the app correctly stops standing down, which would
            // be scored as an app failure -- one run in six did exactly that, in BOTH arms.
            foreach (Form f in hold)
                SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (condition()) { elapsedMs = sw.Elapsed.TotalMilliseconds; return true; }
            Thread.Sleep(25);
        }
        elapsedMs = sw.Elapsed.TotalMilliseconds;
        return false;
    }

    private static Form Cover(Rectangle bounds)
    {
        var f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.Manual;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Bounds = bounds;
        f.BackColor = Color.Black;
        f.Opacity = 0.02;      // present to the window manager, invisible to the user
        f.Text = "fullscreen-standdown-probe";
        f.Show();
        Application.DoEvents();
        return f;
    }

    internal static int Run(string exePath, string dataRoot, int phaseDelayMs)
    {
        Screen[] screens = Screen.AllScreens;
        var psi = new ProcessStartInfo(exePath);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
        psi.EnvironmentVariables["DESKTOP_AI_COMPANION_DATA_ROOT"] = dataRoot;
        Process app = Process.Start(psi);
        if (app == null) { Console.WriteLine("RESULT=FAIL could not start " + exePath); return 1; }
        uint pid = (uint)app.Id;
        Console.WriteLine("app pid=" + pid + " monitors=" + screens.Length +
            " phaseDelay=" + phaseDelayMs + "ms");

        int failures = 0;
        var covers = new List<Form>();
        try
        {
            double t;
            var none = new bool[screens.Length];
            bool up = WaitFor(delegate { return CompanionWindows(pid).Count > 0; }, 40000, covers, out t);
            List<IntPtr> tracked = CompanionWindows(pid);
            Console.WriteLine("step1 companion visible=" + up + " after " +
                t.ToString("F0", CultureInfo.InvariantCulture) + "ms, tracking " + tracked.Count +
                Report(tracked, none));
            if (!up) { Console.WriteLine("RESULT=FAIL no companion window ever appeared"); return 1; }

            // Sweep the PHASE. The app's scan cycle is anchored to when the companion spawned and
            // the probe raises its window a fixed interval later, so a fixed delay samples one point
            // of the cycle over and over. The first A/B did that and produced five samples inside a
            // 30ms band, which a latency uniform over a 300ms cycle cannot do.
            if (phaseDelayMs > 0)
            {
                var ps = Stopwatch.StartNew();
                while (ps.Elapsed.TotalMilliseconds < phaseDelayMs)
                { Application.DoEvents(); Thread.Sleep(5); }
            }

            // ---- case ONE: only the primary monitor is blocked --------------------------------
            var blockedOne = new bool[screens.Length];
            int primary = 0;
            for (int i = 0; i < screens.Length; i++) if (screens[i].Primary) primary = i;
            covers.Add(Cover(screens[primary].Bounds));
            blockedOne[primary] = true;
            Console.WriteLine("step2 monitor " + primary + " covered " + screens[primary].Bounds);

            // With more than one monitor the free one must be USED, not skipped in favour of
            // hiding; with only one monitor there is nowhere to go and hiding is the whole answer.
            bool multiMonitor = screens.Length > 1;
            bool okOne = WaitFor(delegate
            {
                foreach (IntPtr h in tracked)
                {
                    if (multiMonitor) { if (!RelocatedRatherThanVanished(h, blockedOne)) return false; }
                    else if (!StoodDown(h, blockedOne)) return false;
                }
                return true;
            }, 5000, covers, out t);
            bool reallyBlocked = Program.PrimaryMonitorBlocked();
            Console.WriteLine("step3 " + (multiMonitor ? "relocated to a free monitor=" : "off the blocked monitor=")
                + okOne + " after " +
                t.ToString("F0", CultureInfo.InvariantCulture) + "ms desktopBlocked=" +
                reallyBlocked + Report(tracked, blockedOne));
            if (!okOne && !reallyBlocked)
            {
                Console.WriteLine("RESULT=INCONCLUSIVE the probe window was not the top window "
                    + "covering the monitor centre, so nothing was fullscreen to stand down from");
                return 2;
            }
            if (!okOne) failures++;

            // ---- case ALL: every monitor blocked, so hiding is the only option ----------------
            var blockedAll = new bool[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                blockedAll[i] = true;
                if (i != primary) covers.Add(Cover(screens[i].Bounds));
            }
            Console.WriteLine("step4 every monitor covered (" + screens.Length + ")");

            bool okAll = WaitFor(delegate
            {
                foreach (IntPtr h in tracked) if (IsWindowVisible(h)) return false;
                return true;
            }, 6000, covers, out t);
            Console.WriteLine("step5 every companion hidden=" + okAll + " after " +
                t.ToString("F0", CultureInfo.InvariantCulture) + "ms" + Report(tracked, blockedAll));
            if (!okAll) failures++;

            // ---- and back ---------------------------------------------------------------------
            foreach (Form f in covers) { try { f.Close(); f.Dispose(); } catch { } }
            covers.Clear();
            Application.DoEvents();
            bool back = WaitFor(delegate
            {
                foreach (IntPtr h in tracked) if (IsWindowVisible(h)) return true;
                return false;
            }, 6000, covers, out t);
            Console.WriteLine("step6 a companion is back=" + back + " after " +
                t.ToString("F0", CultureInfo.InvariantCulture) + "ms" + Report(tracked, none));
            if (!back) failures++;
        }
        finally
        {
            foreach (Form f in covers) { try { f.Close(); } catch { } }
            try { if (!app.HasExited) app.Kill(true); } catch { }
            try { app.WaitForExit(10000); } catch { }
        }

        Console.WriteLine(failures == 0 ? "RESULT=PASS" : "RESULT=FAIL " + failures + " step(s)");
        return failures == 0 ? 0 : 1;
    }
}
