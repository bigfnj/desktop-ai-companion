using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AgentBroker.Worker.Security;

internal static class CertificateContinuityChecks
{
    internal static Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: CERTIFICATE_CONTINUITY_EXECUTED");
        Console.WriteLine("CONTINUITY_WORKER_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BrokerIdentity).Assembly.Location))));
        var now = new DateTimeOffset(2035, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var broker = Guid.NewGuid();
        using var key = RSA.Create(3072);
        using var otherKey = RSA.Create(3072);
        using var legacy = Certificate(key, broker, now.AddYears(-1), now.AddYears(4));
        using var anchor = Certificate(key, broker, now.AddYears(-6), now.AddYears(-1));
        string legacyPin = legacy.GetCertHashString(HashAlgorithmName.SHA256);
        string pin = anchor.GetCertHashString(HashAlgorithmName.SHA256);
        check(CertificateContinuity.IsValidAnchor(anchor, broker), "CERT_ANCHOR_HISTORICAL: expired original remains a structurally verified public anchor");
        check(CertificateContinuity.MatchesPinnedCertificate(legacy, broker, legacyPin, now), "CERT_LEGACY_EXACT_PIN: a valid original legacy certificate remains accepted");
        check(!CertificateContinuity.MatchesPinnedCertificate(legacy, broker, new string('0', 64), now), "CERT_ANCHOR_EXACT_HASH: an unrelated full-certificate pin refuses");
        check(!CertificateContinuity.MatchesPinnedCertificate(legacy, Guid.NewGuid(), legacyPin, now), "CERT_BROKER_EXACT: a matching certificate cannot authorize another broker ID");
        check(!CertificateContinuity.MatchesPinnedCertificate(anchor, broker, pin, now), "CERT_EXPIRED_LIVE_REFUSED: an expired anchor cannot itself serve as the live leaf");
        var proof = CertificateContinuity.CreateAnchorExtension(anchor, broker);
        using var renewed = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), proof);
        check(renewed.GetCertHashString(HashAlgorithmName.SHA256) != pin && CertificateContinuity.MatchesPinnedCertificate(renewed, broker, pin, now),
            "CERT_RENEWED_ANCHOR_CONTINUITY: same-key renewal authenticates against the original expired pin");
        check(!CertificateContinuity.MatchesPinnedCertificate(renewed, broker, legacyPin, now), "CERT_PROOF_EXACT_HASH: another certificate on the same key cannot replace the exact anchor");
        using var wrongKey = Certificate(otherKey, broker, now.AddDays(-1), now.AddYears(5), proof);
        check(!CertificateContinuity.MatchesPinnedCertificate(wrongKey, broker, pin, now), "CERT_SPKI_CONTINUITY: a valid self-signed leaf on another key refuses");
        using var wrongBroker = Certificate(key, Guid.NewGuid(), now.AddDays(-1), now.AddYears(5), proof);
        check(!CertificateContinuity.MatchesPinnedCertificate(wrongBroker, broker, pin, now), "CERT_RENEWED_BROKER: same-key proof cannot change the live broker identity");
        check(!CertificateContinuity.MatchesPinnedCertificate(renewed, broker, pin, now.AddYears(-1)) &&
            !CertificateContinuity.MatchesPinnedCertificate(renewed, broker, pin, now.AddYears(6)), "CERT_LIVE_TIME_BOUND: explicit time must place the live leaf within its validity");
        check(!CertificateContinuity.MatchesPinnedCertificate(renewed, broker, pin, new DateTimeOffset(renewed.NotAfter.ToUniversalTime())),
            "CERT_LIVE_EXPIRY_EXCLUSIVE: exact live expiry refuses");
        byte[] tampered = renewed.RawData; tampered[^1] ^= 1;
        using var badSignature = X509CertificateLoader.LoadCertificate(tampered);
        check(!CertificateContinuity.MatchesPinnedCertificate(badSignature, broker, pin, now), "CERT_SELF_SIGNATURE: changed signed bytes cannot pass same-key continuity");
        using var wrongAlgorithm = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), proof, hash: HashAlgorithmName.SHA384);
        check(!CertificateContinuity.MatchesPinnedCertificate(wrongAlgorithm, broker, pin, now), "CERT_SIGNATURE_PROFILE: a different certificate signature algorithm refuses");
        using var wrongUsage = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), proof, usage: X509KeyUsageFlags.DigitalSignature);
        check(!CertificateContinuity.MatchesPinnedCertificate(wrongUsage, broker, pin, now), "CERT_USAGE_PROFILE: a different live key-usage policy refuses");
        using var authority = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), proof, authority: true);
        check(!CertificateContinuity.MatchesPinnedCertificate(authority, broker, pin, now), "CERT_NOT_CA: same-key renewal creates no certificate authority");
        MoreProofs(check, key, broker, anchor, renewed, proof, pin, now);
        return Task.CompletedTask;
    }

    private static void MoreProofs(Action<bool, string> check, RSA key, Guid broker, X509Certificate2 anchor,
        X509Certificate2 renewed, X509Extension proof, string pin, DateTimeOffset now)
    {
        using var unknownVersion = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), RawProof(anchor.RawData, 2));
        check(!CertificateContinuity.MatchesPinnedCertificate(unknownVersion, broker, pin, now), "CERT_PROOF_VERSION: unsupported proof version refuses without fallback");
        using var trailing = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), new X509Extension(CertificateContinuity.AnchorExtensionOid, proof.RawData.Concat(new byte[] { 0 }).ToArray(), false));
        check(!CertificateContinuity.MatchesPinnedCertificate(trailing, broker, pin, now), "CERT_PROOF_TRAILING: trailing proof bytes refuse");
        using var critical = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), new X509Extension(CertificateContinuity.AnchorExtensionOid, proof.RawData, true));
        check(!CertificateContinuity.MatchesPinnedCertificate(critical, broker, pin, now), "CERT_PROOF_CRITICAL: the application proof must be noncritical");
        using var nested = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), RawProof(renewed.RawData));
        check(!CertificateContinuity.MatchesPinnedCertificate(nested, broker, renewed.GetCertHashString(HashAlgorithmName.SHA256), now) &&
            !CertificateContinuity.IsValidAnchor(renewed, broker), "CERT_PROOF_NO_NESTING: a renewed leaf cannot become a recursive original anchor");
        using var largeAnchor = Certificate(key, broker, now.AddYears(-6), now.AddYears(-1), padding: CertificateContinuity.MaximumAnchorBytes);
        check(largeAnchor.RawData.Length > CertificateContinuity.MaximumAnchorBytes && !CertificateContinuity.IsValidAnchor(largeAnchor, broker), "CERT_ANCHOR_BOUND: signed original metadata is bounded");
        using var largeLeaf = Certificate(key, broker, now.AddDays(-1), now.AddYears(5), proof, padding: CertificateContinuity.MaximumLeafBytes);
        check(largeLeaf.RawData.Length > CertificateContinuity.MaximumLeafBytes && !CertificateContinuity.MatchesPinnedCertificate(largeLeaf, broker, pin, now), "CERT_LEAF_BOUND: an otherwise valid same-key leaf is bounded");
        using var later = Certificate(key, broker, now.AddDays(-1), now.AddYears(4), CertificateContinuity.CreateAnchorExtension(anchor, broker));
        check(CertificateContinuity.MatchesPinnedCertificate(later, broker, pin, now) && later.Extensions[CertificateContinuity.AnchorExtensionOid]!.RawData.SequenceEqual(proof.RawData),
            "CERT_PROOF_CONSTANT_SIZE: further renewal retains identical original proof without history growth");
    }

    private static X509Extension RawProof(byte[] anchor, int version = 1)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); writer.PushSequence(); writer.WriteObjectIdentifier(CertificateContinuity.AnchorAttributeOid); writer.PushSetOf();
        writer.PushSequence(); writer.WriteInteger(version); writer.WriteOctetString(anchor); writer.PopSequence();
        writer.PopSetOf(); writer.PopSequence(); writer.PopSequence();
        return new(CertificateContinuity.AnchorExtensionOid, writer.Encode(), false);
    }

    private static X509Certificate2 Certificate(RSA key, Guid broker, DateTimeOffset start, DateTimeOffset end,
        X509Extension? proof = null, HashAlgorithmName? hash = null,
        X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, bool authority = false, int padding = 0)
    {
        var request = new CertificateRequest("CN=AgentBroker-" + broker.ToString("N"), key, hash ?? HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("broker-" + broker.ToString("N") + ".local"); request.CertificateExtensions.Add(san.Build());
        if (proof is not null) request.CertificateExtensions.Add(proof);
        if (padding != 0)
        {
            var data = new AsnWriter(AsnEncodingRules.DER); data.WriteOctetString(new byte[padding]);
            request.CertificateExtensions.Add(new X509Extension("1.2.3.4.56789", data.Encode(), false));
        }
        return request.CreateSelfSigned(start, end);
    }
}
