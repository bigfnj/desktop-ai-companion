using System.Net;
using System.Net.Sockets;

namespace AgentBroker.Worker.Discovery;

public sealed class DiscoveryCache(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, (BrokerAdvertisement Peer, DateTimeOffset Until)> peers = new();
    public IReadOnlyList<BrokerAdvertisement> Entries { get { lock (gate) { Trim(); return peers.Values.Select(p => p.Peer).ToArray(); } } }
    public void Observe(byte[] packet, IPAddress source, Guid self)
    {
        lock (gate)
        {
            Trim(); foreach (var item in DnsSdCodec.Parse(packet, source, clock.GetUtcNow()))
            {
                if (item.Broker.BrokerId == self) continue;
                if (item.Ttl == 0) { peers.Remove(item.Broker.BrokerId); continue; }
                if (!peers.ContainsKey(item.Broker.BrokerId) && peers.Count >= 32) peers.Remove(peers.MinBy(p => p.Value.Until).Key);
                peers[item.Broker.BrokerId] = (item.Broker, clock.GetUtcNow().AddSeconds(item.Ttl));
            }
        }
    }
    private void Trim() { foreach (var p in peers.Where(p => p.Value.Until <= clock.GetUtcNow()).ToArray()) peers.Remove(p.Key); }
}

public sealed class MdnsDiscovery : IAsyncDisposable
{
    private sealed record Session(IMdnsTransport Transport, IPAddress[] Addresses, CancellationTokenSource Stop, Task Run);
    private readonly BrokerAdvertisement own;
    private readonly TimeProvider clock;
    private readonly DiscoveryCache cache;
    private readonly Func<IReadOnlyList<IPAddress>> addressSource;
    private readonly Func<IReadOnlyList<IPAddress>, IMdnsTransport> transportFactory;
    private readonly TimeSpan refreshInterval, announceInterval;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly object lifetimeGate = new();
    private CancellationTokenRegistration parentCancellation;
    private bool parentLinked, started;
    private Task? disposal;
    private Task? supervisor;
    public IReadOnlyList<BrokerAdvertisement> Entries => cache.Entries;
    public string Status { get; private set; } = "manual_pairing_available";

    public MdnsDiscovery(BrokerAdvertisement own, TimeProvider clock)
        : this(own, clock, Federation.PeerFederation.PrivateAddresses, addresses => new UdpMdnsTransport(addresses),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(45)) { }

    internal MdnsDiscovery(BrokerAdvertisement own, TimeProvider clock,
        Func<IReadOnlyList<IPAddress>> addressSource, Func<IReadOnlyList<IPAddress>, IMdnsTransport> transportFactory,
        TimeSpan refreshInterval, TimeSpan announceInterval)
    {
        this.own = own; this.clock = clock; cache = new(clock);
        this.addressSource = addressSource; this.transportFactory = transportFactory;
        if (refreshInterval <= TimeSpan.Zero || announceInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        this.refreshInterval = refreshInterval; this.announceInterval = announceInterval;
    }

    public Task StartAsync(CancellationToken token)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (started) throw new InvalidOperationException("Discovery already started.");
            started = true;
            LinkParentCancellation(token);
            supervisor = SuperviseAsync(stop.Token);
        }
        return Task.CompletedTask;
    }

    // Kept separate so lifetime regressions can run without opening multicast sockets on a real LAN.
    internal void LinkParentCancellation(CancellationToken token)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (parentLinked) throw new InvalidOperationException("Discovery parent cancellation already linked.");
            parentCancellation = token.Register(static state => ((CancellationTokenSource)state!).Cancel(), stop);
            parentLinked = true;
        }
    }
    internal CancellationToken LifetimeToken => stop.Token;
    private async Task SuperviseAsync(CancellationToken token)
    {
        Session? session = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                IPAddress[] current;
                try
                {
                    current = addressSource().Select(Security.NetworkPolicy.Normalize)
                        .Where(a => Security.NetworkPolicy.PrivateV4(a, false)).Distinct()
                        .OrderBy(a => a.ToString(), StringComparer.Ordinal).Take(4).ToArray();
                }
                catch (System.Net.NetworkInformation.NetworkInformationException)
                { current = []; Status = "discovery_unavailable_manual_pairing_available"; }
                if (session is not null && (session.Run.IsCompleted || !session.Addresses.SequenceEqual(current)))
                { var retired = session; session = null; await RetireAsync(retired); }
                if (session is null)
                {
                    if (current.Length == 0) Status = "discovery_waiting_for_private_lan";
                    else
                    {
                        IMdnsTransport? transport = null;
                        CancellationTokenSource? sessionStop = null;
                        try
                        {
                            transport = transportFactory(current);
                            sessionStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                            Status = "discovery_active";
                            session = new(transport, current, sessionStop, RunSessionAsync(transport, current, sessionStop));
                        }
                        catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
                        {
                            if (transport is not null) await transport.DisposeAsync();
                            sessionStop?.Dispose(); Status = "discovery_unavailable_manual_pairing_available";
                        }
                    }
                }
                await Task.Delay(refreshInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (session is not null) await RetireAsync(session); }
    }

    private async Task RunSessionAsync(IMdnsTransport transport, IPAddress[] addresses, CancellationTokenSource sessionStop)
    {
        var token = sessionStop.Token;
        Task receive = Receive(transport, addresses, token), announce = Announce(transport, addresses, token);
        try
        {
            var first = await Task.WhenAny(receive, announce);
            await first;
            if (!token.IsCancellationRequested) throw new IOException("Discovery session stopped unexpectedly.");
        }
        finally
        {
            if (!token.IsCancellationRequested) Status = "discovery_unavailable_manual_pairing_available";
            sessionStop.Cancel();
            // Every failure is observed; the supervisor owns closing the socket and its linked CTS.
            try { await Task.WhenAll(receive, announce); }
            catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task RetireAsync(Session session)
    {
        var failures = new List<Exception>();
        try { session.Stop.Cancel(); } catch (Exception error) { failures.Add(error); }
        try { await session.Transport.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { await session.Run; }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { failures.Add(error); }
        try { session.Stop.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Discovery session cleanup failed.", failures);
    }

    private async Task Send(IMdnsTransport transport, IReadOnlyList<IPAddress> addresses, bool query, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try
        {
            foreach (var address in addresses)
            {
                var peer = own with { Endpoint = $"https://{address}:{new Uri(own.Endpoint).Port}/" };
                await transport.SendAsync(query ? DnsSdCodec.Query() : DnsSdCodec.Announce(peer), address, token);
            }
        }
        finally { sendGate.Release(); }
    }
    private async Task Announce(IMdnsTransport transport, IPAddress[] addresses, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        { await Send(transport, addresses, true, token); await Send(transport, addresses, false, token); await Task.Delay(announceInterval, token); }
    }
    private async Task Receive(IMdnsTransport transport, IPAddress[] addresses, CancellationToken token)
    {
        DateTimeOffset replied = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            var packet = await transport.ReceiveAsync(token);
            if (packet.RemoteEndPoint.Port != 5353 || !Security.NetworkPolicy.PrivateV4(packet.RemoteEndPoint.Address, false)) continue;
            cache.Observe(packet.Buffer, packet.RemoteEndPoint.Address, own.BrokerId);
            if (DnsSdCodec.IsQuery(packet.Buffer) && clock.GetUtcNow() - replied > TimeSpan.FromSeconds(1))
            { replied = clock.GetUtcNow(); await Send(transport, addresses, false, token); }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (lifetimeGate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        // Wait for any in-flight parent callback before disposing its target CTS. Unregistering
        // also prevents a long-lived parent from retaining this discovery's cancellation state.
        try { await parentCancellation.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { stop.Cancel(); } catch (Exception error) { failures.Add(error); }
        try { if (supervisor is not null) await supervisor; } catch (Exception error) { failures.Add(error); }
        try { stop.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { sendGate.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Discovery shutdown failed.", failures);
    }
}

internal interface IMdnsTransport : IAsyncDisposable
{
    ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken token);
    ValueTask SendAsync(byte[] packet, IPAddress source, CancellationToken token);
}

internal sealed class UdpMdnsTransport : IMdnsTransport
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private readonly UdpClient udp;
    internal UdpMdnsTransport(IReadOnlyList<IPAddress> addresses)
    {
        udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false, MulticastLoopback = false, Ttl = 255 };
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var address in addresses) udp.JoinMulticastGroup(Group, address);
        }
        catch { udp.Dispose(); throw; }
    }
    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken token) => udp.ReceiveAsync(token);
    public async ValueTask SendAsync(byte[] packet, IPAddress source, CancellationToken token)
    {
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, source.GetAddressBytes());
        await udp.SendAsync(packet, new IPEndPoint(Group, 5353), token);
    }
    public ValueTask DisposeAsync() { udp.Dispose(); return ValueTask.CompletedTask; }
}
