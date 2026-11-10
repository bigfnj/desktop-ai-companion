using AgentBroker.Protocol;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker;

/// <summary>One-request client for hosts that do not expose AgentBroker MCP tools.</summary>
public static class McpCallClient
{
    public const string Command = "--mcp-call";
    private const int MaximumRequestBytes = 128 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const string ProtocolVersion = "2025-11-25";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    public sealed record Options(string DataDirectory, string? RequestFile, bool ListTools, string? OutputFile = null)
    {
        public static Options Parse(string[] arguments)
        {
            string? directory = null, request = null, output = null;
            bool list = false;
            if (arguments.Length == 0 || arguments[0] != Command) throw new ArgumentException("invalid_arguments");
            for (int i = 1; i < arguments.Length; i++)
            {
                if (arguments[i] == "--list-tools" && !list) { list = true; continue; }
                if (i + 1 >= arguments.Length) throw new ArgumentException("invalid_arguments");
                switch (arguments[i])
                {
                    case "--data" when directory is null: directory = arguments[++i]; break;
                    case "--request-file" when request is null: request = arguments[++i]; break;
                    case "--output-file" when output is null: output = arguments[++i]; break;
                    default: throw new ArgumentException("invalid_arguments");
                }
            }
            if (directory is null || !Path.IsPathFullyQualified(directory) ||
                (list ? request is not null : request is null) ||
                request is not null && !Path.IsPathFullyQualified(request) ||
                output is not null && !Path.IsPathFullyQualified(output))
                throw new ArgumentException("invalid_arguments");
            return new(directory, request, list, output);
        }
    }

    public static async Task<int> RunCommandAsync(string[] arguments, Stream output, CancellationToken cancellation = default)
    {
        Options options;
        try { options = Options.Parse(arguments); }
        catch (ArgumentException)
        {
            return await WriteAsync(output, new { status = "invalid_arguments", dispatchState = "not_dispatched", code = "invalid_arguments" }, 2, cancellation);
        }
        return await RunAsync(options, output, cancellation);
    }

    // Credentials come from the existing verified control pipe, remain in memory, and are never output.
    // Business message IDs come from the request unchanged. No submitted operation is retried.
    public static Task<int> RunAsync(Options options, Stream output, CancellationToken cancellation = default) =>
        RunAsync(options, output, null, cancellation);

    // The same call with LocalOutputFile test seams, passed per call so the exit-74 path through this catch site is
    // reachable in process. Production passes null.
    internal static async Task<int> RunAsync(Options options, Stream output, LocalOutputFile.Seams? seams,
        CancellationToken cancellation = default)
    {
        if (options.OutputFile is null) return await RunCoreAsync(options, output, null, cancellation);
        try { LocalOutputFile.Validate(options.OutputFile, seams); }
        catch (Exception error) when (OutputFailure(error))
        {
            return await WriteAsync(output, new { status = "output_error", dispatchState = "not_dispatched",
                retained = false, code = "output_unavailable", nextAction = "Choose a new local file in an existing plain directory; no tool was dispatched." }, 74, CancellationToken.None);
        }

        using var original = new MemoryStream();
        int code = await RunCoreAsync(options, original, null, cancellation);
        byte[] bytes = original.ToArray();
        using var document = JsonDocument.Parse(bytes);
        LocalOutputFile.Retention retention;
        try
        {
            retention = LocalOutputFile.Save(options.OutputFile, bytes, seams: seams);
        }
        catch (Exception error) when (OutputFailure(error))
        {
            // The broker response is known even if the file could not be retained. Never turn a confirmed send into
            // transport uncertainty or replay it because its output path raced, filled up, or became unwritable.
            return await WriteAsync(output, OutputError(error, document.RootElement), 74, CancellationToken.None);
        }
        var response = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        response.Add("retention", JsonSerializer.SerializeToElement(retention, Json));
        return await WriteAsync(output, response, code, CancellationToken.None);
    }

    private static bool OutputFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or System.Security.SecurityException;

    // The post-dispatch exit-74 body. A temporary this call could not remove through its own handle is reported, never
    // hidden; otherwise the body is byte-identical to the 1.10.0 one (no null member).
    internal static object OutputError(Exception error, JsonElement response) => LocalOutputFile.CleanupReport(error) is { } cleanup
        ? new { status = "output_error", dispatchState = response.GetProperty("dispatchState").GetString(), retained = false,
            code = "output_unavailable", cleanup, response,
            nextAction = "Retain this response using a new local file. Do not repeat a confirmed operation because retention failed." }
        : new { status = "output_error", dispatchState = response.GetProperty("dispatchState").GetString(), retained = false,
            code = "output_unavailable", response,
            nextAction = "Retain this response using a new local file. Do not repeat a confirmed operation because retention failed." };

    // Purpose-scoped local integrations reuse exactly the authenticated transport and result/exit contract, without
    // writing request files. This is internal and exposes no extra CLI operation or credential path.
    internal static Task<int> RunToolAsync(string directory, string tool, JsonElement arguments, Stream output,
        CancellationToken cancellation) => RunCoreAsync(new(directory, null, false), output,
            JsonSerializer.SerializeToElement(new { tool, arguments }, Json), cancellation);

    private static async Task<int> RunCoreAsync(Options options, Stream output, JsonElement? memoryRequest, CancellationToken cancellation)
    {
        var response = await RequestEnvelopeAsync(options, memoryRequest, cancellation);
        // Publication is local output, after transport classification has finished. Caller cancellation or a failed
        // output stream must never downgrade a known response or append a second transport-error envelope.
        return await WriteAsync(output, response.Value, response.ExitCode, CancellationToken.None);
    }

    private static async Task<(object Value, int ExitCode)> RequestEnvelopeAsync(Options options, JsonElement? memoryRequest, CancellationToken cancellation)
    {
        bool toolDispatchStarted = false;
        bool requestValidated = options.ListTools;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            JsonElement? arguments = null;
            string? tool = null;
            if (!options.ListTools)
            {
                using var document = memoryRequest is { } prepared
                    ? JsonDocument.Parse(prepared.GetRawText(), new JsonDocumentOptions { MaxDepth = 32 })
                    : await ReadRequestAsync(options.RequestFile!, deadline.Token);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.EnumerateObject().Any(property => property.Name is not ("tool" or "arguments")) ||
                    !root.TryGetProperty("tool", out var name) || name.ValueKind != JsonValueKind.String ||
                    name.GetString() is not { Length: > 0 and <= 80 } selected ||
                    selected.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
                    throw new InvalidDataException("invalid_request");
                tool = selected;
                arguments = root.TryGetProperty("arguments", out var supplied) ? supplied.Clone() : JsonSerializer.SerializeToElement(new { });
                if (arguments.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("invalid_request");
            }
            requestValidated = true;
            LocalConnection connection = await McpStdioBridge.GetConnectionAsync(options.DataDirectory, deadline.Token);
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            }) { Timeout = Timeout.InfiniteTimeSpan };
            string prefix = "agentbroker-call-" + Guid.NewGuid().ToString("N");
            var initialized = await ExchangeAsync("initialize", new
            {
                protocolVersion = ProtocolVersion, capabilities = new { },
                clientInfo = new { name = "agentbroker-cli", version = "1.0" },
            }, prefix + "-initialize");
            if (initialized.TryGetProperty("error", out var initializationError))
                return (new { status = "rpc_error", dispatchState = "not_dispatched", error = initializationError }, 11);
            if (!initialized.TryGetProperty("result", out var initialization) || initialization.ValueKind != JsonValueKind.Object ||
                !initialization.TryGetProperty("protocolVersion", out var negotiated) ||
                negotiated.ValueKind != JsonValueKind.String || negotiated.GetString() != ProtocolVersion)
                throw new InvalidDataException("protocol_unavailable");
            await ExchangeAsync("notifications/initialized", new { }, null);

            toolDispatchStarted = true; // Later transport failure cannot prove whether the tool acted.
            var reply = options.ListTools
                ? await ExchangeAsync("tools/list", new { }, prefix + "-query")
                : await ExchangeAsync("tools/call", new { name = tool, arguments }, prefix + "-query");
            if (reply.TryGetProperty("error", out var rpcError))
                return (new { status = "rpc_error", dispatchState = "confirmed", error = rpcError }, 11);
            if (!reply.TryGetProperty("result", out var result)) throw new InvalidDataException("response_unavailable");
            if (options.ListTools)
                return (new { status = "ok", dispatchState = "confirmed", result,
                    instructions = initialization.TryGetProperty("instructions", out var card) && card.ValueKind == JsonValueKind.String
                        ? card.GetString() : null }, 0);
            if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("response_unavailable");
            bool failed = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (result.TryGetProperty("isError", out flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("response_unavailable");
            var decoded = DecodeToolResult(result);
            return (new
            {
                status = failed ? "tool_error" : "ok", dispatchState = "confirmed",
                result = decoded.Value, advisories = decoded.Advisories, structuredContent = decoded.Structured,
            }, failed ? 10 : 0);

            async Task<JsonElement> ExchangeAsync(string method, object parameters, string? id)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, connection.McpEndpoint);
                message.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
                message.Headers.Accept.ParseAdd("application/json, text/event-stream");
                if (connection.BearerToken is not null) message.Headers.Authorization = new("Bearer", connection.BearerToken);
                byte[] body = id is null
                    ? JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters }, Json)
                    : JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }, Json);
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.ContentType = new("application/json");
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                if (id is null && (response.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.NoContent ||
                    response.Content.Headers.ContentLength == 0)) return default;
                if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("response_unavailable");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                byte[] bytes = await McpStdioBridge.ReadBoundedAsync(stream, MaximumResponseBytes, deadline.Token);
                return DecodeWire(bytes, response.Content.Headers.ContentType?.MediaType, id!);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            HttpRequestException or OperationCanceledException or DecoderFallbackException or ArgumentException)
        {
            bool invalidRequest = !requestValidated && error is not OperationCanceledException;
            string status = invalidRequest ? "invalid_request" : error is HttpRequestException { StatusCode: not null }
                ? "http_error" : "transport_error";
            return (new
            {
                status, dispatchState = toolDispatchStarted ? "unknown" : "not_dispatched",
                code = invalidRequest ? "invalid_request" : "broker_request_unconfirmed",
                httpStatus = error is HttpRequestException { StatusCode: { } httpStatus } ? (int?)httpStatus : null,
                nextAction = toolDispatchStarted
                    ? "Check the receipt before retrying a message with its identical stable ID and payload."
                    : "Check the request and broker connection; the tool was not dispatched.",
            }, invalidRequest ? 2 : status == "http_error" ? 12 : 69);
        }
    }

    private static async Task<JsonDocument> ReadRequestAsync(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("invalid_request");
        string full = Path.GetFullPath(path);
        for (string? component = full; component is not null; component = Path.GetDirectoryName(component))
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("invalid_request");
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var bytes = await McpStdioBridge.ReadBoundedAsync(stream, MaximumRequestBytes, token);
        var document = JsonDocument.Parse(McpStdioBridge.WithoutUtf8Signature(bytes), new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            var pending = new Stack<JsonElement>();
            pending.Push(document.RootElement);
            int count = 0;
            while (pending.Count > 0)
            {
                if (++count > 8192) throw new InvalidDataException("invalid_request");
                var element = pending.Pop();
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in element.EnumerateObject())
                    {
                        // A property NAME is transcoded lazily: invalid UTF-8 or an escaped lone surrogate throws here.
                        string name;
                        try { name = property.Name; }
                        catch (InvalidOperationException) { throw new InvalidDataException("invalid_request"); }
                        if (!names.Add(name)) throw new InvalidDataException("invalid_request");
                        pending.Push(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var value in element.EnumerateArray()) pending.Push(value);
                else if (element.ValueKind == JsonValueKind.String)
                {
                    // String values decode lazily too. Refuse malformed tool or nested argument text at this bounded
                    // local input boundary, before connection/dispatch or later JsonElement serialization.
                    try { _ = element.GetString(); }
                    catch (InvalidOperationException error) { throw new InvalidDataException("invalid_request", error); }
                }
            }
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    public static JsonElement DecodeWire(byte[] bytes, string? mediaType, string expectedId)
    {
        var matches = new List<JsonElement>();
        if (bytes.Length > MaximumResponseBytes) throw new InvalidDataException("response_unavailable");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (mediaType == "application/json") Add(text);
        else if (mediaType == "text/event-stream")
        {
            string[] frames = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            if (frames.Length > 64) throw new InvalidDataException("response_unavailable");
            foreach (string frame in frames)
            {
                string data = string.Join("\n", frame.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart(' ')));
                if (data.Length > 0) Add(data);
            }
        }
        else throw new InvalidDataException("response_unavailable");
        if (matches.Count != 1) throw new InvalidDataException("response_unavailable");
        return matches[0];

        void Add(string json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var protocol) ||
                protocol.ValueKind != JsonValueKind.String || protocol.GetString() != "2.0")
                throw new InvalidDataException("response_unavailable");
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == expectedId)
                matches.Add(root.Clone());
        }
    }

    public sealed record DecodedResult(JsonElement Value, IReadOnlyList<string> Advisories, JsonElement? Structured);

    public static DecodedResult DecodeToolResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("response_unavailable");
        JsonElement? structured = result.TryGetProperty("structuredContent", out var supplied) ? supplied.Clone() : null;
        var texts = new List<string>();
        if (result.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var block in blocks.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    texts.Add(text.GetString()!);
        JsonElement value;
        if (texts.Count > 0)
        {
            try { using var parsed = JsonDocument.Parse(texts[0], new JsonDocumentOptions { MaxDepth = 32 }); value = parsed.RootElement.Clone(); }
            catch (JsonException) { value = JsonSerializer.SerializeToElement(texts[0]); }
        }
        else value = structured ?? JsonSerializer.SerializeToElement<object?>(null);
        return new(value, texts.Skip(1).ToArray(), structured);
    }

    private static async Task<int> WriteAsync(Stream output, object value, int exitCode, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await output.WriteAsync(bytes, token);
        await output.WriteAsync("\n"u8.ToArray(), token);
        await output.FlushAsync(token);
        return exitCode;
    }
}
