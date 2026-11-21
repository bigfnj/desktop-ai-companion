using AgentBroker.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AgentBroker.Worker;

public sealed partial class BrokerTools
{
    [McpServerTool(Name = "read_native_recovery", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true),
     Description("Report-only local inventory for your current adapter-discovered canonical native session across its incarnations. Generic registrations, labels and native-looking strings do not establish recovery authority. Returns original incoming/outgoing receipts and reply links, content availability, recoveryPendingCount and a bounded cursor. canAcknowledge and canReply describe only ordinary operations for messages addressed to your CURRENT exact incarnation; prior incarnations confer no action rights. Record order is local inventory observation order, not historical arrival. Retain nextCursor for newly seen records; rescan without a cursor to refresh existing statuses. A worker restart or privacy/trust policy change returns native_recovery_cursor_reset_required: discard cursor and rescan. Invalid/wrong-address cursors are refused. limit is 1..16. Nothing is retargeted, acknowledged, replied to or reinjected. Blocked peer content is omitted. For prior-incarnation work, explicitly contact the peer using send from your current address after refreshing list_sessions, with a fresh messageId and retained conversationId if useful. There is no automatic recovery wake.")]
    public NativeRecoveryPage ReadNativeRecovery(SessionAddress session, string? cursor = null, int limit = MaximumPageSize)
    {
        ValidateLimit(limit); CheckParticipant(session); RequireLocal();
        var page = Run(() => runtime.Store.ReadNativeRecovery(session, cursor, limit));
        ObserveInboxParticipant(session);
        return page;
    }

}
