#requires -Version 7
<#
.SYNOPSIS
    Check indexed text for workstation-specific identifiers before a public commit or after a push.
.DESCRIPTION
    Refuses protected indexed paths before reading any indexed contents. Git grep also receives
    explicit case-insensitive exclusions. Only offending file paths and counts are printed.
    The employer and business-folder identifiers are derived from OneDriveCommercial at runtime.
.EXAMPLE
    pwsh -NoProfile -File tests/Test-PublicCommitPrivacy.ps1
.EXAMPLE
    pwsh -NoProfile -File tests/Test-PublicCommitPrivacy.ps1 -SelfTest
#>
[CmdletBinding()]
param([string]$EmployerName = '', [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))

function Get-ProtectedPattern([string]$Path) {
    $segments = $Path.Replace('\', '/').Split('/')
    $previous = ''
    foreach ($raw in $segments) {
        # Account for ordinary Win32 trailing-name normalization and alternate streams too.
        $segment = $raw.Split(':')[0].TrimEnd(' ', '.')
        if ($segment.EndsWith('.env', [StringComparison]::OrdinalIgnoreCase)) { return 'W01' }
        if ($segment.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase)) { return 'W02' }
        if ($segment.Equals('.npmrc', [StringComparison]::OrdinalIgnoreCase)) { return 'W03' }
        if ($previous.Equals('.git', [StringComparison]::OrdinalIgnoreCase) -and
            $segment.Equals('credentials', [StringComparison]::OrdinalIgnoreCase)) { return 'W04' }
        foreach ($entry in @(@('.pem', 'W05'), @('.key', 'W06'), @('.p12', 'W07'),
                              @('.pfx', 'W08'), @('.keystore', 'W09'))) {
            if ($segment.EndsWith($entry[0], [StringComparison]::OrdinalIgnoreCase)) { return $entry[1] }
        }
        if ($segment.StartsWith('privatekey', [StringComparison]::OrdinalIgnoreCase)) { return 'W10' }
        $previous = $segment
    }
    return $null
}

if ($SelfTest) {
    $count = 0
    foreach ($case in @(
        @('nested/config.env', 'W01'), @('nested/.ENV.local', 'W02'), @('.npmrc', 'W03'),
        @('nested/.GIT/credentials', 'W04'), @('nested/CERT.PEM', 'W05'), @('nested/test.KEY', 'W06'),
        @('nested/test.p12', 'W07'), @('nested/test.pfx', 'W08'), @('nested/test.keystore', 'W09'),
        @('nested/PrivateKeyFixture/source.cs', 'W10'), @('nested/test.key/source.cs', 'W06'),
        @('nested\.env.settings', 'W02'), @('nested/test.key. ', 'W06'), @('nested/test.key:stream', 'W06'),
        @('nested/.npmrc/child.txt', 'W03'), @('nested/.git/credentials/child.txt', 'W04'),
        @('src/Worker.cs', ''), @('docs/credential-policy.md', ''), @('src/KeyPolicy.cs', '')
    )) {
        if ([string](Get-ProtectedPattern $case[0]) -cne $case[1]) { throw 'Public privacy path-policy self-test failed.' }
        $count++
    }
    Write-Host "Public privacy path-policy checks passed: $count cases, no files created or opened."
    exit 0
}

function Invoke-IndexedGit([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($argument in @('-c', 'core.fsmonitor=false', '-C', $repoRoot) + $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'The indexed privacy check exceeded its 60-second bound.'
        }
        [void]$stderr.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

# Listing names from the index does not open their contents. Refuse first, even when an excluded
# protected path was already tracked before this change, rather than silently declaring it safe.
$listed = Invoke-IndexedGit @('ls-files', '--cached', '-z')
if ($listed.ExitCode -ne 0) { throw 'Could not enumerate indexed paths. No indexed contents were scanned.' }
$protected = @()
foreach ($path in $listed.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
    $pattern = Get-ProtectedPattern $path
    if ($pattern) { $protected += [pscustomobject]@{ Path = $path; Pattern = $pattern } }
}
if ($protected.Count -gt 0) {
    Write-Host "Public privacy check blocked: $($protected.Count) protected indexed path(s). No indexed contents were scanned."
    foreach ($entry in $protected) { Write-Host ($entry.Pattern + ' ' + ($entry.Path | ConvertTo-Json -Compress)) }
    exit 1
}

$commercial = [Environment]::GetEnvironmentVariable('OneDriveCommercial')
$username = [Environment]::GetEnvironmentVariable('USERNAME')
if ([string]::IsNullOrWhiteSpace($commercial) -or [string]::IsNullOrWhiteSpace($username)) {
    throw 'The user name and business OneDrive location must be available for all three privacy checks.'
}
$businessFolder = [IO.Path]::GetFileName([IO.Path]::GetFullPath($commercial).TrimEnd('\', '/'))
if ([string]::IsNullOrWhiteSpace($EmployerName)) {
    if ($businessFolder -notmatch '^OneDrive\s*-\s*(.+)$') { throw 'Provide -EmployerName because the business OneDrive folder does not identify it.' }
    $EmployerName = $Matches[1]
}
if ($EmployerName.Length -lt 2 -or $username.Length -lt 2 -or $businessFolder.Length -lt 2) {
    throw 'Privacy identifiers must each contain at least two characters.'
}
$arguments = @('grep', '--cached', '--no-textconv', '-n', '-i', '-I', '-l', '-F', '-z',
               '-e', $EmployerName, '-e', $username, '-e', $businessFolder, '--', '.')
foreach ($glob in @('*.env', '.env.*', '.npmrc', '*.pem', '*.key', '*.p12', '*.pfx',
                     '*.keystore', 'privatekey*', '.git/credentials')) {
    $arguments += ":(top,glob,icase,exclude)**/$glob"
    $arguments += ":(top,glob,icase,exclude)**/$glob/**"
}
$result = Invoke-IndexedGit $arguments
if ($result.ExitCode -gt 1) { throw 'Indexed git grep failed. No matching contents were printed.' }
$offenders = @($result.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique)
if ($result.ExitCode -eq 0) {
    Write-Host "Public privacy check failed: $($offenders.Count) indexed text file(s) contain a workstation identifier."
    foreach ($path in $offenders) { Write-Host ($path | ConvertTo-Json -Compress) }
    exit 1
}
Write-Host 'Public privacy check passed: 3 runtime-derived identifiers checked across indexed text files.'
