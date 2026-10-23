#requires -Version 5.1
# Runs only PrepareOnly/revalidation paths. Never passes -Launch or starts VS Code.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $repoRoot 'tools\agentbroker\Open-CodexEditorProbe.ps1'
$checks = 0
function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw ('Test-CodexEditorProbePreparation.ps1: ' + $Description) }
    $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Description) {
    $failed = $false
    try { $null = & $Action } catch { $failed = $true }
    Check $failed $Description
}
function Prepare {
    $json = & $helper -TargetThreadId '11111111-1111-4111-8111-111111111111'
    return (($json -join "`n") | ConvertFrom-Json)
}
$prepared = Prepare
Check ($prepared.Status -eq 'prepared_only' -and $prepared.Launched -eq $false) 'default run opens no GUI'
Check (-not [IO.File]::Exists($prepared.Result)) 'preparation does not invent an editor result'
Check ($prepared.Arguments.Count -eq 3 -and $prepared.Arguments[0] -ceq '--new-window') 'fixed command forces a separate window'
Check ($prepared.Arguments[1] -ceq ('--extensionDevelopmentPath=' + $prepared.ExtensionDevelopmentPath)) 'fixed development path'
Check ($prepared.Arguments[2] -ceq $prepared.Workspace) 'exact prepared workspace is the only path argument'
Check (($prepared.Arguments -join ' ') -notmatch '(?i)disable|user-data|extensions-dir|profile|reuse|inspect|trust') 'no settings or security overrides'
$root = Split-Path -Parent $prepared.Workspace
$manifest = [IO.File]::ReadAllText($prepared.Manifest) | ConvertFrom-Json
$document = [IO.File]::ReadAllText($prepared.Workspace) | ConvertFrom-Json
Check ($manifest.probeId -cmatch '^[0-9a-f]{32}$') 'unique preparation identity'
Check ($document.settings.'agentbrokerEditorProbe.preparationId' -ceq $manifest.probeId) 'owning workspace identity matches manifest'
Check ($document.settings.'agentbrokerEditorProbe.targetThreadId' -ceq $manifest.targetThreadId) 'exact synthetic target matches manifest'
foreach ($name in @('extension.js', 'package.json')) {
    Check ((Get-FileHash -LiteralPath (Join-Path $prepared.ExtensionDevelopmentPath $name)).Hash.ToLowerInvariant() -ceq $manifest.sourceHashes.$name) ('copied bytes verified: ' + $name)
}
$again = ((& $helper -PreparedWorkspace $prepared.Workspace) -join "`n") | ConvertFrom-Json
Check ($again.Launched -eq $false -and $again.Workspace -ceq $prepared.Workspace) 'prepared workspace revalidation also opens no GUI'
$bound = ((& $helper -PreparedWorkspace $prepared.Workspace -ExpectedTargetThreadId '11111111-1111-4111-8111-111111111111') -join "`n") | ConvertFrom-Json
Check ($bound.Launched -eq $false) 'explicit expected target validates without launch'
Refuses { & $helper -PreparedWorkspace $prepared.Workspace -ExpectedTargetThreadId '22222222-2222-4222-8222-222222222222' } 'wrong explicit expected target refuses'
$manifestBytes = [IO.File]::ReadAllBytes($prepared.Manifest)
$workspaceBytes = [IO.File]::ReadAllBytes($prepared.Workspace)
try {
    $tamperedManifest = [IO.File]::ReadAllText($prepared.Manifest) | ConvertFrom-Json
    $tamperedWorkspace = [IO.File]::ReadAllText($prepared.Workspace) | ConvertFrom-Json
    $tamperedManifest.targetThreadId = '22222222-2222-4222-8222-222222222222'
    $tamperedWorkspace.settings.'agentbrokerEditorProbe.targetThreadId' = $tamperedManifest.targetThreadId
    [IO.File]::WriteAllText($prepared.Manifest, ($tamperedManifest | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($prepared.Workspace, ($tamperedWorkspace | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Refuses { & $helper -PreparedWorkspace $prepared.Workspace -ExpectedTargetThreadId '11111111-1111-4111-8111-111111111111' } 'coherently tampered manifest and workspace target still refuse'
} finally {
    [IO.File]::WriteAllBytes($prepared.Manifest, $manifestBytes)
    [IO.File]::WriteAllBytes($prepared.Workspace, $workspaceBytes)
}
# Exercise the actual pure production predicate with metadata only. No alternate EXE or protected fixture is created.
$tokens = $null
$parseErrors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$parseErrors)
$definition = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Confirm-CodeImageMetadata' }, $true)
if ($null -eq $definition) { throw 'Actual public-image metadata predicate not found.' }
. ([scriptblock]::Create($definition.Extent.Text))
$syntheticImage = Join-Path $root 'Code.exe'
Confirm-CodeImageMetadata $syntheticImage 'SYNTHETIC-HASH' '1.2.3' $syntheticImage 'SYNTHETIC-HASH' '1.2.3'
Check $true 'actual metadata predicate accepts matching synthetic metadata'
Refuses { Confirm-CodeImageMetadata (Join-Path $root 'foreign\Code.exe') 'SYNTHETIC-HASH' '1.2.3' $syntheticImage 'SYNTHETIC-HASH' '1.2.3' } 'actual predicate refuses a foreign Code image path'
Refuses { Confirm-CodeImageMetadata $syntheticImage 'CHANGED-HASH' '1.2.3' $syntheticImage 'SYNTHETIC-HASH' '1.2.3' } 'actual predicate refuses an image hash mismatch'
Refuses { Confirm-CodeImageMetadata $syntheticImage 'SYNTHETIC-HASH' '1.2.4' $syntheticImage 'SYNTHETIC-HASH' '1.2.3' } 'actual predicate refuses an installed version mismatch'
Refuses { & $helper -TargetThreadId 'bad-id' } 'malformed target refuses'
Refuses { & $helper -TargetThreadId '00000000-0000-0000-0000-000000000000' } 'empty target refuses'
Refuses { & $helper -PreparedWorkspace (Join-Path $repoRoot 'foreign.code-workspace') } 'foreign workspace refuses before read'
Refuses { & $helper -PreparedWorkspace $prepared.Workspace -TargetThreadId '22222222-2222-4222-8222-222222222222' } 'prepared target cannot be replaced'
$workspaceBytes = [IO.File]::ReadAllBytes($prepared.Workspace)
try {
    $document.settings.'agentbrokerEditorProbe.preparationId' = 'f' * 32
    [IO.File]::WriteAllText($prepared.Workspace, ($document | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Refuses { & $helper -PreparedWorkspace $prepared.Workspace } 'mismatched preparation refuses'
} finally { [IO.File]::WriteAllBytes($prepared.Workspace, $workspaceBytes) }
try {
    $document = [IO.File]::ReadAllText($prepared.Workspace) | ConvertFrom-Json
    $document | Add-Member -NotePropertyName tasks -NotePropertyValue @{}
    [IO.File]::WriteAllText($prepared.Workspace, ($document | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Refuses { & $helper -PreparedWorkspace $prepared.Workspace } 'extra workspace task configuration refuses'
} finally { [IO.File]::WriteAllBytes($prepared.Workspace, $workspaceBytes) }
$copy = Join-Path $prepared.ExtensionDevelopmentPath 'extension.js'
$sourceBytes = [IO.File]::ReadAllBytes($copy)
try {
    [IO.File]::AppendAllText($copy, "`n// synthetic change`n")
    Refuses { & $helper -PreparedWorkspace $prepared.Workspace } 'changed prepared source refuses'
} finally { [IO.File]::WriteAllBytes($copy, $sourceBytes) }
[IO.File]::WriteAllText($prepared.Result, 'preserve this receipt')
try { Refuses { & $helper -PreparedWorkspace $prepared.Workspace } 'existing result refuses re-use' }
finally {
    Check ([IO.File]::ReadAllText($prepared.Result) -ceq 'preserve this receipt') 'receipt never overwritten'
    [IO.File]::Delete($prepared.Result)
}
$link = Join-Path $root 'workspace'
$target = Join-Path $root 'ordinary-junction-target'
$null = [IO.Directory]::CreateDirectory($target)
[IO.Directory]::Delete($link)
try {
    $null = New-Item -ItemType Junction -Path $link -Target $target
    Refuses { & $helper -PreparedWorkspace $prepared.Workspace } 'owned junction refuses before descent'
} finally {
    if (([IO.File]::GetAttributes($link) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { [IO.Directory]::Delete($link) }
    $null = [IO.Directory]::CreateDirectory($link)
}
Check ([IO.Directory]::Exists($target)) 'junction target preserved'
Write-Output ('PASS: ' + $checks + ' preparation checks; no GUI launched.')
