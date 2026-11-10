using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunMcpStatusChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        RunClientEndpointDisplayChecks(check, dispatcher);
        DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var empty = new McpStatusView(true, null, null,
            [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Codex)]);
        var native = new SessionView("synthetic-native-only", Guid.NewGuid(), "Synthetic Claude native", "claude-native",
            "idle", true, true, false, false);
        // The bare base, as LocalControlServer reports it; the module appends /mcp (K3).
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
            [], [], [], [native], new Dictionary<string, string>(), Mcp: empty);
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-mcp-status-synthetic")));
        var supervisor = new StatusSupervisor(view);
        var control = new StatusControl { Response = new(true, "ok", view) };
        var setup = new TestClientCommand();
        var module = new AgentBrokerModule(control, supervisor) { McpNow = () => now, ClientSetup = new(setup) };
        try
        {
            module.Init(host);
            var pane = host.OptionsPanes.Single();
            check(pane.Schema.Any(field => field.Id == "mcpStatus" && field.Label == "MCP client traffic" && field.Kind == SettingKind.Info) &&
                pane.Schema.Any(field => field.Id == "agentInboxes" && field.Kind == SettingKind.Info) &&
                pane.Load()["mcpStatus"].Contains("Enable AgentBroker", StringComparison.Ordinal),
                "MCP status: disabled module separates client traffic and agent inbox information without claiming readiness");
            pane.Save(new Dictionary<string, string> { ["enabled"] = "true" });
            Pump(() => pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal), dispatcher);
            check(pane.Load()["status"].Contains("MCP ready", StringComparison.Ordinal) &&
                pane.Load()["status"].Contains("MCP agent inboxes: 0", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Claude client: not seen", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Codex client: not seen", StringComparison.Ordinal) &&
                pane.Load()["agentInboxes"].Contains("1 native, 0 MCP", StringComparison.Ordinal),
                "MCP status: native discovery and ready listener do not invent a vendor MCP connection");

            int notifications = 0;
            pane.InfoChanged += () => notifications++;
            int saved = host.SettingsFor(Id).SaveCount, generation = module.generation, stops = supervisor.Stops;
            var seen = empty with { LastSuccessfulRequestUtc = now, LastToolsListUtc = now,
                Clients = [new(McpClientFamily.Other, LastToolsListUtc: now),
                    new(McpClientFamily.Claude, LastInitializeUtc: now.AddSeconds(-30)), new(McpClientFamily.Codex)] };
            var active = view with { Mcp = seen };
            module.ApplyStatus(new(true, true, "running", active));
            check(notifications == 1 && pane.Load()["mcpStatus"].Contains("Claude client: seen just now", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Codex client: not seen", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Tool list served just now", StringComparison.Ordinal),
                "MCP status: observed Claude handshake updates the existing status bar without attributing other tools to Codex");
            module.ApplyStatus(new(true, true, "running", active));
            check(notifications == 1, "MCP status: identical observations do not cause duplicate Info notifications");
            now = now.AddMinutes(2);
            module.ApplyStatus(new(true, true, "running", active));
            check(notifications == 2 && pane.Load()["mcpStatus"].Contains("Claude client: seen 2 min ago", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Live connection health is not verified", StringComparison.Ordinal) &&
                !pane.Load()["mcpStatus"].Contains("disconnected", StringComparison.OrdinalIgnoreCase),
                "MCP status: idle traffic ages through existing polls without claiming an idle client disconnected");

            var polling = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Codex collaboration",
                "mcp", "Idle", true, false, true, false);
            var inboxes = active with { Sessions = [native, polling] };
            int beforeInbox = notifications;
            module.ApplyStatus(new(true, true, "running", inboxes));
            check(notifications == beforeInbox + 1 &&
                pane.Load()["status"].Contains("MCP agent inboxes: 1", StringComparison.Ordinal) &&
                pane.Load()["agentInboxes"].Contains("1 native, 1 MCP", StringComparison.Ordinal) &&
                pane.Load()["agentInboxes"].Contains("MCP labels (self-reported): \"Codex collaboration\"", StringComparison.Ordinal),
                "MCP status: generic Codex inbox is visible independently of its harness client implementation name");
            check(pane.Load()["mcpStatus"].Contains("Codex client: not seen", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("not participating agents", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Live connection health is not verified", StringComparison.Ordinal) &&
                !pane.Load()["status"].Contains("Codex client", StringComparison.Ordinal),
                "MCP status: a self-reported Codex label cannot manufacture Codex client traffic or verified connection health");
            int beforeRename = notifications;
            module.ApplyStatus(new(true, true, "running", inboxes with { Sessions = [native, polling with { Label = "Renamed generic agent" }] }));
            check(notifications == beforeRename + 1 && pane.Load()["agentInboxes"].Contains("Renamed generic agent", StringComparison.Ordinal),
                "MCP status: a label-only inbox change notifies the pane without changing the session count");
            var mixed = FormatAgentInboxes([native, polling with { Label = "Claude native owner" },
                polling with { Adapter = "unrecognized", Label = "Codex" }]);
            check(mixed.Detail.StartsWith("1 native, 1 MCP, 1 other.", StringComparison.Ordinal),
                "MCP status: inbox kind uses only the exact adapter, never a label or session-ID prefix");
            var labels = Enumerable.Range(0, 6).Select(index => polling with
                { Label = index == 0 ? "\n&\"" + new string('X', 100) : "Synthetic " + index }).ToArray();
            string bounded = FormatAgentInboxes(labels).Detail;
            check(!bounded.Contains('\n') && !bounded.Contains('&') && !bounded.Contains(new string('X', 49), StringComparison.Ordinal) &&
                bounded.Contains("2 more", StringComparison.Ordinal) && !bounded.Contains("Synthetic 4", StringComparison.Ordinal),
                "MCP status: declared labels are sanitized and capped at four labels of forty-eight characters");
            check(FormatAgentInboxes([polling with { Label = "\n&" }]).Detail.Contains("\"Unnamed\"", StringComparison.Ordinal) &&
                FormatAgentInboxes(null).Summary.Contains("unknown", StringComparison.Ordinal) &&
                FormatAgentInboxes([null!]).Summary.Contains("unknown", StringComparison.Ordinal),
                "MCP status: empty declared labels and unavailable inbox data have bounded truthful fallbacks");
            module.ApplyStatus(new(true, true, "running", active));
            check(pane.Load()["status"].Contains("MCP agent inboxes: 0", StringComparison.Ordinal) &&
                !pane.Load()["agentInboxes"].Contains("Codex collaboration", StringComparison.Ordinal),
                "MCP status: a generic inbox removed from the worker snapshot disappears from the display");

            module.ApplyStatus(new(true, true, "running", active with { Mcp = null }));
            check(pane.Load()["status"].Contains("MCP status unknown", StringComparison.Ordinal) &&
                !pane.Load()["mcpStatus"].Contains("Claude client: seen", StringComparison.Ordinal),
                "MCP status: an older worker without MCP observations clears stale client claims");
            module.ApplyStatus(new(true, true, "running", active with { Mcp = seen with { EndpointReady = false } }));
            check(pane.Load()["status"].Contains("MCP unavailable", StringComparison.Ordinal),
                "MCP status: a stopped endpoint overrides historical successful activity");
            foreach (McpClientActivityView[]? clients in new McpClientActivityView[]?[] {
                null, [], [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Claude)],
                [new(McpClientFamily.Other), new(McpClientFamily.Claude), new((McpClientFamily)99)], [null!, null!, null!] })
                check(FormatMcpStatus(seen with { Clients = clients! }, now).Summary.Contains("client traffic unknown", StringComparison.Ordinal),
                    "MCP status: malformed or duplicate client rows cannot display a false vendor connection");
            var future = empty with { LastToolsListUtc = now.AddHours(1), Clients = [new(McpClientFamily.Other),
                new(McpClientFamily.Claude, LastInitializeUtc: now.AddHours(1)), new(McpClientFamily.Codex)] };
            var display = FormatMcpStatus(future, now);
            check(display.Detail.Contains("Claude client: activity time unknown", StringComparison.Ordinal) &&
                display.Detail.Contains("Tool-list activity time unknown", StringComparison.Ordinal),
                "MCP status: future observations cannot masquerade as a recent successful connection");
            module.ApplyStatus(new(false, true, "worker_unresponsive"));
            check(pane.Load()["status"].Contains("MCP status unknown", StringComparison.Ordinal) &&
                setup.Calls.Count == 0 && control.Requests.Count == 0 && module.generation == generation &&
                host.SettingsFor(Id).SaveCount == saved && supervisor.Stops == stops,
                "MCP status: failed worker status clears readiness; display changes send no CLI, control action, settings write or restart");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private static void RunClientEndpointDisplayChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string live = "http://127.0.0.1:7432/mcp";
        const string ready = "\u2713 Endpoint: ready at " + live + ".";
        const string unverified = "\u26A0 Endpoint: ready; its address is not verified.";
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic live endpoint", "http://127.0.0.1:7432", null,
            [], [], [], [], new Dictionary<string, string>(), Mcp: new(true, null, null,
                [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Codex)]));
        var control = new EndpointDisplayControl(view);
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-client-endpoint-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var command = new TestClientCommand { Existing =
            "{\"transport\":{\"type\":\"streamable_http\",\"url\":\"" + live + "\"},\"enabled\":true}" };
        var module = new AgentBrokerModule(control, new WorkerSupervisor(control, control)) { ClientSetup = new(command) };
        try
        {
            module.Init(host);
            var pane = host.OptionsPanes.Single();
            Pump(() => module.snapshot?.Mcp?.EndpointReady == true, dispatcher);
            check(!module.ownsWorker && control.Starts == 0 && pane.Load()["codexClient"].Contains(ready, StringComparison.Ordinal),
                "CLIENT_ENDPOINT_BASELINE: the real supervisor attaches to an existing ready worker without launching one");
            check(pane.Save(new Dictionary<string, string> { ["loopbackPort"] = "17432" }),
                "CLIENT_ENDPOINT_SETTINGS: a standby instance can save a different requested port");
            Pump(() => module.snapshot?.Mcp?.EndpointReady == true, dispatcher);
            var checking = module.CheckClientSetupAsync("codex");
            Pump(() => checking.IsCompleted, dispatcher);
            _ = checking.GetAwaiter().GetResult();
            check(module.codexInspection?.State == ClientConfiguration.Configured && command.Calls.Count == 1 &&
                command.Calls[0].Arguments.SequenceEqual(new[] { "mcp", "get", AgentClientSetup.ServerName, "--json" }),
                "CLIENT_ENDPOINT_CHECK_CONFIGURED: the real Check action verifies the existing live HTTP entry");
            string details = pane.Load()["codexClient"];
            string claudeDetails = pane.Load()["claudeClient"];
            check(module.settings.LoopbackPort == 17432 && !module.ownsWorker && control.Starts == 0 && control.Stops == 0 &&
                details.Contains(ready, StringComparison.Ordinal) && !details.Contains(":17432/mcp", StringComparison.Ordinal) &&
                claudeDetails.Contains(ready, StringComparison.Ordinal) && !claudeDetails.Contains(":17432/mcp", StringComparison.Ordinal),
                "CLIENT_ENDPOINT_STANDBY_LIVE_PORT: a standby settings restart preserves the owner's worker and displays its actual port");
            check(module.ClientTrayItems().Single(item => item.Label.StartsWith("Codex:", StringComparison.Ordinal)).BuildChildren!()
                .Any(item => item.Label == ready),
                "CLIENT_ENDPOINT_TRAY_LIVE_PORT: the tray uses the same live endpoint as the pane");

            control.View = view with { LoopbackEndpoint = "http://localhost:7432" };
            module.ApplyStatus(new(true, false, "attached_standby", control.View));
            check(pane.Load()["codexClient"].Contains("Endpoint: ready at http://localhost:7432/mcp.", StringComparison.Ordinal),
                "CLIENT_ENDPOINT_CANONICAL_LOCALHOST: another canonical loopback spelling is displayed exactly");
            foreach (var invalid in new (string Name, string? Endpoint)[] {
                ("EMPTY", ""), ("ABSENT", null), ("DOUBLE_PATH", live),
                ("FOREIGN", "http://example.invalid:7432"), ("SCHEME", "https://127.0.0.1:7432"),
                ("USERINFO", "http://user:SYNTHETIC_VALUE@127.0.0.1:7432"),
                ("QUERY", "http://127.0.0.1:7432?token=SYNTHETIC_VALUE") })
            {
                control.View = view with { LoopbackEndpoint = invalid.Endpoint! };
                module.ApplyStatus(new(true, false, "attached_standby", control.View));
                details = pane.Load()["codexClient"];
                check(details.Contains(unverified, StringComparison.Ordinal) && !details.Contains("Endpoint: ready at", StringComparison.Ordinal) &&
                    !details.Contains("SYNTHETIC_VALUE", StringComparison.Ordinal),
                    "CLIENT_ENDPOINT_UNVERIFIED_" + invalid.Name + ": ready status without a canonical address has no URL or token claim");
            }
            check(command.Calls.Count == 1 && control.Starts == 0 && control.Stops == 0,
                "CLIENT_ENDPOINT_DISPLAY_READ_ONLY: status formatting launches no worker or vendor command and stops no peer");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private sealed class EndpointDisplayControl(BrokerSnapshot view) : IBrokerControlClient, IWorkerChildLauncher
    {
        internal BrokerSnapshot View = view;
        internal int Starts, Stops;
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
        {
            token.ThrowIfCancellationRequested();
            if (request.Operation == "stop") Interlocked.Increment(ref Stops);
            return Task.FromResult(request.Operation == "connection"
                ? new ControlResponse(true, "ok", Connection: new("http://127.0.0.1:7432/mcp", null))
                : new ControlResponse(true, "ok", View));
        }
        public IWorkerChild Start(System.Diagnostics.ProcessStartInfo start)
        {
            Interlocked.Increment(ref Starts);
            throw new InvalidOperationException("The standby fixture must not launch a worker.");
        }
    }
}
