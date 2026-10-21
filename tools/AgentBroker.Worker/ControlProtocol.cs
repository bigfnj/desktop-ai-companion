// Linked into the thin module. This protocol contains no host or ASP.NET dependencies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Protocol;

public sealed record PeerView(Guid BrokerId, string Name, string Endpoint, string CertificateSha256);
public sealed record PairingView(Guid RequestId, PeerView Peer, string VerificationCode,
    DateTimeOffset ExpiresAt, bool Outbound, bool LocalApproved = false, bool RemoteApproved = false);
public sealed record SessionView(string SessionId, Guid Incarnation, string Label, string Adapter,
    string Activity, bool CanReceive, bool CanWake, bool IsPrivate, bool IsMuted);
public sealed record BrokerSnapshot(Guid BrokerId, string Name, string LoopbackEndpoint, string? LanEndpoint,
    PeerView[] Discovered, PeerView[] Paired, PairingView[] PairingOffers, SessionView[] Sessions,
    IReadOnlyDictionary<string, string> AdapterErrors, string? CertificateSha256 = null);
public sealed record ControlRequest(string Operation, Guid Id = default, PeerView? Peer = null,
    string? SessionId = null, bool Value = false);
public sealed record LocalConnection(string McpEndpoint, string? BearerToken);
public sealed record ControlResponse(bool Success, string Code, BrokerSnapshot? Snapshot = null,
    LocalConnection? Connection = null);

public static class ControlProtocol
{
    public const int MaximumFrameBytes = 512 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
      MaxDepth = 16 };
    public static string PipeName(string dataDirectory) => "desktop-ai-companion.agentbroker." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant())))[..24];
}
