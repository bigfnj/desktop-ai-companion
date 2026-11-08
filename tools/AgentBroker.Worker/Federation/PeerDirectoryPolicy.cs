using System.Diagnostics.CodeAnalysis;

namespace AgentBroker.Worker.Federation;

// Validate collection elements explicitly: serializer nullability does not cover generic element types.
// Preserve the existing wire and local presentation caps. This is shape validation, not a new lease or routing policy.
internal static class PeerDirectoryPolicy
{
    internal const int WireMaximumSessions = 512;
    internal const int ListingMaximumSessions = 256;

    internal static bool HasValidShape([NotNullWhen(true)] PeerDirectory? directory, Guid expectedBroker, int maximumSessions) =>
        directory is not null && directory.BrokerId == expectedBroker && directory.Sessions is not null &&
        directory.Sessions.Count <= maximumSessions && directory.Sessions.All(session =>
            session is { Address: not null });

    internal static bool IsValid([NotNullWhen(true)] PeerDirectory? directory, Guid expectedBroker, int maximumSessions) =>
        HasValidShape(directory, expectedBroker, maximumSessions) && directory.Sessions.All(session =>
            !session.IsPrivate && session.Address.BrokerId == expectedBroker);
}
