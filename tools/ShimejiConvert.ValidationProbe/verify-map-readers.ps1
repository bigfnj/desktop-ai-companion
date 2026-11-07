#requires -Version 5.1
[CmdletBinding()]
param([switch]$Mutate, [string[]]$Only=@('*'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
# Reuse the converter's explicit, protected-path-checked closure, including build-task read dependencies.
$prepared = @(& powershell.exe -NoProfile -File (Join-Path $repo 'tools\ShimejiConvert\verify-source-ir.ps1') -PrepareOnly -ScratchRoot $ScratchRoot)
if ($LASTEXITCODE -ne 0) { throw 'MAP_READERS_SNAPSHOT_FAILED' }
$paths = @($prepared | Where-Object { $_ -like 'Source IR evidence: *' })
if ($paths.Count -ne 1) { throw 'MAP_READERS_SNAPSHOT_MISSING' }
$evidence = $paths[0].Substring('Source IR evidence: '.Length)
$snapshot = Join-Path $evidence 'source'
$inputs = New-Object 'Collections.Generic.List[object]'
foreach ($row in (Get-Content -LiteralPath (Join-Path $evidence 'snapshot.json') -Raw | ConvertFrom-Json)) { $inputs.Add($row) }
function Add-Input([string]$Relative) {
    $source = [IO.Path]::GetFullPath((Join-Path $repo $Relative))
    $destination = [IO.Path]::GetFullPath((Join-Path $snapshot $Relative))
    if (-not $source.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $destination.StartsWith($snapshot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'MAP_READERS_COPY_OUTSIDE_ROOT' }
    $current = $source
    while ($current -ne $repo) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'MAP_READERS_REPARSE_INPUT' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($source))
    $inputs.Add([pscustomobject]@{ Source=$source; Copy=$destination; Sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() })
}
foreach ($name in @('Program.cs', 'Run.ps1', 'ShimejiConvert.ValidationProbe.csproj')) { Add-Input ('tools\ShimejiConvert.ValidationProbe\' + $name) }
$fixture = Join-Path $repo 'tools\ShimejiConvert\fixtures\geometry\pack'
foreach ($file in Get-ChildItem -LiteralPath $fixture -File -Recurse) { Add-Input $file.FullName.Substring($repo.Length + 1) }
$feed = Join-Path $evidence 'empty-feed'
[void][IO.Directory]::CreateDirectory($feed)
$config = Join-Path $snapshot 'NuGet.Config'
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="offline" value="' + [Security.SecurityElement]::Escape($feed) + '"/></packageSources></configuration>', $utf8)
$env:DOTNET_CLI_HOME = Join-Path $evidence 'cli-home'
$env:NUGET_PACKAGES = Join-Path $evidence 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $evidence 'http-cache'
$env:NuGetAudit = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT = '1'
$env:DESKTOP_AI_COMPANION_DATA_ROOT = Join-Path $evidence 'data'
$env:TEMP = $evidence
$env:TMP = $evidence
$results = New-Object 'Collections.Generic.List[object]'
$summary = [ordered]@{ status='running'; evidence=$evidence; snapshot=$snapshot; mutations=$results; offlineFeed=$feed }
$summaryPath = Join-Path $evidence 'map-readers-results.json'
function Save-Summary { [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 12), $utf8) }
function Invoke-Child([string]$Label, [string]$Executable, [string[]]$Arguments) {
    $quoted = @()
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"') -or $argument.Contains("`n") -or $argument.EndsWith('\')) { throw 'MAP_READERS_ARGUMENT_UNSUPPORTED' }
        $quoted += '"' + $argument + '"'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Executable; $start.Arguments=$quoted -join ' '; $start.WorkingDirectory=$snapshot
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process = New-Object Diagnostics.Process
    $process.StartInfo=$start
    try {
        [void]$process.Start()
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) { $process.Kill(); throw ('MAP_READERS_TIMEOUT: ' + $Label) }
        $output=$stdout.Result + $stderr.Result
        [IO.File]::WriteAllText((Join-Path $evidence ($Label + '.log')), $output, $utf8)
        return [pscustomobject]@{ exitCode=$process.ExitCode; output=$output; command=$Executable; arguments=$Arguments; log=(Join-Path $evidence ($Label + '.log')) }
    } finally { $process.Dispose() }
}
$project=Join-Path $snapshot 'tools\ShimejiConvert\ShimejiConvert.csproj'
$artifact=Join-Path $snapshot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.Engine.dll'
$cli=Join-Path $snapshot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.exe'
function Build-Converter([string]$Label) {
    $began=[DateTime]::UtcNow
    $run=Invoke-Child $Label 'dotnet' @('build', $project, '-c', 'Release', '--no-restore', '--no-incremental', '--nologo', '-v:minimal', '-p:UseSharedCompilation=false', '-nodeReuse:false', '-maxcpucount:1')
    if ($run.exitCode -ne 0 -or -not [IO.File]::Exists($artifact) -or [IO.File]::GetLastWriteTimeUtc($artifact) -lt $began) { throw ('MAP_READERS_BUILD_FAILED: ' + $run.log) }
    return [pscustomobject]@{ sha256=(Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant(); lastWriteUtc=[IO.File]::GetLastWriteTimeUtc($artifact); execution=$run }
}
function Run-Pack([string]$Label, [bool]$ExpectedPass) {
    $group=if ($ExpectedPass) { 'pack-conversion' } else { 'pack-conversion/complete-pack-startup-and-geometry' }
    $run=Invoke-Child $Label $cli @('selftest', $group)
    $lines=@($run.output -split '\r?\n' | ForEach-Object { $_.Trim() })
    if ($lines -notcontains 'pack-conversion self-test: executing public API witnesses' -or $lines -notcontains 'SELFTEST-COUNT: 1') { throw 'MAP_READERS_PACK_EXECUTION_MISSING' }
    if ($ExpectedPass) {
        if ($run.exitCode -ne 0 -or $lines -notcontains 'SELFTEST PASS' -or @($lines | Where-Object { $_ -like '*pack-conversion PASS *' }).Count -lt 28) { throw ('MAP_READERS_PACK_FAILED: ' + $run.log) }
    } else {
        $fails=@($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' })
        if ($run.exitCode -ne 1 -or $fails.Count -ne 1 -or $fails[0] -notlike 'FAIL PackConversionSelfTest/complete-pack-startup-and-geometry:*') { throw ('MAP_READERS_PACK_MUTATION_MISSED: ' + $run.log) }
    }
    return $run
}
$xml=Join-Path $evidence 'geometry\G\animations.xml'
function Run-Probe([string]$Label, [string[]]$ExpectedFailures=@()) {
    $fileLabel=[regex]::Replace($Label, '[^A-Za-z0-9_.-]', '_')
    $output=Join-Path $evidence ('probe-' + $fileLabel)
    $run=Invoke-Child ('probe-' + $fileLabel) 'powershell.exe' @('-NoProfile', '-File', (Join-Path $snapshot 'tools\ShimejiConvert.ValidationProbe\Run.ps1'), '-HostSourceRoot', $snapshot, '-XmlPath', $xml, '-OutputRoot', $output)
    $record=Get-Content -LiteralPath (Join-Path $output 'results.json') -Raw | ConvertFrom-Json
    $witness=Get-Content -LiteralPath (Join-Path $output 'witness.json') -Raw | ConvertFrom-Json
    $fails=@($record.results | Where-Object { -not $_.pass } | ForEach-Object { $_.name })
    if (($fails -join ',') -cne ($ExpectedFailures -join ',') -or $run.exitCode -ne $(if ($ExpectedFailures.Count) { 1 } else { 0 }) -or
        $witness.buildExit -ne 0 -or $record.artifactSha256 -cne $witness.artifactSha256) { throw ('MAP_READERS_PROBE_WITNESS_FAILED: ' + $run.log) }
    return [pscustomobject]@{ sha256=$witness.artifactSha256; lastWriteUtc=$witness.artifactLastWriteUtc; failures=$fails; evidence=$output; execution=$run }
}
$mutations=@(
    @{ name='pack-reader-v1'; kind='pack'; path='tools\ShimejiConvert.Engine\Shimeji\PackConversionSelfTest.cs'; old='== "shimeji-parity-map/2"'; new='== "shimeji-parity-map/1"' },
    @{ name='pack-poses-empty'; kind='pack'; path='tools\ShimejiConvert.Engine\Emit\TickPackEmitter.cs'; old='poses = poseMap },'; new='poses = new Dictionary<string, object>() },' },
    @{ name='probe-accepts-v1'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='if (map.RootElement.GetProperty("schema").GetString() != "shimeji-parity-map/2")'; new='if (map.RootElement.GetProperty("schema").GetString() != "shimeji-parity-map/2" && map.RootElement.GetProperty("schema").GetString() != "shimeji-parity-map/1")'; fails=@('control-map-v1-schema') },
    @{ name='probe-duplicate'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='if (!keys.Add(key)) throw'; new='if (!keys.Add(key) && key == "__never__") throw'; fails=@('control-map-duplicate-pose-key') },
    @{ name='probe-surplus'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='if (keys.Count != 0) throw'; new='if (keys.Count < 0) throw'; fails=@('control-map-surplus-pose') },
    @{ name='probe-source-index'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='|| position != ordinal - 1)'; new='|| position < 0)'; fails=@('control-map-pose-source-index') },
    # N-bd-parity-recheck-04: these existing controls need actual guard-removal evidence, too.
    @{ name='bd-codex-parity-2: probe-missing-emitted-pose'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='if (!keys.Remove(key) && !placeholder)'; new='if (!keys.Remove(key) && !placeholder && key == "__never__")'; fails=@('control-map-missing-pose','control-map-false-stay-unmapped','control-map-false-turn-unmapped','control-map-placeholder-two-variants-unmapped','control-map-placeholder-two-poses-unmapped','control-map-placeholder-real-frame-unmapped','control-map-placeholder-ticks-unmapped','control-map-placeholder-dx-unmapped','control-map-placeholder-dy-unmapped') },
    @{ name='bd-codex-parity-2: probe-placeholder-class'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='if (!(type == "Animate" || (type == "Embedded" && cls == "Interact")) || variants.Length != 1'; new='if (variants.Length != 1'; fails=@('control-map-false-stay-unmapped','control-map-false-turn-unmapped') },
    # ---- burndown/codex-parity-2-review ----
    @{ name='bd-codex-parity-2-review: probe-placeholder-variant-count'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old=' || variants.Length != 1'; new=''; fails=@('control-map-placeholder-two-variants-unmapped') },
    @{ name='bd-codex-parity-2-review: probe-placeholder-pose-count'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='return poses.Length == 1 && '; new='return '; fails=@('control-map-placeholder-two-poses-unmapped') },
    @{ name='bd-codex-parity-2-review: probe-placeholder-frame'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='Attribute(poses[0], "frame", -2) == -1'; new='true'; fails=@('control-map-placeholder-real-frame-unmapped') },
    @{ name='bd-codex-parity-2-review: probe-placeholder-ticks'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='Attribute(poses[0], "ticks", 0) == 1'; new='true'; fails=@('control-map-placeholder-ticks-unmapped') },
    @{ name='bd-codex-parity-2-review: probe-placeholder-dx'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='Attribute(poses[0], "dx", 0) == 0'; new='true'; fails=@('control-map-placeholder-dx-unmapped') },
    @{ name='bd-codex-parity-2-review: probe-placeholder-dy'; kind='probe'; path='tools\ShimejiConvert.ValidationProbe\Program.cs'; old='Attribute(poses[0], "dy", 0) == 0'; new='true'; fails=@('control-map-placeholder-dy-unmapped') }
)
$selected=@($mutations | Where-Object { $name=$_.name; @($Only | Where-Object { $name -like $_ }).Count -ne 0 })
if ($Mutate -and $selected.Count -eq 0) { throw 'MAP_READERS_NO_MUTATIONS_SELECTED' }
$originals=@{}
$failure=$null
Save-Summary
try {
    $restore=Invoke-Child 'restore' 'dotnet' @('restore', $project, '--configfile', $config, '--source', $feed, '--packages', $env:NUGET_PACKAGES, '--disable-parallel', '--nologo', '-v:minimal', '-p:NuGetAudit=false')
    if ($restore.exitCode -ne 0) { throw ('MAP_READERS_RESTORE_FAILED: ' + $restore.log) }
    $summary.baselineArtifact=Build-Converter 'baseline-build'
    $summary.packExecution=Run-Pack 'pack-baseline' $true
    $export=Invoke-Child 'geometry-export' $cli @('paritypack', (Join-Path $snapshot 'tools\ShimejiConvert\fixtures\geometry\pack'), (Join-Path $evidence 'geometry'))
    if ($export.exitCode -ne 0 -or -not [IO.File]::Exists($xml)) { throw ('MAP_READERS_GEOMETRY_EXPORT_FAILED: ' + $export.log) }
    $summary.probeBaseline=Run-Probe 'baseline'
    Save-Summary
    if ($Mutate) {
        foreach ($mutation in $selected) {
            $path=Join-Path $snapshot $mutation.path
            if (-not $originals.ContainsKey($path)) { $originals[$path]=[IO.File]::ReadAllBytes($path) }
            $source=$utf8.GetString($originals[$path])
            if ([regex]::Matches($source, [regex]::Escape($mutation.old)).Count -ne 1) { throw ('MAP_READERS_MUTATION_ANCHOR: ' + $mutation.name) }
            try {
                [IO.File]::WriteAllText($path, $source.Replace($mutation.old, $mutation.new), $utf8)
                if ($mutation.kind -eq 'pack') {
                    $built=Build-Converter ($mutation.name + '-build')
                    $run=Run-Pack $mutation.name $false
                    $baseline=$summary.baselineArtifact
                } else {
                    $built=Run-Probe $mutation.name $mutation.fails
                    $run=$built.execution
                    $baseline=$summary.probeBaseline
                }
                if ($built.sha256 -ceq $baseline.sha256 -or [DateTime]$built.lastWriteUtc -le [DateTime]$baseline.lastWriteUtc) { throw ('MAP_READERS_FRESH_CHANGED_ARTIFACT_MISSING: ' + $mutation.name) }
                $results.Add([pscustomobject]@{ name=$mutation.name; status='FIRED'; artifact=$built; execution=$run })
                Write-Host ('FIRED ' + $mutation.name)
                Save-Summary
            } finally { [IO.File]::WriteAllBytes($path, $originals[$path]) }
        }
    }
    if ($Mutate) {
        $summary.restoredArtifact=Build-Converter 'restored-build'
        $summary.restoredPackExecution=Run-Pack 'pack-restored' $true
        $summary.restoredProbe=Run-Probe 'restored'
    }
    foreach ($input in $inputs) {
        if ((Get-FileHash -LiteralPath $input.Source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $input.Sha256) { throw ('MAP_READERS_SOURCE_CHANGED: ' + $input.Source) }
        if ((Get-FileHash -LiteralPath $input.Copy -Algorithm SHA256).Hash.ToLowerInvariant() -cne $input.Sha256) { throw ('MAP_READERS_COPY_NOT_RESTORED: ' + $input.Copy) }
    }
    $summary.sourcesUnchanged=$true
    $summary.status='pass'
} catch { $failure=$_; $summary.status='fail'; $summary.error=$_.Exception.Message }
finally {
    foreach ($path in $originals.Keys) { [IO.File]::WriteAllBytes($path, $originals[$path]) }
    Save-Summary
    Write-Host ('Map reader evidence: ' + $evidence)
}
if ($failure) { throw $failure }
Write-Host ('MAP READERS VERIFY PASS (' + $results.Count + ' mutations fired)')
