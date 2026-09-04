using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free unit test of the parser + classifier. The real gil/shimeji-ee config cannot live in
    /// this repo (it is copyrighted, and the handoff forbids it), so the gate cannot assert the 91/53/32/6
    /// census against it. Instead this exercises every classification branch with a hand-written synthetic
    /// actions.xml whose actions are named "G1_", "G2_" or "G3_" after the group they must land in, and
    /// asserts each one buckets correctly. The 91/53/32/6 validation against the actual reference config is a
    /// dev step: `ShimejiConvert classify &lt;conf-dir&gt;` against an external clone.
    /// </summary>
    public static class ClassifierSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            ShimejiConfig config;
            try
            {
                config = ShimejiParser.ParseActionsXml(SyntheticActionsXml);
            }
            catch (Exception ex)
            {
                detail = "parser threw: " + ex.Message;
                return false;
            }

            // Every action name encodes its expected group as the first two characters (G1/G2/G3).
            int checkedCount = 0;
            foreach (ShimejiAction a in config.Actions)
            {
                if (string.IsNullOrEmpty(a.Name) || a.Name.Length < 2 || a.Name[0] != 'G')
                {
                    failures.Add("action '" + a.Name + "' is not named G1_/G2_/G3_");
                    continue;
                }
                FidelityGroup expected;
                switch (a.Name[1])
                {
                    case '1': expected = FidelityGroup.Group1; break;
                    case '2': expected = FidelityGroup.Group2; break;
                    case '3': expected = FidelityGroup.Group3; break;
                    default: failures.Add("action '" + a.Name + "' has no valid group digit"); continue;
                }
                checkedCount++;
                if (a.Group != expected)
                    failures.Add(string.Format("{0}: expected {1} but got {2} ({3})", a.Name, expected, a.Group, a.Reason));
            }

            // Guard against a fixture that silently stopped parsing: it must cover all three groups.
            if (checkedCount < 12)
                failures.Add("expected at least 12 classified actions from the fixture, got " + checkedCount);

            var sb = new StringBuilder();
            sb.AppendLine("classifier self-test: " + checkedCount + " synthetic actions across Group1/2/3");
            if (failures.Count == 0)
            {
                sb.Append("  all classified as named");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        // One action per classification branch. Names carry the expected group. Kept minimal and clearly
        // synthetic -- this is our content, not Shimeji's.
        private const string SyntheticActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""G1_Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation><Pose Image=""/a.png"" ImageAnchor=""64,128"" Velocity=""-2,0"" Duration=""6"" /></Animation>
    </Action>
    <Action Name=""G1_GrabCeiling"" Type=""Stay"" BorderType=""Ceiling"">
      <Animation><Pose Image=""/b.png"" ImageAnchor=""64,48"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G1_Look"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Look"" />
    <Action Name=""G1_Offset"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Offset"" />
    <Action Name=""G1_Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/c.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G1_Dragged"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Dragged"">
      <Animation><Pose Image=""/d.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""5"" /></Animation>
    </Action>
    <Action Name=""G1_Jumping"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Jump"" VelocityParam=""20"">
      <Animation><Pose Image=""/e.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G2_Cursor"" Type=""Stay"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.environment.cursor.x &lt; 100}""><Pose Image=""/f.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""G2_Ie"" Type=""Sequence"">
      <ActionReference Name=""G1_Walk"" TargetX=""${mascot.environment.activeIE.left}"" />
    </Action>
    <Action Name=""G2_BreedCap"" Type=""Sequence"" Condition=""#{mascot.totalCount &lt; 50}"">
      <ActionReference Name=""G1_Walk"" />
    </Action>
    <Action Name=""G2_Anchor"" Type=""Sequence"" Condition=""#{mascot.anchor.x &lt; 400}"">
      <ActionReference Name=""G1_Walk"" />
    </Action>
    <Action Name=""G3_ThrowIe"" Type=""Embedded"" Class=""com.group_finity.mascot.action.ThrowIE"" InitialVX=""32"">
      <Animation><Pose Image=""/g.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""40"" /></Animation>
    </Action>
    <Action Name=""G3_Breed"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Breed"" BornBehavior=""PullUp"">
      <Animation><Pose Image=""/h.png"" ImageAnchor=""64,128"" Velocity=""0,0"" Duration=""16"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";
    }
}
