using AgentBroker.Pins;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AgentBroker.CodexDesktopAdapter;

public static class DesktopCompatibilityProfile
{
    internal sealed record PackageProfile(string PackageFullName, string ApplicationVersion,
        string ExecutableSha256, string ArchiveSha256, string ElectronVersion)
    {
        internal string ApplicationDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", PackageFullName, "app");
        internal string ExecutablePath => Path.Combine(ApplicationDirectory, "ChatGPT.exe");
    }

    // Each reviewed package is a complete tuple, a "codexDesktop" pin of the embedded document
    // tools/AgentBroker.Pins/agentbroker-pins.json. Never mix an executable from one package with an archive from
    // another or infer compatibility from its version.
    internal static IReadOnlyList<PackageProfile> Packages => PackagesOf(PinDocument.Current);

    /// <summary>The reviewed desktop package versions, for status text. Empty when the pin document was refused.</summary>
    public static string VerifiedVersions => string.Join(", ", PinDocument.Current.CodexDesktop.Select(pin => pin.ApplicationVersion));

    /// <summary>Why the embedded pin document was refused, or null when it validated.</summary>
    public static string? PinDocumentError => PinDocument.Current.Error;

    // A refused document is its own refusal, never an exact-pin mismatch: IsExactPinMismatch must stay false for it,
    // so publisher evidence cannot stand in and the router fails closed instead of being observed or woken.
    internal static IReadOnlyList<PackageProfile> PackagesOf(PinDocument pins) => pins.Error is not null
        ? throw new DesktopAdapterException("pin_document_invalid")
        : pins.CodexDesktop.Select(pin => new PackageProfile(pin.PackageFullName, pin.ApplicationVersion,
            pin.ExecutableSha256, pin.ArchiveSha256, pin.ElectronVersion)).ToArray();

    internal static void VerifyPublicImages(string image)
    {
        var profile = SelectProfile(image);
        string expected = profile.ExecutablePath;
        string archive = Path.Combine(profile.ApplicationDirectory, "resources", "app.asar");
        VerifyRegularPath(expected);
        VerifyRegularPath(archive);
        ValidateMetadata(image, Hash(expected), Hash(archive), FileVersionInfo.GetVersionInfo(expected).ProductVersion);
    }

    internal static PackageProfile SelectProfile(string image) => SelectProfile(image, PinDocument.Current);

    internal static PackageProfile SelectProfile(string image, PinDocument pins)
    {
        string fullImage = Path.GetFullPath(image);
        foreach (var profile in PackagesOf(pins))
            if (string.Equals(fullImage, Path.GetFullPath(profile.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                return profile;
        throw new DesktopAdapterException("unsupported_desktop_version", InstalledApplicationVersion(image));
    }

    internal static void ValidateMetadata(string image, string executableSha256, string archiveSha256, string? electronVersion)
    {
        var profile = SelectProfile(image);
        if (electronVersion != profile.ElectronVersion || executableSha256 != profile.ExecutableSha256 ||
            archiveSha256 != profile.ArchiveSha256)
            throw new DesktopAdapterException("desktop_image_mismatch", profile.ApplicationVersion);
    }

    // Exact-pin refusals that Windows-verified publisher evidence may stand in for. A linked image, an
    // unverifiable process snapshot or any identity/ACL failure is never one of them.
    private static readonly HashSet<string> PinMismatchCodes = new(StringComparer.Ordinal)
    {
        "unsupported_desktop_version", "desktop_image_mismatch", "unsupported_vscode_router",
        "unsupported_vscode_profile", "vscode_image_mismatch", "unsupported_running_vscode_profile",
        "running_vscode_image_mismatch",
    };

    /// <summary>True only for a refusal that means "not an exactly reviewed build", never for an identity failure.</summary>
    public static bool IsExactPinMismatch(string code) => PinMismatchCodes.Contains(code);

    internal static string? InstalledApplicationVersion(string image)
    {
        // Identify only the package-name version. This supplies diagnostics and grants no image trust.
        string fullImage = Path.GetFullPath(image);
        string? appDirectory = Path.GetDirectoryName(fullImage);
        string? packageDirectory = Path.GetDirectoryName(appDirectory);
        if (!string.Equals(Path.GetFileName(fullImage), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(appDirectory), "app", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(packageDirectory), Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), StringComparison.OrdinalIgnoreCase))
            return null;
        string[] parts = (Path.GetFileName(packageDirectory) ?? "").Split('_');
        return parts.Length == 5 && parts[0] == "OpenAI.Codex" && parts[3] == "" && parts[4] == "2p2nqsd0c76g0"
            && Version.TryParse(parts[1], out var version) ? version.ToString() : null;
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void VerifyRegularPath(string file)
        => VerifyRegularPath(file, File.GetAttributes);

    internal static void VerifyRegularPath(string file, Func<string, FileAttributes> attributes)
    {
        for (string? path = Path.GetFullPath(file); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if ((attributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new DesktopAdapterException("linked_desktop_image");
            if (Path.GetPathRoot(path) == path) break;
        }
    }
}

internal sealed class WindowsDesktopConnection : IVerifiedDesktopConnection
{
    private readonly NamedPipeClientStream pipe;
    public Stream Stream => pipe;
    public OwnerProcessIdentity Identity { get; }
    public DesktopRouterProfile Profile { get; }
    public DesktopRouterTrust Trust { get; }
    public DesktopAdapterException? ExactPinRefusal { get; }
    private bool disposed;

    private WindowsDesktopConnection(NamedPipeClientStream pipe, OwnerProcessIdentity identity, DesktopRouterVerification verification)
    {
        this.pipe = pipe; Identity = identity; Profile = verification.Profile; Trust = verification.Trust;
        ExactPinRefusal = verification.ExactPinRefusal;
    }

    internal static Task<IVerifiedDesktopConnection> ConnectAsync(TimeSpan timeout, CancellationToken token)
        => ConnectAsync(timeout, allowPublisherSigned: false, token);

    internal static async Task<IVerifiedDesktopConnection> ConnectAsync(TimeSpan timeout, bool allowPublisherSigned, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        // Fixed local endpoint only. Never create a router and never accept a remote/custom production endpoint.
        var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            await pipe.ConnectAsync(deadline.Token);
            // Process, user, start time and pipe ACL are checked identically for exact and publisher trust.
            var identity = ReadConnectedIdentity(pipe.SafePipeHandle);
            VerifyPipeAcl(pipe.SafePipeHandle);
            var verification = VerifyRouter(identity.ImagePath, allowPublisherSigned,
                VsCodeRouterCompatibilityProfile.VerifyPublicImage, DesktopCompatibilityProfile.VerifyPublicImages,
                VerifyPublisherSignedRouter);
            var connection = new WindowsDesktopConnection(pipe, identity, verification);
            connection.VerifyStillSameOwner();
            return connection;
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    internal static DesktopRouterVerification VerifyRouter(string image, bool allowPublisherSigned,
        Action<string> verifyVsCode, Action<string> verifyDesktop, Func<string, DesktopRouterProfile> verifyPublisher)
    {
        try { return new(VerifyPublicProfile(image, verifyVsCode, verifyDesktop), DesktopRouterTrust.ExactPinned); }
        catch (DesktopAdapterException pinned) when (allowPublisherSigned && DesktopCompatibilityProfile.IsExactPinMismatch(pinned.Code))
        {
            try { return new(verifyPublisher(image), DesktopRouterTrust.PublisherSigned, pinned); }
            catch (DesktopAdapterException publisher)
            {
                // Report the publisher refusal, keeping the installed version the exact-pin check identified.
                throw new DesktopAdapterException(publisher.Code, publisher.InstalledVersion ?? pinned.InstalledVersion);
            }
        }
    }

    /// <summary>
    /// Publisher evidence for a router that is not an exactly reviewed build. The desktop package must sit in its
    /// protected WindowsApps package directory with an OpenAI signature; a Code-hosted router needs a Microsoft
    /// signature plus at least one running Code-owned engine with an OpenAI signature.
    /// </summary>
    internal static DesktopRouterProfile VerifyPublisherSignedRouter(string image)
        => VerifyPublisherSignedRouter(image, WindowsPublisherImageTrust.Verify, WindowsPublisherImageTrust.IsProtectedFile,
            VsCodeCompatibilityProfile.VerifyRunningClientsByPublisher);

    internal static DesktopRouterProfile VerifyPublisherSignedRouter(string image,
        Func<string, string, bool, PublisherImageTrust> verify, Func<string, bool> isProtectedFile, Func<int> verifyRunningEngines)
    {
        if (VsCodeRouterCompatibilityProfile.IsExpectedImage(image))
        {
            RequirePublisher(verify(image, WindowsPublisherImageTrust.MicrosoftPublisher, false), null);
            if (verifyRunningEngines() == 0) throw new DesktopAdapterException("vscode_router_engine_unverified");
            return DesktopRouterProfile.VsCode;
        }
        // The package-name parse also pins the OpenAI.Codex family and its publisher ID; it grants no trust alone.
        string? version = DesktopCompatibilityProfile.InstalledApplicationVersion(image);
        if (version is null) throw new DesktopAdapterException("unsupported_desktop_version");
        RequirePublisher(verify(image, WindowsPublisherImageTrust.OpenAIPublisher, true), version);
        // The archive is not Authenticode signed. Package installation verified it, and the protected location
        // keeps it unchanged since; anything writable by another principal refuses.
        string archive = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(image))!, "resources", "app.asar");
        if (!isProtectedFile(archive)) throw new DesktopAdapterException("public_image_location_unprotected", version);
        return DesktopRouterProfile.Desktop;
    }

    internal static void RequirePublisher(PublisherImageTrust trust, string? version)
    {
        string? code = trust switch
        {
            PublisherImageTrust.ExpectedPublisher => null,
            PublisherImageTrust.UntrustedSignature => "public_image_signature_untrusted",
            PublisherImageTrust.UnexpectedPublisher => "public_image_publisher_mismatch",
            PublisherImageTrust.UnprotectedLocation => "public_image_location_unprotected",
            _ => "public_image_trust_unavailable",
        };
        if (code is not null) throw new DesktopAdapterException(code, version);
    }

    internal static DesktopRouterProfile VerifyPublicProfile(string image, Action<string> verifyVsCode,
        Action<string> verifyDesktop)
    {
        if (VsCodeRouterCompatibilityProfile.IsExpectedImage(image))
        {
            verifyVsCode(image);
            return DesktopRouterProfile.VsCode;
        }
        verifyDesktop(image);
        return DesktopRouterProfile.Desktop;
    }

    public void VerifyStillSameOwner()
    {
        if (disposed || !pipe.IsConnected) throw new DesktopAdapterException("connection_closed");
        var current = ReadConnectedIdentity(pipe.SafePipeHandle);
        if (current != Identity) throw new DesktopAdapterException("server_incarnation_changed");
    }

    private static OwnerProcessIdentity ReadConnectedIdentity(SafePipeHandle pipe)
    {
        if (!Native.GetNamedPipeServerProcessId(pipe, out uint pid) || pid <= 1)
            throw new DesktopAdapterException("server_pid_unverified");
        using var process = Native.OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !Native.GetProcessTimes(process, out long created, out _, out _, out _))
            throw new DesktopAdapterException("server_process_unverified");
        var image = new StringBuilder(32768);
        uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageNameW(process, 0, image, ref length))
            throw new DesktopAdapterException("server_image_unverified");
        if (!Native.OpenProcessToken(process, 8, out var accessToken))
            throw new DesktopAdapterException("server_user_unverified");
        using (accessToken)
        using (var owner = new WindowsIdentity(accessToken.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (owner.User is null || current.User is null || !owner.User.Equals(current.User))
                throw new DesktopAdapterException("server_user_mismatch");
        }
        return new(pid, unchecked((ulong)created), Path.GetFullPath(image.ToString()));
    }

    private static void VerifyPipeAcl(SafePipeHandle pipe)
    {
        // Request only OWNER/DACL metadata. Pipe ownership may be an application/admin SID; compare process token above.
        Native.GetKernelObjectSecurity(pipe, 5, null, 0, out uint needed);
        if (needed is < 20 or > 65536) throw new DesktopAdapterException("pipe_acl_unverified");
        byte[] bytes = new byte[needed];
        if (!Native.GetKernelObjectSecurity(pipe, 5, bytes, needed, out _))
            throw new DesktopAdapterException("pipe_acl_unverified");
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        if (descriptor.DiscretionaryAcl is null) throw new DesktopAdapterException("pipe_acl_unrestricted");
        using var current = WindowsIdentity.GetCurrent();
        string? user = current.User?.Value;
        // Data write, append/create-instance, write-EA/attributes, delete, ACL/owner changes, generic write/all.
        const int writeRights = 0x500D0116;
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce qualified || qualified.AceQualifier != AceQualifier.AccessAllowed) continue;
            if ((qualified.AccessMask & writeRights) == 0) continue;
            string sid = qualified.SecurityIdentifier.Value;
            if (sid != user && sid != "S-1-5-18" && sid != "S-1-5-32-544")
                throw new DesktopAdapterException("pipe_acl_foreign_write");
        }
        // Successful read/write connection establishes effective access for this token. No pipe-owner equality shortcut.
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await pipe.DisposeAsync();
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKernelObjectSecurity(SafePipeHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
    }
}
