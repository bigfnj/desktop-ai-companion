using AgentBroker.Core;

namespace AgentBroker.Worker;

/// <summary>Untrusted local-client custom title. Never replaces a native label or exact routing address.</summary>
public sealed record SessionTitle(string Title, DateTimeOffset DeclaredAtUtc)
{
    public string Source => "local_client_custom_title";
}

internal sealed class SessionTitleRegistry
{
    internal const int MaximumTitles = 256;
    internal const int MaximumTitleLength = 160;
    private readonly object gate = new();
    private readonly Dictionary<SessionAddress, SessionTitle> titles = [];

    internal static bool CanonicalClaude(SessionAddress address) => address.SessionId.StartsWith("claude:", StringComparison.Ordinal) &&
        Guid.TryParseExact(address.SessionId.AsSpan(7), "D", out var id) && id != Guid.Empty &&
        address.SessionId[7..] == id.ToString("D");

    internal static bool ValidTitle(string? value)
    {
        if (value is null || value.Length > MaximumTitleLength || value.Any(char.IsControl)) return false;
        try { _ = value.Normalize(); return true; }
        catch (ArgumentException) { return false; }
    }

    internal SessionTitle? Set(SessionAddress address, string title, DateTimeOffset now, Func<SessionAddress, bool> eligible)
    {
        address.Validate();
        if (!CanonicalClaude(address) || !eligible(address))
            throw new BrokerException("native_session_unverified", "A current verified canonical Claude row is required.");
        if (!ValidTitle(title)) throw new BrokerException("invalid_session_title", "The local custom title is invalid.");
        lock (gate)
        {
            if (title.Length == 0) { titles.Remove(address); return null; }
            if (titles.Count >= MaximumTitles && !titles.ContainsKey(address))
            {
                var stale = titles.Where(pair => !eligible(pair.Key)).OrderBy(pair => pair.Value.DeclaredAtUtc).FirstOrDefault();
                if (stale.Key is not null) titles.Remove(stale.Key);
                if (titles.Count >= MaximumTitles) throw new BrokerException("session_title_limit", "The local title limit was reached.");
            }
            var value = new SessionTitle(title, now);
            titles[address] = value;
            return value;
        }
    }

    // Inactive entries remain bounded and can survive a same-incarnation discovery lapse, but are never displayed
    // without current native proof. New incarnations neither inherit nor retarget their predecessor's title.
    internal IReadOnlyDictionary<SessionAddress, SessionTitle> Snapshot(Func<SessionAddress, bool> eligible)
    {
        lock (gate) return titles.Where(pair => eligible(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
