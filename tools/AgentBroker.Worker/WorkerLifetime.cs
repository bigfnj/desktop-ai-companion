namespace AgentBroker.Worker;

internal static class WorkerLifetime
{
    internal static async Task<int> WaitForShutdownAsync(WorkerApplication worker, TextWriter error)
    {
        await worker.Application.WaitForShutdownAsync();
        // Runtime settlement can still fail while shutdown drains an in-flight adapter. Observe the final
        // fatal status only after that drain; the later await-using disposal is idempotent.
        await worker.DisposeAsync();
        if (!worker.Runtime.DurableStateUnavailable) return 0;
        // Never emit an exception message, path, native input, token or peer-supplied value.
        await error.WriteLineAsync("agentbroker: durable_state_unavailable");
        return 73;
    }
}
