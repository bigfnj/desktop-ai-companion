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
        RunClientConnectionCancellationChecks(check, dispatcher);
        RunClientAddCompletionChecks(check, dispatcher);
        RunClientHttpSetupChecks(check, dispatcher);
        RunClientHttpConnectionChecks(check, dispatcher);
        RunClientUnavailableConnectionChecks(check, dispatcher);
        RunClientPreHelperOutcomeChecks(check, dispatcher);
        RunClientLiveEndpointChecks(check, dispatcher);
    }

    private static void RunClientLiveEndpointChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string live = "http://127.0.0.1:7432/mcp";
        const string stale = "http://127.0.0.1:17432/mcp";
        string directory = Path.Combine(Path.GetTempPath(), "agentbroker-client-live-port-synthetic");
        foreach (bool matchesLive in new[] { true, false })
        {
            string url = matchesLive ? live : stale;
            var command = new HttpSetupCommand { Existing = "{\"enabled\":true,\"transport\":{\"type\":\"streamable_http\",\"url\":\"" + url + "\"}}" };
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(directory)); host.SettingsFor(Id).Set("enabled", "true");
            host.SettingsFor(Id).Set("loopbackPort", "17432");
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>());
            var control = new StatusControl { Response = new(true, "ok", Connection: new(live, null)) };
            var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(command) };
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                var expected = matchesLive ? ClientConfiguration.Configured : ClientConfiguration.DifferentEntry;
                string suffix = matchesLive ? "LIVE" : "STALE_SETTINGS";
                Task<string> action = module.CheckClientSetupAsync("codex"); Pump(() => action.IsCompleted, dispatcher);
                string inspected = action.GetAwaiter().GetResult();
                check(module.settings.LoopbackPort == 17432 && module.codexInspection?.State == expected &&
                    inspected.Contains(matchesLive ? "configured" : "needs review", StringComparison.Ordinal) &&
                    command.Calls.Count == 1 && command.Calls[0][1] == "get" &&
                    control.Requests.Count(item => item == "connection") == 1,
                    "CLIENT_HTTP_LIVE_PORT_CHECK_" + suffix + ": inspection uses the verified live port when stored settings differ");
                command.Calls.Clear(); control.Requests.Clear();
                action = module.ConnectAndCheckAsync("codex"); Pump(() => action.IsCompleted, dispatcher);
                string connected = action.GetAwaiter().GetResult();
                check(connected.Contains(matchesLive ? "already configured" : "Review it", StringComparison.Ordinal) &&
                    module.codexInspection?.State == expected && command.Calls.Count == 2 &&
                    command.Calls.All(call => call[1] == "get") && control.Requests.Count(item => item == "connection") == 2,
                    "CLIENT_HTTP_LIVE_PORT_CONNECT_" + suffix + ": Connect compares existing HTTP entries with the verified live port without replacing them");
            }
            finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
        }

        var absent = new HttpSetupCommand();
        var addHost = new TestHost();
        addHost.UseStorage(Id, new TestStorage(directory)); addHost.SettingsFor(Id).Set("enabled", "true");
        addHost.SettingsFor(Id).Set("loopbackPort", "17432");
        var addView = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
            [], [], [], [], new Dictionary<string, string>());
        var addModule = new AgentBrokerModule(new StatusControl { Response = new(true, "ok", Connection: new(live, null)) },
            new StatusSupervisor(addView)) { ClientSetup = new(absent) };
        try
        {
            addModule.Init(addHost); Pump(() => addModule.snapshot is not null, dispatcher);
            Task<string> action = addModule.ConnectAndCheckAsync("claude"); Pump(() => action.IsCompleted, dispatcher);
            string result = action.GetAwaiter().GetResult();
            string[][] additions = absent.Calls.Where(call => call[1] == "add").ToArray();
            check(result.Contains("was added", StringComparison.Ordinal) && additions.Length == 1 &&
                additions[0][^1] == live && !additions[0].Contains(stale) && addModule.settings.LoopbackPort == 17432,
                "CLIENT_HTTP_LIVE_PORT_CLAUDE_ADD: an absent Claude entry is added with the verified live URL rather than stale stored settings");
        }
        finally { addModule.Shutdown(); Pump(() => addModule.pending.IsCompleted, dispatcher); }
    }

    private static void RunClientConnectionCancellationChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string endpoint = "http://127.0.0.1:7432/mcp";
        const string privateDiagnostic = "SYNTHETIC_CONNECTION_CANCELLATION_PRIVATE_VALUE";
        foreach (string client in new[] { "claude", "codex" })
        foreach (string change in new[] { "restart", "shutdown", "cancel" })
        foreach (bool cancelledReply in new[] { false, true })
        {
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>());
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-client-connection-synthetic")));
            host.SettingsFor(Id).Set("enabled", "true");
            var control = new ClientConnectionControl(new(true, "ok", Connection: new(endpoint, null)));
            var command = new ClientSetupCommand(new(0, privateDiagnostic, privateDiagnostic));
            var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(command) };
            Task<string>? action = null;
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                int notifications = 0;
                host.OptionsPanes.Single().InfoChanged += () => notifications++;
                action = module.ConnectAndCheckAsync(client);
                Pump(() => control.Requests.Contains("connection"), dispatcher);
                if (change == "restart")
                {
                    if (!host.OptionsPanes.Single().Save(new Dictionary<string, string> { ["loopbackPort"] = "17432" }))
                        throw new InvalidOperationException("Synthetic connection-probe settings change was not applied.");
                    Pump(() => module.snapshot is not null, dispatcher);
                }
                else if (change == "shutdown") module.Shutdown();
                else module.run!.Cancel();
                int beforeCompletion = notifications;
                if (cancelledReply) control.Completion.SetException(new OperationCanceledException(privateDiagnostic));
                else control.Completion.SetResult(control.Response);
                Pump(() => action.IsCompleted, dispatcher);
                string result = action.GetAwaiter().GetResult();
                check(result.Split("broker connection changed", StringSplitOptions.None).Length == 2 &&
                    result.Contains("before a client command started", StringComparison.Ordinal) &&
                    result.Contains("Check client setup before retrying", StringComparison.Ordinal) &&
                    !result.Contains(privateDiagnostic, StringComparison.Ordinal) && command.Calls == 0 &&
                    module.clientSetupResults.Count == 0 && module.claudeInspection is null && module.codexInspection is null &&
                    notifications == beforeCompletion,
                    "CLIENT_SETUP_CONNECTION_CANCELLED_" + client.ToUpperInvariant() + "_" + change.ToUpperInvariant() +
                    (cancelledReply ? "_CANCELLED_REPLY" : "_LATE_REPLY") +
                    ": cancellation before client setup gives one connection-change explanation, no vendor call and no cached client state");
            }
            finally
            {
                control.Completion.TrySetCanceled();
                if (action is not null) Pump(() => action.IsCompleted, dispatcher);
                module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher);
            }
        }
    }

    private sealed class ClientConnectionControl(ControlResponse response) : IBrokerControlClient
    {
        internal ControlResponse Response { get; } = response;
        internal List<string> Requests { get; } = [];
        internal TaskCompletionSource<ControlResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request.Operation);
            return request.Operation == "connection" ? Completion.Task : Task.FromResult(Response);
        }
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
            var module = new AgentBrokerModule(new StatusControl { Response = new(true, "ok", view,
                Connection: new("http://127.0.0.1:" + sample.Port + "/mcp", null)) }, new StatusSupervisor(view))
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

    private static void RunClientHttpConnectionChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string endpoint = "http://127.0.0.1:7432/mcp";
        const string privateToken = "SYNTHETIC_HTTP_CONNECTION_PRIVATE_TOKEN";
        foreach (var sample in new[]
        {
            (NoAuth: true, Connection: new LocalConnection(endpoint, null), Url: endpoint, State: ClientConfiguration.Configured, Expected: "already configured", Label: "TOKEN_FREE"),
            (NoAuth: false, Connection: new LocalConnection(endpoint, privateToken), Url: endpoint, State: ClientConfiguration.DifferentEntry, Expected: "Review it", Label: "TOKEN_REQUIRED"),
            (NoAuth: true, Connection: new LocalConnection(endpoint, privateToken), Url: endpoint, State: ClientConfiguration.DifferentEntry, Expected: "Review it", Label: "ACTUAL_TOKEN_REQUIRED"),
            (NoAuth: true, Connection: new LocalConnection("http://192.0.2.10:7432/mcp", null), Url: "http://192.0.2.10:7432/mcp", State: ClientConfiguration.ConnectionUnverified, Expected: "Could not verify this HTTP entry", Label: "UNVERIFIED_CONNECTION")
        })
        {
            string existing = "{\"enabled\":true,\"transport\":{\"type\":\"streamable_http\",\"url\":" +
                System.Text.Json.JsonSerializer.Serialize(sample.Url) + "}}";
            var command = new HttpSetupCommand { Existing = existing };
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-client-http-connection-synthetic")));
            host.SettingsFor(Id).Set("enabled", "true");
            host.SettingsFor(Id).Set("noAuthLoopback", sample.NoAuth ? "true" : "false");
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>(), Mcp: new(true, null, null, []));
            var control = new StatusControl { Response = new(true, "ok", Connection: sample.Connection) };
            var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(command) };
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                Task<string> inspection = module.CheckClientSetupAsync("codex");
                Pump(() => inspection.IsCompleted, dispatcher);
                string inspected = inspection.GetAwaiter().GetResult();
                check(module.codexInspection?.State == sample.State && command.Calls.Count == 1 &&
                    command.Calls[0][1] == "get" && control.Requests.Count(item => item == "connection") == 1 &&
                    !inspected.Contains(privateToken, StringComparison.Ordinal),
                    "CLIENT_HTTP_CHECK_" + sample.Label + ": inspection recognizes HTTP only from a verified token-free live connection");
                if (sample.State == ClientConfiguration.DifferentEntry)
                {
                    string display = module.ClientDisplay("codex");
                    string[] trayLines = module.ClientStateLines("codex").ToArray();
                    check(inspected.Contains("needs review", StringComparison.Ordinal) &&
                        display.Contains("does not match this broker's supported connection", StringComparison.Ordinal) &&
                        trayLines.Any(line => line.Contains("not verified for this connection", StringComparison.Ordinal)) &&
                        !display.Contains("points elsewhere", StringComparison.Ordinal) &&
                        !display.Contains("does not point at this broker", StringComparison.Ordinal),
                        "CLIENT_HTTP_CHECK_" + sample.Label + "_WORDING: an incompatible entry needs review without falsely claiming its URL points elsewhere");
                }
                command.Calls.Clear(); control.Requests.Clear();
                Task<string> action = module.ConnectAndCheckAsync("codex");
                Pump(() => action.IsCompleted, dispatcher);
                string result = action.GetAwaiter().GetResult();
                check(result.Contains(sample.Expected, StringComparison.Ordinal) && module.codexInspection?.State == sample.State &&
                    command.Calls.Count == 2 && command.Calls.All(call => call[1] == "get") &&
                    control.Requests.Count(item => item == "connection") == 2 &&
                    !result.Contains(privateToken, StringComparison.Ordinal),
                    "CLIENT_HTTP_CONNECT_" + sample.Label + ": Connect preserves token-incompatible or unverified existing entries without claiming configured");
            }
            finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
        }
    }

    private static void RunClientUnavailableConnectionChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string endpoint = "http://127.0.0.1:7432/mcp";
        const string privateDiagnostic = "SYNTHETIC_UNAVAILABLE_CONNECTION_PRIVATE_VALUE";
        string directory = Path.Combine(Path.GetTempPath(), "agentbroker-client-unavailable-synthetic");
        foreach (string client in new[] { "claude", "codex" })
        foreach (string transport in new[] { "http", "stdio" })
        foreach (string failure in new[] { "FAULT", "NEGATIVE", "MISSING", "INVALID" })
        {
            string worker = WorkerExecutable();
            string existing = client == "claude"
                ? AgentClientSetup.ServerName + ":\n  Scope: User config\n  Status: √ Connected\n  Type: " + transport + "\n" +
                    (transport == "http" ? "  URL: " + endpoint + "\n" : "  Command: " + worker + "\n  Args: --mcp-stdio --data " + directory + "\n")
                : "{\"enabled\":true,\"transport\":" + (transport == "http"
                    ? "{\"type\":\"streamable_http\",\"url\":\"" + endpoint + "\"}}"
                    : "{\"type\":\"stdio\",\"command\":" + System.Text.Json.JsonSerializer.Serialize(worker) +
                        ",\"args\":[\"--mcp-stdio\",\"--data\"," + System.Text.Json.JsonSerializer.Serialize(directory) + "]}}");
            var command = new HttpSetupCommand { Existing = existing };
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(directory)); host.SettingsFor(Id).Set("enabled", "true");
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>(), Mcp: new(true, null, null, []));
            var control = new StatusControl
            {
                Failure = failure == "FAULT" ? new IOException(privateDiagnostic) : null,
                Response = failure switch
                {
                    "NEGATIVE" => new(false, privateDiagnostic, Connection: new(endpoint, null)),
                    "INVALID" => new(true, "ok", Connection: new("http://192.0.2.10:7432/mcp", null)),
                    _ => new(true, "ok")
                }
            };
            var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(command) };
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                Task<string> action = module.CheckClientSetupAsync(client);
                Pump(() => action.IsCompleted, dispatcher);
                string result = action.GetAwaiter().GetResult();
                string display = module.ClientDisplay(client);
                var tray = module.ClientTrayItems().Single(item => item.Label.StartsWith(ClientName(client) + ":", StringComparison.Ordinal));
                var expected = transport == "http" ? ClientConfiguration.ConnectionUnverified : ClientConfiguration.Configured;
                check(module.Inspection(client)?.State == expected && command.Calls.Count == 1 && command.Calls[0][1] == "get" &&
                    control.Requests.Count(item => item == "connection") == 1 &&
                    !display.Contains(privateDiagnostic, StringComparison.Ordinal) &&
                    (transport != "http" || result.Contains("HTTP entry not verified", StringComparison.Ordinal) &&
                        display.Contains("could not be verified", StringComparison.Ordinal) &&
                        !display.Contains("does not match", StringComparison.Ordinal) && !display.Contains("needs review", StringComparison.Ordinal) &&
                        tray.BuildChildren!().Any(item => item.Label.Contains("HTTP entry not verified", StringComparison.Ordinal))),
                    "CLIENT_CONNECTION_UNAVAILABLE_" + client.ToUpperInvariant() + "_" + transport.ToUpperInvariant() + "_" + failure +
                    ": an unavailable broker probe leaves HTTP unverified while exact stdio stays configured in pane and tray");
                if (client == "codex" && transport == "http")
                {
                    command.Calls.Clear(); control.Requests.Clear();
                    action = module.ConnectAndCheckAsync(client); Pump(() => action.IsCompleted, dispatcher);
                    string connected = action.GetAwaiter().GetResult();
                    check(connected.StartsWith("Could not verify this HTTP entry", StringComparison.Ordinal) &&
                        module.codexInspection?.State == ClientConfiguration.ConnectionUnverified && command.Calls.Count == 2 &&
                        command.Calls.All(call => call[1] == "get") && !connected.Contains(privateDiagnostic, StringComparison.Ordinal),
                        "CLIENT_CONNECTION_UNAVAILABLE_CODEX_CONNECT_" + failure + ": Connect preserves an existing HTTP entry with unverified guidance");
                }
                control.Failure = null; control.Response = new(true, "ok", Connection: new(endpoint, null));
                action = module.CheckClientSetupAsync(client); Pump(() => action.IsCompleted, dispatcher);
                check(module.Inspection(client)?.State == ClientConfiguration.Configured &&
                    !module.ClientDisplay(client).Contains("HTTP entry not verified", StringComparison.Ordinal),
                    "CLIENT_CONNECTION_AVAILABLE_AGAIN_" + client.ToUpperInvariant() + "_" + transport.ToUpperInvariant() + "_" + failure +
                    ": a fresh verified check replaces an unavailable result without a module restart");
            }
            finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
        }
    }

    private static void RunClientPreHelperOutcomeChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string endpoint = "http://127.0.0.1:7432/mcp";
        const string privateDiagnostic = "SYNTHETIC_PREHELPER_CONNECTION_PRIVATE_VALUE";
        foreach (string failure in new[] { "NEGATIVE", "FAULT", "MISSING", "INVALID" })
        {
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-client-prehelper-synthetic")));
            host.SettingsFor(Id).Set("enabled", "true");
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [], [], [], [], new Dictionary<string, string>());
            var control = new StatusControl { Response = new(true, "ok", Connection: new(endpoint, null)) };
            var command = new HttpSetupCommand();
            var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(command) };
            try
            {
                module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
                Task<string> inspection = module.CheckClientSetupAsync("claude"); Pump(() => inspection.IsCompleted, dispatcher);
                if (module.claudeInspection?.State != ClientConfiguration.Absent) throw new InvalidOperationException("Synthetic tray Connect action was not exposed.");
                module.clientSetupResults["claude"] = "Earlier setup guidance.";
                command.Calls.Clear(); control.Requests.Clear();
                control.Failure = failure == "FAULT" ? new IOException(privateDiagnostic) : null;
                control.Response = failure switch
                {
                    "NEGATIVE" => new(false, privateDiagnostic, Connection: new(endpoint, null)),
                    "INVALID" => new(true, "ok", Connection: new("http://192.0.2.10:7432/mcp", null)),
                    _ => new(true, "ok")
                };
                int outcomeNotifications = 0;
                host.OptionsPanes.Single().InfoChanged += () =>
                {
                    if (module.clientSetupResults.TryGetValue("claude", out string? published) &&
                        published.StartsWith("Could not verify the broker connection.", StringComparison.Ordinal)) outcomeNotifications++;
                };
                var tray = module.ClientTrayItems().Single(item => item.Label.StartsWith("Claude Code:", StringComparison.Ordinal));
                // These already-completed fake responses finish the async action before Click returns.
                tray.BuildChildren!().Single(item => item.Label == "Connect Claude MCP").Click!.Invoke();
                check(module.clientSetupResults.TryGetValue("claude", out string? result) &&
                    result.StartsWith("Could not verify the broker connection.", StringComparison.Ordinal) &&
                    !result.Contains("broker connection changed", StringComparison.Ordinal) && !result.Contains(privateDiagnostic, StringComparison.Ordinal) &&
                    module.ClientDisplay("claude").Contains("Last setup action: " + result, StringComparison.Ordinal) &&
                    tray.BuildChildren!().Any(item => item.Label.StartsWith("Last setup action: Could not verify", StringComparison.Ordinal)) &&
                    outcomeNotifications == 1 && command.Calls.Count == 0 && control.Requests.SequenceEqual(new[] { "connection" }) &&
                    module.claudeInspection.State == ClientConfiguration.Absent,
                    "CLIENT_SETUP_PREHELPER_CURRENT_" + failure + ": a tray Connect failure replaces stale guidance and notifies without any vendor command");
            }
            finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
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
