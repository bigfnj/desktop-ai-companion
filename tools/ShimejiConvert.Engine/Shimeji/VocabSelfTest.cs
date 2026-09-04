using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// The parser must read the official Japanese XML vocabulary (ポーズ / 画像 / 基準座標 / 種類=組み込み /
    /// 枠=地面 ...), not just English. A Japanese skin used to parse to zero poses and fail compositing with
    /// "no sprite poses to composite". This asserts a Japanese actions.xml parses to the same shape an English
    /// one would, including the canonicalised Type/BorderType enum VALUES the classifier keys on.
    /// </summary>
    public static class VocabSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();
            try
            {
                ShimejiConfig cfg = ShimejiParser.ParseActionsXml(JapaneseActionsXml);

                if (cfg.Actions.Count != 3)
                    failures.Add("expected 3 Japanese actions, got " + cfg.Actions.Count);
                if (cfg.Poses.Count < 4)
                    failures.Add("expected >=4 poses from Japanese <" + "ポーズ" + ">, got " + cfg.Poses.Count);

                ShimejiPose first = cfg.Poses.FirstOrDefault();
                if (first == null || first.Image != "/shime1.png")
                    failures.Add("Image attribute did not map (" + (first == null ? "no pose" : first.Image) + ")");
                if (first != null && (first.AnchorX != 64 || first.AnchorY != 128))
                    failures.Add("ImageAnchor did not map (" + (first == null ? "" : first.AnchorX + "," + first.AnchorY) + ")");

                if (!cfg.Actions.Any(a => a.BorderType == "Floor"))
                    failures.Add("BorderType value did not canonicalise to Floor");
                if (!cfg.Actions.Any(a => a.Type == "Move"))
                    failures.Add("Type value did not canonicalise to Move");

                ShimejiAction fall = cfg.Actions.FirstOrDefault(a => a.Class == "Fall");
                if (fall == null) failures.Add("embedded Fall action not recognised (Class=Fall)");
                else if (fall.Group != FidelityGroup.Group1) failures.Add("Japanese Fall not classified Group1");
            }
            catch (Exception ex) { failures.Add("threw: " + ex.Message); }

            var sb = new StringBuilder();
            sb.AppendLine("vocab self-test: Japanese XML vocabulary parses like English");
            if (failures.Count == 0)
            {
                sb.Append("  3 actions, 4 poses, Floor + Move + Fall recognised across vocabularies");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private const string JapaneseActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <動作リスト>
    <動作 名前=""立つ"" 種類=""静止"" 枠=""地面"">
      <アニメーション><ポーズ 画像=""/shime1.png"" 基準座標=""64,128"" 移動速度=""0,0"" 長さ=""250"" /></アニメーション>
    </動作>
    <動作 名前=""歩く"" 種類=""移動"" 枠=""地面"">
      <アニメーション>
        <ポーズ 画像=""/shime2.png"" 基準座標=""64,128"" 移動速度=""-2,0"" 長さ=""6"" />
        <ポーズ 画像=""/shime3.png"" 基準座標=""64,128"" 移動速度=""-2,0"" 長さ=""6"" />
      </アニメーション>
    </動作>
    <動作 名前=""落ちる"" 種類=""組み込み"" クラス=""com.group_finity.mascot.action.Fall"" 重力=""2"">
      <アニメーション><ポーズ 画像=""/shime4.png"" 基準座標=""64,128"" 移動速度=""0,0"" 長さ=""250"" /></アニメーション>
    </動作>
  </動作リスト>
</Mascot>";
    }
}
