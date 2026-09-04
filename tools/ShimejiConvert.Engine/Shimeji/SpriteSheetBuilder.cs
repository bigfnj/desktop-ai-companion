using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>The composited sprite sheet plus the mapping every pose needs to find its tile.</summary>
    public sealed class SpriteSheet
    {
        public byte[] PngBytes;
        public string Base64Png;
        public int TilesX;
        public int TilesY;
        public int CellWidth;
        public int CellHeight;
        public double Scale;             // uniform scale applied to fit the caps/budget (1.0 = none)
        public bool IsAlpha;             // true = real alpha channel preserved (no magenta key); host renders per-pixel
        public int ProjectedXmlBytes;    // base64 length + a fixed markup allowance
        public readonly Dictionary<string, int> FrameIndexByKey =
            new Dictionary<string, int>(StringComparer.Ordinal); // ShimejiPose.FrameKey -> row-major tile index
    }

    /// <summary>
    /// Composites a Shimeji skin's individual pose PNGs into ONE equal-cell sprite sheet in the exact shape
    /// the desktopPet engine slices (Xml.ReadImages): tilesx * tilesy equal cells, row-major 0-based indices,
    /// magenta (#FF00FF) as the transparency KEY -- the engine keys on colour, not alpha (FormCompanion.Designer.cs).
    ///
    /// Two things it must get right, both baked into pixels because animations.xml cannot express them:
    ///   * Anchor alignment. A Shimeji pose has an ImageAnchor hotspot (x,y) that stays fixed as frames change;
    ///     desktopPet has no per-frame anchor. So every frame is placed so its anchor lands at the SAME point
    ///     in the cell -- this bakes the x-offset the format's y-only &lt;offsety&gt; cannot carry.
    ///   * Transparency. Alpha is hard-thresholded onto magenta (below the cutoff -> keyed, at/above -> opaque).
    ///     A hard cutoff avoids the magenta halo a blend would leave; the cost is that anti-aliased edges go
    ///     jagged. Genuinely-magenta art pixels are nudged to (254,0,255) so they are not keyed out.
    ///
    /// The caps come straight from CompanionXmlValidator: cells <= 256 px, <= 1024 tiles, a 4096 px sheet, and the
    /// whole XML (which is mostly this sheet's base64) <= 12 MiB. It downscales uniformly to fit and fails
    /// loudly if it cannot.
    /// </summary>
    public static class SpriteSheetBuilder
    {
        public const int MaxCell = 256;                       // CompanionXmlValidator.MaximumSpriteFrameDimension
        public const int MaxTiles = 1024;                     // SpriteFrameStore.MaximumFrames
        public const int MaxSheetDimension = 4096;            // CompanionXmlValidator.MaximumImageDimension
        public const int XmlBudgetBytes = 12 * 1024 * 1024;   // CompanionXmlValidator.MaximumXmlBytes (raised from 4:
                                                              // lets a frame-heavy skin fill the 4096 sheet up to
                                                              // the 256px cell cap instead of being squeezed under)
        public const int MarkupAllowanceBytes = 256 * 1024;   // header/icon/animations markup + headroom
        public const int AlphaThreshold = 128;                // >= is opaque, < is keyed to magenta

        private const int MinCell = 8;                        // refuse to shrink a sheet into mush

        private static readonly int KeyB = 255, KeyG = 0, KeyR = 255; // magenta in RGB

        /// <summary>A loader that reads a pose image name (e.g. "/shime1.png") from a skin's img directory.</summary>
        public static Func<string, Bitmap> FileLoader(string imgDir)
        {
            return delegate(string image)
            {
                string name = (image ?? "").TrimStart('/', '\\');
                string path = Path.Combine(imgDir, name);
                if (!File.Exists(path)) throw new FileNotFoundException("Sprite not found: " + path);
                using (var raw = new Bitmap(path))
                    return new Bitmap(raw); // detach from the file handle
            };
        }

        public static bool Build(IList<ShimejiPose> poses, Func<string, Bitmap> load, bool alpha,
            out SpriteSheet sheet, out string error)
        {
            sheet = null;
            error = null;

            // 1. distinct frames by (image, anchor), first-seen order.
            var frames = new List<ShimejiPose>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShimejiPose p in poses)
            {
                if (p == null || string.IsNullOrEmpty(p.Image)) continue;
                if (seen.Add(p.FrameKey)) frames.Add(p);
            }
            if (frames.Count == 0) { error = "the skin has no sprite poses to composite."; return false; }
            if (frames.Count > MaxTiles)
            {
                error = string.Format("{0} distinct frames exceeds the {1}-tile limit.", frames.Count, MaxTiles);
                return false;
            }

            // 2. load images once, keyed by name.
            var images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
            try
            {
                foreach (ShimejiPose f in frames)
                {
                    if (images.ContainsKey(f.Image)) continue;
                    Bitmap bmp;
                    try { bmp = load(f.Image); }
                    catch (Exception ex) { error = "could not load sprite '" + f.Image + "': " + ex.Message; return false; }
                    if (bmp == null) { error = "sprite loader returned null for '" + f.Image + "'."; return false; }
                    images[f.Image] = bmp;
                }

                // 2b. collapse frames whose CELL would be byte-identical.
                //
                // Step 1 deduped by FrameKey, which is the image NAME plus the anchor -- so a skin that
                // ships the same picture under two filenames got two cells. Both causes are real: an
                // Android-Shimeji template that duplicates sprite files, and a reversed sequence (one skin's
                // `descend` is its `climb` frame-for-frame backwards) which needs no new cells at all,
                // because <sequence> already takes an arbitrary frame list. Across the 31 shipped converted
                // pets that was 559 wasted cells and 20% of their XML.
                //
                // What makes two cells identical is the source PIXELS plus everything that decides where
                // they land in the cell: the anchor, and whether the pose hangs from the cell top. Scale is
                // uniform across the sheet, so it cannot separate two otherwise-identical frames.
                var aliasToOwnerKey = new Dictionary<string, string>(StringComparer.Ordinal);
                {
                    var ownerByContent = new Dictionary<string, string>(StringComparer.Ordinal);
                    var deduped = new List<ShimejiPose>(frames.Count);
                    var imageHash = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (ShimejiPose f in frames)
                    {
                        string ih;
                        if (!imageHash.TryGetValue(f.Image, out ih))
                        {
                            ih = BitmapContentHash(images[f.Image]);
                            imageHash[f.Image] = ih;
                        }
                        string content = ih + "|" + f.AnchorX + "," + f.AnchorY + "," + (f.AnchorToTop ? 1 : 0);
                        string ownerKey;
                        if (ownerByContent.TryGetValue(content, out ownerKey))
                        {
                            aliasToOwnerKey[f.FrameKey] = ownerKey;   // same picture, same placement
                            continue;
                        }
                        ownerByContent[content] = f.FrameKey;
                        deduped.Add(f);
                    }
                    frames = deduped;
                }

                // 3. anchor-aligned unscaled cell size. Every frame's anchor maps to O=(Ox,Oy); the cell must
                //    hold the widest left/right extents any frame has around its anchor.
                //
                //    VERTICALLY the anchor is put on the cell's BOTTOM EDGE, not somewhere inside it. The
                //    Shimeji ImageAnchor is the mascot's ground-contact point, and the host stands a pet by
                //    putting its WINDOW's bottom edge on the floor -- and the window is one cell. Reserving
                //    `below` pixels underneath the anchor therefore lifted every pet off the ground by that
                //    much: Hornet's standing frame sat 14px clear of the taskbar while a hand-authored sheep
                //    (whose tight cell has no such gap) stood on it correctly. Anything a source frame draws
                //    BELOW its own anchor is below the floor line, so dropping it is also what the original
                //    means. Reclaiming that band makes the sheet smaller too.
                int ox = 0, right = 0, oy = 0;
                foreach (ShimejiPose f in frames)
                {
                    Bitmap b = images[f.Image];
                    ox = Math.Max(ox, f.AnchorX);
                    right = Math.Max(right, b.Width - f.AnchorX);
                    oy = Math.Max(oy, f.AnchorY);
                }
                int cellW = Math.Max(1, ox + right);
                int cellH = Math.Max(1, oy);

                int n = frames.Count;
                int tilesX = (int)Math.Ceiling(Math.Sqrt(n));
                int tilesY = (int)Math.Ceiling((double)n / tilesX);

                // initial scale: satisfy the 256 px cell cap and the 4096 px sheet cap.
                double scale = Math.Min(1.0, Math.Min((double)MaxCell / cellW, (double)MaxCell / cellH));
                scale = ClampSheet(scale, cellW, cellH, tilesX, tilesY);

                // 4. compose, encode, and shrink until the base64 fits the XML budget.
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    int scaledCellW = Math.Max(1, (int)Math.Round(cellW * scale));
                    int scaledCellH = Math.Max(1, (int)Math.Round(cellH * scale));

                    SpriteSheet built = Compose(frames, images, ox, oy, scale, scaledCellW, scaledCellH, tilesX, tilesY, alpha);
                    int projected = built.Base64Png.Length + MarkupAllowanceBytes;
                    if (projected <= XmlBudgetBytes)
                    {
                        // Every pose that was collapsed in 2b still has to resolve to a tile, or the caller
                        // would look up its FrameKey, miss, and emit an animation with no frames.
                        foreach (var alias in aliasToOwnerKey)
                        {
                            int owner;
                            if (built.FrameIndexByKey.TryGetValue(alias.Value, out owner))
                                built.FrameIndexByKey[alias.Key] = owner;
                        }
                        built.ProjectedXmlBytes = projected;
                        sheet = built;
                        return true;
                    }
                    if (scaledCellW <= MinCell || scaledCellH <= MinCell)
                    {
                        error = string.Format(
                            "skin is too large: {0} frames project to {1:N0} bytes of XML, over the {2:N0} limit, " +
                            "even at the minimum cell size. Reduce the sprite count or resolution.",
                            n, projected, XmlBudgetBytes);
                        return false;
                    }
                    scale *= 0.85;
                }
                error = "could not fit the sheet within the XML budget after downscaling.";
                return false;
            }
            finally
            {
                foreach (Bitmap b in images.Values) b.Dispose();
            }
        }

        private static double ClampSheet(double scale, int cellW, int cellH, int tilesX, int tilesY)
        {
            double sheetW = tilesX * cellW * scale;
            double sheetH = tilesY * cellH * scale;
            if (sheetW > MaxSheetDimension) scale *= MaxSheetDimension / sheetW;
            if (sheetH > MaxSheetDimension) scale *= MaxSheetDimension / sheetH;
            return scale;
        }

        private static SpriteSheet Compose(List<ShimejiPose> frames, Dictionary<string, Bitmap> images,
            int ox, int oy, double scale, int scaledCellW, int scaledCellH, int tilesX, int tilesY, bool alpha)
        {
            var result = new SpriteSheet
            {
                TilesX = tilesX,
                TilesY = tilesY,
                CellWidth = scaledCellW,
                CellHeight = scaledCellH,
                Scale = scale,
                IsAlpha = alpha,
            };

            int sheetW = tilesX * scaledCellW;
            int sheetH = tilesY * scaledCellH;

            using (var sheet = new Bitmap(sheetW, sheetH, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(sheet))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.Clear(alpha
                        ? Color.FromArgb(0, 0, 0, 0)                  // transparent background (real alpha preserved)
                        : Color.FromArgb(255, KeyR, KeyG, KeyB));     // magenta background = the transparency key
                }

                for (int i = 0; i < frames.Count; i++)
                {
                    ShimejiPose f = frames[i];
                    Bitmap src = images[f.Image];
                    int col = i % tilesX;
                    int row = i / tilesX;
                    result.FrameIndexByKey[f.FrameKey] = i;

                    int sw = Math.Max(1, (int)Math.Round(src.Width * scale));
                    int sh = Math.Max(1, (int)Math.Round(src.Height * scale));

                    using (var scaled = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(scaled))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.FromArgb(0, 0, 0, 0));
                            g.DrawImage(src, new Rectangle(0, 0, sw, sh));
                        }
                        if (!alpha) KeyToMagenta(scaled);            // alpha mode keeps the sprite's real anti-aliased edges

                        int offsetX = (int)Math.Round((ox - f.AnchorX) * scale);
                        int offsetY;
                        int srcY;
                        if (f.AnchorToTop)
                        {
                            // Ceiling pose: the contact point goes on the cell's TOP edge, because the engine
                            // pins the window's TOP to the screen top at a horizontal border.
                            //
                            // Where that contact point IS depends on the source. A desktop Shimeji ceiling
                            // pose anchors at 64,48 -- near the top, because that is where the mascot grips --
                            // so the 48px above it is inside the ceiling and gets skipped. An Android bundle
                            // anchors EVERY pose bottom-centre, so its "anchor" is the sprite's bottom edge and
                            // carries no ceiling meaning at all; skipping AnchorY rows there skipped the whole
                            // image and emitted a blank tile (shipped in 1.9.4 and caught on Kopo).
                            //
                            // So only treat the anchor as a ceiling contact point when it actually sits in the
                            // top half. Otherwise align the sprite's own top to the cell top and skip nothing.
                            offsetY = 0;
                            int anchorFromTop = (int)Math.Round(f.AnchorY * scale);
                            srcY = (f.AnchorY * 2 <= src.Height) ? anchorFromTop : 0;
                            // Belt and braces: whatever the arithmetic, never skip so far that the tile comes
                            // out empty. A blank tile is invisible at conversion time and only shows up as a
                            // pet that vanishes mid-animation.
                            if (srcY >= sh) srcY = 0;
                        }
                        else
                        {
                            offsetY = (int)Math.Round((oy - f.AnchorY) * scale);
                            srcY = 0;
                        }
                        int placeX = col * scaledCellW + offsetX;
                        int placeY = row * scaledCellH + offsetY;
                        // Room left inside THIS tile. The cell now ends at the anchor, so a frame with pixels
                        // below its own anchor is taller than its tile and must be clipped, or it bleeds into
                        // the neighbouring frame.
                        BlitOpaque(sheet, scaled, placeX, placeY, scaledCellW - offsetX, scaledCellH - offsetY, 0, srcY);
                    }
                }

                using (var ms = new MemoryStream())
                {
                    sheet.Save(ms, ImageFormat.Png);
                    result.PngBytes = ms.ToArray();
                }
            }
            result.Base64Png = Convert.ToBase64String(result.PngBytes);
            return result;
        }

        /// <summary>SHA-256 of a bitmap's pixels, read row by row through a fixed 32bppArgb view so neither
        /// stride padding nor the source's own pixel format can make two identical pictures hash apart.</summary>
        private static string BitmapContentHash(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    // Dimensions go in first: two bitmaps can otherwise agree on every row they share.
                    byte[] dims = System.Text.Encoding.ASCII.GetBytes(bmp.Width + "x" + bmp.Height + ":");
                    sha.TransformBlock(dims, 0, dims.Length, null, 0);
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            data.Scan0 + y * data.Stride, row, 0, row.Length);
                        sha.TransformBlock(row, 0, row.Length, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return BitConverter.ToString(sha.Hash);
                }
            }
            finally { bmp.UnlockBits(data); }
        }

        // Hard-threshold alpha onto magenta, in place, on a Format32bppArgb bitmap.
        private static void KeyToMagenta(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(data.Stride) * bmp.Height;
                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);
                for (int y = 0; y < bmp.Height; y++)
                {
                    int rowStart = y * data.Stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = rowStart + x * 4; // BGRA
                        byte a = buf[i + 3];
                        if (a < AlphaThreshold)
                        {
                            buf[i + 0] = (byte)KeyB; buf[i + 1] = (byte)KeyG; buf[i + 2] = (byte)KeyR; buf[i + 3] = 255;
                        }
                        else
                        {
                            // opaque; avoid a genuine magenta art pixel being keyed out.
                            if (buf[i + 0] == 255 && buf[i + 1] == 0 && buf[i + 2] == 255) buf[i + 2] = 254;
                            buf[i + 3] = 255;
                        }
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, bytes);
            }
            finally { bmp.UnlockBits(data); }
        }

        // Copy every pixel of an opaque source onto the sheet at (dx,dy), clipped to the sheet bounds.
        /// <summary>
        /// Draw one frame into its tile, CLIPPED to that tile.
        ///
        /// The clip is not defensive tidiness, it is required. Now that the cell ends at the anchor rather
        /// than reserving a band beneath it, a frame with pixels below its own anchor is taller than its tile
        /// and would spill into the neighbouring one -- which is exactly what happened: a black blob from the
        /// frame above appeared in the corner of Hornet's drag frame. Clipping keeps the overflow (which is
        /// below the floor line anyway) out of the sheet.
        /// </summary>
        /// <param name="roomW">Pixels left in the tile to the right of dx.</param>
        /// <param name="roomH">Pixels left in the tile below dy.</param>
        private static void BlitOpaque(Bitmap sheet, Bitmap src, int dx, int dy, int roomW, int roomH)
        {
            BlitOpaque(sheet, src, dx, dy, roomW, roomH, 0, 0);
        }

        // srcX/srcY skip that much of the SOURCE, which is how a top-anchored (ceiling) frame drops the band
        // above its anchor instead of drawing it up into the neighbouring tile.
        private static void BlitOpaque(Bitmap sheet, Bitmap src, int dx, int dy, int roomW, int roomH, int srcX, int srcY)
        {
            if (srcX < 0) srcX = 0;
            if (srcY < 0) srcY = 0;
            int clipW = Math.Min(src.Width - srcX, roomW);
            int clipH = Math.Min(src.Height - srcY, roomH);
            if (clipW <= 0 || clipH <= 0) return;

            using (var g = Graphics.FromImage(sheet))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(dx, dy, clipW, clipH),
                    srcX, srcY, clipW, clipH, GraphicsUnit.Pixel);
            }
        }
    }
}
