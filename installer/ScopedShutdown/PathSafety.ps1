#requires -Version 5
# Local native-build guard. Do not change the historical packaging helper's semantics.
function Assert-ScopedShutdownPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$TrustedRoot)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($TrustedRoot).TrimEnd('\')
    foreach ($raw in ($full -split '[\\/]')) {
        $part = $raw.Split(':')[0].TrimEnd(' ', '.')
        if ($part -match '(?i)(\.env$|^\.env\.|\.npmrc$|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.aws$|^\.ssh$|^\.kube$|^\.config$|^\.mozilla$|^\.git$|^credentials?$|^User Data$|^Firefox$|^Keychains$)') {
            throw 'native_output_protected_scope_refused'
        }
    }
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $full.IndexOf(':', 2) -ge 0 -or [IO.Path]::GetPathRoot($full) -notmatch '^[A-Za-z]:\\$') {
        throw 'native_output_scope_refused'
    }
    # The reparse walk judges only the components BELOW the trusted root (N-audit-1106-261). It used to start at the
    # drive root, so the checkout root and every folder above it were judged too, and a checkout reached through a
    # junction (or one that IS a junction, as a runner's work folder can be) was refused as its own root. That is the
    # case F218 settled for packaging\StagingPathSafety.ps1 on 2026-09-30: the root is the caller's boundary, so what it
    # is made of is the caller's business, while a junction below it is still an escape and still refused. The path is
    # already known to be strictly below the root (checked above), so there is always at least one component to judge.
    $current = $root
    foreach ($part in $full.Substring($root.Length + 1).Split('\')) {
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch {
            $cause = $_.Exception
            while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
            if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { break }
            throw 'native_output_metadata_unavailable'
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'native_output_reparse_refused' }
    }
    return $full
}
