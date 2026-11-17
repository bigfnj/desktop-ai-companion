using System.Globalization;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    // PANE-POPUP (1.10.5, N-broker-pairing-popups-01). Discovery and pairing offers raise no question any more: the
    // "Another broker discovered" card and the "Share with" offer card are gone, with their prompt state. A discovered
    // broker shows only in the Brokers list. An incoming offer is a passive signal: the tray root reads "AgentBroker:
    // pairing request waiting" when the menu opens, and the live Pairing row names the requester. These are pure functions
    // of the current snapshot and clock, so nothing here can send a control request,
    // and no episode cache exists to keep in step. Approval stays an explicit press on one offer, in the pane or the tray,
    // after the owner compares the same complete code on both workstations.
    // Every unexpired offer with a usable identity, in the Brokers list's order.
    private PairingView[] WaitingOffers()
    {
        BrokerSnapshot? view = snapshot;
        if (view is null || !settings.Enabled) return [];
        DateTimeOffset now = McpNow();
        return view.PairingOffers.Where(offer => offer.ExpiresAt > now &&
            PairingIdentity(offer.Peer) is not null).OrderBy(offer => offer.ExpiresAt).ThenBy(offer => offer.RequestId).ToArray();
    }

    // An incoming request this workstation has not approved yet: the only offer that raises the passive signals.
    private PairingView[] IncomingOffers() => WaitingOffers().Where(offer => !offer.Outbound && !offer.LocalApproved).ToArray();

    // "Name (1a2b3c4d, until 14:32)", with the certificate hint where the broker ID is paired under another certificate.
    private string OfferNames(PairingView[] offers)
    {
        string names = string.Join(", ", offers.Take(4).Select(offer => SafeBrokerName(offer.Peer) + " (" + ShortBrokerId(offer.Peer.BrokerId) +
            ", until " + offer.ExpiresAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) +
            (CertificateDiffers(snapshot, offer.Peer) ? ", " + CertificateDiffersHint : "") + ")"));
        return offers.Length > 4 ? names + " and " + (offers.Length - 4) + " more" : names;
    }

    // The live Pairing row's offer lines: who is waiting, in which direction and until when (the list's Detail holds only
    // the complete code), and the sharing scope, so it is visible on both workstations before either presses approve
    // (review LP-SAFE-2). The outgoing and approved-here lines never say "Pairing request waiting", which is the incoming
    // signal alone.
    private string PendingOfferText()
    {
        PairingView[] waiting = WaitingOffers();
        if (waiting.Length == 0) return "";
        PairingView[] incoming = waiting.Where(offer => !offer.Outbound && !offer.LocalApproved).ToArray();
        PairingView[] outgoing = waiting.Where(offer => offer.Outbound && !offer.LocalApproved).ToArray();
        PairingView[] approved = waiting.Where(offer => offer.LocalApproved).ToArray();
        var parts = new List<string>();
        if (incoming.Length != 0)
            parts.Add((incoming.Length == 1 ? "Pairing request waiting from " : incoming.Length + " pairing requests waiting, from ") + OfferNames(incoming) + ".");
        if (outgoing.Length != 0) parts.Add("Your pairing request to " + OfferNames(outgoing) + " also needs approval here.");
        if (approved.Length != 0) parts.Add("Approved here; waiting for " + OfferNames(approved) + " to approve.");
        if (incoming.Length + outgoing.Length != 0)
            parts.Add("Press Refresh brokers, compare the full code under Awaiting approval on both workstations, then press Codes match: approve on both.");
        if (waiting.Any(offer => CertificateDiffers(snapshot, offer.Peer)))
            parts.Add("A request whose " + CertificateDiffersHint + " cannot complete while that pairing is saved.");
        parts.Add(PairingScopeSentence);
        return string.Join(" ", parts);
    }

    internal const string TrayRootWaitingLabel = "AgentBroker: pairing request waiting";

    // BSM-4 (1.10.5): mail waiting past the notice threshold for a busy session adds its own passive part, after the pairing
    // signal and never in place of it.
    private string TrayRootLabel()
    {
        string root = !shutDown && IncomingOffers().Length != 0 ? TrayRootWaitingLabel : "AgentBroker";
        return WaitingMailTrayLabel(root, WaitingMailSignalSessions());
    }
}
