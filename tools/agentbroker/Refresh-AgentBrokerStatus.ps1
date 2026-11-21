<#
.SYNOPSIS
Inspects the exact installed companion's AgentBroker pane and optionally invokes its status refresh.
.DESCRIPTION
Run with Windows PowerShell 5.1. Without -Invoke this performs only inspection.
The optional action uses UI Automation InvokePattern on one verified button. It never
saves settings, restarts a process, changes focus, or sends keyboard/mouse input.
Only source-identified status rows are read; arbitrary UI text is never printed.
.EXAMPLE
powershell.exe -NoProfile -Mta -ExecutionPolicy Bypass -File tools/agentbroker/Refresh-AgentBrokerStatus.ps1 -CompanionProcessId 53772
.EXAMPLE
powershell.exe -NoProfile -Mta -ExecutionPolicy Bypass -File tools/agentbroker/Refresh-AgentBrokerStatus.ps1 -CompanionProcessId 53772 -Invoke
#>
[CmdletBinding(DefaultParameterSetName = 'Live')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Live')][ValidateRange(2, 2147483647)][int]$CompanionProcessId,
    [Parameter(Mandatory = $true, ParameterSetName = 'SelfTest')][switch]$SelfTest,
    [switch]$Invoke,
    [ValidateRange(1, 15)][int]$WaitSeconds = 8
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Convert-KnownStatus([string]$Status, [string]$Native, [string]$Traffic,
    [int]$RefreshSuccessCount = 0, [int]$RefreshUnavailableCount = 0, [bool]$NativeObserved = $true) {
    $nativeObservation = if (-not $NativeObserved) { 'not-observed' }
        elseif ($Native -ceq 'No worker status yet.') { 'visible-worker-status-unavailable' }
        elseif ($Native -ceq 'No adapter errors reported.') { 'visible-no-adapter-errors' }
        elseif ($Native -cmatch '^(?:claude-native|codex-native): ') { 'visible-adapter-errors' }
        else { 'visible-unrecognized' }
    $nativeKnown = $NativeObserved -and $nativeObservation -cne 'visible-unrecognized'
    return [pscustomobject]@{
        Starting = $Status -ceq 'Starting'
        Running = $Status -cmatch '^Running\. [0-9]+ sessions, [0-9]+ paired brokers\.(?: MCP |$)'
        Standby = $Status -cmatch '^Connected to the existing broker \(standby\)\. [0-9]+ sessions, [0-9]+ paired brokers\.(?: MCP |$)'
        Disabled = $Status -ceq 'Disabled'
        McpStatusPresent = $Status.Contains('. MCP ')
        McpReady = $Status.Contains('. MCP ready.')
        ClaudeMcpSeen = $Traffic -cmatch 'Claude client: seen (?:just now|[0-9]+ (?:min|h|days) ago)'
        CodexMcpSeen = $Traffic -cmatch 'Codex client: seen (?:just now|[0-9]+ (?:min|h|days) ago)'
        NativeRowObserved = $NativeObserved
        NativeObservation = $nativeObservation
        NativeStatusMissing = if ($nativeKnown) { $Native -ceq 'No worker status yet.' } else { $null }
        NoAdapterErrors = if ($nativeKnown) { $Native -ceq 'No adapter errors reported.' } else { $null }
        RefreshSuccessCount = $RefreshSuccessCount
        RefreshUnavailableCount = $RefreshUnavailableCount
    }
}
function Get-PaneIdentityLabels { return @('Enable AgentBroker', 'Local port', 'Pairing') }
# Lane U round 6 (U-1): the host names each editor after its label (BuildEditorRow), so an untyped search sees 'Enable
# AgentBroker' and 'Local port' twice (label and editor). Identity counts the Text label only, as Get-StatusRowText reads it.
# $FindVisible takes (label, control type name) and returns the visible matches; the live finder and the self-test's pane
# model both go through this one function, so the Text filter is under the self-test.
function Get-PaneIdentityCount([scriptblock]$FindVisible, [string]$Label) { return @(& $FindVisible $Label 'Text').Count }
function Assert-AgentBrokerPaneIdentity([scriptblock]$FindVisible) {
    foreach ($label in @(Get-PaneIdentityLabels)) {
        if ((Get-PaneIdentityCount $FindVisible $label) -ne 1) {
            throw 'STATUS_PANE_IDENTITY: the visible settings content does not uniquely identify the AgentBroker pane.'
        }
    }
}
function Read-KnownRows([scriptblock]$ReadRow, [scriptblock]$CountText) {
    $status = & $ReadRow 'Status' $false
    # Clients may be folded. Absence of this optional row is an observation limit, not a healthy result.
    $native = & $ReadRow 'Native adapters' $true
    $traffic = & $ReadRow 'MCP client traffic' $false
    $success = & $CountText 'Status refreshed.'
    $unavailable = & $CountText 'The broker is unavailable. Try again after it reconnects.'
    return Convert-KnownStatus $status $native $traffic $success $unavailable ($null -ne $native)
}
if ($SelfTest) {
    $checks = 0
    foreach ($age in @('just now', '2 min ago', '3 h ago', '4 days ago')) {
        $state = Convert-KnownStatus 'Running. 2 sessions, 1 paired brokers. MCP ready.' 'No adapter errors reported.' `
            ('Claude client: seen ' + $age + '. Codex client: seen ' + $age + '.')
        if (-not $state.Running -or -not $state.McpReady -or -not $state.ClaudeMcpSeen -or -not $state.CodexMcpSeen) {
            throw 'STATUS_TRAFFIC_ROW: source-identified MCP traffic was not recognized.'
        }
        $checks++
    }
    $state = Convert-KnownStatus 'Claude: seen just now. Codex: seen just now.' 'No worker status yet.' `
        'Claude client: not seen. Codex client: not seen.'
    if ($state.ClaudeMcpSeen -or $state.CodexMcpSeen -or -not $state.NativeStatusMissing) {
        throw 'STATUS_TRAFFIC_NOT_STATUS: unrelated status text or unseen clients must not count as traffic.'
    }
    $checks++
    $state = Convert-KnownStatus 'Running. 2 sessions, 1 paired brokers. MCP ready.' 'No adapter errors reported.' ''
    if (-not $state.NativeRowObserved -or $state.NativeObservation -cne 'visible-no-adapter-errors' -or
        $state.NativeStatusMissing -ne $false -or $state.NoAdapterErrors -ne $true) {
        throw 'STATUS_NATIVE_ROW_HEALTHY: a visible exact no-error status is known healthy evidence.'
    }
    $checks++
    # Round 6 (U-m10): each identity label is a Label literal of the module's schema, so a renamed pane row fails here and not
    # only at the live smoke step. Read from the source beside this tool; a missing source is a failure, never a skip.
    $moduleSource = Join-Path $PSScriptRoot '..\..\modules\AgentBroker\AgentBrokerModule.cs'
    if (-not (Test-Path -LiteralPath $moduleSource)) { throw 'STATUS_PANE_LABELS_IN_SCHEMA: the module source is not beside this tool.' }
    $moduleText = [IO.File]::ReadAllText($moduleSource)
    foreach ($label in @(Get-PaneIdentityLabels)) {
        if (([regex]::Matches($moduleText, [regex]::Escape('Label = "' + $label + '"'))).Count -ne 1) {
            throw ('STATUS_PANE_LABELS_IN_SCHEMA: ' + $label + ' is not exactly one Label in the module schema.')
        }
    }
    $checks++
    # The pane as UI Automation sees it (U-1): an editor row is its Text label plus an editor named after it; an info row is
    # its Text label and a value named by its text. Untyped, 'Enable AgentBroker' and 'Local port' match twice.
    $paneModel = @{ 'Enable AgentBroker' = @('Text', 'CheckBox'); 'Local port' = @('Text', 'Edit'); 'Pairing' = @('Text'); 'Native adapters' = @('Text') }
    $finder = { param($label, $type)
        if (-not $paneModel.ContainsKey($label)) { return @() }
        return @($paneModel[$label] | Where-Object { $null -eq $type -or $_ -ceq $type })
    }
    Assert-AgentBrokerPaneIdentity $finder
    $checks++
    $paneModel = @{ 'Enable AgentBroker' = @('Text', 'CheckBox'); 'Local port' = @('Text', 'Edit'); 'Native delivery' = @('Text'); 'Native adapters' = @('Text') }
    $identityRefused = $false
    try { Assert-AgentBrokerPaneIdentity $finder }
    catch { if (-not $_.Exception.Message.StartsWith('STATUS_PANE_IDENTITY:', [StringComparison]::Ordinal)) { throw }; $identityRefused = $true }
    if (-not $identityRefused) { throw 'STATUS_PANE_IDENTITY_RETIRED: the retired labels must not identify this pane.' }
    $checks++
    # Round 6 (U-m10): uniqueness. Two visible AgentBroker panes (a Pairing label twice) do not identify one pane.
    $paneModel = @{ 'Enable AgentBroker' = @('Text', 'CheckBox'); 'Local port' = @('Text', 'Edit'); 'Pairing' = @('Text', 'Text') }
    $identityRefused = $false
    try { Assert-AgentBrokerPaneIdentity $finder }
    catch { if (-not $_.Exception.Message.StartsWith('STATUS_PANE_IDENTITY:', [StringComparison]::Ordinal)) { throw }; $identityRefused = $true }
    if (-not $identityRefused) { throw 'STATUS_PANE_IDENTITY_UNIQUE: a label seen twice as Text must not identify one pane.' }
    $checks++
    $visibleRows = @{ 'Status' = 'Running. 2 sessions, 1 paired brokers. MCP ready.'; 'MCP client traffic' = '' }
    $state = Read-KnownRows {
        param($label, $optional)
        if ($visibleRows.ContainsKey($label)) { return $visibleRows[$label] }
        if (-not $optional) { throw 'STATUS_NATIVE_ROW_HIDDEN: a required status row was unavailable.' }
        return $null
    } { param($text) 0 }
    if ($state.NativeRowObserved -or $state.NativeObservation -cne 'not-observed' -or
        $null -ne $state.NativeStatusMissing -or $null -ne $state.NoAdapterErrors) {
        throw 'STATUS_NATIVE_ROW_HIDDEN: a folded Clients row is unobserved, never healthy or worker-missing evidence.'
    }
    $checks++
    foreach ($required in @('Status', 'MCP client traffic')) {
        $visibleRows = @{ 'Status' = 'Running. 2 sessions, 1 paired brokers. MCP ready.'; 'MCP client traffic' = '' }
        $visibleRows.Remove($required)
        $requiredRefused = $false
        try {
            $null = Read-KnownRows {
                param($label, $optional)
                if ($visibleRows.ContainsKey($label)) { return $visibleRows[$label] }
                if (-not $optional) { throw ('Required row missing: ' + $label) }
                return $null
            } { param($text) 0 }
        }
        catch {
            if ($_.Exception.Message -cne ('Required row missing: ' + $required)) { throw }
            $requiredRefused = $true
        }
        if (-not $requiredRefused) {
            $label = if ($required -ceq 'Status') { 'STATUS_REQUIRED_STATUS_ROW' } else { 'STATUS_REQUIRED_TRAFFIC_ROW' }
            throw ($label + ': the pinned status row must remain required.')
        }
        $checks++
    }
    $state = Convert-KnownStatus 'Running. 2 sessions, 1 paired brokers. MCP ready.' 'unexpected native text' ''
    if (-not $state.NativeRowObserved -or $state.NativeObservation -cne 'visible-unrecognized' -or $null -ne $state.NoAdapterErrors) {
        throw 'STATUS_NATIVE_ROW_UNKNOWN: an unrecognized visible row is not healthy evidence.'
    }
    $checks++
    $state = Convert-KnownStatus 'Running. 2 sessions, 1 paired brokers. MCP ready.' 'claude-native: synthetic_adapter_error' ''
    if ($state.NativeObservation -cne 'visible-adapter-errors' -or $state.NoAdapterErrors -ne $false) {
        throw 'STATUS_NATIVE_ROW_ERROR: a visible adapter error must remain a known error.'
    }
    $checks++
    $state = Convert-KnownStatus 'Disabled' 'No adapter errors reported.' 'Claude: seen just now. Codex: seen just now.'
    if ($state.ClaudeMcpSeen -or $state.CodexMcpSeen -or -not $state.Disabled) {
        throw 'STATUS_TRAFFIC_CURRENT_LABEL: retired traffic labels must not match.'
    }
    $checks++
    [pscustomobject]@{ SelfTestPassed = $true; Checks = $checks } | ConvertTo-Json -Compress
    return
}
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::OSVersion.Platform -ne 'Win32NT') {
    throw 'Use Windows PowerShell 5.1 for the installed UI Automation assemblies.'
}
try {
    Add-Type -AssemblyName 'UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'
    Add-Type -AssemblyName 'UIAutomationTypes, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'
} catch { throw 'The installed UI Automation assemblies are unavailable; no action was attempted.' }

function Get-VisibleMatches($Parent, [string]$Name, $ControlType = $null,
    $Scope = [System.Windows.Automation.TreeScope]::Descendants) {
    $conditions = [Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $CompanionProcessId))
    if ($null -ne $ControlType) {
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType))
    }
    $condition = [System.Windows.Automation.AndCondition]::new($conditions.ToArray())
    $matches = $Parent.FindAll($Scope, $condition)
    return @($matches | Where-Object { -not $_.Current.IsOffscreen })
}

function Get-StatusRowText($Window, [string]$Label, [switch]$Optional) {
    $labels = @(Get-VisibleMatches $Window $Label ([System.Windows.Automation.ControlType]::Text))
    if ($Optional -and $labels.Count -eq 0) { return $null }
    if ($labels.Count -ne 1) { throw 'A source-identified status label was not unique and visible.' }
    # BuildEditorRow adds the label followed immediately by the display-only TextBlock.
    # Require same-process, same-row geometry before reading that one sibling's name.
    $value = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetNextSibling($labels[0])
    if ($null -eq $value) { throw 'The expected status value was unavailable; no wider UI scan was attempted.' }
    $labelInfo = $labels[0].Current
    $valueInfo = $value.Current
    $labelBounds = $labelInfo.BoundingRectangle
    $valueBounds = $valueInfo.BoundingRectangle
    if ($valueInfo.ProcessId -ne $CompanionProcessId -or $valueInfo.IsPassword -or $valueInfo.IsOffscreen -or
        $valueInfo.ControlType -ne [System.Windows.Automation.ControlType]::Text -or
        $valueBounds.IsEmpty -or $labelBounds.IsEmpty -or $valueBounds.Left -lt $labelBounds.Right - 3 -or
        [Math]::Abs(($labelBounds.Top + $labelBounds.Height / 2) - ($valueBounds.Top + $valueBounds.Height / 2)) -gt 6) {
        throw 'The expected status sibling did not match its source layout; no text was read.'
    }
    return $value.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
}

function Get-KnownStatus($Window) {
    return Read-KnownRows { param($label, $optional) Get-StatusRowText $Window $label -Optional:$optional } `
        { param($text) @(Get-VisibleMatches $Window $text ([System.Windows.Automation.ControlType]::Text)).Count }
}

function Write-KnownStatus([string]$Prefix, $State) {
    foreach ($name in @('Starting', 'Running', 'Standby', 'Disabled', 'McpStatusPresent', 'McpReady',
                        'ClaudeMcpSeen', 'CodexMcpSeen', 'NativeRowObserved', 'NativeObservation', 'NativeStatusMissing', 'NoAdapterErrors',
                        'RefreshSuccessCount', 'RefreshUnavailableCount')) {
        $value = $State.$name
        if ($null -eq $value) { $value = 'not-observed' }
        Write-Output ($Prefix + $name + '=' + $value)
    }
}

$process = [Diagnostics.Process]::GetProcessById($CompanionProcessId)
try {
    $expectedImage = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Desktop AI Companion\DesktopAICompanion.exe'))
    if ($process.HasExited -or -not $process.MainModule.FileName.Equals($expectedImage, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The supplied process does not own the supported installed companion.'
    }
    $created = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
    $title = 'DesktopAICompanion ' + [char]0x2014 + ' Settings'
    $windows = @(Get-VisibleMatches ([System.Windows.Automation.AutomationElement]::RootElement) $title `
        ([System.Windows.Automation.ControlType]::Window) ([System.Windows.Automation.TreeScope]::Children))
    if ($windows.Count -ne 1) { throw 'The supplied process has no unique visible settings window.' }
    $window = $windows[0]
    Assert-AgentBrokerPaneIdentity { param($label, $type)
        $controlType = if ($type -ceq 'Text') { [System.Windows.Automation.ControlType]::Text } else { $null }
        @(Get-VisibleMatches $window $label $controlType) }
    $buttons = @(Get-VisibleMatches $window 'Refresh status' ([System.Windows.Automation.ControlType]::Button))
    if ($buttons.Count -ne 1 -or -not $buttons[0].Current.IsEnabled) {
        throw 'The AgentBroker refresh button is not uniquely visible and enabled.'
    }
    $pattern = $null
    if (-not $buttons[0].TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw 'The verified refresh button does not support background invocation.'
    }
    $initialButtonIdentity = [string]::Join(',', $buttons[0].GetRuntimeId())
    $before = Get-KnownStatus $window
    Write-Output 'ExactCompanionVerified=True'
    Write-Output 'UniqueAgentBrokerPane=True'
    Write-Output 'UniqueRefreshButton=True'
    Write-KnownStatus 'Before' $before
    if (-not $Invoke) { Write-Output 'Invoked=False'; return }
    if ($process.HasExited -or $process.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $created -or
        -not $process.MainModule.FileName.Equals($expectedImage, [StringComparison]::OrdinalIgnoreCase) -or
        $buttons[0].Current.ProcessId -ne $CompanionProcessId -or $buttons[0].Current.IsOffscreen -or
        -not $buttons[0].Current.IsEnabled) {
        throw 'The retained companion or verified button changed before invocation.'
    }
    # Source: PaneView.BuildActionRow directly calls this action, without Save. Its only
    # request is AgentBrokerModule.SendAsync(new("status")); ReloadPaneAfter rebuilds the view.
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
    Write-Output 'Invoked=True'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $after = $null
    $rebuilt = $false
    do {
        Start-Sleep -Milliseconds 200
        try {
            $currentButtons = @(Get-VisibleMatches $window 'Refresh status' ([System.Windows.Automation.ControlType]::Button))
            $rebuilt = $currentButtons.Count -eq 1 -and $currentButtons[0].Current.IsEnabled -and
                [string]::Join(',', $currentButtons[0].GetRuntimeId()) -cne $initialButtonIdentity
            if ($rebuilt) { $after = Get-KnownStatus $window }
        }
        catch { $after = $null; $rebuilt = $false }
        if ($rebuilt -and $null -ne $after -and ($after.RefreshSuccessCount -gt 0 -or $after.RefreshUnavailableCount -gt 0)) { break }
    } while ($watch.Elapsed.TotalSeconds -lt $WaitSeconds)
    if ($null -eq $after) { throw 'The rebuilt status rows were not available within the observation interval.' }
    Write-Output ('RefreshRebuilt=' + $rebuilt)
    Write-KnownStatus 'After' $after
    Write-Output ('CompanionUnchanged=' + (-not $process.HasExited -and $process.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $created))
} finally { $process.Dispose() }
