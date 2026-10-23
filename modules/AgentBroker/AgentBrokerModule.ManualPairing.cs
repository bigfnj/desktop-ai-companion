using AgentBroker.Protocol;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private Task<string> CopyInvitationAsync()
    {
        var view = snapshot;
        if (shutDown || !settings.Enabled || view?.LanEndpoint is null || view.CertificateSha256 is null)
            return Task.FromResult("Enable LAN and wait for the broker to start first.");
        try
        {
            var invitation = new PeerView(view.BrokerId, view.Name, view.LanEndpoint, view.CertificateSha256);
            System.Windows.Forms.Clipboard.SetText(JsonSerializer.Serialize(invitation, ControlProtocol.Json));
            return Task.FromResult("Public broker invitation copied. Paste it into AgentBroker on the other workstation, then compare pairing codes. This contains no private key or access token.");
        }
        catch (Exception) { return Task.FromResult("The clipboard is unavailable. Automatic discovery remains available from the tray."); }
    }

    private async Task<string> PairInvitationAsync(IReadOnlyDictionary<string, string> values)
    {
        if (shutDown || !settings.Enabled || !settings.EnableLan || snapshot is null)
            return "Enable LAN and wait for the broker to start first.";
        if (!values.TryGetValue("manualPeer", out string? json) || string.IsNullOrWhiteSpace(json) || json.Length > 4096)
            return "Paste the other broker's invitation first. Use this when automatic discovery is unavailable.";
        PeerView? peer;
        try { peer = JsonSerializer.Deserialize<PeerView>(json, ControlProtocol.Json); }
        catch (JsonException) { return "This broker invitation is invalid."; }
        if (peer is null || peer.BrokerId == Guid.Empty || peer.BrokerId == snapshot.BrokerId ||
            peer.Name is null || peer.Name.Length > 80 || peer.Endpoint is null || peer.Endpoint.Length > 512 ||
            peer.CertificateSha256 is null || peer.CertificateSha256.Length != 64 || !peer.CertificateSha256.All(char.IsAsciiHexDigit))
            return "This broker invitation is invalid.";
        ControlResponse? response = await SendAsync(new("pair-request", Peer: peer));
        if (IsTrustedPairingPeer(PairingIdentity(peer))) return PairingDisplayText();
        if (response?.Code == "pairing_in_progress") return pairingStatus;
        return response?.Success == true
            ? "Pairing requested. Compare the complete code, then approve on both workstations to share current and future discoverable sessions. The request alone does not share sessions."
            : "The broker could not request pairing. " + pairingStatus;
    }
}
