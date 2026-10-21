using AgentBroker.Worker;
using AgentBroker.Worker.Discovery;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

internal static class DiscoveryLifetimeChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var own = new BrokerAdvertisement(Guid.NewGuid(), "Synthetic lifetime check", "https://10.0.0.1:7433/", new string('A', 64), DateTimeOffset.UtcNow);
        using (var parent = new CancellationTokenSource())
        {
            var discovery = new MdnsDiscovery(own, TimeProvider.System);
            discovery.LinkParentCancellation(parent.Token);
            var stop = discovery.LifetimeToken;
            parent.Cancel();
            check(stop.IsCancellationRequested, "discovery parent cancellation reaches its own lifetime without multicast I/O");
            await discovery.DisposeAsync();
        }
        using (var parent = new CancellationTokenSource())
        {
            var discovery = new MdnsDiscovery(own, TimeProvider.System);
            discovery.LinkParentCancellation(parent.Token);
            var stop = discovery.LifetimeToken;
            await discovery.DisposeAsync();
            parent.Cancel(); // Regression: the dropped registration previously called Cancel on a disposed CTS.
            await discovery.DisposeAsync();
            check(stop.IsCancellationRequested, "discovery disposal unlinks a live parent and remains repeatable after later cancellation");
        }
        for (int i = 0; i < 8; i++)
        {
            using var parent = new CancellationTokenSource();
            var discovery = new MdnsDiscovery(own, TimeProvider.System);
            discovery.LinkParentCancellation(parent.Token);
            using var ready = new ManualResetEventSlim();
            var cancel = Task.Run(() => { ready.Wait(); parent.Cancel(); });
            var dispose = Task.Run(async () => { ready.Wait(); await discovery.DisposeAsync(); });
            ready.Set();
            await Task.WhenAll(cancel, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            await discovery.DisposeAsync();
        }
        check(true, "concurrent parent cancellation and discovery disposal complete without callbacks using disposed state");

        var scenario = new Scenario();
        var recovery = new MdnsDiscovery(own, TimeProvider.System, scenario.ReadAddresses, scenario.Connect,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30));
        try
        {
            await recovery.StartAsync(default);
            await Until(() => scenario.AddressReads >= 2);
            check(scenario.Transports.Count == 0 && recovery.Status == "discovery_waiting_for_private_lan",
                "offline startup keeps a bounded refresh loop without opening a transport");
            scenario.SetAddresses([IPAddress.Loopback, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.10.0.2")]);
            await Until(() => scenario.Transports.Count == 1 && scenario.Transports[0].Announcements.Count > 0);
            var first = scenario.Transports[0];
            check(first.Addresses.SequenceEqual([IPAddress.Parse("10.10.0.2")])
                && first.Announcements.All(a => a.Broker.Endpoint == "https://10.10.0.2:7433/"),
                "offline-to-online recovery binds only private interfaces and advertises their actual current address");
            int reads = scenario.AddressReads;
            await Until(() => scenario.AddressReads >= reads + 3);
            check(scenario.Transports.Count == 1, "unchanged healthy interfaces reuse one transport across refresh ticks");
            scenario.SetAddresses([IPAddress.Parse("192.168.7.3")]);
            await Until(() => scenario.Transports.Count >= 2 && scenario.Transports[1].Announcements.Count > 0);
            var second = scenario.Transports[1];
            check(first.Disposals == 1 && second.Announcements.All(a => a.Broker.Endpoint == "https://192.168.7.3:7433/"),
                "interface changes close the old session and replace stale advertisement addresses");
            second.FailSend = true;
            await Until(() => scenario.Transports.Count >= 3 && scenario.Transports[2].Announcements.Count > 0);
            check(second.Disposals == 1 && recovery.Status == "discovery_active",
                "a send failure retires both old loops and reconnects without restarting the module");
            var third = scenario.Transports[2];
            third.FailReceive();
            await Until(() => scenario.Transports.Count >= 4 && scenario.Transports[3].Announcements.Count > 0);
            check(third.Disposals == 1, "receive failures also trigger a fresh transport and complete prior-session cleanup");
            scenario.SetAddresses([]);
            await Until(() => scenario.Transports[3].Disposals == 1 && recovery.Status == "discovery_waiting_for_private_lan");
            check(scenario.Transports.All(t => t.Disposals == 1), "returning offline closes every previously opened transport");
            scenario.FailNextConnection();
            scenario.SetAddresses([IPAddress.Parse("172.20.0.5")]);
            await Until(() => scenario.Transports.Count >= 5 && scenario.Transports[4].Announcements.Count > 0);
            check(scenario.ConnectionFailures == 1 && recovery.Status == "discovery_active",
                "socket setup failure is retried on a bounded later refresh and does not stop discovery permanently");
        }
        finally { await recovery.DisposeAsync(); }
        check(scenario.Transports.All(t => t.Disposals == 1 && t.ActiveReceives == 0),
            "recovery shutdown closes each transport exactly once and leaves no pending receive loop");

        var inbound = new Scenario(); inbound.SetAddresses([IPAddress.Parse("10.10.0.2")]);
        await using (var discovery = new MdnsDiscovery(own, TimeProvider.System, inbound.ReadAddresses, inbound.Connect,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1)))
        {
            await discovery.StartAsync(default);
            await Until(() => inbound.Transports.Count == 1 && inbound.Transports[0].SendCount == 2);
            var transport = inbound.Transports[0];
            transport.Inject(DnsSdCodec.Query(), IPAddress.Parse("8.8.8.8"), 5353);
            transport.Inject(DnsSdCodec.Query(), IPAddress.Parse("10.10.0.9"), 9999);
            await Until(() => transport.ReceivedCount == 2);
            check(transport.SendCount == 2, "recovery transport still rejects nonprivate and wrong-port discovery queries");
            transport.Inject(DnsSdCodec.Query(), IPAddress.Parse("10.10.0.9"), 5353);
            await Until(() => transport.SendCount == 3);
            transport.Inject(DnsSdCodec.Query(), IPAddress.Parse("10.10.0.9"), 5353);
            await Until(() => transport.ReceivedCount == 4);
            check(transport.SendCount == 3, "valid private queries receive one announcement and retain the global one-second reply bound");
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Scenario
    {
        private readonly object gate = new();
        private IPAddress[] addresses = [];
        private readonly List<FakeTransport> transports = [];
        private bool failNext;
        internal int AddressReads, ConnectionFailures;
        internal IReadOnlyList<FakeTransport> Transports { get { lock (gate) return transports.ToArray(); } }
        internal void SetAddresses(IPAddress[] value) { lock (gate) addresses = value; }
        internal void FailNextConnection() { lock (gate) failNext = true; }
        internal IReadOnlyList<IPAddress> ReadAddresses() { lock (gate) { AddressReads++; return addresses; } }
        internal IMdnsTransport Connect(IReadOnlyList<IPAddress> current)
        {
            lock (gate)
            {
                if (failNext) { failNext = false; ConnectionFailures++; throw new SocketException((int)SocketError.NetworkDown); }
                var transport = new FakeTransport(current.ToArray()); transports.Add(transport); return transport;
            }
        }
    }

    private sealed class FakeTransport(IPAddress[] addresses) : IMdnsTransport
    {
        private readonly Channel<UdpReceiveResult> input = Channel.CreateUnbounded<UdpReceiveResult>();
        private readonly object gate = new();
        private readonly List<DnsSdCodec.Advertisement> announcements = [];
        private int disposals, activeReceives, sendCount, receivedCount;
        internal readonly IPAddress[] Addresses = addresses;
        internal volatile bool FailSend;
        internal int Disposals => Volatile.Read(ref disposals);
        internal int ActiveReceives => Volatile.Read(ref activeReceives);
        internal int SendCount => Volatile.Read(ref sendCount);
        internal int ReceivedCount => Volatile.Read(ref receivedCount);
        internal IReadOnlyList<DnsSdCodec.Advertisement> Announcements { get { lock (gate) return announcements.ToArray(); } }
        internal void Inject(byte[] packet, IPAddress source, int port) => input.Writer.TryWrite(new(packet, new(source, port)));
        internal void FailReceive() => input.Writer.TryComplete(new IOException("Synthetic receive failure."));
        public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken token)
        {
            Interlocked.Increment(ref activeReceives);
            try
            {
                var packet = await input.Reader.ReadAsync(token); Interlocked.Increment(ref receivedCount); return packet;
            }
            catch (ChannelClosedException) { throw new IOException("Synthetic transport closed."); }
            finally { Interlocked.Decrement(ref activeReceives); }
        }
        public ValueTask SendAsync(byte[] packet, IPAddress source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailSend) throw new IOException("Synthetic send failure.");
            Interlocked.Increment(ref sendCount);
            lock (gate) announcements.AddRange(DnsSdCodec.Parse(packet, source, DateTimeOffset.UtcNow));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposals); input.Writer.TryComplete(); return ValueTask.CompletedTask;
        }
    }
}
