using AgentBroker.Core;
using System.Collections.Concurrent;

namespace AgentBroker.Worker;

/// <summary>Serializes claim selection with returning a busy claim to the accepted queue.</summary>
internal sealed class DeferredDeliveryClaims(BrokerStore store, Action? beforeStoreSelection = null)
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> deferred = new();

    internal MessageReceipt? BeginDelivery()
    {
        lock (gate)
        {
            foreach (var item in deferred.Where(p => p.Value <= DateTimeOffset.UtcNow).Select(p => p.Key))
                deferred.TryRemove(item, out _);
            var excluded = deferred.Keys.ToHashSet();
            beforeStoreSelection?.Invoke(); // Internal deterministic concurrency probe; production supplies no callback.
            return store.BeginDelivery(excluded);
        }
    }

    internal void ReleaseUnsubmittedClaim(Guid messageId, string? reason)
    {
        lock (gate)
        {
            deferred[messageId] = DateTimeOffset.UtcNow.AddSeconds(5);
            store.ReleaseUnsubmittedClaim(messageId, reason);
        }
    }
}
