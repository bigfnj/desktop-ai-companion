#requires -Version 5.1
#
# GUI smoke test: the tray menu opens, and "Synchronise companions" obeys its condition -- ABSENT with
# one pet, PRESENT with two -- and invoking it does not kill the app. The second pet is added through
# the app's own "Add a companion" submenu, so nothing about the pet count is faked.
#
# HERMETIC. The pet count lives in settings.json and survives the process, so earlier versions of this
# script kept failing on their own leftovers: one assumed it would start at one pet and the run before
# had left two; the next left three behind. AppPaths documents
# DESKTOP_AI_COMPANION_DATA_ROOT for exactly this, so each run gets a fresh data root, starts from the
# default single pet, and touches neither the build tree's data\ nor an installed copy's
# %LOCALAPPDATA%.
#
# NOT wired into tests/run-gate.ps1: it needs an interactive desktop session and UI Automation. Run it
# by hand after touching ContextMenus.cs or the pet lifecycle:
#
#     powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\tray-menu-smoke.ps1
#
# Exit 0 = correct in both states. 1 = wrong in one of them, or the app died. 4 = could not drive it.
#
# NotifyIcon routes tray mouse input through a hidden NativeWindow of our own process as
# WM_TRAYMOUSEMESSAGE (WM_USER + 1024), lParam = the mouse message. Posting that is how the menu is
# opened here without hunting the icon's rectangle inside explorer's toolbar.
#
# Measured 2026-09-25: one pet -> 18 items and no sync entry; two pets -> 19 items, entry present.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -Namespace Tray -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
'@ -UsingNamespace System.Text

$WM_TRAYMOUSEMESSAGE = 0x0400 + 1024
$WM_RBUTTONUP = 0x0205
$SYNC = 'ynchronise companions'

function Get-HiddenWindows([int]$ProcessId) {
    $found = New-Object System.Collections.ArrayList
    $cb = [Tray.Native+EnumWindowsProc] {
        param($h, $l)
        $wpid = 0
        [void][Tray.Native]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq $ProcessId -and -not [Tray.Native]::IsWindowVisible($h)) { [void]$found.Add($h) }
        return $true
    }
    [void][Tray.Native]::EnumWindows($cb, [IntPtr]::Zero)
    return $found
}

# Pets are the visible windows titled "Sheep". Counting windows rather than reading settings keeps
# this honest about what is actually on screen.
function Get-PetCount([int]$ProcessId) {
    $script:petTally = 0
    $cb = [Tray.Native+EnumWindowsProc] {
        param($h, $l)
        $wpid = 0
        [void][Tray.Native]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq $ProcessId -and [Tray.Native]::IsWindowVisible($h)) {
            $t = New-Object System.Text.StringBuilder 256
            [void][Tray.Native]::GetWindowText($h, $t, 256)
            if ($t.ToString() -eq 'Sheep') { $script:petTally++ }
        }
        return $true
    }
    [void][Tray.Native]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:petTally
}

function Open-TrayMenu([int]$ProcessId) {
    foreach ($h in (Get-HiddenWindows $ProcessId)) {
        [void][Tray.Native]::PostMessage($h, $WM_TRAYMOUSEMESSAGE, [IntPtr]1, [IntPtr]$WM_RBUTTONUP)
    }
    Start-Sleep -Milliseconds 1500
}

function Get-MenuItems([int]$ProcessId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $menuCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)
    $items = New-Object System.Collections.ArrayList
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        foreach ($f in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
            [void]$items.Add($f)
        }
    }
    return $items
}

function Close-Menus { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 900 }

# Expands a built-on-open submenu and invokes its first real child. The children do not exist at the
# instant Expand() returns -- a DropDownOpening handler builds them -- so this polls rather than
# sleeping a fixed amount, which is what made an earlier version pass one run and throw the next
# against an identical build.
function Invoke-SubmenuChild([int]$ProcessId, [string]$Parent, [string]$Skip) {
    $ellipsis = [string][char]0x2026
    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $parentItem = Get-MenuItems $ProcessId | Where-Object { $_.Current.Name -eq $Parent } | Select-Object -First 1
        if ($parentItem) {
            $parentItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            for ($waited = 0; $waited -lt 6000; $waited += 300) {
                Start-Sleep -Milliseconds 300
                $child = $parentItem.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond) |
                    Where-Object { $_.Current.Name -and $_.Current.Name -ne $ellipsis -and $_.Current.Name -notmatch $Skip } |
                    Select-Object -First 1
                if ($child) {
                    $name = $child.Current.Name
                    $child.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    return $name
                }
            }
        }
        Write-Host "  (attempt $attempt found nothing under '$Parent'; reopening)"
        Close-Menus; Open-TrayMenu $ProcessId
    }
    throw "no usable entry under '$Parent' after 3 attempts"
}

# Waits for the pet count to reach $Want rather than sleeping a guess: staging and spawning a
# companion is asynchronous, and a fixed wait reported "the second pet never appeared" on a run where
# it appeared a second later.
function Wait-PetCount([int]$ProcessId, [int]$Want, [int]$TimeoutMs = 20000) {
    for ($waited = 0; $waited -lt $TimeoutMs; $waited += 500) {
        if ((Get-PetCount $ProcessId) -ge $Want) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Get-Process -Name DesktopAICompanion -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*\build\*' } |
    ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }

$dataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dac-tray-smoke-" + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $dataRoot -Force)
$previousRoot = $env:DESKTOP_AI_COMPANION_DATA_ROOT
$env:DESKTOP_AI_COMPANION_DATA_ROOT = $dataRoot
Write-Host "isolated data root: $dataRoot"

$proc = $null
$code = 4
try {
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    $one = Get-PetCount $proc.Id
    Write-Host "pets at launch: $one"
    if ($one -ne 1) { throw "expected exactly 1 pet in a fresh data root, saw $one" }

    Open-TrayMenu $proc.Id
    $itemsOne = @(Get-MenuItems $proc.Id | ForEach-Object { $_.Current.Name })
    $syncAtOne = [bool]($itemsOne | Where-Object { $_ -match $SYNC })
    Write-Host "with 1 pet  -- $($itemsOne.Count) items, sync present: $syncAtOne"

    $addedName = Invoke-SubmenuChild $proc.Id 'Add a companion' 'nothing|none'
    $arrived = Wait-PetCount $proc.Id 2
    Close-Menus
    $two = Get-PetCount $proc.Id
    Write-Host "added '$addedName'; pets now: $two (second arrived: $arrived)"

    Open-TrayMenu $proc.Id
    $itemsTwo = @(Get-MenuItems $proc.Id | ForEach-Object { $_.Current.Name })
    $syncAtTwo = [bool]($itemsTwo | Where-Object { $_ -match $SYNC })
    Write-Host "with 2 pets -- $($itemsTwo.Count) items, sync present: $syncAtTwo"

    $invoked = $false
    if ($syncAtTwo) {
        $item = Get-MenuItems $proc.Id | Where-Object { $_.Current.Name -match $SYNC } | Select-Object -First 1
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
        $invoked = $true
    }
    Close-Menus

    Write-Host ''
    Write-Host "hidden at one pet : $(-not $syncAtOne)"
    Write-Host "shown at two pets : $syncAtTwo"
    Write-Host "invoked it        : $invoked"
    Write-Host "app alive after   : $(-not $proc.HasExited)"

    if (-not $arrived)       { Write-Host 'FAIL: the second pet never appeared' -ForegroundColor Red; $code = 1 }
    elseif ($syncAtOne)      { Write-Host 'FAIL: the item showed with a single pet' -ForegroundColor Red; $code = 1 }
    elseif (-not $syncAtTwo) { Write-Host 'FAIL: the item did not appear with two pets' -ForegroundColor Red; $code = 1 }
    elseif ($proc.HasExited) { Write-Host 'FAIL: invoking it killed the app' -ForegroundColor Red; $code = 1 }
    else { Write-Host 'PASS: hidden at one pet, shown at two, invoked without incident' -ForegroundColor Green; $code = 0 }
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $code = 4
} finally {
    if ($proc) { try { $proc.Kill(); [void]$proc.WaitForExit(8000) } catch { } }
    Get-Process -Name DesktopAICompanion -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like '*\build\*' } |
        ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $previousRoot
    try { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
exit $code
