using AgentBroker.Core;

namespace AgentBroker.Worker;

/// <summary>
/// What the delivery loops last learned about a local native recipient, kept for the sender's receipt and the directory
/// (N-broker-busy-visibility-01). A Codex recipient's receipt sat Accepted / native_context_busy for about two hours while
/// list_sessions showed that row's Activity as Unknown: the receipt's UpdatedAt moves on every deferral (every few
/// seconds), and nothing said since when the recipient had been busy, so a sender could not tell busy-for-hours from stuck.
/// Observation only: nothing here changes which message is claimed, when, or how often (the store and
/// DeferredDeliveryClaims still decide that), and nothing is persisted. broker-state.json keeps the schema a 1.0.4 worker
/// reads, so a receipt field there would break a rollback; a worker restart starts these afresh. This class writes nothing
/// itself; the runtime copies CountAttempt's ordinal into its report-only events line (broker-events.jsonl), never into a receipt.
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
    // UUIDs become reusable after store retention. Match the admitted lifetime as well as its routing metadata,
    // without retaining message text, so report-only state cannot attach to a later admission of the same UUID.
    private readonly record struct MessageEpoch(SessionAddress Sender, SessionAddress Recipient, Guid ConversationId,
        Guid? ReplyTo, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int RemainingReplies)
    {
        internal static MessageEpoch From(BrokerMessage message) => new(message.Sender, message.Recipient,
            message.ConversationId, message.ReplyTo, message.CreatedAt, message.ExpiresAt, message.RemainingReplies);
    }
    private sealed record BusyObservation(MessageEpoch Epoch, Guid Admission, DateTimeOffset Since, DateTimeOffset LastObservedAt,
        long LastObservedTimestamp)
    { internal SessionAddress Recipient => Epoch.Recipient; }
    private readonly Dictionary<Guid, BusyObservation> busySince = new();
    private readonly Dictionary<SessionAddress, HashSet<Guid>> busyMessagesByRecipient = new();
    private sealed record RecipientBusyObservation(DateTimeOffset Since, DateTimeOffset LastObservedAt, bool FromDirectory);
    private readonly Dictionary<SessionAddress, RecipientBusyObservation> recipientBusyAt = new();
    // The engine binding a native adapter observed at each message's latest attempt (report only,
    // N-agentbroker-code-owner-provenance-01). Kept after the attempt, since a Submitted receipt is where it matters most;
    // past the bound the older half by observation time goes.
    private sealed record EngineObservation(MessageEpoch Epoch, Guid Admission, EngineBinding Binding);
    private readonly Dictionary<Guid, EngineObservation> engineAtAttempt = new();
    // The lifetimeOrdinal of the runtime's report-only native_submission_stage line (BrokerRuntime.NativeStage.cs, lane N round 2,
    // DECISIONS 127), per message ID: the settled attempts this process published for the message's current lifetime (its epoch,
    // which holds the exact recipient incarnation, and its admission), and when it was last counted (a sequence, not a clock, so
    // the least recently counted entry is exact however many counts share one timestamp). See CountAttempt.
    private readonly Dictionary<Guid, (MessageEpoch Epoch, Guid Admission, int Count, long Sequence)> attempts = new();
    private long attemptSequence;

    // After every local native attempt. A busy deferral starts (or continues) the message's busy run and refreshes the
    // recipient's; any other answer ends both, since it says nothing about a turn still running.
    internal void Record(BrokerMessage message, AdapterResult result, Guid admission = default)
    {
        var now = clock.GetUtcNow();
        long observedAt = clock.GetTimestamp();
        var epoch = MessageEpoch.From(message);
        lock (gate)
        {
            if (busySince.TryGetValue(message.Id, out var previousBusy) && (previousBusy.Epoch != epoch || previousBusy.Admission != admission))
                RemoveBusyMessage(message.Id);
            if (engineAtAttempt.TryGetValue(message.Id, out var previousEngine) && (previousEngine.Epoch != epoch || previousEngine.Admission != admission))
                engineAtAttempt.Remove(message.Id);
            if (result.EngineBinding is { } engine)
            {
                if (!engineAtAttempt.ContainsKey(message.Id) && engineAtAttempt.Count >= MaximumMessages)
                    foreach (var old in engineAtAttempt.OrderBy(p => p.Value.Binding.ObservedAt).Take(MaximumMessages / 2).Select(p => p.Key).ToArray())
                        engineAtAttempt.Remove(old);
                engineAtAttempt[message.Id] = new(epoch, admission, engine);
            }
            if (result.Outcome == AdapterAdmission.DeferredNotSubmitted && IsBusyReason(result.Reason))
            {
                bool continuingRecipient = recipientBusyAt.TryGetValue(message.Recipient, out var previousRecipient) &&
                    Fresh(previousRecipient.LastObservedAt, now);
                RecordRecipientBusy(message.Recipient, now, fromDirectory: false);
                if (busySince.TryGetValue(message.Id, out var existing))
                    busySince[message.Id] = new(epoch, admission,
                        (Fresh(existing.LastObservedAt, now) || continuingRecipient) && existing.Since <= now ? existing.Since : now, now,
                        Math.Max(existing.LastObservedTimestamp, observedAt));
                else
                {
                    if (busySince.Count >= MaximumMessages)
                        RemoveBusyMessage(busySince.MinBy(pair => (pair.Value.LastObservedTimestamp, pair.Key)).Key);
                    busySince[message.Id] = new(epoch, admission, now, now, observedAt);
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

    // One more settled native attempt of this message, published by the runtime's Observe (never from a rejected settlement), and
    // its 1-based ordinal. Observe runs under the claims gate, which serializes one message's attempts, so the ordinals of one
    // retained lifetime are 1, 2, 3 with no gap. The count restarts at 1 when the message's epoch or admission changes (a reused
    // UUID, another recipient incarnation, a new admission), when a worker restart starts this class afresh, and when its entry was
    // evicted: at MaximumMessages the least recently counted entry goes, so the ordinal is consecutive 1..N only while that entry
    // is retained. Report only: nothing reads it to decide anything, and receipts, requeue and the claims never see it.
    internal int CountAttempt(BrokerMessage message, Guid admission)
    {
        var epoch = MessageEpoch.From(message);
        lock (gate)
        {
            int count = attempts.TryGetValue(message.Id, out var seen) && seen.Epoch == epoch && seen.Admission == admission ? seen.Count + 1 : 1;
            if (!attempts.ContainsKey(message.Id) && attempts.Count >= MaximumMessages)
                attempts.Remove(attempts.MinBy(pair => pair.Value.Sequence).Key);
            attempts[message.Id] = (epoch, admission, count, ++attemptSequence);
            return count;
        }
    }

    // For the checks: how many messages currently hold an attempt ordinal.
    internal int AttemptEntries { get { lock (gate) return attempts.Count; } }

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
    internal DateTimeOffset? BusySince(MessageReceipt receipt, Guid? admission = null)
    {
        if (receipt.Status is not (MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted)) return null;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (IsBusyReason(receipt.Reason) && receipt.Status is not MessageStatus.Submitted &&
                busySince.TryGetValue(receipt.Message.Id, out var observation) && observation.Epoch == MessageEpoch.From(receipt.Message) &&
                (admission is null || observation.Admission == admission) &&
                Fresh(observation.LastObservedAt, now))
                return observation.Since;
            return (IsBusyReason(receipt.Reason) || receipt.Reason == "recipient_busy" || receipt.Status == MessageStatus.Submitted) &&
                recipientBusyAt.TryGetValue(receipt.Message.Recipient, out var recipient) && recipient.FromDirectory &&
                recipient.LastObservedAt <= now && now - recipient.LastObservedAt < BusyFreshFor ? recipient.Since : null;
        }
    }

    // What the adapter observed about the recipient's engine at this message's latest attempt that observed one.
    internal EngineBinding? EngineAtAttempt(BrokerMessage message, Guid? admission = null)
    {
        lock (gate) return engineAtAttempt.TryGetValue(message.Id, out var engine) && engine.Epoch == MessageEpoch.From(message) &&
            (admission is null || engine.Admission == admission)
            ? engine.Binding : null;
    }

    // A row discovery left Unknown, whose recipient answered busy within BusyFreshFor.
    internal bool SeenBusy(SessionAddress recipient)
    {
        var now = clock.GetUtcNow();
        lock (gate) return recipientBusyAt.TryGetValue(recipient, out var observation) &&
            now - observation.LastObservedAt < BusyFreshFor && observation.LastObservedAt <= now;
    }
}
