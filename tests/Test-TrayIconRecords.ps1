#requires -Version 5.1
<#
.SYNOPSIS
    Check tools\Remove-TrayIconRecords.ps1 against synthetic tray records under a scratch HKCU key.
.DESCRIPTION
    Never reads or writes the real HKCU\Control Panel\NotifyIconSettings: every record here lives under
    HKCU\Software\DesktopAICompanion-TrayRecordsSelfTest-<random>, removed in a finally. Throws on the first run with
    every failed check listed, as the other checks in tests\Invoke-OfflineScriptChecks.ps1 do.
.EXAMPLE
    pwsh -NoProfile -File tests/Test-TrayIconRecords.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tool = Join-Path $PSScriptRoot '..\tools\Remove-TrayIconRecords.ps1'
$scratch = 'HKCU:\Software\DesktopAICompanion-TrayRecordsSelfTest-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$installed = 'C:\Users\Example\AppData\Local\Programs\Desktop AI Companion\DesktopAICompanion.exe'
$records = [ordered]@{
    'installed'   = $installed
    'worktree'    = 'D:\work\dac-x\build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
    'caseDiffers' = 'd:\WORK\DAC-X\copy\desktopaicompanion.EXE'
    'sibling'     = 'D:\work\dac-x2\build\DesktopAICompanion.exe'
    'otherApp'    = 'D:\work\dac-x\tools\Other.exe'
    'knownFolder' = '{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\Programs\Desktop AI Companion\DesktopAICompanion.exe'
    'noPath'      = $null
    'tempCopy'    = 'C:\Temp\af-queue-x\app\DesktopAICompanion.exe'
}
$failures = New-Object 'Collections.Generic.List[string]'
$checks = 0
function Check([bool]$Condition, [string]$Label) {
    $script:checks++
    if (-not $Condition) { $script:failures.Add($Label) }
}
function Reset-Records {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse }
    New-Item -Path $scratch | Out-Null
    foreach ($name in $records.Keys) {
        New-Item -Path (Join-Path $scratch $name) | Out-Null
        New-ItemProperty -LiteralPath (Join-Path $scratch $name) -Name 'UID' -Value 1 -PropertyType DWord | Out-Null
        if ($null -ne $records[$name]) {
            New-ItemProperty -LiteralPath (Join-Path $scratch $name) -Name 'ExecutablePath' -Value $records[$name] -PropertyType String | Out-Null
        }
    }
}
function Get-Remaining { @(Get-ChildItem -LiteralPath $scratch | ForEach-Object { $_.PSChildName } | Sort-Object) }
function Same([string[]]$Actual, [string[]]$Expected) { (($Actual | Sort-Object) -join ',') -eq (($Expected | Sort-Object) -join ',') }

try {
    Reset-Records
    $out = & $tool -UnderPath 'D:\work\dac-x' -RegistryRoot $scratch
    $left = Get-Remaining
    Check (-not ($left -contains 'worktree')) 'UnderPath removes a copy inside the folder'
    Check (-not ($left -contains 'caseDiffers')) 'UnderPath matches the folder and the file name whatever their letter case'
    Check ($left -contains 'sibling') 'UnderPath keeps a copy in a sibling folder whose name only starts the same'
    Check ($left -contains 'otherApp') 'UnderPath keeps another program inside the folder'
    Check ($left -contains 'installed' -and $left -contains 'tempCopy') 'UnderPath keeps copies outside the folder'
    Check ($left -contains 'knownFolder' -and $left -contains 'noPath') 'UnderPath keeps a record it cannot compare'
    Check (Same $left @('installed', 'sibling', 'otherApp', 'knownFolder', 'noPath', 'tempCopy')) "UnderPath removes exactly the two copies inside the folder (left: $($left -join ','))"
    Check ((@($out) -join "`n") -match 'removed 2 of 5 ') "UnderPath reports what it removed (got: $(@($out)[-1]))"

    Reset-Records
    $out = & $tool -AllExceptInstalled -InstalledPath $installed -RegistryRoot $scratch -WhatIf
    Check ((Get-Remaining).Count -eq $records.Count) 'WhatIf removes nothing'
    Check ((@($out) -join "`n") -match 'removed 0 of 5 ') "WhatIf reports nothing removed (got: $(@($out)[-1]))"

    $out = & $tool -AllExceptInstalled -InstalledPath $installed.ToUpperInvariant() -RegistryRoot $scratch
    $left = Get-Remaining
    Check ($left -contains 'installed') 'AllExceptInstalled keeps the installed app, whatever the letter case of its path'
    Check ($left -contains 'otherApp') 'AllExceptInstalled keeps other programs'
    Check ($left -contains 'knownFolder' -and $left -contains 'noPath') 'AllExceptInstalled keeps a record it cannot compare'
    Check (Same $left @('installed', 'otherApp', 'knownFolder', 'noPath')) "AllExceptInstalled removes every other copy (left: $($left -join ','))"
    Check ((@($out) -join "`n") -match 'removed 4 of 5 ') "AllExceptInstalled reports what it removed (got: $(@($out)[-1]))"

    $refused = $false
    try { $null = & $tool -UnderPath 'relative\folder' -RegistryRoot $scratch } catch { $refused = $true }
    Check $refused 'a relative UnderPath is refused, not resolved against the current folder'
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse }
}
Check (-not (Test-Path -LiteralPath $scratch)) 'the scratch key is removed'
# One line per failed check as well, since a thrown message is wrapped to the console width.
foreach ($failure in $failures) { Write-Host "FAIL: $failure" }
if ($failures.Count -gt 0) { throw ("Test-TrayIconRecords.ps1: $($failures.Count) of $checks check(s) failed: " + ($failures -join ' | ')) }
Write-Host "PASS: Test-TrayIconRecords.ps1 ($checks checks)"
