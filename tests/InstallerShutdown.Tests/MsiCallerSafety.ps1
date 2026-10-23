#requires -Version 5
# Shared only by the isolated MSI fixture and its fake-caller regression checks.
function New-ScopedMsiCallerState {
    return [pscustomobject]@{
        Uncertain = $false
        Reason = $null
        CallerPid = 0
        CallerCreationFileTime = $null
    }
}

function Assert-ScopedMsiMayStart {
    param([Parameter(Mandatory = $true)]$State)
    if ($State.Uncertain) { throw 'An earlier owned MSI caller has uncertain state; further MSI operations are refused.' }
}

function Invoke-ScopedMsiCaller {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][scriptblock]$StartCaller,
        [int]$TimeoutMilliseconds = 180000
    )
    Assert-ScopedMsiMayStart $State
    $process = $null
    $primary = $null
    $code = 0
    # A throwing launch can still have created a caller. Fail closed before dispatch.
    $State.Uncertain = $true
    $State.Reason = 'launch'
    $State.CallerPid = 0
    $State.CallerCreationFileTime = $null
    try {
        $process = & $StartCaller
        if ($null -eq $process) { throw 'Owned MSI launch returned no retained caller.' }
        $State.Reason = 'process_id'
        $State.CallerPid = [int]$process.Id
        if ($State.CallerPid -le 0) { throw 'Owned MSI caller PID is unavailable.' }
        $State.Reason = 'retain_handle'
        $handle = $process.SafeHandle
        if ($null -eq $handle) { throw 'Owned MSI caller handle is unavailable.' }
        $State.Reason = 'creation_time'
        $State.CallerCreationFileTime = $process.StartTime.ToFileTimeUtc()
        $State.Reason = 'wait'
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $State.Reason = 'timeout'
            throw 'Owned MSI caller timed out; no process was terminated and no further MSI will be started.'
        }
        # Exit was observed. Later ExitCode/Dispose errors cannot leave this caller running.
        $State.Uncertain = $false
        $State.Reason = $null
        $exitCode = $process.ExitCode
        if ($null -eq $exitCode) { throw 'Exited MSI caller exit code is unavailable.' }
        $code = [int]$exitCode
    }
    catch { $primary = $_ }
    finally {
        if ($null -ne $process) {
            try { $process.Dispose() }
            catch { if ($null -eq $primary) { $primary = $_ } }
        }
    }
    if ($null -ne $primary) { throw $primary }
    return $code
}
