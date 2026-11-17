using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

// PANE-PAIR and PANE-POPUP (1.10.5): the Brokers list, its actions and the passive pairing signals, through the real
// module Init, pane and ListCard delegates with an injected control client and clock. Each label below has a killing
// mutation in tests/mutate-selftest-guards.py ("broker-pane: ...").
public sealed partial class AgentBrokerModule
{
    private sealed class PaneControl : IBrokerControlClient
    {
        internal List<ControlRequest> Requests { get; } = [];
        internal ControlResponse Response = new(true, "ok");
        internal TaskCompletionSource<ControlResponse>? Pending;
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Pending?.Task ?? Task.FromResult(Response);
        }
    }

    private static int PairingRequestCount(PaneControl control) =>
        control.Requests.Count(request => request.Operation is "pair-request" or "pair-approve" or "pair-reject" or "peer-revoke");

    // Ticks exactly the rows `pick` selects on a fresh render, then presses one list action. An exception is a result too.
    private static string PressBrokerAction(OptionsPane pane, string label, Func<ListItem, bool> pick)
    {
        ListCard card = pane.Lists!.Single();
        IReadOnlyList<ListItem> items = card.LoadItems();
        foreach (var item in items) card.SetChecked(item.Id, false);
        foreach (var item in items.Where(pick)) card.SetChecked(item.Id, true);
        return Invoke(card, label);
    }

    private static string Invoke(ListCard card, string label)
    {
        try { return card.Actions.Single(action => action.Label == label).InvokeAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { return "EXCEPTION " + error.GetType().Name; }
    }

    private static void RunPaneBrokerChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-pane-brokers-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        string cert(char ch) => new(ch, 64);
        var a = new PeerView(Guid.NewGuid(), "Same name", "https://192.0.2.31:7433/", cert('A'));
        var b = new PeerView(Guid.NewGuid(), "Same name", "https://192.0.2.32:7433/", cert('B'));
        var c = new PeerView(Guid.NewGuid(), "Charlie", "https://192.0.2.33:7433/", cert('C'));
        var bOtherCert = b with { CertificateSha256 = cert('D') };
        var now = new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
        const string codeA = "01234567 89ABCDEF FEDCBA98 76543210", codeC = "AAAAAAAA BBBBBBBB CCCCCCCC DDDDDDDD";
        var offerA = new PairingView(Guid.NewGuid(), a, codeA, now.AddMinutes(2), false);
        var offerC = new PairingView(Guid.NewGuid(), c, codeC, now.AddMinutes(2), true);
        var empty = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
            [], [], [], [], new Dictionary<string, string>(), cert('E'));
        var control = new PaneControl();
        var module = new AgentBrokerModule(control, new StatusSupervisor(empty)) { ClientSetup = new(new TestClientCommand()) };
        module.McpNow = () => now;
        void Apply(BrokerSnapshot view) => module.ApplyStatus(new(true, false, "standby", view));
        try
        {
            module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
            OptionsPane pane = host.OptionsPanes.Single();
            ListCard card = pane.Lists!.Single();

            // This checks values installed by Init, not references to contract members in compiled code.
            // CardEnabledWhen belongs to host 1.4.0; PaneAction.EnabledWhen and VisibleWhen need newer hosts.
            check(module.Info.MinHostVersion == "1.6.1" && pane.Lists.Count == 1 && card.DeferChanges == false &&
                pane.Actions.Concat(card.Actions).All(action => string.IsNullOrEmpty(action.EnabledWhen)) &&
                pane.Schema.All(field => string.IsNullOrEmpty(field.VisibleWhen) && string.IsNullOrEmpty(field.CardEnabledWhen)),
                "PANE_ABI_FLOOR: Init installs no PaneAction EnabledWhen, VisibleWhen or CardEnabledWhen condition on the 1.6.1 pane");
            check(new[] { PairedGroup, DiscoveredGroup, AwaitingGroup }.Order(StringComparer.OrdinalIgnoreCase).First() == AwaitingGroup,
                "BROKER_AWAITING_SORTS_FIRST: the host's ordinal group sort puts Awaiting approval above Discovered and Paired");

            // One identity, one row, first group that applies; a changed certificate stays visible under Discovered.
            Apply(empty with { PairingOffers = [offerA], Paired = [a, b], Discovered = [a, b, bOtherCert, c] });
            IReadOnlyList<ListItem> rows = card.LoadItems();
            check(rows.Count(row => row.Group == AwaitingGroup) == 1 && rows.Count(row => row.Group == PairedGroup) == 1 &&
                rows.Count(row => row.Group == DiscoveredGroup) == 2 && rows.Single(row => row.Group == PairedGroup).Id.Contains(b.BrokerId.ToString("N"), StringComparison.Ordinal) &&
                rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() == rows.Count,
                "BROKER_ROWS_GROUPED: each broker identity appears once, an offer before Paired before Discovered");
            check(rows.SingleOrDefault(row => row.Group == DiscoveredGroup && row.Id.Contains(cert('D'), StringComparison.Ordinal)) is
                { Detail: "certificate differs from the paired one" },
                "BROKER_ROWS_CERT_CHANGED: a discovered broker whose certificate differs from its paired one stays listed and says so");
            ListItem rowA = rows.Single(row => row.Group == AwaitingGroup), rowB = rows.Single(row => row.Group == PairedGroup);
            // The short ID leads a Paired or Discovered label, so two brokers with one name stay apart there.
            check(rowA.Label != rowB.Label && rowB.Label.StartsWith(b.BrokerId.ToString("N")[..8] + " ", StringComparison.Ordinal),
                "BROKER_ROWS_SAME_NAME: two brokers with one name are told apart by a short broker ID that leads the label");
            // The Detail is the code and nothing else: any suffix zeroed the row's checkbox width in the 360 DIP card.
            check(rowA.Detail == codeA && rowA.Id.Contains(offerA.RequestId.ToString("N"), StringComparison.Ordinal) &&
                rowA.Id.Contains(cert('A'), StringComparison.Ordinal),
                "BROKER_DETAIL_FULL_CODE: an Awaiting approval row's Detail is exactly the complete code, and its row ID binds the request and certificate");
            // An Awaiting row's label is the broker's name alone (owner decision 2026-11-12, replacing the short ID): its code
            // leaves the label 50 to 75 DIP, so a long name ends in an ellipsis and Pairing shows it in full. No name reads "Broker".
            Apply(empty with { PairingOffers = [offerA with { RequestId = Guid.NewGuid(), Peer = a with { Name = " " } }] });
            string unnamed = card.LoadItems().Single(row => row.Group == AwaitingGroup).Label;
            check(rowA.Label == "Same name" && unnamed == "Broker",
                "BROKER_AWAITING_NAME: an Awaiting approval row's label is the broker's name alone, and Broker when it has none");

            // An offer claiming a paired broker's ID under another certificate says so in its label and in Pairing. The label
            // leads with the warning sign, because the hint never fits on the row after a name.
            Apply(empty with { Paired = [b], PairingOffers = [offerA with { RequestId = Guid.NewGuid(), Peer = bOtherCert }] });
            ListItem impostor = card.LoadItems().Single(row => row.Group == AwaitingGroup);
            string impostorText = module.PairingDisplayText();
            check(impostor.Label == "\u26A0 Same name, " + CertificateDiffersHint && impostor.Detail == codeA &&
                impostorText.Contains(", " + CertificateDiffersHint + ")", StringComparison.Ordinal) &&
                impostorText.Contains("cannot complete while that pairing is saved", StringComparison.Ordinal),
                "BROKER_AWAITING_CERT_DIFFERS: an offer for a paired broker ID under another certificate says so in its row and in Pairing");

            // The sharing scope is on screen before any press, on both workstations: incoming and outgoing offers alike.
            Apply(empty with { PairingOffers = [offerA] });
            string incomingText = module.PairingDisplayText();
            Apply(empty with { PairingOffers = [offerC with { LocalApproved = false }] });
            string outgoingText = module.PairingDisplayText();
            check(incomingText.Contains(PairingScope, StringComparison.Ordinal) && incomingText.Contains("until ", StringComparison.Ordinal) &&
                outgoingText.Contains(PairingScope, StringComparison.Ordinal) && outgoingText.StartsWith("Your pairing request to Charlie (", StringComparison.Ordinal),
                "SCOPE_BEFORE_DECISION: the Pairing row states who, until when and the sharing scope while an incoming or outgoing offer waits");

            // The passive signal is for incoming offers only: an outgoing request, an offer approved here and an expired
            // incoming offer raise nothing on the tray root, Status or Pairing.
            bool Quiet()
            {
                var values = pane.Load();
                return module.TrayRootLabel() == "AgentBroker" &&
                    !values["status"].Contains("Pairing request waiting", StringComparison.Ordinal) &&
                    !module.PairingDisplayText().Contains("Pairing request waiting", StringComparison.Ordinal);
            }
            check(Quiet(), "INBOUND_OFFER_SIGNAL: an outgoing request raises no incoming signal");
            Apply(empty with { PairingOffers = [offerA with { LocalApproved = true }] });
            check(Quiet(), "INBOUND_OFFER_SIGNAL: an incoming offer approved here raises no signal");
            Apply(empty with { PairingOffers = [offerA] });
            module.McpNow = () => now.AddMinutes(3);
            bool expiredQuiet = Quiet();
            module.McpNow = () => now;
            check(expiredQuiet && !Quiet(), "INBOUND_OFFER_SIGNAL: an expired incoming offer raises no signal");

            // The live Pairing row is in the notified set: a new requester reaches an open pane without a rebuild.
            int infoChanges = 0;
            pane.InfoChanged += () => infoChanges++;
            Apply(empty with { PairingOffers = [offerA with { RequestId = Guid.NewGuid(), Peer = c }] });
            check(infoChanges > 0 && module.PairingDisplayText().StartsWith("Pairing request waiting from Charlie (", StringComparison.Ordinal),
                "BROKER_SUMMARY_LIVE: an offer from another broker replacing the waiting one notifies the open pane");

            // Request pairing sends the exact discovered record, certificate pin included.
            Apply(empty with { Discovered = [c] });
            string result = PressBrokerAction(pane, RequestPairingLabel, row => row.Group == DiscoveredGroup);
            check(control.Requests.LastOrDefault() is { Operation: "pair-request" } request && request.Peer == c &&
                result.Contains("does not share sessions", StringComparison.Ordinal),
                "BROKER_REQUEST_EXACT_PIN: Request pairing sends the ticked broker's exact identity and certificate");

            // A row rendered for a discovered record that changed before the press sends nothing.
            Apply(empty with { Discovered = [c] });
            foreach (var item in card.LoadItems()) card.SetChecked(item.Id, item.Group == DiscoveredGroup);
            Apply(empty with { Discovered = [c with { Endpoint = "https://192.0.2.77:7433/" }] });
            int beforeStale = PairingRequestCount(control);
            result = Invoke(card, RequestPairingLabel);
            check(PairingRequestCount(control) == beforeStale && result == BrokerListChanged,
                "BROKER_REQUEST_STALE_ROW_REFUSED: a row rendered for a discovered record that changed before the press requests nothing");

            // A row from the wrong group is refused by every action that needs another group.
            Apply(empty with { Discovered = [c] });
            beforeStale = PairingRequestCount(control);
            result = PressBrokerAction(pane, ApprovePairingLabel, row => row.Group == DiscoveredGroup);
            string wrongCancel = Invoke(card, CancelPairingLabel), wrongRevoke = Invoke(card, RevokeSharingLabel);
            check(PairingRequestCount(control) == beforeStale && result == "This action needs a broker under " + AwaitingGroup + "." &&
                wrongCancel == "This action needs a broker under " + AwaitingGroup + "." && wrongRevoke == "This action needs a broker under " + PairedGroup + ".",
                "BROKER_WRONG_GROUP_REFUSED: approve, cancel and revoke refuse a ticked row from another group and send nothing");

            // Exactly one ticked row: a group header can tick several, and that sends nothing.
            Apply(empty with { Discovered = [b, c] });
            int before = PairingRequestCount(control);
            result = PressBrokerAction(pane, RequestPairingLabel, row => row.Group == DiscoveredGroup);
            check(PairingRequestCount(control) == before && result.StartsWith("Tick exactly one broker.", StringComparison.Ordinal),
                "BROKER_EXACTLY_ONE: two ticked rows send nothing and say to tick one");

            // Approve the ticked offer, not the first one.
            Apply(empty with { PairingOffers = [offerA, offerC] });
            result = PressBrokerAction(pane, ApprovePairingLabel, row => row.Id.Contains(offerC.RequestId.ToString("N"), StringComparison.Ordinal));
            check(control.Requests.LastOrDefault() is { Operation: "pair-approve" } approve && approve.Id == offerC.RequestId &&
                result.Contains(codeC, StringComparison.Ordinal),
                "BROKER_APPROVE_TICKED_OFFER: Codes match: approve sends the ticked offer's request and names its full code");

            // The workstation that approves second: the reply's snapshot already lists the broker as paired.
            Apply(empty with { PairingOffers = [offerA] });
            control.Response = new(true, "ok", empty with { Paired = [a] });
            string secondApprover = PressBrokerAction(pane, ApprovePairingLabel, row => row.Group == AwaitingGroup);
            control.Response = new(true, "ok");
            check(module.snapshot!.Paired.Length == 1 && secondApprover.Contains(codeA, StringComparison.Ordinal) &&
                secondApprover.Contains(PairingCompleteSentence, StringComparison.Ordinal) && secondApprover.Contains(PairingScope, StringComparison.Ordinal),
                "SCOPE_AT_DECISION: the workstation that approves second, and so completes the pairing, still gets the sharing scope");

            // A rendered row binds the whole offer: a changed certificate or code under the same request sends nothing.
            Apply(empty with { PairingOffers = [offerA] });
            ListItem shownA = card.LoadItems().Single();
            foreach (var item in card.LoadItems()) card.SetChecked(item.Id, item.Id == shownA.Id);
            before = PairingRequestCount(control);
            Apply(empty with { PairingOffers = [offerA with { Peer = a with { CertificateSha256 = cert('F') } }] });
            string changedPeer = Invoke(card, ApprovePairingLabel);
            Apply(empty with { PairingOffers = [offerA with { VerificationCode = codeC }] });
            string changedCode = Invoke(card, ApprovePairingLabel);
            check(PairingRequestCount(control) == before && changedPeer.StartsWith("This offer changed", StringComparison.Ordinal) &&
                changedCode.StartsWith("This offer changed", StringComparison.Ordinal),
                "BROKER_APPROVE_STALE_ROW_REFUSED: a row rendered for one peer and code cannot approve a changed certificate or code");

            // Expiry is checked at the press, while the rendered row may still show the offer.
            Apply(empty with { PairingOffers = [offerA] });
            foreach (var item in card.LoadItems()) card.SetChecked(item.Id, true);
            module.McpNow = () => now.AddMinutes(3);
            before = PairingRequestCount(control);
            result = Invoke(card, ApprovePairingLabel);
            module.McpNow = () => now;
            check(PairingRequestCount(control) == before && result.StartsWith("This offer expired.", StringComparison.Ordinal),
                "BROKER_APPROVE_EXPIRED_REFUSED: an expired offer is never approved from a row still on screen");

            // Cancel sends pair-reject for the exact ticked request.
            result = PressBrokerAction(pane, CancelPairingLabel, row => row.Group == AwaitingGroup);
            check(control.Requests.LastOrDefault() is { Operation: "pair-reject" } reject && reject.Id == offerA.RequestId &&
                result.StartsWith("Pairing request cancelled", StringComparison.Ordinal) &&
                result.EndsWith("No pairing was created.", StringComparison.Ordinal),
                "BROKER_CANCEL_EXACT: Cancel pairing rejects the ticked request and approves nothing");
            // A broker that is already paired under the same identity can still have a fresh offer waiting; cancelling it
            // removes only the offer, so the result must say the pairing is unchanged, never that nothing is shared (FU-2).
            Apply(empty with { Paired = [a], PairingOffers = [offerA] });
            result = PressBrokerAction(pane, CancelPairingLabel, row => row.Group == AwaitingGroup);
            check(control.Requests.LastOrDefault() is { Operation: "pair-reject" } keptReject && keptReject.Id == offerA.RequestId &&
                result.StartsWith("Pairing request cancelled", StringComparison.Ordinal) &&
                result.EndsWith(PairingCancelKeepsTrust, StringComparison.Ordinal) &&
                !result.Contains("Nothing is shared", StringComparison.Ordinal) && !result.Contains("No pairing was created", StringComparison.Ordinal),
                "BROKER_CANCEL_TRUST_NEUTRAL: cancelling a fresh offer from an already paired broker says its pairing is unchanged, never that nothing is shared");
            // FU-2's other axis: the saved pairing is looked up by broker ID alone, so an offer from b's broker ID under another
            // certificate (bOtherCert, D) still finds b's saved pairing (certificate B). Cancelling sends only that offer's
            // pair-reject, the result is exactly the trust-neutral one, and the saved pairing is left as it was.
            PairingView otherCertOffer = offerA with { RequestId = Guid.NewGuid(), Peer = bOtherCert };
            Apply(empty with { Paired = [b], PairingOffers = [otherCertOffer] });
            int beforeOtherCert = PairingRequestCount(control);
            bool otherCertAwaiting = card.LoadItems().Any(row => row.Group == AwaitingGroup && row.Id.Contains(cert('D'), StringComparison.Ordinal));
            result = PressBrokerAction(pane, CancelPairingLabel, row => row.Group == AwaitingGroup);
            check(otherCertAwaiting && PairingRequestCount(control) == beforeOtherCert + 1 &&
                control.Requests.LastOrDefault() is { Operation: "pair-reject" } otherCertReject && otherCertReject.Id == otherCertOffer.RequestId &&
                result == "Pairing request cancelled for Same name (" + b.BrokerId.ToString("N")[..8] + "). " + PairingCancelKeepsTrust &&
                module.snapshot!.Paired.Length == 1 && module.snapshot.Paired[0] == b && module.snapshot.Paired[0].CertificateSha256 == cert('B'),
                "BROKER_CANCEL_TRUST_NEUTRAL: cancelling an offer from a paired broker ID under another certificate says its saved pairing is unchanged, and leaves it");

            // Revoke: the first press arms the ticked row only, the second within 15 s revokes that broker, then reloads.
            Apply(empty with { Paired = [b, c] });
            PaneAction revoke = card.Actions.Single(action => action.Label == RevokeSharingLabel);
            before = PairingRequestCount(control);
            string first = PressBrokerAction(pane, RevokeSharingLabel, row => row.Id.Contains(b.BrokerId.ToString("N"), StringComparison.Ordinal));
            bool reloadAfterFirst = revoke.ReloadPaneAfter;
            int afterFirst = PairingRequestCount(control);
            module.McpNow = () => now.AddSeconds(10);
            string second = Invoke(card, RevokeSharingLabel);
            module.McpNow = () => now;
            check(afterFirst == before && !reloadAfterFirst && first.StartsWith("Press Revoke sharing again", StringComparison.Ordinal) &&
                control.Requests.LastOrDefault() is { Operation: "peer-revoke" } revoked && revoked.Id == b.BrokerId &&
                second.StartsWith("Sharing revoked", StringComparison.Ordinal),
                "BROKER_REVOKE_TWO_PRESS: the first press only arms, the second within 15 seconds revokes the ticked broker");
            check(revoke.ReloadPaneAfter, "BROKER_REVOKE_RELOADS: a press that revoked asks the host to reload the pane");
            // The next first press, after a revoke left ReloadPaneAfter true, must not reload: the reload would disarm it.
            before = PairingRequestCount(control);
            string firstAgain = PressBrokerAction(pane, RevokeSharingLabel, row => row.Id.Contains(c.BrokerId.ToString("N"), StringComparison.Ordinal));
            check(!revoke.ReloadPaneAfter && PairingRequestCount(control) == before &&
                firstAgain.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_FIRST_PRESS_NO_RELOAD: a first press after a revoke asks for no reload, so the arm survives to the second press");
            // Another worker identity disarms, even when it lists the same broker as paired.
            module.McpNow = () => now.AddSeconds(5);
            Apply(empty with { BrokerId = Guid.NewGuid(), Paired = [b, c] });
            string otherWorker = Invoke(card, RevokeSharingLabel);
            module.McpNow = () => now;
            Apply(empty with { Paired = [b, c] });
            check(PairingRequestCount(control) == before && otherWorker.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_CLEARED_ON_IDENTITY: a broker identity change between the presses disarms the revoke");
            // Any tick change disarms, even back to the same row (armed again first: the identity change above disarmed).
            string cTicked = module.renderedBrokerRows.Keys.Single(id => id.Contains(c.BrokerId.ToString("N"), StringComparison.Ordinal));
            bool armedBeforeTick = Invoke(card, RevokeSharingLabel).StartsWith("Press Revoke sharing again", StringComparison.Ordinal) && module.revokeArm is not null;
            card.SetChecked(cTicked, false); card.SetChecked(cTicked, true);
            string reTicked = Invoke(card, RevokeSharingLabel);
            check(armedBeforeTick && PairingRequestCount(control) == before && reTicked.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_CLEARED_ON_TICK: a tick change between the presses disarms the revoke");
            // A clock stepped back to before the arm time re-arms instead of confirming with a stretched window. A step back that
            // stays inside the 15-second window is not detected; the second explicit press is still required (FU-4).
            module.McpNow = () => now.AddHours(-1);
            string backward = Invoke(card, RevokeSharingLabel);
            module.McpNow = () => now;
            check(PairingRequestCount(control) == before && backward.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_BACKWARD_CLOCK: a clock stepped back to before the arm time re-arms instead of revoking");
            // Deadline: a second press after 15 s re-arms instead of revoking.
            before = PairingRequestCount(control);
            PressBrokerAction(pane, RevokeSharingLabel, row => row.Id.Contains(b.BrokerId.ToString("N"), StringComparison.Ordinal));
            module.McpNow = () => now.AddSeconds(16);
            string late = Invoke(card, RevokeSharingLabel);
            module.McpNow = () => now;
            check(PairingRequestCount(control) == before && late.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_DEADLINE: a second press after the 15-second window arms again and revokes nothing");
            // Binding: an arm on one row never confirms another, even if the tick moves without a SetChecked call.
            before = PairingRequestCount(control);
            PressBrokerAction(pane, RevokeSharingLabel, row => row.Id.Contains(b.BrokerId.ToString("N"), StringComparison.Ordinal));
            string cRow = module.renderedBrokerRows.Keys.Single(id => id.Contains(c.BrokerId.ToString("N"), StringComparison.Ordinal));
            module.tickedBrokerRows.Clear(); module.tickedBrokerRows.Add(cRow);
            string moved = Invoke(card, RevokeSharingLabel);
            check(PairingRequestCount(control) == before && moved.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_BOUND_ROW: an arm made on one broker cannot revoke a different ticked broker");
            // A new render (pane reopened) disarms.
            PressBrokerAction(pane, RevokeSharingLabel, row => row.Id.Contains(c.BrokerId.ToString("N"), StringComparison.Ordinal));
            card.LoadItems();
            string reopened = Invoke(card, RevokeSharingLabel);
            check(PairingRequestCount(control) == before && reopened.StartsWith("Press Revoke sharing again", StringComparison.Ordinal),
                "BROKER_REVOKE_CLEARED_ON_RENDER: reopening the pane makes the next press a first press again");

            // Every row action refuses without a current snapshot, after the row was rendered and ticked.
            foreach (var (label, group, view) in new[] {
                (RequestPairingLabel, DiscoveredGroup, empty with { Discovered = [c] }),
                (ApprovePairingLabel, AwaitingGroup, empty with { PairingOffers = [offerA] }),
                (CancelPairingLabel, AwaitingGroup, empty with { PairingOffers = [offerA] }),
                (RevokeSharingLabel, PairedGroup, empty with { Paired = [c] }) })
            {
                Apply(view);
                foreach (var item in card.LoadItems()) card.SetChecked(item.Id, item.Group == group);
                module.ApplyStatus(new(false, false, "worker_unresponsive"));
                before = PairingRequestCount(control);
                result = Invoke(card, label);
                check(PairingRequestCount(control) == before && result == BrokerListUnavailable,
                    "BROKER_ACTIONS_NEED_SNAPSHOT: " + label + " refuses while the broker status is unavailable");
            }

            // One action at a time: a second press while the first waits sends nothing.
            Apply(empty with { PairingOffers = [offerA] });
            foreach (var item in card.LoadItems()) card.SetChecked(item.Id, true);
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PaneAction approveAction = card.Actions.Single(action => action.Label == ApprovePairingLabel),
                cancelAction = card.Actions.Single(action => action.Label == CancelPairingLabel),
                refreshAction = card.Actions.Single(action => action.Label == RefreshBrokersLabel);
            Task<string> running = approveAction.InvokeAsync();
            bool approveReloadWhileWaiting = approveAction.ReloadPaneAfter;
            before = PairingRequestCount(control);
            int statusWhileBusy = control.Requests.Count(request => request.Operation == "status");
            // Each other button as a previous press of it would have left it, asking for a reload.
            foreach (var other in new[] { cancelAction, refreshAction, revoke }) other.ReloadPaneAfter = true;
            // Not awaited here: a press that was not refused would wait on the same pending control reply.
            Task<string> concurrentPress = cancelAction.InvokeAsync();
            Task<string> busyRefresh = refreshAction.InvokeAsync(), busyRevoke = revoke.InvokeAsync();
            bool refusedAtOnce = concurrentPress.IsCompleted;
            bool busyRefusedAtOnce = busyRefresh.IsCompleted && busyRevoke.IsCompleted;
            bool noBusyReload = !cancelAction.ReloadPaneAfter && !refreshAction.ReloadPaneAfter && !revoke.ReloadPaneAfter;
            bool noBusyStatus = control.Requests.Count(request => request.Operation == "status") == statusWhileBusy;
            control.Pending.SetResult(new(true, "ok")); control.Pending = null;
            Pump(() => running.IsCompleted && concurrentPress.IsCompleted && busyRefresh.IsCompleted && busyRevoke.IsCompleted, dispatcher);
            string concurrent = concurrentPress.GetAwaiter().GetResult();
            check(refusedAtOnce && PairingRequestCount(control) == before && concurrent.StartsWith("Another broker action is still running", StringComparison.Ordinal),
                "BROKER_ACTIONS_SERIALIZED: a second list action while one is waiting sends nothing");
            check(busyRefusedAtOnce && noBusyReload && noBusyStatus && !approveReloadWhileWaiting && approveAction.ReloadPaneAfter &&
                busyRefresh.GetAwaiter().GetResult().StartsWith("Another broker action is still running", StringComparison.Ordinal) &&
                busyRevoke.GetAwaiter().GetResult().StartsWith("Another broker action is still running", StringComparison.Ordinal),
                "BROKER_BUSY_PRESS_NO_RELOAD: a press refused while another action waits asks for no reload, so the waiting action's result line survives");

            // A restart while an action waits for its reply: the action reports the restart and restores nothing old.
            Apply(empty with { PairingOffers = [offerA] });
            foreach (var item in card.LoadItems()) card.SetChecked(item.Id, true);
            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> acrossRestart = approveAction.InvokeAsync();
            module.Restart();
            control.Pending.SetResult(new(true, "ok", empty with { Paired = [a] })); control.Pending = null;
            Pump(() => acrossRestart.IsCompleted && module.snapshot is not null, dispatcher);
            check(acrossRestart.GetAwaiter().GetResult() == BrokerActionRestarted && module.snapshot!.Paired.Length == 0 &&
                module.tickedBrokerRows.Count == 0,
                "BROKER_ACTION_RESTART_DURING_AWAIT: an action whose reply arrives after a restart reports the restart and acts on nothing old");

            // Refresh brokers asks the worker for status and the host to reload the pane, which re-reads the list.
            control.Response = new(true, "ok", empty with { Discovered = [c with { Endpoint = "https://192.0.2.99:7433/" }] });
            int statusBefore = control.Requests.Count(request => request.Operation == "status");
            result = Invoke(card, RefreshBrokersLabel);
            control.Response = new(true, "ok");
            check(card.Actions.Single(action => action.Label == RefreshBrokersLabel).ReloadPaneAfter &&
                control.Requests.Count(request => request.Operation == "status") == statusBefore + 1 && result == "Broker list refreshed." &&
                module.snapshot!.Discovered.Single().Endpoint == "https://192.0.2.99:7433/",
                "BROKER_REFRESH_RELOADS: Refresh brokers requests status and reloads the pane so the list is rendered again");

            // The empty list says why it is empty: no brokers, an unavailable status, or AgentBroker turned off.
            Apply(empty);
            bool noBrokers = card.LoadItems().Count == 0 && card.EmptyHint == BrokerListEmpty;
            module.ApplyStatus(new(false, false, "worker_unresponsive"));
            bool unavailableHint = card.LoadItems().Count == 0 && card.EmptyHint == BrokerListUnavailable;
            module.SaveOptions(new Dictionary<string, string> { ["enabled"] = "false" });
            check(noBrokers && unavailableHint && card.LoadItems().Count == 0 && card.EmptyHint == BrokerListDisabled,
                "BROKER_EMPTY_HINT_STATE: the empty Brokers list names its real cause, so an unavailable or disabled broker never reads as no brokers");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }

        RunNoPopupChecks(check, dispatcher);
        RunStaleOfferRowChecks(check, dispatcher);
    }

    // PANE-POPUP: forty status snapshots and three restarts with a discovered broker and incoming offers raise no question
    // and send no pairing request, approval or rejection.
    private static void RunNoPopupChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-no-popup-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var found = new PeerView(Guid.NewGuid(), "Found broker", "https://192.0.2.41:7433/", new string('A', 64));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
            [found], [], [], [], new Dictionary<string, string>(), new string('E', 64));
        var incoming = new PairingView(Guid.NewGuid(), found, "01234567 89ABCDEF FEDCBA98 76543210", DateTimeOffset.UtcNow.AddMinutes(2), false);
        var control = new PaneControl();
        var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
            for (int restart = 0; restart < 3; restart++)
            {
                for (int poll = 0; poll < 14; poll++) module.ApplyStatus(new(true, false, "standby", view));
                module.Restart();
                Pump(() => module.snapshot is not null, dispatcher);
            }
            check(host.Asks.Count == 0 && PairingRequestCount(control) == 0,
                "NO_DISCOVERY_POPUP: a discovered broker across 40 snapshots and 3 restarts raises no question and sends no request");
            for (int poll = 0; poll < 40; poll++) module.ApplyStatus(new(true, false, "standby", view with { PairingOffers = [incoming] }));
            check(host.Asks.Count == 0, "NO_OFFER_POPUP: an incoming offer across 40 snapshots raises no question");
            check(!control.Requests.Any(request => request.Operation is "pair-approve" or "pair-reject"),
                "NO_AUTO_APPROVE: an incoming offer from a discovered broker is never approved or rejected without a press");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }

    // Replaces the removed offer-question checks (withdrawn and late questions): a stale rendered row never approves a
    // replacement offer, only an explicit press on the freshly rendered row sends its exact request, and a restart clears
    // ticks and bindings so nothing old can be confirmed after it.
    private static void RunStaleOfferRowChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-stale-offer-rows-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var peer = new PeerView(Guid.NewGuid(), "Synthetic peer", "https://192.168.1.2:7433/", new string('B', 64));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", "https://192.168.1.1:7433/",
            [], [], [], [], new Dictionary<string, string>(), new string('A', 64));
        var control = new PaneControl();
        var module = new AgentBrokerModule(control, new StatusSupervisor(view)) { ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
            OptionsPane pane = host.OptionsPanes.Single();
            ListCard card = pane.Lists!.Single();
            var oldOffer = new PairingView(Guid.NewGuid(), peer, "11111111 11111111 11111111 11111111", DateTimeOffset.UtcNow.AddMinutes(2), true);
            module.ApplyStatus(new(true, true, "running", view with { PairingOffers = [oldOffer] }));
            foreach (var item in card.LoadItems()) card.SetChecked(item.Id, true);
            var freshOffer = oldOffer with { RequestId = Guid.NewGuid(), Outbound = false, VerificationCode = "22222222 22222222 22222222 22222222" };
            module.ApplyStatus(new(true, true, "running", view with { PairingOffers = [freshOffer] }));
            string stale = Invoke(card, ApprovePairingLabel);
            check(PairingRequestCount(control) == 0 && stale.StartsWith("This offer changed or ended", StringComparison.Ordinal),
                "pairing: a row rendered for a replaced offer cannot approve either the old or the replacement offer");
            IReadOnlyList<ListItem> fresh = card.LoadItems();
            check(fresh.Count == 1 && fresh[0].Detail.StartsWith(freshOffer.VerificationCode, StringComparison.Ordinal) && !fresh[0].Checked,
                "pairing: the re-rendered list shows only the fresh complete code, unticked");
            card.SetChecked(fresh[0].Id, true);
            Invoke(card, ApprovePairingLabel);
            check(control.Requests.Count(request => request.Operation == "pair-approve") == 1 &&
                control.Requests.Single(request => request.Operation == "pair-approve").Id == freshOffer.RequestId,
                "pairing: only an explicit press on the freshly rendered row sends its exact replacement request ID");
            module.Restart();
            Pump(() => module.snapshot is not null, dispatcher);
            module.ApplyStatus(new(true, true, "running", view with { PairingOffers = [freshOffer] }));
            string afterRestart = Invoke(card, ApprovePairingLabel);
            check(control.Requests.Count(request => request.Operation == "pair-approve") == 1 &&
                afterRestart.StartsWith("Tick one broker", StringComparison.Ordinal),
                "pairing: restart clears ticks and rendered rows, so no approval or rejection is sent for stale state");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }
}
