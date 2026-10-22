using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private string pairingStatus = "";
    private bool pairingFailed;
    private readonly Dictionary<Guid, CancellationTokenSource> pairingQuestions = [];

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
        (pairingFailed ? " " + pairingStatus : "");

    private void SetPairingStatus(string value, bool failed)
    {
        pairingStatus = value;
        pairingFailed = failed;
        NotifyInfoChanged();
    }

    private void ReportPairingResult(ControlRequest request, ControlResponse response)
    {
        if (!response.Success)
        {
            SetPairingStatus(PairingFailureText(response.Code), response.Code != "pairing_in_progress");
            return;
        }
        SetPairingStatus(request.Operation == "pair-request"
            ? "Pairing requested. Compare the complete code on both workstations, then approve on both. The request alone does not share sessions."
            : snapshot?.PairingOffers.Any(offer => offer.RequestId == request.Id) == true
                ? "This workstation sent its confirmation. Both workstations must approve the same complete code; check pending or paired brokers for current state."
                : "Confirmation received. Check Paired brokers for the completed connection.", false);
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
