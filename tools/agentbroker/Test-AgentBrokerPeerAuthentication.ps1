#requires -Version 7
<#
.SYNOPSIS
    Probe authenticated paired-broker directories without reading agent messages.
.DESCRIPTION
    Uses the exact current-user production management pipe and required worker PID.
    The worker performs its existing pinned, authenticated peer-directory operation.
    A successful empty directory reports zero; failed or unperformed checks report
    a null session count and a fixed error code. No session identifiers or contents
    are returned. No pairing, settings, firewall, credential export or input changes.
    Older workers report unknown_control_operation instead of implying success.
    Exit 1 means the operation was unavailable or at least one peer check failed.
    Exit 0 means all returned checks succeeded; an empty peer array means no
    paired peers were checked, and does not establish remote authentication.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerPeerAuthentication.ps1 -CompileOnly
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerPeerAuthentication.ps1 -WorkerProcessId 1234
#>
[CmdletBinding()]
param([ValidateRange(2, 2147483647)][int]$WorkerProcessId, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This diagnostic requires Windows.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$parts = [Collections.Generic.List[string]]::new()
$parts.Add(@'
global using System;
global using System.IO;
global using System.Linq;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;
'@)
foreach ($relative in @('tools\AgentBroker.Worker\ControlProtocol.cs',
    'tools\AgentBroker.Worker\ControlPipeSecurity.cs', 'modules\AgentBroker\BrokerControlClient.cs')) {
    $source = [IO.File]::ReadAllText((Join-Path $repo $relative))
    $match = [regex]::Match($source, '(?m)^namespace ([A-Za-z0-9_.]+);\s*$')
    if (-not $match.Success -or [regex]::Matches($source, '(?m)^namespace ').Count -ne 1) {
        throw 'An expected diagnostic source namespace changed.'
    }
    $preamble = [regex]::Replace($source.Substring(0, $match.Index), '(?m)^using ', 'using global::')
    $parts.Add('namespace ' + $match.Groups[1].Value + " {`n" + $preamble +
        $source.Substring($match.Index + $match.Length) + "`n}")
}
$parts.Add(@'
namespace DesktopAICompanion.AgentBroker {
public static class AuthenticatedPeerDiagnostic {
    public static async Task<string> RunAsync(string data, int pid) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await new BrokerControlClient().SendAsync(data,
            new global::AgentBroker.Protocol.ControlRequest("peer-diagnostics"), timeout.Token, pid);
        return System.Text.Json.JsonSerializer.Serialize(new {
            CheckedUtc = DateTimeOffset.UtcNow, WorkerProcessId = pid,
            OwnedPipeVerified = true, Success = response.Success, Code = response.Code,
            Peers = response.PeerDiagnostics
        });
    }
}
}
'@)
Add-Type -TypeDefinition ([string]::Join("`n", $parts)) -CompilerOptions '/nullable:enable'
if ($CompileOnly) { Write-Output 'SourceCompiled=True PipeOpened=False NetworkProbed=False'; return }
if (-not $PSBoundParameters.ContainsKey('WorkerProcessId')) { throw 'An exact current WorkerProcessId is required.' }
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DesktopAICompanion\modules\agentbroker'
$json = [DesktopAICompanion.AgentBroker.AuthenticatedPeerDiagnostic]::RunAsync($data, $WorkerProcessId).GetAwaiter().GetResult()
Write-Output $json
$result = $json | ConvertFrom-Json
if (-not $result.Success -or $null -eq $result.Peers -or @($result.Peers | Where-Object { -not $_.Success }).Count -gt 0) { exit 1 }
