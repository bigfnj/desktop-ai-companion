using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Pins;

// The Windows Authenticode mechanics both public-image checks share (N-broker-b-02): the one WinVerifyTrust call that
// selects a signer, the certificate it selected, the DER subject parse, and the held-handle path check, with their
// structures and P/Invokes. It is compiled into the Codex desktop adapter (WindowsPublisherImageTrust) and into the worker
// (WindowsClaudePublicImageTrust) as linked source, as PinDocument.cs is, because no shared project fits: AgentBroker.Core
// is net10.0, not net10.0-windows. Every type is internal; the worker also references the Codex adapter, whose copy it
// never sees.
//
// POLICY stays in each wrapper: which publisher is expected, which file name, whether the location must be protected,
// whether the image is hashed and leased, and what a malformed subject means. A malformed subject DIFFERS on purpose and
// is kept as it was before the move: ReadPublisherNames throws AsnContentException, which the Codex check reads as an
// unexpected publisher and the Claude check lets reach Observe, which reads it as Unavailable.
internal enum AuthenticodeSignature { Unavailable, Untrusted, Signed }

internal static class WindowsAuthenticode
{
    /// <summary>
    /// Runs WinVerifyTrust on the held image and, when Windows trusts the signature, returns the subject of the signer
    /// certificate that verification selected. No network fetch or UI; unavailable cached revocation refuses. The caller
    /// decides whether that signer is its publisher. Exceptions (a certificate that will not load, a missing export)
    /// reach the caller, whose catch reads them as Unavailable.
    /// </summary>
    internal static AuthenticodeSignature VerifySigner(SafeFileHandle image, string path, out X500DistinguishedName? subject)
    {
        subject = null;
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
            // No network fetch or UI during discovery. Unavailable cached revocation trust refuses observation.
            ProviderFlags = 0x80 | 0x1000 | 0x2000,
        };
        Guid policy = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        bool fileMarshaled = false;
        try
        {
            Marshal.StructureToPtr(file, nativeFile, false);
            fileMarshaled = true;
            int status = WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
            if (status != 0) return AuthenticodeSignature.Untrusted;
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return AuthenticodeSignature.Unavailable;
            IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return AuthenticodeSignature.Unavailable;
            var signerInfo = Marshal.PtrToStructure<ProviderSigner>(signer);
            if (signerInfo.Error != 0 || signerInfo.CertificateCount == 0) return AuthenticodeSignature.Unavailable;
            IntPtr cert = WTHelperGetProvCertFromChain(signer, 0);
            if (cert == IntPtr.Zero) return AuthenticodeSignature.Unavailable;
            IntPtr context = Marshal.PtrToStructure<ProviderCertificatePrefix>(cert).Certificate;
            if (context == IntPtr.Zero) return AuthenticodeSignature.Unavailable;
            var certificate = Marshal.PtrToStructure<CertificateContext>(context);
            if (certificate.Encoded == IntPtr.Zero || certificate.EncodedBytes is 0 or > 262144)
                return AuthenticodeSignature.Unavailable;
            byte[] encoded = new byte[certificate.EncodedBytes];
            Marshal.Copy(certificate.Encoded, encoded, 0, encoded.Length);
            using var publicCertificate = X509CertificateLoader.LoadCertificate(encoded);
            // A copy of the subject's DER bytes, so it outlives the certificate disposed here.
            subject = new X500DistinguishedName(publicCertificate.SubjectName.RawData);
            return AuthenticodeSignature.Signed;
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

    /// <summary>
    /// Reads the subject's common name and organization from its DER attributes, never from a display name or a PE
    /// version resource. False for a duplicate of either or a value that is not a string type. A malformed encoding
    /// THROWS <see cref="AsnContentException"/>: each wrapper decides what that means (see the note at the top).
    /// </summary>
    internal static bool ReadPublisherNames(X500DistinguishedName subject, out string? commonName, out string? organization)
    {
        commonName = null; organization = null;
        var reader = new AsnReader(subject.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
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
        return true;
    }

    /// <summary>The held handle is one ordinary single-link file, not a directory or reparse point, still at the path.</summary>
    internal static void VerifyHeldPath(SafeFileHandle image, string expected)
    {
        if (!GetFileInformationByHandle(image, out var info) || info.Links != 1 || (info.Attributes & (0x400 | 0x10)) != 0)
            throw new IOException("Public file is not an ordinary single-link file.");
        var final = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(image, final, (uint)final.Capacity, 0);
        if (count == 0 || count >= final.Capacity) throw new IOException("Public file handle path is unavailable.");
        string resolved = final.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) || resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(resolved[4..]), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Public file path changed.");
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
}
