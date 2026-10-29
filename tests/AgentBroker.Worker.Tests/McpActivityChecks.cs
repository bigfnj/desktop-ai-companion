using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class McpActivityChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "mcp-activity-" + Guid.NewGuid().ToString("N"));
        var options = new WorkerOptions(scratch, "Synthetic MCP activity", LoopbackPort: 0, LanPort: 0,
            EnableLan: true, AllowUnauthenticatedLoopback: false)
        { LanListenAddress = IPAddress.Loopback, EnableBrokerDiscovery = false };
        string? keyName = null;
        WorkerApplication? worker = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            check(!worker.McpStatus.EndpointReady && Empty(worker.McpStatus), "MCP status is unready before endpoint startup with exactly three empty family rows");
            await worker.StartAsync();
            worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic directory agent", false);
            check(worker.Runtime.Store.Directory().Count > 0 && worker.McpStatus.EndpointReady && Empty(worker.McpStatus),
                "native or polling agent directory entries do not imply observed MCP clients");

            var connection = (await Control(options, new("connection"))).Connection!;
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = new(connection.McpEndpoint), Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using (var denied = await client.PostAsJsonAsync("", Initialize("claude-code")))
                check(denied.StatusCode == HttpStatusCode.Unauthorized, "MCP observation requires successful loopback transport authentication");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
            using (var origin = new HttpRequestMessage(HttpMethod.Post, "") { Content = JsonContent.Create(Initialize("claude-code")) })
            {
                origin.Headers.Add("Origin", "https://untrusted.invalid");
                using var denied = await client.SendAsync(origin);
                check(denied.StatusCode == HttpStatusCode.Forbidden, "browser-origin MCP requests cannot create activity evidence");
            }
            using (var malformed = new StringContent("{", Encoding.UTF8, "application/json"))
            using (var denied = await client.PostAsync("", malformed))
                check(denied.StatusCode == HttpStatusCode.BadRequest, "malformed MCP payload is rejected without activity evidence");
            using (var lanHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == worker.Runtime.Federation.LocalAdvertisement.CertificateSha256 })
            using (var lan = new HttpClient(lanHandler))
            using (var denied = await lan.PostAsJsonAsync(worker.Application.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal)) + "/mcp", Initialize("codex-mcp-client")))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "unpaired LAN MCP traffic cannot create local-client activity");
            var invalid = await Rpc(client, "synthetic/missing", new { });
            check(invalid.TryGetProperty("error", out _) && Empty(worker.McpStatus), "protocol errors and rejected traffic leave all MCP timestamps empty");

            var initialized = await Send(client, Initialize("claude-code"));
            check(initialized.GetProperty("result").TryGetProperty("protocolVersion", out _), "real HTTP MCP initialize still negotiates normally with observation filters");
            await Until(() => Row(worker.McpStatus, McpClientFamily.Claude).LastInitializeUtc is not null);
            var claude = worker.McpStatus;
            check(Row(claude, McpClientFamily.Claude).LastSuccessfulRequestUtc is not null &&
                Row(claude, McpClientFamily.Codex).LastSuccessfulRequestUtc is null && claude.LastToolsListUtc is null,
                "verified Claude initialize name records only Claude and global request evidence");

            var tools = await Rpc(client, "tools/list", new { });
            check(tools.GetProperty("result").GetProperty("tools").GetArrayLength() == 12, "passive MCP observation preserves all twelve tool schemas");
            await Until(() => worker.McpStatus.LastToolsListUtc is not null);
            check(Row(worker.McpStatus, McpClientFamily.Other).LastToolsListUtc is not null &&
                Row(worker.McpStatus, McpClientFamily.Claude).LastToolsListUtc is null,
                "stateless legacy tools requests without client info remain Other instead of inheriting the last initialize family");

            await Send(client, Initialize("codex-mcp-client"));
            await Until(() => Row(worker.McpStatus, McpClientFamily.Codex).LastInitializeUtc is not null);
            check(Row(worker.McpStatus, McpClientFamily.Claude).LastInitializeUtc == Row(claude, McpClientFamily.Claude).LastInitializeUtc,
                "Codex initialize evidence does not overwrite Claude's independent timestamp");

            var perRequest = await Rpc(client, "tools/list", Metadata("codex-mcp-client"), modern: true);
            check(perRequest.GetProperty("result").GetProperty("tools").GetArrayLength() == 12,
                "2026 per-request MCP client metadata works without adding a session or initialize handshake");
            await Until(() => Row(worker.McpStatus, McpClientFamily.Codex).LastToolsListUtc is not null);
            check(Row(worker.McpStatus, McpClientFamily.Claude).LastToolsListUtc is null,
                "SDK-validated per-request client info attributes only the actual Codex tools request");

            string beforeErrors = Serialize(worker.McpStatus);
            var missing = await Rpc(client, "tools/call", new { name = "synthetic_missing", arguments = new { } });
            var refused = await Rpc(client, "tools/call", new { name = "list_sessions", arguments = new { limit = 17 } });
            check(IsError(missing) && IsError(refused) && beforeErrors == Serialize(worker.McpStatus),
                "missing tools and application-level tool errors do not count as successful MCP activity");
            var registered = await Rpc(client, "tools/call", new { name = "register_agent", arguments = new {
                clientId = Guid.NewGuid(), incarnation = Guid.NewGuid(), label = "synthetic-private-tool-content", isPrivate = true } });
            check(!IsError(registered), "successful real broker tool is still executable through the observation filters");
            await Until(() => Row(worker.McpStatus, McpClientFamily.Other).LastSuccessfulToolUtc is not null);

            const string privateName = "synthetic-private-client-name";
            await Send(client, Initialize(privateName));
            await Send(client, Initialize(new string('x', 256)));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Send(client, Initialize(index % 2 == 0 ? "claude-code" : "codex-mcp-client"))));
            var final = worker.McpStatus;
            check(final.Clients.Length == 3 && final.Clients.Select(row => row.Family).SequenceEqual(new[] { McpClientFamily.Other, McpClientFamily.Claude, McpClientFamily.Codex }) &&
                final.LastSuccessfulRequestUtc >= claude.LastSuccessfulRequestUtc,
                "concurrent and unknown client requests retain only three bounded rows and monotonic UTC evidence");
            string retained = Serialize(final);
            check(!retained.Contains(privateName, StringComparison.Ordinal) && !retained.Contains("synthetic-private-version", StringComparison.Ordinal) &&
                !retained.Contains("synthetic-private-tool-content", StringComparison.Ordinal) && !retained.Contains(connection.BearerToken!, StringComparison.Ordinal) &&
                !retained.Contains(new string('x', 64), StringComparison.Ordinal),
                "MCP status retains no client names, versions, tool contents or bearer token");
            var status = await Control(options, new("status"));
            check(status.Success && status.Snapshot?.Mcp is { EndpointReady: true, Clients.Length: 3 },
                "current-user control snapshot exposes the bounded passive MCP status");
            var legacy = JsonSerializer.Deserialize<BrokerSnapshot>("{\"brokerId\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"Synthetic\",\"loopbackEndpoint\":\"http://127.0.0.1:1/mcp\",\"lanEndpoint\":null,\"discovered\":[],\"paired\":[],\"pairingOffers\":[],\"sessions\":[],\"adapterErrors\":{}}", ControlProtocol.Json);
            check(legacy?.Mcp is null, "older worker snapshots without MCP status remain explicitly unknown");
            await worker.DisposeAsync();
            check(!worker.McpStatus.EndpointReady, "stopping the endpoint clears readiness while preserving passive historical evidence");
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            {
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                key.Delete();
            }
        }
    }

    private static McpClientActivityView Row(McpStatusView status, McpClientFamily family) => status.Clients.Single(row => row.Family == family);
    private static bool Empty(McpStatusView status) => status.Clients.Length == 3 && status.LastSuccessfulRequestUtc is null && status.LastToolsListUtc is null &&
        status.Clients.All(row => row.LastInitializeUtc is null && row.LastSuccessfulRequestUtc is null && row.LastToolsListUtc is null && row.LastSuccessfulToolUtc is null);
    private static string Serialize(McpStatusView status) => JsonSerializer.Serialize(status, ControlProtocol.Json);
    private static object Initialize(string name) => new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "initialize",
        @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name, version = "synthetic-private-version" } } };
    private static object Metadata(string name) => new { _meta = new Dictionary<string, object> {
        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28", ["io.modelcontextprotocol/clientInfo"] = new { name, version = "synthetic-private-version" },
        ["io.modelcontextprotocol/clientCapabilities"] = new { } } };
    private static bool IsError(JsonElement reply) => reply.TryGetProperty("error", out _) ||
        reply.GetProperty("result").TryGetProperty("isError", out var value) && value.GetBoolean();
    private static Task<JsonElement> Rpc(HttpClient client, string method, object parameters, bool modern = false) =>
        Send(client, new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }, modern);
    private static async Task<JsonElement> Send(HttpClient client, object payload, bool modern = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = JsonContent.Create(payload) };
        if (modern)
        {
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", JsonSerializer.SerializeToElement(payload).GetProperty("method").GetString());
        }
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Concat(text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private static async Task<ControlResponse> Control(WorkerOptions options, ControlRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = ControlPipeSecurity.CreateClient(options.ControlPipeName);
        await pipe.ConnectAsync(deadline.Token);
        ControlPipeSecurity.ValidateConnectedServer(pipe, Environment.ProcessId);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ControlProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, deadline.Token); await pipe.FlushAsync(deadline.Token);
        using var line = new MemoryStream();
        byte[] single = new byte[1];
        while (line.Length <= ControlProtocol.MaximumFrameBytes)
        {
            if (await pipe.ReadAsync(single, deadline.Token) == 0) throw new IOException("Synthetic control response ended early.");
            if (single[0] == (byte)'\n') return JsonSerializer.Deserialize<ControlResponse>(line.ToArray(), ControlProtocol.Json)!;
            line.WriteByte(single[0]);
        }
        throw new IOException("Synthetic control response exceeded its bound.");
    }
}
