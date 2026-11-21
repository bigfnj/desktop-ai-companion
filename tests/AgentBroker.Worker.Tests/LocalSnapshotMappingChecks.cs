using System.Security.Cryptography;
using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

internal static class LocalSnapshotMappingChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: LOCAL_SNAPSHOT_MAPPING_EXECUTED");
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "snapshot-mapping-" + Guid.NewGuid().ToString("N"));
        string? keyName = null;
        try
        {
            var adapter = new MappingAdapter();
            await using var worker = await WorkerApplication.CreateAsync(new WorkerOptions(root, "Synthetic mapping", LoopbackPort: 0,
                EnableLan: false) { EnableCodexNativeDiscovery = false, EnableClaudeNativeDiscovery = false }, [adapter]);
            keyName = BrokerIdentity.KeyNameFor(root, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            for (int i = 0; i < 100 && (worker.Runtime.Store.Directory().Count == 0 ||
                !worker.Runtime.AdapterNotes.ContainsKey("codex-native")); i++) await Task.Delay(25);
            worker.Runtime.SetSessionPreference(adapter.SessionId, true, true);
            await using var control = new LocalControlServer(worker);
            ControlResponse response = await control.HandleAsync(new("status"), default);
            var snapshot = response.Snapshot;
            var mapped = snapshot?.Sessions.SingleOrDefault(session => session.SessionId == adapter.SessionId);
            check(response.Success && snapshot?.BrokerId == worker.Runtime.Store.BrokerId &&
                mapped is { Label: "Synthetic mapped row", Adapter: "codex-native", Activity: "Interrupted",
                    CanReceive: true, CanWake: true, IsPrivate: true, IsMuted: true,
                    Engine: { Bound: true, Reason: "code_engine" } } &&
                mapped.Incarnation == adapter.Incarnation,
                "LOCAL_CONTROL_SESSION_MAPPING: actual status handler maps identity, activity, capabilities, preferences and engine report");
            check(snapshot?.AdapterNotes?.TryGetValue("codex-native", out string? note) == true &&
                note.Contains("Synthetic publisher note", StringComparison.Ordinal) && snapshot.AdapterErrors.Count == 0,
                "LOCAL_CONTROL_NOTE_MAPPING: actual status handler preserves adapter notes separately from errors");
            check(snapshot?.PeerClocks is null,
                "LOCAL_CONTROL_CLOCK_ABSENT: actual status omits peer-clock data while no peer is paired");
            Guid peer = Guid.NewGuid();
            worker.Runtime.Store.AuthorizePeer(new(peer, "Synthetic uncontacted peer", "https://192.0.2.15/",
                new string('A', 64), DateTimeOffset.UtcNow));
            var withPeer = await control.HandleAsync(new("status"), default);
            check(withPeer.Snapshot?.PeerClocks is { Length: 1 } clocks && clocks[0] is
                { State: "not_checked", OffsetSeconds: null, CheckedUtc: null, Warning: false } && clocks[0].BrokerId == peer,
                "LOCAL_CONTROL_CLOCK_MAPPING: actual status maps an uncontacted paired clock as unknown rather than zero");
        }
        finally
        {
            if (keyName is not null)
            {
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                key.Delete();
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class MappingAdapter : ILocalAgentAdapter
    {
        internal Guid Incarnation { get; } = Guid.NewGuid();
        internal string SessionId { get; } = "codex:" + Guid.NewGuid().ToString("D");
        public string Id => "codex-native";
        public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics =>
            [new("codex_update_tolerant_delivery", "Synthetic publisher note") { Note = true }];
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<BrokerSession>>([new(new(brokerId, SessionId, Incarnation),
                "Synthetic mapped row", Id, SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
                SessionActivity.ApprovalRequired, DateTimeOffset.UtcNow.AddMinutes(1))]);
        public EngineBinding? EngineBindingOf(SessionAddress address) => new(true, "code_engine", DateTimeOffset.UtcNow);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) =>
            throw new InvalidOperationException("Snapshot mapping must not submit a turn.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
