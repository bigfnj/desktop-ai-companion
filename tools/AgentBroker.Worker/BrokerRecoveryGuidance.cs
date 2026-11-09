using AgentBroker.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBroker.Worker;

public sealed record RecoveryGuidance(string Code, string NextTool, string Advice, bool DeliveryMayHaveOccurred = false,
    AvailabilityDiagnostic? Availability = null, string? Operation = null);
internal sealed record BrokerToolFailure(string Code, AvailabilityDiagnostic? Availability);

/// <summary>Fixed protocol advice, never a retry, a new authorization, or an inference from peer text.</summary>
internal static class BrokerRecoveryGuidance
{
    internal const string AvailabilityKey = "agentbroker.availability-diagnostic";
    internal const string FailureKey = "agentbroker.tool-failure";
    internal const string ReceiptKey = "agentbroker.receipt-guidance";
    internal const string OperationKey = "agentbroker.tool-operation";
    internal static RecoveryGuidance? ForError(string code) => code switch
    {
        "session_unavailable" or "sender_unavailable" => new(code, "list_sessions",
            "Refresh your exact native address; a generic mailbox instead re-registers its same clientId and incarnation. Never register or impersonate an old native address."),
        "recipient_unavailable" => new(code, "list_sessions",
            "Refresh the exact recipient. If absent, wait for its return. Inspect the original message receipt before deciding whether a separately authorized fresh send is needed; do not blindly resend."),
        "recipient_mismatch" => new(code, "get_message_receipt",
            "Inspect the original exact recipient. Native recovery is report-only: a successor cannot acknowledge or reply as its predecessor. If authorized, continue with an explicit send between current complete addresses."),
        "receipt_final" => new(code, "get_message_receipt",
            "Inspect the final receipt. Acknowledgement is receipt of content, not completion of the requested work."),
        "wait_in_progress" or "wait_capacity" => new(code, "read_inbox_page",
            "Finish or cancel the existing wait, or fetch pending incoming messages without taking a wait slot. wait_for_messages and wait_for_reply share one wait per session; wait_for_receipt has its own and may run beside them. Do not start a second wait of the same kind."),
        "broker_stopping" => new(code, "list_sessions",
            "The broker is stopping. Reconnect after it restarts, refresh the exact session address, then inspect existing receipts before retrying any message."),
        "inbox_cursor_reset_required" or "invalid_inbox_cursor" => new(code, "read_inbox_page",
            "Discard the inbox cursor and rescan without it, deduplicating by stable message ID. Retain needed content before acknowledgement."),
        "native_recovery_cursor_reset_required" or "invalid_native_recovery_cursor" => new(code, "read_native_recovery",
            "Discard the recovery cursor and rescan with your current verified native address. Deduplicate by message ID and keep original addresses unchanged."),
        "native_recovery_unverified" => new(code, "list_sessions",
            "Wait for fresh matching native-adapter discovery. A name, label, note or native-looking registration cannot establish recovery authority."),
        "reply_budget_exhausted" => new(code, "send",
            "If another response is authorized, send with a fresh message ID and the same conversationId. Keep the exchange bounded and preserve peer permissions."),
        "message_id_collision" => new(code, "get_message_receipt",
            "Inspect the existing ID. Retries reuse identical content and addresses; use a fresh ID only for a deliberately new message."),
        "unpaired_peer" or "private_session" or "recipient_cannot_receive" => new(code, "list_sessions",
            "Delivery is not authorized or the recipient cannot receive. Preserve that boundary; do not route through another session to bypass it."),
        "recipient_queue_full" or "message_limit" => new(code, "get_message_receipt",
            "Admission is refused at a storage bound. Check the stable message ID, then wait for capacity; do not generate repeated new messages."),
        "notice_reply_refused" => new(code, "acknowledge_message",
            "A delivery notice cannot be answered. Acknowledge it (Delivered); to follow up, inspect your original message's receipt and, if authorized, send from your current address."),
        // X3 (AgentBroker next campaign 2026-11-08): local display hints only; none of these affects delivery or routing.
        "native_session_unverified" => new(code, "list_sessions",
            "Declarations need your exact current adapter-verified native row with Receive, and titles a canonical claude: row. Check your row in list_sessions; a generic mailbox, an observation-only row or a codex: title cannot set one."),
        "invalid_session_declaration" or "invalid_session_title" => new(code, "list_sessions",
            "Correct the value to the bounds in the tool description and retry on the same exact address; an empty value clears it."),
        "declaration_limit" or "session_title_limit" => new(code, "list_sessions",
            "The local hint capacity is in use. Clear a hint you set or retry later; never register another session to get around it."),
        _ => null
    };

    internal static RecoveryGuidance? ForReceipt(MessageReceipt receipt)
    {
        if (receipt.Status == MessageStatus.Uncertain)
            return new(receipt.Reason ?? "delivery_uncertain", "get_message_receipt",
                "Retrieve your original text with includeText if needed, check for actual recipient acknowledgement or reply, and coordinate before a deliberate new send. Reusing this ID only reads the existing outcome; it does not reinject uncertain delivery.", true);
        if (receipt.Status == MessageStatus.Refused && receipt.Reason == "recipient_replaced")
            return new(receipt.Reason, "list_sessions",
                "The old incarnation was replaced and this accepted message was refused before native submission. Inspect the receipt, discover the current complete address, and choose a fresh message ID only for a deliberately new authorized send.");
        if (receipt.Status is MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted)
            return new(receipt.Reason ?? "awaiting_recipient_receipt", "wait_for_receipt",
                "Wait for actual recipient acknowledgement. Submission is transport admission, not receipt or work completion. A recipient doing active work can fetch pending content with read_inbox_page.",
                receipt.Status is MessageStatus.Submitting or MessageStatus.Submitted);
        return null;
    }

    internal static void AppendReceipt(CallToolResult result, HttpContext context)
    {
        if (result.IsError is true || context.Items[ReceiptKey] is not RecoveryGuidance guidance) return;
        result.Content = [.. result.Content, new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new { kind = "agentbroker.recovery_guidance", guidance }, McpJsonUtilities.DefaultOptions)
        }];
    }

    // The SDK turns an exception into a CallToolResult outside the tool-request filter.
    // Add advice to that final result before it is serialized, using only our typed failure.
    internal static void AppendError(JsonNode? result, HttpContext context)
    {
        if (context.Items[FailureKey] is not BrokerToolFailure failure ||
            result is not JsonObject response || response["isError"] is not JsonValue error ||
            !error.TryGetValue<bool>(out var isError) || !isError ||
            response["content"] is not JsonArray content ||
            ForError(failure.Code) is not { } guidance) return;
        guidance = guidance with
        {
            Availability = failure.Availability,
            Operation = context.Items[OperationKey] as string
        };
        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = JsonSerializer.Serialize(new { kind = "agentbroker.recovery_guidance", guidance }, McpJsonUtilities.DefaultOptions)
        });
    }
}
