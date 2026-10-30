using AgentBroker.Core;

namespace AgentBroker.Worker;

/// <summary>
/// What the delivery loops last learned about a local native recipient, kept for the sender's receipt and the directory
/// (N-broker-busy-visibility-01). A Codex recipient's receipt sat Accepted / native_context_busy for about two hours while
/// list_sessions showed that row's Activity as Unknown: the receipt's UpdatedAt moves on every deferral (every few
/// seconds), and nothing said since when the recipient had been busy, so a sender could not tell busy-for-hours from stuck.
/// Observation only: nothing here changes which message is claimed, when, or how often (the store and
/// DeferredDeliveryClaims still decide that), and nothing is persisted. broker-state.json keeps the schema a 1.0.4 worker
/// reads, so a receipt field there would break a rollback; a worker restart starts these afresh.
/// </summary>
internal sealed class DeliveryObservations(TimeProvider clock)
{
    internal const string BusyReason = "native_context_busy";
    // How long one busy answer lets the directory present an Unknown row as Busy. The answer recurs every few seconds while
    // a message waits (each deferral backs off five seconds), and a native row's own lease is 30 seconds.
    internal static readonly TimeSpan BusyFreshFor = TimeSpan.FromSeconds(30);
    // Bounds: a message entry lives while its message is attempted; an entry whose message stopped being attempted (it was
    // acknowledged, expired or refused without another attempt) is pruned once the map passes this size.
    internal const int MaximumMessages = 4096;
    internal const int MaximumRecipients = 256;
    private readonly object gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> busySince = new();
    private readonly Dictionary<SessionAddress, DateTimeOffset> recipientBusyAt = new();

    // After every local native attempt. A busy deferral starts (or continues) the message's busy run and refreshes the
    // recipient's; any other answer ends both, since it says nothing about a turn still running.
    internal void Record(BrokerMessage message, AdapterResult result, Func<Guid, bool> stillAttempted)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (result.Outcome == AdapterAdmission.DeferredNotSubmitted && result.Reason == BusyReason)
            {
                if (!busySince.ContainsKey(message.Id))
                {
                    if (busySince.Count >= MaximumMessages)
                        foreach (var stale in busySince.Keys.Where(id => !stillAttempted(id)).ToArray()) busySince.Remove(stale);
                    if (busySince.Count < MaximumMessages) busySince[message.Id] = now;
                }
                if (!recipientBusyAt.ContainsKey(message.Recipient) && recipientBusyAt.Count >= MaximumRecipients)
                    foreach (var stale in recipientBusyAt.Where(p => now - p.Value >= BusyFreshFor).Select(p => p.Key).ToArray())
                        recipientBusyAt.Remove(stale);
                if (recipientBusyAt.ContainsKey(message.Recipient) || recipientBusyAt.Count < MaximumRecipients)
                    recipientBusyAt[message.Recipient] = now;
            }
            else
            {
                busySince.Remove(message.Id);
                recipientBusyAt.Remove(message.Recipient);
            }
        }
    }

    // Only while the receipt itself still says busy: an Accepted (or in-flight) message whose last attempt was deferred busy.
    internal DateTimeOffset? BusySince(MessageReceipt receipt)
    {
        if (receipt.Reason != BusyReason || receipt.Status is not (MessageStatus.Accepted or MessageStatus.Submitting)) return null;
        lock (gate) return busySince.TryGetValue(receipt.Message.Id, out var since) ? since : null;
    }

    // A row discovery left Unknown, whose recipient answered busy within BusyFreshFor.
    internal bool SeenBusy(SessionAddress recipient)
    {
        var now = clock.GetUtcNow();
        lock (gate) return recipientBusyAt.TryGetValue(recipient, out var at) && now - at < BusyFreshFor && at <= now;
    }
}
