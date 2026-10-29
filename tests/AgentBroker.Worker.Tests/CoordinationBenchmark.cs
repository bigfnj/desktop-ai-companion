using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

// O-agentbroker-02: one measurement per fresh process, driven by tests/Measure-AgentBrokerCoordination.ps1, which
// interleaves configurations across processes. Not a check: it asserts only that each measured operation did what it
// was timed doing, prints one BENCHMARK line of JSON and deletes everything it created (directories and CNG keys).
// It measures the current store, runtime and MCP endpoint unmodified; no limit or code path is changed for it.
internal static class CoordinationBenchmark
{
    private const int CommitSamples = 6;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string json = args.Length > 0 ? args[0] switch
            {
                "commit" when args.Length == 5 => Commit(int.Parse(args[1]), int.Parse(args[2]), args[3], args[4]),
                "tombstone" when args.Length == 7 => Tombstone(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]),
                    int.Parse(args[4]), int.Parse(args[5]), args[6]),
                "wake" when args.Length == 3 => await WakeAsync(args[1], int.Parse(args[2])),
                "directory" when args.Length == 5 => await DirectoryAsync(int.Parse(args[1]), int.Parse(args[2]), args[3], int.Parse(args[4])),
                _ => throw new ArgumentException("Unknown benchmark arguments.")
            } : throw new ArgumentException("A benchmark scenario is required.");
            Console.WriteLine("BENCHMARK " + json);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.WriteLine("BENCHMARK-FAILED " + error.GetType().Name);
            return 1;
        }
    }

    // Accept + Commit (one store.Send, the path the send tool takes) at a retained-message count. The state is seeded
    // directly, as PaginationChecks does, because filling 4,096 messages through Accept would rewrite a growing
    // snapshot 4,096 times. The first Send in the process is cold (JIT, serializer metadata, first replacement);
    // the next five are warm. Each one rewrites and flushes the whole snapshot.
    private static string Commit(int retained, int textBytes, string kind, string root)
    {
        if (retained is < CommitSamples + 1 or > 4096 || textBytes is < 1 or > 16 * 1024 || kind is not ("ascii" or "escaped"))
            throw new ArgumentOutOfRangeException(nameof(retained));
        string storeDirectory = NewDirectory(root, "store");
        try
        {
            var brokerId = Guid.NewGuid();
            SessionAddress Address() => new(brokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            SessionAddress sender = Address(), recipient = Address();
            var others = Enumerable.Range(0, 64).Select(_ => Address()).ToArray();
            // "escaped" is the serializer's worst case: every character becomes a six-byte JSON escape.
            string text = new(kind == "escaped" ? (char)1 : 'x', textBytes);
            var now = DateTimeOffset.UtcNow;
            var seeds = Enumerable.Range(0, retained - CommitSamples).Select(index => new MessageReceipt(
                new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender, others[index % others.Length], text, now, now.AddHours(20)),
                MessageStatus.Accepted, now)).ToList();
            using (var seed = File.Create(Path.Combine(storeDirectory, "broker-state.json")))
                JsonSerializer.Serialize(seed, new { Version = 1, BrokerId = brokerId, Peers = Array.Empty<PeerTrust>(), Messages = seeds });
            seeds.Clear();
            var load = Stopwatch.StartNew();
            using var store = new BrokerStore(storeDirectory);
            double loadMs = load.Elapsed.TotalMilliseconds;
            foreach (var address in new[] { sender, recipient })
                store.Register(new BrokerSession(address, "Benchmark session", "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
                    SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1)));
            var sends = new List<double>();
            for (int index = 0; index < CommitSamples; index++)
            {
                var watch = Stopwatch.StartNew();
                var receipt = store.Send(sender, recipient, Guid.NewGuid(), text, null, 0, brokerId);
                sends.Add(watch.Elapsed.TotalMilliseconds);
                if (receipt.Status != MessageStatus.Accepted) throw new InvalidDataException("A benchmark send was not accepted.");
            }
            int count = store.Messages(sender).Count;
            if (count != retained) throw new InvalidDataException("The store did not retain the expected message count.");
            long stateBytes = new FileInfo(Path.Combine(storeDirectory, "broker-state.json")).Length;
            return JsonSerializer.Serialize(new { scenario = "commit", retained, textBytes, kind, stateBytes, loadMs, sendMs = sends });
        }
        finally { DeleteTree(root); }
    }

    // Lane broker-e (N-broker-a-02, tombstone on acknowledgement). Run by BOTH builds from the same arguments: this
    // file compiles against the current store too, which is the control. The snapshot is seeded with `acknowledged`
    // Delivered records, the shape the current store keeps; this lane's store moves them to tombstones as it opens, so
    // its loadMs includes that move. The journal is seeded with `tombstones` live and `dead` expired lines in this
    // lane's private format, which the current store never reads (it cannot hold a tombstone at all). `cap` above zero
    // sets BrokerLimits.MaximumTombstones by reflection where that property exists. Each of six rounds times one
    // store.Send (sendMs) and then the recipient's acknowledgement (ackMs); messagesMs times store.Messages for the
    // session every seeded record involves, which is what each read_inbox page and wait pass builds. With dead lines,
    // the first acknowledgement compacts the journal, so a scratch store warms the code paths first (warmed = true).
    private static string Tombstone(int acknowledged, int tombstones, int dead, int textBytes, int cap, string root)
    {
        if (acknowledged is < 0 or > 4096 - CommitSamples || tombstones is < 0 or > 1_000_000 || dead is < 0 or > 1_000_000 ||
            textBytes is < 1 or > 16 * 1024 || cap < 0)
            throw new ArgumentOutOfRangeException(nameof(acknowledged));
        string storeDirectory = NewDirectory(root, "store");
        try
        {
            bool warmed = dead > 0;
            if (warmed) WarmUp(Path.Combine(root, "warm-up"));
            var brokerId = Guid.NewGuid();
            SessionAddress Address() => new(brokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            SessionAddress sender = Address(), recipient = Address();
            var others = Enumerable.Range(0, 64).Select(_ => Address()).ToArray();
            string text = new('x', textBytes);
            string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var now = DateTimeOffset.UtcNow;
            var seeds = Enumerable.Range(0, acknowledged).Select(index => new MessageReceipt(
                new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender, others[index % others.Length], text, now, now.AddHours(20)),
                MessageStatus.Delivered, now, "recipient_receipt", 1)).ToList();
            using (var seed = File.Create(Path.Combine(storeDirectory, "broker-state.json")))
                JsonSerializer.Serialize(seed, new { Version = 1, BrokerId = brokerId, Peers = Array.Empty<PeerTrust>(), Messages = seeds });
            seeds.Clear();
            if (tombstones + dead > 0)
                using (var journal = new BufferedStream(File.Create(Path.Combine(storeDirectory, "broker-tombstones.jsonl")), 1 << 20))
                {
                    void Line(object value) { JsonSerializer.Serialize(journal, value); journal.WriteByte((byte)'\n'); }
                    Line(new { Version = 1, BrokerId = brokerId });
                    for (int index = 0; index < tombstones + dead; index++)
                    {
                        var created = index < tombstones ? now : now.AddDays(-3);
                        Line(new { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ReplyTo = (Guid?)null, Sender = sender,
                            Recipient = others[index % others.Length], TextSha256 = hash, CreatedAt = created, ExpiresAt = created.AddHours(20),
                            RemainingReplies = 8, Status = (int)MessageStatus.Delivered, UpdatedAt = created, Reason = "recipient_receipt", Attempts = 1 });
                    }
                }
            var limits = new BrokerLimits();
            var capProperty = typeof(BrokerLimits).GetProperty("MaximumTombstones");
            if (cap > 0 && capProperty is not null) capProperty.SetValue(limits, cap);
            long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var load = Stopwatch.StartNew();
            using var store = new BrokerStore(storeDirectory, limits);
            double loadMs = load.Elapsed.TotalMilliseconds;
            // What the open store keeps in memory, after a full collection and outside every timed section.
            long storeHeapBytes = GC.GetTotalMemory(forceFullCollection: true) - heapBefore;
            foreach (var address in new[] { sender, recipient })
                store.Register(new BrokerSession(address, "Benchmark session", "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
                    SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1)));
            List<double> sends = [], acks = [];
            for (int index = 0; index < CommitSamples; index++)
            {
                var watch = Stopwatch.StartNew();
                var receipt = store.Send(sender, recipient, Guid.NewGuid(), text, null, 0, brokerId);
                sends.Add(watch.Elapsed.TotalMilliseconds);
                if (receipt.Status != MessageStatus.Accepted) throw new InvalidDataException("A benchmark send was not accepted.");
                watch.Restart();
                var acknowledgement = store.RecordRecipientReceipt(receipt.Message.Id, recipient, brokerId, MessageStatus.Delivered);
                acks.Add(watch.Elapsed.TotalMilliseconds);
                if (acknowledgement.Status != MessageStatus.Delivered) throw new InvalidDataException("A benchmark acknowledgement was not recorded.");
            }
            var scan = Stopwatch.StartNew();
            int listed = store.Messages(sender).Count;
            double messagesMs = scan.Elapsed.TotalMilliseconds;
            // The control keeps every seeded record; the tombstone build keeps the journal's live lines as well.
            if (listed != acknowledged + CommitSamples && listed != acknowledged + tombstones + CommitSamples)
                throw new InvalidDataException("The store did not list the expected history.");
            // Lane broker-next (N-broker-e-02): one wait_for_messages pass for the sender, whose history every seeded record
            // shares, timed six times (the first in the process is the cold one). It runs the store's AcceptedIncoming query
            // where that method exists (this lane's build), else the filter over Messages that BrokerTools ran before (the
            // control), so one source measures both builds. One message to the sender is Accepted first, so every pass has
            // exactly that record to return, and it is checked.
            var incoming = store.Send(recipient, sender, Guid.NewGuid(), text, null, 0, brokerId);
            var query = typeof(BrokerStore).GetMethod("AcceptedIncoming");
            Func<MessageReceipt[]> waitPass = query is not null
                ? () => ((IReadOnlyList<MessageReceipt>)query.Invoke(store, [sender, 16])!).ToArray()
                : () => store.Messages(sender).Where(receipt => receipt.Message.Recipient == sender && receipt.Status == MessageStatus.Accepted)
                    .OrderBy(receipt => receipt.Message.CreatedAt).ThenBy(receipt => receipt.Message.Id).Take(16).ToArray();
            List<double> waitPasses = [];
            for (int index = 0; index < CommitSamples; index++)
            {
                var watch = Stopwatch.StartNew();
                var found = waitPass();
                waitPasses.Add(watch.Elapsed.TotalMilliseconds);
                if (found.Length != 1 || found[0].Message.Id != incoming.Message.Id)
                    throw new InvalidDataException("A benchmark wait pass did not return the one accepted message.");
            }
            long Bytes(string name) => File.Exists(Path.Combine(storeDirectory, name)) ? new FileInfo(Path.Combine(storeDirectory, name)).Length : 0;
            return JsonSerializer.Serialize(new { scenario = "tombstone", acknowledged, tombstones, dead, textBytes, cap, warmed,
                tombstoneStore = capProperty is not null, listed, stateBytes = Bytes("broker-state.json"),
                journalBytes = Bytes("broker-tombstones.jsonl"), storeHeapBytes, loadMs, sendMs = sends, ackMs = acks, messagesMs,
                waitPassQuery = query is not null ? "accepted-incoming" : "messages-filter", waitPassMs = waitPasses });
        }
        finally { DeleteTree(root); }
    }

    // One send and acknowledgement on a throwaway store, so a later first operation measures the store, not the JIT.
    private static void WarmUp(string directory)
    {
        using var store = new BrokerStore(directory);
        SessionAddress from = new(store.BrokerId, "mcp:warm-from", Guid.NewGuid()), to = new(store.BrokerId, "mcp:warm-to", Guid.NewGuid());
        foreach (var address in new[] { from, to })
            store.Register(new BrokerSession(address, "Benchmark warm-up", "mcp", SessionCapabilities.Receive | SessionCapabilities.Reply,
                SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1)));
        var receipt = store.Send(from, to, Guid.NewGuid(), "Benchmark warm-up message.", null, 0, store.BrokerId);
        store.RecordRecipientReceipt(receipt.Message.Id, to, store.BrokerId, MessageStatus.Delivered);
        store.Messages(from);
    }

    // wait_for_messages wake latency over the real HTTP MCP endpoint: a recipient's wait is in flight, then a second
    // agent sends to it. sendMs is the sender's own round trip; wakeMs runs from the same start to the waiter's reply.
    private static async Task<string> WakeAsync(string root, int samples)
    {
        if (samples is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(samples));
        NewDirectory(root, "");
        var keys = new List<string>();
        WorkerApplication? worker = null;
        try
        {
            worker = await Start(root, lan: false, keys);
            using var http = Client(worker.Application.Urls.First(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp");
            var sender = await RegisterAsync(http, "Benchmark sender");
            List<double> send = [], wake = [];
            for (int index = 0; index < samples; index++)
            {
                var recipient = await RegisterAsync(http, "Benchmark recipient");
                var waiting = CallAsync(http, "wait_for_messages", new { session = recipient, timeoutSeconds = 20, limit = 16 });
                await Task.Delay(250); // The wait has captured the store's change signal long before this.
                if (waiting.IsCompleted) throw new InvalidDataException("A benchmark wait returned before any send.");
                Guid messageId = Guid.NewGuid();
                var watch = Stopwatch.StartNew();
                await CallAsync(http, "send", new { sender, recipient, messageId, text = "Benchmark wake message." });
                send.Add(watch.Elapsed.TotalMilliseconds);
                var woke = await waiting;
                wake.Add(watch.Elapsed.TotalMilliseconds);
                if (!woke.GetRawText().Contains(messageId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A benchmark wait did not return the message that woke it.");
            }
            return JsonSerializer.Serialize(new { scenario = "wake", samples, sendMs = send, wakeMs = wake });
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            DeleteKeys(keys); DeleteTree(root);
        }
    }

    // A federated directory page: list_sessions on a broker paired with every peer, each a real worker with mutual TLS
    // on loopback. Every page refreshes every peer's directory (one HTTPS exchange each, at most four in parallel,
    // inside a five-second budget). runtimeMs times BrokerRuntime.DirectoryAsync itself and checks completeness;
    // pageMs times the MCP tool page by page with the cursor, the first page cold.
    private static async Task<string> DirectoryAsync(int peers, int sessionsPerPeer, string root, int pages)
    {
        if (peers is < 1 or > 32 || sessionsPerPeer is < 1 or > 256 || pages is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(peers));
        NewDirectory(root, "");
        var keys = new List<string>();
        var workers = new List<WorkerApplication>();
        try
        {
            var local = await Start(Path.Combine(root, "local"), lan: true, keys);
            workers.Add(local);
            for (int index = 0; index < peers; index++) workers.Add(await Start(Path.Combine(root, "peer-" + index), lan: true, keys));
            var now = DateTimeOffset.UtcNow;
            foreach (var peer in workers.Skip(1))
            {
                local.Runtime.Store.AuthorizePeer(new(peer.Runtime.Store.BrokerId, "Benchmark peer", LanEndpoint(peer), peer.Runtime.Federation.LocalAdvertisement.CertificateSha256, now));
                peer.Runtime.Store.AuthorizePeer(new(local.Runtime.Store.BrokerId, "Benchmark local", LanEndpoint(local), local.Runtime.Federation.LocalAdvertisement.CertificateSha256, now));
            }
            // Registered last: presence lasts one minute, and the measurement must see every session present.
            foreach (var peer in workers.Skip(1))
                for (int index = 0; index < sessionsPerPeer; index++)
                    peer.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Benchmark peer session", false);
            int expected = peers * sessionsPerPeer;
            List<double> runtime = [];
            int federated = 0;
            for (int index = 0; index < 3; index++)
            {
                var watch = Stopwatch.StartNew();
                var all = await local.Runtime.DirectoryAsync(false, CancellationToken.None);
                runtime.Add(watch.Elapsed.TotalMilliseconds);
                federated = all.Count;
            }
            using var http = Client(local.Application.Urls.First(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp");
            List<double> page = [];
            JsonElement? cursor = null;
            int returned = 0;
            for (int index = 0; index < pages; index++)
            {
                var watch = Stopwatch.StartNew();
                var result = await CallAsync(http, "list_sessions", cursor is null ? new { limit = 16 } : new { limit = 16, afterAddress = cursor.Value });
                page.Add(watch.Elapsed.TotalMilliseconds);
                var sessions = Sessions(result);
                returned += sessions.Length;
                if (sessions.Length < 16) break;
                cursor = sessions[^1].GetProperty("address").Clone();
            }
            return JsonSerializer.Serialize(new { scenario = "directory", peers, sessionsPerPeer, expected, federated, returned, runtimeMs = runtime, pageMs = page });
        }
        finally
        {
            foreach (var worker in workers) await worker.DisposeAsync();
            DeleteKeys(keys); DeleteTree(root);
        }
    }

    private static async Task<WorkerApplication> Start(string directory, bool lan, List<string> keys)
    {
        var options = new WorkerOptions(directory, "Benchmark broker", LoopbackPort: 0, LanPort: 0, EnableLan: lan,
            AllowUnauthenticatedLoopback: true) { LanListenAddress = IPAddress.Loopback, EnableBrokerDiscovery = false };
        var worker = await WorkerApplication.CreateAsync(options, []);
        keys.Add(BrokerIdentity.KeyNameFor(directory, worker.Runtime.Store.BrokerId));
        await worker.StartAsync();
        return worker;
    }

    private static string LanEndpoint(WorkerApplication worker) =>
        worker.Application.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal)) + "/";

    private static HttpClient Client(string endpoint)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        return client;
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient http, string label)
    {
        var result = await CallAsync(http, "register_agent", new { clientId = Guid.NewGuid(), incarnation = Guid.NewGuid(), label });
        return Structured(result).GetProperty("address").Clone();
    }

    private static async Task<JsonElement> CallAsync(HttpClient http, string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = JsonContent.Create(new
        { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call", @params = new { name = tool, arguments } }) };
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Concat(text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(text);
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var error) && error.GetBoolean())
            throw new InvalidDataException("Benchmark tool call " + tool + " failed.");
        return result.Clone();
    }

    // Tool results carry their JSON value as structured content and, for older clients, as one text block.
    private static JsonElement Structured(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured))
            return structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("result", out var wrapped) ? wrapped : structured;
        using var document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Sessions(JsonElement result)
    {
        var value = Structured(result);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : throw new InvalidDataException("list_sessions returned no array.");
    }

    private static string NewDirectory(string root, string child)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root))
            throw new IOException("The benchmark root must be a new absolute directory.");
        Directory.CreateDirectory(root);
        string directory = child.Length == 0 ? root : Path.Combine(root, child);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTree(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { Console.Error.WriteLine("benchmark: could not delete its scratch directory"); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("benchmark: could not delete its scratch directory"); }
    }

    private static void DeleteKeys(IEnumerable<string> names)
    {
        foreach (string name in names)
            if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            {
                using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                key.Delete();
            }
    }
}
