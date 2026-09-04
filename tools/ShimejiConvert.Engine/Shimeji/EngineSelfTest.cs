using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Runs every engine self-test on committed, IP-free fixtures. This is what the CLI `selftest` verb and
    /// run-gate.ps1 invoke; each sub-test is self-contained and never touches an external Shimeji clone.
    /// </summary>
    public static class EngineSelfTest
    {
        public static bool RunAll(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            string d;

            if (!ClassifierSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!CompositorSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!EmitterSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!HubWeightSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!BundledConfSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!BundleSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!VocabSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            detail = sb.ToString().TrimEnd();
            return ok;
        }
    }
}
