using AgentBroker.Core;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentBroker.Worker;

/// <summary>
/// What the sender's tools return (send, send_message, reply_to_message, wait_for_receipt, get_message_receipt) instead of
/// the stored MessageReceipt (N-broker-compact-receipts-01). Every poll of a receipt used to hand the message text, up to
/// 16 KiB, back to the agent that wrote it. The shape is the receipt's own, nested the same way, so a client that reads
/// message.id, message.sender, message.recipient or status reads them unchanged; only message.text is left out, unless
/// get_message_receipt is asked for it (includeText). Readers keep the full MessageReceipt: wait_for_messages, read_inbox
/// and wait_for_reply. Refused: a flat new shape, which would have broken the channel bridge and the collaboration helper,
/// both of which read message.recipient or message.sender from get_message_receipt.
/// The extra members describe the receipt and are never stored. TextBytes is the text's length in UTF-8 bytes (the unit of
/// the 16 KiB limit), null once the broker no longer holds it (an acknowledged Delivered or Refused message this
/// workstation sent: its tombstone keeps only a hash). ReadAt is this broker's clock when the receipt was read, so the
/// receipt tells its own age (N-broker-busy-visibility-01): ReadAt minus UpdatedAt is how long a Submitted or Held receipt
/// has held its status, or how long ago an Accepted one was last attempted. BusySince is present only while the
/// message is Accepted (or being attempted) because its native recipient said a turn is still running
/// (native_context_busy): the first such answer in the current unbroken run, as this worker process saw it.
/// </summary>
public sealed record SenderReceipt(SenderReceiptMessage Message, MessageStatus Status, DateTimeOffset UpdatedAt,
    string? Reason, int Attempts, int? TextBytes, DateTimeOffset ReadAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? BusySince = null)
{
    internal static SenderReceipt From(MessageReceipt receipt, DateTimeOffset readAt, bool includeText = false,
        DateTimeOffset? busySince = null)
    {
        var message = receipt.Message;
        // An empty text is always a tombstone: real message text is never empty or whitespace (BrokerStore.FindReceipt).
        bool held = !string.IsNullOrEmpty(message.Text);
        return new(new(message.Id, message.ConversationId, message.ReplyTo, message.Sender, message.Recipient,
                message.CreatedAt, message.ExpiresAt, message.RemainingReplies, includeText ? message.Text : null),
            receipt.Status, receipt.UpdatedAt, receipt.Reason, receipt.Attempts,
            held ? Encoding.UTF8.GetByteCount(message.Text) : null, readAt, busySince);
    }
}

/// <summary>The receipt's message without its text (see <see cref="SenderReceipt"/>); Text only when asked for.</summary>
public sealed record SenderReceiptMessage(Guid Id, Guid ConversationId, Guid? ReplyTo, SessionAddress Sender, SessionAddress Recipient,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int RemainingReplies,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);
