#requires -Version 5
<# SDK-free provenance API regressions. The producer needs PS7's metadata-reader runtime references. #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$producer = Join-Path $PSScriptRoot 'Test-AgentBrokerReleaseProvenanceRules.Producer.ps1'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    & $producer
} else {
    $shell = Get-Command pwsh -CommandType Application -ErrorAction Stop
    & $shell.Source -NoLogo -NoProfile -File $producer
    if ($LASTEXITCODE -ne 0) { throw 'PROVENANCE_RULES_PS7_PRODUCER_FAILED' }
}
