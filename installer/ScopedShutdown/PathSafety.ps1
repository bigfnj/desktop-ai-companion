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
    $drive = [IO.Path]::GetPathRoot($full)
    $current = $drive
    foreach ($part in $full.Substring($drive.Length).Split('\')) {
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
