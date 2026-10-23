#requires -Version 5.1
<#
.SYNOPSIS
    Read the Public firewall profile and rules for this installed worker or TCP 7433.
.DESCRIPTION
    Normal-user, read-only metadata. Never changes rules or network categories, launches
    an agent, or reads settings/credentials. Rule names and unrelated rule contents are
    never printed. Port-filter selection considers explicit TCP 7433/ranges; application
    selection includes every rule for the exact installed worker. This configuration
    evidence does not establish the final packet decision on either workstation.
.EXAMPLE
    powershell.exe -NoProfile -File tools/agentbroker/Test-AgentBrokerFirewall.ps1 -SelfTest
.EXAMPLE
    powershell.exe -NoProfile -File tools/agentbroker/Test-AgentBrokerFirewall.ps1
#>
[CmdletBinding()]
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-ExplicitBrokerPort($Ports) {
    foreach ($part in @($Ports)) {
        foreach ($token in ([string]$part).Split(',')) {
            $token = $token.Trim()
            if ($token -eq '7433') { return $true }
            if ($token -match '^([0-9]{1,5})-([0-9]{1,5})$' -and
                [int]$Matches[1] -le 7433 -and [int]$Matches[2] -ge 7433) { return $true }
        }
    }
    return $false
}

function Get-PublicRemoteScopes($Values) {
    $safe = [Collections.Generic.List[string]]::new()
    foreach ($value in @($Values)) {
        $text = [string]$value
        if ($text -in @('Any', 'LocalSubnet', 'LocalSubnet4', 'LocalSubnet6', 'Internet', 'Intranet',
                         'DNS', 'DHCP', 'WINS', 'DefaultGateway') -or
            $text -match '^[0-9.]+(?:/[0-9]{1,2}|-[0-9.]+)?$' -or
            $text -match '^[0-9a-fA-F:]+(?:/[0-9]{1,3})?$') { $safe.Add($text) }
        else { $safe.Add('OtherRestrictedScope') }
        if ($safe.Count -ge 16) { break }
    }
    return $safe.ToArray()
}

function Get-FirewallFailureCode([Management.Automation.ErrorRecord]$Record) {
    if ($Record.CategoryInfo.Category -in @('PermissionDenied', 'SecurityError') -or
        $Record.FullyQualifiedErrorId -match 'AccessDenied|Unauthorized|PermissionDenied') { return 'access_denied' }
    $exception = $Record.Exception
    for ($depth = 0; $null -ne $exception -and $depth -lt 8; $depth++) {
        if ($exception -is [UnauthorizedAccessException] -or $exception.HResult -eq -2147024891) { return 'access_denied' }
        if ($exception -is [NotSupportedException] -or $exception -is [Management.Automation.ParameterBindingException]) {
            return 'unsupported_query'
        }
        if ($exception.PSObject.Properties['NativeErrorCode']) {
            $native = [string]$exception.NativeErrorCode
            if ($native -in @('5', 'AccessDenied')) { return 'access_denied' }
            if ($native -in @('InvalidQuery', 'QueryLanguageNotSupported', 'NotSupported', 'InvalidParameter')) { return 'unsupported_query' }
        }
        $exception = $exception.InnerException
    }
    return 'provider_unavailable'
}

if ($SelfTest) {
    $checks = 0
    foreach ($case in @(@('7433', $true), @('7000-8000', $true), @('7432,7433', $true),
                        @('7432', $false), @('Any', $false), @('8000-9000', $false), @('invalid', $false))) {
        if ((Test-ExplicitBrokerPort $case[0]) -ne $case[1]) { throw 'Port-selection self-test failed.' }
        $checks++
    }
    if ((Get-PublicRemoteScopes @('LocalSubnet', '192.168.1.0/24', 'unexpected-private-label')) -join '|' -cne
        'LocalSubnet|192.168.1.0/24|OtherRestrictedScope') { throw 'Fixed remote-scope output self-test failed.' }
    $checks++
    foreach ($case in @(@([UnauthorizedAccessException]::new('SYNTHETIC_PRIVATE_DETAIL'), 'access_denied'),
                        @([NotSupportedException]::new('SYNTHETIC_PRIVATE_DETAIL'), 'unsupported_query'),
                        @([Exception]::new('SYNTHETIC_PRIVATE_DETAIL'), 'provider_unavailable'))) {
        $record = [Management.Automation.ErrorRecord]::new($case[0], 'SyntheticFailure', 'NotSpecified', $null)
        if ((Get-FirewallFailureCode $record) -cne $case[1]) { throw 'Fixed error classification self-test failed.' }
        $checks++
    }
    Write-Output ('PolicyChecks=' + $checks)
    Write-Output 'FirewallQueried=False'
    return
}

$stage = 'public_profile'
try {
    $worker = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Desktop AI Companion\modules\agentbroker\worker\AgentBroker.Worker.exe'))
    $profile = Get-NetFirewallProfile -Name Public -PolicyStore ActiveStore
    $rules = @{}
    $stage = 'worker_application_filters'
    foreach ($filter in @(Get-NetFirewallApplicationFilter -Program $worker -PolicyStore ActiveStore)) {
        $stage = 'worker_rules'
        foreach ($rule in @(Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $filter -PolicyStore ActiveStore)) {
            if ([string]$rule.Direction -eq 'Inbound') { $rules[[string]$rule.Name] = $rule }
        }
    }
    $stage = 'tcp_port_filters'
    foreach ($filter in @(Get-NetFirewallPortFilter -Protocol TCP -PolicyStore ActiveStore)) {
        if (-not (Test-ExplicitBrokerPort $filter.LocalPort)) { continue }
        $stage = 'tcp_port_rules'
        foreach ($rule in @(Get-NetFirewallRule -AssociatedNetFirewallPortFilter $filter -PolicyStore ActiveStore)) {
            if ([string]$rule.Direction -eq 'Inbound') { $rules[[string]$rule.Name] = $rule }
        }
    }
    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($rule in $rules.Values) {
        $stage = 'rule_application'
        $application = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -PolicyStore ActiveStore
        $stage = 'rule_address'
        $address = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule -PolicyStore ActiveStore
        $stage = 'rule_port'
        $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -PolicyStore ActiveStore
        $program = [string]$application.Program
        $summaries.Add([pscustomobject]@{
            Enabled = ([string]$rule.Enabled -eq 'True')
            Action = [string]$rule.Action
            Profiles = [string]$rule.Profile
            ExactWorkerProgram = $program.Equals($worker, [StringComparison]::OrdinalIgnoreCase)
            AnyProgram = $program -in @('', 'Any', '*')
            Protocol = [string]$port.Protocol
            ExplicitTcp7433 = ([string]$port.Protocol -in @('TCP', '6', 'Any', '256') -and (Test-ExplicitBrokerPort $port.LocalPort))
            RemoteScopes = @(Get-PublicRemoteScopes $address.RemoteAddress)
        })
        if ($summaries.Count -ge 64) { break }
    }
    [pscustomobject]@{
        FirewallMetadataAvailable = $true
        Profile = 'Public'
        Enabled = ([string]$profile.Enabled -eq 'True')
        DefaultInboundAction = [string]$profile.DefaultInboundAction
        AllowInboundRules = [string]$profile.AllowInboundRules
        AllowLocalFirewallRules = [string]$profile.AllowLocalFirewallRules
        MatchedRuleCount = $rules.Count
        OutputTruncated = ($rules.Count -gt $summaries.Count)
        Rules = $summaries.ToArray()
        RulesChanged = $false
        PacketDecisionProven = $false
    } | ConvertTo-Json -Depth 6 -Compress
}
catch {
    [pscustomobject]@{ FirewallMetadataAvailable = $false; Stage = $stage;
        Code = (Get-FirewallFailureCode $_); RulesChanged = $false } | ConvertTo-Json -Compress
    exit 1
}
