#requires -Version 7
<#
SDK-free refusal controls for actual AgentBroker provenance APIs. The public b234fcd0 source/payload
is the fixed fixture baseline, so an unrelated future production edit does not silently redefine it.
Current library/metadata/operational scripts execute from a separate owned API directory. The identity
root remains the fixed public source fixture, so testing a changed API does not forge new payload stamps.
No dotnet build/publish, Prepare CLI/Git-parent check, installation or runtime acceptance is claimed.
This covers source identity, output inventory, ZIP contents and tool authority. It does not execute
New-ModulePublish's Git mutations or New-ModuleDistZip's compression route.
Local history must contain the fixed public baseline; no fetch is attempted. Hosted CI uses fetch-depth 0.
#>
[CmdletBinding()]
param([string]$ApiDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $PSBoundParameters.ContainsKey('ApiDirectory')) { $ApiDirectory = Join-Path $repoRoot 'packaging' }
$ApiDirectory = [IO.Path]::GetFullPath($ApiDirectory)
$baseline = 'b234fcd0bda619fb34b4de438d12bc6ac164829d'
$baselineZipSha = 'FB1D0BE95579AF9589C9D9D7019A51DF03914A9E449C6B582D0237911006A747'
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = 0
function Require([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw ('PROVENANCE_RULES_' + $Label) }
    $script:checks++
    Write-Output ('PASS PROVENANCE_RULES_' + $Label)
}
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { $null = & $Action; return $false }
    catch { return $_.Exception.Message -ceq $Expected }
}
function Read-GitFixture([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new('git')
    $info.WorkingDirectory = $repoRoot; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $memory = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($memory); $process.WaitForExit(); $null = $errorRead.Result
            if ($process.ExitCode -ne 0) { throw 'PROVENANCE_RULES_BASELINE_GIT_READ' }
            return ,$memory.ToArray()
        } finally { $memory.Dispose() }
    } finally { $process.Dispose() }
}
function Expand-OwnedZip([byte[]]$Bytes, [string]$Destination) {
    $prefix = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $memory = [IO.MemoryStream]::new($Bytes, $false)
    $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        foreach ($entry in $zip.Entries) {
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'PROVENANCE_RULES_FIXTURE_ENTRY_SCOPE' }
            if ($entry.FullName.EndsWith('/')) { [void][IO.Directory]::CreateDirectory($path); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $input = $entry.Open(); $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose(); $memory.Dispose() }
}

$owned = Join-Path $PSScriptRoot ('provenance-api-rules-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $owned 'repo'
$apiRoot = Join-Path $owned 'api'
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($apiRoot)
$loaded = @{}
try {
    try { $null = Read-GitFixture @('cat-file', '-e', ($baseline + '^{commit}')) }
    catch { throw 'PROVENANCE_RULES_BASELINE_HISTORY_REQUIRED: local history must contain b234fcd0; no fetch attempted' }
    # Archive only fixed public source paths. No checkout or shared Git object store is created.
    $prefixes = @('modules/AgentBroker/', 'src/DesktopAICompanion.Contracts/', 'src/DesktopAICompanion.ModuleKit/',
        'src/AgentBroker.Core/', 'tools/AgentBroker.Worker/', 'tools/AgentBroker.CodexDesktopAdapter/',
        'tools/AgentBroker.ClaudeAdapter/', 'tools/AgentBroker.Pins/', 'shared/AgentBroker/', 'packaging/AgentBrokerNotices/')
    $names = @('global.json', 'ProductVersion.props', 'modules/Directory.Build.props', 'src/Directory.Build.props',
        'tools/Directory.Build.props', 'packaging/AgentBrokerReleaseProvenance.ps1', 'packaging/AgentBrokerReleaseOutput.ps1',
        'packaging/AgentBrokerReleasePayloadMetadata.cs', 'packaging/AgentBrokerReleaseProvenance.props',
        'packaging/Publish-AgentBrokerWorker.ps1', 'packaging/StagingPathSafety.ps1')
    $tree = $utf8.GetString((Read-GitFixture @('ls-tree', '-r', '--name-only', $baseline))) -split "`n"
    $selected = @($tree | Where-Object {
        $path = $_.TrimEnd("`r"); $include = $path -cin $names
        foreach ($prefix in $prefixes) { if ($path.StartsWith($prefix, [StringComparison]::Ordinal)) { $include = $true } }
        $include
    })
    if ($selected.Count -lt 179 -or @($selected | Where-Object { $_ -cnotmatch '^[A-Za-z0-9._/-]+\z' }).Count) { throw 'PROVENANCE_RULES_BASELINE_SOURCE_SET' }
    Expand-OwnedZip (Read-GitFixture (@('archive', '--format=zip', $baseline, '--') + $selected)) $fixture

    $apiFiles = @('AgentBrokerReleaseProvenance.ps1', 'AgentBrokerReleaseOutput.ps1', 'AgentBrokerReleasePayloadMetadata.cs',
        'New-ModulePublish.ps1', 'New-ModuleDistZip.ps1')
    foreach ($leaf in $apiFiles) {
        $path = Join-Path $ApiDirectory $leaf
        $bytes = [IO.File]::ReadAllBytes($path)
        $loaded[$leaf] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        [IO.File]::WriteAllBytes((Join-Path $apiRoot $leaf), $bytes)
        Write-Output ('LOADED packaging/' + $leaf + ' sha256 ' + $loaded[$leaf])
    }
    . (Join-Path $apiRoot 'AgentBrokerReleaseProvenance.ps1') -Mode Library
    # Actual API functions operate on a fixed source dependency fixture, not this test's checkout.
    $script:ABPRepoRoot = $fixture
    $identity = Get-ABPIdentity
    $sourceSha = Get-ABPHash (Get-ABPIdentityBytes $identity)
    Require ($identity.sources.Count -eq 179) 'PUBLIC_SOURCE_CLOSURE_179'
    $zipBytes = Read-GitFixture @('cat-file', 'blob', ($baseline + ':modules-dist/agentbroker.zip'))
    Require ((Get-ABPHash $zipBytes) -ceq $baselineZipSha) 'PUBLIC_PAYLOAD_BYTES'
    $payload = Get-ABPCanonicalPayloadRoot
    [void][IO.Directory]::CreateDirectory($payload)
    Expand-OwnedZip $zipBytes $payload

    # Read only the public stamp blobs through the actual committed-payload helper. No PDB names or source blobs.
    $tokens = $null; $errors = $null
    $stampAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'packaging/Test-ModulePublishFreshness.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'PROVENANCE_RULES_STAMP_READER_SOURCE' }
    foreach ($name in @('Find-AgentBrokerStampValues', 'Get-GitSuffixVersionBlobCount', 'Get-AgentBrokerStampFacts')) {
        $function = @($stampAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true))
        if ($function.Count -ne 1) { throw 'PROVENANCE_RULES_STAMP_READER_FUNCTION' }
        . ([scriptblock]::Create($function[0].Extent.Text))
    }
    $stamps = Get-AgentBrokerStampFacts ([IO.File]::ReadAllBytes((Join-Path $payload 'AgentBroker.dll')))
    Require ($stamps.SourceValues.Count -eq 1 -and $stamps.SourceValues[0] -ceq $sourceSha -and $stamps.ParentValues.Count -eq 1) 'PUBLIC_SOURCE_STAMP_MATCH'
    $parent = $stamps.ParentValues[0]
    $manifestRoot = Get-ABPOutputRoot; [void][IO.Directory]::CreateDirectory($manifestRoot)
    $manifest = Join-Path $manifestRoot ($sourceSha + '.json')
    $record = [ordered]@{ schema = 1; sourceContentSha256 = $sourceSha; parentCommit = $parent; identity = $identity }
    [IO.File]::WriteAllText($manifest, ($record | ConvertTo-Json -Depth 14), $utf8)
    $null = Read-AgentBrokerReleaseManifest $manifest $sourceSha $parent ''
    Require $true 'SOURCE_HEALTHY'
    $receipt = New-AgentBrokerReleaseOutputReceipt $manifest $sourceSha $parent
    $accepted = Test-AgentBrokerReleaseOutput $receipt.OutputReceipt $receipt.OutputReceiptSha256 $payload
    Require ($accepted.fileCount -eq 357 -and $accepted.sourceContentSha256 -ceq $sourceSha) 'OUTPUT_HEALTHY'
    $zipPath = Join-Path $owned 'accepted.zip'; [IO.File]::WriteAllBytes($zipPath, $zipBytes)
    Assert-AgentBrokerReleaseZip $zipPath $accepted $baselineZipSha
    Require $true 'ZIP_HEALTHY'

    $changedSource = Join-Path $fixture 'modules/AgentBroker/AgentBrokerModule.cs'
    $sourceBytes = [IO.File]::ReadAllBytes($changedSource)
    try {
        [IO.File]::WriteAllBytes($changedSource, [byte[]]($sourceBytes + [byte]10))
        Require (Refuses { Read-AgentBrokerReleaseManifest $manifest $sourceSha $parent '' } 'PROVENANCE_SOURCE_CHANGED') 'SOURCE_CHANGED'
    } finally { [IO.File]::WriteAllBytes($changedSource, $sourceBytes) }
    $null = Read-AgentBrokerReleaseManifest $manifest $sourceSha $parent ''
    Require ((Get-ABPHash (Get-ABPIdentityBytes (Get-ABPIdentity))) -ceq $sourceSha) 'SOURCE_RESTORED'

    $changedOutput = Join-Path $payload 'AgentBroker.deps.json'; $outputBytes = [IO.File]::ReadAllBytes($changedOutput)
    try {
        [IO.File]::WriteAllBytes($changedOutput, [byte[]]($outputBytes + [byte]10))
        Require (Refuses { Test-AgentBrokerReleaseOutput $receipt.OutputReceipt $receipt.OutputReceiptSha256 $payload } 'PROVENANCE_OUTPUT_CHANGED') 'OUTPUT_CHANGED'
    } finally { [IO.File]::WriteAllBytes($changedOutput, $outputBytes) }
    Require ((Get-ABPFileHash $changedOutput) -ceq (Get-ABPHash $outputBytes)) 'OUTPUT_RESTORED'

    $extraZip = Join-Path $owned 'extra.zip'; [IO.File]::WriteAllBytes($extraZip, $zipBytes)
    $archive = [IO.Compression.ZipFile]::Open($extraZip, [IO.Compression.ZipArchiveMode]::Update)
    try { [void]$archive.CreateEntry('unexpected.txt') } finally { $archive.Dispose() }
    Require (Refuses { Assert-AgentBrokerReleaseZip $extraZip $accepted } 'PROVENANCE_ZIP_CHANGED') 'ZIP_EXTRA_ENTRY'
    Assert-AgentBrokerReleaseZip $zipPath $accepted $baselineZipSha
    Require $true 'ZIP_RESTORED'

    $changedTool = Join-Path $apiRoot 'New-ModuleDistZip.ps1'; $toolBytes = [IO.File]::ReadAllBytes($changedTool)
    try {
        [IO.File]::WriteAllBytes($changedTool, [byte[]]($toolBytes + [byte]10))
        Require (Refuses { Test-AgentBrokerReleaseOutput $receipt.OutputReceipt $receipt.OutputReceiptSha256 $payload } 'PROVENANCE_OUTPUT_AUTHORITY_CHANGED') 'TOOL_AUTHORITY_CHANGED'
    } finally { [IO.File]::WriteAllBytes($changedTool, $toolBytes) }
    $restored = Test-AgentBrokerReleaseOutput $receipt.OutputReceipt $receipt.OutputReceiptSha256 $payload
    Require ($restored.inventorySha256 -ceq $accepted.inventorySha256 -and (Get-ABPFileHash $changedTool) -ceq $loaded['New-ModuleDistZip.ps1']) 'TOOL_AND_OUTPUT_RESTORED'
    foreach ($leaf in $apiFiles) {
        Require ((Get-ABPFileHash (Join-Path $ApiDirectory $leaf)) -ceq $loaded[$leaf]) ('ORIGINAL_UNCHANGED_' + $leaf)
    }
} finally {
    $full = [IO.Path]::GetFullPath($owned)
    $prefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar + 'provenance-api-rules-'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'PROVENANCE_RULES_CLEANUP_SCOPE' }
    if ([IO.Directory]::Exists($full)) {
        foreach ($entry in Get-ChildItem -LiteralPath $full -Recurse -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PROVENANCE_RULES_CLEANUP_REPARSE' }
        }
        [IO.Directory]::Delete($full, $true)
    }
    if ([IO.Directory]::Exists($full)) { throw 'PROVENANCE_RULES_FIXTURE_REMAINS' }
}
Write-Output ('PROVENANCE_RULES=' + $checks + ' checks passed; no SDK build; owned fixture removed')
