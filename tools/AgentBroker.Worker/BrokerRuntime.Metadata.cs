using AgentBroker.Core;
using AgentBroker.Observations;

namespace AgentBroker.Worker;

public sealed partial class BrokerRuntime
{
    internal SessionDeclarationRegistry SessionDeclarations { get; } = new();
    private CodexApprovalObservation? approvalObservation;
    private readonly object approvalObservationGate = new();

    internal CodexApprovalView? ApprovalObservationView => Volatile.Read(ref approvalObservation) is { } observation
        ? CodexApprovalObservation.View(observation, StoreNow) : null;

    internal void SetApprovalObservation(int? count, DateTimeOffset? observedAt)
    {
        if (count is null && observedAt is null) { lock (approvalObservationGate) Volatile.Write(ref approvalObservation, null); return; }
        if (count is null || observedAt is null) throw new BrokerException("invalid_approval_observation", "A prompt observation needs a count and timestamp.");
        var observation = new CodexApprovalObservation(count.Value, observedAt.Value);
        lock (approvalObservationGate)
        {
            var now = StoreNow;
            if (CodexApprovalObservation.View(observation, now).State == "unavailable")
                throw new BrokerException("invalid_approval_observation", "The prompt observation is invalid or from an unknown future clock.");
            if (approvalObservation is { } previous && CodexApprovalObservation.View(previous, now).State != "unavailable" &&
                observation.ObservedAtUtc < previous.ObservedAtUtc) return;
            Volatile.Write(ref approvalObservation, observation);
        }
    }
}
