#requires -Version 5
<#
.SYNOPSIS
    Assert the installer's user-facing surface against the BUILT MSI's tables.

.DESCRIPTION
    Everything here was verified once, by hand, and then written down in a commit message. That is not
    coverage: a commit message cannot fail. Two of these facts had already been wrong in a shipped build --
    the reset page was published as a ControlEvent that MSI could never reach, so it existed in the MSI and
    was unreachable, and the same mistake had shipped for several releases in a sibling project before anyone
    checked the ControlEvent table.

    What makes these worth asserting rather than eyeballing is that all of them fail SILENTLY. An unreachable
    dialog, a checkbox whose property is pre-set so it renders ticked, a custom action sequenced before the
    files it needs -- none of these break the build, and none of them are visible without either reading the
    tables or performing an interactive install.

    Runs against the validation copy inside build-installer.ps1, beside Test-MsiUpgradeSchedule.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsiPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$absoluteMsi = [IO.Path]::GetFullPath($MsiPath)
if (-not (Test-Path -LiteralPath $absoluteMsi -PathType Leaf)) {
    throw "No such MSI: $absoluteMsi"
}

$failures = New-Object 'Collections.Generic.List[string]'
function Assert-MsiTrue {
    param([bool]$Condition, [string]$Because)
    if ($Condition) { Write-Host ("  ok   " + $Because) }
    else { Write-Host ("  FAIL " + $Because) -ForegroundColor Red; $failures.Add($Because) }
}

# One reader for every query. The COM objects are released in reverse order because an un-released view keeps
# the database handle open, which makes the caller's later atomic publish fail with a sharing violation.
function Get-MsiRows {
    param([Parameter(Mandatory = $true)][string]$Query, [int]$Columns = 1)

    $installer = $null; $database = $null; $view = $null
    $rows = New-Object 'Collections.Generic.List[object]'
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($absoluteMsi, 0)
        $view = $database.OpenView($Query)
        [void]$view.Execute()
        while ($true) {
            $record = $view.Fetch()
            if ($null -eq $record) { break }
            $values = @()
            for ($i = 1; $i -le $Columns; $i++) {
                # StringData works for integer columns too, which keeps one code path for both.
                $values += [string]$record.StringData($i)
            }
            $rows.Add($values)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        }
        # `,` on purpose: PowerShell unrolls a returned collection, so a no-row result would come back as
        # $null and a one-row result as that row's own values -- both of which make .Count lie about how many
        # rows matched, which is exactly what most of these assertions turn on.
        return ,$rows.ToArray()
    }
    finally {
        foreach ($value in @($view, $database, $installer)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
            }
        }
    }
}

Write-Host 'MSI surface:' -ForegroundColor Cyan

# ---- the "clear all settings and modules" page -------------------------------------------------------
# Reachability is the whole point. A dialog present in the Dialog table but absent from InstallUISequence
# (or chained off a button whose EndDialog fires first) is authored, shipped and never seen.
$resetShow = Get-MsiRows "SELECT ``Sequence``,``Condition`` FROM ``InstallUISequence`` WHERE ``Action`` = 'DesktopAICompanionResetDlg'" 2
Assert-MsiTrue ($resetShow.Count -eq 1) 'the reset page is scheduled exactly once in InstallUISequence'
if ($resetShow.Count -eq 1) {
    $resetSequence = [int]$resetShow[0][0]
    Assert-MsiTrue ($resetSequence -gt 1200 -and $resetSequence -lt 1295) (
        "the reset page runs before the stock welcome dialogs (sequence $resetSequence, must be 1201-1294)")
    Assert-MsiTrue ($resetShow[0][1] -eq 'NOT Installed') 'the reset page is shown on install, not on maintenance'
}

# It must NOT also be chained off a button: that was the original unreachable authoring, and leaving a dead
# row behind would make a future reader think the sequence entry is redundant.
$deadChain = Get-MsiRows "SELECT ``Dialog_`` FROM ``ControlEvent`` WHERE ``Argument`` = 'DesktopAICompanionResetDlg'" 1
Assert-MsiTrue ($deadChain.Count -eq 0) 'no leftover ControlEvent tries to reach the reset page from a button'

$checkBox = Get-MsiRows "SELECT ``Property``,``Type`` FROM ``Control`` WHERE ``Dialog_`` = 'DesktopAICompanionResetDlg' AND ``Type`` = 'CheckBox'" 2
Assert-MsiTrue ($checkBox.Count -eq 1 -and $checkBox[0][0] -eq 'CLEANINSTALL') 'the page carries one checkbox bound to CLEANINSTALL'

# An MSI checkbox renders TICKED whenever its property holds any non-empty value, so a destructive option
# with a default of "0" would arm itself. The only safe default is no Property row at all.
$cleanDefault = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'CLEANINSTALL'" 1
Assert-MsiTrue ($cleanDefault.Count -eq 0) 'CLEANINSTALL has no default value, so the destructive box starts unticked'

# ---- the wipe action ----------------------------------------------------------------------------------
$wipe = Get-MsiRows "SELECT ``Type``,``Source``,``Target`` FROM ``CustomAction`` WHERE ``Action`` = 'ClearUserDataOnInstall'" 3
Assert-MsiTrue ($wipe.Count -eq 1) 'the wipe action exists'
if ($wipe.Count -eq 1) {
    # 18 exe-from-installed-file + 1024 deferred + 64 continue-on-error. Crucially NOT 2048 (no-impersonate):
    # this is a perUser package and the data lives in the installing user's profile.
    Assert-MsiTrue ([int]$wipe[0][0] -eq 1106) ("the wipe action is deferred, impersonated and non-fatal (type " + $wipe[0][0] + ", expected 1106)")
    Assert-MsiTrue ($wipe[0][2] -eq '--factory-reset') 'the wipe action invokes the app rather than deleting paths itself'
}
$wipeSeq = Get-MsiRows "SELECT ``Sequence``,``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'ClearUserDataOnInstall'" 2
$filesSeq = Get-MsiRows "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'InstallFiles'" 1
Assert-MsiTrue ($wipeSeq.Count -eq 1 -and $filesSeq.Count -eq 1 -and [int]$wipeSeq[0][0] -gt [int]$filesSeq[0][0]) (
    'the wipe runs AFTER InstallFiles, so the exe it calls is the newly installed one')
Assert-MsiTrue ($wipeSeq.Count -eq 1 -and $wipeSeq[0][1] -match 'CLEANINSTALL' -and $wipeSeq[0][1] -match 'NOT REMOVE') (
    'the wipe is conditioned on the checkbox and skipped on uninstall')

# ---- closing a running instance -------------------------------------------------------------------------
# Without this the installer stops on "unable to automatically close all requested applications", and worse,
# Restart Manager closes the windows it can reach while the process survives: pets on screen, no tray icon.
$close = Get-MsiRows "SELECT ``Target``,``Attributes`` FROM ``Wix4CloseApplication``" 2
Assert-MsiTrue ($close.Count -eq 1 -and $close[0][0] -eq 'DesktopAICompanion.exe') 'the installer closes a running DesktopAICompanion.exe'
if ($close.Count -eq 1) {
    $attributes = [int]$close[0][1]
    Assert-MsiTrue (($attributes -band 1) -ne 0) 'it asks the app to close first (CloseMessage)'
    # The fallback is the point: the app demonstrably does not answer a close request once modules are loaded.
    Assert-MsiTrue (($attributes -band 32) -ne 0) 'it terminates the process if asking does not work (TerminateProcess)'
}

# ---- launch on finish -----------------------------------------------------------------------------------
# The inverse of the CLEANINSTALL rule: here a non-empty property is what makes the box render TICKED, and
# ticked is the wanted default. Assert the value, because losing it silently un-ticks the box.
$launchDefault = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'WIXUI_EXITDIALOGOPTIONALCHECKBOX'" 1
Assert-MsiTrue ($launchDefault.Count -eq 1 -and $launchDefault[0][0] -ne '') 'the launch-on-finish box is ticked by default'
$launchText = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'" 1
Assert-MsiTrue ($launchText.Count -eq 1 -and $launchText[0][0] -like 'Launch*') 'the launch box is captioned, so it is not an unlabelled tick'
$launchAction = Get-MsiRows "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = 'LaunchDesktopAICompanion'" 1
# 18 exe-from-installed-file + 192 asyncNoWait. Must NOT be deferred (1024): Finish is clicked after
# InstallFinalize, when there is no install script left to defer into.
Assert-MsiTrue ($launchAction.Count -eq 1 -and ([int]$launchAction[0][0] -band 1024) -eq 0) (
    'the launch action is immediate, because Finish happens after the install script has run')
$launchPublish = Get-MsiRows "SELECT ``Condition`` FROM ``ControlEvent`` WHERE ``Dialog_`` = 'ExitDialog' AND ``Argument`` = 'LaunchDesktopAICompanion'" 1
Assert-MsiTrue ($launchPublish.Count -eq 1 -and $launchPublish[0][0] -match 'WIXUI_EXITDIALOGOPTIONALCHECKBOX') (
    'launching is conditioned on the checkbox, so unticking it means what it says')

# ---- repair must be offered, and must actually repair -------------------------------------------------
# Two halves, and offering it WITHOUT the second is worse than not offering it at all: the stock maintenance
# dialog would enable a Repair button that, under the default omus, replaces only files that are missing or
# older. A file corrupted in place at the same version survives, and the user is told the repair succeeded.
$noRepair = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ARPNOREPAIR'" 1
Assert-MsiTrue ($noRepair.Count -eq 0) 'repair is offered rather than greyed out'
$reinstallMode = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'REINSTALLMODE'" 1
Assert-MsiTrue ($reinstallMode.Count -eq 1 -and $reinstallMode[0][0] -like '*a*') (
    'a repair force-copies every file, not just missing or older ones')

# ---- the reset page must not look like a different application ---------------------------------------
# PrepareDlg (sequence 49) shows while costing runs and is replaced by the next sequenced dialog. Every stock
# dialog shares WixUI_Bmp_Dialog, so that hand-off is invisible; a page with different chrome made it read as
# a dialog flashing up and vanishing.
$resetBitmap = Get-MsiRows "SELECT ``Text`` FROM ``Control`` WHERE ``Dialog_`` = 'DesktopAICompanionResetDlg' AND ``Type`` = 'Bitmap'" 1
Assert-MsiTrue ($resetBitmap.Count -eq 1 -and $resetBitmap[0][0] -eq 'WixUI_Bmp_Dialog') (
    'the reset page uses the same background as the stock dialogs, so the hand-off from PrepareDlg is seamless')

if ($failures.Count -gt 0) {
    throw ("MSI surface assertions failed: " + ($failures -join '; '))
}
Write-Host 'MSI surface OK.' -ForegroundColor Green