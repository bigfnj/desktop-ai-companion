using AgentBroker.Core;

// N-broker-e-02 (lane broker-next, AgentBroker 1.6.0): BrokerStore.AcceptedIncoming, the bounded query each
// wait_for_messages pass now runs instead of filtering the participant's whole history from Messages. Each label names the
// rule a mutation of the query must break; the differential check compares it with the filter it replaced.
internal static class AcceptedIncomingChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        var clock = new Clock();
        using var store = new BrokerStore(Path.Combine(root, "accepted-incoming"), clock: clock);
        BrokerSession Mailbox(string id) => new(new(store.BrokerId, "mcp:" + id, Guid.NewGuid()), "Mailbox " + id, "mcp",
            SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(2));
        var waiter = Mailbox("waiter"); var peer = Mailbox("peer"); var bystander = Mailbox("bystander");
        foreach (var session in new[] { waiter, peer, bystander }) store.Register(session);
        BrokerMessage Message(BrokerSession from, BrokerSession to, TimeSpan lifetime, Guid? id = null) => new(id ?? Guid.NewGuid(), Guid.NewGuid(),
            null, from.Address, to.Address, "Synthetic accepted-incoming message.", clock.GetUtcNow(), clock.GetUtcNow() + lifetime);

        var first = Message(peer, waiter, TimeSpan.FromMinutes(30));
        store.Accept(first, store.BrokerId);
        clock.Advance(TimeSpan.FromSeconds(1));
        // Two messages created in the same instant: the ID breaks the tie, as the filter it replaced ordered them.
        var tieLow = Message(peer, waiter, TimeSpan.FromMinutes(30), new Guid("00000000-0000-4000-8000-000000000001"));
        var tieHigh = Message(peer, waiter, TimeSpan.FromMinutes(30), new Guid("ffffffff-0000-4000-8000-000000000001"));
        store.Accept(tieHigh, store.BrokerId); store.Accept(tieLow, store.BrokerId);
        var outgoing = Message(waiter, peer, TimeSpan.FromMinutes(30));
        store.Accept(outgoing, store.BrokerId);
        var delivered = Message(peer, waiter, TimeSpan.FromMinutes(30));
        store.Accept(delivered, store.BrokerId);
        store.RecordRecipientReceipt(delivered.Id, waiter.Address, store.BrokerId, MessageStatus.Delivered);
        var held = Message(peer, waiter, TimeSpan.FromMinutes(30));
        store.Accept(held, store.BrokerId);
        store.RecordRecipientReceipt(held.Id, waiter.Address, store.BrokerId, MessageStatus.Held);
        var elsewhere = Message(peer, bystander, TimeSpan.FromMinutes(30));
        store.Accept(elsewhere, store.BrokerId);
        clock.Advance(TimeSpan.FromSeconds(1));
        var expiring = Message(peer, waiter, TimeSpan.FromSeconds(30));
        store.Accept(expiring, store.BrokerId);

        Check(store.Messages(waiter.Address).Any(r => r.Message.Id == delivered.Id && r.Status == MessageStatus.Delivered && r.Message.Text.Length == 0),
            "ACCEPTED_INCOMING WITNESS: the acknowledged message is a tombstone in the waiter's history");
        var before = store.AcceptedIncoming(waiter.Address, 16).Select(r => r.Message.Id).ToArray();
        Check(before.SequenceEqual(new[] { first.Id, tieLow.Id, tieHigh.Id, expiring.Id }),
            "ACCEPTED_INCOMING_ONLY: only this recipient's Accepted records, oldest first, the ID breaking a tie; never its outgoing, a tombstone, a held message or another recipient's");
        Check(store.AcceptedIncoming(waiter.Address, 2).Select(r => r.Message.Id).SequenceEqual(new[] { first.Id, tieLow.Id }),
            "ACCEPTED_INCOMING_LIMIT: limit takes the oldest");
        // Past the short message's deadline and inside every session's two-minute lease.
        clock.Advance(TimeSpan.FromSeconds(45));
        var after =store.AcceptedIncoming(waiter.Address, 16);
        Check(after.Select(r => r.Message.Id).SequenceEqual(new[] { first.Id, tieLow.Id, tieHigh.Id }) && after.All(r => r.Status == MessageStatus.Accepted),
            "ACCEPTED_INCOMING_EXPIRES: a record past its deadline is aged to Expired by the query itself and never returned");
        Check(store.FindReceipt(expiring.Id)?.Status == MessageStatus.Expired,
            "ACCEPTED_INCOMING_EXPIRES WITNESS: the expired record reads Expired");
        // Differential: for every participant, the query equals the filter over Messages that BrokerTools ran before.
        foreach (var session in new[] { waiter, peer, bystander })
        {
            var expected = store.Messages(session.Address).Where(r => r.Message.Recipient == session.Address && r.Status == MessageStatus.Accepted)
                .OrderBy(r => r.Message.CreatedAt).ThenBy(r => r.Message.Id).Take(16).ToArray();
            var actual = store.AcceptedIncoming(session.Address, 16);
            Check(actual.SequenceEqual(expected), "ACCEPTED_INCOMING_EQUALS_FILTER: the query equals the old filter over Messages for " + session.Label);
        }
        // The peer is owed delivery notices for its expired message, so its own list is not empty: a non-vacuous comparison.
        Check(store.AcceptedIncoming(peer.Address, 16).Count > 0 && store.AcceptedIncoming(bystander.Address, 16).Count == 1,
            "ACCEPTED_INCOMING_EQUALS_FILTER WITNESS: the compared lists are not all empty");
        try { store.AcceptedIncoming(waiter.Address, 0); Check(false, "ACCEPTED_INCOMING_LIMIT: a zero limit refuses"); }
        catch (ArgumentOutOfRangeException) { passed++; }
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
