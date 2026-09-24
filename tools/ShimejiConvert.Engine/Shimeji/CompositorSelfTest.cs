using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free test of the sprite compositor on SYNTHETIC images (solid rectangles), so the gate
    /// exercises the real compositing path without any copyrighted skin art. It proves: equal cells within the
    /// 256 px cap, uniform downscale when a frame is oversized, alpha hard-thresholded onto magenta, genuine
    /// magenta art nudged off the key, and the sheet fitting the XML budget. Compositing a REAL skin is the
    /// dev command `ShimejiConvert composite &lt;conf&gt; &lt;img&gt; &lt;out.png&gt;`.
    /// </summary>
    public static class CompositorSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            // Four synthetic frames. "big" is 400x300 so it forces a downscale below the 256 px cap.
            var owned = new Dictionary<string, Bitmap>(StringComparer.Ordinal)
            {
                { "red",        Solid(40, 60, Color.FromArgb(255, 255, 0, 0)) },
                { "magentaArt", Solid(20, 20, Color.FromArgb(255, 255, 0, 255)) },
                { "transp",     Transparent(30, 30) },
                { "big",        Solid(400, 300, Color.FromArgb(255, 0, 255, 0)) },
            };
            var poses = new List<ShimejiPose>
            {
                new ShimejiPose { Image = "red",        AnchorX = 20,  AnchorY = 60 },
                new ShimejiPose { Image = "magentaArt", AnchorX = 10,  AnchorY = 20 },
                new ShimejiPose { Image = "transp",     AnchorX = 15,  AnchorY = 30 },
                new ShimejiPose { Image = "big",        AnchorX = 200, AnchorY = 300 },
            };

            try
            {
                Func<string, Bitmap> load = delegate(string name) { return new Bitmap(owned[name]); };

                SpriteSheet sheet;
                string error;
                bool ok = SpriteSheetBuilder.Build(poses, load, false, out sheet, out error);
                if (!ok) { detail = "compositor self-test: Build failed -- " + error; return false; }

                if (sheet.CellWidth > SpriteSheetBuilder.MaxCell || sheet.CellHeight > SpriteSheetBuilder.MaxCell)
                    failures.Add(string.Format("cell {0}x{1} exceeds the {2} px cap", sheet.CellWidth, sheet.CellHeight, SpriteSheetBuilder.MaxCell));
                if (sheet.Scale >= 1.0)
                    failures.Add("expected a downscale (Scale < 1) because a 400x300 frame is present, got " + sheet.Scale);
                if (sheet.FrameIndexByKey.Count != 4)
                    failures.Add("expected 4 distinct frames, got " + sheet.FrameIndexByKey.Count);
                if (sheet.TilesX * sheet.TilesY < 4)
                    failures.Add(string.Format("grid {0}x{1} cannot hold 4 frames", sheet.TilesX, sheet.TilesY));
                if (sheet.ProjectedXmlBytes >= SpriteSheetBuilder.XmlBudgetBytes)
                    failures.Add("projected XML " + sheet.ProjectedXmlBytes + " exceeds the budget");
                if (sheet.PngBytes == null || sheet.PngBytes.Length == 0)
                    failures.Add("no PNG bytes produced");

                // Decode the produced sheet and count key colours.
                if (sheet.PngBytes != null && sheet.PngBytes.Length > 0)
                {
                    int red, green, magenta, nudged;
                    CountColours(sheet.PngBytes, out red, out green, out magenta, out nudged);
                    if (red == 0) failures.Add("opaque red art was not preserved");
                    if (green == 0) failures.Add("opaque green art (the downscaled big frame) was not preserved");
                    if (magenta == 0) failures.Add("no magenta key pixels (transparent areas were not keyed)");
                    if (nudged == 0) failures.Add("a genuine magenta art pixel was not nudged off the key (254,0,255)");
                }

                // Cells identical in CONTENT collapse to one tile, even under different image names.
                // A skin can ship the same picture twice (an Android-Shimeji template does, and a reversed
                // sequence re-lists poses it already has); deduping only by image NAME kept 559 wasted cells
                // and 20% of the XML across the shipped corpus. Placement is part of identity, so the same
                // picture at a DIFFERENT anchor must still get its own cell.
                var dupOwned = new Dictionary<string, Bitmap>(StringComparer.Ordinal)
                {
                    { "a", Solid(30, 40, Color.FromArgb(255, 10, 20, 30)) },
                    { "copyOfA", Solid(30, 40, Color.FromArgb(255, 10, 20, 30)) },   // same picture, other name
                    { "other", Solid(30, 40, Color.FromArgb(255, 90, 80, 70)) },
                };
                try
                {
                    Func<string, Bitmap> dupLoad = delegate(string name) { return new Bitmap(dupOwned[name]); };
                    var dupPoses = new List<ShimejiPose>
                    {
                        new ShimejiPose { Image = "a",       AnchorX = 15, AnchorY = 40 },
                        new ShimejiPose { Image = "copyOfA", AnchorX = 15, AnchorY = 40 },  // collapses onto "a"
                        new ShimejiPose { Image = "other",   AnchorX = 15, AnchorY = 40 },
                        new ShimejiPose { Image = "copyOfA", AnchorX = 7,  AnchorY = 40 },  // other anchor: keeps its cell
                    };
                    SpriteSheet dup;
                    string dupError;
                    if (!SpriteSheetBuilder.Build(dupPoses, dupLoad, true, out dup, out dupError))
                    {
                        failures.Add("dedupe fixture failed to build -- " + dupError);
                    }
                    else
                    {
                        int cells = 0;
                        var distinct = new HashSet<int>();
                        foreach (var kv in dup.FrameIndexByKey) distinct.Add(kv.Value);
                        cells = distinct.Count;
                        if (cells != 3)
                            failures.Add("expected 3 distinct cells after content dedupe (a+other+a-at-another-anchor), got " + cells);
                        if (dup.FrameIndexByKey.Count != 4)
                            failures.Add("all 4 pose keys must still resolve to a tile, got " + dup.FrameIndexByKey.Count);
                        int viaA, viaCopy;
                        if (dup.FrameIndexByKey.TryGetValue(dupPoses[0].FrameKey, out viaA) &&
                            dup.FrameIndexByKey.TryGetValue(dupPoses[1].FrameKey, out viaCopy) &&
                            viaA != viaCopy)
                            failures.Add("two names for the same picture at the same anchor must share one cell");
                        int viaOffset;
                        if (dup.FrameIndexByKey.TryGetValue(dupPoses[3].FrameKey, out viaOffset) &&
                            viaOffset == viaA)
                            failures.Add("the same picture at a DIFFERENT anchor must not be collapsed");
                    }
                }
                finally
                {
                    foreach (Bitmap b in dupOwned.Values) b.Dispose();
                }

                // ---- THE TILE CAP IS TESTED ON WHAT THE SHEET CARRIES, NOT ON WHAT WAS ASKED FOR ----
                // The cap used to be applied BEFORE the byte-identical collapse above, so a skin the collapse
                // would have fitted was refused outright: the Android-Shimeji templates the dedup comment
                // names duplicate their sprite files, roughly 1100 distinct FrameKeys collapsing to about
                // 600. This fixture is that shape in miniature -- MaxTiles + 40 poses that are all the SAME
                // picture at the same anchor under different names, so they collapse to one cell.
                var capOwned = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
                try
                {
                    var capPoses = new List<ShimejiPose>();
                    Bitmap one = Solid(12, 12, Color.FromArgb(255, 30, 140, 210));
                    for (int i = 0; i < SpriteSheetBuilder.MaxTiles + 40; i++)
                    {
                        string nm = "/cap" + i + ".png";
                        capOwned[nm] = new Bitmap(one);
                        capPoses.Add(new ShimejiPose { Image = nm, AnchorX = 6, AnchorY = 12, Duration = 1 });
                    }
                    one.Dispose();
                    Func<string, Bitmap> capLoad = delegate(string n) { return new Bitmap(capOwned[n]); };
                    SpriteSheet capSheet; string capErr;
                    bool capOk = SpriteSheetBuilder.Build(capPoses, capLoad, true, out capSheet, out capErr);
                    if (!capOk)
                        failures.Add("a skin of " + capPoses.Count + " duplicate frames was refused, though they "
                            + "collapse to one cell: " + capErr);
                    else
                    {
                        var cells = new HashSet<int>();
                        foreach (var kv in capSheet.FrameIndexByKey) cells.Add(kv.Value);
                        if (cells.Count != 1)
                            failures.Add("the duplicate-frame skin should occupy ONE cell, got " + cells.Count
                                + "; without that the cap assertion above proves nothing");
                        if (capSheet.FrameIndexByKey.Count != capPoses.Count)
                            failures.Add("every one of the " + capPoses.Count + " poses must still resolve to a "
                                + "tile, got " + capSheet.FrameIndexByKey.Count);
                    }

                    // And the cap must still REFUSE what genuinely does not fit, or moving it made it inert.
                    var tooMany = new List<ShimejiPose>();
                    for (int i = 0; i < SpriteSheetBuilder.MaxTiles + 40; i++)
                    {
                        string nm = "/big" + i + ".png";
                        // A genuinely different colour per frame. The first version of this used
                        // (i % 251, i*7 % 251, i*13 % 251), and 251 is prime, so the triple depends only on
                        // i mod 251: frames i and i+251 were IDENTICAL, the dedup collapsed 1064 of them to
                        // 251, and the assertion below failed against correct code. Splitting i across the
                        // three channels gives one unique colour per frame up to 16.7 million.
                        capOwned[nm] = Solid(4, 4, Color.FromArgb(255, (i >> 16) & 0xFF, (i >> 8) & 0xFF, i & 0xFF));
                        tooMany.Add(new ShimejiPose { Image = nm, AnchorX = 2, AnchorY = 4, Duration = 1 });
                    }
                    SpriteSheet bigSheet; string bigErr;
                    if (SpriteSheetBuilder.Build(tooMany, capLoad, true, out bigSheet, out bigErr))
                        failures.Add("a skin of " + tooMany.Count + " DISTINCT frames was accepted; the "
                            + SpriteSheetBuilder.MaxTiles + "-tile cap is inert");
                    else if (bigErr == null || bigErr.IndexOf("tile limit", StringComparison.OrdinalIgnoreCase) < 0)
                        failures.Add("the cap refused, but not with a tile-limit message: " + bigErr);
                }
                finally
                {
                    foreach (Bitmap b in capOwned.Values) b.Dispose();
                }
            }
            finally
            {
                foreach (Bitmap b in owned.Values) b.Dispose();
            }

            var sb = new StringBuilder();
            sb.AppendLine("compositor self-test: 4 synthetic frames -> equal-cell magenta-keyed sheet");
            if (failures.Count == 0) { sb.Append("  cells capped, downscale applied, keying + collision correct, within budget"); detail = sb.ToString(); return true; }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static Bitmap Solid(int w, int h, Color c)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.Clear(c); }
            return bmp;
        }

        private static Bitmap Transparent(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.Clear(Color.FromArgb(0, 0, 0, 0)); }
            return bmp;
        }

        private static void CountColours(byte[] png, out int red, out int green, out int magenta, out int nudged)
        {
            red = green = magenta = nudged = 0;
            using (var ms = new MemoryStream(png))
            using (var raw = new Bitmap(ms))
            using (var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImage(raw, 0, 0); }
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
                            byte b = buf[i + 0], gg = buf[i + 1], r = buf[i + 2];
                            if (r == 255 && gg == 0 && b == 0) red++;
                            else if (r == 0 && gg == 255 && b == 0) green++;
                            else if (r == 255 && gg == 0 && b == 255) magenta++;
                            else if (r == 254 && gg == 0 && b == 255) nudged++;
                        }
                    }
                }
                finally { bmp.UnlockBits(data); }
            }
        }
    }
}
