#requires -Version 5
<#
    Zips one module's build output into packaging/modules-dist/<id>.zip -- the exact shape
    ModulesPaneControl's install flow extracts directly into modules/<id>/ (files at the
    zip's own root, no nested folder). Excludes .pdb/.lib (debug symbols / link-time-only
    libraries, neither needed at runtime, matching the base's own lean 16-file manifest
    convention). Deterministic in the ways that matter for review (sorted entry order,
    fixed 1980-01-01 timestamps, zeroed external attributes) without the full portable-ZIP
    ceremony in New-DeterministicPortableZip.ps1 -- that script's marker-file/content-directory
    machinery is specific to the portable install artifact, not a plugin module payload.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModuleId,
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [string[]]$ExcludeExtensions = @('.pdb', '.lib')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
    throw "Module source directory not found: $SourceDirectory"
}
# Resolve relative paths against the CALLER'S location, not [IO.Path]::GetFullPath's notion of the
# current directory: .NET reads the process working directory, which PowerShell's Set-Location does
# NOT update, so a relative -DestinationPath silently wrote the zip outside the repo. An already-rooted
# path is passed through untouched -- joining it onto the location yields "D:\repo\D:\..." and throws
# a format error that reads like a bad argument rather than a bug here.
function Resolve-AgainstCaller([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $path))
}
$sourceFull = (Resolve-AgainstCaller $SourceDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$destinationFull = Resolve-AgainstCaller $DestinationPath
$destinationParent = Split-Path -Parent $destinationFull
if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
}

$files = @(
    Get-ChildItem -LiteralPath $sourceFull -File -Recurse |
        Where-Object { $ExcludeExtensions -notcontains $_.Extension } |
        Sort-Object FullName
)
if ($files.Count -eq 0) {
    throw "Module '$ModuleId' contributes no files to package (after exclusions)."
}

$temporaryPath = $destinationFull + '.tmp'
if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
$normalizedTimestamp = New-Object DateTimeOffset 1980, 1, 1, 0, 0, 0, ([TimeSpan]::Zero)

$output = New-Object IO.FileStream(
    $temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $archive = New-Object IO.Compression.ZipArchive(
        $output, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($sourceFull.Length).TrimStart(
                [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $entryName = ($relative -replace '\\', '/')
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $normalizedTimestamp
            $entry.ExternalAttributes = 0
            $entryStream = $entry.Open()
            try {
                $sourceStream = [IO.File]::OpenRead($file.FullName)
                try { $sourceStream.CopyTo($entryStream) }
                finally { $sourceStream.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    $output.Flush($true)
}
finally { $output.Dispose() }

Move-Item -LiteralPath $temporaryPath -Destination $destinationFull -Force

$hash = (Get-FileHash -LiteralPath $destinationFull -Algorithm SHA256).Hash.ToLowerInvariant()
$bytes = (Get-Item -LiteralPath $destinationFull).Length
Write-Host ("Module zip created: {0} ({1} entries, {2} bytes, sha256={3})" -f `
    $destinationFull, $files.Count, $bytes, $hash) -ForegroundColor Green
Write-Output ([PSCustomObject]@{ Id = $ModuleId; Path = $destinationFull; Bytes = $bytes; Sha256 = $hash })