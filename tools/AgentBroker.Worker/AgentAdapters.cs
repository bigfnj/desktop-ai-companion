using AgentBroker.Core;

namespace AgentBroker.Worker;

public enum AdapterAdmission { Submitted, Delivered, Held, Refused, Uncertain, DeferredNotSubmitted }
public sealed record AdapterResult(AdapterAdmission Outcome, string? Reason = null);

// How the native envelope names a message's sender (BrokerRuntime.DescribeSender). Label is that session's own
// registration label: self-declared, it identifies and authorizes nothing. Scope is "local", or the paired broker by the
// name it advertised when the pairing was approved (PeerTrust.Name).
public sealed record PeerSenderInfo(string? Label, string Scope);

public interface ILocalAgentAdapter : IAsyncDisposable
{
    string Id { get; }
    IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => Array.Empty<AdapterDiscoveryDiagnostic>();
    Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token);
    Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token);
    // The runtime's call. An adapter that renders an envelope takes the sender's description; any other ignores it.
    Task<AdapterResult> SubmitAsync(BrokerMessage message, PeerSenderInfo? sender, CancellationToken token) => SubmitAsync(message, token);
}
