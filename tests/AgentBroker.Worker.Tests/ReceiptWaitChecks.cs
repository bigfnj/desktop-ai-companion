using System.Diagnostics;
using AgentBroker.Core;
using ModelContextProtocol;

// wait_for_receipt (N-broker-receipts-01): a sender waits for its message's outcome instead of polling, because a
// recipient's acknowledgement never arrives in the sender's inbox. Timings are real; leases and deadlines move by the
// manual clock. Each label names the rule a mutation of WaitForReceipt, Answered or FindReceipt must break.
internal static class ReceiptWaitChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("receipt-wait");
        var tools = fixture.Tools;
        var sender = fixture.Register("Synthetic receipt sender");
        var recipient = fixture.Register("Synthetic receipt recipient");

        Guid delivered = Guid.NewGuid();
        tools.Send(sender.Address, recipient.Address, delivered, "Synthetic message the recipient acknowledges.");
        var waiting = tools.WaitForReceipt(delivered, default, timeoutSeconds: 20);
        await Task.WhenAny(waiting, Task.Delay(300));
        check(!waiting.IsCompleted, "RECEIPT_WAIT_BLOCKS: wait_for_receipt blocks while the message is only Accepted");
        var elapsed = Stopwatch.StartNew();
        tools.AcknowledgeMessage(delivered, recipient.Address, MessageStatus.Delivered);
        await Task.WhenAny(waiting, Task.Delay(5000));
        elapsed.Stop();
        check(waiting.IsCompletedSuccessfully && waiting.Result.Status == MessageStatus.Delivered && waiting.Result.Message.Id == delivered &&
            elapsed.Elapsed < TimeSpan.FromSeconds(2),
            "RECEIPT_WAIT_DELIVERED: the recipient's acknowledgement ends the sender's wait within two seconds");

        Guid refused = Guid.NewGuid();
        tools.Send(sender.Address, recipient.Address, refused, "Synthetic message the recipient refuses.");
        tools.AcknowledgeMessage(refused, recipient.Address, MessageStatus.Refused);
        elapsed.Restart();
        var answered = await tools.WaitForReceipt(refused, default, timeoutSeconds: 20).WaitAsync(TimeSpan.FromSeconds(5));
        elapsed.Stop();
        check(answered.Status == MessageStatus.Refused && elapsed.Elapsed < TimeSpan.FromSeconds(1),
            "RECEIPT_WAIT_AT_ONCE: an outcome that already exists is returned at once");

        Guid pending = Guid.NewGuid();
        tools.Send(sender.Address, recipient.Address, pending, "Synthetic message nobody acknowledges yet.");
        elapsed.Restart();
        AgentBroker.Worker.SenderReceipt? current = null;
        try { current = await tools.WaitForReceipt(pending, default, timeoutSeconds: 1); }
        catch (McpException) { /* A deadline must return the receipt; the check below names the failure. */ }
        elapsed.Stop();
        check(current is { Status: MessageStatus.Accepted } && current.Message.Id == pending &&
            elapsed.Elapsed >= TimeSpan.FromMilliseconds(900) && elapsed.Elapsed < TimeSpan.FromSeconds(3),
            "RECEIPT_WAIT_DEADLINE: at its deadline the wait returns the current Accepted receipt");

        // A deadline that passes during a wait: the next store read ages it, and that commit ends the wait.
        var expiring = tools.WaitForReceipt(pending, default, timeoutSeconds: 20);
        await Task.WhenAny(expiring, Task.Delay(300));
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        fixture.Runtime.RegisterPollingAgent(Guid.Parse(sender.Address.SessionId[4..]), sender.Address.Incarnation, sender.Label, false);
        fixture.Runtime.RegisterPollingAgent(Guid.Parse(recipient.Address.SessionId[4..]), recipient.Address.Incarnation, recipient.Label, false);
        elapsed.Restart();
        check(fixture.Store.FindReceipt(pending)?.Status == MessageStatus.Expired,
            "RECEIPT_AGES: a receipt read after its deadline is Expired without waiting for a delivery pass");
        await Task.WhenAny(expiring, Task.Delay(5000));
        elapsed.Stop();
        check(expiring.IsCompletedSuccessfully && expiring.Result.Status == MessageStatus.Expired && elapsed.Elapsed < TimeSpan.FromSeconds(2),
            "RECEIPT_WAIT_EXPIRED: an expiry during the wait ends it with Expired");

        Guid aged = Guid.NewGuid();
        tools.Send(sender.Address, recipient.Address, aged, "Synthetic message read only after its deadline.", ttlMinutes: 1);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        check(tools.GetMessageReceipt(aged)?.Status == MessageStatus.Expired,
            "RECEIPT_TOOL_AGES: get_message_receipt reports a passed deadline as Expired");

        // Submitted is transport admission, not an answer: the wait continues until the recipient's own receipt.
        var native = fixture.Native("codex:receipt-native");
        fixture.Store.Register(native);
        Guid submitted = Guid.NewGuid();
        tools.Send(sender.Address, native.Address, submitted, "Synthetic message a native adapter admits.");
        var claim = fixture.Store.BeginDelivery();
        check(claim?.Message.Id == submitted, "The native message is claimed for submission.");
        fixture.Store.CompleteDelivery(submitted, MessageStatus.Submitted, "native_write_unconfirmed");
        elapsed.Restart();
        var transport = await tools.WaitForReceipt(submitted, default, timeoutSeconds: 1);
        elapsed.Stop();
        check(transport.Status == MessageStatus.Submitted && elapsed.Elapsed >= TimeSpan.FromMilliseconds(900),
            "RECEIPT_SUBMITTED_NOT_ANSWER: a Submitted receipt does not end the wait");
        var afterAck = tools.WaitForReceipt(submitted, default, timeoutSeconds: 20);
        fixture.Store.Register(native with { LeaseUntil = fixture.Clock.GetUtcNow().AddMinutes(1) });
        fixture.Store.RecordRecipientReceipt(submitted, native.Address, fixture.Store.BrokerId, MessageStatus.Delivered);
        check((await afterAck.WaitAsync(TimeSpan.FromSeconds(5))).Status == MessageStatus.Delivered,
            "RECEIPT_WAIT_AFTER_SUBMITTED: the recipient's receipt after a submission ends the wait");

        // One wait per session: the receipt wait shares the sender's slot with its other waits. A fresh sender, because
        // the first one's inbox now holds delivery notices, which would end its wait_for_messages at once.
        var slotSender = fixture.Register("Synthetic slot sender");
        Guid slot = Guid.NewGuid();
        tools.Send(slotSender.Address, recipient.Address, slot, "Synthetic message for the wait slot.");
        using (var cancel = new CancellationTokenSource())
        {
            var holding = tools.WaitForMessages(slotSender.Address, cancel.Token, timeoutSeconds: 20);
            bool busy = await ToolFixture.RefusedAsync(() => tools.WaitForReceipt(slot, default, timeoutSeconds: 0), "wait_in_progress");
            cancel.Cancel();
            try { await holding.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
            check(busy, "RECEIPT_WAIT_SLOT: a receipt wait beside another wait of the same sender is wait_in_progress");
        }

        // The wait wakes at the message's own deadline. This fixture never starts the delivery loops and nothing else reads
        // or commits, which is the state Codex 01a18357 reproduced with all four loops busy; only the wake can end the wait.
        await using (var real = new ToolFixture("receipt-wait-real-clock", clock: TimeProvider.System))
        {
            var from = real.Register("Synthetic real-clock sender"); var to = real.Register("Synthetic real-clock recipient");
            var now = DateTimeOffset.UtcNow;
            var brief = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, from.Address, to.Address,
                "Synthetic message that expires during the wait.", now, now.AddMilliseconds(1500));
            real.Store.Accept(brief, real.Store.BrokerId);
            elapsed.Restart();
            var woke = await real.Tools.WaitForReceipt(brief.Id, default, timeoutSeconds: 10);
            elapsed.Stop();
            check(woke.Status == MessageStatus.Expired && elapsed.Elapsed < TimeSpan.FromSeconds(4),
                $"RECEIPT_WAKES_AT_DEADLINE: a waiting wait_for_receipt wakes at the message's own deadline ({elapsed.Elapsed.TotalSeconds:F2} s for a 1.5 s deadline, request timeout 10 s)");
        }

        // A deadline that passes between the wait's reread and its wake calculation still ends the wait at once. The clock jumps
        // past the deadline on the first read that comes through the tools but not the store, which is exactly that wake
        // calculation (Codex 01a18357's BoundaryClock, review of 5c0528f).
        var boundary = new BoundaryClock();
        await using (var straddle = new ToolFixture("receipt-wait-straddle", clock: boundary))
        {
            var from = straddle.Register("Synthetic straddle sender"); var to = straddle.Register("Synthetic straddle recipient");
            var start = boundary.GetUtcNow();
            var edge = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, from.Address, to.Address,
                "Synthetic message whose deadline passes inside one wait pass.", start, start.AddSeconds(1));
            straddle.Store.Accept(edge, straddle.Store.BrokerId);
            boundary.Arm(edge.ExpiresAt.AddTicks(1));
            elapsed.Restart();
            var straddled = await straddle.Tools.WaitForReceipt(edge.Id, default, timeoutSeconds: 5);
            elapsed.Stop();
            check(boundary.Advanced && straddled.Status == MessageStatus.Expired && elapsed.Elapsed < TimeSpan.FromSeconds(2),
                $"RECEIPT_WAKE_STRADDLE: a deadline passing between the reread and the wake calculation ends the wait at once ({elapsed.Elapsed.TotalSeconds:F2} s, request timeout 5 s)");
        }

        bool unknown = false;
        try { await tools.WaitForReceipt(Guid.NewGuid(), default, timeoutSeconds: 0); }
        catch (McpException error) { unknown = error.Message == "The original message is unavailable."; }
        check(unknown, "RECEIPT_WAIT_UNKNOWN: an unknown message ID is refused, never waited on");
        bool ranges = true;
        foreach (int timeout in new[] { -1, 26 })
        {
            try { await tools.WaitForReceipt(slot, default, timeoutSeconds: timeout); ranges = false; }
            catch (McpException) { }
        }
        check(ranges, "RECEIPT_WAIT_RANGE: timeoutSeconds outside 0..25 is refused");
    }

    // Holds a fixed time until armed, then jumps to the target on the first read made through BrokerTools but not
    // BrokerStore: the wake calculation, which reads the runtime clock right after the store reread.
    private sealed class BoundaryClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero).Ticks;
        private long target;
        private bool armed;
        public bool Advanced { get; private set; }
        public void Arm(DateTimeOffset afterDeadline) { target = afterDeadline.Ticks; armed = true; }
        public override DateTimeOffset GetUtcNow()
        {
            if (armed && !Advanced)
            {
                var frames = new StackTrace().GetFrames();
                bool throughStore = frames.Any(frame => Within(frame.GetMethod()?.DeclaringType, typeof(BrokerStore)));
                bool throughTools = frames.Any(frame => Within(frame.GetMethod()?.DeclaringType, typeof(AgentBroker.Worker.BrokerTools)));
                if (throughTools && !throughStore) { Interlocked.Exchange(ref ticks, target); Advanced = true; }
            }
            return new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        }
        private static bool Within(Type? type, Type expected)
        {
            while (type is not null) { if (type == expected) return true; type = type.DeclaringType; }
            return false;
        }
    }
}
