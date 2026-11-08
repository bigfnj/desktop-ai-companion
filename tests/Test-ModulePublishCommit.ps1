#requires -Version 5
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Join-Path $RepoRoot 'packaging\New-ModulePublish.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'MODULE_COMMIT_SOURCE: publisher source does not parse.' }
foreach ($name in @('Invoke-Git', 'Invoke-ModulePayloadCommit')) {
    $functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($functions.Count -ne 1) { throw 'MODULE_COMMIT_SOURCE: expected one publisher function.' }
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
$parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $parent ('dac-module-commit-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'modules-dist'))
$checks = 0
try {
    $null = Invoke-Git @('-C', $fixture, 'init', '-q') 'init synthetic repository'
    $null = Invoke-Git @('-C', $fixture, 'config', 'user.name', 'bigfnj') 'configure synthetic identity'
    $null = Invoke-Git @('-C', $fixture, 'config', 'user.email', 'bigfnj@users.noreply.github.com') 'configure synthetic identity'
    [IO.File]::WriteAllText((Join-Path $fixture 'modules-dist/broker.zip'), 'synthetic baseline')
    [IO.File]::WriteAllText((Join-Path $fixture 'modules-dist/modules.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $fixture 'foreign.txt'), 'foreign baseline')
    $null = Invoke-Git @('-C', $fixture, 'add', '--', 'modules-dist/broker.zip', 'modules-dist/modules.json', 'foreign.txt') 'stage baseline'
    $null = Invoke-Git @('-C', $fixture, 'commit', '-q', '-m', 'synthetic baseline') 'commit baseline'
    [IO.File]::WriteAllText((Join-Path $fixture 'modules-dist/broker.zip'), 'synthetic changed payload')
    [IO.File]::WriteAllText((Join-Path $fixture 'foreign.txt'), 'foreign staged work')
    $null = Invoke-Git @('-C', $fixture, 'add', '--', 'foreign.txt') 'stage unrelated work'
    $foreignBefore = Invoke-Git @('-C', $fixture, 'rev-parse', ':foreign.txt') 'read unrelated staged blob'
    $committed = Invoke-ModulePayloadCommit $fixture 'modules-dist/broker.zip' 'synthetic payload publication' 'synthetic runtime body'
    $paths = @(Invoke-Git @('-C', $fixture, 'diff-tree', '--no-commit-id', '--name-only', '-r', 'HEAD') 'read payload commit paths')
    if (-not $committed -or $paths.Count -ne 1 -or $paths[0] -cne 'modules-dist/broker.zip') { throw 'MODULE_COMMIT_OWNS_PATHS: the payload commit included another path.' }
    $checks++
    $foreignAfter = Invoke-Git @('-C', $fixture, 'rev-parse', ':foreign.txt') 'reread unrelated staged blob'
    $staged = @(Invoke-Git @('-C', $fixture, 'diff', '--cached', '--name-only') 'read remaining index')
    if ([string]$foreignBefore -cne [string]$foreignAfter -or $staged.Count -ne 1 -or $staged[0] -cne 'foreign.txt') { throw 'MODULE_COMMIT_PRESERVES_INDEX: unrelated staged work changed.' }
    $checks++
    $head = Invoke-Git @('-C', $fixture, 'rev-parse', 'HEAD') 'read head'
    $repeated = $true; $repeatSucceeded = $false
    try { $repeated = Invoke-ModulePayloadCommit $fixture 'modules-dist/broker.zip' 'synthetic repeated publication' 'synthetic body'; $repeatSucceeded = $true } catch { }
    $after = Invoke-Git @('-C', $fixture, 'rev-parse', 'HEAD') 'reread head'
    if (-not $repeatSucceeded -or $repeated -or [string]$head -cne [string]$after) { throw 'MODULE_COMMIT_NOOP: an unchanged payload must continue without a commit.' }
    $checks++
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved) -cne $parent -or -not [IO.Path]::GetFileName($resolved).StartsWith('dac-module-commit-')) { throw 'Synthetic cleanup scope mismatch.' }
    if (@(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) { throw 'Synthetic cleanup refused a link.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    if (Test-Path -LiteralPath $resolved) { throw 'Synthetic repository remains after cleanup.' }
}
Write-Output ('SUMMARY: Test-ModulePublishCommit passed ' + $checks + ' checks; synthetic repository removed.')
