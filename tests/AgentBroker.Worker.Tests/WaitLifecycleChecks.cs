using AgentBroker.Core;
using AgentBroker.Worker;
using ModelContextProtocol;
using System.Reflection;

internal static class WaitLifecycleChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool includeHost = true)
    {
        Console.WriteLine("WITNESS: WAIT_LIFECYCLE_EXECUTED");
        await RequestScopesAsync(check);
        await EndingsAsync(check);
        await QuietAsync(check);
        await AddressChangesAsync(check);
        await RunRegistryAsync(check);
        await ReleaseRaceAsync(check);
        if (includeHost) await WaitHostShutdownChecks.RunAsync(check);
    }

    internal static Task RunRegistryAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: WAIT_LIFECYCLE_REGISTRY_EXECUTED");
        return RegistryAsync(check);
    }

    private static object PrivateField(object value, string name) =>
        value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;

    internal static int ListeningCount(BrokerStore store) =>
        ((Dictionary<SessionAddress, int>)PrivateField(store, "listening")).Count;

    private static SessionActivity Activity(BrokerStore store, SessionAddress address) =>
        store.PresentedDirectory().Single(row => row.Address == address).Activity;

    private static async Task DrainAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        catch (McpException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task RequestScopesAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("wait-request-scopes");
        fixture.Context.Items["synthetic-base-metadata"] = new object();
        var first = fixture.NewRequest();
        var second = fixture.NewRequest();
        check(!ReferenceEquals(first.Context, second.Context) && !ReferenceEquals(first.Context, fixture.Context) &&
            !ReferenceEquals(first.Context.Items, second.Context.Items) && !ReferenceEquals(first.Context.Items, fixture.Context.Items) &&
            first.Context.Items.Count == 1 && second.Context.Items.Count == 1 &&
            first.Context.Items["agentbroker.identity"] is Guid firstIdentity && firstIdentity == fixture.Store.BrokerId &&
            second.Context.Items["agentbroker.identity"] is Guid secondIdentity && secondIdentity == fixture.Store.BrokerId,
            "WAIT_REQUEST_SCOPES_DISTINCT: requests own distinct contexts and metadata dictionaries and copy only transport identity");
        var owner = fixture.Register("Synthetic isolated request owner");
        first.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        bool foreignRefused = await ToolFixture.RefusedAsync(() => first.Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0),
            "The session must belong to the authenticated broker.");
        bool localAccepted = false;
        try { localAccepted = (await second.Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0)).Count == 0; }
        catch (McpException) { }
        check(foreignRefused && localAccepted && second.Context.Items["agentbroker.identity"] is Guid local && local == fixture.Store.BrokerId,
            "WAIT_REQUEST_SCOPES_IDENTITY: changing one request's identity cannot change another request's caller or ambient holder");
    }

    // Only the request's monotonic budget/timer is controlled; Store clocks remain ordinary.
    private sealed class QuietRequestClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<QuietTimer> timers = [];
        private readonly List<TimeSpan> scheduled = [];
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return timestamp; }
        internal TimeSpan[] ScheduledDelays { get { lock (gate) return scheduled.ToArray(); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new QuietTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (gate) { timers.Add(timer); scheduled.Add(dueTime); }
            return timer;
        }
        internal void Advance(TimeSpan amount)
        {
            QuietTimer[] ready;
            lock (gate)
            {
                timestamp += amount.Ticks;
                ready = timers.Where(timer => !timer.Disposed && timer.Due <= timestamp).ToArray();
                foreach (var timer in ready) timer.Due = long.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class QuietTimer(QuietRequestClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("This fixture owns one-shot request timers only.");
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + dueTime.Ticks;
                    return true;
                }
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (owner.gate) { Disposed = true; Due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task QuietAsync(Action<bool, string> check)
    {
        await using (var fixture = new ToolFixture("wait-lifecycle-no-poll", clock: TimeProvider.System))
        {
            var owner = fixture.Register("Synthetic quiet owner");
            int pendingReads = 0;
            Func<MessageReceipt[]> pending = () => { Interlocked.Increment(ref pendingReads); return []; };
            using var cancel = new CancellationTokenSource();
            var requestClock = new QuietRequestClock();
            var method = typeof(BrokerTools).GetMethod("WaitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var waiting = (Task<IReadOnlyList<MessageReceipt>>)method.Invoke(fixture.NewRequest().Tools,
                [owner.Address, WaitClass.Incoming, 4, pending, cancel.Token, null, "caller_recipient", requestClock])!;
            bool primaryFailed = false;
            using var guardCancel = new CancellationTokenSource();
            try
            {
                bool admitted = !waiting.IsCompleted && Volatile.Read(ref pendingReads) == 1 &&
                    fixture.Runtime.ActiveWaitCount == 1 && ListeningCount(fixture.Store) == 1;
                var scheduled = requestClock.ScheduledDelays;
                requestClock.Advance(TimeSpan.FromSeconds(4));
                // Finite deadlock guard only; completion faults/timeouts reach the named assertion below.
                var completed = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(30), guardCancel.Token));
                guardCancel.Cancel();
                bool succeeded = ReferenceEquals(completed, waiting) && waiting.IsCompletedSuccessfully;
                var finalScheduled = requestClock.ScheduledDelays;
                check(admitted && scheduled.Length == 1 && scheduled[0] == TimeSpan.FromSeconds(4) &&
                    succeeded && waiting.Result.Count == 0 && finalScheduled.Length == 1 &&
                    finalScheduled[0] == TimeSpan.FromSeconds(4) && Volatile.Read(ref pendingReads) == 2 &&
                    fixture.Runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0,
                    "WAIT_LIFECYCLE_QUIET_NO_POLL: a complete quiet four-second wait reads pending exactly twice and releases both bounded maps");
            }
            catch { primaryFailed = true; throw; }
            finally
            {
                try { cancel.Cancel(); await DrainAsync(waiting); }
                catch (Exception cleanup) when (primaryFailed)
                {
                    // Preserve the primary named assertion while keeping incomplete cleanup observable.
                    Console.WriteLine("WAIT_LIFECYCLE_QUIET_CLEANUP_FAILURE=" + cleanup.GetType().Name);
                }
            }
        }
        // The lease clock holds still through Register and the wait's admission, so a delayed call cannot reach an already
        // expired lease and pass on the initial addressability refusal; it starts only once the wait is admitted and listening.
        var leaseClock = new SetupFrozenClock();
        await using (var fixture = new ToolFixture("wait-lifecycle-address-deadline", clock: leaseClock))
        {
            var native = new BrokerSession(new(fixture.Store.BrokerId, "codex:quiet-lease", Guid.NewGuid()), "Synthetic short lease", "synthetic-native",
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, leaseClock.GetUtcNow().AddMilliseconds(400));
            fixture.Store.Register(native);
            using var cancel = new CancellationTokenSource();
            var waiting = fixture.NewRequest().Tools.WaitForMessages(native.Address, cancel.Token, timeoutSeconds: 20);
            try
            {
                bool admitted = await SetupFrozenClock.AdmittedAsync(fixture, waiting) && ListeningCount(fixture.Store) == 1;
                leaseClock.Start();
                await Task.WhenAny(waiting, Task.Delay(2500));
                check(admitted, "WITNESS WAIT_LIFECYCLE_ADDRESS_DEADLINE: the wait was admitted and listening before its lease clock started");
                check(waiting.IsFaulted && waiting.Exception!.InnerException is McpException { Message: "session_unavailable" } &&
                    fixture.Runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0,
                    "WAIT_LIFECYCLE_ADDRESS_DEADLINE: a quiet exact native lease boundary ends its wait without periodic pending scans or another mutation");
            }
            finally { cancel.Cancel(); await DrainAsync(waiting); }
        }
    }

    private static async Task AddressChangesAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("wait-lifecycle-address-signals");
        var store = fixture.Store;
        var absent = new SessionAddress(store.BrokerId, "synthetic-absent", Guid.NewGuid());
        var unchanged = store.ChangeSignal();
        store.Disconnect(absent);
        check(!unchanged.IsCompleted && store.TimeUntilAddressUnavailable(absent) == TimeSpan.Zero,
            "WAIT_SIGNAL_NOOP: disconnecting an unknown address neither pulses nor invents addressability");
        var native = fixture.Native("codex:trim-signal") with { LeaseUntil = fixture.Clock.GetUtcNow().AddSeconds(1) };
        store.Register(native);
        var trimmed = store.ChangeSignal();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        _ = store.Directory();
        check(trimmed.IsCompleted && !store.IsAddressable(native.Address),
            "WAIT_SIGNAL_TRIM: actual lease trimming pulses the captured signal");
        var mailbox = fixture.Register("Synthetic replaced mailbox");
        var successor = mailbox with { Address = mailbox.Address with { Incarnation = Guid.NewGuid() } };
        var replaced = store.ChangeSignal();
        store.Register(successor);
        check(replaced.IsCompleted && !store.IsAddressable(mailbox.Address) && store.IsAddressable(successor.Address),
            "WAIT_SIGNAL_REPLACEMENT: exact-address replacement pulses without routing the old address to its successor");
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var evicted = store.ChangeSignal();
        var victim = store.EvictRetainedMailbox();
        check(victim is not null && evicted.IsCompleted && !store.IsAddressable(victim),
            "WAIT_SIGNAL_EVICTION: a real retained-mailbox eviction pulses address loss");
        var retained = fixture.Register("Synthetic retention boundary");
        fixture.Clock.Advance(BrokerLimits.MailboxRetention - TimeSpan.FromMilliseconds(100));
        var remaining = store.TimeUntilAddressUnavailable(retained.Address);
        var quiet = store.ChangeSignal();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
        check(remaining == TimeSpan.FromMilliseconds(100) && store.TimeUntilAddressUnavailable(retained.Address) == TimeSpan.Zero &&
            !store.IsAddressable(retained.Address) && !quiet.IsCompleted,
            "WAIT_SIGNAL_RETENTION_BOUNDARY: a clock-only mailbox-retention crossing has an exact deadline even before a trimming mutation pulses");
    }

    private static async Task EndingsAsync(Action<bool, string> check)
    {
        await using (var fixture = new ToolFixture("wait-lifecycle-endings"))
        {
            var owner = fixture.Register("Synthetic ending owner");
            var peer = fixture.Register("Synthetic ending peer");
            Guid original = Guid.NewGuid();
            fixture.Tools.Send(owner.Address, peer.Address, original, "Synthetic ending question.");
            using var cancel = new CancellationTokenSource();
            var incoming = fixture.NewRequest().Tools.WaitForMessages(owner.Address, cancel.Token, timeoutSeconds: 20);
            var timedReceipt = await fixture.NewRequest().Tools.WaitForReceipt(original, default, timeoutSeconds: 1);
            check(timedReceipt.Message.Id == original && fixture.Runtime.ActiveWaitCount == 1 && !incoming.IsCompleted &&
                Activity(fixture.Store, owner.Address) == SessionActivity.Idle,
                "WAIT_LIFECYCLE_TIMEOUT: a timed-out receipt wait leaves its incoming wait visibly listening");
            var receipt = fixture.NewRequest().Tools.WaitForReceipt(original, cancel.Token, timeoutSeconds: 20);
            var ended = Task.WhenAll(incoming, receipt);
            try
            {
                long disconnectedAt = Environment.TickCount64;
                fixture.Store.Disconnect(owner.Address);
                await Task.WhenAny(ended, Task.Delay(2500));
                Console.WriteLine("NOTE WAIT_LIFECYCLE_DISCONNECT elapsedMs=" + (Environment.TickCount64 - disconnectedAt) +
                    "; incomingStatus=" + incoming.Status + "; receiptStatus=" + receipt.Status);
                check(incoming.IsFaulted && receipt.IsFaulted && fixture.Runtime.ActiveWaitCount == 0 &&
                    incoming.Exception!.InnerException is McpException { Message: "session_unavailable" } &&
                    receipt.Exception!.InnerException is McpException { Message: "session_unavailable" } && ListeningCount(fixture.Store) == 0,
                    "WAIT_LIFECYCLE_DISCONNECT: quiet address loss promptly releases both twenty-second waits");
            }
            finally { cancel.Cancel(); await DrainAsync(ended); }
        }

        await using (var fixture = new ToolFixture("wait-lifecycle-cancel"))
        {
            var owner = fixture.Register("Synthetic cancelled owner");
            var peer = fixture.Register("Synthetic cancelled peer");
            Guid original = Guid.NewGuid();
            fixture.Tools.Send(owner.Address, peer.Address, original, "Synthetic cancelled question.");
            using var incomingCancel = new CancellationTokenSource();
            using var receiptCancel = new CancellationTokenSource();
            var incoming = fixture.NewRequest().Tools.WaitForMessages(owner.Address, incomingCancel.Token, timeoutSeconds: 20);
            var receipt = fixture.NewRequest().Tools.WaitForReceipt(original, receiptCancel.Token, timeoutSeconds: 20);
            try
            {
                incomingCancel.Cancel();
                await DrainAsync(incoming);
                check(incoming.IsCanceled && !receipt.IsCompleted && fixture.Runtime.ActiveWaitCount == 1 &&
                    Activity(fixture.Store, owner.Address) == SessionActivity.Idle,
                    "WAIT_LIFECYCLE_CANCEL_ONE: cancelling incoming preserves the receipt wait and listening");
                receiptCancel.Cancel();
                await DrainAsync(receipt);
                check(receipt.IsCanceled && fixture.Runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0 &&
                    Activity(fixture.Store, owner.Address) == SessionActivity.Unknown,
                    "WAIT_LIFECYCLE_CANCEL_LAST: cancelling the last class clears both bounded maps");
            }
            finally { incomingCancel.Cancel(); receiptCancel.Cancel(); await DrainAsync(incoming, receipt); }
        }

        await using (var fixture = new ToolFixture("wait-lifecycle-store-failure"))
        {
            var owner = fixture.Register("Synthetic failed-store owner");
            var peer = fixture.Register("Synthetic failed-store peer");
            Guid original = Guid.NewGuid();
            fixture.Tools.Send(owner.Address, peer.Address, original, "Synthetic store failure question.");
            using var cancel = new CancellationTokenSource();
            var incoming = fixture.NewRequest().Tools.WaitForMessages(owner.Address, cancel.Token, timeoutSeconds: 20);
            var receipt = fixture.NewRequest().Tools.WaitForReceipt(original, cancel.Token, timeoutSeconds: 20);
            try
            {
                fixture.Store.Dispose();
                await DrainAsync(incoming, receipt);
                check(new Task[] { incoming, receipt }.All(task => task.IsFaulted && task.Exception!.InnerException is ObjectDisposedException) &&
                    fixture.Runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0,
                    "WAIT_LIFECYCLE_EXCEPTION_CLEANUP: a disposed-store fault leaves no wait or listening entry");
            }
            finally { cancel.Cancel(); await DrainAsync(incoming, receipt); }
        }

        await using (var fixture = new ToolFixture("wait-lifecycle-runtime-shutdown"))
        {
            var owner = fixture.Register("Synthetic shutdown owner");
            var peer = fixture.Register("Synthetic shutdown peer");
            Guid original = Guid.NewGuid();
            fixture.Tools.Send(owner.Address, peer.Address, original, "Synthetic shutdown question.");
            using var cancel = new CancellationTokenSource();
            var incoming = fixture.NewRequest().Tools.WaitForMessages(owner.Address, cancel.Token, timeoutSeconds: 20);
            var receipt = fixture.NewRequest().Tools.WaitForReceipt(original, cancel.Token, timeoutSeconds: 20);
            try
            {
                await fixture.Runtime.DisposeAsync();
                await Task.WhenAny(Task.WhenAll(incoming, receipt), Task.Delay(2000));
                check(new Task[] { incoming, receipt }.All(task => task.IsFaulted && task.Exception!.InnerException is McpException { Message: "broker_stopping" }) &&
                    fixture.Runtime.ActiveWaitCount == 0 &&
                    Activity(fixture.Store, owner.Address) == SessionActivity.Unknown,
                    "WAIT_LIFECYCLE_SHUTDOWN: runtime disposal ends both tool waits with broker_stopping and clears listening");
                bool admissionRefused = false;
                try { using var unexpected = fixture.Runtime.BeginWait(owner.Address, WaitClass.Incoming); }
                catch (BrokerException error) { admissionRefused = error.Code == "broker_stopping"; }
                bool refused = false;
                try { await fixture.NewRequest().Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 0); }
                catch (McpException error) { refused = error.Message == "broker_stopping"; }
                await fixture.Runtime.DisposeAsync();
                check(admissionRefused && refused && fixture.Runtime.ActiveWaitCount == 0 && fixture.Runtime.WaitStopping.IsCancellationRequested,
                    "WAIT_LIFECYCLE_SHUTDOWN_FINAL: admission itself refuses broker_stopping before later tools can link a cached cancelled token");
            }
            finally { cancel.Cancel(); await DrainAsync(incoming, receipt); }
        }
    }

    private static async Task RegistryAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("wait-lifecycle-registry");
        var runtime = fixture.Runtime;
        var owners = Enumerable.Range(0, 9).Select(index => fixture.Register("Synthetic slot owner " + index)).ToArray();
        var held = new List<IDisposable>();
        try
        {
            for (int index = 0; index < BrokerRuntime.MaximumConcurrentWaits; index++)
                held.Add(runtime.BeginWait(owners[index / 2].Address, (WaitClass)(index % 2)));
            bool capacity = false, duplicate = false;
            try { runtime.BeginWait(owners[8].Address, WaitClass.Receipt).Dispose(); }
            catch (BrokerException error) { capacity = error.Code == "wait_capacity"; }
            try { runtime.BeginWait(owners[0].Address, WaitClass.Incoming).Dispose(); }
            catch (BrokerException error) { duplicate = error.Code == "wait_in_progress"; }
            check(capacity && duplicate && runtime.ActiveWaitCount == 16 && ListeningCount(fixture.Store) == 8,
                "WAIT_LIFECYCLE_CAPACITY: sixteen two-class slots fill the cap and preserve duplicate precedence");
            held[0].Dispose();
            held.Add(runtime.BeginWait(owners[8].Address, WaitClass.Receipt));
            check(runtime.ActiveWaitCount == 16 && Activity(fixture.Store, owners[0].Address) == SessionActivity.Idle,
                "WAIT_LIFECYCLE_CAPACITY_REUSE: a released slot is reusable while its other class remains listening");
        }
        finally { foreach (var slot in held) slot.Dispose(); }
        check(runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0,
            "WAIT_LIFECYCLE_CAPACITY_CLEANUP: releasing a full registry empties its slots and listening keys");

        var owner = owners[0];
        using (var local = runtime.BeginWait(owner.Address, WaitClass.Incoming))
        using (var incarnation = runtime.BeginWait(owner.Address with { Incarnation = Guid.NewGuid() }, WaitClass.Incoming))
        using (var broker = runtime.BeginWait(owner.Address with { BrokerId = Guid.NewGuid() }, WaitClass.Incoming))
            check(runtime.ActiveWaitCount == 3, "WAIT_LIFECYCLE_EXACT_ADDRESS: broker and incarnation remain part of the exclusion key");

        var old = runtime.BeginWait(owner.Address, WaitClass.Incoming);
        old.Dispose();
        using (var current = runtime.BeginWait(owner.Address, WaitClass.Incoming))
        {
            old.Dispose();
            check(runtime.ActiveWaitCount == 1 && Activity(fixture.Store, owner.Address) == SessionActivity.Idle,
                "WAIT_LIFECYCLE_IDEMPOTENT_RELEASE: disposing an old slot again cannot remove its replacement");
        }
        bool bounded = true;
        for (int index = 0; index < 64; index++)
        {
            var cycle = Enum.GetValues<WaitClass>().Select(waitClass => runtime.BeginWait(owner.Address, waitClass)).ToArray();
            bounded &= runtime.ActiveWaitCount == 2;
            await Task.WhenAll(cycle.Select(slot => Task.Run(() => { slot.Dispose(); slot.Dispose(); })));
            bounded &= runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0 &&
                Activity(fixture.Store, owner.Address) == SessionActivity.Unknown;
        }
        check(bounded, "WAIT_LIFECYCLE_BOUNDED_CHURN: sixty-four parallel release cycles retain no slot or listening key");
        var shutdownSlot = runtime.BeginWait(owner.Address, WaitClass.Receipt);
        await runtime.DisposeAsync();
        check(runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0 && Activity(fixture.Store, owner.Address) == SessionActivity.Unknown,
            "WAIT_LIFECYCLE_REGISTRY_DISPOSE: runtime disposal itself releases direct slots before callers dispose their handles");
        shutdownSlot.Dispose(); shutdownSlot.Dispose();
        bool closed = false;
        try { runtime.BeginWait(owner.Address, WaitClass.Incoming).Dispose(); }
        catch (BrokerException error) { closed = error.Code == "broker_stopping"; }
        check(closed && runtime.WaitStopping.IsCancellationRequested && runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0,
            "WAIT_LIFECYCLE_REGISTRY_FINAL: repeated handle disposal cannot reopen or retain a stopped registry");
    }

    private static async Task ReleaseRaceAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("wait-lifecycle-release-race");
        var owner = fixture.Register("Synthetic release-race owner");
        var old = fixture.Runtime.BeginWait(owner.Address, WaitClass.Incoming);
        object storeGate = PrivateField(fixture.Store, "gate"), waitGate = PrivateField(fixture.Runtime, "waitGate");
        Task release;
        Task<IDisposable>? acquire = null;
        IDisposable? acquired = null;
        bool serialized;
        // Hold the existing store gate so release pauses exactly at its listening decrement. No timing-only sleep.
        Monitor.Enter(storeGate);
        try
        {
            release = Task.Run(old.Dispose);
            serialized = SpinWait.SpinUntil(() =>
            {
                bool entered = Monitor.TryEnter(waitGate);
                if (entered) Monitor.Exit(waitGate);
                return !entered;
            }, TimeSpan.FromSeconds(2));
            check(serialized, "WAIT_LIFECYCLE_RELEASE_SERIALIZED: a release blocked at its listening decrement holds the admission gate");
            if (serialized) acquire = Task.Run(() => fixture.Runtime.BeginWait(owner.Address, WaitClass.Receipt));
        }
        finally { Monitor.Exit(storeGate); }
        await release.WaitAsync(TimeSpan.FromSeconds(3));
        if (acquire is not null) acquired = await acquire.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            check(acquired is not null && fixture.Runtime.ActiveWaitCount == 1 && Activity(fixture.Store, owner.Address) == SessionActivity.Idle,
                "WAIT_LIFECYCLE_RELEASE_ADMIT_RACE: a wait admitted after a paused release remains visibly listening");
        }
        finally { acquired?.Dispose(); old.Dispose(); }
        check(fixture.Runtime.ActiveWaitCount == 0 && ListeningCount(fixture.Store) == 0,
            "WAIT_LIFECYCLE_RELEASE_RACE_CLEANUP: the controlled release and admission leave no keys");
    }
}
