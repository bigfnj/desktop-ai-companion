using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

internal static class PaginationChecks
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "pagination-" + Guid.NewGuid().ToString("N"));
        string storeDirectory = Path.Combine(scratch, "store");
        Directory.CreateDirectory(storeDirectory);
        Guid brokerId = Guid.NewGuid(), senderId = Guid.NewGuid(), recipientId = Guid.NewGuid();
        Guid senderIncarnation = Guid.NewGuid(), recipientIncarnation = Guid.NewGuid();
        var sender = new SessionAddress(brokerId, "mcp:" + senderId.ToString("D"), senderIncarnation);
        var recipient = new SessionAddress(brokerId, "mcp:" + recipientId.ToString("D"), recipientIncarnation);
        var now = DateTimeOffset.UtcNow;
        string text = new('\u0001', 16 * 1024); // Every character expands to a six-byte JSON escape.
        Guid MessageId(int value) => new(value, 0, 0, new byte[8]);
        var receipts = Enumerable.Range(1, 129).Where(index => index != 65).Select(index =>
            new MessageReceipt(new(MessageId(index), Guid.NewGuid(), null, sender, recipient, text,
                now, now.AddHours(1)), MessageStatus.Delivered, now, Attempts: 1)).ToList();
        Guid pendingId = MessageId(130);
        receipts.Add(new(new(pendingId, Guid.NewGuid(), null, sender, recipient, "Newest pending synthetic message.",
            now, now.AddHours(1)), MessageStatus.Accepted, now));
        check(JsonSerializer.SerializeToUtf8Bytes(receipts).Length > 8 * 1024 * 1024,
            "retained finalized inbox fixture exceeds the actual stdio response cap before pagination");
        // Seed one valid durable history instead of rewriting a growing large fixture hundreds of times.
        // Worker startup still exercises the production store's complete persisted-state validation.
        File.WriteAllBytes(Path.Combine(storeDirectory, "broker-state.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { Version = 1, BrokerId = brokerId, Peers = Array.Empty<PeerTrust>(), Messages = receipts }));
        string keyName = BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic pagination test", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            worker.Runtime.RegisterPollingAgent(senderId, senderIncarnation, "Synthetic pagination sender", false);
            worker.Runtime.RegisterPollingAgent(recipientId, recipientIncarnation, "Synthetic pagination recipient", false);
            await using var bridge = new StdioClient(scratch);
            var initialized = await bridge.RpcAsync("initialize", new
            { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "synthetic-pagination", version = "1" } });
            check(initialized.GetProperty("result").TryGetProperty("protocolVersion", out _),
                "pagination regression negotiates through the real authenticated HTTP-to-stdio bridge");
            var tools = await bridge.RpcAsync("tools/list", new { });
            check(tools.GetProperty("result").GetProperty("tools").GetArrayLength() == 10,
                "bounded collection tools preserve the ten-operation MCP contract");

            var first = await bridge.ArrayAsync("read_inbox", new { session = recipient });
            check(first.GetArrayLength() == 16 && bridge.LastFrameBytes < 8 * 1024 * 1024,
                "default inbox page contains sixteen worst-case escaped receipts within the stdio byte bound");
            check(first.EnumerateArray().All(item => item.GetProperty("message").GetProperty("text").GetString() == text),
                "pagination preserves each complete sixteen-KiB message without truncating escaped contents");
            var seen = new List<Guid>();
            JsonElement page = first;
            while (true)
            {
                var ids = page.EnumerateArray().Select(item => item.GetProperty("message").GetProperty("id").GetGuid()).ToArray();
                seen.AddRange(ids);
                if (ids.Length < 16) break;
                page = await bridge.ArrayAsync("read_inbox", new { session = recipient, afterMessageId = ids[^1] });
            }
            check(seen.SequenceEqual(receipts.Select(receipt => receipt.Message.Id).Order()),
                "keyset inbox traversal returns every retained receipt exactly once in Guid order");
            check(seen[^1] == pendingId && worker.Runtime.Store.FindReceipt(pendingId)?.Status == MessageStatus.Accepted,
                "large finalized history cannot hide the newest pending inbox message or acknowledge it implicitly");
            var pastEnd = await bridge.ArrayAsync("read_inbox", new { session = recipient, afterMessageId = pendingId });
            check(pastEnd.GetArrayLength() == 0, "inbox pagination returns an empty page after the final message");
            var missingCursor = await bridge.ArrayAsync("read_inbox", new { session = recipient, afterMessageId = MessageId(65), limit = 1 });
            check(missingCursor.GetArrayLength() == 1 && missingCursor[0].GetProperty("message").GetProperty("id").GetGuid() == MessageId(66),
                "an inbox cursor need not remain in retained history to resume at the next key");
            var polledAgain = await bridge.ArrayAsync("read_inbox", new { session = recipient, limit = 1 });
            check(polledAgain[0].GetProperty("message").GetProperty("id").GetGuid() == MessageId(1),
                "a new inbox poll without a cursor starts again at the first retained key");
            check(await bridge.ErrorAsync("read_inbox", new { session = recipient with { BrokerId = Guid.NewGuid() } }),
                "inbox pagination cannot use a receiver outside the authenticated broker");
            check(await bridge.ErrorAsync("read_inbox", new { session = recipient with { Incarnation = Guid.Empty } }),
                "inbox pagination validates the complete receiving session address");

            foreach (string id in new[] { "Directory-A", "Directory-a", "Directory-z" })
                worker.Runtime.Store.Register(new(new(brokerId, id, MessageId(100)), new string('\u4e00', 160), "synthetic-pagination",
                    SessionCapabilities.Receive, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(2)));
            for (int index = 0; index < 37; index++)
                worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), new string('\u4e00', 160), false);
            var expected = worker.Runtime.Store.Directory().Select(session => session.Address)
                .OrderBy(address => address.BrokerId).ThenBy(address => address.SessionId, StringComparer.Ordinal)
                .ThenBy(address => address.Incarnation).ToArray();
            var directoryPage = await bridge.ArrayAsync("list_sessions", new { });
            check(directoryPage.GetArrayLength() == 16, "default directory page is bounded even when more live sessions exist");
            var addresses = new List<SessionAddress>();
            while (true)
            {
                var current = directoryPage.EnumerateArray().Select(item => item.GetProperty("address").Deserialize<SessionAddress>(WebJson)!).ToArray();
                addresses.AddRange(current);
                if (current.Length < 16) break;
                directoryPage = await bridge.ArrayAsync("list_sessions", new { afterAddress = current[^1] });
            }
            check(addresses.SequenceEqual(expected), "directory keyset traversal returns each live address once with ordinal session ordering");
            var beforeIncarnation = await bridge.ArrayAsync("list_sessions", new { afterAddress = expected[0] with { Incarnation = MessageId(99) }, limit = 1 });
            var afterIncarnation = await bridge.ArrayAsync("list_sessions", new { afterAddress = expected[0] with { Incarnation = MessageId(101) }, limit = 1 });
            check(beforeIncarnation[0].GetProperty("address").Deserialize<SessionAddress>(WebJson) == expected[0] &&
                afterIncarnation[0].GetProperty("address").Deserialize<SessionAddress>(WebJson) == expected[1],
                "directory cursors compare incarnation as well as broker and ordinal session ID without an exact-record lookup");
            var afterBroker = await bridge.ArrayAsync("list_sessions", new
            { afterAddress = new SessionAddress(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), "cursor", MessageId(1)) });
            check(afterBroker.GetArrayLength() == 0, "directory cursor comparison respects the broker identity key");
            check(await bridge.ErrorAsync("list_sessions", new { afterAddress = expected[0] with { BrokerId = Guid.Empty } }),
                "directory pagination rejects an incomplete address cursor");
            check(await bridge.ErrorAsync("list_sessions", new { afterAddress = expected[0] with { SessionId = new string('x', 161) } }),
                "directory pagination applies normal address field bounds to cursors");
            foreach (int invalid in new[] { 0, 17 })
            {
                check(await bridge.ErrorAsync("read_inbox", new { session = recipient, limit = invalid }),
                    "inbox rejects an out-of-range page limit instead of returning an unbounded result");
                check(await bridge.ErrorAsync("list_sessions", new { limit = invalid }),
                    "directory rejects an out-of-range page limit instead of returning an unbounded result");
            }
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
            string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(scratch).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Pagination fixture escaped its scratch root.");
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed class StdioClient : IAsyncDisposable
    {
        private readonly Pipe input = new(), output = new();
        private readonly Stream bridgeInput, bridgeOutput;
        private readonly StreamReader reader;
        private readonly Task<int> bridge;
        private int nextId;
        internal int LastFrameBytes { get; private set; }
        internal StdioClient(string directory)
        {
            bridgeInput = input.Reader.AsStream(); bridgeOutput = output.Writer.AsStream();
            reader = new(output.Reader.AsStream(), new UTF8Encoding(false, true));
            bridge = McpStdioBridge.RunAsync(directory, bridgeInput, bridgeOutput);
        }
        internal async Task<JsonElement> RpcAsync(string method, object parameters)
        {
            int id = ++nextId;
            byte[] frame = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }, WebJson);
            await input.Writer.WriteAsync(frame); await input.Writer.WriteAsync("\n"u8.ToArray());
            string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new IOException("Missing pagination stdio response.");
            LastFrameBytes = Encoding.UTF8.GetByteCount(line);
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("id").GetInt32() != id) throw new IOException("Pagination RPC correlation changed.");
            return json.RootElement.Clone();
        }
        internal async Task<JsonElement> ArrayAsync(string name, object arguments)
        {
            var response = await RpcAsync("tools/call", new { name, arguments });
            if (IsError(response)) throw new IOException("Pagination tool returned an error: " + name);
            string text = response.GetProperty("result").GetProperty("content").EnumerateArray()
                .First(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("Collection tool changed its array result shape.");
            return json.RootElement.Clone();
        }
        internal async Task<bool> ErrorAsync(string name, object arguments) =>
            IsError(await RpcAsync("tools/call", new { name, arguments }));
        private static bool IsError(JsonElement response) => response.TryGetProperty("error", out _) ||
            response.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
        public async ValueTask DisposeAsync()
        {
            try
            {
                await input.Writer.CompleteAsync();
                if (await bridge.WaitAsync(TimeSpan.FromSeconds(5)) != 0) throw new IOException("Pagination bridge did not stop cleanly.");
            }
            finally { bridgeInput.Dispose(); bridgeOutput.Dispose(); reader.Dispose(); }
        }
    }
}
