using AgentBroker.Core;
using DesktopAICompanion.ModuleKit;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>Explicit privacy and pause choices survive discovery and worker restarts.</summary>
public sealed class SessionPreferences
{
    private sealed record Choice(bool IsPrivate, bool IsMuted);
    private readonly object gate = new();
    private readonly string path;
    private Dictionary<string, Choice> choices;
    public SessionPreferences(string directory)
    {
        path = Path.Combine(directory, "session-preferences.json");
        if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
            throw new InvalidDataException("Session preferences exceed their bound.");
        try
        {
            choices = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Choice>>(
                File.ReadAllBytes(path), Protocol.ControlProtocol.Json) ?? throw new JsonException() : new(StringComparer.Ordinal);
            if (choices.Count > 1024 || choices.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 160 || p.Value is null))
                throw new JsonException();
        }
        catch (JsonException e) { throw new InvalidDataException("Session preferences are invalid and were preserved.", e); }
    }
    public BrokerSession Apply(BrokerSession session)
    {
        lock (gate)
        {
            if (!choices.TryGetValue(session.Address.SessionId, out var choice)) return session;
            return session with { IsPrivate = choice.IsPrivate,
                Activity = choice.IsMuted ? SessionActivity.Interrupted : session.Activity };
        }
    }
    public bool IsMuted(string sessionId) { lock (gate) return choices.GetValueOrDefault(sessionId)?.IsMuted == true; }
    // At the bound an entry for a session the broker no longer observes makes room (A-broker-04). Every native session ID is
    // per conversation, so each choice on a chat that later ended stayed forever; at 1,024 every new choice failed
    // preference_limit, and the stale ones could not be cleared because their sessions were gone. An observed session's
    // choice is never evicted, so the limit still refuses when every entry belongs to a present session. Not a last-seen
    // time per entry: the file's strict reader (UnmappedMemberHandling.Disallow) would make an older worker refuse a file
    // that carried one, so a rollback would lose every choice.
    public void Set(string sessionId, bool? isPrivate, bool? isMuted, Func<string, bool>? isObserved = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 160 || sessionId.Any(char.IsControl))
            throw new BrokerException("invalid_session", "Invalid session preference target.");
        lock (gate)
        {
            var copy = new Dictionary<string, Choice>(choices, StringComparer.Ordinal);
            var old = copy.GetValueOrDefault(sessionId) ?? new(false, false);
            var updated = new Choice(isPrivate ?? old.IsPrivate, isMuted ?? old.IsMuted);
            if (updated == old) return;
            if (!updated.IsPrivate && !updated.IsMuted) copy.Remove(sessionId); else copy[sessionId] = updated;
            if (copy.Count > 1024 && isObserved is not null &&
                copy.Keys.FirstOrDefault(key => key != sessionId && !isObserved(key)) is { } stale) copy.Remove(stale);
            if (copy.Count > 1024) throw new BrokerException("preference_limit", "Session preference limit reached.");
            if (!AtomicFile.TryWriteAllText(path, JsonSerializer.Serialize(copy, Protocol.ControlProtocol.Json), null))
                throw new IOException("Session preferences could not be persisted.");
            choices = copy;
        }
    }
}
