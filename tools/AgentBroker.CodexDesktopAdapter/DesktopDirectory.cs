using System.Text.Json.Nodes;

namespace AgentBroker.CodexDesktopAdapter;

public enum DesktopSessionActivity { Unknown, Idle, Busy, ApprovalRequired, Interrupted }

/// <summary>Observed open-view metadata, not proof of a live owner or permission to wake it.</summary>
public sealed record DesktopDirectorySession(Guid ConversationId, DesktopSessionActivity Activity,
    DateTimeOffset LastSeenAt, DateTimeOffset? ActivityObservedAt)
{
    public string DisplayLabel => "Codex " + ConversationId.ToString("D")[..8];
}

public static class DesktopDirectory
{
    /// <summary>
    /// Connects a read-only observer, receives the existing GUI's open-view announcements and closes it.
    /// No following subscription, thread read/list, resume, model input or history request is sent.
    /// Each returned candidate still requires exact owner discovery on the delivery adapter.
    /// </summary>
    public static async Task<IReadOnlyList<DesktopDirectorySession>> ObserveOpenSessionsAsync(
        TimeSpan? observationWindow = null, CancellationToken token = default)
    {
        TimeSpan window = observationWindow ?? TimeSpan.FromSeconds(1);
        ValidateWindow(window);
        await using var adapter = await DesktopAdapter.ConnectAsync(token: token);
        return await adapter.CollectDirectoryCandidatesAsync(window, token);
    }

    internal static async Task<IReadOnlyList<DesktopDirectorySession>> ObserveForTestAsync(
        IDesktopConnection connection, TimeSpan window, CancellationToken token = default)
    {
        ValidateWindow(window);
        await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new(), token);
        return await adapter.CollectDirectoryCandidatesAsync(window, token);
    }

    private static void ValidateWindow(TimeSpan window)
    {
        if (window < TimeSpan.FromMilliseconds(50) || window > TimeSpan.FromSeconds(5))
            throw new DesktopAdapterException("invalid_observation_window");
    }
}

internal sealed class DesktopDirectoryIndex(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, Entry> entries = [];
    internal const int MaximumSessions = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private sealed class Entry
    {
        internal HashSet<Guid> Sources { get; } = [];
        internal DateTimeOffset SeenAt;
        internal DateTimeOffset? ActivityAt;
        internal DesktopSessionActivity Activity;
    }

    internal void Capture(JsonObject frame, Guid localClientId)
    {
        Expire();
        if (DesktopFrames.Text(frame, "type") != "broadcast") return;
        if (frame["targetClientIds"] is JsonArray targets && localClientId != Guid.Empty
            && !targets.Any(t => t is JsonValue value && value.TryGetValue<string>(out var target)
                && Guid.TryParseExact(target, "D", out var id) && id == localClientId)) return;
        if (!Guid.TryParseExact(DesktopFrames.Text(frame, "sourceClientId"), "D", out Guid source)
            || source == Guid.Empty || source == localClientId) return;
        string? method = DesktopFrames.Text(frame, "method");
        var parameters = DesktopFrames.Object(frame, "params");
        if (method == "client-status-changed")
        {
            if (DesktopFrames.Text(parameters, "status") == "disconnected") RemoveSource(source);
            return;
        }
        if (DesktopFrames.Text(parameters, "hostId") != "local"
            || !Guid.TryParseExact(DesktopFrames.Text(parameters, "conversationId"), "D", out Guid thread)
            || thread == Guid.Empty || frame["version"] is not JsonValue version || !version.TryGetValue<int>(out int revision)) return;
        bool following = method == "thread-stream-following-changed" && revision == 1;
        bool ownerAnnouncement = method == "thread-stream-following-status-requested" && revision == 1;
        bool state = method == "thread-stream-state-changed" && revision == 11;
        if (!following && !ownerAnnouncement && !state) return;
        if (following && !DesktopFrames.IsTrue(parameters, "following"))
        {
            if (parameters?["following"] is JsonValue flag && flag.TryGetValue<bool>(out bool value) && !value)
                RemoveSource(source, thread);
            return;
        }
        if (!entries.TryGetValue(thread, out var entry))
        {
            if (entries.Count >= MaximumSessions)
                entries.Remove(entries.OrderBy(pair => pair.Value.SeenAt).First().Key);
            entries.Add(thread, entry = new());
        }
        if (entry.Sources.Count < 16) entry.Sources.Add(source);
        entry.SeenAt = clock.GetUtcNow();
        if (!state) return; // Following/ownership proves no activity state.
        var change = DesktopFrames.Object(parameters, "change");
        JsonObject? status = null;
        if (DesktopFrames.Text(change, "type") == "snapshot")
            status = DesktopFrames.Object(DesktopFrames.Object(change, "conversationState"), "threadRuntimeStatus");
        else if (DesktopFrames.Text(change, "type") == "patches" && change?["patches"] is JsonArray patches)
        {
            foreach (var patch in patches.OfType<JsonObject>())
            {
                if (patch["path"] is JsonArray path && path.Count == 1
                    && path[0] is JsonValue key && key.TryGetValue<string>(out var field) && field == "threadRuntimeStatus")
                    status = DesktopFrames.Object(patch, "value");
            }
        }
        if (status is null) return;
        string? type = DesktopFrames.Text(status, "type");
        entry.Activity = type switch
        {
            "idle" => DesktopSessionActivity.Idle,
            "active" when status["activeFlags"] is JsonArray flags && flags.All(f => f is JsonValue v && v.TryGetValue<string>(out _))
                => flags.Any(f => (string?)f is "waitingOnApproval" or "waitingOnUserInput")
                    ? DesktopSessionActivity.ApprovalRequired : DesktopSessionActivity.Busy,
            _ => DesktopSessionActivity.Unknown
        };
        // Interrupted is intentionally not inferred from idle, following or missing metadata.
        entry.ActivityAt = clock.GetUtcNow();
    }

    internal IReadOnlyList<DesktopDirectorySession> Snapshot()
    {
        Expire();
        return entries.Select(pair => new DesktopDirectorySession(pair.Key,
            pair.Value.ActivityAt is { } observed && clock.GetUtcNow() - observed < Lifetime
                ? pair.Value.Activity : DesktopSessionActivity.Unknown,
            pair.Value.SeenAt, pair.Value.ActivityAt)).OrderBy(row => row.ConversationId).ToArray();
    }

    private void Expire()
    {
        foreach (Guid id in entries.Where(pair => clock.GetUtcNow() - pair.Value.SeenAt >= Lifetime).Select(pair => pair.Key).ToArray())
            entries.Remove(id);
    }

    private void RemoveSource(Guid source, Guid? thread = null)
    {
        foreach (var pair in entries.Where(pair => thread is null || pair.Key == thread).ToArray())
            if (pair.Value.Sources.Remove(source) && pair.Value.Sources.Count == 0) entries.Remove(pair.Key);
    }
}
