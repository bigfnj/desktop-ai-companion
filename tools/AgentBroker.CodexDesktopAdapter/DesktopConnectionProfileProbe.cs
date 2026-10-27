namespace AgentBroker.CodexDesktopAdapter;

/// <summary>The public-image profile that passed the connected router's existing verification.</summary>
public enum DesktopRouterProfile { Desktop, VsCode }

/// <summary>Connection verification only; this result establishes no conversation owner or delivery route.</summary>
public sealed record DesktopConnectionProfileResult(DesktopRouterProfile Profile);

/// <summary>
/// Connects, verifies the existing router and closes without initializing the native protocol or reading its stream.
/// </summary>
public static class DesktopConnectionProfileProbe
{
    public static Task<DesktopConnectionProfileResult> ObserveAsync(TimeSpan? timeout = null,
        CancellationToken token = default)
        => ObserveForTestAsync(WindowsDesktopConnection.ConnectAsync, timeout, token);

    internal static async Task<DesktopConnectionProfileResult> ObserveForTestAsync(
        Func<TimeSpan, CancellationToken, Task<IVerifiedDesktopConnection>> connect,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        var options = new DesktopAdapterOptions(RequestTimeout: timeout);
        options.Validate();
        token.ThrowIfCancellationRequested();
        await using var connection = await connect(options.Timeout, token);
        token.ThrowIfCancellationRequested();
        // A profile belongs to this verified connection, never merely to an installed image or inferred client.
        connection.VerifyStillSameOwner();
        var profile = connection.Profile;
        if (profile is not (DesktopRouterProfile.Desktop or DesktopRouterProfile.VsCode))
            throw new DesktopAdapterException("invalid_connection_profile");
        return new(profile);
    }

    /// <summary>
    /// The same connect-verify-close probe under the discovery observation policy, reporting how the router was
    /// trusted: an exact pin, or Windows-verified publisher evidence after a named exact-pin refusal. It sends no
    /// initialization, discovery or input, and its result carries only a fixed code and a numeric version.
    /// </summary>
    public static Task<DesktopConnectionTrustResult> ObserveTrustAsync(TimeSpan? timeout = null, CancellationToken token = default)
        => ObserveTrustForTestAsync((limit, cancel) => WindowsDesktopConnection.ConnectAsync(limit, allowPublisherSigned: true, cancel),
            timeout, token);

    internal static async Task<DesktopConnectionTrustResult> ObserveTrustForTestAsync(
        Func<TimeSpan, CancellationToken, Task<IVerifiedDesktopConnection>> connect,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        var options = new DesktopAdapterOptions(RequestTimeout: timeout);
        options.Validate();
        token.ThrowIfCancellationRequested();
        await using var connection = await connect(options.Timeout, token);
        token.ThrowIfCancellationRequested();
        connection.VerifyStillSameOwner();
        var profile = connection.Profile;
        if (profile is not (DesktopRouterProfile.Desktop or DesktopRouterProfile.VsCode) ||
            connection.Trust is not (DesktopRouterTrust.ExactPinned or DesktopRouterTrust.PublisherSigned))
            throw new DesktopAdapterException("invalid_connection_profile");
        return new(profile, connection.Trust, connection.ExactPinRefusal?.Code, connection.ExactPinRefusal?.InstalledVersion);
    }
}

/// <summary>Connection trust only; this result establishes no conversation owner or delivery route.</summary>
public sealed record DesktopConnectionTrustResult(DesktopRouterProfile Profile, DesktopRouterTrust Trust,
    string? ExactPinRefusalCode, string? InstalledVersion);

internal interface IVerifiedDesktopConnection : IDesktopConnection
{
    DesktopRouterProfile Profile { get; }
}
