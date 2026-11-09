using AgentBroker.TestSupport;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

internal static class WaitHostShutdownChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: WAIT_HOST_SHUTDOWN_EXECUTED");
        using var owned = OwnedScratch.Create("wait-host-shutdown");
        string keyName = "";
        try
        {
            using (var initial = new BrokerStore(Path.Combine(owned.Root, "store")))
                keyName = BrokerIdentity.KeyNameFor(owned.Root, initial.BrokerId);
            await using var worker = await WorkerApplication.CreateAsync(new(owned.Root, "Synthetic wait host shutdown", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                BaseAddress = new(worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
            await RpcAsync(http, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "synthetic-wait-shutdown", version = "1" } }, default);
            var owner = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic HTTP wait owner", false);
            using var cancel = new CancellationTokenSource();
            Task<JsonElement> waiting = RpcAsync(http, "tools/call", new { name = "wait_for_messages", arguments = new { session = owner.Address, timeoutSeconds = 20 } }, cancel.Token);
            Task<int>? shutdown = null;
            using var errors = new StringWriter();
            Exception? primaryFailure = null;
            try
            {
                long admittedBy = Environment.TickCount64 + 3000;
                while (worker.Runtime.ActiveWaitCount != 1 && !waiting.IsCompleted && Environment.TickCount64 < admittedBy) await Task.Delay(10);
                check(worker.Runtime.ActiveWaitCount == 1 && !waiting.IsCompleted,
                    "WAIT_HOST_HTTP_ADMITTED: a real authenticated twenty-second HTTP tool wait is in flight before host stopping");
                shutdown = WorkerLifetime.WaitForShutdownAsync(worker, errors);
                var elapsed = Stopwatch.StartNew();
                worker.Application.Lifetime.StopApplication();
                await Task.WhenAny(shutdown, Task.Delay(3500));
                elapsed.Stop();
                bool prompt = shutdown.IsCompletedSuccessfully && shutdown.Result == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(4);
                JsonElement result = default;
                try { if (prompt) result = await waiting.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
                bool coded = false;
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("result", out var body) &&
                    body.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True)
                {
                    foreach (var block in body.GetProperty("content").EnumerateArray())
                    {
                        if (!block.TryGetProperty("text", out var text) || text.GetString() is not { } value || !value.StartsWith('{')) continue;
                        using var hint = JsonDocument.Parse(value);
                        if (hint.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "agentbroker.recovery_guidance" &&
                            hint.RootElement.GetProperty("guidance").GetProperty("code").GetString() == "broker_stopping") coded = true;
                    }
                }
                Console.WriteLine("NOTE WAIT_HOST_STOP_BEFORE_DRAIN elapsedMs=" + elapsed.ElapsedMilliseconds + "; coded=" + coded);
                check(prompt && coded,
                    "WAIT_HOST_STOP_BEFORE_DRAIN: the actual lifetime exits below four seconds and the draining HTTP client receives broker_stopping");
                check(worker.Runtime.ActiveWaitCount == 0 && WaitLifecycleChecks.ListeningCount(worker.Runtime.Store) == 0 && errors.ToString().Length == 0,
                    "WAIT_HOST_WAIT_RELEASED: actual host stopping leaves no wait or listening entry");
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                // Owned cleanup runs only after the observed stop assertions. In particular, a missing host-hook
                // mutant must still drain its synthetic request rather than masking the intended failure with a timeout.
                try
                {
                    worker.Runtime.StopWaits();
                    cancel.Cancel();
                    try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
                    if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception cleanup) when (primaryFailure is not null)
                { Console.WriteLine("WAIT_HOST_CLEANUP_FAILURE=" + cleanup.GetType().Name); }
            }
        }
        finally
        {
            try
            {
                if (keyName.Length > 0 && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                {
                    using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                    key.Delete();
                }
            }
            finally { owned.Dispose(); }
            Console.WriteLine("WAIT_HOST_OWNED_RESOURCES_CLEAN=" + (!Directory.Exists(owned.Root) &&
                (keyName.Length == 0 || !CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))));
        }
        owned.Dispose();
        check(!Directory.Exists(owned.Root) && !CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider),
            "WAIT_HOST_OWNED_CLEAN: the synthetic HTTP host, identity key and exact scratch tree are released");
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object parameters, CancellationToken cancellation)
    {
        using var response = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }, Json, cancellation);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellation);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Concat(body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
