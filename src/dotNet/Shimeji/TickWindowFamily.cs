using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The window family of embedded classes (grimoire/06 sections 5 and 8): WalkWithIE, FallWithIE and JumpWithIE carry
    /// the interactive window by its lower corner, ThrowIE throws it, MinimizeIE minimises it. One class per source class
    /// (action/WalkWithIE.java and the rest), each method body in the source's order of evaluation.
    ///
    /// Window actions are opt-in per companion type and off by default: the switch is the type's
    /// <see cref="Setting"/>. Off, a class plays as its base class and never reads or touches a window
    /// (policy:window-off-plays); the source has no such switch, and ends the action when its own Throwing setting is
    /// off, a check every class here keeps as well. The engine never calls Win32: it asks the environment
    /// (<see cref="ITickEnvironment.MoveActiveWindow"/>, <see cref="ITickEnvironment.MinimizeActiveWindow"/>), and the real
    /// desktop's environment applies the host's safety rules (<see cref="TickWindowRules"/>) on top of the source's.
    /// </summary>
    internal static class TickWindowFamily
    {
        /// <summary>The per-type opt-in (grimoire/06 section 8), read through <see cref="ITickSettings.Get"/>.</summary>
        public const string Setting = "WindowActions";

        public static TickAction TryCreate(TickWorld world, TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Class)
            {
                case "WalkWithIE": return new TickWalkWithIE(world, def, incoming);
                case "FallWithIE": return new TickFallWithIE(world, def, incoming);
                case "JumpWithIE": return new TickJumpWithIE(world, def, incoming);
                case "ThrowIE": return new TickThrowIE(world, def, incoming);
                case "MinimizeIE": return new TickMinimizeIE(world, def, incoming);
                default: return null;
            }
        }

        /// <summary>Main.getMascotBooleanSetting("Throwing", mascot): the source's own check at the top of every window
        /// class's hasNext (WalkWithIE.java:39, FallWithIE.java:39, JumpWithIE.java:39, ThrowIE.java:42,
        /// MinimizeIE.java:27). Off, the action ends.</summary>
        internal static bool Throwing(TickWorld world, TickMascot mascot)
        {
            return world.Settings.Get("Throwing", mascot.Type.Name);
        }

        /// <summary>The type's opt-in. Off, the window classes play as their bases (policy:window-off-plays).</summary>
        internal static bool IsOn(TickWorld world, TickMascot mascot)
        {
            return world.Settings.Get(Setting, mascot.Type.Name);
        }

        /// <summary>MascotEnvironment.moveActiveIE, reported when the environment moved a window.</summary>
        internal static void MoveActiveWindow(TickWorld world, TickMascot mascot, TickPoint topLeft)
        {
            TickRect from, to;
            if (world.Environment.MoveActiveWindow(topLeft, out from, out to)) world.Observer.WindowMoved(mascot, from, to);
        }

        /// <summary>MascotEnvironment.minimizeActiveIE, reported when the environment minimised a window.</summary>
        internal static void MinimizeActiveWindow(TickWorld world, TickMascot mascot)
        {
            if (world.Environment.MinimizeActiveWindow()) world.Observer.WindowMinimized(mascot);
        }
    }

    /// <summary>
    /// The grip the three carrying classes share. Their tick bodies are identical in the source (WalkWithIE.java:45-92,
    /// FallWithIE.java:45-92, JumpWithIE.java:45-92): before the base class moves the companion, the window must be
    /// visible and held EXACTLY at its lower corner; after, the window is moved so that corner is at the grip again.
    /// </summary>
    internal static class TickWindowGrip
    {
        /// <summary>The window is visible, then IeOffsetX and IeOffsetY are read (in that order), then the grip is
        /// tested: facing right, anchor.x - IeOffsetX is the window's left; facing left, anchor.x + IeOffsetX is its
        /// right; either way anchor.y + IeOffsetY is its bottom. Anything else loses the ground.</summary>
        public static void Hold(TickAction action, TickMascot mascot, TickArea activeIE, out int offsetX, out int offsetY)
        {
            if (!activeIE.Visible) throw new TickLostGroundException();

            offsetX = action.EvalInt("IeOffsetX", 0);
            offsetY = action.EvalInt("IeOffsetY", 0);

            if (mascot.LookRight)
            {
                if (mascot.Anchor.X - offsetX != activeIE.Left || mascot.Anchor.Y + offsetY != activeIE.Bottom)
                    throw new TickLostGroundException();
            }
            else
            {
                if (mascot.Anchor.X + offsetX != activeIE.Right || mascot.Anchor.Y + offsetY != activeIE.Bottom)
                    throw new TickLostGroundException();
            }
        }

        /// <summary>The window follows, with the facing the base class left and the size the area had at the start of
        /// the step (the area is refreshed only at the next step). The source tests the same area's visibility again,
        /// which nothing in a step changes.</summary>
        public static void Carry(TickWorld world, TickMascot mascot, TickArea activeIE, int offsetX, int offsetY)
        {
            if (!activeIE.Visible) return;
            if (mascot.LookRight)
                TickWindowFamily.MoveActiveWindow(world, mascot,
                    new TickPoint(mascot.Anchor.X - offsetX, mascot.Anchor.Y + offsetY - activeIE.Height));
            else
                TickWindowFamily.MoveActiveWindow(world, mascot,
                    new TickPoint(mascot.Anchor.X + offsetX - activeIE.Width, mascot.Anchor.Y + offsetY - activeIE.Height));
        }
    }

    /// <summary>WalkWithIE (WalkWithIE.java): a Move that carries the interactive window by its lower corner.</summary>
    internal sealed class TickWalkWithIE : TickMove
    {
        public TickWalkWithIE(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            if (!TickWindowFamily.Throwing(World, Mascot)) return false;
            return base.HasNext();
        }

        protected override void Tick()
        {
            // policy:window-off-plays: with the type's window actions off, the walk plays as a plain Move and the
            // window is neither read nor moved. (The source has no such switch; its Throwing setting ends the action.)
            if (!TickWindowFamily.IsOn(World, Mascot))
            {
                base.Tick();
                return;
            }
            TickArea activeIE = Mascot.Environment.GetActiveWindow();
            int offsetX, offsetY;
            TickWindowGrip.Hold(this, Mascot, activeIE, out offsetX, out offsetY);
            base.Tick();
            TickWindowGrip.Carry(World, Mascot, activeIE, offsetX, offsetY);
        }
    }

    /// <summary>FallWithIE (FallWithIE.java): a Fall that carries the interactive window by its lower corner.</summary>
    internal sealed class TickFallWithIE : TickFall
    {
        public TickFallWithIE(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            if (!TickWindowFamily.Throwing(World, Mascot)) return false;
            return base.HasNext();
        }

        protected override void Tick()
        {
            // policy:window-off-plays: off, a plain Fall; the window is neither read nor moved.
            if (!TickWindowFamily.IsOn(World, Mascot))
            {
                base.Tick();
                return;
            }
            TickArea activeIE = Mascot.Environment.GetActiveWindow();
            int offsetX, offsetY;
            TickWindowGrip.Hold(this, Mascot, activeIE, out offsetX, out offsetY);
            base.Tick();
            TickWindowGrip.Carry(World, Mascot, activeIE, offsetX, offsetY);
        }
    }

    /// <summary>JumpWithIE (JumpWithIE.java): a Jump that carries the interactive window by its lower corner.</summary>
    internal sealed class TickJumpWithIE : TickJump
    {
        public TickJumpWithIE(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            if (!TickWindowFamily.Throwing(World, Mascot)) return false;
            return base.HasNext();
        }

        protected override void Tick()
        {
            // policy:window-off-plays: off, a plain Jump; the window is neither read nor moved.
            if (!TickWindowFamily.IsOn(World, Mascot))
            {
                base.Tick();
                return;
            }
            TickArea activeIE = Mascot.Environment.GetActiveWindow();
            int offsetX, offsetY;
            TickWindowGrip.Hold(this, Mascot, activeIE, out offsetX, out offsetY);
            base.Tick();
            TickWindowGrip.Carry(World, Mascot, activeIE, offsetX, offsetY);
        }
    }

    /// <summary>
    /// ThrowIE (ThrowIE.java): an Animate that, while a window is visible, moves it each step by InitialVX (to the right
    /// when the companion faces right, else to the left) and by InitialVY + (int)(age * Gravity) vertically, measured
    /// from where the window was at the start of the step.
    /// </summary>
    internal sealed class TickThrowIE : TickAnimate
    {
        public TickThrowIE(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            if (!TickWindowFamily.Throwing(World, Mascot)) return false;

            // policy:window-off-plays: off, the throw plays as a plain Animate, so whether a window is visible is not
            // read. (The source reads it, then its Throwing setting decides.)
            if (!TickWindowFamily.IsOn(World, Mascot)) return base.HasNext();

            // Read before the base check, as the source does (ThrowIE.java:45-47).
            bool ieVisible = Mascot.Environment.GetActiveWindow().Visible;

            return base.HasNext() && ieVisible;
        }

        protected override void Tick()
        {
            base.Tick();

            // policy:window-off-plays: off, the window is neither read nor moved.
            if (!TickWindowFamily.IsOn(World, Mascot)) return;

            TickArea activeIE = Mascot.Environment.GetActiveWindow();

            if (activeIE.Visible)
            {
                // Each Point's coordinates in Java's order: left, InitialVX, top, InitialVY, the age, Gravity.
                if (Mascot.LookRight)
                {
                    TickWindowFamily.MoveActiveWindow(World, Mascot, new TickPoint(activeIE.Left + InitialVx(),
                        activeIE.Top + InitialVy() + TickScriptContext.JavaInt(Time * Gravity())));
                }
                else
                {
                    TickWindowFamily.MoveActiveWindow(World, Mascot, new TickPoint(activeIE.Left - InitialVx(),
                        activeIE.Top + InitialVy() + TickScriptContext.JavaInt(Time * Gravity())));
                }
            }
        }

        private int InitialVx() { return EvalInt("InitialVX", 32); }

        private int InitialVy() { return EvalInt("InitialVY", -10); }

        private double Gravity() { return EvalDouble("Gravity", 0.5); }
    }

    /// <summary>
    /// MinimizeIE (MinimizeIE.java): an Animate that, while a window is visible, minimises it on the last tick of its
    /// animation (or its only one). The companion does not have to stand on the window.
    /// </summary>
    internal sealed class TickMinimizeIE : TickAnimate
    {
        public TickMinimizeIE(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        public override bool HasNext()
        {
            if (!TickWindowFamily.Throwing(World, Mascot)) return false;

            // policy:window-off-plays: off, a plain Animate; whether a window is visible is not read.
            if (!TickWindowFamily.IsOn(World, Mascot)) return base.HasNext();

            bool ieVisible = Mascot.Environment.GetActiveWindow().Visible;

            return base.HasNext() && ieVisible;
        }

        protected override void Tick()
        {
            base.Tick();

            // policy:window-off-plays: off, the animation plays out and nothing is minimised.
            if (!TickWindowFamily.IsOn(World, Mascot)) return;

            // The source looks the animation up again for each test, the second only when the first fails
            // (MinimizeIE.java:40); a null animation is its NullPointerException, the companion's end here.
            if (Time == TickMotionFamily.Require(CurrentVariant(), Name).Duration - 1
                || TickMotionFamily.Require(CurrentVariant(), Name).Duration == 1)
            {
                TickWindowFamily.MinimizeActiveWindow(World, Mascot);
            }
        }
    }

    /// <summary>
    /// The one window a scripted desktop has (a parity scenario's activeWindow timeline, the CoreTests desk), modelled as
    /// the source's WindowsEnvironment sees a real one: the walk finds it only while it is shown, not minimised and
    /// intersecting the screen (WindowsEnvironment.java:109-138); a move or a minimise changes the desktop at once, while
    /// the area the companions read is refreshed only at the next tick (WindowsEnvironment.java:292-300). Showing it
    /// again (the next timeline entry) replaces it whole, minimised state included.
    /// </summary>
    internal sealed class TickDesktopWindow
    {
        public bool Shown { get; private set; }
        public bool Minimised { get; private set; }
        public TickRect Rect { get; private set; }

        public void Show(TickRect rect)
        {
            Shown = true;
            Minimised = false;
            Rect = rect;
        }

        public void Hide()
        {
            Shown = false;
            Minimised = false;
        }

        /// <summary>findActiveIE would return it.</summary>
        public bool IsFound(TickArea screen)
        {
            return Shown && !Minimised && TickWindowRules.Intersects(Rect, new TickRect(screen.Left, screen.Top, screen.Width, screen.Height));
        }

        /// <summary>Environment.tick's refresh of the active-window area: visible and Set while the window is found;
        /// otherwise invisible, with the rectangle left as it was.</summary>
        public void ApplyTo(TickArea area, TickArea screen)
        {
            bool found = IsFound(screen);
            area.Visible = found;
            if (found) area.Set(Rect.X, Rect.Y, Rect.Width, Rect.Height);
        }

        /// <summary>moveIE(findActiveIE(), ...): no window found, no move.</summary>
        public bool Move(TickPoint topLeft, int width, int height, TickArea screen, out TickRect from, out TickRect to)
        {
            from = Rect;
            to = Rect;
            if (!IsFound(screen)) return false;
            to = new TickRect(topLeft.X, topLeft.Y, width, height);
            Rect = to;
            return true;
        }

        /// <summary>minimizeIE(findActiveIE()): no window found, nothing minimised.</summary>
        public bool Minimize(TickArea screen)
        {
            if (!IsFound(screen)) return false;
            Minimised = true;
            return true;
        }
    }

    /// <summary>One top-level window as the real desktop's walk reads it. The host (TickHost.cs) gathers these facts
    /// from Win32, so the source's rules and the host's safety rules below are pure functions CoreTests can pin.</summary>
    internal struct TickWindowFacts
    {
        // Read by the source's walk (WindowsEnvironment.isViableIE and isIE).
        public bool Visible;
        public bool Cloaked;
        public bool Maximised;
        public bool Minimised;
        public string Title;
        public TickRect Bounds;

        // Read by the host only.
        public bool OwnProcess;
        public bool Shell;
        public bool ToolWindow;
        public bool Fullscreen;
        public bool MonitorBlocked;
        public bool Elevated;
        public bool Hung;
        public bool MovingOrSizing;
    }

    /// <summary>What the source's walk makes of one window (WindowsEnvironment.IEResult).</summary>
    internal enum TickWindowVerdict
    {
        /// <summary>NOT_IE: passed over.</summary>
        NotInteractive,
        /// <summary>IE_OUT_OF_BOUNDS: interactive but off the screen; passed over.</summary>
        OutOfBounds,
        /// <summary>IE: the active window.</summary>
        Interactive,
        /// <summary>INVALID: a maximised window; the walk ends with no active window.</summary>
        StopWalk,
    }

    /// <summary>
    /// The real desktop's window rules, pure. Detection follows the source (WindowsEnvironment.java:43-167); acting adds
    /// the host's safety rules (grimoire/06 section 8), each a recorded divergence.
    /// </summary>
    internal static class TickWindowRules
    {
        /// <summary>The source's shipped interactive-window list (conf/settings.properties:3 of the source archive). The
        /// code's own default, when the file has no entry, is empty (WindowsEnvironment.java:83), which admits none.</summary>
        public const string DefaultWhitelist = "Chat/Notepad/Friends/Windows Live Messenger";

        /// <summary>The shipped settings have no blacklist entry; the code's default is empty (WindowsEnvironment.java:66).</summary>
        public const string DefaultBlacklist = "";

        /// <summary>String.split("/") as the source splits a list. Entries are matched untrimmed; a blank one is
        /// skipped by the matcher, so the empty strings Java's split would drop make no difference.</summary>
        public static string[] ParseList(string property)
        {
            return (property ?? "").Split('/');
        }

        /// <summary>
        /// WindowsEnvironment.isIE (:43-107): an empty title and exactly "Program Manager" are never interactive; a
        /// blacklist entry contained in the title (case-sensitive) refuses it; else a whitelist entry contained in it
        /// admits it; else it is interactive only when a blacklist is in use and no whitelist is. Neither list in use
        /// admits none. The source caches each window's verdict until its settings reload; the host reads it afresh.
        /// </summary>
        public static bool IsTitleInteractive(string title, string[] whitelist, string[] blacklist)
        {
            if (string.IsNullOrEmpty(title) || title == "Program Manager") return false;

            bool blacklistInUse = false;
            foreach (string entry in blacklist ?? new string[0])
            {
                if (entry.Trim().Length == 0) continue;
                blacklistInUse = true;
                if (title.IndexOf(entry, StringComparison.Ordinal) >= 0) return false;
            }

            bool whitelistInUse = false;
            foreach (string entry in whitelist ?? new string[0])
            {
                if (entry.Trim().Length == 0) continue;
                whitelistInUse = true;
                if (title.IndexOf(entry, StringComparison.Ordinal) >= 0) return true;
            }

            return !whitelistInUse && blacklistInUse;
        }

        /// <summary>
        /// WindowsEnvironment.isViableIE (:109-138), in its order: visible, then not cloaked, then a maximised window
        /// ends the walk (before its title is read), then the title rule and not minimised, then intersecting the screen.
        /// Divergence: this app's own windows are passed over first (the source's own windows have no title).
        /// </summary>
        public static TickWindowVerdict Classify(TickWindowFacts w, TickRect screen, string[] whitelist, string[] blacklist)
        {
            if (w.OwnProcess) return TickWindowVerdict.NotInteractive;
            if (!w.Visible) return TickWindowVerdict.NotInteractive;
            if (w.Cloaked) return TickWindowVerdict.NotInteractive;
            if (w.Maximised) return TickWindowVerdict.StopWalk;
            if (IsTitleInteractive(w.Title, whitelist, blacklist) && !w.Minimised)
                return Intersects(w.Bounds, screen) ? TickWindowVerdict.Interactive : TickWindowVerdict.OutOfBounds;
            return TickWindowVerdict.NotInteractive;
        }

        /// <summary>findActiveIE (:140-167): the first interactive window in EnumWindows (z) order, or -1 when there is
        /// none or a maximised window comes first.</summary>
        public static int FindActive(IList<TickWindowFacts> zOrder, TickRect screen, string[] whitelist, string[] blacklist)
        {
            for (int i = 0; i < zOrder.Count; i++)
            {
                switch (Classify(zOrder[i], screen, whitelist, blacklist))
                {
                    case TickWindowVerdict.Interactive: return i;
                    case TickWindowVerdict.StopWalk: return -1;
                }
            }
            return -1;
        }

        /// <summary>
        /// The host's safety rules (grimoire/06 section 8): why a window may not be moved or minimised, or null when it
        /// may. Never this app's own windows or the shell; never one that is hidden, cloaked, minimised, maximised,
        /// fullscreen or on a monitor a fullscreen window holds; never a tool window, one at a higher integrity level,
        /// one that is not responding, or one the user is moving or resizing.
        /// </summary>
        public static string Refusal(TickWindowFacts w)
        {
            if (w.OwnProcess) return "a window of this app";
            if (w.Shell) return "the shell";
            if (!w.Visible) return "hidden";
            if (w.Cloaked) return "cloaked";
            if (w.Minimised) return "minimised";
            if (w.Maximised) return "maximised";
            if (w.Fullscreen) return "fullscreen";
            if (w.MonitorBlocked) return "on a monitor a fullscreen window holds";
            if (w.ToolWindow) return "a tool window";
            if (w.Elevated) return "at a higher integrity level";
            if (w.Hung) return "not responding";
            if (w.MovingOrSizing) return "being moved or resized";
            return null;
        }

        /// <summary>The host's clamp: the window's top-left so the whole window stays inside the work area of the
        /// monitor it is on, so it never leaves it or crosses to another monitor. A window wider or taller than the work
        /// area is pinned to its left or top edge.</summary>
        public static TickPoint ClampInto(TickPoint topLeft, int width, int height, TickRect workArea)
        {
            int x = Math.Max(workArea.X, Math.Min(topLeft.X, workArea.X + workArea.Width - width));
            int y = Math.Max(workArea.Y, Math.Min(topLeft.Y, workArea.Y + workArea.Height - height));
            return new TickPoint(x, y);
        }

        /// <summary>java.awt.Rectangle.intersects, overflow cases included: an empty rectangle intersects nothing.</summary>
        public static bool Intersects(TickRect a, TickRect r)
        {
            int tw = a.Width, th = a.Height, rw = r.Width, rh = r.Height;
            if (rw <= 0 || rh <= 0 || tw <= 0 || th <= 0) return false;
            int tx = a.X, ty = a.Y, rx = r.X, ry = r.Y;
            rw = unchecked(rw + rx);
            rh = unchecked(rh + ry);
            tw = unchecked(tw + tx);
            th = unchecked(th + ty);
            return (rw < rx || rw > tx) && (rh < ry || rh > ty) && (tw < tx || tw > rx) && (th < ty || th > ry);
        }
    }
}
