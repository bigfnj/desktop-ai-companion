using AgentBroker.CodexDesktopAdapter;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ExactThreadMetadataChecks
{
    internal static async Task RunAsync(Action<bool, string> assertion)
    {
        void Check(bool value, string message) => assertion(value, "ExactThreadMetadataChecks.cs: " + message);
        Guid target = Guid.NewGuid(), self = Guid.NewGuid(), source = Guid.NewGuid(), unrelated = Guid.NewGuid();
        JsonObject Advertisement(Guid thread, string host = "local", int version = 1, Guid? sender = null) => new()
        {
            ["type"] = "broadcast", ["method"] = "thread-stream-following-changed", ["version"] = version,
            ["sourceClientId"] = (sender ?? source).ToString("D"),
            ["params"] = new JsonObject { ["conversationId"] = thread.ToString("D"), ["hostId"] = host, ["following"] = true,
                ["title"] = "PRIVATE_SYNTHETIC_TITLE", ["cwd"] = "PRIVATE_SYNTHETIC_CWD", ["history"] = "PRIVATE_SYNTHETIC_HISTORY" }
        };
        JsonObject Status(Guid sender, string status, Guid? declared = null, int version = 0) => new()
        {
            ["type"] = "broadcast", ["method"] = "client-status-changed", ["version"] = version,
            ["sourceClientId"] = sender.ToString("D"),
            ["params"] = new JsonObject { ["clientId"] = (declared ?? sender).ToString("D"), ["status"] = status }
        };
        var index = new DesktopExactThreadMetadataIndex(target, self);
        index.Capture(Advertisement(unrelated));
        index.Capture(Advertisement(target, version: 2));
        index.Capture(Advertisement(target, sender: self));
        var foreignTarget = Advertisement(target); foreignTarget["targetClientIds"] = new JsonArray(unrelated.ToString("D")); index.Capture(foreignTarget);
        var wrongMethod = Advertisement(target); wrongMethod["method"] = "thread-stream-following-status-requested"; index.Capture(wrongMethod);
        var notFollowing = Advertisement(target); notFollowing["params"]!["following"] = false; index.Capture(notFollowing);
        Check(!index.Snapshot(true, 6, false, false).ExactTargetAdvertised, "unrelated UUID, wrong version, self echo, foreign target, requests and false-following are not advertisements");
        index.Capture(Advertisement(target));
        index.Capture(Advertisement(target, "unretained-remote-host"));
        index.Capture(Status(unrelated, "connected"));
        index.Capture(Status(source, "connected", declared: unrelated));
        index.Capture(Status(source, "connected", version: 1));
        var beforeStatus = index.Snapshot(true, 11, false, false);
        Check(beforeStatus.ExactTargetAdvertisementCount == 2 && beforeStatus.LocalHostAdvertisementCount == 1
            && beforeStatus.OtherHostAdvertisementCount == 1, "exact target is counted separately for local and other host without retaining the host text");
        Check(beforeStatus.ExactTargetSourceCount == 1 && !beforeStatus.SourceConnectedStatusSeen,
            "broadcast presence, unrelated status, mismatching client identity and wrong status version do not invent connectivity");
        index.Capture(Status(source, "connected"));
        index.Capture(Status(source, "disconnected"));
        var summary = index.Snapshot(true, 13, true, false);
        Check(summary.ConnectedStatusSeenForTargetSourceCount == 1 && summary.DisconnectedStatusSeenForTargetSourceCount == 1,
            "connection fields report observed status announcements for the matching target source only");
        Check(summary.AnyConversationLocalHostAdvertisementCount == 2 && summary.AnyConversationOtherHostAdvertisementCount == 1
            && summary.ExactTargetAdvertisementCount == 2, "other conversations contribute broad counters without becoming exact-target advertisements");
        Check(summary.EligibleForeignSourceCount == 2 && summary.EligibleForeignBroadcastCount == 11 && summary.BroadcastFrameCount == 13,
            "eligible foreign-source observations remain separate from self echoes, foreign recipients and all received broadcasts");
        Check(summary.ConnectedStatusEventCount == 2 && summary.DisconnectedStatusEventCount == 1,
            "unrelated but valid source status events are observable without claiming target connectivity");
        Check(summary.UnsupportedEligibleVersionCount == 2 && summary.MalformedEligibleBroadcastCount == 1
            && summary.UnrecognizedEligibleBroadcastCount == 1, "known unsupported versions, malformed status and unrecognized methods are separate fixed counts");
        Check(!summary.SourceIdentityAuthenticated, "status observations never claim authenticated client identity");
        string serialized = JsonSerializer.Serialize(summary);
        Check(new[] { target.ToString("D"), unrelated.ToString("D"), source.ToString("D"), self.ToString("D"),
                "PRIVATE_SYNTHETIC", "unretained-remote-host" }.All(secret => !serialized.Contains(secret, StringComparison.Ordinal)),
            "serialized result never exports thread/source UUIDs, title, cwd, history or nonlocal host text");
        Check(JsonNode.Parse(serialized)!.AsObject().All(property => property.Value is JsonValue value
            && (value.TryGetValue<bool>(out _) || value.TryGetValue<int>(out _))), "result schema contains only typed booleans and counts");
        var targetedIndex = new DesktopExactThreadMetadataIndex(target, self);
        var addressed = Advertisement(target); addressed["targetClientIds"] = new JsonArray(self.ToString("D")); targetedIndex.Capture(addressed);
        Check(targetedIndex.Snapshot(true, 1, false, false).ExactTargetAdvertisementCount == 1,
            "metadata specifically addressed to this initialized diagnostic client is accepted");
        var noTarget = new DesktopExactThreadMetadataIndex(target, self);
        noTarget.Capture(Advertisement(unrelated));
        noTarget.Capture(Advertisement(Guid.NewGuid(), "PRIVATE_SYNTHETIC_REMOTE", sender: unrelated));
        noTarget.Capture(Status(source, "connected")); noTarget.Capture(Status(source, "connected"));
        noTarget.Capture(Status(unrelated, "disconnected"));
        var passive = noTarget.Snapshot(true, 5, false, false);
        Check(!passive.ExactTargetAdvertised && passive.AnyConversationLocalHostAdvertisementCount == 1
            && passive.AnyConversationOtherHostAdvertisementCount == 1 && passive.EligibleForeignSourceCount == 2,
            "broad traffic remains visible when no exact authorized target advertises");
        Check(passive.ConnectedStatusEventCount == 2 && passive.DisconnectedStatusEventCount == 1
            && passive.ConnectedStatusSeenForTargetSourceCount == 0 && !passive.SourceIdentityAuthenticated,
            "status-event counts include repeated announcements without authenticating a source or inventing exact-target linkage");
        var malformed = new DesktopExactThreadMetadataIndex(target, self);
        malformed.Capture(Status(source, "connected", declared: unrelated));
        var absentVersion = Advertisement(unrelated); absentVersion.Remove("version"); malformed.Capture(absentVersion);
        malformed.Capture(Advertisement(Guid.Empty));
        var unknown = Advertisement(unrelated); unknown["method"] = "PRIVATE_SYNTHETIC_METHOD"; malformed.Capture(unknown);
        malformed.Capture(Advertisement(unrelated, version: 7));
        var malformedResult = malformed.Snapshot(true, 5, false, false);
        Check(malformedResult.MalformedEligibleBroadcastCount == 3 && malformedResult.UnrecognizedEligibleBroadcastCount == 1
            && malformedResult.UnsupportedEligibleVersionCount == 1 && malformedResult.AnyConversationLocalHostAdvertisementCount == 0,
            "malformed broad advertisements cannot create positive following or connectivity counts");
        Check(!JsonSerializer.Serialize(malformedResult).Contains("PRIVATE_SYNTHETIC", StringComparison.Ordinal)
            && !JsonSerializer.Serialize(passive).Contains("PRIVATE_SYNTHETIC", StringComparison.Ordinal),
            "new broad diagnostics never export unrecognized method text or another conversation's host text");
        var broadBound = new DesktopExactThreadMetadataIndex(target, self);
        for (int i = 0; i < 300; i++) broadBound.Capture(Status(Guid.NewGuid(), "connected"));
        var broadLimited = broadBound.Snapshot(true, 300, false, false);
        Check(broadLimited.EligibleForeignSourceCount == 256 && broadLimited.ForeignSourceLimitReached
            && broadLimited.ConnectedStatusEventCount == 300 && !broadLimited.ExactTargetAdvertised,
            "broad distinct-source retention is bounded independently of exact-target sources");
        for (int i = 0; i < 30; i++) index.Capture(Advertisement(target, sender: Guid.NewGuid()));
        Check(index.Snapshot(true, 43, false, false).SourceLimitReached
            && index.Snapshot(true, 43, false, false).ExactTargetSourceCount == 16, "target source retention is bounded");

        await using (var connection = new FakeConnection { DirectoryThread = target })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            connection.Inject(Status(connection.OwnerId, "connected"));
            connection.Inject(new() { ["type"] = "client-discovery-request", ["requestId"] = Guid.NewGuid().ToString("D") });
            var result = await adapter.CollectExactThreadMetadataAsync(target, TimeSpan.FromMilliseconds(60), default);
            var request = connection.Requests.Single(frame => DesktopFrames.Text(frame, "method") == "thread-stream-following-status-requested");
            Check(result.RequestSent && result.ExactTargetAdvertisementCount == 2 && result.LocalHostAdvertisementSeen,
                "initial native advertisement and one status reply are counted from actual frames");
            Check((int?)request["version"] == 1 && (string?)request["params"]?["hostId"] == "local"
                && (string?)request["params"]?["conversationId"] == target.ToString("D"), "one exact-known-ID request retains the pinned metadata version and local host");
            Check(request.Count == 5 && request["params"]!.AsObject().Count == 2
                && connection.Requests.Count == 2 && connection.DiscoveryCount == 0 && connection.SubmissionCount == 0,
                "only initialization and a metadata request are sent, without subscription, history, discovery, resume or model input");
            Check(connection.DeclinedDiscovery == 1, "incoming discovery requests receive only canHandle:false, without claiming an owner role");
            Check(result.SourceConnectedStatusSeen && !result.SourceIdentityAuthenticated, "matching source status remains an observation rather than owner proof");
            Check(result.IncomingDiscoveryRequestCount == 1 && result.BroadcastFrameCount > 0
                && result.ReceivedFrameCount == result.BroadcastFrameCount + result.IncomingDiscoveryRequestCount,
                "the real framed reader distinguishes incoming discovery traffic from broadcasts");
            Check(result.AnyConversationLocalHostAdvertisementCount == result.ExactTargetAdvertisementCount
                && result.ConnectedStatusEventCount == 1, "actual frame traffic contributes broad counts while preserving exact metadata semantics");
            Check(result.ObservationWindowElapsed && connection.Disposed, "window cancellation closes the dedicated diagnostic transport");
            bool closed = false;
            try { await adapter.DiscoverAsync(target); } catch (DesktopAdapterException error) { closed = error.Code == "connection_closed"; }
            Check(closed, "a possibly partial diagnostic stream is never reused for owner discovery or delivery");
        }
        await using (var connection = new PartialConnection(target))
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            var result = await adapter.CollectExactThreadMetadataAsync(target, TimeSpan.FromMilliseconds(40), default);
            Check(result.ObservationWindowElapsed && !result.ExactTargetAdvertised && result.ReceivedFrameCount == 0,
                "timeout during a partial late frame cannot fabricate an advertisement");
            Check(connection.Disposed && connection.StreamClosed, "partial-frame timeout disposes both the connection and wrapper stream");
            Check(result.BroadcastFrameCount == 0 && result.IncomingDiscoveryRequestCount == 0 && result.EligibleForeignSourceCount == 0,
                "a partial frame cannot fabricate broad traffic or source observations");
        }
        await using (var connection = new FakeConnection { DirectoryThread = target })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            for (int i = 0; i < 300; i++) connection.Inject(Advertisement(unrelated));
            var result = await adapter.CollectExactThreadMetadataAsync(target, TimeSpan.FromSeconds(2), default);
            Check(result.FrameLimitReached && result.ReceivedFrameCount == 256 && !result.ObservationWindowElapsed,
                "incoming traffic stops at the hard 256-frame bound");
            Check(connection.Disposed && connection.Requests.Count == 2, "frame limit closes transport without follow-up requests");
            Check(result.BroadcastFrameCount == 256 && result.AnyConversationLocalHostAdvertisementCount == 256,
                "broad traffic counts obey the same hard frame budget and never trigger extra discovery");
        }
        await using (var connection = new PartialConnection(target))
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            using var outer = new CancellationTokenSource();
            var pending = adapter.CollectExactThreadMetadataAsync(target, TimeSpan.FromSeconds(2), outer.Token);
            await connection.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2)); outer.Cancel();
            bool cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && connection.Disposed, "external cancellation propagates while closing the exact diagnostic connection");
        }
        await using (var connection = new FakeConnection { DirectoryThread = target, IdentityChanged = false })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            connection.IdentityChanged = true;
            bool rejected = false;
            try { await adapter.CollectExactThreadMetadataAsync(target, TimeSpan.FromMilliseconds(40), default); }
            catch (DesktopAdapterException error) { rejected = error.Code == "server_incarnation_changed"; }
            Check(rejected && connection.Disposed && connection.Requests.Count == 1,
                "changed router identity closes before the metadata request");
        }
        bool emptyRejected = false;
        try { await DesktopExactThreadMetadata.ObserveAsync(Guid.Empty); }
        catch (DesktopAdapterException error) { emptyRejected = error.Code == "invalid_thread_id"; }
        Check(emptyRejected, "an empty target is rejected before attempting a real native connection");
        await using (var connection = new FakeConnection { DirectoryThread = target })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            bool invalidWindow = false;
            try { await adapter.CollectExactThreadMetadataAsync(target, TimeSpan.FromSeconds(3), default); }
            catch (DesktopAdapterException error) { invalidWindow = error.Code == "invalid_observation_window"; }
            Check(invalidWindow && connection.Requests.Count == 1, "a larger observation window is rejected before any status request");
        }
    }

    private sealed class PartialConnection : IDesktopConnection
    {
        private readonly FakeConnection inner;
        private readonly PartialStream stream;
        internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed => inner.Disposed;
        internal bool StreamClosed => stream.Closed;
        internal PartialConnection(Guid target) { inner = new() { DirectoryThread = target }; stream = new(inner.Stream, Requested); }
        public Stream Stream => stream;
        public OwnerProcessIdentity Identity => inner.Identity;
        public void VerifyStillSameOwner() => inner.VerifyStillSameOwner();
        public ValueTask DisposeAsync() { stream.Dispose(); return inner.DisposeAsync(); }
    }
    private sealed class PartialStream(Stream inner, TaskCompletionSource requested) : Stream
    {
        private bool afterRequest, prefixRead;
        internal bool Closed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!afterRequest) return await inner.ReadAsync(buffer, cancellationToken);
            if (!prefixRead)
            {
                prefixRead = true;
                // Deliver only two bytes of the next length header, then wait beyond the observation deadline.
                buffer.Span[0] = 32; buffer.Span[1] = 0; return 2;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var frame = (JsonObject)JsonNode.Parse(buffer.Span[4..])!;
            if (DesktopFrames.Text(frame, "method") == "thread-stream-following-status-requested")
            { afterRequest = true; requested.TrySetResult(); return ValueTask.CompletedTask; }
            return inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
