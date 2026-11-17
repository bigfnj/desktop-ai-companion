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
        // No catalogue case, by reason (U-m4): every fold check below starts from this fixture, so a product mutant that keeps
        // the healthy fixture open also breaks each "...FOLDS" check that expects a fold, and none can fail this label alone.
        // Its killers are those of the fold checks; a sole killer would have to target the fixture, which is not product code.
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

        // Round 6 (U-2, DECISIONS 59): two states the generic token rule keeps open, fed to the fold AND the summary, so a
        // NoAction slipped onto either one fails here rather than folding the band and counting the client OK.
        static void ClaudeToolsUnattributed(AgentBrokerModule module) =>
            module.snapshot = module.snapshot! with { Mcp = module.snapshot.Mcp! with { Clients =
                [new(McpClientFamily.Other), new(McpClientFamily.Claude, LastInitializeUtc: module.McpNow()),
                    new(McpClientFamily.Codex, LastInitializeUtc: module.McpNow(), LastToolsListUtc: module.McpNow())] } };
        check(!Fold(ClaudeToolsUnattributed) && Summary(ClaudeToolsUnattributed) == "1 OK, 0 notes",
            "CLIENTS_TOOLS_UNATTRIBUTED_OPENS: a client that connected but listed no tools keeps Clients open and is not counted OK");
        // Both inbox forms: nothing registered at all, and a registered inbox that carries no client identity.
        static void ClaudeInboxNone(AgentBrokerModule module) =>
            module.snapshot = module.snapshot! with
            {
                Sessions = module.snapshot.Sessions.Where(session => session.Label != "Synthetic Claude inbox").ToArray(),
                Mcp = module.snapshot.Mcp! with { Inboxes = module.snapshot.Mcp.Inboxes!.Where(inbox => inbox.Family != McpClientFamily.Claude).ToArray() }
            };
        static void ClaudeInboxUnattributed(AgentBrokerModule module) =>
            module.snapshot = module.snapshot! with
            {
                Mcp = module.snapshot.Mcp! with { Inboxes = module.snapshot.Mcp.Inboxes!.Where(inbox => inbox.Family != McpClientFamily.Claude).ToArray() }
            };
        check(!Fold(ClaudeInboxNone) && Summary(ClaudeInboxNone) == "1 OK, 0 notes" &&
            !Fold(ClaudeInboxUnattributed) && Summary(ClaudeInboxUnattributed) == "1 OK, 0 notes",
            "CLIENTS_INBOX_UNREGISTERED_OPENS: no inbox registered, or only inboxes without a client identity, keeps Clients open and is not counted OK");

        // Round 6 (U-3, DECISIONS 107): the report-only engine binding asks for nothing, so an unbound Codex session folds the
        // band and is counted OK, and the report line itself stays in the Codex states.
        AgentBrokerModule unbound = Fixture();
        unbound.snapshot = unbound.snapshot! with { Sessions = unbound.snapshot.Sessions.Select(session => session.Adapter == "codex-native"
            ? session with { Engine = new EngineBindingView(false, "foreign_holder") } : session).ToArray() };
        check(unbound.ClientsAreHealthy() && unbound.ClientsSummaryText() == "2 OK, 0 notes" &&
            unbound.ClientStateLines("codex").Any(line => line.StartsWith("⚠ Engine binding (report only): 0 of 1", StringComparison.Ordinal)),
            "CLIENTS_ENGINE_REPORT_FOLDS: an unbound engine-binding report asks for nothing, so Clients folds and still lists it");

        // Round 6 (U-q1, DECISIONS 107): with wake on, no discovered native session asks for nothing; Clients folds and each
        // client's folded header still names it.
        AgentBrokerModule noSessions = Fixture();
        noSessions.snapshot = noSessions.snapshot! with { Sessions = noSessions.snapshot.Sessions.Where(session => session.Adapter == "mcp").ToArray() };
        check(noSessions.ClientsAreHealthy() && noSessions.ClientsSummaryText() == "2 OK, 0 notes" &&
            noSessions.ClientDisplay("claude").Contains("⚠ wake 0/0", StringComparison.Ordinal) &&
            noSessions.ClientDisplay("codex").Contains("⚠ wake 0/0", StringComparison.Ordinal),
            "CLIENTS_ZERO_SESSIONS_FOLDS: with wake on and no session discovered Clients folds, and each header names wake 0/0");

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
            attentionSummary == "0 OK, 2 notes, 1 off" &&
            // Round 6 (U-4): a no-action note is still a note the folded band has to name.
            Summary(module => Notes(module, "claude-native", "claude_update_tolerant_delivery: Claude Code 2.1.322 is woken because Windows verified Anthropic's signature.")) == "2 OK, 1 note" &&
            // Round 6 (U-5): off counts distinct clients, and every one of the four switches can move it. The last input is the
            // default install (BrokerSettings.Read: both discovery switches on, both wake switches off).
            Summary(module => module.settings = module.settings with { ClaudePeerCredentials = false }) == "2 OK, 0 notes, 1 off" &&
            Summary(module => module.settings = module.settings with { ClaudeDiscovery = false }) == "2 OK, 0 notes, 1 off" &&
            Summary(module => module.settings = module.settings with { CodexDiscovery = false }) == "2 OK, 0 notes, 1 off" &&
            Summary(module => module.settings = module.settings with { CodexDiscovery = false, CodexPeerMessages = false }) == "2 OK, 0 notes, 1 off" &&
            Summary(module => module.settings = module.settings with { ClaudePeerCredentials = false, CodexPeerMessages = false }) == "2 OK, 0 notes, 2 off",
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
        // Round 6: "beside Pairing" is the schema position itself, the row directly after Pairing (U-m1), and each Clients
        // action is counted by its exact label, so a renamed action fails here instead of leaving an All() over nothing (U-m9).
        var schemaIds = pane.Schema.Select(field => field.Id).ToList();
        int pairingAt = schemaIds.IndexOf("pairingStatus");
        string[] clientActions = ["Check client setup", "Connect Codex MCP", "Connect Claude MCP"];
        check(pane.Schema.SingleOrDefault(field => field.Id == "clientsSummary") is
                { Label: "Clients", Group: "AgentBroker", Kind: SettingKind.Info } &&
            pairingAt >= 0 && schemaIds.IndexOf("clientsSummary") == pairingAt + 1 &&
            values.TryGetValue("clientsSummary", out string? summary) && summary == wired.ClientsSummaryText() &&
            wired.InfoValues().Contains(wired.ClientsSummaryText(), StringComparer.Ordinal) &&
            clientActions.All(label => pane.Actions.Count(action => action.Label == label && action.Group == "Clients") == 1),
            "CLIENTS_SUMMARY_PINNED: Clients summary stays beside Pairing and refreshes without renaming the Clients group");
    }
}
