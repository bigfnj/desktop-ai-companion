using AgentBroker.Core;
using AgentBroker.Worker;

internal static class ObservationPruningChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var clock = new ObservationClock();
        var observations = new DeliveryObservations(clock);
        var busy = new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_context_busy");
        Guid broker = Guid.NewGuid();
        var sender = new SessionAddress(broker, "synthetic-sender", Guid.NewGuid());
        var recipient = new SessionAddress(broker, "synthetic-recipient", Guid.NewGuid());
        BrokerMessage Message(int index) => new(Guid.Parse(index.ToString("x8") + "-0000-4000-8000-000000000001"),
            Guid.NewGuid(), null, sender, recipient, "Synthetic bounded observation.", clock.GetUtcNow(), clock.GetUtcNow().AddDays(2));
        MessageReceipt Receipt(BrokerMessage message) => new(message, MessageStatus.Accepted, clock.GetUtcNow(), "native_context_busy");
        var original = Enumerable.Range(0, DeliveryObservations.MaximumMessages).Select(Message).ToArray();
        var since = clock.GetUtcNow();
        foreach (var message in original) { observations.Record(message, busy); clock.Advance(); }
        observations.Record(original[0], busy);
        clock.Advance();
        var added = Message(DeliveryObservations.MaximumMessages + 1);
        observations.Record(added, busy);
        check(typeof(DeliveryObservations).Assembly == typeof(WorkerApplication).Assembly,
            "OBS_PRUNE_WITNESS: capacity checks execute the rebuilt product observation class");
        check(observations.BusySince(Receipt(original[0])) == since,
            "OBS_PRUNE_REFRESH_KEEPS_SINCE: refreshing the oldest busy run preserves its original start");
        check(observations.BusySince(Receipt(original[1])) is null && observations.BusySince(Receipt(added)) is not null,
            "OBS_PRUNE_LEAST_RECENT: new arrival evicts the least recently observed entry, not the oldest busy start");
        check(original.Append(added).Count(message => observations.BusySince(Receipt(message)) is not null) == DeliveryObservations.MaximumMessages,
            "OBS_PRUNE_BOUND: public observation results remain at the configured bound");
        observations.Record(original[0], new(AdapterAdmission.Submitted, "native_admission_unconfirmed"));
        check(observations.BusySince(Receipt(original[0])) is null && !observations.SeenBusy(recipient),
            "OBS_PRUNE_NONBUSY: another answer ends message and recipient busy observations");

        var rolledClock = new ObservationClock();
        var rolled = new DeliveryObservations(rolledClock);
        foreach (var message in original) { rolled.Record(message, busy); rolledClock.Advance(); }
        rolledClock.RollBackWallClock();
        rolled.Record(original[0], busy);
        rolledClock.Advance();
        rolled.Record(added, busy);
        check(rolled.BusySince(Receipt(original[0])) is not null && rolled.BusySince(Receipt(original[1])) is null,
            "OBS_PRUNE_MONOTONIC: wall-clock rollback cannot evict a refreshed observation ahead of stale ones");

        var tied = new DeliveryObservations(new ObservationClock());
        foreach (var message in original.Reverse()) tied.Record(message, busy);
        tied.Record(added, busy);
        check(tied.BusySince(Receipt(original[0])) is null && tied.BusySince(Receipt(original[^1])) is not null,
            "OBS_PRUNE_TIES: equal observation timestamps evict deterministically by message ID");
    }

    private sealed class ObservationClock : TimeProvider
    {
        private DateTimeOffset wall = DateTimeOffset.UnixEpoch;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => wall;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance() { wall = wall.AddTicks(1); timestamp++; }
        internal void RollBackWallClock() => wall = wall.AddDays(-2);
    }
}
