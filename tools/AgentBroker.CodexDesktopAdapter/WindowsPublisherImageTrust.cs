using System.ComponentModel;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>
/// How a connected Codex router was trusted. Only <see cref="ExactPinned"/> carries the reviewed delivery
/// profile; <see cref="PublisherSigned"/> is Windows-verified publisher evidence for read-only observation,
/// and it delivers only under the separate opt-in update-tolerant policy. A default value grants nothing.
/// </summary>
public enum DesktopRouterTrust { Unverified, ExactPinned, PublisherSigned }

/// <summary>A connected router's verification; <c>ExactPinRefusal</c> is the pin a publisher-signed router fell back from.</summary>
internal sealed record DesktopRouterVerification(DesktopRouterProfile Profile, DesktopRouterTrust Trust,
    DesktopAdapterException? ExactPinRefusal = null);

internal enum PublisherImageTrust { Unavailable, UntrustedSignature, UnexpectedPublisher, UnprotectedLocation, ExpectedPublisher }

/// <summary>
/// Authenticode observation of one public executable, mirroring the reviewed Claude check
/// (<c>WindowsClaudePublicImageTrust</c> in the worker). The publisher is read from the signer that
/// successful Windows trust verification selected, never from a PE version resource or display name.
/// A valid signature establishes publisher identity only; it proves no protocol compatibility.
/// </summary>
internal static class WindowsPublisherImageTrust
{
    internal const string OpenAIPublisher = "OpenAI OpCo, LLC";
    internal const string MicrosoftPublisher = "Microsoft Corporation";
    private const string LocalSystem = "S-1-5-18";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    // File data write/append, write EA/attributes, delete child, delete, ACL/owner changes, generic write/all.
    private const int WriteRights = 0x500D0156;

    /// <summary>
    /// Verifies the executable at <paramref name="path"/> under one held, single-link, reparse-free handle.
    /// <paramref name="requireProtectedLocation"/> additionally requires the packaged-app (WindowsApps)
    /// ownership and DACL: owned by SYSTEM or TrustedInstaller, writable by nobody else.
    /// </summary>
    internal static PublisherImageTrust Verify(string path, string expectedPublisher, bool requireProtectedLocation)
    {
        if (!OperatingSystem.IsWindows()) return PublisherImageTrust.Unavailable;
        FileStream? image = null;
        try
        {
            string expected = Path.GetFullPath(path);
            if (expected.StartsWith(@"\\", StringComparison.Ordinal) ||
                !expected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return PublisherImageTrust.Unavailable;
            DesktopCompatibilityProfile.VerifyRegularPath(expected);
            // Block image edits/replacement until Windows verification has completed and the path is rechecked.
            image = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            VerifyHeldPath(image.SafeFileHandle, expected);
            if (requireProtectedLocation && !IsProtected(image.SafeFileHandle)) return PublisherImageTrust.UnprotectedLocation;
            var verified = VerifyAuthenticode(image.SafeFileHandle, expected, expectedPublisher);
            if (verified != PublisherImageTrust.ExpectedPublisher) return verified;
            VerifyHeldPath(image.SafeFileHandle, expected);
            return PublisherImageTrust.ExpectedPublisher;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            CryptographicException or ArgumentException or NotSupportedException or OverflowException or
            AsnContentException or DllNotFoundException or EntryPointNotFoundException)
        { return PublisherImageTrust.Unavailable; }
        finally { image?.Dispose(); }
    }

    /// <summary>A non-executable packaged file (the desktop archive) must carry the same protected ownership and DACL.</summary>
    internal static bool IsProtectedFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string expected = Path.GetFullPath(path);
            DesktopCompatibilityProfile.VerifyRegularPath(expected);
            using var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            VerifyHeldPath(file.SafeFileHandle, expected);
            return IsProtected(file.SafeFileHandle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or Win32Exception)
        { return false; }
    }

    internal static bool IsProtected(SafeFileHandle file)
    {
        GetKernelObjectSecurity(file, 5, null, 0, out uint needed);
        if (needed is < 20 or > 65536) return false;
        byte[] bytes = new byte[needed];
        if (!GetKernelObjectSecurity(file, 5, bytes, needed, out _)) return false;
        return IsProtectedDescriptor(new RawSecurityDescriptor(bytes, 0));
    }

    internal static bool IsProtectedDescriptor(RawSecurityDescriptor descriptor)
    {
        if (descriptor.Owner?.Value is not (LocalSystem or TrustedInstaller) || descriptor.DiscretionaryAcl is null) return false;
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            // An ACE this parser cannot interpret refuses rather than being skipped.
            if (ace is not KnownAce known) return false;
            // Every allow form counts, including conditional (callback) ACEs: a condition can still be met.
            bool allows = known is QualifiedAce qualified ? qualified.AceQualifier == AceQualifier.AccessAllowed : known is CompoundAce;
            if (!allows || (known.AccessMask & WriteRights) == 0) continue;
            if (known.SecurityIdentifier.Value is not (LocalSystem or TrustedInstaller)) return false;
        }
        return true;
    }

    private static PublisherImageTrust VerifyAuthenticode(SafeFileHandle image, string path, string expectedPublisher)
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
            if (status != 0) return PublisherImageTrust.UntrustedSignature;
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return PublisherImageTrust.Unavailable;
            IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return PublisherImageTrust.Unavailable;
            var signerInfo = Marshal.PtrToStructure<ProviderSigner>(signer);
            if (signerInfo.Error != 0 || signerInfo.CertificateCount == 0) return PublisherImageTrust.Unavailable;
            IntPtr cert = WTHelperGetProvCertFromChain(signer, 0);
            if (cert == IntPtr.Zero) return PublisherImageTrust.Unavailable;
            IntPtr context = Marshal.PtrToStructure<ProviderCertificatePrefix>(cert).Certificate;
            if (context == IntPtr.Zero) return PublisherImageTrust.Unavailable;
            var certificate = Marshal.PtrToStructure<CertificateContext>(context);
            if (certificate.Encoded == IntPtr.Zero || certificate.EncodedBytes is 0 or > 262144)
                return PublisherImageTrust.Unavailable;
            byte[] encoded = new byte[certificate.EncodedBytes];
            Marshal.Copy(certificate.Encoded, encoded, 0, encoded.Length);
            using var publicCertificate = X509CertificateLoader.LoadCertificate(encoded);
            return IsExpectedPublisher(publicCertificate.SubjectName, expectedPublisher)
                ? PublisherImageTrust.ExpectedPublisher : PublisherImageTrust.UnexpectedPublisher;
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

    internal static bool IsExpectedPublisher(X500DistinguishedName subject, string expectedPublisher)
    {
        // Parse DER attributes rather than substring matching a display name. Exactly one common name and one
        // organization, both equal to the expected legal name; duplicates or other string types refuse.
        try
        {
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
            return commonName == expectedPublisher && organization == expectedPublisher;
        }
        catch (AsnContentException) { return false; }
    }

    private static void VerifyHeldPath(SafeFileHandle image, string expected)
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
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
}
