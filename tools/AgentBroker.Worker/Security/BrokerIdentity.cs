using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AgentBroker.Worker.Security;

/// <summary>A user-bound nonexportable Windows key and DPAPI-protected public certificate metadata.</summary>
public sealed class BrokerIdentity : IDisposable
{
    private sealed record StoredIdentity(int Version, Guid BrokerId, string KeyName, byte[] Certificate);
    private sealed record RenewableIdentity(int Version, Guid BrokerId, string KeyName, byte[] OriginalAnchorDER, byte[] CurrentCertificateDER);
    private const int MaximumCiphertextBytes = 131072;
    private static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(90);
    public Guid BrokerId { get; }
    public X509Certificate2 Certificate { get; }
    public string KeyName { get; }
    public string Fingerprint { get; }
    public DateTimeOffset RenewalDueAt => new DateTimeOffset(Certificate.NotAfter.ToUniversalTime()) - RenewalWindow;
    private BrokerIdentity(Guid id, X509Certificate2 certificate, string keyName, string fingerprint)
    { BrokerId = id; Certificate = certificate; KeyName = keyName; Fingerprint = fingerprint; }
    public static string KeyNameFor(string dataDir, Guid brokerId)
    {
        if (brokerId == Guid.Empty) throw new ArgumentException("Empty broker identity.");
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        return "AgentBroker.v1." + brokerId.ToString("N") + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];
    }
    public static BrokerIdentity LoadOrCreate(string dataDir, Guid brokerId) => LoadOrCreate(dataDir, brokerId, DateTimeOffset.UtcNow);

    internal static BrokerIdentity LoadOrCreate(string dataDir, Guid brokerId, DateTimeOffset now) =>
        LoadOrCreate(dataDir, brokerId, now, static (temporary, destination) => File.Replace(temporary, destination, null));

    // The replacement seam verifies failure ownership without touching another identity or the installed app.
    internal static BrokerIdentity LoadOrCreate(string dataDir, Guid brokerId, DateTimeOffset now, Action<string, string> replace)
    {
        if (brokerId == Guid.Empty) throw new ArgumentException("Empty broker identity.");
        ArgumentNullException.ThrowIfNull(replace);
        now = now.ToUniversalTime();
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        RejectLinks(directory, allowMissing: true);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        string path = Path.Combine(directory, "tls-identity.dat");
        string keyName = KeyNameFor(directory, brokerId);
        if (File.Exists(path))
        {
            RejectLinks(path);
            if (new FileInfo(path).Length > MaximumCiphertextBytes) throw new InvalidDataException("Identity ciphertext is oversized.");
            byte[] plain = UserProtection.Unseal(File.ReadAllBytes(path));
            try
            {
                var (anchorBytes, currentBytes) = ReadMetadata(plain, brokerId, keyName);
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                if (key.IsMachineKey || key.ExportPolicy != CngExportPolicies.None || key.AlgorithmGroup != CngAlgorithmGroup.Rsa)
                    throw new InvalidDataException("Stored runtime key has an unexpected policy.");
                using var rsa = new RSACng(key);
                using var anchor = X509CertificateLoader.LoadCertificate(anchorBytes);
                using var current = X509CertificateLoader.LoadCertificate(currentBytes);
                string fingerprint = anchor.GetCertHashString(HashAlgorithmName.SHA256);
                if (!anchor.RawData.AsSpan().SequenceEqual(anchorBytes) || !current.RawData.AsSpan().SequenceEqual(currentBytes) ||
                    !CertificateContinuity.IsValidAnchor(anchor, brokerId) ||
                    anchor.NotBefore.ToUniversalTime() > now.UtcDateTime || current.NotBefore.ToUniversalTime() > now.UtcDateTime)
                    throw InvalidIdentity();
                // Expired local metadata is recoverable only after its original identity and key validate.
                var lastValidInstant = new DateTimeOffset(current.NotAfter.ToUniversalTime()).AddTicks(-1);
                var validationTime = now < lastValidInstant ? now : lastValidInstant;
                if (!CertificateContinuity.MatchesPinnedCertificate(current, brokerId, fingerprint, validationTime)) throw InvalidIdentity();
                using var anchorPublicKey = anchor.GetRSAPublicKey();
                if (anchorPublicKey is null || !anchorPublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo()))
                    throw InvalidIdentity();
                if (now < new DateTimeOffset(current.NotAfter.ToUniversalTime()) - RenewalWindow)
                    return new(brokerId, current.CopyWithPrivateKey(rsa), keyName, fingerprint);

                var renewed = CreateCertificate(rsa, brokerId, now, anchor);
                try
                {
                    if (!CertificateContinuity.MatchesPinnedCertificate(renewed, brokerId, fingerprint, now) || !renewed.HasPrivateKey)
                        throw InvalidIdentity();
                    WriteMetadata(directory, path, new RenewableIdentity(2, brokerId, keyName, anchorBytes, renewed.RawData), replace);
                    return new(brokerId, renewed, keyName, fingerprint);
                }
                catch { renewed.Dispose(); throw; }
            }
            catch (JsonException ex) { throw new InvalidDataException("Invalid identity metadata. Identity was preserved.", ex); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        // No overwrite flag and no machine-key flag. An orphaned key is preserved and startup fails closed.
        using var newKey = CngKey.Create(CngAlgorithm.Rsa, keyName, new CngKeyCreationParameters {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
            Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) } });
        X509Certificate2? certificate = null;
        try
        {
            using var rsa = new RSACng(newKey);
            certificate = CreateCertificate(rsa, brokerId, now);
            if (!CertificateContinuity.IsValidAnchor(certificate, brokerId) || !IsFor(certificate, brokerId, now) || !certificate.HasPrivateKey)
                throw InvalidIdentity();
            WriteMetadata(directory, path, new StoredIdentity(1, brokerId, keyName, certificate.RawData), null);
            return new(brokerId, certificate, keyName, certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch { certificate?.Dispose(); newKey.Delete(); throw; }
    }

    private static (byte[] Anchor, byte[] Current) ReadMetadata(byte[] plain, Guid brokerId, string keyName)
    {
        using var document = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw InvalidIdentity();
        var members = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (members.Length != members.Distinct(StringComparer.Ordinal).Count() ||
            !root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int number)) throw InvalidIdentity();
        byte[] anchor, current;
        Guid storedId;
        string storedKey;
        if (number == 1 && members.Order(StringComparer.Ordinal).SequenceEqual(new[] { "BrokerId", "Certificate", "KeyName", "Version" }))
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(plain) ?? throw InvalidIdentity();
            storedId = stored.BrokerId; storedKey = stored.KeyName;
            anchor = current = stored.Certificate;
        }
        else if (number == 2 && members.Order(StringComparer.Ordinal).SequenceEqual(new[] { "BrokerId", "CurrentCertificateDER", "KeyName", "OriginalAnchorDER", "Version" }))
        {
            var stored = JsonSerializer.Deserialize<RenewableIdentity>(plain) ?? throw InvalidIdentity();
            storedId = stored.BrokerId; storedKey = stored.KeyName;
            anchor = stored.OriginalAnchorDER; current = stored.CurrentCertificateDER;
        }
        else throw InvalidIdentity();
        if (storedId != brokerId || storedKey != keyName || anchor is not { Length: > 0 and <= CertificateContinuity.MaximumAnchorBytes } ||
            current is not { Length: > 0 and <= CertificateContinuity.MaximumLeafBytes }) throw InvalidIdentity();
        return (anchor, current);
    }

    private static X509Certificate2 CreateCertificate(RSA rsa, Guid brokerId, DateTimeOffset now, X509Certificate2? anchor = null)
    {
        var request = new CertificateRequest("CN=AgentBroker-" + brokerId.ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {
            new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("broker-" + brokerId.ToString("N") + ".local");
        request.CertificateExtensions.Add(san.Build());
        if (anchor is not null) request.CertificateExtensions.Add(CertificateContinuity.CreateAnchorExtension(anchor, brokerId));
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));
    }

    private static void WriteMetadata<T>(string directory, string path, T metadata, Action<string, string>? replace)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(metadata);
        byte[] encrypted;
        try { encrypted = UserProtection.Seal(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaximumCiphertextBytes) throw new InvalidDataException("Identity ciphertext is oversized.");
        string temporary = Path.Combine(directory, "identity-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(encrypted); output.Flush(true); }
            RejectLinks(path, allowMissing: replace is null);
            if (replace is null) File.Move(temporary, path, false);
            else replace(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static InvalidDataException InvalidIdentity() => new("Stored TLS identity does not match this broker. Identity was preserved.");
    public static bool IsFor(X509Certificate2 certificate, Guid brokerId) => IsFor(certificate, brokerId, DateTimeOffset.UtcNow);
    private static bool IsFor(X509Certificate2 certificate, Guid brokerId, DateTimeOffset now) =>
        certificate.GetNameInfo(X509NameType.SimpleName, false) == "AgentBroker-" + brokerId.ToString("N") &&
        certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime && certificate.NotAfter.ToUniversalTime() > now.UtcDateTime;
    private static void RejectLinks(string path, bool allowMissing = false)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Identity path reaches a link."); }
            catch (Exception ex) when (allowMissing && ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }
    public void Dispose() => Certificate.Dispose();
}

internal static class UserProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static byte[] Seal(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unseal(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool seal)
    {
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        var input = new Blob { Length = bytes.Length, Data = buffer };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            bool ok = seal ? CryptProtectData(ref input, "AgentBroker identity v1", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Current-user identity protection failed.");
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, buffer, bytes.Length); Marshal.FreeHGlobal(buffer);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}
