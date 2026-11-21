using System.Diagnostics;
using System.Reflection;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// Exercise a reconnect that outlasts the preparation cap through the actual runtime and provider.
internal static partial class RuntimeLifecycleChecks
{
    private static async Task ReconnectPreparationOutlastedCapChecksAsync(Action<bool, string> check)
    {
        using var scratch = OwnedScratch.Create("runtime-reconnect-outlasted-cap");
        var clock = new OutlastCapClock();
        var observationClock = new OutlastCapObservationClock(clock);
        var healthy = Guid.NewGuid(); var slow = Guid.NewGuid();
        var ownerId = Guid.NewGuid(); var incarnation = Guid.NewGuid();
        var first = new OutlastCapOwner(healthy, slow, ownerId, incarnation, hangSlow: true);
        var next = new OutlastCapOwner(healthy, slow, ownerId, incarnation, hangSlow: false);
        var interval = TimeSpan.FromSeconds(1);
        first.SlowCancelled = () => clock.Advance(interval);
        var release = new TaskCompletionSource<ICodexOwnerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdStop = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        int connects = 0, deliveryTouches = 0;
        var provider = new CodexNativeProvider(true, _ =>
        {
            if (Interlocked.Increment(ref connects) == 1) return Task.FromResult<ICodexOwnerConnection>(first);
            connecting.TrySetResult(); return release.Task;
        }, () => { }, _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(
            [new(healthy, DesktopSessionActivity.Idle, clock.GetUtcNow(), clock.GetUtcNow()),
             new(slow, DesktopSessionActivity.Idle, clock.GetUtcNow(), clock.GetUtcNow())]));
        // Production owns different discovery and delivery providers. None of these delivery delegates may run.
        var deliveryProvider = new CodexNativeProvider(true,
            _ => { Interlocked.Increment(ref deliveryTouches); throw new InvalidOperationException("Synthetic delivery connect must remain unused."); },
            () => { Interlocked.Increment(ref deliveryTouches); throw new InvalidOperationException("Synthetic delivery verification must remain unused."); },
            _ => { Interlocked.Increment(ref deliveryTouches); throw new InvalidOperationException("Synthetic delivery observation must remain unused."); });
        var inner = new CodexNativeAdapter(provider, enableDelivery: true, clock: clock, deliveryProvider: deliveryProvider);
        async Task ReleaseAfterHoldAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(4), holdStop.Token);
            release.TrySetResult(next);
        }
        var adapter = new OutlastCapRecordingAdapter(inner, first, next, healthy, slow, release.Task, ReleaseAfterHoldAsync);
        using var store = new BrokerStore(Path.Combine(scratch.Root, "store"), clock: clock);
        var address = new SessionAddress(store.BrokerId, "codex:" + healthy.ToString("D"), first.Binding.NativeSessionIncarnation);
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationGate = new object(); DateTimeOffset firstLease = default;
        observationClock.BeforeUtcNow = () =>
        {
            var row = store.Directory().SingleOrDefault(item => item.Address == address);
            if (row is null || !store.IsVerifiedNativeSession(address)) return;
            lock (publicationGate)
            {
                if (firstLease == default) { firstLease = row.LeaseUntil; firstPublished.TrySetResult(); }
                else if (row.LeaseUntil > firstLease) renewed.TrySetResult();
            }
        };
        var runtime = new BrokerRuntime(store, new SlowDirectoryFederation(Guid.Empty), [adapter], clock: observationClock)
        { DiscoveryInterval = interval, DiscoveryPassDeadline = TimeSpan.FromMilliseconds(250) };
        bool renewedExact = false, clean = true, timedOut = false;
        string phase = "not_started", failureType = "none";
        try
        {
            runtime.Start(stop.Token);
            phase = "first_publication"; await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(4));
            phase = "reconnect_started"; await connecting.Task.WaitAsync(TimeSpan.FromSeconds(4));
            phase = "preparation_entered"; await adapter.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            // This await observes completion only. The timestamp and release state were saved inside DiscoverAsync.
            phase = "first_post_preparation_discovery"; await adapter.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(7));
            phase = "outlasted_pass_completion"; await adapter.DiscoveryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(7));
            phase = "same_tuple_renewal"; await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = store.Directory().SingleOrDefault(row => row.Address == address);
            renewedExact = current is not null && firstLease != default && current.LeaseUntil > firstLease &&
                store.IsAddressable(address) && store.IsVerifiedNativeSession(address) &&
                first.Binding.NativeSessionIncarnation == next.Binding.NativeSessionIncarnation;
        }
        catch (TimeoutException) { timedOut = true; }
        catch (Exception error) { failureType = error.GetType().Name; }
        finally
        {
            // Release the actual pending owner even on the first failed observation, then cancel and drain every owner.
            release.TrySetResult(next); holdStop.Cancel(); stop.Cancel();
            try { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { clean = false; if (failureType == "none") failureType = error.GetType().Name; }
            if (adapter.ReleaseTask is { } holding)
            {
                try { await holding.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (holdStop.IsCancellationRequested) { }
                catch (Exception error) { clean = false; if (failureType == "none") failureType = error.GetType().Name; }
            }
            if (adapter.InFlightDiscovery is { } discovering)
            {
                try { await discovering.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { clean = false; if (failureType == "none") failureType = error.GetType().Name; }
            }
        }
        var elapsed = adapter.PreparationToDiscovery;
        Console.WriteLine("WITNESS: CODEX_RUNTIME_RECONNECT_OUTLASTED_CAP phase=" + phase +
            "; preparationToDiscoveryTicks=" + elapsed.Ticks + "; maximumWaitTicks=" + adapter.MaximumWait.Ticks +
            "; releasedAtDiscovery=" + adapter.ReleasedAtDiscovery + "; discoveryRows=" + adapter.FirstRows +
            "; deadlineExpired=" + adapter.FirstDeadlineExpired + "; nextHealthyQueriesAtCompletion=" + adapter.NextHealthyQueriesAtCompletion +
            "; healthyNotSlow=" + adapter.HealthyNotSlow + "; originalSlowRetained=" + adapter.OriginalSlowRetained +
            "; connects=" + Volatile.Read(ref connects) + "; deliveryTouches=" + Volatile.Read(ref deliveryTouches) +
            "; firstDisposals=" + Volatile.Read(ref first.Disposals) + "; nextDisposals=" + Volatile.Read(ref next.Disposals) +
            "; cleanup=" + clean + "; timeout=" + timedOut + "; failureType=" + failureType);
        check(!timedOut && failureType == "none" && clean && adapter.MaximumWait == interval &&
            adapter.DiscoveryEntered.Task.IsCompletedSuccessfully && adapter.DiscoveryCompleted.Task.IsCompletedSuccessfully &&
            elapsed >= TimeSpan.FromMilliseconds(750) && elapsed <= TimeSpan.FromMilliseconds(2500) &&
            !adapter.ReleasedAtDiscovery && adapter.FirstRows == 0 && adapter.FirstDeadlineExpired &&
            adapter.NextHealthyQueriesAtCompletion == 0 && adapter.HealthyNotSlow && adapter.OriginalSlowRetained &&
            renewedExact && next.HealthyQueries > 0 && connects == 2 && deliveryTouches == 0 && first.Disposals == 1 && next.Disposals == 1 &&
            first.Submits == 0 && next.Submits == 0,
            "CODEX_RUNTIME_RECONNECT_PREPARATION_OUTLASTED_CAP: actual runtime enters discovery after its one-second preparation cap while the four-second reconnect remains held; the outlasted pass neither queries nor marks the healthy row slow, then the same tuple renews after release");
    }

    private sealed class OutlastCapRecordingAdapter(CodexNativeAdapter inner, OutlastCapOwner first,
        OutlastCapOwner next, Guid healthy, Guid slow, Task pending, Func<Task> beginHold) : ILocalAgentAdapter
    {
        internal readonly TaskCompletionSource PreparationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DiscoveryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int recordedPreparation, recordedDiscovery;
        private long preparationStamp, discoveryStamp;
        internal TimeSpan MaximumWait;
        internal bool ReleasedAtDiscovery, FirstDeadlineExpired, HealthyNotSlow, OriginalSlowRetained;
        internal int FirstRows = -1, NextHealthyQueriesAtCompletion = -1;
        internal Task? ReleaseTask, InFlightDiscovery;
        internal TimeSpan PreparationToDiscovery => preparationStamp != 0 && discoveryStamp != 0
            ? Stopwatch.GetElapsedTime(preparationStamp, discoveryStamp) : TimeSpan.Zero;
        public string Id => inner.Id;
        public bool DiscoveryCensusComplete => inner.DiscoveryCensusComplete;
        public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics => inner.DiscoveryDiagnostics;
        public Task PrepareDiscoveryAsync(TimeSpan maximumWait, CancellationToken stop)
        {
            if (Volatile.Read(ref first.Disposals) > 0 && Interlocked.CompareExchange(ref recordedPreparation, 1, 0) == 0)
            {
                preparationStamp = Stopwatch.GetTimestamp(); MaximumWait = maximumWait;
                ReleaseTask = beginHold(); PreparationEntered.TrySetResult();
            }
            return inner.PrepareDiscoveryAsync(maximumWait, stop);
        }
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            bool record = Volatile.Read(ref first.Disposals) > 0 && Interlocked.CompareExchange(ref recordedDiscovery, 1, 0) == 0;
            if (record)
            {
                discoveryStamp = Stopwatch.GetTimestamp(); ReleasedAtDiscovery = pending.IsCompleted;
                DiscoveryEntered.TrySetResult();
            }
            var task = DiscoverCoreAsync(brokerId, token, record); InFlightDiscovery = task; return task;
        }
        private async Task<IReadOnlyList<BrokerSession>> DiscoverCoreAsync(Guid brokerId, CancellationToken token, bool record)
        {
            var rows = await inner.DiscoverAsync(brokerId, token);
            if (record)
            {
                FirstRows = rows.Count; FirstDeadlineExpired = token.IsCancellationRequested;
                NextHealthyQueriesAtCompletion = Volatile.Read(ref next.HealthyQueries);
                // Capture inside this serialized discovery call, before the runtime can begin another pass.
                var field = typeof(CodexNativeAdapter).GetField("slowLookups", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(inner) is HashSet<Guid> marked)
                { HealthyNotSlow = !marked.Contains(healthy); OriginalSlowRetained = marked.Contains(slow); }
                DiscoveryCompleted.TrySetResult();
            }
            return rows;
        }
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => inner.SubmitAsync(message, token);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, PeerSenderInfo? sender, CancellationToken token)
            => ((ILocalAgentAdapter)inner).SubmitAsync(message, sender, token);
        public SessionAddress? FindSessionByProcess(int pid, ulong created) => ((ILocalAgentAdapter)inner).FindSessionByProcess(pid, created);
        public EngineBinding? EngineBindingOf(SessionAddress address) => inner.EngineBindingOf(address);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class OutlastCapOwner(Guid healthy, Guid slow, Guid owner, Guid incarnation, bool hangSlow) : ICodexOwnerConnection
    {
        private int closed;
        internal int HealthyQueries, Disposals, Submits;
        internal Action? SlowCancelled;
        public bool NativeIdleGuard => true;
        internal DesktopOwnerBinding Binding => new(healthy, owner, incarnation, 12345, 134000000000000000, true)
            { RouterTrust = DesktopRouterTrust.ExactPinned };
        public async Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            if (Volatile.Read(ref closed) != 0) throw new IOException("Synthetic owner is already closed.");
            if (conversationId == healthy)
            { Interlocked.Increment(ref HealthyQueries); token.ThrowIfCancellationRequested(); return Binding; }
            if (conversationId == slow && hangSlow)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { SlowCancelled?.Invoke(); throw; }
            }
            return null;
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        { Interlocked.Increment(ref Submits); throw new InvalidOperationException("No synthetic input is permitted."); }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref closed, 1); Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }

    private sealed class OutlastCapClock : TimeProvider
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly DateTimeOffset origin = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        private long offset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Elapsed.Ticks + Interlocked.Read(ref offset);
        public override DateTimeOffset GetUtcNow() => origin.AddTicks(GetTimestamp());
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref offset, amount.Ticks);
    }
    private sealed class OutlastCapObservationClock(OutlastCapClock clock) : TimeProvider
    {
        internal Action? BeforeUtcNow;
        public override DateTimeOffset GetUtcNow() { BeforeUtcNow?.Invoke(); return clock.GetUtcNow(); }
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
}
