namespace AgentBroker.ClaudeAdapter;

public sealed record EngineProfile(string Version, string Sha256)
{
    // Verified public standalone executable, not a claim about the Desktop-bundled runtime.
    public static readonly EngineProfile Standalone21292 = new("2.1.292",
        "EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23");
    // Independently inspected public Windows VS Code extension runtime.
    public static readonly EngineProfile VsCode21304 = new("2.1.304",
        "019E5174D5D954585EDB1A946FE280E44A973050B4897034ADF0E16306F85FDD");
    // The public Windows VS Code 2.1.306 native source was independently checked after client update.
    public static readonly EngineProfile VsCode21306 = new("2.1.306",
        "F6BC9DE38F507C36C9A3BB0085A59A27E32156E2208252BF1707C4E3EF309114");
    public static IReadOnlyList<EngineProfile> Verified { get; } = Array.AsReadOnly(new[] { Standalone21292, VsCode21304, VsCode21306 });

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
