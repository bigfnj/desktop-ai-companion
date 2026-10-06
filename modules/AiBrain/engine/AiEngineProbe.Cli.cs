using System;
using System.Text;
using DesktopAICompanion.CodingAgent;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Lane feature/cli-backend (aibrain 1.3.0): the coding-agent CLI backend. First the shared runner's own self-check
    /// (shared/CodingAgentCli, run by Remembrance's self-test too, so each payload proves the copy it ships), then this
    /// module's use of it. No CLI is started, no model is called and no screen is captured: the runner's process seam is
    /// a fake, every binary is an empty file in a scratch tree, and every image is a one-pixel PNG.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunCli(StringBuilder sb)
        {
            bool ok = true;
            CodingAgentCliSelfCheck.Run(delegate(string name, bool condition) { ok &= Check(sb, name, condition); });
            return ok;
        }
    }
}
