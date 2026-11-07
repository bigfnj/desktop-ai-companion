using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

internal static class PairedReceiptReasonChecks
{
    internal static async Task RunAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: PAIRED_RECEIPT_REASONS_EXECUTED");
        await using var a = await Node.Create(Path.Combine(scratch, "receipt-reasons-a"), TimeProvider.System, identity);
        await using var b = await Node.Create(Path.Combine(scratch, "receipt-reasons-b"), TimeProvider.System, identity);
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        await b.Federation.ApprovePairAsync(b.Federation.PairingOffers.Single().RequestId, default);
        await a.Federation.ApprovePairAsync(a.Federation.PairingOffers.Single().RequestId, default);
        var trust = a.Store.Peers().Single();
        var sender = a.Register("Synthetic receipt reason sender") with { Activity = SessionActivity.Busy,
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2) };
        a.Store.Register(sender);
        BrokerSession Native() => new(new(b.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            "Synthetic exact replacement", "codex-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(2));
        void Discover(BrokerSession row) { b.Store.Register(row); b.Store.ObserveNativeSession(row.Address, row.Adapter); }
        BrokerMessage Message(SessionAddress target) => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, target,
            "Synthetic exact-message refusal control.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
        async Task<MessageReceipt> Until(Guid id, MessageStatus status)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                var row = a.Store.FindReceipt(id);
                if (row?.Status == status) return row;
                await Task.Delay(25);
            }
            throw new TimeoutException("Synthetic receipt reason did not reach its expected status.");
        }
        await using var runtime = new BrokerRuntime(a.Store, a.Federation, []);
        runtime.Start(default);

        var old = Native(); Discover(old);
        var immediate = Message(old.Address);
        await a.Federation.SubmitAsync(trust, immediate, default);
        var successor = old with { Address = old.Address with { Incarnation = Guid.NewGuid() } };
        Discover(successor); b.Store.BeginDelivery();
        a.Store.Accept(immediate, a.Store.BrokerId);
        var first = await Until(immediate.Id, MessageStatus.Refused);
        check(first.Reason == "recipient_replaced" && first.Message.Recipient == old.Address &&
            b.Store.ReadPendingInbox(successor.Address).PendingCount == 0,
            "PAIRED_REPLACEMENT_IMMEDIATE: an authenticated exact-message retry preserves the peer's pre-input replacement reason without successor delivery");

        old = Native(); Discover(old);
        var reconciled = Message(old.Address); a.Store.Accept(reconciled, a.Store.BrokerId);
        await Until(reconciled.Id, MessageStatus.Submitted);
        successor = old with { Address = old.Address with { Incarnation = Guid.NewGuid() } };
        Discover(successor); b.Store.BeginDelivery();
        var later = await Until(reconciled.Id, MessageStatus.Refused);
        check(later.Reason == "recipient_replaced" && later.Message.Recipient == old.Address &&
            b.Store.ReadPendingInbox(successor.Address).PendingCount == 0,
            "PAIRED_REPLACEMENT_RECONCILE: actual authenticated receipt reconciliation preserves the exact predecessor refusal");
        check(new[] { immediate.Id, reconciled.Id }.All(id => a.Store.Messages(sender.Address).Any(row =>
            row.Message.Sender.SessionId == BrokerStore.NoticeSessionId && row.Message.Text.Contains(id.ToString(), StringComparison.Ordinal) &&
            row.Message.Text.Contains("fresh messageId", StringComparison.Ordinal))),
            "PAIRED_REPLACEMENT_NOTICES: both receipt paths use the existing exact-sender replacement notice guidance");

        foreach (string? reason in new[] { "unknown_future_code", "bad\nprivate_reason", new string('x', 81), null })
        {
            b.Pairing.BeforeMessages = async context =>
            {
                if (b.Federation.AuthenticatePeer(context) != a.Store.BrokerId) throw new UnauthorizedAccessException();
                var message = await context.Request.ReadFromJsonAsync<BrokerMessage>() ?? throw new InvalidDataException();
                await context.Response.WriteAsJsonAsync(new MessageReceipt(message, MessageStatus.Refused, DateTimeOffset.UtcNow, reason));
                return true;
            };
            var unknown = Message(successor.Address); a.Store.Accept(unknown, a.Store.BrokerId);
            check((await Until(unknown.Id, MessageStatus.Refused)).Reason == "peer_receipt",
                "PAIRED_RECEIPT_REASON_FALLBACK: unknown, malformed, oversized and absent reasons keep the fixed immediate fallback");
        }
        b.Pairing.BeforeMessages = async context =>
        {
            if (b.Federation.AuthenticatePeer(context) != a.Store.BrokerId) throw new UnauthorizedAccessException();
            var message = await context.Request.ReadFromJsonAsync<BrokerMessage>() ?? throw new InvalidDataException();
            await context.Response.WriteAsJsonAsync(new MessageReceipt(message with { Text = "Different synthetic payload." },
                MessageStatus.Refused, DateTimeOffset.UtcNow, "recipient_replaced"));
            return true;
        };
        var wrong = Message(successor.Address); a.Store.Accept(wrong, a.Store.BrokerId);
        check((await Until(wrong.Id, MessageStatus.Uncertain)).Reason == "submission_unconfirmed",
            "PAIRED_RECEIPT_IMMEDIATE_EXACT: replacement metadata never overrides a mismatched authenticated message");

        var pending = new ConcurrentDictionary<Guid, BrokerMessage>();
        b.Pairing.BeforeMessages = async context =>
        {
            if (b.Federation.AuthenticatePeer(context) != a.Store.BrokerId) throw new UnauthorizedAccessException();
            var message = await context.Request.ReadFromJsonAsync<BrokerMessage>() ?? throw new InvalidDataException();
            pending[message.Id] = message;
            await context.Response.WriteAsJsonAsync(new MessageReceipt(message, MessageStatus.Accepted, DateTimeOffset.UtcNow));
            return true;
        };
        var mismatch = Message(successor.Address); var sentinel = Message(successor.Address);
        b.Pairing.BeforeReceipt = async context =>
        {
            if (b.Federation.AuthenticatePeer(context) != a.Store.BrokerId) throw new UnauthorizedAccessException();
            if (!Guid.TryParse(context.Request.Path.Value?.Split('/').Last(), out var id) || !pending.TryGetValue(id, out var message))
            { context.Response.StatusCode = 404; return true; }
            await context.Response.WriteAsJsonAsync(new MessageReceipt(id == mismatch.Id
                ? message with { Text = "Different synthetic receipt payload." } : message,
                MessageStatus.Refused, DateTimeOffset.UtcNow, id == mismatch.Id ? "recipient_replaced" : "unknown_future_code"));
            return true;
        };
        a.Store.Accept(mismatch, a.Store.BrokerId); await Until(mismatch.Id, MessageStatus.Submitted);
        a.Store.Accept(sentinel, a.Store.BrokerId); await Until(sentinel.Id, MessageStatus.Submitted);
        var terminal = await Until(sentinel.Id, MessageStatus.Refused);
        check(terminal.Reason == "recipient_receipt" && a.Store.FindReceipt(mismatch.Id)?.Status == MessageStatus.Submitted,
            "PAIRED_RECEIPT_RECONCILE_EXACT: a later valid sentinel proves the loop rejected the earlier mismatched replacement receipt and used a fixed unknown-reason fallback");
    }
}
