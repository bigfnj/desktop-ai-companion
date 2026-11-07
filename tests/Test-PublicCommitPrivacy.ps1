#requires -Version 7
<#
.SYNOPSIS
    Check indexed text, or a commit range, for workstation-specific identifiers before a public push.
.DESCRIPTION
    Refuses protected paths before reading any contents. Git grep, which chooses the indexed
    text files to read, also receives explicit case-insensitive exclusions. Both modes compare
    with one matcher, case-insensitively in every script. Only offending file paths, commits
    and counts are printed, never the matched text.

    Identifiers come from the run, never from this file: -Identifier and -EmployerName, plus
    the business OneDrive folder and the employer it names when OneDriveCommercial is set.
    OneDrive is optional once an identifier is given. The user name is matched only as a
    profile path (\Users\<name>, /Users/<name> and the doubled-backslash form of the first),
    because a common profile name such as a dictionary word matches everywhere as a bare word.
    The name must end there: at a separator, a quote, a space, the end of the line or any
    other character that cannot continue a name (a letter, mark or number of any script, or
    _ . $ - can), so the profile root itself is found, and a longer name that merely starts
    with this one is not.

    Index mode reads one Git blob at a time, with a 256 MiB per-file limit. A larger file refuses the
    check explicitly. The total size of the selected batch is not held in one array or string.

    -Range scans the commits of a revision range instead of the index: every added line of
    every commit (so a value added and later removed is still found), each commit message,
    and the author and committer lines. A merge is diffed against its first parent, so a line
    written while resolving a conflict, and a path only the merge adds, are read too. A rename
    is read as a deletion and an addition, so its old path is checked against the protected
    shapes before any contents are read.
.EXAMPLE
    pwsh -NoProfile -File tests/Test-PublicCommitPrivacy.ps1
.EXAMPLE
    pwsh -NoProfile -File tests/Test-PublicCommitPrivacy.ps1 -Identifier '<employer>','<work-domain>'
.EXAMPLE
    pwsh -NoProfile -File tests/Test-PublicCommitPrivacy.ps1 -Identifier '<employer>' -Range origin/master..HEAD
.EXAMPLE
    pwsh -NoProfile -File tests/Test-PublicCommitPrivacy.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string]$EmployerName = '',
    [string[]]$Identifier = @(),
    [string]$Range = '',
    [switch]$SelfTest
)
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

function Get-PrivacyIdentifiers([string[]]$Explicit, [string]$Employer, [string]$Commercial) {
    # A business OneDrive adds its folder name and the employer it names; it is optional
    # once the run names an identifier, so a workstation without one can still be checked.
    $identifiers = New-Object 'Collections.Generic.List[string]'
    foreach ($value in @($Explicit) + @($Employer)) {
        if (-not [string]::IsNullOrWhiteSpace($value)) { $identifiers.Add($value.Trim()) }
    }
    if (-not [string]::IsNullOrWhiteSpace($Commercial)) {
        $businessFolder = [IO.Path]::GetFileName([IO.Path]::GetFullPath($Commercial).TrimEnd('\', '/'))
        $identifiers.Add($businessFolder)
        if ([string]::IsNullOrWhiteSpace($Employer)) {
            if ($businessFolder -match '^OneDrive\s*-\s*(.+)$') { $identifiers.Add($Matches[1].Trim()) }
            elseif ($identifiers.Count -eq 1) { throw 'Provide -EmployerName or -Identifier because the business OneDrive folder does not identify the employer.' }
        }
    } elseif ($identifiers.Count -eq 0) {
        throw 'No business OneDrive location is set here: pass the identifiers to refuse with -Identifier (or -EmployerName).'
    }
    return ,$identifiers.ToArray()
}

function ConvertTo-PrivacyNfc([string]$Value) {
    try { return $Value.Normalize([Text.NormalizationForm]::FormC) }
    catch [ArgumentException] { throw 'The privacy check cannot normalize malformed Unicode text.' }
}

function Get-PrivacyNeedles([string[]]$Identifiers, [string]$UserName) {
    # Identifiers are fixed strings; the user name only ever as a profile path. Test-PrivacyText alone compares them,
    # case-insensitively, in BOTH modes (see Get-IndexedPrivacyOffenders).
    # Fixed strings cannot say where a name ENDS, and a trailing separator on each needle missed the profile root
    # itself, at the end of a line or before a quote (Codex review 2026-11-02), while dropping the separator would
    # match a longer name too. So a .NET pattern locates a separator, Users, one or more separators (the
    # doubled-backslash form too), and the name; the matcher checks the next scalar. It must not go on with a letter, mark or
    # number of any script, a connector such as _, or . $ - (Codex recheck 2026-11-02: an ASCII-only class let an
    # accented, CJK or combining-mark continuation end the name, so a different, longer name matched). A single
    # trailing dot is allowed before it, as Win32 drops one and a sentence ends with one, but a dot that continues a
    # name does not end it. The prefix is required, so the bare word never matches.
    $fixed = New-Object 'Collections.Generic.List[string]'
    foreach ($value in $Identifiers) {
        $normalized = ConvertTo-PrivacyNfc $value
        if (-not $fixed.Contains($normalized)) { $fixed.Add($normalized) }
    }
    $profileRoot = ''
    if (-not [string]::IsNullOrEmpty($UserName)) {
        $profileRoot = '[\\/]Users[\\/]+' + [regex]::Escape((ConvertTo-PrivacyNfc $UserName))
    }
    return [pscustomobject]@{ Fixed = $fixed.ToArray(); Profile = $profileRoot }
}

function Test-PrivacyText([string]$Text, [psobject]$Needles) {
    $Text = ConvertTo-PrivacyNfc $Text
    foreach ($needle in $Needles.Fixed) {
        if ($Text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    if ($Needles.Profile) {
        foreach ($candidate in [regex]::Matches($Text, $Needles.Profile,
                [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')) {
            $next = $candidate.Index + $candidate.Length
            if ($next -lt $Text.Length -and $Text[$next] -eq '.') { $next++ }
            if ($next -eq $Text.Length) { return $true }
            # CharUnicodeInfo reads the scalar at this UTF-16 index, including a surrogate pair. A regex
            # category class sees only its high surrogate and would mistake a supplementary letter for a boundary.
            $category = [Globalization.CharUnicodeInfo]::GetUnicodeCategory($Text, $next)
            $continues = $Text[$next] -in @('.', '$', '-') -or $category -in @(
                'UppercaseLetter', 'LowercaseLetter', 'TitlecaseLetter', 'ModifierLetter', 'OtherLetter',
                'NonSpacingMark', 'SpacingCombiningMark', 'EnclosingMark', 'DecimalDigitNumber',
                'LetterNumber', 'OtherNumber', 'ConnectorPunctuation')
            if (-not $continues) { return $true }
        }
    }
    return $false
}

function Invoke-IndexedGit([string[]]$Arguments, [string]$Repository = $repoRoot, [switch]$Lenient) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Patches can carry text in any encoding; names and grep output must be exact UTF-8.
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, (-not $Lenient.IsPresent))
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $false)
    foreach ($argument in @('-c', 'core.fsmonitor=false', '-c', 'core.quotePath=false', '-C', $Repository) + $Arguments) {
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
            throw 'The privacy check exceeded its 60-second git bound.'
        }
        [void]$stderr.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Get-RangePrivacyFindings([string]$Range, [psobject]$Needles, [string]$Repository = $repoRoot) {
    if ($Range -notmatch '^[A-Za-z0-9_][A-Za-z0-9_./~^@{}-]*(\.\.\.?[A-Za-z0-9_][A-Za-z0-9_./~^@{}-]*)?$') {
        throw 'The range must be a plain revision range such as origin/master..HEAD.'
    }
    # Names first: a protected path anywhere in the range refuses before any content is read.
    # Both log calls name a merge format (N-audit-20261029-14): with none, git log prints NO names and
    # NO diff for a merge commit, so a line written only while resolving a conflict, or a path a merge
    # alone adds, was never read. --diff-merges=first-parent diffs a merge against the branch it was
    # made on, which keeps the plain "diff --git" / "+++ b/" shape the parser below reads; -m would too
    # but repeats the merge once per parent, and --cc prints a combined format the parser cannot read.
    # The price is a superset, never a gap: a merge repeats the lines its side branch brought in, so
    # a value on that branch is reported at its own commit AND at the merge.
    # Both also pass --no-renames (Codex review 2026-11-02). With rename detection, the git default, --name-only
    # prints only a rename's NEW path, so a protected file renamed to an ordinary name passed this check and the
    # patch read below printed its old path and context; finding the rename also compares contents. Without it a
    # rename is a deletion plus an addition in both passes: both paths are named here, and the patch reads every
    # line of the new path as added (again a superset: the index mode reads that whole file too).
    $names = Invoke-IndexedGit @('log', '--no-color', '--no-renames', '--diff-merges=first-parent', '--format=', '--name-only', '-z', '--end-of-options', $Range, '--') $Repository
    if ($names.ExitCode -ne 0) { throw 'Could not list the paths the range changes. No commit contents were scanned.' }
    # -CaseSensitive (N-audit-infra-01): -Unique alone compares ignoring case, so two paths that differ only in case,
    # two files to git and to any case-sensitive checkout, were listed and counted as one.
    $protected = @($names.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim("`n") } |
        Where-Object { $_.Length -gt 0 -and (Get-ProtectedPattern $_) } | Sort-Object -Unique -CaseSensitive |
        ForEach-Object { [pscustomobject]@{ Path = $_; Pattern = (Get-ProtectedPattern $_) } })
    $findings = New-Object 'Collections.Generic.List[object]'
    if ($protected.Count -gt 0) { return [pscustomobject]@{ Protected = $protected; Findings = @(); Commits = 0 } }
    $log = Invoke-IndexedGit @('log', '--no-color', '--no-renames', '--no-ext-diff', '--no-textconv', '-p', '--diff-merges=first-parent',
        '--format=%x00%x01%H%n%an <%ae>%n%cn <%ce>%n%B%x00%x02', '--end-of-options', $Range, '--') $Repository -Lenient
    if ($log.ExitCode -ne 0) { throw 'Could not read the range. No findings were printed.' }
    $commits = 0
    # The format brackets each header with NUL+SOH and NUL+STX, which no commit text contains.
    $headerStart = [string][char]0 + [char]1
    $headerEnd = [string][char]0 + [char]2
    foreach ($chunk in $log.Output.Split($headerStart, [StringSplitOptions]::RemoveEmptyEntries)) {
        $split = $chunk.IndexOf($headerEnd, [StringComparison]::Ordinal)
        if ($split -lt 0) { continue }
        $commits++
        $header = $chunk.Substring(0, $split).Split("`n")
        $commit = $header[0].Trim().Substring(0, 12)
        if (Test-PrivacyText $header[1] $Needles) { $findings.Add([pscustomobject]@{ Commit = $commit; Kind = 'author'; Path = ''; Count = 1 }) }
        if (Test-PrivacyText $header[2] $Needles) { $findings.Add([pscustomobject]@{ Commit = $commit; Kind = 'committer'; Path = ''; Count = 1 }) }
        if ($header.Count -gt 3 -and (Test-PrivacyText (($header[3..($header.Count - 1)]) -join "`n") $Needles)) {
            $findings.Add([pscustomobject]@{ Commit = $commit; Kind = 'message'; Path = ''; Count = 1 })
        }
        # Added lines only, inside hunks, so a "+++ b/path" header is never read as content. Ordinal keys: [ordered]@{}
        # ignores case, so two added paths differing only in case were reported as one path counted twice (found by the
        # both-modes self-test case below, as N-audit-infra-01 found for protected paths).
        $path = ''; $inHunk = $false; $counts = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        foreach ($line in $chunk.Substring($split + 2).Split("`n")) {
            if ($line.StartsWith('diff --git ')) { $inHunk = $false; $path = ''; continue }
            if (-not $inHunk) {
                if ($line.StartsWith('+++ b/')) { $path = $line.Substring(6).TrimEnd("`r") }
                elseif ($line.StartsWith('@@')) { $inHunk = $true }
                continue
            }
            if ($line.StartsWith('@@')) { continue }
            if ($line.StartsWith('+') -and (Test-PrivacyText $line.Substring(1) $Needles)) {
                if (-not $counts.Contains($path)) { $counts[$path] = 0 }
                $counts[$path]++
            }
        }
        foreach ($entry in $counts.GetEnumerator()) {
            $findings.Add([pscustomobject]@{ Commit = $commit; Kind = 'added-line'; Path = $entry.Key; Count = $entry.Value })
        }
    }
    return [pscustomobject]@{ Protected = @(); Findings = $findings.ToArray(); Commits = $commits }
}

# The default mode reads the index. Listing names does not open their contents, so protected paths are
# refused first, even when one was already tracked before this change, rather than silently declared safe.
function Get-IndexedProtectedPaths([string]$Repository = $repoRoot) {
    $listed = Invoke-IndexedGit @('ls-files', '--cached', '-z') $Repository
    if ($listed.ExitCode -ne 0) { throw 'Could not enumerate indexed paths. No indexed contents were scanned.' }
    $protected = @()
    foreach ($path in $listed.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        $pattern = Get-ProtectedPattern $path
        if ($pattern) { $protected += [pscustomobject]@{ Path = $path; Pattern = $pattern } }
    }
    return ,$protected
}

# The default mode's matcher is Test-PrivacyText, the one the range mode applies to every added line, so the two
# modes cannot disagree about case or about where a name ends (Codex recheck 2026-11-02: git grep -i folds only ASCII
# here, so an upper-case accented profile name passed the index mode that the range mode refused, and its extended
# regex has no Unicode classes). git chooses WHICH files: every indexed text file (-I; the empty pattern matches any
# line), names only, NUL-separated, with every protected shape excluded again as a second line behind the refusal
# above. One git cat-file then reads exactly those blobs, and only the offending paths leave this function.
function Get-IndexedPrivacyOffenders([psobject]$Needles, [string]$Repository = $repoRoot) {
    $arguments = @('grep', '--cached', '--no-textconv', '-I', '-l', '-z', '-e', '', '--', '.')
    foreach ($glob in @('*.env', '.env.*', '.npmrc', '*.pem', '*.key', '*.p12', '*.pfx',
                         '*.keystore', 'privatekey*', '.git/credentials')) {
        $arguments += ":(top,glob,icase,exclude)**/$glob"
        $arguments += ":(top,glob,icase,exclude)**/$glob/**"
    }
    $listed = Invoke-IndexedGit $arguments $Repository
    if ($listed.ExitCode -gt 1) { throw 'Indexed git grep failed. No matching contents were printed.' }
    $paths = @($listed.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    $offenders = New-Object 'Collections.Generic.List[string]'
    if ($paths.Count -gt 0) {
        $staged = Invoke-IndexedGit @('ls-files', '--cached', '-s', '-z') $Repository
        if ($staged.ExitCode -ne 0) { throw 'Could not enumerate indexed paths. No indexed contents were scanned.' }
        # Ordinal keys (N-audit-infra-01): two indexed paths differing only in case are two files. An unmerged path has
        # one entry per stage, and every one is read.
        $blobsByPath = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
        foreach ($record in $staged.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
            $tab = $record.IndexOf("`t")
            $path = $record.Substring($tab + 1)
            if (-not $blobsByPath.ContainsKey($path)) { $blobsByPath[$path] = [Collections.Generic.List[string]]::new() }
            $blobsByPath[$path].Add($record.Substring(0, $tab).Split(' ')[1])
        }
        $blobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($path in $paths) {
            if (-not $blobsByPath.ContainsKey($path)) { throw 'An indexed text file had no index entry. No matching contents were printed.' }
            foreach ($blob in $blobsByPath[$path]) { [void]$blobs.Add($blob) }
        }
        $matched = Get-MatchingIndexedBlobs ([string[]]@($blobs)) $Needles $Repository
        foreach ($path in $paths) {
            foreach ($blob in $blobsByPath[$path]) { if ($matched.Contains($blob)) { $offenders.Add($path); break } }
        }
    }
    # -CaseSensitive, as in the range mode (N-audit-infra-01): two indexed paths differing only in case are two offenders.
    return [pscustomobject]@{
        Matched = ($offenders.Count -gt 0)
        Offenders = @($offenders | Sort-Object -Unique -CaseSensitive)
    }
}

# Read one exact cat-file response at a time. The aggregate batch has no array-size limit; a single text file
# is bounded before allocation. UTF-8 is decoded only after its complete payload arrives, so a split multibyte
# sequence or combining sequence has the same normalization and matching semantics as the range mode.
function Read-IndexedBlobText([IO.Stream]$Stream, [string]$Blob,
        [Threading.CancellationToken]$CancellationToken, [long]$MaximumBlobBytes = 256MB) {
    $headerBytes = [byte[]]::new(128)
    $single = [byte[]]::new(1)
    $headerLength = 0
    while ($true) {
        $read = $Stream.ReadAsync($single, 0, 1, $CancellationToken).GetAwaiter().GetResult()
        if ($read -ne 1) { throw 'Could not read an indexed text file header. No matching contents were printed.' }
        if ($single[0] -eq 10) { break }
        if ($headerLength -eq $headerBytes.Length -or $single[0] -gt 127) {
            throw 'An indexed text file header was invalid. No matching contents were printed.'
        }
        $headerBytes[$headerLength++] = $single[0]
    }
    $header = [Text.Encoding]::ASCII.GetString($headerBytes, 0, $headerLength).Split(' ')
    $size = 0L
    if ($header.Count -ne 3 -or $header[0] -cne $Blob -or $header[1] -cne 'blob' -or
        $header[2] -cnotmatch '^(0|[1-9][0-9]*)$' -or
        -not [long]::TryParse($header[2], [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$size)) {
        throw 'An indexed text file header was invalid. No matching contents were printed.'
    }
    if ($size -gt $MaximumBlobBytes -or $size -gt [int]::MaxValue) {
        throw 'An indexed text file exceeds the privacy check per-file limit. No matching contents were printed.'
    }
    $bytes = [byte[]]::new([int]$size)
    $position = 0
    while ($position -lt $bytes.Length) {
        $read = $Stream.ReadAsync($bytes, $position, [Math]::Min(65536, $bytes.Length - $position),
            $CancellationToken).GetAwaiter().GetResult()
        if ($read -eq 0) { throw 'An indexed text file payload was incomplete. No matching contents were printed.' }
        $position += $read
    }
    if ($Stream.ReadAsync($single, 0, 1, $CancellationToken).GetAwaiter().GetResult() -ne 1 -or $single[0] -ne 10) {
        throw 'An indexed text file terminator was invalid. No matching contents were printed.'
    }
    return ([Text.UTF8Encoding]::new($false, $false)).GetString($bytes)
}

function Get-MatchingIndexedBlobs([string[]]$Blobs, [psobject]$Needles, [string]$Repository) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-c', 'core.fsmonitor=false', '-C', $Repository, 'cat-file', '--batch')) {
        $start.ArgumentList.Add($argument)
    }
    return ,(Invoke-IndexedBlobProcess $start $Blobs $Needles)
}

# The process I/O has its own boundary so the self-test can exercise real failed and blocked children without
# substituting Git behavior or waiting a full production deadline. Production always uses the 60-second default.
function Invoke-IndexedBlobProcess([Diagnostics.ProcessStartInfo]$Start, [string[]]$Blobs, [psobject]$Needles,
        [ValidateRange(1, 60000)][int]$TimeoutMilliseconds = 60000) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $Start
    $deadline = [Threading.CancellationTokenSource]::new($TimeoutMilliseconds)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    $matched = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    try {
        [void]$process.Start()
        $started = $true
        $stderr = $process.StandardError.ReadToEndAsync()
        foreach ($blob in $Blobs) {
            # One request, then one response: stdin cannot fill while stdout waits to be drained.
            $request = [Text.Encoding]::ASCII.GetBytes($blob + "`n")
            [void]$process.StandardInput.BaseStream.WriteAsync($request, 0, $request.Length, $deadline.Token).GetAwaiter().GetResult()
            [void]$process.StandardInput.BaseStream.FlushAsync($deadline.Token).GetAwaiter().GetResult()
            $text = Read-IndexedBlobText $process.StandardOutput.BaseStream $blob $deadline.Token
            if (Test-PrivacyText $text $Needles) { [void]$matched.Add($blob) }
            $text = $null
        }
        $process.StandardInput.Close()
        $extra = [byte[]]::new(1)
        if ($process.StandardOutput.BaseStream.ReadAsync($extra, 0, 1, $deadline.Token).GetAwaiter().GetResult() -ne 0) {
            throw 'Unexpected indexed text file response data. No matching contents were printed.'
        }
        if (-not $process.WaitForExit([int][Math]::Max(1, $TimeoutMilliseconds - $timer.ElapsedMilliseconds))) {
            throw 'The privacy check exceeded its 60-second git bound.'
        }
        [void]$stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Could not read the indexed text files. No matching contents were printed.' }
    }
    catch {
        if ($deadline.IsCancellationRequested) { throw 'The privacy check exceeded its 60-second git bound.' }
        throw
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill()
            if (-not $process.WaitForExit(5000)) { throw 'The privacy check could not stop its owned Git child.' }
        }
        $deadline.Dispose()
        $process.Dispose()
    }
    return ,$matched
}

function Get-IndexPrivacyFindings([psobject]$Needles, [string]$Repository = $repoRoot) {
    $protected = Get-IndexedProtectedPaths $Repository
    if (@($protected).Count -gt 0) {
        return [pscustomobject]@{ Protected = $protected; Scanned = $false; Matched = $false; Offenders = @() }
    }
    $grep = Get-IndexedPrivacyOffenders $Needles $Repository
    return [pscustomobject]@{ Protected = @(); Scanned = $true; Matched = $grep.Matched; Offenders = $grep.Offenders }
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
    # Identifier sources: OneDrive is optional once the run names an identifier.
    foreach ($case in @(
        @(@('Contoso'), '', '', 'Contoso', 'an explicit identifier without OneDrive'),
        @(@(), 'Contoso', '', 'Contoso', 'an employer name without OneDrive'),
        @(@(), '', 'C:\Synthetic\OneDrive - Contoso Ltd', 'OneDrive - Contoso Ltd|Contoso Ltd', 'the employer a business OneDrive names'),
        @(@('Fabrikam'), '', 'C:\Synthetic\OneDrive Work', 'Fabrikam|OneDrive Work', 'an identifier beside an unnamed OneDrive'),
        @(@(), '', '', 'THROWS', 'nothing to refuse'),
        @(@(), '', 'C:\Synthetic\OneDrive Work', 'THROWS', 'an unnamed OneDrive and no identifier')
    )) {
        try { $got = (Get-PrivacyIdentifiers $case[0] $case[1] $case[2]) -join '|' } catch { $got = 'THROWS' }
        if ($got -cne $case[3]) { throw ('Public privacy identifier self-test failed: ' + $case[4] + '.') }
        $count++
    }
    # Matchers, with synthetic identifiers only, never a real account's (a range scan of this file
    # would rightly report one): a profile name matches as a profile path, never as a bare word.
    $needles = Get-PrivacyNeedles @('Contoso') 'ExampleProfile'
    foreach ($case in @(
        @('C:\Users\ExampleProfile\AppData\Local\file.txt', $true, 'a Windows profile path'),
        @('/c/Users/exampleprofile/source', $true, 'a POSIX profile path, any case'),
        @('"C:\\Users\\ExampleProfile\\AppData"', $true, 'an escaped profile path in a string literal'),
        # The profile root itself, where the name ends the path (Codex review 2026-11-02): every form used to pass.
        @('cache=C:\Users\ExampleProfile', $true, 'a Windows profile root at the end of the line'),
        @('"C:\Users\ExampleProfile"', $true, 'a quoted Windows profile root'),
        @('home is /c/Users/EXAMPLEPROFILE', $true, 'a POSIX profile root at the end of the line, any case'),
        @('"C:\\Users\\ExampleProfile"', $true, 'a quoted escaped profile root'),
        @('cd /Users/ExampleProfile && ls', $true, 'a profile root before a space'),
        @(('C:\Users\ExampleProfile' + "`r"), $true, 'a profile root before a carriage return'),
        @('The cache is under C:\Users\ExampleProfile.', $true, 'a profile root ending a sentence'),
        @('Run as ExampleProfile, then open the ExampleProfile panel.', $false, 'the bare user name'),
        @('open the panel as "ExampleProfile"', $false, 'the bare user name, quoted at the end of the line'),
        @('C:\Users\ExampleProfiles\file.txt', $false, 'a longer name sharing the prefix'),
        @('"C:\Users\ExampleProfiles"', $false, 'a longer name sharing the prefix, as a quoted root'),
        @('/c/Users/ExampleProfile.Old/cache', $false, 'a dotted longer name sharing the prefix'),
        # A longer name in any script is still a longer name (Codex recheck 2026-11-02): these all used to match.
        @(('C:\Users\ExampleProfile' + [char]0x00E9), $false, 'a longer name ending in an accented letter'),
        @(('/c/Users/ExampleProfile' + [char]0x5F20 + '/cache'), $false, 'a longer name ending in a CJK letter'),
        @(('"C:\Users\ExampleProfile' + [char]0x0301 + '"'), $false, 'a longer name ending in a combining mark'),
        @(('C:\Users\ExampleProfile' + [char]0x0663), $false, 'a longer name ending in a non-ASCII digit'),
        @(('C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x10400)), $false, 'a longer name ending in a supplementary letter'),
        @(('C:\Users\ExampleProfile.' + [char]::ConvertFromUtf32(0x10400)), $false, 'a dotted longer name ending in a supplementary letter'),
        @(('C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x1D7CE)), $false, 'a longer name ending in a supplementary digit'),
        @(('C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x1F600)), $true, 'a profile root before a supplementary symbol'),
        @('Built for CONTOSO by its team', $true, 'an explicit identifier, any case'),
        @('nothing here identifies anyone', $false, 'unrelated text')
    )) {
        if ((Test-PrivacyText $case[0] $needles) -ne $case[1]) { throw ('Public privacy matcher self-test failed: ' + $case[2] + '.') }
        $count++
    }
    # A user name outside ASCII: its other case is the same profile, and a longer name in any script is not.
    $accentName = 'L' + [char]0x00E9 + 'a'
    $accentNeedles = Get-PrivacyNeedles @('Fabrikam') $accentName
    foreach ($case in @(
        @(('C:\Users\' + $accentName), $true, 'a non-ASCII profile root'),
        @(('C:\Users\L' + [char]0x00C9 + 'A'), $true, 'a non-ASCII profile root in another case'),
        @(('/c/Users/' + $accentName + 'x'), $false, 'a longer non-ASCII name ending in an ASCII letter'),
        @(('C:\Users\' + $accentName + [char]0x00E9), $false, 'a longer non-ASCII name ending in an accented letter')
    )) {
        if ((Test-PrivacyText $case[0] $accentNeedles) -ne $case[1]) { throw ('Public privacy matcher self-test failed: ' + $case[2] + '.') }
        $count++
    }
    $decomposedName = 'Le' + [char]0x0301 + 'a'
    $composedIdentifier = 'Syntheti' + [char]0x00E9 + 'c'
    $decomposedIdentifier = 'Synthetie' + [char]0x0301 + 'c'
    $composedNeedles = Get-PrivacyNeedles @($composedIdentifier) $accentName
    $decomposedNeedles = Get-PrivacyNeedles @($decomposedIdentifier) $decomposedName
    foreach ($normalizedNeedles in @($composedNeedles, $decomposedNeedles)) {
        foreach ($normalizedText in @($composedIdentifier, $decomposedIdentifier,
                ('C:\Users\' + $accentName), ('C:\Users\' + $decomposedName))) {
            if (-not (Test-PrivacyText $normalizedText $normalizedNeedles)) {
                throw 'Public privacy NFC self-test failed: canonically equivalent identifiers or profile names did not match.'
            }
            $count++
        }
        if (Test-PrivacyText ('C:\Users\' + $decomposedName + 'x') $normalizedNeedles) {
            throw 'Public privacy NFC self-test failed: normalization matched a different longer profile name.'
        }
        $count++
    }
    foreach ($invalidUnicode in @(
            { ConvertTo-PrivacyNfc ([string][char]0xD800) },
            { Get-PrivacyNeedles @([string][char]0xD800) 'SyntheticProfile' },
            { Get-PrivacyNeedles @('SyntheticIdentifier') ([string][char]0xD800) },
            { Test-PrivacyText ([string][char]0xD800) $needles })) {
        $refusal = ''
        try { [void](& $invalidUnicode) } catch { $refusal = $_.Exception.Message }
        if ($refusal -cne 'The privacy check cannot normalize malformed Unicode text.') {
            throw 'Public privacy Unicode self-test failed: malformed UTF-16 was not refused with a fixed diagnostic.'
        }
        $count++
    }
    Write-Host 'PASS: privacy Unicode scalar boundaries, NFC in both directions, and malformed UTF-16 refusal'
    if (-not ('PrivacyChunkedStream' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public sealed class PrivacyChunkedStream : MemoryStream {
    private readonly int maximumRead;
    public PrivacyChunkedStream(byte[] value, int maximumRead) : base(value, false) { this.maximumRead = maximumRead; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        return base.ReadAsync(buffer, offset, Math.Min(count, maximumRead), token);
    }
}
'@
    }
    function Blob-Frame([string]$Value) {
        $payload = [Text.Encoding]::UTF8.GetBytes($Value)
        $header = [Text.Encoding]::ASCII.GetBytes('abc blob ' + $payload.Length + [char]10)
        return ,([byte[]](@($header) + @($payload) + @(10)))
    }
    function Frame-Refuses([string]$Name, [string]$Response, [string]$Expected, [long]$Maximum = 256MB) {
        $stream = [PrivacyChunkedStream]::new([Text.Encoding]::UTF8.GetBytes($Response), 1)
        $refusal = ''
        try { [void](Read-IndexedBlobText $stream 'abc' ([Threading.CancellationToken]::None) $Maximum) }
        catch { $refusal = $_.Exception.Message }
        finally { $stream.Dispose() }
        if ($refusal -cne $Expected) { throw ('Public privacy blob-frame self-test failed: ' + $Name + '.') }
        $script:count++
    }
    $frameText = 'C:\Users\' + $decomposedName + [char]::ConvertFromUtf32(0x1F600)
    $frames = [byte[]]((Blob-Frame $frameText) + (Blob-Frame '') + (Blob-Frame 'neutral'))
    foreach ($chunkSize in @(1, 3, 65536)) {
        $stream = [PrivacyChunkedStream]::new($frames, $chunkSize)
        try {
            $first = Read-IndexedBlobText $stream 'abc' ([Threading.CancellationToken]::None)
            $empty = Read-IndexedBlobText $stream 'abc' ([Threading.CancellationToken]::None)
            $last = Read-IndexedBlobText $stream 'abc' ([Threading.CancellationToken]::None)
            if ($first -cne $frameText -or $empty -cne '' -or $last -cne 'neutral' -or
                -not (Test-PrivacyText $first $accentNeedles) -or $stream.Position -ne $frames.Length) {
                throw 'Public privacy blob-frame self-test failed: split UTF-8, empty payload, or sequential frames changed.'
            }
            $count++
        }
        finally { $stream.Dispose() }
    }
    $lf = [string][char]10
    $headerFailure = 'An indexed text file header was invalid. No matching contents were printed.'
    foreach ($case in @(
            @('wrong object id', ('other blob 0' + $lf + $lf)),
            @('wrong object type', ('abc tree 0' + $lf + $lf)),
            @('negative size', ('abc blob -1' + $lf)),
            @('nondecimal size', ('abc blob 1x' + $lf)),
            @('noncanonical size', ('abc blob 01' + $lf)),
            @('overflowing size', ('abc blob 9223372036854775808' + $lf)),
            @('overlong header', (('a' * 129) + $lf)),
            @('non-ASCII header', ('abc bl' + [char]0x00E9 + 'b 0' + $lf)))) {
        Frame-Refuses $case[0] $case[1] $headerFailure
    }
    Frame-Refuses 'missing header newline' 'abc blob 0' 'Could not read an indexed text file header. No matching contents were printed.'
    Frame-Refuses 'short payload' ('abc blob 3' + $lf + 'ab') 'An indexed text file payload was incomplete. No matching contents were printed.'
    Frame-Refuses 'missing terminator' ('abc blob 3' + $lf + 'abc') 'An indexed text file terminator was invalid. No matching contents were printed.'
    Frame-Refuses 'wrong terminator' ('abc blob 3' + $lf + 'abcX') 'An indexed text file terminator was invalid. No matching contents were printed.'
    Frame-Refuses 'size checked before allocation' ('abc blob 5' + $lf) 'An indexed text file exceeds the privacy check per-file limit. No matching contents were printed.' 4
    Frame-Refuses 'oversized blob' ('abc blob 268435457' + $lf) 'An indexed text file exceeds the privacy check per-file limit. No matching contents were printed.'
    $canceled = [Threading.CancellationTokenSource]::new()
    $canceled.Cancel()
    $stream = [PrivacyChunkedStream]::new((Blob-Frame 'neutral'), 1)
    $cancelObserved = $false
    try { [void](Read-IndexedBlobText $stream 'abc' $canceled.Token) }
    catch { $cancelObserved = $_.Exception.InnerException -is [OperationCanceledException] -or $_.Exception -is [OperationCanceledException] }
    finally { $stream.Dispose(); $canceled.Dispose() }
    if (-not $cancelObserved) { throw 'Public privacy blob-frame self-test failed: a canceled read did not stop.' }
    $count++
    Write-Host 'PASS: privacy bounded blob frames, split UTF-8, framing refusals, and canceled reads'
    # Range mode, then the default index mode, each on a synthetic repository this self-test creates and
    # removes under build\.
    $fixtureRoot = Join-Path $repoRoot ('build\privacy-selftest\' + [Guid]::NewGuid().ToString('N'))
    $synthetic = Join-Path $fixtureRoot 'range'
    $indexRepo = Join-Path $fixtureRoot 'index'
    [void][IO.Directory]::CreateDirectory($synthetic)
    [void][IO.Directory]::CreateDirectory($indexRepo)
    try {
        function Child-Start([string]$Command) {
            $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME ('pwsh' + $(if ($IsWindows) { '.exe' } else { '' }))))
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', $Command)) { $start.ArgumentList.Add($argument) }
            return $start
        }
        foreach ($childCase in @(
                @('failed child', '[Console]::Error.WriteLine("synthetic-private-content"); exit 23',
                    'Could not read the indexed text files. No matching contents were printed.'),
                @('unexpected output', '[Console]::Out.Write("synthetic-private-content")',
                    'Unexpected indexed text file response data. No matching contents were printed.'))) {
            $refusal = ''
            try { [void](Invoke-IndexedBlobProcess (Child-Start $childCase[1]) @() $needles 5000) }
            catch { $refusal = $_.Exception.Message }
            if ($refusal -cne $childCase[2]) {
                throw ('Public privacy process self-test failed: ' + $childCase[0] + ' did not produce its fixed refusal.')
            }
            $count++
        }
        $ready = Join-Path $fixtureRoot 'blocked-child-ready.txt'
        $blockedCommand = '[IO.File]::WriteAllText(''{0}'',[string]$PID); [Console]::Out.Write(''abc blob 4'' + [char]10 + ''ab''); [Console]::Out.Flush(); Start-Sleep -Seconds 12' -f $ready.Replace("'", "''")
        $refusal = ''
        $blockedTimer = [Diagnostics.Stopwatch]::StartNew()
        try { [void](Invoke-IndexedBlobProcess (Child-Start $blockedCommand) @('abc') $needles 5000) }
        catch { $refusal = $_.Exception.Message }
        if ($refusal -cne 'The privacy check exceeded its 60-second git bound.' -or
            -not [IO.File]::Exists($ready) -or $blockedTimer.ElapsedMilliseconds -gt 10000) {
            throw 'Public privacy process self-test failed: an admitted child blocked inside a payload did not time out.'
        }
        $childAlive = $false
        try {
            $ownedChild = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($ready))
            try { $childAlive = -not $ownedChild.HasExited } finally { $ownedChild.Dispose() }
        }
        catch [ArgumentException] { }
        if ($childAlive) { throw 'Public privacy process self-test failed: its timed-out child remained alive.' }
        $count++
        Write-Host 'PASS: privacy failed child, unexpected output, and blocked-child timeout with process cleanup'
        function Step([string[]]$Arguments, [string]$Repository = $synthetic) {
            $done = Invoke-IndexedGit $Arguments $Repository
            if ($done.ExitCode -ne 0) { throw ('Synthetic repository step failed: git ' + ($Arguments -join ' ')) }
            return $done.Output.Trim()
        }
        function Commit([string]$Line, [string]$Message, [string]$Author = 'Synthetic Author', [string]$File = 'notes.txt') {
            [IO.File]::AppendAllText((Join-Path $synthetic $File), $Line + "`n")
            [void](Step @('add', '--', $File))
            [void](Step @('-c', ('user.name=' + $Author), '-c', 'user.email=synthetic@example.invalid', 'commit', '-q', '-m', $Message))
            return (Step @('rev-parse', 'HEAD')).Substring(0, 12)
        }
        [void](Step @('init', '-q'))
        [void](Commit 'base' 'base')
        $base = Step @('rev-parse', 'HEAD')
        $pathCommit = Commit 'cache at C:\Users\ExampleProfile\AppData\Local\cache' 'record a cache path'
        $bareCommit = Commit 'ExampleProfile can open the panel' 'mention the role'
        $messageCommit = Commit 'neutral line' 'built for Contoso'
        $authorCommit = Commit 'another neutral line' 'neutral subject' 'Contoso Builder'
        $addedCommit = Commit 'contoso was here' 'add a line'
        [IO.File]::WriteAllText((Join-Path $synthetic 'notes.txt'), ([IO.File]::ReadAllText((Join-Path $synthetic 'notes.txt'))).Replace("contoso was here`n", ''))
        [void](Step @('add', '--', 'notes.txt'))
        [void](Step @('-c', 'user.name=Synthetic Author', '-c', 'user.email=synthetic@example.invalid', 'commit', '-q', '-m', 'remove the line'))
        $removeCommit = (Step @('rev-parse', 'HEAD')).Substring(0, 12)
        $scan = Get-RangePrivacyFindings ($base + '..HEAD') $needles $synthetic
        $seen = @($scan.Findings | ForEach-Object { $_.Commit + ' ' + $_.Kind + ' ' + $_.Path + ' ' + $_.Count })
        $expected = @(($pathCommit + ' added-line notes.txt 1'), ($messageCommit + ' message  1'), ($authorCommit + ' author  1'),
            ($authorCommit + ' committer  1'), ($addedCommit + ' added-line notes.txt 1'))
        if ($scan.Commits -ne 6 -or (@($seen | Sort-Object) -join '|') -cne (@($expected | Sort-Object) -join '|')) {
            throw 'Public privacy range self-test failed: the findings were not exactly the path, message, author, committer and later-removed added line.'
        }
        if (@($scan.Findings | Where-Object { $_.Commit -in @($bareCommit, $removeCommit) }).Count -ne 0) {
            throw 'Public privacy range self-test failed: a bare user name or a removed line was reported.'
        }
        $count += 2
        $protectedCommit = Commit 'TOKEN=synthetic' 'add a settings file' 'Synthetic Author' 'local.env'
        $refused = Get-RangePrivacyFindings ($removeCommit + '..HEAD') $needles $synthetic
        if (@($refused.Protected).Count -ne 1 -or $refused.Protected[0].Pattern -cne 'W01' -or $refused.Commits -ne 0) {
            throw ('Public privacy range self-test failed: a protected path in the range was not refused before reading (' + $protectedCommit + ').')
        }
        $count++
        # Merges (N-audit-20261029-14): a conflict resolved with a line new to BOTH parents, and a path
        # only a merge adds. With no merge format git log prints neither, so both used to pass unread.
        $identity = @('-c', 'user.name=Synthetic Author', '-c', 'user.email=synthetic@example.invalid')
        [void](Commit 'shared line' 'add the merge file' 'Synthetic Author' 'merge.txt')
        $mergeBase = Step @('rev-parse', 'HEAD')
        $trunk = Step @('symbolic-ref', '--short', 'HEAD')
        [void](Step @('checkout', '-q', '-b', 'side'))
        [void](Commit 'side wording' 'side edit' 'Synthetic Author' 'merge.txt')
        [void](Step @('checkout', '-q', $trunk))
        [void](Commit 'trunk wording' 'trunk edit' 'Synthetic Author' 'merge.txt')
        $conflict = Invoke-IndexedGit ($identity + @('merge', '-q', '--no-ff', '--no-edit', 'side')) $synthetic
        if ($conflict.ExitCode -eq 0) { throw 'Synthetic repository step failed: the side branch merged without the conflict the merge case needs.' }
        [IO.File]::WriteAllText((Join-Path $synthetic 'merge.txt'), "shared line`ntrunk wording`nside wording`nresolved for Contoso`n")
        [void](Step @('add', '--', 'merge.txt'))
        [void](Step ($identity + @('commit', '-q', '-m', 'merge the side branch')))
        $resolution = (Step @('rev-parse', 'HEAD')).Substring(0, 12)
        $merged = Get-RangePrivacyFindings ($mergeBase + '..HEAD') $needles $synthetic
        $mergeSeen = @($merged.Findings | ForEach-Object { $_.Commit + ' ' + $_.Kind + ' ' + $_.Path + ' ' + $_.Count }) -join '|'
        if ($merged.Commits -ne 3 -or $mergeSeen -cne ($resolution + ' added-line merge.txt 1')) {
            throw 'Public privacy range self-test failed: a line written only in a merge conflict resolution was not reported at the merge.'
        }
        $count++
        [void](Step @('checkout', '-q', '-b', 'side-two'))
        [void](Commit 'side two wording' 'side two edit' 'Synthetic Author' 'merge.txt')
        [void](Step @('checkout', '-q', $trunk))
        [void](Step ($identity + @('merge', '-q', '--no-ff', '--no-commit', 'side-two')))
        [IO.File]::WriteAllText((Join-Path $synthetic 'merge-only.pem'), "synthetic`n")
        [void](Step @('add', '--', 'merge-only.pem'))
        [void](Step ($identity + @('commit', '-q', '-m', 'merge the second side branch')))
        $mergeRefused = Get-RangePrivacyFindings ($resolution + '..HEAD') $needles $synthetic
        if (@($mergeRefused.Protected).Count -ne 1 -or $mergeRefused.Protected[0].Path -cne 'merge-only.pem' -or
            $mergeRefused.Protected[0].Pattern -cne 'W05' -or $mergeRefused.Commits -ne 0) {
            throw 'Public privacy range self-test failed: a protected path added only by a merge was not refused before reading.'
        }
        $count++
        # Two protected paths that differ only in case (N-audit-infra-01) are two refusals, not one. Staged straight into
        # the index, since this file system cannot hold both names, with core.ignorecase off so git keeps both entries.
        [void](Step @('config', 'core.ignorecase', 'false'))
        $variantBlob = Step @('hash-object', '-w', '--', 'notes.txt')
        $beforeVariants = Step @('rev-parse', 'HEAD')
        foreach ($variant in @('keys/Site.PEM', 'keys/site.pem')) {
            [void](Step @('update-index', '--add', '--cacheinfo', ('100644,' + $variantBlob + ',' + $variant)))
        }
        [void](Step ($identity + @('commit', '-q', '-m', 'add two keys whose names differ only in case')))
        $variantRefused = Get-RangePrivacyFindings ($beforeVariants + '..HEAD') $needles $synthetic
        $variantPaths = (@($variantRefused.Protected | ForEach-Object { $_.Path } | Sort-Object -CaseSensitive)) -join '|'
        if (@($variantRefused.Protected).Count -ne 2 -or $variantPaths -cne ((@('keys/Site.PEM', 'keys/site.pem') | Sort-Object -CaseSensitive) -join '|') -or
            $variantRefused.Commits -ne 0) {
            throw ('Public privacy range self-test failed: two protected paths differing only in case were not both refused: ' + $variantPaths)
        }
        $count++
        # Renames (Codex review 2026-11-02). With rename detection the names pass printed only a rename's new path, so a
        # protected file renamed to an ordinary name reached the patch read. Detection is forced on in this repository,
        # and proven to happen, so a global diff.renames=false cannot make the case pass without testing anything.
        [void](Step @('config', 'diff.renames', 'true'))
        $renameText = ((1..20 | ForEach-Object { 'neutral line ' + $_ }) -join "`n") + "`n"
        [IO.File]::WriteAllText((Join-Path $synthetic 'renamed-from.pem'), $renameText)
        [void](Step @('add', '-f', '--', 'renamed-from.pem'))
        [void](Step ($identity + @('commit', '-q', '-m', 'add a key')))
        $beforeRename = Step @('rev-parse', 'HEAD')
        [void](Step @('mv', '--', 'renamed-from.pem', 'renamed-to.txt'))
        [IO.File]::AppendAllText((Join-Path $synthetic 'renamed-to.txt'), "one more neutral line`n")
        [void](Step @('add', '--', 'renamed-to.txt'))
        [void](Step ($identity + @('commit', '-q', '-m', 'rename the key')))
        if ((Step @('log', '-M', '--format=', '--name-only', ($beforeRename + '..HEAD'))) -cne 'renamed-to.txt') {
            throw 'Synthetic repository step failed: git did not detect the rename the rename case needs.'
        }
        $renameRefused = Get-RangePrivacyFindings ($beforeRename + '..HEAD') $needles $synthetic
        if (@($renameRefused.Protected).Count -ne 1 -or $renameRefused.Protected[0].Path -cne 'renamed-from.pem' -or
            $renameRefused.Protected[0].Pattern -cne 'W05' -or $renameRefused.Commits -ne 0) {
            throw 'Public privacy range self-test failed: a protected path renamed to an ordinary one was not refused before reading.'
        }
        $count++
        # The patch pass reads a rename the same way, as a deletion plus an addition: a file carried into the range under
        # a new name is read whole at the rename. Under rename detection a pure rename printed no lines at all.
        [IO.File]::WriteAllText((Join-Path $synthetic 'carried.txt'), $renameText + "kept for Contoso`n")
        [void](Step @('add', '--', 'carried.txt'))
        [void](Step ($identity + @('commit', '-q', '-m', 'add a carried file')))
        $beforeCarry = Step @('rev-parse', 'HEAD')
        [void](Step @('mv', '--', 'carried.txt', 'carried-renamed.txt'))
        [void](Step ($identity + @('commit', '-q', '-m', 'rename the carried file')))
        $carryCommit = (Step @('rev-parse', 'HEAD')).Substring(0, 12)
        $carried = Get-RangePrivacyFindings ($beforeCarry + '..HEAD') $needles $synthetic
        $carriedSeen = @($carried.Findings | ForEach-Object { $_.Commit + ' ' + $_.Kind + ' ' + $_.Path + ' ' + $_.Count }) -join '|'
        if ($carried.Commits -ne 1 -or $carriedSeen -cne ($carryCommit + ' added-line carried-renamed.txt 1')) {
            throw ('Public privacy range self-test failed: a renamed file was not read whole at the rename: ' + $carriedSeen)
        }
        $count++
        # The DEFAULT mode (N-audit-20261029-15). git grep chooses the files it reads and cat-file reads them, so
        # nothing above vouches for it. This index holds the identifier in another case, a profile path, a profile
        # root at the end of a line and another quoted, a longer name, a dotted longer name and longer names in
        # other scripts as roots, a bare user name, quoted or not, clean text, a non-ASCII user name's root in both
        # cases and two longer forms of it, and the identifier inside a path of each protected shape the grep
        # excludes, in mixed case (icase) and once as a directory (the /** form). The policy refuses those paths
        # before the grep runs, so the grep is also driven directly to prove its own exclusions. .git/credentials
        # has no case: git refuses to index any path with a .git component.
        $indexFiles = @(
            @('docs/Notes.txt', 'Built for CONTOSO by its team', 'found'),
            @('src/Cache.txt', 'cache at /c/Users/ExampleProfile/source', 'found'),
            @('src/Root.txt', 'home=C:\Users\exampleprofile', 'found'),
            @('src/Quoted.txt', 'home: "/c/Users/ExampleProfile"', 'found'),
            @('src/Longer.txt', 'home="C:\Users\ExampleProfiles"', 'clean'),
            @('src/Dotted.txt', 'cache at /c/Users/ExampleProfile.Old/source', 'clean'),
            @('src/QuotedRole.txt', 'open the panel as "ExampleProfile"', 'clean'),
            @('src/LongerAccented.txt', ('home=C:\Users\ExampleProfile' + [char]0x00E9), 'clean'),
            @('src/LongerCjk.txt', ('cache at /c/Users/ExampleProfile' + [char]0x5F20 + '/source'), 'clean'),
            @('src/LongerMark.txt', ('home="C:\Users\ExampleProfile' + [char]0x0301 + '"'), 'clean'),
            @('src/LongerSupplementary.txt', ('home=C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x10400)), 'clean'),
            @('src/SymbolBoundary.txt', ('home=C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x1F600)), 'found'),
            @('src/AccentRoot.txt', ('home=C:\Users\' + $accentName), 'clean'),
            @('src/AccentUpper.txt', ('home="C:\Users\L' + [char]0x00C9 + 'A"'), 'clean'),
            @('src/AccentNfd.txt', ('home=C:\Users\' + $decomposedName), 'clean'),
            @('src/AccentLonger.txt', ('home=/c/Users/' + $accentName + 'x'), 'clean'),
            @('src/AccentLongerAccented.txt', ('home=C:\Users\' + $accentName + [char]0x00E9), 'clean'),
            @('src/IdentifierNfc.txt', $composedIdentifier, 'clean'),
            @('src/IdentifierNfd.txt', $decomposedIdentifier, 'clean'),
            @('src/Late.txt', (('x' * (1MB + 3)) + 'Contoso'), 'found'),
            @('src/LargeClean.txt', ('y' * (1MB + 7)), 'clean'),
            @('src/Role.txt', 'ExampleProfile can open the panel', 'clean'),
            @('src/Clean.txt', 'nothing here identifies anyone', 'clean'),
            @('config/LOCAL.ENV', 'owner=Contoso', 'protected'),
            @('config/.Env.Production', 'owner=Contoso', 'protected'),
            @('.NPMRC', 'owner=Contoso', 'protected'),
            @('certs/Site.PEM', 'owner=Contoso', 'protected'),
            @('certs/Site.KEY', 'owner=Contoso', 'protected'),
            @('certs/Site.P12', 'owner=Contoso', 'protected'),
            @('certs/Site.PFX', 'owner=Contoso', 'protected'),
            @('certs/App.KeyStore', 'owner=Contoso', 'protected'),
            @('certs/PrivateKey-Backup.txt', 'owner=Contoso', 'protected'),
            @('vault/old.pem/readme.txt', 'owner=Contoso', 'protected')
        )
        [void](Step @('init', '-q') $indexRepo)
        foreach ($entry in $indexFiles) {
            $file = Join-Path $indexRepo $entry[0]
            [void][IO.Directory]::CreateDirectory((Split-Path $file -Parent))
            [IO.File]::WriteAllText($file, $entry[1] + "`n")
            # -f: a global excludes file that ignores *.pem or .env must not shrink the fixture.
            [void](Step @('add', '-f', '--', $entry[0]) $indexRepo)
        }
        $protectedFixture = @($indexFiles | Where-Object { $_[2] -ceq 'protected' } | ForEach-Object { $_[0] } | Sort-Object)
        $foundFixture = (@($indexFiles | Where-Object { $_[2] -ceq 'found' } | ForEach-Object { $_[0] } | Sort-Object)) -join '|'
        $blocked = Get-IndexPrivacyFindings $needles $indexRepo
        if ($blocked.Scanned -or (@($blocked.Protected | ForEach-Object { $_.Path } | Sort-Object) -join '|') -cne ($protectedFixture -join '|')) {
            throw 'Public privacy index self-test failed: the protected indexed paths were not all refused before the grep.'
        }
        $count++
        $grep = Get-IndexedPrivacyOffenders $needles $indexRepo
        if (-not $grep.Matched -or (@($grep.Offenders) -join '|') -cne $foundFixture) {
            throw ('Public privacy index self-test failed: git grep did not report exactly the identifier in another case and the profile paths and roots, nothing excluded, no longer name and no bare user name: ' +
                (@($grep.Offenders) -join ', '))
        }
        $count++
        # The non-ASCII user name in the index (Codex recheck 2026-11-02): git grep -i missed its upper-case root here.
        $accentGrep = Get-IndexedPrivacyOffenders $accentNeedles $indexRepo
        $accentExpected = 'src/AccentNfd.txt|src/AccentRoot.txt|src/AccentUpper.txt'
        if ((@($accentGrep.Offenders) -join '|') -cne $accentExpected) {
            throw ('Public privacy index self-test failed: the index mode did not report exactly the non-ASCII profile root in both cases, and neither longer name: ' +
                (@($accentGrep.Offenders) -join ', '))
        }
        $count++
        [void](Step (@('rm', '-q', '--cached', '--') + $protectedFixture) $indexRepo)
        $scanned = Get-IndexPrivacyFindings $needles $indexRepo
        if (-not $scanned.Scanned -or @($scanned.Protected).Count -ne 0 -or -not $scanned.Matched -or (@($scanned.Offenders) -join '|') -cne $foundFixture) {
            throw 'Public privacy index self-test failed: the default mode did not grep, and report the same files, once nothing protected was indexed.'
        }
        $count++
        # Two indexed paths that differ only in case (N-audit-infra-01) are two offenders, listed and counted apart. The
        # second name is staged straight into the index with the first one's text, as the range case above stages its pair.
        [void](Step @('config', 'core.ignorecase', 'false') $indexRepo)
        $variantText = Step @('hash-object', '-w', '--', 'docs/Notes.txt') $indexRepo
        [void](Step @('update-index', '--add', '--cacheinfo', ('100644,' + $variantText + ',docs/NOTES.txt')) $indexRepo)
        $variantScan = Get-IndexPrivacyFindings $needles $indexRepo
        $variantExpected = (@(@('docs/NOTES.txt') + @($foundFixture.Split('|')) | Sort-Object -CaseSensitive)) -join '|'
        $variantSeen = (@($variantScan.Offenders | Sort-Object -CaseSensitive)) -join '|'
        if (-not $variantScan.Scanned -or @($variantScan.Offenders).Count -ne (1 + $foundFixture.Split('|').Count) -or $variantSeen -cne $variantExpected) {
            throw ('Public privacy index self-test failed: two indexed paths differing only in case were not both reported: ' + $variantSeen)
        }
        $count++
        # Both modes on the SAME content (Codex recheck 2026-11-02): once committed, the range mode must report exactly
        # the files the index mode reports, for both user names. log.showRoot is set so a global false cannot hide the
        # root commit's lines.
        [void](Step @('config', 'log.showRoot', 'true') $indexRepo)
        [void](Step ($identity + @('commit', '-q', '-m', 'commit the index fixture')) $indexRepo)
        $normalizedExpected = $accentExpected + '|src/IdentifierNfc.txt|src/IdentifierNfd.txt'
        foreach ($pair in @(@($needles, $variantExpected), @($accentNeedles, $accentExpected),
                @($composedNeedles, $normalizedExpected), @($decomposedNeedles, $normalizedExpected))) {
            $indexSeen = (@((Get-IndexPrivacyFindings $pair[0] $indexRepo).Offenders | Sort-Object -CaseSensitive)) -join '|'
            $rangeSeen = (@((Get-RangePrivacyFindings 'HEAD' $pair[0] $indexRepo).Findings | ForEach-Object { $_.Path } |
                Sort-Object -Unique -CaseSensitive)) -join '|'
            if ($indexSeen -cne $pair[1] -or $rangeSeen -cne $pair[1]) {
                throw ('Public privacy self-test failed: both modes did not report the same files on the same content. Index: ' +
                    $indexSeen + '; range: ' + $rangeSeen)
            }
            $count++
        }
    } finally {
        if ([IO.Directory]::Exists($fixtureRoot)) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    }
    Write-Host "Public privacy self-test passed: $count cases (path policy, matchers, range mode with merges and the default index mode on synthetic repositories, removed again)."
    exit 0
}

$identifiers = Get-PrivacyIdentifiers $Identifier $EmployerName ([Environment]::GetEnvironmentVariable('OneDriveCommercial'))
$username = [Environment]::GetEnvironmentVariable('USERNAME')
if ([string]::IsNullOrWhiteSpace($username)) { throw 'The user name must be available for the profile-path check.' }
foreach ($value in @($identifiers) + @($username)) {
    if ($value.Length -lt 2) { throw 'Privacy identifiers must each contain at least two characters.' }
}
$needles = Get-PrivacyNeedles $identifiers $username
$described = "$($identifiers.Count) identifier(s) and the profile path"

if (-not [string]::IsNullOrWhiteSpace($Range)) {
    $scan = Get-RangePrivacyFindings $Range $needles
    if (@($scan.Protected).Count -gt 0) {
        Write-Host "Public privacy range check blocked: $(@($scan.Protected).Count) protected path(s) in the range. No commit contents were scanned."
        foreach ($entry in $scan.Protected) { Write-Host ($entry.Pattern + ' ' + ($entry.Path | ConvertTo-Json -Compress)) }
        exit 1
    }
    if (@($scan.Findings).Count -gt 0) {
        Write-Host "Public privacy range check failed: $(@($scan.Findings).Count) finding(s) in $($scan.Commits) commit(s)."
        foreach ($finding in $scan.Findings) {
            Write-Host ($finding.Commit + ' ' + $finding.Kind + $(if ($finding.Path) { ' ' + ($finding.Path | ConvertTo-Json -Compress) } else { '' }) + ' x' + $finding.Count)
        }
        exit 1
    }
    Write-Host "Public privacy range check passed: $described checked across $($scan.Commits) commit(s): added lines, messages, author and committer lines."
    exit 0
}

$index = Get-IndexPrivacyFindings $needles
if (@($index.Protected).Count -gt 0) {
    Write-Host "Public privacy check blocked: $(@($index.Protected).Count) protected indexed path(s). No indexed contents were scanned."
    foreach ($entry in $index.Protected) { Write-Host ($entry.Pattern + ' ' + ($entry.Path | ConvertTo-Json -Compress)) }
    exit 1
}
if ($index.Matched) {
    Write-Host "Public privacy check failed: $(@($index.Offenders).Count) indexed text file(s) contain a workstation identifier."
    foreach ($path in $index.Offenders) { Write-Host ($path | ConvertTo-Json -Compress) }
    exit 1
}
Write-Host "Public privacy check passed: $described checked across indexed text files."
