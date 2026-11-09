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
        var waiting = fixture.NewRequest().Tools.WaitForReceipt(delivered, default, timeoutSeconds: 20);
        await Task.WhenAny(waiting, Task.Delay(300));
        check(!waiting.IsCompleted, "RECEIPT_WAIT_BLOCKS: wait_for_receipt blocks while the message is only Accepted");
        var elapsed = Stopwatch.StartNew();
        tools.AcknowledgeMessage(delivered, recipient.Address, MessageStatus.Delivered);
        long acknowledgementMilliseconds = elapsed.ElapsedMilliseconds;
        // Measure the wait's reaction after the durable acknowledgement returns, independently of disk commit latency.
        elapsed.Restart();
        await Task.WhenAny(waiting, Task.Delay(5000));
        elapsed.Stop();
        Console.WriteLine($"NOTE RECEIPT_WAIT_DELIVERED acknowledgementMs={acknowledgementMilliseconds} wakeMs={elapsed.ElapsedMilliseconds} completed={waiting.IsCompletedSuccessfully}");
        check(waiting.IsCompletedSuccessfully && waiting.Result.Status == MessageStatus.Delivered && waiting.Result.Message.Id == delivered &&
            elapsed.Elapsed < TimeSpan.FromSeconds(2),
            "RECEIPT_WAIT_DELIVERED: the recipient's acknowledgement ends the sender's wait within two seconds");

        Guid refused = Guid.NewGuid();
        tools.Send(sender.Address, recipient.Address, refused, "Synthetic message the recipient refuses.");
        tools.AcknowledgeMessage(refused, recipient.Address, MessageStatus.Refused);
        elapsed.Restart();
        var answered = await fixture.NewRequest().Tools.WaitForReceipt(refused, default, timeoutSeconds: 20).WaitAsync(TimeSpan.FromSeconds(5));
        elapsed.Stop();
        check(answered.Status == MessageStatus.Refused && elapsed.Elapsed < TimeSpan.FromSeconds(1),
            "RECEIPT_WAIT_AT_ONCE: an outcome that already exists is returned at once");

        Guid pending = Guid.NewGuid();
        tools.Send(sender.Address, recipient.Address, pending, "Synthetic message nobody acknowledges yet.", ttlMinutes: 1);
        elapsed.Restart();
        AgentBroker.Worker.SenderReceipt? current = null;
        try { current = await fixture.NewRequest().Tools.WaitForReceipt(pending, default, timeoutSeconds: 1); }
        catch (McpException) { /* A deadline must return the receipt; the check below names the failure. */ }
        elapsed.Stop();
        check(current is { Status: MessageStatus.Accepted } && current.Message.Id == pending &&
            elapsed.Elapsed >= TimeSpan.FromMilliseconds(900) && elapsed.Elapsed < TimeSpan.FromSeconds(3),
            "RECEIPT_WAIT_DEADLINE: at its deadline the wait returns the current Accepted receipt");

        // A deadline that passes during a wait: the next store read ages it, and that commit ends the wait.
        var expiring = fixture.NewRequest().Tools.WaitForReceipt(pending, default, timeoutSeconds: 20);
        await Task.WhenAny(expiring, Task.Delay(300));
        var expirySignal = fixture.Store.ChangeSignal();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.Runtime.RegisterPollingAgent(Guid.Parse(sender.Address.SessionId[4..]), sender.Address.Incarnation, sender.Label, false);
        fixture.Runtime.RegisterPollingAgent(Guid.Parse(recipient.Address.SessionId[4..]), recipient.Address.Incarnation, recipient.Label, false);
        check(!expirySignal.IsCompleted,
            "RECEIPT_EXPIRY_SIGNAL_ISOLATED: ordinary mailbox renewal before retention ends does not mask the expiry commit's signal");
        elapsed.Restart();
        check(fixture.Store.FindReceipt(pending)?.Status == MessageStatus.Expired,
            "RECEIPT_AGES: a receipt read after its deadline is Expired without waiting for a delivery pass");
        long expiryCommitMilliseconds = elapsed.ElapsedMilliseconds;
        elapsed.Restart();
        await Task.WhenAny(expiring, Task.Delay(5000));
        elapsed.Stop();
        Console.WriteLine($"NOTE RECEIPT_WAIT_EXPIRED expiryCommitMs={expiryCommitMilliseconds} wakeMs={elapsed.ElapsedMilliseconds} completed={expiring.IsCompletedSuccessfully}");
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
        fixture.Store.CompleteDelivery(fixture.Store.FindReceipt(submitted)!, MessageStatus.Submitted, "native_write_unconfirmed");
        elapsed.Restart();
        var transport = await fixture.NewRequest().Tools.WaitForReceipt(submitted, default, timeoutSeconds: 1);
        elapsed.Stop();
        check(transport.Status == MessageStatus.Submitted && elapsed.Elapsed >= TimeSpan.FromMilliseconds(900),
            "RECEIPT_SUBMITTED_NOT_ANSWER: a Submitted receipt does not end the wait");
        var afterAck = fixture.NewRequest().Tools.WaitForReceipt(submitted, default, timeoutSeconds: 20);
        fixture.Store.Register(native with { LeaseUntil = fixture.Clock.GetUtcNow().AddMinutes(1) });
        fixture.Store.RecordRecipientReceipt(submitted, native.Address, fixture.Store.BrokerId, MessageStatus.Delivered);
        check((await afterAck.WaitAsync(TimeSpan.FromSeconds(5))).Status == MessageStatus.Delivered,
            "RECEIPT_WAIT_AFTER_SUBMITTED: the recipient's receipt after a submission ends the wait");

        // One wait per session and class: the receipt wait has its own class, so it runs beside the sender's incoming wait,
        // while a second receipt wait, or a reply wait beside the message wait, is refused. A fresh sender, because the
        // first one's inbox now holds delivery notices, which would end its wait_for_messages at once.
        var slotSender = fixture.Register("Synthetic slot sender");
        Guid slot = Guid.NewGuid();
        tools.Send(slotSender.Address, recipient.Address, slot, "Synthetic message for the wait slot.");
        using (var cancel = new CancellationTokenSource())
        using (var cancelReceipt = new CancellationTokenSource())
        {
            var holding = fixture.NewRequest().Tools.WaitForMessages(slotSender.Address, cancel.Token, timeoutSeconds: 20);
            // Asked while ONLY the incoming wait is held, so a reply wait moved to any other class is admitted and fails this.
            bool replyBeside = await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForReply(slot, default, timeoutSeconds: 0), "wait_in_progress");
            var receiptHold = fixture.NewRequest().Tools.WaitForReceipt(slot, cancelReceipt.Token, timeoutSeconds: 20);
            await Task.WhenAny(receiptHold, Task.Delay(300));
            bool beside = !receiptHold.IsCompleted && !holding.IsCompleted;
            bool secondReceipt = await ToolFixture.RefusedAsync(() => fixture.NewRequest().Tools.WaitForReceipt(slot, default, timeoutSeconds: 0), "wait_in_progress");
            cancelReceipt.Cancel();
            await Ended(receiptHold);
            bool receiptReleased = false;
            try { receiptReleased = (await fixture.NewRequest().Tools.WaitForReceipt(slot, default, timeoutSeconds: 0)).Message.Id == slot && !holding.IsCompleted; }
            catch (McpException) { /* Still held: the check below reports it. */ }
            cancel.Cancel();
            await Ended(holding);
            check(beside, "RECEIPT_WAIT_BESIDE_INCOMING: a receipt wait runs beside the same sender's wait_for_messages");
            check(secondReceipt, "RECEIPT_WAIT_SLOT: a second receipt wait of the same sender is wait_in_progress");
            check(replyBeside, "REPLY_WAIT_SHARES_INCOMING: a reply wait beside the same sender's wait_for_messages is wait_in_progress");
            check(receiptReleased, "WITNESS an ended receipt wait releases its class while the incoming wait continues");
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
            var woke = await real.NewRequest().Tools.WaitForReceipt(brief.Id, default, timeoutSeconds: 10);
            elapsed.Stop();
            Console.WriteLine($"NOTE RECEIPT_WAKES_AT_DEADLINE elapsedSeconds={elapsed.Elapsed.TotalSeconds:F2}; deadlineSeconds=1.5; timeoutSeconds=10");
            check(woke.Status == MessageStatus.Expired && elapsed.Elapsed < TimeSpan.FromSeconds(4),
                "RECEIPT_WAKES_AT_DEADLINE: a waiting wait_for_receipt wakes at the message's own deadline");
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
            var straddled = await straddle.NewRequest().Tools.WaitForReceipt(edge.Id, default, timeoutSeconds: 5);
            elapsed.Stop();
            Console.WriteLine($"NOTE RECEIPT_WAKE_STRADDLE elapsedSeconds={elapsed.Elapsed.TotalSeconds:F2}; timeoutSeconds=5");
            check(boundary.Advanced && straddled.Status == MessageStatus.Expired && elapsed.Elapsed < TimeSpan.FromSeconds(2),
                "RECEIPT_WAKE_STRADDLE: a deadline passing between the reread and the wake calculation ends the wait at once");
        }

        bool unknown = false;
        try { await fixture.NewRequest().Tools.WaitForReceipt(Guid.NewGuid(), default, timeoutSeconds: 0); }
        catch (McpException error) { unknown = error.Message == "The original message is unavailable."; }
        check(unknown, "RECEIPT_WAIT_UNKNOWN: an unknown message ID is refused, never waited on");
        bool ranges = true;
        foreach (int timeout in new[] { -1, 26 })
        {
            try { await fixture.NewRequest().Tools.WaitForReceipt(slot, default, timeoutSeconds: timeout); ranges = false; }
            catch (McpException) { }
        }
        check(ranges, "RECEIPT_WAIT_RANGE: timeoutSeconds outside 0..25 is refused");

        await AnswerStatusesAsync(check);
    }

    // N1 (AgentBroker next campaign, hygiene lane 2026-11-08): Uncertain and Held are answers, so a waiting sender gets them at
    // once; and a Submitted message ages to Uncertain at its own deadline. No label covered either before.
    private static async Task AnswerStatusesAsync(Action<bool, string> check)
    {
        await using (var fixture = new ToolFixture("receipt-answer-statuses"))
        {
            var tools = fixture.Tools;
            var sender = fixture.Register("Synthetic answer sender");
            var mailbox = fixture.Register("Synthetic answer mailbox");
            var native = fixture.Native("codex:receipt-answer-native");
            fixture.Store.Register(native);
            Guid uncertain = Guid.NewGuid();
            tools.Send(sender.Address, native.Address, uncertain, "Synthetic message whose native submission is uncertain.");
            var claim = fixture.Store.BeginDelivery();
            if (claim?.Message.Id != uncertain) throw new InvalidOperationException("Synthetic native message was not claimed.");
            fixture.Store.CompleteDelivery(claim, MessageStatus.Uncertain, "synthetic_uncertain");
            var elapsed = Stopwatch.StartNew();
            var answer = await fixture.NewRequest().Tools.WaitForReceipt(uncertain, default, timeoutSeconds: 20).WaitAsync(TimeSpan.FromSeconds(25));
            elapsed.Stop();
            check(answer.Status == MessageStatus.Uncertain && answer.Reason == "synthetic_uncertain" && elapsed.Elapsed < TimeSpan.FromSeconds(1),
                "RECEIPT_WAIT_UNCERTAIN_AT_ONCE: an Uncertain receipt is an answer and ends the wait at once");

            Guid held = Guid.NewGuid();
            tools.Send(sender.Address, mailbox.Address, held, "Synthetic message the mailbox holds.");
            tools.AcknowledgeMessage(held, mailbox.Address, MessageStatus.Held);
            elapsed.Restart();
            var holding = await fixture.NewRequest().Tools.WaitForReceipt(held, default, timeoutSeconds: 20).WaitAsync(TimeSpan.FromSeconds(25));
            elapsed.Stop();
            check(holding.Status == MessageStatus.Held && elapsed.Elapsed < TimeSpan.FromSeconds(1),
                "RECEIPT_WAIT_HELD_AT_ONCE: a Held receipt is an answer and ends the wait at once");
        }

        // A real clock, because only the message's own deadline can end this wait: the record is Submitted and nothing else commits.
        await using (var real = new ToolFixture("receipt-answer-real-clock", clock: TimeProvider.System))
        {
            var from = real.Register("Synthetic aging sender");
            var native = new BrokerSession(new(real.Store.BrokerId, "codex:receipt-answer-aging", Guid.NewGuid()), "Synthetic aging native", "codex",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
            real.Store.Register(native);
            var now = DateTimeOffset.UtcNow;
            var brief = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, from.Address, native.Address,
                "Synthetic submitted message whose deadline passes unacknowledged.", now, now.AddMilliseconds(1500));
            real.Store.Accept(brief, real.Store.BrokerId);
            var claim = real.Store.BeginDelivery();
            if (claim?.Message.Id != brief.Id) throw new InvalidOperationException("Synthetic aging message was not claimed.");
            real.Store.CompleteDelivery(claim, MessageStatus.Submitted, "synthetic_admission");
            var elapsed = Stopwatch.StartNew();
            var aged = await real.NewRequest().Tools.WaitForReceipt(brief.Id, default, timeoutSeconds: 10).WaitAsync(TimeSpan.FromSeconds(15));
            elapsed.Stop();
            Console.WriteLine($"NOTE RECEIPT_WAIT_SUBMITTED_AGES_UNCERTAIN elapsed {elapsed.Elapsed.TotalSeconds:F2} s for a 1.5 s deadline");
            check(aged.Status == MessageStatus.Uncertain && aged.Reason == "receipt_unconfirmed" && elapsed.Elapsed < TimeSpan.FromSeconds(4),
                "RECEIPT_WAIT_SUBMITTED_AGES_UNCERTAIN: a Submitted message's deadline ends the wait with Uncertain, receipt_unconfirmed");
        }
    }

    // Ends a held wait whichever way it went: cancelled, or refused (a mutant that refuses it must reach its named check).
    private static async Task Ended(Task wait)
    {
        try { await wait.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { /* A cancelled hold ends cancelled. */ }
        catch (McpException) { /* A refused wait has already ended. */ }
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
