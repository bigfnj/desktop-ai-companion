#requires -Version 7
<#
.SYNOPSIS
    Identify the failing stage of a read-only native Codex owner probe.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-CodexOwnerConnection.ps1 -ThreadId <authorized-test-UUID>
.DESCRIPTION
    Sends no agent input. The first connection sends no bytes; the adapter then
    performs its unchanged pinned identity, initialization and exact-owner checks.
    Only bounded stage names, error codes and capability booleans are reported.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Guid]$ThreadId,
    [ValidateRange(1, 15)][int]$RequestTimeoutSeconds = 5
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ThreadId -eq [Guid]::Empty) { throw 'A nonempty authorized test thread ID is required.' }
$stage = 'pipe-connect'
$pipeReached = $false
$initialized = $false
$ownerFound = $false
$capability = $false
$errorCode = $null
$adapter = $null
$pipe = $null
$discoveryClock = $null
$deadline = [Threading.CancellationTokenSource]::new(30000)
try {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'codex-ipc', [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
    [void]$pipe.ConnectAsync(5000, $deadline.Token).GetAwaiter().GetResult()
    $pipeReached = $true
    $pipe.Dispose()
    $pipe = $null
    $stage = 'load-probe-runtime'
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $assembly = Join-Path $repo 'tools\AgentBroker.CodexDesktopAdapter\bin\Release\net10.0-windows\AgentBroker.CodexDesktopAdapter.dll'
    [void][Reflection.Assembly]::LoadFrom($assembly)
    $stage = 'pinned-connect-and-initialize'
    $options = [AgentBroker.CodexDesktopAdapter.DesktopAdapterOptions]::new(
        $false, [TimeSpan]::FromSeconds($RequestTimeoutSeconds), 16384, $false)
    $adapter = [AgentBroker.CodexDesktopAdapter.DesktopAdapter]::ConnectAsync($options, $deadline.Token).GetAwaiter().GetResult()
    $initialized = $true
    $stage = 'exact-owner-discovery'
    $discoveryClock = [Diagnostics.Stopwatch]::StartNew()
    $binding = $adapter.DiscoverAsync($ThreadId, $deadline.Token).GetAwaiter().GetResult()
    $ownerFound = $null -ne $binding
    if ($ownerFound) { $capability = $binding.SupportsUntrustedAppInput }
    $stage = 'complete'
} catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    if ($failure -is [OperationCanceledException] -or $failure -is [TimeoutException]) {
        $errorCode = 'timeout_or_cancelled'
    } elseif ($failure.GetType().FullName -eq 'AgentBroker.CodexDesktopAdapter.DesktopAdapterException') {
        $errorCode = [string]$failure.Code
    } elseif ($failure -is [UnauthorizedAccessException]) {
        $errorCode = 'access_refused'
    } elseif ($failure -is [IO.IOException]) {
        $errorCode = 'connection_io_failed'
    } else { $errorCode = 'probe_runtime_failed' }
} finally {
    if ($null -ne $discoveryClock) { $discoveryClock.Stop() }
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $adapter) {
        try { [void]$adapter.DisposeAsync().AsTask().GetAwaiter().GetResult() }
        catch { if ($null -eq $errorCode) { $errorCode = 'probe_cleanup_failed' } }
    }
    $deadline.Dispose()
}
[ordered]@{ probe = 'codex-owner-connection-stages'; stage = $stage;
    requestTimeoutSeconds = $RequestTimeoutSeconds;
    discoveryElapsedMilliseconds = $(if ($null -eq $discoveryClock) { 0 } else { $discoveryClock.ElapsedMilliseconds });
    pipeReached = $pipeReached; initialized = $initialized; ownerFound = $ownerFound;
    supportsUntrustedAppInput = $capability; errorCode = $errorCode; inputSent = $false } |
    ConvertTo-Json -Compress
if ($null -ne $errorCode -or -not $ownerFound) { exit 1 }
