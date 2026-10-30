using AgentBroker.Core;

namespace AgentBroker.Worker;

public enum AdapterAdmission { Submitted, Delivered, Held, Refused, Uncertain, DeferredNotSubmitted }
// EngineBinding: what the adapter observed about the recipient's engine around this attempt, for the sender's receipt.
// Report only (N-agentbroker-code-owner-provenance-01): the runtime never reads it to decide anything.
public sealed record AdapterResult(AdapterAdmission Outcome, string? Reason = null, EngineBinding? EngineBinding = null);

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
    // whoami: the one session this exact process (ID and OS creation time) owns in the last discovery pass, or null when
    // none does or several do. An adapter without a one-process binding never claims a caller.
    SessionAddress? FindSessionByProcess(int processId, ulong startFileTime) => null;
    // Report only (N-agentbroker-code-owner-provenance-01): what the last discovery pass observed about the engine behind
    // this row. An adapter that observes nothing says null.
    EngineBinding? EngineBindingOf(SessionAddress address) => null;
}
