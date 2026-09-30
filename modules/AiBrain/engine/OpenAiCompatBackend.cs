using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// One backend for any OpenAI-compatible <c>/v1</c> endpoint: LM Studio, llama.cpp
    /// (<c>llama-server</c>), OpenRouter, OpenAI, or a custom base URL. The base URL should include
    /// <c>/v1</c> (e.g. <c>https://openrouter.ai/api/v1</c>, <c>http://localhost:1234/v1</c>). Optional
    /// Bearer key. Chat via <c>/chat/completions</c>, vision via <c>image_url</c> content parts. These
    /// providers manage their own model lifetime, so start/warm/unload are no-ops (cloud has no local
    /// VRAM; local servers load on first request). Ollama keeps its native client for keep-alive VRAM
    /// control; everything else routes here.
    /// </summary>
    internal sealed class OpenAiCompatBackend : ICompanionBrainBackend, IModelLister
    {
        private readonly HttpClient _http;
        private readonly string _base;   // ".../v1"
        private readonly string _key;
        private readonly TimeSpan _deadline;
        /// <summary>The bound on the reachability probe, shorter than the chat deadline it used to borrow (F105).</summary>
        private readonly TimeSpan _probeDeadline;
        /// <summary>The bound on a model listing, shorter than the chat deadline it used to borrow (R-014).</summary>
        private readonly TimeSpan _listingDeadline;

        public OpenAiCompatBackend(string baseUrl, string apiKey, TimeSpan timeout)
        {
            _base = AiEndpointPolicy.NormalizeOrThrow(baseUrl, "baseUrl");
            _key  = apiKey ?? "";
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _probeDeadline = AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.AvailabilityProbeDeadline);
            _listingDeadline = AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.ListingDeadline);
            _http = new HttpClient(AiEndpointPolicy.CreateNoRedirectHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _http.DefaultRequestHeaders.Add("User-Agent", "DesktopAICompanion");
            // OpenRouter attribution headers (harmless for other providers).
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bigfnj/desktop-ai-companion");
            _http.DefaultRequestHeaders.Add("X-Title", "DesktopAICompanion");
        }

        /// <summary>Test-only: inject a fake transport (e.g. a canned /models response) instead of a real
        /// HttpClientHandler, and optionally a probe deadline of its own. Mirrors OllamaClient's diagnostic
        /// constructor.</summary>
        internal OpenAiCompatBackend(
            string baseUrl, string apiKey, TimeSpan timeout, HttpMessageHandler handler, TimeSpan? probeDeadline = null,
            TimeSpan? listingDeadline = null)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            _base = AiEndpointPolicy.NormalizeOrThrow(baseUrl, "baseUrl");
            _key = apiKey ?? "";
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _probeDeadline = probeDeadline.HasValue
                ? AiEndpointPolicy.ValidateDeadline(probeDeadline.Value, "probeDeadline")
                : AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.AvailabilityProbeDeadline);
            _listingDeadline = AiEndpointPolicy.Shorter(
                _deadline,
                listingDeadline.HasValue
                    ? AiEndpointPolicy.ValidateDeadline(listingDeadline.Value, "listingDeadline")
                    : AiEndpointPolicy.ListingDeadline);
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Add("User-Agent", "DesktopAICompanion");
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bigfnj/desktop-ai-companion");
            _http.DefaultRequestHeaders.Add("X-Title", "DesktopAICompanion");
        }

        /// <summary>
        /// "Reachable" means the endpoint ANSWERED, whatever it answered. A 401 is a server saying the key is
        /// wrong and a 404 a server saying the path is wrong; until 2026-09-29 both came back false here, so
        /// "Test connection" told an OpenAI user with a mistyped key that api.openai.com was not reachable, and
        /// the cloud+local composite treated a keyed-out cloud as down (F107). Only a redirect and a transport
        /// failure or timeout are "not reachable". The status itself surfaces from the chat request, where the
        /// recorded no-fallover-on-a-bad-key decision wants it. Local to this backend, deliberately: OllamaClient
        /// keeps the SUCCESS probe, because its EnsureServerAsync uses that answer to decide whether to launch
        /// `ollama serve`, and a foreign server answering 404 on :11434 must not suppress the launch.
        /// </summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try
            {
                using (var request = CreateRequest(HttpMethod.Get, "/models"))
                {
                    return await AiEndpointPolicy.SendAndCheckAnsweredAsync(
                        _http,
                        request,
                        _probeDeadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <summary>
        /// List models this endpoint reports as available (<c>GET /models</c> — the same endpoint
        /// <see cref="IsAvailableAsync"/> already probes, but reading the body this time). The generic
        /// OpenAI-compatible response carries no capability metadata, so every <see cref="ModelListing"/>
        /// comes back with <c>Vision = null</c> (unknown) — the caller applies the name heuristic. Never
        /// throws; an unreachable endpoint or a malformed response yields an empty list.
        /// </summary>
        public async Task<IReadOnlyList<ModelListing>> ListModelsAsync(CancellationToken ct)
        {
            var result = new List<ModelListing>();
            try
            {
                using (var request = CreateRequest(HttpMethod.Get, "/models"))
                {
                    // The LISTING cap, not the reply cap: a provider's catalogue is a flat list that only grows,
                    // and OpenRouter's stood at 72.5% of the 1 MiB reply cap on 2026-09-29; crossing it would
                    // have turned this into an empty list reported as "No models found" (F078).
                    // Under the LISTING bound, not the chat deadline: this runs on the ask path on a transition to
                    // reachable (F071), where a hung cloud primary used to hold the remark for the user's whole
                    // timeout (R-014).
                    string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                        _http,
                        request,
                        _listingDeadline,
                        ct,
                        AiEndpointPolicy.MaximumListingResponseBytes).ConfigureAwait(false);
                    JsonNode obj = JsonNode.Parse(json);
                    JsonArray data = obj?["data"] as JsonArray;
                    if (data != null)
                        foreach (JsonNode entry in data)
                        {
                            if (entry == null) continue;
                            string id = JsonRead.Str(entry["id"]);
                            if (id.Length == 0) continue;
                            result.Add(new ModelListing(id, null));
                        }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return result;
        }

        // We don't own these servers, and cloud has nothing to warm/unload.
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return IsAvailableAsync(ct); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            string normalizedModel = AiModelPolicy.NormalizeOrThrow(model, "model");
            JsonArray msgs = new JsonArray();
            foreach (ChatMessage m in messages)
            {
                JsonObject jm = new JsonObject { ["role"] = m.Role };
                if (m.ImagesBase64 != null && m.ImagesBase64.Length > 0)
                {
                    JsonArray parts = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Content))
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                    foreach (string b64 in m.ImagesBase64)
                        parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + b64 } });
                    jm["content"] = parts;
                }
                else jm["content"] = m.Content ?? "";
                msgs.Add(jm);
            }

            JsonObject payload = new JsonObject
            {
                ["model"] = normalizedModel,
                ["messages"] = msgs,
                ["stream"] = false
            };
            if (jsonFormat) payload["response_format"] = new JsonObject { ["type"] = "json_object" };

            using (var request = CreateRequest(HttpMethod.Post, "/chat/completions"))
            {
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                    _http,
                    request,
                    _deadline,
                    ct).ConfigureAwait(false);
                JsonNode obj = JsonNode.Parse(json);
                JsonArray choices = obj?["choices"] as JsonArray;
                if (choices != null && choices.Count > 0)
                {
                    JsonObject message = (choices[0] as JsonObject)?["message"] as JsonObject;
                    if (message != null) return JsonRead.Str(message["content"]);
                }
                return "";
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, _base + relativePath);
            if (!string.IsNullOrWhiteSpace(_key))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            return request;
        }

        public void Dispose() { _http.Dispose(); }
    }

    /// <summary>
    /// The CLOUD provider presets: the id AiSettings.Provider stores and the base URL prefilled on selection.
    /// Cloud only since schema v2, where Provider is the cloud selector and the local slot is
    /// Endpoint/LocalBackendKind. Until 2026-09-30 this table also carried ollama/lmstudio/llamacpp rows and
    /// Name/NeedsKey/IsLocal fields nothing read, and Get() answered an unknown id with the Ollama row, which
    /// SelectProviderEndpoint would have written over the local Endpoint (F108).
    /// </summary>
    internal static class AiProviders
    {
        public struct Preset { public string Id, BaseUrl; }

        public static readonly Preset[] All =
        {
            new Preset { Id="openrouter", BaseUrl="https://openrouter.ai/api/v1" },
            new Preset { Id="openai",     BaseUrl="https://api.openai.com/v1" },
            new Preset { Id="custom",     BaseUrl="" },
        };

        /// <summary>False, with a default preset, for an id no row carries. Never a fallback row.</summary>
        public static bool TryGet(string id, out Preset preset)
        {
            foreach (var p in All)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) { preset = p; return true; }
            preset = default(Preset);
            return false;
        }
    }
}
