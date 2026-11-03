using AgentBroker.CodexDesktopAdapter;

// Only synthetic in-memory connections. The reporting proof reuses a connect-time verdict, never another image read.
internal static class DirectoryObserverChecks
{
    private const string File = "DirectoryObserverChecks.cs: ";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var signed = new ProbeConnection(DesktopRouterTrust.PublisherSigned);
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(signed, new()))
        {
            var observer = adapter.CreateDirectoryObserver(TimeSpan.FromHours(1));
            try
            {
                check(!observer.PublisherVerifiedForReporting,
                    File + "OBSERVER_REPORT_REQUIRES_SNAPSHOT: a new connection has no successful snapshot proof");
                int before = signed.OwnerChecks;
                _ = observer.Snapshot();
                check(observer.PublisherVerifiedForReporting && signed.OwnerChecks == before + 1
                    && observer.RouterIdentity == (signed.Identity.Pid, signed.Identity.CreationFileTime),
                    File + "OBSERVER_REPORT_SIGNED_GATE: a same-incarnation signed snapshot reports its connect-time publisher verdict");
                signed.FailIdentity = true;
                string failure = SnapshotFailure(observer);
                check(failure == "server_incarnation_changed" && !observer.PublisherVerifiedForReporting,
                    File + "OBSERVER_REPORT_RESET_ON_FAILURE: an identity failure clears a prior successful snapshot proof");
                signed.FailIdentity = false;
                before = signed.OwnerChecks;
                _ = observer.Snapshot();
                check(observer.PublisherVerifiedForReporting && signed.OwnerChecks == before + 1,
                    File + "OBSERVER_REPORT_RECHECKS_IDENTITY: recovery requires this connection's next same-owner check");
                check(signed.Inner.Requests.Count == 1 && signed.Inner.SubmissionCount == 0,
                    File + "OBSERVER_REPORT_NO_NEW_IO: snapshot proof adds no protocol request or submission after initialize");
            }
            finally { await observer.DisposeAsync(); }
            check(!observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_DISPOSE_CLEARS: disposal clears the successful snapshot proof");
            bool disposed = false;
            try { _ = observer.Snapshot(); }
            catch (ObjectDisposedException) { disposed = true; }
            check(disposed && !observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_DISPOSED_SNAPSHOT: a disposed snapshot cannot restore the reporting proof");
        }

        // Even the same synthetic PID/start tuple does not carry proof from one connection object to a new one.
        var fresh = new ProbeConnection(DesktopRouterTrust.PublisherSigned);
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(fresh, new()))
        await using (var observer = adapter.CreateDirectoryObserver(TimeSpan.FromHours(1)))
        {
            check(fresh.Identity == signed.Identity && !observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_FRESH_CONNECTION: a reconnect starts without its predecessor's reporting proof");
            fresh.FailIdentity = true;
            check(SnapshotFailure(observer) == "server_incarnation_changed" && !observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_FRESH_FAILURE: a fresh connection cannot borrow proof when its first snapshot fails");
            fresh.FailIdentity = false;
            _ = observer.Snapshot();
            check(observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_FRESH_SUCCESS: a reconnect establishes its own successful signed snapshot proof");
        }

        var exact = new ProbeConnection(DesktopRouterTrust.ExactPinned);
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(exact, new()))
        await using (var observer = adapter.CreateDirectoryObserver(TimeSpan.FromHours(1)))
        {
            _ = observer.Snapshot();
            check(!observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_EXACT_GATE: exact trust alone is not publisher-verification reporting proof");
        }

        foreach (bool protocolFailure in new[] { false, true })
        {
            var broken = new ProbeConnection(DesktopRouterTrust.PublisherSigned);
            await using var adapter = await DesktopAdapter.ConnectForTestAsync(broken, new());
            await using var observer = adapter.CreateDirectoryObserver(TimeSpan.FromHours(1));
            _ = observer.Snapshot();
            if (protocolFailure) broken.Inner.Inject(new() { ["type"] = "response" });
            else broken.Inner.Disconnect();
            var until = System.Diagnostics.Stopwatch.StartNew();
            string failure = SnapshotFailure(observer);
            while ((!broken.Inner.Disposed || failure != "directory_observer_disconnected") && until.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(5);
                failure = SnapshotFailure(observer);
            }
            check(broken.Inner.Disposed && failure == "directory_observer_disconnected"
                && !observer.PublisherVerifiedForReporting,
                File + "OBSERVER_REPORT_DISCONNECT_CLEARS: " + (protocolFailure ? "protocol" : "transport")
                    + " failure cannot retain a signed snapshot proof");
        }
    }

    private static string SnapshotFailure(DesktopDirectoryObserver observer)
    {
        try { _ = observer.Snapshot(); return "accepted"; }
        catch (DesktopAdapterException error) { return error.Code; }
    }

    private sealed class ProbeConnection(DesktopRouterTrust trust) : IDesktopConnection
    {
        internal FakeConnection Inner { get; } = new() { Trust = trust };
        internal int OwnerChecks;
        internal bool FailIdentity;
        public Stream Stream => Inner.Stream;
        public OwnerProcessIdentity Identity => Inner.Identity;
        public DesktopRouterTrust Trust => Inner.Trust;
        public void VerifyStillSameOwner()
        {
            OwnerChecks++;
            if (FailIdentity) throw new DesktopAdapterException("server_incarnation_changed");
            Inner.VerifyStillSameOwner();
        }
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
