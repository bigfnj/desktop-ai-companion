using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;
using DesktopAICompanion.CodingAgent;
using DesktopAICompanion.ModuleKit.Testing;   // FakeCompanion (through StandDownRig)
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Burn-down lane aibrain-1 (aibrain 1.7.1): AI Brain's items of the 2026-11-06 full audit. The refresh buttons list
    /// what the pane shows (N-audit-1106-009), the Goes through row says what a remark sends (-010), a custom provider's
    /// base URL typed with the switch is kept (-011), a body dropped after the headers is retried and falls over (-012),
    /// the Status card's fallback words follow the brain CreateBrain builds (-122), the hotkey's companion outlives the
    /// one it last saw (-123), the drop's cooldown runs from the answer too (-124), and the "keep" warm-up loads the model
    /// the first remark sends (-125). No server, no model, no screen: listings go through the module's seam, HTTP through
    /// an in-process handler, turns end in the rig's AskSink.
    /// </summary>
    public static partial class AiEngineProbe
    {
        private static bool CheckAudit1106Lane(StringBuilder sb)
        {
            bool ok = true;
            ok &= GuardedCheck(sb, "CheckAudit1106Refresh", CheckAudit1106Refresh);
            ok &= GuardedCheck(sb, "CheckAudit1106SendsRow", CheckAudit1106SendsRow);
            ok &= GuardedCheck(sb, "CheckAudit1106CustomEndpoint", CheckAudit1106CustomEndpoint);
            ok &= GuardedCheck(sb, "CheckAudit1106DroppedBody", CheckAudit1106DroppedBody);
            ok &= GuardedCheck(sb, "CheckAudit1106FallbackWords", CheckAudit1106FallbackWords);
            ok &= GuardedCheck(sb, "CheckAudit1106Anchor", CheckAudit1106Anchor);
            ok &= GuardedCheck(sb, "CheckAudit1106Cooldown", CheckAudit1106Cooldown);
            ok &= GuardedCheck(sb, "CheckAudit1106WarmUp", CheckAudit1106WarmUp);
            return ok;
        }

        private static string Normalized(string endpoint)
        {
            string normalized, error;
            return AiEndpointPolicy.TryNormalize(endpoint, out normalized, out error) ? normalized : "(refused: " + error + ")";
        }

        /// <summary>N-audit-1106-009: both Refresh buttons list against the settings on screen, applied or not; pressed with
        /// the saved values they list the saved ones (the WITNESS).</summary>
        private static bool CheckAudit1106Refresh(StringBuilder sb)
        {
            bool ok = true;
            const string TypedKey = "sk-or-AIBRAIN-SELFTEST-typed-not-a-real-key";
            using (var rig = new StandDownRig("aibrain-audit1106-refresh"))
            {
                AiSettings listedWith = null;
                string listedAt = null;
                bool listedLocal = false;
                rig.Module.ListingBackendForDiagnostics = delegate(AiSettings s, string endpoint, bool local)
                {
                    listedWith = s;
                    listedAt = endpoint;
                    listedLocal = local;
                    return new FixedListingBackend("selftest-model-a");
                };
                PaneAction cloud = FindAction(rig.Pane, "Cloud provider", "Refresh cloud models");
                PaneAction local = FindAction(rig.Pane, "Local provider", "Refresh local models");

                string cloudAnswer = PressWith(cloud, "brainRunsOn", "Cloud provider", "cloudProvider", "openrouter", "apiKey", TypedKey);
                ok &= Check(sb, "aibrain audit-1106: Refresh cloud models lists the provider, base URL and key on screen, applied or not: " +
                                (cloudAnswer ?? "(no answer)") + " at " + (listedAt ?? "(nothing listed)"),
                    cloudAnswer != null && cloudAnswer.StartsWith("✓ 1 model(s) found", StringComparison.Ordinal) &&
                    listedWith != null && !listedLocal && listedWith.Provider == "openrouter" &&
                    listedAt == Normalized("https://openrouter.ai/api/v1") && listedWith.ApiKey == TypedKey &&
                    rig.Module.SettingsForDiagnostics.Provider == "");

                listedWith = null;
                string localAnswer = PressWith(local, "brainRunsOn", "Local model", "localBackendKind", "Ollama (native)",
                    "endpoint", "http://127.0.0.1:8");
                ok &= Check(sb, "aibrain audit-1106: Refresh local models lists the local backend and endpoint on screen, applied or not: " +
                                (localAnswer ?? "(no answer)") + " at " + (listedAt ?? "(nothing listed)"),
                    localAnswer != null && localAnswer.StartsWith("✓ 1 model(s) found", StringComparison.Ordinal) &&
                    listedWith != null && listedLocal && listedAt == Normalized("http://127.0.0.1:8") &&
                    listedWith.LocalBackendKind == "ollama" && rig.Module.SettingsForDiagnostics.Endpoint == "http://127.0.0.1:9");

                listedWith = null;
                Task<string> savedPress = local != null && local.InvokeAsync != null ? local.InvokeAsync() : null;
                string savedAnswer = savedPress != null && savedPress.Wait(TimeSpan.FromSeconds(20)) ? savedPress.Result : null;
                ok &= Check(sb, "WITNESS aibrain audit-1106: pressed with the saved values, Refresh local models lists the saved endpoint and backend",
                    savedAnswer != null && listedWith != null && listedAt == Normalized("http://127.0.0.1:9") &&
                    listedWith.LocalBackendKind == "openai-compat");
            }
            return ok;
        }

        /// <summary>N-audit-1106-010: the CLI card's Goes through row names what a remark sends, pinned to the two functions
        /// that decide it: DescribeScreenContext (both window titles, the other apps' names, how many) and ChooseCaptureBounds
        /// (the whole monitor when the front window is not a sensible subject).</summary>
        private static bool CheckAudit1106SendsRow(StringBuilder sb)
        {
            bool ok = true;
            var monitor = new Rectangle(0, 0, 1920, 1080);
            var others = new List<ScreenWindow>();
            for (int i = 0; i < AiBrain.MaximumOtherWindowsDescribed + 2; i++)
                others.Add(new ScreenWindow { ProcessName = "selftestapp" + i, Bounds = new PixelRect(10, 10, 400, 300) });
            string described = AiBrain.DescribeScreenContext(new ScreenContext
            {
                WindowTitle = "SELFTEST-FRONT-TITLE",
                MonitorBounds = new PixelRect(monitor.X, monitor.Y, monitor.Width, monitor.Height),
                Windows = others,
            }, "SELFTEST-UNDER-TITLE");
            int named = 0;
            for (int i = 0; i < others.Count; i++) if (described.Contains("selftestapp" + i)) named++;
            bool wholeMonitor = AiBrain.ChooseCaptureBounds(default(PixelRect), monitor) == monitor;
            string row = AiBrainModule.CliSendsLine(new AiSettings { UseVision = true }, CodingAgentKind.Claude, false);
            ok &= Check(sb, "aibrain audit-1106: a remark sends both window titles and " + named + " other apps' names (DescribeScreenContext)",
                described.Contains("SELFTEST-FRONT-TITLE") && described.Contains("SELFTEST-UNDER-TITLE") &&
                named == AiBrain.MaximumOtherWindowsDescribed);
            ok &= Check(sb, "aibrain audit-1106: the Goes through row names both window titles and up to that many other apps: " + row,
                row.Contains("the front window's title, the title of the window the companion stands on and the names of up to " +
                             AiBrain.MaximumOtherWindowsDescribed + " other apps open on the companion's monitor"));
            ok &= Check(sb, "aibrain audit-1106: the Goes through row says the screenshot can be the companion's whole monitor, as ChooseCaptureBounds shoots it",
                wholeMonitor && row.Contains("a screenshot of the front window, or of the companion's whole monitor when that window is too small or not on it"));
            return ok;
        }

        /// <summary>N-audit-1106-011: a switch to custom with a base URL typed in the same Apply keeps it, and a key typed
        /// beside it is stored; a switch with the field as Load showed it (the stale URL) still ignores the field (the
        /// WITNESS, both ways round).</summary>
        private static bool CheckAudit1106CustomEndpoint(StringBuilder sb)
        {
            bool ok = true;
            const string TypedKey = "sk-custom-AIBRAIN-SELFTEST-typed-not-a-real-key";
            const string Typed = "http://127.0.0.1:7/v1";
            using (var rig = new StandDownRig("aibrain-audit1106-custom"))
            {
                OptionsPane pane = rig.Pane;
                bool saved = pane != null && pane.Save(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "brainRunsOn", "Cloud provider" }, { "cloudProvider", "custom" }, { "cloudEndpoint", Typed }, { "apiKey", TypedKey },
                });
                AiSettings s = rig.Module.SettingsForDiagnostics;
                ok &= Check(sb, "aibrain audit-1106: choosing custom and typing its base URL in one Apply keeps the URL and stores the key beside it: " +
                                (saved ? "saved, " : "refused, ") + (s.OpenAiBaseUrl ?? "(null)"),
                    saved && s.Provider == "custom" && s.OpenAiBaseUrl == Typed && s.CustomOpenAiBaseUrl == Typed && s.ApiKey == TypedKey &&
                    AiSettings.Load().OpenAiBaseUrl == Typed);

                string shown = pane.Load()["cloudEndpoint"];
                pane.Save(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "brainRunsOn", "Cloud provider" }, { "cloudProvider", "openai" }, { "cloudEndpoint", shown },
                });
                bool presetWon = s.Provider == "openai" && s.OpenAiBaseUrl == "https://api.openai.com/v1";
                shown = pane.Load()["cloudEndpoint"];
                pane.Save(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "brainRunsOn", "Cloud provider" }, { "cloudProvider", "custom" }, { "cloudEndpoint", shown },
                });
                ok &= Check(sb, "WITNESS aibrain audit-1106: a switch with the URL field as Load showed it ignores the stale field, to a preset and back to custom: " +
                                (s.OpenAiBaseUrl ?? "(null)"),
                    presetWon && s.Provider == "custom" && s.OpenAiBaseUrl == Typed);
            }
            return ok;
        }

        /// <summary>N-audit-1106-012: a connection dropped after the response headers (HttpIOException from the body read)
        /// is retried once and falls over to the local slot; the size cap stays one attempt (the WITNESS).</summary>
        private static bool CheckAudit1106DroppedBody(StringBuilder sb)
        {
            bool ok = true;
            var messages = new List<ChatMessage> { ChatMessage.User("hello", null) };
            var handler = new DroppedBodyHandler();
            using (var cloud = new OpenAiCompatBackend("https://cloud.selftest.invalid/v1", "", TimeSpan.FromSeconds(10), handler))
            {
                Exception thrown = null;
                try { AiBrain.ChatWithRetryForDiagnosticsAsync(cloud, "cloud-text", messages, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) { thrown = ex; }
                ok &= Check(sb, "aibrain audit-1106: a body dropped after the response headers is retried once (" + handler.Requests + " requests, " +
                                (thrown == null ? "no exception" : thrown.GetType().Name) + ")",
                    handler.Requests == 2 && thrown is HttpIOException);
            }
            var dropped = new DroppedBodyHandler();
            var local = new RecordingBackend("{\"text\":\"local\",\"emotion\":\"neutral\"}", true);
            using (var cloud = new OpenAiCompatBackend("https://cloud.selftest.invalid/v1", "", TimeSpan.FromSeconds(10), dropped))
            using (var composite = new FallbackBackend(cloud, local, "cloud-vision", "local-text", "local-vision"))
            {
                string reply = null;
                try { reply = composite.ChatAsync("cloud-text", messages, true, CancellationToken.None).GetAwaiter().GetResult(); }
                catch { reply = null; }
                ok &= Check(sb, "aibrain audit-1106: a body dropped after the response headers falls over to the local model",
                    dropped.Requests == 1 && local.ChatCalls == 1 && local.LastModel == "local-text" &&
                    reply != null && reply.Contains("local"));
            }
            ok &= Check(sb, "WITNESS aibrain audit-1106: an oversized or undecodable answer, a CLI failure and a cancelled call stay one attempt",
                !AiEndpointPolicy.IsRetryable(new InvalidDataException("AI response exceeds its size limit."), CancellationToken.None) &&
                !AiEndpointPolicy.IsRetryable(new System.Text.DecoderFallbackException("bad"), CancellationToken.None) &&
                !AiEndpointPolicy.IsRetryable(new IOException("dropped"), new CancellationToken(true)));
            return ok;
        }

        /// <summary>N-audit-1106-122: the Status card says the local fallback is on only when CreateBrain builds it, which is
        /// for a loopback local endpoint; ticked with Ollama on another machine, it says off and why.</summary>
        private static bool CheckAudit1106FallbackWords(StringBuilder sb)
        {
            bool ok = true;
            Func<string, AiSettings> cloudWith = delegate(string localEndpoint)
            {
                return new AiSettings
                {
                    Provider = "openai", OpenAiBaseUrl = "https://api.openai.com/v1", CloudDataConsent = true, CloudTextModel = "gpt-selftest",
                    UseLocalFallback = true, Endpoint = localEndpoint, AutoStartServer = false,
                };
            };
            var module = new AiBrainModule();
            AiSettings remote = cloudWith("http://192.0.2.10:11434");
            AiSettings onThisComputer = cloudWith("http://127.0.0.1:9");
            string remoteLine = module.StatusRowLine(remote, null);
            string localLine = module.StatusRowLine(onThisComputer, null);
            bool remoteComposite, localComposite;
            using (AiBrain brain = AiBrainModule.CreateBrain(remote)) remoteComposite = brain.BackendForDiagnostics is FallbackBackend;
            using (AiBrain brain = AiBrainModule.CreateBrain(onThisComputer)) localComposite = brain.BackendForDiagnostics is FallbackBackend;
            ok &= Check(sb, "aibrain audit-1106: with the fallback ticked and a local endpoint on another machine, the Status card says it is off, as the brain is built: " + remoteLine,
                !remoteComposite && remoteLine.Contains(", local fallback off (it needs a local endpoint on this computer)") &&
                !remoteLine.Contains("local fallback on"));
            ok &= Check(sb, "WITNESS aibrain audit-1106: with a loopback local endpoint the card says on, and the brain is the composite: " + localLine,
                localComposite && localLine.Contains(", local fallback on"));
            return ok;
        }

        /// <summary>N-audit-1106-123: the tray row (the hotkey's call) asks the most recently seen companion still on screen
        /// when the one it last saw is gone; with none left it still says no companion (the WITNESS).</summary>
        private static bool CheckAudit1106Anchor(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-audit1106-anchor"))
            {
                ok &= Check(sb, "aibrain audit-1106 rig (anchor): the live brain is built over the counting double", rig.WaitForLiveBrain());
                var removed = new FakeCompanion(12, "eSheep");
                rig.Host.RaiseCompanionSpawned(removed);   // the last one seen, then removed
                rig.Host.CompanionAlivePredicate = delegate(ICompanion pet) { return pet != null && !ReferenceEquals(pet, removed); };
                bool offered = ClickTray(rig.Host, "Ask about my screen");
                List<KeyValuePair<string, bool>> started = rig.Started;
                ok &= Check(sb, "aibrain audit-1106: with the last companion seen gone, the tray ask goes to another one still on screen (" +
                                started.Count + " started)",
                    offered && started.Count == 1 && started[0].Key == "11");
                rig.Host.CompanionAlivePredicate = delegate(ICompanion pet) { return false; };
                ClickTray(rig.Host, "Ask about my screen");
                ok &= Check(sb, "WITNESS aibrain audit-1106: with no companion left the tray ask still declines as no companion",
                    rig.Started.Count == 1 && rig.CountLog("ask declined: no companion") == 1);
            }
            return ok;
        }

        /// <summary>N-audit-1106-124: the drop's 30 s cooldown runs from the answer's arrival too, so a slow ask's answer is
        /// not followed straight away by a drop's remark; a turn with no answer moves nothing (the WITNESS).</summary>
        private static bool CheckAudit1106Cooldown(StringBuilder sb)
        {
            bool ok = true;
            using (var rig = new StandDownRig("aibrain-audit1106-cooldown"))
            {
                ok &= Check(sb, "aibrain audit-1106 rig (cooldown): the live brain is built over the counting double", rig.WaitForLiveBrain());
                DateTime start = rig.Now;
                bool asked = ClickTray(rig.Host, "Ask about my screen") && rig.Started.Count == 1;
                rig.Now = start + TimeSpan.FromSeconds(40);
                rig.Module.RecordRemark(DateTime.Now, 40000, null, "selftest-slow-model");   // the slow ask's answer arrives
                rig.Now = start + TimeSpan.FromSeconds(41);
                bool dropAfterAnswer = rig.Host.RaiseDrop(rig.Pet);
                ok &= Check(sb, "aibrain audit-1106: a drop one second after a slow ask's answer is declined, though 41 s have passed since the ask began",
                    asked && !dropAfterAnswer && rig.Started.Count == 1);
                rig.Now = start + TimeSpan.FromSeconds(80);
                rig.Module.RecordRemark(DateTime.Now, 1000, "timeout", null);   // a turn that ended with no answer
                rig.Now = start + TimeSpan.FromSeconds(100);
                ok &= Check(sb, "WITNESS aibrain audit-1106: a turn with no answer moves nothing, so a drop 60 s after the last answer starts a turn",
                    rig.Host.RaiseDrop(rig.Pet) && rig.Started.Count == 2);
            }
            return ok;
        }

        /// <summary>N-audit-1106-125: the "keep" warm-up loads the model the first remark will send: the substitute when the
        /// inventory lacks the configured one, nothing on a cloud primary that never substitutes, the configured one when it
        /// is there (the WITNESS).</summary>
        private static bool CheckAudit1106WarmUp(StringBuilder sb)
        {
            bool ok = true;
            Func<string[], Func<CancellationToken, Task<IReadOnlyList<ModelListing>>>> listing = delegate(string[] ids)
            {
                var models = new List<ModelListing>();
                foreach (string id in ids) models.Add(new ModelListing(id, false));
                return delegate(CancellationToken ct) { return Task.FromResult((IReadOnlyList<ModelListing>)models); };
            };
            Func<AiSettings> keep = delegate
            {
                return new AiSettings
                {
                    ModelResidency = AiSettings.ResidencyKeep, AutoStartServer = false, UseVision = false, TextModel = "selftest-missing:1b",
                };
            };

            var substituted = new RecordingBackend("", true);
            using (var brain = new AiBrain(substituted, keep()))
            {
                brain.ModelLister = listing(new[] { "selftest-installed:2b" });
                brain.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                brain.UnloadAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "aibrain audit-1106: under keep, the launch warm-up loads the substitute the first remark sends when the configured model is missing (" +
                                (substituted.LastWarmUpModel ?? "(nothing warmed)") + "), and the release covers it",
                    substituted.WarmUpCalls == 1 && substituted.LastWarmUpModel == "selftest-installed:2b" &&
                    substituted.UnloadedModels.Contains("selftest-installed:2b"));
            }
            var cloudPrimary = new RecordingBackend("", true);
            using (var brain = new AiBrain(cloudPrimary, keep()))
            {
                brain.SubstituteMissingModel = false;
                brain.ModelLister = listing(new[] { "selftest-installed:2b" });
                brain.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "aibrain audit-1106: on a primary that never substitutes, a missing model warms nothing (" + cloudPrimary.WarmUpCalls + " warm-ups)",
                    cloudPrimary.WarmUpCalls == 0);
            }
            var present = new RecordingBackend("", true);
            using (var brain = new AiBrain(present, keep()))
            {
                brain.ModelLister = listing(new[] { "selftest-installed:2b", "selftest-missing:1b" });
                brain.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS aibrain audit-1106: with the configured model installed, the warm-up loads it",
                    present.WarmUpCalls == 1 && present.LastWarmUpModel == "selftest-missing:1b");
            }
            return ok;
        }
    }
}
