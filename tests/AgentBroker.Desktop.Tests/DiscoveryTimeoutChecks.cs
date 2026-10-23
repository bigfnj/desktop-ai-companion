using AgentBroker.CodexDesktopAdapter;
using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class DiscoveryTimeoutChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string? scenario = null)
    {
        var cases = new (string Name, Func<Action<bool, string>, Task> Run)[]
        {
            ("default-delay", DefaultDelayAsync), ("method-defaults", MethodDefaultsAsync),
            ("explicit-deadline", ExplicitDeadlineAsync), ("outer-cancellation", OuterCancellationAsync),
            ("missing-owner", MissingOwnerAsync)
        };
        if (scenario is not null && !cases.Any(item => item.Name == scenario))
            throw new ArgumentException("Unknown discovery timeout test case.", nameof(scenario));
        foreach (var item in cases.Where(item => scenario is null || item.Name == scenario))
            await item.Run((condition, message) => check(condition, "DiscoveryTimeoutChecks.cs: " + message));
    }

    private static async Task DefaultDelayAsync(Action<bool, string> check)
    {
        await using var connection = new DelayedConnection { DiscoveryDelay = TimeSpan.FromMilliseconds(5200) };
        await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new());
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Guid target = Guid.NewGuid();
        var elapsed = Stopwatch.StartNew();
        DesktopOwnerBinding? binding;
        try { binding = await adapter.DiscoverAsync(target, outer.Token); }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException("DiscoveryTimeoutChecks.cs: default discovery must accept an owner reply after the legacy five-second limit.", error);
        }
        check(binding?.ConversationId == target && binding.OwnerClientId == connection.Inner.OwnerId,
            "delayed discovery preserves the exact thread and owner binding");
        check(elapsed.Elapsed >= TimeSpan.FromSeconds(5), "the delayed owner reply actually crosses the legacy deadline");
        check(TimeoutFor(connection, "thread-owner-discovery") == 12000,
            "default discovery advertises twelve seconds to the native router");
        check(connection.Inner.SubmissionCount == 0 && connection.Inner.Requests.Count == 2,
            "delayed read-only discovery sends only initialization and the exact owner query");
    }

    private static async Task MethodDefaultsAsync(Action<bool, string> check)
    {
        check(new DesktopAdapterOptions().Timeout == TimeSpan.FromSeconds(5), "native connection default remains five seconds");
        await using var connection = new DelayedConnection();
        await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new(EnableAuthorizedPlaintextDelivery: true));
        var binding = await adapter.DiscoverAsync(Guid.NewGuid());
        var receipt = await adapter.SubmitAuthorizedPlaintextAsync(binding!, Guid.NewGuid(), "Synthetic transport-only timeout check.");
        check(receipt.Outcome == DesktopSubmissionOutcome.Accepted, "synthetic submission still completes through the existing protocol");
        check(TimeoutFor(connection, "initialize") == 5000, "initialization default remains five seconds");
        check(TimeoutFor(connection, "thread-follower-start-turn") == 5000, "submission default remains five seconds");
        check(connection.Inner.Requests.Where(request => Method(request) == "thread-owner-discovery")
            .All(request => (int?)request["timeoutMs"] == 12000), "initial discovery and pre-submit owner revalidation use the discovery deadline");
        var discovery = connection.Inner.Requests.First(request => Method(request) == "thread-owner-discovery");
        check((int?)discovery["version"] == 1 && (string?)discovery["params"]?["hostId"] == "local",
            "discovery host and pinned protocol version are unchanged");
    }

    private static async Task ExplicitDeadlineAsync(Action<bool, string> check)
    {
        var options = new DesktopAdapterOptions(EnableAuthorizedPlaintextDelivery: true, RequestTimeout: TimeSpan.FromMilliseconds(100));
        await using (var connection = new DelayedConnection { DiscoveryDelay = TimeSpan.FromMilliseconds(400) })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
        {
            var elapsed = Stopwatch.StartNew();
            bool canceled = false;
            try { await adapter.DiscoverAsync(Guid.NewGuid()); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && elapsed.Elapsed < TimeSpan.FromSeconds(2), "an explicit shorter discovery deadline cancels the actual pending read");
            check(TimeoutFor(connection, "thread-owner-discovery") == 100, "explicit discovery deadline also reaches the native envelope");
            check(connection.Inner.Disposed && connection.Inner.SubmissionCount == 0,
                "timed-out owner discovery closes its transport without submitting");
        }
        await using (var connection = new DelayedConnection { SubmissionDelay = TimeSpan.FromMilliseconds(400) })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
        {
            var binding = await adapter.DiscoverAsync(Guid.NewGuid());
            var receipt = await adapter.SubmitAuthorizedPlaintextAsync(binding!, Guid.NewGuid(), "Synthetic transport-only deadline check.");
            check(receipt.Outcome == DesktopSubmissionOutcome.Uncertain && receipt.ErrorCode == "response_timeout_or_cancelled",
                "an explicit submission deadline retains the uncertain no-resend result");
            check(connection.Inner.Requests.All(request => (int?)request["timeoutMs"] == 100),
                "explicit caller timeout overrides initialization, discovery, revalidation and submission");
        }
        await using (var connection = new DelayedConnection())
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options with { RequestTimeout = TimeSpan.FromSeconds(5) }))
        {
            check(await adapter.DiscoverAsync(Guid.NewGuid()) is not null && TimeoutFor(connection, "thread-owner-discovery") == 5000,
                "an explicit five-second caller timeout also overrides the twelve-second default");
        }
    }

    private static async Task OuterCancellationAsync(Action<bool, string> check)
    {
        await using (var connection = new DelayedConnection { DiscoveryDelay = TimeSpan.FromSeconds(30) })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            using var outer = new CancellationTokenSource();
            var pending = adapter.DiscoverAsync(Guid.NewGuid(), outer.Token);
            await connection.DiscoveryRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var elapsed = Stopwatch.StartNew();
            outer.Cancel();
            bool canceled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && elapsed.Elapsed < TimeSpan.FromSeconds(2), "caller cancellation ends a pending twelve-second discovery promptly");
            check(connection.Inner.Disposed && connection.Inner.SubmissionCount == 0,
                "outer cancellation closes the pending transport and never submits");
        }
        await using (var connection = new DelayedConnection())
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
        {
            using var outer = new CancellationTokenSource();
            outer.Cancel();
            bool canceled = false;
            try { await adapter.DiscoverAsync(Guid.NewGuid(), outer.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && connection.Inner.Requests.Count == 1, "an already-canceled caller sends no owner query");
        }
    }

    private static async Task MissingOwnerAsync(Action<bool, string> check)
    {
        await using var connection = new DelayedConnection { DiscoveryDelay = TimeSpan.FromMilliseconds(20) };
        connection.Inner.NoOwner = true;
        await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new());
        check(await adapter.DiscoverAsync(Guid.NewGuid()) is null, "native no-client-found remains an absent owner rather than a fabricated binding");
        check(connection.Inner.Requests.All(request => Method(request) is "initialize" or "thread-owner-discovery")
            && connection.Inner.SubmissionCount == 0, "a negative discovery does not resume, replace or submit to any thread");
    }

    private static string? Method(JsonObject request) => DesktopFrames.Text(request, "method");
    private static int? TimeoutFor(DelayedConnection connection, string method) =>
        (int?)connection.Inner.Requests.First(request => Method(request) == method)["timeoutMs"];

    // Decorate the existing synthetic framed connection. There are no pipes,
    // client processes or timer tasks that survive cancellation/disposal.
    private sealed class DelayedConnection : IDesktopConnection
    {
        internal FakeConnection Inner { get; } = new();
        internal TimeSpan DiscoveryDelay { get; init; }
        internal TimeSpan SubmissionDelay { get; init; }
        internal TaskCompletionSource DiscoveryRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DelayedReadStream stream;
        internal DelayedConnection() { stream = new(Inner.Stream, SelectDelay); }
        private TimeSpan SelectDelay(JsonObject request)
        {
            if (Method(request) == "thread-owner-discovery") { DiscoveryRequested.TrySetResult(); return DiscoveryDelay; }
            return Method(request) == "thread-follower-start-turn" ? SubmissionDelay : TimeSpan.Zero;
        }
        public Stream Stream => stream;
        public OwnerProcessIdentity Identity => Inner.Identity;
        public void VerifyStillSameOwner() => Inner.VerifyStillSameOwner();
        public ValueTask DisposeAsync() { stream.Dispose(); return Inner.DisposeAsync(); }
    }

    private sealed class DelayedReadStream(Stream inner, Func<JsonObject, TimeSpan> selectDelay) : Stream
    {
        private TimeSpan nextDelay;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var delay = nextDelay;
            nextDelay = TimeSpan.Zero;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            nextDelay = selectDelay((JsonObject)JsonNode.Parse(buffer.Span[4..])!);
            return inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
    }
}
