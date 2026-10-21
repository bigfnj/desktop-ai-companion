using AgentBroker.Core;

namespace AgentBroker.Worker;

public sealed record BrokerAdvertisement(Guid BrokerId, string Name, string Endpoint,
    string CertificateSha256, DateTimeOffset SeenAt);
public sealed record PairingOffer(Guid RequestId, BrokerAdvertisement Peer,
    string VerificationCode, DateTimeOffset ExpiresAt, bool Outbound,
    bool LocalApproved = false, bool RemoteApproved = false);
public sealed record PeerDirectory(Guid BrokerId, IReadOnlyList<BrokerSession> Sessions);

public interface IPeerFederation : IAsyncDisposable
{
    IReadOnlyList<BrokerAdvertisement> Discovered { get; }
    IReadOnlyList<PairingOffer> PairingOffers { get; }
    BrokerAdvertisement LocalAdvertisement { get; }
    Guid? AuthenticatePeer(HttpContext context);
    void MapEndpoints(WebApplication application);
    Task StartAsync(CancellationToken token);
    // Requests an offer only. Both local users must separately compare and approve its full code.
    Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token);
    Task ApprovePairAsync(Guid requestId, CancellationToken token);
    void RejectPair(Guid requestId);
    void RevokePeer(Guid brokerId);
    Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token);
    Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token);
    Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token);
}
