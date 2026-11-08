using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class CertificateRenewalLifecycleChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CERTIFICATE_RENEWAL_LIFECYCLE_EXECUTED");
        var due = new DateTimeOffset(2035, 4, 5, 12, 0, 0, TimeSpan.Zero);
        check(!WorkerLifetime.RenewalIsDue(due.AddTicks(-1), due), "RENEWAL_BEFORE_BOUNDARY: a certificate before its renewal deadline remains live");
        check(WorkerLifetime.RenewalIsDue(due, due), "RENEWAL_AT_BOUNDARY: the exact renewal deadline requests graceful replacement");
        check(WorkerLifetime.RenewalIsDue(due.AddYears(10), due), "RENEWAL_FORWARD_CLOCK: a later wall clock still reaches the renewal deadline");
        check(!WorkerLifetime.RenewalIsDue(due.AddYears(-10), due), "RENEWAL_BACKWARD_CLOCK: an earlier wall clock cannot manufacture an early renewal");
        check(WorkerLifetime.RenewalProgressed(null, due.AddYears(1), due), "RENEWAL_INITIAL_FUTURE: an initial future deadline is acceptable");
        check(WorkerLifetime.RenewalProgressed(due, due.AddYears(5), due.AddHours(1)), "RENEWAL_ADVANCES: a renewed future leaf advances its prior deadline");
        check(!WorkerLifetime.RenewalProgressed(due, due, due.AddDays(-1)), "RENEWAL_STALE_DEADLINE: unchanged metadata cannot create a restart loop");
        check(!WorkerLifetime.RenewalProgressed(due, due.AddDays(-1), due.AddDays(-2)), "RENEWAL_REGRESSED_DEADLINE: a backwards certificate deadline refuses recreation");
        check(!WorkerLifetime.RenewalProgressed(due, due.AddYears(1), due.AddYears(1)), "RENEWAL_CLOCK_OVERTAKES: an already due replacement refuses recreation");
        // Indexed by durable, renewal, external cancellation and parent ownership bits. This explicit table
        // keeps the independent stop causes observable even when several arrive during the same drain.
        int[] expected = [0, 0, 0, 0, 0, 75, 0, 0, 73, 73, 73, 73, 73, 73, 73, 73];
        for (int state = 0; state < expected.Length; state++)
        {
            check(WorkerLifetime.ShutdownExitCode((state & 8) != 0, (state & 4) != 0, (state & 2) != 0, (state & 1) != 0) == expected[state],
                "RENEWAL_EXIT_PRECEDENCE_" + state + ": durable failure and external stop supersede renewal");
            check(WorkerLifetime.ShutdownExitCode((state & 8) != 0, (state & 4) != 0, (state & 2) != 0, (state & 1) != 0, cleanupFailure: true) ==
                ((state & 8) != 0 ? 73 : 1), "RENEWAL_CLEANUP_PRECEDENCE_" + state + ": failed cleanup cannot permit recreation or override durable failure");
        }
        using (var process = Process.GetCurrentProcess())
        {
            var options = new WorkerOptions(Path.GetFullPath("."), "Synthetic parent ownership");
            check(WorkerLifetime.ParentIsAlive(options), "RENEWAL_NO_PARENT: a standalone worker needs no parent ownership");
            check(WorkerLifetime.ParentIsAlive(options with { ParentPid = process.Id, ParentCreationFileTime = process.StartTime.ToUniversalTime().ToFileTimeUtc() }),
                "RENEWAL_EXACT_PARENT: a current exact process incarnation remains owned");
            check(!WorkerLifetime.ParentIsAlive(options with { ParentPid = process.Id, ParentCreationFileTime = process.StartTime.ToUniversalTime().ToFileTimeUtc() - 1 }),
                "RENEWAL_PARENT_REUSED_PID: a mismatched creation time cannot authorize recreation");
            check(!WorkerLifetime.ParentIsAlive(options with { ParentPid = int.MaxValue, ParentCreationFileTime = 1 }),
                "RENEWAL_PARENT_GONE: a missing parent cannot authorize recreation");
        }
        foreach (string mode in new[] { "normal", "renewal", "cancel", "cleanup", "owner" }) await CurrentWorkerAsync(check, mode);
    }

    private static async Task CurrentWorkerAsync(Action<bool, string> check, string mode)
    {
        using var owned = OwnedScratch.Create("certificate-lifecycle-" + mode);
        string keyName = "";
        try
        {
            await using var worker = await WorkerApplication.CreateAsync(new(owned.Root, "Synthetic renewal lifecycle", 0, 0, false)
                { EnableBrokerDiscovery = false }, mode == "cleanup" ? [new FailingCleanupAdapter()] : []);
            Guid broker = worker.Runtime.Store.BrokerId;
            keyName = BrokerIdentity.KeyNameFor(owned.Root, broker);
            await worker.StartAsync();
            DateTimeOffset due = worker.CertificateRenewalDueAt;
            string identityPath = Path.Combine(owned.Root, "tls-identity.dat");
            byte[] original = File.ReadAllBytes(identityPath);
            using var cancellation = new CancellationTokenSource();
            using var error = new StringWriter();
            check(!worker.RequestCertificateRenewal(due.AddTicks(-1)) && !worker.CertificateRenewalRequested,
                "RENEWAL_CURRENT_NOT_EARLY_" + mode + ": the actual worker stays live before its deadline");
            if (mode == "normal")
            {
                worker.Application.Lifetime.StopApplication();
                check(!worker.RequestCertificateRenewal(due) && !worker.CertificateRenewalRequested,
                    "RENEWAL_CURRENT_NORMAL_STOP: an already stopping worker cannot acquire a renewal request");
            }
            else
            {
                check(worker.RequestCertificateRenewal(due) && worker.CertificateRenewalRequested &&
                    worker.Application.Lifetime.ApplicationStopping.IsCancellationRequested,
                    "RENEWAL_CURRENT_STOP_" + mode + ": the due worker requests graceful shutdown");
                check(!worker.RequestCertificateRenewal(due.AddYears(1)),
                    "RENEWAL_CURRENT_ONCE_" + mode + ": the same live certificate cannot request repeated recreation");
                if (mode == "cancel") cancellation.Cancel();
                if (mode == "owner")
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using var pipe = new NamedPipeClientStream(".", worker.Options.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(deadline.Token);
                    await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ControlRequest("stop"), ControlProtocol.Json), deadline.Token);
                    await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var response = JsonSerializer.Deserialize<ControlResponse>(await reader.ReadLineAsync(deadline.Token) ?? "", ControlProtocol.Json);
                    check(response is { Success: true, Code: "stopping" },
                        "RENEWAL_OWNER_CONTROL_RESPONSE: the actual authorized control stop acknowledges success after renewal was requested");
                    try { while (!worker.ExternalStopRequested) await Task.Delay(10, deadline.Token); }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                    check(worker.ExternalStopRequested,
                        "RENEWAL_OWNER_CONTROL_LATCH: the actual successful owner stop permanently supersedes renewal");
                }
            }
            int result = await WorkerLifetime.WaitForShutdownAsync(worker, error, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            check(result == (mode == "renewal" ? WorkerLifetime.CertificateRestartExitCode : mode == "cleanup" ? 1 : 0),
                "RENEWAL_CURRENT_EXIT_" + mode + ": the actual disposed worker returns the intended restart or stop result");
            check(worker.ShutdownCleanupFailed == (mode == "cleanup"),
                "RENEWAL_CURRENT_CLEANUP_STATUS_" + mode + ": actual adapter cleanup failure is retained after complete disposal");
            check(worker.ExternalStopRequested == (mode == "owner"),
                "RENEWAL_CURRENT_OWNER_STOP_" + mode + ": authorized control stop remains latched after renewal and complete disposal");
            check(error.ToString() == "", "RENEWAL_CURRENT_NO_DUPLICATE_DIAGNOSTIC_" + mode + ": the lifetime adds no duplicate diagnostic after component cleanup");
            check(worker.CertificateRenewalDueAt == due && File.ReadAllBytes(identityPath).SequenceEqual(original),
                "RENEWAL_CURRENT_NO_LIVE_SWAP_" + mode + ": live shutdown leaves certificate metadata unchanged and its cached deadline readable");
            using var reopened = new BrokerStore(Path.Combine(owned.Root, "store"));
            check(reopened.BrokerId == broker, "RENEWAL_CURRENT_DRAINED_" + mode + ": the disposed worker releases its durable store before recreation");
        }
        finally
        {
            if (keyName.Length != 0 && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                key.Delete();
            }
        }
        check(!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider),
            "RENEWAL_CURRENT_KEY_REMOVED_" + mode + ": the synthetic identity key is removed after disposal");
        owned.Dispose();
        check(!Path.Exists(owned.Root), "RENEWAL_CURRENT_TREE_REMOVED_" + mode + ": the exact synthetic worker tree is removed");
    }

    private sealed class FailingCleanupAdapter : ILocalAgentAdapter
    {
        public string Id => "synthetic-renewal-cleanup";
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid broker, CancellationToken token) => Task.FromResult<IReadOnlyList<BrokerSession>>([]);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("Synthetic renewal cleanup failure."));
    }
}
