#requires -Version 7
<#
.SYNOPSIS
    The scheduled re-pin check's steps, runnable here: fetch the newest vendor builds, check them offline, and draft
    one pull request per build that passes every check. It never merges and never applies a pin.
.DESCRIPTION
    Check phase (default): downloads the newest Claude Code CLI native package from npm (verifying the registry's
    published sha512 integrity), and the newest win32-x64 VSIX of the Claude Code and Codex VS Code extensions from
    the Visual Studio Marketplace, into -OutputDirectory\downloads; runs tools/agentbroker/Test-AgentBrokerRepin.ps1
    in package mode on each (signatures, markers and hashes are READ; no downloaded file is executed); writes
    results.json, summary.md (for the run summary) and, for each eligible build, the drafted entry and a pull request
    body. Every download happens inside this one invocation.

    Pull request phase (-OpenPullRequests): for each eligible build that has no open pull request yet (matched by its
    branch, agentbroker-repin/<build>), creates that branch from the current commit with the entry merged into
    tools/AgentBroker.Pins/agentbroker-pins.json, pushes it and opens a DRAFT pull request. -DryRun prints the plan and
    changes nothing. It never merges, approves or pushes to the base branch.

    The Codex desktop app is a Microsoft Store package that this check cannot fetch; it stays a manual re-pin with
    Test-AgentBrokerRepin.ps1 against the installed package.
.PARAMETER OutputDirectory
    A new or empty folder for downloads and results; the pull request phase reads the check phase's results from it.
.PARAMETER ClaudeVersion
    npm versions of @anthropic-ai/claude-code to check ('latest' is its latest dist-tag). Default: latest.
.PARAMETER ClaudeVsCodeVersion
    Marketplace versions of anthropic.claude-code to check ('latest' is the newest non-prerelease win32-x64). Default: latest.
.PARAMETER CodexVsCodeVersion
    Marketplace versions of openai.chatgpt to check ('latest' as above). Default: latest.
.PARAMETER OpenPullRequests
    Run the pull request phase from the results already in -OutputDirectory.
.PARAMETER DryRun
    With -OpenPullRequests: print the git and gh commands it would run; run none of them.
.PARAMETER BaseBranch
    The branch the pull requests target. Default: master.
.PARAMETER SelfTest
    Offline fixture checks of the selection, binding, de-duplication and pull request rules, and of the workflow file.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Invoke-AgentBrokerRepinCheck.ps1 -OutputDirectory $env:TEMP\repin
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Invoke-AgentBrokerRepinCheck.ps1 -OpenPullRequests -DryRun -OutputDirectory $env:TEMP\repin
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string[]]$ClaudeVersion = @('latest'),
    [string[]]$ClaudeVsCodeVersion = @('latest'),
    [string[]]$CodexVsCodeVersion = @('latest'),
    [switch]$OpenPullRequests,
    [switch]$DryRun,
    [string]$BaseBranch = 'master',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:Helper = Join-Path $PSScriptRoot 'Test-AgentBrokerRepin.ps1'
$script:PinDocumentRelative = 'tools/AgentBroker.Pins/agentbroker-pins.json'
$script:Workflow = Join-Path $script:RepoRoot '.github\workflows\agentbroker-repin.yml'
$script:Registry = 'https://registry.npmjs.org'
$script:Gallery = 'https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery'
$script:ClaudeWrapper = '@anthropic-ai/claude-code'
$script:ClaudeNative = '@anthropic-ai/claude-code-win32-x64'
$script:BranchPrefix = 'agentbroker-repin/'
$script:BotName = 'github-actions[bot]'
$script:BotEmail = '41898282+github-actions[bot]@users.noreply.github.com'

# ---- Selection rules (pure, fixture-tested) ----
function Test-DownloadUrl([string]$Url, [string[]]$Hosts) {
    # Only https, only the expected registry or marketplace hosts; a redirect elsewhere is refused by Invoke-WebRequest's
    # own host and the checks on the bytes, but the starting URL itself must already be one of these.
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https') { return $false }
    foreach ($allowed in $Hosts) {
        if ($uri.Host -ieq $allowed -or $uri.Host.EndsWith('.' + $allowed, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Select-MarketplaceVersion($Query, [string]$Requested) {
    # The newest (or the requested) non-prerelease win32-x64 version, with its VSIX URL.
    $extension = @(@($Query.results)[0].extensions)[0]
    if ($null -eq $extension) { throw 'The marketplace returned no such extension.' }
    $candidates = foreach ($entry in @($extension.versions)) {
        # A universal (platform-neutral) version has no targetPlatform member at all; it is never the win32-x64 build.
        if ($null -eq $entry.PSObject.Properties['targetPlatform'] -or [string]$entry.targetPlatform -cne 'win32-x64') { continue }
        $parsed = $null
        if (-not [Version]::TryParse([string]$entry.version, [ref]$parsed)) { continue }
        $properties = @(); if ($entry.PSObject.Properties['properties']) { $properties = @($entry.properties) }
        $prerelease = @($properties | Where-Object { $_.key -ceq 'Microsoft.VisualStudio.Code.PreRelease' -and [string]$_.value -ceq 'true' }).Count -gt 0
        if ($prerelease) { continue }
        $files = @(); if ($entry.PSObject.Properties['files']) { $files = @($entry.files) }
        $file = @($files | Where-Object { $_.assetType -ceq 'Microsoft.VisualStudio.Services.VSIXPackage' })[0]
        if ($null -eq $file) { continue }
        [pscustomobject]@{ Version = [string]$entry.version; Parsed = $parsed; Url = [string]$file.source }
    }
    $candidates = @($candidates | Sort-Object Parsed -Descending)
    if ($Requested -ceq 'latest') { $chosen = $candidates | Select-Object -First 1 }
    else { $chosen = @($candidates | Where-Object { $_.Version -ceq $Requested })[0] }
    if ($null -eq $chosen) { throw "No non-prerelease win32-x64 version '$Requested' is published." }
    return $chosen
}

function Assert-NativeBinding($Wrapper, $Native) {
    # The CLI users install is the wrapper; its postinstall copies the win32-x64 package's claude.exe. Check the build
    # the wrapper actually names, at the same version, and that npm publishes a sha512 integrity for it.
    $bound = $null
    if ($Wrapper.PSObject.Properties['optionalDependencies']) { $bound = $Wrapper.optionalDependencies.$($script:ClaudeNative) }
    if ([string]$bound -cne [string]$Wrapper.version) { throw "$($script:ClaudeWrapper) $($Wrapper.version) does not bind $($script:ClaudeNative) at its own version." }
    if ([string]$Native.name -cne $script:ClaudeNative -or [string]$Native.version -cne [string]$Wrapper.version) { throw 'The native package metadata is not the bound build.' }
    if ([string]$Native.dist.integrity -notmatch '^sha512-[A-Za-z0-9+/]{86}==$') { throw 'npm publishes no sha512 integrity for the native package.' }
    if (-not (Test-DownloadUrl ([string]$Native.dist.tarball) @('registry.npmjs.org'))) { throw 'The native tarball URL is not on registry.npmjs.org.' }
}

function Get-BuildId([string]$List, $Entry) {
    # The version and the first 12 hex digits of the native image's hash: one id, one branch, per distinct build.
    if ($List -ceq 'claudeCode') { return 'claude-code-' + [string]$Entry.version + '-' + ([string]$Entry.sha256).Substring(0, 12).ToLowerInvariant() }
    return 'codex-vscode-' + [string]$Entry.version + '-' + ([string]$Entry.nativeImageSha256).Substring(0, 12).ToLowerInvariant()
}

function Group-Drafts($Results) {
    # One pull request per BUILD, which for Claude is one image: an npm CLI and a VS Code extension that ship the
    # same claude.exe are one pin, and two pull requests adding it would make the second a repeated hash the loader
    # refuses. Results are kept in the order they were checked.
    $groups = [ordered]@{}
    foreach ($result in @($Results | Where-Object { $_.Eligible })) {
        $id = Get-BuildId $result.List $result.Entry
        if (-not $groups.Contains($id)) { $groups[$id] = New-Object 'Collections.Generic.List[object]' }
        $groups[$id].Add($result)
    }
    return $groups
}

function New-PullRequestBody([string]$Id, $Group, [string]$List, $Entry, [string[]]$Evidence) {
    $first = $Group[0]
    $lines = New-Object 'Collections.Generic.List[string]'
    $lines.Add('Drafted by the scheduled AgentBroker re-pin check (`.github/workflows/agentbroker-repin.yml`). It never merges and never applies a pin: merging this pull request IS the review, and a merged re-pin reaches users only with the next AgentBroker module publish.')
    $lines.Add('')
    $lines.Add('**Build**: ' + (($Group | ForEach-Object { $_.Name + ' ' + $_.Version + ' (' + $_.Package + ')' }) -join '; '))
    $lines.Add('')
    $lines.Add('**Entry** added to `' + $List + '` in `' + $script:PinDocumentRelative + '`:')
    $lines.Add('')
    $lines.Add('```json')
    $lines.Add([string]$first.DraftPin)
    $lines.Add('```')
    $lines.Add('')
    $lines.Add('Every offline check ran and passed: no FAIL and no UNPROVEN. A signature and matching markers never authorize a build by themselves, so before merging a person must establish these REVIEW items by re-reading the new build:')
    $lines.Add('')
    foreach ($check in @($first.Checks | Where-Object { $_.Status -eq 'REVIEW' })) { $lines.Add('- [ ] `' + $check.Name + '`: ' + $check.Reason) }
    $lines.Add('')
    $lines.Add('After the module is published, the owner''s live acceptance (LIVE, never inferred offline):')
    $lines.Add('')
    foreach ($check in @($first.Checks | Where-Object { $_.Status -eq 'LIVE' })) { $lines.Add('- [ ] `' + $check.Name + '`') }
    $lines.Add('')
    $lines.Add('GitHub does not start other workflows for a pull request opened with the workflow token, so the build does not run here until the pull request is closed and reopened, or a commit is pushed to it.')
    $lines.Add('')
    $lines.Add('<details><summary>Re-pin helper output</summary>')
    $lines.Add('')
    $lines.Add('```text')
    foreach ($line in $Evidence) { $lines.Add($line) }
    $lines.Add('```')
    $lines.Add('')
    $lines.Add('</details>')
    return (($lines -join "`n") + "`n")
}

function Get-PullRequestTitle($Group) {
    $names = ($Group | ForEach-Object { $_.Name } | Select-Object -Unique) -join ' and '
    return "AgentBroker re-pin: $names $($Group[0].Version) (draft for review)"
}

# ---- Workflow contract (the property the owner asked for, checked against the file itself) ----
function Test-WorkflowContract([string]$Text) {
    $failures = New-Object 'Collections.Generic.List[string]'
    $lines = $Text -split "`r?`n"
    $code = @($lines | ForEach-Object { ($_ -replace '\s+#.*$', '') } | Where-Object { $_ -notmatch '^\s*#' })
    $joined = $code -join "`n"
    # Top-level permissions: exactly contents: write and pull-requests: write, and no job may widen them. Every line
    # pattern uses [ \t], never \s, which in multiline mode would also cross a line break.
    $top = [regex]::Match($joined, '(?m)^permissions:[ \t]*\n((?:[ ]{2}\S.*\n?)+)')
    $granted = @()
    if ($top.Success) { $granted = @($top.Groups[1].Value -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() } | Sort-Object) }
    if (($granted -join ';') -cne 'contents: write;pull-requests: write') { $failures.Add('top-level permissions are not exactly contents: write and pull-requests: write') }
    if ([regex]::Matches($joined, '(?m)^[ \t]+permissions:').Count -gt 0) { $failures.Add('a job declares its own permissions') }
    foreach ($match in [regex]::Matches($joined, '(?m)^[ \t]*-?[ \t]*uses:[ \t]*(\S+)')) {
        if ($match.Groups[1].Value -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$') { $failures.Add('an action is not pinned by commit SHA: ' + $match.Groups[1].Value) }
    }
    if ($joined -notmatch '(?m)^[ \t]+schedule:' -or $joined -notmatch '(?m)^[ \t]+workflow_dispatch:') { $failures.Add('the workflow is not scheduled with a manual dispatch') }
    if ($joined -match 'pull_request_target') { $failures.Add('the workflow uses pull_request_target') }
    if ($joined -match '(?i)gh\s+pr\s+(merge|review)|--admin|auto-merge|enablePullRequestAutoMerge') { $failures.Add('the workflow can merge, approve or enable auto-merge') }
    if ($joined -notmatch '(?m)^[ \t]+runs-on:[ \t]*windows-latest[ \t]*$') { $failures.Add('the job does not run on windows-latest') }
    if ($joined -match '\$\{\{\s*github\.event\.(?!repository\.default_branch)') { $failures.Add('the workflow interpolates event data other than the default branch') }
    return , $failures.ToArray()
}

function Test-ScriptNeverMerges([string]$Text) {
    # This script is what the workflow runs; it may create and push a re-pin branch and open a DRAFT pull request,
    # nothing more. Comment lines are skipped, so documentation can name what is refused.
    $code = (@($Text -split "`r?`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n")
    $findings = New-Object 'Collections.Generic.List[string]'
    if ($code -match "(?i)'pr',\s*'(merge|review)'|gh\s+pr\s+(merge|review)") { $findings.Add('it can merge or approve a pull request') }
    if ($code -notmatch "'--draft'") { $findings.Add('it does not open the pull request as a draft') }
    # Assembled from parts, so this line itself is not a push line naming the base branch.
    $toBase = '(?im)' + 'pu' + 'sh' + '[^\n]*(refs/heads/(mas' + 'ter|main)\b|\$Base' + 'Branch)'
    if ($code -match $toBase) { $findings.Add('it can push to the base branch') }
    return , $findings.ToArray()
}

# ---- Check phase ----
function Initialize-OutputDirectory([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { throw '-OutputDirectory is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container) -or @(Get-ChildItem -LiteralPath $full -Force).Count -gt 0) { throw "The output folder $full must be new or empty." }
    }
    else { New-Item -ItemType Directory -Path $full | Out-Null }
    return $full
}

function Save-Download([string]$Url, [string[]]$Hosts, [string]$Path) {
    if (-not (Test-DownloadUrl $Url $Hosts)) { throw "Refused download URL: $Url" }
    Invoke-WebRequest -Uri $Url -OutFile $Path -MaximumRedirection 5 -UseBasicParsing | Out-Null
    return $Path
}

function Get-NpmDocument([string]$Package, [string]$Version) {
    $url = $script:Registry + '/' + $Package.Replace('/', '%2F') + '/' + [Uri]::EscapeDataString($Version)
    return Invoke-RestMethod -Uri $url -Headers @{ Accept = 'application/json' }
}

function Get-MarketplaceQuery([string]$ItemName) {
    # Flags: versions, files, version properties and asset URIs (1 + 2 + 16 + 128).
    $body = @{ filters = @(@{ criteria = @(@{ filterType = 7; value = $ItemName }); pageNumber = 1; pageSize = 1 }); flags = 147 } | ConvertTo-Json -Depth 6
    return Invoke-RestMethod -Uri $script:Gallery -Method Post -Body $body -ContentType 'application/json' -Headers @{ Accept = 'application/json;api-version=7.2-preview.1' }
}

function Invoke-Helper([string]$Package, [string]$Integrity, [string]$Out) {
    $arguments = @('-NoProfile', '-File', $script:Helper, '-Package', $Package, '-OutputDirectory', $Out)
    if ($Integrity) { $arguments += @('-ExpectedIntegrity', $Integrity) }
    $output = @(& pwsh @arguments 2>&1 | ForEach-Object { [string]$_ })
    $resultFile = Join-Path $Out 'result.json'
    if (-not (Test-Path -LiteralPath $resultFile)) {
        # A package the helper could not even read (a changed layout) is an ERROR for this build only: the other
        # builds are still checked and drafted, and the pull request phase fails the run afterwards.
        return [pscustomobject]@{ Package = (Split-Path $Package -Leaf); Name = (Split-Path $Package -Leaf); Version = ''; Verdict = 'ERROR'
            Eligible = $false; Blockers = @(); List = $null; Entry = $null; DraftPin = ''; Checks = @(); CheckDirectory = $Out; EntryFile = $null
            Error = ("the re-pin helper produced no result (exit $LASTEXITCODE): " + (($output | Select-Object -Last 3) -join ' | ')) }
    }
    $result = @(Get-Content -LiteralPath $resultFile -Raw -Encoding utf8 | ConvertFrom-Json)[0]
    $entry = $null; $list = $null
    $entryFile = Join-Path $Out 'entry.json'
    if (Test-Path -LiteralPath $entryFile) { $draft = Get-Content -LiteralPath $entryFile -Raw -Encoding utf8 | ConvertFrom-Json; $entry = $draft.entry; $list = [string]$draft.list }
    [pscustomobject]@{ Package = (Split-Path $Package -Leaf); Name = [string]$result.Name; Version = [string]$result.Version; Verdict = [string]$result.Verdict
        Eligible = [bool]$result.PullRequestEligible; Blockers = @($result.PullRequestBlockers); List = $list; Entry = $entry; DraftPin = [string]$result.DraftPin
        Checks = @($result.Checks); CheckDirectory = $Out; EntryFile = $(if ($entry) { $entryFile } else { $null }); Error = $null }
}

function Split-Versions([string[]]$Values) {
    # "-ClaudeVersion 2.1.293,2.1.310" through pwsh -File arrives as ONE string; accept either form.
    return @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Invoke-CheckPhase {
    $ClaudeVersion = Split-Versions $ClaudeVersion
    $ClaudeVsCodeVersion = Split-Versions $ClaudeVsCodeVersion
    $CodexVsCodeVersion = Split-Versions $CodexVsCodeVersion
    $out = Initialize-OutputDirectory $OutputDirectory
    $downloads = Join-Path $out 'downloads'; New-Item -ItemType Directory -Path $downloads | Out-Null
    $checks = Join-Path $out 'checks'; New-Item -ItemType Directory -Path $checks | Out-Null
    $results = New-Object 'Collections.Generic.List[object]'
    $sources = New-Object 'Collections.Generic.List[object]'

    # 'latest' can name a version also requested explicitly; each package version is fetched and checked once.
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($requested in $ClaudeVersion) {
        $wrapper = Get-NpmDocument $script:ClaudeWrapper $requested
        if (-not $seen.Add('npm|' + [string]$wrapper.version)) { continue }
        $native = Get-NpmDocument $script:ClaudeNative ([string]$wrapper.version)
        Assert-NativeBinding $wrapper $native
        $file = Save-Download ([string]$native.dist.tarball) @('registry.npmjs.org') (Join-Path $downloads ("claude-code-win32-x64-" + $native.version + '.tgz'))
        $sources.Add("npm $($script:ClaudeNative)@$($native.version) integrity $($native.dist.integrity)")
        $results.Add((Invoke-Helper $file ([string]$native.dist.integrity) (Join-Path $checks ('npm-claude-code-' + $native.version))))
    }
    foreach ($item in @(@{ Name = 'anthropic.claude-code'; Versions = $ClaudeVsCodeVersion }, @{ Name = 'openai.chatgpt'; Versions = $CodexVsCodeVersion })) {
        $query = Get-MarketplaceQuery $item.Name
        foreach ($requested in $item.Versions) {
            $chosen = Select-MarketplaceVersion $query $requested
            if (-not $seen.Add($item.Name + '|' + $chosen.Version)) { continue }
            $file = Save-Download $chosen.Url @('vsassets.io', 'visualstudio.com') (Join-Path $downloads ($item.Name + '-' + $chosen.Version + '@win32-x64.vsix'))
            $sources.Add("marketplace $($item.Name)@$($chosen.Version) win32-x64")
            $results.Add((Invoke-Helper $file '' (Join-Path $checks ($item.Name + '-' + $chosen.Version))))
        }
    }

    $groups = Group-Drafts $results
    $planned = New-Object 'Collections.Generic.List[object]'
    foreach ($id in $groups.Keys) {
        $group = $groups[$id].ToArray()
        $evidence = @()
        foreach ($member in $group) { $evidence += @(Get-Content -LiteralPath (Join-Path $member.CheckDirectory 'evidence.txt') -Encoding utf8) }
        $body = Join-Path $out ("pr-$id.md")
        Set-Content -LiteralPath $body -Value (New-PullRequestBody $id $group $group[0].List $group[0].Entry $evidence) -Encoding utf8NoBOM -NoNewline
        $planned.Add([pscustomobject]@{ Id = $id; Branch = $script:BranchPrefix + $id; Title = (Get-PullRequestTitle $group); List = $group[0].List
            EntryFile = $group[0].EntryFile; BodyFile = $body; Packages = @($group | ForEach-Object { $_.Package }) })
    }
    $document = [ordered]@{ schema = 1; sources = $sources.ToArray()
        results = @($results | ForEach-Object { [ordered]@{ package = $_.Package; name = $_.Name; version = $_.Version; verdict = $_.Verdict
            eligible = $_.Eligible; blockers = $_.Blockers; list = $_.List; entryFile = $_.EntryFile; error = $_.Error } })
        pullRequests = $planned.ToArray() }
    Set-Content -LiteralPath (Join-Path $out 'results.json') -Value ($document | ConvertTo-Json -Depth 8) -Encoding utf8NoBOM

    $summary = New-Object 'Collections.Generic.List[string]'
    $summary.Add('## AgentBroker re-pin check')
    $summary.Add('')
    $summary.Add('| Build | Verdict | Pull request |')
    $summary.Add('|---|---|---|')
    foreach ($result in $results) {
        $pr = if ($result.Eligible) { 'eligible: ' + $script:BranchPrefix + (Get-BuildId $result.List $result.Entry) }
              elseif ($result.Verdict -eq 'DRAFT FOR REVIEW') { 'not eligible; UNPROVEN: ' + (@($result.Blockers) -join ', ') }
              elseif ($result.Verdict -eq 'ERROR') { 'none; ' + $result.Error }
              elseif ($result.Verdict -eq 'ALREADY PINNED') { 'none; already pinned' }
              else { 'none; failed: ' + (@($result.Checks | Where-Object { $_.Status -eq 'FAIL' } | ForEach-Object { $_.Name }) -join ', ') }
        $summary.Add("| $($result.Name) $($result.Version) ($($result.Package)) | $($result.Verdict) | $pr |")
    }
    $summary.Add('')
    $summary.Add('No pin was applied. Builds that are NOT READY or have an UNPROVEN check are reported here only. The Codex desktop app is a Store package this check cannot fetch: re-pin it by hand with `tools/agentbroker/Test-AgentBrokerRepin.ps1`.')
    Set-Content -LiteralPath (Join-Path $out 'summary.md') -Value (($summary -join "`n") + "`n") -Encoding utf8NoBOM -NoNewline
    $summary | ForEach-Object { Write-Output $_ }
}

# ---- Pull request phase ----
function Invoke-Native([string]$File, [string[]]$Arguments) {
    # The host stream, so a caller discarding the command's output still shows the plan.
    if ($DryRun) { Write-Host ('DRY-RUN: ' + $File + ' ' + (($Arguments | ForEach-Object { if ($_ -match '\s') { "'" + $_ + "'" } else { $_ } }) -join ' ')); return @() }
    $output = @(& $File @Arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw ("$File $($Arguments[0]) failed (exit $LASTEXITCODE): " + ($output -join ' | ')) }
    return $output
}

function Invoke-PullRequestPhase {
    $out = [IO.Path]::GetFullPath($OutputDirectory)
    $document = Get-Content -LiteralPath (Join-Path $out 'results.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $planned = @($document.pullRequests)
    $errors = @($document.results | Where-Object { $_.verdict -eq 'ERROR' })
    if ($planned.Count -eq 0) { Write-Output 'No build is eligible for a pull request; nothing to open.' }
    else { Open-PlannedPullRequests $out $planned }
    # Builds the helper could not read at all fail the run after the others are drafted, so the owner is told.
    if ($errors.Count -gt 0) { throw ('Could not check: ' + (($errors | ForEach-Object { $_.package + ' (' + $_.error + ')' }) -join '; ')) }
}

function Open-PlannedPullRequests([string]$out, $planned) {
    $open = @()
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -ne $gh -and ($env:GH_TOKEN -or -not $DryRun)) {
        $open = @((& gh pr list --state open --limit 200 --json 'number,headRefName' | ConvertFrom-Json) | ForEach-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw 'gh pr list failed; no pull request was opened.' }
    }
    elseif ($DryRun) { Write-Output 'DRY-RUN: gh is unavailable or unauthenticated here, so no open pull request is known.' }
    $base = (& git -C $script:RepoRoot rev-parse HEAD).Trim()
    if (-not $DryRun) { Invoke-Native 'gh' @('auth', 'setup-git') | Out-Null }
    foreach ($pr in $planned) {
        $existing = @($open | Where-Object { [string]$_.headRefName -ceq [string]$pr.Branch })
        if ($existing.Count -gt 0) { Write-Output ("Skipped $($pr.Id): pull request #" + $existing[0].number + ' is already open for this build.'); continue }
        $merge = Join-Path $out ('merge-' + $pr.Id)
        & pwsh -NoProfile -File $script:Helper -MergeEntry $pr.EntryFile -OutputDirectory $merge | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The entry for $($pr.Id) could not be merged into the pin document." }
        $target = Join-Path $script:RepoRoot $script:PinDocumentRelative
        Invoke-Native 'git' @('-C', $script:RepoRoot, 'switch', '--quiet', '--force-create', [string]$pr.Branch, $base) | Out-Null
        if ($DryRun) { Write-Output ("DRY-RUN: copy " + (Join-Path $merge 'agentbroker-pins.json') + " over $target") }
        else { Copy-Item -LiteralPath (Join-Path $merge 'agentbroker-pins.json') -Destination $target -Force }
        Invoke-Native 'git' @('-C', $script:RepoRoot, 'add', '--', $script:PinDocumentRelative) | Out-Null
        Invoke-Native 'git' @('-C', $script:RepoRoot, '-c', "user.name=$($script:BotName)", '-c', "user.email=$($script:BotEmail)", 'commit', '--quiet',
            '-m', [string]$pr.Title, '-m', 'Drafted by the scheduled AgentBroker re-pin check; see the pull request for the evidence and the review checklist.') | Out-Null
        Invoke-Native 'git' @('-C', $script:RepoRoot, 'push', '--force', 'origin', ('HEAD:refs/heads/' + [string]$pr.Branch)) | Out-Null
        Invoke-Native 'gh' @('pr', 'create', '--draft', '--base', $BaseBranch, '--head', [string]$pr.Branch, '--title', [string]$pr.Title, '--body-file', [string]$pr.BodyFile) |
            ForEach-Object { Write-Output $_ }
        Invoke-Native 'git' @('-C', $script:RepoRoot, 'switch', '--quiet', '--detach', $base) | Out-Null
    }
}

# ---- Self-test ----
function Invoke-SelfTest {
    $script:checks = 0
    function Assert([bool]$Condition, [string]$Label) { if (-not $Condition) { throw "FAIL: $Label" }; $script:checks++; Write-Output "PASS: $Label" }

    Assert ((Test-DownloadUrl 'https://registry.npmjs.org/@anthropic-ai/x/-/x-1.tgz' @('registry.npmjs.org')) -and
        -not (Test-DownloadUrl 'http://registry.npmjs.org/x.tgz' @('registry.npmjs.org')) -and
        -not (Test-DownloadUrl 'https://registry.npmjs.org.evil.example/x.tgz' @('registry.npmjs.org')) -and
        -not (Test-DownloadUrl 'https://evilregistry.npmjs.org.example/x' @('registry.npmjs.org')) -and
        -not (Test-DownloadUrl 'https://evilregistry.npmjs.org/x.tgz' @('registry.npmjs.org')) -and
        (Test-DownloadUrl 'https://ms-vscode.gallery.vsassets.io/_apis/x' @('vsassets.io'))) 'downloads start only from https on the registry or marketplace hosts; WITNESS: those hosts pass'

    function Version([string]$Number, [string]$Platform, [bool]$Pre = $false) {
        $properties = @(); if ($Pre) { $properties = @([pscustomobject]@{ key = 'Microsoft.VisualStudio.Code.PreRelease'; value = 'true' }) }
        [pscustomobject]@{ version = $Number; targetPlatform = $Platform; properties = $properties
            files = @([pscustomobject]@{ assetType = 'Microsoft.VisualStudio.Services.VSIXPackage'; source = "https://x.gallery.vsassets.io/$Number-$Platform.vsix" }) }
    }
    $universal = [pscustomobject]@{ version = '9.9.9'; files = @() }
    $query = [pscustomobject]@{ results = @([pscustomobject]@{ extensions = @([pscustomobject]@{ versions = @(
                (Version '2.1.312' 'win32-x64' $true), (Version '2.1.311' 'darwin-arm64'), $universal, (Version '2.1.311' 'win32-x64'), (Version '2.1.310' 'win32-x64'), (Version '2.1.9' 'win32-x64')) }) }) }
    $latest = Select-MarketplaceVersion $query 'latest'
    Assert ($latest.Version -ceq '2.1.311' -and $latest.Url -like '*2.1.311-win32-x64.vsix') 'latest is the newest non-prerelease win32-x64 build, compared as a version'
    Assert ((Select-MarketplaceVersion $query '2.1.310').Version -ceq '2.1.310') 'a requested version selects exactly that win32-x64 build'
    $refused = $false; try { $null = Select-MarketplaceVersion $query '2.1.312' } catch { $refused = $true }
    Assert $refused 'a prerelease-only version is never selected'

    $wrapper = [pscustomobject]@{ version = '2.1.311'; optionalDependencies = [pscustomobject]@{ '@anthropic-ai/claude-code-win32-x64' = '2.1.311' } }
    $native = [pscustomobject]@{ name = '@anthropic-ai/claude-code-win32-x64'; version = '2.1.311'
        dist = [pscustomobject]@{ integrity = 'sha512-' + [Convert]::ToBase64String((New-Object byte[] 64)); tarball = 'https://registry.npmjs.org/@anthropic-ai/claude-code-win32-x64/-/claude-code-win32-x64-2.1.311.tgz' } }
    Assert ($null -eq (Assert-NativeBinding $wrapper $native)) 'WITNESS: a wrapper binding its own version of the native package with a sha512 integrity is accepted'
    foreach ($case in @(
            @{ Label = 'a wrapper binding another native version'; Wrapper = [pscustomobject]@{ version = '2.1.311'; optionalDependencies = [pscustomobject]@{ '@anthropic-ai/claude-code-win32-x64' = '2.1.310' } }; Native = $native },
            @{ Label = 'a native package without sha512 integrity'; Wrapper = $wrapper; Native = [pscustomobject]@{ name = $native.name; version = '2.1.311'; dist = [pscustomobject]@{ integrity = 'sha1-abc'; tarball = $native.dist.tarball } } },
            @{ Label = 'a tarball off the registry host'; Wrapper = $wrapper; Native = [pscustomobject]@{ name = $native.name; version = '2.1.311'; dist = [pscustomobject]@{ integrity = $native.dist.integrity; tarball = 'https://example.com/x.tgz' } } })) {
        $threw = $false; try { Assert-NativeBinding $case.Wrapper $case.Native } catch { $threw = $true }
        Assert $threw ('the npm binding refuses ' + $case.Label)
    }

    $hex = 'F9A3920634AE3B961EE92C2EB58D0051F5F8852B120983D0A4F57B6A5EFEBA61'
    function Draft([string]$Name, [string]$Package, [string]$Hash, [bool]$Eligible = $true) {
        [pscustomobject]@{ Package = $Package; Name = $Name; Version = '2.1.311'; Verdict = 'DRAFT FOR REVIEW'; Eligible = $Eligible; Blockers = @()
            List = 'claudeCode'; Entry = [pscustomobject]@{ version = '2.1.311'; sha256 = $Hash }; DraftPin = '{ "version": "2.1.311", "sha256": "' + $Hash + '" }'
            Checks = @([pscustomobject]@{ Name = 'source.claude.peer-token-classification'; Status = 'REVIEW'; Reason = 'needs a person' },
                [pscustomobject]@{ Name = 'live.busy-ordering'; Status = 'LIVE'; Reason = 'after publish' }); CheckDirectory = ''; EntryFile = 'entry.json' }
    }
    $same = Group-Drafts @((Draft 'Claude Code CLI' 'a.tgz' $hex), (Draft 'Claude Code VS Code' 'b.vsix' $hex), (Draft 'Claude Code VS Code' 'c.vsix' ('0' * 64) $false))
    Assert ($same.Count -eq 1 -and @($same.Values)[0].Count -eq 2 -and @($same.Keys)[0] -ceq 'claude-code-2.1.311-f9a3920634ae') 'an npm CLI and a VS Code extension shipping one image are ONE pull request; an ineligible draft opens none'
    $two = Group-Drafts @((Draft 'Claude Code CLI' 'a.tgz' $hex), (Draft 'Claude Code VS Code' 'b.vsix' ('1' * 64)))
    Assert ($two.Count -eq 2) 'two different images of one version are two builds, two pull requests'
    $group = @($same.Values)[0].ToArray()
    $body = New-PullRequestBody 'claude-code-2.1.311-f9a3920634ae' $group 'claudeCode' $group[0].Entry @('== evidence line')
    Assert ($body.Contains('never merges') -and $body.Contains('- [ ] `source.claude.peer-token-classification`') -and $body.Contains('- [ ] `live.busy-ordering`') -and
        $body.Contains($hex) -and $body.Contains('== evidence line') -and (Get-PullRequestTitle $group) -ceq 'AgentBroker re-pin: Claude Code CLI and Claude Code VS Code 2.1.311 (draft for review)') 'the pull request carries the entry, the REVIEW and LIVE checklists and the helper output'

    $workflow = Get-Content -LiteralPath $script:Workflow -Raw -Encoding utf8
    $violations = Test-WorkflowContract $workflow
    Assert ($violations.Count -eq 0) ('the workflow file keeps its contract: ' + ($violations -join '; '))
    foreach ($mutant in @(
            @{ Label = 'a widened permission'; Text = $workflow.Replace("  pull-requests: write`n", "  pull-requests: write`n  actions: write`n") },
            @{ Label = 'an action pinned by tag'; Text = [regex]::Replace($workflow, '@[0-9a-f]{40}', '@v7', 1) },
            # The refused texts are assembled from parts, so this file's own contract check does not match them.
            @{ Label = 'a merge step'; Text = $workflow + "      - run: gh pr " + "merge --squash`n" },
            @{ Label = 'a job-level permission'; Text = $workflow.Replace("    runs-on: windows-latest`n", "    runs-on: windows-latest`n    permissions:`n      contents: write`n") })) {
        Assert ((Test-WorkflowContract $mutant.Text).Count -gt 0) ('the workflow contract refuses ' + $mutant.Label)
    }
    $self = Get-Content -LiteralPath $PSCommandPath -Raw -Encoding utf8
    $selfFindings = Test-ScriptNeverMerges $self
    Assert ($selfFindings.Count -eq 0) ('this script only drafts: ' + ($selfFindings -join '; '))
    $mergeCall = "`nInvoke-Native 'gh' @('pr', '" + "merge', '1')`n"
    $basePush = "`nInvoke-Native 'git' @('pu" + "sh', 'origin', 'HEAD:refs/heads/mas" + "ter')`n"
    Assert ((Test-ScriptNeverMerges ($self + $mergeCall)).Count -gt 0 -and (Test-ScriptNeverMerges ($self + $basePush)).Count -gt 0 -and
        (Test-ScriptNeverMerges ($self.Replace("'--draft'", "'--fill'"))).Count -gt 0) 'the script contract refuses a merge call, a push to the base branch and a non-draft pull request; WITNESS for the line above'
    Write-Output ("PASS: {0} re-pin check fixture checks" -f $script:checks)
}

if ($SelfTest) {
    try { Invoke-SelfTest; exit 0 }
    catch { Write-Output $_.Exception.Message; Write-Output $_.ScriptStackTrace; Write-Output 'RESULT=FAIL'; exit 1 }
}
if ($OpenPullRequests) { Invoke-PullRequestPhase }
else { Invoke-CheckPhase }
exit 0
