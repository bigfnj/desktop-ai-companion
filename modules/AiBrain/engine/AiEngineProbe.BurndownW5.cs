using System;
using System.Net;
using System.Text;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Burn-down lane w5-aibrain-1 (aibrain 1.7.1, wave 5): Test connection on the Cloud provider card names a base URL that
    /// answers with a redirect as a redirect from its host, where it said the host was not reachable (N-bd-w4-aibrain-1-01).
    /// No server, no model: the press goes through the module's ConnectionTestBackendForDiagnostics seam to the real
    /// OpenAiCompatBackend over a handler double, so the probe's own refusal of the redirect, and the reason it keeps, are
    /// the ones that run. The custom base URL is on loopback, so a press that ever missed the seam reaches a refused port on
    /// this computer and nothing further.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckBurndownW5Lane(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckBurndownW5RedirectedTest", CheckBurndownW5RedirectedTest);
            return ok;
        }

        /// <summary>N-bd-w4-aibrain-1-01: the Cloud provider card's Test connection against a base URL that redirects says
        /// HTTP 301 from the host, not "Not reachable"; the redirect's line says what to check; a press whose probe gets no
        /// answer still says not reachable, and a 400 and a 502 keep their own lines (the WITNESSES). Each check is judged
        /// on its own step, so a mutant fails exactly one.</summary>
        private static bool CheckBurndownW5RedirectedTest(StringBuilder sb)
        {
            bool ok = true;
            const string BaseUrl = "http://127.0.0.1:9/v1";
            string expectedEndpoint = Normalized(BaseUrl);
            using (var rig = new StandDownRig("aibrain-bd-w5-redirect"))
            {
                PaneAction cloudTest = FindAction(rig.Pane, "Cloud provider", "Test connection");
                FixedStatusHandler redirect = null;
                RefusingHandler refusing = null;
                int seamCalls = 0;
                bool seamLocal = true;
                string seamEndpoint = null;
                rig.Module.ConnectionTestBackendForDiagnostics = delegate(AiSettings s, string endpoint, bool local)
                {
                    seamCalls++;
                    seamLocal = local;
                    seamEndpoint = endpoint;
                    if (redirect != null)
                        return new OpenAiCompatBackend(endpoint, s.ApiKey, TimeSpan.FromSeconds(5), redirect);
                    return new OpenAiCompatBackend(endpoint, s.ApiKey, TimeSpan.FromSeconds(5), refusing);
                };

                redirect = new FixedStatusHandler(HttpStatusCode.MovedPermanently, "");
                string redirected = PressWith(cloudTest,
                    "brainRunsOn", "Cloud provider", "cloudProvider", "custom", "cloudEndpoint", BaseUrl);
                ok &= Check(sb, "aibrain bd-w5: Test connection on the Cloud provider card names a base URL that answers with a redirect as HTTP 301 from its host, not as not reachable (N-bd-w4-aibrain-1-01): " +
                                (redirected ?? "(no answer)") + ", probe requests " + redirect.Requests,
                    redirected != null && redirected.StartsWith("✗ HTTP 301 from 127.0.0.1", StringComparison.Ordinal) &&
                    redirected.IndexOf("Not reachable", StringComparison.Ordinal) < 0 &&
                    seamCalls == 1 && !seamLocal && seamEndpoint == expectedEndpoint && redirect.Requests == 1);

                string described = AiBrainModule.DescribeHttpFailure(new AiBackendHttpException(307, false), "https://cloud.example.com/v1");
                ok &= Check(sb, "aibrain bd-w5: a redirect's pane line says the base URL redirects and to check it: " + described,
                    described == "✗ HTTP 307 from cloud.example.com: the base URL redirects, and a redirect is never followed; " +
                                 "check the base URL (it usually ends in /v1)");

                redirect = null;
                refusing = new RefusingHandler();
                string refused = PressWith(cloudTest,
                    "brainRunsOn", "Cloud provider", "cloudProvider", "custom", "cloudEndpoint", BaseUrl);
                ok &= Check(sb, "WITNESS aibrain bd-w5: a Test connection whose probe gets no answer still says not reachable: " +
                                (refused ?? "(no answer)"),
                    refused == "✗ Not reachable at " + expectedEndpoint && seamCalls == 2);

                string badRequest = AiBrainModule.DescribeHttpFailure(new AiBackendHttpException(400, false), "https://cloud.example.com/v1");
                string badGateway = AiBrainModule.DescribeHttpFailure(new AiBackendHttpException(502, true), "https://cloud.example.com/v1");
                ok &= Check(sb, "WITNESS aibrain bd-w5: a 400 and a 502 keep their own lines, not the redirect's: " + badRequest + " | " + badGateway,
                    badRequest == "✗ HTTP 400 from cloud.example.com" &&
                    badGateway == "✗ HTTP 502 from cloud.example.com: the provider is having trouble; try again later");
            }
            return ok;
        }
    }
}
