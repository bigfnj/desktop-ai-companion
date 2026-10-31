using AgentBroker.Core;
using System.Text.Json.Serialization;

namespace AgentBroker.Worker;

/// <summary>
/// A list_sessions row: the directory's BrokerSession, member for member, plus the native adapter's report-only engine
/// binding (N-agentbroker-code-owner-provenance-01), omitted while there is none, so a row without one reads exactly as
/// before. A separate type on purpose: BrokerSession is what a paired broker is sent and keeps the 1.0.4 members, and a
/// paired broker's caller and a paired broker's rows never get an engine binding.
/// </summary>
public sealed record ListedSession(SessionAddress Address, string Label, string Adapter,
    SessionCapabilities Capabilities, SessionActivity Activity, DateTimeOffset LeaseUntil, bool IsPrivate = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EngineBinding? EngineBinding = null)
{
    internal static ListedSession From(BrokerSession session, EngineBinding? engine) => new(session.Address, session.Label,
        session.Adapter, session.Capabilities, session.Activity, session.LeaseUntil, session.IsPrivate, engine);
}
