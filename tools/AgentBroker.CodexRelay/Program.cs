namespace DesktopAICompanion.AgentBroker.CodexRelay;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = RelayOptions.Parse(args);
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                await using var session = new RelaySession(options);
                return await session.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                    Console.OpenStandardError(), stop.Token).ConfigureAwait(false);
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Never print arguments, protocol frames, paths, keys, or exception messages.
            await Console.Error.WriteLineAsync("AgentBroker relay stopped: " + ex.GetType().Name).ConfigureAwait(false);
            return 2;
        }
    }
}
