// Linked into the thin module. This protocol contains no host or ASP.NET dependencies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBroker.Protocol;

public sealed record PeerView(Guid BrokerId, string Name, string Endpoint, string CertificateSha256);
public sealed record PairingView(Guid RequestId, PeerView Peer, string VerificationCode,
    DateTimeOffset ExpiresAt, bool Outbound, bool LocalApproved = false, bool RemoteApproved = false);
public sealed record SessionView(string SessionId, Guid Incarnation, string Label, string Adapter,
    string Activity, bool CanReceive, bool CanWake, bool IsPrivate, bool IsMuted);
public enum McpClientFamily { Other = 0, Claude = 1, Codex = 2 }
public sealed record McpClientActivityView(McpClientFamily Family, DateTimeOffset? LastInitializeUtc = null,
    DateTimeOffset? LastSuccessfulRequestUtc = null, DateTimeOffset? LastToolsListUtc = null,
    DateTimeOffset? LastSuccessfulToolUtc = null);
// A generic inbox whose successful register_agent call itself carried a verified Claude or Codex
// client identity. Registrations without one are never attributed, and a label never is.
public sealed record McpInboxAttributionView(string SessionId, Guid Incarnation, McpClientFamily Family);
// Optional trailing members are omitted while null, so a snapshot without them still reads in an older module.
public sealed record McpStatusView(bool EndpointReady, DateTimeOffset? LastSuccessfulRequestUtc,
    DateTimeOffset? LastToolsListUtc, McpClientActivityView[] Clients,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpInboxAttributionView[]? Inboxes = null);
// Fixed codes only, never an address, interface name, network name or rule text. Category is the
// Windows network category of the interface that carries the advertised LAN address; Rule is this
// worker's own firewall rule as read without elevation; LastInboundUtc is the last HTTPS request that
// reached the LAN listener from another private address since this worker started.
public sealed record LanProfileView(string Category, string Rule, int RuleProfiles, DateTimeOffset? CheckedUtc,
    DateTimeOffset? LastInboundUtc);
// Delivery notices the store dropped since the worker started, by why they could not be admitted (counts only).
public sealed record NoticeDropView(int SenderUnavailable, int StoreFull, int QueueFull, DateTimeOffset? LastUtc);
public sealed record BrokerSnapshot(Guid BrokerId, string Name, string LoopbackEndpoint, string? LanEndpoint,
    PeerView[] Discovered, PeerView[] Paired, PairingView[] PairingOffers, SessionView[] Sessions,
    IReadOnlyDictionary<string, string> AdapterErrors, string? CertificateSha256 = null, McpStatusView? Mcp = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LanProfileView? Lan = null,
    // Absent while nothing was dropped, so that snapshot is exactly the one before this field existed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoticeDropView? DroppedNotices = null);
public sealed record ControlRequest(string Operation, Guid Id = default, PeerView? Peer = null,
    string? SessionId = null, bool Value = false);
public sealed record LocalConnection(string McpEndpoint, string? BearerToken);
public sealed record PeerDiagnosticView(Guid BrokerId, string Endpoint, string CertificateSha256,
    bool Success, string Code, int? ReturnedSessionCount, string AuthenticationStatus, DateTimeOffset? CheckedAtUtc);
public sealed record ControlResponse(bool Success, string Code, BrokerSnapshot? Snapshot = null,
    LocalConnection? Connection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PeerDiagnosticView[]? PeerDiagnostics = null);

public static class ControlProtocol
{
    public const int MaximumFrameBytes = 512 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
      MaxDepth = 16 };
    public static string PipeName(string dataDirectory) => "desktop-ai-companion.agentbroker." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant())))[..24];
    public static bool IsPairingOperation(string? operation) => operation is "pair-request" or "pair-approve";
    // Requesting pairing performs two ten-second HTTPS exchanges. The worker must finish
    // and report a classified result before its caller's outer deadline expires.
    public static TimeSpan ClientTimeout(string? operation) => TimeSpan.FromSeconds(IsPairingOperation(operation) ? 30 :
        operation == "peer-diagnostics" ? 8 : 3);
    public static TimeSpan ServerTimeout(string? operation) => TimeSpan.FromSeconds(IsPairingOperation(operation) ? 25 :
        operation == "peer-diagnostics" ? 7 : 20);
    public static string FailureCode(string? operation, Exception error)
    {
        if (!IsPairingOperation(operation)) return "control_request_failed";
        if (error is OperationCanceledException) return "pairing_network_timeout";
        if (error is UnauthorizedAccessException) return "pairing_identity_refused";
        if (error is HttpRequestException http)
        {
            if (http.HttpRequestError == HttpRequestError.SecureConnectionError || http.InnerException is AuthenticationException)
                return "pairing_tls_failed";
            if (http.StatusCode == HttpStatusCode.Forbidden) return "pairing_peer_refused";
            if (http.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict) return "pairing_offer_invalid";
            if (http.StatusCode is not null) return "pairing_http_failed";
            if (http.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return "pairing_connection_refused";
            return "pairing_connection_failed";
        }
        if (error is InvalidDataException or JsonException or ArgumentException) return "pairing_offer_invalid";
        return "pairing_transport_failed";
    }
}
