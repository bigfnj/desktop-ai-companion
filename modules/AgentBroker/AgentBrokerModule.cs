using System.Diagnostics;
using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

/// <summary>Host configuration and tray UI. Broker/network work runs in the supervised worker.</summary>
public sealed partial class AgentBrokerModule : IModule
{
    internal const string Id = "agentbroker";
    private readonly IBrokerControlClient control;
    private readonly IWorkerSupervisor supervisor;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private IHost? host;
    private IModuleSettings? settingsStore;
    private OptionsPane? optionsPane;
    private SettingField? clientsBand;
    // The live Info values last notified, in InfoValues order. An array rather than a fixed tuple so that a new live Info
    // row is one more entry in InfoValues, compared the same way (lane P, 1.10.5).
    private string[]? notifiedInfo;
    private ITrayQuestions? questions;
    private SynchronizationContext? ui;
    private BrokerSettings settings = new(false, "AgentBroker", 7432, 7433, true, true, true, true, false, false);
    private BrokerSnapshot? snapshot;
    private CancellationTokenSource? run;
    private Task pending = Task.CompletedTask;
    private string? dataDirectory;
    private string status = "Disabled";
    private int generation;
    private long nextObservation;
    private long lastAppliedObservation;
    private bool ownsWorker;
    private bool shutDown;
    internal AgentClientSetup ClientSetup = new(new AgentClientCommand());
    internal ILanFirewallLauncher FirewallLauncher = new LanFirewallLauncher();

    public AgentBrokerModule() : this(new BrokerControlClient(), null) { }
    internal AgentBrokerModule(IBrokerControlClient control, IWorkerSupervisor? supervisor)
    { this.control = control; this.supervisor = supervisor ?? new WorkerSupervisor(control); }

    public ModuleInfo Info => new()
    {
        Id = "agentbroker",
        Name = "AgentBroker",
        Version = "1.10.5",  // Reply settlement, waiting-mail observations, compiled inbox watching and clearer pairing controls.
                             // 1.10.4: slow native discovery consumes its refresh interval instead of adding a second full pause.
                             // 1.10.3: --mcp-call encodes and bounds the whole query before connecting, so depth-limit requests travel.
                             // 1.10.2: request values decoded before dispatch, output checks before dispatch, live endpoint display.
                             // 1.10.1: verified client HTTP setup and a single cancellation outcome.
                             // 1.10.0: collaboration handoffs, retained client output and delivery advisories.
                             // 1.9.0: authenticated certificate renewal and post-push reliability repairs.
                             //        Exact-incarnation delivery and the host ABI floor are unchanged.
                             // 1.8.0: arrival-cursor inbox reads, report-only native recovery, exact-address declarations,
                             //        independent discovery, delivery diagnostics and a supported one-request client.
                             // 1.7.2: a Codex build that is not exactly pinned but that Windows verifies as signed by its
                             //        publisher (the VS Code extension, the desktop app or the VS Code router) is reported
                             //        as a note naming the build ("Publisher-verified builds: ... Update-tolerant delivery is
                             //        enabled.") instead of an adapter error, as Claude's has been since 1.7.1 (the owner's
                             //        rule of 2026-11-02: a user should not have to know about a build trusted on its
                             //        signature; built by Codex). The note appears even when no Codex chat is open. A
                             //        failed signature, update-tolerant delivery turned off, or a router other than the one
                             //        verified keeps its error. A long note now wraps onto further lines in the tray menu
                             //        instead of being cut, and the pane shows each note whole.
                             // 1.7.1: Claude Code sessions keep waking after Claude Code updates itself (the owner's
                             //        decision of 2026-11-02). "Also wake updated Claude builds (publisher verified)", on
                             //        by default, wakes a Claude Code build that is not exactly pinned as a pinned one is,
                             //        when Windows verifies the running session's claude.exe as signed by "Anthropic, PBC"
                             //        and its hash is the image the session's own native pipe server runs; checked at
                             //        discovery, again before each send, and on the connected image before any credential
                             //        is read. It takes effect with "Wake existing Claude sessions". While it carries a
                             //        session the pane shows a note naming the build (2.1.314) instead of the
                             //        unsupported_public_version error, and several notes each get a line of their own.
                             //        The adapter now names a running claude.exe by the kernel's image path on a retained
                             //        handle (QueryFullProcessImageNameW), compared again after the image lease and at
                             //        re-inspection, refusing native_image_path_mismatch before any credential read, and
                             //        refuses an elevated process on that handle (TokenElevation), so elevated sessions stay
                             //        counted and skipped (N-broker-image-path-01, found and fixed by Codex; this also
                             //        hardens exact pins). Not re-proven for an unreviewed build: how it handles the
                             //        authenticated peer frame, the same class of gap accepted for Codex. MinHostVersion
                             //        unchanged (1.6.1).
                             // 1.7.0: four reported items (lane feature/broker-163). Codex rows report, never enforce,
                             //        which engine holds their thread: the adapter asks Restart Manager, read-only and
                             //        in the background after each discovery pass within a budget, who has the thread's
                             //        writer lock open and says bound (code_engine, desktop_engine) or unbound with the
                             //        reason, on the list_sessions row, on the sender's receipt (the row's latest
                             //        observation at the attempt) and as a Codex line in client status; nothing is
                             //        refused on it and no address changes (N-agentbroker-code-owner-provenance-01, the
                             //        owner's report-only decision). The observation keeps each thread's own query
                             //        time, and a conversation whose owner lookup ran out of time keeps its last
                             //        observation. An elevated Claude Code or Codex caller gets whoami caller_elevated
                             //        instead of caller_unresolved or no_native_session, whichever way Windows refuses
                             //        the Medium broker, and elevated Claude Code sessions are counted as a note beside
                             //        the healthy sessions' line (claude_session_elevated); the Medium-to-High boundary
                             //        is not bridged (N-broker-elevated-caller-01). The sender's receipts (send,
                             //        send_message, reply_to_message, wait_for_receipt, get_message_receipt) leave out
                             //        the message text, with textBytes, and get_message_receipt takes includeText
                             //        (N-broker-compact-receipts-01). Receipts carry readAt and, while a native
                             //        recipient keeps answering that its turn is running, busySince; such a row that
                             //        discovery calls Unknown lists as Busy (N-broker-busy-visibility-01). A paired
                             //        broker's caller gets neither busySince nor the engine binding. MinHostVersion
                             //        unchanged (1.6.1).
                             // 1.6.2: broker-161's filed items and two found on the way (lane feature/broker-162). A
                             //        delivery or receipt loop that fails unexpectedly is shown in Status (delivery:
                             //        loop_failed or receipts: loop_failed, with a count and the last time) and runs
                             //        again after a pause of one second doubling to a minute (N-broker-161-02); a
                             //        submission that threw an unexpected exception ends Uncertain submission_failed
                             //        instead of holding every later message to its recipient until a restart. Codex
                             //        rows stay listed when one chat's owner lookup keeps timing out: the owner
                             //        reconnect runs in the pause between discovery passes, after a failed lookup and
                             //        after a failed submission alike (N-broker-161-01, N-broker-162-02), and the
                             //        lookup order no longer blames a slow reconnect on the chat that waited for it, so
                             //        even a reconnect longer than a pass and a pause lets every other pass renew the
                             //        rows (N-broker-162-01). An uncertain Claude delivery names its cause
                             //        (native_uncertain_raised_io, _access or _cancelled; N-broker-161-03). The
                             //        private/mute preference wiring has its own check.
                             // 1.6.1: fixes after the 2026-10-29 audit (lane feature/broker-161). A message to a Codex
                             //        chat is no longer refused because the broker could not finish checking the Codex
                             //        engine: the row stays listed with Receive only and the message waits for a check
                             //        that completes, refused only after five minutes of incomplete checks
                             //        (N-broker-next-02). A message waiting for a native session gives the reason in
                             //        its receipt (recipient_busy, recipient_awaiting_approval, recipient_interrupted,
                             //        recipient_activity_unknown, recipient_wake_unavailable or recipient_unavailable;
                             //        N-broker-next-01), and an uncertain Codex delivery names its cause
                             //        (native_uncertain_raised_<code>). Codex rows stay listed: a slow reconnect is
                             //        kept rather than lost to the pass deadline (N-broker-next-03), a delivery whose
                             //        answer was lost retires the connection so the next pass reconnects instead of
                             //        failing whole, a router error answer keeps an intact connection, and a stray
                             //        codex.exe whose parent has exited is ignored rather than turning Codex delivery
                             //        off. A paired broker's answer this one cannot read costs only that request; it
                             //        used to end a delivery or receipt loop until a restart and break list_sessions.
                             //        Private and mute choices keep working past 1,024 by dropping choices for sessions
                             //        no longer seen. Status says when the last delivery notice was dropped. The docs
                             //        say that while "Allow local connections without a token" is on, any local account
                             //        can reach the broker.
                             // 1.6.0: backlog batch 3 (2026-10-29). A Codex check the broker could not complete (an
                             //        engine read failing under load, or the owner connection dropped) defers as
                             //        native_verification_unavailable instead of refusing, without reconnecting (the
                             //        discovery loop does that); a missing capability or a verified mismatch is still
                             //        refused. Adapters renew independently; one slow, failing or rejected Codex owner
                             //        lookup skips only its own row; an unexpected adapter failure reads adapter_failed
                             //        and discovery keeps trying. A worker start reads about half as
                             //        much for Codex verification; tombstones share addresses (44% less heap); a wait
                             //        reads only Accepted incoming records. The collaboration helper can send as the
                             //        calling Codex agent's own codex:<CODEX_THREAD_ID> row. The Client connection
                             //        text says a session started while the broker was down must reconnect its MCP.
                             // 1.5.0: backlog batch 2 (2026-10-29). "Also wake updated Codex builds" is on by default
                             //        (the owner's decision): publisher trust plus the runtime checks; off is strict.
                             //        Status counts delivery notices dropped undelivered, by reason.
                             // 1.4.1: backlog batch (2026-10-29). A paired broker's Expired is recorded as Refused with
                             //        reason peer_expired and its notice says so, instead of "refused by its recipient".
                             //        send adds a second content block when the recipient is a local mailbox that
                             //        nothing can wake; the receipt stays the first block.
                             //        Codex discovery reuses the publisher check while the same engine processes run
                             //        (measured: 824 MB/min of reads to 0); the exact-profile check and every
                             //        submission stay fresh, and a fresh refusal forgets the reused success. A paired
                             //        broker's Expired answer to the forward itself is peer_expired too.
                             //        MinHostVersion unchanged (1.6.1).
                             // 1.4.0: first contact (2026-10-29), from agents on a second workstation who registered
                             //        mailboxes and ran pollers despite native rows, started fresh threads at the eighth
                             //        reply and saw messages out of order. A new whoami tool returns a Claude caller's
                             //        own native row from the process that opened its loopback connection (a Codex
                             //        caller uses CODEX_THREAD_ID: one Codex server hosts many chats); the card leads
                             //        with it, sends observation-only rows to a mailbox and says how to recover from a
                             //        changed incarnation. reply_to_message
                             //        at remainingReplies 0 refuses as reply_budget_exhausted and points to send with the
                             //        same conversationId. The native envelope carries createdAt. One sender's messages
                             //        to one recipient are delivered in the order the broker accepted them. The installer
                             //        expects twelve tools. MinHostVersion unchanged (1.6.1).
                             // 1.3.0: delivery feedback (2026-10-28, with Codex session 01a18357 as reviewer). A local
                             //        sender whose message ends Expired or Refused gets one notice from the reserved
                             //        agentbroker:notices address, owed to its exact address until it can be admitted
                             //        and rebuilt from the durable receipts after a restart; a native sender is woken with
                             //        it. A new wait_for_receipt tool returns a sent message's outcome instead of
                             //        polling, and receipts show a passed deadline at once. The native envelope names
                             //        the sender (senderLabel, senderScope), marked self-declared. Notice IDs are a
                             //        reserved subspace (reserved_message_id). The installer expects eleven tools.
                             //        MinHostVersion unchanged (1.6.1).
                             // 1.2.0: vendor re-pins as data (lane broker-pins, 2026-10-27). Every exact pin is one
                             //        JSON document embedded in the worker's adapters and validated strictly at start;
                             //        a missing or malformed document fails closed (no wake, Codex discovery refused)
                             //        and the Native status says why. Claude Code 2.1.293 (npm CLI), 2.1.310, 2.1.311
                             //        and 2.1.312 are verified builds (the owner approved the four re-pins). A client
                             //        newer than every pin is named in the Native status with "a reviewed re-pin
                             //        arrives as a module update"; nothing is checked online. The re-pin helper checks
                             //        a downloaded npm package or VSIX and drafts a pin entry, and a daily repository
                             //        workflow drafts one pull request per new build that passes every offline check,
                             //        never merging. MinHostVersion unchanged (1.6.1).
                             // 1.1.0: the agent-to-agent bus (campaign 2026-10-26). A generic MCP mailbox stays
                             //        addressable for 24 hours after its owner's last call (listed locally as Away,
                             //        never to peers); new send, wait_for_messages and wait_for_reply tools and MCP
                             //        server instructions; the lease sidecar is retired, and the installer's probe
                             //        expects the tool set of the package it installs. The loopback listener also
                             //        answers Host localhost:<port> (a managed Claude Code allowlist admits only
                             //        http://localhost*), Connect Claude MCP retries an allowlist miss once with that
                             //        URL, and client setup finds npm-installed Claude Code and Codex by the model
                             //        calls' locator, never a shim. A wait on a restart-lost mailbox is refused before
                             //        it returns pending messages. An acknowledged message sent from this workstation
                             //        keeps a tombstone without its text, so only messages with text count toward the
                             //        4,096 bound; paired-broker, Held and Expired messages keep theirs, and a rollback
                             //        to 1.0.4 loads. Coding agents and the tray show per-client setup states; LAN
                             //        access shows the network category beside the firewall rule. Codex sessions on a
                             //        publisher-signed build that is not exactly pinned are listed read-only instead of
                             //        hidden, with an opt-in to wake them (default off); a read-only re-pin helper; the
                             //        worker's opt-in --mcp-channel Claude Code channel bridge. MinHostVersion
                             //        unchanged (1.6.1).
        MinHostVersion = "1.6.1",
        Permissions = ModulePermissions.Network | ModulePermissions.Storage | ModulePermissions.LaunchProcess |
            ModulePermissions.AgentTranscripts
    };

    public void Init(IHost host)
    {
        if (this.host is not null) throw new InvalidOperationException("Module already initialized.");
        this.host = host;
        ui = SynchronizationContext.Current;
        AttachApprovalObservation(host);
        questions = host as ITrayQuestions;
        settingsStore = host.GetSettings(Id) ?? new DesktopAICompanion.ModuleKit.MemoryModuleSettings();
        settings = BrokerSettings.Read(settingsStore);
        string? storage = host.GetStorage(Id)?.DataDirectory;
        if (storage is not null && Path.IsPathFullyQualified(storage)) dataDirectory = Path.GetFullPath(storage);
        brokerCard = BuildBrokerCard();
        optionsPane = BuildOptions();
        notifiedInfo = InfoValues();
        host.AddOptionsPane(optionsPane);
        // PANE-POPUP (1.10.5): a waiting incoming pairing request is a passive signal on the tray root, read when the menu opens.
        host.AddTrayItems([new TrayItem { Label = "AgentBroker", DynamicText = TrayRootLabel, Group = 30, Order = 0, BuildChildren = BuildTray,
            IconPng = DesktopAICompanion.ModuleKit.EmbeddedResources.LoadBytes(typeof(AgentBrokerModule).Assembly, "agentbroker.png") }]);
        host.HostShutdown += Shutdown;
        Restart();
    }

    public void Shutdown()
    {
        if (shutDown) return;
        shutDown = true; generation++;
        if (host is not null) { host.HostShutdown -= Shutdown; DetachApprovalObservation(host); }
        run?.Cancel();
        ResetBrokerList();
        CancellationTokenSource? stopped = run;
        _ = pending.ContinueWith(_ => stopped?.Dispose(), TaskScheduler.Default);
        // RunAsync's finally stops our own child off the UI thread. Parent PID/start binding is
        // the worker's last cleanup guard if host exit prevents this continuation.
        host = null; questions = null; snapshot = null; waitingMail = null; optionsPane = null;
    }

    private OptionsPane BuildOptions() => new()
    {
        Title = "AgentBroker",
        Schema =
        [
            new() { Id = "status", Label = "Status", Kind = SettingKind.Info, Group = "AgentBroker", FullWidth = true, PinTop = true },
            new() { Id = "pairingStatus", Label = "Pairing", Kind = SettingKind.Info, Group = "AgentBroker" },
            new() { Id = "clientsSummary", Label = "Clients", Kind = SettingKind.Info, Group = "AgentBroker" },
            new() { Id = "agentInboxes", Label = "Agent inboxes", Kind = SettingKind.Info, Group = "AgentBroker" },
            new() { Id = "waitingMail", Label = "Waiting mail", Kind = SettingKind.Info, Group = "AgentBroker" },
            new() { Id = "mcpStatus", Label = "MCP client traffic", Kind = SettingKind.Info, Group = "AgentBroker" },
            new() { Id = "approvalPrompts", Label = "Visible Codex approval prompts", Kind = SettingKind.Info, Group = "AgentBroker" },
            new() { Id = "enabled", Label = "Enable AgentBroker", Kind = SettingKind.Bool, Group = "AgentBroker" },
            clientsBand = new() { Id = "claudeClient", Label = "Claude Code (CLI and VS Code)", Kind = SettingKind.Info,
                Group = "Clients", FullWidth = true, Collapsible = true },
            new() { Id = "codexClient", Label = "Codex", Kind = SettingKind.Info, Group = "Clients" },
            new() { Id = "nativeStatus", Label = "Native adapters", Kind = SettingKind.Info, Group = "Clients" },
            new() { Id = "name", Label = "Broker name", Kind = SettingKind.Text, Group = "Connections" },
            new() { Id = "loopbackPort", Label = "Local port", Kind = SettingKind.Int, Min = 1, Max = 65535, Group = "Connections" },
            new() { Id = "messageLifetimeHours", Label = "Default message delivery window (hours)", Kind = SettingKind.Int, Min = 1, Max = 24, Group = "Connections" },
            new() { Id = "noAuthLoopback", Label = "Allow local connections without a token", Kind = SettingKind.Bool, Group = "Connections" },
            new() { Id = "enableLan", Label = "Enable LAN discovery and pairing", Kind = SettingKind.Bool, Group = "Connections" },
            new() { Id = "lanPort", Label = "LAN port", Kind = SettingKind.Int, Min = 1, Max = 65535, Group = "Connections", EnabledWhen = "enableLan=true" },
            new() { Id = "lanAccess", Label = "LAN firewall access", Kind = SettingKind.Info, Group = "Connections" },
            new() { Id = "codexDiscovery", Label = "Discover open Codex sessions", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "additionalCodexHome", Label = "Additional Codex home to observe", Kind = SettingKind.FolderPath,
                Group = "Coding agents", EmptyHint = "Optional; the default Codex home is already observed", EnabledWhen = "codexDiscovery=true" },
            new() { Id = "claudeDiscovery", Label = "Discover open Claude sessions", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "codexPeerMessages", Label = "Wake existing Codex sessions", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "claudePeerCredentials", Label = "Wake existing Claude sessions", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "claudeWakeConsent", Label = "Claude wake-up", Kind = SettingKind.Info, Group = "Coding agents" },
            new() { Id = "codexUpdateTolerantDelivery", Label = "Also wake updated Codex builds (publisher verified)", Kind = SettingKind.Bool,
                Group = "Coding agents", EnabledWhen = "codexPeerMessages=true" },
            new() { Id = "claudeUpdateTolerantDelivery", Label = "Also wake updated Claude builds (publisher verified)", Kind = SettingKind.Bool,
                Group = "Coding agents", EnabledWhen = "claudePeerCredentials=true" },
            new() { Id = "desktopChatHelp", Label = "Open an existing chat", Kind = SettingKind.Info,
                Group = "Reopen a Codex desktop chat", Collapsible = true, StartCollapsed = true },
            new() { Id = "desktopChatOrigin", Label = "Original app", Kind = SettingKind.Enum,
                Options = [DesktopChatOriginPrompt, DesktopChatOrigin, DesktopChatVsCodeOrigin], Group = "Reopen a Codex desktop chat" },
            new() { Id = "desktopChatThreadId", Label = "Thread ID from the chat link", Kind = SettingKind.Text,
                Group = "Reopen a Codex desktop chat", EnabledWhen = "desktopChatOrigin=" + DesktopChatOrigin }
        ],
        Lists = [brokerCard!],
        Load = LoadOptions, Save = SaveOptions,
        Actions = [new PaneAction { Label = "Refresh status", Group = "AgentBroker", ReloadPaneAfter = true,
            InvokeAsync = RefreshStatusAsync },
            new PaneAction { Label = GuideActionLabel, Group = "AgentBroker", InvokeAsync = OpenGuideAsync },
            new PaneAction { Label = "Check client setup", Group = "Clients", ReloadPaneAfter = true, InvokeAsync = () => CheckClientSetupAsync() },
            new PaneAction { Label = "Connect Codex MCP", Group = "Clients", ReloadPaneAfter = true, InvokeAsync = () => ConnectAndCheckAsync("codex") },
            new PaneAction { Label = "Connect Claude MCP", Group = "Clients", ReloadPaneAfter = true, InvokeAsync = () => ConnectAndCheckAsync("claude") },
            new PaneAction { Label = "Open existing desktop chat", Group = "Reopen a Codex desktop chat",
                InvokeWithPendingAsync = OpenCodexDesktopChatAsync },
            new PaneAction { Label = "Allow LAN access", Group = "Connections", ReloadPaneAfter = true, InvokeAsync = AllowLanAccessAsync }]
    };

    private IReadOnlyDictionary<string, string> LoadOptions()
    {
        // The host loads values before reading the schema on each open; Init has no client observations yet.
        if (clientsBand is not null) clientsBand.StartCollapsed = ClientsAreHealthy();
        var values = settings.Values();
        values["status"] = DisplayedStatus();
        values["approvalPrompts"] = ApprovalPromptText();
        values["mcpStatus"] = McpDisplay().Detail;
        values["agentInboxes"] = AgentInboxDisplay().Detail;
        values["waitingMail"] = WaitingMailText();
        values["pairingStatus"] = PairingDisplayText();
        values["clientsSummary"] = ClientsSummaryText();
        values["lanAccess"] = LanAccessText();
        // PANE-TEXT (1.10.5): each static explanation is at most two sentences (PaneTextRows); the full text is in the guide.
        // Publisher verification stays in the switch labels; credential consent stays directly under Claude wake-up.
        values["claudeWakeConsent"] = ClaudeWakeConsent;
        values["nativeStatus"] = NativeStatusText();
        values["claudeClient"] = ClientDisplay("claude");
        values["codexClient"] = ClientDisplay("codex");
        values["desktopChatHelp"] = "Choose the original app and enter the chat's thread ID from its link to ask the desktop app to open it.";
        values["desktopChatOrigin"] = DesktopChatOriginPrompt;
        values["desktopChatThreadId"] = "";
        return values;
    }

    internal const string ClaudeWakeConsent = "Claude wake-up uses sensitive local peer credentials. Agent permission controls still apply.";
    // The static explanation rows PANE_TEXT_SHORT holds to two sentences and 280 characters.
    internal static readonly string[] PaneTextRows = ["claudeWakeConsent", "desktopChatHelp"];

    private string NativeStatusText()
    {
        if (snapshot is null) return "No worker status yet.";
        string errors = snapshot.AdapterErrors.Count == 0 ? "No adapter errors reported."
            : string.Join("; ", snapshot.AdapterErrors.Select(x => Safe(x.Key, 32) + ": " + Safe(x.Value, 512)).Take(8));
        // Known adapters have their own client lines. Other adapters still need a visible explanation for an open band.
        var unassigned = ClientNotes().Where(note => note.Adapter is not ("claude-native" or "codex-native")).ToArray();
        if (unassigned.Length == 0) return errors;
        string notes = string.Join("; ", unassigned.Take(8).Select(note => "Adapter note " +
            System.Text.Json.JsonSerializer.Serialize(note.Adapter.Length <= 32 ? note.Adapter : note.Adapter[..32] + "...") +
            ": " + Safe(StripCode(note.Text), 320)));
        return errors + " Notes: " + notes + (unassigned.Length <= 8 ? "" : " (" + Count(unassigned.Length - 8) +
            (unassigned.Length == 9 ? " more adapter note)" : " more adapter notes)"));
    }

    private void SetStatus(string value)
    {
        status = value;
        NotifyInfoChanged();
    }

    // Every Info value the pane reloads live. Order is fixed; equality is ordinal per entry, as the tuple's was.
    private string[] InfoValues() =>
    [
        DisplayedStatus(), NativeStatusText(), PairingDisplayText(), ClientsSummaryText(), McpDisplay().Detail, AgentInboxDisplay().Detail, LanAccessText(),
        ClientDisplay("claude"), ClientDisplay("codex"), ApprovalPromptText(), WaitingMailText()
    ];

    private void NotifyInfoChanged()
    {
        if (shutDown || optionsPane is null) return;
        string[] current = InfoValues();
        if (notifiedInfo is not null && notifiedInfo.SequenceEqual(current, StringComparer.Ordinal)) return;
        notifiedInfo = current;
        optionsPane.NotifyInfoChanged();
    }

    private bool SaveOptions(IReadOnlyDictionary<string, string> values)
    {
        if (shutDown || settingsStore is null || !BrokerSettings.TryEdit(settings, values, out var edited)) return false;
        if (edited == settings) return true;
        var previous = settings.Values();
        foreach (var pair in edited.Values()) settingsStore.Set(pair.Key, pair.Value);
        if (!settingsStore.Save())
        {
            foreach (var pair in previous) settingsStore.Set(pair.Key, pair.Value);
            return false;
        }
        settings = edited;
        Restart();
        return true;
    }

    private void Restart()
    {
        if (shutDown) return;
        CancellationTokenSource? previous = run;
        previous?.Cancel();
        if (previous is not null) _ = pending.ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
        run = new CancellationTokenSource();
        int current = ++generation;
        snapshot = null; ownsWorker = false;
        ResetBrokerList();
        // BSM-4: the previous worker's waiting mail is process-local and never joins a status read after the restart.
        waitingMail = null;
        ResetPairingStatus();
        lanFirewallStatus = "";
        // A check compared the entry with the previous port, worker and data directory; it no longer applies.
        claudeInspection = null; codexInspection = null; clientSetupResults.Clear();
        SetStatus(!settings.Enabled ? "Disabled" : dataDirectory is null ? "Module storage unavailable" :
            !settings.Valid ? "Invalid broker settings" : ui is null ? "UI dispatcher unavailable" : "Starting");
        var selected = settings;
        CancellationToken token = run.Token;
        pending = Task.Run(() => RunAsync(selected, current, token));
    }

    private async Task RunAsync(BrokerSettings selected, int current, CancellationToken token)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        long observation = NextObservation();
        try
        {
            if (token.IsCancellationRequested || !selected.Enabled || dataDirectory is null || !selected.Valid || ui is null) return;
            using Process parent = Process.GetCurrentProcess();
            var launch = new WorkerLaunch(Path.Combine(Path.GetDirectoryName(typeof(AgentBrokerModule).Assembly.Location)!,
                "worker", "AgentBroker.Worker.exe"), dataDirectory, selected, parent.Id,
                parent.StartTime.ToUniversalTime().ToFileTimeUtc());
            string lastApprovalRaw = "";
            Guid? lastApprovalBroker = null;
            while (!token.IsCancellationRequested)
            {
                observation = NextObservation();
                long requestedObservation = observation;
                WorkerStatus state;
                try { state = await supervisor.RefreshAsync(launch, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // A failed observation is not the end of supervision. Keep the exact child
                    // under its supervisor and retry on the normal bounded polling interval.
                    state = new(false, false, "supervision_retry");
                }
                string approvalRaw = Volatile.Read(ref approvalObservationRaw);
                if (state.Connected && NeedsApprovalObservationForwarding(approvalRaw, lastApprovalRaw, lastApprovalBroker, state.Snapshot))
                {
                    // Only the count and original observation time cross this local control pipe. A stale value stays stale.
                    try
                    {
                        var approval = await control.SendAsync(dataDirectory, ApprovalControlRequest(approvalRaw), token).ConfigureAwait(false);
                        if (approval.Success)
                        {
                            lastApprovalRaw = approvalRaw; lastApprovalBroker = state.Snapshot?.BrokerId;
                            if (approval.Snapshot is not null) state = state with { Snapshot = approval.Snapshot };
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException) { }
                }
                // BSM-4 (1.10.5): waiting mail is the worker's separate "waiting-mail" read, after a connected status in this
                // poll and applied in the same generation-guarded post. Any failure is "measurement unavailable" for this poll
                // and never ends supervision.
                ControlResponse? mail = null;
                if (state.Connected && state.Snapshot is not null)
                {
                    try { mail = await supervisor.ReadWaitingMailAsync(launch, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception) { mail = null; }
                }
                Post(current, () => ApplyStatus(state, requestedObservation, mail));
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Post(current, () => {
            if (!AcceptObservation(observation)) return;
            // BSM-4: no status means no measurement, so a later action's status never re-joins this answer.
            snapshot = null; waitingMail = null;
            SetStatus("Worker unavailable. Check the packaged worker and settings.");
        }); }
        finally
        {
            if (dataDirectory is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                try { await supervisor.StopOwnedAsync(dataDirectory, stop.Token).ConfigureAwait(false); } catch (Exception) { }
            }
            lifecycle.Release();
        }
    }

    private void Post(int current, Action action) => ui?.Post(_ =>
    { if (!shutDown && generation == current) action(); }, null);

    private long NextObservation() => Interlocked.Increment(ref nextObservation);

    private bool AcceptObservation(long observation)
    {
        if (observation < lastAppliedObservation) return false;
        lastAppliedObservation = observation;
        return true;
    }

    private void ApplyStatus(WorkerStatus state) => ApplyStatus(state, NextObservation());

    // A status from any other source (Refresh status, an action's answer) keeps the waiting mail already held; the row joins
    // it to the new snapshot by BrokerId and exact address when it is drawn.
    private bool ApplyStatus(WorkerStatus state, long observation) => ApplyStatus(state, observation, waitingMail);

    private bool ApplyStatus(WorkerStatus state, long observation, ControlResponse? mail)
    {
        if (!AcceptObservation(observation)) return false;
        // BSM-4: the poll's waiting-mail answer belongs to this accepted observation and is set before Status notifies, so
        // one poll raises one InfoChanged. No status means no measurement.
        waitingMail = state.Snapshot is null ? null : mail;
        // A standby that takes over, or a worker that reappears on another port, makes every client verdict stale; Restart is
        // not the only way that happens (post-push audit U5). The same endpoint seen again keeps them.
        string? liveEndpoint = state.Snapshot?.LoopbackEndpoint;
        if ((liveEndpoint is not null && verdictEndpoint is not null && liveEndpoint != verdictEndpoint) || state.OwnsWorker != ownsWorker)
        {
            claudeInspection = null; codexInspection = null; clientSetupResults.Clear();
            clientEpoch++;
        }
        if (liveEndpoint is not null) verdictEndpoint = liveEndpoint;
        // A pressed-once revoke belongs to the broker it was shown for; another worker identity or no status disarms it.
        if (state.Snapshot is null || state.Snapshot.BrokerId != snapshot?.BrokerId) revokeArm = null;
        snapshot = state.Snapshot;
        ownsWorker = state.OwnsWorker;
        SetStatus(state.Connected ? (state.OwnsWorker ? "Running" : "Connected to the existing broker (standby)") +
            ". " + (snapshot?.Sessions.Length ?? 0) + " sessions, " + (snapshot?.Paired.Length ?? 0) + " paired brokers."
            : state.Code switch
            {
                "worker_not_packaged" => "The worker is missing from this module package.",
                "worker_exited" => "The worker exited. Retrying with a delay.",
                "worker_start_failed" => "The worker could not start. Retrying with a delay.",
                "restart_backoff" => "Waiting before restarting the worker.",
                "starting" => "Starting",
                "worker_unresponsive" => "Worker is not responding to status requests. Retrying automatically.",
                _ => "Worker status unavailable. Retrying automatically."
            });
        // PANE-POPUP (1.10.5): no question is raised here for a discovered broker or a pairing offer. Discovered brokers
        // and offers show in the Brokers list, an incoming offer also in Pairing and the tray root, and only an
        // explicit press on one row or tray item sends pair-request, pair-approve or pair-reject.
        if (snapshot is null) return true;
        ReconcilePairingStatus();
        return true;
    }

    private IEnumerable<TrayItem> BuildTray()
    {
        var items = new List<TrayItem>
        {
            new() { Label = Safe(status, 120) },
            new() { Label = settings.Enabled ? (ownsWorker ? "Disable AgentBroker" : "Disconnect this companion") : "Enable AgentBroker", Click = () =>
                SaveOptions(new Dictionary<string, string> { ["enabled"] = settings.Enabled ? "false" : "true" }) }
        };
        if (settings.Enabled) items.AddRange(ClientTrayItems());
        if (pairingStatus.Length != 0) items.Add(new() { Label = Safe(pairingStatus, 120) });
        string pairedSummary = PairedTrustSummary();
        if (pairedSummary.Length != 0) items.Add(new() { Label = Safe(pairedSummary, 120) });
        items.Add(new() { Label = "Allow LAN access", Click = () => _ = AllowLanAccessAsync() });
        if (LanProfileMismatch()) items.Add(new() { Label = snapshot!.Lan!.Category == "public"
            ? "This network is Public; the LAN rule covers Private networks only"
            : "This network is a Domain network; the LAN rule covers Private networks only" });
        if (lanFirewallStatus.Length != 0) items.Add(new() { Label = Safe(lanFirewallStatus, 120) });
        BrokerSnapshot? view = snapshot;
        if (view is null) return items;
        foreach (var offer in view.PairingOffers.Take(32))
            items.Add(new() { Label = "Pairing code " + Safe(offer.VerificationCode, 35) + " for " + Safe(StripDisplayIdentity(offer.Peer.Name), 20),
                BuildChildren = () => [new TrayItem { Label = offer.LocalApproved ? "Waiting for the other broker; retry confirmation" : "Codes match; allow sharing",
                    Click = () => _ = SendAsync(new("pair-approve", offer.RequestId)) },
                    new TrayItem { Label = "Cancel pairing", Click = () => _ = SendAsync(new("pair-reject", offer.RequestId)) }] });
        items.Add(new() { Label = "Pair a broker", BuildChildren = () => view.Discovered.Take(32).Select(peer => new TrayItem
            { Label = Safe(StripDisplayIdentity(peer.Name), 60), Click = () => _ = SendAsync(new("pair-request", Peer: peer)) }).ToArray() });
        // No revoke in the tray (owner decision 2026-11-12, D10): removing a pairing is a deliberate action, the pane's two-press
        // Revoke sharing. The paired summary line above still names the paired brokers.
        items.Add(new() { Label = "Sessions", BuildChildren = () => view.Sessions.Take(256).Select(session => new TrayItem
        {
            Label = Safe(session.Label, 60) + " (" + Safe(session.Activity, 24) + ")",
            BuildChildren = () =>
            [
                new TrayItem { Label = session.IsPrivate ? "Make discoverable" : "Make private", Click = () =>
                    _ = SendAsync(new("session-private", SessionId: session.SessionId, Value: !session.IsPrivate)) },
                new TrayItem { Label = session.IsMuted ? "Allow peer messages" : "Mute peer messages", Click = () =>
                    _ = SendAsync(new("session-muted", SessionId: session.SessionId, Value: !session.IsMuted)) }
            ]
        }).ToArray() });
        return items;
    }

    private async Task<ControlResponse?> SendAsync(ControlRequest request)
    {
        string? directory = dataDirectory;
        if (shutDown || !settings.Enabled || directory is null || run is null) return null;
        int current = generation;
        CancellationToken token = run.Token;
        bool pairing = ControlProtocol.IsPairingOperation(request.Operation);
        PairingPeerIdentity? pairingPeer = pairing ? PairingSubject(request, snapshot) : null;
        if (pairing) SetPairingStatus(request.Operation == "pair-request"
            ? "Contacting the other broker. Pairing can take up to 25 seconds."
            : "Sending this workstation's confirmation. Sharing requires both approvals.", false, pairingPeer);
        long requestedObservation = NextObservation();
        try
        {
            ControlResponse response = await control.SendAsync(directory, request, token);
            if (shutDown || generation != current) return null;
            if (response.Success && response.Snapshot is not null)
                ApplyStatus(new(true, ownsWorker, "updated", response.Snapshot), requestedObservation);
            else if (!pairing && (!response.Success || request.Operation == "status"))
                ReportControlFailure(request, "The worker did not return its status.", requestedObservation);
            if (pairing) ReportPairingResult(request, response, pairingPeer);
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException)
        { if (!shutDown && generation == current) {
            if (pairing) SetPairingStatus(PairingFailureText("pairing_outcome_unknown"), true, pairingPeer);
            else ReportControlFailure(request, "The status request timed out.", requestedObservation);
        } }
        catch (Exception)
        { if (!shutDown && generation == current) {
            if (pairing) SetPairingStatus(PairingFailureText("pairing_control_unavailable"), true, pairingPeer);
            else ReportControlFailure(request, "The status connection is unavailable.", requestedObservation);
        } }
        return null;
    }

    private void ReportControlFailure(ControlRequest request, string reason, long observation)
    {
        if (!AcceptObservation(observation)) return;
        if (request.Operation == "status")
        {
            // BSM-4: no status means no measurement, so a later action's status never re-joins the held waiting mail.
            snapshot = null; waitingMail = null;
            SetStatus("Worker unavailable. " + reason + " Retrying automatically.");
        }
        // The read-only "connection" probe (Check client setup, Connect) changes nothing, so its failure must not replace the
        // module status (post-push audit U6). The callers report it where it decides their outcome: Connect Claude MCP stops
        // with "Could not verify the broker connection", and an HTTP entry checks as not verified. Stdio setup and absent or
        // stdio verdicts do not depend on the probe and do not mention it.
        else if (request.Operation != "connection") SetStatus("The broker could not apply that action.");
    }

    private async Task<string> RefreshStatusAsync() => (await SendAsync(new("status"))) is
        { Success: true, Snapshot: not null } ? "Status refreshed."
        : "The broker is unavailable. Try again after it reconnects.";

    private static string Safe(string? value, int limit) => new((value ?? "").Where(c => !char.IsControl(c) && c != '&')
        .Take(limit).ToArray());

    private async Task<ClientSetupOutcome> ConfigureClientAsync(string client)
    {
        if (shutDown || !settings.Enabled || dataDirectory is null || run is null)
            return new("Enable AgentBroker before connecting a client.", false);
        int current = generation, epoch = clientEpoch;
        string directory = dataDirectory;
        CancellationToken token = run.Token;
        ControlResponse? connection = await SendAsync(new("connection"));
        // Post-push audit U5 follow-up: a live endpoint or ownership change during the probe makes its answer obsolete.
        if (shutDown || generation != current || clientEpoch != epoch || token.IsCancellationRequested)
            return new(ClientSetupCancelledBeforeCommand, false);
        if (client == "claude" && !AgentClientSetup.HasVerifiedConnection(connection))
            return new("Could not verify the broker connection. Refresh status, then try Connect Claude MCP again.", false);
        string? endpoint = UsableClientHttpEndpoint(connection);
        string worker = Path.Combine(Path.GetDirectoryName(typeof(AgentBrokerModule).Assembly.Location)!, "worker", "AgentBroker.Worker.exe");
        // The helper re-checks currentness before each add, so a connection that moves during its get or a refused add sends no
        // further add. Its result is not proof that a vendor command completed or was submitted.
        string text = await ClientSetup.ConfigureAsync(client, worker, directory, token,
            loopbackMcpEndpoint: client == "claude" ? endpoint : null,
            inspectionMcpEndpoint: client == "codex" ? endpoint : null,
            httpConnectionVerified: AgentClientSetup.HasVerifiedConnection(connection),
            stillCurrent: () => !shutDown && generation == current && clientEpoch == epoch && !token.IsCancellationRequested);
        // A stale stop already says the connection changed and what was not attempted, so it is final as written:
        // ConfigurationInvoked false means no obsolete-operation suffix and no follow-up check (see ConnectAndCheckAsync).
        return new(text, !AgentClientSetup.IsStaleStop(text));
    }
}
