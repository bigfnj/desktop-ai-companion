using AgentBroker.Core;

// Delivery order (2026-10-29): one sender's messages to one recipient are delivered in the order the broker accepted them.
// Agents on another workstation reported messages arriving out of order, because BeginDelivery took the first Accepted
// message outside the runtime's backoff set, so a newer message overtook an older one that was backing off after a
// deferral. Each label names the rule a mutation of BeginDelivery's pick must break.
internal static class DeliveryOrderChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        BrokerSession Native(BrokerStore store, Clock clock, string id) => new(new(store.BrokerId, id, Guid.NewGuid()), "Native " + id,
            "claude-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
        BrokerMessage Message(BrokerSession from, BrokerSession to, Clock clock, string text, TimeSpan lifetime) => new(Guid.NewGuid(), Guid.NewGuid(), null,
            from.Address, to.Address, text, clock.GetUtcNow(), clock.GetUtcNow() + lifetime);

        var clock = new Clock();
        using var store = new BrokerStore(Path.Combine(root, "delivery-order"), clock: clock);
        var sender = Native(store, clock, "claude:order-sender");
        var other = Native(store, clock, "claude:order-other");
        var recipient = Native(store, clock, "codex:order-recipient");
        store.Register(sender); store.Register(other); store.Register(recipient);

        var first = Message(sender, recipient, clock, "Synthetic first message.", TimeSpan.FromMinutes(10));
        store.Accept(first, store.BrokerId);
        // The first delivery attempt defers (the recipient turned busy): the message returns to Accepted and backs off.
        var claimed = store.BeginDelivery();
        Check(claimed?.Message.Id == first.Id, "ORDER_FIRST_CLAIMED: the only pending message is claimed first");
        store.ReleaseUnsubmittedClaim(first.Id, "native_recipient_not_idle");
        var second = Message(sender, recipient, clock, "Synthetic second message.", TimeSpan.FromMinutes(10));
        var unrelated = Message(other, recipient, clock, "Synthetic message from another sender.", TimeSpan.FromMinutes(10));
        store.Accept(second, store.BrokerId);
        store.Accept(unrelated, store.BrokerId);
        var backingOff = new HashSet<Guid> { first.Id };

        var pick = store.BeginDelivery(backingOff);
        Check(pick?.Message.Id == unrelated.Id,
            "ORDER_SAME_PAIR_WAITS: while the older message backs off, the newer one from the same sender waits and another sender's goes");
        store.CompleteDelivery(unrelated.Id, MessageStatus.Submitted, "synthetic_submitted");
        Check(store.BeginDelivery(backingOff) is null,
            "ORDER_NO_OVERTAKE: with only the backing-off message and its successor left, nothing is claimed");
        Check(store.BeginDelivery()?.Message.Id == first.Id, "ORDER_OLDER_FIRST: once the backoff ends, the older message is claimed before the newer");
        store.CompleteDelivery(first.Id, MessageStatus.Submitted, "synthetic_submitted");
        Check(store.BeginDelivery()?.Message.Id == second.Id, "ORDER_THEN_NEWER: the newer message follows once the older one has gone");
        store.CompleteDelivery(second.Id, MessageStatus.Submitted, "synthetic_submitted");

        // Expiry ends the wait: an older message that never becomes deliverable must not hold its successor forever.
        var stale = Message(sender, recipient, clock, "Synthetic message that will expire.", TimeSpan.FromMinutes(1));
        store.Accept(stale, store.BrokerId);
        var fresh = Message(sender, recipient, clock, "Synthetic message after the expiring one.", TimeSpan.FromMinutes(30));
        store.Accept(fresh, store.BrokerId);
        var staleOnly = new HashSet<Guid> { stale.Id };
        Check(store.BeginDelivery(staleOnly) is null, "ORDER_WAIT_BEFORE_EXPIRY: the successor waits while the older message is pending");
        clock.Advance(TimeSpan.FromMinutes(2));
        store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
        store.Register(recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
        Check(store.BeginDelivery(staleOnly)?.Message.Id == fresh.Id && store.FindReceipt(stale.Id)?.Status == MessageStatus.Expired,
            "ORDER_EXPIRY_RELEASES: once the older message expires, its successor is claimed");
        store.CompleteDelivery(fresh.Id, MessageStatus.Submitted, "synthetic_submitted");
        passed += WaitReasons(store, clock, sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
        return passed;
    }

    // N-broker-next-01: a message waiting for a native recipient that is not Ready was never claimed and had no reason, so its
    // sender could not tell busy from lost. The receipt now names the wait, derived when read; nothing is written or claimed.
    private static int WaitReasons(BrokerStore store, Clock clock, BrokerSession sender)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        BrokerMessage Message(SessionAddress to, string text) => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, to, text,
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(30));
        store.Register(sender);
        // The expired message above owes its sender a notice; deliver what is claimable so only this section's records remain.
        while (store.BeginDelivery() is { } owed) store.CompleteDelivery(owed.Message.Id, MessageStatus.Submitted, "synthetic_submitted");
        var busy = new BrokerSession(new(store.BrokerId, "claude:wait-busy", Guid.NewGuid()), "Native busy", "claude-native",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy, clock.GetUtcNow().AddMinutes(2));
        store.Register(busy);
        var waiting = Message(busy.Address, "Synthetic message to a busy session.");
        store.Accept(waiting, store.BrokerId);
        var claimed = store.BeginDelivery();
        var receipt = store.FindReceipt(waiting.Id);
        Check(claimed is null && receipt is { Status: MessageStatus.Accepted, Reason: "recipient_busy", Attempts: 0 },
            "WAIT_REASON_BUSY: a message waiting for a busy native session reads Accepted with recipient_busy, unclaimed (" +
            receipt?.Status + " " + receipt?.Reason + ")");
        Check(store.FindReceiptWithText(waiting.Id)?.Reason is null,
            "WAIT_REASON_BUSY WITNESS: the reason is derived when read; the stored record is unchanged, so no pass writes for it");
        store.Register(busy with { Activity = SessionActivity.ApprovalRequired });
        Check(store.FindReceipt(waiting.Id)?.Reason == "recipient_awaiting_approval",
            "WAIT_REASON_FOLLOWS_STATE: the reason follows the recipient's present state (" + store.FindReceipt(waiting.Id)?.Reason + ")");
        store.Register(busy with { Activity = SessionActivity.Idle });
        Check(store.FindReceipt(waiting.Id)?.Reason is null && store.BeginDelivery()?.Message.Id == waiting.Id,
            "WAIT_REASON_FOLLOWS_STATE WITNESS: a Ready recipient shows no wait and the message is claimed");
        // A deferral's own reason is the better account of a message that was tried: it is kept.
        store.ReleaseUnsubmittedClaim(waiting.Id, "native_recipient_not_idle");
        store.Register(busy);
        Check(store.FindReceipt(waiting.Id)?.Reason == "native_recipient_not_idle",
            "WAIT_REASON_KEEPS_DEFERRAL: a message that was tried keeps its deferral reason (" + store.FindReceipt(waiting.Id)?.Reason + ")");
        // A Codex row whose engine check could not complete lists Receive alone (N-broker-next-02).
        var pending = new BrokerSession(new(store.BrokerId, "codex:wait-pending", Guid.NewGuid()), "Native pending", "codex-native",
            SessionCapabilities.Receive, SessionActivity.Unknown, clock.GetUtcNow().AddSeconds(30));
        store.Register(pending);
        var toPending = Message(pending.Address, "Synthetic message to a row whose check is pending.");
        store.Accept(toPending, store.BrokerId);
        Check(store.FindReceipt(toPending.Id)?.Reason == "recipient_wake_unavailable",
            "WAIT_REASON_WAKE_UNAVAILABLE: a native row with Receive alone reads recipient_wake_unavailable (" + store.FindReceipt(toPending.Id)?.Reason + ")");
        // The row lapses (a flicker): the message is still Accepted, and now says why.
        clock.Advance(TimeSpan.FromSeconds(40));
        Check(store.FindReceipt(toPending.Id) is { Status: MessageStatus.Accepted, Reason: "recipient_unavailable" },
            "WAIT_REASON_UNAVAILABLE: a message whose native recipient lapsed reads recipient_unavailable (" + store.FindReceipt(toPending.Id)?.Reason + ")");
        // A generic mailbox's Accepted already means unread; its receipt is unchanged.
        var mailbox = new BrokerSession(new(store.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Mailbox", "mcp",
            SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
        store.Register(sender with { LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
        store.Register(mailbox);
        var toMailbox = Message(mailbox.Address, "Synthetic message to a mailbox.");
        store.Accept(toMailbox, store.BrokerId);
        Check(store.FindReceipt(toMailbox.Id) is { Status: MessageStatus.Accepted, Reason: null },
            "WAIT_REASON_MAILBOX_UNCHANGED: a generic mailbox's Accepted receipt carries no wait reason (" + store.FindReceipt(toMailbox.Id)?.Reason + ")");
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 29, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
