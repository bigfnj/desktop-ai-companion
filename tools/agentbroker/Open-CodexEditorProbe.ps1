#requires -Version 5.1
<#
.SYNOPSIS
Prepares an isolated, development-only exact-thread GUI test. Preparation opens no GUI.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Open-CodexEditorProbe.ps1 -TargetThreadId '<authorized-test-UUID>'
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Open-CodexEditorProbe.ps1 -PreparedWorkspace '<printed-workspace>' -ExpectedTargetThreadId '<authorized-test-UUID>' -Launch
#>
[CmdletBinding()]
param(
    [string]$TargetThreadId,
    [string]$PreparedWorkspace,
    [string]$ExpectedTargetThreadId,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $repoRoot 'artifacts\agentbroker-editor-probes'
$sourceRoot = Join-Path $PSScriptRoot 'vscode-editor-probe'
$encoding = New-Object Text.UTF8Encoding($false)
$cli = $null

function Assert-ProbePath([string]$Path, [string]$AllowedRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\')
    if (($full -ne $allowed -and -not $full.StartsWith($allowed + '\', [StringComparison]::OrdinalIgnoreCase)) -or
        $full.StartsWith('\\') -or $full.Substring(3).Contains(':') -or $full -match '[\x00-\x1f]') {
        throw 'Probe path is outside its allowed ordinary local scope.'
    }
    $normalized = $full.Replace('\', '/').ToLowerInvariant()
    if ($normalized -match '/(\.git/credentials|\.config/(google-chrome|chromium)|appdata/local/(google/chrome|microsoft/edge)/user data)(/|$)' -or
        $normalized -match '/appdata/.*/mozilla/firefox(/|$)') { throw 'Protected probe path refused before access.' }
    $parts = $full.Substring([IO.Path]::GetPathRoot($full).Length).Split('\')
    foreach ($part in $parts) {
        if ($part -match '^(\.env(\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.mozilla)$' -or
            $part -match '\.(env|pem|key|p12|pfx|keystore)$') { throw 'Protected probe path refused before access.' }
    }
    $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $parts) {
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { break }
        catch [IO.DirectoryNotFoundException] { break }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse probe path refused before descent.' }
    }
    return $full
}
function Write-ProbeNew([string]$Path, [string]$Text, [string]$Root) {
    $null = Assert-ProbePath $Path $Root
    $bytes = $encoding.GetBytes($Text + "`n")
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Read-ProbeText([string]$Path, [string]$Root, [long]$Limit = 32768) {
    $null = Assert-ProbePath $Path $Root
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or $file.Length -gt $Limit) { throw 'Probe input is not a bounded ordinary file.' }
    return [IO.File]::ReadAllText($Path, $encoding)
}
function Get-ProbeHash([string]$Path, [string]$Root) {
    $null = Assert-ProbePath $Path $Root
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Confirm-CodeImageMetadata([string]$Image, [string]$Hash, [string]$Version,
    [string]$ExpectedImage, [string]$ExpectedHash, [string]$ExpectedVersion) {
    if (-not [string]::Equals([IO.Path]::GetFullPath($Image), [IO.Path]::GetFullPath($ExpectedImage), [StringComparison]::OrdinalIgnoreCase) -or
        $Hash -cne $ExpectedHash -or $Version -cne $ExpectedVersion) { throw 'Code CLI public image does not match the existing router profile.' }
}
function Get-CodeCli {
    # Consume existing pins rather than inventing another compatibility profile: the router pairs of the embedded
    # pin document (lane broker-pins moved them out of VsCodeRouterCompatibilityProfile.cs), and that file's image path.
    $profile = Read-ProbeText (Join-Path $repoRoot 'tools\AgentBroker.CodexDesktopAdapter\VsCodeRouterCompatibilityProfile.cs') $repoRoot
    $pins = @(((Read-ProbeText (Join-Path $repoRoot 'tools\AgentBroker.Pins\agentbroker-pins.json') $repoRoot) | ConvertFrom-Json).vscodeRouter)
    $expectedFormula = 'internal static string ExpectedImage\s*=>\s*Path\.Combine\(Environment\.GetFolderPath\(Environment\.SpecialFolder\.LocalApplicationData\),\s*"Programs",\s*"Microsoft VS Code",\s*"Code\.exe"\);'
    if ($pins.Count -lt 1 -or -not [regex]::IsMatch($profile, $expectedFormula)) {
        throw 'Existing Code router profile cannot be consumed safely.'
    }
    $root = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\Microsoft VS Code'
    $code = Assert-ProbePath (Join-Path $root 'Code.exe') $root
    $cmd = Assert-ProbePath (Join-Path $root 'bin\code.cmd') $root
    if (-not [IO.File]::Exists($code) -or -not [IO.File]::Exists($cmd)) { throw 'The existing profile Code installation was not found. Nothing was installed.' }
    $installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($code).ProductVersion
    # The installed version selects one reviewed pair; its hash must then match exactly, as in the adapter.
    $pin = @($pins | Where-Object { [string]$_.productVersion -ceq $installedVersion }) | Select-Object -First 1
    if ($null -eq $pin) { throw 'Code CLI public image does not match the existing router profile.' }
    Confirm-CodeImageMetadata $code (Get-ProbeHash $code $root).ToUpperInvariant() $installedVersion `
        (Join-Path $root 'Code.exe') ([string]$pin.executableSha256) ([string]$pin.productVersion)
    $body = Read-ProbeText $cmd $root 8192
    $match = [regex]::Match($body, '(?m)^"%~dp0\.\.\\Code\.exe" "%~dp0\.\.\\(?<cli>[^"\r\n]+\\resources\\app\\out\\cli\.js)" %\*\r?$')
    if (-not $match.Success -or $body -notmatch 'set ELECTRON_RUN_AS_NODE=1') { throw 'Installed Code CLI wrapper is not recognized.' }
    $entry = Assert-ProbePath (Join-Path $root $match.Groups['cli'].Value) $root
    if (-not [IO.File]::Exists($entry)) { throw 'Installed Code CLI entry point is unavailable.' }
    return [pscustomobject]@{ Exe = $code; Cmd = $cmd; Cli = $entry }
}
function Confirm-Prepared([string]$WorkspacePath, [string]$ExpectedTarget) {
    $workspace = Assert-ProbePath $WorkspacePath $artifactRoot
    $root = Split-Path -Parent $workspace
    $idMatch = [regex]::Match((Split-Path -Leaf $root), '^editor-probe-([0-9a-f]{32})$')
    if ((Split-Path -Parent $root) -ne $artifactRoot -or -not $idMatch.Success -or
        (Split-Path -Leaf $workspace) -ne 'probe.code-workspace') { throw 'Only an exact prepared probe workspace is accepted.' }
    $manifestPath = Join-Path $root 'prepared.json'
    $manifest = (Read-ProbeText $manifestPath $root) | ConvertFrom-Json
    $document = (Read-ProbeText $workspace $root) | ConvertFrom-Json
    $parsed = [guid]::Empty
    if ($manifest.schema -ne 1 -or $manifest.kind -ne 'agentbroker-vscode-editor-probe' -or
        $manifest.developmentOnly -ne $true -or $manifest.probeId -cne $idMatch.Groups[1].Value -or
        -not [guid]::TryParseExact($manifest.targetThreadId, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty -or
        $manifest.targetThreadId -cne $parsed.ToString('D') -or
        $manifest.workspace -cne 'probe.code-workspace' -or $manifest.result -cne 'result.json' -or
        (@($document.PSObject.Properties.Name | Sort-Object) -join ',') -cne 'folders,settings' -or
        @($document.settings.PSObject.Properties).Count -ne 2 -or
        $document.settings.'agentbrokerEditorProbe.targetThreadId' -cne $manifest.targetThreadId -or
        $document.settings.'agentbrokerEditorProbe.preparationId' -cne $manifest.probeId -or
        @($document.folders).Count -ne 1 -or $document.folders[0].path -cne 'workspace' -or
        @($document.folders[0].PSObject.Properties).Count -ne 1) {
        throw 'Prepared probe manifest or owning workspace settings are invalid.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTarget)) {
        $expected = [guid]::Empty
        if (-not [guid]::TryParseExact($ExpectedTarget, 'D', [ref]$expected) -or $expected -eq [guid]::Empty -or
            $manifest.targetThreadId -cne $expected.ToString('D')) { throw 'Prepared target does not match the explicitly expected authorized thread.' }
    }
    $extension = Join-Path $root 'extension'
    foreach ($name in @('extension.js', 'package.json')) {
        $source = Assert-ProbePath (Join-Path $sourceRoot $name) $repoRoot
        $copy = Assert-ProbePath (Join-Path $extension $name) $root
        if ((Get-ProbeHash $source $repoRoot) -cne $manifest.sourceHashes.$name -or
            (Get-ProbeHash $copy $root) -cne $manifest.sourceHashes.$name) { throw 'Prepared probe source changed.' }
    }
    $null = Assert-ProbePath (Join-Path $root 'workspace') $root
    foreach ($name in @('result.json', 'launch-reservation.json')) {
        $output = Assert-ProbePath (Join-Path $root $name) $root
        if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) { throw 'Prepared probe has already been used; nothing will be overwritten.' }
    }
    return [pscustomobject]@{ Root = $root; Workspace = $workspace; Extension = $extension; Manifest = $manifestPath }
}

if ($Launch -and [string]::IsNullOrWhiteSpace($PreparedWorkspace)) {
    throw 'Launch requires a separately prepared and reviewed workspace; default preparation opens no GUI.'
}
if ($Launch -and [string]::IsNullOrWhiteSpace($ExpectedTargetThreadId)) { throw 'Launch requires an explicit expected authorized thread UUID.' }
if (-not [string]::IsNullOrWhiteSpace($PreparedWorkspace)) {
    if (-not [string]::IsNullOrWhiteSpace($TargetThreadId)) { throw 'Do not replace the target of an existing prepared workspace.' }
    $prepared = Confirm-Prepared $PreparedWorkspace $ExpectedTargetThreadId
} else {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetThreadId)) { throw 'ExpectedTargetThreadId belongs to a previously prepared workspace.' }
    $parsed = [guid]::Empty
    if (-not [guid]::TryParseExact($TargetThreadId, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw 'A nonempty canonical authorized test-thread UUID is required.'
    }
    $null = Assert-ProbePath $artifactRoot $repoRoot
    $cli = Get-CodeCli
    $id = [guid]::NewGuid().ToString('N')
    $root = Assert-ProbePath (Join-Path $artifactRoot ('editor-probe-' + $id)) $repoRoot
    if ([IO.Directory]::Exists($root) -or [IO.File]::Exists($root)) { throw 'Unique preparation path already exists.' }
    $null = [IO.Directory]::CreateDirectory($root)
    $null = [IO.Directory]::CreateDirectory((Join-Path $root 'workspace'))
    $extension = Join-Path $root 'extension'
    $null = [IO.Directory]::CreateDirectory($extension)
    $hashes = [ordered]@{}
    foreach ($name in @('extension.js', 'package.json')) {
        $source = Assert-ProbePath (Join-Path $sourceRoot $name) $repoRoot
        $destination = Assert-ProbePath (Join-Path $extension $name) $root
        $bytes = [IO.File]::ReadAllBytes($source)
        $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $hashes[$name] = Get-ProbeHash $source $repoRoot
    }
    $document = [ordered]@{
        folders = @(@{ path = 'workspace' })
        settings = [ordered]@{ 'agentbrokerEditorProbe.targetThreadId' = $parsed.ToString('D'); 'agentbrokerEditorProbe.preparationId' = $id }
    }
    $manifest = [ordered]@{
        schema = 1; kind = 'agentbroker-vscode-editor-probe'; developmentOnly = $true; probeId = $id
        targetThreadId = $parsed.ToString('D'); workspace = 'probe.code-workspace'; result = 'result.json'; sourceHashes = $hashes
    }
    $workspace = Join-Path $root 'probe.code-workspace'
    Write-ProbeNew $workspace ($document | ConvertTo-Json -Depth 8) $root
    Write-ProbeNew (Join-Path $root 'prepared.json') ($manifest | ConvertTo-Json -Depth 8) $root
    $prepared = Confirm-Prepared $workspace
}
if ($null -eq $cli) { $cli = Get-CodeCli }
$arguments = @('--new-window', ('--extensionDevelopmentPath=' + $prepared.Extension), $prepared.Workspace)
$launched = $false
if ($Launch) {
    # Mirror the installed public code.cmd without batch parsing; only this child receives the two wrapper variables.
    $null = Confirm-Prepared $prepared.Workspace $ExpectedTargetThreadId
    $reservation = [ordered]@{ schema = 1; status = 'launch_reserved'; developmentOnly = $true }
    Write-ProbeNew (Join-Path $prepared.Root 'launch-reservation.json') ($reservation | ConvertTo-Json -Compress) $prepared.Root
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $cli.Exe
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $all = @($cli.Cli) + $arguments
    foreach ($argument in $all) {
        if ($argument.Contains('"') -or $argument -match '[\r\n]') { throw 'Unsafe CLI argument refused.' }
    }
    $info.Arguments = (($all | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $info.EnvironmentVariables['ELECTRON_RUN_AS_NODE'] = '1'
    $info.EnvironmentVariables.Remove('VSCODE_DEV')
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Code CLI launch was refused.' }
        $launched = $true
    } finally { $process.Dispose() }
}
[ordered]@{
    Status = $(if ($launched) { 'launch_requested' } else { 'prepared_only' })
    Launched = $launched; Workspace = $prepared.Workspace; Manifest = $prepared.Manifest
    ExtensionDevelopmentPath = $prepared.Extension; Result = (Join-Path $prepared.Root 'result.json')
    CodeCli = $cli.Cmd; Arguments = $arguments
} | ConvertTo-Json -Depth 5
