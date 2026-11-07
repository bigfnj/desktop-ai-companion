using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost, TempModuleStorage

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Burn-down lane w3-aibrain-1 (aibrain 1.7.1, wave 3): the OCR scratch images a timed-out Tesseract run left are swept
    /// at every start, whatever the engine (N-audit-1106-126); the cloud-with-local-fallback composite sends a remark
    /// straight to the local model while the cloud's last probe got no answer (-128); the auto-start tells `ollama serve`
    /// to listen on the configured endpoint (-129). No server, no model, no screen: the sweep runs on a folder of the
    /// check's own, the composite over recording doubles, the auto-start as the start info it would launch.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckAudit1106Wave3Lane(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckAudit1106OcrScratch", CheckAudit1106OcrScratch);
            ok &= GuardedCheck(sb, "CheckAudit1106PrimaryDown", CheckAudit1106PrimaryDown);
            ok &= GuardedCheck(sb, "CheckAudit1106ServeHost", CheckAudit1106ServeHost);
            return ok;
        }

        /// <summary>N-audit-1106-126: Init sweeps an OCR scratch image older than an hour with the brain off and no OCR run
        /// at all; the sweep keeps a younger one (another instance's, in flight) and a file it did not write (the WITNESS).
        /// Each check stands alone, so a mutant fails exactly one of them.</summary>
        private static bool CheckAudit1106OcrScratch(StringBuilder sb)
        {
            bool ok = true;
            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            string folder = Path.Combine(Path.GetTempPath(), "aibrain-selftest-ocr-scratch-" + Guid.NewGuid().ToString("N"));
            AiBrainModule module = null;
            TempModuleStorage storage = null;
            try
            {
                Directory.CreateDirectory(folder);
                string stale = Path.Combine(folder, "pet_ocr_" + Guid.NewGuid().ToString("N") + ".png");
                string fresh = Path.Combine(folder, "pet_ocr_" + Guid.NewGuid().ToString("N") + ".png");
                string unrelated = Path.Combine(folder, "selftest_" + Guid.NewGuid().ToString("N") + ".png");
                foreach (string file in new[] { stale, fresh, unrelated })
                    File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                DateTime hoursAgo = DateTime.UtcNow - TimeSpan.FromHours(3);
                File.SetLastWriteTimeUtc(stale, hoursAgo);
                File.SetLastWriteTimeUtc(unrelated, hoursAgo);

                // The brain OFF, so no engine runs and no OCR either: the sweep must depend on neither. The port pin of the
                // shared seed all the same (RA-072), so nothing here could reach a real server.
                storage = new TempModuleStorage("aibrain-audit1106-ocr-scratch");
                File.WriteAllText(
                    Path.Combine(storage.DataDirectory, "ai-settings.json"),
                    "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"AiBrainEnabled\": false, " +
                    "\"AutoStartServer\": false, \"LocalBackendKind\": \"openai-compat\", \"Endpoint\": \"http://127.0.0.1:9\" }",
                    new UTF8Encoding(false));
                var host = new RecordingHost();
                host.UseStorage("aibrain", storage);
                module = new AiBrainModule();
                host.Declared = module.Info.Permissions;
                module.OcrScratchDirectoryForDiagnostics = folder;
                module.Init(host);

                Task<int> sweep = module.OcrScratchSweepForDiagnostics;
                bool finished = sweep != null && sweep.Wait(TimeSpan.FromSeconds(10));
                ok &= Check(sb, "aibrain audit-1106 w3: Init sweeps an OCR scratch image older than an hour, with the brain off and no OCR run (N-audit-1106-126): " +
                                (finished ? sweep.Result + " removed" : "no sweep finished"),
                    finished && !File.Exists(stale));
                ok &= Check(sb, "WITNESS aibrain audit-1106 w3: the sweep keeps an image younger than an hour and a file it did not write",
                    File.Exists(fresh) && File.Exists(unrelated));
            }
            finally
            {
                try { if (module != null) module.Shutdown(); } catch { }
                AiBrain.LogSink = previousSink;
                AiPaths.SwapRoot(previousRoot);
                if (storage != null) storage.Dispose();
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
            }
            return ok;
        }

        /// <summary>N-audit-1106-128: while the cloud's last probe got no answer, a remark goes straight to the local model and
        /// the log says the cloud was passed over; with a stand-down holding the local leg back the cloud is still tried, and
        /// the first probe the cloud answers puts the remarks back on it (the WITNESSES, each judged on its own step's counts,
        /// so a mutant fails exactly one check).</summary>
        private static bool CheckAudit1106PrimaryDown(StringBuilder sb)
        {
            bool ok = true;
            var messages = new List<ChatMessage> { ChatMessage.User("hi", null) };
            Action<string> previousSink = AiBrain.LogSink;
            var lines = new List<string>();
            try
            {
                AiBrain.LogSink = delegate(string line) { lock (lines) lines.Add(line); };
                var cloud = new RecordingBackend("{\"text\":\"from the cloud\",\"emotion\":\"happy\"}", false);
                var local = new RecordingBackend("{\"text\":\"from local\",\"emotion\":\"happy\"}", true);
                bool held = false;
                using (var composite = new FallbackBackend(cloud, local, "cloud-vision", "local-text", "local-vision")
                {
                    LocalFallbackHold = delegate { return held ? "a test hold" : null; },
                })
                {
                    bool up = composite.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    bool recorded = composite.PrimaryProbeRecordedForDiagnostics.Wait(TimeSpan.FromSeconds(5));
                    string reply = composite.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult();
                    bool logged;
                    lock (lines) logged = lines.Exists(delegate(string l) { return l.IndexOf("fallback: the cloud did not answer its last probe", StringComparison.Ordinal) >= 0; });
                    ok &= Check(sb, "aibrain audit-1106 w3: while the cloud's last probe got no answer a remark goes straight to the local model, and the log says so (N-audit-1106-128): cloud asked " +
                                    cloud.ChatCalls + ", local " + local.ChatCalls,
                        up && recorded && cloud.ChatCalls == 0 && local.ChatCalls == 1 && local.LastModel == "local-text" &&
                        reply != null && reply.Contains("from local") && composite.LastServedModel() == "local-text" && logged);

                    int cloudBefore = cloud.ChatCalls, localBefore = local.ChatCalls;
                    held = true;
                    string heldReply = composite.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult();
                    held = false;
                    ok &= Check(sb, "WITNESS aibrain audit-1106 w3: with a stand-down holding the local leg back the cloud is still tried",
                        cloud.ChatCalls == cloudBefore + 1 && local.ChatCalls == localBefore && heldReply != null && heldReply.Contains("from the cloud"));

                    cloudBefore = cloud.ChatCalls;
                    localBefore = local.ChatCalls;
                    cloud.Available = true;
                    composite.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    bool recordedAgain = composite.PrimaryProbeRecordedForDiagnostics.Wait(TimeSpan.FromSeconds(5));
                    string back = composite.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "WITNESS aibrain audit-1106 w3: the first probe the cloud answers puts the remarks back on it",
                        recordedAgain && cloud.ChatCalls == cloudBefore + 1 && local.ChatCalls == localBefore &&
                        back != null && back.Contains("from the cloud") && composite.LastServedModel() == "cloud-text");
                }
            }
            finally { AiBrain.LogSink = previousSink; }
            return ok;
        }

        /// <summary>N-audit-1106-129: the auto-start's `ollama serve` carries OLLAMA_HOST set to the configured endpoint's host
        /// and port, and replaces an inherited value that listens elsewhere; an inherited value that already listens on every
        /// interface on that port is kept, so a user who exposed Ollama on purpose keeps it exposed (the WITNESS). Nothing is
        /// launched: the check reads the start info the auto-start would launch.</summary>
        private static bool CheckAudit1106ServeHost(StringBuilder sb)
        {
            bool ok = true;
            ProcessStartInfo psi = OllamaClient.BuildServeStartInfo(@"C:\selftest-ollama\ollama.exe", "http://127.0.0.1:11500", null);
            string told;
            if (!psi.Environment.TryGetValue("OLLAMA_HOST", out told)) told = null;
            ok &= Check(sb, "aibrain audit-1106 w3: auto-start tells ollama serve to listen on the endpoint's own host and port (N-audit-1106-129): OLLAMA_HOST=" +
                            (told ?? "(unset)"),
                told == "127.0.0.1:11500" && psi.Arguments == "serve");

            string defaultOverOther = OllamaClient.ServeHostFor("http://localhost:11434", "127.0.0.1:12000");
            string ipv6 = OllamaClient.ServeHostFor("http://[::1]:11500", null);
            string wildcardOtherPort = OllamaClient.ServeHostFor("http://127.0.0.1:11500", "0.0.0.0");
            string schemeNoPort = OllamaClient.ServeHostFor("http://127.0.0.1:11434", "http://0.0.0.0");
            ok &= Check(sb, "aibrain audit-1106 w3: an inherited OLLAMA_HOST that listens anywhere else is replaced by the endpoint's: " +
                            defaultOverOther + " | " + ipv6 + " | " + wildcardOtherPort + " | " + schemeNoPort,
                defaultOverOther == "localhost:11434" && ipv6 == "[::1]:11500" && wildcardOtherPort == "127.0.0.1:11500" &&
                schemeNoPort == "127.0.0.1:11434");

            ok &= Check(sb, "WITNESS aibrain audit-1106 w3: an inherited OLLAMA_HOST that already listens everywhere on the endpoint's port is kept",
                OllamaClient.ServeHostFor("http://localhost:11434", "0.0.0.0") == null &&
                OllamaClient.ServeHostFor("http://127.0.0.1:11500", "0.0.0.0:11500") == null &&
                OllamaClient.ServeHostFor("http://127.0.0.1:11500", ":11500") == null &&
                OllamaClient.ServeHostFor("http://127.0.0.1:11500", "[::]:11500") == null &&
                OllamaClient.ServeHostFor("http://127.0.0.1:11500", "http://0.0.0.0:11500/") == null);
            return ok;
        }
    }
}
