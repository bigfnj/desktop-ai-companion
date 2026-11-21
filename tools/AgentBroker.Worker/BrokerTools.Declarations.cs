using AgentBroker.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AgentBroker.Worker;

public sealed partial class BrokerTools
{
    [McpServerTool(Name = "set_session_declaration", Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Attach bounded local-client hints to an exact current adapter-verified native row. displayName (up to 160 characters) and note (up to 512) never replace its native label or routing address. availability is optional: available, busy, waiting, rate_limited or unavailable. resumeAtUtc is a declared estimate within the next 24 hours, never an observed fact. ttlMinutes is 1..1440 (default 60). Empty name/note with no availability or resume time clears the declaration. Hints are local only and disappear on expiry, loss of verified native observation, incarnation change or worker restart. The broker verifies the row, not which chat owns a shared connection; these are untrusted local-client declarations, not authentication or activity. Generic inbox labels continue to use register_agent.")]
    public SessionDeclaration? SetSessionDeclaration(SessionAddress session, string displayName = "", string note = "",
        string? availability = null, DateTimeOffset? resumeAtUtc = null, int ttlMinutes = 60)
    {
        CheckParticipant(session);
        RequireLocal();
        var declaration = Run(() => runtime.SessionDeclarations.Set(session, displayName, note, availability, resumeAtUtc,
            ttlMinutes, runtime.StoreNow, runtime.Store.IsVerifiedNativeSession));
        ObserveInboxParticipant(session);
        return declaration;
    }
}
