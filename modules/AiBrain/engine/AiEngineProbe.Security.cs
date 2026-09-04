using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopAICompanion.Ai;

namespace DesktopAICompanion.AiBrainModule
{
    /// <summary>
    /// Relocated AI SECURITY assertions. These were ported ~verbatim from the base
    /// <c>SecuritySelfTest.cs</c> so they exercise the SHIPPING module engine (DesktopAICompanion.Ai.* — the
    /// module's own copies) instead of the base's about-to-be-deleted duplicate. Every reject/failure
    /// invariant still drives its reject/failure path; nothing was weakened. Runs headless: no live LLM,
    /// no network, and all file/DPAPI writes are isolated under throwaway temp roots (AiPaths.SetRoot).
    /// </summary>
    public static partial class AiEngineProbe
    {
        // Mirrors the base's Newtonsoft Formatting.Indented for the throwaway ai-settings.json the DPAPI-
        // failure assertions inject: WriteIndented + the relaxed encoder keep the base64 ciphertext literal.
        // IncludeFields serializes the settings' public fields exactly as the store persists them.
        private static readonly JsonSerializerOptions ProbeJson = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        internal static bool RunSecurity(StringBuilder sb)
        {
            bool ok = true;

            // --- endpoint policy rejects (loopback + cloud ACCEPTS are asserted in AiEngineProbe.Run) ---
            // The two IP-loopback ACCEPT call sites from the base (127.0.0.1 / [::1]) exercise the
            // IPAddress.IsLoopback path (distinct from localhost's Uri.IsLoopback), so they are ported too.
            ok &= CheckEndpoint(sb, "http://127.0.0.1:8080/v1", true);
            ok &= CheckEndpoint(sb, "http://[::1]:8080/v1", true);
            ok &= CheckEndpoint(sb, "http://example.com/v1", false);
            ok &= CheckEndpoint(sb, "http://192.168.1.20/v1", false);
            ok &= CheckEndpoint(sb, "ftp://localhost/model", false);
            ok &= CheckEndpoint(sb, "https://user:password@example.com/v1", false);
            ok &= CheckEndpoint(sb, "https://example.com/v1?token=secret", false);

            ok &= CheckAiSettingsPersistence(sb);
            ok &= CheckAiCredentialScoping(sb);
            ok &= CheckAiNormalization(sb);
            ok &= CheckAiSchemaMigration(sb);
            ok &= CheckDispositionMigration(sb);
            ok &= CheckLocalBackendKind(sb);
            ok &= CheckAiResponseBounds(sb);
            ok &= CheckAiResponseDeadline(sb);
            ok &= CheckOllamaStartupDeadline(sb);
            ok &= CheckAiHttpStatusPolicy(sb);
            ok &= CheckFallbackBackend(sb);
            ok &= CheckModelListing(sb);
            ok &= CheckKeepAliveAndResidency(sb);
            ok &= CheckAiRetirementBound(sb);
            ok &= CheckAiReconfigureDisposeRace(sb);
            ok &= CheckAiAfterRetireDurability(sb);

            return ok;
        }

        // The cloud->local FallbackBackend (BACKLOG #13): a retryable cloud failure fails over to the local
        // backend with the MAPPED local model; a deterministic cloud failure surfaces without falling over;
        // and availability is true if either leg is up. Uses the same retry classifier as AiBrain's retry.
        private static bool CheckFallbackBackend(StringBuilder sb)
        {
            bool ok = true;
            var msgs = new List<ChatMessage> { ChatMessage.User("x", null) };

            using (var primary = new TransientFailBackend())
            using (var local = new RecordingBackend("local-reply", true))
            using (var fb = new FallbackBackend(primary, local, "cloud-vision", "local-text", "local-vision"))
            {
                string reply = fb.ChatAsync("cloud-text", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "fallback: transient cloud failure fails over to the local text model",
                    reply == "local-reply" && primary.ChatCalls == 1 && local.ChatCalls == 1 && local.LastModel == "local-text");
            }

            using (var primary = new TransientFailBackend())
            using (var local = new RecordingBackend("local-reply", true))
            using (var fb = new FallbackBackend(primary, local, "cloud-vision", "local-text", "local-vision"))
            {
                fb.ChatAsync("cloud-vision", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "fallback: the cloud vision model maps to the local vision model on failover",
                    local.LastModel == "local-vision");
            }

            using (var primary = new DeterministicFailureBackend())
            using (var local = new RecordingBackend("local-reply", true))
            using (var fb = new FallbackBackend(primary, local, "cloud-vision", "local-text", "local-vision"))
            {
                bool threw = Throws<AiBackendHttpException>(delegate
                {
                    fb.ChatAsync("cloud-text", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                });
                ok &= Check(sb, "fallback: a deterministic cloud failure surfaces without failing over (local untouched)",
                    threw && local.ChatCalls == 0);
            }

            using (var primary = new TransientFailBackend())        // IsAvailable = false
            using (var local = new RecordingBackend("local-reply", true))   // IsAvailable = true
            using (var fb = new FallbackBackend(primary, local, "cloud-vision", "local-text", "local-vision"))
            {
                bool avail = fb.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
                ok &= Check(sb, "fallback: available when the local leg is up even if cloud is down", avail);
            }

            return ok;
        }

        // ListModelsAsync (model-picker dropdowns): offline via FixedJsonResponseHandler, proving (1)
        // Ollama's /api/tags real "capabilities" array is honored for both the has-vision and
        // explicitly-no-vision cases, (2) a response with no "capabilities" key (an older server) yields
        // Vision=null (unknown -> the caller's LooksVisionCapable heuristic applies, not a false claim),
        // (3) the "size" field (the VRAM/weight-footprint proxy shown in the model-picker label) parses as a
        // real byte count well past Int32 range, and (4) the generic OpenAI-compatible /models response (no
        // capability or size metadata at all) parses ids with Vision=null and SizeBytes=null for every entry.
        /// <summary>
        /// The VRAM settings: keep_alive on the chat request, and reading live residency from /api/ps.
        ///
        /// Asserted on the OUTGOING PAYLOAD rather than on the property, because the property being set proves
        /// nothing -- the bug worth catching is a value that never reaches the request. And the -1 case is
        /// asserted as an ABSENCE: sending keep_alive:-1 would pin the model in VRAM for ever, the exact
        /// opposite of "leave it to the server", so "no field at all" is the property that matters.
        /// </summary>
        private static bool CheckKeepAliveAndResidency(StringBuilder sb)
        {
            bool ok = true;
            var msgs = new List<ChatMessage> { ChatMessage.User("hello", null) };
            const string reply = "{\"message\":{\"content\":\"hi\"}}";

            try
            {
                // 0 = evict as soon as the answer is done.
                using (var h = new CapturingJsonHandler(reply))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    client.KeepAliveSeconds = 0;
                    client.ChatAsync("llama3", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "vram: keep_alive 0 is sent on the chat request",
                        h.LastBody.Replace(" ", "").Contains("\"keep_alive\":0"));
                }

                // A positive window reaches the request verbatim.
                using (var h = new CapturingJsonHandler(reply))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    client.KeepAliveSeconds = 30;
                    client.ChatAsync("llama3", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "vram: a positive keep_alive window is sent verbatim",
                        h.LastBody.Replace(" ", "").Contains("\"keep_alive\":30"));
                }

                // NULL omits the field. This is the "let the server decide" case, and it must be an ABSENCE:
                // an earlier version of this used -1 as the omit sentinel, which was a latent bug, because -1
                // is a real instruction to Ollama meaning "stay resident for ever" -- the exact opposite.
                using (var h = new CapturingJsonHandler(reply))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    client.KeepAliveSeconds = null;
                    client.ChatAsync("llama3", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "vram: null omits keep_alive entirely (server decides)",
                        !h.LastBody.Contains("keep_alive"));
                }

                // ...and a negative value IS sent, because that is how "keep loaded" is expressed.
                using (var h = new CapturingJsonHandler(reply))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    client.KeepAliveSeconds = -1;
                    client.ChatAsync("llama3", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "vram: a negative keep_alive is sent, which is how 'keep loaded' is asked for",
                        h.LastBody.Replace(" ", "").Contains("\"keep_alive\":-1"));
                }

                // The default must be the pre-existing behaviour, or upgrading silently changes performance.
                using (var h = new CapturingJsonHandler(reply))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    client.ChatAsync("llama3", msgs, false, CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "vram: an unset client sends no keep_alive",
                        !h.LastBody.Contains("keep_alive"));
                }

                // /api/ps: name, VRAM and eviction time, so the pane can state fact instead of a default.
                const string psJson =
                    "{\"models\":[{\"name\":\"qwen2.5:3b\",\"size_vram\":3221225472," +
                    "\"expires_at\":\"2099-01-01T00:00:00Z\"}]}";
                using (var h = new CapturingJsonHandler(psJson))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    IReadOnlyList<OllamaClient.RunningModel> running =
                        client.RunningModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    ok &= Check(sb, "vram: /api/ps is the endpoint asked", h.LastPath.EndsWith("/api/ps", StringComparison.Ordinal));
                    ok &= Check(sb, "vram: residency reports the model, its VRAM and its eviction time",
                        running.Count == 1 &&
                        running[0].Name == "qwen2.5:3b" &&
                        running[0].VramBytes == 3221225472L &&
                        running[0].ExpiresAt.HasValue);
                }

                // The stored DEFAULT, asserted separately from the client's. Flipping this to 0 would make
                // every existing user pay a cold reload per remark on upgrade -- a performance change nobody
                // asked for -- and the payload checks above cannot see it, because they set the property
                // directly. Mutation testing reported this silent.
                // The stored DEFAULT must be "unload": this module holds VRAM only for a remark it has
                // already made, so holding after the answer is the thing there is no reason for. Asserted
                // separately from the payload checks above, which set the client property directly and so
                // cannot see the settings default -- mutation testing reported exactly that gap.
                ok &= Check(sb, "vram: the stored default unloads after each remark",
                    new AiSettings().ModelResidency == AiSettings.ResidencyUnload);
                // Each choice maps to a DISTINCT wire value. -1 is a real instruction (stay resident), not an
                // absence, so "keep" and "server" must not collapse onto the same thing.
                ok &= Check(sb, "vram: unload -> 0, keep -> negative, server -> omitted",
                    new AiSettings { ModelResidency = AiSettings.ResidencyUnload }.KeepAliveForRequests == 0 &&
                    new AiSettings { ModelResidency = AiSettings.ResidencyKeep }.KeepAliveForRequests < 0 &&
                    new AiSettings { ModelResidency = AiSettings.ResidencyServer }.KeepAliveForRequests == null);
                // Only "keep" wants a launch warm-up: warming a model and then evicting it after the first
                // remark is work done to be thrown away.
                ok &= Check(sb, "vram: only 'keep' asks for a launch warm-up",
                    new AiSettings { ModelResidency = AiSettings.ResidencyKeep }.WarmUpDesired &&
                    !new AiSettings { ModelResidency = AiSettings.ResidencyUnload }.WarmUpDesired &&
                    !new AiSettings { ModelResidency = AiSettings.ResidencyServer }.WarmUpDesired);
                // An unrecognised stored value must fall back to the safe choice, not hold VRAM for ever.
                ok &= Check(sb, "vram: an unknown residency value falls back to unloading",
                    new AiSettings { ModelResidency = "nonsense" }.KeepAliveForRequests == 0 &&
                    !new AiSettings { ModelResidency = "nonsense" }.WarmUpDesired);

                // Stand-down-for-a-game is ON by default. The cost of being wrong is a free fortune instead
                // of a quip; the cost the other way is a game losing VRAM it already owns. Defaults matter
                // more than the setting here, because the people at risk are the ones who never open the pane.
                ok &= Check(sb, "vram: standing down for a fullscreen app is on by default",
                    new AiSettings().StandDownForFullscreen);

                // The pane label <-> stored token round-trip. Storing a LABEL where a token belongs would
                // leave the dropdown showing one choice while the setting behaved as another, and it degrades
                // quietly rather than throwing -- mutation testing reported this unguarded.
                foreach (string token in new[]
                    { AiSettings.ResidencyUnload, AiSettings.ResidencyKeep, AiSettings.ResidencyServer })
                {
                    string label = DesktopAICompanion.AiBrainModule.AiBrainModule.ResidencyLabel(token);
                    ok &= Check(sb, "vram: residency '" + token + "' survives the label round-trip",
                        DesktopAICompanion.AiBrainModule.AiBrainModule.ResidencyFromLabel(label) == token);
                }
                ok &= Check(sb, "vram: an unrecognised label falls back to the default token",
                    DesktopAICompanion.AiBrainModule.AiBrainModule.ResidencyFromLabel("who knows") == AiSettings.ResidencyUnload);
                ok &= Check(sb, "vram: every stored token has a distinct label offered by the pane",
                    DesktopAICompanion.AiBrainModule.AiBrainModule.ResidencyLabels().Length == 3);

                // ...and that the stored value actually REACHES the client. Breaking this propagation was
                // silent too: the setting saved, the payload logic was right, and nothing joined them.
                var wired = new AiSettings { ModelResidency = AiSettings.ResidencyKeep, LocalBackendKind = "ollama" };
                using (ICompanionBrainBackend backend = DesktopAICompanion.AiBrainModule.AiBrainModule.BuildLocalBackend(
                        wired, "http://localhost:11434", TimeSpan.FromSeconds(5)))
                {
                    var asOllama = backend as OllamaClient;
                    ok &= Check(sb, "vram: the stored setting reaches the Ollama client",
                        asOllama != null && asOllama.KeepAliveSeconds.HasValue && asOllama.KeepAliveSeconds.Value < 0);
                }

                // An empty or older server is a legitimate "nothing resident", never an exception.
                using (var h = new CapturingJsonHandler("{}"))
                using (var client = new OllamaClient("http://localhost:11434", TimeSpan.FromSeconds(5), "",
                        h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
                        delegate(CancellationToken ignored) { return true; }))
                {
                    ok &= Check(sb, "vram: a server with no models block answers 'nothing resident'",
                        client.RunningModelsAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0);
                }
            }
            catch (Exception ex)
            {
                ok &= Check(sb, "vram: keep_alive/residency probe threw (" + ex.GetType().Name + ")", false);
            }
            return ok;
        }

        private static bool CheckModelListing(StringBuilder sb)
        {
            bool ok = true;
            try
            {
                // > int.MaxValue -> proves the Int64 parse, not Int32. Kept in sync with the literal in
                // tagsJson below by hand (a single reuse, not worth a runtime string-format indirection).
                const long llavaSizeBytes = 4683075271;
                const string tagsJson =
                    "{\"models\":[" +
                    "{\"name\":\"llava:13b\",\"capabilities\":[\"completion\",\"vision\"],\"size\":4683075271}," +
                    "{\"name\":\"qwen2.5:7b\",\"capabilities\":[\"completion\"]}," +
                    "{\"name\":\"llama3.1:8b\"}" +
                    "]}";
                using (var handler = new FixedJsonResponseHandler(tagsJson))
                using (var client = new OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(30),
                    "",
                    handler,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored) { return true; }))
                {
                    IReadOnlyList<ModelListing> models =
                        client.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    ModelListing llava = FindModel(models, "llava:13b");
                    ModelListing qwen = FindModel(models, "qwen2.5:7b");
                    ModelListing llama = FindModel(models, "llama3.1:8b");
                    ok &= Check(
                        sb,
                        "Ollama model list: real capabilities honored (vision true/false), absent capabilities -> unknown",
                        models.Count == 3 &&
                        llava != null && llava.Vision == true &&
                        qwen != null && qwen.Vision == false &&
                        llama != null && llama.Vision == null);
                    ok &= Check(
                        sb,
                        "Ollama model list: real \"size\" (Int64) parsed for VRAM display, absent size -> unknown",
                        llava != null && llava.SizeBytes == llavaSizeBytes &&
                        qwen != null && qwen.SizeBytes == null &&
                        llama != null && llama.SizeBytes == null);
                }

                const string modelsJson =
                    "{\"data\":[{\"id\":\"gpt-4o-mini\"},{\"id\":\"dolphin-mixtral:8x7b\"}]}";
                using (var handler = new FixedJsonResponseHandler(modelsJson))
                using (var client = new OpenAiCompatBackend(
                    "https://api.openai.com/v1", "", TimeSpan.FromSeconds(30), handler))
                {
                    IReadOnlyList<ModelListing> models =
                        client.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    ModelListing gpt = FindModel(models, "gpt-4o-mini");
                    ModelListing dolphin = FindModel(models, "dolphin-mixtral:8x7b");
                    ok &= Check(
                        sb,
                        "generic OpenAI-compatible model list: ids parsed, no capability or size metadata (Vision/SizeBytes unknown)",
                        models.Count == 2 &&
                        gpt != null && gpt.Vision == null && gpt.SizeBytes == null &&
                        dolphin != null && dolphin.Vision == null && dolphin.SizeBytes == null);
                }
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "model listing self-test threw " + ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            return ok;
        }

        private static ModelListing FindModel(IReadOnlyList<ModelListing> models, string id)
        {
            if (models == null) return null;
            foreach (ModelListing m in models)
                if (m != null && string.Equals(m.Id, id, StringComparison.Ordinal)) return m;
            return null;
        }

        private static bool CheckEndpoint(StringBuilder sb, string value, bool expected)
        {
            string normalized;
            string error;
            bool actual = AiEndpointPolicy.TryNormalize(value, out normalized, out error);
            return Check(sb, "endpoint policy: " + value, actual == expected);
        }

        private static bool CheckAiSettingsPersistence(StringBuilder sb)
        {
            bool ok = true;
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-ai-settings-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                // The module resolves ai-settings.json / chat-history.json under AiPaths.Root; point it at
                // this throwaway directory (the base used the DESKTOP_AI_COMPANION_DATA_ROOT override instead).
                AiPaths.SetRoot(directory);
                string path = AiSettings.FilePath;
                File.WriteAllText(
                    path,
                    "{\n" +
                    "  \"SchemaVersion\": 1,\n" +
                    "  \"TimeoutSeconds\": 120,\n" +
                    "  \"UseVision\": false,\n" +
                    "  \"futureSameSchema\": { \"keep\": true }\n" +
                    "}",
                    new UTF8Encoding(false));

                AiSettings first = AiSettings.Load();
                AiSettings second = AiSettings.Load();
                first.TimeoutSeconds = 77;
                bool firstSaved = first.Save();
                second.UseVision = true;
                bool secondSaved = second.Save();
                JsonObject merged = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings stale writers merge and preserve unknown fields",
                    firstSaved &&
                    secondSaved &&
                    (int)merged["TimeoutSeconds"] == 77 &&
                    (bool)merged["UseVision"] &&
                    (bool)merged["futureSameSchema"]["keep"]);

                AiSettings keyWriterA = AiSettings.Load();
                AiSettings keyWriterB = AiSettings.Load();
                keyWriterA.Provider = "openai";
                keyWriterA.OpenAiBaseUrl = "https://api.openai.com/v1";
                keyWriterA.ApiKey = "stale-writer-openai-key";
                keyWriterB.Provider = "openrouter";
                keyWriterB.OpenAiBaseUrl = "https://openrouter.ai/api/v1";
                keyWriterB.ApiKey = "stale-writer-router-key";
                bool keyWriterASaved = keyWriterA.Save();
                bool keyWriterBSaved = keyWriterB.Save();
                AiSettings mergedKeys = AiSettings.Load();
                bool routerKeyPreserved =
                    mergedKeys.ApiKey == "stale-writer-router-key";
                mergedKeys.Provider = "openai";
                mergedKeys.OpenAiBaseUrl = "https://api.openai.com/v1";
                ok &= Check(
                    sb,
                    "AI settings stale writers merge provider-scoped keys",
                    keyWriterASaved &&
                    keyWriterBSaved &&
                    routerKeyPreserved &&
                    mergedKeys.ApiKey == "stale-writer-openai-key");

                const string customEndpoint =
                    "https://gateway.example/TenantA/v1";
                const string customKey =
                    "custom-endpoint-key-do-not-persist";
                AiSettings customSettings = AiSettings.Load();
                customSettings.SelectProviderEndpoint("custom", true);
                customSettings.UpdateSelectedProviderEndpoint(customEndpoint);
                customSettings.ApiKey = customKey;
                string openAiEndpoint =
                    customSettings.SelectProviderEndpoint("openai", true);
                string restoredCustomEndpoint =
                    customSettings.SelectProviderEndpoint("custom", true);
                bool customSaved = customSettings.Save();
                AiSettings customReloaded = AiSettings.Load();
                ok &= Check(
                    sb,
                    "Custom provider endpoint and scoped key survive switching and reload",
                    string.Equals(
                        openAiEndpoint,
                        "https://api.openai.com/v1",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        restoredCustomEndpoint,
                        customEndpoint,
                        StringComparison.Ordinal) &&
                    customSettings.ApiKey == customKey &&
                    customSaved &&
                    string.Equals(
                        customReloaded.Provider,
                        "custom",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        customReloaded.OpenAiBaseUrl,
                        customEndpoint,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        customReloaded.CustomOpenAiBaseUrl,
                        customEndpoint,
                        StringComparison.Ordinal) &&
                    customReloaded.ApiKey == customKey);

                Stopwatch lockWait = Stopwatch.StartNew();
                bool boundedSaveRejected;
                using (var contention = new FileStream(
                    path + ".lock",
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    customReloaded.Disposition = "bounded save contention";
                    boundedSaveRejected = !customReloaded.SaveWithin(125);
                }
                lockWait.Stop();
                ok &= Check(
                    sb,
                    "UI-budgeted AI settings save rejects a held lock promptly",
                    boundedSaveRejected &&
                    lockWait.Elapsed < TimeSpan.FromSeconds(2));

                string undecryptable =
                    Convert.ToBase64String(new byte[] { 1, 3, 3, 7, 9, 11, 13, 17 });
                string openAiScope = AiSettings.BuildCredentialScope(
                    "openai",
                    "https://api.openai.com/v1");
                byte[] normalizedPrimary = File.ReadAllBytes(path);
                byte[] normalizedBackup = File.ReadAllBytes(path + ".bak");

                JsonObject scopedFailure = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                scopedFailure["Provider"] = "openai";
                scopedFailure["OpenAiBaseUrl"] = "https://api.openai.com/v1";
                scopedFailure["ApiKeyEnc"] = "";
                scopedFailure["ApiKeysEnc"] = new JsonObject {
                    [openAiScope] = undecryptable
                };
                File.WriteAllText(
                    path,
                    scopedFailure.ToJsonString(ProbeJson),
                    new UTF8Encoding(false));
                byte[] scopedBeforeLoad = File.ReadAllBytes(path);
                AiSettings scopedLoaded = AiSettings.Load();
                JsonObject scopedAfterLoad = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings preserve provider-scoped ciphertext on DPAPI failure",
                    string.IsNullOrEmpty(scopedLoaded.ApiKey) &&
                    string.Equals(
                        (string)scopedAfterLoad["ApiKeysEnc"][openAiScope],
                        undecryptable,
                        StringComparison.Ordinal) &&
                    ByteArraysEqual(scopedBeforeLoad, File.ReadAllBytes(path)) &&
                    ByteArraysEqual(normalizedBackup, File.ReadAllBytes(path + ".bak")));

                JsonObject legacyFailure = JsonNode.Parse(
                    Encoding.UTF8.GetString(normalizedPrimary)).AsObject();
                legacyFailure["Provider"] = "openai";
                legacyFailure["OpenAiBaseUrl"] = "https://api.openai.com/v1";
                legacyFailure["ApiKeysEnc"] = new JsonObject();
                legacyFailure["ApiKeyEnc"] = undecryptable;
                File.WriteAllText(
                    path,
                    legacyFailure.ToJsonString(ProbeJson),
                    new UTF8Encoding(false));
                byte[] legacyBeforeLoad = File.ReadAllBytes(path);
                AiSettings legacyLoaded = AiSettings.Load();
                JsonObject legacyAfterLoad = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings preserve legacy ciphertext on DPAPI failure",
                    string.IsNullOrEmpty(legacyLoaded.ApiKey) &&
                    string.Equals(
                        (string)legacyAfterLoad["ApiKeyEnc"],
                        undecryptable,
                        StringComparison.Ordinal) &&
                    ByteArraysEqual(legacyBeforeLoad, File.ReadAllBytes(path)) &&
                    ByteArraysEqual(normalizedBackup, File.ReadAllBytes(path + ".bak")));
                File.WriteAllBytes(path, normalizedPrimary);

                string backupPath = path + ".bak";
                byte[] validBackup = File.ReadAllBytes(backupPath);
                JsonObject expectedBackup = JsonNode.Parse(
                    File.ReadAllText(backupPath, Encoding.UTF8)).AsObject();
                File.WriteAllText(
                    path,
                    "{ corrupt primary",
                    new UTF8Encoding(false));
                AiSettings recovered = AiSettings.Load();
                JsonObject repairedPrimary = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings corrupt-primary recovery preserves the valid backup",
                    recovered.TimeoutSeconds == (int)expectedBackup["TimeoutSeconds"] &&
                    (int)repairedPrimary["TimeoutSeconds"] ==
                        (int)expectedBackup["TimeoutSeconds"] &&
                    ByteArraysEqual(validBackup, File.ReadAllBytes(backupPath)));

                string future =
                    "{\n  \"SchemaVersion\": 99,\n" +
                    "  \"TimeoutSeconds\": 42,\n" +
                    "  \"futureOnly\": true\n}";
                File.WriteAllText(path, future, new UTF8Encoding(false));
                byte[] before = File.ReadAllBytes(path);
                AiSettings futureSettings = AiSettings.Load();
                futureSettings.TimeoutSeconds = 50;
                bool blocked = !futureSettings.Save();
                byte[] after = File.ReadAllBytes(path);
                ok &= Check(
                    sb,
                    "AI settings future schema remains byte-for-byte untouched",
                    blocked && ByteArraysEqual(before, after));
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI settings persistence self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    ok &= Check(sb, "AI settings persistence self-test cleanup", false);
                }
            }
            return ok;
        }

        private static bool CheckAiCredentialScoping(StringBuilder sb)
        {
            bool ok = true;
            const string openAiKey = "selftest-openai-key-do-not-persist";
            const string routerKey = "selftest-router-key-do-not-persist";
            const string customKey = "selftest-custom-key-do-not-persist";
            try
            {
                var settings = new AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                settings.ApiKey = openAiKey;
                bool openAiStored = settings.ApiKey == openAiKey;

                settings.Provider = "openrouter";
                settings.OpenAiBaseUrl = "https://openrouter.ai/api/v1";
                bool providerIsolated = string.IsNullOrEmpty(settings.ApiKey);
                settings.ApiKey = routerKey;

                settings.SelectProviderEndpoint("custom", true);
                settings.UpdateSelectedProviderEndpoint(
                    "https://gateway.example/TenantA/v1");
                bool customEndpointIsolated = string.IsNullOrEmpty(settings.ApiKey);
                settings.ApiKey = customKey;

                settings.SelectProviderEndpoint("openai", true);
                bool openAiRestored = settings.ApiKey == openAiKey;
                settings.SelectProviderEndpoint("custom", true);
                bool customRestored =
                    settings.OpenAiBaseUrl ==
                        "https://gateway.example/TenantA/v1" &&
                    settings.ApiKey == customKey;
                settings.SelectProviderEndpoint("openrouter", true);
                bool routerRestored = settings.ApiKey == routerKey;
                ok &= Check(
                    sb,
                    "API keys are isolated by provider and endpoint",
                    openAiStored &&
                    providerIsolated &&
                    customEndpointIsolated &&
                    openAiRestored &&
                    customRestored &&
                    routerRestored);

                var credentialA = new AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                credentialA.ApiKey = openAiKey;

                string serialized =
                    JsonSerializer.Serialize(credentialA, ProbeJson);
                string scope = AiSettings.BuildCredentialScope(
                    credentialA.Provider,
                    credentialA.OpenAiBaseUrl);
                ok &= Check(
                    sb,
                    "credential scope and persistence omit plaintext keys",
                    serialized.IndexOf(openAiKey, StringComparison.Ordinal) < 0 &&
                    scope.IndexOf(openAiKey, StringComparison.Ordinal) < 0);

                var boundedCredentials = new AiSettings
                {
                    Provider = "custom"
                };
                bool admittedAllScopes = true;
                string admissionError = "";
                for (int index = 0;
                    index < AiSettings.MaximumApiKeyScopes;
                    index++)
                {
                    boundedCredentials.OpenAiBaseUrl =
                        "https://credentials.example/scope/" +
                        index.ToString(CultureInfo.InvariantCulture);
                    admittedAllScopes &=
                        boundedCredentials.TrySetApiKey(
                            "bounded-key-" +
                            index.ToString(CultureInfo.InvariantCulture),
                            out admissionError);
                }
                boundedCredentials.OpenAiBaseUrl =
                    "https://credentials.example/scope/overflow";
                bool overflowRejected =
                    !boundedCredentials.TrySetApiKey(
                        "must-not-be-silently-discarded",
                        out admissionError);
                boundedCredentials.OpenAiBaseUrl =
                    "https://credentials.example/scope/0";
                string updateError;
                bool existingScopeUpdated =
                    boundedCredentials.TrySetApiKey(
                        "updated-existing-key",
                        out updateError) &&
                    boundedCredentials.ApiKey == "updated-existing-key";
                ok &= Check(
                    sb,
                    "API key scope limit rejects new keys explicitly and permits updates",
                    admittedAllScopes &&
                    overflowRejected &&
                    boundedCredentials.ApiKeysEnc.Count ==
                        AiSettings.MaximumApiKeyScopes &&
                    !string.IsNullOrWhiteSpace(admissionError) &&
                    existingScopeUpdated);

            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI credential scoping self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            return ok;
        }

        private static bool CheckAiNormalization(StringBuilder sb)
        {
            bool ok = true;
            var settings = new AiSettings
            {
                SchemaVersion = 0,
                Endpoint = " " + new string('e', 3000) + "\0 ",
                TextModel = new string('m', 400),
                VisionModel = new string('v', 400),
                TesseractPath = new string('t', 2000),
                PetName = new string('p', 200),
                UserName = new string('u', 200),
                Disposition = "NOT-A-DISPOSITION",
                Provider = "NOT-A-PROVIDER",
                LocalBackendKind = "NOT-A-KIND",
                TimeoutSeconds = int.MaxValue,
                // The idle-commentary trio used to be clamped here. It is gone: unprompted commentary now
                // rides the host's global drop schedule, so the module owns no interval of its own. The
                // remaining int clamps stand in, so this still exercises the Clamp path on more than one field.
                RandomDropMinutes = int.MaxValue,
                RandomDropJitterMinutes = -1,
                DisabledSources = new List<string>()
            };
            for (int i = 0; i < 300; i++)
                settings.DisabledSources.Add(" source-" + i + " ");
            settings.Normalize();

            ok &= Check(sb, "AI settings schema normalized",
                settings.SchemaVersion == AiSettings.CurrentSchemaVersion);
            ok &= Check(sb, "AI settings strings bounded",
                settings.Endpoint.Length <= 2048 &&
                settings.TextModel.Length <= 256 &&
                settings.VisionModel.Length <= 256 &&
                settings.TesseractPath.Length <= 1024 &&
                settings.PetName.Length <= 80 &&
                settings.UserName.Length <= 80);
            ok &= Check(sb, "AI settings values clamped",
                settings.Provider == "" &&
                settings.LocalBackendKind == "ollama" &&
                settings.Disposition == Dispositions.DefaultId &&
                settings.TimeoutSeconds == 600 &&
                settings.RandomDropMinutes == 9999 &&
                settings.RandomDropJitterMinutes == 0);
            ok &= Check(sb, "AI disabled-source list bounded",
                settings.DisabledSources.Count == 128);

            string normalizedModel;
            ok &= Check(
                sb,
                "AI model identifiers bounded and sanitized",
                AiModelPolicy.TryNormalize(" owner/model:tag ", out normalizedModel) &&
                normalizedModel == "owner/model:tag" &&
                !AiModelPolicy.TryNormalize("model\r\ninjected", out normalizedModel));
            ok &= Check(
                sb,
                "relative configured AI executable rejected",
                AiExecutablePolicy.ResolveConfigured(
                    "ollama.exe",
                    "ollama.exe") == null);
            ok &= CheckAiExecutablePathPolicy(sb);

            settings.Provider = "openai";
            settings.OpenAiBaseUrl = "https://api.openai.com/v1";
            string apiKeyError;
            bool oversizedApiKeyRejected =
                !settings.TrySetApiKey(
                    new string('k', 9000),
                    out apiKeyError);
            ok &= Check(
                sb,
                "oversized API key rejected",
                oversizedApiKeyRejected &&
                !string.IsNullOrWhiteSpace(apiKeyError) &&
                string.IsNullOrEmpty(settings.ApiKeyEnc) &&
                settings.ApiKeysEnc.Count == 0);
            return ok;
        }

        // Schema v2 migration + new cloud-slot fields. Proves that (1) a v1 doc that was on a CLOUD provider
        // migrates its old TextModel/VisionModel into the cloud slot, resets the local slot to its defaults,
        // advances SchemaVersion, and KEEPS the scoped credential resolvable (the scope hash is unchanged
        // because Provider+OpenAiBaseUrl are preserved); and (2) the new cloud-slot fields round-trip through
        // save/reload. Isolated under a throwaway AiPaths root so real settings are never touched.
        private static bool CheckAiSchemaMigration(StringBuilder sb)
        {
            bool ok = true;
            const string migrateKey = "selftest-migrate-scoped-key-do-not-persist";
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-ai-migration-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                AiPaths.SetRoot(directory);
                string path = AiSettings.FilePath;

                // Seed the ENCRYPTED cloud key at the openai scope, then hand-craft a v1 doc around it whose
                // legacy TextModel/VisionModel are the CLOUD models (as an old cloud user's doc would be).
                var seed = new AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "gpt-4o-mini",
                    VisionModel = "gpt-4o"
                };
                string seedError;
                bool keySeeded = seed.TrySetApiKey(migrateKey, out seedError);
                JsonObject v1 = JsonNode.Parse(
                    JsonSerializer.Serialize(seed, ProbeJson)).AsObject();
                v1["SchemaVersion"] = 1;
                v1["TextModel"] = "gpt-4o-mini";
                v1["VisionModel"] = "gpt-4o";
                v1.Remove("CloudTextModel");   // a genuine v1 doc predates the cloud-slot fields
                v1.Remove("CloudVisionModel");
                v1.Remove("UseLocalFallback");
                File.WriteAllText(
                    path,
                    v1.ToJsonString(ProbeJson),
                    new UTF8Encoding(false));

                AiSettings migrated = AiSettings.Load();
                ok &= Check(
                    sb,
                    "v1 cloud doc migrates models into the cloud slot and keeps its scoped key",
                    keySeeded &&
                    string.Equals(migrated.Provider, "openai", StringComparison.Ordinal) &&
                    string.Equals(migrated.OpenAiBaseUrl, "https://api.openai.com/v1", StringComparison.Ordinal) &&
                    string.Equals(migrated.CloudTextModel, "gpt-4o-mini", StringComparison.Ordinal) &&
                    string.Equals(migrated.CloudVisionModel, "gpt-4o", StringComparison.Ordinal) &&
                    string.Equals(migrated.TextModel, "llama3.1:8b", StringComparison.Ordinal) &&
                    string.Equals(migrated.VisionModel, "gemma3:4b", StringComparison.Ordinal) &&
                    migrated.SchemaVersion == AiSettings.CurrentSchemaVersion &&
                    string.Equals(migrated.ApiKey, migrateKey, StringComparison.Ordinal));

                // New cloud-slot fields round-trip through save + reload (UseLocalFallback flipped off its
                // default so persistence, not the default, is what is being observed).
                AiSettings writer = AiSettings.Load();
                writer.CloudTextModel = "cloud-text-model";
                writer.CloudVisionModel = "cloud-vision-model";
                writer.UseLocalFallback = false;
                bool cloudSaved = writer.Save();
                AiSettings cloudReloaded = AiSettings.Load();
                ok &= Check(
                    sb,
                    "new cloud-slot fields (CloudTextModel/CloudVisionModel/UseLocalFallback) round-trip",
                    cloudSaved &&
                    string.Equals(cloudReloaded.CloudTextModel, "cloud-text-model", StringComparison.Ordinal) &&
                    string.Equals(cloudReloaded.CloudVisionModel, "cloud-vision-model", StringComparison.Ordinal) &&
                    !cloudReloaded.UseLocalFallback);
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI schema migration self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    ok &= Check(sb, "AI schema migration self-test cleanup", false);
                }
            }
            return ok;
        }

        private static bool CheckDispositionMigration(StringBuilder sb)
        {
            bool ok = true;
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-ai-disposition-migration-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                AiPaths.SetRoot(directory);
                string path = AiSettings.FilePath;

                // A v2 doc whose legacy SpeechPattern this schema's curated list absorbed under the SAME id
                // (see MigrateDispositionFromV2) carries straight over onto that disposition.
                JsonObject carried = JsonNode.Parse(
                    JsonSerializer.Serialize(new AiSettings(), ProbeJson)).AsObject();
                carried["SchemaVersion"] = 2;
                carried["SpeechPattern"] = "samuel";
                carried["Personality"] = "intense, blunt and effortlessly cool";
                carried.Remove("Disposition");   // a genuine v2 doc predates this field
                File.WriteAllText(path, carried.ToJsonString(ProbeJson), new UTF8Encoding(false));
                AiSettings migratedCarried = AiSettings.Load();
                ok &= Check(
                    sb,
                    "v2 doc with a carried-over SpeechPattern id migrates onto that disposition",
                    string.Equals(migratedCarried.Disposition, "samuel", StringComparison.Ordinal) &&
                    migratedCarried.SchemaVersion == AiSettings.CurrentSchemaVersion &&
                    !migratedCarried.ExtensionData.ContainsKey("SpeechPattern") &&
                    !migratedCarried.ExtensionData.ContainsKey("Personality"));

                // A v2 doc whose legacy SpeechPattern this schema's curated list did NOT absorb (retired,
                // e.g. "uwu") falls back to the default disposition instead of carrying over a dead id.
                JsonObject retired = JsonNode.Parse(
                    JsonSerializer.Serialize(new AiSettings(), ProbeJson)).AsObject();
                retired["SchemaVersion"] = 2;
                retired["SpeechPattern"] = "uwu";
                retired.Remove("Disposition");
                File.WriteAllText(path, retired.ToJsonString(ProbeJson), new UTF8Encoding(false));
                AiSettings migratedRetired = AiSettings.Load();
                ok &= Check(
                    sb,
                    "v2 doc with a retired SpeechPattern id falls back to the default disposition",
                    string.Equals(migratedRetired.Disposition, Dispositions.DefaultId, StringComparison.Ordinal) &&
                    migratedRetired.SchemaVersion == AiSettings.CurrentSchemaVersion);
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "Disposition migration self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    ok &= Check(sb, "Disposition migration self-test cleanup", false);
                }
            }
            return ok;
        }

        // LocalBackendKind (the local-slot regression fix): a new optional field, no schema bump needed since
        // an absent JSON key keeps the C# field initializer's default ("ollama") after deserialization. Proves
        // (1) an old doc written before this field existed still defaults to "ollama" (the existing local-only
        // behavior is unchanged for every current user), and (2) the new value round-trips through save/reload.
        private static bool CheckLocalBackendKind(StringBuilder sb)
        {
            bool ok = true;
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-ai-localbackend-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                AiPaths.SetRoot(directory);
                string path = AiSettings.FilePath;

                // A doc with no "LocalBackendKind" key at all (as every doc written before this field existed
                // would be) must still resolve to the Ollama-native default.
                File.WriteAllText(
                    path,
                    "{ \"SchemaVersion\": " + AiSettings.CurrentSchemaVersion + " }",
                    new UTF8Encoding(false));
                AiSettings absent = AiSettings.Load();
                ok &= Check(
                    sb,
                    "a doc with no LocalBackendKind key defaults to Ollama-native",
                    string.Equals(absent.LocalBackendKind, "ollama", StringComparison.Ordinal));

                AiSettings writer = AiSettings.Load();
                writer.LocalBackendKind = "openai-compat";
                bool saved = writer.Save();
                AiSettings reloaded = AiSettings.Load();
                ok &= Check(
                    sb,
                    "LocalBackendKind (openai-compat) round-trips through save and reload",
                    saved && string.Equals(reloaded.LocalBackendKind, "openai-compat", StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "LocalBackendKind self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    ok &= Check(sb, "LocalBackendKind self-test cleanup", false);
                }
            }
            return ok;
        }

        private static bool CheckAiExecutablePathPolicy(StringBuilder sb)
        {
            bool ok = true;
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-ExecutablePolicy-" +
                    Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                string executable = Path.Combine(directory, "ollama.exe");
                File.WriteAllBytes(executable, new byte[] { 0 });
                string canonical = Path.GetFullPath(executable);
                string pathWithRemotePrefix =
                    @"\\server.invalid\share" +
                    Path.PathSeparator +
                    directory;

                ok &= Check(
                    sb,
                    "local absolute AI executable paths remain supported",
                    string.Equals(
                        AiExecutablePolicy.ResolveConfigured(
                            executable,
                            "ollama.exe"),
                        canonical,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        AiExecutablePolicy.ResolveFromPath(
                            pathWithRemotePrefix,
                            "ollama.exe"),
                        canonical,
                        StringComparison.OrdinalIgnoreCase) &&
                    AiExecutablePolicy.IsReparseFreeLocalFile(
                        executable));

                ok &= Check(
                    sb,
                    "UNC and device AI executable paths rejected before probing",
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\server.invalid\share\ollama.exe") &&
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\?\C:\Apps\Ollama\ollama.exe") &&
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\.\C:\Apps\Ollama\ollama.exe") &&
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\??\C:\Apps\Ollama\ollama.exe") &&
                    AiExecutablePolicy.ResolveConfigured(
                        @"\\server.invalid\share\ollama.exe",
                        "ollama.exe") == null);
                ok &= Check(
                    sb,
                    "AI executable reparse point rejected before descendant probing",
                    AiExecutablePolicy
                        .ReparseScanStopsBeforeTraversalForDiagnostics());
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI executable path policy regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    ok &= Check(sb, "AI executable path policy cleanup", false);
                }
            }
            return ok;
        }

        private static bool CheckAiResponseBounds(StringBuilder sb)
        {
            bool ok = true;
            string sanitized = AiBrain.SanitizeResponseText(
                " hello\r\n" + new string('x', 700) + "\0tail ");
            ok &= Check(sb, "assistant response text bounded and sanitized",
                sanitized.Length <= 512 &&
                sanitized.IndexOf('\r') < 0 &&
                sanitized.IndexOf('\n') < 0 &&
                sanitized.IndexOf('\0') < 0);

            string astral = char.ConvertFromUtf32(0x1F642);
            string boundary = AiBrain.SanitizeResponseText(
                new string('x', 511) + astral + "tail");
            string exact = AiBrain.SanitizeResponseText(
                new string('x', 510) + astral + "tail");
            ok &= Check(
                sb,
                "assistant response truncation preserves surrogate pairs",
                boundary.Length == 511 &&
                exact.Length == 512 &&
                IsWellFormedUtf16(boundary) &&
                IsWellFormedUtf16(exact));

            byte[] oversized = Encoding.UTF8.GetBytes(new string('a', 2048));
            using (var content = new ByteArrayContent(oversized))
            {
                content.Headers.ContentLength = 1;
                ok &= Check(sb, "chunked/misleading AI response rejected",
                    Throws<InvalidDataException>(delegate
                    {
                        AiEndpointPolicy.ReadResponseStringAsync(
                            content,
                            CancellationToken.None,
                            1024).GetAwaiter().GetResult();
                    }));
            }

            using (var invalidUtf8 = new ByteArrayContent(new byte[] { 0xC3, 0x28 }))
            {
                invalidUtf8.Headers.ContentLength = null;
                ok &= Check(sb, "invalid UTF-8 AI response rejected",
                    Throws<DecoderFallbackException>(delegate
                    {
                        AiEndpointPolicy.ReadResponseStringAsync(
                            invalidUtf8,
                            CancellationToken.None,
                            1024).GetAwaiter().GetResult();
                    }));
            }
            return ok;
        }

        private static bool CheckAiResponseDeadline(StringBuilder sb)
        {
            bool ok = true;
            bool bodyTimedOut = false;
            var bodyHandler = new BlockingBodyHandler();
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (bodyHandler)
                using (var client = new HttpClient(bodyHandler, false)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                })
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/ai-body"))
                {
                    AiEndpointPolicy.SendAndReadResponseStringAsync(
                        client,
                        request,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None,
                        1024).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                bodyTimedOut = true;
            }
            stopwatch.Stop();
            ok &= Check(
                sb,
                "AI deadline bounds cancellation-ignoring response reads and disposes them",
                bodyTimedOut &&
                bodyHandler.StreamDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5));

            bool streamAcquisitionTimedOut = false;
            var streamHandler = new BlockingReadAsStreamHandler();
            stopwatch.Restart();
            try
            {
                using (streamHandler)
                using (var client = new HttpClient(streamHandler, false)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                })
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/ai-stream"))
                {
                    AiEndpointPolicy.SendAndReadResponseStringAsync(
                        client,
                        request,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None,
                        1024).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                streamAcquisitionTimedOut = true;
            }
            stopwatch.Stop();
            ok &= Check(
                sb,
                "AI deadline bounds cancellation-ignoring response stream acquisition",
                streamAcquisitionTimedOut &&
                streamHandler.ContentDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            return ok;
        }

        private static bool CheckOllamaStartupDeadline(StringBuilder sb)
        {
            bool ok = true;
            var boundedHandler = new FirstUnavailableThenBlockingHandler();
            bool boundedResult = true;
            bool boundedCompleted = false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (var client = new OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    boundedHandler,
                    TimeSpan.FromMilliseconds(300),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored) { return true; }))
                {
                    boundedResult = client.EnsureServerAsync(
                        CancellationToken.None).GetAwaiter().GetResult();
                    boundedCompleted = true;
                }
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "Ollama startup deadline regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            stopwatch.Stop();
            ok &= Check(
                sb,
                "Ollama startup uses short probes within one overall deadline",
                boundedCompleted &&
                !boundedResult &&
                boundedHandler.RequestCount >= 2 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));

            var starterEnteredEvent = new ManualResetEventSlim(false);
            var starterReleaseEvent = new ManualResetEventSlim(false);
            var starterExitedEvent = new ManualResetEventSlim(false);
            bool blockingStarterCompleted = false;
            bool blockingStarterResult = true;
            bool blockingStarterEntered = false;
            bool blockingStarterExited = false;
            stopwatch.Restart();
            try
            {
                using (var starterHandler =
                    new FirstUnavailableThenBlockingHandler())
                using (var client = new OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    starterHandler,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored)
                    {
                        starterEnteredEvent.Set();
                        try
                        {
                            starterReleaseEvent.Wait();
                            return true;
                        }
                        finally
                        {
                            starterExitedEvent.Set();
                        }
                    }))
                {
                    blockingStarterResult = client.EnsureServerAsync(
                        CancellationToken.None).GetAwaiter().GetResult();
                    blockingStarterCompleted = true;
                }
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "Ollama blocking-starter deadline regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                stopwatch.Stop();
                starterReleaseEvent.Set();
                blockingStarterExited = starterExitedEvent.Wait(
                    TimeSpan.FromSeconds(3));
                blockingStarterEntered = starterEnteredEvent.IsSet;
                starterExitedEvent.Dispose();
                starterReleaseEvent.Dispose();
                starterEnteredEvent.Dispose();
            }
            ok &= Check(
                sb,
                "Ollama overall deadline bounds a synchronous blocking starter",
                blockingStarterCompleted &&
                !blockingStarterResult &&
                blockingStarterEntered &&
                blockingStarterExited &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));

            var lateStarterEntered = new ManualResetEventSlim(false);
            var lateStarterRelease = new ManualResetEventSlim(false);
            var lateStarterExited = new ManualResetEventSlim(false);
            int lateLaunchCount = 0;
            int lateStarterObservedCancellation = 0;
            bool lateStarterCanceled = false;
            bool lateStarterFinished = false;
            stopwatch.Restart();
            try
            {
                using (var lateHandler =
                    new FirstUnavailableThenBlockingHandler())
                using (var callerCancellation =
                    new CancellationTokenSource())
                using (var client = new OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    lateHandler,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken starterToken)
                    {
                        lateStarterEntered.Set();
                        try
                        {
                            lateStarterRelease.Wait();
                            if (starterToken.IsCancellationRequested)
                                Interlocked.Exchange(
                                    ref lateStarterObservedCancellation,
                                    1);
                            starterToken.ThrowIfCancellationRequested();
                            Interlocked.Increment(ref lateLaunchCount);
                            return true;
                        }
                        finally
                        {
                            lateStarterExited.Set();
                        }
                    }))
                {
                    Task<bool> pending = client.EnsureServerAsync(
                        callerCancellation.Token);
                    lateStarterEntered.Wait(TimeSpan.FromSeconds(2));
                    callerCancellation.Cancel();
                    try { pending.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        lateStarterCanceled = true;
                    }
                    lateStarterRelease.Set();
                    lateStarterFinished = lateStarterExited.Wait(
                        TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "Ollama late-starter cancellation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                stopwatch.Stop();
                lateStarterRelease.Set();
                if (!lateStarterFinished)
                    lateStarterFinished = lateStarterExited.Wait(
                        TimeSpan.FromSeconds(2));
                if (lateStarterFinished)
                {
                    lateStarterExited.Dispose();
                    lateStarterRelease.Dispose();
                    lateStarterEntered.Dispose();
                }
            }
            ok &= Check(
                sb,
                "Ollama cancellation prevents a queued starter's late launch",
                lateStarterCanceled &&
                lateStarterFinished &&
                Volatile.Read(ref lateStarterObservedCancellation) == 1 &&
                Volatile.Read(ref lateLaunchCount) == 0 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));

            bool callerCancellationObserved = false;
            stopwatch.Restart();
            try
            {
                using (var callerHandler = new BlockingHeadersHandler())
                using (var callerCancellation = new CancellationTokenSource())
                using (var client = new OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    callerHandler,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored) { return true; }))
                {
                    callerCancellation.CancelAfter(
                        TimeSpan.FromMilliseconds(75));
                    client.EnsureServerAsync(
                        callerCancellation.Token).GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException)
            {
                callerCancellationObserved = true;
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "Ollama caller-cancellation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            stopwatch.Stop();
            ok &= Check(
                sb,
                "Ollama startup preserves caller cancellation",
                callerCancellationObserved &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));
            return ok;
        }

        private static bool CheckAiHttpStatusPolicy(StringBuilder sb)
        {
            bool ok = true;
            using (var badRequest = new HttpResponseMessage(HttpStatusCode.BadRequest))
            {
                bool deterministic = false;
                try { AiEndpointPolicy.EnsureSuccess(badRequest); }
                catch (AiBackendHttpException ex)
                {
                    deterministic = ex.StatusCode == 400 && !ex.IsTransient;
                }
                ok &= Check(sb, "HTTP 400 is not retryable", deterministic);
            }

            using (var throttled = new HttpResponseMessage((HttpStatusCode)429))
            {
                bool transient = false;
                try { AiEndpointPolicy.EnsureSuccess(throttled); }
                catch (AiBackendHttpException ex)
                {
                    transient = ex.StatusCode == 429 && ex.IsTransient;
                }
                ok &= Check(sb, "HTTP 429 is retryable", transient);
            }

            using (var redirect = new HttpResponseMessage(HttpStatusCode.Redirect))
            {
                bool deterministicRedirect = false;
                try { AiEndpointPolicy.EnsureSuccess(redirect); }
                catch (AiBackendHttpException ex)
                {
                    deterministicRedirect = ex.StatusCode == 302 && !ex.IsTransient;
                }
                ok &= Check(
                    sb,
                    "AI redirect rejected as non-retryable before credential forwarding",
                    deterministicRedirect);
            }

            var backend = new DeterministicFailureBackend();
            bool failedWithoutRetry = Throws<AiBackendHttpException>(delegate
            {
                AiBrain.ChatWithRetryForDiagnosticsAsync(
                    backend,
                    "model",
                    new List<ChatMessage>
                    {
                        ChatMessage.User("test", null)
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
            });
            ok &= Check(
                sb,
                "deterministic AI failure was not retried",
                failedWithoutRetry && backend.ChatCalls == 1);
            return ok;
        }

        private static bool CheckAiRetirementBound(StringBuilder sb)
        {
            bool ok = true;
            var manager = new AiSessionManager();
            try
            {
                var settings = new AiSettings
                {
                    TextModel = "text-model",
                    VisionModel = "vision-model"
                };
                manager.ReconfigureAsync(
                    delegate
                    {
                        return new AiBrain(
                            new CancellationIgnoringBackend(),
                            settings);
                    },
                    true,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();

                Stopwatch stopwatch = Stopwatch.StartNew();
                manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();
                stopwatch.Stop();
                ok &= Check(sb, "cancellation-ignoring AI retirement bounded",
                    stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "AI retirement test threw " + ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiReconfigureDisposeRace(StringBuilder sb)
        {
            bool ok = true;
            var admitted = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var manager = new AiSessionManager();
            Task<bool> reconfigure = null;
            try
            {
                manager.ReconfigureAdmittedForDiagnostics =
                    delegate
                    {
                        admitted.Set();
                        release.Wait();
                    };
                reconfigure = Task.Run(delegate
                {
                    return manager.ReconfigureAsync(
                        null,
                        false,
                        false,
                        CancellationToken.None).GetAwaiter().GetResult();
                });
                bool reachedAdmission =
                    admitted.Wait(TimeSpan.FromSeconds(2));
                manager.DisposeForDiagnostics(
                    TimeSpan.FromMilliseconds(250));
                release.Set();
                bool completed =
                    reconfigure.Wait(TimeSpan.FromSeconds(2));
                ok &= Check(
                    sb,
                    "AI reconfiguration returns false when disposal wins before semaphore wait",
                    reachedAdmission &&
                    completed &&
                    reconfigure.Status == TaskStatus.RanToCompletion &&
                    !reconfigure.Result);
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI reconfigure/dispose race threw " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                release.Set();
                if (reconfigure != null)
                    try { reconfigure.Wait(TimeSpan.FromSeconds(2)); }
                    catch { }
                manager.Dispose();
                release.Dispose();
                admitted.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireDurability(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckAiAfterRetireSupersession(sb);
            ok &= CheckAiAfterRetireMultipleSupersessions(sb);
            ok &= CheckAiAfterRetireNormalDispose(sb);
            ok &= CheckAiAfterRetireDeferredDispose(sb);
            return ok;
        }

        private static bool CheckAiAfterRetireSupersession(StringBuilder sb)
        {
            bool ok = true;
            RetirementTrackingBackend backend;
            AiSessionManager manager = CreateRetirementTestManager(out backend);
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            try
            {
                operation.Wait();
                held = true;
                int callbackCount = 0;
                bool observedRetired = false;
                bool observedSerialized = false;
                Task<bool> first = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None,
                    delegate
                    {
                        Interlocked.Increment(ref callbackCount);
                        observedRetired =
                            backend.UnloadCalls == 1 &&
                            backend.DisposeCount == 1;
                        observedSerialized = operation.CurrentCount == 0;
                    });
                Task<bool> second = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None);
                bool supersededWhileHeld = first.Wait(TimeSpan.FromSeconds(1));

                operation.Release();
                held = false;
                Task.WaitAll(new Task[] { first, second });

                ok &= Check(
                    sb,
                    "after-retire action survives a superseding generation",
                    supersededWhileHeld &&
                    callbackCount == 1 &&
                    observedRetired &&
                    observedSerialized);
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "after-retire supersession test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireMultipleSupersessions(StringBuilder sb)
        {
            bool ok = true;
            var manager = new AiSessionManager();
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            try
            {
                operation.Wait();
                held = true;
                int firstCount = 0;
                int secondCount = 0;
                int order = 0;
                int firstOrder = 0;
                int secondOrder = 0;
                bool callbacksSerialized = true;

                Task<bool> first = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None,
                    delegate
                    {
                        Interlocked.Increment(ref firstCount);
                        firstOrder = Interlocked.Increment(ref order);
                        callbacksSerialized &= operation.CurrentCount == 0;
                    });
                Task<bool> second = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None,
                    delegate
                    {
                        Interlocked.Increment(ref secondCount);
                        secondOrder = Interlocked.Increment(ref order);
                        callbacksSerialized &= operation.CurrentCount == 0;
                    });
                Task<bool> third = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None);
                Task<bool> fourth = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None);

                operation.Release();
                held = false;
                Task.WaitAll(new Task[] { first, second, third, fourth });

                ok &= Check(
                    sb,
                    "after-retire actions survive multiple superseding generations exactly once",
                    firstCount == 1 &&
                    secondCount == 1 &&
                    firstOrder == 1 &&
                    secondOrder == 2 &&
                    callbacksSerialized);
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "after-retire multi-supersession test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireNormalDispose(StringBuilder sb)
        {
            bool ok = true;
            RetirementTrackingBackend backend;
            AiSessionManager manager = CreateRetirementTestManager(out backend);
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            try
            {
                operation.Wait();
                held = true;
                int callbackCount = 0;
                bool observedRetired = false;
                bool observedSerialized = false;
                using (var cancellation = new CancellationTokenSource())
                {
                    Task<bool> pending = manager.ReconfigureAsync(
                        null,
                        false,
                        false,
                        cancellation.Token,
                        delegate
                        {
                            Interlocked.Increment(ref callbackCount);
                            observedRetired =
                                backend.UnloadCalls == 1 &&
                                backend.DisposeCount == 1;
                            observedSerialized = operation.CurrentCount == 0;
                        });
                    cancellation.Cancel();
                    bool canceledWhileHeld =
                        pending.Wait(TimeSpan.FromSeconds(1));

                    operation.Release();
                    held = false;
                    // Exercise the production disposal budget here. A zero diagnostic
                    // budget intentionally skips the optional unload wait and only
                    // disposes the backend, so it cannot prove normal retirement.
                    manager.Dispose();

                    ok &= Check(
                        sb,
                        "normal dispose drains pending after-retire actions",
                        canceledWhileHeld &&
                        callbackCount == 1 &&
                        observedRetired &&
                        observedSerialized);
                }
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "after-retire normal-dispose test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireDeferredDispose(StringBuilder sb)
        {
            bool ok = true;
            RetirementTrackingBackend backend;
            AiSessionManager manager = CreateRetirementTestManager(out backend);
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            using (var completed = new ManualResetEventSlim(false))
            {
                try
                {
                    operation.Wait();
                    held = true;
                    int callbackCount = 0;
                    bool observedRetired = false;
                    bool observedSerialized = false;
                    using (var cancellation = new CancellationTokenSource())
                    {
                        Task<bool> pending = manager.ReconfigureAsync(
                            null,
                            false,
                            false,
                            cancellation.Token,
                            delegate
                            {
                                Interlocked.Increment(ref callbackCount);
                                observedRetired =
                                    backend.UnloadCalls == 1 &&
                                    backend.DisposeCount == 1;
                                observedSerialized =
                                    operation.CurrentCount == 0;
                                completed.Set();
                            });
                        cancellation.Cancel();
                        bool canceledWhileHeld =
                            pending.Wait(TimeSpan.FromSeconds(1));

                        manager.DisposeForDiagnostics(TimeSpan.Zero);
                        operation.Release();
                        held = false;
                        bool deferredCompleted =
                            completed.Wait(TimeSpan.FromSeconds(3));

                        ok &= Check(
                            sb,
                            "deferred dispose drains pending after-retire actions",
                            canceledWhileHeld &&
                            deferredCompleted &&
                            callbackCount == 1 &&
                            observedRetired &&
                            observedSerialized);
                    }
                }
                catch (Exception ex)
                {
                    ok &= Check(sb,
                        "after-retire deferred-dispose test threw " +
                        ex.GetType().Name + ": " + ex.Message,
                        false);
                }
                finally
                {
                    if (held) operation.Release();
                    manager.Dispose();
                }
            }
            return ok;
        }

        private static AiSessionManager CreateRetirementTestManager(
            out RetirementTrackingBackend backend)
        {
            var manager = new AiSessionManager();
            var createdBackend = new RetirementTrackingBackend();
            var settings = new AiSettings
            {
                TextModel = "retirement-model",
                VisionModel = "retirement-model"
            };
            manager.ReconfigureAsync(
                delegate
                {
                    return new AiBrain(createdBackend, settings);
                },
                true,
                false,
                CancellationToken.None).GetAwaiter().GetResult();
            backend = createdBackend;
            return manager;
        }

        private static SemaphoreSlim GetManagerOperation(
            AiSessionManager manager)
        {
            FieldInfo field = typeof(AiSessionManager).GetField(
                "_operation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(
                    typeof(AiSessionManager).FullName,
                    "_operation");
            return (SemaphoreSlim)field.GetValue(manager);
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }

        private static bool IsWellFormedUtf16(string value)
        {
            if (value == null) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[index + 1]))
                        return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index])
                    return false;
            return true;
        }
    }
}
