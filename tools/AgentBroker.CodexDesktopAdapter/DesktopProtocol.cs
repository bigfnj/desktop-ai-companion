using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBroker.CodexDesktopAdapter;

public sealed class DesktopAdapterException : Exception
{
    public string Code { get; }
    public string? InstalledVersion { get; }

    public DesktopAdapterException(string code) : this(code, null) { }

    public DesktopAdapterException(string code, string? installedVersion) : base(code)
    {
        Code = code;
        // Diagnostic metadata is restricted to a public numeric version, never an image path or native error body.
        InstalledVersion = Version.TryParse(installedVersion, out var parsed) ? parsed.ToString() : null;
    }
}

public sealed record DesktopAdapterOptions(bool EnableAuthorizedPlaintextDelivery = false,
    TimeSpan? RequestTimeout = null, int MaximumMessageBytes = 16 * 1024,
    bool EnableExternalPeerDelivery = false)
{
    /// <summary>
    /// Discovery may admit a router that fails its exact pin when Windows verifies the expected publisher
    /// (and, for the desktop package, its protected WindowsApps location). Such a connection is observation
    /// only: its bindings carry <see cref="DesktopRouterTrust.PublisherSigned"/> and every submission refuses.
    /// </summary>
    public bool AllowPublisherSignedRouter { get; init; }
    /// <summary>Opt-in update-tolerant delivery through a publisher-signed router. Strict exact pins stay the default.</summary>
    public bool EnableUpdateTolerantDelivery { get; init; }
    internal TimeSpan Timeout => RequestTimeout ?? TimeSpan.FromSeconds(5);
    // Pinned VS Code routers can spend ten seconds discovering a recipient.
    // Leave time for that result without changing connect or submission limits.
    internal TimeSpan DiscoveryTimeout => RequestTimeout ?? TimeSpan.FromSeconds(12);
    internal void Validate()
    {
        if (Timeout < TimeSpan.FromMilliseconds(50) || Timeout > TimeSpan.FromSeconds(30)
            || MaximumMessageBytes < 1 || MaximumMessageBytes > 32 * 1024
            || EnableUpdateTolerantDelivery && (!AllowPublisherSignedRouter || !EnableExternalPeerDelivery))
            throw new DesktopAdapterException("invalid_options");
    }
}

public sealed record DesktopOwnerBinding(Guid ConversationId, Guid OwnerClientId, Guid Incarnation,
    uint ServerPid, ulong ServerCreationFileTime, bool SupportsUntrustedAppInput)
{
    /// <summary>
    /// The trust of the router connection that discovered this owner. The adapter sets it from its verified
    /// connection and refuses a binding whose trust differs; the default grants no delivery authority.
    /// </summary>
    public DesktopRouterTrust RouterTrust { get; init; } = DesktopRouterTrust.Unverified;
    /// <summary>Stable broker address identity while the same native GUI process/client owns this conversation.</summary>
    public Guid NativeSessionIncarnation
    {
        get
        {
            string identity = FormattableString.Invariant($"codex-owner-v1|{ServerPid}|{ServerCreationFileTime}|{OwnerClientId:D}|{ConversationId:D}");
            byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
            return new Guid(digest.AsSpan(0, 16));
        }
    }
}

public enum DesktopSubmissionOutcome { Accepted, Rejected, Uncertain, DeferredNotSubmitted }

public sealed record DesktopSubmissionResult(DesktopSubmissionOutcome Outcome,
    string? TurnId = null, string? ErrorCode = null, bool Duplicate = false,
    DesktopProtocolDiagnostic? Diagnostic = null);

public sealed record DesktopProtocolDiagnostic(string[] FrameKeys, int UnknownFrameKeyCount,
    string? ResultType, bool MethodPresent, bool OwnerClientPresent, string ResultShape,
    string[] ResultKeys, string[] NativeResultKeys, string? NativeErrorCategory);

internal sealed record OwnerProcessIdentity(uint Pid, ulong CreationFileTime, string ImagePath);

internal interface IDesktopConnection : IAsyncDisposable
{
    Stream Stream { get; }
    OwnerProcessIdentity Identity { get; }
    // Every implementation states its trust; there is deliberately no default member that could grant a pin.
    DesktopRouterTrust Trust { get; }
    // The exact-pin refusal a publisher-signed router fell back from: diagnostic only, never authority.
    DesktopAdapterException? ExactPinRefusal => null;
    void VerifyStillSameOwner();
}

internal static class DesktopFrames
{
    internal const int MaximumFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };

    internal static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is < 1 or > MaximumFrameBytes)
            throw new DesktopAdapterException("frame_too_large");
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<JsonObject> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes)
            throw new DesktopAdapterException("invalid_frame_size");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        try
        {
            return JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
                ?? throw new DesktopAdapterException("invalid_frame");
        }
        catch (JsonException) { throw new DesktopAdapterException("invalid_frame"); }
    }

    internal static string? Text(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool IsTrue(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    internal static JsonObject? Object(JsonNode? node, string key) => node is JsonObject obj ? obj[key] as JsonObject : null;

    internal static Guid Id(JsonNode? node, string key)
        => Guid.TryParseExact(Text(node, key), "D", out var id) && id != Guid.Empty
            ? id : throw new DesktopAdapterException("invalid_protocol_identity");

    internal static DesktopProtocolDiagnostic DescribeSubmissionResponse(JsonObject response)
    {
        string[] known = ["type", "requestId", "resultType", "method", "handledByClientId", "result", "error"];
        var result = Object(response, "result");
        var native = Object(result, "result");
        string? error = Text(response, "error");
        string? category = error switch
        {
            null => null,
            "no-client-found" => "owner_unavailable",
            "User-message admission requires a cloud app-server host." => "cloud_host_required",
            "User-message admission is unavailable on this app-server transport" => "user_message_admission_unavailable",
            "Aeon user messages require a message group ID" => "message_group_required",
            "App context must wait until the current turn finishes" => "context_busy_not_submitted",
            _ when error.Contains("timeout", StringComparison.OrdinalIgnoreCase) => "native_timeout_unconfirmed",
            _ => "native_error_unclassified"
        };
        string[] Keys(JsonObject? obj, string[] allowed) => obj is null ? []
            : allowed.Where(obj.ContainsKey).ToArray();
        string shape = response["result"] switch
        { null => "null", JsonObject => "object", JsonArray => "array", JsonValue => "scalar", _ => "other" };
        return new(Keys(response, known), response.Count - Keys(response, known).Length,
            Text(response, "resultType") is "success" or "error" ? Text(response, "resultType") : null,
            response.ContainsKey("method"), response.ContainsKey("handledByClientId"), shape,
            Keys(result, ["result", "turn", "admissionOutcome", "settings"]),
            Keys(native, ["turn", "admissionOutcome", "turnId", "status"]), category);
    }
}
