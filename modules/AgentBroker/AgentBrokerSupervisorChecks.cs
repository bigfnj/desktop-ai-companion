using System.Diagnostics;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

/// <summary>Failure-path lifecycle checks with synthetic children, without starting any process.</summary>
internal static class AgentBrokerSupervisorChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        WorkerLaunch Launch() => new(typeof(AgentBrokerModule).Assembly.Location,
            Path.Combine(Path.GetTempPath(), "agentbroker-supervisor-synthetic"),
            new(true, "Synthetic", 7432, 7433, true, true, false, false, false, false), 1, 1);

        var child = new Child { CancelWait = true };
        var launcher = new Launcher(child);
        var control = new Control { RefusedStatuses = 1, StopRefused = true, Existing = () => launcher.Starts > 0 };
        var supervisor = new WorkerSupervisor(control, launcher);
        WorkerStatus status = await supervisor.RefreshAsync(Launch(), CancellationToken.None).ConfigureAwait(false);
        check(status.Connected && status.OwnsWorker && launcher.Starts == 1,
            "a refused control status does not abandon supervision");
        await supervisor.StopOwnedAsync(Launch().DataDirectory, CancellationToken.None).ConfigureAwait(false);
        check(child.ForcedStops == 1 && child.Disposals == 1 && control.LastRequiredPid == child.Id,
            "refused control and canceled exit waiting still stop and dispose the exact owned child");
        await supervisor.StopOwnedAsync(Launch().DataDirectory, CancellationToken.None).ConfigureAwait(false);
        check(child.ForcedStops == 1 && child.Disposals == 1 && control.Stops == 1,
            "repeated shutdown never touches an already released child");

        child = new Child(); launcher = new Launcher(child);
        control = new Control { Existing = () => true };
        supervisor = new WorkerSupervisor(control, launcher);
        status = await supervisor.RefreshAsync(Launch(), CancellationToken.None).ConfigureAwait(false);
        await supervisor.StopOwnedAsync(Launch().DataDirectory, CancellationToken.None).ConfigureAwait(false);
        check(status.Connected && !status.OwnsWorker && launcher.Starts == 0 && control.Stops == 0 && child.ForcedStops == 0,
            "a companion attached in standby never stops another broker");

        child = new Child { FailWait = true }; launcher = new Launcher(child);
        control = new Control { Existing = () => launcher.Starts > 0 };
        supervisor = new WorkerSupervisor(control, launcher);
        await supervisor.RefreshAsync(Launch(), CancellationToken.None).ConfigureAwait(false);
        bool faultReported = false;
        try { await supervisor.StopOwnedAsync(Launch().DataDirectory, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { faultReported = true; }
        check(faultReported && child.ForcedStops == 1 && child.Disposals == 1,
            "an exit-wait failure is reported after exact-child cleanup");

        child = new Child(); launcher = new Launcher(child);
        control = new Control { Existing = () => launcher.Starts > 0 };
        supervisor = new WorkerSupervisor(control, launcher);
        await supervisor.RefreshAsync(Launch(), CancellationToken.None).ConfigureAwait(false);
        child.FailLiveness = true;
        faultReported = false;
        try { await supervisor.StopOwnedAsync(Launch().DataDirectory, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { faultReported = true; }
        check(faultReported && child.ForcedStops == 1 && child.Disposals == 1,
            "unavailable process metadata cannot bypass cleanup of the retained child handle");

        child = new Child(); launcher = new Launcher(child);
        control = new Control { Existing = () => launcher.Starts > 0, OnStop = () => child.Alive = false };
        supervisor = new WorkerSupervisor(control, launcher);
        await supervisor.RefreshAsync(Launch(), CancellationToken.None).ConfigureAwait(false);
        await supervisor.StopOwnedAsync(Launch().DataDirectory, CancellationToken.None).ConfigureAwait(false);
        check(child.ForcedStops == 0 && child.Disposals == 1,
            "a gracefully stopped child is disposed without forced termination");
    }

    private sealed class Launcher(Child child) : IWorkerChildLauncher
    {
        public int Starts;
        public IWorkerChild Start(ProcessStartInfo start) { Starts++; return child; }
    }
    private sealed class Child : IWorkerChild
    {
        public int Id => 4242;
        public bool Alive = true;
        public bool CancelWait, FailWait, FailLiveness;
        public int ForcedStops, Disposals;
        public bool IsAlive => FailLiveness ? throw new IOException("Synthetic metadata failure.") : Alive;
        public Task WaitForExitAsync(CancellationToken token) => FailWait
            ? Task.FromException(new IOException("Synthetic exit-wait failure."))
            : CancelWait ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask;
        public void StopForcefully() { if (Alive) { ForcedStops++; Alive = false; } }
        public void Dispose() { Disposals++; }
    }
    private sealed class Control : IBrokerControlClient
    {
        public int RefusedStatuses, Stops;
        public bool StopRefused;
        public int? LastRequiredPid;
        public Func<bool> Existing = () => false;
        public Action? OnStop;
        public Task<ControlResponse> SendAsync(string directory, ControlRequest request,
            CancellationToken token, int? requiredServerPid = null)
        {
            LastRequiredPid = requiredServerPid;
            if (request.Operation == "stop")
            {
                Stops++;
                if (StopRefused) throw new UnauthorizedAccessException("Synthetic refused pipe authority.");
                OnStop?.Invoke();
                return Task.FromResult(new ControlResponse(true, "stopping"));
            }
            if (RefusedStatuses-- > 0) throw new UnauthorizedAccessException("Synthetic refused pipe authority.");
            return Task.FromResult(Existing() ? new ControlResponse(true, "ok", new(Guid.NewGuid(), "Synthetic",
                "http://127.0.0.1:7432", null, [], [], [], [], new Dictionary<string, string>())) : new(false, "unavailable"));
        }
    }
}
