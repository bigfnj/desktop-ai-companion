<#
.SYNOPSIS
    The bytes raw.githubusercontent.com actually serves for a committed asset, and their SHA-256.

.DESCRIPTION
    Extracted from New-ContentCatalog.ps1 on 2026-09-17 so the GENERATOR and the VERIFIER cannot
    disagree about what "the catalog's hash" means. Two implementations of that is how a catalog and
    a checker end up both confident and both wrong -- and the trap here is specific and documented
    below: hashing the working-tree copy of a text asset gives a different answer from hashing the
    committed blob, because a checkout has CRLF and git stores LF.

    Since 2026-09-30 the blobs come from ONE `git cat-file --batch` child per repository root, started
    on first use and closed by Stop-CatalogAssetBatch, which both callers run in a finally. Callers:
    New-ContentCatalog.ps1 (the generator) and Test-ContentCatalogIntegrity.ps1 (the verifier).

    Deliberately 5.1-compatible, unlike its first caller: the verifier runs inside the gate, which is
    invoked under both powershell.exe and pwsh.
#>

# raw.githubusercontent.com serves the git blob verbatim, and these assets were
# committed with mixed line endings (some CRLF, some LF), so neither the
# working-tree copy nor a normalized copy is universally correct. Hash the actual
# committed blob. If the file is not yet committed (a brand-new pet/pack), fall
# back to the LF-normalized working-tree bytes, matching how git stores a new
# text asset on commit (.gitattributes: * text=auto eol=lf).
# $RelPath must use FORWARD slashes: it is handed to git as `HEAD:<path>`, which does not accept
# backslashes; such a path comes back as `missing` and lands in the fallback below.
#
# ONE CHILD, MANY ASSETS. This used to start a fresh `git cat-file blob HEAD:<path>` process per asset,
# with its own ProcessStartInfo, two async pipe readers and a MemoryStream: 219 spawns at ~30 ms of
# process creation each came to 9-10 s per run, on every gate and every CI push, growing with every
# companion or pack added (F208). `git cat-file --batch` answers every request over one stdin/stdout
# pair; the same 219 paths take about a second. The child is cached per repository root and closed by
# Stop-CatalogAssetBatch; a child left behind would keep the pack files open for the rest of the
# PowerShell process, which is why both callers close it in a finally.
#
# The protocol, per request line `HEAD:<path>`:
#   <oid> blob <size>\n<size bytes>\n   the committed bytes
#   HEAD:<path> missing\n               the path is NOT in HEAD: the one case the worktree fallback exists
#                                       for. A text asset that is brand new has no blob yet, and git stores
#                                       it LF-normalized on commit, which the fallback emulates.
#   anything else, or EOF               git died, could not open the repository, or answered a shape this
#                                       code does not know. REFUSED, never guessed: the old code turned
#                                       every non-zero exit into the fallback, so an unreadable object or a
#                                       git that failed to start hashed CR-stripped worktree bytes -- for a
#                                       zip a plausible, wrong hash by construction (F209).
#
# $TimeoutMs bounds EVERY pipe read, so a git that hangs on a lock or a stalled filesystem still surfaces
# as a refusal naming the asset rather than a gate that stops dead on one of ~219 assets with nothing
# said about which. $StallChild keeps that path testable: when set, the child is that command instead of
# git, a one-off that is never cached, and the read timeout, the Kill and the refusal all run for real.
# Test-ContentCatalogIntegrity.ps1 records why a tiny budget against real git could not do this: a child
# that finishes inside the gap between Start and the first wait satisfies any budget, so the timeout
# was only ever exercised by SUBSTITUTING the child.
$script:CatalogAssetBatches = @{}

function Start-CatalogAssetBatch([string]$RepoRoot, [string[]]$StallChild, [int]$TimeoutMs = 60000) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $isStallChild = [bool]($StallChild -and $StallChild.Count -gt 0)
    if ($isStallChild) {
        $psi.FileName = $StallChild[0]
        if ($StallChild.Count -gt 1) {
            $psi.Arguments = ($StallChild[1..($StallChild.Count - 1)] -join ' ')
        }
    }
    else {
        $psi.FileName = 'git'
        $psi.Arguments = "-C `"$RepoRoot`" cat-file --batch"
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $batch = [pscustomobject]@{
        Process = $process
        Stdin   = $process.StandardInput.BaseStream
        Stdout  = $process.StandardOutput.BaseStream
        # Drained concurrently from the start, so a chatty child can never block on a full stderr pipe
        # while this side waits on stdout -- the two-pipe deadlock the per-asset version guarded against.
        Stderr  = $process.StandardError.ReadToEndAsync()
        Buffer  = New-Object byte[] 65536
        Start   = 0
        End     = 0
    }
    # UNDER WINDOWS POWERSHELL THE CHILD MAY ALREADY HAVE RECEIVED A UTF-8 BOM. .NET Framework's
    # Process.Start wraps the redirected stdin in a StreamWriter over Console.InputEncoding and sets
    # AutoFlush, whose setter flushes at once and writes the encoding's PREAMBLE: with the console code
    # page at 65001 that is EF BB BF on the pipe before this file writes a byte (pwsh's runtime strips
    # the preamble, and a legacy code page has none, which is why F208's own 5.1 runs never saw it).
    # git then read the first request as "<BOM>HEAD:<path>", answered "missing" for a committed file,
    # and F209 refused that echo as an unexpected reply, so the verifier and the generator failed on
    # their very first asset in every 5.1 session whose console is UTF-8 (measured 2026-09-30, reply
    # bytes EF BB BF 48 45 41 44 3A; N-scripts-pack-01). The bytes cannot be unsent, so they are
    # closed off as a request of their own and the reply git owes for exactly those bytes is consumed
    # and checked; anything else is refused, never skipped. Not for a substituted child, which is not
    # git and whose whole purpose is to stall or die.
    if (-not $isStallChild) {
        $preamble = $process.StandardInput.Encoding.GetPreamble()
        if ($preamble.Length -gt 0) {
            $batch.Stdin.Write([byte[]]@(10), 0, 1)
            $batch.Stdin.Flush()
            $echo = Read-CatalogBatchLine $batch $TimeoutMs
            $expectedEcho = [Text.Encoding]::UTF8.GetString($preamble) + ' missing'
            if ($echo.TimedOut -or $echo.Eof -or -not [string]::Equals($echo.Line, $expectedEcho, [StringComparison]::Ordinal)) {
                Close-CatalogAssetBatch $batch -Kill:$echo.TimedOut
                throw ("git cat-file --batch did not echo the stdin preamble Windows PowerShell wrote ahead of the " +
                       "first request (expected '<BOM> missing', got timed out: $($echo.TimedOut), eof: $($echo.Eof), " +
                       "line: '$($echo.Line)'), so the request stream is not in a known state. Refusing to read assets through it.")
            }
        }
    }
    return $batch
}

function Close-CatalogAssetBatch($Batch, [switch]$Kill) {
    if ($null -eq $Batch) { return }
    if ($Kill) { try { $Batch.Process.Kill() } catch { } }
    # EOF on stdin is how `--batch` is told to finish; a child that does not take the hint is killed.
    try { $Batch.Stdin.Dispose() } catch { }
    try { if (-not $Batch.Process.WaitForExit(2000)) { $Batch.Process.Kill() } } catch { }
    try { $Batch.Process.Dispose() } catch { }
}

# Close every cached child. Both callers run this in a finally; calling it with nothing open is a no-op.
function Stop-CatalogAssetBatch {
    foreach ($batch in @($script:CatalogAssetBatches.Values)) { Close-CatalogAssetBatch $batch }
    $script:CatalogAssetBatches.Clear()
}

# One bounded read from the child's stdout. Returns the byte count (0 = the child closed its stdout),
# or $null when the wait expired.
function Read-CatalogBatchChunk($Batch, [byte[]]$Destination, [int]$Offset, [int]$Count, [int]$TimeoutMs) {
    $task = $Batch.Stdout.ReadAsync($Destination, $Offset, $Count)
    if (-not $task.Wait($TimeoutMs)) { return $null }
    return $task.Result
}

# The next LF-terminated line out of the child, through the batch's own read-ahead buffer.
function Read-CatalogBatchLine($Batch, [int]$TimeoutMs) {
    while ($true) {
        $newline = [Array]::IndexOf($Batch.Buffer, [byte]10, $Batch.Start, $Batch.End - $Batch.Start)
        if ($newline -ge 0) {
            $line = [Text.Encoding]::UTF8.GetString($Batch.Buffer, $Batch.Start, $newline - $Batch.Start)
            $Batch.Start = $newline + 1
            return [pscustomobject]@{ Line = $line; TimedOut = $false; Eof = $false }
        }
        if ($Batch.Start -gt 0) {
            [Array]::Copy($Batch.Buffer, $Batch.Start, $Batch.Buffer, 0, $Batch.End - $Batch.Start)
            $Batch.End -= $Batch.Start
            $Batch.Start = 0
        }
        if ($Batch.End -ge $Batch.Buffer.Length) {
            return [pscustomobject]@{ Line = $null; TimedOut = $false; Eof = $true }
        }
        $got = Read-CatalogBatchChunk $Batch $Batch.Buffer $Batch.End ($Batch.Buffer.Length - $Batch.End) $TimeoutMs
        if ($null -eq $got) { return [pscustomobject]@{ Line = $null; TimedOut = $true; Eof = $false } }
        if ($got -eq 0) { return [pscustomobject]@{ Line = $null; TimedOut = $false; Eof = $true } }
        $Batch.End += $got
    }
}

# Exactly $Size bytes of blob body, then the LF that follows it. Whatever the read-ahead buffer already
# holds is taken first; the rest is read straight into the destination array. Returns the bytes, or a
# string naming why it could not.
function Read-CatalogBatchBody($Batch, [long]$Size, [int]$TimeoutMs) {
    if ($Size -gt [int]::MaxValue) { return "a $Size-byte blob is larger than this reader handles" }
    $body = New-Object byte[] ([int]$Size)
    $got = 0
    $buffered = [Math]::Min($Batch.End - $Batch.Start, [int]$Size)
    if ($buffered -gt 0) {
        [Array]::Copy($Batch.Buffer, $Batch.Start, $body, 0, $buffered)
        $Batch.Start += $buffered
        $got += $buffered
    }
    while ($got -lt $Size) {
        $read = Read-CatalogBatchChunk $Batch $body $got ([int]$Size - $got) $TimeoutMs
        if ($null -eq $read) { return 'timeout' }
        if ($read -eq 0) { return "the child closed its output $($Size - $got) byte(s) short of the blob" }
        $got += $read
    }
    # The trailing LF, which is part of the reply and would otherwise prefix the NEXT header.
    $terminator = Read-CatalogBatchLine $Batch $TimeoutMs
    if ($terminator.TimedOut) { return 'timeout' }
    if ($terminator.Eof) { return 'the child closed its output before the blob terminator' }
    if ($terminator.Line -ne '') { return "unexpected bytes after the blob: '$($terminator.Line)'" }
    return ,$body
}

function Get-CatalogAsset(
    [string]$RepoRoot,
    [string]$RelPath,
    [string]$FullPath,
    [int]$TimeoutMs = 60000,
    [string[]]$StallChild = $null) {
    $bytes = $null
    $missing = $false
    $failure = $null
    $oneOff = [bool]($StallChild -and $StallChild.Count -gt 0)
    $key = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').ToLowerInvariant()
    $batch = $null
    try {
        if ($oneOff) {
            $batch = Start-CatalogAssetBatch $RepoRoot $StallChild $TimeoutMs
        }
        else {
            if (-not $script:CatalogAssetBatches.ContainsKey($key)) {
                $script:CatalogAssetBatches[$key] = Start-CatalogAssetBatch $RepoRoot $null $TimeoutMs
            }
            $batch = $script:CatalogAssetBatches[$key]
        }

        $request = [Text.Encoding]::UTF8.GetBytes("HEAD:$RelPath`n")
        $batch.Stdin.Write($request, 0, $request.Length)
        $batch.Stdin.Flush()

        $header = Read-CatalogBatchLine $batch $TimeoutMs
        if ($header.TimedOut) { $failure = 'timeout' }
        elseif ($header.Eof) { $failure = 'the child exited or closed its output before answering' }
        # ORDINAL, not -eq: PowerShell's -eq folds case, and the echo is the only reply that reaches the
        # worktree fallback, so it is matched byte for byte; a BOM-prefixed or re-cased echo is an
        # 'unexpected reply' and refused with the rest (RA-194).
        elseif ([string]::Equals($header.Line, "HEAD:$RelPath missing", [StringComparison]::Ordinal)) { $missing = $true }
        elseif ($header.Line -match '^[0-9a-f]{40,64} blob (\d+)$') {
            $body = Read-CatalogBatchBody $batch ([long]$Matches[1]) $TimeoutMs
            if ($body -is [string]) { $failure = $body } else { $bytes = $body }
        }
        else { $failure = "unexpected reply '$($header.Line)'" }
    }
    catch {
        $failure = "could not talk to the child: $($_.Exception.Message)"
    }
    finally {
        # A one-off is never reused, and a child that failed is not trusted with the next request
        # either: it is closed (killed on a timeout, where it is still blocking) and the next call
        # starts a fresh one. Disposal on every path, so nothing depends on a GC a short-lived script
        # is not obliged to run.
        if ($null -ne $batch -and ($oneOff -or $null -ne $failure)) {
            Close-CatalogAssetBatch $batch -Kill:($failure -eq 'timeout')
            if (-not $oneOff) { $script:CatalogAssetBatches.Remove($key) }
        }
    }

    # Thrown OUTSIDE the catch deliberately, and for EVERY failure that is not `missing`. A timeout, a
    # dead child or an unreadable object must NOT fall through to the worktree fallback below: for a
    # binary asset that path CR-strips its way to a plausible, wrong hash -- the exact failure documented
    # there -- and a catalog silently built from a wrong hash is worse than one that refused to build.
    # The fallback is for "not committed yet", never for "could not read".
    if ($failure -eq 'timeout') {
        throw ("git cat-file did not return within ${TimeoutMs}ms for '$RelPath'. " +
               'Refusing to fall back to the working-tree bytes, which would hash a different ' +
               'thing from what raw.githubusercontent.com serves.')
    }
    if ($null -ne $failure) {
        $stderrText = ''
        try { if ($null -ne $batch -and $batch.Stderr.Wait(1000)) { $stderrText = ([string]$batch.Stderr.Result).Trim() } } catch { }
        throw ("git cat-file --batch could not answer for '$RelPath': $failure" +
               $(if ($stderrText) { " (git said: $stderrText)" } else { '' }) +
               '. Refusing to fall back to the working-tree bytes, which would hash a different ' +
               'thing from what raw.githubusercontent.com serves.')
    }

    # Which source answered, so a CALLER can refuse the fallback. The generator needs it -- a
    # brand-new pet or pack is not committed yet -- but a VERIFIER must not accept it: for a binary
    # asset the CR-stripping below produces a plausible, wrong hash, and a silent wrong answer is how
    # the first run of Test-ContentCatalogIntegrity.ps1 reported five of six module zips as
    # mismatched. The cause was a backslash in the path handed to `git cat-file`, which needs forward
    # slashes; cat-file failed, the fallback ran, and nothing said so. Added 2026-09-17. Since
    # 2026-09-30 the generator refuses it for the module zips too (New-ContentCatalog.ps1).
    $source = 'blob'
    if ($missing) {
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
