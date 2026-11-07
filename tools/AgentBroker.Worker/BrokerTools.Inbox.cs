using AgentBroker.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBroker.Worker;

public sealed partial class BrokerTools
{
    [McpServerTool(Name = "read_inbox_page", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true),
     Description("Fetch this exact local session's pending messages without blocking, including native sessions while their turn is active. Returns entries in broker arrival order, pendingCount, hasMore, and an opaque nextCursor. Keep the cursor between polls; after inbox_cursor_reset_required (worker restart), discard it and rescan without a cursor, deduplicating by message ID. Pending means Accepted, Submitting, Submitted or Uncertain and not expired; Held, Delivered and Refused are already acknowledged. limit is 1..16, default 16. Reading does not acknowledge or cancel native submission, so the same ID can also arrive through wake: handle it once. Retain content before acknowledge_message. session_unavailable means refresh native discovery, or re-register the same generic mailbox identity; never invent a native mailbox. This is an explicit active-turn fetch, not a second mailbox or background poller.")]
    public PendingInboxPage ReadInboxPage(SessionAddress session, string? cursor = null, int limit = MaximumPageSize)
    {
        ValidateLimit(limit);
        CheckParticipant(session);
        RequireLocal();
        var page = Run(() => runtime.Store.ReadPendingInbox(session, cursor, limit));
        runtime.RenewPollingAgent(session, Caller);
        ObserveInboxParticipant(session);
        return page;
    }

    // Per-request only. A Codex transport serves several chats, so no process, client label, last caller or global
    // connection cache can establish the inbox. Only a successful operation's exact participant can supply a hint.
    private void ObserveInboxParticipant(SessionAddress participant)
    {
        if (participant.BrokerId == runtime.Store.BrokerId && Caller == runtime.Store.BrokerId && http.HttpContext is { } context)
            context.Items[McpInboxHints.ParticipantKey] = participant;
    }
}

internal static class McpInboxHints
{
    internal const string ParticipantKey = "agentbroker.inbox-participant";
    private static readonly JsonSerializerOptions HintJson = new(McpJsonUtilities.DefaultOptions)
    { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    // Append only. Existing clients read content[0], and send may already carry its polling-only advisory afterward.
    internal static void Append(CallToolResult result, HttpContext context, BrokerRuntime runtime)
    {
        if (result.IsError is true || context.Items[ParticipantKey] is not SessionAddress session) return;
        int? pending = null;
        int? recoveryPending = null;
        string? reason = null;
        try
        {
            pending = runtime.Store.PendingIncomingCount(session);
            recoveryPending = runtime.Store.NativeRecoveryPendingCount(session);
        }
        catch (BrokerException error) { reason = error.Code; }
        catch (ObjectDisposedException) { reason = "pending_status_unavailable"; }
        var hint = new Dictionary<string, object?>
        {
            ["kind"] = "agentbroker.pending_messages", ["session"] = session,
            ["status"] = pending is null ? "unavailable" : "available", ["pendingCount"] = pending,
            ["fetchTool"] = "read_inbox_page", ["reason"] = reason
        };
        if (recoveryPending is not null)
        {
            hint["recoveryPendingCount"] = recoveryPending;
            hint["recoveryTool"] = "read_native_recovery";
        }
        result.Content = [.. result.Content, new TextContentBlock
        {
            Text = JsonSerializer.Serialize(hint, HintJson)
        }];
    }
}
