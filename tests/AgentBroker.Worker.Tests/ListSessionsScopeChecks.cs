using System.Diagnostics;
using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class ListSessionsScopeChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: LIST_SESSIONS_SCOPE_EXECUTED");
        await using var federation = new ScopeFederation();
        var clock = new ScopeClock();
        await using var fixture = new ToolFixture("list-sessions-scope", clock: clock, federation: federation);
        DateTimeOffset initialNow = clock.GetUtcNow();
        var trust = new PeerTrust(Guid.NewGuid(), "Synthetic scope peer", "https://127.0.0.1:18521/",
            new string('A', 64), initialNow);
        fixture.Store.AuthorizePeer(trust);
        federation.Remote = new(new(trust.BrokerId, "scope-remote", Guid.NewGuid()), "Synthetic remote scope row",
            "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle,
            initialNow.AddMinutes(2));

        var seededAddresses = new List<SessionAddress>();
        for (int index = 0; index < 16; index++)
        {
            var row = new BrokerSession(new(fixture.Store.BrokerId, "scope-public-" + index.ToString("D2"), Guid.NewGuid()),
                "Synthetic public scope row " + index, "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply,
                SessionActivity.Idle, initialNow.AddMinutes(2));
            fixture.Store.Register(row);
            seededAddresses.Add(row.Address);
        }
        var native = new BrokerSession(new(fixture.Store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            "Synthetic Unknown native scope row", "codex-native", SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
            SessionActivity.Unknown, initialNow.AddMinutes(2));
        fixture.Store.Register(native);
        seededAddresses.Add(native.Address);
        var privateRow = new BrokerSession(new(fixture.Store.BrokerId, "scope-private", Guid.NewGuid()),
            "Synthetic private scope row", "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply,
            SessionActivity.Idle, initialNow.AddMinutes(2), IsPrivate: true);
        fixture.Store.Register(privateRow);
        seededAddresses.Add(privateRow.Address);
        var away = fixture.Register("Synthetic retained scope mailbox");
        seededAddresses.Add(away.Address);
        clock.Advance(TimeSpan.FromSeconds(61));
        var observations = (DeliveryObservations)typeof(BrokerRuntime).GetField("observations",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Runtime)!;
        observations.ObserveDirectory(native with { Activity = SessionActivity.Busy });

        // There are 19 local rows, of which one is private and one is a retained Away mailbox.
        // Only 17 public present rows may leave this broker. The peer row uses this injected clock.
        SessionAddress[] expectedLocal = Ordered(seededAddresses);
        string statePath = Path.Combine(fixture.Root, "store", "broker-state.json");
        byte[] before = File.ReadAllBytes(statePath);
        var timer = Stopwatch.StartNew();
        var firstLocal = await fixture.Tools.ListSessions(default, scope: "local");
        timer.Stop();
        check(timer.Elapsed < TimeSpan.FromMilliseconds(500) && federation.Fetches == 0,
            "LIST_SESSIONS_LOCAL_SCOPE_EXCLUDES_PEERS: local scope returns within 500 ms without starting the two-second peer fetch");

        var local = await ScanAsync(fixture, "local", expectedLocal.Length, firstPage: firstLocal);
        check(local.Rows.Any(row => row.Address == privateRow.Address && row.IsPrivate) &&
            local.Rows.Any(row => row.Address == away.Address && row.Activity == SessionActivity.Away) &&
            local.Rows.Any(row => row.Address == native.Address && row.Activity == SessionActivity.Busy) &&
            local.Rows.All(row => row.Address.BrokerId == fixture.Store.BrokerId) && federation.Fetches == 0,
            "LIST_SESSIONS_LOCAL_SCOPE_PRESERVES_LOCAL_VIEW: the local caller retains private and Away rows and fresh native Busy presentation without any remote row");
        check(local.Rows.Count > 0 && local.Rows.All(row => row.PendingMessageCount == 0),
            "LIST_SESSIONS_LOCAL_SCOPE_LOCAL_METADATA: zero pending counts remain available to the local caller");
        check(local.Completed && expectedLocal.Length == 19 && local.PageSizes.SequenceEqual([16, 3]) &&
            local.Rows.Select(row => row.Address).SequenceEqual(expectedLocal),
            "LIST_SESSIONS_LOCAL_SCOPE_PAGINATION: a bounded local scan returns all nineteen ordered addresses once across two pages");

        int beforeDefault = federation.Fetches;
        var defaultScope = await ScanAsync(fixture, null, expectedLocal.Length + 1);
        check(defaultScope.Completed && defaultScope.Rows.Any(row => row.Address == federation.Remote.Address) &&
            defaultScope.Rows.Count == expectedLocal.Length + 1 &&
            federation.Fetches - beforeDefault == defaultScope.PageSizes.Count,
            "LIST_SESSIONS_DEFAULT_FEDERATED: omitting scope fetches the paired directory and includes its row");
        int beforeExplicit = federation.Fetches;
        var explicitScope = await ScanAsync(fixture, "federated", expectedLocal.Length + 1);
        check(explicitScope.Completed && explicitScope.Rows.SequenceEqual(defaultScope.Rows) &&
            federation.Fetches - beforeExplicit == explicitScope.PageSizes.Count,
            "WITNESS: LIST_SESSIONS_EXPLICIT_FEDERATED: explicit federated scope preserves the omitted-scope ordered results");

        int beforeInvalid = federation.Fetches;
        bool allInvalidRefused = true;
        clock.RejectUtcReads = true;
        try
        {
            foreach (string invalid in new[] { "", "Local", "Federated", "local ", "federated ", "unsupported" })
            {
                try
                {
                    allInvalidRefused &= await ToolFixture.RefusedAsync(
                        () => fixture.Tools.ListSessions(default, scope: invalid), "invalid_scope");
                }
                catch (InvalidOperationException error) when (error.Message == ScopeClock.DirectoryWorkStarted)
                { allInvalidRefused = false; }
            }
        }
        finally { clock.RejectUtcReads = false; }
        check(allInvalidRefused && clock.RejectedUtcReads == 0 && federation.Fetches == beforeInvalid,
            "LIST_SESSIONS_INVALID_SCOPE: exact scope values are required before any directory clock read or peer fetch");

        // Scope is a listing choice, never transport authority. A paired caller gets the public,
        // present local view under both values and never receives process-local hint members.
        int beforePeer = federation.Fetches;
        DirectoryScan peerLocal;
        DirectoryScan peerFederated;
        object? previousCaller = fixture.Context.Items["agentbroker.identity"];
        fixture.Context.Items["agentbroker.identity"] = trust.BrokerId;
        try
        {
            peerLocal = await ScanAsync(fixture, "local", 17);
            peerFederated = await ScanAsync(fixture, "federated", 17);
        }
        finally { fixture.Context.Items["agentbroker.identity"] = previousCaller; }
        static bool PeerSafe(DirectoryScan scan, Guid localBroker, SessionAddress privateAddress, SessionAddress awayAddress) =>
            scan.Completed && scan.Rows.Count == 17 && scan.Rows.All(row => row.Address.BrokerId == localBroker &&
                row.Address != privateAddress && row.Address != awayAddress && !row.IsPrivate && row.Activity != SessionActivity.Away &&
                row.EngineBinding is null && row.LocalDeclaration is null && row.LocalTitle is null && row.PendingMessageCount is null);
        check(PeerSafe(peerLocal, fixture.Store.BrokerId, privateRow.Address, away.Address) &&
            PeerSafe(peerFederated, fixture.Store.BrokerId, privateRow.Address, away.Address) &&
            peerLocal.Rows.Select(row => row.Address).SequenceEqual(peerFederated.Rows.Select(row => row.Address)) &&
            federation.Fetches == beforePeer,
            "LIST_SESSIONS_SCOPE_PEER_PRIVACY: both scopes preserve private-row and local-metadata boundaries and forbid transitive peer fetches");

        // Cancel only after the controlled fetch begins, so a loaded scheduler cannot turn this
        // into a pre-cancelled-token test that never exercises the transport cancellation path.
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        federation.NextStarted = started;
        federation.NextCancelled = cancellationObserved;
        int beforeCancelled = federation.CancelledFetches;
        int beforeCancellationFetch = federation.Fetches;
        var pending = fixture.Tools.ListSessions(stop.Token, scope: "federated");
        bool cancelled = false;
        bool transportCancelled = false;
        bool fetchStarted = false, callerCancellationPropagated = false;
        try
        {
            CancellationToken transportToken = default;
            try
            {
                transportToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fetchStarted = true;
            }
            catch (TimeoutException) { }
            // A deadline already reached cannot substitute for propagation from this caller.
            bool alreadyCancelled = transportToken.IsCancellationRequested;
            stop.Cancel();
            callerCancellationPropagated = fetchStarted && !alreadyCancelled && transportToken.IsCancellationRequested;
            try { await pending; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
            // Runtime's outer WaitAsync can finish before the transport continuation increments
            // its counter. Wait for that concrete continuation instead of asserting on its race.
            try { transportCancelled = await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }
        finally
        {
            stop.Cancel();
            // Observe the task even if startup timed out; the stub owns no work after disposal.
            try { await pending; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        check(fetchStarted && callerCancellationPropagated && cancelled && transportCancelled &&
            federation.Fetches == beforeCancellationFetch + 1 &&
            federation.CancelledFetches == beforeCancelled + 1,
            "LIST_SESSIONS_SCOPE_CANCELLATION: cancelling a federated listing cancels its already-started peer fetch");
        check(File.ReadAllBytes(statePath).SequenceEqual(before) && fixture.Runtime.ActiveWaitCount == 0,
            "WITNESS: LIST_SESSIONS_SCOPE_READ_ONLY: listing and validation preserve durable bytes and never occupy a broker wait slot");
    }

    private static SessionAddress[] Ordered(IEnumerable<SessionAddress> addresses) => addresses
        .OrderBy(address => address.BrokerId).ThenBy(address => address.SessionId, StringComparer.Ordinal)
        .ThenBy(address => address.Incarnation).ToArray();

    // Three pages are the independent bound for the largest fixture (twenty rows, page size 16).
    // A duplicate or non-progressing cursor terminates locally and fails the single paging check.
    private static async Task<DirectoryScan> ScanAsync(ToolFixture fixture, string? scope, int expectedRows,
        IReadOnlyList<ListedSession>? firstPage = null)
    {
        var rows = new List<ListedSession>();
        var sizes = new List<int>();
        var seen = new HashSet<SessionAddress>();
        SessionAddress? cursor = null;
        int maximumPages = (expectedRows + 15) / 16 + 1;
        for (int index = 0; index < maximumPages; index++)
        {
            var page = index == 0 && firstPage is not null ? firstPage : scope is null
                ? await fixture.Tools.ListSessions(default, limit: 16, afterAddress: cursor)
                : await fixture.Tools.ListSessions(default, limit: 16, afterAddress: cursor, scope: scope);
            sizes.Add(page.Count);
            rows.AddRange(page);
            if (page.Count > 16 || page.Any(row => !seen.Add(row.Address))) return new(rows, sizes, false);
            if (page.Count < 16) return new(rows, sizes, true);
            cursor = page[^1].Address;
        }
        return new(rows, sizes, false);
    }

    private sealed record DirectoryScan(IReadOnlyList<ListedSession> Rows, IReadOnlyList<int> PageSizes, bool Completed);

    // A test-owned clock makes directory work observable without adding a product test API.
    // DirectoryAsync reads this clock for its populated local view. A valid early scope guard must not read it.
    private sealed class ScopeClock : TimeProvider
    {
        internal const string DirectoryWorkStarted = "A10_SCOPE_DIRECTORY_WORK_STARTED";
        private long ticks = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).Ticks;
        private int rejectUtcReads, rejectedUtcReads;
        internal bool RejectUtcReads { set => Volatile.Write(ref rejectUtcReads, value ? 1 : 0); }
        internal int RejectedUtcReads => Volatile.Read(ref rejectedUtcReads);
        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref rejectUtcReads) != 0)
            {
                Interlocked.Increment(ref rejectedUtcReads);
                throw new InvalidOperationException(DirectoryWorkStarted);
            }
            return new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero);
        }
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private sealed class ScopeFederation : IPeerFederation
    {
        private int fetches;
        private int cancelledFetches;
        internal int Fetches => Volatile.Read(ref fetches);
        internal int CancelledFetches => Volatile.Read(ref cancelledFetches);
        internal BrokerSession Remote { get; set; } = null!;
        internal TaskCompletionSource<CancellationToken>? NextStarted;
        internal TaskCompletionSource<bool>? NextCancelled;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => throw new NotSupportedException();
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid id) { }
        public void RevokePeer(Guid id) { }
        public async Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
        {
            Interlocked.Increment(ref fetches);
            Interlocked.Exchange(ref NextStarted, null)?.TrySetResult(token);
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancelledFetches);
                Interlocked.Exchange(ref NextCancelled, null)?.TrySetResult(true);
                throw;
            }
            return new(peer.BrokerId, [Remote, Remote with
            {
                Address = Remote.Address with { SessionId = "scope-remote-private" },
                Label = "Synthetic private remote scope row", IsPrivate = true
            }]);
        }
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid id, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
