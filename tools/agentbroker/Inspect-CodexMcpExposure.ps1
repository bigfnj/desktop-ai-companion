#requires -Version 5.1
<#
.SYNOPSIS
Read-only AgentBroker configuration and current-chat tool exposure observations.
.DESCRIPTION
Uses the vendor's read-only mcp get for the fixed server name. ExposedToolNames must
come from the current chat's actual tool catalog, including deferred tool keys.
This script never reads or changes client configuration, credentials or transcripts.
Endpoint reachability is deliberately separate: use Worker --mcp-call --list-tools
with the module data directory, which resolves the actual endpoint and authentication.
#>
[CmdletBinding()]
param([string]$CodexExecutable, [string[]]$ExposedToolNames, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-ExposureConfiguration([string]$Json) {
    try {
        $value = ConvertFrom-Json -InputObject $Json
        if ($null -eq $value -or $value -is [array]) { return 'unrecognized' }
        $fields = @($value.PSObject.Properties.Name)
        if ($fields -notcontains 'transport') { return 'unrecognized' }
        if ($fields -contains 'enabled' -and $value.enabled -isnot [bool]) { return 'unrecognized' }
        if ($fields -contains 'disabled_reason' -and $null -ne $value.disabled_reason) {
            if ($value.disabled_reason -isnot [string] -or $value.disabled_reason.Length -eq 0) { return 'unrecognized' }
            if ($value.disabled_reason -ceq 'requirements') { return 'disabled_by_requirements' }
            return 'disabled'
        }
        if ($fields -contains 'enabled' -and -not $value.enabled) { return 'disabled' }
        return 'present'
    } catch { return 'unrecognized' }
}
function Get-ChatToolExposure([string[]]$Names, [bool]$Supplied) {
    if (-not $Supplied) { return 'not_checked' }
    foreach ($name in $Names) {
        if ($name -cmatch '^mcp__desktop[-_]ai[-_]companion[-_]agentbroker(?:__|_)') { return 'present' }
    }
    return 'absent'
}
if ($SelfTest) {
    if ((Get-ExposureConfiguration '{"enabled":true,"transport":{},"disabled_reason":"requirements"}') -cne 'disabled_by_requirements') { throw 'EXPOSURE_REQUIREMENTS' }
    if ((Get-ExposureConfiguration '{"enabled":true,"transport":{},"disabled_reason":"unfamiliar"}') -cne 'disabled') { throw 'EXPOSURE_UNKNOWN_REASON' }
    if ((Get-ExposureConfiguration '{"enabled":"yes","transport":{}}') -cne 'unrecognized') { throw 'EXPOSURE_BAD_SHAPE' }
    if ((Get-ChatToolExposure @('mcp__desktop-ai-companion-agentbroker__send') $true) -cne 'present') { throw 'EXPOSURE_DEFERRED_KEY' }
    if ((Get-ChatToolExposure @('mcp__desktop_ai_companion_agentbroker_send') $true) -cne 'present') { throw 'EXPOSURE_NORMALIZED_KEY' }
    if ((Get-ChatToolExposure @('desktop-ai-companion-agentbroker','mcp__another_server_send') $true) -cne 'absent') { throw 'EXPOSURE_NAMESPACE_NOT_TOOL' }
    if ((Get-ChatToolExposure @() $false) -cne 'not_checked') { throw 'EXPOSURE_UNKNOWN_NOT_ABSENT' }
    Write-Output 'PASS: 7 exposure classification controls; no client or endpoint invoked.'
    exit 0
}
$configuration = 'check_failed'
try {
    if (-not $CodexExecutable) {
        $command = Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue
        if ($command) { $CodexExecutable = $command.Source }
    }
    if (-not $CodexExecutable) { $configuration = 'cli_missing' }
    elseif (-not [IO.Path]::IsPathRooted($CodexExecutable) -or [IO.Path]::GetExtension($CodexExecutable) -ine '.exe') { $configuration = 'invalid_executable' }
    else {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $CodexExecutable
        $start.Arguments = 'mcp get desktop-ai-companion-agentbroker --json'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $output = $process.StandardOutput.ReadToEndAsync()
            $errors = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw 'Probe timed out.' }
            $text = $output.GetAwaiter().GetResult()
            $errorText = $errors.GetAwaiter().GetResult()
            if ($text.Length -gt 65536 -or $errorText.Length -gt 65536) { throw 'Probe output limit.' }
            if ($process.ExitCode -eq 0) { $configuration = Get-ExposureConfiguration $text }
            elseif (($text + $errorText) -cmatch 'desktop-ai-companion-agentbroker' -and ($text + $errorText) -match '(?i)not found|No MCP server') { $configuration = 'absent' }
        } finally { $process.Dispose() }
    }
} catch { $configuration = 'check_failed' }
$exposure = Get-ChatToolExposure $ExposedToolNames ($PSBoundParameters.ContainsKey('ExposedToolNames'))
$next = if ($configuration -eq 'disabled_by_requirements') { 'Ask the policy owner to permit this server; then start a fresh Codex chat.' }
    elseif ($configuration -eq 'absent') { 'Use Connect Codex MCP, then start a fresh chat and check its actual tool catalog.' }
    elseif ($exposure -eq 'absent') { 'Start a fresh Codex chat and inspect its actual tool catalog; configuration does not prove exposure.' }
    elseif ($exposure -eq 'not_checked') { 'Supply current chat tool keys to establish exposure; deferred keys count.' }
    else { 'Use the exposed AgentBroker tools for this chat.' }
[ordered]@{ configuration = $configuration; endpoint = 'not_checked'; currentChatTools = $exposure; nextAction = $next } | ConvertTo-Json -Compress
