<#
.SYNOPSIS
    The bytes raw.githubusercontent.com actually serves for a committed asset, and their SHA-256.

.DESCRIPTION
    Extracted from New-ContentCatalog.ps1 on 2026-09-17 so the GENERATOR and the VERIFIER cannot
    disagree about what "the catalog's hash" means. Two implementations of that is how a catalog and
    a checker end up both confident and both wrong -- and the trap here is specific and documented
    below: hashing the working-tree copy of a text asset gives a different answer from hashing the
    committed blob, because a checkout has CRLF and git stores LF.

    Deliberately 5.1-compatible, unlike its first caller: the verifier runs inside the gate, which is
    invoked under both powershell.exe and pwsh.
#>

# raw.githubusercontent.com serves the git blob verbatim, and these assets were
# committed with mixed line endings (some CRLF, some LF), so neither the
# working-tree copy nor a normalized copy is universally correct. Hash the actual
# committed blob. If the file is not yet committed (a brand-new pet/pack), fall
# back to the LF-normalized working-tree bytes, matching how git stores a new
# text asset on commit (.gitattributes: * text=auto eol=lf).
# $RelPath must use FORWARD slashes: it is handed to `git cat-file blob HEAD:<path>`, which does not
# accept backslashes and fails silently into the fallback below if given them.
function Get-CatalogAsset([string]$RepoRoot, [string]$RelPath, [string]$FullPath) {
    $bytes = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'git'
        $psi.Arguments = "-C `"$RepoRoot`" cat-file blob `"HEAD:$RelPath`""
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        $memory = New-Object System.IO.MemoryStream
        $process.StandardOutput.BaseStream.CopyTo($memory)
        [void]$process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -eq 0) { $bytes = $memory.ToArray() }
    }
    catch {
        $bytes = $null
    }

    # Which source answered, so a CALLER can refuse the fallback. The generator needs it -- a
    # brand-new pet or pack is not committed yet -- but a VERIFIER must not accept it: for a binary
    # asset the CR-stripping below produces a plausible, wrong hash, and a silent wrong answer is how
    # the first run of Test-ContentCatalogIntegrity.ps1 reported five of six module zips as
    # mismatched. The cause was a backslash in the path handed to `git cat-file`, which needs forward
    # slashes; cat-file failed, the fallback ran, and nothing said so. Added 2026-09-17.
    $source = 'blob'
    if ($null -eq $bytes) {
        $source = 'worktree'
        $raw = [IO.File]::ReadAllBytes($FullPath)
        $out = New-Object 'System.Collections.Generic.List[byte]' ($raw.Length)
        for ($i = 0; $i -lt $raw.Length; $i++) {
            if ($raw[$i] -eq 13 -and ($i + 1) -lt $raw.Length -and $raw[$i + 1] -eq 10) {
                continue   # drop CR in a CRLF pair; git stores LF for a new text file
            }
            $out.Add($raw[$i])
        }
        $bytes = $out.ToArray()
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    return [pscustomobject]@{ Sha256 = $hash; Bytes = $bytes.Length; Source = $source }
}
