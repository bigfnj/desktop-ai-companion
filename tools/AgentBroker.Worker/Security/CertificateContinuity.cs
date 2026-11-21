using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AgentBroker.Worker.Security;

/// <summary>Bounded application identity continuity on the original key, independent of certificate renewal.</summary>
internal static class CertificateContinuity
{
    // Standard noncritical SubjectDirectoryAttributes; the UUID attribute is parsed only by managed ASN.1.
    internal const string AnchorExtensionOid = "2.5.29.9";
    internal const string AnchorAttributeOid = "2.25.87879080473710008048449786743199618470";
    internal const int MaximumAnchorBytes = 16384;
    internal const int MaximumProofBytes = MaximumAnchorBytes + 128;
    internal const int MaximumLeafBytes = 32768;
    private const string Sha256RsaOid = "1.2.840.113549.1.1.11";

    internal static bool IsValidAnchor(X509Certificate2 anchor, Guid brokerId)
    {
        try { return IdentityPolicy(anchor, brokerId, MaximumAnchorBytes) && Proofs(anchor).Length == 0; }
        catch (Exception error) when (InvalidInput(error)) { return false; }
    }

    internal static X509Extension CreateAnchorExtension(X509Certificate2 anchor, Guid brokerId)
    {
        if (!IsValidAnchor(anchor, brokerId)) throw new ArgumentException("The original identity anchor is invalid.", nameof(anchor));
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); writer.PushSequence(); writer.WriteObjectIdentifier(AnchorAttributeOid); writer.PushSetOf();
        writer.PushSequence(); writer.WriteInteger(1); writer.WriteOctetString(anchor.RawData); writer.PopSequence();
        writer.PopSetOf(); writer.PopSequence(); writer.PopSequence();
        return new X509Extension(AnchorExtensionOid, writer.Encode(), critical: false);
    }

    // Pure verification: TLS still proves possession; the store separately owns revocation and authorization.
    internal static bool MatchesPinnedCertificate(X509Certificate2 presented, Guid brokerId, string anchorSha256, DateTimeOffset now)
    {
        try
        {
            if (anchorSha256 is not { Length: 64 } || !anchorSha256.All(Uri.IsHexDigit) ||
                !IdentityPolicy(presented, brokerId, MaximumLeafBytes) || !Current(presented, now)) return false;
            var proofs = Proofs(presented);
            if (proofs.Length == 0)
                return presented.GetCertHashString(HashAlgorithmName.SHA256).Equals(anchorSha256, StringComparison.OrdinalIgnoreCase);
            if (proofs.Length != 1 || proofs[0].Critical || proofs[0].RawData.Length > MaximumProofBytes) return false;
            var proof = new AsnReader(proofs[0].RawData, AsnEncodingRules.DER);
            var attributes = proof.ReadSequence(); proof.ThrowIfNotEmpty();
            var attribute = attributes.ReadSequence(); attributes.ThrowIfNotEmpty();
            if (attribute.ReadObjectIdentifier() != AnchorAttributeOid) return false;
            var values = attribute.ReadSetOf(); attribute.ThrowIfNotEmpty();
            var fields = values.ReadSequence(); values.ThrowIfNotEmpty();
            if (!fields.TryReadInt32(out int version) || version != 1) return false;
            byte[] anchorDer = fields.ReadOctetString(); fields.ThrowIfNotEmpty();
            if (anchorDer.Length is 0 or > MaximumAnchorBytes ||
                !Convert.ToHexString(SHA256.HashData(anchorDer)).Equals(anchorSha256, StringComparison.OrdinalIgnoreCase)) return false;
            using var anchor = X509CertificateLoader.LoadCertificate(anchorDer);
            if (!anchor.RawData.AsSpan().SequenceEqual(anchorDer) || !IsValidAnchor(anchor, brokerId)) return false;
            using var anchorKey = anchor.GetRSAPublicKey();
            using var leafKey = presented.GetRSAPublicKey();
            return anchorKey is not null && leafKey is not null &&
                anchorKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(leafKey.ExportSubjectPublicKeyInfo());
        }
        catch (Exception error) when (InvalidInput(error)) { return false; }
    }

    private static bool Current(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime && certificate.NotAfter.ToUniversalTime() > now.UtcDateTime;
    private static X509Extension[] Proofs(X509Certificate2 certificate) => certificate.Extensions.Cast<X509Extension>()
        .Where(extension => extension.Oid?.Value == AnchorExtensionOid).ToArray();

    private static bool IdentityPolicy(X509Certificate2 certificate, Guid brokerId, int maximumBytes)
    {
        if (certificate is null || brokerId == Guid.Empty || certificate.RawData.Length > maximumBytes ||
            certificate.GetNameInfo(X509NameType.SimpleName, false) != "AgentBroker-" + brokerId.ToString("N") ||
            certificate.GetNameInfo(X509NameType.DnsName, false) != "broker-" + brokerId.ToString("N") + ".local" ||
            !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) ||
            certificate.NotAfter.ToUniversalTime() <= certificate.NotBefore.ToUniversalTime()) return false;
        var extensions = certificate.Extensions.Cast<X509Extension>().ToArray();
        if (extensions.GroupBy(extension => extension.Oid?.Value, StringComparer.Ordinal).Any(group => group.Count() != 1)) return false;
        var constraints = extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var usage = extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var purposes = extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (constraints is not { CertificateAuthority: false } || constraints.HasPathLengthConstraint ||
            usage?.KeyUsages != (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment) ||
            purposes is null || purposes.EnhancedKeyUsages.Count != 2 ||
            !purposes.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1") ||
            !purposes.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2")) return false;
        if (extensions.Any(extension => extension.Critical && extension.Oid?.Value is not
            ("2.5.29.19" or "2.5.29.15" or "2.5.29.37" or "2.5.29.17"))) return false;
        using var rsa = certificate.GetRSAPublicKey();
        return rsa is { KeySize: 3072 } && SelfSigned(certificate.RawData, rsa);
    }

    private static bool SelfSigned(byte[] encoded, RSA publicKey)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        var certificate = reader.ReadSequence(); reader.ThrowIfNotEmpty();
        ReadOnlyMemory<byte> tbs = certificate.ReadEncodedValue();
        ReadOnlyMemory<byte> algorithm = certificate.ReadEncodedValue();
        byte[] signature = certificate.ReadBitString(out int unused); certificate.ThrowIfNotEmpty();
        if (unused != 0 || !SignatureAlgorithm(algorithm)) return false;
        var bodyReader = new AsnReader(tbs, AsnEncodingRules.DER);
        var body = bodyReader.ReadSequence(); bodyReader.ThrowIfNotEmpty();
        var version = body.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        if (!version.TryReadInt32(out int number) || number != 2) return false;
        version.ThrowIfNotEmpty();
        _ = body.ReadIntegerBytes();
        if (!body.ReadEncodedValue().Span.SequenceEqual(algorithm.Span)) return false;
        return publicKey.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static bool SignatureAlgorithm(ReadOnlyMemory<byte> encoded)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        var algorithm = reader.ReadSequence(); reader.ThrowIfNotEmpty();
        if (algorithm.ReadObjectIdentifier() != Sha256RsaOid) return false;
        algorithm.ReadNull(); algorithm.ThrowIfNotEmpty();
        return true;
    }
    private static bool InvalidInput(Exception error) => error is CryptographicException or AsnContentException or
        ArgumentException or InvalidOperationException or OverflowException;
}
