using AgentBroker.Core;

internal static class ReplyLifetimeChecks
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        BrokerSession Participant(BrokerStore store, Clock clock, string id, SessionAddress? address = null) => new(
            address ?? new(store.BrokerId, id, Guid.NewGuid()), id, "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply,
            SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
        BrokerMessage Parent(BrokerSession from, BrokerSession to, Clock clock, TimeSpan lifetime) => new(
            Guid.NewGuid(), Guid.NewGuid(), null, from.Address, to.Address, "Synthetic task.", clock.GetUtcNow(), clock.GetUtcNow() + lifetime);

        Check(new BrokerLimits().DefaultMessageLifetimeMinutes == 1440 && BrokerLimits.MaximumLifetime == TimeSpan.FromHours(24),
            "The configurable default is24hours within the unchanged24hour maximum.");
        foreach (int minutes in new[] { int.MinValue, -1, 0, 1441, int.MaxValue })
        {
            string path = Path.Combine(root, "invalid-lifetime-" + minutes);
            bool refused = false;
            try { using var invalid = new BrokerStore(path, new(DefaultMessageLifetimeMinutes: minutes)); }
            catch (ArgumentOutOfRangeException error) { refused = error.ParamName == "limits"; }
            Check(refused && !Directory.Exists(path), "An invalid default lifetime is rejected before durable state is created.");
        }
        foreach (int? configured in new int?[] { null, 1, 7, 1440 })
        {
            var clock = new Clock();
            using var store = new BrokerStore(Path.Combine(root, "reply-lifetime-" + (configured?.ToString() ?? "default")),
                configured is int minutes ? new(DefaultMessageLifetimeMinutes: minutes) : null, clock);
            var from = Participant(store, clock, "from"); var to = Participant(store, clock, "to");
            store.Register(from); store.Register(to);
            var parent = Parent(from, to, clock, TimeSpan.FromSeconds(1));
            store.Accept(parent, store.BrokerId);
            store.RecordRecipientReceipt(parent.Id, to.Address, store.BrokerId, MessageStatus.Delivered);
            clock.Advance(TimeSpan.FromSeconds(2));
            Check(store.Messages(to.Address).Single().Status == MessageStatus.Delivered,
                "A delivered parent's passed delivery deadline never becomes failed or expired work.");
            var reply = store.AcceptReply(parent.Id, Guid.NewGuid(), "Completed work after delivery deadline.", store.BrokerId);
            Check(reply.Status == MessageStatus.Accepted && reply.Message.CreatedAt == clock.GetUtcNow() &&
                reply.Message.ExpiresAt == clock.GetUtcNow().AddMinutes(configured ?? 1440),
                "A fresh reply after its parent's deadline receives exactly the configured/default delivery lifetime.");
            Check(store.Messages(to.Address).Single(receipt => receipt.Message.Id == parent.Id).Message == parent,
                "Creating a reply never extends or rewrites the explicitly supplied parent timestamps.");
            store.RecordRecipientReceipt(reply.Message.Id, from.Address, store.BrokerId, MessageStatus.Delivered);
            clock.Advance(TimeSpan.FromMinutes((configured ?? 1440) + 1));
            Check(store.Messages(from.Address).Single(receipt => receipt.Message.Id == reply.Message.Id).Status == MessageStatus.Delivered,
                "A delivered reply remains delivered when its own delivery deadline passes.");
        }
        foreach ((int previous, int current) in new[] { (7, 1440), (1440, 1) })
        {
            string path = Path.Combine(root, $"reply-reconfigure-{previous}-{current}");
            var clock = new Clock();
            BrokerMessage parent; MessageReceipt first; SessionAddress fromAddress, toAddress; Guid broker;
            using (var store = new BrokerStore(path, new(DefaultMessageLifetimeMinutes: previous), clock))
            {
                var from = Participant(store, clock, "from"); var to = Participant(store, clock, "to");
                fromAddress = from.Address; toAddress = to.Address; broker = store.BrokerId;
                store.Register(from); store.Register(to);
                parent = Parent(from, to, clock, TimeSpan.FromMinutes(30));
                store.Accept(parent, broker);
                first = store.AcceptReply(parent.Id, Guid.NewGuid(), "Stable accepted reply.", broker);
            }
            string state = Path.Combine(path, "broker-state.json");
            byte[] before = File.ReadAllBytes(state);
            clock.Advance(TimeSpan.FromSeconds(3));
            using var reopened = new BrokerStore(path, new(DefaultMessageLifetimeMinutes: current), clock);
            var retried = reopened.AcceptReply(parent.Id, first.Message.Id, first.Message.Text, broker);
            Check(retried == first && retried.Message.CreatedAt == first.Message.CreatedAt && retried.Message.ExpiresAt == first.Message.ExpiresAt,
                "A stable reply ID retains the originally accepted timestamps after a default lifetime configuration change.");
            Check(File.ReadAllBytes(state).SequenceEqual(before) && reopened.Messages(fromAddress).Count == 2,
                "Reconfiguration and an exact retry neither rewrite durable bytes nor add another message.");
            reopened.Register(Participant(reopened, clock, "from", fromAddress));
            reopened.Register(Participant(reopened, clock, "to", toAddress));
            var fresh = reopened.AcceptReply(parent.Id, Guid.NewGuid(), "New reply uses the new setting.", broker);
            Check(fresh.Message.ExpiresAt == clock.GetUtcNow().AddMinutes(current),
                "Only new replies use the changed default lifetime.");
            Check(reopened.FindReceipt(parent.Id)?.Message == parent,
                "A new default never changes an explicit incoming message deadline.");
        }
        return passed;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
