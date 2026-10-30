using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBroker.Core;
using AgentBroker.Worker;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// Lane broker-163. N-broker-compact-receipts-01: the sender's tools return the receipt without the message text, which every
// receipt poll used to hand back (up to 16 KiB). N-broker-busy-visibility-01: a receipt tells its own age (readAt) and, while
// its native recipient keeps answering that a turn is still running, since when (busySince); such a row discovery calls
// Unknown is listed Busy. Each label names the rule a mutation of SenderReceipt, BrokerTools or DeliveryObservations breaks.
internal static class CompactReceiptChecks
{
    // Non-ASCII on purpose: its UTF-8 length differs from its character count.
    private const string Text = "Synthetic compact receipt text, café ✓.";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        // Age first: the compact checks' retry comparison also depends on readAt, and READ_AT_NOW names that rule.
        await AgeAsync(check);
        await CompactAsync(check);
        ObservationRules(check);
        await BusyDeliveryAsync(check);
    }

    private static JsonObject FirstBlock(IEnumerable<ContentBlock> blocks) =>
        JsonNode.Parse(((TextContentBlock)blocks.First()).Text)!.AsObject();

    private static async Task CompactAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("compact-receipts");
        var tools = fixture.Tools;
        var sender = fixture.Register("Synthetic compact sender");
        var recipient = fixture.Register("Synthetic compact recipient");
        Guid id = Guid.NewGuid();
        var sent = FirstBlock(tools.SendTool(sender.Address, recipient.Address, id, Text));
        var message = sent["message"]?.AsObject();
        check(message is not null && !message.ContainsKey("text") && message["id"]?.GetValue<Guid>() == id &&
            message["recipient"]?["sessionId"]?.GetValue<string>() == recipient.Address.SessionId,
            "COMPACT_SEND_NO_TEXT: send's receipt keeps the message's ids and addresses and leaves out its text");
        check(sent["textBytes"]?.GetValue<int>() == Encoding.UTF8.GetByteCount(Text) && Encoding.UTF8.GetByteCount(Text) != Text.Length,
            "COMPACT_TEXT_BYTES: textBytes is the text's length in UTF-8 bytes");
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        var retry = FirstBlock(tools.SendTool(sender.Address, recipient.Address, id, Text));
        bool readMoved = retry["readAt"]?.GetValue<DateTimeOffset>() == sent["readAt"]!.GetValue<DateTimeOffset>().AddSeconds(3);
        retry.Remove("readAt"); sent.Remove("readAt");
        check(readMoved && JsonNode.DeepEquals(retry, sent),
            "COMPACT_SEND_RETRY: a retry returns the same compact receipt, only its read time differs");

        var now = fixture.Clock.GetUtcNow();
        var record = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address, Text, now, now.AddMinutes(5));
        var legacy = tools.SendMessageTool(record);
        check(legacy.Message.Id == record.Id && legacy.Message.Text is null && legacy.TextBytes == Encoding.UTF8.GetByteCount(Text),
            "COMPACT_SEND_MESSAGE: send_message returns the compact receipt too");
        var reply = tools.ReplyTool(id, Guid.NewGuid(), Text);
        check(reply.Message.ReplyTo == id && reply.Message.Text is null && reply.TextBytes is > 0,
            "COMPACT_REPLY: reply_to_message returns the reply's compact receipt");
        var waited = await tools.WaitForReceipt(id, default, timeoutSeconds: 0);
        check(waited.Message.Id == id && waited.Message.Text is null && waited.Status == MessageStatus.Accepted,
            "COMPACT_WAIT_NO_TEXT: wait_for_receipt returns the receipt without its text");
        check(tools.GetMessageReceipt(id) is { Message.Text: null, TextBytes: > 0 },
            "COMPACT_GET_DEFAULT: get_message_receipt leaves the text out unless asked");
        check(tools.GetMessageReceipt(id, includeText: true)?.Message.Text == Text,
            "COMPACT_GET_INCLUDE: get_message_receipt with includeText returns the text");
        string json = JsonSerializer.Serialize(tools.GetMessageReceipt(id), McpJsonUtilities.DefaultOptions);
        check(!json.Contains("\"text\"", StringComparison.Ordinal) && !json.Contains("busySince", StringComparison.Ordinal) &&
            json.Contains("\"readAt\"", StringComparison.Ordinal),
            "COMPACT_JSON_OMITS: the serialized receipt has no text member and no busySince while nothing was seen busy");

        var inbox = tools.ReadInbox(recipient.Address);
        var incoming = await tools.WaitForMessages(recipient.Address, default, timeoutSeconds: 0);
        var replies = await tools.WaitForReply(id, default, timeoutSeconds: 0);
        check(inbox.Any(r => r.Message.Id == id && r.Message.Text == Text) && incoming.Any(r => r.Message.Id == id && r.Message.Text == Text) &&
            replies.Count == 1 && replies[0].Message.Text == Text,
            "COMPACT_READERS_KEEP_TEXT: read_inbox, wait_for_messages and wait_for_reply still carry the text");
    }

    // A receipt's own fields tell its age: readAt is the broker's clock at the read, and a Submitted receipt's updatedAt
    // stays the time it was submitted (two others sat Submitted / native_admission_unconfirmed for two hours).
    private static async Task AgeAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("compact-age");
        var sender = fixture.Register("Synthetic age sender");
        var native = fixture.Native("codex:" + Guid.NewGuid().ToString("D"));
        fixture.Store.Register(native);
        Guid id = Guid.NewGuid();
        fixture.Tools.Send(sender.Address, native.Address, id, Text);
        check(fixture.Store.BeginDelivery()?.Message.Id == id, "The synthetic native message is claimed.");
        fixture.Store.CompleteDelivery(id, MessageStatus.Submitted, "native_admission_unconfirmed");
        var submittedAt = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        check(fixture.Tools.GetMessageReceipt(id)?.ReadAt == submittedAt.AddSeconds(1), "READ_AT_NOW: readAt is the broker's clock at the read");
        fixture.Clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        var later = fixture.Tools.GetMessageReceipt(id);
        check(later is { Status: MessageStatus.Submitted } && later.UpdatedAt == submittedAt && later.ReadAt - later.UpdatedAt == TimeSpan.FromHours(2),
            "READ_AT_AGE: two hours later a Submitted receipt reads readAt minus updatedAt of two hours");
    }

    private static void ObservationRules(Action<bool, string> check)
    {
        var clock = new ToolFixture.ManualClock();
        var observations = new DeliveryObservations(clock);
        var recipient = new SessionAddress(Guid.NewGuid(), "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var created = clock.GetUtcNow();
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
            new(recipient.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), recipient, Text, created, created.AddHours(1));
        MessageReceipt Busy() => new(message, MessageStatus.Accepted, clock.GetUtcNow(), DeliveryObservations.BusyReason);
        var busy = new AdapterResult(AdapterAdmission.DeferredNotSubmitted, DeliveryObservations.BusyReason);
        var first = clock.GetUtcNow();
        observations.Record(message, busy, _ => true);
        clock.Advance(TimeSpan.FromMinutes(5));
        observations.Record(message, busy, _ => true);
        check(observations.BusySince(Busy()) == first, "BUSY_SINCE_KEPT: later busy answers keep the first one's time");
        check(observations.SeenBusy(recipient), "BUSY_ROW_FRESH: a recipient that answered busy just now is seen busy");
        clock.Advance(DeliveryObservations.BusyFreshFor);
        check(!observations.SeenBusy(recipient), "BUSY_ROW_FRESHNESS: a busy answer older than 30 seconds no longer counts");
        observations.Record(message, new(AdapterAdmission.DeferredNotSubmitted, "native_owner_unavailable"), _ => true);
        clock.Advance(TimeSpan.FromSeconds(1));
        var restarted = clock.GetUtcNow();
        observations.Record(message, busy, _ => true);
        check(observations.BusySince(Busy()) == restarted, "BUSY_RUN_RESTARTS: another answer ends the busy run, so the next busy answer starts a new one");
        check(observations.BusySince(Busy() with { Status = MessageStatus.Submitted }) is null &&
            observations.BusySince(Busy() with { Reason = "native_owner_unavailable" }) is null,
            "BUSY_SINCE_ONLY_WHILE_BUSY: busySince is shown only while the receipt itself still says native_context_busy");
    }

    // Through the started runtime's real delivery loop: a native recipient that keeps answering busy.
    private static async Task BusyDeliveryAsync(Action<bool, string> check)
    {
        var adapter = new BusyAdapter();
        await using var fixture = new ToolFixture("compact-busy", adapters: [adapter]);
        var sender = fixture.Register("Synthetic busy sender");
        // Unknown with the native idle guard is Ready (BrokerStore.Ready), as the live Codex row was: discovery knew no activity.
        var row = new BrokerSession(new(fixture.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Synthetic busy row",
            adapter.Id, SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply | SessionCapabilities.NativeIdleGuard,
            SessionActivity.Unknown, fixture.Clock.GetUtcNow().AddMinutes(2));
        fixture.Store.Register(row);
        Guid id = Guid.NewGuid();
        fixture.Tools.Send(sender.Address, row.Address, id, Text);
        using var stop = new CancellationTokenSource();
        fixture.Runtime.Start(stop.Token);
        async Task<bool> Attempts(int count)
        {
            for (int i = 0; i < 200 && adapter.Attempts < count; i++) await Task.Delay(50);
            for (int i = 0; i < 40 && fixture.Store.FindReceipt(id)?.Status == MessageStatus.Submitting; i++) await Task.Delay(25);
            return adapter.Attempts >= count;
        }
        var firstSeen = fixture.Clock.GetUtcNow();
        bool attempted = await Attempts(1);
        var view = fixture.Tools.GetMessageReceipt(id);
        check(attempted && view is { Status: MessageStatus.Accepted, Reason: "native_context_busy" } && view.BusySince == firstSeen,
            "BUSY_SINCE_RECORDED: a receipt deferred native_context_busy says since when its recipient has been seen busy");
        var listed = (await fixture.Tools.ListSessions(default)).Single(session => session.Address == row.Address);
        check(listed.Activity == SessionActivity.Busy && fixture.Current(row.Address)?.Activity == SessionActivity.Unknown,
            "BUSY_ROW_PRESENTED: list_sessions shows the Unknown row Busy while the stored activity, which decides delivery, stays Unknown");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Store.Register(row with { LeaseUntil = fixture.Clock.GetUtcNow().AddMinutes(2) });
        bool again = await Attempts(2);
        var later = fixture.Tools.GetMessageReceipt(id);
        check(again && later is { Reason: "native_context_busy" } && later.BusySince == firstSeen && later.UpdatedAt > firstSeen,
            "BUSY_SINCE_DELIVERY_KEPT: the next busy deferral moves updatedAt but keeps busySince");
        adapter.Answer = new(AdapterAdmission.Submitted, "native_admission_unconfirmed");
        bool third = await Attempts(3);
        var submitted = fixture.Tools.GetMessageReceipt(id);
        var after = (await fixture.Tools.ListSessions(default)).Single(session => session.Address == row.Address);
        check(third && submitted is { Status: MessageStatus.Submitted, BusySince: null } && after.Activity == SessionActivity.Unknown,
            "BUSY_ROW_ENDS: an admitted attempt ends the busy run: no busySince, and the row is Unknown again");
        adapter.Answer = new(AdapterAdmission.DeferredNotSubmitted, "native_context_busy");
        // Review of 25a0c65, finding 2: a paired broker that sent a message to the busy row reads its receipt without
        // busySince, a local read of the same receipt keeps it.
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic busy peer", "https://192.0.2.31/", new string('D', 64), fixture.Clock.GetUtcNow());
        fixture.Store.AuthorizePeer(peer);
        var sent = fixture.Clock.GetUtcNow();
        var fromPeer = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, new(peer.BrokerId, "remote", Guid.NewGuid()), row.Address,
            "Synthetic busy message from a paired broker.", sent, sent.AddHours(2));
        fixture.Store.Accept(fromPeer, peer.BrokerId);
        for (int i = 0; i < 300 && fixture.Store.FindReceipt(fromPeer.Id)?.Reason != "native_context_busy"; i++) await Task.Delay(50);
        var localRead = fixture.Tools.GetMessageReceipt(fromPeer.Id);
        fixture.Context.Items["agentbroker.identity"] = peer.BrokerId;
        var peerRead = fixture.Tools.GetMessageReceipt(fromPeer.Id);
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        check(localRead?.BusySince is not null && peerRead is { Reason: "native_context_busy", BusySince: null },
            "BUSY_SINCE_NOT_FEDERATED: a paired broker's caller reads the busy receipt without busySince; a local read keeps it");
        stop.Cancel();
    }

    private sealed class BusyAdapter : ILocalAgentAdapter
    {
        private int attempts;
        internal int Attempts => Volatile.Read(ref attempts);
        internal volatile AdapterResult Answer = new(AdapterAdmission.DeferredNotSubmitted, "native_context_busy");
        public string Id => "synthetic-busy";
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token) => Task.FromResult<IReadOnlyList<BrokerSession>>([]);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            var answer = Answer;
            Interlocked.Increment(ref attempts);
            return Task.FromResult(answer);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
