using System.Collections.Concurrent;
using System.Diagnostics;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class RuntimeLifecycleChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "runtime-" + Guid.NewGuid().ToString("N"));
        using (var store = new BrokerStore(Path.Combine(scratch, "directory")))
        {
            for (int i = 0; i < 8; i++)
                store.AuthorizePeer(new(Guid.NewGuid(), "Synthetic peer " + i, "https://127.0.0.1:" + (15000 + i), new string('A', 64), DateTimeOffset.UtcNow));
            var peers = store.Peers();
            await using var federation = new SlowDirectoryFederation(peers[0].BrokerId);
            await using var runtime = new BrokerRuntime(store, federation, []);
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic local", false);
            var elapsed = Stopwatch.StartNew();
            var directory = await runtime.DirectoryAsync(false, default);
            elapsed.Stop();
            check(elapsed.Elapsed < TimeSpan.FromSeconds(8), "all peer directories share one bounded five-second budget despite transports ignoring cancellation");
            check(directory.Any(s => s.Address == local.Address) && directory.Count == 2 &&
                directory.Any(s => s.Address.BrokerId == peers[0].BrokerId), "directory budget returns local and completed live peer sessions without waiting for unavailable peers");
            check(federation.PeakConcurrent is > 1 and <= 4, "peer directory lookups run concurrently with a fixed limit of four");
            check(federation.Calls < peers.Count, "global directory deadline stops starting additional unavailable peer lookups");
            int beforePeerList = federation.Calls;
            var ownDirectory = await runtime.DirectoryAsync(true, default);
            check(ownDirectory.Count == 1 && federation.Calls == beforePeerList, "peer-facing directory never performs transitive peer discovery");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await runtime.DirectoryAsync(false, cancelled.Token); throw new InvalidOperationException("Caller cancellation was swallowed."); }
            catch (OperationCanceledException) { check(true, "directory caller cancellation remains observable instead of returning a success-shaped partial result"); }
            federation.ReleasePending();
            await federation.DrainAsync();
            check(federation.Active == 0, "late transport completions drain without publishing additional directory sessions");
            federation.UnavailablePeersReturnEmpty = true;
            federation.OnReady = peer => store.RevokePeer(peer.BrokerId);
            var revoked = await runtime.DirectoryAsync(false, default);
            check(revoked.Count == 1 && revoked[0].Address == local.Address,
                "a peer revoked during directory lookup cannot publish a late session listing");
        }
        using (var store = new BrokerStore(Path.Combine(scratch, "shutdown")))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            // Since lane broker-next (P3-7) a discovery failure no longer faults the loop (RUNTIME_ADAPTER_FAILURE_RECORDED
            // below), so the loop fault injected here is a delivery's: the adapter lists a session and its submission throws.
            var failing = new LifecycleAdapter("faulting", failDispose: true, listSession: true);
            var healthy = new LifecycleAdapter("healthy");
            var runtime = new BrokerRuntime(store, federation, [failing, healthy]);
            runtime.Start(default);
            var started = Stopwatch.StartNew();
            while (failing.Address is null || !store.Directory().Any(session => session.Address == failing.Address))
            {
                if (started.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The faulting adapter's session was never listed.");
                await Task.Delay(20);
            }
            var poller = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic lifecycle sender", false);
            store.Accept(new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, poller.Address, failing.Address!,
                "Synthetic lifecycle delivery.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)), store.BrokerId);
            while (Volatile.Read(ref failing.Submissions) == 0)
            {
                if (started.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("The faulting delivery never ran.");
                await Task.Delay(20);
            }
            try { await runtime.DisposeAsync(); throw new InvalidOperationException("Injected lifecycle faults were lost."); }
            catch (AggregateException error)
            {
                check(error.InnerExceptions.Count == 2, "runtime preserves both loop and adapter shutdown failures after attempting cleanup");
            }
            check(failing.DisposeCalls == 1 && healthy.DisposeCalls == 1, "a loop fault and first-adapter disposal failure cannot skip remaining native adapter cleanup");
            try { await runtime.DisposeAsync(); }
            catch (AggregateException) { }
            check(failing.DisposeCalls == 1 && healthy.DisposeCalls == 1, "repeated runtime disposal shares the original cleanup task and never disposes adapters twice");
        }
        await IndependentAdapterLoopsAsync(check, scratch);
        await AdapterFailureRecordedAsync(check, scratch);
        await SlowCodexRowAsync(check);
        await CodexReconnectAsync(check);
    }

    // N-broker-next-03: after a failed lookup the provider drops the owner connection and the next lookup reconnects. The
    // reconnect's router verification is synchronous (WindowsDesktopConnection.ConnectAsync) and the initialize request after
    // it honours the token it is given. With the pass token, a reconnect outlasting the pass deadline failed at initialize and
    // was thrown away, so the next pass reconnected again: while a reconnect costs more than a pass, no pass renewed a row.
    // Scaled down: pass deadline 400 ms, reconnect 700 ms (ignoring cancellation, as the verification does).
    private static async Task CodexReconnectAsync(Action<bool, string> check)
    {
        var owner = new ReconnectOwner();
        int connects = 0;
        var connectCost = TimeSpan.FromMilliseconds(700);
        var provider = new CodexNativeProvider(true, async token =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(connectCost);
                token.ThrowIfCancellationRequested(); // The initialize request the library sends after the verification.
                return owner;
            }, () => { },
            _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
                [new(owner.Thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]),
            null, false, () => { });
        await using var adapter = new CodexNativeAdapter(provider, enableDelivery: true);
        var brokerId = Guid.NewGuid();
        async Task<int> PassAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try { return (await adapter.DiscoverAsync(brokerId, deadline.Token)).Count; }
            catch (OperationCanceledException) { return 0; }
        }
        int renewed = 0;
        for (int pass = 0; pass < 4; pass++) renewed += await PassAsync();
        check(renewed == 3 && Volatile.Read(ref connects) == 1,
            "CODEX_RECONNECT_KEPT: a reconnect that outlasts the pass deadline is kept, so the next pass renews the row (renewed " +
            renewed + " of 4 passes, " + Volatile.Read(ref connects) + " connects)");
        // A lookup that FAILED still discards the connection: the library closed its protocol, and a late answer could be
        // read as the next request's (DesktopAdapter.RequestAsync). Only a connection no request ever failed on is kept.
        connectCost = TimeSpan.Zero;
        owner.Failure = new IOException("Synthetic owner lookup failure.");
        await PassAsync();
        owner.Failure = null;
        int before = Volatile.Read(ref connects);
        int after = await PassAsync();
        check(after == 1 && Volatile.Read(ref connects) == before + 1,
            "CODEX_RECONNECT_KEPT WITNESS: a lookup that failed still discards the connection and the next lookup reconnects (" +
            (Volatile.Read(ref connects) - before) + " reconnects)");
    }

    private sealed class ReconnectOwner : ICodexOwnerConnection
    {
        internal Guid Thread { get; } = Guid.NewGuid();
        internal Exception? Failure;
        public bool NativeIdleGuard => true;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) return Task.FromException<DesktopOwnerBinding?>(failure);
            return Task.FromResult<DesktopOwnerBinding?>(new DesktopOwnerBinding(conversationId, Guid.NewGuid(), Guid.NewGuid(), 12345,
                134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned });
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // N-agentbroker-native-flicker-01, the runtime half: one adapter's slow pass (a transport that outlives the pass
    // deadline and ignores it) must not let another adapter's leases lapse. Scaled down: pass deadline and pause 300 ms,
    // the other adapter's lease 3 s, the slow pass 5 s. When the adapters ran one after another, the other adapter was
    // renewed only every 5.3 s and its row was missing for about 2.3 s of each cycle; run independently it renews every
    // 0.3 s, which leaves about 2.7 s of margin for a stalled thread pool under load (independent review P3-8: 1.2 s
    // and 2 s left only 0.9 s).
    private static async Task IndependentAdapterLoopsAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "independent-loops"));
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        var slow = new TimedAdapter("slow-native", store.BrokerId, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), ignoreCancellation: true);
        var peer = new TimedAdapter("peer-native", store.BrokerId, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        var runtime = new BrokerRuntime(store, federation, [slow, peer])
        { DiscoveryPassDeadline = TimeSpan.FromMilliseconds(300), DiscoveryInterval = TimeSpan.FromMilliseconds(300) };
        await using (runtime)
        {
            runtime.Start(default);
            var first = Stopwatch.StartNew();
            while (!store.Directory().Any(session => session.Address == peer.Address) && first.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            int samples = 0, missing = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                samples++;
                if (!store.Directory().Any(session => session.Address == peer.Address)) missing++;
                await Task.Delay(50);
            }
            check(samples >= 40 && missing == 0 && peer.Passes >= 10,
                "RUNTIME_ADAPTERS_INDEPENDENT: a slow adapter's pass never lets another adapter's lease lapse (" + missing + " of " + samples + " samples missing)");
            check(slow.Passes >= 1 && slow.Completed >= 1,
                "RUNTIME_ADAPTERS_INDEPENDENT WITNESS: the slow adapter ran its long pass past the deadline");
        }
    }

    // Independent review of 1.6.0, P3-7: with one loop per adapter, an exception outside the loop's catch list ended that
    // adapter's discovery in silence while the other went on. It is now that adapter's error and the loop tries again.
    private static async Task AdapterFailureRecordedAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "adapter-failure"));
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        var broken = new LifecycleAdapter("broken-native", faultDiscovery: true);
        var peer = new TimedAdapter("peer-native", store.BrokerId, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        var runtime = new BrokerRuntime(store, federation, [broken, peer])
        { DiscoveryPassDeadline = TimeSpan.FromMilliseconds(300), DiscoveryInterval = TimeSpan.FromMilliseconds(100) };
        runtime.Start(default);
        var watch = Stopwatch.StartNew();
        while (Volatile.Read(ref broken.Passes) < 3 && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        int passes = Volatile.Read(ref broken.Passes);
        bool recorded = runtime.AdapterErrors.TryGetValue("broken-native", out var error) && error == "adapter_failed";
        bool peerListed = store.Directory().Any(session => session.Address == peer.Address);
        // Disposed before the checks: a loop that died would rethrow its fault here and hide the check's own label.
        try { await runtime.DisposeAsync(); } catch (Exception) { }
        check(passes >= 3 && recorded,
            "RUNTIME_ADAPTER_FAILURE_RECORDED: an unexpected discovery failure is that adapter's error and its loop keeps trying (" + passes + " passes)");
        check(peerListed, "RUNTIME_ADAPTER_FAILURE_RECORDED WITNESS: the other adapter keeps its row");
    }

    // N-agentbroker-native-flicker-01, the Codex half: a conversation whose owner lookup outlives the pass deadline is
    // skipped and goes last on the next pass, so every other conversation is still renewed; a lookup failing at once is
    // skipped without ending the pass. The diagnostic says so on every pass that skipped one.
    private static async Task SlowCodexRowAsync(Action<bool, string> check)
    {
        var provider = new SlowRowProvider(3);
        await using var adapter = new CodexNativeAdapter(provider);
        var brokerId = Guid.NewGuid();
        async Task<IReadOnlyList<BrokerSession>> PassAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            return await adapter.DiscoverAsync(brokerId, deadline.Token);
        }
        string Ids(IReadOnlyList<BrokerSession> sessions) => string.Join(",", sessions.Select(session =>
            Array.IndexOf(provider.Threads, Guid.Parse(session.Address.SessionId["codex:".Length..]))).Order());
        provider.Slow = provider.Threads[1];
        IReadOnlyList<BrokerSession>? firstPass = null;
        try { firstPass = await PassAsync(); } catch (OperationCanceledException) { }
        check(firstPass is not null && Ids(firstPass) == "0",
            "CODEX_SLOW_ROW_PARTIAL: a lookup that outlives the pass deadline ends the pass with what was resolved, not a failure (" + (firstPass is null ? "failed" : Ids(firstPass)) + ")");
        check(adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped" && item.Description.StartsWith("2 Codex conversations", StringComparison.Ordinal)),
            "CODEX_SLOW_ROW_DIAGNOSTIC: the pass that skipped conversations names how many");
        var second = await PassAsync();
        check(Ids(second) == "0,2" && provider.Order[^3..].SequenceEqual(new[] { provider.Threads[0], provider.Threads[2], provider.Threads[1] }),
            "CODEX_SLOW_ROW_LAST: the slow conversation goes last on the next pass, so every other one is renewed (" + Ids(second) + ")");
        provider.Slow = null; provider.Failing = provider.Threads[0];
        IReadOnlyList<BrokerSession>? third = null;
        try { third = await PassAsync(); } catch (IOException) { }
        check(third is not null && Ids(third) == "1,2",
            "CODEX_SLOW_ROW_FAILING: a lookup that fails at once is skipped and the pass goes on (" + (third is null ? "failed" : Ids(third)) + ")");
        provider.Failing = null;
        // The router's error answer for one conversation (owner_discovery_rejected) skips that row, not the pass
        // (independent review of 1.6.0, P3-5).
        provider.Rejected = provider.Threads[1];
        IReadOnlyList<BrokerSession>? rejectedPass = null;
        try { rejectedPass = await PassAsync(); } catch (AdapterDiscoveryException) { }
        check(rejectedPass is not null && Ids(rejectedPass) == "0,2",
            "CODEX_SLOW_ROW_REJECTED: one conversation's owner_discovery_rejected skips that row and the others are renewed (" +
            (rejectedPass is null ? "failed" : Ids(rejectedPass)) + ")");
        provider.Rejected = null;
        var healthy = await PassAsync();
        check(Ids(healthy) == "0,1,2" && !adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped"),
            "CODEX_SLOW_ROW_DIAGNOSTIC WITNESS: a pass that resolves every conversation renews all and reports no skip");
    }

    private sealed class TimedAdapter(string id, Guid brokerId, TimeSpan pass, TimeSpan lease, bool ignoreCancellation = false) : ILocalAgentAdapter
    {
        public string Id => id;
        public SessionAddress Address { get; } = new(brokerId, id + ":" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        internal int Passes, Completed;
        public async Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid broker, CancellationToken token)
        {
            Interlocked.Increment(ref Passes);
            if (pass > TimeSpan.Zero) await Task.Delay(pass, ignoreCancellation ? CancellationToken.None : token);
            Interlocked.Increment(ref Completed);
            return [new BrokerSession(Address, "Synthetic " + id, id, SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                SessionActivity.Idle, DateTimeOffset.UtcNow + lease)];
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A provider with three observed conversations; one may hang until the pass deadline or fail at once.
    private sealed class SlowRowProvider(int count) : ICodexNativeProvider
    {
        internal Guid[] Threads { get; } = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        internal Guid? Slow, Failing, Rejected;
        internal List<Guid> Order { get; } = [];
        public bool NativeIdleGuard => false;
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(Threads.Select(thread =>
                new DesktopDirectorySession(thread, DesktopSessionActivity.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray());
        public async Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token)
        {
            Order.Add(conversationId);
            if (conversationId == Slow) await Task.Delay(Timeout.Infinite, token);
            if (conversationId == Failing) throw new IOException("Synthetic owner connection failure.");
            if (conversationId == Rejected) throw new DesktopAdapterException("owner_discovery_rejected");
            return new DesktopOwnerBinding(conversationId, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned };
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LifecycleAdapter(string id, bool faultDiscovery = false, bool failDispose = false, bool listSession = false) : ILocalAgentAdapter
    {
        public string Id => id;
        internal int DisposeCalls, Passes, Submissions;
        internal SessionAddress? Address { get; private set; }
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Interlocked.Increment(ref Passes);
            if (faultDiscovery) return Task.FromException<IReadOnlyList<BrokerSession>>(new InvalidOperationException("Synthetic discovery failure."));
            if (!listSession) return Task.FromResult<IReadOnlyList<BrokerSession>>([]);
            Address ??= new(brokerId, id + ":synthetic", Guid.NewGuid());
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new BrokerSession(Address, "Synthetic " + id, id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))]);
        }
        // An unexpected delivery failure: it still faults the runtime's delivery loop, which shutdown reports.
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        { Interlocked.Increment(ref Submissions); throw new NotSupportedException("Synthetic delivery failure."); }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return failDispose ? ValueTask.FromException(new IOException("Synthetic disposal failure.")) : ValueTask.CompletedTask;
        }
    }

    private sealed class SlowDirectoryFederation(Guid readyPeer) : IPeerFederation
    {
        private readonly ConcurrentBag<TaskCompletionSource<PeerDirectory>> pending = [];
        private int active, calls, peak;
        internal int Calls => Volatile.Read(ref calls);
        internal int Active => Volatile.Read(ref active);
        internal int PeakConcurrent => Volatile.Read(ref peak);
        internal Action<PeerTrust>? OnReady;
        internal bool UnavailablePeersReturnEmpty;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15000", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public async Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            int count = Interlocked.Increment(ref active), old;
            do { old = Volatile.Read(ref peak); if (old >= count) break; }
            while (Interlocked.CompareExchange(ref peak, count, old) != old);
            try
            {
                if (peer.BrokerId == readyPeer)
                {
                    await Task.Delay(25, token);
                    var session = new BrokerSession(new(peer.BrokerId, "synthetic-ready", Guid.NewGuid()), "Ready peer", "fake",
                        SessionCapabilities.Receive, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
                    OnReady?.Invoke(peer);
                    return new(peer.BrokerId, [session, session with { IsPrivate = true }, session with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) },
                        session with { Address = session.Address with { BrokerId = Guid.NewGuid() } }]);
                }
                if (UnavailablePeersReturnEmpty) return new(peer.BrokerId, []);
                // Intentionally ignores cancellation to exercise the public directory's hard wait bound.
                var completion = new TaskCompletionSource<PeerDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(completion);
                return await completion.Task;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        internal void ReleasePending()
        { foreach (var completion in pending) completion.TrySetResult(new(Guid.Empty, [])); }
        internal async Task DrainAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Active != 0) await Task.Delay(10, deadline.Token);
        }
        public async ValueTask DisposeAsync() { ReleasePending(); await DrainAsync(); }
    }
}
