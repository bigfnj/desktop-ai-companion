using System;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Runs every engine self-test on committed, IP-free fixtures. This is what the CLI `selftest` verb and
    /// run-gate.ps1 invoke; each sub-test is self-contained and never touches an external Shimeji clone.
    ///
    /// Each sub-test runs inside its own guard: one that throws is reported as a named FAIL line and the ones
    /// after it still run, where before the exception unwound to the runtime and replaced every later report
    /// with a stack trace (F452). The number of sub-tests that ran travels out as well, printed by the CLI with
    /// the same SELFTEST-COUNT sentinel tests/Invoke-SelfTests.ps1 emits, so a deleted registration line is a
    /// number that changed rather than a quieter PASS.
    /// </summary>
    public static class EngineSelfTest
    {
        private delegate bool SubTest(out string detail);

        /// <summary>The sentinel the CLI prints after the detail; a gate parses the number that follows it.</summary>
        public const string CountPrefix = "SELFTEST-COUNT: ";

        public static bool RunAll(out string detail)
        {
            int ran;
            return RunAll(out detail, out ran);
        }

        public static bool RunAll(out string detail, out int ran)
        {
            var sb = new StringBuilder();
            bool ok = true;
            ran = 0;
            var suites = new[]
            {
                new { Name = "ClassifierSelfTest", Run = (SubTest)ClassifierSelfTest.Run },
                new { Name = "CompositorSelfTest", Run = (SubTest)CompositorSelfTest.Run },
                new { Name = "EmitterSelfTest", Run = (SubTest)EmitterSelfTest.Run },
                new { Name = "HubWeightSelfTest", Run = (SubTest)HubWeightSelfTest.Run },
                new { Name = "BundledConfSelfTest", Run = (SubTest)BundledConfSelfTest.Run },
                new { Name = "BundleSelfTest", Run = (SubTest)BundleSelfTest.Run },
                new { Name = "VocabSelfTest", Run = (SubTest)VocabSelfTest.Run },
                new { Name = "SoundResolveSelfTest", Run = (SubTest)SoundResolveSelfTest.Run },
                new { Name = "PetGraphSelfTest", Run = (SubTest)PetGraphSelfTest.Run },
            };
            foreach (var suite in suites)
            {
                string d;
                bool suiteOk;
                try { suiteOk = suite.Run(out d); }
                catch (Exception ex)
                {
                    suiteOk = false;
                    d = "FAIL " + suite.Name + " threw " + ex.GetType().Name + ": " + ex.Message;
                }
                ran++;
                if (!suiteOk) ok = false;
                sb.AppendLine(d);
            }

            detail = sb.ToString().TrimEnd();
            return ok;
        }
    }
}
