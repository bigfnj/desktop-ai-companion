using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace DesktopAICompanion.Tools.ShimejiConvert.Emit
{
    /// <summary>
    /// Builds the 48x48 ICO the pet header requires. CompanionXmlValidator wants a REAL icon container (magic
    /// 00 00 01 00, not a bare PNG) whose directory entry's declared dimensions match the embedded image.
    /// We wrap a 48x48 PNG (alpha preserved -- the icon is NOT magenta-keyed, unlike the sprite sheet) in a
    /// one-entry ICO directory, which is a valid PNG-compressed icon.
    /// </summary>
    public static class IconBuilder
    {
        public const int Size = 48;

        public static byte[] BuildIco(Bitmap source)
        {
            byte[] png = RenderPng48(source);

            using (var ms = new MemoryStream())
            {
                // ICONDIR
                WriteU16(ms, 0); // reserved
                WriteU16(ms, 1); // type = icon
                WriteU16(ms, 1); // count
                // ICONDIRENTRY
                ms.WriteByte(Size);          // width
                ms.WriteByte(Size);          // height
                ms.WriteByte(0);             // palette colour count (0 = no palette)
                ms.WriteByte(0);             // reserved
                WriteU16(ms, 1);             // colour planes
                WriteU16(ms, 32);            // bits per pixel
                WriteU32(ms, (uint)png.Length); // bytes of image data
                WriteU32(ms, 22);            // offset of image data (6 + 16)
                ms.Write(png, 0, png.Length);
                return ms.ToArray();
            }
        }

        private static byte[] RenderPng48(Bitmap source)
        {
            using (var icon = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(icon))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.Clear(Color.FromArgb(0, 0, 0, 0)); // transparent
                    if (source != null)
                    {
                        g.CompositingMode = CompositingMode.SourceOver;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        // Fit the source into 48x48 preserving aspect, centred.
                        double scale = Math.Min((double)Size / source.Width, (double)Size / source.Height);
                        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
                        int x = (Size - w) / 2;
                        int y = (Size - h) / 2;
                        g.DrawImage(source, new Rectangle(x, y, w, h));
                    }
                }
                using (var ms = new MemoryStream())
                {
                    icon.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)((v >> 8) & 0xFF)); }
        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }
    }
}
