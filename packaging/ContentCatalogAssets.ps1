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
# $TimeoutMs exists so the timeout path below is REACHABLE in a test. A 60-second default cannot be
# provoked in a gate, and an error path nobody has ever executed is a guess, not a safeguard.
#
# $StallChild is the other half of reaching it, and it exists because $TimeoutMs ALONE could not.
# The test drove the timeout by asking for 1ms on the theory that "git cat-file cannot finish that
# fast", and that theory is not what the code measures: Process.Start runs BEFORE
# $stdout.Wait($TimeoutMs), so a child that finishes inside that gap leaves the async copy already
# complete and Wait returns true however small the budget. On this box git lost that race and the
# check passed; in CI it won, once, and the gate went red on a docs-only commit. A timing assumption
# about somebody else's binary is not a test.
# So: when $StallChild is set the child is that command instead of git, the test points it at
# something that sleeps well past $TimeoutMs, and the Wait-timeout, the Kill and the refusal below
# all run for real, on real process machinery, with nothing racing.
function Get-CatalogAsset(
    [string]$RepoRoot,
    [string]$RelPath,
    [string]$FullPath,
    [int]$TimeoutMs = 60000,
    [string[]]$StallChild = $null) {
    $bytes = $null
    $timedOut = $false
    $process = $null
    $memory = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        if ($StallChild -and $StallChild.Count -gt 0) {
            $psi.FileName = $StallChild[0]
            if ($StallChild.Count -gt 1) {
                $psi.Arguments = ($StallChild[1..($StallChild.Count - 1)] -join ' ')
            }
        }
        else {
            $psi.FileName = 'git'
            $psi.Arguments = "-C `"$RepoRoot`" cat-file blob `"HEAD:$RelPath`""
        }
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($psi)

        # BOTH PIPES READ CONCURRENTLY, AND BOTH READS BOUNDED.
        #
        # This used to copy stdout to EOF, then read stderr, then WaitForExit with no timeout. Two
        # distinct hangs live in that order. A child that fills its stderr pipe (~4 KiB) blocks
        # writing, so it never closes stdout, so the copy never returns and the stderr read that
        # would have unblocked it is never reached -- the classic two-pipe deadlock. Separately, a
        # child that simply stalls writes nothing at all, so the COPY blocks first and the unbounded
        # WaitForExit was never even the statement that hung.
        #
        # Neither has fired here, and the reason is worth naming because it is not a property this
        # file controls: git cat-file's diagnostics are one short line, and a local object read does
        # not stall. A git that hangs on a lock, a stalled filesystem, or a scanner holding the
        # objects directory would all arrive as a gate that stops dead on one of ~219 assets with
        # nothing said about which. Async both, bound both, name the asset.
        $memory = New-Object System.IO.MemoryStream
        $stdout = $process.StandardOutput.BaseStream.CopyToAsync($memory)
        $stderr = $process.StandardError.ReadToEndAsync()

        if ($stdout.Wait($TimeoutMs) -and $process.WaitForExit($TimeoutMs)) {
            [void]$stderr.Wait(1000)
            if ($process.ExitCode -eq 0) { $bytes = $memory.ToArray() }
        }
        else {
            try { $process.Kill() } catch { }
            $timedOut = $true
        }
    }
    catch {
        $bytes = $null
    }
    finally {
        # Disposed on every path, including the throw below. This is correctness, NOT a leak fix, and
        # the distinction is measured rather than assumed: the audit that filed this claimed ~219
        # leaked handles per gate run, and that is wrong. Sampling DURING 400 real calls under 5.1,
        # before the change: peak growth +69, end of loop +28, settled -79 (below its own baseline),
        # never more than 5 git.exe objects live at once. Handle growth was already bounded, because
        # the finalizer comfortably keeps up. After: +30 / +7 / -21 / 4 -- better on every reading,
        # but these are single runs of a metric this repo has already documented as noisy, so read
        # them as "bounded either way", not as a 2x win. What deterministic disposal buys is not a
        # smaller number: it is that the release no longer
        # DEPENDS on a GC that a short-lived script is not obliged to run.
        if ($null -ne $memory) { $memory.Dispose() }
        if ($null -ne $process) { $process.Dispose() }
    }

    # Thrown OUTSIDE the catch deliberately. A timeout must NOT fall through to the worktree fallback
    # below: for a binary asset that path CR-strips its way to a plausible, wrong hash -- the exact
    # failure documented there -- and a catalog silently built from a wrong hash is worse than one
    # that refused to build. The fallback is for "not committed yet", never for "could not read".
    if ($timedOut) {
        throw ("git cat-file did not return within ${TimeoutMs}ms for '$RelPath'. " +
               'Refusing to fall back to the working-tree bytes, which would hash a different ' +
               'thing from what raw.githubusercontent.com serves.')
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
