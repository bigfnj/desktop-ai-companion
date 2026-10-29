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
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 29, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
