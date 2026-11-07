namespace AgentBroker.Worker;

public sealed partial class BrokerRuntime
{
    internal SessionTitleRegistry SessionTitles { get; } = new();
}
