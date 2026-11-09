using AgentBroker.TestSupport;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

// N4 (AgentBroker next campaign, 2026-11-08): list_sessions' 5-second paired-broker budget now also ends when the broker
// starts stopping, so a paired broker that never answers cannot hold the host drain past the supervisor's 4-second kill.
// Stopping returns what the budget returns (local rows plus already completed peer directories); a cancelled caller still
// gets cancellation. The named kill removes the stop link from the budget; the first check below must then fail.
internal static class DirectoryStopChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(Action<bool, string> check, bool includeHost = true, bool includeRuntime = true)
    {
        if (includeRuntime) await RuntimeAsync(check);
        if (includeHost) await HostAsync(check);
    }

    private static async Task RuntimeAsync(Action<bool, string> check)
    {
        using var owned = OwnedScratch.Create("directory-stop");
        using var store = new BrokerStore(Path.Combine(owned.Root, "store"));
        store.AuthorizePeer(new PeerTrust(Guid.NewGuid(), "Synthetic silent peer", "https://192.0.2.77/", new string('B', 64), DateTimeOffset.UtcNow));

        var silent = new SilentPeers();
        await using (var runtime = new BrokerRuntime(store, silent, []))
        {
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic local row", false);
            var listing = runtime.DirectoryAsync(false, default);
            bool blocked = await Task.WhenAny(silent.Entered, Task.Delay(3000)) == silent.Entered && !listing.IsCompleted;
            check(blocked, "DIRECTORY_STOP_WITNESS_BLOCKED: the listing waits on a paired broker that never answers or observes cancellation");
            var elapsed = Stopwatch.StartNew();
            runtime.StopWaits();
            IReadOnlyList<BrokerSession>? rows = null;
            try { rows = await listing.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { /* Reported by the check below. */ }
            elapsed.Stop();
            Console.WriteLine("NOTE DIRECTORY_STOP_RETURNS_PROMPTLY elapsedMs=" + elapsed.ElapsedMilliseconds);
            check(rows is not null && elapsed.Elapsed < TimeSpan.FromSeconds(2) && rows.Any(row => row.Address == local.Address) &&
                rows.All(row => row.Address.BrokerId == store.BrokerId),
                "DIRECTORY_STOP_RETURNS_PROMPTLY: stopping ends the peer budget at once and the listing returns its local rows");
        }

        var cancelled = new SilentPeers();
        await using (var runtime = new BrokerRuntime(store, cancelled, []))
        {
            using var caller = new CancellationTokenSource();
            var listing = runtime.DirectoryAsync(false, caller.Token);
            await Task.WhenAny(cancelled.Entered, Task.Delay(3000));
            caller.Cancel();
            bool sawCancellation = false;
            try { await listing.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { sawCancellation = true; }
            catch (TimeoutException) { /* Reported by the check below. */ }
            check(cancelled.Entered.IsCompleted && sawCancellation,
                "DIRECTORY_STOP_WITNESS_CALLER_CANCEL: a cancelled caller still gets cancellation, not a partial listing");
        }
    }

    // The actual host: an authenticated HTTP list_sessions is blocked on a paired broker whose TCP endpoint accepts and never
    // answers TLS, then WorkerLifetime stops the host. Without the stop link the request holds the drain for the full budget.
    private static async Task HostAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: DIRECTORY_HOST_STOP_EXECUTED");
        using var owned = OwnedScratch.Create("directory-host-stop");
        string keyName = "";
        var silentListener = new TcpListener(IPAddress.Loopback, 0);
        silentListener.Start();
        TcpClient? heldPeer = null;
        try
        {
            using (var initial = new BrokerStore(Path.Combine(owned.Root, "store")))
                keyName = BrokerIdentity.KeyNameFor(owned.Root, initial.BrokerId);
            await using var worker = await WorkerApplication.CreateAsync(new(owned.Root, "Synthetic directory host stop", LoopbackPort: 0,
                EnableLan: false, AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false }, []);
            await worker.StartAsync();
            int silentPort = ((IPEndPoint)silentListener.LocalEndpoint).Port;
            worker.Runtime.Store.AuthorizePeer(new PeerTrust(Guid.NewGuid(), "Synthetic silent peer", "https://127.0.0.1:" + silentPort + "/",
                new string('C', 64), DateTimeOffset.UtcNow));
            var local = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic local row", false);
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                BaseAddress = new(worker.Application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) + "/mcp"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.LocalAccessToken);
            await RpcAsync(http, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "synthetic-directory-stop", version = "1" } });
            Task<TcpClient> accepted = silentListener.AcceptTcpClientAsync();
            Task<JsonElement> listing = RpcAsync(http, "tools/call", new { name = "list_sessions", arguments = new { } });
            Task<int>? shutdown = null;
            using var errors = new StringWriter();
            Exception? primaryFailure = null;
            try
            {
                bool inFlight = await Task.WhenAny(accepted, Task.Delay(5000)) == accepted && !listing.IsCompleted;
                if (accepted.IsCompletedSuccessfully) heldPeer = accepted.Result;
                check(inFlight, "DIRECTORY_HOST_WITNESS_BLOCKED: a real HTTP list_sessions is blocked on a paired broker that never answers TLS");
                shutdown = WorkerLifetime.WaitForShutdownAsync(worker, errors);
                var elapsed = Stopwatch.StartNew();
                worker.Application.Lifetime.StopApplication();
                await Task.WhenAny(shutdown, Task.Delay(4500));
                elapsed.Stop();
                Console.WriteLine("NOTE DIRECTORY_HOST_STOP elapsedMs=" + elapsed.ElapsedMilliseconds);
                bool prompt = shutdown.IsCompletedSuccessfully && shutdown.Result == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(3);
                bool localRow = false;
                try
                {
                    var result = await listing.WaitAsync(TimeSpan.FromSeconds(2));
                    var body = result.GetProperty("result");
                    localRow = !(body.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True) &&
                        body.GetRawText().Contains(local.Address.SessionId, StringComparison.Ordinal);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException) { }
                check(prompt && localRow && errors.ToString().Length == 0,
                    "DIRECTORY_HOST_STOP_BEFORE_DRAIN: the actual lifetime exits within three seconds and the blocked list_sessions returns its local rows");
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                try
                {
                    worker.Runtime.StopWaits();
                    try { await listing.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException) { }
                    if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception cleanup) when (primaryFailure is not null)
                { Console.WriteLine("DIRECTORY_HOST_CLEANUP_FAILURE=" + cleanup.GetType().Name); }
            }
        }
        finally
        {
            heldPeer?.Dispose();
            silentListener.Stop();
            try
            {
                if (keyName.Length > 0 && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                {
                    using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                    key.Delete();
                }
            }
            finally { owned.Dispose(); }
        }
        check(!Directory.Exists(owned.Root) && (keyName.Length == 0 || !CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)),
            "DIRECTORY_HOST_OWNED_CLEAN: the synthetic host, identity key and exact scratch tree are released");
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }, Json);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Concat(body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    // A paired broker that never answers and never observes cancellation: only DirectoryAsync's own budget can end the wait.
    private sealed class SilentPeers : IPeerFederation
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PeerDirectory> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => entered.Task;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => throw new NotSupportedException();
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
        {
            entered.TrySetResult();
            return never.Task;
        }
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
