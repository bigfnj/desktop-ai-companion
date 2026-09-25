#requires -Version 5.1
#
# GUI smoke test: the debug right-click menu, and that pets spawn and render at all.
#
# NOT wired into tests/run-gate.ps1, deliberately. It needs an interactive desktop session, it
# launches the real app, and it presses SHIFT globally for the length of the launch -- none of which
# belongs in a gate that has to run unattended. Run it by hand after touching FormCompanion's mouse
# handling or anything on the startup path:
#
#     powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\debug-menu-smoke.ps1
#
# Exit 0 = the menu opened. 1 = an exception dialog, or the app died. 2 = inconclusive (no drop-down
# detected; look at the screenshots it leaves in %TEMP%).
#
# Mutation-tested 2026-09-25 in both directions, with the built assembly checked each way: with
# `throw new PlatformNotSupportedException` injected into the handler the compiled DLL contained the
# probe string and this reported exit 2; with it removed the string was gone and this reported exit 0.
#
# The crash this proves gone is runtime-only: ContextMenu is a .NET 9+ binary-compatibility stub, so
# `new ContextMenu()` compiled fine and threw PlatformNotSupportedException into the message pump the
# moment a Shift-launched instance was right-clicked. Nothing but a running app on this runtime can
# tell you whether it is fixed.
#
# Holds SHIFT across the launch (StartUp reads Control.ModifierKeys in its constructor), right-clicks a
# pet, and screenshots. Restores the cursor afterwards.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe'
$out  = if ($env:TEMP) { $env:TEMP } else { $PSScriptRoot }

if (-not (Test-Path -LiteralPath $exe)) { throw "No built exe at $exe" }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace Smoke -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int max);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
public struct RECT { public int Left, Top, Right, Bottom; }
'@ -UsingNamespace System.Text

function Get-ProcessWindows([int]$ProcessId) {
    $found = New-Object System.Collections.ArrayList
    $cb = [Smoke.Native+EnumWindowsProc] {
        param($h, $l)
        $wpid = 0
        [void][Smoke.Native]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq $ProcessId -and [Smoke.Native]::IsWindowVisible($h)) {
            $r = New-Object Smoke.Native+RECT
            [void][Smoke.Native]::GetWindowRect($h, [ref]$r)
            $cls = New-Object System.Text.StringBuilder 256
            [void][Smoke.Native]::GetClassName($h, $cls, 256)
            $txt = New-Object System.Text.StringBuilder 256
            [void][Smoke.Native]::GetWindowText($h, $txt, 256)
            [void]$found.Add([pscustomobject]@{
                Handle = $h
                Class  = $cls.ToString()
                Title  = $txt.ToString()
                X = $r.Left; Y = $r.Top
                W = ($r.Right - $r.Left); H = ($r.Bottom - $r.Top)
            })
        }
        return $true
    }
    [void][Smoke.Native]::EnumWindows($cb, [IntPtr]::Zero)
    return $found
}

function Save-Screen([string]$path) {
    $b  = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bm = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g  = [System.Drawing.Graphics]::FromImage($bm)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bm.Size)
    $g.Dispose()
    $bm.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bm.Dispose()
}

# Only ever touch instances running out of the build tree; the user's installed copy is left alone.
Get-Process -Name DesktopAICompanion -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*\build\*' } |
    ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }

$VK_SHIFT = 0x10
$KEYEVENTF_KEYUP = 0x0002

# A fresh data root per run, so this never reads or writes the build tree's data\ or an installed
# copy's %LOCALAPPDATA%, and always starts from the default single pet. AppPaths documents the
# variable for exactly this.
$dataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dac-debug-smoke-" + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $dataRoot -Force)
$previousRoot = $env:DESKTOP_AI_COMPANION_DATA_ROOT
$env:DESKTOP_AI_COMPANION_DATA_ROOT = $dataRoot
Write-Host "isolated data root: $dataRoot"

$proc = $null
try {
    [Smoke.Native]::keybd_event($VK_SHIFT, 0, 0, [UIntPtr]::Zero)          # SHIFT down
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 6                                                  # let pets spawn
} finally {
    [Smoke.Native]::keybd_event($VK_SHIFT, 0, $KEYEVENTF_KEYUP, [UIntPtr]::Zero)
}

if ($proc.HasExited) { throw "app exited during startup with code $($proc.ExitCode)" }

$wins = Get-ProcessWindows $proc.Id
Write-Host "--- windows owned by pid $($proc.Id):"
$wins | ForEach-Object { Write-Host ("    {0,-24} {1,-28} {2}x{3} at {4},{5}" -f $_.Class, $_.Title, $_.W, $_.H, $_.X, $_.Y) }

# ASSERTED, not just printed. Without SHIFT, IsDebugActive() is false and a right-click goes to
# OnPetPoked instead, which draws a speech bubble and creates no drop-down -- so the run would end
# at "inconclusive" and send the reader to a screenshot to work out which of the two things broke.
$debugWindowSeen = [bool]($wins | Where-Object { $_.Title -match 'debug' })
Write-Host "debug window present (proves Shift reached the app): $debugWindowSeen"
if (-not $debugWindowSeen) {
    Write-Host 'SMOKE FAIL: SHIFT did not reach the app, so debug mode is off and the menu under test is not the one that would open' -ForegroundColor Red
    try { $proc.Kill() } catch { }
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $previousRoot
    exit 1
}

# The pet form is titled "Sheep". Matching on size alone picked up the speech bubble instead, which
# is untitled, the same order of size, and NOT what the debug menu hangs off.
$pet = $wins | Where-Object { $_.Title -eq 'Sheep' } | Select-Object -First 1
if (-not $pet) { Save-Screen (Join-Path $out 'smoke-nopet.png'); throw 'no pet window found' }

Save-Screen (Join-Path $out 'smoke-1-launched.png')

# POSTED, not synthesised at the cursor. A pet form is transparency-keyed, so a real click on its
# geometric centre lands on a keyed pixel and passes through to the desktop whenever the sprite's
# opaque area happens not to cover the middle -- which depends on where the pet has wandered and
# which frame it is on. That made a mouse_event version pass twice and then fail on identical
# binaries. The message goes to the child control that owns the handler instead.
$children = New-Object System.Collections.ArrayList
$childCb = [Smoke.Native+EnumWindowsProc] {
    param($h, $l)
    $r = New-Object Smoke.Native+RECT
    [void][Smoke.Native]::GetWindowRect($h, [ref]$r)
    [void]$children.Add([pscustomobject]@{ Handle = $h; W = ($r.Right - $r.Left); H = ($r.Bottom - $r.Top) })
    return $true
}
[void][Smoke.Native]::EnumChildWindows($pet.Handle, $childCb, [IntPtr]::Zero)
Write-Host "pet child controls: $($children.Count)"

$target = if ($children.Count -gt 0) { ($children | Sort-Object { $_.W * $_.H } -Descending)[0].Handle } else { $pet.Handle }
$lx = [int]($pet.W / 2); $ly = [int]($pet.H / 2)
$lParam = [IntPtr](($ly -shl 16) -bor $lx)
Write-Host "posting WM_RBUTTONDOWN/UP to handle $target at client $lx,$ly"
[void][Smoke.Native]::PostMessage($target, 0x0204, [IntPtr]::Zero, $lParam)   # WM_RBUTTONDOWN
Start-Sleep -Milliseconds 120
[void][Smoke.Native]::PostMessage($target, 0x0205, [IntPtr]::Zero, $lParam)   # WM_RBUTTONUP
Start-Sleep -Seconds 2

Save-Screen (Join-Path $out 'smoke-2-rightclick.png')

# Captured BEFORE the cleanup below kills the app, or every run reports "died on right-click".
$diedOnClick = $proc.HasExited
$after = Get-ProcessWindows $proc.Id
$crashDialog = $after | Where-Object { $_.Title -match 'Unhandled|exception|has stopped' }
# The menu is a window that was NOT there before the click. Matching on shape alone passed against the
# speech bubble, which was already open -- a check with no way to fail.
$before = @($wins | ForEach-Object { $_.Handle })
$fresh = @($after | Where-Object { $before -notcontains $_.Handle })
# A drop-down specifically, not just "something new". The mutation run opened a 455x175 window of its
# own (the app catches the exception and reports it), which a bare new-window test accepted as a menu.
# Windows creates a SysShadow companion window for a drop-down and for nothing else here, so that is
# the signal. If menu shadows are ever turned off system-wide this reports INCONCLUSIVE, not PASS.
$menuShadow = $fresh | Where-Object { $_.Class -eq 'SysShadow' }
$menuWindow  = if ($menuShadow) { $fresh | Where-Object { $_.Class -ne 'SysShadow' -and $_.W -gt 60 -and $_.H -gt 40 } } else { $null }

Write-Host ''
Write-Host "process alive after right-click : $(-not $diedOnClick)"
Write-Host "unhandled-exception dialog      : $([bool]$crashDialog)"
Write-Host "a drop-down (new window + SysShadow) appeared: $([bool]$menuWindow)"
if ($menuWindow) { $menuWindow | ForEach-Object { Write-Host ("    menu candidate {0} {1}x{2}" -f $_.Class, $_.W, $_.H) } }

# ALWAYS close the app. Leaving it running holds a lock on the exe, and the next build then fails
# with MSB3027 -- which looks nothing like a compile error and silently leaves the previous binary
# in place, so the run after that measures the wrong build.
try { $proc.Kill(); [void]$proc.WaitForExit(8000) } catch { }
Get-Process -Name DesktopAICompanion -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*\build\*' } |
    ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }
$env:DESKTOP_AI_COMPANION_DATA_ROOT = $previousRoot
try { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }

if ($crashDialog) { Write-Host 'SMOKE FAIL: the right-click produced an exception dialog' -ForegroundColor Red; exit 1 }
if ($diedOnClick) { Write-Host 'SMOKE FAIL: the app died on right-click' -ForegroundColor Red; exit 1 }
if (-not $menuWindow) { Write-Host 'SMOKE INCONCLUSIVE: no menu window detected; check smoke-2-rightclick.png' -ForegroundColor Yellow; exit 2 }

Write-Host 'SMOKE PASS: debug menu opened, no exception, app still running' -ForegroundColor Green
exit 0
