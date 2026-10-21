using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

internal interface IBrokerControlClient
{
    Task<ControlResponse> SendAsync(string dataDirectory, ControlRequest request, CancellationToken token, int? requiredServerPid = null);
}

internal sealed class BrokerControlClient : IBrokerControlClient
{
    public async Task<ControlResponse> SendAsync(string dataDirectory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = ControlPipeSecurity.CreateClient(ControlProtocol.PipeName(dataDirectory));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        ControlPipeSecurity.ValidateConnectedServer(pipe, requiredServerPid);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, ControlProtocol.Json);
        if (payload.Length > ControlProtocol.MaximumFrameBytes) throw new IOException("control_frame_too_large");
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var body = new MemoryStream();
        byte[] one = new byte[1];
        while (true)
        {
            if (await pipe.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0) throw new IOException("control_eof");
            if (one[0] == (byte)'\n') break;
            if (body.Length == ControlProtocol.MaximumFrameBytes) throw new IOException("control_frame_too_large");
            body.WriteByte(one[0]);
        }
        return JsonSerializer.Deserialize<ControlResponse>(body.ToArray(), ControlProtocol.Json)
            ?? throw new IOException("control_invalid_response");
    }
}
