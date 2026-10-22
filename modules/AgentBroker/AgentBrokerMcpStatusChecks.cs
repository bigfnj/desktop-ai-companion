using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunMcpStatusChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var empty = new McpStatusView(true, null, null,
            [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Codex)]);
        var native = new SessionView("synthetic-native-only", Guid.NewGuid(), "Synthetic Claude native", "claude-native",
            "idle", true, true, false, false);
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432/mcp", null,
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
            check(pane.Schema.Any(field => field.Id == "mcpStatus" && field.Kind == SettingKind.Info) &&
                pane.Load()["mcpStatus"].Contains("Enable AgentBroker", StringComparison.Ordinal),
                "MCP status: disabled module exposes connection information without claiming a ready endpoint");
            pane.Save(new Dictionary<string, string> { ["enabled"] = "true" });
            Pump(() => pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal), dispatcher);
            check(pane.Load()["status"].Contains("MCP ready", StringComparison.Ordinal) &&
                pane.Load()["status"].Contains("Claude: not seen", StringComparison.Ordinal) &&
                pane.Load()["status"].Contains("Codex: not seen", StringComparison.Ordinal),
                "MCP status: native discovery and ready listener do not invent a vendor MCP connection");

            int notifications = 0;
            pane.InfoChanged += () => notifications++;
            int saved = host.SettingsFor(Id).SaveCount, generation = module.generation, stops = supervisor.Stops;
            var seen = empty with { LastSuccessfulRequestUtc = now, LastToolsListUtc = now,
                Clients = [new(McpClientFamily.Other, LastToolsListUtc: now),
                    new(McpClientFamily.Claude, LastInitializeUtc: now.AddSeconds(-30)), new(McpClientFamily.Codex)] };
            var active = view with { Mcp = seen };
            module.ApplyStatus(new(true, true, "running", active));
            check(notifications == 1 && pane.Load()["status"].Contains("Claude: seen just now", StringComparison.Ordinal) &&
                pane.Load()["status"].Contains("Codex: not seen", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Tool list served just now", StringComparison.Ordinal),
                "MCP status: observed Claude handshake updates the existing status bar without attributing other tools to Codex");
            module.ApplyStatus(new(true, true, "running", active));
            check(notifications == 1, "MCP status: identical observations do not cause duplicate Info notifications");
            now = now.AddMinutes(2);
            module.ApplyStatus(new(true, true, "running", active));
            check(notifications == 2 && pane.Load()["status"].Contains("Claude: seen 2 min ago", StringComparison.Ordinal) &&
                pane.Load()["mcpStatus"].Contains("Idle clients connect on demand", StringComparison.Ordinal) &&
                !pane.Load()["mcpStatus"].Contains("disconnected", StringComparison.OrdinalIgnoreCase),
                "MCP status: idle traffic ages through existing polls without claiming an idle client disconnected");

            module.ApplyStatus(new(true, true, "running", active with { Mcp = null }));
            check(pane.Load()["status"].Contains("MCP status unknown", StringComparison.Ordinal) &&
                !pane.Load()["mcpStatus"].Contains("Claude: seen", StringComparison.Ordinal),
                "MCP status: an older worker without MCP observations clears stale client claims");
            module.ApplyStatus(new(true, true, "running", active with { Mcp = seen with { EndpointReady = false } }));
            check(pane.Load()["status"].Contains("MCP unavailable", StringComparison.Ordinal),
                "MCP status: a stopped endpoint overrides historical successful activity");
            foreach (McpClientActivityView[]? clients in new McpClientActivityView[]?[] {
                null, [], [new(McpClientFamily.Other), new(McpClientFamily.Claude), new(McpClientFamily.Claude)],
                [new(McpClientFamily.Other), new(McpClientFamily.Claude), new((McpClientFamily)99)], [null!, null!, null!] })
                check(FormatMcpStatus(seen with { Clients = clients! }, now).Summary.Contains("client status unknown", StringComparison.Ordinal),
                    "MCP status: malformed or duplicate client rows cannot display a false vendor connection");
            var future = empty with { LastToolsListUtc = now.AddHours(1), Clients = [new(McpClientFamily.Other),
                new(McpClientFamily.Claude, LastInitializeUtc: now.AddHours(1)), new(McpClientFamily.Codex)] };
            var display = FormatMcpStatus(future, now);
            check(display.Summary.Contains("Claude: activity time unknown", StringComparison.Ordinal) &&
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
}
