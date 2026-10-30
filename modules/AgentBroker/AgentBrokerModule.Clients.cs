using System.Globalization;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

/// <summary>
/// Separate onboarding states per client (N-agentbroker-client-onboarding-01): broker running, client
/// configured, endpoint, tools, inbox and native wake. Each state comes from its own evidence and none
/// is inferred from another: a saved entry is not a connection, a connection is not an inbox, and a
/// label is never evidence of anything.
/// </summary>
public sealed partial class AgentBrokerModule
{
    private ClientInspection? claudeInspection, codexInspection;
    private bool clientCheckOpen;
    private readonly Dictionary<string, string> clientSetupResults = new(StringComparer.Ordinal);

    private static string ClientName(string client) => client == "claude" ? "Claude Code" : "Codex";
    private ClientInspection? Inspection(string client) => client == "claude" ? claudeInspection : codexInspection;
    private static string WorkerExecutable() =>
        Path.Combine(Path.GetDirectoryName(typeof(AgentBrokerModule).Assembly.Location)!, "worker", "AgentBroker.Worker.exe");
    private string LoopbackMcpEndpoint() =>
        "http://127.0.0.1:" + settings.LoopbackPort.ToString(CultureInfo.InvariantCulture) + "/mcp";

    /// <summary>On demand only (the pane action, the tray item, after a Connect action). The status poll
    /// never launches a vendor CLI: a check spawns the client's CLI, and Claude's get also runs its own
    /// connection check against the entry.</summary>
    private async Task<string> CheckClientSetupAsync(string? only = null)
    {
        if (shutDown || !settings.Enabled || dataDirectory is null || run is null)
            return "Enable AgentBroker before checking client setup.";
        if (clientCheckOpen) return "A client setup check is already running.";
        clientCheckOpen = true;
        int current = generation;
        CancellationToken token = run.Token;
        string directory = dataDirectory, worker = WorkerExecutable(), endpoint = LoopbackMcpEndpoint();
        try
        {
            foreach (string client in only is null ? new[] { "claude", "codex" } : new[] { only })
            {
                ClientInspection inspection = await ClientSetup.InspectAsync(client, worker, directory, endpoint, McpNow(), token);
                if (shutDown || generation != current || token.IsCancellationRequested)
                    return "The client setup check was cancelled because the broker connection changed.";
                if (client == "claude") claudeInspection = inspection; else codexInspection = inspection;
                NotifyInfoChanged();
            }
            return only is null
                ? "Client setup checked. " + ClientName("claude") + ": " + ClientHeadline("claude") + " " + ClientName("codex") + ": " + ClientHeadline("codex")
                : ClientName(only) + ": " + ClientHeadline(only);
        }
        finally { clientCheckOpen = false; }
    }

    private async Task<string> ConnectAndCheckAsync(string client)
    {
        string result = await ConfigureClientAsync(client);
        if (!shutDown && settings.Enabled && run is not null && !run.IsCancellationRequested) await CheckClientSetupAsync(client);
        clientSetupResults[client] = result;
        NotifyInfoChanged();
        return result;
    }

    private string ClientHeadline(string client)
    {
        if (!settings.Enabled) return "AgentBroker is disabled.";
        var inspection = Inspection(client);
        return inspection?.State switch
        {
            null => "configuration not checked yet.",
            ClientConfiguration.CliMissing => "CLI not found.",
            ClientConfiguration.PolicyBlocked => "blocked by managed policy.",
            ClientConfiguration.Absent => "not configured.",
            ClientConfiguration.DifferentEntry => "an entry with AgentBroker's name points elsewhere.",
            ClientConfiguration.Disabled => "configured but disabled.",
            ClientConfiguration.Unrecognized => "configuration not recognized (unsupported CLI output).",
            ClientConfiguration.Configured => InboxCounts(client).Attributed > 0
                ? "configured, with an inbox registered through this client."
                : "configured; no inbox registered through this client yet.",
            _ => "configuration check did not complete."
        };
    }

    private string ClientDisplay(string client)
    {
        var lines = new List<string> { ClientName(client) + ": " + ClientHeadline(client) };
        lines.AddRange(ClientStateLines(client));
        return string.Join("\n", lines);
    }

    private IEnumerable<string> ClientStateLines(string client)
    {
        string name = ClientName(client);
        // Broker running.
        if (!settings.Enabled) { yield return "\u2717 Broker: disabled. Enable AgentBroker first."; yield break; }
        yield return snapshot is null ? "\u2717 Broker: not running (" + Safe(status, 80) + ")."
            : ownsWorker ? "\u2713 Broker: running." : "\u2713 Broker: running in another companion; this one is on standby.";

        // Client configured: the exact entry and its effective scope, from the client's own read command.
        var inspection = Inspection(client);
        string exit = inspection is null ? "" : inspection.Health == "timed_out" ? "timed out"
            : "exit " + inspection.ExitCode.ToString(CultureInfo.InvariantCulture);
        yield return inspection?.State switch
        {
            null => "\u26A0 Configuration: not checked. Choose Check client setup; it runs " + name + "'s read-only mcp get for this entry only.",
            ClientConfiguration.CliMissing => client == "claude"
                ? "\u2717 Configuration: the Claude Code CLI was not found. The companion starts claude.exe from PATH or the standard user install, never a script shim such as claude.cmd."
                : "\u2717 Configuration: the Codex CLI was not found. The companion starts codex.exe from PATH, never a script shim such as codex.cmd.",
            ClientConfiguration.PolicyBlocked => "\u2717 Configuration: blocked by managed policy (" + exit + "). Ask the policy owner to approve this exact local server.",
            ClientConfiguration.Absent => "\u2717 Configuration: absent. Use Connect " + (client == "claude" ? "Claude" : "Codex") + " MCP to add it for this user.",
            ClientConfiguration.DifferentEntry => "\u2717 Configuration: an entry named " + AgentClientSetup.ServerName +
                " exists but does not point at this broker. Review it in " + name + "'s MCP settings; AgentBroker never replaces it.",
            ClientConfiguration.Disabled => client == "claude"
                ? "\u2717 Configuration: this broker's entry exists, but Claude Code reports it rejected or disabled in its settings."
                : "\u2717 Configuration: this broker's entry exists, but it is disabled in Codex (enabled = false).",
            ClientConfiguration.Unrecognized => "\u26A0 Configuration: " + name + "'s report was not in a recognized form, so this CLI version is unsupported here. Nothing was changed.",
            ClientConfiguration.Configured => "\u2713 Configuration: the exact entry for this broker, " + ScopeText(inspection!.Scope) + ", " +
                (inspection.Transport == "http" ? "local HTTP" : "stdio bridge") + ". Checked " + Age(inspection.CheckedUtc, McpNow()) + ".",
            ClientConfiguration.Cancelled => "\u26A0 Configuration: the check was cancelled.",
            _ => "\u26A0 Configuration: the check did not complete (" + exit + "). Try again."
        };

        // Endpoint: the worker's own listener readiness, plus Claude's own connection check when it ran one.
        var mcp = snapshot?.Mcp;
        if (mcp is null) yield return "\u26A0 Endpoint: status unknown.";
        else if (!mcp.EndpointReady) yield return "\u2717 Endpoint: the broker's MCP endpoint is not accepting connections.";
        else if (inspection?.State == ClientConfiguration.DifferentEntry) yield return "\u2717 Endpoint: ready, but the configured entry does not reach it.";
        else if (client == "claude" && inspection?.State == ClientConfiguration.Configured && inspection!.Health != "unknown")
            yield return inspection.Health == "connected"
                ? "\u2713 Endpoint: reachable; the Claude CLI connected and listed tools during the check."
                : "\u2717 Endpoint: ready, but the Claude CLI reported " + HealthText(inspection.Health) + " during the check.";
        else yield return "\u2713 Endpoint: ready at " + LoopbackMcpEndpoint() + ".";

        // Tools available: only traffic this client identified itself in, never another client's.
        yield return ToolsLine(mcp, client == "claude" ? McpClientFamily.Claude : McpClientFamily.Codex);

        // Live inbox: generic sessions in the directory, attributed only by their own registration request.
        var inbox = InboxCounts(client);
        if (inbox.Unknown) yield return "\u26A0 Inbox: unknown until the broker reports its sessions.";
        else if (inbox.Attributed > 0)
            yield return "\u2713 Inbox: " + Count(inbox.Attributed) + " registered through this client (" + Count(inbox.AttributedPresent) +
                " present, " + Count(inbox.Attributed - inbox.AttributedPresent) + " away).";
        else if (inbox.Unattributed > 0)
            yield return "\u26A0 Inbox: none identified as " + name + ". " + Count(inbox.Unattributed) + " MCP inbox" + (inbox.Unattributed == 1 ? " here carries" : "es here carry") +
                " no client identity (" + Count(inbox.UnattributedPresent) + " present, " + Count(inbox.Unattributed - inbox.UnattributedPresent) + " away).";
        else yield return "\u2717 Inbox: none registered. An agent registers with register_agent; an MCP connection alone is not an inbox.";

        // Native receive and wake support.
        yield return NativeLine(client);
        // Report only (lane broker-163, N-agentbroker-code-owner-provenance-01): whose engine holds each Codex row's thread.
        if (client == "codex" && EngineBindingLine() is { } engine) yield return engine;

        if (clientSetupResults.TryGetValue(client, out string? result)) yield return "Last setup action: " + result;
    }

    private static string ScopeText(string scope) => scope switch
    {
        "user" => "user scope (every project)",
        "project" => "project scope (that project only)",
        "local" => "local scope (that project only, this user)",
        "managed" => "managed by policy",
        "" => "scope not reported by Codex",
        _ => "scope not recognized"
    };

    private static string HealthText(string health) => health switch
    {
        "tools_unavailable" => "a connection whose tool list failed",
        "needs_authentication" => "that it needs authentication",
        "failed" => "a failed connection",
        "connection_error" => "a connection error",
        "pending_approval" => "that the entry is pending approval",
        "rejected" => "that the entry is rejected in its settings",
        "disabled" => "that the entry is disabled",
        _ => "an unrecognized state"
    };

    private string ToolsLine(McpStatusView? mcp, McpClientFamily family)
    {
        var clients = mcp?.Clients;
        if (clients is null || clients.Length != 3 || clients.Any(row => row is null || !Enum.IsDefined(row.Family)) ||
            clients.Select(row => row.Family).Distinct().Count() != 3)
            return "\u26A0 Tools: client traffic unknown.";
        var now = McpNow();
        var row = clients.Single(item => item.Family == family);
        var other = clients.Single(item => item.Family == McpClientFamily.Other).LastToolsListUtc;
        string unidentified = other is { } seen && seen <= now.AddSeconds(5)
            ? "; clients that did not identify themselves listed tools " + Age(seen, now) : "";
        if (row.LastToolsListUtc is { } listed && listed <= now.AddSeconds(5))
            return "\u2713 Tools: listed by this client " + Age(listed, now) + ".";
        DateTimeOffset? contact = new[] { row.LastInitializeUtc, row.LastSuccessfulRequestUtc, row.LastSuccessfulToolUtc }.Max();
        return contact is { } any && any <= now.AddSeconds(5)
            ? "\u26A0 Tools: this client connected " + Age(any, now) + ", but no tool list was attributed to it" + unidentified + "."
            : "\u26A0 Tools: no traffic from this client since the broker started" + unidentified + ".";
    }

    private (bool Unknown, int Attributed, int AttributedPresent, int Unattributed, int UnattributedPresent) InboxCounts(string client)
    {
        var sessions = snapshot?.Sessions;
        if (sessions is null || sessions.Any(session => session is null)) return (true, 0, 0, 0, 0);
        var family = client == "claude" ? McpClientFamily.Claude : McpClientFamily.Codex;
        var attributions = (snapshot!.Mcp?.Inboxes ?? []).Where(inbox => inbox is not null)
            .GroupBy(inbox => (inbox.SessionId, inbox.Incarnation)).ToDictionary(group => group.Key, group => group.Last().Family);
        int attributed = 0, attributedPresent = 0, unattributed = 0, unattributedPresent = 0;
        foreach (var session in sessions.Where(session => session.Adapter == "mcp"))
        {
            bool present = session.Activity != "Away";
            if (!attributions.TryGetValue((session.SessionId, session.Incarnation), out var owner))
            { unattributed++; if (present) unattributedPresent++; }
            else if (owner == family) { attributed++; if (present) attributedPresent++; }
        }
        return (false, attributed, attributedPresent, unattributed, unattributedPresent);
    }

    private string NativeLine(string client)
    {
        bool claude = client == "claude";
        bool discovery = claude ? settings.ClaudeDiscovery : settings.CodexDiscovery;
        bool wake = claude ? settings.ClaudePeerCredentials : settings.CodexPeerMessages;
        string adapter = claude ? "claude-native" : "codex-native", name = claude ? "Claude" : "Codex";
        if (!discovery) return "\u26A0 Native: discovery of open " + name + " sessions is off; MCP agents still wait or poll.";
        if (snapshot is null) return "\u26A0 Native: unknown until the broker reports.";
        if (snapshot.AdapterErrors.TryGetValue(adapter, out string? error))
            return "\u2717 Native: the " + name + " adapter reports " + Safe(error, 160) + ".";
        var native = snapshot.Sessions.Where(session => session?.Adapter == adapter).ToArray();
        int wakeable = native.Count(session => session.CanWake);
        if (!wake)
            return "\u26A0 Native wake off: " + Count(native.Length) + " open " + name + " session" + (native.Length == 1 ? "" : "s") +
                " discovered; MCP agents still wait or poll. Turn on Wake existing " + name + " sessions for automatic delivery.";
        return native.Length == 0 ? "\u26A0 Native wake on; no open " + name + " session is discovered yet."
            : "\u2713 Native wake on: " + Count(wakeable) + " of " + Count(native.Length) + " discovered " + name + " sessions can be woken.";
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Counts and reason codes only, never a thread or process. Absent until the worker reports an observation.
    private string? EngineBindingLine()
    {
        var observed = (snapshot?.Sessions ?? []).Where(session => session?.Adapter == "codex-native" && session.Engine is not null).ToArray();
        if (observed.Length == 0) return null;
        int bound = observed.Count(session => session.Engine!.Bound);
        string unbound = string.Join(", ", observed.Where(session => !session.Engine!.Bound)
            .GroupBy(session => Safe(session.Engine!.Reason, 32)).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + " " + Count(group.Count())));
        return (bound == observed.Length ? "\u2713" : "\u26A0") + " Engine binding (report only): " + Count(bound) + " of " + Count(observed.Length) +
            " Codex sessions held by a Code or desktop engine" + (unbound.Length == 0 ? "" : "; unbound: " + unbound) + ". Nothing is refused on it.";
    }

    private IEnumerable<TrayItem> ClientTrayItems()
    {
        foreach (string client in new[] { "claude", "codex" })
        {
            string current = client;
            yield return new TrayItem
            {
                Label = Safe(ClientName(current) + ": " + ClientHeadline(current), 120),
                BuildChildren = () =>
                {
                    var children = ClientStateLines(current).Select(line => new TrayItem { Label = Safe(line, 120) }).ToList();
                    children.Add(new TrayItem { Label = "Check client setup", Click = () => _ = CheckClientSetupAsync() });
                    if (Inspection(current)?.State == ClientConfiguration.Absent)
                        children.Add(new TrayItem { Label = "Connect " + (current == "claude" ? "Claude" : "Codex") + " MCP",
                            Click = () => _ = ConnectAndCheckAsync(current) });
                    return children;
                }
            };
        }
    }
}
