using System.Text.Json.Nodes;

namespace AgentBroker.CodexDesktopAdapter;

internal sealed record DesktopReadySmokeMatch(Guid ConversationId, Guid OwnerClientId, DesktopSessionActivity Activity);
internal sealed record DesktopReadySmokeSearch(int CandidateCount, int UnreadableCount, IReadOnlyList<DesktopReadySmokeMatch> Matches,
    IReadOnlyList<ReadySnapshotDiagnostic> Diagnostics);
internal sealed record ReadySnapshotDiagnostic(int TransientTurns, int CanonicalIslands, int CanonicalEntities,
    string LatestStatus, int LatestInputCount, bool PromptExact, bool ReadyAnswerExact, int KnownStateKeys);

/// <summary>
/// Explicit read-only diagnostic for a person's dedicated test conversation. Unlike production directory
/// discovery, it briefly receives a bounded live snapshot, matches one fixed readiness exchange in memory,
/// and discards the entire snapshot. It never opens persisted conversation files or returns other text.
/// </summary>
internal static class DesktopSmokeTargetFinder
{
    internal const string ReadinessPrompt = "Reply READY for the AgentBroker smoke test and use no tools.";
    internal const string ShortReadinessPrompt = "Reply READY and use no tools";

    private static bool IsReadinessPrompt(string? text)
        => text is ReadinessPrompt or ShortReadinessPrompt;

    internal static async Task<DesktopReadySmokeSearch> FindAsync(CancellationToken token = default)
    {
        var candidates = await DesktopDirectory.ObserveOpenSessionsAsync(token: token);
        if (candidates.Count > 32) throw new DesktopAdapterException("smoke_candidate_limit");
        List<DesktopReadySmokeMatch> matches = [];
        List<ReadySnapshotDiagnostic> diagnostics = [];
        int unreadable = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var adapter = await DesktopAdapter.ConnectAsync(token: token);
                var binding = await adapter.DiscoverAsync(candidate.ConversationId, token);
                if (binding is null) continue;
                var match = await adapter.FindReadySmokeSnapshotAsync(binding, token);
                if (adapter.ReadySnapshotDiagnostic is { } diagnostic) diagnostics.Add(diagnostic);
                if (match is not null) matches.Add(match);
            }
            catch (Exception e) when (e is DesktopAdapterException or IOException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                unreadable++;
            }
        }
        return new(candidates.Count, unreadable, matches, diagnostics);
    }

    internal static ReadySnapshotDiagnostic Diagnose(JsonObject state)
    {
        var history = DesktopFrames.Object(state["turnHistory"], "history");
        var transient = state["turns"] as JsonArray;
        var entities = DesktopFrames.Object(history, "entitiesByKey");
        var islands = history?["islands"] as JsonArray;
        var latest = transient?.OfType<JsonObject>().LastOrDefault();
        if (latest is null && islands?.LastOrDefault()?["entries"] is JsonArray entries
            && DesktopFrames.Text(entries.LastOrDefault(), "value") is { } key)
            latest = entities?[key] as JsonObject;
        var input = latest?["params"]?["input"] as JsonArray;
        var answer = (latest?["items"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(item => DesktopFrames.Text(item, "type") == "agentMessage");
        string? status = DesktopFrames.Text(latest, "status");
        string? text = DesktopFrames.Text(answer, "text")?.Trim();
        string[] known = ["turns", "turnHistory", "threadRuntimeStatus", "resumeState", "id"];
        return new(transient?.Count ?? -1, islands?.Count ?? -1, entities?.Count ?? -1,
            status is "completed" or "inProgress" or "interrupted" or "failed" ? status : "unknown",
            input?.Count ?? -1, input?.Count == 1 && IsReadinessPrompt(DesktopFrames.Text(input[0], "text")),
            text is "READY" or "READY for the AgentBroker smoke test" or "READY for the AgentBroker smoke test.", known.Count(state.ContainsKey));
    }

    internal static bool MatchesReadinessExchange(JsonObject state)
    {
        var turns = state["turns"] as JsonArray;
        List<JsonObject> ordered = [];
        if (DesktopFrames.Text(state["turnHistory"], "kind") == "canonical")
        {
            var history = DesktopFrames.Object(state["turnHistory"], "history");
            var entities = DesktopFrames.Object(history, "entitiesByKey");
            if (history?["islands"] is not JsonArray { Count: 1 } islands || entities is null
                || islands[0]?["entries"] is not JsonArray entries || entries.Count > 64) return false;
            foreach (var entry in entries)
            {
                string? key = DesktopFrames.Text(entry, "value");
                if (key is null || entities[key] is not JsonObject turn) return false;
                ordered.Add(turn);
            }
        }
        if (turns is not null)
            foreach (var turn in turns.OfType<JsonObject>())
                if (!ordered.Any(existing => DesktopFrames.Text(existing, "turnId") == DesktopFrames.Text(turn, "turnId"))) ordered.Add(turn);
        if (ordered.Count is < 1 or > 64) return false;
        var latest = ordered[^1];
        if (DesktopFrames.Text(latest, "status") != "completed"
            || latest["params"]?["input"] is not JsonArray { Count: 1 } input
            || DesktopFrames.Text(input[0], "type") != "text"
            || !IsReadinessPrompt(DesktopFrames.Text(input[0], "text"))
            || latest["items"] is not JsonArray items || items.Count > 64) return false;
        string? answer = items.OfType<JsonObject>().LastOrDefault(item => DesktopFrames.Text(item, "type") == "agentMessage") is { } agent
            ? DesktopFrames.Text(agent, "text")?.Trim() : null;
        return answer is "READY" or "READY for the AgentBroker smoke test" or "READY for the AgentBroker smoke test.";
    }
}

public sealed partial class DesktopAdapter
{
    internal ReadySnapshotDiagnostic? ReadySnapshotDiagnostic { get; private set; }
    internal async Task<DesktopReadySmokeMatch?> FindReadySmokeSnapshotAsync(DesktopOwnerBinding binding, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureOpen();
            ValidateBinding(binding);
            connection.VerifyStillSameOwner();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await DesktopFrames.WriteAsync(connection.Stream, new()
            {
                ["type"] = "broadcast", ["method"] = "thread-stream-following-changed", ["version"] = 1,
                ["sourceClientId"] = clientId.ToString("D"),
                ["targetClientIds"] = new JsonArray(binding.OwnerClientId.ToString("D")),
                ["params"] = new JsonObject
                {
                    ["hostId"] = "local", ["conversationId"] = binding.ConversationId.ToString("D"), ["following"] = true
                }
            }, deadline.Token);
            for (int ignored = 0; ignored < 128; ignored++)
            {
                var frame = await DesktopFrames.ReadAsync(connection.Stream, deadline.Token);
                if (DesktopFrames.Text(frame, "type") == "client-discovery-request")
                {
                    await DesktopFrames.WriteAsync(connection.Stream, new()
                    {
                        ["type"] = "client-discovery-response", ["requestId"] = DesktopFrames.Id(frame, "requestId").ToString("D"),
                        ["response"] = new JsonObject { ["canHandle"] = false }
                    }, deadline.Token);
                    continue;
                }
                if (DesktopFrames.Text(frame, "type") != "broadcast") throw new DesktopAdapterException("unexpected_smoke_observer_frame");
                if (DesktopFrames.Text(frame, "method") != "thread-stream-state-changed"
                    || frame["version"] is not JsonValue version || !version.TryGetValue<int>(out int v) || v != 11
                    || DesktopFrames.Text(frame, "sourceClientId") != binding.OwnerClientId.ToString("D")
                    || DesktopFrames.Text(frame["params"], "hostId") != "local"
                    || DesktopFrames.Text(frame["params"], "conversationId") != binding.ConversationId.ToString("D")) continue;
                var change = DesktopFrames.Object(frame["params"], "change");
                if (DesktopFrames.Text(change, "type") != "snapshot"
                    || change?["conversationState"] is not JsonObject state) continue;
                connection.VerifyStillSameOwner();
                ReadySnapshotDiagnostic = DesktopSmokeTargetFinder.Diagnose(state);
                if (!DesktopSmokeTargetFinder.MatchesReadinessExchange(state)) return null;
                string? status = DesktopFrames.Text(state["threadRuntimeStatus"], "type");
                var activity = status == "idle" ? DesktopSessionActivity.Idle
                    : status == "active" ? DesktopSessionActivity.Busy : DesktopSessionActivity.Unknown;
                return new(binding.ConversationId, binding.OwnerClientId, activity);
            }
            throw new DesktopAdapterException("smoke_observer_frame_limit");
        }
        finally
        {
            // Closing the dedicated connection removes its follower registration, even after a partial frame.
            protocolClosed = true;
            await connection.DisposeAsync();
            gate.Release();
        }
    }
}
