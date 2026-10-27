using AgentBroker.Pins;

namespace AgentBroker.ClaudeAdapter;

public sealed record EngineProfile(string Version, string Sha256)
{
    // The reviewed public executables (standalone or npm CLI, and the VS Code extension runtime; never a claim about
    // the Desktop-bundled runtime) are the "claudeCode" pins of the embedded document
    // tools/AgentBroker.Pins/agentbroker-pins.json; docs/agentbroker-client-compatibility.md says which client each
    // was reviewed in. A refused document leaves this list empty, so Find returns null for every build and no Claude
    // session can be woken.
    public static IReadOnlyList<EngineProfile> Verified { get; } =
        Array.AsReadOnly(PinDocument.Current.ClaudeCode.Select(pin => new EngineProfile(pin.Version, pin.Sha256)).ToArray());

    /// <summary>Why the embedded pin document was refused, or null when it validated.</summary>
    public static string? PinDocumentError => PinDocument.Current.Error;

    /// <summary>Status text only: the public version is newer than every verified build. Grants no trust.</summary>
    public static bool IsNewerThanEveryVerified(string? version) => PinDocument.IsNewer(version, Verified.Select(profile => profile.Version));

    public static EngineProfile? Find(string version, string sha256)
    {
        foreach (var profile in Verified)
            if (version == profile.Version && string.Equals(sha256, profile.Sha256, StringComparison.OrdinalIgnoreCase)) return profile;
        return null;
    }

    // Version metadata can cheaply refuse an update, but never grants trust without the image hash.
    public static bool IsSupportedPublicVersion(string? value)
    {
        if (!System.Version.TryParse(value, out var parsed) || parsed.Revision is not (0 or -1)) return false;
        string normalized = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{parsed.Major}.{parsed.Minor}.{parsed.Build}");
        return Verified.Any(profile => profile.Version == normalized);
    }
}

public enum RecipientMode { Unknown, Prompting, BypassPermissions }
public enum NativeActivity { Unknown, Idle, Busy, Shell, Waiting }
public enum NativeInboundSetting { Unspecified, Accept, Hold, Deny }
public enum SubmissionStatus { SubmittedUnconfirmed, Held, Refused }
public sealed record SubmissionResult(SubmissionStatus Status, string Reason);

public sealed record NativeTarget(string PipePath, int ProcessId, string ImagePath, string ImageSha256,
    string EngineVersion, ulong StartFileTime, string SessionId);

public sealed record NativeProcessIdentity(int ProcessId, string ImagePath, string ImageSha256,
    ulong StartFileTime, bool IsCurrentUser);

public sealed record NativeMessage(Guid MessageId, string SessionId, string Text,
    RecipientMode RecipientMode, NativeInboundSetting InboundSetting = NativeInboundSetting.Unspecified,
    bool DelegateNativeAdmission = false, NativeActivity Activity = NativeActivity.Unknown);

public sealed record CredentialTarget(string FullPath, int ProcessId, string CanonicalPipe);

public interface IPeerCredentialSource
{
    // Reads protected data in production. Invocation by an agent requires prior exact platform approval.
    // Tests use only fresh in-memory byte arrays, never a file fixture or a real credential path.
    byte[] Read(CredentialTarget target);
}

public interface INativePeerConnection : IAsyncDisposable
{
    bool EnforcesCurrentUser { get; }
    NativeProcessIdentity Identity { get; }
    Stream Outbound { get; }
    NativeProcessIdentity Reinspect();
}

public interface INativePeerConnectionFactory
{
    Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation);
}
