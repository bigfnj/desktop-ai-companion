using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class DurableFailureChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: DURABLE_FAILURE_PRODUCT_EXECUTED");
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "durable-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new BrokerStore(Path.Combine(root, "replace")))
            {
                var sender = Row(store, "sender"); var recipient = Row(store, "recipient");
                store.Register(sender); store.Register(recipient);
                var sent = store.Send(sender.Address, recipient.Address, Guid.NewGuid(), "Synthetic durable replacement", null, 0, store.BrokerId);
                store.BeginDelivery();
                using var locked = new FileStream(Path.Combine(root, "replace", "broker-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                Exception? failure = null;
                try { store.CompleteDelivery(store.FindReceipt(sent.Message.Id)!, MessageStatus.Submitted); } catch (Exception error) { failure = error; }
                check(failure is IOException { InnerException: Win32Exception },
                    "DURABLE_REPLACE_CLASSIFIED: actual Windows replacement fallback is a durable IO failure");
                check(store.FindReceipt(sent.Message.Id)?.Status == MessageStatus.Submitting,
                    "DURABLE_REPLACE_ATOMIC: failed snapshot publishes no successful submission result");
            }
            foreach (string mode in new[] { "fault", "normal", "shutdown-fault" })
            {
                bool fault = mode != "normal";
                string directory = Path.Combine(root, mode);
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
                foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--durable-failure-child", directory, mode }) start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new IOException("Synthetic durable child did not start.");
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await child.WaitForExitAsync(deadline.Token); }
                catch { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
                string stdout = await output, stderr = await error;
                check(stdout.Contains("DURABLE_RUNTIME_STOP_PASS", StringComparison.Ordinal),
                    mode == "shutdown-fault" ? "DURABLE_SHUTDOWN_DRAIN: final status waits for in-flight settlement during shutdown" :
                    "DURABLE_RUNTIME_STOP: a fatal durable failure stops the actual application instead of restarting a stuck loop");
                check(child.ExitCode == (fault ? 73 : 0),
                    "DURABLE_PROCESS_EXIT: production shutdown returns storage failure for fatal state and zero for ordinary stop");
                check(stderr == (fault ? "agentbroker: durable_state_unavailable" + Environment.NewLine : ""),
                    "DURABLE_PROCESS_DIAGNOSTIC: exactly one bounded fixed diagnostic reaches stderr");
                if (fault) check(stdout.Contains("DURABLE_NO_REPLAY_RECOVERY_PASS", StringComparison.Ordinal),
                    "DURABLE_NO_REPLAY_RECOVERY: failed settlement is never replayed and restart recovers uncertainty");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        check(!Directory.Exists(root), "DURABLE_FIXTURE_CLEANUP: owned store, identity and child files are removed");
    }

    internal static async Task<int> RunChildAsync(string directory, string mode)
    {
        bool fault = mode != "normal";
        WorkerApplication? worker = null;
        Guid brokerId = Guid.Empty;
        var adapter = new HeldAdapter(mode == "shutdown-fault");
        SessionAddress? recipientAddress = null;
        Guid messageId = Guid.Empty;
        int fatalNotifications = 0;
        int exitCode;
        try
        {
            worker = await WorkerApplication.CreateAsync(new(directory, "Synthetic durable child", 0, 0, false)
                { EnableBrokerDiscovery = false }, [adapter]);
            brokerId = worker.Runtime.Store.BrokerId;
            worker.Runtime.FatalError += () => Interlocked.Increment(ref fatalNotifications);
            await worker.StartAsync();
            if (fault)
            {
                var recipient = await adapter.Discovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                recipientAddress = recipient;
                var discovery = Stopwatch.StartNew();
                while (!worker.Runtime.Store.IsAddressable(recipient))
                {
                    if (discovery.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Synthetic durable row was not registered.");
                    await Task.Delay(10);
                }
                var sender = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic durable sender", false);
                messageId = worker.Runtime.Store.Send(sender.Address, recipient, Guid.NewGuid(), "Synthetic native input", null, 0, brokerId).Message.Id;
                await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var locked = new FileStream(Path.Combine(directory, "store", "broker-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var shutdown = WorkerLifetime.WaitForShutdownAsync(worker, Console.Error);
                if (mode == "shutdown-fault")
                {
                    worker.Application.Lifetime.StopApplication();
                    await adapter.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (await Task.WhenAny(shutdown, Task.Delay(200)) == shutdown) return 90;
                }
                adapter.Release.TrySetResult();
                try { exitCode = await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { worker.Application.Lifetime.StopApplication(); await shutdown; return 86; }
                if (!worker.Runtime.DurableStateUnavailable || Volatile.Read(ref fatalNotifications) != 1 ||
                    worker.Runtime.AdapterErrors.GetValueOrDefault("broker") != "durable_state_unavailable" ||
                    worker.Runtime.AdapterErrors.ContainsKey("delivery") || adapter.Submissions != 1) return 87;
                using var persisted = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "store", "broker-state.json")));
                if (!persisted.RootElement.GetProperty("Messages").EnumerateArray().Any(row =>
                        row.GetProperty("Message").GetProperty("Id").GetGuid() == messageId &&
                        row.GetProperty("Status").GetInt32() == (int)MessageStatus.Submitting)) return 87;
                Console.WriteLine("DURABLE_RUNTIME_STOP_PASS");
            }
            else
            {
                worker.Application.Lifetime.StopApplication();
                exitCode = await WorkerLifetime.WaitForShutdownAsync(worker, Console.Error).WaitAsync(TimeSpan.FromSeconds(5));
                if (worker.Runtime.DurableStateUnavailable || fatalNotifications != 0) return 88;
                Console.WriteLine("DURABLE_RUNTIME_STOP_PASS");
            }
        }
        finally
        {
            adapter.Release.TrySetResult();
            if (worker is not null) await worker.DisposeAsync();
            if (brokerId != Guid.Empty)
            {
                string key = BrokerIdentity.KeyNameFor(directory, brokerId);
                if (CngKey.Exists(key, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                { using var owned = CngKey.Open(key, CngProvider.MicrosoftSoftwareKeyStorageProvider); owned.Delete(); }
                if (CngKey.Exists(key, CngProvider.MicrosoftSoftwareKeyStorageProvider)) throw new IOException("Synthetic durable identity was not removed.");
            }
        }
        if (fault)
        {
            using var reopened = new BrokerStore(Path.Combine(directory, "store"));
            if (reopened.FindReceipt(messageId)?.Status != MessageStatus.Uncertain || adapter.Submissions != 1 || recipientAddress is null) return 89;
            reopened.Register(new(recipientAddress, "Synthetic exact recipient returned", adapter.Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1)));
            if (reopened.BeginDelivery() is not null) return 89;
            Console.WriteLine("DURABLE_NO_REPLAY_RECOVERY_PASS");
        }
        return exitCode;
    }

    private static BrokerSession Row(BrokerStore store, string id) => new(new(store.BrokerId, "durable:" + id, Guid.NewGuid()),
        "Synthetic durable row", "durable", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
        SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class HeldAdapter(bool holdThroughShutdown) : ILocalAgentAdapter
    {
        public string Id => "durable";
        internal TaskCompletionSource<SessionAddress> Discovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Submissions;
        private SessionAddress? address;
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            address ??= new(brokerId, "durable:child", Guid.NewGuid());
            Discovered.TrySetResult(address);
            return Task.FromResult<IReadOnlyList<BrokerSession>>([new(address, "Synthetic durable child", Id,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1))]);
        }
        public async Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token)
        {
            Interlocked.Increment(ref Submissions); Entered.TrySetResult();
            using var stopping = token.Register(() => StopRequested.TrySetResult());
            if (holdThroughShutdown) await Release.Task;
            else await Release.Task.WaitAsync(token);
            return new(AdapterAdmission.Submitted);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
