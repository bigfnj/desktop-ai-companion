using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost, TempModuleStorage
using DesktopAICompanion.Modules;             // OptionsPane, PaneAction

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Residency and readiness, offline: how long a reachability probe may take and that the composite asks both
    /// legs at once, that a same-backend Apply retires a brain without evicting its model and that the fingerprint
    /// it compares belongs to the brain that was built, that a complete reply is never discarded to a late
    /// deadline, and that the persona audition holds its model between samples without warming one it will not
    /// use, as the MODULE runs it. Every backend here is a double; the module instance aims its local slot at a
    /// loopback port nothing listens on. Nothing on this machine is contacted.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunResidency(StringBuilder sb)
        {
            bool ok = true;
            // Each check under its own catch (RA-074): one that throws is a FAIL naming it, and the rest still run.
            ok &= GuardedCheck(sb, "CheckProbeBounds", CheckProbeBounds);
            ok &= GuardedCheck(sb, "CheckRetireKeepsResidentModel", CheckRetireKeepsResidentModel);
            ok &= GuardedCheck(sb, "CheckFingerprintFollowsTheBuild", CheckFingerprintFollowsTheBuild);
            ok &= GuardedCheck(sb, "CheckDeadlineAfterConsume", CheckDeadlineAfterConsume);
            ok &= GuardedCheck(sb, "CheckAuditionResidency", CheckAuditionResidency);
            ok &= GuardedCheck(sb, "CheckAuditionThroughTheModule", CheckAuditionThroughTheModule);
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
                // The hanging double's counter, read at last (RA-077): the cloud leg was asked exactly once, at the
                // same time as the local leg, not retried while it hung.
                ok &= Check(sb, "the hung cloud leg was asked once, alongside the local leg (RA-077)",
                    hungServer.EnsureServerCalls == 1);
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

            // The session, through the PRODUCTION entry point, which compares the fingerprints itself at retire time
            // (R-012): the same fingerprint under a residency that keeps disposes the old brain and leaves its model
            // where it is; a different fingerprint, or "unload", evicts.
            var settings = new AiSettings { TextModel = "retirement-model", VisionModel = "retirement-model" };
            RetirementTrackingBackend kept;
            AiSessionManager keeper = CreateRetirementTestManager(out kept, "fingerprint-A", true);
            try
            {
                keeper.ReconfigureForBackendAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None, "fingerprint-A", true).GetAwaiter().GetResult();
                ok &= Check(sb, "a same-backend Apply retires the brain without evicting its model, and still disposes it",
                    kept.UnloadCalls == 0 && kept.DisposeCount == 1);
            }
            finally { keeper.Dispose(); }
            RetirementTrackingBackend released;
            AiSessionManager releaser = CreateRetirementTestManager(out released, "fingerprint-A", true);
            try
            {
                releaser.ReconfigureForBackendAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None, "fingerprint-B", true).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS a different fingerprint evicts on retire",
                    released.UnloadCalls == 1 && released.DisposeCount == 1);
            }
            finally { releaser.Dispose(); }
            RetirementTrackingBackend unloading;
            AiSessionManager unloader = CreateRetirementTestManager(out unloading, "fingerprint-A", false);
            try
            {
                unloader.ReconfigureForBackendAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None, "fingerprint-A", false).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS under 'unload' the same fingerprint still evicts on retire (the eviction is free there)",
                    unloading.UnloadCalls == 1 && unloading.DisposeCount == 1);
            }
            finally { unloader.Dispose(); }
            RetirementTrackingBackend defaulted;
            AiSessionManager defaulter = CreateRetirementTestManager(out defaulted);
            try
            {
                defaulter.ReconfigureAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS a reconfigure that does not say otherwise still evicts on retire",
                    defaulted.UnloadCalls == 1 && defaulted.DisposeCount == 1);
            }
            finally { defaulter.Dispose(); }
            return ok;
        }

        /// <summary>R-012 and RA-079. The fingerprint belongs to the brain that was BUILT, not to the Apply that was
        /// issued, so an Apply cancelled while queued cannot spare the older brain its eviction; and a fresh brain
        /// superseded during its own build is retired against what comes next.</summary>
        private static bool CheckFingerprintFollowsTheBuild(StringBuilder sb)
        {
            bool ok = true;
            var settings = new AiSettings { TextModel = "retirement-model", VisionModel = "retirement-model" };

            RetirementTrackingBackend older = null;
            AiSessionManager manager = null;
            SemaphoreSlim operation = null;
            bool held = false;
            try
            {
                // Brain W, built for fingerprint OLD under "keep". The gate is then held (an ask in flight).
                manager = CreateRetirementTestManager(out older, "fingerprint-old", true);
                operation = GetManagerOperation(manager);
                operation.Wait();
                held = true;
                // Apply1: fingerprint NEW, queued behind the held gate. Apply2, the same NEW fingerprint, supersedes it
                // before it built anything. Recorded at issue time, Apply1's fingerprint matched Apply2's and W was
                // retired WITHOUT eviction although its model is the OLD one; under "keep" that model carried
                // keep_alive -1 and outlived the process (R-012).
                Task<bool> apply1 = manager.ReconfigureForBackendAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None, "fingerprint-new", true);
                Task<bool> apply2 = manager.ReconfigureForBackendAsync(
                    delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                    true, false, CancellationToken.None, "fingerprint-new", true);
                bool apply1Cancelled = apply1.Wait(TimeSpan.FromSeconds(2)) && !apply1.Result;
                operation.Release();
                held = false;
                bool apply2Built = apply2.Wait(TimeSpan.FromSeconds(5)) && apply2.Result;
                ok &= Check(sb, "an Apply cancelled while queued never becomes the fingerprint the next Apply compares against: the older brain is evicted (R-012)",
                    apply1Cancelled && apply2Built && older.UnloadCalls == 1 && older.DisposeCount == 1);
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "fingerprint-follows-the-build probe threw " + ex.GetType().Name + ": " + ex.Message, false);
            }
            finally
            {
                if (held) operation.Release();
                if (manager != null) manager.Dispose();
            }

            // RA-079: a fresh brain superseded between its factory and the currency check has loaded nothing of its
            // own; it is retired against what comes NEXT, like every other retirement. The same fingerprint under
            // "keep" keeps the model, a different one evicts it. Superseded from INSIDE the factory, which is the
            // window: the next generation arrives before the check.
            ok &= CheckSupersededBuild(sb, "fingerprint-same", true,
                "a brain superseded during its build by the same fingerprint under 'keep' is retired without eviction (RA-079)", 0);
            ok &= CheckSupersededBuild(sb, "fingerprint-other", true,
                "WITNESS a brain superseded during its build by a different fingerprint is evicted", 1);
            return ok;
        }

        private static bool CheckSupersededBuild(StringBuilder sb, string nextFingerprint, bool nextKeeps, string label, int expectedUnloads)
        {
            var settings = new AiSettings { TextModel = "retirement-model", VisionModel = "retirement-model" };
            var fresh = new RetirementTrackingBackend();
            var manager = new AiSessionManager();
            try
            {
                Task<bool> superseder = null;
                bool built = manager.ReconfigureForBackendAsync(
                    delegate
                    {
                        superseder = manager.ReconfigureForBackendAsync(
                            delegate { return new AiBrain(new RetirementTrackingBackend(), settings); },
                            true, false, CancellationToken.None, nextFingerprint, nextKeeps);
                        return new AiBrain(fresh, settings);
                    },
                    true, false, CancellationToken.None, "fingerprint-same", true).GetAwaiter().GetResult();
                bool superseded = superseder != null && superseder.Wait(TimeSpan.FromSeconds(5)) && superseder.Result;
                return Check(sb, label,
                    !built && superseded && fresh.UnloadCalls == expectedUnloads && fresh.DisposeCount == 1);
            }
            finally { manager.Dispose(); }
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

        /// <summary>
        /// RA-073, RA-056 and RA-055. The audition as the MODULE runs it: the keep_alive it builds the brain with,
        /// that it warms nothing, that it evicts (within a bound) when the run ends under "unload", and that the
        /// pending-aware press auditions the pane's disposition on a copy that cannot be saved. Through the factory
        /// seam, so every backend is a double and no network is touched; the checks above pin the helpers, these pin
        /// the call sites that consume them.
        /// </summary>
        private static bool CheckAuditionThroughTheModule(StringBuilder sb)
        {
            bool ok = true;
            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            AiBrainModule module = null;
            TempModuleStorage storage = null;
            try
            {
                storage = new TempModuleStorage("aibrain-audition-module");
                SeedOfflineModuleSettings(storage);
                var host = new RecordingHost();
                host.UseStorage("aibrain", storage);
                module = new AiBrainModule();
                host.Declared = module.Info.Permissions;
                module.Init(host);

                const string Reply = "{\"text\":\"REMARK\",\"emotion\":\"neutral\"}";
                RecordingBackend recorder = null;
                AiSettings handed = null;
                int? handedKeepAlive = null;
                module.AuditionBrainFactoryForDiagnostics = delegate(AiSettings s, int? keepAlive)
                {
                    handed = s;
                    handedKeepAlive = keepAlive;
                    recorder = new RecordingBackend(Reply, true);
                    return new AiBrain(recorder, s.ActiveSlotSnapshot());
                };

                // Under the seeded "unload" residency.
                string text = module.PreviewDispositionAsync(false).GetAwaiter().GetResult();
                ok &= Check(sb, "the audition builds its brain with the audition window under 'unload' (RA-073: the call site, not only the helper)",
                    handedKeepAlive.HasValue && handedKeepAlive.Value > 0 &&
                    handedKeepAlive.Value == AiBrainModule.AuditionKeepAliveSeconds(new AiSettings()));
                ok &= Check(sb, "WITNESS under 'unload' the audition prepares without a warm-up (nothing wants one there)",
                    recorder != null && recorder.WarmUpCalls == 0);
                ok &= Check(sb, "the audition evicts when the run ends under 'unload' (call site)",
                    recorder != null && recorder.UnloadedModels.Count >= 1 && recorder.ChatCalls == DispositionScenes.All.Length);
                ok &= Check(sb, "WITNESS the saved-values press says it auditioned the saved settings",
                    text != null && text.IndexOf("as currently saved", StringComparison.Ordinal) >= 0);

                // Under "keep", where a launch preparation WOULD warm: the residency's own keep_alive, still no warm-up
                // (F063, the call site's warmUp:false), and no eviction at the end.
                module.SettingsForDiagnostics.ModelResidency = AiSettings.ResidencyKeep;
                module.PreviewDispositionAsync(false).GetAwaiter().GetResult();
                ok &= Check(sb, "under 'keep' the audition still prepares without a warm-up and evicts nothing at the end, building with -1 (call sites, RA-073)",
                    handedKeepAlive == -1 && recorder != null && recorder.WarmUpCalls == 0 && recorder.UnloadedModels.Count == 0);
                module.SettingsForDiagnostics.ModelResidency = AiSettings.ResidencyUnload;

                // RA-055: the pending-aware press auditions the dropdown's disposition, on a copy that cannot be saved.
                string pirateName = "";
                foreach (Dispositions.Disposition d in Dispositions.All)
                    if (d.Id == "pirate") pirateName = d.Name;
                var pending = new Dictionary<string, string>(StringComparer.Ordinal) { { "disposition", pirateName } };
                string pendingText = module.PreviewDispositionAsync(false, pending).GetAwaiter().GetResult();
                ok &= Check(sb, "the audition reads the pending disposition: the pane's dropdown, not the saved value (RA-055)",
                    pirateName.Length > 0 && handed != null && handed.Disposition == "pirate" &&
                    pendingText.IndexOf(pirateName, StringComparison.Ordinal) >= 0 &&
                    pendingText.IndexOf("not yet applied", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "the pending copy can never write the settings file, and the saved disposition is untouched",
                    handed != null && !handed.SaveWithin(AiSettings.ProcessLockTimeoutMilliseconds) &&
                    module.SettingsForDiagnostics.Disposition != "pirate");
                ok &= Check(sb, "WITNESS the pane offers the pending-aware delegate on the audition, Test OCR and Test connection (host 1.2.5)",
                    CountPendingAware(host, "Show me 5 examples") == 1 &&
                    CountPendingAware(host, "5 about my screen") == 1 &&
                    CountPendingAware(host, "Test OCR") == 1 &&
                    CountPendingAware(host, "Test connection") == 1);

                // RA-056: the audition-end eviction is bounded, so a backend whose unload never completes cannot pin
                // the pane on it. Run on a pool thread and waited on, so a regression fails here by name instead of
                // hanging the run.
                module.AuditionBrainFactoryForDiagnostics = delegate(AiSettings s, int? keepAlive)
                {
                    return new AiBrain(new CancellationIgnoringBackend(), s.ActiveSlotSnapshot());
                };
                var clock = Stopwatch.StartNew();
                Task<string> press = Task.Run(delegate { return module.PreviewDispositionAsync(false).GetAwaiter().GetResult(); });
                bool returned = press.Wait(TimeSpan.FromSeconds(15));
                ok &= Check(sb, "the audition-end unload is bounded: a backend whose unload never completes still hands the pane its text within the budget (RA-056)",
                    returned && clock.Elapsed < TimeSpan.FromSeconds(15) && press.Result != null);
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "audition-through-the-module probe threw " + ex.GetType().Name + ": " + ex.Message, false);
            }
            finally
            {
                try { if (module != null) module.Shutdown(); } catch { }
                AiBrain.LogSink = previousSink;
                AiPaths.SwapRoot(previousRoot);
                if (storage != null) storage.Dispose();
            }
            return ok;
        }

        /// <summary>How many actions labelled <paramref name="label"/> the module's pane offers WITH a pending-aware
        /// delegate (RA-055).</summary>
        private static int CountPendingAware(RecordingHost host, string label)
        {
            int count = 0;
            foreach (OptionsPane pane in host.OptionsPanes)
            {
                if (pane == null || pane.Actions == null) continue;
                foreach (PaneAction action in pane.Actions)
                    if (action != null && action.Label == label && action.InvokeWithPendingAsync != null) count++;
            }
            return count;
        }
    }
}
