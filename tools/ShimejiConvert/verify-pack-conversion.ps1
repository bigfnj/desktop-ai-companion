#requires -Version 5.1
[CmdletBinding()]
param([switch]$Mutate, [string[]]$Only = @('*'), [string]$ScratchRoot = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$helper = Join-Path $PSScriptRoot 'verify-source-ir.ps1'
# The shared preparer copies only the explicit project closure, refuses protected/reparse paths,
# and checks the original bytes. Its child process makes PrepareOnly's exit local to that child.
$prepared = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -PrepareOnly -ScratchRoot $ScratchRoot)
if ($LASTEXITCODE -ne 0) { throw 'PACK_API_SNAPSHOT_FAILED' }
$paths = @($prepared | Where-Object { $_ -like 'Source IR evidence: *' })
if ($paths.Count -ne 1) { throw 'PACK_API_SNAPSHOT_WITNESS_MISSING' }
$evidence = $paths[0].Substring('Source IR evidence: '.Length)
$summaryPath = Join-Path $evidence 'pack-conversion-results.json'
$snapshotPath = Join-Path $evidence 'snapshot.json'
$preparationPath = Join-Path $evidence 'results.json'
$preparation = [IO.File]::ReadAllText($preparationPath, $utf8) | ConvertFrom-Json
if ($preparation.Status -ne 'prepared' -or $preparation.BuildExecuted -or -not $preparation.SourceBytesUnchanged) {
    throw 'PACK_API_SNAPSHOT_NOT_PREPARED'
}
$snapshot = $preparation.Snapshot
$feed = Join-Path $evidence 'empty-feed'
$packages = Join-Path $evidence 'packages'
$config = Join-Path $evidence 'offline-nuget.config'
[void][IO.Directory]::CreateDirectory($feed)
[void][IO.Directory]::CreateDirectory($packages)
$feedXml = [Security.SecurityElement]::Escape($feed)
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="offline" value="' + $feedXml + '"/></packageSources></configuration>', $utf8)
$project = Join-Path $snapshot 'tools\ShimejiConvert\ShimejiConvert.csproj'
$engine = Join-Path $snapshot 'tools\ShimejiConvert.Engine\PackConversion.cs'
$emitter = Join-Path $snapshot 'tools\ShimejiConvert.Engine\Emit\TickPackEmitter.cs'
$artifact = Join-Path $snapshot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.Engine.dll'
$executable = Join-Path $snapshot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.exe'
$original = [IO.File]::ReadAllBytes($engine)
$source = $utf8.GetString($original)
$originals = @{}
$originals[$engine] = $original
$originals[$emitter] = [IO.File]::ReadAllBytes($emitter)
$inputFiles = [IO.File]::ReadAllText($snapshotPath, $utf8) | ConvertFrom-Json
$marker = 'pack-conversion self-test: executing public API witnesses'
$cases = @('complete-pack-startup-and-geometry', 'missing-member-warning', 'missing-main-refusal',
    'unknown-option-refusal', 'startup-member-refusal', 'startup-behaviour-refusal', 'egg-return-refusal',
    'format-validation-refusal', 'deep-copy-fault-refusal', 'fixed-point-fault-refusal',
    'member-refusal-no-write', 'independent-results', 'unused-action-diagnostic', 'optional-parity-map', 'long-member-key')
$environment = @{
    DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT='1'
    DESKTOP_AI_COMPANION_DATA_ROOT=(Join-Path $evidence 'pack-api-data')
    TEMP=$evidence
    TMP=$evidence
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'
    DOTNET_CLI_USE_MSBUILD_SERVER='0'
    MSBUILDDISABLENODEREUSE='1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
    DOTNET_CLI_HOME=(Join-Path $evidence 'cli-home')
    NUGET_PACKAGES=$packages
    NUGET_HTTP_CACHE_PATH=(Join-Path $evidence 'nuget-http-cache')
    GIT_CEILING_DIRECTORIES=$snapshot
}
function Quote-Argument([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
function Write-Summary {
    [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine, $utf8)
}
function Invoke-Captured([string]$Label, [string]$FileName, [string[]]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FileName
    $start.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $start.WorkingDirectory = $snapshot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($name in $environment.Keys) { $start.EnvironmentVariables[$name] = $environment[$name] }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    $ownedProcessId = $process.Id
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit(300000)
    if ($timedOut) { try { $process.Kill() } catch { }; [void]$process.WaitForExit(5000) }
    if (-not $outTask.Wait(10000) -or -not $errTask.Wait(10000)) { throw "PACK_API_OUTPUT_NOT_CLOSED: $Label, owned pid $ownedProcessId" }
    $stdout = $outTask.GetAwaiter().GetResult()
    $stderr = $errTask.GetAwaiter().GetResult()
    $stdoutPath = Join-Path $evidence ($Label + '.stdout.log')
    $stderrPath = Join-Path $evidence ($Label + '.stderr.log')
    [IO.File]::WriteAllText($stdoutPath, $stdout, $utf8)
    [IO.File]::WriteAllText($stderrPath, $stderr, $utf8)
    $record = [pscustomobject]@{ Label=$Label; FileName=$FileName; Arguments=$Arguments; WorkingDirectory=$snapshot
        ProcessId=$ownedProcessId; TimedOut=$timedOut; ExitCode=$process.ExitCode; Stdout=$stdoutPath; Stderr=$stderrPath }
    $process.Dispose()
    return [pscustomobject]@{ Record=$record; Output=$stdout + [Environment]::NewLine + $stderr }
}
function Invoke-Build([string]$Label, [switch]$NoRestore) {
    $arguments = @('build', $project, '-c', 'Release', '--nologo', '-v:minimal', '--no-incremental', '--no-restore',
        '-p:UseSharedCompilation=false', '-nodeReuse:false', '-maxcpucount:1')
    $run = Invoke-Captured $Label 'dotnet' $arguments
    if ($run.Record.TimedOut -or $run.Record.ExitCode -ne 0 -or -not [IO.File]::Exists($artifact) -or -not [IO.File]::Exists($executable)) {
        throw "PACK_API_BUILD_FAILED: $Label; see $($run.Record.Stdout)"
    }
    return [pscustomobject]@{ Path=$artifact; Sha256=(Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        LastWriteUtcTicks=[IO.File]::GetLastWriteTimeUtc($artifact).Ticks; Execution=$run.Record }
}
function Invoke-Positive([string]$Label) {
    $run = Invoke-Captured $Label $executable @('selftest', 'pack-conversion')
    $lines = @($run.Output -split '\r?\n' | ForEach-Object { $_.Trim() })
    if ($run.Record.ExitCode -ne 0 -or $run.Record.TimedOut -or $lines -notcontains $marker -or
        $lines -notcontains 'SELFTEST PASS' -or $lines -notcontains 'SELFTEST-COUNT: 1' -or
        @($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' }).Count -ne 0) {
        throw "PACK_API_POSITIVE_WITNESS_FAILED: $Label; see $($run.Record.Stdout)"
    }
    foreach ($case in $cases) {
        if (@($lines | Where-Object { $_ -eq ('pack-conversion PASS ' + $case) }).Count -ne 1) {
            throw "PACK_API_POSITIVE_CASE_MISSING: $case"
        }
    }
    return $run.Record
}
function Assert-OriginalsUnchanged {
    foreach ($entry in $inputFiles) {
        if ((Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Sha256) {
            throw "PACK_API_ORIGINAL_CHANGED: $($entry.RelativePath)"
        }
    }
}
$mutations = @(
    [pscustomobject]@{ Name='main-required'; Witness='missing-main-refusal'
        Old='if (string.IsNullOrEmpty(snapshot.MainMember))'; New='if (snapshot.MainMember == "__mutation__")' },
    [pscustomobject]@{ Name='unknown-options'; Witness='unknown-option-refusal'
        Old='if (!keys.Contains(key) && !skipped.Contains(key)) errors.Add("TICK_PACK_OPTIONS_UNKNOWN:'
        New='if (key == "__mutation__") errors.Add("TICK_PACK_OPTIONS_UNKNOWN:' },
    [pscustomobject]@{ Name='whole-pack-validation'; Witness='startup-member-refusal'
        Old='errors.AddRange(pack.Errors);'; New='errors.AddRange(new string[0]);' },
    [pscustomobject]@{ Name='missing-target-warning'; Witness='missing-member-warning'
        Old='warnings.AddRange(pack.Warnings);'; New='warnings.AddRange(new string[0]);' },
    [pscustomobject]@{ Name='partial-source-identity'; Witness='member-refusal-no-write'
        Old='if (ready.Count == prepared.Count) identity = previewIdentity;'; New='identity = previewIdentity;' },
    [pscustomobject]@{ Name='capture-member-refusals'; Witness='member-refusal-no-write'
        Old='TickPackEmitter.PrepareMembers(extractedPackRoot, true, snapshot.RecoverMissingNext)'; New='TickPackEmitter.PrepareMembers(extractedPackRoot, false, snapshot.RecoverMissingNext)' },
    [pscustomobject]@{ Name='api-no-source-writes'; Witness='member-refusal-no-write'
        Old='List<TickPackEmitter.PreparedMember> prepared = TickPackEmitter.PrepareMembers(extractedPackRoot, true, snapshot.RecoverMissingNext);'
        New='List<TickPackEmitter.PreparedMember> prepared = TickPackEmitter.PrepareMembers(extractedPackRoot, true, snapshot.RecoverMissingNext); File.AppendAllText(Path.Combine(extractedPackRoot, "img", "Alpha", "conf", "actions.xml"), Environment.NewLine);' },
    [pscustomobject]@{ Name='deep-copy-guard'; Witness='deep-copy-fault-refusal'
        Old='if (!string.Equals(canonical, copiedXml, StringComparison.Ordinal))'; New='if (canonical.Length < 0)' },
    [pscustomobject]@{ Name='fixed-point-guard'; Witness='fixed-point-fault-refusal'
        Old='if (!string.Equals(copiedXml, roundTrip, StringComparison.Ordinal))'; New='if (copiedXml.Length < 0)' },
    [pscustomobject]@{ Name='real-validator-feature-set'; Witness='complete-pack-startup-and-geometry'
        Old='document.ToString(), CompanionFormatFeatures.All, out parsed'
        New='document.ToString(), CompanionFormatFeatures.Supported, out parsed' },
    [pscustomobject]@{ Name='requested-map-only'; Witness='optional-parity-map'
        Old='options.IncludeParityMaps ? member.Map : null'; New='member.Map' },
    [pscustomobject]@{ Name='chosen-member-role'; Witness='complete-pack-startup-and-geometry'
        Old='if (metadata != null && metadata.Role != null) role = metadata.Role;'; New='if (member.Name == "__mutation__") role = metadata.Role;' },
    [pscustomobject]@{ Name='unused-source-retained'; Witness='unused-action-diagnostic'
        Old='&& Members.All(member => member.Disposition == PackMemberDisposition.Prepared);'
        New='&& Members.All(member => member.Disposition == PackMemberDisposition.Prepared && member.Graph.Unreachable.Count == 0);' }
    [pscustomobject]@{ Name='long-member-display-name'; Witness='long-member-key'; Path=$emitter
        Old='E("petname", displayName)'; New='E("petname", _member)' }
    [pscustomobject]@{ Name='source-image-stream-loader'; Witness='long-member-key'; Path=$emitter
        Old='new Bitmap(imageInput)'; New='new Bitmap(path)' }
    [pscustomobject]@{ Name='sheet-image-stream-loader'; Witness='long-member-key'; Path=$emitter
        Old='new Bitmap(spriteInput)'; New='new Bitmap(frame.Path)' }
)
$patterns = @($Only | ForEach-Object { $_.Split(',') } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$selected = @($mutations | Where-Object { $name=$_.Name; @($patterns | Where-Object { $name -like $_ }).Count -ne 0 })
if ($Mutate -and $selected.Count -eq 0) { throw 'PACK_API_NO_MUTATIONS_SELECTED' }
$results = New-Object 'Collections.Generic.List[object]'
$summary = [ordered]@{ Status='running'; Evidence=$evidence; Snapshot=$snapshot; MutationMode='scratch'; Results=$results }
$failure = $null
$touched = $false
try {
    $restored = Invoke-Captured 'pack-api-offline-restore' 'dotnet' @('restore', $project, '--configfile', $config,
        '--source', $feed, '--packages', $packages, '--disable-parallel', '--nologo', '-v:minimal', '-p:NuGetAudit=false')
    if ($restored.Record.TimedOut -or $restored.Record.ExitCode -ne 0) {
        throw ('PACK_API_OFFLINE_RESTORE_FAILED: ' + $restored.Record.Stdout)
    }
    $summary.OfflineRestore = $restored.Record
    $summary.OfflineSource = $feed
    $summary.BaselineArtifact = Invoke-Build 'pack-api-baseline-build'
    $summary.BaselineExecution = Invoke-Positive 'pack-api-baseline-selftest'
    Write-Summary
    if ($Mutate) {
        foreach ($mutation in $selected) {
            $mutationPath = if ($mutation.PSObject.Properties.Name -contains 'Path') { $mutation.Path } else { $engine }
            $mutationOriginal = $originals[$mutationPath]
            $mutationSource = $utf8.GetString($mutationOriginal)
            if ([regex]::Matches($mutationSource, [regex]::Escape($mutation.Old)).Count -ne 1) { throw "PACK_API_MUTATION_ANCHOR_NOT_UNIQUE: $($mutation.Name)" }
            $beforeTicks = [IO.File]::GetLastWriteTimeUtc($artifact).Ticks
            try {
                $touched = $true
                [IO.File]::WriteAllBytes($mutationPath, $utf8.GetBytes($mutationSource.Replace($mutation.Old, $mutation.New)))
                $built = Invoke-Build ($mutation.Name + '-build') -NoRestore
                if ($built.LastWriteUtcTicks -le $beforeTicks -or $built.Sha256 -eq $summary.BaselineArtifact.Sha256) {
                    throw "PACK_API_FRESH_CHANGED_ARTIFACT_MISSING: $($mutation.Name)"
                }
                $run = Invoke-Captured ($mutation.Name + '-selftest') $executable @('selftest', ('pack-conversion/' + $mutation.Witness))
                $lines = @($run.Output -split '\r?\n' | ForEach-Object { $_.Trim() })
                $fails = @($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' })
                $expected = '^FAIL PackConversionSelfTest/' + [regex]::Escape($mutation.Witness) + ':'
                if ($run.Record.ExitCode -ne 1 -or $run.Record.TimedOut -or $fails.Count -ne 1 -or $fails[0] -notmatch $expected -or
                    $lines -notcontains $marker -or $lines -notcontains 'SELFTEST FAIL' -or $lines -notcontains 'SELFTEST-COUNT: 1') {
                    throw "PACK_API_MUTATION_WITNESS_FAILED: $($mutation.Name); see $($run.Record.Stdout)"
                }
                $results.Add([pscustomobject]@{ Mutation=$mutation.Name; Status='FIRED'; Witness=$fails[0]; Artifact=$built; Execution=$run.Record })
                Write-Host ('FIRED ' + $mutation.Name + ': ' + $fails[0])
                Write-Summary
            }
            finally { [IO.File]::WriteAllBytes($mutationPath, $mutationOriginal) }
        }
    }
    $summary.Status = 'pass'
}
catch { $failure=$_; $summary.Status='fail'; $summary.Error=$_.Exception.Message }
finally {
    try {
        foreach ($path in $originals.Keys) {
            [IO.File]::WriteAllBytes($path, $originals[$path])
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($originals[$path])) {
                throw 'PACK_API_SCRATCH_RESTORE_FAILED'
            }
        }
        if ($touched) {
            $summary.RestoredArtifact = Invoke-Build 'pack-api-restored-build' -NoRestore
            $summary.RestoredExecution = Invoke-Positive 'pack-api-restored-selftest'
        }
        Assert-OriginalsUnchanged
        $summary.SourceBytesUnchanged = $true
        $summary.ScratchBytesRestored = $true
    }
    catch { $summary.Status='fail'; $summary.RestoreError=$_.Exception.Message; if ($null -eq $failure) { $failure=$_ } }
    Write-Summary
    Write-Host ('Pack conversion evidence: ' + $evidence)
}
if ($null -ne $failure) { throw $failure }
Write-Host ("PACK CONVERSION VERIFY PASS ($($results.Count) mutations fired)")
