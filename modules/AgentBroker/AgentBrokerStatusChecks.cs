using AgentBroker.Protocol;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    /// <summary>Real options/status producers with synthetic control, storage and supervision only.</summary>
    private static void RunStatusChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-status-synthetic")));
        var view = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null,
            [], [], [], [], new Dictionary<string, string>());
        var control = new StatusControl { Response = new(true, "ok", view) };
        var supervisor = new StatusSupervisor(view);
        var module = new AgentBrokerModule(control, supervisor);
        try
        {
            module.Init(host);
            var pane = host.OptionsPanes.Single();
            int notifications = 0;
            string observedStatus = "", observedNative = "";
            pane.InfoChanged += () => {
                notifications++;
                var values = pane.Load();
                observedStatus = values["status"]; observedNative = values["nativeStatus"];
            };
            check(pane.Save(new Dictionary<string, string> { ["enabled"] = "true" }),
                "status: enabling an idle synthetic module saves its actual setting change");
            Pump(() => pane.Load()["status"].StartsWith("Running", StringComparison.Ordinal), dispatcher);
            check(observedStatus.StartsWith("Running", StringComparison.Ordinal) && observedNative == "No adapter errors reported.",
                "status: background readiness notifies the existing pane with both fresh Info values");

            int generation = module.generation;
            int saved = host.SettingsFor(Id).SaveCount;
            int previousNotifications = notifications;
            int stops = supervisor.Stops;
            var lifetime = module.run;
            var snapshot = module.snapshot;
            check(pane.Save(pane.Load()) && pane.Save(new Dictionary<string, string>()) &&
                host.SettingsFor(Id).SaveCount == saved && module.generation == generation &&
                ReferenceEquals(module.run, lifetime) && ReferenceEquals(module.snapshot, snapshot) &&
                notifications == previousNotifications && supervisor.Stops == stops,
                "status: unchanged full or empty saves preserve data, generation, snapshot, lifetime and worker");
            module.ApplyStatus(new(true, false, "attached_standby", view));
            module.ApplyStatus(new(true, false, "attached_standby", view));
            check(notifications == previousNotifications + 1 && observedStatus.StartsWith("Connected", StringComparison.Ordinal),
                "status: changed ownership notifies once and identical poll results do not notify repeatedly");
            previousNotifications = notifications;
            var errorView = view with { AdapterErrors = new Dictionary<string, string> { ["codex-native"] = "adapter_unavailable" } };
            module.ApplyStatus(new(true, false, "attached_standby", errorView));
            module.ApplyStatus(new(true, false, "attached_standby", errorView));
            check(notifications == previousNotifications + 1 && observedNative == "codex-native: adapter_unavailable",
                "status: native-adapter changes notify even when main status is identical, without duplicate notifications");

            module.ApplyStatus(new(false, true, "worker_unresponsive"));
            check(pane.Load()["status"].Contains("not responding", StringComparison.Ordinal) &&
                pane.Load()["status"] != "Starting" && pane.Load()["nativeStatus"] == "No worker status yet.",
                "status: a live unresponsive worker has an explicit unavailable state and no stale adapter result");

            PaneAction refresh = pane.Actions.Single(action => action.Label == "Refresh status");
            module.ApplyStatus(new(false, true, "starting"));
            control.Failure = new OperationCanceledException("Synthetic per-request timeout.");
            previousNotifications = notifications;
            string result = refresh.InvokeAsync().GetAwaiter().GetResult();
            check(result.Contains("unavailable", StringComparison.Ordinal) && observedStatus.Contains("timed out", StringComparison.Ordinal) &&
                observedStatus != "Starting" && notifications == previousNotifications + 1 &&
                module.generation == generation && !lifetime!.IsCancellationRequested &&
                host.SettingsFor(Id).SaveCount == saved && control.Requests.SequenceEqual(new[] { "status" }),
                "status: refresh timeout updates the visible Info, sends only status, and neither saves nor restarts");

            control.Failure = new IOException("Synthetic unavailable control pipe.");
            result = refresh.InvokeAsync().GetAwaiter().GetResult();
            check(result.Contains("unavailable", StringComparison.Ordinal) && pane.Load()["status"].Contains("connection is unavailable", StringComparison.Ordinal),
                "status: refresh connection failure replaces stale startup text with a truthful failure");
            control.Failure = null;
            control.Response = new(false, "synthetic_refusal");
            result = refresh.InvokeAsync().GetAwaiter().GetResult();
            check(result.Contains("unavailable", StringComparison.Ordinal) && pane.Load()["status"].Contains("did not return", StringComparison.Ordinal),
                "status: a refused status response is not shown as successful refresh");
            control.Response = new(true, "ok");
            result = refresh.InvokeAsync().GetAwaiter().GetResult();
            check(result.Contains("unavailable", StringComparison.Ordinal) && pane.Load()["nativeStatus"] == "No worker status yet.",
                "status: a successful response without a snapshot cannot claim status was refreshed");
            control.Response = new(true, "ok", view);
            result = refresh.InvokeAsync().GetAwaiter().GetResult();
            check(result == "Status refreshed." && observedStatus.StartsWith("Running", StringComparison.Ordinal) &&
                observedNative == "No adapter errors reported." && module.generation == generation &&
                host.SettingsFor(Id).SaveCount == saved && supervisor.Stops == stops,
                "status: explicit refresh recovers live Info on the same module lifetime without saving or restarting");

            control.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> lateRefresh = refresh.InvokeAsync();
            module.Shutdown();
            previousNotifications = notifications;
            control.Pending.SetResult(new(true, "ok", errorView));
            Pump(() => lateRefresh.IsCompleted && module.pending.IsCompleted, dispatcher);
            check(notifications == previousNotifications && module.snapshot is null,
                "status: a late control result after shutdown cannot notify a closed pane or restore stale status");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }

    private sealed class StatusControl : IBrokerControlClient
    {
        internal ControlResponse Response = new(false, "unavailable");
        internal Exception? Failure;
        internal TaskCompletionSource<ControlResponse>? Pending;
        internal List<string> Requests { get; } = [];
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request,
            CancellationToken token, int? requiredServerPid = null)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.Operation);
            if (Failure is not null) return Task.FromException<ControlResponse>(Failure);
            return Pending?.Task ?? Task.FromResult(Response);
        }
    }

    private sealed class StatusSupervisor(BrokerSnapshot view) : IWorkerSupervisor
    {
        internal int Stops;
        public Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new WorkerStatus(true, true, "running", view)); }
        public Task StopOwnedAsync(string directory, CancellationToken token)
        { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
    }
}
