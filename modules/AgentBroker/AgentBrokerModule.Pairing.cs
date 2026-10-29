using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private string pairingStatus = "";
    private bool pairingFailed;
    private string pairingActivity = "";
    private PairingPeerIdentity? pairingActivityPeer;
    private string? unscopedPairingFailure;
    private readonly Dictionary<PairingPeerIdentity, string> pairingFailures = [];
    private readonly Dictionary<Guid, CancellationTokenSource> pairingQuestions = [];

    private readonly record struct PairingPeerIdentity(Guid BrokerId, string CertificateSha256);

    private static PairingPeerIdentity? PairingIdentity(PeerView? peer) => peer is not null && peer.BrokerId != Guid.Empty &&
        peer.CertificateSha256 is { Length: 64 } certificate && certificate.All(char.IsAsciiHexDigit)
            ? new(peer.BrokerId, certificate.ToUpperInvariant()) : null;

    private static PairingPeerIdentity? PairingSubject(ControlRequest request, BrokerSnapshot? view) =>
        PairingIdentity(request.Operation == "pair-request" ? request.Peer :
            view?.PairingOffers.FirstOrDefault(offer => offer.RequestId == request.Id)?.Peer);

    private static bool HasTrustedPairingPeer(BrokerSnapshot? view, PairingPeerIdentity? peer) => peer is not null &&
        view?.Paired.Any(paired => PairingIdentity(paired) == peer) == true;

    private bool IsTrustedPairingPeer(PairingPeerIdentity? peer) => HasTrustedPairingPeer(snapshot, peer);

    private string PairedTrustSummary()
    {
        PeerView[] paired = snapshot?.Paired.Where(peer => PairingIdentity(peer) is not null).ToArray() ?? [];
        if (paired.Length == 0) return "";
        string names = string.Join(", ", paired.OrderBy(peer => Safe(peer.Name, 48), StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.BrokerId).Take(4).Select(peer =>
            string.IsNullOrWhiteSpace(Safe(peer.Name, 48)) ? "broker " + peer.BrokerId.ToString("N")[..8] : Safe(peer.Name, 48)));
        if (paired.Length > 4) names += " and " + (paired.Length - 4) + " more";
        return "Paired with " + names + ". Trust is saved on this PC; current reachability is not verified.";
    }

    private string PairingDisplayText()
    {
        string paired = PairedTrustSummary();
        if (paired.Length != 0) return paired + (pairingStatus.Length == 0 ? "" : " " + pairingStatus);
        return pairingStatus.Length != 0 ? pairingStatus : "No pairing attempt yet.";
    }

    private string PairedBrokersDisplay()
    {
        if (snapshot is null) return settings.Enabled
            ? "Paired broker status is unavailable. Wait for the worker or refresh status."
            : "Enable AgentBroker to view paired brokers.";
        if (snapshot.Paired.Length == 0) return "No paired brokers. Pairing saves trust after both workstations approve.";
        string Name(PeerView peer)
        {
            string name = Safe(peer.Name, 80);
            return string.IsNullOrWhiteSpace(name) ? "Broker " + peer.BrokerId.ToString("N")[..8] : name;
        }
        string Endpoint(PeerView peer) => Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 &&
            endpoint.Fragment.Length == 0 && endpoint.AbsolutePath == "/"
                ? Safe(endpoint.AbsoluteUri, 512) : "endpoint unavailable";
        IEnumerable<string> lines = snapshot.Paired.OrderBy(Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.BrokerId).ThenBy(peer => peer.CertificateSha256, StringComparer.OrdinalIgnoreCase)
            .Take(32).Select(peer => Name(peer) + " | " + Endpoint(peer));
        string result = "Saved trust on this PC; current reachability is unknown.\n" + string.Join("\n", lines);
        return snapshot.Paired.Length > 32 ? result + "\nShowing 32 of " + snapshot.Paired.Length + " saved brokers." : result;
    }

    private void ResetPairingStatus()
    {
        pairingStatus = ""; pairingFailed = false; pairingActivity = ""; pairingActivityPeer = null;
        unscopedPairingFailure = null; pairingFailures.Clear();
    }

    private void ReconcilePairingStatus()
    {
        foreach (var peer in pairingFailures.Keys.Where(peer => IsTrustedPairingPeer(peer)).ToArray())
            pairingFailures.Remove(peer);
        if (IsTrustedPairingPeer(pairingActivityPeer)) { pairingActivity = ""; pairingActivityPeer = null; }
        RefreshPairingStatus();
    }

    private void RefreshPairingStatus()
    {
        pairingFailed = pairingFailures.Count != 0 || unscopedPairingFailure is not null;
        IEnumerable<string> errors = pairingFailures.Values;
        if (unscopedPairingFailure is not null) errors = errors.Append(unscopedPairingFailure);
        pairingStatus = string.Join(" ", (pairingActivity.Length == 0 ? errors : errors.Prepend(pairingActivity)).Distinct(StringComparer.Ordinal));
        NotifyInfoChanged();
    }

    private void CancelObsoletePairingQuestions(PairingView[]? offers)
    {
        foreach (var entry in pairingQuestions.ToArray())
        {
            if (offers?.Any(o => o.RequestId == entry.Key && !o.LocalApproved && o.ExpiresAt > DateTimeOffset.UtcNow) == true) continue;
            pairingQuestions.Remove(entry.Key);
            entry.Value.Cancel();
            // AskPairingAsync owns disposal after the question service has finished.
        }
    }

    private string DisplayedStatus() => status + (settings.Enabled ? " " + McpDisplay().Summary : "") +
        (pairingFailed ? " " + pairingStatus : "") + (settings.Enabled ? NoticeDropText(snapshot?.DroppedNotices) : "");

    // N-agentbroker-notice-loss-signal-01 (1.5.0): a delivery notice that could not be admitted before its message's
    // record was pruned is dropped. Status counts them since the worker started, by why they were refused, and names no
    // message or session. Empty while none was dropped.
    internal static string NoticeDropText(NoticeDropView? drops)
    {
        if (drops is null) return "";
        int total = drops.SenderUnavailable + drops.StoreFull + drops.QueueFull;
        if (total <= 0) return "";
        var reasons = new List<string>();
        if (drops.SenderUnavailable > 0) reasons.Add(drops.SenderUnavailable + " while the sender was away");
        if (drops.StoreFull > 0) reasons.Add(drops.StoreFull + " while the store was full");
        if (drops.QueueFull > 0) reasons.Add(drops.QueueFull + " while the sender's queue was full");
        return " Since the broker started, " + total + (total == 1 ? " delivery notice was" : " delivery notices were") +
            " dropped undelivered (" + string.Join(", ", reasons) + ").";
    }

    private void SetPairingStatus(string value, bool failed, PairingPeerIdentity? peer = null, bool clearFailure = false)
    {
        if (IsTrustedPairingPeer(peer))
        {
            pairingFailures.Remove(peer!.Value);
            if (pairingActivityPeer == peer) { pairingActivity = ""; pairingActivityPeer = null; }
        }
        else if (failed)
        {
            if (peer is { } identity) pairingFailures[identity] = value;
            else unscopedPairingFailure = value;
            if (pairingActivityPeer == peer) { pairingActivity = ""; pairingActivityPeer = null; }
        }
        else
        {
            pairingActivity = value; pairingActivityPeer = peer;
            if (clearFailure)
            {
                if (peer is { } identity) pairingFailures.Remove(identity);
            }
        }
        RefreshPairingStatus();
    }

    private void ReportPairingResult(ControlRequest request, ControlResponse response, PairingPeerIdentity? peer)
    {
        // The current persisted trust record outranks a late command result. A failed
        // exchange can arrive after the peer's approved callback already finalized trust.
        if (IsTrustedPairingPeer(peer)) { ReconcilePairingStatus(); return; }
        if (!response.Success)
        {
            SetPairingStatus(PairingFailureText(response.Code), response.Code != "pairing_in_progress", peer,
                clearFailure: response.Code == "pairing_in_progress");
            return;
        }
        SetPairingStatus(request.Operation == "pair-request"
            ? "Pairing requested. Compare the complete code on both workstations, then approve on both. The request alone does not share sessions."
            : snapshot?.PairingOffers.Any(offer => offer.RequestId == request.Id) == true
                ? "This workstation sent its confirmation. Both workstations must approve the same complete code; check pending or paired brokers for current state."
                : "Confirmation received. Check Paired brokers for the completed connection.", false, peer, clearFailure: true);
    }

    private static string PairingFailureText(string code) => code switch
    {
        "pairing_in_progress" =>
            "Both brokers requested pairing together. Compare the current incoming offer's complete code on both workstations; a superseded code grants no access. Do not request another offer.",
        "pairing_network_timeout" or "pairing_connection_failed" or "pairing_connection_refused" =>
            "Pairing could not reach the other broker's LAN HTTPS port. Check the invitation address and its TCP port (default 7433) on both workstations. Discovery alone does not prove that connection.",
        "pairing_tls_failed" or "pairing_identity_refused" =>
            "Pairing refused the TLS identity. Copy a current invitation and verify both brokers' complete comparison code. Never bypass the certificate check.",
        "pairing_peer_refused" =>
            "The other broker refused this pairing identity or confirmation. Check the current invitation and pending code on both workstations.",
        "pairing_offer_invalid" =>
            "The invitation or pairing offer was rejected or expired. Check current pairing state before requesting a new offer.",
        "pairing_outcome_unknown" =>
            "Pairing did not report a result before its deadline. Refresh status and check pending or paired brokers before retrying; the outcome is unconfirmed.",
        "pairing_control_unavailable" =>
            "This companion could not reach its local broker to confirm the pairing result. Refresh status before retrying.",
        _ => "The broker could not complete that pairing action. Check pending or paired brokers and LAN HTTPS reachability before retrying."
    };
}
