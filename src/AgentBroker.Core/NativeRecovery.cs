using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Core;

public sealed record NativeRecoveryEntry(long RecordSequence, MessageReceipt Receipt, string Relation,
    bool PreviousIncarnation, bool ContentAvailable, bool CanAcknowledge, bool CanReply, string? BlockReason);
public sealed record NativeRecoveryPage(SessionAddress CurrentSession, IReadOnlyList<NativeRecoveryEntry> Entries,
    string NextCursor, bool HasMore, int RecoveryPendingCount);

public sealed partial class BrokerStore
{
    private sealed record NativeProof(SessionAddress Address, string Adapter, DateTimeOffset Until);
    private sealed record RecoveryCursor(int Version, Guid Epoch, SessionAddress Session, string PolicyStamp, long AfterSequence);
    private sealed record RecoveryRecord(long Sequence, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    private readonly Dictionary<string, NativeProof> nativeProofs = new(StringComparer.Ordinal);
    // Lazy inventory index only. No ordinary commit scans or renders tombstone history for recovery.
    private readonly Dictionary<Guid, RecoveryRecord> recoveryRecordSequences = new();
    private long recoveryHighWater;

    private static string? NativeAdapter(string sessionId)
    {
        string? adapter = sessionId.StartsWith("codex:", StringComparison.Ordinal) ? "codex-native" :
            sessionId.StartsWith("claude:", StringComparison.Ordinal) ? "claude-native" : null;
        int prefix = sessionId.IndexOf(':') + 1;
        return adapter is not null && Guid.TryParseExact(sessionId.AsSpan(prefix), "D", out var id) && id != Guid.Empty &&
            sessionId[prefix..] == id.ToString("D") ? adapter : null;
    }
    private static bool SameNativeLineage(SessionAddress address, SessionAddress current) =>
        address.BrokerId == current.BrokerId && address.SessionId == current.SessionId && NativeAdapter(current.SessionId) is not null;

    /// <summary>
    /// Trusted host boundary, called only after the matching native adapter's discovery pass registered this row.
    /// Not an MCP operation. Registering a row or claiming a native-looking name never establishes this proof.
    /// The discovery lease is retained independently, so a generic registration cannot extend its authority.
    /// </summary>
    public void ObserveNativeSession(SessionAddress address, string adapterId)
    {
        lock (gate)
        {
            EnsureOpen(); address.Validate();
            var row = sessions.GetValueOrDefault(address);
            if (address.BrokerId != state.BrokerId || NativeAdapter(address.SessionId) != adapterId ||
                row is null || row.Adapter != adapterId || !Live(row)) Fail("native_recovery_unverified");
            foreach (var stale in nativeProofs.Where(pair => pair.Value.Until <= Now).Select(pair => pair.Key).ToArray()) nativeProofs.Remove(stale);
            if (nativeProofs.Count >= limits.MaximumSessions && !nativeProofs.ContainsKey(address.SessionId)) Fail("session_limit");
            nativeProofs[address.SessionId] = new(address, adapterId, row!.LeaseUntil);
        }
    }

    private BrokerSession RequireNativeRecovery(SessionAddress current)
    {
        current.Validate();
        var proof = nativeProofs.GetValueOrDefault(current.SessionId);
        var row = sessions.GetValueOrDefault(current);
        if (current.BrokerId != state.BrokerId || proof is null || proof.Address != current ||
            proof.Until <= Now || NativeAdapter(current.SessionId) != proof.Adapter || row is null ||
            row.Adapter != proof.Adapter || !Live(row) || !row.Capabilities.HasFlag(SessionCapabilities.Receive)) Fail("native_recovery_unverified");
        return row!;
    }

    /// <summary>Report-only eligibility for other exact-address local tools; labels and registration alone never qualify.</summary>
    public bool IsVerifiedNativeSession(SessionAddress current)
    {
        lock (gate)
        {
            EnsureOpen();
            try { RequireNativeRecovery(current); return true; }
            catch (BrokerException) { return false; }
        }
    }

    // Only the matching adapter's fresh observation establishes replacement. A registration, label, expired proof,
    // missing row or lease lapse cannot turn a queued message into a refusal.
    private SessionAddress? NativeReplacement(SessionAddress original)
    {
        var proof = nativeProofs.GetValueOrDefault(original.SessionId);
        if (original.BrokerId != state.BrokerId || proof is null || proof.Address == original || proof.Until <= Now ||
            !SameNativeLineage(proof.Address, original) || NativeAdapter(original.SessionId) != proof.Adapter ||
            !sessions.TryGetValue(proof.Address, out var row) || row.Adapter != proof.Adapter || !Live(row)) return null;
        return proof.Address;
    }

    private string? RecoveryBlockReason(BrokerMessage message, BrokerSession current)
    {
        Guid peer = message.Sender.BrokerId == state.BrokerId ? message.Recipient.BrokerId : message.Sender.BrokerId;
        if (peer != state.BrokerId && !state.Peers.Any(saved => saved.BrokerId == peer)) return "unpaired_peer";
        if (SameNativeLineage(message.Recipient, current.Address) && message.Sender.BrokerId != state.BrokerId && current.IsPrivate)
            return "private_session";
        return null;
    }
    private void AgeRecoveryRecords()
    {
        var messages = Expire(state.Messages);
        if (!messages.SequenceEqual(state.Messages)) Commit(state with { Messages = messages });
        PruneTombstones();
    }

    public int? NativeRecoveryPendingCount(SessionAddress current)
    {
        lock (gate)
        {
            EnsureOpen();
            BrokerSession row;
            try { row = RequireNativeRecovery(current); }
            catch (BrokerException) { return null; }
            var now = Now;
            return state.Messages.Count(receipt => receipt.Message.Recipient != current && SameNativeLineage(receipt.Message.Recipient, current) &&
                NeedsRecipientReceipt(receipt, now) && RecoveryBlockReason(receipt.Message, row) is null);
        }
    }

    /// <summary>Explicit lineage inventory. Record sequence is local first-observation order, not historical arrival.</summary>
    public NativeRecoveryPage ReadNativeRecovery(SessionAddress current, string? cursor = null, int limit = 16)
    {
        lock (gate)
        {
            EnsureOpen(); var row = RequireNativeRecovery(current);
            if (limit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(limit));
            string policy = RecoveryPolicyStamp(row);
            long after = ParseRecoveryCursor(cursor, current, policy);
            AgeRecoveryRecords();
            var fullIds = state.Messages.Select(receipt => receipt.Message.Id).ToHashSet();
            var activeIds = fullIds.Concat(tombstones.Keys).ToHashSet();
            foreach (var stale in recoveryRecordSequences.Keys.Where(id => !activeIds.Contains(id)).ToArray()) recoveryRecordSequences.Remove(stale);
            foreach (var message in state.Messages.Select(receipt => receipt.Message))
                IndexRecoveryRecord(message.Id, message.CreatedAt, message.ExpiresAt);
            foreach (var historical in tombstones.Values.Where(record => !fullIds.Contains(record.Id)).OrderBy(record => record.Id))
                IndexRecoveryRecord(historical.Id, historical.CreatedAt, historical.ExpiresAt);
            var records = state.Messages.Where(receipt => SameNativeLineage(receipt.Message.Sender, current) || SameNativeLineage(receipt.Message.Recipient, current)).ToList();
            var historicalIds = tombstonesByParticipant.Where(pair => SameNativeLineage(pair.Key, current)).SelectMany(pair => pair.Value).Distinct();
            records.AddRange(historicalIds.Where(id => !fullIds.Contains(id)).Select(id => Render(tombstones[id], "")));
            var page = records.Where(receipt => recoveryRecordSequences[receipt.Message.Id].Sequence > after)
                .OrderBy(receipt => recoveryRecordSequences[receipt.Message.Id].Sequence).Take(limit + 1).ToArray();
            bool hasMore = page.Length > limit;
            var now = Now;
            var entries = page.Take(limit).Select(receipt =>
            {
                bool incoming = SameNativeLineage(receipt.Message.Recipient, current);
                string? blocked = RecoveryBlockReason(receipt.Message, row);
                return new NativeRecoveryEntry(recoveryRecordSequences[receipt.Message.Id].Sequence,
                    blocked is null ? receipt : receipt with { Message = receipt.Message with { Text = "" } },
                    incoming ? "incoming" : "outgoing", (incoming ? receipt.Message.Recipient : receipt.Message.Sender) != current,
                    blocked is null && receipt.Message.Text.Length > 0,
                    blocked is null && receipt.Message.Recipient == current &&
                        receipt.Status is not (MessageStatus.Delivered or MessageStatus.Refused or MessageStatus.Expired) &&
                        (receipt.Status != MessageStatus.Accepted || receipt.Message.ExpiresAt > now),
                    blocked is null && receipt.Message.Recipient == current && receipt.Message.RemainingReplies > 0 &&
                        receipt.Message.Sender.SessionId != NoticeSessionId && row.Capabilities.HasFlag(SessionCapabilities.Reply) &&
                        (receipt.Message.Sender.BrokerId != state.BrokerId || Addressable(receipt.Message.Sender) &&
                            sessions[receipt.Message.Sender].Capabilities.HasFlag(SessionCapabilities.Receive)), blocked);
            }).ToArray();
            long continuation = hasMore ? entries[^1].RecordSequence : recoveryHighWater;
            string next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new RecoveryCursor(1, inboxEpoch, current, policy, continuation), CursorJson));
            int pending = state.Messages.Count(receipt => receipt.Message.Recipient != current && SameNativeLineage(receipt.Message.Recipient, current) &&
                NeedsRecipientReceipt(receipt, now) && RecoveryBlockReason(receipt.Message, row) is null);
            return new(current, entries, next, hasMore, pending);
        }
    }

    private void IndexRecoveryRecord(Guid id, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        // An ID may be admitted again after its retention window without any inventory read in between. Its validated
        // new creation/deadline tuple must get a fresh sequence, even if this lazy map still holds the old ID. Never cache
        // message text here: acknowledgement must not leave a second retained copy outside the durable store.
        if (!recoveryRecordSequences.TryGetValue(id, out var previous) || previous.CreatedAt != createdAt || previous.ExpiresAt != expiresAt)
            recoveryRecordSequences[id] = new(checked(++recoveryHighWater), createdAt, expiresAt);
    }

    private string RecoveryPolicyStamp(BrokerSession row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        row.IsPrivate.ToString() + ":" + ((int)row.Capabilities).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        string.Join("|", state.Peers.OrderBy(peer => peer.BrokerId).Select(peer => peer.BrokerId.ToString("D") + peer.CertificateSha256)))));
    private long ParseRecoveryCursor(string? cursor, SessionAddress current, string policy)
    {
        if (cursor is null) return 0;
        if (cursor.Length is 0 or > 3072) Fail("invalid_native_recovery_cursor");
        RecoveryCursor? parsed;
        try { parsed = JsonSerializer.Deserialize<RecoveryCursor>(Convert.FromBase64String(cursor), CursorJson); }
        catch (Exception error) when (error is FormatException or JsonException) { Fail("invalid_native_recovery_cursor"); throw; }
        if (parsed is null || parsed.Version != 1 || parsed.Session != current || parsed.AfterSequence < 0) Fail("invalid_native_recovery_cursor");
        if (parsed!.Epoch != inboxEpoch || parsed.PolicyStamp != policy) Fail("native_recovery_cursor_reset_required");
        if (parsed.AfterSequence > recoveryHighWater) Fail("invalid_native_recovery_cursor");
        return parsed.AfterSequence;
    }
}
