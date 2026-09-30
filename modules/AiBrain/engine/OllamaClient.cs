using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// <see cref="ICompanionBrainBackend"/> over Ollama's native REST API (<c>POST /api/chat</c>).
    /// Non-streaming; the request JSON is built by hand so we control the vision "images" array.
    /// A single <see cref="HttpClient"/> is reused for the client's lifetime.
    /// </summary>
    internal sealed class OllamaClient : ICompanionBrainBackend, IModelLister, IModelListingStatus
    {
        private static readonly TimeSpan DefaultStartupDeadline =
            TimeSpan.FromSeconds(20);
        private static readonly TimeSpan DefaultProbeDeadline =
            TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DefaultPollInterval =
            TimeSpan.FromMilliseconds(500);

        private readonly string _endpoint;
        private readonly string _exePath;
        private readonly HttpClient _http;
        private readonly TimeSpan _deadline;
        private readonly TimeSpan _startupDeadline;
        private readonly TimeSpan _probeDeadline;
        private readonly TimeSpan _pollInterval;
        private readonly Func<CancellationToken, bool> _serverStarter;
        /// <summary>The bound on the PUBLIC reachability probe (F105); the startup poll keeps its own, shorter one.</summary>
        private readonly TimeSpan _availabilityDeadline;
        /// <summary>The bound on GET /api/tags read as a listing, shorter than the chat deadline it used to borrow (R-014).</summary>
        private readonly TimeSpan _listingDeadline;

        public OllamaClient(string endpoint, TimeSpan timeout, string exePath)
        {
            _endpoint = AiEndpointPolicy.NormalizeOrThrow(
                string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint,
                "endpoint");
            _exePath = exePath;
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _startupDeadline = DefaultStartupDeadline;
            _probeDeadline = DefaultProbeDeadline;
            _pollInterval = DefaultPollInterval;
            _availabilityDeadline = AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.AvailabilityProbeDeadline);
            _listingDeadline = AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.ListingDeadline);
            _http = new HttpClient(AiEndpointPolicy.CreateNoRedirectHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _serverStarter = TryStartServer;
        }

        internal OllamaClient(
            string endpoint,
            TimeSpan timeout,
            string exePath,
            HttpMessageHandler handler,
            TimeSpan startupDeadline,
            TimeSpan probeDeadline,
            TimeSpan pollInterval,
            Func<CancellationToken, bool> serverStarter,
            TimeSpan? listingDeadline = null)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (serverStarter == null)
                throw new ArgumentNullException("serverStarter");

            _endpoint = AiEndpointPolicy.NormalizeOrThrow(
                string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint,
                "endpoint");
            _exePath = exePath;
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _startupDeadline = AiEndpointPolicy.ValidateDeadline(
                startupDeadline,
                "startupDeadline");
            _probeDeadline = AiEndpointPolicy.ValidateDeadline(
                probeDeadline,
                "probeDeadline");
            _pollInterval = AiEndpointPolicy.ValidateDeadline(
                pollInterval,
                "pollInterval");
            // One knob bounds every probe under test, the startup poll and the public availability probe alike.
            _availabilityDeadline = _probeDeadline;
            _listingDeadline = AiEndpointPolicy.Shorter(
                _deadline,
                listingDeadline.HasValue
                    ? AiEndpointPolicy.ValidateDeadline(listingDeadline.Value, "listingDeadline")
                    : AiEndpointPolicy.ListingDeadline);
            _http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _serverStarter = serverStarter;
        }

        /// <summary>The bound the PUBLIC constructor wired for the reachability probe, for the self-test: the F105
        /// check builds its client through the diagnostic constructor, whose bound is a different assignment, so
        /// the shipped wiring (the shorter of the chat deadline and the 10 s probe deadline) was asserted by nothing
        /// and a regression to the chat deadline left every check green (R-023).</summary>
        internal TimeSpan AvailabilityDeadlineForDiagnostics { get { return _availabilityDeadline; } }

        /// <summary>Reachability, bounded by the probe deadline rather than the chat deadline it used to borrow:
        /// a refused loopback connection burns its whole deadline before failing (measured 2026-09-27), so this
        /// probe ran before every ask with a two-minute worst case (F105).</summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            return await IsAvailableAsync(_availabilityDeadline, ct).ConfigureAwait(false);
        }

        private async Task<bool> IsAvailableAsync(
            TimeSpan deadline,
            CancellationToken ct)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, _endpoint + "/api/tags"))
                {
                    return await AiEndpointPolicy.SendAndCheckSuccessAsync(
                        _http,
                        request,
                        deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// List models currently installed on this server (<c>GET /api/tags</c> — the same endpoint
        /// <see cref="IsAvailableAsync"/> already probes, but reading the body this time). Vision capability
        /// is set from the server's own <c>"capabilities"</c> array when the response includes one (a real
        /// signal, present on current Ollama servers); left null (unknown) on an older server that omits it,
        /// so the caller falls back to a name heuristic. Size is the response's own <c>"size"</c> field (the
        /// on-disk/weight footprint in bytes) when present. Never throws; an unreachable server or a
        /// malformed response yields an empty list, and <see cref="LastListingFailure"/> says why (RA-059).
        /// </summary>
        public async Task<IReadOnlyList<ModelListing>> ListModelsAsync(CancellationToken ct)
        {
            var result = new List<ModelListing>();
            LastListingFailure = null;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, _endpoint + "/api/tags"))
                {
                    // Under the LISTING bound, not the chat deadline (R-014; see OpenAiCompatBackend.ListModelsAsync).
                    string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                        _http,
                        request,
                        _listingDeadline,
                        ct,
                        AiEndpointPolicy.MaximumListingResponseBytes).ConfigureAwait(false);
                    JsonNode obj = JsonNode.Parse(json);
                    JsonArray models = obj?["models"] as JsonArray;
                    if (models != null)
                        foreach (JsonNode entry in models)
                        {
                            if (entry == null) continue;
                            string name = JsonRead.Str(entry["name"]);
                            if (name.Length == 0) continue;
                            result.Add(new ModelListing(
                                name,
                                VisionFromCapabilities(entry["capabilities"]),
                                JsonRead.Int64OrNull(entry["size"])));
                        }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LastListingFailure = ex; }
            return result;
        }

        /// <summary>Why the last listing came back empty, or null when it answered (RA-059, <see cref="IModelListingStatus"/>).</summary>
        public Exception LastListingFailure { get; private set; }

        // The response's "capabilities" array (e.g. ["completion","vision"]) when present -> a real true/
        // false signal; absent/malformed -> null (unknown, caller applies the name heuristic instead).
        private static bool? VisionFromCapabilities(JsonNode capabilitiesNode)
        {
            JsonArray capabilities = capabilitiesNode as JsonArray;
            if (capabilities == null) return null;
            foreach (JsonNode capability in capabilities)
                if (string.Equals(JsonRead.Str(capability), "vision", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public async Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            using (var startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                startupCancellation.CancelAfter(_startupDeadline);
                CancellationToken startupToken = startupCancellation.Token;
                try
                {
                    if (await IsAvailableAsync(
                        _probeDeadline,
                        startupToken).ConfigureAwait(false))
                        return true;

                    startupToken.ThrowIfCancellationRequested();
                    if (!AiEndpointPolicy.IsLoopbackEndpoint(_endpoint))
                        return false;

                    bool started;
                    try
                    {
                        started = await RunServerStarterAsync(
                            _serverStarter,
                            startupToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        startupToken.ThrowIfCancellationRequested();
                        return false;
                    }

                    startupToken.ThrowIfCancellationRequested();
                    if (!started) return false;

                    while (true)
                    {
                        await Task.Delay(
                            _pollInterval,
                            startupToken).ConfigureAwait(false);
                        if (await IsAvailableAsync(
                            _probeDeadline,
                            startupToken).ConfigureAwait(false))
                            return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    if (startupCancellation.IsCancellationRequested)
                        return false;
                    throw;
                }
            }
        }

        private static async Task<bool> RunServerStarterAsync(
            Func<CancellationToken, bool> serverStarter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<bool> starterTask = Task.Run(
                delegate { return serverStarter(cancellationToken); },
                cancellationToken);
            if (starterTask.IsCompleted)
                return await starterTask.ConfigureAwait(false);

            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                delegate { cancellation.TrySetResult(true); }))
            {
                Task completed = await Task.WhenAny(
                    starterTask,
                    cancellation.Task).ConfigureAwait(false);
                if (completed == starterTask)
                    return await starterTask.ConfigureAwait(false);

                // The engine's one observe-fault helper (F064); a byte-equivalent private copy sat here from 1.0.0
                // until 2026-09-30 (RA-086).
                AiEndpointPolicy.ObserveTaskFailure(starterTask);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private bool TryStartServer(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string executable = ResolveOllamaExe();
                if (string.IsNullOrEmpty(executable)) return false;
                cancellationToken.ThrowIfCancellationRequested();
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable)
                };
                cancellationToken.ThrowIfCancellationRequested();
                using (Process started = Process.Start(psi))
                {
                    // Dispose only our process handle. The server itself intentionally remains alive.
                    return started != null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;   // exe not found / can't launch
            }
        }

        public async Task WarmUpAsync(string model, CancellationToken ct)
        {
            string normalizedModel;
            if (!AiModelPolicy.TryNormalize(model, out normalizedModel)) return;
            try
            {
                // No "prompt" -> Ollama just loads the model into memory (done_reason: "load").
                // The SAME keep_alive the chat requests carry, so the warm-up cannot disagree with the residency
                // it serves. The only production caller is the "keep" residency (AiBrain.PrepareAsync gates on
                // WarmUpDesired), whose contract is "hold it for the session"; a literal "10m" here meant the launch
                // warm-up expired ten idle minutes later and the first remark paid the cold load the setting exists
                // to avoid, while the pane said the model stays loaded (F106). The literal survives only for a
                // client nobody gave a policy to.
                JsonObject payload = new JsonObject
                {
                    ["model"] = normalizedModel,
                    ["stream"] = false,
                    ["keep_alive"] = KeepAliveSeconds.HasValue ? (JsonNode)KeepAliveSeconds.Value : (JsonNode)"10m"
                };
                using (StringContent content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/generate"))
                {
                    request.Content = content;
                    await AiEndpointPolicy.SendAndEnsureSuccessAsync(
                        _http,
                        request,
                        _deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        /// <summary>
        /// The <c>keep_alive</c> to send with a chat request: null (the default) omits it and lets the server
        /// decide, 0 unloads as soon as the response is done, and a negative value keeps it resident.
        ///
        /// A property rather than a ChatAsync parameter so the backend interface stays as it is -- every other
        /// caller of ChatAsync is unaffected, and the policy lives in one place.
        /// </summary>
        public int? KeepAliveSeconds { get; set; }

        /// <summary>One model currently resident, as reported by <c>GET /api/ps</c>.</summary>
        public sealed class RunningModel
        {
            public string Name;
            public long VramBytes;
            public DateTimeOffset? ExpiresAt;
        }

        /// <summary>
        /// Models resident RIGHT NOW (<c>GET /api/ps</c>), with their VRAM and eviction time.
        ///
        /// This exists so the options pane can state what is actually true on this machine instead of printing
        /// a documented default. The documented default is 5 minutes, but OLLAMA_KEEP_ALIVE overrides it
        /// server-wide, so claiming it in the UI would be wrong on exactly the machines that had tuned it.
        /// Best-effort: an unreachable or older server answers as an empty list, never an exception.
        /// </summary>
        public async Task<IReadOnlyList<RunningModel>> RunningModelsAsync(CancellationToken ct)
        {
            var result = new List<RunningModel>();
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, _endpoint + "/api/ps"))
                {
                    string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                        _http, request, _deadline, ct).ConfigureAwait(false);
                    JsonNode root = JsonNode.Parse(json);
                    JsonArray models = root != null ? root["models"] as JsonArray : null;
                    if (models == null) return result;
                    foreach (JsonNode m in models)
                    {
                        if (m == null) continue;
                        string modelName = JsonRead.Str(m["name"]);
                        if (modelName.Length == 0) modelName = JsonRead.Str(m["model"]);
                        var entry = new RunningModel { Name = modelName };
                        entry.VramBytes = JsonRead.Int64OrNull(m["size_vram"]) ?? 0;
                        string expires = JsonRead.Str(m["expires_at"]);
                        DateTimeOffset when;
                        if (!string.IsNullOrEmpty(expires) &&
                            DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when))
                            entry.ExpiresAt = when;
                        if (entry.Name.Length > 0) result.Add(entry);
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Evict a model from memory/VRAM immediately (keep_alive: 0). Best-effort; never throws.</summary>
        public async Task UnloadAsync(string model, CancellationToken ct)
        {
            string normalizedModel;
            if (!AiModelPolicy.TryNormalize(model, out normalizedModel)) return;
            try
            {
                JsonObject payload = new JsonObject
                {
                    ["model"] = normalizedModel,
                    ["keep_alive"] = 0
                };
                using (StringContent content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/generate"))
                {
                    request.Content = content;
                    await AiEndpointPolicy.SendAndEnsureSuccessAsync(
                        _http,
                        request,
                        _deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        private string ResolveOllamaExe()
        {
            // The ladder itself lives in AiExecutablePolicy.Resolve, shared with the tesseract resolver (F087);
            // only the literals are this executable's.
            return AiExecutablePolicy.Resolve(
                _exePath,
                new[]
                {
                    Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Ollama\ollama.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Ollama\ollama.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\Ollama\ollama.exe")
                },
                "ollama.exe");
        }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            string normalizedModel = AiModelPolicy.NormalizeOrThrow(model, "model");
            JsonArray msgArray = new JsonArray();
            foreach (ChatMessage m in messages)
            {
                JsonObject jm = new JsonObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? ""
                };
                if (m.ImagesBase64 != null && m.ImagesBase64.Length > 0)
                {
                    JsonArray images = new JsonArray();
                    foreach (string b64 in m.ImagesBase64)
                        images.Add((JsonNode)b64);
                    jm["images"] = images;
                }
                msgArray.Add(jm);
            }

            JsonObject payload = new JsonObject
            {
                ["model"] = normalizedModel,
                ["stream"] = false,
                ["messages"] = msgArray,
                // A little extra sampling variety so short in-character remarks don't converge on one line.
                ["options"] = new JsonObject { ["temperature"] = 0.9 }
            };
            if (jsonFormat) payload["format"] = "json";
            // How long the model may sit in VRAM after answering. Sent as ONE FIELD on the request rather than
            // scheduled from a timer: Ollama evicts N seconds after the response with no further traffic, and
            // it still evicts if this app exits in the meantime. A timer would need to race a second quip
            // arriving inside the window, and would leave the model resident if we died first.
            //
            // Null = omit the field and let the server's own policy apply (documented as 5 minutes, unless the
            // machine sets OLLAMA_KEEP_ALIVE). 0 = unload as soon as the response is done. NEGATIVE = stay
            // resident indefinitely. Three distinct meanings, which is why this is nullable rather than an int
            // with a sentinel value: -1 is a real instruction to Ollama, not an absence.
            if (KeepAliveSeconds.HasValue) payload["keep_alive"] = KeepAliveSeconds.Value;

            using (StringContent content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"))
            using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/chat"))
            {
                request.Content = content;
                string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                    _http,
                    request,
                    _deadline,
                    ct).ConfigureAwait(false);
                JsonNode obj = JsonNode.Parse(json);
                JsonObject message = obj?["message"] as JsonObject;
                if (message != null)
                    return JsonRead.Str(message["content"]);
                return "";
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
