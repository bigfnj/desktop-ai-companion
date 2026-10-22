using AgentBroker.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunPairingChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        CheckPairingControlDeadlinesAsync(check).GetAwaiter().GetResult();
        CheckObsoletePairingQuestions(check, dispatcher);
        const string privateDiagnostic = "SYNTHETIC_PRIVATE_PAIRING_DIAGNOSTIC";
        foreach (var item in new (Exception Error, string Code)[] {
            (new TaskCanceledException(privateDiagnostic), "pairing_network_timeout"),
            (new HttpRequestException(HttpRequestError.SecureConnectionError, privateDiagnostic), "pairing_tls_failed"),
            (new HttpRequestException(privateDiagnostic, null, HttpStatusCode.Forbidden), "pairing_peer_refused"),
            (new HttpRequestException(privateDiagnostic, null, HttpStatusCode.BadRequest), "pairing_offer_invalid"),
            (new HttpRequestException(privateDiagnostic, new SocketException((int)SocketError.ConnectionRefused)), "pairing_connection_refused"),
            (new HttpRequestException(privateDiagnostic), "pairing_connection_failed"),
            (new UnauthorizedAccessException(privateDiagnostic), "pairing_identity_refused") })
            check(ControlProtocol.FailureCode("pair-request", item.Error) == item.Code &&
                !PairingFailureText(item.Code).Contains(privateDiagnostic, StringComparison.Ordinal),
                "pairing: worker failure classification has fixed actionable guidance without private diagnostics");

        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-pairing-ui-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var peer = new PeerView(Guid.NewGuid(), "Synthetic peer", "https://192.168.1.2:7433/", new string('B', 64));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.168.1.1:7433/",
            [], [], [], [], new Dictionary<string, string>(), new string('A', 64));
        var control = new StatusControl { Response = new(false, "pairing_network_timeout") };
        var module = new AgentBrokerModule(control, new StatusSupervisor(view));
        try
        {
            module.Init(host);
            var pane = host.OptionsPanes.Single();
            Pump(() => pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal), dispatcher);
            int notifications = 0;
            pane.InfoChanged += () => notifications++;
            var invitation = new Dictionary<string, string> { ["manualPeer"] = JsonSerializer.Serialize(peer, ControlProtocol.Json) };
            string result = module.PairInvitationAsync(invitation).GetAwaiter().GetResult();
            string feedback = pane.Load()["pairingStatus"];
            check(result.Contains("could not", StringComparison.Ordinal) && result.Contains("7433", StringComparison.Ordinal) &&
                notifications > 0 && pane.Load()["status"].Contains(feedback, StringComparison.Ordinal),
                "pairing: manual timeout is visible in its action, persistent pairing Info and pinned status");
            int requests = control.Requests.Count;
            module.ApplyStatus(new(true, true, "running", view));
            module.ApplyStatus(new(true, true, "running", view));
            check(pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal) &&
                pane.Load()["status"].Contains(feedback, StringComparison.Ordinal) && pane.Load()["pairingStatus"] == feedback &&
                control.Requests.Count == requests && !feedback.Contains("sharing was enabled", StringComparison.Ordinal),
                "pairing: healthy status polls preserve the failure without retrying or making a trust claim");

            module.ApplyStatus(new(true, true, "running", view with { Discovered = [peer] }));
            host.Asks.Last().Completion.SetResult(new() { Outcome = DesktopAICompanion.Modules.TrayQuestionOutcome.Chosen, ChoiceId = "allow" });
            Pump(() => !module.discoveryPromptOpen, dispatcher);
            check(control.Requests.Last() == "pair-request" && pane.Load()["pairingStatus"].Contains("7433", StringComparison.Ordinal) &&
                host.TrayItems.Single().BuildChildren().Any(item => item.Label.StartsWith("Pairing could not", StringComparison.Ordinal)),
                "pairing: an automatic discovery Allow failure remains visible in Info and the tray");

            control.Response = new(false, "pairing_tls_failed");
            result = module.PairInvitationAsync(invitation).GetAwaiter().GetResult();
            check(result.Contains("TLS identity", StringComparison.Ordinal) && result.Contains("Never bypass the certificate check.", StringComparison.Ordinal),
                "pairing: certificate failure advises a current invitation and preserves pin verification");
            control.Response = new(false, "pairing_in_progress");
            result = module.PairInvitationAsync(invitation).GetAwaiter().GetResult();
            check(!module.pairingFailed && result.Contains("current incoming offer", StringComparison.Ordinal) &&
                !result.Contains("could not", StringComparison.Ordinal) && module.snapshot!.Paired.Length == 0,
                "pairing: a simultaneous manual request gives informative current-code guidance without claiming failure or trust");
            module.ResetDiscoveryPrompts();
            module.ApplyStatus(new(true, true, "running", view with { Discovered = [peer] }));
            host.Asks.Last().Completion.SetResult(new() { Outcome = DesktopAICompanion.Modules.TrayQuestionOutcome.Chosen, ChoiceId = "allow" });
            Pump(() => !module.discoveryPromptOpen, dispatcher);
            check(!module.pairingFailed && pane.Load()["pairingStatus"].Contains("Do not request another offer.", StringComparison.Ordinal),
                "pairing: simultaneous discovery Allow uses the same informative nonretry code guidance");
            control.Response = new(false, privateDiagnostic);
            result = module.PairInvitationAsync(invitation).GetAwaiter().GetResult();
            check(!result.Contains(privateDiagnostic, StringComparison.Ordinal) && !pane.Load()["pairingStatus"].Contains(privateDiagnostic, StringComparison.Ordinal),
                "pairing: unknown worker codes never appear as raw diagnostics");
            control.Response = new(true, "ok", view);
            result = module.PairInvitationAsync(invitation).GetAwaiter().GetResult();
            check(result.Contains("Pairing requested", StringComparison.Ordinal) &&
                !pane.Load()["status"].Contains("could not", StringComparison.Ordinal) &&
                pane.Load()["pairingStatus"].Contains("both workstations", StringComparison.Ordinal) && module.snapshot!.Paired.Length == 0,
                "pairing: an explicit successful retry clears the old failure and still requires both final approvals");

            control.Failure = new OperationCanceledException(privateDiagnostic);
            module.SendAsync(new("pair-approve", Guid.NewGuid())).GetAwaiter().GetResult();
            check(pane.Load()["pairingStatus"].Contains("outcome is unconfirmed", StringComparison.Ordinal) &&
                !pane.Load()["pairingStatus"].Contains(privateDiagnostic, StringComparison.Ordinal),
                "pairing: a lost confirmation outcome asks for state inspection rather than an automatic retry");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }

    private static void CheckObsoletePairingQuestions(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-stale-pairing-ui-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var peer = new PeerView(Guid.NewGuid(), "Synthetic peer", "https://192.168.1.2:7433/", new string('B', 64));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.168.1.1:7433/",
            [], [], [], [], new Dictionary<string, string>(), new string('A', 64));
        var control = new TestControl();
        var module = new AgentBrokerModule(control, new StatusSupervisor(view));
        try
        {
            module.Init(host);
            Pump(() => module.snapshot is not null, dispatcher);
            var oldOffer = new PairingView(Guid.NewGuid(), peer, "11111111 11111111 11111111 11111111", DateTimeOffset.UtcNow.AddMinutes(2), true);
            module.snapshot = view with { PairingOffers = [oldOffer] };
            var oldSource = CancellationTokenSource.CreateLinkedTokenSource(module.run!.Token);
            module.pairingQuestions.Add(oldOffer.RequestId, oldSource);
            module.askedOffers.Add(oldOffer.RequestId);
            host.IgnoreQuestionCancellation = true;
            Task oldQuestionTask = module.AskPairingAsync(oldOffer, module.generation, oldSource);
            var oldQuestion = host.Asks.Last();
            host.IgnoreQuestionCancellation = false;
            var freshOffer = oldOffer with { RequestId = Guid.NewGuid(), Outbound = false,
                VerificationCode = "22222222 22222222 22222222 22222222" };
            module.ApplyStatus(new(true, true, "running", view with { PairingOffers = [freshOffer] }));
            var freshQuestion = host.Asks.Last();
            check(oldQuestion.Withdrawn && oldQuestion.Token.IsCancellationRequested && !freshQuestion.Token.IsCancellationRequested &&
                module.pairingQuestions.Count == 1 && freshQuestion.Question.Text.Contains(freshOffer.VerificationCode, StringComparison.Ordinal),
                "pairing: replacing a colliding offer withdraws its obsolete question and shows only the fresh complete code");
            oldQuestion.Completion.SetResult(new() { Outcome = DesktopAICompanion.Modules.TrayQuestionOutcome.Chosen, ChoiceId = "allow" });
            Pump(() => oldQuestionTask.IsCompleted, dispatcher);
            check(control.Requests.Count == 0 && module.pairingQuestions.Count == 1,
                "pairing: a late Allow response for the discarded code cannot approve either old or replacement offer");
            freshQuestion.Completion.SetResult(new() { Outcome = DesktopAICompanion.Modules.TrayQuestionOutcome.Chosen, ChoiceId = "allow" });
            Pump(() => control.Requests.Count == 1 && module.pairingQuestions.Count == 0, dispatcher);
            check(control.Requests.Single().Operation == "pair-approve" && control.Requests.Single().Id == freshOffer.RequestId,
                "pairing: only explicit approval of the fresh question sends its exact replacement request ID");
            module.ApplyStatus(new(true, true, "running", view with { PairingOffers = [freshOffer with { RequestId = Guid.NewGuid() }] }));
            var restartedQuestion = host.Asks.Last();
            module.Restart();
            Pump(() => restartedQuestion.Withdrawn && module.pairingQuestions.Count == 0 && module.snapshot is not null, dispatcher);
            check(restartedQuestion.Token.IsCancellationRequested && control.Requests.Count == 1,
                "pairing: restart withdraws outstanding offer questions without sending approval or rejection for stale state");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }

    private static async Task CheckPairingControlDeadlinesAsync(Action<bool, string> check)
    {
        async Task<ControlResponse> Delayed(string operation, TimeSpan delay, CancellationToken caller = default)
        {
            string directory = Path.Combine(Path.GetTempPath(), "agentbroker-pairing-pipe-" + Guid.NewGuid().ToString("N"));
            await using var pipe = ControlPipeSecurity.CreateServer(ControlProtocol.PipeName(directory), 1);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serving = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                var one = new byte[1];
                while (await pipe.ReadAsync(one, lifetime.Token).ConfigureAwait(false) == 1 && one[0] != (byte)'\n') { }
                await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                byte[] response = JsonSerializer.SerializeToUtf8Bytes(new ControlResponse(false, "pairing_connection_refused"), ControlProtocol.Json);
                await pipe.WriteAsync(response, lifetime.Token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { (byte)'\n' }, lifetime.Token).ConfigureAwait(false);
            });
            try
            {
                return await new BrokerControlClient().SendAsync(directory, new(operation), caller, Environment.ProcessId).ConfigureAwait(false);
            }
            finally
            {
                lifetime.Cancel();
                try { await serving.ConfigureAwait(false); } catch (Exception error) when (error is OperationCanceledException or IOException) { }
            }
        }
        var elapsed = Stopwatch.StartNew();
        var response = await Delayed("pair-request", TimeSpan.FromMilliseconds(3500)).ConfigureAwait(false);
        check(!response.Success && response.Code == "pairing_connection_refused" && elapsed.Elapsed >= TimeSpan.FromSeconds(3),
            "pairing: the real secured pipe client waits beyond the status deadline for a classified pairing result");
        elapsed.Restart();
        bool timedOut = false;
        try { await Delayed("status", TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (OperationCanceledException) { timedOut = true; }
        check(timedOut && elapsed.Elapsed < TimeSpan.FromSeconds(5),
            "pairing: ordinary status retains its short deadline against an unresponsive secured pipe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        elapsed.Restart();
        bool cancelled = false;
        try { await Delayed("pair-approve", TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(2),
            "pairing: the longer confirmation deadline still obeys caller cancellation promptly");
        check(ControlProtocol.ServerTimeout("pair-request") > TimeSpan.FromSeconds(20) &&
            ControlProtocol.ClientTimeout("pair-request") > ControlProtocol.ServerTimeout("pair-request"),
            "pairing: two HTTPS budgets fit inside the worker deadline and the outer client deadline");
    }
}
