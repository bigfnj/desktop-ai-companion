using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using AgentBroker.Worker.Security;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class CodexQueueChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratchRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch"));
        string scratch = Path.Combine(scratchRoot, "codex-queue-" + Guid.NewGuid().ToString("N"));
        string keyName;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store")))
            keyName = BrokerIdentity.KeyNameFor(scratch, seed.BrokerId);
        var engine = new QueueNativeOwner();
        try
        {
            var options = new WorkerOptions(scratch, "Synthetic Codex queue", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false };
            await using (var worker = await WorkerApplication.CreateAsync(options,
                [new CodexNativeAdapter(engine, enableDelivery: true)]))
            {
                await worker.StartAsync();
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri(worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp"),
                    Timeout = TimeSpan.FromSeconds(5)
                };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
                var initialized = await RpcAsync(client, "initialize", new
                { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-codex-queue", version = "1" } });
                check(initialized.GetProperty("result").TryGetProperty("protocolVersion", out _),
                    "Unknown-native queue fixture initializes the real authenticated MCP HTTP transport");
                BrokerSession? recipient = null;
                await UntilAsync(async () =>
                {
                    recipient = (await ToolAsync<BrokerSession[]>(client, "list_sessions", new { })).SingleOrDefault(session =>
                        session.Address.SessionId == "codex:" + engine.Thread.ToString("D"));
                    return recipient is not null;
                });
                check(recipient is { Adapter: "codex-native", Activity: SessionActivity.Unknown } &&
                    recipient.Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.NativeIdleGuard),
                    "Production Codex adapter publishes fresh Unknown activity with its verified native idle guard");
                var sender = await ToolAsync<BrokerSession>(client, "register_agent", new
                { clientId = Guid.NewGuid(), incarnation = Guid.NewGuid(), label = "Synthetic Codex queue sender", isPrivate = true });
                var now = DateTimeOffset.UtcNow;
                var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient!.Address,
                    "Synthetic queue request, never sent to a real native client.", now, now.AddMinutes(1));
                var accepted = await ToolAsync<MessageReceipt>(client, "send_message", new { message });
                check(accepted.Message == message && accepted.Status == MessageStatus.Accepted,
                    "MCP enqueues the immutable message for an Unknown session through the real Codex adapter boundary");
                await UntilAsync(async () => engine.AttemptsFor(message.Id).Length == 1 &&
                    (await ReceiptAsync(client, message.Id)).Status != MessageStatus.Submitting);
                var deferred = await ReceiptAsync(client, message.Id);
                check(deferred.Status == MessageStatus.Accepted && deferred.Reason == "native_context_busy" &&
                    deferred.Attempts == 0 && engine.AdmittedFor(message.Id) == 0,
                    "Typed pre-input native busy deferral returns the durable message to Accepted without consuming admission");
                engine.MarkIdle();
                await UntilAsync(async () => (await ReceiptAsync(client, message.Id)).Status == MessageStatus.Submitted);
                var submitted = await ReceiptAsync(client, message.Id);
                var attempts = engine.AttemptsFor(message.Id);
                check(attempts.Length == 2 && attempts[0].Outcome == DesktopSubmissionOutcome.DeferredNotSubmitted &&
                    attempts[1].Outcome == DesktopSubmissionOutcome.Accepted &&
                    Stopwatch.GetElapsedTime(attempts[0].Tick, attempts[1].Tick) >= TimeSpan.FromSeconds(4.5),
                    "Unknown native activity retries only its recognized unsubmitted busy outcome after the backoff");
                check(attempts.All(attempt => attempt.Id == message.Id && attempt.Text == attempts[0].Text) &&
                    engine.AdmittedFor(message.Id) == 1 && submitted.Message == message && submitted.Attempts == 1,
                    "Busy-to-idle admission preserves the original message ID, complete envelope and exact recipient once");
                check(attempts[1].Text.StartsWith("AgentBroker external agent message.", StringComparison.Ordinal) &&
                    attempts[1].Text.Contains(message.Id.ToString("D"), StringComparison.Ordinal) &&
                    submitted.Reason == "native_admission_unconfirmed",
                    "Production peer formatting and native admission remain distinct from a recipient acknowledgement");

                var uncertainMessage = message with { Id = Guid.NewGuid(), Text = "Synthetic outcome-unknown request." };
                engine.MakeUncertain(uncertainMessage.Id);
                await ToolAsync<MessageReceipt>(client, "send_message", new { message = uncertainMessage });
                await UntilAsync(async () => (await ReceiptAsync(client, uncertainMessage.Id)).Status == MessageStatus.Uncertain);
                check(engine.AttemptsFor(uncertainMessage.Id).Length == 1,
                    "Native ambiguity becomes an Uncertain receipt after one attempt");
                var laterMessage = message with { Id = Guid.NewGuid(), Text = "Synthetic later request." };
                await ToolAsync<MessageReceipt>(client, "send_message", new { message = laterMessage });
                await UntilAsync(async () => (await ReceiptAsync(client, laterMessage.Id)).Status == MessageStatus.Submitted);
                check(engine.AdmittedFor(laterMessage.Id) == 1,
                    "An uncertain earlier native request does not prevent a later distinct queue message");
                var duplicateSubmitted = await ToolAsync<MessageReceipt>(client, "send_message", new { message });
                var duplicateUncertain = await ToolAsync<MessageReceipt>(client, "send_message", new { message = uncertainMessage });
                check(duplicateSubmitted.Status == MessageStatus.Submitted && duplicateUncertain.Status == MessageStatus.Uncertain,
                    "Identical MCP retries return existing Submitted and Uncertain receipts");
                await Task.Delay(TimeSpan.FromSeconds(5.7));
                check(engine.AttemptsFor(message.Id).Length == 2 && engine.AttemptsFor(uncertainMessage.Id).Length == 1 &&
                    engine.AttemptsFor(laterMessage.Id).Length == 1,
                    "Submitted and Uncertain native outcomes remain unreplayed beyond the busy retry interval");
                check((await ReceiptAsync(client, message.Id)).Status == MessageStatus.Submitted &&
                    (await ReceiptAsync(client, uncertainMessage.Id)).Status == MessageStatus.Uncertain,
                    "No test observer fabricates a recipient Delivered acknowledgement");
            }
            check(engine.Disposed, "Worker shutdown disposes the synthetic native provider without starting any vendor engine");
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
            string allowed = scratchRoot + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(scratch).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic Codex queue fixture escaped its scratch root.");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static Task<MessageReceipt> ReceiptAsync(HttpClient client, Guid messageId)
        => ToolAsync<MessageReceipt>(client, "get_message_receipt", new { messageId });

    private static async Task<T> ToolAsync<T>(HttpClient client, string name, object arguments)
    {
        var response = await RpcAsync(client, "tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var error) && error.GetBoolean()) throw new IOException("Synthetic MCP tool failed: " + name);
        string text = result.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString() ?? throw new IOException("Synthetic MCP content was empty.");
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new IOException("Synthetic MCP payload was null.");
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object parameters)
    {
        int id = Interlocked.Increment(ref nextRpcId);
        string call = method == "tools/call" && parameters.GetType().GetProperty("name")?.GetValue(parameters) is string tool ? tool : method;
        var watch = Stopwatch.StartNew();
        HttpResponseMessage posted;
        // N-bus-03 (lane broker-next, test only): one run in 19 timed out here at get_message_receipt. The 5-second
        // timeout is unchanged; a timeout now says which call, how long it ran, the thread pool's state and the last RPC
        // timings, so the next one can be attributed instead of guessed at.
        try { posted = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id, method, @params = parameters }); }
        catch (TaskCanceledException timeout) when (!timeout.CancellationToken.IsCancellationRequested || timeout.InnerException is TimeoutException)
        {
            ThreadPool.GetAvailableThreads(out int workers, out int ports);
            ThreadPool.GetMaxThreads(out int maxWorkers, out int maxPorts);
            string recent;
            lock (RecentRpcs) recent = string.Join(", ", RecentRpcs);
            throw new TimeoutException($"N-bus-03 capture: {call} (RPC id {id}) timed out after {watch.ElapsedMilliseconds} ms; " +
                $"thread pool busy {maxWorkers - workers} of {maxWorkers} workers, {maxPorts - ports} IO, {ThreadPool.ThreadCount} threads, " +
                $"{ThreadPool.PendingWorkItemCount} queued; last RPCs: {recent}", timeout);
        }
        using var response = posted;
        lock (RecentRpcs) { RecentRpcs.Enqueue(call + " " + watch.ElapsedMilliseconds + " ms"); while (RecentRpcs.Count > 6) RecentRpcs.Dequeue(); }
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Join("\n", text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.GetProperty("id").GetInt32() != id || root.TryGetProperty("error", out _))
            throw new IOException("Synthetic MCP request failed or changed correlation.");
        return root.Clone();
    }
    private static int nextRpcId;
    private static readonly Queue<string> RecentRpcs = new();

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Synthetic Codex queue did not settle.");
            await Task.Delay(30);
        }
    }

    private sealed record NativeAttempt(Guid Id, string Text, long Tick, DesktopSubmissionOutcome Outcome);
    private sealed class QueueNativeOwner : ICodexNativeProvider
    {
        internal Guid Thread { get; } = Guid.NewGuid();
        private readonly Guid owner = Guid.NewGuid(), connection = Guid.NewGuid();
        private readonly ConcurrentDictionary<Guid, byte> uncertain = new();
        private readonly ConcurrentQueue<NativeAttempt> attempts = new();
        private int busy = 1;
        internal bool Disposed { get; private set; }
        public bool NativeIdleGuard => !Disposed;
        internal void MarkIdle() => Volatile.Write(ref busy, 0);
        internal void MakeUncertain(Guid id) => uncertain.TryAdd(id, 0);
        internal NativeAttempt[] AttemptsFor(Guid id) => attempts.Where(attempt => attempt.Id == id).ToArray();
        internal int AdmittedFor(Guid id) => attempts.Count(attempt => attempt.Id == id && attempt.Outcome == DesktopSubmissionOutcome.Accepted);
        private DesktopOwnerBinding Binding => new(Thread, owner, connection, 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned };
        public bool IsDeliveryAuthorized(DesktopOwnerBinding binding) => !Disposed && binding == Binding;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([new(Thread, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)]);
        }
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<DesktopOwnerBinding?>(conversationId == Thread ? Binding : null);
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsDeliveryAuthorized(binding)) throw new IOException("Synthetic native owner changed.");
            var outcome = Volatile.Read(ref busy) != 0 ? DesktopSubmissionOutcome.DeferredNotSubmitted
                : uncertain.ContainsKey(messageId) ? DesktopSubmissionOutcome.Uncertain : DesktopSubmissionOutcome.Accepted;
            attempts.Enqueue(new(messageId, text, Stopwatch.GetTimestamp(), outcome));
            return Task.FromResult(new DesktopSubmissionResult(outcome,
                ErrorCode: outcome == DesktopSubmissionOutcome.DeferredNotSubmitted ? "native_context_busy" : null));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
