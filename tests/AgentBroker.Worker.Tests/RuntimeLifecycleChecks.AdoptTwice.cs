using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Worker.Adapters;

internal static partial class RuntimeLifecycleChecks
{
    // A second retirement must start a new reconnect rather than adopt the previously consumed task.
    private static async Task ReconnectAdoptTwiceChecksAsync(Action<bool, string> check)
    {
        var thread = Guid.NewGuid();
        var ownersGate = new object();
        var owners = new List<AdoptTwiceOwner>();
        int connects = 0;
        AdoptTwiceOwner? OwnerAt(int index)
        {
            lock (ownersGate) return index < owners.Count ? owners[index] : null;
        }
        AdoptTwiceOwner[] OwnersSnapshot()
        {
            lock (ownersGate) return owners.ToArray();
        }
        var provider = new CodexNativeProvider(true, token =>
        {
            token.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref connects);
            var owner = new AdoptTwiceOwner(thread, ordinal);
            lock (ownersGate) owners.Add(owner);
            return Task.FromResult<ICodexOwnerConnection>(owner);
        }, () => { }, _ => Task.FromResult<IReadOnlyList<DesktopDirectorySession>>([]));
        bool initial = false, firstRetired = false, firstCycle = false, secondRetired = false, secondCycle = false;
        bool providerDisposed = false;
        int firstCycleConnects = 0, secondCycleConnects = 0;
        string phase = "initial_lookup";
        string? failureKind = null, cleanupKind = null;
        var bound = TimeSpan.FromSeconds(4);
        try
        {
            var firstBinding = await provider.ResolveOwnerAsync(thread, default).WaitAsync(bound);
            var first = OwnerAt(0);
            initial = first is not null && firstBinding == first.Binding && Volatile.Read(ref connects) == 1 &&
                first.Queries == 1 && first.DisposeCalls == 0;
            if (first is not null)
            {
                phase = "first_failure";
                first.FailNextLookup();
                firstRetired = await AdoptTwiceInjectedFailureAsync(provider, thread, bound);
                phase = "first_adoption";
                var secondBinding = await provider.ResolveOwnerAsync(thread, default).WaitAsync(bound);
                var second = OwnerAt(1);
                firstCycleConnects = Volatile.Read(ref connects);
                firstCycle = firstRetired && second is not null && secondBinding == second.Binding &&
                    firstCycleConnects == 2 && first.DisposeCalls == 1 && first.Queries == 2 &&
                    second.DisposeCalls == 0 && second.Queries == 1;
                if (second is not null)
                {
                    phase = "second_failure";
                    second.FailNextLookup();
                    secondRetired = await AdoptTwiceInjectedFailureAsync(provider, thread, bound);
                    phase = "second_adoption";
                    // Do not wait for a third-connect signal: the stale-task mutant never starts it.
                    // Actually attempting adoption must exercise the closed owner's refusal instead.
                    var thirdBinding = await provider.ResolveOwnerAsync(thread, default).WaitAsync(bound);
                    var third = OwnerAt(2);
                    secondCycleConnects = Volatile.Read(ref connects);
                    secondCycle = secondRetired && third is not null && thirdBinding == third.Binding &&
                        secondCycleConnects == 3 && first.DisposeCalls == 1 && second.DisposeCalls == 1 &&
                        second.Queries == 2 && third.DisposeCalls == 0 && third.Queries == 1;
                }
            }
        }
        catch (Exception error) { failureKind = error.GetType().Name; }
        finally
        {
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(bound);
                providerDisposed = true;
            }
            catch (Exception error) { cleanupKind = error.GetType().Name; }
        }
        var finished = OwnersSnapshot();
        bool exactLifetimes = finished.Length == 3 &&
            finished.All(owner => owner.DisposeCalls == 1 && owner.ClosedUses == 0 && owner.Submits == 0) &&
            finished[0].InjectedFailures == 1 && finished[1].InjectedFailures == 1 && finished[2].InjectedFailures == 0;
        Console.WriteLine("NOTE CODEX_RECONNECT_ADOPT_TWICE phase=" + phase +
            "; connects=" + Volatile.Read(ref connects) + "; owners=" + finished.Length +
            "; firstCycleConnects=" + firstCycleConnects + "; secondCycleConnects=" + secondCycleConnects +
            "; firstCycle=" + firstCycle + "; secondCycle=" + secondCycle +
            "; disposed=" + providerDisposed + "; failureKind=" + (failureKind ?? "none") +
            "; cleanupKind=" + (cleanupKind ?? "none") +
            "; closedUses=" + finished.Sum(owner => owner.ClosedUses) +
            "; disposalCalls=" + finished.Sum(owner => owner.DisposeCalls));
        check(initial && firstRetired && firstCycle && secondRetired && secondCycle && providerDisposed &&
            failureKind is null && cleanupKind is null && Volatile.Read(ref connects) == 3 && exactLifetimes,
            "CODEX_RECONNECT_ADOPT_TWICE: two failed connections start two fresh adoptions and all three synthetic owners are disposed exactly once without using a closed owner");
    }

    private static async Task<bool> AdoptTwiceInjectedFailureAsync(CodexNativeProvider provider, Guid thread, TimeSpan bound)
    {
        try { await provider.ResolveOwnerAsync(thread, default).WaitAsync(bound); return false; }
        catch (AdoptTwiceLookupFailure) { return true; }
    }

    private sealed class AdoptTwiceLookupFailure : IOException { }

    private sealed class AdoptTwiceOwner : ICodexOwnerConnection
    {
        private readonly Guid thread;
        private int closed, failNext, queries, disposeCalls, closedUses, submits, injectedFailures;
        internal AdoptTwiceOwner(Guid thread, int ordinal)
        {
            this.thread = thread;
            Binding = new(thread, Guid.NewGuid(), Guid.NewGuid(), checked((uint)(12000 + ordinal)),
                134000000000000000UL + checked((ulong)ordinal), true) { RouterTrust = DesktopRouterTrust.ExactPinned };
        }
        internal DesktopOwnerBinding Binding { get; }
        internal int Queries => Volatile.Read(ref queries);
        internal int DisposeCalls => Volatile.Read(ref disposeCalls);
        internal int ClosedUses => Volatile.Read(ref closedUses);
        internal int Submits => Volatile.Read(ref submits);
        internal int InjectedFailures => Volatile.Read(ref injectedFailures);
        internal void FailNextLookup() => Interlocked.Exchange(ref failNext, 1);
        public bool NativeIdleGuard => Volatile.Read(ref closed) == 0;
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref queries);
            if (Volatile.Read(ref closed) != 0)
            {
                Interlocked.Increment(ref closedUses);
                return Task.FromException<DesktopOwnerBinding?>(new DesktopAdapterException("connection_closed"));
            }
            if (Interlocked.Exchange(ref failNext, 0) != 0)
            {
                Interlocked.Increment(ref injectedFailures);
                return Task.FromException<DesktopOwnerBinding?>(new AdoptTwiceLookupFailure());
            }
            return Task.FromResult<DesktopOwnerBinding?>(conversationId == thread ? Binding : null);
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        {
            Interlocked.Increment(ref submits);
            throw new InvalidOperationException("Synthetic adoption checks never submit.");
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCalls);
            Volatile.Write(ref closed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
