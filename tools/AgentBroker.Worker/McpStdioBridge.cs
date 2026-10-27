using AgentBroker.Protocol;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>Per-client stdio transport. Retrieves local connection credentials in memory on each request.</summary>
public static class McpStdioBridge
{
    private const int MaximumInputBytes = 128 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    public static async Task<int> RunAsync(string directory, Stream input, Stream output,
        CancellationToken stop = default)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute broker data directory is required.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var capacity = new SemaphoreSlim(8, 8);
        using var writer = new SemaphoreSlim(1, 1);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            MaxConnectionsPerServer = 8, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
        var active = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var pending = new List<Task>();
        string protocolVersion = "2025-11-25";
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                byte[]? frame = await ReadFrameAsync(input, lifetime.Token);
                if (frame is null) break;
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) ||
                    version.GetString() != "2.0" || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid MCP request.");
                JsonElement? id = root.TryGetProperty("id", out var requestId) ? requestId.Clone() : null;
                if (id is { ValueKind: not (JsonValueKind.String or JsonValueKind.Number) }) throw new InvalidDataException("Invalid MCP request ID.");
                if (method.GetString() == "notifications/cancelled" && root.TryGetProperty("params", out var parameters) &&
                    parameters.TryGetProperty("requestId", out var cancelled) && active.TryGetValue(cancelled.GetRawText(), out var cancellation))
                {
                    try { cancellation.Cancel(); }
                    catch (ObjectDisposedException) { /* The request completed while cancellation arrived. */ }
                }
                await capacity.WaitAsync(lifetime.Token);
                pending.RemoveAll(t => t.IsCompleted);
                var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                string? key = id?.GetRawText();
                if (key is not null && !active.TryAdd(key, deadline))
                { deadline.Dispose(); capacity.Release(); throw new InvalidDataException("Duplicate active request ID."); }
                pending.Add(ForwardAsync(frame, id, key, deadline));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
        { Console.Error.WriteLine("agentbroker-stdio: invalid_or_closed_input"); return 1; }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(pending);
        }
        return 0;

        async Task ForwardAsync(byte[] frame, JsonElement? id, string? key, CancellationTokenSource deadline)
        {
            try
            {
                LocalConnection connection = await GetConnectionAsync(directory, deadline.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, connection.McpEndpoint)
                { Content = new ByteArrayContent(frame) };
                request.Content.Headers.ContentType = new("application/json");
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                request.Headers.Add("MCP-Protocol-Version", protocolVersion);
                if (connection.BearerToken is not null) request.Headers.Authorization = new("Bearer", connection.BearerToken);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return;
                byte[] body = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token), MaximumResponseBytes, deadline.Token);
                if (body.Length == 0) return;
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    string text = new UTF8Encoding(false, true).GetString(body);
                    foreach (string entry in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        string data = string.Join("\n", entry.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart(' ')));
                        if (data.Length == 0) continue;
                        await WriteJsonAsync(Encoding.UTF8.GetBytes(data), deadline.Token);
                    }
                }
                else await WriteJsonAsync(body, deadline.Token);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or
                HttpRequestException or OperationCanceledException or UnauthorizedAccessException or DecoderFallbackException)
            {
                if (id is not null && !lifetime.IsCancellationRequested)
                {
                    byte[] error = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, error = new { code = -32001,
                        message = "AgentBroker request was not confirmed. For message submission, check its receipt before retrying the identical message ID and payload." } });
                    try { await WriteJsonAsync(error, lifetime.Token); }
                    catch (Exception) { lifetime.Cancel(); }
                }
            }
            finally
            {
                if (key is not null) active.TryRemove(key, out _);
                deadline.Dispose(); capacity.Release();
            }
        }

        async Task WriteJsonAsync(byte[] json, CancellationToken token)
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("protocolVersion", out var negotiated) && negotiated.ValueKind == JsonValueKind.String &&
                negotiated.GetString() is { Length: 10 } selected && selected.All(c => char.IsAsciiDigit(c) || c == '-'))
                protocolVersion = selected;
            // Normalize onto one stdio frame even if the HTTP implementation pretty-printed JSON.
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(parsed.RootElement);
            await writer.WaitAsync(token);
            try { await output.WriteAsync(line, token); await output.WriteAsync("\n"u8.ToArray(), token); await output.FlushAsync(token); }
            finally { writer.Release(); }
        }
    }

    // Shared with McpChannelBridge, which reaches the broker through the same validated connection.
    internal static async Task<LocalConnection> GetConnectionAsync(string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var pipe = ControlPipeSecurity.CreateClient(ControlProtocol.PipeName(directory));
        await pipe.ConnectAsync(deadline.Token);
        ControlPipeSecurity.ValidateConnectedServer(pipe);
        await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ControlRequest("connection"), ControlProtocol.Json), deadline.Token);
        await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token); await pipe.FlushAsync(deadline.Token);
        var response = JsonSerializer.Deserialize<ControlResponse>(await LocalControlServer.ReadFrameAsync(pipe, deadline.Token), ControlProtocol.Json);
        var connection = response?.Success == true ? response.Connection : null;
        if (connection is null || !Uri.TryCreate(connection.McpEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/mcp" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            connection.BearerToken is not null && (connection.BearerToken.Length != 64 || !connection.BearerToken.All(char.IsAsciiHexDigit)))
            throw new IOException("Invalid local broker connection.");
        return connection;
    }

    internal static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken token)
    {
        using var body = new MemoryStream(); var one = new byte[1];
        while (await input.ReadAsync(one, token) == 1)
        {
            if (one[0] == (byte)'\n')
            { if (body.Length == 0) continue; return body.ToArray(); }
            if (body.Length >= MaximumInputBytes) throw new InvalidDataException("MCP input exceeds its bound.");
            body.WriteByte(one[0]);
        }
        if (body.Length != 0) throw new InvalidDataException("Incomplete MCP stdio frame.");
        return null;
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        { if (result.Length + read > maximum) throw new InvalidDataException("MCP response exceeds its bound."); await result.WriteAsync(buffer.AsMemory(0, read), token); }
        return result.ToArray();
    }
}
