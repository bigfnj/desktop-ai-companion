#requires -Version 7.4
# 7.4, not 7: Start-Process -Environment (each child's private TEMP and data root, below) first shipped in PowerShell
# 7.4, so under 7.0 to 7.3 this probe failed at its first launch with a parameter error (N-audit-1106-371).
<#
.SYNOPSIS
Runs actual registry self-tests concurrently with private file and registry fixtures.
.EXAMPLE
pwsh -NoProfile -File tests/Test-PetTypeRegistryConcurrency.ps1 -Executable build/DesktopAICompanionPortable/bin/Release/x64/DesktopAICompanion.exe
.EXAMPLE
pwsh -NoProfile -File tests/Test-PetTypeRegistryConcurrency.ps1 -Executable build/DesktopAICompanionPortable/bin/Release/x64/DesktopAICompanion.exe -Parallelism 1 -Rounds 1 -ExpectF290Failure
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateRange(1, 8)][int]$Parallelism = 4,
    [ValidateRange(1, 20)][int]$Rounds = 4,
    [switch]$ExpectF290Failure
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This real registry concurrency probe requires Windows.' }
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$image = [IO.Path]::GetFullPath($Executable)
if ([IO.Path]::GetFileName($image) -cne 'DesktopAICompanion.exe' -or -not (Test-Path -LiteralPath $image -PathType Leaf)) {
    throw 'Supply the built public DesktopAICompanion.exe.'
}
$boundary = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$evidence = Join-Path $boundary ('pettyperegistry-concurrency-' + [Guid]::NewGuid().ToString('N'))
if (-not $evidence.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $evidence)) {
    throw 'Require a new contained evidence directory.'
}
[void][IO.Directory]::CreateDirectory($evidence)
$results = [Collections.Generic.List[object]]::new()
$namespaces = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$passed = $false
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        $children = [Collections.Generic.List[object]]::new()
        try {
            for ($slot = 1; $slot -le $Parallelism; $slot++) {
                $scratch = Join-Path $evidence ("r$round-$slot")
                $temp = Join-Path $scratch 'temp'
                $data = Join-Path $scratch 'data'
                [void][IO.Directory]::CreateDirectory($temp)
                [void][IO.Directory]::CreateDirectory($data)
                $stdout = Join-Path $scratch 'stdout.txt'
                $stderr = Join-Path $scratch 'stderr.txt'
                $child = Start-Process -FilePath $image -ArgumentList '--pettyperegistry-selftest' `
                    -WorkingDirectory ([IO.Path]::GetDirectoryName($image)) -WindowStyle Hidden -PassThru `
                    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -Environment @{
                        TEMP = $temp; TMP = $temp; DESKTOP_AI_COMPANION_DATA_ROOT = $data;
                        DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT = '1'
                    }
                $children.Add([pscustomobject]@{ Process = $child; Stdout = $stdout; Stderr = $stderr;
                    Marker = (Join-Path $temp 'dp-pettyperegistry-selftest.txt') })
            }
            foreach ($item in $children) {
                if (-not $item.Process.WaitForExit(20000)) { throw 'Owned registry self-test exceeded twenty seconds.' }
                $output = [IO.File]::ReadAllText($item.Stdout)
                $errorText = [IO.File]::ReadAllText($item.Stderr)
                $marker = [IO.File]::ReadAllText($item.Marker)
                $found = [regex]::Matches($output, '(?m)^NOTE: registry scratch namespace ([0-9a-f]{32})\r?$')
                if ($found.Count -ne 1 -or -not $namespaces.Add($found[0].Groups[1].Value)) {
                    throw 'Every actual self-test must report one distinct owned registry namespace.'
                }
                $ownedRoot = 'Software\DesktopAICompanion-SelfTest-' + $found[0].Groups[1].Value
                $remaining = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($ownedRoot, $false)
                if ($null -ne $remaining) {
                    $remaining.Dispose()
                    throw 'An exact owned registry namespace survived process exit.'
                }
                $failures = @([regex]::Matches($output, '(?m)^(?:FAIL:|EXC:).*$') | ForEach-Object { $_.Value })
                if ($ExpectF290Failure) {
                    $valid = $item.Process.ExitCode -eq 1 -and $output -match '(?m)^RESULT=FAIL\r?$' -and
                        $marker -match '(?m)^RESULT=FAIL\r?$' -and $failures.Count -eq 1 -and
                        $failures[0].StartsWith('FAIL: registry scratch: an empty Software', [StringComparison]::Ordinal)
                } else {
                    $valid = $item.Process.ExitCode -eq 0 -and $output -match '(?m)^RESULT=PASS\r?$' -and
                        $marker -match '(?m)^RESULT=PASS\r?$' -and $failures.Count -eq 0
                }
                $valid = $valid -and [string]::IsNullOrWhiteSpace($errorText)
                $results.Add([pscustomobject]@{ Round = $round; ProcessId = $item.Process.Id; ExitCode = $item.Process.ExitCode;
                    PrivateTempMarker = $true; DistinctRegistryNamespace = $true; OwnedRegistryRootRemoved = $true;
                    ExpectedVerdict = $valid; ExpectedF290Failure = [bool]$ExpectF290Failure })
                if (-not $valid) { throw 'Actual exit code, stdout, private marker or intended assertion disagreed.' }
            }
        }
        finally {
            $cleanupFailures = [Collections.Generic.List[string]]::new()
            foreach ($item in $children) {
                try {
                    if (-not $item.Process.HasExited) {
                        $item.Process.Kill()
                        if (-not $item.Process.WaitForExit(5000)) { $cleanupFailures.Add('owned child did not exit within five seconds after Kill') }
                    }
                }
                catch { $cleanupFailures.Add('owned child cleanup threw ' + $_.Exception.GetType().Name) }
                finally { $item.Process.Dispose() }
            }
            if ($cleanupFailures.Count -gt 0) { throw ('Owned registry self-test cleanup failed: ' + ($cleanupFailures -join '; ')) }
        }
        Write-Output "Round=$round Verified=$Parallelism"
    }
    $passed = $true
}
finally {
    $report = [ordered]@{ Passed = $passed; Parallelism = $Parallelism; Rounds = $Rounds; VerifiedProcesses = $results.Count;
        DistinctOwnedNamespaces = $namespaces.Count; ExpectedF290Failure = [bool]$ExpectF290Failure;
        PublicExecutableSha256 = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash; Results = @($results.ToArray()) } |
        ConvertTo-Json -Depth 6
    if (-not $passed) {
        # A failed run keeps its evidence folder and its report, as docs/test-artifact-retention.md keeps every failed
        # run's evidence, and says where they are. The throw that brought it here goes on after this block.
        [IO.File]::WriteAllText((Join-Path $evidence 'result.json'), $report, (New-Object Text.UTF8Encoding($false)))
        Write-Warning "Test-PetTypeRegistryConcurrency.ps1 failed; its evidence folder is kept for inspection: $evidence"
        Write-Output "Evidence=$evidence"
    }
}
# A PASSING RUN REMOVES ITS EVIDENCE FOLDER (N-bd-w10-tests-scripts-1-01). Every run kept artifacts\pettyperegistry-
# concurrency-<guid> (each child's stdout, stderr, private TEMP and data root, about 20 KB) on a pass as well as on a
# failure, and since tests\run-gate.ps1 runs this probe every gate run left one more, the class N-audit-1106-363 and
# N-audit-1106-366 closed for the worker-cleanup and installer-shutdown tests. Only here, past the finally above, which a
# failing run leaves by its throw and only after every owned child has exited or been killed. The leaf is checked first,
# so nothing but this run's own folder is deleted recursively, and a reparse point inside refuses the delete rather than
# having the delete meet it. A removal that fails keeps the folder with its report and says so; it never turns this
# passing run into a failure, the rule docs/test-artifact-retention.md sets for the gate's own cache cleanup.
$evidenceRemoved = $false
if (([IO.Path]::GetDirectoryName($evidence) + [IO.Path]::DirectorySeparatorChar) -ne $boundary -or
    (Split-Path -Leaf $evidence) -cnotmatch '^pettyperegistry-concurrency-[0-9a-f]{32}$') {
    Write-Warning "the evidence folder is not a pettyperegistry-concurrency-<guid> child of $boundary, so it is not deleted: $evidence"
}
elseif (@(Get-ChildItem -LiteralPath $evidence -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
    Write-Warning "the evidence folder holds a reparse point, so it is not deleted: $evidence"
}
else {
    try {
        [IO.Directory]::Delete($evidence, $true)
        $evidenceRemoved = -not [IO.Directory]::Exists($evidence)
    }
    catch { Write-Warning "the evidence folder could not be removed and is left for inspection: $evidence ($($_.Exception.Message))" }
}
if (-not $evidenceRemoved) {
    [IO.File]::WriteAllText((Join-Path $evidence 'result.json'), $report, (New-Object Text.UTF8Encoding($false)))
    Write-Output "Evidence=$evidence"
}
Write-Output "RESULT=PASS processes=$($results.Count) distinctNamespaces=$($namespaces.Count) evidenceRemoved=$evidenceRemoved"
