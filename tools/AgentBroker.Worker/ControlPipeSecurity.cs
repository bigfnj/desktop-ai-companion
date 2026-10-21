// Linked into the thin module alongside ControlProtocol. No ASP.NET or host dependencies.
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentBroker.Protocol;

/// <summary>Local management authority is the actual token User, never its default Owner group.</summary>
public static class ControlPipeSecurity
{
    // Generic write/all, delete, change-owner/DACL, write-data/attributes/EA, create-pipe-instance.
    private const int WriteOrControl = 0x500D0116;

    public static NamedPipeServerStream CreateServer(string name, int maximumInstances = 4)
    {
        SecurityIdentifier user = CurrentUser();
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // Named pipes can otherwise be reached over SMB by a network logon of the same account.
        // Management is local; LAN broker authority is exclusively the paired HTTPS listener.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maximumInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        try
        {
            // Verify the kernel object's actual descriptor too, including when another instance
            // already existed. A foreign-owned or weakened pipe never acquires control authority.
            if (!IsCurrentUserPipe(ReadDescriptor(pipe.SafePipeHandle), user))
                throw new UnauthorizedAccessException("control_pipe_security_refused");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public static NamedPipeClientStream CreateClient(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

    /// <summary>Call after ConnectAsync and before writing any management request or reading a token.</summary>
    public static int ValidateConnectedServer(NamedPipeClientStream pipe, int? requiredServerPid = null)
    {
        if (!pipe.IsConnected || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) ||
            pid is 0 or > int.MaxValue || requiredServerPid is { } expected && pid != expected)
            throw new IOException("control_server_pid_mismatch");
        using SafeProcessHandle process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY
            throw SecurityUnavailable("control_server_identity_unavailable");
        SecurityIdentifier user = CurrentUser();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User is not { } serverUser || !serverUser.Equals(user))
                throw new UnauthorizedAccessException("control_server_user_mismatch");
        if (!IsCurrentUserPipe(ReadDescriptor(pipe.SafePipeHandle), user))
            throw new UnauthorizedAccessException("control_pipe_security_refused");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint again) || again != pid ||
            !GetExitCodeProcess(process, out uint exitCode) || exitCode != 259) // STILL_ACTIVE
            throw new IOException("control_server_identity_changed");
        return (int)pid;
    }

    /// <summary>Pure policy over already retrieved metadata, also used by hostile-descriptor tests.</summary>
    public static bool IsCurrentUserPipe(RawSecurityDescriptor descriptor, SecurityIdentifier user)
    {
        if (descriptor.Owner is not { } owner || !owner.Equals(user) ||
            (descriptor.ControlFlags & (ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected)) !=
                (ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected) ||
            descriptor.DiscretionaryAcl is not { } dacl)
            return false;
        bool networkDenied = false;
        bool allowSeen = false;
        foreach (GenericAce ace in dacl)
        {
            if (ace is not CommonAce common || common.IsCallback) return false;
            if (common.AceQualifier == AceQualifier.AccessDenied)
            {
                // A deny after an allow can be bypassed by access already granted. Inherit-only
                // ACEs do not protect this pipe object, even if the SID and rights look correct.
                if (allowSeen) return false;
                if ((common.AceFlags & AceFlags.InheritOnly) == 0 &&
                    common.SecurityIdentifier.IsWellKnown(WellKnownSidType.NetworkSid) &&
                    ((common.AccessMask & (int)PipeAccessRights.FullControl) == (int)PipeAccessRights.FullControl ||
                     (common.AccessMask & 0x10000000) != 0)) networkDenied = true;
                continue;
            }
            if (common.AceQualifier != AceQualifier.AccessAllowed) return false;
            allowSeen = true;
            if ((common.AccessMask & WriteOrControl) == 0) continue;
            if (!common.SecurityIdentifier.Equals(user) &&
                !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid)) return false;
        }
        return networkDenied;
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("control_current_user_unavailable");
    }

    private static RawSecurityDescriptor ReadDescriptor(SafePipeHandle handle)
    {
        uint error = GetSecurityInfo(handle, 6, 1 | 4, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw SecurityUnavailable("control_pipe_security_unavailable", (int)error);
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 65536) throw new IOException("control_pipe_security_invalid");
            byte[] bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally { LocalFree(descriptor); }
    }

    private static UnauthorizedAccessException SecurityUnavailable(string code, int? error = null) =>
        new(code, new Win32Exception(error ?? Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int kind, uint information, out IntPtr owner,
        out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
