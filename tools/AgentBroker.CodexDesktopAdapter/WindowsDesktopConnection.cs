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
    public const string PackageFullName = "OpenAI.Codex_26.1016.6573.0_x64__2p2nqsd0c76g0";
    public const string ApplicationVersion = "26.1016.6573.0";
    public const string ExecutableSha256 = "602A21169FA011CF48997DBC07A0FFCDC11058CAA5DC992E534876E8CB6F9A4E";
    public const string ArchiveSha256 = "6F56542B508EEA5B6E4D03E852DCD90AFC2578AAC7125BAA3F25EBE85EBC7E25";
    public const string ElectronVersion = "155.0.8059.80";

    internal static string ApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", PackageFullName, "app");

    internal static void VerifyPublicImages(string image)
    {
        string expected = Path.Combine(ApplicationDirectory, "ChatGPT.exe");
        if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new DesktopAdapterException("unsupported_desktop_version");
        string archive = Path.Combine(ApplicationDirectory, "resources", "app.asar");
        VerifyRegularPath(expected);
        VerifyRegularPath(archive);
        if (FileVersionInfo.GetVersionInfo(expected).ProductVersion != ElectronVersion
            || Hash(expected) != ExecutableSha256 || Hash(archive) != ArchiveSha256)
            throw new DesktopAdapterException("desktop_image_mismatch");
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void VerifyRegularPath(string file)
    {
        for (string? path = Path.GetFullPath(file); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new DesktopAdapterException("linked_desktop_image");
            if (Path.GetPathRoot(path) == path) break;
        }
    }
}

internal sealed class WindowsDesktopConnection : IDesktopConnection
{
    private readonly NamedPipeClientStream pipe;
    public Stream Stream => pipe;
    public OwnerProcessIdentity Identity { get; }
    private bool disposed;

    private WindowsDesktopConnection(NamedPipeClientStream pipe, OwnerProcessIdentity identity)
    { this.pipe = pipe; Identity = identity; }

    internal static async Task<IDesktopConnection> ConnectAsync(TimeSpan timeout, CancellationToken token)
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
            var identity = ReadConnectedIdentity(pipe.SafePipeHandle);
            VerifyPipeAcl(pipe.SafePipeHandle);
            if (VsCodeRouterCompatibilityProfile.IsExpectedImage(identity.ImagePath))
                VsCodeRouterCompatibilityProfile.VerifyPublicImage(identity.ImagePath);
            else DesktopCompatibilityProfile.VerifyPublicImages(identity.ImagePath);
            var connection = new WindowsDesktopConnection(pipe, identity);
            connection.VerifyStillSameOwner();
            return connection;
        }
        catch { await pipe.DisposeAsync(); throw; }
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
