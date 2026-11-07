using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class NativeRecoveryToolChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "native-recovery-wire-" + Guid.NewGuid().ToString("N"));
        string? keyName = null;
        try
        {
            var adapter = new NativeAdapter();
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic native recovery", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, [adapter]);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            var httpSeed = Seed(worker, adapter); var stdioSeed = Seed(worker, adapter);
            var impostor = Native(worker.Runtime.Store.BrokerId, Guid.NewGuid(), Guid.NewGuid()); worker.Runtime.Store.Register(impostor);
            await worker.StartAsync();
            for (int attempt = 0; attempt < 100 &&
                (worker.Runtime.Store.NativeRecoveryPendingCount(httpSeed.Current) != 2 || worker.Runtime.Store.NativeRecoveryPendingCount(stdioSeed.Current) != 2); attempt++) await Task.Delay(25);
            bool discovered = worker.Runtime.Store.NativeRecoveryPendingCount(httpSeed.Current) == 2 && worker.Runtime.Store.NativeRecoveryPendingCount(stdioSeed.Current) == 2;
            check(adapter.Discoveries > 0 && discovered, "RECOVERY_NATIVE_DISCOVERY_WITNESS: real runtime discovery verifies exact current native rows");
            if (!discovered) return;
            var peerContext = new DefaultHttpContext(); Guid foreignBroker = Guid.NewGuid(); peerContext.Items["agentbroker.identity"] = foreignBroker;
            var foreignTools = new BrokerTools(worker.Runtime, new HttpContextAccessor { HttpContext = peerContext }); bool localOnly = false;
            try { foreignTools.ReadNativeRecovery(httpSeed.Current with { BrokerId = foreignBroker }); }
            catch (McpException error) { localOnly = error.Message == "Waiting is a local operation."; }
            check(localOnly, "RECOVERY_LOCAL_ONLY: authenticated foreign caller cannot inventory local lineage");
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                BaseAddress = new(worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
            await Rpc(http, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-recovery", version = "1" } });
            await VerifyAsync("HTTP", httpSeed, async (method, parameters) => await Rpc(http, method, parameters));
            await using var bridge = new Bridge(scratch);
            await bridge.RpcAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-recovery-stdio", version = "1" } });
            await VerifyAsync("STDIO", stdioSeed, bridge.RpcAsync);
            check(adapter.Submissions == 0, "RECOVERY_NO_REINJECTION: inventory never submits predecessor prompts through the native adapter");

            async Task VerifyAsync(string transport, RecoverySeed seed, Func<string, object, Task<JsonElement>> rpc)
            {
                Task<JsonElement> Call(string name, object arguments) => rpc("tools/call", new { name, arguments });
                var tools = (await rpc("tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
                check(tools.Length == 16 && new[] { "read_inbox_page", "read_native_recovery" }.All(name => tools.Any(tool =>
                        tool.GetProperty("name").GetString() == name && tool.TryGetProperty("outputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)) &&
                    tools.All(tool => tool.GetProperty("name").GetString() is not ("acknowledge_recovered_message" or "reply_to_recovered_message")),
                    "RECOVERY_READ_ONLY_TOOLS_" + transport + ": structured query tools exist without successor mutation operations");
                var ordinary = await Call("read_inbox_page", new { session = seed.Current });
                check(Body(ordinary).GetProperty("pendingCount").GetInt32() == 0 && StructuredMatches(ordinary) && RecoveryHint(ordinary, seed.Current, 2),
                    "RECOVERY_NO_RETARGET_" + transport + ": prior mail stays outside current inbox and is separately reported");
                var generic = await Call("register_agent", new { clientId = Guid.NewGuid(), incarnation = Guid.NewGuid(), label = seed.Current.SessionId });
                var genericAddress = Body(generic).GetProperty("address").Deserialize<SessionAddress>(Json)!;
                check(!Hint(await Call("read_inbox_page", new { session = genericAddress })).TryGetProperty("recoveryPendingCount", out _) &&
                    IsError(await Call("read_native_recovery", new { session = genericAddress }), "native_recovery_unverified") &&
                    IsError(await Call("read_native_recovery", new { session = impostor.Address }), "native_recovery_unverified") &&
                    IsError(await Call("read_native_recovery", new { session = seed.Old }), "native_recovery_unverified"),
                    "RECOVERY_DISCOVERY_AUTHORITY_" + transport + ": labels, native-looking registrations and predecessors cannot borrow discovery proof");
                var recovered = await Call("read_native_recovery", new { session = seed.Current }); var page = Body(recovered);
                var entries = page.GetProperty("entries").EnumerateArray().ToArray();
                var ids = entries.Select(entry => entry.GetProperty("receipt").GetProperty("message").GetProperty("id").GetGuid()).ToHashSet();
                check(ids.SetEquals([seed.Incoming.Message.Id, seed.Outgoing.Message.Id, seed.Reply.Message.Id]) &&
                    entries.All(entry => entry.GetProperty("previousIncarnation").GetBoolean() && !entry.GetProperty("canAcknowledge").GetBoolean() && !entry.GetProperty("canReply").GetBoolean()) &&
                    page.GetProperty("currentSession").Deserialize<SessionAddress>(Json) == seed.Current && StructuredMatches(recovered),
                    "RECOVERY_INVENTORY_WIRE_" + transport + ": original incoming/outgoing records retain metadata without successor action rights");
                check(TryEntry(page, seed.Incoming.Message.Id, out var incoming) && incoming.GetProperty("contentAvailable").GetBoolean() &&
                    incoming.GetProperty("receipt").GetProperty("message").GetProperty("text").GetString() == seed.Incoming.Message.Text &&
                    incoming.GetProperty("receipt").GetProperty("message").GetProperty("recipient").Deserialize<SessionAddress>(Json) == seed.Old &&
                    TryEntry(page, seed.Reply.Message.Id, out var reply) && reply.GetProperty("receipt").GetProperty("message").GetProperty("replyTo").GetGuid() == seed.Outgoing.Message.Id,
                    "RECOVERY_CONTENT_AND_LINKS_" + transport + ": Unicode submitted content and reply links preserve exact original addresses");
                var first = Body(await Call("read_native_recovery", new { session = seed.Current, limit = 1 }));
                var rest = Body(await Call("read_native_recovery", new { session = seed.Current, cursor = first.GetProperty("nextCursor").GetString() }));
                Guid firstId = first.GetProperty("entries")[0].GetProperty("receipt").GetProperty("message").GetProperty("id").GetGuid();
                check(first.GetProperty("hasMore").GetBoolean() && rest.GetProperty("entries").GetArrayLength() == 2 && !TryEntry(rest, firstId, out _),
                    "RECOVERY_BOUNDED_CURSOR_" + transport + ": continuation reports each original record once");
                check(IsError(await Call("read_native_recovery", new { session = seed.Current, cursor = "malformed" }), "invalid_native_recovery_cursor"),
                    "RECOVERY_CURSOR_ERROR_" + transport + ": malformed cursor fails without success hint");
                check(IsError(await Call("acknowledge_message", new { messageId = seed.Incoming.Message.Id, recipient = seed.Current, outcome = "Delivered" }), "recipient_mismatch") &&
                    IsError(await Call("acknowledge_message", new { messageId = seed.Incoming.Message.Id, recipient = seed.Old, outcome = "Delivered" }), "recipient_unavailable") &&
                    IsError(await Call("reply_to_message", new { originalMessageId = seed.Incoming.Message.Id, messageId = Guid.NewGuid(), text = "No predecessor impersonation" }), "sender_unavailable"),
                    "RECOVERY_STANDARD_ACTIONS_EXACT_" + transport + ": ordinary operations cannot acknowledge or reply across incarnations");
                Guid absentReplyId = Guid.NewGuid();
                check(IsError(await Call("reply_to_message", new { originalMessageId = seed.Outgoing.Message.Id, messageId = absentReplyId, text = "No absent-recipient hold" }), "recipient_unavailable") &&
                    worker.Runtime.Store.FindReceipt(absentReplyId) is null && worker.Runtime.Store.FindReceipt(seed.Incoming.Message.Id)?.Status == MessageStatus.Submitted,
                    "RECOVERY_NO_ABSENT_RETENTION_" + transport + ": no absent reply record or synthetic acknowledgement is published");
                Guid followupId = Guid.NewGuid();
                var followup = Body(await Call("send", new { sender = seed.Current, recipient = seed.Peer, messageId = followupId,
                    text = "Explicit current-incarnation follow-up", conversationId = seed.Incoming.Message.ConversationId }));
                check(followup.GetProperty("message").GetProperty("sender").Deserialize<SessionAddress>(Json) == seed.Current &&
                    (!followup.GetProperty("message").TryGetProperty("replyTo", out var replyTo) || replyTo.ValueKind == JsonValueKind.Null),
                    "RECOVERY_EXPLICIT_FOLLOWUP_" + transport + ": continued work uses a fresh current-address send without predecessor reply");
                var continued = Body(await Call("read_native_recovery", new { session = seed.Current, cursor = page.GetProperty("nextCursor").GetString() }));
                check(continued.GetProperty("entries").GetArrayLength() == 1 && TryEntry(continued, followupId, out _),
                    "RECOVERY_CURSOR_NEW_RECORD_" + transport + ": saved inventory cursor observes the explicit new send");
                check(RecoveryHint(await Call("read_inbox_page", new { session = seed.Current }), seed.Current, 2),
                    "RECOVERY_REPORT_PRESERVES_PENDING_" + transport + ": report-only recovery does not drain predecessor receipt state");
            }
        }
        finally
        {
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
            CleanupScratch(scratch);
        }
    }
    private sealed record RecoverySeed(SessionAddress Current, SessionAddress Old, SessionAddress Peer,
        MessageReceipt Incoming, MessageReceipt Outgoing, MessageReceipt Reply);
    private static RecoverySeed Seed(WorkerApplication worker, NativeAdapter adapter)
    {
        var store = worker.Runtime.Store;
        var old = Native(store.BrokerId, Guid.NewGuid(), Guid.NewGuid()) with { Activity = SessionActivity.Idle };
        var current = old with { Address = old.Address with { Incarnation = Guid.NewGuid() }, Activity = SessionActivity.Busy }; adapter.Add(current);
        var peer = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic recovery peer", false); store.Register(old);
        var incoming = store.Send(peer.Address, old.Address, Guid.NewGuid(), "Synthetic request 猫 and emoji 🐑\nLiteral path D:\\synthetic\\request; quoted empty string: \"\".", null, 0, store.BrokerId);
        if (store.BeginDelivery()?.Message.Id != incoming.Message.Id) throw new InvalidOperationException("Recovery seed did not claim its original incoming record.");
        store.CompleteDelivery(incoming.Message.Id, MessageStatus.Submitted, "synthetic_submitted");
        var outgoing = store.Send(old.Address, peer.Address, Guid.NewGuid(), "Synthetic prior native request.", null, 0, store.BrokerId);
        store.RecordRecipientReceipt(outgoing.Message.Id, peer.Address, store.BrokerId, MessageStatus.Delivered);
        var reply = store.AcceptReply(outgoing.Message.Id, Guid.NewGuid(), "Synthetic answer accepted while the exact requester was live.", store.BrokerId);
        if (store.BeginDelivery()?.Message.Id != reply.Message.Id) throw new InvalidOperationException("Recovery seed did not claim its original reply.");
        store.CompleteDelivery(reply.Message.Id, MessageStatus.Submitted, "synthetic_submitted"); store.Disconnect(old.Address);
        return new(current.Address, old.Address, peer.Address, incoming, outgoing, reply);
    }
    private static BrokerSession Native(Guid brokerId, Guid sessionId, Guid incarnation) => new(new(brokerId, "codex:" + sessionId.ToString("D"), incarnation),
        "Same native label", "codex-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
        SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(1));
    private static bool TryEntry(JsonElement page, Guid id, out JsonElement entry)
    {
        foreach (var item in page.GetProperty("entries").EnumerateArray())
            if (item.GetProperty("receipt").GetProperty("message").GetProperty("id").GetGuid() == id) { entry = item; return true; }
        entry = default; return false;
    }
    private static bool StructuredMatches(JsonElement rpc) => rpc.GetProperty("result").TryGetProperty("structuredContent", out var structured) &&
        JsonNode.DeepEquals(JsonNode.Parse(Body(rpc).GetRawText()), JsonNode.Parse(structured.GetRawText()));
    private static bool RecoveryHint(JsonElement rpc, SessionAddress session, int pending)
    {
        var hint = Hint(rpc);
        return hint.TryGetProperty("recoveryPendingCount", out var count) && count.GetInt32() == pending &&
            hint.GetProperty("recoveryTool").GetString() == "read_native_recovery" && hint.GetProperty("session").Deserialize<SessionAddress>(Json) == session;
    }
    private static bool IsError(JsonElement rpc, string code)
    {
        var result = rpc.GetProperty("result");
        return result.TryGetProperty("isError", out var error) && error.GetBoolean() && result.GetProperty("content")[0].GetProperty("text").GetString()?.Contains(code, StringComparison.Ordinal) == true &&
            !result.GetProperty("content").EnumerateArray().Any(block => block.GetProperty("text").GetString()!.Contains("agentbroker.pending_messages", StringComparison.Ordinal));
    }
    private static JsonElement Body(JsonElement rpc)
    {
        using var json = JsonDocument.Parse(rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!); return json.RootElement.Clone();
    }
    private static JsonElement Hint(JsonElement rpc)
    {
        using var json = JsonDocument.Parse(rpc.GetProperty("result").GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString()!); return json.RootElement.Clone();
    }
    private static void CleanupScratch(string scratch)
    {
        string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(scratch).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Native recovery fixture escaped scratch.");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    }
    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }, Json);
        response.EnsureSuccessStatusCode(); string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Concat(body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var json = JsonDocument.Parse(body); return json.RootElement.Clone();
    }
    private sealed class NativeAdapter : ILocalAgentAdapter
    {
        private readonly List<BrokerSession> rows = [];
        private int submissions, discoveries;
        public string Id => "codex-native";
        internal int Submissions => Volatile.Read(ref submissions);
        internal int Discoveries => Volatile.Read(ref discoveries);
        internal void Add(BrokerSession row) => rows.Add(row);
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Interlocked.Increment(ref discoveries); return Task.FromResult<IReadOnlyList<BrokerSession>>(rows.Select(row => row with { LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1) }).ToArray());
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref submissions); return Task.FromResult(new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_context_busy"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Bridge : IAsyncDisposable
    {
        private readonly Pipe input = new(), output = new();
        private readonly Stream inputStream, outputStream;
        private readonly StreamReader reader;
        private readonly Task<int> task;
        private int id;
        internal Bridge(string directory)
        {
            inputStream = input.Reader.AsStream(); outputStream = output.Writer.AsStream(); reader = new(output.Reader.AsStream(), new UTF8Encoding(false, true));
            task = McpStdioBridge.RunAsync(directory, inputStream, outputStream);
        }
        internal async Task<JsonElement> RpcAsync(string method, object parameters)
        {
            int requestId = ++id;
            await input.Writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = requestId, method, @params = parameters }, Json)); await input.Writer.WriteAsync("\n"u8.ToArray());
            string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("Missing recovery bridge response.");
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("id").GetInt32() != requestId) throw new IOException("Recovery bridge correlation changed."); return json.RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            try { await input.Writer.CompleteAsync(); if (await task.WaitAsync(TimeSpan.FromSeconds(5)) != 0) throw new IOException("Recovery bridge failed to stop."); }
            finally { inputStream.Dispose(); outputStream.Dispose(); reader.Dispose(); }
        }
    }
}
