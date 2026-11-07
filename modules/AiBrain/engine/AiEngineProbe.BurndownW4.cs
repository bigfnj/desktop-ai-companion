using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using DesktopAICompanion.Ai;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Burn-down lane w4-aibrain-1 (aibrain 1.7.1, wave 4): the cloud-with-local-fallback composite tells a probe the cloud
    /// answered with a redirect from one that got no answer, and only the second sends remarks straight to the local model
    /// (N-bd-w3-aibrain-1-01). No server, no model: the cloud leg is the real OpenAiCompatBackend over a handler double, so
    /// the probe's own refusal of the redirect and the chat's are the ones that run, and the local leg is a recording double.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckBurndownW4Lane(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckBurndownW4RedirectedPrimary", CheckBurndownW4RedirectedPrimary);
            return ok;
        }

        /// <summary>N-bd-w3-aibrain-1-01: a cloud whose probe was answered with a redirect is still tried, so the remark fails
        /// on the redirect (deterministic, no fallover) instead of going to the local model, and the listing still asks it
        /// nothing (RA-062); a cloud whose probe got no answer at all still sends the remark straight to the local model, the
        /// N-audit-1106-128 behaviour (the WITNESS). Each check is judged on its own step, so a mutant fails exactly one.</summary>
        private static bool CheckBurndownW4RedirectedPrimary(StringBuilder sb)
        {
            bool ok = true;
            var messages = new List<ChatMessage> { ChatMessage.User("hi", null) };
            Action<string> previousSink = AiBrain.LogSink;
            var lines = new List<string>();
            Predicate<string> passedOver = delegate(string l)
            {
                return l.IndexOf("fallback: the cloud did not answer its last probe", StringComparison.Ordinal) >= 0;
            };
            try
            {
                AiBrain.LogSink = delegate(string line) { lock (lines) lines.Add(line); };
                using (var redirect = new FixedStatusHandler(HttpStatusCode.MovedPermanently, ""))
                using (var cloud = new OpenAiCompatBackend("https://cloud.example.com/v1", "", TimeSpan.FromSeconds(5), redirect))
                {
                    var local = new RecordingBackend("{\"text\":\"from local\",\"emotion\":\"happy\"}", true);
                    using (var composite = new FallbackBackend(cloud, local, "cloud-vision", "local-text", "local-vision"))
                    {
                        bool up = composite.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
                        bool recorded = composite.PrimaryProbeRecordedForDiagnostics.Wait(TimeSpan.FromSeconds(5));
                        string reply = null;
                        AiBackendHttpException caught = null;
                        try { reply = composite.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (AiBackendHttpException ex) { caught = ex; }
                        bool logged;
                        lock (lines) logged = lines.Exists(passedOver);
                        ok &= Check(sb, "aibrain bd-w4: a cloud whose probe was answered with a redirect is still tried, and the remark fails on the redirect instead of going to the local model (N-bd-w3-aibrain-1-01): cloud requests " +
                                        redirect.Requests + ", local chats " + local.ChatCalls + ", error " +
                                        (caught == null ? "none" : "http-" + caught.StatusCode),
                            up && recorded && redirect.Requests == 2 && local.ChatCalls == 0 && reply == null &&
                            caught != null && caught.StatusCode == 301 && !caught.IsTransient && !logged);

                        int requestsBefore = redirect.Requests;
                        IReadOnlyList<ModelListing> listed = AiBrainModule.ListBackendModelsAsync(composite, CancellationToken.None).GetAwaiter().GetResult();
                        ok &= Check(sb, "aibrain bd-w4: the composite asks nothing of a cloud whose probe was answered with a redirect for its models, and lists none (RA-062): listing requests " +
                                        (redirect.Requests - requestsBefore),
                            listed == null && redirect.Requests == requestsBefore);
                    }
                }

                lock (lines) lines.Clear();
                using (var refusing = new RefusingHandler())
                using (var cloud = new OpenAiCompatBackend("https://cloud.example.com/v1", "", TimeSpan.FromSeconds(5), refusing))
                {
                    var local = new RecordingBackend("{\"text\":\"from local\",\"emotion\":\"happy\"}", true);
                    using (var composite = new FallbackBackend(cloud, local, "cloud-vision", "local-text", "local-vision"))
                    {
                        composite.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
                        bool recorded = composite.PrimaryProbeRecordedForDiagnostics.Wait(TimeSpan.FromSeconds(5));
                        string reply = composite.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult();
                        bool logged;
                        lock (lines) logged = lines.Exists(passedOver);
                        ok &= Check(sb, "WITNESS aibrain bd-w4: a cloud whose probe got no answer (a refused connection) still sends the remark straight to the local model, and the log says so",
                            recorded && local.ChatCalls == 1 && reply != null && reply.Contains("from local") && logged);
                    }
                }
            }
            finally { AiBrain.LogSink = previousSink; }
            return ok;
        }
    }
}
