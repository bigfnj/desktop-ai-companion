using System.ComponentModel;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker.Adapters;

internal enum ClaudePublicImageTrust { Unavailable, UntrustedSignature, UnexpectedPublisher, ExpectedPublisher }
internal sealed record ClaudePublicImageTrustResult(ClaudePublicImageTrust Trust, string? ImageSha256 = null) : IDisposable
{
    internal IDisposable? ImageLease { get; init; }
    public void Dispose() => ImageLease?.Dispose();
}

/// <summary>
/// Authenticode observation of one public executable. This never grants native delivery authority.
/// The publisher is read from the signer selected by successful Windows trust verification.
/// </summary>
internal static class WindowsClaudePublicImageTrust
{
    internal const string ExpectedPublisher = "Anthropic, PBC";

    internal static ClaudePublicImageTrustResult Observe(string path)
    {
        if (!OperatingSystem.IsWindows()) return new(ClaudePublicImageTrust.Unavailable);
        FileStream? image = null;
        try
        {
            string expected = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(expected), "claude.exe", StringComparison.OrdinalIgnoreCase) ||
                expected.StartsWith(@"\\", StringComparison.Ordinal)) return new(ClaudePublicImageTrust.Unavailable);
            AgentBroker.ClaudeAdapter.CredentialPathPolicy.RejectReparseComponents(expected, File.GetAttributes);
            // Block image edits/replacement until Windows verification and hashing have both completed.
            image = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            VerifyHeldPath(image.SafeFileHandle, expected);
            var verified = VerifyAuthenticode(image.SafeFileHandle, expected);
            if (verified != ClaudePublicImageTrust.ExpectedPublisher) return new(verified);
            image.Position = 0;
            string hash = Convert.ToHexString(SHA256.HashData(image));
            VerifyHeldPath(image.SafeFileHandle, expected);
            var result = new ClaudePublicImageTrustResult(ClaudePublicImageTrust.ExpectedPublisher, hash) { ImageLease = image };
            image = null; // The discovery caller owns this short lease through its final process check.
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            CryptographicException or ArgumentException or NotSupportedException or OverflowException or
            AsnContentException or DllNotFoundException or EntryPointNotFoundException)
        { return new(ClaudePublicImageTrust.Unavailable); }
        finally { image?.Dispose(); }
    }

    internal static bool IsCurrentUserProcess(SafeProcessHandle process)
    {
        if (!OperatingSystem.IsWindows() || process.IsInvalid ||
            !OpenProcessToken(process, 0x0008, out var token)) return false;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            return owner.User is { } ownerSid && current.User is { } currentSid && ownerSid.Equals(currentSid);
    }

    private static ClaudePublicImageTrust VerifyAuthenticode(SafeFileHandle image, string path)
    {
        var file = new WintrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), Path = path,
            File = image.DangerousGetHandle(),
        };
        IntPtr nativeFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var data = new WintrustData
        {
            Size = (uint)Marshal.SizeOf<WintrustData>(), UiChoice = 2, UnionChoice = 1,
            File = nativeFile, StateAction = 1,
            // No network fetch or UI in directory polling. Unavailable cached revocation trust refuses observation.
            ProviderFlags = 0x80 | 0x1000 | 0x2000,
        };
        Guid policy = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        bool fileMarshaled = false;
        try
        {
            Marshal.StructureToPtr(file, nativeFile, false);
            fileMarshaled = true;
            int status = WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
            if (status != 0) return ClaudePublicImageTrust.UntrustedSignature;
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return ClaudePublicImageTrust.Unavailable;
            IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return ClaudePublicImageTrust.Unavailable;
            var signerInfo = Marshal.PtrToStructure<ProviderSigner>(signer);
            if (signerInfo.Error != 0 || signerInfo.CertificateCount == 0) return ClaudePublicImageTrust.Unavailable;
            IntPtr cert = WTHelperGetProvCertFromChain(signer, 0);
            if (cert == IntPtr.Zero) return ClaudePublicImageTrust.Unavailable;
            IntPtr context = Marshal.PtrToStructure<ProviderCertificatePrefix>(cert).Certificate;
            if (context == IntPtr.Zero) return ClaudePublicImageTrust.Unavailable;
            var certificate = Marshal.PtrToStructure<CertificateContext>(context);
            if (certificate.Encoded == IntPtr.Zero || certificate.EncodedBytes is 0 or > 262144)
                return ClaudePublicImageTrust.Unavailable;
            byte[] encoded = new byte[certificate.EncodedBytes];
            Marshal.Copy(certificate.Encoded, encoded, 0, encoded.Length);
            using var publicCertificate = X509CertificateLoader.LoadCertificate(encoded);
            return IsExpectedPublisher(publicCertificate.SubjectName)
                ? ClaudePublicImageTrust.ExpectedPublisher : ClaudePublicImageTrust.UnexpectedPublisher;
        }
        finally
        {
            // Close the same trust state, including failed verification states.
            try
            {
                if (data.StateData != IntPtr.Zero)
                { data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref policy, ref data); }
            }
            finally
            {
                if (fileMarshaled) Marshal.DestroyStructure<WintrustFileInfo>(nativeFile);
                Marshal.FreeHGlobal(nativeFile);
            }
        }
    }

    internal static bool IsExpectedPublisher(X500DistinguishedName subject)
    {
        // Parse DER attributes rather than substring matching a display name or PE version resource.
        var reader = new AsnReader(subject.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        string? commonName = null, organization = null;
        while (sequence.HasData)
        {
            var set = sequence.ReadSetOf();
            while (set.HasData)
            {
                var attribute = set.ReadSequence();
                string oid = attribute.ReadObjectIdentifier();
                if (oid is "2.5.4.3" or "2.5.4.10")
                {
                    var tag = attribute.PeekTag();
                    if (tag.TagClass != TagClass.Universal || tag.TagValue is not (12 or 19 or 20 or 22 or 28 or 30))
                        return false;
                    string value = attribute.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                    if (oid == "2.5.4.3")
                    { if (commonName is not null) return false; commonName = value; }
                    else
                    { if (organization is not null) return false; organization = value; }
                }
                else _ = attribute.ReadEncodedValue();
                attribute.ThrowIfNotEmpty();
            }
        }
        reader.ThrowIfNotEmpty();
        return commonName == ExpectedPublisher && organization == ExpectedPublisher;
    }

    private static void VerifyHeldPath(SafeFileHandle image, string expected)
    {
        if (!GetFileInformationByHandle(image, out var info) || info.Links != 1 || (info.Attributes & (0x400 | 0x10)) != 0)
            throw new IOException("Public executable is not an ordinary single-link file.");
        var final = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(image, final, (uint)final.Capacity, 0);
        if (count == 0 || count >= final.Capacity) throw new IOException("Public executable handle path is unavailable.");
        string resolved = final.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) || resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(resolved[4..]), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Public executable path changed.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
        internal IntPtr File, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallback, SipClient;
        internal uint UiChoice, RevocationChecks, UnionChoice;
        internal IntPtr File;
        internal uint StateAction;
        internal IntPtr StateData, UrlReference;
        internal uint ProviderFlags, UiContext;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        internal uint Size;
        internal FileTime VerifiedAt;
        internal uint CertificateCount;
        internal IntPtr Certificates;
        internal uint SignerType;
        internal IntPtr SignerInfo;
        internal uint Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificatePrefix { internal uint Size; internal IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { internal uint Encoding; internal IntPtr Encoded; internal uint EncodedBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal FileTime Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid policy, ref WintrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint count, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
