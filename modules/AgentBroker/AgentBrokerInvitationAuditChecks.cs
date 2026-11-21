using AgentBroker.Protocol;
using DesktopAICompanion.Modules;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunInvitationAuditChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        RunLegacyInvitationChecks(check);
        RunInvitationDisarmChecks(check, dispatcher);
        RunInvitationDisplayChecks(check, dispatcher);
        RunInvitationRefusalChecks(check, dispatcher);
        RunInvitationCopyChecks(check, dispatcher);
        RunPairingAvailabilityChecks(check, dispatcher);
    }

    private static string LegacyInvitationJson(PeerView peer) => "{\"brokerId\":\"" + peer.BrokerId.ToString("D") +
        "\",\"name\":\"Legacy1.10.4\",\"endpoint\":\"" + peer.Endpoint + "\",\"certificateSha256\":\"" + peer.CertificateSha256 + "\"}";

    private static void RunLegacyInvitationChecks(Action<bool, string> check)
    {
        // Frozen four-property camelCase output of 1.10.4, independently compared with today's serializer.
        var legacyPeer = new PeerView(new Guid("11111111-2222-4333-8444-555555555555"), "Legacy1.10.4",
            "https://10.0.0.1:7433/", new string('B', 64));
        string[] hosts = ["10.0.0.0", "10.255.255.255", "172.16.0.0", "172.31.255.255",
            "192.168.0.0", "192.168.255.255", "127.0.0.1"];
        foreach (string endpoint in hosts.SelectMany(address => new[] { 1, 443, 7433, 65535 }
            .Select(port => "https://" + address + ":" + port.ToString(CultureInfo.InvariantCulture) + "/")))
        {
            PeerView expected = legacyPeer with { Endpoint = endpoint };
            string legacy = LegacyInvitationJson(expected);
            check(legacy == JsonSerializer.Serialize(expected, ControlProtocol.Json) &&
                TryReadInvitation(legacy, out PeerView? parsed) && parsed == expected,
                "INVITATION_LEGACY_LAN_SHAPES: exact 1.10.4 JSON accepts every base address/port boundary: " + endpoint);
        }
        // Supplemental shapes are not produced by the base LAN profile; keep their distinct acceptance guard explicit.
        foreach (string endpoint in new[] { "https://[fe80::1%2512]:7433/", "https://[fe80::1%25eth0]:7433/", "https://[::1]:7433/" })
        {
            PeerView scoped = legacyPeer with { Endpoint = endpoint };
            check(TryReadInvitation(LegacyInvitationJson(scoped), out PeerView? parsed) && parsed == scoped,
                "INVITATION_LEGACY_LAN_SHAPES_SCOPED: scoped numeric/alphanumeric and unscoped IPv6 remain parseable");
        }
    }

    private static void RunInvitationDisarmChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            f.Arm();
            f.Control.Response = new(true, "ok", Snapshot: f.View);
            PaneAction refresh = f.Pane.Actions.Single(action => action.Label == "Refresh status");
            string result = refresh.InvokeAsync().GetAwaiter().GetResult();
            bool armedUntilHostRebuild = f.Module.invitationArm is not null;
            f.Module.LoadBrokerItems();
            check(armedUntilHostRebuild && refresh.ReloadPaneAfter && result == "Status refreshed." &&
                f.Module.invitationArm is null && PairingRequestCount(f.Control) == 0,
                "INVITATION_REFRESH_DISARMS: top-band Refresh preserves the arm until the actual host list rebuild clears it");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            f.Arm();
            PaneAction request = f.Module.BrokerAction(RequestPairingLabel, f.Module.RequestPairingFromListAsync);
            string refused = f.Press(request);
            check(refused.StartsWith("Tick one broker", StringComparison.Ordinal) && f.Module.invitationArm is null &&
                PairingRequestCount(f.Control) == 0 && !request.ReloadPaneAfter,
                "INVITATION_OTHER_ACTION_DISARMS: even a refused actual Request pairing action clears an existing arm");
        }
    }

    private static bool HasHiddenIdentityRunes(string text) => text.EnumerateRunes().Any(rune =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    private static string DisplayPreview(ClipboardPairingFixture f, PeerView peer)
    {
        f.Clipboard = JsonSerializer.Serialize(peer, ControlProtocol.Json);
        // Rendering has its own fixture: either of these independent reasons requires a preview.
        // A single changed-identity/deadline mutation cannot turn a renderer test into a confirmation test.
        f.Module.invitationArm = (f.Peer with { Name = "Different synthetic peer" }, f.Now.AddSeconds(-1),
            f.Module.generation, f.View.BrokerId);
        return f.Press();
    }

    private static void RunInvitationDisplayChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        const string label = "INVITATION_PREVIEW_VISIBLE_IDENTITY";
        foreach (string name in new[] { "Visible\u202E\u2066\U000E0001name", "Visible\u2028name", "Visible\u2029name" })
        {
            using var f = new ClipboardPairingFixture(dispatcher);
            PeerView peer = f.Peer with { Name = name };
            string preview = DisplayPreview(f, peer);
            check(preview.Contains("Visiblename (" + ShortBrokerId(peer.BrokerId) + ")", StringComparison.Ordinal) &&
                !HasHiddenIdentityRunes(preview) && PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is not null,
                label + ": preview names strip format, line-separator and paragraph-separator runes");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            PeerView peer = f.Peer with { Name = "Visible\u202E\u2028\u2029name", Endpoint = "https://раypal.example:7443/" };
            var offer = new PairingView(Guid.NewGuid(), peer, "123456-ABCDEF-123456-ABCDEF-123456", f.Now.AddMinutes(2), true);
            f.Apply(f.View with { PairingOffers = [offer], Discovered = [peer] });
            IReadOnlyList<ListItem> rows = f.Module.LoadBrokerItems();
            // Total conditions (the P-CHK-3 pattern of AgentBrokerSelfTest.cs's "Total conditions (review P-CHK-3)" comment;
            // lane G round 2, case 1664): with no Awaiting row these two checks fail by their own labels instead of throwing and
            // skipping the rest of the group.
            ListItem? shown = rows.SingleOrDefault(row => row.Group == AwaitingGroup);
            check(shown is not null && shown.Label == "Visiblename" && !HasHiddenIdentityRunes(shown.Label) &&
                f.Module.PairingDisplayText().Contains("Visiblename (" + ShortBrokerId(peer.BrokerId), StringComparison.Ordinal) &&
                !HasHiddenIdentityRunes(f.Module.PairingDisplayText()),
                label + ": Awaiting labels and outbound-offer Pairing prose use the same visible broker name");
            check(shown is not null && shown.Detail == offer.VerificationCode,
                "BROKER_DETAIL_FULL_CODE: name sanitizing preserves the complete separate comparison code");
            TrayItem[] tray = f.Module.BuildTray().ToArray();
            string offerLabel = tray.Single(item => item.Label.StartsWith("Pairing code ", StringComparison.Ordinal)).Label;
            string discoveredLabel = tray.Single(item => item.Label == "Pair a broker").BuildChildren().Single().Label;
            check(offerLabel.EndsWith(" for Visiblename", StringComparison.Ordinal) && !HasHiddenIdentityRunes(offerLabel),
                label + ": the tray comparison-code label uses the shared visible broker name");
            check(discoveredLabel == "Visiblename" && !HasHiddenIdentityRunes(discoveredLabel),
                label + ": the tray discovery action uses the shared visible broker name");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            PeerView peer = f.Peer with { Name = "Visible\u202E\u2028\u2029name" };
            f.Apply(f.View with { Paired = [peer] });
            check(f.Module.PairingDisplayText().Contains("Paired with Visiblename.", StringComparison.Ordinal) &&
                !HasHiddenIdentityRunes(f.Module.PairingDisplayText()),
                label + ": saved-trust Pairing prose also strips hidden identity runes");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            PeerView peer = f.Peer with { Endpoint = "https://раypal.example:7443/" };
            string preview = DisplayPreview(f, peer);
            string asciiHost = new Uri(peer.Endpoint).IdnHost;
            f.Apply(f.View with { Paired = [peer] });
            string detail = f.Module.LoadBrokerItems().Single(row => row.Group == PairedGroup).Detail;
            check(asciiHost.StartsWith("xn--", StringComparison.Ordinal) && preview.Contains(asciiHost + ":7443", StringComparison.Ordinal) &&
                detail == asciiHost + ":7443" && !preview.Contains("раypal.example", StringComparison.Ordinal) &&
                !detail.Contains("раypal.example", StringComparison.Ordinal),
                label + ": copied previews and existing Paired endpoint details display the same punycode authority");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            // Snapshot display is an independent boundary from clipboard admission: the whole authority is sanitized here.
            PeerView peer = f.Peer with { Endpoint = "https://[fe80::1%25link\u202Eid]:7433/" };
            f.Apply(f.View with { Paired = [peer] });
            string detail = f.Module.LoadBrokerItems().Single(row => row.Group == PairedGroup).Detail;
            check(detail.Contains("linkid", StringComparison.Ordinal) && !HasHiddenIdentityRunes(detail),
                label + ": the whole existing-row authority strips format runes, including IPv6 scope text");
        }
    }

    private static void RunInvitationRefusalChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        void RefusedEndpoint(string endpoint, string label)
        {
            using var f = new ClipboardPairingFixture(dispatcher);
            f.Arm();
            f.Clipboard = JsonSerializer.Serialize(f.Peer with { Endpoint = endpoint }, ControlProtocol.Json);
            string refused = f.Press();
            check(refused == "Copy a valid broker invitation first. Its contents are not shown here." &&
                f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter && PairingRequestCount(f.Control) == 0,
                label + ": unsupported authority text refuses without sending, echoing, or retaining the previous arm");
        }
        foreach (string zone in new[] { ".trusted-host.example", "eth-0", "eth_0", "x%25y", "x y", "x:y", "\u202Eevil", "x\u2028y", "x\u2029y" })
            RefusedEndpoint("https://[fe80::1%25" + zone + "]:7433/", "INVITATION_ZONE_ALNUM_ONLY");
        RefusedEndpoint("https://[fe80::1%25]:7433/", "INVITATION_ZONE_ALNUM_ONLY");
        RefusedEndpoint("https://раураl-ассоупт-ѕесуріту-септерассоупт.example:7433/", "INVITATION_ASCII_HOST_REQUIRED");
        RefusedEndpoint("https://x\u200Dy.example:7433/", "INVITATION_ASCII_HOST_REQUIRED");

        foreach (string malformed in new[]
        {
            "SYNTHETIC_PRIVATE_" + (char)0xDC00,
            "{\"brokerId\":\"11111111-2222-4333-8444-555555555555\",\"name\":\"SYNTHETIC_PRIVATE_" +
                (char)0xD800 + "\",\"endpoint\":\"https://10.0.0.1:7433/\",\"certificateSha256\":\"" + new string('B', 64) + "\"}"
        })
        {
            using var f = new ClipboardPairingFixture(dispatcher);
            f.Arm();
            f.Clipboard = malformed;
            string refused = f.Press();
            check(refused == "Copy a valid broker invitation first. Its contents are not shown here." &&
                f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter && PairingRequestCount(f.Control) == 0,
                "INVITATION_INVALID_UTF16_REFUSED: raw unpaired UTF16 is fixed refusal text and clears the previous arm");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            f.Apply(f.View with { Paired = [f.Peer with { CertificateSha256 = new string('A', 64) }] });
            f.Arm();
            string refused = f.Press();
            check(refused == "That broker is already paired under a different certificate. Revoke sharing first." &&
                f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter && PairingRequestCount(f.Control) == 0,
                "INVITATION_PAIRED_CERTIFICATE_CHANGED: a familiar broker ID with another certificate refuses before previewing");
        }
        using (var f = new ClipboardPairingFixture(dispatcher))
        {
            f.Apply(f.View with { Paired = [f.Peer with { CertificateSha256 = f.Peer.CertificateSha256.ToLowerInvariant() }] });
            f.Arm();
            check(f.Press() == "That broker identity is already paired. See Paired in the Brokers list." &&
                f.Module.invitationArm is null && PairingRequestCount(f.Control) == 0,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_PAIRED_REFUSED: certificate hex casing does not invent a different identity");
        }
    }

    private static void RunInvitationCopyChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        using var f = new ClipboardPairingFixture(dispatcher);
        int writeThread = 0, writes = 0;
        string? copied = null;
        f.Module.writeInvitationClipboard = text => { copied = text; writeThread = Environment.CurrentManagedThreadId; writes++; };
        PaneAction copy = f.Module.BrokerAction(CopyInvitationLabel, _ => f.Module.CopyInvitationAsync());
        Task<string> offThread = Task.Run(() => copy.InvokeAsync());
        Pump(() => offThread.IsCompleted, dispatcher);
        string result = offThread.GetAwaiter().GetResult();
        var expected = new PeerView(f.View.BrokerId, f.View.Name, f.View.LanEndpoint!, f.View.CertificateSha256!);
        check(writes == 1 && writeThread == f.UiThread && copied == JsonSerializer.Serialize(expected, ControlProtocol.Json) &&
            result.StartsWith("Public broker invitation copied.", StringComparison.Ordinal) && PairingRequestCount(f.Control) == 0,
            "INVITATION_COPY_UI_THREAD: the actual Copy factory/handler dispatches its synthetic writer to UI and preserves public legacy JSON");

        // Both fallback inputs (DECISIONS 111, U-m25). A definite failure: calling on the captured UI context isolates the
        // fallback wording from the dispatch guard, and the synthetic writer throws, so the copy definitely failed and the
        // result must not say it may still complete.
        using var failure = new ClipboardPairingFixture(dispatcher);
        int failedWrites = 0;
        failure.Module.writeInvitationClipboard = _ => { failedWrites++; throw new InvalidOperationException("synthetic clipboard failure"); };
        PaneAction failedCopy = failure.Module.BrokerAction(CopyInvitationLabel, _ => failure.Module.CopyInvitationAsync());
        string failedResult = failedCopy.InvokeAsync().GetAwaiter().GetResult();
        // The uncertain input: called off the UI thread while the UI thread is blocked, so the posted write is still queued
        // when the 2-second wait ends. The result keeps DECISIONS 67's wording verbatim, and draining the UI afterwards runs
        // the queued write, which is exactly why that wording says it may still complete.
        using var queued = new ClipboardPairingFixture(dispatcher);
        int queuedWrites = 0;
        queued.Module.writeInvitationClipboard = _ => queuedWrites++;
        PaneAction queuedCopy = queued.Module.BrokerAction(CopyInvitationLabel, _ => queued.Module.CopyInvitationAsync());
        string queuedResult = Task.Run(() => queuedCopy.InvokeAsync()).GetAwaiter().GetResult();
        int writesBeforeDrain = queuedWrites;
        dispatcher.Drain();
        // A POSTED writer that throws (Clipboard.SetText failing on the UI thread for an off-thread press), pumped as the UI
        // would run it: the write ran and failed, so this is a definite failure too and must not say it may still complete.
        using var posted = new ClipboardPairingFixture(dispatcher);
        int postedWrites = 0;
        posted.Module.writeInvitationClipboard = _ => { postedWrites++; throw new InvalidOperationException("synthetic posted clipboard failure"); };
        PaneAction postedCopy = posted.Module.BrokerAction(CopyInvitationLabel, _ => posted.Module.CopyInvitationAsync());
        Task<string> postedTask = Task.Run(() => postedCopy.InvokeAsync());
        Pump(() => postedTask.IsCompleted, dispatcher);
        string postedResult = postedTask.GetAwaiter().GetResult();
        check(failedWrites == 1 && failedResult ==
            "Clipboard copy failed. Discovery remains available in the Brokers list." &&
            !failedResult.Contains("may still complete", StringComparison.Ordinal) && PairingRequestCount(failure.Control) == 0 &&
            writesBeforeDrain == 0 && queuedResult ==
            "Clipboard copy did not finish; it may still complete. Discovery remains available in the Brokers list." &&
            queuedWrites == 1 && PairingRequestCount(queued.Control) == 0 &&
            postedWrites == 1 && postedResult == "Clipboard copy failed. Discovery remains available in the Brokers list." &&
            PairingRequestCount(posted.Control) == 0,
            "INVITATION_COPY_PENDING_RESULT: only a write still queued after the wait says it may still complete; a definite failure says it failed; both keep discovery available");

        // U round 7 (U6-X3-1, DECISIONS 124): the completion boundary, through the test-only seam that runs after the wait ended
        // and before the queued write's state is read. Each press is made off the UI thread while the UI thread is blocked, so the
        // 2-second wait ends with the write still queued, unless the input pumps the UI. Four inputs:
        //   pending, then completes: the seam runs the queued write (the UI catching up exactly at the boundary) -> copied;
        //   pending, stays pending:  the seam does nothing -> "may still complete", and draining afterwards runs the write;
        //   completed during the wait: the UI is pumped, the wait succeeds, and the seam is never reached -> copied;
        //   faulted at the boundary: the seam runs a queued write that throws -> "failed".
        // The state is read ONCE: the round 6 code's second read let a write completing between two reads report "failed".
        string Boundary(Action<int>? atBoundary, bool pumpUi, bool throwing, out int writes, out int boundaryCalls)
        {
            using var fixture = new ClipboardPairingFixture(dispatcher);
            int written = 0, calls = 0;
            fixture.Module.writeInvitationClipboard = _ => { written++; if (throwing) throw new InvalidOperationException("synthetic posted clipboard failure"); };
            fixture.Module.AfterInvitationCopyWaitForTest = () => { calls++; atBoundary?.Invoke(calls); };
            PaneAction press = fixture.Module.BrokerAction(CopyInvitationLabel, _ => fixture.Module.CopyInvitationAsync());
            Task<string> pressed = Task.Run(() => press.InvokeAsync());
            if (pumpUi) Pump(() => pressed.IsCompleted, dispatcher);
            string result = pressed.GetAwaiter().GetResult();
            fixture.Module.AfterInvitationCopyWaitForTest = null;
            dispatcher.Drain();
            writes = written; boundaryCalls = calls;
            return result;
        }
        string thenCompletes = Boundary(_ => dispatcher.Drain(), false, false, out int completesWrites, out int completesCalls);
        string staysPending = Boundary(null, false, false, out int pendingWrites, out int pendingCalls);
        string completedEarly = Boundary(null, true, false, out int earlyWrites, out int earlyCalls);
        string faulted = Boundary(_ => dispatcher.Drain(), false, true, out int faultedWrites, out int faultedCalls);
        check(thenCompletes.StartsWith("Public broker invitation copied.", StringComparison.Ordinal) && completesWrites == 1 && completesCalls == 1 &&
            staysPending == "Clipboard copy did not finish; it may still complete. Discovery remains available in the Brokers list." &&
            pendingWrites == 1 && pendingCalls == 1 &&
            completedEarly.StartsWith("Public broker invitation copied.", StringComparison.Ordinal) && earlyWrites == 1 && earlyCalls == 0 &&
            faulted == "Clipboard copy failed. Discovery remains available in the Brokers list." && faultedWrites == 1 && faultedCalls == 1,
            "INVITATION_COPY_WAIT_BOUNDARY: one read of the queued write's state at the end of the wait: completing then is copied, still pending may still complete, faulted failed, completed during the wait never reaches it");
    }

    private static void RunPairingAvailabilityChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        using var f = new ClipboardPairingFixture(dispatcher);
        f.Module.pairingStatus = "Synthetic pairing activity.";
        f.Module.snapshot = null;
        string unavailable = f.Module.PairingDisplayText();
        f.Module.snapshot = f.View;
        string healthy = f.Module.PairingDisplayText();
        f.Module.snapshot = null;
        f.Module.settings = f.Module.settings with { Enabled = false };
        string disabled = f.Module.PairingDisplayText();
        check(unavailable.Contains("Paired broker status is unavailable.", StringComparison.Ordinal) &&
            unavailable.Contains("Synthetic pairing activity.", StringComparison.Ordinal) &&
            !healthy.Contains("unavailable", StringComparison.Ordinal) && !disabled.Contains("unavailable", StringComparison.Ordinal),
            "PAIRING_UNAVAILABLE_WITH_ACTIVITY: missing worker status stays visible beside activity, with healthy/disabled controls");
    }
}
