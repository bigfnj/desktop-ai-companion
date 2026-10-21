# The editor can retain the old synthetic template project after its scaffold is removed.
# Exempt that exact fixture only when nothing remains at its root except generated directories.
# Real module directories, files at the fixture root, extra directories and links stay strict.
function Test-LegacyTemplateBuildResidue {
    param([IO.DirectoryInfo]$Directory, [string]$RepoRoot)
    $fixture = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'modules\TemplateCheck'))
    if (-not $Directory.FullName.Equals($fixture, [StringComparison]::OrdinalIgnoreCase) -or
        ($Directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
    $entries = @(Get-ChildItem -LiteralPath $Directory.FullName -Force)
    if ($entries.Count -eq 0) { return $false }
    foreach ($entry in $entries) {
        if (-not $entry.PSIsContainer -or $entry.Name -notin @('obj', 'bin') -or
            ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
    }
    return $true
}
