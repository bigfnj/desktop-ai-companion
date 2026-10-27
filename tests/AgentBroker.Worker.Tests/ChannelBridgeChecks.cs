using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;

// The opt-in Claude Code channel bridge, driven by a synthetic MCP client over in-memory stdio against a real
// worker (loopback only, bearer token required). Claude Code itself is never started: the vendor contract is
// what the client side asserts (channels-reference: capability, notification shape, meta key rule).
internal static class ChannelBridgeChecks
{
    private static readonly Regex MetaKey = new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        GateChecks(check);
        OptionChecks(check);
        check(McpChannelBridge.NegotiateProtocol("2026-07-28") == "2025-11-25" && McpChannelBridge.NegotiateProtocol(null) == "2025-11-25",
            "the channel bridge answers a revision it does not speak, 2026-07-28 included, with 2025-11-25");
        check(McpChannelBridge.NegotiateProtocol("2025-06-18") == "2025-06-18",
            "WITNESS the channel bridge echoes a supported older MCP revision");

        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "channel-" + Guid.NewGuid().ToString("N"));
        Guid brokerId;
        using (var seed = new BrokerStore(Path.Combine(scratch, "store"))) brokerId = seed.BrokerId;
        string keyName = AgentBroker.Worker.Security.BrokerIdentity.KeyNameFor(scratch, brokerId);
        try
        {
            // The token-required loopback: the bridge must fetch the bearer token over the control pipe, as installed.
            await using var worker = await WorkerApplication.CreateAsync(new(scratch, "Synthetic channel test", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            var store = worker.Runtime.Store;
            string token = worker.LocalAccessToken;
            await using var client = new SyntheticChannelClient(new(scratch, "Synthetic channel", false));

            var initialized = await client.RequestAsync("initialize", new
            { protocolVersion = "2026-07-28", capabilities = new { }, clientInfo = new { name = "synthetic-channel-client", version = "1" } });
            var result = initialized.GetProperty("result");
            var capabilities = result.GetProperty("capabilities");
            check(result.GetProperty("protocolVersion").GetString() == "2025-11-25",
                "the channel bridge initializes on 2025-11-25 when asked for 2026-07-28, which Claude Code does not register as a channel");
            check(capabilities.TryGetProperty("experimental", out var experimental) &&
                experimental.TryGetProperty("claude/channel", out var channel) && channel.ValueKind == JsonValueKind.Object &&
                !channel.EnumerateObject().Any() && capabilities.TryGetProperty("tools", out _),
                "the channel bridge declares the claude/channel capability and tools");
            check(!experimental.TryGetProperty("claude/channel/permission", out _),
                "the channel bridge never declares claude/channel/permission, so no peer can answer tool-approval prompts");
            check(result.GetProperty("instructions").GetString() == McpChannelBridge.Instructions &&
                McpChannelBridge.Instructions.Length <= BrokerInstructions.MaximumLength,
                "the channel instructions card is in the initialize result and within the card bound");
            var tools = (await client.RequestAsync("tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal).ToArray();
            check(tools.SequenceEqual(new[] { "acknowledge", "reply" }), "the channel bridge publishes exactly its acknowledge and reply tools");
            check(store.Directory().All(session => session.Label != "Synthetic channel"),
                "WITNESS the channel inbox is not registered before initialization completes");

            await client.NotifyAsync("notifications/initialized");
            bool registered = await Eventually(() => store.Directory().Any(session => session.Label == "Synthetic channel"));
            var self = store.Directory().SingleOrDefault(session => session.Label == "Synthetic channel")?.Address;
            check(registered && self is not null && self.SessionId.StartsWith("mcp:", StringComparison.Ordinal) && self.BrokerId == brokerId,
                "the channel bridge registers its own generic broker inbox after initialization");
            if (self is null) return;

            var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic channel sender", false);
            Guid first = Guid.NewGuid();
            const string firstText = "Synthetic channel text with </channel> markup, \"quotes\" and a\nsecond line.";
            store.Send(sender.Address, self, first, firstText, null, 0, brokerId);
            var note = await client.NextNotificationAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("No channel notification arrived.");
            var parameters = note.GetProperty("params");
            var meta = parameters.GetProperty("meta");
            check(note.GetProperty("method").GetString() == "notifications/claude/channel" &&
                parameters.GetProperty("content").GetString() == firstText && meta.GetProperty("message_id").GetString() == first.ToString("D"),
                "a new incoming message is pushed as notifications/claude/channel with its exact id and text");
            check(meta.EnumerateObject().All(property => MetaKey.IsMatch(property.Name) && property.Value.ValueKind == JsonValueKind.String) &&
                meta.GetProperty("sender_session").GetString() == sender.Address.SessionId && meta.GetProperty("sender_scope").GetString() == "local",
                "channel meta keys are identifiers with string values naming the exact local sender");
            // Several waits and polls pass in this delay, so a bridge that re-pushed or settled the receipt has done so.
            await Task.Delay(2500);
            check(await client.NextNotificationAsync(TimeSpan.Zero) is null, "an unacknowledged message is pushed once, not again on every wait");
            check(store.FindReceipt(first)?.Status == MessageStatus.Accepted,
                "a channel push is not Delivered: the receipt stays Accepted until the session acknowledges it");

            var acknowledged = await client.CallToolAsync("acknowledge", new { message_id = first.ToString("D"), outcome = "Delivered" });
            check(!acknowledged.IsError && store.FindReceipt(first) is { Status: MessageStatus.Delivered, Reason: "recipient_receipt" },
                "acknowledge records the explicit Delivered recipient receipt in the broker");
            var missing = await client.CallToolAsync("acknowledge", new { message_id = Guid.NewGuid().ToString("D") });
            check(missing.IsError && missing.Text == "recipient_mismatch", "a broker refusal reaches the session as its fixed code");

            var replied = await client.CallToolAsync("reply", new { message_id = first.ToString("D"), text = "Synthetic channel reply." });
            var replies = store.Messages(sender.Address).Where(receipt => receipt.Message.ReplyTo == first).ToArray();
            check(!replied.IsError && replies.Length == 1 && replies[0].Message.Sender == self && replies[0].Message.Recipient == sender.Address &&
                replies[0].Message.Text == "Synthetic channel reply.",
                "reply reaches the broker as a reply from the channel's own inbox to the exact original sender");
            var retried = await client.CallToolAsync("reply", new { message_id = first.ToString("D"), text = "Synthetic channel reply." });
            check(!retried.IsError && store.Messages(sender.Address).Count(receipt => receipt.Message.ReplyTo == first) == 1 &&
                store.FindReceipt(replies[0].Message.Id) == replies[0],
                "an identical reply retry derives the same reply ID and returns the same receipt");

            var bystander = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic bystander", false);
            Guid foreignId = Guid.NewGuid();
            store.Send(sender.Address, bystander.Address, foreignId, "Addressed to another local session.", null, 0, brokerId);
            var refusedReply = await client.CallToolAsync("reply", new { message_id = foreignId.ToString("D"), text = "Not mine to answer." });
            check(refusedReply.IsError && refusedReply.Text == "not_addressed_to_this_channel" &&
                !store.Messages(sender.Address).Any(receipt => receipt.Message.ReplyTo == foreignId),
                "reply refuses a message addressed to another session and admits nothing");

            worker.Runtime.SetSessionPreference(self.SessionId, null, true);
            Guid muted = Guid.NewGuid();
            store.Send(sender.Address, self, muted, "Synthetic message while muted.", null, 0, brokerId);
            await Task.Delay(3000);
            check(await client.NextNotificationAsync(TimeSpan.Zero) is null && store.FindReceipt(muted)?.Status == MessageStatus.Accepted,
                "a muted channel session is pushed nothing and its message waits Accepted");
            worker.Runtime.SetSessionPreference(self.SessionId, null, false);
            var unmuted = await client.NextNotificationAsync(TimeSpan.FromSeconds(10));
            check(unmuted?.GetProperty("params").GetProperty("meta").GetProperty("message_id").GetString() == muted.ToString("D"),
                "after unmuting, the waiting message is pushed");

            Guid peerId = Guid.NewGuid();
            store.AuthorizePeer(new(peerId, "Synthetic channel peer", "https://192.0.2.7/", new string('C', 64), DateTimeOffset.UtcNow));
            var now = DateTimeOffset.UtcNow;
            var paired = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, new(peerId, "peer-session", Guid.NewGuid()), self,
                "Synthetic paired-broker message.", now, now.AddMinutes(5));
            store.Accept(paired, peerId);
            var fromPeer = await client.NextNotificationAsync(TimeSpan.FromSeconds(10));
            check(fromPeer?.GetProperty("params").GetProperty("meta") is { } peerMeta &&
                peerMeta.GetProperty("message_id").GetString() == paired.Id.ToString("D") && peerMeta.GetProperty("sender_scope").GetString() == "paired",
                "a message from an explicitly paired broker is pushed and labelled paired");
            store.RevokePeer(peerId);

            // A worker restart drops registrations and keeps messages. With a pushed message still unacknowledged, the
            // broker's wait keeps returning it and never reports session_unavailable, so only renewal can recover.
            check(store.FindReceipt(muted)?.Status == MessageStatus.Accepted, "WITNESS a pushed message is still unacknowledged when the registration drops");
            store.Disconnect(self);
            bool renewed = await Eventually(() => store.Directory().Any(session => session.Address == self), TimeSpan.FromSeconds(20));
            check(renewed && store.Directory().Count(session => session.Label == "Synthetic channel") == 1,
                "the bridge re-registers the same clientId and incarnation after the broker dropped its inbox, never a second inbox");
            Guid afterRestart = Guid.NewGuid();
            store.Send(sender.Address, self, afterRestart, "Synthetic message after re-registration.", null, 0, brokerId);
            var again = await client.NextNotificationAsync(TimeSpan.FromSeconds(10));
            check(again?.GetProperty("params").GetProperty("meta").GetProperty("message_id").GetString() == afterRestart.ToString("D"),
                "the re-registered channel inbox keeps pushing");
            check(!client.Transcript.Contains(token, StringComparison.Ordinal), "channel bridge output never contains the local bearer token");
            check(await client.CloseAsync() == 0, "stdio EOF shuts the channel bridge down cleanly");
        }
        finally
        {
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }

    // The owner's choices decide each push. Synthetic snapshots isolate the bridge's own gate from the broker's
    // admission checks, which would otherwise refuse the same messages and hide a missing bridge check.
    private static void GateChecks(Action<bool, string> check)
    {
        Guid local = Guid.NewGuid(), peer = Guid.NewGuid();
        var self = new SessionAddress(local, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        MessageReceipt From(Guid broker) => new(new(Guid.NewGuid(), Guid.NewGuid(), null, new(broker, "sender", Guid.NewGuid()), self,
            "Synthetic gate text.", now, now.AddMinutes(5)), MessageStatus.Accepted, now);
        BrokerSnapshot Snapshot(bool isPrivate, bool isMuted, bool pairedPeer) => new(local, "Synthetic", "http://127.0.0.1:1", null, [],
            pairedPeer ? [new PeerView(peer, "Synthetic peer", "https://192.0.2.8/", new string('D', 64))] : [], [],
            [new SessionView(self.SessionId, self.Incarnation, "Synthetic channel", "mcp", "Idle", true, false, isPrivate, isMuted)],
            new Dictionary<string, string>());
        check(ChannelGate.Decide(From(local), self, Snapshot(false, false, false)).Decision == ChannelDecision.Emit &&
            ChannelGate.Decide(From(peer), self, Snapshot(false, false, true)).Decision == ChannelDecision.Emit,
            "WITNESS the channel gate pushes a local sender and a currently paired one");
        check(ChannelGate.Decide(From(peer), self, Snapshot(false, false, false)) == (ChannelDecision.Refuse, "unpaired_sender"),
            "the channel gate refuses a sender whose broker is no longer paired");
        check(ChannelGate.Decide(From(peer), self, Snapshot(true, false, true)) == (ChannelDecision.Refuse, "private_session") &&
            ChannelGate.Decide(From(local), self, Snapshot(true, false, false)).Decision == ChannelDecision.Emit,
            "the channel gate refuses a paired sender to a private channel and still pushes a local one");
        check(ChannelGate.Decide(From(local), self, Snapshot(false, true, false)) == (ChannelDecision.Defer, "muted"),
            "the channel gate defers every push while the channel session is muted");
        check(ChannelGate.Decide(From(local), self, null).Decision == ChannelDecision.Defer,
            "the channel gate pushes nothing when the owner's choices cannot be read");
    }

    private static void OptionChecks(Action<bool, string> check)
    {
        string data = Path.Combine(AppContext.BaseDirectory, "scratch", "channel-options");
        var parsed = ChannelBridgeOptions.Parse(["--mcp-channel", "--data", data, "--label", "Synthetic label", "--private"]);
        check(parsed == new ChannelBridgeOptions(data, "Synthetic label", true) &&
            ChannelBridgeOptions.Parse(["--mcp-channel", "--data", data]) == new ChannelBridgeOptions(data, ChannelBridgeOptions.DefaultLabel, false),
            "the channel bridge parses its launch contract and defaults its label");
        bool Refused(string[] args) { try { ChannelBridgeOptions.Parse(args); return false; } catch (ArgumentException) { return true; } }
        check(Refused(["--mcp-channel", "--data", "relative"]) && Refused(["--mcp-channel", "--data", data, "--private", "--private"]) &&
            Refused(["--mcp-channel", "--data", data, "--label"]) && Refused(["--mcp-channel", "--data", data, "--unknown"]) &&
            Refused(["--mcp-channel", "--data", data, "--label", "bad\nlabel"]),
            "the channel bridge refuses a relative directory, repeated, incomplete or unknown options and a control-character label");
    }

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan? limit = null)
    {
        long deadline = Environment.TickCount64 + (long)(limit ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(50);
        }
        return true;
    }

    internal sealed record ToolOutcome(bool IsError, string Text);

    /// <summary>The Claude Code side of the stdio transport: requests by ID, notifications in arrival order.</summary>
    private sealed class SyntheticChannelClient : IAsyncDisposable
    {
        private readonly Pipe input = new();
        private readonly Pipe output = new();
        private readonly Stream bridgeInput;
        private readonly Stream bridgeOutput;
        private readonly StreamReader reader;
        private readonly Task<int> bridge;
        private readonly Task reading;
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
        private readonly Channel<JsonElement> notifications = Channel.CreateUnbounded<JsonElement>();
        private readonly StringBuilder transcript = new();
        private int nextId;
        internal string Transcript { get { lock (transcript) return transcript.ToString(); } }

        internal SyntheticChannelClient(ChannelBridgeOptions options)
        {
            bridgeInput = input.Reader.AsStream();
            bridgeOutput = output.Writer.AsStream();
            reader = new StreamReader(output.Reader.AsStream(), new UTF8Encoding(false, true));
            bridge = McpChannelBridge.RunAsync(options, bridgeInput, bridgeOutput, new ChannelBridgeTuning(WaitSeconds: 2, PollMilliseconds: 500, RenewMilliseconds: 3000), default);
            reading = Task.Run(ReadAsync);
        }

        private async Task ReadAsync()
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (transcript) transcript.AppendLine(line);
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                {
                    TaskCompletionSource<JsonElement>? waiter;
                    lock (pending) pending.Remove(id.GetInt32(), out waiter);
                    waiter?.TrySetResult(root);
                }
                else notifications.Writer.TryWrite(root);
            }
        }

        internal async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            int id = Interlocked.Increment(ref nextId);
            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) pending[id] = waiter;
            await input.Writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await input.Writer.WriteAsync("\n"u8.ToArray());
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        internal async Task NotifyAsync(string method)
        {
            await input.Writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method }));
            await input.Writer.WriteAsync("\n"u8.ToArray());
        }

        internal async Task<ToolOutcome> CallToolAsync(string name, object arguments)
        {
            var result = (await RequestAsync("tools/call", new { name, arguments })).GetProperty("result");
            return new(result.TryGetProperty("isError", out var error) && error.GetBoolean(),
                result.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        }

        internal async Task<JsonElement?> NextNotificationAsync(TimeSpan limit)
        {
            if (notifications.Reader.TryRead(out var ready)) return ready;
            if (limit <= TimeSpan.Zero) return null;
            using var deadline = new CancellationTokenSource(limit);
            try { return await notifications.Reader.ReadAsync(deadline.Token); }
            catch (OperationCanceledException) { return null; }
        }

        internal async Task<int> CloseAsync()
        {
            await input.Writer.CompleteAsync();
            return await bridge.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await input.Writer.CompleteAsync();
            try { await bridge.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                await output.Writer.CompleteAsync();
                try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is TimeoutException or IOException) { }
                bridgeInput.Dispose(); bridgeOutput.Dispose(); reader.Dispose();
            }
        }
    }
}
