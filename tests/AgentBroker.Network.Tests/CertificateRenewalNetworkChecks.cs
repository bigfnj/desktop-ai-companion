using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AgentBroker.Worker.Security;

internal static class CertificateRenewalNetworkChecks
{
    internal static async Task RunAsync(string scratch, ISet<string> ownedKeys, Action<bool, string> check)
    {
        // Only fresh, owned synthetic identities are dated in the past. TLS itself uses the real clock.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrokerIdentity Renewed(string directory, Guid brokerId)
        {
            ownedKeys.Add(BrokerIdentity.KeyNameFor(directory, brokerId));
            string originalPin;
            using (var original = BrokerIdentity.LoadOrCreate(directory, brokerId, now.AddYears(-6)))
                originalPin = original.Fingerprint;
            var renewed = BrokerIdentity.LoadOrCreate(directory, brokerId, now);
            try
            {
                check(renewed.Fingerprint == originalPin &&
                    renewed.Certificate.GetCertHashString(HashAlgorithmName.SHA256) != originalPin,
                    "TLS_RENEWAL_STABLE_PIN: expired synthetic identity renews its leaf and retains its original paired pin");
                return renewed;
            }
            catch { renewed.Dispose(); throw; }
        }

        await using var a = await Node.Create(Path.Combine(scratch, "renewal-a"), TimeProvider.System, Renewed);
        await using var b = await Node.Create(Path.Combine(scratch, "renewal-b"), TimeProvider.System, Renewed);
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        await b.Federation.ApprovePairAsync(b.Federation.PairingOffers.Single().RequestId, default);
        await a.Federation.ApprovePairAsync(a.Federation.PairingOffers.Single().RequestId, default);
        var peerB = a.Store.Peers().Single();
        var peerA = b.Store.Peers().Single();
        check(peerB.CertificateSha256 == b.Identity.Fingerprint && peerA.CertificateSha256 == a.Identity.Fingerprint,
            "TLS_RENEWAL_PAIRING: both explicit approvals retain stable original pins over renewed TLS leaves");
        var rowA = a.Register("renewal-local-a");
        var rowB = b.Register("renewal-local-b");
        var fromB = await a.Federation.GetDirectoryAsync(peerB, default);
        var fromA = await b.Federation.GetDirectoryAsync(peerA, default);
        check(fromB.Sessions.Any(row => row.Address == rowB.Address) && fromA.Sessions.Any(row => row.Address == rowA.Address),
            "TLS_RENEWAL_MUTUAL_AUTH: production outbound pin validation and inbound authentication accept both renewed identities");

        using (var legacyHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions {
                ClientCertificates = new X509CertificateCollection { a.Identity.Certificate },
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is X509Certificate2 cert &&
                    cert.GetCertHashString(HashAlgorithmName.SHA256) == peerB.CertificateSha256 } })
        using (var legacyClient = new HttpClient(legacyHandler) { BaseAddress = new Uri(b.Endpoint), Timeout = TimeSpan.FromSeconds(5) })
        {
            bool refused = false;
            try { using var response = await legacyClient.GetAsync("broker/info"); }
            catch (HttpRequestException error) when (error.InnerException is System.Security.Authentication.AuthenticationException)
            { refused = true; }
            check(refused, "TLS_RENEWAL_LEGACY_PIN_REFUSES: a legacy exact-leaf validator rejects a changed certificate");
        }

        a.Store.RevokePeer(peerB.BrokerId);
        bool revoked = false;
        try { await b.Federation.GetDirectoryAsync(peerA, default); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Forbidden)
        { revoked = true; }
        check(revoked, "TLS_RENEWAL_REVOCATION: a valid continuity proof cannot restore revoked peer authority");
    }
}
