using System;
using System.Net;
using System.Text;
using System.Threading;
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
    /// nothing further, and an audition on loopback needs no cloud consent. The same press with the local fallback on and
    /// no local model running goes through the real FallbackBackend, whose reason is its cloud leg's (N-bd-w7-aibrain-1-01).
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckBurndownW7Lane(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckBurndownW7RedirectedAudition", CheckBurndownW7RedirectedAudition);
            ok &= GuardedCheck(sb, "CheckBurndownW7CompositeRedirect", CheckBurndownW7CompositeRedirect);
            ok &= GuardedCheck(sb, "CheckBurndownW7AskRedirect", CheckBurndownW7AskRedirect);
            return ok;
        }

        /// <summary>N-bd-w7-aibrain-1-02: a remark whose probe the host answered with a redirect is recorded for the Status
        /// card as that answer's category, the one the cloud's chat gives the same redirect behind the composite, not as an
        /// engine that is not reachable; one whose probe got no answer still is (the WITNESS). The probe fails first, so
        /// the ask ends before any capture and needs no screen.</summary>
        private static bool CheckBurndownW7AskRedirect(StringBuilder sb)
        {
            bool ok = true;
            string endpoint = Normalized("http://127.0.0.1:9/v1");
            var settings = new AiSettings { AutoStartServer = false, Endpoint = "http://127.0.0.1:9" };
            var redirect = new FixedStatusHandler(HttpStatusCode.MovedPermanently, "");
            using (var brain = new AiBrain(new OpenAiCompatBackend(endpoint, "", TimeSpan.FromSeconds(5), redirect), settings))
            {
                BrainResponse response = brain.AskAboutScreenAsync(null, null, false).GetAwaiter().GetResult();
                ok &= Check(sb, "aibrain bd-w7: a remark whose probe was answered with a redirect is recorded as that answer for the Status card, not as an engine that is not reachable (N-bd-w7-aibrain-1-02): " +
                                (brain.LastFailure ?? "(none)") + ", probe requests " + redirect.Requests,
                    response == null && brain.LastFailure == "http-301" && redirect.Requests == 1);
            }
            using (var brain = new AiBrain(new OpenAiCompatBackend(endpoint, "", TimeSpan.FromSeconds(5), new RefusingHandler()), settings))
            {
                BrainResponse response = brain.AskAboutScreenAsync(null, null, false).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS aibrain bd-w7: a remark whose probe got no answer is still recorded as an engine that is not reachable: " +
                                (brain.LastFailure ?? "(none)"),
                    response == null && brain.LastFailure == "its engine is not reachable");
            }
            return ok;
        }

        /// <summary>N-bd-w7-aibrain-1-01: the same press with the local fallback on, the default, and no local model running.
        /// The composite answers not reachable, and its reason is its cloud leg's redirect, so the audition names the
        /// redirect too. On the composite itself: one its local leg kept up keeps no reason although its cloud kept a
        /// redirect (with the WITNESS that the cloud did keep it), and one readied (the auto-start path) with neither leg
        /// up keeps the cloud's. Each mutant fails exactly one check.</summary>
        private static bool CheckBurndownW7CompositeRedirect(StringBuilder sb)
        {
            bool ok = true;
            const string BaseUrl = "http://127.0.0.1:9/v1";
            string expectedEndpoint = Normalized(BaseUrl);
            using (var rig = new StandDownRig("aibrain-bd-w7-composite-redirect"))
            {
                PaneAction audition = FindAction(rig.Pane, "Persona", "Show me 5 examples");
                var redirect = new FixedStatusHandler(HttpStatusCode.MovedPermanently, "");
                int built = 0;
                rig.Module.AuditionBrainFactoryForDiagnostics = delegate(AiSettings s, int? keepAlive)
                {
                    built++;
                    var composite = new FallbackBackend(
                        new OpenAiCompatBackend(expectedEndpoint, s.ApiKey, TimeSpan.FromSeconds(5), redirect),
                        new RecordingBackend("", false), "", "selftest-local-text", "selftest-local-vision");
                    return new AiBrain(composite, s.ActiveSlotSnapshot());
                };
                string redirected = PressWith(audition,
                    "brainRunsOn", "Cloud provider", "cloudProvider", "custom", "cloudEndpoint", BaseUrl, "useLocalFallback", "true");
                ok &= Check(sb, "aibrain bd-w7: with the local fallback and no local model running, Show me 5 examples names a base URL that answers with a redirect as HTTP 301 (N-bd-w7-aibrain-1-01): " +
                                (redirected ?? "(no answer)") + ", brains " + built + ", probe requests " + redirect.Requests,
                    redirected != null && redirected.StartsWith("✗ HTTP 301 from 127.0.0.1", StringComparison.Ordinal) &&
                    redirected.IndexOf("Not reachable", StringComparison.Ordinal) < 0 &&
                    built == 1 && redirect.Requests == 1);
            }

            var keptUpCloud = new OpenAiCompatBackend(expectedEndpoint, "", TimeSpan.FromSeconds(5),
                new FixedStatusHandler(HttpStatusCode.MovedPermanently, ""));
            using (var keptUp = new FallbackBackend(keptUpCloud, new RecordingBackend("", true), "", "selftest-local-text", "selftest-local-vision"))
            {
                bool up = keptUp.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
                bool recorded = keptUp.PrimaryProbeRecordedForDiagnostics.Wait(TimeSpan.FromSeconds(5));
                Exception composite = keptUp.LastProbeFailure;
                ok &= Check(sb, "aibrain bd-w7: a composite its local leg kept up keeps no probe reason, so nothing names a redirect for it: up " + up +
                                ", reason " + (composite == null ? "none" : composite.GetType().Name),
                    up && recorded && composite == null);
                AiBackendHttpException cloudKept = keptUpCloud.LastProbeFailure as AiBackendHttpException;
                ok &= Check(sb, "WITNESS aibrain bd-w7: that composite's cloud leg did keep its redirect: " +
                                (cloudKept == null ? "none" : "HTTP " + cloudKept.StatusCode),
                    recorded && cloudKept != null && cloudKept.StatusCode == 301);
            }

            using (var readied = new FallbackBackend(
                new OpenAiCompatBackend(expectedEndpoint, "", TimeSpan.FromSeconds(5), new FixedStatusHandler(HttpStatusCode.MovedPermanently, "")),
                new RecordingBackend("", false), "", "selftest-local-text", "selftest-local-vision"))
            {
                bool up = readied.EnsureServerAsync(CancellationToken.None).GetAwaiter().GetResult();
                AiBackendHttpException kept = readied.LastProbeFailure as AiBackendHttpException;
                ok &= Check(sb, "aibrain bd-w7: a composite readied (auto-start) with neither leg up keeps its cloud's redirect as its reason: up " + up +
                                ", reason " + (kept == null ? "none" : "HTTP " + kept.StatusCode),
                    !up && kept != null && kept.StatusCode == 301);
            }
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
