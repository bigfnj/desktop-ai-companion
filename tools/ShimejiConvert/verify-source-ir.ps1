#requires -Version 5.1
[CmdletBinding()]
param([switch]$Mutate, [string]$Only = '*')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$env:DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT = '1'
$env:DESKTOP_AI_COMPANION_DATA_ROOT = 'D:\.ai-work\worktrees\_data\shimeji-convert'
$env:TEMP = 'D:\.ai-work\worktrees\_temp\shimeji-convert'
$env:TMP = $env:TEMP
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
foreach ($directory in @($env:DESKTOP_AI_COMPANION_DATA_ROOT, $env:TEMP)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
$evidence = Join-Path $env:TEMP ('source-ir-verify-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($evidence)
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$project = Join-Path $PSScriptRoot 'ShimejiConvert.csproj'
$executable = Join-Path $PSScriptRoot 'bin\Release\ShimejiConvert.exe'
$artifact = Join-Path $PSScriptRoot 'bin\Release\ShimejiConvert.Engine.dll'
$parser = Join-Path $repo 'tools\ShimejiConvert.Engine\Shimeji\ShimejiParser.cs'
$model = Join-Path $repo 'tools\ShimejiConvert.Engine\Shimeji\ShimejiModel.cs'
$marker = 'source-ir self-test: executing source preservation witnesses'
$caseNames = @('ordered-reference-overrides', 'raw-parameters-and-images', 'script-envelopes',
    'behavior-wrappers-and-add', 'aliases-and-collisions', 'source-path-raw-ordinals', 'immutable-snapshot', 'entrypoints-and-locators')

function Quote-Argument([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine, $utf8)
}

function Invoke-Captured([string]$Label, [string]$FileName, [string[]]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FileName
    $start.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    $ownedProcessId = $process.Id
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit(300000)
    if ($timedOut) {
        # Only this direct child is owned. Node reuse and shared compilation are disabled above/below.
        try { $process.Kill() } catch { }
        [void]$process.WaitForExit(5000)
    }
    if (-not $stdoutTask.Wait(10000) -or -not $stderrTask.Wait(10000)) {
        throw "source-ir: redirected output did not close for $Label (owned pid $ownedProcessId)"
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $stdoutPath = Join-Path $evidence ($Label + '.stdout.log')
    $stderrPath = Join-Path $evidence ($Label + '.stderr.log')
    [IO.File]::WriteAllText($stdoutPath, $stdout, $utf8)
    [IO.File]::WriteAllText($stderrPath, $stderr, $utf8)
    $record = [pscustomobject]@{
        Label = $Label; FileName = $FileName; Arguments = $Arguments; WorkingDirectory = $repo
        ProcessId = $ownedProcessId; Exited = $process.HasExited; TimedOut = $timedOut
        ExitCode = $process.ExitCode; Stdout = $stdoutPath; Stderr = $stderrPath
    }
    Write-Json (Join-Path $evidence ($Label + '.process.json')) $record
    $process.Dispose()
    return [pscustomobject]@{ Record = $record; Output = $stdout + [Environment]::NewLine + $stderr }
}

function Invoke-Build([string]$Label, [switch]$NoRestore) {
    $arguments = @('build', $project, '-c', 'Release', '--nologo', '-v:minimal',
        '-p:UseSharedCompilation=false', '-nodeReuse:false', '-maxcpucount:1')
    if ($NoRestore) { $arguments += @('--no-restore', '--no-incremental') }
    $result = Invoke-Captured $Label 'dotnet' $arguments
    if ($result.Record.TimedOut -or $result.Record.ExitCode -ne 0) {
        throw "source-ir: $Label build failed; see $($result.Record.Stdout) and $($result.Record.Stderr)"
    }
    if (-not [IO.File]::Exists($artifact) -or -not [IO.File]::Exists($executable)) {
        throw 'source-ir: successful build produced no runnable CLI/engine artifact'
    }
    return [pscustomobject]@{
        Path = $artifact; Sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        LastWriteUtcTicks = [IO.File]::GetLastWriteTimeUtc($artifact).Ticks
    }
}

function Invoke-Positive([string]$Label) {
    $result = Invoke-Captured $Label $executable @('selftest')
    $lines = @($result.Output -split '\r?\n' | ForEach-Object { $_.Trim() })
    if ($result.Record.ExitCode -ne 0 -or $result.Record.TimedOut -or @($lines | Where-Object { $_ -eq $marker }).Count -ne 1) {
        throw "source-ir: positive execution witness missing for $Label; see $($result.Record.Stdout)"
    }
    foreach ($caseName in $caseNames) {
        if (@($lines | Where-Object { $_ -eq ('source-ir PASS ' + $caseName) }).Count -ne 1) {
            throw "source-ir: positive case $caseName missing for $Label"
        }
    }
    if (@($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' }).Count -ne 0 -or $lines -notcontains 'SELFTEST PASS') {
        throw "source-ir: contradictory positive result for $Label"
    }
    return $result.Record
}

$mutations = @(
    [pscustomobject]@{ Name = 'attribute-value'; Path = $parser; Witness = 'ordered-reference-overrides'
        Old = '                        attribute.Value, key + "@" + name + "[" + (previous + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"));'
        New = '                        "", key + "@" + name + "[" + (previous + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"));' },
    [pscustomobject]@{ Name = 'child-order'; Path = $parser; Witness = 'ordered-reference-overrides'
        Old = '                foreach (XElement child in element.Elements()) children.Add(nodes[child]);'
        New = '                foreach (XElement child in element.Elements().Reverse()) children.Add(nodes[child]);' },
    [pscustomobject]@{ Name = 'actions-source-attachment'; Path = $parser; Witness = 'raw-parameters-and-images'
        Old = '            config.Source = new ShimejiSourceConfig(ReadSource(actions, "actions.xml"), null);'
        New = '            config.Source = null;' },
    [pscustomobject]@{ Name = 'script-envelope'; Path = $model; Witness = 'script-envelopes'
        Old = 'rawValue.StartsWith("${", System.StringComparison.Ordinal)'
        New = 'rawValue.Contains("${")' },
    [pscustomobject]@{ Name = 'attribute-collision'; Path = $parser; Witness = 'aliases-and-collisions'
        Old = '                    attributes.Add(new ShimejiSourceAttribute(name, attribute.Name.LocalName, attribute.Name.NamespaceName,'
        New = '                    if (previous == 0) attributes.Add(new ShimejiSourceAttribute(name, attribute.Name.LocalName, attribute.Name.NamespaceName,' },
    [pscustomobject]@{ Name = 'sibling-ordinal'; Path = $parser; Witness = 'ordered-reference-overrides'
        Old = '                    ordinal = previous + 1;'
        New = '                    ordinal = 1;' },
    [pscustomobject]@{ Name = 'immutable-child-copy'; Path = $model; Witness = 'immutable-snapshot'
        Old = 'Children = new List<ShimejiSourceElement>(children).AsReadOnly();'
        New = 'Children = ((List<ShimejiSourceElement>)children).AsReadOnly();' },
    [pscustomobject]@{ Name = 'embedded-line-information'; Path = $parser; Witness = 'entrypoints-and-locators'
        Old = 'return XDocument.Load(s, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);'
        New = 'return XDocument.Load(s, LoadOptions.PreserveWhitespace);' },
    [pscustomobject]@{ Name = 'raw-source-sibling-name'; Path = $parser; Witness = 'source-path-raw-ordinals'
        Old = 'sourceSiblings.TryGetValue(sourceName, out sourcePrevious);'
        New = 'sourceSiblings.TryGetValue(name, out sourcePrevious);' }
)
$selectedMutations = @($mutations | Where-Object { $_.Name -like $Only })
if ($Mutate -and $selectedMutations.Count -eq 0) { throw "source-ir: filter '$Only' selected no mutations" }
$originals = @{}
$results = New-Object 'Collections.Generic.List[object]'
$summary = [ordered]@{ Status = 'running'; Evidence = $evidence; Mutate = [bool]$Mutate; Results = $results }
$touched = $false
$failure = $null
try {
    $summary.BaselineArtifact = Invoke-Build 'baseline-build'
    $summary.BaselineExecution = Invoke-Positive 'baseline-selftest'
    if ($Mutate) {
        foreach ($path in @($parser, $model)) { $originals[$path] = [IO.File]::ReadAllBytes($path) }
        foreach ($mutation in $selectedMutations) {
            $original = $originals[$mutation.Path]
            $source = $utf8.GetString($original)
            $matchCount = [regex]::Matches($source, [regex]::Escape($mutation.Old)).Count
            if ($matchCount -ne 1) { throw "source-ir: mutation $($mutation.Name) matched $matchCount sites instead of one" }
            $before = [pscustomobject]@{
                Sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
                LastWriteUtcTicks = [IO.File]::GetLastWriteTimeUtc($artifact).Ticks
            }
            try {
                $touched = $true
                [IO.File]::WriteAllBytes($mutation.Path, $utf8.GetBytes($source.Replace($mutation.Old, $mutation.New)))
                $built = Invoke-Build ($mutation.Name + '-build') -NoRestore
                if ($built.LastWriteUtcTicks -le $before.LastWriteUtcTicks -or $built.Sha256 -eq $before.Sha256) {
                    throw "source-ir: mutation $($mutation.Name) did not produce a newly built, changed engine DLL"
                }
                $run = Invoke-Captured ($mutation.Name + '-selftest') $executable @('selftest')
                $lines = @($run.Output -split '\r?\n' | ForEach-Object { $_.Trim() })
                $fails = @($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' })
                $expected = '^FAIL SourceIrSelfTest/' + [regex]::Escape($mutation.Witness) + ':'
                if ($run.Record.ExitCode -ne 1 -or $run.Record.TimedOut -or $fails.Count -ne 1 -or
                    $fails[0] -notmatch $expected -or $lines -notcontains $marker -or $lines -notcontains 'SELFTEST FAIL') {
                    throw "source-ir: mutation $($mutation.Name) did not fire exactly its named witness; see $($run.Record.Stdout)"
                }
                $results.Add([pscustomobject]@{ Mutation = $mutation.Name; Status = 'FIRED'; Witness = $fails[0]
                    Artifact = $built; Execution = $run.Record })
                Write-Host ("FIRED $($mutation.Name): " + $fails[0])
            }
            finally {
                [IO.File]::WriteAllBytes($mutation.Path, $original)
                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($mutation.Path)) -cne [Convert]::ToBase64String($original)) {
                    throw "source-ir: byte-exact restoration failed for $($mutation.Path)"
                }
            }
        }
    }
    $summary.Status = 'pass'
}
catch {
    $failure = $_
    $summary.Status = 'fail'
    $summary.Error = $_.Exception.Message
}
finally {
    try {
        if ($touched) {
            foreach ($path in $originals.Keys) {
                [IO.File]::WriteAllBytes($path, $originals[$path])
                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($originals[$path])) {
                    throw "source-ir: final byte-exact restoration failed for $path"
                }
            }
            $summary.RestoredArtifact = Invoke-Build 'restored-build' -NoRestore
            $summary.RestoredExecution = Invoke-Positive 'restored-selftest'
            $summary.SourceBytesRestored = $true
        }
    }
    catch {
        $summary.Status = 'fail'
        $summary.RestoreError = $_.Exception.Message
        if ($null -eq $failure) { $failure = $_ }
    }
    Write-Json (Join-Path $evidence 'results.json') $summary
    Write-Host ("Source IR evidence: " + $evidence)
}
if ($null -ne $failure) { throw $failure }
Write-Host ("SOURCE-IR VERIFY PASS ($($results.Count) mutations fired)")
