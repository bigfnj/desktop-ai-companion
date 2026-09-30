using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Residency and readiness, offline: how long a reachability probe may take and that the composite asks both
    /// legs at once, that a same-backend Apply retires a brain without evicting its model, that a complete reply
    /// is never discarded to a late deadline, and that the persona audition holds its model between samples
    /// without warming one it will not use. Every backend here is a double; nothing on this machine is contacted.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunResidency(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckProbeBounds(sb);
            ok &= CheckRetireKeepsResidentModel(sb);
            ok &= CheckDeadlineAfterConsume(sb);
            ok &= CheckAuditionResidency(sb);
            return ok;
        }

        /// <summary>F105. Probes are bounded by their own deadline, and the composite does not queue its legs.</summary>
        private static bool CheckProbeBounds(StringBuilder sb)
        {
            bool ok = true;
            ok &= Check(sb, "the reachability probe deadline is shorter than the default chat timeout it used to borrow",
                AiEndpointPolicy.AvailabilityProbeDeadline < TimeSpan.FromSeconds(new AiSettings().TimeoutSeconds));

            // A server that accepts the connection and then says nothing, against a 600 s chat deadline.
            var clock = Stopwatch.StartNew();
            using (var blocking = new BlockingHeadersHandler())
            using (var cloud = new OpenAiCompatBackend(
                "https://api.openai.com/v1", "", TimeSpan.FromSeconds(600), blocking, TimeSpan.FromMilliseconds(150)))
            {
                Task<bool> probe = cloud.IsAvailableAsync(CancellationToken.None);
                bool answered = probe.Wait(TimeSpan.FromSeconds(5));
                ok &= Check(sb, "a cloud reachability probe is bounded by the probe deadline, not by the 600 s chat deadline",
                    answered && !probe.Result);
            }
            using (var blocking = new BlockingHeadersHandler())
            using (var local = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(600), "", blocking,
                    TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored) { return true; }))
            {
                Task<bool> probe = local.IsAvailableAsync(CancellationToken.None);
                bool answered = probe.Wait(TimeSpan.FromSeconds(5));
                ok &= Check(sb, "the local reachability probe is bounded the same way", answered && !probe.Result);
            }
            ok &= Check(sb, "...and both answered well inside the bound", clock.Elapsed < TimeSpan.FromSeconds(10));

            // The composite: a cloud leg that never answers must not delay a local leg that does.
            var hungCloud = new HangingBackend();
            using (var up = new RecordingBackend("", true))
            using (var composite = new FallbackBackend(hungCloud, up, "cloud-vision", "local-text", "local-vision"))
            {
                Task<bool> avail = composite.IsAvailableAsync(CancellationToken.None);
                bool answered = avail.Wait(TimeSpan.FromSeconds(3));
                ok &= Check(sb, "the composite answers 'available' from the local leg without waiting out a hung cloud probe",
                    answered && avail.Result);
                hungCloud.Release(false);
            }
            var hungServer = new HangingBackend();
            using (var up = new RecordingBackend("", true))
            using (var composite = new FallbackBackend(hungServer, up, "cloud-vision", "local-text", "local-vision"))
            {
                Task<bool> ready = composite.EnsureServerAsync(CancellationToken.None);
                var wait = Stopwatch.StartNew();
                while (up.EnsureServerCalls == 0 && wait.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(10);
                ok &= Check(sb, "the local leg is readied while the cloud probe is still pending, so launch auto-start no longer waits behind it",
                    up.EnsureServerCalls == 1 && !ready.IsCompleted);
                hungServer.Release(false);
                ok &= Check(sb, "WITNESS the composite is ready once the local leg is, whatever the cloud leg said",
                    ready.Wait(TimeSpan.FromSeconds(3)) && ready.Result);
            }
            return ok;
        }

        /// <summary>F095 and F065. What the backend fingerprint sees, and a retirement that keeps the model.</summary>
        private static bool CheckRetireKeepsResidentModel(StringBuilder sb)
        {
            bool ok = true;
            var a = new AiSettings();
            var b = new AiSettings();
            ok &= Check(sb, "two identical configurations share a backend fingerprint",
                AiBrainModule.BackendFingerprint(a) == AiBrainModule.BackendFingerprint(b));
            b.CompanionName = "Renamed";
            b.UserName = "Someone";
            b.Disposition = "pirate";
            b.Hotkey = "Ctrl+Shift+F9";
            ok &= Check(sb, "a persona, hotkey or name edit leaves the backend fingerprint unchanged (the brain is rebuilt; the model stays)",
                AiBrainModule.BackendFingerprint(a) == AiBrainModule.BackendFingerprint(b));
            // UseVision decides which of the two models is resident, so it is IN the fingerprint: until 2026-09-30 a
            // vision toggle under "keep" retired without evicting and left the vision model resident for nothing (R-011).
            ok &= Check(sb, "a vision toggle changes the fingerprint: it decides which model is resident, so under 'keep' it must evict (R-011)",
                AiBrainModule.BackendFingerprint(a) != AiBrainModule.BackendFingerprint(new AiSettings { UseVision = true }));
            b.TextModel = "other:1b";
            ok &= Check(sb, "a model change changes the fingerprint",
                AiBrainModule.BackendFingerprint(a) != AiBrainModule.BackendFingerprint(b));
            ok &= Check(sb, "a residency change changes the fingerprint",
                AiBrainModule.BackendFingerprint(a) !=
                AiBrainModule.BackendFingerprint(new AiSettings { ModelResidency = AiSettings.ResidencyKeep }));
            ok &= Check(sb, "a provider change changes the fingerprint",
                AiBrainModule.BackendFingerprint(a) !=
                AiBrainModule.BackendFingerprint(new AiSettings { Provider = "openai", OpenAiBaseUrl = "https://api.openai.com/v1" }));

            // The session: told the replacement targets the same backend, it disposes the old brain and leaves
            // its model where it is.
            var settings = new AiSettings { TextModel = "retirement-model", VisionModel = "retirement-model" };
            RetirementTrackingBackend kept;
            AiSessionManager keeper = CreateRetirementTestManager(out kept);
            try
            {
                keeper.ReconfigureAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None, null, false).GetAwaiter().GetResult();
                ok &= Check(sb, "a same-backend Apply retires the brain without evicting its model, and still disposes it",
                    kept.UnloadCalls == 0 && kept.DisposeCount == 1);
            }
            finally { keeper.Dispose(); }
            RetirementTrackingBackend released;
            AiSessionManager releaser = CreateRetirementTestManager(out released);
            try
            {
                releaser.ReconfigureAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS a reconfigure that does not say otherwise still evicts on retire",
                    released.UnloadCalls == 1 && released.DisposeCount == 1);
            }
            finally { releaser.Dispose(); }
            return ok;
        }

        /// <summary>F081. A consumer that finishes after the deadline still hands back its complete result.</summary>
        private static bool CheckDeadlineAfterConsume(StringBuilder sb)
        {
            bool ok = true;
            using (var handler = new FixedJsonResponseHandler("{\"ok\":true}"))
            using (var client = new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan })
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/ai-late"))
            {
                // The consumer ignores the token and finishes after the 50 ms deadline: the shape of a body read that
                // completed just as the timer fired. The result is complete, and used to be thrown away.
                string result = null;
                bool timedOut = false;
                try
                {
                    result = AiEndpointPolicy.SendWithDeadlineAsync(
                        client, request, TimeSpan.FromMilliseconds(50), CancellationToken.None,
                        delegate(HttpResponseMessage response, CancellationToken boundedToken)
                        {
                            Thread.Sleep(250);
                            return Task.FromResult("finished");
                        }).GetAwaiter().GetResult();
                }
                catch (TimeoutException) { timedOut = true; }
                ok &= Check(sb, "a complete reply is returned even when the deadline fired before the consumer handed it back",
                    !timedOut && result == "finished");
            }
            using (var handler = new FixedJsonResponseHandler("{\"ok\":true}"))
            using (var client = new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan })
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/ai-late-observed"))
            {
                // WITNESS: a consumer that observes the token, as every real one does, still times out.
                bool timedOut = false;
                try
                {
                    AiEndpointPolicy.SendWithDeadlineAsync(
                        client, request, TimeSpan.FromMilliseconds(50), CancellationToken.None,
                        delegate(HttpResponseMessage response, CancellationToken boundedToken)
                        {
                            Thread.Sleep(250);
                            boundedToken.ThrowIfCancellationRequested();
                            return Task.FromResult("never");
                        }).GetAwaiter().GetResult();
                }
                catch (TimeoutException) { timedOut = true; }
                ok &= Check(sb, "WITNESS a consumer that observes the deadline still reports a timeout", timedOut);
            }
            return ok;
        }

        /// <summary>F070 and F063. The audition brain's own residency, and a preparation that warms nothing.</summary>
        private static bool CheckAuditionResidency(StringBuilder sb)
        {
            bool ok = true;
            int? underUnload = AiBrainModule.AuditionKeepAliveSeconds(new AiSettings());
            ok &= Check(sb, "the audition brain holds the model between samples under 'unload' residency (a positive window, not 0)",
                underUnload.HasValue && underUnload.Value > 0);
            ok &= Check(sb, "WITNESS under 'keep' and 'server' the audition sends the residency's own value",
                AiBrainModule.AuditionKeepAliveSeconds(new AiSettings { ModelResidency = AiSettings.ResidencyKeep }) == -1 &&
                AiBrainModule.AuditionKeepAliveSeconds(new AiSettings { ModelResidency = AiSettings.ResidencyServer }) == null);
            using (ICompanionBrainBackend backend = AiBrainModule.BuildLocalBackend(
                new AiSettings(), "http://localhost:11434", TimeSpan.FromSeconds(5), underUnload))
            {
                var asOllama = backend as OllamaClient;
                ok &= Check(sb, "...and that window reaches the audition's Ollama client",
                    asOllama != null && asOllama.KeepAliveSeconds == underUnload);
            }
            using (ICompanionBrainBackend backend = AiBrainModule.BuildLocalBackend(
                new AiSettings(), "http://localhost:11434", TimeSpan.FromSeconds(5)))
            {
                var asOllama = backend as OllamaClient;
                ok &= Check(sb, "WITNESS the live brain's client still carries the residency's own value (0 under 'unload')",
                    asOllama != null && asOllama.KeepAliveSeconds == 0);
            }

            // F063: preparation without a warm-up, for a brain whose samples run on the text model anyway.
            var keep = new AiSettings { ModelResidency = AiSettings.ResidencyKeep, AutoStartServer = false };
            var recorder = new RecordingBackend("", true);
            using (var brain = new AiBrain(recorder, keep))
            {
                brain.PrepareAsync(CancellationToken.None, false).GetAwaiter().GetResult();
                ok &= Check(sb, "an audition's preparation warms nothing, even under 'keep'", recorder.WarmUpCalls == 0);
                brain.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS the live preparation still warms the model under 'keep'", recorder.WarmUpCalls == 1);
            }
            return ok;
        }
    }
}
