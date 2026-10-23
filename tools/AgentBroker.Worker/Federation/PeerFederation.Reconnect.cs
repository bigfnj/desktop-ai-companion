using AgentBroker.Core;
using AgentBroker.Worker.Security;

namespace AgentBroker.Worker.Federation;

public sealed partial class PeerFederation
{
    internal Func<IReadOnlyList<BrokerAdvertisement>>? DiscoverySource { get; init; }
    internal TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? reconnectStop;
    private Task? reconnect;
    private int nextReconnectPeer;

    private void StartReconnect(CancellationToken token)
    {
        reconnectStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        reconnect = ReconnectLoopAsync(reconnectStop.Token);
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReconnectDiscoveredAsync(token);
                await Task.Delay(ReconnectInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task ReconnectDiscoveredAsync(CancellationToken token)
    {
        if (!options.EnableLan || (!enableDiscovery && DiscoverySource is null)) return;
        var peers = store.Peers();
        var candidates = Discovered.Take(32).Select(advertisement => (Advertisement: advertisement,
            Peer: peers.FirstOrDefault(p => p.BrokerId == advertisement.BrokerId &&
                p.CertificateSha256.Equals(advertisement.CertificateSha256, StringComparison.OrdinalIgnoreCase) &&
                p.Endpoint != advertisement.Endpoint))).Where(p => p.Peer is not null).ToArray();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var batch = Enumerable.Range(0, Math.Min(4, candidates.Length))
            .Select(i => candidates[(nextReconnectPeer + i) % candidates.Length]).ToArray();
        if (candidates.Length != 0) nextReconnectPeer = (nextReconnectPeer + batch.Length) % candidates.Length;
        try
        {
            await Parallel.ForEachAsync(batch, new ParallelOptions
            { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (candidate, stop) =>
            {
                try
                {
                    NetworkPolicy.Advertisement(candidate.Advertisement);
                    PeerTrust peer = candidate.Peer!;
                    var authorization = store.CapturePeerAuthorization(peer);
                    if (authorization is null) return;
                    // Multihomed peers may advertise several addresses in turn. Keep a working
                    // saved address instead of rewriting durable state on each announcement.
                    using (var currentDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        currentDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            using var currentClient = Client(Advert(peer));
                            var currentInfo = await Exchange<BrokerAdvertisement>(currentClient, HttpMethod.Get, "broker/info", null, currentDeadline.Token);
                            NetworkPolicy.Advertisement(currentInfo);
                            if (SameIdentity(currentInfo, Advert(peer))) return;
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                            HttpRequestException or System.Text.Json.JsonException or OperationCanceledException) { stop.ThrowIfCancellationRequested(); }
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    using var client = Client(candidate.Advertisement);
                    var info = await Exchange<BrokerAdvertisement>(client, HttpMethod.Get, "broker/info", null, deadline.Token);
                    NetworkPolicy.Advertisement(info);
                    if (!SameIdentity(info, candidate.Advertisement)) return;
                    deadline.Token.ThrowIfCancellationRequested();
                    store.TryRefreshPeerEndpoint(peer, authorization, info.Endpoint);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                    HttpRequestException or System.Text.Json.JsonException or BrokerException or OperationCanceledException)
                { stop.ThrowIfCancellationRequested(); }
            });
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
    }

    private async Task StopReconnectAsync()
    {
        if (reconnectStop is null) return;
        reconnectStop.Cancel();
        try { if (reconnect is not null) await reconnect; }
        finally { reconnectStop.Dispose(); reconnectStop = null; }
    }
}
