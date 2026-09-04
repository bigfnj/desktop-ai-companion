[CmdletBinding()]
param(
    # Accepted for CI compatibility but unused: this script now performs only source-text invariant
    # checks (it reads .cs files, no assembly load). The reflection/runtime half moved in-process to
    # the app's --hardening-selftest flag (no PowerShell hosts a net10 assembly).
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool] $Condition, [string] $Name)
    if (-not $Condition) { throw "$Name failed." }
    Write-Host "PASS: $Name"
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
$redirectOffenders = @()
foreach ($file in Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ($text -notmatch 'RedirectStandardOutput\s*=\s*true') { continue }
    if ($text -notmatch 'StandardOutputEncoding\s*=') {
        $redirectOffenders += $file.FullName.Substring($repoRoot.Length + 1)
    }
}
Assert-True ($redirectOffenders.Count -eq 0) (
    'every RedirectStandardOutput pins StandardOutputEncoding' +
    $(if ($redirectOffenders.Count -gt 0) { " (offenders: $($redirectOffenders -join ', '))" } else { '' }))

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

# Module audio must NEVER enter AudioOutput._cache. That dictionary is keyed by byte[] REFERENCE identity and
# is cleared only in Dispose, so caching synthesized speech would retain every line the pet ever spoke -- plus
# a mixer-format buffer roughly 7x larger than the input. The engine path caches on purpose (a pet has a fixed
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
    # A third list exists and is rendered from a STALENESS diff, not from the id diff.
    $petsPane -match '(?s)private List<CatalogCompanion> DiffStale\(\)[\s\S]{0,900}?CompanionProvenance\.IsStale\(FreshnessOf\(pet\)\)' -and
    # ...and it is actually wired to the button that checks the catalog, or nothing ever populates it.
    $petsPane -match '(?s)CheckButton_Click[\s\S]{0,1200}?DiffStale\(\)' -and
    $petsPane -match '(?s)CheckButton_Click[\s\S]{0,1400}?RenderUpdates\(' -and
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
$optionsWindowSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\OptionsWindow.cs') -Raw
$buildStart  = $optionsWindowSource.IndexOf('public FrameworkElement Build()')
$loadCall    = $optionsWindowSource.IndexOf('_pane.Load()', $buildStart)
$schemaRead  = $optionsWindowSource.IndexOf('_pane.Schema', $buildStart)
Assert-True (
    $buildStart -gt 0 -and $loadCall -gt $buildStart -and $schemaRead -gt $loadCall
) 'a pane Load() runs before its Schema is read, so a dropdown can offer live options'

# ...and that the two panes actually USE that window, rather than merely declaring a field. A dropdown whose
# Options are only set at construction is the frozen case above.
$optionsShellSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\OptionsShell.cs') -Raw
Assert-True (
    $optionsShellSource -match '(?s)Load = delegate[\s\S]{0,4000}?BuildSpeakerOptions\([\s\S]{0,400}?speakerField\.Options ='
) 'the speaker dropdown refreshes its options inside Load, not only at construction'

# The host-level fullscreen answer must come from the scan the PETS already run, not a second detector.
# Two implementations of "is a game running" would drift, and only one of them has --fullscreen-selftest
# behind it. FormCompanion hands the whole blocked-monitor array up before narrowing to its own monitor, because a
# module asking the question means ANY monitor -- an alt-tabbed game still owns its VRAM.
Assert-True (
    $formPetSource -match '(?s)FullscreenScan\.BlockedMonitors\([\s\S]{0,600}?NoteFullscreenScan\(blocked\)' -and
    $startUpSource -match '(?s)internal void NoteFullscreenScan\([\s\S]{0,900}?RaiseFullscreenChanged\('
) 'the module-facing fullscreen state comes from the pets own scan, and raises on change'

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

# Strip line comments so an invariant cannot be satisfied -- or an ordering check inverted -- by prose. This
# repo has been bitten four times by a source check that a comment alone was enough to pass.
function Remove-LineComments {
    param([string] $Text)
    return (($Text -split "`n") | ForEach-Object { $_ -replace '//.*$', '' }) -join "`n"
}
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
Assert-True (
    $checkFsStart -gt 0 -and
    $checkFsBody -notmatch 'else if \(!_fullscreenHidden\)' -and
    $checkFsBody -match 'if \(Visible\) Visible = false;'
) 'hiding for a fullscreen app is enforced every scan, not latched behind a flag'

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
$petsPaneSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Portable\Wpf\CompanionsPaneControl.cs') -Raw
$fetchIndex = $petsPaneSource.IndexOf('SecureDownload.WriteAllBytesAtomic(Path.Combine(directory, "animations.xml"), bytes);')
$afterWrite = if ($fetchIndex -ge 0) { $petsPaneSource.Substring($fetchIndex, [Math]::Min(1200, $petsPaneSource.Length - $fetchIndex)) } else { '' }
Assert-True (
    $fetchIndex -ge 0 -and
    $afterWrite -match 'CompanionCatalog\.Forget\(' -and
    $afterWrite -match 'ForgetStats\('
) 'replacing a pet file drops its cached display name and stats, so the tray cannot keep the old name'

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

# The seed-without-checking branch is the bug AppUpdateCheck documents: it stamped "I looked" on a fresh
# install without looking, so a new install stayed blind for a whole interval. It must not come back.
Assert-True ($startUpSource -notmatch 'seed the month WITHOUT checking') (
    'a fresh install checks rather than stamping a check it never performed'
)

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
    Assert-True ($paneSource -match 'RemoteCatalogClient\.InvalidateShared\(\);') (
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
    $darkest = 255
    for ($sx = $sampleX0; $sx -lt $sampleX1; $sx += 3) {
        for ($sy = $sampleY0; $sy -lt $sampleY1; $sy += 3) {
            $pixel = $dialogBmp.GetPixel($sx, $sy)
            foreach ($channel in @($pixel.R, $pixel.G, $pixel.B)) {
                if ($channel -lt $darkest) { $darkest = $channel }
            }
        }
    }
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
foreach ($pair in @(
        @{ Name = 'build.ps1'; Text = $buildScript },
        @{ Name = 'installer\build-installer.ps1'; Text = $installerScript })) {
    Assert-True (
        $pair.Text -match '\$SigningCertThumbprint' -and
        $pair.Text -match 'IsNullOrWhiteSpace\(\$SigningCertThumbprint\)'
    ) "$($pair.Name) only signs when a thumbprint is supplied"
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
Assert-True (
    $releaseWorkflow -match 'Remove the signing certificate from the runner' -and
    $releaseWorkflow -match 'if: always\(\)'
) 'the signing certificate is scrubbed from the runner even when a build step fails'

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
foreach ($wxs in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.wxs' -File)) {
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
$ciScriptNames = @()
foreach ($wf in @('build.yml', 'release.yml')) {
    $wfPath = Join-Path $repoRoot ".github\workflows\$wf"
    if (-not (Test-Path -LiteralPath $wfPath)) { continue }
    $wfText = Get-Content -LiteralPath $wfPath -Raw
    # Derived from the workflows, not hardcoded, so a newly CI-invoked script is covered the day it lands.
    foreach ($m in [regex]::Matches($wfText, '[A-Za-z0-9_\\/.-]+\.ps1')) {
        $ciScriptNames += ($m.Value -replace '^\.\\', '' -replace '/', '\')
    }
}
$ciScripts = @($ciScriptNames | Sort-Object -Unique | ForEach-Object {
    $p = Join-Path $repoRoot $_
    if (Test-Path -LiteralPath $p -PathType Leaf) { $p }
})
Assert-True ($ciScripts.Count -ge 5) (
    "the workflows name at least 5 PowerShell scripts to check for shell parity (found $($ciScripts.Count), " +
    "running under $parityHost)")

# Removed in PowerShell 7: present in 5.1, so they pass a local run and break CI.
$goneInPwsh = @('Get-WmiObject', 'Invoke-WmiMethod', 'New-WebServiceProxy', 'Add-PSSnapin', 'Get-EventLog')
# PS7-only operators. Under 5.1 these are parse errors, caught by the parse assertion; under pwsh they
# tokenize, so they are caught here. Between the two, the gate catches them whichever host it runs on.
$pwshOnlyTokens = @('AndAnd', 'OrOr', 'QuestionQuestion', 'QuestionDot', 'QuestionLBracket')
# Set-Content defaults to ANSI under 5.1 and UTF-8-no-BOM under pwsh, and Out-File differs too, so the same
# script emits different BYTES on each. That is not academic: it is the shape of the CRLF SHA256SUMS bug.
$encodingSensitive = @('Set-Content', 'Add-Content', 'Out-File')

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

    $unencoded = @($commands | Where-Object {
        $name = $_.GetCommandName()
        if ($encodingSensitive -notcontains $name) { return $false }
        $hasEncoding = @($_.CommandElements | Where-Object {
            $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -like 'Enc*'
        }).Count -gt 0
        -not $hasEncoding
    })
    Assert-True ($unencoded.Count -eq 0) (
        "$rel pins -Encoding on every file write, so both shells emit the same bytes" +
        $(if ($unencoded.Count) { " -- $($unencoded[0].GetCommandName()) at line $($unencoded[0].Extent.StartLineNumber)" } else { '' }))
}

Write-Host 'PASS: runtime hardening source invariants.'
