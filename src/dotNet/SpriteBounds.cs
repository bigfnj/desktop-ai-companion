using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DesktopAICompanion
{
    /// <summary>
    /// The tight bounding box of a sprite frame's VISIBLE pixels, so the speech bubble can anchor over the
    /// actual character rather than the whole frame. The built-in pets fill their frame, but a converted
    /// shimeji floats inside a larger transparent/colour-keyed cell (poses are padded to the largest pose's
    /// box), so anchoring to the frame rectangle put the bubble out over empty padding. Handles both
    /// transparency modes: a colour-keyed frame (visible = not the key colour) and an alpha frame (visible =
    /// alpha above a small threshold, used when the key is <see cref="Color.Empty"/>). The result is cached per
    /// frame image with a <see cref="ConditionalWeakTable"/>, so a walking pet does not rescan every tick and
    /// retired frames are collected with no manual eviction.
    /// </summary>
    internal static class SpriteBounds
    {
        private const int AlphaThreshold = 16;

        private static readonly ConditionalWeakTable<Image, object> Cache =
            new ConditionalWeakTable<Image, object>();

        /// <summary>Visible bounds in the image's own pixel coordinates. Returns the full frame when nothing is
        /// visible or the image can't be scanned, so the caller always gets a usable box.</summary>
        public static Rectangle VisibleBounds(Image image, Color transparencyKey)
        {
            if (image == null) return Rectangle.Empty;
            object cached;
            if (Cache.TryGetValue(image, out cached)) return (Rectangle)cached;
            Rectangle box = Compute(image, transparencyKey);
            try { Cache.Add(image, box); } catch { /* raced with another add; the value is identical */ }
            return box;
        }

        private static Rectangle Compute(Image image, Color transparencyKey)
        {
            var bmp = image as Bitmap;
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
                return new Rectangle(0, 0, Math.Max(1, image.Width), Math.Max(1, image.Height));

            bool alphaMode = transparencyKey.ToArgb() == Color.Empty.ToArgb();
            BitmapData data = null;
            try
            {
                data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = data.Stride, w = bmp.Width, h = bmp.Height;
                byte[] buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                byte kr = transparencyKey.R, kg = transparencyKey.G, kb = transparencyKey.B;

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;   // 32bppArgb is B,G,R,A in memory
                        byte b = buf[i], g = buf[i + 1], r = buf[i + 2], a = buf[i + 3];
                        bool visible = alphaMode ? (a > AlphaThreshold) : (r != kr || g != kg || b != kb);
                        if (!visible) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < minX || maxY < minY) return new Rectangle(0, 0, w, h);   // nothing visible -> full frame
                return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            }
            catch { return new Rectangle(0, 0, bmp.Width, bmp.Height); }
            finally { if (data != null) { try { bmp.UnlockBits(data); } catch { } } }
        }

        internal static bool SelfTest(out string detail)
        {
            using (var bmp = new Bitmap(20, 20, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Magenta);
                    using (var br = new SolidBrush(Color.Blue)) g.FillRectangle(br, 5, 6, 8, 7);
                }
                Rectangle r = VisibleBounds(bmp, Color.Magenta);
                if (r != new Rectangle(5, 6, 8, 7)) { detail = "colour-key bounds wrong: " + r; return false; }
            }
            using (var bmp = new Bitmap(20, 20, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (var br = new SolidBrush(Color.FromArgb(255, 10, 200, 30))) g.FillRectangle(br, 3, 4, 6, 9);
                }
                Rectangle r = VisibleBounds(bmp, Color.Empty);
                if (r != new Rectangle(3, 4, 6, 9)) { detail = "alpha bounds wrong: " + r; return false; }
            }
            detail = "sprite visible-bounds: colour-key + alpha both tight to the drawn rect";
            return true;
        }
    }
}
