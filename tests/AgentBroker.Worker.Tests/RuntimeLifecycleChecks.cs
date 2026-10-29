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
            var failing = new LifecycleAdapter("faulting", faultDiscovery: true, failDispose: true);
            var healthy = new LifecycleAdapter("healthy");
            var runtime = new BrokerRuntime(store, federation, [failing, healthy]);
            runtime.Start(default);
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
        await SlowCodexRowAsync(check);
    }

    // N-agentbroker-native-flicker-01, the runtime half: one adapter's slow pass (a transport that outlives the pass
    // deadline and ignores it) must not let another adapter's leases lapse. Scaled down: pass deadline and pause 300 ms,
    // the other adapter's lease 1.2 s, the slow pass 2 s. When the adapters ran one after another, the other adapter was
    // renewed only every 2.3 s, and its row dropped out of the directory between renewals.
    private static async Task IndependentAdapterLoopsAsync(Action<bool, string> check, string scratch)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "independent-loops"));
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        var slow = new TimedAdapter("slow-native", store.BrokerId, TimeSpan.FromSeconds(2), ignoreCancellation: true);
        var peer = new TimedAdapter("peer-native", store.BrokerId, TimeSpan.Zero);
        var runtime = new BrokerRuntime(store, federation, [slow, peer])
        { DiscoveryPassDeadline = TimeSpan.FromMilliseconds(300), DiscoveryInterval = TimeSpan.FromMilliseconds(300) };
        await using (runtime)
        {
            runtime.Start(default);
            var first = Stopwatch.StartNew();
            while (!store.Directory().Any(session => session.Address == peer.Address) && first.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            int samples = 0, missing = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(4))
            {
                samples++;
                if (!store.Directory().Any(session => session.Address == peer.Address)) missing++;
                await Task.Delay(50);
            }
            check(samples >= 20 && missing == 0 && peer.Passes >= 6,
                "RUNTIME_ADAPTERS_INDEPENDENT: a slow adapter's pass never lets another adapter's lease lapse (" + missing + " of " + samples + " samples missing)");
            check(slow.Passes >= 1 && slow.Completed >= 1,
                "RUNTIME_ADAPTERS_INDEPENDENT WITNESS: the slow adapter ran its long pass past the deadline");
        }
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
        var healthy = await PassAsync();
        check(Ids(healthy) == "0,1,2" && !adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped"),
            "CODEX_SLOW_ROW_DIAGNOSTIC WITNESS: a pass that resolves every conversation renews all and reports no skip");
    }

    private sealed class TimedAdapter(string id, Guid brokerId, TimeSpan pass, bool ignoreCancellation = false) : ILocalAgentAdapter
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
                SessionActivity.Idle, DateTimeOffset.UtcNow.AddMilliseconds(1200))];
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A provider with three observed conversations; one may hang until the pass deadline or fail at once.
    private sealed class SlowRowProvider(int count) : ICodexNativeProvider
    {
        internal Guid[] Threads { get; } = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        internal Guid? Slow, Failing;
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
            return new DesktopOwnerBinding(conversationId, Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned };
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LifecycleAdapter(string id, bool faultDiscovery = false, bool failDispose = false) : ILocalAgentAdapter
    {
        public string Id => id;
        internal int DisposeCalls;
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
            => faultDiscovery ? Task.FromException<IReadOnlyList<BrokerSession>>(new InvalidOperationException("Synthetic discovery failure."))
                : Task.FromResult<IReadOnlyList<BrokerSession>>([]);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
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
