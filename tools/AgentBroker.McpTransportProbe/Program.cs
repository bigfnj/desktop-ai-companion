using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("--run-isolated: run fixed SDK 2.2 hybrid-HTTP checks on one OS-assigned 127.0.0.1 port. No existing broker or client configuration is accessed.");
    return 0;
}
if (args is not ["--run-isolated"])
{
    Console.Error.WriteLine("probe: invalid_arguments");
    return 2;
}

var checks = new List<string>();
var observed = new ProbeState();
var phase = "start";
WebApplication? app = null;
McpClient? sdkClient = null;
HttpClientTransport? transport = null;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
bool cleanShutdown = false;
try
{
    // Empty builder: no appsettings files, environment configuration, broker services,
    // certificates, identity material, client configuration or history are loaded.
    var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
    {
        Args = [], ApplicationName = typeof(ProbeTools).Assembly.GetName().Name,
        ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production"
    });
    builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    builder.Services.AddRouting();
    builder.Services.AddSingleton(observed);
    builder.Services.AddMcpServer().WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
#pragma warning disable MCP9006 // This isolated fixture deliberately tests bounded legacy-session compatibility.
        options.IdleTimeout = TimeSpan.FromSeconds(20);
        options.MaxIdleSessionCount = 4;
#pragma warning restore MCP9006
    }).WithTools<ProbeTools>();
    app = builder.Build();
    app.Use(async (context, next) =>
    {
        Interlocked.Increment(ref observed.Requests);
        if (context.Request.Method == "GET" && context.Request.Headers.ContainsKey("Mcp-Session-Id"))
            observed.LegacyGet.TrySetResult();
        await next(context);
    });
    app.MapMcp("/mcp");
    await app.StartAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
    var endpoint = new Uri(addresses.Addresses.Single() + "/mcp");
    Require(endpoint.Host == "127.0.0.1" && endpoint.Port != 7432 && endpoint.Port > 0,
        "listener_is_own_ephemeral_loopback", checks);
    using var audit = new LoopbackAuditHandler(endpoint);
    using var http = new HttpClient(audit) { Timeout = Timeout.InfiniteTimeSpan };

    phase = "legacy_sdk";
    // Pinning an initialize-capable revision forces the real initialize handshake.
    // The client has no custom ping responder or application request handlers.
    transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
        EnableStandaloneGetStream = true, ConnectionTimeout = TimeSpan.FromSeconds(5)
    }, http);
    sdkClient = await McpClient.CreateAsync(transport, new McpClientOptions
    {
        ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5),
        ClientInfo = new() { Name = "isolated-hybrid-probe", Version = "1.0" }
    }, cancellationToken: deadline.Token);
    Require(!string.IsNullOrEmpty(sdkClient.SessionId) && sdkClient.NegotiatedProtocolVersion == "2025-11-25",
        "legacy_sdk_negotiates_session_and_exact_revision", checks);
    var tool = await sdkClient.CallToolAsync("probe_bind", cancellationToken: deadline.Token);
    Require(!tool.IsError.GetValueOrDefault() && tool.Content.OfType<TextContentBlock>().Any(item => item.Text == "probe_ok"),
        "legacy_sdk_tool_call_works", checks);
    var legacyServer = await observed.LegacyServer.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
    await observed.LegacyGet.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
    Require(audit.SessionIssued, "legacy_initialize_response_issues_session_header", checks);
    // Inherited public API; no experimental RunSessionHandler is needed.
    // https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/src/ModelContextProtocol.Core/McpSession.Methods.cs
    var pong = await legacyServer.SendRequestAsync(new JsonRpcRequest
    {
        Id = new RequestId("isolated-server-ping"), Method = "ping"
    }, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
    Require(pong.Result is { } value && value.GetValueKind() == JsonValueKind.Object && !value.AsObject().Any(),
        "server_ping_gets_automatic_sdk_empty_pong", checks);
    Require(audit.LegacyGetCount > 0, "legacy_sdk_uses_standalone_get_stream", checks);

    phase = "modern_raw";
    var modern = await PostAsync(http, endpoint, "2026-07-28", "tools/call", "probe_bind", true, deadline.Token);
    Require(modern.Status == 200 && modern.ToolOk && !modern.SessionIssued,
        "modern_raw_tool_call_works_without_initialize_or_session", checks);
    var missingMetadata = await PostAsync(http, endpoint, "2026-07-28", "tools/call", "probe_bind", true, deadline.Token,
        includeModernMetadata: false);
    Require(missingMetadata.Status == 400, "modern_raw_missing_request_metadata_is_rejected", checks);
    var missingMethod = await PostAsync(http, endpoint, "2026-07-28", "tools/call", "probe_bind", false, deadline.Token);
    Require(missingMethod.Status == 400, "modern_raw_missing_method_header_is_rejected", checks);
    var mismatchedName = await PostAsync(http, endpoint, "2026-07-28", "tools/call", "wrong_name", true, deadline.Token);
    Require(mismatchedName.Status == 400, "modern_raw_mismatched_name_header_is_rejected", checks);
    using (var get = new HttpRequestMessage(HttpMethod.Get, endpoint))
    {
        get.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        get.Headers.Accept.Add(new("text/event-stream"));
        using var response = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Require((int)response.StatusCode == 405, "modern_stateless_get_is_unavailable", checks);
    }
    var oldRaw = await PostAsync(http, endpoint, "2025-11-25", "tools/call", "probe_bind", true, deadline.Token);
    Require(oldRaw.Status == 400, "legacy_raw_without_session_is_rejected", checks);

    phase = "mixed_revision_observation";
    var mixed = await PostAsync(http, endpoint, "2026-07-28", "initialize", null, true, deadline.Token);
    observed.MixedStatus = mixed.Status;
    observed.MixedResultRevision = mixed.ResultRevision;
    observed.MixedSessionIssued = mixed.SessionIssued;
    Require(!mixed.SessionIssued, "mixed_revision_request_does_not_mint_legacy_session", checks);
    // This observation is never used to claim valid negotiation or migrate a client.

    phase = "shutdown";
    await sdkClient.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    sdkClient = null;
    await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    transport = null;
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await app.StopAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5));
    await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    app = null;
    cleanShutdown = true;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Status = "PASS", SdkVersion = "2.2.0", Checks = checks, RequestCount = observed.Requests,
        MixedInitializeHttpStatus = observed.MixedStatus, MixedInitializeResultRevision = observed.MixedResultRevision,
        MixedInitializeSessionIssued = observed.MixedSessionIssued, MixedRevisionUsedForNegotiation = false,
        NoExistingBrokerAccessed = true, ClientConfigurationRead = false, ProductChanged = false,
        ListenerAndClientDisposed = cleanShutdown
    }));
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Status = "FAIL", Phase = phase,
        FailureType = ex.GetType().Name, FailedCheck = (ex as ProbeCheckException)?.Check,
        PassedChecks = checks, NoExistingBrokerAccessed = true,
        ClientConfigurationRead = false, ProductChanged = false }));
    return 1;
}
finally
{
    if (sdkClient is not null) try { await sdkClient.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    if (transport is not null) try { await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    if (app is not null)
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}

static void Require(bool condition, string name, List<string> checks)
{
    if (!condition) throw new ProbeCheckException(name);
    checks.Add(name);
}

static async Task<RawObservation> PostAsync(HttpClient http, Uri endpoint, string revision,
    string method, string? headerName, bool methodHeader, CancellationToken cancellation,
    bool includeModernMetadata = true)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
    request.Headers.Accept.Add(new("application/json"));
    request.Headers.Accept.Add(new("text/event-stream"));
    request.Headers.Add("MCP-Protocol-Version", revision);
    if (methodHeader) request.Headers.Add("Mcp-Method", method);
    if (headerName is not null) request.Headers.Add("Mcp-Name", headerName);
    object parameters;
    if (method == "initialize")
        parameters = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "isolated-mixed-probe", version = "1.0" } };
    else
    {
        var toolParameters = new Dictionary<string, object> { ["name"] = "probe_bind", ["arguments"] = new { } };
        if (revision == "2026-07-28" && includeModernMetadata)
            toolParameters["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = revision,
                ["io.modelcontextprotocol/clientInfo"] = new { name = "isolated-modern-probe", version = "1.0" },
                ["io.modelcontextprotocol/clientCapabilities"] = new { }
            };
        parameters = toolParameters;
    }
    request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 100, method, @params = parameters }), Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
    await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
    using var bytes = new MemoryStream();
    var buffer = new byte[4096];
    int read;
    while ((read = await stream.ReadAsync(buffer, cancellation)) != 0)
    {
        if (bytes.Length + read > 32768) throw new InvalidOperationException("bounded_response_required");
        bytes.Write(buffer, 0, read);
    }
    var text = Encoding.UTF8.GetString(bytes.ToArray());
    var payload = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
        ? text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]).LastOrDefault()
        : text;
    bool toolOk = false;
    string? resultRevision = null;
    if (!string.IsNullOrWhiteSpace(payload))
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("result", out var result))
        {
            if (result.TryGetProperty("content", out var content))
                toolOk = content.EnumerateArray().Any(item => item.TryGetProperty("text", out var value) && value.GetString() == "probe_ok");
            if (result.TryGetProperty("protocolVersion", out var version))
                resultRevision = version.GetString() is "2025-11-25" or "2026-07-28" ? version.GetString() : "other";
        }
    }
    return new((int)response.StatusCode, response.Headers.Contains("Mcp-Session-Id"), toolOk, resultRevision);
}

sealed record RawObservation(int Status, bool SessionIssued, bool ToolOk, string? ResultRevision);
sealed class ProbeCheckException(string check) : Exception(check)
{
    public string Check { get; } = check;
}

sealed class ProbeState
{
    public readonly TaskCompletionSource<McpServer> LegacyServer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource LegacyGet = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Requests;
    public int MixedStatus;
    public string? MixedResultRevision;
    public bool MixedSessionIssued;
}

[McpServerToolType]
sealed class ProbeTools(ProbeState state)
{
    [McpServerTool(Name = "probe_bind", ReadOnly = true, OpenWorld = false)]
    public string Bind(McpServer server)
    {
        if (server.SessionId is not null) state.LegacyServer.TrySetResult(server);
        return "probe_ok";
    }
}

sealed class LoopbackAuditHandler(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler
{
    UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null
})
{
    public bool SessionIssued { get; private set; }
    public int LegacyGetCount { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || uri.Scheme != endpoint.Scheme || uri.Host != endpoint.Host ||
            uri.Port != endpoint.Port || uri.AbsolutePath != "/mcp") throw new InvalidOperationException("own_endpoint_only");
        if (request.Method == HttpMethod.Get && request.Headers.Contains("Mcp-Session-Id")) LegacyGetCount++;
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.Contains("Mcp-Session-Id")) SessionIssued = true;
        return response;
    }
}
