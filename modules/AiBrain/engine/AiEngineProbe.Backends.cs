using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// The cloud slot and the backends, offline: what a cloud provider needs before the brain will build, what the
    /// cloud+local composite can say about its models, how the vision gate treats a backend that reports no
    /// capabilities, and how an ANSWERED HTTP failure is described to the pane and to the log. Every request here
    /// goes to a handler double; nothing on this machine is contacted.
    /// </summary>
    public static partial class AiEngineProbe
    {
        internal static bool RunBackends(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckCloudSlotNeedsAModel(sb);
            ok &= CheckCompositeEnumerates(sb);
            ok &= CheckVisionGateOnUnreportedBackends(sb);
            ok &= CheckAnsweredStatusDescriptions(sb);
            ok &= CheckListingResponseCap(sb);
            ok &= CheckFailureLineFields(sb);
            ok &= CheckListingIsBounded(sb);
            ok &= CheckCloudPrimaryNeverSubstitutes(sb);
            return ok;
        }

        /// <summary>
        /// F101. A cloud slot has no model until the user picks one, so a first-time cloud setup naturally leaves
        /// it blank; the brain used to fill the blank with the LOCAL default and send "gemma3:4b" to the provider.
        /// Two gates now: CanUse refuses the configuration (ApplyState logs why), and the brain's constructor keeps
        /// a cloud blank blank, so even a hand-edited file gets a spoken "pick one" instead of an Ollama tag on the
        /// wire.
        /// </summary>
        private static bool CheckCloudSlotNeedsAModel(StringBuilder sb)
        {
            bool ok = true;
            string why;
            var cloud = new AiSettings
            {
                Provider = "openai",
                OpenAiBaseUrl = "https://api.openai.com/v1",
                CloudDataConsent = true,
                CloudTextModel = "",
            };
            ok &= Check(sb, "a cloud provider with no cloud text model does not pass CanUse, and the reason names the fix",
                !AiBrainModule.CanUse(cloud, out why) &&
                why != null && why.IndexOf("cloud text model", StringComparison.OrdinalIgnoreCase) >= 0);
            cloud.CloudTextModel = "gpt-4o-mini";
            ok &= Check(sb, "WITNESS the same cloud provider with a text model passes CanUse",
                AiBrainModule.CanUse(cloud, out why));
            cloud.UseVision = true;
            ok &= Check(sb, "with vision on, a cloud provider also needs a cloud vision model",
                !AiBrainModule.CanUse(cloud, out why) &&
                why != null && why.IndexOf("vision", StringComparison.OrdinalIgnoreCase) >= 0);
            cloud.CloudVisionModel = "gpt-4o";
            ok &= Check(sb, "WITNESS ...and passes once one is picked", AiBrainModule.CanUse(cloud, out why));
            ok &= Check(sb, "WITNESS the local slot never needs a cloud model",
                AiBrainModule.CanUse(new AiSettings(), out why));

            // The brain itself, on a cloud snapshot with a blank model: nothing may be sent, and the user is told.
            var reachable = new RecordingBackend("{\"text\":\"hi\",\"emotion\":\"happy\"}", true);
            var blankCloud = new AiSettings
            {
                Provider = "openrouter",
                OpenAiBaseUrl = "https://openrouter.ai/api/v1",
                CloudDataConsent = true,
                CloudTextModel = "",
            };
            using (var brain = new AiBrain(reachable, blankCloud.ActiveSlotSnapshot()))
            {
                DispositionAudition audition = brain.SampleDispositionAsync(
                    "pirate", TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "a cloud snapshot with no model never invents the local default: nothing is sent and the user is told to pick one",
                    reachable.ChatCalls == 0 &&
                    audition.Samples.Count == 1 && !audition.Samples[0].Ok &&
                    audition.Advisory != null && audition.Advisory.IndexOf("Pick one", StringComparison.Ordinal) >= 0);
            }
            // The LOCAL slot keeps its literal fallback: an un-normalized local snapshot with a blank model still
            // runs on the shipped default, exactly as before.
            var localReachable = new RecordingBackend("{\"text\":\"hi\",\"emotion\":\"happy\"}", true);
            using (var brain = new AiBrain(localReachable, new AiSettings { TextModel = "" }))
            {
                brain.SampleDispositionAsync("pirate", TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult();
                ok &= Check(sb, "WITNESS the LOCAL slot keeps its gemma3:4b fallback for a blank model",
                    localReachable.ChatCalls == DispositionScenes.All.Length && localReachable.LastModel == "gemma3:4b");
            }
            return ok;
        }

        /// <summary>F103. The composite lists its PRIMARY's models; a backend that cannot list yields null, never
        /// an empty list that reads as "known and empty".</summary>
        private static bool CheckCompositeEnumerates(StringBuilder sb)
        {
            bool ok = true;
            const string modelsJson =
                "{\"data\":[{\"id\":\"openai/gpt-4o-mini\"},{\"id\":\"anthropic/claude-sonnet-5.5\"}]}";
            using (var handler = new FixedJsonResponseHandler(modelsJson))
            using (var cloud = new OpenAiCompatBackend("https://openrouter.ai/api/v1", "", TimeSpan.FromSeconds(5), handler))
            using (var local = new RecordingBackend("", true))
            using (var composite = new FallbackBackend(cloud, local, "", "local-text", "local-vision"))
            {
                IReadOnlyList<ModelListing> listed = AiBrainModule.ListBackendModelsAsync(
                    composite, CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "the cloud+local composite can be enumerated, and lists the PRIMARY's models (BUG-002 applies to cloud ids too)",
                    listed != null && listed.Count == 2 && FindModel(listed, "openai/gpt-4o-mini") != null);
            }
            ok &= Check(sb, "WITNESS a backend that cannot enumerate yields null, so ChooseModel does not complain",
                AiBrainModule.ListBackendModelsAsync(new RecordingBackend("", true), CancellationToken.None)
                    .GetAwaiter().GetResult() == null);
            using (var composite = new FallbackBackend(
                new RecordingBackend("", true), new RecordingBackend("", true), "", "t", "v"))
            {
                ok &= Check(sb, "a composite over a primary that cannot enumerate answers null too, not an empty list",
                    AiBrainModule.ListBackendModelsAsync(composite, CancellationToken.None)
                        .GetAwaiter().GetResult() == null);
            }
            return ok;
        }

        /// <summary>
        /// F102. On a backend that reports nothing about capabilities (every /v1 list), the hand-maintained
        /// marker list is a hint, not a gate: a configured vision model it does not know is used as configured,
        /// while a model the backend REPORTS as text-only is still substituted. An empty id is its own answer.
        /// </summary>
        private static bool CheckVisionGateOnUnreportedBackends(StringBuilder sb)
        {
            bool ok = true;
            var v1List = new List<ModelListing>
            {
                new ModelListing("anthropic/claude-sonnet-5.5", null),
                new ModelListing("acme/seer-9000", null),       // a vision model no marker knows
                new ModelListing("dolphin3:latest", false),      // a backend that SAYS it cannot see
            };
            ModelChoice unverified = AiModelPolicy.ChooseModel("acme/seer-9000", v1List, true);
            ok &= Check(sb, "a listed vision model with no marker and no report is used as configured, not rerouted to another vendor's",
                unverified.Model == "acme/seer-9000" && unverified.Advisory == null &&
                unverified.Reason == "configured-unverified-vision");
            ModelChoice reportedBlind = AiModelPolicy.ChooseModel("dolphin3:latest", v1List, true);
            ok &= Check(sb, "WITNESS a model the backend REPORTS as text-only is still substituted on a vision ask, and the advisory says why",
                reportedBlind.Reason == "substituted" && reportedBlind.Model == "anthropic/claude-sonnet-5.5" &&
                reportedBlind.Advisory != null &&
                reportedBlind.Advisory.IndexOf("can't see images", StringComparison.Ordinal) >= 0);
            ModelChoice absent = AiModelPolicy.ChooseModel("nobody/has-this", v1List, true);
            ok &= Check(sb, "WITNESS an absent id is still substituted, and its advisory says it isn't available",
                absent.Reason == "substituted" &&
                absent.Advisory != null && absent.Advisory.IndexOf("isn't available", StringComparison.Ordinal) >= 0);
            ok &= Check(sb, "WITNESS a text ask on the same list uses the configured model without a second thought",
                AiModelPolicy.ChooseModel("acme/seer-9000", v1List, false).Reason == "configured");

            // R-020: the substitution loop applies the gate's own rule, so an advisory can never name the model it
            // rejected, and a reported-blind model is never the substitute even when its name carries a marker. The
            // fixture is what an older Ollama's /api/tags reported for Gemma on 2026-09-10 (0.34.4 reports vision).
            var oldTags = new List<ModelListing>
            {
                new ModelListing("gemma3:4b", false),
                new ModelListing("gemma3:12b", false),
                new ModelListing("llava:13b", true),
            };
            ModelChoice consistent = AiModelPolicy.ChooseModel("gemma3:4b", oldTags, true);
            ok &= Check(sb, "a reported-blind model is never the substitute, marker or not: the loop applies the gate's rule (R-020)",
                consistent.Reason == "substituted" && consistent.Model == "llava:13b" &&
                consistent.Advisory != null && consistent.Advisory.IndexOf("using llava:13b", StringComparison.Ordinal) >= 0);
            var onlyBlind = new List<ModelListing> { new ModelListing("gemma3:4b", false), new ModelListing("gemma3:12b", false) };
            ModelChoice noSelf = AiModelPolicy.ChooseModel("gemma3:4b", onlyBlind, true);
            ok &= Check(sb, "an advisory can never name the model it rejected: with only reported-blind listings the answer is none-usable",
                !noSelf.Usable && noSelf.Reason == "none-usable");
            var unreportedMarker = new List<ModelListing> { new ModelListing("gemma3:4b", false), new ModelListing("acme/gemma3-vl", null) };
            ok &= Check(sb, "WITNESS a marker-matching model with NO report is still the substitute (the union where nothing was reported)",
                AiModelPolicy.ChooseModel("gemma3:4b", unreportedMarker, true).Model == "acme/gemma3-vl");

            // The lone local /v1 model: one file, no marker, nothing else to fall back to. Used, not refused.
            var oneFile = new List<ModelListing> { new ModelListing("Local-Seer-24B-Q4_K_M.gguf", null) };
            ok &= Check(sb, "a lone local /v1 model with no marker is usable for vision rather than refused as none-usable",
                AiModelPolicy.ChooseModel("Local-Seer-24B-Q4_K_M.gguf", oneFile, true).Usable);

            // The marker list itself, refreshed 2026-09-29 with the families that had shipped since it was written.
            ok &= Check(sb, "the marker list knows the vision families that shipped since it was written (gpt-5, gemini-3, grok-4, mistral-small-3, glm-4.5v)",
                AiModelPolicy.LooksVisionCapable("openai/gpt-5.1") &&
                AiModelPolicy.LooksVisionCapable("google/gemini-3-pro") &&
                AiModelPolicy.LooksVisionCapable("x-ai/grok-4.7") &&
                AiModelPolicy.LooksVisionCapable("mistralai/mistral-small-3.2-24b-instruct") &&
                AiModelPolicy.LooksVisionCapable("z-ai/glm-4.5v"));

            // No id at all (F101's other face): an advisory the user can act on, never a substitution.
            ModelChoice blank = AiModelPolicy.ChooseModel("", v1List, false);
            ok &= Check(sb, "an empty configured id is 'none-configured' with an advisory, never a substitution billed to the user's key",
                !blank.Usable && blank.Reason == "none-configured" &&
                blank.Advisory != null && blank.Advisory.IndexOf("Pick one", StringComparison.Ordinal) >= 0);
            ok &= Check(sb, "...and the same with an unknown inventory",
                AiModelPolicy.ChooseModel("", null, true).Reason == "none-configured");
            return ok;
        }

        /// <summary>
        /// R-014. A model listing runs under its own bound, not the chat deadline, and an empty re-list never replaces
        /// a good inventory.
        /// </summary>
        private static bool CheckListingIsBounded(StringBuilder sb)
        {
            bool ok = true;
            ok &= Check(sb, "the listing bound is its own: above the 10 s probe bound (a listing is a body, not a handshake) and well under a 120 s chat deadline",
                AiEndpointPolicy.ListingDeadline > AiEndpointPolicy.AvailabilityProbeDeadline &&
                AiEndpointPolicy.ListingDeadline < TimeSpan.FromSeconds(120));
            using (var hung = new BlockingHeadersHandler())
            using (var cloud = new OpenAiCompatBackend("https://openrouter.ai/api/v1", "", TimeSpan.FromSeconds(5), hung, null, TimeSpan.FromMilliseconds(200)))
            {
                var clock = Stopwatch.StartNew();
                IReadOnlyList<ModelListing> listed = cloud.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "a cloud listing that hangs is bounded by the listing deadline, not by the 5 s chat deadline it used to borrow (R-014)",
                    listed != null && listed.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(3));
            }
            using (var hung = new BlockingHeadersHandler())
            using (var ollama = new OllamaClient("http://127.0.0.1:11434", TimeSpan.FromSeconds(5), null, hung,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100),
                delegate { return false; }, TimeSpan.FromMilliseconds(200)))
            {
                var clock = Stopwatch.StartNew();
                IReadOnlyList<ModelListing> listed = ollama.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "the local listing is bounded the same way",
                    listed != null && listed.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(3));
            }
            using (var answers = new FixedJsonResponseHandler("{\"data\":[{\"id\":\"openai/gpt-4o-mini\"}]}"))
            using (var cloud = new OpenAiCompatBackend("https://openrouter.ai/api/v1", "", TimeSpan.FromSeconds(5), answers, null, TimeSpan.FromMilliseconds(200)))
            {
                ok &= Check(sb, "WITNESS a listing that answers lists under the same bound",
                    cloud.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 1);
            }

            // An empty re-list, which is what a tripped bound yields, keeps what the brain knew.
            var backend = new RecordingBackend("{\"text\":\"hi\",\"emotion\":\"happy\"}", true);
            using (var brain = new AiBrain(backend, new AiSettings { TextModel = "gemma3:4b" }))
            {
                IReadOnlyList<ModelListing> next = new List<ModelListing> { new ModelListing("gemma3:4b", true) };
                brain.ModelLister = delegate(CancellationToken ct) { return Task.FromResult(next); };
                brain.RefreshInventoryAsync(CancellationToken.None).GetAwaiter().GetResult();
                next = new List<ModelListing>();
                brain.RefreshInventoryAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModelChoice choice;
                BrainResponse advisory;
                brain.ResolveBeforeCapture(false, out choice, out advisory);
                ok &= Check(sb, "an empty re-list (a bound that tripped) keeps the previous inventory instead of replacing it",
                    choice.Reason == "configured");
            }
            using (var brain = new AiBrain(backend, new AiSettings { TextModel = "gemma3:4b" }))
            {
                brain.ModelLister = delegate(CancellationToken ct)
                {
                    return Task.FromResult((IReadOnlyList<ModelListing>)new List<ModelListing>());
                };
                brain.RefreshInventoryAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModelChoice choice;
                BrainResponse advisory;
                brain.ResolveBeforeCapture(false, out choice, out advisory);
                ok &= Check(sb, "WITNESS a first listing that is empty leaves the inventory unknown",
                    choice.Reason == "model-list-unknown");
            }
            return ok;
        }

        /// <summary>
        /// R-022. Substitution is a local courtesy: on a cloud primary a configured id the provider does not offer ends
        /// the turn on an advisory naming the host, and nothing the user did not choose is billed.
        /// </summary>
        private static bool CheckCloudPrimaryNeverSubstitutes(StringBuilder sb)
        {
            bool ok = true;
            var v1 = new List<ModelListing>
            {
                new ModelListing("openai/gpt-4o-mini", null),
                new ModelListing("anthropic/claude-sonnet-5.5", null),
            };
            ModelChoice cloud = AiModelPolicy.ChooseModel("openai/gpt-4.1-mini", v1, false, false, "openrouter.ai->localhost");
            ok &= Check(sb, "a cloud primary never substitutes: a configured id the provider does not offer is 'none-offered' with an advisory naming the host (R-022)",
                !cloud.Usable && cloud.Reason == "none-offered" && cloud.Advisory != null &&
                cloud.Advisory.IndexOf("openrouter.ai", StringComparison.Ordinal) >= 0 &&
                cloud.Advisory.IndexOf("Pick a model", StringComparison.Ordinal) >= 0);
            ok &= Check(sb, "WITNESS the same inventory on a local backend substitutes, as BUG-002 intended",
                AiModelPolicy.ChooseModel("openai/gpt-4.1-mini", v1, false).Reason == "substituted");
            ok &= Check(sb, "WITNESS a configured id the provider DOES offer is used as configured on a cloud primary",
                AiModelPolicy.ChooseModel("openai/gpt-4o-mini", v1, false, false, "openrouter.ai").Reason == "configured");

            // The policy is set from the primary slot by the module's own factory (construction touches no network).
            var settings = new AiSettings
            {
                Provider = "openai",
                OpenAiBaseUrl = "https://api.openai.com/v1",
                CloudDataConsent = true,
                CloudTextModel = "gpt-4.1-mini",
                UseLocalFallback = true,
            };
            using (AiBrain composite = AiBrainModule.CreateBrain(settings))
                ok &= Check(sb, "CreateBrain turns substitution off for a cloud primary with the local fallback", !composite.SubstituteMissingModel);
            settings.UseLocalFallback = false;
            using (AiBrain cloudOnly = AiBrainModule.CreateBrain(settings))
                ok &= Check(sb, "...and for a cloud primary without it", !cloudOnly.SubstituteMissingModel);
            using (AiBrain local = AiBrainModule.CreateBrain(new AiSettings()))
                ok &= Check(sb, "WITNESS CreateBrain leaves substitution on for the local slot", local.SubstituteMissingModel);

            // Through the brain: the turn ends on the advisory before any capture, once, and the backend is never asked.
            var backend = new RecordingBackend("{\"text\":\"hi\",\"emotion\":\"happy\"}", true);
            using (var brain = new AiBrain(backend, new AiSettings { TextModel = "openai/gpt-4.1-mini" }))
            {
                brain.SubstituteMissingModel = false;
                brain.BackendHostDescription = "openrouter.ai->localhost";
                brain.ModelLister = delegate(CancellationToken ct) { return Task.FromResult((IReadOnlyList<ModelListing>)v1); };
                brain.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModelChoice choice;
                BrainResponse advisory;
                bool proceed = brain.ResolveBeforeCapture(false, out choice, out advisory);
                ok &= Check(sb, "on a cloud primary the turn ends on the advisory before any capture, and the backend is never asked",
                    !proceed && advisory != null && choice.Reason == "none-offered" && backend.ChatCalls == 0 &&
                    advisory.Text.IndexOf("openrouter.ai", StringComparison.Ordinal) >= 0);
                bool again = brain.ResolveBeforeCapture(false, out choice, out advisory);
                ok &= Check(sb, "WITNESS the advisory is spoken once; later asks end silently until the pane is fixed",
                    !again && advisory == null);
            }
            return ok;
        }

        /// <summary>
        /// F107 and F080. An answered status is reachable (the server is there and says no), is its own category
        /// in the log, and carries the provider's own words to the pane.
        /// </summary>
        private static bool CheckAnsweredStatusDescriptions(StringBuilder sb)
        {
            bool ok = true;
            var msgs = new List<ChatMessage> { ChatMessage.System("Reply with OK."), ChatMessage.User("OK?", null) };

            ok &= Check(sb, "an answered HTTP status is its own category in the log (http-401), not 'backend-unreachable'",
                AiBrain.DescribeError(new AiBackendHttpException(401, false)) == "http-401");
            ok &= Check(sb, "WITNESS a transport failure is still 'backend-unreachable'",
                AiBrain.DescribeError(new HttpRequestException("refused")) == "backend-unreachable");

            using (var handler = new FixedStatusHandler(
                HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}"))
            using (var backend = new OpenAiCompatBackend("https://api.openai.com/v1", "not-a-real-key-fixture", TimeSpan.FromSeconds(5), handler))
            {
                ok &= Check(sb, "an answered 401 is reachable: the server said the key is wrong, not that it is down",
                    backend.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult());

                AiBackendHttpException caught = null;
                try
                {
                    backend.ChatAsync("gpt-4o-mini", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (AiBackendHttpException ex) { caught = ex; }
                ok &= Check(sb, "the chat surfaces the 401 as a deterministic AiBackendHttpException (no retry, no fallover)",
                    caught != null && caught.StatusCode == 401 && !caught.IsTransient);
                ok &= Check(sb, "the provider's error message reaches the exception for the pane (never the log)",
                    caught != null && caught.ProviderMessage == "Incorrect API key provided");
                string line = caught == null ? "" : AiBrainModule.DescribeHttpFailure(caught, "https://api.openai.com/v1");
                ok &= Check(sb, "Test connection describes it as a rejected key from the named host, in the provider's own words",
                    line.IndexOf("HTTP 401", StringComparison.Ordinal) >= 0 &&
                    line.IndexOf("api.openai.com", StringComparison.Ordinal) >= 0 &&
                    line.IndexOf("API key", StringComparison.Ordinal) >= 0 &&
                    line.IndexOf("Incorrect API key provided", StringComparison.Ordinal) >= 0);
            }
            using (var handler = new RefusingHandler())
            using (var backend = new OpenAiCompatBackend("https://api.openai.com/v1", "", TimeSpan.FromSeconds(5), handler))
            {
                ok &= Check(sb, "WITNESS a transport failure is still not reachable",
                    !backend.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult());
            }
            using (var handler = new FixedStatusHandler(HttpStatusCode.Redirect, ""))
            using (var backend = new OpenAiCompatBackend("https://api.openai.com/v1", "", TimeSpan.FromSeconds(5), handler))
            {
                ok &= Check(sb, "WITNESS a redirect is still not reachable (credentials never follow one)",
                    !backend.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult());
            }

            // The message extraction on both provider shapes, and its bounds.
            ok &= Check(sb, "the provider message is read from OpenAI's error.message and from Ollama's bare error",
                AiEndpointPolicy.ExtractProviderErrorMessage("{\"error\":{\"message\":\"Insufficient credits\"}}") == "Insufficient credits" &&
                AiEndpointPolicy.ExtractProviderErrorMessage("{\"error\":\"model 'x' not found\"}") == "model 'x' not found");
            ok &= Check(sb, "a non-JSON or empty body yields no provider message",
                AiEndpointPolicy.ExtractProviderErrorMessage("<html>Bad Gateway</html>") == "" &&
                AiEndpointPolicy.ExtractProviderErrorMessage("") == "" &&
                AiEndpointPolicy.ExtractProviderErrorMessage(null) == "");
            string longMessage = AiEndpointPolicy.ExtractProviderErrorMessage(
                "{\"error\":{\"message\":\"" + new string('x', 500) + "\\r\\n\\ttail\"}}");
            ok &= Check(sb, "a provider message is bounded and stripped of line breaks and control characters",
                longMessage.Length <= AiEndpointPolicy.MaximumProviderMessageCharacters &&
                longMessage.IndexOf('\r') < 0 && longMessage.IndexOf('\n') < 0 && longMessage.IndexOf('\t') < 0);
            ok &= Check(sb, "WITNESS whitespace inside a message collapses to one space and the text survives",
                AiEndpointPolicy.ExtractProviderErrorMessage("{\"error\":{\"message\":\"not a\\n\\n  valid model ID\"}}") == "not a valid model ID");

            // The pane wording for the statuses a user can act on.
            ok &= Check(sb, "a 404 points at the base URL, a 402 at credits, a 5xx at the provider",
                AiBrainModule.DescribeHttpFailure(new AiBackendHttpException(404, false), "https://api.openai.com/v1")
                    .IndexOf("/v1", StringComparison.Ordinal) >= 0 &&
                AiBrainModule.DescribeHttpFailure(new AiBackendHttpException(402, false), "https://openrouter.ai/api/v1")
                    .IndexOf("credits", StringComparison.Ordinal) >= 0 &&
                AiBrainModule.DescribeHttpFailure(new AiBackendHttpException(503, true), "https://openrouter.ai/api/v1")
                    .IndexOf("provider", StringComparison.Ordinal) >= 0);
            return ok;
        }

        /// <summary>F078. A model catalogue is read under its own cap; a chat reply keeps the 1 MiB one.</summary>
        private static bool CheckListingResponseCap(StringBuilder sb)
        {
            bool ok = true;
            var msgs = new List<ChatMessage> { ChatMessage.User("x", null) };
            const int Ids = 30000;
            var big = new StringBuilder("{\"data\":[");
            for (int i = 0; i < Ids; i++)
            {
                if (i > 0) big.Append(',');
                big.Append("{\"id\":\"vendor/model-name-number-").Append(i).Append("\"}");
            }
            big.Append("]}");
            string bigJson = big.ToString();
            ok &= Check(sb, "the listing fixture exceeds the chat reply cap, or this check proves nothing",
                Encoding.UTF8.GetByteCount(bigJson) > AiEndpointPolicy.MaximumResponseBytes);
            using (var handler = new FixedJsonResponseHandler(bigJson))
            using (var backend = new OpenAiCompatBackend("https://openrouter.ai/api/v1", "", TimeSpan.FromSeconds(30), handler))
            {
                ok &= Check(sb, "a /models catalogue above the 1 MiB reply cap still lists (OpenRouter's stood at 72.5% of it on 2026-09-29)",
                    backend.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult().Count == Ids);
            }
            using (var handler = new FixedJsonResponseHandler(bigJson))
            using (var backend = new OpenAiCompatBackend("https://openrouter.ai/api/v1", "", TimeSpan.FromSeconds(30), handler))
            {
                ok &= Check(sb, "WITNESS a chat reply of that size is still refused: the wider cap is for listings only",
                    Throws<InvalidDataException>(delegate
                    {
                        backend.ChatAsync("m", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                    }));
            }
            return ok;
        }

        /// <summary>F073 and F072. The failure line names the path taken, and a substitution is announced before
        /// the screen is captured.</summary>
        private static bool CheckFailureLineFields(StringBuilder sb)
        {
            bool ok = true;
            var settings = new AiSettings { TextModel = "gemma3:4b", VisionModel = "llava:13b", UseVision = true };
            using (var brain = new AiBrain(new RecordingBackend("", true), settings))
            {
                string unresolved = brain.DescribeFailure(new TimeoutException(), null, false);
                ok &= Check(sb, "a failure before the model was resolved says so, and names the path taken rather than the setting",
                    unresolved.IndexOf("model=(unresolved)", StringComparison.Ordinal) >= 0 &&
                    unresolved.IndexOf("vision=False", StringComparison.Ordinal) >= 0 &&
                    unresolved.IndexOf("timeout-or-cancelled", StringComparison.Ordinal) >= 0);
                string substituted = brain.DescribeFailure(
                    new AiBackendHttpException(400, false), new ModelChoice("qwen3.6:27b", "swapped", "substituted"), true);
                ok &= Check(sb, "a failure after resolution names the model actually SENT (a substitute) and the vision path",
                    substituted.IndexOf("model=qwen3.6:27b", StringComparison.Ordinal) >= 0 &&
                    substituted.IndexOf("vision=True", StringComparison.Ordinal) >= 0 &&
                    substituted.IndexOf("http-400", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "WITNESS a local brain's failure line names the local host",
                    unresolved.IndexOf("endpoint=localhost", StringComparison.Ordinal) >= 0);
            }

            // The composite names both legs, so a cloud outage no longer logs "endpoint=localhost". Built by
            // the module's own factory from settings alone: construction touches no network.
            var cloud = new AiSettings
            {
                Provider = "openai",
                OpenAiBaseUrl = "https://api.openai.com/v1",
                CloudDataConsent = true,
                CloudTextModel = "gpt-4o-mini",
                UseLocalFallback = true,
            };
            using (AiBrain composite = AiBrainModule.CreateBrain(cloud))
            {
                ok &= Check(sb, "a cloud-primary brain with the local fallback describes its backend as cloudHost->localHost",
                    composite.BackendHostDescription == "api.openai.com->localhost");
            }
            cloud.UseLocalFallback = false;
            using (AiBrain cloudOnly = AiBrainModule.CreateBrain(cloud))
            {
                ok &= Check(sb, "WITNESS without the fallback it names the cloud host alone",
                    cloudOnly.BackendHostDescription == "api.openai.com");
            }

            // F072: the substitution is decided, and announced, before any capture.
            var reachable = new RecordingBackend("{\"text\":\"arr\",\"emotion\":\"happy\"}", true);
            var absentModel = new AiSettings { TextModel = "a-model-nobody-has:1b" };
            using (var brain = new AiBrain(reachable, absentModel))
            {
                brain.ModelLister = delegate(CancellationToken ct)
                {
                    return System.Threading.Tasks.Task.FromResult((IReadOnlyList<ModelListing>)new List<ModelListing>
                    {
                        new ModelListing("gemma3:4b", true),
                    });
                };
                brain.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModelChoice first;
                BrainResponse advisory;
                bool proceedFirst = brain.ResolveBeforeCapture(false, out first, out advisory);
                ok &= Check(sb, "a substitution is announced before any capture: the first ask ends on the advisory, naming both models",
                    !proceedFirst && first.Reason == "substituted" && advisory != null &&
                    advisory.Text.IndexOf("a-model-nobody-has:1b", StringComparison.Ordinal) >= 0 &&
                    advisory.Text.IndexOf("gemma3:4b", StringComparison.Ordinal) >= 0);
                ModelChoice second;
                bool proceedSecond = brain.ResolveBeforeCapture(false, out second, out advisory);
                ok &= Check(sb, "WITNESS the next ask goes on to the capture with the substitute, the advisory having been spoken once",
                    proceedSecond && advisory == null && second.Model == "gemma3:4b");
                ok &= Check(sb, "nothing was generated to be discarded: the backend was never asked",
                    reachable.ChatCalls == 0);
            }
            using (var blind = new AiBrain(new RecordingBackend("", true), new AiSettings { TextModel = "x:1b" }))
            {
                blind.ModelLister = delegate(CancellationToken ct)
                {
                    return System.Threading.Tasks.Task.FromResult((IReadOnlyList<ModelListing>)new List<ModelListing>
                    {
                        new ModelListing("dolphin3:latest", false),
                    });
                };
                blind.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModelChoice unusable;
                BrainResponse advisory;
                bool proceedBlind = blind.ResolveBeforeCapture(true, out unusable, out advisory);
                ok &= Check(sb, "WITNESS an unusable choice still ends the turn, with its advisory, before any capture",
                    !proceedBlind && !unusable.Usable && advisory != null);
                bool proceedBlindAgain = blind.ResolveBeforeCapture(true, out unusable, out advisory);
                ok &= Check(sb, "WITNESS ...and stays ended on the next ask, silently, rather than proceeding with no model",
                    !proceedBlindAgain && advisory == null);
            }
            return ok;
        }
    }
}
