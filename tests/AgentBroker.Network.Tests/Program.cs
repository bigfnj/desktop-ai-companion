using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Discovery;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;
using Microsoft.AspNetCore.Server.Kestrel.Https;

int assertions = 0;
var ownedKeys = new HashSet<string>(StringComparer.Ordinal);
BrokerIdentity Identity(string directory, Guid id) { var identity = BrokerIdentity.LoadOrCreate(directory, id); ownedKeys.Add(identity.KeyName); return identity; }
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); assertions++; Console.WriteLine("PASS: " + name); }
async Task Refuses(Func<Task> action, string name)
{
    try { await action(); throw new InvalidOperationException("Expected refusal: " + name); }
    catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException) { Check(true, name); }
}
string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    await DiscoveryLifetimeChecks.RunAsync(Check);
    if (args.Contains("--discovery-only")) return 0;
    var clock = new TestClock();
    await using var a = await Node.Create(Path.Combine(scratch, "a"), clock, Identity);
    await using var b = await Node.Create(Path.Combine(scratch, "b"), clock, Identity);
    await using var c = await Node.Create(Path.Combine(scratch, "c"), new SkewClock(clock, TimeSpan.FromMinutes(15)), Identity);
    using (var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri(a.HttpEndpoint), Timeout = TimeSpan.FromSeconds(5) })
    {
        var info = (await plain.GetFromJsonAsync<BrokerAdvertisement>("broker/info"))!;
        Check(info.Endpoint == a.Endpoint && info.BrokerId == a.Store.BrokerId && info.CertificateSha256 == a.Identity.Fingerprint,
            "HTTP broker info advertises the actual HTTPS LAN port and socket-local address");
    }
    using (var secured = a.Client())
        Check((await secured.GetFromJsonAsync<BrokerAdvertisement>("broker/info"))!.Endpoint == a.Endpoint,
            "HTTPS broker info preserves its actual listener port and socket-local address");
    using (var loaded = BrokerIdentity.LoadOrCreate(a.Directory, a.Store.BrokerId))
        Check(loaded.Fingerprint == a.Identity.Fingerprint, "DPAPI identity reload retains certificate and broker pin");
    using (var loaded = BrokerIdentity.LoadOrCreate(a.Directory + Path.DirectorySeparatorChar, a.Store.BrokerId))
        Check(loaded.KeyName == a.Identity.KeyName, "equivalent directory spelling retains the same runtime key");
    Check(File.Exists(Path.Combine(a.Directory, "tls-identity.dat")), "identity persistence uses the encrypted data blob");
    using (var key = CngKey.Open(a.Identity.KeyName)) Check(key.ExportPolicy == CngExportPolicies.None && !key.IsMachineKey, "TLS runtime key is current-user and nonexportable");
    try { using var wrong = BrokerIdentity.LoadOrCreate(a.Directory, Guid.NewGuid()); throw new Exception("Wrong broker identity loaded."); }
    catch (InvalidDataException) { Check(true, "identity cannot be reassigned to another broker ID"); }
    using (var anon = b.Client()) Check((await anon.GetAsync("federation/directory")).StatusCode == HttpStatusCode.Forbidden, "anonymous HTTPS cannot read a directory");
    using (var stranger = b.Client(c.Identity))
    {
        Check((await stranger.GetAsync("federation/directory")).StatusCode == HttpStatusCode.Forbidden, "an unpaired certificate cannot read a directory");
        var invalid = c.Advertisement with { Endpoint = $"https://127.0.0.2:{new Uri(c.Endpoint).Port}/" };
        var response = await stranger.PostAsJsonAsync("pair/request", new { requestId = Guid.NewGuid(), nonce = new string('A', 64), peer = invalid, expiresAt = clock.GetUtcNow().AddMinutes(1) });
        Check(!response.IsSuccessStatusCode, "pairing callback address must equal the actual TLS caller IP");
        response = await stranger.PostAsJsonAsync("pair/request", new { requestId = Guid.NewGuid(), nonce = new string('A', 64), peer = c.Advertisement with { BrokerId = a.Store.BrokerId }, expiresAt = clock.GetUtcNow().AddMinutes(1) });
        Check(response.StatusCode == HttpStatusCode.Forbidden, "payload broker ID cannot impersonate the client certificate");
        response = await stranger.PostAsJsonAsync("pair/approve", new { requestId = Guid.NewGuid(), nonce = new string('B', 64), peer = c.Advertisement, verificationCode = "NO" });
        Check(response.StatusCode == HttpStatusCode.Forbidden, "an unsolicited approval cannot create trust");
        using var oversized = new ByteArrayContent(new byte[40000]); oversized.Headers.ContentType = new("application/json");
        response = await stranger.PostAsync("pair/request", oversized);
        Check(!response.IsSuccessStatusCode, "oversized pairing body is refused");
        using var chunked = new HttpRequestMessage(HttpMethod.Post, "pair/request") { Content = new ByteArrayContent(new byte[40000]) };
        chunked.Content.Headers.ContentType = new("application/json"); chunked.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await stranger.SendAsync(chunked);
        Check(!chunkedResponse.IsSuccessStatusCode, "chunked pairing bodies enforce the same streaming size limit");
    }
    await a.Federation.RequestPairAsync(b.Advertisement, default);
    var outbound = a.Federation.PairingOffers.Single(); var incoming = b.Federation.PairingOffers.Single();
    Check(outbound.VerificationCode == incoming.VerificationCode && outbound.RequestId == incoming.RequestId, "both local users see the same identity-bound verification code");
    Check(outbound.VerificationCode.Length == 35 && outbound.VerificationCode.Split(' ').All(g => g.Length == 8 && g.All(char.IsAsciiHexDigit)),
        "comparison includes the complete 128-bit code in four readable groups");
    Check(a.Store.Peers().Count == 0 && b.Store.Peers().Count == 0, "request alone creates no trust on either broker");
    await b.Federation.ApprovePairAsync(incoming.RequestId, default);
    Check(a.Store.Peers().Count == 0 && b.Store.Peers().Count == 0 && b.Federation.PairingOffers.Single().LocalApproved &&
        !a.Federation.PairingOffers.Single().LocalApproved && a.Federation.PairingOffers.Single().RemoteApproved,
        "one final approval grants no access and cannot replace the initiating user's final confirmation");
    using (var notYet = b.Client(a.Identity))
        Check((await notYet.GetAsync("federation/directory")).StatusCode == HttpStatusCode.Forbidden,
            "first approval alone cannot read the remote directory");
    await a.Federation.ApprovePairAsync(outbound.RequestId, default);
    Check(a.Store.Peers().Single().BrokerId == b.Store.BrokerId && b.Store.Peers().Single().BrokerId == a.Store.BrokerId,
        "separate final approval on both workstations completes direct two-party trust");
    var sa = a.Register("a"); var sb = b.Register("b"); b.Register("private", true);
    var peerB = a.Store.Peers().Single(); var peerA = b.Store.Peers().Single();
    var directory = await a.Federation.GetDirectoryAsync(peerB, default);
    Check(directory.Sessions.Count == 1 && directory.Sessions[0].Address == sb.Address, "mutual TLS directory exports exact public sessions only");
    var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, sa.Address, sb.Address, "Synthetic federation message.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1));
    a.Store.Accept(message, a.Store.BrokerId);
    var receipt = await a.Federation.SubmitAsync(peerB, message, default);
    Check(receipt.Status == MessageStatus.Accepted && b.Store.Messages(sb.Address).Count == 1, "paired HTTPS submission records the bounded message once");
    await a.Federation.SubmitAsync(peerB, message, default);
    Check(b.Store.Messages(sb.Address).Count == 1, "same message retry does not enqueue a second copy");
    Check((await a.Federation.GetReceiptAsync(peerB, message.Id, default))?.Message.Id == message.Id, "only the authenticated participant can query a receipt");
    using (var transit = b.Client(a.Identity))
    {
        var response = await transit.PostAsJsonAsync("federation/messages", message with { Id = Guid.NewGuid(), Recipient = new(c.Store.BrokerId, "third", Guid.NewGuid()) });
        Check(!response.IsSuccessStatusCode, "paired credentials do not grant transitive forwarding");
        response = await transit.PostAsJsonAsync("pair/request", new { requestId = incoming.RequestId, nonce = new string('A', 64), peer = a.Advertisement, expiresAt = clock.GetUtcNow().AddMinutes(1) });
        Check(!response.IsSuccessStatusCode, "a consumed pairing request cannot be replayed");
    }
    using (var replacement = Identity(Path.Combine(scratch, "changed"), a.Store.BrokerId))
    using (var changed = b.Client(replacement))
        Check((await changed.GetAsync("federation/directory")).StatusCode == HttpStatusCode.Forbidden, "same broker name with a changed key never inherits paired trust");
    b.Federation.RevokePeer(a.Store.BrokerId);
    Check(b.Store.Messages(sb.Address).Single().Status == MessageStatus.Refused, "revocation immediately refuses queued inbound messages");
    await Refuses(() => a.Federation.GetDirectoryAsync(peerB, default), "revoked client loses directory access on its next request");
    await Refuses(() => b.Federation.GetDirectoryAsync(peerA, default), "revocation invalidates an already-held outgoing peer handle");
    using (var revoked = b.Client(a.Identity))
    using (var replay = new StringContent(b.Pairing.LastApproval!, Encoding.UTF8, "application/json"))
        Check((await revoked.PostAsync("pair/approve", replay)).StatusCode == HttpStatusCode.Forbidden && b.Store.Peers().Count == 0,
            "a consumed authenticated approval cannot restore revoked trust");
    a.Federation.RevokePeer(b.Store.BrokerId);
    await MutualPairingChecks.RunAsync(a, b, Check, Refuses);
    await c.Federation.RequestPairAsync(b.Advertisement, default);
    Check(b.Federation.PairingOffers.Single().ExpiresAt <= clock.GetUtcNow().AddMinutes(2), "clock-skewed initiators cannot extend the receiver's local offer deadline");
    Guid rejectedId = b.Federation.PairingOffers.Single().RequestId;
    b.Federation.RejectPair(rejectedId); c.Federation.RejectPair(rejectedId);
    await Refuses(() => b.Federation.ApprovePairAsync(rejectedId, default), "explicitly rejected pairing cannot become trust");
    await c.Federation.RequestPairAsync(b.Advertisement, default);
    var expiredId = b.Federation.PairingOffers.Single().RequestId;
    clock.Advance(TimeSpan.FromMinutes(3));
    Check(b.Federation.PairingOffers.Count == 0, "pairing offers expire after two minutes");
    await Refuses(() => b.Federation.ApprovePairAsync(expiredId, default), "expired pairing cannot become trust");
    Check(!b.Store.Peers().Any(p => p.BrokerId == c.Store.BrokerId), "expired or rejected offers create no transitive trust");
    using (var offers = b.Client(c.Identity))
    {
        for (int i = 0; i < 32; i++)
        {
            using var answer = await offers.PostAsJsonAsync("pair/request", new { requestId = Guid.NewGuid(), nonce = new string('C', 64), peer = c.Advertisement, expiresAt = clock.GetUtcNow().AddMinutes(1) });
            if (!answer.IsSuccessStatusCode) throw new InvalidOperationException("Bounded incoming offer was unexpectedly refused.");
        }
        using var overflow = await offers.PostAsJsonAsync("pair/request", new { requestId = Guid.NewGuid(), nonce = new string('D', 64), peer = c.Advertisement, expiresAt = clock.GetUtcNow().AddMinutes(1) });
        Check(!overflow.IsSuccessStatusCode && b.Federation.PairingOffers.Count == 32, "untrusted incoming pairing offers are bounded at 32");
        foreach (var offer in b.Federation.PairingOffers) b.Federation.RejectPair(offer.RequestId);
    }
    var ip = IPAddress.Parse("10.23.4.5"); var cache = new DiscoveryCache(clock);
    for (int i = 0; i < 40; i++)
    {
        var metadata = new BrokerAdvertisement(Guid.NewGuid(), "Never included in DNS", "https://10.23.4.5:7433/", new string('A', 64), clock.GetUtcNow());
        byte[] packet = DnsSdCodec.Announce(metadata);
        Check(DnsSdCodec.Parse(packet, IPAddress.Parse("10.23.4.6"), clock.GetUtcNow()).Count == 0, "DNS advertised address is bound to its actual packet source");
        cache.Observe(packet, ip, a.Store.BrokerId);
        Check(!Encoding.ASCII.GetString(packet).Contains(metadata.Name), "DNS contains identity and endpoint metadata without user-chosen names or messages");
    }
    Check(cache.Entries.Count == 32, "discovery cache is bounded at 32 brokers");
    clock.Advance(TimeSpan.FromSeconds(121)); Check(cache.Entries.Count == 0, "discovery metadata expires at its bounded TTL");
    for (int i = 0; i < 200; i++) { byte[] invalid = RandomNumberGenerator.GetBytes(i % 1400); DnsSdCodec.Parse(invalid, ip, clock.GetUtcNow()); }
    Check(DnsSdCodec.Parse(new byte[1401], ip, clock.GetUtcNow()).Count == 0 && DnsSdCodec.IsQuery(DnsSdCodec.Query()), "malformed/oversized DNS is bounded and the service question parses");
    byte[] wrongClass = DnsSdCodec.Query(); wrongClass[^1] = 3;
    Check(!DnsSdCodec.IsQuery(wrongClass) && !DnsSdCodec.IsQuery(DnsSdCodec.Query()[..^1]), "non-IN and truncated DNS questions cannot trigger announcements");
    var goodbye = new BrokerAdvertisement(Guid.NewGuid(), "Synthetic", "https://10.23.4.5:7433/", new string('A', 64), clock.GetUtcNow());
    cache.Observe(DnsSdCodec.Announce(goodbye), ip, a.Store.BrokerId);
    cache.Observe(DnsSdCodec.Announce(goodbye, 0), ip, a.Store.BrokerId);
    Check(cache.Entries.Count == 0, "zero-TTL discovery records immediately remove metadata");
    cache.Observe(DnsSdCodec.Announce(goodbye), ip, goodbye.BrokerId);
    Check(cache.Entries.Count == 0, "discovery never lists its own broker identity");
    string restartPath = Path.Combine(scratch, "restart"); Guid brokerId; string fingerprint;
    using (var persisted = new BrokerStore(restartPath))
    using (var identity = Identity(restartPath, persisted.BrokerId))
    { brokerId = persisted.BrokerId; fingerprint = identity.Fingerprint; persisted.AuthorizePeer(peerB); }
    using (var persisted = new BrokerStore(restartPath))
    using (var identity = BrokerIdentity.LoadOrCreate(restartPath, persisted.BrokerId))
        Check(persisted.BrokerId == brokerId && identity.Fingerprint == fingerprint && persisted.Peers().Single() == peerB, "restart preserves broker identity and explicit certificate-pinned trust");
    Console.WriteLine($"RESULT=PASS assertions={assertions}");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("RESULT=FAIL"); return 1; }
finally
{
    // Only exact keys returned by this invocation's new scratch identities. Never enumerate the key store.
    var cleanupFailures = new List<Exception>();
    foreach (string name in ownedKeys)
    {
        try { using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        catch (CryptographicException ex) { cleanupFailures.Add(ex); }
    }
    if (cleanupFailures.Count != 0) throw new AggregateException("Owned test key cleanup failed.", cleanupFailures);
    /* Failed encrypted metadata artifacts are intentionally retained for scoped diagnosis. */
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan time) => now += time;
}
sealed class SkewClock(TimeProvider parent, TimeSpan skew) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => parent.GetUtcNow() + skew;
}
sealed class Node : IAsyncDisposable
{
    public required string Directory { get; init; }
    public required BrokerStore Store { get; init; }
    public required BrokerIdentity Identity { get; init; }
    public required PeerFederation Federation { get; init; }
    public required WebApplication App { get; init; }
    public required string Endpoint { get; init; }
    public required string HttpEndpoint { get; init; }
    public required PairingProbe Pairing { get; init; }
    public BrokerAdvertisement Advertisement => new(Store.BrokerId, "Synthetic broker", Endpoint, Identity.Fingerprint, DateTimeOffset.UtcNow);
    public static async Task<Node> Create(string directory, TimeProvider clock, Func<string, Guid, BrokerIdentity> loadIdentity)
    {
        var store = new BrokerStore(directory, clock: clock); var identity = loadIdentity(directory, store.BrokerId);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => {
            k.Listen(IPAddress.Loopback, 0);
            k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(new HttpsConnectionAdapterOptions {
                ServerCertificate = identity.Certificate, ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                ClientCertificateValidation = (_, _, _) => true }));
        });
        var app = builder.Build(); var probe = new PairingProbe();
        app.Use(async (context, next) => {
            if (context.Request.Path != "/pair/approve") { await next(context); return; }
            context.Request.EnableBuffering();
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
                probe.LastApproval = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            if (!probe.DropNextApprovalResponse) { await next(context); return; }
            probe.DropNextApprovalResponse = false;
            Stream original = context.Response.Body;
            await using var capture = new MemoryStream();
            context.Response.Body = capture;
            try { await next(context); context.Abort(); }
            finally { context.Response.Body = original; }
        });
        var federation = new PeerFederation(store, identity, new(directory, "Synthetic broker", LanPort: 0), enableDiscovery: false, clock: clock);
        federation.MapEndpoints(app); await app.StartAsync(); await federation.StartAsync(default);
        return new Node { Directory = directory, Store = store, Identity = identity, Federation = federation, App = app,
            Endpoint = app.Urls.Single(u => u.StartsWith("https://", StringComparison.Ordinal)) + "/",
            HttpEndpoint = app.Urls.Single(u => u.StartsWith("http://", StringComparison.Ordinal)) + "/", Pairing = probe };
    }
    public BrokerSession Register(string name, bool privateSession = false)
    {
        var session = new BrokerSession(new(Store.BrokerId, name, Guid.NewGuid()), name, "synthetic",
            SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(1), privateSession);
        Store.Register(session); return session;
    }
    public HttpClient Client(BrokerIdentity? client = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == Identity.Fingerprint };
        if (client is not null) handler.ClientCertificates.Add(client.Certificate);
        return new(handler) { BaseAddress = new Uri(Endpoint), Timeout = TimeSpan.FromSeconds(10) };
    }
    public async ValueTask DisposeAsync() { await Federation.DisposeAsync(); await App.StopAsync(); await App.DisposeAsync(); Identity.Dispose(); Store.Dispose(); }
}

sealed class PairingProbe
{
    public bool DropNextApprovalResponse;
    public string? LastApproval;
}
