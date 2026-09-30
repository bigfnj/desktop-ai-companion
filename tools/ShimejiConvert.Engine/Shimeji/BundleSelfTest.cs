using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free test of the Android-Shimeji bundle path. Three parts, no copyrighted art:
    ///   1) PARSE: map a synthetic manifest.json + animation.json (in memory, no sprites) and assert the
    ///      ShimejiConfig -- action Type/Class/BorderType, per-frame dx/dy velocity, bottom-centre anchor,
    ///      the filePattern -&gt; "/0000.png" image name, and FALL -&gt; Class "Fall".
    ///   2) END-TO-END: write that bundle to a temp dir with tiny solid-colour PNG sprites and run
    ///      <see cref="BundleConverter.ConvertBundle"/>, asserting the pet is ACCEPTED, alpha-transparent,
    ///      and has the expected animation count. The sprites are PNG so this half exercises the compositor
    ///      through WIC; real bundles ship WebP, which WIC must NOT decode (it drops the alpha on some
    ///      machines -- see WebPLoader), so this half deliberately routes around dwebp.
    ///   3) THE WEBP PATH: one committed 4x4 lossless WebP with alpha goes through
    ///      <see cref="WebPLoader.Decode"/>, the same entry the compositor uses for .webp, so FindDwebp, the
    ///      process spawn, the '-o -' stdout stream and both bounds execute in the gate. Until 2026-09-29
    ///      nothing did, and a missing or broken dwebp passed SELFTEST while every real bundle import
    ///      failed (F445).
    /// </summary>
    public static class BundleSelfTest
    {
        private const int SpriteW = 32;
        private const int SpriteH = 40;

        // A 4x4 lossless WebP with alpha: fully transparent except an opaque rgb(200,40,40) 2x2 block at
        // (1,1)-(2,2). Generated 2026-09-29 with ImageMagick 7 from a synthetic image
        // (`magick -size 4x4 xc:none -fill "rgb(200,40,40)" -draw "rectangle 1,1 2,2"
        // -define webp:lossless=true -define webp:exact=true`), so it is IP-free, and decoded once with the
        // bundled dwebp to confirm the corner reads alpha 0 and the block alpha 255 before it was committed.
        private static readonly byte[] TinyWebPWithAlpha =
        {
            0x52, 0x49, 0x46, 0x46, 0x20, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x4C,
            0x14, 0x00, 0x00, 0x00, 0x2F, 0x03, 0xC0, 0x00, 0x10, 0x0F, 0x30, 0x28, 0x83, 0x3C, 0x28, 0xF3,
            0x1F, 0xF0, 0x18, 0x83, 0x88, 0xFE, 0x87, 0x01,
        };

        // A four-animation synthetic bundle: a stand hub, a walk (locomotion), an AIR fall, and a USER drag.
        private const string ManifestJson =
            "{\"name\":\"SelfTest Skin\",\"author\":{\"name\":\"tester\"}," +
            "\"license\":{\"type\":\"CUSTOM\"}," +
            "\"sprites\":{\"basePath\":\"sprites/\",\"filePattern\":\"%04d.png\",\"spriteCount\":6,\"size\":[32,40]}}";

        private const string AnimationJson =
            "{\"default_animation\":\"stand\",\"initial_candidates\":[\"fall\"],\"animations\":[" +
              "{\"key\":\"stand\",\"type\":\"GROUND\",\"subtype\":\"STAND\",\"loop\":\"ONESHOT\",\"direction\":\"ANY\"," +
                "\"frames\":[{\"sprite\":0,\"durationTicks\":30},{\"sprite\":1,\"durationTicks\":5}]}," +
              "{\"key\":\"walk_left\",\"type\":\"GROUND\",\"subtype\":\"WALK\",\"loop\":\"LOOP\",\"direction\":\"LEFT\"," +
                "\"frames\":[{\"sprite\":2,\"dx\":-2,\"durationTicks\":7},{\"sprite\":3,\"dx\":-2,\"durationTicks\":7}]}," +
              "{\"key\":\"fall\",\"type\":\"AIR\",\"subtype\":\"FALL\",\"loop\":\"LOOP\",\"direction\":\"ANY\"," +
                "\"frames\":[{\"sprite\":4,\"dy\":10,\"durationTicks\":4}]}," +
              "{\"key\":\"drag\",\"type\":\"USER\",\"subtype\":\"DRAG\",\"loop\":\"LOOP\",\"direction\":\"ANY\"," +
                "\"frames\":[{\"sprite\":5,\"durationTicks\":8}]}" +
            "]}";

        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            // ---- 1) parse mapping (no sprites needed) ----
            BundleInfo info;
            ShimejiConfig config;
            try { config = BundleParser.ParseJson(ManifestJson, AnimationJson, out info); }
            catch (Exception ex) { detail = "bundle self-test: ParseJson threw -- " + ex.Message; return false; }

            if (info.SpriteWidth != SpriteW || info.SpriteHeight != SpriteH)
                failures.Add(string.Format("manifest size {0}x{1}, expected {2}x{3}", info.SpriteWidth, info.SpriteHeight, SpriteW, SpriteH));
            if (config.Actions.Count != 4)
                failures.Add("expected 4 actions, got " + config.Actions.Count);

            ShimejiAction stand = ByName(config, "stand");
            ShimejiAction walk = ByName(config, "walk_left");
            ShimejiAction fall = ByName(config, "fall");
            ShimejiAction drag = ByName(config, "drag");

            CheckAction(failures, stand, "stand", "Stay", null, "Floor");
            CheckAction(failures, walk, "walk_left", "Move", null, "Floor");
            CheckAction(failures, fall, "fall", "Animate", "Fall", null);   // AIR -> no border; FALL -> Class Fall
            CheckAction(failures, drag, "drag", "Animate", "Dragged", null); // USER -> no border; DRAG -> Class Dragged

            // every action must land in Group1 (nothing in a bundle carries the Group2/3 state signals)
            foreach (ShimejiAction a in config.Actions)
                if (a != null && a.Group != FidelityGroup.Group1)
                    failures.Add(a.Name + " classified " + a.Group + ", expected Group1");

            if (stand != null && stand.Animations.Count > 0 && stand.Animations[0].Poses.Count > 0)
            {
                ShimejiPose p0 = stand.Animations[0].Poses[0];
                if (p0.Image != "/0000.png") failures.Add("stand frame 0 image '" + p0.Image + "', expected /0000.png");
                if (p0.Duration != 30) failures.Add("stand frame 0 duration " + p0.Duration + ", expected 30");
                if (p0.AnchorX != SpriteW / 2 || p0.AnchorY != SpriteH)
                    failures.Add(string.Format("stand anchor {0},{1}, expected bottom-centre {2},{3}", p0.AnchorX, p0.AnchorY, SpriteW / 2, SpriteH));
            }
            else failures.Add("stand has no poses");

            if (walk != null && walk.Animations.Count > 0 && walk.Animations[0].Poses.Count == 2)
            {
                if (walk.Animations[0].Poses[0].VelX != -2) failures.Add("walk dx not carried to VelX (-2)");
                if (walk.Animations[0].Poses[1].Image != "/0003.png") failures.Add("walk frame 1 image '" + walk.Animations[0].Poses[1].Image + "', expected /0003.png");
            }
            else failures.Add("walk_left should have 2 poses");

            if (fall != null && fall.Animations.Count > 0 && fall.Animations[0].Poses.Count > 0)
                if (fall.Animations[0].Poses[0].VelY != 10) failures.Add("fall dy not carried to VelY (10)");

            // config.Poses must gather every frame (2+2+1+1 = 6), mirroring ShimejiParser.
            if (config.Poses.Count != 6) failures.Add("config.Poses = " + config.Poses.Count + ", expected 6");

            // ---- 2) end-to-end convert through the real pipeline (WIC decodes the PNG sprites) ----
            string tempDir = Path.Combine(Path.GetTempPath(), "shimeji-bundle-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteSyntheticBundle(tempDir);

                if (!BundleConverter.IsBundle(tempDir)) failures.Add("IsBundle returned false for a real bundle dir");

                string error;
                ConversionResult r = BundleConverter.ConvertBundle(tempDir, "SelfTest Skin", out error);
                if (r == null)
                {
                    failures.Add("ConvertBundle returned null: " + error);
                }
                else
                {
                    if (!r.Accepted) failures.Add("pet not ACCEPTED (valid=" + r.Valid + ", roundtrip=" + r.RoundTrips +
                        ", unreachable=" + (r.Graph != null ? r.Graph.Unreachable.Count : -1) + ", err=" + r.Error + ")");
                    if (r.Root == null || r.Root.Image == null || r.Root.Image.Transparency != "Alpha")
                        failures.Add("expected <transparency>Alpha (WebP alpha), got " +
                            (r.Root != null && r.Root.Image != null ? r.Root.Image.Transparency : "<none>"));
                    int anims = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                        ? r.Root.Animations.Animation.Length : 0;
                    // 2 floor spokes (stand, walk_left) + fall + drag + kill + sync + turn = 7
                    if (anims != 7) failures.Add("expected 7 animations, got " + anims);
                    // WITNESS for part 4 below: a bundle whose sprites all match the manifest is not told
                    // anything was re-anchored.
                    if (r.Residue != null && r.Residue.Notes.Exists(s => s.IndexOf("anchored to their own pixels", StringComparison.Ordinal) >= 0))
                        failures.Add("WITNESS: the uniform bundle's residue reports re-anchored sprites although every sprite matches the manifest");
                }
            }
            catch (Exception ex)
            {
                failures.Add("end-to-end convert threw -- " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
            }

            // ---- 4) ANCHORS COME FROM THE DECODED SPRITES, NOT FROM THE MANIFEST ----
            // A bundle anchors every pose at (width/2, height) of the manifest's declared size. Five of 948 real
            // bundles ship sprites that disagree with their own manifest, and the cell height came from that
            // anchor while the width came from the real bitmap: a shorter sprite was drawn at the cell top and
            // floated above the floor by the difference, a taller one lost its bottom rows to the tile clip,
            // and the pet was ACCEPTED (F444). This bundle declares 32x40 and ships sprite 0 at 24 rows and
            // sprite 2 at 48. The cell must be 48 tall (the tallest real sprite), and every sprite's lowest
            // painted row must sit on the SAME line, four rows above the cell bottom (WritePng leaves a 4px
            // transparent border): that is the feet on the floor, whatever the sprite's height.
            string mixedDir = Path.Combine(Path.GetTempPath(), "shimeji-bundle-mixed-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteSyntheticBundle(mixedDir, new[] { 24, SpriteH, 48, SpriteH, SpriteH, SpriteH });
                string mixedError;
                ConversionResult m = BundleConverter.ConvertBundle(mixedDir, "Mixed Skin", out mixedError);
                if (m == null)
                    failures.Add("the mixed-size bundle failed to convert: " + mixedError);
                else if (!m.Accepted)
                    failures.Add("the mixed-size bundle was not ACCEPTED: " + m.Error);
                else
                {
                    using (var ms = new MemoryStream(Convert.FromBase64String(m.Root.Image.Png)))
                    using (var sheetBmp = new Bitmap(ms))
                    {
                        int tilesX = m.Root.Image.TilesX;
                        int cw = sheetBmp.Width / tilesX;
                        int ch = sheetBmp.Height / m.Root.Image.TilesY;
                        if (ch != 48)
                            failures.Add("the mixed-size bundle's cell is " + ch + " rows tall; the tallest decoded sprite is 48, so "
                                + "the manifest's 40 won and the tall sprite lost its bottom rows");
                        XmlData.AnimationNode standAnim = AnimationNamed(m, "stand");
                        XmlData.AnimationNode walkAnim = AnimationNamed(m, "walk") ?? AnimationNamed(m, "walk_left");
                        if (standAnim == null || standAnim.Sequence == null || standAnim.Sequence.Frame == null
                            || standAnim.Sequence.Frame.Length < 2 || walkAnim == null || walkAnim.Sequence == null
                            || walkAnim.Sequence.Frame == null || walkAnim.Sequence.Frame.Length < 1)
                            failures.Add("the mixed-size bundle lost the animations the anchor assertions need (stand with 2 frames, walk with 1+)");
                        else
                        {
                            int floorRow = ch - 1 - 4;
                            int shortRow = LowestPaintedRow(sheetBmp, tilesX, cw, ch, standAnim.Sequence.Frame[0]);
                            int normalRow = LowestPaintedRow(sheetBmp, tilesX, cw, ch, standAnim.Sequence.Frame[1]);
                            int tallRow = LowestPaintedRow(sheetBmp, tilesX, cw, ch, walkAnim.Sequence.Frame[0]);
                            if (shortRow != floorRow)
                                failures.Add("the 24-row sprite's feet are on row " + shortRow + " of a " + ch + "-row cell, not on the "
                                    + "floor line (" + floorRow + "): its anchor came from the manifest, so it floats above the floor");
                            if (normalRow != floorRow)
                                failures.Add("the 40-row sprite's feet are on row " + normalRow + ", not on the floor line (" + floorRow + ")");
                            if (tallRow != floorRow)
                                failures.Add("the 48-row sprite's feet are on row " + tallRow + ", not on the floor line (" + floorRow
                                    + "): it was clipped to the manifest's height instead of anchored to its own");
                        }
                    }
                    if (!m.Residue.Notes.Exists(s => s.IndexOf("anchored to their own pixels", StringComparison.Ordinal) >= 0
                                                     && s.IndexOf("/0000.png", StringComparison.Ordinal) >= 0
                                                     && s.IndexOf("/0002.png", StringComparison.Ordinal) >= 0))
                        failures.Add("the residue does not name the two sprites that disagree with the manifest");
                }
            }
            catch (Exception ex)
            {
                failures.Add("mixed-size bundle convert threw -- " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(mixedDir)) Directory.Delete(mixedDir, true); } catch { /* best-effort cleanup */ }
            }

            // ---- 3) the WebP path itself: FindDwebp, the process spawn, the '-o -' stream, both bounds ----
            string webpDir = Path.Combine(Path.GetTempPath(), "shimeji-webp-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(webpDir);
                string webpPath = Path.Combine(webpDir, "0000.webp");
                File.WriteAllBytes(webpPath, TinyWebPWithAlpha);
                using (Bitmap decoded = WebPLoader.Decode(webpPath))
                {
                    if (decoded.Width != 4 || decoded.Height != 4)
                        failures.Add("dwebp-decoded WebP is " + decoded.Width + "x" + decoded.Height + ", expected 4x4");
                    else
                    {
                        Color corner = decoded.GetPixel(0, 0);
                        Color block = decoded.GetPixel(1, 1);
                        if (corner.A != 0)
                            failures.Add("dwebp-decoded WebP lost its alpha: the transparent corner reads A=" + corner.A);
                        if (block.A != 255 || block.R != 200 || block.G != 40 || block.B != 40)
                            failures.Add("dwebp-decoded WebP pixel (1,1) is A=" + block.A + " R=" + block.R + " G=" + block.G +
                                " B=" + block.B + ", expected opaque (200,40,40)");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add("WebP decode through dwebp threw -- " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(webpDir)) Directory.Delete(webpDir, true); } catch { /* best-effort cleanup */ }
            }

            var sb = new StringBuilder();
            sb.AppendLine("bundle self-test: manifest.json + animation.json -> ShimejiConfig -> accepted pet; one WebP through dwebp");
            if (failures.Count == 0)
            {
                sb.Append("  mapping correct (Type/Class/border, dx/dy velocity, bottom-centre anchor, Fall->Class Fall); " +
                          "WIC-decoded PNG sprites composited to an accepted alpha pet; a 4x4 WebP decoded through dwebp with its alpha intact");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static ShimejiAction ByName(ShimejiConfig config, string name)
        {
            return config.Actions.FirstOrDefault(a => a != null && string.Equals(a.Name, name, StringComparison.Ordinal));
        }

        private static void CheckAction(List<string> failures, ShimejiAction a, string name, string type, string cls, string border)
        {
            if (a == null) { failures.Add("action '" + name + "' missing"); return; }
            if (a.Type != type) failures.Add(name + " Type=" + (a.Type ?? "<null>") + ", expected " + type);
            if (a.Class != cls) failures.Add(name + " Class=" + (a.Class ?? "<null>") + ", expected " + (cls ?? "<null>"));
            if (a.BorderType != border) failures.Add(name + " BorderType=" + (a.BorderType ?? "<null>") + ", expected " + (border ?? "<null>"));
        }

        private static XmlData.AnimationNode AnimationNamed(ConversionResult r, string name)
        {
            if (r == null || r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (a != null && string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
            return null;
        }

        /// <summary>The lowest row WITHIN the tile that carries a pixel with alpha, or -1 for a blank tile.
        /// Where a sprite's feet landed, in other words.</summary>
        private static int LowestPaintedRow(Bitmap sheet, int tilesX, int cellW, int cellH, int tile)
        {
            int x0 = (tile % tilesX) * cellW;
            int y0 = (tile / tilesX) * cellH;
            for (int row = cellH - 1; row >= 0; row--)
            {
                int y = y0 + row;
                if (y >= sheet.Height) continue;
                for (int x = x0; x < x0 + cellW && x < sheet.Width; x++)
                    if (sheet.GetPixel(x, y).A != 0) return row;
            }
            return -1;
        }

        /// <summary>Write the synthetic bundle. <paramref name="heights"/>, when given, overrides each sprite's
        /// pixel height while the manifest keeps declaring <see cref="SpriteH"/>: the mixed-size case.</summary>
        private static void WriteSyntheticBundle(string dir, int[] heights = null)
        {
            string spritesDir = Path.Combine(dir, "sprites");
            Directory.CreateDirectory(spritesDir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), ManifestJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "animation.json"), AnimationJson, new UTF8Encoding(false));

            Color[] colours =
            {
                Color.FromArgb(255, 200, 40, 40), Color.FromArgb(255, 40, 200, 40),
                Color.FromArgb(255, 40, 40, 200), Color.FromArgb(255, 200, 200, 40),
                Color.FromArgb(255, 200, 40, 200), Color.FromArgb(255, 40, 200, 200),
            };
            for (int i = 0; i < colours.Length; i++)
                WritePng(Path.Combine(spritesDir, string.Format("{0:D4}.png", i)), colours[i],
                    heights != null && i < heights.Length ? heights[i] : SpriteH);
        }

        private static void WritePng(string path, Color colour, int height)
        {
            using (var bmp = new Bitmap(SpriteW, height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(0, 0, 0, 0));   // a transparent border, so alpha is meaningful
                    using (var brush = new SolidBrush(colour))
                        g.FillRectangle(brush, 4, 4, SpriteW - 8, height - 8);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
