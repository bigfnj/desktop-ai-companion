using System.Collections.Concurrent;
using System.Diagnostics;
using AgentBroker.Core;
using AgentBroker.Worker;
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
