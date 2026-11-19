using AgentBroker.Core;

namespace AgentBroker.Worker;

public enum AdapterAdmission { Submitted, Delivered, Held, Refused, Uncertain, DeferredNotSubmitted }
// EngineBinding: what the adapter observed about the recipient's engine around this attempt, for the sender's receipt.
// Report only (N-agentbroker-code-owner-provenance-01): the runtime never reads it to decide anything.
public sealed record AdapterResult(AdapterAdmission Outcome, string? Reason = null, EngineBinding? EngineBinding = null);
// One row of a completed discovery pass's trace (ILocalAgentAdapter.LastDiscoveryTrace): the session ID, the native
// incarnation the pass renewed or kept (null when it resolved none), and the outcome code. Codes only, never text.
public sealed record DiscoveryTraceRow(string SessionId, Guid? Incarnation, string Outcome);

// How the native envelope names a message's sender (BrokerRuntime.DescribeSender). Label is that session's own
// registration label: self-declared, it identifies and authorizes nothing. Scope is "local", or the paired broker by the
// name it advertised when the pairing was approved (PeerTrust.Name).
public sealed record PeerSenderInfo(string? Label, string Scope);

public interface ILocalAgentAdapter : IAsyncDisposable
{
    string Id { get; }
    // Report only: a complete successful native pass can end busy observations for absent rows.
    bool DiscoveryCensusComplete => true;
    IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => Array.Empty<AdapterDiscoveryDiagnostic>();
    // Give work already started between passes a bounded head start before the fresh pass deadline.
    // Adapters without such work keep their existing discovery schedule.
    Task PrepareDiscoveryAsync(TimeSpan maximumWait, CancellationToken stop) => Task.CompletedTask;
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
    // Report only (N-broker-native-lease-gap-01, lane N round 2): what the last COMPLETED discovery pass did with each row
    // it considered, one entry per session ID, ordered by session ID. Replaced whole at the end of a completed pass and never
    // changed afterwards; a failed pass keeps the previous snapshot. The runtime only writes it to the events file
    // (discovery_pass) and never reads it to decide anything. Null means no completed pass yet, or an adapter that reports no
    // per-row outcome; the runtime then records every row the pass returned as renewed.
    IReadOnlyList<DiscoveryTraceRow>? LastDiscoveryTrace => null;
}
