using System;
using System.Collections.Generic;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// The window family of embedded classes (src/dotNet/Shimeji/TickWindowFamily.cs, grimoire/06 sections 5 and 8) and
    /// the real desktop's window rules, pure. Companions run on MotionDesk, whose one window the engine moves and
    /// minimises through the environment seam; no test here enumerates, moves or minimises a real window.
    /// </summary>
    internal static partial class Program
    {
        private static string WindowMoves(TickLog log)
        {
            return string.Join("|", log.Events.FindAll(e => e.Contains(" window-")));
        }

        private static string RectOf(TickRect r) { return TickLog.Ltrb(r); }

        /// <summary>WalkWithIE from (640, 680) facing left to TargetX 600, IeOffsetX 10, IeOffsetY -64, two poses of
        /// 2 px a tick.</summary>
        private static TickType WalkCarryType(string targetX)
        {
            return MotionType("Carry", MAct(30, "Carry", "Embedded", "WalkWithIE", "Floor",
                new[] { TParam("TargetX", targetX), TParam("IeOffsetX", "10"), TParam("IeOffsetY", "-64") },
                MVar(null, false, TPose(0, 1, -2), TPose(1, 1, -2))));
        }

        // ---- WalkWithIE

        private static void TestTickWindowsWalkCarries()
        {
            // WalkWithIE.java:45-92. Facing left, the grip is the window's lower RIGHT corner: anchor.x + IeOffsetX is its
            // right and anchor.y + IeOffsetY its bottom. The window (350, 416) 300x200 has right 650 = 640 + 10 and bottom
            // 616 = 680 - 64. Each step the Move goes 2 px left, then the window is put back at the grip:
            // (anchor.x + 10 - 300, 680 - 64 - 200): (348, 416), (346, 416), (344, 416).
            var desk = new MotionDesk { Window = new[] { 350, 416, 300, 200 } };
            var log = new TickLog();
            TickType t = WalkCarryType("600");
            TickWorld w = MotionWorld(desk, log, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Carry");
            for (int i = 0; i < 3; i++) w.StepOnce();
            AssertEqual(new TickPoint(634, 680), m.Anchor, "WalkWithIE did not walk as its Move does.");
            AssertEqual("move (348, 416)|move (346, 416)|move (344, 416)", string.Join("|", desk.WindowCalls),
                "WalkWithIE did not keep the window's lower right corner at anchor + (IeOffsetX, IeOffsetY) facing left.");
            AssertEqual("m1 window-move [350,416,650,616]>[348,416,648,616]", WindowMoves(log).Split('|')[0],
                "The first move was not reported with the window's rectangle before and after.");
            AssertEqual("[344,416,644,616]", RectOf(desk.Desktop.Rect), "The desktop's window is not where the last move put it.");
            AssertEqual("Carry", m.Behaviour.Name, "WalkWithIE let go of a window it held exactly.");

            // Facing right the grip is the lower LEFT corner: anchor.x - IeOffsetX is its left. From (300, 680) to
            // TargetX 700 the pose's -2 mirrors to +2: window (300 + 2 - 10, 416) = (292, 416), then (294, 416).
            var desk2 = new MotionDesk { Window = new[] { 290, 416, 300, 200 } };
            TickType u = WalkCarryType("700");
            TickWorld v = MotionWorld(desk2, null, null, null, u);
            TickMascot n = v.CreateAtStart("m1", u, new TickPoint(300, 680), true, "Carry");
            v.StepOnce();
            v.StepOnce();
            AssertEqual(new TickPoint(304, 680), n.Anchor, "WalkWithIE facing right did not walk right.");
            AssertEqual("move (292, 416)|move (294, 416)", string.Join("|", desk2.WindowCalls),
                "WalkWithIE did not keep the window's lower left corner at anchor - IeOffsetX facing right.");
        }

        private static void TestTickWindowsWalkLosesGround()
        {
            // The grip is exact (WalkWithIE.java:58-75): a window 1 px right of it, or 1 px low, loses the ground at the
            // first step, before anything moves; the companion enters Fall where it stood.
            TickType t = WalkCarryType("600");
            foreach (int[] off in new[] { new[] { 351, 416, 300, 200 }, new[] { 350, 417, 300, 200 } })
            {
                var desk = new MotionDesk { Window = off };
                var log = new TickLog();
                TickWorld w = MotionWorld(desk, log, null, null, t);
                TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Carry");
                w.StepOnce();
                AssertTrue(log.Events.Contains("m1 behaviour Carry>Fall forced"),
                    "A grip 1 px off (" + RectOf(desk.Desktop.Rect) + ") did not lose the ground: " + string.Join("; ", log.Events));
                AssertEqual(0, desk.WindowCalls.Count, "A window that was not held exactly was moved.");
                AssertEqual(new TickPoint(640, 680), m.Anchor, "Losing the grip moved the companion.");
            }

            // The window goes away (closed, hidden) after the first step: the second step sees no visible window, its
            // area empty at (0, 0) as the source's refresh leaves it, and loses the ground (WalkWithIE.java:49-53).
            TickType still = MotionType("Carry", MAct(30, "Carry", "Embedded", "WalkWithIE", "Floor",
                new[] { TParam("TargetX", "600"), TParam("IeOffsetX", "10"), TParam("IeOffsetY", "-64") },
                MVar(null, false, TPose(0, 1, 0))));
            var gone = new MotionDesk { Window = new[] { 350, 416, 300, 200 } };
            var log2 = new TickLog();
            TickWorld v = MotionWorld(gone, log2, null, null, still);
            v.CreateAtStart("m1", still, new TickPoint(640, 680), false, "Carry");
            v.StepOnce();
            AssertEqual("move (350, 416)", string.Join("|", gone.WindowCalls), "A still WalkWithIE did not put the window back at its grip.");
            gone.Window = null;
            v.StepOnce();
            AssertTrue(log2.Events.Contains("m1 behaviour Carry>Fall forced"), "Losing the window did not lose the ground: " + string.Join("; ", log2.Events));
            AssertEqual(1, gone.WindowCalls.Count, "A window that had gone was moved.");

            // Only the visibility test can lose this one: no window at all, so the area is invisible at (0, 0, 0, 0), and
            // the offsets put the grip exactly on that empty area's corner (facing left: 640 - 640 = its right, 680 - 680
            // = its bottom). Multiscreen on, so the companion sees the area as it is rather than the visible empty Area
            // single-screen mode hands over (MascotEnvironment.java:82-83).
            TickType corner = MotionType("Carry", MAct(30, "Carry", "Embedded", "WalkWithIE", "Floor",
                new[] { TParam("TargetX", "600"), TParam("IeOffsetX", "-640"), TParam("IeOffsetY", "-680") },
                MVar(null, false, TPose(0, 1, 0))));
            var none = new MotionDesk();
            var log3 = new TickLog();
            TickWorld u = MotionWorld(none, log3, null, new TickSwitches { Multiscreen = true }, corner);
            u.CreateAtStart("m1", corner, new TickPoint(640, 680), false, "Carry");
            u.StepOnce();
            AssertTrue(log3.Events.Contains("m1 behaviour Carry>Fall forced"),
                "An invisible area whose empty rectangle matched the grip did not lose the ground: " + string.Join("; ", log3.Events));
        }

        // ---- the switch

        private static void TestTickWindowsSwitchOff()
        {
            // policy:window-off-plays. The type's WindowActions off: WalkWithIE walks as a Move (frames 0, 1, 0, 2 px a
            // step) with or without a window at its grip, and nothing is moved or reported.
            var off = new TickSwitches();
            off.Off["Alpha:WindowActions"] = false;
            TickType t = WalkCarryType("600");
            foreach (int[] window in new[] { new[] { 350, 416, 300, 200 }, null })
            {
                var desk = new MotionDesk { Window = window };
                var log = new TickLog();
                TickWorld w = MotionWorld(desk, log, null, off, t);
                TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Carry");
                var frames = new List<int>();
                for (int i = 0; i < 3; i++) { w.StepOnce(); frames.Add(m.Pose.Frame); }
                AssertEqual("0,1,0", string.Join(",", frames), "With window actions off WalkWithIE's animation did not play.");
                AssertTrue(m.Anchor.Equals(new TickPoint(634, 680)) && m.Behaviour.Name == "Carry",
                    "With window actions off WalkWithIE did not walk on as a Move (" + m.Anchor + ", " + m.Behaviour.Name + ").");
                AssertEqual(0, desk.WindowCalls.Count, "With window actions off a window was moved.");
                AssertEqual("", WindowMoves(log), "With window actions off a window event was reported.");
            }

            // ThrowIE and MinimizeIE off: each plays its whole animation (3 ticks), moving and minimising nothing.
            foreach (string cls in new[] { "ThrowIE", "MinimizeIE" })
            {
                TickType u = MotionType("Act", MAct(30, "Act", "Embedded", cls, "Floor", null, MVar(null, false, TPose(2, 3))));
                var desk = new MotionDesk { Window = new[] { 100, 100, 300, 200 } };
                TickWorld v = MotionWorld(desk, null, null, off, u);
                TickMascot n = v.CreateAtStart("m1", u, new TickPoint(640, 680), true, "Act");
                for (int i = 0; i < 3; i++) v.StepOnce();
                AssertEqual("Act", n.Behaviour.Name, cls + " with window actions off did not play its animation to the end.");
                AssertEqual(0, desk.WindowCalls.Count, cls + " with window actions off acted on the window.");
                v.StepOnce();
                AssertEqual("Stand", n.Behaviour.Name, cls + " with window actions off did not end with its animation.");
            }

            // The source's own Throwing setting off ends the action at once (WalkWithIE.java:39), whatever WindowActions says.
            var noThrowing = new TickSwitches();
            noThrowing.Off["Alpha:Throwing"] = false;
            var desk3 = new MotionDesk { Window = new[] { 350, 416, 300, 200 } };
            TickWorld x = MotionWorld(desk3, null, null, noThrowing, t);
            TickMascot o = x.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Carry");
            AssertEqual("Stand", o.Behaviour.Name, "WalkWithIE ran with the Throwing setting off.");
        }

        // ---- FallWithIE and JumpWithIE

        private static void TestTickWindowsFallCarries()
        {
            // FallWithIE.java:45-92 from (640, 300) facing left, IeOffsetY -64, no resistance, Gravity 2: vy 2, 4, 6, so y
            // 302, 306, 312 (Fall.java). The window's lower right corner (right 640, bottom 236) follows to
            // (640 - 300, y - 64 - 200): (340, 38), (340, 42), (340, 48).
            TickType t = MotionType("Drop", MAct(30, "Drop", "Embedded", "FallWithIE", "",
                new[] { TParam("IeOffsetY", "-64"), TParam("RegistanceY", "0"), TParam("Gravity", "2") },
                MVar(null, false, TPose(0, 1))));
            var desk = new MotionDesk { Window = new[] { 340, 36, 300, 200 } };
            var log = new TickLog();
            TickWorld w = MotionWorld(desk, log, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 300), false, "Drop");
            for (int i = 0; i < 3; i++) w.StepOnce();
            AssertEqual(new TickPoint(640, 312), m.Anchor, "FallWithIE did not fall as its Fall does.");
            AssertEqual("move (340, 38)|move (340, 42)|move (340, 48)", string.Join("|", desk.WindowCalls),
                "FallWithIE did not carry the window down with the companion.");
            AssertEqual("Drop", m.Behaviour.Name, "FallWithIE let go of a window it held exactly.");
        }

        private static void TestTickWindowsJumpCarries()
        {
            // JumpWithIE.java:45-92 from (100, 600) facing right to (160, 600), Arc off, IeOffsetY -64: 20 px a step to
            // 120 and 140, then within reach it lands on 160 (Jump.java). The window's lower left corner (left 100,
            // bottom 536) follows to (x, 600 - 64 - 200): (120, 336), (140, 336), (160, 336).
            TickType t = MotionType("Leap", MAct(30, "Leap", "Embedded", "JumpWithIE", "",
                new[] { TParam("TargetX", "160"), TParam("TargetY", "600"), TParam("Arc", "false"), TParam("IeOffsetY", "-64") },
                MVar(null, false, TPose(0, 1))));
            var desk = new MotionDesk { Window = new[] { 100, 336, 200, 200 } };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(100, 600), true, "Leap");
            var xs = new List<int>();
            for (int i = 0; i < 3; i++) { w.StepOnce(); xs.Add(m.Anchor.X); }
            AssertEqual("120,140,160", string.Join(",", xs), "JumpWithIE did not jump as its Jump does.");
            AssertEqual("move (120, 336)|move (140, 336)|move (160, 336)", string.Join("|", desk.WindowCalls),
                "JumpWithIE did not carry the window with the companion.");
        }

        // ---- ThrowIE

        private static void TestTickWindowsThrow()
        {
            // ThrowIE.java:50-70, the defaults InitialVX 32, InitialVY -10, Gravity .5, a five-tick animation, the window
            // at (400, 100). Each step, from where the window is: x +32 facing right, y -10 + (int)(age * .5):
            //   age 0: (432, 90); 1: (464, 80); 2: (496, 71); 3: (528, 62); 4: (560, 54); then the animation ends.
            TickType t = MotionType("Toss", MAct(30, "Toss", "Embedded", "ThrowIE", "Floor", null, MVar(null, false, TPose(2, 5))));
            var desk = new MotionDesk { Window = new[] { 400, 100, 300, 200 } };
            var log = new TickLog();
            TickWorld w = MotionWorld(desk, log, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Toss");
            for (int i = 0; i < 5; i++) w.StepOnce();
            AssertEqual("move (432, 90)|move (464, 80)|move (496, 71)|move (528, 62)|move (560, 54)", string.Join("|", desk.WindowCalls),
                "ThrowIE facing right did not move the window by +InitialVX and InitialVY + (int)(age * Gravity).");
            AssertEqual("m1 window-move [400,100,700,300]>[432,90,732,290]", WindowMoves(log).Split('|')[0],
                "ThrowIE's first move was not reported with the window's rectangle before and after.");
            w.StepOnce();
            AssertEqual("Stand", m.Behaviour.Name, "ThrowIE did not end with its animation.");

            // Facing left with InitialVX 10, InitialVY 4, Gravity 1.5: x -10, y +4 + (int)(age * 1.5):
            //   age 0: (390, 104); 1: (380, 109); 2: (370, 116).
            TickType u = MotionType("Toss", MAct(30, "Toss", "Embedded", "ThrowIE", "Floor",
                new[] { TParam("InitialVX", "10"), TParam("InitialVY", "4"), TParam("Gravity", "1.5") }, MVar(null, false, TPose(2, 5))));
            var desk2 = new MotionDesk { Window = new[] { 400, 100, 300, 200 } };
            TickWorld v = MotionWorld(desk2, null, null, null, u);
            v.CreateAtStart("m1", u, new TickPoint(640, 680), false, "Toss");
            for (int i = 0; i < 3; i++) v.StepOnce();
            AssertEqual("move (390, 104)|move (380, 109)|move (370, 116)", string.Join("|", desk2.WindowCalls),
                "ThrowIE facing left did not move the window by -InitialVX, or did not read its parameters.");

            // No window, in single-screen mode: the companion's view swaps a window that misses its work area for the
            // source's VISIBLE empty area (MascotEnvironment.java:82-83), so ThrowIE starts, aims at (0 + 32, 0 - 10) and
            // the desktop finds no window to move (moveIE(null), WindowsEnvironment.java:231-234). Kept as the source has it.
            var empty = new MotionDesk();
            var log3 = new TickLog();
            TickWorld x = MotionWorld(empty, log3, null, null, t);
            TickMascot o = x.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Toss");
            x.StepOnce();
            AssertEqual("Toss", o.Behaviour.Name, "ThrowIE did not start on the source's visible empty area in single-screen mode.");
            AssertEqual("move (32, -10) (none)", string.Join("|", empty.WindowCalls), "ThrowIE did not aim at the empty area's corner.");
            AssertEqual("", WindowMoves(log3), "A move that found no window was reported.");
        }

        // ---- MinimizeIE

        private static void TestTickWindowsMinimize()
        {
            // MinimizeIE.java:35-44, a three-tick animation, the companion on the work area's floor far from the window:
            // nothing at ages 0 and 1; at age 2 (duration - 1) the window is minimised. At the next step the window is
            // gone, so the action ends.
            TickType t = MotionType("Hide", MAct(30, "Hide", "Embedded", "MinimizeIE", "Floor", null, MVar(null, false, TPose(3, 3))));
            var desk = new MotionDesk { Window = new[] { 100, 100, 300, 200 } };
            var log = new TickLog();
            TickWorld w = MotionWorld(desk, log, null, null, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), false, "Hide");
            w.StepOnce();
            w.StepOnce();
            AssertEqual(0, desk.WindowCalls.Count, "MinimizeIE minimised before the last tick of its animation.");
            w.StepOnce();
            AssertEqual("minimize", string.Join("|", desk.WindowCalls), "MinimizeIE did not minimise on the last tick of its animation.");
            AssertTrue(desk.Desktop.Minimised && log.Events.Contains("m1 window-minimize"), "The minimise was not done or not reported.");
            w.StepOnce();
            AssertEqual("Stand", m.Behaviour.Name, "MinimizeIE went on after its window was minimised.");

            // A one-tick animation minimises at its only tick.
            TickType u = MotionType("Hide", MAct(30, "Hide", "Embedded", "MinimizeIE", "Floor", null, MVar(null, false, TPose(3, 1))));
            var desk2 = new MotionDesk { Window = new[] { 100, 100, 300, 200 } };
            TickWorld v = MotionWorld(desk2, null, null, null, u);
            v.CreateAtStart("m1", u, new TickPoint(640, 680), false, "Hide");
            v.StepOnce();
            AssertEqual("minimize", string.Join("|", desk2.WindowCalls), "A one-tick MinimizeIE did not minimise at its only tick.");
        }

        // ---- the desktop's window

        private static void TestTickWindowsDesktopModel()
        {
            // WindowsEnvironment.java: the walk finds a window only while it is shown, not minimised and on the screen;
            // a move changes the desktop at once and the area at the next tick; a window not found leaves the area empty
            // at (0, 0).
            var screen = new TickArea();
            screen.Set(0, 0, 1280, 720);
            var area = new TickArea();
            var d = new TickDesktopWindow();
            d.Show(new TickRect(100, 100, 300, 200));
            d.ApplyTo(area, screen);
            AssertTrue(area.Visible && area.Left == 100, "A shown window on the screen was not found.");
            TickRect from, to;
            AssertTrue(d.Move(new TickPoint(150, 120), 300, 200, screen, out from, out to), "A found window was not moved.");
            AssertEqual(100, area.Left, "A move changed the area before the next tick.");
            d.ApplyTo(area, screen);
            AssertEqual(150, area.Left, "The next tick did not show the moved window.");
            AssertTrue(d.Minimize(screen), "A found window was not minimised.");
            d.ApplyTo(area, screen);
            AssertFalse(area.Visible, "A minimised window was still found.");
            AssertTrue(area.Left == 0 && area.Top == 0 && area.Width == 0 && area.Height == 0,
                "A window not found left the area at (" + area.Left + ", " + area.Top + ", " + area.Width + ", " + area.Height
                + "), not at (0, 0, 0, 0) as the source's getIERect(null) leaves it.");
            AssertFalse(d.Move(new TickPoint(0, 0), 300, 200, screen, out from, out to), "A minimised window was moved.");
            d.Show(new TickRect(100, 100, 300, 200));
            d.ApplyTo(area, screen);
            AssertTrue(area.Visible, "Showing the window again (the next timeline entry) did not replace its minimised state.");
            d.Show(new TickRect(1280, 100, 300, 200));
            d.ApplyTo(area, screen);
            AssertFalse(area.Visible, "A window wholly off the screen was found.");

            // Through ThrowIE: from x 1240 the window is thrown to 1272 (still touching the screen), then to 1304, off
            // it; at the next step it is not found, so the area is invisible and empty at (0, 0).
            TickType t = MotionType("Toss", MAct(30, "Toss", "Embedded", "ThrowIE", "Floor", null, MVar(null, false, TPose(2, 10))));
            // Multiscreen on: the companion sees that area, so ThrowIE ends after two moves.
            var desk = new MotionDesk { Window = new[] { 1240, 300, 300, 200 } };
            TickWorld w = MotionWorld(desk, null, null, new TickSwitches { Multiscreen = true }, t);
            TickMascot m = w.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Toss");
            for (int i = 0; i < 3; i++) w.StepOnce();
            AssertEqual("move (1272, 290)|move (1304, 280)", string.Join("|", desk.WindowCalls), "With Multiscreen on, ThrowIE did not throw the window off the screen and stop.");
            AssertEqual("Stand", m.Behaviour.Name, "With Multiscreen on, ThrowIE went on with its window off the screen.");
            // Multiscreen off: a window area that does not touch the companion's work area, the empty one included, is
            // handed over as the source's new Area(), which is VISIBLE at (0, 0) (MascotEnvironment.java:82-83). So the
            // source's ThrowIE goes on, and its next throw finds no window (moveActiveIE with findActiveIE() null). The
            // old stale-rectangle refresh hid this: the area still held the last window found, which touched the work area.
            var single = new MotionDesk { Window = new[] { 1240, 300, 300, 200 } };
            TickWorld ws = MotionWorld(single, null, null, null, t);
            TickMascot ms = ws.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Toss");
            for (int i = 0; i < 3; i++) ws.StepOnce();
            AssertEqual("move (1272, 290)|move (1304, 280)|move (32, -9) (none)", string.Join("|", single.WindowCalls),
                "Single-screen, ThrowIE did not go on throwing at the empty area as the source does.");
            AssertEqual("Toss", ms.Behaviour.Name, "Single-screen, ThrowIE stopped once its window left the screen.");
        }

        // ---- the real desktop's rules, pure

        private static void TestTickWindowsTitleRules()
        {
            // WindowsEnvironment.isIE (:43-107), case by case.
            string[] none = TickWindowRules.ParseList("");
            string[] shipped = TickWindowRules.ParseList(TickWindowRules.DefaultWhitelist);
            AssertFalse(TickWindowRules.IsTitleInteractive("", shipped, none), "An empty title was interactive.");
            AssertFalse(TickWindowRules.IsTitleInteractive("Program Manager", none, TickWindowRules.ParseList("x")), "Program Manager was interactive.");
            AssertTrue(TickWindowRules.IsTitleInteractive("Program Manager 2", none, TickWindowRules.ParseList("x")),
                "Only the exact title Program Manager is excluded, but a longer one was too.");
            AssertTrue(TickWindowRules.IsTitleInteractive("notes.txt - Notepad", shipped, none), "A whitelisted title was not interactive.");
            AssertFalse(TickWindowRules.IsTitleInteractive("Calculator", shipped, none), "A title the whitelist does not list was interactive.");
            AssertFalse(TickWindowRules.IsTitleInteractive("notes.txt - notepad", shipped, none), "The whitelist matched without regard to case.");
            AssertFalse(TickWindowRules.IsTitleInteractive("secret - Notepad", shipped, TickWindowRules.ParseList("secret")),
                "A whitelisted title the blacklist also matches was interactive; the blacklist wins.");
            AssertTrue(TickWindowRules.IsTitleInteractive("Calculator", none, TickWindowRules.ParseList("secret/Mail")),
                "With only a blacklist, a title it does not match was not interactive.");
            AssertFalse(TickWindowRules.IsTitleInteractive("Mail - Inbox", none, TickWindowRules.ParseList("secret/Mail")),
                "With only a blacklist, a title it matches was interactive.");
            AssertFalse(TickWindowRules.IsTitleInteractive("Anything", none, none), "With neither list a window was interactive.");
            AssertFalse(TickWindowRules.IsTitleInteractive("Anything", TickWindowRules.ParseList(" / "), TickWindowRules.ParseList("  ")),
                "Blank list entries were counted as lists in use.");
            AssertTrue(TickWindowRules.IsTitleInteractive("Note Pad", TickWindowRules.ParseList(" Pad"), none), "An entry was not matched as written.");
            AssertFalse(TickWindowRules.IsTitleInteractive("NotePad", TickWindowRules.ParseList(" Pad"), none), "An entry was trimmed before matching.");
        }

        private static TickWindowFacts Shown(string title)
        {
            return new TickWindowFacts { Visible = true, Title = title, Bounds = new TickRect(100, 100, 300, 200) };
        }

        private static void TestTickWindowsWalkOrder()
        {
            // findActiveIE and isViableIE (:109-165): the first eligible window in EnumWindows order; hidden, cloaked,
            // unlisted, minimised and off-screen ones are passed over; a maximised one ends the walk with none, and is
            // checked before its title is.
            var screen = new TickRect(0, 0, 1280, 720);
            string[] wl = TickWindowRules.ParseList(TickWindowRules.DefaultWhitelist), bl = TickWindowRules.ParseList("");
            TickWindowFacts hidden = Shown("Notepad"); hidden.Visible = false;
            TickWindowFacts cloaked = Shown("Notepad"); cloaked.Cloaked = true;
            TickWindowFacts minimised = Shown("Notepad"); minimised.Minimised = true;
            TickWindowFacts offScreen = Shown("Notepad"); offScreen.Bounds = new TickRect(1280, 100, 300, 200);
            TickWindowFacts own = Shown("Notepad"); own.OwnProcess = true;
            var walk = new List<TickWindowFacts> { hidden, cloaked, Shown("Calculator"), minimised, offScreen, own, Shown("A - Notepad"), Shown("B - Notepad") };
            AssertEqual(6, TickWindowRules.FindActive(walk, screen, wl, bl), "The walk did not take the first eligible window in z-order.");
            AssertEqual(TickWindowVerdict.OutOfBounds, TickWindowRules.Classify(offScreen, screen, wl, bl), "A window off the screen was not out of bounds.");

            TickWindowFacts maximised = Shown("Calculator"); maximised.Maximised = true;
            var stopped = new List<TickWindowFacts> { Shown("Calculator"), maximised, Shown("A - Notepad") };
            AssertEqual(-1, TickWindowRules.FindActive(stopped, screen, wl, bl),
                "A maximised window (even one whose title is not listed) did not end the walk with no window.");
            TickWindowFacts maximisedHidden = maximised; maximisedHidden.Cloaked = true;
            var passed = new List<TickWindowFacts> { maximisedHidden, Shown("A - Notepad") };
            AssertEqual(1, TickWindowRules.FindActive(passed, screen, wl, bl), "A cloaked maximised window ended the walk.");
        }

        private static void TestTickWindowsSafety()
        {
            // grimoire/06 section 8: each fact alone refuses the window; a plain one is accepted.
            AssertTrue(TickWindowRules.Refusal(Shown("Notepad")) == null, "A plain window was refused.");
            var refused = new List<string>();
            for (int i = 0; i < 13; i++)
            {
                TickWindowFacts f = Shown("Notepad");
                switch (i)
                {
                    case 0: f.OwnProcess = true; break;
                    case 1: f.Shell = true; break;
                    case 2: f.Visible = false; break;
                    case 3: f.Cloaked = true; break;
                    case 4: f.Minimised = true; break;
                    case 5: f.Maximised = true; break;
                    case 6: f.Fullscreen = true; break;
                    case 7: f.MonitorBlocked = true; break;
                    case 8: f.ToolWindow = true; break;
                    case 9: f.Elevated = true; break;
                    case 10: f.Hung = true; break;
                    case 11: f.MovingOrSizing = true; break;
                    default: break;
                }
                string why = TickWindowRules.Refusal(f);
                if (i < 12 && why == null) throw new Exception("Safety fact #" + i + " did not refuse the window.");
                if (why != null) refused.Add(why);
            }
            AssertEqual(12, refused.Count, "Not every safety fact refused, or a plain window was refused.");
        }

        private static void TestTickWindowsClamp()
        {
            // The host's clamp (TickWindowRules.ClampInto): a 300x200 window stays wholly inside the work area (0, 0)
            // 1280x680, so x in 0..980 and y in 0..480; a window wider than the work area is pinned to its left.
            var work = new TickRect(0, 0, 1280, 680);
            AssertEqual(new TickPoint(980, 90), TickWindowRules.ClampInto(new TickPoint(992, 90), 300, 200, work), "The right edge did not hold the window.");
            AssertEqual(new TickPoint(0, 480), TickWindowRules.ClampInto(new TickPoint(-5, 900), 300, 200, work), "The left or bottom edge did not hold the window.");
            AssertEqual(new TickPoint(500, 0), TickWindowRules.ClampInto(new TickPoint(500, -10), 300, 200, work), "The top edge did not hold the window.");
            AssertEqual(new TickPoint(500, 300), TickWindowRules.ClampInto(new TickPoint(500, 300), 300, 200, work), "A window already inside was moved.");
            AssertEqual(new TickPoint(0, 0), TickWindowRules.ClampInto(new TickPoint(40, 0), 1400, 200, work), "A window wider than the work area was not pinned to its left.");

            // ThrowIE from (960, 5) facing right on a desk that clamps as the real one does: the throw asks for (992, -5),
            // then, from (980, 0), for (1012, -10) and (1012, -9); held inside, it goes to (980, 0) each time.
            TickType t = MotionType("Toss", MAct(30, "Toss", "Embedded", "ThrowIE", "Floor", null, MVar(null, false, TPose(2, 5))));
            var desk = new MotionDesk { Window = new[] { 960, 5, 300, 200 }, ClampTo = work };
            TickWorld w = MotionWorld(desk, null, null, null, t);
            w.CreateAtStart("m1", t, new TickPoint(640, 680), true, "Toss");
            for (int i = 0; i < 3; i++) w.StepOnce();
            AssertEqual("move (980, 0)|move (980, 0)|move (980, 0)", string.Join("|", desk.WindowCalls),
                "A thrown window was not held inside the work area.");
        }
    }
}
