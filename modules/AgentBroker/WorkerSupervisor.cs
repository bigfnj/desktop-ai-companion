using System.Diagnostics;
using System.Globalization;
using AgentBroker.Protocol;

namespace DesktopAICompanion.AgentBroker;

internal sealed record WorkerLaunch(string Executable, string DataDirectory, BrokerSettings Settings,
    int ParentPid, long ParentCreationFileTime);
internal sealed record WorkerStatus(bool Connected, bool OwnsWorker, string Code, BrokerSnapshot? Snapshot = null);

internal interface IWorkerSupervisor
{
    Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token);
    Task StopOwnedAsync(string dataDirectory, CancellationToken token);
}

internal sealed class WorkerSupervisor(IBrokerControlClient control, IWorkerChildLauncher? launcher = null,
    TimeProvider? clock = null) : IWorkerSupervisor
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IWorkerChildLauncher launcher = launcher ?? new WorkerChildLauncher();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private IWorkerChild? owned;
    private DateTimeOffset retryAfter;
    private long ownedStarted;
    private bool ownedResponded;
    private int failures;

    public async Task<WorkerStatus> RefreshAsync(WorkerLaunch launch, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BrokerSnapshot? existing;
            if (IsOwnedAlive())
            {
                existing = await StatusAsync(launch.DataDirectory, token, owned!.Id).ConfigureAwait(false);
                if (existing is not null)
                {
                    ownedResponded = true; failures = 0;
                    return new(true, true, "running", existing);
                }
                // A transient failure is not proof that another worker owns the pipe. An unbound
                // retry can reach this SAME child and must never cause its forced termination.
                return new(false, true, ownedResponded || clock.GetElapsedTime(ownedStarted) >= StartupGrace
                    ? "worker_unresponsive" : "starting");
            }
            existing = await StatusAsync(launch.DataDirectory, token).ConfigureAwait(false);
            if (existing is not null)
            {
                owned?.Dispose(); owned = null;
                return new(true, false, "attached_standby", existing);
            }
            if (owned is not null && !IsOwnedAlive())
            {
                ReleaseExited();
                retryAfter = clock.GetUtcNow().AddSeconds(Math.Min(60, 2 << Math.Min(5, failures++)));
                return new(false, false, "worker_exited");
            }
            if (clock.GetUtcNow() < retryAfter) return new(false, false, "restart_backoff");
            if (!File.Exists(launch.Executable)) return new(false, false, "worker_not_packaged");
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(launch.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launch.Executable)!
            };
            AddArguments(start, launch);
            owned = launcher.Start(start);
            ownedStarted = clock.GetTimestamp();
            ownedResponded = false;
            // Cancellation after launch still leaves this instance owning its exact child, so shutdown
            // can stop it. The outer poll, rather than many inline pipe timeouts, observes readiness.
            // The worker also watches the parent's PID and creation FILETIME.
            if (!IsOwnedAlive())
            {
                ReleaseExited();
                retryAfter = clock.GetUtcNow().AddSeconds(Math.Min(60, 2 << Math.Min(5, failures++)));
                return new(false, false, "worker_exited");
            }
            return new(false, true, "starting");
        }
        finally { gate.Release(); }
    }

    public async Task StopOwnedAsync(string dataDirectory, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            IWorkerChild? child = owned;
            if (child is null) { failures = 0; retryAfter = default; return; }
            try
            {
                if (child.IsAlive)
                {
                    try { await control.SendAsync(dataDirectory, new("stop"), token, child.Id).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        OperationCanceledException or System.Text.Json.JsonException) { }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(4));
                    try { await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                // Refused control, canceled waiting, and metadata failures all converge here. The
                // retained process handle can only stop our original child, never a standby or PID reuse.
                try { child.StopForcefully(); }
                finally { child.Dispose(); owned = null; failures = 0; retryAfter = default; }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<BrokerSnapshot?> StatusAsync(string directory, CancellationToken token, int? expectedPid = null)
    {
        try
        {
            ControlResponse response = await control.SendAsync(directory, new("status"), token, expectedPid).ConfigureAwait(false);
            return response.Success ? response.Snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            OperationCanceledException or System.Text.Json.JsonException)
        { token.ThrowIfCancellationRequested(); return null; }
    }

    private bool IsOwnedAlive()
    {
        return owned?.IsAlive == true;
    }
    private void ReleaseExited() { if (owned is not null && !IsOwnedAlive()) { owned.Dispose(); owned = null; } }

    internal static void AddArguments(ProcessStartInfo start, WorkerLaunch launch)
    {
        void Add(string key, object value) { start.ArgumentList.Add(key); start.ArgumentList.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        static string Bool(bool value) => value ? "true" : "false";
        Add("--data", launch.DataDirectory); Add("--name", launch.Settings.Name);
        Add("--loopback-port", launch.Settings.LoopbackPort); Add("--lan-port", launch.Settings.LanPort);
        Add("--lan", Bool(launch.Settings.EnableLan));
        Add("--allow-unauthenticated-loopback", Bool(launch.Settings.NoAuthLoopback));
        Add("--parent-pid", launch.ParentPid); Add("--parent-start", launch.ParentCreationFileTime);
        Add("--codex-discovery", Bool(launch.Settings.CodexDiscovery));
        Add("--claude-discovery", Bool(launch.Settings.ClaudeDiscovery));
        Add("--codex-peer-messages", Bool(launch.Settings.CodexPeerMessages));
        Add("--claude-peer-credentials", Bool(launch.Settings.ClaudePeerCredentials));
        Add("--message-lifetime-minutes", launch.Settings.MessageLifetimeHours * 60);
    }
}
