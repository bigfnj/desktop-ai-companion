using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

// Shared by the mailbox, wait and send checks: the real store, runtime and tool class over a manual
// clock. The clock fakes only GetUtcNow; timers and waits stay real, so lease and retention move by
// Advance while a blocking wait still ends on real elapsed time.
internal sealed class ToolFixture : IAsyncDisposable
{
    internal ManualClock Clock { get; } = new();
    internal string Root { get; }
    internal BrokerStore Store { get; }
    internal BrokerRuntime Runtime { get; }
    internal DefaultHttpContext Context { get; } = new();
    internal BrokerTools Tools { get; }
    // A real clock (TimeProvider.System) instead of the manual one, for checks that a wait wakes at a real deadline.
    // Adapters and a caller resolver are for the whoami checks; every other check runs with neither.
    internal ToolFixture(string name, BrokerLimits? limits = null, TimeProvider? clock = null,
        IReadOnlyList<ILocalAgentAdapter>? adapters = null, ICallerProcessResolver? callers = null)
    {
        Root = Path.Combine(AppContext.BaseDirectory, "scratch", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = new BrokerStore(Path.Combine(Root, "store"), limits, clock ?? Clock);
        Runtime = new BrokerRuntime(Store, new NoPeers(), adapters ?? [], new SessionPreferences(Root), clock ?? Clock, callers);
        Context.Items["agentbroker.identity"] = Store.BrokerId;
        Tools = new BrokerTools(Runtime, new HttpContextAccessor { HttpContext = Context });
    }
    internal BrokerSession Register(string label) => Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), label, false);
    internal BrokerSession? Current(SessionAddress address) => Store.Directory().SingleOrDefault(session => session.Address == address);
    internal BrokerSession Native(string id) => new(new(Store.BrokerId, id, Guid.NewGuid()), "Synthetic native " + id, "codex",
        SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, Clock.GetUtcNow().AddMinutes(1));
    public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Store.Dispose(); }

    internal static bool Refused(Action action, string code)
    {
        try { action(); return false; }
        catch (McpException error) { return error.Message == code; }
    }

    internal static async Task<bool> RefusedAsync(Func<Task> action, string code)
    {
        try { await action(); return false; }
        catch (McpException error) { return error.Message == code; }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private sealed class NoPeers : IPeerFederation
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
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
