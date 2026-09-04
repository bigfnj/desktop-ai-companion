using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopAICompanion.PetStudioModule
{
    /// <summary>
    /// A decoded pet sprite sheet: the base64 PNG cut into a TilesX×TilesY grid with the pet's transparency
    /// colour keyed out to alpha, so an animation's frames can be shown the way the pet actually renders them.
    /// Decoded once per analysis and cached; Frame(i) hands back a cropped, frozen tile ready to bind to an
    /// Image. All failure is swallowed into a null return — the window falls back to "no preview".
    /// </summary>
    internal sealed class PetSprite
    {
        private readonly BitmapSource _sheet;
        private readonly int _tileW, _tileH, _cols, _rows;
        private readonly Dictionary<int, bool> _blankCache = new Dictionary<int, bool>();

        private PetSprite(BitmapSource sheet, int tileW, int tileH, int cols, int rows)
        {
            _sheet = sheet; _tileW = tileW; _tileH = tileH; _cols = cols; _rows = rows;
        }

        internal int TileWidth { get { return _tileW; } }
        internal int TileHeight { get { return _tileH; } }

        internal static PetSprite TryDecode(string base64Png, int tilesX, int tilesY, string transparency)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(base64Png) || tilesX <= 0 || tilesY <= 0) return null;
                byte[] bytes = Convert.FromBase64String(base64Png.Trim());

                BitmapSource src;
                using (var ms = new MemoryStream(bytes))
                {
                    var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    src = decoder.Frames[0];
                }

                var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                BitmapSource keyed = ApplyColorKey(bgra, transparency);

                int tw = keyed.PixelWidth / tilesX;
                int th = keyed.PixelHeight / tilesY;
                if (tw <= 0 || th <= 0) return null;
                return new PetSprite(keyed, tw, th, tilesX, tilesY);
            }
            catch { return null; }
        }

        /// <summary>Zero the alpha of every pixel matching the pet's transparency colour. Exact-match keying,
        /// like the engine — anti-aliased edges keep a faint halo, acceptable for a preview. src is already
        /// Bgra32, so its pixels are read directly rather than copied through a throwaway WriteableBitmap.</summary>
        private static BitmapSource ApplyColorKey(BitmapSource src, string transparency)
        {
            byte kr, kg, kb;
            if (!TryParseColor(transparency, out kr, out kg, out kb))
            {
                if (src.CanFreeze) src.Freeze();
                return src;
            }

            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            byte[] px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);
            for (int i = 0; i + 3 < px.Length; i += 4)   // Bgra32: B, G, R, A
                if (px[i] == kb && px[i + 1] == kg && px[i + 2] == kr) px[i + 3] = 0;

            var outBmp = new WriteableBitmap(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null);
            outBmp.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
            outBmp.Freeze();
            return outBmp;
        }

        private static bool TryParseColor(string name, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                System.Drawing.Color c = System.Drawing.ColorTranslator.FromHtml(name.Trim());
                r = c.R; g = c.G; b = c.B;
                return true;
            }
            catch { return false; }
        }

        /// <summary>True when frame index i renders a fully transparent tile — a blank sheet cell the pet uses
        /// to be invisible during a state (e.g. a spawn-start). Cached, since tiles repeat across animations, so
        /// repeated selections do not re-scan the same tile's pixels.</summary>
        internal bool IsBlank(int index)
        {
            bool blank;
            if (_blankCache.TryGetValue(index, out blank)) return blank;
            BitmapSource f = Frame(index);
            blank = f == null || IsAllTransparent(f);
            _blankCache[index] = blank;
            return blank;
        }

        private static bool IsAllTransparent(BitmapSource frame)
        {
            int stride = frame.PixelWidth * 4;   // cropped from the Bgra32 sheet, so 4 bytes/pixel
            byte[] px = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(px, stride, 0);
            for (int i = 3; i < px.Length; i += 4)
                if (px[i] != 0) return false;
            return true;
        }

        /// <summary>The tile at frame index i (row-major over the grid), or null when it falls outside it.</summary>
        internal BitmapSource Frame(int index)
        {
            if (index < 0) return null;
            int col = index % _cols;
            int row = index / _cols;
            if (row >= _rows) return null;
            try
            {
                var cropped = new CroppedBitmap(_sheet, new Int32Rect(col * _tileW, row * _tileH, _tileW, _tileH));
                cropped.Freeze();
                return cropped;
            }
            catch { return null; }
        }
    }
}
