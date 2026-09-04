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
    internal sealed class OpenAiCompatBackend : ICompanionBrainBackend
    {
        private readonly HttpClient _http;
        private readonly string _base;   // ".../v1"
        private readonly string _key;
        private readonly TimeSpan _deadline;

        public OpenAiCompatBackend(string baseUrl, string apiKey, TimeSpan timeout)
        {
            _base = AiEndpointPolicy.NormalizeOrThrow(baseUrl, "baseUrl");
            _key  = apiKey ?? "";
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
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
        /// HttpClientHandler. Mirrors OllamaClient's diagnostic constructor.</summary>
        internal OpenAiCompatBackend(string baseUrl, string apiKey, TimeSpan timeout, HttpMessageHandler handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            _base = AiEndpointPolicy.NormalizeOrThrow(baseUrl, "baseUrl");
            _key = apiKey ?? "";
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Add("User-Agent", "DesktopAICompanion");
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bigfnj/desktop-ai-companion");
            _http.DefaultRequestHeaders.Add("X-Title", "DesktopAICompanion");
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try
            {
                using (var request = CreateRequest(HttpMethod.Get, "/models"))
                {
                    return await AiEndpointPolicy.SendAndCheckSuccessAsync(
                        _http,
                        request,
                        _deadline,
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
                    string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                        _http,
                        request,
                        _deadline,
                        ct).ConfigureAwait(false);
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

    /// <summary>Provider presets for the "One Interface" — base URL + whether a key/host is needed.</summary>
    internal static class AiProviders
    {
        public struct Preset { public string Id, Name, BaseUrl; public bool NeedsKey, IsLocal; }

        public static readonly Preset[] All =
        {
            new Preset { Id="ollama",    Name="Ollama (local)",         BaseUrl="http://localhost:11434", NeedsKey=false, IsLocal=true  },
            new Preset { Id="lmstudio",  Name="LM Studio (local)",      BaseUrl="http://localhost:1234/v1", NeedsKey=false, IsLocal=true },
            new Preset { Id="llamacpp",  Name="llama.cpp (local)",      BaseUrl="http://localhost:8080/v1", NeedsKey=false, IsLocal=true },
            new Preset { Id="openrouter",Name="OpenRouter (cloud)",     BaseUrl="https://openrouter.ai/api/v1", NeedsKey=true, IsLocal=false },
            new Preset { Id="openai",    Name="OpenAI (cloud)",         BaseUrl="https://api.openai.com/v1", NeedsKey=true, IsLocal=false },
            new Preset { Id="custom",    Name="Custom (OpenAI-compat)", BaseUrl="", NeedsKey=false, IsLocal=false },
        };

        public static Preset Get(string id)
        {
            foreach (var p in All) if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return All[0];
        }
    }
}
