using AgentBroker.Core;

namespace AgentBroker.Worker;

public enum AdapterAdmission { Submitted, Delivered, Held, Refused, Uncertain, DeferredNotSubmitted }
public sealed record AdapterResult(AdapterAdmission Outcome, string? Reason = null);

public interface ILocalAgentAdapter : IAsyncDisposable
{
    string Id { get; }
    Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token);
    Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token);
}
