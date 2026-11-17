using AgentBroker.Protocol;
using DesktopAICompanion.Modules;
using System.Text.Json;

namespace DesktopAICompanion.AgentBroker;

// Lane U checks separate the actual schema contract from production action delegates and synthetic clipboard/control seams.
public sealed partial class AgentBrokerModule
{
    private static void RunPaneLayoutChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        RunPaneCardLayoutChecks(check, dispatcher);
        RunClipboardPairingChecks(check, dispatcher);
        RunInvitationAuditChecks(check, dispatcher);
    }

    private static void RunPaneCardLayoutChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        DateTimeOffset now = new(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);
        var claude = new SessionView("claude:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Claude", "claude-native", "Idle", true, true, false, false);
        var codex = new SessionView("codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Codex", "codex-native", "Idle", true, true, false, false);
        var claudeMailbox = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Claude mailbox", "mcp", "Idle", true, false, false, false);
        var codexMailbox = new SessionView("mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid(), "Synthetic Codex mailbox", "mcp", "Idle", true, false, false, false);
        var mcp = new McpStatusView(true, now, now,
            [new(McpClientFamily.Other), new(McpClientFamily.Claude, LastInitializeUtc: now, LastToolsListUtc: now),
                new(McpClientFamily.Codex, LastInitializeUtc: now, LastToolsListUtc: now)],
            [new(claudeMailbox.SessionId, claudeMailbox.Incarnation, McpClientFamily.Claude),
                new(codexMailbox.SessionId, codexMailbox.Incarnation, McpClientFamily.Codex)]);
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic layout", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
            [], [], [], [claude, codex, claudeMailbox, codexMailbox], new Dictionary<string, string>(), new string('A', 64), Mcp: mcp);
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-pane-layout-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        host.SettingsFor(Id).Set("claudePeerCredentials", "true");
        host.SettingsFor(Id).Set("codexPeerMessages", "true");
        var module = new AgentBrokerModule(new PaneControl(), new StatusSupervisor(view))
            { McpNow = () => now, ClientSetup = new(new TestClientCommand()) };
        try
        {
            module.Init(host); Pump(() => module.snapshot is not null, dispatcher);
            OptionsPane pane = host.OptionsPanes.Single();
            pane.Load();
            var schema = pane.Schema.ToArray();
            SettingField? Lead(string group) => schema.FirstOrDefault(field => field.Group == group);
            string[] groups = schema.Select(field => field.Group).Distinct(StringComparer.Ordinal).ToArray();
            check(groups.SequenceEqual(new[] { "AgentBroker", "Clients", "Connections", "Coding agents", "Reopen a Codex desktop chat" }, StringComparer.Ordinal) &&
                Lead("AgentBroker") is { FullWidth: true, PinTop: true } &&
                Lead("Clients") is { FullWidth: true, Collapsible: true } &&
                Lead("Connections") is { FullWidth: false, PinTop: false } &&
                Lead("Coding agents") is { FullWidth: false, PinTop: false } &&
                Lead("Reopen a Codex desktop chat") is { FullWidth: false, PinTop: false, Collapsible: true, StartCollapsed: true } &&
                schema.Where(field => field.Group is "Connections" or "Coding agents" or "Reopen a Codex desktop chat").All(field => !field.FullWidth) &&
                schema.SingleOrDefault(field => field.Id == "pairingStatus") is { Group: "AgentBroker", Kind: SettingKind.Info } &&
                schema.Count(field => field.Id is "claudeClient" or "codexClient" or "nativeStatus") == 3 &&
                schema.Where(field => field.Id is "claudeClient" or "codexClient" or "nativeStatus").All(field => field.Group == "Clients"),
                "CARD_ORDER_AND_WIDTHS: pinned status and full-width Clients precede consecutive narrow Connections, Coding agents and folded Reopen cards");

            ListCard[] lists = pane.Lists?.ToArray() ?? [];
            // Lane F (DECISIONS 140): the per-broker attachments allow acts on one ticked Paired row, so it sits with the seven
            // pairing actions; the card still holds exactly the broker actions and nothing else.
            string[] expectedActions = [RefreshBrokersLabel, RequestPairingLabel, ApprovePairingLabel, CancelPairingLabel,
                AllowAttachmentsLabel, RevokeSharingLabel, CopyInvitationLabel, PairCopiedInvitationLabel];
            check(lists is [{ Title: "Brokers" }] && lists[0].Actions.Select(action => action.Label).SequenceEqual(expectedActions, StringComparer.Ordinal) &&
                !schema.Any(field => field.Id is "manualPeer" or "pairedBrokers" or "nativePrivacy" || field.Group == "Pair by invitation") &&
                !pane.Load().ContainsKey("pairedBrokers") &&
                !pane.Actions.Any(action => action.Group is "Pair by invitation" or "Brokers" || expectedActions.Contains(action.Label, StringComparer.Ordinal)),
                "BROKERS_ONE_CARD: one Brokers list owns exactly the seven pairing actions and the attachments allow, and no invitation text box or duplicate summary paragraph remains");

            // Health folding and the pinned summary are checked separately by RunClientsSummaryChecks.
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }

    // Schema membership belongs to BROKERS_ONE_CARD. Behavior fixtures use the exact production
    // action factory and handlers so moving/removing a schema action cannot abort unrelated checks.
    private sealed class ClipboardPairingFixture : IDisposable
    {
        private readonly TestDispatcher dispatcher;
        internal DateTimeOffset Now = new(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);
        internal PeerView Peer = new(Guid.NewGuid(), "Clipboard & peer", "https://192.0.2.31:7433/", new string('B', 64));
        internal BrokerSnapshot View;
        internal readonly PaneControl Control = new();
        internal readonly AgentBrokerModule Module;
        internal readonly OptionsPane Pane;
        internal readonly PaneAction Pair;
        internal string Clipboard;
        internal int Reads, ReadThread;
        internal readonly int UiThread = Environment.CurrentManagedThreadId;

        internal ClipboardPairingFixture(TestDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            View = new(Guid.NewGuid(), "Synthetic clipboard", "http://127.0.0.1:7432", "https://192.0.2.10:7433/",
                [], [], [], [], new Dictionary<string, string>(), new string('A', 64));
            var host = new TestHost();
            host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-pane-clipboard-synthetic")));
            host.SettingsFor(Id).Set("enabled", "true");
            host.SettingsFor(Id).Set("enableLan", "true");
            Module = new(Control, new StatusSupervisor(View))
                { McpNow = () => Now, ClientSetup = new(new TestClientCommand()) };
            Clipboard = JsonSerializer.Serialize(Peer, ControlProtocol.Json);
            Module.readInvitationClipboard = Read;
            Module.Init(host);
            Pump(() => Module.snapshot is not null, dispatcher);
            Pane = host.OptionsPanes.Single();
            Pair = Module.BrokerAction(PairCopiedInvitationLabel, Module.PairCopiedInvitationAsync);
        }

        internal string Read() { Reads++; ReadThread = Environment.CurrentManagedThreadId; return Clipboard; }
        internal void Apply(BrokerSnapshot view) { View = view; Module.ApplyStatus(new(true, true, "running", view)); }
        internal void Arm(PeerView? peer = null) =>
            Module.invitationArm = (peer ?? Peer, Now + InvitationConfirmWindow, Module.generation, Module.snapshot!.BrokerId);
        internal string Press(PaneAction? action = null)
        {
            try { return (action ?? Pair).InvokeAsync().GetAwaiter().GetResult(); }
            catch (Exception error) { return "EXCEPTION " + error.GetType().Name; }
        }
        public void Dispose() { Module.Shutdown(); Pump(() => Module.pending.IsCompleted, dispatcher); }
    }

    private static void RunClipboardPairingChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        void Scenario(Action<ClipboardPairingFixture> body)
        {
            using var fixture = new ClipboardPairingFixture(dispatcher);
            body(fixture);
        }

        Scenario(f =>
        {
            Task<string> offThread = Task.Run(() => f.Pair.InvokeAsync());
            Pump(() => offThread.IsCompleted, dispatcher);
            string preview = offThread.GetAwaiter().GetResult();
            check(f.Reads == 1 && f.ReadThread == f.UiThread,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_UI_THREAD: a first press reads the synthetic clipboard on the captured UI thread");
            check(PairingRequestCount(f.Control) == 0 && !f.Pair.ReloadPaneAfter &&
                f.Module.invitationArm is { } arm && arm.Peer == f.Peer && arm.Deadline == f.Now + InvitationConfirmWindow &&
                arm.Generation == f.Module.generation &&
                preview.Contains(BrokerRowName(f.Peer), StringComparison.Ordinal) && preview.Contains("192.0.2.31:7433", StringComparison.Ordinal) &&
                preview.Contains("within " + InvitationConfirmWindow.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " seconds", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_PREVIEW_ONLY: a first press arms exactly the displayed identity and configured window without sending or reloading");
            check(!preview.Contains(f.Clipboard, StringComparison.Ordinal) && !preview.Contains(f.Peer.CertificateSha256, StringComparison.Ordinal) &&
                !preview.StartsWith("EXCEPTION", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_NO_ECHO: the preview contains neither clipboard JSON nor certificate material");
        });
        Scenario(f =>
        {
            // Round 6 (U-6): the owner's flow, from a REAL first press (preview), so the arm the confirm compares is the one the
            // product recorded, not the synthetic Arm().
            string preview = f.Press();
            f.Now = f.Now.AddSeconds(30);
            string confirmed = f.Press();
            check(preview.Contains("No request has been sent.", StringComparison.Ordinal) &&
                PairingRequestCount(f.Control) == 1 && f.Control.Requests.LastOrDefault() is { Operation: "pair-request" } request &&
                request.Peer == f.Peer && f.Pair.ReloadPaneAfter && f.Module.invitationArm is null &&
                confirmed.Contains("both", StringComparison.OrdinalIgnoreCase),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_WINDOW_30_ACCEPTS: a matching second press exactly thirty seconds after arming requests the same peer");
        });
        Scenario(f =>
        {
            // Round 6 (U-7): the Brokers card's OWN two invitation buttons, taken by label from the list the pane shows, bound to
            // the right handlers: Copy writes once and never reads; Pair reads once, never writes, and only previews.
            int writes = 0;
            f.Module.writeInvitationClipboard = _ => writes++;
            PaneAction[] cardActions = f.Pane.Lists!.Single().Actions.ToArray();
            string copied = f.Press(cardActions.Single(action => action.Label == CopyInvitationLabel));
            bool copyBound = writes == 1 && f.Reads == 0 && copied.StartsWith("Public broker invitation copied.", StringComparison.Ordinal);
            string preview = f.Press(cardActions.Single(action => action.Label == PairCopiedInvitationLabel));
            check(copyBound && f.Reads == 1 && writes == 1 && PairingRequestCount(f.Control) == 0 &&
                preview.StartsWith("Invitation from ", StringComparison.Ordinal) && preview.Contains("No request has been sent.", StringComparison.Ordinal),
                "BROKERS_INVITATION_BINDING: the Brokers card's Copy button writes without reading and its Pair button reads and previews without writing");
        });
        Scenario(f =>
        {
            f.Clipboard = JsonSerializer.Serialize(f.Peer with { BrokerId = f.View.BrokerId }, ControlProtocol.Json);
            f.Arm();
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                refused == "This is this broker's own invitation. Copy one from the other workstation.",
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_OWN_REFUSED: this broker's own invitation clears an existing arm without sending or echoing it");
        });
        Scenario(f =>
        {
            f.Apply(f.View with { Paired = [f.Peer] });
            f.Arm();
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                refused == "That broker identity is already paired. See Paired in the Brokers list.",
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_PAIRED_REFUSED: an already-paired identity clears an existing arm without sending");
        });

        foreach ((string label, Func<ClipboardPairingFixture, string> input) in new (string, Func<ClipboardPairingFixture, string>)[]
        {
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_INVALID_REFUSED", _ => "SYNTHETIC_PRIVATE_CLIPBOARD_PASSWORD"),
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_OVERSIZE_REFUSED", f => JsonSerializer.Serialize(f.Peer, ControlProtocol.Json) + new string(' ', 4097)),
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_USERINFO_REFUSED", f => JsonSerializer.Serialize(f.Peer with { Endpoint = "https://user:PRIVATE_PASSWORD@192.0.2.31:7433/" }, ControlProtocol.Json)),
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_QUERY_REFUSED", f => JsonSerializer.Serialize(f.Peer with { Endpoint = "https://192.0.2.31:7433/?PRIVATE_QUERY" }, ControlProtocol.Json)),
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_FRAGMENT_REFUSED", f => JsonSerializer.Serialize(f.Peer with { Endpoint = "https://192.0.2.31:7433/#PRIVATE_FRAGMENT" }, ControlProtocol.Json)),
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_PATH_REFUSED", f => JsonSerializer.Serialize(f.Peer with { Endpoint = "https://192.0.2.31:7433/PRIVATE_PATH" }, ControlProtocol.Json)),
            ("CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_SCHEME_REFUSED", f => JsonSerializer.Serialize(f.Peer with { Endpoint = "http://192.0.2.31:7433/" }, ControlProtocol.Json))
        })
        {
            Scenario(f =>
            {
                f.Arm();
                f.Clipboard = input(f);
                string refused = f.Press();
                check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                    refused == "Copy a valid broker invitation first. Its contents are not shown here.",
                    label + ": invalid clipboard text is a fixed refusal with its existing preview disarmed");
            });
        }
        Scenario(f =>
        {
            f.Arm();
            f.Module.readInvitationClipboard = () => throw new System.Runtime.InteropServices.ExternalException("SYNTHETIC_PRIVATE_CLIPBOARD_ERROR");
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                refused == "The clipboard is unavailable. Copy an invitation again or use discovery in Brokers.",
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_CLIPBOARD_FAILURE_REFUSED: clipboard access failure is fixed text and clears the existing arm");
        });
        Scenario(f =>
        {
            f.Arm();
            var changed = f.Peer with { Endpoint = "https://192.0.2.32:7433/" };
            f.Clipboard = JsonSerializer.Serialize(changed, ControlProtocol.Json);
            string preview = f.Press();
            check(PairingRequestCount(f.Control) == 0 && !f.Pair.ReloadPaneAfter && f.Module.invitationArm is { } arm &&
                arm.Peer == changed && preview.Contains("192.0.2.32:7433", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_CHANGED_INVITATION: a changed identity requires a new preview rather than confirming the old one");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Module.ResetBrokerList();
            check(f.Module.invitationArm is null,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_RESET_DISARMS: clearing broker state also clears its copied-invitation arm");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Now = f.Now.AddSeconds(31);
            string preview = f.Press();
            check(PairingRequestCount(f.Control) == 0 && !f.Pair.ReloadPaneAfter && f.Module.invitationArm is not null &&
                preview.Contains("No request has been sent.", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_WINDOW_31_REFUSES: a thirty-one-second-old arm requires a new preview");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Now = f.Now.AddSeconds(-1);
            string preview = f.Press();
            check(PairingRequestCount(f.Control) == 0 && !f.Pair.ReloadPaneAfter && f.Module.invitationArm is not null &&
                preview.Contains("No request has been sent.", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_BACKWARD_CLOCK: a clock before the arm time requires a new preview");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Module.LoadBrokerItems();
            check(f.Module.invitationArm is null,
                "INVITATION_REFRESH_DISARMS: rebuilding the Brokers list clears the previous preview");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Module.Restart();
            Pump(() => f.Module.snapshot is not null, dispatcher);
            check(f.Module.invitationArm is null,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_RESET_DISARMS: worker restart clears the same broker-state arm");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Apply(f.View with { BrokerId = Guid.NewGuid() });
            string preview = f.Press();
            check(PairingRequestCount(f.Control) == 0 && !f.Pair.ReloadPaneAfter && f.Module.invitationArm is { } arm &&
                arm.BrokerId == f.View.BrokerId && preview.Contains("No request has been sent.", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_LOCAL_BROKER_CHANGED: an arm from another local broker identity requires a fresh preview");
        });
        Scenario(f =>
        {
            f.Arm();
            int generation = f.Module.generation;
            f.Module.readInvitationClipboard = () => { f.Module.settings = f.Module.settings with { EnableLan = false }; return f.Clipboard; };
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                f.Module.generation == generation && refused == "Enable LAN and wait for the broker to start first.",
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_LAN_CHANGED_DURING_READ: disabling LAN during reading refuses without a generation change");
        });
        Scenario(f =>
        {
            f.Arm();
            int generation = f.Module.generation;
            f.Module.readInvitationClipboard = () => { f.Apply(f.View with { BrokerId = Guid.NewGuid() }); return f.Clipboard; };
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                f.Module.generation == generation && refused == BrokerActionRestarted,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_LOCAL_IDENTITY_CHANGED_DURING_READ: changing local identity while reading refuses without a generation change");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Module.readInvitationClipboard = () => { f.Module.Restart(); f.Apply(f.View); return f.Clipboard; };
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter && refused == BrokerActionRestarted,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_GENERATION_CHANGED_DURING_READ: a restart during reading refuses old-generation clipboard text");
        });
        Scenario(f =>
        {
            f.Control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PaneAction refresh = f.Module.BrokerAction(RefreshBrokersLabel, f.Module.RefreshBrokersAsync);
            Task<string> refreshing = refresh.InvokeAsync();
            int before = PairingRequestCount(f.Control);
            Task<string> busy = f.Pair.InvokeAsync();
            bool refused = busy.IsCompleted && f.Reads == 0 && !f.Pair.ReloadPaneAfter;
            f.Control.Pending.SetResult(new(true, "ok", f.View)); f.Control.Pending = null;
            Pump(() => refreshing.IsCompleted && busy.IsCompleted, dispatcher);
            check(refused && PairingRequestCount(f.Control) == before &&
                busy.GetAwaiter().GetResult().StartsWith("Another broker action is still running", StringComparison.Ordinal),
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_BUSY_REFUSED: another running action refuses copied pairing before reading or sending");
        });
        Scenario(f =>
        {
            f.Arm();
            f.Pane.Save(new Dictionary<string, string> { ["enabled"] = "false" });
            string refused = f.Press();
            check(PairingRequestCount(f.Control) == 0 && f.Module.invitationArm is null && !f.Pair.ReloadPaneAfter &&
                refused == BrokerListUnavailable,
                "CLIPBOARD_PAIRING_PREVIEW_NO_ECHO_OWN_REFUSED_DISABLED_REFUSED: disabling AgentBroker clears its arm and cannot confirm it");
        });
    }
}
