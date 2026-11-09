using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using ModelContextProtocol.Protocol;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PendingInboxToolChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var fixture = new ToolFixture("pending-inbox-tools");
        try
        {
            var owner = fixture.Register("Synthetic wait owner");
            using var cancel = new CancellationTokenSource();
            var waiting = fixture.Tools.WaitForMessages(owner.Address, cancel.Token, 20);
            check(fixture.Tools.ReadInboxPage(owner.Address).PendingCount == 0 && !waiting.IsCompleted,
                "INBOX_FETCH_NO_WAIT_SLOT: nonblocking fetch works beside an existing wait without occupying it");
            cancel.Cancel();
            try { await waiting; } catch (OperationCanceledException) { }
            fixture.Store.Disconnect(owner.Address);
            var result = new CallToolResult { Content = [new TextContentBlock { Text = "original" }] };
            McpInboxHints.Append(result, fixture.Context, fixture.Runtime);
            check(((TextContentBlock)result.Content[0]).Text == "original",
                "INBOX_HINT_FIRST_BLOCK: pending hint never replaces or precedes existing content");
            using var hint = JsonDocument.Parse(((TextContentBlock)result.Content[^1]).Text);
            check(hint.RootElement.GetProperty("status").GetString() == "unavailable" &&
                hint.RootElement.GetProperty("pendingCount").ValueKind == JsonValueKind.Null && ((TextContentBlock)result.Content[0]).Text == "original",
                "INBOX_HINT_UNAVAILABLE: lost address is not a false zero and preserves the original block");
        }
        finally { await fixture.DisposeAsync(); CleanupScratch(fixture.Root); }
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "pending-inbox-wire-" + Guid.NewGuid().ToString("N"));
        string? keyName = null;
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic pending inbox", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, []);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                BaseAddress = new(worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
            await Rpc(http, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-pending", version = "1" } });
            var httpSchema = Schema(await Rpc(http, "tools/list", new { }));
            await VerifyAsync("HTTP", httpSchema, async (name, arguments) => await Rpc(http, "tools/call", new { name, arguments }));
            await using var bridge = new Bridge(scratch);
            await bridge.RpcAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-pending-stdio", version = "1" } });
            await VerifyAsync("STDIO", Schema(await bridge.RpcAsync("tools/list", new { })),
                async (name, arguments) => await bridge.RpcAsync("tools/call", new { name, arguments }));

            async Task VerifyAsync(string transport, JsonElement outputSchema, Func<string, object, Task<JsonElement>> call)
            {
                var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic sender", false);
                BrokerSession Native() => new(new(worker.Runtime.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
                    "same synthetic native label", "synthetic", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                    SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(1));
                var owner = Native(); var other = Native(); worker.Runtime.Store.Register(owner); worker.Runtime.Store.Register(other);
                const string text = "Synthetic Unicode 猫 and emoji 🐑\nLiteral Windows path: D:\\synthetic\\file; quoted empty string: \"\".";
                worker.Runtime.Store.Send(owner.Address, sender.Address, Guid.NewGuid(), "Pending answer to sender.", null, 0, worker.Runtime.Store.BrokerId);
                Guid messageId = Guid.NewGuid();
                var sent = await call("send", new { sender = sender.Address, recipient = owner.Address, messageId, text });
                var sentBody = Body(sent);
                check(sentBody.TryGetProperty("message", out var sentMessage) && sentMessage.GetProperty("id").GetGuid() == messageId && !sentMessage.TryGetProperty("text", out _),
                    "INBOX_WIRE_FIRST_BLOCK_" + transport + ": send keeps its original compact receipt first");
                check(Hint(sent).GetProperty("session").Deserialize<SessionAddress>(Json) == sender.Address && Hint(sent).GetProperty("pendingCount").GetInt32() == 1,
                    "INBOX_HINT_SENDER_" + transport + ": send counts exact sender inbox, never recipient inbox");
                var legacyWait = Body(await call("wait_for_messages", new { session = owner.Address, timeoutSeconds = 0 }));
                check(legacyWait.GetArrayLength() == 1 && HasNonReplyNull(legacyWait[0]),
                    "INBOX_LEGACY_WAIT_NULL_" + transport + ": the actual legacy incoming-wait content includes replyTo:null for a non-reply message");
                var legacyInbox = Body(await call("read_inbox", new { session = owner.Address }));
                check(legacyInbox.GetArrayLength() == 1 && HasNonReplyNull(legacyInbox[0]),
                    "INBOX_LEGACY_READ_NULL_" + transport + ": the actual legacy inbox content includes replyTo:null for a non-reply message");
                var fetched = await call("read_inbox_page", new { session = owner.Address });
                var body = Body(fetched); var receipt = body.GetProperty("entries")[0].GetProperty("receipt");
                var structured = fetched.GetProperty("result").GetProperty("structuredContent");
                check(PublishedOutputSchema.Matches(outputSchema, structured, out string schemaFailure) &&
                    PublishedOutputSchema.Matches(outputSchema, body, out schemaFailure),
                    "INBOX_OUTPUT_SCHEMA_" + transport + ": actual non-reply output follows tools/list schema; " + schemaFailure);
                var missing = JsonNode.Parse(structured.GetRawText())!;
                missing["entries"]![0]!["receipt"]!["message"]!.AsObject().Remove("replyTo");
                check(!PublishedOutputSchema.Matches(outputSchema, JsonSerializer.SerializeToElement(missing), out string missingFailure) &&
                    missingFailure.Contains("replyTo: required", StringComparison.Ordinal),
                    "INBOX_SCHEMA_MISSING_CONTROL_" + transport + ": evaluator refuses the original absent-null defect at its path");
                var wrongType = JsonNode.Parse(structured.GetRawText())!;
                wrongType["entries"]![0]!["receipt"]!["message"]!["text"] = 17;
                check(!PublishedOutputSchema.Matches(outputSchema, JsonSerializer.SerializeToElement(wrongType), out string typeFailure) &&
                    typeFailure.Contains("text: type", StringComparison.Ordinal),
                    "INBOX_SCHEMA_TYPE_CONTROL_" + transport + ": evaluator refuses invalid nested value types");
                var wrongDate = JsonNode.Parse(structured.GetRawText())!;
                wrongDate["entries"]![0]!["receipt"]!["updatedAt"] = "11/08/2026 12:00:00+00:00";
                check(!PublishedOutputSchema.Matches(outputSchema, JsonSerializer.SerializeToElement(wrongDate), out string dateFailure) &&
                    dateFailure.Contains("updatedAt: format date-time", StringComparison.Ordinal),
                    "INBOX_SCHEMA_DATE_CONTROL_" + transport + ": evaluator refuses locale dates that are not RFC3339");
                var unsupported = JsonNode.Parse(outputSchema.GetRawText())!;
                unsupported["unimplementedValidationKeyword"] = false;
                check(!PublishedOutputSchema.Matches(JsonSerializer.SerializeToElement(unsupported), structured, out string unsupportedFailure) &&
                    unsupportedFailure.Contains("unsupported schema keyword", StringComparison.Ordinal),
                    "INBOX_SCHEMA_VOCABULARY_CONTROL_" + transport + ": new validation vocabulary cannot silently pass");
                check(body.GetProperty("pendingCount").GetInt32() == 1 && receipt.GetProperty("message").GetProperty("text").GetString() == text &&
                    receipt.GetProperty("message").GetProperty("id").GetGuid() == messageId && !body.GetProperty("hasMore").GetBoolean(),
                    "INBOX_NATIVE_BUSY_FETCH_" + transport + ": actual busy native address returns complete quoted data without another mailbox");
                check(Hint(fetched).GetProperty("session").Deserialize<SessionAddress>(Json) == owner.Address,
                    "INBOX_HINT_RECIPIENT_" + transport + ": fetch hint names exact recipient");
                var otherPage = await call("read_inbox_page", new { session = other.Address });
                check(Body(otherPage).GetProperty("pendingCount").GetInt32() == 0 && Hint(otherPage).GetProperty("session").Deserialize<SessionAddress>(Json) == other.Address,
                    "INBOX_HINT_SHARED_TRANSPORT_" + transport + ": another chat on same transport has its own count");
                var directory = await call("list_sessions", new { });
                check(directory.GetProperty("result").GetProperty("content").GetArrayLength() == 1,
                    "INBOX_HINT_NO_GUESS_" + transport + ": ambiguous operation never inherits prior chat inbox");
                var invalid = await call("read_inbox_page", new { session = owner.Address, cursor = "malformed" });
                check(invalid.GetProperty("result").GetProperty("isError").GetBoolean() &&
                    !invalid.GetProperty("result").GetProperty("content").EnumerateArray().Any(block => block.GetProperty("text").GetString()!.Contains("agentbroker.pending_messages", StringComparison.Ordinal)),
                    "INBOX_CURSOR_ERROR_" + transport + ": invalid cursor is tool error without a success hint");
                var foreign = await call("read_inbox_page", new { session = owner.Address with { BrokerId = Guid.NewGuid() } });
                check(foreign.GetProperty("result").GetProperty("isError").GetBoolean(),
                    "INBOX_FOREIGN_SCOPE_" + transport + ": fetch refuses foreign broker address");
                var acknowledged = await call("acknowledge_message", new { messageId, recipient = owner.Address, outcome = "Delivered" });
                check(HasNonReplyNull(Body(acknowledged)),
                    "INBOX_LEGACY_ACK_NULL_" + transport + ": the actual acknowledgement content includes replyTo:null for a non-reply message");
                check(Hint(acknowledged).GetProperty("pendingCount").GetInt32() == 0 && worker.Runtime.Store.FindReceipt(messageId)?.Status == MessageStatus.Delivered,
                    "INBOX_ACK_HINT_" + transport + ": acknowledgement updates count and durable receipt");
                var continued = await call("read_inbox_page", new { session = owner.Address, cursor = body.GetProperty("nextCursor").GetString() });
                check(Body(continued).GetProperty("entries").GetArrayLength() == 0 && Body(continued).GetProperty("pendingCount").GetInt32() == 0,
                    "INBOX_CURSOR_AFTER_ACK_" + transport + ": cursor works after last record becomes tombstone");
            }
        }
        finally
        {
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
            CleanupScratch(scratch);
        }
    }
    private static bool HasNonReplyNull(JsonElement receipt) => receipt.GetProperty("message").TryGetProperty("replyTo", out var value) && value.ValueKind == JsonValueKind.Null;
    private static JsonElement Schema(JsonElement reply) => reply.GetProperty("result").GetProperty("tools").EnumerateArray()
        .Single(tool => tool.GetProperty("name").GetString() == "read_inbox_page").GetProperty("outputSchema").Clone();
    private static void CleanupScratch(string scratch)
    {
        string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(scratch).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Pending inbox fixture escaped scratch.");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    }
    private static JsonElement Body(JsonElement rpc)
    {
        using var json = JsonDocument.Parse(rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        return json.RootElement.Clone();
    }
    private static JsonElement Hint(JsonElement rpc)
    {
        using var json = JsonDocument.Parse(rpc.GetProperty("result").GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString()!);
        return json.RootElement.Clone();
    }
    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }, Json);
        response.EnsureSuccessStatusCode(); string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Concat(body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var json = JsonDocument.Parse(body); return json.RootElement.Clone();
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
            inputStream = input.Reader.AsStream(); outputStream = output.Writer.AsStream();
            reader = new(output.Reader.AsStream(), new UTF8Encoding(false, true));
            task = McpStdioBridge.RunAsync(directory, inputStream, outputStream);
        }
        internal async Task<JsonElement> RpcAsync(string method, object parameters)
        {
            int requestId = ++id;
            await input.Writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = requestId, method, @params = parameters }, Json));
            await input.Writer.WriteAsync("\n"u8.ToArray());
            string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("Missing inbox bridge response.");
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("id").GetInt32() != requestId) throw new IOException("Inbox bridge correlation changed.");
            return json.RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            try { await input.Writer.CompleteAsync(); if (await task.WaitAsync(TimeSpan.FromSeconds(5)) != 0) throw new IOException("Inbox bridge failed to stop."); }
            finally { inputStream.Dispose(); outputStream.Dispose(); reader.Dispose(); }
        }
    }
}
