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
}

internal interface IVerifiedDesktopConnection : IDesktopConnection
{
    DesktopRouterProfile Profile { get; }
}
