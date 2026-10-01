using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
// The enumeration, the state reads, the rect and the two filters (IsCloaked, IsShell) are DesktopWindows'
// (N-deadcode-07): this file carried a verbatim copy of all six imports and both filters, kept equal by hand.
// `using static` keeps the bare names in BlockedMonitors, which the N-host-04 invariant pins by text.
using static DesktopAICompanion.DesktopWindows;

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
            if (screens.Length == 0) return new bool[0];

            var monitors = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) monitors[i] = screens[i].Bounds;
            var decider = new MonitorDecider(monitors, petHandles);

            try
            {
                EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    if (!decider.Undecided) return false;       // every monitor decided; stop early
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                    if (IsCloaked(hWnd) || IsShell(hWnd)) return true;
                    if (!GetWindowRect(hWnd, out RECT r)) return true;
                    return decider.Offer(hWnd, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                }, IntPtr.Zero);
            }
            catch
            {
                // A hostile or racing window handle must never crash the pet: treat the desktop as clear.
                return new bool[screens.Length];
            }

            return decider.Blocked;
        }

        /// <summary>
        /// The decision half of <see cref="BlockedMonitors"/>, fed one window at a time in z-order (top first)
        /// after the enumeration's own filters (visible, not iconic, not cloaked, not the shell). The FIRST
        /// window offered that contains a monitor's centre decides that monitor -- blocked when its rect covers
        /// the whole monitor, clear otherwise -- and no later window can re-decide it; a window in
        /// <paramref name="petHandles"/> never decides. Separate from the enumeration so the rule can be pinned
        /// against described windows in <see cref="SelfTest"/> (N-host-04): a borderless form sized to the
        /// virtual screen at the bottom of the z-order (MatrixDesktop) blocks every monitor it covers while
        /// nothing above it holds a centre, and a normal window above it at a centre releases that monitor.
        /// That is the same rule the shipped 1.2.6 applies; the two builds differ in nothing here.
        /// </summary>
        internal sealed class MonitorDecider
        {
            private readonly Rectangle[] monitors;
            private readonly ICollection<IntPtr> petHandles;
            private readonly bool[] decided;
            private int remaining;

            /// <summary>One flag per monitor, true once a fullscreen window decided it.</summary>
            public readonly bool[] Blocked;

            public MonitorDecider(Rectangle[] monitors, ICollection<IntPtr> petHandles)
            {
                this.monitors = monitors ?? new Rectangle[0];
                this.petHandles = petHandles;
                decided = new bool[this.monitors.Length];
                Blocked = new bool[this.monitors.Length];
                remaining = this.monitors.Length;
            }

            /// <summary>True while at least one monitor has not been decided yet.</summary>
            public bool Undecided { get { return remaining > 0; } }

            /// <summary>Offer the next window in z-order. Returns false once every monitor is decided, so an
            /// enumeration can stop there.</summary>
            public bool Offer(IntPtr hWnd, Rectangle bounds)
            {
                if (remaining <= 0) return false;
                if (petHandles != null && petHandles.Contains(hWnd)) return true;
                if (bounds.Width <= 0 || bounds.Height <= 0) return true;

                for (int i = 0; i < monitors.Length; i++)
                {
                    if (decided[i]) continue;
                    if (!bounds.Contains(DesktopGeometry.Center(monitors[i]))) continue;
                    decided[i] = true;
                    remaining--;
                    if (DesktopGeometry.IsFullscreenOnMonitor(bounds, monitors[i]))
                        Blocked[i] = true;
                }
                return remaining > 0;
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

                // The z-order rule, pinned against described windows (N-host-04). Two monitors, the left one
                // at negative x, and a window shaped exactly like MatrixDesktop's "Matrix Digital Rain": a
                // borderless form sized to the virtual screen, offered LAST (the bottom of the z-order). The
                // enumeration's own filters (visibility, cloaking, the shell) run before Offer and are not what
                // is under test here; the decision is.
                var left = new Rectangle(-3440, 0, 3440, 1440);
                var primary = new Rectangle(0, 0, 2560, 1440);
                Rectangle[] twoMonitors = { left, primary };
                Rectangle rainWindow = Rectangle.Union(left, primary);          // (-3440,0) 6000x1440
                var maximizedRect = new Rectangle(0, 0, 2560, 1400);           // maximized: the taskbar strip stays free
                IntPtr rain = (IntPtr)0x1000, maximized = (IntPtr)0x2000, small = (IntPtr)0x3000,
                       companion = (IntPtr)0x4000, game = (IntPtr)0x5000;

                // 1. Alone above the desktop: every monitor it covers is blocked (the stand-down of 2026-09-29).
                var alone = new MonitorDecider(twoMonitors, null);
                alone.Offer(rain, rainWindow);
                ok = Expect(sb, "rain-alone-left", true, alone.Blocked[0]) && ok;
                ok = Expect(sb, "rain-alone-primary", true, alone.Blocked[1]) && ok;

                // 2. WITNESS: a normal MAXIMIZED window above it releases only its own monitor. Maximized is not
                //    fullscreen (the taskbar strip is free), so that monitor is clear and the other stays blocked.
                var underMaximized = new MonitorDecider(twoMonitors, null);
                underMaximized.Offer(maximized, maximizedRect);
                underMaximized.Offer(rain, rainWindow);
                ok = Expect(sb, "maximized-above-rain-primary", false, underMaximized.Blocked[1]) && ok;
                ok = Expect(sb, "maximized-above-rain-left", true, underMaximized.Blocked[0]) && ok;

                // 3. A small normal window holding a monitor's centre above it releases that monitor (the
                //    desktop of 2026-09-30 05:27: dialogs and a helper window at the centres).
                var underSmall = new MonitorDecider(twoMonitors, null);
                underSmall.Offer(small, new Rectangle(-2000, 500, 400, 300));
                underSmall.Offer(rain, rainWindow);
                ok = Expect(sb, "small-above-rain-left", false, underSmall.Blocked[0]) && ok;
                ok = Expect(sb, "small-above-rain-primary", true, underSmall.Blocked[1]) && ok;

                // 4. A companion's own window at a centre never decides (F278): the rain window still blocks.
                var underCompanion = new MonitorDecider(twoMonitors, new HashSet<IntPtr> { companion });
                underCompanion.Offer(companion, new Rectangle(-1740, 700, 40, 40));
                bool stillUndecided = underCompanion.Offer(rain, rainWindow);
                ok = Expect(sb, "companion-above-rain-left", true, underCompanion.Blocked[0]) && ok;
                ok = Expect(sb, "companion-above-rain-primary", true, underCompanion.Blocked[1]) && ok;
                ok = Expect(sb, "offer-stops-once-decided", false, stillUndecided) && ok;

                // 5. A fullscreen game over a maximized window: the topmost real window decides, blocked.
                var gameOnTop = new MonitorDecider(twoMonitors, null);
                gameOnTop.Offer(game, primary);
                gameOnTop.Offer(maximized, maximizedRect);
                ok = Expect(sb, "game-above-maximized-primary", true, gameOnTop.Blocked[1]) && ok;
                ok = Expect(sb, "game-above-maximized-left-undecided", true, gameOnTop.Undecided) && ok;
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
            // "PASS: " / "FAIL: " at column 0, the verdict shape every other marker uses and the mutation
            // harness reads (a failure is a line that STARTS with FAIL).
            bool pass = expected == actual;
            sb.AppendLine((pass ? "PASS: " : "FAIL: ") + label + " expected=" + expected + " actual=" + actual);
            return pass;
        }

        private static bool Expect(StringBuilder sb, string label, bool expected, bool actual)
        {
            return Expect(sb, label, expected ? 1 : 0, actual ? 1 : 0);
        }
    }
}
