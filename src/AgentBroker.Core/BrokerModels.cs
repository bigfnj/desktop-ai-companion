namespace AgentBroker.Core;

[Flags]
public enum SessionCapabilities { None = 0, Receive = 1, WakeIdle = 2, Reply = 4, NativeIdleGuard = 8 }
// Values travel as numbers, so a member is only ever APPENDED. Away is display-only: a local directory
// shows it for a retained generic mailbox whose presence lease lapsed. It never reaches a peer
// directory and a registration cannot claim it.
public enum SessionActivity { Idle, Busy, ApprovalRequired, Interrupted, Disconnected, Unknown, Away }
// Delivered records an adapter/peer admission receipt supplied by the caller. It never proves
// agent execution, response completion, or visibility in a vendor GUI.
public enum MessageStatus { Accepted, Submitting, Delivered, Held, Refused, Expired, Uncertain, Submitted }

public sealed record SessionAddress(Guid BrokerId, string SessionId, Guid Incarnation)
{
    public void Validate()
    {
        if (BrokerId == Guid.Empty || Incarnation == Guid.Empty || string.IsNullOrWhiteSpace(SessionId) ||
            SessionId.Length > 160 || SessionId.Any(char.IsControl))
            throw new BrokerException("invalid_session", "A complete session address is required.");
    }
}

// Labels are user supplied; they never identify a session or authorize a delivery.
public sealed record BrokerSession(SessionAddress Address, string Label, string Adapter,
    SessionCapabilities Capabilities, SessionActivity Activity, DateTimeOffset LeaseUntil, bool IsPrivate = false);

public sealed record PeerTrust(Guid BrokerId, string Name, string Endpoint, string CertificateSha256,
    DateTimeOffset PairedAt);

public sealed record BrokerMessage(Guid Id, Guid ConversationId, Guid? ReplyTo,
    SessionAddress Sender, SessionAddress Recipient, string Text,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int RemainingReplies = 8);

public sealed record MessageReceipt(BrokerMessage Message, MessageStatus Status,
    DateTimeOffset UpdatedAt, string? Reason = null, int Attempts = 0);

public sealed class BrokerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record BrokerLimits(int MaximumMessages = 4096, int MaximumTextBytes = 16 * 1024,
    int MaximumSessions = 256, int MaximumPeers = 32, int MaximumPendingPerSession = 64,
    int MaximumReplies = 8, int DefaultMessageLifetimeMinutes = 1440)
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumSessionLease = TimeSpan.FromMinutes(2);
    // A generic MCP mailbox stays addressable this long after its owner's last call, so a turn-based
    // agent keeps receiving between turns without a renewal sidecar. Process-local, never persisted.
    public static readonly TimeSpan MailboxRetention = TimeSpan.FromHours(24);
    public const int MaximumRetainedMailboxes = 64;
}
