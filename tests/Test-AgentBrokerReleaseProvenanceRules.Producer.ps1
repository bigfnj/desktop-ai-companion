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

# Supported current call shapes, not a general PowerShell evaluator. Each binding names its real
# branch and the operation that must remain after it. Publishing and compression are never invoked.
function Get-OperationalBindings([string]$PublishSource, [string]$ZipSource) {
    $errors = $null; $tokens = $null
    $publishAst = [Management.Automation.Language.Parser]::ParseInput($PublishSource, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'PROVENANCE_RULES_PUBLISH_SOURCE_PARSE' }
    $zipAst = [Management.Automation.Language.Parser]::ParseInput($ZipSource, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'PROVENANCE_RULES_ZIP_SOURCE_PARSE' }
    function Commands($Root, [string]$Name) {
        @($Root.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq $Name }, $true))
    }
    function Statement($Call) {
        if ($null -eq $Call -or $Call.Parent -isnot [Management.Automation.Language.PipelineAst] -or
            $Call.Parent.PipelineElements.Count -ne 1 -or $Call.Redirections.Count -ne 0) { return $null }
        $value = $Call.Parent
        if ($value.Parent -is [Management.Automation.Language.AssignmentStatementAst]) { $value = $value.Parent }
        return $value
    }
    function Branch($Call, [string]$Condition, $Container) {
        $statement = Statement $Call
        if ($null -eq $statement) { return $null }
        $body = $statement.Parent; $owner = $body.Parent
        if ($owner -isnot [Management.Automation.Language.IfStatementAst] -or $owner.Clauses.Count -ne 1 -or
            $null -ne $owner.ElseClause -or -not [object]::ReferenceEquals($owner.Clauses[0].Item2, $body) -or
            -not [object]::ReferenceEquals($owner.Parent, $Container) -or
            ($owner.Clauses[0].Item1.Extent.Text -replace '\s', '') -cne $Condition) { return $null }
        return $owner
    }
    function CallContainer($Call) {
        $statement = Statement $Call
        if ($null -eq $statement -or $statement.Parent.Parent -isnot [Management.Automation.Language.IfStatementAst]) { return $null }
        return $statement.Parent.Parent.Parent
    }
    $zipInvocation = @($publishAst.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$zip' -and
        ($_.Right.Extent.Text -replace '\s', '') -ceq "&(Join-Path`$PSScriptRoot'New-ModuleDistZip.ps1')@zipArguments"
    })
    $commitCalls = @(Commands $publishAst 'Invoke-ModulePayloadCommit')
    $moves = @(Commands $zipAst 'Move-Item' | Where-Object {
        ($_.Extent.Text -replace '\s', '') -ceq 'Move-Item-LiteralPath$temporaryPath-Destination$destinationFull-Force'
    })
    $streams = @(Commands $zipAst 'New-Object' | Where-Object { $_.CommandElements.Count -gt 1 -and $_.CommandElements[1].Extent.Text -ceq 'IO.FileStream' })
    $commit = if ($commitCalls.Count -eq 1) { Statement $commitCalls[0] } else { $null }
    $commitIf = if ($null -ne $commit) { $commit.Parent.Parent } else { $null }
    $commitBody = if ($commitIf -is [Management.Automation.Language.IfStatementAst] -and
        ($commitIf.Clauses[0].Item1.Extent.Text -replace '\s', '') -ceq '$Commit' -and
        $commitIf.Parent.Parent -is [Management.Automation.Language.TryStatementAst]) { $commitIf.Parent } else { $null }
    $move = if ($moves.Count -eq 1) { Statement $moves[0] } else { $null }
    $moveBody = if ($null -ne $move -and $move.Parent.Parent -is [Management.Automation.Language.TryStatementAst]) { $move.Parent } else { $null }
    $compression = if ($streams.Count -eq 1) { $streams[0] } else { $null }
    $authority = @(Commands $publishAst 'Assert-AgentBrokerPublishAuthority')
    $outputs = @(Commands $zipAst 'Test-AgentBrokerReleaseOutput')
    $zipChecks = @(Commands $zipAst 'Assert-AgentBrokerReleaseZip')
    $definitions = @(
        @{ label='PUBLISH_BEFORE_ZIP'; calls=@($authority | Where-Object { [object]::ReferenceEquals((CallContainer $_), $publishAst.EndBlock) }); condition="`$moduleId-ceq'agentbroker'"; container=$publishAst.EndBlock; text='Assert-AgentBrokerPublishAuthority'; before=if ($zipInvocation.Count -eq 1) { $zipInvocation[0] } else { $null }; after=$null; source='publish' },
        @{ label='PUBLISH_BEFORE_COMMIT'; calls=@($authority | Where-Object { $null -ne $commitBody -and [object]::ReferenceEquals((CallContainer $_), $commitBody) }); condition="`$moduleId-ceq'agentbroker'"; container=$commitBody; text='Assert-AgentBrokerPublishAuthority$zipPath'; before=$commitIf; after=$null; source='publish' },
        @{ label='OUTPUT_BEFORE_COMPRESSION'; calls=@($outputs | Where-Object { [object]::ReferenceEquals((CallContainer $_), $zipAst.EndBlock) }); condition='$boundAgentBroker'; container=$zipAst.EndBlock; text='Test-AgentBrokerReleaseOutput$AgentBrokerOutputReceipt$AgentBrokerOutputReceiptSha256$sourceFull'; before=$compression; after=$null; source='zip' },
        @{ label='OUTPUT_BEFORE_MOVE'; calls=@($outputs | Where-Object { $null -ne $moveBody -and [object]::ReferenceEquals((CallContainer $_), $moveBody) }); condition='$boundAgentBroker'; container=$moveBody; text='Test-AgentBrokerReleaseOutput$AgentBrokerOutputReceipt$AgentBrokerOutputReceiptSha256$sourceFull'; before=$move; after=$compression; source='zip' },
        @{ label='ZIP_BEFORE_MOVE'; calls=$zipChecks; condition='$boundAgentBroker'; container=$moveBody; text='Assert-AgentBrokerReleaseZip$temporaryPath$boundRecord$ExpectedAgentBrokerZipSha256'; before=$move; after=$compression; source='zip' }
    )
    $result = @()
    foreach ($definition in $definitions) {
        $call = if ($definition.calls.Count -eq 1) { $definition.calls[0] } else { $null }
        $branch = if ($null -ne $call -and $null -ne $definition.container) { Branch $call $definition.condition $definition.container } else { $null }
        $valid = $null -ne $branch -and $null -ne $definition.before -and
            ($call.Extent.Text -replace '\s', '') -ceq $definition.text -and
            $branch.Extent.EndOffset -lt $definition.before.Extent.StartOffset -and
            ($null -eq $definition.after -or $branch.Extent.StartOffset -gt $definition.after.Extent.StartOffset)
        if ($definition.label -ceq 'OUTPUT_BEFORE_COMPRESSION') { $valid = $valid -and (Statement $call).Extent.Text.TrimStart().StartsWith('$boundRecord = ', [StringComparison]::Ordinal) }
        if ($definition.label -ceq 'OUTPUT_BEFORE_MOVE') { $valid = $valid -and (Statement $call).Extent.Text.TrimStart().StartsWith('$null = ', [StringComparison]::Ordinal) }
        $result += [pscustomobject]@{ Label=$definition.label; Valid=[bool]$valid; Call=$call; Statement=if ($null -ne $call) { Statement $call } else { $null }; Branch=$branch; Barrier=if ($definition.label -ceq 'OUTPUT_BEFORE_COMPRESSION' -and $null -ne $moveBody) { $moveBody.Parent } else { $definition.before }; Source=$definition.source }
    }
    return $result
}
function Replace-OperationalNode([string]$Source, $Node, [string]$After) {
    $length = $Node.Extent.EndOffset - $Node.Extent.StartOffset
    if ($Source.Substring($Node.Extent.StartOffset, $length) -cne $Node.Extent.Text) { throw 'PROVENANCE_RULES_OPERATIONAL_ANCHOR' }
    return $Source.Remove($Node.Extent.StartOffset, $length).Insert($Node.Extent.StartOffset, $After)
}
$publishBytes = [IO.File]::ReadAllBytes((Join-Path $ApiDirectory 'New-ModulePublish.ps1'))
$zipBytesForWiring = [IO.File]::ReadAllBytes((Join-Path $ApiDirectory 'New-ModuleDistZip.ps1'))
$publishSource = $utf8.GetString($publishBytes).TrimStart([char]0xfeff)
$zipSource = $utf8.GetString($zipBytesForWiring).TrimStart([char]0xfeff)
Write-Output ('WIRING_SOURCE packaging/New-ModulePublish.ps1 sha256 ' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publishBytes)))
Write-Output ('WIRING_SOURCE packaging/New-ModuleDistZip.ps1 sha256 ' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($zipBytesForWiring)))
$bindings = @(Get-OperationalBindings $publishSource $zipSource)
foreach ($binding in $bindings) { Require $binding.Valid ('WIRING_' + $binding.Label) }
foreach ($binding in $bindings) {
    foreach ($kind in @('OMITTED', 'CONDITIONAL')) {
        $source = if ($binding.Source -ceq 'publish') { $publishSource } else { $zipSource }
        $replacement = if ($kind -ceq 'OMITTED') { '' } else { 'if ($false) { ' + $binding.Statement.Extent.Text + ' }' }
        $variant = Replace-OperationalNode $source $binding.Statement $replacement
        $observed = if ($binding.Source -ceq 'publish') { @(Get-OperationalBindings $variant $zipSource) } else { @(Get-OperationalBindings $publishSource $variant) }
        Require ((@($observed | Where-Object { -not $_.Valid } | ForEach-Object Label) -join ',') -ceq $binding.Label) ('WIRING_' + $binding.Label + '_' + $kind + '_CONTROL')
    }
}
# Move whole legitimate branches after their real operation, keeping their original condition.
foreach ($label in @('PUBLISH_BEFORE_ZIP', 'PUBLISH_BEFORE_COMMIT', 'OUTPUT_BEFORE_COMPRESSION', 'OUTPUT_BEFORE_MOVE')) {
    $binding = @($bindings | Where-Object { $_.Label -ceq $label })[0]
    $source = if ($binding.Source -ceq 'publish') { $publishSource } else { $zipSource }
    $variant = $source.Insert($binding.Barrier.Extent.EndOffset, ("`n" + $binding.Branch.Extent.Text))
    $variant = Replace-OperationalNode $variant $binding.Branch ''
    $observed = if ($binding.Source -ceq 'publish') { @(Get-OperationalBindings $variant $zipSource) } else { @(Get-OperationalBindings $publishSource $variant) }
    $expected = if ($label -ceq 'OUTPUT_BEFORE_MOVE') { 'OUTPUT_BEFORE_MOVE,ZIP_BEFORE_MOVE' } else { $label }
    Require ((@($observed | Where-Object { -not $_.Valid } | ForEach-Object Label) -join ',') -ceq $expected) ('WIRING_' + $label + '_LATE_CONTROL')
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if ($tempRoot.Length -gt [IO.Path]::GetPathRoot($tempRoot).Length) { $tempRoot = $tempRoot.TrimEnd('\', '/') }
try {
    for ($walk = $tempRoot; $walk; $walk = [IO.Path]::GetDirectoryName($walk)) {
        if (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PROVENANCE_RULES_FIXTURE_ENVIRONMENT_REPARSE' }
        foreach ($leaf in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'Directory.Build.rsp', '.editorconfig', '.globalconfig')) {
            if ([IO.File]::Exists((Join-Path $walk $leaf))) { throw 'PROVENANCE_RULES_FIXTURE_ENVIRONMENT_IMPLICIT_INPUT' }
        }
    }
} catch {
    if ($_.Exception.Message.StartsWith('PROVENANCE_RULES_FIXTURE_ENVIRONMENT_', [StringComparison]::Ordinal)) { throw }
    throw 'PROVENANCE_RULES_FIXTURE_ENVIRONMENT_UNREADABLE'
}
$owned = Join-Path $tempRoot ('provenance-api-rules-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $owned 'repo'
$apiRoot = Join-Path $owned 'api'
$loaded = @{}
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    [void][IO.Directory]::CreateDirectory($apiRoot)
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

    $manifestBytes = [IO.File]::ReadAllBytes($manifest)
    foreach ($field in @('schema', 'sourceContentSha256', 'parentCommit', 'identity')) {
        $changed = ConvertFrom-Json ($utf8.GetString($manifestBytes))
        switch ($field) {
            'schema' { $changed.schema = 2 }
            'sourceContentSha256' { $changed.sourceContentSha256 = '0' * 64 }
            'parentCommit' { $changed.parentCommit = '0' * 40 }
            'identity' { $changed.identity.settings.configuration = 'Changed' }
        }
        try {
            [IO.File]::WriteAllText($manifest, ($changed | ConvertTo-Json -Depth 14), $utf8)
            Require (Refuses { Read-AgentBrokerReleaseManifest $manifest $sourceSha $parent '' } 'PROVENANCE_SOURCE_CHANGED') ('SOURCE_RECORD_' + $field.ToUpperInvariant())
        } finally { [IO.File]::WriteAllBytes($manifest, $manifestBytes) }
        $null = Read-AgentBrokerReleaseManifest $manifest $sourceSha $parent ''
        Require ((Get-ABPFileHash $manifest) -ceq (Get-ABPHash $manifestBytes)) ('SOURCE_RECORD_' + $field.ToUpperInvariant() + '_RESTORED')
    }
    foreach ($authority in @(
        @{label='MANIFEST';path=$manifest},
        @{label='METADATA_READER';path=(Join-Path $apiRoot 'AgentBrokerReleasePayloadMetadata.cs')},
        @{label='MODULE_PUBLISH';path=(Join-Path $apiRoot 'New-ModulePublish.ps1')},
        @{label='MODULE_DIST_ZIP';path=(Join-Path $apiRoot 'New-ModuleDistZip.ps1')}
    )) {
        $authorityBytes = [IO.File]::ReadAllBytes($authority.path)
        try {
            # Whitespace keeps parsed manifest/tool semantics unchanged, isolating byte authority.
            [IO.File]::WriteAllBytes($authority.path, [byte[]]($authorityBytes + [byte]10))
            Require (Refuses { Test-AgentBrokerReleaseOutput $receipt.OutputReceipt $receipt.OutputReceiptSha256 $payload } 'PROVENANCE_OUTPUT_AUTHORITY_CHANGED') ('TOOL_AUTHORITY_' + $authority.label + '_CHANGED')
        } finally { [IO.File]::WriteAllBytes($authority.path, $authorityBytes) }
        $restored = Test-AgentBrokerReleaseOutput $receipt.OutputReceipt $receipt.OutputReceiptSha256 $payload
        Require ($restored.inventorySha256 -ceq $accepted.inventorySha256 -and
            (Get-ABPFileHash $authority.path) -ceq (Get-ABPHash $authorityBytes)) ('TOOL_AUTHORITY_' + $authority.label + '_RESTORED')
    }
    foreach ($leaf in $apiFiles) {
        Require ((Get-ABPFileHash (Join-Path $ApiDirectory $leaf)) -ceq $loaded[$leaf]) ('ORIGINAL_UNCHANGED_' + $leaf)
    }
} finally {
    $full = [IO.Path]::GetFullPath($owned)
    if ([IO.Path]::GetDirectoryName($full) -cne $tempRoot -or
        [IO.Path]::GetFileName($full) -cnotmatch '^provenance-api-rules-[0-9a-f]{32}\z') { throw 'PROVENANCE_RULES_CLEANUP_SCOPE' }
    if ([IO.Directory]::Exists($full)) {
        if (([IO.File]::GetAttributes($tempRoot) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PROVENANCE_RULES_CLEANUP_REPARSE' }
        if (([IO.File]::GetAttributes($full) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PROVENANCE_RULES_CLEANUP_REPARSE' }
        foreach ($entry in Get-ChildItem -LiteralPath $full -Recurse -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PROVENANCE_RULES_CLEANUP_REPARSE' }
        }
        [IO.Directory]::Delete($full, $true)
    }
    if ([IO.Directory]::Exists($full)) { throw 'PROVENANCE_RULES_FIXTURE_REMAINS' }
}
Write-Output ('PROVENANCE_RULES=' + $checks + ' checks passed; no SDK build; owned fixture removed')
