namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunSupervisionRecoveryChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-supervision-synthetic")));
        host.SettingsFor(Id).Set("enabled", "true");
        var supervisor = new TestSupervisor { RefreshFaults = 1 };
        var module = new AgentBrokerModule(new TestControl(), supervisor);
        try
        {
            module.Init(host);
            Pump(() => supervisor.Launches.Count > 0 || module.pending.IsCompleted, dispatcher);
            check(supervisor.Launches.Count > 0 && !module.pending.IsCompleted,
                "SUPERVISION_REFRESH_RECOVERS: one failed supervisor observation does not end the running loop");
        }
        finally
        {
            module.Shutdown();
            Pump(() => module.pending.IsCompleted, dispatcher);
        }
    }
}
