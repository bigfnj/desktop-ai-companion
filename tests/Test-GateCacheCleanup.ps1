#requires -Version 7
<# Run: pwsh -NoProfile -File tests/Test-GateCacheCleanup.ps1 #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$helper = Join-Path $repo 'tools\agentbroker\Remove-CompletedGateCaches.ps1'
$root = Join-Path $repo 'artifacts\isolated-gate'
$ids = @(1..4 | ForEach-Object { [Guid]::NewGuid().ToString('N').Substring(0, 8) })
$runs = @($ids | ForEach-Object { Join-Path $root $_ })
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Gate cache cleanup: $Message" }
    $script:checks++
}
function Invoke-Helper([string]$RunIds, [bool]$Apply) {
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $helper, '-CompletedRunIds', $RunIds)) { $start.ArgumentList.Add($argument) }
    if ($Apply) { $start.ArgumentList.Add('-Apply') }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) { $process.Kill(); $process.WaitForExit(); throw 'Owned cleanup probe timed out.' }
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
try {
    foreach ($run in $runs) {
        Check (-not [IO.Directory]::Exists($run)) 'synthetic run root is new'
        [void][IO.Directory]::CreateDirectory($run)
    }
    foreach ($run in $runs[0..2]) {
        [void][IO.Directory]::CreateDirectory((Join-Path $run 'dotnet-home'))
        [IO.File]::WriteAllText((Join-Path $run 'dotnet-home\cache.txt'), 'synthetic generated cache')
        [IO.File]::SetLastWriteTimeUtc((Join-Path $run 'dotnet-home\cache.txt'), [DateTime]::UtcNow.AddHours(-5))
        [IO.File]::WriteAllText((Join-Path $run 'result.log'), 'retained evidence')
    }
    $dry = Invoke-Helper $ids[0] $false
    Check ($dry.Code -eq 0 -and (Test-Path -LiteralPath (Join-Path $runs[0] 'dotnet-home\cache.txt'))) 'dry run preserves the cache'
    $apply = Invoke-Helper $ids[0] $true
    Check ($apply.Code -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $runs[0] 'dotnet-home'))) 'apply removes only the completed cache'
    Check (([IO.File]::ReadAllText((Join-Path $runs[0] 'result.log'))) -ceq 'retained evidence') 'evidence outside the cache survives'
    Check ((Get-Content -LiteralPath (Join-Path $runs[0] 'cache-cleanup.json') -Raw | ConvertFrom-Json).State -eq 'removed') 'successful cleanup records its receipt'
    [IO.File]::SetLastWriteTimeUtc((Join-Path $runs[2] 'dotnet-home\cache.txt'), [DateTime]::UtcNow)
    $refused = Invoke-Helper ($ids[1] + ',' + $ids[2]) $true
    Check ($refused.Code -ne 0 -and (Test-Path -LiteralPath (Join-Path $runs[1] 'dotnet-home\cache.txt'))) 'all candidates validate before any deletion'
    $link = Join-Path $runs[3] 'dotnet-home'
    [void](New-Item -ItemType Junction -Path $link -Target (Join-Path $runs[1] 'dotnet-home'))
    $refusedLink = Invoke-Helper $ids[3] $true
    Check ($refusedLink.Code -ne 0 -and ([IO.File]::ReadAllText((Join-Path $runs[1] 'dotnet-home\cache.txt'))) -ceq 'synthetic generated cache') 'junction is refused without following or changing its target'
    [IO.Directory]::Delete($link, $false)
    $invalid = Invoke-Helper '../escape' $true
    Check ($invalid.Code -ne 0) 'traversal input is refused'

    # Exercise the actual pure path predicate without running its enclosing script.
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) 'cleanup helper parses cleanly'
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-RunImagePath' }, $false)
    . ([scriptblock]::Create($function.Extent.Text))
    $long = 'C:\Users\test-user\workspace\artifacts\isolated-gate\1234abcd'
    $short = 'C:\Users\TEST-U~1\WORKSP~1\ARTIFA~1\ISOLAT~1\1234abcd'
    Check (Test-RunImagePath ($long + '\test.exe') $long $short) 'long running-image path matches'
    Check (Test-RunImagePath ($short + '\test.exe') $long $short) 'short running-image path matches'
    Check (Test-RunImagePath 'C:\Users\TEST-U~1\workspace\ARTIFA~1\isolated-gate\1234abcd\test.exe' $long $short) 'mixed long and short components match'
    Check (-not (Test-RunImagePath ($long + '-other\test.exe') $long $short)) 'a sibling run is not confused with the selected run'

    # Execute the actual gate success hook, without its SDK/test dispatch.
    $gateAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-IsolatedGate.ps1'), [ref]$tokens, [ref]$errors)
    $hooks = @($gateAst.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq '$gateExit -eq 0'
    })
    Check ($hooks.Count -eq 1) 'the actual gate has one successful-run cleanup hook'
    $hook = [scriptblock]::Create($hooks[0].Extent.Text)
    $repoRoot = $repo; $runRoot = $runs[3]
    [void][IO.Directory]::CreateDirectory((Join-Path $runRoot 'dotnet-home'))
    [IO.File]::WriteAllText((Join-Path $runRoot 'dotnet-home\cache.txt'), 'synthetic current owner cache')
    $gateExit = 1
    & $hook | Out-Null
    Check ([IO.File]::Exists((Join-Path $runRoot 'dotnet-home\cache.txt'))) 'failed gate retains its cache'
    $gateExit = 0
    & $hook | Out-Null
    Check (-not [IO.Directory]::Exists((Join-Path $runRoot 'dotnet-home'))) 'successful owner releases its recent SDK cache'
    Write-Host "Gate cache cleanup checks passed: $checks"
} finally {
    # Every path here is one of the fresh ordinary fixtures created above.
    foreach ($run in $runs) {
        $link = Join-Path $run 'dotnet-home'
        if ([IO.Directory]::Exists($link) -and (([IO.File]::GetAttributes($link) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            [IO.Directory]::Delete($link, $false)
        }
        foreach ($relative in @('dotnet-home\cache.txt', 'result.log', 'cache-cleanup.json')) {
            $file = Join-Path $run $relative
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        if ([IO.Directory]::Exists($link)) { [IO.Directory]::Delete($link, $false) }
        if ([IO.Directory]::Exists($run)) { [IO.Directory]::Delete($run, $false) }
    }
}
