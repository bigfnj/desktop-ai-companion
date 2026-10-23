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
        CheckConfirmedPairingStatus(check, dispatcher);
        CheckPairingObservationsAndList(check, dispatcher);
        CheckIssuedObservationOrdering(check, dispatcher);
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

    private static void CheckIssuedObservationOrdering(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-issued-observations-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var peer = new PeerView(Guid.NewGuid(), "Old paired peer", "https://192.0.2.20:7433/", new string('B', 64));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
            [], [], [], [], new Dictionary<string, string>(), new string('A', 64));
        AgentBrokerModule? module = null;
        var supervisor = new ObservationSupervisor(() => Volatile.Read(ref module!.nextObservation));
        var control = new ObservationControl(() => Volatile.Read(ref module!.nextObservation), view);
        module = new AgentBrokerModule(control, supervisor);
        try
        {
            module.Init(host); Pump(() => supervisor.Sequence > 0, dispatcher);
            module.SendAsync(new("status")).GetAwaiter().GetResult();
            check(supervisor.Sequence > 0 && control.Sequence > supervisor.Sequence && module.snapshot!.Paired.Length == 0,
                "pairing observation: real supervisor refresh and control paths allocate distinct ordered requests before either injected call");
            int executed = dispatcher.Executed;
            supervisor.Pending.SetResult(new(true, false, "standby", view with { Paired = [peer] }));
            Pump(() => dispatcher.Executed > executed, dispatcher);
            check(module.snapshot!.Paired.Length == 0 && module.lastAppliedObservation == control.Sequence,
                "pairing observation: an actual delayed supervisor result cannot replace the newer control snapshot on the dispatcher");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }

    private sealed class ObservationSupervisor(Func<long> sequence) : IWorkerSupervisor
    {
        internal long Sequence;
        internal TaskCompletionSource<WorkerStatus> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token)
        {
            Interlocked.Exchange(ref Sequence, sequence());
            return Pending.Task.WaitAsync(token);
        }
        public Task StopOwnedAsync(string directory, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class ObservationControl(Func<long> sequence, BrokerSnapshot view) : IBrokerControlClient
    {
        internal long Sequence;
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
        {
            Sequence = sequence();
            return Task.FromResult(new ControlResponse(true, "ok", view));
        }
    }

    private static void CheckPairingObservationsAndList(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-paired-list-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var peer = new PeerView(Guid.NewGuid(), "Same name", "https://192.0.2.20:7433/", new string('B', 64));
        var other = peer with { BrokerId = Guid.NewGuid(), Endpoint = "https://192.0.2.21:7433/" };
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
            [], [], [], [], new Dictionary<string, string>(), new string('A', 64));
        var supervisor = new TestSupervisor { View = view };
        var control = new StatusControl();
        var module = new AgentBrokerModule(control, supervisor);
        void Apply(BrokerSnapshot updated)
        { supervisor.View = updated; module.ApplyStatus(new(true, false, "standby", updated)); }
        try
        {
            module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
            var pane = host.OptionsPanes.Single();
            check(pane.Schema.Single(field => field.Id == "pairedBrokers") is
                { Kind: DesktopAICompanion.Modules.SettingKind.Info, Group: "AgentBroker", FullWidth: true } &&
                pane.Load()["pairedBrokers"].StartsWith("No paired brokers.", StringComparison.Ordinal),
                "pairing list: explicit full-width paired broker list sits beside primary Status with a clear empty state");
            var paired = view with { Paired = [peer, other] };
            Apply(paired);
            string shown = pane.Load()["pairedBrokers"];
            check(shown.Contains(peer.Endpoint, StringComparison.Ordinal) && shown.Contains(other.Endpoint, StringComparison.Ordinal) &&
                shown.Contains("Same name", StringComparison.Ordinal) && shown.Contains("Saved trust", StringComparison.Ordinal) &&
                shown.Contains("reachability is unknown", StringComparison.Ordinal),
                "pairing list: identical broker names retain both endpoints and distinguish saved trust from reachability");
            int notifications = 0; pane.InfoChanged += () => notifications++;
            var moved = other with { Endpoint = "https://192.0.2.22:7433/" };
            Apply(view with { Paired = [peer, moved] });
            shown = pane.Load()["pairedBrokers"];
            check(notifications == 1 && shown.Contains(moved.Endpoint, StringComparison.Ordinal) &&
                !shown.Contains(other.Endpoint, StringComparison.Ordinal),
                "pairing list: endpoint-only changes notify the live Info row even when counts and names are unchanged");
            int before = notifications;
            Apply(view with { Paired = [moved, peer] });
            check(pane.Load()["pairedBrokers"] == shown && notifications == before,
                "pairing list: stable ordering avoids list churn for reordered equivalent snapshots");
            var alpha = peer with { Name = "Alpha" };
            var zulu = moved with { Name = "Zulu" };
            Apply(view with { Paired = [zulu, alpha] });
            shown = pane.Load()["pairedBrokers"]; before = notifications;
            Apply(view with { Paired = [alpha, zulu] });
            check(pane.Load()["pairedBrokers"] == shown && notifications == before &&
                shown.IndexOf("Alpha", StringComparison.Ordinal) < shown.IndexOf("Zulu", StringComparison.Ordinal),
                "pairing list: distinct broker names and the trust summary use the same stable order across equivalent snapshots");
            Apply(view with { Paired = [peer with { Name = "Line\n&Break" }] });
            check(pane.Load()["pairedBrokers"].Contains("LineBreak", StringComparison.Ordinal) &&
                !pane.Load()["pairedBrokers"].Contains("Line\n&Break", StringComparison.Ordinal),
                "pairing list: broker names are sanitized before display");
            Apply(view with { Paired = Enumerable.Range(0, 35).Select(index => peer with {
                BrokerId = new Guid(index + 1, 0, 0, new byte[8]), Name = "Peer" + index.ToString("D2"),
                Endpoint = "https://192.0.2." + (index + 1) + ":7433/" }).ToArray() });
            shown = pane.Load()["pairedBrokers"];
            check(shown.Split('\n').Length == 34 && shown.Contains("Peer31", StringComparison.Ordinal) &&
                !shown.Contains("Peer32", StringComparison.Ordinal) && shown.Contains("Showing 32 of 35", StringComparison.Ordinal),
                "pairing list: lists thirty-two actual peers and states the bounded display when more exist");
            module.ApplyStatus(new(false, false, "worker_unresponsive"));
            check(pane.Load()["pairedBrokers"].Contains("unavailable", StringComparison.Ordinal) &&
                !pane.Load()["pairedBrokers"].Contains("Peer31", StringComparison.Ordinal),
                "pairing list: unavailable worker state does not retain a misleading live peer list");

            Apply(paired);
            long oldPoll = module.NextObservation();
            control.Response = new(true, "ok", view);
            module.SendAsync(new("peer-revoke", peer.BrokerId)).GetAwaiter().GetResult();
            bool staleRevokePoll = module.ApplyStatus(new(true, false, "standby", paired), oldPoll);
            check(!staleRevokePoll && module.snapshot!.Paired.Length == 0 &&
                pane.Load()["pairedBrokers"].StartsWith("No paired brokers.", StringComparison.Ordinal),
                "pairing observation: an older supervisor poll cannot resurrect trust after a newer revoke command snapshot");

            Apply(view);
            long oldEmptyPoll = module.NextObservation();
            control.Response = new(true, "ok", paired);
            module.SendAsync(new("pair-request", Peer: peer)).GetAwaiter().GetResult();
            bool staleEmptyPoll = module.ApplyStatus(new(true, false, "standby", view), oldEmptyPoll);
            check(!staleEmptyPoll && module.snapshot!.Paired.Length == 2 &&
                pane.Load()["pairedBrokers"].Contains(peer.Endpoint, StringComparison.Ordinal),
                "pairing observation: an older supervisor poll cannot erase trust confirmed by a newer command snapshot");

            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ControlResponse?> lateStatus = module.SendAsync(new("status"));
            Apply(paired);
            control.Pending.SetException(new IOException("SYNTHETIC_PRIVATE_OLD_STATUS_ERROR"));
            Pump(() => lateStatus.IsCompleted, dispatcher); lateStatus.GetAwaiter().GetResult(); control.Pending = null;
            check(module.snapshot?.Paired.Length == 2 && pane.Load()["pairedBrokers"].Contains(peer.Endpoint, StringComparison.Ordinal),
                "pairing observation: an older failed status request cannot clear a newer successful peer snapshot");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }

    private static void CheckConfirmedPairingStatus(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-confirmed-pairing-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var peer = new PeerView(Guid.NewGuid(), "Approved peer", "https://192.0.2.20:7433/", new string('B', 64));
        var other = peer with { BrokerId = Guid.NewGuid(), Name = "Unrelated peer", CertificateSha256 = new string('C', 64) };
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
            [], [peer], [], [], new Dictionary<string, string>(), new string('A', 64));
        var control = new StatusControl { Response = new(false, "pairing_network_timeout") };
        var supervisor = new TestSupervisor { View = view };
        var module = new AgentBrokerModule(control, supervisor);
        void Apply(BrokerSnapshot updated)
        {
            supervisor.View = updated;
            module.ApplyStatus(new(true, false, "standby", updated));
        }
        try
        {
            module.Init(host);
            Pump(() => module.snapshot is not null, dispatcher);
            var pane = host.OptionsPanes.Single();
            string shown = pane.Load()["pairingStatus"];
            check(shown.Contains("Paired with Approved peer", StringComparison.Ordinal) &&
                shown.Contains("current reachability is not verified", StringComparison.Ordinal) &&
                !shown.Contains("No pairing attempt", StringComparison.Ordinal) &&
                host.TrayItems.Single().BuildChildren().Any(item => item.Label.StartsWith("Paired with Approved peer", StringComparison.Ordinal)) &&
                host.Asks.Count == 0 && control.Requests.Count == 0,
                "pairing: initial saved trust has durable pane/tray confirmation without a popup or online claim");
            int notifications = 0; pane.InfoChanged += () => notifications++;
            Apply(view); Apply(view);
            check(notifications == 0 && host.Asks.Count == 0 && control.Requests.Count == 0,
                "pairing: identical trusted snapshots do not repeat notifications, questions or commands");
            module.Restart();
            Pump(() => module.snapshot is not null && module.pending.Status != TaskStatus.Created, dispatcher);
            check(pane.Load()["pairingStatus"].Contains("Paired with Approved peer", StringComparison.Ordinal) &&
                host.Asks.Count == 0 && control.Requests.Count == 0,
                "pairing: restart restores saved trust summary without manufacturing a new approval or popup");

            Apply(view with { Paired = [] });
            control.Response = new(false, "pairing_tls_failed");
            module.SendAsync(new("pair-request", Peer: peer)).GetAwaiter().GetResult();
            check(module.pairingFailed && pane.Load()["pairingStatus"].Contains("TLS identity", StringComparison.Ordinal),
                "pairing: a failed untrusted peer attempt retains its scoped failure");
            Apply(view);
            check(!module.pairingFailed && pane.Load()["pairingStatus"].Contains("Paired with Approved peer", StringComparison.Ordinal) &&
                !pane.Load()["pairingStatus"].Contains("TLS identity", StringComparison.Ordinal),
                "pairing: matching confirmed trust clears its earlier failure even when completion arrives only by polling");

            control.Response = new(false, "pairing_connection_refused");
            module.SendAsync(new("pair-request", Peer: other)).GetAwaiter().GetResult();
            string unrelatedFailure = module.pairingStatus;
            Apply(view); Apply(view);
            check(module.pairingFailed && module.pairingStatus == unrelatedFailure &&
                pane.Load()["pairingStatus"].Contains("Paired with Approved peer", StringComparison.Ordinal) &&
                pane.Load()["pairingStatus"].Contains("could not reach", StringComparison.Ordinal),
                "pairing: confirming one peer preserves a different peer's outstanding failure");

            Apply(view with { Paired = [] });
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ControlResponse?> late = module.SendAsync(new("pair-request", Peer: peer));
            Apply(view);
            control.Pending.SetResult(new(false, "pairing_tls_failed"));
            Pump(() => late.IsCompleted, dispatcher);
            late.GetAwaiter().GetResult(); control.Pending = null;
            check(module.pairingFailed && module.pairingStatus == unrelatedFailure &&
                !pane.Load()["pairingStatus"].Contains("TLS identity", StringComparison.Ordinal),
                "pairing: a late failed command cannot restore a matching error after confirmed trust or erase another peer's error");

            var changedCertificate = peer with { CertificateSha256 = new string('D', 64) };
            control.Response = new(false, "pairing_tls_failed");
            module.SendAsync(new("pair-request", Peer: changedCertificate)).GetAwaiter().GetResult();
            Apply(view);
            check(module.pairingFailed && pane.Load()["pairingStatus"].Contains("TLS identity", StringComparison.Ordinal),
                "pairing: matching broker ID with a different certificate cannot clear a failed identity");
            Apply(view with { Paired = [changedCertificate] });
            check(module.pairingFailed && module.pairingStatus == unrelatedFailure &&
                !pane.Load()["pairingStatus"].Contains("TLS identity", StringComparison.Ordinal),
                "pairing: only the exact confirmed certificate clears its scoped failure");

            var offer = new PairingView(Guid.NewGuid(), peer, "01234567 89ABCDEF FEDCBA98 76543210",
                DateTimeOffset.UtcNow.AddMinutes(2), true, true);
            Apply(view with { Paired = [], PairingOffers = [offer] });
            control.Response = new(true, "ok", view);
            module.SendAsync(new("pair-approve", offer.RequestId)).GetAwaiter().GetResult();
            shown = pane.Load()["pairingStatus"];
            check(shown.Contains("Paired with Approved peer", StringComparison.Ordinal) &&
                !shown.Contains("Sending this workstation", StringComparison.Ordinal) &&
                !shown.Contains("Confirmation received", StringComparison.Ordinal) && module.pairingStatus == unrelatedFailure,
                "pairing: confirmed command-response trust outranks generic confirmation wording while preserving unrelated errors");

            Apply(view with { Paired = [] });
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            late = module.SendAsync(new("pair-request", Peer: peer));
            Apply(view);
            control.Pending.SetResult(new(true, "ok", view with { Paired = [], PairingOffers = [offer] }));
            Pump(() => late.IsCompleted, dispatcher);
            late.GetAwaiter().GetResult(); control.Pending = null;
            check(pane.Load()["pairingStatus"].Contains("Paired with Approved peer", StringComparison.Ordinal) &&
                !pane.Load()["pairingStatus"].Contains("Pairing requested", StringComparison.Ordinal) &&
                module.snapshot!.Paired.Any(paired => paired.BrokerId == peer.BrokerId) && module.pairingStatus == unrelatedFailure,
                "pairing: a late successful pre-completion reply cannot replace already confirmed trust with an older pending snapshot");

            Apply(view with { Paired = [] });
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var invitation = new Dictionary<string, string> { ["manualPeer"] = JsonSerializer.Serialize(peer, ControlProtocol.Json) };
            Task<string> manual = module.PairInvitationAsync(invitation);
            Apply(view);
            control.Pending.SetResult(new(false, "pairing_connection_refused"));
            Pump(() => manual.IsCompleted, dispatcher);
            shown = manual.GetAwaiter().GetResult(); control.Pending = null;
            check(shown.StartsWith("Paired with Approved peer", StringComparison.Ordinal) &&
                !shown.StartsWith("The broker could not request", StringComparison.Ordinal),
                "pairing: a late failed invitation action returns current confirmed trust rather than contradicting the pane");

            Apply(view with { Paired = [], PairingOffers = [offer] });
            Apply(view with { Paired = [] });
            check(!pane.Load()["pairingStatus"].Contains("Paired with", StringComparison.Ordinal) && module.pairingFailed,
                "pairing: superseded or disappearing offers alone cannot imply completed trust");
            module.ApplyStatus(new(false, false, "worker_unresponsive"));
            check(!pane.Load()["pairingStatus"].Contains("Paired with", StringComparison.Ordinal) &&
                module.pairingStatus == unrelatedFailure,
                "pairing: a missing snapshot does not clear failures or assert stale live trust");
            control.Failure = new OperationCanceledException("SYNTHETIC_PRIVATE_PAIRING_DIAGNOSTIC");
            module.SendAsync(new("pair-approve", Guid.NewGuid())).GetAwaiter().GetResult();
            control.Failure = null; control.Response = new(true, "ok", view);
            module.SendAsync(new("pair-approve", Guid.NewGuid())).GetAwaiter().GetResult();
            check(module.pairingFailed && pane.Load()["pairingStatus"].Contains("outcome is unconfirmed", StringComparison.Ordinal) &&
                pane.Load()["pairingStatus"].Contains("could not reach", StringComparison.Ordinal),
                "pairing: an uncorrelated successful confirmation cannot clear unknown or unrelated failure subjects");

            module.ResetPairingStatus();
            Apply(view with { Paired = [], PairingOffers = [offer] });
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            late = module.SendAsync(new("pair-approve", offer.RequestId));
            Apply(view);
            Apply(view with { Paired = [] });
            control.Pending.SetResult(new(true, "ok", view));
            Pump(() => late.IsCompleted, dispatcher);
            late.GetAwaiter().GetResult(); control.Pending = null;
            check(module.snapshot!.Paired.Length == 0 && !pane.Load()["pairingStatus"].Contains("Paired with", StringComparison.Ordinal),
                "pairing: a delayed successful approval snapshot cannot resurrect trust after a newer revocation poll");

            Apply(view with { Paired = [] });
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            late = module.SendAsync(new("pair-request", Peer: peer));
            Apply(view with { Paired = [peer, other] });
            control.Pending.SetResult(new(true, "ok", view));
            Pump(() => late.IsCompleted, dispatcher);
            late.GetAwaiter().GetResult(); control.Pending = null;
            shown = pane.Load()["pairingStatus"];
            check(module.snapshot!.Paired.Length == 2 && shown.Contains("Approved peer", StringComparison.Ordinal) &&
                shown.Contains("Unrelated peer", StringComparison.Ordinal),
                "pairing: an older command snapshot cannot discard another peer confirmed by a newer poll");
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
