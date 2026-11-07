using AgentBroker.ClaudeAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;
using System.Diagnostics;
using System.Text;

internal static class ClaudeSubmissionBoundaryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CLAUDE_SUBMISSION_BOUNDARY_EXECUTED");
        await GateCancellationAsync(check);
        await TypedResultsAsync(check);
        await LibraryTimeoutsAsync(check);
        await LibraryCancellationsAsync(check);
        await RuntimeCancellationAsync(check);
    }

    private static ClaudeNativeCandidate Candidate(int processId = 12345)
    {
        var pin = EngineProfile.Verified.First();
        return new(new NativeTarget(@"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef", processId,
            @"C:\SyntheticPublic\claude.exe", pin.Sha256, pin.Version, 134000000000000000,
            Guid.NewGuid().ToString("D")), "idle");
    }

    private static BrokerMessage Message(SessionAddress recipient) => new(Guid.NewGuid(), Guid.NewGuid(), null,
        new(recipient.BrokerId, "synthetic-boundary-sender", Guid.NewGuid()), recipient, "Synthetic boundary check.",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private static async Task GateCancellationAsync(Action<bool, string> check)
    {
        var provider = new Provider(Candidate()) { Hold = true };
        await using var adapter = new ClaudeNativeAdapter(provider, enablePeerCredentials: true);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        var first = adapter.SubmitAsync(Message(row.Address), default);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var second = adapter.SubmitAsync(Message(row.Address), cancel.Token);
            check(!second.IsCompleted && provider.Submissions == 1,
                "CLAUDE_GATE_WAIT_WITNESS: the second delivery is waiting behind a held first submission");
            cancel.Cancel();
            AdapterResult result;
            try { result = await second.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException)
            {
                check(false, "CLAUDE_GATE_CANCEL_NOT_SUBMITTED: a canceled gate waiter escaped instead of proving no submission");
                throw;
            }
            check(result is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_submission_wait_cancelled" } && provider.Submissions == 1,
                "CLAUDE_GATE_CANCEL_NOT_SUBMITTED: a canceled gate waiter never enters the provider and remains retryable");
        }
        finally { provider.Hold = false; provider.Release.TrySetResult(); }
        check((await first).Outcome == AdapterAdmission.Submitted &&
            (await adapter.SubmitAsync(Message(row.Address), default)).Outcome == AdapterAdmission.Submitted && provider.Submissions == 2,
            "CLAUDE_GATE_CANCEL_RELEASE: cancellation does not release another caller's gate or prevent a later submission");
    }

    private static async Task TypedResultsAsync(Action<bool, string> check)
    {
        var provider = new Provider(Candidate());
        await using var adapter = new ClaudeNativeAdapter(provider, enablePeerCredentials: true);
        var row = (await adapter.DiscoverAsync(Guid.NewGuid(), default)).Single();
        provider.Result = new(SubmissionStatus.Refused, "native_transport_unavailable");
        check(await adapter.SubmitAsync(Message(row.Address), default) is
            { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_transport_unavailable" },
            "CLAUDE_PREWRITE_TRANSPORT_RETRIES: a typed library pre-user-frame transport failure stays retryable");
        foreach (string reason in new[] { "invalid_credential", "native_inbound_denied", "native_identity_mismatch", "native_publisher_unverified" })
        {
            provider.Result = new(SubmissionStatus.Refused, reason);
            var result = await adapter.SubmitAsync(Message(row.Address), default);
            check(result.Outcome == AdapterAdmission.Refused && result.Reason == reason,
                "CLAUDE_TYPED_REFUSAL: a definite credential, identity or policy refusal stays final and preserves its bounded cause");
        }
        provider.Result = new(SubmissionStatus.Refused, "untrusted-text-with-punctuation");
        check(await adapter.SubmitAsync(Message(row.Address), default) is
            { Outcome: AdapterAdmission.Refused, Reason: "native_admission_refused" },
            "CLAUDE_UNKNOWN_REFUSAL_BOUNDED: an unknown provider refusal never becomes retryable or arbitrary receipt text");
        provider.Failure = new TimeoutException("Synthetic provider timeout after possible submission.");
        AdapterResult timeout;
        try { timeout = await adapter.SubmitAsync(Message(row.Address), default); }
        catch (TimeoutException)
        {
            check(false, "CLAUDE_PROVIDER_TIMEOUT_UNCERTAIN: an arbitrary provider timeout escaped cause classification");
            throw;
        }
        check(timeout is { Outcome: AdapterAdmission.Uncertain, Reason: "native_uncertain_raised_timeout" },
            "CLAUDE_PROVIDER_TIMEOUT_UNCERTAIN: an arbitrary provider timeout carries no proof of non-submission");
        provider.Failure = null;
        provider.Result = new((SubmissionStatus)999, "unknown");
        check((await adapter.SubmitAsync(Message(row.Address), default)).Outcome == AdapterAdmission.Uncertain,
            "CLAUDE_UNKNOWN_SUBMISSION_UNCERTAIN: an unknown native result is not invented as a recipient refusal");
    }

    private static async Task LibraryTimeoutsAsync(Action<bool, string> check)
    {
        var target = Candidate().Target;
        var native = new NativeMessage(Guid.NewGuid(), target.SessionId, "Synthetic timeout boundary.", RecipientMode.Unknown,
            DelegateNativeAdmission: true, Activity: NativeActivity.Idle);
        var credentials = new Credentials();
        SubmissionResult result;
        try { result = await new NativePeerAdapter(new ConnectTimeout(), credentials).SendAsync(target, native, @"C:\SyntheticHome"); }
        catch (TimeoutException)
        {
            check(false, "CLAUDE_CONNECT_TIMEOUT_PREWRITE: a connection-only timeout escaped its proven pre-write boundary");
            throw;
        }
        check(result is { Status: SubmissionStatus.Refused, Reason: "native_transport_unavailable" } && credentials.Reads == 0,
            "CLAUDE_CONNECT_TIMEOUT_PREWRITE: a real native adapter connection timeout proves no user frame or credential read");
        foreach (int write in new[] { 1, 2 })
        {
            var connection = new Connection(target, write);
            result = await new NativePeerAdapter(new Connected(connection), credentials).SendAsync(target, native, @"C:\SyntheticHome");
            check(write == 1 ? result is { Status: SubmissionStatus.Refused, Reason: "native_transport_unavailable" }
                : result is { Status: SubmissionStatus.SubmittedUnconfirmed, Reason: "native_write_uncertain" },
                "CLAUDE_TIMEOUT_WRITE_BOUNDARY: authentication-only timeout is retryable; a started user frame remains uncertain");
            check(connection.Disposed && connection.Writes == write,
                "CLAUDE_TIMEOUT_CONNECTION_DISPOSED: each timeout closes its actual connection at the observed write boundary");
        }
    }

    private static async Task LibraryCancellationsAsync(Action<bool, string> check)
    {
        var target = Candidate().Target;
        var native = new NativeMessage(Guid.NewGuid(), target.SessionId, "Synthetic cancellation boundary.", RecipientMode.Unknown,
            DelegateNativeAdmission: true, Activity: NativeActivity.Idle);
        var credentials = new Credentials();
        var factory = new ConnectCanceled();
        var library = new NativePeerAdapter(factory, credentials);
        async Task<SubmissionResult> SendAsync(Func<Task<SubmissionResult>> send)
        {
            try { return await send(); }
            catch (OperationCanceledException)
            {
                check(false, "CLAUDE_LIBRARY_PREWRITE_CANCEL: cancellation escaped despite no user frame starting");
                throw;
            }
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var result = await SendAsync(() => library.SendAsync(target, native, @"C:\SyntheticHome", cancellation: canceled.Token));
        check(result is { Status: SubmissionStatus.Refused, Reason: "native_transport_unavailable" } && factory.Calls == 0 && credentials.Reads == 0,
            "CLAUDE_LIBRARY_PREWRITE_CANCEL: an already canceled real library call proves no connection, credentials or user frame");
        result = await SendAsync(() => library.SendAsync(target, native, @"C:\SyntheticHome"));
        check(result is { Status: SubmissionStatus.Refused, Reason: "native_transport_unavailable" } && factory.Calls == 1 && credentials.Reads == 0,
            "CLAUDE_LIBRARY_CONNECT_CANCEL: cancellation inside connection setup proves no credential or user-frame access");
        foreach (int write in new[] { 1, 2 })
        {
            var connection = new Connection(target, write, cancelWrite: true);
            result = await SendAsync(() => new NativePeerAdapter(new Connected(connection), credentials).SendAsync(target, native, @"C:\SyntheticHome"));
            check(write == 1 ? result is { Status: SubmissionStatus.Refused, Reason: "native_transport_unavailable" }
                : result is { Status: SubmissionStatus.SubmittedUnconfirmed, Reason: "native_write_uncertain" },
                "CLAUDE_CANCEL_WRITE_BOUNDARY: authentication cancellation is retryable; a started user frame remains uncertain");
            check(connection.Disposed && connection.Writes == write,
                "CLAUDE_CANCEL_CONNECTION_DISPOSED: cancellation closes the actual connection at the witnessed write boundary");
        }
    }

    private static async Task RuntimeCancellationAsync(Action<bool, string> check)
    {
        var first = Candidate();
        var second = Candidate(12346);
        var provider = new Provider(first, second) { Hold = true };
        var adapter = new ClaudeNativeAdapter(provider, enablePeerCredentials: true);
        var fixture = new ToolFixture("claude-cancel-runtime", clock: TimeProvider.System, adapters: [adapter]);
        try
        {
            using var stop = new CancellationTokenSource();
            fixture.Runtime.Start(stop.Token);
            await UntilAsync(() => fixture.Store.Directory().Count(row => row.Adapter == adapter.Id) == 2);
            var rows = fixture.Store.Directory().Where(row => row.Adapter == adapter.Id).ToArray();
            var sender = fixture.Register("Synthetic cancel sender");
            var entered = Guid.NewGuid();
            fixture.Store.Send(sender.Address, rows[0].Address, entered, "Synthetic entered submission.", null, 0, fixture.Store.BrokerId);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = Guid.NewGuid();
            fixture.Store.Send(sender.Address, rows[1].Address, waiting, "Synthetic gate waiter.", null, 0, fixture.Store.BrokerId);
            await UntilAsync(() => fixture.Store.FindReceipt(waiting)?.Status == MessageStatus.Submitting);
            stop.Cancel();
            await fixture.Runtime.DisposeAsync();
            check(fixture.Store.FindReceipt(waiting) is { Status: MessageStatus.Accepted, Reason: "native_submission_wait_cancelled" } &&
                provider.Submissions == 1,
                "RUNTIME_GATE_CANCEL_REMAINS_ACCEPTED: actual worker shutdown releases the proven-unsubmitted waiter for recovery");
            check(fixture.Store.FindReceipt(entered) is { Status: MessageStatus.Uncertain, Reason: "native_uncertain_raised_cancelled" },
                "RUNTIME_ENTERED_CANCEL_UNCERTAIN: actual shutdown never retries a provider-entered ambiguous submission");
        }
        finally
        {
            provider.Release.TrySetResult();
            await fixture.DisposeAsync();
            if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, true);
        }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Synthetic Claude boundary did not reach its witness.");
            await Task.Delay(10);
        }
    }

    private sealed class Provider(params ClaudeNativeCandidate[] candidates) : IClaudeNativeProvider
    {
        internal bool Hold;
        internal int Submissions;
        internal Exception? Failure;
        internal SubmissionResult Result = new(SubmissionStatus.SubmittedUnconfirmed, "synthetic_write");
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ClaudeNativeCandidate>>(candidates);
        public Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(candidates.SingleOrDefault(row => row.Target.ProcessId == processId)); }
        public async Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref Submissions);
            Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(token);
            if (Failure is { } error) throw error;
            return Result;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConnectTimeout : INativePeerConnectionFactory
    {
        public Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
            => Task.FromException<INativePeerConnection>(new TimeoutException("Synthetic native connect timeout."));
    }
    private sealed class Connected(Connection connection) : INativePeerConnectionFactory
    {
        public Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation) => Task.FromResult<INativePeerConnection>(connection);
    }
    private sealed class ConnectCanceled : INativePeerConnectionFactory
    {
        internal int Calls;
        public Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromException<INativePeerConnection>(new OperationCanceledException("Synthetic connection cancellation."));
        }
    }
    private sealed class Credentials : IPeerCredentialSource
    {
        internal int Reads;
        public byte[] Read(CredentialTarget target)
        {
            Reads++;
            // Fresh synthetic in-memory bytes only; no production credential file is opened.
            return Encoding.UTF8.GetBytes("{\"peerToken\":\"0123456789abcdef0123456789abcdef\",\"procStartFt\":\"134000000000000000\"}");
        }
    }
    private sealed class Connection(NativeTarget target, int timeoutWrite, bool cancelWrite = false) : INativePeerConnection
    {
        private readonly TimeoutStream stream = new(timeoutWrite, cancelWrite);
        internal bool Disposed;
        internal int Writes => stream.Writes;
        public bool EnforcesCurrentUser => true;
        public NativeProcessIdentity Identity => new(target.ProcessId, target.ImagePath, target.ImageSha256, target.StartFileTime, true);
        public Stream Outbound => stream;
        public NativeProcessIdentity Reinspect() => Identity;
        public ValueTask DisposeAsync() { Disposed = true; stream.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class TimeoutStream(int timeoutWrite, bool cancelWrite) : MemoryStream
    {
        internal int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (Writes == timeoutWrite) return ValueTask.FromException(cancelWrite
                ? new OperationCanceledException("Synthetic native write cancellation.") : new TimeoutException("Synthetic native write timeout."));
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
