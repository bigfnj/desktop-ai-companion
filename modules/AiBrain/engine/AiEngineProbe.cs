using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Self-test hook (NOT part of the plugin ABI) for --aibrain-selftest's engine leg. Proves the relocated
    /// AI-brain engine actually RUNS inside the module's own load context, without needing a live LLM:
    /// the DPAPI-scoped settings store (encrypt -> atomic write -> cross-session lock -> reload -> decrypt),
    /// chat-history persistence, endpoint/persona/model policy, and backend construction. This is the S4a
    /// expand-phase gate (mirrors the Fortunes FortuneEngineProbe). Invoked reflectively by the host so the
    /// base keeps no reference to the module engine.
    /// </summary>
    public static partial class AiEngineProbe
    {
        public static bool Run(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            string root = null;
            try
            {
                // Isolate the settings/history files in a throwaway root so the probe never touches real data.
                root = Path.Combine(Path.GetTempPath(), "dp-aibrain-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                AiPaths.SetRoot(root);

                // --- endpoint policy (in-module) ---
                string normLocal, normCloud, err;
                bool okLocal = AiEndpointPolicy.TryNormalize("http://localhost:11434", out normLocal, out err);
                ok &= Check(sb, "endpoint policy normalizes a loopback endpoint", okLocal);
                ok &= Check(sb, "loopback endpoint is recognized as local", okLocal && AiEndpointPolicy.IsLoopbackEndpoint(normLocal));
                bool okCloud = AiEndpointPolicy.TryNormalize("https://api.openai.com/v1", out normCloud, out err);
                ok &= Check(sb, "endpoint policy normalizes a cloud endpoint as non-loopback", okCloud && !AiEndpointPolicy.IsLoopbackEndpoint(normCloud));

                // --- disposition catalog (in-module) ---
                ok &= Check(sb, "disposition catalog knows the 'pirate' id", Dispositions.IsKnown("pirate"));
                ok &= Check(sb, "disposition catalog rejects an unknown id", !Dispositions.IsKnown("definitely-not-a-disposition"));
                ok &= Check(sb, "instruction for a known disposition is non-empty", !string.IsNullOrEmpty(Dispositions.InstructionForId("pirate")));

                // --- model-capability policy (in-module) ---
                ok &= Check(sb, "model policy flags a vision model", AiModelPolicy.LooksVisionCapable("llava"));
                ok &= Check(sb, "model policy treats a text model as text-only", !AiModelPolicy.LooksVisionCapable("llama3.1:8b"));
                ok &= Check(sb, "model policy flags an uncensored model", AiModelPolicy.LooksUncensored("dolphin3:8b"));
                ok &= Check(sb, "model policy treats an unmarked model as untagged", !AiModelPolicy.LooksUncensored("llama3.1:8b"));
                string normModel;
                ok &= Check(sb, "model policy normalizes a valid id", AiModelPolicy.TryNormalize("gemma3:4b", out normModel) && normModel == "gemma3:4b");

                // --- backend construction (types + HttpClient load in the module ALC; no network) ---
                try
                {
                    using (ICompanionBrainBackend ollama = new OllamaClient(normLocal, TimeSpan.FromSeconds(30), ""))
                    using (ICompanionBrainBackend compat = new OpenAiCompatBackend(normCloud, "", TimeSpan.FromSeconds(30)))
                        ok &= Check(sb, "Ollama + OpenAI-compat backends construct in-module", ollama != null && compat != null);
                }
                catch (Exception ex) { ok = false; sb.AppendLine("FAIL: backend construction threw: " + ex.GetType().Name + ": " + ex.Message); }

                // --- the crown jewel: the DPAPI-scoped settings store, end to end, in the module ALC ---
                // Proves AtomicFile.TryWriteAllText + CrossSessionLock + ProtectedData all rebound cleanly.
                AiSettings s = AiSettings.Load();
                s.PetName = "ProbePet";
                s.Provider = "openai";
                s.OpenAiBaseUrl = "https://api.openai.com/v1";
                string setError;
                bool keyStored = s.TrySetApiKey("sk-probe-secret-1234567890", out setError);
                bool saved = s.Save();
                ok &= Check(sb, "settings save (atomic write + cross-session lock) succeeds", saved);

                AiSettings reloaded = AiSettings.Load();
                ok &= Check(sb, "settings scalar round-trips (PetName)", string.Equals(reloaded.PetName, "ProbePet", StringComparison.Ordinal));
                if (keyStored)
                {
                    // DPAPI encrypted the key on Save; reload must decrypt it back to plaintext.
                    ok &= Check(sb, "DPAPI API key round-trips (encrypt->save->reload->decrypt) in-module",
                        string.Equals(reloaded.ApiKey, "sk-probe-secret-1234567890", StringComparison.Ordinal));
                }
                else
                {
                    // DPAPI can be unavailable in a headless/service context; that is not an engine defect.
                    sb.AppendLine("SKIP: DPAPI key store unavailable here (" + setError + ") - round-trip not asserted");
                }

                // --- Windows built-in OCR (the zero-install fallback when Tesseract is absent) ---
                // This runs INSIDE the module's own collectible AssemblyLoadContext, so it is also the
                // standing proof that the WinRT projection resolves there — the one risk the spike flagged.
                // Skip-passes where the OS has no recognizer for the user's languages (a CI runner with no
                // language pack), exactly like the DPAPI check above: absence is an environment fact, not
                // an engine defect.
                if (!WindowsOcr.IsAvailable)
                {
                    sb.AppendLine("SKIP: no Windows OCR recognizer for this machine's languages");
                }
                else
                {
                    string ocrText = "";
                    try
                    {
                        using (var ocrProbe = new System.Drawing.Bitmap(420, 90))
                        {
                            using (var g = System.Drawing.Graphics.FromImage(ocrProbe))
                            using (var font = new System.Drawing.Font("Segoe UI", 28, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
                            {
                                g.Clear(System.Drawing.Color.White);
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                                g.DrawString("OCR works", font, System.Drawing.Brushes.Black, new System.Drawing.PointF(10f, 15f));
                            }
                            ocrText = WindowsOcr.RecognizeAsync(ocrProbe, System.Threading.CancellationToken.None)
                                .GetAwaiter().GetResult() ?? "";
                        }
                    }
                    catch (Exception ex) { sb.AppendLine("  Windows OCR threw: " + ex.GetType().Name + ": " + ex.Message); }
                    string ocrLetters = "";
                    foreach (char c in ocrText) if (char.IsLetter(c)) ocrLetters += char.ToLowerInvariant(c);
                    ok &= Check(sb, "Windows built-in OCR reads a probe image in the module's load context",
                        ocrLetters.Contains("ocr") || ocrLetters.Contains("works"));
                }

                // --- relocated AI SECURITY assertions (ported ~verbatim from the base SecuritySelfTest;
                // see AiEngineProbe.Security.cs). They exercise the SHIPPING module engine so no coverage
                // is lost when the base's dead Ai/* copy is deleted in a later phase. ---
                ok &= RunSecurity(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { if (root != null) Directory.Delete(root, true); } catch { } }
            detail = sb.ToString();
            return ok;
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
    }
}
