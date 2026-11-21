using AgentBroker.Core;
using AgentBroker.Protocol;
using System.Globalization;
using AgentBroker.TestSupport;
using AgentBroker.Worker;
using AgentBroker.Worker.Federation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class PeerDirectoryShapeChecks
{
    internal static void PureContracts(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: DIRECTORY_SHAPE_POLICY_EXECUTED");
        Console.WriteLine("DIRECTORY_SHAPE_WORKER_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BrokerRuntime).Assembly.Location))));
        Guid peer = Guid.NewGuid();
        var row = Row(peer);
        bool Valid(PeerDirectory? value, int maximum = PeerDirectoryPolicy.ListingMaximumSessions) => PeerDirectoryPolicy.IsValid(value, peer, maximum);
        bool Shape(PeerDirectory? value) => PeerDirectoryPolicy.HasValidShape(value, peer, PeerDirectoryPolicy.ListingMaximumSessions);
        check(Valid(new(peer, [row])) && Valid(new(peer, [])),
            "DIRECTORY_SHAPE_VALID: ordinary and authenticated empty directories remain valid");
        check(!Valid(null), "DIRECTORY_SHAPE_NULL_DIRECTORY: a null directory cannot publish rows");
        check(!Valid(new(peer, null!)), "DIRECTORY_SHAPE_NULL_COLLECTION: a missing collection cannot publish rows");
        check(!Shape(new(peer, [null!])) && !Valid(new(peer, [null!])), "DIRECTORY_SHAPE_NULL_ROW: collection elements require an actual session");
        check(!Valid(new(peer, [row with { Address = null! }])), "DIRECTORY_SHAPE_NULL_ADDRESS: a row requires an actual address");
        check(!Valid(new(Guid.NewGuid(), [row])), "DIRECTORY_SHAPE_BROKER: the directory belongs to the expected broker");
        check(!Valid(new(peer, [row with { Address = row.Address with { BrokerId = Guid.NewGuid() } }])),
            "DIRECTORY_SHAPE_ROW_BROKER: a row cannot claim another broker");
        check(!Valid(new(peer, [row with { IsPrivate = true }])), "DIRECTORY_SHAPE_PRIVATE: a private row cannot leave its broker");
        check(Valid(new(peer, Enumerable.Repeat(row, 256).ToArray())) && !Valid(new(peer, Enumerable.Repeat(row, 257).ToArray())),
            "DIRECTORY_SHAPE_LISTING_CAP: the existing local presentation cap remains256");
        check(Valid(new(peer, Enumerable.Repeat(row, 512).ToArray()), PeerDirectoryPolicy.WireMaximumSessions) &&
            !Valid(new(peer, Enumerable.Repeat(row, 513).ToArray()), PeerDirectoryPolicy.WireMaximumSessions),
            "DIRECTORY_SHAPE_WIRE_CAP: the existing wire cap remains512");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
        };
        var parsed = JsonSerializer.Deserialize<PeerDirectory>(JsonSerializer.Serialize(new PeerDirectory(peer, [null!]), json), json);
        check(parsed?.Sessions.Count == 1 && parsed.Sessions[0] is null && !Valid(parsed),
            "DIRECTORY_SHAPE_PARSED_ELEMENT: collection-element nullability needs explicit validation after decoding");
        check(Valid(JsonSerializer.Deserialize<PeerDirectory>(JsonSerializer.Serialize(new PeerDirectory(peer, [row]), json), json)),
            "DIRECTORY_SHAPE_PARSED_VALID: ordinary directory JSON keeps its original shape and values");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        PureContracts(check);
        string root;
        using (var scratch = OwnedScratch.Create("directory-shape"))
        {
            root = scratch.Root;
            var clock = TimeProvider.System;
            using var store = new BrokerStore(Path.Combine(root, "store"), clock: clock);
            var healthy = Peer(store, "healthy", 18501, clock);
            var malformed = Peer(store, "malformed", 18502, clock);
            var healthyRow = Row(healthy.BrokerId, clock);
            var badRow = Row(malformed.BrokerId, clock);
            await using var federation = new DirectoryFederation(healthy.BrokerId, healthyRow);
            await using var runtime = new BrokerRuntime(store, federation, [], clock: clock);
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic local directory", false);
            byte[] before = File.ReadAllBytes(Path.Combine(root, "store", "broker-state.json"));
            PeerDirectory?[] cases = [null, new(malformed.BrokerId, null!), new(malformed.BrokerId, [null!]),
                new(malformed.BrokerId, [badRow with { Address = null! }])];
            foreach (var candidate in cases)
            {
                federation.Other = candidate;
                var listed = await runtime.DirectoryAsync(false, default);
                check(listed.Count == 2 && listed.Any(row => row.Address == local.Address) && listed.Any(row => row.Address == healthyRow.Address),
                    "DIRECTORY_SHAPE_PEER_ISOLATION: malformed peer shape leaves local and healthy peer rows available");
                var diagnostics = await PeerDiagnostics.CollectAsync(runtime, default);
                check(diagnostics.Single(row => row.BrokerId == malformed.BrokerId) is { Success: false, Code: "invalid_directory" } &&
                    diagnostics.Single(row => row.BrokerId == healthy.BrokerId) is { Success: true, ReturnedSessionCount: 1 },
                    "DIRECTORY_SHAPE_DIAGNOSTIC: malformed peer shape is classified without failing a healthy peer");
            }
            federation.Other = new(malformed.BrokerId, [badRow, badRow with { IsPrivate = true },
                badRow with { Address = badRow.Address with { BrokerId = Guid.NewGuid() } },
                badRow with { LeaseUntil = clock.GetUtcNow().AddSeconds(-1) }]);
            var mixed = await runtime.DirectoryAsync(false, default);
            check(mixed.Count == 3 && mixed.Count(row => row.Address == badRow.Address) == 1 &&
                mixed.Any(row => row.Address == local.Address) && mixed.Any(row => row.Address == healthyRow.Address),
                "DIRECTORY_SHAPE_RUNTIME_FILTERING: the runtime retains eligible rows while filtering private, transitive and expired rows");
            check(File.ReadAllBytes(Path.Combine(root, "store", "broker-state.json")).SequenceEqual(before),
                "DIRECTORY_SHAPE_READ_ONLY: directory and diagnostic checks do not rewrite durable trust or messages");
        }
        check(!Directory.Exists(root), "DIRECTORY_SHAPE_FIXTURE_CLEAN: the exact owned scratch root is absent");
        await PeerClockDirectoryAsync(check);
    }

    private static async Task PeerClockDirectoryAsync(Action<bool, string> check)
    {
        using var scratch = OwnedScratch.Create("directory-peer-clock");
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "injected")))
        {
            var row = Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(10) };
            f.Federation.Rows = [row];
            bool before = (await f.ListedAsync()).Any(item => item.Address == row.Address);
            f.Clock.Advance(TimeSpan.FromSeconds(11));
            check(before && (await f.ListedAsync()).Count == 0,
                "DIRECTORY_PEER_CLOCK_INJECTED_NOW");
        }
        foreach (int offset in new[] { -30, 30 })
        {
            await using var f = new ClockFixture(Path.Combine(scratch.Root, offset < 0 ? "behind" : "ahead"));
            f.Measure(offset, TimeSpan.Zero);
            var peerNow = f.Clock.GetUtcNow().AddSeconds(offset);
            var live = Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = peerNow.AddTicks(1) };
            var exact = live with { Address = live.Address with { SessionId = "exact" }, LeaseUntil = peerNow };
            var expired = live with { Address = live.Address with { SessionId = "expired" }, LeaseUntil = peerNow.AddTicks(-1) };
            f.Federation.Rows = [live, exact, expired,
                live with { Address = live.Address with { SessionId = "private" }, IsPrivate = true },
                live with { Address = live.Address with { BrokerId = Guid.NewGuid() } }];
            var listed = await f.ListedAsync();
            string sign = offset < 0 ? "BEHIND" : "AHEAD";
            check(listed.Any(item => item.Address == live.Address),
                (offset > 0 ? "WITNESS: " : "") + "DIRECTORY_PEER_CLOCK_OFFSET_KEPT_" + sign);
            check(listed.Count == 1 && listed[0].Address == live.Address,
                "DIRECTORY_PEER_CLOCK_EXPIRED_DROPPED_" + sign);
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "zero")))
        {
            f.Measure(0, TimeSpan.Zero);
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddTicks(1) }];
            check((await f.ListedAsync()).Count == 1, "WITNESS: DIRECTORY_PEER_CLOCK_ZERO_OFFSET");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "quiet-offset")))
        {
            f.Measure(-2, TimeSpan.Zero);
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-1) }];
            check(!f.Observation.Warning && (await f.ListedAsync()).Count == 1,
                "DIRECTORY_PEER_CLOCK_MEASURED_WITHOUT_WARNING");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "offset-range")))
        {
            f.Measure(0, TimeSpan.Zero);
            f.Federation.ReadClocks = () => [f.Observation with { OffsetSeconds = double.MaxValue }];
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-1) }];
            bool failed = false;
            IReadOnlyList<BrokerSession> listed = [];
            try { listed = await f.ListedAsync(); }
            catch (Exception error) when (error is ArgumentException or OverflowException) { failed = true; }
            check(!failed && listed.Count == 0, "DIRECTORY_PEER_CLOCK_UNREPRESENTABLE_OFFSET_IGNORED");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "offset-nan")))
        {
            f.Measure(0, TimeSpan.Zero);
            f.Federation.ReadClocks = () => [f.Observation with { OffsetSeconds = double.NaN }];
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-1) }];
            bool failed = false;
            IReadOnlyList<BrokerSession> listed = [];
            try { listed = await f.ListedAsync(); }
            catch (Exception error) when (error is ArgumentException or OverflowException) { failed = true; }
            check(!failed && listed.Count == 0, "DIRECTORY_PEER_CLOCK_NAN_OFFSET_IGNORED");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "offset-date-range")))
        {
            var at = f.Clocks.Begin();
            f.Clocks.Record(f.Peer.BrokerId, f.Authorization,
                [DateTimeOffset.MaxValue.ToString("r", CultureInfo.InvariantCulture)], at, at);
            f.Clock.Advance(TimeSpan.FromSeconds(2));
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-1) }];
            bool failed = false;
            IReadOnlyList<BrokerSession> listed = [];
            try { listed = await f.ListedAsync(); }
            catch (Exception error) when (error is ArgumentException or OverflowException) { failed = true; }
            check(f.Observation is { State: "observed", OffsetSeconds: { } offset } && double.IsFinite(offset) &&
                !failed && listed.Count == 0, "DIRECTORY_PEER_CLOCK_DATE_RANGE_OFFSET_IGNORED");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "age-bound")))
        {
            f.Measure(-30, TimeSpan.Zero);
            f.Clock.Advance(TimeSpan.FromMinutes(5));
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-29) }];
            check(f.Observation.State == "observed" && (await f.ListedAsync()).Count == 1,
                "DIRECTORY_PEER_CLOCK_FRESH_AT_FIVE_MINUTES");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "rtt-bound")))
        {
            f.Measure(-30, TimeSpan.FromSeconds(2));
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-29) }];
            check(f.Observation.State == "observed" && (await f.ListedAsync()).Count == 1,
                "DIRECTORY_PEER_CLOCK_MEASURED_AT_TWO_SECONDS");
        }
        foreach (string state in new[] { "not_checked", "missing_date", "invalid_date", "high_latency",
                     "stale", "local_clock_changed", "transport_unavailable" })
        {
            await using var f = new ClockFixture(Path.Combine(scratch.Root, state));
            if (state != "not_checked") f.Measure(-30, state == "high_latency" ? TimeSpan.FromSeconds(3) : TimeSpan.Zero);
            if (state == "stale") f.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));
            if (state == "local_clock_changed") f.Clock.Jump(TimeSpan.FromSeconds(10));
            if (state is "missing_date" or "invalid_date")
            {
                var at = f.Clocks.Begin();
                f.Clocks.Record(f.Peer.BrokerId, f.Authorization,
                    state == "missing_date" ? null : ["not-a-date"], at, at);
            }
            if (state == "transport_unavailable") f.Clocks.Unavailable(f.Peer.BrokerId, f.Authorization);
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-1) }];
            check(f.Observation.State == state && (await f.ListedAsync()).Count == 0,
                (state is "high_latency" or "stale" ? "" : "WITNESS: ") +
                "DIRECTORY_PEER_CLOCK_UNUSABLE_IGNORED_" + state);
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "wrong-peer")))
        {
            f.Measure(-30, TimeSpan.Zero);
            f.Federation.ReadClocks = () => [f.Observation with { BrokerId = Guid.NewGuid() }];
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-1) }];
            check((await f.ListedAsync()).Count == 0, "DIRECTORY_PEER_CLOCK_OTHER_PEER_IGNORED");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "response-sample")))
        {
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(-29) }];
            f.Federation.BeforeDirectoryReturn = () => f.Measure(-30, TimeSpan.Zero);
            check((await f.ListedAsync()).Count == 1, "DIRECTORY_PEER_CLOCK_READ_AFTER_RESPONSE");
        }
        await using (var f = new ClockFixture(Path.Combine(scratch.Root, "response-local-now")))
        {
            f.Federation.Rows = [Row(f.Peer.BrokerId, f.Clock) with { LeaseUntil = f.Clock.GetUtcNow().AddSeconds(10) }];
            f.Federation.BeforeDirectoryReturn = () => f.Clock.Advance(TimeSpan.FromSeconds(11));
            check((await f.ListedAsync()).Count == 0, "DIRECTORY_PEER_CLOCK_LOCAL_NOW_AFTER_RESPONSE");
        }
    }

    private sealed class ClockFixture : IAsyncDisposable
    {
        internal readonly PeerObservationChecks.Clock Clock = new();
        internal readonly BrokerStore Store;
        internal readonly PeerTrust Peer;
        internal readonly BrokerStore.PeerAuthorization Authorization;
        internal readonly PeerClockObservations Clocks;
        internal readonly DirectoryFederation Federation;
        internal readonly BrokerRuntime Runtime;
        internal PeerClockView Observation => Clocks.Read(Peer.BrokerId);
        internal ClockFixture(string root)
        {
            Store = new BrokerStore(root, clock: Clock);
            Peer = PeerDirectoryShapeChecks.Peer(Store, "clock", 18503, Clock);
            Authorization = Store.CapturePeerAuthorization(Peer)!;
            Clocks = new(Clock, Store.IsPeerAuthorized);
            Federation = new(Peer.BrokerId, Row(Peer.BrokerId, Clock));
            Federation.ReadClocks = () => [Observation];
            Runtime = new(Store, Federation, [], clock: Clock);
        }
        internal void Measure(double offset, TimeSpan roundTrip)
        {
            var start = Clocks.Begin();
            Clock.Advance(roundTrip);
            string date = (start.Utc + roundTrip / 2 + TimeSpan.FromSeconds(offset)).ToString("r", CultureInfo.InvariantCulture);
            Clocks.Record(Peer.BrokerId, Authorization, [date], start, Clocks.Begin());
        }
        internal async Task<IReadOnlyList<BrokerSession>> ListedAsync() => await Runtime.DirectoryAsync(false, default);
        public async ValueTask DisposeAsync()
        {
            try { await Runtime.DisposeAsync(); }
            finally { await Federation.DisposeAsync(); Store.Dispose(); }
        }
    }

    private static BrokerSession Row(Guid broker, TimeProvider? clock = null) => new(new(broker, "synthetic-directory", Guid.NewGuid()),
        "Synthetic directory row", "synthetic", SessionCapabilities.Receive, SessionActivity.Idle, (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(2));
    private static PeerTrust Peer(BrokerStore store, string name, int port, TimeProvider? clock = null)
    {
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic " + name, "https://127.0.0.1:" + port + "/", new string('A', 64), (clock ?? TimeProvider.System).GetUtcNow());
        store.AuthorizePeer(peer); return peer;
    }
    private sealed class DirectoryFederation(Guid healthy, BrokerSession healthyRow) : IPeerFederation
    {
        internal PeerDirectory? Other { get; set; }
        internal IReadOnlyList<BrokerSession> Rows { get; set; } = [healthyRow];
        internal Func<IReadOnlyList<PeerClockView>>? ReadClocks { get; set; }
        internal Action? BeforeDirectoryReturn { get; set; }
        public IReadOnlyList<PeerClockView> ClockObservations => ReadClocks?.Invoke() ?? [];
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:18500/", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication app) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid id) { }
        public void RevokePeer(Guid id) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
        {
            BeforeDirectoryReturn?.Invoke();
            return Task.FromResult(peer.BrokerId == healthy ? new PeerDirectory(healthy, Rows) : Other!);
        }
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid id, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
