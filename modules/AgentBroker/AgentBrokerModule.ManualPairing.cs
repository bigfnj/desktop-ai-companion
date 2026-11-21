using AgentBroker.Protocol;
using DesktopAICompanion.Modules;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static readonly TimeSpan InvitationConfirmWindow = TimeSpan.FromSeconds(30);
    private const string InvitationCopyPendingResult = "Clipboard copy did not finish; it may still complete. Discovery remains available in the Brokers list.";
    // Only a write still queued on the UI thread may complete later. A definite failure (no UI context, a writer or Post that
    // threw, an invitation that could not be built) says so instead (DECISIONS 67 as amended by DECISIONS 111, U-m25).
    private const string InvitationCopyFailedResult = "Clipboard copy failed. Discovery remains available in the Brokers list.";
    private (PeerView Peer, DateTimeOffset Deadline, int Generation, Guid BrokerId)? invitationArm;
    private Func<string> readInvitationClipboard = static () => System.Windows.Forms.Clipboard.GetText();
    private Action<string> writeInvitationClipboard = static text => System.Windows.Forms.Clipboard.SetText(text);

    private async Task<string> CopyInvitationAsync()
    {
        var view = snapshot;
        if (shutDown || !settings.Enabled || view?.LanEndpoint is null || view.CertificateSha256 is null)
            return "Enable LAN and wait for the broker to start first.";
        try
        {
            var invitation = new PeerView(view.BrokerId, view.Name, view.LanEndpoint, view.CertificateSha256);
            bool? written = await WriteInvitationClipboardAsync(JsonSerializer.Serialize(invitation, ControlProtocol.Json));
            if (written is null) return InvitationCopyPendingResult;
            if (written == false) return InvitationCopyFailedResult;
            return "Public broker invitation copied. On the other workstation, use Pair from copied invitation in Brokers and confirm its preview, then compare pairing codes. This contains no private key or access token.";
        }
        catch (Exception) { return InvitationCopyFailedResult; }
    }

    // true: written. false: definitely not written. null: the write is still queued on the UI thread after 2 seconds (or the
    // module is stopping) and may yet run, the one uncertain outcome.
    private async Task<bool?> WriteInvitationClipboardAsync(string text)
    {
        if (ui is null) return false;
        Task<bool>? queued = null;
        try
        {
            if (ReferenceEquals(SynchronizationContext.Current, ui)) { writeInvitationClipboard(text); return true; }
            var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ =>
            {
                try { writeInvitationClipboard(text); copied.TrySetResult(true); }
                catch (Exception error) { copied.TrySetException(error); }
            }, null);
            queued = copied.Task;
            return await queued.WaitAsync(TimeSpan.FromSeconds(2), run?.Token ?? CancellationToken.None);
        }
        // Decided from the queued write's own state, read ONCE after the wait ended (U round 7, U6-X3-1): a write that lands
        // between the timeout and this read reports what happened, never a failure it did not have.
        catch (Exception) when (queued is not null)
        {
            AfterInvitationCopyWaitForTest?.Invoke();
            return QueuedCopyOutcome(queued.Status);
        }
        catch (Exception) { return false; }
    }

    // U round 7 (U6-X3-1): one snapshot of the queued write's state. The round 6 code read IsCompletedSuccessfully and then
    // IsCompleted; a write that succeeded between the two reads was reported as a failure. Completed = written; faulted or
    // cancelled = definitely not written; anything still pending may yet run, so it stays the uncertain outcome.
    internal static bool? QueuedCopyOutcome(TaskStatus status) => status switch
    {
        TaskStatus.RanToCompletion => true,
        TaskStatus.Faulted or TaskStatus.Canceled => false,
        _ => null,
    };

    // Self-test seam (U round 7), null outside the self-test: runs after the wait for a posted write ended and before its state
    // is read, which is exactly where a write that completes late lands.
    internal Action? AfterInvitationCopyWaitForTest { get; set; }

    private static string StripDisplayIdentity(string? text) => string.Concat((text ?? "").EnumerateRunes()
        .Where(rune => Rune.GetUnicodeCategory(rune) is not
            (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        .Select(rune => rune.ToString()));

    private static string InvitationPreviewName(PeerView peer) => SafeBrokerName(peer) + " (" + ShortBrokerId(peer.BrokerId) + ")";

    private static string InvitationPreviewAuthority(PeerView peer)
    {
        var endpoint = new Uri(peer.Endpoint);
        string host = endpoint.IdnHost;
        if (endpoint.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[')) host = "[" + host + "]";
        string authority = host + (endpoint.IsDefaultPort ? "" : ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture));
        return Safe(StripDisplayIdentity(authority), 512);
    }

    private static bool InvitationHostIsVisible(Uri endpoint)
    {
        string host;
        try { host = endpoint.IdnHost; }
        catch (UriFormatException) { return false; }
        if (host.Any(character => character > 127)) return false;
        string scopedHost = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        int zoneAt = scopedHost.IndexOf('%');
        if (endpoint.HostNameType == UriHostNameType.IPv6 && zoneAt >= 0)
        {
            // Uri.IdnHost preserves scope text. Accept ordinary numeric/interface identifiers, never display syntax.
            string zone = scopedHost[(zoneAt + 1)..];
            if (zone.StartsWith("25", StringComparison.Ordinal)) zone = zone[2..];
            if (zone.Length == 0 || !zone.All(char.IsAsciiLetterOrDigit)) return false;
        }
        return true;
    }

    private async Task<string?> ReadInvitationClipboardAsync()
    {
        if (ui is null) return null;
        try
        {
            if (ReferenceEquals(SynchronizationContext.Current, ui)) return readInvitationClipboard();
            var copied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ =>
            {
                try { copied.TrySetResult(readInvitationClipboard()); }
                catch (Exception error) { copied.TrySetException(error); }
            }, null);
            return await copied.Task.WaitAsync(TimeSpan.FromSeconds(2), run?.Token ?? CancellationToken.None);
        }
        catch (Exception) { return null; }
    }

    private static bool TryReadInvitation(string? json, out PeerView? peer)
    {
        peer = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try { peer = JsonSerializer.Deserialize<PeerView>(json, ControlProtocol.Json); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return false; }
        if (peer is null || peer.BrokerId == Guid.Empty || peer.Name is null || peer.Name.Length > 80 ||
            peer.Endpoint is null || peer.Endpoint.Length > 512 || peer.CertificateSha256 is null ||
            peer.CertificateSha256.Length != 64 || !peer.CertificateSha256.All(char.IsAsciiHexDigit)) return false;
        return Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps &&
            endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 &&
            endpoint.AbsolutePath == "/" && endpoint.Host.Length != 0 && InvitationHostIsVisible(endpoint);
    }

    private async Task<string> PairCopiedInvitationAsync(PaneAction pressed)
    {
        string? unavailable = BrokerActionUnavailable();
        if (unavailable is not null || !settings.EnableLan)
        {
            invitationArm = null;
            return unavailable ?? "Enable LAN and wait for the broker to start first.";
        }
        int current = generation;
        Guid localBrokerId = snapshot!.BrokerId;
        string? json = await ReadInvitationClipboardAsync();
        if (shutDown || generation != current) { invitationArm = null; return BrokerActionRestarted; }
        if (BrokerActionUnavailable() is { } noLongerAvailable)
        { invitationArm = null; return noLongerAvailable; }
        if (!settings.EnableLan || snapshot!.BrokerId != localBrokerId)
        {
            invitationArm = null;
            return !settings.EnableLan ? "Enable LAN and wait for the broker to start first." : BrokerActionRestarted;
        }
        if (!TryReadInvitation(json, out PeerView? peer))
        {
            invitationArm = null;
            return json is null ? "The clipboard is unavailable. Copy an invitation again or use discovery in Brokers."
                : "Copy a valid broker invitation first. Its contents are not shown here.";
        }
        if (peer!.BrokerId == snapshot!.BrokerId)
        {
            invitationArm = null;
            return "This is this broker's own invitation. Copy one from the other workstation.";
        }
        if (CertificateDiffers(snapshot, peer!))
        {
            invitationArm = null;
            return "That broker is already paired under a different certificate. Revoke sharing first.";
        }
        if (IsTrustedPairingPeer(PairingIdentity(peer)))
        {
            invitationArm = null;
            return "That broker identity is already paired. See Paired in the Brokers list.";
        }
        DateTimeOffset now = McpNow();
        if (invitationArm is not { } arm || arm.Peer != peer || arm.Generation != current || arm.BrokerId != localBrokerId ||
            now > arm.Deadline || now < arm.Deadline - InvitationConfirmWindow)
        {
            string authority;
            try { authority = InvitationPreviewAuthority(peer); }
            catch (UriFormatException) { invitationArm = null; return "This broker invitation is invalid."; }
            invitationArm = (peer, now + InvitationConfirmWindow, current, localBrokerId);
            return "Invitation from " + InvitationPreviewName(peer) + " at " + authority +
                ". Review this identity, then press Pair from copied invitation again within " +
                InvitationConfirmWindow.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
                " seconds to request pairing. No request has been sent.";
        }
        invitationArm = null;
        pressed.ReloadPaneAfter = true;
        string result = await PairInvitationAsync(new Dictionary<string, string>
        {
            ["manualPeer"] = JsonSerializer.Serialize(peer, ControlProtocol.Json)
        });
        return shutDown || generation != current ? BrokerActionRestarted : result;
    }

    private async Task<string> PairInvitationAsync(IReadOnlyDictionary<string, string> values)
    {
        if (shutDown || !settings.Enabled || !settings.EnableLan || snapshot is null)
            return "Enable LAN and wait for the broker to start first.";
        if (!values.TryGetValue("manualPeer", out string? json) || string.IsNullOrWhiteSpace(json) || json.Length > 4096)
            return "Copy a valid broker invitation first.";
        if (!TryReadInvitation(json, out PeerView? peer) || peer!.BrokerId == snapshot.BrokerId)
            return "This broker invitation is invalid.";
        ControlResponse? response = await SendAsync(new("pair-request", Peer: peer));
        if (IsTrustedPairingPeer(PairingIdentity(peer))) return PairingDisplayText();
        if (response?.Code == "pairing_in_progress") return pairingStatus;
        return response?.Success == true
            ? "Pairing requested. Compare the complete code, then approve on both workstations to share current and future discoverable sessions. The request alone does not share sessions."
            : "The broker could not request pairing. " + pairingStatus;
    }
}
