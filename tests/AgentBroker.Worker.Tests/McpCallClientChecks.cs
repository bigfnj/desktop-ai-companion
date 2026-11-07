using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using AgentBroker.Protocol;
using Microsoft.AspNetCore.Builder;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class McpCallClientChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: MCP_CALL_CLIENT_EXECUTED");
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "mcp-call-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string request = Path.Combine(scratch, "request.json");
        var parsed = McpCallClient.Options.Parse(["--mcp-call", "--data", scratch, "--list-tools"]);
        check(parsed.ListTools && parsed.RequestFile is null, "MCP_CALL_OPTIONS: explicit tool discovery has no request file");
        foreach (string[] bad in new[]
        {
            new[] { "--mcp-call" },
            new[] { "--mcp-call", "--data", "relative", "--list-tools" },
            new[] { "--mcp-call", "--data", scratch, "--request-file", request, "--list-tools" },
            new[] { "--mcp-call", "--data", scratch, "--list-tools", "--data", scratch },
        })
        {
            using var output = new MemoryStream();
            int code = await McpCallClient.RunCommandAsync(bad, output);
            using var json = JsonDocument.Parse(output.ToArray());
            check(code == 2 && json.RootElement.GetProperty("dispatchState").GetString() == "not_dispatched",
                "MCP_CALL_BAD_OPTIONS: malformed arguments produce one JSON failure before dispatch");
        }

        var wire = new { jsonrpc = "2.0", id = "witness", result = new { value = "line\nC:\\literal\\file\n雪🙂" } };
        string encoded = JsonSerializer.Serialize(wire);
        foreach (var (body, type) in new[] { (encoded, "application/json"), ("event: message\r\ndata: " + encoded + "\r\n\r\n", "text/event-stream") })
        {
            var value = McpCallClient.DecodeWire(Encoding.UTF8.GetBytes(body), type, "witness");
            check(value.GetProperty("result").GetProperty("value").GetString() == wire.result.value,
                "MCP_CALL_WIRE_TEXT: JSON and SSE preserve actual newlines, backslashes and Unicode");
        }
        foreach (string body in new[] { encoded.Replace("witness", "other", StringComparison.Ordinal),
            "data: " + encoded + "\n\ndata: " + encoded + "\n\n" })
        {
            bool refused = false;
            try { McpCallClient.DecodeWire(Encoding.UTF8.GetBytes(body), body.StartsWith("data:", StringComparison.Ordinal) ? "text/event-stream" : "application/json", "witness"); }
            catch (InvalidDataException) { refused = true; }
            check(refused, "MCP_CALL_WIRE_ID: missing or duplicate matching response is refused");
        }
        foreach (object scalar in new object[] { "", Array.Empty<string>(), new[] { "x\nC:\\x", "雪" }, false })
        {
            var result = JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(scalar) } } });
            var decoded = McpCallClient.DecodeToolResult(result);
            check(decoded.Value.GetRawText() == JsonSerializer.Serialize(scalar),
                "MCP_CALL_DECODE_SCALAR: empty strings, arrays, Unicode and booleans keep their JSON types");
        }
        var advisory = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "{\"receipt\":true}" }, new { type = "text", text = "pending mail" } },
            structuredContent = new { pending = 2 },
        });
        var retained = McpCallClient.DecodeToolResult(advisory);
        check(retained.Value.GetProperty("receipt").GetBoolean() && retained.Advisories.SequenceEqual(["pending mail"]) &&
            retained.Structured!.Value.GetProperty("pending").GetInt32() == 2,
            "MCP_CALL_ADVISORIES: first decoded result, advisory and structured data are all retained");

        var options = new WorkerOptions(scratch, "Synthetic CLI worker", LoopbackPort: 0, LanPort: 0,
            EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false };
        WorkerApplication? worker = null;
        string? keyName = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            async Task<(int Code, JsonElement Value)> Child(params string[] arguments)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                start.ArgumentList.Add(typeof(WorkerApplication).Assembly.Location);
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("CLI process did not start.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
                string text = await stdout;
                string errors = await stderr;
                check(errors.Length == 0 && text.Count(character => character == '\n') == 1,
                    "MCP_CALL_PROCESS_FRAME: actual CLI emits exactly one JSON line and no stderr");
                using var document = JsonDocument.Parse(text);
                return (process.ExitCode, document.RootElement.Clone());
            }
            using (var output = new MemoryStream())
            {
                int code = await McpCallClient.RunAsync(new(scratch, null, true), output);
                using var result = JsonDocument.Parse(output.ToArray());
                check(code == 0 && result.RootElement.GetProperty("status").GetString() == "ok" &&
                    result.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0 &&
                    result.RootElement.GetProperty("instructions").GetString() == BrokerInstructions.Text,
                    "MCP_CALL_LIVE_DISCOVERY: actual authenticated worker serves decoded tool schemas");
                check(!Encoding.UTF8.GetString(output.ToArray()).Contains(worker.LocalAccessToken, StringComparison.Ordinal),
                    "MCP_CALL_TOKEN_PRIVATE: protected-pipe connection token never enters JSON output");
            }
            using (var output = new MemoryStream())
            {
                int code = await McpCallClient.RunToolAsync(scratch, "list_sessions", JsonSerializer.SerializeToElement(new { }), output, default);
                using var result = JsonDocument.Parse(output.ToArray());
                check(code == 0 && result.RootElement.GetProperty("status").GetString() == "ok" &&
                    result.RootElement.GetProperty("result").ValueKind == JsonValueKind.Array && !File.Exists(request) &&
                    !Encoding.UTF8.GetString(output.ToArray()).Contains(worker.LocalAccessToken, StringComparison.Ordinal),
                    "MCP_CALL_IN_MEMORY: purpose-scoped calls use the authenticated result contract without request files or exposed credentials");
            }
            var childDiscovery = await Child("--mcp-call", "--data", scratch, "--list-tools");
            check(childDiscovery.Code == 0 && childDiscovery.Value.GetProperty("result").GetProperty("tools").GetArrayLength() > 0,
                "MCP_CALL_PROCESS_DISCOVERY: actual Worker entry point executes the supported client mode");
            var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic CLI sender", true);
            var recipient = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic CLI recipient", true);
            string text = "actual first line\nC:\\literal\\path\n雪🙂";
            var sent = worker.Runtime.Store.Send(sender.Address, recipient.Address, Guid.NewGuid(), text, null, 0, worker.Runtime.Store.BrokerId);
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { tool = "read_inbox", arguments = new { session = recipient.Address } }), new UTF8Encoding(false));
            using (var output = new MemoryStream())
            {
                int code = await McpCallClient.RunAsync(new(scratch, request, false), output);
                using var result = JsonDocument.Parse(output.ToArray());
                var message = result.RootElement.GetProperty("result")[0].GetProperty("message");
                check(code == 0 && message.GetProperty("id").GetGuid() == sent.Message.Id &&
                    message.GetProperty("text").GetString() == text,
                    "MCP_CALL_LIVE_INBOX: actual HTTP tool response round-trips exact message text");
            }
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { tool = "wait_for_messages", arguments = new
            { session = new SessionAddress(worker.Runtime.Store.BrokerId, "mcp:" + Guid.NewGuid(), Guid.NewGuid()), timeoutSeconds = 0 } }), new UTF8Encoding(false));
            using (var output = new MemoryStream())
            {
                int code = await McpCallClient.RunAsync(new(scratch, request, false), output);
                using var result = JsonDocument.Parse(output.ToArray());
                check(code == 10 && result.RootElement.GetProperty("status").GetString() == "tool_error" &&
                    result.RootElement.GetProperty("dispatchState").GetString() == "confirmed",
                    "MCP_CALL_TOOL_ERROR_EXIT: an MCP isError result cannot masquerade as process success");
            }
            var childFailure = await Child("--mcp-call", "--data", scratch, "--request-file", request);
            check(childFailure.Code == 10 && childFailure.Value.GetProperty("status").GetString() == "tool_error",
                "MCP_CALL_PROCESS_FAILURE: actual process exit code reflects MCP tool failure");
            var childArguments = await Child("--mcp-call");
            check(childArguments.Code == 2 && childArguments.Value.GetProperty("dispatchState").GetString() == "not_dispatched",
                "MCP_CALL_PROCESS_ARGUMENTS: actual malformed invocation returns JSON and exit two");
            await File.WriteAllTextAsync(request, "{\"tool\":", new UTF8Encoding(false));
            var malformed = await Child("--mcp-call", "--data", scratch, "--request-file", request);
            check(malformed.Code == 2 && malformed.Value.GetProperty("status").GetString() == "invalid_request" &&
                malformed.Value.GetProperty("dispatchState").GetString() == "not_dispatched",
                "MCP_CALL_JSON_SYNTAX: invalid JSON is a request failure before any connection or dispatch");
            await File.WriteAllTextAsync(request, "{\"tool\":\"list_sessions\",\"arguments\":{\"limit\":1,\"limit\":2}}", new UTF8Encoding(false));
            using (var output = new MemoryStream())
            {
                int code = await McpCallClient.RunAsync(new(scratch, request, false), output);
                using var result = JsonDocument.Parse(output.ToArray());
                check(code == 2 && result.RootElement.GetProperty("dispatchState").GetString() == "not_dispatched",
                    "MCP_CALL_DUPLICATE_KEY: duplicate nested request keys are refused before tool dispatch");
            }
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
        await WireContractsAsync(check);
    }

    private static async Task WireContractsAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: MCP_CALL_PROCESS_WIRE_EXECUTED");
        await using var wire = new WireFixture();
        const string text = "first actual line\nC:\\literal\\path\n雪🙂";
        foreach (var mode in Enum.GetValues<WireMode>())
        {
            wire.Begin(mode);
            Guid stableId = Guid.NewGuid();
            await File.WriteAllTextAsync(wire.RequestFile, mode == WireMode.MalformedRequest ? "{" :
                JsonSerializer.Serialize(new { tool = "send", arguments = new { messageId = stableId, text } }), new UTF8Encoding(false));
            var child = await RunWireChildAsync(wire.Root, wire.RequestFile, list: mode == WireMode.Discovery, check);
            var value = child.Value;
            check(!child.Raw.Contains(wire.Token, StringComparison.Ordinal),
                "MCP_CALL_PROCESS_TOKEN_PRIVATE: a real protected-pipe token never enters child output");
            if (mode == WireMode.MalformedRequest)
            {
                check(child.Code == 2 && value.GetProperty("status").GetString() == "invalid_request" &&
                    value.GetProperty("dispatchState").GetString() == "not_dispatched" && wire.Methods.Count == 0,
                    "MCP_CALL_PROCESS_INVALID_JSON: malformed syntax exits two before connecting or dispatching");
                continue;
            }
            check(wire.AllAuthorized, "MCP_CALL_PROCESS_AUTHENTICATION: actual HTTP requests use credentials obtained through the protected pipe");
            if (mode is WireMode.InitializeRpcError or WireMode.BadProtocol)
            {
                check(child.Code == (mode == WireMode.InitializeRpcError ? 11 : 69) && wire.ToolCalls == 0 &&
                    value.GetProperty("dispatchState").GetString() == "not_dispatched",
                    "MCP_CALL_PROCESS_INITIALIZATION: RPC refusal and incompatible protocol keep truthful pre-dispatch exits");
                continue;
            }
            check(wire.ToolCalls == 1 && wire.Methods.SequenceEqual(["initialize", "notifications/initialized", mode == WireMode.Discovery ? "tools/list" : "tools/call"]),
                "MCP_CALL_PROCESS_ONCE: initialize, initialized notification and one requested call occur with no retry");
            if (mode != WireMode.Discovery)
                check(wire.LastArguments is { } arguments && arguments.GetProperty("messageId").GetGuid() == stableId &&
                    arguments.GetProperty("text").GetString() == text,
                    "MCP_CALL_PROCESS_REQUEST_ROUNDTRIP: the actual request preserves its stable ID, newlines, backslashes and Unicode");
            switch (mode)
            {
                case WireMode.Json:
                case WireMode.Sse:
                    var result = value.GetProperty("result");
                    check(child.Code == 0 && value.GetProperty("status").GetString() == "ok" &&
                        value.GetProperty("dispatchState").GetString() == "confirmed" && result.GetProperty("text").GetString() == text &&
                        result.GetProperty("empty").GetString() == "" && result.GetProperty("emptyArray").GetArrayLength() == 0 &&
                        result.GetProperty("single").GetArrayLength() == 1,
                        "MCP_CALL_PROCESS_JSON_SSE: real JSON and multiline SSE responses retain exact typed payloads");
                    check(value.GetProperty("advisories").GetArrayLength() == 1 &&
                        value.GetProperty("advisories")[0].GetString() == WireFixture.Advisory &&
                        value.GetProperty("structuredContent").GetProperty("pending").GetInt32() == 2,
                        "MCP_CALL_PROCESS_ADVISORIES: later JSON-shaped hints cannot replace the first payload or erase structured data");
                    break;
                case WireMode.EmptyArray:
                    check(child.Code == 0 && value.GetProperty("result").ValueKind == JsonValueKind.Array && value.GetProperty("result").GetArrayLength() == 0,
                        "MCP_CALL_PROCESS_EMPTY_ARRAY: an actual empty-array result remains a JSON array");
                    break;
                case WireMode.Singleton:
                    check(child.Code == 0 && value.GetProperty("result").GetArrayLength() == 1 && value.GetProperty("result")[0].GetString() == "x",
                        "MCP_CALL_PROCESS_SINGLETON: an actual singleton-array result is not unwrapped");
                    break;
                case WireMode.EmptyString:
                    check(child.Code == 0 && value.GetProperty("result").ValueKind == JsonValueKind.String && value.GetProperty("result").GetString() == "",
                        "MCP_CALL_PROCESS_EMPTY_STRING: an actual empty string is retained");
                    break;
                case WireMode.ToolError:
                    check(child.Code == 10 && value.GetProperty("status").GetString() == "tool_error" &&
                        value.GetProperty("dispatchState").GetString() == "confirmed" && value.GetProperty("result").GetString() == "synthetic_tool_refusal",
                        "MCP_CALL_PROCESS_WIRE_TOOL_ERROR: an actual MCP isError response exits ten with its original payload");
                    break;
                case WireMode.RpcError:
                    check(child.Code == 11 && value.GetProperty("status").GetString() == "rpc_error" &&
                        value.GetProperty("dispatchState").GetString() == "confirmed" && value.GetProperty("error").GetProperty("code").GetInt32() == -32017,
                        "MCP_CALL_PROCESS_RPC_EXIT: an actual matching JSON-RPC failure exits eleven");
                    break;
                case WireMode.HttpError:
                    check(child.Code == 12 && value.GetProperty("status").GetString() == "http_error" &&
                        value.GetProperty("dispatchState").GetString() == "unknown" && value.GetProperty("httpStatus").GetInt32() == 503,
                        "MCP_CALL_PROCESS_HTTP_EXIT: a real HTTP failure exits twelve and does not claim the request was unacted");
                    break;
                case WireMode.DropAfterDispatch:
                case WireMode.WrongResponseId:
                    check(child.Code == 69 && value.GetProperty("status").GetString() == "transport_error" &&
                        value.GetProperty("dispatchState").GetString() == "unknown",
                        "MCP_CALL_PROCESS_UNCONFIRMED: a received request followed by connection loss or mismatched response exits sixty-nine without replay");
                    break;
                case WireMode.Discovery:
                    check(child.Code == 0 && value.GetProperty("instructions").GetString() == WireFixture.Instructions &&
                        value.GetProperty("result").GetProperty("tools").GetArrayLength() == 1,
                        "MCP_CALL_PROCESS_DISCOVERY_CARD: actual tool discovery retains the initialization instructions");
                    break;
            }
        }
    }

    private static async Task<(int Code, JsonElement Value, string Raw)> RunWireChildAsync(string root, string request, bool list, Action<bool, string> check)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { typeof(WorkerApplication).Assembly.Location, "--mcp-call", "--data", root }) start.ArgumentList.Add(argument);
        if (list) start.ArgumentList.Add("--list-tools");
        else { start.ArgumentList.Add("--request-file"); start.ArgumentList.Add(request); }
        using var process = Process.Start(start) ?? throw new IOException("CLI wire child did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        string raw = await stdout, errors = await stderr;
        check(errors.Length == 0 && raw.Count(character => character == '\n') == 1,
            "MCP_CALL_PROCESS_WIRE_FRAME: each actual child emits exactly one JSON line and no stderr");
        using var result = JsonDocument.Parse(raw);
        return (process.ExitCode, result.RootElement.Clone(), raw);
    }

    private enum WireMode { Json, Sse, EmptyArray, Singleton, EmptyString, ToolError, RpcError, HttpError, DropAfterDispatch,
        WrongResponseId, InitializeRpcError, BadProtocol, Discovery, MalformedRequest }

    private sealed class WireFixture : ILocalControlWorker, IAsyncDisposable
    {
        internal const string Instructions = "Synthetic broker card with \\paths and actual\nnewlines 雪.";
        internal const string Advisory = "{\"result\":\"this later hint must not replace the payload\"}";
        private readonly ToolFixture fixture = new("mcp-wire-" + Guid.NewGuid().ToString("N"));
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly LocalControlServer control;
        private readonly Task loop;
        private volatile WireMode mode;
        internal string Root => fixture.Root;
        internal string RequestFile => Path.Combine(Root, "wire-request.json");
        internal string Token { get; } = new('A', 64);
        internal List<string> Methods { get; } = [];
        internal bool AllAuthorized = true;
        internal int ToolCalls;
        internal JsonElement? LastArguments;
        public WorkerOptions Options { get; }
        public BrokerRuntime Runtime => fixture.Runtime;
        public WebApplication Application { get; }
        public string LocalAccessToken => Token;
        public McpStatusView McpStatus => new(false, null, null, []);
        public LanProfileMonitor LanProfile => throw new InvalidOperationException("The connection fixture must not query LAN state.");

        internal WireFixture()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Options = new(Root, "Synthetic wire control", port, 0, false, false) { EnableBrokerDiscovery = false };
            Application = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = Root }).Build();
            Application.Urls.Add("http://127.0.0.1:" + port);
            control = new LocalControlServer(this);
            control.Start();
            loop = RunAsync();
        }

        internal void Begin(WireMode selected)
        { mode = selected; Methods.Clear(); AllAuthorized = true; ToolCalls = 0; LastArguments = null; }

        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream();
            using var header = new MemoryStream();
            byte[] one = new byte[1];
            while (header.Length < 16384)
            {
                if (await stream.ReadAsync(one, deadline.Token) != 1) throw new IOException("Incomplete wire headers.");
                header.WriteByte(one[0]);
                if (header.Length >= 4 && Encoding.ASCII.GetString(header.GetBuffer(), (int)header.Length - 4, 4) == "\r\n\r\n") break;
            }
            string headers = Encoding.ASCII.GetString(header.ToArray());
            AllAuthorized &= headers.Contains("Authorization: Bearer " + Token, StringComparison.OrdinalIgnoreCase);
            string length = headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
            int count = int.Parse(length, System.Globalization.CultureInfo.InvariantCulture);
            if (count is < 1 or > 128 * 1024) throw new InvalidDataException("Wire request body exceeded its bound.");
            byte[] body = new byte[count];
            await stream.ReadExactlyAsync(body, deadline.Token);
            using var request = JsonDocument.Parse(body);
            var root = request.RootElement;
            string method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            string? id = root.TryGetProperty("id", out var supplied) ? supplied.GetString() : null;
            if (method == "notifications/initialized") { await ReplyAsync(stream, 202, "application/json", "", deadline.Token); return; }
            if (method == "initialize")
            {
                string initialized = mode == WireMode.InitializeRpcError
                    ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32016, message = "Synthetic initialization refusal." } })
                    : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { protocolVersion = mode == WireMode.BadProtocol ? "2000-01-01" : "2025-11-25", instructions = Instructions } });
                await ReplyAsync(stream, 200, "application/json", initialized, deadline.Token);
                return;
            }
            ToolCalls++;
            if (method == "tools/call") LastArguments = root.GetProperty("params").GetProperty("arguments").Clone();
            if (mode == WireMode.DropAfterDispatch) { client.Client.LingerState = new LingerOption(true, 0); return; }
            if (mode == WireMode.HttpError) { await ReplyAsync(stream, 503, "application/json", "{}", deadline.Token); return; }
            if (mode == WireMode.RpcError)
            {
                await ReplyAsync(stream, 200, "application/json", JsonSerializer.Serialize(new { jsonrpc = "2.0", id,
                    error = new { code = -32017, message = "Synthetic request refusal." } }), deadline.Token);
                return;
            }
            if (mode == WireMode.Discovery)
            {
                await ReplyAsync(stream, 200, "application/json", JsonSerializer.Serialize(new { jsonrpc = "2.0", id,
                    result = new { tools = new[] { new { name = "synthetic_wire_tool" } } } }), deadline.Token);
                return;
            }
            object value = mode switch
            {
                WireMode.EmptyArray => Array.Empty<string>(), WireMode.Singleton => new[] { "x" }, WireMode.EmptyString => "",
                WireMode.ToolError => "synthetic_tool_refusal",
                _ => new { text = LastArguments!.Value.GetProperty("text").GetString(), empty = "", emptyArray = Array.Empty<string>(), single = new[] { "x" } },
            };
            string serialized = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = mode == WireMode.WrongResponseId ? "a-different-request" : id,
                result = new { isError = mode == WireMode.ToolError,
                    content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) }, new { type = "text", text = Advisory } },
                    structuredContent = new { pending = 2 } },
            }, new JsonSerializerOptions { WriteIndented = mode == WireMode.Sse });
            string payload = mode == WireMode.Sse
                ? "event: message\r\n" + string.Join("\r\n", serialized.Split('\n').Select(line => "data: " + line)) + "\r\n\r\n"
                : serialized;
            await ReplyAsync(stream, 200, mode == WireMode.Sse ? "text/event-stream" : "application/json", payload, deadline.Token);
        }

        private static async Task ReplyAsync(NetworkStream stream, int status, string type, string text, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Synthetic\r\nContent-Type: " + type +
                "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await loop; }
            finally { await control.DisposeAsync(); await Application.DisposeAsync(); await fixture.DisposeAsync(); stop.Dispose(); }
        }
    }
}
