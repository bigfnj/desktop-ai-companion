using System.ComponentModel;
using System.Diagnostics;
using AgentBroker.Firewall;

namespace DesktopAICompanion.AgentBroker;

internal readonly record struct LanFirewallRequest(string Executable, int Port)
{
    internal static bool TryCreate(string moduleAssembly, int port, out LanFirewallRequest request)
    {
        request = default;
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(moduleAssembly) ||
            !Path.IsPathFullyQualified(moduleAssembly) ||
            !string.Equals(Path.GetFileName(moduleAssembly), "AgentBroker.dll", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(moduleAssembly));
            if (directory is null) return false;
            request = new(Path.Combine(directory, "worker", "AgentBroker.Worker.exe"), port);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}

internal enum LanFirewallLaunchOutcome { Exited, Cancelled, TimedOut, InvalidScope, Unavailable, AlreadyPending }
internal readonly record struct LanFirewallLaunchResult(LanFirewallLaunchOutcome Outcome, int? ExitCode = null);
internal interface ILanFirewallLauncher
{
    bool IsPending { get; }
    Task<LanFirewallLaunchResult> AllowAsync(LanFirewallRequest request, CancellationToken token);
}
internal interface ILanFirewallElevationRunner
{
    Task<LanFirewallLaunchResult> RunAsync(LanFirewallRequest request, CancellationToken token);
}

/// <summary>Only the packaged worker can request its fixed local firewall operation. No shell command or script is accepted.</summary>
internal sealed class LanFirewallLauncher : ILanFirewallLauncher
{
    private readonly object launchSync = new();
    private readonly ILanFirewallElevationRunner runner;
    private readonly TimeSpan callerWait;
    private readonly Func<string, bool> inspectWorker;
    private Task<LanFirewallLaunchResult>? inFlight;
    public bool IsPending { get { lock (launchSync) return inFlight is not null && !inFlight.IsCompleted; } }

    internal LanFirewallLauncher() : this(new LanFirewallElevationRunner(), TimeSpan.FromMinutes(2), IsPackagedWorker) { }
    internal LanFirewallLauncher(ILanFirewallElevationRunner runner, TimeSpan callerWait, Func<string, bool> inspectWorker)
    {
        this.runner = runner; this.callerWait = callerWait; this.inspectWorker = inspectWorker;
    }
    internal static ProcessStartInfo StartInfo(LanFirewallRequest request) => new()
    {
        FileName = request.Executable,
        Arguments = FirewallSetupProtocol.Arguments(request.Port),
        WorkingDirectory = Path.GetDirectoryName(request.Executable)!,
        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        ErrorDialog = false
    };

    public async Task<LanFirewallLaunchResult> AllowAsync(LanFirewallRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !LanFirewallRequest.TryCreate(typeof(AgentBrokerModule).Assembly.Location,
                request.Port, out var expected) || request != expected ||
            !inspectWorker(request.Executable)) return new(LanFirewallLaunchOutcome.InvalidScope);
        token.ThrowIfCancellationRequested();
        Task<LanFirewallLaunchResult> launch;
        lock (launchSync)
        {
            // A caller timeout or cancellation does not mean UAC or the elevated child
            // ended. Keep the original task until it really completes, so another click
            // cannot create a second administrator prompt or concurrent rule operation.
            if (inFlight is not null && !inFlight.IsCompleted) return new(LanFirewallLaunchOutcome.AlreadyPending);
            token.ThrowIfCancellationRequested();
            inFlight = launch = RunTrackedAsync(request, token);
        }
        // Bound only this caller's wait. The runner retains its exact child handle until
        // actual exit, including when Windows is still waiting for the UAC answer.
        try { return await launch.WaitAsync(callerWait, token).ConfigureAwait(false); }
        catch (TimeoutException) { return new(LanFirewallLaunchOutcome.TimedOut); }
        finally
        {
            lock (launchSync)
                if (ReferenceEquals(inFlight, launch) && launch.IsCompleted) inFlight = null;
        }
    }

    private async Task<LanFirewallLaunchResult> RunTrackedAsync(LanFirewallRequest request, CancellationToken token)
    {
        try { return await runner.RunAsync(request, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(LanFirewallLaunchOutcome.Unavailable); }
    }

    private static bool IsPackagedWorker(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileName(executable), "AgentBroker.Worker.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), "worker", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string full = Path.GetFullPath(executable);
            string root = Path.GetPathRoot(full)!;
            string current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}

internal sealed class LanFirewallElevationRunner : ILanFirewallElevationRunner
{
    public Task<LanFirewallLaunchResult> RunAsync(LanFirewallRequest request, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        Process? child = null;
        try
        {
            child = Process.Start(LanFirewallLauncher.StartInfo(request));
            if (child is null) return new LanFirewallLaunchResult(LanFirewallLaunchOutcome.Unavailable);
            // No PID lookup and no kill. The owner may still be considering UAC, and a
            // caller timeout cannot safely claim an elevated operation was cancelled.
            await child.WaitForExitAsync().ConfigureAwait(false);
            return new(LanFirewallLaunchOutcome.Exited, child.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return new(LanFirewallLaunchOutcome.Cancelled); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return new(LanFirewallLaunchOutcome.Unavailable); }
        finally { child?.Dispose(); }
    }, CancellationToken.None);
}
