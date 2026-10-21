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
    private readonly HashSet<Guid> askedOffers = [];
    private IHost? host;
    private IModuleSettings? settingsStore;
    private ITrayQuestions? questions;
    private SynchronizationContext? ui;
    private BrokerSettings settings = new(false, "AgentBroker", 7432, 7433, true, true, true, true, false, false);
    private BrokerSnapshot? snapshot;
    private CancellationTokenSource? run;
    private Task pending = Task.CompletedTask;
    private string? dataDirectory;
    private string status = "Disabled";
    private int generation;
    private bool ownsWorker;
    private bool shutDown;
    internal AgentClientSetup ClientSetup = new(new AgentClientCommand());

    public AgentBrokerModule() : this(new BrokerControlClient(), null) { }
    internal AgentBrokerModule(IBrokerControlClient control, IWorkerSupervisor? supervisor)
    { this.control = control; this.supervisor = supervisor ?? new WorkerSupervisor(control); }

    public ModuleInfo Info => new()
    {
        Id = "agentbroker",
        Name = "AgentBroker",
        Version = "1.0.1",
        MinHostVersion = "1.6.0",
        Permissions = ModulePermissions.Network | ModulePermissions.Storage | ModulePermissions.LaunchProcess |
            ModulePermissions.AgentTranscripts
    };

    public void Init(IHost host)
    {
        if (this.host is not null) throw new InvalidOperationException("Module already initialized.");
        this.host = host;
        ui = SynchronizationContext.Current;
        questions = host as ITrayQuestions;
        settingsStore = host.GetSettings(Id) ?? new DesktopAICompanion.ModuleKit.MemoryModuleSettings();
        settings = BrokerSettings.Read(settingsStore);
        string? storage = host.GetStorage(Id)?.DataDirectory;
        if (storage is not null && Path.IsPathFullyQualified(storage)) dataDirectory = Path.GetFullPath(storage);
        host.AddOptionsPane(BuildOptions());
        host.AddTrayItems([new TrayItem { Label = "AgentBroker", Group = 30, Order = 0, BuildChildren = BuildTray,
            IconPng = DesktopAICompanion.ModuleKit.EmbeddedResources.LoadBytes(typeof(AgentBrokerModule).Assembly, "agentbroker.png") }]);
        host.HostShutdown += Shutdown;
        Restart();
    }

    public void Shutdown()
    {
        if (shutDown) return;
        shutDown = true; generation++;
        if (host is not null) host.HostShutdown -= Shutdown;
        run?.Cancel();
        CancellationTokenSource? stopped = run;
        _ = pending.ContinueWith(_ => stopped?.Dispose(), TaskScheduler.Default);
        // RunAsync's finally stops our own child off the UI thread. Parent PID/start binding is
        // the worker's last cleanup guard if host exit prevents this continuation.
        host = null; questions = null; snapshot = null;
    }

    private OptionsPane BuildOptions() => new()
    {
        Title = "AgentBroker",
        Schema =
        [
            new() { Id = "status", Label = "Status", Kind = SettingKind.Info, Group = "AgentBroker", FullWidth = true, PinTop = true },
            new() { Id = "enabled", Label = "Enable AgentBroker", Kind = SettingKind.Bool, Group = "AgentBroker" },
            new() { Id = "name", Label = "Broker name", Kind = SettingKind.Text, Group = "Connections" },
            new() { Id = "loopbackPort", Label = "Local port", Kind = SettingKind.Int, Min = 1, Max = 65535, Group = "Connections" },
            new() { Id = "noAuthLoopback", Label = "Allow local connections without a token", Kind = SettingKind.Bool, Group = "Connections" },
            new() { Id = "enableLan", Label = "Enable LAN discovery and pairing", Kind = SettingKind.Bool, Group = "Connections" },
            new() { Id = "lanPort", Label = "LAN port", Kind = SettingKind.Int, Min = 1, Max = 65535, Group = "Connections", EnabledWhen = "enableLan=true" },
            new() { Id = "sharing", Label = "Sharing", Kind = SettingKind.Info, Group = "Connections" },
            new() { Id = "manualPeer", Label = "Broker invitation (optional)", Kind = SettingKind.Text, Group = "Connections", FullWidth = true },
            new() { Id = "codexDiscovery", Label = "Discover open Codex sessions", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "claudeDiscovery", Label = "Discover open Claude sessions", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "codexPeerMessages", Label = "Enable Codex peer messages", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "claudePeerCredentials", Label = "Enable Claude native peer credentials", Kind = SettingKind.Bool, Group = "Coding agents" },
            new() { Id = "nativePrivacy", Label = "Native delivery", Kind = SettingKind.Info, Group = "Coding agents" },
            new() { Id = "clientSetup", Label = "Client connection", Kind = SettingKind.Info, Group = "Coding agents" },
            new() { Id = "nativeStatus", Label = "Native adapters", Kind = SettingKind.Info, Group = "Coding agents" }
        ],
        Load = LoadOptions, Save = SaveOptions,
        Actions = [new PaneAction { Label = "Refresh status", Group = "AgentBroker", ReloadPaneAfter = true,
            InvokeAsync = async () => (await SendAsync(new("status")))?.Success == true
                ? "Status refreshed." : "The broker is unavailable. Try again after it reconnects." },
            new PaneAction { Label = "Connect Codex MCP", Group = "Coding agents", InvokeAsync = () => ConfigureClientAsync("codex") },
            new PaneAction { Label = "Connect Claude MCP", Group = "Coding agents", InvokeAsync = () => ConfigureClientAsync("claude") },
            new PaneAction { Label = "Copy broker invitation", Group = "Connections", InvokeAsync = CopyInvitationAsync },
            new PaneAction { Label = "Pair from invitation", Group = "Connections", InvokeWithPendingAsync = PairInvitationAsync }]
    };

    private IReadOnlyDictionary<string, string> LoadOptions()
    {
        var values = settings.Values();
        values["status"] = status;
        values["sharing"] = "Pair brokers from the tray. Pairing shares current and future sessions. Mark a session private to hide it, or mute it to stop peer messages. LAN connections always authenticate.";
        values["manualPeer"] = "";
        values["nativePrivacy"] = "Peer delivery is off until explicitly enabled. Claude native delivery accesses local peer credentials. Existing agent permission controls still apply.";
        values["clientSetup"] = "Connect MCP once per client to enable receipt and reply tools. The buttons add this module's stdio bridge to your user configuration; connection tokens stay in memory. Reconnect MCP or restart the client afterward.";
        values["nativeStatus"] = snapshot is null ? "No worker status yet." : snapshot.AdapterErrors.Count == 0
            ? "No adapter errors reported."
            : string.Join("; ", snapshot.AdapterErrors.Select(x => Safe(x.Key, 32) + ": " + Safe(x.Value, 120)).Take(8));
        return values;
    }

    private bool SaveOptions(IReadOnlyDictionary<string, string> values)
    {
        if (shutDown || settingsStore is null || !BrokerSettings.TryEdit(settings, values, out var edited)) return false;
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
        snapshot = null; ownsWorker = false; askedOffers.Clear();
        ResetDiscoveryPrompts();
        status = !settings.Enabled ? "Disabled" : dataDirectory is null ? "Module storage unavailable" :
            !settings.Valid ? "Invalid broker settings" : ui is null ? "UI dispatcher unavailable" : "Starting";
        var selected = settings;
        CancellationToken token = run.Token;
        pending = Task.Run(() => RunAsync(selected, current, token));
    }

    private async Task RunAsync(BrokerSettings selected, int current, CancellationToken token)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (token.IsCancellationRequested || !selected.Enabled || dataDirectory is null || !selected.Valid || ui is null) return;
            using Process parent = Process.GetCurrentProcess();
            var launch = new WorkerLaunch(Path.Combine(Path.GetDirectoryName(typeof(AgentBrokerModule).Assembly.Location)!,
                "worker", "AgentBroker.Worker.exe"), dataDirectory, selected, parent.Id,
                parent.StartTime.ToUniversalTime().ToFileTimeUtc());
            while (!token.IsCancellationRequested)
            {
                WorkerStatus state = await supervisor.RefreshAsync(launch, token).ConfigureAwait(false);
                Post(current, () => ApplyStatus(state));
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Post(current, () => status = "Worker unavailable. Check the packaged worker and settings."); }
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

    private void ApplyStatus(WorkerStatus state)
    {
        snapshot = state.Snapshot;
        ownsWorker = state.OwnsWorker;
        status = state.Connected ? (state.OwnsWorker ? "Running" : "Connected to the existing broker (standby)") +
            ". " + (snapshot?.Sessions.Length ?? 0) + " sessions, " + (snapshot?.Paired.Length ?? 0) + " paired brokers."
            : state.Code switch
            {
                "worker_not_packaged" => "The worker is missing from this module package.",
                "worker_exited" => "The worker exited. Retrying with a delay.",
                "restart_backoff" => "Waiting before restarting the worker.", _ => "Starting"
            };
        if (snapshot is null) return;
        askedOffers.RemoveWhere(id => !snapshot.PairingOffers.Any(o => o.RequestId == id));
        foreach (var offer in snapshot.PairingOffers.Where(o => !o.LocalApproved && o.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(10)).Take(32))
            if (askedOffers.Add(offer.RequestId)) _ = AskPairingAsync(offer, generation, run!.Token);
        AskAboutDiscovery(snapshot);
    }

    private IEnumerable<TrayItem> BuildTray()
    {
        var items = new List<TrayItem>
        {
            new() { Label = Safe(status, 120) },
            new() { Label = settings.Enabled ? (ownsWorker ? "Disable AgentBroker" : "Disconnect this companion") : "Enable AgentBroker", Click = () =>
                SaveOptions(new Dictionary<string, string> { ["enabled"] = settings.Enabled ? "false" : "true" }) }
        };
        BrokerSnapshot? view = snapshot;
        if (view is null) return items;
        foreach (var offer in view.PairingOffers.Take(32))
            items.Add(new() { Label = "Pairing code " + Safe(offer.VerificationCode, 35) + " for " + Safe(offer.Peer.Name, 20),
                BuildChildren = () => [new TrayItem { Label = offer.LocalApproved ? "Waiting for the other broker; retry confirmation" : "Codes match; allow sharing",
                    Click = () => _ = SendAsync(new("pair-approve", offer.RequestId)) },
                    new TrayItem { Label = "Cancel pairing", Click = () => _ = SendAsync(new("pair-reject", offer.RequestId)) }] });
        items.Add(new() { Label = "Pair a broker", BuildChildren = () => view.Discovered.Take(32).Select(peer => new TrayItem
            { Label = Safe(peer.Name, 60), Click = () => _ = SendAsync(new("pair-request", Peer: peer)) }).ToArray() });
        items.Add(new() { Label = "Paired brokers", BuildChildren = () => view.Paired.Take(32).Select(peer => new TrayItem
            { Label = Safe(peer.Name, 60), BuildChildren = () => [new TrayItem { Label = "Revoke sharing", Click = () => _ = SendAsync(new("peer-revoke", peer.BrokerId)) }] }).ToArray() });
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

    private async Task AskPairingAsync(PairingView offer, int current, CancellationToken token)
    {
        var questionHost = questions;
        if (questionHost is null || shutDown) return;
        try
        {
            string name = Safe(offer.Peer.Name, 20);
            string code = Safe(offer.VerificationCode, 35);
            TrayAnswer answer = await questionHost.AskAsync(Id, new TrayQuestion
            {
                Key = "pair." + offer.RequestId.ToString("N"), Title = "Share with " + name,
                Text = "Match the full code on both brokers:\n" + code + "\nShare current and future discoverable sessions with " + name +
                    "? Both workstations must confirm. Private sessions stay hidden; muted sessions cannot receive.",
                Choices = [new("allow", "Allow sharing"), new("reject", "Reject")], TimeoutChoiceId = "reject",
                Timeout = TimeSpan.FromMinutes(2), Deadline = offer.ExpiresAt
            }, token);
            if (shutDown || generation != current || token.IsCancellationRequested) return;
            bool approved = answer.Outcome == TrayQuestionOutcome.Chosen && answer.ChoiceId == "allow";
            if (approved || answer.Outcome is TrayQuestionOutcome.Chosen or TrayQuestionOutcome.TimedOut or TrayQuestionOutcome.Expired)
                await SendAsync(new(approved ? "pair-approve" : "pair-reject", offer.RequestId));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!shutDown && generation == current)
                status = "Pairing confirmation unavailable. Compare codes and confirm from the tray menu.";
        }
    }

    private async Task<ControlResponse?> SendAsync(ControlRequest request)
    {
        string? directory = dataDirectory;
        if (shutDown || !settings.Enabled || directory is null || run is null) return null;
        int current = generation;
        try
        {
            ControlResponse response = await control.SendAsync(directory, request, run.Token);
            if (shutDown || generation != current) return null;
            if (response.Snapshot is not null) ApplyStatus(new(true, ownsWorker, "updated", response.Snapshot));
            else if (!response.Success) status = "The broker could not apply that action.";
            return response;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!shutDown && generation == current) status = "The broker could not apply that action."; }
        return null;
    }

    private static string Safe(string? value, int limit) => new((value ?? "").Where(c => !char.IsControl(c) && c != '&')
        .Take(limit).ToArray());

    private Task<string> ConfigureClientAsync(string client)
    {
        if (shutDown || !settings.Enabled || dataDirectory is null || run is null)
            return Task.FromResult("Enable AgentBroker before connecting a client.");
        string worker = Path.Combine(Path.GetDirectoryName(typeof(AgentBrokerModule).Assembly.Location)!, "worker", "AgentBroker.Worker.exe");
        return ClientSetup.ConfigureAsync(client, worker, dataDirectory, run.Token);
    }
}
