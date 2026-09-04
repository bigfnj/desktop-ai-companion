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
    /// Committed, IP-free test of the Android-Shimeji bundle path. Two halves, no copyrighted art:
    ///   1) PARSE: map a synthetic manifest.json + animation.json (in memory, no sprites) and assert the
    ///      ShimejiConfig -- action Type/Class/BorderType, per-frame dx/dy velocity, bottom-centre anchor,
    ///      the filePattern -&gt; "/0000.png" image name, and FALL -&gt; Class "Fall".
    ///   2) END-TO-END: write that bundle to a temp dir with tiny solid-colour PNG sprites (WIC decodes PNG the
    ///      same way it decodes the real WebP frames) and run <see cref="BundleConverter.ConvertBundle"/>,
    ///      asserting the pet is ACCEPTED, alpha-transparent, and has the expected animation count.
    /// </summary>
    public static class BundleSelfTest
    {
        private const int SpriteW = 32;
        private const int SpriteH = 40;

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

            var sb = new StringBuilder();
            sb.AppendLine("bundle self-test: manifest.json + animation.json -> ShimejiConfig -> accepted pet");
            if (failures.Count == 0)
            {
                sb.Append("  mapping correct (Type/Class/border, dx/dy velocity, bottom-centre anchor, Fall->Class Fall); WIC-decoded sprites composited to an accepted alpha pet");
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

        private static void WriteSyntheticBundle(string dir)
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
                WritePng(Path.Combine(spritesDir, string.Format("{0:D4}.png", i)), colours[i]);
        }

        private static void WritePng(string path, Color colour)
        {
            using (var bmp = new Bitmap(SpriteW, SpriteH, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(0, 0, 0, 0));   // a transparent border, so alpha is meaningful
                    using (var brush = new SolidBrush(colour))
                        g.FillRectangle(brush, 4, 4, SpriteW - 8, SpriteH - 8);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
