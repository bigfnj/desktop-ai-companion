using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopAICompanion.Ai;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress
using DesktopAICompanion.Modules;    // ABI ScreenContext / ScreenWindow / PixelRect

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

                // --- BUG-002: a saved model id re-validated against what the backend actually has -----
                // The bug was that this never happened: an id that was valid when chosen kept being sent
                // after the model was removed, and the ask path returns null on failure, so the result was
                // indistinguishable from a companion with nothing to say.
                var installed = new System.Collections.Generic.List<ModelListing>
                {
                    new ModelListing("gemma3:4b", null),
                    new ModelListing("qwen3.6:27b", true),
                    new ModelListing("dolphin3:latest", false),
                };

                ModelChoice picked = AiModelPolicy.ChooseModel("gemma3:4b", installed, true);
                ok &= Check(sb, "model: an installed vision model is used as configured",
                    picked.Model == "gemma3:4b" && picked.Advisory == null && picked.Reason == "configured");

                // The exact live failure: VisionModel=gemma4:12b with only gemma4:26b present.
                ModelChoice missing = AiModelPolicy.ChooseModel("gemma4:12b", installed, true);
                ok &= Check(sb, "model: an absent model is replaced by one that can do the job",
                    missing.Model == "gemma3:4b" && missing.Reason == "substituted");
                ok &= Check(sb, "model: the substitution names both models so the user can act",
                    missing.Advisory != null &&
                    missing.Advisory.IndexOf("gemma4:12b", StringComparison.Ordinal) >= 0 &&
                    missing.Advisory.IndexOf("gemma3:4b", StringComparison.Ordinal) >= 0);

                // Nothing can see: the advisory must be actionable and the model unusable, NOT a silent null.
                var textOnly = new System.Collections.Generic.List<ModelListing>
                {
                    new ModelListing("dolphin3:latest", false),
                };
                ModelChoice none = AiModelPolicy.ChooseModel("gemma4:12b", textOnly, true);
                ok &= Check(sb, "model: no vision-capable model leaves the choice unusable",
                    !none.Usable && none.Reason == "none-usable");
                ok &= Check(sb, "model: an unusable choice still tells the user where to go",
                    none.Advisory != null &&
                    none.Advisory.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0);

                // A text ask may use the text-only model the vision ask rejected.
                ok &= Check(sb, "model: a text ask accepts a text-only model",
                    AiModelPolicy.ChooseModel("dolphin3:latest", textOnly, false).Model == "dolphin3:latest");

                // The critical non-nag case. An empty or absent list means the backend is offline or cannot
                // enumerate -- NOT that the model is missing -- so it must pass through unchanged and
                // silently, or every offline start would accuse a perfectly good configuration.
                ModelChoice unknown = AiModelPolicy.ChooseModel("gemma3:4b", null, true);
                ok &= Check(sb, "model: an unknown inventory uses the configured model without complaint",
                    unknown.Model == "gemma3:4b" && unknown.Advisory == null &&
                    unknown.Reason == "model-list-unknown");
                ok &= Check(sb, "model: an empty inventory is also treated as unknown",
                    AiModelPolicy.ChooseModel("gemma3:4b", new System.Collections.Generic.List<ModelListing>(), true)
                        .Advisory == null);

                // A model that IS installed but cannot do the job must not be used just because it matched.
                ok &= Check(sb, "model: an installed text-only model is not used for a vision ask",
                    AiModelPolicy.ChooseModel("dolphin3:latest", installed, true).Reason == "substituted");

                // Ollama omits ":latest" in a setting but reports it in the list; treating that as absent
                // would report a missing model that is installed.
                ok &= Check(sb, "model: a tagless id matches the backend's :latest",
                    AiModelPolicy.ModelIdMatches("dolphin3", "dolphin3:latest"));
                ok &= Check(sb, "model: a :latest id matches a tagless listing",
                    AiModelPolicy.ModelIdMatches("dolphin3:latest", "dolphin3"));
                ok &= Check(sb, "model: matching is case-insensitive",
                    AiModelPolicy.ModelIdMatches("Gemma3:4B", "gemma3:4b"));
                ok &= Check(sb, "model: a different tag is a different model",
                    !AiModelPolicy.ModelIdMatches("gemma4:12b", "gemma4:26b"));
                ok &= Check(sb, "model: a prefix is not a match",
                    !AiModelPolicy.ModelIdMatches("gemma", "gemma3:4b"));

                // BUG-002 item 4: the marker list is the fallback for backends that report nothing.
                ok &= Check(sb, "vision: gemma4 is recognised by name",
                    AiModelPolicy.IsVisionCapable("gemma4:26b", null));

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
                // BUG-003(b) composition. Capture now follows the COMPANION's monitor, so a foreground
                // window on a different display must not drag the subject there -- it clamps to nothing
                // and the companion's own monitor wins. This is the pair that makes the two decisions
                // ("which monitor" then "which rect inside it") safe to compose.
                ok &= Check(sb, "capture: a foreground window on another monitor cannot pull the subject off this one",
                    AiBrain.ChooseCaptureBounds(new DesktopAICompanion.Modules.PixelRect(3000, 100, 1200, 800), mon) == mon);

                // --- what else is open, and on WHICH screen (BUG-003(b) consistency) -----------------
                // Untested until now. The phrase is "Also open on this screen", so it has to mean the
                // screen being captured; keying it off the foreground window's monitor made it describe
                // one display next to a picture of another.
                var thisMon = new DesktopAICompanion.Modules.PixelRect(0, 0, 2560, 1440);
                var otherMon = new DesktopAICompanion.Modules.PixelRect(2560, 0, 1920, 1080);
                Func<DesktopAICompanion.Modules.PixelRect, string, bool, int, ScreenWindow> mk =
                    delegate(DesktopAICompanion.Modules.PixelRect r, string proc, bool fore, int z)
                    {
                        return new ScreenWindow
                        {
                            Title = "SECRET-TITLE",
                            ProcessName = proc,
                            Bounds = r,
                            MonitorIndex = 0,
                            IsForeground = fore,
                            ZOrder = z,
                        };
                    };
                var ctxOne = new ScreenContext
                {
                    MonitorBounds = thisMon,
                    Windows = new System.Collections.Generic.List<ScreenWindow>
                    {
                        mk(new DesktopAICompanion.Modules.PixelRect(0, 0, 1200, 800), "code", true, 0),
                        mk(new DesktopAICompanion.Modules.PixelRect(200, 200, 900, 700), "outlook", false, 1),
                        mk(new DesktopAICompanion.Modules.PixelRect(2600, 50, 800, 600), "msedge", false, 2),
                    },
                };
                string described = AiBrain.DescribeOtherWindows(ctxOne);
                ok &= Check(sb, "windows: a window sharing the captured monitor is named",
                    described.IndexOf("outlook", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "windows: a window on a DIFFERENT monitor is not named",
                    described.IndexOf("msedge", StringComparison.Ordinal) < 0);
                ok &= Check(sb, "windows: the foreground window is not listed as an 'also'",
                    described.IndexOf("code", StringComparison.Ordinal) < 0);
                // The privacy constraint, asserted rather than trusted: titles carry document names,
                // customer names and subject lines, and this string goes into a prompt.
                ok &= Check(sb, "windows: a window TITLE never reaches the prompt",
                    described.IndexOf("SECRET-TITLE", StringComparison.Ordinal) < 0);

                // Same windows, captured monitor moved to the other display: the answer must invert.
                var ctxTwo = new ScreenContext
                {
                    MonitorBounds = otherMon,
                    Windows = ctxOne.Windows,
                };
                string describedTwo = AiBrain.DescribeOtherWindows(ctxTwo);
                ok &= Check(sb, "windows: moving the captured monitor moves which windows are named",
                    describedTwo.IndexOf("msedge", StringComparison.Ordinal) >= 0 &&
                    describedTwo.IndexOf("outlook", StringComparison.Ordinal) < 0);

                // A duplicate process must not be listed twice.
                var ctxDup = new ScreenContext
                {
                    MonitorBounds = thisMon,
                    Windows = new System.Collections.Generic.List<ScreenWindow>
                    {
                        mk(new DesktopAICompanion.Modules.PixelRect(0, 0, 100, 100), "code", true, 0),
                        mk(new DesktopAICompanion.Modules.PixelRect(10, 10, 900, 700), "msedge", false, 1),
                        mk(new DesktopAICompanion.Modules.PixelRect(20, 20, 900, 700), "msedge", false, 2),
                    },
                };
                string dup = AiBrain.DescribeOtherWindows(ctxDup);
                int firstEdge = dup.IndexOf("msedge", StringComparison.Ordinal);
                ok &= Check(sb, "windows: a repeated process name is listed once",
                    firstEdge >= 0 &&
                    dup.IndexOf("msedge", firstEdge + 1, StringComparison.Ordinal) < 0);

                // No monitor rect at all (a host that could not resolve one): describe everything rather
                // than silently dropping the whole list.
                var ctxNoMon = new ScreenContext
                {
                    MonitorBounds = default(DesktopAICompanion.Modules.PixelRect),
                    Windows = ctxOne.Windows,
                };
                ok &= Check(sb, "windows: an unknown captured monitor does not blank the list",
                    AiBrain.DescribeOtherWindows(ctxNoMon).IndexOf("outlook", StringComparison.Ordinal) >= 0);

                // --- BUG-003(a): the capture-content check that made the report falsifiable --------
                // The reported symptom was "the capture shows only the wallpaper", and nothing recorded
                // what the capture contained, so it could not be confirmed or dismissed. Measured
                // 2026-09-10: the GDI screen-DC path reads DWM-composited windows (Edge WebView,
                // WhatsApp, VS Code) perfectly on this machine, and PrintWindow is the path that returns
                // blank surfaces -- so the suspected "GDI cannot see composited content" cause is ruled
                // out and no capture-API rewrite is warranted. This statistic is what remains: it tells a
                // blank capture apart from a model that simply had nothing to say.
                using (var flat = new System.Drawing.Bitmap(64, 64))
                {
                    using (var g = System.Drawing.Graphics.FromImage(flat))
                        g.Clear(System.Drawing.Color.FromArgb(30, 30, 30));
                    ok &= Check(sb, "capture: a blank surface reads as fully uniform",
                        AiBrain.UniformityPercent(flat) == 100);
                }
                using (var busy = new System.Drawing.Bitmap(64, 64))
                {
                    for (int yy = 0; yy < busy.Height; yy++)
                        for (int xx = 0; xx < busy.Width; xx++)
                            busy.SetPixel(xx, yy, System.Drawing.Color.FromArgb(
                                (xx * 4) % 256, (yy * 4) % 256, ((xx + yy) * 3) % 256));
                    ok &= Check(sb, "capture: a varied surface does not read as uniform",
                        AiBrain.UniformityPercent(busy) < 50);
                }
                ok &= Check(sb, "capture: uniformity of nothing is zero, not a crash",
                    AiBrain.UniformityPercent(null) == 0);

                ok &= Check(sb, "windows: no windows produces no line",
                    AiBrain.DescribeOtherWindows(new ScreenContext { MonitorBounds = thisMon,
                        Windows = new System.Collections.Generic.List<ScreenWindow>() }) == "");

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
