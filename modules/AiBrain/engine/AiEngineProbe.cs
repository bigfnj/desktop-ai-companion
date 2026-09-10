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

                // --- vision capability: an INCOMPLETE backend report must not hide a working model ---
                // The three cases are the real /api/tags vs /api/show disagreement measured on this
                // machine. The first is the bug: tags said ["completion"] for gemma3:4b, so a
                // report-wins fallback excluded the recommended vision model from its own dropdown.
                ok &= Check(sb, "vision: a name-known model survives an incomplete report",
                    AiModelPolicy.IsVisionCapable("gemma3:4b", false));
                ok &= Check(sb, "vision: a reported vision model is offered",
                    AiModelPolicy.IsVisionCapable("mistral-small3.2:24b", true));
                ok &= Check(sb, "vision: a name-unknown model with no report is not offered",
                    !AiModelPolicy.IsVisionCapable("llama2-uncensored:latest", null));
                ok &= Check(sb, "vision: a text-only model reported as such stays out",
                    !AiModelPolicy.IsVisionCapable("dolphin3:latest", false));
                ok &= Check(sb, "vision: an unreported model still matches on name",
                    AiModelPolicy.IsVisionCapable("llava:13b", null));

                // --- capture subject: the foreground window, or the monitor when that is a bad subject ---
                // Pure, so the cases that matter are asserted here rather than by arranging real windows.
                var mon = new System.Drawing.Rectangle(0, 0, 2560, 1440);
                ok &= Check(sb, "capture: a normal window is the subject",
                    AiBrain.ChooseCaptureBounds(new DesktopAICompanion.Modules.PixelRect(100, 80, 1200, 800), mon)
                        == new System.Drawing.Rectangle(100, 80, 1200, 800));
                // No foreground window at all: a zero rect must not become a zero-size capture.
                ok &= Check(sb, "capture: no foreground window falls back to the monitor",
                    AiBrain.ChooseCaptureBounds(default(DesktopAICompanion.Modules.PixelRect), mon) == mon);
                ok &= Check(sb, "capture: a dialog-sized window falls back to the monitor",
                    AiBrain.ChooseCaptureBounds(new DesktopAICompanion.Modules.PixelRect(100, 100, 300, 200), mon) == mon);
                // A window hanging off the right edge is clamped, so the capture cannot read pixels from a
                // neighbouring display or off the desktop entirely.
                ok &= Check(sb, "capture: an overhanging window is clamped to the monitor",
                    AiBrain.ChooseCaptureBounds(new DesktopAICompanion.Modules.PixelRect(2000, 100, 1200, 800), mon)
                        == new System.Drawing.Rectangle(2000, 100, 560, 800));
                // Almost entirely offscreen: clamping leaves too little to be a subject, so the monitor wins
                // rather than capturing a 40px strip.
                ok &= Check(sb, "capture: a barely-visible window falls back to the monitor",
                    AiBrain.ChooseCaptureBounds(new DesktopAICompanion.Modules.PixelRect(2520, 100, 1200, 800), mon) == mon);

                // --- the persona must not be sanitised behind the user's back ---
                // Six of the 26 dispositions ask for profanity or insults. A model that self-censors to
                // f*** is serving its own training rather than the character the user picked, so the
                // prompt tells it not to -- and it says so ONCE, globally, because the rule previously
                // lived only inside the Jules Winnfield text and never reached Jeff Ross or the Drill
                // Sergeant. Asserted against a real AiBrain instance, so this fails if the sentence is
                // ever dropped or reworded past recognition. Measured effect: it took Jules Winnfield
                // compliance from 3 of 5 local models to 5 of 5.
                try
                {
                    var promptSettings = new AiSettings();
                    promptSettings.Disposition = "samuel";
                    using (ICompanionBrainBackend probeBackend = new OllamaClient(normLocal, TimeSpan.FromSeconds(5), ""))
                    using (var promptBrain = new AiBrain(probeBackend, promptSettings))
                    {
                        string prompt = promptBrain.BuildSystemPrompt();
                        ok &= Check(sb, "persona: the prompt forbids censoring a swear word",
                            prompt.IndexOf("never censor it with asterisks", StringComparison.OrdinalIgnoreCase) >= 0);
                        ok &= Check(sb, "persona: the chosen disposition reaches the prompt",
                            prompt.IndexOf("Jules Winnfield", StringComparison.Ordinal) >= 0);
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    sb.AppendLine("FAIL: system-prompt assertions threw: " + ex.GetType().Name + ": " + ex.Message);
                }

                // --- reply parsing: the JSON envelope must never be spoken ---
                // Fixtures are the literal shapes the two installed vision models emitted when A/B
                // tested, fence and all. Before the extractor these went down the plain-text fallback
                // and the companion read the braces and key names out loud.
                string fence = new string('`', 3);
                string fenced = fence + "json {\"text\": \"Well now, look at that file manager.\", " +
                                        "\"emotion\": \"happy\"} " + fence;
                string bare = "{\"text\":\"Bare object.\",\"emotion\":\"excited\"}";
                ok &= Check(sb, "parse: a fenced object is found",
                    AiBrain.ExtractJsonObject(fenced) != null &&
                    AiBrain.ExtractJsonObject(fenced).StartsWith("{", StringComparison.Ordinal) &&
                    AiBrain.ExtractJsonObject(fenced).EndsWith("}", StringComparison.Ordinal));
                ok &= Check(sb, "parse: a bare object is found unchanged",
                    AiBrain.ExtractJsonObject(bare) == bare);
                ok &= Check(sb, "parse: prose with no object yields nothing to parse",
                    AiBrain.ExtractJsonObject("The image shows a computer screen.") == null);
                ok &= Check(sb, "parse: an empty reply yields nothing to parse",
                    AiBrain.ExtractJsonObject("") == null);
                // A stray opening brace with no close is not an object, and must not be treated as one.
                ok &= Check(sb, "parse: an unterminated object yields nothing to parse",
                    AiBrain.ExtractJsonObject("oh {text: broken") == null);

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
                s.CompanionName = "ProbeCompanion";
                s.Provider = "openai";
                s.OpenAiBaseUrl = "https://api.openai.com/v1";
                string setError;
                // Deliberately NOT shaped like a real key. TrySetApiKey validates only length, so the
                // value is arbitrary and only has to survive the encrypt/reload round trip below -- and
                // an "sk-..." literal in a public repo is the one string here that trips a secret
                // scanner and makes a human stop and check whether a key leaked.
                bool keyStored = s.TrySetApiKey("DPAPI-ROUNDTRIP-FIXTURE-not-a-real-key", out setError);
                bool saved = s.Save();
                ok &= Check(sb, "settings save (atomic write + cross-session lock) succeeds", saved);

                AiSettings reloaded = AiSettings.Load();
                ok &= Check(sb, "settings scalar round-trips (CompanionName)", string.Equals(reloaded.CompanionName, "ProbeCompanion", StringComparison.Ordinal));
                if (keyStored)
                {
                    // DPAPI encrypted the key on Save; reload must decrypt it back to plaintext.
                    ok &= Check(sb, "DPAPI API key round-trips (encrypt->save->reload->decrypt) in-module",
                        string.Equals(reloaded.ApiKey, "DPAPI-ROUNDTRIP-FIXTURE-not-a-real-key", StringComparison.Ordinal));
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
