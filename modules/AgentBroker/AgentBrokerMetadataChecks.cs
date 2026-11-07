using AgentBroker.Observations;
using DesktopAICompanion.ModuleKit.Testing;
using System.Reflection;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunMetadataChecks(Action<bool, string> check)
    {
        var host = new TestHost();
        var now = DateTimeOffset.UtcNow;
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, new CodexApprovalObservation(2, now).ToJson());
        int Subscribers() => (typeof(RecordingHost).GetField("ContextChanged", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host) as Delegate)?.GetInvocationList().Length ?? 0;
        int baseline = Subscribers();
        var module = new AgentBrokerModule(new TestControl(), new TestSupervisor());
        module.Init(host);
        var pane = host.OptionsPanes.Single();
        check(Subscribers() == baseline + 1 && pane.Load()["approvalPrompts"].Contains("2 visible Codex approval prompts; conversation unknown.", StringComparison.Ordinal),
            "APPROVAL_LATE_LOAD: the module subscribes and reads an observation published before it loaded");
        check(module.approvalExpiry is not null, "APPROVAL_EXPIRY_ARMED: a retained fresh observation has a bounded expiry notification");
        var broker = Guid.NewGuid();
        var retained = new CodexApprovalObservation(2, now).ToJson();
        var emptyWorker = new BrokerSnapshot(broker, "Synthetic", "http://127.0.0.1:7432", null,
            [], [], [], [], new Dictionary<string, string>());
        check(NeedsApprovalObservationForwarding(retained, retained, broker, emptyWorker),
            "APPROVAL_WORKER_RESTART: unchanged retained context is resent when a same-broker worker has lost its observation");
        var confirmedWorker = emptyWorker with { ApprovalPrompts = new("fresh", 2, now) };
        check(!NeedsApprovalObservationForwarding(retained, retained, broker, confirmedWorker),
            "APPROVAL_CONFIRMED_HANDOFF: an unchanged confirmed observation is not sent on every status poll");
        check(NeedsApprovalObservationForwarding("", "", broker, confirmedWorker) &&
            !NeedsApprovalObservationForwarding("", "", broker, emptyWorker),
            "APPROVAL_CLEAR_HANDOFF: an old worker observation is cleared even when this module has no retained publication");
        check(NeedsApprovalObservationForwarding(retained, retained, broker,
                confirmedWorker with { ApprovalPrompts = new("fresh", 1, now) }),
            "APPROVAL_COUNT_HANDOFF: a changed count at the same observation time does not masquerade as the retained value");
        int changes = 0;
        pane.InfoChanged += () => changes++;
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, new CodexApprovalObservation(0, now).ToJson());
        check(changes > 0 && pane.Load()["approvalPrompts"].StartsWith("0 visible", StringComparison.Ordinal),
            "APPROVAL_LIVE_CHANGE: a complete zero observation is shown as zero through the existing context event");
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, new CodexApprovalObservation(9, now.AddMinutes(-1)).ToJson());
        check(pane.Load()["approvalPrompts"].Contains("stale", StringComparison.Ordinal) && !pane.Load()["approvalPrompts"].Contains("9 visible", StringComparison.Ordinal),
            "APPROVAL_STALE: an old observation is unavailable and never presented as a current count");
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, "{\"observedAtUtc\":\"2026-11-06T12:00:00Z\"}");
        check(pane.Load()["approvalPrompts"].StartsWith("Unavailable", StringComparison.Ordinal) && module.ApprovalControlRequest().ApprovalPromptCount is null,
            "APPROVAL_MISSING_COUNT: malformed or incomplete context cannot become a zero observation");
        var home = Path.Combine(Path.GetTempPath(), "agentbroker-explicit-codex-home");
        var defaults = BrokerSettings.Read(host.SettingsFor(Id));
        check(BrokerSettings.TryEdit(defaults, new Dictionary<string, string> { ["additionalCodexHome"] = home }, out var configured) && configured.AdditionalCodexHome == home &&
            !BrokerSettings.TryEdit(defaults, new Dictionary<string, string> { ["additionalCodexHome"] = "relative" }, out _),
            "HOME_SETTING: the optional folder is explicitly saved and relative paths are refused");
        var launch = new WorkerLaunch("synthetic.exe", home, configured, 1, 1);
        var start = new System.Diagnostics.ProcessStartInfo("synthetic.exe");
        WorkerSupervisor.AddArguments(start, launch);
        int flag = start.ArgumentList.IndexOf("--codex-additional-homes");
        check(flag >= 0 && System.Text.Json.JsonSerializer.Deserialize<string[]>(start.ArgumentList[flag + 1])?.SequenceEqual([home]) == true,
            "HOME_WORKER_ARGUMENT: the selected home reaches the worker as one structured path argument");
        module.Shutdown();
        int before = changes;
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, new CodexApprovalObservation(3, now).ToJson());
        check(Subscribers() == baseline && module.approvalExpiry is null && changes == before,
            "APPROVAL_SHUTDOWN: the subscriber and expiry timer are released and late publications cannot update the module");
        RunApprovalRestartForwardingCheck(check, emptyWorker, now);
        RunExtremeApprovalTimestampCheck(check, emptyWorker, now);
    }

    private static void RunExtremeApprovalTimestampCheck(Action<bool, string> check, BrokerSnapshot emptyWorker, DateTimeOffset now)
    {
        var host = new TestHost();
        int Subscribers() => (typeof(RecordingHost).GetField("ContextChanged", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host) as Delegate)?.GetInvocationList().Length ?? 0;
        int baseline = Subscribers();
        string extreme = new CodexApprovalObservation(1, DateTimeOffset.MaxValue).ToJson();
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, extreme);
        var module = new AgentBrokerModule(new TestControl(), new TestSupervisor());
        try
        {
            bool initialized = true;
            try { module.Init(host); }
            catch (ArgumentOutOfRangeException) { initialized = false; }
            var pane = host.OptionsPanes.SingleOrDefault();
            check(initialized && pane is not null && pane.Load()["approvalPrompts"].StartsWith("Unavailable", StringComparison.Ordinal) && module.approvalExpiry is null,
                "APPROVAL_EXTREME_TIMESTAMP_INIT: an extreme retained timestamp cannot overflow module initialization or become a current count");
            if (!initialized || pane is null) return;
            check(module.ApprovalControlRequest() is { ApprovalPromptCount: null, ApprovalObservedAtUtc: null } &&
                host.ReadContext(CodexApprovalObservation.ContextKey) == extreme &&
                !NeedsApprovalObservationForwarding(extreme, extreme, emptyWorker.BrokerId, emptyWorker) &&
                NeedsApprovalObservationForwarding(extreme, extreme, emptyWorker.BrokerId,
                    emptyWorker with { ApprovalPrompts = new("fresh", 2, now) }),
                "APPROVAL_EXTREME_HANDOFF: invalid future context withdraws only the worker observation, preserves source context and stops resending after confirmation");
            host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, new CodexApprovalObservation(2, DateTimeOffset.UtcNow).ToJson());
            check(pane.Load()["approvalPrompts"].StartsWith("2 visible", StringComparison.Ordinal) && module.approvalExpiry is not null,
                "APPROVAL_EXTREME_RECOVERY: a fresh publication still arms ordinary expiry after unavailable retained context");
            bool refreshed = true;
            try { host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, extreme); }
            catch (ArgumentOutOfRangeException) { refreshed = false; }
            check(refreshed && pane.Load()["approvalPrompts"].StartsWith("Unavailable", StringComparison.Ordinal) &&
                module.approvalExpiry is null && module.ApprovalControlRequest().ApprovalPromptCount is null &&
                host.ReadContext(CodexApprovalObservation.ContextKey) == extreme,
                "APPROVAL_EXTREME_TIMESTAMP_UNAVAILABLE: live extreme context cannot overflow expiry or be sent as an observed count");
        }
        finally { module.Shutdown(); }
        check(Subscribers() == baseline && module.approvalExpiry is null,
            "APPROVAL_EXTREME_SHUTDOWN: extreme timestamp handling retains normal subscriber and timer cleanup");
    }

    private static void RunApprovalRestartForwardingCheck(Action<bool, string> check, BrokerSnapshot emptyWorker, DateTimeOffset observedAt)
    {
        var previous = SynchronizationContext.Current;
        var dispatcher = new TestDispatcher();
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        var transport = new ApprovalHandoffFixture(emptyWorker);
        var host = new TestHost();
        host.SettingsFor(Id).Set("enabled", "true");
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-approval-forwarding")));
        host.PublishContext("agentflow", CodexApprovalObservation.ContextKey, new CodexApprovalObservation(2, observedAt).ToJson());
        var module = new AgentBrokerModule(transport, transport);
        try
        {
            module.Init(host);
            Pump(() => transport.Requests.Count > 0, dispatcher);
            transport.View = emptyWorker;
            bool resent = true;
            try { Pump(() => transport.Requests.Count > 1, dispatcher); }
            catch (TimeoutException) { resent = false; }
            var requests = transport.Requests.ToArray();
            check(resent && requests.Length >= 2 && requests.Take(2).All(request =>
                    request.Operation == "approval-observation" && request.ApprovalPromptCount == 2 && request.ApprovalObservedAtUtc == observedAt),
                "APPROVAL_RESTART_RUNTIME_FORWARDING: the running module resends retained count and original time after same-broker observation loss");
        }
        finally
        {
            module.Shutdown();
            try { Pump(() => Volatile.Read(ref transport.Stops) > 0, dispatcher); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private sealed class ApprovalHandoffFixture(BrokerSnapshot initial) : IWorkerSupervisor, IBrokerControlClient
    {
        private readonly object gate = new();
        private BrokerSnapshot view = initial;
        internal BrokerSnapshot View { get { lock (gate) return view; } set { lock (gate) view = value; } }
        internal System.Collections.Concurrent.ConcurrentQueue<ControlRequest> Requests { get; } = new();
        internal int Stops;
        public Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new WorkerStatus(true, true, "running", View)); }
        public Task StopOwnedAsync(string directory, CancellationToken token)
        { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request, CancellationToken token, int? requiredServerPid = null)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                Requests.Enqueue(request);
                view = view with { ApprovalPrompts = new("fresh", request.ApprovalPromptCount, request.ApprovalObservedAtUtc) };
                return Task.FromResult(new ControlResponse(true, "ok", view));
            }
        }
    }
}
