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

        public TickArea GetWorkArea()
        {
            TickPoint a = _mascot.Anchor;
            if (_currentWorkArea != null)
            {
                if (_multiscreen())
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
            // Single-screen mode swaps a window that is not inside the companion's own work area for the empty area.
            if (_currentWorkArea != null && !_multiscreen() && !ContainsRect(_currentWorkArea, active))
                return NoWindow;
            return active;
        }

        public ITickBorder GetFloor()
        {
            TickPoint a = _mascot.Anchor;
            TickArea active = GetActiveWindow();
            if (active.TopBorder.IsOn(a)) return active.TopBorder;
            TickArea work = GetWorkArea();
            if (work.BottomBorder.IsOn(a)) return work.BottomBorder;
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

        public ITickBorder GetWall()
        {
            TickPoint a = _mascot.Anchor;
            TickArea active = GetActiveWindow();
            TickArea work;
            if (_mascot.LookRight)
            {
                if (active.LeftBorder.IsOn(a)) return active.LeftBorder;
                work = GetWorkArea();
                if (work.RightBorder.IsOn(a)) return work.RightBorder;
            }
            else
            {
                if (active.RightBorder.IsOn(a)) return active.RightBorder;
                work = GetWorkArea();
                if (work.LeftBorder.IsOn(a)) return work.LeftBorder;
            }
            return TickNotOnBorder.Instance;
        }

        private static bool ContainsRect(TickArea outer, TickArea inner)
        {
            // java.awt.Rectangle.contains(Rectangle): every corner inside, empty rectangles never contained.
            if (inner.Width <= 0 || inner.Height <= 0 || outer.Width <= 0 || outer.Height <= 0) return false;
            return inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
        }
    }
}
