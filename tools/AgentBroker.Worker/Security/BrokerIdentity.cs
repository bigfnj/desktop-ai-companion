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
    public Guid BrokerId { get; }
    public X509Certificate2 Certificate { get; }
    public string KeyName { get; }
    public string Fingerprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    private BrokerIdentity(Guid id, X509Certificate2 certificate, string keyName) { BrokerId = id; Certificate = certificate; KeyName = keyName; }
    public static string KeyNameFor(string dataDir, Guid brokerId)
    {
        if (brokerId == Guid.Empty) throw new ArgumentException("Empty broker identity.");
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        return "AgentBroker.v1." + brokerId.ToString("N") + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];
    }
    public static BrokerIdentity LoadOrCreate(string dataDir, Guid brokerId)
    {
        if (brokerId == Guid.Empty) throw new ArgumentException("Empty broker identity.");
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        RejectLinks(directory, allowMissing: true);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        string path = Path.Combine(directory, "tls-identity.dat");
        string keyName = KeyNameFor(directory, brokerId);
        X509Certificate2 certificate;
        if (File.Exists(path))
        {
            RejectLinks(path);
            if (new FileInfo(path).Length > 131072) throw new InvalidDataException("Identity ciphertext is oversized.");
            byte[] plain = UserProtection.Unseal(File.ReadAllBytes(path));
            try
            {
                var stored = JsonSerializer.Deserialize<StoredIdentity>(plain) ?? throw new InvalidDataException("Empty identity metadata.");
                if (stored.Version != 1 || stored.BrokerId != brokerId || stored.KeyName != keyName || stored.Certificate is null || stored.Certificate.Length > 16384)
                    throw new InvalidDataException("Stored TLS identity does not match this broker. Identity was preserved.");
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent);
                if (key.IsMachineKey || key.ExportPolicy != CngExportPolicies.None || key.AlgorithmGroup != CngAlgorithmGroup.Rsa)
                    throw new InvalidDataException("Stored runtime key has an unexpected policy.");
                using var rsa = new RSACng(key);
                using var publicCertificate = X509CertificateLoader.LoadCertificate(stored.Certificate);
                certificate = publicCertificate.CopyWithPrivateKey(rsa);
            }
            catch (JsonException ex) { throw new InvalidDataException("Invalid identity metadata. Identity was preserved.", ex); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        else
        {
            // No overwrite flag and no machine-key flag. An orphaned key is preserved and startup fails closed.
            using var key = CngKey.Create(CngAlgorithm.Rsa, keyName, new CngKeyCreationParameters {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) } });
            try
            {
                using var rsa = new RSACng(key);
                var request = new CertificateRequest("CN=AgentBroker-" + brokerId.ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {
                    new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("broker-" + brokerId.ToString("N") + ".local"); request.CertificateExtensions.Add(san.Build());
                certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(1, brokerId, keyName, certificate.RawData));
                byte[] encrypted;
                try { encrypted = UserProtection.Seal(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                string temporary = Path.Combine(directory, "identity-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { output.Write(encrypted); output.Flush(true); }
                    File.Move(temporary, path, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch { key.Delete(); throw; }
        }
        if (!IsFor(certificate, brokerId) || !certificate.HasPrivateKey)
        { certificate.Dispose(); throw new InvalidDataException("Stored TLS identity does not match this broker. Identity was preserved."); }
        return new(brokerId, certificate, keyName);
    }
    public static bool IsFor(X509Certificate2 certificate, Guid brokerId) =>
        certificate.GetNameInfo(X509NameType.SimpleName, false) == "AgentBroker-" + brokerId.ToString("N") &&
        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow;
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
