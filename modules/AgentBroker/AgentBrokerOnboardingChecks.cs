using System.ComponentModel;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    /// <summary>Lane feature/agentbroker-backlog-a: per-client onboarding states and the LAN network profile,
    /// with a fake vendor CLI and synthetic worker snapshots. No CLI, configuration file, firewall or profile.</summary>
    private static void RunOnboardingChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string privateMarker = "SYNTHETIC_ONBOARDING_PRIVATE_VALUE";
        const string endpoint = "http://127.0.0.1:7432/mcp";
        string worker = @"C:\synthetic module\worker\AgentBroker.Worker.exe", data = @"C:\synthetic data\agentbroker";
        DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        string Block(string scope, string status, string type, params string[] rest) =>
            "\n" + AgentClientSetup.ServerName + ":\n  Scope: " + scope + "\n  Status: " + status + "\n  Type: " + type + "\n" +
            string.Concat(rest.Select(line => "  " + line + "\n")) + "\nTo remove this server, run: claude mcp remove \"" + AgentClientSetup.ServerName + "\" -s user\n";
        const string user = "User config (available in all your projects)";
        string connected = "\u221A Connected";

        var command = new OnboardingCommand();
        var setup = new AgentClientSetup(command);
        ClientInspection Inspect(string client, Func<ClientCommandResult> result)
        {
            command.Next = result; command.Calls.Clear();
            return setup.InspectAsync(client, worker, data, endpoint, now, CancellationToken.None).GetAwaiter().GetResult();
        }

        var absent = Inspect("claude", () => new(1, "", "No MCP server named \"" + AgentClientSetup.ServerName + "\". Configured servers: " + privateMarker));
        check(absent.State == ClientConfiguration.Absent && command.Calls.Count == 1 && command.Calls[0].Executable == "claude" &&
            command.Calls[0].Arguments.SequenceEqual(new[] { "mcp", "get", AgentClientSetup.ServerName }),
            "onboarding: Claude detection runs only the read-only mcp get for AgentBroker's own name and reports an absent entry");
        var codexAbsent = Inspect("codex", () => new(1, "", "Error: No MCP server named '" + AgentClientSetup.ServerName + "' found."));
        check(codexAbsent.State == ClientConfiguration.Absent && command.Calls.Single().Executable == "codex" &&
            command.Calls[0].Arguments.SequenceEqual(new[] { "mcp", "get", AgentClientSetup.ServerName, "--json" }),
            "onboarding: Codex detection runs only the read-only mcp get --json for AgentBroker's own name and reports an absent entry");
        var http = Inspect("claude", () => new(0, Block(user, connected, "http", "URL: " + endpoint, "Headers:", "    Authorization: " + privateMarker), ""));
        check(http is { State: ClientConfiguration.Configured, Scope: "user", Transport: "http", Health: "connected" } && http.CheckedUtc == now,
            "onboarding: the exact user-scope HTTP entry is configured, with Claude's own connection check");
        check(Inspect("claude", () => new(0, Block(user, connected, "http", "URL: http://127.0.0.1:7999/mcp"), "")).State == ClientConfiguration.DifferentEntry,
            "onboarding: an entry with AgentBroker's name for another endpoint is a different entry, never configured");
        var project = Inspect("claude", () => new(0, Block("Project config (shared via .mcp.json)", "\u2718 Failed to connect", "stdio",
            "Command: " + worker, "Args: --mcp-stdio --data " + data), ""));
        check(project is { State: ClientConfiguration.Configured, Scope: "project", Transport: "stdio", Health: "failed" },
            "onboarding: the effective scope is the one the client reports, here a project-scope stdio entry");
        check(Inspect("claude", () => new(0, Block(user, connected, "stdio", "Command: " + worker, "Args: --mcp-stdio --data " + data + " other"), "")).State ==
            ClientConfiguration.DifferentEntry, "onboarding: a stdio entry for another data directory is a different entry");
        check(Inspect("claude", () => new(0, Block(user, "\u2718 Rejected (see disabledMcpjsonServers in settings)", "http", "URL: " + endpoint), "")).State ==
            ClientConfiguration.Disabled, "onboarding: an entry Claude reports as rejected is disabled, not configured");
        foreach (string output in new[] { "No heading at all\n  Type: http\n", Block(user, connected, "http", "URL: " + endpoint) + Block(user, connected, "http", "URL: " + endpoint),
            "\n" + AgentClientSetup.ServerName + ":\n  Scope: " + user + "\n  Status: " + connected + "\n" })
            check(Inspect("claude", () => new(0, output, "")).State == ClientConfiguration.Unrecognized,
                "onboarding: an unrecognized report (no heading, two headings or no type) is unsupported, never configured");
        var policy = Inspect("claude", () => new(1, privateMarker, "MCP server blocked: managed settings allow only approved servers"));
        check(policy is { State: ClientConfiguration.PolicyBlocked, ExitCode: 1 }, "onboarding: a managed-policy refusal is blocked by policy");
        command.Throw = new FileNotFoundException("synthetic");
        check(Inspect("claude", () => new(0, "", "")).State == ClientConfiguration.CliMissing, "onboarding: a missing Claude CLI is not found, not a failure");
        command.Throw = new Win32Exception(2);
        check(Inspect("codex", () => new(0, "", "")).State == ClientConfiguration.CliMissing, "onboarding: a missing Codex executable is not found, not a failure");
        command.Throw = new OperationCanceledException();
        check(Inspect("codex", () => new(0, "", "")) is { State: ClientConfiguration.Failed, Health: "timed_out" },
            "onboarding: a CLI that outlives its deadline is a failed check, not an absent entry");
        command.Throw = null;
        check(Inspect("claude", () => new(6, privateMarker, "unrelated")) is { State: ClientConfiguration.Failed, ExitCode: 6 },
            "onboarding: an unrelated nonzero exit is a failed check, never absent");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); command.Calls.Clear();
            check(setup.InspectAsync("claude", worker, data, endpoint, now, cancelled.Token).GetAwaiter().GetResult().State == ClientConfiguration.Cancelled &&
                command.Calls.Count == 0, "onboarding: a cancelled check starts no CLI");
        }
        string codexStdio = "{\"name\":\"" + AgentClientSetup.ServerName + "\",\"enabled\":true,\"transport\":{\"type\":\"stdio\",\"command\":" +
            System.Text.Json.JsonSerializer.Serialize(worker) + ",\"args\":[\"--mcp-stdio\",\"--data\"," + System.Text.Json.JsonSerializer.Serialize(data) + "]}}";
        check(Inspect("codex", () => new(0, codexStdio, "")) is { State: ClientConfiguration.Configured, Scope: "", Transport: "stdio" },
            "onboarding: the exact Codex stdio entry is configured, and Codex reports no scope");
        check(Inspect("codex", () => new(0, codexStdio.Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal), "")).State == ClientConfiguration.Disabled &&
            Inspect("codex", () => new(0, "{\"transport\":{\"type\":\"streamable_http\",\"url\":\"" + endpoint + "\"}}", "")).State == ClientConfiguration.Configured &&
            Inspect("codex", () => new(0, "{\"transport\":{\"type\":\"streamable_http\",\"url\":\"http://127.0.0.1:1/mcp\"}}", "")).State == ClientConfiguration.DifferentEntry &&
            Inspect("codex", () => new(0, "{" + privateMarker, "")).State == ClientConfiguration.Unrecognized,
            "onboarding: a disabled, a foreign and a malformed Codex entry are each reported as such");

        // The module: separate per-client rows in the pane and tray.
        var native = new SessionView("synthetic-claude-native", Guid.NewGuid(), "Synthetic native", "claude-native", "Idle", true, true, false, false);
        var empty = new McpStatusView(true, null, null, [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Codex)]);
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433",
            [], [], [], [native], new Dictionary<string, string>(), Mcp: empty);
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-onboarding-synthetic")));
        var control = new StatusControl { Response = new(true, "ok", view) };
        var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { McpNow = () => now, ClientSetup = setup };
        try
        {
            module.Init(host);
            var pane = host.OptionsPanes.Single();
            command.Calls.Clear();
            check(pane.Load()["claudeClient"].Contains("AgentBroker is disabled", StringComparison.Ordinal) &&
                module.CheckClientSetupAsync().GetAwaiter().GetResult().Contains("Enable AgentBroker", StringComparison.Ordinal) && command.Calls.Count == 0,
                "onboarding: a disabled broker shows it per client and checks nothing");
            pane.Save(new Dictionary<string, string> { ["enabled"] = "true" });
            Pump(() => pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal), dispatcher);
            string claude = pane.Load()["claudeClient"], codex = pane.Load()["codexClient"];
            check(pane.Schema.Any(field => field.Id == "claudeClient" && field.Kind == SettingKind.Info && field.Label.Contains("VS Code", StringComparison.Ordinal)) &&
                pane.Schema.Any(field => field.Id == "codexClient" && field.Kind == SettingKind.Info) &&
                claude.StartsWith("Claude Code: configuration not checked yet.", StringComparison.Ordinal) &&
                codex.StartsWith("Codex: configuration not checked yet.", StringComparison.Ordinal) &&
                claude.Contains("\u2713 Broker: running.", StringComparison.Ordinal) && command.Calls.Count == 0,
                "onboarding: each client has its own row, unchecked until asked, and status polls launch no CLI");

            var traffic = empty with { LastToolsListUtc = now, Clients = [new(McpClientFamily.Other),
                new(McpClientFamily.Claude, LastInitializeUtc: now, LastToolsListUtc: now.AddMinutes(-2)), new(McpClientFamily.Codex)] };
            module.ApplyStatus(new(true, true, "running", view with { Mcp = traffic }));
            claude = pane.Load()["claudeClient"]; codex = pane.Load()["codexClient"];
            check(claude.Contains("\u2713 Tools: listed by this client 2 min ago.", StringComparison.Ordinal) &&
                claude.Contains("\u2717 Inbox: none registered.", StringComparison.Ordinal),
                "onboarding: Claude MCP traffic shows its tools without claiming an inbox");
            check(codex.Contains("\u26A0 Tools: no traffic from this client since the broker started.", StringComparison.Ordinal),
                "onboarding: Claude's tool list is not Codex traffic");

            var unattributed = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Claude Code", "mcp", "Idle", true, false, false, false);
            module.ApplyStatus(new(true, true, "running", view with { Mcp = traffic, Sessions = [native, unattributed] }));
            claude = pane.Load()["claudeClient"];
            check(claude.Contains("\u26A0 Inbox: none identified as Claude Code. 1 MCP inbox here carries no client identity (1 present, 0 away).", StringComparison.Ordinal),
                "onboarding: a generic inbox labelled Claude Code without a verified registration is not Claude's inbox");
            var away = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic", "mcp", "Away", true, false, false, false);
            var attributed = traffic with { Inboxes = [new(away.SessionId, away.Incarnation, McpClientFamily.Claude)] };
            module.ApplyStatus(new(true, true, "running", view with { Mcp = attributed, Sessions = [native, away] }));
            claude = pane.Load()["claudeClient"]; codex = pane.Load()["codexClient"];
            check(claude.Contains("\u2713 Inbox: 1 registered through this client (0 present, 1 away).", StringComparison.Ordinal) &&
                codex.Contains("\u2717 Inbox: none registered.", StringComparison.Ordinal),
                "onboarding: a verified Claude registration counts for Claude only, present and away separately");
            module.ApplyStatus(new(true, true, "running", view with { Mcp = attributed with { Inboxes = [new(away.SessionId, Guid.NewGuid(), McpClientFamily.Claude)] }, Sessions = [native, away] }));
            check(pane.Load()["claudeClient"].Contains("\u26A0 Inbox: none identified as Claude Code.", StringComparison.Ordinal),
                "onboarding: an attribution for another incarnation does not attribute this inbox");

            // The explicit check: both clients, one read command each, then separate tray items.
            command.Responses["claude"] = () => new(0, Block(user, connected, "http", "URL: " + endpoint), "");
            command.Responses["codex"] = () => new(1, "", "Error: No MCP server named '" + AgentClientSetup.ServerName + "' found. " + privateMarker);
            command.Calls.Clear();
            module.ApplyStatus(new(true, true, "running", view with { Mcp = attributed, Sessions = [native, away] }));
            string answer = module.CheckClientSetupAsync().GetAwaiter().GetResult();
            claude = pane.Load()["claudeClient"]; codex = pane.Load()["codexClient"];
            check(command.Calls.Count == 2 && command.Calls.All(call => call.Arguments[1] == "get") &&
                answer.Contains("Claude Code: configured, with an inbox registered through this client.", StringComparison.Ordinal) &&
                answer.Contains("Codex: not configured.", StringComparison.Ordinal),
                "onboarding: Check client setup runs one read-only get per client and answers per client");
            check(claude.Contains("\u2713 Configuration: the exact entry for this broker, user scope (every project), local HTTP. Checked just now.", StringComparison.Ordinal) &&
                claude.Contains("\u2713 Endpoint: reachable; the Claude CLI connected and listed tools during the check.", StringComparison.Ordinal) &&
                codex.Contains("\u2717 Configuration: absent. Use Connect Codex MCP", StringComparison.Ordinal),
                "onboarding: configuration, scope and the CLI's own reachability are separate lines per client");
            var tray = host.TrayItems.Single().BuildChildren().ToArray();
            var claudeTray = tray.Single(item => item.Label.StartsWith("Claude Code: ", StringComparison.Ordinal));
            var codexTray = tray.Single(item => item.Label.StartsWith("Codex: ", StringComparison.Ordinal));
            var codexChildren = codexTray.BuildChildren().ToArray();
            check(claudeTray.BuildChildren().Any(item => item.Label.StartsWith("\u2713 Inbox", StringComparison.Ordinal)) &&
                codexChildren.Any(item => item.Label == "Check client setup") && codexChildren.Any(item => item.Label == "Connect Codex MCP") &&
                !claudeTray.BuildChildren().Any(item => item.Label.StartsWith("Connect", StringComparison.Ordinal)) &&
                tray.All(item => item.Label.Length <= 120) && codexChildren.All(item => item.Label.Length <= 120),
                "onboarding: the tray carries a separate bounded item per client, offering Connect only for an absent entry");
            check(!host.TrayItems.Single().BuildChildren().SelectMany(item => item.BuildChildren?.Invoke() ?? []).Any(item => item.Label.Contains(privateMarker, StringComparison.Ordinal)) &&
                !pane.Load().Values.Any(value => value.Contains(privateMarker, StringComparison.Ordinal)),
                "onboarding: vendor output never reaches the pane or the tray");

            command.Responses["claude"] = () => new(1, "", "Cannot use MCP server: enterprise policy " + privateMarker);
            command.Throw = new Win32Exception(2);
            command.ThrowFor = "codex";
            module.CheckClientSetupAsync().GetAwaiter().GetResult();
            claude = pane.Load()["claudeClient"]; codex = pane.Load()["codexClient"];
            check(claude.StartsWith("Claude Code: blocked by managed policy.", StringComparison.Ordinal) &&
                claude.Contains("\u2717 Configuration: blocked by managed policy (exit 1).", StringComparison.Ordinal) &&
                codex.StartsWith("Codex: CLI not found.", StringComparison.Ordinal) && codex.Contains("codex.cmd", StringComparison.Ordinal),
                "onboarding: blocked-by-policy and a missing CLI are visible per client, never silence");
            command.Throw = null; command.ThrowFor = null;

            check(claude.Contains("\u26A0 Native wake off: 1 open Claude session discovered", StringComparison.Ordinal) &&
                codex.Contains("\u26A0 Native wake off: 0 open Codex sessions discovered", StringComparison.Ordinal),
                "onboarding: native wake support is its own line per client and defaults to off");
            module.ApplyStatus(new(true, true, "running", view with { AdapterErrors = new Dictionary<string, string> { ["codex-native"] = "unsupported_vscode_router 9.9.9" } }));
            check(pane.Load()["codexClient"].Contains("\u2717 Native: the Codex adapter reports unsupported_vscode_router 9.9.9.", StringComparison.Ordinal) &&
                pane.Load()["claudeClient"].Contains("Native wake off: 1 open Claude session", StringComparison.Ordinal),
                "onboarding: an unsupported native client is shown for that client only");
            // Lane broker-163: the report-only engine binding is a Codex line of its own, counts and reason codes only.
            SessionView Codex(EngineBindingView? engine) => new("codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Codex",
                "codex-native", "Idle", true, true, false, false, engine);
            module.ApplyStatus(new(true, true, "running", view with { Sessions = [native, Codex(new(true, "code_engine")),
                Codex(new(false, "foreign_holder")), Codex(new(false, "foreign_holder")), Codex(null)] }));
            string engines = pane.Load()["codexClient"];
            check(engines.Contains("\u26A0 Engine binding (report only): 1 of 3 Codex sessions held by a Code or desktop engine; unbound: foreign_holder 2. Nothing is refused on it.", StringComparison.Ordinal) &&
                !pane.Load()["claudeClient"].Contains("Engine binding", StringComparison.Ordinal),
                "ENGINE_BINDING_LINE: the Codex client shows how many observed rows are bound, and the unbound reasons by count");
            module.ApplyStatus(new(true, true, "running", view with { Sessions = [native, Codex(null)] }));
            check(!pane.Load()["codexClient"].Contains("Engine binding", StringComparison.Ordinal),
                "ENGINE_BINDING_LINE_ABSENT: with no observation reported there is no engine line");
            // Review finding 5: an elevated Claude Code session is a note beside the healthy sessions' line, not an adapter error.
            const string elevated = "1 Claude Code session is elevated or another user's and cannot be woken; use register_agent or run unelevated.";
            module.ApplyStatus(new(true, true, "running", view with { AdapterNotes = new Dictionary<string, string> { ["claude-native"] = "claude_session_elevated: " + elevated } }));
            string noted = pane.Load()["claudeClient"];
            check(noted.Contains("Native wake off: 1 open Claude session", StringComparison.Ordinal) && noted.Contains("\n\u26A0 " + elevated, StringComparison.Ordinal) &&
                !noted.Contains("\u2717 Native", StringComparison.Ordinal) && !pane.Load()["codexClient"].Contains("elevated", StringComparison.Ordinal) &&
                pane.Load()["nativeStatus"].Contains("Notes: claude-native: claude_session_elevated", StringComparison.Ordinal),
                "ELEVATED_NOTE_LINE: the elevated count is its own line beside the healthy Claude line, whole, and a note in Native adapters");
            // Review of d9c2967, item 3: the plural at the 32-process bound keeps its final period.
            const string many = "32 Claude Code sessions are elevated or another user's and cannot be woken; use register_agent or run unelevated.";
            module.ApplyStatus(new(true, true, "running", view with { AdapterNotes = new Dictionary<string, string> { ["claude-native"] = "claude_session_elevated: " + many } }));
            check(pane.Load()["claudeClient"].Contains("\n\u26A0 " + many, StringComparison.Ordinal),
                "ELEVATED_NOTE_PLURAL_WHOLE: the plural note at the 32-session bound is shown whole, final period included");
            module.ApplyStatus(new(true, true, "running", view));

            // A settings change restarts the worker; a check made against the old settings no longer applies.
            pane.Save(new Dictionary<string, string> { ["loopbackPort"] = "7440" });
            Pump(() => pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal), dispatcher);
            check(pane.Load()["claudeClient"].StartsWith("Claude Code: configuration not checked yet.", StringComparison.Ordinal),
                "onboarding: a settings restart clears the previous configuration check");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }

        RunLanProfileDisplayChecks(check, dispatcher, now);
    }

    private static void RunLanProfileDisplayChecks(Action<bool, string> check, TestDispatcher dispatcher, DateTimeOffset now)
    {
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433",
            [], [], [], [], new Dictionary<string, string>());
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-lan-profile-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var control = new StatusControl { Response = new(true, "ok", view) };
        var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { McpNow = () => now };
        var launcher = new TestFirewallLauncher(); module.FirewallLauncher = launcher;
        try
        {
            module.Init(host);
            Pump(() => module.snapshot is not null, dispatcher);
            var pane = host.OptionsPanes.Single();
            string Show(LanProfileView? lan)
            {
                module.ApplyStatus(new(true, true, "running", view with { Lan = lan }));
                return pane.Load()["lanAccess"];
            }
            string text = Show(null);
            check(text.StartsWith("Network category: not reported by this worker.", StringComparison.Ordinal) &&
                text.Contains("Private networks", StringComparison.Ordinal),
                "lan profile: an older worker without a reading says so and keeps the setup guidance");
            text = Show(new("public", "configured", 2, now, null));
            check(text.Contains("Network category: Public on the interface carrying this broker's LAN address.", StringComparison.Ordinal) &&
                text.Contains("Firewall rule: configured for the Private profile.", StringComparison.Ordinal) &&
                text.Contains("\u26A0 Windows treats this network as Public and the AgentBroker rule covers Private networks only", StringComparison.Ordinal) &&
                host.TrayItems.Single().BuildChildren().Any(item => item.Label == "This network is Public; the LAN rule covers Private networks only"),
                "lan profile: a Public network beside the Private rule is explained in one sentence in the pane and the tray");
            check(text.Contains("Observed: no LAN request from another PC since the broker started. A configured rule does not prove reachability.", StringComparison.Ordinal),
                "lan profile: a configured rule is not presented as observed reachability");
            text = Show(new("private", "configured", 2, now, now.AddMinutes(-2)));
            check(text.Contains("Network category: Private", StringComparison.Ordinal) && !text.Contains("treats this network", StringComparison.Ordinal) &&
                text.Contains("Observed: a LAN request from another PC reached this broker 2 min ago.", StringComparison.Ordinal) &&
                !host.TrayItems.Single().BuildChildren().Any(item => item.Label.StartsWith("This network is", StringComparison.Ordinal)),
                "WITNESS lan profile: a Private network with the Private rule shows no mismatch, and observed traffic is reported separately");
            text = Show(new("public", "absent", 0, now, null));
            check(text.Contains("Firewall rule: not configured; use Allow LAN access.", StringComparison.Ordinal) && !text.Contains("treats this network", StringComparison.Ordinal),
                "lan profile: without the rule there is no rule mismatch to explain, only the missing rule");
            text = Show(new("domain", "configured", 2, now, null));
            check(text.Contains("\u26A0 Windows treats this network as a Domain network", StringComparison.Ordinal),
                "lan profile: a Domain network beside the Private rule is explained too");
            text = Show(new("private", "conflicting", 7, now, null));
            check(text.Contains("a different rule uses this broker's rule name (profiles: Domain, Private, Public)", StringComparison.Ordinal),
                "lan profile: a conflicting rule names its profiles and is never shown as configured");
            text = Show(new("pending", "pending", 0, null, null));
            check(text.Contains("Network category: checking", StringComparison.Ordinal) && text.Contains("Firewall rule: checking.", StringComparison.Ordinal),
                "lan profile: a reading in progress is shown as checking, not as a result");
            module.ApplyStatus(new(true, true, "running", view with { LanEndpoint = null, Lan = new("public", "configured", 2, now, null) }));
            check(!pane.Load()["lanAccess"].Contains("Network category", StringComparison.Ordinal),
                "lan profile: no LAN listener means no network category line");
            check(launcher.Requests.Count == 0 && host.Asks.Count == 0 && control.Requests.Count == 0,
                "lan profile: showing the profile asks nothing, elevates nothing and sends no control request");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private sealed class OnboardingCommand : IAgentClientCommand
    {
        internal Func<ClientCommandResult>? Next;
        internal Dictionary<string, Func<ClientCommandResult>> Responses { get; } = new(StringComparer.Ordinal);
        internal Exception? Throw;
        internal string? ThrowFor;
        internal List<(string Executable, string[] Arguments)> Calls = [];
        public Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add((executable, arguments.ToArray()));
            if (Throw is not null && (ThrowFor is null || ThrowFor == executable)) return Task.FromException<ClientCommandResult>(Throw);
            var result = Responses.TryGetValue(executable, out var response) ? response : Next;
            return Task.FromResult(result?.Invoke() ?? new ClientCommandResult(1, "", "unconfigured synthetic command"));
        }
    }
}
