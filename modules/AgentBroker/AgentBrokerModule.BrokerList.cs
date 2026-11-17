using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

// PANE-PAIR (1.10.5, N-broker-pane-pairing-01): pairing in the pane, through the existing ListCard ABI (no host change,
// MinHostVersion stays 1.6.1). The list is the view the owner last rendered, not a live view: the host re-reads a ListCard
// only when the pane is built, so "Refresh brokers" and every mutating action ask for a pane reload, while the live Pairing
// row and tray label say when something new is waiting. Each action acts on exactly one ticked row and checks that row's
// rendered binding against the current snapshot before it sends anything, so a stale row never acts on a different peer,
// certificate, request or code. The tray menus keep sending the same control requests.
public sealed partial class AgentBrokerModule
{
    internal const string AwaitingGroup = "Awaiting approval", PairedGroup = "Paired", DiscoveredGroup = "Discovered";
    internal const string RefreshBrokersLabel = "Refresh brokers", RequestPairingLabel = "Request pairing",
        ApprovePairingLabel = "Codes match: approve", CancelPairingLabel = "Cancel pairing", RevokeSharingLabel = "Revoke sharing";
    private const int BrokerGroupLimit = 32;
    internal static readonly TimeSpan RevokeConfirmWindow = TimeSpan.FromSeconds(15);
    private const string BrokerListUnavailable = "The broker status is unavailable. Press Refresh brokers after the broker reconnects.";
    private const string BrokerListChanged = "That broker changed after the list was shown. Press Refresh brokers and check it again.";
    // The Brokers card's empty text says why it is empty (review LP-SAFE-6): an unavailable state is not an empty list.
    private const string BrokerListEmpty = "No brokers to show. Turn on LAN discovery and pairing, then press Refresh brokers.";
    private const string BrokerListDisabled = "AgentBroker is off. Enable AgentBroker to list brokers.";
    private const string BrokerListUnreadable = "The broker list could not be shown. Press Refresh brokers.";
    internal const string CertificateDiffersHint = "certificate differs from the paired one";

    // One rendered row. Its Id carries the group, the broker ID and certificate, and an offer's request ID, so a tick is
    // bound to that exact subject. Offer rows keep the complete offer they showed, code and expiry included; only a pane
    // render replaces this map, and no live Info refresh touches it.
    private sealed record BrokerRow(string Id, string Group, string Label, string Detail, PeerView Peer, PairingView? Offer);

    private ListCard? brokerCard;
    private IReadOnlyDictionary<string, BrokerRow> renderedBrokerRows = new Dictionary<string, BrokerRow>(StringComparer.Ordinal);
    private readonly HashSet<string> tickedBrokerRows = new(StringComparer.Ordinal);
    private (string RowId, DateTimeOffset Deadline)? revokeArm;
    private bool brokerActionRunning;
    private const string CopyInvitationLabel = "Copy my invitation";
    private const string PairCopiedInvitationLabel = "Pair from copied invitation";

    private ListCard BuildBrokerCard() => new()
    {
        Title = "Brokers",
        LoadItems = LoadBrokerItems,
        SetChecked = SetBrokerChecked,
        DeferChanges = false,
        EmptyHint = BrokerListEmpty,
        Actions =
        [
            BrokerAction(RefreshBrokersLabel, RefreshBrokersAsync),
            BrokerAction(RequestPairingLabel, RequestPairingFromListAsync),
            BrokerAction(ApprovePairingLabel, ApprovePairingFromListAsync),
            BrokerAction(CancelPairingLabel, CancelPairingFromListAsync),
            BrokerAction(RevokeSharingLabel, RevokeFromListAsync),
            BrokerAction(CopyInvitationLabel, _ => CopyInvitationAsync()),
            BrokerAction(PairCopiedInvitationLabel, PairCopiedInvitationAsync)
        ]
    };

    // Every list action runs through RunBrokerActionAsync with its own PaneAction, built once in Init. The host reads
    // ReloadPaneAfter after the press, so each press starts with it false and an action sets it only once it has sent its
    // request: a refused press, or a second press while another action waits, never rebuilds the view and so never drops
    // the waiting action's result line (review LP-SAFE-4).
    private PaneAction BrokerAction(string label, Func<PaneAction, Task<string>> body)
    {
        var action = new PaneAction { Label = label };
        action.InvokeAsync = () => RunBrokerActionAsync(action, body);
        return action;
    }

    private static string ShortBrokerId(Guid id) => id.ToString("N")[..8];

    private static string SafeBrokerName(PeerView peer)
    {
        string name = Safe(StripDisplayIdentity(peer.Name), 48);
        return string.IsNullOrWhiteSpace(name) ? "Broker" : name;
    }

    // A Paired or Discovered row label: "<first 8 hex of the broker ID> <name>", so two brokers with one name stay
    // distinguishable and the same broker reads alike in every group.
    private static string BrokerRowLabel(PeerView peer) => ShortBrokerId(peer.BrokerId) + " " + SafeBrokerName(peer);

    // An Awaiting approval row's label is the broker's name (owner decision 2026-11-12, replacing the short ID of review
    // LP-SAFE-1). The row's complete code leaves the label about 50 to 75 DIP of the 360 DIP card (median 65, measured over
    // 2,000 random codes), so a long name ends in an ellipsis: a default machine name such as DESKTOP-7Q2K9LM (about 106 DIP
    // at Segoe UI 12) shows as "DESKTOP-" and the ellipsis. The Pairing row shows the full name beside the short ID. A changed
    // certificate (LP-SAFE-5) leads with the warning sign (about 20 DIP), because the hint (about 197 DIP) never fits after a name.
    private static string AwaitingRowLabel(BrokerSnapshot view, PeerView peer) => CertificateDiffers(view, peer)
        ? "\u26A0 " + SafeBrokerName(peer) + ", " + CertificateDiffersHint
        : SafeBrokerName(peer);

    // The same broker in prose (result lines and the Pairing row): "<name> (<first 8 hex of the broker ID>)".
    private static string BrokerRowName(PeerView peer) => SafeBrokerName(peer) + " (" + ShortBrokerId(peer.BrokerId) + ")";

    private static string BrokerRowKey(PairingPeerIdentity identity) =>
        identity.BrokerId.ToString("N") + ":" + identity.CertificateSha256;

    // The complete comparison code and nothing else: at Segoe UI 12 it is about 228 DIP, and anything appended pushed the
    // row's checkbox to zero width. Direction and expiry are in the live Pairing row.
    private static string OfferDetail(PairingView offer) => Safe(offer.VerificationCode, 35);

    // A broker ID already paired under another certificate. Core refuses to finalize such a pairing while the old one is
    // saved (BrokerStore.AuthorizePeer, peer_identity_changed), so the list and the Pairing row say so.
    private static bool CertificateDiffers(BrokerSnapshot? view, PeerView peer) => view is not null &&
        view.Paired.Any(paired => paired.BrokerId == peer.BrokerId && PairingIdentity(paired) != PairingIdentity(peer));

    private static string PairedDetail(PeerView peer, BrokerSnapshot view)
    {
        string endpoint = "endpoint unavailable";
        if (Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0)
        {
            try { endpoint = Safe(InvitationPreviewAuthority(peer), 80); }
            catch (UriFormatException) { }
        }
        return view.PeerClocks?.FirstOrDefault(clock => clock.BrokerId == peer.BrokerId)?.Warning == true
            ? endpoint + ", check clocks" : endpoint;
    }

    // Each broker identity (broker ID and certificate) appears once, in the first group that applies: a pairing offer under
    // Awaiting approval (which sorts first), then Paired, then Discovered. A discovered record whose broker ID is paired
    // under a different certificate stays under Discovered and says so. At most 32 rows per group.
    private static List<BrokerRow> BrokerRows(BrokerSnapshot view)
    {
        var rows = new List<BrokerRow>();
        var shown = new HashSet<PairingPeerIdentity>();
        foreach (var offer in view.PairingOffers.Where(offer => PairingIdentity(offer.Peer) is not null)
            .OrderBy(offer => offer.ExpiresAt).ThenBy(offer => offer.RequestId).Take(BrokerGroupLimit))
        {
            PairingPeerIdentity identity = PairingIdentity(offer.Peer)!.Value;
            shown.Add(identity);
            // The label carries the certificate hint, never the Detail, whose width the full code needs (review LP-SAFE-5).
            rows.Add(new("offer:" + offer.RequestId.ToString("N") + ":" + BrokerRowKey(identity), AwaitingGroup,
                AwaitingRowLabel(view, offer.Peer), OfferDetail(offer), offer.Peer, offer));
        }
        foreach (var peer in view.Paired.Where(peer => PairingIdentity(peer) is { } identity && shown.Add(identity)).ToArray()
            .OrderBy(peer => Safe(peer.Name, 48), StringComparer.OrdinalIgnoreCase).ThenBy(peer => peer.BrokerId).Take(BrokerGroupLimit))
            rows.Add(new("paired:" + BrokerRowKey(PairingIdentity(peer)!.Value), PairedGroup, BrokerRowLabel(peer),
                PairedDetail(peer, view), peer, null));
        foreach (var peer in view.Discovered.Where(peer => peer.BrokerId != view.BrokerId &&
                PairingIdentity(peer) is { } identity && shown.Add(identity)).ToArray()
            .OrderBy(peer => Safe(peer.Name, 48), StringComparer.OrdinalIgnoreCase).ThenBy(peer => peer.BrokerId).Take(BrokerGroupLimit))
            rows.Add(new("discovered:" + BrokerRowKey(PairingIdentity(peer)!.Value), DiscoveredGroup, BrokerRowLabel(peer),
                view.Paired.Any(paired => paired.BrokerId == peer.BrokerId) ? CertificateDiffersHint : "not paired",
                peer, null));
        return rows;
    }

    // Runs on every pane build, which is also every pane open: a revoke pressed once before is disarmed here, so a closed
    // and reopened pane is never one press from revocation. Ticks survive only on rows rendered again with the same Id.
    // The host reads EmptyHint right after LoadItems in the same build, and shows it also when LoadItems throws, so the hint
    // is set before anything that could throw and then to the real reason the list is empty.
    private IReadOnlyList<ListItem> LoadBrokerItems()
    {
        revokeArm = null;
        invitationArm = null;
        if (brokerCard is not null) brokerCard.EmptyHint = BrokerListUnreadable;
        BrokerSnapshot? view = shutDown || !settings.Enabled ? null : snapshot;
        List<BrokerRow> rows = view is null ? [] : BrokerRows(view);
        renderedBrokerRows = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        tickedBrokerRows.IntersectWith(renderedBrokerRows.Keys);
        if (brokerCard is not null)
            brokerCard.EmptyHint = shutDown || !settings.Enabled ? BrokerListDisabled : view is null ? BrokerListUnavailable : BrokerListEmpty;
        return rows.Select(row => new ListItem
        {
            Id = row.Id, Group = row.Group, Label = row.Label, Detail = row.Detail, Checked = tickedBrokerRows.Contains(row.Id)
        }).ToArray();
    }

    // Memory only: a tick never pairs, approves, revokes or saves anything. Any change of selection disarms a revoke.
    private void SetBrokerChecked(string id, bool value)
    {
        revokeArm = null;
        if (value && renderedBrokerRows.ContainsKey(id)) tickedBrokerRows.Add(id);
        else tickedBrokerRows.Remove(id);
    }

    private void ResetBrokerList()
    {
        renderedBrokerRows = new Dictionary<string, BrokerRow>(StringComparer.Ordinal);
        tickedBrokerRows.Clear();
        revokeArm = null;
        invitationArm = null;
    }

    private string? BrokerActionUnavailable() =>
        shutDown || !settings.Enabled || dataDirectory is null || run is null || snapshot is null ? BrokerListUnavailable : null;

    // Exactly one ticked row, rendered in the named group. A group header's box can tick every row; that refuses too.
    private BrokerRow? TickedBrokerRow(string group, out string refusal)
    {
        string[] ticked = tickedBrokerRows.Where(renderedBrokerRows.ContainsKey).ToArray();
        refusal = ticked.Length == 0 ? "Tick one broker under " + group + " first."
            : ticked.Length > 1 ? "Tick exactly one broker. " + ticked.Length + " are ticked." : "";
        if (ticked.Length != 1) return null;
        BrokerRow row = renderedBrokerRows[ticked[0]];
        if (row.Group == group) return row;
        refusal = "This action needs a broker under " + group + ".";
        return null;
    }

    // One list action at a time. The host disables only the button that was pressed, so a second button (or the same
    // subject from another row) waits for the first to finish rather than racing it. The reset comes first, so a press
    // refused here never carries a reload left by an earlier press of the same button.
    private async Task<string> RunBrokerActionAsync(PaneAction pressed, Func<PaneAction, Task<string>> action)
    {
        pressed.ReloadPaneAfter = false;
        if (pressed.Label != PairCopiedInvitationLabel) invitationArm = null;
        if (brokerActionRunning) return "Another broker action is still running. Wait for its result, then try again.";
        brokerActionRunning = true;
        try { return await action(pressed); }
        finally { brokerActionRunning = false; }
    }

    private const string BrokerActionRestarted = "The broker restarted during that action. Press Refresh brokers to see the current state.";

    // Asks the worker for status now; the reload then rebuilds the pane, which re-reads the list.
    private async Task<string> RefreshBrokersAsync(PaneAction pressed)
    {
        if (shutDown || !settings.Enabled) return "Enable AgentBroker to list brokers.";
        ControlResponse? response = await SendAsync(new("status"));
        pressed.ReloadPaneAfter = true;
        return response is { Success: true, Snapshot: not null } ? "Broker list refreshed." : "The broker is unavailable. Try again after it reconnects.";
    }

    private async Task<string> RequestPairingFromListAsync(PaneAction pressed)
    {
        if (BrokerActionUnavailable() is { } unavailable) return unavailable;
        if (TickedBrokerRow(DiscoveredGroup, out string refusal) is not { } row) return refusal;
        // The exact rendered record, certificate pin included, must still be discovered now.
        if (!snapshot!.Discovered.Contains(row.Peer)) return BrokerListChanged;
        int current = generation;
        ControlResponse? response = await SendAsync(new("pair-request", Peer: row.Peer));
        pressed.ReloadPaneAfter = true;
        if (shutDown || generation != current) return BrokerActionRestarted;
        if (IsTrustedPairingPeer(PairingIdentity(row.Peer))) return PairingDisplayText();
        return response?.Success == true
            ? "Pairing requested with " + BrokerRowName(row.Peer) + ". Compare the full code under Awaiting approval on both workstations, then approve on both. The request alone does not share sessions."
            : pairingStatus.Length != 0 ? pairingStatus : "The broker could not request pairing.";
    }

    private async Task<string> ApprovePairingFromListAsync(PaneAction pressed)
    {
        if (BrokerActionUnavailable() is { } unavailable) return unavailable;
        if (TickedBrokerRow(AwaitingGroup, out string refusal) is not { Offer: { } shown } row) return refusal;
        // The whole rendered offer: same request, same complete code, same peer and certificate, still unexpired.
        PairingView? offer = snapshot!.PairingOffers.FirstOrDefault(candidate => candidate.RequestId == shown.RequestId);
        if (offer is null || offer.VerificationCode != shown.VerificationCode || offer.Peer != shown.Peer)
            return "This offer changed or ended after the list was shown. Press Refresh brokers and compare the new code.";
        if (offer.ExpiresAt <= McpNow()) return "This offer expired. Request pairing again, then compare the new code.";
        int current = generation;
        ControlResponse? response = await SendAsync(new("pair-approve", offer.RequestId));
        pressed.ReloadPaneAfter = true;
        if (shutDown || generation != current) return BrokerActionRestarted;
        string approved = "Approved code " + Safe(offer.VerificationCode, 35) + " for " + BrokerRowName(offer.Peer) + ". ";
        // The workstation that approves second gets a snapshot that already lists the broker as paired; its result still
        // states the scope it just granted (review LP-SAFE-2).
        if (IsTrustedPairingPeer(PairingIdentity(offer.Peer))) return approved + PairingCompleteSentence + " " + PairingScope;
        return response?.Success == true
            ? approved + PairingScopeSentence
            : pairingStatus.Length != 0 ? pairingStatus : "The broker could not send this approval.";
    }

    // The scope consent at the decision point: in the live Pairing row while an offer waits, and in the approve result.
    internal const string PairingScope = "Pairing shares current and future discoverable sessions; private sessions stay hidden and muted sessions cannot receive.";
    internal const string PairingScopeSentence = "Sharing starts when both workstations approve the same code. " + PairingScope;
    internal const string PairingCompleteSentence = "Both workstations have approved, so sharing has started.";

    private async Task<string> CancelPairingFromListAsync(PaneAction pressed)
    {
        if (BrokerActionUnavailable() is { } unavailable) return unavailable;
        if (TickedBrokerRow(AwaitingGroup, out string refusal) is not { Offer: { } shown }) return refusal;
        PairingView? offer = snapshot!.PairingOffers.FirstOrDefault(candidate => candidate.RequestId == shown.RequestId);
        if (offer is null || offer.Peer != shown.Peer) return "This offer already ended. Press Refresh brokers to see the current state.";
        int current = generation;
        ControlResponse? response = await SendAsync(new("pair-reject", offer.RequestId));
        pressed.ReloadPaneAfter = true;
        if (shutDown || generation != current) return BrokerActionRestarted;
        if (response?.Success != true) return "The broker could not cancel this pairing. Press Refresh brokers and check it again.";
        // Cancelling an offer never removes saved trust: a broker that is already paired (under this or another certificate)
        // can still have a fresh offer waiting, and pair-reject removes only the offer (review FU-2). So the result is
        // trust-neutral and never claims that nothing is shared.
        bool alreadyPaired = snapshot?.Paired.Any(peer => peer.BrokerId == offer.Peer.BrokerId) == true;
        return "Pairing request cancelled for " + BrokerRowName(offer.Peer) + ". " +
            (alreadyPaired ? PairingCancelKeepsTrust : "No pairing was created.");
    }

    internal const string PairingCancelKeepsTrust = "Its existing pairing is unchanged; use Revoke sharing to stop sharing with it.";

    // Two presses on the same Paired row within 15 seconds. The first only arms and asks for no reload (the reload's render
    // would disarm it); a press that sent a revoke asks for one. A clock stepped back to before the arm time re-arms; a step
    // back that stays inside the 15-second window is not detected and can stretch it, but a second explicit press on the
    // same row is still required (review FU-4).
    private async Task<string> RevokeFromListAsync(PaneAction pressed)
    {
        if (BrokerActionUnavailable() is { } unavailable) { revokeArm = null; return unavailable; }
        if (TickedBrokerRow(PairedGroup, out string refusal) is not { } row) { revokeArm = null; return refusal; }
        PairingPeerIdentity? identity = PairingIdentity(row.Peer);
        if (snapshot!.Paired.All(peer => PairingIdentity(peer) != identity))
        { revokeArm = null; return "This broker is no longer paired. Press Refresh brokers to see the current state."; }
        DateTimeOffset now = McpNow();
        if (revokeArm is not { } arm || arm.RowId != row.Id || now > arm.Deadline || now < arm.Deadline - RevokeConfirmWindow)
        {
            revokeArm = (row.Id, now + RevokeConfirmWindow);
            return "Press Revoke sharing again within 15 seconds to remove the saved trust for " + BrokerRowName(row.Peer) + ". Sharing with it stops.";
        }
        revokeArm = null;
        int current = generation;
        ControlResponse? response = await SendAsync(new("peer-revoke", row.Peer.BrokerId));
        pressed.ReloadPaneAfter = true;
        if (shutDown || generation != current) return BrokerActionRestarted;
        return response?.Success == true ? "Sharing revoked for " + BrokerRowName(row.Peer) + ". Pair again to share with it."
            : "The broker could not revoke sharing. Press Refresh brokers and check it again.";
    }
}
