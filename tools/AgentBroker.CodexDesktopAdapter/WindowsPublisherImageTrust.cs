using System.ComponentModel;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AgentBroker.Pins;
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
/// Authenticode observation of one public executable. The WinVerifyTrust call, the selected signer's subject parse and
/// the held-path check are the ones the reviewed Claude check (<c>WindowsClaudePublicImageTrust</c> in the worker) uses,
/// compiled from one linked file (<c>tools/AgentBroker.Pins/WindowsAuthenticode.cs</c>); the publisher, location and
/// malformed-subject policy below are this adapter's own. The publisher is read from the signer that successful Windows
/// trust verification selected, never from a PE version resource or display name.
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
        => WindowsAuthenticode.VerifySigner(image, path, out var subject) switch
        {
            AuthenticodeSignature.Untrusted => PublisherImageTrust.UntrustedSignature,
            AuthenticodeSignature.Signed => IsExpectedPublisher(subject!, expectedPublisher)
                ? PublisherImageTrust.ExpectedPublisher : PublisherImageTrust.UnexpectedPublisher,
            _ => PublisherImageTrust.Unavailable,
        };

    internal static bool IsExpectedPublisher(X500DistinguishedName subject, string expectedPublisher)
    {
        // Exactly one common name and one organization, both equal to the expected legal name; duplicates or other
        // string types refuse. A malformed subject refuses here as an unexpected publisher, where the Claude check reads
        // it as Unavailable (WindowsAuthenticode.cs explains; both kept as they were, N-broker-b-02).
        try
        {
            return WindowsAuthenticode.ReadPublisherNames(subject, out string? commonName, out string? organization) &&
                commonName == expectedPublisher && organization == expectedPublisher;
        }
        catch (AsnContentException) { return false; }
    }

    private static void VerifyHeldPath(SafeFileHandle image, string expected) => WindowsAuthenticode.VerifyHeldPath(image, expected);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
}
