using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBroker.Core;
using AgentBroker.Worker.Discovery;
using AgentBroker.Worker.Security;

namespace AgentBroker.Worker.Federation;

public sealed class PeerFederation : IPeerFederation
{
    private sealed record Request(Guid RequestId, string Nonce, BrokerAdvertisement Peer, DateTimeOffset ExpiresAt);
    private sealed record Approval(Guid RequestId, string Nonce, BrokerAdvertisement Peer, string VerificationCode);
    private sealed record ApprovalAnswer(Guid RequestId, string Nonce, BrokerAdvertisement Peer, string VerificationCode, bool LocalApproved);
    private sealed record PairingConflict(string Error);
    private sealed class Pending(PairingOffer offer, string nonce, BrokerAdvertisement local)
    {
        public PairingOffer Offer = offer;
        public string Nonce { get; } = nonce;
        public BrokerAdvertisement Local { get; } = local;
    }
    private sealed record Finished(BrokerAdvertisement Peer, BrokerAdvertisement Local, string Nonce,
        string VerificationCode, DateTimeOffset Until, bool Authorized);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    private readonly object gate = new();
    private readonly BrokerStore store;
    private readonly BrokerIdentity identity;
    private readonly WorkerOptions options;
    private readonly TimeProvider clock;
    private readonly bool enableDiscovery;
    private readonly Dictionary<Guid, Pending> pending = new();
    private readonly Dictionary<Guid, Finished> consumed = new();
    private WebApplication? app;
    private MdnsDiscovery? discovery;
    private int port;
    public PeerFederation(BrokerStore store, BrokerIdentity identity, WorkerOptions options, bool enableDiscovery = true, TimeProvider? clock = null)
    { this.store = store; this.identity = identity; this.options = options; this.enableDiscovery = enableDiscovery; this.clock = clock ?? TimeProvider.System; port = options.LanPort; }
    private DateTimeOffset Now => clock.GetUtcNow();
    public BrokerAdvertisement LocalAdvertisement => Local(PrivateAddresses().FirstOrDefault() ?? IPAddress.Loopback);
    public IReadOnlyList<BrokerAdvertisement> Discovered => discovery?.Entries ?? [];
    public IReadOnlyList<PairingOffer> PairingOffers { get { lock (gate) { Trim(); return pending.Values.Select(p => p.Offer).ToArray(); } } }
    private BrokerAdvertisement Local(IPAddress address, int? localPort = null) => new(store.BrokerId, options.Name,
        $"https://{NetworkPolicy.Normalize(address)}:{localPort ?? port}/", identity.Fingerprint, Now);
    public static IReadOnlyList<IPAddress> PrivateAddresses() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => NetworkPolicy.PrivateV4(a, false)).Distinct().Take(4).ToArray();
    public Guid? AuthenticatePeer(HttpContext context)
    {
        if (!context.Request.IsHttps || context.Connection.RemoteIpAddress is not { } remote || !NetworkPolicy.PrivateV4(remote) ||
            context.Connection.ClientCertificate is not { } cert) return null;
        string fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
        foreach (var peer in store.Peers())
            if (fingerprint.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase) && BrokerIdentity.IsFor(cert, peer.BrokerId))
            {
                try { NetworkPolicy.Endpoint(peer.Endpoint, remote); return peer.BrokerId; }
                catch (InvalidDataException) { return null; }
            }
        return null;
    }
    private void PairCertificate(HttpContext context, BrokerAdvertisement peer)
    {
        if (!context.Request.IsHttps || context.Connection.RemoteIpAddress is not { } remote || context.Connection.ClientCertificate is not { } cert ||
            !BrokerIdentity.IsFor(cert, peer.BrokerId) || !cert.GetCertHashString(HashAlgorithmName.SHA256).Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Pairing identity does not match its TLS caller.");
        NetworkPolicy.Advertisement(peer, remote);
        if (peer.BrokerId == store.BrokerId) throw new InvalidDataException("Cannot pair the broker with itself.");
        if (store.Peers().Any(p => p.BrokerId == peer.BrokerId && !p.CertificateSha256.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Paired identity changed.");
    }
    public void MapEndpoints(WebApplication application)
    {
        app = application;
        application.MapGet("/broker/info", (HttpContext c) => Results.Json(Local(c.Connection.LocalIpAddress ?? IPAddress.Loopback,
            c.Request.IsHttps ? c.Connection.LocalPort : port), Json));
        application.MapPost("/pair/request", (Func<HttpContext, Task<IResult>>)((HttpContext c) => Handle(c, async () => {
            var request = await Read<Request>(c);
            PairCertificate(c, request.Peer);
            if (request.RequestId == Guid.Empty || request.Nonce.Length != 64 || !request.Nonce.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid pairing request.");
            lock (gate)
            {
                Trim();
                if (consumed.ContainsKey(request.RequestId) || pending.ContainsKey(request.RequestId))
                    throw new InvalidDataException("Pairing request was replayed or the offer limit was reached.");
                var local = Local(c.Connection.LocalIpAddress!, c.Connection.LocalPort);
                var collisions = pending.Values.Where(p => p.Offer.Outbound && SameIdentity(p.Offer.Peer, request.Peer)).ToArray();
                if (collisions.Length != 0)
                {
                    // Concurrent discovery clicks must produce one code. Only a genuine
                    // outbound collision uses this tie-break; unrelated inbound requests
                    // retain their normal replay and bounded-offer handling.
                    if (StringComparer.Ordinal.Compare(store.BrokerId.ToString("N"), request.Peer.BrokerId.ToString("N")) < 0)
                    {
                        var rejected = new PairingOffer(request.RequestId, request.Peer,
                            Verification(local, request.Peer, request.RequestId, request.Nonce), Now.AddMinutes(2), false);
                        Remember(new(rejected, request.Nonce, local));
                        throw new BrokerException("pairing_in_progress", "Compare the current pairing offer.");
                    }
                    foreach (var old in collisions) { Remember(old); pending.Remove(old.Offer.RequestId); }
                }
                if (pending.Count >= 32) throw new InvalidDataException("Pairing offer limit reached.");
                string code = Verification(local, request.Peer, request.RequestId, request.Nonce);
                // Each broker owns its two-minute deadline. Remote wall clocks are not authorization evidence.
                var offer = new PairingOffer(request.RequestId, request.Peer, code, Now.AddMinutes(2), false);
                pending.Add(request.RequestId, new(offer, request.Nonce, local));
                return Results.Json(offer, Json);
            }
        })));
        application.MapPost("/pair/approve", (Func<HttpContext, Task<IResult>>)((HttpContext c) => Handle(c, async () => {
            var approval = await Read<Approval>(c); PairCertificate(c, approval.Peer);
            lock (gate)
            {
                Trim();
                if (Completed(approval, out var done))
                    return Results.Json(new ApprovalAnswer(approval.RequestId, done.Nonce, done.Local, done.VerificationCode, true), Json);
                if (!pending.TryGetValue(approval.RequestId, out var item) || !Matches(item, approval))
                    throw new UnauthorizedAccessException("No matching current pairing offer.");
                // The pinned TLS caller attests its own explicit final approval. This never
                // replaces this workstation's separate comparison-code confirmation.
                item.Offer = item.Offer with { RemoteApproved = true };
                var answer = new ApprovalAnswer(approval.RequestId, item.Nonce, item.Local,
                    item.Offer.VerificationCode, item.Offer.LocalApproved);
                if (item.Offer.LocalApproved) FinalizePair(item);
                return Results.Json(answer, Json);
            }
        })));
        application.MapGet("/federation/directory", (Func<HttpContext, Task<IResult>>)((HttpContext c) => Handle(c, () => {
            RequirePeer(c); return Task.FromResult<IResult>(Results.Json(new PeerDirectory(store.BrokerId, store.Directory(true)), Json));
        })));
        application.MapPost("/federation/messages", (Func<HttpContext, Task<IResult>>)((HttpContext c) => Handle(c, async () => {
            Guid caller = RequirePeer(c); var message = await Read<BrokerMessage>(c);
            // Recheck after body read so revocation during a slow upload cannot authorize admission.
            if (RequirePeer(c) != caller) throw new UnauthorizedAccessException();
            return Results.Json(store.Accept(message, caller), Json);
        })));
        application.MapGet("/federation/receipts/{id:guid}", (HttpContext c, Guid id) => Handle(c, () => {
            Guid caller = RequirePeer(c); var receipt = store.FindReceipt(id);
            return Task.FromResult<IResult>(receipt is not null && (receipt.Message.Sender.BrokerId == caller || receipt.Message.Recipient.BrokerId == caller)
                ? Results.Json(receipt, Json) : Results.NotFound());
        }));
    }
    private Guid RequirePeer(HttpContext c) => AuthenticatePeer(c) ?? throw new UnauthorizedAccessException("An explicitly paired TLS identity is required.");
    private static async Task<IResult> Handle(HttpContext c, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (BrokerException ex) { return Results.Json(new { error = ex.Code }, statusCode: 409); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { return Results.BadRequest(new { error = "invalid_request" }); }
        catch (OperationCanceledException) when (!c.RequestAborted.IsCancellationRequested) { return Results.StatusCode(408); }
    }
    public async Task StartAsync(CancellationToken token)
    {
        if (!options.EnableLan) return;
        if (port == 0 && app is not null) port = app.Urls.Select(u => new Uri(u)).First(u => u.Scheme == "https").Port;
        if (port == 0) throw new InvalidOperationException("Start HTTPS before starting peer federation.");
        if (enableDiscovery) { discovery = new MdnsDiscovery(LocalAdvertisement, clock); await discovery.StartAsync(token); }
    }
    public async Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token)
    {
        NetworkPolicy.Advertisement(peer); if (peer.BrokerId == store.BrokerId) throw new InvalidDataException("Self pairing is forbidden.");
        if (store.Peers().Any(p => p.BrokerId == peer.BrokerId && !p.CertificateSha256.Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Paired certificate changed; revoke explicitly before pairing again.");
        lock (gate) { Trim(); if (pending.Values.Any(p => SameIdentity(p.Offer.Peer, peer))) return; }
        IPAddress? source = null;
        using var client = Client(peer, ip => source = ip);
        var info = await Exchange<BrokerAdvertisement>(client, HttpMethod.Get, "broker/info", null, token);
        if (!SameIdentity(info, peer)) throw new UnauthorizedAccessException("Displayed identity changed.");
        var local = Local(source ?? throw new IOException("No direct peer connection."));
        Guid id = Guid.NewGuid(); string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var offer = new PairingOffer(id, peer, Verification(local, peer, id, nonce), Now.AddMinutes(2), true);
        lock (gate)
        {
            Trim();
            // An incoming request or another local click can win while public info is
            // being checked. Keep that exact offer and its approval/code boundaries.
            if (pending.Values.Any(p => SameIdentity(p.Offer.Peer, peer))) return;
            if (pending.Count >= 32) throw new InvalidDataException("Pairing offer limit reached.");
            pending.Add(id, new(offer, nonce, local));
        }
        try
        {
            var answer = await Exchange<PairingOffer>(client, HttpMethod.Post, "pair/request", new Request(id, nonce, local, offer.ExpiresAt), token,
                pairingRequest: true);
            if (answer.RequestId != id || answer.VerificationCode != offer.VerificationCode || answer.Outbound ||
                answer.LocalApproved || answer.RemoteApproved || !SameIdentity(answer.Peer, local))
                throw new UnauthorizedAccessException("Pairing acknowledgement differs.");
        }
        catch { RejectPair(id); throw; }
    }
    public async Task ApprovePairAsync(Guid requestId, CancellationToken token)
    {
        Pending item;
        lock (gate)
        {
            Trim();
            if (consumed.TryGetValue(requestId, out var done) && IsAuthorized(done)) return;
            item = pending.GetValueOrDefault(requestId) ?? throw new InvalidDataException("No current pairing offer.");
            item.Offer = item.Offer with { LocalApproved = true };
        }
        // Never perform a callback while holding our inbound state lock: both users can
        // approve together, and each broker must remain able to receive the other's proof.
        using var client = Client(item.Offer.Peer);
        var answer = await Exchange<ApprovalAnswer>(client, HttpMethod.Post, "pair/approve",
            new Approval(requestId, item.Nonce, item.Local, item.Offer.VerificationCode), token);
        if (answer.RequestId != requestId || answer.Nonce != item.Nonce ||
            answer.VerificationCode != item.Offer.VerificationCode || !SameIdentity(answer.Peer, item.Offer.Peer))
            throw new UnauthorizedAccessException("Pairing approval acknowledgement differs.");
        lock (gate)
        {
            Trim();
            if (consumed.TryGetValue(requestId, out var done) && IsAuthorized(done)) return;
            if (!pending.TryGetValue(requestId, out var current) || !ReferenceEquals(current, item))
                throw new InvalidDataException("Pairing offer expired or was rejected.");
            item.Offer = item.Offer with { RemoteApproved = item.Offer.RemoteApproved || answer.LocalApproved };
            if (item.Offer.LocalApproved && item.Offer.RemoteApproved) FinalizePair(item);
        }
    }
    public void RejectPair(Guid requestId) { lock (gate) { if (pending.Remove(requestId, out var item)) Remember(item); } }
    public void RevokePeer(Guid brokerId)
    {
        lock (gate) { foreach (var item in pending.Values.Where(p => p.Offer.Peer.BrokerId == brokerId).ToArray()) { Remember(item); pending.Remove(item.Offer.RequestId); } store.RevokePeer(brokerId); }
    }
    public async Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token)
    {
        Current(peer); using var client = Client(Advert(peer));
        var directory = await Exchange<PeerDirectory>(client, HttpMethod.Get, "federation/directory", null, token, maximum: 262144); Current(peer);
        if (directory.BrokerId != peer.BrokerId || directory.Sessions.Count > 512 || directory.Sessions.Any(s => s.Address.BrokerId != peer.BrokerId || s.IsPrivate))
            throw new InvalidDataException("Peer directory contains invalid or transitive identities.");
        return directory;
    }
    public async Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
    {
        Current(peer); if (message.Sender.BrokerId != store.BrokerId || message.Recipient.BrokerId != peer.BrokerId) throw new InvalidDataException("Only direct local-to-peer submission is allowed.");
        using var client = Client(Advert(peer)); var receipt = await Exchange<MessageReceipt>(client, HttpMethod.Post, "federation/messages", message, token, maximum: 65536); Current(peer);
        if (receipt.Message != message) throw new InvalidDataException("Peer receipt differs from submitted message."); return receipt;
    }
    public async Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token)
    {
        Current(peer); using var client = Client(Advert(peer));
        try { var receipt = await Exchange<MessageReceipt>(client, HttpMethod.Get, "federation/receipts/" + messageId, null, token, maximum: 65536); Current(peer); if (receipt.Message.Id != messageId) throw new InvalidDataException("Wrong receipt identity."); return receipt; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    private void Current(PeerTrust peer) { if (!store.Peers().Any(p => p == peer)) throw new UnauthorizedAccessException("Peer is not currently authorized."); }
    private BrokerAdvertisement Advert(PeerTrust p) => new(p.BrokerId, p.Name, p.Endpoint, p.CertificateSha256, Now);
    private PeerTrust Trust(BrokerAdvertisement p) => new(p.BrokerId, p.Name, p.Endpoint, p.CertificateSha256, Now);
    private static bool SameIdentity(BrokerAdvertisement a, BrokerAdvertisement b) => a.BrokerId == b.BrokerId && NetworkPolicy.Endpoint(a.Endpoint) == NetworkPolicy.Endpoint(b.Endpoint) && a.CertificateSha256.Equals(b.CertificateSha256, StringComparison.OrdinalIgnoreCase);
    private static string Verification(BrokerAdvertisement a, BrokerAdvertisement b, Guid id, string nonce)
    {
        string[] identities = [a.BrokerId.ToString("N") + a.CertificateSha256.ToUpperInvariant(), b.BrokerId.ToString("N") + b.CertificateSha256.ToUpperInvariant()]; Array.Sort(identities, StringComparer.Ordinal);
        string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", identities) + "|" + id.ToString("N") + "|" + nonce)))[..32];
        return string.Join(" ", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 8, 8)));
    }
    private static bool Matches(Pending item, Approval proof) => item.Nonce == proof.Nonce &&
        item.Offer.VerificationCode == proof.VerificationCode && SameIdentity(item.Offer.Peer, proof.Peer);
    private bool IsAuthorized(Finished done) => done.Authorized && store.Peers().Any(p =>
        p.BrokerId == done.Peer.BrokerId && p.CertificateSha256.Equals(done.Peer.CertificateSha256, StringComparison.OrdinalIgnoreCase));
    private bool Completed(Approval proof, out Finished done)
    {
        if (consumed.TryGetValue(proof.RequestId, out var found) && found.Nonce == proof.Nonce &&
            found.VerificationCode == proof.VerificationCode && SameIdentity(found.Peer, proof.Peer) && IsAuthorized(found))
        { done = found; return true; }
        done = null!; return false;
    }
    private void FinalizePair(Pending item)
    {
        store.AuthorizePeer(Trust(item.Offer.Peer));
        Remember(item, authorized: true); pending.Remove(item.Offer.RequestId);
    }
    private void Remember(Pending p, bool authorized = false)
    {
        consumed[p.Offer.RequestId] = new(p.Offer.Peer, p.Local, p.Nonce, p.Offer.VerificationCode, Now.AddMinutes(10), authorized);
        if (consumed.Count > 256) consumed.Remove(consumed.MinBy(p => p.Value.Until).Key);
    }
    private void Trim() { foreach (var p in pending.Where(p => p.Value.Offer.ExpiresAt <= Now).ToArray()) { Remember(p.Value); pending.Remove(p.Key); } foreach (var p in consumed.Where(p => p.Value.Until <= Now).ToArray()) consumed.Remove(p.Key); }
    private HttpClient Client(BrokerAdvertisement peer, Action<IPAddress>? local = null)
    {
        Uri endpoint = NetworkPolicy.Endpoint(peer.Endpoint);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.Zero };
        handler.SslOptions = new SslClientAuthenticationOptions { ClientCertificates = new X509CertificateCollection { identity.Certificate },
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is X509Certificate2 cert && BrokerIdentity.IsFor(cert, peer.BrokerId) &&
                cert.GetCertHashString(HashAlgorithmName.SHA256).Equals(peer.CertificateSha256, StringComparison.OrdinalIgnoreCase) };
        handler.ConnectCallback = async (_, cancellation) => {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(endpoint.Host), endpoint.Port), cancellation); local?.Invoke(((IPEndPoint)socket.LocalEndPoint!).Address); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        return new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10) };
    }
    private static async Task<T> Exchange<T>(HttpClient client, HttpMethod method, string path, object? payload, CancellationToken token,
        int maximum = 32768, bool pairingRequest = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) { byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json); if (body.Length > 32768) throw new InvalidDataException("Request body is oversized."); request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (pairingRequest && response.StatusCode == HttpStatusCode.Conflict)
        {
            // Only the pinned peer's exact, bounded collision response has this meaning.
            // A generic conflict still follows the normal unsuccessful-HTTP path.
            byte[] conflict = await Bounded(await response.Content.ReadAsStreamAsync(timeout.Token), 1024, timeout.Token);
            try
            {
                if (JsonSerializer.Deserialize<PairingConflict>(conflict, Json)?.Error == "pairing_in_progress")
                    throw new BrokerException("pairing_in_progress", "Compare the current pairing offer.");
            }
            catch (JsonException) { }
        }
        response.EnsureSuccessStatusCode();
        byte[] bytes = await Bounded(await response.Content.ReadAsStreamAsync(timeout.Token), maximum, timeout.Token);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty response.");
    }
    private static async Task<T> Read<T>(HttpContext c)
    {
        if (c.Request.ContentLength > 32768 || !c.Request.HasJsonContentType()) throw new InvalidDataException("Invalid body.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return JsonSerializer.Deserialize<T>(await Bounded(c.Request.Body, 32768, timeout.Token), Json) ?? throw new InvalidDataException("Empty body.");
    }
    private static async Task<byte[]> Bounded(Stream stream, int maximum, CancellationToken token)
    {
        using var memory = new MemoryStream(); byte[] buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) { if (memory.Length + count > maximum) throw new InvalidDataException("Body exceeds its limit."); memory.Write(buffer, 0, count); }
        return memory.ToArray();
    }
    public async ValueTask DisposeAsync() { if (discovery is not null) await discovery.DisposeAsync(); }
}
