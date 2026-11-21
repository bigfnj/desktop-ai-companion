using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;

internal static class FederationBoundaryChecks
{
    internal static async Task RunAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: POSTPUSH_FEDERATION_BOUNDARIES_EXECUTED");
        Console.WriteLine("FEDERATION_WORKER_SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(typeof(PeerFederation).Assembly.Location))));
        await EnvelopeAsync(scratch, identity, check);
        await FailedConnectAsync(scratch, identity, check);
    }

    private static async Task Pair(Node a, Node b)
    {
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        await b.Federation.ApprovePairAsync(b.Federation.PairingOffers.Single().RequestId, default);
        await a.Federation.ApprovePairAsync(a.Federation.PairingOffers.Single().RequestId, default);
    }

    private static async Task EnvelopeAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "envelope-a"), clock, identity);
        await using var b = await Node.Create(Path.Combine(scratch, "envelope-b"), clock, identity);
        await Pair(a, b);
        var sender = a.Register("envelope-sender");
        var recipient = b.Register("envelope-recipient");
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address,
            new string('\'', 16 * 1024), clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5));
        check(JsonSerializer.SerializeToUtf8Bytes(message).Length > 65536,
            "FEDERATION_ENVELOPE_WITNESS: admitted text expands beyond both former request and receipt limits");
        a.Store.Accept(message, a.Store.BrokerId);
        check(a.Store.BeginDelivery()?.Message.Id == message.Id, "FEDERATION_ENVELOPE_CLAIM: the real store admits and claims the worst-case text");
        var peer = a.Store.Peers().Single();
        MessageReceipt? submitted = null;
        try { submitted = await a.Federation.SubmitAsync(peer, message, default); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or JsonException) { }
        check(submitted?.Message == message && b.Store.FindReceipt(message.Id)?.Message == message,
            "FEDERATION_ESCAPED_MESSAGE: authenticated submission and its full receipt preserve every admitted text byte");
        a.Store.CompleteDelivery(a.Store.FindReceipt(message.Id)!, MessageStatus.Submitted, "peer_admitted");
        b.Store.RecordRecipientReceipt(message.Id, recipient.Address, b.Store.BrokerId, MessageStatus.Delivered);
        MessageReceipt? remote = null;
        try { remote = await a.Federation.GetReceiptAsync(peer, message.Id, default); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or JsonException) { }
        check(remote is { Status: MessageStatus.Delivered } && remote.Message == message,
            "FEDERATION_ESCAPED_RECEIPT: later authenticated receipt reads preserve the full escaped message");
        check(a.Store.RecordPeerReceipt(a.Store.FindReceipt(message.Id)!, recipient.Address, b.Store.BrokerId, remote!.Status, remote.Reason).Status == MessageStatus.Delivered,
            "FEDERATION_ESCAPED_RECONCILE: the sender settles the same admitted message from its exact peer receipt");

        for (int i = 1; i < 256; i++)
        {
            string id = i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(4, '0') + new string('&', 156);
            b.Store.Register(new(new(b.Store.BrokerId, id, Guid.NewGuid()), new string('\'', 160), new string('+', 80),
                SessionCapabilities.Receive, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1)));
        }
        check(JsonSerializer.SerializeToUtf8Bytes(new PeerDirectory(b.Store.BrokerId, b.Store.PresentedDirectory(true))).Length > 262144,
            "FEDERATION_DIRECTORY_WITNESS: a valid maximum directory exceeds the former response limit");
        PeerDirectory? directory = null;
        try { directory = await a.Federation.GetDirectoryAsync(peer, default); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or JsonException) { }
        check(directory?.Sessions.Count == 256 && directory.Sessions.Any(row => row.Address == recipient.Address),
            "FEDERATION_DIRECTORY_ENVELOPE: every valid escaped directory row survives authenticated transport");
    }

    private static async Task FailedConnectAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        await using var a = await Node.Create(Path.Combine(scratch, "connect-a"), clock, identity);
        string bPath = Path.Combine(scratch, "connect-b");
        Node? b = await Node.Create(bPath, clock, identity);
        try
        {
            await Pair(a, b);
            var peer = a.Store.Peers().Single();
            var sender = a.Register("connect-sender");
            var recipient = b.Register("connect-recipient");
            var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address,
                "Synthetic exact-message reconnect check.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5));
            int closedPort = new Uri(b.Endpoint).Port;
            await b.DisposeAsync(); b = null;
            // Bind without listening: retain ownership of the closed port so no unrelated listener can race this case.
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, closedPort));
            a.Store.Accept(message, a.Store.BrokerId);
            await using var runtime = new BrokerRuntime(a.Store, a.Federation, []);
            runtime.Start(default);
            await Until(() => a.Store.FindReceipt(message.Id) is { Status: MessageStatus.Accepted, Reason: "peer_connection_unavailable" }, "FEDERATION_PRECONNECT_DEFERS");
            check(a.Store.FindReceipt(message.Id) is { Attempts: 0 } pending && pending.Message == message,
                "FEDERATION_PRECONNECT_DEFERS: proven failed socket connect retains the same accepted message and unconsumed attempt");
            b = await Node.Create(bPath, clock, identity);
            b.Store.Register(recipient with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
            check(a.Store.TryRefreshPeerEndpoint(peer, a.Store.CapturePeerAuthorization(peer)!, b.Endpoint),
                "FEDERATION_PRECONNECT_REFRESH: an authenticated same-peer endpoint refresh preserves routing identity");
            await Until(() => b.Store.FindReceipt(message.Id)?.Message == message && a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Submitted);
            check(b.Store.Messages(recipient.Address).Count(row => row.Message.Id == message.Id) == 1 &&
                a.Store.FindReceipt(message.Id) is { Attempts: 1 },
                "FEDERATION_PRECONNECT_RECOVERS: the same UUID reaches the same exact recipient once after the peer returns");
            b.Store.RecordRecipientReceipt(message.Id, recipient.Address, b.Store.BrokerId, MessageStatus.Delivered);
            await Until(() => a.Store.FindReceipt(message.Id)?.Status == MessageStatus.Delivered);
            check(a.Store.FindReceipt(message.Id)?.Message.Recipient == recipient.Address,
                "FEDERATION_PRECONNECT_RECEIPT: normal receipt reconciliation keeps the original exact recipient");
        }
        finally { if (b is not null) await b.DisposeAsync(); }
    }

    private static async Task Until(Func<bool> condition, string label = "FEDERATION_BOUNDARY_DEADLINE")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException error) { throw new InvalidOperationException(label + ": the expected bounded state was not reached.", error); }
    }
}
