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

                // ---- THE SHEET CLAMP TAKES THE SMALLER RATIO, ONCE ----
                // ClampSheet used to multiply BOTH ratios into the scale, the second computed from the
                // pre-clamp size and applied to a scale the first had already reduced, so a sheet over the cap
                // on both axes was shrunk by their PRODUCT: the ~600-cell 512px template shape came out at
                // 109px cells where 163px fit every cap, the pet at two thirds of its allowed size, silently
                // (F459). Asserted on the arithmetic over a grid of shapes rather than on a 4096px fixture:
                // the shapes cover neither axis over, one axis over, and both axes over, at three scales.
                int clampCases = 0, clampEngaged = 0;
                foreach (int[] shape in new[]
                         {
                             new[] { 25, 24, 512, 512 },   // both axes over at scale 0.5: the template shape
                             new[] { 17, 16, 256, 256 },   // width over only
                             new[] { 32, 32, 300, 160 },   // both over at scale 1 and 0.85, width only at 0.5
                             new[] { 2, 40, 200, 200 },    // height over only
                             new[] { 10, 10, 100, 100 },   // neither: the clamp must leave the scale alone
                         })
                    foreach (double scale in new[] { 1.0, 0.5, 0.8505 })
                    {
                        int tx = shape[0], ty = shape[1], cw = shape[2], ch = shape[3];
                        double sheetW = tx * cw * scale, sheetH = ty * ch * scale;
                        double expected = scale * Math.Min(1.0, Math.Min(
                            SpriteSheetBuilder.MaxSheetDimension / sheetW, SpriteSheetBuilder.MaxSheetDimension / sheetH));
                        double got = SpriteSheetBuilder.ClampSheet(scale, cw, ch, tx, ty);
                        clampCases++;
                        if (expected < scale) clampEngaged++;
                        if (Math.Abs(got - expected) > 1e-9)
                            failures.Add(string.Format(
                                "ClampSheet({0}, cell {1}x{2}, tiles {3}x{4}) = {5:0.######}, expected {6:0.######} (the smaller "
                                + "ratio applied once); the ratios were compounded, over-shrinking a sheet that exceeds "
                                + "the cap on both axes", scale, cw, ch, tx, ty, got, expected));
                    }
                // WITNESS: the grid engaged the clamp in more than half its cases, or the equality above is
                // mostly checking that an untouched scale comes back untouched.
                if (clampEngaged * 2 <= clampCases)
                    failures.Add("WITNESS: the clamp grid engaged the clamp in only " + clampEngaged + " of " + clampCases
                        + " cases, so it barely tests the ratio arithmetic");

                // ---- ROUNDING MUST NOT LAND THE SHEET PAST THE CAP ----
                // After a binding clamp, cell*scale is exactly 4096/tiles and Math.Round can take it UP: 17
                // tiles of 241px cells clamp to 240.94, round to 241, and the sheet is 4097px, which the app's
                // validator refuses ("invalid dimensions or tile geometry") while the compositor reports
                // success (F459). 289 distinct 1x241 frames keep the fixture to a few hundred KB of bitmaps and a
                // 17x4080 sheet. The fit must come from the SCALE, not from trimming the cell: the frame's
                // anchor row (its feet) must still be the bottom row of its tile.
                var tallOwned = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
                try
                {
                    var tallPoses = new List<ShimejiPose>();
                    for (int i = 0; i < 17 * 17; i++)
                    {
                        string nm = "/tall" + i + ".png";
                        // One colour per frame (the G,B pair is i), so the content dedupe keeps all 289 cells.
                        tallOwned[nm] = Solid(1, 241, Color.FromArgb(255, (i >> 16) & 0xFF, (i >> 8) & 0xFF, i & 0xFF));
                        tallPoses.Add(new ShimejiPose { Image = nm, AnchorX = 0, AnchorY = 241, Duration = 1 });
                    }
                    Func<string, Bitmap> tallLoad = delegate(string n) { return new Bitmap(tallOwned[n]); };
                    SpriteSheet tall; string tallErr;
                    if (!SpriteSheetBuilder.Build(tallPoses, tallLoad, true, out tall, out tallErr))
                        failures.Add("the 17x17 rounding fixture would not build: " + tallErr);
                    else
                    {
                        if (tall.TilesX * tall.CellWidth > SpriteSheetBuilder.MaxSheetDimension
                            || tall.TilesY * tall.CellHeight > SpriteSheetBuilder.MaxSheetDimension)
                            failures.Add("sheet " + (tall.TilesX * tall.CellWidth) + "x" + (tall.TilesY * tall.CellHeight)
                                + " exceeds the " + SpriteSheetBuilder.MaxSheetDimension + " px cap the validator enforces; "
                                + "rounding the clamped cell up landed the sheet past it");
                        if (!TileBottomRowPainted(tall.PngBytes, tall.TilesX, tall.CellWidth, tall.CellHeight, 0))
                            failures.Add("the first frame does not reach the bottom row of its cell, so the sheet was fitted "
                                + "by trimming the cell rather than by scaling the sprite, and the feet are clipped");
                        // WITNESS: the clamp engaged on this shape, or the geometry assertion is idle.
                        if (tall.Scale >= 1.0)
                            failures.Add("WITNESS: 17 tiles of 241px cells never engaged the sheet clamp (scale " + tall.Scale
                                + "), so the rounding assertion tests nothing");
                        if (tall.TilesX != 17 || tall.TilesY != 17)
                            failures.Add("WITNESS: the rounding fixture is " + tall.TilesX + "x" + tall.TilesY + " tiles, not the "
                                + "17x17 the rounding case needs");
                    }
                }
                finally
                {
                    foreach (Bitmap b in tallOwned.Values) b.Dispose();
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

        /// <summary>True when the bottom row of the given tile carries any pixel with alpha, i.e. the frame's
        /// anchor row was drawn rather than clipped off.</summary>
        private static bool TileBottomRowPainted(byte[] png, int tilesX, int cellW, int cellH, int tile)
        {
            using (var ms = new MemoryStream(png, false))
            using (var bmp = new Bitmap(ms))
            {
                int x0 = (tile % tilesX) * cellW;
                int y = (tile / tilesX) * cellH + cellH - 1;
                if (y >= bmp.Height) return false;
                for (int x = x0; x < x0 + cellW && x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).A != 0) return true;
                return false;
            }
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
