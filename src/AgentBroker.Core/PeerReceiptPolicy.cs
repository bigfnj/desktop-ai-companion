namespace AgentBroker.Core;

/// <summary>Known peer refusal metadata only; never a routing or admission decision.</summary>
public static class PeerReceiptPolicy
{
    public static string? KnownRefusalReason(string? reason) => reason is "recipient_replaced" or
        "recipient_unavailable" or "recipient_cannot_receive" or "private_session" or "recipient_queue_full" or "message_limit"
        ? reason : null;
}
