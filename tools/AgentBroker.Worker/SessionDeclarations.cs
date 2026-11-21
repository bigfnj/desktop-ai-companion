using AgentBroker.Core;

namespace AgentBroker.Worker;

/// <summary>Untrusted local-client hints, separate from the native label, activity and exact routing identity.</summary>
public sealed record SessionDeclaration(string DisplayName, string Note, string? Availability,
    DateTimeOffset? ResumeAtUtc, DateTimeOffset DeclaredAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public string Source => "local_client_declaration";
}

internal sealed class SessionDeclarationRegistry
{
    internal const int MaximumDeclarations = 256;
    private readonly object gate = new();
    private readonly Dictionary<SessionAddress, SessionDeclaration> declarations = [];

    internal SessionDeclaration? Set(SessionAddress address, string displayName, string note, string? availability,
        DateTimeOffset? resumeAtUtc, int ttlMinutes, DateTimeOffset now, Func<SessionAddress, bool> eligible)
    {
        address.Validate();
        if (!eligible(address)) throw new BrokerException("native_session_unverified", "An exact current adapter-verified native row is required.");
        if (!ValidText(displayName, 160) || !ValidText(note, 512) || ttlMinutes is < 1 or > 1440 ||
            availability is not (null or "available" or "busy" or "waiting" or "rate_limited" or "unavailable") ||
            resumeAtUtc is { } resume && (resume < now || resume > now.AddHours(24)))
            throw new BrokerException("invalid_session_declaration", "Declared name, note, availability, resume time or lifetime is invalid.");
        lock (gate)
        {
            Prune(now, eligible);
            if (displayName.Length == 0 && note.Length == 0 && availability is null && resumeAtUtc is null)
            { declarations.Remove(address); return null; }
            if (declarations.Count >= MaximumDeclarations && !declarations.ContainsKey(address))
                throw new BrokerException("declaration_limit", "The local session declaration limit was reached.");
            var value = new SessionDeclaration(displayName, note, availability, resumeAtUtc, now, now.AddMinutes(ttlMinutes));
            declarations[address] = value;
            return value;
        }
    }

    internal IReadOnlyDictionary<SessionAddress, SessionDeclaration> Snapshot(DateTimeOffset now, Func<SessionAddress, bool> eligible)
    {
        lock (gate) { Prune(now, eligible); return new Dictionary<SessionAddress, SessionDeclaration>(declarations); }
    }

    private void Prune(DateTimeOffset now, Func<SessionAddress, bool> eligible)
    {
        foreach (var address in declarations.Where(entry => entry.Value.ExpiresAtUtc <= now || !eligible(entry.Key)).Select(entry => entry.Key).ToArray())
            declarations.Remove(address);
    }

    private static bool ValidText(string value, int maximum)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl)) return false;
        try { _ = value.Normalize(); return true; }
        catch (ArgumentException) { return false; }
    }
}
