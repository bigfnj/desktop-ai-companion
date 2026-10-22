using AgentBroker.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Net;
using System.Text.Json.Nodes;

namespace AgentBroker.Worker;

internal enum McpObservedOperation { Other, Initialize, ListTools, CallTool }

/// <summary>Passive, process-local protocol evidence. Retains only three fixed rows and UTC times.</summary>
internal sealed class McpActivityTracker
{
    private readonly object gate = new();
    private readonly McpClientActivityView[] clients = [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Codex)];
    private bool ready;
    private DateTimeOffset? lastRequest, lastToolsList;

    public void SetReady(bool value) { lock (gate) ready = value; }
    public McpStatusView Snapshot()
    {
        lock (gate) return new(ready, lastRequest, lastToolsList, [.. clients]);
    }
    public void Observe(McpClientFamily family, McpObservedOperation operation)
    {
        lock (gate)
        {
            if (!ready) return;
            var now = DateTimeOffset.UtcNow;
            if (lastRequest is { } previousUtc && previousUtc > now) now = previousUtc;
            lastRequest = now;
            int index = family is McpClientFamily.Claude or McpClientFamily.Codex ? (int)family : 0;
            var previous = clients[index];
            clients[index] = previous with
            {
                LastSuccessfulRequestUtc = now,
                LastInitializeUtc = operation == McpObservedOperation.Initialize ? now : previous.LastInitializeUtc,
                LastToolsListUtc = operation == McpObservedOperation.ListTools ? now : previous.LastToolsListUtc,
                LastSuccessfulToolUtc = operation == McpObservedOperation.CallTool ? now : previous.LastSuccessfulToolUtc
            };
            if (operation == McpObservedOperation.ListTools) lastToolsList = now;
        }
    }

    internal static McpClientFamily Classify(string? name)
    {
        if (name is null || name.Length > 64) return McpClientFamily.Other;
        // Verified public client implementation names. These are self-reported UI labels,
        // never an authentication, session-routing or authorization input.
        if (name.Equals("claude-code", StringComparison.OrdinalIgnoreCase)) return McpClientFamily.Claude;
        if (name.Equals("codex-mcp-client", StringComparison.OrdinalIgnoreCase)) return McpClientFamily.Codex;
        return McpClientFamily.Other;
    }
}

internal sealed class McpActivityFilters(McpActivityTracker tracker, Guid brokerId)
{
    private const string ObservationKey = "agentbroker.mcp-observation";

    // This object lives only in one HTTP request's Items. It contains no request id,
    // client name/version, header, tool name/arguments, response text or server reference.
    private sealed class Observation(McpClientFamily family, McpObservedOperation operation)
    {
        private int successful = operation is McpObservedOperation.Initialize or McpObservedOperation.Other ? 1 : 0;
        private int recorded;
        public McpClientFamily Family = family;
        public McpObservedOperation Operation { get; } = operation;
        public void MarkSuccessful(McpClientFamily value) { Family = value; Volatile.Write(ref successful, 1); }
        public bool TakeSuccess() => Volatile.Read(ref successful) == 1 && Interlocked.Exchange(ref recorded, 1) == 0;
    }

    public McpMessageFilter Incoming() => next => async (context, token) =>
    {
        if (context.JsonRpcMessage is JsonRpcRequest request && LocalContext(context) is { } http)
        {
            var operation = request.Method switch
            {
                RequestMethods.Initialize => McpObservedOperation.Initialize,
                RequestMethods.ToolsList => McpObservedOperation.ListTools,
                RequestMethods.ToolsCall => McpObservedOperation.CallTool,
                _ => McpObservedOperation.Other
            };
            var family = McpActivityTracker.Classify(context.Server.ClientInfo?.Name);
            if (operation == McpObservedOperation.Initialize && request.Params is JsonObject parameters &&
                parameters["clientInfo"] is JsonObject client && client["name"] is JsonValue name &&
                name.TryGetValue<string>(out var value)) family = McpActivityTracker.Classify(value);
            http.Items[ObservationKey] = new Observation(family, operation);
        }
        await next(context, token).ConfigureAwait(false);
    };

    public McpMessageFilter Outgoing() => next => async (context, token) =>
    {
        await next(context, token).ConfigureAwait(false);
        if (!token.IsCancellationRequested && context.JsonRpcMessage is JsonRpcResponse &&
            LocalContext(context) is { } http && !http.RequestAborted.IsCancellationRequested &&
            http.Response.StatusCode < 400 && http.Items.TryGetValue(ObservationKey, out var value) &&
            value is Observation observation && observation.TakeSuccess())
            tracker.Observe(observation.Family, observation.Operation);
    };

    public McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListTools() => next => async (context, token) =>
    {
        var result = await next(context, token).ConfigureAwait(false);
        MarkSuccessful(context, McpObservedOperation.ListTools, token);
        return result;
    };

    public McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool() => next => async (context, token) =>
    {
        var result = await next(context, token).ConfigureAwait(false);
        if (result.IsError is not true) MarkSuccessful(context, McpObservedOperation.CallTool, token);
        return result;
    };

    private void MarkSuccessful(MessageContext context, McpObservedOperation operation, CancellationToken token)
    {
        if (!token.IsCancellationRequested && LocalContext(context) is { } http &&
            http.Items.TryGetValue(ObservationKey, out var value) && value is Observation observation &&
            observation.Operation == operation)
            observation.MarkSuccessful(McpActivityTracker.Classify(context.Server.ClientInfo?.Name));
    }

    private HttpContext? LocalContext(MessageContext context)
    {
        var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        return http is not null && !http.Request.IsHttps && http.Connection.RemoteIpAddress is { } address &&
            IPAddress.IsLoopback(address) && http.Items.TryGetValue("agentbroker.identity", out var identity) &&
            identity is Guid id && id == brokerId ? http : null;
    }
}
