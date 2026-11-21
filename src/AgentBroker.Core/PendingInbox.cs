using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBroker.Core;

// Local query results only. Neither these fields nor cursors enter the schema-1 state or federation records.
public sealed record PendingInboxEntry(long ArrivalSequence, MessageReceipt Receipt);
public sealed record PendingInboxPage(IReadOnlyList<PendingInboxEntry> Entries, string NextCursor,
    bool HasMore, int PendingCount);

public sealed partial class BrokerStore
{
    private sealed record InboxCursor(int Version, Guid Epoch, SessionAddress Session, long AfterSequence);
    private static readonly JsonSerializerOptions CursorJson = new()
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private readonly Guid inboxEpoch = Guid.NewGuid();
    private Dictionary<Guid, long> inboxSequences = new();
    private long inboxHighWater;

    // The map is bounded by full records, not tombstone history. Removing a record never reuses its ordinal in this epoch.
    private (Dictionary<Guid, long> Sequences, long HighWater) PrepareInboxSequences(List<MessageReceipt> messages)
    {
        var next = new Dictionary<Guid, long>(messages.Count);
        long highWater = inboxHighWater;
        foreach (var receipt in messages)
            next.Add(receipt.Message.Id, inboxSequences.TryGetValue(receipt.Message.Id, out long sequence)
                ? sequence : checked(++highWater));
        return (next, highWater);
    }

    private static bool NeedsRecipientReceipt(MessageReceipt receipt, DateTimeOffset now) => receipt.Message.ExpiresAt > now &&
        receipt.Status is MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted or MessageStatus.Uncertain;

    /// <summary>Local directory enrichment in one bounded pass, without rendering tombstones or scanning per row.</summary>
    public IReadOnlyDictionary<SessionAddress, int> PendingIncomingCounts()
    {
        lock (gate)
        {
            EnsureOpen();
            var counts = new Dictionary<SessionAddress, int>();
            var now = Now;
            foreach (var receipt in state.Messages)
                if (receipt.Message.Recipient.BrokerId == state.BrokerId && NeedsRecipientReceipt(receipt, now))
                    counts[receipt.Message.Recipient] = counts.GetValueOrDefault(receipt.Message.Recipient) + 1;
            return counts;
        }
    }

    /// <summary>Current messages needing the exact recipient's acknowledgement, without rendering acknowledged history.</summary>
    public int PendingIncomingCount(SessionAddress recipient)
    {
        lock (gate)
        {
            EnsureOpen();
            recipient.Validate();
            if (!Addressable(recipient)) FailAvailability("session_unavailable", recipient, "caller_recipient");
            var now = Now;
            return state.Messages.Count(receipt => receipt.Message.Recipient == recipient && NeedsRecipientReceipt(receipt, now));
        }
    }

    /// <summary>
    /// Nonblocking pending inbox, in broker acceptance order. A cursor survives polls and record removal within this
    /// store opening. A restart explicitly invalidates it; rescan without a cursor and deduplicate by message ID.
    /// Reading never acknowledges, cancels, retries or changes native admission.
    /// </summary>
    public PendingInboxPage ReadPendingInbox(SessionAddress recipient, string? cursor = null, int limit = 16)
    {
        lock (gate)
        {
            EnsureOpen();
            recipient.Validate();
            if (limit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(limit));
            if (!Addressable(recipient)) FailAvailability("session_unavailable", recipient, "caller_recipient");
            long after = ParseInboxCursor(cursor, recipient);
            var messages = Expire(state.Messages);
            if (!messages.SequenceEqual(state.Messages)) Commit(state with { Messages = messages });
            var now = Now;
            var pending = state.Messages.Where(receipt => receipt.Message.Recipient == recipient && NeedsRecipientReceipt(receipt, now)).ToArray();
            var page = pending.Where(receipt => inboxSequences[receipt.Message.Id] > after)
                .OrderBy(receipt => inboxSequences[receipt.Message.Id]).Take(limit + 1).ToArray();
            bool hasMore = page.Length > limit;
            var entries = page.Take(limit).Select(receipt => new PendingInboxEntry(inboxSequences[receipt.Message.Id], receipt)).ToArray();
            // A complete scan can advance past irrelevant and acknowledged records; none can become pending again.
            long continuation = hasMore ? entries[^1].ArrivalSequence : inboxHighWater;
            return new(entries, EncodeInboxCursor(recipient, continuation), hasMore, pending.Length);
        }
    }

    private string EncodeInboxCursor(SessionAddress recipient, long after) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new InboxCursor(1, inboxEpoch, recipient, after), CursorJson));

    private long ParseInboxCursor(string? cursor, SessionAddress recipient)
    {
        if (cursor is null) return 0;
        if (cursor.Length is 0 or > 2048) Fail("invalid_inbox_cursor");
        InboxCursor? parsed;
        try { parsed = JsonSerializer.Deserialize<InboxCursor>(Convert.FromBase64String(cursor), CursorJson); }
        catch (Exception error) when (error is FormatException or JsonException) { Fail("invalid_inbox_cursor"); throw; }
        if (parsed is null || parsed.Version != 1 || parsed.Epoch == Guid.Empty || parsed.Session != recipient || parsed.AfterSequence < 0)
            Fail("invalid_inbox_cursor");
        if (parsed!.Epoch != inboxEpoch) Fail("inbox_cursor_reset_required");
        if (parsed.AfterSequence > inboxHighWater) Fail("invalid_inbox_cursor");
        return parsed.AfterSequence;
    }
}
