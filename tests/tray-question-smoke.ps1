#requires -Version 5.1
#
# GUI smoke test: the question card by the clock (app 1.6.0, lane feature/tray-questions), end to end in the real app.
#
# NOT wired into tests/run-gate.ps1, for the reasons debug-menu-smoke.ps1 gives: it needs an interactive desktop, it
# launches the real app, it holds SHIFT across the launch, it clicks the mouse once and types into a window of its own.
# Run it by hand after touching TrayQuestions.cs, TrayAnchor.cs, Wpf\TrayQuestionCard.cs or the tray menu:
#
#     powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\tray-question-smoke.ps1
#
# Exit 0 = every check passed. 1 = a check failed, or the app died. 3 = nothing failed, but the checks that need
# synthetic keyboard and mouse input did not run, because this session drops it (they are listed as NOT RUN).
# 4 = could not drive it (no debug window, no card).
#
# The debug window is opened with DESKTOP_AI_COMPANION_DEBUG_WINDOW=1 rather than by holding SHIFT: a synthesised SHIFT
# was released before a slow first start read the keys, and an agent's shell on the owner's machine drops synthetic
# input altogether (measured 2026-10-15). Everything but the typing and click checks goes through UI Automation and
# window messages, which need no synthetic input.
#
# It runs a COPY of the build WITHOUT its modules folder against a fresh data root, so no module starts (Blinking LED
# would press Scroll Lock) and nothing of the user's is read or written; the questions come from the debug window's
# Tools menu (FormDebug), which asks them under its own module id with a sentinel in the text. What it proves:
#   1. the card appears, in a corner of its monitor's work area, styled never to activate and excluded from capture;
#   2. while it is up, keys typed into another window still land there (the card took no focus);
#   3. a press answers (Keep), a click on the card's body then Enter answers the focused no-answer choice (Drop), and
#      an unanswered card times out -- each recorded in the diagnostics log by key, outcome and choice id;
#   4. the tray menu then lists it under "No answer (n)";
#   5. the sentinel text never reaches the diagnostics log.
# The card cannot be screenshotted -- capture exclusion is the point -- so --trayquestions-selftest renders its
# contents to %TEMP%\dp-trayquestions-card-*.png for a visual check instead.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo 'build\DesktopAICompanionPortable\bin\Release\x64'
if (-not (Test-Path -LiteralPath (Join-Path $build 'DesktopAICompanion.exe'))) { throw "No built exe under $build" }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -Namespace QSmoke -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
[DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr hWnd, int index);
[DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@ -UsingNamespace System.Text

$SentinelFragment = 'sentinel 7f3a91'
$CardTitle = 'Question from Debug window'
$WM_TRAYMOUSEMESSAGE = 0x0400 + 1024
$WM_RBUTTONUP = 0x0205

function Get-Windows([int]$ProcessId, [bool]$Visible) {
    $found = New-Object System.Collections.ArrayList
    $cb = [QSmoke.Native+EnumWindowsProc] {
        param($h, $l)
        $wpid = 0
        [void][QSmoke.Native]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq $ProcessId -and ([QSmoke.Native]::IsWindowVisible($h) -eq $Visible)) {
            $t = New-Object System.Text.StringBuilder 256
            [void][QSmoke.Native]::GetWindowText($h, $t, 256)
            $r = New-Object QSmoke.Native+RECT
            [void][QSmoke.Native]::GetWindowRect($h, [ref]$r)
            [void]$found.Add([pscustomobject]@{ Handle = $h; Title = $t.ToString(); Rect = $r })
        }
        return $true
    }
    [void][QSmoke.Native]::EnumWindows($cb, [IntPtr]::Zero)
    return $found
}

function Wait-Card([int]$ProcessId, [bool]$Present, [int]$Seconds) {
    for ($i = 0; $i -lt ($Seconds * 4); $i++) {
        $card = Get-Windows $ProcessId $true | Where-Object { $_.Title -eq $CardTitle } | Select-Object -First 1
        if ($Present -and $card) { return $card }
        if (-not $Present -and -not $card) { return $true }
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-Element([int]$ProcessId, [string]$Name, $ControlType) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byProcess = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $byType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    $match = New-Object System.Windows.Automation.AndCondition($byName, $byType)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        $hit = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $match)
        if ($hit) { return $hit }
    }
    return $null
}

# The debug window's Tools menu -> one of the two "Ask a test question" items.
function Ask-TestQuestion([int]$ProcessId, [string]$Item) {
    $tools = Find-Element $ProcessId 'Tools' ([System.Windows.Automation.ControlType]::MenuItem)
    if (-not $tools) { throw 'the debug window has no Tools menu' }
    $tools.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    for ($waited = 0; $waited -lt 4000; $waited += 250) {
        Start-Sleep -Milliseconds 250
        $ask = Find-Element $ProcessId $Item ([System.Windows.Automation.ControlType]::MenuItem)
        if ($ask) { $ask.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return }
    }
    throw "no '$Item' item under Tools"
}

$checks = New-Object System.Collections.ArrayList
$notRun = New-Object System.Collections.ArrayList
function NotRun([string]$Name) {
    [void]$notRun.Add($Name)
    Write-Host ('NOT RUN: ' + $Name + ' (this session drops synthetic input)') -ForegroundColor Yellow
}
function Check([string]$Name, [bool]$Condition) {
    [void]$checks.Add([pscustomobject]@{ Name = $Name; Pass = $Condition })
    $colour = if ($Condition) { 'Green' } else { 'Red' }
    Write-Host ($(if ($Condition) { 'PASS: ' } else { 'FAIL: ' }) + $Name) -ForegroundColor $colour
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('dac-question-smoke-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $work 'app'
$dataRoot = Join-Path $work 'data'
$previousRoot = $env:DESKTOP_AI_COMPANION_DATA_ROOT
$proc = $null
$probe = $null
$code = 1
$cursor = New-Object QSmoke.Native+POINT
[void][QSmoke.Native]::GetCursorPos([ref]$cursor)
try {
    # The app without its modules: every file beside the exe, nothing under modules\.
    [void](New-Item -ItemType Directory -Path $app -Force)
    [void](New-Item -ItemType Directory -Path $dataRoot -Force)
    Get-ChildItem -LiteralPath $build -Force | Where-Object { $_.Name -ne 'modules' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $app -Recurse -Force }
    [void](New-Item -ItemType Directory -Path (Join-Path $app 'modules') -Force)
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $dataRoot
    Write-Host "app copy (no modules): $app"
    Write-Host "isolated data root:    $dataRoot"

    # Does synthetic input reach this desktop at all? Pressed and read back once, before anything depends on it.
    [QSmoke.Native]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    $canInject = ([QSmoke.Native]::GetAsyncKeyState(0x10) -band 0x8000) -ne 0
    [QSmoke.Native]::keybd_event(0x10, 0, 0x0002, [UIntPtr]::Zero)
    Write-Host "synthetic input reaches this desktop: $canInject"

    $previousDebug = $env:DESKTOP_AI_COMPANION_DEBUG_WINDOW
    $env:DESKTOP_AI_COMPANION_DEBUG_WINDOW = '1'
    try { $proc = Start-Process -FilePath (Join-Path $app 'DesktopAICompanion.exe') -PassThru }
    finally { $env:DESKTOP_AI_COMPANION_DEBUG_WINDOW = $previousDebug }
    # A freshly copied app folder starts slowly while the endpoint scanners inspect every new DLL: wait for the window.
    for ($waited = 0; $waited -lt 40000; $waited += 250) {
        Start-Sleep -Milliseconds 250
        if ($proc.HasExited) { break }
        if (Get-Windows $proc.Id $true | Where-Object { $_.Title -match 'debug' }) { break }
    }
    Start-Sleep -Seconds 3                                                  # let the rest of startup settle
    if ($proc.HasExited) { throw "the app exited during startup with code $($proc.ExitCode)" }
    if (-not (Get-Windows $proc.Id $true | Where-Object { $_.Title -match 'debug' })) {
        throw [System.InvalidOperationException]::new('CANNOT DRIVE: no debug window appeared, so there is nothing to ask from')
    }

    # ---- 1 + 2: shown, placed, styled, and it takes no keystrokes ----
    Ask-TestQuestion $proc.Id 'Ask a test question'
    $card = Wait-Card $proc.Id $true 5
    if (-not $card) { throw [System.InvalidOperationException]::new('CANNOT DRIVE: no question card appeared') }
    $ex = [QSmoke.Native]::GetWindowLong($card.Handle, -20)
    Check 'card: shown with WS_EX_NOACTIVATE and WS_EX_TOOLWINDOW' ((($ex -band 0x08000000) -ne 0) -and (($ex -band 0x80) -ne 0))
    $affinity = [uint32]0
    [void][QSmoke.Native]::GetWindowDisplayAffinity($card.Handle, [ref]$affinity)
    Check ("card: excluded from screen capture (affinity 0x{0:x})" -f $affinity) ($affinity -eq 0x11 -or $affinity -eq 0x1)
    $wa = [System.Windows.Forms.Screen]::FromHandle($card.Handle).WorkingArea
    $r = $card.Rect
    $inside = $r.Left -ge $wa.Left -and $r.Top -ge $wa.Top -and $r.Right -le $wa.Right -and $r.Bottom -le $wa.Bottom
    $nearX = [Math]::Min([Math]::Abs($r.Left - $wa.Left), [Math]::Abs($wa.Right - $r.Right))
    $nearY = [Math]::Min([Math]::Abs($r.Top - $wa.Top), [Math]::Abs($wa.Bottom - $r.Bottom))
    Check ("card: inside its monitor's work area and in a corner ({0},{1} {2}x{3}; corner gaps {4},{5})" -f $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top), $nearX, $nearY) ($inside -and $nearX -le 48 -and $nearY -le 48)

    Check 'card: appearing did not make it the foreground window' ([QSmoke.Native]::GetForegroundWindow() -ne $card.Handle)
    if ($canInject) {
        # A window of our own with a text box: "the user typing". Opened only now, AFTER the menu was used: shown
        # earlier, its focus change closed the debug window's Tools drop-down before the item could be invoked.
        $probe = New-Object System.Windows.Forms.Form
        $probe.Text = 'question smoke probe'
        $probe.StartPosition = 'Manual'
        $probe.Location = New-Object System.Drawing.Point(80, 80)
        $box = New-Object System.Windows.Forms.TextBox
        $box.Dock = 'Fill'
        $probe.Controls.Add($box)
        $probe.Show()
        [System.Windows.Forms.Application]::DoEvents()
        $probe.Activate()
        [void][QSmoke.Native]::SetForegroundWindow($probe.Handle)
        $box.Focus() | Out-Null
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait('abc')
        [System.Windows.Forms.Application]::DoEvents()
        Check 'card: with it on screen, the probe window keeps the foreground' ([QSmoke.Native]::GetForegroundWindow() -eq $probe.Handle)
        Check ("card: keys typed while it is up land in the probe (got '{0}')" -f $box.Text) ($box.Text -eq 'abc')
    }
    else { NotRun 'card: keys typed while it is up land in another window' }
    Check 'card: still on screen' ([bool](Wait-Card $proc.Id $true 1))

    # ---- 3a: a press answers ----
    Start-Sleep -Milliseconds 700                                            # past the 500 ms click guard
    $keep = Find-Element $proc.Id 'Keep' ([System.Windows.Automation.ControlType]::Button)
    Check 'card: its buttons are found by name (UI Automation)' ([bool]$keep)
    if ($keep) { $keep.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Check 'card: pressing Keep takes it down' ([bool](Wait-Card $proc.Id $false 5))

    # ---- 3b: click the body, then Enter answers the focused no-answer choice ----
    if (-not $canInject) {
        NotRun 'card: clicking its body gives it the keyboard, and Enter answers the focused no-answer choice'
    }
    else {
    Ask-TestQuestion $proc.Id 'Ask a test question'
    $card = Wait-Card $proc.Id $true 5
    if ($card) {
        Start-Sleep -Milliseconds 700
        [void][QSmoke.Native]::SetCursorPos($card.Rect.Left + 24, $card.Rect.Top + 16)
        [QSmoke.Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)    # left down
        [QSmoke.Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)    # left up
        Start-Sleep -Milliseconds 600
        Check 'card: clicking its body gives it the keyboard' ([QSmoke.Native]::GetForegroundWindow() -eq $card.Handle)
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Check 'card: Enter answers the focused choice and takes it down' ([bool](Wait-Card $proc.Id $false 5))
    }
    else { Check 'card: a second question appeared' $false }
    }

    # ---- 3c: unanswered, it times out ----
    Ask-TestQuestion $proc.Id 'Ask a test question'
    if (Wait-Card $proc.Id $true 5) {
        Write-Host '  waiting out the 20 s timeout...'
        Check 'card: unanswered, it times out and goes' ([bool](Wait-Card $proc.Id $false 30))
    }
    else { Check 'card: a third question appeared' $false }

    # ---- 4: the tray's "No answer" entry ----
    foreach ($h in (Get-Windows $proc.Id $false)) {
        [void][QSmoke.Native]::PostMessage($h.Handle, $WM_TRAYMOUSEMESSAGE, [IntPtr]1, [IntPtr]$WM_RBUTTONUP)
    }
    Start-Sleep -Milliseconds 1500
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byProcess = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $menuItem = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $names = @()
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        foreach ($m in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuItem)) { $names += $m.Current.Name }
    }
    Check ("tray: the menu lists the timed-out question under 'No answer (1)' (top items: {0})" -f (($names | Select-Object -First 3) -join ' | ')) (@($names | Where-Object { $_ -eq 'No answer (1)' }).Count -eq 1)
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')

    # ---- 3 + 5: the log ----
    Start-Sleep -Milliseconds 500
    $log = Join-Path $dataRoot 'diagnostics.log'
    $text = if (Test-Path -LiteralPath $log) { [IO.File]::ReadAllText($log) } else { '' }
    Check 'log: the press is recorded by key, outcome and choice id' ($text -match 'ended key=debug\.question #\d+ outcome=Chosen choice=keep')
    if ($canInject) { Check 'log: the click-and-Enter answer is recorded as the no-answer choice' ($text -match 'ended key=debug\.question #\d+ outcome=Chosen choice=drop') }
    Check 'log: the timeout is recorded as TimedOut with the no-answer choice' ($text -match 'ended key=debug\.question #\d+ outcome=TimedOut choice=drop')
    Check 'log: the question text never reached it' ($text.Length -gt 0 -and $text.IndexOf($SentinelFragment, [StringComparison]::OrdinalIgnoreCase) -lt 0)

    $failed = @($checks | Where-Object { -not $_.Pass }).Count
    if ($proc.HasExited) { Write-Host 'SMOKE FAIL: the app died' -ForegroundColor Red; $code = 1 }
    elseif ($failed -gt 0) { Write-Host "SMOKE FAIL: $failed of $($checks.Count) checks failed" -ForegroundColor Red; $code = 1 }
    elseif ($notRun.Count -gt 0) { Write-Host "SMOKE PASS WITH GAPS: $($checks.Count) checks passed, $($notRun.Count) not run here (synthetic input is dropped in this session)" -ForegroundColor Yellow; $code = 3 }
    else { Write-Host "SMOKE PASS: $($checks.Count) checks" -ForegroundColor Green; $code = 0 }
}
catch {
    if ($_.Exception -is [System.InvalidOperationException] -and $_.Exception.Message.StartsWith('CANNOT DRIVE')) {
        Write-Host ('SMOKE ' + $_.Exception.Message) -ForegroundColor Yellow
        $code = 4
    }
    else {
        Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
        $code = 1
    }
}
finally {
    [void][QSmoke.Native]::SetCursorPos($cursor.X, $cursor.Y)
    if ($probe) { try { $probe.Close(); $probe.Dispose() } catch { } }
    if ($proc) { try { if (-not $proc.HasExited) { $proc.Kill(); [void]$proc.WaitForExit(8000) } } catch { } }
    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $previousRoot
    try { [IO.Directory]::Delete($work, $true) }
    catch { Write-Warning ("the smoke's work folder could not be removed and is left for inspection: $work ($($_.Exception.Message))") }
}
exit $code
