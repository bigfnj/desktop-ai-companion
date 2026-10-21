using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker.CodexRelay;

/// <summary>One GUI transport and exactly one original engine. Never creates or resumes a thread itself.</summary>
public sealed class RelaySession : IAsyncDisposable
{
    private sealed record GuiRequest(string Method, string? ThreadId);
    private sealed class Injection
    {
        internal required string ThreadId;
        internal readonly TaskCompletionSource<AdmissionResult> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record AdmissionResult(bool Ok, string? TurnId, string? Reason);
    private sealed record Delivery(string ThreadId, string TextHash, string RequestId, Injection Pending);

    private readonly RelayOptions _options;
    private readonly object _state = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<string, GuiRequest> _guiRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Injection> _injections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _threads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenGuiIds = new(StringComparer.Ordinal);
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private long _sequence;
    private bool _verified;
    private bool _stopped;
    private bool _ran;
    private bool _inputClosed;
    private string? _disabledReason;
    private Process? _engine;
    private CancellationTokenSource? _lifetime;
    private Task? _pipeTask;
    private Task?[] _ownedTasks = [];
    private Task<int>? _runTask;
    private int _disposed;
    private int _resourcesDisposed;

    public RelaySession(RelayOptions options) => _options = options;

    public Task<int> RunAsync(Stream guiInput, Stream guiOutput, Stream engineError, CancellationToken cancellation)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_ran) throw new InvalidOperationException("A relay can run once.");
            _ran = true;
            return _runTask = RunCoreAsync(guiInput, guiOutput, engineError, cancellation);
        }
    }

    private async Task<int> RunCoreAsync(Stream guiInput, Stream guiOutput, Stream engineError, CancellationToken cancellation)
    {
        using var verifiedFile = _options.VerifyEngine();
        var info = new ProcessStartInfo
        {
            FileName = _options.EnginePath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        foreach (string argument in _options.EngineArguments) info.ArgumentList.Add(argument);
        // Environment is inherited without adding, removing, or rewriting any variables.
        _engine = Process.Start(info) ?? throw new IOException("Engine did not start.");
        verifiedFile.Dispose();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancellationToken stop = _lifetime.Token;
        _pipeTask = ServePipeAsync(stop);
        Task input = PumpGuiAsync(guiInput, stop);
        Task output = PumpEngineAsync(guiOutput, stop);
        Task errors = _engine.StandardError.BaseStream.CopyToAsync(engineError, stop);
        Task exited = _engine.WaitForExitAsync(stop);
        Task errorFailure = WatchBackgroundFailureAsync(errors, stop, mustRemainRunning: false);
        Task controlFailure = WatchBackgroundFailureAsync(_pipeTask, stop, mustRemainRunning: true);
        _ownedTasks = [input, output, errors, exited, _pipeTask, errorFailure, controlFailure];
        try
        {
            Task first = await Task.WhenAny(input, output, exited, errorFailure, controlFailure).ConfigureAwait(false);
            if (first.IsFaulted) await first.ConfigureAwait(false);
            if (first == input) _engine.StandardInput.Close();
            if (first != exited)
            {
                try { await exited.WaitAsync(_options.ExitTimeout, stop).ConfigureAwait(false); }
                catch (TimeoutException) { KillOwnedEngine(); }
            }
            try
            {
                await _engine.WaitForExitAsync(CancellationToken.None).WaitAsync(_options.ExitTimeout).ConfigureAwait(false);
                await Task.WhenAll(output, errors).WaitAsync(_options.ExitTimeout, stop).ConfigureAwait(false);
            }
            catch (TimeoutException) { throw new IOException("Relay shutdown drain timed out."); }
            stop.ThrowIfCancellationRequested();
            return _engine.ExitCode;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            KillOwnedEngine();
            return 130;
        }
        finally
        {
            StopState();
            _lifetime.Cancel();
            KillOwnedEngine();
            await ObserveAsync(_ownedTasks).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0) DisposeOwnedResources();
        }
    }

    private static async Task WatchBackgroundFailureAsync(Task task, CancellationToken stop, bool mustRemainRunning)
    {
        await task.ConfigureAwait(false); // Preserve an early stderr/control exception for the lifetime supervisor.
        if (mustRemainRunning && !stop.IsCancellationRequested)
            throw new IOException("Relay control listener stopped unexpectedly.");
        // Stderr EOF alone is valid while stdout/the engine remain alive. Only its faults stop the relay.
        await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
    }

    private async Task PumpGuiAsync(Stream input, CancellationToken stop)
    {
        await foreach (byte[] frame in JsonlFrames.ReadAsync(input, stop).ConfigureAwait(false))
        {
            await WriteGuiAsync(frame, stop).ConfigureAwait(false);
        }
    }

    private async Task PumpEngineAsync(Stream output, CancellationToken stop)
    {
        await foreach (byte[] frame in JsonlFrames.ReadAsync(_engine!.StandardOutput.BaseStream, stop).ConfigureAwait(false))
        {
            using var json = JsonlFrames.Parse(frame);
            bool own = json != null && ObserveEngine(json.RootElement);
            if (!own)
            {
                await output.WriteAsync(frame, stop).ConfigureAwait(false);
                await output.FlushAsync(stop).ConfigureAwait(false);
            }
        }
    }

    private void ObserveGui(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return;
        string? id = JsonlFrames.Id(message), method = JsonlFrames.String(message, "method");
        if (id == null || method == null) return; // GUI responses to engine approvals pass through untouched.
        lock (_state)
        {
            if (_injections.ContainsKey(id))
            {
                // An adversarial exact ID collision cannot safely be disambiguated. Never misroute its response.
                _disabledReason = "request-id-collision";
                throw new IOException("Ambiguous request ID.");
            }
            if (_seenGuiIds.Count >= 100_000) _disabledReason = "request-history-limit";
            else _seenGuiIds.Add(id);
            string? thread = message.TryGetProperty("params", out var p) ? JsonlFrames.String(p, "threadId") : null;
            if (method is "initialize" or "thread/start" or "thread/resume" or "turn/start")
            {
                if (_guiRequests.Count >= 4096) _disabledReason = "request-history-limit";
                else _guiRequests[id] = new GuiRequest(method, thread);
            }
            if (method == "turn/start" && thread != null && _threads.ContainsKey(thread)) _threads[thread] = "active";
            if (method is "thread/unsubscribe" or "thread/archive" && thread != null) _threads.Remove(thread);
        }
    }

    private bool ObserveEngine(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return false;
        string? id = JsonlFrames.Id(message), method = JsonlFrames.String(message, "method");
        lock (_state)
        {
            if (id != null && method == null && _injections.Remove(id, out var own))
            {
                // Errors never establish idle. A GUI turn may already be active; await its real status.
                bool error = message.TryGetProperty("error", out _);
                string? turnId = message.TryGetProperty("result", out var ownResult)
                    && ownResult.ValueKind == JsonValueKind.Object && ownResult.TryGetProperty("turn", out var turn)
                    ? JsonlFrames.String(turn, "id") : null;
                own.Response.TrySetResult(new AdmissionResult(!error && turnId != null, turnId,
                    error ? "engine-refused" : turnId == null ? "unexpected-engine-response" : null));
                return true;
            }
            if (id != null && method == null && _guiRequests.Remove(id, out var gui))
            {
                if (message.TryGetProperty("result", out var result))
                {
                    if (gui.Method == "initialize")
                    {
                        string? userAgent = JsonlFrames.String(result, "userAgent");
                        _verified = VersionFromUserAgent(userAgent) == _options.EngineVersion;
                        if (!_verified) _disabledReason = "engine-version-mismatch";
                    }
                    else if (gui.Method is "thread/start" or "thread/resume") AddThread(result);
                    // thread/read and thread/list describe persisted records, not loaded engine threads.
                }
                else if (gui.Method == "turn/start" && gui.ThreadId != null && _threads.ContainsKey(gui.ThreadId))
                    _threads[gui.ThreadId] = "unknown"; // Require a later authoritative idle status before injection.
            }
            if (method == null || !message.TryGetProperty("params", out var p)) return false;
            if (method == "thread/started") AddThread(p);
            else
            {
                string? thread = JsonlFrames.String(p, "threadId");
                if (thread == null) return false;
                if (method == "thread/closed") _threads.Remove(thread);
                else if (_threads.ContainsKey(thread))
                {
                    if (method == "turn/started") _threads[thread] = "active";
                    else if (method == "turn/completed")
                    {
                        string? ended = p.TryGetProperty("turn", out var turn) ? JsonlFrames.String(turn, "status") : null;
                        _threads[thread] = ended == "completed" ? "idle" : "unknown";
                    }
                    else if (method == "thread/status/changed" && p.TryGetProperty("status", out var status))
                    {
                        string? kind = JsonlFrames.String(status, "type");
                        if (kind == "notLoaded") _threads.Remove(thread);
                        else _threads[thread] = kind is "idle" or "active" ? kind : "unknown";
                    }
                }
            }
            return false;
        }
    }

    private void AddThread(JsonElement parent)
    {
        if (!parent.TryGetProperty("thread", out var thread)) return;
        string? id = JsonlFrames.String(thread, "id");
        if (string.IsNullOrEmpty(id)) return;
        if (!_threads.ContainsKey(id) && _threads.Count >= 4096) { _disabledReason = "thread-history-limit"; return; }
        string? state = thread.TryGetProperty("status", out var status) ? JsonlFrames.String(status, "type") : null;
        _threads[id] = state is "idle" or "active" ? state : "unknown";
    }

    private static string? VersionFromUserAgent(string? value)
    {
        if (value == null) return null;
        int slash = value.IndexOf('/');
        if (slash < 0) return null;
        return value[(slash + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private async Task WriteGuiAsync(byte[] frame, CancellationToken stop)
    {
        await _write.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            if (_inputClosed) throw new IOException("GUI input is closed.");
            using var json = JsonlFrames.Parse(frame);
            if (json != null) ObserveGui(json.RootElement);
            await _engine!.StandardInput.BaseStream.WriteAsync(frame, stop).ConfigureAwait(false);
            await _engine.StandardInput.BaseStream.FlushAsync(stop).ConfigureAwait(false);
            // Never append an injected JSON document to the GUI's final unterminated frame.
            if (frame[^1] != (byte)'\n') _inputClosed = true;
        }
        finally { _write.Release(); }
    }

    private async Task ServePipeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            // This is per-user local IPC, not the LAN listener and not a bearer-token transport.
            await using var pipe = new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
            try
            {
                await foreach (byte[] frame in JsonlFrames.ReadAsync(pipe, stop).ConfigureAwait(false))
                {
                    object response;
                    using var json = JsonlFrames.Parse(frame);
                    if (json == null || json.RootElement.ValueKind != JsonValueKind.Object)
                        response = new { ok = false, reason = "invalid-request" };
                    else response = await HandleControlAsync(json.RootElement, stop).ConfigureAwait(false);
                    await pipe.WriteAsync(JsonlFrames.Encode(response), stop).ConfigureAwait(false);
                    await pipe.FlushAsync(stop).ConfigureAwait(false);
                }
            }
            catch (IOException) when (!stop.IsCancellationRequested) { /* A disconnected client owns no engine lifetime. */ }
        }
    }

    private async Task<object> HandleControlAsync(JsonElement request, CancellationToken stop)
    {
        string? operation = JsonlFrames.String(request, "operation");
        if (operation == "status")
        {
            lock (_state) return new
            {
                ok = true, incarnation = _nonce, verified = _verified && _disabledReason == null,
                reason = _disabledReason,
                threads = _threads.Select(t => new { threadId = t.Key, state = t.Value }).ToArray(),
            };
        }
        if (operation != "send") return new { ok = false, reason = "unsupported-operation" };
        string? threadId = JsonlFrames.String(request, "threadId"), text = JsonlFrames.String(request, "text");
        string? messageId = JsonlFrames.String(request, "messageId");
        if (threadId == null || string.IsNullOrWhiteSpace(text) || text.Length > 65536 || messageId == null
            || messageId.Length is < 1 or > 128
            || messageId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            return new { ok = false, reason = "invalid-request" };
        string? incarnation = JsonlFrames.String(request, "incarnation");
        string textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Delivery delivery;
        bool write = false;
        // Admission and GUI observation share this boundary. A GUI turn already written cannot be
        // overtaken by an injection that reserved idle before waiting for the writer.
        await _write.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                if (_stopped || _inputClosed || !_verified || _disabledReason != null)
                    return new { ok = false, reason = _disabledReason ?? "not-ready" };
                if (incarnation != _nonce) return new { ok = false, reason = "stale-incarnation" };
                if (_deliveries.TryGetValue(messageId, out var prior))
                {
                    if (prior.ThreadId != threadId || prior.TextHash != textHash)
                        return new { ok = false, reason = "message-id-conflict" };
                    delivery = prior;
                }
                else
                {
                    if (!_threads.TryGetValue(threadId, out var state)) return new { ok = false, reason = "thread-not-loaded" };
                    if (state != "idle") return new { ok = false, reason = "thread-not-idle" };
                    if (_injections.Count >= 32) return new { ok = false, reason = "too-many-pending" };
                    if (_deliveries.Count >= 4096) return new { ok = false, reason = "delivery-history-full" };
                    string ownedId;
                    do { ownedId = "agentbroker:" + _nonce + ":" + (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                    while (_seenGuiIds.Contains(JsonSerializer.Serialize(ownedId)));
                    var pending = new Injection { ThreadId = threadId };
                    delivery = new Delivery(threadId, textHash, ownedId, pending);
                    _injections.Add(JsonSerializer.Serialize(ownedId), pending);
                    _deliveries.Add(messageId, delivery);
                    _threads[threadId] = "active";
                    write = true;
                }
            }
            if (write)
            {
                byte[] frame = JsonlFrames.Encode(new
                {
                    id = delivery.RequestId, method = "turn/start",
                    @params = new { threadId, input = new[] { new { type = "text", text } } },
                });
                await _engine!.StandardInput.BaseStream.WriteAsync(frame, stop).ConfigureAwait(false);
                await _engine.StandardInput.BaseStream.FlushAsync(stop).ConfigureAwait(false);
            }
        }
        finally { _write.Release(); }
        try
        {
            AdmissionResult response = await delivery.Pending.Response.Task.WaitAsync(_options.ResponseTimeout, stop).ConfigureAwait(false);
            return new { ok = response.Ok, requestId = delivery.RequestId, turnId = response.TurnId, reason = response.Reason };
        }
        catch (TimeoutException)
        {
            // Keep ownership of the ID so its late response never leaks into the GUI's request map.
            return new { ok = false, reason = "response-timeout", requestId = delivery.RequestId };
        }
    }

    private void StopState()
    {
        lock (_state)
        {
            _stopped = true;
            _threads.Clear();
            foreach (var pending in _injections.Values) pending.Response.TrySetCanceled();
            _injections.Clear();
        }
    }

    private void KillOwnedEngine()
    {
        try { if (_engine is { HasExited: false }) _engine.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static async Task ObserveAsync(params Task?[] tasks)
    {
        foreach (Task? task in tasks)
        {
            if (task == null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<int>? running;
        lock (_state)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            running = _runTask;
        }
        try { _lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
        KillOwnedEngine();
        if (running is not null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            catch (Exception) { /* RunAsync's caller owns its outcome; disposal must still release resources. */ }
        }
        // If an external stream ignores cancellation past this bound, RunCoreAsync's finally owns
        // deferred resource cleanup. Never destroy process handles while that lifetime still reads them.
        if (running?.IsCompleted != false) DisposeOwnedResources();
        // A canceled caller may still release the managed semaphore after its underlying stream
        // ignores cancellation. Do not dispose it under a running pump; it owns no OS wait handle.
    }

    private void DisposeOwnedResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _engine?.Dispose();
        _lifetime?.Dispose();
    }
}
