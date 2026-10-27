using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

// N-bus-04 (owner decision 2026-10-27, "Accept localhost"): a managed Claude Code allowlist admits local MCP only as
// http://localhost*, so the loopback listener answers Host localhost:<port> as well as 127.0.0.1:<port>. Every
// request here goes over the 127.0.0.1 socket with the Host header set, so the authority check is what decides,
// never name resolution. The refusals are what keep the DNS-rebinding guard: another name, and another port.
internal static class LoopbackHostChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "loopback-host-" + Guid.NewGuid().ToString("N"));
        var options = new WorkerOptions(scratch, "Synthetic loopback host", LoopbackPort: 0, LanPort: 0,
            EnableLan: false, AllowUnauthenticatedLoopback: true) { EnableBrokerDiscovery = false };
        string? keyName = null;
        WorkerApplication? worker = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            var endpoint = new Uri(worker.Application.Urls.Single(url => url.StartsWith("http://", StringComparison.Ordinal)));
            int port = endpoint.Port;
            int other = port == 65535 ? port - 1 : port + 1;
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            async Task<HttpStatusCode> Initialize(string host)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/mcp"))
                { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new {
                    protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "agentbroker-host-check", version = "1" } } }) };
                request.Headers.Host = host;
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }

            check(endpoint.Host == "127.0.0.1" && await Initialize("127.0.0.1:" + port) == HttpStatusCode.OK,
                "WITNESS the literal loopback authority with the listener's port is answered");
            check(await Initialize("localhost:" + port) == HttpStatusCode.OK,
                "localhost with the listener's port is answered on the loopback listener");
            check(await Initialize("LocalHost:" + port) == HttpStatusCode.OK,
                "the localhost name is matched case-insensitively");
            check(await Initialize("evil.example:" + port) == HttpStatusCode.Forbidden,
                "an arbitrary host name with the listener's port is refused");
            check(await Initialize("localhost.evil.example:" + port) == HttpStatusCode.Forbidden &&
                await Initialize("[::1]:" + port) == HttpStatusCode.Forbidden,
                "a name that only starts with localhost and an IPv6 literal are refused");
            check(await Initialize("localhost:" + other) == HttpStatusCode.Forbidden,
                "localhost naming another port is refused");
            check(await Initialize("127.0.0.1:" + other) == HttpStatusCode.Forbidden && await Initialize("localhost") == HttpStatusCode.Forbidden,
                "the literal address naming another port, and a Host without a port, are refused");
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
            { using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }
}
