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
    private static extern bool IsWindow(IntPtr hWnd);
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
    // As the host declares it (DesktopWindows.cs): the extended style is a 32-bit value on every platform.
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;

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
    //
    // Filtered by WS_EX_LAYERED (RA-348). The speech bubble (FormSpeech) is a top-level borderless Form of
    // the same class prefix, TopMost and not parked while it shows, so the Fortunes welcome that appears in
    // the same UI-thread burst as the pet was tracked as a companion -- and the stand-down only drops a
    // bubble's TopMost, it never hides it, so a tracked bubble failed the single-monitor step 3 outright
    // and made step 5's "hide" latency the bubble's remaining life rather than the app's hide.
    // WS_EX_TOOLWINDOW does not discriminate (both forms set it); WS_EX_LAYERED does: FormCompanion's
    // CreateParams forces it for UpdateLayeredWindow, and FormSpeech deliberately does not (its shape comes
    // from Form.Region). Report prints each tracked window's class and extended style, so the filter's
    // verdict is readable in the log.
    private static bool LooksLikeCompanion(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        if (r.Left <= -30000 || r.Top <= -30000) return false;
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_LAYERED) == 0) return false;
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

    // EVERY top-level window the app's process owns, visible or not, companion or not: what the replica
    // excludes when it asks whether the primary monitor is blocked (RA-344), the way the app's scan
    // excludes StartUp.SheepHandles(). Recomputed at the call, like SheepHandles is per scan.
    private static HashSet<IntPtr> WindowsOf(uint pid)
    {
        var found = new HashSet<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "no window";
        if (!IsWindow(h)) return "0x" + h.ToString("X") + " (destroyed)";
        return "0x" + h.ToString("X") + " '" + TitleOf(h) + "' " + ClassOf(h);
    }

    // A tracked handle that is no longer a window (RA-349). IsWindowVisible answers false for a destroyed
    // HWND, so StoodDown and the step-5 predicate read a companion form the app closed and recreated -- a
    // reload, a pet-mix change, a Kill; not the walk-off respawn, which reuses the HWND -- as "hidden" and
    // PASSED, while only Report said "gone" and step 6 then blamed the restore path. Checked before each
    // verdict is trusted, beside the HasExited check F395 added for the whole-app death.
    private static IntPtr FirstDestroyed(List<IntPtr> tracked)
    {
        foreach (IntPtr h in tracked) if (!IsWindow(h)) return h;
        return IntPtr.Zero;
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
        if (!IsWindow(h)) return false;                  // destroyed is not stood down (RA-349); the caller reports it
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
            // A destroyed handle used to print as "hid ok": GetWindowRect fails and IsWindowVisible is
            // false, which reads exactly like a companion that stood down. Say "gone" instead, which also
            // covers a form destroyed and recreated without the process exiting (F395).
            if (!IsWindow(h))
            {
                sb.Append(" ['" + TitleOf(h) + "' gone]");
                continue;
            }
            RECT r;
            GetWindowRect(h, out r);
            int m = MonitorOf(h);
            sb.Append(" ['" + TitleOf(h) + "' " + ClassOf(h) + " ex=0x" + GetWindowLong(h, GWL_EXSTYLE).ToString("X") +
                " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
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
            IntPtr decidedBy;
            bool reallyBlocked = Program.PrimaryMonitorBlocked(WindowsOf(pid), out decidedBy);
            Console.WriteLine("step3 " + (multiMonitor ? "relocated to a free monitor=" : "off the blocked monitor=")
                + okOne + " after " +
                t.ToString("F0", CultureInfo.InvariantCulture) + "ms desktopBlocked=" +
                reallyBlocked + Report(tracked, blockedOne));
            // A DEAD app satisfies every "not visible" invariant: IsWindowVisible on a destroyed HWND is
            // false, so on a single monitor a companion that crashed after step 1 passed step 3 and step 5
            // vacuously and failed only at step 6, blaming the restore path (F395). Checked before each
            // verdict is trusted, the way debug-menu-smoke.ps1 already does. A destroyed tracked WINDOW
            // in a live app is the same vacuity one level down (RA-349), and is refused the same way.
            if (app.HasExited)
            {
                Console.WriteLine("RESULT=FAIL app exited during step 3 with code " + app.ExitCode);
                return 1;
            }
            IntPtr destroyed = FirstDestroyed(tracked);
            if (destroyed != IntPtr.Zero)
            {
                Console.WriteLine("RESULT=FAIL a tracked companion window was destroyed during step 3 (" + Describe(destroyed) +
                    "): the app closed and recreated a form, so its dead handle would have read as hidden");
                return 1;
            }
            if (!okOne && !reallyBlocked)
            {
                Console.WriteLine("RESULT=INCONCLUSIVE the probe window was not the top window "
                    + "covering the monitor centre, so nothing was fullscreen to stand down from"
                    + " (the centre was decided by " + Describe(decidedBy) + ")");
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
                // A destroyed handle is not "hidden" (RA-349): it keeps this wait running until the
                // check below names it, instead of passing the step on a dead HWND.
                foreach (IntPtr h in tracked) if (!IsWindow(h) || IsWindowVisible(h)) return false;
                return true;
            }, 6000, covers, out t);
            Console.WriteLine("step5 every companion hidden=" + okAll + " after " +
                t.ToString("F0", CultureInfo.InvariantCulture) + "ms" + Report(tracked, blockedAll));
            if (app.HasExited)
            {
                Console.WriteLine("RESULT=FAIL app exited during step 5 with code " + app.ExitCode);
                return 1;
            }
            destroyed = FirstDestroyed(tracked);
            if (destroyed != IntPtr.Zero)
            {
                Console.WriteLine("RESULT=FAIL a tracked companion window was destroyed during step 5 (" + Describe(destroyed) +
                    "): the app closed and recreated a form, so its dead handle would have read as hidden");
                return 1;
            }
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
            if (app.HasExited)
            {
                Console.WriteLine("RESULT=FAIL app exited during step 6 with code " + app.ExitCode);
                return 1;
            }
            destroyed = FirstDestroyed(tracked);
            if (destroyed != IntPtr.Zero)
            {
                Console.WriteLine("RESULT=FAIL a tracked companion window was destroyed during step 6 (" + Describe(destroyed) +
                    "): the restore path is not what failed, the form the probe was watching is gone");
                return 1;
            }
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
