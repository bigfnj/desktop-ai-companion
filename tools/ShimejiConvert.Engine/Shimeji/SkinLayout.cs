using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>One convertible skin found on disk: where its conf lives, where its sprites live, its name.</summary>
    public sealed class DetectedSkin
    {
        public string Name;
        public string ConfDir;           // holds actions.xml (+ optionally behaviors.xml); null when bundled
        public string ImgDir;            // holds the shimeN.png sprites
        public bool UsesBundledConf;     // true when the skin ships no conf and the bundled base config is used
    }

    /// <summary>
    /// Works out the Shimeji conf + sprite folders inside whatever a user points us at. Shimeji-EE lays a
    /// skin out as a shared conf/ plus one or more img/&lt;Skin&gt;/ sprite folders, but downloads vary, so
    /// this is heuristic and tolerant:
    ///   * conf = the nearest folder (root, root/conf, or a shallow search) containing actions.xml;
    ///   * each img folder = a folder containing shimeN-style PNGs (root/img/*, or the root itself).
    /// A skin with NO actions.xml cannot be converted here -- a sprites-only skin relies on the base
    /// Shimeji conf, which is copyrighted and this repo does not ship. That case returns no skins with a
    /// clear reason, rather than guessing.
    /// </summary>
    public static class SkinLayout
    {
        public static List<DetectedSkin> Detect(string rootDir, out string note)
        {
            note = null;
            var skins = new List<DetectedSkin>();
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
            {
                note = "no such folder: " + rootDir;
                return skins;
            }

            string confDir = FindConfDir(rootDir);
            bool bundled = confDir == null;

            foreach (string imgDir in FindImgDirs(rootDir))
                skins.Add(new DetectedSkin
                {
                    Name = SkinName(rootDir, imgDir),
                    ConfDir = confDir,
                    ImgDir = imgDir,
                    UsesBundledConf = bundled,
                });

            if (skins.Count == 0)
                note = bundled
                    ? "found no sprites (looked for *.png) and no actions.xml. Point at a Shimeji skin folder."
                    : "found a conf but no sprite folder (looked for *.png). Point at the skin's img folder.";
            else if (bundled)
                note = "this skin has no behaviour config of its own; the bundled Shimeji base behaviour will be used.";
            return skins;
        }

        private static string FindConfDir(string root)
        {
            if (File.Exists(Path.Combine(root, "actions.xml"))) return root;
            string conf = Path.Combine(root, "conf");
            if (File.Exists(Path.Combine(conf, "actions.xml"))) return conf;
            // shallow search (root's descendants, capped depth) for any actions.xml
            foreach (string dir in EnumerateDirs(root, 3))
                if (File.Exists(Path.Combine(dir, "actions.xml"))) return dir;
            return null;
        }

        private static IEnumerable<string> FindImgDirs(string root)
        {
            // Gather every candidate folder (preferred locations first, then a capped descendant sweep) and
            // rank by how many sprites it actually holds, richest first. Real downloads nest sprites in ways a
            // fixed root/img/<Skin> assumption misses -- e.g. img/<Skin>/shime*.png next to an icon-only img/,
            // or a whole pack of sibling <Character>/img folders -- so a stray icon.png dir must never outrank
            // the true sprite folder. shime-named sprites win ties so a skin's frames beat a banner/icon dir.
            var candidates = new List<string>();
            string img = Path.Combine(root, "img");
            if (Directory.Exists(img))
            {
                foreach (string sub in Directory.GetDirectories(img)) candidates.Add(sub);
                candidates.Add(img);
            }
            candidates.Add(root);
            foreach (string dir in EnumerateDirs(root, 4)) candidates.Add(dir);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scored = new List<DetectedImgDir>();
            foreach (string d in candidates)
            {
                string full;
                try { full = Path.GetFullPath(d); } catch { continue; }
                if (!seen.Add(full)) continue;
                int total, shime;
                SpriteCounts(d, out total, out shime);
                if (total > 0) scored.Add(new DetectedImgDir { Dir = d, Total = total, Shime = shime });
            }
            // richest first; a dir with shime*.png outranks a same-size dir without (drops icon/banner dirs).
            scored.Sort(delegate (DetectedImgDir a, DetectedImgDir b)
            {
                int byShime = b.Shime.CompareTo(a.Shime);
                return byShime != 0 ? byShime : b.Total.CompareTo(a.Total);
            });
            return scored.Select(s => s.Dir).ToList();
        }

        private sealed class DetectedImgDir { public string Dir; public int Total; public int Shime; }

        private static void SpriteCounts(string dir, out int total, out int shime)
        {
            total = 0; shime = 0;
            try
            {
                string[] pngs = Directory.GetFiles(dir, "*.png");
                total = pngs.Length;
                foreach (string p in pngs)
                    if (Path.GetFileName(p).StartsWith("shime", StringComparison.OrdinalIgnoreCase))
                        shime++;
            }
            catch { }
        }

        private static string SkinName(string root, string imgDir)
        {
            string leaf = new DirectoryInfo(imgDir).Name;
            if (!string.Equals(leaf, "img", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(imgDir, root, StringComparison.OrdinalIgnoreCase))
                return leaf;
            return new DirectoryInfo(root).Name;
        }

        private static IEnumerable<string> EnumerateDirs(string root, int maxDepth)
        {
            var queue = new Queue<Tuple<string, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            while (queue.Count > 0)
            {
                Tuple<string, int> cur = queue.Dequeue();
                string[] subs;
                try { subs = Directory.GetDirectories(cur.Item1); }
                catch { continue; }
                foreach (string sub in subs)
                {
                    yield return sub;
                    if (cur.Item2 + 1 < maxDepth) queue.Enqueue(Tuple.Create(sub, cur.Item2 + 1));
                }
            }
        }
    }
}
