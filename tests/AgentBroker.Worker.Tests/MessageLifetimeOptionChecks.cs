using AgentBroker.Worker;

internal static class MessageLifetimeOptionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "synthetic-lifetime-options");
        var defaults = WorkerOptions.Parse(["--data", directory]);
        check(defaults.DefaultMessageLifetimeMinutes == 1440,
            "message lifetime options: older launch arguments retain the twenty-four-hour default");
        foreach (int minutes in new[] { 1, 60, 1440 })
        {
            var options = WorkerOptions.Parse(["--data", directory, "--message-lifetime-minutes",
                minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            check(options.DefaultMessageLifetimeMinutes == minutes,
                "message lifetime options: worker accepts the bounded configured delivery window");
        }
        foreach (string minutes in new[] { "0", "-1", "1441", "2147483647", "", "1.5", "invalid", "2147483648" })
        {
            bool rejected = false;
            try { WorkerOptions.Parse(["--data", directory, "--message-lifetime-minutes", minutes]); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { rejected = true; }
            check(rejected, "message lifetime options: invalid minute values fail before worker startup");
        }
        bool duplicate = false;
        try { WorkerOptions.Parse(["--data", directory, "--message-lifetime-minutes", "60", "--message-lifetime-minutes", "120"]); }
        catch (ArgumentException) { duplicate = true; }
        check(duplicate, "message lifetime options: duplicate delivery-window switches cannot silently replace a value");
        bool missing = false;
        try { WorkerOptions.Parse(["--data", directory, "--message-lifetime-minutes"]); }
        catch (ArgumentException) { missing = true; }
        check(missing, "message lifetime options: a delivery-window switch requires its value");
        foreach (int minutes in new[] { 0, 1441 })
        {
            bool rejected = false;
            try { (defaults with { DefaultMessageLifetimeMinutes = minutes }).Validate(); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "message lifetime options: programmatic options retain the same validation bounds");
        }
        check(defaults.AllowUnauthenticatedLoopback && defaults.EnableCodexNativeDiscovery && !defaults.EnableCodexPeerMessages &&
            defaults.EnableClaudeNativeDiscovery && !defaults.EnableClaudePeerCredentials,
            "message lifetime options: default delivery windows do not change transport or native-wake defaults");
    }
}
