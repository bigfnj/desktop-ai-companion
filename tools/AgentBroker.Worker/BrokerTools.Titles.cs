using AgentBroker.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AgentBroker.Worker;

public sealed partial class BrokerTools
{
    [McpServerTool(Name = "set_session_title", Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Set or clear an untrusted local-client custom title on an exact current adapter-verified canonical claude: row. title is at most 160 characters; empty clears it. It never replaces native Label, routing identity, manual declarations or availability. Titles have no declaration TTL, disappear on worker restart, and are shown only with current exact native proof. A successor incarnation inherits nothing. Local broker access does not authenticate which chat owns a shared connection. The opt-in documented Claude title hook obtains its own whoami row and checks session_id before calling this operation. Generic registrations and native-looking labels do not establish eligibility.")]
    public SessionTitle? SetSessionTitle(SessionAddress session, string title)
    {
        CheckParticipant(session);
        RequireLocal();
        var value = Run(() => runtime.SessionTitles.Set(session, title, runtime.StoreNow, runtime.Store.IsVerifiedNativeSession));
        ObserveInboxParticipant(session);
        return value;
    }
}
