// Operation counter for FormCompanion.CheckFullScreen's two halves.
//
// A faithful replica of FullscreenScan.BlockedMonitors (same filter chain, same order, same
// early exit) instrumented to COUNT: EnumWindows callback invocations and every user32/dwmapi
// call issued. Counting rather than timing, because this box runs several agent sessions and
// wall clock is noise; a syscall count is the same on a quiet machine and a busy one.
//
// Second half: the per-companion DECISION pass that would run in place of the walk
// (Screen.AllScreens + Screen.FromRectangle + the DeviceName loop) -- measured as calls and
// allocated bytes, which is what it actually costs.
//
// modes:
//   walk        one cold BlockedMonitors replica, print the counters
//   walk-fs     same, but first create a borderless topmost window covering the primary
//               monitor, so the early exit fires the way it does with a game running
//   decide      one cold decision pass, print the allocation delta
//   time-walk   cold-first-call stopwatch around ONE walk, then exit (for interleaving)
//   time-decide cold-first-call stopwatch around ONE decision pass, then exit
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Program
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
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // counters
    private static int _callbacks;
    private static int _cIsWindowVisible;
    private static int _cIsIconic;
    private static int _cDwm;
    private static int _cGetClassName;
    private static int _cGetWindowRect;

    private static void ResetCounters()
    {
        _callbacks = 0; _cIsWindowVisible = 0; _cIsIconic = 0;
        _cDwm = 0; _cGetClassName = 0; _cGetWindowRect = 0;
    }

    private static int PInvokeTotal()
    {
        return _cIsWindowVisible + _cIsIconic + _cDwm + _cGetClassName + _cGetWindowRect;
    }

    private static Point Center(Rectangle r)
    {
        return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private static bool IsFullscreenOnMonitor(Rectangle w, Rectangle m)
    {
        if (w.Width <= 0 || w.Height <= 0 || m.Width <= 0 || m.Height <= 0) return false;
        long wr = (long)w.Left + w.Width, wb = (long)w.Top + w.Height;
        long mr = (long)m.Left + m.Width, mb = (long)m.Top + m.Height;
        return w.Left <= m.Left && w.Top <= m.Top && wr >= mr && wb >= mb;
    }

    private static bool CountedIsCloaked(IntPtr h)
    {
        try
        {
            _cDwm++;
            int cloaked;
            return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    private static bool CountedIsShell(IntPtr h)
    {
        var name = new StringBuilder(64);
        _cGetClassName++;
        if (GetClassName(h, name, name.Capacity) <= 0) return false;
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

    // The replica, for the stand-down probe: is the primary monitor ACTUALLY blocked right now?
    // The probe needs this because a normal window coming to the front over its fake fullscreen
    // window makes the desktop genuinely un-blocked, and the app is then CORRECT not to hide --
    // scoring that as a failure would be scoring the probe's own environment.
    internal static bool PrimaryMonitorBlocked()
    {
        bool[] b = BlockedMonitorsCore(new HashSet<IntPtr>(), new[] { Screen.PrimaryScreen.Bounds });
        return b.Length > 0 && b[0];
    }

    // Byte-for-byte the shape of FullscreenScan.BlockedMonitors, with counters inserted.
    // `extraMonitors` models the realistic worst case: a monitor whose CENTRE nothing covers --
    // a second display showing bare desktop, or every window minimised. `remaining` then never
    // reaches 0, the early exit never fires, and the walk runs the whole filter chain over every
    // top-level window on the desktop.
    private static bool[] BlockedMonitors(ICollection<IntPtr> petHandles, Rectangle[] extraMonitors)
    {
        Screen[] real = Screen.AllScreens;
        var bounds = new List<Rectangle>();
        foreach (Screen s in real) bounds.Add(s.Bounds);
        if (extraMonitors != null) foreach (Rectangle r in extraMonitors) bounds.Add(r);
        return BlockedMonitorsCore(petHandles, bounds.ToArray());
    }

    private static bool[] BlockedMonitorsCore(ICollection<IntPtr> petHandles, Rectangle[] screens)
    {
        var blocked = new bool[screens.Length];
        if (screens.Length == 0) return blocked;

        var decided = new bool[screens.Length];
        int remaining = screens.Length;

        try
        {
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                _callbacks++;
                if (remaining <= 0) return false;
                if (petHandles != null && petHandles.Contains(hWnd)) return true;
                _cIsWindowVisible++;
                if (!IsWindowVisible(hWnd)) return true;
                _cIsIconic++;
                if (IsIconic(hWnd)) return true;
                if (CountedIsCloaked(hWnd) || CountedIsShell(hWnd)) return true;
                _cGetWindowRect++;
                RECT r;
                if (!GetWindowRect(hWnd, out r)) return true;

                Rectangle bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (bounds.Width <= 0 || bounds.Height <= 0) return true;

                for (int i = 0; i < screens.Length; i++)
                {
                    if (decided[i]) continue;
                    Rectangle mon = screens[i];
                    if (!bounds.Contains(Center(mon))) continue;
                    decided[i] = true;
                    remaining--;
                    if (IsFullscreenOnMonitor(bounds, mon)) blocked[i] = true;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { return new bool[screens.Length]; }

        return blocked;
    }

    // How many top-level windows EnumWindows yields in total, with no filtering and no early exit.
    private static int TotalTopLevelWindows()
    {
        int n = 0;
        EnumWindows(delegate (IntPtr h, IntPtr l) { n++; return true; }, IntPtr.Zero);
        return n;
    }

    // The per-companion decision pass that replaces the walk: everything CheckFullScreen does with
    // the blocked[] array once it has one.
    private static int DecisionPass(Rectangle petBounds)
    {
        Screen[] screens = Screen.AllScreens;
        if (screens.Length == 0) return -1;
        int current = 0;
        string device = Screen.FromRectangle(petBounds).DeviceName;
        for (int i = 0; i < screens.Length; i++)
            if (screens[i].DeviceName == device) { current = i; break; }
        return current;
    }

    private static Form MakeFakeFullscreen()
    {
        Rectangle b = Screen.PrimaryScreen.Bounds;
        var f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.Manual;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Bounds = b;
        f.BackColor = Color.Black;
        f.Opacity = 0.02;   // present to the window manager, invisible to the user
        f.Show();
        Application.DoEvents();
        return f;
    }

    private static void PrintWalk(string label, bool[] blocked)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < blocked.Length; i++) sb.Append(blocked[i] ? '1' : '0');
        Console.WriteLine(label + " monitors=" + blocked.Length +
            " blocked=" + sb +
            " callbacks=" + _callbacks.ToString(CultureInfo.InvariantCulture) +
            " pinvokes=" + PInvokeTotal().ToString(CultureInfo.InvariantCulture) +
            " (vis=" + _cIsWindowVisible + " icon=" + _cIsIconic + " dwm=" + _cDwm +
            " cls=" + _cGetClassName + " rect=" + _cGetWindowRect + ")");
    }

    [STAThread]
    private static int Main(string[] argv)
    {
        string mode = argv.Length > 0 ? argv[0] : "walk";
        Rectangle pet = new Rectangle(
            Screen.PrimaryScreen.Bounds.X + 100, Screen.PrimaryScreen.Bounds.Y + 100, 128, 128);

        // A monitor nothing covers the centre of: a second display showing bare desktop.
        Rectangle[] uncovered = new[] { new Rectangle(-40000, -40000, 1920, 1080) };

        if (mode == "walk" || mode == "walk-fs" || mode == "walk-uncovered")
        {
            Rectangle[] extra = mode == "walk-uncovered" ? uncovered : null;
            Form fake = null;
            if (mode == "walk-fs") fake = MakeFakeFullscreen();
            try
            {
                Console.WriteLine("total-top-level-windows=" + TotalTopLevelWindows());
                for (int pass = 1; pass <= 3; pass++)
                {
                    ResetCounters();
                    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
                    bool[] b = BlockedMonitors(new HashSet<IntPtr>(), extra);
                    long alloc1 = GC.GetAllocatedBytesForCurrentThread();
                    PrintWalk(pass == 1 ? "cold-walk-1" : ("walk-" + pass + "     "), b);
                    Console.WriteLine("            allocated_bytes=" + (alloc1 - alloc0));
                }
            }
            finally { if (fake != null) fake.Close(); }
            return 0;
        }

        if (mode == "decide")
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            int c1 = DecisionPass(pet);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Console.WriteLine("cold-decision monitor=" + c1 + " allocated_bytes=" + (after - before));
            // 1000 further passes, to get a per-pass allocation figure free of first-call one-offs
            long b2 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) DecisionPass(pet);
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            Console.WriteLine("steady-decision allocated_bytes_per_pass=" +
                ((a2 - b2) / 1000.0).ToString("F1", CultureInfo.InvariantCulture));
            return 0;
        }

        if (mode == "time-walk" || mode == "time-walk-uncovered")
        {
            Rectangle[] extra = mode == "time-walk-uncovered" ? uncovered : null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BlockedMonitors(new HashSet<IntPtr>(), extra);
            sw.Stop();
            Console.WriteLine("walk_us=" + (sw.Elapsed.TotalMilliseconds * 1000.0)
                .ToString("F1", CultureInfo.InvariantCulture));
            return 0;
        }

        if (mode == "time-decide")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DecisionPass(pet);
            sw.Stop();
            Console.WriteLine("decide_us=" + (sw.Elapsed.TotalMilliseconds * 1000.0)
                .ToString("F1", CultureInfo.InvariantCulture));
            return 0;
        }

        if (mode == "standdown")
        {
            if (argv.Length < 3)
            {
                Console.Error.WriteLine("standdown <exe> <dataRoot> [phaseDelayMs]");
                return 2;
            }
            int phase = argv.Length > 3
                ? int.Parse(argv[3], CultureInfo.InvariantCulture) : 0;
            return StandDown.Run(argv[1], argv[2], phase);
        }

        Console.Error.WriteLine("unknown mode " + mode);
        return 2;
    }
}
