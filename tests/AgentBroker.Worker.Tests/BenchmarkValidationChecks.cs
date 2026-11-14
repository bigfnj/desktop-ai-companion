using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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

        foreach (string cohort in new[] { "pending-only", "fresh-busy" })
            foreach (int pending in new[] { 0, 16, 4096 })
            {
                var healthy = await Run(["waiting-mail", pending.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cohort, Path.Combine(scratch.Root, "waiting-" + cohort + "-" + pending)]);
                if (healthy.Code != 0) Console.Error.Write(healthy.Output);
                check(healthy.Code == 0 && WaitingShape(healthy.Output, pending, cohort),
                    "BENCHMARK_WAITING_" + pending + "_" + cohort.Replace('-', '_').ToUpperInvariant());
            }
        try
        {
            CoordinationBenchmark.AfterWaitingMailSeed = directory =>
            {
                string path = Path.Combine(directory, "broker-state.json");
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                var messages = node["Messages"]!.AsArray();
                if (messages.Count != 16) throw new InvalidDataException("Waiting validation seed is not the intended 16-message fixture.");
                messages.RemoveAt(0);
                File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));
            };
            var lost = await Run(["waiting-mail", "16", "fresh-busy", Path.Combine(scratch.Root, "waiting-lost")]);
            check(lost.Code == 1 && lost.Output.Contains("BENCHMARK-FAILED", StringComparison.Ordinal) &&
                lost.Output.Contains("Waiting benchmark summary differs from the requested fixture.", StringComparison.Ordinal),
                "BENCHMARK_WAITING_COUNTS_GUARD");
        }
        finally { CoordinationBenchmark.AfterWaitingMailSeed = null; }
    }

    private static bool WaitingShape(string output, int pending, string cohort)
    {
        string[] lines = output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("BENCHMARK ", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1) return false;
        try
        {
            using var document = JsonDocument.Parse(lines[0][10..]);
            var result = document.RootElement;
            double[] times = result.GetProperty("queryMs").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            string[] hashes = result.GetProperty("summarySha256ByCall").EnumerateArray().Select(item => item.GetString()!).ToArray();
            byte[] bytes = Convert.FromBase64String(result.GetProperty("summaryUtf8Base64").GetString()!);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            using var summary = JsonDocument.Parse(bytes);
            return result.GetProperty("scenario").GetString() == "waiting-mail" &&
                result.GetProperty("pending").GetInt32() == pending && result.GetProperty("cohort").GetString() == cohort &&
                result.GetProperty("sessions").GetInt32() == 65 && result.GetProperty("recipients").GetInt32() == 64 &&
                result.GetProperty("maximumPerRecipient").GetInt32() == (pending == 4096 ? 64 : pending == 16 ? 1 : 0) &&
                result.GetProperty("expectedObservedMessages").GetInt32() == (cohort == "fresh-busy" ? pending : 0) &&
                result.GetProperty("samples").GetInt32() == 6 && result.GetProperty("gapMilliseconds").GetInt32() == 500 &&
                result.GetProperty("firstQuerySample").GetInt32() == 0 && times.Length == 6 &&
                times.All(value => double.IsFinite(value) && value >= 0) && hashes.Length == 6 &&
                hashes.All(value => value == hash) && result.GetProperty("expectedSummarySha256").GetString() == hash &&
                summary.RootElement.GetProperty("Sessions").GetArrayLength() == 65;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}
