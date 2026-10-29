using System.Collections.Concurrent;
using System.Diagnostics;
using AgentBroker.Protocol;
using DesktopAICompanion.ModuleKit.Testing;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    /// <summary>Real module Init/tray/options/lifecycle with injected control and supervisor. No worker child, external network or native credentials.</summary>
    public static bool SelfTest(out string detail)
    {
        var failures = new List<string>();
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) failures.Add(name); }
        SynchronizationContext? previous = SynchronizationContext.Current;
        var dispatcher = new TestDispatcher();
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        AgentBrokerModule? module = null;
        try
        {
            AgentBrokerSupervisorChecks.RunAsync(Check).GetAwaiter().GetResult();
            RunStatusChecks(Check, dispatcher);
            RunMcpStatusChecks(Check, dispatcher);
            RunLifetimeChecks(Check, dispatcher);
            RunPairingChecks(Check, dispatcher);
            RunFirewallChecks(Check, dispatcher);
            RunOnboardingChecks(Check, dispatcher);
            RunClientRouteChecks(Check);
            var host = new TestHost();
            string directory = Path.Combine(Path.GetTempPath(), "agentbroker-synthetic-" + Guid.NewGuid().ToString("N"));
            host.UseStorage(Id, new TestStorage(directory));
            var control = new TestControl();
            var supervisor = new TestSupervisor();
            module = new(control, supervisor);
            module.Init(host);
            OptionsPane pane = host.OptionsPanes.Single();
            var defaults = pane.Load();
            Check(defaults["enabled"] == "false" && defaults["codexPeerMessages"] == "false" &&
                defaults["claudePeerCredentials"] == "false", "broker and native delivery default off");
            Check(defaults["codexDiscovery"] == "true" && defaults["claudeDiscovery"] == "true",
                "metadata discovery defaults on");
            Check(supervisor.Launches.Count == 0 && control.Requests.Count == 0, "disabled Init does no worker or pipe work");
            Check(module.Info.MinHostVersion == "1.6.1" &&
                module.Info.Permissions.HasFlag(ModulePermissions.LaunchProcess) &&
                module.Info.Permissions.HasFlag(ModulePermissions.AgentTranscripts), "ABI and disclosures");
            Check(!pane.Schema.Any(f => f.Id.Contains("source", StringComparison.OrdinalIgnoreCase)),
                "no invented MCP app source setting");
            Check(!pane.Save(new Dictionary<string, string> { ["loopbackPort"] = "7433", ["lanPort"] = "7433" }),
                "reject colliding ports");
            Check(!pane.Save(new Dictionary<string, string> { ["name"] = "bad\nname" }), "reject control characters");
            var edits = new Dictionary<string, string> { ["enabled"] = "true", ["name"] = "Synthetic broker" };
            Check(pane.Save(edits), "enable saved");
            Pump(() => supervisor.Launches.Count > 0, dispatcher);
            WorkerLaunch launch = supervisor.Launches.First();
            Check(launch.DataDirectory == directory && launch.ParentPid == Environment.ProcessId &&
                launch.ParentCreationFileTime > 0 && launch.Executable.EndsWith(
                    Path.Combine("worker", "AgentBroker.Worker.exe"), StringComparison.OrdinalIgnoreCase),
                "stable module storage nested worker and parent identity");
            var start = new ProcessStartInfo("synthetic.exe");
            WorkerSupervisor.AddArguments(start, launch);
            Check(start.ArgumentList.Contains("--parent-start") && start.ArgumentList.Contains("--codex-peer-messages") &&
                !start.ArgumentList.Contains("--codex-mcp-app-source"), "worker args bounded typed values no fake app ID");
            // 1.5.0 (the owner's decision of 2026-10-29): update-tolerant Codex delivery is the default; off is strict.
            int tolerantFlag = start.ArgumentList.IndexOf("--codex-update-tolerant-delivery");
            Check(defaults["codexUpdateTolerantDelivery"] == "true" && tolerantFlag >= 0 && start.ArgumentList[tolerantFlag + 1] == "true",
                "update-tolerant Codex delivery defaults on and reaches the default worker launch");
            var strictStart = new ProcessStartInfo("synthetic.exe");
            WorkerSupervisor.AddArguments(strictStart, launch with { Settings = launch.Settings with { CodexUpdateTolerantDelivery = false } });
            Check(!strictStart.ArgumentList.Contains("--codex-update-tolerant-delivery"),
                "WITNESS: strict mode (the switch off) keeps the flag out of the worker launch, so the worker admits exact pins only");

            var peer = new PeerView(Guid.NewGuid(), "Peer&\nName" + new string('P', 70), "https://example.invalid:7433", new string('A', 64));
            var session = new SessionView("synthetic-session", Guid.NewGuid(), "Synthetic", "codex", "Unknown", false, false, false, false);
            const string comparisonCode = "01234567 89ABCDEF FEDCBA98 76543210";
            var offer = new PairingView(Guid.NewGuid(), peer, comparisonCode, DateTimeOffset.UtcNow.AddMinutes(2), false);
            var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
                [peer], [peer], [offer], [session], new Dictionary<string, string>());
            supervisor.View = view;
            module.ApplyStatus(new(true, false, "attached_standby", view));
            Check(host.Asks.Count == 1 && host.Asks[0].Question.Text.Length <= 240 &&
                host.Asks[0].Question.Text.Contains("current and future", StringComparison.Ordinal) &&
                host.Asks[0].Question.Text.Contains(comparisonCode, StringComparison.Ordinal), "pairing scope full 128-bit code and ABI bounds");
            Check(host.Asks[0].Question.TimeoutChoiceId == "reject", "pairing safely rejects on timeout");
            module.ApplyStatus(new(true, false, "attached_standby", view));
            Check(host.Asks.Count == 1, "one question per pairing offer");
            host.Asks[0].Completion.SetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = "allow" });
            Pump(() => control.Requests.Any(r => r.Operation == "pair-approve"), dispatcher);
            Check(control.Requests.Single(r => r.Operation == "pair-approve").Id == offer.RequestId,
                "explicit allow approves exact offer");
            int asked = host.Asks.Count;
            var outbound = offer with { RequestId = Guid.NewGuid(), Outbound = true };
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [outbound] }));
            Check(host.Asks.Count == asked + 1 && host.Asks.Last().Question.Text.Contains(comparisonCode, StringComparison.Ordinal) &&
                host.TrayItems.Single().BuildChildren().Any(t => t.Label.Contains(comparisonCode, StringComparison.Ordinal)),
                "outgoing request requires separate final approval and displays the complete code");
            host.Asks.Last().Completion.SetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Chosen, ChoiceId = "allow" });
            Pump(() => control.Requests.Any(r => r.Operation == "pair-approve" && r.Id == outbound.RequestId), dispatcher);
            asked = host.Asks.Count;
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [outbound with { LocalApproved = true }] }));
            Check(host.Asks.Count == asked && host.TrayItems.Single().BuildChildren().Single(t =>
                t.Label.Contains(comparisonCode, StringComparison.Ordinal)).BuildChildren().First().Label.StartsWith("Waiting", StringComparison.Ordinal),
                "local confirmation waits for authenticated remote approval without another consent prompt");

            var discovered = peer with { BrokerId = Guid.NewGuid() };
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [], Discovered = [discovered] }));
            Check(host.Asks.Count == asked + 1 && host.Asks.Last().Question.Text.Length <= 240 &&
                host.Asks.Last().Question.Text.Contains("current and future", StringComparison.Ordinal),
                "new broker discovery prompts explicit authorization within card bounds");
            int beforeDiscovery = control.Requests.Count;
            host.Asks.Last().Completion.SetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.TimedOut, ChoiceId = "later" });
            Pump(() => !module.discoveryPromptOpen, dispatcher);
            Check(control.Requests.Count == beforeDiscovery, "unanswered discovery never creates a pairing request");
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [] }));

            // N-agentbroker-notice-loss-signal-01 (1.5.0): dropped notices reach Status as counts by reason, naming nothing.
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [], DroppedNotices = new(2, 0, 1, DateTimeOffset.UtcNow) }));
            string dropStatus = pane.Load()["status"];
            Check(dropStatus.Contains("Since the broker started, 3 delivery notices were dropped undelivered (2 while the sender " +
                "was away, 1 while the sender's queue was full).", StringComparison.Ordinal), "Status counts dropped delivery notices by reason: " + dropStatus);
            Check(AgentBrokerModule.NoticeDropText(new(0, 1, 0, null)) ==
                " Since the broker started, 1 delivery notice was dropped undelivered (1 while the store was full).",
                "one dropped notice reads in the singular and names the store");
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [] }));
            Check(!pane.Load()["status"].Contains("delivery notice", StringComparison.Ordinal),
                "WITNESS: a snapshot with nothing dropped says nothing about notices");

            Check(pane.Load()["manualPeer"] == "", "broker invitation is not persisted");
            Check(module.PairInvitationAsync(new Dictionary<string, string> { ["manualPeer"] = "{" }).GetAwaiter().GetResult()
                .Contains("invalid", StringComparison.Ordinal), "malformed manual invitation is rejected before networking");
            var manualPeer = peer with { BrokerId = Guid.NewGuid() };
            var invitation = new Dictionary<string, string> { ["manualPeer"] = System.Text.Json.JsonSerializer.Serialize(manualPeer, ControlProtocol.Json) };
            control.Success = false;
            Check(module.PairInvitationAsync(invitation).GetAwaiter().GetResult().Contains("could not", StringComparison.Ordinal),
                "manual invitation does not claim success after broker refusal");
            control.Success = true;
            Check(module.PairInvitationAsync(invitation).GetAwaiter().GetResult().Contains("Pairing requested", StringComparison.Ordinal),
                "manual invitation uses exact peer and ordinary pairing approval flow");

            string syntheticWorker = typeof(AgentBrokerModule).Assembly.Location;
            var setupCommand = new TestClientCommand();
            var setup = new AgentClientSetup(setupCommand);
            string setupResult = setup.ConfigureAsync("codex", syntheticWorker, directory, CancellationToken.None).GetAwaiter().GetResult();
            Check(setupResult.Contains("added", StringComparison.Ordinal) && setupCommand.Calls.Count == 2 &&
                setupCommand.Calls[1].Arguments.Contains("--mcp-stdio") && setupCommand.Calls[1].Arguments.Contains(directory) &&
                !setupCommand.Calls[1].Arguments.Any(a => a.Contains("token", StringComparison.OrdinalIgnoreCase)),
                "client setup adds typed stdio arguments without persisting connection tokens");
            setupCommand.Existing = "{}"; setupCommand.Calls.Clear();
            setup.ConfigureAsync("codex", syntheticWorker, directory, CancellationToken.None).GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 1, "existing client entry is never silently overwritten");
            setupCommand.Existing = null; setupCommand.Calls.Clear();
            string spacedDirectory = directory + " directory with spaces";
            setupResult = setup.ConfigureAsync("claude", syntheticWorker, spacedDirectory, CancellationToken.None).GetAwaiter().GetResult();
            Check(setupResult.Contains("added", StringComparison.Ordinal) && setupCommand.Calls.Count == 2 &&
                setupCommand.Calls[1].Executable == "claude" && setupCommand.Calls[1].Arguments.SequenceEqual(
                    new[] { "mcp", "add", "--transport", "stdio", "--scope", "user", AgentClientSetup.ServerName,
                        "--", syntheticWorker, "--mcp-stdio", "--data", spacedDirectory }),
                "Claude CLI option boundary keeps worker flags and a spaced data directory in the stdio command");
            Check(setupResult.Contains("Claude Code", StringComparison.Ordinal) && setupResult.Contains("/mcp", StringComparison.Ordinal) &&
                setupResult.Contains("new Claude conversation", StringComparison.Ordinal) && !setupResult.Contains("restart", StringComparison.OrdinalIgnoreCase),
                "Claude success identifies the Code integration and loads it in a new conversation without an editor restart");

            const string privateDiagnostic = "SYNTHETIC_SETUP_PRIVATE_VALUE";
            setupCommand.GetResult = new(6, privateDiagnostic, "Configuration parse failed"); setupCommand.Calls.Clear();
            setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None).GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 1 && setupResult.Contains("verify", StringComparison.Ordinal) &&
                setupResult.Contains("exit 6", StringComparison.Ordinal) && !setupResult.Contains(privateDiagnostic, StringComparison.Ordinal),
                "unrelated failed get never adds or discloses configuration diagnostics");
            setupCommand.GetResult = new(7, "", "Managed settings prohibit this MCP server " + privateDiagnostic); setupCommand.Calls.Clear();
            setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None).GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 1 && setupResult.Contains("managed policy", StringComparison.Ordinal) &&
                setupResult.Contains("exit 7", StringComparison.Ordinal) && !setupResult.Contains(privateDiagnostic, StringComparison.Ordinal),
                "managed get refusal reports a fixed reason without an add attempt");
            setupCommand.GetResult = null;
            foreach (var failure in new[] {
                (Error: "Cannot add MCP server: not allowed by enterprise policy", Expected: "managed policy"),
                (Error: "MCP server already exists in user config", Expected: "already exists"),
                (Error: "EPERM: configuration write failed", Expected: "configuration locks"),
                (Error: "error: unknown option --synthetic", Expected: "rejected the MCP setup arguments"),
                (Error: "Git Bash was not found and PowerShell is disabled", Expected: "required shell"),
                (Error: "unclassified vendor failure", Expected: "did not confirm MCP setup") })
            {
                setupCommand.AddResult = new(9, privateDiagnostic, failure.Error); setupCommand.Calls.Clear();
                setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None).GetAwaiter().GetResult();
                Check(setupCommand.Calls.Count == 2 && setupCommand.Calls[1].Arguments[1] == "add" &&
                    setupResult.Contains(failure.Expected, StringComparison.Ordinal) && setupResult.Contains("exit 9", StringComparison.Ordinal) &&
                    !setupResult.Contains(privateDiagnostic, StringComparison.Ordinal) && !setupResult.Contains("was added", StringComparison.Ordinal),
                    "nonzero add reports fixed " + failure.Expected + " reason without private diagnostics or false success");
            }
            setupCommand.AddResult = null;

            const string httpEndpoint = "http://127.0.0.1:7432/mcp";
            var noAuthConnection = new ControlResponse(true, "ok", Connection: new(httpEndpoint, null));
            setupCommand.Calls.Clear();
            setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None,
                AgentClientSetup.EndpointFromConnection(noAuthConnection)).GetAwaiter().GetResult();
            Check(setupResult.Contains("added", StringComparison.Ordinal) && setupCommand.Calls.Count == 2 &&
                setupCommand.Calls[1].Arguments.SequenceEqual(new[] { "mcp", "add", "--transport", "http", "--scope", "user",
                    AgentClientSetup.ServerName, httpEndpoint }),
                "Claude token-free control connection registers the exact local HTTP URL under the same server name");
            setupCommand.AddResult = new(1, privateDiagnostic, "Cannot add MCP server: not allowed by enterprise policy");
            setupCommand.Calls.Clear();
            setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None, httpEndpoint).GetAwaiter().GetResult();
            // Since N-bus-04 an allowlist miss gets one localhost add of the same endpoint, and nothing else.
            Check(setupCommand.Calls.Count == 3 && setupCommand.Calls[2].Arguments.SequenceEqual(new[] { "mcp", "add", "--transport", "http", "--scope", "user",
                    AgentClientSetup.ServerName, "http://localhost:7432/mcp" }) &&
                setupResult.Contains("allowlist http://localhost:7432/mcp or " + httpEndpoint, StringComparison.Ordinal) &&
                !setupResult.Contains(privateDiagnostic, StringComparison.Ordinal),
                "HTTP allowlist refusal names only the validated local URLs and retries no other URL or transport");
            setupCommand.AddResult = null;
            setupCommand.GetResult = new(0, "existing server", ""); setupCommand.Calls.Clear();
            setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None, httpEndpoint).GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 1, "existing Claude HTTP entry is never overwritten");
            setupCommand.GetResult = null;
            foreach (string endpoint in new[] { "http://localhost:7432/mcp", "http://127.1:7432/mcp", "https://127.0.0.1:7432/mcp",
                "http://user@127.0.0.1:7432/mcp", "http://127.0.0.1:7432/mcp?token=synthetic", "http://127.0.0.1:7432/mcp#fragment",
                "http://127.0.0.1/mcp", "http://127.0.0.1:07432/mcp", "http://127.0.0.1:0/mcp", "http://127.0.0.1:65536/mcp",
                "http://127.0.0.1:7432/mcp/", "http://127.0.0.1.evil.invalid:7432/mcp", "", " " })
            {
                setupCommand.Calls.Clear();
                setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None, endpoint).GetAwaiter().GetResult();
                Check(setupCommand.Calls.Count == 0 && setupResult.Contains("invalid local MCP endpoint", StringComparison.Ordinal),
                    "noncanonical HTTP endpoint refuses before any client command");
            }
            setupCommand.Calls.Clear();
            setup.ConfigureAsync("codex", syntheticWorker, directory, CancellationToken.None, httpEndpoint).GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 0, "Claude HTTP preference cannot change Codex transport");
            foreach (ControlResponse? response in new ControlResponse?[] { new(true, "authenticated", Connection: new(httpEndpoint, privateDiagnostic)),
                new(true, "authenticated", Connection: new(httpEndpoint, "")) })
            {
                setupCommand.Calls.Clear();
                string? endpoint = AgentClientSetup.EndpointFromConnection(response);
                setup.ConfigureAsync("claude", syntheticWorker, directory, CancellationToken.None, endpoint).GetAwaiter().GetResult();
                Check(AgentClientSetup.HasVerifiedConnection(response) && endpoint is null && setupCommand.Calls.Count == 2 && setupCommand.Calls[1].Arguments.Contains("stdio") &&
                    !setupCommand.Calls[1].Arguments.Contains(httpEndpoint) && !setupCommand.Calls[1].Arguments.Contains(privateDiagnostic),
                    "verified token-required control connection preserves stdio without persisting a bearer token");
            }
            module.ClientSetup = setup;
            control.Connection = new("http://localhost:7432/mcp", null); setupCommand.Calls.Clear();
            setupResult = module.ConfigureClientAsync("claude").GetAwaiter().GetResult();
            Check(control.Requests.Last().Operation == "connection" && setupCommand.Calls.Count == 0 &&
                setupResult.Contains("Refresh status", StringComparison.Ordinal),
                "module client action gets the live user-only control connection and rejects its invalid URL before CLI work");
            control.Success = false; control.Connection = new(httpEndpoint, null); setupCommand.Calls.Clear();
            setupResult = module.ConfigureClientAsync("claude").GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 0 && setupResult.Contains("Refresh status", StringComparison.Ordinal),
                "refused connection-info response performs no client command or transport fallback");
            control.Success = true; control.Connection = null; setupCommand.Calls.Clear();
            setupResult = module.ConfigureClientAsync("claude").GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 0 && setupResult.Contains("Refresh status", StringComparison.Ordinal),
                "successful response without connection-info performs no client command");
            control.FailTransport = true; setupCommand.Calls.Clear();
            setupResult = module.ConfigureClientAsync("claude").GetAwaiter().GetResult();
            Check(setupCommand.Calls.Count == 0 && setupResult.Contains("Refresh status", StringComparison.Ordinal) &&
                !setupResult.Contains(privateDiagnostic, StringComparison.Ordinal),
                "failed connection-info transport returns fixed refresh guidance without client work or private errors");
            control.FailTransport = false; control.Connection = new(httpEndpoint, privateDiagnostic); setupCommand.Calls.Clear();
            setupResult = module.ConfigureClientAsync("claude").GetAwaiter().GetResult();
            Check(setupResult.Contains("added", StringComparison.Ordinal) && setupCommand.Calls.Count == 2 &&
                setupCommand.Calls[1].Arguments.Contains("stdio") && setupCommand.Calls[1].Arguments.Contains("--mcp-stdio") &&
                !setupCommand.Calls[1].Arguments.Contains(httpEndpoint) && !setupCommand.Calls[1].Arguments.Contains(privateDiagnostic),
                "module client action uses stdio only after a verified authenticated connection without persisting its token");
            control.Connection = null;
            // The bare-name launch and its one ~/.local/bin fallback are gone (N-broker-a-01): the client is located before
            // anything starts, the user's own Claude install is one rung of that order, and the located binary starts once.
            // RunLocatorChecks holds the replacements ("cli locator: ..."), Claude-only native rung included.
            using (var cancelledSetup = new CancellationTokenSource())
            {
                cancelledSetup.Cancel(); setupCommand.Calls.Clear();
                setupResult = setup.ConfigureAsync("claude", syntheticWorker, directory, cancelledSetup.Token, httpEndpoint).GetAwaiter().GetResult();
                Check(setupCommand.Calls.Count == 0 && setupResult.Contains("cancelled", StringComparison.Ordinal),
                    "a cancelled client action cannot start CLI configuration work");
            }

            TrayItem[] tray = host.TrayItems.Single().BuildChildren().ToArray();
            tray.Single(t => t.Label == "Pair a broker").BuildChildren().Single().Click();
            Check(control.Requests.Last().Operation == "pair-request" && control.Requests.Last().Peer == peer,
                "discovered peer exact pin passed");
            var sessionMenu = tray.Single(t => t.Label == "Sessions").BuildChildren().Single().BuildChildren().ToArray();
            sessionMenu[0].Click(); sessionMenu[1].Click();
            Check(control.Requests.Any(r => r.Operation == "session-private" && r.SessionId == session.SessionId && r.Value) &&
                control.Requests.Any(r => r.Operation == "session-muted" && r.SessionId == session.SessionId && r.Value),
                "private and mute exact session controls");
            tray.Single(t => t.Label == "Paired brokers").BuildChildren().Single().BuildChildren().Single().Click();
            Check(control.Requests.Last().Operation == "peer-revoke" && control.Requests.Last().Id == peer.BrokerId,
                "revoke exact peer");
            var failedOffer = offer with { RequestId = Guid.NewGuid(), Peer = peer with { BrokerId = Guid.NewGuid() } };
            int requestsBeforeFault = control.Requests.Count;
            host.ThrowNextAsk = true;
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [failedOffer] }));
            Check(control.Requests.Count == requestsBeforeFault && module.status.Contains("confirmation unavailable", StringComparison.Ordinal),
                "throwing question service reports failure and never approves or rejects an offer implicitly");
            var manualConfirmation = host.TrayItems.Single().BuildChildren().Single(t =>
                t.Label.Contains(comparisonCode, StringComparison.Ordinal)).BuildChildren().First();
            manualConfirmation.Click();
            Check(control.Requests.Last().Operation == "pair-approve" && control.Requests.Last().Id == failedOffer.RequestId,
                "failed confirmation keeps the full comparison code and explicit tray retry operable");
            var pendingOffer = offer with { RequestId = Guid.NewGuid() };
            module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [pendingOffer] }));
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
            Check(host.Asks.Last().Withdrawn && supervisor.Stops > 0, "shutdown withdraws questions and cleans owned worker off thread");
            int requests = control.Requests.Count;
            dispatcher.Drain();
            Check(control.Requests.Count == requests, "shutdown never approves late answers");

            var missingHost = new TestHost();
            var missingSupervisor = new TestSupervisor();
            var missing = new AgentBrokerModule(new TestControl(), missingSupervisor);
            missingHost.SettingsFor(Id).Set("enabled", "true");
            missing.Init(missingHost);
            Pump(() => missing.pending.IsCompleted, dispatcher);
            Check(missingSupervisor.Launches.Count == 0 && missingHost.OptionsPanes[0].Load()["status"].Contains("storage"),
                "missing storage never falls back to a different root");
            missing.Shutdown();
        }
        catch (Exception ex) { failures.Add("self-test exception " + ex.GetType().Name + ": " + ex.Message); }
        finally { module?.Shutdown(); SynchronizationContext.SetSynchronizationContext(previous); }
        // One FAIL: line per failed check, under the summary line: the host re-emits each line with the module's
        // prefix, and the self-test mutation harness grades a case by the failed check's own label on such a line.
        detail = checks + " checks" + (failures.Count == 0 ? " passed"
            : "; " + failures.Count + " failed" + string.Concat(failures.Select(failure => "\nFAIL: " + failure)));
        return failures.Count == 0;
    }

    private static void Pump(Func<bool> complete, TestDispatcher dispatcher)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(5))
        { dispatcher.Drain(); Thread.Sleep(5); }
        dispatcher.Drain();
        if (!complete()) throw new TimeoutException("Synthetic lifecycle did not complete.");
    }
    private sealed class TestDispatcher : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        internal int Executed;
        public override void Post(SendOrPostCallback callback, object? state) => queue.Enqueue((callback, state));
        internal void Drain() { while (queue.TryDequeue(out var work)) { work.Callback(work.State); Executed++; } }
    }
    private sealed record TestStorage(string DataDirectory) : IModuleStorage;
    private sealed class TestHost : RecordingHost, ITrayQuestions
    {
        internal sealed record Asked(TrayQuestion Question, TaskCompletionSource<TrayAnswer> Completion, CancellationToken Token)
        { internal bool Withdrawn; }
        internal List<Asked> Asks { get; } = [];
        internal bool ThrowNextAsk;
        internal bool IgnoreQuestionCancellation;
        public Task<TrayAnswer> AskAsync(string moduleId, TrayQuestion question, CancellationToken token)
        {
            if (ThrowNextAsk) { ThrowNextAsk = false; throw new InvalidOperationException("Synthetic question service failure."); }
            var completion = new TaskCompletionSource<TrayAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asked = new Asked(question, completion, token); Asks.Add(asked);
            bool ignoreCancellation = IgnoreQuestionCancellation;
            token.Register(() => { asked.Withdrawn = true; if (!ignoreCancellation) completion.TrySetResult(new TrayAnswer { Outcome = TrayQuestionOutcome.Withdrawn }); });
            return completion.Task;
        }
    }
    private sealed class TestControl : IBrokerControlClient
    {
        internal List<ControlRequest> Requests { get; } = [];
        internal bool Success = true;
        internal bool FailTransport;
        internal LocalConnection? Connection;
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request);
            if (FailTransport) throw new IOException("SYNTHETIC_SETUP_PRIVATE_VALUE");
            return Task.FromResult(new ControlResponse(Success, Success ? "ok" : "refused", Connection: Connection));
        }
    }
    private sealed class TestSupervisor : IWorkerSupervisor
    {
        internal ConcurrentQueue<WorkerLaunch> Launches { get; } = new();
        internal BrokerSnapshot? View;
        internal int Stops;
        public Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Launches.Enqueue(launch); return Task.FromResult(new WorkerStatus(true, false, "standby", View)); }
        public Task StopOwnedAsync(string directory, CancellationToken token)
        { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
    }
    private sealed class TestClientCommand : IAgentClientCommand
    {
        internal string? Existing;
        internal ClientCommandResult? GetResult, AddResult;
        internal List<(string Executable, string[] Arguments)> Calls = [];
        public Task<ClientCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add((executable, arguments.ToArray()));
            if (arguments[1] == "get") return Task.FromResult(GetResult ?? (Existing is null
                ? new ClientCommandResult(1, "", "No MCP server named " + AgentClientSetup.ServerName + " found.")
                : new ClientCommandResult(0, Existing, "")));
            return Task.FromResult(AddResult ?? new ClientCommandResult(0, "", ""));
        }
    }
}
