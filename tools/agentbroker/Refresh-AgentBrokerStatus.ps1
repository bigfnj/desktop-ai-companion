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
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateRange(2, 2147483647)][int]$CompanionProcessId,
    [switch]$Invoke,
    [ValidateRange(1, 15)][int]$WaitSeconds = 8
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
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

function Get-StatusRowText($Window, [string]$Label) {
    $labels = @(Get-VisibleMatches $Window $Label ([System.Windows.Automation.ControlType]::Text))
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
    $status = Get-StatusRowText $Window 'Status'
    $native = Get-StatusRowText $Window 'Native adapters'
    return [pscustomobject]@{
        Starting = $status -ceq 'Starting'
        Running = $status -cmatch '^Running\. [0-9]+ sessions, [0-9]+ paired brokers\.(?: MCP |$)'
        Standby = $status -cmatch '^Connected to the existing broker \(standby\)\. [0-9]+ sessions, [0-9]+ paired brokers\.(?: MCP |$)'
        Disabled = $status -ceq 'Disabled'
        McpStatusPresent = $status.Contains('. MCP ')
        McpReady = $status.Contains('. MCP ready.')
        ClaudeMcpSeen = $status -cmatch 'Claude: seen (?:just now|[0-9]+ (?:min|h|days) ago)'
        CodexMcpSeen = $status -cmatch 'Codex: seen (?:just now|[0-9]+ (?:min|h|days) ago)'
        NativeStatusMissing = $native -ceq 'No worker status yet.'
        NoAdapterErrors = $native -ceq 'No adapter errors reported.'
        RefreshSuccessCount = @(Get-VisibleMatches $Window 'Status refreshed.' ([System.Windows.Automation.ControlType]::Text)).Count
        RefreshUnavailableCount = @(Get-VisibleMatches $Window 'The broker is unavailable. Try again after it reconnects.' ([System.Windows.Automation.ControlType]::Text)).Count
    }
}

function Write-KnownStatus([string]$Prefix, $State) {
    foreach ($name in @('Starting', 'Running', 'Standby', 'Disabled', 'McpStatusPresent', 'McpReady',
                        'ClaudeMcpSeen', 'CodexMcpSeen', 'NativeStatusMissing', 'NoAdapterErrors',
                        'RefreshSuccessCount', 'RefreshUnavailableCount')) {
        Write-Output ($Prefix + $name + '=' + $State.$name)
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
    foreach ($label in @('Enable AgentBroker', 'Native adapters', 'Native delivery', 'Local port')) {
        if (@(Get-VisibleMatches $window $label).Count -ne 1) {
            throw 'The visible settings content does not uniquely identify the AgentBroker pane.'
        }
    }
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
