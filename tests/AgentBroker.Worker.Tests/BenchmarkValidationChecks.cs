using System.Text;
using AgentBroker.TestSupport;

internal static class BenchmarkValidationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: POSTPUSH_BENCHMARK_VALIDATION_EXECUTED");
        using var scratch = OwnedScratch.Create("benchmark-validation");
        async Task<(int Code, string Output)> Run(string[] arguments)
        {
            var original = Console.Out;
            var originalError = Console.Error;
            using var capture = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(capture);
                Console.SetError(capture);
                int code = await CoordinationBenchmark.RunAsync(arguments).WaitAsync(TimeSpan.FromSeconds(35));
                return (code, capture.ToString());
            }
            finally { Console.SetOut(original); Console.SetError(originalError); }
        }
        string[] Tombstone(string name) => ["tombstone", "0", "3", "0", "16", "65536", Path.Combine(scratch.Root, name)];
        string[] DirectoryCase(string name, int pages) => ["directory", "1", "17", Path.Combine(scratch.Root, name), pages.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var healthyHistory = await Run(Tombstone("history-healthy"));
        check(healthyHistory.Code == 0 && healthyHistory.Output.Contains("\"listed\":9", StringComparison.Ordinal),
            "BENCHMARK_HISTORY_WITNESS: the actual benchmark loads its seeded tombstones before reporting success");
        try
        {
            CoordinationBenchmark.AfterTombstoneSeed = directory =>
            {
                string path = Path.Combine(directory, "broker-tombstones.jsonl");
                File.WriteAllText(path, File.ReadLines(path).First() + Environment.NewLine, new UTF8Encoding(false));
            };
            var lostHistory = await Run(Tombstone("history-lost"));
            check(lostHistory.Code == 1 && lostHistory.Output.Contains("BENCHMARK-FAILED", StringComparison.Ordinal),
                "BENCHMARK_HISTORY_GUARD: the actual benchmark fails when its candidate silently loads none of the seeded journal history");
        }
        finally { CoordinationBenchmark.AfterTombstoneSeed = null; }
        var healthyDirectory = await Run(DirectoryCase("directory-healthy", 1));
        if (healthyDirectory.Code != 0) Console.Error.Write(healthyDirectory.Output);
        check(healthyDirectory.Code == 0 && healthyDirectory.Output.Contains("\"federated\":17", StringComparison.Ordinal) &&
            healthyDirectory.Output.Contains("\"returned\":16", StringComparison.Ordinal),
            "BENCHMARK_DIRECTORY_WITNESS: complete runtime census and an explicitly requested page prefix can both succeed");
        try
        {
            CoordinationBenchmark.AfterDirectoryRegistration = workers =>
            {
                var peer = workers[1].Runtime.Store;
                peer.Disconnect(peer.Directory().First().Address);
            };
            var partial = await Run(DirectoryCase("directory-partial", 1));
            check(partial.Code == 1 && partial.Output.Contains("BENCHMARK-FAILED", StringComparison.Ordinal),
                "BENCHMARK_DIRECTORY_GUARD: the actual benchmark refuses success-shaped timing of an incomplete runtime census");
        }
        finally { CoordinationBenchmark.AfterDirectoryRegistration = null; }
        try
        {
            CoordinationBenchmark.AfterDirectoryMeasurement = workers =>
            {
                var peer = workers[1].Runtime.Store;
                peer.Disconnect(peer.Directory().First().Address);
            };
            var partial = await Run(DirectoryCase("pages-partial", 2));
            check(partial.Code == 1 && partial.Output.Contains("BENCHMARK-FAILED", StringComparison.Ordinal),
                "BENCHMARK_PAGE_GUARD: the actual benchmark refuses a truncated measured keyset traversal");
        }
        finally { CoordinationBenchmark.AfterDirectoryMeasurement = null; }
    }
}
