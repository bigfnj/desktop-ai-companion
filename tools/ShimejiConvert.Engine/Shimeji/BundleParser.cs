using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>Metadata read from an Android-Shimeji bundle's manifest.json (name/author/license plus the
    /// sprite sheet layout the pose mapping needs). Defaults match the sample bundle so a manifest that omits
    /// a field still resolves.</summary>
    public sealed class BundleInfo
    {
        public string Name;
        public string Author;
        public string License;
        public string SpritesBasePath = "sprites/";
        public string FilePattern = "%04d.webp";
        public int SpriteCount;
        public int SpriteWidth = 512;
        public int SpriteHeight = 512;
        public string DefaultAnimation;
    }

    /// <summary>
    /// Maps a modern "Android Shimeji" JSON+WebP bundle (manifest.json + animation.json) into the SAME
    /// <see cref="ShimejiConfig"/> the classic XML parser produces, so the shared compositor + emitter run
    /// unchanged. It does not touch pixels; <see cref="WebPLoader"/> handles decoding at composite time.
    ///
    /// Mapping (see the converter brief): each animation.key -&gt; one <see cref="ShimejiAction"/>;
    /// type GROUND/WALL/CEILING -&gt; BorderType Floor/Wall/Ceiling (AIR/USER carry none); subtype
    /// FALL/DRAG -&gt; Class Fall/Dragged, STAND/IDLE -&gt; Type Stay, WALK -&gt; Type Move, else Type Animate.
    /// Each frame -&gt; one <see cref="ShimejiPose"/> with Image from the manifest filePattern (leading "/"),
    /// Duration=durationTicks, VelX=dx, VelY=dy, and a bottom-centre anchor (width/2, height). Every action is
    /// then run through <see cref="ActionClassifier"/> exactly as <see cref="ShimejiParser"/> does.
    /// </summary>
    public static class BundleParser
    {
        /// <summary>Read manifest.json + animation.json from <paramref name="bundleDir"/> into a config.</summary>
        public static ShimejiConfig Parse(string bundleDir, out BundleInfo info)
        {
            if (string.IsNullOrEmpty(bundleDir)) throw new ArgumentNullException("bundleDir");
            string manifestPath = Path.Combine(bundleDir, "manifest.json");
            string animationPath = Path.Combine(bundleDir, "animation.json");
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("No manifest.json under " + bundleDir);
            if (!File.Exists(animationPath)) throw new FileNotFoundException("No animation.json under " + bundleDir);
            return ParseJson(File.ReadAllText(manifestPath), File.ReadAllText(animationPath), out info);
        }

        /// <summary>Map the two JSON documents directly (no disk), for self-tests and in-memory callers.</summary>
        internal static ShimejiConfig ParseJson(string manifestJson, string animationJson, out BundleInfo info)
        {
            info = new BundleInfo();
            ParseManifest(manifestJson, info);
            var config = new ShimejiConfig();
            ParseAnimation(animationJson, info, config);
            return config;
        }

        private static void ParseManifest(string json, BundleInfo info)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                info.Name = GetString(root, "name") ?? info.Name;

                JsonElement author;
                if (root.TryGetProperty("author", out author))
                {
                    if (author.ValueKind == JsonValueKind.Object) info.Author = GetString(author, "name");
                    else if (author.ValueKind == JsonValueKind.String) info.Author = author.GetString();
                }

                JsonElement license;
                if (root.TryGetProperty("license", out license))
                {
                    if (license.ValueKind == JsonValueKind.Object) info.License = GetString(license, "type");
                    else if (license.ValueKind == JsonValueKind.String) info.License = license.GetString();
                }

                JsonElement sprites;
                if (root.TryGetProperty("sprites", out sprites) && sprites.ValueKind == JsonValueKind.Object)
                {
                    info.SpritesBasePath = GetString(sprites, "basePath") ?? info.SpritesBasePath;
                    info.FilePattern = GetString(sprites, "filePattern") ?? info.FilePattern;
                    info.SpriteCount = GetInt(sprites, "spriteCount", 0);

                    JsonElement size;
                    if (sprites.TryGetProperty("size", out size) && size.ValueKind == JsonValueKind.Array
                        && size.GetArrayLength() >= 2)
                    {
                        int w, h;
                        if (TryGetInt(size[0], out w) && w > 0) info.SpriteWidth = w;
                        if (TryGetInt(size[1], out h) && h > 0) info.SpriteHeight = h;
                    }
                }
            }
        }

        private static void ParseAnimation(string json, BundleInfo info, ShimejiConfig config)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                info.DefaultAnimation = GetString(root, "default_animation") ?? info.DefaultAnimation;

                JsonElement anims;
                if (!root.TryGetProperty("animations", out anims) || anims.ValueKind != JsonValueKind.Array)
                    return;

                var inflow = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (JsonElement anim in anims.EnumerateArray())
                {
                    if (anim.ValueKind != JsonValueKind.Object) continue;

                    var action = new ShimejiAction
                    {
                        Name = GetString(anim, "key"),
                        BorderType = MapBorder(GetString(anim, "type")),
                    };
                    MapSubtype(GetString(anim, "subtype"), action);

                    var animation = new ShimejiAnimation();
                    JsonElement frames;
                    if (anim.TryGetProperty("frames", out frames) && frames.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement fr in frames.EnumerateArray())
                        {
                            if (fr.ValueKind != JsonValueKind.Object) continue;
                            var pose = new ShimejiPose
                            {
                                // sprite index -> filename via the manifest pattern, with a leading '/' to match
                                // the classic parser's "/shime1.png" convention the loader/compositor key on.
                                Image = "/" + FormatSprite(info.FilePattern, GetInt(fr, "sprite", 0)),
                                Duration = GetInt(fr, "durationTicks", 1),
                                VelX = GetInt(fr, "dx", 0),
                                VelY = GetInt(fr, "dy", 0),
                                // Bottom-centre anchor: sprites are drawn feet-at-bottom, centred horizontally.
                                AnchorX = info.SpriteWidth / 2,
                                AnchorY = info.SpriteHeight,
                            };
                            animation.Poses.Add(pose);
                            config.Poses.Add(pose);   // complete sprite set, exactly as ShimejiParser gathers it
                        }
                    }
                    action.Animations.Add(animation);
                    ActionClassifier.Classify(action);
                    config.Actions.Add(action);

                    AccumulateInflow(anim, action.Name, inflow);
                }

                // The bundle carries its behaviour model as a per-animation transition graph (auto.onFinish
                // weights), not a flat list. Reduce it to a resting-selection weight per animation -- how much
                // the chain flows INTO each state from OTHER states (self-loops excluded, since the emitter's
                // own loco self-chain already models "keep going") -- and hand it to the emitter as the same
                // BehaviorFrequency the classic behaviors.xml path fills, so both source formats bias the hub
                // identically. Without this the 21 web-export pets pick every action with equal odds and barely
                // move (the "shuffles animations but never goes anywhere" report).
                foreach (KeyValuePair<string, int> kv in inflow)
                    config.BehaviorFrequency[kv.Key] = kv.Value * BundleSelectionScale;
            }
        }

        // Scales the small integer transition weights up into the same "clearly dominant over the reachability
        // baseline" range the classic path's Frequency values (typically 100) already sit in, so a much-visited
        // walk state outweighs a rarely-entered idle instead of edging it by a point or two.
        private const int BundleSelectionScale = 20;

        private static void AccumulateInflow(JsonElement anim, string fromKey, Dictionary<string, int> inflow)
        {
            JsonElement auto;
            if (string.IsNullOrEmpty(fromKey) ||
                !anim.TryGetProperty("auto", out auto) || auto.ValueKind != JsonValueKind.Object)
                return;
            JsonElement onFinish;
            if (!auto.TryGetProperty("onFinish", out onFinish) || onFinish.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement t in onFinish.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                string to = GetString(t, "to");
                if (string.IsNullOrEmpty(to) || string.Equals(to, fromKey, StringComparison.Ordinal)) continue;
                int w = GetInt(t, "weight", 1);
                if (w <= 0) w = 1;
                int current;
                inflow.TryGetValue(to, out current);
                inflow[to] = current + w;
            }
        }

        // GROUND/WALL/CEILING carry a border context; AIR (fall) and USER (drag/fling) do not.
        private static string MapBorder(string type)
        {
            if (type == null) return null;
            switch (type.Trim().ToUpperInvariant())
            {
                case "GROUND": return "Floor";
                case "WALL": return "Wall";
                case "CEILING": return "Ceiling";
                default: return null;
            }
        }

        // Class is set only for the two magic subtypes (Fall/Dragged) the emitter routes specially; every other
        // subtype leaves Class null and just picks the Type the classifier and floor test read.
        private static void MapSubtype(string subtype, ShimejiAction action)
        {
            string s = subtype == null ? "" : subtype.Trim().ToUpperInvariant();
            switch (s)
            {
                case "FALL": action.Class = "Fall"; action.Type = "Animate"; break;
                case "DRAG": action.Class = "Dragged"; action.Type = "Animate"; break;
                case "STAND":
                case "IDLE": action.Type = "Stay"; break;
                case "WALK": action.Type = "Move"; break;
                default: action.Type = "Animate"; break;
            }
        }

        private static readonly Regex FieldPattern = new Regex(@"%0*(\d*)d", RegexOptions.CultureInvariant);

        /// <summary>Expand a C-style zero-padded integer field, e.g. FormatSprite("%04d.webp", 5) == "0005.webp".</summary>
        internal static string FormatSprite(string pattern, int index)
        {
            if (string.IsNullOrEmpty(pattern)) return index.ToString(CultureInfo.InvariantCulture);
            Match m = FieldPattern.Match(pattern);
            if (!m.Success) return pattern;
            int width = m.Groups[1].Value.Length > 0
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            string num = index.ToString(CultureInfo.InvariantCulture);
            if (num.Length < width) num = num.PadLeft(width, '0');
            return pattern.Substring(0, m.Index) + num + pattern.Substring(m.Index + m.Length);
        }

        private static string GetString(JsonElement obj, string name)
        {
            JsonElement v;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static int GetInt(JsonElement obj, string name, int fallback)
        {
            JsonElement v;
            int r;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out v) && TryGetInt(v, out r))
                return r;
            return fallback;
        }

        private static bool TryGetInt(JsonElement v, out int result)
        {
            result = 0;
            if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt32(out result);
            if (v.ValueKind == JsonValueKind.String)
                return int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            return false;
        }
    }
}
