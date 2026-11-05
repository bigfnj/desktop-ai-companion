#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$HostSourceRoot,
    [Parameter(Mandatory=$true)][string[]]$XmlPath,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$HostSourceRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($HostSourceRoot).TrimEnd([char[]]'\/')
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
$runnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (Test-Path -LiteralPath $OutputRoot) { throw 'PROBE_OUTPUT_EXISTS: choose a fresh output directory.' }
$links = @('src/LocalData/AnimationXML.cs', 'src/dotNet/SafeScript.cs', 'src/dotNet/ShimejiFormatValidator.cs',
    'src/dotNet/CompanionXmlValidator.cs', 'src/dotNet/SafeExpression.cs', 'tools/ShimejiConvert.Engine/ValidatorResources.cs', 'src/Resources/animations.xsd')
$sourceHashes = @{}
foreach ($relative in $links) {
    $path = Join-Path $HostSourceRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('PROBE_SOURCE_MISSING: ' + $relative) }
    $sourceHashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$inputHashes = @{}
$inputPaths = @()
foreach ($path in $XmlPath) {
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
    if ([IO.Path]::GetFileName($full) -cne 'animations.xml') { throw 'PROBE_INPUT_NAME: explicit animations.xml paths are required.' }
    $inputHashes[$full] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    $mapPath = Join-Path ([IO.Path]::GetDirectoryName($full)) 'parity-map.json'
    if (-not (Test-Path -LiteralPath $mapPath -PathType Leaf)) { throw ('PROBE_MAP_MISSING: ' + $mapPath) }
    $inputHashes[$mapPath] = (Get-FileHash -LiteralPath $mapPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $inputPaths += $full
}
$actualCommit = $null
$gitArgs = @('-c', ('safe.directory=' + $HostSourceRoot.Replace('\', '/')), '-c', 'core.excludesFile=', '-C', $HostSourceRoot)
if ($ExpectedCommit) {
    $actualCommit = (& git @gitArgs rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $ExpectedCommit.ToLowerInvariant()) { throw 'PROBE_HOST_COMMIT_MISMATCH' }
    & git @gitArgs diff --quiet $ExpectedCommit -- @links
    if ($LASTEXITCODE -ne 0) { throw 'PROBE_HOST_LINKED_SOURCE_DIRTY' }
}
[void][IO.Directory]::CreateDirectory($OutputRoot)
$temp = Join-Path $OutputRoot 'temp'
$data = Join-Path $OutputRoot 'data'
[void][IO.Directory]::CreateDirectory($temp)
[void][IO.Directory]::CreateDirectory($data)
$childEnv = @{
    DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT='1'; DESKTOP_AI_COMPANION_DATA_ROOT=$data;
    TEMP=$temp; TMP=$temp; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'; DOTNET_CLI_USE_MSBUILD_SERVER='0'; MSBUILDDISABLENODEREUSE='1'
}
function Invoke-ProbeChild([string]$Executable, [string[]]$Arguments, [string]$Log) {
    $quoted = @()
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"') -or $argument.Contains("`n") -or $argument.Contains("`r") -or $argument.EndsWith('\')) { throw 'PROBE_ARGUMENT_UNSUPPORTED' }
        $quoted += '"' + $argument + '"'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $OutputRoot
    foreach ($key in $childEnv.Keys) { $start.EnvironmentVariables[$key] = $childEnv[$key] }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'PROBE_CHILD_START_FAILED' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'PROBE_CHILD_TIMEOUT' }
        [IO.File]::WriteAllText($Log, $stdout.Result + $stderr.Result, (New-Object Text.UTF8Encoding($false)))
        return $process.ExitCode
    } finally { $process.Dispose() }
}
$dotnet = (Get-Command dotnet -CommandType Application).Source
$bin = Join-Path $OutputRoot 'bin'
$project = Join-Path $PSScriptRoot 'ShimejiConvert.ValidationProbe.csproj'
$buildArgs = @('build', $project, '-c', 'Release', '--nologo', '-v:minimal', '--no-incremental', '-o', $bin,
    ('-p:HostSourceRoot=' + $HostSourceRoot), ('-p:BaseIntermediateOutputPath=' + (Join-Path $OutputRoot 'obj') + '/'),
    '-p:UseSharedCompilation=false', '-nodeReuse:false', '-maxcpucount:1')
$started = [DateTime]::UtcNow
$buildExit = Invoke-ProbeChild $dotnet $buildArgs (Join-Path $OutputRoot 'build.log')
if ($buildExit -ne 0) { throw 'PROBE_BUILD_FAILED: see build.log.' }
$artifact = Join-Path $bin 'ShimejiConvert.ValidationProbe.dll'
if (-not (Test-Path -LiteralPath $artifact) -or [IO.File]::GetLastWriteTimeUtc($artifact) -lt $started) { throw 'PROBE_FRESH_BUILD_WITNESS_MISSING' }
$artifactHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
$resultPath = Join-Path $OutputRoot 'results.json'
$runArgs = @($artifact, '--results', $resultPath)
foreach ($path in $inputPaths) { $runArgs += @('--xml', $path) }
$executeExit = Invoke-ProbeChild $dotnet $runArgs (Join-Path $OutputRoot 'execution.log')
$record = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($record.artifactSha256 -cne $artifactHash) { throw 'PROBE_EXECUTED_ARTIFACT_WITNESS_MISMATCH' }
foreach ($relative in $links) {
    if ((Get-FileHash -LiteralPath (Join-Path $HostSourceRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceHashes[$relative]) { throw ('PROBE_SOURCE_CHANGED: ' + $relative) }
}
foreach ($path in $inputHashes.Keys) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $inputHashes[$path]) { throw ('PROBE_INPUT_CHANGED: ' + $path) }
}
if ($ExpectedCommit) {
    $head = (& git @gitArgs rev-parse HEAD).Trim()
    & git @gitArgs diff --quiet $ExpectedCommit -- @links
    if ($LASTEXITCODE -ne 0 -or $head -cne $actualCommit) { throw 'PROBE_HOST_SOURCE_CHANGED_DURING_RUN' }
}
if ((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $runnerSha256) { throw 'PROBE_RUNNER_CHANGED_DURING_RUN' }
[pscustomobject]@{ schema='shimeji-validation-witness/1'; hostSourceRoot=$HostSourceRoot; expectedCommit=$ExpectedCommit;
    actualCommit=$actualCommit; sourceHashes=$sourceHashes; inputHashes=$inputHashes; runnerSha256=$runnerSha256; buildArgs=$buildArgs;
    artifactSha256=$artifactHash; artifactLastWriteUtc=[IO.File]::GetLastWriteTimeUtc($artifact);
    buildExit=$buildExit; executeExit=$executeExit; coreOnly=$true; featureOverride='CompanionFormatFeatures.All';
    productionRuntimeProof=$false; result=$resultPath } | ConvertTo-Json -Depth 6 |
    Out-File -LiteralPath (Join-Path $OutputRoot 'witness.json') -Encoding utf8
Write-Output ('Evidence: ' + $OutputRoot)
if ($executeExit -ne 0 -or $record.status -ne 'pass') { exit 1 }
