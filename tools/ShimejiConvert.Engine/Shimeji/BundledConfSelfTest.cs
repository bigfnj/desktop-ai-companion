using System;
using System.Linq;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Verifies the bundled Shimeji base conf embeds and parses, so a sprites-only skin (no conf of its own)
    /// can convert against it. The bundled conf IS the gil/shimeji-ee reference config, so this also pins
    /// that it is intact: 91 actions grouping 54 Group1 / 31 Group2 / 6 Group3.
    ///
    /// The census is a PIN, not a target: it exists to make a classification change impossible to ship
    /// unnoticed, and it has earned that once already. It moved 53/32/6 -> 54/31/6 on 2026-08-28, when
    /// ClimbWall stopped being reported as needing selfX/selfY. Its condition (#{TargetY <
    /// mascot.anchor.y}) is a loop-continuation test that the emitter's border-driven graph already answers,
    /// so it is a Group1 deterministic map and always was. Update this deliberately, with the reason, or not
    /// at all.
    /// </summary>
    public static class BundledConfSelfTest
    {
        public static bool Run(out string detail)
        {
            ShimejiConfig cfg;
            try { cfg = ShimejiParser.ParseBundledConf(); }
            catch (Exception ex) { detail = "bundled-conf self-test: ParseBundledConf threw -- " + ex.Message; return false; }

            int total = cfg.Actions.Count;
            int g1 = cfg.Actions.Count(a => a.Group == FidelityGroup.Group1);
            int g2 = cfg.Actions.Count(a => a.Group == FidelityGroup.Group2);
            int g3 = cfg.Actions.Count(a => a.Group == FidelityGroup.Group3);

            var sb = new StringBuilder();
            sb.AppendLine("bundled-conf self-test: base actions.xml + behaviors.xml embed and parse");
            if (total == 91 && g1 == 54 && g2 == 31 && g3 == 6)
            {
                sb.Append("  91 actions (54/31/6) -- the reference census, intact");
                detail = sb.ToString();
                return true;
            }
            sb.AppendLine(string.Format("  FAIL expected 91 actions (54/31/6), got {0} ({1}/{2}/{3})", total, g1, g2, g3));
            detail = sb.ToString();
            return false;
        }
    }
}
