namespace AgentBroker.Worker;

/// <summary>Non-secret compatibility information from a completed discovery pass.</summary>
public sealed record AdapterDiscoveryDiagnostic(string Code, string Description)
{
    public string ToStatusText() => Clean(Code, 64) + ": " + Clean(Description, 320);

    /// <summary>
    /// The description for an installed client newer than every pin (lane broker-pins): its name and version, and that
    /// a reviewed re-pin arrives as a module update, FIRST, because the pane's per-client Native line shows only the
    /// first 160 characters of its adapter's status. The verified list follows for the full Native adapters field.
    /// Nothing here looks anything up: re-pins reach users only inside a published module.
    /// </summary>
    internal static string NewerThanVerified(string client, string version, string verified)
        => client + " " + version + " installed is newer than every verified build; a reviewed re-pin arrives as a module update. Verified " +
            verified + ".";

    private static string Clean(string value, int limit)
        => new(value.Where(character => !char.IsControl(character)).Take(limit).ToArray());
}

/// <summary>A failed discovery whose specific compatibility reason is safe to display.</summary>
public sealed class AdapterDiscoveryException(AdapterDiscoveryDiagnostic diagnostic, Exception? inner = null)
    : IOException(diagnostic.ToStatusText(), inner)
{
    public AdapterDiscoveryDiagnostic Diagnostic { get; } = diagnostic;
}
