using System.Net;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

internal static class PairedReconnectChecks
{
    internal static async Task RunAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var hints = new Hints();
        string aPath = Path.Combine(scratch, "reconnect-a"), bPath = Path.Combine(scratch, "reconnect-b");
        var a = await Node.Create(aPath, clock, identity, hints.Read, TimeSpan.FromHours(1));
        var b = await Node.Create(bPath, clock, identity);
        try
        {
            await Pair(a, b);
            var idA = a.Store.BrokerId; var pinA = a.Identity.Fingerprint;
            var idB = b.Store.BrokerId; var pinB = b.Identity.Fingerprint;
            var trustB = a.Store.Peers().Single(); var trustA = b.Store.Peers().Single();
            string oldEndpointB = b.Endpoint;
            await b.DisposeAsync();
            b = await Node.Create(bPath, clock, identity);
            check(b.Store.BrokerId == idB && b.Identity.Fingerprint == pinB && b.Store.Peers().Single() == trustA,
                "restart reloads the same broker identity, certificate pin and complete paired trust from disk");
            check(b.Endpoint != oldEndpointB, "restart fixture exercises an actual listener endpoint change");
            var incoming = new DefaultHttpContext(); incoming.Request.Scheme = "https";
            incoming.Connection.ClientCertificate = b.Identity.Certificate;
            incoming.Connection.RemoteIpAddress = IPAddress.Parse("192.168.74.199");
            check(a.Federation.AuthenticatePeer(incoming) == idB,
                "paired certificate authenticates after an actual source IP change without inheriting a stale address pin");
            incoming.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.3");
            check(a.Federation.AuthenticatePeer(incoming) is null, "paired certificate still cannot authenticate from a public source address");
            incoming.Connection.RemoteIpAddress = IPAddress.Parse("192.168.74.199");
            using (var impostor = identity(Path.Combine(scratch, "reconnect-impostor"), idB))
            {
                incoming.Connection.ClientCertificate = impostor.Certificate;
                check(a.Federation.AuthenticatePeer(incoming) is null, "changed certificate with the same broker ID cannot inherit paired trust");
            }
            hints.Set(b.Advertisement);
            await a.Federation.ReconnectDiscoveredAsync(default);
            var refreshed = a.Store.Peers().Single();
            check(refreshed.Endpoint == b.Endpoint && refreshed.Name == trustB.Name && refreshed.PairedAt == trustB.PairedAt &&
                refreshed.CertificateSha256 == trustB.CertificateSha256,
                "pinned rediscovery refreshes only the saved endpoint and preserves the original approval and pin");
            var session = b.Register("restarted-peer");
            check((await a.Federation.GetDirectoryAsync(refreshed, default)).Sessions.Single().Address == session.Address &&
                a.Federation.PairingOffers.Count == 0 && b.Federation.PairingOffers.Count == 0,
                "restarted paired broker exchanges a live directory without another comparison code");
            string statePath = Path.Combine(aPath, "broker-state.json");
            byte[] stable = File.ReadAllBytes(statePath); var stableTime = File.GetLastWriteTimeUtc(statePath);
            int requests = b.Pairing.InfoRequests;
            await a.Federation.ReconnectDiscoveredAsync(default);
            check(b.Pairing.InfoRequests == requests && stable.SequenceEqual(File.ReadAllBytes(statePath)) &&
                File.GetLastWriteTimeUtc(statePath) == stableTime,
                "unchanged discovered endpoint performs no probe or durable write");

            hints.Set(b.Advertisement with { Endpoint = b.HttpEndpoint.Replace("http:", "https:", StringComparison.Ordinal) });
            await a.Federation.ReconnectDiscoveredAsync(default);
            check(a.Store.Peers().Single() == refreshed && stable.SequenceEqual(File.ReadAllBytes(statePath)),
                "a healthy saved endpoint survives alternate multihomed hints without durable address churn");
            requests = b.Pairing.InfoRequests;
            hints.Set(b.Advertisement with { CertificateSha256 = new string('F', 64), Endpoint = oldEndpointB });
            await a.Federation.ReconnectDiscoveredAsync(default);
            check(b.Pairing.InfoRequests == requests && a.Store.Peers().Single() == refreshed,
                "reconnect exact pin prefilter rejects unsigned mismatched identity before any HTTP probe");
            hints.Set(b.Advertisement with { BrokerId = Guid.NewGuid(), Endpoint = oldEndpointB });
            await a.Federation.ReconnectDiscoveredAsync(default);
            check(b.Pairing.InfoRequests == requests && a.Store.Peers().Single() == refreshed,
                "unpaired discovery creates no trust or network probe");

            // A process restart must reload the refreshed endpoint, not retain it only in memory.
            await a.DisposeAsync();
            a = await Node.Create(aPath, clock, identity);
            check(a.Store.BrokerId == idA && a.Identity.Fingerprint == pinA && a.Store.Peers().Single() == refreshed,
                "local restart reloads the revalidated endpoint with the original broker identity and approval");
            check((await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), default)).Sessions.Single().Address == session.Address,
                "same-address restart reconnects directly from persisted trust without discovery or a new pairing offer");
            await VerifyInFlightReads(a, b, session, check);
        }
        finally { await a.DisposeAsync(); await b.DisposeAsync(); }

        await VerifyUntrustedHints(scratch, clock, identity, check);
        await VerifyRevocationAndCancellation(scratch, clock, identity, check);
        await VerifyAutomaticRecovery(scratch, clock, identity, check);
        await VerifyPendingReconnectShutdown(scratch, clock, identity, check);
        VerifyStoreAuthorizations(scratch, clock, check);
    }

    private static async Task VerifyInFlightReads(Node a, Node b, BrokerSession session, Action<bool, string> check)
    {
        foreach (bool throughRuntime in new[] { false, true })
        {
            var original = a.Store.Peers().Single();
            var authorization = a.Store.CapturePeerAuthorization(original)!;
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.Pairing.BeforeDirectory = async () => { arrived.TrySetResult(); await release.Task; };
            await using var runtime = new BrokerRuntime(a.Store, a.Federation, []);
            var operation = throughRuntime ? runtime.DirectoryAsync(false, default) : ReadDirect();
            async Task<IReadOnlyList<BrokerSession>> ReadDirect() => (await a.Federation.GetDirectoryAsync(original, default)).Sessions;
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(a.Store.TryRefreshPeerEndpoint(original, authorization, "https://127.0.0.2:7433/"),
                "concurrent endpoint refresh completes during " + (throughRuntime ? "merged" : "direct") + " directory lookup");
            release.TrySetResult();
            check((await operation).Single().Address == session.Address,
                "endpoint refresh preserves the result of an authorized in-flight " + (throughRuntime ? "merged" : "direct") + " directory read");
            var current = a.Store.Peers().Single();
            a.Store.TryRefreshPeerEndpoint(current, authorization, b.Endpoint);
        }
        var trust = a.Store.Peers().Single();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Pairing.BeforeDirectory = async () => { started.TrySetResult(); await finish.Task; };
        var pending = a.Federation.GetDirectoryAsync(trust, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        a.Store.RevokePeer(trust.BrokerId); a.Store.AuthorizePeer(trust);
        finish.TrySetResult();
        try { await pending; check(false, "stale direct read must not survive a new identical-value authorization"); }
        catch (UnauthorizedAccessException) { check(true, "revoke and identical-value new pairing invalidates an already pending directory result"); }
        b.Pairing.BeforeDirectory = null;
    }

    private static async Task VerifyUntrustedHints(string scratch, TimeProvider clock, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var hints = new Hints();
        await using var a = await Node.Create(Path.Combine(scratch, "bad-info-a"), clock, identity, hints.Read, TimeSpan.FromHours(1));
        await using var b = await Node.Create(Path.Combine(scratch, "bad-info-b"), clock, identity);
        var trust = Trust(b, clock) with { Endpoint = "https://127.0.0.1:1/" };
        a.Store.AuthorizePeer(trust);
        foreach (string axis in new[] { "broker_id", "certificate", "endpoint" })
        {
            b.Pairing.BeforeInfo = async context => {
                var info = b.Advertisement;
                info = axis switch {
                    "broker_id" => info with { BrokerId = Guid.NewGuid() },
                    "certificate" => info with { CertificateSha256 = new string('E', 64) },
                    _ => info with { Endpoint = "https://127.0.0.2:7433/" } };
                await context.Response.WriteAsJsonAsync(info); return true;
            };
            hints.Set(b.Advertisement);
            await a.Federation.ReconnectDiscoveredAsync(default);
            check(a.Store.Peers().Single() == trust, "pinned TLS with mismatched info " + axis + " cannot overwrite a trusted endpoint");
        }
        b.Pairing.BeforeInfo = null;
        // These hints pass the identity prefilter but must fail the literal private HTTPS policy.
        foreach (string endpoint in new[] { "https://example.test:7433/", "https://203.0.113.3:7433/", "http://127.0.0.1:7433/", "https://127.0.0.1:7433/other" })
        {
            int requests = b.Pairing.InfoRequests;
            hints.Set(b.Advertisement with { Endpoint = endpoint });
            await a.Federation.ReconnectDiscoveredAsync(default);
            check(a.Store.Peers().Single() == trust && b.Pairing.InfoRequests == requests,
                "rediscovery rejects nonliteral, public, nonHTTPS or nonroot address " + endpoint);
        }
        int before = b.Pairing.InfoRequests;
        await using var disabled = await Node.Create(Path.Combine(scratch, "lan-disabled"), clock, identity, hints.Read, TimeSpan.FromMilliseconds(20), enableLan: false);
        disabled.Store.AuthorizePeer(trust);
        hints.Set(b.Advertisement);
        await disabled.Federation.ReconnectDiscoveredAsync(default);
        await Task.Delay(60);
        check(b.Pairing.InfoRequests == before && disabled.Store.Peers().Single() == trust,
            "LAN disabled forbids reconnect probing and trust changes even when discovery hints exist");
        await using var discoveryDisabled = await Node.Create(Path.Combine(scratch, "discovery-disabled"), clock, identity);
        discoveryDisabled.Store.AuthorizePeer(trust);
        await discoveryDisabled.Federation.ReconnectDiscoveredAsync(default);
        check(b.Pairing.InfoRequests == before && discoveryDisabled.Store.Peers().Single() == trust,
            "discovery disabled does not launch unsolicited endpoint recovery");
    }

    private static async Task VerifyRevocationAndCancellation(string scratch, TimeProvider clock, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var hints = new Hints();
        await using var a = await Node.Create(Path.Combine(scratch, "race-a"), clock, identity, hints.Read, TimeSpan.FromHours(1));
        await using var b = await Node.Create(Path.Combine(scratch, "race-b"), clock, identity);
        var trust = Trust(b, clock) with { Endpoint = "https://127.0.0.1:1/" };
        foreach (bool repair in new[] { false, true })
        {
            a.Store.AuthorizePeer(trust);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.Pairing.BeforeInfo = async _ => { arrived.TrySetResult(); await release.Task; return false; };
            hints.Set(b.Advertisement);
            var attempt = a.Federation.ReconnectDiscoveredAsync(default);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            a.Store.RevokePeer(trust.BrokerId);
            if (repair) a.Store.AuthorizePeer(trust); // Deliberately identical values, including PairedAt.
            release.TrySetResult(); await attempt;
            check(repair ? a.Store.Peers().Single() == trust : a.Store.Peers().Count == 0,
                repair ? "stale reconnect authorization cannot overwrite an identical-value new pairing" :
                    "stale reconnect authorization cannot resurrect a revoked pairing");
        }
        b.Pairing.BeforeInfo = null;
        a.Store.AuthorizePeer(trust);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await a.Federation.ReconnectDiscoveredAsync(cancelled.Token); }
        catch (OperationCanceledException) { }
        check(a.Store.Peers().Single() == trust, "cancelled recovery cannot publish an endpoint update");
    }

    private static async Task VerifyAutomaticRecovery(string scratch, TimeProvider clock, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var hints = new Hints();
        var a = await Node.Create(Path.Combine(scratch, "automatic-a"), clock, identity, hints.Read, TimeSpan.FromMilliseconds(20));
        await using var b = await Node.Create(Path.Combine(scratch, "automatic-b"), clock, identity);
        try
        {
            var trust = Trust(b, clock) with { Endpoint = "https://127.0.0.1:1/" };
            a.Store.AuthorizePeer(trust);
            await Task.Delay(60);
            check(a.Store.Peers().Single() == trust, "startup before peer discovery preserves persisted pairing while the peer is offline");
            b.Pairing.BeforeInfo = async context => { await context.Response.WriteAsync("{"); return true; };
            int malformedBefore = b.Pairing.InfoRequests;
            hints.Set(b.Advertisement);
            await Until(() => b.Pairing.InfoRequests >= malformedBefore + 2);
            check(a.Store.Peers().Single() == trust, "malformed pinned peer info cannot update trust or terminate subsequent background recovery passes");
            b.Pairing.BeforeInfo = null;
            await Until(() => a.Store.Peers().Single().Endpoint == b.Endpoint);
            check(a.Store.Peers().Single().PairedAt == trust.PairedAt && a.Federation.PairingOffers.Count == 0,
                "background rediscovery recovers automatically when the peer appears after startup");

            // More than one four-peer batch is needed; inaccessible early hints cannot starve later peers.
            var adverts = Enumerable.Range(0, 8).Select(i => new BrokerAdvertisement(Guid.NewGuid(), "Offline peer " + i,
                "https://127.0.0.1:1/", new string('A', 64), DateTimeOffset.UtcNow)).ToList();
            foreach (var advert in adverts) a.Store.AuthorizePeer(new(advert.BrokerId, advert.Name, "https://127.0.0.1:2/", advert.CertificateSha256, clock.GetUtcNow()));
            a.Store.AuthorizePeer(trust);
            adverts.Add(b.Advertisement); hints.Set(adverts.ToArray());
            await Until(() => a.Store.Peers().Single(p => p.BrokerId == b.Store.BrokerId).Endpoint == b.Endpoint);
            check(true, "round-robin bounded recovery reaches a live peer behind more than one batch of dead candidates");
        }
        finally { if (a is not null) await a.DisposeAsync(); }
    }

    private static async Task VerifyPendingReconnectShutdown(string scratch, TimeProvider clock, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        // Keep shutdown independent of the fairness phase's in-flight five-second batches.
        // Its arrival bound measures one saved-address probe, not a previous batch as well.
        var hints = new Hints();
        var a = await Node.Create(Path.Combine(scratch, "shutdown-a"), clock, identity, hints.Read, TimeSpan.FromMilliseconds(20));
        await using var b = await Node.Create(Path.Combine(scratch, "shutdown-b"), clock, identity);
        try
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.Pairing.BeforeInfo = async context => { arrived.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted); return false; };
            a.Store.AuthorizePeer(Trust(b, clock) with { Endpoint = "https://127.0.0.1:1/" });
            hints.Set(b.Advertisement);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await a.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            a = null!;
            check(true, "shutdown cancels and joins pending reconnect I/O before disposing identity and durable state");
        }
        finally { if (a is not null) await a.DisposeAsync(); }
    }

    private static void VerifyStoreAuthorizations(string scratch, TimeProvider clock, Action<bool, string> check)
    {
        using var store = new BrokerStore(Path.Combine(scratch, "authorization-stamps"));
        var trust = new PeerTrust(Guid.NewGuid(), "Original", "https://127.0.0.1:7433/", new string('A', 64), clock.GetUtcNow());
        store.AuthorizePeer(trust);
        var authorization = store.CapturePeerAuthorization(trust)!;
        check(store.TryRefreshPeerEndpoint(trust, authorization, "https://127.0.0.2:7433/") && store.IsPeerAuthorized(authorization),
            "an endpoint refresh preserves the authorization of an in-flight read");
        check(!store.TryRefreshPeerEndpoint(trust, authorization, "https://127.0.0.3:7433/"),
            "an overlapping refresh cannot overwrite a more recent endpoint");
        store.RevokePeer(trust.BrokerId); store.AuthorizePeer(trust);
        check(!store.IsPeerAuthorized(authorization) && !store.TryRefreshPeerEndpoint(trust, authorization, "https://127.0.0.4:7433/"),
            "revocation followed by identical-value pairing invalidates earlier operation authorization");
    }

    private static PeerTrust Trust(Node node, TimeProvider clock) => new(node.Store.BrokerId, "Original approved label", node.Endpoint, node.Identity.Fingerprint, clock.GetUtcNow());
    private static async Task Pair(Node a, Node b)
    {
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        Guid request = a.Federation.PairingOffers.Single().RequestId;
        await b.Federation.ApprovePairAsync(request, default);
        await a.Federation.ApprovePairAsync(request, default);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private sealed class Hints
    {
        private BrokerAdvertisement[] entries = [];
        internal IReadOnlyList<BrokerAdvertisement> Read() => Volatile.Read(ref entries);
        internal void Set(params BrokerAdvertisement[] value) => Volatile.Write(ref entries, value);
    }
}
