using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopAICompanion
{
    /// <summary>Single source of truth for the persisted 1/2/3 scale level and 1x/2x/4x factor.</summary>
    internal static class ScalePolicy
    {
        public const int MinimumLevel = 1;
        public const int MaximumLevel = 3;

        public static int ClampLevel(int level)
        {
            if (level < MinimumLevel) return MinimumLevel;
            if (level > MaximumLevel) return MaximumLevel;
            return level;
        }

        public static int FactorFromLevel(int level)
        {
            switch (ClampLevel(level))
            {
                case 2: return 2;
                case 3: return 4;
                default: return 1;
            }
        }

        public static int ClampFactor(int factor)
        {
            if (factor <= 1) return 1;
            if (factor <= 2) return 2;
            return 4;
        }

        public static int FitFactorForFrame(
            int requestedFactor,
            int sourceWidth,
            int sourceHeight,
            int maximumDimension)
        {
            if (sourceWidth <= 0)
                throw new ArgumentOutOfRangeException("sourceWidth");
            if (sourceHeight <= 0)
                throw new ArgumentOutOfRangeException("sourceHeight");
            if (maximumDimension <= 0)
                throw new ArgumentOutOfRangeException("maximumDimension");

            int effectiveFactor = ClampFactor(requestedFactor);
            while (effectiveFactor > 1 &&
                   ((long)sourceWidth * effectiveFactor > maximumDimension ||
                    (long)sourceHeight * effectiveFactor > maximumDimension))
                effectiveFactor = effectiveFactor == 4 ? 2 : 1;
            return effectiveFactor;
        }

        public static string StatusText(int requestedLevel, int effectiveFactor)
        {
            int requested = FactorFromLevel(requestedLevel);
            int effective = ClampFactor(effectiveFactor);
            return requested == effective
                ? requested.ToString() + "x"
                : requested.ToString() + "x requested (" +
                    effective.ToString() + "x active)";
        }

        public static int Scale(int value, int factor)
        {
            if (factor < 1) factor = 1;
            long scaled = (long)value * factor;
            if (scaled > int.MaxValue) return int.MaxValue;
            if (scaled < int.MinValue) return int.MinValue;
            return (int)scaled;
        }

        // --- Fractional scale (the user-facing size slider, which may go BELOW 1x). The integer path above
        // still drives the 'scale' expression variable exposed to pet XML, so hand-authored pets that read
        // 'scale' are unaffected; only frame size and movement use the fractional factor. ---

        public const double MinimumFactorD = 0.25;
        public const double MaximumFactorD = 4.0;

        public static double ClampFactorD(double factor)
        {
            if (double.IsNaN(factor) || factor < MinimumFactorD) return MinimumFactorD;
            if (factor > MaximumFactorD) return MaximumFactorD;
            return factor;
        }

        /// <summary>The integer level a fractional factor exposes to XML's 'scale' variable: rounded, never
        /// below 1, so a sub-1 pet reads scale=1 exactly like an unscaled pet (back-compat).</summary>
        public static int LevelForExpression(double factor)
        {
            int level = (int)Math.Round(factor);
            return level < 1 ? 1 : level;
        }

        /// <summary>Clamp a fractional factor so a source cell neither exceeds the runtime max dimension nor
        /// shrinks a frame below 1px. The cap wins over the floor for a very large source.</summary>
        public static double FitFactorForFrameD(
            double requestedFactor, int sourceWidth, int sourceHeight, int maximumDimension)
        {
            if (sourceWidth <= 0) throw new ArgumentOutOfRangeException("sourceWidth");
            if (sourceHeight <= 0) throw new ArgumentOutOfRangeException("sourceHeight");
            if (maximumDimension <= 0) throw new ArgumentOutOfRangeException("maximumDimension");

            double f = ClampFactorD(requestedFactor);
            int larger = Math.Max(sourceWidth, sourceHeight);
            if ((double)larger * f > maximumDimension) f = (double)maximumDimension / larger;
            int smaller = Math.Min(sourceWidth, sourceHeight);
            if (smaller > 0 && (double)smaller * f < 1.0) f = 1.0 / smaller;
            if ((double)larger * f > maximumDimension) f = (double)maximumDimension / larger;
            return f;
        }

        /// <summary>
        /// Scale a VELOCITY, guaranteeing that a moving animation still moves.
        ///
        /// ScaleD rounds to an int, so at small scales a slow animation rounds to a dead stop: a walk of -2 at
        /// 25% is -0.5, and Math.Round's banker's rounding makes that exactly 0. The pet then plays its walk
        /// cycle on the spot for ever -- reported on a 25% Luffy, but it hits ANY pet whose walk is 1 or 2
        /// px/step, which is most of them.
        ///
        /// Zero stays zero: a still pose must not be given motion it never had. Anything non-zero keeps its
        /// SIGN and gets at least one pixel, so the animation's intent ("this one travels") survives every
        /// scale. A 25% pet then walks proportionally faster than its art suggests, which is visible but
        /// correct; frozen is neither.
        /// </summary>
        public static int ScaleVelocity(int value, double factor)
        {
            if (value == 0) return 0;
            int scaled = ScaleD(value, factor);
            if (scaled != 0) return scaled;
            return value > 0 ? 1 : -1;
        }

        public static int ScaleD(int value, double factor)
        {
            if (double.IsNaN(factor) || factor <= 0) factor = 1.0;
            double scaled = value * factor;
            if (scaled > int.MaxValue) return int.MaxValue;
            if (scaled < int.MinValue) return int.MinValue;
            return (int)Math.Round(scaled);
        }

        // The size slider is stored as an integer PERCENT (100 = 1x) so settings stay integer-clean while
        // still expressing sub-1 sizes; it converts to the fractional factor above.
        public const int MinimumPercent = 25;
        public const int MaximumPercent = 400;

        public static int ClampPercent(int percent)
        {
            if (percent < MinimumPercent) return MinimumPercent;
            if (percent > MaximumPercent) return MaximumPercent;
            return percent;
        }

        public static double FactorFromPercent(int percent)
        {
            return ClampPercent(percent) / 100.0;
        }

        public static int PercentFromLevel(int level)
        {
            return FactorFromLevel(level) * 100;   // 1x -> 100, 2x -> 200, 4x -> 400
        }
    }

    /// <summary>Dimension-only values exposed to animation XML expressions.</summary>
    internal struct ScreenMetrics
    {
        public int ScreenWidth;
        public int ScreenHeight;
        public int WorkAreaWidth;
        public int WorkAreaHeight;
    }

    /// <summary>
    /// Result of testing a falling pet against desktop windows. The explicit hit flag keeps every
    /// virtual-screen Y coordinate valid, including negative values and -1.
    /// </summary>
    internal struct WindowTopHit
    {
        public bool Found { get; private set; }
        public int Top { get; private set; }

        public static WindowTopHit None
        {
            get { return new WindowTopHit(); }
        }

        public static WindowTopHit At(int top)
        {
            return new WindowTopHit
            {
                Found = true,
                Top = top
            };
        }
    }

    /// <summary>
    /// Tracks objects that have left the active collection but still own shared runtime state
    /// while an asynchronous retirement animation completes.
    /// </summary>
    internal sealed class RetiringValueRegistry<T> where T : class
    {
        private readonly object sync = new object();
        private readonly HashSet<T> values = new HashSet<T>();

        public int Count
        {
            get
            {
                lock (sync)
                    return values.Count;
            }
        }

        public bool Add(T value)
        {
            if (value == null) return false;
            lock (sync)
                return values.Add(value);
        }

        public bool Remove(T value)
        {
            if (value == null) return false;
            lock (sync)
                return values.Remove(value);
        }

        public T FirstOrDefault()
        {
            lock (sync)
            {
                foreach (T value in values)
                    return value;
                return null;
            }
        }

        public IList<T> Drain()
        {
            lock (sync)
            {
                var snapshot = new List<T>(values);
                values.Clear();
                return snapshot;
            }
        }
    }

    internal static class UnicodeTextProgress
    {
        public static int NextCodePointBoundary(string text, int currentLength)
        {
            text = text ?? "";
            if (currentLength < 0) currentLength = 0;
            if (currentLength >= text.Length) return text.Length;
            if (char.IsHighSurrogate(text[currentLength]) &&
                currentLength + 1 < text.Length &&
                char.IsLowSurrogate(text[currentLength + 1]))
                return currentLength + 2;
            return currentLength + 1;
        }

        public static string TruncateAtCodePointBoundary(
            string text,
            int maximumCodeUnits)
        {
            text = text ?? "";
            if (maximumCodeUnits <= 0) return "";
            if (text.Length <= maximumCodeUnits) return text;

            int length = maximumCodeUnits;
            if (length > 0 &&
                char.IsHighSurrogate(text[length - 1]) &&
                length < text.Length &&
                char.IsLowSurrogate(text[length]))
                length--;
            return text.Substring(0, length);
        }
    }

    internal struct SpriteSpeechAnchor
    {
        public double X;
        public double Top;
        public double Bottom;
    }

    /// <summary>
    /// Pure virtual-desktop helpers. WinForms positions always use virtual-screen coordinates.
    /// Animation spawn/child expressions are monitor-local and cross the boundary exactly once
    /// through <see cref="MonitorLocalToVirtual"/> or <see cref="VirtualToMonitorLocal"/>.
    /// </summary>
    internal static class DesktopGeometry
    {
        public static Point MonitorLocalToVirtual(Point local, Rectangle monitorBounds)
        {
            return new Point(
                SaturatingAdd(monitorBounds.X, local.X),
                SaturatingAdd(monitorBounds.Y, local.Y));
        }

        public static Point VirtualToMonitorLocal(Point virtualPoint, Rectangle monitorBounds)
        {
            return new Point(
                SaturatingSubtract(virtualPoint.X, monitorBounds.X),
                SaturatingSubtract(virtualPoint.Y, monitorBounds.Y));
        }

        public static Point Center(Rectangle rectangle)
        {
            return new Point(
                SaturatingAdd(rectangle.Left, rectangle.Width / 2),
                SaturatingAdd(rectangle.Top, rectangle.Height / 2));
        }

        public static bool IsFullscreenOnMonitor(Rectangle windowBounds, Rectangle monitorBounds)
        {
            if (windowBounds.Width <= 0 || windowBounds.Height <= 0 ||
                monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
                return false;

            // Fullscreen means that the foreground window covers this entire monitor. Size plus
            // center-point checks misclassify an exact-monitor-sized window shifted partly
            // offscreen, which can incorrectly drop the pet's TopMost state.
            long windowRight = (long)windowBounds.Left + windowBounds.Width;
            long windowBottom = (long)windowBounds.Top + windowBounds.Height;
            long monitorRight = (long)monitorBounds.Left + monitorBounds.Width;
            long monitorBottom = (long)monitorBounds.Top + monitorBounds.Height;
            return windowBounds.Left <= monitorBounds.Left &&
                windowBounds.Top <= monitorBounds.Top &&
                windowRight >= monitorRight &&
                windowBottom >= monitorBottom;
        }

        /// <summary>
        /// Pick a monitor to move a pet to when its own monitor is blocked by a fullscreen window.
        /// Returns the index of an unblocked monitor other than <paramref name="currentIndex"/>,
        /// preferring the one whose center is nearest. Returns -1 when the current monitor is not
        /// blocked (no move needed) or when no other unblocked monitor exists (the caller then hides
        /// the pet instead of relocating it). Ties resolve to the lower index for determinism.
        /// </summary>
        public static int ChooseRelocationTarget(
            int currentIndex, IList<Rectangle> monitors, IList<bool> blocked)
        {
            if (monitors == null || blocked == null) return -1;
            int n = monitors.Count;
            if (n == 0 || blocked.Count != n || currentIndex < 0 || currentIndex >= n)
                return -1;
            if (!blocked[currentIndex]) return -1;          // current monitor is fine

            Point from = Center(monitors[currentIndex]);
            int best = -1;
            long bestDistance = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (i == currentIndex || blocked[i]) continue;
                Point c = Center(monitors[i]);
                long dx = (long)c.X - from.X;
                long dy = (long)c.Y - from.Y;
                long distance = dx * dx + dy * dy;
                if (distance < bestDistance)        // strict: ties keep the earlier (lower) index
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Returns whether a positive, possibly fractional, downward step crosses a horizontal
        /// boundary. Keeping the movement as a double prevents a subpixel step from being rounded
        /// to zero before window-top collision detection.
        /// </summary>
        public static bool CrossesDescendingBoundary(
            double currentBottom,
            double deltaY,
            int boundaryTop)
        {
            if (double.IsNaN(currentBottom) ||
                double.IsInfinity(currentBottom) ||
                double.IsNaN(deltaY) ||
                double.IsInfinity(deltaY) ||
                deltaY <= 0)
                return false;
            return currentBottom < boundaryTop &&
                currentBottom + deltaY >= boundaryTop;
        }

        /// <summary>
        /// The mirror of <see cref="CrossesDescendingBoundary"/>: whether a negative, possibly
        /// fractional, UPWARD step carries a rising pet's head through a horizontal boundary. Used to
        /// catch the underside of a window a jumping pet passes into.
        ///
        /// Written out rather than expressed as CrossesDescendingBoundary with the signs flipped,
        /// because the two are not symmetric at the boundary: descending lands ON the boundary (>=),
        /// which is a pet coming to rest on a surface, while ascending must pass THROUGH it (&lt;=)
        /// to have gone under a window rather than merely touched it.
        ///
        /// The <c>deltaY &gt;= 0</c> early-out is redundant and kept deliberately, alongside the same
        /// idiom in CrossesDescendingBoundary: a non-negative step cannot satisfy the pair of inequalities
        /// below (a top already past the boundary only gets further past it), so removing it changes no
        /// answer. Verified by negative-testing rather than assumed -- the mutation survives, which is the
        /// evidence that this line is documentation of intent and not a guard.
        /// </summary>
        public static bool CrossesAscendingBoundary(
            double currentTop,
            double deltaY,
            int boundaryBottom)
        {
            if (double.IsNaN(currentTop) ||
                double.IsInfinity(currentTop) ||
                double.IsNaN(deltaY) ||
                double.IsInfinity(deltaY) ||
                deltaY >= 0)
                return false;
            return currentTop > boundaryBottom &&
                currentTop + deltaY <= boundaryBottom;
        }

        /// <summary>
        /// Preserve a pet's relative horizontal position while its supporting window resizes.
        /// Invalid or collapsed rectangles are rejected so a transient zero-width window cannot
        /// become the divisor on the next animation tick.
        /// </summary>
        public static bool TryScaleWindowRelativeX(
            int petLeft,
            int previousWindowLeft,
            int previousWindowRight,
            int currentWindowLeft,
            int currentWindowRight,
            out int scaledLeft)
        {
            scaledLeft = petLeft;
            long previousWidth =
                (long)previousWindowRight - previousWindowLeft;
            long currentWidth =
                (long)currentWindowRight - currentWindowLeft;
            if (previousWidth <= 0 || currentWidth <= 0)
                return false;

            decimal scaledOffset =
                (decimal)((long)petLeft - previousWindowLeft) *
                currentWidth /
                previousWidth;
            decimal candidate = currentWindowLeft + decimal.Truncate(
                scaledOffset);
            if (candidate > int.MaxValue) scaledLeft = int.MaxValue;
            else if (candidate < int.MinValue) scaledLeft = int.MinValue;
            else scaledLeft = (int)candidate;
            return true;
        }

        public static SpriteSpeechAnchor GetSpriteSpeechAnchor(
            double spriteLeft,
            double spriteTop,
            int fullWidth,
            int fullHeight,
            bool faceLeft)
        {
            fullWidth = Math.Max(1, fullWidth);
            fullHeight = Math.Max(1, fullHeight);
            double mouthOffset = faceLeft
                ? fullWidth / 3.0
                : fullWidth * 2.0 / 3.0;
            return new SpriteSpeechAnchor
            {
                X = spriteLeft + mouthOffset,
                Top = spriteTop,
                Bottom = spriteTop + fullHeight
            };
        }

        /// <summary>
        /// Select the single monitor whose content should accompany foreground-window context.
        /// The monitor with the largest foreground-window overlap wins; an off-screen window uses
        /// the nearest monitor, and missing foreground geometry falls back to the pet's monitor.
        /// </summary>
        public static Rectangle SelectCaptureMonitor(
            Rectangle foregroundWindowBounds,
            Rectangle fallbackMonitorBounds,
            IList<Rectangle> monitorBounds)
        {
            if (monitorBounds == null || monitorBounds.Count == 0)
                return fallbackMonitorBounds;

            Rectangle target =
                foregroundWindowBounds.Width > 0 &&
                foregroundWindowBounds.Height > 0
                    ? foregroundWindowBounds
                    : fallbackMonitorBounds;
            int bestIndex = -1;
            long bestArea = -1;
            for (int index = 0; index < monitorBounds.Count; index++)
            {
                Rectangle monitor = monitorBounds[index];
                if (monitor.Width <= 0 || monitor.Height <= 0) continue;
                long area = IntersectionArea(target, monitor);
                if (area > bestArea ||
                    (area == bestArea &&
                     SameBounds(monitor, fallbackMonitorBounds)))
                {
                    bestIndex = index;
                    bestArea = area;
                }
            }
            if (bestIndex < 0) return fallbackMonitorBounds;
            if (bestArea > 0) return monitorBounds[bestIndex];

            double targetCenterX =
                (double)target.Left + target.Width / 2.0;
            double targetCenterY =
                (double)target.Top + target.Height / 2.0;
            double bestDistance = double.PositiveInfinity;
            for (int index = 0; index < monitorBounds.Count; index++)
            {
                Rectangle monitor = monitorBounds[index];
                if (monitor.Width <= 0 || monitor.Height <= 0) continue;
                double monitorRight = (double)monitor.Left + monitor.Width;
                double monitorBottom = (double)monitor.Top + monitor.Height;
                double nearestX = Math.Max(
                    monitor.Left,
                    Math.Min(targetCenterX, monitorRight));
                double nearestY = Math.Max(
                    monitor.Top,
                    Math.Min(targetCenterY, monitorBottom));
                double deltaX = targetCenterX - nearestX;
                double deltaY = targetCenterY - nearestY;
                double distance = deltaX * deltaX + deltaY * deltaY;
                if (distance < bestDistance ||
                    (distance == bestDistance &&
                     SameBounds(monitor, fallbackMonitorBounds)))
                {
                    bestIndex = index;
                    bestDistance = distance;
                }
            }
            return bestIndex >= 0
                ? monitorBounds[bestIndex]
                : fallbackMonitorBounds;
        }

        public static ScreenMetrics Metrics(Rectangle monitorBounds, Rectangle workArea)
        {
            return new ScreenMetrics
            {
                ScreenWidth = Math.Max(0, monitorBounds.Width),
                ScreenHeight = Math.Max(0, monitorBounds.Height),
                WorkAreaWidth = Math.Max(0, workArea.Width),
                WorkAreaHeight = Math.Max(0, workArea.Height)
            };
        }

        private static long IntersectionArea(Rectangle left, Rectangle right)
        {
            if (left.Width <= 0 || left.Height <= 0 ||
                right.Width <= 0 || right.Height <= 0)
                return 0;
            long intersectionLeft = Math.Max(
                (long)left.Left,
                (long)right.Left);
            long intersectionTop = Math.Max(
                (long)left.Top,
                (long)right.Top);
            long intersectionRight = Math.Min(
                (long)left.Left + left.Width,
                (long)right.Left + right.Width);
            long intersectionBottom = Math.Min(
                (long)left.Top + left.Height,
                (long)right.Top + right.Height);
            long width = Math.Max(0L, intersectionRight - intersectionLeft);
            long height = Math.Max(0L, intersectionBottom - intersectionTop);
            if (width == 0 || height == 0) return 0;
            if (width > long.MaxValue / height) return long.MaxValue;
            return width * height;
        }

        private static bool SameBounds(Rectangle left, Rectangle right)
        {
            return left.X == right.X &&
                left.Y == right.Y &&
                left.Width == right.Width &&
                left.Height == right.Height;
        }

        private static int SaturatingAdd(int left, int right)
        {
            long value = (long)left + right;
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        private static int SaturatingSubtract(int left, int right)
        {
            long value = (long)left - right;
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }
    }
}
