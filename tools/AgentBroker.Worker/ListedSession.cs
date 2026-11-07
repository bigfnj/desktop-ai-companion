using AgentBroker.Core;
using System.Text.Json.Serialization;

namespace AgentBroker.Worker;

/// <summary>
/// A list_sessions row: the directory's BrokerSession, member for member, plus optional report-only engine binding
/// and local-client declaration hints. Each extra field is omitted when absent. BrokerSession remains the separate
/// federation type, so paired brokers never receive these local fields.
/// </summary>
public sealed record ListedSession(SessionAddress Address, string Label, string Adapter,
    SessionCapabilities Capabilities, SessionActivity Activity, DateTimeOffset LeaseUntil, bool IsPrivate = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EngineBinding? EngineBinding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionDeclaration? LocalDeclaration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PendingMessageCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionTitle? LocalTitle = null)
{
    internal static ListedSession From(BrokerSession session, EngineBinding? engine, SessionDeclaration? declaration = null, int? pendingCount = null,
        SessionTitle? title = null) => new(session.Address, session.Label,
        session.Adapter, session.Capabilities, session.Activity, session.LeaseUntil, session.IsPrivate, engine, declaration, pendingCount, title);
}
