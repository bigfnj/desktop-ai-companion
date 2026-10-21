# Query security metadata of the existing Codex IPC pipe. No message bytes are sent or read.
# Run: pwsh -NoProfile -File tools/agentbroker/inspect-codex-ipc-security.ps1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class AgentBrokerPipeSecurityNative {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information,
        byte[] descriptor, uint length, out uint needed);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafeFileHandle handle, out uint pid);
}
'@

# READ_CONTROL, shared read/write, OPEN_EXISTING, identification-only security context.
$agentBrokerHandle = [AgentBrokerPipeSecurityNative]::CreateFileW(
    '\\.\pipe\codex-ipc', 0x00020000, 3, [IntPtr]::Zero, 3, 0x00110000, [IntPtr]::Zero)
if ($agentBrokerHandle.IsInvalid) {
    throw "Pipe security metadata open failed (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
}
try {
    [uint32]$agentBrokerNeeded = 0
    $null = [AgentBrokerPipeSecurityNative]::GetKernelObjectSecurity($agentBrokerHandle, 5, $null, 0, [ref]$agentBrokerNeeded)
    if ($agentBrokerNeeded -lt 20 -or $agentBrokerNeeded -gt 65536) {
        throw 'Pipe security descriptor size was unexpected.'
    }
    $agentBrokerDescriptorBytes = [byte[]]::new($agentBrokerNeeded)
    if (-not [AgentBrokerPipeSecurityNative]::GetKernelObjectSecurity(
            $agentBrokerHandle, 5, $agentBrokerDescriptorBytes, $agentBrokerNeeded, [ref]$agentBrokerNeeded)) {
        throw "Pipe security descriptor query failed (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
    }
    $agentBrokerDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($agentBrokerDescriptorBytes, 0)
    $agentBrokerCurrentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $agentBrokerRoles = @{
        'S-1-1-0' = 'world'
        'S-1-5-7' = 'anonymous'
        'S-1-5-11' = 'authenticatedUsers'
        'S-1-5-18' = 'system'
        'S-1-5-32-544' = 'administrators'
    }
    $agentBrokerRoles[$agentBrokerCurrentSid] = 'currentUser'
    $agentBrokerAces = @()
    foreach ($agentBrokerAce in $agentBrokerDescriptor.DiscretionaryAcl) {
        if ($agentBrokerAce -isnot [Security.AccessControl.QualifiedAce]) { continue }
        $agentBrokerRole = $agentBrokerRoles[$agentBrokerAce.SecurityIdentifier.Value]
        $agentBrokerAces += [pscustomobject]@{
            principal = $(if ($agentBrokerRole) { $agentBrokerRole } else { 'other' })
            qualifier = $agentBrokerAce.AceQualifier.ToString()
            accessMask = ('0x{0:X8}' -f $agentBrokerAce.AccessMask)
        }
    }
    [uint32]$agentBrokerServerPid = 0
    $agentBrokerPidKnown = [AgentBrokerPipeSecurityNative]::GetNamedPipeServerProcessId(
        $agentBrokerHandle, [ref]$agentBrokerServerPid)
    [pscustomobject]@{
        probe = 'codex-ipc-security'
        ownerIsCurrentUser = ($agentBrokerDescriptor.Owner.Value -eq $agentBrokerCurrentSid)
        daclPresent = ($null -ne $agentBrokerDescriptor.DiscretionaryAcl)
        serverPid = $(if ($agentBrokerPidKnown) { $agentBrokerServerPid } else { $null })
        aces = $agentBrokerAces
    } | ConvertTo-Json -Depth 4
} finally {
    $agentBrokerHandle.Dispose()
}
