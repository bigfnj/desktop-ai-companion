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
    ///
    /// The BEHAVIOURS half is pinned the same way: 47 root behaviours carry a Frequency > 0 (summed per
    /// name, the way ParseBehaviorFrequencies reads them), SitDown is 200, and at least one selection
    /// condition parsed. Until 2026-09-29 the line this prints named behaviors.xml while nothing asserted it:
    /// the resource was loaded as optional, and with it renamed the census stayed 91/54/31/6 and the test
    /// printed success over a conf whose every hub spoke would sit at the base weight (F442).
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
            bool censusOk = total == 91 && g1 == 54 && g2 == 31 && g3 == 6;

            int weighted = cfg.BehaviorFrequency.Count;
            int conditions = cfg.BehaviorConditions.Count;
            int sitDown;
            cfg.BehaviorFrequency.TryGetValue("SitDown", out sitDown);
            bool behavioursOk = weighted == 47 && sitDown == 200 && conditions > 0;

            var sb = new StringBuilder();
            sb.AppendLine("bundled-conf self-test: base actions.xml + behaviors.xml embed and parse");
            if (censusOk && behavioursOk)
            {
                sb.Append("  91 actions (54/31/6) -- the reference census, intact; " + weighted +
                          " weighted root behaviours (SitDown=" + sitDown + "), " + conditions + " selection conditions");
                detail = sb.ToString();
                return true;
            }
            if (!censusOk)
                sb.AppendLine(string.Format("  FAIL expected 91 actions (54/31/6), got {0} ({1}/{2}/{3})", total, g1, g2, g3));
            if (!behavioursOk)
                sb.AppendLine(string.Format("  FAIL expected 47 weighted root behaviours with SitDown=200 and at least one " +
                                            "selection condition from behaviors.xml, got {0} weighted, SitDown={1}, {2} conditions",
                                            weighted, sitDown, conditions));
            detail = sb.ToString();
            return false;
        }
    }
}
