using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Reflection;

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
        var index = (Dictionary<SessionAddress, HashSet<Guid>>)typeof(DeliveryObservations)
            .GetField("busyMessagesByRecipient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(observations)!;
        check(index.Values.Sum(messages => messages.Count) == DeliveryObservations.MaximumMessages,
            "OBS_PRUNE_INDEX_BOUND: the recipient index retains no evicted message IDs");
        observations.Record(original[0], new(AdapterAdmission.Submitted, "native_admission_unconfirmed"));
        check(observations.BusySince(Receipt(original[0])) is null && !observations.SeenBusy(recipient),
            "OBS_PRUNE_NONBUSY: another answer ends message and recipient busy observations");
        check(original.Append(added).All(message => observations.BusySince(Receipt(message)) is null),
            "OBS_PRUNE_RECIPIENT_INDEX: ending a recipient removes every bounded per-message entry after an eviction");

        var rolledClock = new ObservationClock();
        var rolled = new DeliveryObservations(rolledClock);
        foreach (var message in original) { rolled.Record(message, busy); rolledClock.Advance(); }
        rolledClock.RollBackWallClock();
        rolled.Record(original[0], busy);
        rolledClock.Advance();
        rolled.Record(added, busy);
        check(rolled.BusySince(Receipt(original[0])) == rolledClock.GetUtcNow().AddTicks(-1) &&
            rolled.BusySince(Receipt(original[1])) is null,
            "OBS_PRUNE_CLOCK_ROLLBACK_ENDS_EPISODE: an observation now in the future cannot continue the old busy episode");

        var tied = new DeliveryObservations(new ObservationClock());
        foreach (var message in original.Reverse()) tied.Record(message, busy);
        tied.Record(added, busy);
        check(tied.BusySince(Receipt(original[0])) is null && tied.BusySince(Receipt(original[^1])) is not null,
            "OBS_PRUNE_TIES: equal observation timestamps evict deterministically by message ID");
        Lifetime(check);
    }

    internal static async Task RunRuntimeAsync(Action<bool, string> check)
    {
        string? root = null;
        try
        {
            var clock = new ToolFixture.ManualClock();
            var adapter = new CensusAdapter(clock);
            root = Path.Combine(AppContext.BaseDirectory, "scratch", "busy-episode-" + Guid.NewGuid().ToString("N"));
            using var store = new BrokerStore(root, clock: clock);
            await using var runtime = new BrokerRuntime(store, new CensusFederation(), [adapter], clock: clock)
            { DiscoveryInterval = TimeSpan.FromMilliseconds(20) };
            runtime.Start(default);
            await UntilAsync(() => adapter.Address is not null && store.Directory().Any(row => row.Address == adapter.Address));
            var sender = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic episode sender", false);
            var sent = store.Send(sender.Address, adapter.Address!, Guid.NewGuid(), "Synthetic typed busy episode.", null, 0,
                store.BrokerId);
            await UntilAsync(() => store.FindReceipt(sent.Message.Id)?.Reason == "native_recipient_busy");
            DateTimeOffset? BusySince() => runtime.View(store.FindReceipt(sent.Message.Id)!).BusySince;
            check(BusySince() is not null, "BUSY_CENSUS_RUNTIME_TYPED: actual native deferral starts the receipt's busy observation");
            adapter.Mode = 1;
            await UntilAsync(() => runtime.AdapterErrors.ContainsKey(adapter.Id));
            check(BusySince() is not null,
                "BUSY_CENSUS_FAILED_PASS_PRESERVES: an unavailable directory pass cannot claim the native recipient disappeared");
            adapter.Mode = 4;
            await UntilAsync(() => !runtime.AdapterErrors.ContainsKey(adapter.Id));
            check(BusySince() is not null,
                "BUSY_CENSUS_PARTIAL_PASS_PRESERVES: an incomplete successful pass cannot claim the native recipient disappeared");
            adapter.Mode = 2;
            await UntilAsync(() => BusySince() is null,
                "BUSY_CENSUS_RUNTIME_DISAPPEARED: a successful empty native census did not end the old busy run");
            check(store.FindReceipt(sent.Message.Id) is { Status: MessageStatus.Accepted, Reason: "native_recipient_busy" },
                "BUSY_CENSUS_RUNTIME_DISAPPEARED: a successful empty native census ends report-only timing without rewriting or retrying the receipt");
        }
        finally { if (root is not null && Directory.Exists(root)) Directory.Delete(root, true); }
        await ClaudeObservationChecks.RunCensusAsync(check);
    }

    private static async Task UntilAsync(Func<bool> condition, string failure = "BUSY_CENSUS_RUNTIME_SETUP: expected discovery or receipt transition did not complete")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { while (!condition()) await Task.Delay(10, deadline.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException(failure); }
    }

    private sealed class CensusAdapter(TimeProvider clock) : ILocalAgentAdapter
    {
        internal SessionAddress? Address;
        internal volatile int Mode;
        private readonly TaskCompletionSource busyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "claude-native";
        public bool DiscoveryCensusComplete => Mode != 4;
        public async Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            if (Address is not null && Mode == 0) await busyStarted.Task.WaitAsync(token);
            Address ??= new(brokerId, "claude:" + Guid.NewGuid(), Guid.NewGuid());
            if (Mode == 1) throw new IOException("Synthetic unavailable pass.");
            return Mode is 2 or 4 ? [] : [new(Address,
                "Synthetic busy episode", Id, SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                Mode == 3 ? SessionActivity.Unknown : SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1))];
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            Mode = 3;
            busyStarted.TrySetResult();
            return Task.FromResult(new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_recipient_busy"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CensusFederation : IPeerFederation
    {
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => throw new NotSupportedException();
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Lifetime(Action<bool, string> check)
    {
        var clock = new ObservationClock();
        var observations = new DeliveryObservations(clock);
        Guid broker = Guid.NewGuid();
        var sender = new SessionAddress(broker, "synthetic-sender", Guid.NewGuid());
        var recipient = new SessionAddress(broker, "claude:" + Guid.NewGuid(), Guid.NewGuid());
        var other = new SessionAddress(broker, "codex:" + Guid.NewGuid(), Guid.NewGuid());
        BrokerMessage Message(SessionAddress address) => new(Guid.NewGuid(), Guid.NewGuid(), null, sender,
            address, "Synthetic busy lifetime.", clock.GetUtcNow(), clock.GetUtcNow().AddDays(1));
        MessageReceipt Receipt(BrokerMessage message, string reason = "native_recipient_busy", MessageStatus status = MessageStatus.Accepted) =>
            new(message, status, clock.GetUtcNow(), reason);
        BrokerSession Row(SessionAddress address, SessionActivity activity) => new(address, "Synthetic busy lifetime",
            address == recipient ? "claude-native" : "codex-native", SessionCapabilities.Receive, activity, clock.GetUtcNow().AddMinutes(1));
        var busy = new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_recipient_busy");
        var first = Message(recipient);
        var second = Message(recipient);
        var unrelated = Message(other);
        observations.Record(unrelated, busy);
        foreach (var activity in new[] { SessionActivity.Idle, SessionActivity.ApprovalRequired, SessionActivity.Interrupted })
        {
            observations.Record(first, busy);
            observations.Record(second, busy);
            observations.ObserveDirectory(Row(recipient, activity));
            check(observations.BusySince(Receipt(first)) is null && observations.BusySince(Receipt(second)) is null &&
                !observations.SeenBusy(recipient),
                $"BUSY_EPISODE_{activity.ToString().ToUpperInvariant()}: authoritative state ends all deferred message runs");
            check(observations.BusySince(Receipt(unrelated)) is not null,
                "BUSY_EPISODE_RECIPIENT_ISOLATION: ending one recipient leaves another exact address unchanged");
            clock.Advance(TimeSpan.FromSeconds(1));
            var restartedAt = clock.GetUtcNow();
            observations.Record(first, busy);
            check(observations.BusySince(Receipt(first)) == restartedAt,
                "BUSY_EPISODE_RESTART: a later busy turn starts now rather than keeping a previous message latch");
        }
        observations.ObserveDirectory(Row(recipient, SessionActivity.Unknown));
        check(observations.BusySince(Receipt(first)) is not null,
            "BUSY_EPISODE_UNKNOWN_PRESERVES: unknown activity does not invent an authoritative turn ending");
        clock.Advance(DeliveryObservations.BusyFreshFor);
        check(observations.BusySince(Receipt(first)) is null && !observations.SeenBusy(recipient),
            "BUSY_EPISODE_STALE: lapsed evidence cannot report an unbounded per-message busy run");
        var freshAt = clock.GetUtcNow();
        observations.Record(first, busy);
        check(observations.BusySince(Receipt(first)) == freshAt,
            "BUSY_EPISODE_GAP_RESETS: a fresh busy answer after the freshness gap begins a new episode");
        observations.ObserveDirectory(Row(recipient, SessionActivity.Busy) with { LeaseUntil = clock.GetUtcNow() });
        check(observations.BusySince(Receipt(first)) is null,
            "BUSY_EPISODE_LEASE_ENDS: an expired native row ends its message's busy evidence");

        observations.Record(first, busy);
        observations.Record(unrelated, busy);
        observations.ObserveDirectoryCensus("claude-native", []);
        check(observations.BusySince(Receipt(first)) is null && observations.BusySince(Receipt(unrelated)) is not null,
            "BUSY_EPISODE_CENSUS_ABSENCE: a complete empty native pass ends only its own adapter's missing recipients");
        observations.Record(first, busy);
        observations.ObserveDirectoryCensus("claude-native", [Row(recipient, SessionActivity.Unknown)]);
        check(observations.BusySince(Receipt(first)) is not null,
            "BUSY_EPISODE_CENSUS_PRESENT: a present unknown row preserves recent busy evidence");
        observations.ObserveDirectoryCensus("synthetic-other", []);
        check(observations.BusySince(Receipt(first)) is not null,
            "BUSY_EPISODE_CENSUS_SCOPE: another adapter cannot end native observations");

        observations.ObserveDirectory(Row(recipient, SessionActivity.Busy));
        var directorySince = clock.GetUtcNow();
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            observations.ObserveDirectory(Row(recipient, SessionActivity.Busy));
        }
        check(observations.BusySince(Receipt(first)) == directorySince &&
            observations.BusySince(Receipt(first, "native_write_unconfirmed", MessageStatus.Submitted)) == directorySince,
            "BUSY_EPISODE_DIRECTORY_CONTINUES: fresh directory evidence continues the current run after the last message attempt ages out");
        clock.Advance(DeliveryObservations.BusyFreshFor);
        var afterGap = clock.GetUtcNow();
        observations.Record(first, busy);
        check(observations.BusySince(Receipt(first)) == afterGap &&
            observations.BusySince(Receipt(first, "native_write_unconfirmed", MessageStatus.Submitted)) is null,
            "BUSY_EPISODE_GAP_PROVENANCE: an old directory observation cannot authorize timing for a new delivery-only busy episode");
        observations.ObserveDirectory(Row(recipient, SessionActivity.ApprovalRequired));
        clock.Advance(TimeSpan.FromSeconds(1));
        directorySince = clock.GetUtcNow();
        observations.ObserveDirectory(Row(recipient, SessionActivity.Busy));
        check(observations.BusySince(Receipt(first)) == directorySince,
            "BUSY_EPISODE_DIRECTORY_RESTART: an old busy receipt can describe only the newly observed directory episode");
        UntrackedRollback(check);
    }

    private static void UntrackedRollback(Action<bool, string> check)
    {
        var clock = new ObservationClock();
        var observations = new DeliveryObservations(clock);
        Guid broker = Guid.NewGuid();
        var sender = new SessionAddress(broker, "synthetic-sender", Guid.NewGuid());
        var recipient = new SessionAddress(broker, "claude:" + Guid.NewGuid(), Guid.NewGuid());
        BrokerMessage Message(SessionAddress address) => new(Guid.NewGuid(), Guid.NewGuid(), null, sender, address,
            "Synthetic bounded-recipient rollback.", clock.GetUtcNow(), clock.GetUtcNow().AddDays(1));
        MessageReceipt Receipt(BrokerMessage message) => new(message, MessageStatus.Accepted, clock.GetUtcNow(), "native_recipient_busy");
        var busy = new AdapterResult(AdapterAdmission.DeferredNotSubmitted, "native_recipient_busy");
        for (int i = 0; i < DeliveryObservations.MaximumRecipients; i++)
            observations.Record(Message(new(broker, "claude:" + Guid.NewGuid(), Guid.NewGuid())), busy);
        var old = Message(recipient);
        observations.Record(old, busy); // This recipient is beyond the bounded directory map, but has its own message observation.
        var originalAt = clock.GetUtcNow();
        clock.RollBackWallClock();
        var current = Message(recipient);
        observations.Record(current, busy);
        check(observations.BusySince(Receipt(current)) == clock.GetUtcNow() && observations.BusySince(Receipt(old)) is null,
            "BUSY_EPISODE_ROLLBACK_NEW_EPISODE: a previously untracked recipient starts with the corrected current time");
        clock.SetWallClock(originalAt);
        check(observations.BusySince(Receipt(old)) is null,
            "BUSY_EPISODE_ROLLBACK_NO_RESURRECTION: restoring wall time cannot resurrect a message from an ended episode");
    }

    private sealed class ObservationClock : TimeProvider
    {
        private DateTimeOffset wall = DateTimeOffset.UnixEpoch;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => wall;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance() { wall = wall.AddTicks(1); timestamp++; }
        internal void Advance(TimeSpan elapsed) { wall = wall.Add(elapsed); timestamp += elapsed.Ticks; }
        internal void RollBackWallClock() => wall = wall.AddDays(-2);
        internal void SetWallClock(DateTimeOffset value) => wall = value;
    }
}
