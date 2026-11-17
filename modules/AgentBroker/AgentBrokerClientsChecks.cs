using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    // Each fold fixture is independent, so one mutated transition cannot poison later conditions.
    private static void RunClientsSummaryChecks(Action<bool, string> check)
    {
        static AgentBrokerModule Fixture()
        {
            DateTimeOffset now = new(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);
            var claude = new SessionView("claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Claude", "claude-native", "Idle", true, true, false, false);
            var codex = new SessionView("codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Codex", "codex-native", "Idle", true, true, false, false);
            var claudeInbox = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Claude inbox", "mcp", "Idle", true, false, false, false);
            var codexInbox = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Codex inbox", "mcp", "Idle", true, false, false, false);
            var mcp = new McpStatusView(true, now, now,
                [new(McpClientFamily.Other), new(McpClientFamily.Claude, LastInitializeUtc: now, LastToolsListUtc: now),
                    new(McpClientFamily.Codex, LastInitializeUtc: now, LastToolsListUtc: now)],
                [new(claudeInbox.SessionId, claudeInbox.Incarnation, McpClientFamily.Claude),
                    new(codexInbox.SessionId, codexInbox.Incarnation, McpClientFamily.Codex)]);
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic Clients", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
                [], [], [], [claude, codex, claudeInbox, codexInbox], new Dictionary<string, string>(), new string('A', 64), Mcp: mcp);
            return new AgentBrokerModule(new PaneControl(), new StatusSupervisor(view))
            {
                settings = new(true, "Synthetic Clients", 7432, 7433, true, true, true, true, true, true),
                snapshot = view, ownsWorker = true, McpNow = () => now,
                claudeInspection = new(ClientConfiguration.Configured, "user", "http", "connected", 0, now),
                codexInspection = new(ClientConfiguration.Configured, "user", "http", "unknown", 0, now)
            };
        }
        static void Notes(AgentBrokerModule module, string adapter, string text) =>
            module.snapshot = module.snapshot! with { AdapterNotes = new Dictionary<string, string> { [adapter] = text } };
        static bool Fold(Action<AgentBrokerModule>? change = null)
        {
            AgentBrokerModule module = Fixture();
            change?.Invoke(module);
            return module.ClientsAreHealthy();
        }
        static string Summary(Action<AgentBrokerModule>? change = null)
        {
            AgentBrokerModule module = Fixture();
            change?.Invoke(module);
            return module.ClientsSummaryText();
        }
        check(Fold(), "CLIENTS_HEALTHY_FOLDS: healthy current configuration, tools, inbox and native evidence folds Clients");
        check(!Fold(module => module.claudeInspection = null),
            "CLIENTS_UNCHECKED_OPENS: configuration not checked after restart keeps Clients open");
        check(Fold(module => module.clientSetupResults["claude"] = "Synthetic setup completed."),
            "CLIENTS_SETUP_RESULT_NEUTRAL: a stored setup result does not create actionable failure");
        check(Fold(module => Notes(module, "claude-native", "claude_update_tolerant_delivery: Claude Code 2.1.322 is woken because Windows verified Anthropic's signature.")),
            "CLIENTS_NO_ACTION_CLAUDE_NOTE_FOLDS: the real update-tolerant Claude note requires no action");
        check(Fold(module => Notes(module, "codex-native", "codex_update_tolerant_delivery: Publisher-verified builds remain eligible for native delivery.")),
            "CLIENTS_NO_ACTION_CODEX_NOTE_FOLDS: the real update-tolerant Codex note requires no action");
        check(Fold(module => module.settings = module.settings with { CodexPeerMessages = false }),
            "CLIENTS_WAKE_OFF_FOLDS: an owner-chosen native-wake off state requires no action");
        check(Fold(module => module.settings = module.settings with { ClaudeDiscovery = false }),
            "CLIENTS_DISCOVERY_OFF_FOLDS: an owner-chosen discovery off state requires no action");
        check(!Fold(module => Notes(module, "claude-native", "claude_unknown_note: review this state")),
            "CLIENTS_UNKNOWN_NOTE_OPENS: an unknown raw code stays actionable before display formatting");
        check(!Fold(module => Notes(module, "claude-native", "claude_discovery_truncated: some sessions may be missing")),
            "CLIENTS_TRUNCATED_NOTE_OPENS: a truncated discovery result stays actionable");
        check(!Fold(module => Notes(module, "unknown-adapter", "claude_update_tolerant_delivery: this code belongs to another adapter")),
            "CLIENTS_UNKNOWN_ADAPTER_NOTE_OPENS: an unattributed adapter note cannot inherit a no-action classification");
        check(!Fold(module => Notes(module, "codex-native", "claude_update_tolerant_delivery: this code belongs to Claude")),
            "CLIENTS_CLAUDE_CODE_ON_CODEX_OPENS: a Claude code cannot neutralize a Codex adapter note");
        check(!Fold(module => Notes(module, "claude-native", "codex_update_tolerant_delivery: this code belongs to Codex")),
            "CLIENTS_CODEX_CODE_ON_CLAUDE_OPENS: a Codex code cannot neutralize a Claude adapter note");
        check(!Fold(module => Notes(module, "claude-native", string.Join(ControlProtocol.NoteSeparator,
                Enumerable.Repeat("claude_update_tolerant_delivery: synthetic no-action note", 9)))),
            "CLIENTS_HIDDEN_NOTE_OPENS: a bounded display that omits a ninth note cannot imply complete healthy evidence");
        check(!Fold(module => module.snapshot = module.snapshot! with { AdapterErrors = new Dictionary<string, string> { ["unknown-adapter"] = "synthetic error" } }),
            "CLIENTS_ADAPTER_ERROR_OPENS: adapter errors keep Clients open");
        check(!Fold(module => module.snapshot = null),
            "CLIENTS_WORKER_UNAVAILABLE_OPENS: missing worker evidence does not fold Clients");

        static bool PaneFold(Action<AgentBrokerModule>? change = null)
        {
            AgentBrokerModule module = Fixture();
            change?.Invoke(module);
            OptionsPane pane = module.BuildOptions();
            pane.Load();
            return pane.Schema.Single(field => field.Id == "claudeClient").StartCollapsed;
        }
        check(PaneFold(), "CLIENTS_FOLD_WIRING_HEALTHY: LoadOptions starts healthy Clients folded");
        check(!PaneFold(module => module.settings = module.settings with { Enabled = false }),
            "CLIENTS_FOLD_WIRING_ATTENTION: LoadOptions keeps an actionable Clients state open");

        string attentionSummary = Summary(module =>
        {
            module.settings = module.settings with { CodexPeerMessages = false };
            module.snapshot = module.snapshot! with { AdapterNotes = new Dictionary<string, string>
            {
                ["claude-native"] = "summary_attention_one: synthetic warning",
                ["codex-native"] = "summary_attention_two: synthetic warning"
            } };
        });
        check(Summary() == "2 OK, 0 notes" &&
            Summary(module => Notes(module, "claude-native", "summary_attention_one: synthetic warning")) == "1 OK, 1 note" &&
            attentionSummary == "0 OK, 2 notes, 1 off",
            "CLIENTS_SUMMARY_COUNTS: the pinned summary counts OK clients, all notes and distinct off clients");

        AgentBrokerModule unknownNote = Fixture();
        Notes(unknownNote, "future\u202E&\nadapter", "future_warning: A future adapter needs attention.");
        string unknownDisplay = unknownNote.BuildOptions().Load()["nativeStatus"];
        bool unknownIdentityVisible = unknownDisplay.Contains("A future adapter needs attention.", StringComparison.Ordinal) &&
            unknownDisplay.Contains("\\u202E", StringComparison.Ordinal) && unknownDisplay.Contains("\\u0026", StringComparison.Ordinal) &&
            !unknownDisplay.Contains('\u202E') && !unknownDisplay.Contains('\n') && unknownDisplay.Length < 600;
        AgentBrokerModule boundedNotes = Fixture();
        Notes(boundedNotes, new string('\u202E', 100), string.Join(ControlProtocol.NoteSeparator,
            Enumerable.Range(1, 9).Select(index => "future_warning: row-" + index + " " + new string('x', 350))));
        string boundedDisplay = boundedNotes.NativeStatusText();
        check(unknownIdentityVisible && boundedDisplay.Length <= 4400 && boundedDisplay.Contains("row-8 ", StringComparison.Ordinal) &&
            !boundedDisplay.Contains("row-9 ", StringComparison.Ordinal) && boundedDisplay.EndsWith(" (1 more adapter note)", StringComparison.Ordinal),
            "CLIENTS_UNKNOWN_NOTE_VISIBLE: unknown notes are visible with escaped bounded identities, bounded rows and a count of omitted notes");

        AgentBrokerModule wired = Fixture();
        OptionsPane pane = wired.BuildOptions();
        var values = pane.Load();
        check(pane.Schema.SingleOrDefault(field => field.Id == "clientsSummary") is
                { Label: "Clients", Group: "AgentBroker", Kind: SettingKind.Info } &&
            values.TryGetValue("clientsSummary", out string? summary) && summary == wired.ClientsSummaryText() &&
            wired.InfoValues().Contains(wired.ClientsSummaryText(), StringComparer.Ordinal) &&
            pane.Actions.Where(action => action.Label is "Check client setup" or "Connect Codex MCP" or "Connect Claude MCP").All(action => action.Group == "Clients"),
            "CLIENTS_SUMMARY_PINNED: Clients summary stays beside Pairing and refreshes without renaming the Clients group");
    }
}
