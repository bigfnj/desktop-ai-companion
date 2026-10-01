[CmdletBinding()]
param()
# No parameters. This script performs only source-text invariant checks (it reads .cs files, no assembly
# load); the reflection/runtime half is the app's --hardening-selftest flag, driven by tests\Invoke-SelfTests.ps1.
# An -ExecutablePath parameter sat here "for CI compatibility" long after every caller (run-gate.ps1, build.yml,
# two mutation harnesses) had stopped passing one (F408).

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# WINDOWS POWERSHELL MUST NOT AUTOLOAD PWSH'S MODULES. A powershell.exe descended from a pwsh 7 process
# inherits pwsh's PSModulePath with the PowerShell 7 module folders FIRST, and the 5.1 engine then autoloads
# Get-FileHash from the 7-only Microsoft.PowerShell.Utility manifest it cannot run: this script died with
# `The term 'Get-FileHash' is not recognized` inside the gate when the gate was launched through a shell
# pwsh had started (2026-09-30; the N-fortunes-01 trap, which the mutation harnesses already fence off for
# their powershell.exe children). Under the Desktop edition the process keeps only the WindowsPowerShell
# entries it inherited and is guaranteed the two system defaults; under pwsh this does nothing. The same
# block sits at the top of tests/run-gate.ps1, which runs this file in-process.
#
# AND PUT BACK ON EVERY EXIT (RA-358). `$env:` is the process environment, so a Windows PowerShell console
# that ran this script in-process kept the stripped path -- a developer's own module folder gone -- for the
# rest of its session. This file signals failure by throw and has no try/finally around its 3,000 lines,
# so the restore is a script-scope `trap`, which runs on the terminating error an Assert-True throw is and
# then rethrows it (`break`), plus the ordinary restore on the last line for the passing run. Measured
# under 5.1.26100 and 7.6.5 with a scratch child script: the caller's `catch` still receives the
# assertion's own message, and the variable reads its original value after both the passing and the
# failing run. Inside the gate this restores the gate's already-stripped value, so the gate's later steps
# keep the guard until the gate's own finally puts the caller's path back.
$originalModulePath = $env:PSModulePath
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $windowsModulePaths = @(($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -match '(?i)windowspowershell' })
    foreach ($defaultModulePath in @((Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
                                     (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'))) {
        if (-not @($windowsModulePaths | Where-Object { $_ -ieq $defaultModulePath }).Count) {
            $windowsModulePaths += $defaultModulePath
        }
    }
    $env:PSModulePath = ($windowsModulePaths -join ';')
}
trap { $env:PSModulePath = $originalModulePath; break }

function Assert-True {
    param([bool] $Condition, [string] $Name)
    if (-not $Condition) { throw "$Name failed." }
    Write-Host "PASS: $Name"
}

# DEFINED HERE, above every check, because PowerShell only makes a function callable
# BELOW its definition. This sat further down the file and the first check needing it
# then died with CommandNotFoundException -- which reads exactly like a broken check.
#
# STRING-AWARE, since 2026-09-30. This was `-replace '//.*$'` per line, which also stripped from the `//`
# of every URL and of every string literal containing one -- 4 of the 17 files it is applied to carry
# `://` today (AboutWindow.cs, WhisperInstaller.cs, AgentFlowModule.cs, the .wxs's xmlns lines) -- so a
# token an assertion needed after a URL on the same line vanished (a false red) and a forbidden token
# after one was hidden (a false green). No assertion reads such a line today; every new call site
# re-rolled that die (F409). The prefix before the `//` is now matched as a sequence of ordinary
# characters, complete double-quoted strings (with backslash escapes), char literals and lone slashes,
# so a `//` inside a string is not a comment. Fail-safe by construction: a line whose quotes do not
# balance (the body of a multi-line verbatim string, say) matches nothing and is left whole, which
# keeps text rather than cutting it. Per line, as before.
function Remove-LineComments {
    param([string] $Text)
    return ($Text -replace '(?m)^((?:[^"''/\n]|"(?:[^"\\\n]|\\.)*"|''(?:[^''\\\n]|\\.)*''|/(?!/))*)//.*$', '$1')
}

# Every object-initialiser body that follows `new ProcessStartInfo`, sliced on BRACE BALANCE.
#
# The slicer this replaced was the regex `new ProcessStartInfo(.*?)\}\s*;`. It was right for every
# site in the tree -- all of them close with `};` -- and wrong for the shape
# `Process.Start(new ProcessStartInfo { ... }))`, which closes with `}))`: the lazy match then ran on
# to the next `};` anywhere later in the file and judged that span instead, so a later, pinned
# initialiser's `StandardOutputEncoding =` would have cleared an unpinned site and its redirects would
# have kept the declared-vs-covered count square (F412). Counting braces from the initialiser's `{` to
# its match ends each slice where the initialiser ends, whatever follows it. Constructor arguments
# before the brace (`new ProcessStartInfo("cmd.exe", "/c") { ... }`) are skipped over; a bare
# `new ProcessStartInfo(...)` with no initialiser yields no slice, and its later property assignments
# then surface in the declared-vs-covered count exactly as before. Braces inside string literals are
# not tracked: no initialiser in the repo carries one, and the WITNESS beside the redirect check is
# where a slicer regression shows first. Takes COMMENT-STRIPPED code.
function Get-ProcessStartInfoInitialisers {
    param([string] $Code)
    $slices = @()
    $from = 0
    while ($from -lt $Code.Length) {
        $m = [regex]::Match($Code.Substring($from), 'new\s+(?:[\w.]+\.)?ProcessStartInfo\b')
        if (-not $m.Success) { break }
        $pos = $from + $m.Index + $m.Length
        while ($pos -lt $Code.Length -and [char]::IsWhiteSpace($Code[$pos])) { $pos++ }
        if ($pos -lt $Code.Length -and $Code[$pos] -eq '(') {
            $parens = 0
            do {
                if ($Code[$pos] -eq '(') { $parens++ } elseif ($Code[$pos] -eq ')') { $parens-- }
                $pos++
            } while ($pos -lt $Code.Length -and $parens -gt 0)
            while ($pos -lt $Code.Length -and [char]::IsWhiteSpace($Code[$pos])) { $pos++ }
        }
        if ($pos -ge $Code.Length -or $Code[$pos] -ne '{') { $from = $from + $m.Index + $m.Length; continue }
        $open = $pos
        $braces = 0
        do {
            if ($Code[$pos] -eq '{') { $braces++ } elseif ($Code[$pos] -eq '}') { $braces-- }
            $pos++
        } while ($pos -lt $Code.Length -and $braces -gt 0)
        $slices += $Code.Substring($open, $pos - $open)
        $from = $pos
    }
    # Enumerated on the way out; every caller wraps the call in @(), which is what keeps one slice
    # and zero slices both arrays. (`return ,$slices` double-wrapped under that @() and the first
    # run of this iterated over the array itself.)
    return $slices
}

# Synchronous reads of a redirected stream, in CODE. Strips line comments first: until 2026-09-29 the
# scan ran on the raw text, so a comment saying "never call StandardOutput.ReadToEnd()" would have
# redded the gate -- the prose-versus-code failure this file records fixing four times (F412). Two
# forms, because those are the two the repo actually uses; a sync ReadLine loop would slip through and
# is worth adding the day someone writes one.
function Get-SynchronousReadSites {
    param([string] $Text)
    $code = Remove-LineComments $Text
    return @([regex]::Matches($code,
        'Standard(?:Output|Error)\s*\.\s*(?:BaseStream\s*\.\s*CopyTo\(|ReadToEnd\(\))'))
}


$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot

$formPetSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FormCompanion.cs') -Raw
$formSpeechSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FormSpeech.cs') -Raw
$contextMenuSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\ContextMenus.cs') -Raw
$startUpSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\StartUp.cs') -Raw
$petHostSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Plugins\CompanionHost.cs') -Raw

Assert-True (
    $formPetSource -match
        '(?s)Timer1_Tick\(.*?CheckFullScreen\(\);\s*NextStep\(\);' -and
    $formPetSource.Contains('_speech.SetFullscreenSuppressed(') -and
    $formSpeechSource.Contains(
        'internal void SetFullscreenSuppressed(bool suppressed)') -and
    -not $formSpeechSource.Contains(
        'cp.ExStyle |= 0x00000008')
) 'stationary fullscreen polling and speech z-order propagation'
Assert-True (
    $formPetSource.Contains('rctO.Right <= rctO.Left') -and
    $formPetSource.Contains('rctO.Bottom <= rctO.Top') -and
    $formPetSource.Contains('DesktopGeometry.TryScaleWindowRelativeX(')
) 'window following rejects collapsed rectangles and uses safe relative scaling'

# A poke must be attributed to the pet the user actually clicked. Only FormCompanion knows which one that is, and
# the host cannot recover it afterwards -- it falls back to the first pet on screen, so dropping `this` here
# silently reports a poke on pet #5 as a poke on pet #1. Invisible while every speaker broadcasts through
# SayAll, and wrong the instant anything reacts per pet, which is exactly where this is heading.
Assert-True (
    $formPetSource.Contains('OnPetPoked(this)') -and
    -not ($formPetSource -match 'OnPetPoked\(\s*\)')
) 'a poke is attributed to the pet that was actually clicked'

Assert-True (
    # S4b: the AI-brain tray items (Ask / Enable-Disable) moved out of the base with the AI-brain module,
    # so the base context menu no longer carries any AI tray label. The Test Speech item stays.
    -not $contextMenuSource.Contains('&Enable AI') -and
    -not $contextMenuSource.Contains('&Disable AI') -and
    -not $contextMenuSource.Contains('As&k about my screen') -and
    -not $contextMenuSource.Contains('Unload AI (free VRAM)') -and
    -not $contextMenuSource.Contains('Load AI (uses GPU)') -and
    $contextMenuSource.Contains('Right-click the tray icon for options.')
) 'AI tray items removed from the base (moved to the AiBrain module); test-speech intact'

# Any redirected child-process output must pin its own encoding. Leaving StandardOutputEncoding unset
# decodes the stream via GetConsoleOutputCP(), which is 0 in a GUI process with no console, and .NET reads
# codepage 0 as CP_ACP (the system ANSI codepage). That silently mojibake'd every non-ASCII glyph tesseract
# read off the screen before it ever reached the model. Repo-wide because the next redirect will be written
# by someone who never met this bug.
#
# PER SITE, BOTH STREAMS, AND A FLOOR. All three of those were missing and each hid a real hole:
#
#   Per FILE meant one pin anywhere cleared a file however many redirect sites it had.
#   tools\ShimejiConvert.Engine\Engine.cs has TWO ProcessStartInfo blocks; deleting the pin from the
#   second left this check green with the live CP_ACP bug it exists to prevent.
#
#   Stdout ONLY meant RedirectStandardError with no StandardErrorEncoding was never checked at all,
#   and stderr is where child processes put the diagnostics you read when something has gone wrong.
#
#   No floor meant a moved or renamed tree yields zero files and the check passes on no evidence --
#   the same defect this file already guards against for the .wxs loop and the CI script set.
#
# Asserts that AN encoding is pinned, not that it is UTF-8: WebPLoader.cs pins Latin1 on stdout
# deliberately, and a UTF-8 assertion would fail correct code.
# The per-file judgement, as a FUNCTION so each offender branch has a positive control of its own
# (R-067). The WITNESS trio below the check exercised the two helpers -- the slicer and the
# synchronous-read finder -- while the decision that turns their output into an offender was inline in
# the file loop and nothing in the gate ever fed it a positive case: `-notmatch 'StandardOutputEncoding'`
# becoming `-match` would have kept every witness green and been caught only by
# tests/mutate-hardening-guards.py, which is not part of the gate. Takes COMMENT-STRIPPED code and the
# file's display name; answers with the offenders and the number of initialiser sites it judged (site
# numbers in the offender text are per file), because the floor after the loop counts sites across
# files. ONE object back (a hashtable), not two values: a function that returns an array enumerates it
# on the way out, which is how the slicer's `return ,$slices` once double-wrapped.
function Get-RedirectOffenders {
    param([string] $Code, [string] $Relative)
    $offenders = @()
    $sites = 0

    # DRAIN ORDER, REPO-WIDE -- and judged BEFORE the redirect admission below, for every file handed in
    # (RA-360). A SYNCHRONOUS read of a redirected stream blocks until that stream hits EOF, which for a
    # child process means it exited -- so any WaitForExit(timeout) after one is unreachable until the
    # thing it was meant to bound has already resolved itself. WebPLoader.cs did
    # `StandardOutput.BaseStream.CopyTo(...)` before `WaitForExit(30000)`, so a dwebp that hung without
    # closing stdout hung the converter forever and the 30 seconds never applied. Async reads
    # (ReadToEndAsync, CopyToAsync) do not have the problem, and every other site in the repo already
    # used them. Three named files had this pinned individually; nothing asserted it for the rest, and
    # the fourth site was exactly where the defect was. Until 2026-09-30 this scan sat AFTER the
    # admission test, so a file that drains a stream whose ProcessStartInfo (and redirect assignment)
    # lives in another file -- a shared runner helper -- was never scanned at all, under a heading that
    # said repo-wide; the WITNESS below feeds it exactly that fixture.
    foreach ($syncRead in @(Get-SynchronousReadSites $Code)) {
        $line = ($Code.Substring(0, $syncRead.Index) -split "`n").Count
        $offenders += ("$Relative`:$line reads a redirected stream SYNCHRONOUSLY " +
            "($($syncRead.Value.Trim())), which blocks until the child exits and makes any " +
            'WaitForExit timeout after it unreachable -- use ReadToEndAsync/CopyToAsync')
    }

    # Admission for the ENCODING half: only a file that assigns a redirect has initialiser sites to judge.
    if ($Code -notmatch 'RedirectStandard(Output|Error)\s*=\s*true') {
        return @{ Offenders = $offenders; Sites = $sites }
    }
    $coveredRedirects = 0
    # One ProcessStartInfo initialiser at a time, each sliced from its opening brace to the brace
    # that closes it (see Get-ProcessStartInfoInitialisers for why not the next `};`). Slicing per
    # initialiser is what makes this per-SITE: two sites in one file are judged separately.
    foreach ($body in @(Get-ProcessStartInfoInitialisers $Code)) {
        $redirectsOut = $body -match 'RedirectStandardOutput\s*=\s*true'
        $redirectsErr = $body -match 'RedirectStandardError\s*=\s*true'
        if (-not ($redirectsOut -or $redirectsErr)) { continue }
        $coveredRedirects += [regex]::Matches($body, 'RedirectStandard(Output|Error)\s*=\s*true').Count
        $sites++
        if ($redirectsOut -and ($body -notmatch 'StandardOutputEncoding\s*=')) {
            $offenders += "$Relative (site $sites): stdout redirected, encoding unpinned"
        }
        if ($redirectsErr -and ($body -notmatch 'StandardErrorEncoding\s*=')) {
            $offenders += "$Relative (site $sites): stderr redirected, encoding unpinned"
        }
    }

    # COVERAGE, not just correctness -- the same hole as the per-FILE one above, one level further
    # out. The slice only understands the object-initialiser form `new ProcessStartInfo { ... };`. A
    # file that instead writes `psi.RedirectStandardOutput = true;` after construction still PASSES
    # the admission test above, then yields no blocks, so it contributes zero sites and zero
    # offenders and is silently never judged. Counting what was admitted against what was actually
    # sliced turns that blind spot into a named failure.
    #
    # Latent in the tree and deliberately left that way: every redirect assignment in the repo sits in
    # an initialiser, so this adds no work now. It stops being latent the first time anyone writes the
    # other form -- which is the form ContentCatalogAssets.ps1 uses, in PowerShell, where nothing
    # scans it at all. Its positive control is the WITNESS below, not a file in the tree.
    $declaredRedirects = [regex]::Matches($Code, 'RedirectStandard(Output|Error)\s*=\s*true').Count
    if ($declaredRedirects -gt $coveredRedirects) {
        $offenders += ("$Relative`: $($declaredRedirects - $coveredRedirects) redirect " +
            'assignment(s) sit outside a ProcessStartInfo initialiser, so this scan cannot judge ' +
            'their encoding -- move them into the initialiser, or teach this check that form')
    }
    return @{ Offenders = $offenders; Sites = $sites }
}

$redirectOffenders = @()
$redirectSiteCount = 0
# THE BRANCH'S files, from git, not everything under the checkout. Get-ChildItem -Recurse descended
# into `.claude\worktrees\` (not hidden), where a git worktree of ANOTHER branch is a full second copy
# of the tree, so an unpinned site on that branch failed this gate on master and a stale copy of a
# pinned site kept the floor below satisfied (F411); the shell-parity block further down already
# scopes itself with `git ls-files` for the same reason. `-co --exclude-standard` is tracked PLUS
# untracked-not-ignored, so a brand-new .cs with a fresh redirect is judged before it is staged, while
# `.claude/`, bin\ and obj\ are ignored and drop out. Measured 2026-09-30: 213 files either way on
# this tree, with no file on one side only.
$redirectScanFiles = @(& git -C $repoRoot ls-files -co --exclude-standard -- '*.cs' 2>$null | ForEach-Object {
    $p = Join-Path $repoRoot ($_ -replace '/', '\')
    if (Test-Path -LiteralPath $p -PathType Leaf) { Get-Item -LiteralPath $p }
})
foreach ($file in $redirectScanFiles |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    # Comment-stripped throughout, so a redirect mentioned in prose neither admits a file nor counts
    # as a declared assignment the slicer then fails to cover. Every file is judged, admitted or not:
    # the synchronous-read half has no admission (RA-360).
    $judged = Get-RedirectOffenders (Remove-LineComments (Get-Content -LiteralPath $file.FullName -Raw)) (
        $file.FullName.Substring($repoRoot.Length + 1))
    $redirectOffenders += @($judged.Offenders)
    $redirectSiteCount += $judged.Sites
}
Assert-True ($redirectSiteCount -ge 6) (
    "there are redirect sites to check at all (found $redirectSiteCount, floor 6)")
Assert-True ($redirectOffenders.Count -eq 0) (
    'every redirected child stream pins its own encoding, per SITE' +
    $(if ($redirectOffenders.Count -gt 0) { " (offenders: $($redirectOffenders -join '; '))" } else { '' }))

# WITNESSES FOR THE JUDGEMENT ITSELF (R-067, RA-360): one fixture per offender branch, each expected to
# name exactly one offender and to count the sites the fixture really has, then a pinned fixture that
# must name none. Fed to the same function the loop above calls, so a branch deleted or inverted there
# fails HERE, in the gate, rather than only under the mutation harness. The fourth fixture is the RA-360
# shape: a synchronous read in a file that assigns no redirect of its own, which the loop's old
# admission test used to skip before the scan ran.
$redirectJudgementWitnesses = @(
    @{ Name = 'stdout unpinned'; Sites = 1; Offender = 'stdout redirected, encoding unpinned'
       Code = 'var psi = new ProcessStartInfo { FileName = "x", RedirectStandardOutput = true };' },
    @{ Name = 'stderr unpinned'; Sites = 1; Offender = 'stderr redirected, encoding unpinned'
       Code = 'var psi = new ProcessStartInfo { FileName = "x", RedirectStandardError = true };' },
    @{ Name = 'redirect outside an initialiser'; Sites = 0; Offender = 'sit outside a ProcessStartInfo initialiser'
       Code = 'var psi = new ProcessStartInfo("x"); psi.RedirectStandardOutput = true;' },
    @{ Name = 'synchronous read in a file that declares no redirect'; Sites = 0; Offender = 'reads a redirected stream SYNCHRONOUSLY'
       Code = 'string all = process.StandardOutput.ReadToEnd();' }
)
foreach ($witness in $redirectJudgementWitnesses) {
    $judgedWitness = Get-RedirectOffenders $witness.Code 'witness.cs'
    Assert-True (@($judgedWitness.Offenders).Count -eq 1 -and $judgedWitness.Sites -eq $witness.Sites -and
        @($judgedWitness.Offenders)[0] -cmatch [regex]::Escape($witness.Offender)) (
        "WITNESS: the redirect judgement reports $($witness.Name) as exactly one offender" +
        " (got $(@($judgedWitness.Offenders).Count) offender(s), $($judgedWitness.Sites) site(s))")
}
$judgedPinned = Get-RedirectOffenders (
    'var psi = new ProcessStartInfo { FileName = "x", RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };') 'witness.cs'
Assert-True (@($judgedPinned.Offenders).Count -eq 0 -and $judgedPinned.Sites -eq 1) (
    'WITNESS: the redirect judgement names no offender for a pinned initialiser and counts its one site' +
    " (got $(@($judgedPinned.Offenders).Count) offender(s), $($judgedPinned.Sites) site(s))")

# WITNESS for the slicer: the exact shape the regex slicer got wrong. Two initialisers, the first an
# unpinned site closed by `}))`, the second pinned and closed by `};`, then a bare constructor. The
# old slicer returned ONE span running from the first `new` to the second `};`, in which the pin
# cleared the unpinned site. The brace walk must return two slices, and only the second may carry the
# pin. Fed through the same function the loop above uses, so a slicer regression fails here by name.
$slicerProbe = @'
using (Process p = Process.Start(new ProcessStartInfo { FileName = "a", RedirectStandardOutput = true })) { }
var psi = new ProcessStartInfo("cmd.exe", "/c dir") { RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
var bare = new ProcessStartInfo();
'@
$slicerSites = @(Get-ProcessStartInfoInitialisers $slicerProbe)
Assert-True ($slicerSites.Count -eq 2) (
    'WITNESS the initialiser slicer separates a site closed by }))' + ' from the site closed by };' +
    ' after it (found ' + $slicerSites.Count + ' slices, expected 2)')
Assert-True (
    $slicerSites.Count -eq 2 -and
    ($slicerSites[0] -notmatch 'StandardOutputEncoding') -and
    ($slicerSites[1] -match 'StandardOutputEncoding\s*=')
) 'WITNESS the pin in the second initialiser does not leak into the unpinned first one'
# WITNESS for the synchronous-read scan: the same call as a comment must not count, and as code must.
Assert-True (
    @(Get-SynchronousReadSites '    // never call proc.StandardOutput.ReadToEnd() here').Count -eq 0 -and
    @(Get-SynchronousReadSites '    string all = proc.StandardOutput.ReadToEnd();').Count -eq 1
) 'WITNESS the synchronous-read scan sees code and ignores comments'

# Module payloads must be unpacked OFF the UI thread. fortunes.zip is ~31 MB, and unpacking it
# synchronously froze the settings window for seconds during an install or update. Nothing else catches
# this: there is no .editorconfig and CA1849 is not surfaced at warning severity, which is how the
# synchronous version shipped in the first place.
$modulesPaneSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\ModulesPaneControl.cs') -Raw
Assert-True (
    $modulesPaneSource.Contains('ZipFile.ExtractToDirectoryAsync(') -and
    $modulesPaneSource -notmatch '(?<!Async)\bExtractToDirectory\('
) 'module payloads are extracted asynchronously, never on the UI thread'

# THE SAME RULE, AT THE OTHER SITE. The check above names ModulesPaneControl.cs only, so Companion
# Studio's import sat outside every check while doing strictly MORE work on the UI thread: a skin zip
# extracted inline, then SpriteSheetBuilder.Build (up to 8 full composite + PNG-encode + base64 passes
# over a sheet as large as 4096x4096) and SoundBaker (one ffmpeg per unique clip, 30s cap, up to 64),
# all from a click handler.
#
# PetStudio WRAPS rather than calling an Async overload, because ShimejiEngine.ConvertSkin and
# BundleConverter.ConvertBundle have none -- so what is asserted here is the wrapping itself.
# Asserting merely that Task.Run appears somewhere in the file would pass on a version that wrapped
# something else and still converted inline.
$petStudioWindowSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\PetStudio\PetStudioWindow.cs') -Raw
$petStudioFlat = ($petStudioWindowSource -replace '\s+', ' ')
Assert-True (
    $petStudioFlat.Contains('await Task.Run(delegate { ZipFile.ExtractToDirectory(') -and
    $petStudioFlat.Contains('await Task.Run(delegate { string e; ConversionResult r = ShimejiEngine.ConvertSkin(') -and
    $petStudioFlat.Contains('await Task.Run(delegate { string e; ConversionResult r = BundleConverter.ConvertBundle(')
) 'Companion Studio extracts and converts off the UI thread, not inline from the click handler'

# A pet's speech preference must NEVER be keyed by the raw pet-mix id. The mix writes the active/default pet
# as "", but "" in triggerSpeech already means the ALL-PETS entry -- so keying a real pet as "" silently
# rewrites the global preference, and it LOOKS correct because the lookup falls back to global. Every pet type
# other than the active one would test fine. Exactly the class of bug this file exists to catch.
Assert-True (
    $contextMenuSource.Contains('SpeechRoutingKey(') -and
    $contextMenuSource -notmatch 'SetTriggerSpeechModule\(\s*(entry\.Id|mixId)\b' -and
    $petHostSource.Contains('SpeechRoutingKey(')
) 'a pet speech preference is keyed by the routing key, never by the raw mix id'

# The module tray section anchors after Pet Speech. Anchoring on Test Speech (as it did before Pet Speech was
# inserted between them) drops module items into the middle of the base's own speech block.
Assert-True (
    $contextMenuSource.Contains('petSpeechMenuItem ?? testSpeechMenuItem')
) 'module tray items are anchored after the Pet Speech item'

# A reaction belongs to ONE pet. The base's poke sass used to go through SayAll, which is the reported bug:
# poke one pet and every pet on screen says the same line at the same moment.
Assert-True (
    $startUpSource -notmatch 'RandomSass\(\)[\s\S]{0,200}SayAll\('
) 'the poke sass is spoken by the poked pet, not broadcast'

# SayAll and PlayAnimationOnAll must skip authoring previews. They walked sheeps[] directly, so a preview pet
# spoke and emoted -- contradicting the documented "previews are invisible to modules" invariant, which
# otherwise rests solely on DeriveOnScreenMix.
Assert-True (
    # SayAll delegates to ShowBubbleOnAll, which is the one place an unaddressed message is spoken, so that is
    # where the preview filter has to live. It no longer fans out -- it picks ONE speaker -- so the filter now
    # sits one hop deeper, in DefaultSpeaker. Both hops are asserted: ShowBubbleOnAll must resolve its target
    # through DefaultSpeaker, and DefaultSpeaker must enumerate PersistentPets rather than walking sheeps[].
    $startUpSource -match '(?s)internal void ShowBubbleOnAll\([\s\S]{0,400}?DefaultSpeaker\(\)' -and
    $startUpSource -match '(?s)internal FormCompanion DefaultSpeaker\([\s\S]{0,600}?PersistentPets\(\)' -and
    $startUpSource -match '(?s)public void SayAll\(string text\)[\s\S]{0,600}?ShowBubbleOnAll\(' -and
    $startUpSource -match '(?s)internal void PlayAnimationOnAll\([\s\S]{0,600}?PersistentPets\(\)'
) 'broadcast speech and animation skip preview pets'

# An unaddressed message must reach exactly ONE pet. Every pet saying the same line at the same instant is the
# reported bug, and the ABI comment on SayAll called it out long before it was fixed. Asserting the absence of
# the fan-out, because the presence of DefaultSpeaker above does not prove the loop is gone.
Assert-True (
    $startUpSource -notmatch '(?s)internal void ShowBubbleOnAll\([\s\S]{0,400}?foreach[\s\S]{0,80}?PersistentPets\(\)'
) 'an unaddressed message is spoken by one pet, not broadcast to every pet'

# A module can hold an ICompanion across a slow await and there is no CompanionRemoved event, so Say must tolerate a pet
# that has closed rather than throwing out of the module's call.
Assert-True (
    $petHostSource -match '(?s)public void Say\(ICompanion pet, string text\)[\s\S]{0,300}?IsDisposed'
) 'IHost.Say guards a disposed pet'

# Module audio must NEVER enter AudioOutput._cache. That table is keyed by byte[] REFERENCE identity and
# holds each decode for as long as its SOURCE array lives, so caching synthesized speech would pin every line
# the pet is still holding -- plus a mixer-format buffer roughly 7x larger than the input -- for no reuse at
# all, because no two spoken lines share bytes. The engine path caches on purpose (a pet has a fixed
# set of animation sounds); the module path must not. Nothing else can catch this: it leaks slowly, only with
# a voice module installed, and never fails a test.
$audioSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\AudioOutput.cs') -Raw
# Sliced by POSITION rather than brace-matched. A regex counting braces cannot see past PlayOwned's inner
# lock block, so it silently passed even with `_cache[audio] = samples` injected -- found by negative-testing
# the check itself, which is the only way that class of dud assertion ever surfaces.
$playOwnedStart = $audioSource.IndexOf('public bool PlayOwned(')
$playOwnedEnd = $audioSource.IndexOf('public bool StopOwned(')
Assert-True (
    $playOwnedStart -gt 0 -and $playOwnedEnd -gt $playOwnedStart -and
    -not $audioSource.Substring($playOwnedStart, $playOwnedEnd - $playOwnedStart).Contains('_cache')
) 'module audio is never entered into the decode cache'

# The shared notification sound's three layers -- the notificationSounds switch, the master volume, the
# output device -- live in NotificationSound.Play, and --audio-selftest asserts all three (and their ORDER)
# with no device. What nothing there can see is whether StartUp's seam still routes through it. That method
# shipped as a literal `return false;` while the ABI already advertised IHost.PlayNotificationSound, so
# reverting it -- by a bad merge, or by someone "inlining" the gates -- restores a build where every gate is
# green and every module is silent, which is exactly the state this feature was added to end.
#
# The CALL with both of its real arguments, not merely the name: a body that passed null for either would
# gate on nothing (null settings answers NoSettings, null output answers NoDevice) and still compile, still
# return a bool, and still look right in a diff.
#
# MATCHED ON COMMENT-STRIPPED SOURCE, because it used to match the raw text with a 120-character
# window between the signature and the call -- so adding a comment that EXPLAINED the call broke the
# assertion while satisfying everything it is about. A source check whose verdict depends on how much
# prose sits beside the code is measuring the wrong thing.
Assert-True (
    (Remove-LineComments $startUpSource) -match '(?s)internal bool PlayNotificationSound\(string moduleId\)[\s\S]{0,160}?NotificationSound\.Play\(Program\.MyData, audioOutput,[\s\S]{0,120}?== NotificationOutcome\.Played'
) 'the notification-sound seam routes through the gated policy, with the live settings and output'

# The faceCursor DISPATCH. Converted gaze animations carry <action>faceCursor</action>, the validator accepts
# it, and the pure facing rule is asserted in --hardening-selftest -- but none of that reaches a pet unless
# SetNewAnimationCore actually calls FaceTheCursor when the tag is present. Delete the call and every other
# check in the project still passes: the animation plays, just never aimed. A source-text check because the
# real thing needs a live form, a real cursor and a loaded pet.
Assert-True (
    $formPetSource -match '(?s)"faceCursor"[\s\S]{0,200}?FaceTheCursor\(\);' -and
    # ...and it must SET facing, not toggle it. FlipOrientation here would be wrong half the time, which is
    # exactly the bug that looks like "the gaze works, sometimes".
    $formPetSource -match '(?s)private void FaceTheCursor\(\)[\s\S]{0,600}?IsMovingLeft = ShouldFaceLeft\('
) 'a faceCursor animation aims the pet at the pointer on entry'

# All THREE window borders must raise their discriminator. The flag algebra is asserted in
# --hardening-selftest, but nothing there can see whether the detection sites actually pass the new value:
# revert any one of them to a bare WINDOW and every other check stays green while that edge silently stops
# being distinguishable. Anchored on the three distinct comparisons so the checks cannot pass by matching
# the same line three times.
# Anchored on each site's own comment rather than on its comparison: the comparisons sit several hundred
# characters from the call once the reasoning above them is written down, and a distance-based anchor that
# has to be widened every time a comment grows is a check that will eventually be widened into uselessness.
Assert-True (
    $formPetSource -match '(?s)// left window border![\s\S]{0,600}?TOnly\.WINDOW \| TNextAnimation\.TOnly\.WINDOW_LEFT' -and
    $formPetSource -match '(?s)// right window border![\s\S]{0,600}?TOnly\.WINDOW \| TNextAnimation\.TOnly\.WINDOW_RIGHT' -and
    $formPetSource -match '(?s)FallDetect\(y\)[\s\S]{0,600}?TOnly\.WINDOW \| TNextAnimation\.TOnly\.WINDOW_TOP'
) 'each window border raises which edge it is'

# Window-side grip: the parts that need a real window on screen and so cannot be asserted anywhere else.
#
# 1. The rect is RE-READ every tick. Caching it at grip time leaves a pet pinned to where a window used to
#    be after you drag, resize or close it, which is the most obviously broken thing this can do.
# 2. A degenerate rect releases. Minimised windows report one, and pinning to it teleports the pet.
# 3. hwndWindow is a PROPERTY that clears the grip. Nine sites drop that handle for their own reasons and a
#    grip surviving one of them pins the pet to a window it is no longer tracking.
Assert-True (
    $formPetSource -match '(?s)windowGrip != WindowGrip\.None[\s\S]{0,600}?GetWindowRect\(new HandleRef\(this, hwndWindow\)' -and
    $formPetSource -match '(?s)windowGrip != WindowGrip\.None[\s\S]{0,600}?gripRect\.Right <= gripRect\.Left' -and
    # The CONDITION, not just the assignment. Asserting only that the property body mentions
    # `windowGrip = WindowGrip.None` passes a setter whose guard has been disabled -- the statement is
    # still there, just unreachable. Negative-tested: this is the version that fails when the guard goes.
    $formPetSource -match '(?s)IntPtr hwndWindow\s*\{[\s\S]{0,900}?if \(value == \(IntPtr\)0\)[\s\S]{0,300}?windowGrip = WindowGrip\.None;'
) 'a window grip re-reads the window every tick and is dropped with the handle'

# The vertical limits of a grip are the WINDOW's, and they must be tested BEFORE the screen ones. Reorder
# them and a gripping pet climbs straight past the frame it is holding, up to the top of the screen.
#
# By POSITION rather than by a bounded regex, because the property is an ordering and a distance-based
# pattern only approximates it: the branch is nearly two thousand characters long, so any regex wide enough
# to span it would also match the two branches in the wrong order.
$gripBranch = $formPetSource.IndexOf('else if (gripping)')
$downBranch = $formPetSource.IndexOf('else if(y > 0)')
Assert-True (
    $gripBranch -gt 0 -and $downBranch -gt $gripBranch -and
    $formPetSource.Substring($gripBranch, $downBranch - $gripBranch).Contains('gripRect.Bottom') -and
    $formPetSource.Substring($gripBranch, $downBranch - $gripBranch).Contains('gripRect.Top')
) 'a gripping pet is bounded by the window, checked before the screen'

# Letting go must work in both directions. ReleaseWindowGrip implements "let go" by playing the fall
# animation; nothing did the inverse, so a graph that transitioned INTO fall by its own <next> edge kept the
# grip. Under a window that is permanent: the underside branch above pins y to 0 and both of its release
# conditions test y, so neither can ever fire again.
#
# GripMustRelease is a pure static with its own assertions in --hardening-selftest, so what is checked HERE is
# the thing a unit test cannot see: that SetNewAnimationCore actually calls it, and that the call is wired to
# the fall animation and to clearing hwndWindow. A correct predicate nobody invokes is the exact failure mode
# the standing rule about source-text checks warns about.
$setNewCore = $formPetSource.IndexOf('private void SetNewAnimationCore(int id)')
$coreEnd    = $formPetSource.IndexOf('private void NextStep()', $setNewCore)
$coreBody   = if ($setNewCore -gt 0 -and $coreEnd -gt $setNewCore) { $formPetSource.Substring($setNewCore, $coreEnd - $setNewCore) } else { '' }
Assert-True (
    $coreBody -match 'GripMustRelease\(' -and
    # ...told which animation it is entering, or it can never answer the fall case.
    $coreBody -match '(?s)GripMustRelease\([\s\S]{0,200}?id == Animations\.AnimationFall' -and
    # ...and the release it guards is the one that clears the handle. Clearing windowGrip alone leaves the
    # pet "on" a window it is no longer pinned to, which is why ReleaseWindowGrip exists as one place.
    $coreBody -match '(?s)GripMustRelease\([\s\S]{0,400}?hwndWindow = \(IntPtr\)0'
) 'entering an animation that cannot hold a grip drops the window handle'

# The Pets pane diffed the catalog by ID alone, so a pet already installed was filtered out of "available to
# download" however much its CONTENT had changed. A corrected pet reached new downloads only, and the pane said
# "you already have every available pet" while an update sat there. CompanionProvenance now answers it by hash.
#
# Asserted HERE rather than only in the unit table because the classifier is useless unless the pane calls it,
# and because the shipped bug was precisely a missing comparison rather than a wrong one.
$petsPane = Get-Content -Raw (Join-Path $repoRoot 'src\Portable\Wpf\CompanionsPaneControl.cs')
Assert-True (
    # A third list exists and is rendered from a STALENESS diff, not from the id diff. Asserted
    # against the SHARED classifier rather than against the pane's own expression: the first form of
    # this clause pinned the literal `IsStale(FreshnessOf(pet))`, which is an implementation and not
    # the property -- so collapsing the pane's duplicate hashing onto CompanionProvenance.StaleInstalled
    # broke the check while strictly improving what it was protecting.
    $petsPane -match '(?s)private static List<StalePet> DiffStale\([\s\S]{0,900}?CompanionProvenance\.StaleInstalled\(' -and
    # ...and it is actually wired to the button that checks the catalog, or nothing ever populates it.
    $petsPane -match '(?s)CheckButton_Click[\s\S]{0,1600}?DiffStale\(' -and
    $petsPane -match '(?s)CheckButton_Click[\s\S]{0,1800}?RenderUpdates\(' -and
    # The freshness verdict must come from the shared classifier, not from a second opinion in the UI.
    # It used to call Classify directly; it now calls FreshnessOfInstalled, which wraps Classify and is
    # shared with the weekly background check, so the pane and the notification cannot disagree.
    $petsPane -match 'CompanionProvenance\.FreshnessOfInstalled\(' -and
    # Installing must stamp provenance from the DOWNLOADED BYTES. Stamping from a re-read file would still
    # work today, but hashing what was verified is what makes the comparison exact.
    $petsPane -match 'CompanionProvenance\.WriteStamp\([^)]*CompanionProvenance\.HashBytes\(bytes\)' -and
    # And the confirm prompt must be driven by the classifier, so the prompt cannot disagree with the badge.
    $petsPane -match 'CompanionProvenance\.UpdateWouldDiscardChanges\(' -and
    # The status line has to be derived from the STALE count too. The old one read "you already have every
    # available pet", which was true by the ID diff and false in the only sense that mattered, and a pane that
    # renders the update cards while still saying that is the shipped bug with extra steps.
    #
    # Deliberately NOT a ban on that sentence: the comments in there explain the bug and quote it, and a check
    # that forbids describing a bug is a check that gets deleted. Assert the derivation instead.
    $petsPane -match '(?s)stalePets\.Count[\s\S]{0,600}?_status\.Text'
) 'the Pets pane offers a content update, stamps what it installed, and says so'

# Hashing every installed catalog companion must happen OFF the UI thread, on every path that does it.
#
# RefreshCatalogOnOpen has said so in a comment since it was written, and the button did it
# synchronously anyway -- so the pane froze for the duration of a SHA-256 over the whole installed
# library precisely when the user had just asked it to do something. A comment on one path is not a
# property of the pane.
#
# Counted rather than pattern-ordered: every DiffStale CALL must be the body of a Task.Run, so a
# fourth call site added later cannot quietly be synchronous.
$diffStaleTotal = ([regex]::Matches($petsPane, 'DiffStale\(')).Count
$diffStaleDeclared = ([regex]::Matches($petsPane, 'private static List<StalePet> DiffStale\(')).Count
$diffStaleOffThread = ([regex]::Matches($petsPane, 'Run\(delegate \{ return DiffStale\(')).Count
Assert-True (($diffStaleTotal - $diffStaleDeclared) -gt 0) (
    "the Pets pane calls DiffStale somewhere (found $($diffStaleTotal - $diffStaleDeclared) calls)")
Assert-True (($diffStaleTotal - $diffStaleDeclared) -eq $diffStaleOffThread) (
    'every Pets-pane staleness diff runs off the UI thread' +
    " ($($diffStaleTotal - $diffStaleDeclared) calls, $diffStaleOffThread of them inside a Task.Run)")

# The classification must TRAVEL to the card. Recovering it from the id costs another SHA-256 of the
# same file, which is what BuildUpdateCard did: DiffStale classified every installed catalog pet, then
# each card re-hashed the one it was rendering, on the UI thread, for a string it had already computed.
Assert-True (
    $petsPane -match '(?s)private FrameworkElement BuildUpdateCard\(StalePet entry\)[\s\S]{0,600}?CompanionFreshness freshness = entry\.Freshness;'
) 'a stale companion card reads the freshness the diff already computed instead of re-hashing'

# The window UNDERSIDE, checked before the screen's top border for the same reason the window top is
# checked before the taskbar: a window is inside the screen, so testing the screen first lets a jumping pet
# pass straight through one on its way to the top of the display.
#
# Ordering alone is not the property, and asserting only that was negative-tested away: a RiseDetect call
# that appears first but is gated behind something unreachable still satisfies it. The load-bearing part is
# that the screen-top test is CHAINED off the underside result (`else if`), so the two cannot both fire on
# one tick -- a pet that just grabbed an overhang must not also be snapped to the top of the display.
$upBranch    = $formPetSource.IndexOf('else if(y < 0)')
$screenTop   = $formPetSource.IndexOf('else if (PositionY + y < workArea.Y)', $upBranch)
$riseCall    = $formPetSource.IndexOf('RiseDetect(y, ins)', $upBranch)
Assert-True (
    $upBranch -gt 0 -and $riseCall -gt $upBranch -and $screenTop -gt $riseCall -and
    # ...and nothing re-tests the screen top unconditionally alongside it.
    $formPetSource.IndexOf('if (PositionY + y < workArea.Y)', $upBranch) -eq ($screenTop + 5)
) 'a window underside is checked before the top of the screen, and the screen top is chained off it'

# RiseDetect claims hwndWindow on the way in. If nothing wants to hang there it MUST give it back, or the
# pet believes it is standing on a window it is merely underneath and the gravity branch starts following
# that window around.
Assert-True (
    $formPetSource -match '(?s)RiseDetect\(y, ins\)[\s\S]{0,1600}?else\s*\{[\s\S]{0,400}?hwndWindow = \(IntPtr\)0;'
) 'a refused window underside gives the window handle back'

# A maximised window's bottom edge sits on the work area, directly over a pet standing on the taskbar.
# Without the clearance test the pet grabs the underside on the first tick of every jump it ever makes.
Assert-True (
    $formPetSource -match '(?s)private WindowTopHit RiseDetect\([\s\S]{0,3000}?rct\.Bottom >= ScreenArea\.Y \+ ScreenArea\.Height'
) 'the underside test ignores a window whose bottom is the work area'

# A pane's Load() MUST run before its Schema is read. Two dynamic dropdowns now depend on it -- the
# Preferences "pet that speaks for the app" and Reminder's per-calendar "Reminder pet" -- because both
# repopulate their Options from the pets currently on screen inside Load(). Reverse the two lines and nothing
# throws, nothing fails a build, and both dropdowns silently freeze at whatever list existed when the pane was
# constructed: the pets you added since simply never appear. Asserting the ORDER, not the presence of either
# statement, because both statements survive the reordering that breaks this.
# Strip line comments so an invariant cannot be satisfied -- or an ordering check inverted -- by prose. This
# repo has been bitten four times by a source check that a comment alone was enough to pass.
$optionsWindowSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\OptionsWindow.cs') -Raw
# Comments stripped FIRST. This check previously read raw source, and a comment saying
# 'runs BEFORE _pane.Schema is read below' landed earlier in the file than the read it was
# describing -- so the order assertion inverted and failed on correct code. A source check
# that cannot tell code from prose is measuring the wrong thing in either direction.
$optionsWindowCode = Remove-LineComments $optionsWindowSource
$buildStart  = $optionsWindowCode.IndexOf('public FrameworkElement Build()')
$loadCall    = [Math]::Max($optionsWindowCode.IndexOf('_pane.Load()', $buildStart),
                           $optionsWindowCode.IndexOf('_pane.LoadPending(', $buildStart))
$schemaRead  = $optionsWindowCode.IndexOf('_pane.Schema', $buildStart)
Assert-True (
    $buildStart -gt 0 -and $loadCall -gt $buildStart -and $schemaRead -gt $loadCall
) 'a pane Load() runs before its Schema is read, so a dropdown can offer live options'

# ...and that the two panes actually USE that window, rather than merely declaring a field. A dropdown whose
# Options are only set at construction is the frozen case above.
$optionsShellSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\OptionsShell.cs') -Raw
# CONTAINMENT, not distance. This was a regex with a 4000-character budget between "Load = delegate" and
# "speakerField.Options =", which is a proxy for "inside the Load body" that stops being true the moment
# anything else is added to that body -- as adding the diagnostic-log fields did. It then fails against
# correct code, which is the worst kind of check: it trains you to widen the number rather than read it.
# Load runs before Save in this initializer, so "between the two" is the real relationship.
$loadAt    = $optionsShellSource.IndexOf('Load = delegate')
$saveAt    = $optionsShellSource.IndexOf('Save = delegate', [Math]::Max($loadAt, 0))
$optionsAt = $optionsShellSource.IndexOf('speakerField.Options =', [Math]::Max($loadAt, 0))
Assert-True (
    $loadAt -ge 0 -and $saveAt -gt $loadAt -and $optionsAt -gt $loadAt -and $optionsAt -lt $saveAt
) 'the speaker dropdown refreshes its options inside Load, not only at construction'

# There is ONE fullscreen detector and every scan lands in NoteFullscreenScan. Two implementations of
# "is a game running" would drift, and only one of them has --fullscreen-selftest behind it. The
# spawn-time check in FormCompanion deliberately scans for itself (a companion appearing on a blocked
# monitor cannot wait for the next cycle) and so it has to hand the whole blocked-monitor array up,
# before narrowing to its own monitor -- a module asking the question means ANY monitor, and an
# alt-tabbed game still owns its VRAM.
Assert-True (
    $formPetSource -match '(?s)FullscreenScan\.BlockedMonitors\([\s\S]{0,600}?NoteFullscreenScan\(blocked\)' -and
    $startUpSource -match '(?s)internal void NoteFullscreenScan\([\s\S]{0,900}?RaiseFullscreenChanged\('
) 'the module-facing fullscreen state comes from a companion scan, and raises on change'

# ...and that asking with no pets on screen still scans rather than answering from a stale cache. With no
# pets nobody is polling, so a cached "no game running" could be arbitrarily old -- and the module would
# happily load a model into a running game.
Assert-True (
    $startUpSource -match '(?s)internal bool IsFullscreenActive[\s\S]{0,700}?FullscreenCacheLife[\s\S]{0,300}?FullscreenScan\.BlockedMonitors\('
) 'a stale fullscreen answer is refreshed on demand rather than trusted'

# The stand-down guard, asserted where a unit test cannot reach: the module's drop responder must CONSULT it,
# and the transition handler must RELEASE the model. Mutation testing found both of these silent -- the
# settings default and the subscription were covered, but nothing proved the check was wired into the decision
# or that detection actually evicted anything. A guard nobody calls is the failure this file exists to catch.
$aiBrainSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\AiBrain\AiBrainModule.cs') -Raw
function Get-MethodBody {
    param(
        [string] $Source,
        [string] $Signature,
        # Defaults to the original single terminator, so every existing caller slices exactly as before.
        # Public members and doc-commented members need their own terminators (see the ProcessIcon checks).
        [string[]] $StopAt = @("`n        private ")
    )
    $start = $Source.IndexOf($Signature)
    if ($start -lt 0) { return '' }
    # The next member declaration at method indentation ends the body. Anything else (a comment, a nested
    # block) stays inside it, which is what makes this a body and not a window.
    $next = $Source.Length
    foreach ($stop in $StopAt) {
        $at = $Source.IndexOf($stop, $start + $Signature.Length)
        if ($at -ge 0 -and $at -lt $next) { $next = $at }
    }
    return $Source.Substring($start, $next - $start)
}

# A RevealsPath action is scoped to the OWNING MODULE's storage, and the lookup's answer is what
# gets used. The decision (RevealRootFor) and the attribution (CompanionHost.ModuleOwningPane) are
# both covered by real assertions in --wpf-options-selftest and --module-host-selftest; the three
# lines BETWEEN them are not, because they need a live Program.Mainthread that a headless self-test
# does not have. Mutation-tested 2026-09-24: rewriting the call as RevealRootFor(null) -- which
# silently restores the old data-root-wide rule -- was the ONE mutation of seven that survived the
# runtime suites. This is what catches it.
#
# It asserts the ARGUMENT, not that the call is present. A check for "RevealRootFor(" alone would
# pass against the surviving mutation, which is the failure mode this whole file exists to avoid.
$revealRootBody = Get-MethodBody $optionsWindowCode 'private string PermittedRevealRoot()'
Assert-True ($revealRootBody.Length -gt 0) 'PermittedRevealRoot exists and could be sliced out for inspection'
Assert-True (
    $revealRootBody -match 'RevealRootFor\(owner\)' -and
    $revealRootBody -notmatch 'RevealRootFor\(null\)'
) 'a reveal is scoped by the owner the host resolved, not by a discarded lookup'

# Whisper: the REASON reaches the caller, and both pipes drain before the wait.
#
# Two things the runtime suites cannot see. DescribeExit is asserted directly as a pure function in
# --module-selftest=remembrance, but a mutation that swaps the CALL SITE back to the configuration
# message compiled clean and every one of those assertions still passed -- the defect stepped around
# the question they ask. And the pipe-drain order needs a real child process that blocks, which a
# self-test has no business spawning.
#
# So these assert the ARGUMENT and the ORDER in the source, not that some call is present: a check
# for "DescribeExit(" alone would pass the mutation that deleted its use.
$transcriberSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\Remembrance\Transcriber.cs') -Raw
$runWhisperBody = Get-MethodBody (Remove-LineComments $transcriberSource) `
    'private static string RunWhisper('
Assert-True ($runWhisperBody.Length -gt 0) 'RunWhisper exists and could be sliced out for inspection'
Assert-True (
    $runWhisperBody -match 'DescribeExit\(proc\.ExitCode, err\)'
) 'a non-zero whisper exit is described from its OWN stderr, not from the setup message'
Assert-True (
    # Both readers started before the wait, and neither is the blocking overload. ReadToEnd() on
    # stdout only returns at EOF, which is exit -- so the 30-minute cap below it could never fire,
    # and a child whose stderr crossed the 4 KB pipe default stopped forever.
    ([regex]::Matches($runWhisperBody, 'ReadToEndAsync\(\)').Count -eq 2) -and
    ($runWhisperBody -notmatch 'Standard(Output|Error)\.ReadToEnd\(\)')
) 'whisper''s stdout and stderr are drained CONCURRENTLY, so its timeout can actually fire'

$whisperInstallerSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\Remembrance\WhisperInstaller.cs') -Raw
$installerCode = Remove-LineComments $whisperInstallerSource
Assert-True (
    ([regex]::Matches($installerCode, 'ReadToEndAsync\(\)').Count -ge 2) -and
    ($installerCode -notmatch 'Standard(Output|Error)\.ReadToEnd\(\)')
) 'the whisper verify probe drains both pipes the same way, so its five-minute cap can fire too'

# The converter's two ffmpeg drains, same defect and same fix as the whisper pair above. Kept in the
# same shape deliberately: four call sites across three files had the identical bug, so the check that
# catches a regression in one has to catch it in all of them.
$engineSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert.Engine\Engine.cs') -Raw
$engineCode = Remove-LineComments $engineSource
Assert-True (
    ([regex]::Matches($engineCode, 'ReadToEndAsync\(\)').Count -ge 4) -and
    ($engineCode -notmatch 'Standard(Output|Error)\.ReadToEnd\(\)')
) 'the converter drains ffmpeg''s stdout and stderr CONCURRENTLY, so its timeouts can fire'
Assert-True (
    # ProbeFfmpeg used to return WaitForExit(5000) ANDed with the exit code, which leaves ffmpeg
    # running
    # for the life of the converter whenever the wait returns false.
    [regex]::Matches($engineCode, 'Kill\(true\)').Count -ge 2
) 'a converter child that outlives its timeout is killed, not abandoned'

# The per-companion size slider writes ONCE per drag.
#
# settings.json embeds the active pet's animations.xml, so it is ~1.17 MB here, and
# SetPetScalePercent persists it synchronously on the UI thread through AtomicFile. The slider snaps
# every 25 from 25 to 400, so a drag across the range used to cost 15 durable writes: about two
# seconds of frozen UI for one gesture.
#
# A real drag cannot be staged from a self-test (no UI Automation assemblies are referenced here), so
# this asserts the CONDITION rather than the behaviour: the persist inside ValueChanged must be gated
# on the drag flag, and both exits from a drag must flush. Checking merely that persistPending is
# CALLED would pass the revert, because the reverted code calls it on every tick.
$companionsPaneSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\CompanionsPaneControl.cs') -Raw
$companionsPaneCode = Remove-LineComments $companionsPaneSource
Assert-True (
    $companionsPaneCode -match 'if \(!dragging\) persistPending\(\);'
) 'the size slider persists from ValueChanged only when no drag is in progress'
Assert-True (
    ($companionsPaneCode -match 'Thumb\.DragCompletedEvent') -and
    ($companionsPaneCode -match 'Thumb\.DragStartedEvent')
) 'the size slider tracks both ends of a drag, so the deferral has a beginning and an end'
Assert-True (
    # A pane rebuilt mid-drag never sees DragCompleted, and the pane IS rebuilt on every selection.
    $companionsPaneCode -match 'Unloaded \+= delegate \{ if \(dragging\)'
) 'a drag interrupted by a pane rebuild still flushes the value the user chose'

# A backgrounded smart-picker build publishes only if it is still the CURRENT one.
#
# RebuildEngine is reachable from Init, SavePaneValues, RescanAsync, ImportPacksAsync,
# DownloadPacksAsync and RebuildSmartIndexAsync, so two builds can overlap. Without the generation
# check an earlier, slower build lands last and replaces a current picker with a stale one -- a race
# that needs two overlapping rebuilds to show, which is why it is asserted here rather than left to a
# timing-dependent test. Asserts the ORDER: the guard must come BEFORE the publish.
$fortunesModuleSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\Fortunes\FortunesModule.cs') -Raw
$fortunesModuleCode = Remove-LineComments $fortunesModuleSource
$generationCheck = $fortunesModuleCode.IndexOf('Volatile.Read(ref _smartGeneration) != generation')
$publish = $fortunesModuleCode.IndexOf('_smart = built;')
Assert-True (
    $generationCheck -gt 0 -and $publish -gt $generationCheck
) 'a superseded smart-picker build is dropped BEFORE it can replace a newer one'

$dropBody = Get-MethodBody $aiBrainSource 'private bool OnDrop(ICompanion pet)'
$pokeBody = Get-MethodBody $aiBrainSource 'private bool OnPokeReaction(ICompanion pet)'
$guardBody = Get-MethodBody $aiBrainSource 'private bool FullscreenBlocked()'
$transitionBody = Get-MethodBody $aiBrainSource 'private void OnFullscreenChanged(bool active)'
Assert-True (
    $dropBody.Length -gt 0 -and $pokeBody.Length -gt 0 -and
    $guardBody.Length -gt 0 -and $transitionBody.Length -gt 0
) 'the AI fullscreen guard, its callers and its transition handler were all found'
Assert-True (
    # the automatic paths ask before loading a model. Sliced to each method's OWN BODY rather than matched
    # by proximity: FullscreenBlocked is DEFINED immediately after OnDrop, so a distance-bounded regex matched
    # the definition and passed even with the call deleted -- caught by mutation testing, and exactly the
    # "asserts the statement exists, not that it is reached" trap.
    $dropBody -match 'FullscreenBlocked\(\)' -and
    $pokeBody -match 'FullscreenBlocked\(\)' -and
    # ...the guard reads the HOST predicate rather than guessing...
    $guardBody -match 'IsFullscreenActive' -and
    # ...and BOTH the guard and the transition release what is already resident, which is the half that
    # actually protects a game: declining to load does nothing about a model loaded before it started. The
    # guard's copy is not redundant with the transition's -- if the app starts while a game is ALREADY
    # fullscreen, no transition ever fires and the first drop is the only chance to hand the VRAM back.
    # Body-sliced for the same reason as above: ReleaseModelForFullscreen is defined next to both callers.
    $guardBody -match 'ReleaseModelForFullscreen\(\)' -and
    $transitionBody -match 'ReleaseModelForFullscreen\(\)' -and
    $aiBrainSource -match '(?s)private void ReleaseModelForFullscreen\([\s\S]{0,600}?ReleaseModelAsync\('
) 'a fullscreen app both blocks a model load and releases one already held'

# A pet must touch a screen edge with its CHARACTER, not its window. Converted pets make this load-bearing:
# the compositor sizes one cell to fit the largest pose, so a narrow walk frame floats inside it. Measured on
# the shipped corpus -- hand-authored pets are cropped tight (0px both sides) while Hornet's walk sits 175px
# from the left of its 256px cell and 22px from the right. Without the inset the pet would rest a sixth of a
# screen inland on one side and near-flush on the other, which is how it was reported ("looks inland").
#
# BOTH branches are asserted because the insets are ASYMMETRIC: correcting only one side looks fine on one
# wall and wrong on the other, and a pet that never walks left would never show it.
Assert-True (
    $formPetSource -match '(?s)left screen border[\s\S]{0,400}?PositionX = workArea\.X - ins\.Left' -and
    $formPetSource -match '(?s)right screen border[\s\S]{0,400}?PositionX = workRight - Width \+ ins\.Right' -and
    # ...and the DETECTION uses it too, not just the resting position: testing the cell edge would trigger
    # the border early on the left and late on the right.
    $formPetSource -match 'PositionX \+ ins\.Left \+ x < workArea\.X' -and
    $formPetSource -match 'PositionX \+ x \+ Width - ins\.Right > workRight'
) 'a pet meets a screen edge with its character, not its sprite cell'

# Hiding for a fullscreen app must be ENFORCED every scan, never latched. A hidden pet KEEPS TICKING, so its
# animation runs on invisibly and can reach a respawn (`spawn_ship` in the sheep). Play() then showed the form
# unconditionally while `_fullscreenHidden` stayed true, so the hide branch believed it had already hidden the
# pet and never hid it again -- a UFO permanently over a fullscreen borderless game, kept there by the very
# flag meant to prevent it. Reported from smoke testing; no existing test could see it.
#
# Asserting the ABSENCE of the latch, because the presence of the hide call proves nothing: the bug was a
# correct hide sitting behind a condition that could never become true again.
$checkFsStart = $formPetSource.IndexOf('private void CheckFullScreen()')
$checkFsEnd   = $formPetSource.IndexOf("`n        private ", $checkFsStart + 40)
if ($checkFsEnd -lt 0) { $checkFsEnd = $formPetSource.Length }
$checkFsBody  = $formPetSource.Substring($checkFsStart, $checkFsEnd - $checkFsStart)
# The POSITIVE half reads the comment-stripped body; the two NEGATIVE halves read the raw body.
# That asymmetry is deliberate and is the whole correction. A comment containing
# `if (Visible) Visible = false;` would satisfy the positive half over code that no longer hides at
# all, so it must not see comments. But a comment merely MENTIONING `_fullscreenHidden` must not fail
# a -notmatch, because prose about a latch is not a latch -- stripping there would fail correct code,
# which is how this file's OptionsWindow check false-red four times in one session.
$checkFsStripped = Remove-LineComments $checkFsBody
Assert-True (
    $checkFsStart -gt 0 -and
    $checkFsBody -notmatch 'else if \(!_fullscreenHidden\)' -and
    $checkFsStripped -match 'if \(Visible\) Visible = false;'
) 'hiding for a fullscreen app is enforced every scan, not latched behind a flag'

# The z-order walk is shared. It used to be throttled PER COMPANION, so one desktop-wide answer was
# recomputed once per companion per cycle -- 16 walks at MAX_SHEEPS, and a walk costs 781
# user32/dwmapi calls whenever no window covers some monitor's centre and the early exit cannot fire.
# Asserted in both directions, because either half alone is satisfiable by broken code: the tick path
# must READ the shared answer, and it must not keep its own walk beside it.
#
# The cache has to hold the PER-MONITOR ARRAY. Collapsing it to "a game is running somewhere" would
# hide a companion on monitor 2 because monitor 1 has a game -- and "anything visible over a
# fullscreen game" is already on SMOKETEST.md's regression watchlist in the other direction, so this
# is the half where a plausible optimisation re-ships a shipped bug. `_fullscreenBlocked = blocked`
# is what keeps per-monitor per-monitor; drop it and the field stays null for ever, which reads as
# "no scan has succeeded" and skips the stand-down on every tick.
Assert-True (
    $checkFsStart -gt 0 -and
    $checkFsBody -match 'BlockedMonitorsForStandDown\(' -and
    $checkFsBody -notmatch 'FullscreenScan\.BlockedMonitors\(' -and
    $startUpSource -match '(?s)internal void NoteFullscreenScan\([\s\S]{0,400}?_fullscreenBlocked = blocked;' -and
    $startUpSource -match '(?s)internal bool\[\] BlockedMonitorsForStandDown\([\s\S]{0,400}?FullscreenScanInterval[\s\S]{0,200}?FullscreenScan\.BlockedMonitors\('
) 'the fullscreen z-order walk is shared per cycle, and what it caches is per MONITOR'

# ...and the half that pays for that: the ENFORCEMENT must still run on every tick. The scan may be
# throttled; the correction may not. A hidden companion keeps ticking and can reach a respawn that
# shows its window again, which is the bug above -- so re-introducing a per-instance time gate here
# would resurrect it with a timestamp in place of the bool, and would also push the stand-down out to
# two cycles (the companion's own gate plus the shared one) rather than the one it has always been.
#
# An ORDER assertion, not a presence one: the relocation rate-limit legitimately compares elapsed
# milliseconds, so what is forbidden is any such comparison BEFORE the scan is consulted. Comments are
# stripped first, or the paragraph explaining the rule would satisfy it.
$checkFsCode     = Remove-LineComments $checkFsBody
$standDownScanAt = $checkFsCode.IndexOf('BlockedMonitorsForStandDown(')
$firstElapsedAt  = $checkFsCode.IndexOf('TotalMilliseconds')
Assert-True (
    $standDownScanAt -ge 0 -and
    ($firstElapsedAt -lt 0 -or $firstElapsedAt -gt $standDownScanAt) -and
    $formPetSource -notmatch '_lastFullscreenScanUtc'
) 'the fullscreen stand-down is enforced every tick, not re-throttled per companion'

# ...and a RESPAWN must not walk back onto a blocked monitor at all. Correcting it one tick later is a visible
# flash; before the enforcement fix it was permanent. Play() has to decide at spawn time.
Assert-True (
    $formPetSource -match '(?s)private bool MonitorIsBlockedNow\([\s\S]{0,900}?FullscreenScan\.BlockedMonitors\(' -and
    $formPetSource -match 'Visible = !spawningOntoBlockedMonitor;' -and
    $formPetSource -match 'TopMost = !spawningOntoBlockedMonitor;' -and
    $formPetSource -match '_fullscreenHidden = spawningOntoBlockedMonitor;' -and
    # A CHILD is a separate window shown by its own code path and inherits nothing from a hidden parent --
    # the UFO ship is one. It has to ask the same question.
    $formPetSource -match 'Visible = !childOntoBlockedMonitor;' -and
    # ...and grabbing a pet must not be a way to force it back over a game. Asserting the CONDITION is
    # present, not the absence of the old adjacent pair: a comment between the two lines defeats an
    # adjacency pattern, which is exactly how this assertion first came back silent under mutation.
    $formPetSource -match 'TopMost = hwndFullscreenWindow == IntPtr\.Zero;'
) 'a respawning pet, a child, or a grabbed pet cannot appear over a fullscreen app'

# A pet pinned to a monitor must (a) spawn there rather than on a random screen, and (b) HIDE rather than
# relocate when a fullscreen app takes that screen -- "Hornet on monitor 2" is an instruction, not a
# preference to override the first time a game starts. An UNPINNED pet keeps the old behaviour and moves to a
# free monitor instead of vanishing. Neither half had any coverage when first written.
Assert-True (
    # the spawn consults the pin, and the pin outranks both the random pick and a pending relocation
    $formPetSource -match '(?s)int pinned = PinnedDisplay;[\s\S]{0,300}?if \(pinned >= 0\)[\s\S]{0,120}?DisplayIndex = pinned;' -and
    # ...and a pinned pet is excluded from relocation, so it hides instead
    $formPetSource -match 'int target = \(isChild \|\| PinnedDisplay >= 0\)' -and
    # ...and the pin is read per TYPE from settings, not invented locally
    $formPetSource -match '(?s)private int PinnedDisplay[\s\S]{0,1400}?GetPetMonitor\(' -and
    # ...via PetTypeId, NOT the petEntries registry. AddSheepCore calls Play() inside its initialize callback
    # and registers the pet in petEntries only AFTERWARDS, so a registry lookup at spawn time misses and
    # falls back to the ACTIVE pet's id. That shipped: a pet pinned to screen 2 spawned on screen 1.
    $formPetSource -match '(?s)private int PinnedDisplay[\s\S]{0,1400}?string typeId = PetTypeId;' -and
    $formPetSource -notmatch '(?s)private int PinnedDisplay[\s\S]{0,1400}?PetTypeIdOf\('
) 'a pinned pet reads its type from PetTypeId, which is set before Play, not from the registry'
# The VELOCITY fields must go through ScaleVelocity, not ScaleD. ScaleVelocity is unit-tested and correct, but
# a correct function nobody calls is exactly the failure this file exists to catch -- mutation testing found
# the wiring unguarded while the maths was covered. OffsetY deliberately stays on ScaleD: it is a position
# offset, and scaling it to zero is right.
$animationsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Animations.cs') -Raw
Assert-True (
    $animationsSource -match 'ScaleVelocity\(Start\.X\.GetRawValue' -and
    $animationsSource -match 'ScaleVelocity\(Start\.Y\.GetRawValue' -and
    $animationsSource -match 'ScaleVelocity\(End\.X\.GetRawValue' -and
    $animationsSource -match 'ScaleVelocity\(End\.Y\.GetRawValue' -and
    # ...and no velocity field slipped back onto the rounding-to-zero path
    $animationsSource -notmatch 'ScaleD\((Start|End)\.(X|Y)\.GetRawValue' -and
    # ...while NEITHER position offset drifts onto the velocity path. Asserting the absence, because matching
    # just one ScaleD(...UnscaledOffsetY) passed while the other had already been switched -- mutation
    # testing caught exactly that.
    $animationsSource -notmatch 'ScaleVelocity\((Start|End)\.UnscaledOffsetY' -and
    $animationsSource -match 'ScaleD\(Start\.UnscaledOffsetY' -and
    $animationsSource -match 'ScaleD\(End\.UnscaledOffsetY'
) 'velocities scale through ScaleVelocity so a moving animation never freezes at small sizes'
# SHA256SUMS.txt must name files the way a DOWNLOADER sees them. Release assets are flat, so a build-tree
# path in the checksum file makes `sha256sum -c` report a missing file on a release whose own notes tell you
# to verify against it. Asserting the absence of the raw path too: printing $file is the exact regression.
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml') -Raw
Assert-True (
    $releaseWorkflow -match '\$name = \[System\.IO\.Path\]::GetFileName\(\$file\)' -and
    $releaseWorkflow -match 'ToLowerInvariant\(\)\)  \$name"' -and
    $releaseWorkflow -notmatch 'ToLowerInvariant\(\)\)  \$file"'
) 'SHA256SUMS.txt lists bare download filenames, so verifying a downloaded release actually works'
Assert-True (
    # ...and is written with LF. Set-Content on Windows writes CRLF, which GNU `sha256sum -c` reads as part
    # of the filename, so every line fails to verify even though every hash is correct. Asserting the WRITE
    # CALL and the absence of the Set-Content form, because a comment about line endings guards nothing.
    $releaseWorkflow -match '\[System\.IO\.File\]::WriteAllText\(' -and
    $releaseWorkflow -match '\$lines -join "`n"' -and
    $releaseWorkflow -notmatch '\$lines \| Set-Content'
) 'SHA256SUMS.txt is written with LF endings, so sha256sum -c can actually read the filenames'

# The installer must NOT carry MsiLogging. It reads like a helpful default and is the opposite: it makes
# Windows Installer open a log file on every install, uninstall and repair, so on any machine where %TEMP%
# is not writable the user gets "Error opening installation log file" instead of a working install. That
# dialog was reported once already, from a management agent holding a transaction, and the package was
# clean -- adding this property is exactly the change that would make it ours and make it permanent.
# Opt-in logging is documented in SUPPORT.md instead.
$installerWxs = Get-Content -LiteralPath (
    Join-Path $repoRoot 'installer\DesktopAICompanion.wxs') -Raw
# Asserted on the parsed XML, not the text. Remove-LineComments is a C# `//` stripper: it did nothing
# for an XML `<!-- -->` comment (so a commented-out MsiLogging element failed this) and mangled the
# xmlns URLs for nothing (F409). No element may carry MsiLogging as its Id or Name; a comment may say
# whatever it likes.
[xml]$installerWxsDocument = $installerWxs
Assert-True (
    @($installerWxsDocument.SelectNodes("//*[@Id='MsiLogging' or @Name='MsiLogging']")).Count -eq 0
) 'the installer does not force Windows Installer logging on every run'
Assert-True (
    # ...and the escape hatch is actually written down, so "no logging" is a documented choice rather than
    # an omission someone later fixes by adding the property.
    (Get-Content -LiteralPath (Join-Path $repoRoot 'SUPPORT.md') -Raw) -match '/l\*v'
) 'SUPPORT.md tells a user how to produce an installer log on demand'

# The debug stream must reach a FILE, not only the debug window. AddDebugInfo returns early when that
# window is closed, so on an ordinary run all 57 call sites were discarded -- which is why an intermittent
# missing tray icon left no evidence at all and cost a whole investigation. The window is the live view;
# the log is the record, and the record is the half that exists when nobody predicted the fault.
$startUpText = Remove-LineComments $startUpSource
$diagSource = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\DiagnosticLog.cs') -Raw)
$addDebugBody = Get-MethodBody $startUpText 'public static void AddDebugInfo(' @(
    "`n        private ", "`n        public ", "`n        internal ")
$writeAt  = $addDebugBody.IndexOf('DiagnosticLog.Write(')
$windowAt = $addDebugBody.IndexOf('ShowInDebugWindow(')
$returnAt = $addDebugBody.IndexOf('return')
Assert-True (
    # ORDER and UNREACHABILITY, not presence. The window half returns early when no debug window is open, so
    # a DiagnosticLog.Write placed after it is dead on exactly the runs that need it -- while still reading
    # as correctly wired. Asserting three things: the write happens, it happens before the window hand-off,
    # and no return precedes it. The third is what stops a future guard clause quietly re-creating the bug.
    $writeAt -ge 0 -and $windowAt -gt $writeAt -and ($returnAt -lt 0 -or $returnAt -gt $writeAt)
) 'AddDebugInfo writes to the log first, with nothing able to return before it'
$settingsSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\AppSettingsStore.cs') -Raw
Assert-True (
    # The property that matters is "the previous run survives a restart", because a missing tray icon leaves
    # the app unreachable and the first reaction is to restart it -- destroying the only record. That is now
    # expressed as N-file rotation with a default of 2, so BOTH halves are asserted: the rotation shifts
    # files rather than truncating, and the shipped default keeps more than just the current one.
    $diagSource -match 'File\.Move\(Current\(i - 1\), Current\(i\)\)' -and
    $settingsSource -match 'DiagnosticLogKeep = ([2-9]|[1-9][0-9])\s*,'
) 'the diagnostic log rotates and keeps at least the previous run by default'
$diagWriteBody = Get-MethodBody $diagSource 'internal static void Write(' @(
    "`n        private ", "`n        public ", "`n        internal ")
Assert-True (
    # Write must DELEGATE its filtering to IsEnabled rather than repeat it. Both once carried the same three
    # checks, and because the self-tests can only reach IsEnabled, deleting the per-module check inside Write
    # left every assertion green while muted modules kept writing -- a mutation proved exactly that. So this
    # asserts the delegation exists AND that no private copy of the state has grown back beside it. The
    # directory check is the one thing Write is allowed to decide alone: it is about the file, not the policy.
    $diagWriteBody -match 'IsEnabled\(category, moduleId\)' -and
    $diagWriteBody -notmatch '_mutedCategories' -and
    $diagWriteBody -notmatch '_mutedModules' -and
    $diagWriteBody -notmatch '_enabled'
) 'DiagnosticLog.Write filters through IsEnabled and keeps no second copy of the rules'
$levelEnumBody = [regex]::Match($startUpText, 'enum DEBUG_TYPE\s*\{(?<b>[^}]*)\}').Groups['b'].Value
$longestLevel = ([regex]::Matches($levelEnumBody, '(?m)^\s*(\w+)\s*=\s*\d+') |
    ForEach-Object { $_.Groups[1].Value.Length } | Measure-Object -Maximum).Maximum
$levelPadWidth = [int] [regex]::Match(
    $diagSource, 'level \?\? "info"\)\.PadRight\((?<n>\d+)\)').Groups['n'].Value
Assert-True (
    # Derived from the enum, not hard-coded: the level column has to be WIDER than the longest level name,
    # not equal to it. It was PadRight(7) against a 7-character "warning", so every warning in a real log
    # came out as "warningApp" with the level welded to the category. Comparing the two numbers is the only
    # form of this check that survives someone adding a longer DEBUG_TYPE later.
    $longestLevel -gt 0 -and $levelPadWidth -gt $longestLevel
) 'the diagnostic log level column is wider than the longest level name'
# Read once, above the assertion: an assignment inside an Assert-True (...) expression is a parser
# error, and $processIconSource is defined further down the file (a forward reference throws under
# Set-StrictMode).
$processIconLogLine = (Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\ProcessIcon.cs') -Raw))
Assert-True (
    # The tray path must say what happened on EVERY run, not only when it fails.
    #
    # Strengthened in 1.1.1, because the previous form of this check asserted only that a log line
    # EXISTED and was itself satisfied by the line that hid BUG-001 for two sessions. That line read
    # "success=True" whenever SetIcon's try block did not throw, which is not the same as the shell
    # having accepted the icon -- so a dropped NIM_ADD and a working one logged identically. The
    # invariant's own comment already described that exact failure while the regex it used could not
    # detect it.
    #
    # What is required now is the SHELL's verdict, which is the only thing that distinguishes the two:
    # 'shellHasIt=' carries Shell_NotifyIcon(NIM_MODIFY)'s answer (True/False/unknown).
    # Read inline rather than reusing $processIconSource: that is defined further down the file, and under
    # Set-StrictMode a forward reference throws rather than evaluating to empty.
    ($processIconLogLine -match 'tray icon set: noThrow=') -and
    ($processIconLogLine -match 'shellHasIt=')
) 'SetIcon records the SHELL''s verdict, not merely that the call did not throw'

# The converted-author string is DUPLICATED across two assemblies that cannot reference each other: the
# converter stamps it (PetEmitter.ConvertedAuthor) and the app reads it back to decide whether a pet's
# <version> is the author's own or the converter's format number. If the two ever drift, nothing throws --
# IsConvertedAuthor just quietly returns false for every pet and the About dialog goes back to calling a
# migration gate "Version:". Compare the LITERALS, extracted from both files, rather than trusting that
# whoever edits one remembers the other.
$emitterAuthor = ([regex]::Match(
    (Get-Content -LiteralPath (Join-Path $repoRoot 'tools\ShimejiConvert.Engine\Emit\PetEmitter.cs') -Raw),
    'ConvertedAuthor\s*=\s*"([^"]+)"')).Groups[1].Value
$appAuthor = ([regex]::Match(
    (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\CompanionCatalog.cs') -Raw),
    'ConvertedAuthor\s*=\s*"([^"]+)"')).Groups[1].Value
Assert-True (
    $emitterAuthor.Length -gt 0 -and $appAuthor.Length -gt 0 -and $emitterAuthor -ceq $appAuthor
) 'the app and the converter agree on the converted-author string, character for character'

$aboutSource = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\AboutWindow.cs') -Raw)
Assert-True (
    # CONDITION, not presence: the row has to be chosen by provenance. Asserting only that the string
    # "Converter format:" appears would still pass if someone hard-coded it for every pet, which would
    # mislabel the hand-authored sheep whose <version> really is the author's own.
    $aboutSource -match 'IsConvertedAuthor\(author\)\s*\?\s*"Converter format:"\s*:\s*"Version:"'
) 'the About card labels the version row by provenance, not unconditionally'

# Every tray surface that holds only a pet ID must resolve it through DisplayNameForId, which reads the
# pet's own header. DisplayName(id, null) has no catalog name to consult and falls through to the prettified
# folder id, so "Remove a pet" and "Pet Speech" read "Shimeji 3x56f4pl" while "Add a pet" -- which enumerates
# and therefore HAS the header -- read "Monkey D. Luffy" for the same pet. Asserting the absence too, because
# reintroducing the null argument is the exact regression and the resolver would still be sitting there.
$contextMenusSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\ContextMenus.cs') -Raw
Assert-True (
    $contextMenusSource -match 'CompanionCatalog\.DisplayNameForId\(id\)' -and
    $contextMenusSource -notmatch 'CompanionCatalog\.DisplayName\(\s*id\s*,\s*null\s*\)'
) 'the tray resolves a pet id to its friendly name, not to a prettified folder id'

# Windows 11 hides a tray icon whose HKCU NotifyIconSettings entry has no IsPromoted value, so a fresh
# install reads as "the pet is on screen but there is no tray icon" -- the icon is registered and working,
# just behind the chevron among thirty others. Two things have to hold in SetIcon, and both are ORDER or
# CALL-SITE facts that a passing "the code is present" check would miss entirely.
$processIconSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\ProcessIcon.cs') -Raw
$setIconBody = Remove-LineComments (Get-MethodBody $processIconSource 'public void SetIcon(' @(
    "`n            /// <summary>", "`n        public ", "`n        private "))
$textAssign = $setIconBody.IndexOf('ni.Text =')
$iconAssign = $setIconBody.IndexOf('ni.Icon = replacement')
Assert-True (
    $setIconBody.Length -gt 0 -and $textAssign -ge 0 -and $iconAssign -ge 0
) 'SetIcon was sliced and assigns both the tray text and the tray icon'
Assert-True (
    # THE ORDER IS THE INVARIANT. WinForms only issues the Shell_NotifyIcon NIM_ADD once an icon exists --
    # Display() sets Visible with a null Icon, which adds nothing -- and Windows 11 permanently caches the
    # tooltip carried by that first ADD. Assigning the icon first burns whatever the text happened to be in
    # as the label forever, which is what the user reads when hunting the flyout. Both statements are
    # present either way, so only their relative position can catch a regression here.
    $textAssign -lt $iconAssign
) 'SetIcon sets the tray text BEFORE the icon, so the first NIM_ADD carries the right label'
Assert-True (
    # The label must be the CONSTANT app name, not a per-pet string. Windows keys the entry on the
    # executable and caches one label per path, so a pet name there describes whichever pet happened to be
    # the default and misdescribes every other type on screen. Asserting the absence of the old
    # concatenation too, because reintroducing it is the exact regression and a constant would still be
    # sitting there next to it.
    $setIconBody -match 'ni\.Text = TrayDisplayName;' -and
    $setIconBody -notmatch 'petName \+ " Desktop Pet"'
) 'the tray label is the constant app name, not the active pet name'
Assert-True (
    # Matched as a CALL, on comment-stripped source: a bare identifier match is satisfied by the prose above,
    # which is how four earlier absence checks in this repo passed against deliberately broken code.
    $setIconBody -match 'TrayPromotion\.PromoteOnce\('
) 'SetIcon asks for the tray icon to be promoted out of the hidden-icons flyout'
$trayPromotionSource = Remove-LineComments (
    Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\TrayPromotion.cs') -Raw)
Assert-True (
    # Promotion must be conditional on the value being ABSENT. Windows writes 0 when the user drags the icon
    # back into the flyout, so treating 0 as "promote" would have the pet overrule the user on every launch.
    $trayPromotionSource -match 'if\s*\(storedIsPromoted == null\) return true;' -and
    $trayPromotionSource -notmatch 'Registry\.LocalMachine'
) 'promotion is gated on an absent preference and never leaves HKCU'

# ...and the resolver must actually READ the header. Asserting the call site, not just the function, because
# a build output ships no bundled pets, so a runtime assertion over installed pets passes vacuously on a
# clean runner -- the same "a correct function nobody wires up" hole that let ScaleVelocity ship unguarded.
$petCatalogSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\CompanionCatalog.cs') -Raw
Assert-True (
    $petCatalogSource -match 'string resolved = DisplayName\(id, ReadHeaderNameForId\(id\)\);' -and
    $petCatalogSource -match 'private static string ReadHeaderNameForId\(string id\)' -and
    $petCatalogSource -match 'return ReadHeaderName\(path\);'
) 'the id-only resolver reads the pet header rather than falling straight to the folder id'

# Replacing a pet's animations.xml must drop every per-id cache keyed off it.
#
# Two caches are process-lifetime and neither expires: CompanionCatalog's header-name cache (which feeds every tray
# menu) and the Pets pane's animation/sound counts. Both were written when a pet could only be replaced by a
# download that restarted the app. Once an update is applied in-process, a stale entry means the tray keeps
# showing the OLD pet's name and the card keeps showing the OLD counts, with nothing to make it expire.
#
# Asserted at the WRITE site rather than by the existence of the two Forget methods: a cache-clearing method
# nobody calls is the failure this file exists to catch, and it has already happened twice in this repo.
#
# Re-pointed 2026-09-30 by lane burn/host-shell (RA-314): since F249/F336 the pane's static constructor
# subscribes ForgetStats to CompanionCatalog.Forgotten (asserted further down), so the ONE call every writer
# makes reaches all three caches, and the explicit ForgetStats this used to require beside it was a second
# copy of the rule that this pin made the correct cleanup fail the gate for. Comment-stripped, so the
# comment that names the retired call cannot satisfy or trip the absence half.
$petsPaneSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Portable\Wpf\CompanionsPaneControl.cs') -Raw
$petsPaneCode = Remove-LineComments $petsPaneSource
$fetchIndex = $petsPaneCode.IndexOf('SecureDownload.WriteAllBytesAtomic(Path.Combine(directory, "animations.xml"), bytes);')
$afterWrite = if ($fetchIndex -ge 0) { $petsPaneCode.Substring($fetchIndex, [Math]::Min(1200, $petsPaneCode.Length - $fetchIndex)) } else { '' }
Assert-True (
    $fetchIndex -ge 0 -and
    $afterWrite -cmatch 'CompanionCatalog\.Forget\(pet\.Id\);' -and
    $afterWrite -cnotmatch 'ForgetStats\('
) 'replacing a pet file drops its cached display name and stats through the one Forget call, whose Forgotten listeners reach the pane caches'

# Reloading a pet type must DISPLACE the cached parse, not re-add and hope.
#
# The obvious implementation is what the tray already does, RemoveOnePet then AddSheep, and it silently does
# nothing: KillSheep frees the sheeps[] slot immediately but registry.Decrement waits for FormClosed behind
# the kill animation, so an immediate re-add still finds RefCount > 0, ResolveExtraType hits the CACHED parse
# and the OLD skin comes back. The user sees no change and no error. Only registry.Add displaces it safely.
#
# Every claim here is about call ORDER inside one method, which no unit test can observe; the registry
# primitive itself is already covered by --pettyperegistry-selftest.
$reloadStart = $startUpSource.IndexOf('internal CompanionReloadOutcome ReloadPetType(')
$reloadEnd = $startUpSource.IndexOf('/// <summary>Remove one specific pet instance', [Math]::Max(0, $reloadStart))
$reloadBody = if ($reloadStart -ge 0 -and $reloadEnd -gt $reloadStart) {
    $startUpSource.Substring($reloadStart, $reloadEnd - $reloadStart)
} else { '' }

Assert-True ($reloadBody.Length -gt 0) 'ReloadPetType exists and could be sliced out for inspection'
Assert-True ($reloadBody -match 'registry\.Add\(target, stagedXml, stagedAnimations\)') (
    'a reload displaces the cached parse, instead of respawning the old skin from it')
Assert-True ($reloadBody -match 'AnyPetBusy\(\)') (
    'a reload refuses while a pet is being dragged, which RemoveOnePet does not check for itself')
# Kill before spawn, or a reload at MAX_SHEEPS spawns nothing and quietly loses pets.
Assert-True (
    $reloadBody.IndexOf('RemoveOnePet(target)') -gt 0 -and
    $reloadBody.IndexOf('AddSheepCore(fresh.Xml') -gt $reloadBody.IndexOf('RemoveOnePet(target)')
) 'a reload kills before it spawns, so it cannot exceed the pet cap half way through'
# Staging must precede any teardown, so a bad file leaves the pets alone.
Assert-True (
    $reloadBody.IndexOf('TryStageRuntime(') -gt 0 -and
    $reloadBody.IndexOf('TryStageRuntime(') -lt $reloadBody.IndexOf('RemoveOnePet(target)')
) 'a reload validates the new definition before closing anything'

# Every respawn would otherwise re-announce the pet to modules, so four copies of an updated skin would
# fire four welcomes for pets the user never saw leave.
Assert-True (
    $startUpSource -match 'reloadInProgress = true;' -and
    $startUpSource -match '!reloadInProgress\)'
) 'a reload does not announce its respawns to modules as new arrivals'

# The ACTIVE pet's live definition is in settings.json, not the library folder, so a swap cannot work and
# the in-process alternative tears down the whole desktop. It must ask for a restart instead.
# The absence half matches the CALL form with its parenthesis. The method's own doc comment names
# LoadNewXMLFromString to explain why it is not used, and matching the bare identifier made this guard fail
# against correct code -- the third time in this file that an absence check has been defeated by prose that
# describes the very thing it forbids.
Assert-True (
    $reloadBody -match 'CompanionReloadOutcome\.NeedsRestart' -and
    $reloadBody -notmatch 'LoadNewXMLFromString\('
) 'the active pet asks for a restart rather than triggering a whole-desktop reload'

# ...and the pane has to actually call it, right where the file was replaced.
Assert-True ($afterWrite -match 'ReloadOnScreen\(') (
    'replacing a pet file reloads any copies of it that are on screen')

# The weekly content checks must be ARMED and their results must be SURFACED.
#
# Every part of this is a wiring claim that a unit test cannot see. The rule itself (ShouldCheck) is pure and
# tested in --module-host-selftest; what breaks silently is the plumbing around it.
$startUpSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\StartUp.cs') -Raw

# ArmModuleUpdateCheck used to live INSIDE the module try/catch and behind `loadedModules > 0`, so a
# module-host failure took the update check with it, and a pets check could never have run at all with zero
# modules installed. Assert all three sit together, after the catch.
$armBlock = [regex]::Match(
    $startUpSource,
    'catch \(Exception moduleEx\).*?ArmAppUpdateCheck\(\);\s*ArmModuleUpdateCheck\(\);\s*ArmPetUpdateCheck\(\);',
    'Singleline')
Assert-True ($armBlock.Success) 'all three update checks are armed outside the module try/catch, not gated on modules loading'
Assert-True ($startUpSource -notmatch 'if \(loadedModules > 0\) ArmModuleUpdateCheck\(\)') (
    'the module check is no longer skipped when no modules are installed')

# The seed-without-checking branch is the bug AppUpdateCheck documents: it stamped "I looked" on a
# fresh install without looking, so a new install stayed blind for a whole interval.
#
# This used to assert that the source did NOT CONTAIN THE ENGLISH PHRASE 'seed the month WITHOUT
# checking'. The only input that failed it was someone re-adding a comment with that exact wording;
# re-introducing the actual defect under any other comment, or none, passed. This file warns about
# precisely that class four separate times, and this was the one place the warning was not applied.
#
# Now it asserts the ORDER of the real statements, in the file that actually does the work:
# AppUpdateCheck.MaybeCheckAsync must FETCH before it STAMPS. Reorder those two and this fails;
# reword every comment in the repo and it does not. Both subjects are asserted present first, so
# renaming either cannot turn this into a pass on an absent pair.
#
# ON COMMENT-STRIPPED CODE, SLICED TO THE METHOD (RA-362). The paragraph above was true of the
# statements and false of the check: it read the raw file and took file-wide IndexOf pairs, so a
# comment naming FetchAppVersionAsync above a stamp moved ahead of the fetch -- "FetchAppVersionAsync
# can throw, so stamp the attempt first" -- would have put $fetchIndex first and passed the very
# regression it exists to catch, exactly what this file records happening to its poke-sass check.
# Judged inside MaybeCheckAsync (the interval overload, where both statements live) so a second use of
# either name elsewhere in the file cannot decide the order either.
$appUpdateCode = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\AppUpdateCheck.cs') -Raw)
$maybeCheckBody = Get-MethodBody $appUpdateCode 'internal static async Task<bool> MaybeCheckAsync(' @(
    "`n        internal ", "`n        private ", "`n        public ", "`n    }")
Assert-True ($maybeCheckBody.Length -gt 0) 'AppUpdateCheck.MaybeCheckAsync (the interval overload) was located'
$fetchIndex = $maybeCheckBody.IndexOf('FetchAppVersionAsync')
$stampIndex = $maybeCheckBody.IndexOf('SetAppUpdateResult')
Assert-True ($fetchIndex -ge 0) (
    'AppUpdateCheck still fetches the catalog version, so this ordering invariant has a subject')
Assert-True ($stampIndex -ge 0) (
    'AppUpdateCheck still stamps a result, so this ordering invariant has an object')
Assert-True ($fetchIndex -lt $stampIndex) (
    'the app update check FETCHES before it stamps that it checked' +
    " (fetch at $fetchIndex, stamp at $stampIndex)")

# Discarding the result is what made the previous module check useless to the pane: it raised a balloon and
# threw the offers away, so opening Modules still knew nothing.
Assert-True (
    $startUpSource -match 'SetModuleUpdateResult\(' -and
    $startUpSource -match 'SetPetUpdateResult\('
) 'both content checks write their result down, so a pane can render it without a network call'

# A pane is rebuilt on every selection, so its constructor IS "on open". Without these the panes fetch
# nothing and no update can appear until the user presses a button, which is the whole complaint.
foreach ($pane in @('CompanionsPaneControl', 'ModulesPaneControl')) {
    $paneSource = Get-Content -LiteralPath (Join-Path $repoRoot "src\Portable\Wpf\$pane.cs") -Raw
    Assert-True (
        $paneSource -match 'RefreshCatalogOnOpen\(\);' -and
        $paneSource -match 'private async void RefreshCatalogOnOpen\(\)'
    ) "$pane refreshes itself when it opens rather than waiting for a button"
    # A user-initiated check must not be served the shared copy, or the button appears to do nothing.
    # Since F286 the pane calls RefreshSharedAsync, which drops the copy and REFILLS it with what the
    # check finds; the lane's own invariant below asserts the refill, this one keeps asserting the drop.
    Assert-True ($paneSource -match 'RemoteCatalogClient\.(InvalidateShared\(\);|RefreshSharedAsync\()') (
        "$pane drops the shared catalog when the user asks to check now")
}

# The message loop must run WITH a main form.
#
# Application.Run() with no argument runs a loop nothing can end: closing every window leaves the process
# alive. Measured against the installed build with modules loaded, that meant Restart Manager asked the app
# to close, got no result 32 seconds later, and the installer stopped on "unable to automatically close all
# requested applications" -- after closing the windows it could reach, which took the tray icon with them and
# left pets on screen with no way to quit. With AppLifetime as the main form the same probe exits in ~316ms.
#
# Asserting the absence of the bare call as well as the presence of the form: adding a second bare
# Application.Run() somewhere else would reintroduce exactly this, and the guard would otherwise still pass.
$programSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Program.cs') -Raw
# The absence half matches the STATEMENT form, with its semicolon. A bare `Application.Run()` also appears
# in a comment a few lines up, and matching the bare call alone made this guard fail against correct code.
Assert-True (
    $programSource -match 'Application\.Run\(lifetime\)\s*;' -and
    $programSource -notmatch 'Application\.Run\(\s*\)\s*;'
) 'the message loop runs with a main form, so a close request can actually end the process'

# The welcome/exit sidebar must keep a LIGHT content panel where WixUI puts its controls.
#
# WixUI lays every control of those dialogs over one full-bleed bitmap. Title, description and the optional
# text are Transparent so they sit over artwork happily, but ExitDialog's OptionalCheckBox is not (attrs=2):
# it paints its own 220x40 rectangle in the dialog background colour. Against WiX's own pale default bitmap
# that is invisible. Against full-bleed pasture art it was reported as "a large white box around the text".
#
# So the art carries the constraint, and only the art can. Sampling the region the checkbox occupies is the
# cheapest way to stop a future redesign quietly reintroducing the slab.
$dialogBmpPath = Join-Path $repoRoot 'installer\dialog.bmp'
Add-Type -AssemblyName System.Drawing
$dialogBmp = [System.Drawing.Bitmap]::FromFile($dialogBmpPath)
try {
    # x=136..355 of 370 dialog units, y=190..230 of 234: where OptionalCheckBox lands.
    $sampleX0 = [int](136.0 / 370.0 * $dialogBmp.Width)
    $sampleX1 = [int](355.0 / 370.0 * $dialogBmp.Width)
    $sampleY0 = [int](190.0 / 234.0 * $dialogBmp.Height)
    $sampleY1 = [int](230.0 / 234.0 * $dialogBmp.Height)
    # The sample window must be non-empty BEFORE the loop, because $darkest is seeded to 255 and the
    # assertion below is '-ge 216' -- so the seed IS the pass value. Any bitmap narrower than about
    # 3px in either axis makes the loop body never run and the check pass on no evidence at all.
    # Counting the samples is what turns that from a silent pass into a failure.
    Assert-True (($sampleX1 -gt $sampleX0) -and ($sampleY1 -gt $sampleY0)) (
        "the sidebar sample window is non-empty (x $sampleX0..$sampleX1, y $sampleY0..$sampleY1)")
    $darkest = 255
    $sampled = 0
    for ($sx = $sampleX0; $sx -lt $sampleX1; $sx += 3) {
        for ($sy = $sampleY0; $sy -lt $sampleY1; $sy += 3) {
            $pixel = $dialogBmp.GetPixel($sx, $sy)
            $sampled++
            foreach ($channel in @($pixel.R, $pixel.G, $pixel.B)) {
                if ($channel -lt $darkest) { $darkest = $channel }
            }
        }
    }
    Assert-True ($sampled -gt 0) (
        "the sidebar check actually read pixels rather than passing on its seed ($sampled sampled)")
    # COLOR_BTNFACE is 240,240,240. 24 allows a whisper of background tint without letting real art back in.
    Assert-True ($darkest -ge 216) (
        "the installer sidebar keeps a light panel behind the non-transparent checkbox (darkest channel $darkest, needs >= 216)")
}
finally { $dialogBmp.Dispose() }

# Signing must stay OPT-IN, and must sign in the one position the pipeline allows.
#
# All of this is scaffolding for a certificate that does not exist yet, which is exactly when it can rot
# unnoticed: nothing exercises the signed path on a normal build, so a mistake here surfaces on the first
# release that tries to use it.
$buildScript = Get-Content -LiteralPath (Join-Path $repoRoot 'build.ps1') -Raw
$installerScript = Get-Content -LiteralPath (Join-Path $repoRoot 'installer\build-installer.ps1') -Raw
$signtoolScript = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging\Invoke-Signtool.ps1') -Raw

# Opt-in on BOTH scripts. build.yml runs both on every pull request with no certificate, so an unguarded
# call would fail every PR.
# THE CONDITION, not the presence of the words. This asserted only that the two strings appeared
# somewhere in the file, which survives dropping the `-not` -- `IsNullOrWhiteSpace($x)` still matches
# `if (IsNullOrWhiteSpace($x))`, so every PR build would call signtool with an EMPTY thumbprint while
# this check stayed green. It also survives moving the signtool call out of the guarded block and
# leaving the block behind. Found by an audit 2026-09-17; nothing is mis-signed today only because no
# certificate exists, which is the same reason the rot was invisible.
#
# What is required now: EVERY signtool invocation must sit inside the then-block of a guard on
# `-not [string]::IsNullOrWhiteSpace($SigningCertThumbprint)` -- proven by the AST, not by comparing
# two string offsets.
#
# Position was not enough, twice over. The old form asserted only `guard.Index -lt callAt`, so
# nothing located the guard's CLOSING brace: close the block early, move the call below it, and the
# guard still opened first. All three assertions passed over a script that signed unconditionally.
# And `$callAt` was an IndexOf, so a SECOND unguarded call added later was invisible behind the
# first. The AST answers both: a then-block has an extent with an end offset, and every CommandAst
# can be enumerated rather than just the first.
#
# It also lets the comment-stripping go. This file's own note at the old site recorded that prose
# describing a check had defeated that check five times; the AST cannot see a comment at all, so the
# class of failure disappears rather than being worked around.
foreach ($pair in @(
        @{ Name = 'build.ps1'; Path = (Join-Path $repoRoot 'build.ps1') },
        @{ Name = 'installer\build-installer.ps1'; Path = (Join-Path $repoRoot 'installer\build-installer.ps1') })) {
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($pair.Path, [ref]$null, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) "$($pair.Name) parses, so its signing guard can be read from the AST"

    $guardBlocks = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match 'IsNullOrWhiteSpace\(\$SigningCertThumbprint\)' -and
        $node.Clauses[0].Item1.Extent.Text -match '-not'
    }, $true) | ForEach-Object { $_.Clauses[0].Item2.Extent })
    Assert-True ($guardBlocks.Count -ge 1) (
        "$($pair.Name) guards signing on a NON-EMPTY thumbprint (the -not is the whole check)")

    $signtoolCalls = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.Extent.Text -match 'Invoke-Signtool\.ps1'
    }, $true))
    Assert-True ($signtoolCalls.Count -ge 1) "$($pair.Name) still invokes packaging\Invoke-Signtool.ps1"

    # EVERY call, not the first one IndexOf happened to find.
    $unguarded = @($signtoolCalls | Where-Object {
        $call = $_
        -not ($guardBlocks | Where-Object {
            $call.Extent.StartOffset -ge $_.StartOffset -and $call.Extent.EndOffset -le $_.EndOffset
        })
    })
    Assert-True ($unguarded.Count -eq 0) (
        "$($pair.Name) invokes signtool only from INSIDE the thumbprint guard's block" +
        " ($($signtoolCalls.Count) call(s), $($unguarded.Count) outside" +
        $(if ($unguarded.Count) { ", first at line $($unguarded[0].Extent.StartLineNumber)" } else { '' }) + ")")
}

# The MSI signature has exactly one legal position: after Normalize-MsiDeterminism (which rewrites the whole
# file and REFUSES to run on a signed one) and before the seal (which takes the hash every later check and
# the atomic publish are compared against).
# Anchored on the CALL text, not the script names: both names also appear in comments explaining the
# ordering, and IndexOf finds the first occurrence, so matching the bare name compared a comment's position
# against a call's. Fourth time in this file that a check has been defeated by prose describing its subject.
$normalizeAt = $installerScript.IndexOf("Join-Path `$repoRoot 'packaging\Normalize-MsiDeterminism.ps1'")
$signAt = $installerScript.IndexOf("Join-Path `$repoRoot 'packaging\Invoke-Signtool.ps1'")
$sealAt = $installerScript.IndexOf('$sealedStagedMsi = Open-DesktopAICompanionSealedStagedFile')
Assert-True (
    $normalizeAt -gt 0 -and $signAt -gt $normalizeAt -and $sealAt -gt $signAt
) 'the MSI is signed after determinism normalisation and before the hash seal'

# Signing without verifying can produce a signature that does not validate (an untrusted chain being the
# obvious case), and shipping that is worse than shipping nothing.
Assert-True ($signtoolScript -match "'verify', '/pa'") 'a signature is verified against the Authenticode policy after being written'
# Re-signing a Microsoft-signed dependency would replace their attestation with ours on a binary we did not
# build. System.Numerics.Tensors.dll ships signed today, so this is a live case, not a hypothetical.
Assert-True (
    $signtoolScript -match 'SignatureStatus\]::Valid' -and
    $signtoolScript -match 'already signed by'
) 'a file already validly signed by someone else is skipped rather than re-signed'

# The release workflow must tolerate a missing secret, or adding the scaffolding breaks releases today.
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml') -Raw
Assert-True (
    $releaseWorkflow -match 'SIGNING_PFX_BASE64' -and
    $releaseWorkflow -match '::warning::No signing certificate configured'
) 'a release with no signing secret warns and continues unsigned rather than failing'
# And the key must not be left behind on a runner that outlives the job.
#
# BOUND TO ITS OWN STEP. The old form matched the step's name anywhere in the file and `if: always()`
# anywhere in the file, with nothing associating the two. release.yml happens to contain exactly one
# `if: always()` today, so it passed by luck: move that line onto the artifact-upload step -- the
# natural place for it -- and both assertions still passed while a failed build left the imported PFX
# sitting on the runner, which is the property the assertion's own text claims.
#
# The slice runs from the step's `- name:` to the next `- name:` at the same indentation, which is the
# same shape this file already uses to slice a C# method before asserting an ORDER inside it.
$scrubStep = [regex]::Match(
    $releaseWorkflow,
    '(?s)^[ \t]*- name: Remove the signing certificate from the runner\r?\n(.*?)(?=^[ \t]*- name: |\z)',
    [System.Text.RegularExpressions.RegexOptions]::Multiline)
Assert-True ($scrubStep.Success) (
    'the signing-certificate scrub step exists and could be sliced out for inspection')
Assert-True ($scrubStep.Success -and $scrubStep.Groups[1].Value -match 'if:\s*always\(\)') (
    'the signing certificate is scrubbed even when a build step fails (if: always() is on THAT step)')

# Every WiX source must be well-formed XML.
#
# The specific trap this exists for is WIX0104: "--" cannot appear inside an XML comment. It is easy to
# write (a dash used as punctuation, or naming a command-line flag) and it is invisible until the installer
# is compiled -- which locally is a separate slow step and in CI happens only on a v* tag. It has already
# broken a release tag once and three local builds since. Parsing costs milliseconds and turns a
# release-time failure into a gate failure.
#
# XmlDocument rejects exactly what the WiX compiler rejects here, so this needs no bespoke dash-hunting
# regex: a regex would also have to understand where comments start and end, and would drift.
# A floor first. This loop asserts nothing at all if the set empties, and there is exactly ONE .wxs
# today -- so renaming it, moving it, or generating it elsewhere would silently reduce this to zero
# assertions and a pass. The shell-parity block further down already asserts a floor for its own
# derived set; this is the same discipline applied here.
$wxsFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.wxs' -File)
Assert-True ($wxsFiles.Count -ge 1) (
    "installer\ still holds at least one .wxs to validate (found $($wxsFiles.Count))")
foreach ($wxs in $wxsFiles) {
    $wxsError = $null
    try { [void]([xml](Get-Content -LiteralPath $wxs.FullName -Raw)) }
    catch { $wxsError = $_.Exception.Message }
    Assert-True ($null -eq $wxsError) (
        "installer\$($wxs.Name) is well-formed XML" +
        $(if ($wxsError) { " -- $wxsError (a '--' inside a comment is WIX0104)" } else { '' }))
}

# --- shell parity: every script CI runs must work under pwsh 7 AND Windows PowerShell 5.1 -------------
#
# CI runs every step with `shell: pwsh`. A developer box may only have Windows PowerShell 5.1, so the two
# can silently diverge: PS7-only syntax passes CI and fails locally, and 5.1-only syntax does the reverse,
# which is the dangerous direction because CI is what publishes a release.
#
# Parsed with PowerShell's own parser rather than grepped, for the same reason the .wxs guard above parses
# XML: a regex for `&&` or `??` matches them inside comments and string literals, and this file itself
# asserts on C# source text containing `??`. Tokens and AST nodes cannot be faked by prose.
$parityHost = "$($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)"
# EVERY tracked .ps1, not the ones a workflow happens to name in its text.
#
# The set used to be regexed out of build.yml and release.yml AS TEXT, which had two consequences and
# both were live. It covered 11 of the 28 tracked scripts, missing installer\New-RuntimeWixFragment.ps1
# (invoked on every CI MSI build), packaging\StagingPathSafety.ps1 and packaging\WixToolchainPolicy.ps1
# (dot-sourced by build.ps1 and build-installer.ps1, so a parse error in them breaks the build that
# sources them), and seven other packaging scripts. And run-gate.ps1 was in the set only because
# build.yml mentions it inside four COMMENTS -- so tidying a comment would have silently dropped the
# repo's own gate script from its own parity coverage.
#
# `git ls-files` is the house pattern already used for the .csproj count below, and it gives obj/bin
# exclusion for free. `-co --exclude-standard`, tracked PLUS untracked-not-ignored, the way the redirect
# scan enumerates its .cs files (F411): tracked-only meant a brand-new script with a `??` or a
# Set-Content write passed the local gate until `git add` and failed on the run after (RA-363). CI
# under pwsh caught a committed `??` regardless; what was late was the local answer.
$ciScripts = @(& git -C $repoRoot ls-files -co --exclude-standard -- '*.ps1' 2>$null | ForEach-Object {
    $p = Join-Path $repoRoot ($_ -replace '/', '\')
    if (Test-Path -LiteralPath $p -PathType Leaf) { $p }
})
Assert-True ($ciScripts.Count -ge 25) (
    "every tracked or untracked-not-ignored PowerShell script is checked for shell parity (found $($ciScripts.Count), floor 25, " +
    "running under $parityHost)")

# Removed in PowerShell 7: present in 5.1, so they pass a local run and break CI.
$goneInPwsh = @('Get-WmiObject', 'Invoke-WmiMethod', 'New-WebServiceProxy', 'Add-PSSnapin', 'Get-EventLog')
# PS7-only operators. Under 5.1 these are parse errors, caught by the parse assertion; under pwsh they
# tokenize, so they are caught here. Between the two, the gate catches them whichever host it runs on --
# which was NOT true until QuestionMark and QuestionQuestionEquals were added. Measured under the real
# 7.6.5 parser: a ternary `$c ? 'a' : 'b'` emits QuestionMark, and `$x ??= 1` emits
# QuestionQuestionEquals, so the two most likely PS7-only spellings in this repo were both invisible.
# QuestionDot and QuestionLBracket are kept as belt-and-braces but do NOT fire for the usual
# `$var?.Prop` form: that parser folds the `?` into the Variable token, so `$var?` arrives as one
# token. And `?` as the Where-Object alias tokenizes as Generic, not QuestionMark, so adding it is
# false-positive-free -- verified, and there are no uses of that alias in the repo anyway.
$pwshOnlyTokens = @('AndAnd', 'OrOr', 'QuestionQuestion', 'QuestionQuestionEquals', 'QuestionMark',
                    'QuestionDot', 'QuestionLBracket')
# NOT "call these with -Encoding" -- DO NOT CALL THESE AT ALL. Set-Content defaults to ANSI under 5.1 and
# UTF-8-no-BOM under pwsh, and Out-File differs too, so the same script emits different BYTES on each.
# That is not academic: it is the shape of the CRLF SHA256SUMS bug.
#
# The trap is that -Encoding does NOT fix it, which is why this check used to be wrong in two ways at once.
# `-Encoding UTF8` writes a BOM under 5.1 and no BOM under pwsh, so it is still not byte parity; and the
# unambiguous spellings that would be (utf8NoBOM, utf8BOM) are PowerShell 7 NAMES, so a script using one
# PARSES cleanly under 5.1 and then dies at RUNTIME on the ValidateSet -- the same family as
# `ConvertFrom-Json -AsHashtable`. No spelling of -Encoding means the same bytes in both shells.
#
# So the rule is a ban, and it codifies what this repo already does: every tracked script writes files
# through [IO.File]::WriteAllText/WriteAllBytes with an explicit encoding object, unambiguous by
# construction. Measured at the time of writing: 0 calls and 0 file redirections across all 32 scripts.
#
# What was here before REQUIRED an -Encoding parameter and could not fail -- nothing in the repo calls any
# of the three, so the filter never matched and it was 32 guaranteed passes per gate -- while its message
# claimed to establish that "both shells emit the same bytes", which the presence of a parameter does not
# establish even when it does match.
#
# Aliases are hardcoded rather than resolved with Get-Alias, because that would ask the RUNNING host: `sc`
# is Set-Content under 5.1 but was dropped in 7 (it collides with sc.exe), so a gate running under pwsh
# would silently stop catching the 5.1-only spelling -- a check whose coverage depends on where it runs.
$bannedWriters = @('Set-Content', 'Add-Content', 'Out-File', 'sc', 'ac')

foreach ($script in $ciScripts) {
    $rel = $script.Substring($repoRoot.Length).TrimStart('\')
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) (
        "$rel parses under $parityHost" +
        $(if ($parseErrors.Count) { " -- $($parseErrors[0].Message)" } else { '' }))

    $badToken = @($tokens | Where-Object { $pwshOnlyTokens -contains $_.Kind.ToString() })
    Assert-True ($badToken.Count -eq 0) (
        "$rel uses no PowerShell-7-only operator" +
        $(if ($badToken.Count) { " -- $($badToken[0].Kind) at line $($badToken[0].Extent.StartLineNumber)" } else { '' }))

    $commands = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true))

    $removed = @($commands | Where-Object { $goneInPwsh -contains $_.GetCommandName() })
    Assert-True ($removed.Count -eq 0) (
        "$rel calls nothing removed in PowerShell 7" +
        $(if ($removed.Count) { " -- $($removed[0].GetCommandName()) at line $($removed[0].Extent.StartLineNumber)" } else { '' }))

    # Both spellings of the same divergence: the cmdlets, and the `>` / `>>` operators, which are
    # Out-File underneath and carry exactly the same 5.1-vs-pwsh byte difference. `2>$null` and friends
    # are discards rather than file writes, so they are excluded by Location rather than by stream number
    # -- `2>errors.log` IS a file write and must still be caught.
    $divergent = @()
    $divergent += @($commands | Where-Object {
        $name = $_.GetCommandName()
        $null -ne $name -and $bannedWriters -contains $name
    } | ForEach-Object { "$($_.GetCommandName()) at line $($_.Extent.StartLineNumber)" })
    $divergent += @($ast.FindAll({
        $args[0] -is [System.Management.Automation.Language.FileRedirectionAst] }, $true) |
        Where-Object { $_.Location.Extent.Text -ne '$null' } |
        ForEach-Object { "redirection $($_.Extent.Text) at line $($_.Extent.StartLineNumber)" })
    Assert-True ($divergent.Count -eq 0) (
        "$rel writes no file through a cmdlet or redirection whose bytes differ between 5.1 and pwsh" +
        $(if ($divergent.Count) { " -- $($divergent[0]); use [IO.File]::WriteAllText with an explicit encoding object" } else { '' }))
}

# ---- the two animations.xsd copies must stay byte-identical ----
# BOTH are live, which is why neither can simply be deleted: src\Resources\animations.xsd is embedded by
# three csproj files (it is what the running app validates against), and Resources\animations.xsd is the
# one the grimoire docs link as the authoritative schema. handoff.md has recorded that they must stay in
# sync for a long time, and nothing asserted it -- so the failure mode was a companion the app rejects for
# a rule the published schema does not contain, or the reverse, with the docs confidently wrong.
# Hash the bytes rather than diffing text: a line-ending difference between the two would be a real drift
# for an embedded resource, so it must not be normalized away by the comparison.
$xsdPaths = @('Resources\animations.xsd', 'src\Resources\animations.xsd')
$xsdHashes = @()
foreach ($xsdRelative in $xsdPaths) {
    $xsdFull = Join-Path $repoRoot $xsdRelative
    Assert-True (Test-Path -LiteralPath $xsdFull) "$xsdRelative exists"
    $xsdHashes += (Get-FileHash -LiteralPath $xsdFull -Algorithm SHA256).Hash
}
Assert-True ($xsdHashes[0] -eq $xsdHashes[1]) (
    "the two animations.xsd copies are byte-identical (embedded $($xsdPaths[1]) vs documented $($xsdPaths[0]))" +
    $(if ($xsdHashes[0] -ne $xsdHashes[1]) { " -- $($xsdHashes[0].Substring(0,12)) vs $($xsdHashes[1].Substring(0,12)); copy the one you edited over the other" } else { '' }))

# ---- every user-facing line in the BASE must be offered to the speech responders ----
# RegisterSpeechResponder's ABI comment promises the chain is "offered every utterance BEFORE any
# bubble is drawn". The poke sass broke that: it called FormCompanion.Say directly, which draws a
# bubble and raises nothing, so a voice module would have spoken fortunes, reminders and AI answers
# and then gone silent on the sass -- with a bubble appearing anyway. Found by an ABI audit
# 2026-09-17; no module registers a responder yet, so nothing was visibly wrong.
#
# An ORDER check inside the sliced sass branch, not a presence one: a RaiseSpeechRequest somewhere
# else in the file says nothing about this call site, and the bubble must be the fallback rather than
# the first thing that happens.
$pokeSass = [regex]::Match(
    $startUpSource,
    '(?s)PokeReactions\.RandomSass\(\).*?return;')
Assert-True ($pokeSass.Success) 'the poke-sass branch exists and could be sliced out for inspection'
# LINE COMMENTS STRIPPED FIRST, and this is not defensive tidying -- it is the mutation result. The
# first version of this check SURVIVED reverting the fix, because the comment explaining the fix
# contains the word RaiseSpeechRequest and sits above the call: the index found the COMMENT, the
# order held, and the check passed over code that had gone back to calling Say directly. A
# source-text check that a comment can satisfy is a check that measures its own documentation.
#
# Through Remove-LineComments, not the whole-line regex that sat here (RA-362; the register's F410
# entry named this stripper as the one to retire first). `(?m)^\s*//.*$` removed only lines that
# BEGIN with a comment, so a trailing one -- `string sass = s; // RaiseSpeechRequest ...` on the line
# above a bare Say -- kept the word in the judged text, ahead of `.Say(`, and the order held again.
$pokeSassCode = Remove-LineComments $pokeSass.Value
$sassOfferIndex = $pokeSassCode.IndexOf('RaiseSpeechRequest')
$sassSayIndex = $pokeSassCode.IndexOf('.Say(')
Assert-True ($sassOfferIndex -ge 0) (
    'the poke sass is offered to the speech responders, not spoken straight into a bubble')
Assert-True ($sassSayIndex -ge 0) (
    'the poke sass still falls back to a bubble (the anchor for the order below)')
Assert-True ($sassOfferIndex -lt $sassSayIndex) (
    'the poke sass reaches the speech responders BEFORE the bubble' +
    " (offer at $sassOfferIndex, bubble at $sassSayIndex)")

# ---- an update that WIDENS a module's permissions must reach the consent prompt ----
# ModulePermissionConsent is pure and its table is asserted in --hardening-selftest. What a table
# cannot reach is whether the PANE consults it, and that half is the whole feature: the promise in
# ModulePermissions' doc block ("a module that later widens its set re-prompts rather than widening
# silently") went years with a helper's worth of nothing behind it.
#
# An ORDER check, not a presence one. A consult that happens after the bytes are downloaded and
# staged is not consent, it is a notification -- and asserting merely that the call APPEARS in the
# file is satisfied by a call sitting unreachable below a return.
$modulesPaneSource = Get-Content -Raw (Join-Path $repoRoot 'src\Portable\Wpf\ModulesPaneControl.cs')
$updateBody = [regex]::Match(
    $modulesPaneSource,
    '(?s)private async Task UpdateModuleAsync\(.*?
        \}')
Assert-True ($updateBody.Success) 'UpdateModuleAsync exists and could be sliced out for inspection'
# COMMENTS STRIPPED, like the structurally identical poke-sass order check thirty lines above, whose
# note records that an unstripped version survived a revert. Without this, moving the consent block
# below the download and writing "// ModulePermissionConsent.NewlyRequested is consulted here" above
# the download satisfies the order assertion while consent happens after the bytes are on disk.
# Verified safe for this file specifically: ModulesPaneControl.cs contains no `://`, so the line-comment
# strip cannot eat a URL.
$updateCode = Remove-LineComments $updateBody.Value
$consentIndex = $updateCode.IndexOf('ModulePermissionConsent.NewlyRequested')
$downloadIndex = $updateCode.IndexOf('DownloadVerifiedAsync')
Assert-True ($consentIndex -ge 0) (
    'the module update path consults ModulePermissionConsent at all')
Assert-True ($downloadIndex -ge 0) (
    'the module update path still downloads through DownloadVerifiedAsync (the anchor for the order below)')
Assert-True ($consentIndex -lt $downloadIndex) (
    'a widened permission set is put to the user BEFORE the update is downloaded' +
    " (consent at $consentIndex, download at $downloadIndex)")

# ---- a module the host cannot run must not be offered for install ----
# ModuleHostRequirement's rule table is asserted in --module-host-selftest. What a table cannot reach
# is whether the PANE asks it, and that half is the whole feature: the catalog dropped minHostVersion
# entirely until 2026-09-27, so the pane offered agentflow (needs host 1.2.0) to every user still on
# 1.1.x, downloaded the payload, installed it, and the LOADER refused it -- with nothing in the pane
# to explain why.
#
# Asserts the BINDING, not the presence of a call. `install.IsEnabled = runnable` is what makes the
# answer matter; a file that calls IsSatisfied, ignores the result, and sets IsEnabled = true would
# satisfy any presence check while behaving exactly as it did before the fix.
$availableBody = [regex]::Match(
    $modulesPaneSource,
    '(?s)private FrameworkElement BuildAvailableRow\(.*?
        \}')
Assert-True ($availableBody.Success) 'BuildAvailableRow exists and could be sliced out for inspection'
$availableCode = Remove-LineComments $availableBody.Value
$requirementIndex = $availableCode.IndexOf('ModuleHostRequirement.IsSatisfied')
$enabledIndex = $availableCode.IndexOf('install.IsEnabled = runnable')
Assert-True ($requirementIndex -ge 0) (
    'the install row asks ModuleHostRequirement whether this host can run the module')
Assert-True ($enabledIndex -ge 0) (
    'the Install button''s enabled state is BOUND to that answer, not merely computed beside it')
Assert-True ($requirementIndex -lt $enabledIndex) (
    'the requirement is resolved before the Install button is enabled' +
    " (requirement at $requirementIndex, binding at $enabledIndex)")

# ---- the behaviour-timeline Run button is wired, and a chain that will not build never spawns ----
# The chain COMPILER is covered by BehaviourChainSelfCheck. What was not covered is the button:
# whether pressing it reaches the compiler at all, and whether a build FAILURE stops before a pet is
# spawned. Driving the real button was tried three times and abandoned for reasons recorded in the
# backlog -- no way to drive the tray from a test, previews auto-hide under a fullscreen foreground
# window, and an isolated data root kept falling back to eSheep -- so this asserts the two
# properties that actually regress, by ORDER, which is deterministic where UI automation was not.
#
# This is NOT the click-through test the entry asked for and does not pretend to be. It catches the
# button being disconnected and the failure path spawning anyway; it cannot catch anything that only
# shows up on screen.
#
# Re-pointed 2026-09-30 by lane burn/petstudio (RA-133): Run is `async void` now, builds the pet on a pool
# thread and holds the result in `built`; the ORDER this asserts is unchanged, and the pool-thread half is
# the lane's own invariant further down.
$timelineSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\PetStudio\TimelinePane.cs') -Raw
$runBody = [regex]::Match($timelineSource, '(?s)private async void Run\(\).*?\n        \}')
Assert-True ($runBody.Success) 'TimelinePane.Run exists and could be sliced out for inspection'
$runCode = Remove-LineComments $runBody.Value
$buildIndex = $runCode.IndexOf('BehaviourChain.BuildDebugXml')
$guardIndex = $runCode.IndexOf('if (built == null)')
$spawnIndex = $runCode.IndexOf('_runDebugPet(')
Assert-True (
    $timelineSource -cmatch '_runButton\.Click \+= delegate \{ Run\(\); \}' -and
    $buildIndex -ge 0 -and $guardIndex -gt $buildIndex -and $spawnIndex -gt $guardIndex
) ('the Run button reaches the chain compiler, and a chain that will not build returns before a ' +
   "pet is spawned (build at $buildIndex, guard at $guardIndex, spawn at $spawnIndex)")

# ---- a monitor pin is honoured at spawn whatever the multiscreen setting says ----
# PinnedDisplay used to be read only INSIDE `if (Program.MyData.GetMultiscreen())`, and that
# setting defaults to false -- so on default settings, pinning a pet to screen 2 did nothing and it
# spawned on the primary. Only the PUNITIVE half of the pin worked, because CheckFullScreen honours
# it unconditionally and refuses to relocate a pinned pet off a monitor a fullscreen app has taken.
#
# Two doc blocks promise the opposite in almost the same words -- PinnedDisplay's own summary and
# CompanionsPaneControl's pin UI: "that setting only decides whether an UNPINNED pet spawns on a
# random screen, whereas naming a monitor is an explicit instruction."
#
# Asserted as STRUCTURE, not presence: what matters is that the RANDOM arm is the gated one. A
# check that merely found `PinnedDisplay` somewhere in Play would pass on the broken version,
# which read it one line inside the gate.
$companionSource = Get-Content -Raw (Join-Path $repoRoot 'src\dotNet\FormCompanion.cs')
$companionFlat = ($companionSource -replace '\s+', ' ')
Assert-True (
    $companionFlat.Contains('int pinned = PinnedDisplay; if (pinned >= 0) DisplayIndex = pinned;') -and
    $companionFlat.Contains('else if (Program.MyData.GetMultiscreen()) DisplayIndex = new Random()')
) ('a pinned monitor is honoured at spawn, with only the RANDOM screen pick gated by ' +
   '"Let companions spawn on any screen"')

# ---- the numbers the docs quote about this suite are re-measured, not trusted ----
# A number nobody re-measures goes stale. SMOKETEST.md and Readme.md both quote how many source
# invariants and how many self-tests exist, and both were wrong again within one session of being
# corrected -- because adding an assertion is exactly the moment nobody thinks about a prose line in
# another file. tests\DesktopAICompanion.CoreTests\Program.cs is the model: it COUNTS its groups.
# These two assertions move the same discipline to a doc claim, so the drift fails the gate that
# caused it instead of being found by a later audit.
# The catalog fetch on pane open must be wired to Loaded, not called from the constructor.
#
# RemoteCatalogClient.FetchSharedAsync returns its cached catalog from inside a lock with no await
# executed, so on a warm cache the task is ALREADY COMPLETE and `await ... ConfigureAwait(true)`
# resumes synchronously on the calling stack. Called from a constructor, that lands on the
# `if (!IsLoaded) return;` guard while still inside the constructor, where IsLoaded is false by
# definition -- so the catalog was dropped and no "Update to vX.Y.Z" button rendered. The first open
# in any 90-second window did a real round trip whose continuation was posted to the dispatcher and
# arrived after Loaded, so the feature worked exactly once per window and then went quiet.
#
# Asserts the WIRING and the ABSENCE of the old shape, not merely that the call exists: the call
# existed throughout the bug.
foreach ($paneFile in @('ModulesPaneControl.cs', 'CompanionsPaneControl.cs')) {
    $paneText = Get-Content -LiteralPath (
        Join-Path $repoRoot (Join-Path 'src\Portable\Wpf' $paneFile)) -Raw
    $paneCode = Remove-LineComments $paneText
    Assert-True (
        # -cmatch, and a boundary before Loaded. -match is CASE-INSENSITIVE in PowerShell, so
        # 'Loaded \+= delegate' was satisfied by the Unloaded += delegate line sitting a few
        # characters above -- this assertion passed against a mutation that put the call back in the
        # constructor, which is the exact shape of check this file exists to forbid.
        $paneCode -cmatch '(?<![A-Za-z])Loaded \+= delegate[\s\S]{0,400}?RefreshCatalogOnOpen\(\)'
    ) "$paneFile fetches the catalog from Loaded, where IsLoaded is true"
    Assert-True (
        $paneCode -notmatch 'Reload\(\);\s*\r?\n\s*RefreshCatalogOnOpen\(\);'
    ) "$paneFile does not call RefreshCatalogOnOpen straight from the constructor"
}

$hardeningOwnSource = Get-Content -LiteralPath $MyInvocation.MyCommand.Path -Raw
# Line-start calls only, which excludes this file's own `function Assert-True` definition and the one
# comment that names it. The pattern is written mid-line on purpose so it cannot match itself.
$assertSiteCount = ([regex]::Matches($hardeningOwnSource, '(?m)^[ \t]*Assert-True')).Count
$selfTestsSource = Get-Content -LiteralPath (Join-Path $testsRoot 'Invoke-SelfTests.ps1') -Raw
$selfTestFlagCount = ([regex]::Matches($selfTestsSource, "(?m)^\s+'--[a-z0-9=-]+'\s+=")).Count
$smokeSource = Get-Content -LiteralPath (Join-Path $repoRoot 'SMOKETEST.md') -Raw
$readmeSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Readme.md') -Raw

Assert-True ($assertSiteCount -gt 50) (
    "this file's own assertion sites are countable (found $assertSiteCount)")
Assert-True ($selfTestFlagCount -gt 10) (
    "Invoke-SelfTests.ps1's flag table is countable (found $selfTestFlagCount)")

# The project count in Readme.md, guarded for the same reason the self-test count above is. Added
# 2026-09-22 after an audit found "All sixteen projects target net10.0-windows" had been correct when
# written and wrong ever since a test project was added: `git ls-tree` at that commit returns exactly
# 16. The self-test count next to it had NOT rotted in the same period, and the only difference between
# the two numbers was that one of them was asserted. Spelled-out numerals, because that is how the
# sentence is written and a digit would not match.
$projectCount = @(git -C $repoRoot ls-files '*.csproj').Count
$numeralNames = @{ 14 = 'fourteen'; 15 = 'fifteen'; 16 = 'sixteen'; 17 = 'seventeen';
                   18 = 'eighteen'; 19 = 'nineteen'; 20 = 'twenty' }
Assert-True ($projectCount -gt 10) (
    "the tracked project count is countable (found $projectCount)")
Assert-True ($numeralNames.ContainsKey($projectCount)) (
    "the project count $projectCount has a spelled-out name in this check's table -- add it")
$documentedProjects = [regex]::Match($readmeSource, '(?m)^All ([a-z]+) projects target')
Assert-True ($documentedProjects.Success) 'Readme.md states a project count'
Assert-True ($documentedProjects.Groups[1].Value -eq $numeralNames[$projectCount]) (
    "Readme.md's project count matches the tracked .csproj files" +
    $(if ($documentedProjects.Groups[1].Value -ne $numeralNames[$projectCount]) {
        " -- it says '$($documentedProjects.Groups[1].Value)', there are $projectCount" +
        " ('$($numeralNames[$projectCount])')" } else { '' }))

# THE NEXT BUG NUMBER, asserted rather than restated. BACKLOG.md carried "The next one filed is
# BUG-005" for weeks after DESIGN-REGISTER.md had moved on to BUG-009, and four more post-mortems
# were written in between without either line noticing. The number now lives in exactly one place
# (the register) and is derived from the one thing that cannot drift: the post-mortems themselves.
$issuesSource = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\ISSUES-post-1.0.0.md') -Raw -Encoding UTF8
$registerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\DESIGN-REGISTER.md') -Raw -Encoding UTF8
$backlogSource = Get-Content -LiteralPath (Join-Path $repoRoot 'BACKLOG.md') -Raw -Encoding UTF8
$bugHeadings = [regex]::Matches($issuesSource, '(?m)^#{2,4}\s+BUG-(\d+)\b')
Assert-True ($bugHeadings.Count -gt 0) (
    "ISSUES-post-1.0.0.md's bug post-mortems are countable (found $($bugHeadings.Count))")
$highestBug = 0
foreach ($m in $bugHeadings) { $n = [int] $m.Groups[1].Value; if ($n -gt $highestBug) { $highestBug = $n } }
$expectedNextBug = 'BUG-{0:000}' -f ($highestBug + 1)
$declaredNextBug = [regex]::Match($registerSource, 'The next one filed is (BUG-\d+)')
Assert-True ($declaredNextBug.Success) 'DESIGN-REGISTER.md names the next bug number'
Assert-True ($declaredNextBug.Groups[1].Value -eq $expectedNextBug) (
    "DESIGN-REGISTER.md's next bug number follows the highest post-mortem" +
    $(if ($declaredNextBug.Groups[1].Value -ne $expectedNextBug) {
        " -- it says $($declaredNextBug.Groups[1].Value), the highest written up is BUG-{0:000}, so the next is $expectedNextBug" -f $highestBug } else { '' }))
# And BACKLOG.md must not carry a competing copy of that number, which is how the two drifted apart.
Assert-True (-not [regex]::IsMatch($backlogSource, 'next one filed is BUG-\d+')) (
    'BACKLOG.md does not restate the next bug number; it points at the register, which owns it')

foreach ($docPair in @(@{ Name = 'SMOKETEST.md'; Text = $smokeSource },
                       @{ Name = 'Readme.md';    Text = $readmeSource })) {
    $documentedSelfTests = [regex]::Match($docPair.Text, '(\d+) self-tests')
    Assert-True ($documentedSelfTests.Success) "$($docPair.Name) states a self-test count"
    Assert-True ([int] $documentedSelfTests.Groups[1].Value -eq $selfTestFlagCount) (
        "$($docPair.Name)'s self-test count matches Invoke-SelfTests.ps1" +
        $(if ([int] $documentedSelfTests.Groups[1].Value -ne $selfTestFlagCount) {
            " -- it says $($documentedSelfTests.Groups[1].Value), the table has $selfTestFlagCount" } else { '' }))
}

# "Reset to default settings" must both WRITE the defaults and APPLY the ones that have a live
# counterpart. Two fields were missing and their absence was invisible, which is the failure mode the
# reset block's own comment was written about: the pane rebuilds underneath the button, so a field
# that did not move looks exactly like a field whose default is what it already held.
#
# SCOPED TO THE METHOD, and that is what makes it able to fail. A file-wide grep for
# 'DiagnosticLog.Configure(' is satisfied by the SAVE path's call one screen away, so it would have
# passed against the shipped defect. Sliced first, then asserted -- from COMMENT-STRIPPED code (RA-362):
# sliced from the raw source, a comment inside the method saying "DiagnosticLog.Configure( is applied
# by the pane" satisfied the Contains below with the call itself gone.
$resetBody = Get-MethodBody (Remove-LineComments $optionsShellSource) 'private static string ResetToDefaultSettings()' @(
    "`n        private ", "`n    }")
Assert-True ($resetBody.Length -gt 0) 'the reset-to-defaults method body was located'
Assert-True ($resetBody.Contains('DiagnosticLog.Configure(')) (
    'the reset re-applies the diagnostic-log settings to the RUNNING logger, not just to the store' +
    ' -- without it, switching logging off and pressing Reset brings the checkbox back ticked while' +
    ' nothing is recorded until the next launch')
Assert-True ($resetBody.Contains('SetDefaultSpeakingPet(')) (
    'the reset restores the global speaking companion, which the confirmation text promises when it' +
    ' says it restores the speech settings shown on this page')

# Lowering "how many logs to keep" has to take effect NOW, not at the next time the cap happens to be
# hit. Start() rotates before Configure has ever run -- it must, because the rotation precedes
# anything worth recording and the settings store is not loaded that early -- so it rotates with the
# field default of 2, and every launch recreated diagnostics.1.log whatever the user had chosen.
#
# --wpf-options-selftest proves TrimArchivesIn behaves (including that it leaves the LIVE file alone,
# which a rotation would not). This asserts the CALL, because the only other thing that proves the
# wiring is a real-app smoke that is not in the gate. Scoped to Configure, so RotateIn's own trim
# loop cannot satisfy it.
$configureBody = Get-MethodBody $diagSource 'internal static void Configure(' @(
    "`n        private ", "`n        internal ", "`n        /// ")
Assert-True ($configureBody.Length -gt 0) 'the diagnostic-log Configure body was located'
Assert-True ($configureBody.Contains('TrimArchivesIn(')) (
    'lowering the diagnostic-log keep count drops the archives it no longer allows immediately' +
    ' -- Start() rotates with the field default before Configure runs, so without this "keep 1"' +
    ' never holds across a launch')

# The two local-enumeration entry points must read the SAME pair of roots. EnumerateLocalIds skips
# the per-pet header read for callers that discard display names, and --catalog-selftest compares the
# two on a synthetic corpus -- but that test passes the roots in, so it cannot see what the public
# entry points pass. Mutation-tested 2026-09-28: pointing EnumerateLocalIds at `null` instead of the
# bundled directory, which would make the tray and the Companions pane disagree about which pets
# exist, still reported PASS.
#
# It asserts the ARGUMENTS, not that a call is present. A check for 'EnumerateIdsFrom(' alone passes
# against that mutation, which is the failure mode this whole file exists to avoid.
$catalogSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\CompanionCatalog.cs') -Raw
$catalogFlat = (Remove-LineComments $catalogSource) -replace '\s+', ' '
Assert-True ($catalogFlat.Contains(
    'return EnumerateFrom(AppPaths.BundledPetsDirectory, AppPaths.LibraryPetsDirectory, true);')) (
    'EnumerateLocal reads both pet roots, bundled first')
Assert-True ($catalogFlat.Contains(
    'return EnumerateIdsFrom(AppPaths.BundledPetsDirectory, AppPaths.LibraryPetsDirectory);')) (
    'EnumerateLocalIds reads the SAME two roots as EnumerateLocal -- otherwise the tray and the' +
    ' Companions pane disagree about which pets are installed, and the synthetic-corpus check in' +
    ' --catalog-selftest cannot see it because that test passes its own roots in')

# The CONVERTED-pet count, measured rather than quoted. Two comments said "31" against a real 32 --
# harmless on their own, and exactly the drift the Readme project count and the self-test count were
# turned into assertions for, because correcting a number nobody re-measures only resets the clock.
$convertedPets = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Companions') -Directory |
    Where-Object {
        $x = Join-Path $_.FullName 'animations.xml'
        (Test-Path -LiteralPath $x) -and
        (Select-String -LiteralPath $x -Pattern '<author>Converted from a Shimeji skin</author>' -SimpleMatch -Quiet)
    }).Count
$mappingSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert\MAPPING.md') -Raw
$documentedSkins = [regex]::Match($mappingSource, 'occurrences across the (\d+) shipping skins')
Assert-True ($documentedSkins.Success) 'MAPPING.md states a shipping-skin count'
Assert-True ([int] $documentedSkins.Groups[1].Value -eq $convertedPets) (
    "MAPPING.md's shipping-skin count matches the corpus" +
    $(if ([int] $documentedSkins.Groups[1].Value -ne $convertedPets) {
        " -- it says $($documentedSkins.Groups[1].Value), there are $convertedPets" } else { '' }))
$converterSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert\Program.cs') -Raw
$documentedConverted = [regex]::Match($converterSource, 'all (\d+) CONVERTED pets')
Assert-True ($documentedConverted.Success) 'Program.cs states a converted-pet count'
Assert-True ([int] $documentedConverted.Groups[1].Value -eq $convertedPets) (
    "Program.cs's converted-pet count matches the corpus" +
    $(if ([int] $documentedConverted.Groups[1].Value -ne $convertedPets) {
        " -- it says $($documentedConverted.Groups[1].Value), there are $convertedPets" } else { '' }))

# AgentFlow must not probe VS Code's setup on the UI thread, and must not fetch a setting for itself
# from a pool thread.
#
# SetupStatusLine runs while the options pane is being built. Inspect is a file read, a JSON parse
# and a bounded TCP connect -- 30.1-30.7 ms with the port listening, 273.3-284.6 ms with it closed,
# by that module's own measurement -- and its doc used to claim the cold path was "only in the window
# between Init and the first tick completing", which is false in Off mode because OnTick returns
# before the probe when not Enabled. Off is the default for a new user.
#
# SCOPED TO EACH METHOD. VsCodeSetup.Inspect is called from three places and ArgvPath from several,
# so a file-wide grep passes against both mutations -- which is the failure mode this file exists for.
$agentFlowSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\AgentFlow\AgentFlowModule.cs') -Raw
$setupLineBody = Get-MethodBody (Remove-LineComments $agentFlowSource) `
    'private string SetupStatusLine()' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($setupLineBody.Length -gt 0) 'the AgentFlow SetupStatusLine body was located'
Assert-True (-not $setupLineBody.Contains('VsCodeSetup.Inspect(')) (
    'SetupStatusLine does not probe VS Code synchronously -- it runs on the UI thread while the' +
    ' options pane is built, and in Off mode the tick never warms the cache, so a cold pane open' +
    ' paid up to 284 ms for it')
Assert-True ($setupLineBody.Contains('BeginSetupProbe()')) (
    'SetupStatusLine asks for the probe in the background instead, so the next pane build has it')

# The tick worker must be handed ArgvPath rather than reading it. CompanionHost.ModuleSettings is a
# bare Dictionary<string,string> with no lock, written on the UI thread on every Apply. The tick's own
# comment says every value on that beat is copied across the boundary; ArgvPath was the exception,
# and it hid behind a property so it did not read as a settings access.
$tickBody = Get-MethodBody (Remove-LineComments $agentFlowSource) `
    'private void OnTick(' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($tickBody.Length -gt 0) 'the AgentFlow OnTick body was located'
Assert-True ($tickBody.Contains('string argvPathNow = ArgvPath;')) (
    'the tick copies ArgvPath across the thread boundary on the UI thread, beside Enabled, rather' +
    ' than letting the pool-thread worker read the unsynchronised settings dictionary')
Assert-True (-not $tickBody.Contains('Inspect(ArgvPath')) (
    'the tick worker inspects the COPIED path, not the live settings property')

# The notification sound's custom-file read and decode must not run on a module's tick, and the
# Preferences preview must still run synchronously.
#
# ReadChosen is File.ReadAllBytes of up to 8 MiB and PlayNotification decodes it into mixer format
# inside lock (_sync). A module's notification arrives on the UI timer, so that pair was stalling the
# interface per notice; the preview's whole job is to report WHICH layer stopped the sound, so an
# optimistic answer there would be no answer. The two callers therefore have to differ, and this
# asserts that they do -- one of them being wrong is invisible at runtime.
#
# SCOPED to each method. NotificationSound.Play is called from four places, so an unscoped grep for
# 'NotificationSound.Play(' passes against either mutation.
$startUpForNotify = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\StartUp.cs') -Raw
$notifyBody = Get-MethodBody (Remove-LineComments $startUpForNotify) `
    'internal bool PlayNotificationSound(' @("`n        internal ", "`n        public ", "`n        private ", "`n        /// ")
$previewBody = Get-MethodBody (Remove-LineComments $startUpForNotify) `
    'internal NotificationOutcome PreviewNotificationSound()' @("`n        internal ", "`n        public ", "`n        private ", "`n        /// ")
Assert-True ($notifyBody.Length -gt 0 -and $previewBody.Length -gt 0) (
    'both notification-sound entry points were located')
Assert-True ($notifyBody -match 'NotificationSound\.Play\([^)]*,\s*true\s*\)') (
    'a MODULE notification defers the custom-file read off the UI thread -- it arrives on the UI' +
    ' timer and a custom pick is an up-to-8-MiB read plus a decode into mixer format')
Assert-True (-not ($previewBody -match 'NotificationSound\.Play\([^)]*,\s*true\s*\)')) (
    'the Preferences preview does NOT defer, because it reports which layer stopped the sound and' +
    ' the user is waiting for that answer rather than for the chime')

# ---- 2026-09-29 audit campaign: per-lane anchors ----
# Each lane inserts its source invariants directly under its own anchor, so parallel branches do not touch
# the same lines. Anchors are comments and do not change the Assert-True count. They stay after the
# campaign as section markers; an empty one is harmless.

# ---- lane fix/gates ----
# (invariants added by lane fix/gates go directly below this line)

# The fortune-pack FILE cap used to exist twice: the host's FortunePackLoadPolicy.MaximumFiles, read by the
# --catalog-selftest check that compares it with the catalog entry cap, and the Fortunes module's own copy in
# FortuneProvider.cs, which governs LOADING and which the module project did not source-link. The two were
# kept equal by a comment, so the 512-listed-vs-128-loadable regression that check describes would have
# passed it (F287). Since F124 the module COMPILES the host's file (a Compile Include in Fortunes.csproj), so
# there is one definition; what is asserted is that the link is still there and that no second definition
# has grown back in the module tree, then that the one cap covers every pack the catalog may list. Parsed as
# NUMBERS from comment-stripped source and compared as a CONDITION. -cmatch, and a digit group, so a renamed
# or removed constant fails the presence line rather than matching prose.
$hostPolicyCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\Ai\FortunePackLoadPolicy.cs') -Raw)
$fortunesProject = Get-Content -LiteralPath (Join-Path $repoRoot 'modules\Fortunes\Fortunes.csproj') -Raw
$fortunesPolicyLinked = $fortunesProject -cmatch '<Compile\s+Include="[^"]*src\\dotNet\\Ai\\FortunePackLoadPolicy\.cs"'
$fortunesPolicyCopies = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\Fortunes') -Filter '*.cs' -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Where-Object { (Remove-LineComments (Get-Content -LiteralPath $_.FullName -Raw)) -cmatch 'class\s+FortunePackLoadPolicy\b' })
$catalogCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\RemoteCatalog.cs') -Raw)
$hostFileCap = [regex]::Match($hostPolicyCode, 'MaximumFiles\s*=\s*(\d+)')
$catalogEntryCap = [regex]::Match($catalogCode, 'MaximumEntries\s*=\s*(\d+)')
Assert-True ($hostFileCap.Success -and $catalogEntryCap.Success) (
    'the pack caps are present to compare (host MaximumFiles ' + $hostFileCap.Success +
    ', catalog MaximumEntries ' + $catalogEntryCap.Success + ')')
Assert-True ($fortunesPolicyLinked -and $fortunesPolicyCopies.Count -eq 0) (
    "the Fortunes module compiles the host's FortunePackLoadPolicy.cs and defines no copy of its own (linked " +
    $fortunesPolicyLinked + ', copies ' + $fortunesPolicyCopies.Count + ')')
Assert-True ([int] $hostFileCap.Groups[1].Value -ge [int] $catalogEntryCap.Groups[1].Value) (
    'the pack file cap that governs LOADING covers every pack the catalog may list (cap ' +
    $hostFileCap.Groups[1].Value + ', catalog entries ' + $catalogEntryCap.Groups[1].Value + ')')

# 'Rebuild smart index' decides "already built" by comparing the indexed pool's signature with the pool
# the CURRENT folder yields. It compared it with `provider.PoolEntries()` -- the very list the signature
# was computed from, written together with it in RebuildEngine -- so the equality could never be false
# and a complete index made the button a no-op that reported "already built" after a pack had been
# dropped into the folder (F149). The runtime suites cannot see this: reaching the guard needs a
# COMPLETE warm of the whole corpus, minutes on the embedder. So the ARGUMENT is asserted here: the
# comparison reads a freshly built provider, and the self-referential operand is gone.
#
# AND THE INITIALISER (RA-364). Asserting the tokens `PoolSignature(fresh.PoolEntries())` present and
# `PoolSignature(provider.PoolEntries())` absent asserted names, not the condition: `FortuneProvider
# fresh = provider;` (or `= _provider`) is a plausible "avoid the extra parse on the UI thread" edit
# that restores the self-comparison exactly, and both regexes still matched. What F149 fixed is that
# the compared pool is a FRESH read of the folder, so the read is what is pinned: `fresh` is constructed
# from the current settings, and is never an alias of either provider field.
$fortunesModuleCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\Fortunes\FortunesModule.cs') -Raw)
$rebuildBody = Get-MethodBody $fortunesModuleCode 'private Task<string> RebuildSmartIndexAsync()' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($rebuildBody.Length -gt 0) 'RebuildSmartIndexAsync exists and could be sliced out for inspection'
Assert-True (
    $rebuildBody -cmatch 'FortuneProvider fresh = new FortuneProvider\(LoadFortuneSettings\(_host\)\)' -and
    $rebuildBody -cnotmatch '\bfresh = (provider|_provider)\b' -and
    $rebuildBody -cmatch 'PoolSignature\(fresh\.PoolEntries\(\)\)' -and
    $rebuildBody -cnotmatch 'PoolSignature\((provider|_provider)\.PoolEntries\(\)\)'
) "'Rebuild smart index' compares the index against a FRESHLY built pool, not the list it was built from"



# ---- lane fix/host ----
# (invariants added by lane fix/host go directly below this line)

# The fullscreen scan's exclusion set is EVERY window a companion owns, not just the root form (F278). A
# TopMost child (the UFO) or a speech bubble that covered a monitor's centre pixel was enumerated ahead of
# the game and decided that monitor as clear, so the whole family stayed visible over it for as long as the
# overlap lasted. The runtime suites cannot see this (a child and a bubble need a live desktop), so the
# two methods are sliced and the DELEGATION is asserted, with the old root-only shape asserted absent.
$formPetCodeHost = Remove-LineComments $formPetSource
$startUpCodeHost = Remove-LineComments $startUpSource
$sheepHandlesBody = Get-MethodBody $startUpCodeHost 'public HashSet<IntPtr> SheepHandles()' `
    @("`n        public ", "`n        internal ", "`n        private ")
$ownedHandlesBody = Get-MethodBody $formPetCodeHost 'internal void CollectOwnedHandles(HashSet<IntPtr> into)' `
    @("`n        internal ", "`n        private ", "`n        public ")
Assert-True ($sheepHandlesBody.Length -gt 0 -and $ownedHandlesBody.Length -gt 0) (
    'SheepHandles and CollectOwnedHandles were both located')
Assert-True (
    $sheepHandlesBody -cmatch 'sheep\.CollectOwnedHandles\(handles\)' -and
    $sheepHandlesBody -cnotmatch 'handles\.Add\(sheep\.Handle\)'
) 'the fullscreen scan excludes every window a companion OWNS, through CollectOwnedHandles, not only the root handle'
Assert-True (
    $ownedHandlesBody -cmatch 'child\.CollectOwnedHandles\(into\)' -and
    $ownedHandlesBody -cmatch 'into\.Add\(bubble\.Handle\)'
) 'CollectOwnedHandles gathers the children recursively AND the speech bubble'

# CheckTopWindow decides "is this window a real occluder" from the title bar's RECT SHAPE, not from its
# screen position (F270). TITLEBARINFO.rcTitleBar is in screen coordinates, so `Bottom >= 0` rejected every
# genuine title bar on a monitor arranged ABOVE the primary and coverage detection was simply off there.
$checkTopBody = Get-MethodBody $formPetCodeHost 'private bool CheckTopWindow(bool bCheck)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($checkTopBody.Length -gt 0) 'CheckTopWindow was located'
Assert-True (
    $checkTopBody -cmatch 'rcTitleBar\.Bottom >= titleBarInfo\.rcTitleBar\.Top' -and
    $checkTopBody -cnotmatch 'rcTitleBar\.Bottom >= 0'
) 'CheckTopWindow accepts an occluder by its title bar having a shape, not by its title bar lying below screen y=0'

# Leaving the stand-down has ONE implementation (F268). RelocateToDisplay cleared the marker on its own and
# left the bubble suppressed, while the clear branch that would have un-suppressed it was gated on the very
# marker relocation had just cleared. Both exits must reach the bubble, and inside RelocateToDisplay the
# ORDER matters: un-suppress before the Play() that re-shows the pet.
$clearBody = Get-MethodBody $formPetCodeHost 'private void ClearFullscreenStandDown()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$relocateBody = Get-MethodBody $formPetCodeHost 'private void RelocateToDisplay(int target)' `
    @("`n        private ", "`n        internal ", "`n        public ")
$checkFsBodyHost = Get-MethodBody $formPetCodeHost 'private void CheckFullScreen()' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($clearBody.Length -gt 0 -and $relocateBody.Length -gt 0 -and $checkFsBodyHost.Length -gt 0) (
    'the three fullscreen stand-down methods were located')
Assert-True (
    $checkFsBodyHost -cmatch 'ClearFullscreenStandDown\(\)' -and
    $clearBody -cmatch '_speech\.SetFullscreenSuppressed\(false\)' -and
    $relocateBody -cmatch '_speech\.SetFullscreenSuppressed\(false\)'
) 'both exits from the fullscreen stand-down un-suppress the speech bubble'
Assert-True (
    $relocateBody.IndexOf('SetFullscreenSuppressed(false)') -lt $relocateBody.IndexOf('Play(false)')
) 'RelocateToDisplay un-suppresses the bubble BEFORE the respawn that shows the pet'

# A stood-down companion DEFERS its line rather than opening a bubble over the game (found while fixing
# F278; a bubble is its own window and a new one lands above a borderless game whatever its TopMost). The
# stand-down test sits ahead of the bubble's construction AND ahead of the repeat guard -- recording the
# line as said would make its own replay a duplicate -- and the clear path is what replays it.
$sayBody = Get-MethodBody $formPetCodeHost 'internal void SayWithDwell(' `
    @("`n        internal ", "`n        private ", "`n        public ")
Assert-True ($sayBody.Length -gt 0) 'SayWithDwell was located'
$standDownTest = $sayBody.IndexOf('hwndFullscreenWindow != IntPtr.Zero || _fullscreenHidden')
Assert-True (
    $standDownTest -ge 0 -and
    $standDownTest -lt $sayBody.IndexOf('_lastSaid') -and
    $standDownTest -lt $sayBody.IndexOf('new FormSpeech()') -and
    $clearBody -cmatch 'ReplayDeferredSpeech\(\)'
) 'a stood-down companion defers its line before the repeat guard and before any bubble exists, and the clear path replays it'

# Every grip release inside NextStep finishes the tick as a NEW animation (F263). ReleaseWindowGrip swaps
# CurrentAnimation to the fall but renders nothing, so a release without bNewAnimation showed the old
# climbing pose for one more step at the old interval and ran the y-detectors against the fall. Counted
# as a ratio rather than listed, so a fifth site added later is held to the same rule.
$nextStepBody = Get-MethodBody $formPetCodeHost 'private void NextStep()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$gripReleases = ([regex]::Matches($nextStepBody, 'ReleaseWindowGrip\(true\);')).Count
$gripRestarts = ([regex]::Matches($nextStepBody, 'ReleaseWindowGrip\(true\);\s*\}?\s*bNewAnimation = true;')).Count
Assert-True ($nextStepBody.Length -gt 0 -and $gripReleases -ge 4) (
    "NextStep was located and releases the window grip at its sites (found $gripReleases)")
Assert-True ($gripRestarts -eq $gripReleases) (
    "every grip release in NextStep is followed by bNewAnimation = true ($gripRestarts of $gripReleases)")

# A stationary pose rides a moving window (F264). The fork's `Start.X.Value != 0 &&` gate in front of
# FollowWindow sent a sitting or sleeping pet down the "covered" branch whenever its window was dragged.
Assert-True (
    $nextStepBody -cmatch 'if \(FollowWindow\(\)\)' -and
    $nextStepBody -cnotmatch 'Start\.X\.Value != 0 && FollowWindow\(\)'
) 'a stationary pose rides a moving window: FollowWindow is not gated on horizontal velocity'

# The FullscreenChanged raise lands on the UI thread whichever thread scanned (F309, F331). The getter can
# scan from a module's worker; the ORDER is what is asserted -- thread test, then Post, then the inline
# raise -- because a mutation that drops the marshalling leaves the inline raise standing and a presence
# check would still match it.
$noteScanBody = Get-MethodBody $startUpCodeHost 'internal void NoteFullscreenScan(bool[] blocked)' `
    @("`n        internal ", "`n        private ", "`n        public ")
Assert-True ($noteScanBody.Length -gt 0) 'NoteFullscreenScan was located'
$threadTest = $noteScanBody.IndexOf('Thread.CurrentThread.ManagedThreadId != uiThreadId')
$uiPost = $noteScanBody.IndexOf('uiContext.Post(')
$inlineRaise = $noteScanBody.LastIndexOf('Host.RaiseFullscreenChanged(any)')
Assert-True ($threadTest -ge 0 -and $uiPost -gt $threadTest -and $inlineRaise -gt $uiPost) (
    'a fullscreen scan on a worker thread posts its FullscreenChanged raise to the UI thread before the inline raise is reached')

# IsFullscreenActive stamps the ATTEMPT before it scans (F310), the way BlockedMonitorsForStandDown does,
# so the two entry points to the one scan share one design.
$fullscreenGetterBody = Get-MethodBody $startUpCodeHost 'internal bool IsFullscreenActive' `
    @("`n        internal ", "`n        private ", "`n        public ")
Assert-True (
    $fullscreenGetterBody.Length -gt 0 -and
    $fullscreenGetterBody.IndexOf('_fullscreenScanUtc = DateTime.UtcNow;') -ge 0 -and
    $fullscreenGetterBody.IndexOf('_fullscreenScanUtc = DateTime.UtcNow;') -lt $fullscreenGetterBody.IndexOf('FullscreenScan.BlockedMonitors(')
) 'the module-facing fullscreen getter stamps the attempt before it walks the desktop'

# The footer update stamp has ONE click handler (F373). The constructor attached one when the cached check
# offered an update and the interactive refresh attached another when its fresh answer differed, so a user two
# releases behind opened the releases page twice per click. The refresh may only RESTYLE the label; the single
# attach re-reads the cached answer at click time, which is what lets it be unconditional.
$optionsWindowCodeHost = Remove-LineComments $optionsWindowSource
$refreshStampBody = Get-MethodBody $optionsWindowCodeHost 'private static async void RefreshUpdateStampAsync(TextBlock label, string runningVersion)' `
    @("`n        private ", "`n        internal ", "`n        public ", "`n        protected ")
$openReleasesBody = Get-MethodBody $optionsWindowCodeHost 'private static void OpenReleasesPage(string runningVersion)' `
    @("`n        private ", "`n        internal ", "`n        public ", "`n        protected ")
Assert-True ($refreshStampBody.Length -gt 0 -and $openReleasesBody.Length -gt 0) 'RefreshUpdateStampAsync and OpenReleasesPage were located'
Assert-True (
    ([regex]::Matches($optionsWindowCodeHost, 'MouseLeftButtonUp \+=')).Count -eq 1 -and
    $refreshStampBody -cnotmatch 'MouseLeftButtonUp' -and
    $refreshStampBody -cnotmatch 'Process\.Start'
) 'the footer update stamp attaches exactly one click handler, and the interactive refresh only restyles it'
Assert-True (
    $openReleasesBody.IndexOf('AppUpdateCheck.OffersUpdate(runningVersion, latest)') -ge 0 -and
    $openReleasesBody.IndexOf('AppUpdateCheck.OffersUpdate(runningVersion, latest)') -lt $openReleasesBody.IndexOf('Process.Start(')
) 'the footer click re-reads the cached update answer before it opens anything'

# The window is FITTED to the work area before it is shown (F372). --wpf-options-selftest proves the pure
# InitialSize on displays this box does not have; this pins that the constructor CALLS it with the live work
# area and takes both axes from the answer, with no fixed height left standing beside it. The work area is
# the one StartupWorkArea reads for the monitor under the cursor, where an ownerless CenterScreen opens
# (RA-326, re-pointed by lane burn/host-shell): SystemParameters.WorkArea is the PRIMARY's and let a
# shorter secondary monitor open the caption off-screen, so its presence in the constructor is the old shape.
$optionsCtorBody = Get-MethodBody $optionsWindowCodeHost 'public OptionsWindow(IReadOnlyList<ShellPane> panes, string initialPaneTitle = null)' `
    @("`n        private ", "`n        internal ", "`n        public ", "`n        protected ")
Assert-True ($optionsCtorBody.Length -gt 0) 'the OptionsWindow constructor was located'
Assert-True (
    $optionsCtorBody -cmatch 'InitialSize\(PreferredSize, MinimumSize, StartupWorkArea\(\)\)' -and
    $optionsCtorBody -cnotmatch 'InitialSize\(PreferredSize, MinimumSize, SystemParameters\.WorkArea\)' -and
    $optionsCtorBody -cmatch 'Height = fitted\.Height;' -and
    $optionsCtorBody -cmatch 'Width = fitted\.Width;' -and
    $optionsCtorBody -cnotmatch 'Height = 820;'
) 'the settings window is fitted to the monitor it opens on, not the primary or a fixed 820'

# Every LocalData setter in the Preferences Save folds its durable result into ok (F369). Five diagnostic-log
# setters discarded it, so a failed save of only those fields greyed Apply out without the "could not be
# saved" dialog and handed the running logger the rolled-back values. A RATIO, so a setter added later is
# held to the same rule; the delegate is sliced from its Save = to the Actions = that follows it.
$optionsShellCodeHost = Remove-LineComments $optionsShellSource
$prefsSaveStart = $optionsShellCodeHost.IndexOf('Save = delegate(IReadOnlyDictionary<string, string> values)')
$prefsSaveEnd = $optionsShellCodeHost.IndexOf('Actions = BuildPreferencesActions(),', [Math]::Max($prefsSaveStart, 0))
Assert-True ($prefsSaveStart -ge 0 -and $prefsSaveEnd -gt $prefsSaveStart) 'the Preferences Save delegate was located'
$prefsSaveBody = $optionsShellCodeHost.Substring($prefsSaveStart, $prefsSaveEnd - $prefsSaveStart)
$prefsSetters = ([regex]::Matches($prefsSaveBody, 'data\.Set\w+\(')).Count
$prefsFolded = ([regex]::Matches($prefsSaveBody, 'ok &= data\.Set\w+\(')).Count
Assert-True ($prefsSetters -ge 18) "the Preferences Save writes through LocalData setters (found $prefsSetters)"
Assert-True ($prefsFolded -eq $prefsSetters) (
    "every LocalData setter in the Preferences Save folds its durable result into ok ($prefsFolded of $prefsSetters)")

# Reset to defaults leaves the dormant themeMode alone (F371): the page has had no theme control since the
# dropdown was dropped, so the only non-default value is a hand edit of settings.json, which the reset
# reverted silently. The audio-device reset beside it is the WITNESS that the slice still holds its setters.
$resetBodyHost = Get-MethodBody $optionsShellCodeHost 'private static string ResetToDefaultSettings()' @("`n        private ", "`n    }")
Assert-True ($resetBodyHost.Length -gt 0 -and $resetBodyHost -cmatch 'SetAudioDeviceId\(def\.AudioDeviceId\)') (
    'the reset-to-defaults body was located and still resets the audio device')
Assert-True ($resetBodyHost -cnotmatch 'SetThemeMode\(') 'reset to defaults does not touch the dormant theme mode, which the page does not show'

# The per-companion controls follow the STORE, not the click (F363), and the size row's success line is
# written only when the store took the value (F364). ORDER is what is asserted: in the sound handler the
# read-back precedes the control update; in ValueChanged the persist precedes the verdict test, which
# precedes the success-shaped line that used to overwrite persistPending's failure one statement later.
$companionsPaneCodeHost = Remove-LineComments $companionsPaneSource
$soundClickStart = $companionsPaneCodeHost.IndexOf('soundLink.Click += delegate')
$soundClickEnd = $companionsPaneCodeHost.IndexOf('line.Inlines.Add(soundLink);', [Math]::Max($soundClickStart, 0))
Assert-True ($soundClickStart -ge 0 -and $soundClickEnd -gt $soundClickStart) 'the per-companion sound toggle handler was located'
$soundClick = $companionsPaneCodeHost.Substring($soundClickStart, $soundClickEnd - $soundClickStart)
$soundReadBack = $soundClick.IndexOf('Program.MyData.IsPetSoundEnabled(addId)')
$soundFollow = $soundClick.IndexOf('enabled = stored;')
$soundText = $soundClick.IndexOf('soundRun.Text = enabled')
Assert-True (
    $soundReadBack -ge 0 -and $soundFollow -gt $soundReadBack -and $soundText -gt $soundFollow -and
    $soundClick -cnotmatch 'enabled = !enabled;'
) 'the sound link reads the store back and then shows what the store holds, never the click that failed'
$sizeRowBody = Get-MethodBody $companionsPaneCodeHost 'private FrameworkElement BuildSizeRow(string addId, string displayName)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($sizeRowBody.Length -gt 0) 'BuildSizeRow was located'
$sizePersist = $sizeRowBody.IndexOf('if (!dragging) persistPending();')
$sizeVerdict = $sizeRowBody.IndexOf('if (!storeTookIt) return;')
$sizeSuccess = $sizeRowBody.IndexOf('_status.Text = displayName + " size "')
Assert-True ($sizePersist -ge 0 -and $sizeVerdict -gt $sizePersist -and $sizeSuccess -gt $sizeVerdict) (
    'the size row announces a size only when the store took it')
Assert-True (
    $sizeRowBody -cmatch 'storeTookIt = storedPercent == pendingPercent;' -and
    $sizeRowBody -cmatch 'slider\.Value = storedPercent;'
) 'a failed size write moves the thumb back to the stored size'
$monitorRowBody = Get-MethodBody $companionsPaneCodeHost 'private FrameworkElement BuildMonitorRow(string addId, string displayName)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($monitorRowBody.Length -gt 0) 'BuildMonitorRow was located'
$monitorFailure = $monitorRowBody.IndexOf("Couldn't save the screen for")
$monitorRevert = $monitorRowBody.IndexOf('box.SelectedIndex = storedChoice >= 0 ? storedChoice + 1 : 0;')
Assert-True (
    $monitorFailure -ge 0 -and $monitorRevert -gt $monitorFailure -and $monitorRowBody -cmatch 'if \(syncingBox\) return;'
) 'a failed screen pin puts the combo back on the stored screen, behind a re-entrancy flag'

# The built-in card's icon is made ONCE and the resource Icon behind it is disposed (F365). LoadThumb caches
# its miss for the built-in id, so the fallback ran on every rebuild and left a live HICON per run.
$loadAppIconBody = Get-MethodBody $companionsPaneCodeHost 'private static ImageSource LoadAppIcon()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$loadAppIconCachedBody = Get-MethodBody $companionsPaneCodeHost 'private static ImageSource LoadAppIconCached()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$buildCardBody = Get-MethodBody $companionsPaneCodeHost 'private FrameworkElement BuildCard(CompanionRow row, Dictionary<string, int> mix)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($loadAppIconBody.Length -gt 0 -and $loadAppIconCachedBody.Length -gt 0 -and $buildCardBody.Length -gt 0) (
    'the built-in icon path was located')
Assert-True (
    $loadAppIconBody -cmatch 'using \(System\.Drawing\.Icon icon = DesktopAICompanion\.Properties\.Resources\.icon\)' -and
    $loadAppIconBody -cnotmatch 'Resources\.icon\.ToBitmap\(\)'
) 'the app icon resource is disposed after it is converted, not left to the finalizer'
Assert-True (
    $buildCardBody -cmatch 'LoadAppIconCached\(\)' -and
    $buildCardBody -cnotmatch 'LoadAppIcon\(\)' -and
    $loadAppIconCachedBody -cmatch '_iconCache\[CompanionCatalog\.BuiltInPetId\] = icon;'
) 'the built-in card takes its icon from the cache, keyed by the built-in id, instead of re-encoding it per rebuild'

# A new module is unpacked into staging and MOVED into place once whole (F367), and every interrupted install
# or update discards its staging folder and says so (F366). ORDER in InstallModuleAsync: extraction into the
# staged folder, then the move; extraction straight into installDir asserted absent.
$modulesPaneCodeHost = Remove-LineComments $modulesPaneSource
$installBody = Get-MethodBody $modulesPaneCodeHost 'private async Task InstallModuleAsync(CatalogModule module, Button install)' `
    @("`n        private ", "`n        internal ", "`n        public ")
$updateBody = Get-MethodBody $modulesPaneCodeHost 'private async Task UpdateModuleAsync(CatalogModule module, Button update, ModuleInfo installed)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($installBody.Length -gt 0 -and $updateBody.Length -gt 0) 'InstallModuleAsync and UpdateModuleAsync were located'
$installExtract = $installBody.LastIndexOf('ZipFile.ExtractToDirectoryAsync(zipStream, stagedHere, true, _netCts.Token)')
$installMove = $installBody.IndexOf('Directory.Move(stagedHere, installDir);')
Assert-True (
    $installExtract -ge 0 -and $installMove -gt $installExtract -and
    $installBody -cnotmatch 'ExtractToDirectoryAsync\(zipStream, installDir'
) 'a new module is unpacked into the staging folder and moved into modules/<id> whole, never extracted in place'
Assert-True (
    ([regex]::Matches($installBody, 'DiscardStaged\(stagedHere\);')).Count -eq 2 -and
    ([regex]::Matches($updateBody, 'DiscardStaged\(stagedHere\);')).Count -eq 2 -and
    $updateBody -cmatch '"Stopped updating "' -and
    $installBody -cmatch '"Stopped installing "'
) 'a cancelled or failed install or update discards its staging folder in both catches and says so'
$fetchPetBody = Get-MethodBody $companionsPaneCodeHost 'private async Task FetchPetAsync(CatalogCompanion pet, Button trigger, bool isUpdate)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($fetchPetBody.Length -gt 0) 'FetchPetAsync was located'
Assert-True (
    $fetchPetBody -cmatch '"Stopped " \+ \(isUpdate \? "updating " : "downloading "\)' -and
    $fetchPetBody -cnotmatch 'catch \(OperationCanceledException\) \{ \}'
) 'a cancelled companion download says so instead of leaving the status line to the check that cancelled it'

# The responder chains RECORD a throwing responder and treat it as declined (F328), the speech chain walks a
# SNAPSHOT (F332), and a target-less bubble re-shown from a worker is posted to the UI thread (F333). None of
# the three is reachable headless -- Program.MyData is null, so SpeechEnabled is false and the speech chain
# never runs -- so the ARGUMENT and the ORDER are asserted: the catch logs under the responder's module id and
# the bare Safe() wrapper is gone; ToArray() is what the foreach walks; the Post branch precedes the
# targeted-only InvokeRequired one and decides by thread id, not by context instance.
$petHostCodeHost = Remove-LineComments $petHostSource
$raiseChainBody = Get-MethodBody $petHostCodeHost 'private bool RaiseChain(List<Responder> chain, FormCompanion subject, string only, bool shuffle)' `
    @("`n        private ", "`n        internal ", "`n        public ")
# The three-argument overload is the implementation since RA-275 (lane burn/host-shell); the two-argument
# form forwards to it, so slicing from that signature would stop at the next member and see no chain.
$raiseSpeechBody = Get-MethodBody $petHostCodeHost 'internal bool RaiseSpeechRequest(FormCompanion target, string text, SpeechStyle style)' `
    @("`n        private ", "`n        internal ", "`n        public ")
$showBubbleBody = Get-MethodBody $petHostCodeHost 'internal void Show(double seconds)' `
    @("`n            private ", "`n            internal ", "`n            public ", "`n        }")
Assert-True ($raiseChainBody.Length -gt 0 -and $raiseSpeechBody.Length -gt 0 -and $showBubbleBody.Length -gt 0) (
    'RaiseChain, RaiseSpeechRequest and PendingBubble.Show were located')
Assert-True (
    $raiseChainBody -cmatch 'catch \(Exception ex\)' -and
    $raiseChainBody -cmatch 'Log\(r\.ModuleId, "responder threw and was treated as declined' -and
    $raiseChainBody -cnotmatch 'Safe\(' -and
    $raiseSpeechBody -cmatch 'Log\(r\.ModuleId, "speech responder threw and was treated as declined' -and
    $raiseSpeechBody -cnotmatch 'Safe\('
) 'a responder that throws is logged under its module id and treated as declined, in every chain'
Assert-True (
    $raiseSpeechBody -cmatch 'foreach \(SpeechResponder r in _speechResponders\.ToArray\(\)\)'
) 'the speech chain walks a snapshot, so a responder disposing itself in-callback cannot break the walk'
$bubblePost = $showBubbleBody.IndexOf('_host._ui.Post(')
$bubbleInvoke = $showBubbleBody.IndexOf('_target.InvokeRequired')
Assert-True (
    $bubblePost -ge 0 -and $bubbleInvoke -gt $bubblePost -and
    $showBubbleBody -cmatch 'Thread\.CurrentThread\.ManagedThreadId != _host\._uiThreadId'
) 'a bubble re-shown from a worker thread is posted to the UI thread before the targeted-only marshal is consulted'

# CompanionHost keeps no catalog copy of its own (F334): both catalog verbs read RemoteCatalogClient's shared
# copy, the one the panes read and "Check ... online" invalidates. The host's private copy had no TTL, so a pack
# re-published mid-session failed its hash check until the module re-browsed; its volatile publish (F335) went
# with it, and this invariant replaces that one.
Assert-True (
    $petHostCodeHost -cnotmatch '_catalogCache' -and
    ([regex]::Matches($petHostCodeHost, 'RemoteCatalogClient\s*\.FetchSharedAsync\(')).Count -ge 2 -and
    $petHostCodeHost -cnotmatch 'RemoteCatalogClient\s*\.FetchAsync\('
) "both catalog verbs read RemoteCatalogClient's shared copy and the host keeps no catalog cache of its own"

# The foreground process name comes from the snapshot entry that IS the foreground window (F329), and the
# old third GetForegroundWindow read is the FALLBACK, not the answer: ORDER of the capture and the use.
$captureBody = Get-MethodBody $petHostCodeHost 'public ScreenContext CaptureScreenContext(ICompanion pet)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($captureBody.Length -gt 0) 'CaptureScreenContext was located'
$foregroundCapture = $captureBody.IndexOf('foregroundProcess = w.ProcessName;')
$foregroundUse = $captureBody.IndexOf('ProcessName = !string.IsNullOrEmpty(foregroundProcess) ? foregroundProcess : ActiveWindow.ProcessName(),')
Assert-True (
    $foregroundCapture -ge 0 -and $foregroundUse -gt $foregroundCapture -and
    $captureBody -cnotmatch 'ProcessName = ActiveWindow\.ProcessName\(\),'
) 'the screen context names the foreground process from the window snapshot, falling back to a fresh read only when no entry is flagged'

# --aibrain-selftest switches the brain OFF again before the engine leg (F327). Enabling it for the
# declined-drop check reconfigures the session in the background (up to a 20 s server-start deadline, with
# AutoStartServer on by default) while the engine probe promises no live LLM and swaps the process-global log
# sink. The row's Label is static, so the same press is the off switch: TWO presses, the second before the probe.
$aiBrainSelfTestCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\Plugins\AiBrainModuleSelfTest.cs') -Raw)
$aiRunBody = Get-MethodBody $aiBrainSelfTestCode 'public static bool Run()' @("`n        private ", "`n        internal ", "`n        public ")
$firstEnablePress = $aiRunBody.IndexOf('host.ClickTray("Enable AI")')
$secondEnablePress = -1
if ($firstEnablePress -ge 0) { $secondEnablePress = $aiRunBody.IndexOf('host.ClickTray("Enable AI")', $firstEnablePress + 1) }
$engineLeg = $aiRunBody.IndexOf('AiEngineProbe')
Assert-True ($aiRunBody.Length -gt 0 -and $firstEnablePress -ge 0 -and $engineLeg -gt 0) (
    'the AiBrain self-test Run body, its Enable press and its engine leg were located')
Assert-True ($secondEnablePress -gt $firstEnablePress -and $secondEnablePress -lt $engineLeg) (
    'the AiBrain self-test presses Enable a second time, switching the brain OFF, before the engine leg runs')

# The convention runner names the loader's reason when it refuses a module (F341), and the commonest reason
# for an out-of-tree module -- this host's null GetStorage/GetSettings, which the shipped host never returns
# -- is named beside it. ORDER: the reasons follow the acceptance check they explain.
$conventionCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\Plugins\ModuleConventionSelfTest.cs') -Raw)
$conventionRun = Get-MethodBody $conventionCode 'public static bool Run(string moduleId)' @("`n        private ", "`n        internal ", "`n        public ")
$acceptedAt = $conventionRun.IndexOf('"the real loader accepted the module"')
$reasonsAt = $conventionRun.IndexOf('foreach (ModuleLoadFailure f in loader.Failures)')
Assert-True ($conventionRun.Length -gt 0 -and $acceptedAt -ge 0) 'the convention runner and its loader-acceptance check were located'
Assert-True ($reasonsAt -gt $acceptedAt -and $conventionRun -cmatch 'returns null from GetStorage/GetSettings') (
    "a module the convention host refused is reported with the loader's reason, and the null-storage cause is named")

# The loader is fail-closed on a partial type load (F342, register): a module any of whose types fails to load
# is refused whole, so the ReflectionTypeLoadException catch the finder carried was unreachable and is gone.
Assert-True (
    $conventionCode -cmatch 'Type\[\] types = assembly\.GetTypes\(\);' -and
    $conventionCode -cnotmatch 'catch \(ReflectionTypeLoadException'
) 'the self-test finder takes the whole type list the loader already accepted, with no partial-load catch of its own'

# Factory reset wipes the module staging folder too (F353): a staged or half-swapped update beside modules\
# survived both "Clear all settings and modules" and an MSI uninstall, which removes INSTALLFOLDER only when empty.
$factoryResetCode = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FactoryReset.cs') -Raw)
$factoryRunBody = Get-MethodBody $factoryResetCode 'internal static int Run()' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True (
    $factoryRunBody.Length -gt 0 -and
    $factoryRunBody -cmatch 'Wipe\(stagingRoot, "staged module updates", log\)' -and
    $factoryRunBody -cmatch 'PendingModuleUpdates\.DefaultStagingRoot'
) 'factory reset wipes the module staging folder beside modules\, at the path the update machinery uses'

# A removal that could not finish is handed to the loader (F352): the ids ProcessPending reports back are what
# LoadFrom is told to skip, so this launch does not lock again the folder the next launch must delete.
Assert-True (
    $startUpCodeHost -cmatch 'IReadOnlyList<string> stillRemoving = DesktopAICompanion\.Plugins\.PendingModuleRemovals\.ProcessPending\(' -and
    $startUpCodeHost -cmatch 'moduleHost\.LoadFrom\(modulesDir, Host, [^;]*, stillRemoving\);'
) 'the launch hands the loader the removals that could not finish, so it skips rather than re-locks them'

# A kill mid-RELOAD does not persist the shrinking transient mix (F308): ReloadPetType closes N pets and
# respawns N, then persists once, so the CONDITION on the KillSheep persist is what is asserted, not the
# presence of PersistMix, which the reverted code also calls.
$killSheepBody = Get-MethodBody $startUpCodeHost 'public bool KillSheep(FormCompanion sheep)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($killSheepBody.Length -gt 0) 'KillSheep was located'
Assert-True (
    $killSheepBody -cmatch 'if \(bSheepRemoved && !wasTransient && !reloadInProgress\) PersistMix\(\);' -and
    $killSheepBody -cnotmatch 'if \(bSheepRemoved && !wasTransient\) PersistMix\(\);'
) 'a pet closed by a reload does not persist the mix; the reload persists once at its end'

# Nothing spawns, restages or persists behind the exit (F312). KillSheeps sets the flag FIRST -- before the
# tray icon is disposed -- and closes the modal settings window; each entry point declines on it. The tray
# icon's SetIcon is a no-op once disposed, so a restage that raced the exit can no longer throw from inside
# its caller's catch and abandon the pending Application.Exit.
$killSheepsBody = Get-MethodBody $startUpCodeHost 'public void KillSheeps()' @("`n        private ", "`n        internal ", "`n        public ")
$loadNewBody = Get-MethodBody $startUpCodeHost 'public bool LoadNewXMLFromString(string strXml)' @("`n        private ", "`n        internal ", "`n        public ")
$addCoreBody = Get-MethodBody $startUpCodeHost 'private FormCompanion AddSheepCore(Xml petXml, Animations petAnimations, CompanionTypeRegistry.Entry entry)' @("`n        private ", "`n        internal ", "`n        public ")
$persistMixBody = Get-MethodBody $startUpCodeHost 'private void PersistMix()' @("`n        private ", "`n        internal ", "`n        public ")
$reloadTypeBody = Get-MethodBody $startUpCodeHost 'internal CompanionReloadOutcome ReloadPetType(string id, out int reloaded, out string error)' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True (
    $killSheepsBody.Length -gt 0 -and $loadNewBody.Length -gt 0 -and $addCoreBody.Length -gt 0 -and
    $persistMixBody.Length -gt 0 -and $reloadTypeBody.Length -gt 0
) 'KillSheeps, LoadNewXMLFromString, AddSheepCore, PersistMix and ReloadPetType were located'
$flagSet = $killSheepsBody.IndexOf('shuttingDown = true;')
$iconDisposed = $killSheepsBody.IndexOf('pi.Dispose();')
Assert-True (
    $flagSet -ge 0 -and $iconDisposed -gt $flagSet -and $killSheepsBody -cmatch 'OptionsShell\.CloseOpenWindow\(\)'
) 'KillSheeps raises the shutting-down flag before it disposes the tray icon, and closes the settings window'
Assert-True (
    $loadNewBody -cmatch 'if \(disposed \|\| shuttingDown\) return false;' -and
    $addCoreBody.IndexOf('if (shuttingDown)') -ge 0 -and
    $addCoreBody.IndexOf('if (shuttingDown)') -lt $addCoreBody.IndexOf('iSheeps >= MAX_SHEEPS') -and
    $persistMixBody -cmatch 'if \(disposed \|\| shuttingDown\) return;' -and
    $reloadTypeBody.IndexOf('if (shuttingDown)') -ge 0 -and
    $reloadTypeBody.IndexOf('if (shuttingDown)') -lt $reloadTypeBody.IndexOf('reloadInProgress = true;')
) 'every spawn, restage and persist entry point declines while the app is shutting down'
$processIconCodeHost = Remove-LineComments $processIconSource
$setIconBody = Get-MethodBody $processIconCodeHost 'public void SetIcon(System.IO.MemoryStream icon, string petName, string aboutAuthor, string aboutTitle, string aboutVersion, string aboutInfo)' `
    @("`n        private ", "`n        internal ", "`n        public ", "`n            /// ")
Assert-True (
    $setIconBody.Length -gt 0 -and
    $setIconBody.IndexOf('if (ni == null) return;') -ge 0 -and
    $setIconBody.IndexOf('if (ni == null) return;') -lt $setIconBody.IndexOf('bool success = true;')
) 'SetIcon is a no-op once the tray icon is disposed, before it touches anything'

# The built-in that runs after a rejected configured pet is keyed as the built-in (F305), and the rejected
# XML is left in settings rather than re-persisted under the built-in's key. ORDER: the rekey precedes the
# PetTypeId assignment, and the persist is conditional on not having fallen back.
$startUpCtorBody = Get-MethodBody $startUpCodeHost 'public StartUp(ProcessIcon processIcon)' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($startUpCtorBody.Length -gt 0) 'the StartUp constructor was located'
$rekey = $startUpCtorBody.IndexOf('activeId = CompanionCatalog.BuiltInPetId;')
$keyed = $startUpCtorBody.IndexOf('animations.PetTypeId = activeId;')
Assert-True (
    $rekey -ge 0 -and $keyed -gt $rekey -and
    $startUpCtorBody -cmatch 'if \(!fellBackToBuiltIn && !Program\.MyData\.SetXml\(candidate\)\)'
) 'a rejected configured pet leaves the built-in keyed as the built-in and the rejected XML unpersisted'

# The preview registry entry goes on the THROW path too (F307), not only on the null return.
$spawnPreviewBody = Get-MethodBody $startUpCodeHost 'internal FormCompanion SpawnPreviewPet(string animationsXml, out string error)' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($spawnPreviewBody.Length -gt 0) 'SpawnPreviewPet was located'
Assert-True (
    $spawnPreviewBody -cmatch 'catch \{ registry\.DropIfUnused\(entry\); throw; \}'
) 'a preview spawn that throws drops its registry entry on the way out'

# The tray handlers never dereference a Program.Mainthread that is not there yet (F282), and Exit is never a
# silent no-op.
$mouseClickBody = Get-MethodBody $processIconCodeHost 'void Ni_MouseClick(object sender, MouseEventArgs e)' @("`n        void ", "`n        private ", "`n        internal ", "`n        public ", "`n            /// ")
$mouseDoubleBody = Get-MethodBody $processIconCodeHost 'void Ni_MouseDoubleClick(object sender, MouseEventArgs e)' @("`n        void ", "`n        private ", "`n        internal ", "`n        public ", "`n        /// ")
$contextMenusCodeHost = Remove-LineComments $contextMenusSource
$exitClickBody = Get-MethodBody $contextMenusCodeHost 'void Exit_Click(object sender, EventArgs e)' @("`n        void ", "`n        private ", "`n        internal ", "`n        public ")
Assert-True ($mouseClickBody.Length -gt 0 -and $mouseDoubleBody.Length -gt 0 -and $exitClickBody.Length -gt 0) (
    'the tray click, double-click and Exit handlers were located')
Assert-True (
    $mouseClickBody -cmatch 'if \(main == null\) return;' -and $mouseClickBody -cnotmatch 'Program\.Mainthread\.TopMostSheeps' -and
    $mouseDoubleBody -cmatch 'if \(main == null\) return;' -and $mouseDoubleBody -cnotmatch 'Program\.Mainthread\.AddSheep' -and
    $contextMenusCodeHost -cnotmatch 'Program\.Mainthread\.SayAll\('
) 'the tray click, double-click and Test Speech handlers guard the main thread before using it'
Assert-True (
    $exitClickBody -cmatch 'else Application\.Exit\(\);' -and $exitClickBody -cnotmatch 'Program\.Mainthread\.KillSheeps\(\);'
) 'Exit quits outright when there is no StartUp to close the pets, never a silent no-op'

# One catalog.json per launch window (F286): the three due checks share the bytes, the app-version check
# parses only its block from them, and a pane's "check now" refills the shared copy it drops.
$remoteCatalogCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\RemoteCatalog.cs') -Raw)
$appVersionBody = Get-MethodBody $remoteCatalogCodeHost 'public static async Task<string> FetchAppVersionAsync(CancellationToken cancellationToken)' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($appVersionBody.Length -gt 0) 'FetchAppVersionAsync was located'
Assert-True (
    $appVersionBody -cmatch 'FetchSharedBytesAsync\(cancellationToken\)' -and
    $appVersionBody -cnotmatch 'SecureDownload\.DownloadBytesAsync\(' -and
    $appVersionBody -cmatch 'ParseAppVersion\('
) 'the app-version check reads the shared catalog bytes and still parses only its own block'
$petCheckBody = Get-MethodBody $startUpCodeHost 'private async System.Threading.Tasks.Task RunPetUpdateCheckAsync()' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True (
    $petCheckBody.Length -gt 0 -and
    $petCheckBody -cmatch 'RemoteCatalogClient\s*\.FetchSharedAsync\(' -and
    $startUpCodeHost -cnotmatch 'RemoteCatalogClient\s*\.FetchAsync\('
) 'the launch checks read the shared catalog copy, never a private download'
$companionsCheckBody = Get-MethodBody $companionsPaneCodeHost 'private async void CheckButton_Click(object sender, RoutedEventArgs e)' @("`n        private ", "`n        internal ", "`n        public ")
$modulesCheckBody = Get-MethodBody $modulesPaneCodeHost 'private async void CheckButton_Click(object sender, RoutedEventArgs e)' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True (
    $companionsCheckBody.Length -gt 0 -and $modulesCheckBody.Length -gt 0 -and
    $companionsCheckBody -cmatch 'RemoteCatalogClient\.RefreshSharedAsync\(' -and $companionsCheckBody -cnotmatch 'InvalidateShared\(\)' -and
    $modulesCheckBody -cmatch 'RemoteCatalogClient\.RefreshSharedAsync\(' -and $modulesCheckBody -cnotmatch 'InvalidateShared\(\)'
) "both panes' check-now buttons refill the shared catalog copy rather than dropping it for the next pane to fetch again"

# Every writer of a pet file invalidates the per-id caches through the one call (F249, F336): Companion
# Studio's install and uninstall through the host, and the pane's uninstall, beside the download that
# already did. The install also swaps the on-screen copies onto the new definition.
$installTypeBody = Get-MethodBody $petHostCodeHost 'public bool InstallType(string typeId, string animationsXml, out string error)' @("`n        private ", "`n        internal ", "`n        public ")
$uninstallTypeBody = Get-MethodBody $petHostCodeHost 'public bool UninstallType(string typeId, out string error)' @("`n        private ", "`n        internal ", "`n        public ")
$uninstallPetBody = Get-MethodBody $companionsPaneCodeHost 'private void UninstallPet(string id, string name, int onScreen)' @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($installTypeBody.Length -gt 0 -and $uninstallTypeBody.Length -gt 0 -and $uninstallPetBody.Length -gt 0) (
    'InstallType, UninstallType and UninstallPet were located')
Assert-True (
    $installTypeBody -cmatch 'CompanionCatalog\.Forget\(typeId\)' -and
    $installTypeBody -cmatch '_startUp\.ReloadPetType\(typeId' -and
    $uninstallTypeBody -cmatch 'CompanionCatalog\.Forget\(typeId\)'
) 'a Companion Studio install forgets the cached name and reloads the on-screen copies, and its uninstall forgets too'
Assert-True (
    $uninstallPetBody.IndexOf('Directory.Delete(dir, true)') -ge 0 -and
    $uninstallPetBody.IndexOf('CompanionCatalog.Forget(id)') -gt $uninstallPetBody.IndexOf('Directory.Delete(dir, true)') -and
    $companionsPaneCodeHost -cmatch 'CompanionCatalog\.Forgotten \+= ForgetStats;'
) "the pane's uninstall forgets the deleted pet's caches, and the pane's own caches follow the catalog's Forget"

# The Preferences Apply and the reset are ONE durable write each (F361): the setters run inside a batch and
# the batch's Commit is the write whose result reaches the user; what reads the store back runs after it.
$prefsSaveBatch = $prefsSaveBody.IndexOf('using (LocalData.Batch batch = data.BeginBatch())')
$prefsFirstSet = $prefsSaveBody.IndexOf('data.Set')
$prefsCommit = $prefsSaveBody.IndexOf('ok &= batch.Commit();')
$prefsConfigure = $prefsSaveBody.IndexOf('DiagnosticLog.Configure(')
Assert-True (
    $prefsSaveBatch -ge 0 -and $prefsFirstSet -gt $prefsSaveBatch -and $prefsCommit -gt $prefsFirstSet -and
    $prefsCommit -gt $prefsSaveBody.LastIndexOf('data.Set') -and $prefsConfigure -gt $prefsCommit
) 'the Preferences Apply opens a batch before its first setter, commits it once after the last, and configures the logger from the committed store'
Assert-True (
    $resetBodyHost -cmatch 'using \(LocalData\.Batch batch = data\.BeginBatch\(\)\)' -and
    $resetBodyHost -cmatch 'if \(!batch\.Commit\(\)\)' -and
    $resetBodyHost -cmatch 'Reset failed: the settings could not be saved'
) 'the reset is one committed batch whose failure is reported instead of a rebuilt pane over unmoved values'

# ---- lane fix/host, groups F to H: loader, ModuleKit, TestModule, tray, debug window, corpus scripts ----
$xmlCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Xml.cs') -Raw)
$animationsCodeHost = Remove-LineComments $animationsSource
$formDebugCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FormDebug.cs') -Raw)
$xmlToDotCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\Tools\XmlToDot.cs') -Raw)
$formPetDesignerCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FormCompanion.Designer.cs') -Raw)
$factoryResetCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FactoryReset.cs') -Raw)
$embeddedResourcesCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\DesktopAICompanion.ModuleKit\EmbeddedResources.cs') -Raw)
$jsonStoreCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\DesktopAICompanion.ModuleKit\JsonSettingsStore.cs') -Raw)
$testModuleCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'modules\TestModule\TestModule.cs') -Raw)
$sampleModuleCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'templates\desktop-ai-companion-module\SampleModule.cs') -Raw)
$classifySource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Fortunes\classify-corpus.py') -Raw
$stripAuthorsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Fortunes\strip-authors.py') -Raw
$hostMemberStops = @("`n        private ", "`n        internal ", "`n        public ", "`n        void ", "`n        static ")

# The loader reads the bytes the validator already decoded and proved, and decodes no base64 of its own (F318):
# one pass over a multi-megabyte string per staged pet, not two. The overload that skips the sprite staging is
# the additive shape PetStudio's F155 asked for, and its frame size comes from the PNG header. Asserted as an
# ABSENCE across the whole file beside the argument the loader passes.
$tryReadBodyHost = Get-MethodBody $xmlCodeHost 'public bool TryReadXml(string xmlText, bool stageImages, out string error)' $hostMemberStops
$readImagesBodyHost = Get-MethodBody $xmlCodeHost 'private void ReadImages(' $hostMemberStops
Assert-True ($tryReadBodyHost.Length -gt 0 -and $readImagesBodyHost.Length -gt 0) 'Xml.TryReadXml(xml, stageImages, out error) and ReadImages were located'
Assert-True (
    $xmlCodeHost -cnotmatch 'FromBase64String' -and
    $tryReadBodyHost -cmatch 'CompanionXmlValidator\.TryParse\(xmlText, out parsed, out sheetBytes, out iconBytes, out error\)' -and
    $readImagesBodyHost -cmatch 'if \(!stageImages\)' -and
    $readImagesBodyHost -cmatch 'ReadPngSize\(imageBytes, out sheetWidth, out sheetHeight\)'
) 'the loader takes the sheet and icon bytes the validator decoded, decodes no base64 of its own, and the no-stage path sizes the frame from the PNG header'

# The alpha flag is part of the commit block (F317): assigned after DisposeAssets, and ReadImages assigns a
# local, never the field, so a failed re-read cannot leave the old frames with the new pet's flag.
$disposeAssetsAt = $tryReadBodyHost.IndexOf('DisposeAssets();')
$usesAlphaAt = $tryReadBodyHost.IndexOf('usesAlpha = stagedUsesAlpha;')
Assert-True (
    $disposeAssetsAt -ge 0 -and $usesAlphaAt -gt $disposeAssetsAt -and
    $readImagesBodyHost -cnotmatch '(?m)^\s*usesAlpha ='
) 'the alpha flag is committed with the rest of the definition, after DisposeAssets, and ReadImages assigns a local instead of the field'

# The chooser announces the chosen animation and evaluates nothing (F241): the one consumer evaluates its own
# copy for the pet's DisplayIndex right after GetAnimation, and the old evaluate-and-store method is gone.
$announceBodyHost = Get-MethodBody $animationsCodeHost 'private void AnnounceChosenAnimation(int id)' $hostMemberStops
$setNewAnimationCoreBody = Get-MethodBody $formPetCodeHost 'private void SetNewAnimationCore(int id)' $hostMemberStops
Assert-True ($announceBodyHost.Length -gt 0 -and $setNewAnimationCoreBody.Length -gt 0) 'AnnounceChosenAnimation and SetNewAnimationCore were located'
# The anchor is asserted PRESENT before the order is compared (RA-365): IndexOf answers -1 for an absent
# needle and every present first operand is greater than -1, so a reshaped anchor (`TAnimation next =
# Animations.GetAnimation(id); CurrentAnimation = next;`) left "right after GetAnimation" green with
# nothing ordered. This and the debug window's trim-after-add below were the file's only unguarded
# order checks.
Assert-True (
    $announceBodyHost -cnotmatch '\.UpdateValues\(' -and
    $announceBodyHost -cmatch 'AddDebugInfo\(StartUp\.DEBUG_TYPE\.info, "new animation: "' -and
    $animationsCodeHost -cnotmatch 'UpdateAnimationValues' -and
    $setNewAnimationCoreBody.IndexOf('CurrentAnimation = Animations.GetAnimation(id);') -ge 0 -and
    $setNewAnimationCoreBody.IndexOf('CurrentAnimation.UpdateValues(DisplayIndex);') -gt $setNewAnimationCoreBody.IndexOf('CurrentAnimation = Animations.GetAnimation(id);')
) 'the chooser only announces the chosen animation; the one evaluation is the consumer''s, for its own DisplayIndex, right after GetAnimation'

# The drag-drop read is sized from the file (F271): the fixed 12 MiB buffer is gone, the capacity comes from
# BoundedReadCapacity given the stream, and the over-limit test on the bytes actually read stays.
$readBoundedBody = Get-MethodBody $formPetCodeHost 'private static string ReadBoundedPetXml(string file)' $hostMemberStops
$readBoundedBytesBody = Get-MethodBody $formPetCodeHost 'internal static int ReadBoundedBytes(Stream stream, int maximumBytes, out byte[] bytes)' $hostMemberStops
Assert-True ($readBoundedBody.Length -gt 0 -and $readBoundedBytesBody.Length -gt 0) 'ReadBoundedPetXml and ReadBoundedBytes were located'
Assert-True (
    $readBoundedBody -cnotmatch 'new byte\[' -and
    $readBoundedBody -cmatch 'total = ReadBoundedBytes\(stream, maximumBytes, out bytes\);' -and
    $readBoundedBody -cmatch 'if \(total > maximumBytes\)' -and
    $readBoundedBytesBody -cmatch 'bytes = new byte\[\(int\)BoundedReadCapacity\(stream, maximumBytes\)\];' -and
    $readBoundedBytesBody -cnotmatch 'checked\(maximumBytes \+ 1\)'
) 'the drop read allocates from the file''s length through BoundedReadCapacity and keeps the over-limit test on the bytes read'

# The embedded-resource suffix starts at a segment boundary (F229): FindName tries the exact name, then the
# boundary rule, and the bare EndsWith that let "icon.png" find "tray-icon.png" is gone.
$findNameBody = Get-MethodBody $embeddedResourcesCodeHost 'private static string FindName(Assembly assembly, string fileNameSuffix)' $hostMemberStops
$matchesBody = Get-MethodBody $embeddedResourcesCodeHost 'public static bool MatchesResourceName(string manifestName, string fileNameSuffix)' $hostMemberStops
Assert-True ($findNameBody.Length -gt 0 -and $matchesBody.Length -gt 0) 'EmbeddedResources.FindName and MatchesResourceName were located'
Assert-True (
    $findNameBody -cnotmatch 'candidate\.EndsWith\(' -and
    $findNameBody -cmatch 'MatchesResourceName\(candidate, fileNameSuffix\)' -and
    $matchesBody -cmatch "manifestName\[manifestName\.Length - fileNameSuffix\.Length - 1\] == '\.'"
) 'the resource lookup matches an exact name or a suffix that starts after a dot, never a bare EndsWith'

# JsonSettingsStore.Update reads, mutates and writes under ONE lease and refuses an unreadable document
# (F230, F231), and the write keeps the previous document as a backup. ORDER inside Update, not presence.
$jsonUpdateBody = Get-MethodBody $jsonStoreCodeHost 'public bool Update(Action<T> mutate)' $hostMemberStops
$jsonSaveCoreBody = Get-MethodBody $jsonStoreCodeHost 'private bool SaveCore(T value)' $hostMemberStops
Assert-True ($jsonUpdateBody.Length -gt 0 -and $jsonSaveCoreBody.Length -gt 0) 'JsonSettingsStore.Update and SaveCore were located'
$jsonLeaseAt = $jsonUpdateBody.IndexOf('CrossSessionLock.TryAcquire(')
$jsonReadAt = $jsonUpdateBody.IndexOf('TryRead(out current)')
$jsonRefuseAt = $jsonUpdateBody.IndexOf('if (result == ReadResult.Unreadable) return false;')
$jsonWriteAt = $jsonUpdateBody.IndexOf('SaveCore(current)')
Assert-True (
    $jsonLeaseAt -ge 0 -and $jsonReadAt -gt $jsonLeaseAt -and $jsonRefuseAt -gt $jsonReadAt -and $jsonWriteAt -gt $jsonRefuseAt -and
    $jsonUpdateBody -cnotmatch 'Load\(\)' -and $jsonUpdateBody -cnotmatch 'return Save\(' -and
    $jsonSaveCoreBody -cmatch 'AtomicFile\.TryWriteAllText\(_path, json, BackupPath_\)'
) 'Update takes its lease first, reads under it, refuses an unreadable document before mutating, writes under the same lease, and the write names the backup path'

# TestModule's preview verb reads the installed pet through the ABI (F207): no hand-rolled walk of the
# installed layout's %LOCALAPPDATA% library, which the portable dev tree never has.
$previewClickedBody = Get-MethodBody $testModuleCodeHost 'private void PreviewClicked()' $hostMemberStops
Assert-True ($previewClickedBody.Length -gt 0) 'TestModule.PreviewClicked was located'
Assert-True (
    $previewClickedBody -cmatch 'pets\.TryReadTypeXml\(type\.TypeId, out xml, out readError\)' -and
    $testModuleCodeHost -cnotmatch 'LocalApplicationData' -and
    $testModuleCodeHost -cnotmatch 'ReadInstalledXml'
) 'the preview verb reads the installed pet through ICompanionManager.TryReadTypeXml, with no hand-rolled path into the installed library'

# The module template holds ONE settings handle (F330 follow-up in the lane's own boundary): each
# IHost.GetSettings call is a file parse, so the sample every module is copied from must not fetch per call.
$templateSettingsBody = Get-MethodBody $sampleModuleCodeHost 'private IModuleSettings Settings()' $hostMemberStops
Assert-True ($templateSettingsBody.Length -gt 0) 'the template''s Settings() accessor was located'
Assert-True (
    $templateSettingsBody -cmatch 'if \(_settings == null && _host != null\) _settings = _host\.GetSettings\(Info\.Id\);' -and
    $sampleModuleCodeHost -cnotmatch 'Settings\(\) \{ return _host\.GetSettings'
) 'the template''s Settings() memoises one handle instead of fetching a fresh parse per call'

# Tray drop-downs are emptied with ClearAndDispose and module items disposed as trees (F255, F256): no
# DropDownItems.Clear() anywhere in the file, and the tree disposal reaches children and Image before the item.
$moduleTrayOpeningBody = Get-MethodBody $contextMenusCodeHost 'private void ModuleTray_Opening(object sender, System.ComponentModel.CancelEventArgs e)' $hostMemberStops
$rebuildSubmenuBody = Get-MethodBody $contextMenusCodeHost 'private static void RebuildModuleSubmenu(ToolStripMenuItem parent, TrayItem ti)' $hostMemberStops
$disposeTreeBody = Get-MethodBody $contextMenusCodeHost 'internal static void DisposeItemTree(ToolStripItem item)' $hostMemberStops
Assert-True ($moduleTrayOpeningBody.Length -gt 0 -and $rebuildSubmenuBody.Length -gt 0 -and $disposeTreeBody.Length -gt 0) 'ModuleTray_Opening, RebuildModuleSubmenu and DisposeItemTree were located'
Assert-True (
    $contextMenusCodeHost -cnotmatch 'DropDownItems\.Clear\(\);' -and
    $moduleTrayOpeningBody -cmatch 'DisposeItemTree\(prior\);' -and
    $rebuildSubmenuBody -cmatch 'ClearAndDispose\(parent\.DropDownItems\);' -and
    $contextMenusCodeHost -cmatch 'ClearAndDispose\(addPetMenuItem\.DropDownItems\);' -and
    $contextMenusCodeHost -cmatch 'ClearAndDispose\(removePetMenuItem\.DropDownItems\);' -and
    $contextMenusCodeHost -cmatch 'ClearAndDispose\(petSpeechMenuItem\.DropDownItems\);'
) 'no tray drop-down is Clear()ed without disposing its rows, and the module items are disposed as trees'
Assert-True (
    $disposeTreeBody.IndexOf('DisposeItemTree(child)') -ge 0 -and $disposeTreeBody.IndexOf('image.Dispose()') -ge 0 -and
    $disposeTreeBody.IndexOf('DisposeItemTree(child)') -lt $disposeTreeBody.IndexOf('item.Dispose()') -and
    $disposeTreeBody.IndexOf('image.Dispose()') -lt $disposeTreeBody.IndexOf('item.Dispose()')
) 'DisposeItemTree disposes the children and the Image before the item itself'

# The companion form deserialises no Icon (F272): no border, ShowIcon false, a tool window off the taskbar, so
# nothing could ever show the HICON the designer created per spawn.
$initComponentBody = Get-MethodBody $formPetDesignerCodeHost 'private void InitializeComponent()' @("`n        private ", "`n        #endregion", "`n        internal ", "`n        public ")
Assert-True ($initComponentBody.Length -gt 0) 'FormCompanion.InitializeComponent was located'
Assert-True (
    $initComponentBody -cnotmatch 'this\.Icon = ' -and
    $initComponentBody -cnotmatch 'ComponentResourceManager' -and
    $initComponentBody -cmatch 'this\.ShowIcon = false;'
) 'the companion form loads no Icon from its resx: ShowIcon is false and nothing could show one'

# The debug window caps its rows and hands text over by file (F273, F274): no P/Invoke, no WM_SETTEXT into a
# window it did not create, and a failed handoff is logged in the window instead of swallowed.
$debugMemberStops = @("`n`t`tprivate ", "`n`t`tinternal ", "`n`t`tpublic ", "`n        private ", "`n        internal ", "`n        public ")
$addDebugInfoBody = Get-MethodBody $formDebugCodeHost 'public void AddDebugInfo(StartUp.DEBUG_TYPE type, string text)' $debugMemberStops
$openTextBody = Get-MethodBody $formDebugCodeHost 'private static void OpenText(string kind, string text)' $debugMemberStops
Assert-True ($addDebugInfoBody.Length -gt 0 -and $openTextBody.Length -gt 0) 'FormDebug.AddDebugInfo and OpenText were located'
Assert-True (
    $formDebugCodeHost -cnotmatch 'DllImport' -and
    $formDebugCodeHost -cnotmatch 'SendMessageTimeout|FindWindowEx|MainWindowHandle' -and
    # Anchor present first, then the order (RA-365): an `Items.Add(row)` rename left the trim "after" -1.
    $addDebugInfoBody.IndexOf('listView1.Items.Add(item);') -ge 0 -and
    $addDebugInfoBody.IndexOf('TrimRows();') -gt $addDebugInfoBody.IndexOf('listView1.Items.Add(item);') -and
    $openTextBody -cmatch 'UseShellExecute = true' -and
    $openTextBody -cmatch 'catch \(Exception ex\)\s*\{\s*StartUp\.AddDebugInfo\(StartUp\.DEBUG_TYPE\.error,'
) 'the debug window trims after every add, opens its text through the shell from a file, and logs a failed handoff instead of swallowing it'

# The DOT export escapes every name it puts inside a label (F325), builds into a StringBuilder and writes no
# Console line a windowed process could read.
$dotMemberStops = @("`n`t`tstatic ", "`n`t`tprivate ", "`n`t`tinternal ", "`n`t`tpublic ")
$processNextBody = Get-MethodBody $xmlToDotCodeHost 'static private void ProcessNext(StringBuilder dot, Next type, int totalProbability, XmlData.AnimationNode anim, XmlData.NextNode[] nexts)' $dotMemberStops
$processAnimationsBody = Get-MethodBody $xmlToDotCodeHost 'static private string ProcessAnimations(string animationTitle, XmlData.AnimationNode[] animations)' $dotMemberStops
Assert-True ($processNextBody.Length -gt 0 -and $processAnimationsBody.Length -gt 0) 'XmlToDot.ProcessAnimations and ProcessNext were located'
Assert-True (
    $xmlToDotCodeHost -cnotmatch 'Console\.WriteLine' -and
    $processAnimationsBody -cmatch 'Append\(EscapeLabel\(anim\.Name\)\)' -and
    $processNextBody -cmatch 'Append\(EscapeLabel\(next\.OnlyFlag\)\)' -and
    $xmlToDotCodeHost -cnotmatch 'returnString \+='
) 'every animation name and only-flag in a DOT label passes through EscapeLabel, into a StringBuilder, with no Console line'

# A root that cannot be listed is a failed wipe (F260): SafeList counts and logs a listing that throws, and both
# of Wipe's listings pass the failure counter in.
$safeListBody = Get-MethodBody $factoryResetCodeHost 'internal static string[] SafeList(Func<string[]> list, string what, List<string> log, ref int failed)' $hostMemberStops
$wipeBody = Get-MethodBody $factoryResetCodeHost 'private static bool Wipe(string root, string what, List<string> log)' $hostMemberStops
Assert-True ($safeListBody.Length -gt 0 -and $wipeBody.Length -gt 0) 'FactoryReset.SafeList and Wipe were located'
Assert-True (
    $safeListBody -cmatch 'catch \(Exception ex\)\s*\{\s*failed\+\+;' -and
    $safeListBody -cnotmatch 'catch \{ return new string\[0\]; \}' -and
    ([regex]::Matches($wipeBody, 'ref failed\)')).Count -eq 2
) 'a listing that throws counts as a failure in SafeList, and both of Wipe''s listings pass the counter in'

# The corpus classifier decides the field layout ONCE per file (F321) and the byline stripper counts a stripped
# row only after the drop test (F323). Python, so no comment stripping; the bodies are sliced on def boundaries.
$classifyProcessBody = Get-MethodBody $classifySource 'def process(path):' @("`ndef ", "`nif __name__")
$stripProcessBody = Get-MethodBody $stripAuthorsSource 'def process(path):' @("`ndef ", "`nif __name__")
Assert-True ($classifyProcessBody.Length -gt 0 -and $stripProcessBody.Length -gt 0) 'both corpus transforms'' process() bodies were located'
Assert-True (
    $classifyProcessBody -cmatch '(?m)^\s+if layout is None:' -and
    $classifyProcessBody -cmatch '(?m)^\s+elif len\(parts\) != layout:' -and
    $classifyProcessBody -cmatch '(?m)^\s+if layout == 6:' -and
    $classifyProcessBody -cnotmatch 'if len\(parts\) == 6:'
) 'the classifier fixes the field layout from row 1, rejects a later row that differs, and no longer infers the schema per row'
$stripDropAt = $stripProcessBody.IndexOf('dropped += 1')
$stripChangedAt = $stripProcessBody.IndexOf('changed += 1')
$stripContinueAt = if ($stripDropAt -ge 0) { $stripProcessBody.IndexOf('continue', $stripDropAt) } else { -1 }
Assert-True (
    $stripDropAt -ge 0 -and $stripContinueAt -gt $stripDropAt -and $stripChangedAt -gt $stripContinueAt
) 'the stripper counts a byline as stripped only for a row it kept: the count follows the drop test''s continue'

# The fullscreen scan's decision half is MonitorDecider, and the enumeration offers it only what passed its own
# filters (N-host-04): visible, not iconic, not cloaked, not the shell, in that ORDER before Offer. The rule
# itself (the topmost real window at a monitor's centre decides it; a decided monitor stays decided; a
# companion's own window never decides) is pinned at runtime in --fullscreen-selftest against described windows;
# this asserts the order the runtime probe cannot see, and that no decision is made outside the decider.
$fullscreenScanCodeHost = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FullscreenScan.cs') -Raw)
$blockedMonitorsBody = Get-MethodBody $fullscreenScanCodeHost 'public static bool[] BlockedMonitors(ICollection<IntPtr> petHandles)' $hostMemberStops
Assert-True ($blockedMonitorsBody.Length -gt 0) 'FullscreenScan.BlockedMonitors was located'
$scanVisibleAt = $blockedMonitorsBody.IndexOf('if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;')
$scanCloakAt = $blockedMonitorsBody.IndexOf('if (IsCloaked(hWnd) || IsShell(hWnd)) return true;')
$scanOfferAt = $blockedMonitorsBody.IndexOf('decider.Offer(hWnd, ')
Assert-True (
    $scanVisibleAt -ge 0 -and $scanCloakAt -gt $scanVisibleAt -and $scanOfferAt -gt $scanCloakAt -and
    $blockedMonitorsBody -cmatch 'new MonitorDecider\(monitors, petHandles\)' -and
    $blockedMonitorsBody -cnotmatch 'IsFullscreenOnMonitor'
) 'the enumeration filters hidden, iconic, cloaked and shell windows before it offers a window to the decider, and the decision itself lives in MonitorDecider'



# ---- lane fix/tools ----
# (invariants added by lane fix/tools go directly below this line)



# ---- lane fix/remembrance ----
# (invariants added by lane fix/remembrance go directly below this line)



# ---- lane fix/blinkingled ----
# (invariants added by lane fix/blinkingled go directly below this line)



# ---- lane fix/aibrain ----
# (invariants added by lane fix/aibrain go directly below this line)



# ---- lane fix/fortunes ----
# (invariants added by lane fix/fortunes go directly below this line)

# The smart picker's supersession and its publish take ONE lock (F144). The generation check and the
# publish were two unlocked steps in the build's worker, and the bump and the clear two unlocked steps in
# RebuildEngine, so a build that had just passed its check could publish after the field was cleared and
# the generation moved on: a superseded picker went live and the next build overwrote it without disposing
# it. A two-thread interleaving no self-test can force, so the SHAPE is asserted: each pair sits inside a
# `lock (_smartLock)` block, check before publish and bump before clear. Sliced to the two methods' own
# bodies and matched as a single-depth block, so a statement moved past the block's closing brace fails
# it; -cmatch, so a renamed lock fails rather than matching prose. The ORDER-only line further up (the
# check precedes the publish) stays: it is the half a lock does not give.
$fortunesModuleCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\Fortunes\FortunesModule.cs') -Raw)
$smartBuildBody = Get-MethodBody $fortunesModuleCode `
    'private void BuildSmartPicker(int generation, SmartFortunes old, List<FortuneEntry> pool)' `
    @("`n        private ", "`n        internal ", "`n        public ")
$smartScheduleBody = Get-MethodBody $fortunesModuleCode `
    'private void ScheduleSmartPicker(bool wanted, List<FortuneEntry> pool, bool force)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($smartBuildBody.Length -gt 0 -and $smartScheduleBody.Length -gt 0) 'BuildSmartPicker and ScheduleSmartPicker exist and could be sliced out for inspection'
Assert-True (
    $smartBuildBody -cmatch 'lock \(_smartLock\)\s*\{[^}]*Volatile\.Read\(ref _smartGeneration\) != generation;[^}]*_smart = built;[^}]*\}'
) "the smart picker's generation check and its publish sit inside one lock (_smartLock), check first"
Assert-True (
    $smartScheduleBody -cmatch 'lock \(_smartLock\)\s*\{[^}]*Interlocked\.Increment\(ref _smartGeneration\);[^}]*_smart = null;[^}]*\}'
) 'a rebuild bumps the smart generation and clears the picker inside the same lock, bump first'



# ---- lane fix/petstudio ----
# (invariants added by lane fix/petstudio go directly below this line)

# BUG-012 (F155): the studio's analysis leaves the UI thread, and a result a newer request overtook is
# dropped before anything renders. The pool-thread half is asserted by --module-selftest=petstudio (the
# staged Xml carries no sprite frame and adopts the validator's parse). WHERE the analysis runs and the
# generation compare ahead of the render are dispatcher ordering no headless self-test can force, so the
# SHAPE is asserted: PetAnalyzer.Analyze is called exactly once in the window and that call sits inside
# the awaited Task.Run, and the continuation compares the generation before RenderAnalysis. -cmatch, so a
# renamed field or method fails rather than matching prose.
$petStudioWindowCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\PetStudio\PetStudioWindow.cs') -Raw)
$beginAnalyzeBody = Get-MethodBody $petStudioWindowCode `
    'private async void BeginAnalyze(string statusPrefix)' `
    @("`n        private ", "`n        internal ")
Assert-True ($beginAnalyzeBody.Length -gt 0) 'BeginAnalyze exists and could be sliced out for inspection'
Assert-True (
    ($beginAnalyzeBody -cmatch 'await Task\.Run\(delegate \{ return PetAnalyzer\.Analyze\(xml\); \}\)') -and
    ([regex]::Matches($petStudioWindowCode, 'PetAnalyzer\.Analyze\(').Count -eq 1)
) 'the studio analyzes on a pool thread: PetAnalyzer.Analyze is called once in the window, inside the awaited Task.Run (BUG-012)'
Assert-True (
    $beginAnalyzeBody -cmatch '(?s)await Task\.Run.*Volatile\.Read\(ref _analyzeGeneration\) == generation && report != null\) RenderAnalysis\('
) 'an analysis a newer request overtook is dropped unrendered: the generation is compared ahead of RenderAnalysis'

# F165: the dropped-step count Resync hands back reaches the ONE status the analysis writes. The note used
# to be written by Resync and overwritten by the verdict a few statements later in the same Analyze, so it
# never rendered; AnalysisStatus is pinned as a pure function in --module-selftest=petstudio, and this is
# the wiring: the count RenderMap returns is the argument, not a literal.
$renderAnalysisBody = Get-MethodBody $petStudioWindowCode `
    'private void RenderAnalysis(PetReport report, string statusPrefix)' `
    @("`n        private ", "`n        internal ")
Assert-True ($renderAnalysisBody.Length -gt 0) 'RenderAnalysis exists and could be sliced out for inspection'
Assert-True (
    ($renderAnalysisBody -cmatch 'int droppedSteps = RenderMap\(report\);') -and
    ($renderAnalysisBody -cmatch 'AnalysisStatus\(report\.IsValid, report\.UnreachableAnimations\.Count, droppedSteps\)')
) 'the dropped-step count Resync returns reaches the one status the analysis writes (F165)'

# The zip-import lifecycle (F158, F163, F159, F160, F162): five dispatcher-ordering and thread-placement
# facts no headless self-test can drive, asserted as shape on the method bodies. Each slice gets its
# non-vacuity line first.
#
# F158: the zip import OWNS _importing through its extraction. The guard tested a flag that was first set
# inside ImportSkinFromRootAsync, after the extraction await, so for the seconds a skin took to extract a
# second click deleted the tree the extractor was writing. ORDER: set before the first await, and never
# after one (a `_importing = true` after an await is the old shape wherever it sits).
$importZipBody = Get-MethodBody $petStudioWindowCode 'private async void ImportShimejiZip()' `
    @("`n        private ", "`n        internal ")
Assert-True ($importZipBody.Length -gt 0) 'ImportShimejiZip exists and could be sliced out for inspection'
Assert-True (
    ($importZipBody -cmatch '(?s)_importing = true;.*await Task\.Run') -and
    ($importZipBody -cnotmatch '(?s)await .*_importing = true;')
) 'the zip import sets _importing before its extraction await, and never after one (F158)'

# F163: Open and the installed picker refuse during an import, as the two imports already did.
$openFileBody = Get-MethodBody $petStudioWindowCode 'private void OpenFile()' `
    @("`n        private ", "`n        internal ")
$pickedBody = Get-MethodBody $petStudioWindowCode 'private void OnInstalledPicked()' `
    @("`n        private ", "`n        internal ")
$importFolderBody = Get-MethodBody $petStudioWindowCode 'private async void ImportShimeji()' `
    @("`n        private ", "`n        internal ")
$ctorBody = Get-MethodBody $petStudioWindowCode 'internal PetStudioWindow(IHost host)' `
    @("`n        private ", "`n        internal ")
$importCoreBody = Get-MethodBody $petStudioWindowCode 'private async Task ImportSkinFromRootCoreAsync(string root)' `
    @("`n        private ", "`n        internal ")
$beginDeleteBody = Get-MethodBody $petStudioWindowCode 'private void BeginDeleteExtracted()' `
    @("`n        private ", "`n        internal ")
$beginSweepBody = Get-MethodBody $petStudioWindowCode 'private static void BeginOrphanSweep()' `
    @("`n        private ", "`n        internal ")
Assert-True (
    $openFileBody.Length -gt 0 -and $pickedBody.Length -gt 0 -and $importFolderBody.Length -gt 0 -and
    $ctorBody.Length -gt 0 -and $importCoreBody.Length -gt 0 -and $beginDeleteBody.Length -gt 0 -and
    $beginSweepBody.Length -gt 0
) 'OpenFile, OnInstalledPicked, ImportShimeji, the constructor, the import core, BeginDeleteExtracted and BeginOrphanSweep could be sliced out for inspection'
Assert-True (
    ($openFileBody -cmatch 'if \(_importing\)') -and ($pickedBody -cmatch 'if \(_importing\)')
) 'Open and the installed picker are refused while an import is converting (F163)'

# F159: the recursive deletes and the sweep run on a pool thread. Directory.Delete appears in the window
# exactly twice (DeleteTree and the sweep), the zip path's delete of the previous tree is the first
# statement of the awaited Task.Run, Closed's delete is handed to Task.Run, and the sweep runs inside one.
Assert-True (
    ([regex]::Matches($petStudioWindowCode, 'Directory\.Delete\(').Count -eq 2) -and
    ($importZipBody -cmatch '(?s)await Task\.Run\(delegate\s*\{\s*DeleteTree\(previous\);') -and
    ($beginDeleteBody -cmatch 'Task\.Run\(delegate \{ DeleteTree\(path\); \}\)') -and
    ($beginSweepBody -cmatch '(?s)Task\.Run\(delegate\s*\{.*SweepOrphanedExtractions\(\);')
) 'the extraction trees are deleted and swept on a pool thread, never in the click handler or in Closed (F159)'

# F160: the sweep is started on every load path, and when the window opens.
Assert-True (
    ($ctorBody -cmatch 'BeginOrphanSweep\(\);') -and ($openFileBody -cmatch 'BeginOrphanSweep\(\);') -and
    ($pickedBody -cmatch 'BeginOrphanSweep\(\);') -and ($importFolderBody -cmatch 'BeginOrphanSweep\(\);') -and
    ($importZipBody -cmatch 'BeginOrphanSweep\(\);')
) 'the orphan sweep is started at construction and on every load path: Open, the picker, both imports (F160)'

# F162: the skin folder is remembered at the two entry points from what the author chose, and never from
# the root the core is handed (which is the extraction tree on the zip path).
Assert-True (
    ($importCoreBody -cnotmatch 'RememberSkinDir\(') -and
    ($importFolderBody -cmatch 'RememberSkinDir\(root\);') -and
    ($importZipBody -cmatch 'RememberSkinDir\(Path\.GetDirectoryName\(dlg\.FileName\)\);')
) 'the remembered skin folder is the one the author chose: the import core remembers nothing (F162)'

# F157: picking an installed companion offers Save, as Open and an import do (Save prompts for a path while
# _openedPath is null). The button used to keep whatever state history had left it. Asserted on the
# ASSIGNMENT to true, so a mutation that writes the button's own state back to it does not pass.
Assert-True (
    $pickedBody -cmatch '_saveButton\.IsEnabled = true;'
) 'picking an installed companion offers Save, as Open and an import do (F157)'



# ---- lane fix/reminder ----
# (invariants added by lane fix/reminder go directly below this line)



# ---- lane fix/agentflow ----
# (invariants added by lane fix/agentflow go directly below this line)



# ---- lane fix/followups ----
# (invariants added by lane fix/followups go directly below this line)



# ---- lane burn/reminder ----
# (invariants added by lane burn/reminder go directly below this line)

# EVERY Process.Start IN THE REMINDER MODULE IS DISPOSED. The Join tray entry opens a meeting link through a
# shell-execute Process.Start whose returned Process (null when the shell hands back none, which a using
# accepts) waited for the finalizer, one handle per click: the Reminder site RA-123 named beside the Fortunes
# folder button. The Fortunes lane pinned its module's shape; this is the same pin for Reminder, over every
# .cs file of the module, comment-stripped, both counts reported, the total asserted non-zero first (a module
# with no Start site must not read as a pass), and the file of any bare site named so the failure says where
# to look. Two counts over the same text, not a lookbehind, for the reason the Fortunes block records.
$reminderStartSites = 0
$reminderStartDisposed = 0
$reminderBareFiles = @()
foreach ($reminderFile in (Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\Reminder') -Filter *.cs -File)) {
    $reminderCode = Remove-LineComments (Get-Content -LiteralPath $reminderFile.FullName -Raw)
    $reminderFileSites = ([regex]::Matches($reminderCode, 'Process\.Start\(')).Count
    $reminderFileDisposed = ([regex]::Matches($reminderCode, 'using \((?:System\.Diagnostics\.)?Process\.Start\(')).Count
    $reminderStartSites += $reminderFileSites
    $reminderStartDisposed += $reminderFileDisposed
    if ($reminderFileSites -gt $reminderFileDisposed) { $reminderBareFiles += $reminderFile.Name }
}
Assert-True ($reminderStartSites -gt 0) (
    "the Reminder module's Process.Start sites are countable (found $reminderStartSites)")
Assert-True (($reminderStartSites - $reminderStartDisposed) -eq 0) (
    "every Process.Start in the Reminder module disposes what it returns (sites $reminderStartSites, " +
    "disposed $reminderStartDisposed" +
    $(if ($reminderBareFiles.Count -gt 0) { '; bare in ' + ($reminderBareFiles -join ', ') } else { '' }) + ')')

# ---- lane burn/blinkingled ----
# (invariants added by lane burn/blinkingled go directly below this line)
# ---- lane burn/host-shell ----
# (invariants added by lane burn/host-shell go directly below this line)

# The Preferences Save reads the Run-key registration BACK after writing it, and so does the reset (RA-320,
# RA-321). StartupRegistration.Set is a swallowing void, so a Run key a policy has made read-only made Apply
# report success while the rebuilt pane silently un-ticked the box; the read-back (IsEnabled() == b) is what
# lets the refusal be said. ORDER asserted in both bodies: Set, then the read-back. $prefsSaveBody and
# $resetBodyHost are the comment-stripped slices the F369 and F371 checks above cut.
$prefsStartupSet = $prefsSaveBody.IndexOf('StartupRegistration.Set(b);')
$prefsStartupReadBack = $prefsSaveBody.IndexOf('StartupRegistration.IsEnabled() == b')
Assert-True ($prefsStartupSet -ge 0 -and $prefsStartupReadBack -gt $prefsStartupSet) (
    'the Preferences Save reads the Run-key registration back after writing it, so a refused write is reported')
$resetStartupSet = $resetBodyHost.IndexOf('StartupRegistration.Set(false);')
$resetStartupReadBack = $resetBodyHost.IndexOf('StartupRegistration.IsEnabled()')
Assert-True ($resetStartupSet -ge 0 -and $resetStartupReadBack -gt $resetStartupSet) (
    'reset to defaults reads the Run-key registration back after clearing it')

# The chosen output device is applied to the running companion AFTER the batch commits and FROM THE STORE
# (RA-322). Inside the batch, with the requested value, a failed commit left the runtime on the new device and
# the store on the old. ORDER: Commit precedes the apply; ARGUMENT: the store's read-back; and the old
# argument shape is refused.
$prefsCommit = $prefsSaveBody.IndexOf('ok &= batch.Commit();')
$prefsApplyDevice = $prefsSaveBody.IndexOf('ApplyAudioDevice(data.GetAudioDeviceId())')
Assert-True (
    $prefsCommit -ge 0 -and $prefsApplyDevice -gt $prefsCommit -and $prefsSaveBody -cnotmatch 'ApplyAudioDevice\(toStore\)'
) 'the output device is applied after the commit, from what the store holds, never from the requested value inside the batch'

# Reset to defaults does not write the global size fallback either (RA-325): F371's principle, applied to
# ScaleLevel, which the page does not show. The F371 check above is the WITNESS that the slice still holds
# its setters.
Assert-True ($resetBodyHost -cnotmatch 'SetScale\(') 'reset to defaults does not write the global size fallback, which the page does not show'

# The Companions controller refuses a null runtime before it dereferences one (RA-311): Load tolerated a
# null runtime and UsePet/AddPet then threw NullReferenceException. ORDER: the guard precedes the first
# use in each body.
$optionsControllerCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Options\OptionsController.cs') -Raw)
$usePetBody = Get-MethodBody $optionsControllerCode 'public OpResult UsePet(string petId)' @("`n        public ", "`n        internal ", "`n        private ")
$addPetBody = Get-MethodBody $optionsControllerCode 'public OpResult AddPet(string petId)' @("`n        public ", "`n        internal ", "`n        private ")
Assert-True ($usePetBody.Length -gt 0 -and $addPetBody.Length -gt 0) 'the Companions controller''s UsePet and AddPet were located'
Assert-True (
    $usePetBody.IndexOf('if (_runtime == null)') -ge 0 -and
    $usePetBody.IndexOf('if (_runtime == null)') -lt $usePetBody.IndexOf('_runtime.LoadNewXMLFromString') -and
    $addPetBody.IndexOf('if (_runtime == null)') -ge 0 -and
    $addPetBody.IndexOf('if (_runtime == null)') -lt $addPetBody.IndexOf('_runtime.AddPetFromTray')
) 'UsePet and AddPet refuse a null runtime before they dereference it'

# The Companions pane's Remove reports what RemoveOnePet answered (RA-313), and Use/Add show the controller's
# reason on failure instead of a guess (RA-310). $buildCardBody is the comment-stripped BuildCard slice the
# F365 check above cut. ARGUMENT: the bool is captured and the status is conditional on it; the failure text
# is built from OpResult.Message and the guessed literals are gone.
Assert-True (
    $buildCardBody -cmatch 'removed = Program\.Mainthread\.RemoveOnePet\(removeId\);' -and
    $buildCardBody -cmatch '_status\.Text = removed \?' -and
    $buildCardBody -cnotmatch '_status\.Text = "Removed one "'
) 'the Remove button reports a removal only when RemoveOnePet says one happened'
Assert-True (
    ([regex]::Matches($buildCardBody, 'PaneText\.Short\(r\.Message\)')).Count -eq 2 -and
    $buildCardBody -cnotmatch 'max companions reached\?' -and
    $buildCardBody -cnotmatch '"Couldn''t apply that companion\."'
) 'the Use and Add buttons show the controller''s reason on failure, not a guess'

# UninstallPet is contained by the one library rule (RA-315): the last inline copy of the containment F337
# folded into CompanionProvenance.SafeLibraryDirectory kept accepting what the install and download paths
# refused. ARGUMENT: the shared helper is called with the id, and the inline StartsWith test is gone.
$uninstallPetBody = Get-MethodBody $companionsPaneCodeHost 'private void UninstallPet(string id, string name, int onScreen)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($uninstallPetBody.Length -gt 0) 'UninstallPet was located'
Assert-True (
    $uninstallPetBody -cmatch 'CompanionProvenance\.SafeLibraryDirectory\(id\)' -and
    $uninstallPetBody -cnotmatch 'StartsWith\(root \+ Path\.DirectorySeparatorChar'
) 'UninstallPet contains the delete through CompanionProvenance.SafeLibraryDirectory, not an inline copy of the rule'

# ModuleHost publishes its loaded set as an immutable snapshot (RA-276): Modules is read off the UI thread
# by CompanionHost.PlaySound's permission lookup while LoadFrom may still be adding, and a LINQ projection
# over the mutable list could see a growth mid-enumeration. ARGUMENT: the getter returns the snapshot and
# never projects the live list.
$moduleHostCode = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Plugins\ModuleHost.cs') -Raw)
Assert-True (
    $moduleHostCode -cmatch 'public IReadOnlyList<IModule> Modules \{ get \{ return _snapshot; \} \}' -and
    $moduleHostCode -cnotmatch 'Modules \{ get \{ return _loaded\.Select' -and
    ([regex]::Matches($moduleHostCode, 'PublishSnapshot\(\);')).Count -eq 2
) 'ModuleHost.Modules answers from a snapshot republished after every add and on shutdown, never from the live list'

# A claimed styled line keeps its SpeechStyle when a voice module re-shows it (RA-275): the styled Say hands
# the style to the offer, and the pending bubble draws with it on both the targeted and the broadcast path.
# ARGUMENT asserted at all three sites; the plain two-argument offer is refused in the styled Say. No
# headless check can draw a bubble (it needs a FormCompanion), so the source is the layer that sees this.
$styledSayBody = Get-MethodBody $petHostCodeHost 'public void Say(ICompanion pet, string text, SpeechStyle style)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True (
    $styledSayBody.Length -gt 0 -and
    $styledSayBody -cmatch 'RaiseSpeechRequest\(p\.Pet, text, style\)' -and
    $styledSayBody -cnotmatch 'RaiseSpeechRequest\(p\.Pet, text\)' -and
    $showBubbleBody -cmatch 'SayWithDwell\(_text, dwell, _style\)' -and
    $showBubbleBody -cmatch 'ShowBubbleOnAll\(_text, dwell, _style\)'
) 'a styled line offered to the speech chain keeps its style through the host-supplied ShowBubble'
# ---- lane burn/petstudio ----
# (invariants added by lane burn/petstudio go directly below this line)

# RA-145: a rejected re-parse has no graph, so the behaviour timeline is not resynced against its empty node
# list. The RULE (nothing is dropped without a graph) is pinned as a pure function in --module-selftest=petstudio;
# this is the WIRING, which only the window knows, in both of its halves: RenderMap hands the report's validity
# to Resync, and it clears and rebuilds the map's node dictionary only inside the valid branch, so the last
# accepted graph stands while the text is rejected. Each half alone saves the author's chain, and both are
# asserted so neither can rot unseen behind the other.
$petStudioModuleCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\PetStudio\PetStudioModule.cs') -Raw)
$timelinePaneCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\PetStudio\TimelinePane.cs') -Raw)
$renderMapBody = Get-MethodBody $petStudioWindowCode 'private int RenderMap(PetReport report)' `
    @("`n        private ", "`n        internal ")
$loadConvertedBody = Get-MethodBody $petStudioWindowCode `
    'private void LoadConvertedIntoEditor(ConversionResult result, string name, string extra)' `
    @("`n        private ", "`n        internal ")
$runDebugPetBody = Get-MethodBody $petStudioWindowCode 'private bool RunDebugPet(string debugXml)' `
    @("`n        private ", "`n        internal ")
$openAsyncBody = Get-MethodBody $petStudioModuleCode 'private System.Threading.Tasks.Task<string> OpenAsync()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$timelineRunBody = Get-MethodBody $timelinePaneCode 'private async void Run()' `
    @("`n        private ", "`n        internal ")
Assert-True (
    $renderMapBody.Length -gt 0 -and $loadConvertedBody.Length -gt 0 -and $runDebugPetBody.Length -gt 0 -and
    $openAsyncBody.Length -gt 0 -and $timelineRunBody.Length -gt 0
) 'RenderMap, LoadConvertedIntoEditor, RunDebugPet, OpenAsync and TimelinePane.Run could be sliced out for inspection'
Assert-True (
    ($renderMapBody -cmatch '_timeline\.Resync\(report\.IsValid\)') -and
    ($renderMapBody -cnotmatch 'Resync\((true|false)\)') -and
    ($renderMapBody -cmatch '(?s)if \(report\.IsValid\)\s*\{[^}]*_nodesById\.Clear\(\);') -and
    ($renderMapBody -cnotmatch '(?s)_nodesById\.Clear\(\);.*if \(report\.IsValid\)')
) 'a rejected re-parse does not resync the timeline: RenderMap hands Resync the report''s validity and clears the node dictionary only inside the valid branch (RA-145)'

# RA-140: a close overtakes everything in flight. ORDER inside the Closed handler: the flag first, then the
# generation bump and the rerun cleared, before the timers stop; and the three continuations that can land in a
# closed window -- the analysis, the conversion, the chain build -- each test the flag before touching it.
Assert-True (
    ($ctorBody -cmatch '(?s)Closed \+= delegate\s*\{\s*_closed = true;\s*Interlocked\.Increment\(ref _analyzeGeneration\);\s*_analyzeRerun = false;\s*_analyzeRerunPrefix = null;\s*_playTimer\.Stop\(\);') -and
    ($beginAnalyzeBody -cmatch '(?s)^private async void BeginAnalyze\(string statusPrefix\)\s*\{\s*if \(_closed\) return;') -and
    ($loadConvertedBody -cmatch '(?s)^private void LoadConvertedIntoEditor\([^)]*\)\s*\{\s*if \(_closed\) return;') -and
    ($runDebugPetBody -cmatch '(?s)^private bool RunDebugPet\(string debugXml\)\s*\{\s*if \(_closed\) return false;.*SpawnPreview\(')
) 'closing the studio overtakes the analysis in flight (flag first, generation bumped, rerun cleared) and the analysis, the conversion and the chain build each refuse a closed window (RA-140)'

# RA-133: the chain build (two validating parses, each an XSD compile and a full GDI+ decode of the sheet, and a
# whole-document serialize) runs on a pool thread: BuildDebugXml is called exactly once in the pane, inside the
# awaited Task.Run behind the single-flight gate, and the preview is spawned only after the await.
Assert-True (
    ([regex]::Matches($timelinePaneCode, 'BehaviourChain\.BuildDebugXml\(').Count -eq 1) -and
    ($timelineRunBody -cmatch '(?s)Interlocked\.CompareExchange\(ref _buildInFlight, 1, 0\).*await Task\.Run\(delegate\s*\{[^}]*BehaviourChain\.BuildDebugXml\(') -and
    ($timelineRunBody -cmatch '(?s)await Task\.Run\(.*_runDebugPet\(built\)')
) 'Run chain compiles the debug pet on a pool thread: BuildDebugXml is called once in the pane, inside the awaited Task.Run behind the single-flight gate, and the preview is spawned only after the await (RA-133)'

# RA-142: the zip import refuses a second import BEFORE its picker, as the folder import and Open do, and still
# after it (the modal dialog pumps messages, so a conversion can start while it is open).
Assert-True (
    $importZipBody -cmatch '(?s)^private async void ImportShimejiZip\(\)\s*\{\s*if \(_importing\) \{ SetStatus\(StillConverting\); return; \}\s*var dlg = new Microsoft\.Win32\.OpenFileDialog.*ShowDialog\(this\)[^;]*;\s*if \(_importing\) \{ SetStatus\(StillConverting\); return; \}'
) 'the zip import refuses a second import before its picker and again after it (RA-142)'

# RA-138: the settings pane's status beside "Open Companion Studio" comes from the open's outcome, never from a
# constant. OpenStatus is pinned as a pure function in --module-selftest=petstudio; this is the wiring.
Assert-True (
    ($openAsyncBody -cmatch 'OpenStatus\(TryOpen\(out failureCategory\), failureCategory\)') -and
    ($openAsyncBody -cnotmatch 'Task\.FromResult\("')
) 'reports a failed open on the pane: OpenAsync routes TryOpen''s outcome through OpenStatus and carries no constant status text (RA-138)'

# F161 / RA-147: no PetStudio member carries two <summary> blocks. The compiler merges consecutive summaries
# silently and IntelliSense shows the first, so the stranded one is the one that gets lost; two of them sat on
# SweepAgeHours and one on CanPlayOnPreview. Over the RAW text, since the comments are the subject.
$petStudioSourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\PetStudio') -Filter '*.cs' -File)
$petStudioSummaries = 0
$petStudioStackedSummaries = 0
foreach ($petStudioFile in $petStudioSourceFiles) {
    $petStudioRaw = Get-Content -LiteralPath $petStudioFile.FullName -Raw
    $petStudioSummaries += [regex]::Matches($petStudioRaw, '/// <summary>').Count
    $petStudioStackedSummaries += [regex]::Matches($petStudioRaw, '</summary>[ \t]*\r?\n[ \t]*/// <summary>').Count
}
Assert-True ($petStudioSourceFiles.Count -ge 5 -and $petStudioSummaries -gt 20) (
    "the PetStudio sources carry countable <summary> blocks ($petStudioSummaries in $($petStudioSourceFiles.Count) files)")
Assert-True ($petStudioStackedSummaries -eq 0) (
    "no PetStudio member carries two <summary> blocks: a summary is never stacked directly on another (found $petStudioStackedSummaries; F161, RA-147)")
# ---- lane burn/tools ----
# (invariants added by lane burn/tools go directly below this line)

# THE CONVERTER CLI'S SHAPES. tools\ShimejiConvert\Program.cs has no self-test of its own (the selftest
# verb runs the engine's suites), so what the burn-down changed in it is pinned here, method-scoped and
# comment-stripped, asserting the ARGUMENT or the ORDER and the absence of the old shape.
$burnToolsCliCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert\Program.cs') -Raw)

# RA-368: reloop rejects 1.1 -- the version it stamps itself -- and used to say "not one this migration
# understands" about its own output. SkipOrStranded names `reground` for it, like every other verb.
$burnToolsReloopBody = Get-MethodBody $burnToolsCliCode 'private static int Reloop(string petsDirectory, string bundlesDirectory)'
Assert-True ($burnToolsReloopBody.Length -gt 0) 'Program.Reloop exists and could be sliced out for inspection'
Assert-True ($burnToolsReloopBody -cmatch 'SkipOrStranded\(root\.Header\.Version\)' -and
    $burnToolsReloopBody -cnotmatch 'not one this migration understands') (
    "reloop's version skip goes through SkipOrStranded, which names the verb for the rung, not a message of its own")

# RA-369: the shape test admits only taskbar/window-bottom edges, so none of the three situations can be
# present; the "already has this edge" loop and its "added nothing" bail could never run and their comment
# credited them with the version gate's idempotence. Positive control: the three edges are still added.
$burnToolsRegroundBody = Get-MethodBody $burnToolsCliCode 'private static int Reground(string petsDirectory)'
Assert-True ($burnToolsRegroundBody.Length -gt 0) 'Program.Reground exists and could be sliced out for inspection'
Assert-True ($burnToolsRegroundBody -cmatch 'foreach \(string situation in situations\)\s*edges\.Add\(new XmlData\.NextNode' -and
    $burnToolsRegroundBody -cnotmatch 'bool already') (
    'reground adds the three situations unconditionally after its shape test; the unreachable already-present guard is gone')

# RA-370: one array. PetGraph.ReservedEntryPointNames is the declaration (the fix/deadcode invariant below
# compares it with the loader); the CLI used to carry a fourth literal copy that neither drift gate read.
# The needle is the two-name HEAD of the literal, not all four names: a copy keeps its head, and the
# fix/deadcode mutation that drops "sync" from the declaration must reach ITS assertion below instead of
# failing this WITNESS first (Assert-True throws, so the first failure is the only one reported).
$burnToolsReservedLiteral = '"fall",\s*"drag"'
$burnToolsPetGraphCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert.Engine\PetGraph.cs') -Raw)
$burnToolsEmitterCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert.Engine\Emit\PetEmitter.cs') -Raw)
$burnToolsEngineCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert.Engine\Engine.cs') -Raw)
Assert-True ($burnToolsPetGraphCode -cmatch $burnToolsReservedLiteral) (
    'WITNESS: PetGraph.cs still declares the reserved names as a literal whose head is "fall", "drag"')
Assert-True (($burnToolsCliCode -cnotmatch $burnToolsReservedLiteral) -and
    ($burnToolsEmitterCode -cnotmatch $burnToolsReservedLiteral) -and
    ($burnToolsEngineCode -cnotmatch $burnToolsReservedLiteral)) (
    'no literal copy of the reserved animation names outside PetGraph: Program.cs, PetEmitter.cs and Engine.cs read the one array')

# RA-372: the fixed-point diagnostic is set exactly when the pet is valid and does not round-trip, and both
# convert verbs printed the engine's reason under `if (!r.Valid)` alone.
$burnToolsConvertBody = Get-MethodBody $burnToolsCliCode 'private static int ConvertVerb('
$burnToolsConvertBundleBody = Get-MethodBody $burnToolsCliCode 'private static int ConvertBundleVerb('
Assert-True ($burnToolsConvertBody.Length -gt 0 -and $burnToolsConvertBundleBody.Length -gt 0) (
    'both convert verbs exist and could be sliced out for inspection')
Assert-True ($burnToolsConvertBody -cmatch 'PrintConversionError\(r\)' -and
    $burnToolsConvertBundleBody -cmatch 'PrintConversionError\(r\)' -and
    $burnToolsConvertBody -cnotmatch 'if \(!r\.Valid\) Console\.Error\.WriteLine' -and
    $burnToolsConvertBundleBody -cnotmatch 'if \(!r\.Valid\) Console\.Error\.WriteLine') (
    "both convert verbs print the engine's reason whenever it has one, not only when the validator refused")

# N-deadcode-03: the serialise / re-validate / reachability / write tail is one helper, and every migration
# verb goes through it. Positive control first: the helper does each of the four things.
$burnToolsCommitBody = Get-MethodBody $burnToolsCliCode 'private static bool CommitMigratedPet('
Assert-True ($burnToolsCommitBody -cmatch 'TryValidate\(' -and $burnToolsCommitBody -cmatch 'Analyze\(' -and
    $burnToolsCommitBody -cmatch 'WritePetXmlPreservingEncoding\(path, outXml\)') (
    'CommitMigratedPet serialises, re-validates, proves reachability and then writes with the encoding preserved')
$burnToolsMigrationVerbs = @('Reweight', 'Rebalance', 'Rejump', 'Reclimb', 'RestSplit', 'Dedupe', 'Undirect', 'Reloop', 'Reground')
$burnToolsVerbsMissingCommit = @()
$burnToolsVerbsWritingDirectly = @()
foreach ($burnToolsVerb in $burnToolsMigrationVerbs) {
    $burnToolsVerbBody = Get-MethodBody $burnToolsCliCode ('private static int ' + $burnToolsVerb + '(')
    if ($burnToolsVerbBody.Length -eq 0 -or $burnToolsVerbBody -cnotmatch 'CommitMigratedPet\(path, root, out commitFailure\)') {
        $burnToolsVerbsMissingCommit += $burnToolsVerb
    }
    if ($burnToolsVerbBody -cmatch 'WritePetXmlPreservingEncoding\(') { $burnToolsVerbsWritingDirectly += $burnToolsVerb }
}
Assert-True ($burnToolsVerbsMissingCommit.Count -eq 0 -and $burnToolsVerbsWritingDirectly.Count -eq 0) (
    'every migration verb commits through CommitMigratedPet and none writes the file itself (missing: ' +
    ($burnToolsVerbsMissingCommit -join ',') + '; direct writers: ' + ($burnToolsVerbsWritingDirectly -join ',') + ')')

# The mount-prefix item's reclimb half: the rung used to write repeatfrom 0 on every surface pose while
# lengthening the repeat, so the rung that fixed reach was also the one that flattened a mount. It keeps
# the value the sequence carries and feeds the solver that same value.
$burnToolsReclimbBody = Get-MethodBody $burnToolsCliCode 'private static int Reclimb(string petsDirectory)'
Assert-True ($burnToolsReclimbBody.Length -gt 0) 'Program.Reclimb exists and could be sliced out for inspection'
Assert-True ($burnToolsReclimbBody -cmatch 'SurfaceRepeatForReach\(frames, repeatFrom\)' -and
    $burnToolsReclimbBody -cnotmatch 'RepeatFromFrame = 0;') (
    'reclimb keeps the repeatfrom a sequence carries and solves the reach around it, instead of flattening a mount prefix to 0')

# RA-333: THE TEMPLATE'S OWN INSTRUCTIONS. Raw text, not comment-stripped, because the doc block IS a
# comment: it told a new author to add the self-test flag to run-gate.ps1 and build.yml, two files that
# stopped carrying a flag list on 2026-09-17, and template.json named a flag shape the host never parsed.
$burnToolsTemplateSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'templates\desktop-ai-companion-module\SampleModule.cs') -Raw
$burnToolsTemplateDoc = [regex]::Match($burnToolsTemplateSource,
    '(?s)/// The module''s self-test, reached by REFLECTION.*?public static bool SelfTest\(')
$burnToolsTemplateJson = Get-Content -LiteralPath (
    Join-Path $repoRoot 'templates\desktop-ai-companion-module\.template.config\template.json') -Raw
Assert-True ($burnToolsTemplateDoc.Success -and $burnToolsTemplateDoc.Value.Length -gt 0) (
    "the template's self-test doc block exists and could be sliced out for inspection")
Assert-True ($burnToolsTemplateDoc.Value -cmatch 'Test-ModuleSelfTests\.ps1' -and
    $burnToolsTemplateDoc.Value -cnotmatch 'then add that flag to tests\\run-gate\.ps1') (
    'the template''s self-test doc block sends an in-tree author to Test-ModuleSelfTests.ps1 ($Covered), not to the retired run-gate.ps1/build.yml flag lists')
Assert-True ($burnToolsTemplateJson -cmatch '--module-selftest=<id>' -and
    $burnToolsTemplateJson -cnotmatch '\(--<id>-selftest\)') (
    'template.json names the self-test flag as --module-selftest=<id>, the shape the host parses')
# ---- lane burn/remembrance ----
# (invariants added by lane burn/remembrance go directly below this line)

# EVERY SETTINGS WRITE A REMEMBRANCE PANE ACTION MAKES REPORTS ITS RESULT. IModuleSettings.Save() reports
# a failed write by returning false, never by throwing, and seven Save() calls in six user-triggered
# Remembrance actions discarded that bool and answered with a tick (RA-157): a user who had waited out a
# multi-hundred-megabyte Whisper download read "✓ Whisper is ready" over paths that were gone at the next
# launch. The module now routes every such write through TrySaveSettings, whose false becomes the action's
# answer and one log line. The module self-test drives two of the seven through AdoptWhisperPaths; the other
# five open a dialog or reach the network, so this pins the SHAPE for them: no line of the comment-stripped
# file is a bare `_settings.Save();` whose bool goes nowhere. The pane's own Save keeps `bool ok =
# _settings.Save();`, and the self-test's `linkModule._settings.Save();` is another object's handle; neither
# is a line-start `_settings.Save();`. The positive control is the helper's own definition, so a renamed or
# deleted helper cannot pass as "nothing discards".
$remembranceModuleCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\Remembrance\RemembranceModule.cs') -Raw)
Assert-True ($remembranceModuleCode.Length -gt 0 -and
    $remembranceModuleCode -cmatch 'private bool TrySaveSettings\(string what, out string notPersisted\)') (
    'RemembranceModule.cs could be read and defines TrySaveSettings, the one Save() its actions call')
$discardedRemembranceSaves = [regex]::Matches($remembranceModuleCode, '(?m)^[ \t]*_settings\.Save\(\);[ \t]*$').Count
Assert-True ($discardedRemembranceSaves -eq 0) (
    "no Remembrance pane action discards a Save() result (found $discardedRemembranceSaves bare _settings.Save() statements)")
# ---- lane burn/scripts-pack ----
# (invariants added by lane burn/scripts-pack go directly below this line)

# CI RUNS THE MODULE SELF-TESTS AFTER A RED PRODUCT SELF-TEST STEP. build.yml's module self-test step ran
# under GitHub Actions' default success() condition, so one failed host self-test skipped all seven module
# suites and the same push had to be made twice to learn about a broken module (RA-001; before F397 four of
# those suites sat inside the product step, whose failures are collected). The step carries an `if:` keyed
# on the BUILD step's outcome, and the build step carries the id it reads; `always()` is not the answer,
# because Test-ModuleSelfTests.ps1 throws without a build. Sliced per step like the release.yml check under
# the fix/scripts anchor: one slice from each `- name:` to the next, comment lines dropped, the ARGUMENT of
# the if: asserted and not the presence of one. Positive control first: both steps are still there to judge.
$buildWorkflowText = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\build.yml') -Raw
$buildWorkflowSteps = @([regex]::Split($buildWorkflowText, '(?m)^      - name:') | Select-Object -Skip 1 |
    ForEach-Object { $_ -replace '(?m)^[ \t]*#.*$', '' })
$buildStepSlices = @($buildWorkflowSteps | Where-Object { $_ -cmatch '\A Build Release x64 and the portable ZIP' })
$moduleSelfTestSlices = @($buildWorkflowSteps | Where-Object { $_ -cmatch "\A Run each module's own self-test" })
Assert-True ($buildStepSlices.Count -eq 1 -and $moduleSelfTestSlices.Count -eq 1) (
    "build.yml has one build step and one module self-test step to judge (found $($buildStepSlices.Count) and $($moduleSelfTestSlices.Count))")
$moduleSelfTestCondition = '        if: ${{ !cancelled() && steps.build.outcome == ''success'' }}'
Assert-True (($buildStepSlices[0] -cmatch '(?m)^        id: build\s*$') -and
             ($moduleSelfTestSlices[0] -cmatch ('(?m)^' + [regex]::Escape($moduleSelfTestCondition) + '\s*$'))) (
    "build.yml's module self-test step runs whenever the build step succeeded, red product self-tests or not (RA-001)")

# THE NUGET PACK RUNS ON EVERY PUSH. New-NuGetPackages.ps1 (dotnet pack, the README/lib assertions and the
# dependency-free check) first executed on a v* tag, the one release-only build step left (RA-190); build.yml
# runs it after the MSI and ships the packages in the artifact. The whole file is judged comment-stripped so
# the prose that explains the step cannot satisfy it.
$buildWorkflowCode = $buildWorkflowText -replace '(?m)^[ \t]*#.*$', ''
$nugetPackRun = '        run: .\packaging\New-NuGetPackages.ps1 -OutputDirectory dist\nuget'
Assert-True (($buildWorkflowCode -cmatch ('(?m)^' + [regex]::Escape($nugetPackRun) + '\s*$')) -and
             ($buildWorkflowCode -cmatch '(?m)^            dist/nuget/\*\.nupkg\s*$')) (
    'build.yml packs the module-author NuGet packages on every push and uploads them with the ZIP and MSI (RA-190)')

# THE RELEASE NOTES SAY SIGNED OR UNSIGNED FROM THE THUMBPRINT. release.yml hardcoded "Unsigned" in the notes
# while its own header promised the first signed release needs no workflow edit (RA-002). The publish step's
# env maps the thumbprint (the F002 route) and the --notes text is built from $signedWord; the old literal is
# asserted absent from every run body of the file.
$releaseWorkflowText = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml') -Raw
$releasePublishSlices = @([regex]::Split($releaseWorkflowText, '(?m)^      - name:') | Select-Object -Skip 1 |
    ForEach-Object { $_ -replace '(?m)^[ \t]*#.*$', '' } | Where-Object { $_ -cmatch '\A Publish the GitHub release' })
$releaseNotesFromThumbprint = '--notes "$signedWord x64 build for $tag'
Assert-True ($releasePublishSlices.Count -eq 1 -and
             ($releasePublishSlices[0] -cmatch '(?m)^          SIGNING_THUMBPRINT: \$\{\{ steps\.signing\.outputs\.thumbprint \}\}\s*$') -and
             ($releasePublishSlices[0] -cmatch [regex]::Escape($releaseNotesFromThumbprint))) (
    "release.yml's publish step takes the thumbprint through env and words the notes from it (RA-002)")
$releaseRunBodiesForNotes = @()
foreach ($releaseNoteStep in @([regex]::Split($releaseWorkflowText, '(?m)^      - name:') | Select-Object -Skip 1)) {
    $noteRunAt = [regex]::Match($releaseNoteStep, '(?m)^        run:')
    if ($noteRunAt.Success) { $releaseRunBodiesForNotes += ($releaseNoteStep.Substring($noteRunAt.Index) -replace '(?m)^[ \t]*#.*$', '') }
}
Assert-True ($releaseRunBodiesForNotes.Count -ge 5 -and @($releaseRunBodiesForNotes | Where-Object { $_ -cmatch 'Unsigned x64 build' }).Count -eq 0) (
    'no release.yml run body hardcodes "Unsigned x64 build" any more (RA-002)')

# build.ps1 REFUSES A MISSING OR UNDECLARED MODULE PROJECT BEFORE IT RESTORES. Both module-list assertions
# read only the list and the filesystem and used to run after the restore and the host compile, so a leftover
# modules\TemplateCheck surfaced after the minutes the host build takes (RA-203). An ORDER check on the
# comment-stripped script: the two refusal texts are unique to their sites, and both must precede the first
# restore line. Positive control first, so a renamed message cannot pass as "in order".
$buildScriptCode = (Get-Content -LiteralPath (Join-Path $repoRoot 'build.ps1') -Raw) -replace '(?m)^[ \t]*#.*$', ''
$undeclaredModuleThrowAt = $buildScriptCode.IndexOf('module project(s) on disk but not declared in build.ps1')
$missingModuleThrowAt = $buildScriptCode.IndexOf('module project(s) declared in build.ps1 but not found on disk')
$restoreLineAt = $buildScriptCode.IndexOf("Write-Host 'Restoring NuGet packages...'")
Assert-True ($undeclaredModuleThrowAt -gt 0 -and $missingModuleThrowAt -gt 0 -and $restoreLineAt -gt 0) (
    'build.ps1 still carries both module-list refusals and the restore step (positive control for the order check)')
Assert-True ($missingModuleThrowAt -lt $restoreLineAt -and $undeclaredModuleThrowAt -lt $restoreLineAt) (
    'build.ps1 refuses a missing or undeclared module project BEFORE it restores NuGet packages (RA-203)')
# ---- lane burn/aibrain ----
# (invariants added by lane burn/aibrain go directly below this line)

# THE AIBRAIN PROBE WRITES NO SKIP LINE. Both runners (Invoke-SelfTests.ps1 for --aibrain-selftest,
# Test-ModuleSelfTests.ps1 for the COVERED module) grade a SKIP: line as a failure of the whole run, so a
# "skip-pass" never existed: F083 turned the DPAPI skip into a FAIL naming the check, and the Windows OCR
# recognizer (AiEngineProbe.cs) and the clone's DPAPI branch (AiEngineProbe.Lifecycle.cs) followed on 2026-09-30
# (RA-069, RA-070, R-017). Comment-stripped, so the prose that records the old shape cannot trip it; the positive
# control is that the two FAIL-naming checks are present and the files were read at all.
$aibrainProbeSources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\AiBrain\engine') -Filter 'AiEngineProbe*.cs' -File)
$aibrainProbeCode = (@($aibrainProbeSources | ForEach-Object { Remove-LineComments (Get-Content -LiteralPath $_.FullName -Raw) }) -join "`n")
Assert-True ($aibrainProbeSources.Count -ge 5 -and $aibrainProbeCode.Length -gt 0 -and
    ($aibrainProbeCode -cmatch 'so the in-context read can be asserted') -and
    ($aibrainProbeCode -cmatch 'so the clone''s key isolation can be asserted')) (
    'the aibrain probe names the check an absent OCR recognizer or DPAPI store stops (positive control, RA-069, R-017)')
Assert-True ($aibrainProbeCode -cnotmatch '"SKIP:') (
    'the aibrain probe writes no SKIP: line: an absent recognizer or DPAPI store is a FAIL naming the check (RA-069, RA-070, R-017)')

# ONE OBSERVE-FAULT CONTINUATION IN THE AIBRAIN ENGINE. F064 consolidated the two copies it named onto
# AiEndpointPolicy.ObserveTaskFailure and its summary said "one helper"; FallbackBackend.ObserveOutcome and
# OllamaClient.ObserveLateStarterFailure were two more, byte-equivalent (RA-086). Counted over the
# comment-stripped engine sources: the OnlyOnFaulted continuation exists in AiEndpointPolicy.cs and nowhere else,
# and the other files reach it by name. Positive control first: the helper is called, qualified, from at least the
# callers that used to carry copies (AiBrain, AiSessionManager, FallbackBackend, OllamaClient, AiBrainModule).
$aibrainEngineSources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\AiBrain\engine') -Filter '*.cs' -File)
$aibrainEngineCode = (@($aibrainEngineSources | ForEach-Object { Remove-LineComments (Get-Content -LiteralPath $_.FullName -Raw) }) -join "`n")
$aibrainOnlyOnFaulted = @($aibrainEngineSources | Where-Object {
        (Remove-LineComments (Get-Content -LiteralPath $_.FullName -Raw)) -cmatch 'TaskContinuationOptions\.OnlyOnFaulted' } |
    ForEach-Object { $_.Name })
Assert-True ($aibrainEngineSources.Count -ge 10 -and
    ([regex]::Matches($aibrainEngineCode, 'AiEndpointPolicy\.ObserveTaskFailure\(')).Count -ge 6) (
    'AiEndpointPolicy.ObserveTaskFailure is the engine''s observe-fault helper and is called from its former copies (positive control, RA-086)')
Assert-True (($aibrainOnlyOnFaulted -join ',') -ceq 'AiEndpointPolicy.cs') (
    'one observe-fault continuation in the AiBrain engine: OnlyOnFaulted appears in AiEndpointPolicy.cs alone (found ' +
    ($aibrainOnlyOnFaulted -join ',') + ') (RA-086)')
# ---- lane burn/fortunes ----
# (invariants added by lane burn/fortunes go directly below this line)

# NO INVISIBLE CHARACTER IS A LITERAL IN THE FORTUNES ENGINE. FortuneProvider.cs states the rule at IsZeroWidth
# ("By CODE POINT, never as a literal") and carried one exception: LooksTagged stripped a per-line BOM with a
# raw U+FEFF between single quotes, which reads as '' in an editor, which a whitespace-normalising tool or a
# copy-paste would have deleted without a visible diff, and which could not change the result for any
# reachable input (R-029). A file's own BOM at byte 0 is its encoding, not a literal, so the scan starts at the
# second character; every source file of the module is checked, not only the one that states the rule, and
# the generated files under obj\ are left out because they are not source.
$fortunesEngineFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\Fortunes') -Filter '*.cs' -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
Assert-True ($fortunesEngineFiles.Count -ge 7) (
    "the Fortunes module's source files are countable (found $($fortunesEngineFiles.Count))")
foreach ($fortunesFile in $fortunesEngineFiles) {
    $fortunesText = Get-Content -LiteralPath $fortunesFile.FullName -Raw -Encoding UTF8
    Assert-True ($fortunesText.Length -gt 0 -and $fortunesText.IndexOf([char]0xFEFF, 1) -lt 0) (
        "no U+FEFF literal past the first character in $($fortunesFile.Name) (zero-width characters are spelled by code point)")
}

# A SUPERSEDED SMART BUILD STOPS BEFORE IT CONSTRUCTS, AND AGAIN BEFORE IT WARMS. BuildSmartPicker disposed
# the picker it replaces (a wait of up to 3 s on a native call), then constructed the next one (a parse of
# cache.bin, ~94 MB at the full catalog) and warmed it (a session load and an embed), and learnt only at the
# publish that a later rebuild had moved the generation on: two rebuilds within a build's construction
# window ran two parses and two sessions for a few seconds (RA-118), and F143's "the peak stays at one
# session" held for a published picker only (RA-119). The interleaving cannot be forced by a self-test, so
# the SHAPE is asserted the way F144's is: sliced to the method's own body, comment-stripped, the ORDER
# dispose -> check -> construct -> check -> warm, and the absence of the old shape (a construction followed
# straight by the warm). The absence assertion comes FIRST: the script stops at its first failure, and the
# mutation that deletes the second check must be reported by the line that names it.
$smartBuildBodyBurn = Get-MethodBody $fortunesModuleCode `
    'private void BuildSmartPicker(int generation, SmartFortunes old, List<FortuneEntry> pool)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($smartBuildBodyBurn.Length -gt 0) 'BuildSmartPicker could be sliced out for its supersession checks'
Assert-True (
    $smartBuildBodyBurn -cnotmatch 'new SmartFortunes\(\);\s*built\.Warm\(pool\);'
) 'never goes straight from construction to warm: a smart build asks whether it was superseded in between'
Assert-True (
    $smartBuildBodyBurn -cmatch ('old\.Dispose\(\);[\s\S]*?if \(SmartBuildSuperseded\(generation\)\) return;[\s\S]*?' +
        'built = new SmartFortunes\(\);[\s\S]*?if \(SmartBuildSuperseded\(generation\)\)[\s\S]*?' +
        'built\.Dispose\(\);[\s\S]*?return;[\s\S]*?built\.Warm\(pool\);')
) 're-checks its generation after the dispose it waited on and again before it warms: a superseded smart build constructs and warms nothing it cannot publish'

# EVERY Process.Start IN THE FORTUNES MODULE IS DISPOSED. With UseShellExecute the call returns a Process
# whose handle waits for the finalizer unless it is disposed (null when the shell hands back none, which a
# using accepts), and the folder button leaked one per click while every other Start site in the tree wraps
# its result (RA-123). Nothing pinned the shape, so it could come back with the next shell-execute call.
# Counted over comment-stripped source, both numbers reported, and the total asserted non-zero first: a
# module with no Start site at all must not read as a pass.
# Two counts over the same text, not one pattern with a lookbehind: `Process.Start(` appears once per site
# whatever qualifies it, while a lookbehind anchored on the optional `System.Diagnostics.` prefix matched the
# leaf of a WRAPPED site as well and reported the one disposed call as bare (caught here on the first run).
$fortunesStartSites = 0
$fortunesStartDisposed = 0
foreach ($fortunesFile in $fortunesEngineFiles) {
    $fortunesCode = Remove-LineComments (Get-Content -LiteralPath $fortunesFile.FullName -Raw)
    $fortunesStartSites += ([regex]::Matches($fortunesCode, 'Process\.Start\(')).Count
    $fortunesStartDisposed += ([regex]::Matches($fortunesCode, 'using \((?:System\.Diagnostics\.)?Process\.Start\(')).Count
}
Assert-True ($fortunesStartSites -gt 0) (
    "the Fortunes module's Process.Start sites are countable (found $fortunesStartSites)")
Assert-True (($fortunesStartSites - $fortunesStartDisposed) -eq 0) (
    "every Process.Start in the Fortunes module disposes what it returns (sites $fortunesStartSites, " +
    "disposed $fortunesStartDisposed)")
# ---- lane burn/agentflow ----
# (invariants added by lane burn/agentflow go directly below this line)

# THE TICK HANDS ITS SWEEP PASS THE LIVE SWITCH AND THE LIVE BUDGET (RA-021, RA-047). The per-prompt
# decision moved out of OnTick's lambda into AgentFlowModule.SweepPass so the module self-test can drive
# it, and that test supplies its own PressBudget and its own Func<bool>; what it cannot see is what the
# TICK passes. A constant in place of StillArmed reopens the F026 window (a press landing after the tray
# switch moved) with every runtime assertion green, and a fresh budget in place of _pressBudget resets the
# repeat guard every tick. Method-scoped, comment-stripped, the ARGUMENTS asserted rather than the call.
$agentFlowModuleCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\AgentFlow\AgentFlowModule.cs') -Raw)
$agentFlowTickBody = Get-MethodBody $agentFlowModuleCode 'private void OnTick(object sender, EventArgs args)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($agentFlowTickBody.Length -gt 0) 'AgentFlow OnTick exists and could be sliced out for inspection'
Assert-True ($agentFlowTickBody -cmatch 'new SweepPass\(cdpPort, mayPress, allProjects, similar, _pressBudget, StillArmed\)') (
    'the tick hands its sweep pass the live StillArmed switch and the live press budget, not a constant (RA-021)')
Assert-True ($agentFlowTickBody -cmatch 'CdpApprover\.Sweep\(cdpPort, pass\.Handle, ') (
    'the tick sweeps through the pass, so the sweep can tell a press from a refusal (RA-047)')

# THE MODE HAS ONE WRITER (R-005). `_pressArmed` mirrors the mode for the worker, which must not read the
# host's unsynchronised settings dictionary, and it was kept by hand at every site that wrote the mode: the
# pane's Save, both tray rows, each remembering to set the flag a few lines after `_settings.Set(SettingMode,
# ...)`. A fourth writer that forgot reopens the F026 window (a press landing after the switch moved) with
# every runtime assertion green, since the self-test pins only the writers that exist. So the write and the
# mirror live in one method, SetMode, and this asserts that no BARE `_settings.Set(SettingMode,` exists
# outside it: the lookbehind excludes the self-test's `module._settings.Set(SettingMode, ...)`, which reaches
# through an instance and is the test seeding state, not a writer.
$agentFlowBareModeWrites = ([regex]::Matches($agentFlowModuleCode, '(?<![\w.])_settings\.Set\(SettingMode,')).Count
$agentFlowSetModeBody = Get-MethodBody $agentFlowModuleCode 'private void SetMode(string mode)' `
    @("`n        private ", "`n        internal ", "`n        public ")
Assert-True ($agentFlowSetModeBody.Length -gt 0 -and $agentFlowBareModeWrites -eq 1 -and
    $agentFlowSetModeBody -cmatch '(?<![\w.])_settings\.Set\(SettingMode, mode\)') (
    "the mode is written in ONE place, SetMode (found $agentFlowBareModeWrites bare writes of SettingMode) (R-005)")
Assert-True ($agentFlowSetModeBody -cmatch '_pressArmed = AgentMode\.Presses\(mode\)') (
    'SetMode mirrors the mode into the press flag, so no writer can forget it (R-005)')
Assert-True (([regex]::Matches($agentFlowModuleCode, '(?<!void )(?<![\w.])SetMode\(')).Count -ge 3) (
    'the pane save and both tray rows write the mode through SetMode (R-005)')

# 'CHECK NOW' RENDERS THE SETUP LINE ON THE CALLING THREAD (RA-032). SetupStatusLine starts BeginSetupProbe
# when the cache is cold, and BeginSetupProbe reads ArgvPath from the host's unsynchronised settings
# dictionary; called inside CheckNowAsync's Task.Run that read ran on a pool thread beside the UI thread's
# Apply, and a throw there wedged the probe's single-flight gate for the rest of the session. Enable and
# Disable already snapshot ArgvPath before their Task.Run; this asserts Check now takes its setup line the
# same way, by ORDER: the call sits before the Task.Run in the method body.
$agentFlowCheckNowBody = Get-MethodBody $agentFlowModuleCode 'private Task<string> CheckNowAsync()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$agentFlowCheckNowSetupAt = $agentFlowCheckNowBody.IndexOf('string setup = SetupStatusLine();')
$agentFlowCheckNowTaskAt = $agentFlowCheckNowBody.IndexOf('Task.Run(')
Assert-True ($agentFlowCheckNowBody.Length -gt 0 -and $agentFlowCheckNowSetupAt -ge 0 -and
    $agentFlowCheckNowTaskAt -ge 0 -and $agentFlowCheckNowSetupAt -lt $agentFlowCheckNowTaskAt) (
    "'Check now' renders the setup line on the calling thread, before its Task.Run (RA-032)")

# DISABLE RE-INSPECTS AFTER ITS WRITE (RA-031). SetupStatusLine inspects only when the cache is null and
# the tick fills the cache only in a scanning mode, so in Off mode -- the default for a new install, and the
# mode someone is in when they press "Disable (undo changes)" -- the pane's Status row and "Check now" went
# on reporting the state from BEFORE the write for the rest of the session: "Waiting for a VS Code restart"
# after the port had been removed and VS Code restarted. EnableCdpAsync has re-inspected since 1.4.7. The
# runtime suites cannot reach this: Disable refuses while VS Code is running and writes a real argv.json, so
# whether it runs at all depends on the machine. Asserted as ORDER, method-scoped: the write comes first,
# then a second Inspect, and ITS report is what the cache keeps.
$agentFlowDisableBody = Get-MethodBody $agentFlowModuleCode 'private Task<string> DisableCdpAsync()' `
    @("`n        private ", "`n        internal ", "`n        public ")
$agentFlowDisableWriteAt = $agentFlowDisableBody.IndexOf('TryWriteAllText(')
$agentFlowDisableReinspectAt = $agentFlowDisableBody.IndexOf('SetupReport after = VsCodeSetup.Inspect(')
Assert-True ($agentFlowDisableBody.Length -gt 0 -and $agentFlowDisableWriteAt -ge 0 -and
    $agentFlowDisableReinspectAt -gt $agentFlowDisableWriteAt) (
    'Disable re-inspects argv.json AFTER it writes it, so the cache is not the state from before (RA-031)')
Assert-True ($agentFlowDisableBody -cmatch '_setupCache = after;') (
    'Disable caches the report it took AFTER the write, which is what the pane renders (RA-031)')

# StripLineComments IS NOT PUBLIC (R-010). F061 moved every offset-based edit onto BlankLineComments and left
# this with no production caller; public on a public static class reads as a supported entry point and invites
# the next offset edit to use it, which is the F061 class of defect. It stays compilable because the
# self-test inspects with it and two mutate-agentflow.py mutants restore it as the shape they break.
$vsCodeSetupCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\AgentFlow\VsCodeSetup.cs') -Raw)
Assert-True ($vsCodeSetupCode -cmatch 'internal static string StripLineComments\(' -and
    $vsCodeSetupCode -cnotmatch 'public static string StripLineComments\(') (
    'VsCodeSetup.StripLineComments is internal, not public API inviting the next offset-based edit (R-010)')

# NO STACKED <summary> BLOCK IN AgentFlowPane.cs (RA-042). The compiler merges consecutive summaries onto
# the member that follows, so a block left behind when its member moved reads as documentation of whatever
# is next: here, a paragraph stating the pre-2026-09-22 installed-only rule PetChoices reversed sat on the
# _initialising field, and a maintainer following it would have reintroduced the v1.2.4 defect. F027's
# census covered AgentFlowModule.cs only, which is why this file kept one. Checked on the raw text, because
# Remove-LineComments does not strip /// lines and this is about their ADJACENCY.
$agentFlowPaneRaw = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\AgentFlow\AgentFlowPane.cs') -Raw
$agentFlowStackedSummaries = ([regex]::Matches(
    $agentFlowPaneRaw, '(?m)^\s*///\s*</summary>\s*\r?\n\s*///\s*<summary>')).Count
Assert-True ($agentFlowPaneRaw.Length -gt 0 -and
    ([regex]::Matches($agentFlowPaneRaw, '(?m)^\s*///\s*<summary>')).Count -gt 5) (
    'AgentFlowPane.cs was read and carries documentation to judge')
Assert-True ($agentFlowStackedSummaries -eq 0) (
    "no stacked summary in AgentFlowPane.cs: a block whose member moved documents whatever follows it " +
    "(found $agentFlowStackedSummaries) (RA-042)")

# THE DETECTOR'S RECALL FIGURE AGREES WITH THE RECORD (N-records-02). BlockedDetector's class doc quotes the
# measured recall, docs/BLOCKED.md settles it, and they disagreed for a day: the comment said 93% (28/30),
# the harness's predictor figure, while the shipped semantics give 83% (25/30) because an argument-less call
# is Undecidable here and the detector never raises on it (F018). Parsed as NUMBERS from both sides and
# compared, so neither can be edited alone; the detector must also not carry the retired pair.
$blockedDetectorCode = Get-Content -LiteralPath (
    Join-Path $repoRoot 'modules\AgentFlow\BlockedDetector.cs') -Raw
$blockedDocText = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\BLOCKED.md') -Raw
$detectorRecall = [regex]::Match($blockedDetectorCode, 'is (\d+)% \((\d+)/(\d+)\) under the semantics')
$recordRecall = [regex]::Match($blockedDocText, 'settled at \*\*(\d+)% \((\d+)/(\d+)\)\*\*')
Assert-True ($detectorRecall.Success -and $recordRecall.Success) (
    'the detector and docs/BLOCKED.md both state a recall figure to compare (detector ' +
    $detectorRecall.Success + ', record ' + $recordRecall.Success + ')')
Assert-True (
    $detectorRecall.Groups[1].Value -ceq $recordRecall.Groups[1].Value -and
    $detectorRecall.Groups[2].Value -ceq $recordRecall.Groups[2].Value -and
    $detectorRecall.Groups[3].Value -ceq $recordRecall.Groups[3].Value) (
    "BlockedDetector's recall figure matches the one docs/BLOCKED.md settles (detector " +
    $detectorRecall.Groups[1].Value + '% ' + $detectorRecall.Groups[2].Value + '/' +
    $detectorRecall.Groups[3].Value + ', record ' + $recordRecall.Groups[1].Value + '% ' +
    $recordRecall.Groups[2].Value + '/' + $recordRecall.Groups[3].Value + ') (N-records-02)')



# ---- lane fix/deadcode ----
# (invariants added by lane fix/deadcode go directly below this line)

# The four reserved animation names exist as ONE array (PetGraph.ReservedEntryPointNames, read by the emitter,
# the graph and PetStudio since F432) and as the loader's switch in src/dotNet/Xml.cs. The converter's csproj
# asserts the loader still binds each of the four names it knows; nothing compared the ARRAY with the loader,
# so an element removed from it, or a fifth entry point added to the loader, built clean. Both sets are parsed
# from comment-stripped source and compared as sorted lists.
$petGraphCode = Remove-LineComments (Get-Content -LiteralPath (
    Join-Path $repoRoot 'tools\ShimejiConvert.Engine\PetGraph.cs') -Raw)
$xmlLoaderCode = Remove-LineComments (Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Xml.cs') -Raw)
$reservedArray = [regex]::Match($petGraphCode, 'ReservedEntryPointNames\s*=\s*\{([^}]*)\}')
$reservedNames = @([regex]::Matches($reservedArray.Groups[1].Value, '"([a-z]+)"') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object)
$loaderNames = @([regex]::Matches($xmlLoaderCode, 'case\s+"([a-z]+)":\s*animations\.Animation[A-Za-z]+\s*=\s*node\.Id') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object)
Assert-True ($reservedArray.Success -and $reservedNames.Count -gt 0 -and $loaderNames.Count -gt 0 -and
    (($reservedNames -join ',') -ceq ($loaderNames -join ','))) (
    'PetGraph.ReservedEntryPointNames names exactly the animation names Xml.cs binds as entry points (array ' +
    ($reservedNames -join ',') + '; loader ' + ($loaderNames -join ',') + ')')



# ---- lane fix/scripts ----
# (invariants added by lane fix/scripts go directly below this line)

# NO RUNNER-SUPPLIED EXPRESSION IS PASTED INTO A release.yml RUN BODY. The workflow's own rule, stated
# at its tag step ("The three ${{ }} values arrive through env, never interpolated into the script
# body"), was applied to the dispatch input and not to `vars.SIGN_TIMESTAMP_URL`, which sat between
# single quotes in the two build steps' run bodies -- the steps that run with the imported signing
# certificate in the store (F002). GitHub expands `${{ }}` before pwsh parses the text, so a value
# that closes the literal runs as PowerShell, and validating it afterwards is the wrong order.
#
# Asserted per STEP rather than per file, because `inputs.tag` is legitimately interpolated into the
# `concurrency:` group above the steps: each slice from one `- name:` to the next is cut at its `run:`
# line and only the part AFTER `run:` is judged. That is meaningful because every step's `env:` block
# sits above its `run:`. Comment lines are dropped first so the prose explaining the rule cannot
# trip it. The first assertion is the positive control: the file still has run bodies to judge and
# still routes the URL through env at all, so deleting the mapping cannot pass as "nothing pasted".
$releaseStepsText = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml') -Raw
$releaseRunBodies = @()
foreach ($releaseStep in @([regex]::Split($releaseStepsText, '(?m)^      - name:') | Select-Object -Skip 1)) {
    $runKeyAt = [regex]::Match($releaseStep, '(?m)^        run:')
    if ($runKeyAt.Success) {
        $releaseRunBodies += ($releaseStep.Substring($runKeyAt.Index) -replace '(?m)^[ \t]*#.*$', '')
    }
}
$signTimestampEnvMappings = [regex]::Matches(
    $releaseStepsText, '(?m)^          SIGN_TIMESTAMP_URL: \$\{\{ vars\.SIGN_TIMESTAMP_URL \}\}[ \t]*$').Count
Assert-True ($releaseRunBodies.Count -ge 5 -and $signTimestampEnvMappings -ge 2) (
    "release.yml has countable run bodies (found $($releaseRunBodies.Count)) and maps " +
    "SIGN_TIMESTAMP_URL through env in both build steps (found $signTimestampEnvMappings)")
Assert-True (@($releaseRunBodies | Where-Object { $_ -cmatch '\$\{\{\s*(vars|secrets|inputs)\.' }).Count -eq 0) (
    'no release.yml run body interpolates a vars./secrets./inputs. expression; they arrive through env')

# THE GUI SMOKES' PROCESS SWEEPS ARE SCOPED TO THIS CHECKOUT. tests\tray-menu-smoke.ps1 and
# tests\debug-menu-smoke.ps1 stop stray DesktopAICompanion instances before and after a run, so a stale
# one cannot hold the exe or be graded as the build under test, and the filter was
# `$_.Path -like '*\build\*'`: every OTHER worktree's build output on this box matches that as well
# (D:\...\.dac-worktrees\<lane>\build\...), so a coordinator smoke run killed a lane's self-test or
# mutation-harness exe mid-run and scored it a spurious FIRED or a missing marker (N-scripts-01, found
# 2026-09-30). The sweeps now compare against the script's own `$buildRoot = Join-Path $repo 'build\'`.
#
# Asserted on the AST rather than the text, the way the signing guard above is: a comment that quotes
# the old literal can neither satisfy nor trip it. Positive control first -- the definition exists and
# every Where-Object filter that reads `.Path` calls StartsWith($buildRoot, OrdinalIgnoreCase) -- then the
# negative: the old wildcard appears as a string constant nowhere in the script.
foreach ($sweepScript in @('tests\tray-menu-smoke.ps1', 'tests\debug-menu-smoke.ps1')) {
    $sweepTokens = $null
    $sweepErrors = $null
    $sweepAst = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot $sweepScript), [ref]$sweepTokens, [ref]$sweepErrors)
    $sweepFilters = @($sweepAst.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq 'Where-Object' -and $node.Extent.Text -match '\$_\.Path' }, $true) |
        ForEach-Object { $_.Extent.Text })
    $sweepRootDefinitions = @($sweepAst.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$buildRoot' -and
            $node.Right.Extent.Text -like "*Join-Path `$repo 'build\'*" }, $true)).Count
    $sweepScopedFilter = [regex]::Escape('.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)')
    $sweepUnscoped = @($sweepFilters | Where-Object { $_ -notmatch $sweepScopedFilter }).Count
    $sweepOldLiterals = @($sweepAst.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value -eq '*\build\*' }, $true)).Count
    # The distinctive words come FIRST in both labels: PowerShell wraps a thrown message across lines
    # and mutate-hardening-guards.py matches the expected fragment against single lines.
    Assert-True ($sweepErrors.Count -eq 0 -and $sweepRootDefinitions -eq 1 -and $sweepFilters.Count -ge 2 -and $sweepUnscoped -eq 0) (
        "sweeps scoped to this checkout: $sweepScript defines `$buildRoot from `$repo and its " +
        "$($sweepFilters.Count) process sweep(s) all compare against it ($sweepUnscoped do not)")
    Assert-True ($sweepOldLiterals -eq 0) (
        "no other checkout's build output: $sweepScript no longer carries the '*\build\*' wildcard in its sweeps")
}

# LAST, and deliberately: this is the one assertion a BRANCH is expected to fail. Adding a source
# invariant changes the count here, while SMOKETEST.md is updated at the merge -- so any branch that
# adds one carries this failure until then. The self-test aborts at its first failure, so whatever
# stands here masks every assertion after it: mutate-hardening-guards.py scored a real case as
# SURVIVED when the self-test-count block sat below this one and was never reached. Nothing that
# needs to be reachable on a branch may be placed after this point.
$documentedInvariants = [regex]::Match($smokeSource, '(\d+) source invariants')
Assert-True ($documentedInvariants.Success) 'SMOKETEST.md states a source-invariant count'
Assert-True ([int] $documentedInvariants.Groups[1].Value -eq $assertSiteCount) (
    "SMOKETEST.md's source-invariant count matches this file" +
    $(if ([int] $documentedInvariants.Groups[1].Value -ne $assertSiteCount) {
        " -- it says $($documentedInvariants.Groups[1].Value), there are $assertSiteCount" } else { '' }))

Write-Host 'PASS: runtime hardening source invariants.'
# The passing run's half of the PSModulePath restore; the trap at the top is the failing run's (RA-358).
$env:PSModulePath = $originalModulePath

