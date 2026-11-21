# Test-owned directories only. This helper never follows links or uses recursive deletion.
function Assert-OwnedTestPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        try {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'OwnedTestTree.ps1: owned_test_reparse'
            }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        if ($cursor -eq [IO.Path]::GetPathRoot($cursor)) { break }
    }
    return $full
}

function Remove-OwnedTestTree([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $boundary = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [IO.Path]::GetDirectoryName($full).Equals($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OwnedTestTree.ps1: owned_test_parent_mismatch'
    }
    [void](Assert-OwnedTestPath $full)
    if (-not [IO.Directory]::Exists($full)) {
        if ([IO.File]::Exists($full)) { throw 'OwnedTestTree.ps1: owned_test_root_replaced' }
        return
    }
    $directories = New-Object 'Collections.Generic.List[string]'
    $files = New-Object 'Collections.Generic.List[string]'
    $directories.Add($full)
    # Complete preflight before the first delete, including unexpected files left by a failed check.
    for ($index = 0; $index -lt $directories.Count; $index++) {
        [void](Assert-OwnedTestPath $directories[$index])
        foreach ($entry in [IO.Directory]::GetFileSystemEntries($directories[$index])) {
            [void](Assert-OwnedTestPath $entry)
            if (([IO.File]::GetAttributes($entry) -band [IO.FileAttributes]::Directory) -ne 0) { $directories.Add($entry) }
            else { $files.Add($entry) }
        }
    }
    foreach ($file in $files) {
        [void](Assert-OwnedTestPath $file)
        Remove-Item -LiteralPath $file -Force -ErrorAction Stop
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) {
        [void](Assert-OwnedTestPath $directories[$index])
        [IO.Directory]::Delete($directories[$index], $false)
    }
    if (Test-Path -LiteralPath $full) { throw 'OwnedTestTree.ps1: owned_test_cleanup_incomplete' }
}

function Complete-OwnedTestTree([string]$Path, [string]$Parent, [bool]$HadFailure = $false) {
    try { Remove-OwnedTestTree $Path $Parent }
    catch {
        if (-not $HadFailure) { throw }
        Write-Warning ('Owned fixture retained after cleanup refusal; original failure preserved: ' + $Path)
    }
}
