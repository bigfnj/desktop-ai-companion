using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Core;

public static class WaitingMailPolicy
{
    public static readonly TimeSpan NoticeAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ObservationFreshFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DirectoryObservationInterval = TimeSpan.FromSeconds(2);
    public const int MaximumNoticesPerPass = 16;
}

public sealed record WaitReasonObservation(string Reason, string Source,
    DateTimeOffset FirstObservedAtUtc, DateTimeOffset LastObservedAtUtc, long Count);
public sealed record MessageWaitView(Guid MessageId, WaitReasonObservation[] Waits, string LatestReason, string LatestSource,
    DateTimeOffset? FirstBusyObservedAtUtc, long? BusyObservedAgeMilliseconds);
public sealed record SessionWaitingMail(SessionAddress Session, int PendingCount, int QueuedCount, int BlockedCount,
    int BusyCount, int UnobservedBlockedCount, DateTimeOffset? OldestBusyObservedAtUtc, long? OldestBusyObservedAgeMilliseconds);
public sealed record WaitingMailSnapshot(Guid BrokerId, DateTimeOffset ObservedAtUtc, int NoticeAfterSeconds, SessionWaitingMail[] Sessions);
public sealed record WaitingNoticePass(int Admitted, int AlreadyAdmitted, int Blocked, int Cancelled);

public sealed partial class BrokerStore
{
    private readonly record struct WaitIdentity(Guid Id, Guid ConversationId, Guid? ReplyTo,
        SessionAddress Sender, SessionAddress Recipient, DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt, int RemainingReplies)
    {
        internal static WaitIdentity From(BrokerMessage message) => new(message.Id,
            message.ConversationId, message.ReplyTo, message.Sender, message.Recipient,
            message.CreatedAt, message.ExpiresAt, message.RemainingReplies);
    }

    // Process-local, bounded by current full pending records, never persisted in schema-1 receipts or tombstones.
    private sealed class WaitingObservation(MessageReceipt receipt)
    {
        internal readonly Guid Admission = receipt.AdmissionEpoch;
        internal readonly WaitIdentity Source = WaitIdentity.From(receipt.Message);
        internal readonly Dictionary<(string Reason, string Source), WaitReasonObservation> Reasons = new();
        internal string LatestReason = "", LatestSource = "";
        internal long LastTimestamp;
        internal long? BusyTimestamp, AdapterBusyTimestamp;
        internal DateTimeOffset? BusyAt;
        internal bool NoticeAdmitted;
    }
    private readonly Dictionary<Guid, WaitingObservation> waitingMail = new();
    private static bool Native(BrokerSession session) => session.Adapter is "claude-native" or "codex-native";
    private bool WaitingPolicy(BrokerMessage message, BrokerSession target) => Live(target) &&
        target.Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle) &&
        (message.Sender.BrokerId == state.BrokerId || !target.IsPrivate);
    private static bool BusyReason(string? reason) => reason is "native_context_busy" or "native_recipient_busy";
    private static string AdapterWaitReason(string? reason) => reason switch
    {
        "native_context_busy" or "native_recipient_busy" or "native_recipient_awaiting_approval" or
        "native_recipient_activity_unknown" or "native_metadata_unavailable" or "native_submission_wait_cancelled" or
        "native_delivery_wait_cancelled" or "native_session_unavailable" or "native_owner_changed" or
        "native_owner_unavailable" or "native_verification_unavailable" or "native_receipt_capacity_reconnect" or
        "native_not_submitted" or "native_transport_unavailable" => reason,
        _ => "other_deferred"
    };
    private bool FreshWait(long timestamp) => clock.GetElapsedTime(timestamp, clock.GetTimestamp()) is var elapsed &&
        elapsed >= TimeSpan.Zero && elapsed < WaitingMailPolicy.ObservationFreshFor;
    private static void EndBusy(WaitingObservation observation)
    { observation.BusyTimestamp = null; observation.BusyAt = null; observation.AdapterBusyTimestamp = null; }
    private bool CurrentWait(MessageReceipt receipt, WaitingObservation observation) =>
        receipt.AdmissionEpoch == observation.Admission && WaitIdentity.From(receipt.Message) == observation.Source;
    private bool PendingNative(MessageReceipt receipt) => receipt.Message.Recipient.BrokerId == state.BrokerId &&
        NeedsRecipientReceipt(receipt, Now) && receipt.Message.Sender.SessionId != NoticeSessionId &&
        (!sessions.TryGetValue(receipt.Message.Recipient, out var target) || Native(target)) &&
        NativeReplacement(receipt.Message.Recipient) is null;

    // Caller holds gate. No observations are created by a read, and resolved history cannot fill this map.
    private void PruneWaitingMail()
    {
        if (waitingMail.Count == 0) return;
        var current = state.Messages.Where(PendingNative).ToDictionary(receipt => receipt.Message.Id);
        foreach (var (id, observation) in waitingMail.ToArray())
        {
            if (!current.TryGetValue(id, out var receipt) || !CurrentWait(receipt, observation)) waitingMail.Remove(id);
            else if (receipt.Status is not (MessageStatus.Accepted or MessageStatus.Submitting) ||
                !FreshWait(observation.LastTimestamp)) EndBusy(observation);
        }
    }

    private void ObserveWait(MessageReceipt receipt, string reason, string source, bool busy)
    {
        if (!PendingNative(receipt) || receipt.Status != MessageStatus.Accepted ||
            !sessions.TryGetValue(receipt.Message.Recipient, out var target) || !Native(target)) return;
        if (waitingMail.TryGetValue(receipt.Message.Id, out var observation) && !CurrentWait(receipt, observation))
        { waitingMail.Remove(receipt.Message.Id); observation = null; }
        if (observation is null)
        {
            if (waitingMail.Count >= limits.MaximumMessages) return;
            waitingMail.Add(receipt.Message.Id, observation = new(receipt));
        }
        var timestamp = clock.GetTimestamp();
        if (!FreshWait(observation.LastTimestamp)) EndBusy(observation);
        var now = Now;
        bool throttledDirectory = source == "directory" && observation.LatestSource == source &&
            observation.LatestReason == reason && clock.GetElapsedTime(observation.LastTimestamp, timestamp) is var elapsed &&
            elapsed >= TimeSpan.Zero && elapsed < WaitingMailPolicy.DirectoryObservationInterval;
        if (!throttledDirectory)
        {
            var key = (reason, source);
            if (observation.Reasons.TryGetValue(key, out var previous))
                observation.Reasons[key] = previous with { LastObservedAtUtc = now,
                    Count = previous.Count == long.MaxValue ? long.MaxValue : previous.Count + 1 };
            else if (observation.Reasons.Count < 20) observation.Reasons.Add(key, new(reason, source, now, now, 1));
            observation.LatestReason = reason; observation.LatestSource = source; observation.LastTimestamp = timestamp;
        }
        if (!busy || !WaitingPolicy(receipt.Message, target)) EndBusy(observation);
        else
        {
            observation.BusyTimestamp ??= timestamp; observation.BusyAt ??= now;
            if (source == "adapter") observation.AdapterBusyTimestamp = timestamp;
        }
    }

    // Called only after a matched release committed; rejected/stale claims publish nothing.
    private void ObserveAdapterWait(MessageReceipt released, string reason)
    {
        PruneWaitingMail();
        ObserveWait(released, AdapterWaitReason(reason), "adapter", BusyReason(reason));
    }

    private void ObserveDirectoryWaits()
    {
        PruneWaitingMail();
        foreach (var receipt in state.Messages.Where(receipt => receipt.Status == MessageStatus.Accepted && PendingNative(receipt)))
        {
            if (!sessions.TryGetValue(receipt.Message.Recipient, out var target) || !Native(target)) continue;
            if (!WaitingPolicy(receipt.Message, target) && waitingMail.TryGetValue(receipt.Message.Id, out var invalidPolicy))
                EndBusy(invalidPolicy);
            if (target.Activity == SessionActivity.Unknown && waitingMail.TryGetValue(receipt.Message.Id, out var unknown) &&
                (unknown.AdapterBusyTimestamp is not { } adapterAt || !FreshWait(adapterAt))) EndBusy(unknown);
            string? reason = !Live(target) ? "recipient_unavailable" : target.Activity switch
            {
                SessionActivity.Busy => "recipient_busy",
                SessionActivity.ApprovalRequired => "recipient_awaiting_approval",
                SessionActivity.Interrupted => "recipient_interrupted",
                SessionActivity.Unknown when !target.Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard) => "recipient_activity_unknown",
                _ => null
            };
            if (reason is not null) ObserveWait(receipt, reason, "directory", reason == "recipient_busy");
            else if (target.Activity == SessionActivity.Idle && waitingMail.TryGetValue(receipt.Message.Id, out var observation))
                EndBusy(observation);
        }
    }

    // Current activity and policy are checked again under the same gate as notice admission and selection.
    private bool CurrentlyBusy(MessageReceipt receipt, WaitingObservation observation) =>
        receipt.Status == MessageStatus.Accepted && PendingNative(receipt) && CurrentWait(receipt, observation) &&
        observation.BusyTimestamp is not null && FreshWait(observation.LastTimestamp) &&
        sessions.TryGetValue(receipt.Message.Recipient, out var target) && Native(target) && Live(target) &&
        WaitingPolicy(receipt.Message, target) &&
        (target.Activity == SessionActivity.Busy || target.Activity == SessionActivity.Unknown &&
            target.Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard) && observation.AdapterBusyTimestamp is { } adapterAt && FreshWait(adapterAt));

    private MessageWaitView WaitView(MessageReceipt receipt, WaitingObservation observation)
    {
        bool busy = CurrentlyBusy(receipt, observation);
        return new(receipt.Message.Id, observation.Reasons.Values.ToArray(), observation.LatestReason, observation.LatestSource,
            busy ? observation.BusyAt : null, busy ? Math.Max(0, (long)clock.GetElapsedTime(observation.BusyTimestamp!.Value).TotalMilliseconds) : null);
    }

    public MessageWaitView? ReadMessageWait(MessageReceipt expected)
    {
        lock (gate)
        {
            EnsureOpen(); PruneWaitingMail();
            var current = state.Messages.Find(receipt => receipt.Message.Id == expected.Message.Id);
            return current is not null && current.AdmissionEpoch == expected.AdmissionEpoch &&
                current.Message == expected.Message && waitingMail.TryGetValue(current.Message.Id, out var observation) && CurrentWait(current, observation)
                ? WaitView(current, observation) : null;
        }
    }

    private struct WaitingMailCounts
    {
        internal int Pending, Queued, Blocked, Busy, Unobserved;
        internal bool IsNative;
        internal bool? Ready;
        internal DateTimeOffset? Oldest;
        internal long? Age;
    }

    public WaitingMailSnapshot ReadWaitingMail()
    {
        lock (gate)
        {
            EnsureOpen(); PruneWaitingMail();
            var now = Now;
            var targets = sessions.Values.ToArray();
            var indices = new Dictionary<SessionAddress, int>(targets.Length);
            var counts = new WaitingMailCounts[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                indices.Add(targets[i].Address, i);
                counts[i].IsNative = Native(targets[i]);
            }
            foreach (var receipt in state.Messages)
            {
                if (!NeedsRecipientReceipt(receipt, now) || receipt.Message.Recipient.BrokerId != state.BrokerId ||
                    !indices.TryGetValue(receipt.Message.Recipient, out int index)) continue;
                ref var count = ref counts[index];
                count.Pending++;
                if (receipt.Status != MessageStatus.Accepted) continue;
                count.Queued++;
                bool observed = waitingMail.TryGetValue(receipt.Message.Id, out var observation) && CurrentWait(receipt, observation) && FreshWait(observation.LastTimestamp);
                bool isBusy = observed && CurrentlyBusy(receipt, observation!);
                if (count.IsNative)
                {
                    count.Ready ??= Ready(targets[index].Address);
                    if (!count.Ready.Value || isBusy) { count.Blocked++; if (!observed) count.Unobserved++; }
                }
                if (!isBusy) continue;
                count.Busy++;
                long elapsed = Math.Max(0, (long)clock.GetElapsedTime(observation!.BusyTimestamp!.Value).TotalMilliseconds);
                if (count.Age is null || elapsed > count.Age) { count.Age = elapsed; count.Oldest = observation.BusyAt; }
            }
            var rows = new SessionWaitingMail[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                ref var count = ref counts[i];
                rows[i] = new(targets[i].Address, count.Pending, count.Queued, count.Blocked, count.Busy,
                    count.Unobserved, count.Oldest, count.Age);
            }
            return new(state.BrokerId, now, (int)WaitingMailPolicy.NoticeAfter.TotalSeconds, rows);
        }
    }

    public WaitingNoticePass AdmitDueWaitingMailNotices()
    {
        lock (gate)
        {
            EnsureOpen(); ObserveDirectoryWaits();
            int admitted = 0, already = 0, blocked = 0, cancelled = 0;
            PruneTombstones();
            var messages = state.Messages.ToList();
            var ids = state.Messages.Select(item => item.Message.Id).ToHashSet();
            int withText = state.Messages.Count(item => !Tombstoned(item)), records = state.Messages.Count + tombstones.Count;
            var pending = PendingByRecipient(state.Messages);
            var toLatch = new List<WaitingObservation>();
            foreach (var receipt in state.Messages.ToArray())
            {
                if (!waitingMail.TryGetValue(receipt.Message.Id, out var observation)) continue;
                if (!CurrentlyBusy(receipt, observation)) { cancelled++; continue; }
                if (clock.GetElapsedTime(observation.BusyTimestamp!.Value) < WaitingMailPolicy.NoticeAfter) continue;
                if (receipt.Message.Sender.BrokerId != state.BrokerId) continue;
                var id = WaitingNoticeId(receipt.Message);
                if (observation.NoticeAdmitted || ids.Contains(id) || tombstones.ContainsKey(id))
                { observation.NoticeAdmitted = true; already++; continue; }
                if (Refusal(receipt.Message.Sender, withText, records, pending) is not null) { blocked++; continue; }
                var now = Now;
                string text = $"AgentBroker waiting advisory: your message {receipt.Message.Id} was still queued at {now:u}. " +
                    $"Its recipient was observed Busy for at least {(long)clock.GetElapsedTime(observation.BusyTimestamp.Value).TotalSeconds} seconds " +
                    $"during this worker lifetime (latest reason: {observation.LatestReason}). The observation clock resets on worker restart. " +
                    "Check get_message_receipt before deciding what to do. Do not resend automatically. This is not delivery or work completion.";
                var notice = new BrokerMessage(id, id, null, new(state.BrokerId, NoticeSessionId, NoticeIncarnation), receipt.Message.Sender,
                    FitText(text), now, now + BrokerLimits.MaximumLifetime, 0);
                messages.Add(new(notice, MessageStatus.Accepted, now)); ids.Add(id);
                withText++; records++; pending[notice.Recipient] = pending.GetValueOrDefault(notice.Recipient) + 1;
                toLatch.Add(observation);
                if (toLatch.Count == WaitingMailPolicy.MaximumNoticesPerPass) break;
            }
            if (toLatch.Count > 0)
            {
                Commit(state with { Messages = messages });
                foreach (var observation in toLatch) observation.NoticeAdmitted = true;
                admitted = toLatch.Count;
            }
            return new(admitted, already, blocked, cancelled);
        }
    }

    private static Guid WaitingNoticeId(BrokerMessage message)
    {
        string identity = JsonSerializer.Serialize(new { message.Id, message.ConversationId, message.ReplyTo, message.Sender, message.Recipient,
            CreatedAt = message.CreatedAt.UtcTicks, ExpiresAt = message.ExpiresAt.UtcTicks, message.RemainingReplies });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("agentbroker-waiting-notice-v1:" + identity))[..16];
        NoticeMarker.CopyTo(bytes, 0);
        bytes[7] = (byte)(bytes[7] & 0x0F | 0x80); bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes);
    }
}
