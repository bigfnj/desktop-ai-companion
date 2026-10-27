using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Lane feature/agentbroker-backlog-a. Inbox attribution (N-agentbroker-client-onboarding-01) runs over the
// real HTTP MCP endpoint; the LAN profile (N-agentbroker-lan-profile-01) runs through an injected probe and
// a manual clock, then once through a real worker for the snapshot wiring. No firewall or profile change.
internal static class OnboardingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await InboxAttributionAsync(check);
        await LanProfileAsync(check);
    }

    public static async Task InboxAttributionAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "inbox-attribution-" + Guid.NewGuid().ToString("N"));
        var options = new WorkerOptions(scratch, "Synthetic inbox attribution", LoopbackPort: 0, EnableLan: false,
            AllowUnauthenticatedLoopback: false) { EnableBrokerDiscovery = false };
        string? keyName = null;
        WorkerApplication? worker = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            var connection = (await Control(options, new("connection"))).Connection!;
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = new(connection.McpEndpoint), Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
            const string privateLabel = "synthetic-private-attribution-label";

            Guid legacyClient = Guid.NewGuid(), legacyIncarnation = Guid.NewGuid();
            await Send(client, Initialize("claude-code"));
            var legacy = await Register(client, legacyClient, legacyIncarnation, privateLabel, null);
            check(!IsError(legacy) && Listed(await Status(options), legacyClient, legacyIncarnation) &&
                Attribution(await Status(options), legacyClient, legacyIncarnation) is null,
                "inbox attribution: a stateless registration without its own client identity is never attributed, even right after a Claude initialize");

            Guid claudeClient = Guid.NewGuid(), claudeIncarnation = Guid.NewGuid();
            var claude = await Register(client, claudeClient, claudeIncarnation, "Codex", "claude-code");
            var status = await Status(options);
            check(!IsError(claude) && Attribution(status, claudeClient, claudeIncarnation) == McpClientFamily.Claude,
                "inbox attribution: a registration whose own request identifies Claude Code is attributed to Claude whatever its label says");
            check(Attribution(status, legacyClient, legacyIncarnation) is null && status.Mcp!.Inboxes!.Length == 1,
                "inbox attribution: attributing one inbox leaves the unidentified inbox unattributed");

            Guid codexClient = Guid.NewGuid(), codexIncarnation = Guid.NewGuid();
            var refused = await Register(client, codexClient, codexIncarnation, " ", "codex-mcp-client");
            check(IsError(refused) && Attribution(await Status(options), codexClient, codexIncarnation) is null,
                "inbox attribution: a refused registration is never attributed");
            var codex = await Register(client, codexClient, codexIncarnation, "Synthetic Codex inbox", "codex-mcp-client");
            check(!IsError(codex) && Attribution(await Status(options), codexClient, codexIncarnation) == McpClientFamily.Codex,
                "inbox attribution: a registration whose own request identifies the Codex client is attributed to Codex");

            var renewed = await Register(client, claudeClient, claudeIncarnation, "Codex", null);
            check(!IsError(renewed) && Attribution(await Status(options), claudeClient, claudeIncarnation) == McpClientFamily.Claude,
                "inbox attribution: an unidentified renewal keeps the verified attribution instead of inheriting Other");

            worker.Runtime.Store.Disconnect(new(worker.Runtime.Store.BrokerId, "mcp:" + codexClient.ToString("D"), codexIncarnation));
            status = await Status(options);
            check(!Listed(status, codexClient, codexIncarnation) && Attribution(status, codexClient, codexIncarnation) is null &&
                Attribution(status, claudeClient, claudeIncarnation) == McpClientFamily.Claude,
                "inbox attribution: an inbox gone from the directory drops out of the snapshot's attributions");
            string serialized = JsonSerializer.Serialize(status, ControlProtocol.Json);
            check(!serialized.Contains("\"inboxes\":[{\"sessionId\":\"mcp:" + legacyClient.ToString("D"), StringComparison.Ordinal) &&
                JsonSerializer.Serialize(status.Mcp, ControlProtocol.Json).Contains("\"inboxes\"", StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(status.Mcp, ControlProtocol.Json).Contains(privateLabel, StringComparison.Ordinal),
                "inbox attribution: the MCP status carries attributions but never a label");
            var older = JsonSerializer.Deserialize<McpStatusView>("{\"endpointReady\":true,\"lastSuccessfulRequestUtc\":null,\"lastToolsListUtc\":null,\"clients\":[]}", ControlProtocol.Json);
            check(older is { Inboxes: null } && !JsonSerializer.Serialize(older, ControlProtocol.Json).Contains("inboxes", StringComparison.Ordinal),
                "inbox attribution: a status without attributions omits the member, so an older reader still parses it");
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            DeleteKey(keyName);
        }
    }

    public static async Task LanProfileAsync(Action<bool, string> check)
    {
        var lan = IPAddress.Parse("10.0.0.20");
        Guid adapter = Guid.NewGuid();
        (string, IReadOnlyList<IPAddress>)[] interfaces =
        [
            ("{" + Guid.NewGuid().ToString("D") + "}", [IPAddress.Loopback]),
            (adapter.ToString("B"), [IPAddress.Parse("fe80::1"), lan]),
        ];
        check(LanProfileMonitor.ResolveCategory(lan, interfaces, [(adapter, 0)]) == "public" &&
            LanProfileMonitor.ResolveCategory(lan, interfaces, [(adapter, 1)]) == "private" &&
            LanProfileMonitor.ResolveCategory(lan, interfaces, [(adapter, 2)]) == "domain",
            "lan profile: the category is read from the adapter that carries the broker's LAN address");
        check(LanProfileMonitor.ResolveCategory(lan, interfaces, [(Guid.NewGuid(), 1), (adapter, 0)]) == "public" &&
            LanProfileMonitor.ResolveCategory(lan, interfaces, [(adapter, 1), (adapter, 0)]) == "public",
            "lan profile: another adapter's Private network cannot hide this adapter's Public one");
        check(LanProfileMonitor.ResolveCategory(IPAddress.Parse("10.0.0.21"), interfaces, [(adapter, 1)]) == "interface_not_found" &&
            LanProfileMonitor.ResolveCategory(lan, interfaces, [(Guid.NewGuid(), 1)]) == "unknown" &&
            LanProfileMonitor.ResolveCategory(lan, interfaces, [(adapter, 7)]) == "unknown" &&
            LanProfileMonitor.ResolveCategory(lan.MapToIPv6(), interfaces, [(adapter, 1)]) == "private",
            "lan profile: a missing interface, an unmatched adapter and an unknown category are reported as such, never as Private");

        var desired = FirewallSetup.CreateRule(@"C:\synthetic\worker\AgentBroker.Worker.exe", 7433);
        check(LanProfileMonitor.ClassifyRule([], desired) == ("absent", 0) &&
            LanProfileMonitor.ClassifyRule([desired], desired) == ("configured", 2),
            "lan profile: the exact Private rule is configured, and no rule under its name is absent");
        check(LanProfileMonitor.ClassifyRule([desired with { Profiles = 7 }], desired) == ("conflicting", 7) &&
            LanProfileMonitor.ClassifyRule([desired, desired], desired) == ("conflicting", 2),
            "lan profile: a widened or duplicated rule under this worker's name is conflicting, never configured");

        var clock = new ManualClock();
        var probe = new FakeProbe { CategoryValue = "public", RuleValue = ("configured", 2) };
        var monitor = new LanProfileMonitor(probe, clock);
        var first = monitor.Snapshot(lan, desired.ApplicationName, 7433);
        await monitor.WhenRefreshed();
        var second = monitor.Snapshot(lan, desired.ApplicationName, 7433);
        check(first.Category == "pending" && first.Rule == "pending" && first.CheckedUtc is null &&
            second is { Category: "public", Rule: "configured", RuleProfiles: 2 } && second.CheckedUtc == clock.GetUtcNow(),
            "lan profile: the first status is pending and the next one carries the completed reading");
        await monitor.WhenRefreshed();
        clock.Advance(TimeSpan.FromSeconds(5));
        monitor.Snapshot(IPAddress.Parse(lan.ToString()), desired.ApplicationName, 7433);
        await monitor.WhenRefreshed();
        check(probe.Categories == 1 && probe.Rules == 1,
            "lan profile: status polls inside the reading's lifetime reuse it, including for an equal address instance");
        clock.Advance(LanProfileMonitor.MaximumAge);
        probe.CategoryValue = "private";
        monitor.Snapshot(lan, desired.ApplicationName, 7433);
        await monitor.WhenRefreshed();
        check(probe.Categories == 2 && monitor.Snapshot(lan, desired.ApplicationName, 7433).Category == "private",
            "lan profile: an expired reading is refreshed once, off the caller");
        await monitor.WhenRefreshed();
        check(monitor.Snapshot(IPAddress.Parse("10.0.0.30"), desired.ApplicationName, 7433).Category == "pending",
            "lan profile: a changed LAN address never shows the previous address's category");
        await monitor.WhenRefreshed();

        var failing = new LanProfileMonitor(new FakeProbe { CategoryFailure = new COMException("synthetic"), RuleFailure = new ArgumentException("synthetic") }, clock);
        failing.Snapshot(lan, "synthetic", 7433);
        await failing.WhenRefreshed();
        check(failing.Snapshot(lan, "synthetic", 7433) is { Category: "unavailable", Rule: "unavailable" },
            "lan profile: a failed category or rule read is unavailable, not Private and not configured");
        var odd = new LanProfileMonitor(new FakeProbe { CategoryValue = "synthetic-category", RuleValue = ("synthetic-rule", 0) }, clock);
        odd.Snapshot(lan, "synthetic", 7433);
        await odd.WhenRefreshed();
        check(odd.Snapshot(lan, "synthetic", 7433) is { Category: "unknown", Rule: "unavailable" },
            "lan profile: only fixed codes leave the monitor");

        monitor.ObserveInbound(IPAddress.Loopback, IPAddress.Loopback);
        monitor.ObserveInbound(lan, lan);
        monitor.ObserveInbound(IPAddress.Parse("203.0.113.5"), lan);
        monitor.ObserveInbound(null, lan);
        check(monitor.LastInboundUtc is null && monitor.Snapshot(lan, desired.ApplicationName, 7433).LastInboundUtc is null,
            "lan profile: loopback, this PC's own LAN address and a public source are not evidence of LAN reachability");
        clock.Advance(TimeSpan.FromMinutes(1));
        monitor.ObserveInbound(IPAddress.Parse("10.0.0.40").MapToIPv6(), lan);
        check(monitor.LastInboundUtc == clock.GetUtcNow() && monitor.Snapshot(lan, desired.ApplicationName, 7433).LastInboundUtc == clock.GetUtcNow(),
            "WITNESS lan profile: a request from another private address is recorded as observed reachability");
        await monitor.WhenRefreshed();

        // The real worker: snapshot wiring and the Windows probe, read-only. Inside this test process the rule
        // read is refused (the host is not AgentBroker.Worker.exe); the category is whatever Windows reports.
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "lan-profile-" + Guid.NewGuid().ToString("N"));
        var options = new WorkerOptions(scratch, "Synthetic LAN profile", LoopbackPort: 0, LanPort: 0, EnableLan: true,
            AllowUnauthenticatedLoopback: false) { LanListenAddress = IPAddress.Loopback, EnableBrokerDiscovery = false };
        string? keyName = null;
        WorkerApplication? worker = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            keyName = BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId);
            await worker.StartAsync();
            var initial = (await Status(options)).Lan;
            await worker.LanProfile.WhenRefreshed();
            using (var lanHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate?.GetCertHashString(HashAlgorithmName.SHA256) == worker.Runtime.Federation.LocalAdvertisement.CertificateSha256 })
            using (var lanClient = new HttpClient(lanHandler))
            using (var info = await lanClient.GetAsync(worker.Application.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal)) + "/broker/info"))
                check(info.IsSuccessStatusCode, "lan profile: the loopback LAN listener answered the synthetic request");
            var read = (await Status(options)).Lan;
            check(initial is not null && read is not null && read.Rule == "unavailable" && read.LastInboundUtc is null &&
                read.CheckedUtc is not null &&
                read.Category is "public" or "private" or "domain" or "unknown" or "interface_not_found" or "unavailable",
                "lan profile: the control snapshot carries fixed codes, refuses a rule read outside the worker executable and counts no loopback request as LAN evidence");
            var disabled = JsonSerializer.Serialize((await Status(options)) with { Lan = null }, ControlProtocol.Json);
            check(!disabled.Contains("\"lan\"", StringComparison.Ordinal),
                "lan profile: a snapshot without a LAN reading omits the member, so an older module still parses it");
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            DeleteKey(keyName);
        }
    }

    private sealed class FakeProbe : ILanProfileProbe
    {
        internal string CategoryValue = "private";
        internal (string, int) RuleValue = ("absent", 0);
        internal Exception? CategoryFailure, RuleFailure;
        internal int Categories, Rules;
        public string Category(IPAddress address)
        { Interlocked.Increment(ref Categories); if (CategoryFailure is not null) throw CategoryFailure; return CategoryValue; }
        public (string State, int Profiles) Rule(string workerPath, int port)
        { Interlocked.Increment(ref Rules); if (RuleFailure is not null) throw RuleFailure; return RuleValue; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private static void DeleteKey(string? keyName)
    {
        if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
        {
            using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
            key.Delete();
        }
    }

    private static async Task<BrokerSnapshot> Status(WorkerOptions options) =>
        (await Control(options, new("status"))).Snapshot ?? throw new InvalidDataException("Synthetic status had no snapshot.");
    private static bool Listed(BrokerSnapshot status, Guid clientId, Guid incarnation) =>
        status.Sessions.Any(session => session.SessionId == "mcp:" + clientId.ToString("D") && session.Incarnation == incarnation);
    private static McpClientFamily? Attribution(BrokerSnapshot status, Guid clientId, Guid incarnation) =>
        status.Mcp?.Inboxes?.SingleOrDefault(inbox => inbox.SessionId == "mcp:" + clientId.ToString("D") && inbox.Incarnation == incarnation)?.Family;

    private static Task<JsonElement> Register(HttpClient client, Guid clientId, Guid incarnation, string label, string? identity)
    {
        var arguments = new { clientId, incarnation, label, isPrivate = false };
        object parameters = identity is null ? new { name = "register_agent", arguments } : new
        {
            name = "register_agent", arguments,
            _meta = new Dictionary<string, object> {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new { name = identity, version = "synthetic-version" },
                ["io.modelcontextprotocol/clientCapabilities"] = new { } }
        };
        return Send(client, new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call", @params = parameters }, identity is not null);
    }
    private static object Initialize(string name) => new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "initialize",
        @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name, version = "synthetic-version" } } };
    private static bool IsError(JsonElement reply) => reply.TryGetProperty("error", out _) ||
        reply.GetProperty("result").TryGetProperty("isError", out var value) && value.GetBoolean();
    private static async Task<JsonElement> Send(HttpClient client, object payload, bool modern = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = JsonContent.Create(payload) };
        if (modern)
        {
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            var element = JsonSerializer.SerializeToElement(payload);
            request.Headers.Add("Mcp-Method", element.GetProperty("method").GetString());
            // The 2026 transport requires the tool name beside the method for a tools/call.
            if (element.GetProperty("params").TryGetProperty("name", out var name)) request.Headers.Add("Mcp-Name", name.GetString());
        }
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Concat(text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static async Task<ControlResponse> Control(WorkerOptions options, ControlRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = ControlPipeSecurity.CreateClient(options.ControlPipeName);
        await pipe.ConnectAsync(deadline.Token);
        ControlPipeSecurity.ValidateConnectedServer(pipe, Environment.ProcessId);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ControlProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, deadline.Token); await pipe.FlushAsync(deadline.Token);
        using var line = new MemoryStream();
        byte[] single = new byte[1];
        while (line.Length <= ControlProtocol.MaximumFrameBytes)
        {
            if (await pipe.ReadAsync(single, deadline.Token) == 0) throw new IOException("Synthetic control response ended early.");
            if (single[0] == (byte)'\n') return JsonSerializer.Deserialize<ControlResponse>(line.ToArray(), ControlProtocol.Json)!;
            line.WriteByte(single[0]);
        }
        throw new IOException("Synthetic control response exceeded its bound.");
    }
}
