using System;
using System.Net;
using System.Text;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Burn-down lane w7-aibrain-1 (aibrain 1.7.1, wave 7): the persona audition ("Show me 5 examples") names a base URL that
    /// answers with a redirect as a redirect from its host, where it said the host was not reachable and to start it
    /// (N-bd-w5-aibrain-1-01), the line wave 5 gave Test connection. No server, no model: the press goes through the
    /// module's AuditionBrainFactoryForDiagnostics seam to a brain over the real OpenAiCompatBackend on a handler double, so
    /// the brain's own PrepareAsync, the probe's refusal of the redirect and the reason it keeps are the ones that run. The
    /// custom base URL is on loopback, so a press that ever missed the seam reaches a refused port on this computer and
    /// nothing further, and an audition on loopback needs no cloud consent.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckBurndownW7Lane(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckBurndownW7RedirectedAudition", CheckBurndownW7RedirectedAudition);
            return ok;
        }

        /// <summary>N-bd-w5-aibrain-1-01: Show me 5 examples on the cloud slot, with no local fallback, against a base URL
        /// that redirects says HTTP 301 from the host and sends no sample; a press whose probe gets no answer still says
        /// not reachable and to start the provider (the WITNESS). Each check is judged on its own press, so a mutant fails
        /// exactly one.</summary>
        private static bool CheckBurndownW7RedirectedAudition(StringBuilder sb)
        {
            bool ok = true;
            const string BaseUrl = "http://127.0.0.1:9/v1";
            string expectedEndpoint = Normalized(BaseUrl);
            using (var rig = new StandDownRig("aibrain-bd-w7-audition-redirect"))
            {
                PaneAction audition = FindAction(rig.Pane, "Persona", "Show me 5 examples");
                FixedStatusHandler redirect = null;
                RefusingHandler refusing = null;
                int built = 0;
                rig.Module.AuditionBrainFactoryForDiagnostics = delegate(AiSettings s, int? keepAlive)
                {
                    built++;
                    var backend = redirect != null
                        ? new OpenAiCompatBackend(expectedEndpoint, s.ApiKey, TimeSpan.FromSeconds(5), redirect)
                        : new OpenAiCompatBackend(expectedEndpoint, s.ApiKey, TimeSpan.FromSeconds(5), refusing);
                    return new AiBrain(backend, s.ActiveSlotSnapshot());
                };

                redirect = new FixedStatusHandler(HttpStatusCode.MovedPermanently, "");
                string redirected = PressWith(audition,
                    "brainRunsOn", "Cloud provider", "cloudProvider", "custom", "cloudEndpoint", BaseUrl, "useLocalFallback", "false");
                ok &= Check(sb, "aibrain bd-w7: Show me 5 examples names a base URL that answers with a redirect as HTTP 301 from its host, not as not reachable (N-bd-w5-aibrain-1-01): " +
                                (redirected ?? "(no answer)") + ", brains " + built + ", probe requests " + redirect.Requests,
                    redirected != null && redirected.StartsWith("✗ HTTP 301 from 127.0.0.1", StringComparison.Ordinal) &&
                    redirected.IndexOf("Not reachable", StringComparison.Ordinal) < 0 &&
                    built == 1 && redirect.Requests == 1);

                redirect = null;
                refusing = new RefusingHandler();
                string refused = PressWith(audition,
                    "brainRunsOn", "Cloud provider", "cloudProvider", "custom", "cloudEndpoint", BaseUrl, "useLocalFallback", "false");
                ok &= Check(sb, "WITNESS aibrain bd-w7: Show me 5 examples whose probe gets no answer still says not reachable and to start the provider: " +
                                (refused ?? "(no answer)"),
                    refused == "✗ Not reachable at " + expectedEndpoint + " — start the provider and try again." && built == 2);
            }
            return ok;
        }
    }
}
