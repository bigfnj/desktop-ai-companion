using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit.Testing;   // RecordingHost, TempModuleStorage

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// The brain's lifecycle, offline: the settings copy the brain factory is handed and what happens when the
    /// factory throws, when the inventory is learned, how often the OCR engine is resolved, and the audition's
    /// one-at-a-time guard. Every backend here is a double; the one module instance aims its local slot at a
    /// loopback port nothing listens on. Nothing on this machine is contacted.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunLifecycle(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckCloneForBrain(sb);
            ok &= CheckInventoryFollowsReachability(sb);
            ok &= CheckOcrResolution(sb);
            ok &= CheckAuditionGuard(sb);
            ok &= CheckTestOcrReachesLiveBrain(sb);
            return ok;
        }

        /// <summary>F068 and F100. The factory reads a private copy, and a factory that throws is reported.</summary>
        private static bool CheckCloneForBrain(StringBuilder sb)
        {
            bool ok = true;
            var live = new AiSettings
            {
                Provider = "openai",
                OpenAiBaseUrl = "https://api.openai.com/v1",
                CloudDataConsent = true,
                CloudTextModel = "gpt-4o-mini",
            };
            // The extension data is the collection fixture (the two Fortunes-era lists this used went with F093):
            // an unknown key an older file carried has to survive the copy too, or SaveMerged would drop it.
            live.ExtensionData["fixture-key"] = System.Text.Json.JsonDocument.Parse("\"kept\"").RootElement;
            string keyError;
            bool keyStored = live.TrySetApiKey("clone-fixture-key-not-a-real-key", out keyError);
            AiSettings copy = live.CloneForBrain();
            ok &= Check(sb, "the brain's settings copy owns its credential dictionary and its collections",
                copy.ApiKeysEnc != null && !ReferenceEquals(copy.ApiKeysEnc, live.ApiKeysEnc) &&
                copy.ExtensionData != null && !ReferenceEquals(copy.ExtensionData, live.ExtensionData));
            ok &= Check(sb, "WITNESS the copy carries the fields the factory reads",
                copy.Provider == "openai" && copy.CloudDataConsent && copy.CloudTextModel == "gpt-4o-mini" &&
                copy.ExtensionData.ContainsKey("fixture-key") &&
                copy.ExtensionData["fixture-key"].GetString() == "kept");
            if (keyStored)
            {
                // The window F068 and F100 describe: the pane rotates the key after the hand-off and before the
                // pool thread builds the brain. The copy still decrypts the key it was handed.
                live.TrySetApiKey("rotated-after-the-hand-off-not-a-real-key", out keyError);
                ok &= Check(sb, "a key rotated on the live instance after the hand-off does not reach the copy (no torn read)",
                    copy.ApiKey == "clone-fixture-key-not-a-real-key" &&
                    live.ApiKey == "rotated-after-the-hand-off-not-a-real-key");
            }
            else
            {
                sb.AppendLine("SKIP: DPAPI unavailable (" + keyError + "), so the copy's key isolation is not asserted");
            }
            ok &= Check(sb, "the brain's settings copy can never write the settings file",
                !copy.SaveWithin(AiSettings.ProcessLockTimeoutMilliseconds));

            // A factory that throws: the reconfigure completes false and the log says why, instead of a discarded
            // task faulting silently with the brain left null until the next Apply.
            var lines = new List<string>();
            Action<string> previous = AiBrain.LogSink;
            var manager = new AiSessionManager();
            bool completedFalse = false;
            try
            {
                AiBrain.LogSink = delegate(string line) { lines.Add(line); };
                completedFalse = !manager.ReconfigureAsync(
                    delegate { throw new InvalidOperationException("factory fixture"); },
                    true, false, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception) { completedFalse = false; }
            finally
            {
                AiBrain.LogSink = previous;
                manager.Dispose();
            }
            ok &= Check(sb, "a throwing brain factory is reported and does not fault the reconfigure",
                completedFalse &&
                lines.Exists(delegate(string l) { return l.IndexOf("brain build failed: invalid-state", StringComparison.Ordinal) >= 0; }));
            return ok;
        }

        /// <summary>F071. The inventory is learned on the first reachability check of an unprepared brain, not
        /// re-listed while the backend stays up, listed again when the backend comes back, and re-listed on the
        /// pane's request through the session.</summary>
        private static bool CheckInventoryFollowsReachability(StringBuilder sb)
        {
            bool ok = true;
            var backend = new RecordingBackend("{\"text\":\"hi\",\"emotion\":\"happy\"}", true);
            int listings = 0;
            using (var brain = new AiBrain(backend, new AiSettings { TextModel = "a-model-nobody-has:1b" }))
            {
                brain.ModelLister = delegate(CancellationToken ct)
                {
                    listings++;
                    return Task.FromResult((IReadOnlyList<ModelListing>)new List<ModelListing>
                    {
                        new ModelListing("gemma3:4b", true),
                    });
                };
                // Never prepared: auto-start off with no warm-up builds the brain lazily on the first ask, and until
                // 2026-09-29 that brain never took an inventory at all.
                bool up = brain.CheckBackendAvailableForDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModelChoice choice;
                BrainResponse advisory;
                brain.ResolveBeforeCapture(false, out choice, out advisory);
                ok &= Check(sb, "an unprepared brain learns the inventory on its first reachability check, so BUG-002's re-validation applies to it",
                    up && listings == 1 && choice.Reason == "substituted");
                brain.CheckBackendAvailableForDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
                brain.CheckBackendAvailableForDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS a backend that stays up is not re-listed on every ask", listings == 1);
                backend.Available = false;
                bool down = brain.CheckBackendAvailableForDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
                backend.Available = true;
                bool back = brain.CheckBackendAvailableForDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "a backend that went away and came back is listed again, since its models may have changed",
                    !down && back && listings == 2);
            }

            // The pane's "Refresh models" reaches the live brain through the session, without the gate.
            var manager = new AiSessionManager();
            int sessionListings = 0;
            try
            {
                var sessionBackend = new RecordingBackend("{\"text\":\"hi\",\"emotion\":\"happy\"}", true);
                bool built = manager.ReconfigureAsync(delegate
                {
                    var b = new AiBrain(sessionBackend, new AiSettings());
                    b.ModelLister = delegate(CancellationToken ct)
                    {
                        sessionListings++;
                        return Task.FromResult((IReadOnlyList<ModelListing>)new List<ModelListing>
                        {
                            new ModelListing("gemma3:4b", true),
                        });
                    };
                    return b;
                }, true, false, CancellationToken.None).GetAwaiter().GetResult();
                int beforeRefresh = sessionListings;
                manager.RefreshInventoryAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "the pane's model refresh reaches the live brain's inventory through the session (F071)",
                    built && beforeRefresh == 0 && sessionListings == 1);
            }
            finally { manager.Dispose(); }
            return ok;
        }

        /// <summary>F077. Resolving the OCR engine throws nothing per PATH entry and happens once per brain.</summary>
        private static bool CheckOcrResolution(StringBuilder sb)
        {
            bool ok = true;
            string scratch = Path.Combine(Path.GetTempPath(), "dp-aibrain-path-" + Guid.NewGuid().ToString("N"));
            try
            {
                var entries = new List<string>();
                for (int i = 0; i < 12; i++)
                {
                    string directory = Path.Combine(scratch, "entry" + i);
                    Directory.CreateDirectory(directory);
                    entries.Add(directory);
                }
                string pathValue = string.Join(Path.PathSeparator.ToString(), entries.ToArray());

                // First-chance exceptions on THIS thread only: the process has background work of its own.
                int thrown = 0;
                int testThread = Thread.CurrentThread.ManagedThreadId;
                EventHandler<FirstChanceExceptionEventArgs> counter = delegate(object sender, FirstChanceExceptionEventArgs e)
                {
                    if (Thread.CurrentThread.ManagedThreadId == testThread && e.Exception is IOException) thrown++;
                };
                AppDomain.CurrentDomain.FirstChanceException += counter;
                string resolved;
                try { resolved = AiExecutablePolicy.ResolveFromPath(pathValue, "tesseract.exe"); }
                finally { AppDomain.CurrentDomain.FirstChanceException -= counter; }
                ok &= Check(sb, "resolving an absent executable from PATH throws nothing per entry (it used to throw once per directory)",
                    resolved == null && thrown == 0);

                string present = Path.Combine(entries[5], "tesseract.exe");
                File.WriteAllBytes(present, new byte[] { 0 });
                ok &= Check(sb, "WITNESS an executable present in one PATH entry still resolves through the same walk",
                    string.Equals(AiExecutablePolicy.ResolveFromPath(pathValue, "tesseract.exe"), present, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "OCR resolution probe threw " + ex.GetType().Name + ": " + ex.Message, false);
            }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch { ok &= Check(sb, "OCR resolution probe cleanup", false); }
            }

            using (var brain = new AiBrain(new RecordingBackend("", true), new AiSettings()))
            {
                brain.ResolveTesseractForDiagnostics();
                brain.ResolveTesseractForDiagnostics();
                ok &= Check(sb, "the OCR engine is resolved once per brain, not once per ask",
                    brain.TesseractResolutionsForDiagnostics == 1);
                // R-015: forgetting the resolution with a newly chosen path makes the next read resolve afresh, and
                // to that path. A fake tesseract.exe in a scratch directory stands in for the one the user picked.
                string chosenDir = Path.Combine(Path.GetTempPath(), "dp-aibrain-chosen-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(chosenDir);
                    string chosen = Path.Combine(chosenDir, "tesseract.exe");
                    File.WriteAllBytes(chosen, new byte[] { 0 });
                    brain.ForgetTesseractResolution(chosen);
                    string resolved = brain.ResolveTesseractForDiagnostics();
                    ok &= Check(sb, "forgetting the resolution with a newly chosen path makes the next read resolve afresh, to that path (R-015)",
                        brain.TesseractResolutionsForDiagnostics == 2 &&
                        string.Equals(resolved, chosen, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    try { if (Directory.Exists(chosenDir)) Directory.Delete(chosenDir, true); }
                    catch { ok &= Check(sb, "chosen OCR engine scratch cleanup", false); }
                }
            }
            return ok;
        }

        /// <summary>The settings every module instance in this file starts from: the brain on, auto-start off, the
        /// local slot on the OpenAI-compatible protocol at a loopback port nothing listens on. Written BEFORE Init, so
        /// the one-time migrator finds a file and copies nothing real in.</summary>
        private static void SeedOfflineModuleSettings(TempModuleStorage storage)
        {
            File.WriteAllText(
                Path.Combine(storage.DataDirectory, "ai-settings.json"),
                "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + ", \"AiBrainEnabled\": true, " +
                "\"AutoStartServer\": false, \"LocalBackendKind\": \"openai-compat\", \"Endpoint\": \"http://127.0.0.1:9\" }",
                new UTF8Encoding(false));
        }

        /// <summary>R-015. The "Test OCR" button resets the LIVE brain's Tesseract cache, not only the throwaway it
        /// tests with, so an install made mid-session reaches the next remark.</summary>
        private static bool CheckTestOcrReachesLiveBrain(StringBuilder sb)
        {
            bool ok = true;
            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            AiBrainModule module = null;
            TempModuleStorage storage = null;
            try
            {
                storage = new TempModuleStorage("aibrain-test-ocr");
                SeedOfflineModuleSettings(storage);
                var host = new RecordingHost();
                host.UseStorage("aibrain", storage);
                module = new AiBrainModule();
                host.Declared = module.Info.Permissions;
                module.Init(host);

                // Init's ApplyState builds the brain on a pool thread (no PrepareAsync: auto-start is off and the
                // residency is "unload"); wait for it, briefly.
                AiBrain live = null;
                var wait = Stopwatch.StartNew();
                while (live == null && wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    live = module.SessionForDiagnostics.LiveBrainForDiagnostics;
                    if (live == null) Thread.Sleep(20);
                }
                ok &= Check(sb, "the module built a live brain to test the OCR button against", live != null);
                if (live != null)
                {
                    live.ResolveTesseractForDiagnostics();
                    live.ResolveTesseractForDiagnostics();
                    int before = live.TesseractResolutionsForDiagnostics;
                    string verdict = module.TestOcrAsync().GetAwaiter().GetResult();
                    live.ResolveTesseractForDiagnostics();
                    ok &= Check(sb, "the Test OCR button makes the LIVE brain resolve its engine afresh, not only the throwaway it tests with (R-015)",
                        before == 1 && live.TesseractResolutionsForDiagnostics == 2 && verdict != null);
                    live.ResolveTesseractForDiagnostics();
                    ok &= Check(sb, "WITNESS with no button press the live brain keeps its resolution",
                        live.TesseractResolutionsForDiagnostics == 2);
                }
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "Test OCR probe threw " + ex.GetType().Name + ": " + ex.Message, false);
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

        /// <summary>F062. One audition at a time, whichever thread the last one ended on.</summary>
        private static bool CheckAuditionGuard(StringBuilder sb)
        {
            bool ok = true;
            Action<string> previousSink = AiBrain.LogSink;
            string previousRoot = AiPaths.CurrentRootForDiagnostics;
            AiBrainModule module = null;
            TempModuleStorage storage = null;
            try
            {
                storage = new TempModuleStorage("aibrain-audition-guard");
                // Seeded before Init as in CheckModuleEntryPoints, and with the local slot aimed at a loopback port
                // nothing listens on: the guard is what is under test, and should it ever fail to refuse, the
                // audition it lets through must fail fast against a refused connection rather than reach the
                // Ollama this machine may be running.
                SeedOfflineModuleSettings(storage);
                var host = new RecordingHost();
                host.UseStorage("aibrain", storage);
                module = new AiBrainModule();
                host.Declared = module.Info.Permissions;
                module.Init(host);

                bool claimed = module.TryBeginAudition();
                string refused = module.PreviewDispositionAsync(false).GetAwaiter().GetResult();
                ok &= Check(sb, "a second audition press while one is running is refused with an explanation (F062)",
                    claimed && refused != null && refused.IndexOf("Already generating", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "WITNESS the audition slot cannot be claimed twice", !module.TryBeginAudition());
                module.EndAudition();
                ok &= Check(sb, "WITNESS once the audition ends the slot is free again", module.TryBeginAudition());
                module.EndAudition();
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "audition guard probe threw " + ex.GetType().Name + ": " + ex.Message, false);
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
    }
}
