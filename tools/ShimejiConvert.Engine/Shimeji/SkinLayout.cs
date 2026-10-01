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
    ///   * each img folder = a folder containing shimeN-style PNGs (root/img/*, or the root itself);
    ///   * each img folder's conf = resolved PER SKIN in the reference loader's order (its own conf/, the
    ///     root's conf/&lt;Skin&gt;/, then up the tree toward the root), and only then the root-wide search.
    /// A skin with NO actions file IS converted here, using the bundled base conf: such a skin is
    /// returned with <see cref="DetectedSkin.UsesBundledConf"/> set, and ShimejiParser.ParseBundledConf
    /// supplies the behaviour. Only a skin with no sprite folder at all returns no skins, with a reason.
    ///
    /// PER SKIN, not per root (RA-386). One conf used to be found for the whole root and stamped on every
    /// sprite folder, so in a multi-character pack the richest character's sprites converted against
    /// whichever actions.xml the search met first: the maintainer's own Gengar archive paired Gengar_Shiny's
    /// sprites with Gastly_Egg's conf and failed on "Sprite not found: egg1.png", Hornet's paired Hornet with
    /// .Hornet_Needle, and 37 of the 56 multi-conf archives in the corpus mis-paired the same way. And a
    /// sibling-install pack (Alice/conf + Alice/img, Bob/conf + Bob/img) named every skin after the ROOT,
    /// because each sprite folder's leaf is literally "img"; on PetStudio's zip path the root is
    /// %TEMP%\petstudio-shimeji-&lt;guid&gt;, which is what the user then saw. Both halves resolve from the
    /// sprite folder outward now. Intended consequence, recorded in the design register: a Shimeji-EE pack
    /// carrying a root conf AND an img/&lt;Skin&gt;/conf override converts against the override, as
    /// Shimeji-EE itself does.
    ///
    /// This paragraph previously said the opposite -- that such a skin "cannot be converted here"
    /// because the base conf "is copyrighted and this repo does not ship" it. All three halves were
    /// false, and the licensing one is the reason it is worth correcting rather than deleting: the repo
    /// DOES ship tools/ShimejiConvert.Engine/base-conf/{actions,behaviors}.xml, deliberately, and they
    /// are redistributable. They are the Shimeji-EE defaults under a 3-clause BSD licence, included
    /// unmodified with attribution, precisely so that a sprites-only skin converts without the user
    /// supplying a conf. See base-conf/NOTICE.txt, which also records the one thing that genuinely is
    /// never shipped: sprite ART, which stays the skin author's copyright.
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

            // Each sprite folder's OWN conf first (its ancestry, the reference loader's rule), then the
            // root-wide search for whatever is left -- unless that search lands on a conf another sprite folder
            // just resolved as its own. That exclusion is what the mixed pack needs: Erin/conf + Erin/img beside
            // a sprites-only Frank/img used to hand Frank Erin's actions, which is the mis-pairing this method
            // exists to stop, wearing the fallback's clothes. A conf shared through a common ancestor (root/conf
            // over img/A and img/B) is not affected, because both folders resolve it as their own.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var imgDirs = new List<string>(FindImgDirs(rootDir));
            var ownConf = new List<string>();
            foreach (string imgDir in imgDirs)
            {
                string confDir = FindConfDirFor(rootDir, imgDir);
                ownConf.Add(confDir);
                if (confDir != null) claimed.Add(FullPathOrSelf(confDir));
            }
            string rootConf = null;
            bool rootConfSearched = false;
            for (int i = 0; i < imgDirs.Count; i++)
            {
                string confDir = ownConf[i];
                if (confDir == null)
                {
                    if (!rootConfSearched) { rootConf = FindConfDir(rootDir); rootConfSearched = true; }
                    confDir = rootConf != null && claimed.Contains(FullPathOrSelf(rootConf)) ? null : rootConf;
                }
                skins.Add(new DetectedSkin
                {
                    Name = SkinName(rootDir, imgDirs[i]),
                    ConfDir = confDir,
                    ImgDir = imgDirs[i],
                    UsesBundledConf = confDir == null,
                });
            }

            if (skins.Count == 0)
            {
                if (!rootConfSearched) rootConf = FindConfDir(rootDir);
                note = rootConf == null
                    ? "found no sprites (looked for *.png) and no actions file (" + string.Join(", ", ShimejiParser.ActionsFileNames) + "). Point at a Shimeji skin folder."
                    : "found a conf but no sprite folder (looked for *.png). Point at the skin's img folder.";
                return skins;
            }
            // Per skin, like the conf itself: a pack can mix a conf'd character with a sprites-only one, and
            // a note about "this skin" that described the whole root was how a skin with conf/動作.xml came
            // to be told it had no behaviour config of its own (RA-385).
            var bundledNames = new List<string>();
            foreach (DetectedSkin s in skins) if (s.UsesBundledConf) bundledNames.Add(s.Name);
            if (bundledNames.Count == skins.Count)
                note = "this skin has no behaviour config of its own; the bundled Shimeji base behaviour will be used.";
            else if (bundledNames.Count > 0)
                note = bundledNames.Count + " of " + skins.Count + " skins have no behaviour config of their own and would use the bundled Shimeji base behaviour: " + string.Join(", ", bundledNames) + ".";
            return skins;
        }

        /// <summary>
        /// The conf that belongs to ONE sprite folder, in the order the reference loader resolves an image
        /// set's conf: the folder's own conf/ (img/&lt;Skin&gt;/conf), the root's conf/&lt;Skin&gt;/, then each
        /// ancestor up to and including the root, testing &lt;dir&gt;/conf and &lt;dir&gt; itself (the sibling-install
        /// shape, Alice/conf beside Alice/img). Null when none of those holds an actions file; the caller then
        /// falls back to the root-wide search, which is the only place a conf unrelated to any sprite folder's
        /// ancestry is still accepted.
        /// </summary>
        private static string FindConfDirFor(string root, string imgDir)
        {
            string fullRoot, fullImg;
            try { fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); fullImg = Path.GetFullPath(imgDir).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return null; }

            string own = Path.Combine(fullImg, "conf");
            if (HasActionsFile(own)) return own;
            string leaf = new DirectoryInfo(fullImg).Name;
            if (!string.Equals(leaf, "img", StringComparison.OrdinalIgnoreCase))
            {
                string perSet = Path.Combine(fullRoot, "conf", leaf);
                if (HasActionsFile(perSet)) return perSet;
            }
            // Up the tree, from the sprite folder itself to the root inclusive, testing <dir>/conf and then
            // <dir>: the sprite folder's parent is the character's own install in a sibling pack, and the
            // root is the last ancestor consulted (root/conf, then root itself), so a plain single skin
            // resolves exactly where it always did. Bounded so a folder outside the root cannot climb for ever.
            string dir = fullImg;
            for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
            {
                string conf = Path.Combine(dir, "conf");
                if (HasActionsFile(conf)) return conf;
                if (HasActionsFile(dir)) return dir;
                if (string.Equals(dir, fullRoot, StringComparison.OrdinalIgnoreCase)) break;
                if (dir.Length <= fullRoot.Length) break;   // not under the root at all: stop
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static string FullPathOrSelf(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return dir; }
        }

        private static bool HasActionsFile(string dir)
        {
            try { return Directory.Exists(dir) && ShimejiParser.FindActionsFile(dir) != null; }
            catch { return false; }
        }

        private static string FindConfDir(string root)
        {
            if (HasActionsFile(root)) return root;
            string conf = Path.Combine(root, "conf");
            if (HasActionsFile(conf)) return conf;
            // shallow search (root's descendants, capped depth) for any actions file
            foreach (string dir in EnumerateDirs(root, 3))
                if (HasActionsFile(dir)) return dir;
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
            if (string.Equals(imgDir, root, StringComparison.OrdinalIgnoreCase))
                return new DirectoryInfo(root).Name;
            if (!string.Equals(leaf, "img", StringComparison.OrdinalIgnoreCase))
                return leaf;
            // A folder literally called "img" is named after what OWNS it: root/img is the root's own
            // sprites and takes the root's name as before, while Pack/Alice/img is Alice's (RA-386). Without
            // this a pack of sibling installs was N skins all called after the root.
            DirectoryInfo parent = new DirectoryInfo(imgDir).Parent;
            if (parent != null)
            {
                string fullParent, fullRoot;
                try { fullParent = Path.GetFullPath(parent.FullName).TrimEnd(Path.DirectorySeparatorChar); fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); }
                catch { fullParent = null; fullRoot = null; }
                if (fullParent != null && !string.Equals(fullParent, fullRoot, StringComparison.OrdinalIgnoreCase))
                    return parent.Name;
            }
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
