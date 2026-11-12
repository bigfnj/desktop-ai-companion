#requires -Version 7
<#
.SYNOPSIS
    Check indexed text, or a commit range, for workstation-specific identifiers before a public push.
.DESCRIPTION
    Refuses protected paths before reading any contents. Git grep, which chooses the indexed
    text files to read, also receives explicit case-insensitive exclusions. During an unresolved
    merge every stage of a conflicted text file (base, ours and theirs) is read too. Both modes
    compare with one matcher, case-insensitively in every script. Only offending file paths,
    commits and counts are printed, never the matched text.

    Identifiers come from the run, never from this file: -Identifier and -EmployerName, plus
    the business OneDrive folder and the employer it names when OneDriveCommercial is set.
    OneDrive is optional once an identifier is given. The user name is matched only as a
    profile path (\Users\<name>, /Users/<name> and the doubled-backslash form of the first),
    because a common profile name such as a dictionary word matches everywhere as a bare word.
    The name must end there: at a separator, a quote, a space, the end of the line or any
    other character that cannot continue a name (a letter, mark or number of any script, or
    _ . $ - can), so the profile root itself is found, and a longer name that merely starts
    with this one is not.

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

function Get-PrivacyNeedles([string[]]$Identifiers, [string]$UserName) {
    # Identifiers are fixed strings; the user name only ever as a profile path. Test-PrivacyText alone compares them,
    # case-insensitively, in BOTH modes (see Get-IndexedPrivacyOffenders).
    # Fixed strings cannot say where a name ENDS, and a trailing separator on each needle missed the profile root
    # itself, at the end of a line or before a quote (Codex review 2026-11-02), while dropping the separator would
    # match a longer name too. So the profile rule is a .NET pattern: a separator, Users, one or more separators (the
    # doubled-backslash form too), the name, and then a boundary: the name must not go on with a letter, mark or
    # number of any script, a connector such as _, or . $ - (Codex recheck 2026-11-02: an ASCII-only class let an
    # accented, CJK or combining-mark continuation end the name, so a different, longer name matched). A single
    # trailing dot is allowed before it, as Win32 drops one and a sentence ends with one, but a dot that continues a
    # name does not end it. The prefix is required, so the bare word never matches.
    # Each value is kept as given AND composed (NFC), so a name stored one way still matches text written the other way
    # (N-codex-1102-01): Test-PrivacyText reads the text as given and composed too.
    $fixed = New-Object 'Collections.Generic.List[string]'
    foreach ($value in $Identifiers) {
        foreach ($form in @($value, (ConvertTo-ComposedPrivacyText $value))) { if (-not $fixed.Contains($form)) { $fixed.Add($form) } }
    }
    $profileRoot = ''
    if (-not [string]::IsNullOrEmpty($UserName)) {
        $names = @([regex]::Escape($UserName))
        $composedName = ConvertTo-ComposedPrivacyText $UserName
        if (-not [string]::Equals($composedName, $UserName, [StringComparison]::Ordinal)) { $names += [regex]::Escape($composedName) }
        $profileRoot = '[\\/]Users[\\/]+(?:' + ($names -join '|') + ')\.?(?![\p{L}\p{M}\p{N}\p{Pc}.$-])'
    }
    return [pscustomobject]@{ Fixed = $fixed.ToArray(); Profile = $profileRoot }
}

# The composed (NFC) form, or the text unchanged when it holds a lone surrogate, which Normalize refuses.
function ConvertTo-ComposedPrivacyText([string]$Text) {
    try { return $Text.Normalize([Text.NormalizationForm]::FormC) } catch [ArgumentException] { return $Text }
}

# The categories that continue a name, the profile pattern's lookahead class: a letter, mark or number of any script, or
# a connector such as _.
$script:PrivacyNameCategories = [Collections.Generic.HashSet[Globalization.UnicodeCategory]]::new([Globalization.UnicodeCategory[]]@(
    'UppercaseLetter', 'LowercaseLetter', 'TitlecaseLetter', 'ModifierLetter', 'OtherLetter',
    'NonSpacingMark', 'SpacingCombiningMark', 'EnclosingMark',
    'DecimalDigitNumber', 'LetterNumber', 'OtherNumber', 'ConnectorPunctuation'))

function Test-PrivacyText([string]$Text, [psobject]$Needles) {
    # The text as given, then composed (NFC) when that differs (N-codex-1102-01): a user name stored composed did not match
    # the same name written decomposed, in either mode. Both forms, not the composed one alone, so nothing the old matcher
    # found is lost: composing folds a combining mark into the letter before it, and an identifier followed by one (no
    # boundary applies to identifiers) matched as given and would not once composed.
    $forms = @($Text)
    $composed = ConvertTo-ComposedPrivacyText $Text
    # Ordinal: -cne compares culture-aware, which calls the two spellings EQUAL, so the composed form was never read.
    if (-not [string]::Equals($composed, $Text, [StringComparison]::Ordinal)) { $forms += $composed }
    foreach ($form in $forms) {
        foreach ($needle in $Needles.Fixed) {
            if ($form.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
        if (-not $Needles.Profile) { continue }
        foreach ($match in [regex]::Matches($form, $Needles.Profile, [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')) {
            # The lookahead's classes see one UTF-16 unit, so a letter outside the Basic Multilingual Plane (a surrogate
            # pair) after the name ended it, and a longer name matched (N-codex-1102-01). The character after the match is
            # classified whole here; a lone or reversed surrogate is no letter and still ends the name.
            $end = $match.Index + $match.Length
            if ($end -lt $form.Length -and [char]::IsHighSurrogate($form[$end]) -and
                $script:PrivacyNameCategories.Contains([Globalization.CharUnicodeInfo]::GetUnicodeCategory($form, $end))) { continue }
            return $true
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

# Paths without repeats, in ordinal order (N-bd-w11-tests-scripts-1-02). Sort-Object -Unique compares culture-aware even
# with -CaseSensitive, so two names that differ only in Unicode normalization (a composed and a decomposed letter), two
# files to git, were listed and counted as one; measured under Windows PowerShell 5.1 and PowerShell 7 alike. The ordinal
# comparer also keeps N-audit-infra-01's rule: two names that differ only in case stay two. Ordinal order lists upper
# case before lower case, where the culture order it replaces listed lower case first.
function Select-UniquePrivacyPath([string[]]$Paths) {
    $unique = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $Paths) { [void]$unique.Add($path) }
    return $unique
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
    # Ordinal (N-audit-infra-01, N-bd-w11-tests-scripts-1-02): Sort-Object -Unique alone compared ignoring case, and with
    # -CaseSensitive still culture-aware, so two paths that differ only in case, or only in Unicode normalization, two
    # files to git and to any case-sensitive checkout, were listed and counted as one.
    $protectedPaths = @($names.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim("`n") } |
        Where-Object { $_.Length -gt 0 -and (Get-ProtectedPattern $_) })
    $protected = @(Select-UniquePrivacyPath $protectedPaths |
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
# During an unresolved merge git ls-files --cached names a conflicted path once per stage, so a conflicted key was
# listed and counted up to three times (N-bd-w12-review-05); the listing goes through the ordinal de-duplication the
# range mode uses, which keeps two paths that differ only in case or in normalization apart.
function Get-IndexedProtectedPaths([string]$Repository = $repoRoot) {
    $listed = Invoke-IndexedGit @('ls-files', '--cached', '-z') $Repository
    if ($listed.ExitCode -ne 0) { throw 'Could not enumerate indexed paths. No indexed contents were scanned.' }
    $indexedNames = @($listed.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { Get-ProtectedPattern $_ })
    $protected = @()
    foreach ($path in (Select-UniquePrivacyPath $indexedNames)) {
        $protected += [pscustomobject]@{ Path = $path; Pattern = (Get-ProtectedPattern $path) }
    }
    return ,$protected
}

# The default mode's matcher is Test-PrivacyText, the one the range mode applies to every added line, so the two
# modes cannot disagree about case or about where a name ends (Codex recheck 2026-11-02: git grep -i folds only ASCII
# here, so an upper-case accented profile name passed the index mode that the range mode refused, and its extended
# regex has no Unicode classes). git chooses WHICH files: every indexed text file (-I; the empty pattern matches any
# line), names only, NUL-separated, with every protected shape excluded again as a second line behind the refusal
# above. One git cat-file then reads exactly those blobs, and only the offending paths leave this function.
# git grep --cached reads resolved (stage 0) entries only and skips every stage of an unmerged path, so during an
# unresolved merge a conflicted file was never read and the check passed whatever its stages held (N-bd-w12-review-05).
# Those stages are read too: git grep picks the text ones (Select-IndexedTextStages), the same cat-file reads them, and
# a conflicted path is one offender however many of its stages match.
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
    $staged = Invoke-IndexedGit @('ls-files', '--cached', '-s', '-z') $Repository
    if ($staged.ExitCode -ne 0) { throw 'Could not enumerate indexed paths. No indexed contents were scanned.' }
    # Ordinal keys (N-audit-infra-01): two indexed paths differing only in case are two files. Each record is
    # "<mode> <blob> <stage>" TAB path. A resolved path has its one stage-0 entry, which the grep above may name. An
    # unmerged path has one entry per stage (1 base, 2 ours, 3 theirs) and no stage 0; each stage of a regular file is
    # named to git grep as :<stage>:<path>, unless the path has a protected shape, which the grep's own exclusions do not
    # reach in that form (the refusal before this function already stops a protected path, unmerged or not).
    # A symbolic link (mode 120000) is a blob holding its target's text, which the range mode reads as an added line, but
    # git grep --cached reads regular files only, so a link whose target carried an identifier or a profile path passed
    # this mode and failed -Range (N-bd-w13-review-07). Every stage of a link is named the same way, :0:<path> once it is
    # resolved, so git grep applies its text rule to the link's blob as to any stage, and the one cat-file reads it.
    $resolved = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $stages = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    foreach ($record in $staged.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        $tab = $record.IndexOf("`t")
        $entry = $record.Substring(0, $tab).Split(' ')
        $path = $record.Substring($tab + 1)
        if ($entry[2] -ceq '0') { $resolved[$path] = $entry[1] }
        elseif ($entry[0].StartsWith('100') -and -not (Get-ProtectedPattern $path)) { $stages[':' + $entry[2] + ':' + $path] = @($path, $entry[1]) }
        if ($entry[0] -ceq '120000' -and -not (Get-ProtectedPattern $path)) { $stages[':' + $entry[2] + ':' + $path] = @($path, $entry[1]) }
    }
    # (path, blob) pairs to read: the resolved text files the grep named, then the unmerged stages and links it calls text.
    $candidates = New-Object 'Collections.Generic.List[string[]]'
    foreach ($path in $paths) {
        if (-not $resolved.ContainsKey($path)) { throw 'An indexed text file had no index entry. No matching contents were printed.' }
        $candidates.Add(@($path, $resolved[$path]))
    }
    foreach ($name in (Select-IndexedTextStages ([string[]]@($stages.Keys)) $Repository)) {
        if (-not $stages.ContainsKey($name)) { throw 'An unmerged text stage had no index entry. No matching contents were printed.' }
        $candidates.Add($stages[$name])
    }
    if ($candidates.Count -gt 0) {
        $blobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($candidate in $candidates) { [void]$blobs.Add($candidate[1]) }
        $matched = Get-MatchingIndexedBlobs ([string[]]@($blobs)) $Needles $Repository
        foreach ($candidate in $candidates) { if ($matched.Contains($candidate[1])) { $offenders.Add($candidate[0]) } }
    }
    # Ordinal, as in the range mode (N-audit-infra-01, N-bd-w11-tests-scripts-1-02): two indexed paths differing only in
    # case, or only in Unicode normalization, are two offenders.
    return [pscustomobject]@{
        Matched = ($offenders.Count -gt 0)
        Offenders = @(Select-UniquePrivacyPath $offenders)
    }
}

# The unmerged stages, and the stages of symbolic links (N-bd-w13-review-07), named :<stage>:<path>, that git grep -I
# calls text (N-bd-w12-review-05). Given such a name, git grep reads that stage as a blob under its path, so it applies
# the rule it applies to a resolved file (the path's diff attributes, then a NUL byte near the start) and the two cannot
# disagree about which files are text. Names go in batches, as one command line cannot hold every stage of a large merge.
function Select-IndexedTextStages([string[]]$Names, [string]$Repository) {
    $text = New-Object 'Collections.Generic.List[string]'
    $next = 0
    while ($next -lt $Names.Count) {
        $batch = New-Object 'Collections.Generic.List[string]'
        $length = 0
        while ($next -lt $Names.Count -and ($batch.Count -eq 0 -or $length + $Names[$next].Length -lt 16000)) {
            $batch.Add($Names[$next])
            $length += $Names[$next].Length + 3
            $next++
        }
        $listed = Invoke-IndexedGit (@('grep', '--no-textconv', '-I', '-l', '-z', '-e', '', '--end-of-options') + $batch) $Repository
        if ($listed.ExitCode -gt 1) { throw 'Indexed git grep failed on the unmerged stages. No matching contents were printed.' }
        $text.AddRange([string[]]$listed.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    }
    return ,$text.ToArray()
}

# Read the named blobs with one git cat-file --batch and return the ones Test-PrivacyText matches. Each blob is
# decoded as the range mode decodes its patch (UTF-8, invalid bytes replaced) and dropped once judged.
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
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $buffer = [IO.MemoryStream]::new()
    try {
        [void]$process.Start()
        # Both outputs drain while the request is written, so neither side can block the other.
        $copy = $process.StandardOutput.BaseStream.CopyToAsync($buffer)
        $stderr = $process.StandardError.ReadToEndAsync()
        $request = [Text.Encoding]::ASCII.GetBytes(($Blobs -join "`n") + "`n")
        $write = $process.StandardInput.BaseStream.WriteAsync($request, 0, $request.Length)
        $inTime = $write.Wait(60000)
        if ($inTime) {
            $process.StandardInput.Close()
            $inTime = $process.WaitForExit(60000)
        }
        if (-not $inTime) {
            $process.Kill()
            $process.WaitForExit()
            throw 'The privacy check exceeded its 60-second git bound.'
        }
        [void]$copy.GetAwaiter().GetResult()
        [void]$stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Could not read the indexed text files. No matching contents were printed.' }
    }
    finally { $process.Dispose() }
    $bytes = $buffer.GetBuffer()
    $length = [int]$buffer.Length
    $decoder = [Text.UTF8Encoding]::new($false, $false)
    $matched = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $position = 0
    foreach ($blob in $Blobs) {
        # Each answer is "<oid> blob <size>" LF, the bytes, LF; anything else is refused, never guessed at.
        $end = [Array]::IndexOf($bytes, [byte]10, $position, $length - $position)
        $header = if ($end -lt 0) { @() } else { [Text.Encoding]::ASCII.GetString($bytes, $position, $end - $position).Split(' ') }
        if ($header.Count -ne 3 -or $header[0] -cne $blob -or $header[1] -cne 'blob' -or
            ($end + 1 + [long]$header[2]) -gt $length) {
            throw 'Could not read an indexed text file. No matching contents were printed.'
        }
        $size = [int]$header[2]
        if (Test-PrivacyText ($decoder.GetString($bytes, $end + 1, $size)) $Needles) { [void]$matched.Add($blob) }
        $position = $end + 1 + $size + 1
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

# Every fixture root holds this marker file, which only this script writes (N-bd-w12-review-06). The run that made the
# root keeps it open, shared for deletion alone, until the root is gone; a later run removes a root that holds the marker
# once it can open the marker the same way, which the open claim of a live run refuses (and a run that was killed, or a
# mutant of this script that skipped its removal, releases with its process). A root without the marker is never removed.
$script:PrivacyFixtureMarker = '.privacy-selftest-fixture'

# The self-test's fixture root under Parent, claimed by its marker, with every folder this run had to create for it,
# deepest first (N-bd-w11-tests-scripts-1-03). The root is new on every run; its build\privacy-selftest parent, and build
# itself, may already be there. What earlier runs left under Parent is cleared first (N-bd-w12-review-06), here rather
# than at the call site, so the synthetic cases below run the same path as the real fixture. The record is taken before
# anything is created, so Remove-PrivacyFixtureRoot never takes a folder above the parent that was already there.
function New-PrivacyFixtureRoot([string]$Parent) {
    Clear-StalePrivacyFixtures $Parent
    $root = Join-Path $Parent ([Guid]::NewGuid().ToString('N'))
    $created = New-Object 'Collections.Generic.List[string]'
    for ($dir = $root; $dir -and -not [IO.Directory]::Exists($dir); $dir = [IO.Path]::GetDirectoryName($dir)) { $created.Add($dir) }
    [void][IO.Directory]::CreateDirectory($root)
    $claim = [IO.FileStream]::new((Join-Path $root $script:PrivacyFixtureMarker), [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete)
    $note = [Text.Encoding]::ASCII.GetBytes("A fixture root of tests/Test-PublicCommitPrivacy.ps1 -SelfTest. A later run removes it once no run holds this file open.`n")
    $claim.Write($note, 0, $note.Length)
    $claim.Flush()
    return [pscustomobject]@{ Root = $root; Parent = [IO.Path]::GetDirectoryName($root); Created = $created.ToArray(); Claim = $claim; Removed = $false }
}

# Removes everything inside Path, deepest first, except the top-level entry named Keep. A reparse point is removed as a
# link and never entered, so a junction or symbolic link inside a fixture root cannot take what it points at. Anything
# else is made writable first, as git writes its objects read-only.
function Remove-PrivacyFixtureContents([string]$Path, [string]$Keep = '') {
    foreach ($entry in [IO.DirectoryInfo]::new($Path).GetFileSystemInfos()) {
        if ($entry.Name -ceq $Keep) { continue }
        if (-not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReadOnly) { $entry.Attributes = $entry.Attributes -bxor [IO.FileAttributes]::ReadOnly }
            if ($entry -is [IO.DirectoryInfo]) { Remove-PrivacyFixtureContents $entry.FullName }
        }
        $entry.Delete()
    }
}

# Removes a fixture root whose marker the caller holds open as Claim: the contents, then the marker, then the root. The
# marker is deleted while the claim is still open (it is shared for deletion), so no other run can claim the root between
# this run's last use of it and its removal.
function Remove-ClaimedPrivacyFixture([string]$Root, [IO.FileStream]$Claim) {
    try {
        Remove-PrivacyFixtureContents $Root $script:PrivacyFixtureMarker
        [IO.File]::Delete($Claim.Name)
    } finally { $Claim.Dispose() }
    [IO.Directory]::Delete($Root, $false)
}

# Removes the fixture roots earlier runs left under Parent (N-bd-w12-review-06): a run that was killed, or a firing mutant
# that skipped the removal, left its whole root there, one more per harness run, and no later run took any back. A folder
# goes only when it is no reparse point, holds the marker, and that marker can be claimed; a live run's root, a folder
# without the marker and a link stay, and so does a root whose removal fails here (it is counted in the line after the
# summary). New-PrivacyFixtureRoot runs it before it makes its own root.
function Clear-StalePrivacyFixtures([string]$Parent) {
    if (-not [IO.Directory]::Exists($Parent)) { return }
    foreach ($dir in [IO.DirectoryInfo]::new($Parent).GetDirectories()) {
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $marker = Join-Path $dir.FullName $script:PrivacyFixtureMarker
        if (-not [IO.File]::Exists($marker)) { continue }
        try { $claim = [IO.FileStream]::new($marker, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Delete) }
        catch [IO.IOException], [UnauthorizedAccessException] { continue }
        try { Remove-ClaimedPrivacyFixture $dir.FullName $claim } catch [IO.IOException], [UnauthorizedAccessException] { continue }
    }
}

# Removes the fixture root, then its build\privacy-selftest parent once that is empty, whoever made it, since that folder
# is this script's alone (N-bd-w12-review-06: one that was already there used to stay, so a leftover never cleared), then
# each folder above the parent that this run created, deepest first, once it is empty; a folder above it that was
# already there stays. Each delete is not recursive, so a parent that another run's fixture is still inside refuses it
# and stays, with everything above it; testing for emptiness first instead would race that run between the test and the
# delete.
function Remove-PrivacyFixtureRoot([psobject]$Fixture) {
    if ([IO.Directory]::Exists($Fixture.Root)) { Remove-ClaimedPrivacyFixture $Fixture.Root $Fixture.Claim } else { $Fixture.Claim.Dispose() }
    $Fixture.Removed = $true
    $above = @($Fixture.Created | Where-Object { $_.Length -lt $Fixture.Parent.Length })
    foreach ($dir in @($Fixture.Parent) + $above) {
        if (-not [IO.Directory]::Exists($dir)) { continue }
        try { [IO.Directory]::Delete($dir, $false) } catch [IO.IOException], [UnauthorizedAccessException] { return }
    }
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
        # Outside the Basic Multilingual Plane the next character is a surrogate pair (N-codex-1102-01): a letter there
        # continues the name, which the lookahead alone let end it. WITNESS: a symbol there still ends it.
        @(('C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x10400)), $false, 'a longer name ending in a letter outside the Basic Multilingual Plane'),
        @(('"/c/Users/ExampleProfile' + [char]::ConvertFromUtf32(0x1D7CE) + '"'), $false, 'a longer name ending in a digit outside the Basic Multilingual Plane'),
        @(('C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x1F600)), $true, 'a profile root before a symbol outside the Basic Multilingual Plane'),
        @('Built for CONTOSO by its team', $true, 'an explicit identifier, any case'),
        # Composing must not lose what the text as given matched: the o and the mark compose into one letter.
        @(('Built for Contoso' + [char]0x0301 + ' by its team'), $true, 'an explicit identifier followed by a combining mark'),
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
        @(('C:\Users\' + $accentName + [char]0x00E9), $false, 'a longer non-ASCII name ending in an accented letter'),
        # Composed and decomposed spellings are one name (N-codex-1102-01): neither matched the other.
        @(('C:\Users\Le' + [char]0x0301 + 'a'), $true, 'a decomposed spelling of a composed profile name'),
        @(('/c/Users/LE' + [char]0x0301 + 'A/cache'), $true, 'a decomposed spelling of a composed profile name in another case'),
        @(('C:\Users\Le' + [char]0x0301 + 'ax'), $false, 'a longer decomposed name')
    )) {
        if ((Test-PrivacyText $case[0] $accentNeedles) -ne $case[1]) { throw ('Public privacy matcher self-test failed: ' + $case[2] + '.') }
        $count++
    }
    $decomposedNeedles = Get-PrivacyNeedles @('Fabrik' + 'a' + [char]0x0301 + 'm') ('Le' + [char]0x0301 + 'a')
    foreach ($case in @(
        @(('C:\Users\' + $accentName), $true, 'a composed spelling of a decomposed profile name'),
        @(('C:\Users\Le' + [char]0x0301 + 'a'), $true, 'a decomposed profile name as given'),
        @(('/c/Users/' + $accentName + 'x'), $false, 'a longer composed spelling of a decomposed profile name'),
        @(('Built for Fabrik' + [char]0x00E1 + 'm'), $true, 'a composed spelling of a decomposed identifier'),
        @('Built for Fabrikam', $false, 'the identifier without its accent')
    )) {
        if ((Test-PrivacyText $case[0] $decomposedNeedles) -ne $case[1]) { throw ('Public privacy matcher self-test failed: ' + $case[2] + '.') }
        $count++
    }
    # Range mode, then the default index mode, each on a synthetic repository this self-test creates under
    # build\privacy-selftest\ and removes again, with that folder once it is empty and every folder above it that the run
    # created. What earlier runs left there goes first, when it is theirs and they are over (N-bd-w12-review-06).
    $fixture = New-PrivacyFixtureRoot (Join-Path $repoRoot 'build\privacy-selftest')
    $fixtureRoot = $fixture.Root
    $synthetic = Join-Path $fixtureRoot 'range'
    $indexRepo = Join-Path $fixtureRoot 'index'
    [void][IO.Directory]::CreateDirectory($synthetic)
    [void][IO.Directory]::CreateDirectory($indexRepo)
    try {
        # The fixture's own removal (N-bd-w11-tests-scripts-1-03, N-bd-w12-review-06), on synthetic folders inside this
        # run's root: the build\privacy-selftest parent goes once it is empty, even when it was already there, and a folder
        # above it that was already there stays; one this run created goes once it is empty, with build above it; and one
        # that another run's fixture is still inside stays, with that fixture untouched, until the last of them goes.
        $cleanup = Join-Path $fixtureRoot 'cleanup'
        $kept = Join-Path $cleanup 'kept\build\privacy-selftest'
        [void][IO.Directory]::CreateDirectory($kept)
        Remove-PrivacyFixtureRoot (New-PrivacyFixtureRoot $kept)
        if ([IO.Directory]::Exists($kept)) {
            throw 'Public privacy fixture self-test failed: an emptied build\privacy-selftest folder stayed because it was there before the run.'
        }
        if (-not [IO.Directory]::Exists((Split-Path $kept -Parent))) {
            throw 'Public privacy fixture self-test failed: a folder above build\privacy-selftest that the run did not create was removed.'
        }
        $count++
        $fresh = Join-Path $cleanup 'fresh'
        [void][IO.Directory]::CreateDirectory($fresh)
        Remove-PrivacyFixtureRoot (New-PrivacyFixtureRoot (Join-Path $fresh 'build\privacy-selftest'))
        if (@([IO.Directory]::GetFileSystemEntries($fresh)).Count -ne 0) {
            throw 'Public privacy fixture self-test failed: an emptied parent folder the run created was left behind.'
        }
        $count++
        $shared = Join-Path $cleanup 'shared\build\privacy-selftest'
        $first = New-PrivacyFixtureRoot $shared
        $second = New-PrivacyFixtureRoot $shared
        Remove-PrivacyFixtureRoot $first
        if ([IO.Directory]::Exists($first.Root) -or -not [IO.File]::Exists((Join-Path $second.Root $script:PrivacyFixtureMarker))) {
            throw 'Public privacy fixture self-test failed: removing a fixture took the fixture of another run still inside its parent.'
        }
        Remove-PrivacyFixtureRoot $second
        if ([IO.Directory]::Exists($shared)) {
            throw 'Public privacy fixture self-test failed: the build\privacy-selftest folder stayed after the last of two overlapping runs left it empty.'
        }
        $count++
        # What earlier runs left (N-bd-w12-review-06), on a synthetic parent, cleared as each new fixture is made there. A
        # root whose run is over (its claim released, as a killed run's is) goes, with a read-only file and a junction
        # inside it, and the junction's target is never entered. A root a live run still claims stays, and so do a folder
        # without the marker and a junction under the parent whose target holds one.
        $sweep = Join-Path $cleanup 'sweep\build\privacy-selftest'
        $outsideInner = Join-Path $cleanup 'outside-inner'
        $outsideLinked = Join-Path $cleanup 'outside-linked'
        foreach ($dir in @($outsideInner, $outsideLinked)) {
            [void][IO.Directory]::CreateDirectory($dir)
            [IO.File]::WriteAllText((Join-Path $dir $script:PrivacyFixtureMarker), "synthetic`n")
        }
        $stale = New-PrivacyFixtureRoot $sweep
        $readOnly = Join-Path $stale.Root 'object'
        [IO.File]::WriteAllText($readOnly, "synthetic`n")
        [IO.File]::SetAttributes($readOnly, [IO.FileAttributes]::ReadOnly)
        [void](New-Item -ItemType Junction -Path (Join-Path $stale.Root 'link') -Target $outsideInner)
        $stale.Claim.Dispose()
        $live = New-PrivacyFixtureRoot $sweep
        $unmarked = Join-Path $sweep 'unmarked'
        [void][IO.Directory]::CreateDirectory($unmarked)
        $linked = Join-Path $sweep 'linked'
        [void](New-Item -ItemType Junction -Path $linked -Target $outsideLinked)
        $later = New-PrivacyFixtureRoot $sweep
        # Observed first, and the junction under the parent removed as a link before any verdict: a failing case throws,
        # and the removal in the finally below must not meet a junction that a defect left dangling, whose error would
        # replace the verdict.
        $innerKept = [IO.File]::Exists((Join-Path $outsideInner $script:PrivacyFixtureMarker))
        $linkedKept = [IO.File]::Exists((Join-Path $outsideLinked $script:PrivacyFixtureMarker))
        $staleLeft = [IO.Directory]::Exists($stale.Root)
        $liveKept = [IO.File]::Exists((Join-Path $live.Root $script:PrivacyFixtureMarker))
        $othersKept = [IO.Directory]::Exists($unmarked) -and [IO.Directory]::Exists($linked)
        Remove-PrivacyFixtureRoot $later
        Remove-PrivacyFixtureRoot $live
        if ([IO.Directory]::Exists($linked)) { [IO.Directory]::Delete($linked, $false) }
        if (-not $innerKept) {
            throw 'Public privacy fixture self-test failed: removing a finished run''s fixture went through a junction inside it.'
        }
        if (-not $linkedKept) {
            throw 'Public privacy fixture self-test failed: the sweep removed a fixture through a junction.'
        }
        if ($staleLeft) {
            throw 'Public privacy fixture self-test failed: a fixture root a finished run left was not removed.'
        }
        if (-not $liveKept) {
            throw 'Public privacy fixture self-test failed: the sweep removed the fixture of a run still holding it.'
        }
        if (-not $othersKept) {
            throw 'Public privacy fixture self-test failed: the sweep removed a folder without the fixture marker, or a junction.'
        }
        $count++
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
        # Two protected paths that differ only in Unicode normalization (N-bd-w11-tests-scripts-1-02), a composed and a
        # decomposed letter, are two names to git and two refusals; Sort-Object -Unique -CaseSensitive called them one.
        # Compared ordinally, since -ceq calls the two spellings equal too.
        $composedKey = 'keys/caf' + [char]0x00E9 + '.pem'
        $decomposedKey = 'keys/cafe' + [char]0x0301 + '.pem'
        $beforeForms = Step @('rev-parse', 'HEAD')
        foreach ($form in @($composedKey, $decomposedKey)) {
            [void](Step @('update-index', '--add', '--cacheinfo', ('100644,' + $variantBlob + ',' + $form)))
        }
        [void](Step ($identity + @('commit', '-q', '-m', 'add two keys whose names differ only in normalization')))
        $formRefused = Get-RangePrivacyFindings ($beforeForms + '..HEAD') $needles $synthetic
        $formPaths = (@($formRefused.Protected | ForEach-Object { $_.Path })) -join '|'
        if (@($formRefused.Protected).Count -ne 2 -or $formRefused.Commits -ne 0 -or
            -not [string]::Equals($formPaths, ($decomposedKey + '|' + $composedKey), [StringComparison]::Ordinal)) {
            throw 'Public privacy range self-test failed: two protected paths differing only in normalization were not both refused.'
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
        # cases and decomposed and two longer forms of it, a longer name ending in a letter outside the Basic
        # Multilingual Plane and a root before a symbol there, and the identifier inside a path of each protected shape the grep
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
            @('src/AccentRoot.txt', ('home=C:\Users\' + $accentName), 'clean'),
            @('src/AccentUpper.txt', ('home="C:\Users\L' + [char]0x00C9 + 'A"'), 'clean'),
            @('src/AccentLonger.txt', ('home=/c/Users/' + $accentName + 'x'), 'clean'),
            @('src/AccentLongerAccented.txt', ('home=C:\Users\' + $accentName + [char]0x00E9), 'clean'),
            @('src/AccentDecomposed.txt', ('home="C:\Users\Le' + [char]0x0301 + 'a"'), 'clean'),
            @('src/LongerSupplementary.txt', ('home=C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x10400)), 'clean'),
            @('src/RootBeforeSymbol.txt', ('home=C:\Users\ExampleProfile' + [char]::ConvertFromUtf32(0x1F600)), 'found'),
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
        # Two symbolic links (mode 120000) too (N-bd-w13-review-07): git grep --cached reads regular files only, so the
        # default mode passed a link whose target is a profile path, which -Range reads as an added line and refused. The
        # protected one is the WITNESS that a link's protected shape still keeps it unread when it is named by its stage,
        # where the grep's exclusions do not reach. Each is staged straight into the index, as this file system may refuse
        # to make a link, with core.symlinks off, so the working file holds the target's text as git writes it then.
        $indexLinks = @(
            @('src/ProfileLink', 'C:\Users\ExampleProfile\AppData', 'found'),
            @('certs/Link.PEM', 'owner=Contoso', 'protected')
        )
        [void](Step @('init', '-q') $indexRepo)
        [void](Step @('config', 'core.symlinks', 'false') $indexRepo)
        foreach ($entry in $indexFiles) {
            $file = Join-Path $indexRepo $entry[0]
            [void][IO.Directory]::CreateDirectory((Split-Path $file -Parent))
            [IO.File]::WriteAllText($file, $entry[1] + "`n")
            # -f: a global excludes file that ignores *.pem or .env must not shrink the fixture.
            [void](Step @('add', '-f', '--', $entry[0]) $indexRepo)
        }
        foreach ($entry in $indexLinks) {
            [IO.File]::WriteAllText((Join-Path $indexRepo $entry[0]), $entry[1])
            $linkBlob = Step @('hash-object', '-w', '--', $entry[0]) $indexRepo
            [void](Step @('update-index', '--add', '--cacheinfo', ('120000,' + $linkBlob + ',' + $entry[0])) $indexRepo)
        }
        $protectedFixture = @(@($indexFiles) + @($indexLinks) | Where-Object { $_[2] -ceq 'protected' } | ForEach-Object { $_[0] } | Sort-Object)
        $foundFixture = (@(@($indexFiles) + @($indexLinks) | Where-Object { $_[2] -ceq 'found' } | ForEach-Object { $_[0] } | Sort-Object)) -join '|'
        if ((Step @('ls-files', '-s', '--', 'src/ProfileLink') $indexRepo) -cnotlike '120000 *') {
            throw 'Synthetic repository step failed: the link the index case needs was not staged as a symbolic link.'
        }
        $blocked = Get-IndexPrivacyFindings $needles $indexRepo
        if ($blocked.Scanned -or (@($blocked.Protected | ForEach-Object { $_.Path } | Sort-Object) -join '|') -cne ($protectedFixture -join '|')) {
            throw 'Public privacy index self-test failed: the protected indexed paths were not all refused before the grep.'
        }
        $count++
        $grep = Get-IndexedPrivacyOffenders $needles $indexRepo
        if (-not (@($grep.Offenders) -ccontains 'src/ProfileLink')) {
            throw 'Public privacy index self-test failed: a symbolic link whose target is a profile path was not read.'
        }
        if (@($grep.Offenders) -ccontains 'certs/Link.PEM') {
            throw 'Public privacy index self-test failed: a symbolic link with a protected shape was read.'
        }
        if (-not $grep.Matched -or (@($grep.Offenders) -join '|') -cne $foundFixture) {
            throw ('Public privacy index self-test failed: git grep did not report exactly the identifier in another case and the profile paths and roots, nothing excluded, no longer name and no bare user name: ' +
                (@($grep.Offenders) -join ', '))
        }
        $count++
        # The non-ASCII user name in the index (Codex recheck 2026-11-02): git grep -i missed its upper-case root here.
        $accentGrep = Get-IndexedPrivacyOffenders $accentNeedles $indexRepo
        if ((@($accentGrep.Offenders) -join '|') -cne 'src/AccentDecomposed.txt|src/AccentRoot.txt|src/AccentUpper.txt') {
            throw ('Public privacy index self-test failed: the index mode did not report exactly the non-ASCII profile root in both cases and decomposed, and neither longer name: ' +
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
        # Two indexed paths that differ only in Unicode normalization (N-bd-w11-tests-scripts-1-02) are two offenders too,
        # staged the same way. The expected listing is ordered here by Array.Sort with the ordinal comparer, not by the
        # function under test, and compared ordinally.
        $composedNote = 'docs/Caf' + [char]0x00E9 + '.txt'
        $decomposedNote = 'docs/Cafe' + [char]0x0301 + '.txt'
        foreach ($form in @($composedNote, $decomposedNote)) {
            [void](Step @('update-index', '--add', '--cacheinfo', ('100644,' + $variantText + ',' + $form)) $indexRepo)
        }
        $formIndexed = [string[]](@($variantExpected.Split('|')) + @($composedNote, $decomposedNote))
        [Array]::Sort($formIndexed, [StringComparer]::Ordinal)
        $formExpected = $formIndexed -join '|'
        $formScan = Get-IndexPrivacyFindings $needles $indexRepo
        if (-not $formScan.Scanned -or @($formScan.Offenders).Count -ne $formIndexed.Count -or
            -not [string]::Equals((@($formScan.Offenders) -join '|'), $formExpected, [StringComparison]::Ordinal)) {
            throw 'Public privacy index self-test failed: two indexed paths differing only in normalization were not both reported.'
        }
        $count++
        # Both modes on the SAME content (Codex recheck 2026-11-02): once committed, the range mode must report exactly
        # the files the index mode reports, for both user names. log.showRoot is set so a global false cannot hide the
        # root commit's lines.
        [void](Step @('config', 'log.showRoot', 'true') $indexRepo)
        [void](Step ($identity + @('commit', '-q', '-m', 'commit the index fixture')) $indexRepo)
        # The listings are ordered by the ordinal comparer and compared ordinally, as the normalization pair above needs.
        foreach ($pair in @(@($needles, $formExpected), @($accentNeedles, 'src/AccentDecomposed.txt|src/AccentRoot.txt|src/AccentUpper.txt'))) {
            $indexSeen = (@((Get-IndexPrivacyFindings $pair[0] $indexRepo).Offenders)) -join '|'
            $rangeSeen = (@(Select-UniquePrivacyPath @((Get-RangePrivacyFindings 'HEAD' $pair[0] $indexRepo).Findings |
                ForEach-Object { $_.Path }))) -join '|'
            if (-not [string]::Equals($indexSeen, $pair[1], [StringComparison]::Ordinal) -or
                -not [string]::Equals($rangeSeen, $pair[1], [StringComparison]::Ordinal)) {
                throw ('Public privacy self-test failed: both modes did not report the same files on the same content. Index: ' +
                    $indexSeen + '; range: ' + $rangeSeen)
            }
            $count++
        }
        # The default mode during an unresolved merge (N-bd-w12-review-05). git grep --cached skips every stage of a
        # conflicted path, so these stages were never read and the check passed, and git ls-files names a path once per
        # stage, so the conflicted key was refused three times. Every file but resolved.txt changes on both sides of a
        # merge left unresolved: only theirs (stage 3) carries the identifier in theirs.txt, only ours (stage 2) in
        # ours.txt, only the merge base (stage 1) in base.txt, both sides do in both.txt and neither does in clean.txt.
        # Each stage alone carries it in one file (N-bd-w13-review-06): with theirs.txt and both.txt alone, a reader of
        # stage 3 only, or one that kept the stages by path so the last one listed replaced the others, passed every check
        # here. blob.bin's stage 3 carries it after a NUL byte, so git grep -I calls that stage binary, as it would the
        # same file resolved (the WITNESS that git still picks the text files), and site.pem has a protected shape.
        # resolved.txt changes on one side only, so it merges cleanly, at stage 0. link is a symbolic link whose target
        # changes on both sides and only theirs names the identifier (N-bd-w13-review-07): the index case above reads a
        # resolved link, so a mode that named a link by stage 0 only passed there. It is staged as that case stages its links.
        $conflictRepo = Join-Path $fixtureRoot 'conflict'
        [void][IO.Directory]::CreateDirectory($conflictRepo)
        $conflictFiles = @('theirs.txt', 'ours.txt', 'base.txt', 'both.txt', 'clean.txt', 'blob.bin', 'site.pem')
        $conflictSides = @(
            @('shared line', 'shared line', 'shared line for Contoso', 'shared line', 'shared line', "shared`0", 'shared line'),
            @('trunk wording', 'trunk wording for Contoso', 'trunk wording', 'trunk wording for CONTOSO', 'trunk wording', "trunk`0", 'trunk wording'),
            @('side wording for Contoso', 'side wording', 'side wording', 'side wording for Contoso', 'side wording', "side`0 for Contoso", 'side for Contoso'))
        $conflictLinks = @('shared target', 'trunk target', 'side target for Contoso')
        function Save-ConflictSide([int]$Side, [string]$Message) {
            for ($i = 0; $i -lt $conflictFiles.Count; $i++) {
                [IO.File]::WriteAllText((Join-Path $conflictRepo $conflictFiles[$i]), $conflictSides[$Side][$i] + "`n")
            }
            [void](Step (@('add', '-f', '--') + $conflictFiles + @('resolved.txt')) $conflictRepo)
            [IO.File]::WriteAllText((Join-Path $conflictRepo 'link'), $conflictLinks[$Side])
            $linkBlob = Step @('hash-object', '-w', '--', 'link') $conflictRepo
            [void](Step @('update-index', '--add', '--cacheinfo', ('120000,' + $linkBlob + ',link')) $conflictRepo)
            [void](Step ($identity + @('commit', '-q', '-m', $Message)) $conflictRepo)
        }
        [void](Step @('init', '-q') $conflictRepo)
        [void](Step @('config', 'core.symlinks', 'false') $conflictRepo)
        [IO.File]::WriteAllText((Join-Path $conflictRepo 'resolved.txt'), "shared line`n")
        Save-ConflictSide 0 'base'
        $conflictTrunk = Step @('symbolic-ref', '--short', 'HEAD') $conflictRepo
        [void](Step @('checkout', '-q', '-b', 'side') $conflictRepo)
        [IO.File]::WriteAllText((Join-Path $conflictRepo 'resolved.txt'), "shared line`nresolved for Contoso`n")
        Save-ConflictSide 2 'side edit'
        [void](Step @('checkout', '-q', $conflictTrunk) $conflictRepo)
        Save-ConflictSide 1 'trunk edit'
        if ((Invoke-IndexedGit ($identity + @('merge', '-q', '--no-edit', 'side')) $conflictRepo).ExitCode -eq 0 -or
            @((Step @('ls-files', '-u') $conflictRepo).Split("`n")).Count -ne 3 * ($conflictFiles.Count + 1) -or
            @((Step @('ls-files', '-u', '--', 'link') $conflictRepo).Split("`n") | Where-Object { $_ -clike '120000 *' }).Count -ne 3) {
            throw 'Synthetic repository step failed: the merge did not leave every conflicting file and the link unmerged at three stages.'
        }
        $conflictBlocked = Get-IndexPrivacyFindings $needles $conflictRepo
        if ($conflictBlocked.Scanned -or @($conflictBlocked.Protected).Count -ne 1 -or $conflictBlocked.Protected[0].Path -cne 'site.pem') {
            throw ('Public privacy index self-test failed: an unmerged protected path was not refused exactly once: ' + @($conflictBlocked.Protected).Count)
        }
        $count++
        $conflictGrep = Get-IndexedPrivacyOffenders $needles $conflictRepo
        $conflictSeen = (@($conflictGrep.Offenders)) -join '|'
        if (@($conflictGrep.Offenders) -ccontains 'blob.bin') {
            throw 'Public privacy index self-test failed: an unmerged stage git calls binary was read as text.'
        }
        if (@($conflictGrep.Offenders) -ccontains 'site.pem') {
            throw 'Public privacy index self-test failed: an unmerged stage of a protected path was read.'
        }
        if (-not (@($conflictGrep.Offenders) -ccontains 'ours.txt')) {
            throw 'Public privacy index self-test failed: an unmerged file whose identifier only ours (stage 2) carries was not reported.'
        }
        if (-not (@($conflictGrep.Offenders) -ccontains 'base.txt')) {
            throw 'Public privacy index self-test failed: an unmerged file whose identifier only the merge base (stage 1) carries was not reported.'
        }
        if (-not (@($conflictGrep.Offenders) -ccontains 'link')) {
            throw 'Public privacy index self-test failed: an unmerged symbolic link whose target only theirs (stage 3) gives carries the identifier was not reported.'
        }
        if (-not $conflictGrep.Matched -or $conflictSeen -cne 'base.txt|both.txt|link|ours.txt|resolved.txt|theirs.txt') {
            throw ('Public privacy index self-test failed: the unmerged text files were not each reported once from their stages: ' + $conflictSeen)
        }
        $count++
        [void](Step @('rm', '-q', '--cached', '--', 'site.pem') $conflictRepo)
        $conflictScan = Get-IndexPrivacyFindings $needles $conflictRepo
        if (-not $conflictScan.Scanned -or ((@($conflictScan.Offenders)) -join '|') -cne 'base.txt|both.txt|link|ours.txt|resolved.txt|theirs.txt') {
            throw 'Public privacy index self-test failed: the default mode did not read the unmerged stages once nothing protected was indexed.'
        }
        $count++
    } finally {
        Remove-PrivacyFixtureRoot $fixture
    }
    # The real fixture went through that removal, and what this run made is gone (N-bd-w11-tests-scripts-1-03,
    # N-bd-w12-review-06): the root, the build\privacy-selftest parent once it is empty, whoever made it, and each folder
    # above it that the run made, unless another run's fixture is inside now. Read after the finally, so it sees what the
    # finally left. When the parent stays, the line after the summary says how much is inside it.
    if (-not $fixture.Removed) { throw 'Public privacy fixture self-test failed: the real fixture was not removed by Remove-PrivacyFixtureRoot.' }
    $leftBehind = @(@($fixture.Parent) + @($fixture.Created) | Where-Object { [IO.Directory]::Exists($_) -and @([IO.Directory]::GetFileSystemEntries($_)).Count -eq 0 })
    if ([IO.Directory]::Exists($fixture.Root) -or $leftBehind.Count -gt 0) {
        throw 'Public privacy fixture self-test failed: the run left its fixture root, or an emptied folder it owns, behind.'
    }
    Write-Host "Public privacy self-test passed: $count cases (path policy, matchers, range mode with merges and the default index mode on synthetic repositories, removed again)."
    if ([IO.Directory]::Exists($fixture.Parent)) {
        Write-Host ('Its build\privacy-selftest folder stays: it holds ' + @([IO.Directory]::GetFileSystemEntries($fixture.Parent)).Count +
            ' item(s) this run did not remove (a fixture another run still holds, anything without the fixture marker, or a fixture whose removal failed).')
    }
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
