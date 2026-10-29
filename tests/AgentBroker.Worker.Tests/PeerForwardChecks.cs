using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Codex 01a1855c's review of 3f6c2a3: a paired broker that answers the forward itself with Expired was still recorded with the
// generic peer_receipt reason, so its sender's notice did not say the message expired there. The real runtime delivery loop
// forwards to a fake paired broker; the label names the rule a mutation of BrokerRuntime's forwarding result must break.
internal static class PeerForwardChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "peer-forward-" + Guid.NewGuid().ToString("N"));
        using var store = new BrokerStore(Path.Combine(root, "store"));
        Guid peerId = Guid.NewGuid();
        store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.11/", new string('B', 64), DateTimeOffset.UtcNow));
        // Busy, so the notice this check reads stays queued instead of being handed to an adapter that does not exist.
        var sender = new BrokerSession(new(store.BrokerId, "claude:peer-forward-sender", Guid.NewGuid()), "Synthetic forward sender",
            "synthetic", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Busy,
            DateTimeOffset.UtcNow.AddMinutes(2));
        store.Register(sender);
        var federation = new ExpiringPeer();
        await using var runtime = new BrokerRuntime(store, federation, []);
        using var stop = new CancellationTokenSource();
        runtime.Start(stop.Token);
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, new(peerId, "codex:peer-forward-recipient", Guid.NewGuid()),
            "Synthetic message the paired broker answers as expired.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        store.Accept(message, store.BrokerId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (store.FindReceipt(message.Id)?.Status is not MessageStatus.Refused && DateTime.UtcNow < deadline) await Task.Delay(100);
        string? notice = store.Messages(sender.Address)
            .Where(r => r.Message.Sender.SessionId == BrokerStore.NoticeSessionId && r.Message.Text.Contains(message.Id.ToString(), StringComparison.Ordinal))
            .Select(r => r.Message.Text).SingleOrDefault();
        check(federation.Submissions >= 1 && store.FindReceipt(message.Id) is { Status: MessageStatus.Refused, Reason: "peer_expired" } &&
            notice?.Contains("expired at the recipient's paired broker", StringComparison.Ordinal) == true,
            "PEER_FORWARD_EXPIRED: a paired broker's Expired answer to the forward is recorded as peer_expired, and the notice says it expired there");
        stop.Cancel();
    }

    private sealed class ExpiringPeer : IPeerFederation
    {
        internal int Submissions;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15001", new string('C', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => Task.FromResult(new PeerDirectory(peer.BrokerId, []));
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref Submissions);
            return Task.FromResult(new MessageReceipt(message, MessageStatus.Expired, DateTimeOffset.UtcNow, "expired", 1));
        }
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
