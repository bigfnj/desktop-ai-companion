#requires -Version 7
<#
.SYNOPSIS
    Check indexed text, or a commit range, for workstation-specific identifiers before a public push.
.DESCRIPTION
    Refuses protected paths before reading any contents. Git grep also receives explicit
    case-insensitive exclusions. Only offending file paths, commits and counts are printed,
    never the matched text.

    Identifiers come from the run, never from this file: -Identifier and -EmployerName, plus
    the business OneDrive folder and the employer it names when OneDriveCommercial is set.
    OneDrive is optional once an identifier is given. The user name is matched only as a
    profile path (\Users\<name>\, /Users/<name>/ and the doubled-backslash form of the first),
    because a common profile name such as a dictionary word matches everywhere as a bare word.

    -Range scans the commits of a revision range instead of the index: every added line of
    every commit (so a value added and later removed is still found), each commit message,
    and the author and committer lines. A merge is diffed against its first parent, so a line
    written while resolving a conflict, and a path only the merge adds, are read too.
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
    # Fixed strings, all compared case-insensitively. The user name only ever as a profile path.
    $needles = New-Object 'Collections.Generic.List[string]'
    foreach ($value in $Identifiers) { if (-not $needles.Contains($value)) { $needles.Add($value) } }
    if (-not [string]::IsNullOrEmpty($UserName)) {
        $needles.Add('\Users\' + $UserName + '\')
        $needles.Add('/Users/' + $UserName + '/')
        $needles.Add('\\Users\\' + $UserName + '\\')
    }
    return ,$needles.ToArray()
}

function Test-PrivacyText([string]$Text, [string[]]$Needles) {
    foreach ($needle in $Needles) {
        if ($Text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
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

function Get-RangePrivacyFindings([string]$Range, [string[]]$Needles, [string]$Repository = $repoRoot) {
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
    $names = Invoke-IndexedGit @('log', '--no-color', '--diff-merges=first-parent', '--format=', '--name-only', '-z', '--end-of-options', $Range, '--') $Repository
    if ($names.ExitCode -ne 0) { throw 'Could not list the paths the range changes. No commit contents were scanned.' }
    # -CaseSensitive (N-audit-infra-01): -Unique alone compares ignoring case, so two paths that differ only in case,
    # two files to git and to any case-sensitive checkout, were listed and counted as one.
    $protected = @($names.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim("`n") } |
        Where-Object { $_.Length -gt 0 -and (Get-ProtectedPattern $_) } | Sort-Object -Unique -CaseSensitive |
        ForEach-Object { [pscustomobject]@{ Path = $_; Pattern = (Get-ProtectedPattern $_) } })
    $findings = New-Object 'Collections.Generic.List[object]'
    if ($protected.Count -gt 0) { return [pscustomobject]@{ Protected = $protected; Findings = @(); Commits = 0 } }
    $log = Invoke-IndexedGit @('log', '--no-color', '--no-ext-diff', '--no-textconv', '-p', '--diff-merges=first-parent',
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
        # Added lines only, inside hunks, so a "+++ b/path" header is never read as content.
        $path = ''; $inHunk = $false; $counts = [ordered]@{}
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

# The default mode's matcher is git grep, not Test-PrivacyText: case-insensitive fixed strings, names only,
# NUL-separated, with every protected shape excluded again as a second line behind the refusal above.
function Get-IndexedPrivacyOffenders([string[]]$Needles, [string]$Repository = $repoRoot) {
    $arguments = @('grep', '--cached', '--no-textconv', '-n', '-i', '-I', '-l', '-F', '-z')
    foreach ($needle in $Needles) { $arguments += @('-e', $needle) }
    $arguments += @('--', '.')
    foreach ($glob in @('*.env', '.env.*', '.npmrc', '*.pem', '*.key', '*.p12', '*.pfx',
                         '*.keystore', 'privatekey*', '.git/credentials')) {
        $arguments += ":(top,glob,icase,exclude)**/$glob"
        $arguments += ":(top,glob,icase,exclude)**/$glob/**"
    }
    $result = Invoke-IndexedGit $arguments $Repository
    if ($result.ExitCode -gt 1) { throw 'Indexed git grep failed. No matching contents were printed.' }
    # -CaseSensitive, as in the range mode (N-audit-infra-01): two indexed paths differing only in case are two offenders.
    return [pscustomobject]@{
        Matched = ($result.ExitCode -eq 0)
        Offenders = @($result.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique -CaseSensitive)
    }
}

function Get-IndexPrivacyFindings([string[]]$Needles, [string]$Repository = $repoRoot) {
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
        @('Run as ExampleProfile, then open the ExampleProfile panel.', $false, 'the bare user name'),
        @('C:\Users\ExampleProfiles\file.txt', $false, 'a longer name sharing the prefix'),
        @('Built for CONTOSO by its team', $true, 'an explicit identifier, any case'),
        @('nothing here identifies anyone', $false, 'unrelated text')
    )) {
        if ((Test-PrivacyText $case[0] $needles) -ne $case[1]) { throw ('Public privacy matcher self-test failed: ' + $case[2] + '.') }
        $count++
    }
    # Range mode, then the default index mode, each on a synthetic repository this self-test creates and
    # removes under build\.
    $fixtureRoot = Join-Path $repoRoot ('build\privacy-selftest\' + [Guid]::NewGuid().ToString('N'))
    $synthetic = Join-Path $fixtureRoot 'range'
    $indexRepo = Join-Path $fixtureRoot 'index'
    [void][IO.Directory]::CreateDirectory($synthetic)
    [void][IO.Directory]::CreateDirectory($indexRepo)
    try {
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
        # The DEFAULT mode (N-audit-20261029-15). Its matcher is git grep, not Test-PrivacyText, so nothing
        # above vouches for it. This index holds the identifier in another case (found only with -i), a
        # profile path, a bare user name and clean text, and the identifier inside a path of each protected
        # shape the grep excludes, in mixed case (icase) and once as a directory (the /** form). The policy
        # refuses those paths before the grep runs, so the grep is also driven directly to prove its own
        # exclusions. .git/credentials has no case: git refuses to index any path with a .git component.
        $indexFiles = @(
            @('docs/Notes.txt', 'Built for CONTOSO by its team', 'found'),
            @('src/Cache.txt', 'cache at /c/Users/ExampleProfile/source', 'found'),
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
            throw ('Public privacy index self-test failed: git grep did not report exactly the identifier in another case and the profile path, nothing excluded and no bare user name: ' +
                (@($grep.Offenders) -join ', '))
        }
        $count++
        [void](Step (@('rm', '-q', '--cached', '--') + $protectedFixture) $indexRepo)
        $scanned = Get-IndexPrivacyFindings $needles $indexRepo
        if (-not $scanned.Scanned -or @($scanned.Protected).Count -ne 0 -or -not $scanned.Matched -or (@($scanned.Offenders) -join '|') -cne $foundFixture) {
            throw 'Public privacy index self-test failed: the default mode did not grep, and report the same two files, once nothing protected was indexed.'
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
        if (-not $variantScan.Scanned -or @($variantScan.Offenders).Count -ne 3 -or $variantSeen -cne $variantExpected) {
            throw ('Public privacy index self-test failed: two indexed paths differing only in case were not both reported: ' + $variantSeen)
        }
        $count++
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
