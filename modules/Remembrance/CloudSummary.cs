using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Ai;          // AiEndpointPolicy, AiBackendHttpException, JsonRead: AI Brain's, compiled here by link
using DesktopAICompanion.ModuleKit;   // AtomicFile

// The summary on a cloud provider (remembrance 2.2.0, the owner, 2026-10-07: "we forgot the 'runs on' Local Model, cloud
// provider, claude cli, codex cli box"). AI Brain's four engines, in its words, so "Summary runs on" reads like "Brain runs
// on". The transport is AI Brain's, linked rather than copied (Remembrance.csproj): AiEndpointPolicy refuses plain http to
// anything but this computer, follows no redirect (a key is never forwarded to a host the provider pointed at), bounds
// every read and every wait, and carries the provider's own error text for the pane while the log gets a category. What is
// here is what a summary needs and AI Brain's backend does not: one chat call with no image, under the summary's own
// deadline, and a model listing for the card. AI Brain's OpenAiCompatBackend was not linked: it implements the brain's
// backend interfaces, which would bring half of its engine with it.
namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>The providers the card offers: AI Brain's rows (AiProviders in OpenAiCompatBackend.cs), in its dropdown's
    /// order. "custom" is any OpenAI-compatible base URL, a server on this computer included (llama.cpp, LM Studio, Ollama's
    /// own /v1), which is how a local OpenAI-style server is reached.</summary>
    internal static class CloudProviders
    {
        internal const string Default = "openai";
        private static readonly string[][] Rows =
        {
            new[] { "openai", "https://api.openai.com/v1" },
            new[] { "openrouter", "https://openrouter.ai/api/v1" },
            new[] { "custom", "" },
        };

        internal static string[] Ids()
        {
            var ids = new string[Rows.Length];
            for (int i = 0; i < Rows.Length; i++) ids[i] = Rows[i][0];
            return ids;
        }

        internal static bool IsKnown(string id)
        {
            foreach (string[] row in Rows) if (string.Equals(row[0], id, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static string PresetUrl(string id)
        {
            foreach (string[] row in Rows) if (string.Equals(row[0], id, StringComparison.Ordinal)) return row[1];
            return "";
        }

        /// <summary>The base URL a call goes to: the card's box when filled, else the provider's own; "" when neither
        /// (custom with a blank box).</summary>
        internal static string EffectiveUrl(string provider, string endpoint)
        {
            string typed = (endpoint ?? "").Trim();
            return typed.Length > 0 ? typed : PresetUrl(provider);
        }

        /// <summary>The host a URL names, for the pane and the summary file's header ("api.openai.com"), or the text
        /// itself when it is not a URL.</summary>
        internal static string HostOf(string url)
        {
            Uri uri;
            return Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out uri) && uri.Host.Length > 0 ? uri.Host : (url ?? "").Trim();
        }
    }

    /// <summary>How one cloud call ended. <see cref="Failure"/> is the plain words for the pane and the Status line, and
    /// can carry the provider's own text; <see cref="Category"/> is the only thing the log gets.</summary>
    internal sealed class CloudResult
    {
        internal bool Ok;
        internal string Text = "";
        internal string Failure = "";
        internal string Category = "";
        internal List<string> Models = new List<string>();
        internal long ElapsedMilliseconds;
    }

    /// <summary>One provider at one base URL with one key: a chat call and a model listing, never throwing.</summary>
    internal sealed class CloudSummarizer : IDisposable
    {
        internal static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(60);
        internal const int MaximumModelCharacters = 256;

        private readonly HttpClient _http;
        private readonly string _base;
        private readonly string _key;
        private readonly string _host;

        private CloudSummarizer(string normalizedBase, string key, HttpMessageHandler handler)
        {
            _base = normalizedBase;
            _key = key ?? "";
            _host = CloudProviders.HostOf(normalizedBase);
            // An injected transport (the self-test's fake provider) is the caller's to dispose: one fake serves many calls.
            _http = handler != null
                ? new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan }
                : new HttpClient(AiEndpointPolicy.CreateNoRedirectHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Add("User-Agent", "DesktopAICompanion");
            // OpenRouter's attribution headers, AI Brain's; harmless for other providers.
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bigfnj/desktop-ai-companion");
            _http.DefaultRequestHeaders.Add("X-Title", "DesktopAICompanion");
        }

        /// <summary>A summarizer for <paramref name="baseUrl"/>, or null with the reason in the pane's words: no base URL,
        /// or one AiEndpointPolicy refuses (plain http to another computer, credentials or a query in it).
        /// <paramref name="handler"/> is the self-test's transport; null for the real one.</summary>
        internal static CloudSummarizer TryCreate(string baseUrl, string apiKey, HttpMessageHandler handler, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                error = "Enter the provider's base URL (it ends in /v1).";
                return null;
            }
            string normalized;
            string policyError;
            if (!AiEndpointPolicy.TryNormalize(baseUrl, out normalized, out policyError))
            {
                error = policyError;
                return null;
            }
            return new CloudSummarizer(normalized, apiKey, handler);
        }

        internal string Host { get { return _host; } }

        /// <summary>Null when <paramref name="model"/> can be sent, else the reason.</summary>
        internal static string CheckModel(string model)
        {
            string m = (model ?? "").Trim();
            if (m.Length == 0) return "Pick a model first (Refresh cloud models lists what the provider offers).";
            if (m.Length > MaximumModelCharacters) return "That is longer than a model name can be.";
            foreach (char c in m) if (c <= ' ' || c == '\u007f') return "A model name has no spaces or line breaks in it.";
            return null;
        }

        /// <summary>One chat call: the system prompt and the prompt, no stream, the answer's text. Never throws.</summary>
        internal async Task<CloudResult> ChatAsync(string model, string systemPrompt, string prompt, TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            var result = new CloudResult();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string modelError = CheckModel(model);
            if (modelError != null)
            {
                result.Failure = modelError;
                result.Category = "no-model";
                return result;
            }
            try
            {
                var messages = new JsonArray();
                if (!string.IsNullOrEmpty(systemPrompt)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt ?? "" });
                var payload = new JsonObject { ["model"] = model.Trim(), ["messages"] = messages, ["stream"] = false };
                using (HttpRequestMessage request = NewRequest(HttpMethod.Post, "/chat/completions"))
                {
                    request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                    string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(_http, request, deadline, cancellationToken)
                        .ConfigureAwait(false);
                    JsonNode root = JsonNode.Parse(json);
                    JsonArray choices = root == null ? null : root["choices"] as JsonArray;
                    JsonObject message = choices != null && choices.Count > 0 && choices[0] is JsonObject first ? first["message"] as JsonObject : null;
                    string text = message == null ? "" : JsonRead.Str(message["content"]).Trim();
                    if (text.Length == 0)
                    {
                        result.Failure = _host + " answered with no text";
                        result.Category = "no-answer";
                        return result;
                    }
                    result.Ok = true;
                    result.Text = text;
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Failure = "the call was cancelled";
                result.Category = "cancelled";
                return result;
            }
            catch (Exception ex)
            {
                Classify(ex, _host, deadline, out result.Failure, out result.Category);
                return result;
            }
            finally
            {
                clock.Stop();
                result.ElapsedMilliseconds = clock.ElapsedMilliseconds;
            }
        }

        /// <summary>GET /models: the ids the provider lists, sorted, under AI Brain's listing cap and bound. Never throws;
        /// a refusal (a 401 for the key) is a failure with its words, which is how Refresh proves a typed key.</summary>
        internal async Task<CloudResult> ListModelsAsync(CancellationToken cancellationToken)
        {
            var result = new CloudResult();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (HttpRequestMessage request = NewRequest(HttpMethod.Get, "/models"))
                {
                    string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(_http, request, AiEndpointPolicy.ListingDeadline,
                        cancellationToken, AiEndpointPolicy.MaximumListingResponseBytes).ConfigureAwait(false);
                    JsonNode root = JsonNode.Parse(json);
                    JsonArray data = root == null ? null : root["data"] as JsonArray;
                    var ids = new List<string>();
                    if (data != null)
                        foreach (JsonNode entry in data)
                        {
                            string id = entry == null ? "" : JsonRead.Str(entry["id"]).Trim();
                            if (id.Length > 0 && CheckModel(id) == null && !ids.Contains(id)) ids.Add(id);
                        }
                    ids.Sort(StringComparer.OrdinalIgnoreCase);
                    result.Ok = true;
                    result.Models = ids;
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Failure = "the call was cancelled";
                result.Category = "cancelled";
                return result;
            }
            catch (Exception ex)
            {
                Classify(ex, _host, AiEndpointPolicy.ListingDeadline, out result.Failure, out result.Category);
                return result;
            }
            finally
            {
                clock.Stop();
                result.ElapsedMilliseconds = clock.ElapsedMilliseconds;
            }
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, _base + relativePath);
            if (!string.IsNullOrWhiteSpace(_key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            return request;
        }

        /// <summary>A failure in the pane's words (which can quote the provider) and the log's category (which never
        /// does: a provider's message is the one string here that could echo the request back).</summary>
        internal static void Classify(Exception ex, string host, TimeSpan deadline, out string words, out string category)
        {
            var http = ex as AiBackendHttpException;
            if (http != null)
            {
                int code = http.StatusCode;
                string said = string.IsNullOrEmpty(http.ProviderMessage) ? "" : ": " + http.ProviderMessage;
                string status = " (HTTP " + code.ToString(CultureInfo.InvariantCulture) + said + ")";
                if (code == 401 || code == 403) { words = host + " refused the API key" + status; category = "key-refused"; return; }
                if (code == 402) { words = "the account at " + host + " has no credit left" + status; category = "no-credit"; return; }
                if (code == 404) { words = host + " has no such model, or the base URL is wrong" + status; category = "not-found"; return; }
                if (code == 429) { words = host + " is limiting how fast this key may call; try again later" + status; category = "rate-limited"; return; }
                if (code >= 300 && code <= 399) { words = host + " answered with a redirect, which is never followed" + status; category = "redirect"; return; }
                if (code >= 500) { words = host + " had an error of its own" + status; category = "server-error"; return; }
                words = host + " refused the request" + status;
                category = "http-" + code.ToString(CultureInfo.InvariantCulture);
                return;
            }
            if (ex is TimeoutException)
            {
                words = host + " did not answer within " + ((int)deadline.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
                category = "timed-out";
                return;
            }
            if (ex is HttpRequestException)
            {
                words = "could not reach " + host;
                category = "unreachable";
                return;
            }
            if (ex is System.Text.Json.JsonException)
            {
                words = host + " answered with something that is not the JSON an OpenAI-compatible provider sends";
                category = "bad-answer";
                return;
            }
            words = "the call to " + host + " failed (" + ex.GetType().Name + ")";
            category = "failed";
        }

        public void Dispose() { _http.Dispose(); }
    }

    /// <summary>
    /// The cloud API key, sealed the way AI Brain seals its own and the CLI runner seals a sign-in token: DPAPI for the
    /// current Windows user, with an entropy string of its own, in this module's data folder (cloud\api-key.dpapi), never in
    /// settings.json (IModuleSettings stores cleartext; PluginApi.cs says so), never logged and never shown again. One
    /// key, not one per provider: a key that does not fit the provider chosen is what Validate says.
    /// </summary>
    internal sealed class CloudKeyStore
    {
        internal enum State { None, Saved, Unreadable }

        internal const string FileName = "api-key.dpapi";
        internal const int MaximumKeyCharacters = 8192;
        internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopAICompanion.Remembrance.CloudApiKey");

        private readonly string _path;

        /// <param name="folder">This module's own folder for it, or null when the host gave no storage.</param>
        internal CloudKeyStore(string folder)
        {
            _path = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, FileName);
        }

        internal string PathForDiagnostics { get { return _path; } }

        /// <summary>Null when <paramref name="value"/> can be a key, which comes back trimmed; else the reason. Like the
        /// sign-in token's check it refuses what cannot work (a space inside, a web address), not a prefix.</summary>
        internal static string Check(string value, out string key)
        {
            key = (value ?? "").Trim();
            if (key.Length == 0) return "The API key is empty.";
            if (key.Length > MaximumKeyCharacters) return "That is longer than an API key can be.";
            foreach (char c in key)
                if (c < '!' || c > '~')
                    return "An API key is one unbroken line of letters, digits and punctuation, and this one has a space or another character inside it, so it was probably not copied whole.";
            if (key.IndexOf("://", StringComparison.Ordinal) >= 0)
                return "That is a web address, not an API key. The base URL has a box of its own.";
            return null;
        }

        internal bool TrySet(string value, out string error)
        {
            string key;
            error = Check(value, out key);
            if (error != null) return false;
            if (_path == null)
            {
                error = "This module has no data folder of its own to keep an API key in.";
                return false;
            }
            string sealedKey;
            try
            {
                sealedKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                error = "Windows would not encrypt the key (" + ex.GetType().Name + ").";
                return false;
            }
            if (!AtomicFile.TryWriteAllText(_path, sealedKey, null))
            {
                error = "The key could not be written to this module's folder.";
                return false;
            }
            return true;
        }

        internal State Read(out string key)
        {
            key = null;
            if (_path == null) return State.None;
            try
            {
                if (!File.Exists(_path)) return State.None;
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(_path).Trim()), Entropy,
                    DataProtectionScope.CurrentUser);
                string unsealed;
                if (Check(Encoding.UTF8.GetString(clear), out unsealed) != null) return State.Unreadable;
                key = unsealed;
                return State.Saved;
            }
            catch
            {
                return State.Unreadable;
            }
        }

        /// <summary>Delete the saved key. The user's data, so the caller logs the delete either way (the delete-logging
        /// rule); this answers what happened in the pane's words and the log's.</summary>
        internal bool Remove(out string words, out string logLine)
        {
            if (_path == null || !File.Exists(_path))
            {
                words = "No API key is saved here.";
                logLine = null;
                return true;
            }
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex)
            {
                words = "✗ The saved API key could not be removed (" + ex.GetType().Name + "). Press Remove key again in a moment.";
                logLine = "cloud: api key not removed: " + ex.GetType().Name;
                return false;
            }
            words = "✓ Removed. The cloud summary has no key until you add one.";
            logLine = "cloud: api key removed";
            return true;
        }
    }
}
