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
    internal static bool IsBusyReason(string? reason) => reason is BusyReason or "native_recipient_busy";
    // How long one busy answer lets the directory present an Unknown row as Busy. The answer recurs every few seconds while
    // a message waits (each deferral backs off five seconds), and a native row's own lease is 30 seconds.
    internal static readonly TimeSpan BusyFreshFor = TimeSpan.FromSeconds(30);
    // At the bound evict the least recently observed entry, without reentering the store under this lock.
    // Recency is monotonic; a wall-clock correction must not make old observations outlive refreshed ones.
    internal const int MaximumMessages = 4096;
    internal const int MaximumRecipients = 256;
    private readonly object gate = new();
    private sealed record BusyObservation(SessionAddress Recipient, DateTimeOffset Since, DateTimeOffset LastObservedAt,
        long LastObservedTimestamp);
    private readonly Dictionary<Guid, BusyObservation> busySince = new();
    private readonly Dictionary<SessionAddress, HashSet<Guid>> busyMessagesByRecipient = new();
    private sealed record RecipientBusyObservation(DateTimeOffset Since, DateTimeOffset LastObservedAt, bool FromDirectory);
    private readonly Dictionary<SessionAddress, RecipientBusyObservation> recipientBusyAt = new();
    // The engine binding a native adapter observed at each message's latest attempt (report only,
    // N-agentbroker-code-owner-provenance-01). Kept after the attempt, since a Submitted receipt is where it matters most;
    // past the bound the older half by observation time goes.
    private readonly Dictionary<Guid, EngineBinding> engineAtAttempt = new();

    // After every local native attempt. A busy deferral starts (or continues) the message's busy run and refreshes the
    // recipient's; any other answer ends both, since it says nothing about a turn still running.
    internal void Record(BrokerMessage message, AdapterResult result)
    {
        var now = clock.GetUtcNow();
        long observedAt = clock.GetTimestamp();
        lock (gate)
        {
            if (result.EngineBinding is { } engine)
            {
                if (!engineAtAttempt.ContainsKey(message.Id) && engineAtAttempt.Count >= MaximumMessages)
                    foreach (var old in engineAtAttempt.OrderBy(p => p.Value.ObservedAt).Take(MaximumMessages / 2).Select(p => p.Key).ToArray())
                        engineAtAttempt.Remove(old);
                engineAtAttempt[message.Id] = engine;
            }
            if (result.Outcome == AdapterAdmission.DeferredNotSubmitted && IsBusyReason(result.Reason))
            {
                bool continuingRecipient = recipientBusyAt.TryGetValue(message.Recipient, out var previousRecipient) &&
                    Fresh(previousRecipient.LastObservedAt, now);
                RecordRecipientBusy(message.Recipient, now, fromDirectory: false);
                if (busySince.TryGetValue(message.Id, out var existing))
                    busySince[message.Id] = new(message.Recipient,
                        (Fresh(existing.LastObservedAt, now) || continuingRecipient) && existing.Since <= now ? existing.Since : now, now,
                        Math.Max(existing.LastObservedTimestamp, observedAt));
                else
                {
                    if (busySince.Count >= MaximumMessages)
                        RemoveBusyMessage(busySince.MinBy(pair => (pair.Value.LastObservedTimestamp, pair.Key)).Key);
                    busySince[message.Id] = new(message.Recipient, now, now, observedAt);
                    if (!busyMessagesByRecipient.TryGetValue(message.Recipient, out var messages))
                        busyMessagesByRecipient[message.Recipient] = messages = [];
                    messages.Add(message.Id);
                }
            }
            else
            {
                EndBusyEpisode(message.Recipient);
            }
        }
    }

    // Discovery can already know Busy before any submission is attempted. Only fresh native discovery contributes;
    // Unknown does not erase a recent authoritative busy answer, and approval/interruption are never labeled Busy.
    internal void ObserveDirectory(BrokerSession session)
    {
        if (session.Adapter is not ("codex-native" or "claude-native")) return;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (session.LeaseUntil <= now) { EndBusyEpisode(session.Address); return; }
            if (session.Activity == SessionActivity.Busy) RecordRecipientBusy(session.Address, now, fromDirectory: true);
            else if (session.Activity is SessionActivity.Idle or SessionActivity.ApprovalRequired or SessionActivity.Interrupted)
                EndBusyEpisode(session.Address);
        }
    }

    // Only a successfully completed native directory pass may end episodes for absent rows.
    // A failed pass supplies no census and lets the last observation expire normally.
    internal void ObserveDirectoryCensus(string adapter, IReadOnlyList<BrokerSession> sessions)
    {
        if (adapter is not ("codex-native" or "claude-native")) return;
        string prefix = adapter == "codex-native" ? "codex:" : "claude:";
        var present = sessions.Where(session => session.Adapter == adapter).Select(session => session.Address).ToHashSet();
        lock (gate)
        {
            var missing = recipientBusyAt.Keys.Concat(busyMessagesByRecipient.Keys)
                .Where(address => address.SessionId.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(address))
                .Distinct().ToArray();
            foreach (var address in missing) EndBusyEpisode(address);
        }
    }

    // Caller holds gate. A recipient's current state ends every message's previous busy episode.
    private void EndBusyEpisode(SessionAddress recipient)
    {
        recipientBusyAt.Remove(recipient);
        if (busyMessagesByRecipient.Remove(recipient, out var messages))
            foreach (var id in messages) busySince.Remove(id);
    }

    // Keep the recipient index bounded by the same live message entries as the capacity map.
    private void RemoveBusyMessage(Guid id)
    {
        if (!busySince.Remove(id, out var observation)) return;
        var messages = busyMessagesByRecipient[observation.Recipient];
        messages.Remove(id);
        if (messages.Count == 0) busyMessagesByRecipient.Remove(observation.Recipient);
    }

    private static bool Fresh(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt <= now && now - observedAt < BusyFreshFor;

    // Caller holds gate. At capacity expired observations leave first; a still-full map remains bounded.
    private void RecordRecipientBusy(SessionAddress recipient, DateTimeOffset now, bool fromDirectory)
    {
        bool hasRecipient = recipientBusyAt.TryGetValue(recipient, out var expired);
        if ((hasRecipient && !Fresh(expired!.LastObservedAt, now)) ||
            (!hasRecipient && busyMessagesByRecipient.TryGetValue(recipient, out var tracked) &&
                !tracked.Any(id => Fresh(busySince[id].LastObservedAt, now))))
            EndBusyEpisode(recipient);
        if (!recipientBusyAt.ContainsKey(recipient) && recipientBusyAt.Count >= MaximumRecipients)
            foreach (var stale in recipientBusyAt.Where(pair => now - pair.Value.LastObservedAt >= BusyFreshFor ||
                pair.Value.LastObservedAt > now).Select(pair => pair.Key).ToArray()) recipientBusyAt.Remove(stale);
        if (recipientBusyAt.TryGetValue(recipient, out var previous))
            recipientBusyAt[recipient] = new(previous.Since, now, fromDirectory || previous.FromDirectory);
        else if (recipientBusyAt.Count < MaximumRecipients) recipientBusyAt[recipient] = new(now, now, fromDirectory);
    }

    // A deferred busy message carries its own busy run. A queued or submitted message can instead report the current
    // directory's first observed Busy time; this says nothing about whether the requested work is progressing.
    internal DateTimeOffset? BusySince(MessageReceipt receipt)
    {
        if (receipt.Status is not (MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted)) return null;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (IsBusyReason(receipt.Reason) && receipt.Status is not MessageStatus.Submitted &&
                busySince.TryGetValue(receipt.Message.Id, out var observation) && Fresh(observation.LastObservedAt, now))
                return observation.Since;
            return (IsBusyReason(receipt.Reason) || receipt.Reason == "recipient_busy" || receipt.Status == MessageStatus.Submitted) &&
                recipientBusyAt.TryGetValue(receipt.Message.Recipient, out var recipient) && recipient.FromDirectory &&
                recipient.LastObservedAt <= now && now - recipient.LastObservedAt < BusyFreshFor ? recipient.Since : null;
        }
    }

    // What the adapter observed about the recipient's engine at this message's latest attempt that observed one.
    internal EngineBinding? EngineAtAttempt(Guid messageId)
    {
        lock (gate) return engineAtAttempt.TryGetValue(messageId, out var engine) ? engine : null;
    }

    // A row discovery left Unknown, whose recipient answered busy within BusyFreshFor.
    internal bool SeenBusy(SessionAddress recipient)
    {
        var now = clock.GetUtcNow();
        lock (gate) return recipientBusyAt.TryGetValue(recipient, out var observation) &&
            now - observation.LastObservedAt < BusyFreshFor && observation.LastObservedAt <= now;
    }
}
