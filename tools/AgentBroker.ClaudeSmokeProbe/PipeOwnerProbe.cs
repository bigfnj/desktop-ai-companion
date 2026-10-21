using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AgentBroker.ClaudeAdapter;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.ClaudeSmokeProbe;

/// <summary>One-target diagnostics only. No credential reader and no stream write operations.</summary>
internal static class PipeOwnerProbe
{
    internal static async Task<int> RunAsync(SmokeTarget selected)
    {
        string stage = "pipe_connect_without_owner_constraint";
        bool connected = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var name = CanonicalPeerTarget.Parse(selected.Native.PipePath).PipeName;
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, deadline.Token);
            connected = true;
            stage = "server_identity";
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid > int.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var process = Process.GetProcessById((int)pid);
            if (!GetProcessTimes(process.SafeHandle, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            ulong started = ((ulong)created.High << 32) | created.Low;
            string image = process.MainModule?.FileName ?? throw new IOException();
            if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            bool currentUser;
            using var current = WindowsIdentity.GetCurrent();
            using (token)
            using (var server = new WindowsIdentity(token.DangerousGetHandle()))
                currentUser = server.User is { } user && current.User is { } currentSid && user.Equals(currentSid);
            bool processMatches = pid == selected.Native.ProcessId && started == selected.Native.StartFileTime;
            bool pathMatches = string.Equals(Path.GetFullPath(image), Path.GetFullPath(selected.Native.ImagePath), StringComparison.OrdinalIgnoreCase);
            if (!currentUser || !processMatches || !pathMatches)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { stage, pipeConnected = connected, currentUser, processMatches, pathMatches,
                    credentialsRead = false, bytesWritten = 0 }));
                return 3;
            }
            stage = "server_public_image_hash";
            CredentialPathPolicy.RejectReparseComponents(image, File.GetAttributes);
            using var executable = File.OpenRead(image);
            bool imageMatches = Convert.ToHexString(SHA256.HashData(executable)) == selected.Native.ImageSha256;
            if (!imageMatches || process.HasExited || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint again) || again != pid)
                throw new IOException();
            stage = "pipe_owner_and_dacl";
            uint error = GetSecurityInfo(pipe.SafePipeHandle, 6, 1 | 4, out _, out _, out _, out _, out var security);
            if (error != 0) throw new Win32Exception((int)error);
            try
            {
                uint length = GetSecurityDescriptorLength(security);
                if (length is 0 or > 65536) throw new IOException();
                byte[] bytes = new byte[length];
                Marshal.Copy(security, bytes, 0, bytes.Length);
                var descriptor = new RawSecurityDescriptor(bytes, 0);
                var owner = descriptor.Owner;
                bool ownerMatchesUser = owner is not null && current.User is { } userSid && owner.Equals(userSid);
                bool ownerMatchesTokenOwner = owner is not null && current.Owner is { } ownerSid && owner.Equals(ownerSid);
                bool ownerIsAdministrators = owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) == true;
                bool ownerIsSystem = owner?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
                bool explicitPolicyAccepts = current.User is { } policyUser && NativePipeSecurityPolicy.IsCurrentUserPipe(descriptor, policyUser);
                bool foreignWriteAllowed = descriptor.DiscretionaryAcl is null;
                foreach (GenericAce ace in descriptor.DiscretionaryAcl ?? new RawAcl(2, 0))
                    if (ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed } allowed &&
                        (allowed.AccessMask & 0x500D0116) != 0 &&
                        (allowed.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) ||
                         allowed.SecurityIdentifier.IsWellKnown(WellKnownSidType.AnonymousSid) ||
                         allowed.SecurityIdentifier.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))) foreignWriteAllowed = true;
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "complete", pipeConnected = connected, currentUser, processMatches, imageMatches,
                    ownerMatchesUser, ownerMatchesTokenOwner, ownerIsAdministrators, ownerIsSystem, foreignWriteAllowed, explicitPolicyAccepts,
                    daclPresent = descriptor.DiscretionaryAcl is not null, credentialsRead = false, bytesWritten = 0 }));
                return 0;
            }
            finally { LocalFree(security); }
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { stage, pipeConnected = connected, errorType = ex.GetType().Name,
                nativeErrorCode = ex is Win32Exception native ? native.NativeErrorCode : 0, hresult = ex.HResult,
                credentialsRead = false, bytesWritten = 0 }));
            return 1;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low; internal uint High; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int kind, uint information, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
