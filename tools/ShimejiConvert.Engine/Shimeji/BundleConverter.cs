using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Converts a modern "Android Shimeji" JSON+WebP bundle (manifest.json + animation.json + sprites/*.webp)
    /// into a desktopPet animations.xml by REUSING the shared pipeline: <see cref="BundleParser"/> builds a
    /// <see cref="ShimejiConfig"/>, <see cref="WebPLoader"/> decodes the WebP sprites, and the unchanged
    /// <see cref="SpriteSheetBuilder"/> + <see cref="PetEmitter"/> composite (in ALPHA mode, since WebP carries
    /// a real alpha channel) and emit the pet -- graded by the same acceptance bar as every other conversion.
    ///
    /// This is a separate entry point from <see cref="ShimejiEngine.ConvertSkin"/> (classic actions.xml skins);
    /// the two formats only meet at the shared <see cref="ShimejiConfig"/> shape.
    /// </summary>
    public static class BundleConverter
    {
        /// <summary>True if <paramref name="dir"/> looks like an Android-Shimeji bundle (has both JSON files).</summary>
        public static bool IsBundle(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, "manifest.json"))
                && File.Exists(Path.Combine(dir, "animation.json"));
        }

        /// <summary>
        /// Convert the bundle under <paramref name="bundleDir"/>. Pass <paramref name="skinName"/> null/blank to
        /// use the manifest name. Returns null with <paramref name="error"/> set on a parse/composite failure;
        /// otherwise the <see cref="ConversionResult"/> carries the pet, residue report, and acceptance verdict.
        /// </summary>
        public static ConversionResult ConvertBundle(string bundleDir, string skinName, out string error)
        {
            error = null;
            if (!IsBundle(bundleDir))
            {
                error = "not an Android Shimeji bundle: manifest.json + animation.json required under " + bundleDir;
                return null;
            }

            BundleInfo info;
            ShimejiConfig config;
            try { config = BundleParser.Parse(bundleDir, out info); }
            catch (Exception ex) { error = "bundle parse failed: " + ex.Message; return null; }

            string name = !string.IsNullOrWhiteSpace(skinName) ? skinName.Trim()
                : (!string.IsNullOrWhiteSpace(info.Name) ? info.Name.Trim() : "Shimeji");

            string spritesDir = ResolveSpritesDir(bundleDir, info.SpritesBasePath);
            Func<string, Bitmap> load = WebPLoader.ForDirectory(spritesDir);

            SpriteSheet sheet;
            // ALPHA mode: WebP has real per-pixel alpha, so the emitter writes <transparency>Alpha and the app
            // renders the pet per-pixel rather than through the 1-bit magenta key.
            if (!SpriteSheetBuilder.Build(PetEmitter.PosesToComposite(config), load, true, out sheet, out error))
                return null;

            ConversionResult result = PetEmitter.Emit(config, sheet, load, name);

            // Sprites the compositor re-anchored to their own pixels because they disagree with the manifest's
            // declared size (ShimejiPose.AnchorFollowsSprite). Named in the residue: a bundle that lies about
            // its sprite size is worth knowing about, and the report's promise is to say what was decided.
            if (result != null && result.Residue != null)
            {
                int declaredX = info.SpriteWidth / 2, declaredY = info.SpriteHeight;
                var reanchored = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (ShimejiPose p in config.Poses)
                {
                    if (p == null || !p.AnchorFollowsSprite || string.IsNullOrEmpty(p.Image)) continue;
                    if (p.AnchorX == declaredX && p.AnchorY == declaredY) continue;
                    if (!reanchored.ContainsKey(p.Image))
                        reanchored[p.Image] = p.Image + " (anchor " + p.AnchorX + "," + p.AnchorY + ")";
                }
                if (reanchored.Count > 0)
                    result.Residue.Notes.Add("The manifest declares " + info.SpriteWidth + "x" + info.SpriteHeight
                        + " sprites (anchor " + declaredX + "," + declaredY + "), but " + reanchored.Count
                        + " sprite(s) decode to a different size and were anchored to their own pixels instead: "
                        + string.Join(", ", new List<string>(reanchored.Values).ToArray())
                        + ". Trusting the manifest would have floated a shorter sprite above the floor or clipped a taller one.");
            }
            return result;
        }

        private static string ResolveSpritesDir(string bundleDir, string basePath)
        {
            string rel = (basePath ?? "sprites/")
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(rel) ? bundleDir : Path.Combine(bundleDir, rel);
        }
    }
}
