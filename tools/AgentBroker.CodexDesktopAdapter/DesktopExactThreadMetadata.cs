using System.Text.Json.Nodes;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>Bounded observations only. A status announcement is not authenticated client identity or ownership.</summary>
public sealed record DesktopExactThreadMetadataResult(
    bool RequestSent, int ReceivedFrameCount, bool ObservationWindowElapsed, bool FrameLimitReached,
    int ExactTargetAdvertisementCount, int LocalHostAdvertisementCount, int OtherHostAdvertisementCount,
    int ExactTargetSourceCount, int ConnectedStatusSeenForTargetSourceCount, int DisconnectedStatusSeenForTargetSourceCount,
    bool SourceLimitReached, int BroadcastFrameCount, int IncomingDiscoveryRequestCount,
    int EligibleForeignSourceCount, int EligibleForeignBroadcastCount,
    int ConnectedStatusEventCount, int DisconnectedStatusEventCount,
    int AnyConversationLocalHostAdvertisementCount, int AnyConversationOtherHostAdvertisementCount,
    int UnsupportedEligibleVersionCount, int MalformedEligibleBroadcastCount, int UnrecognizedEligibleBroadcastCount,
    bool ForeignSourceLimitReached)
{
    public bool ExactTargetAdvertised => ExactTargetAdvertisementCount > 0;
    public bool LocalHostAdvertisementSeen => LocalHostAdvertisementCount > 0;
    public bool SourceConnectedStatusSeen => ConnectedStatusSeenForTargetSourceCount > 0;
    public bool SourceIdentityAuthenticated => false;
}

public static class DesktopExactThreadMetadata
{
    public static async Task<DesktopExactThreadMetadataResult> ObserveAsync(Guid authorizedThreadId,
        CancellationToken token = default)
    {
        if (authorizedThreadId == Guid.Empty) throw new DesktopAdapterException("invalid_thread_id");
        await using var adapter = await DesktopAdapter.ConnectAsync(token: token);
        return await adapter.CollectExactThreadMetadataAsync(authorizedThreadId, TimeSpan.FromSeconds(2), token);
    }
}

internal sealed class DesktopExactThreadMetadataIndex(Guid target, Guid self)
{
    internal const int MaximumSources = 16;
    internal const int MaximumForeignSources = 256;
    private readonly HashSet<Guid> sources = [];
    private readonly HashSet<Guid> foreignSources = [];
    // At most one status identity per incoming frame in the bounded 256-frame reader.
    private readonly Dictionary<Guid, int> statuses = [];
    private int advertisements, localAdvertisements, otherAdvertisements;
    private int broadcasts, foreignBroadcasts, connectedEvents, disconnectedEvents, anyLocalAdvertisements, anyOtherAdvertisements;
    private int unsupportedVersions, malformedBroadcasts, unrecognizedBroadcasts;
    private bool sourceLimitReached, foreignSourceLimitReached;

    internal void Capture(JsonObject frame)
    {
        if (DesktopFrames.Text(frame, "type") != "broadcast") return;
        broadcasts++;
        if (!Guid.TryParseExact(DesktopFrames.Text(frame, "sourceClientId"), "D", out var source)
            || source == Guid.Empty || source == self) return;
        if (frame["targetClientIds"] is { } targetsNode)
        {
            if (targetsNode is not JsonArray targets || !targets.Any(value => value is JsonValue item
                && item.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id) && id == self)) return;
        }
        // Eligibility means a valid nonself source addressed to us (or untargeted), not authenticated identity.
        foreignBroadcasts++;
        if (foreignSources.Count < MaximumForeignSources || foreignSources.Contains(source)) foreignSources.Add(source);
        else foreignSourceLimitReached = true;
        if (frame["version"] is not JsonValue version || !version.TryGetValue<int>(out int revision))
        { malformedBroadcasts++; return; }
        string? method = DesktopFrames.Text(frame, "method");
        var parameters = DesktopFrames.Object(frame, "params");
        if (method == "client-status-changed")
        {
            if (revision != 0) { unsupportedVersions++; return; }
            if (!Guid.TryParseExact(DesktopFrames.Text(parameters, "clientId"), "D", out var declared) || declared != source)
            { malformedBroadcasts++; return; }
            int status = DesktopFrames.Text(parameters, "status") switch { "connected" => 1, "disconnected" => 2, _ => 0 };
            if (status == 0) { malformedBroadcasts++; return; }
            if (status == 1) connectedEvents++; else disconnectedEvents++;
            if (statuses.ContainsKey(source) || statuses.Count < 256)
                statuses[source] = statuses.GetValueOrDefault(source) | status;
            return;
        }
        if (method != "thread-stream-following-changed")
        { if (string.IsNullOrEmpty(method)) malformedBroadcasts++; else unrecognizedBroadcasts++; return; }
        if (revision != 1) { unsupportedVersions++; return; }
        if (!Guid.TryParseExact(DesktopFrames.Text(parameters, "conversationId"), "D", out var thread) || thread == Guid.Empty
            || parameters?["following"] is not JsonValue following || !following.TryGetValue<bool>(out bool isFollowing))
        { malformedBroadcasts++; return; }
        if (!isFollowing) return;
        string? host = DesktopFrames.Text(parameters, "hostId");
        if (string.IsNullOrEmpty(host)) { malformedBroadcasts++; return; }
        if (host == "local") anyLocalAdvertisements++; else anyOtherAdvertisements++;
        if (thread != target) return;
        advertisements++;
        if (host == "local") localAdvertisements++; else otherAdvertisements++;
        if (sources.Count < MaximumSources || sources.Contains(source)) sources.Add(source);
        else sourceLimitReached = true;
    }

    internal DesktopExactThreadMetadataResult Snapshot(bool requestSent, int frames, bool elapsed, bool frameLimit, int discoveryRequests = 0) =>
        new(requestSent, frames, elapsed, frameLimit, advertisements, localAdvertisements, otherAdvertisements, sources.Count,
            sources.Count(id => (statuses.GetValueOrDefault(id) & 1) != 0),
            sources.Count(id => (statuses.GetValueOrDefault(id) & 2) != 0), sourceLimitReached, broadcasts, discoveryRequests,
            foreignSources.Count, foreignBroadcasts, connectedEvents, disconnectedEvents, anyLocalAdvertisements, anyOtherAdvertisements,
            unsupportedVersions, malformedBroadcasts, unrecognizedBroadcasts, foreignSourceLimitReached);
}

public sealed partial class DesktopAdapter
{
    // Dedicated short-lived diagnostic connection only. Timeout can consume part
    // of a frame, so the connection is closed on every exit and cannot be reused.
    internal async Task<DesktopExactThreadMetadataResult> CollectExactThreadMetadataAsync(Guid target,
        TimeSpan window, CancellationToken token)
    {
        if (target == Guid.Empty) throw new DesktopAdapterException("invalid_thread_id");
        if (window <= TimeSpan.Zero || window > TimeSpan.FromSeconds(2))
            throw new DesktopAdapterException("invalid_observation_window");
        await gate.WaitAsync(token);
        var index = new DesktopExactThreadMetadataIndex(target, clientId);
        bool requestSent = false, elapsed = false;
        int frames = 0, discoveryRequests = 0;
        try
        {
            EnsureOpen();
            connection.VerifyStillSameOwner();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(window);
            try
            {
                await DesktopFrames.WriteAsync(connection.Stream, new()
                {
                    ["type"] = "broadcast", ["method"] = "thread-stream-following-status-requested", ["version"] = 1,
                    ["sourceClientId"] = clientId.ToString("D"),
                    ["params"] = new JsonObject { ["hostId"] = "local", ["conversationId"] = target.ToString("D") }
                }, deadline.Token);
                requestSent = true;
                while (frames < 256)
                {
                    connection.VerifyStillSameOwner();
                    var frame = await DesktopFrames.ReadAsync(connection.Stream, deadline.Token);
                    frames++;
                    switch (DesktopFrames.Text(frame, "type"))
                    {
                        case "broadcast": index.Capture(frame); break;
                        case "client-discovery-request":
                            discoveryRequests++;
                            await DesktopFrames.WriteAsync(connection.Stream, new()
                            {
                                ["type"] = "client-discovery-response", ["requestId"] = DesktopFrames.Id(frame, "requestId").ToString("D"),
                                ["response"] = new JsonObject { ["canHandle"] = false }
                            }, deadline.Token);
                            break;
                        default: throw new DesktopAdapterException("unexpected_exact_metadata_frame");
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { elapsed = true; }
            connection.VerifyStillSameOwner();
            return index.Snapshot(requestSent, frames, elapsed, frames == 256, discoveryRequests);
        }
        finally
        {
            protocolClosed = true;
            try { await connection.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }
}
