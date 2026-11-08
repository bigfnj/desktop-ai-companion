using AgentBroker.Core;

namespace AgentBroker.Worker;

/// <summary>Serializes selection with guarded settlement and report publication; never holds the gate over IO.</summary>
internal sealed class DeferredDeliveryClaims(BrokerStore store, Action? beforeStoreSelection = null,
    Action? beforeObservation = null)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, (Guid Admission, DateTimeOffset Until)> deferred = new();

    internal DeliveryClaim? BeginDelivery()
    {
        lock (gate)
        {
            foreach (var item in deferred.Where(p => p.Value.Until <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray())
                deferred.Remove(item);
            var excluded = deferred.ToDictionary(pair => pair.Key, pair => pair.Value.Admission);
            beforeStoreSelection?.Invoke(); // Internal deterministic concurrency probe; production supplies no callback.
            return store.BeginDeliveryClaim(excluded);
        }
    }

    internal bool ReleaseUnsubmittedClaim(DeliveryClaim claim, string? reason, Action? observe = null)
    {
        lock (gate)
        {
            if (!store.TryReleaseUnsubmittedClaim(claim, reason, out var released)) return false;
            if (released!.Status == MessageStatus.Accepted)
                deferred[claim.Message.Id] = (claim.AdmissionEpoch, DateTimeOffset.UtcNow.AddSeconds(5));
            else deferred.Remove(claim.Message.Id);
            beforeObservation?.Invoke();
            observe?.Invoke();
            return true;
        }
    }

    internal bool CompleteDelivery(DeliveryClaim claim, MessageStatus outcome, string? reason, Action observe)
    {
        lock (gate)
        {
            if (!store.TryCompleteDelivery(claim, outcome, reason, out _)) return false;
            beforeObservation?.Invoke();
            observe();
            return true;
        }
    }
}
