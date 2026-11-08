using System.Diagnostics;

namespace AgentBroker.Worker;

internal static class WorkerLifetime
{
    internal const int CertificateRestartExitCode = 75;

    internal static bool RenewalIsDue(DateTimeOffset now, DateTimeOffset due) => now >= due;

    internal static bool RenewalProgressed(DateTimeOffset? previous, DateTimeOffset current, DateTimeOffset now) =>
        current > now && (previous is null || current > previous.Value);

    internal static int ShutdownExitCode(bool durableFailure, bool renewalRequested, bool externalCancellation, bool parentAlive,
        bool cleanupFailure = false) =>
        durableFailure ? 73 : cleanupFailure ? 1 : renewalRequested && !externalCancellation && parentAlive ? CertificateRestartExitCode : 0;

    internal static bool ParentIsAlive(WorkerOptions options)
    {
        if (options.ParentPid == 0) return true;
        try
        {
            using var parent = Process.GetProcessById(options.ParentPid);
            return !parent.HasExited && parent.StartTime.ToUniversalTime().ToFileTimeUtc() == options.ParentCreationFileTime;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal static async Task<int> WaitForShutdownAsync(WorkerApplication worker, TextWriter error,
        CancellationToken externalCancellation = default)
    {
        await worker.Application.WaitForShutdownAsync(externalCancellation);
        // Runtime settlement can still fail while shutdown drains an in-flight adapter. Observe the final
        // fatal status only after that drain; the later await-using disposal is idempotent.
        await worker.DisposeAsync();
        int result = ShutdownExitCode(worker.Runtime.DurableStateUnavailable, worker.CertificateRenewalRequested,
            externalCancellation.IsCancellationRequested || worker.ExternalStopRequested,
            ParentIsAlive(worker.Options), worker.ShutdownCleanupFailed);
        if (result != 73) return result;
        // Never emit an exception message, path, native input, token or peer-supplied value.
        await error.WriteLineAsync("agentbroker: durable_state_unavailable");
        return 73;
    }
}
