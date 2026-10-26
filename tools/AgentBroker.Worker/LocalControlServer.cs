using AgentBroker.Core;
using AgentBroker.Protocol;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>Local administration never shares the LAN listener or its trust domain.</summary>
public sealed class LocalControlServer(WorkerApplication worker) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task[] listeners = [];
    public void Start()
    {
        if (listeners.Length != 0) throw new InvalidOperationException("Local control already started.");
        listeners = Enumerable.Range(0, 4).Select(_ => ListenAsync(stop.Token)).ToArray();
    }
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = ControlPipeSecurity.CreateServer(worker.Options.ControlPipeName);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                ControlResponse response;
                ControlRequest? request = null;
                bool shouldStop = false;
                try
                {
                    request = JsonSerializer.Deserialize<ControlRequest>(await ReadFrameAsync(pipe, deadline.Token),
                        ControlProtocol.Json) ?? throw new InvalidDataException("Empty control request.");
                    deadline.CancelAfter(ControlProtocol.ServerTimeout(request.Operation));
                    response = await HandleAsync(request, deadline.Token);
                    shouldStop = request.Operation == "stop" && response.Success;
                }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException or
                    IOException or UnauthorizedAccessException or BrokerException or HttpRequestException or OperationCanceledException)
                { response = new(false, e is BrokerException b ? b.Code : ControlProtocol.FailureCode(request?.Operation, e)); }
                byte[] frame = JsonSerializer.SerializeToUtf8Bytes(response, ControlProtocol.Json);
                if (frame.Length > ControlProtocol.MaximumFrameBytes)
                    frame = JsonSerializer.SerializeToUtf8Bytes(new ControlResponse(false, "control_response_limit"), ControlProtocol.Json);
                await pipe.WriteAsync(frame, deadline.Token);
                await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
                await pipe.FlushAsync(deadline.Token);
                if (shouldStop) worker.Application.Lifetime.StopApplication();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
        }
    }
    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken token)
    {
        switch (request.Operation)
        {
            case "status": return new(true, "ok", Snapshot());
            case "peer-diagnostics":
                return new(true, "ok", PeerDiagnostics: await PeerDiagnostics.CollectAsync(worker.Runtime, token));
            case "connection":
                return new(true, "ok", Connection: new(LoopbackEndpoint() + "/mcp",
                    worker.Options.AllowUnauthenticatedLoopback ? null : worker.LocalAccessToken));
            case "pair-request" when request.Peer is { } peer:
                await worker.Runtime.Federation.RequestPairAsync(new(peer.BrokerId, peer.Name, peer.Endpoint,
                    peer.CertificateSha256, DateTimeOffset.UtcNow), token); break;
            case "pair-approve" when request.Id != Guid.Empty:
                await worker.Runtime.Federation.ApprovePairAsync(request.Id, token); break;
            case "pair-reject" when request.Id != Guid.Empty:
                worker.Runtime.Federation.RejectPair(request.Id); break;
            case "peer-revoke" when request.Id != Guid.Empty:
                worker.Runtime.Federation.RevokePeer(request.Id); break;
            case "session-private" when request.SessionId is not null:
                worker.Runtime.SetSessionPreference(request.SessionId, request.Value, null); break;
            case "session-muted" when request.SessionId is not null:
                worker.Runtime.SetSessionPreference(request.SessionId, null, request.Value); break;
            case "stop": return new(true, "stopping");
            default: return new(false, "unknown_control_operation");
        }
        return new(true, "ok", Snapshot());
    }
    private string LoopbackEndpoint() => worker.Application.Urls.FirstOrDefault(u => u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
        ?? "http://127.0.0.1:" + worker.Options.LoopbackPort;
    public BrokerSnapshot Snapshot()
    {
        var runtime = worker.Runtime;
        return new(runtime.Store.BrokerId, worker.Options.Name, LoopbackEndpoint(),
            worker.Options.EnableLan ? runtime.Federation.LocalAdvertisement.Endpoint : null,
            runtime.Federation.Discovered.Take(32).Select(Peer).ToArray(),
            runtime.Store.Peers().Select(p => new PeerView(p.BrokerId, p.Name, p.Endpoint, p.CertificateSha256)).ToArray(),
            runtime.Federation.PairingOffers.Take(32).Select(p => new PairingView(p.RequestId, Peer(p.Peer),
                p.VerificationCode, p.ExpiresAt, p.Outbound, p.LocalApproved, p.RemoteApproved)).ToArray(),
            runtime.Store.Directory().Select(s => new SessionView(s.Address.SessionId, s.Address.Incarnation,
                s.Label, s.Adapter, s.Activity.ToString(), s.Capabilities.HasFlag(SessionCapabilities.Receive),
                s.Capabilities.HasFlag(SessionCapabilities.WakeIdle), s.IsPrivate, runtime.IsMuted(s.Address.SessionId))).ToArray(),
            new Dictionary<string, string>(runtime.AdapterErrors, StringComparer.Ordinal),
            worker.Options.EnableLan ? runtime.Federation.LocalAdvertisement.CertificateSha256 : null, worker.McpStatus);
    }
    private static PeerView Peer(BrokerAdvertisement peer) => new(peer.BrokerId, peer.Name, peer.Endpoint, peer.CertificateSha256);
    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        using var frame = new MemoryStream();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, token) == 1)
        {
            if (buffer[0] == (byte)'\n')
            {
                if (frame.Length == 0) throw new InvalidDataException("Empty control frame.");
                return frame.ToArray();
            }
            if (frame.Length >= ControlProtocol.MaximumFrameBytes) throw new InvalidDataException("Control frame exceeds limit.");
            frame.WriteByte(buffer[0]);
        }
        throw new IOException("Incomplete control frame.");
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await Task.WhenAll(listeners); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        stop.Dispose();
    }
}
