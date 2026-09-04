using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>Captures the full virtual screen (all monitors) to a PNG, in-process. Covered by the module's
    /// declared ScreenContext permission. Best-effort: returns false rather than throwing.</summary>
    internal static class ScreenSnapshot
    {
        public static bool Capture(string pngPath)
        {
            try
            {
                Rectangle b = SystemInformation.VirtualScreen;
                if (b.Width <= 0 || b.Height <= 0) b = Screen.PrimaryScreen.Bounds;
                using (var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                    bmp.Save(pngPath, ImageFormat.Png);
                }
                return true;
            }
            catch { return false; }
        }
    }
}
