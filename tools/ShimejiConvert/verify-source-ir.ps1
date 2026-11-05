#requires -Version 5.1
[CmdletBinding()]
param([switch]$Mutate, [string[]]$Only = @('*'), [switch]$PrepareOnly, [switch]$CheckClassicContinuation,
    [string]$ScratchRoot = [IO.Path]::GetTempPath(), [string]$DataRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Test-Inside([string]$Boundary, [string]$Path) {
    $Boundary = [IO.Path]::GetFullPath($Boundary).TrimEnd('\', '/')
    $Path = [IO.Path]::GetFullPath($Path)
    return $Path.Equals($Boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
function Assert-NotReparse([IO.FileAttributes]$Attributes, [string]$Path) {
    if (($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "SNAPSHOT_REPARSE_REFUSED: $Path" }
}
function Assert-SafePath([string]$Path, [string]$Boundary, [bool]$InspectLinks = $true) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($Boundary -and -not (Test-Inside $Boundary $full)) { throw "SNAPSHOT_OUTSIDE_BOUNDARY: $full" }
    $current = [IO.Path]::GetPathRoot($full); $previous = ''
    foreach ($part in $full.Substring($current.Length).Split(@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $leaf = $part.ToLowerInvariant(); $pattern = $null
        if ($leaf.EndsWith('.env')) { $pattern = 'W01' }
        elseif ($leaf.StartsWith('.env.')) { $pattern = 'W02' }
        elseif ($leaf -eq '.npmrc') { $pattern = 'W03' }
        elseif ($previous -eq '.git' -and $leaf -eq 'credentials') { $pattern = 'W04' }
        elseif ($leaf.EndsWith('.pem')) { $pattern = 'W05' }
        elseif ($leaf.EndsWith('.key')) { $pattern = 'W06' }
        elseif ($leaf.EndsWith('.p12')) { $pattern = 'W07' }
        elseif ($leaf.EndsWith('.pfx')) { $pattern = 'W08' }
        elseif ($leaf.EndsWith('.keystore')) { $pattern = 'W09' }
        elseif ($leaf.StartsWith('privatekey')) { $pattern = 'W10' }
        elseif ($leaf -in @('.aws', '.ssh', '.kube', '.docker', '.mozilla', 'gcloud', 'keychains', 'user data')) { $pattern = 'home-sensitive-directory' }
        if ($pattern) { throw ('SNAPSHOT_PROTECTED_PATH_REFUSED ' + $pattern + ': ' + $full) }
        $current = Join-Path $current $part
        if ($InspectLinks -and ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current))) {
            Assert-NotReparse ([IO.File]::GetAttributes($current)) $current
        }
        $previous = $leaf
    }
    return $full
}
$ScratchRoot = [IO.Path]::GetFullPath($ScratchRoot)
[void](Assert-SafePath $ScratchRoot)
if (Test-Inside $repo $ScratchRoot) { throw 'SNAPSHOT_SCRATCH_INSIDE_REPOSITORY' }
$evidence = Join-Path $ScratchRoot ('source-ir-verify-' + [Guid]::NewGuid().ToString('N'))
[void](Assert-SafePath $evidence)
[void][IO.Directory]::CreateDirectory($evidence)
$snapshotRoot = Join-Path $evidence 'source'
[void][IO.Directory]::CreateDirectory($snapshotRoot)
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $evidence 'data' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
[void](Assert-SafePath $DataRoot)
[void][IO.Directory]::CreateDirectory($DataRoot)
$converterEnvironment = @{
    DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT = '1'
    DESKTOP_AI_COMPANION_DATA_ROOT = $DataRoot
    TEMP = $evidence
    TMP = $evidence
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    MSBUILDDISABLENODEREUSE = '1'
    GIT_CEILING_DIRECTORIES = $snapshotRoot
}
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$project = Join-Path $snapshotRoot 'tools\ShimejiConvert\ShimejiConvert.csproj'
$executable = Join-Path $snapshotRoot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.exe'
$artifact = Join-Path $snapshotRoot 'tools\ShimejiConvert\bin\Release\ShimejiConvert.Engine.dll'
$parser = Join-Path $snapshotRoot 'tools\ShimejiConvert.Engine\Shimeji\ShimejiParser.cs'
$model = Join-Path $snapshotRoot 'tools\ShimejiConvert.Engine\Shimeji\ShimejiModel.cs'
$emitter = Join-Path $snapshotRoot 'tools\ShimejiConvert.Engine\Emit\TickPackEmitter.cs'
$emitterSelfTest = Join-Path $snapshotRoot 'tools\ShimejiConvert.Engine\Shimeji\EmitterSelfTest.cs'
$marker = 'source-ir self-test: executing source preservation witnesses'
$tickMarker = 'tick-pack-emitter self-test: executing conversion witnesses'
$caseNames = @('ordered-reference-overrides', 'raw-parameters-and-images', 'script-envelopes',
    'behavior-wrappers-and-add', 'aliases-and-collisions', 'source-path-raw-ordinals', 'source-schema-resolution',
    'qualified-names-and-depth', 'source-materialization-budget', 'source-pack-context', 'immutable-snapshot', 'entrypoints-and-locators')
$tickCaseNames = @('graph-bindings-and-origins', 'sprite-pixels-and-identity', 'lifecycle-parameters',
    'expression-refusal', 'mixed-pose-geometry', 'explicit-right-geometry', 'pair-anchor-cache', 'pair-anchor-preload-order', 'pair-anchor-identity',
    'right-only-refusal', 'right-path-refusal', 'existing-output-refusal', 'private-action-reference-refusal',
    'private-behavior-action-refusal', 'java-literal-refusal', 'sprite-dpi72', 'declaration-order', 'missing-add-false',
    'duplicate-root-last-definition', 'enclosing-conditions', 'prepare-all-members', 'cross-schema-refusal',
    'toggle-refusal', 'information-refusal', 'own-set-binding-reset', 'action-schema-refusal',
    'ignored-reference-lists', 'cross-member-pack-metadata', 'composite-cycle-refusal', 'composite-depth-refusal',
    'composite-size-refusal', 'hidden-pack-members', 'explicit-pack-metadata', 'pack-source-identity')

function Quote-Argument([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Write-Json([string]$Path, $Value) {
    [void](Assert-SafePath $Path $evidence)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine, $utf8)
}

$inputFiles = @{}
$visitedXml = @{}
function Hash-Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}
function Write-ScratchBytes([string]$Path, [byte[]]$Bytes) {
    [void](Assert-SafePath $Path $snapshotRoot)
    [IO.File]::WriteAllBytes($Path, $Bytes)
}
function Test-CopyRules {
    $witnesses = New-Object 'Collections.Generic.List[string]'
    foreach ($entry in @(
        @{ Path = (Join-Path $repo 'synthetic.env'); Error = 'SNAPSHOT_PROTECTED_PATH_REFUSED W01' },
        @{ Path = (Join-Path $repo 'synthetic.env\child.cs'); Error = 'SNAPSHOT_PROTECTED_PATH_REFUSED W01' },
        @{ Path = (Join-Path $repo '..\outside.cs'); Error = 'SNAPSHOT_OUTSIDE_BOUNDARY' })) {
        $refused = $false
        try { [void](Assert-SafePath $entry.Path $repo $false) }
        catch { $refused = $_.Exception.Message.StartsWith($entry.Error, [StringComparison]::Ordinal) }
        if (-not $refused) { throw ('COPY_RULE_WITNESS: ' + $entry.Error) }
        $witnesses.Add($entry.Error)
    }
    $refused = $false
    try { Assert-NotReparse ([IO.FileAttributes]::ReparsePoint) 'synthetic-link' }
    catch { $refused = $_.Exception.Message.StartsWith('SNAPSHOT_REPARSE_REFUSED:', [StringComparison]::Ordinal) }
    if (-not $refused) { throw 'COPY_RULE_WITNESS: reparse metadata accepted' }
    Assert-NotReparse ([IO.FileAttributes]::Normal) 'synthetic-regular'
    [void](Assert-SafePath (Join-Path $repo 'synthetic.cs') $repo $false)
    $witnesses.Add('SNAPSHOT_REPARSE_REFUSED')
    return @($witnesses)
}
function Add-SnapshotFile([string]$Path) {
    $full = Assert-SafePath $Path $repo
    $relative = $full.Substring($repo.TrimEnd('\', '/').Length + 1)
    if (-not $inputFiles.ContainsKey($relative)) {
        if (-not [IO.File]::Exists($full)) { throw "SNAPSHOT_MISSING_INPUT: $relative" }
        $destination = Assert-SafePath (Join-Path $snapshotRoot $relative) $snapshotRoot
        $bytes = [IO.File]::ReadAllBytes($full)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::WriteAllBytes($destination, $bytes)
        $inputFiles[$relative] = [pscustomobject]@{ RelativePath=$relative; Source=$full; Copy=$destination; Sha256=(Hash-Bytes $bytes); Bytes=$bytes.Length }
    }
    return $inputFiles[$relative].Copy
}
function Resolve-Include([string]$Directory, [string]$Include) {
    $path = $Include.Replace('$(MSBuildThisFileDirectory)', $Directory.TrimEnd('\') + '\').Replace('$(MSBuildProjectDirectory)', $Directory)
    if ($path -match '[$*?]' -or $path.Contains(';')) { throw "SNAPSHOT_EXPLICIT_INCLUDE_REQUIRED: $Include" }
    if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $Directory $path }
    return (Assert-SafePath $path $repo)
}
function Add-XmlClosure([string]$Path) {
    $full = Assert-SafePath $Path $repo
    if ($visitedXml.ContainsKey($full)) { return }
    $visitedXml[$full] = $true
    $copy = Add-SnapshotFile $full
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($copy, $settings)
    try { $document = New-Object Xml.XmlDocument; $document.XmlResolver=$null; $document.Load($reader) }
    finally { $reader.Dispose() }
    $directory = [IO.Path]::GetDirectoryName($full)
    if ([IO.Path]::GetExtension($full) -eq '.csproj') {
        $explicit = $document.SelectNodes("//*[local-name()='EnableDefaultItems' or local-name()='EnableDefaultCompileItems']")
        if (-not @($explicit | Where-Object { $_.InnerText -eq 'false' }).Count) { throw "SNAPSHOT_DEFAULT_COMPILE_ITEMS_UNSUPPORTED: $full" }
        $ancestor = $directory
        while (Test-Inside $repo $ancestor) {
            foreach ($name in @('Directory.Build.props', 'Directory.Build.targets')) {
                $candidate = Assert-SafePath (Join-Path $ancestor $name) $repo
                if ([IO.File]::Exists($candidate)) { Add-XmlClosure $candidate }
            }
            if ($ancestor -eq $repo.TrimEnd('\', '/')) { break }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
    }
    foreach ($import in $document.SelectNodes("//*[local-name()='Import' and @Project]")) {
        Add-XmlClosure (Resolve-Include $directory $import.GetAttribute('Project'))
    }
    foreach ($item in $document.SelectNodes("//*[local-name()='ItemGroup']/*[@Include]")) {
        $kind = $item.LocalName
        if ($kind -eq 'PackageReference') { throw "SNAPSHOT_PACKAGE_REFERENCE_UNSUPPORTED: $full" }
        if ($kind -eq 'ProjectReference') { Add-XmlClosure (Resolve-Include $directory $item.GetAttribute('Include')); continue }
        $copyItem = $kind -in @('Compile', 'EmbeddedResource')
        if ($kind -in @('None', 'Content')) {
            $copyItem = @($item.ChildNodes | Where-Object { $_.LocalName -in @('CopyToOutputDirectory', 'CopyToPublishDirectory') -and $_.InnerText -ne 'Never' }).Count -ne 0
        }
        if ($copyItem) {
            foreach ($include in $item.GetAttribute('Include').Split(';')) { [void](Add-SnapshotFile (Resolve-Include $directory $include)) }
        }
    }
}
function Assert-InputsUnchanged {
    foreach ($entry in $inputFiles.Values) {
        [void](Assert-SafePath $entry.Source $repo)
        if ((Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Sha256) {
            throw "SNAPSHOT_SOURCE_CHANGED: $($entry.RelativePath)"
        }
    }
}
function New-Snapshot {
    Add-XmlClosure (Join-Path $repo 'tools\ShimejiConvert\ShimejiConvert.csproj')
    foreach ($relative in @('ProductVersion.props', 'src\dotNet\Xml.cs', 'src\dotNet\Animations.cs')) { [void](Add-SnapshotFile (Join-Path $repo $relative)) }
    foreach ($name in @('global.json', '.editorconfig')) {
        $path = Assert-SafePath (Join-Path $repo $name) $repo
        if ([IO.File]::Exists($path)) { [void](Add-SnapshotFile $path) }
    }
    Assert-InputsUnchanged
    Write-Json (Join-Path $evidence 'snapshot.json') @($inputFiles.Values | Sort-Object RelativePath)
}

function Invoke-Captured([string]$Label, [string]$FileName, [string[]]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FileName
    $start.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $start.WorkingDirectory = $snapshotRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($name in $converterEnvironment.Keys) { $start.EnvironmentVariables[$name] = $converterEnvironment[$name] }
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
        Label = $Label; FileName = $FileName; Arguments = $Arguments; WorkingDirectory = $snapshotRoot
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
    if (@($lines | Where-Object { $_ -eq $tickMarker }).Count -ne 1) {
        throw "source-ir: tick emitter execution witness missing for $Label"
    }
    foreach ($caseName in $tickCaseNames) {
        if (@($lines | Where-Object { $_ -eq ('tick-pack-emitter PASS ' + $caseName) }).Count -ne 1) {
            throw "source-ir: positive tick emitter case $caseName missing for $Label"
        }
    }
    if (@($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' }).Count -ne 0 -or $lines -notcontains 'SELFTEST PASS' -or
        $lines -notcontains 'SELFTEST-COUNT: 11' -or $lines -notcontains 'emitter self-test: classic checks executed') {
        throw "source-ir: contradictory positive result for $Label"
    }
    return $result.Record
}

function Assert-ClassicContinuation([string[]]$Lines) {
    if ($Lines -notcontains 'emitter self-test: classic checks executed' -or
        @($Lines | Where-Object { $_ -like 'classic PASS:*' }).Count -ne 1 -or
        $Lines -notcontains 'emitter suites FAIL: tick suite failed; classic suite passed') {
        throw 'CLASSIC_CONTINUATION_WITNESS: tick failure prevented the classic emitter checks'
    }
}

function Invoke-ClassicContinuation {
    $emitterBytes = [IO.File]::ReadAllBytes($emitter)
    $testBytes = [IO.File]::ReadAllBytes($emitterSelfTest)
    $originals[$emitter] = $emitterBytes
    $originals[$emitterSelfTest] = $testBytes
    $source = $utf8.GetString($emitterBytes)
    $tests = $utf8.GetString($testBytes)
    $old = 'Edge(root.Id, Int(root.Source, "Frequency", null), root.Condition)'
    $replacement = 'Edge(root.Id, Math.Max(1, Int(root.Source, "Frequency", null)), root.Condition)'
    $call = 'bool tickResult = TickPackEmitterSelfTest.Run(out tickDetail);'
    if ([regex]::Matches($source, [regex]::Escape($old)).Count -ne 1 -or
        [regex]::Matches($tests, [regex]::Escape($call)).Count -ne 1) {
        throw 'CLASSIC_CONTINUATION_WITNESS: unique mutation anchors missing'
    }
    $records = New-Object 'Collections.Generic.List[object]'
    $script:touched = $true
    try {
        Write-ScratchBytes $emitter ($utf8.GetBytes($source.Replace($old, $replacement)))
        foreach ($leg in @('control', 'early-return')) {
            if ($leg -eq 'early-return') {
                $regression = $call + [Environment]::NewLine + '            if (!tickResult) { detail = tickDetail; return false; }'
                Write-ScratchBytes $emitterSelfTest ($utf8.GetBytes($tests.Replace($call, $regression)))
            }
            $beforeHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
            $beforeTicks = [IO.File]::GetLastWriteTimeUtc($artifact).Ticks
            $built = Invoke-Build ('classic-continuation-' + $leg + '-build') -NoRestore
            if ($built.LastWriteUtcTicks -le $beforeTicks -or $built.Sha256 -eq $beforeHash.ToLowerInvariant()) {
                throw 'CLASSIC_CONTINUATION_WITNESS: changed rebuilt artifact missing'
            }
            $run = Invoke-Captured ('classic-continuation-' + $leg) $executable @('selftest')
            $lines = @($run.Output -split '\r?\n' | ForEach-Object { $_.Trim() })
            $fails = @($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' })
            if ($run.Record.ExitCode -ne 1 -or $run.Record.TimedOut -or $fails.Count -ne 1 -or
                $fails[0] -notmatch '^FAIL TickPackEmitterSelfTest/graph-bindings-and-origins:' -or
                $lines -notcontains $tickMarker -or $lines -notcontains 'SELFTEST FAIL' -or
                $lines -notcontains 'SELFTEST-COUNT: 11') {
                throw ('CLASSIC_CONTINUATION_WITNESS: full-suite tick fault did not execute as expected: ' + $run.Record.Stdout)
            }
            $fired = $false
            try { Assert-ClassicContinuation $lines }
            catch {
                if (-not $_.Exception.Message.StartsWith('CLASSIC_CONTINUATION_WITNESS:', [StringComparison]::Ordinal)) { throw }
                if ($leg -eq 'control') { throw }
                $fired = $true
            }
            if ($leg -eq 'early-return' -and -not $fired) { throw 'CLASSIC_CONTINUATION_WITNESS: early return survived' }
            $records.Add([pscustomobject]@{ Leg=$leg; Status= $(if ($fired) { 'FIRED' } else { 'CONTROL PASS' })
                Witness='CLASSIC_CONTINUATION_WITNESS'; Artifact=$built; Execution=$run.Record })
            Write-Json (Join-Path $evidence 'classic-continuation.json') ($records.ToArray())
            Write-Host ('CLASSIC_CONTINUATION_WITNESS ' + $leg + ': ' + $(if ($fired) { 'FIRED' } else { 'CONTROL PASS' }))
        }
    }
    finally {
        Write-ScratchBytes $emitter $emitterBytes
        Write-ScratchBytes $emitterSelfTest $testBytes
    }
    return $records.ToArray()
}

$copyWitnesses = Test-CopyRules
New-Snapshot
Write-Host 'mutationMode=scratch'
if ($PrepareOnly) {
    Write-Json (Join-Path $evidence 'results.json') ([ordered]@{
        Status='prepared'; mutationMode='scratch'; Snapshot=$snapshotRoot; InputCount=$inputFiles.Count
        SourceBytesUnchanged=$true; CopyRuleWitnesses=$copyWitnesses; BuildExecuted=$false
    })
    Write-Host ('Source IR evidence: ' + $evidence)
    Write-Host ("SOURCE-IR SNAPSHOT PREPARED ($($inputFiles.Count) explicit inputs; no build or execution)")
    exit 0
}

$mutations = @(
    [pscustomobject]@{ Name = 'attribute-value'; Path = $model; Witness = 'ordered-reference-overrides'
        Old = 'RawValue = rawValue;'
        New = 'RawValue = "";' },
    [pscustomobject]@{ Name = 'child-order'; Path = $parser; Witness = 'ordered-reference-overrides'
        Old = '                foreach (XElement child in element.Elements()) children.Add(nodes[child]);'
        New = '                foreach (XElement child in element.Elements().Reverse()) children.Add(nodes[child]);' },
    [pscustomobject]@{ Name = 'actions-source-attachment'; Path = $model; Witness = 'ordered-reference-overrides'
        Old = 'Actions = actions;'
        New = 'Actions = null;' },
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
        Old = 'key, line.HasLineInfo() ? line.LineNumber : 0,'
        New = 'key, fileName.StartsWith("base-", StringComparison.Ordinal) ? 0 : (line.HasLineInfo() ? line.LineNumber : 0),' },
    [pscustomobject]@{ Name = 'raw-source-sibling-name'; Path = $parser; Witness = 'source-path-raw-ordinals'
        Old = 'sourceSiblings.TryGetValue(sourceName, out sourcePrevious);'
        New = 'sourceSiblings.TryGetValue(name, out sourcePrevious);' },
    [pscustomobject]@{ Name = 'japanese-resistance-x'; Path = $parser; Witness = 'source-schema-resolution'
        Old = '{ "空気抵抗X", "RegistanceX" }'
        New = '{ "空気抵抗X", "ResistanceX" }' },
    [pscustomobject]@{ Name = 'japanese-resistance-y'; Path = $parser; Witness = 'source-schema-resolution'
        Old = '{ "空気抵抗Y", "RegistanceY" }'
        New = '{ "空気抵抗Y", "ResistanceY" }' },
    [pscustomobject]@{ Name = 'base-resistance-read-name'; Path = $parser; Witness = 'source-schema-resolution'
        Old = '{ "ResistanceX", "RegistanceX" }'
        New = '{ "ResistanceX", "ResistanceX" }' },
    [pscustomobject]@{ Name = 'schema-root-children'; Path = $parser; Witness = 'source-schema-resolution'
        Old = 'document.Root.Elements().Any(e => e.Annotation<SourceXmlName>().QualifiedName == "動作リスト"'
        New = 'document.Root.Descendants().Any(e => e.Annotation<SourceXmlName>().QualifiedName == "動作リスト"' },
    [pscustomobject]@{ Name = 'japanese-base-fallback'; Path = $parser; Witness = 'source-schema-resolution'
        Old = '            var byQualifiedName = new Dictionary<string, string>(StringComparer.Ordinal);'
        New = ('            if (japanese) values["ImageRight"] = "右画像";' + [Environment]::NewLine +
               '            var byQualifiedName = new Dictionary<string, string>(StringComparer.Ordinal);') },
    [pscustomobject]@{ Name = 'british-element-ignored'; Path = $parser; Witness = 'aliases-and-collisions'
        Old = 'return schema.TryGetValue(qualifiedName, out key) ? key : null;'
        New = 'return qualifiedName == "Behaviour" ? "Behaviour" : (schema.TryGetValue(qualifiedName, out key) ? key : null);' },
    [pscustomobject]@{ Name = 'nextbehavior-ignored'; Path = $parser; Witness = 'behavior-wrappers-and-add'
        Old = 'return schema.TryGetValue(qualifiedName, out key) ? key : null;'
        New = 'return qualifiedName == "NextBehavior" ? "NextBehaviourList" : (schema.TryGetValue(qualifiedName, out key) ? key : null);' },
    [pscustomobject]@{ Name = 'schema-attribute-resolution'; Path = $model; Witness = 'aliases-and-collisions'
        Old = 'attribute.SchemaKey != null && string.Equals(attribute.SchemaKey, schemaKey, System.StringComparison.Ordinal)'
        New = 'string.Equals(schemaKey == "Name" ? attribute.Name : attribute.SchemaKey, schemaKey, System.StringComparison.Ordinal)' },
    [pscustomobject]@{ Name = 'qualified-element-name'; Path = $parser; Witness = 'qualified-names-and-depth'
        Old = 'QualifiedName = reader.Name, Prefix = reader.Prefix };'
        New = 'QualifiedName = reader.LocalName, Prefix = reader.Prefix };' },
    [pscustomobject]@{ Name = 'qualified-attribute-name'; Path = $parser; Witness = 'qualified-names-and-depth'
        Old = 'QualifiedName = reader.Name, Prefix = reader.Prefix,'
        New = 'QualifiedName = reader.Name == "a:Name" ? reader.LocalName : reader.Name, Prefix = reader.Prefix,' },
    [pscustomobject]@{ Name = 'direct-text-only'; Path = $parser; Witness = 'qualified-names-and-depth'
        Old = 'string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value))'
        New = 'element.Value' },
    [pscustomobject]@{ Name = 'source-pack-relative-path'; Path = $model; Witness = 'source-pack-context'
        Old = 'PackRelativePath = packRelativePath ?? fileName;'
        New = 'PackRelativePath = fileName;' },
    [pscustomobject]@{ Name = 'source-image-set-context'; Path = $model; Witness = 'source-pack-context'
        Old = 'ImageSet = imageSet;'
        New = 'ImageSet = null;' },
    [pscustomobject]@{ Name = 'source-root-japanese-precedence'; Path = $parser; Witness = 'source-pack-context'
        Old = '            if (File.Exists(Path.Combine(rootConf, japaneseName))) selected = Path.Combine(rootConf, japaneseName);'
        New = '            if (!File.Exists(Path.Combine(rootConf, defaultName)) && File.Exists(Path.Combine(rootConf, japaneseName))) selected = Path.Combine(rootConf, japaneseName);' },
    [pscustomobject]@{ Name = 'source-member-english-precedence'; Path = $parser; Witness = 'source-pack-context'
        Old = ': new[] { "actions.xml", "動作.xml",'
        New = ': new[] { "動作.xml", "actions.xml",' },
    [pscustomobject]@{ Name = 'depth-first-invalid'; Path = $parser; Witness = 'qualified-names-and-depth'
        Old = 'reader.Depth + 1 > MaximumSourceDepth'
        New = 'reader.Depth > MaximumSourceDepth' },
    [pscustomobject]@{ Name = 'boundary-pack-path-escape'; Path = $parser; Witness = 'source-pack-context'
        Old = 'p == ".." || p == "." || p.Length == 0'
        New = 'p == "." || p.Length == 0' },
    [pscustomobject]@{ Name = 'boundary-character-limit'; Path = $parser; Witness = 'qualified-names-and-depth'
        Old = 'MaxCharactersInDocument = MaximumSourceCharacters'
        New = 'MaxCharactersInDocument = 0' },
    [pscustomobject]@{ Name = 'tick-root-zero-frequency'; Path = $emitter; Witness = 'graph-bindings-and-origins'
        Old = 'Edge(root.Id, Int(root.Source, "Frequency", null), root.Condition)'
        New = 'Edge(root.Id, Math.Max(1, Int(root.Source, "Frequency", null)), root.Condition)' },
    [pscustomobject]@{ Name = 'tick-next-reference-bindings'; Path = $emitter; Witness = 'graph-bindings-and-origins'
        Old = 'EmitBehaviour(entry, when, id);'
        New = 'EmitBehaviour(_roots[name].Source, when, id);' },
    [pscustomobject]@{ Name = 'tick-next-reference-origin'; Path = $emitter; Witness = 'graph-bindings-and-origins'
        Old = '_behaviourMap.Add(Key(id), Ref(_source.Behaviors, source));'
        New = '_behaviourMap.Add(Key(id), Ref(_source.Behaviors, _roots[name].Source));' },
    [pscustomobject]@{ Name = 'tick-child-order'; Path = $emitter; Witness = 'graph-bindings-and-origins'
        Old = 'children.Add(reference);'
        New = 'children.AddFirst(reference);' },
    [pscustomobject]@{ Name = 'tick-frame-member-path'; Path = $emitter; Witness = 'sprite-pixels-and-identity'
        Old = 'Image = "img/" + _member + "/" + relative, Path = path'
        New = 'Image = relative, Path = path' },
    [pscustomobject]@{ Name = 'tick-below-anchor-pixels'; Path = $emitter; Witness = 'sprite-pixels-and-identity'
        Old = 'height = _frames.Max(frame => frame.Height);'
        New = 'height = _frames.Max(frame => frame.Height) + 1;' },
    [pscustomobject]@{ Name = 'tick-lifecycle-born-count'; Path = $emitter; Witness = 'lifecycle-parameters'
        Old = 'act.Add(MemberParameter("param", attribute, null));'
        New = 'if (attribute.Name != "BornCount") act.Add(MemberParameter("param", attribute, null));' },
    [pscustomobject]@{ Name = 'tick-expression-refusal'; Path = $emitter; Witness = 'expression-refusal'
        Old = 'if (attribute.Cache != ShimejiSourceCache.Literal) Fail(source, "expressions are not lowered yet: " + attribute.QualifiedName);'
        New = 'if (attribute.Cache != ShimejiSourceCache.Literal && attribute.Name == "__never__") Fail(source, "expressions are not lowered yet: " + attribute.QualifiedName);' },
    [pscustomobject]@{ Name = 'tick-mixed-width-refusal'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'width = _frames.Max(frame => frame.Width)'
        New = 'width = _frames.Max(frame => frame.Width) + 1' },
    [pscustomobject]@{ Name = 'geometry-cell-height'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'height = _frames.Max(frame => frame.Height);'
        New = 'height = _frames.Max(frame => frame.Height) + 1;' },
    [pscustomobject]@{ Name = 'geometry-cell-top-left'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'CopyPixels(bitmap, destination, (i % tilesX) * width, (i / tilesX) * height);'
        New = 'CopyPixels(bitmap, destination, (i % tilesX) * width + (frame.Width < width ? 1 : 0), (i / tilesX) * height);' },
    [pscustomobject]@{ Name = 'geometry-pose-anchor-x'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'new XAttribute("ax", effective.X)'; New = 'new XAttribute("ax", 0)' },
    [pscustomobject]@{ Name = 'geometry-pose-anchor-y'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'new XAttribute("ay", effective.Y)'; New = 'new XAttribute("ay", 0)' },
    [pscustomobject]@{ Name = 'geometry-pose-width'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'new XAttribute("w", _frames[frame].Width)'; New = 'new XAttribute("w", 1)' },
    [pscustomobject]@{ Name = 'geometry-pose-height'; Path = $emitter; Witness = 'mixed-pose-geometry'
        Old = 'new XAttribute("h", _frames[frame].Height)'; New = 'new XAttribute("h", 1)' },
    [pscustomobject]@{ Name = 'geometry-right-frame'; Path = $emitter; Witness = 'explicit-right-geometry'
        Old = 'new XAttribute("right", rightFrame)'; New = 'new XAttribute("right", frame)' },
    [pscustomobject]@{ Name = 'geometry-right-width'; Path = $emitter; Witness = 'explicit-right-geometry'
        Old = 'new XAttribute("rw", _frames[rightFrame].Width)'; New = 'new XAttribute("rw", _frames[frame].Width)' },
    [pscustomobject]@{ Name = 'geometry-right-height'; Path = $emitter; Witness = 'explicit-right-geometry'
        Old = 'new XAttribute("rh", _frames[rightFrame].Height)'; New = 'new XAttribute("rh", _frames[frame].Height)' },
    [pscustomobject]@{ Name = 'geometry-pair-cache-first'; Path = $emitter; Witness = 'pair-anchor-cache'
        Old = 'if (!_pairAnchors.ContainsKey(key)) _pairAnchors.Add(key, new PairAnchor { X = x, Y = y });'
        New = '_pairAnchors[key] = new PairAnchor { X = x, Y = y };' },
    [pscustomobject]@{ Name = 'geometry-pair-cache-effective-anchor'; Path = $emitter; Witness = 'pair-anchor-cache'
        Old = 'PairAnchor effective = _pairAnchors[PairKey(image, right, child)];'
        New = 'PairAnchor effective = new PairAnchor { X = x, Y = y };' },
    [pscustomobject]@{ Name = 'review-map-effective-anchor'; Path = $emitter; Witness = 'pair-anchor-cache'
        Old = 'int frame = AddFrame(image, effective.X, effective.Y, child);'
        New = 'int frame = AddFrame(image, x, y, child);' },
    [pscustomobject]@{ Name = 'review-map-effective-right-anchor'; Path = $emitter; Witness = 'pair-anchor-cache'
        Old = 'int rightFrame = AddFrame(right, effective.X, effective.Y, child);'
        New = 'int rightFrame = AddFrame(right, x, y, child);' },
    [pscustomobject]@{ Name = 'review-composite-cycle'; Path = $emitter; Witness = 'composite-cycle-refusal'
        Old = 'if (active.Contains(name))'
        New = 'if (active.Contains(name) && _member == "__never__")' },
    [pscustomobject]@{ Name = 'review-composite-depth'; Path = $emitter; Witness = 'composite-depth-refusal'
        Old = 'if (path.Count >= ActivationDepthLimit)'
        New = 'if (path.Count >= ActivationDepthLimit && _member == "__never__")' },
    [pscustomobject]@{ Name = 'review-composite-cached-depth'; Path = $emitter; Witness = 'composite-depth-refusal'
        Old = 'if (path.Count + cached.Depth > ActivationDepthLimit)'
        New = 'if (path.Count + cached.Depth > ActivationDepthLimit && _member == "__never__")' },
    [pscustomobject]@{ Name = 'review-composite-size'; Path = $emitter; Witness = 'composite-size-refusal'
        Old = 'if (count > ActivationSizeLimit)'
        New = 'if (count > ActivationSizeLimit && _member == "__never__")' },
    [pscustomobject]@{ Name = 'review-hidden-dot'; Path = $emitter; Witness = 'hidden-pack-members'
        Old = 'return name.StartsWith(".", StringComparison.Ordinal)'
        New = 'return name == "__never__"' },
    [pscustomobject]@{ Name = 'review-hidden-unused-case'; Path = $emitter; Witness = 'hidden-pack-members'
        Old = '|| string.Equals(name, "unused", StringComparison.OrdinalIgnoreCase);'
        New = '|| string.Equals(name, "unused", StringComparison.Ordinal);' },
    [pscustomobject]@{ Name = 'review-hidden-role'; Path = $emitter; Witness = 'hidden-pack-members'
        Old = 'hidden ? "helper" : "form"'
        New = 'hidden ? "form" : "form"' },
    [pscustomobject]@{ Name = 'review-explicit-pack-metadata'; Path = $emitter; Witness = 'explicit-pack-metadata'
        Old = 'if (prepared.Any(m => m.CrossMember) || mainMember != null || prepared.Any(m => HiddenInSourceChooser(m.Name)))'
        New = 'if (prepared.Any(m => m.CrossMember))' },
    [pscustomobject]@{ Name = 'review-hidden-pack-main-required'; Path = $emitter; Witness = 'explicit-pack-metadata'
        Old = '|| prepared.Any(m => HiddenInSourceChooser(m.Name)))'
        New = ')' },
    [pscustomobject]@{ Name = 'review-source-pack-identity'; Path = $emitter; Witness = 'pack-source-identity'
        Old = 'm.Name + "\n" + m.SourceIdentity'
        New = 'm.Name + "\n" + m.Xml' },
    [pscustomobject]@{ Name = 'geometry-pair-preload-nested-first'; Path = $emitter; Witness = 'pair-anchor-preload-order'
        Old = 'foreach (ShimejiSourceElement animation in action.Children.Where(n => n.Kind == ShimejiSourceKind.Animation))'
        New = 'foreach (ShimejiSourceElement nestedEarly in action.Children.Where(n => n.Kind == ShimejiSourceKind.Action)) IndexPairAnchors(nestedEarly); foreach (ShimejiSourceElement animation in action.Children.Where(n => n.Kind == ShimejiSourceKind.Animation))' },
    [pscustomobject]@{ Name = 'geometry-right-only-refusal'; Path = $emitter; Witness = 'right-only-refusal'
        Old = 'else if (right != null) Fail(child, "TICK_RIGHT_ONLY_UNSUPPORTED:'
        New = 'else if (right != null && _member == "__never__") Fail(child, "TICK_RIGHT_ONLY_UNSUPPORTED:' },
    [pscustomobject]@{ Name = 'geometry-right-path-refusal'; Path = $emitter; Witness = 'right-path-refusal'
        Old = 'if (right != null && (right.Length == 0 || (right[0] != ''/'' && right[0] != ''\\'')))'
        New = 'if (right != null && _member == "__never__")' },
    [pscustomobject]@{ Name = 'geometry-pair-identity-case'; Path = $emitter; Witness = 'pair-anchor-identity'
        Old = 'private readonly Dictionary<string, PairAnchor> _pairAnchors = new Dictionary<string, PairAnchor>(StringComparer.Ordinal);'
        New = 'private readonly Dictionary<string, PairAnchor> _pairAnchors = new Dictionary<string, PairAnchor>(StringComparer.OrdinalIgnoreCase);' },
    [pscustomobject]@{ Name = 'geometry-pair-identity-explicit-right'; Path = $emitter; Witness = 'pair-anchor-identity'
        Old = '+ (right == null ? "" : prefix + SpriteRelative(right, source).Replace(''/'', ''\\''));'
        New = '+ "";' },
    [pscustomobject]@{ Name = 'geometry-pair-identity-repeated-separators'; Path = $emitter; Witness = 'pair-anchor-identity'
        Old = 'return string.Join("/", relative.Split(new[] { ''/'' }, StringSplitOptions.RemoveEmptyEntries));'
        New = 'return relative;' },
    [pscustomobject]@{ Name = 'tick-existing-output-refusal'; Path = $emitter; Witness = 'existing-output-refusal'
        Old = 'if (Directory.Exists(outDir) || File.Exists(outDir))'
        New = 'if (File.Exists(outDir))' },
    [pscustomobject]@{ Name = 'tick-private-action-reference'; Path = $emitter; Witness = 'private-action-reference-refusal'
        Old = '_globalActions.Contains(target)'
        New = '_actions.ContainsKey(target)' },
    [pscustomobject]@{ Name = 'tick-private-behavior-action'; Path = $emitter; Witness = 'private-behavior-action-refusal'
        Old = '_globalActions.Contains(runs)'
        New = '_actions.ContainsKey(runs)' },
    [pscustomobject]@{ Name = 'tick-java-only-literal'; Path = $emitter; Witness = 'java-literal-refusal'
        Old = 'if (javaOnlyNumber) throw new InvalidDataException('
        New = 'if (javaOnlyNumber && literal == "__never__") throw new InvalidDataException(' },
    [pscustomobject]@{ Name = 'review-materialization-cumulative'; Path = $parser; Witness = 'source-materialization-budget'
        Old = 'materializationCharacters += 32 + keyPrefixCharacters + 2 * canonical + raw;'
        New = 'materializationCharacters = 32 + keyPrefixCharacters + 2 * canonical + raw;' },
    [pscustomobject]@{ Name = 'review-materialization-attributes'; Path = $parser; Witness = 'source-materialization-budget'
        Old = 'materializationCharacters += 16 + keyPrefixCharacters + canonical + Canon(reader.LocalName).Length + 13 + reader.Name.Length;'
        New = 'materializationCharacters += 0;' },
    [pscustomobject]@{ Name = 'review-materialization-prefix'; Path = $parser; Witness = 'source-materialization-budget'
        Old = 'int keyPrefixCharacters = sourceFileName.Length + 1;'
        New = 'int keyPrefixCharacters = 1;' },
    [pscustomobject]@{ Name = 'review-materialization-boundary'; Path = $parser; Witness = 'source-materialization-budget'
        Old = 'materializationCharacters += 32 + keyPrefixCharacters + 2 * canonical + raw;'
        New = 'materializationCharacters += 33 + keyPrefixCharacters + 2 * canonical + raw;' },
    [pscustomobject]@{ Name = 'review-tick-packs-feature'; Path = $emitter; Witness = 'cross-member-pack-metadata'
        Old = 'features.Value += " packs";'; New = 'features.Value += "";' },
    [pscustomobject]@{ Name = 'review-tick-own-member-omission'; Path = $emitter; Witness = 'cross-member-pack-metadata'
        Old = 'IsMemberParameter(attribute.Name) && attribute.RawValue == _member'
        New = 'IsMemberParameter(attribute.Name) && attribute.RawValue == _member + "__never__"' },
    [pscustomobject]@{ Name = 'review-tick-own-binding-reset'; Path = $emitter; Witness = 'own-set-binding-reset'
        Old = 'if (inherited == null || inherited.RawValue == _member) return null;'
        New = 'if (inherited != null || inherited == null) return null;' },
    [pscustomobject]@{ Name = 'review-tick-main-role'; Path = $emitter; Witness = 'cross-member-pack-metadata'
        Old = 'member.Name == mainMember ? "main" : hidden ? "helper" : "form"'
        New = 'member.Name == mainMember ? "form" : hidden ? "helper" : "form"' },
    [pscustomobject]@{ Name = 'review-tick-pack-id'; Path = $emitter; Witness = 'cross-member-pack-metadata'
        Old = 'new XAttribute("id", packId)'; New = 'new XAttribute("id", packId + member.Name)' },
    [pscustomobject]@{ Name = 'review-tick-action-schema'; Path = $emitter; Witness = 'action-schema-refusal'
        Old = '_source.Actions.Schema != ShimejiSourceSchema.Base'
        New = '_source.Actions.Schema != ShimejiSourceSchema.Base && _member == "__never__"' },
    [pscustomobject]@{ Name = 'review-tick-ignored-reference-lists'; Path = $emitter; Witness = 'ignored-reference-lists'
        Old = 'bool rootDefinition = source.Kind == ShimejiSourceKind.Behavior;'; New = 'bool rootDefinition = true;' },
    [pscustomobject]@{ Name = 'review-tick-missing-add'; Path = $emitter; Witness = 'missing-add-false'
        Old = 'add = Bool(list, "Add");'; New = 'add = list.Attribute("Add") == null || Bool(list, "Add");' },
    [pscustomobject]@{ Name = 'review-tick-declaration-order'; Path = $emitter; Witness = 'declaration-order'
        Old = 'route.Element(Ns + "sequence").Add(Edge(root.Id, Int(root.Source, "Frequency", null), root.Condition));'
        New = 'route.Element(Ns + "sequence").AddFirst(Edge(root.Id, Int(root.Source, "Frequency", null), root.Condition));' },
    [pscustomobject]@{ Name = 'review-tick-duplicate-root'; Path = $emitter; Witness = 'duplicate-root-last-definition'
        Old = '_roots[name] = new RootBehaviour { Source = node, Condition = Combine(condition, SelectionCondition(node)) };'
        New = 'if (!_roots.ContainsKey(name)) _roots[name] = new RootBehaviour { Source = node, Condition = Combine(condition, SelectionCondition(node)) };' },
    [pscustomobject]@{ Name = 'review-tick-enclosing-conditions'; Path = $emitter; Witness = 'enclosing-conditions'
        Old = 'return first == null ? second : second == null ? first : "(" + first + ") && (" + second + ")";'
        New = 'return second;' },
    [pscustomobject]@{ Name = 'review-tick-cross-schema'; Path = $emitter; Witness = 'cross-schema-refusal'
        Old = '_source.Actions.Schema != _source.Behaviors.Schema'
        New = '_source.Actions.Schema != _source.Behaviors.Schema && _member == "__never__"' },
    [pscustomobject]@{ Name = 'review-tick-toggle'; Path = $emitter; Witness = 'toggle-refusal'
        Old = 'if (Bool(source, "Toggleable")) Fail'; New = 'if (Bool(source, "Toggleable") && _member == "__never__") Fail' },
    [pscustomobject]@{ Name = 'review-tick-information'; Path = $emitter; Witness = 'information-refusal'
        Old = '_source.Information != null'; New = '_source.Information != null && _member == "__never__"' },
    [pscustomobject]@{ Name = 'review-tick-prepare-all-members'; Path = $emitter; Witness = 'prepare-all-members'
        Old = 'prepared.Add(new MemberBuilder(packRoot, member, source).Build());'
        New = 'prepared.Add(new MemberBuilder(packRoot, member, source).Build()); if (prepared.Count == 1) { string partial = Path.Combine(outDir, member); Directory.CreateDirectory(partial); File.WriteAllText(Path.Combine(partial, "animations.xml"), prepared[0].Xml); }' }
)
$emitterText = $utf8.GetString([IO.File]::ReadAllBytes($emitter))
$pixelStart = $emitterText.IndexOf('BitmapData destination = sheet.LockBits(', [StringComparison]::Ordinal)
$pixelEndToken = 'finally { sheet.UnlockBits(destination); }'
$pixelEnd = $emitterText.IndexOf($pixelEndToken, [StringComparison]::Ordinal)
if ($pixelStart -lt 0 -or $pixelEnd -lt $pixelStart) { throw 'source-ir: compositor mutation anchors missing' }
$pixelOld = $emitterText.Substring($pixelStart, $pixelEnd + $pixelEndToken.Length - $pixelStart)
$pixelNew = 'using (Graphics graphics = Graphics.FromImage(sheet)) { graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; graphics.Clear(Color.Transparent); for (int i = 0; i < _frames.Count; i++) { Frame frame = _frames[i]; using (var bitmap = new Bitmap(frame.Path)) graphics.DrawImageUnscaled(bitmap, (i % tilesX) * width, (i / tilesX) * height); } }'
$mutations += [pscustomobject]@{ Name='review-tick-sprite-dpi'; Path=$emitter; Witness='sprite-dpi72'; Old=$pixelOld; New=$pixelNew }
$mutations += [pscustomobject]@{ Name='review-tick-sprite-alpha'; Path=$emitter; Witness='sprite-pixels-and-identity'; Old=$pixelOld; New=$pixelNew }
$patterns = @($Only | ForEach-Object { $_.Split(',') } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$selectedMutations = @($mutations | Where-Object { $name=$_.Name; @($patterns | Where-Object { $name -like $_ }).Count -ne 0 })
if ($Mutate -and $selectedMutations.Count -eq 0) { throw "source-ir: filter '$Only' selected no mutations" }
$originals = @{}
$results = New-Object 'Collections.Generic.List[object]'
$summary = [ordered]@{ Status = 'running'; Evidence = $evidence; Mutate = [bool]$Mutate; mutationMode='scratch'
    Snapshot=$snapshotRoot; InputCount=$inputFiles.Count; CopyRuleWitnesses=$copyWitnesses; Results = $results }
$touched = $false
$failure = $null
try {
    $summary.BaselineArtifact = Invoke-Build 'baseline-build'
    $summary.BaselineExecution = Invoke-Positive 'baseline-selftest'
    if ($CheckClassicContinuation) { $summary.ClassicContinuation = Invoke-ClassicContinuation }
    if ($Mutate) {
        foreach ($path in @($selectedMutations | Select-Object -ExpandProperty Path -Unique)) {
            [void](Assert-SafePath $path $snapshotRoot)
            $originals[$path] = [IO.File]::ReadAllBytes($path)
        }
        foreach ($mutation in $selectedMutations) {
            [void](Assert-SafePath $mutation.Path $snapshotRoot)
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
                Write-ScratchBytes $mutation.Path ($utf8.GetBytes($source.Replace($mutation.Old, $mutation.New)))
                $built = Invoke-Build ($mutation.Name + '-build') -NoRestore
                # Independent witnesses may deliberately use identical production mutations (DPI and alpha).
                # Require a fresh rebuild and a change from the positive baseline, rather than from a prior mutant.
                if ($built.LastWriteUtcTicks -le $before.LastWriteUtcTicks -or $built.Sha256 -eq $summary.BaselineArtifact.Sha256) {
                    throw "source-ir: mutation $($mutation.Name) did not produce a newly built, changed engine DLL"
                }
                $group = if ($mutation.Path -eq $emitter) { 'tick-emitter/' + $mutation.Witness } else { 'source-ir' }
                $run = Invoke-Captured ($mutation.Name + '-selftest') $executable @('selftest', $group)
                $lines = @($run.Output -split '\r?\n' | ForEach-Object { $_.Trim() })
                $fails = @($lines | Where-Object { $_ -match '^FAIL(?:\s|:)' })
                $expectedSuite = if ($mutation.Path -eq $emitter) { 'TickPackEmitterSelfTest' } else { 'SourceIrSelfTest' }
                $expectedMarker = if ($mutation.Path -eq $emitter) { $tickMarker } else { $marker }
                $expected = '^FAIL ' + $expectedSuite + '/' + [regex]::Escape($mutation.Witness) + ':'
                if ($run.Record.ExitCode -ne 1 -or $run.Record.TimedOut -or $fails.Count -ne 1 -or
                    $fails[0] -notmatch $expected -or $lines -notcontains $expectedMarker -or $lines -notcontains 'SELFTEST FAIL' -or
                    $lines -notcontains 'SELFTEST-COUNT: 1') {
                    throw "source-ir: mutation $($mutation.Name) did not fire exactly its named witness; see $($run.Record.Stdout)"
                }
                $results.Add([pscustomobject]@{ Mutation = $mutation.Name; Status = 'FIRED'; Witness = $fails[0]
                    Artifact = $built; Execution = $run.Record })
                Write-Host ("FIRED $($mutation.Name): " + $fails[0])
            }
            finally {
                Write-ScratchBytes $mutation.Path $original
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
                Write-ScratchBytes $path ($originals[$path])
                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($originals[$path])) {
                    throw "source-ir: final byte-exact restoration failed for $path"
                }
            }
            $summary.RestoredArtifact = Invoke-Build 'restored-build' -NoRestore
            $summary.RestoredExecution = Invoke-Positive 'restored-selftest'
            $summary.ScratchBytesRestored = $true
        }
    }
    catch {
        $summary.Status = 'fail'
        $summary.RestoreError = $_.Exception.Message
        if ($null -eq $failure) { $failure = $_ }
    }
    try { Assert-InputsUnchanged; $summary.SourceBytesUnchanged = $true }
    catch { $summary.Status='fail'; $summary.SourceError=$_.Exception.Message; if ($null -eq $failure) { $failure=$_ } }
    Write-Json (Join-Path $evidence 'results.json') $summary
    Write-Host ("Source IR evidence: " + $evidence)
}
if ($null -ne $failure) { throw $failure }
Write-Host ("SOURCE-IR VERIFY PASS ($($results.Count) mutations fired)")
