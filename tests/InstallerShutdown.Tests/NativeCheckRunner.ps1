#requires -Version 5
function Invoke-ScopedNativeCheck {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [ValidateRange(1, 30000)][int]$TimeoutMilliseconds = 30000,
        [ValidateRange(1, 5000)][int]$CleanupMilliseconds = 5000,
        [ValidateRange(1, 5000)][int]$DrainMilliseconds = 5000,
        [scriptblock]$StartCaller = { param($Info) [Diagnostics.Process]::Start($Info) }
    )
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $quoted = foreach ($argument in $Arguments) {
        if ($argument -match '["\r\n]') { throw 'Native checker argument contains an unsupported character.' }
        # Every native checker argument is a flag, PID or complete image path.
        '"' + [regex]::Replace($argument, '(\\+)$', '$1$1') + '"'
    }
    $info.Arguments = $quoted -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = New-Object Text.UTF8Encoding($false, $true)
    $info.StandardErrorEncoding = New-Object Text.UTF8Encoding($false, $true)
    $process = $null; $stdoutReader = $null; $stderrReader = $null
    $primary = $null; $result = $null
    $exited = $false; $cleanupAttempted = $false
    try {
        $process = & $StartCaller $info
        if ($null -eq $process) { throw 'Native checker launch returned no retained child.' }
        $handle = $process.SafeHandle
        if ($null -eq $handle) { throw 'Native checker retained handle is unavailable.' }
        $stdoutReader = $process.StandardOutput
        $stderrReader = $process.StandardError
        # Start both readers before waiting, so neither pipe can block the checker.
        $stdout = $stdoutReader.ReadToEndAsync()
        $stderr = $stderrReader.ReadToEndAsync()
        $exited = $process.WaitForExit($TimeoutMilliseconds)
        $timedOut = -not $exited
        if ($timedOut) {
            $cleanupAttempted = $true
            $process.Kill()
            $exited = $process.WaitForExit($CleanupMilliseconds)
            if (-not $exited) { throw 'Owned native checker cleanup exceeded its bound.' }
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), $DrainMilliseconds)) {
            throw 'Native checker output drain exceeded its bound.'
        }
        $outText = $stdout.GetAwaiter().GetResult()
        $errText = $stderr.GetAwaiter().GetResult()
        $lines = @($outText -split '\r?\n') + @($errText -split '\r?\n')
        $result = [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $timedOut; ExitObserved = $exited; CallerPid = $process.Id; Lines = [string[]]$lines }
    }
    catch { $primary = $_ }
    finally {
        if ($null -ne $process -and -not $exited -and -not $cleanupAttempted) {
            try {
                $process.Kill()
                if (-not $process.WaitForExit($CleanupMilliseconds)) { throw 'Owned native checker failure cleanup exceeded its bound.' }
            }
            catch { if ($null -eq $primary) { $primary = $_ } }
        }
        foreach ($owned in @($stdoutReader, $stderrReader, $process)) {
            if ($null -ne $owned) {
                try { $owned.Dispose() }
                catch { if ($null -eq $primary) { $primary = $_ } }
            }
        }
    }
    if ($null -ne $primary) { throw $primary }
    return $result
}
