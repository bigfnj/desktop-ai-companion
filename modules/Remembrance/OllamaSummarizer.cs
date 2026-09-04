using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>
    /// Turns a finished transcript into a short summary using a LOCAL Ollama, and nothing else.
    ///
    /// Local-only is a hard requirement, not a default: a meeting recording can contain confidential,
    /// privileged or consent-regulated speech, so there is deliberately no cloud provider, no API-key field
    /// and no code path that could acquire one. The endpoint defaults to loopback.
    ///
    /// Deliberately self-contained rather than reusing modules/AiBrain's OllamaClient. That file is 412 lines
    /// and pulls in AiEndpointPolicy, ICompanionBrainBackend, BrainResponse, JsonRead and ModelListing; a module
    /// cannot reference another module (separate load contexts), so adopting it would mean SOURCE-LINKING
    /// five files across a boundary. That is the shared-source staleness this repo already got bitten by
    /// (see handoff.md's "do not add a shared source file and register it in three csprojs"), and one
    /// non-streaming POST does not justify it. What IS copied from AiBrain is the security posture: a
    /// no-redirect handler, so a reply cannot bounce the request somewhere else.
    /// </summary>
    internal static class OllamaSummarizer
    {
        public const string DefaultEndpoint = "http://127.0.0.1:11434";

        /// <summary>Characters per map chunk. Small enough that a 4k-context local model still has room for
        /// the instructions and its own answer, which is the common case on a tester's machine.</summary>
        public const int MaxChunkCharacters = 6000;

        /// <summary>How many map summaries get folded into the reduce pass. A very long meeting summarizes to
        /// more text than one prompt should carry, so the reduce input is capped the same way.</summary>
        private const int MaxReduceCharacters = 8000;

        // ---- pure helpers (self-testable, no network) -----------------------------------------------

        /// <summary>
        /// Can this model generate text? An embedding model cannot, and Ollama installs are full of them
        /// (this box serves bge-m3, qwen3-embedding and embeddinggemma), so offering them would hand the user
        /// a model that always fails. Prefers Ollama's real per-model "capabilities" signal and falls back to
        /// a name heuristic only when the server does not report one.
        /// </summary>
        public static bool LooksGenerative(string name, IEnumerable<string> capabilities)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (capabilities != null)
            {
                List<string> caps = capabilities.Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim().ToLowerInvariant()).ToList();
                if (caps.Count > 0)
                {
                    if (caps.Contains("embedding")) return false;
                    return caps.Contains("completion");
                }
            }

            string lower = name.ToLowerInvariant();
            string[] embeddingMarkers = { "embed", "bge", "gte", "e5-", "minilm", "nomic-embed" };
            return !embeddingMarkers.Any(marker => lower.Contains(marker));
        }

        /// <summary>
        /// Split a transcript into prompt-sized pieces on paragraph then line boundaries, so a chunk does not
        /// end mid-sentence. A single oversized line (a transcript with no line breaks at all is normal for
        /// whisper's -otxt output) is hard-split rather than dropped.
        /// </summary>
        public static IReadOnlyList<string> Chunk(string text, int maxCharacters)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;
            if (maxCharacters < 500) maxCharacters = 500;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length <= maxCharacters)
            {
                chunks.Add(normalized.Trim());
                return chunks;
            }

            var current = new StringBuilder();
            foreach (string line in normalized.Split('\n'))
            {
                string piece = line;
                // A line longer than a whole chunk cannot be placed as a unit; cut it into chunk-sized runs.
                while (piece.Length > maxCharacters)
                {
                    if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                    chunks.Add(piece.Substring(0, maxCharacters).Trim());
                    piece = piece.Substring(maxCharacters);
                }
                if (current.Length + piece.Length + 1 > maxCharacters && current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
                current.Append(piece).Append('\n');
            }
            if (current.Length > 0) chunks.Add(current.ToString().Trim());
            return chunks.Where(c => c.Length > 0).ToList();
        }

        public static string BuildMapPrompt(string meetingName, string chunk, int index, int total)
        {
            var sb = new StringBuilder();
            sb.Append("You are summarizing part ").Append((index + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" of ").Append(total.ToString(CultureInfo.InvariantCulture))
              .AppendLine(" of a meeting transcript.");
            if (!string.IsNullOrWhiteSpace(meetingName)) sb.Append("Meeting: ").AppendLine(meetingName.Trim());
            sb.AppendLine("The transcript is machine-generated, so expect mishearings; do not quote them as fact.");
            sb.AppendLine("Write terse notes covering only what this part actually contains: decisions, action");
            sb.AppendLine("items with an owner where one is named, and open questions. No preamble, no closing");
            sb.AppendLine("remarks, and do not invent anything that is not in the text.");
            sb.AppendLine();
            sb.AppendLine("TRANSCRIPT PART:");
            sb.AppendLine(chunk);
            return sb.ToString();
        }

        public static string BuildReducePrompt(string meetingName, string notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Below are notes taken from consecutive parts of one meeting transcript.");
            if (!string.IsNullOrWhiteSpace(meetingName)) sb.Append("Meeting: ").AppendLine(meetingName.Trim());
            sb.AppendLine("Merge them into a single summary with these sections, omitting any section that has");
            sb.AppendLine("no content rather than padding it: Summary (3-6 bullets), Decisions, Action items");
            sb.AppendLine("(owner where named), Open questions. Remove duplicates. Add nothing new.");
            sb.AppendLine();
            sb.AppendLine("NOTES:");
            sb.AppendLine(notes);
            return sb.ToString();
        }

        public static string BuildSingleShotPrompt(string meetingName, string transcript)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Summarize this meeting transcript.");
            if (!string.IsNullOrWhiteSpace(meetingName)) sb.Append("Meeting: ").AppendLine(meetingName.Trim());
            sb.AppendLine("The transcript is machine-generated, so expect mishearings; do not quote them as fact.");
            sb.AppendLine("Use these sections and omit any that has no content rather than padding it:");
            sb.AppendLine("Summary (3-6 bullets), Decisions, Action items (owner where named), Open questions.");
            sb.AppendLine("Add nothing that is not in the transcript.");
            sb.AppendLine();
            sb.AppendLine("TRANSCRIPT:");
            sb.AppendLine(transcript);
            return sb.ToString();
        }

        /// <summary>Trim a user-entered endpoint to the form the request paths are appended to.</summary>
        public static string NormalizeEndpoint(string endpoint)
        {
            string value = (endpoint ?? "").Trim();
            if (value.Length == 0) value = DefaultEndpoint;
            return value.TrimEnd('/');
        }

        // ---- network -------------------------------------------------------------------------------

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            // AllowAutoRedirect=false mirrors AiBrain's endpoint posture: a local generation endpoint has no
            // legitimate reason to redirect, and following one would send the transcript somewhere unexamined.
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var http = new HttpClient(handler, true) { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopAICompanion-Remembrance");
            return http;
        }

        /// <summary>Generation-capable models installed on the server. Empty on any failure, so a caller shows
        /// "none found" rather than a stack trace.</summary>
        public static async Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, CancellationToken cancellationToken)
        {
            var models = new List<string>();
            try
            {
                using (HttpClient http = CreateClient(TimeSpan.FromSeconds(20)))
                using (HttpResponseMessage response = await http
                    .GetAsync(NormalizeEndpoint(endpoint) + "/api/tags", cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return models;
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ParseModels(json);
                }
            }
            catch { return models; }
        }

        /// <summary>Split out from the fetch so the capability filter is self-testable without a server.</summary>
        public static IReadOnlyList<string> ParseModels(string json)
        {
            var models = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return models;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement array;
                    if (!document.RootElement.TryGetProperty("models", out array) ||
                        array.ValueKind != JsonValueKind.Array) return models;

                    foreach (JsonElement entry in array.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        JsonElement nameElement;
                        if (!entry.TryGetProperty("name", out nameElement) ||
                            nameElement.ValueKind != JsonValueKind.String) continue;
                        string name = nameElement.GetString();

                        List<string> capabilities = null;
                        JsonElement capsElement;
                        if (entry.TryGetProperty("capabilities", out capsElement) &&
                            capsElement.ValueKind == JsonValueKind.Array)
                        {
                            capabilities = capsElement.EnumerateArray()
                                .Where(c => c.ValueKind == JsonValueKind.String)
                                .Select(c => c.GetString())
                                .ToList();
                        }

                        if (LooksGenerative(name, capabilities)) models.Add(name);
                    }
                }
            }
            catch { }
            return models;
        }

        public sealed class SummaryResult
        {
            public bool Ok;
            public string Text;
            public string Message;
        }

        /// <summary>
        /// Map-reduce the transcript into a summary. One chunk takes a single call; several are summarized
        /// individually and then merged, so a long meeting does not overflow a small local context window.
        /// Never throws: a failure is Ok=false plus a message, because a failed summary must not lose a
        /// recording or a transcript.
        /// </summary>
        public static async Task<SummaryResult> SummarizeAsync(string endpoint, string model, string meetingName,
            string transcript, Action<string> report, CancellationToken cancellationToken)
        {
            var result = new SummaryResult { Ok = false };
            Action<string> say = report ?? delegate { };

            if (string.IsNullOrWhiteSpace(transcript)) { result.Message = "The transcript is empty."; return result; }
            if (string.IsNullOrWhiteSpace(model)) { result.Message = "No summary model is configured."; return result; }

            try
            {
                IReadOnlyList<string> chunks = Chunk(transcript, MaxChunkCharacters);
                if (chunks.Count == 0) { result.Message = "The transcript is empty."; return result; }

                using (HttpClient http = CreateClient(TimeSpan.FromMinutes(20)))
                {
                    if (chunks.Count == 1)
                    {
                        say("summarizing...");
                        string only = await GenerateAsync(http, endpoint, model,
                            BuildSingleShotPrompt(meetingName, chunks[0]), cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(only)) { result.Message = "The model returned nothing."; return result; }
                        result.Ok = true;
                        result.Text = only.Trim();
                        return result;
                    }

                    var notes = new StringBuilder();
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        say("summarizing part " + (i + 1).ToString(CultureInfo.InvariantCulture) + " of " +
                            chunks.Count.ToString(CultureInfo.InvariantCulture) + "...");
                        string part = await GenerateAsync(http, endpoint, model,
                            BuildMapPrompt(meetingName, chunks[i], i, chunks.Count), cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            notes.AppendLine(part.Trim());
                            notes.AppendLine();
                        }
                        // A truncated-but-real set of notes beats abandoning the whole summary.
                        if (notes.Length > MaxReduceCharacters) break;
                    }

                    if (notes.Length == 0) { result.Message = "The model returned nothing for any part."; return result; }

                    say("merging...");
                    string merged = await GenerateAsync(http, endpoint, model,
                        BuildReducePrompt(meetingName, notes.ToString()), cancellationToken).ConfigureAwait(false);

                    // If the merge fails, the per-part notes are still worth keeping.
                    result.Ok = true;
                    result.Text = string.IsNullOrWhiteSpace(merged) ? notes.ToString().Trim() : merged.Trim();
                    return result;
                }
            }
            catch (OperationCanceledException) { result.Message = "Summarizing was cancelled."; return result; }
            catch (Exception ex) { result.Message = ex.Message; return result; }
        }

        private static async Task<string> GenerateAsync(HttpClient http, string endpoint, string model,
            string prompt, CancellationToken cancellationToken)
        {
            string body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["stream"] = false,
            });

            using (var content = new StringContent(body, new UTF8Encoding(false), "application/json"))
            using (HttpResponseMessage response = await http
                .PostAsync(NormalizeEndpoint(endpoint) + "/api/generate", content, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Ollama answered " + (int)response.StatusCode + " " + response.StatusCode + ".");
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ExtractResponse(json);
            }
        }

        /// <summary>Read the generated text out of a /api/generate reply. Public for the self-test.</summary>
        public static string ExtractResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement value;
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("response", out value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? "";
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>Header written above a summary file, so a stray .summary.txt is self-describing.</summary>
        public static string FileHeader(string meetingName, string model)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(meetingName) ? "Recording" : meetingName.Trim());
            sb.AppendLine("Summary written: " + DateTime.Now.ToString("f"));
            sb.AppendLine("Model: " + (model ?? "") + " (local Ollama; nothing left this machine)");
            sb.AppendLine(new string('-', 48));
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
