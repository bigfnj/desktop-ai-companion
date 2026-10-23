using System.Diagnostics;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunLifetimeChecks(Action<bool, string> check, TestDispatcher dispatcher)
    {
        var host = new TestHost();
        host.UseStorage(Id, new TestStorage(Path.Combine(Path.GetTempPath(), "agentbroker-lifetime-synthetic")));
        var supervisor = new TestSupervisor();
        var module = new AgentBrokerModule(new TestControl(), supervisor);
        try
        {
            module.Init(host);
            var pane = host.OptionsPanes.Single();
            check(pane.Load()["messageLifetimeHours"] == "24" && module.settings.MessageLifetimeHours == 24,
                "message lifetime: missing stored value preserves the twenty-four-hour default");
            check(pane.Schema.Any(field => field.Id == "messageLifetimeHours" && field.Kind == SettingKind.Int &&
                field.Min == 1 && field.Max == 24),
                "message lifetime: options expose the bounded one-to-twenty-four-hour delivery window");
            check(pane.Load()["messageLifetimeHelp"].Contains("explicit message deadlines stay unchanged", StringComparison.Ordinal) &&
                pane.Load()["messageLifetimeHelp"].Contains("already received remain received", StringComparison.Ordinal),
                "message lifetime: help distinguishes delivery deadlines from already received messages");
            int saves = host.SettingsFor(Id).SaveCount, generation = module.generation;
            foreach (string invalid in new[] { "0", "25", "-1", "1.5", "", " 2", "2 " })
                check(!pane.Save(new Dictionary<string, string> { ["messageLifetimeHours"] = invalid }) &&
                    module.settings.MessageLifetimeHours == 24 && module.generation == generation && host.SettingsFor(Id).SaveCount == saves,
                    "message lifetime: invalid hours are rejected before saving or restarting");
            foreach (int hours in new[] { 1, 24 })
                check(BrokerSettings.TryEdit(module.settings, new Dictionary<string, string>
                    { ["messageLifetimeHours"] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture) }, out var edited) &&
                    edited.MessageLifetimeHours == hours && edited.Valid,
                    "message lifetime: both allowed hour boundaries round-trip through settings validation");
            foreach (int hours in new[] { 1, 7, 24 })
            {
                var start = new ProcessStartInfo("synthetic.exe");
                WorkerSupervisor.AddArguments(start, new("synthetic.exe", "synthetic-data",
                    module.settings with { MessageLifetimeHours = hours }, 1, 1));
                int index = start.ArgumentList.IndexOf("--message-lifetime-minutes");
                check(index >= 0 && start.ArgumentList[index + 1] == (hours * 60).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "message lifetime: worker launch converts the configured hours to exact invariant minutes");
            }
            check(pane.Save(new Dictionary<string, string> { ["messageLifetimeHours"] = "12" }) &&
                host.SettingsFor(Id).GetInt("messageLifetimeHours", 24) == 12 && pane.Load()["messageLifetimeHours"] == "12" &&
                BrokerSettings.Read(host.SettingsFor(Id)).MessageLifetimeHours == 12 && supervisor.Launches.Count == 0,
                "message lifetime: an actual options edit persists and reloads without launching a disabled worker");
        }
        finally { module.Shutdown(); Pump(() => module.pending.IsCompleted, dispatcher); }
    }
}
