using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunClientSetupChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        string worker = typeof(AgentBrokerModule).Assembly.Location;
        string directory = Path.Combine(Path.GetTempPath(), "agentbroker-client-setup-synthetic");
        string Codex(string fields, string? executable = null) => "{" + fields + ",\"transport\":{\"type\":\"stdio\",\"command\":" +
            System.Text.Json.JsonSerializer.Serialize(executable ?? worker) + ",\"args\":[\"--mcp-stdio\",\"--data\"," +
            System.Text.Json.JsonSerializer.Serialize(directory) + "]}}";
        const string privateReason = "SYNTHETIC_SETUP_DISABLED_PRIVATE_VALUE";
        foreach (var sample in new[]
        {
            (Fields: "\"enabled\":false", Expected: "disabled in Codex", Label: "CLIENT_SETUP_EXISTING_DISABLED"),
            (Fields: "\"enabled\":true,\"disabled_reason\":\"requirements\"", Expected: "policy owner", Label: "CLIENT_SETUP_EXISTING_REQUIREMENTS"),
            (Fields: "\"enabled\":true,\"disabled_reason\":\"" + privateReason + "\"", Expected: "disabled in Codex", Label: "CLIENT_SETUP_EXISTING_UNKNOWN_REASON"),
            (Fields: "\"enabled\":\"yes\"", Expected: "unrecognized", Label: "CLIENT_SETUP_EXISTING_BAD_ENABLED"),
            (Fields: "\"enabled\":true,\"disabled_reason\":17", Expected: "unrecognized", Label: "CLIENT_SETUP_EXISTING_BAD_REASON")
        })
        {
            var command = new ClientSetupCommand(new(0, Codex(sample.Fields), ""));
            string result = new AgentClientSetup(command).ConfigureAsync("codex", worker, directory, CancellationToken.None).GetAwaiter().GetResult();
            check(result.Contains(sample.Expected, StringComparison.Ordinal) &&
                !result.Contains("already configured", StringComparison.Ordinal) && !result.Contains(privateReason, StringComparison.Ordinal) &&
                command.Calls == 1, sample.Label + ": existing ineffective configuration is explained without an add or private diagnostic");
        }
        var ordinary = new ClientSetupCommand(new(0, Codex("\"enabled\":true"), ""));
        check(new AgentClientSetup(ordinary).ConfigureAsync("codex", worker, directory, CancellationToken.None).GetAwaiter().GetResult()
            .Contains("already configured", StringComparison.Ordinal) && ordinary.Calls == 1,
            "CLIENT_SETUP_EXISTING_ENABLED: an effective matching entry still reports configured without another add");

        void CompletionCase(bool duringCheck, string change, string label)
        {
            if (!File.Exists(WorkerExecutable())) throw new IOException("CLIENT_SETUP_FIXTURE_PACKAGED_WORKER_REQUIRED");
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>());
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(directory));
            host.SettingsFor(Id).Set("enabled", "true");
            var command = new ClientSetupCommand(new(0, Codex("\"enabled\":true", WorkerExecutable()), "")) { DelayCall = duringCheck ? 2 : 1 };
            var module = new AgentBrokerModule(new StatusControl { Response = new(true, "ok", view) }, new StatusSupervisor(view))
                { ClientSetup = new(command) };
            Task<string>? action = null;
            try
            {
                module.Init(host);
                Pump(() => module.snapshot is not null, dispatcher);
                int notifications = 0;
                host.OptionsPanes.Single().InfoChanged += () => notifications++;
                action = module.ConnectAndCheckAsync("codex");
                Pump(() => command.Calls == command.DelayCall, dispatcher);
                if (change == "restart")
                {
                    module.Restart();
                    Pump(() => module.snapshot is not null, dispatcher);
                }
                else if (change == "shutdown") module.Shutdown();
                else if (change == "cancel") module.run!.Cancel();
                int beforeCompletion = notifications;
                // An already completing vendor operation can report success even after cancellation.
                command.Completion.SetResult(command.Result);
                Pump(() => action.IsCompleted, dispatcher);
                string result = action.GetAwaiter().GetResult();
                if (change == "none")
                    check(result.Contains("already configured", StringComparison.Ordinal) &&
                        module.clientSetupResults.TryGetValue("codex", out string? recorded) && recorded == result &&
                        module.codexInspection?.State == ClientConfiguration.Configured && command.Calls == 2,
                        label + ": a current completion checks configuration and publishes its result");
                else
                    check(result.StartsWith("AgentBroker is already configured.", StringComparison.Ordinal) &&
                        result.Contains("broker connection changed", StringComparison.Ordinal) && result.Contains("Check client setup before retrying", StringComparison.Ordinal) &&
                        module.clientSetupResults.Count == 0 && module.codexInspection is null && command.Calls == command.DelayCall && notifications == beforeCompletion,
                        label + ": an obsolete completion neither checks a new run nor publishes its result");
            }
            finally
            {
                if (!command.Completion.Task.IsCompleted) command.Completion.TrySetResult(command.Result);
                if (action is not null) Pump(() => action.IsCompleted, dispatcher);
                module.Shutdown();
                Pump(() => module.pending.IsCompleted, dispatcher);
            }
        }

        CompletionCase(false, "restart", "CLIENT_SETUP_STALE_CONFIGURE_RESTART");
        CompletionCase(true, "restart", "CLIENT_SETUP_STALE_CHECK_RESTART");
        CompletionCase(false, "shutdown", "CLIENT_SETUP_STALE_CONFIGURE_SHUTDOWN");
        CompletionCase(false, "cancel", "CLIENT_SETUP_CANCELLED_TOKEN");
        CompletionCase(true, "none", "CLIENT_SETUP_CURRENT_COMPLETION");
        RunClientAddCompletionChecks(check, dispatcher);
        RunClientHttpSetupChecks(check, dispatcher);
    }

    private static void RunClientAddCompletionChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string endpoint = "http://127.0.0.1:7432/mcp";
        const string privateDiagnostic = "SYNTHETIC_ADD_COMPLETION_PRIVATE_VALUE";
        foreach (bool cancelled in new[] { false, true })
        {
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>());
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-add-completion-synthetic")));
            host.SettingsFor(Id).Set("enabled", "true");
            var command = new AddCompletionCommand(privateDiagnostic);
            var control = new StatusControl { Response = new(true, "ok", view, Connection: new(endpoint, null)) };
            var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(command) };
            Task<string>? action = null;
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                var pane = host.OptionsPanes.Single();
                int notifications = 0;
                pane.InfoChanged += () => notifications++;
                action = pane.Actions.Single(item => item.Label == "Connect Claude MCP").InvokeAsync();
                Pump(() => command.Calls.Count == 2 && command.Calls[1][1] == "add", dispatcher);
                if (!pane.Save(new Dictionary<string, string> { ["loopbackPort"] = "17432" }))
                    throw new InvalidOperationException("Synthetic port change was not applied.");
                Pump(() => module.snapshot is not null, dispatcher);
                int beforeCompletion = notifications;
                command.CompleteAfterWrite(cancelled);
                Pump(() => action.IsCompleted, dispatcher);
                string result = action.GetAwaiter().GetResult();
                string expected = cancelled ? "Setup was cancelled or timed out. Check the client's MCP settings before retrying."
                    : "AgentBroker MCP was added for this user in Claude Code";
                check(result.StartsWith(expected, StringComparison.Ordinal) &&
                    result.Contains("broker connection changed", StringComparison.Ordinal) && result.Contains("Check client setup before retrying", StringComparison.Ordinal) &&
                    !result.Contains(privateDiagnostic, StringComparison.Ordinal) && command.Entries[AgentClientSetup.ServerName] == endpoint &&
                    command.Calls.Count == 2 && module.settings.LoopbackPort == 17432 && module.clientSetupResults.Count == 0 &&
                    module.claudeInspection is null && notifications == beforeCompletion,
                    cancelled ? "CLIENT_SETUP_ADD_UNCERTAIN_AFTER_CHANGE: a cancelled submitted add preserves its retry guidance without stale status or private diagnostics"
                        : "CLIENT_SETUP_ADD_SUCCESS_AFTER_CHANGE: a completed add preserves its outcome and recheck guidance without publishing into the new connection");
            }
            finally
            {
                command.Completion.TrySetCanceled();
                if (action is not null) Pump(() => action.IsCompleted, dispatcher);
                module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher);
            }
        }
    }

    private sealed class AddCompletionCommand(string diagnostic) : IAgentClientCommand
    {
        internal List<string[]> Calls { get; } = [];
        internal Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource<ClientCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void CompleteAfterWrite(bool cancelled)
        {
            string[] add = Calls.Single(call => call[1] == "add");
            Entries[AgentClientSetup.ServerName] = add[^1];
            if (cancelled) Completion.SetException(new OperationCanceledException(diagnostic));
            else Completion.SetResult(new(0, diagnostic, diagnostic));
        }
        public Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(arguments.ToArray());
            if (arguments[1] == "add") return Completion.Task;
            return Task.FromResult(Entries.TryGetValue(AgentClientSetup.ServerName, out string? url)
                ? new ClientCommandResult(0, AgentClientSetup.ServerName + ":\n  Scope: User config\n  Status: √ Connected\n  Type: http\n  URL: " + url + "\n", diagnostic)
                : new ClientCommandResult(1, diagnostic, "No MCP server named " + AgentClientSetup.ServerName + " found."));
        }
    }

    private static void RunClientHttpSetupChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string endpoint = "http://127.0.0.1:7432/mcp";
        string directory = Path.Combine(Path.GetTempPath(), "agentbroker-client-http-synthetic");
        foreach (var sample in new[]
        {
            (Port: 7432, Url: endpoint, Fields: "\"enabled\":true", State: ClientConfiguration.Configured, Expected: "already configured", Label: "CLIENT_HTTP_SETUP_EXACT"),
            (Port: 7432, Url: "http://localhost:7432/mcp", Fields: "\"enabled\":true", State: ClientConfiguration.Configured, Expected: "already configured", Label: "CLIENT_HTTP_SETUP_LOCALHOST"),
            (Port: 17432, Url: "http://127.0.0.1:17432/mcp", Fields: "\"enabled\":true", State: ClientConfiguration.Configured, Expected: "already configured", Label: "CLIENT_HTTP_SETUP_CUSTOM_PORT"),
            (Port: 7432, Url: "http://127.0.0.1:7999/mcp", Fields: "\"enabled\":true", State: ClientConfiguration.DifferentEntry, Expected: "Review it", Label: "CLIENT_HTTP_SETUP_FOREIGN_PORT"),
            (Port: 7432, Url: "http://192.0.2.10:7432/mcp", Fields: "\"enabled\":true", State: ClientConfiguration.DifferentEntry, Expected: "Review it", Label: "CLIENT_HTTP_SETUP_REMOTE"),
            (Port: 7432, Url: endpoint, Fields: "\"enabled\":false", State: ClientConfiguration.Disabled, Expected: "disabled in Codex", Label: "CLIENT_HTTP_SETUP_DISABLED"),
            (Port: 7432, Url: endpoint, Fields: "\"enabled\":true,\"disabled_reason\":\"requirements\"", State: ClientConfiguration.PolicyBlocked, Expected: "policy owner", Label: "CLIENT_HTTP_SETUP_REQUIREMENTS")
        })
        {
            string json = "{" + sample.Fields + ",\"transport\":{\"type\":\"streamable_http\",\"url\":" + System.Text.Json.JsonSerializer.Serialize(sample.Url) + "}}";
            var command = new HttpSetupCommand { Existing = json };
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(directory)); host.SettingsFor(Id).Set("enabled", "true");
            host.SettingsFor(Id).Set("loopbackPort", sample.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:" + sample.Port, null,
                [], [], [], [], new Dictionary<string, string>());
            var module = new AgentBrokerModule(new StatusControl { Response = new(true, "ok", view) }, new StatusSupervisor(view))
                { ClientSetup = new(command) };
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                Task<string> action = module.ConnectAndCheckAsync("codex");
                Pump(() => action.IsCompleted, dispatcher);
                string result = action.GetAwaiter().GetResult();
                check(result.Contains(sample.Expected, StringComparison.Ordinal) && module.codexInspection?.State == sample.State &&
                    command.Calls.Count == 2 && command.Calls.All(call => call[1] == "get"),
                    sample.Label + ": the real connect action agrees with inspection and never replaces an existing HTTP entry");
            }
            finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
        }
        string worker = typeof(AgentBrokerModule).Assembly.Location;
        var absent = new HttpSetupCommand();
        string added = new AgentClientSetup(absent).ConfigureAsync("codex", worker, directory, CancellationToken.None,
            inspectionMcpEndpoint: endpoint).GetAwaiter().GetResult();
        check(added.Contains("was added", StringComparison.Ordinal) && absent.Calls.Count == 2 &&
            absent.Calls[1].SequenceEqual(new[] { "mcp", "add", AgentClientSetup.ServerName, "--", worker, "--mcp-stdio", "--data", directory }),
            "CLIENT_HTTP_SETUP_ADD_STAYS_STDIO: an inspection endpoint never changes Codex's add transport or typed arguments");
        foreach (var invalid in new[] { (Url: endpoint + "?private=synthetic", Label: "CLIENT_HTTP_SETUP_INVALID_QUERY"),
            (Url: "http://192.0.2.10:7432/mcp", Label: "CLIENT_HTTP_SETUP_INVALID_REMOTE") })
        {
            var invalidCommand = new HttpSetupCommand { Existing = "{\"transport\":{\"type\":\"streamable_http\",\"url\":" + System.Text.Json.JsonSerializer.Serialize(invalid.Url) + "}}" };
            string result = new AgentClientSetup(invalidCommand).ConfigureAsync("codex", worker, directory, CancellationToken.None,
                inspectionMcpEndpoint: invalid.Url).GetAwaiter().GetResult();
            check(result.Contains("invalid inspection MCP endpoint", StringComparison.Ordinal) && invalidCommand.Calls.Count == 0,
                invalid.Label + ": an untrusted inspection endpoint cannot bless an existing entry or invoke the CLI");
        }
    }

    private sealed class HttpSetupCommand : IAgentClientCommand
    {
        internal string? Existing;
        internal List<string[]> Calls { get; } = [];
        public Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(arguments.ToArray());
            return Task.FromResult(arguments[1] == "get"
                ? Existing is null ? new ClientCommandResult(1, "", "No MCP server named " + AgentClientSetup.ServerName + " found.") : new(0, Existing, "")
                : new ClientCommandResult(0, "", ""));
        }
    }

    private sealed class ClientSetupCommand(ClientCommandResult result) : IAgentClientCommand
    {
        internal ClientCommandResult Result { get; } = result;
        internal int Calls;
        internal int DelayCall;
        internal TaskCompletionSource<ClientCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Calls == DelayCall ? Completion.Task : Task.FromResult(Result);
        }
    }
}
