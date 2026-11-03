#requires -Version 5.1
<#
.SYNOPSIS
    Remove the tray icon records Windows keeps for Desktop AI Companion copies other than the installed app.
.DESCRIPTION
    Windows 11 files one record per tray icon under HKCU\Control Panel\NotifyIconSettings, keyed by the executable's
    path and the icon's ID, and lists every record in Settings > Personalization > Taskbar > Other system tray icons.
    An update in place keeps the installed app's record. Every copy started from another folder (a worktree build, a
    host self-test, a copy made for a real-app test) leaves records of its own that nothing removes, so they pile up in
    that list: 38 beside the installed app's one on the maintainer's workstation on 2026-11-02.

    -UnderPath removes the records of copies inside the given folders (the isolated gate passes its own worktree).
    -AllExceptInstalled removes every record but the installed app's. Either way only a record whose executable is named
    DesktopAICompanion.exe is touched, and a record whose path is not an absolute drive or UNC path (Windows can store a
    known-folder form) is kept, since it cannot be compared safely. -WhatIf lists without removing.
.EXAMPLE
    pwsh -NoProfile -File tools/Remove-TrayIconRecords.ps1 -AllExceptInstalled -WhatIf
.EXAMPLE
    pwsh -NoProfile -File tools/Remove-TrayIconRecords.ps1 -UnderPath D:\work\app-copy
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Under')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Under')][string[]]$UnderPath,
    [Parameter(Mandatory, ParameterSetName = 'AllExceptInstalled')][switch]$AllExceptInstalled,
    [Parameter(ParameterSetName = 'AllExceptInstalled')]
    [string]$InstalledPath = (Join-Path $env:LOCALAPPDATA 'Programs\Desktop AI Companion\DesktopAICompanion.exe'),
    # A test seam: tests\Test-TrayIconRecords.ps1 points this at a scratch key, never at the real records.
    [string]$RegistryRoot = 'HKCU:\Control Panel\NotifyIconSettings'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exeName = 'DesktopAICompanion.exe'

# An absolute drive or UNC path, normalised; $null for anything else, which is then never removed.
function ConvertTo-AbsolutePath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path) -or $Path -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\])') { return $null }
    try { return [IO.Path]::GetFullPath($Path) } catch { return $null }
}

$roots = @()
$installed = $null
if ($PSCmdlet.ParameterSetName -eq 'Under') {
    foreach ($folder in $UnderPath) {
        $full = ConvertTo-AbsolutePath $folder
        if ($null -eq $full) { throw "UnderPath must be an absolute folder path: $folder" }
        # The trailing separator keeps D:\work\app from matching D:\work\app2.
        $roots += $full.TrimEnd('\') + '\'
    }
}
else {
    $installed = ConvertTo-AbsolutePath $InstalledPath
    if ($null -eq $installed) { throw "InstalledPath must be an absolute file path: $InstalledPath" }
}

if (-not (Test-Path -LiteralPath $RegistryRoot)) {
    Write-Output "tray records: nothing to check, $RegistryRoot is absent"
    return
}
$checked = 0
$removed = 0
foreach ($key in @(Get-ChildItem -LiteralPath $RegistryRoot)) {
    $value = $key.GetValue('ExecutablePath')
    $full = if ($value -is [string]) { ConvertTo-AbsolutePath $value } else { $null }
    if ($null -eq $full -or -not [string]::Equals([IO.Path]::GetFileName($full), $exeName, [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    $checked++
    if ($null -ne $installed) {
        $remove = -not [string]::Equals($full, $installed, [StringComparison]::OrdinalIgnoreCase)
    }
    else {
        $remove = @($roots | Where-Object { $full.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    }
    if (-not $remove) { continue }
    if ($PSCmdlet.ShouldProcess("$($key.PSChildName) ($full)", 'Remove tray icon record')) {
        Remove-Item -LiteralPath $key.PSPath -Recurse
        $removed++
        Write-Output "removed tray record $($key.PSChildName): $full"
    }
}
Write-Output "tray records: removed $removed of $checked Desktop AI Companion record(s)"
