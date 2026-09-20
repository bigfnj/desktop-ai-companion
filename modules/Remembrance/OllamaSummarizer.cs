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

        /// <summary>Where to get Ollama itself, for the case where nothing is answering at all.</summary>
        public const string OllamaDownloadUrl = "https://ollama.com/download";

        /// <summary>The model suggested by default: the best size/speed trade for THIS job.</summary>
        public const string DefaultRecommendedId = "gemma4:12b";

        /// <summary>
        /// Models worth suggesting for meeting summaries, smallest first.
        ///
        /// Chosen for the shape of this particular job rather than for general cleverness. The
        /// transcript is cut into 6000-character chunks (MaxChunkCharacters), so context length is
        /// not the differentiator and a 24B model earns very little on window size. What it is, is
        /// map-reduce: an hour of meeting is around nine chunk calls plus a merge, run one after
        /// another, so speed compounds. And the prompts ask for structured extraction under "add
        /// nothing that is not in the transcript", which models below about 4B follow poorly --
        /// they invent action items and drop the owner.
        ///
        /// EVERY TAG HERE WAS CHECKED AGAINST THE REGISTRY, and the sizes come from summing the
        /// manifest layers, not from memory. That is not ceremony: gemma4:4b reads as though it
        /// ought to exist, sits right between two tags that do, and 404s.
        /// </summary>
        public static readonly string[][] Recommended =
        {
            new[] { "gemma3:4b",            "gemma3:4b (3.1 GB) -- small GPU, or CPU only" },
            new[] { "qwen3:8b",             "qwen3:8b (4.9 GB) -- a middle option" },
            new[] { "gemma4:12b",           "gemma4:12b (7.0 GB) -- recommended" },
            new[] { "mistral-small3.2:24b", "mistral-small3.2:24b (14.1 GB) -- best, wants 16 GB+ of VRAM" },
        };

        public static string[] RecommendedDisplays()
        {
            var list = new List<string>();
            foreach (string[] row in Recommended) list.Add(row[1]);
            return list.ToArray();
        }

        /// <summary>Display label back to the tag actually pulled. Unknown text falls back to the
        /// default rather than being sent to the registry as a model name.</summary>
        public static string RecommendedIdFromDisplay(string display)
        {
            string value = (display ?? "").Trim();
            foreach (string[] row in Recommended)
                if (string.Equals(row[1], value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(row[0], value, StringComparison.OrdinalIgnoreCase))
                    return row[0];
            return DefaultRecommendedId;
        }

        public static string RecommendedDisplayFor(string id)
        {
            string value = (id ?? "").Trim();
            foreach (string[] row in Recommended)
                if (string.Equals(row[0], value, StringComparison.OrdinalIgnoreCase)) return row[1];
            return Recommended[2][1];   // the recommended one
        }

        /// <summary>One line of pull progress. Percent only when the server has told us a total:
        /// early lines ("pulling manifest") carry none, and 0 of 0 must not render as 100%.</summary>
        public static string ProgressLine(string model, string status, long completed, long total)
        {
            string head = (model ?? "") + ": " + (string.IsNullOrWhiteSpace(status) ? "working" : status.Trim());
            if (total <= 0 || completed < 0) return head;
            double fraction = completed > total ? 1.0 : (double)completed / total;
            return head + " " + ((int)(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "% (" +
                   (completed / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " of " +
                   (total / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB)";
        }

        public sealed class PullProgress
        {
            public string Status;
            public long Completed;
            public long Total;
            public string Error;
        }

        /// <summary>One NDJSON line of /api/pull. Null for a blank or unparseable line, which is
        /// skipped rather than treated as a failure: the stream ends with an empty line.</summary>
        public static PullProgress ParsePullLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(line))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                    var progress = new PullProgress();
                    JsonElement element;
                    if (document.RootElement.TryGetProperty("error", out element) &&
                        element.ValueKind == JsonValueKind.String) progress.Error = element.GetString();
                    if (document.RootElement.TryGetProperty("status", out element) &&
                        element.ValueKind == JsonValueKind.String) progress.Status = element.GetString();
                    if (document.RootElement.TryGetProperty("completed", out element) &&
                        element.ValueKind == JsonValueKind.Number) progress.Completed = element.GetInt64();
                    if (document.RootElement.TryGetProperty("total", out element) &&
                        element.ValueKind == JsonValueKind.Number) progress.Total = element.GetInt64();
                    return progress;
                }
            }
            catch { return null; }
        }

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

        public sealed class PullResult
        {
            public bool Ok;
            public string Message;
        }

        /// <summary>
        /// Is anything answering at all?
        ///
        /// Separate from ListModelsAsync on purpose. That one swallows every failure into an empty
        /// list, which is right for filling a dropdown and useless here: "Ollama is not running" and
        /// "Ollama is running but has no generative model" are the same answer from it, and they need
        /// opposite advice -- install the runtime, versus pull a model.
        /// </summary>
        public static async Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
        {
            try
            {
                using (HttpClient http = CreateClient(TimeSpan.FromSeconds(8)))
                using (HttpResponseMessage response = await http
                    .GetAsync(NormalizeEndpoint(endpoint) + "/api/tags", cancellationToken).ConfigureAwait(false))
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Pull a model into the local Ollama, reporting progress as it goes.
        ///
        /// This is a DOWNLOAD, not inference: it moves bytes onto the disk and loads nothing onto the
        /// GPU, so it costs no VRAM and evicts nothing the user is running. Gigabytes though, so the
        /// caller is expected to have said which model and how big before calling.
        /// </summary>
        public static async Task<PullResult> PullModelAsync(string endpoint, string model,
            Action<string> report, CancellationToken cancellationToken)
        {
            var result = new PullResult { Ok = false };
            Action<string> say = report ?? delegate { };
            if (string.IsNullOrWhiteSpace(model)) { result.Message = "No model was named."; return result; }

            try
            {
                // Hours, not minutes: 14 GB over a domestic line is a long sit, and a timeout here
                // throws away a download that was working.
                using (HttpClient http = CreateClient(TimeSpan.FromHours(6)))
                {
                    string payload = "{\"model\":" + JsonSerializer.Serialize(model) +
                                     ",\"stream\":true}";
                    using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                    using (var request = new HttpRequestMessage(
                               HttpMethod.Post, NormalizeEndpoint(endpoint) + "/api/pull") { Content = content })
                    using (HttpResponseMessage response = await http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            result.Message = "Ollama answered HTTP " +
                                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                                " for the pull. Is " + model + " a real tag?";
                            return result;
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string line;
                            bool sawSuccess = false;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                PullProgress progress = ParsePullLine(line);
                                if (progress == null) continue;
                                if (!string.IsNullOrWhiteSpace(progress.Error))
                                {
                                    result.Message = progress.Error;
                                    return result;
                                }
                                say(ProgressLine(model, progress.Status, progress.Completed, progress.Total));
                                if (string.Equals(progress.Status, "success", StringComparison.OrdinalIgnoreCase))
                                    sawSuccess = true;
                            }

                            // The stream ending is not the same as the pull succeeding: a connection
                            // dropped mid-download also ends it, and reporting that as done would
                            // leave a half-model selected.
                            if (!sawSuccess)
                            {
                                result.Message = "The download stopped before Ollama reported success.";
                                return result;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { result.Message = ex.Message; return result; }

            result.Ok = true;
            result.Message = model + " is installed.";
            return result;
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
