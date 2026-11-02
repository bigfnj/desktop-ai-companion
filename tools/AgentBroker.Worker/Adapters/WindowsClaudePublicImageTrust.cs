using System.ComponentModel;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AgentBroker.Pins;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Worker.Adapters;

internal enum ClaudePublicImageTrust { Unavailable, UntrustedSignature, UnexpectedPublisher, ExpectedPublisher }
internal sealed record ClaudePublicImageTrustResult(ClaudePublicImageTrust Trust, string? ImageSha256 = null) : IDisposable
{
    internal IDisposable? ImageLease { get; init; }
    public void Dispose() => ImageLease?.Dispose();
}

/// <summary>
/// The send-time publisher check of update-tolerant Claude delivery (the owner's decision of 2026-11-02): the image at a
/// path is Windows-verified as "Anthropic, PBC" and its held SHA-256 is the expected one. Anything else, a check that
/// could not complete included, answers false. Production observes through <see cref="WindowsClaudePublicImageTrust.Observe"/>.
/// </summary>
internal sealed class ClaudePublisherImageTrust(Func<string, ClaudePublicImageTrustResult> observe) : AgentBroker.ClaudeAdapter.IPublisherImageTrust
{
    public bool IsExpectedPublisher(string imagePath, string imageSha256)
    {
        using var trust = observe(imagePath);
        return trust.Trust == ClaudePublicImageTrust.ExpectedPublisher &&
            trust.ImageSha256 is { Length: 64 } hash && string.Equals(hash, imageSha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Authenticode observation of one public executable. On its own this grants no native delivery authority: discovery
/// uses it for observation, and update-tolerant delivery (off in the worker unless the module passes its switch) uses it
/// as the publisher half of a rule whose other half is the native pipe's own process identity (NativePeerAdapter).
/// The publisher is read from the signer selected by successful Windows trust verification. The WinVerifyTrust
/// call, subject parse and held-path check are shared with the Codex adapter's check through one linked file
/// (<c>tools/AgentBroker.Pins/WindowsAuthenticode.cs</c>); the claude.exe name, the publisher, the SHA-256 lease and
/// the malformed-subject policy are this check's own.
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

    private static ClaudePublicImageTrust VerifyAuthenticode(SafeFileHandle image, string path)
        => WindowsAuthenticode.VerifySigner(image, path, out var subject) switch
        {
            AuthenticodeSignature.Untrusted => ClaudePublicImageTrust.UntrustedSignature,
            AuthenticodeSignature.Signed => IsExpectedPublisher(subject!)
                ? ClaudePublicImageTrust.ExpectedPublisher : ClaudePublicImageTrust.UnexpectedPublisher,
            _ => ClaudePublicImageTrust.Unavailable,
        };

    internal static bool IsExpectedPublisher(X500DistinguishedName subject)
        // A malformed subject throws AsnContentException out of here on purpose, and Observe reads it as Unavailable,
        // where the Codex check reads it as an unexpected publisher (WindowsAuthenticode.cs explains; N-broker-b-02).
        => WindowsAuthenticode.ReadPublisherNames(subject, out string? commonName, out string? organization) &&
            commonName == ExpectedPublisher && organization == ExpectedPublisher;

    private static void VerifyHeldPath(SafeFileHandle image, string expected) => WindowsAuthenticode.VerifyHeldPath(image, expected);

}
