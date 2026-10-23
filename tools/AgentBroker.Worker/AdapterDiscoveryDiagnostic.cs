namespace AgentBroker.Worker;

/// <summary>Non-secret compatibility information from a completed discovery pass.</summary>
public sealed record AdapterDiscoveryDiagnostic(string Code, string Description)
{
    public string ToStatusText() => Clean(Code, 64) + ": " + Clean(Description, 320);

    private static string Clean(string value, int limit)
        => new(value.Where(character => !char.IsControl(character)).Take(limit).ToArray());
}

/// <summary>A failed discovery whose specific compatibility reason is safe to display.</summary>
public sealed class AdapterDiscoveryException(AdapterDiscoveryDiagnostic diagnostic, Exception? inner = null)
    : IOException(diagnostic.ToStatusText(), inner)
{
    public AdapterDiscoveryDiagnostic Diagnostic { get; } = diagnostic;
}
