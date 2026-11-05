using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>A screen point in whole pixels, as the source's java.awt.Point: every anchor sum is integer.</summary>
    internal struct TickPoint : IEquatable<TickPoint>
    {
        public readonly int X;
        public readonly int Y;

        public TickPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(TickPoint other) { return X == other.X && Y == other.Y; }
        public override bool Equals(object obj) { return obj is TickPoint && Equals((TickPoint)obj); }
        public override int GetHashCode() { return unchecked(X * 397) ^ Y; }
        public override string ToString() { return "(" + X + ", " + Y + ")"; }
    }

    /// <summary>A border a companion can stand on, cling to or hang from (environment/Border.java).</summary>
    internal interface ITickBorder
    {
        bool IsOn(TickPoint location);

        /// <summary>Where a companion on this border is carried when the border moved since the last tick.</summary>
        TickPoint Move(TickPoint location);
    }

    /// <summary>No border: never on it, never moves (NotOnBorder.java). A bordered action that starts off its
    /// border gets this one, so its first step loses the ground.</summary>
    internal sealed class TickNotOnBorder : ITickBorder
    {
        public static readonly TickNotOnBorder Instance = new TickNotOnBorder();

        private TickNotOnBorder() { }

        public bool IsOn(TickPoint location) { return false; }
        public TickPoint Move(TickPoint location) { return location; }
    }

    /// <summary>
    /// A rectangle the environment reports (a screen, the work area, the active window), with the per-side change since
    /// the previous <see cref="Set"/> that carries a companion standing on a moving window (environment/Area.java).
    /// </summary>
    internal sealed class TickArea
    {
        public bool Visible = true;
        public int Left, Top, Right, Bottom;
        public int DLeft, DTop, DRight, DBottom;

        public readonly TickWall LeftBorder;
        public readonly TickFloorCeiling TopBorder;
        public readonly TickWall RightBorder;
        public readonly TickFloorCeiling BottomBorder;

        public TickArea()
        {
            LeftBorder = new TickWall(this, false);
            TopBorder = new TickFloorCeiling(this, false);
            RightBorder = new TickWall(this, true);
            BottomBorder = new TickFloorCeiling(this, true);
        }

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }

        /// <summary>Area.set: the deltas are against the previous values, so the first Set after construction reports
        /// the whole rectangle as movement (the borders' Move then sees a zero span and leaves the companion alone).</summary>
        public void Set(int x, int y, int width, int height)
        {
            DLeft = x - Left;
            DTop = y - Top;
            DRight = x + width - Right;
            DBottom = y + height - Bottom;
            Left = x;
            Top = y;
            Right = x + width;
            Bottom = y + height;
        }

        public bool Contains(int x, int y)
        {
            return Left <= x && x <= Right && Top <= y && y <= Bottom;
        }
    }

    /// <summary>The top or bottom edge of an area (environment/FloorCeiling.java), integer arithmetic as the source.</summary>
    internal sealed class TickFloorCeiling : ITickBorder
    {
        private readonly TickArea _area;
        private readonly bool _bottom;

        public TickFloorCeiling(TickArea area, bool bottom)
        {
            _area = area;
            _bottom = bottom;
        }

        public int Y { get { return _bottom ? _area.Bottom : _area.Top; } }
        private int DY { get { return _bottom ? _area.DBottom : _area.DTop; } }

        public bool IsOn(TickPoint location)
        {
            return _area.Visible && Y == location.Y && _area.Left <= location.X && location.X <= _area.Right;
        }

        public TickPoint Move(TickPoint location)
        {
            if (!_area.Visible) return location;
            int d = _area.Right - _area.DRight - (_area.Left - _area.DLeft);
            if (d == 0) return location;
            // The source divides the span first: (x - oldLeft) * ((right - left) / d) + left.
            var moved = new TickPoint((location.X - (_area.Left - _area.DLeft)) * ((_area.Right - _area.Left) / d) + _area.Left,
                location.Y + DY);
            if (Math.Abs(moved.X - location.X) >= 80 || moved.Y - location.Y > 20 || moved.Y - location.Y < -80)
                return location;
            return moved;
        }
    }

    /// <summary>The left or right edge of an area (environment/Wall.java), integer arithmetic as the source.</summary>
    internal sealed class TickWall : ITickBorder
    {
        private readonly TickArea _area;
        private readonly bool _right;

        public TickWall(TickArea area, bool right)
        {
            _area = area;
            _right = right;
        }

        public int X { get { return _right ? _area.Right : _area.Left; } }
        private int DX { get { return _right ? _area.DRight : _area.DLeft; } }

        public bool IsOn(TickPoint location)
        {
            return _area.Visible && X == location.X && _area.Top <= location.Y && location.Y <= _area.Bottom;
        }

        public TickPoint Move(TickPoint location)
        {
            if (!_area.Visible) return location;
            int d = _area.Bottom - _area.DBottom - (_area.Top - _area.DTop);
            if (d == 0) return location;
            // Unlike the floor, the wall multiplies first: (y - oldTop) * (bottom - top) / d + top.
            var moved = new TickPoint(location.X + DX,
                (location.Y - (_area.Top - _area.DTop)) * (_area.Bottom - _area.Top) / d + _area.Top);
            if (Math.Abs(moved.X - location.X) >= 80 || Math.Abs(moved.Y - location.Y) >= 80)
                return location;
            return moved;
        }
    }

    /// <summary>
    /// What the world sees of the desktop, once per step (the source's NativeFactory environment). The real one reads
    /// Win32; the parity one applies a scenario's timeline, so a parity run never reads the real mouse or windows.
    /// </summary>
    internal interface ITickEnvironment
    {
        /// <summary>Environment.tick: refresh everything for logical tick <paramref name="tick"/> (0 at startup).</summary>
        void Tick(int tick);

        TickArea Screen { get; }
        TickArea WorkArea { get; }
        IReadOnlyList<TickArea> Screens { get; }

        /// <summary>The interactive window; not <see cref="TickArea.Visible"/> when there is none.</summary>
        TickArea ActiveWindow { get; }

        TickPoint Cursor { get; }
        TickPoint CursorDelta { get; }

        /// <summary>This tick's left-button edges and held-pointer moves, each addressed to the companion whose window
        /// receives it, in order. A parity run takes them from the scenario; the real desktop delivers window events
        /// through the window layer instead, and reports none here.</summary>
        IReadOnlyList<TickInput> Inputs { get; }

        /// <summary>Environment.moveActiveIE (WindowsEnvironment.java:307-311): the interactive window as the desktop
        /// has it NOW is moved so its top-left is <paramref name="topLeft"/>, keeping the width and height
        /// <see cref="ActiveWindow"/> reports. The area itself changes only at the next <see cref="Tick"/>, as the
        /// source's does. False when there is no window to move, or the real desktop's safety rules refused it;
        /// <paramref name="from"/> and <paramref name="to"/> are the window before and after.</summary>
        bool MoveActiveWindow(TickPoint topLeft, out TickRect from, out TickRect to);

        /// <summary>Environment.minimizeActiveIE (WindowsEnvironment.java:313-317): the interactive window found now
        /// is minimised. False when there is none, or the real desktop's safety rules refused it.</summary>
        bool MinimizeActiveWindow();
    }

    internal enum TickInputKind { Press, Release, Move }

    /// <summary>One pointer event for one companion's window, at a screen point (the window layer converts it to the
    /// window-local point the source's MouseEvent carries).</summary>
    internal struct TickInput
    {
        public TickInputKind Kind;
        public string Sid;
        public TickPoint Screen;

        public TickInput(TickInputKind kind, string sid, TickPoint screen)
        {
            Kind = kind;
            Sid = sid;
            Screen = screen;
        }
    }

    /// <summary>
    /// One companion's view of the environment (environment/MascotEnvironment.java): which work area it is in, and
    /// which border it stands on. The work area it last stood in is remembered, as the source does.
    /// </summary>
    internal sealed class TickMascotEnvironment
    {
        private readonly ITickEnvironment _impl;
        private readonly TickMascot _mascot;
        private readonly Func<bool> _multiscreen;
        private TickArea _currentWorkArea;

        public TickMascotEnvironment(ITickEnvironment impl, TickMascot mascot, Func<bool> multiscreen)
        {
            _impl = impl;
            _mascot = mascot;
            _multiscreen = multiscreen;
        }

        public ITickEnvironment Impl { get { return _impl; } }

        public TickArea GetWorkArea() { return GetWorkArea(false); }

        /// <summary>getWorkArea(ignoreSettings): ignoring the settings takes the multiscreen path in single-screen mode
        /// too, so the companion's work area follows its anchor (refreshWorkArea, which Dragged calls every tick).</summary>
        public TickArea GetWorkArea(bool ignoreSettings)
        {
            TickPoint a = _mascot.Anchor;
            if (_currentWorkArea != null)
            {
                if (ignoreSettings || _multiscreen())
                {
                    if (_currentWorkArea != _impl.WorkArea && ContainsRect(_currentWorkArea, _impl.WorkArea)
                        && _impl.WorkArea.Contains(a.X, a.Y))
                    {
                        _currentWorkArea = _impl.WorkArea;
                        return _currentWorkArea;
                    }
                    if (_currentWorkArea.Contains(a.X, a.Y)) return _currentWorkArea;
                }
                else return _currentWorkArea;
            }
            if (_impl.WorkArea.Contains(a.X, a.Y))
            {
                _currentWorkArea = _impl.WorkArea;
                return _currentWorkArea;
            }
            foreach (TickArea screen in _impl.Screens)
                if (screen.Contains(a.X, a.Y))
                {
                    _currentWorkArea = screen;
                    return _currentWorkArea;
                }
            _currentWorkArea = _impl.WorkArea;
            return _currentWorkArea;
        }

        // The source returns `new Area()` here: VISIBLE, every side 0, never Set. Its top border is "on" only at (0, 0),
        // and that is kept rather than tidied into an invisible area.
        private static readonly TickArea NoWindow = new TickArea();

        public TickArea GetActiveWindow()
        {
            TickArea active = _impl.ActiveWindow;
            // Single-screen mode swaps a window that does not even TOUCH the companion's own work area for the empty
            // area (MascotEnvironment.java:82 tests intersects, not contains: a window partly off it is kept).
            if (_currentWorkArea != null && !_multiscreen() && !IntersectsRect(_currentWorkArea, active))
                return NoWindow;
            return active;
        }

        /// <summary>refreshWorkArea: the work area looked up again with the settings ignored.</summary>
        public void RefreshWorkArea() { GetWorkArea(true); }

        public ITickBorder GetFloor() { return GetFloor(false); }

        /// <summary>getFloor(ignoreSeparator): with the separator ignored, a work-area bottom that is not exactly one
        /// screen edge (two stacked screens meet there) is no floor, so Fall passes through it.</summary>
        public ITickBorder GetFloor(bool ignoreSeparator)
        {
            TickPoint a = _mascot.Anchor;
            TickArea active = GetActiveWindow();
            if (active.TopBorder.IsOn(a)) return active.TopBorder;
            TickArea work = GetWorkArea();
            if (work.BottomBorder.IsOn(a) && (!ignoreSeparator || IsScreenTopBottom(a))) return work.BottomBorder;
            return TickNotOnBorder.Instance;
        }

        public ITickBorder GetCeiling()
        {
            TickPoint a = _mascot.Anchor;
            TickArea active = GetActiveWindow();
            if (active.BottomBorder.IsOn(a)) return active.BottomBorder;
            TickArea work = GetWorkArea();
            if (work.TopBorder.IsOn(a)) return work.TopBorder;
            return TickNotOnBorder.Instance;
        }

        public ITickBorder GetWall() { return GetWall(false); }

        /// <summary>getWall(ignoreSeparator): as <see cref="GetFloor(bool)"/>, for side-by-side screens.</summary>
        public ITickBorder GetWall(bool ignoreSeparator)
        {
            TickPoint a = _mascot.Anchor;
            TickArea active = GetActiveWindow();
            TickArea work;
            if (_mascot.LookRight)
            {
                if (active.LeftBorder.IsOn(a)) return active.LeftBorder;
                work = GetWorkArea();
                if (work.RightBorder.IsOn(a) && (!ignoreSeparator || IsScreenLeftRight(a))) return work.RightBorder;
            }
            else
            {
                if (active.RightBorder.IsOn(a)) return active.RightBorder;
                work = GetWorkArea();
                if (work.LeftBorder.IsOn(a) && (!ignoreSeparator || IsScreenLeftRight(a))) return work.LeftBorder;
            }
            return TickNotOnBorder.Instance;
        }

        // Environment.isScreenTopBottom / isScreenLeftRight: the point is on exactly one screen edge, or on none and on
        // the environment's own work area's edge (its WorkArea, not the companion's).
        private bool IsScreenTopBottom(TickPoint location)
        {
            int count = 0;
            foreach (TickArea area in _impl.Screens)
            {
                if (area.TopBorder.IsOn(location)) ++count;
                if (area.BottomBorder.IsOn(location)) ++count;
            }
            if (count == 0)
            {
                if (_impl.WorkArea.TopBorder.IsOn(location)) return true;
                if (_impl.WorkArea.BottomBorder.IsOn(location)) return true;
            }
            return count == 1;
        }

        private bool IsScreenLeftRight(TickPoint location)
        {
            int count = 0;
            foreach (TickArea area in _impl.Screens)
            {
                if (area.LeftBorder.IsOn(location)) ++count;
                if (area.RightBorder.IsOn(location)) ++count;
            }
            if (count == 0)
            {
                if (_impl.WorkArea.LeftBorder.IsOn(location)) return true;
                if (_impl.WorkArea.RightBorder.IsOn(location)) return true;
            }
            return count == 1;
        }

        /// <summary>java.awt.Rectangle.intersects, overflow cases included: an empty rectangle intersects nothing.</summary>
        private static bool IntersectsRect(TickArea a, TickArea r)
        {
            int tw = a.Width, th = a.Height, rw = r.Width, rh = r.Height;
            if (rw <= 0 || rh <= 0 || tw <= 0 || th <= 0) return false;
            int tx = a.Left, ty = a.Top, rx = r.Left, ry = r.Top;
            rw = unchecked(rw + rx);
            rh = unchecked(rh + ry);
            tw = unchecked(tw + tx);
            th = unchecked(th + ty);
            return (rw < rx || rw > tx) && (rh < ry || rh > ty) && (tw < tx || tw > rx) && (th < ty || th > ry);
        }

        private static bool ContainsRect(TickArea outer, TickArea inner)
        {
            // java.awt.Rectangle.contains(Rectangle): every corner inside, empty rectangles never contained.
            if (inner.Width <= 0 || inner.Height <= 0 || outer.Width <= 0 || outer.Height <= 0) return false;
            return inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
        }
    }

    /// <summary>
    /// The source's wall clock (Manager.java's thread): it wakes about every millisecond and steps once when 40 ms are due
    /// since the last step's slot, moving the slot on by 40 ms, or to now when it is more than two steps behind, so a stall
    /// is never made up in a burst. The host's timer polls every <see cref="PollMilliseconds"/> and asks <see cref="Due"/>.
    /// A WinForms timer fires on the system timer's tick (15.6 ms here unless something raised the resolution), so one set
    /// to 40 ms fires every 46.9 ms (measured 2026-11-05: 64 firings in 3001 ms) and ran every companion at 85% speed.
    /// </summary>
    internal sealed class TickPacer
    {
        public const int TickMilliseconds = 40;
        public const int PollMilliseconds = 10;

        private long _slot;

        public TickPacer(long now) { _slot = now; }

        /// <summary>The clock starts again at <paramref name="now"/>: the first step is one tick later.</summary>
        public void Restart(long now) { _slot = now; }

        /// <summary>Whether a step is due at <paramref name="now"/> (milliseconds); when it is, the slot moves on.</summary>
        public bool Due(long now)
        {
            if (now - _slot < TickMilliseconds) return false;
            if (now > _slot + TickMilliseconds * 2) _slot = now;
            else _slot += TickMilliseconds;
            return true;
        }
    }
}
