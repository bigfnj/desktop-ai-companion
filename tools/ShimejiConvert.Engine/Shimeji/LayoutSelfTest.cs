using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// The two DIRECTORY entry points, SkinLayout.Detect and ShimejiParser.ParseConfDirectory, against the
    /// layouts real downloads ship. Every other parser test hands the parser text; nothing walked a folder,
    /// so both entry points keyed on the one English leaf name while the content parser read three
    /// vocabularies (RA-385), and Detect found one conf for a whole root and stamped it on every sprite folder
    /// (RA-386). Committed and IP-free: the confs are a few synthetic actions, the sprites one-colour PNGs,
    /// every tree is built under a private temp directory and removed in a finally.
    /// </summary>
    public static class LayoutSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();
            string root = Path.Combine(Path.GetTempPath(), "dp-layout-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);

                // ---- JAPANESE FILE NAMES: conf/動作.xml + conf/行動.xml ----
                // The original group-finity layout, 74 of the 2779 archives in the maintainer's corpus. Both
                // the parse and the detection must see the conf, or the skin converts against the bundled base
                // conf under a note saying it has no behaviour config of its own.
                string jpRoot = Path.Combine(root, "Japanese");
                string jpConf = Path.Combine(jpRoot, "conf");
                Directory.CreateDirectory(jpConf);
                WriteUtf8(Path.Combine(jpConf, "動作.xml"), JapaneseActionsXml);
                WriteUtf8(Path.Combine(jpConf, "行動.xml"), JapaneseBehavioursXml);
                WriteSprites(Path.Combine(jpRoot, "img"), 4);
                try
                {
                    ShimejiConfig jp = ShimejiParser.ParseConfDirectory(jpConf);
                    if (jp.Actions.Count != 3)
                        failures.Add("conf/動作.xml parsed to " + jp.Actions.Count + " actions, expected 3");
                    int walk;
                    jp.BehaviorFrequency.TryGetValue("歩く", out walk);
                    if (walk != 60)
                        failures.Add("conf/行動.xml was not read beside 動作.xml: 歩く has frequency " + walk + ", expected 60");
                    if (jp.BehaviorsFile == null)
                        failures.Add("the config does not record which behaviours file was read (BehaviorsFile is null)");
                }
                catch (Exception ex)
                {
                    failures.Add("ParseConfDirectory refused a conf directory holding 動作.xml + 行動.xml: " + ex.Message);
                }
                string jpNote;
                List<DetectedSkin> jpSkins = SkinLayout.Detect(jpRoot, out jpNote);
                if (jpSkins.Count != 1)
                    failures.Add("Detect found " + jpSkins.Count + " skin(s) in the Japanese-named layout, expected 1");
                else
                {
                    if (jpSkins[0].UsesBundledConf || jpSkins[0].ConfDir == null)
                        failures.Add("Detect reported the skin with conf/動作.xml as having no conf of its own, so it would "
                            + "convert against the bundled base conf");
                    else if (!SamePath(jpSkins[0].ConfDir, jpConf))
                        failures.Add("Detect paired the Japanese-named conf with '" + jpSkins[0].ConfDir + "', expected " + jpConf);
                    if (jpNote != null && jpNote.IndexOf("no behaviour config", StringComparison.Ordinal) >= 0)
                        failures.Add("Detect's note says the Japanese-named skin has no behaviour config of its own: " + jpNote);
                }

                // ---- THE SINGULAR Behavior.xml BESIDE actions.xml ----
                // 21 corpus archives; the frequencies used to vanish in silence, the F442 symptom on the path
                // F442 left alone.
                string singularConf = Path.Combine(root, "Singular", "conf");
                Directory.CreateDirectory(singularConf);
                WriteUtf8(Path.Combine(singularConf, "actions.xml"), EnglishActionsXml);
                WriteUtf8(Path.Combine(singularConf, "Behavior.xml"), EnglishBehavioursXml);
                ShimejiConfig singular = ShimejiParser.ParseConfDirectory(singularConf);
                int singularWalk;
                singular.BehaviorFrequency.TryGetValue("Walk", out singularWalk);
                if (singularWalk != 100)
                    failures.Add("Behavior.xml (singular) beside actions.xml was not read: Walk has frequency " + singularWalk + ", expected 100");
                // WITNESS: with no behaviours file at all the frequencies are empty and the config says so, so the
                // assertion above is reading the file and not a default.
                string bareConf = Path.Combine(root, "Bare", "conf");
                Directory.CreateDirectory(bareConf);
                WriteUtf8(Path.Combine(bareConf, "actions.xml"), EnglishActionsXml);
                ShimejiConfig bare = ShimejiParser.ParseConfDirectory(bareConf);
                if (bare.BehaviorFrequency.Count != 0 || bare.BehaviorsFile != null)
                    failures.Add("WITNESS: a conf with no behaviours file parsed " + bare.BehaviorFrequency.Count
                        + " frequencies (BehaviorsFile=" + (bare.BehaviorsFile ?? "null") + "), so the singular-file assertion proves nothing");

                // ---- A PACK OF SIBLING INSTALLS: Alice/conf + Alice/img, Bob/conf + Bob/img ----
                // Bob has more sprites, so his folder ranks first. Each skin must carry ITS OWN conf and ITS OWN
                // name: the whole-root resolution paired Bob's sprites with Alice's conf, and every leaf being
                // "img" named both skins after the root.
                string pack = Path.Combine(root, "Pack");
                string aliceConf = Path.Combine(pack, "Alice", "conf");
                string bobConf = Path.Combine(pack, "Bob", "conf");
                Directory.CreateDirectory(aliceConf);
                Directory.CreateDirectory(bobConf);
                WriteUtf8(Path.Combine(aliceConf, "actions.xml"), EnglishActionsXml);
                WriteUtf8(Path.Combine(bobConf, "actions.xml"), EnglishActionsXml);
                WriteSprites(Path.Combine(pack, "Alice", "img"), 3);
                WriteSprites(Path.Combine(pack, "Bob", "img"), 5);
                string packNote;
                List<DetectedSkin> packSkins = SkinLayout.Detect(pack, out packNote);
                if (packSkins.Count != 2)
                    failures.Add("Detect found " + packSkins.Count + " skin(s) in the sibling pack, expected 2");
                else
                {
                    DetectedSkin first = packSkins[0], second = packSkins[1];
                    if (first.Name != "Bob" || second.Name != "Alice")
                        failures.Add("the sibling pack's skins are named '" + first.Name + "' and '" + second.Name
                            + "', expected Bob (richer, first) and Alice: a sprite folder called img is named after the "
                            + "character that owns it, not after the root");
                    if (!SamePath(first.ConfDir, bobConf))
                        failures.Add("Bob's sprites were paired with '" + (first.ConfDir ?? "(bundled)") + "', expected his own conf "
                            + bobConf + ": one conf for the whole root pairs the richest character with another's actions");
                    if (!SamePath(second.ConfDir, aliceConf))
                        failures.Add("Alice's sprites were paired with '" + (second.ConfDir ?? "(bundled)") + "', expected " + aliceConf);
                    if (first.UsesBundledConf || second.UsesBundledConf)
                        failures.Add("a sibling install with its own conf was marked as using the bundled conf");
                }

                // ---- SHIMEJI-EE PER-SET OVERRIDE: root/conf plus img/Carol/conf, img/Dave without ----
                // The reference loader resolves img/<set>/conf before ./conf. Carol converts against her own
                // override; Dave, who has none, against the root's. Recorded in the design register as the
                // intended change: the whole-root rule used to give Carol the root conf.
                string ee = Path.Combine(root, "ShimejiEE");
                string eeRootConf = Path.Combine(ee, "conf");
                string carolConf = Path.Combine(ee, "img", "Carol", "conf");
                Directory.CreateDirectory(eeRootConf);
                Directory.CreateDirectory(carolConf);
                WriteUtf8(Path.Combine(eeRootConf, "actions.xml"), EnglishActionsXml);
                WriteUtf8(Path.Combine(carolConf, "actions.xml"), EnglishActionsXml);
                WriteSprites(Path.Combine(ee, "img", "Carol"), 3);
                WriteSprites(Path.Combine(ee, "img", "Dave"), 2);
                string eeNote;
                List<DetectedSkin> eeSkins = SkinLayout.Detect(ee, out eeNote);
                DetectedSkin carol = eeSkins.Find(delegate(DetectedSkin s) { return s.Name == "Carol"; });
                DetectedSkin dave = eeSkins.Find(delegate(DetectedSkin s) { return s.Name == "Dave"; });
                if (carol == null || dave == null)
                    failures.Add("the img/<Skin> layout did not yield skins named Carol and Dave (got "
                        + string.Join(", ", eeSkins.ConvertAll(delegate(DetectedSkin s) { return s.Name; }).ToArray()) + ")");
                else
                {
                    if (!SamePath(carol.ConfDir, carolConf))
                        failures.Add("Carol has her own img/Carol/conf but was paired with '" + (carol.ConfDir ?? "(bundled)")
                            + "'; the per-set override must win over the root conf, as in Shimeji-EE");
                    if (!SamePath(dave.ConfDir, eeRootConf))
                        failures.Add("Dave has no conf of his own and was paired with '" + (dave.ConfDir ?? "(bundled)")
                            + "', expected the root's " + eeRootConf);
                }

                // ---- A MIXED PACK: one character with a conf, one with sprites only ----
                // UsesBundledConf is decided per skin, and the note names the one that will use the base conf.
                string mixed = Path.Combine(root, "Mixed");
                string erinConf = Path.Combine(mixed, "Erin", "conf");
                Directory.CreateDirectory(erinConf);
                WriteUtf8(Path.Combine(erinConf, "actions.xml"), EnglishActionsXml);
                WriteSprites(Path.Combine(mixed, "Erin", "img"), 3);
                WriteSprites(Path.Combine(mixed, "Frank", "img"), 2);
                string mixedNote;
                List<DetectedSkin> mixedSkins = SkinLayout.Detect(mixed, out mixedNote);
                DetectedSkin erin = mixedSkins.Find(delegate(DetectedSkin s) { return s.Name == "Erin"; });
                DetectedSkin frank = mixedSkins.Find(delegate(DetectedSkin s) { return s.Name == "Frank"; });
                if (erin == null || frank == null)
                    failures.Add("the mixed pack did not yield skins named Erin and Frank");
                else
                {
                    if (erin.UsesBundledConf) failures.Add("Erin has a conf and was marked as using the bundled one");
                    if (!frank.UsesBundledConf) failures.Add("Frank has no conf anywhere above his sprites and was not marked as using the bundled one");
                    if (mixedNote == null || mixedNote.IndexOf("Frank", StringComparison.Ordinal) < 0)
                        failures.Add("the note for a mixed pack does not name the skin that will use the bundled conf: " + (mixedNote ?? "(none)"));
                }

                // ---- WITNESS: a plain single skin still resolves and is still named as it always was ----
                string plain = Path.Combine(root, "Plain");
                string plainConf = Path.Combine(plain, "conf");
                Directory.CreateDirectory(plainConf);
                WriteUtf8(Path.Combine(plainConf, "actions.xml"), EnglishActionsXml);
                WriteSprites(Path.Combine(plain, "img"), 2);
                string plainNote;
                List<DetectedSkin> plainSkins = SkinLayout.Detect(plain, out plainNote);
                if (plainSkins.Count != 1 || plainSkins[0].Name != "Plain" || !SamePath(plainSkins[0].ConfDir, plainConf))
                    failures.Add("WITNESS: the plain root/conf + root/img layout no longer detects as one skin named after the root "
                        + "with the root's conf (got " + plainSkins.Count + " skin(s)"
                        + (plainSkins.Count > 0 ? ", '" + plainSkins[0].Name + "' -> " + (plainSkins[0].ConfDir ?? "(bundled)") : "") + ")");
            }
            catch (Exception ex) { failures.Add("threw: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception ex) { failures.Add("DEGRADED: could not remove the test tree " + root + " (" + ex.Message + ")"); }
            }

            var sb = new StringBuilder();
            sb.AppendLine("layout self-test: conf files under every accepted name; one conf per sprite folder, named after its owner");
            if (failures.Count == 0)
            {
                sb.Append("  動作.xml + 行動.xml and actions.xml + Behavior.xml both read; a sibling pack, a per-set override and a mixed "
                    + "pack each resolve per skin; the plain layout is unchanged");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                                     Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void WriteUtf8(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>shime1..shimeN.png, one solid colour each, in <paramref name="dir"/>.</summary>
        private static void WriteSprites(string dir, int count)
        {
            Directory.CreateDirectory(dir);
            for (int i = 1; i <= count; i++)
            {
                using (var bmp = new Bitmap(8, 12, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.Clear(Color.FromArgb(255, 40 * i, 120, 200 - 20 * i));
                    }
                    bmp.Save(Path.Combine(dir, "shime" + i + ".png"), ImageFormat.Png);
                }
            }
        }

        private const string EnglishActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/shime1.png"" ImageAnchor=""4,12"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/shime1.png"" ImageAnchor=""4,12"" Velocity=""-2,0"" Duration=""6"" />
        <Pose Image=""/shime2.png"" ImageAnchor=""4,12"" Velocity=""-2,0"" Duration=""6"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/shime2.png"" ImageAnchor=""4,12"" Velocity=""0,2"" Duration=""4"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";

        private const string EnglishBehavioursXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <BehaviorList>
    <Behavior Name=""Stand"" Frequency=""40"" />
    <Behavior Name=""Walk"" Frequency=""100"" />
  </BehaviorList>
</Mascot>";

        // The official Japanese vocabulary, which VocabSelfTest already proves the CONTENT parser reads; here
        // it sits under the Japanese FILE names, which is the half that was never exercised.
        private const string JapaneseActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<マスコット xmlns=""http://www.group-finity.com/Mascot"">
  <動作リスト>
    <動作 名前=""立つ"" 種類=""静止"" 枠=""地面"">
      <アニメーション><ポーズ 画像=""/shime1.png"" 基準座標=""4,12"" 移動速度=""0,0"" 長さ=""250"" /></アニメーション>
    </動作>
    <動作 名前=""歩く"" 種類=""移動"" 枠=""地面"">
      <アニメーション>
        <ポーズ 画像=""/shime2.png"" 基準座標=""4,12"" 移動速度=""-2,0"" 長さ=""6"" />
        <ポーズ 画像=""/shime3.png"" 基準座標=""4,12"" 移動速度=""-2,0"" 長さ=""6"" />
      </アニメーション>
    </動作>
    <動作 名前=""落下"" 種類=""組み込み"" クラス=""com.group_finity.mascot.action.Fall"" 重力=""2"">
      <アニメーション><ポーズ 画像=""/shime4.png"" 基準座標=""4,12"" 移動速度=""0,2"" 長さ=""4"" /></アニメーション>
    </動作>
  </動作リスト>
</マスコット>";

        private const string JapaneseBehavioursXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<マスコット xmlns=""http://www.group-finity.com/Mascot"">
  <行動リスト>
    <行動 名前=""立つ"" 頻度=""40"" />
    <行動 名前=""歩く"" 頻度=""60"" />
  </行動リスト>
</マスコット>";
    }
}
