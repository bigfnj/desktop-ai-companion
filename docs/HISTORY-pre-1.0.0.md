# Development history before v1.0.0

This project was rebuilt as **Desktop AI Companion** at v1.0.0 in a new repository with a
single initial commit. That discarded the git history, so this file preserves the part worth
keeping: **this maintainer's own commits**, newest first.

The prior repository was a fork. Its upstream history and its other contributors' commits are
deliberately not reproduced here -- that record belonged to the fork, not to this project.
Attribution for the upstream work is carried by `LICENSE` and `THIRD_PARTY_NOTICES.md`, which
are tracked files and unaffected by any of this.

Commit hashes refer to a repository that no longer exists. They are kept because commit
messages reference each other, and a dangling reference is still more useful than none.

| | |
|---|---|
| Commits | 761 |
| Range | 2026-06-16 to 2026-09-03 |

---

### 2026-09-03  `a29b08bd7`

**docs: remove a work email from the rename plan, and gate the new repo on a scan**

```
I put it there. Writing up the finding that a work address is published in this
repo's old tags, I spelled the address out in full, twice, in a tracked file, and
pushed it to the public repo -- while documenting the rule against exactly that.
Both occurrences now describe it as "a work email address" without naming it.

Scanned the whole tracked tree afterwards rather than assuming: the two hits were
the only ones in the repository, and both were in this file, added today. No
pre-existing file carries the employer name, a work address, or the maintainer's
legal name. The remaining addresses in tracked content are the personal git
identity, git@github.com in a clone URL, and third-party fortune-pack text.

The commit message of ad395fd still contains the literal address and is not being
rewritten. That repo already published the address through the v1.0.0 to v1.0.6
tags before I wrote anything, so the commit adds no new exposure, and Phase 6
deletes the repo and every object in it. A force-push would leave the old objects
reachable by SHA for a while and buy nothing.

Phase 4 now requires scanning the initial commit -- every tracked file AND the
commit message -- for the employer name, any work address, and the legal name,
before the new repo is pushed. Expected result zero, with the personal address as
the only author identity. That check exists because this mistake happened, which
is recorded in the plan next to the rule so the reason survives.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `ad395fd48`

**docs: scope the 1.0.0 history preservation to this maintainer's work only**

```
Per the maintainer: keep the record of their own effort, not the fork's history,
its contributors, or the original source. Phase 0 now preserves 759 commits
(peshinator@gmail.com, reachable from HEAD, 2026-06-16 onward) instead of all
1,162, and the upstream contributor export is dropped.

Nothing is owed by dropping it. Attribution is discharged by LICENSE and
THIRD_PARTY_NOTICES.md, which are tracked files carried into the new repo, not by
the git graph -- so the obligation survives the history discard while the record
of other people's commits does not. That distinction is now stated in the plan
rather than left implied.

Two findings while scoping it, both arguing for the decisions already taken:

[work identity redacted] authored 10 commits on 2026-06-24 and is CURRENTLY
PUBLISHED in bigfnj/desktopPet, reachable through the old v1.0.0 to v1.0.6 tags.
It is not reachable from HEAD, so a HEAD-scoped history export excludes it
automatically -- but the plan now says to assert that rather than assume it.
Deleting the repo is the only thing that actually removes a work email from a
public history; rewriting in place means force-pushing rewritten tags and still
leaves the old objects reachable. Standing rule is no work material in public
repos, so this counts as a concrete win of the fresh-repo route, not a side
effect.

Tags v1.0.0 through v1.0.6 ALREADY EXIST in this repo. A rename plus a re-tag
could therefore never ship a clean 1.0.0; a new repo is the only route.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `4c8a55c6d`

**docs: the v1.0.0 rename plan, and a full account of what it destroys**

```
Three decisions are taken: repo becomes bigfnj/desktop-ai-companion, the rename
goes all the way through the plugin ABI and settings keys, and the new repo
starts from a single commit with the old one deleted afterwards.

The reason to do it now rather than later is the only real argument for it:
settings keys, the catalog's "pets" key, on-disk directory names and the ABI are
compatibility surfaces that normally need a migration or a breaking major bump.
A rebase to 1.0.0 with a mandatory clean install makes all of them free, exactly
once, and the window shuts when 1.0.0 ships.

Six things would break the migration silently, and the first is the one that
would look like the module system collapsing: every catalog module declares
MinHostVersion between 1.4.0 and 1.9.9, so a host at 1.0.0 is refused by all six.
Also: the MSI needs a NEW UpgradeCode because 1.0.0 is a downgrade of 1.9.16;
catalog.json cannot verify its own URLs until after the push; attribution rides
on LICENSE and THIRD_PARTY_NOTICES rather than the fork link; and neither the
animations.xml schema nor the built-in "eSheep" id may be renamed.

Written down in full because two decisions are irreversible and the interesting
losses are the ones nobody thinks of. Measured, not estimated: 1,162 commits back
to 2015-12-23, 41 tags, 9 remote branches, issues and PRs up to #89, 3 releases,
and 13 contributors of whom about eleven are third parties to this fork. That
contributor list is the loss worth pausing on, and it is cheap to keep as a file.

On this machine the clean install also takes %APPDATA%\DesktopPet, which holds
chat-history.json -- the AI Brain conversation record, small, not in the repo and
recreated by nothing -- and settings.json, which is 174 KB because it inlines the
active companion's whole animations.xml and its base64 icon.

Phase 0 now mitigates each of those item by item rather than listing good
intentions: the log, the contributor list and the issue export all become tracked
files, and the two machine-local data roots get copied before anything uninstalls.

Module versions rebase to 1.0.0 with the host, which is safe only because nothing
survives the clean install: the Update button compares catalog against installed,
so a machine holding petstudio 1.7.0 against a catalog offering 1.0.0 would
consider itself ahead forever.

Nothing is executed yet.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `5e3fbf68b`

**chore(catalog): regenerate for the module republish, and fix the report assertion**

```
Publishing six modules regenerated catalog.json; this commits it. Both
modules.json and catalog.json now carry "Companion Studio" and no "Pet Studio",
and the three descriptions that were user-visible in the Modules pane
(petstudio, reminder, blinkingled) say companion.

Five modules had to be republished for a reason worth recording: the host
rename touched src/DesktopPet.ModuleKit's <Product> metadata, which is bundled
into every module zip, so Test-ModulePublishFreshness correctly reported
fortunes, aibrain, reminder, remembrance and blinkingled as behind their source.
It refused to let petstudio publish until they were all current. That gate is
the reason the catalog cannot end up hashing content nobody can download.

Also fixes --petstudio-selftest, which asserted the report contains "Valid pet".
Renaming PetReport's prose to "Valid companion" broke it, which is the check
doing its job rather than echoing a string: it round-trips the bundled companion
through the module's own analyzer and reads the output.

Gate: 145 PASS. Freshness: 12 OK, 0 stale.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `e3a77d73f`

**chore(modules): publish petstudio 1.7.0**

### 2026-09-03  `31d691366`

**chore(modules): publish blinkingled 1.0.4**

### 2026-09-03  `6c3e4b7b4`

**chore(modules): publish reminder 1.8.1**

### 2026-09-03  `911534559`

**chore(modules): publish remembrance 1.1.2**

### 2026-09-03  `d9135e392`

**chore(modules): publish aibrain 1.4.0**

### 2026-09-03  `45705982f`

**chore(modules): publish fortunes 1.2.7**

### 2026-09-03  `77e358498`

**chore(modules): publish petstudio 1.7.0**

### 2026-09-03  `84b802d56`

**feat(petstudio): rename to Companion Studio, 1.6.7 -> 1.7.0**

```
Follows the host rename to Desktop AI Companion. The app had already been
changed to say "Companion Studio" and then reverted, because renaming the app's
references while the Modules list and the catalog still said "Pet Studio" is
worse than not renaming at all. This does the module half, so both agree.

ModuleInfo.Name, the tray item label and the window title all change. The module
ID stays "petstudio": it is the folder name on disk, the catalog key and the
published zip URL, so renaming it would orphan every installed copy in exchange
for a display string.

The module's own user-facing prose moves to "companion" too (49 strings across
nine files), through the same literals-only script used on the host: it skips
anything containing a path or URL and treats "contains a space" as the prose
test, which is what keeps "petstudio" and the ModulePermissions.Pets enum name
out of it.

Verified in the built binary rather than the source: PetStudio.dll carries seven
"Companion Studio" literals, zero "Pet Studio", and still id "petstudio" at
version 1.7.0.

Left alone: the "Pets, Storage" permission string in the manifest. It is
rendered from the ModulePermissions ABI enum, so it moves with the Contracts
rename or not at all.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `5e73760e1`

**feat(naming): rename the product to "Desktop AI Companion" and the UI to companions**

```
The pivot: these are not pets. Converted Shimeji skins overwhelmingly represent
human characters, so "pet" mislabels most of the library, and "Desktop Pet AI
Companion" kept the word anyway. Product is now "Desktop AI Companion" and the
user-facing vocabulary is "companion".

What changed:
- ProductVersion.props DesktopPetProductName, which flows to the MSI product,
  the ARP entry, the Start Menu folder and the install directory.
- The tray label, now the constant ProcessIcon.TrayDisplayName rather than
  "<pet> Desktop Pet". Windows 11 keys its tray entry on the EXECUTABLE and
  caches one label per path, so a per-pet label in that slot named whichever
  companion happened to be the default and misdescribed every other one on
  screen. The pet's own name still reaches the About dialog.
- 77 user-visible strings across the tray menu, the options shell, the
  Companions pane, the options controller and Program's message boxes.

DELIBERATELY NOT RENAMED, because each is a compatibility surface, not a label:
- settings.json keys (activePetId, petSizes, petMonitors, mutedPets,
  autoStartPets, defaultSpeakingPet, petSoundsEnabled, petUpdate*) -- renaming
  silently resets every per-companion size, monitor pin and mute.
- catalog.json's "pets" array, which already-installed clients read.
- CompanionCatalog.BuiltInPetId = "eSheep", persisted as activePetId.
- the DesktopPet.Contracts ABI (ICompanion, ICompanionManager, CompanionTypeInfo, CompanionSpawned...) --
  renaming breaks all six published modules and forces a Contracts major bump.
- the on-disk pets\ directory, the DesktopPet namespace, exe and repo names.
- "Pet Studio": it is a PUBLISHED MODULE's display name. The rename briefly
  produced "Companion Studio" in the app while the Modules list and catalog
  still said "Pet Studio", which is worse than not renaming it. Reverted; doing
  it properly needs a module republish plus a catalog regen.

The rename is NOT applied blind. A sed over these files corrupts "pets" as a
pane routing key, "petSizes" as a settings key and the grimoire URL, so the
script rewrote string literals only, skipped anything containing a path or URL,
and used "contains a space" as the prose test -- every identifier and routing
token here is a single word. One-word labels went through an explicit allowlist.
Every replacement was printed and reviewed; the bullet character and the absence
of a BOM were both verified byte-wise afterwards.

Two things the guards caught, which is the point of having them:
- --wpf-options-selftest asserted panes[2].Title == "Pets". Renaming the pane
  also moves it alphabetically, so this was a real check, not a string echo.
- Autostart would have broken silently. Task Manager's Startup tab shows the
  registry VALUE NAME, so it had to be renamed too, but the old entry does not
  remove itself and points into the old install directory. IsEnabled now reads
  both names and Set removes the legacy one either way, so a user who had
  autostart on does not get a dead startup item and an unticked checkbox.
  mutation: IsEnabled ignores the legacy name -> FIRED
    "a legacy autostart entry still reads as ENABLED after the rename"
  mutation: Set no longer removes it      -> FIRED 3, incl.
    "disabling clears BOTH names"
  baseline unmutated -> SILENT, restore byte-identical.

That mutation run had to be redone. The first harness restored with
`git checkout --`, which is correct only for an unmodified file; the change under
test was uncommitted, so the first restore reverted it and every subsequent
"FIRED" was the self-test failing against reverted code. The harness now
baselines from the working tree and asserts the code under test is present
before it mutates anything.

No migration for the install directory, by decision: it moves to
%LOCALAPPDATA%\Programs\Desktop AI Companion and the 23 downloaded companions in
the old folder stay behind. Uninstall the old product before installing this.

Gate: 145 PASS under pwsh 7.6.5 and Windows PowerShell 5.1.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `d3eab49e7`

**test(ci): guard that every CI-invoked script runs under both pwsh 7 and 5.1**

```
CI runs every step with `shell: pwsh`; a local gate run may be Windows
PowerShell 5.1. A construct exclusive to either one fails in exactly one place:
PS7-only syntax (&& || ?? ?.) breaks locally, and things REMOVED in 7
(Get-WmiObject, Invoke-WmiMethod, New-WebServiceProxy, -Encoding Byte) break in
CI, which is the dangerous direction because CI publishes the release.

Four checks per script, over the scripts the WORKFLOWS name rather than a
hardcoded list, so a newly CI-invoked script is covered the day it lands (9
today): it parses, it uses no PS7-only operator token, it calls nothing removed
in 7, and every Set-Content/Add-Content/Out-File pins -Encoding. That last one
is not academic -- Set-Content defaults to ANSI under 5.1 and UTF-8 no BOM under
pwsh, which is the same shape as the CRLF SHA256SUMS bug fixed in b1b61d6.

Uses PowerShell's own parser, tokens and AST, not regexes, for the same reason
the .wxs guard parses XML: `&&` and `??` appear inside comments and string
literals, and this very file asserts on C# source text containing `??`. A
regex-based version fails on correct code. It also names the host it ran under
on every run, so a single-shell run says which one it was rather than implying
it covered both.

The parse check and the token check overlap deliberately: under 5.1 a `&&` is a
parse error, under pwsh it tokenizes as AndAnd, so the pair catches it on either
host rather than only the one that happens to be running.

Mutation-tested, five mutations:
  &&  added to a CI script  -> FIRED, pwsh: "uses no PowerShell-7-only operator"
                            -> FIRED, 5.1 : "parses under Desktop 5.1 -- not a
                                             valid statement separator in this version"
  Get-WmiObject             -> FIRED "calls nothing removed in PowerShell 7"
  Set-Content, no -Encoding -> FIRED "pins -Encoding on every file write"
  the SAME operators inside a comment and a string
                            -> SILENT, 106/106 pass. The negative case, and the
                               reason this is AST-based.
  workflow script list emptied
                            -> FIRED the count assertion, so it cannot silently
                               degrade to checking nothing

Two harness faults found and fixed while doing that, both classic ways a
mutation harness lies: it saved its "pristine" baseline AFTER an aborted run had
already mutated the file, so every restore restored the mutation (baseline now
comes from git); and pwsh renders a terminating error in ANSI with the throwing
SOURCE LINE echoed, so grepping for "failed." matched `throw "$Name failed."`
inside Assert-True and printed the same meaningless string for all five.

Get-MethodBody's StopAt addition from 2ef2963 is unrelated to this and already
landed; no behaviour change here beyond the new checks.

Verified: full gate 144 PASS under pwsh 7.6.5 and under Windows PowerShell 5.1,
identical assertion lists, differing only in line endings.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `bc06215d5`

**docs: v1.9.16's published SHA256SUMS.txt was corrected in place**

```
The CRLF fix in b1b61d6 lands on master, so it only takes effect from the next
tag. v1.9.16 was already published, so its asset was replaced by hand instead:
line endings only, all four hash values byte-identical to what shipped, and
`sha256sum -c SHA256SUMS.txt` now exits 0 on a fresh `gh release download` with
no preprocessing.

That leaves one trap worth a warning, because it would silently undo the fix:
the v1.9.16 TAG predates b1b61d6 and release.yml's workflow_dispatch checks out
the tag, so re-running the workflow on it regenerates the CRLF file and clobbers
the corrected asset.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `b1b61d6f7`

**fix(release): write SHA256SUMS.txt with LF, and record the v1.9.16 tray fix**

```
Found while verifying v1.9.16's published assets the way a user would. All four
hashes are correct and all four lines FAIL:

  sha256sum: 'DesktopPet-Portable.zip'$'\r': No such file or directory
  DesktopPet-Portable.zip: FAILED open or read

Set-Content on Windows writes CRLF, and GNU coreutils sha256sum -c treats the
trailing carriage return as part of the FILENAME. Exactly the same class of bug
as the nuget/ prefix fixed in v1.9.14, on the same file, with the same shape:
the hashes were never wrong, the instruction in the release notes was
unfollowable. Windows users were unaffected -- Get-FileHash does not care -- so
this survived every release that has ever shipped this file.

Now written through File.WriteAllText with an explicit LF join and a trailing
newline. Verified by running the exact call and reading the bytes back with
cat -A: LF only, ASCII, no BOM.

Takes effect on the NEXT tag. v1.9.16's published SHA256SUMS.txt still has CRLF;
its hashes are correct and verify after `tr -d '\r'`, or with Get-FileHash.

Invariant asserts the WRITE CALL and the absence of the Set-Content form, since
a comment about line endings guards nothing.
  mutation: restore `$lines | Set-Content` -> FIRED
    "SHA256SUMS.txt is written with LF endings, so sha256sum -c can actually
     read the filenames"

Also records the tray fix in BACKLOG and rewrites the handoff START HERE around
it, including the three measurement traps that cost the most time: the classic
ToolbarWindow32 tray probe reports zero icons for EVERY app on Windows 11 (the
tray is a XAML island -- use UI Automation), swapping only DesktopPet.exe into an
install directory changes nothing because the code is in DesktopPet.dll, and gh
resolves this fork to `upstream` unless passed -R bigfnj/desktopPet, which made
it look like no v1.9.x release or CI run existed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `4a7739cf3`

**chore(release): 1.9.16**

```
One fix on top of 1.9.15: the tray icon now appears in the visible notification
area on a fresh install instead of in the Windows 11 hidden-icons flyout, and it
carries the pet's own name rather than the "eSheep Desktop Pet" placeholder.

Still unsigned, and still true in RELEASE-CHECKLIST and release.yml: the signing
scaffolding from 1.9.15 stays inert until a CA-issued certificate exists.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `2ef296312`

**fix(tray): show the pet's tray icon instead of hiding it in the Win11 flyout**

```
Reported as "on a fresh install, the pet is there, there is NO tray icon".

The icon was never missing. Windows 11 files every tray icon under
HKCU\Control Panel\NotifyIconSettings and hides any entry whose IsPromoted
value is ABSENT, so a first launch put a fully working icon behind the chevron
as one of thirty -- which reads to a user as no icon at all. Confirmed by UI
Automation over the shell (the tray is a XAML island on 11, so the classic
ToolbarWindow32 probe reports zero icons for every app and is not evidence):
the entry was present in TopLevelWindowForOverflowXamlIsland the whole time.

Ruled out first, so the record is straight: Application.Run does NOT return
early, AppLifetime does not tear the app down, ProcessIcon is not disposed, and
bitmapIcon/SetIcon succeed in both the portable and the installed layout. Each
was measured, not reasoned about. The 316ms files-in-use result from v1.9.15
stands.

Two fixes:

- TrayPromotion promotes the entry once, and ONLY when IsPromoted is absent.
  Windows writes 0 when the user drags the icon back into the flyout, so absent
  means "never chose" and 0 means "hidden on purpose" -- which has to stand, or
  the pet overrules the user on every launch. Self-limiting, so it needs no
  setting of its own. HKCU only, no elevation; the shell honours it live
  (measured: out of the flyout in ~3s, no app or explorer restart).
- SetIcon now assigns ni.Text BEFORE ni.Icon. WinForms only issues the NIM_ADD
  once an icon exists -- Display() sets Visible with a null Icon, which adds
  nothing -- and Windows permanently caches the tooltip from that first ADD. In
  the old order every pet was labelled "eSheep Desktop Pet" forever, which is
  what the user reads when hunting the flyout. The label is corrected
  independently of the promotion decision: unlike visibility, there is no user
  preference to overrule.

Path matching is deliberately exact. This machine holds eight NotifyIconSettings
entries whose executable is named DesktopPet.exe; a filename or suffix match
promotes some other copy's icon. A packaged {KnownFolderGuid} path therefore
does not match, and not promoting is the safe outcome.

Mutation-tested, five mutations, each firing only its own checks:
  ShouldPromote returns true unconditionally  -> FIRED 3
    "IsPromoted=0 is the user hiding it -> leave alone"
    "IsPromoted=1 is already shown -> leave alone"
    "...IsPromoted stays 0, the pet does not overrule the user"
  PathMatches loosened to a filename compare  -> FIRED 6
    incl. "a different copy of the same exe does NOT match"
          "...and that other entry is left untouched"
  ni.Icon assigned before ni.Text             -> FIRED 1
    "SetIcon sets the tray text BEFORE the icon..."  (order, not presence)
  PromoteOnce call deleted                    -> FIRED 1
  PromoteOnce left as a COMMENT only          -> FIRED 1  (same check)

That last one matters: an absence check matching a bare identifier has been
defeated by a comment four times in this repo, so both source invariants run on
comment-stripped text and the ordering one compares positions rather than
asserting that two statements exist.

Get-MethodBody gains an optional StopAt, defaulting to its current single
terminator so every existing caller slices exactly as before; SetIcon is public
and doc-commented, which the old terminator could not bound.

SMOKETEST A4 was "the tray icon is there and its menu opens" -- which this bug
passes. Rewritten to say VISIBLE tray, not the flyout, with A5 for the label and
a pointer from K4, since an install is the one path that creates a brand-new
Windows entry and a brand-new entry is the one Windows hides.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

### 2026-09-03  `ce353b382`

**docs: v1.9.15 released, awaiting a smoke report**

```
Marks the batch released and rewrites START HERE around the one thing that is
outstanding: the live smoke test was NOT walked before the tag, deliberately, so
the published artifact can be tested as a user receives it.

Also records what the next session needs and does not have: a real certificate for
the signing scaffolding, two decisions still open with it (timestamping forfeits the
MSI byte-reproducibility Normalize-MsiDeterminism exists to preserve, and the
publisher name will not match if the cert CN is not bigfnj), and the reminder that a
TESTBUILD product may still be installed side by side.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-03  `aa489adfd`

**chore(release): 1.9.15**

```
Five threads that were held back from a release until all of them were resolved.

The installer stops fighting you: a running pet is closed rather than prompted
about, Repair works instead of being greyed out, the pet launches when setup
finishes, and there is an off-by-default clear-all-settings-and-modules option for
a genuinely fresh start.

The files-in-use prompt turned out to be an APP bug, not an installer one.
Application.Run() ran with no main form, so nothing could end the message loop:
Restart Manager asked the app to close, got nothing, and had already closed the
windows it could reach, taking the tray icon with them and leaving pets on screen
with no way to quit. Measured on the installed layout with 18 module DLLs loaded,
the shipped build was still running 32 seconds later; this one exits in 316 ms.

Updates find you now. Opening Modules or Pets already shows what has a newer
version, from a weekly background check whose result is written down so the pane
renders instantly and offline. Two bugs fixed on the way: the module check was
armed behind a modules-loaded guard and inside their try/catch, and it stamped a
check it never performed on a fresh install, so a new install stayed blind for a
month.

A pet on screen reloads when its skin updates. That previously required a manual
remove and re-add, which did not work either: the type registry served the cached
parse and brought the old skin back, silently.

Also lands code-signing scaffolding that signs nothing. Both entry points are
opt-in on a thumbprint, so this build is byte-identical to one from before it
existed. Releases stay unsigned until a certificate exists.

Gate green. Both soaks pass: resource soak PASS with handles and GDI objects DOWN
and private bytes +14.7 MB against a 64 MB bound; module-window soak PASS, flat
across the last segment with every window collected.

Not yet walked: the live smoke test beyond section B3. This release is being cut
deliberately so it can be smoke-tested from the published artifact as a user would
get it, which is a more faithful test than a local build.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-03  `5100987a5`

**docs: record the in-flight batch, and correct three claims it made false**

```
README: the modules paragraph still said the app checks for module updates once a
month, which is now weekly and, more to the point, no longer something the user has
to go looking for. Adds the two user-visible behaviours that had no documentation at
all -- updates showing up when a pane opens, and a pet on screen reloading when its
skin updates -- plus a paragraph on what the installer now does.

SMOKETEST: a new section K for the installer, because that is the one component
whose failures are invisible to every automated check here, and every row in it is a
bug that actually shipped or nearly did. G4 is rewritten around waiting rather than
pressing, since pressing is no longer how you find out. 66 checks in eleven
sections, and the header counts are corrected (61 invariants, not 33).

BACKLOG: the batch recorded as done, with the two measurement traps that made the
Restart Manager fix look useless twice.

HANDOFF: a new START HERE that says plainly that nine commits are local and
unpushed, what is verified, and the three things left before a tag.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-03  `199fba556`

**build: code-signing scaffolding, inert until a certificate exists**

```
Everything needed to sign, wired end to end and signing nothing. A CA-issued
certificate is coming; when it arrives this becomes two GitHub secrets and no code
change.

Nothing about a normal build changes. Both entry points are opt-in on a thumbprint
and build.yml runs both on every pull request with no certificate, so that property
is load-bearing rather than tidiness. Verified by building the MSI both ways and
confirming dist\ is NotSigned afterwards.

The sibling runas-helper approach does not transplant. It hangs signtool off MSBuild
targets in a .wixproj, and desktopPet has no .wixproj at all: build-installer.ps1
invokes the `wix` dotnet tool directly. The ordering that project expresses with
AfterTargets/BeforeTargets is therefore explicit call sites here.

WHERE THE MSI SIGNATURE GOES IS FORCED, NOT CHOSEN. Between Normalize-MsiDeterminism
and the hash seal, and nowhere else:
  * Not earlier. Normalisation rewrites the whole file (it zeroes the compound-file
    root timestamps via WriteAllBytes) and would invalidate a signature. It also
    REFUSES to run on an already-signed MSI rather than quietly breaking one, so
    signing first is a hard error, not a subtle bug.
  * Not later. The next statement seals the staged file and takes the hash the
    validation copy is compared against and that Publish-DesktopPetAtomicFile
    enforces on the way into dist\. Signing after that changes bytes those checks
    have already committed to.
Between the two, every downstream hash covers the SIGNED bytes and nothing else
needs to know.

The payload is signed in build.ps1 after the set-equality check and before anything
packages it, because that one point covers both consumers: the deterministic ZIP
streams those same files, and the installer stages them for the cabinet. Signing in
either of those places instead would leave the other unsigned.

Two things Invoke-Signtool does that are worth stating. It VERIFIES with /pa after
signing, because signing can succeed while producing a signature that does not
validate (an untrusted chain most obviously) and shipping that is worse than
shipping nothing. And it SKIPS a file already validly signed by someone else:
System.Numerics.Tensors.dll ships with a valid Microsoft signature today, so a naive
pass would replace Microsoft's attestation with ours on a binary we did not build.

Smoke-tested against the certificate that happens to exist on this machine, then
rebuilt unsigned. The MSI came back Status=Valid with the expected signer, and the
payload pass signed 7 of 8 binaries and skipped the Microsoft-signed one by name.
That exercises the whole path before the real certificate arrives.

CI is wired and inert: with no SIGNING_PFX_BASE64 it emits a ::warning:: and
publishes unsigned, because an unsigned release is the currently documented state
and a missing secret must not break the release path. The key is written to disk
only long enough to import, and scrubbed from the runner under if: always() -- a
hosted runner is torn down anyway, a self-hosted one is not.

NOT built: the "trust this certificate" checkbox from runas-helper. It exists purely
to work around SELF-signing, asking the user to add a root CA to their machine. A
CA-issued certificate chains to a root Windows already trusts, so it would be dead
weight. Worth recording that it also could not have been copied as-is: that project
is perMachine and runs certutil -addstore -f Root as SYSTEM, whereas this package is
perUser and cannot write LocalMachine\Root at all -- under Return="ignore" it would
have failed SILENTLY, a checkbox that does nothing.

Also unresolved on purpose: RFC3161 timestamping outlives the certificate but
forfeits the MSI byte-reproducibility Normalize-MsiDeterminism exists to preserve.
Plumbed as -SignTimestampUrl and left empty, to be decided with the real cert.

Mutations, seven:
  FIRED   payload signing stops being opt-in (would break every PR)
  FIRED   MSI signing stops being opt-in
  FIRED   the MSI is signed BEFORE normalisation (which refuses a signed MSI)
  FIRED   the signature is never verified
  FIRED   somebody else's signature is overwritten
  FIRED   a missing secret fails the release instead of warning
  FIRED   the key is left on the runner when a build fails

The ordering mutation was SILENT on the first attempt because it only edited a
comment; genuinely swapping the two calls fires it. A no-op mutation reporting
silence is indistinguishable from a missing guard, which is its own lesson.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-03  `b1b8cdf55`

**feat(pets): reload a pet on screen when its skin is updated**

```
Updating a pet you were looking at used to leave the old one walking around, with
the status line admitting it: "Pets already on screen keep the old version until
they respawn."

Worse, the obvious workaround did not work either. Removing the pet from the tray
and adding it back brings the OLD SKIN back, silently. KillSheep frees the sheeps[]
slot immediately but registry.Decrement only runs on FormClosed, which waits behind
the kill animation, so the re-add still finds RefCount > 0, ResolveExtraType hits
the CACHED parse, and nothing changes. No error, no clue.

ReloadPetType fixes it with one call the codebase already had:
CompanionTypeRegistry.Add displaces the cached entry WITHOUT freeing a pair that live pets
are still borrowing, which is precisely the case it was written for and already
self-tested. Then kill N, spawn N, persist once.

Ordering is load-bearing throughout:
  * stage before any teardown, so a bad file leaves the pets exactly as they were;
  * kill before spawn, so a reload at MAX_SHEEPS cannot half-fail and lose pets;
  * refuse while AnyPetBusy(), because RemoveOnePet does not check and would yank a
    window out from under the mouse;
  * suppress CompanionSpawned for the respawns, or four copies of an updated skin fire
    four module welcomes for pets the user never saw leave.

THE ACTIVE PET ASKS FOR A RESTART instead, and that is a deliberate limit rather
than laziness. Its live definition comes from settings.json via StartUp.xml, not
from the library folder, so remove-and-re-add re-uses the in-memory copy and a swap
cannot work at all. The only in-process path that re-stages it is
LoadNewXMLFromString, which closes every pet of every type, wipes the registry and
resets the mix to autostart copies, while racing the autostart timer it re-arms:
a whole-desktop teardown to refresh one skin. Restarting is the same visible
outcome with none of that, and the app already asks for a restart to activate a
module.

Only the COUNT has to survive a swap. Pinned monitor, scale, mute and speech source
are all persisted per type id and re-read on spawn. Screen position and the running
animation cannot be preserved, because Play() restarts at an XML spawn point.

Mutations, six, all against call ORDER inside one method, which no unit test can
observe (the registry primitive itself is covered by --pettyperegistry-selftest):
  FIRED   the cached parse is not displaced (old skin comes back)
  FIRED   a reload yanks a pet out from under the mouse
  FIRED   spawn before kill (can exceed the pet cap)
  FIRED   modules re-greet on every respawn
  FIRED   the active pet triggers a whole-desktop reload
  FIRED   the pane stops reloading on-screen pets

One guard needed fixing before it would fire: its absence half matched the bare
identifier LoadNewXMLFromString, which the method's own doc comment names in order
to explain why it is NOT used. It matches the call form now. That is the third time
in this file an absence check has been defeated by prose describing the very thing
it forbids, and the lesson is the same each time: assert the CODE form, not the name.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `c17d99cf9`

**feat(updates): check modules and pets weekly, and show the answer when a pane opens**

```
You had to press a button to find out that a module or a pet had an update. Now
opening Options shows it already.

Both panes ALREADY rendered updates correctly: ModulesPaneControl builds an
"Update to vX.Y.Z" button and CompanionsPaneControl builds update cards. They failed for
one reason only, that _lastCatalog was null on open, because CustomShellPane
rebuilds each control on every pane selection. So this is plumbing, not UI.

WEEKLY, not the app's hourly. Missing a new app version for an hour matters because
a user restarts expecting to be told; a module or pet update is not urgent, and the
catalog is a network fetch that would otherwise happen every launch for nothing.
Both panes also refresh when they open, so the interval never decides how stale the
answer looks -- it only bounds background traffic, which is the question an interval
should answer.

THE RESULT IS NOW WRITTEN DOWN. The module check already existed and already ran,
but it threw its findings into a balloon and discarded them, so the Modules pane
still knew nothing when you opened it. A pane can only render an update instantly if
the last answer is on disk. Five settings keys, modelled on appUpdate*, orders
37-41, no schema bump (nullable-absent-reads-as-default is the established pattern).

TWO BUGS FIXED ON THE WAY:

  * ArmModuleUpdateCheck sat INSIDE the module try/catch and behind
    `loadedModules > 0`, so a module-host failure silently took the update check
    with it. A pets check could never have lived there at all, since it must run
    with zero modules installed. All three now arm together, after the catch.

  * EvaluateModuleUpdateCheck seeded its stamp on a fresh install WITHOUT checking,
    so a new install could not learn about a module update until the next calendar
    month. That is precisely the "stamp on a negative answer and go blind" bug
    AppUpdateCheck documents, at thirty times the interval. Deleted.

ModuleUpdateSchedule is retired. Its shape was monthly to the core (a yyyy-MM stamp,
IsDue comparing year*12+month), so weekly could not be a tweak. Replaced by the same
clock-injectable AppUpdateCheck.ShouldCheck the app check uses. Its self-test went
with it, replaced by a weekly one covering the boundary, a never-checked stamp, a
clock moved backwards, and the encode/decode round trip.

The stored key monthlyModuleUpdateCheck is deliberately unchanged: renaming it drags
a migration through three files to alter a string nobody sees. Only the label moved.

Also: one shared in-memory catalog with a 90s life, because opening Preferences then
Modules then Pets downloaded catalog.json three times and self-refreshing panes
would have made that worse. A user-initiated "check now" invalidates it first, or
the button would appear to do nothing. Pet freshness moved into CompanionProvenance and is
now shared by the pane and the background check, so a badge and a notification
cannot disagree, and the hashing runs off the UI thread -- it reads and digests every
installed catalog pet, which was tolerable behind a button and is not on every open.

Mutations:
  FIRED   the module check is gated on modules loading again
  FIRED   the pet check is never armed
  FIRED   the module result is discarded again (both call sites)
  FIRED   the pet result is discarded
  FIRED   the Pets pane stops refreshing on open
  FIRED   the Modules pane stops refreshing on open
  FIRED   the manual check is served the cached catalog

One existing invariant needed updating rather than satisfying: it asserted the Pets
pane calls CompanionProvenance.Classify directly, which the shared helper now does on its
behalf. Its intent, that the verdict comes from the shared classifier and not a
second opinion in the UI, is more true than before.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `d038e0f22`

**feat(installer): offer Repair, and force it to actually repair**

```
The maintenance dialog greyed out Repair and said "cannot be repaired", with no
working alternative offered anywhere.

That came from ARPNOREPAIR=1, whose recorded reasoning was that every component's
KeyPath is an HKCU registry value (ICE38 requires that for anything installing into
the user profile, which is all 18 of ours), so Windows Installer cannot infer from a
KeyPath that a payload FILE has gone missing. That is true, and it is an argument
about ARP INFERRING a broken install. It is not an argument about whether a repair
works once the user asks for one.

So Repair is offered now, with REINSTALLMODE=amus: force-copy every file (a)
regardless of version, and restore machine registry (m), user registry (u) and
shortcuts (s). The `a` is the part that matters. Under the default omus a repair
replaces only files that are MISSING or older, so a file corrupted in place at the
same version survives and the user is told the repair succeeded -- which is exactly
the misleading outcome the old suppression was trying to avoid. Offering the button
without forcing the mode would have been worse than leaving it greyed out.

REINSTALLMODE is only consulted when REINSTALL is set, so a first install and a
major upgrade are unaffected.

ARPNOMODIFY stays at 1, and is set by WiX rather than by us: there genuinely is one
feature, so "no independently selectable features" is honest. ARPNOREMOVE was never
set, so Remove was always available.

  FIRED   repair is suppressed again
  FIRED   repair is offered but only replaces missing files

Both halves are asserted because offering Repair without forcing the mode is the
plausible half-change, and it is the harmful one.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `2983b0beb`

**fix(app): run the message loop with a main form, so a close request can end the process**

```
The installer's files-in-use prompt is an APP bug, not an installer one, and this is
the root cause I reverted yesterday for being unproven. It is proven now.

Application.Run() was called with no argument, because a pet IS a window and no
single window owns the app. That runs a loop nothing can end: closing every window
leaves the process alive. Two consequences, and the second is the damaging one.
Restart Manager asks the app to close and gets nothing, so the installer stops on
"unable to automatically close all requested applications" -- but it has already
closed the windows it could reach, which takes the tray icon with them and leaves
pets wandering the desktop with no way to quit.

The cause is thread placement. WinForms turns WM_QUERYENDSESSION into an exit from
its hidden .NET-BroadcastEventWindow, and that window is created on whichever thread
first touches SystemEvents. On a configured install a module touches it during load,
so it lands on a BACKGROUND thread while every form lives on the UI thread. Session
handling then runs somewhere that owns no forms and the loop never stops.

AppLifetime is a never-shown main form carrying two independent belts, because the
failure is a race about thread affinity and either alone leaves a gap: closing the
main form returns from Application.Run and shuts down through the existing disposal
paths, and an explicit SessionEnding subscription marshals the exit onto the thread
that owns that form regardless of where the event is raised.

MEASURED, installed side by side, same 18 module DLLs, same thread split:

  shipped v1.9.14   STILL RUNNING after 32s   (9 windows, 28 threads up)
  with this fix     exited after 316 ms

Two earlier attempts to measure this were worthless and are worth recording. A
portable build does not reproduce it at all, and neither does a portable build
pointed at a copy of the real data root: both exited instantly with or without the
fix, so the mutations came back SILENT and the fix looked useless. Only the
INSTALLED layout with modules beside the exe reproduces. The first side-by-side run
was also invalid -- the test MSI ships no modules, so it loaded 0 and proved nothing
until they were copied in.

  FIRED   the loop goes back to no main form
  FIRED   a second bare Application.Run() creeps in

The absence half of that guard matches the STATEMENT form with its semicolon: a bare
Application.Run() also appears in a comment a few lines above, and matching the call
alone made the guard fail against correct code. Same prose-versus-code trap that has
now bitten twice in this file.

util:CloseApplication stays. It runs at sequence 3999 and the prompt is raised at
InstallValidate 1400, so it can never prevent the prompt -- a deferred action cannot
run before InstallInitialize. It is the belt to this fix's braces, for whatever else
might hold a file.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `952862881`

**fix(installer): give the sidebar a light content panel, so the checkbox stops showing a white box**

```
Reported as a large white box around the launch-on-finish checkbox text.

WixUI lays every control of the welcome and exit dialogs over ONE full-bleed bitmap.
Title, description and the optional text are Transparent (attrs 196611) so they sit
over artwork happily. ExitDialog's OptionalCheckBox is attrs=2, NOT transparent, so
it paints its own 220x40 rectangle in the dialog background colour. Against WiX's
own pale default bitmap that is invisible; against a full-bleed pasture it is a
slab. Nothing in our authoring could fix it, because the control belongs to the
stock dialog.

So the art carries the constraint. dialog.bmp now has the light content panel the
stock layout assumes: the pasture stays crisp in the left band where the sheep and
pikachu are, and from x=180px (x=135 of 370 dialog units, where controls begin) it
eases over 26px into a near-flat panel. That fixes the checkbox and makes every
overlaid string more legible, which the licence page also needed.

Blended towards 240,240,240 rather than white, on purpose: COLOR_BTNFACE is exactly
what the checkbox paints itself, so matching it is what makes the slab disappear.
Blending to white would have left it visible as a slightly brighter rectangle.
Strength was tuned by measuring rather than by eye: at 0.955 the ghosted pets under
the checkbox still left the darkest channel 28 off target, which would show, and
0.975 brings it to 6.

The guard samples the exact region the checkbox occupies and requires the darkest
channel there to be at least 216. Art is the one thing here with no compiler, so a
redesign could otherwise silently undo this.

  FIRED   the original full-bleed art is restored (darkest channel 0, needs >= 216)

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `467f32b0d`

**feat(installer): launch the pet when setup finishes, and stop the page flashing**

```
Both from the first interactive run of the installer work.

LAUNCH ON FINISH, ticked by default. Installing a desktop pet and being left with
an empty desktop is a strange place to stop. No new dialog: the stock ExitDialog
already carries an OptionalCheckBox bound to WIXUI_EXITDIALOGOPTIONALCHECKBOX and
captioned from WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT. Giving the property a value
makes it render ticked, which is the same MSI rule that keeps CLEANINSTALL
deliberately valueless twenty lines above; here the default is the safe direction,
so the rule works in our favour.

The action is immediate, not deferred: Finish is clicked after InstallFinalize,
when there is no install script left to defer into. Impersonated so the pet belongs
to the user, asyncNoWait so setup closes instead of waiting on a process that runs
until the user quits it.

THE FLASH. Reported as the licence page appearing for a split second before the
reset page. It was not the licence: PrepareDlg is shown at UI sequence 49 while
costing runs and is replaced by whatever dialog comes next. Every stock dialog
shares the WixUI_Bmp_Dialog background, so in the normal flow that hand-off is
invisible. The reset page was banner-styled, which turned an invisible swap into a
visible one. It now uses the stock geometry, background at 0,0,370,234 with content
inset from x=130, copied from WelcomeEulaDlg's own Control rows rather than guessed.

The reset page still sits BEFORE the licence, at 1294. That is not a preference:
1295-1298 are the four stock welcome variants and pinning one of those fails the
build with WIX0179, so 1294 is the only free slot. It also keeps the button labels
honest, Next here and Install there, and means nothing has been decided when the
question is asked.

Five more surface assertions, taking it to 19: the launch box is ticked by default
and captioned, its action is immediate rather than deferred, launching is
conditioned on the checkbox, and the reset page uses the stock background.

Also: Control has no Multiline attribute in WiX 5 (WIX0004), so the checkbox
caption is one line and the clarification moved into the body text.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `fa0089856`

**test(installer): assert the MSI surface, and allow a side-by-side test build**

```
Two things, both so the installer work committed in 4bce3e9 and 006057e can
actually be verified rather than asserted in prose.

TEST-MSISURFACE.PS1. Every fact in those two commit messages (the reset page at
1294, no CLEANINSTALL Property row, the type-1106 action after InstallFiles, the
CloseApplication attributes) was checked once by hand and then written down. A
commit message cannot fail. Worse, all of these fail SILENTLY: an unreachable
dialog, a checkbox whose property is pre-set so it renders ticked, an action
sequenced before the files it needs -- none break the build and none show up in
ICE. One of them, the unreachable dialog, had already shipped. Now 14 assertions
run against the validation copy inside build-installer.ps1, next to the existing
Test-MsiUpgradeSchedule.ps1 call.

  FIRED   the destructive checkbox is given a default value (renders TICKED)
  FIRED   the reset page is unscheduled (authored but unreachable)
  FIRED   the wipe stops being conditioned on the checkbox
  FIRED   the terminate fallback is removed (back to ask-nicely-only)

A fifth mutation, TerminateProcess=1 -> 0, came back SILENT and is not a gap:
that attribute is the exit CODE to kill with, not a boolean, so 0 still
terminates. Removing the attribute is the real negative case, and that fires.

SIDE-BY-SIDE TEST BUILDS. -UpgradeCodeOverride plus -ProductNameSuffix produce an
MSI Windows Installer treats as a different product, so it can be installed next to
the shipped build without RemoveExistingProducts uninstalling it. That is the only
way to exercise the installer UI on the one machine that runs the real thing. Both
must be given together, and the override also isolates the registry root and the
component-GUID namespace: two products sharing either would let one uninstall pull
files out from under the other. release.yml passes neither, so the published MSI is
byte-unaffected -- confirmed by building it both ways.

Also worth recording: PowerShell unrolls a returned collection, so a no-row query
came back as $null and a one-row query as that row's own values, making .Count lie
about how many rows matched. Most of these assertions turn on exactly that count.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `319ee586f`

**refactor(pets): drop per-id caches when a pet file is replaced**

```
Groundwork for the automatic update checks and the pet auto-reload, landed on its
own because both need it and neither should carry it.

Two caches are process-lifetime and neither expires: CompanionCatalog's header-name cache,
which feeds every tray menu, and the Pets pane animation/sound counts. Both were
correct when a pet could only be replaced by a download that restarted the app.
CompanionCatalog.cs said so in as many words. That stops being true the moment an update
is applied in-process, and the failure is quiet: the tray keeps offering the OLD
pet name and the card keeps showing the OLD counts, with nothing to expire them.

Each cache gains a Forget for one id, called from the single place a pet file is
actually rewritten, in CompanionsPaneControl.FetchPetAsync right after the provenance
stamp. Deliberately not called from the render paths: the point of the cache is
that rendering is frequent and replacement is rare.

The invariant asserts the calls sit at the WRITE site, not merely that the two
methods exist, because a cache-clearing method nobody calls is the exact failure
this file exists to catch and it has already happened twice here.

Mutations:
  FIRED   the display-name cache is never dropped
  FIRED   the stats cache is never dropped

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `006057e62`

**fix(installer): close a running pet before installing, and make the reset page reachable**

```
Three things, all in the installer.

CLOSE THE RUNNING APP. Adds WixToolset.Util.wixext 5.0.2 to the locked toolchain
and uses util:CloseApplication, which is what the TODO in DesktopPet.wxs has
planned since the pins were recorded. Restart Manager already tries and fails:
measured on a real install, the app answers WM_QUERYENDSESSION in 27ms and is
still running 32 seconds later with 9 windows and 28 threads up. Worse than a
prompt, because RM closes the windows it CAN reach and the process survives, so
the user is left with pets on the desktop, no tray icon and no way to quit.

Root cause found but not fixed here: WinForms hangs session-end handling off a
hidden broadcast window that lands on a BACKGROUND thread (tid 39260) while every
form lives on the UI thread (tid 43076), so the handler runs somewhere that cannot
stop the message loop. A bare build does not reproduce it and a copy of the real
data root does not either, so a fix inside the app stayed unproven and was
reverted rather than shipped on a hunch. TerminateProcess="1" makes the installer
correct regardless of why the app will not close.

THE RESET PAGE WAS UNREACHABLE. The "clear all settings and modules" checkbox
added in 4bce3e9 was published as a NewDialog on WelcomeEulaDlg's Install button
at Order=10. MSI runs a control's events in ordering order and stops at the first
one that CLOSES the dialog, and WixUI_Minimal already publishes EndDialog/Return
there at ordering 2, so the page could never appear. The sibling runas-helper
project shipped exactly this bug for several releases before catching it against a
built MSI's ControlEvent table; this is its corrected shape. Scheduled at
InstallUISequence 1294 instead, the one free slot before WelcomeDlg(1295), which
is also why pinning 1298 earlier failed the build with WIX0179. Back becomes Next,
since a sequenced dialog cannot NewDialog its way back.

A GUARD FOR WIX0104. "--" cannot appear inside an XML comment, which is trivial to
write and invisible until the wxs is compiled: locally a separate slow step, in CI
only on a v* tag. It broke a release tag once and three builds today. The gate now
parses every installer\*.wxs as XML, which rejects exactly what the WiX compiler
rejects without a bespoke dash-hunting regex that would have to model comment
boundaries itself.

  FIRED   a double dash is written inside a comment (WIX0104)
  FIRED   an element is left unclosed

Also adds -SkipValidation to build-installer.ps1: ICE runs through msiexec and
Windows Installer serialises machine-wide, so any interactive install sitting on a
dialog blocks the build indefinitely. The first attempt used `return`, which jumped
past the copy into dist\ and reported success while leaving the previous MSI in
place; it guards only the ICE call now.

Toolchain: the lock hardcoded a two-package list and a UI-extension-specific
verifier, so both were generalised over a set rather than copied. Both new digests
were verified against the values recorded in the TODO before being written to the
lock: nupkg dda1cc1b... (888252 bytes) and wixext5/WixToolset.Util.wixext.dll
c76cd00e... (944912 bytes).

Verified: ICE validation passes; the MSI's InstallUISequence shows the reset page
at 1294 ahead of WelcomeEulaDlg with no dead publish rows; Wix4CloseApplication
carries target DesktopPet.exe with attributes 33 (CloseMessage + TerminateProcess);
a /qn install over a running app returns 0 and the app is gone afterwards.

NOT verified: the interactive prompt. Under /qn, Restart Manager force-closes
without asking ("will attempt to shut down and restart applications in no UI
modes"), so a silent install cannot exercise the path the user actually hits. Our
close also necessarily runs at sequence 3999, after InstallValidate(1400) raises
that prompt, because a deferred action cannot run before InstallInitialize(1500).
Whether the prompt is gone needs an interactive install.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `4bce3e973`

**feat(installer): a "clear all settings and modules" checkbox for a fresh start**

```
Unticked by default. For someone whose install is in a state they would rather not
debug than unpick.

The APP does the deleting, via a new --factory-reset flag, and the installer only
decides whether to call it. What counts as "this app's data" is decided at runtime
by AppPaths (an override, a portable layout beside the exe, or the installed
profile directory), so re-deriving it in WiX would be a second definition free to
drift from the first.

TWO locations, which is the part that is easy to miss: %LOCALAPPDATA%\DesktopPet
holds settings, downloaded pets, fortunes and caches, but downloaded MODULES live
beside the exe in <install>\modules. A major upgrade never touches those, which is
exactly why they survive a reinstall today and why "as if brand new" has to name
them. The MSI ships no modules of its own (runtime-files.txt has none), so nothing
deleted there is ever an installer-owned file.

FactoryReset refuses rather than guesses. Not fully qualified, a drive root, within
one level of a root, or any profile/system folder, and it declines and says why.
Writing the tests caught a real bug in that guard: Path.GetFullPath("C:") returns
the CURRENT DIRECTORY on C:, not the root, and "data" resolves under the working
directory, so both would have resolved to some deep innocent-looking path and been
allowed. It now rejects anything not fully qualified before resolving it.

WiX notes, all three earned the hard way:
  * The property is declared with NO value. An MSI checkbox is ticked whenever its
    property is non-empty, so Value="0" would have ARMED a destructive option by
    default.
  * The dialog is reached by a NewDialog published at ordering 10 on
    WelcomeEulaDlg's Install and InstallNoShield buttons, which is after the stock
    EndDialog rows at 2 and 4. It is NOT scheduled through InstallUISequence: that
    band is packed solid (1295-1300) and pinning a number squeezes the stock
    dialogs out of their own numbering, which is error WIX0179.
  * A "--" inside an XML comment is error WIX0104. This has now broken a build
    twice in this repo, the first time as a latent failure that only surfaced on a
    release tag.

build-installer.ps1 gains -SkipValidation, because ICE runs through msiexec and
Windows Installer serialises machine-wide: any interactive install sitting on a
dialog blocks the build indefinitely. First attempt used `return`, which jumped
past the copy into dist\ and reported success while leaving the previous MSI in
place; it now guards only the ICE call.

Verified: ICE validation passes, and the built MSI's tables carry the dialog, the
checkbox bound to CLEANINSTALL, entry at ordering 10, the type-1106 deferred
impersonated action, and its sequencing at 4001 directly after InstallFiles(4000),
with no CLEANINSTALL row so the box starts unticked. --factory-reset itself was
run against a sandboxed copy of the build output and emptied both locations while
leaving every application file intact.

NOT verified: that the dialog renders and flows correctly during a real install,
and that the deferred action fires. Both need an interactive install, and the only
machine available is running a live setup with 6 modules and 2 downloaded pets that
the option would destroy. A same-UpgradeCode sandbox install is not an option
either: it would trigger RemoveExistingProducts and uninstall that setup.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `fb731696a`

**fix(tray): a pet id reached the user wherever the tray held only an id**

```
"Remove a pet" listed "Shimeji 3x56f4pl" while "Add a pet" listed "Monkey D.
Luffy" for the same pet, because the two took different routes to a label.

CompanionCatalog.DisplayName(folder, catalogName) needs a name passed IN: it tries the
curated character map, then the supplied name, then a title-cased folder id. The
enumeration path has the pet's own <petname> to hand, because EnumerateLocal reads
each header, and passes it. Every caller that holds only an ID passed null, which
skips the header entirely and lands on the folder id. Three surfaces did that, all
via ContextMenus.TrayPetName: Remove a pet, the Pet Speech cascade, and the
Preferences "pet that speaks for the app" dropdown.

New CompanionCatalog.DisplayNameForId(id) locates the pet the same way TryReadPetXml
does (library root, then bundled) and reads the header, so an id reads identically
everywhere. Cached for process lifetime: these are tray menus rebuilt on every
open, and a pet's header cannot change without the file being replaced, which
arrives through a download that restarts the app.

Found a second one while sweeping: the built-in has no folder to read, so it fell
to the prettifier, which upper-cases the first letter of a folder id -- the default
pet would have read "ESheep". It returns the id's own casing now.

Checked the rest rather than assuming. Modules are fine: CompanionHost.InstalledTypes()
already goes through EnumerateLocal, which is why the Reminder module's per-calendar
"Reminder pet" dropdown was already correct. The Pets pane passes pet.Name from the
catalog. activePetName is Header.Petname, so the active-pet entry was correct too.

Mutations, five:
  FIRED   the tray goes back to DisplayName(id, null)
  FIRED   the header is never consulted (always prettify)
  FIRED   the header lookup returns nothing
  FIRED   the built-in falls through to the prettifier
  FIRED   the cache stores the id instead of the name

Two of those needed the assertions rewritten before they would fire. The cache test
first used the built-in, which returns BEFORE the cache is consulted, so it proved
nothing about caching; it now uses a fixed absent id whose prettified name differs
from the id itself. And "the header is never consulted" stayed silent against any
runtime assertion, because a build output ships no bundled pets and anything
asserted over INSTALLED pets passes vacuously on a clean runner -- so the call site
is pinned by a source invariant instead, the same hole that let ScaleVelocity ship
unguarded this morning.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `f527fdf6b`

**docs(backlog): the sprite dedupe is done, and why my recommendation was wrong**

```
I recommended deferring it on blast-radius grounds. There are no users, which the
repo's own no-users-until-10-stars gate exists to settle, and it says check rather
than guess. Records the three things worth keeping: re-gridding can compress worse,
DrawImage resamples a 1:1 blit, and a migration that can legitimately no-op must
still stamp the version or it strands the pet for every later migration.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `65eb2c006`

**chore(catalog): petstudio 1.6.7 and the 31 slimmed pets**

```
Regenerated after committing the pets and the zip, in that order, so every
recorded sha256 is the hash of the blob raw.githubusercontent actually serves.

Pet content in the catalog goes 80.1 MB -> 69.3 MB (13.5% across all 53; 20.0%
across the 31 converted). v1.9.7's freshness check compares the catalog sha256
against the installed file, so this reaches an existing holder rather than only
new downloads.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `9f8ae346e`

**chore(modules): publish petstudio 1.6.7**

### 2026-09-02  `cf7384e67`

**perf(pets): drop duplicate sprite cells and the _left suffix (format 1.6 -> 1.8)**

```
Saves 11.0 MB of 55.4 MB across the converted corpus (20.0%) and renames 232
actions, in two migrations plus the emitter fixes that stop both recurring.

DUPLICATE CELLS (dedupe, 1.6 -> 1.7). SpriteSheetBuilder deduped poses by
ShimejiPose.FrameKey, which is the image NAME plus the anchor, so a skin shipping
the same picture under two filenames got two cells. 559 wasted cells across 24
pets. Two causes, only one ours: an Android-Shimeji template that duplicates
sprite files (seven pets share a byte-identical duplicate structure), and a
reversed sequence emitted as fresh cells -- brq51bkr's descend is its climb
frame-for-frame backwards, 26 cells and 1.08 MB to say "play it backwards", when
<sequence> already takes an arbitrary frame list. The builder now keys cells on
CONTENT (source pixels + anchor + anchor-to-top), so both collapse and every
collapsed pose still resolves to its survivor's tile.

DIRECTIONAL NAMES (undirect, 1.7 -> 1.8). A converted pet keeps one copy of each
mirrored pair and flips its whole sheet on <action>flip</action>, so walk_left
already walks both ways and the suffix reads as a limit the pet does not have.
The maintainer hit this directly: reading a reachability map of _left names and
asking where walk_right went. Luffy now reads stand / walk / jump / climb /
descend / climb_ceiling. 232 renames, 0 refused. PetEmitter.UndirectNames holds
the safety rules for BOTH the emitter and the migration so they cannot drift: a
rename is refused if it would produce a magic name (fall/drag/kill/sync, which
Xml.cs matches exactly), collide with an existing name, or be claimed by two
animations at once.

Verified as a visual no-op, twice and independently. The migration re-slices the
sheet it actually produced and compares each frame to the cell it replaced; a
separate Python pass then re-derived the same answer from the two XML files,
checking rendered art plus repeat, repeatfrom, start, end, next, border and
gravity for all 31 pets. 0 failures, and `verify` still reports 0 invalid, 0
round-trip failures and 0 converted pets stranding an animation.

Mutations, seven:
  FIRED   the content dedupe is removed entirely
  FIRED   the anchor is dropped from cell identity
  FIRED   collapsed poses never get a tile (alias fill removed)
  FIRED   a rename may become a magic name
  FIRED   a rename may collide with an existing name
  FIRED   two animations may claim the same new name
  FIRED   art is scrambled during the pack (25 pets refused, none written)

Two bugs found while building it. Graphics.DrawImage resamples even a 1:1 blit,
because the default InterpolationMode is bilinear, so the first pack altered edge
pixels and the equivalence check rejected all 31 pets -- the guard working before
it had anything to guard. Replaced with a raw row copy. And a pet with nothing to
dedupe was left at 1.6, so it could never reach `undirect`: 3g8t9v4e has no
duplicate cells and 8 names wanting renaming, and it was silently stranded. The
version now marks "has been through the pass", not "was changed by it".

Re-gridding can compress WORSE, so each pet keeps its original sheet unless the
new one is genuinely smaller: gengar came out 1 KB bigger, and five pets whose
only duplicates are blank cells save nothing because the grid does not shrink.

No app release: the host does not reference the converter. Pets and modules serve
off master, so merging is the publish. petstudio 1.6.7 source-links both changed
files.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `0d575373d`

**docs(backlog): sprite-sheet duplication across the converted corpus, measured**

```
Followed up the Luffy finding across all 31 converted pets by hashing every cell
of every sheet. 26 of 31 carry duplicates.

Deduping and re-encoding the corpus saves 7.0 MB of 48.4 MB (14.6%). My first
linear bytes-per-cell estimate said 26%, which was wrong: PNG deflate already
recovers about half the theoretical waste, so the number came from actually
building the deduped sheets and re-encoding both with identical settings.

Two causes. Ours: a reversed sequence is emitted as fresh cells, so brq51bkr's
descend_left is its climb_left in exact reverse across 26 duplicated cells and
1.08 MB, when a reversed frame list costs nothing. Theirs: seven pets share a
byte-identical duplicate structure from one Android-Shimeji template that ships
duplicate sprite files.

Recommends fixing the converter (helps every future import, costs no one a
re-download) and NOT re-migrating shipped pets for this alone, since changing every
sha256 makes existing users pull ~40 MB to save 7 MB on downloads already done.
Also records that regridding made gengar 1 KB worse, so any dedupe needs a
per-pet keep-the-original guard.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `3ff48b6b6`

**docs(smoketest): why converted pets have no walk_right, and how to verify a ceiling pose**

```
Both came out of the maintainer's first pass through B4/B5 on Luffy.

There is no walk_right to drag into Pet Studio's timeline because a Shimeji skin
draws walk_left and walk_right from the same sprites with only the direction
reversed, so the converter keeps one copy and mirrors it via the turn animation's
action=flip (FormCompanion.FlipOrientation). Chain turn, walk_left, climb_left to drive a
rightward walk by hand. Luffy's source has 37 animations; the converted pet has 23
for exactly this reason.

The ceiling pose reads as a sideways wall climb because the artist rotated the
figure ~90 degrees rather than inverting it. The note points at the source
declaration instead of the picture: animation.json gives each action an explicit
type and subtype, and Luffy's climb_ceiling_left is CEILING/HANG. Judging this from
art alone is what produced the reverted frame-swap, so that warning is in the row.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `196e5a793`

**docs: correct the SMOKETEST.md check count (58, not 60)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `2b2ee72a3`

**docs: point the handoff at SMOKETEST.md as the first action next session**

```
The START HERE block told the next session to walk "rows 1-9 of the live smoke
script". Those rows no longer exist: they were the stale ten-row table, now
replaced. Points at the real document instead, and makes reading the maintainer's
report the first action rather than a background wish.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-02  `bf261d38c`

**docs: a live smoke test worth walking**

```
The old script was ten rows in RELEASE-CHECKLIST.md and had not grown with the
product since before pets could climb. It said nothing about jumping, gripping
windows, multi-monitor pinning, fullscreen stand-down, per-pet speech routing or
the update check, so a green pass over it was close to meaningless on a modern
release. That is part of why it went unwalked across ten of them.

SMOKETEST.md replaces it: 60 checks in ten sections, with a 12-minute Core pass
(A-E) that targets the class of bug which has actually reached users. Each row says
what to watch for, and the regression watchlist maps every shipped bug to the row
that would have caught it -- walk-in-place, the welded cursor, the UFO over a
fullscreen game, the pet on the wrong monitor, the sub-second rest.

The argument for it is in the header, from the record rather than from principle:
four of the last ten releases exist only because the maintainer opened the app and
looked, and every one of those bugs was visible within thirty seconds while the
whole automated suite passed straight over it.

Readme gains the two user-facing features it never documented: per-pet monitor
pinning (including the honest limit that pets do not traverse monitors) and getting
out of the way of fullscreen games.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `b78ee115f`

**chore(catalog): petstudio 1.6.6**

```
Regenerated after committing the zip, in that order, so the recorded sha256 is the
hash of the blob raw.githubusercontent.com actually serves.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `9c62a9e59`

**chore(modules): publish petstudio 1.6.6**

### 2026-09-01  `d7e6f0240`

**chore(petstudio): 1.6.6 -- pick up the ScaleVelocity fix in previews**

```
PetStudio source-links Animations.cs and RuntimeGeometry.cs, so the walk-in-place
bug fixed in v1.9.13 is compiled into its DLL too: a small pet previewed in the
behaviour timeline animated on the spot exactly like the live pet did. The freshness
check caught the stale payload -- the host release alone would not have refreshed it,
since modules ship off master rather than in the MSI.

A payload refresh gets a version bump because modules.json is what the in-app Update
button compares; republishing 1.6.5 with different bytes would offer nobody anything.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `05b8b04de`

**fix(release): SHA256SUMS.txt named files nobody could verify**

```
The two author packages were listed as nuget/<name> -- their path in the BUILD tree.
GitHub release assets are flat, so a downloader has all five files in one directory
and sha256sum -c SHA256SUMS.txt reported two of them missing, on a release whose own
notes say to verify downloads against SHA256SUMS.txt. The hashes were always correct;
the instruction was unfollowable. Found by verifying v1.9.13 assets rather than
assuming the pipeline was right.

The old comment claimed the paths were copy-pasteable next to the file layout of the
release, which was the actual error: that is the layout of dist, not of the release.
Hash the build path, print the download name.

Takes effect on the next tag; the published v1.9.13 SHA256SUMS.txt keeps the prefix.
All four of its hashes verify by basename.

Mutations:
  FIRED   the checksum line prints the build path again
  FIRED   the basename derivation is dropped

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `cf64178f0`

**docs: backlog and handoff through v1.9.13**

```
Files the three fixes of 2026-09-02, and files the thing that was NOT built: a pet
still cannot walk between monitors. The user asked for traversal directly; v1.9.12
only relabelled the setting that sounded like it. The note records why it is not
small -- every border, gravity and respawn decision resolves against one
Screen.Bounds, and this box's own 3440x1440 + 2560x1080 pair has a 360px floor
mismatch, so a pet crossing at floor level walks into empty space.

Also sharpens the live-smoke item rather than just re-counting it. Four of the last
ten releases exist only because the user opened the app and looked; every one was a
first-thirty-seconds bug the whole suite passed over. That is the argument for
walking the script, and it now sits in the item.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `c6e9a8229`

**fix(pets): a small pet walked in place, and a pinned pet ignored its monitor**

```
Two bugs found smoke-testing 1.9.12.

A small pet played its walk cycle on the spot. ScaleD rounds a scaled velocity to
an int, so a walk of -2 px/step at 25% is -0.5, and Math.Round's banker's rounding
makes that exactly 0. Reported on a 25% Luffy, but it hits any pet whose walk is 1
or 2 px/step, which is most of them. New ScalePolicy.ScaleVelocity keeps the sign
and floors the magnitude at one pixel, so a moving animation always moves; zero
stays zero, so a still pose is never given motion it never had. Position offsets
stay on ScaleD, where rounding to zero is correct.

A pet pinned to monitor 2 spawned on monitor 1. PinnedDisplay looked the pet up in
the petEntries registry, but AddSheepCore calls Play() inside its initialize
callback and only registers the pet afterwards, so the lookup missed and fell back
to the ACTIVE pet's id. It now reads FormCompanion.PetTypeId, which comes from Animations
and is populated before the form is constructed.

Mutations, six run against the new guards:
  FIRED   the walk X velocity goes back through ScaleD
  FIRED   the Y velocity goes back through ScaleD
  FIRED   velocity scaling reverts to plain rounding (walk-in-place)
  FIRED   a still pose is given motion it never had
  FIRED   the clamp loses the sign (walks the wrong way)
  FIRED   the pin reads the registry again (spawns on the wrong screen)

The first two only fire because of a guard added after the initial sweep left them
SILENT: ScaleVelocity was unit-tested but nothing asserted Animations.cs actually
called it. A correct function nobody wires up is the same gap this file caught
earlier in the session. Its first form was also too loose -- matching one
ScaleD(...UnscaledOffsetY) passed while the other had already been switched -- so
it now asserts both offsets by name and the absence of ScaleVelocity on either.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `8587eb1fb`

**chore(release): 1.9.12 — monitor pinning + the fullscreen respawn fix**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `b83b79727`

**feat(pets): pin a pet to one monitor, and say what "multiple screens" really does**

```
Requested: "I want Hornet on monitor 2 and Pearl on monitor 1 only."

A per-pet-type monitor pin, stored the same way per-pet SIZE already is
(CompanionMonitorEntry mirrors CompanionSizeEntry). The picker appears on each pet card in the
Pets pane and only when more than one screen is attached -- on a single monitor it
would offer one choice with an outcome identical to the default.

BEHAVIOUR, per the user's call: a PINNED pet is honoured strictly and HIDES when a
fullscreen app takes its screen, because "Hornet on monitor 2" is an instruction, not
a preference to override the first time a game starts. An UNPINNED pet keeps the old
behaviour and relocates to a free monitor rather than vanishing.

A pin is validated against the CURRENT screen list on every READ, never on write.
Monitors get unplugged, and a pin to a display that no longer exists must read as
unpinned -- a setting that makes a pet permanently invisible is worse than one that is
ignored. The stale value is deliberately kept, so plugging the screen back in restores
the pin. Children are never pinned separately: a child follows its parent, and pinning
one would tear a UFO off its sheep.

WHAT "ALLOW MULTIPLE SCREENS" ACTUALLY DID, since the question came up and the answer
was not what the label implied: it has exactly ONE use site, and it only decides
whether a pet SPAWNS on a random screen instead of the primary. Pets have never walked
between monitors -- each is bounded by ScreenArea for its own DisplayIndex and turns at
that screen's edge. Dragging one across DOES re-home it (EndDrag), and the multiscreen
gate on that path is commented out, so it works either way. Relabelled from "Allow
multiple screens" to "Let pets spawn on any screen (they stay on the one they appear
on)", the same class of fix as the "Pets at startup" relabel.

TRAVERSAL -- a pet WALKING from one monitor to the next -- does not exist and is not in
this change. It needs continuous virtual-desktop bounds plus a per-monitor floor and
taskbar mapping, which is real work on a 3440x1440 + 2560x1080 pair where the screens
are different heights. Filed rather than half-built.

MUTATION TESTED, seven mutations, all firing:

  pin to a MISSING screen is trusted   -> reads as unpinned when the screen is gone
  re-pinning appends                   -> re-pinning replaces rather than duplicating
  an empty pet id is stored            -> refused rather than stored
  the spawn ignores the pin            -> pinned pet spawns on its screen
  a pinned pet relocates anyway        -> pinned pet hides rather than moving
  the pin is invented, not read        -> read per type from settings
  (plus the RemoveAll predicate)       -> replacement, not accumulation

MY HARNESS WAS WRONG BEFORE THE CODE WAS. Four mutations first came back SILENT
because I measured `& $exe; $LASTEXITCODE` -- and a GUI exe does not block PowerShell,
so that exit code is meaningless. run-gate.ps1 already documents this ("A GUI exe does
not block PowerShell, so wait explicitly") and uses Start-Process -Wait -PassThru. Once
measured the same way, every mutation fired. Two of the four were ALSO genuinely
untested: nothing in the invariant file covered the FormCompanion half at all, which is now
its own assertion.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `956cd1ef7`

**chore(release): 1.9.11 — the fullscreen respawn fix**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `ff2d38f79`

**fix(fullscreen): a hidden pet could respawn back over a fullscreen game and stay there**

```
Reported from smoke testing: the UFO (`spawn_ship`) played over a fullscreen borderless
game. Not a detection failure -- FullscreenScan saw the game correctly throughout. It
was a STATE LATCH that could never reset, plus three places that showed a window
without asking whether the screen was free.

THE MECHANISM, in order:

  1. game goes fullscreen -> the pet hides: _fullscreenHidden = true, Visible = false
  2. a hidden pet KEEPS TICKING. CheckFullScreen hides it and returns; Timer1_Tick then
     runs NextStep() regardless, so the animation continues invisibly and reaches a
     respawn -- `spawn_ship` in the sheep.
  3. Play() set Visible = true and TopMost = true UNCONDITIONALLY, and never cleared
     _fullscreenHidden.
  4. the next scan saw the monitor still blocked, dropped TopMost... and then hit
     `else if (!_fullscreenHidden)`, which was FALSE. The branch believed it had already
     hidden the pet, so it never hid it again.

The pet was left visible over the game PERMANENTLY, held there by the very flag whose
job was to keep it away. A latch that records "I did the thing" desyncs the moment
anything else undoes the thing.

FIXES:

* The hide is ENFORCED on every scan, not latched. Ask "is it visible?" (the window's
  own state, which cannot lie) instead of "did I hide it?" (a belief that can go stale).
  Anything that shows the pet behind our back is now corrected within one scan.
* Play() asks MonitorIsBlockedNow() and spawns hidden + not-top-most when the screen is
  taken, setting the latch to match. This also removes the visible flash that existed
  even when the next scan did correct things.
* The CHILD show path got the same question. A child is a separate window with its own
  code path and inherits nothing from a hidden parent -- and the UFO ship is a child, so
  this was reachable independently of the latch.
* Grabbing a pet no longer re-asserts TopMost unconditionally.

Line 840 ("bring to top again on each new animation") was ALREADY guarded on
hwndFullscreenWindow and needed nothing -- checked rather than assumed.

MUTATION TESTED, six mutations, all firing, restored green -- the first restores the
reported bug exactly:

  hide latch restored              -> enforced every scan, not latched
  Play shows unconditionally       -> a respawning pet cannot appear over a fullscreen app
  Play top-mosts unconditionally   -> same
  child shown unconditionally      -> same
  grabbed pet re-topmosts          -> same
  spawn check bypasses the scanner -> same

One assertion came back SILENT first time and had to be rewritten: it tested for the
ABSENCE of an adjacent `TopMost = false; TopMost = true;` pair, and the comment I added
between those lines defeated the adjacency, so the mutation restoring the bug did not
recreate the pattern. Rewritten to assert the CONDITION is present. Asserting an absence
is fragile in exactly this way -- the standing rule about testing the condition rather
than the shape earned itself again.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `9aac31bdf`

**docs: session close — README, handoff and backlog reconciled through v1.9.10**

```
The README's update-check bullet said "once a day at launch", which v1.9.10 made false
three commits ago. It now says what the code does: at launch AND when Preferences
opens, at most once an hour, still notify-only.

handoff.md START HERE extended through v1.9.10, including WHY that release exists (the
1.9.8 update check blinded a fresh install for 24h and was caught the same day) and the
fourth mistake worth not repeating: a throttle that stamps on a NEGATIVE answer goes
blind, so ask what the interval is bounding — here network traffic, not freshness.

BACKLOG: the v1.9.10 fix recorded with its known limitation (older installs keep the
24h logic, so it improves future updates rather than the one it announces). The open
count is now honest at EIGHT — a section heading and two struck-through resolved
entries were carrying the open marker and inflating it. The smoke-script gap is seven
releases, not six.

No code in this commit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `102db8953`

**fix(update): the daily check blinded a fresh install for its first 24 hours**

```
Reported: closed 1.9.8, reopened it, and it did not flag 1.9.9. The app was behaving
exactly as written; the design was wrong.

What actually happened, from the stored state (appUpdateLastCheckUtc
2026-09-01T15:18:36Z, appUpdateLatestVersion ""):

  15:09:36Z  catalog starts saying app.version 1.9.8
  15:18:36Z  the app checks -- running 1.9.8, catalog says 1.9.8, nothing newer.
             Stores "" and STAMPS the check time.
  19:09:02Z  catalog starts saying 1.9.9
  ...        ShouldCheck() declines until 2026-09-02T15:18Z. Restarting cannot help.

THE FLAW: the stamp is written even on a NEGATIVE answer, and the first check after
any install always IS negative -- you just installed the newest build. So every fresh
install went blind for its first 24 hours, which is precisely the window in which
someone restarts the app and expects to be told. Stamping on a negative is still
right (an offline machine must back off), but a DAY is the wrong number for it.

The interval's job is to bound NETWORK TRAFFIC when the answer is "nothing newer" --
it was never a statement about how fresh the answer needs to be. One hour bounds that
to a single small TLS fetch per hour however often the app restarts, and it makes
restarting mean something again.

ALSO: opening Preferences now refreshes, on a 1-minute floor. The footer is the ONLY
place the answer is ever shown, so asking when the user actually looks is the most
direct fix -- and it is the only thing that lets a LONG-RUNNING instance notice at
all, since a process left open for days never re-runs its launch check. Fire and
forget, never blocks the window opening, and the label is rebuilt in place if the
answer arrives and is news.

MUTATION TESTED, four mutations, all firing, restored green -- the first of them
restores the exact reported bug:

  24h interval restored              -> checked 90 minutes ago re-checks
  interactive floor == launch        -> opening Preferences may refresh inside the interval
  interactive floor removed          -> an interactive refresh still has a floor
  interval ignored entirely          -> checked 30 minutes ago does not re-check

A note on this version number: 1.9.10 is exactly the pair that string comparison gets
backwards (it sorts below 1.9.9), so the assertion written for that case two releases
ago stops being hypothetical here and starts being the thing that keeps the feature
honest.

Known and unfixable by this commit: an install on 1.9.8 or 1.9.9 still carries the old
24h logic, so this improves FUTURE updates, not the one being announced. Clearing
appUpdateLastCheckUtc in settings.json (with the app closed) makes an older build
re-check immediately.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `485425782`

**docs: README covers this session's user-facing changes; handoff for the next one**

```
README was structurally fine (all six modules listed, no hardcoded version) but silent on five things a
user would now see:

  * one pet speaks for the app, and the Preferences setting that picks which
  * the once-a-day notify-only update check, and where to switch it off
  * Reminder's per-calendar "Reminder pet"
  * AI Brain's "Model residency", including that the pane reports what is ACTUALLY resident via /api/ps
    rather than quoting a default OLLAMA_KEEP_ALIVE may have overridden
  * AI Brain standing down for a fullscreen app, and that it RELEASES what is already loaded

handoff.md START HERE rewritten for the sixth session: what shipped, the behaviour baseline, and the three
mistakes worth not repeating -- judging sprite art from ONE signal when a second (the anchor) contradicts it,
hand-rolling a graph walk instead of using ShimejiEngine.Analyze, and tuning one number for a whole corpus
when the thing being tuned was two things.

No code in this commit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `8b8554fe3`

**fix(selftest): the scratch sweep matched a naming convention, not the garbage**

```
The %TEMP% leak was already MOSTLY fixed: SelfTestScratch swept on the NEXT run (the
PendingModuleRemovals trick, because a collectible AssemblyLoadContext still holds the
DLL when the finally runs) and reported failures instead of swallowing them. 3.2 GB
across 387 dirs had come down to 0.58 GB across 61.

What remained was narrower and more interesting: the sweep matched
`dp-*-selftest-*`, which coupled cleanup to a NAMING CONVENTION -- and the convention
moved. Among the survivors were `dp-petmgr-<guid>` directories, oldest a month old,
whose creating code no longer exists anywhere in the tree. They could never be
collected, because they did not carry the marker their creator never wrote.

The sweep now matches on the `dp-` prefix alone. Age is the only safe question to ask
about a transient scratch directory: nothing this app keeps in %TEMP% uses that prefix
(ModulePaths uses "DesktopPet..."), and only directories are enumerated, so the
per-flag `dp-*-selftest.txt` logs are untouched.

Cleaning happened in flight: every Create() sweeps, so the mutation runs collected all
0.58 GB / 61 dirs including the month-old orphans. Verified 0 remaining.

A NEW ASSERTION covers a root that does NOT follow the current naming. Every existing
scratch assertion built its probe with NameFor(), so all seven passed while this
leaked -- the tests were shaped like the convention they were meant to outlive.
Mutation tested three ways: restore the marker requirement (the leak), delete only
empty directories, and ignore age.

ALSO: the horizontal inset is guarded rather than open. The engine already met a border
with the CHARACTER not the window (ins.Left/ins.Right on both the detection and the
resting position, at both screen edges), so the behaviour was right and nothing tested
it -- which is why it read as an open bug. Measured on the corpus: hand-authored pets
crop tight (0px), while Hornet's walk sits 175px from the left of its 256px cell and
22px from the right, because the compositor sizes one cell to fit the largest pose.
That asymmetry is the trap: correcting one side looks right on one wall and wrong on
the other, and a pet that never walks left would never show it. Now pinned across all
four sites, mutation tested 4 ways.

BACKLOG reconciled against reality, since several entries were stale:
  behaviour debugger        -> DONE (petstudio 1.5.0)
  AI Brain VRAM            -> DONE (aibrain 1.3.0/1.4.0, host 1.9.9)
  pet stuck to the mouse    -> DONE (confirmed by the user)
  %TEMP% scratch leak       -> DONE (this change)
  horizontal inset          -> DONE, guarded
  tray audio recorder       -> mostly SUPERSEDED by Remembrance, which already records
                               both directions; the real gaps are WAV/16 kHz mono
                               (tuned for Whisper, not for listening) and a hotkey
                               rather than a tray click
  Shimeji converter         -> was "IN PROGRESS, no conversion yet"; 31 pets ship

And four findings from today filed as open: a one-frame repeat="0" animation is
invisible (~0.1s), a converted pet's ceiling art can legitimately read as sideways
(with the rotation-AND-anchor lesson recorded so the wrong fix is not attempted
again), the live smoke script has never been walked across six releases, and Pet
Studio's timeline Run button has no coverage.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `76b33e70c`

**test(shimeji): a CONVERTED pet stranding an animation now fails the gate**

```
The verify pass has always counted unreachable animations and never failed on them,
with a comment saying why: "whether hand-authored pets are fully connected is exactly
the open question this pass answers." That question is now answered, so the comment
is stale and the check can be tightened.

Measured across all 53 shipped pets, using the project's OWN analyser:

  all 31 converted pets      0 unreachable
  the 7 hand-authored sheep  2 each

So a converted pet stranding an animation is now a FAILURE, and a hand-authored one
stays reported-only. The split is authorship, not taste: a converted graph is
GENERATED, so an unreachable pose means an emitter change or a migration quietly cut
it off -- while an artist's own graph is theirs, and the standing rule here is not to
rewire hand-authored content.

This is the class of bug a user cannot report except as "I have never seen this
animation play", which is exactly how it came up: asked what links to PoseAction
because it had never been seen. That one turned out fine (3.0% of hub picks, about
once every seven minutes, and it was seen minutes later), but nothing would have
caught it if it had been genuinely stranded.

Provenance-unknown pets are counted and SAID rather than silently exempted, so the
check cannot quietly stop applying to anything.

The failure NAMES the pose. report.Unreachable is a list of ids, and "id 27" tells a
reader nothing about which animation stopped playing; it now prints "WatchLoop (id
10)".

MUTATION TESTED, both directions, because an exemption needs proving as much as a
rule does:

  stranded WatchLoop in a CONVERTED pet   -> exit 1, and named it in the output
  stranded shipb4 in a HAND-AUTHORED pet  -> exit 0, correctly exempt

The first attempt at the hand-authored half removed ZERO edges (it picked an
animation with no inbound edges, so nothing was actually stranded) and "passed"
without testing anything. Rewritten to pick a target with the fewest NON-ZERO inbound
edges. A mutation that cannot change behaviour is not evidence.

WHY THIS EXISTS AT ALL: asked whether other pets needed the same treatment as
Hornet, I wrote my own reachability walk and it reported 944 stranded animations
across 16 pets. The analyser says 14 across 7 -- my walk was missing edge kinds, off
by two orders of magnitude. Two other ad-hoc audits in the same pass were similarly
wrong, both by applying converter-only assumptions ("absence of <gravity> means a
cling") to hand-authored pets, which is a rule this repo already documents as NOT
general. Hence this: one check, using the analyser that is already correct, wired
into the gate so nobody has to hand-roll it again.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `72860b67b`

**chore(catalog): all six modules + app.version 1.9.9**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `41787f559`

**chore(modules): publish aibrain 1.4.0**

### 2026-09-01  `8af0f537d`

**chore(modules): publish blinkingled 1.0.4**

### 2026-09-01  `ea1fdaafa`

**chore(modules): publish remembrance 1.1.2**

### 2026-09-01  `6237841e5`

**chore(modules): publish reminder 1.8.1**

### 2026-09-01  `b990a3754`

**chore(modules): publish petstudio 1.6.5**

### 2026-09-01  `aecce562a`

**chore(modules): publish fortunes 1.2.7**

### 2026-09-01  `a768db5bc`

**chore(modules): bump all five for the ModuleKit refresh (host 1.9.9)**

```
RecordingHost gained the fullscreen test double, and every module bundles ModuleKit,
so the freshness check correctly reported all five payloads behind their source.
Patch bumps with no behaviour change, except petstudio which also picks up the
REVERTED wall/ceiling art swap from the source-linked emitter.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `53223dc3b`

**chore(modules): publish aibrain 1.4.0**

### 2026-09-01  `63f8e4a81`

**feat(1.9.9): a module can ask whether a game is running, and AI Brain stands down**

```
The half of the VRAM request that residency could not cover: "don't load a model at
all while a fullscreen app is running". Residency shortens how long VRAM is held; it
does nothing about a load that happens mid-game.

ABI ADDITION (hence the product bump; DesktopPet.Contracts stays frozen at
AssemblyVersion 1.0.0.0):

  IHost.IsFullscreenActive     true while a FULLSCREEN window exists on ANY monitor
  IHost.FullscreenChanged      raised when that flips, with the new value

ANY monitor, foreground or not, deliberately. An alt-tabbed game still owns its VRAM
and its swap chain, so the thing to avoid disturbing is still there when the game is
not focused. Maximised windows do not count: they leave the taskbar visible and are
ordinary windows.

No second detector. This exposes FullscreenScan -- the same scan that already keeps
pets off a fullscreen game, and the one with --fullscreen-selftest behind it --
rather than reimplementing the policy in a module. The host-level answer is fed by
the scan the pets ALREADY run every 300ms, so it costs one array walk rather than a
new timer; with no pets on screen nobody is scanning, so the predicate falls back to
an on-demand scan instead of trusting an arbitrarily old cached "no game running".

An EVENT as well as a predicate, because the reaction is "release the VRAM you are
holding" and a module that only checks at its own next tick could be fifteen minutes
late -- by which point the game has already failed to get the memory.

AI BRAIN 1.4.0 uses it: while a fullscreen app is running it declines the drop and
the poke, so the responder chain falls through to Fortunes and the pet still says
something, just something free. Declining rather than going silent is the whole
reason this is cheap -- the fallback already existed.

AND IT RELEASES WHAT IS ALREADY RESIDENT, which my own first spec missed and the
user supplied: a model loaded BEFORE the game started is not helped by declining to
load. It releases on the transition (the only moment VRAM can be handed back before
the game needs it) and again in the guard, which is not redundant -- if the app
starts while a game is already fullscreen no transition ever fires, and the first
drop is the only chance.

ON by default. The cost of being wrong is a fortune instead of a quip; the cost the
other way is a game crashing. And while a game is fullscreen the pet is hidden
anyway, so a model answer would not even be seen.

THE COST OF AN IHost ADDITION, paid in full: seven test doubles implement IHost and
all seven needed the new members, including ModuleKit's RecordingHost, which ships
to module authors and now offers RaiseFullscreenChanged so a module can prove it
releases what it holds when a game appears. The Contracts comment on IsCompanionAlive
already warned that IHost is implemented by hosts and their fakes; that is exactly
what this cost.

MUTATION TESTED, nine mutations, all firing, restored green -- but THREE were silent
first, and two of those for the same instructive reason:

  drop stops checking fullscreen          -> was SILENT
  guard stops releasing what is resident   -> was SILENT

Both because FullscreenBlocked and ReleaseModelForFullscreen are DEFINED immediately
after their callers, so a distance-bounded regex matched the definition and passed
with the call deleted. That is precisely the trap this repo's own rule names: a
source-text check must assert the CONDITION or the ORDER, not that the guarded
statement exists somewhere nearby. Fixed by slicing each method's OWN body (a
Get-MethodBody helper bounded by the next member declaration) and asserting the call
is inside it. The third silent one was the settings default, which no payload
assertion could see.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `a70cf90d7`

**Revert "fix(shimeji): un-swap wall/ceiling art, so a pet on the ceiling is not sideways"**

```
This reverts d34ae88. The change was WRONG and it introduced a placement bug.

I concluded the source skin had its wall and ceiling art mislabelled, from one
signal: the "Ceiling" art is rotated 90 degrees and the "Wall" art is upright,
which is backwards from how a character on a vertical surface should be drawn. I
never checked the second signal, and it is decisive:

  frames 91-94 (rotated)   content rows  0-32   flush to the cell TOP
  frames 95-98 (upright)   content rows 20-91   flush to the cell BOTTOM

The compositor bakes anchor alignment into pixels, and it anchors a CEILING pose to
the cell top (it hangs down) and a floor/wall pose to the cell bottom (it stands
up). So 91-94 are genuinely the ceiling -- rotated AND top-anchored -- and 95-98
are genuinely the wall. The skin was labelled correctly all along; the art simply
looks odd, because this artist drew a ceiling cling as a body lying flat against
the ceiling rather than upside down.

Swapping the frame INDICES moved the art without moving its baked-in alignment, so
ClimbWall drew top-anchored art in a cell whose bottom is where her feet go --
leaving her floating with 60px of empty cell below her and clipped. Reported as
"part of the animation is cut-off", by the user, who also read the pairs the right
way round from the Pet Studio preview before I did.

What this means for the original report ("standing sideways in the air"): it was
NOT a bug in the mapping. It is the skin's own ceiling art, used correctly, on a
pet that had only just become able to reach a ceiling. Whether that art is worth
showing is a taste question and stays open; it is not a defect to fix by moving
pixels between regions.

Nobody received the broken version: the only installed copy was still at format
1.6, which this restores byte-for-byte.

The lesson, recorded because it is the second time this session that one signal
looked sufficient: a sprite's ROTATION says which surface it was drawn for, and its
ANCHOR says the same thing independently. When only one was consulted, it was
possible to be confidently wrong. Checking both is the cheap version of what the
user did by eye.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `dc5a10ed0`

**chore(catalog): aibrain 1.3.0 (model residency)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `564ecb05d`

**chore(modules): publish aibrain 1.3.0**

### 2026-09-01  `238d4ba08`

**feat(aibrain): one "Model residency" choice, defaulting to giving the VRAM back**

```
Requested: a way to stop a local model sitting in VRAM between remarks, because on
a gaming machine that is a crash risk rather than a cost.

WHAT WAS THERE: the quip path sent no keep_alive at all, so Ollama's own policy
applied and the model stayed resident after every remark. Separately, "Preload
model on launch" was ON by default and pinned keep_alive to 10 minutes.

THE FIRST ATTEMPT WAS TWO SETTINGS AND IT WAS CONFUSING, correctly called out on
review. An eject window plus a preload switch can contradict each other -- a warmed
model outlives a short window until the next remark re-stamps it -- so the pane had
to carry a paragraph explaining the interaction, and the suggestion was to grey one
out. Greying out would have needed a SettingField enable-condition (an ABI change)
to paper over a bad shape. Merged into ONE choice instead, which cannot disagree
with itself, needs no conditional UI, and deletes the explanation:

  Model residency:  Unload after each remark (frees VRAM)      <- default
                    Keep loaded while the app runs (fastest)
                    Leave it to Ollama

Each maps to a DISTINCT wire value, and that distinction fixed a latent bug in my
own first version: I had used -1 as the "send nothing" sentinel, but -1 is a real
instruction to Ollama meaning "stay resident indefinitely" -- the exact opposite of
what the sentinel meant. The field is now nullable: null omits it, 0 evicts on
answer, negative keeps it. "keep" also owns the launch warm-up, since warming a
model and then evicting it after one remark is work done to be thrown away.

DEFAULT IS "UNLOAD", not the old behaviour. The first version defaulted to "leave
it to the server" to avoid changing anything on upgrade; that reasoning was weak
here and was challenged: there are no other users, and the project's own rule says
under 10 stars make the clean change rather than a compat story. This module holds
VRAM only for a remark it has already made, so holding after the answer is the
thing there was never a reason for.

THE PANE STATES FACT, NOT A DOCUMENTED DEFAULT. The request was "the text should say
whatever the default is". Ollama documents 5 minutes (not 30), but OLLAMA_KEEP_ALIVE
overrides it machine-wide, so printing it would be wrong on exactly the machines
whose owner had tuned it. Instead GET /api/ps reports what is resident right now:
model, GB held, seconds until eviction. "Nothing resident" is the expected reading
under the new default, which is itself the confirmation the setting works.

MUTATION TESTED, eight mutations, all firing, restored green:

  keep_alive never reaches the request        -> payload assertions
  default flipped to "keep"                   -> stored-default assertion
  "keep" collapsed onto omit                  -> distinct-wire-value assertion
  "unload" stopped meaning 0                  -> same
  every choice asks for a warm-up             -> warm-up mapping assertion
  unknown stored value pins resident          -> unknown-value fallback assertion
  setting stops reaching the client           -> propagation assertion
  pane label stored verbatim                  -> label round-trip assertion

THREE of those guards did not exist until mutation testing showed the mutation was
SILENT: the stored default, the settings-to-client propagation, and the label
round-trip. All three are seams where a correct value is only correct if something
carries it -- so BuildLocalBackend and the label helpers became internal to be
testable, which is cheaper than shipping a value nobody proves arrives.

STILL NOT BUILT, and it is the half that matters most for the stated motive: "do
not load a model while a fullscreen app is running". Residency shortens how long
VRAM is held; it does not stop a model loading mid-game. Spec sharpened on review
and now includes a step my own plan missed -- EVICT what is already resident on
detection, not merely decline to load, because a game launching next to a resident
model is not helped by "do not load". Needs IHost to expose a fullscreen predicate
(the detector and the fortune fallback both already exist), so it waits for a host
release rather than pushing a second MSI mid-smoke-test.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `21c2f98ab`

**chore(catalog): reminder 1.8.0, petstudio 1.6.4, app.version 1.9.8**

```
app.version is what the new launch update check compares against, generated from
ProductVersion.props so it cannot drift from the build.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `d2980d553`

**chore(modules): publish petstudio 1.6.4**

### 2026-09-01  `3360eea42`

**chore(petstudio): 1.6.4 for the source-linked art un-swap**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `bdedb0cf7`

**chore(modules): publish reminder 1.8.0**

### 2026-09-01  `d34ae888c`

**fix(shimeji): un-swap wall/ceiling art, so a pet on the ceiling is not sideways**

```
Reported: Hornet "started standing sideways in the air", near the ceiling, over
VSCode.

Not a converter bug and not a host bug -- the SOURCE SKIN is mislabelled, and the
converter reproduced the labels faithfully:

  shime12/13/14   skin says "Wall"      art is UPRIGHT (0 degrees)
  shime23/24/25   skin says "Ceiling"   art is ROTATED 90 degrees

A wall pose wants 90 degrees; a ceiling pose wants 180. Each of this skin's sets
is one rotation short, so its "wall" art is really floor art and its "ceiling" art
is really wall art. Nothing was drawn upside down: the skin has no true ceiling art
at all. The original Shimeji shows the same thing.

It only became visible because the ceiling only recently became REACHABLE. Before
the reclimb fix, 47 simulated hours produced 215 wall entries and zero ceiling
visits, so nobody had ever seen these frames play. Two things were wrong at once,
not one: on the ceiling she played 90-degree art (floating sideways) and on a wall
she played upright art (sliding up bolt upright).

The test is GEOMETRIC, not a name and not a per-skin exception: a pose drawn for a
vertical surface is wider than it is tall, because that is what rotating a standing
character 90 degrees does. When the wall art is portrait AND the ceiling art is
landscape, the two sets are swapped, and exchanging them is strictly better on both
surfaces -- the wall gets correctly rotated art, and the ceiling gets upright art,
which reads as a pet standing on a ledge rather than floating on its side.

Only FRAMES move. Names, regions, weights, velocities and the graph stay exactly
where they were, because the defect is which pixels a region points at.

Deliberately conservative, and the corpus proves it matters: of 31 converted pets,
16 were inverted and 15 were already correct and left untouched.

Shipped both ways so new imports and existing pets agree:
  * the emitter checks at conversion time (measures the source pose PNGs)
  * a new `resurface` migration fixes already-shipped pets, measuring orientation
    from the sprite sheet embedded in each pet's own XML -- no source skins needed,
    same as reclimb and restsplit. Header 1.6 -> 1.7, idempotent.

VERIFIED THE STRONGEST WAY AVAILABLE: the migrated Hornet is now byte-identical in
wall/ceiling frame assignment to a fresh from-source conversion with the emitter
rule active (both GrabWall 91,92 / ClimbWall 91-94 / GrabCeiling 95,96 /
ClimbCeiling 95-98).

A REAL BUG CAUGHT BY CHECKING THE RESULT INSTEAD OF THE LOG. The first run reported
"1 pair swapped" and looked fine; inspecting the output showed ClimbCeiling had
been given frame 116 -- which belongs to Grapple4, her JUMP. A jump is also
gravity-less and also travels upward, so "no gravity and moves vertically" selects
the jump, not the climb. Only a cling is unreachable from the hub, which is the
whole reason the wall region is a separate set, and the other migrations already
exclude hub-selectable animations. Adding that exclusion raised detection from 7
pets to 16 and made the output match the from-source conversion.

Corpus safety check across all 49 pets with a cling region: the multiset of frames
used by cling animations is unchanged (they only move between animations), no index
falls outside the sheet, and no pet's cling animation count changed.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-09-01  `7e44ede90`

**feat(1.9.8): one pet speaks for the app, a per-calendar reminder pet, a launch update check**

```
Four requests, one shared foundation: WHICH pet speaks.

1. SayAll REACHES ONE PET, NOT ALL OF THEM.

Reported as "if a bubble is going to fire and all pets receive it, fall back to
only the default pet". The ABI comment on SayAll already said this in as many
words -- "with several pets on screen it makes all of them say the same line at
the same instant, which reads as a bug, because it mostly was one" -- so the fix
matches the contract's own stated intent.

One site: ShowBubbleOnAll stopped iterating PersistentPets() and now resolves
DefaultSpeaker(). Every module benefits with no change of its own (Fortunes,
AiBrain, Reminder). Messages that genuinely belong to a pet already went through
Say(pet, ...); this is the other case.

The choice names a pet TYPE and falls back to the oldest pet on screen, because a
type can leave the mix long after it was chosen and a reminder the user asked for
must not be swallowed by a pet that happens not to be out.

2. "PET THAT SPEAKS FOR THE APP" in Preferences, offered only from the pets
actually on screen. Stored as a type id so a catalog rename cannot invalidate it;
an unrecognised choice shows the default label WITHOUT clearing the stored id, so
removing and re-adding a pet round-trips the preference.

3. REMINDER 1.8.0: a per-calendar "Reminder pet", so each calendar can be
announced by a different pet. No ABI change was needed -- IHost.Say(pet, ...) and
IsCompanionAlive have existed since 1.5.0 -- so this needed no MinHostVersion bump
either. The module tracks pets via CompanionSpawned, never prunes (there is no
CompanionRemoved event, which is exactly why IsCompanionAlive exists), and asks about liveness
at the moment of speaking. Gains the Pets permission, which it now uses.

4. LAUNCH UPDATE CHECK. Once every 24h, notify-only, switchable off -- the same
contract as the existing monthly module check, and stated on the label for the
same reason: it reaches the network without being asked. The Preferences footer
becomes "1.9.7 -> 1.9.8", clickable to the releases page, rendered from the CACHED
answer so opening Preferences never waits on a request and works offline.

Read from catalog.json rather than the GitHub releases API: that file is already
fetched and already TLS-pinned to the repo, the API rate-limits unauthenticated
callers to 60/hour per NAT, and a catalog miss degrades to "no answer" instead of
an error to explain. app.version is generated from ProductVersion.props so it
cannot drift from the build.

Note the ordering nobody can fix: 1.9.7 has no checker, so the first version this
can ever report is 1.9.9.

TWO REQUESTS TURNED OUT TO NEED NOTHING BUILT, found by checking rather than
guessing:

  * "raise the 3-pet cap to 5" -- there is no 3-cap. MAX_SHEEPS is 16 and always
    was; the tray gates on it and the Pets pane has no per-type ceiling. The "3"
    was the user's own saved pet mix. What DID mislead is the "Pets at startup"
    label, which claims authority it does not have: BuildStartupSpawnPlan uses the
    saved mix whenever one exists and only falls back to that count. Relabelled
    rather than given a second control that would fight the mix.
  * a new ICompanionManager.OnScreenPets() for liveness -- IHost.IsCompanionAlive already
    does it. An ABI addition was written and then deleted.

MUTATION TESTED, seven mutations, all firing, restored green:

  SayAll fans out again              -> an unaddressed message is broadcast
  DefaultSpeaker walks sheeps[]      -> previews speak
  Schema read before Load            -> live dropdowns freeze
  speaker options not refreshed      -> same
  version compare -> string ordering -> 1.9.10 ranks below 1.9.9
  once-a-day throttle removed        -> every launch hits the network
  garbled catalog value trusted      -> phantom update offered

The Load()-before-Schema ORDER now has its own invariant. Two dropdowns depend on
it and reversing those two lines throws nothing, fails no build, and silently
freezes both at their construction-time list -- pets added since would simply
never appear. It asserts the order, not the presence of either statement, because
both statements survive the reordering that breaks it.

18 new pure assertions in --hardening-selftest cover IsNewer / ShouldCheck /
FooterText / ParseAppVersion, including 1.9.10 vs 1.9.9 (which string comparison
gets backwards and this project is about to hit) and a clock that jumped backwards.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `cb32f30c1`

**chore(catalog): petstudio 1.6.3 (role-split rest dwell)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `33faf1f52`

**chore(modules): publish petstudio 1.6.3**

### 2026-08-31  `9cf73fa0a`

**fix(shimeji): split the rest dwell by role -- brief hub, lingering performances**

```
Reported: after the last rest change, Sprawl (and the dangle-legs/butterflies and
eat-berry idles) lasted about a second; the user wants those to play 9-12s. The
previous pass over-corrected: it shortened EVERY rest to ~1.2s, which fixed the
sluggish hub but also cut the performances worth watching.

The resolution is that a rest is not one thing. It is two, wanting opposite
lengths, and timing them the same is what was wrong in BOTH directions:

  * the HUB (Stand) is the pose the pet returns to between actions. Held long it
    reads as standing around -- it was 9.6s and 67% of every cycle, the original
    "doesn't do anything" report. It must be BRIEF (~2s).
  * a PERFORMANCE (Sprawl, dangle-legs-with-butterflies, eat-a-berry) is a thing
    the pet is DOING. Cut to 1.2s it flashes by. It must LINGER (9-12s).

So the dwell is split by role: HubDwellMs = 2000 for the hub (the most-connected
node), RestDwellMs = 11000 for every other idle, reached by repeating the pose
with its per-frame interval still capped (so a 3000ms baked-in hold cannot freeze
a frame). This reconciles both of the user's complaints -- the hub no longer
loiters, and the performances are long enough to enjoy.

Per-pose on Hornet after the migration:

  Stand (hub)          1.8s   brief
  Sprawl              11.2s   the "sleep" the user watched
  PetActionDangleLegs 11.8s   the butterflies
  EatBerryAction       9.3s
  Sit                 10.8s
  BePet               11.0s

Shipped by a new `restsplit` migration (1.5 -> 1.6), 31 pets, 489 performances
lengthened, hubs kept brief, idempotent. It supersedes the over-correction of
`restdwell`; the 1.5 intermediate is not a state any pet should stay in.

MUTATION TESTED, the four load-bearing guards, restored green:

  hub given the long dwell      -> hub 'Stand' holds 9000ms, over the brief-hub ceiling
  performances given the brief  -> performance holds only 900ms, must linger 9-12s
  performance dwell -> 1.2s     -> same
  hub dwell -> 9s               -> same

The self-test now asserts the SPLIT on the whole graph: the hub is under a
brief-hub ceiling and every other hub-selectable idle rest lingers 9-12s, with
Stand (single-frame hub) and Lounge (multi-frame non-hub performance, 3000ms hold
baked in) as the two named fixtures.

Pet Studio 1.6.3 for the source-linked emitter. No host change.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `7337e6963`

**chore(catalog): petstudio 1.6.2 (short rest dwell)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `24da15eea`

**chore(modules): publish petstudio 1.6.2**

### 2026-08-31  `8fbc188ff`

**fix(shimeji): a rest lasts ~1.2s, not ~9s, so a pet stops standing idle 79% of the time**

```
Reported: the pet doesn't spend enough time doing anything. Measured over 85
simulated hours, Hornet stood idle 79% of the time, walked 16%, and did the
interesting stuff (jump+wall+ceiling) 4%. The user's read was directionally right
but the label was off -- she barely walks; she STANDS.

WHY, and the answer is the same shape as the jump and the climb. Compared the
converted pet to the hand-authored yellow_sheep, the reference for a good pet:

              hub dwell   idle-pose dwell   pick mix (idle/walk/jump)
  sheep         0.5s          ~0.7s              50 / 45 / 5
  Hornet        9.6s          5.5s               64 / 33 / 3

The WEIGHTS are close (both idle-leaning, faithful to the source). The DWELL was
not: the sheep holds a rest ~0.7s and flicks to the next thing; Hornet held each
rest 5.5s and its hub 9.6s. It is lively because it CYCLES FAST, not because it
does more active things per pick. TargetRestMs = 9000 was invented here, not
measured; RestTargetMs then took MAX(authored, 9000) so a single-frame pose
authored Duration=250 held 10s. Third invented-constant-vs-measured-reference bug
this session (after the jump arc and the climb reach).

Fix: a rest is a short fixed dwell (RestDwellMs = 1200, from the sheep's
0.7-1.4s), with each frame's interval capped (MaxRestIntervalMs = 700) so a "hold
for 3 seconds" baked into a source interval (Stand's 3000ms first frame) is
trimmed to the reference feel while a breathing cycle's real pacing (100-300ms) is
untouched. Weights deliberately LEFT ALONE -- the diagnosis was that they were
never the problem.

Result, measured on the migrated pet:

              idle   walk   wall   ceiling   active
  before      79%    16%     2%      2%       21%
  after       42%    45%     7%      4%       57%

Right on the sheep's character (idle 50 / walk 45), a touch livelier.

Shipped by a new `restdwell` migration, header 1.4 -> 1.5, 31 pets, 353 rests
shortened, idempotent.

KNOWN LIMITATION, stated because it is real: the source's Stay/Animate flag is
gone in the emitted XML, and NO emitted-form proxy recovers it -- distinct-frame
count, repeat, dwell and velocity all leak both ways (Sit has 4 distinct frames,
Sprawl has 21; bounce is a 2-frame performance). So the migration also trims the
long-held frames of a few idle PERFORMANCES (sleeps 36s->8s, Divide/Transform
tightened). This is acceptable and was chosen over the pristine alternative (a
from-source re-conversion, which has the flag) because: it serves the stated goal
(a snappier pet), it breaks nothing (breeding is Group3, dropped, so no
child-spawn timing exists to disturb), it only ever touches TIMING, it preserves
Hornet's hand-edit and every sprite sheet exactly, and it is reversible. A
re-conversion would preserve performance pacing but regenerate 31 sheets, icons
and blurbs and wipe the hand-edit. If the corpus ever grows performances whose
pacing matters, re-convert those from source.

MUTATION TESTED, the three load-bearing emitter guards, restored green:

  rest dwell back to 9000ms          -> single-frame rest holds >2600ms
  per-frame interval cap removed     -> multi-frame rest keeps an interval over cap
  multi-frame start interval uncapped-> same

A FOURTH mutation was silent and that was the useful result: clamping inside a
separate pass-estimate helper was redundant with the independent playback-interval
cap, so no reachable input could make its absence produce a bad dwell. Per the
standing gate (don't ship a guard nothing can fail), the helper was deleted and
the estimate folded inline.

The self-test gained a multi-frame rest fixture (Lounge, a 3000ms held first
frame like Stand's) plus a per-pet ceiling check, so a future rest cannot quietly
reintroduce a long hold.

Pet Studio 1.6.2 for the source-linked emitter. No host change.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `753ed504d`

**chore(catalog): petstudio 1.6.1 (surface reach budget)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `0c860dc12`

**chore(modules): publish petstudio 1.6.1**

### 2026-08-31  `a9f3e0dc2`

**fix(shimeji): a wall climb crosses the wall, so the ceiling is reachable at all**

```
The ceiling region shipped in v1.9.4 and no converted pet had ever touched it.
Measured before touching anything: 1 in 203,000 wall entries, about one visit per
FIVE YEARS of continuous running. A 47-hour simulation gave 215 wall entries, zero
ceiling visits, median climb 62px, and one lucky run that stalled 12px short.

THE REFERENCE IS NOT WHAT IT LOOKS LIKE, and reading it wrong would have produced
the wrong fix. yellow_sheep's climbs are not fast: walk_up rises 2px per step at
150ms, which is 13px/s -- SLOWER per pixel than Hornet's converted climb. What it
does differently is REPEAT ~20000 times, so one sequence covers the whole wall and
the "keep climbing / let go" roll never happens mid-climb. roll_up (8008 steps) and
chasew2 (3003) are the same trick. My first instinct was "make the climb faster",
which would have left the 34%-per-pass let-go intact and fixed nothing.

The converted pets budgeted the sequence by TIME (TargetWallMs = 5s), so Hornet's
32-frame climb ran once, covered 32px, and then rolled a 34% chance of letting go
-- every 12.8 seconds. Reaching a 940px screen top needed 30 consecutive
survivals.

So the observable property is the REACH: one pass must be able to cross any screen.
Constant speed (6px per 100ms = 60px/s, the middle of the sheep's own 13-100px/s
band), a flat interval, and enough repeats for 4000px. The surplus repeats cost
nothing because the pet stops at the border long before the sequence ends, which is
exactly why the sheep can afford 20000 of them.

Constant rather than the source's ramp for the reason BuildFall already documents:
the sequence self-loops, so a ramp snaps back to the slow start speed on every loop
and visibly pulses. Hornet's 0 -> -2 ramp also halved its average speed to 1px per
step, which is most of where 2.5px/s came from.

Applied to CROSSING poses only. A static grab keeps its time budget: a hold is
meant to end and let the pet re-decide, and a 4000px hold would pin it to the wall
for a minute doing nothing. And to ANY vertical motion, not just upward -- a
descending pose is how a pet climbs back down, and direction is preserved or every
descent would silently become a climb.

The same treatment on the ceiling, for a different reason: a ceiling walk that
stops every 32px never reaches a corner, so it never finds the only="vertical" edge
that would take it back down a wall, and its only exit is to drop.

MEASURED AFTER, and it does not over-correct -- it is the opposite:

                  floor    wall   ceiling   ceiling visits
  before (1.3)    94.0%    6.0%     0.0%    never
  after  (1.4)    96.4%    2.2%     1.3%    one every 19 min

Wall time HALVED, because the wall stops being a place the pet dithers: it now
crosses in 15.7s instead of inching and re-rolling. Median climb went 62px -> 940px
(the full screen). Floor time went up.

Shipped by a new `reclimb` migration (numbers only, no source skins), header format
1.3 -> 1.4, 31 pets, 81 crossing poses retimed, 78 holds left alone, idempotent.
Verified across the corpus: 0 poses under 2000px reach, 0 outside 50-70px/s, and
all 18 descending poses still descend.

MUTATION TESTED, six mutations, all firing, restored green:

  old time budget restored     -> one climb pass covers only Npx
  reach given to every pose    -> a static wall grab was given the travel budget
  direction inverted           -> the descending wall pose does not travel DOWN
  source velocity ramp kept    -> the climb's vertical speed ramps
  source interval ramp kept    -> the climb's interval ramps
  ceiling left on time budget  -> one ceiling pass covers only 84px

TWO were silent first time, both because the FIXTURE was flat: its ClimbWall had
velocity 0,-2 on both poses and equal durations, so no ramp existed to preserve and
the two constant-speed guards could not fail. The fixture now ramps 0 -> -2 at
16 -> 4 ticks, mirroring Hornet's real shape. Third time this session a fixture has
been the thing hiding a guard.

No host change: the engine already handles all of this. Pet Studio 1.6.1 for the
source-linked emitter.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `216f03a5a`

**chore(catalog): petstudio 1.6.0 (capability badges)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `6e4517f81`

**chore(modules): publish petstudio 1.6.0**

### 2026-08-31  `516ebcb33`

**feat(petstudio): the map says what each animation DOES, not just its name**

```
Asked "where is jump?" while looking at Hornet's 31 chips. The answer was
#22 Grapple4, and there was no way to know that from the map: names belong to the
SOURCE skin, so a Hollow Knight pet calls its leap "Grapple4", and across the
corpus a jump is variously jump_up_left, jumping, PullUpShimeji2, Launching,
Lay an Egg2 and 引っこ抜く2. The map showed a name and a reachability colour, so
the one question a pet author actually asks was unanswerable without a script.

Every chip now carries a capability badge (JUMP / CLIMB / CLING / MOVE / GAZE /
ENGINE), the legend gained a census, and the detail panel states the physics in
prose. Idle carries NO badge on purpose: it is 17 of Hornet's 31, and badging it
would bury the handful that matter. Verified on the real installed pet -- all 31
correct, including GrabWall/ClimbWall as CLING/CLIMB rather than jumps.

THE FIRST VERSION WAS WRONG, AND THE FIXTURE CAUGHT IT. I read "absence of
<gravity> IS the cling" out of the emitter and applied it as a general rule. It is
not: it is how the CONVERTER expresses a cling. The bundled hand-authored pet has
4 gravity elements across 54 animations, so the rule labelled 41 of its ordinary
floor animations as wall poses -- census came back Cling=24, Climb=17, Idle=0, and
the assertion "not everything is badged, so a badge still means something" failed.

The real signal is how an animation is REACHED. eSheep marks its 7 surface poses
by the border edge that puts the pet there (6 only="vertical", 1 only="horizontal"),
which is also what a converted pet does. So the classifier is graph-aware: seed the
surface set from border edges carrying a surface only= flag, then grow through what
those chain to. Growth is needed rather than speculative -- a ceiling walk is
reached from the ceiling GRAB, never from a border -- and is bounded three ways:
the target must have no gravity (something that can fall is not holding on), must
not be one of the engine's own names (`fall` is where every wall pose exits to, and
without the exclusion it drags the whole floor in behind it), and the edge must
have a non-zero probability.

That also settles a pair that velocity cannot: a jump and a wall climb both rise,
and in a converted pet both omit gravity. Only the graph knows which one the pet
was put on a wall for.

MUTATION TESTED, nine mutations, all firing, restored green:

  cling by missing gravity alone (v1)     -> the bundled pet's census collapses
  wall seed flag dropped                  -> a wall climb reads as a jump
  floor counted as a surface              -> a landing reads as clinging
  propagation removed                     -> a ceiling walk is missed
  Holdable drops the gravity test         -> the floor leaks into the surface set
  Holdable drops the engine exclusion     -> `fall` drags the floor in
  zero-probability edge confers capability-> an unreachable edge grants a badge
  engine names stop winning               -> `fall` reported as a behaviour
  Idle gains a badge                      -> a badge stops meaning anything

FOUR of those nine were SILENT on the first run, and that is the useful part:

  * Three were weak assertions. The bundled pet's wall poses happen not to exercise
    the growth bounds, so the census barely moved and the coarse "not everything is
    badged" check still passed. Each bound now has a direct unit case built from
    hand-made nodes: a taskbar-reached animation is not a surface pose, a
    gravity-having animation reached FROM one is not either, and `fall` does not
    join the set nor pass it on.
  * One was a bad mutation of mine. I replaced `if (!grew) break;` with `break;`,
    which still performs one hop -- and the fixture only needed one hop, so it
    passed legitimately. Fixed on both sides: the mutation now removes the loop
    entirely, and a second fixture chains TWO hops from the seed.

Pet Studio 1.5.0 -> 1.6.0. No host change.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `ed9126445`

**fix(pets): the check button now says it finds updates too**

```
It was labelled "Check for new pets" while also being the only route to the new
update list. The pane never touches the network on open, so that label left the
whole feature undiscoverable: a user with a stale pet would have had no reason to
press the one button that would tell them.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `7a94de0b2`

**feat(pets): a corrected pet can actually reach someone who already has it**

```
Asked how an existing user gets a corrected pet, and the answer was that they did
not. The Pets pane diffed the catalog BY ID ALONE:

    foreach (CatalogCompanion pet in _lastCatalog.Pets)
        if (!local.Contains(pet.Id)) result.Add(pet);

so a pet already installed was filtered out of "available to download" however
much its content had changed, and the pane reported "you already have every
available pet". This morning's jump fix to 31 pets would have reached new
downloads only. Nothing in any gate objected, because nothing was broken: the
feature simply did not exist.

FIXED BY HASHING, NOT BY ADDING A VERSION FIELD. A pet entry has no version, and
adding one would mean maintaining a number by hand for 53 pets that is silently
wrong the first time someone forgets. The catalog already records the SHA-256 of
the exact bytes it serves, and the installer writes those bytes verbatim
(DownloadVerifiedAsync verifies the hash, WriteAllBytesAtomic writes the same
array), so the installed file's hash IS the comparison.

Verified against the live catalog BEFORE writing any of it, because line endings
were the obvious worry: raw.githubusercontent serves the committed git blob and
New-ContentCatalog.ps1 hashes that same blob, so they agree byte for byte
(a33b40e9... both sides, 426526 bytes). Do NOT check this by hashing the
working-tree file: a checkout is CRLF, git stores LF, and the mismatch looks like
a bug in the comparison rather than in the test.

A `catalog.sha256` stamp beside each pet records the hash AS INSTALLED, which is
what separates "the catalog moved on" (update, no prompt) from "you edited this"
(warn, then replace). An absent stamp is deliberately NOT assumed safe:

  installed == catalog                      -> UpToDate
  installed == stamp, != catalog            -> UpdateAvailable   (silent)
  installed != stamp, != catalog            -> LocallyModified   (warns)
  no stamp,  != catalog                     -> UnknownProvenance (warns)
  not in the catalog                        -> UpToDate, left alone

Consequence to expect: EVERY PET INSTALLED BEFORE THIS WARNS ONCE, because
nothing recorded what was installed. Backfilling the stamp from the current file
was considered and rejected -- it would assert the file is unmodified, which is
precisely the thing the stamp exists to avoid guessing at.

END-TO-END TESTED against the live catalog and the real pre-fix Hornet file (the
one that was on this machine this morning), driving the shipped CompanionProvenance by
reflection. All four scenarios correct: a pre-today install -> UpdateAvailable,
silent; the same plus a hand edit -> LocallyModified, warns; no stamp ->
UnknownProvenance, warns; a fresh download -> UpToDate, not offered.

MUTATION TESTED, ten mutations, each caught, restored green. The split held again:

  absent stamp treated as a clean update  -> unit table
  a changed pet reported up to date       -> unit table  (this IS the shipped bug)
  a non-catalog pet reported stale        -> unit table
  hash normalisation dropped              -> unit table
  never warn before overwriting           -> unit table
  a differing state dropped from offered  -> unit table
  a warned state stops saying "replaces"  -> unit table
  pane never stamps what it installed     -> source invariant
  pane never diffs for staleness          -> source invariant
  pane computes the list, never renders it-> source invariant

The unit table caught all seven classifier/policy mutations and NONE of the three
pane-wiring ones; the invariant caught all three and none of the seven. Third
time in this session that split has been load-bearing.

One check I wrote and then replaced: banning the string "you already have every
available pet" from the pane. It failed on my own comments explaining the bug,
and a check that forbids DESCRIBING a bug is a check that gets deleted. Replaced
with an assertion that the status line is derived from the stale count.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `003573964`

**docs: write the versioning scheme down**

```
Asked what the pattern was, and the honest answer was that it was consistent in
practice but documented nowhere, so it had to be read back out of the source.

Three independent numbers: the host product version (ProductVersion.props, drives
the tag and the MSI), each module ModuleInfo.Version (independent, and the reason
it usually moves is picking up a source-linked change rather than the module own
code), and MinHostVersion (bump only when the module actually calls a new ABI
member, or a module stops working on hosts that could have run it).

Also records the two things that are NOT product versions and have bitten before:
Contracts AssemblyVersion is frozen at 1.0.0.0 because it is the ABI binding
identity, while its FileVersion must track the product version or the installer
skips refreshing it; and module assemblies carry no version at all, so the number
in the Modules pane exists only as a string in code.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `01617e928`

**chore(release): bump ProductVersion to 1.9.6**

```
The window-grip fix is engine code, so it needs a tag to reach anyone. Nothing
else in this session did: pets, packs and modules are all served off master via
raw.githubusercontent, so merging IS their publish.

No ABI edit in this release (DesktopPet.Contracts is untouched), so the
Contracts.dll FileVersion row of the smoke script does not apply.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `7052a8eac`

**fix(host): a pet hanging under a window could never let go**

```
Reported from live use: Hornet stuck in the falling animation, in place, going
nowhere. Reproduced by reading, and it is a v1.9.5 bug (Phase E, the window
underside) that yesterday's jump change made easy to hit rather than rare.

The mechanism, and it is structural rather than a missed case:

* ReleaseWindowGrip implements "let go" BY playing the fall animation. Nothing
  did the inverse, so a pet that reached `fall` through its own <next> edge kept
  the grip.
* The underside branch of NextStep pins the pet's y to 0 -- the pin IS the follow
  for a WindowGrip.Bottom -- and BOTH of its release conditions test y
  (`y > 0 && ... > gripRect.Bottom` and `y < 0 && ... < gripRect.Top`). With y
  zeroed, neither can ever fire.
* So a pet under a window that entered fall was pinned there for good, playing
  the falling animation on the spot.

And the ceiling poses offer that edge on every pass: GrabCeiling and ClimbCeiling
both carry `next probability="25" -> fall` out of 105, so 24% per pass. The only
escape is ClimbCeiling crawling to the window's left or right corner, which
covers 32px per 12.8s pass against a 24% chance of wedging each time. Replaying
the emitted weights: reaching a corner from mid-window (~400px, 12.5 passes) is
0.656^12, so about 99% of window-underside grabs ended in a permanent wedge. The
feature shipped as a pet trap.

A SIDE grip (Left/Right) does not have this problem and must not be "fixed": that
branch leaves y alone, so it self-heals through its own gripRect.Bottom test, and
climbing DOWN a window's side is the entire point of Phase D.

Fix is a pure predicate plus one call site. GripMustRelease says a grip cannot
survive (a) entering fall, from anywhere, because that is what letting go means,
or (b) for an UNDERSIDE grip, any animation with vertical velocity at all -- a
pose the pet can legitimately hang in has vy=0, which the converter's own
self-test already asserts, so that is exactly the set which must let go. Clearing
hwndWindow rather than windowGrip is deliberate: the property setter clears the
grip and the follow tracker together, which is why ReleaseWindowGrip exists as
one place.

Why the jump change surfaced it: Grapple4 used to rise 15px, so a window's bottom
edge was almost never in reach. It now rises 46px, roughly tripling the reach, and
the underside grab went from theoretical to routine.

TWO checks, because they fail for different reasons and neither is sufficient:
GripMustRelease has its own assertions in --hardening-selftest, and a new
source-text invariant asserts SetNewAnimationCore actually CALLS it, wired to the
fall animation and to clearing the handle. A correct predicate nobody invokes is
the exact failure the standing rule about source-text checks warns about.

MUTATION TESTED, seven mutations, each caught, restored green:

  entering fall no longer releases        -> unit: entering fall drops a grip on every side
  underside survives vertical motion      -> unit: an underside grip cannot survive vertical motion
    (this one IS the shipped bug)
  release side grips too (deletes PhaseD) -> unit: a side grip survives vertical motion
  always release (nothing can hang)       -> unit: a hanging pose (vy = 0) keeps its underside grip
  CALL SITE DELETED                       -> invariant: entering an animation that cannot hold a grip...
  call site never passes the fall id      -> invariant: same
  call site clears the grip, not the handle -> invariant: same

The split is the point: the unit assertions caught every wrong predicate and NONE
of the three call-site mutations; the invariant caught all three call-site
mutations and none of the predicate ones.

NEEDS A HOST RELEASE to reach users -- this is engine code, not catalog content.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `8f609c7e7`

**docs(backlog): AI Brain should give the user their VRAM back**

```
Requested: a "flush the model after N seconds" setting whose label states the
default, and a checkbox that falls back to fortunes instead of loading a model
while a fullscreen app is running (a game plus a model competing for VRAM can
take the game down).

Both recorded with what the code already provides, plus one correction:

* THE DEFAULT IS 5 MINUTES, NOT 30. Checked against Ollama's own FAQ rather than
  answered from memory. And the label must not hardcode it: OLLAMA_KEEP_ALIVE
  sets a server-wide default that overrides it, so the honest version of "say
  what the default is" is to read GET /api/ps, which returns expires_at and
  size_vram per running model and which the module does not call today.

* The quip path sends NO keep_alive at all, so today every remark leaves the
  model resident for whatever the server default is. The fix is one field on the
  chat request, not a timer: a timer races a second quip inside the window and
  leaves the model loaded if the app exits first. UnloadAsync already exists and
  already sends keep_alive: 0; keep it for an explicit "unload now" button.

* Noted that a short keep_alive and the existing "warm up on launch" setting
  fight each other -- WarmUpAsync pins 10m -- so the pane has to say so or the
  setting reads as broken.

* For the fullscreen check, both halves exist: FullscreenScan.BlockedMonitors is
  already the tested detector behind "don't cover a fullscreen app", and the
  unprompted-remark responder already falls back to a fortune when it declines,
  so the change is an early return rather than a new path. The real cost is that
  IHost has no fullscreen predicate, so it wants a small additive
  IHost.IsFullscreenActive rather than the module re-implementing EnumWindows --
  a second copy of one policy is what source-linking exists to prevent.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `a206118ce`

**chore(catalog): petstudio 1.5.0 (behaviour timeline)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `08ec17570`

**chore(modules): publish petstudio 1.5.0**

### 2026-08-31  `080c3427e`

**feat(petstudio): a behaviour timeline that compiles a chain into a real pet**

```
Requested: a debug window to send animation commands to a live pet, drag its
actions into a linear chain, colour-code natural vs artificially-linked joins,
and trigger the chain (e.g. 10x jump back to back) to validate behaviour.

Two design decisions worth recording, because the obvious build does not work.

* THE COLOUR GOES ON THE JOIN, NOT THE CHIP. "Is this a transition the pet would
  make by itself" is a property of the edge between step N and N+1, so a coloured
  chip is ambiguous about which side it means. And there are THREE states, not
  two: sequence (the pet does this alone), border or gravity (natural, but only on
  contact, and the only= flag says which edge), and forced. Two colours would have
  to lump a border edge in with one of the others, and a jump's landing IS a
  border edge -- which the screenshot proves: chaining Grapple4 into Walk comes
  out amber, because that is the taskbar landing edge added earlier today.

* THE CHAIN IS NOT DRIVEN FROM OUTSIDE. Firing each step at a live pet through
  IHost.TryPlayAnimation and advancing after the animation's declared length is
  wrong, and by a margin that is not small: an animation ending on a BORDER ends
  early, and the old Grapple1 abandoned 16 of its 28 declared steps. A
  duration-based sequencer would start the next step while the previous one was
  still on screen and quietly run a different chain than the one being watched.
  There is no completion signal in the ABI to wait on instead -- CompanionLanded turns
  out to be a one-shot startup event (StartUp.LandTimer_Tick), not floor contact.

  So BehaviourChain.BuildDebugXml COMPILES the timeline into a throwaway pet: one
  clone per chip occurrence, every exit (sequence, border and gravity alike)
  pointed at the next clone, handed to ICompanionManager.SpawnPreview. The engine then
  runs the chain with its own timing and its own physics, which is the thing being
  validated. No new ABI, no host release.

  Pointing ALL THREE exits is what makes it deterministic without predicting which
  fires: an idle ends at its sequence end, a jump at its border, a walk stepping
  off a ledge at its gravity node. Cloning rather than rewriting keeps the pet
  honest -- the originals are untouched, so the last step hands back to them and
  the pet resumes real behaviour. A xN chip becomes N distinct nodes, because one
  node pointed at itself is an infinite loop, not N plays.

MUTATION TESTED, all nine guards, restored green afterwards:

  Classify reports everything as natural  -> FAIL an absent edge classifies as Forced
  only= flag dropped                      -> FAIL the border join carries the only= flag through
  border preferred over sequence          -> FAIL sequence is preferred when a pair has both
  border exit left unwired                -> FAIL a 3-step chain builds (XSD: border has incomplete content)
  gravity deleted instead of replaced     -> FAIL a clone has <gravity> exactly when its original did
  xN collapsed to a self-loop             -> FAIL a x4 chip becomes FOUR distinct animations
  clone inherits the original name        -> FAIL cloning a magic-named animation ...
  pet's own spawns left in place          -> FAIL the debug pet has exactly one spawn
  over-long chain truncated               -> FAIL a chain over the node cap is refused

The magic-name guard was SILENT on the first pass and is the reason this list is
worth reading: it chained the fixture's first three animations, none of which is
named fall/drag/kill/sync, so removing the clone prefix entirely left it green.
It now builds its chain FROM the magic-named animations and also asserts every
name in the debug pet is still unique. Nothing but mutation testing would have
caught that, and the trap is real: the host resolves those four names by taking
the FIRST match, so a clone called "fall" becomes the pet's falling animation.

Assertions live module-side (BehaviourChainSelfCheck.RunChecks) and are invoked by
--petstudio-selftest, which supplies the host's own bundled pet as the fixture.
Host-side reflection could not build an IList<ChainStep> and an
IDictionary<int, AnimNode> readably, and would have tested the reflection as much
as the logic. Named RunChecks, not SelfTest, so it cannot beat a module's own
--module-selftest entry point.

The window itself was smoke-tested by constructing it for real (a throwaway WPF
harness, reflection, null host), loading Hornet, populating a 4-step chain with a
x3 chip, screenshotting, and compiling the chain from what the UI held. That is
what the layout above was checked against; SpawnPreview needs the full host and is
untested.

Pet Studio 1.4.18 -> 1.5.0. No host change.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `765c95561`

**docs(backlog): a behaviour debugger, and the ceiling is unreachable**

```
Two entries, both from questions asked while the jump fix was being verified.

* Requested: a debug window that drives a live pet's animations by hand -- build
  a chain of its actions by drag and drop, colour-coded by whether each step is a
  transition the pet's graph actually offers, then trigger it N times. Most of it
  needs no host change (TryPlayAnimation and TryReadTypeXml already exist), so it
  belongs in Pet Studio. Recorded the one part that does need thought: a chain
  cannot wait on an animation's DECLARED length, because a border-terminated
  animation ends early -- the old Grapple1 ended at step 12 of 28 -- so a
  duration-based sequencer would silently run a different chain than the one on
  screen. Wants a read-only ICompanion.CurrentAnimationName instead.

  Justified by what this session cost without it: the arc had to be verified by
  re-implementing the engine's interpolation in a throwaway script, watching it
  live meant cranking a copy of the pet's hub weights to 99% jump in an isolated
  data root, and Hornet jumps once every three to five minutes at real weights,
  which makes "just watch it" useless as a verification step.

* Measured, after "I have never seen Hornet reach the ceiling": she effectively
  cannot, and it is the same defect class as the jump. ClimbWall covers 32px per
  pass at 2.5px/s, which is 26x slower than the slowest hand-authored wall move
  (yellow_sheep's wall_slide, 66.7px/s), so the top of a 1440p screen is 6.4
  minutes of unbroken climbing away -- while every pass boundary carries a 23.8%
  chance of letting go. Monte Carlo over 3,000,000 wall entries on the emitted
  weights: 9 reached the top, 1 in 333,000, or about 8 years of uptime per
  ceiling visit. A 47-hour behaviour simulation gave 215 wall entries, zero
  ceiling visits, and one lucky run that stalled 12px short.

  Fix is the same move the jump just had: budget the climb by DISTANCE, not time.
  TargetWallMs is a time budget that Hornet's single 12.8s pass already
  overshoots, so the repeat count is 0 and the distance is whatever the source's
  -2px/tick happens to give. Lowering the let-go weight is the weaker half and
  must not be done alone: at 2.5px/s the climb looks wrong even when it works.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `6b934e250`

**chore(catalog): petstudio 1.4.18 (three-phase jump)**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-31  `2105c887f`

**chore(modules): publish petstudio 1.4.18**

### 2026-08-31  `26415d471`

**fix(shimeji): a converted jump reaches a real height and lands on its feet**

```
Reported from live use: Hornet seemed to land in a sit pose. She did not -- the
graph went turn -> 9.4s of Stand -> a ~30% chance of a sit -- but the report was
right that there was no LANDING at all, and measuring the shipped corpus found
three more defects in the same family.

Every one of them produced a valid, reachable, round-tripping pet, which is why
the acceptance bar never saw them: that bar is on the graph, and these are in the
numbers. Replaying the engine's own interpolation over the 32 shipped jumps:

  before   16 arcs peaked under 20px, 16 at 72px, none between
           1 held a single frame 12px off the ground for 2s (interval 80->4000)
           0 could chain a hop
  after    30 arcs at 48-50px, 0 ramping intervals, 0 crossing >150px sideways

Four fixes, all in the converter:

* The height was an accident of the STEP COUNT, not the launch velocity. For a
  linear start->end ramp the rise is about a^2(N-1)/(2(a+b)), so clamping the
  launch a does nothing while N comes from the source skin's frame count and the
  walk budget. The peak is now the invariant and the launch is solved for it
  (SolveJumpLaunchY), against a jump-specific step budget rather than the 2.5s
  walk budget that padded 3 frames to 21 steps and 72px.
* The interval was inherited, ramp included. An arc must not change pace: flat,
  from the airtime budget.
* The 65% locomotion self-edge on a jump was dead code -- the taskbar border
  fires long before the sequence ends (12 steps of 28 on Grapple1) -- so a
  converted pet could never chain hops. Re-jumping moved to the LANDING edge,
  which is where yellow_sheep has it, at weight 30.
* Fixing the arc EXPOSED the horizontal pass-through: Grapple4 dashes at
  -100px/tick, and once the arc lasted a proper 15 steps it crossed 1500px, so
  16 of 18 jumps ended at a side border and never reached the new landing set.
  Capped at yellow_sheep's own 150px span.

Shape is now the sheep's three phases: solved arc -> fall if the arc outlives the
drop -> an only="taskbar" landing weighted toward re-jumping and running. Hornet
went from 30 of 31 landings into turn to 18 of 26 into motion, and chains hops.

Two actions that only LOOKED like jumps (Hornet's Grapple1 and 1l2yvz73's fly,
both rising -5) are flattened to play along the ground rather than dropped, and
reported in the residue. Every target value is measured off yellow_sheep's jump,
not invented.

Shipped to the 31 affected pets by a new `rejump` migration, not a re-conversion:
no new sprite frame is involved, so 25 sheets would have been regenerated into
identical pixels and Hornet's hand-edited fall/Grapple3 frame swap would have
been wiped. Header format 1.2 -> 1.3, idempotent, hand-authored pets skipped by
the author gate. reweight now stamps its own 1.1 rather than whatever the emitter
is on, or it would claim the ceiling and the jump arc for a pet with neither.

MUTATION TESTED, all seven guards, each restored green afterwards:

  step-count linkage broken   -> FAIL 'PullUp' rises 65px over its 21 declared
                                 steps  + FAIL 'HopUp' rises 28px over 7
  solver replaced by -15      -> FAIL 'LongLeap' rises 82px over its 24 steps
  interval ramp restored      -> FAIL 'LongLeap' has a ramping interval (80 -> 4000)
  phase 2 reverted to self/hub-> FAIL all four jumps do not lead to `fall`
  landing edges removed       -> FAIL all four have no only="taskbar" self edge
  weak-rise floor -8 -> -1    -> FAIL a rise too weak to be a jump reached the
                                 output unflattened
  horizontal cap bypassed     -> FAIL 'HopUp' travels 1400px sideways over its arc

Three of the four jump fixtures exist because of that exercise. BigJump alone
proved nothing: its 2 poses at 4 ticks make the old walk budget pick exactly the
14 steps the solved arc wants, so a pass-through launch satisfied every height
assertion by luck. PullUp is the 72px fling, HopUp the 11px twitch plus the
violent x, LongLeap has more frames than the step budget, which is the only case
a fixed launch velocity cannot serve.

Pet Studio 1.4.18 (it source-links the emitter). No host change and no release:
only="taskbar" already existed in the engine, and pets ship off master.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-28  `8f1607acb`

**docs: session wrap-up for v1.9.5 (jumps, drag swing, gaze, four window edges)**

```
Readme: the converted-pet paragraph said they walk, rest, get dragged, climb screen edges and cross the
ceiling. They now also jump, swing from your hand while dragged, sit and look at your pointer, and use all
four edges of a window.

BACKLOG: Phases 0 and A-E marked shipped, with what each estimate got wrong kept next to it rather than
deleted -- that is the part worth having. Chiefly that the `activeIE` action counts which justified the
window work were misleading: all 392 of those actions across the 12 desktop skins carry ZERO sprites, being
Sequence/Select wrappers over actions that already convert, and no converted pet carried a window edge at
all. What the work actually bought is that every converted pet already ships wall and ceiling art usable
only at the two SCREEN edges, and a window has four more. ChaseMouse is now the only open item in that
section, and the question it was deferred on ("does pointer-aware gaze already scratch the itch") is
finally answerable by watching a pet.

handoff.md: new START HERE for this session. The traps, in the order they will bite again; the two
source-text invariants that passed against broken code until the mutation run caught them; the redundant
early-out recorded rather than papered over; and the live smoke that has not happened yet, since nothing
automated can watch a pet hold a real window.

Housekeeping, all verified rather than assumed:
  * no credentials tracked; .gitignore already fail-closes .env/*.pem/*.p12/*.jks/secrets.json, and the app
    keeps real keys DPAPI-encrypted under %LOCALAPPDATA% outside this tree
  * no AI leftovers tracked; CLAUDE.md, CLAUDE.local.md, AGENTS.md, .claude/, .codex/, .mcp.json, SESSION_*
    and HANDOFF_* are all ignored. handoff.md is tracked ON PURPOSE and says so in its own header.
  * deleted SESSION_HANDOFF.md, a stale gitignored local file from 2026-08-20 superseded by handoff.md
  * no PII and no employer material. The only hits for "special ed" across tracked sources are fortune-pack
    quotes (South Park, Drawn Together, an Anathem glossary entry).

Release soaks for v1.9.5, both PASS:
  runtime-resource-soak  handles +2, GDI -24, USER -11, private +12.8 MB (bounds: 16 each, 64 MB)
  module-window-soak     handles +0, GDI +0, USER +0, private -8.9 MB across the last segment;
                         every module window collected, 0 still rooted

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `f13a111a2`

**chore(catalog): petstudio 1.4.17 (window underside)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `198ac05a2`

**chore(modules): publish petstudio 1.4.17**

### 2026-08-28  `b01421775`

**PHASE E: pets hang from the underside of your windows**

```
The last of the four edges. A pet jumping under a window now catches its bottom edge, hangs there, walks
the length of the overhang, and at either corner swings onto the side of the frame -- where Phase D takes
over and it can climb down, or back up over the lip onto the title bar.

Again no new art. Every converted pet ships ceiling poses that until Phase C could only ever be used at the
top of the SCREEN. All 31 gained the underside: 32 window-bottom edges, and window-left/right went from 178
to 237 as the ceiling spokes gained their corner exits.

REACHABLE ONLY BY JUMPING, and the graph says so rather than leaving it to the physics. The window-bottom
edge is attached to jump spokes and nowhere else, the same discipline the ceiling region uses at the screen
top. A walk cannot travel upward so it could never meet the border however the edge were written, but
stating it means a future change to what counts as locomotion cannot quietly open the door. Phase 0 is what
made this possible at all: before jumps converted, nothing a pet did travelled upward off the floor.

THE MAXIMISED-WINDOW TRAP. A maximised window's bottom edge sits on the work area, which is directly over a
pet standing on the taskbar, so without a clearance test the pet would grab the underside on the first tick
of every jump it ever made and never do anything else. RiseDetect ignores any window whose bottom is within
4px of the work area bottom.

RiseDetect is a separate walk from FallDetect rather than a parameter on it. They share the window
enumeration and nothing else: opposite edge, opposite crossing direction, and a different z-order question
entirely -- standing on a window asks "is anything covering the surface I am on", hanging under one asks
nothing of the sort, because a window in front does not stop it being underneath. It also requires the
pet's whole width inside the window, where FallDetect allows half: a pet standing half off an edge reads as
balancing, one hanging half off a corner reads as broken.

CrossesAscendingBoundary is deliberately NOT CrossesDescendingBoundary with the signs flipped. Descending
lands ON the boundary (>=), which is coming to rest on a surface; ascending must pass THROUGH it (<=) to
have gone under a window rather than merely touched it.

WINDOW_BOTTOM is 0x80, the first flag outside the 0x7F that NONE happens to equal.

MUTATION TESTS -- eleven, all caught, each naming the right symptom:

  WINDOW_BOTTOM moved inside the NONE mask, onto WINDOW_TOP
      -> "a bottom-edge animation fires under a window only" + "the other window edges do not fire"
  GripFor does not recognise the underside
      -> "only an explicit window-bottom edge takes hold"
  the hang ADDS its top inset instead of subtracting it
      -> "the character's top edge lands on the window's bottom edge"
  the rising test stops requiring the step to REACH the edge
      -> "a rising step that stops short is not"
  the rising test re-catches a pet already above the edge
      -> "a pet already above the edge is not re-caught by it"
  the screen top un-chained from the underside result (both fire on one tick)
      -> "a window underside is checked before the top of the screen, and the screen top is chained off it"
  a refused underside keeps the window handle
      -> "a refused window underside gives the window handle back"
  the maximised-window clearance removed
      -> "the underside test ignores a window whose bottom is the work area"
  no window-bottom entry emitted
      -> "so the pet can never hang under a window"
  a non-jumping walk offered the underside
      -> "which does not travel upward, so it can never reach a window's underside"
  the underside corners made a dead end
      -> "the corner is a dead end"

TWO FINDINGS RECORDED RATHER THAN PAPERED OVER.

First, the ordering assertion was not enough. "Check the underside before the screen top" is satisfied by a
RiseDetect call that appears first but is gated behind something unreachable, and the mutation proving that
survived. The load-bearing property is that the screen-top test is CHAINED off the underside result, so a
pet that just grabbed an overhang cannot also be snapped to the top of the display on the same tick. The
check now asserts the chaining.

Second, the `deltaY >= 0` early-out in CrossesAscendingBoundary is redundant: a non-negative step cannot
satisfy the inequality pair that follows, so removing it changes no answer, and the mutation survives. The
same is true of the existing `deltaY <= 0` in CrossesDescendingBoundary. It is kept for symmetry and NaN
screening, and the function's comment now says it is documentation of intent rather than a guard, since a
control that cannot fail should not be presented as one.

A third claim was written and then negative-tested away: an earlier version of this work asserted that
WINDOW_BOTTOM sitting outside NONE's mask made the NONE short-circuit in Eligible load-bearing. It does
not. Every site raises its discriminator alongside plain WINDOW, so an unconditional edge still matches by
mask. The comment in Animations.cs says so.

STILL EYEBALL-ONLY, as with Phase D. Worth checking by hand: jump under a floating (not maximised) window
and catch the underside; walk to the corner and swing onto the side; confirm a pet on the taskbar under a
MAXIMISED window jumps normally and does not grab anything; move the window while the pet hangs.

Verification: ShimejiConvert selftest green; verify Pets = 53 pets, 0 invalid, 0 round-trip failures,
unreachable unchanged at 7; tests\run-gate.ps1 green (the petstudio mismatch it reports is the publish that
follows this commit). The XSD gained window-bottom in both copies.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `c843c6823`

**chore(catalog): petstudio 1.4.16 (window-side cling)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `8e1bca543`

**chore(modules): publish petstudio 1.4.16**

### 2026-08-28  `4d7c1eee5`

**PHASE D: pets climb down the sides of your windows**

```
A pet could stand on the top of a window and nothing else. Walk to the edge and it turned round or fell
off. It now sometimes grips the side instead, climbs down the outside of the frame, and either drops off
the bottom or turns and climbs back up over the lip onto the title bar.

No new art. Every converted pet already ships wall poses, and until now the only place they could ever be
used was the two screen edges. A window has two more, and all 31 converted pets gained them: 178
window-left edges, 178 window-right, 32 window-top.

HOW IT OPTS IN, which is the whole safety story. The grip starts only when the border edge the pet chose
said `window-left` or `window-right` exactly. An edge saying the old wildcard `window` fires at the same
place and does NOT grip -- 955 of those ship in the hand-authored pets, written when a window had one
undifferentiated edge, and a bit test rather than an exact match would have recruited every one of them
into a behaviour their authors never asked for. SetNextBorderAnimation gained an overload reporting which
condition the chosen edge declared, because inferring the intent from the chosen animation's SHAPE was the
alternative and it cannot distinguish those two cases at all.

Entered on the DESCENDING wall pose, not the climb. Entering on a climb sends the pet straight back up into
the window top it just left, over the lip, and back where it started: a loop that costs a tick and shows
nothing. The wall spokes chain among themselves, so it can still turn round and climb.

WHAT THE HOST HAD TO LEARN. hwndWindow already existed but means "standing on the top" everywhere it is
read, geometrically: CheckTopWindow's coverage test compares candidates against rctO.Top, FollowWindow
re-pins the pet to the top. So the grip is a separate field, and hwndWindow became a property that clears
it -- nine sites drop that handle for their own reasons (spawn, relocate, drag, walked off, window covered)
and any one of them forgetting would pin the pet to a rectangle nothing re-reads.

The rect is re-read every tick rather than cached at grip time, because a window can be dragged, resized,
minimised or closed underneath the pet. A degenerate rect (what a minimised window reports) releases. The
window's top is carried as a DELTA so a window dragged vertically takes the pet with it, which a fixed
offset could not do while the pet is also climbing.

The grip's vertical limits are the window's and are tested BEFORE the screen's. Reversed, a gripping pet
climbs straight past the frame it is holding and up to the top of the screen.

MUTATION TESTS -- thirteen, all caught, each naming the right symptom:

  GripFor changed to a bit test (recruits every only="window" edge)
      -> "the old generic window edge does NOT take hold"
  GripFor stops taking hold on the right
      -> "only an explicit window-left/right edge takes hold"
  the right grip uses the LEFT inset
      -> "the right grip's character edge really is the window edge"
  the left grip ignores its inset
      -> "the left grip's character edge really is the window edge"
  dropping the window handle no longer drops the grip
      -> "a window grip re-reads the window every tick and is dropped with the handle"
  the grip caches the rect instead of re-reading it            -> same
  a degenerate (minimised) rect no longer releases             -> same
  the window bounds are checked AFTER the screen ones
      -> "a gripping pet is bounded by the window, checked before the screen"
  no window-side entry emitted
      -> "the pet cannot grip a window's left side"
  the window side entered on the CLIMB
      -> "entering on a climb returns the pet to the window top it just left"
  no way back onto the window top
      -> "it can only ever let go"
  a DESCENDING pose also offered the window top
      -> "only a CLIMBING pose can reach a window's top edge"
  the screen-top fall weight reverted to a flat 100
      -> "the screen-top ceiling/fall split moved (ceiling=2, fall=100, expected 2 and 1)"

Two of those needed the check fixing rather than the code. The handle/grip one first passed against the
mutated source: asserting that the property body mentions `windowGrip = WindowGrip.None` is satisfied by a
setter whose guard has been disabled, since the statement is still there, just unreachable. It now asserts
the condition. And an emitter assertion using "carries no <gravity>" to mean "is not a floor animation"
rejected the fixture's own jump -- a jump is a floor animation that deliberately has no gravity node,
because gravity would cut its arc off at frame one. Hub reachability is the right discriminator.

The last mutation is the one worth keeping an eye on: the fall weight on a wall spoke used to be a flat 100
whenever there was no ceiling edge, and the window-top edge now shares that slot. Get the condition wrong
and a pet at the SCREEN top silently stops falling, which nothing else would have noticed, so the 2:1
ceiling-vs-fall split is now pinned by number.

STILL EYEBALL-ONLY. Nothing automated can watch a pet hold onto a real window. Worth checking by hand:
grip and climb down a browser window's side; drag that window while the pet holds it; minimise it while
the pet holds it; climb up and come over the lip onto the title bar.

Verification: ShimejiConvert selftest green; verify Pets = 53 pets, 0 invalid, 0 round-trip failures,
unreachable unchanged at 7; tests\run-gate.ps1 green (the petstudio mismatch it reports is the publish that
follows this commit). The XSD gained the three enumeration values in both copies.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `2cf20ea76`

**chore(catalog): petstudio 1.4.15 (window-edge only= vocabulary)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `9abb35134`

**chore(modules): publish petstudio 1.4.15**

### 2026-08-28  `4ebf08951`

**PHASE C: a pet can tell which edge of a window it reached**

```
The host detects three window borders -- walking off the left side, walking off the right side, landing on
the top -- and raised the same bare WINDOW flag at all three. A pet could react to "I am at a window" and
to nothing more specific than that.

Three discriminators now go out alongside it (WINDOW | WINDOW_LEFT, and so on), with matching only= values
window-left / window-right / window-top. `window` stays exactly what it was: a wildcard that fires at all
three. That is not a nicety. 955 window edges ship in the hand-authored pets, every one of them says
only="window", and they were written when "on a window" was the only thing that could be said.

CORRECTING THE PREMISE THIS WAS PLANNED ON. The plan justified this phase with 184 source actions gated on
window geometry. Surveying all 12 desktop skins finds 392 actions mentioning activeIE and ZERO of them
carry sprites of their own -- they are Sequence and Select wrappers over Walk, Stand, Sit, Jumping and
GrabCeiling, choreographing actions that already convert ("walk to a point 100-400px right of the active
window's left edge, then stand, then sit"). Not one converted pet in the catalog carries a window edge at
all today; all 955 belong to the hand-authored pets.

So this phase, by itself, changes nothing any user can see, and shipping it as if it were a feature would
be dishonest. It is the format and host vocabulary that Phase D (cling to a window's side) and Phase E
(hang from a window's underside) need, and the real prize there is not fidelity to those wrapper actions.
It is that every converted pet already carries wall and ceiling art it can currently only use at a SCREEN
edge, and a window has four edges going spare.

Also collapses the three identical only= switch statements in Xml.cs into one ParseOnlyFlag. They were
per-node-type copies, and adding the new values to the border and sequence copies but not the gravity copy
would have produced a pet whose border edges discriminated and whose gravity edges quietly did not.

MUTATION TESTS -- eight, all caught, each naming the right symptom:

  WINDOW_LEFT given the generic WINDOW bit (0x10 -> 0x12)
      -> "a left-edge animation fires on the left edge only"
  WINDOW_TOP collided onto WINDOW_RIGHT (0x40 -> 0x20)
      -> "a right-edge animation fires on the right edge only" + the top one
  Eligible changed from a mask test to equality
      -> "a generic window edge still fires at all three window borders" + horizontal+ + both sides
  ParseOnlyFlag maps window-left to plain window
      -> "the only= vocabulary maps to the right flags"
  validator drops window-left from its accept list
      -> "the validator accepts the window-edge vocabulary"
  validator accepts everything
      -> "the validator still refuses a value nothing implements"
  left window border reverted to a bare WINDOW
      -> "each window border raises which edge it is"
  top window border reverted to a bare WINDOW
      -> same

The last two needed a source-text check added. The flag algebra is asserted in --hardening-selftest, but
nothing there can see whether the detection SITES pass the new value: revert any one of them and every
other check stays green while that edge silently stops being distinguishable.

Two seams were opened for this: TNextAnimation.Eligible (also removing a duplicated eligibility test that
the weighting loop ran twice, where the two passes disagreeing would have picked an animation whose weight
was never counted) and CompanionXmlValidator.IsAllowedOnly.

Verification: tests\run-gate.ps1 green, 16 self-tests with no skips (the petstudio version mismatch it
reports is the publish that follows this commit). No pet content changes: no pet emits these values yet.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `77d953847`

**chore(catalog): petstudio 1.4.14 (gaze conversion)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `f79e617aa`

**chore(modules): publish petstudio 1.4.14**

### 2026-08-28  `a23fcfba5`

**PHASE B: pets look at the pointer**

```
A shimeji's "sit and look at the mouse" action used to convert to nothing at all. The cursor condition
makes the whole action Group2, IsFloorAction demands Group1, so it never became a spoke -- and, less
obviously, its sprite was never composited into the sheet either, so even after admitting it the frame
lookup found no key and the spoke was dropped a second time for having zero frames.

Both holes are closed and the animation is emitted with a new sequence action, faceCursor, which the host
reads to aim the pet at the pointer as the animation begins. Once on entry rather than tracked per tick,
which is what the source does too: it re-enters its look action every few seconds to re-aim, so the pet
glances rather than swivelling continuously.

WHICH pose. The variants of a gaze are not interchangeable alternatives, they are a cascade over where the
pointer is, and across the seven skins that ship one the FIRST is always "cursor near the top of the
screen" -- so the usual Animations[0] pick is a pet permanently craning upward. The last variant carries no
Condition in every shape the corpus contains (2, 3 and 7 variants wide), because Shimeji takes the first
match top to bottom and the cascade needs a catch-all to resolve at all. That catch-all is the neutral
pose, and it is the one frame that is right under a horizontal-only aim. A median pick was the alternative
and is wrong on the widest case: Serial Designation J's seven variants split on cursor.x as well as
cursor.y, so the middle of the list is "up and to the left", not "level".

The vertical axis is genuinely lost and the residue now says so in those words. It used to say the action
"needs cursorX/cursorY + selfX/selfY, added in Stage 5", which is false now that the horizontal half has
shipped, and a residue report that describes a shipped capability as pending is worse than one that says
nothing.

Chasing stays out. A cursor-conditioned action that MOVES needs per-tick steering the format cannot
express, and admitting one would give a pet that lurches off in a fixed direction whenever it felt like
chasing. IsGazeAction rejects any action with a non-zero velocity in ANY variant, not just the first.

Scope, measured rather than estimated: 8 gaze animations across 8 of the 31 converted pets (cyn, kinitopet,
ralsei, rick, serial-designation-j, cartman, uzi-doorman, gakupo). The remaining 23 re-converted
byte-identical, which is why only 8 pet files are in this commit. Cartman's arrives under its Japanese name
because his entire conf is Japanese and always has been.

MUTATION TESTS -- seven, all caught, each naming the right symptom:

  faceCursor tag dropped from the emitted sequence
      -> "the gaze animation carries no faceCursor action, so it plays facing whichever way the pet
          already was"
  gaze variant pick takes a CONDITIONAL variant instead of the fallback
      -> "the gaze used a conditional variant instead of the unconditional fallback pose"
  IsGaze removed from the direction-collapse guard
      -> "the gaze and the same-framed plain rest collapsed together, losing one of them"
  gaze poses removed from PosesToComposite
      -> "a gaze with no shared art emitted nothing, so gaze poses are not reaching the sprite sheet"
  residue reason reverted to the classifier's stale text
      -> "the residue still describes the gaze as needing a host change that has shipped"
  ShouldFaceLeft inverted
      -> three failures, including "gaze: a cursor left of the character faces left"
  FaceTheCursor() call deleted from SetNewAnimationCore
      -> "a faceCursor animation aims the pet at the pointer on entry failed"

The fourth of those did not fail on the first attempt and the fixture was wrong, not the guard: the single
gaze shared its neutral image with a plain rest, so the tile was composited either way. A second gaze drawn
with art nothing else uses is now the canary. The seventh needed a source-text check added, because a
deleted dispatch leaves every other test green -- the animation still plays, just never aimed.

Verification: ShimejiConvert selftest green; verify Pets = 53 pets, 0 invalid, 0 round-trip failures,
unreachable unchanged at 7; tests\run-gate.ps1 green (the petstudio version mismatch it reports is the
publish that follows this commit, since the catalog hashes the committed blob).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `8d5df32b7`

**chore(catalog): petstudio 1.4.13 (drag swing arc)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `f759e0ff3`

**chore(modules): publish petstudio 1.4.13**

### 2026-08-28  `7424f68f4`

**PHASE A: dragged pets swing from your hand**

```
The plan called this converter-only routing. It is not: Pinched carries up to SEVEN <Animation> blocks, each gated on a band of horizontal offset between the pet's body and the cursor (#{FootX < cursor.x-120}, -30, -10, centred, +30, ...). Those are not alternatives to choose between, they are the frames of a SWING, and the emitter's read-Animations[0]-only rule picked the furthest-left pose and emitted a ONE-FRAME drag. That is why a dragged pet hung frozen and stiff.

What was already right: Pinched is Class=...action.Dragged, so FirstWithClass already consumed it as the drag magic animation, and Thrown is a poseless Type=Sequence composite that correctly never became an animation. Neither was ever playing at random on the floor, so the residue counting them as degraded overstated the problem.

Converter: the drag action now composites and emits EVERY variant, one frame each, in source order. Source order is the swing order by construction, not by luck -- Shimeji evaluates these conditions top to bottom and takes the first match, so an author must write them in ascending offset order for the cascade to work. Parsing the thresholds back out would add a fragile expression parser to re-learn what the file already guarantees. Also raised the drag repeat: one pass was fine for a single frozen frame, but with 7 it ENDED mid-drag and dropped the pet into all while still held.

Host: positional lag cannot drive this, because the drag branch snaps the pet's centre onto the cursor every tick so the lag is always zero. Cursor VELOCITY gives the same feel and touches neither the positioning nor the stuck-to-mouse self-heal: move the mouse right and the body trails left, stop and it settles upright. Smoothed, or a jittery mouse strobes through the poses.

7 assertions on the mapping, made pure so they need no form or mouse. Mutation-tested by inverting the direction (body LEADS the hand), which failed five of them -- the sign is the easiest thing here to get backwards. 12 pets now carry 5-7 swing poses; single-pose drags (most Android bundles) behave exactly as before and the index cannot run out of range.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `342bfab71`

**chore(catalog): petstudio 1.4.12 (jumps)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `b6e25c073`

**chore(modules): publish petstudio 1.4.12**

### 2026-08-28  `e5d2a19b3`

**PHASE 0: converted pets can jump**

```
81 jump actions across 27 pets, refused for the project's whole life by one guard in IsFloorAction that rejected any pose with VelY < 0. That guard was right about the danger -- an unbounded upward velocity on the open floor launches the pet off the top of the screen -- and wrong that the answer was to refuse the family.

It was never a format or engine limit. yellow_sheep carries 22 upward-start animations and jumps constantly; its jump is -15 up then +20 down, and NOT ONE of the 22 has a <gravity> node, because the arc lives entirely in the start/end interpolation and gravity would end the jump the instant the pet left the ground. So jumps are converter-only: no format change, no engine change, no host release, shipping through the catalog like any other pet content.

Upward floor actions are now admitted and emitted as a BOUNDED arc: the launch is clamped to -15 (the corpus contains launches as violent as -40, which would leave the screen), a descent of +20 is FORCED whatever the source said, and gravity is omitted. Bounded is the entire safety argument -- whatever the source asked for, the pet comes back down. Wall spokes are untouched: they still climb without a forced descent, verified on uzi-doorman's ClimbWall coming out at -2/-3.

Mutation-tested three ways, one targeted failure each: removing the clamp let -40 through, removing the forced descent left end y=-30 so the pet never returns, and re-adding gravity produced the cut-off-at-frame-one case. The fixture launches at -40 with no descent of its own, so the assertions check the EMITTED arc rather than the source numbers.

31 pets re-converted, all accepted. Counts up across the board: hornet 29->31, uzi 71->74, cyn 49->51, cartman 23->24. 53 pets, 0 invalid, 0 round-trip failures, unreachable unchanged at 7.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `ed454bde3`

**Re-plan the condition work: jumps first, window underside blocked behind them**

```
Two questions reordered the whole plan, and both corrected an estimate of mine.

The sheep ARE application-box aware, so activeIE is much cheaper than I said. FormCompanion already has three separate window-edge detections (:849 left, :893 right, :939 top) and every one passes the same TOnly.WINDOW -- the engine computes which edge was hit and then discards it. That is missing DISCRIMINATION, not missing detection: 184 of the 335 actions need only new only= values plus a one-line change at each existing site, with plain 'window' kept as a wildcard so all 22 hand-authored pets are untouched. A further 36 (window side cling) reuse the wall region, since clinging is just the absence of <gravity> and a window edge is a wall whose x comes from GetWindowRect.

And asking what a window UNDERSIDE would buy killed that phase as scoped, then produced the best item on the list. Every entry point is ...FromJump: a Shimeji reaches a window's underside by jumping and catching it, so without jumps those 60 actions convert and can never play. Jumps then measured at 81 occurrences across 27 pets -- more pets than activeIE -- and the format and engine ALREADY support them (yellow_sheep and blue_sheep each carry 22 animations with upward start velocity). The only blocker is a converter guard in IsFloorAction rejecting any VelY < 0. So jumps are converter-only, no format or engine change, and they are now Phase 0.

Also split the cursor work by what each part actually needs, because 58-actions-medium-effort was three jobs at three costs: drag reactions (~26) need no format change at all, gaze (~18) needs one sequence action shaped like the existing flip, and ChaseMouse (~14) needs a new per-tick movement mode and is deliberately deferred.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `0b34cb1ea`

**Update the reference-conf census pin: 53/32/6 -> 54/31/6**

```
The pin did its job and I pushed past it. ClimbWall moving from Group2 to Group1 changes the bundled reference conf's census by one action, and the census exists precisely so a classification change cannot ship unnoticed. Updated deliberately with the reason recorded next to the number, and the doc comment now says to update it that way or not at all.

My error was chaining the gate, the commit and the push in one command, so a red gate did not stop the push. Run the gate as its own step.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `ed9a69d58`

**chore(catalog): petstudio 1.4.11**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `0c88a5531`

**chore(modules): publish petstudio 1.4.11**

### 2026-08-28  `e9b346e10`

**Classify target-relative gates honestly, and plan the remaining condition classes**

```
A target-relative condition is not lost host state. ClimbWall's #{TargetY < mascot.anchor.y} is a loop-continuation test -- am I still short of where I am heading -- and the emitter already answers it by replacing Shimeji's conditional selection with a border-driven graph plus a time-budgeted repeat: the pet climbs until it reaches the top border, which is exactly what the condition said. Reporting it as 'needs selfX/selfY' claimed a host change was required to recover something that already converts, and it is why ClimbWall looked like a casualty when the real culprit was my own frame-list collapse. 12 of 13 reports resolved; every animation count unchanged, because nothing about the output was ever wrong.

Also measured, rather than estimated, what the remaining condition classes are worth. activeIE is 335 actions across 13 pets and six times the next item; cursor is 58, of which a large share are drag poses that may map onto the host's existing drag path for nothing; totalCount is ZERO across all 31 shipping skins and should not be built. Recorded in BACKLOG with the blocking reason, the work, and the risk for each.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `a4ee36303`

**Converted pets stop moonwalking; recover behaviours a bad merge had eaten**

```
Reported as "every shimeji only ever faces left", and the diagnosis is worse
than the symptom.

MOONWALKING. A source skin stores ONE set of artwork and defines walk_left AND
walk_right over the very same frames, because the player is expected to MIRROR
one of them. This engine does exactly that: FormCompanion draws
Xml.GetSpriteFrame(index, !IsMovingLeft), so unmirrored art is the left-facing
direction. Emitting BOTH variants therefore guaranteed that half of all
locomotion was wrong: with the default IsMovingLeft=true, walk_left drew
left-facing art moving left (right), while walk_right drew the SAME left-facing
art moving right (wrong). After a flip the two swapped which one was broken.

So direction pairs are collapsed to one animation and the engine's flip handles
facing, exactly as every hand-authored pet does.

AND THEN THE FIRST VERSION OF THAT FIX ATE REAL BEHAVIOURS, which is the part
worth reading. Collapsing on frame-list identity alone is too blunt: KinitoPET's
GrabWall and ClimbWall are built from the very same four images, because the art
IS the pet gripping a wall, but GrabWall holds still (0,0 throughout) while
ClimbWall travels up (0,0 / 0,-1 / 0,-2 / 0,-1). Merging them kept the static
grab, threw away the climb, and with it the only route to the ceiling -- so
KinitoPET then failed acceptance on an unreachable ceiling animation.

The rule now compares VELOCITY PROFILES: two same-framed animations collapse
only if they are an exact duplicate, or a true left/right pair (horizontal
velocity mirrored, vertical identical). Anything else is a different behaviour
reusing artwork and both are kept. That recovered animations across the board,
not just KinitoPET: 76xviks0 28->31, 5xs0ld2m 27->30, hornet 28->29, uzi 70->71,
cartman 21->23. All 31 converted pets now gain wall AND ceiling, up from 30.

Two smaller fixes found on the way:
  * The ceiling needs a wall spoke that actually CLIMBS, not merely a wall
    region, or it is emitted with nothing able to reach it. Guarded, with a
    synthesised climb as a fallback for a skin that has wall sprites but lost its
    climbing action (a grab pose given upward velocity reads as climbing, which
    is the same move the emitter already makes for 'turn').
  * "turn" is uniquified. Several skins already have an action called Turn, so
    pets shipped with TWO animations named "turn", only one carrying
    <action>flip</action> -- and anything resolving by name takes the first.

PROACTIVE SWEEP, because every one of these was found by eye, one pet at a time.
scratchpad/audit-pets.ps1 walks every animation in every pet and checks what the
existing gates cannot: blank tile references, duplicate names, identical frame
lists, gravity on a climb, intervals past the tick cap, frames outside the sheet,
missing magic names. The hand-authored pets are the reference. Current state, and
it is not the direction anyone expects: BLANK 68 hand-authored vs 4 converted,
NOMAGIC 10 vs 0, DUPNAME 7 (all seven sheep) vs 0. Worth knowing that blank
frames are LEGITIMATE for deliberate invisibility (ssj-goku's
Instant_Transmission, alipheese's Teleport, the sheep's bathd), so this is a
diagnostic to read rather than a gate to fail.

Pets: KinitoPET, Ralsei and Cyn added, chosen for diversity across franchises
rather than raw animation count -- a scan of 2,729 archives (585 after
de-duplication) put four Deltarune characters in the top eight, and Gamzee at 135
animations from only 51 sprites. 50 -> 53 pets, 0 invalid, 0 round-trip failures,
unreachable unchanged at 7 (all hand-authored).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `ec694e37f`

**chore(catalog): fortunes 1.2.6, petstudio 1.4.10**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `be74826ae`

**chore(modules): publish petstudio 1.4.10**

### 2026-08-28  `6cab1ee4b`

**petstudio 1.4.10: pick up the tile-bleed fix from the source-linked Xml.cs**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `24fe39b6e`

**chore(modules): publish fortunes 1.2.6**

### 2026-08-28  `bf983ff2f`

**Fix the dark rim on downscaled frames, and make a collapsed fortune pool announce itself**

```
TILE BLEED (host, so this needs a release to reach anyone). Scaling a sub-rectangle straight out of the sprite sheet let the interpolation kernel sample past the source rect, and GDI+ blended the transparent area beyond it into the destination's outer pixels: every smoothly downscaled frame came out with a slightly dark rim. Only converted (alpha) pets being downscaled took that path, which is why it showed on Jesus Our Lord. WrapMode.TileFlipXY, the usual remedy, does NOT work for a source sub-rectangle -- measured on a pure-white tile beside a black one: darkest edge pixel 236 broken, 237 with WrapMode, 254 with extract-then-scale. So the smooth path now cuts the tile 1:1 unfiltered and scales that standalone bitmap, where its edges are real image edges. Asserted in --hardening-selftest with that same fixture.

FORTUNE REPEATS, diagnosed by measurement rather than a ninth guess at the picker. The shuffle bag is correct, the provider is not rebuilt per pick, and CompanionLanded is a one-shot spawn greeting. The actual cause is the FILTER: of 32,522 installed pack lines, only 2,794 were eligible, and every one of them a dad joke -- 157 of 190 sources switched off, taking all 30 tv-* sources, showerthoughts, bofh, all 19 nsfw and all 4 spicy with them. The pane reported that as a green tick, 'from 1 pack'. An EMPTY pool was called out; a collapsed one was not. PoolStatusFor now warns, names how many sources are off, and says repeats arrive sooner than the count suggests -- with assertions on both sides of the threshold so the warning cannot become wallpaper.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `f04edd649`

**chore(catalog): publish the six-module set, and record the tray-icon convention**

```
Every tray entry carries its own unique icon from here on; written into the module template so a new module inherits it. Also logs the two findings that are NOT fixed: the dark line on Jesus Our Lord's fall frame is runtime tile sampling (the baked tiles are clean), and blank frames are legitimate for teleport/bath animations, so the blank-tile assertion stays on the synthetic fixture rather than becoming a corpus gate.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `1ad7f0cfd`

**chore(modules): publish petstudio 1.4.9**

### 2026-08-28  `e7898f962`

**petstudio 1.4.9: pick up the blank-ceiling-tile fix from the source-linked compositor**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `fc5c66866`

**chore(modules): publish blinkingled 1.0.3**

### 2026-08-28  `2604bf7b5`

**chore(modules): publish remembrance 1.1.1**

### 2026-08-28  `bf611fc1e`

**chore(modules): publish reminder 1.7.1**

### 2026-08-28  `c754cb421`

**chore(modules): publish fortunes 1.2.5**

### 2026-08-28  `923b041a5`

**Fix blank ceiling tiles, tray icons everywhere, and HTML entities in fortunes**

```
The ceiling region shipped in 1.9.4 with EMPTY frames on every Android-bundle
pet. Caught by eye on Kopo, not by any gate.

Cause: to put the ceiling contact point on the cell's top edge, the compositor
skipped AnchorY source rows. That is right for a desktop Shimeji ceiling pose,
which anchors at 64,48 because that is where the mascot grips. Android bundles
anchor EVERY pose bottom-centre, so AnchorY equals the image height, and the
blit skipped the entire sprite and wrote a fully transparent tile. The pet
simply vanished for the length of the animation.

Nothing noticed, and that is the more interesting half. The XML validated, the
graph was reachable, the round-trip passed, and the residue report cheerfully
said "Ceiling walking IS converted". The synthetic fixture used a top-anchored
ceiling pose (20,24), so it never exercised the bundle convention at all.

Two fixes, one narrow and one general:
  * Only treat the anchor as a ceiling contact point when it actually sits in
    the sprite's top half; otherwise align the sprite's own top to the cell top
    and skip nothing. Plus a hard guard that a skip can never empty the tile.
  * The fixture gains a BOTTOM-anchored ceiling action, and the self-test now
    fails if ANY animation references a fully blank tile. That is the assertion
    that would have caught this independent of anchor conventions.

Measured, not assumed: 17 pets carried blank ceiling frames before (roughly 290
frame references), 0 after. The one remaining blank reference in the corpus is
alipheese's TeleportStart/TeleportEnd, which is an intentional invisibility
frame in the source skin -- worth knowing, because it means "no blank tiles" is
correct for the fixture but would be WRONG as a corpus-wide gate.

Also in this change:

Tray icons. Every tray entry now carries its own icon, which is a project
convention from here on: agenda / reminder / meeting (Reminder), recording /
snapshot (Remembrance), and the bulb (Blinking LED, lifted from the standalone
app's own bulb.ico rather than redrawn). The BlinkingLed self-test asserts every
entry has one and that no two share a glyph.

Blinking LED drops to ONE tray row. Off is folded into the rate submenu, so the
parent shows state and the submenu carries every action; picking a speed also
switches it on, which is what reaching for "Hyper" while it is off already means.

Fortunes decodes HTML entities left in scraped pack text, so a line reads
"me & Dave" rather than "me &amp; Dave". Reddit-sourced lines are double-escaped
(&amp;#x200B;), hence two bounded passes and a zero-width strip. Done at parse
time, so it repairs packs already sitting in a user's fortunes folder without a
re-download. Bounded on purpose: a fortune ABOUT typing an entity has to survive.

Pets: The Knight and Zote removed at the maintainer's request, and Capybara
(Brown) with them; Capybara (Albino) stays. 53 -> 50. For the record, an audit of
conf/sprite pairing across all converted pets found ZERO mismatches -- The
Knight's "SitAndSpinHead" was verified frame by frame against its own sheet and
is faithful (the white oval is the mask spinning). It came out on taste, not
correctness.

Gate green: 0 warnings, 16 self-tests, no skips. 50 pets, 0 invalid, 0
round-trip failures, unreachable unchanged at 7 (all hand-authored).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `27f2d4341`

**docs: close out the 2026-08-28 session (v1.9.4, aibrain 1.2.3, blinkingled)**

```
Records the two things a future session would otherwise rediscover the hard way: the MSI bundles no modules at all, so merging to master IS the module publish and a host release is only for engine code; and a module cannot push a live value into an open pane or menu, which is why the ported countdown was dropped rather than shipped stale.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-28  `2708499cc`

**chore(catalog): blinkingled 1.0.2, two tray entries only**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `994b437ab`

**chore(modules): publish blinkingled 1.0.2**

### 2026-08-27  `4098d4548`

**blinkingled 1.0.2: drop the countdown and last-keypress readouts**

```
Both could only ever be a snapshot taken when the menu opens, because a module ships data and the host renders it, so there is no way to push an update into an open menu or pane. A stale countdown is worth less than the tray space it costs, and the tray is shared with the host and five other modules. Gone from the tray and from the options pane, along with the engine state that only fed them (MsUntilNextBlink, PhaseOn, the tick stamp, LastSentCount, HasResult).

Kept: LastWin32Error and ToggleCount, which back the 'Blink once now' button. That covers the one question the readouts genuinely answered, telling 'doing nothing' apart from 'being refused by Windows', and it reports the real error number. The self-test now pins the tray contribution at EXACTLY two entries rather than a minimum, since the point is restraint.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `480456700`

**chore(catalog): blinkingled 1.0.1, and document the remark pools and diagnostics**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `7c8603860`

**chore(modules): publish blinkingled 1.0.1**

### 2026-08-27  `2f0aa8eb5`

**blinkingled 1.0.1: a dozen remarks per speed, and the blink diagnostics**

```
One fixed line per speed stopped being funny the second time you saw it. Each
of the six speeds now has twelve lines, written to that speed's actual cadence
(Glacial really is one blink every four minutes; Hyper really is one a second),
so the jokes stay true if anyone re-tunes the intervals without re-reading them.
The picker never repeats the previous line: it steps to the pool neighbour
rather than re-rolling, so that is guaranteed by construction rather than by
luck.

Also restored the standalone app's two diagnostics, which are the only way to
tell "not blinking" from "blinking, but Windows is refusing the input":

  Next blink: 3.4s (lit)
  Last keypress: sent 2, 148 total

Both appear in the tray (via DynamicText, re-evaluated each time the menu opens)
and in the options pane as SettingKind.Info rows, refreshed on open and by
either button. They are labels, not buttons, with a null Click.

One honest limitation, called out in the code: this is a SNAPSHOT, not the
standalone app's 250ms live tick. That app owned its own menu; a module ships
data and the host renders it, and the ABI has no way to push an update into an
open menu or pane. Accurate when you look at it, then stale.

Testing, mutation-tested rather than trusted green:

  * Hardwiring the picker to pool[0] failed exactly "the picker eventually uses
    every line in the pool". This one mattered: the first version of that
    assertion only required "more than one distinct line", which the hardwired
    picker would have PASSED, because the no-repeat guard bounces it between
    indexes 0 and 1. Tightened to require the whole pool, which coupon collector
    makes a certainty over 200 draws (missing one is ~3e-7, a bug not a flake),
    and confirmed stable over five consecutive runs.
  * Removing the no-repeat guard failed exactly "the picker never repeats the
    previous line back to back", on all three runs.

Both restored. Gate green: 0 warnings, 16 self-tests, no skips.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `d33125912`

**chore(catalog): publish blinkingled 1.0.0, and document it in the README**

```
Sixth catalog module. The README entry carries the disclosure the consent screen cannot: it presses Scroll Lock, a key nothing acts on, and Windows counts that as activity.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `3d85c2bae`

**chore(modules): publish blinkingled 1.0.0**

### 2026-08-27  `ea6df58af`

**New module: Blinking LED, ported from the standalone tray app**

```
The pet keeps the machine reading as active by blinking the keyboard's Scroll
Lock light. The engine is the standalone BlinkingLED app's, essentially intact:
a two-phase timer toggling Scroll Lock through Win32 SendInput, with the six
rate presets (Glacial through Hyper) at their original on/off durations so a
user moving over keeps the cadence they already picked.

Everything AROUND the engine is deleted, because being a module supplies it.
The tray entry, the options pane, the settings file, single-instance behaviour
and start-with-Windows were most of the standalone app's ~1000 lines; the host
already owns all of them. What is left is the blinker plus the wiring.

What it actually does, stated precisely because the short description
understates it: this synthesizes a real keyboard event, not an LED poke. Scroll
Lock is chosen because it is inert, so no application receives a meaningful
keystroke and nothing is typed anywhere, but the event does travel through the
OS input queue and Windows counts it as user input. That is the point of the
tool. It needs no ModulePermissions flag because it never goes through the host
(the module P/Invokes SendInput itself), and there is no flag for synthetic
input to declare, so the disclosure lives in the module's description instead of
the consent screen. Declared permissions are Speech and Storage, which is
genuinely all it asks of the host.

Two deliberate differences from the standalone app, both because a module is not
a process. Caps Lock ON used to QUIT; here it stops the blinking and persists
that, since a module cannot quit the pet. And Stop() leaves the LED OFF rather
than wherever the cadence landed, so stopping mid-blink cannot strand the user
with a lit light and no obvious way to clear it.

Speech is deliberately narrow. The module auto-starts, and the pet has its own
opening line, so Init NEVER speaks: the quiet path is structural (ApplyState
takes an announce flag that only user-initiated paths pass) rather than a timing
guess. It speaks when the user switches it on or off, and makes a different
snarky remark per speed when the rate changes, from either the options pane or
the new tray rate submenu. An on/off change plus a speed change at once produces
ONE line, not two talking over each other.

26 assertions, mutation-tested twice rather than trusted green. Making Init
announce failed exactly "says nothing at startup" -- but only after the speech
assertions were rewritten to measure deltas instead of absolute counts, because
the first run cascaded a second failure off the shifted count. Removing the
speed remark failed exactly the two assertions about it. Both restored, gate
green (16 self-tests, no skips).

The self-test deliberately asserts nothing about the LED physically toggling:
that depends on the machine, the session and whether input is blocked, so it
would be flaky and would fail on a headless CI runner. It asserts everything
that can break without hardware, including that every advertised rate resolves
to a real interval and that the rates are ordered slowest to fastest.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `fe0734c34`

**docs(backlog): close the idle-commentary and OCR-scratch items shipped in aibrain 1.2.3**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `1063b2698`

**chore(catalog): regenerate for aibrain 1.2.3**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `106a7059d`

**chore(modules): publish aibrain 1.2.3**

### 2026-08-27  `896dd688d`

**aibrain 1.2.3: one schedule for unprompted commentary, not two**

```
The options window offered two ways to make the pet say something on its own,
and they looked duplicated because they largely were. AiBrain ran its own
90-150s idle timer with three settings of its own (Idle commentary, Idle min,
Idle max), while the host's global "Randomly drop a fortune / insight" already
reached this module through the drop responder at 12 +/- 3 min. With the brain
on and both enabled, the two ended in the same Ask(), the same model and the
same speech bubble with no shared cooldown; idle fired roughly 8x more often, so
the global drop became statistically invisible.

The module's timer and its three settings are gone. Unprompted commentary now
rides the host's global schedule only, so there is one control in one place.
The Ask hotkey stays, because it is the one trigger this module genuinely owns.

The screen-change gate does NOT survive, and that is a deliberate trade rather
than an oversight. It could not: the drop responder must answer SYNCHRONOUSLY
(its bool is what lets Fortunes take the tick instead), and the comparison is
async, so keeping it would have meant a background sampler, i.e. re-adding the
timer this change removes. Its purpose was also rate-limiting a fast loop, and
that pressure is gone at a 12-minute cadence. On a static screen the pet will
now comment anyway. AiBrain.ScreenChanged is kept, unused and labelled as such,
because it is exactly the primitive a future "only speak when something changed"
option needs; the AiSessionManager wrapper around it is deleted, since it was
built only for the removed loop.

_lastInteractionUtc would otherwise have become write-only, so it now guards the
place it actually matters: OnDrop declines when the AI spoke in the last 30s, so
a hotkey ask landing just before a drop yields a fortune rather than two model
answers back to back. Declining is better than going quiet because the responder
chain falls through to Fortunes.

Also hardened the OCR scratch file, prompted by asking where the screenshot
goes. Only the Tesseract path touches disk at all (the vision image is an
in-memory base64 PNG, and Windows OCR is explicitly memory-only); Tesseract
needs a real file because it is a child process. That file IS deleted in a
finally, which covers the normal and cancelled paths, but NOT reliably the
timeout path: there the process tree is killed and the delete runs immediately
after, so a child still dying can hold the handle, File.Delete throws, and the
catch swallows it. These are full screenshots, so repeated timeouts would leave
megabytes behind. Now swept on the next call, an hour old, same reasoning as
SelfTestScratch.

The security probe's clamp assertions moved off the deleted idle fields onto the
remaining int clamps, so that check still exercises more than one field.

Gate green: 0 warnings, 16 self-tests with no skips, including --aibrain-selftest
which covers the rewritten probe.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `c689fd8bb`

**chore(catalog): regenerate for petstudio 1.4.8**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `8ea69eece`

**chore(modules): publish petstudio 1.4.8**

### 2026-08-27  `9654eff0f`

**petstudio 1.4.8: pick up the ceiling region from the source-linked converter**

```
Pet Studio source-links the converter engine, so the ceiling work in 074f5fe changed its payload. The widened freshness check caught this on CI (petstudio 1 commit behind via PetEmitter.cs, ShimejiModel.cs and SpriteSheetBuilder.cs), which is exactly the blind spot it was widened to cover.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `1fe48e92d`

**docs: hand off the v1.9.4 session, and log the idle-commentary findings**

```
The AiBrain idle loop is misnamed: it gates on SCREEN CHANGE, not user idleness (there is no GetLastInputInfo call in the repo), so a genuinely idle machine gets nothing. Recorded alongside the orphan RandomDrop* fields in AiSettings and the real overlap between the two trigger groups when the brain is on.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `c6343f874`

**Regenerate the content catalog: 53 pets (Sonic out, capybara pair and Serial Designation J in)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `074f5fe13`

**Converted pets get the ceiling; swap Sonic for three richer skins**

```
The wall region shipped in 1.9.3 but stopped at the top of the screen: a pet
that climbed all the way up just let go. Every source skin already had the
ceiling animations (all 19 Android bundles carry climb_ceiling_left/right as
CEILING/HANG, and the desktop skins use the stock conf's ClimbCeiling and
GrabCeiling), so this was a converter gap rather than a format limit.

The obstacle was geometry, and it is why ceiling poses were excluded outright.
Ceiling sprites anchor at 64,48 rather than the 64,128 that Stand and Walk use,
because for a hanging mascot the contact point is near the top of the sprite.
Composited under the floor convention they hang from their feet, a whole cell
below the ceiling they are meant to be gripping.

The fix mirrors the floor argument exactly. At a horizontal border the engine
pins the WINDOW's top edge to the screen top, so ceiling frames are composited
with their anchor on the cell TOP, and the band above the anchor (which is
inside the ceiling) is skipped at the source rather than drawn into the
neighbouring tile. Because the ceiling anchor is SMALLER than the floor anchor,
this cannot raise max(AnchorY) and so costs no cell growth: the exact padding
failure the old exclusion existed to prevent. AnchorToTop is part of the sheet
FrameKey, so a skin reusing one sprite for a floor and a ceiling pose gets two
tiles instead of silently sharing one.

Topology: ceiling is entered ONLY by an only="horizontal" edge on the wall CLIMB
spoke, weighted so it wins about 2 in 3 against letting go. It cannot be reached
from the floor at all, since IsFloorAction rejects upward velocity and nothing on
the ground travels up to meet that border. It leaves by only="vertical" onto a
DESCENDING wall pose (not the climb, which would send the pet straight back into
the border it just left) or by a weighted drop to fall. Like the wall it omits
<gravity>, which is the cling, and it is time-budgeted by RepeatCountForBudget.

Mutation-tested. Dropping the AnchorToTop mark failed exactly the two pixel
assertions that check the ceiling frame sits at the tile top and not the bottom;
removing the horizontal entry edge failed the reachability assertion plus the
pre-existing unreachable-animation guard. The fixture makes the two anchor
conventions exact opposites (rows 0..35 vs 36..59), which is what gives those
assertions teeth. Also fixed the test's own hub-finding heuristic: "most
fan-out" silently stopped identifying the FLOOR hub once the wall region had two
spokes, so it now selects on the presence of <gravity>.

Pets: Sonic (shimeji-2l6qm2v5) is out. It carried a single stub wall action and
a single ceiling action against 6-7 and 3-4 for every other bundle, and produced
zero wall spokes, so it could not benefit from any of this. A scan of all 3165
catalog rows (2323 with a positive animation count) put three skins tied at the
maximum of 179; inspecting the archives showed one is a re-upload of Uzi Doorman,
which we already ship, and another is Capybara v2, a two-variant pack. So three
go in: Capybara (Brown), Capybara (Albino) and Serial Designation J, the last at
175 animations with the most ceiling content of any candidate examined.

All 31 converted pets re-converted from local sources. 53 pets, 0 invalid, 0
round-trip failures, and the converted set now has 0 unreachable animations
(the 7 remaining are hand-authored pets, unchanged). Hub weights hold at 740
options with the worst at 1.51% and none below the 1.5% floor. Hornet's
fall/grapple frame swap re-applied, since re-conversion rewrites the XML whole.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `8625d3eea`

**Fix stuck drag, character-accurate wall contact, and the self-test temp leak**

```
Three fixes plus a gallery gap, all found during the v1.9.3 smoke test.

Stuck drag: PictureBox1_MouseUp was the ONLY thing clearing IsDragging, and
NextStep re-snaps the pet to Cursor.Position every tick while it is set. Any
tool that steals mouse capture mid-drag (the reported case was a delayed
Greenshot capture; a lock screen or UAC prompt does the same) ate the MouseUp
and welded the pet to the cursor permanently. NextStep now polls the global
Control.MouseButtons state, which needs no capture, and releases through the
same new EndDrag() the real handler uses.

Character-accurate horizontal borders: the four horizontal border sites
compared raw window edges, but a converted shimeji floats inside a padded cell
(Hornet's standing frame occupies x=176..233 of a 256px cell), so she turned
around while still visibly inland. Border detection and the snap now use the
current frame's visible-pixel box via the SpriteBounds helper GetSpeechAnchor
already relied on, factored out as GetSpriteInsets so there is one definition
of where the character is. The drag grab is centred the same way. Hand-authored
pets fill their frame, get zero insets, and are unaffected.

Self-test temp leak: six self-tests staged modules into %TEMP% and all six
swallowed their delete. The four loading through a collectible ALC could never
succeed, because unload is asynchronous and the DLL is still mapped, so roughly
380 directories had accumulated. Cleanup is now deferred to the next run's
sweep (the PendingModuleRemovals trick) via SelfTestScratch, and a delete that
fails is REPORTED rather than swallowed, so a degraded run says so.

Mutation-tested, not just observed green. Making the sweep skip every directory
failed exactly one assertion, the aged-root case; dropping the age check so it
swept everything failed exactly one, the fresh-root case, which is the control
against a sweep that would delete a concurrent instance's scratch out from
under it. Both restored, exit 0, and %TEMP% went from ~380 leftover directories
to 1 (this run's, which reports itself as deferred).

Also: the Available-to-download cards showed only a name and author, so the
blurbs written for all 51 pets were invisible until after downloading. They now
use the same CompanionBlurbs line as the installed card, plus the download size.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `62bbaf052`

**chore(release): 1.9.3, refresh the README, and hand off the pet-quality session**

```
ProductVersion 1.9.2 -> 1.9.3. The host binary changes only by what the catalog cannot deliver: the 29 new pet blurbs, the two new gallery thumbnails, and ModuleKit's MemoryModuleSettings. Everything functional this session reached users through the catalog already.

README: the module list gained Remembrance (its section existed but the list did not mention it), and the pet count went from a stale '~20' to 51 with a paragraph on the 29 converted Shimeji skins.

handoff.md gets a START HERE for the session, written as warnings rather than a changelog: never pick a fixed repeat count (it has been the bug twice), the interval is also the animation's tick, rests round up while walking rounds to nearest, wall poses share the floor anchor but ceiling poses do not, and re-converting is the only way to change pet frames.

BACKLOG records the timing and anchor fixes plus the two things still open: the horizontal cell inset, and a pet that got stuck to the mouse once and was not reproduced.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `ff3454374`

**chore(catalog): regenerate for authored rest durations and petstudio 1.4.7**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `2436425f3`

**chore(modules): publish petstudio 1.4.7**

### 2026-08-27  `257004505`

**chore(petstudio): 1.4.7, picking up authored rest durations**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `4cdb22a4c`

**content(pets): re-convert all 29 with authored rest durations**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `d6cab8e16`

**fix(shimeji): a rest now lasts the duration the source authored, not a multiple of the interval cap**

```
Reported: the Knight read a book for 4 seconds when it should be 10.

Cause was quantisation. A single-frame hold has ONE interval, that interval is capped at MaxInterval (4s), and the dwell was then built by repeating it -- so the only reachable totals were multiples of 4. The reference conf authors these poses as Duration=250, exactly 10s, and the nearest reachable value was 8s. Every Stay pose in every converted pet shipped at 8s (or 4s where the budget rounded down).

Now a single-frame rest picks the fewest passes that keep each interval under the cap and divides the target evenly between them: 10s becomes 3 passes of 3333ms, exactly 10s on screen. Splitting rather than using one long interval matters because the interval is also the animation's tick, so a single 10s frame would mean 10s before the pet noticed it should fall.

The dwell target is now the duration the SOURCE authored, floored at ~9s so a short looping cycle (which Shimeji holds via the behaviour layer, not the action) still reads as a rest rather than a twitch. Multi-frame cycles keep the artist's per-frame pacing and only repeat, and they round UP so a rest never lands short -- undershooting is what reads as wrong, whereas running a little long just looks restful. Walking keeps nearest-rounding, where overshooting means gliding past where you expected it to stop.

The Knight after: Sit, Stand, Sprawl, SitAndLookUp, SitWithLegsUp and SitWithLegsDown are all exactly 10s. One-shots are untouched: Bouncing 0.32s, Tripping 2.4s, SitAndSpinHeadAction 1.6s.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `f32fb0751`

**chore(catalog): regenerate for the clip-fixed pets and petstudio 1.4.6**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `d7e67064a`

**chore(modules): publish petstudio 1.4.6**

### 2026-08-27  `570ae84b3`

**chore(petstudio): 1.4.6, so the clip fix is actually offered as an update**

```
The previous publish rebuilt the payload with the clip fix but left the version at 1.4.5, which means anyone already on 1.4.5 would never be offered it. That is the exact failure the freshness check's version-parity assertion exists to describe, and republishing without a bump walks straight into it.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `e1a1d47c5`

**chore(modules): publish petstudio 1.4.5**

### 2026-08-27  `ed399988f`

**feat(pets): a blurb for every converted shimeji pet**

```
The gallery shows a short quip per pet plus a stats line. The stats line already worked for converted pets (it is computed from the pet), but CompanionBlurbs had no entry for any of them, so all 29 fell back to the generic 'A delightful desktop companion' -- which made 29 of the 51 catalog pets look interchangeable next to the hand-authored ones.

One line each, in the existing tongue-in-cheek register and specific to the character: Hornet climbs walls and is unimpressed by your bugs, Zote has fifty-seven precepts and zero humility, Wooper has no idea what is going on. The one religious-figure skin gets a plain, respectful line rather than a joke.

Note this ships in the APP, not the catalog, so like the two new gallery thumbnails it only reaches users on the next release.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `b92d8accd`

**content(pets): re-convert all 29 to clear the cross-tile bleed**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `298a293b4`

**fix(shimeji): clip each frame to its tile, fixing the black blob I introduced**

```
Reported: a black blob in the top-left corner when grabbing Hornet. Mine, from the anchor fix one commit earlier.

Putting the anchor on the cell's bottom edge made the cell shorter, but BlitOpaque still drew the WHOLE scaled sprite at its placement offset. Any frame with pixels below its own anchor is therefore taller than its tile and bled into the neighbouring one -- so the bottom of one frame appeared in the corner of the next. Hornet's drag frame showed the tail of the frame above it, which is exactly the artifact reported.

BlitOpaque now takes the room remaining inside the tile (cell minus the frame's offset within it) and clips to it. The clipped band is below the floor line anyway, which is the same reasoning that justified shortening the cell.

Verified by extracting the drag tile as a PNG before and after and looking at it: the blob is present before and gone after. All 29 pets re-converted; verify still reports 51 pets, 0 invalid, 0 round-trip failures, unreachable held at 7.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `d9baa89fa`

**chore(catalog): regenerate for the anchor-fixed pets and petstudio 1.4.5**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `8577a18ac`

**chore(modules): publish petstudio 1.4.5**

### 2026-08-27  `b5ced9877`

**chore(petstudio): 1.4.5, picking up the anchor-on-cell-bottom fix**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `2bb873471`

**content(pets): re-convert all 29 with the anchor fix and the rest/wall time budgets**

```
Pets now stand on the floor rather than hovering, rests dwell ~9s instead of 2-4s (Hornet's Sprawl 2.4 -> 9.6s, BePet 0.2 -> 6.2s), and the wall climb is a 5s budget rather than a fixed repeat that ran 51s. One-shot performances (throw, bounce, trip) still play exactly once.

Verified in scratch before touching Pets/: 51 pets, 0 invalid, 0 round-trip failures, unreachable held at 7, and the 1.5% weight floor intact (worst animation 1.51%, 0 below). Hornet's fall/grapple frame swap re-applied by name.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `6e77fafa0`

**fix(shimeji): put the sprite anchor on the cell's bottom edge, so pets stand on the floor**

```
Reported: the sheep walks on the taskbar but Hornet hovers above it, as if it cannot detect the bottom. It detects the bottom fine -- both pet WINDOWS sat exactly on the work-area bottom (measured: sheep y=1340 h=40, Hornet y=1275 h=105, both ending at 1380). The artwork was drawn high inside its own window.

Cause: the compositor sized each cell as oy + below, reserving a band UNDER the anchor. But the Shimeji ImageAnchor is the mascot's ground-contact point, and the host stands a pet by putting its WINDOW's bottom edge on the floor -- and the window is one cell. So every converted pet floated by whatever elow happened to be. A hand-authored pet has a tight cell and no such gap, which is why the sheep looked right next to it.

Anything a source frame draws below its own anchor is below the floor line, so dropping that band is also what the original means. It makes the sheets smaller as a side effect.

Measured across all 29 re-converted pets: pets with bottom padding 6 -> 1, worst 20px -> 1px. Hornet's standing frame 14px -> 1px, its cell 256x105 -> 256x92; The Knight 128x152 -> 128x128; alipheese 235x256 -> 256x170.

NOT fixed by this, and it is the other half of what was reported as 'climbing away from the edge': HORIZONTAL inset. Hornet's standing frame still sits 176px into a 256px cell, so at the left screen edge the visible character appears inland. The cell cannot simply be trimmed -- across all frames the content fills it, so some pose genuinely needs the width -- and the compositor bakes the x offset into pixels because the format's <offsety> is y-only. Filed rather than guessed at.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `2e898fce7`

**chore(catalog): regenerate for petstudio 1.4.4**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `19c563c13`

**chore(modules): publish petstudio 1.4.4**

### 2026-08-27  `cc5994afc`

**chore(petstudio): 1.4.4, picking up the rest/wall time budgets**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `5655067ea`

**fix(shimeji): budget rest and wall animation time instead of a fixed repeat**

```
Reported: the sleep/rest animation does not last long enough. Two separate causes, both mine.

1. RESTS PLAYED ONCE. Every non-locomotion animation was emitted with repeat=0, so a rest lasted exactly frames x interval: Hornet's Sprawl 2.4s, its BePet 0.2s. Shimeji does not encode the dwell in the ACTION -- a Stay action is held by the BEHAVIOUR that runs it, and the behaviour layer is exactly what this converter does not reproduce -- so the dwell has to be supplied here, the way TargetLocoMs already supplies a walk length. New RestRepeatCount targets ~9s, applied ONLY to Stay-type actions so a one-shot performance (Animate: a trip, a bounce, a needle throw) still plays exactly once.

2. THE WALL CLIMB RAN FOR 51 SECONDS. I gave wall animations a FIXED repeat of 3, which on Hornet's 32-frame climb at 640..160ms is 51.2s of inching up ~256px, plus 12.8s hanging on GrabWall. That is precisely the mistake TargetLocoMs was introduced to prevent (its comment: a fixed count ran a slow animation for ~36s of gliding) and I walked straight into it. Now budgeted to ~5s per sequence.

Both now go through one shared RepeatCountForBudget(passMs, targetMs, maxRepeats), because picking a fixed repeat is wrong twice over -- a fast animation finishes instantly, a slow one runs for the best part of a minute -- and it has now been the bug twice.

Measured on a re-converted Hornet: Sprawl 2.4 -> 9.6s, Sit 3.2 -> 9.6s, BePet 0.2 -> 6.2s, ClimbWall 51.2 -> 12.8s, GrabWall 12.8 -> 6.4s, while ThrowNeedleAction (2.5s), Bouncing (0.3s) and Tripping (2.4s) are correctly unchanged.

The 29 shipped pets are NOT re-converted in this commit; that content pass is held until an open question about where climbing starts is settled.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `ce9882d4a`

**docs(backlog): record wall climbing, and what the ceiling still needs**

```
Written as guidance rather than a changelog: the cling mechanism (absence of <gravity>), why the wall region takes Group2, why cell geometry was the real risk and why ceiling therefore needs <offsety>, and the one thing that genuinely cannot be done (climbing an application window's side).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `64e1841f5`

**chore(catalog): regenerate for the wall-climbing pets and petstudio 1.4.3**

```
Rehashes all 29 re-converted pets and the petstudio payload. Users re-download those pets, which is how pet content updates already work.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `d485114f5`

**chore(modules): publish petstudio 1.4.3**

### 2026-08-27  `153fc4231`

**chore(petstudio): 1.4.3, picking up wall climbing**

```
Pet Studio source-links Emit\PetEmitter.cs, so a skin imported through the Studio must produce the same wall behaviour the CLI now emits. Flagged by the widened freshness check, again.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `3dfd801cf`

**content(pets): re-convert all 29 shimeji pets, 28 of them gaining wall climbing**

```
Re-converted from the ORIGINAL skins rather than migrated, because wall animations need new sprite frames baked into the sheet and the shipped XML cannot supply them. Every source was already local, so nothing was downloaded: 24 resolved through shimeji-catalog/data/catalog.csv (source_item_id -> blob_path), 3 from named zips at that root, and 2 from the Shimeji-EE bundle.

29 converted, 0 failures, 28 gained wall animations. The one exception is 2l6qm2v5, whose source skin has no wall animations at all.

Verified in a SCRATCH tree before touching Pets/: verify reports 51 pets, 0 invalid, 0 round-trip failures, and the unreachable count HELD at 7 (the same seven sheep recolours). The weight floor from the earlier pass survives re-conversion: worst animation 1.51%, 0 options below it. Cell geometry UNCHANGED for all 29 (256x256 before and after), which is the anchor-regression guard -- the growth is purely more frames (brq51bkr 30 -> 81 tiles), not padded cells. Budgets hold: 196 tiles against a 1024 cap, largest pet 9.4 MB against 12 MiB.

Catalog content grows 48.1 -> 62.6 MB total, and three pets roughly double or triple (brq51bkr +199%, 88f9sqb5 +124%, 06n2wuu6 +97%) because their climb cycles carry many frames. That is the honest cost of the animations.

Hornet's fall/grapple frame swap was re-applied, by NAME rather than by the old frame indices, since re-conversion rewrote the sheet layout: fall now carries [100,101,102,103] and Grapple3 [95].

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `540404944`

**feat(shimeji): converted pets can climb walls**

```
Converted pets stayed on the floor, and the residue report said wall and ceiling animations "are not
represented", which reads as a format limitation. It is not one. The engine handles this and it is the NORM
among hand-authored pets: 17 of the 22 shipped hand-authored pets use wall/ceiling/window transitions, and the
seven Oliver B. sheep (Ben, Gus, Omar, Pearl, Patsu, Rick, Yogurt) each carry 153 only="vertical", 48
only="horizontal" and 135 only="window" edges. Only the CONVERTED pets lacked it.

The mechanics were read off those working pets rather than guessed:
  * entry is a border edge on a locomotion animation (run --border only="vertical"--> wall_slide_run)
  * the CLING is the absence of <gravity>. Presence of that element is what tells the engine to fall when
    nothing is underneath, so omitting it is what keeps a pet on a wall.
  * climbing is simply negative Y velocity

Implemented as a second REGION, not more spokes on the floor hub:
  * IsWallAction beside IsFloorAction. It deliberately does NOT inherit the floor's rejection of upward
    velocity: on the floor a negative VelY launches the pet off the top of the screen, which is why that guard
    exists, while on a wall climbing up IS the behaviour.
  * It accepts Group1 AND Group2. Group2 means "the selection CONDITION needs host state we do not have", not
    "the animation is unconvertible", and the wall region replaces Shimeji's conditional selection with its own
    border graph anyway. This was found the hard way: a Group1-only filter took GrabWall but not ClimbWall
    (Group2 because its condition reads mascot.anchor), producing a pet that grabs a wall and hangs there
    motionless. Group3 stays out, being Embedded classes.
  * Wall spokes are unreachable from the floor hub, so a wall-cling can never play mid-screen -- the actual
    reason wall actions were excluded outright before.
  * A locomotion animation's border edge is now weighted: turn (only="none", eligible at every border) versus
    climb (only="vertical"), so climbing wins 1 in 3 at a left/right screen edge and behaviour everywhere else
    is byte-identical to before.
  * Wall animations keep only the vertical velocity component (horizontal motion would walk the pet off the
    wall), omit <gravity>, and exit to the existing `fall` magic animation on their border or by weighted
    choice.

Sheet geometry, which is what made this look risky: PosesToComposite excluded wall poses with the comment "a
tall ceiling-pose anchor pads the cell, the pet floats, and ground detection breaks". That concern is real but
applies to CEILING poses only -- the reference conf anchors ClimbWall and GrabWall at the same 64,128 as Stand
and Walk, while GrabCeiling/ClimbCeiling anchor at 64,48. Verified empirically across all 29 pets after
re-conversion: cell size UNCHANGED for every one of them (still 256x256 where it was 256x256). Ceiling is
therefore deliberately still excluded and is a separate piece of work.

Residue wording fixed, as flagged: it now says wall climbing IS converted and lists what converted for that
skin, and describes ceiling/jump as "not attempted yet ... a converter gap rather than a format limit" instead
of implying the format cannot express it.

Self-tested, and the assertions were MUTATION-TESTED rather than trusted: the synthetic fixture gained a
Wall-border action carrying a Condition (so it is Group2, exactly like the real ClimbWall), and four
assertions cover the cling (no <gravity>), the upward motion, unreachability from the floor hub, and the
presence of an only="vertical" entry edge. Injecting a <gravity> node into wall spokes made the suite fail
with exactly "wall animation has a <gravity> node, so the pet would fall off the wall instead of clinging";
restored and green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `786d5a2bb`

**fix(pets): gallery thumbnails for The Knight and Zote**

```
Reported: both new pets render with no icon in the 'get more pets' gallery while Cartman has one.

My earlier claim that no app rebuild was needed for a new pet's thumbnail was WRONG. I checked the fallback and not the case that actually matters: CompanionsPaneControl.LoadThumb falls back to a pet's own <header><icon> via LoadPetHeaderIcon, which needs FindPetXml to locate a pet already ON DISK. In the download gallery the pet is by definition not downloaded, so the embedded pet-thumbnails.zip is the only possible source, and it was built with the previous 49 pets.

Lifted each pet's header icon out as PNG bytes (the converter stamps a PNG inside the ICO container, so the payload is sliced at the PNG signature rather than re-encoded) and added both to the zip: 49 -> 51 entries, 48x48 each, matching the existing entries.

Note this only reaches users on the next app release, because the zip is an embedded resource. Until then the two cards show name + author + Download and work correctly, and an icon appears as soon as the pet is downloaded (the on-disk fallback then applies).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `60362a83f`

**docs(backlog): record the hub-weighting fix, widen the temp-leak entry to 3.2 GB**

```
The weighting entry is written as a warning, not a changelog: reachability analysis cannot catch this class of bug (it proves an animation CAN play, not that it ever does), the hub self-edge must stay excluded from the floor, and the migration's version gate is load-bearing rather than cosmetic.

The self-test temp leak turned out to be far larger than the 127.5 MB first measured: 3.2 GB across 387 orphaned %TEMP%\dp-* dirs, dominated by dp-aibrain-selftest (179), dp-petstudio-selftest (95) and dp-modulefail-selftest (72). Cleaned 348 dirs / 2.87 GB by hand; it returns on every gate run until fixed. CI runners are ephemeral, which is why nobody noticed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `89b7d44d0`

**chore(catalog): regenerate for 51 pets and petstudio 1.4.2**

```
Picks up the 27 reweighted pets, Hornet's frame swap, the two new Hollow Knight pets (49 -> 51), and the petstudio 1.4.2 payload. The 29 changed pet hashes mean users re-download those pets, which is how pet content updates already work.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `be0cf9f27`

**chore(modules): publish petstudio 1.4.2**

### 2026-08-27  `1eb38e0f4`

**chore(petstudio): 1.4.2, picking up the fixed hub weighting**

```
Pet Studio SOURCE-LINKS Emit\PetEmitter.cs, so 0623d3f's damped + floored hub weighting changes its payload: a skin imported through the Studio must produce the same weighting the CLI now emits, or the two would silently disagree. No other behaviour change.

Found by the widened publish-freshness check (c1b96fd) rather than by anyone remembering that PetStudio compiles 7 files out of src/ and 13 out of tools/. This is the first time that fix has caught a real change rather than a mutation test.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `44f800cdc`

**content(pets): reweight all 27 converted pets, swap Hornet's fall/grapple frames, add The Knight + Zote**

```
Three things, all pet content.

1. REWEIGHT (27 pets, via `ShimejiConvert reweight Pets`). Applies the damped curve + 1.5% floor from
   0623d3f to every already-converted pet. Result across the corpus, counting animations only and excluding
   each hub's deliberately-pinned self-edge:

       worst single animation   0.03%  ->  1.51%
       options below 1%          368   ->  0
       worst max:min ratio       326x  ->  22.4x
       mean top-3 share          66%   ->  47%

   In practice Hornet's rarest animation goes from ~54 minutes of idling to ~3.4 minutes. The 22
   hand-authored pets (the sheep, neko, goku, ...) were correctly skipped by the author gate and are
   byte-identical. Every touched pet is now at header version 1.1, so a re-run is a no-op.

2. HORNET fall/grapple frame swap, by request. `fall` takes frames 96-99 and `Grapple3` takes frame 91, so
   the grapple artwork is what you see on every drop and fall instead of 0.8% of idle picks.

   Only the FRAMES moved. `fall` is one of the format's four magic names and the engine plays it whenever a
   pet is dropped or unsupported; its y=10, self-loop and border->Stand are what make falling work at all.
   Swapping the NAMES (the literal reading of the request) would have left dropped Hornet hovering through a
   4-frame stationary flourish with the real falling animation demoted to a rare idle pick, so it was raised
   and rejected rather than shipped.

   `fall`'s interval deliberately stays at 40ms. Interval controls frame cadence AND fall speed (y=10 per
   step), so slowing it to make the grapple frames more readable would have made Hornet fall 2-3x slower.
   Preserving the physics wins; 4 frames at 40ms reads as a fast fall.

3. TWO NEW PETS from a locally-supplied Shimeji-EE bundle: The Knight (img/Ghost) and Zote (img/Zote). The
   bundle's third skin is Shimeji-EE's own default sample art and is deliberately not shipped. Both convert
   ACCEPTED: 19 animations, valid, round-trips, 0 unreachable, and they are born at header 1.1 with the floor
   already applied (rarest animation 1.67%), so they need no migration.

   The bundle carries the STOCK reference conf (91 actions, 53/32/6 -- the exact census the engine self-test
   pins), so residue is the known set: 6 dropped (the four window-throwing IE actions plus two Breed
   self-cloning actions) and 32 degraded. Wall, ceiling and jump behaviour is not represented, so both stay
   on the floor -- hence 19 animations against Hornet's 25.

   pets.json follows the existing "Local import" convention already used for Alipheese and Loona, since the
   download names no skin author. NOTE for the maintainer: these are fan-made Hollow Knight (Team Cherry)
   character sprites, so they land under the sprite-redistribution item THIRD_PARTY_NOTICES.md already lists
   as unresolved and which you accepted for the existing 27. No app rebuild is needed for their gallery
   thumbnails: a pet absent from the embedded pet-thumbnails.zip falls back to its own header icon.

Verified: `ShimejiConvert verify Pets` reports 51 pets, 0 invalid, 0 round-trip failures, and the
unreachable-animation count HELD at 7 (the same seven sheep recolours as before, so nothing regressed).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `0623d3f7f`

**feat(shimeji): damp + floor the hub weighting, so a converted pet's animations actually play**

```
Chasing "why does Hornet's Grapple3 never play" found a systemic problem in every converted pet. The emitter
set each hub transition to HubBaseWeight(4) + accumulated behaviour frequency, and BuildSpokeWeights SUMS a
frequency every time a behaviour references an action. Locomotion is referenced by many composites, so it
accumulated to ~1100 while a one-off pose stayed at 4. Nobody chose a 326x spread; it fell out of the summing.

Measured over the 27 shipped converted pets, excluding each hub's own re-selection edge:

    582 animation options, 368 of them below 1% of their hub's pool, worst at 0.03%

At Hornet's real ~3.2s idle cadence, 0.03% is one appearance per ~54 MINUTES of idling. A dozen of its best
animations were unreachable in practice. Reachability analysis never caught this and never could: it proves
an animation CAN play, not that it ever does.

Two changes, together:
  * HubWeightFromFrequency damps with a square root (4 + round(3*sqrt(f))). Preserves ORDERING, so a
    character that walks a lot still walks a lot, while collapsing the range to ~10-25x.
  * ApplyMinimumShare then lifts the tail until nothing holds less than 1.5% of the pool. This is the part
    that gives a guarantee rather than an improvement.

Chosen by simulating four candidate curves against the real committed pets before writing any code:
sqrt alone left 88 options under 1%; log left 72; sqrt+floor leaves 0. Hornet's rarest goes 54 min -> 3.4
min while its top three still take 48% of picks, so pets stay recognisably themselves (mean top-3 across the
corpus 66% -> 47%).

Two things this had to get right, both easy to get wrong:
  * The hub's own re-selection edge is EXCLUDED from the floor. HubWeightFor deliberately pins it to the
    baseline because the hub is also every spoke's RETURN target, so lifting it makes the pet loiter on the
    hub instead of getting on with the next action. It is also why the migration reports the rarest real
    ANIMATION rather than the rarest edge -- including the self-edge reports ~0.6% for a pet whose animations
    are all above the floor, which reads as a bug and is not one.
  * The curve is shared, not duplicated. Both statics are public so the migration applies the identical
    curve, following the LocoRepeatCount precedent that Rebalance already reuses.

New `reweight <PetsDir>` verb migrates already-converted pets. It needs no access to the source skins (which
are deliberately not in this repo) because the old formula makes the source frequency recoverable as
(probability - HubBaseWeight). That recovery is exactly why it must not run twice, so it is gated on the
pet's own header version: converted pets were 1.0, the migration writes 1.1 and skips anything already at
1.1, and the emitter now stamps 1.1 so new conversions are never re-curved. Second gate is
header/author == "Converted from a Shimeji skin", so a hand-authored pet can never be touched.

New HubWeightSelfTest pins all of it through `ShimejiConvert selftest` (which run-gate already runs): 14
assertions covering monotonicity, the damping, the floor holding, floor idempotency, the excluded hub edge,
and degenerate input (null/empty/all-zero, plus an unsatisfiable floor that must terminate rather than spin).

Verified: selftest 14/14; trial run on a COPY of Pets/ before touching the repo (27 reweighted, 22
hand-authored correctly skipped, 0 failures); re-running is a no-op (0 reweighted, 49 skipped).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `7c71cd4f7`

**docs(backlog): file the --module-selftest temp-dir leak**

```
The harness copies the module payload to %TEMP%\dp-module-selftest-<guid> and deletes it in a finally, but the collectible AssemblyLoadContext still holds the DLL, so the delete fails and is swallowed. 36 orphaned dirs totalling 127.5 MB on the dev box, cleaned by hand. Dev-flag only. Fix shape: delete on the NEXT run, the way PendingModuleRemovals already handles the identical DLL-lock problem.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `b49036fd7`

**docs: correct three stale backlog claims, add a START HERE for 2026-08-27**

```
BACKLOG.md corrections, each of which had real cost:
* the pet-reaction entry asserted 'the plugin ABI does not let a module drive a specific pet animation', which is false and sent this session planning a host release it did not need. IHost.TryPlayAnimation / PlayAnimationAll have existed since the emotion work.
* the shimeji entry still said 'awaiting master' months after it merged; the catalog serves 27 shimeji pets.
* the freshness blind-spot entry undercounted PetStudio's source-linking as 'four files out of src/' (it is 7 from src/ and 13 from tools/), and the real staleness turned out to be the bundled ModuleKit, not source-linking at all.
Also files the new --module-selftest first-match hazard, and records that moving a pet remains the one genuinely missing ABI verb.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `b5d22542c`

**chore(catalog): regenerate for the five republished modules**

```
Hashes the committed modules-dist zips for fortunes 1.2.4, aibrain 1.2.2, petstudio 1.4.1, reminder 1.7.0 and remembrance 1.1.0. Merging this to master is what makes them live for every user.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `6df9f36c2`

**chore(modules): publish remembrance 1.1.0**

### 2026-08-27  `88487eef1`

**chore(modules): publish reminder 1.7.0**

### 2026-08-27  `4e48d83ce`

**chore(modules): publish petstudio 1.4.1**

### 2026-08-27  `93f784972`

**chore(modules): publish aibrain 1.2.2**

### 2026-08-27  `0587eb27b`

**chore(modules): publish fortunes 1.2.4**

### 2026-08-27  `cd07f56e0`

**chore(modules): bump fortunes/aibrain/petstudio for a stale bundled ModuleKit**

```
Payload refresh only. No behaviour change in any of the three.

All three ship a copy of ModuleKit inside their own folder (its ProjectReference is deliberately NOT
Private="false", so each collectible load context carries its own), and all three had a copy 3-4 commits
behind: the styled-speech setting prefixes, the shared-context RecordingHost, and now
MemoryModuleSettings. Nothing reported it, because Test-ModulePublishFreshness only watched
modules/<Id>/ until c1b96fd widened it to bundled and source-linked paths. Its first widened run named
all three.

The version bump is not cosmetic: the in-app Update button compares the live ModuleInfo.Version against
the catalog's, so republishing the same version would fix the payload for new installs and never offer it
to anyone who already has the module. That is the same trap the freshness check's own version-parity
assertion exists for.

  fortunes  1.2.3 -> 1.2.4
  aibrain   1.2.1 -> 1.2.2
  petstudio 1.4.0 -> 1.4.1

Committed before publishing, because New-ModulePublish refuses to publish a module with uncommitted
source: the freshness check compares commit RECENCY, and a deterministic re-zip cannot produce a new
commit to repair the ordering afterwards.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `c77c6c3c6`

**feat(reminder): the pet physically reacts when a reminder fires (1.7.0), + gate both new modules**

```
A reminder was a speech bubble and a chime, which is easy to miss and invisible behind a fullscreen window.
Now the pet plays an attention animation as the reminder fires.

NO HOST CHANGE AND NO RELEASE NEEDED, contrary to what BACKLOG.md claims. The backlog says "the plugin ABI
does not let a module drive a specific pet animation or move a pet today, so this is a host-release item".
The first half is simply wrong: IHost.TryPlayAnimation(ICompanion, name) and IHost.PlayAnimationAll(candidates)
have existed since the emotion work and are fully wired in CompanionHost (:216, :231). Only MOVING a pet would
need new ABI, and that is deliberately not attempted here: pet position is driven by animation velocity
expressions rather than set directly, so a "walk to centre screen" verb would fight the engine and is a
much bigger piece of work than it sounds. The backlog entry is corrected in a follow-up.

  * PlayAnimationAll picks, per pet, the first candidate that pet's XML actually defines, so the MODULE owns
    the mapping and the host needs no new verb -- the same division AiBrain uses for its emotion map. An
    ordered candidate list rather than one name because the shipped pets and a converted shimeji define
    entirely different animation names; a pet defining none of them is a silent no-op, which is the right
    outcome inside a timer tick.
  * `reactOn` (default on) and `reactAnimations` (default "boing,jump,run,flower").
  * Fires BEFORE the bubble: the animation exists to pull the eye to the pet, which is pointless once the
    thing you were meant to look at is already on screen.
  * Also fires from the per-slot Test button, because waiting for a real calendar event is a poor way to
    find out whether your pets animate, and that button is the only on-demand trigger.
  * Permissions declare Animation. CompanionHost does not actually gate PlayAnimationAll (only Audio and Network
    are enforced), but the pre-install consent list is built from that field, so omitting it would
    under-disclose what the module does to the user's pets.

Second half of this commit: both new modules are now GATED, where before neither had a self-test at all.

  * ReminderModule.SelfTest aggregates the six pure helpers -- QuietHours, ReminderScheduler,
    AggregateCalendarSource, MeetingLinkDetector, PersonalReminder, PersonalReminderParser -- whose internal
    checks already existed and which NOTHING ever ran. They were exercised once from a throwaway console and
    left unwired, which is indistinguishable from having no tests. Plus new coverage of the 1.7.0 candidate
    parsing.
  * Those six are RENAMED SelfTest -> SelfCheck, and that rename is load-bearing rather than cosmetic: the
    app's --module-selftest convention reflects over EVERY type in the assembly for `bool SelfTest(out
    string)` and takes the FIRST match, so any one of the six could have won over the module's own aggregate
    entry point, non-deterministically by metadata order. Remembrance was unaffected only because it happens
    to have exactly one such method. This is a sharp edge for third-party module authors too and is filed.
  * tests\run-gate.ps1 and .github\workflows\build.yml both gained --module-selftest=reminder and
    --module-selftest=remembrance (16 self-tests now, was 14), and the gate's module-presence list gained
    reminder + remembrance. That list is what stops a self-test SKIP-passing on a module the build silently
    failed to produce, and both were missing from it.

Verified: --module-selftest=reminder exit 0 (50 lines, all six suites plus the new checks),
--module-selftest=remembrance exit 0 (63 assertions). Both through the real module loader.

NOT verified here: the reaction eyeballed on screen with live pets, which needs the running app.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `593be258d`

**fix(modulekit): MemoryModuleSettings, so a host that declines settings cannot fail a module's load**

```
Both new modules crashed at LOAD time, reported only as the near-useless "module did not load: <id> --
NullReferenceException". Cause: a module does `_settings = host.GetSettings(Id)` in Init and then uses it
freely, but a host may answer null -- and the app's OWN --module-selftest harness does exactly that
(ConventionHost returns null from both GetSettings and GetStorage). Anything reading settings during Init
then dereferences null:

  * Remembrance builds its options SCHEMA in Init, and the summary-model dropdown needs the saved model
    name to union into its options, so the schema read the store.
  * Reminder calls MigrateLegacy() on the line after GetSettings.

Worth noting this was latent rather than new: Remembrance 1.0.0 and Reminder 1.6.0 both shipped to the
catalog with this, and nothing caught it because neither module had a self-test to run in the first place.

The ABI's convention for a refused service is to DEGRADE rather than throw into a module (GetCompanionManager
returns a refusing instance, RegisterHotkey a no-op handle). MemoryModuleSettings is that convention for
settings: reads return the caller's fallback, writes are kept for the object's lifetime, and Save returns
false because nothing was persisted -- a caller that reported success off a true there would be lying.

In ModuleKit rather than copied into each module, which is its stated purpose ("the helpers every module
was copying by hand"), and it is genuinely useful to any third-party author who hits the same wall. It is
NOT in the Testing namespace on purpose: a test double asserts behaviour, this survives a host that gave
you nothing. Public API, so it also reaches out-of-repo authors through the ModuleKit nupkg.

Note this touches ModuleKit, which is bundled into every module payload, so it stales all five published
zips -- correctly reported by the freshness fix in c1b96fd. Three of them (fortunes, aibrain, petstudio)
were already stale on ModuleKit before this change, and all five are republished in this session.

Verified: --module-selftest=remembrance now loads and passes 63 assertions where it previously failed at
"the real loader accepted the module".

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `3bc62abe7`

**feat(remembrance): one-click Whisper setup + local AI summary (1.1.0)**

```
Setup friction, not a missing feature, was what stopped anyone else testing this module: it took two file
paths and offered no way to obtain what they point at, so a tester had to go install a C++ binary and a
141 MB model by hand before the pet did anything at all.

WhisperInstaller: detect first, download only if needed.
  * probes the module's own storage and then %LOCALAPPDATA%\DevToolbox\whisper, which is where
    scripts-utilities\scripts\install-whisper.ps1 puts things, so an already-provisioned box is adopted
    rather than re-downloaded
  * otherwise resolves whisper-bin-x64.zip from the whisper.cpp GitHub release (verifying the SHA-256 from
    the asset digest when the API supplies one), extracts it, and fetches the chosen GGML model from
    Hugging Face, with percentage progress on the module status line
  * model choice tiny.en / base.en (default) / small.en
  * then PROVES the pair works by running the real whisper-cli against generated silence. Exit code 0 is
    the assertion, not transcript content: silence legitimately transcribes to nothing, so requiring text
    would fail a working install. What this catches is the part that actually breaks, namely the exe not
    resolving its DLLs or the model not loading.
  * nothing is redistributed by this repo, nothing is installed machine-wide, and it all happens on an
    explicit user action

OllamaSummarizer: backlog #17's P3, closing the record -> transcribe -> summarize pipeline. Writes
<capture>.summary.txt beside the transcript, off by default. Map-reduces long transcripts (summarize each
~6000-char chunk, then merge) so a one-hour meeting does not overflow a small local context window. Only
generation-capable models are offered: Ollama's per-model capabilities array where present, else a name
heuristic, because this box alone serves three embedding models that can never generate.

LOCAL ONLY, deliberately and permanently: no cloud provider, no key field, and no code path that could
acquire one, because a meeting recording can be privileged or consent-regulated audio.

Deliberately NOT source-linked from modules/AiBrain's OllamaClient (412 lines, and it drags in
AiEndpointPolicy, ICompanionBrainBackend, BrainResponse, JsonRead and ModelListing). A module cannot reference
another module, so adopting it would mean source-linking five files across a boundary -- the exact
shared-source staleness the freshness fix in c1b96fd exists to catch, for one non-streaming POST. The
security posture IS copied: a no-redirect handler, so a reply cannot bounce the transcript elsewhere.

Two smaller things:
  * CaptureStore now names a Summary path, and IsEphemeral lists transcripts AND summaries as permanent
    explicitly rather than letting them survive by falling through the extension test.
  * Init tolerates a host that declines a settings store. This is not hypothetical: the app's own
    --module-selftest host returns null from GetSettings, and the options SCHEMA is built during Init and
    needs the saved model name for its dropdown, so an unguarded null took the whole module down with a
    NullReferenceException at load. It now falls back to an in-memory store, which is the same degrade-
    rather-than-throw convention the ABI documents for GetCompanionManager and RegisterHotkey.

Permissions gain Network, which is user-visible on update and intended. It covers exactly two
user-initiated calls: the upstream Whisper fetch, and a LOOPBACK Ollama for the summary.

Verified live on this box, not just built:
  * --module-selftest=remembrance PASSES, 63 assertions through the real module loader (new, see below)
  * detect: falls through to the DevToolbox install and finds whisper-cli + ggml-base.en.bin
  * verify: real whisper-cli runs against the real model, ok in 1s
  * download: full install into a THROWAWAY temp root (real install untouched, then deleted) resolved the
    release, hash-checked the zip, extracted, pulled ggml-tiny.en.bin (77,704,715 bytes) with progress, and
    passed the run check, in 3s. Note the Hugging Face LFS redirect that defeated aria2c in an earlier
    session is handled correctly by HttpClient.
  * summarize: 38,983-char transcript -> 7 chunks -> map-reduce with nemotron-3-nano:4b in 18s, and the
    output correctly surfaced the decision, the owned action item and the open question, all three of which
    sat at the END of the transcript. That is the assertion that matters: late content survives the reduce.

NOT verified here and stated plainly: live audio capture. This is an RDP session, which presents no real
microphone (0 capture devices), so start/stop of actual recording still needs the machine's own console.
The module already warns about that in its status line.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-27  `c1b96fdbb`

**fix(packaging): freshness check watches linked + bundled paths, not just modules/<Id>/**

```
Test-ModulePublishFreshness existed to catch a published module payload going stale, but it compared
commits against modules/<Id>/ ALONE. That is blind to the two ways a payload changes without the module's
own folder being touched, and both are live in this repo:

  * source-linked files. modules/PetStudio compiles 7 files out of src/ and 13 out of
    tools/ShimejiConvert.Engine/, so editing src/dotNet/CompanionXmlValidator.cs rebuilds PetStudio.dll while
    this check stayed green -- the exact bug class it exists to catch, arriving through shared sources.
  * bundled ProjectReferences. ModuleKit is referenced WITHOUT Private="false", so its DLL is copied into
    every module folder and ships in every zip; one ModuleKit edit stales all five payloads.

New Get-ModuleWatchSet derives the set from the csproj instead of hardcoding it, so a module that starts
linking something new is covered with no edit here. ProjectReferences are followed recursively and those
marked Private="false" are skipped, which is exactly the "host owns the single shared copy" marker -- so
DesktopPet.Contracts drops out on its own and no module id needs special-casing. On failure the report
names WHICH watched path carries the newer commit, so the fix is obvious rather than a guess.

Deliberately out of scope, stated so nobody "completes" it later: ProductVersion.props. ModuleKit stamps
its assembly Version from it, so a host bump does change the bundled DLL's bytes, but demanding five
republishes per release for a version field and no functional change would make this gate hostile enough
to be routed around. Includes using MSBuild's GeneratePathProperty convention are skipped silently (those
always resolve into the NuGet cache, never the repo); any OTHER unresolved MSBuild property warns on every
run rather than quietly shrinking the watch set, which is how this went blind in the first place.

MUTATION TESTED (a green run proves nothing here). In a throwaway worktree, 3 positives + 1 negative
control, all as expected:
  * mutate src/DesktopPet.ModuleKit/AtomicFile.cs -> remembrance goes STALE naming src/DesktopPet.ModuleKit
  * mutate src/dotNet/CompanionXmlValidator.cs          -> petstudio names src/dotNet/CompanionXmlValidator.cs
  * NEGATIVE CONTROL: mutate src/dotNet/FormCompanion.cs, which no module links -> remembrance stays GREEN,
    proving the set is targeted rather than "watch everything"
Two false FAILs during that exercise were my harness, not the check, and are worth knowing: a worktree
created from HEAD materialises the COMMITTED script (so an uncommitted fix is not under test), and the
check reports detail via Write-Host, which is invisible to a 2>&1 capture and needs *>&1.

Widening the set immediately reported fortunes, aibrain and petstudio as stale: all three ship a ModuleKit
3-4 commits behind. That is the check working, and those republishes follow in this session.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `443284314`

**fix(installer): reinstall a rebuilt same version + sheep banner**

```
Interior/progress dialogs showed the app's red emblem (default WixUIBannerBmp); replace it with a white banner carrying two sheep on the right, title area clear (installer/banner.bmp).

Reinstalling a rebuilt SAME version failed with Windows Installer 1638: the ProductCode was version-only, so a content change (the banner) produced an identical ProductCode with a different PackageCode, which msiexec rejects before upgrade logic runs. Fix folds the payload's base content hash into the ProductCode seed (Normalize-MsiDeterminism.ps1) so a changed same-version build gets a distinct ProductCode, and sets AllowSameVersionUpgrades on the MajorUpgrade so it replaces the prior install. That makes the Upgrade VersionMax inclusive (trips ICE61), suppressed alongside ICE91 in build-installer.ps1.

Determinism preserved: two consecutive Release builds produced a byte-identical MSI (SHA-256 0F932C57...); ICE validation and the upgrade-schedule self-test pass. Releases always bump the version, so the release-to-release upgrade path is unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `2ac3b2a6f`

**chore(release): bump ProductVersion to 1.9.2**

```
Carries the installer welcome/finish pasture sidebar + sheep product icon to a
release. No app-code change; patch bump.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `c36004821`

**docs(readme): pasture hero, fun intro (modular + AI-optional), scattered pets**

```
New showcase hero at the top, a punchier intro framing the two design choices (a lean
modular install from an in-app catalog, and AI-optional/local-first), and a handful of
pet icons floated through the module sections. Per-module sections, install, credits,
and build docs unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `617151679`

**feat(installer): pet-pasture welcome/finish sidebar + sheep product icon**

```
Replaces the default WiX dialog bitmap with a 493x312 scene (blue sky, clouds, a
green hill, a row of pets) via WixUIDialogBmp, and repoints ARPPRODUCTICON from the
app exe's red emblem to a multi-size sheep icon. dialog.png is the editable source;
dialog.bmp (24-bit) is what WiX consumes. Verified: the MSI builds and passes ICE
with the locked wix 5.0.2.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `ca1a2963c`

**ci(release): prune to the 3 most recent releases after each publish (tags kept)**

```
Adds a best-effort post-publish step that deletes release objects beyond the newest
three, keeping the git tags (so any pruned release rebuilds from its tag via
workflow_dispatch). Automates the manual trim; also limits how many redistribution-
unresolved binaries are downloadable at once.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `a058b5df9`

**docs(handoff): record v1.9.1 (speech-anchor fix) + the catalog UTF-8 fix**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `5a23618e1`

**chore(release): bump ProductVersion to 1.9.1**

```
Carries the speech-bubble anchoring fix (anchor over the visible sprite, not the frame)
to a release so other workstations get it. No ABI change; patch bump.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `9aa875192`

**fix(speech): anchor the bubble over the visible sprite, not the whole frame**

```
A converted shimeji floats inside a larger padded/transparent cell (poses are padded
to the largest pose's box), so anchoring the speech bubble to the frame rectangle put
the tail out over empty padding and the bubble detached from the character (Hornet was
the report). New SpriteBounds computes the tight bounding box of a frame's visible
pixels (non-key colour for colour-keyed pets, alpha>threshold for alpha pets), cached
per frame image via a ConditionalWeakTable. GetSpeechAnchor maps that box into screen
coordinates (accounting for per-pet scale) and anchors there. Built-in pets are
unaffected (their visible box fills the frame). Verified: SpriteBounds.SelfTest
(colour-key + alpha) passes; full gate green.

Host change; rides the next release, testable in the local build now.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `33862ed7a`

**fix(catalog): read source JSON as UTF-8 so non-ASCII pet/pack names survive**

```
New-ContentCatalog.ps1 read pets.json / collections.json / pack-names.json /
modules.json with Get-Content -Raw and no -Encoding, so under Windows PowerShell 5.1
(ANSI default) a UTF-8 name was mis-decoded and written back mangled (the write side
was already UTF-8). Forced -Encoding UTF8 on the reads and regenerated: the Cyrillic
shimeji name and an accented fortune-pack name are now correct. Names only, no hashes.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `168654a19`

**docs: Remembrance is now published to the catalog (needs host 1.9.0)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `ee07ef93a`

**chore(modules): publish remembrance 1.0.0 to the catalog**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `9f485152d`

**chore(modules): publish remembrance 1.0.0**

### 2026-08-26  `752c995a6`

**docs: Remembrance module (Readme blurb, backlog status, handoff START HERE)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `fecb540cf`

**feat(remembrance): warn on Remote Desktop sessions + show live device counts**

```
The device dropdowns are read once at module load, so under a Remote Desktop
session (which presents no real mic/speakers, only 'Remote Audio' and zero
capture devices) they looked empty with no explanation. The options status line
now reports live output/mic counts and, when SystemInformation.TerminalServerSession
is set, says plainly that recording won't work here and to use the machine's console.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `fa4d8a3c3`

**docs(remembrance): record build status + decisions + the two pause points**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `5d0ce85b5`

**feat(remembrance): the meeting-recorder module (source; Stage 1 + 2)**

```
New modules/Remembrance (v1.0.0, MinHostVersion 1.9.0, permissions Microphone |
SystemAudio | ScreenContext | Hotkey | Storage):

- Records a selectable microphone + the system output (WASAPI loopback), each to a
  temp WAV, then mixes offline to one 16 kHz mono 16-bit WAV (whisper-ready). Uses
  the classic NAudio capture classes; the newer WasapiRecorder/RealtimeCaptureMixer
  are not in the pinned NAudio 3.0.0-preview.6.
- Start/stop and screen-snapshot each on the tray and a global hotkey; a visible
  "recording" indicator (tray text + a spoken cue while capturing).
- Names {meeting} - {timestamp} from the Reminder module's "meeting.current" shared
  context (else a timestamp), sanitized; a storage-location setting + Browse and a
  "create a folder per capture" toggle (default on).
- Transcribes offline with a local whisper.cpp CLI (path + model in settings). When
  Whisper is not set up the audio is kept and a stub transcript is written; the
  transcript is always headed with the calendar attendee roster (no Whisper needed).
  A "Transcribe a WAV file..." action re-processes a kept recording.
- A 72-hour purge deletes audio + snapshots and keeps transcripts.

Targets net10.0-windows10.0.19041 (NAudio.Wasapi's floor; the base stays on
windows7.0 with DirectSound because it never captures). Build-verified and gate
green; the live WASAPI capture and Whisper paths are not yet run here. Not published
to the catalog and no release cut -- both wait on the Whisper install and a smoke test.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `ffa8a1022`

**chore(modules): regenerate catalog.json for reminder 1.6.0**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `83f08db75`

**chore(modules): publish reminder 1.6.0**

### 2026-08-26  `09f296d50`

**feat(reminder): capture attendees + publish meeting.current (needs host 1.9.0)**

```
Events now carry the invited roster (Outlook Recipients with per-recipient response
status; ICS ATTENDEE with CN + PARTSTAT; JSON attendees array). Each tick the module
publishes the ongoing / about-to-start event to the host shared-context channel as
'meeting.current' {name,startUtc,endUtc,location,attendees[]}, so the Remembrance module
can auto-name a recording and seed its attendance from the calendar. Publishes only on
change. MinHostVersion 1.9.0 (calls IHost.PublishContext); module bumped to 1.6.0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `2db6655f3`

**feat(host): shared-context channel + audio-capture permissions (ABI 1.9.0)**

```
Two additive host ABI members for the coming Remembrance module, and reusable by any
future cross-module handoff:

- IHost.PublishContext / ReadContext / ContextChanged: a tiny host-mediated key/value
  channel so one module can hand a fact to another without a direct reference (modules
  load in isolated contexts and cannot call each other). Opaque JSON values the host
  never parses, ungated, unpersisted live state cleared on restart.
- ModulePermissions.Microphone and .SystemAudio: audio-capture flags, so a recording
  module's install consent honestly lists what it records. Screen capture and global
  hotkeys are already covered by the existing ScreenContext and Hotkey flags.

Implemented across CompanionHost and all seven doubles (the ModuleKit RecordingHost functional
with a PublishedContext dict; the six self-test fakes minimal). ProductVersion bumped to
1.9.0 in the same change per the additive-ABI rule. Gate green, 0 warnings.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `2348891ab`

**docs(remembrance): plan for the Remembrance meeting-recorder module**

```
Converged design: local Whisper transcription; plain transcript in v1 with speaker
diarization as a follow-up; a shared-meeting channel + capture permission flags as the
two host ABI additions; calendar-sourced attendance; a manual snapshot hotkey; a
72-hour purge of audio + snapshots (transcript + attendance kept); and storage-location
+ folder-per-capture settings.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `ea73138df`

**docs(handoff): record the v1.8.1 release**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `598d82153`

**chore(release): bump ProductVersion to 1.8.1**

```
Host code is unchanged since 1.8.0 (this session's work is the catalog-delivered
Reminder module). The bump exists so a v1.8.1 tag matches ProductVersion.props for
release.yml, cutting a fresh signed-by-CI build for manual smoke testing.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `e4d200d6d`

**docs: Reminder module grown to 1.5.0 (handoff + Readme)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `fba7eae78`

**chore(modules): regenerate catalog.json for reminder 1.5.0**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `cba4cbf14`

**chore(modules): publish reminder 1.5.0**

### 2026-08-26  `f7e4e7f15`

**feat(reminder): hush while presenting or in Do Not Disturb; bump to 1.5.0**

```
Announcements (calendar, personal, and the briefing) are suppressed like quiet
hours whenever Windows reports a fullscreen app, presentation mode, a fullscreen
D3D game or Store app, or Do Not Disturb / quiet time (SHQueryUserNotificationState,
the same signal the OS uses to hold its own toasts). A failed query defaults to not
hushing, so a reminder is never silently lost. On by default; the toggle sits with
Quiet hours.

Bumps the module to 1.5.0, the version carrying all of this session's Reminder work
(join links, agenda, briefing, declined/all-day filter, per-slot test, typed personal
reminders, and this).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `b54ec6e1b`

**feat(reminder): typed personal reminders, independent of any calendar**

```
A 'Personal reminders' list card lets you add reminders by typing a short line
(daily 09:00 Standup | every 60m Stretch | in 30m Pizza | weekdays 17:00 Log off
| 2026-09-01 14:00 Dentist), toggle each on/off, and remove disabled ones. They
announce through the same pipeline in their own style + chime, respecting quiet
hours. Dedup is a per-reminder LastFired stamp so the store stays bounded and a
recurring reminder fires once per occurrence; a one-off disables itself.

New: PersonalReminder (record + encode/decode + self-test), PersonalReminderParser
(mini-syntax + self-test), PromptDialog (a minimal WinForms input box).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `57fc6015b`

**feat(reminder): a per-slot 'Test this reminder' button**

```
Each calendar card gets a Test button that fires a sample announcement in that
slot's name, style, and chime, so styling can be previewed while configuring
instead of waiting for a real event. It reads saved settings (a PaneAction can't
see the pane's unsaved edits), and the status says so.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `8f1710b6b`

**feat(reminder): on-demand agenda, morning briefing, and a declined/all-day filter**

```
- 'Read today's agenda' tray entry: the pet speaks what's left today across every
  calendar.
- Daily briefing: at a time you set, the pet reads the day's agenda once (tracked
  per day so it fires exactly once, even across restarts; skipped in quiet hours).
- A single announce filter (PassesFilter) now gates scheduling, the agenda, and
  'next upcoming': skip meetings you've declined (Outlook response status) and,
  optionally, all-day events. Declined-skip is a no-op for .ics feeds, which don't
  carry your own status.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `f058fb9ba`

**feat(reminder): join-the-meeting link detection + a Join tray entry**

```
Events now carry a Description (Outlook body, ICS DESCRIPTION, JSON description),
plus an all-day flag and a normalized response status for a later filter. A new
MeetingLinkDetector scans an event's location then description for a Teams / Zoom /
Google Meet / Webex link (known hosts only, so a doc or map URL is never mistaken
for a meeting). A Join: <title> tray entry opens the link for the meeting that is
ongoing or starting within ten minutes, and the spoken reminder notes when a link
is ready. Opening validates the scheme is http(s) and shell-launches the handler.

Also backlogged: having the pet physically react to an event (needs a host change).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `b15ab32bc`

**chore(modules): regenerate catalog.json for reminder 1.4.1**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `23b839d86`

**chore(modules): publish reminder 1.4.1**

### 2026-08-26  `0854f4efa`

**feat(reminder): a per-calendar 'play a chime' checkbox**

```
Each slot gets its own chime on/off, so one calendar can announce silently while
another sounds. The global chime switch is now the master over all of them; a
slot's checkbox defaults on, so nothing changes for an existing setup.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `f725d210c`

**chore(modules): regenerate catalog.json for reminder 1.4.0**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `2a9b0cc1a`

**chore(modules): publish reminder 1.4.0**

### 2026-08-26  `75848f2ea`

**feat(reminder): per-calendar chime, browsable from the options pane**

```
Each of the five calendar slots gets its own chime: a "Browse for a chime…"
button opens a file picker, and the chosen WAV/MP3 plays for that calendar's
reminders (blank = the built-in chime). The global "play a chime" switch stays
the master on/off.

- Chime.Play gains a (host, customPath) overload: reads a readable WAV/MP3 under
  8 MiB and hands it to the host (which accepts a self-describing WAV or MP3 up
  to 16 MiB), falling back to the embedded default on a blank/missing/oversize
  path or any read error. Best-effort and silent, as before.
- The pane gets a per-slot chime text field plus a per-slot Browse button. A
  PaneAction runs on the UI thread, so the file dialog needs no host change;
  ReloadPaneAfter refreshes the text box so a later Apply reads the path back
  instead of clobbering it.

No host or ABI change. Module bumped to 1.4.0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `a924771cf`

**chore(modules): regenerate catalog.json for reminder 1.3.0**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `bac2b6297`

**chore(modules): publish reminder 1.3.0**

### 2026-08-26  `a747734bf`

**feat(reminder): up to 5 calendar feeds, each with its own name and speech style**

```
A busy person has more than one calendar, so the Reminder module now watches up
to five feeds at once instead of one. Each slot is configured independently (Off
/ Local file / Calendar URL (ICS) / Local Outlook), carries its own name, and has
its own font, size, colour, and bold/italic/underline, so a Home event and a Work
event look and read differently in the bubble.

- AggregateCalendarSource fans one tick out across the configured slots and merges
  the result. Each event is COPIED (never the source's own instance, which a
  CachingCalendarSource may hand back unchanged across ticks), tagged with its slot
  via CalendarEvent.SourceId, and its id is prefixed with the slot id so two
  calendars with a coincidentally equal id can never share one "fired" entry. A slot
  that errors is reported in the combined status but does not suppress the healthy
  slots' events.
- SpeechStyleSettings gains prefix-aware overloads (Fields/AddLoadValues/Save/ToStyle),
  so several independent style sets share one options pane. The existing no-prefix
  methods delegate with an empty prefix, so nothing else changes.
- A 1.2.x single-source config (source/url/file + the flat speech-style keys) is
  migrated into slot 1 exactly once, keyed on a marker so it never re-runs or stomps
  a slot the user has since edited.

Timing, quiet hours, and the chime stay global (they apply to every reminder). No
host or ABI change: MinHostVersion stays 1.8.0. Module bumped to 1.3.0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `4ef5581a6`

**docs(handoff): v1.8.0 released; record the WIX0104 latent-comment lesson**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `9c6239dad`

**fix(installer): remove illegal '--' from WiX comment bodies (WIX0104)**

```
The v1.8.0 release build failed at the MSI step: an XML comment cannot contain
'--'. Two comments added after v1.7.0 (the afterInstallInitialize note and the
util:CloseApplication TODO) each carried a '--' separator in the body, which
WiX rejects with WIX0104. The MSI step only runs on a release tag, so neither
the local gate nor the normal CI build caught it. Switched both separators to
an em dash, valid inside an XML comment.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `ee07a1c33`

**chore(modules): regenerate catalog.json for reminder 1.2.0**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-26  `8a0b64463`

**chore(modules): publish reminder 1.2.0**

### 2026-08-26  `e79389f3a`

**Add Reminder module, module-owned styled speech, and global sound toggles**

```
Reminder module (v1.2.0, MinHostVersion 1.8.0): the pet announces upcoming
calendar events. Three sources - a local JSON feed, a Calendar URL / ICS
(iCal.Net, with recurrence and time zones), and a running desktop Outlook over
late-bound COM (attaches only to a running instance, never launches or quits).
Multiple lead times, quiet hours, an optional chime, and the event location in
the announcement.

Module-owned styled speech: SpeechStyle on the ABI plus IHost.Say/SayAll(text,
style); FormSpeech is now a plain renderer honouring family/size/bold/italic/
underline/colour. ModuleKit SpeechStyleSettings gives any module the setting
fields, load/save, and ToStyle in a couple of lines.

Two global Sound master switches in Preferences: pet sounds (embedded SFX) and
notification sounds (module PlaySound, e.g. the chime), independent, both
default-on.

Host ABI 1.8.0: ICompanionManager.TryReadTypeXml (used by Pet Studio's "Analyze
installed pet" dropdown) plus the styled Say/SayAll members. Additive only;
ProductVersion already at 1.8.0.

Docs: BACKLOG, handoff START HERE, and the Readme updated for the above.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `abdd594a4`

**docs(backlog): note the 'silence pet sounds' Audio checkbox for the TTS module**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `9d473db70`

**chore(catalog): regenerate after the petstudio ffmpeg-encoding rebuild**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `2e827c9ed`

**chore(modules): publish petstudio 1.4.0**

### 2026-08-25  `3b9e333e0`

**fix(convert): pin StandardOutputEncoding on the ffmpeg process starts**

```
The runtime-hardening invariant requires every RedirectStandardOutput to also pin
StandardOutputEncoding, so a redirected pipe never rides the OS default codepage.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `754a5e808`

**chore(catalog): regenerate for the re-converted pets + fortunes 1.2.3 + petstudio 1.4.0**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `00bdf3da1`

**chore(modules): publish petstudio 1.4.0**

### 2026-08-25  `05e7cc2e7`

**chore(modules): publish fortunes 1.2.3**

### 2026-08-25  `5117f5a27`

**feat: frequency-weighted shimeji behaviour, sound capture, Pet Studio installed-pet analysis**

```
Converter:
- Weight the hub's action selection by each source's real behaviour frequencies
  (behaviors.xml root frequencies for classic packs; the auto.onFinish transition
  graph for web-export bundles), so converted pets walk and run instead of idling
  in place and shuffling poses.
- Adaptive locomotion repeat: bound a walk to about 2.5s so a slow Creep no longer
  glides ~36s in one direction. Add a `rebalance` migration verb for shipped pets.
- Capture pose sounds: transcode a pack's WAV clips to MP3 and embed them as
  per-animation <sounds> within a per-pet audio budget (ffmpeg; classic packs only).

Pets:
- Re-convert every shipped shimeji from source with the above. Drop two low-mobility
  pets; add Gengar, Alipheese Fateburn XVI, and Loona the Hellhound (with sound).
- Rebuild pet-thumbnails.zip for the new set.

Fortunes 1.2.3:
- Fix the smart/contextual picker repeating the same handful once its recent window
  saturated: recycle a spent context, never repeat back-to-back, and share recent
  history across the random and smart speech paths.

Host 1.8.0 + Pet Studio 1.4.0:
- Add ICompanionManager.TryReadTypeXml so a module can read any installed pet's
  animations.xml (library, bundled beside the exe, or the built-in default).
- Pet Studio gains an "Analyze installed pet" dropdown that loads a chosen pet
  straight into the analyzer.

Also carries the Reminder module, the Pets uninstall-refresh, the lean portable
bundle, and the MSI upgrade-schedule fix from the pending batch.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `bf6f5437c`

**Bump product version to 1.7.0 (Shimeji import + catalog release)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `c45ea96e9`

**Release prep: docs for the Shimeji import + catalog; note the libwebp/dwebp grant**

```
Readme (Pet Studio imports Shimeji desktop+Android skins; shared converter engine + CLI), THIRD_PARTY_NOTICES (libwebp/dwebp BSD grant, Pets/shimeji provenance, drop stale module name), BACKLOG + handoff (Shimeji done + what's not), and .gitignore the local tests/ media.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `8747a26cc`

**Regenerate catalog.json for Pet Studio 1.3.0 (Android bundle import)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `aa9309a4d`

**chore(modules): publish petstudio 1.3.0**

### 2026-08-25  `3dcad04d8`

**Pet Studio: import Android JSON+WebP bundles, not just desktop skins**

```
Source-link the engine's WebPLoader/BundleParser/BundleConverter and bundle dwebp.exe into the module (source-linking doesn't propagate the engine's copy). ImportSkinFromRoot now detects an Android bundle (manifest.json + animation.json, possibly one level down in a zip) and routes to ConvertBundle, else the existing desktop path; both share LoadConvertedIntoEditor. Version 1.3.0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `93c00662f`

**Regenerate catalog.json: 48 pets (add the 5 shimejis-xyz skins)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `904d37c3a`

**Add 5 vetted shimejis-xyz skins to the pet catalog**

```
Cartman, Rick (StarriiChan), Gakupo (rikka-nyan), Hornet, and Uzi Doorman (Kilkakon and Polar Summit) from the shimejis-xyz archive, a curated set the owner cleared. Desktop Shimeji-EE format, converted via convertroot with per-pixel alpha, each crediting its real author and linking its source page. Ids are shimeji-<slug> so they stay download-only (excluded from the portable bundle).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `d9fa94c58`

**Regenerate catalog.json: the 21 shimeji as downloadable pets**

```
New-ContentCatalog picks up the 21 shimeji added under Pets/, with their real names (from pets.json) and blob-hash sha256, so Check-for-new-pets lists and downloads them from raw.githubusercontent.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-25  `d2c2571c7`

**Finalize Shimeji: ship converted skins as catalog pets, retire the module, fix WebP alpha**

```
The converted shimeji are ordinary pets, so retire modules/ShimejiImporter entirely and publish the 21 curated skins as normal catalog pets under Pets/shimeji-<id>/, listed in pets.json (real name + shimeji.org credit + source URL). Pet Studio keeps the import/convert flow. Download-on-demand: excluded from the portable bundle (Stage-BundledContent), and their thumbnails added to the embedded pet-thumbnails.zip so the download grid shows art before install.

Fix WebP decode properly. WIC drops WebP alpha (decodes Bgr32), which produced an opaque black box. WebPLoader now shells out to a bundled, statically-linked dwebp.exe (libwebp 1.4.0, BSD-3, see native/NOTICE-libwebp.txt), streaming a PNG with alpha intact; PNG/JPEG still go through WIC.

Raise the pet XML budget 4 to 12 MiB (validator, catalog download, settings) so frame-heavy skins fill the 4096 sheet up to the 256px runtime frame ceiling instead of being squeezed smaller; the 4096 sheet + 16 Mi pixel guard (per-pet render memory) is unchanged. The Pets gallery now reads each pet's petname instead of the folder id, and the converter no longer truncates the name to 16 chars.

Also in this branch: converted pets emit encoding=utf-8 matching their bytes (a false utf-16 prolog had broken XDocument.Load and the Pets-pane thumbnail), and the fall animation self-loops at a constant terminal velocity so a tall drop no longer stutter-lands every ~2s. Update the runtime hardening test to the 12 MiB boundary and pin StandardOutputEncoding on the dwebp redirect.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `600fe1728`

**Shimeji Catalog MVP: repurpose the module into a browse+Get store**

```
The standalone importer becomes the curated catalog (import-your-own now lives in
Pet Studio). It ships pre-converted pets we have permission to redistribute plus a
manifest, and carries NO converter -- it installs the pet you pick via
ICompanionManager.InstallType.

- Dropped the ShimejiConvert.Engine reference; the module is now Contracts + a WPF
  browse window + an embedded catalog (catalog/catalog.json + <id>.xml).
- ShimejiCatalog: reads the embedded manifest + pet XMLs.
- ShimejiCatalogWindow: a card grid (thumbnail from each pet's <header><icon>,
  creator credit, source link, Get button, "Show AI-generated" toggle off by
  default). Get -> InstallType.
- Module: options-pane "Browse Shimeji Catalog…"; version 0.1.0; Permissions Pets.
  SelfTest verifies the embedded catalog loads and every entry has an installable
  animations.xml (gate: --module-selftest=shimejiimporter green).
- Seeded with 10 shimeji-org desktop pets (curated + converted at ~2.7MB total,
  full permission). Titles fall back to the id where the harvest CSV lacks a name.

MVP scope: bundled/offline so it works on this branch (master's catalog is stale);
scaling to the full permitted set + hosting come next.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `1adff2b8b`

**Converter: read the Android Shimeji JSON+WebP bundle format**

```
Adds a second input format alongside desktop Shimeji-EE: the modern JSON+WebP
bundle (manifest.json + animation.json + sprites/%04d.webp) that shimeji.org and
the mobile app export. It reuses the whole back half of the pipeline (compositor +
emitter + alpha), so the output is a normal desktopPet pet.

- WebPLoader: decodes WebP via WIC (WPF BitmapDecoder) to a 32bpp premultiplied
  System.Drawing.Bitmap, preserving alpha. GDI+ can't read WebP; WIC can, in-process
  on Win11, with no NuGet/native dependency (engine gains UseWPF for the codec only,
  no XAML).
- BundleParser: maps manifest.json + animation.json into the existing ShimejiConfig
  (GROUND/WALL/CEILING -> Floor/Wall/Ceiling; FALL/DRAG -> Class Fall/Dragged;
  STAND/IDLE -> Stay, WALK -> Move; frame -> pose with dx/dy velocity, durationTicks,
  bottom-centre anchor; ActionClassifier applied per action).
- BundleConverter.ConvertBundle/IsBundle: composites in ALPHA mode (WebP has alpha)
  and emits, returning the usual ConversionResult.
- CLI: `convertbundle <BundleDir> <out.xml>`. BundleSelfTest wired into the run.

Reuses SpriteSheetBuilder/PetEmitter/ActionClassifier unchanged (no protected file
touched), so the desktop path and the census (91/53/32/6) are unaffected. The real
sample bundle converts to an accepted 11-animation alpha pet. Built by a subagent
under my direction; independently verified (0/0, selftest PASS, ACCEPTED).

The bundle's own transition graph (onFinish/borderTransitions/events) is not yet
translated; the shared emitter builds its standard floor hub-and-spoke, the same v1
simplification as classic skins.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `e222bad8a`

**catalog.json: petstudio 1.2.1 (.zip import + converter gains)**

```
Regenerated after committing the petstudio.zip. Not live until this branch merges.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `32dfc7fb0`

**chore(modules): publish petstudio 1.2.1**

### 2026-08-24  `a69b7b280`

**Pet Studio 1.2.1: .zip import + converter gains (vocab, nested sprites)**

```
Version bump so the published module reflects the .zip import button and the
source-linked converter improvements (Japanese/British vocabulary, sprite-dir
ranking). Republish follows.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `2db336fcb`

**Converter: rank sprite dirs by sprite count (fixes nested/real-world skins)**

```
Real downloads nest sprites in ways the fixed root/img/<Skin> assumption missed:
img/<Skin>/shime*.png next to an icon-only img/, a pack of sibling <Character>/img
folders, or sprites a few levels deep. SkinLayout.Detect was returning the first
folder with any .png -- often a stray icon.png dir -- so the converter looked for
shime1.png in the wrong place and every sprite load failed.

FindImgDirs now gathers all candidate folders (preferred locations + a capped
descendant sweep) and ranks them by sprite count, with shime-named sprites winning
ties, so the true sprite folder always wins over an icon/banner dir.

Measured over a 90-blob sample of the harvested collection, desktop-format convert
yield went from 57% to 99% (IMAGES_ONLY 0/30 -> 30/30, PC_SHIMEJI 21 -> 29/30,
generated_desktop_zip 30/30). The one remaining failure is a skin with no sprites.

Also adds a dev CLI verb `convertroot <dir> [out.xml]` (detect + convert a skin
root, as Pet Studio's import does) for measuring convert yield over a collection.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `0f38a733d`

**Pet Studio import: accept a .zip skin, not only a folder**

```
The standalone importer took both a folder and a .zip; the in-studio import only
had the folder picker. Pet Studio now has "Import skin folder…" and "Import .zip…"
side by side. The .zip path extracts to a per-session temp dir (deleted on window
close) and feeds the same ImportSkinFromRoot flow, so detection, conversion, the
loss report, and preview/install are identical to the folder path.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `99717e66d`

**Converter: read Japanese (and British) XML vocabulary, not just English**

```
An outside review caught it: the parser matched only English element/attribute
names, so a Shimeji authored in the official Japanese schema (<ポーズ 画像="..."
基準座標="...">, 種類=組み込み, 枠=地面) parsed to ZERO poses and failed compositing
with "no sprite poses to composite". Roughly a third of a real .xyz collection
uses the Japanese vocabulary.

ShimejiParser now canonicalises every element name, attribute name, and the
Type/BorderType enum VALUES to the English form the engine keys on, from the
gil/shimeji-ee schema_ja.properties map (plus British "Behaviour"). Class stays
verbatim (it's a Java class path, not localised). English skins are unaffected
(canon is identity for them), so the bundled census holds at 91 (53/32/6).

New VocabSelfTest: a Japanese actions.xml parses to 3 actions / 4 poses with
Floor + Move + Fall recognised across vocabularies (also proves the non-ASCII
literals compile intact). Wired into the engine self-test run.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `3501a1e86`

**Size: per-pet size slider (25%..400%, including below 1x)**

```
Wires the fractional scale core to storage and a UI control.

- Settings gain a global ScalePercent and a per-pet CompanionSizeEntry.Percent (0 =
  follow the legacy 1/2/4 level). Normalize keeps an entry with EITHER a valid
  level or a valid percent (the old normalize dropped level-0 rows, which would
  have wiped a percent-only override), and equality/clone carry Percent.
- LocalData: GetEffectivePetScaleFactorD / GetEffectivePetScalePercent /
  SetPetScalePercent, precedence per-pet percent -> global percent -> legacy level.
- StartUp stages every pet through the fractional factor now
  (GetEffectivePetScaleFactorD) and exposes SetPetScalePercent / GetPetScalePercent.
- Pets pane: the inline "size 1/2/3" cycle becomes a Slider (25..400%, snapping to
  25% steps) with a live percent readout; applies the next time the pet is Added,
  same as the old control.
- RuntimeHardeningSelfTest: a sub-1 case (0.5x halves the frame, 'scale' stays 1).

Gate green; every integer factor still produces identical output.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `cb3601d22`

**Size: fractional scale engine core (allows below 1x), back-compat**

```
Groundwork for a sub-1 size slider. The scale factor becomes a double for frame
size and movement, while the integer 'scale' variable exposed to pet XML stays
rounded and never below 1, so hand-authored pets that read 'scale' are unaffected.

- ScalePolicy gains ClampFactorD / FitFactorForFrameD / ScaleD / LevelForExpression.
- Xml carries scaleFactorD alongside iScale; ReadImages sizes frames from the
  fractional factor and, for ALPHA pets only, downscales with HighQualityBicubic
  (magenta pets and every upscale stay nearest-neighbour, so no colour-key halo).
  The Xml ctor is now Xml(double) -- existing int callers convert implicitly.
- Animations movement uses the fractional factor (ScaleD); identical to before for
  1x/2x/4x, proportionally smaller below 1.

Every integer factor (1/2/4) produces byte-identical output, so the full gate is
green with no changes to hardening budgets, security, or the scale-expr tests.
Storage + the slider UI come next.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `c467c55b9`

**Converter: honest residue for sounds/scripts + saner script-duration fallback**

```
From the Shimeji-EE source reference:
- Poses now capture Sound and flag script-computed (${...}/#{...}) numeric attrs.
  A script DURATION previously int-parsed to a 1-tick flash (a cause of the
  "animations play too fast" report); it now flattens to a gentle 8-tick hold.
- Residue gains three honest notes: N poses dropped a sound (pets are silent),
  N poses had script durations/velocities flattened, and N actions rely on
  script-computed timing/targets/conditions (approximated by fixed timing + a
  bounded wander, or dropped). The target-walk note now says it turns at the edge.
- Parser accepts British "Behaviour"/"BehaviourReference" so condition harvest
  isn't spelling-dependent.

EmitterSelfTest's synthetic skin gains a sounded pose + a script duration and
asserts both notes fire. Bundled census intact at 91 (53/32/6); the affordance
example now reports "59 action(s) use script-computed values".

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `5cc6cb1ed`

**Converter: correct embedded-class fidelity (informed by Shimeji-EE source)**

```
The classifier's fallthrough was Group1, so any UNRECOGNIZED Embedded class was
silently treated as a clean deterministic map. The affordance example exposed it:
ScanMove/Interact were graded Group1 and would have converted as ordinary frames
with no warning that the two-pet interaction is lost.

Reading the Shimeji-EE source (com.group_finity.mascot.action.*) corrected the
taxonomy:
- Group3 (dropped): Breed/BreedJump/BreedMove (autonomous spawn),
  ScanMove/ScanJump/ScanInteract/Interact (need a peer pet), Transform (image-set
  swap), alongside the existing ThrowIE family.
- Group1 (clean): SelfDestruct -> the magic 'kill' name; and the DEPRECATED
  aliases Broadcast/BroadcastStay/BroadcastMove/BroadcastJump/MoveWithTurn, which
  behave as their base Animate/Stay/Move/Jump (the affordance broadcast for
  pairing is dropped, the animation itself converts).
- Safety net: any other Embedded class now DEGRADES (Group2) with a named reason
  instead of masquerading as a clean map.

Verified: bundled census intact at 91 (53/32/6); the affordance example converts
to a valid, accepted pet with ScanMove/Interact named in the loss report.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `dd84318a4`

**catalog.json: petstudio 1.2.0 (Shimeji import)**

```
Regenerated after committing modules-dist/petstudio.zip, so the catalog hashes
the committed blob. Not live until this branch merges to master.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `2b951fc83`

**chore(modules): publish petstudio 1.2.0**

### 2026-08-24  `629ebbd86`

**Pets pane: "Import Shimeji skin…" launcher that deep-links Pet Studio**

```
The Pets gallery gets a global "Import Shimeji skin…" button next to "Check for
new pets". It opens Pet Studio straight into the import flow. Pet Studio owns the
converter; the pane only deep-links.

- PetStudioModule.OpenForImport() is public: opens (or activates) the window and
  calls BeginImport(). Public because the host cannot cast across the module's
  load context and IModule stays frozen.
- CompanionsPaneControl finds the petstudio module in Program.Mainthread.LoadedModules
  by id and invokes OpenForImport() by reflection, with legible fallbacks when
  Pet Studio is absent or too old.
- Pet Studio version -> 1.2.0 (the import feature).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `409aa634c`

**Pet Studio: fold Shimeji import in (workshop half of the split)**

```
Import a Shimeji skin directly in Pet Studio: browse a skin folder, convert,
and the emitted animations.xml lands in the editor with the analysis rendered
and a new "Import loss (what didn't convert)" panel showing the residue. From
there the existing Preview and Install act on it. This is the authoring half of
the agreed split -- the standalone module becomes the curated catalog next.

- PetStudio.csproj source-links the conversion-ONLY engine files (Engine,
  PetGraph, Shimeji/*, Emit/*) and embeds the BSD base conf as base-actions.xml
  / base-behaviors.xml. It can't ProjectReference ShimejiConvert.Engine: both
  recompile AnimationXML/CompanionXmlValidator/SafeExpression, which would double-define
  XmlData. ValidatorResources.cs and the engine self-tests are deliberately left
  out (EngineShim + the linked Xml.cs already provide their shims; the CLI runs
  the self-tests).
- PetStudioWindow: "Import Shimeji skin…" button, ImportSkinFromRoot (also the
  entry a future catalog hand-off will call via BeginImport), the import-loss
  section, and a persisted last-skin-dir. Imported pets default to alpha
  transparency, so they render with smooth edges.
- PetStudioModuleSelfTest: ImportEngineIsWired asserts the engine compiled into
  the module and its embedded base conf parses to the 91-action census.

Gate green: 0 warnings, 14 self-tests (petstudio-selftest now covers import),
verify 22/22.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `d94db6285`

**Per-pixel alpha render path for imported pets (host + converter)**

```
Smooth (anti-aliased) edges cannot survive the 1-bit magenta colour key, so
pets that opt in now render with real per-pixel alpha. Gated per-pet, additive,
and colour-key pets are completely untouched.

Format opt-in:
- <transparency>Alpha</transparency> is a reserved keyword selecting the alpha
  path (Xml.AlphaTransparencyKeyword). Any real colour name keeps the magenta
  colour key. The field was previously parsed-and-ignored; the schema already
  allows a free string, so this is backward compatible.

Host (src/dotNet):
- Xml.UsesAlpha parsed from <transparency>.
- FormCompanion gains a parallel render path: alpha pets clear TransparencyKey (so
  WinForms never drives the layered attributes) and push each 32-bpp premultiplied
  frame through UpdateLayeredWindow (ULW_ALPHA) instead of the child PictureBox.
  Opacity is routed through SetPetOpacity (folded into the ULW constant alpha, never
  Form.Opacity, which would fight ULW). Alpha pets skip the form-resize/PictureBox
  edge-clip and position full-size (v1: possible small overhang past a shared
  multi-monitor edge). NativeMethods gains UpdateLayeredWindow + GDI helpers.

Converter (tools/ShimejiConvert.Engine):
- SpriteSheetBuilder alpha mode: transparent background, no magenta flatten, real
  edges preserved. SpriteSheet.IsAlpha carries the choice.
- Emitter writes <transparency>Alpha> for alpha sheets and swaps the residue note
  from "hard magenta edges" to "smooth edges preserved; desktopPet-only".
- ConvertSkin defaults to alpha=true, so the importer produces smooth pets.

Tests: EmitterSelfTest now asserts the colour-key path still writes Magenta and the
alpha path declares Alpha, keeps a fully-transparent sheet pixel, and stays
accepted. Full gate green (0 warnings, 14 self-tests, verify 22/22).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `7a99e87b6`

**Shimeji importer: coherent floor-walk emitter + module UX cleanup**

```
Emitter (tools/ShimejiConvert.Engine):
- Rebuild the pet graph as a floor hub-and-spoke over floor-only actions
  instead of every primitive, so imported pets walk, idle, sit and turn
  coherently rather than flickering through unrelated poses.
- turn-at-edge: locomotion spokes route <border> to a 1-frame `turn`
  animation whose <sequence action="flip"> toggles IsMovingLeft, so the
  pet reverses direction at a screen edge instead of stalling.
- loop-and-land fall: repeat=20 descent with <border> back to the hub,
  mirroring the eSheep fall so a drag-release settles instead of churning.
- Tighter composite cell (floor + fall + drag poses only) and MaxInterval
  lowered to 4000 for less dead time between states.
- EmitterSelfTest guards the spawn-X against a fake screen (invisible-pet
  regression) and asserts the magic names are emitted.

Module UX (modules/ShimejiImporter):
- Drop the tray item for the final build; the importer lives in the
  Options -> Modules pane only.
- Remember the last-used skin folder via IModuleSettings.
- Remove the unused module icon resource.

Gate green: 0 warnings, 14 self-tests no skips, shimeji verify + selftest.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `22cb4da48`

**feat(pets): add an Uninstall button for installed library pets**

```
The Pets gallery could Use/Add/Remove (Remove only despawns an on-screen
instance) but had no way to delete an installed pet -- a downloaded, converted,
or authored one -- from the library. Add an Uninstall button on the card for
pets that live in the writable library (never the built-in eSheep, never the
active pet). It confirms, despawns any on-screen copies, then deletes the pet's
folder, with a path-containment guard so the delete can't escape the library.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `89a951723`

**feat(shimeji): bundle the BSD base conf so sprites-only skins convert**

```
Most Shimeji skins ship only shimeN.png sprites and rely on the shared base
conf; before this, such a skin could not be converted (no actions.xml). Bundle
the Shimeji-EE base conf (actions.xml + behaviors.xml) as embedded engine
resources and fall back to it when a skin has none:

  * SkinLayout.Detect now returns skins with UsesBundledConf=true when sprites
    exist but no conf is found;
  * ShimejiEngine.ConvertSkin parses the bundled conf when confDir is empty and
    adds a residue note crediting the source;
  * ShimejiConvert CLI: `convert - <img> <name> <out>` uses the bundled conf.

The conf is redistributable: Shimeji-EE is 3-clause BSD and the original Group
Finity Shimeji is BSD-style, both permitting redistribution with the notice
retained. Attribution added to THIRD_PARTY_NOTICES.md + base-conf/NOTICE.txt.
This is a deliberate, license-verified reversal of the old "no Shimeji reference
in the repo" default -- and it is ONLY the behaviour XML; never sprite art.

Gated by BundledConfSelfTest (the bundled conf embeds and parses to the intact
91-action / 53-32-6 reference census). Verified live: a sprites-only KuroShimeji
converts to an accepted pet via the bundled conf.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `a0444f719`

**docs(shimeji): clearer residue note on the magenta transparency key**

```
Reword the edges note so it reads as the app's rendering model, not a converter
shortcut: pets render with a 1-bit magenta transparency key (no partial alpha),
so soft edges can't be preserved -- mild for hard-outlined art, more visible on
glows/shadows.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `c63628f88`

**fix(pets): show a thumbnail for installed pets, not a blank card**

```
The Pets gallery sourced card thumbnails only from the bundled pet-thumbnails
zip (keyed by built-in id), with the app-icon fallback limited to the built-in
eSheep. So any INSTALLED pet -- a Shimeji import, a Pet Studio authoring, or a
downloaded catalog pet -- showed a blank card. Fall back to the pet's own
<header><icon> from its animations.xml (decoded with WPF's ICO decoder, which
handles the PNG-in-ICO the importer emits) when the zip has nothing. Pre-existing
host gap; surfaced by the first installed pet in the grid.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `edd0bbdcd`

**fix(shimeji): converted pets always spawn on-screen (were sometimes invisible)**

```
One of the two emitted spawns placed the pet at x=screenW+10 -- fully off the
right edge -- and routed it to the stationary hub (Stand, x=0), so ~half of
spawns left the pet standing off-screen and invisible (it still spoke, so a
speech bubble appeared with no pet). Land both spawns on-screen: one drops in
from the top, one appears standing on the floor, both at a random on-screen x.

Guard it: EmitterSelfTest now evaluates each spawn's X against a fake screen and
fails if the pet lands off-screen horizontally, so an off-screen spawn can't
regress silently.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `1099d7bde`

**chore(catalog): record the fortunes 1.2.2 payload hash**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `52c66af68`

**chore(modules): publish fortunes 1.2.2**

### 2026-08-24  `e8a582c2d`

**fix(fortunes): rename the misleading "Your own packs" fallback to "More packs"**

```
CollectionFor() labels any pack whose id is not in the embedded collection map
as its fallback group. That string was "Your own packs", which wrongly implied
the user imported those packs -- but it also catches catalog packs newer than
the build's collection map. Rename it to the honest "More packs" (module +
the host's FortunesModuleSelfTest assertion). Bump 1.2.1 -> 1.2.2.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `b1c8703ce`

**feat(shimeji): standalone Shimeji Importer module**

```
Stage 6 of the plan. A new modules/ShimejiImporter module: a WPF window that
opens a Shimeji skin (folder or .zip), detects its conf + sprite layout, runs
the shared ShimejiConvert.Engine, shows the honest loss report, previews the pet
on the real desktop (ICompanionManager.SpawnPreview) and installs it
(ICompanionManager.InstallType). A "where to find skins" links section; it never
downloads skins itself. Adds SkinLayout to the engine (locate conf + img,
tolerant of Shimeji-EE layouts) and a folder->detect->convert SelfTest.

Contained like every module: Contracts is Private=false (not shipped), while
ModuleKit and ShimejiConvert.Engine ship inside modules\shimejiimporter\.
MinHostVersion 1.4.7 (IHost.IsDarkTheme + the ICompanionManager verbs); no host edits.

Wired into build.ps1 ($moduleProjects), run-gate.ps1 (presence check +
--module-selftest=shimejiimporter) and build.yml. Full gate green: the module
builds in Release, loads through the real ModuleHost, and its SelfTest passes.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `bda61f471`

**feat(shimeji): emitter -- Shimeji skin to a valid, reachable animations.xml**

```
Stages 3+4 of the Shimeji Importer plan. Add the output half to
ShimejiConvert.Engine and a `convert` CLI verb, closing the pipeline
parse -> composite -> emit.

  * Emit/PetEmitter -- builds a hub-and-spoke pet: each Group1 primitive with
    sprites becomes one animation, a standing pose is the hub the graph fans out
    from and returns to (so nothing is orphaned), and the four magic names are
    emitted (fall/drag from the Fall/Dragged actions, kill/sync synthesised).
    Group2 actions are recorded as degraded, Group3 as dropped. Per-pose velocity
    collapses to one start/end pair; Duration -> interval at ~40 ms/tick.
  * Emit/IconBuilder -- wraps a 48x48 PNG in a one-entry ICO (the header needs a
    real icon container, alpha preserved, not the magenta key).
  * Emit/ResidueReport -- the honest "what was lost" report, a first-class
    deliverable shown before install and written beside the pet.
  * ShimejiEngine.Serialize + ConvertSkin tie the pipeline together.

Acceptance is machine-checkable: the emitted XML must pass the app's OWN
validator, round-trip, and be fully reachable (terminals allowed). Gated by a
committed synthetic EmitterSelfTest. Validated live: both reference skins
(Shimeji + KuroShimeji) convert to accepted pets (24 animations, 0 unreachable,
6 dropped / 32 degraded), and the converted pet passes `verify` like a shipped
pet.

Deliberately NOT done: the reachability unification onto the host's
AnimationReachability. PetGraph is a correct, dependency-light reachability
check and the emitter's acceptance bar only needs "did I orphan anything";
switching would drag the animation runtime + shims into the engine for parity
the converter does not require. Kept lean on purpose.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `2563f4433`

**feat(shimeji): sprite compositor -- poses to one equal-cell magenta-keyed sheet**

```
Stage 2 of the Shimeji Importer plan. Extend the model/parser to capture poses
(Image / ImageAnchor / Velocity / Duration) and add SpriteSheetBuilder, which
composites a skin's individual pose PNGs into ONE equal-cell sheet in the exact
shape the engine slices (Xml.ReadImages):

  * Anchor alignment -- every frame is placed so its ImageAnchor hotspot lands at
    the same point in the cell, baking the x-offset the y-only <offsety> cannot
    carry. Frames are deduped by (image, anchor).
  * Magenta key -- alpha is hard-thresholded onto #FF00FF (below the cutoff keyed,
    at/above opaque), which avoids the halo a blend would leave; genuine magenta
    art is nudged to (254,0,255) so it is not keyed out.
  * Budget -- cells <= 256 px, <= 1024 tiles, whole XML <= 4 MiB, with a uniform
    downscale to fit and a loud failure when a skin cannot.

Gated by a committed synthetic CompositorSelfTest (solid-rectangle frames, no
copyrighted art) folded into the aggregate EngineSelfTest that `selftest` runs.
Validated live on both reference skins (Shimeji + KuroShimeji): 48 frames, 7x7
tiles, 192x208 cells, ~410 KB projected XML, anchor-aligned with clean edges. A
dev `composite <conf> <img> <out.png>` verb writes the sheet for eyeballing.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `4f0814dd4`

**feat(shimeji): Shimeji conf parser + Group 1/2/3 fidelity classifier**

```
Stage 1 of the Shimeji Importer plan. Add the input half's front end to
ShimejiConvert.Engine:

  * Shimeji/ShimejiParser -- tolerant, namespace-blind reader of a Shimeji conf
    dir (actions.xml + behaviors.xml). Drives off observed Type values rather
    than Mascot.xsd (the vendor's own actions.xml uses nine Types the vendor's
    schema forbids).
  * Shimeji/ActionClassifier -- buckets each action into Group1 (converter-only),
    Group2 (needs new host state), Group3 (residue), plus the behaviour-selection
    conditions. A direct port of the census rules.
  * Shimeji/ClassifierSelfTest -- a committed, IP-free synthetic fixture covering
    every classification branch.

CLI verbs: `classify <conf-dir>` prints the full census; `selftest` runs the
classifier self-test (no args, gate-friendly). Both build on the shared engine.

Validated against an external gil/shimeji-ee clone: 91 actions -> 53/32/6 and
24 behaviour conditions -> 5 map cleanly / 19 need state, an exact match to the
recorded census. The real config is copyrighted and stays out of the repo, so
run-gate.ps1 gates the synthetic fixture via `selftest`; the 91/53/32/6 check is
the `classify` dev command. MAPPING.md records the executable census and the
cursorX/selfX rationale for Stage 5.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `c68bda321`

**chore(catalog): record the fortunes 1.2.1 payload hash**

```
Regenerated by New-ContentCatalog.ps1 after publishing fortunes 1.2.1.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `3c114dc6d`

**chore(modules): publish fortunes 1.2.1**

### 2026-08-24  `df32b2508`

**fix(fortunes): bump to 1.2.1 so the repeat-jokes fix ships**

```
Commit 35324a5 fixed the fortune engine ("don't repeat the same fortune so
soon") but never rebuilt/republished modules-dist/fortunes.zip, so the fix
never reached the catalog and the publish-freshness gate was red. Bump the
version and republish (next commit) so existing installs are actually offered
the update instead of an in-place republish they would never see.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-24  `8de55336d`

**refactor(shimeji): extract ShimejiConvert.Engine shared library**

```
Stage 0 of the Shimeji Importer plan. Move the source-linked validator
(AnimationXML / CompanionXmlValidator / SafeExpression), the schema + frame-limit
shims, the reachability pass (PetGraph) and the round-trip helper out of the
CLI into a new net10.0-windows library, tools/ShimejiConvert.Engine, exposed
through a public ShimejiEngine API (TryValidate / Analyze / RoundTrips) so the
CLI and the forthcoming ShimejiImporter module share one copy of the rules.
GraphReport is promoted to public; the sprite-frame / entry-point drift guard
moves with the shim it protects.

Behaviour-preserving: `ShimejiConvert verify Pets/` is byte-identical to before
(22 valid, 22 round-trip, 7 with unreachable). That verify run is wired into
tests/run-gate.ps1 as a gated step.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-23  `35324a58b`

**fix(fortunes): stop the pet repeating the same jokes so soon**

```
The random speak path drew fortunes independently, so by the birthday
paradox lines recurred long before the pool was used up, and it only
avoided the single previous line. Replace it with a shuffle-bag that
hands out a fresh permutation and reshuffles only when empty, so every
fortune shows once before any repeat; the bag boundary is guarded so a
refill can not repeat the previous line.

The smart/contextual picker collapsed thousands of lines to the 32
nearest the foreground window and kept only a 24-deep anti-repeat
window, so a stable window churned the same handful. Widen TopK 32->64
and deepen RecentMemory 24->200 so a window rotates through far more
before recycling and variety carries across windows.

Adds shuffle-bag coverage to FortuneEngineProbe (full sweep before
repeat, full second sweep, no seam repeat). Engine + module self-tests
pass; stable_context_distinct rose to 40/40.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-21  `e6222ae2d`

**docs: fix stale build prerequisites, record the session, file the freshness gap**

```
Readme -- the Building section still required "Visual Studio 2022+ with the .NET
Framework 4.8 targeting pack". That has been wrong since the .NET 10 migration:
all twelve projects target net10.0-windows and global.json pins SDK 10.0.302 with
rollForward disable. Replaced with the real prerequisite, put tests\run-gate.ps1
at the top of the build block (it is the verification and was not mentioned at
all), swapped the msbuild CoreTests line for dotnet build, and added a bullet for
tools/ -- developer tooling that is deliberately not part of the product and not
built by build.ps1 or the gate.

BACKLOG -- #4 moves to IN PROGRESS with what shipped, what the harness measured,
and the next slice written out concretely enough to start cold. Also files a new
Bugs & maintenance item: Test-ModulePublishFreshness compares commits under
modules/<Id>/ only, but modules/PetStudio compiles four files out of src/, so a
shared-source edit changes PetStudio.dll while the check stays green. This
session's Mp3Format refactor is exactly that case -- behaviour-neutral and
invisible -- and it is the same failure class the script's own docstring cites
("aibrain.zip sat one release behind PR #71"), arriving via shared sources.
Deliberately not republishing petstudio for it: a version bump with no
user-visible change is worse noise than the drift.

handoff -- new START HERE block for 2026-08-21. Header date was stale (said
08-18 while the top block was 08-20), and line 2 hard-coded a checkout path from
a different machine; this file is public, so it now says nothing about where the
repo lives. Records what is deliberately unfinished, the two latent tooling bugs
fixed and why each hid (one needs a second gate run, one needs a tilde in TEMP),
and three mistakes worth not repeating -- chiefly that grimoire/03 already
documented the pet-XML behaviour I wrote up as findings.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-21  `d6f9954b1`

**feat(tools): Shimeji converter groundwork -- a verify harness and the mapping study**

```
Opens BACKLOG #4 (Shimeji -> animations.xml). Ships no conversion yet, on purpose:
the first verb is `verify`, not `convert`, because the emitter half can be proven
against pets this repo already has before a single Shimeji file is parsed.

  ShimejiConvert verify Pets
  -> 22 pets, 0 invalid, 0 round-trip failures, 7 with unreachable animations

Built as a console tool under tools/, not a module: BACKLOG #4's own workflow
(convert -> hand-check -> commit to Pets/) is a dev workflow, and a CLI iterates
far faster than a tray app. The engine stays separable so a module could wrap it
later unchanged. Not built by build.ps1 or the gate while it is a stub.

It recompiles CompanionXmlValidator.cs (source-included, the same trick
tests/DesktopPet.CoreTests uses) instead of reimplementing the rules, so
candidate pets are graded by exactly what the app enforces and there is no second
copy to drift. Two constants it must mirror rather than import are pinned by
build-time guards that fail the build if src/dotNet/Xml.cs stops agreeing --
negative-tested by sabotaging one and confirming the build breaks.

PetGraph adds the reachability pass the validator genuinely lacks: it proves
referential integrity (every next target exists) and never proves reachability,
so a pet can validate with animations no spawn can reach. Calibrating against the
shipped corpus corrected the model twice -- first the four magic animation names
(grimoire/03 section 7) are roots, not nodes, which is why 21 of 22 pets looked
disconnected; then per section 6's respawn rule, terminal animations are
intentional, so Terminal is informational and only Unreachable is a signal.

MAPPING.md separates what grimoire/03 already documented from what this pass
added, so the next session does not re-derive it. It also records the two traps
on the source side: Shimeji's own conf/Mascot.xsd restricts Type to six values
while its shipped conf/actions.xml uses nine, so validating input against the
vendor schema rejects the vendor's reference skin; and Type="Embedded" names a
Java class, which is code and does not convert.

No Shimeji assets are vendored -- we ship the converter, not copies.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-21  `baf35c02d`

**fix(tests): clear self-test markers without tripping over a tilde in TEMP**

```
run-gate.ps1 deleted each self-test's stale marker with

    if (Test-Path -LiteralPath $markerPath) { Remove-Item -LiteralPath $markerPath -Force }

Remove-Item still performs ~ home-directory expansion even under -LiteralPath.
Windows sets TEMP to the 8.3 short form whenever the account name exceeds 8
characters, and that form contains a tilde, so the cmdlet failed with "An object
at the specified path ... does not exist" for a path Test-Path had confirmed one
line earlier. The gate then aborted mid-run, three self-tests in.

It was latent because run one has no marker to delete: the first gate run on a
fresh box passes, and only the SECOND fails. That is also why it never showed up
in CI, where the runner's profile is short enough that TEMP has no tilde.

[IO.File]::Delete has no path-expansion behaviour and is a no-op on a missing
file, so the Test-Path guard goes with it. Verified on an affected box: writing a
probe file to $env:TEMP, Remove-Item -LiteralPath fails with the exact gate
error while [IO.File]::Delete succeeds and the file is gone.

Gate now runs clean end to end -- 13 self-tests, source-text invariants, all
three module payload checks, module-template scaffold.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-21  `9686aa467`

**fix(packaging): scope the dirty-module guard to the module being published**

```
The guard that refuses to publish a module with uncommitted source was passing
two pathspecs to git instead of one. In PowerShell,

    @('status', '--porcelain', '--', 'modules/' + $moduleDir.Name)

does not build a 4-element array -- it builds 5, splitting the concatenation so
git received `modules/` AND `AiBrain` as separate pathspecs. `modules/` matches
every module, and a root-level `AiBrain` matches nothing, so the guard actually
tested "is anything under modules/ dirty".

Two consequences, both hit while publishing aibrain 1.2.1:
  * you cannot publish module A while module B has any uncommitted edit;
  * the error names the wrong module -- it reported "modules/AiBrain has
    uncommitted changes: M modules/PetStudio/PetStudio.csproj", sending you to
    look at a clean directory.

Fixed by parenthesising the concatenation. Verified both ways: the old form
yields args [status] [--porcelain] [--] [modules/] [AiBrain] and returns the
dirty PetStudio file, the new form yields [modules/AiBrain] and returns nothing.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-21  `58d0d5b36`

**refactor(validator): compile CompanionXmlValidator without the animation runtime**

```
Moves LooksLikeMp3 off TSound (src/dotNet/Animations.cs) and into
CompanionXmlValidator.cs as Mp3Format.LooksLikeMp3, repointing all three callers --
the runtime, the validator and SecuritySelfTest. Pure move: same bytes checked,
same messages, no behaviour change.

WHY: tools/ShimejiConvert recompiles CompanionXmlValidator.cs so converted pets are
graded by the app's real rules instead of a second copy that can drift. That was
blocked by exactly one symbol -- CompanionXmlValidator called TSound.LooksLikeMp3, and
TSound lives in Animations.cs, which references StartUp 28 times. Reaching a
15-line pure byte check therefore meant dragging the animation runtime and the
app host into an offline console tool.

WHY IT LIVES IN CompanionXmlValidator.cs rather than its own file: EnableDefaultItems
is false in every project here, so a new file must be registered in all three
csprojs that compile the validator -- including modules/PetStudio. Touching
modules/PetStudio makes Test-ModulePublishFreshness mark petstudio.zip stale,
which forces a version bump and an in-app update prompt for a change with no
behavioural effect. One extra type in this file avoids all of that. (The first
attempt did use a separate file and the gate caught the PetStudio build break,
which is what surfaced the cost.)

Verified: tests/run-gate.ps1 green -- 13 self-tests including --security-selftest
(which asserts the MP3 rejection path) and --petstudio-selftest, plus the
source-text invariants and all three module payload checks.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-21  `2cf94af95`

**chore(catalog): record the aibrain 1.2.1 payload hash**

```
Completes the publish sequence for aibrain 1.2.1. catalog.json stores the
SHA-256 of the committed git blob, because that is the byte stream
raw.githubusercontent.com serves -- so the zip commit had to land first
(sha256 97c22455ae531c27, 6,415,630 bytes, 5 entries).

Verified by packaging/Test-ModulePublishFreshness.ps1: all three published
payloads now agree with their source, which clears the STALE aibrain failure
that tests/run-gate.ps1 was reporting.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-21  `73ed1c5e9`

**chore(modules): publish aibrain 1.2.1**

### 2026-08-21  `5a7ccd088`

**fix(aibrain): bump to 1.2.1 so the new tray icon actually ships**

```
0f3def7 swapped the Enable/Disable AI tray icon from the red-X disable-ai.png
to the blue brain-circuit glyph, but it was committed [skip ci] and the module
payload was never rebuilt. modules-dist/aibrain.zip therefore still carried the
old icon, so every download got the retired art.

Worse, it could never self-correct: the in-app Update button compares versions,
and source, modules.json and catalog.json all agreed on 1.2.0. Republishing
1.2.0 in place would fix new installs only, and would leave one version string
meaning two different payloads -- which is what produced this in the first
place. So the version moves.

Found by tests/run-gate.ps1, which flagged
"STALE aibrain -- modules/AiBrain has 1 commit(s) newer than aibrain.zip".
The republish (zip -> commit -> catalog) follows in the next commit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `67354694c`

**docs(backlog): generalize the recorder legal note (drop personal specifics) [skip ci]**

```
The #17 legal-constraint bullet named a specific jurisdiction/role/regulation and
"the owner". This is a public repo, so reword to the generic design rationale —
recordings can carry consent-regulated or privileged audio, so local-only + a
visible recording indicator — without any personal context. No design change.

[skip ci] — docs-only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `b2234b2f6`

**feat(tray): swap Pet Speech icon to the oval speech bubble [skip ci]**

```
Replaces the cloud bubble (which read faint at 16px) with a bolder oval
speech-bubble outline + tail, per the user's supplied art. Same resource
(petspeech -> Images/pet-speech.png), image bytes only; wiring unchanged.
Distinct from Test Speech's blue filled rounded bubble (shape + colour + fill).
Processed to 32x32 RGBA with ImageMagick; base builds clean.

[skip ci] — asset only; locally built + running.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `0f3def7d5`

**feat(tray): distinct Pet Speech + AI-brain tray icons [skip ci]**

```
Two tray items had duplicate/placeholder icons. Give each its own:

- Pet Speech: was a second copy of Test Speech's rounded speech bubble. Now a
  cloud speech bubble (new base resource `petspeech` -> Images/pet-speech.png,
  32x32 RGBA, outline thickened so it reads at menu size). Distinct from the
  rounded bubble (Test Speech), the sheep (Add a pet), and the gear (Options).
  Test Speech keeps the rounded bubble.
- Enable/Disable AI: was the red-X `disable-ai.png`. Now a blue brain-circuit
  glyph fitting the AiBrain module and the menu's blue accents
  (modules/AiBrain/Resources/ai-brain.png, embedded; LoadIconResource repointed).
  Retired the now-unused disable-ai.png (file + EmbeddedResource).

Both projects build clean; AiBrain.dll manifest confirmed to embed ai-brain.png
and not disable-ai.png. Icons processed from user-supplied art with ImageMagick.

[skip ci] — locally built + verified; save Actions minutes.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `5929557eb`

**docs(backlog): add #18 — evaluate standalone tray utilities as pet modules [skip ci]**

```
Captures the read-only assessments of three bigfnj tray apps as candidate
modules: LightHost (C++/JUCE VST effects host — no capture code, GPLv3, not a
fit for the mic module; use NAudio), blinkingLED (C#/WinForms, port-with-work,
needs a LICENSE), IdleLauncherTray (C#/WinForms, port-with-work, GPLv2 must be
relicensed first; global low-level hook must be unhooked on ALC unload).

Two cross-cutting findings: (1) ModulePermissions has no flag for the
capabilities these need — audio capture, synthetic input, input monitoring,
process launch — so consent would under-disclose; add flags before shipping a
suite (this also gates #17). (2) Licensing is a recurring gate against the MIT
host. Plus a reusable same-stack port recipe.

[skip ci] — docs-only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `0a35ca023`

**docs(backlog): record Web Speech API considered+rejected under #17 STT [skip ci]**

```
Captures why the browser SpeechRecognition API isn't the STT path: mic-only
(can't ingest the recorded file or the system-loopback/far-end audio),
cloud-by-default and non-functional in embedded Chromium (works only in
Google-branded Chrome), and it would re-add the WebView2 engine S5b-3 removed.
Chrome 139's on-device mode fixes privacy but not the other two. Whisper-class
on the recorded file remains the choice. Prevents re-litigating it later.

[skip ci] — docs-only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `6d35260dc`

**docs(backlog): expand #17 — pet as record→transcribe→summarize orchestrator [skip ci]**

```
Captures the fuller vision from the design discussion and grounds it in the
actual ABI: the pet-as-trigger part is already expressible (RegisterPokeResponder
/ RegisterHotkey / AddTrayItems) and the bubble+animation surfaces make the pet a
real status indicator, not just a launcher. Records the two frictions found in the
code: (1) modules are ALC-isolated and IHost exposes no summarize/LLM verb, so the
recorder must carry its own Ollama call (or a new host text-gen service is added),
never a module→module call; (2) no speech-to-text exists anywhere, so a
Whisper-class engine is the biggest new dependency. Phasing: capture→MP3, then
local transcript, then local summary. Local-only throughout (CA consent + FERPA).

[skip ci] — docs-only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-20  `aa80652ba`

**docs(backlog): add #17 — tray audio recorder (mic + system loopback → MP3) [skip ci]**

```
Records both the mic and system/loopback audio to a single MP3 from a tray
click. Filed as a candidate module (reuses the tray/module/NAudio-3
scaffolding) or a standalone app — flagged that a meeting recorder isn't
"pet" behaviour, so that framing is a decision, not a given.

Captures the real scoping traps: two-stream mix with a format/rate mismatch,
the WASAPI-payload question the base already decided against for playback,
the silence-stalls-loopback gotcha, MP3 via MediaFoundation or the DevToolbox
ffmpeg, a new capture permission distinct from the playback Audio one, and the
California all-party-consent / FERPA constraint that makes this local-only with
a visible recording indicator. Origin: the 2026-08-20 audio-capture research
(build-it-ourselves vs Meetily/Bandicam).

[skip ci] — docs-only; do not spend Actions minutes on a build.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `d5ce55909`

**docs: session wrap-up -- Readme, BACKLOG closures, and an honest handoff**

```
Readme documents what shipped: the per-pet Pet Speech tray cascade, per-pet poke
ladders, and the two new module capabilities (pet-aware responders + IsCompanionAlive,
and PlaySound/StopSound/RegisterSpeechResponder with the Audio and Voice
permissions), plus WavAudio in the ModuleKit list.

BACKLOG closes the two entries this session actually resolved -- the reported
all-pets-speak-at-once bug and the 'a module cannot play audio' ABI gap -- keeping
the original text beneath each so the reasoning survives.

handoff.md now says plainly what was NOT done. The session was planned as A to F;
A, AA and B shipped and C, D, E and F were never started. There is no half-built
Voice module to find. Part C should begin with the WinRT unpackaged spike rather
than with code, since that is the one genuine unknown, and Kokoro may be
undeliverable on licence grounds, which is recorded as an acceptable outcome
rather than a failure.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `02ac2efc0`

**docs(handoff): record the v1.6.0 audio ABI release**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `b5aa6f1f6`

**Merge pull request #89 from bigfnj/feature/audio-abi**

```
feat(abi)!: audio playback and speech interception for a voice module (1.6.0)
```

### 2026-08-19  `e39e4356c`

**feat(abi)!: audio playback and speech interception for a voice module (1.6.0)**

```
The two gaps that made a TTS module impossible. Both additive.

  ModulePermissions.Audio, ModulePermissions.Voice
  bool PlaySound(string moduleId, byte[] audio, double volume)
  bool StopSound(string moduleId)
  IDisposable RegisterSpeechResponder(string, int, Func<SpeechRequest,bool>)

A byte[] CONTAINER, not raw PCM. A float[] would alias -- the mixer thread reads
it for the life of playback, so a module reusing its synthesis buffer would be
audible as a seam, and defending with a copy gives back the entire saving. It
would also commit the contract permanently to interleaving order, channel
semantics and range clamping, whereas a container commits to nothing and lets a
future codec be a host-side change. Every realistic engine already emits one;
ModuleKit's new WavAudio.FromPcm covers the exception.

TWO permission flags, not a reuse of Speech. Speech means "calls Say/SayAll"; a
voice module never calls Say, it reads and can SUPPRESS every line, which is a
different and privacy-relevant capability -- a speech responder sees every line
the AI brain generated from the user's screen. They are separable in practice
too: a sound-effects module wants playback without interception, a captions
module the reverse.

CLAIMING AND SUPPRESSING ARE SEPARATE. Returning true means "I own the output of
this line", which is not the same as "I spoke it"; SpeechRequest.SuppressBubble
carries the bubble decision. That split is what makes bubble-only, bubble+voice
and voice-instead-of-bubble expressible without overloading one bool.

SpeechRequest.ShowBubble is the load-bearing member. The responder is synchronous
and on the UI thread, so a module must decide whether to claim BEFORE it knows
whether synthesis will succeed. Handing the line back by calling Say/SayAll does
NOT work: SayAll compares against the last line said, and with the default
suppress-repeats preference on, the identical replay is swallowed and the line
vanishes. Only the host can bypass both the chain and that guard.

AudioOutput: a decode seam sniffed by magic bytes (RIFF/WAVE or ID3/MPEG sync),
resampled and upmixed through the path Decode already used, rejecting >2 channels
explicitly so the caller gets false rather than the mixer throwing into a silent
catch. Module audio NEVER enters _cache -- it is keyed by byte[] reference
identity and cleared only in Dispose, so caching speech would retain every line
the pet ever spoke plus a buffer ~7x larger. Pinned by an invariant.

Barge-in cuts by ramping out over ~10 ms and returning short, so NAudio drops the
input; muting a VolumeSampleProvider would leave a silent input occupying the
mixer for the utterance's full remaining length. The live-input registry has its
OWN lock: MixerInputEnded fires on the audio callback thread inside the mixer's
source lock while callers hold _sync and then take that same lock, so sharing one
would be an ABBA deadlock.

Shutdown reordered so modules shut down BEFORE the audio output is disposed --
previously a module calling StopSound during teardown was talking to a disposed,
then nulled, output. Safe only because PlaySound takes a byte[] the host decodes
into its own buffer, so no module-owned provider is ever in the mixer.

ALSO FIXES A LATENT CATALOG BUG. An unrecognised permission name made Parse throw
for the ENTIRE catalog, not the entry -- and because every catalog feature shares
one fetch, the first release to add a flag silently took the Modules pane, the
monthly update check, pack browsing AND the Pets gallery away from every older
host. It had already fired unnoticed: Pets shipped in 1.4.4, so a v1.4.2 host
cannot parse today's catalog at all. Unknown names are now dropped and the entry
kept; an empty or malformed list is still rejected. Publishing the Voice module
would otherwise have done this to every host below 1.6.0.

New --audio-selftest (13 assertions, deliberately device-independent so it runs
on a CI runner with no playback device): resample+upmix proven by frame count,
every rubbish input rejected rather than thrown, and the barge-in ramp terminating
across read sizes smaller than itself.

The cache invariant was negative-tested and FAILED to fail on the first attempt --
a brace-counting regex could not see past PlayOwned's inner lock block. Rewritten
to slice by position. That class of dud assertion only ever surfaces by trying it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `ce505f8d9`

**docs(handoff): record the v1.5.0 release and the decisions taken unattended**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `3deeafc77`

**Merge pull request #88 from bigfnj/feature/modules-per-pet-speech**

```
feat(modules): speak to one pet, not all of them (fortunes + aibrain 1.2.0)
```

### 2026-08-19  `f65d196e8`

**chore(catalog): regenerate for fortunes 1.2.0 + aibrain 1.2.0**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `ae044eba1`

**chore(modules): publish aibrain 1.2.0**

### 2026-08-19  `d7356b757`

**chore(modules): publish fortunes 1.2.0**

### 2026-08-19  `5dfa290c5`

**feat(modules): speak to one pet, not all of them (fortunes + aibrain 1.2.0)**

```
The module half of per-pet speech. The host shipped in 1.5.0; both modules now
declare MinHostVersion 1.5.0 and use the pet-aware responders, so a reaction
reaches the pet it belongs to instead of every pet on screen reciting it in
unison. This is the user-visible end of the reported bug.

FORTUNES 1.2.0
Drop and poke register pet-aware; SpeakFortune takes the subject and speaks it
with Say(pet, ...). CompanionLanded now speaks to the pet that landed -- previously
adding a fourth pet made all four say the same fortune the moment one touched
down, which was the second most visible face of the bug. The screen context is
captured from the subject too, so a contextual pick describes the window THAT
pet is standing on rather than another pet's.

The welcome deliberately stays SayAll, with a comment saying why: it is a
once-per-session greeting addressed to the USER, not a reaction belonging to a
pet, and it fires on first spawn when there is normally one pet anyway.

AI BRAIN 1.2.0
Ask takes the subject through to the async completion rather than re-reading
_lastPet there -- CompanionSpawned, CompanionLanded and CompanionPoked all move it, and a model
round trip is easily long enough for that to happen.

The thinking cue was a second instance of the same bug: PlayAnimationAll +
SayAll("...") made EVERY pet ponder a question only one of them was asked. Now
routed to the subject via TryPlayAnimation, which needs no new ABI because the
module already owns the emotion -> candidates mapping.

If the pet is gone when the answer arrives, the answer is DROPPED and logged,
not handed to another pet. A different pet answering a question it never asked,
having shown no "..." cue, is the same bug wearing a hat.

Noted but deliberately not fixed: session.RequestInProgress is one global flag,
so two pets cannot be asked concurrently. Correct for 1.5.0; per-pet concurrency
is BACKLOG #16(a).

TESTMODULE
OnPoked speaks to info.Pet. It is the reference module, so it should demonstrate
the policy rather than the bug.

Both self-test fakes capture BOTH registration styles behind FireDrop/FirePoke,
so the assertions survived this migration instead of needing to change in
lockstep with it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `7711eee12`

**Merge pull request #87 from bigfnj/feature/per-pet-speech**

```
feat(abi)!: per-pet speech routing — a reaction belongs to one pet (1.5.0)
```

### 2026-08-19  `78bc38d30`

**feat(tray): a Pet Speech cascade to pick which module speaks for each pet**

```
Tray > Pet Speech > <pet> > <source>, with a tick on the EFFECTIVE source (a pet
with no entry of its own shows the all-pets choice, which is what actually
happens), an "All pets" row, and a "Reset all pets to the default" row.

Host-owned rather than module-contributed, for three reasons that all point the
same way: per-pet preferences already belong to the host by an existing decision;
a module cascade would need ModulePermissions.Pets merely to enumerate; and
TrayItem has no Checked, so a module could not render the tick. Deliberately not
adding TrayItem.Checked -- nothing else would consume it.

THE TRAP THIS AVOIDS, now pinned by an invariant. triggerSpeech uses "" to mean
the ALL-PETS entry, while the pet mix writes the active/default pet as "". Keying
a real pet by its raw mix id would therefore rewrite the global preference the
moment anyone touched the eSheep row -- and it would look like it worked, because
the lookup falls back to global, so every OTHER pet type would test fine.
SpeechRoutingKey resolves the active pet to its real type id, matching what
ICompanion.TypeId and the per-pet size/sound settings already use.

Pets come from OnScreenMix(), the single enumeration that already excludes
authoring previews, rather than walking sheeps[]. Sources come from
OptionsShell.BuildTriggerSpeechOptions, so the tray and the Preferences dropdown
cannot drift apart on labels. No "xN" suffix (unlike Remove a pet): the count is
irrelevant to a per-type setting and would imply each copy is configurable.

An uninstalled-but-chosen source shows a disabled, ticked "<id> - not installed"
row. Falling back to showing the default as ticked would be a lie: an explicit
choice is a restriction, so that pet is silent, not random.

Also fixes an anchoring bug this change would otherwise have introduced: the
module tray section anchored after Test Speech, and Pet Speech was inserted
between them, so module items would have landed inside the base's speech block.

LocalData gains TriggerSpeechPetIds() to back the reset row, which is the only
way back once a per-pet choice outlives the pet it was made for -- the
Preferences reset deliberately clears only the global entry.

Five new source-text invariants: the routing key, the tray anchor, sass being
routed rather than broadcast, broadcast speech and animation skipping previews,
and Say guarding a disposed pet.

Gate green: 0 warnings, 37 CoreTests groups, 12 self-tests, 11 invariants,
payloads, template.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `eebf8341b`

**feat(abi)!: pet-aware responders, so a reaction belongs to one pet (1.5.0)**

```
Adds three IHost members and routes the host's own reactions through them.

  IDisposable RegisterCompanionDropResponder(int, Func<ICompanion,bool>)
  IDisposable RegisterCompanionPokeResponder(string, int, Func<ICompanion,bool>)
  bool IsCompanionAlive(ICompanion)

WHY THESE ARE NEW NAMES RATHER THAN OVERLOADS. A parameterless `delegate { }`
converts to both Func<bool> and Func<ICompanion,bool>, and neither is a better
conversion target, so overloading would turn
`RegisterDropResponder(0, delegate { return true; })` into CS0121 for anyone who
recompiles. LangVersion 7.3 means that spelling is everywhere in this repo, and
third-party modules will copy it. Binary compatibility would have survived;
source compatibility would not. Distinct names cost nothing.

WHY IsCompanionAlive IS ON IHost, NOT ICompanion. ICompanion has seven implementations here and
ModuleKit ships FakeCompanion : ICompanion, so adding a member to ICompanion breaks modules and
their test doubles on recompile -- the one way "additive" still breaks someone.
IHost is implemented only by hosts and fakes, and this repo already accepts
updating eight of those per ABI change.

Both registration styles share ONE priority list, so a migrated module and an
unmigrated one still compete fairly; a legacy registration is wrapped as
`pet => f()` at registration time. Two parallel lists would have made "who fires
first" depend on which style was used.

The host now resolves the speech preference ITSELF from the subject pet, so the
poke and drop chains cannot disagree about what a pet's speech source is.
StartUp.TryPokeReaction used to read it with a hard-coded "" key -- which is the
ALL-PETS entry -- so a per-pet choice could never have applied even once the
storage supported it. SpeechRoutingKey resolves the active pet to its real type
id, because the pet MIX writes the active pet as "" while "" in triggerSpeech
already means global: keying a real pet as "" would silently rewrite the
all-pets preference and still look correct, since the lookup falls back to
global.

Drops now belong to one pet too, chosen round-robin (PickDropSubject) rather
than uniformly at random -- random lands on the same pet several times running
often enough to read as "still broken" -- with the cursor seeded randomly so a
session does not always start on pet #1.

POKE ESCALATION IS NOW PER PET. pokeCount, the 7s session reset and the 12s
rich-reaction cooldown were three shared fields: poke Pearl three times then
Rick once and Rick answered at the sass tier, and poking four pets in turn gave
one reaction and three silences. Invisible while everything was broadcast,
plainly wrong now that sass goes to the pet you clicked. Held in a
ConditionalWeakTable so a removed pet's state is collected with it.

Base reactions routed: sass and the turn-away go to the poked pet; the bathtub
escape stays global on purpose (every pet fleeing IS the joke) and now says so.

ModuleKit's RecordingHost gains SaidToCompanions, BroadcastLines and a settable
CompanionAlivePredicate. Say and SayAll both wrote only SaidLines, which made "did the
module route this line or broadcast it?" -- the exact distinction this release
introduces -- impossible to assert. SaidLines stays as the union so existing
third-party tests keep working.

SecuritySelfTest's arbitration assertions now use RaisePokeReactionFor, an
explicit-preference seam, so they still test the chain's semantics without a
live pet and settings file.

Modules still register the legacy way and still broadcast; migrating Fortunes
and the AI brain is the next commit. Gate green: 0 warnings, 37 CoreTests
groups, 12 self-tests, invariants, payloads, template.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `71957226c`

**Merge pull request #86 from bigfnj/fix/release-workflow-collision**

```
fix(ci): stop two workflows racing to publish the same tag
```

### 2026-08-19  `e05d1cd7e`

**fix(speech): make the repeat guard per pet, and stop broadcasting to previews**

```
Groundwork for per-pet speech routing. Three defects, all invisible today only
because every speaker broadcasts the same line to every pet.

THE REPEAT GUARD WAS BYPASSABLE AND WRONG FOR SEVERAL PETS.
It lived in StartUp.SayAll as a single global "last broadcast line". Bypassable:
IHost.Say(pet, text) goes straight to FormCompanion.Say, so the moment modules address
one pet instead of broadcasting -- which is the whole direction of this work --
the guard stops seeing the lines it exists to de-duplicate and the user's
"don't repeat yourself" preference silently stops working. Wrong for N pets:
Pearl saying "X" should not silence Rick saying "X", since those are different
bubbles and no repetition the user can perceive, while Pearl saying "X" twice
genuinely is a repeat. Moved into FormCompanion.Say, keyed per pet, so every path
inherits it and none can route around it. The punctuation-only HasContent rule
moves with it and matters more per-pet, because the AI brain's "..." cue and its
answer now land on the same pet.

SayAll AND PlayAnimationOnAll SPOKE AND EMOTED THROUGH AUTHORING PREVIEWS.
Both walked sheeps[] directly, contradicting the documented invariant that a
transient preview pet is invisible to modules. Added PersistentPets() as the one
place the preview filter is stated -- it was previously re-derived at each call
site, which is precisely how such an invariant rots -- and pointed
FirstPersistentPet, SayAll and PlayAnimationOnAll at it.

CompanionHost.Say HAD NO DISPOSED GUARD AND NO Safe WRAPPER.
A module holds an ICompanion for as long as it likes (Fortunes and AiBrain both keep a
_lastPet, and there is no CompanionRemoved event to tell them otherwise), so a pet the
user removed mid-answer is a normal case. Unguarded, FormCompanion.Say builds a fresh
FormSpeech on a disposed form and throws out of the module's call -- fatal on the
AI brain's async completion path. SayAll is structurally immune because it walks
the live list; Say was not.

Also replaced the poke-responder sort tie-breaker, which recovered registration
order with IndexOf against the very list being replaced: correct only because the
sort ran over a copy, O(n^2), and one refactor from silently changing the
"Default & Random" pick order. A monotonic Seq states the intent directly.

Gate green: 0 warnings, 37 CoreTests groups, 12 self-tests, invariants, payloads,
template.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `de521af8e`

**Merge pull request #85 from bigfnj/feature/petstudio-host-theme**

```
Clear the open backlog: Pet Studio host theme, a module-window leak soak, and per-pet poke attribution
```

### 2026-08-19  `e114b2ff3`

**fix(ci): stop two workflows racing to publish the same tag**

```
Every tagged release was built and published TWICE. release.yml and
publish-release.yml both triggered on push tags v*, both ran the full build, and
both ran `gh release upload --clobber` against the same GitHub release. Whichever
finished last won, so SHA256SUMS.txt listed the module-author nupkgs or not
depending on who lost the race. Every release was non-deterministic.

publish-release.yml's own header claimed release.yml was "manual-dispatch only",
which was factually wrong about its sibling's trigger and is presumably how the
collision survived.

Consolidated into release.yml, which already packed the nupkgs, and deleted
publish-release.yml. Its two correctness properties were folded in, because
release.yml had NEITHER:

  - it checked out the TAG. release.yml's checkout had no ref, so a
    workflow_dispatch re-run built the DEFAULT BRANCH and uploaded those
    artifacts under the requested tag's release, publishing something that was
    never tagged.
  - it verified the tag against ProductVersion.props. release.yml validated only
    the vMAJOR.MINOR.PATCH shape, so a tag disagreeing with the product version
    published happily -- and after an ABI change that means a stale
    Contracts.dll no module can resolve.

Added a concurrency group as well: two concurrent runs of the surviving workflow
would reproduce the same clobbering that the two-workflow collision caused.

Left alone deliberately: release.yml still runs microsoft/setup-msbuild, which is
vestigial now that build.ps1 no longer probes MSBuild and the MSI is built by the
wix dotnet tool. It costs seconds, and the release path is the wrong place to
discover an implicit dependency. Recorded in BACKLOG instead.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `686a075ba`

**fix(pets): attribute a poke to the pet that was actually clicked**

```
FormCompanion knew which pet the user right-clicked and threw it away: it called
OnPetPoked() with no argument, and StartUp then recovered 'a' pet with
FirstPersistentPet(). So poking pet #5 was reported to every module as a poke on
pet #1, and PokeInfo.Pet was wrong for every pet except the first.

Invisible today, because every speaker broadcasts through SayAll and all pets say
the same line at the same moment anyway (backlogged separately). It becomes
silently wrong the instant anything reacts per pet, which is where the per-pet
speech routing work is heading, so it is fixed first as the foundation.

FormCompanion passes 'this'; StartUp gains an OnPetPoked(FormCompanion) overload and keeps
the parameterless one delegating to it. A preview still never becomes the subject
of a poke, and a caller that cannot say which pet still falls back to the first
persistent one, so nothing else changes behaviour.

Pinned in runtime-hardening-selftest.ps1: dropping 'this' again would restore the
bug with no test failing anywhere.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `642738985`

**docs(backlog): close the About window eyeball with a rendered capture**

```
Rendered AboutWindow to a PNG from a throwaway reflection harness rather than
deferring to the next reinstall. Dark theme, all six allowlisted doc links, and
the layout all confirmed good. The harness is deliberately not committed: a
permanent render harness for one static window is not worth the machinery.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `dc6b02f13`

**test(soak): commit a leak soak for a module-owned window**

```
runtime-resource-soak.ps1 drives the shipped app from outside, and its churn
loop (Program.RuntimeResourceChurn) only exercises pets/speech and the tray, so
it never opens a window a module owns; the host also keeps no compile-time
reference to any module. A module window's HWNDs, Bitmaps and decoded sprite
sheets were therefore covered by nothing, and the soak that found the sprite
re-decode bug existed only as prose in handoff.md.

tests\DesktopPet.WindowSoak is a separate UseWPF console exe (CoreTests is
UseWindowsForms, so this could not live there) that loads the module DLL at
RUNTIME and drives it by reflection. There is deliberately no reference to
PetStudio.dll: PetStudioWindow is internal sealed, so a compile-time reference
would buy nothing, and leaving it out keeps the project free of build-order
coupling. It reuses ModuleKit's RecordingHost instead of hand-rolling a fake
that would rot on every ABI addition, and a missing reflected member is a hard
FAIL rather than a skip.

Not in the blocking gate: run-gate.ps1 excludes leak soaks because growth
thresholds flake on a headless runner. Wired into RELEASE-CHECKLIST.md as a
pre-tag step beside the existing soak.

Pet Studio, 2 x 20 cycles: segment 2 handles +0, GDI +0, USER +0,
private -7.8 MB.

THE TRAP, worth knowing before writing any WeakReference leak test: exactly one
window per segment looked rooted, always the last (cycle 7 of 8, cycle 19 of
20). Not a leak, and not Application.MainWindow -- the strong reference was
ESCAPING the cycle method and sitting in the caller's stack slot until
overwritten. Fixed by returning a WeakReference rather than the window, and
marking the cycle NoInlining, so the only strong reference lives in a frame
guaranteed to be torn down. A displacer window was tried first, did nothing, and
was removed rather than left in looking meaningful.

Negative-tested rather than assumed: rooting each window in a static list fails
it on two independent signals, all cycles rooted instead of none and segment-2
private bytes +31.4 MB instead of -9 MB. Reporting WHICH cycles are rooted is
what separates a real leak (all of them) from the artifact above (only the last).

Also backlogs a bug reported today: every pet on screen speaks the same line at
the same moment, because StartUp.SayAll fans one string out to every pet in a
single loop and every speaker goes through it. Pet type is irrelevant.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `e49ef1a8e`

**chore(catalog): regenerate for petstudio 1.1.1**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-19  `488b3858b`

**chore(modules): publish petstudio 1.1.1**

### 2026-08-19  `f2758e64e`

**feat(petstudio): theme the studio window from IHost.IsDarkTheme (1.1.1)**

```
PetStudioTheme read the OS registry (AppsUseLightTheme) directly, which is
correct only while the host sits on its default system setting and wrong the
moment a user pins the opposite -- the host's real preference was invisible to
modules until IHost.IsDarkTheme landed in 1.4.7. Current() now takes the IHost
and asks it; a null or throwing host falls back to light, the same direction the
host's own resolver fails in.

The DESKTOPPET_FORCE_THEME env override goes with it. The settable
RecordingHost.IsDarkTheme is a better version of what it was for, so
--petstudio-selftest now drives the theme both ways plus the no-host case; before
this it asserted nothing about theming at all and its fake host hardcoded
IsDarkTheme to false.

Non-obvious: PetStudioWindow built the theme in a FIELD INITIALIZER, which runs
before the constructor body assigns _host, so it had to move into the ctor.

MinHostVersion 1.4.6 -> 1.4.7, module 1.1.0 -> 1.1.1. Also corrects a stale
BACKLOG note that said About/Help windows plural: Help was folded into
AboutWindow and there is no HelpWindow file to find.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-18  `2b0f010a5`

**Merge pull request #84 from bigfnj/docs/session-wrapup**

```
Session wrap-up: Readme, handoff START HERE, backlog accuracy, stale-file cleanup
```

### 2026-08-18  `7643ca814`

**docs: session wrap-up — Readme, handoff START HERE, backlog accuracy**

```
Readme knew nothing about the last three releases. It now documents Pet Studio
(the reachability map, the frame preview, and what the tool is actually for) and
gains a "Writing your own module" section -- which matters because the point of the
SDK is that someone can do it WITHOUT cloning this repository, and the Readme is
where they would look. It states the deployment story honestly: no signing gate, no
allowlist, build a DLL and drop the folder in.

handoff.md gains a START HERE block, because the useful thing to tell the next
session is that nothing is half-finished. It also records the two traps that cost
real time: publish a module's SOURCE before its payload (the freshness check
compares commit recency and a deterministic re-zip cannot repair a bad order), and
verify master against origin/master rather than a local branch that may have no
upstream.

BACKLOG had gone stale in two places, both now marked done rather than pending: the
Fortunes/AiBrain ModuleKit migration, and the failed-module-invisible bug. Left in
place with reasons: the audio gap, the overlay gap, IsDarkTheme adoption, the
module-window leak soak, and Phase B.

Also drops src/packages -- 38 MB of untracked net48-era NuGet leftovers, unreferenced
by any project (only src/packages.lock.json, a different file, is referenced), and
verified with a clean build afterwards. And redacts a literal personal email from two
docs; it added nothing the public commit history does not already show.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-18  `3acac4df0`

**Merge pull request #83 from bigfnj/docs/stable-not-frozen**

```
Stable, not frozen: replace the freeze with six rules
```

### 2026-08-18  `2e91e5c49`

**docs: replace the freeze with six rules, and record the audio ABI gap**

```
The handoff still opened by telling the next reader the host was being frozen. That
framing has now cost more than it bought, so it is gone rather than softened again.

The freeze failed three times in three days: reopened at 1.4.6 for
ICompanionManager.CompanionsDirectory, then 1.4.7 for IHost.IsDarkTheme and IHost.Log, then
1.4.8. Building ONE module plus the SDK surfaced THREE ABI gaps, which is what
building reveals rather than a lapse in foresight. It had also pushed a real UX
defect -- a failed module being invisible, whose only escape deleted the user's
settings -- into BACKLOG as a "post-freeze fix".

What a freeze was reaching for is "a module written today keeps working", and that
comes from invariants, not from refusing to add: AssemblyVersion pinned at 1.0.0.0;
additive only, never remove or redefine; a product bump in the same commit as an ABI
change; never declare an event you do not raise; raise MinHostVersion only when you
actually call a newer member; and do not move a source-linked engine file without
re-running the parity self-test. All six are already enforced by code or gates, so
this documents reality instead of aspiration.

Also records the audio gap, because a planned module will hit it: IHost exposes
Volume read-only and no playback verb, while the base owns a full DirectSound mixer.
A TTS/voice module is therefore impossible today. Noted with the shape it would take
(a PlaySound routed to the existing mixer, gated on a new ModulePermissions.Audio)
and the instruction to add it WITH that module rather than speculatively -- which is
exactly the workflow a freeze would have forbidden.

Current state refreshed to 1.4.8: three releases, modules at fortunes 1.1.2 /
aibrain 1.1.2 / petstudio 1.1.0, and the box now running everything installed
through the catalog rather than copied in by hand.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `7d7a9b354`

**Merge pull request #82 from bigfnj/refactor/modules-on-modulekit**

```
Build Fortunes and AiBrain on ModuleKit (-820 lines)
```

### 2026-08-17  `141b8c605`

**fix(packaging): refuse to publish a module with uncommitted source**

```
The ordering trap that bit me publishing the ModuleKit migration.
Test-ModulePublishFreshness compares commit RECENCY, so committing the zip before
the source it was built from reads as stale even though the bytes are correct. And
because the zip is deterministic, re-zipping afterwards produces identical bytes --
so there is no new commit available to repair the order. The only exits are
rewriting history or a dummy commit.

So refuse up front, naming the dirty files, rather than letting the sequence get
into a state the script cannot undo. Verified by dirtying a module source file and
watching the publish stop.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `9b617ec86`

**chore(modules): republish fortunes 1.1.2 and aibrain 1.1.2**

```
Rebuilt payloads for the ModuleKit migration, plus the modules.json versions and the
regenerated catalog (22 pets, 152 packs, 3 modules).

Committed AFTER the module source on purpose. Test-ModulePublishFreshness compares
commit RECENCY, so publishing the zip first and the source second makes the payload
look stale even when its bytes are correct -- and a deterministic re-zip produces
identical bytes, so there is no new commit available to fix the order afterwards.
Source first, payload second.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `4653a449b`

**refactor(modules): build Fortunes and AiBrain on ModuleKit**

```
Deletes 752 lines of duplication that existed only because each module was written
before there was anywhere shared to put it:

- modules/Fortunes/engine/FileHelpers.cs (325) and modules/AiBrain/engine/
  FileHelpers.cs (388) held CrossSessionLock + AtomicFile with a byte-identical core;
  AiBrain's copy differed only by also carrying TryWriteAllText.
- modules/AiBrain/engine/TextHelpers.cs (39) was a copy of UnicodeTextProgress.
- Three hand-rolled "scan manifest resources by trailing name" loaders (AiBrain's
  LoadIconResource, Fortunes' ReadEmbeddedText and LoadWelcomeCorpus) collapse into
  EmbeddedResources.

The wrappers stay where their contract differs from ModuleKit's: ReadEmbeddedText
returns null rather than "" because its callers branch on null, and LoadWelcomeCorpus
still defaults to an empty array. Changing those quietly would have been a behaviour
change dressed up as a refactor.

FortuneProvider deliberately keeps its own resource read. It decodes the bundled
10k-line corpus with STRICT UTF-8 (throwOnInvalidBytes) and distinguishes "resource
missing" from "failed to parse"; ModuleKit's loader is deliberately lenient and
returns "" for both. Consistency is not worth losing that. The other remaining
GetManifestResourceStream call uses an exact LogicalName rather than a scan.

Both modules move to 1.1.2, since the payload really does change: ModuleKit.dll now
ships inside each module folder (~27 KB). Contracts.dll stays absent from all three,
as it must -- the host shares its single copy.

Done now rather than deferred: the reason for holding it back was that a republish
reaches every existing user, and the repo has 0 stars.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `47c6f2d17`

**Merge pull request #81 from bigfnj/chore/release-1.4.8**

```
1.4.8: ship the failed-module fix and the author packages
```

### 2026-08-17  `742ac2890`

**chore(release): 1.4.8**

```
Ships the failed-module UI fix, and the release-asset packaging that actually
delivers it: release.yml only packs and attaches the author NuGet packages on a
NEW tag, so until this ships a third-party author still cannot fetch them.

Also moves the template's MinHostVersion / packageVersion defaults to 1.4.8, so a
newly scaffolded module targets the current host. The remaining 1.4.7 references in
BACKLOG and docs are historical on purpose -- they record which release introduced
IsDarkTheme and Log.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `3fc43c453`

**Merge pull request #80 from bigfnj/fix/failed-module-visible**

```
Make a failed module visible and repairable, and let a module be built outside the repo
```

### 2026-08-17  `6a3dd3929`

**feat(sdk): let a module be built outside this repo**

```
Writing a module meant cloning the repository, because the template referenced the
contract and ModuleKit by project path. That was the only real barrier to
third-party authoring -- the module system enforces no signing and sideloading is
unrestricted, so the friction was packaging, not policy.

DesktopPet.Contracts and DesktopPet.ModuleKit are now packable with proper metadata
and nuget.org-facing readmes, and the template takes --standalone, which swaps the
project references for PackageReferences. ExcludeAssets="runtime" on the contract is
the package-world equivalent of Private="false": compile against it, but never copy
it into the output, because the host ships the one true copy and a second one stops
the IModule types unifying.

Proven end to end rather than assumed: a module scaffolded in a temp folder OUTSIDE
the repo, built against a local package feed, produced exactly the right payload
(ModuleKit beside the module, Contracts absent), and the released app loaded it from
a hand-copied folder with --module-selftest passing 13/13.

Deliberately NOT pushed to nuget.org. The packages are attached to each GitHub
release instead, checksummed alongside the app downloads: that unblocks an author
via a local package source without permanently claiming public package ids, and
without committing to publish a new package on every host release even when the
contract has not changed -- the contract's package version tracks the product.
New-NuGetPackages.ps1 packs, verifies (readme present, lib present, and the contract
still dependency-free) and prints the push command without running it.

The docs also point out the simplest path of all: the portable ZIP already ships
DesktopPet.Contracts.dll beside the exe, and a plain Reference to it is enough.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `703d883c4`

**fix(modules): show a module that failed to load, with a way to repair it**

```
A module that did not load was invisible. The Modules pane decides what is
installed by enumerating FOLDERS, so a broken module counted as installed (and was
filtered out of "available to install"), reported no live version so no update was
ever offered, and displayed "installed -- restart to activate" forever. The only
exit was Uninstall, which deletes the module's settings and API keys -- a
destructive action to escape a state the user did not cause.

ModuleHost.LoadFrom already caught every failure and only logged it. It now records
them: all four early-return paths (no module DLL, no IModule type, MinHostVersion
refusal, and any exception) produce a ModuleLoadFailure carrying the folder id and a
reason. Surfaced through StartUp.ModuleFailures, the same route as LoadedModules.

The pane renders "failed to load -- <reason>" in red with a Reinstall button, which
routes to the existing install flow: that flow replaces only the install folder and
leaves the module's data directory alone, so a repair is non-destructive by
construction. The button is disabled until a catalog has been fetched, since there
must be something to reinstall from.

A MinHostVersion refusal is deliberately distinguished -- "needs a newer app" in
amber, with no Reinstall offered -- because the module is fine and reinstalling it
would achieve nothing. Only updating the app helps.

--module-host-selftest drives three genuinely broken folders through the real
loader (empty, a DLL implementing nothing, and a file that is not an assembly) and
asserts each is reported with a reason and none is mislabelled as needing a newer
app. Verified visually too: a real broken module renders in red with the disabled
Reinstall and its tooltip pointing at "Check for modules online".

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `137b999fe`

**Merge pull request #79 from bigfnj/feature/abi-theme-and-log**

```
Close two ABI gaps before re-freezing: IHost.IsDarkTheme and IHost.Log (1.4.7)
```

### 2026-08-17  `f2baa6d37`

**feat(abi)!: add IHost.IsDarkTheme and IHost.Log (1.4.7)**

```
Two gaps closed while the host is deliberately open, because after a re-freeze
anything the ABI cannot express is permanently impossible -- and both of these
would be felt by every module written against the new SDK.

IsDarkTheme. A module that owns a window (which the template now actively
encourages) could only read the OS theme, because the user's real choice is
light / dark / SYSTEM and only the host knows which is set. Reading the OS is
correct for "system" and wrong the moment someone pins the opposite; Pet Studio's
own theme file carries that caveat as a known defect. The host now answers with
the same resolution its own WPF windows use.

Log. IHost had no logging member at all, so a module's only way to report anything
was SayAll -- making the pet speak diagnostics at the user. Lines now go to the
app's diagnostic log tagged with the calling module's id. Deliberately not behind
a permission: it is strictly less capable than the storage a module already has,
and the alternative is modules inventing private log files nobody looks at.

Both are best-effort by contract and asserted against the REAL CompanionHost with no
StartUp behind it -- the host-not-running degradation path -- because a theme query
happens while building UI and a log call must never punish its caller. ModuleKit's
RecordingHost gains a settable IsDarkTheme and a LoggedLines list, so an author can
assert both themes without touching the machine's OS setting.

Carries the product bump to 1.4.7 in the same commit, as an ABI change must: a
Windows Installer major upgrade skips refreshing a Contracts.dll whose version did
not change.

Deliberately touches NO module source. An earlier revision updated a comment in
PetStudioTheme.cs, and CI correctly rejected it: the publish-freshness check is
commit-based (builds are not promised byte-identical), so any change under
modules/<Id>/ makes the published zip stale and demands a republish. Adopting
IsDarkTheme in Pet Studio is queued in BACKLOG instead, where it belongs -- it also
needs MinHostVersion 1.4.7, which would stop the module loading on 1.4.6.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `b9d9508fc`

**docs(handoff): record that 1.4.6 shipped and Pet Studio is published**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `949394c19`

**Merge pull request #78 from bigfnj/publish/petstudio-1.1.0**

```
Publish Pet Studio 1.1.0 to the module catalog
```

### 2026-08-17  `b96b962c8`

**fix(packaging): survive git writing notices to stderr, and publish the catalog**

```
Publishing Pet Studio hit a real bug in New-ModulePublish.ps1: git printed
"warning: ... CRLF will be replaced by LF" to stderr, and with
$ErrorActionPreference='Stop' PowerShell 5.1 turns any native stderr line into a
terminating NativeCommandError. The script aborted mid-sequence AFTER `git add`
had already succeeded -- the exact half-finished state it exists to prevent.

git calls now go through Invoke-Git, which makes errors non-terminating and judges
git the only reliable way, by its exit code, returning just the real stdout lines.

Also carries the regenerated catalog.json: 22 pets, 152 packs, 3 modules.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `e3de56bc5`

**chore(modules): publish petstudio 1.1.0**

### 2026-08-17  `6de8e91f1`

**Merge pull request #77 from bigfnj/feature/module-sdk**

```
A module SDK: ModuleKit, a dotnet new template, and a publish that cannot be done in the wrong order
```

### 2026-08-17  `90c577f35`

**Merge pull request #76 from bigfnj/feature/petstudio-authoring**

```
Pet Studio becomes an authoring tool, on a reopened host (1.4.6)
```

### 2026-08-17  `1d7bde05b`

**docs: tell the truth about the reopened host, and gate the template in CI**

```
handoff.md opened with a FREEZE CONTRACT asserting the host stops shipping and
that anything the ABI cannot express is permanently impossible. This session
contradicted that on purpose, so the block was actively misleading the next reader.
Rewritten as THE HOST CONTRACT: reopened at 1.4.6, treat the freeze as strong
guidance rather than a wall -- while keeping the rules that were right (product
bump in the same commit as an ABI change, AssemblyVersion never moves, never
declare an event you do not raise, previews stay invisible to modules, the
deliberate ABI exclusions). Current state rewritten for 1.4.6 + the SDK.

Also records the Pet Studio leak-soak METHOD, which runtime-resource-soak.ps1
cannot reach: two segments of 20 open/close cycles, passing on zero surviving
WeakReferences plus flat OS handles plus a second segment that barely grows. The
first segment legitimately sets a high watermark from the sprite sheet, and it was
that signal which found the per-keystroke re-decode.

BACKLOG: Pet Studio is published, not held back. Three new items, each with the
reason it is deferred rather than done: migrating Fortunes/AiBrain onto ModuleKit
(both are published, so it is a republish to every existing user), a permanent
soak for a module-owned window, and Phase B of the ecosystem.

RELEASE-CHECKLIST gains the module half, including the sequencing rule this
release taught: publish a module only after the host its MinHostVersion names has
shipped, or the catalog offers users a module their host correctly refuses.

build.yml gains Test-ModuleTemplate.ps1. run-gate.ps1 claims to mirror CI and had
drifted, so the template could have rotted on any branch that never ran the local
gate.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `f994111c5`

**feat(sdk): --module-selftest=<id>, so a new module is testable with no host edit**

```
The SDK's sharpest remaining edge: a scaffolded module could not be run at all
until its author added a flag to Program.cs. That is a poor first experience and a
step people forget, and it meant the template shipped a SelfTest nothing could
invoke.

Convention over registration. A module exposes
`public static bool SelfTest(out string detail)` -- the shape the template
scaffolds on ModuleKit's SelfTestProbe -- and --module-selftest=<id> finds it by
reflection, the same way the host reaches every other module member. The module is
first loaded through the REAL ModuleHost, so a pass also proves the loader accepts
it, the MinHostVersion gate let it through and Init ran; ModuleInfo is checked for
a name and a parseable Version, since the update check compares that.

Verified end to end: dotnet new -> build -> --module-selftest=zerowire passes,
with zero edits to the base. Negative paths too: an absent module SKIPs (which the
gate treats as failure), a module with no SelfTest FAILS rather than quietly
passing, and an unsafe id is refused.

The three pre-SDK modules keep their bespoke *ModuleSelfTest.cs, which assert
host-integration specifics this cannot know about. Wiring a new module into CI is
now two data-only edits (run-gate, build.yml) instead of three.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `5cf8be521`

**docs(sdk): the module authoring guide, and an ecosystem roadmap**

```
module-authoring.md is the guide the four existing modules never had: the two
assemblies and why the contract reference is Private="false" while ModuleKit's is
not, the IHost surface, permissions and MinHostVersion semantics, the rules the
host relies on (never throw, UI thread, clean up in Shutdown, startup-only
loading), what ModuleKit offers, contributing tray/pane UI, the self-test contract
and its three wiring points, the publish sequence and its two traps, and the
csproj gotchas that cost real debugging.

module-ecosystem-roadmap.md records the third-party design WITHOUT building it:
signing plus per-publisher consent on the VS Code model (attribution and informed
consent, not a sandbox we cannot honestly provide), a separate signed index versus
a curated links page, and NuGet-publishing Contracts/ModuleKit/template for
out-of-repo authors. Each with its open questions, and a suggested order whose
honest conclusion is that the cheap steps make an ecosystem possible while the
expensive ones should wait for evidence anyone wants it.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `20d3f90a3`

**feat(sdk): one command for the module publish sequence**

```
Publishing a module is five steps with two traps that have both shipped bugs:
catalog.json records the SHA-256 of the COMMITTED git blob (regenerate it before
committing the zip and it advertises a hash nobody can download), and
modules.json carries the version the in-app Update button compares against (lag
it and the update is never offered, lead it and it is offered forever).

New-ModulePublish.ps1 does build -> zip -> register -> commit -> catalog -> verify
in that order, reads Version and Permissions out of the module's own source so the
catalog cannot drift from the code, and REFUSES to regenerate the catalog while
the zip is uncommitted, printing the three commands to finish by hand. A first
publish must supply -Name/-Description, which no compiled DLL can provide and
which the Modules pane shows before download.

Verified against petstudio (built but unpublished): it read 1.1.0 / "Pets,
Storage", produced a 3-entry zip, added the manifest entry, and stopped at the
guardrail. Its JSON writer is hand-rolled because PowerShell 5.1's ConvertTo-Json
escapes an apostrophe as \u0027, which rewrote an untouched neighbouring
description; the diff is now the added entry alone.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `f9fdf500f`

**feat(sdk): add a dotnet new module template, gated against rot**

```
`dotnet new desktoppet-module --moduleId x --displayName "X"` scaffolds a module
that builds and passes its own self-test as generated. It encodes the four
load-bearing csproj facts a new module gets wrong (the modules\<id>\ output path,
the Private="false" contract reference, the flat output, and the dependency-copy
properties needed once a module references anything), and carries commented
flavour blocks for the three shapes the existing modules take: a window of its
own, a native NuGet dependency, and WinRT.

The generated module is a working example rather than an empty stub: a tray item,
a schema-declared settings pane with Load/Save round-tripping through
IModuleSettings, a poke reaction, and a SelfTest built on ModuleKit's
RecordingHost that asserts all of it.

A template is built by nothing, so it rots silently. Test-ModuleTemplate.ps1
scaffolds a throwaway module, checks every placeholder was substituted, builds it,
asserts ModuleKit shipped beside it while Contracts did NOT, then removes it and
uninstalls the template. Wired into tests\run-gate.ps1.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `1c9d450ac`

**refactor(petstudio): build on ModuleKit, proving the SDK on a real module**

```
Pet Studio is the safe module to migrate first: it is built and gated but not yet
published, so nothing reaches an existing user. Its hand-rolled tray-icon reader
becomes EmbeddedResources.LoadBytes and its hand-rolled temp-file swap becomes
AtomicFile.TryWriteAllText (which also gains the durable flush-through the local
copy lacked). Fortunes and AiBrain are published, so migrating them is a
republish and stays a separate, deliberate step.

This also proves the packaging shape: DesktopPet.ModuleKit.dll ships INSIDE
modules/petstudio/ (a normal, non-private reference) while DesktopPet.Contracts
stays absent and shared from the host, and the module still loads through the
real collectible AssemblyLoadContext -- --petstudio-selftest passes, icon and
validator-agreement assertions included.

The RuntimeGeometry source-link stays, contrary to the plan: the linked Xml.cs
needs DesktopGeometry from that same file, which is pet-engine geometry rather
than a general helper and does not belong in ModuleKit.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `fb71a5ff0`

**test(sdk): cover ModuleKit in the core regression harness**

```
Seven groups over the helpers a module depends on: durable writes (round-trip,
backup, no BOM, no leftover temp, relative path refused), resource lookup by
trailing name and its absent-path degradation, surrogate-pair boundaries,
JsonSettingsStore round-trip plus corrupt/BOM recovery, ModulePaths including the
no-Storage temp fallback, SelfTestProbe reporting, and RecordingHost recording +
responder arbitration. CoreTests already runs in tests\run-gate.ps1, so these
need no new wiring: 30 groups -> 37.

Writing them found a real weakness: ModulePaths.SafeSegment left ".." inside the
fallback folder name. Collapsed it, and the test now asserts the property that
actually matters -- the RESOLVED path stays directly under the temp root -- for
five hostile ids rather than pattern-matching the spelling.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `67c7d0df2`

**feat(sdk): add DesktopPet.ModuleKit, the module author's support library**

```
Four modules had each hand-copied the same helpers: CrossSessionLock + AtomicFile
were byte-for-byte identical in two, the "scan manifest resources by suffix"
loader appeared four times, and UnicodeTextProgress reached one module by copy
and another by source-linking a whole host file. Collect them once.

ModuleKit is deliberately NOT part of the ABI. DesktopPet.Contracts is the frozen
contract shared from the host's default load context; ModuleKit is ordinary
convenience code a module chooses to reference and that ships inside the module's
own folder, so each collectible ALC gets its own copy and two modules may use
different versions.

Contents: AtomicFile, CrossSessionLock, EmbeddedResources, UnicodeTextProgress,
ModulePaths (host-storage-backed, temp fallback), JsonSettingsStore<T> (durable
JSON for state a settings pane cannot express), SelfTestProbe, and a Testing
namespace with the RecordingHost / DenyingCompanionManager / fake settings + storage
that every module self-test was reinventing.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `5294a22eb`

**chore(release): 1.4.6**

```
Bump the product to 1.4.6 (from the 1.4.5 dev line), the first public host to
ship ICompanionManager.CompanionsDirectory. Pet Studio declares MinHostVersion 1.4.6 to
match, and the ABI doc comment follows.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `af6dff24e`

**perf(petstudio): cache the decoded sprite across re-analyze**

```
A leak soak (open/close the window 20x, twice) showed no rooted-window leak and
flat handles, but a large private-byte high-water mark: the sheep's sprite sheet
is the window's biggest allocation and Analyze re-decoded it every time. The
debounced re-analyze fires every ~750ms while editing, so typing re-decoded a
~15 MB sheet on every keystroke-settle for an image the edit never touched.

Skip the decode when a cheap fingerprint of the image (tiles, transparency,
base64 length + head/tail) is unchanged. Soak after: segment-2 growth ~+1 MB
(plateaus/reclaimable), zero windows alive.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `4ea27c577`

**refactor(petstudio): address code-review findings**

```
- Drop the vertical-only dark ScrollBar override. This window has horizontal
  scrollbars (the editor's long base64 line, the frame strip) that the
  host-copied vertical-only template broke in dark mode; default scrollbars work
  in both orientations.
- PetSprite.ApplyColorKey now reads src pixels directly instead of copying them
  through a throwaway WriteableBitmap (this runs on every debounced re-analyze).
- Move the blank-frame pixel scan into a cached PetSprite.IsBlank, so repeated
  selections don't re-scan the same tiles.
- WriteAtomic deletes its temp file if the swap fails, instead of leaving a
  stale .tmp in the pet folder.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `2cde798af`

**fix(pets): wire the orphaned king_slamB animations in the seven sheep**

```
The shared 268-animation sheep set has king-mode animations no transition
reaches. Two are a genuine missing edge: king_slamB_down (#183) and
king_slamB_up (#185) -- the up/down walks and jumps never slam onto the
opposite surface, unlike base/top. Add the six border transitions that mirror
the already-wired base/top directions (walk_up/jump_up/jumpB_up -> 183,
walk_down/jump_down/jumpB_down -> 185), so both now play.

The two jump flips (#187/#188) are left orphaned on purpose: base/up jumps
already rotate directly, so those flips were bypassed by design.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-17  `d578579ac`

**feat(petstudio)!: authoring studio + ICompanionManager.CompanionsDirectory (1.4.5)**

```
Grow the pet validator into a 3-column authoring surface: an editable XML pane
(debounced re-analyze + atomic Save, feeding Preview/Install), a colour-coded
reachability map with clickable legend filters, and a detail panel that renders
the selected animation's sprite frames (with playback) plus its outgoing
transitions. A blank/transparent frame and an orphaned (unreachable-but-built)
animation now explain themselves in the detail panel.

Opening the file dialog in the user's pet library needs a path the ABI did not
expose, so add ICompanionManager.CompanionsDirectory (additive; implemented in
CompanionManagerBridge + DenyingCompanionManager). Per the freeze contract this bumps the
product version to 1.4.5 in the same change so DesktopPet.Contracts refreshes on
a major upgrade; the module now declares MinHostVersion 1.4.5.

New module files: PetSprite (base64 sheet decode + transparency colour-key),
PetStudioTheme (host-matching light/dark), PetStudioPaths (Open-dir policy). A
tray icon ships as an embedded PNG. --petstudio-selftest gains frame-index-bounds,
map-dead-set == host reachability, open-dir-policy and tray-icon assertions.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-15  `f11871e18`

**docs(handoff): update current state to v1.4.4 (freeze candidate shipped)**

```
Reframes the OCR/update work as shipped (v1.4.2), records the 1.4.4 release =
the pre-freeze sweep (PR #74) + Pet Studio (PR #75), and notes the box now runs
the hash-verified published 1.4.4 MSI with Pet Studio copied in by hand. The
freeze contract block at the top is unchanged and still governs.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `8e182d58a`

**chore: 1.4.4**

```
Release cut of the pre-freeze sweep (PR #74) plus the Pet Studio module (PR #75).

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `c9e9ec7d5`

**Merge pull request #75 from bigfnj/feature/petstudio-module**

```
Pet Studio: the pet validator/preview tool, rebuilt as a module
```

### 2026-08-14  `0546dbaa7`

**Merge pull request #74 from bigfnj/freeze/host-1.5.0**

```
Pre-freeze sweep: ABI closed out, dead code gone, leak gate restored (1.4.3)
```

### 2026-08-14  `f677fca49`

**feat(petstudio): a pet validator and preview studio, as a module**

```
The replacement for the retired Tools\PetTester, rebuilt as a module rather than
a second app. Open a pet's animations.xml, see what the host would reject and
which animations can never play, watch it run on the real desktop, then install
it. Built and CI-gated but NOT published -- see below.

Two things make this worth being a module rather than a tool.

It reaches the pet engine through the ABI: ICompanionManager.SpawnPreview puts a
transient pet on the user's actual desktop, so an author sees the real thing
under the real physics, and it is never saved, never joins their pet mix, and
never survives closing the window.

And it SOURCE-LINKS the host's parser, validator and AnimationReachability
instead of carrying a copy. Normally that is how you get skew; here it is exactly
backwards, because the host is frozen -- those files stop moving -- and it means
the studio's verdict cannot drift from what the host will actually run. That is
the whole justification, so --petstudio-selftest tests it rather than asserting
it: the module's analyzer and the host's CompanionXmlValidator must reach the same
verdict on the bundled pet, a DTD-bearing pet, junk, and empty input. A
disagreement means the source-link has rotted, which is precisely how PetTester
died (it link-compiled a file that later moved into another module, and nothing
noticed for a week).

Analysis lives in PetAnalyzer, deliberately UI-free, with the window as layout
plus wiring. That separation is the other lesson from PetTester, whose graph walk
lived inside a WinForms form and so could be neither tested nor reused.

This is also the first module to own a WINDOW. Modules have been data + delegates
with the host rendering everything, which is right for settings panes; an
authoring canvas is not expressible as a schema, and nothing structural prevents
it -- a module is an ordinary assembly in-process, and AiBrain already pulls in
WinForms.

NOT PUBLISHED on purpose: it declares MinHostVersion 1.4.3, so listing it in the
catalog before that host ships would offer users a module their host would
correctly refuse. The publish steps are recorded in BACKLOG.md.

Wired into build.ps1, the CI flag list, and tests/run-gate.ps1 (now 12 self-tests).
CS0649 is suppressed for this project only, with the reason: Animations.SoundSink
and PetTypeId are assigned by the host and merely read here.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `8fc1c36d9`

**docs: record the live smoke script, and the regression pass results**

```
Adds the manual smoke script to the release checklist as a numbered pre-tag step,
because every other gate in this repo is a self-test: they prove invariants, not
that the app still works. That gap is what let the S6p2 UI get built through four
phases before anyone disliked it, let a stale install get debugged as if it were
current, and let the OCR mojibake ship.

Row 10 is the one that fails silently: after an ABI change, the INSTALLED
DesktopPet.Contracts.dll must carry the new product version. Windows Installer
skips refreshing a file whose version did not change.

Regression pass results for this build (1.4.3):

- Published fortunes/aibrain 1.1.1 payloads -- compiled against the pre-freeze
  ABI, before CompanionIdle/AnimationStarted were removed and ICompanionManager was added --
  were extracted over a clean modules folder beside the new host. All four module
  self-tests pass with no skips. So the ABI change is binary-compatible with what
  is already published, and no republish is needed.
- MSI upgrade 1.4.2 -> 1.4.3 on the real box: exe AND Contracts.dll both refresh
  to 1.4.3.0, modules/ and %LOCALAPPDATA%\DesktopPet\modules\ both survive, AI
  Brain's settings are byte-identical, one clean registration. This is the first
  time that refresh has been verified with an actual ABI change riding on it.
- The real 1.18 MB settings.json (schema 2, 23 top-level keys) loaded into an
  isolated data root: app reaches a responsive message loop, schema unchanged, no
  keys lost, file byte-identical afterwards -- no gratuitous rewrite.
- Leak soak on the post-sweep build: handles +7, GDI -6, USER -5, private bytes
  +16.1 MB, against a pre-sweep baseline of +5 / -6 / -6 / +13.6 MB. Nothing in
  this sweep leaks.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `84e7a1340`

**docs: write the freeze contract, and fix five drifted claims**

```
handoff.md gains THE FREEZE CONTRACT at the top -- the things a future session
must not re-derive or re-litigate once the host stops shipping: the frozen
version is the permanent MinHostVersion floor (and what that means for module
authors), every ABI event is raised by the host, previews are invisible to
modules and why that rests entirely on DeriveOnScreenMix, the deliberate ABI
exclusions with their reasoning, the rule that an ABI change requires a product
bump in the same commit, and which gate catches what. The leak baseline is
recorded there so a later run has something to compare against.

Drift fixed:
- --aibrain-selftest's summary still described the S4a "dormant scaffold" and
  claimed Init wires NOTHING, while the test asserts the exact opposite (live
  subscriptions, a drop responder, two tray items, a pane) plus the OCR encoding
  pin added this week.
- Readme still said "Two are published today" and never mentioned that modules
  update in place or that the app checks monthly -- the two things a user most
  needs to know now.
- AboutWindow's shipped prose still listed an "audio" module, retired in B4 when
  the base took audio back.
- Directory.Build.props described a src/legacy quarantine that no longer exists,
  and Tools/ which no longer exists either.
- BACKLOG still said modules ship at 1.1.0.

Also files the one accepted-not-fixed defect: a module that fails to load is
invisible in the Modules pane (counted as installed, so hidden from "available";
no live version, so no update offered), with the post-freeze fix sketched.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `b405494b7`

**refactor: salvage the reachability invariants into the host, retire Tools/**

```
Tools/ leaves the tree: 105 files, 5.8 MB, and the last .NET Framework island in
a net10 repo. Neither tool was reachable any more.

PetEditor is upstream's 2019 pet-authoring IDE, never modified in this fork, and
already disowned in-tree by its own PetEditor.UNSUPPORTED-LEGACY.md ("not built,
tested, packaged, or supported"). It depends on legacy packages.config and
Microsoft.Toolkit.Forms.UI.Controls.WebView, the retired wrapper around legacy
Edge, so it is not portable forward as-is. It stays in git history and upstream.

PetTester could not build at all: its csproj link-compiles
src/dotNet/Ai/AiExecutablePolicy.cs, which moved into the AiBrain module during
S4. It broke silently around 2026-08-06 and nothing noticed, because neither
build.ps1 nor CI ever touched Tools/. It also still targeted net48 against a
net10 product and referenced NAudio for a TSound that stopped needing it.

What was worth keeping is kept. PetTester's two genuinely valuable assertions
were about the ENGINE, not the tool, and they were the only checks anywhere for
which animations in a pet XML can never play. The walk they exercised lived
inside the tool's WinForms form, fused to the checkboxes and text box it painted
into, so it is now src/dotNet/AnimationReachability.cs: a pure function over the
parsed XML, and --security-selftest asserts its two subtle rules.

  1. a <child> edge does not make its target reachable until its PARENT is
  2. a probability-0 transition is not an edge at all

Both rules HIDE a dead animation when broken, which is the worst failure mode for
an authoring check -- it silently tells the author their pet is fine. So both are
negative-tested: making the walk naive (seeding child targets as roots, following
zero-probability edges) fails exactly those two assertions while the
bundled-pet baseline still passes, proving they test the rules and not the
fixtures.

The file is also deliberately shaped to be source-linked by the future PetStudio
module, so the tool's report can come back without a second implementation.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `96bd4ae06`

**fix(modules): unpack payloads off the UI thread, and gate module version parity**

```
Two fixes that both protect the module-delivery path the frozen host depends on.

Zip extraction was synchronous on the WPF UI thread, in both the install and the
update path. fortunes.zip is ~31 MB, so the settings window visibly froze while
it unpacked. net10 has ZipFile.ExtractToDirectoryAsync, so this is the same
extraction implementation -- path-escape rejection unchanged -- simply awaited,
and the ZipArchive wrapper disappears (no await-using needed, which matters at
LangVersion 7.3). Nothing in the build caught this: there is no .editorconfig and
CA1849 is not surfaced at warning severity, which is exactly how it shipped. So
runtime-hardening-selftest.ps1 now asserts the file uses the async API and no
synchronous ExtractToDirectory( survives.

Version parity is now gated. Nothing verified that a module's ModuleInfo.Version,
modules-dist/modules.json and catalog.json agree, and since the in-app Update
button compares the module's LIVE version against the catalog's, a mismatch is
not cosmetic: publish a catalog version below the shipped one and no update is
ever offered, publish one above and the update is offered forever, surviving
every install. Test-ModulePublishFreshness.ps1 already auto-discovers module ids
and already runs in CI, so the check lives there.

The regex is anchored to the start of the line on purpose -- an unanchored
'Version\s*=' also matches MinHostVersion, which sits two lines below it in every
module -- and anything other than exactly one match is itself a failure, so the
check cannot silently stop checking.

Negative-tested: setting modules.json's fortunes version to 9.9.9 fails with
"version mismatch -- source 1.1.1, modules.json 9.9.9, catalog.json 1.1.1" and
names the fix; reverting returns it to green.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `387547c30`

**refactor: delete PackCollections, a dead generic helper, and a test-only type**

```
Three deletions that each turned out to be more than a tidy-up.

PackCollections (9c). The audit first flagged this as a latent NRE: classic
double-checked locking over two non-volatile statics, where the fast path guards
on _collections but CollectionName dereferences _sourceToName, which is assigned
second. The real finding was better -- CollectionName has ZERO callers repo-wide,
because the Fortunes module carries its own embedded collections.json and its own
pack-to-collection map. Deleting the file removes the race with it, which is
strictly safer than the lock I would otherwise have written. packs/collections.json
stays: the catalog generator and the module both consume it.

GenerationOwnedValue<T> (9d), 234 lines whose only reference was its own
typeof(...).Name, plus RemainingShutdownBudget, whose only caller lived inside it.
Deliberately stopped short of GenerationAwareIdleSchedule, which sits immediately
below in the same file, is easy to mistake for the same helper, and is very much
alive -- --security-selftest constructs it. (The audit initially conflated the two;
they merely share a prefix.)

RecoverableErrorState<TDomain> (9e), and its CoreTests group, together in one
commit because RuntimeGeometry.cs compiles into both the app and the test
harness. Nothing in the shipped product used it: AudioOutput superseded it by
simply swallowing device errors behind an _unavailable latch. So a regression
group was testing code that shipped to nobody -- the same orphaning pattern the
handoff already warns about, one layer deeper.

The group count is now computed rather than hardcoded, because the literal was
itself drifting: it claimed 26 while 31 groups ran. It now reports 30.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `6a32b543c`

**refactor: retire the fortune-era leftovers on the Options controller seam**

```
SmartFortunesStatus and RebuildSmartFortunes were declared on ICompanionRuntime and
implemented in StartUp, and neither had a caller anywhere. Both were already
gutted: the status returned the fixed string "Fortunes are provided by the
Fortunes module", and the "rebuild" no longer touched fortunes at all -- it just
resynced the drop timer. They are leftovers from before the Fortunes module
owned fortunes.

Removing them takes their orphans with it, which is why this is one commit:

- OpResult<T> in full. Ok2 and Fail were its only members and neither is used;
  the non-generic OpResult stays, since UsePet and AddPet return it.
- CompanionsController.RestoreDefaultPet and DownloadPet, both callerless.
- ICatalogService, which existed solely for DownloadPet.
- the _catalog field and the ctor's second parameter, updated at the one call
  site (CompanionsPaneControl, which was already passing null).

ApplyRandomDrop survives with its two real callers.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `2a4017aed`

**refactor: delete nine zero-caller members and two unreachable usings**

```
First of the dead-code commits. Each was verified unreferenced across src/,
modules/, tests/, Tools/, packaging/ and the workflows, not merely "looks unused":

- StartUp.GetAnimations()
- WindowTheme.ThemeControlTree (its private ThemeTree survives: Apply calls it)
- LocalData.GetScaleFactor, SetImages, IsFirstBoot
- the whole AnimationXML class, whose only member was ParseXML. Every project
  that compiles that file has its own superseding deserializer, and the host's is
  CompanionXmlValidator.TryParse; ParseXML also had the swallow-and-return-null shape
  the codebase moved away from. Its now-unneeded usings went with it.
- the two `#if !PORTABLE` usings of Windows.ApplicationModel.*, which could not
  have compiled had the branch ever been taken: the project references no WinRT
  projection at all. PORTABLE itself stays load-bearing -- Program.cs wraps live
  code in `#if PORTABLE`.

Deliberately NOT touched: the ScaleLevel SETTING. Only its accessor was dead.
GetEffectivePetScaleFactor replaced it, per-pet size overrides read the field
directly, and six CoreTests assertions depend on the setting.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `26418e86b`

**feat(abi): let a module fetch pets from the catalog, not just install its own**

```
CatalogKinds gained "pet" in the previous commit but nothing served it, so a
module could install a pet it authored and never fetch one of the catalog's 22
published pets. The kind switch is frozen along with the host, so an unserved
kind would have thrown "Unknown catalog kind" forever.

FetchCatalogItemsAsync now maps catalog.Pets (author as the Group, since pets
have no collection), and DownloadCatalogItemAsync resolves a pet id through the
same RemoteCatalogClient.DownloadVerifiedAsync path the host's own Pets gallery
uses -- URL re-validation plus the recorded SHA-256 -- bounded by
CompanionCatalog.MaximumPetXmlBytes rather than the fortune-pack limit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `3ce0d3af2`

**feat(abi)!: add ICompanionManager, pet previews and ICompanion.TypeId (1.4.3)**

```
The pet verbs a module cannot be given after the host stops shipping. A previous
stream built and reverted a 15-member ICompanionManager; this is the 10 members that
earn their place, plus the one it never had -- spawning a preview pet from an
arbitrary XML string, which is what makes a pet-authoring module possible at all.

  inspect  InstalledTypes, OnScreenMix, MaxCompanions, IsAtMax
  place    SpawnOne, RemoveOne
  author   ValidateXml, SpawnPreview, InstallType, UninstallType

Reached through one new IHost member, GetCompanionManager(moduleId), so eight pet verbs
do not appear on the surface every trivial module sees. ICompanion gains TypeId, the
only join between the event stream (bare pet handles) and these type-keyed verbs.
ModulePermissions gains Pets; a module that did not declare it gets a refusing
service rather than an exception or a null, matching how RegisterHotkey degrades
to a no-op handle.

Deliberately excluded, so a later session does not reopen it: there is no "use
this pet" verb. That operation writes the pet's XML into settings.json, closes
every pet and resets the persisted mix, and the host's own Pets pane and tray
already own it -- a frozen host keeping its most destructive verb to itself is a
feature. Per-type size, sound and voice are excluded too: those are user
preferences the Pets pane owns, and a module writing them would fight that pane
with no arbitration, for no known consumer.

The install path is the proven one from the reverted stream, reused rather than
rewritten: safe-id check, size bound, CompanionXmlValidator before anything touches
disk, atomic write, and SafeLibraryDir path containment. Changed only to take a
string rather than bytes (an authoring module holds text) and to strip a leading
BOM so an authored string and a decoded download behave identically.

ProductVersion 1.4.2 -> 1.4.3, which is mandatory rather than cosmetic:
DesktopPet.Contracts stamps its FileVersion from it, and a Windows Installer
major upgrade SKIPS refreshing a file whose version did not change, so an ABI
change without the bump ships a stale Contracts.dll that cannot resolve the new
types. That exact failure is what 9009133 fixed. The reason is now a comment in
ProductVersion.props.

As predicted by LangVersion 7.3 having no default interface members, this broke
all seven implementations at compile time -- 4 fake IHosts and 3 FakePets -- which
is precisely why it had to land before the freeze rather than after.

TestModule gains "Preview a pet from XML" / "Remove the preview pet" tray items.
It is never published, so this is a developer's way to drive the preview path
through a real AssemblyLoadContext against the real host, and it is the
compile-time proof the ABI suffices to write an authoring module against.

Tests: 7 new --module-host-selftest assertions on the permission gate, run
against the real CompanionHost with no StartUp (which doubles as the host-not-running
degradation path): refuses to validate/preview/install/uninstall/spawn/remove
with reasons, returns empty enumerations rather than throwing, and still reports
the real pet cap so a module can size its UI.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `6be7d86b6`

**feat(pets): host-side transient preview pets (internal; nothing calls them yet)**

```
The host verbs a pet-authoring module needs to show the user their draft pet
running on the real desktop. Deliberately unreachable in this commit: nothing
calls SpawnPreviewPet, so behavior is unchanged and the ABI bridge that follows
is a pure translation layer with the risky half already proven.

SpawnPreviewPet(xml, out error) stages an arbitrary animations.xml through the
SAME path an installed pet takes (TryStageRuntime -> CompanionXmlValidator), so a
preview is not a hole in the pet-XML defences, then registers it under a
synthetic transient id and spawns it.

It is emphatically NOT LoadNewXMLFromString. That verb means "use this pet": it
writes the XML into settings.json, kills every pet on screen, wipes the type
registry and re-persists the mix. Previewing with it would permanently replace
the user's pet with somebody's draft.

The four ways a preview could have leaked into the user's state, and what stops
each:

- settings.json / next launch: previews carry a transient registry entry, and
  DeriveOnScreenMix skips those, so PersistMix cannot see them. The id is
  "preview:<guid>" -- unique, so it can never displace an installed type, and
  containing ':' so IsAcceptablePetId would reject it even if one leaked.
- KillSheep: removing a preview no longer calls PersistMix at all. The content
  would have been harmless (the mix already omits transients), but a module's
  preview should not cause a settings.json WRITE.
- CompanionSpawned: not raised for previews. Modules react to it with user-visible
  behavior -- Fortunes speaks a welcome, the AI brain resets its tracked pet --
  and an author re-previewing twenty times should not fire twenty welcomes. The
  previewing module already holds the handle it needs.
- CompanionPoked / CompanionLanded: both now resolve their subject through
  FirstPersistentPet() instead of sheeps[0], which could be a preview. With only
  previews on screen no module hears a poke, which is the correct reading of
  "none of the user's pets was poked".

Also caps previews at 4 so a module that forgets to remove them cannot starve
the 16 real slots, adds RemovePetInstance (the preview path removes a specific
instance; the tray removes BY TYPE and cannot target one), and adds
FormCompanion.PetTypeId to back the ABI's ICompanion.TypeId in the next phase.

Gate green via tests/run-gate.ps1, which now captures self-test child output and
echoes it only on failure -- those processes write straight to the console, so a
run was hundreds of lines of PASS with the summary buried at the bottom.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `c97f4945b`

**feat(modules): enforce MinHostVersion at load time, and add a gate runner**

```
Every module has declared a minimum host version since the ABI existed and the
host has never read it. That was harmless while the host kept shipping. It stops
being harmless at the freeze, because this is the only mechanism by which a
module built against a newer contract can be turned away cleanly -- and it is
not addable afterwards. Without it such a module loads and then dies at its first
call to a member that does not exist, surfacing as a MissingMethodException from
inside someone else's Init.

New ModuleHostRequirement.IsSatisfied, hooked into ModuleHost.LoadFrom after
construction and strictly BEFORE Init, so a module the host cannot satisfy never
touches it. Refusal is a log line plus alc.Unload() plus continue, never a throw,
so one too-new module cannot stop the others loading. The log reads
"module skipped: aibrain needs host 1.0.0 or newer (this host is 0.0.1)".

Permissive by construction: the gate can refuse for exactly one reason, that the
module asked for a newer host than this one. A missing requirement loads (every
module shipped so far predates the gate), an unparseable requirement loads with a
note, and an unparseable HOST version never refuses anything -- refusing
everything because the host could not describe itself would be self-inflicted.
Semver tags are trimmed before comparing, so 1.6.0-beta still compares as 1.6.0
rather than falling through the permissive door. Compared against
host.HostVersion through the interface (so a test can inject one), never against
the Contracts AssemblyVersion, which is pinned at 1.0.0.0 as a binding identity
and would refuse every module that ever declares more than 1.0.0.

Writing the test found a real wrinkle in my own code: the first version treated
"dev" as "no requirement declared", so an author's typo'd MinHostVersion would
have been silently ignored forever. Blank and malformed are now distinct -- the
first is silent, the second loads WITH a note.

Tests: 14 assertions in --module-host-selftest. Nine cover the rule table
directly (older/equal/shorter/newer/one-patch-newer/semver-tagged/absent/
unparseable-requirement/unparseable-host). Five cover the real wiring by lying
about the HOST's version instead of shipping a purpose-built too-new module: at
0.0.1 nothing loads AND no tray item or pane is contributed AND no subscription
happened, which is what proves the refusal precedes Init.

Also adds tests/run-gate.ps1, one command for the whole local gate. Its reason
for existing is a trap I hit twice today: the module self-tests skip-PASS when
their folder is absent (correct for a payload with no dev modules), so a build
that failed to produce modules/ is indistinguishable from a clean run. Once I
caused that by deleting the folder, and once by piping build.ps1 through
Select-Object -First, which short-circuits the upstream pipeline and terminates
the build before the module builds run. The runner asserts the modules exist and
fails on any SKIP marker. The four fake IHosts now also report a parseable
sentinel version so the new gate stays quiet in their logs.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `89881bf74`

**fix(pets): stop a re-staged pet type from evicting the entry that replaced it**

```
CompanionTypeRegistry.Add overwrote _byId[id] without considering the entry it
displaced, and DisposeEntry removed the mapping by KEY. Together those made
re-staging an already-registered pet type actively destructive rather than merely
leaky: once id "x" had been staged twice, the OLD entry reaching zero references
removed the key that now pointed at the NEW entry, so a live pet's type vanished
from the registry, the next spawn of that type staged a third duplicate copy of
the same Xml/Animations pair, and the displaced pair leaked when nothing owned it.

Three changes, all in the registry:

- Add() now disposes a displaced pair only when nothing references it (staged but
  never spawned, where skipping the dispose is a straight leak). If pets are still
  using it, it is deliberately left alive and owned by them -- FormCompanion borrows its
  Xml/Animations and never disposes them, so freeing the pair here would pull the
  sprites out from under a live pet.
- DisposeEntry() removes by IDENTITY, so an old entry reaching zero can never
  evict whichever entry currently owns that id.
- Entry gains IsTransient, the marker the preview work in the next phases needs.

Also extracted StartUp.OnScreenMix's body into a static DeriveOnScreenMix over
one registry entry per live pet, and made it skip transient entries. That single
omission is the entire safety story for preview pets, because this one list is
read by BOTH PersistMix (so a preview can never reach settings.json, never
survive a restart, and never corrupt the startup spawn plan) and the tray's
"Remove a pet" submenu (where a preview row would mislabel itself as the active
pet AND remove a real pet when clicked). Static so the rule is directly testable.

Tests: 11 new assertions in --pettyperegistry-selftest covering displacement with
and without live references, the cross-eviction regression, and the mix rule
including "a screen holding only previews yields an empty mix". Negative-tested
by reintroducing the key-based removal, which fails exactly one assertion
("...without evicting the entry that now owns the id") and nothing else, so the
test provably catches the bug it was written for.

CoreTests' pet-mix validation now also feeds in a synthetic "preview:abc123" id
and asserts no ':' id survives, pinning the second line of defence:
IsAcceptablePetId rejects ':', so even a leak upstream cannot leave a dead id in
the persisted mix, where it would silently cost the user a pet at next launch.

Gate: 0 warnings, CoreTests 26 groups, all 11 self-test flags, source invariants.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `7818664d9`

**feat(abi)!: remove CompanionIdle and AnimationStarted before the contract freezes**

```
Both events were declared in the ABI, bridged in CompanionHost, and never raised by
the host. Nothing subscribes to either: the only references outside the contract
were the four fake IHosts' TouchEvents() bodies, which exist purely to dodge
CS0067 and are themselves never called. In a contract that is about to stop
changing, a declared-but-silent event is a permanent trap -- a module author
subscribes, sees nothing, and there is no host release left in which to fix it.

Raising them instead was the alternative, and it cost more than it was worth.

The host has no idle policy of any kind. GenerationAwareIdleSchedule is a
generation gate with no cadence and no timestamps, and the only real idle
predicate in the product -- a screen-change delta -- lives in the AI-brain
module, which rolls its own timer precisely because CompanionIdle never fired. Raising
CompanionIdle honestly would mean inventing a cadence, new settings keys, Preferences
UI, and a broadcast that races the existing RegisterDropResponder chain for the
speech bubble: new user-visible behavior, added at the moment of freezing.

AnimationStarted is worse. AnimationInfo.AnimationId is an index into one pet's
own XML animation list; there is no name field and no verb to enumerate a pet's
animations, so a module cannot map the int to anything. Making it useful meant
ADDING ABI, which is the opposite of the goal. Its SoundData/SoundLoop payload
had already been stranded: the sound path runs Animations.SoundSink straight into
the base's own AudioOutput, which is what left the event unraised when the Sound
module was retired.

Binary compatibility with already-shipped modules is not assumed here, it is
tested. The published fortunes 1.1.1 and aibrain 1.1.1 payloads from
modules-dist -- compiled against the ABI that still HAD these events -- were
extracted over a clean modules folder beside the new host and both loaded and
passed their full self-test suites (--module-host-selftest, --fortunes-selftest,
--aibrain-selftest), including the OCR encoding pins. Removing an interface event
no module ever referenced is invisible at the IL level, so DesktopPet.Contracts
stays at AssemblyVersion 1.0.0.0 and neither module needs a republish.

Also documents the rule going forward, on the events block itself: every event
here is raised by the host, and a new one wires its raise in the same change.

Gate: 0 warnings, CoreTests, all 11 self-test flags (verified actually running
rather than skip-passing), source invariants.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `34c469a28`

**test: restore the leak soak, the only gate that can catch a leak**

```
The in-process churn harness (--resource-churn-selftest, RuntimeResourceChurn,
RunResourceChurnPetCycle, PaintSpeechForResourceChurn) has been shipping in the
product the whole time; only its driver was gone. 5d36ab3 removed
tests/runtime-resource-soak.ps1 as "referenced by nothing" three hours after
10b059b stopped CI from referencing it, which left the app's sole leak gate with
no way to run while handoff.md still cited it as a release gate.

Recovered from 5d36ab3^ with three deltas.

The counter check is now DISCOVERED rather than hardcoded, which is what killed
it: the old list demanded optionsCycles, optionsCancellationCycles, aboutCycles
and helpCycles, all of which left the harness when the WinForms FormOptions,
AboutBox and FormHelp were retired for WPF (742b0ff, 343336d) -- so a verbatim
restore would fail on a perfectly healthy build. It now requires
speechAndPetCycles and trayAndMenuCycles to exist, then asserts every *Cycles
field equals the cycle count, so new counters are picked up automatically. The
loop's own control fields (cycles, targetCycles) are excluded because the harness
may legitimately run more cycles than requested -- and it does: the first
restored run reported cycles=100 against targetCycles=15, which an equality check
would have failed for entirely the wrong reason.

Dropped the seeded ai-settings.json. It set SmartFortunes / AiBrainEnabled /
IdleCommentaryEnabled to steer the run, and every one of those keys moved into a
module (S3d, S4b); the base has not read them since. A fresh isolated data root
is the correct starting state. Kept the launch + WaitForInputIdle + kill leg as a
real "reaches a responsive message loop" smoke test, reported as
ResponsiveStartup.

Kept exactly as it was: GetGuiResources sampling from outside the process,
the growth bounds (handles 16, GDI 16, USER 16, private bytes 64 MB), the
counters-unavailable guard, the duration floors, and the %TEMP% scratch
containment that both sides enforce.

FREEZE BASELINE, from the first green run on this build (33 samples, 31.7s,
100 churn cycles):
  handles      +5      (bound 16)
  GDI objects  -6      (bound 16)
  USER objects -6      (bound 16)
  private bytes +13.6 MB (bound 64 MB)
GDI and USER going down is caches settling, not a measurement error.

Wired in as a local pre-tag step in docs/RELEASE-CHECKLIST.md and as an opt-in
workflow_dispatch "resource-soak" job in build.yml. Deliberately not blocking on
every PR: it needs a real window station and OS growth thresholds are the
flakiest assertion available on a hosted runner.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `35e5f55d9`

**build: make a cold clone reproducible (exact SDK pin, platform-free DefineConstants)**

```
Two things stood between this repo and a build that reproduces itself, both of
which matter more once a FINAL artifact is the output.

A bare `dotnet build` did not work. DefineConstants lived in PropertyGroups
conditioned on '$(Configuration)|$(Platform)', so without an explicit
-p:Platform=x64 the whole group dropped, PORTABLE went undefined, the
#if PORTABLE block in Program.cs fell out, and the build failed with ~20 CS
errors that pointed nowhere near the cause. The platform half of that condition
carried no information -- Platforms, PlatformTarget and RuntimeIdentifier are
pinned to x64 unconditionally -- so it is now conditioned on Configuration
alone, in the product and the CoreTests project alike.

global.json claimed to pin the SDK and did not: version 10.0.100 with
rollForward "latestMinor" floats to whatever 10.0.x a machine happens to have
(10.0.302 here). Now pinned to 10.0.302 with rollForward "disable", so the
released binary comes from a known compiler.

That pin makes the runner SDK load-bearing, so it lands atomically with CI:
build.yml and release.yml move 10.0.201 -> 10.0.302, and publish-release.yml
gains the setup-dotnet step it never had (it relied on the runner default, which
a hard pin would have broken on the first tag push). release.yml's step name
also loses its stale rationale: 10.0.201 was pinned because 10.0.302 misreported
the legacy net48 project in `dotnet list package`, and neither that project nor
that gate exists any more.

Verified: `dotnet msbuild -getProperty:DefineConstants` returns TRACE;PORTABLE
for Release and TRACE;DEBUG;PORTABLE for Debug; a cold `dotnet build -c Release`
with no -p:Platform succeeds at 0 warnings; full gate green (CoreTests, 11
self-test flags, source invariants, module publish freshness).

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `f5a3f9a42`

**Merge pull request #73 from bigfnj/fix/ocr-utf8-and-module-updates**

```
fix(aibrain): OCR read as ANSI, not UTF-8 ("asÂ®") + a real module update path (1.4.2)
```

### 2026-08-14  `10162b1cf`

**feat(modules): check for module updates once a month, on its own**

```
An Update button nobody reveals by clicking "Check for modules online" is
barely better than no update path, so the check now runs itself.

Deliberately NOT a 1st-of-the-month alarm. A desktop pet is not reliably
running on any given date, and that design silently skips every month the app
happened to be off that day. ModuleUpdateSchedule instead records the month a
check last SUCCEEDED and becomes due as soon as the calendar month moves on: a
pet started on the 5th having missed the 1st still checks, one left running for
a year checks twelve times. The month is stamped only after a successful fetch,
so being offline costs a retry rather than a month. A fresh install is seeded
without checking (first check lands next month, since someone who just
installed is minutes from picking their modules by hand), and with nothing
installed it stamps and skips the network entirely.

The stamp is a yyyy-MM marker file beside the other startup markers in the data
root, not a settings field: machine state with no user meaning should not drag
the settings schema, its migrations and its merges along behind it.

StartUp evaluates two minutes after launch, then every six hours. That cadence
exists to notice a month rolling over, not as a polling rate. A hit raises a
tray notification that opens Settings, Modules when clicked; nothing downloads
or installs itself, so consent stays where S6 put it.

The version rule moved into a shared ModuleUpdateScan used by both the pane and
the check. A badge and a notification that disagreed about what counts as an
update would be worse than either on its own.

This is the only unprompted network request in the app, so it is switchable and
documented: a Preferences toggle (default on; absent in a pre-1.4.2 doc reads
as on, the nullable-bool trap that once left SuppressRepeats silently off), and
a PRIVACY.md paragraph stating what it sends (nothing) and how to turn it off.

Tests: 13 new --module-host-selftest assertions over the due-ness rule (same
month, missed 1st, year boundary, clock moved backwards, unparseable stamp,
stamp round-trip) and the version rule (newer/equal/older/unknown/unparseable
on either side/no catalog, plus the notification wording), and a new CoreTests
group (26 now) pinning the nullable-bool contract and the cross-process merge.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `c1efd57c8`

**chore(catalog): regenerate for aibrain 1.1.1**

```
The catalog hashes the COMMITTED blob, so this lands after the zip commit, not
with it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `ad7038ab0`

**feat(modules): offer updates for installed modules, applied across a restart (1.4.2)**

```
Republishing a module could only ever reach people who had not installed it
yet. ModulesPaneControl.DiffNew() diffs the catalog BY ID, so an installed
module disappears from the available list permanently: no version was ever
compared, nothing checks at startup, and the only route left was Uninstall --
which deletes the module's settings, API keys and chat history. The aibrain
1.1.1 OCR fix would have stranded every existing AI Brain user.

An installed row whose live Info.Version is older than the fetched catalog's
now offers "Update to vX.Y.Z". A loaded module's DLL is locked, so the payload
cannot be written over the install folder from the process asking for it; it is
verified, unpacked into <baseDir>\module-staging\<id>.staged, and swapped in by
the next launch before ModuleHost.LoadFrom can lock anything -- the same
deferred trick PendingModuleRemovals uses for deletes.

Placement is deliberate. Staging sits outside modules\ because LoadFrom loads
every subdirectory it finds and would happily load a half-written "aibrain.new"
as a module, and under BaseDirectory rather than the data root so the swap is a
same-volume Directory.Move (a portable install can live on another drive). The
swap moves the old copy aside first and rolls back if the move fails: deleting
first and then failing would leave the user with no module at all, which is
worse than the stale one they were replacing. Unlike an uninstall, the module's
DATA directory is untouched -- keeping settings across an update is the point.

Removals are processed first, so an uninstall that raced an update wins instead
of resurrecting the module. An unparseable version on either side offers
nothing rather than guessing, since a wrong guess is an Update button that
never goes away.

Four new --module-host-selftest assertions cover the swap on throwaway
directories: payload replaces the install, module data survives, an
uninstalled id is not resurrected, and an empty staging folder keeps the
installed copy. The marker path is an explicit parameter because
AppPaths.DataRoot resolves once per process at static init, so a test cannot
redirect it by setting the override variable late.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `1806becc6`

**fix(aibrain): decode OCR output as UTF-8, not the ANSI codepage (1.1.1)**

```
The pet was quoting mojibake off the user's screen ("'asÂ®' my ass -- that's
just a trademark"). Not the model's fault: RunOcrAsync redirected tesseract's
stdout without setting StandardOutputEncoding, and an unset encoding is taken
from GetConsoleOutputCP(), which returns 0 in a GUI process with no console.
.NET decodes codepage 0 as CP_ACP -- the system ANSI codepage (1252 here) --
so tesseract's UTF-8 arrived mis-decoded: "as®" (61 73 C2 AE) as "asÂ®", and
with it "—" as "â€"", "’" as "â€™", "©" as "Â©", "é" as "Ã©". Curly
apostrophes are on nearly every page, so the brain had been fed corrupted
context routinely, and bytes with no CP1252 mapping landed on C1 controls that
CleanOcr then stripped, losing characters outright.

Pinned to lenient UTF-8 on stdout and stderr. Lenient, not the strict
UTF8Encoding this repo uses for durable files: strict throws mid-read and
RunOcrAsync's catch turns any throw into "", which would blind the pet to the
whole screen over one bad byte. Windows' built-in OCR was never affected (WinRT
strings), so only Tesseract users ever saw this.

Guarded three ways, all negative-tested:
- the Test OCR probe image now carries a ® and the status goes red on a
  mis-decode (a MISSED ® stays a pass -- only a mis-decoded one fails)
- --aibrain-selftest asserts the extracted psi factory pins UTF-8 on both
  streams, so it holds on CI where no OCR engine is installed
- runtime-hardening-selftest.ps1 fails repo-wide if any RedirectStandardOutput
  lacks a paired StandardOutputEncoding

Verified through the shipped module against the real engine: Test OCR returns
"✓ OCR working — using tesseract.exe" with no mis-decode.

Republished modules-dist/aibrain.zip + modules.json at 1.1.1.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-14  `54123c484`

**docs(handoff): v1.4.1 released — codebase/tag/release/box all aligned**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-14  `09d60331a`

**docs: session wrap-up — scrub PII from BACKLOG, refresh handoff, drop stale S6p2 plan**

```
- BACKLOG: remove the actual work email / name / employer from the (now-resolved) "work email" note —
  the scrub is DONE, so the note is updated to DONE and de-PII'd; the upstream-tag-collision note is
  also marked DONE (resolved by jumping to 1.4.0). Added DONE notes for the S6p2 revert and the
  Contracts FileVersion fix.
- handoff.md: new "Current state (2026-08-14)" section (v1.4.0 release, history scrub + refs/pull
  residual, S6p2 built-then-reverted, Contracts FileVersion fix, box on dev 1.4.1); old big-picture
  kept as historical.
- Removed S6P2-PETS-MODULE-PLAN.md — it described the reverted feature as "proposed, not started"
  (stale/misleading); the design is preserved in git history.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-14  `890f76d44`

**revert(s6p2): fully revert the Pets-module stream (P2a–P2d) per user**

```
The Pets-as-a-module direction (module-owned Options pane + tray + per-pet voice) was rejected after the
live eyeball, so it's reverted: the host's original Pets gallery + icon'd tray are restored.

Reverted to the pre-S6p2 state: the ICompanionManager ABI + CompanionHost bridge, the Pets module, per-row RowActions
+ the HideCheckbox renderer, per-type settings scoping, per-pet voice, and all the CollectPanes / build /
self-test-fake wiring. Deleted modules/Pets + the petmanager/pets self-tests.

KEPT (genuine, independent of the module direction):
- the DesktopPet.Contracts FileVersion fix (so MSI upgrades actually refresh the ABI dll), and
- ProductVersion 1.4.1, so that fix carries a version.

Gated: build -Release 0/0; module-host / fortunes / wpf-options / hardening / security all PASS/exit 0
(the pre-S6p2 suite; the petmanager/pets self-test flags are gone with the feature).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-14  `4f2e22505`

**revert(s6p2): keep the Add/Remove tray submenus host-owned (they carry the pet icons)**

```
P2c moved the tray "Add a pet" / "Remove a pet" submenus into the Pets module as plain TrayItems and
hid the host's originals — which dropped the pet icons (Resources.icon / Resources.removepet), since the
module items set no IconPng. Reverted: the tray stays host-owned (icons intact) and ContextMenus no
longer guards those items. The Pets module still owns the Options -> Pets pane; only the tray reverts.

(A fully-modular tray would need TrayItem.IconPng set with real icon bytes — a follow-up if wanted.)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `900913342`

**fix(packaging): stamp DesktopPet.Contracts FileVersion from the product (S6p2 upgrade blocker)**

```
The ABI assembly hardcoded FileVersion=1.0.0.0, so a Windows Installer major upgrade SKIPPED
overwriting DesktopPet.Contracts.dll whenever its content changed but the version didn't — which is
exactly what S6p2 did (added ICompanionManager etc). Upgrading users would get a stale Contracts.dll and the
new modules would throw TypeLoadException resolving the new ABI types (hit live during the 1.4.1 eyeball
install).

Fix: FileVersion now tracks the product ($(DesktopPetAssemblyVersion), via src/Directory.Build.props'
ProductVersion.props import), so the installer refreshes the DLL on every version bump. AssemblyVersion
stays 1.0.0.0 — that's the ABI binding version modules reference, and bumping it would break loading of
already-built modules.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `aba2edb92`

**chore: 1.4.1 (dev build for the S6p2 Pets-module eyeball)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `b4ad1a693`

**docs(s6p2): record P2d part 2 (per-pet voice) done in the plan**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `520aada9a`

**feat(s6p2): P2d (part 2) — per-pet voice (which speaker answers per pet)**

```
Each pet TYPE can now be set to a specific speaker — one sheep does Fortunes, another the AI Brain,
a third inherits the global default — the "which voice" half of backlog #16.

- Contracts: VoiceOption + ICompanionManager SpeechSources()/GetVoice()/SetVoice().
- CompanionManagerBridge: SpeechSources = "" (Default & Random) + each poke-responder module (named from the
  loaded modules); Get/SetVoice route to the existing per-pet GetTriggerSpeechModule /
  SetTriggerSpeechModule (already per-pet with a global fallback — only the poke path was hardcoded "").
- Runtime: the poke path now carries the poked pet's TypeId (FormCompanion -> OnPetPoked(petTypeId) ->
  TryPokeReaction), so poke 1 resolves the per-type Trigger-Speech choice instead of always the global.
- Pets pane: a per-pet "Voice: <speaker>" button (cycles Default -> Fortunes -> AI Brain -> ...), shown
  only when there is a real choice.

NOT in this commit (the deeper, higher-risk half): per-pet PERSONA — e.g. each pet running a different
AiBrain disposition. That needs per-request persona in the AiBrain/Fortunes engines (they bake config
into a session/pool built once, globally), a real retrofit of a security-sensitive module left for its
own stream. P2d part 1's per-type settings overlay is the storage it would use.

Gated: build -Release 0/0; --pets, --petmanager, --wpf-options, --module-host, --aibrain, --hardening,
--security all PASS/exit 0 (security covers the poke/trigger-speech arbitration this touched).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `5b28e42f1`

**docs(s6p2): record P2a-P2c done + P2d part 1 in the plan**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `25dfa2af7`

**feat(s6p2): P2d (part 1) — per-pet-type settings scoping, the #16 foundation**

```
The enabling piece for per-pet voice: a per-type settings overlay so one sheep can run a different
voice/config than another without duplicating a module's whole settings doc.

- Contracts: IHost.GetSettings(moduleId, petTypeId) — a per-type view that overrides the module's
  global settings for that pet type and falls through to global for any unset key ("" => global).
- CompanionHost: ScopedModuleSettings layers a per-type file (settings.pet-<id>.json) over the module's
  global settings.json; reads fall through, Set/Save touch the override only.
- Updated the 5 IHost self-test fakes for the overload.
- --petmanager-selftest gains 6 scoping assertions (override reads back, an unset key falls through,
  a different type sees global, the global stays untouched).

Remaining P2d (consumer wiring, not in this commit): AiBrain/Fortunes reading their per-type config
keyed on the pet an event is for (ICompanion.TypeId), and a per-type "Voice" picker in the Pets pane.

Gated: build -Release 0/0; --petmanager / pets / fortunes / aibrain / module-host / wpf-options /
hardening all PASS/exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `4d5b219ec`

**feat(s6p2): P2c — Pets tray moves into the module; host Pets UI becomes a fallback**

```
The Pets module now contributes its own "Add a pet" / "Remove a pet" tray submenus, and the host's
built-in tray items + Pets gallery are hidden when the module is present — kept as a fallback for a
lean install without it (deliberately NOT deleted, so uninstalling Pets never leaves no pet UI).

- modules/Pets: BuildTrayItems — Add/Remove submenus via TrayItem.BuildChildren over ICompanionManager.
- ContextMenus: hides the built-in Add/Remove pet items when the pets module is loaded (detected by
  its contributed "Pets" pane, the same signal OptionsShell uses for the gallery).
- CompanionManagerBridge: OnScreenMix resolves the active/default pet's "" slot to its real type id (and
  merges), so the roster counts line up with InstalledTypes and the tray can name each row; RemoveOne
  falls back to the "" active slot when a type has no extra instances.

Gated: build -Release 0/0; --pets-selftest, --petmanager-selftest, --wpf-options-selftest,
--module-host, --hardening all PASS/exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `9f508b29f`

**feat(s6p2): P2b — Pets module + per-row-action ABI; the roster/downloads UI leaves the host**

```
Pets becomes a plugin: a new modules/Pets contributes the "Pets" options pane through the ABI,
driving everything via ICompanionManager. The host keeps the pet engine + persistence; the module owns
only the UI.

- Contracts: ListItem.RowActions (per-row buttons) + RowAction; ListCard.HideCheckbox (button-
  driven cards); ICompanionManager gains GetSizeLevel/SetSizeLevel/GetSoundEnabled/SetSoundEnabled for
  pane parity with the old gallery.
- Host renderer (OptionsWindow): a flat button-row branch for HideCheckbox cards renders each
  RowAction as a button (disable -> await -> status -> optional pane reload); the existing
  checkbox/group/tri-state path is untouched.
- modules/Pets (id "pets"): "Your pets" roster (Use/Add/Remove/size/sound per pet via RowActions)
  + "Available online" browse/download (CatalogKinds.Pet), all through ICompanionManager. Ships no pet
  content.
- OptionsShell.CollectPanes defers to the module's "Pets" pane when present (skips the built-in
  gallery) so exactly one shows — a soft guard ahead of P2c deleting the host UI.
- CompanionManagerBridge implements the new size/sound verbs.
- New --pets-selftest (loads Pets.dll, asserts the pane + that row actions call the manager);
  build.ps1 now builds the Pets module.

Gated: build -Release 0/0; --pets-selftest, --petmanager-selftest, --wpf-options-selftest,
--module-host, --hardening all PASS/exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `53912a624`

**feat(s6p2): P2a — ICompanionManager ABI + CompanionHost bridge (Pets-as-module foundation)**

```
The plugin ABI gains a pet-orchestration surface so the Pets capability can move into a
module (S6 phase 2). No behavior change; everything is additive and gated.

- Contracts: ICompanion.TypeId/DisplayName (key per-pet config on the pet an event is for);
  CatalogKinds.Pet; CompanionTypeInfo/CompanionCount DTOs; ICompanionManager (enumerate/spawn/remove/
  set-active/install/uninstall); IHost.GetCompanionManager().
- FormCompanion: internal PetTypeId accessor (from the shared Animations' PetTypeId).
- CompanionHost: CompanionManagerBridge over StartUp's verbs (AddPetFromTray/RemoveOnePet/
  OnScreenMix/IsAtMaxPets/LoadNewXMLFromString) + the pet library on disk (validated,
  path-contained install/uninstall mirroring CompanionsPaneControl); the host keeps owning the
  persisted mix / size / sound / active-id and MAX_SHEEPS. CompanionHandle reports TypeId/
  DisplayName. FetchCatalogItemsAsync/DownloadCatalogItemAsync gain a CatalogKinds.Pet path.
- New --petmanager-selftest (enumerate, no-runtime no-ops, install/enumerate/uninstall
  round-trip); wired the flag in Program.cs and the file into the csproj.
- Updated the 5 IHost/ICompanion self-test fakes for the new ABI members.

Gated: build -Release 0 warn/err; --petmanager-selftest PASS; module-host/aibrain/
fortunes/hardening self-tests all exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `52ba7c3f3`

**chore(catalog): regenerate for Fortunes 1.1.1 (genre-filter fix)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-13  `2dd60ba69`

**fix: stop the pet reading its own "Sheep" window as context; make Genres filter downloaded packs**

```
- ActiveWindow: ignore foreground windows owned by the pet's own process, so a poke/drag
  no longer makes the "Sheep"-titled pet form the foreground window and route contextual
  fortunes into the sheep/wool cluster (the "sheep jokes on a loop" bug). Falls back to a
  plain random fortune; fails open.
- Fortunes: derive a taxonomy genre per downloaded pack via FortuneClassifier.ClassifyGenre
  (tv-* -> tv-quote, *fact* -> fact, limerick/songs-poems -> verse, dadjokes/yo-mama/riddles
  -> joke, else quip) instead of hardcoding Genre="quip", so disabling tv-quote/fact actually
  filters downloaded packs. Republish Fortunes module 1.1.1 (fortunes.zip + modules.json).
- Bump app to 1.4.0 (clears the upstream tag-range collision at v1.2.4-v1.3.2).
- Backlog: drop S7 (third-party signing) + TTS, remove #10 (About tab), mark dispositions and
  capability-aware model dropdowns smoke-tested/verified.
- Add S6 phase 2 plan (Pets becomes a pre-installed module; folds in per-pet personality/voice).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-12  `6c293312b`

**docs: reconcile Readme/BACKLOG/handoff with v1.2.3, and widen the ignores**

```
Readme described the four tone controls that v1.2.3 replaced with one Content
level, claimed Options still had a Fortunes tab rather than per-module panes,
and never mentioned that screen reading now works without Tesseract or that the
Fortunes module carries a built-in corpus. All four are user-facing.

BACKLOG still said the last public release was the v1.0.x line. Records the four
fixes from PR #72 and opens two items that are decisions rather than work:

- Our release tags collide with upstream's v1.2.3-v1.3.2, which is why tagging
  tonight failed. Recurs for the next six versions.
- The first 10 fork commits carry a work email as author and committer on a
  public repo. Everything since is bigfnj/peshinator@gmail.com, and no tracked
  FILE contains it. Fixing the metadata means rewriting history from the first
  commit, invalidating three release tags and every existing clone -- so it is
  deliberately not done, and is flagged for a decision instead.

handoff.md leads with the lesson worth keeping: modules-dist/<id>.zip is a
committed artifact served live from master, so merging IS the publish, and any
PR touching modules/<Id>/ needs a republish commit before CI passes. Also
corrects the claim that the smart self-test flags were retired -- they were
orphaned, and are wired again.

.gitignore gains the AI/agent leftovers it missed (.claude/, .cursor/, .mcp.json,
CLAUDE.local.md, .aider*, HANDOFF_*) and secret-shaped files (.env, *.pem, *.p12,
*.jks, secrets.json, appsettings.*.local.json). Nothing tracked is shadowed by
these; real API keys already live DPAPI-encrypted under %LOCALAPPDATA%, outside
this tree. Audit found no tokens, keys or certs tracked, and no employer material
in any file.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `443ef809a`

**chore(release): 1.2.3**

```
Fortunes clarity (backlog #9), plus three bugs found while building it:

- One ordered Content level replaces four interlocking tone controls, with a
  live pool count that warns when the filters leave nothing to say and a
  "show me 5 examples" preview.
- Pack/genre ticks are staged and applied on Apply. Each one used to write
  settings and rebuild the whole engine, so turning off a group of 19 packs
  meant 19 disk writes and 19 re-warms of the ONNX index.
- The smart-index status read the index's own counters, which are zero for the
  moment right after a rebuild, so "Rebuild smart index" reported "No fortunes
  yet" every single time regardless of pool size.
- The built-in fortune corpus was never embedded in the Fortunes module: the S3
  move dropped it from the base and the module never picked it up, leaving lean
  installs with nothing to say. 10,310 lines restored, 7 of its sources exist
  in no pack file.

Modules published at 1.1.0 (Fortunes gains the corpus, AI Brain gains the
Windows OCR fallback from PR #71), and CI now fails when a published module
payload falls behind its source -- the drift that hid both of the above.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `8fbb6c731`

**Merge pull request #72 from bigfnj/feature/fortunes-clarity**

```
feat(fortunes): one Content level, live pool count, preview, group toggles
```

### 2026-08-12  `c83933483`

**chore(catalog): regenerate for the republished fortunes payload**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `751352d4d`

**chore(modules): republish fortunes.zip; scope the drift guard to shipped files**

```
The new guard caught its first real drift on its first CI run, and it was mine:
the self-test wiring changed FortuneEngineProbe.cs, which compiles into
Fortunes.dll, so the published payload no longer matched its source. Republished.
Still 1.1.0 -- that version has not reached master yet, so this is amending what
1.1.0 will be rather than superseding a released payload.

Scope the check to files that actually reach the assembly: markdown is excluded,
because modules\Fortunes\BACKLOG.md would otherwise demand a 31 MB republish for
a note. Images and welcome.json stay in scope (embedded resources), and so does
probe/self-test code -- it compiles into the shipped DLL like anything else, so
the guard was right to flag it. Exclusion verified against e8d7714, where
BACKLOG.md drops out of the matched file list while the .cs files remain.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `dd8c8f002`

**test(fortunes): un-orphan the smart self-tests and guard publish drift**

```
Two self-tests lost their callers in the S3d move of the ONNX engine into the
module and have asserted nothing since. They are the reason nobody noticed the
built-in corpus was missing: both build their pool from the embedded corpus and
fail when it is empty.

- SmartFortunes.SelfTest joins --fortunes-engine-selftest (11s total). It
  covers contextual picking and a real variety regression: a STABLE context
  must still rotate through 12+ distinct lines out of 40, where the original
  bug served ~3 distinct lines out of thousands. Currently 31/40.
- SmartFortunes.ProgressiveSelfTest gets --fortunes-smart-progress-selftest, a
  cold-cache warm of a 1,500-line sample proving Pick serves the warmed prefix
  before the pool finishes. Measured at 18s, not the "minutes" I first assumed,
  so CI runs it too -- being merely opt-in is how it got orphaned in the first
  place.

Also add packaging\Test-ModulePublishFreshness.ps1. modules-dist\<id>.zip is a
committed artifact the catalog serves and nothing rebuilds it automatically, so
it rots whenever module source lands without a republish. That happened twice in
one day: fortunes.zip with no corpus, aibrain.zip a release behind PR #71.

The check compares commit ordering (does modules/<Id>/ have commits newer than
the newest touching modules-dist/<id>.zip?) rather than rebuilding and comparing
hashes -- hash equality would require the module DLLs to build byte-identically
across SDK versions and checkout paths, a stronger promise than this repo makes.
Verified against a worktree at 3d240a1, where it correctly names both PR #71
commits as missing from the published aibrain payload.

CI runs it after the self-tests, and the checkout gains fetch-depth: 0, since a
shallow single-commit clone cannot answer a commit-ordering question.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `8b856baa7`

**chore(catalog): regenerate for AI Brain 1.1.0**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `773fde454`

**chore(modules): publish AI Brain 1.1.0 with the Windows OCR fallback**

```
The published zip was one release behind: it predated PR #71, so anyone who
installed AI Brain from the catalog got no OCR engine picker and no screen
reading at all unless they had Tesseract on PATH -- which was the whole point
of that PR.

76 KB -> 6.4 MB, all of it the WinRT projection. That reads worse as a
multiplier than it is: fortunes.zip is 31 MB, so this is the smaller half of
the catalog either way. The spike already proved the projection loads in a
collectible ALC, that the host needs no reference to it, and that OCR reads a
probe first try; the one quirk (WinRT pins the ALC) is moot because uninstall
already restarts.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `3d240a1b3`

**chore(catalog): regenerate for Fortunes 1.1.0**

```
Picks up the republished fortunes.zip (now carrying the built-in corpus) and
its new SHA-256, hashed from the committed blob so it matches what raw serves.
aibrain is unchanged at 1.0.0.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `6f5cedd0e`

**fix(fortunes): embed the built-in corpus in the module (it never moved)**

```
A lean install had nothing to say. The S3 relocation dropped the fortunes.txt
EmbeddedResource from the base csproj and left a comment saying it "moved to
modules/Fortunes with the fortune engine" -- but Fortunes.csproj never picked
it up. git log -S "fortunes.txt" on that csproj returns nothing. The
classifier-parity TSV made the trip; the corpus did not.

EmbeddedCorpus() has been failing into _embeddedError on every build since,
and _embeddedError is only ever appended to a diagnostics string nothing
reads, so it never surfaced. The same commit orphaned the two self-tests that
would have caught it -- SmartFortunes.SelfTest and ProgressiveSelfTest both
fail on an empty pool, and neither has had a caller since (the base's
--smart-progress-selftest flag went away with them).

This is not duplicate content: 7 of the corpus's 26 sources exist in no pack
file at all -- quotable (2,109 lines), cleanjokes (1,588), fortunes (431),
godin (401), SimpsonsChalkboard (365), activists (357), BibleAbridged (48).

- Embed it in Fortunes.csproj, which makes the base's comment true at last.
- Assert it in --fortunes-engine-selftest: the corpus must load, and all seven
  pack-less sources must be present, so a silent drop fails a gate instead of
  quietly muting fresh installs.
- --fortunes-selftest asked "did the pet speak a line from my seeded pack?",
  which only passed because the pool was 2 lines deep. With a real corpus the
  picker draws from 10,310, so it now asks against the whole fortune universe,
  pulled across the ALC boundary through a new FortuneEngineProbe.EmbeddedTexts.
- Bump the module to 1.1.0 and republish fortunes.zip (+0.4 MB compressed);
  the Modules pane shows installed vs available version side by side.
- New-ModuleDistZip.ps1 threw "path's format is not supported" on an absolute
  -SourceDirectory: it joined it onto the caller's location. Rooted paths now
  pass through.

aibrain.zip is deliberately untouched. The published one predates PR #71, so
catalog users have an AI Brain with no Windows OCR fallback -- a separate
publish decision, not this fix's to make.

Gates: 8 green, 25 core groups green.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `c45b81471`

**fix(fortunes): the smart-index status always claimed the pool was empty**

```
"Rebuild smart index" reported "No fortunes yet — add a pack, then rebuild"
on every press, no matter how many packs were enabled. Nothing was wrong with
the index; the status was reading the wrong thing. SmartFortunes.Warm() starts
a background task and leaves ready=false, and WarmProgress gates total on
ready, so total is 0 for the moment right after a rebuild — which is exactly
when the button asks. The "no fortunes" branch therefore fired every time.

Take the pool size from the provider, which knows it synchronously, and let
the index's counters answer only "how far along". A just-started warm now says
it is indexing, with the count.

Also split the empty-pool message in two. "Add a pack" is the wrong advice for
someone with 129 of them whose filters exclude everything, so an empty pool
with packs installed now blames the filters and points at the content level.
The pane's pool-status line shares the wording.

And add the case that prompted this: a finished index over an unchanged pool
now reports that it is already built rather than silently re-warming, which
looked identical to a broken button. "Unchanged" is a content fingerprint of
the pool, not its line count, so swapping a pack for another of the same size
still counts as a change.

Gates: 8 green, 25 core groups green. SmartStatusFor/EmptyPoolReason/
PoolSignature are pure, and --fortunes-engine-selftest pins the regression:
a just-started warm must not read as an empty pool.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `4bfdf28a5`

**perf(options): stage pack/genre ticks and commit them on Apply**

```
Every tick on the Fortunes packs card wrote settings to disk AND rebuilt the
engine: re-reading every pack file, re-filtering, and re-warming the ONNX
smart index. One click was slow; the new group toggle made it 19 of them back
to back.

Add ListCard.DeferChanges. A card that sets it has its clicks treated as edits
rather than commands: the box moves at once, the pane goes dirty so Apply
lights up, and the host replays SetChecked once per CHANGED item at Apply,
immediately before OptionsPane.Save. The Fortunes packs and genres cards stage
those ids and commit the whole batch inside Save, so any number of ticks costs
one write and one rebuild.

Ticking a box and ticking it straight back queues nothing. Unapplied ticks are
discarded on close or on a ReloadPaneAfter action, which is what unapplied
field edits already do -- the staging buffer lives on the PaneView, so it dies
with the pane rather than lingering on the module.

Left the "Available online" card live: its ticks feed the Download button, not
the saved settings, so deferring them would hand that button an empty basket.

Gates: all 8 green, 25 core groups green. --wpf-options-selftest asserts the
deferral contract (no work before Apply, dirty still fires, only changed items
replay, they land before Save, a second Apply replays nothing) and
--fortunes-engine-selftest asserts the merge fold that decides which packs the
engine reads.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `4992850e3`

**docs(backlog): close #9 (Fortunes clarity) with what actually shipped**

```
Records the two items found while building that were not in the rescoped
entry: the group-level toggle and the 128-pack ceiling.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `28f9dfde9`

**feat(fortunes): one Content level, live pool count, preview, group toggles**

```
Backlog #9: the Fortunes pane had four overlapping tone controls whose names
did not describe what they did. "Edgy + NSFW" actually admitted general+edgy+
nsfw (i.e. everything), and "True NSFW only" kept tame lines while silently
dropping edgy. Nothing told the user how many fortunes the current combination
left in the pool, so an over-narrow filter looked identical to a broken module.

- Collapse SpicyFortunes/SpicyTier/SpicyOnly into one ordered ContentLevel
  (clean / clean+edgy / everything / spicy only). MigrateContentLevel is a pure
  function so the legacy readings are testable: spicy-off lands on clean and
  skip-tame never widens on its own.
- Add SettingKind.Info to the ABI (display-only field) and use it for a live
  pool count that warns when the current filters leave nothing to say.
- Add "Show me 5 examples" so the tone choice is visible before it is saved.
- Add a whole-group checkbox to collapsible list-card group headers. Turning
  off a section (19 NSFW packs) was 19 clicks. It drives the children through
  their own toggles, so the card's SetChecked still runs per changed item and
  the module persists exactly as it would from individual clicks.

Raise the pack ceiling 128 -> 512: 152 packs are shipped and the old cap was
dropping 24 of them without a word.

Gates: all 8 self-tests green (--wpf-options-selftest now covers the group
toggle's tri-state and its SetChecked calls), 25 core regression groups green.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `26cb692bf`

**docs(backlog): close #14 (Windows OCR, not a bundle) and rescope #9**

```
#14 shipped, but deliberately not as written: Windows' built-in OCR
instead of redistributing Tesseract. #9 was written against the old
WinForms tab and is mostly built now; narrowed to the three items that
actually remain.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `1bd645986`

**Merge pull request #71 from bigfnj/feature/windows-ocr-fallback**

```
feat(aibrain): use Windows' built-in OCR when Tesseract is absent
```

### 2026-08-12  `4cdb048b0`

**feat(aibrain): use Windows' built-in OCR when Tesseract is absent**

```
Backlog #14 wanted a bundled OCR engine so a fresh box can read the
screen; today it silently degrades unless Tesseract happens to be
installed. Bundling, hosting, or CI-compiling Tesseract all mean
redistributing a third-party binary we then own: license notices, CVE
patching, ~30MB of download, and in the compile case a second heavy
build pipeline.

Windows already ships an OCR engine. A throwaway spike confirmed the
three things that could have ruled it out: it reads a probe image with
no install step, it resolves inside the module's own collectible
AssemblyLoadContext, and the host does NOT need the projection -- it
travels with the module. Cost is ~6MB compressed in this module only
(the ~24MB projection DLL is metadata-heavy and compresses ~4x), which
is what the earlier WASAPI rejection was really measuring uncompressed.

Tesseract stays the preferred engine (better on dense text), so
resolution is now: configured path -> usual install locations -> PATH ->
Windows built-in. Test OCR names whichever engine answered, since a
silent fallback would otherwise never tell the user the better option
exists; when it's on the fallback it says so and points at Tesseract.
A "Get Tesseract..." button opens the official install guide -- the
standard installer lands where auto-detect already looks, so afterwards
Test OCR just goes green with nothing to configure.

Opening a browser is a host concern (modules carry no UI), so this adds
IHost.OpenLink, gated on the calling module declaring
ModulePermissions.Network and validated by the existing security-reviewed
WebLinks HTTPS policy.

Known caveat, verified against a no-WinRT control: a WinRT-using module
pins its load context, so its ALC never unloads. Harmless here -- module
uninstall already requires a process restart, and Unload() is only called
at shutdown or on load-failure paths.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `889c31128`

**feat(aibrain): add an OCR engine field and picker**

```
Backlog #14 claimed a "Choose OCR engine..." picker had shipped; it had
not (there was no file dialog anywhere in the module). The OCR path was
also invisible in the UI, so a user whose tesseract lives somewhere the
auto-detect misses had no way to point at it.

Adds a "Screen reading" group with the engine path (blank = auto-detect)
plus a browse button, reusing the PickFilesToOpen host service added for
the fortunes importer. Choosing an engine saves it and immediately runs
the existing Test OCR, so the result flips green or red in one step
rather than leaving the user to guess.

Bundling an engine so a fresh box works out of the box is still open.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `1494187a5`

**Release v1.2.2 - lean host + in-app module catalog**

```
The app now ships lean (installer and portable ZIP carry no modules) and
optional features arrive from Options > Modules: an HTTPS + SHA-256-pinned
catalog that shows a module's declared permissions before downloading and
restarts to activate. This replaces the original "bundle modules into the
installer" plan and absorbs what would have been a separate signed-catalog
and consent stream.

On top of that foundation:
- Right-clicking the pet now speaks. Poke 1 runs an arbitrated responder
  chain (AI quip, else a fortune, else nothing) on its own cooldown; the
  ignore/sass/escape ladder is unchanged. A "Trigger Speech" preference
  picks which installed module wins.
- Fortune packs are reachable at last: browse the catalog, tick what you
  want, download; or import your own through the validating importer.
- The pack picker is browsable - collapsible collections, a filter box,
  and curated names for all 152 packs instead of raw file stems.
- Fixed a silent 128-file load cap that hid 24 installed packs.

Bump ProductVersion 1.2.1 -> 1.2.2.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `d192c9be7`

**chore(catalog): point module entries at the republished packages**

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `e1d074830`

**chore(modules): republish Fortunes + AI Brain module packages**

```
The published zips predated PRs #69/#70, so installing Fortunes from
the catalog got a build with no poke responder, no pack browser, raw
pack ids, and the old 128-file load cap.

Also fixes a path trap in New-ModuleDistZip.ps1: [IO.Path]::GetFullPath
resolves against the PROCESS working directory, which PowerShell's
Set-Location does not update, so a relative -DestinationPath silently
wrote the zip outside the repo (caught doing exactly that). Paths now
resolve against the caller's location.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `9844719d9`

**Merge pull request #70 from bigfnj/feature/pack-browser-usability**

```
feat(fortunes): browsable pack picker, curated names, and a 128-pack ceiling fix
```

### 2026-08-12  `e8d771400`

**feat(fortunes): browsable pack picker, curated names, and a 128-pack ceiling fix**

```
The installed-packs card was a flat, unsorted wall of 150+ checkboxes
labelled with raw file stems ("lwall-quotes", "rfc1925", "off-knghtbrd").
Three fixes, plus a real bug the pack downloader had exposed.

Grouping and filtering are now ABI-level, not fortunes-specific:
ListItem.Group renders collapsible sections and ListCard.Filterable /
CollapseGroups turn on a filter box and start collapsed, so any module's
list card benefits. Installed packs group by the same curated collection
names the online catalog shows -- the local scan knows only ids, so
packs/collections.json is embedded in the module.

Grouping keyed off SourceStat.Custom at first, which was wrong: that flag
is true for anything in the user's fortunes folder, which since the module
bundles nothing is every pack including catalog downloads. All 128 landed
in one section. The curated map is the only reliable signal, so a known id
takes its collection and only unknown ids fall back to "Your own packs".

Filtering matched the generated Detail text ("964 lines - spicy"), and
because every row contains "lines", a query like "lin" matched the entire
list. It now matches identity only (label / group / id); spice already has
three dedicated controls directly above the list.

packs/pack-names.json gives all 152 packs a name that says what they are.
The same file feeds New-ContentCatalog.ps1, so the online card agrees.
Several needed checking rather than guessing -- "stevenson" is Adlai, not
Robert Louis.

The real bug: FortunePackLoadPolicy.MaximumFiles capped loading at 128
files. Nobody could easily install more than a handful before, so it never
bit; "download everything the catalog offers" walks straight into it, and
files 129-152 alphabetically were dropped silently (tv-simpsons among
them -- present on disk, absent from the picker, never spoken). Raised to
512 in both copies (base validates catalog entries, module loads files) to
match the catalog's own per-kind cap, with a self-test asserting the load
cap never falls below it again. The genuine memory bounds -- total bytes
and total entries -- are unchanged and still have headroom.

Also fixed unreadable group headers (Expander had no implicit theme style)
and corrected stale docs claiming the ONNX model ships beside the exe: it
ships inside the Fortunes module package, which is why that module is
~30 MB. Readme now documents Modules as the way features arrive at all.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-12  `cf2de7a07`

**Merge pull request #69 from bigfnj/feature/poke-reactions-and-pack-catalog**

```
feat: arbitrated poke reactions, Trigger Speech, and fortune-pack sourcing
```

### 2026-08-12  `b25b9264c`

**feat: arbitrated poke reactions, Trigger Speech, and fortune-pack sourcing**

```
Right-clicking the pet did nothing until the sass ladder kicked in,
because CompanionPoked was a plain broadcast and only Fortunes acted on it
(the AI brain tracked the poke but never spoke). Poke 1 of a session
now runs an arbitrated responder chain -- an AI quip, else a fortune,
else nothing -- on its own ~12s cooldown, deliberately independent of
the 7s sass reset so a rich reaction can't fire on every brief pause.
The cooldown only advances when something actually spoke, so a silent
attempt doesn't leave the next poke mysteriously mute. The 3-4 ignore
/ 5-11 sass / 12 escape ladder is untouched.

New RegisterPokeResponder mirrors RegisterDropResponder, and a
"Trigger Speech" dropdown (Preferences > Speech) picks which module
wins: "Default & Random" offers every responder in shuffled order,
while an explicit choice restricts to that one (declining = silence,
since a choice is a restriction rather than a preference). The list is
built from live registrations, so it grows and shrinks with installed
modules and needs no base change. Stored keyed by pet id ("" = all
pets) so per-pet voices (BACKLOG #16) land without a migration.

Fortune packs had no acquisition path at all: 152 packs in the catalog
and no way to get them. Added host-mediated catalog access to the ABI
(FetchCatalogItemsAsync / DownloadCatalogItemAsync) so the host keeps
ownership of URL validation and SHA-256 verification while the module
only decides what to keep and where -- any future module with content
gets the same safe path. The Fortunes pane gains browse -> tick ->
"Download selected" (ticking is an in-memory mark only, since
SetChecked is synchronous by contract and must never do network work).

Also wired up FortuneFileImporter, which was fully built but dead code:
"Import your own..." runs user files through its bounded, validated,
per-file atomic path instead of a raw copy, and never silently
overwrites an existing pack. Modules carry no UI framework, so file
picking is a new host service (PickFilesToOpen).

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

### 2026-08-11  `6d80f26cb`

**docs(backlog): queue per-pet speech personality/preference**

```
Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `42d5678a3`

**docs(backlog): record S6 phase 1 as done + live-verified**

```
Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `a2f58fd7b`

**Merge pull request #68 from bigfnj/feature/s6-modules-catalog**

```
feat(modules): S6 phase 1 -- in-app Modules catalog
```

### 2026-08-11  `be9b0bae0`

**feat(catalog): publish Fortunes + AiBrain to catalog.json**

```
Regenerated via New-ContentCatalog.ps1 against the just-committed
modules-dist/*.zip blobs, so the recorded SHA-256/byte-size match
exactly what raw.githubusercontent.com will serve. Also surfaces the
modules count in --catalog-parse-file='s diagnostic output (it silently
only reported pets/packs before, which would have hidden a schema
regression in the new modules array).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `1af865fff`

**feat(packaging): publish Fortunes + AiBrain as installable module zips**

```
New-ModuleDistZip.ps1 zips a module's build output (excluding .pdb/
.lib, matching the base's own lean-manifest convention) into
modules-dist/<id>.zip -- the exact shape the Modules pane's install
flow extracts directly into modules/<id>/. modules-dist/modules.json
carries the catalog metadata (name/desc/version/permissions) that
isn't derivable from the zip alone; New-ContentCatalog.ps1 now reads
it and emits a third "modules" array in catalog.json alongside pets/
packs, hashing each zip the same way (git blob when committed, so the
recorded hash matches exactly what raw.githubusercontent.com serves).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `a46171e54`

**feat(modules): S6 phase 1 -- in-app Modules catalog pane**

```
A lean host ships zero modules today (neither the MSI nor the portable
ZIP carries Fortunes/AiBrain), so this is how a real install ever gets
any: a new Modules pane (fixed second in nav, after Preferences, with
everything else -- Pets today, any module pane -- alphabetized in the
tail) lists what's installed and lets the user install/uninstall from
the same HTTPS/hash-pinned catalog pets and fortune packs already use.
RemoteCatalog gains a third parallel list (CatalogModule, alongside
CatalogCompanion/CatalogPack) carrying each module's declared permissions so
the install prompt shows what it can do before any code ever runs.

Modules only load at startup, so install/uninstall restarts the app --
reusing Program.cs's RequestRestart/CompleteInstanceLifecycle/
LaunchReplacement chain, which existed but had zero real callers until
now. Threaded an optional --reopen-options=<pane> argument through it
so the relaunch reopens Settings back on the Modules pane.

Uninstall can't delete a module's DLL immediately -- it's locked while
loaded in the current process. PendingModuleRemovals marks the id
instead; the next launch deletes it before ModuleHost.LoadFrom ever
gets a chance to re-lock it. (Found live: the first real uninstall
attempt failed with "access denied" until this landed.)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `f4a1ab14e`

**docs(backlog): record the distinct-tray-icons PR and close #15**

```
Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `815d666e4`

**Merge pull request #67 from bigfnj/feature/distinct-tray-icons**

```
feat(tray): give every menu item its own distinct icon
```

### 2026-08-11  `d1763d2f1`

**feat(tray): give every menu item its own distinct icon**

```
New purpose-drawn icons instead of reusing another item's: Remove a
pet gets a red prohibition sign, Test Speech a speech bubble.

Disable AI and Ask about my screen (module-contributed via AiBrain)
could never show an icon at all -- DesktopPet.Contracts.TrayItem had
no icon property, by design (the ABI stays framework-agnostic, no
System.Drawing). Extended it with optional raw PNG bytes (IconPng)
instead of a concrete image type, decoded host-side in
ContextMenus.BuildModuleMenuItem; the decoded Bitmap is cloned off
its source stream (GDI+ can lazily reference a disposed stream
otherwise) and disposed on each menu rebuild so repeat tray opens
don't leak. AiBrain ships its two icons (red X, tiny monitor) as
plain embedded resources, same pattern as Fortunes' welcome.json.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `96451788c`

**docs(backlog): record the tray icon fix + queue the module-icon ABI gap**

```
Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `05afb2db3`

**Merge pull request #66 from bigfnj/fix/tray-menu-icons**

```
fix(tray): add missing icons on Remove a pet + Test Speech
```

### 2026-08-11  `9ad89f586`

**fix(tray): add missing icons on Remove a pet + Test Speech**

```
Every other tray item (Add a pet, Options, About/Help, Remove all
pets and Close) had an .Image assignment; these two never did.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `582b429c2`

**docs(backlog): record the chat-memory removal and optional-username fix**

```
Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `7b011706b`

**Merge pull request #65 from bigfnj/cleanup/remove-chat-memory**

```
fix(aibrain): remove chat-memory feature (self-reinforcing repeat loop)
```

### 2026-08-11  `1409192ba`

**fix(aibrain): remove chat-memory feature (self-reinforcing repeat loop)**

```
MemoryEnabled ("Remember recent remarks") replayed the pet's own past
remarks back into its own prompt, which is exactly what caused an
earlier repetition-loop bug this session (fixed then by turning it
off, not by removing the feature). User: "this caused issues, remove
it" -- and since nothing is left to clear, drop the now-pointless
"Clear chat history" action too. Removes ChatHistory.cs (945 lines)
and every setting/pane/self-test touchpoint; AiSettings.MemoryEnabled
is gone (no migration needed -- a stale key on an old doc is inert).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `a794e3e2a`

**Merge pull request #64 from bigfnj/fix/optional-username**

```
fix(aibrain): stop forcing the user's name into every remark
```

### 2026-08-11  `6e7d722f2`

**fix(aibrain): stop forcing the user's name into every remark**

```
BuildSystemPrompt said "Always address them as <name>" -- a hard
per-remark requirement. Softened to "use their name only when it
actually fits," keeping the existing guard against inventing a name
or reading one off the screen.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `c27a3ab74`

**Release v1.2.1 — net10 migration + plugin re-architecture + AI voice system**

```
Everything since v1.0.6 (61 PRs), bundled into one release:

- .NET Framework 4.8 -> .NET 10 (LTS) migration (net10.0-windows, SDK-style,
  framework-dependent).
- Plugin re-architecture S1-S5c/d/e: DesktopPet.Contracts ABI + ModuleHost
  loader (per-module collectible AssemblyLoadContext); Sound, Fortunes, and
  AI Brain extracted into modules; WPF settings shell (grouped/masonry panes,
  dark theme, version stamp) replaces the old WinForms Options; base AI
  cluster deleted (~6.8k lines) once its security tests were relocated into
  the module; Newtonsoft.Json dropped product-wide for in-box System.Text.Json;
  About/Help retired to themed WPF windows (WebView2 fully retired).
- Pets: multi-pet gallery with character names + active badge, per-pet size
  and sound, online catalog download.
- Fortunes: bundled bge-small ONNX smart-embedding (SmartFortunes, on by
  default, CPU-only), spicy tiers, source/genre management pane.
- AI brain (opt-in, off by default): provider redesign (local Ollama/
  llama.cpp/LM Studio + optional cloud + automatic local fallback),
  capability-aware model dropdowns with real vision-capability + VRAM-size
  detection and uncensored tagging, and the Personality+Speech-style axes
  merged into one curated 26-character Disposition catalog (AiSettings
  schema v3) — Ted Lasso, Jules Winnfield, Jeff Ross, Etrigan, and 22 more.

Bump ProductVersion 1.2.0 -> 1.2.1.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `2f3c34125`

**Merge pull request #63 from bigfnj/feature/ai-voice-tuning**

```
feat(aibrain): merge Personality+Speech into one curated Disposition
```

### 2026-08-11  `9d266db21`

**feat(aibrain): merge Personality + Speech style into one Disposition**

```
The two axes could stack into incoherent pairings (e.g. "Shy and
sweet" + "Jules Winnfield"), and a named character is a much sharper
LLM style-transfer target than an abstract adjective blurb. Replaces
them with one curated 26-entry Disposition catalog where every entry
bakes tone and delivery into a single instruction. AiSettings schema
v2->v3: Disposition replaces Personality+SpeechPattern; a legacy
SpeechPattern id this schema absorbed under the same id carries over,
anything else falls back to the new default (Ted Lasso).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `9b842a984`

**fix(aibrain): stop Jules Winnfield speech from self-censoring swears**

```
The example word list literally contained "motherf***er" with the
censoring asterisks baked in; the model was copying that exact
censored spelling into its output instead of writing the word out.
Spelled it out in full and told the instruction not to self-censor.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `de2c44ea9`

**feat(aibrain): tune persona voice for real roast profanity**

```
Samuel's speech re-targeted at Jules Winnfield specifically (profanity
as a strong default reflex, not a per-remark requirement) since a
named character is a sharper style-transfer target than a generic
actor descriptor. Added a Jeff Ross roast-comic personality. Raised
the remark cap to 1-2 sentences / ~20 words each so a roast has room
for a setup and a knockdown instead of being squeezed into one clause.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
```

### 2026-08-11  `e1b209e35`

**Merge pull request #62 from bigfnj/feature/model-vram-size**

```
feat(aibrain): show real Ollama model size (VRAM proxy) in model dropdowns
```

### 2026-08-11  `97d004606`

**feat(aibrain): show real Ollama model size (VRAM proxy) in the model dropdowns**

```
Ollama's /api/tags already reports each model's on-disk size (bytes) - a solid proxy for
its VRAM/weight footprint when loaded. Surface it in the dropdown label so "will this fit"
is answerable at a glance. (A Browse-for-a-local-file button was considered and explicitly
dropped after discussion: it can't make a file usable by either backend - Ollama requires
an import/registration step for a new local file, and a bare llama.cpp server's model is
fixed at process launch, not swappable per request - so it would only have added a cosmetic
label with no functional effect. ollama pull + the existing "Refresh models" action already
cover real usage.)

- ModelListing: new SizeBytes (long?) - a real value from Ollama's own "size" field, null
  when the backend has none (the generic OpenAI-compatible /v1/models response carries no
  size metadata at all).
- JsonRead.Int64OrNull (Ollama sizes are multi-gigabyte, past Int32 range).
- OllamaClient.ListModelsAsync now also parses the response's "size" field.
- AiBrainModule label formatting redesigned: replaced the old suffix-strip
  ModelLabelForId/ModelIdForLabel with FormatModelLabel/ResolveModelId backed by a
  label->id dictionary. A variable-length size PREFIX ("4.9GB · dolphin3:8b · uncensored")
  can't be reversed by a fixed string pattern the way the old uncensored-only SUFFIX could;
  the dictionary is populated as a side effect every time a label is produced (Load's
  current-value label, and each listed model's label), and Load always runs before Save,
  so a lookup always succeeds for anything the user could have actually picked.
- FormatSize: whole MB under 1GB, one-decimal GB above (decimal/1000-based units).

Verified: clean -Release (0 warnings); --aibrain-selftest 93 PASS/0 FAIL (up from 92, +1 -
the size parse, both the real-value and the absent-key-is-null cases; zero existing
assertions weakened); --wpf-options/--module-host/--hardening/--security/--fortunes exit 0;
CoreTests 24; hardening ps1; resource-churn. Module-only diff.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-11  `d9400890f`

**Merge pull request #61 from bigfnj/docs/backlog-model-dropdowns**

```
docs(backlog): record model dropdowns + uncensored tagging as done
```

### 2026-08-11  `8d6bf7384`

**Merge pull request #60 from bigfnj/feature/model-dropdowns**

```
feat(aibrain): capability-aware model dropdowns + uncensored tagging
```

### 2026-08-11  `93fc69a69`

**﻿docs(backlog): record capability-aware model dropdowns + uncensored tagging as done**

```
PR #60. Notes the closed-dropdown safety invariant and that it still needs a manual
smoke test against a real Ollama instance. Docs-only.
```

### 2026-08-11  `e094fe0e4`

**feat(aibrain): capability-aware model dropdowns + uncensored tagging for Samuel/Triumph**

```
The AI Brain pane's four model fields (local/cloud text+vision) were free-text, so a user
could pick a non-vision model for the vision slot, and there was no easy way to find models
that actually comply with the profane Samuel/Triumph personas (a heavily-RLHF'd model tends
to soften or refuse the roast). Real model-picker dropdowns, capability-filtered, with
uncensored-leaning models tagged and sorted to the top (never hidden - other personas want
ordinary models).

Engine (modules/AiBrain/engine/):
- New ModelListing.cs: one list entry (Id, Vision as bool? - a REAL signal when the backend
  reports one, null when unknown so the caller falls back to the name heuristic).
- AiSettings.cs AiModelPolicy: new LooksUncensored + UncensoredModelMarkers (dolphin,
  uncensored, abliterated, unfiltered), mirroring LooksVisionCapable's exact substring/
  lowercase idiom. Advisory only, empty/unknown -> false (opposite default from the vision
  heuristic, since this is a positive tag, not a warn-if-not-X advisory).
- OllamaClient.ListModelsAsync: GET /api/tags (already probed for connectivity, now also
  reads the body) - a REAL vision signal from the response's "capabilities" array
  (confirmed via Ollama's own docs) when present, else null (older server -> heuristic).
- OpenAiCompatBackend.ListModelsAsync: GET {base}/models -> ids only (no capability
  metadata generically) + a new test-only diagnostic ctor (injectable HttpMessageHandler,
  mirrors OllamaClient's existing one) so the parse logic is testable offline.

AiBrainModule.cs:
- The four model SettingField objects are now retained instance references (Kind Text->Enum)
  so a refresh can mutate .Options in place - the pane's Options array is captured once in
  Schema and PaneView only re-reads it fresh on a PaneAction.ReloadPaneAfter rebuild; Schema
  itself is never rebuilt.
- Two new "Refresh local/cloud models" PaneActions (ReloadPaneAfter=true) fetch + cache the
  list and rebuild the dropdowns.
- BuildModelOptions: text dropdown = every model (uncensored-tagged ones sorted first, label
  "id" / "id · uncensored"); vision dropdown = only vision-flagged models (real capability or
  heuristic) - a non-vision model can never be picked there. SAFETY INVARIANT: the currently-
  saved value is ALWAYS unioned into Options (even pre-refresh or if the fetch came back
  empty) - the pane's Enum field is a closed, non-editable ComboBox, so a value missing from
  Options would show nothing selected and silently blank the field on save.
- ModelLabelForId/ModelIdForLabel: the label IS the id, plus a fixed " · uncensored" suffix
  when tagged, so recovering the id is a plain suffix-strip (no lookup table needed).

Self-tests: LooksUncensored assertions beside the existing vision ones; a new
FixedJsonResponseHandler double + CheckModelListing proving (1) Ollama's real capabilities
array is honored for both vision-true and explicitly-vision-false, (2) an absent
capabilities key yields Vision=null (unknown, not a false claim), (3) the generic /models
response parses ids with no capability metadata.

Verified: clean -Release (0 warnings); --aibrain-selftest 92 PASS/0 FAIL (up from 88, +4
new, zero existing assertions weakened); --wpf-options/--module-host/--hardening/--security/
--fortunes exit 0; CoreTests 24; hardening ps1; resource-churn. Module-only diff.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-11  `aa3cf152c`

**Merge pull request #59 from bigfnj/docs/backlog-local-backend**

```
docs(backlog): record local-backend fix + queue model dropdowns
```

### 2026-08-11  `ea4d6fab8`

**Merge pull request #58 from bigfnj/fix/local-backend-kind**

```
fix(aibrain): local slot was hardcoded to Ollama; restore llama.cpp/LM Studio
```

### 2026-08-11  `de32108fb`

**﻿docs(backlog): record the local-backend fix + queue capability-aware model dropdowns**

```
PR #58 fixed the local-slot Ollama lock-in. Queues the deferred model-dropdown work
(LooksVisionCapable exists but is unwired; needs a live model-list fetch + two
capability-detection paths). Docs-only.
```

### 2026-08-11  `b7796ddcd`

**fix(aibrain): the local slot was hardcoded to Ollama; restore llama.cpp/LM Studio support**

```
The provider redesign (BACKLOG #13) hardcoded the LOCAL slot to OllamaClient unconditionally.
Before that redesign, "lmstudio"/"llamacpp" were valid local Provider ids served by the generic
OpenAiCompatBackend (llama.cpp's server and LM Studio both speak the same OpenAI-compatible /v1
protocol Ollama doesn't use natively) - that capability was silently dropped. Ollama is also not
bundled (confirmed: no ollama.exe in any packaging script); OllamaPath just autodetects it on
PATH, so a user without Ollama installed had no local option at all post-redesign.

- AiSettings: new LocalBackendKind field ("ollama" default | "openai-compat"), clamped/validated
  in Normalize via IsKnownLocalBackendKind. No schema bump needed - it's a new optional field
  with a safe default, so an absent key in an old doc keeps the "ollama" field initializer after
  deserialization (verified by a self-test). The merge-on-save is fully generic (whole-object
  diff via SerializeToNode), so no extra wiring was needed there.
- AiBrainModule: new BuildLocalBackend(s, endpoint, timeout) picks OllamaClient (native, gets the
  auto-start/warm-up/unload lifecycle via OllamaPath) or OpenAiCompatBackend(endpoint, "", timeout)
  (generic /v1, no key needed for a local server - those lifecycle calls are already no-ops on
  it) based on LocalBackendKind. Used at all three local-backend sites: TestConnectionAsync,
  CreateBrain's local-only path, and CreateBrain's fallback local leg.
- Pane: new "Local backend" dropdown (Ollama (native) | Generic OpenAI-compatible) in the Local
  provider group; relabeled the endpoint field (no longer "(Ollama base URL)"); renamed the
  autostart/preload group to "Local server (Ollama only)" since those stay Ollama-specific.

Verified: clean -Release (0 warnings); --aibrain-selftest 88 PASS/0 FAIL (extended the Provider
clamp assertion + two new checks: an old doc with no LocalBackendKind key defaults to "ollama",
and "openai-compat" round-trips through save/reload); --wpf-options/--module-host/--hardening/
--security/--fortunes exit 0; CoreTests 24; hardening ps1; resource-churn. Module-only.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-11  `0a523d501`

**Merge pull request #57 from bigfnj/docs/backlog-provider-done**

```
docs(backlog): mark #13 (AI provider redesign) done
```

### 2026-08-11  `526612fd6`

**Merge pull request #56 from bigfnj/feature/provider-fallback-backend**

```
feat(aibrain): cloud->local fallback backend — provider redesign PR B
```

### 2026-08-11  `ac24b7139`

**﻿docs(backlog): mark #13 (AI provider redesign) done**

```
Local + Cloud coexist, cloud-primary with local fallback (PRs #55/#56). Docs-only.
```

### 2026-08-11  `53d130b87`

**feat(aibrain): cloud->local fallback backend — PR B of BACKLOG #13**

```
Completes the provider redesign: when a cloud provider is primary and "use local as fallback"
is on, a retryable cloud failure fails over to the local Ollama model.

- New engine/FallbackBackend.cs (ICompanionBrainBackend composite): ChatAsync runs the cloud primary;
  on a RETRYABLE failure (timeout / transient HTTP 408-429-5xx / transport) it retries once on
  the local backend with the MAPPED local model (the cloud vision model maps to the local vision
  model, else the local text model); a DETERMINISTIC failure (non-transient 4xx/redirect, e.g. a
  bad key) rethrows without failing over. IsAvailable = either leg up; EnsureServer readies the
  local leg too; WarmUp/Unload/Dispose fan out.
- Shared classifier: extracted AiEndpointPolicy.IsRetryable(ex, ct) and refactored
  AiBrain.ChatWithRetryForDiagnosticsAsync's four catch-when clauses to use it, so the brain's
  own retry and the fallback classify failures identically (behavior unchanged — the HTTP-status
  self-tests confirm it).
- CreateBrain: cloud primary + UseLocalFallback + a valid loopback local endpoint -> wrap the
  cloud backend in FallbackBackend(cloud, localOllama, cloudVisionModel, localText, localVision);
  otherwise cloud-only (or local-only when no cloud). The brain still sees one backend.
- Self-test doubles TransientFailBackend + RecordingBackend + a CheckFallbackBackend probe:
  transient->local(text), vision->vision mapping, deterministic->surfaces (local untouched),
  and available-when-local-up.

Verified: clean -Release (0 warnings); --aibrain-selftest 86 PASS/0 FAIL (all prior security +
the 4 new fallback assertions); --wpf-options/--module-host/--hardening/--security/--fortunes
exit 0; CoreTests 24; hardening ps1. Module-only.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-11  `fd9d6db92`

**Merge pull request #55 from bigfnj/feature/provider-local-cloud**

```
feat(aibrain): Local + Cloud provider slots coexist (schema v2) — provider redesign PR A
```

### 2026-08-11  `92b50e95d`

**feat(aibrain): Local + Cloud provider slots coexist (schema v2) — PR A of BACKLOG #13**

```
Reworks the AI Brain from a single either/or provider into a fixed LOCAL slot + an optional
CLOUD slot (cloud is primary when selected). Settings + migration + pane + routing only; the
runtime cloud->local fallback backend is PR B.

- AiSettings (schema v1->v2): Provider is now the CLOUD selector {""|openai|openrouter|custom}
  ("" = local-only); the LOCAL slot is the fixed Endpoint/TextModel/VisionModel (Ollama). New
  CloudTextModel/CloudVisionModel + UseLocalFallback (default true). One-time v1->v2 migration
  in Normalize: an old cloud id keeps its slot + promotes the old models into the cloud slot
  (local models reset to defaults) with the credential scope hash unchanged (key stays valid);
  an old local id -> "". The credential machinery (ApiKeysEnc / BuildCredentialScope /
  TrySetApiKey / 32-scope cap) is mechanically unchanged. Future-schema (v99) docs are not
  migrated and stay write-blocked.
- Pane: split into "Local provider" (endpoint + local models + useVision), "Local server
  (Ollama)" (autostart/preload), "Cloud provider" (cloudProvider dropdown (none)/openai/
  openrouter/custom + cloud endpoint + API key + cloud models + consent), and "Fallback"
  (use-local-as-fallback). Load/Save round-trip both slots; the cloud key is set after the
  provider/endpoint so it targets the cloud scope.
- CreateBrain: Provider=="" -> local OllamaClient; else cloud OpenAiCompatBackend, using the
  active slot's models via a read-only ActiveSlotSnapshot. Exactly one backend (no composite).
- AiEngineProbe: +migration assertion (seeds a real DPAPI key, proves it resolves post-migrate)
  +cloud-slot round-trip; the one changed assertion tracks the new "" default (invariant kept).

Verified: clean -Release (0 warnings); --aibrain-selftest 82 PASS/0 FAIL (all security
assertions + the 2 new ones); --wpf-options/--module-host/--hardening/--security/--fortunes
exit 0; CoreTests 24; hardening ps1. Module-only.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-11  `e79b41df8`

**Merge pull request #54 from bigfnj/feature/triumph-persona**

```
feat(aibrain): add the Triumph insult-comic personality preset
```

### 2026-08-11  `a63d42160`

**﻿feat(aibrain): add the "Triumph" insult-comic personality preset**

```
BACKLOG #12. Adds a "Triumph" preset to AiBrainModule.PersonalityPresets, modeled on Triumph
the Insult Comic Dog: open with a mock-compliment, then savagely roast whatever's on screen and
the user, with the "for me to POOP on!" catchphrase.

It's a PERSONALITY (tone) so it stacks with the existing SPEECH styles via BuildSystemPrompt -
notably Triumph personality + "Samuel" speech = a relentlessly profane roast, the exact
combination requested. Opt-in (the default persona is unchanged); the system prompt already
backs a strong persona ("commit to it fully... never merely polite"), so no prompt change was
needed. One-line data addition.

Verified: clean -Release (0 warnings); --aibrain-selftest / --wpf-options / --module-host /
--hardening exit 0; CoreTests 24; hardening ps1. BACKLOG #12 marked done.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-11  `8667efa7c`

**Merge pull request #53 from bigfnj/cleanup/about-help-merge**

```
refactor(ui): fold Help into About + reorder the About window
```

### 2026-08-11  `17cbe8382`

**refactor(ui): fold Help into About + reorder the About window**

```
Per smoke-test feedback: one tray dialog, not two. The tray "Help" item + the WPF HelpWindow
are gone; the tray now has a single "About / Help" entry and the usage/help content lives in
the About window.

New About layout, top to bottom:
1. "AI Edition concept & build by BigFN'j" + the .NET 10 modernization line + a short project
   paragraph + the project link.
2. "Using DesktopPet" - the usage bullets + the allowlisted github doc links (folded in from
   the retired HelpWindow).
3. "Original / Legacy" - the upstream credits (Nomura/Petrucci/Grunwaldt + NAudio + eSheep),
   moved down from the top and relabeled.
4. "Information about the current pet" - the author/title/version/info card, now at the very
   bottom (was near the top).

- Deleted src/Portable/Wpf/HelpWindow.cs + its csproj Compile entry.
- OptionsShell.OpenHelp removed (OpenAbout now covers it).
- ContextMenus: dropped the Help tray item + Help_Click + the isHelpLoaded guard.
- AboutWindow widened to 560x640 to fit the merged content (scrolls).

Verified: clean -Release (0 warnings); --security (About-link policy) / --wpf-options /
--module-host / --hardening / --aibrain / --fortunes exit 0; CoreTests 24; hardening ps1;
zero HelpWindow references remain. Reinstalled + smoke-tested locally (user-confirmed).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `282f46dc8`

**Merge pull request #52 from bigfnj/docs/backlog-cleanup2**

```
docs(backlog): record the Newtonsoft drop + About/Help->WPF cleanup
```

### 2026-08-10  `43fb8d737`

**Merge pull request #51 from bigfnj/cleanup/about-help-wpf**

```
refactor(ui): move About + Help to the WPF shell; retire the WinForms dialogs (cleanup 2)
```

### 2026-08-10  `e32eff396`

**﻿docs(backlog): record the Newtonsoft drop + About/Help->WPF cleanup**

```
Maintenance entry for PRs #48/#49/#50/#51: Newtonsoft.Json migrated to in-box
System.Text.Json across the base + AiBrain module (product now Newtonsoft-free), and
the About/Help WinForms dialogs rebuilt as themed WPF windows (only the pet engine +
FormDebug remain WinForms). Notes the open WPF-rendering eyeball. Docs-only.
```

### 2026-08-10  `343336df3`

**refactor(ui): move About + Help to the WPF shell; retire the WinForms dialogs**

```
Final cleanup stream: the last two auxiliary WinForms dialogs (About, Help) become themed WPF
windows on the existing shell. Now the only WinForms left is the pet engine (FormCompanion/FormSpeech)
+ the dev-only FormDebug console (kept). WebView2 + the old FormOptions were already retired in
S5b-3.

- New src/Portable/WebLinks.cs: one security-reviewed link helper shared by the WPF windows +
  the security self-test. TryNormalizeHttpsLink (HTTPS + non-empty host + no-userinfo + <=2048,
  copied verbatim from AboutBox) + TryOpen (any HTTPS) + TryOpenProjectDoc (adds the
  github.com/bigfnj/desktopPet allowlist from FormHelp).
- New src/Portable/Wpf/AboutWindow.cs + HelpWindow.cs: programmatic WPF windows mirroring
  OptionsWindow (WpfTheme dark chrome, ScrollViewer, Close). About shows version + the current
  pet's author/title/version/info (with [br]/[link:] markup -> WPF inlines/Hyperlinks) + the
  fixed repo/esheep links; Help reproduces the offline text + the allowlisted doc links.
- OptionsShell.OpenAbout(author,title,version,info) / OpenHelp() mirror OptionsShell.Open().
- ContextMenus About_Click/Help_Click call the new entry points (re-entry guards + the
  author/title/version/info statics kept).
- Deleted AboutBox + FormHelp (.cs/.designer/.resx) + their csproj entries.
- SecuritySelfTest.CheckAboutLinkPolicy -> WebLinks.TryNormalizeHttpsLink (same assertions).
- resource-churn RunCycle: dropped the hidden AboutBox/FormHelp construction (+ the now-unused
  about/help cycle counters/keys); speech/pet/tray/menu churn intact.
- Doc cleanup: BACKLOG.md + handoff.md corrected (WebView2/FormOptions retired; About/Help now WPF).

Verified: clean -Release (0 warnings, base + 3 modules); --security-selftest PASS (relocated
About-link policy) + --wpf-options / --module-host / --hardening / --aibrain / --fortunes exit 0;
CoreTests 24 + hardening ps1 + resource-churn exit 0. NOTE: the WPF About/Help VISUAL rendering
was NOT eyeballed (headless) - needs a human to open tray -> About / Help.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `82e18eec4`

**Merge pull request #50 from bigfnj/cleanup/stj-aibrain-module**

```
refactor(json): migrate the AiBrain module off Newtonsoft (product Newtonsoft-free) (cleanup 1c)
```

### 2026-08-10  `143e5bd56`

**refactor(json): migrate the AiBrain module off Newtonsoft to System.Text.Json**

```
Completes the Newtonsoft->System.Text.Json drop: the base went in #48/#49; the AiBrain plugin
was the last Newtonsoft user (its own copy in its load context). Now the WHOLE product ships
zero Newtonsoft.Json.dll. Module-only; STJ is in-box on net10 (the module is LangVersion latest).

- engine/OllamaClient.cs + OpenAiCompatBackend.cs: JObject/JArray request payloads ->
  JsonObject/JsonArray + ToJsonString; response parse -> JsonNode + a module-local lenient
  JsonRead.Str (missing/wrong-kind -> fallback).
- engine/AiBrain.cs: model-reply JObject.Parse -> JsonNode.Parse; {text,emotion} read leniently.
- engine/ChatHistory.cs: [JsonIgnore] -> STJ; JsonConvert -> JsonSerializer (runtime-type
  overload so it doesn't bind object -> {}); on-disk envelope preserved.
- engine/AiSettings.cs (the DPAPI credential store): [JsonProperty] -> [JsonPropertyName](+Order);
  public fields -> IncludeFields; [JsonExtensionData] field -> Dictionary<string,JsonElement>
  property; the stale-writer merge engine ported JObject.FromObject -> JsonSerializer.SerializeToNode,
  JToken.DeepEquals -> JsonNode.DeepEquals (.NET 9), DeepClone-before-reparent, and the
  credential-scope merge mutates ApiKeysEnc in place when target already holds it (STJ throws on
  re-parenting an attached node). Default null handling kept.
- engine/AiEngineProbe.Security.cs: the DPAPI-ciphertext-injection probe JObject -> JsonNode.
- AiBrain.csproj: drop the Newtonsoft PackageReference.

Verified: clean -Release (0 warnings, base + 3 modules); ZERO Newtonsoft.Json.dll anywhere in the
Release tree; --aibrain-selftest RESULT=PASS 80/0 (DPAPI ciphertext preservation + credential-scope
merge + no-plaintext-key all pass); --module-host/--fortunes/--security/--wpf-options/--hardening +
CoreTests 24 + hardening ps1 green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `07305b5af`

**Merge pull request #49 from bigfnj/cleanup/stj-appsettings**

```
refactor(json): AppSettingsStore -> System.Text.Json + drop Newtonsoft from the base (cleanup 1b)
```

### 2026-08-10  `dab8f5a34`

**refactor(json): migrate AppSettingsStore to System.Text.Json + drop Newtonsoft from the base**

```
Cleanup step 1b: the last and hardest Newtonsoft consumer, then remove the package. STJ is
in-box on .NET 10, so the base now ships zero third-party JSON.

AppSettingsStore.cs (the versioned settings store, recompiled into CoreTests too):
- 22 doc fields + CompanionCountEntry/CompanionSizeEntry: [JsonProperty("x",Order=n)] ->
  [JsonPropertyName("x"), JsonPropertyOrder(n)]. Public FIELDS need IncludeFields=true (STJ
  ignores fields otherwise -> a silent empty write), set on a shared JsonSerializerOptions.
- [JsonExtensionData] IDictionary<string,JToken> field -> Dictionary<string,JsonElement>
  PROPERTY (STJ requires a property); Clone's JToken.DeepClone -> JsonElement.Clone. The
  future-schema unknown-field round-trip is preserved.
- Read: JsonTextReader{MaxDepth=32,DateParseHandling=None} + JsonSerializer.CreateDefault ->
  JsonSerializer.Deserialize(json, options{MaxDepth=32}). Write: JsonConvert.SerializeObject
  (Formatting.Indented) -> JsonSerializer.Serialize(options{WriteIndented,UnsafeRelaxedJson
  Escaping}). Default null handling kept, so the nullable absent-vs-null distinction
  (suppressRepeats/randomDrop*) is preserved. Output isn't byte-identical to Newtonsoft -> a
  one-time settings-file rewrite (nothing hashes the bytes). Kept C#7.3-clean for CoreTests.
- CoreTests harness (Program.cs) JObject/JArray on-disk verification -> JsonNode/JsonArray.

Dropped Newtonsoft everywhere: the base + CoreTests PackageReferences, the base license
Content, and the packaging manifests (runtime-files.txt / legal-files.json /
THIRD_PARTY_NOTICES.md); both packages.lock.json regenerated. (The AiBrain plugin keeps its
OWN Newtonsoft in its own load context - a separate follow-up stream will migrate the modules.)

Verified: clean -Release (0 warnings, base + 3 modules); build.ps1 payload-manifest check
confirms no Newtonsoft.Json.dll in the base output; CoreTests 24 groups freshly built
(defaults/migration/atomic-backup/corrupt-recovery/future-schema/nullable-absent all pass);
--module-host/--wpf-options/--hardening/--security/--aibrain/--catalog/--fortunes exit 0;
hardening ps1 + resource-churn exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `8ee9c4053`

**Merge pull request #48 from bigfnj/cleanup/stj-easy-files**

```
refactor(json): migrate 5 straightforward base files to System.Text.Json (cleanup 1a)
```

### 2026-08-10  `5fed2c1f1`

**refactor(json): migrate the 5 straightforward base files off Newtonsoft to System.Text.Json**

```
Cleanup step 1a of the Newtonsoft->System.Text.Json drop (STJ is in-box on .NET 10). The
gnarly AppSettingsStore + the package removal come in 1b; this migrates the five simple
consumers, so the base still builds with Newtonsoft present.

- New src/Portable/JsonRead.cs: lenient STJ readers (Str/IntOrNull/BoolOrNull) that mirror
  Newtonsoft's null-tolerant JToken casts - a missing/wrong-kind field yields the fallback
  instead of throwing, so one bad field never aborts a whole parse.
- RemoteCatalog.cs: JObject/JArray/JToken DOM + (string)/(int?) casts -> JsonNode/JsonArray +
  JsonRead; catch (Newtonsoft.Json.JsonException) -> System.Text.Json.JsonException; guard
  null array elements.
- PackCollections.cs: collections.json DOM parse -> JsonNode + JsonRead.
- LocalData.cs: the legacy ai-settings.json random-drop migration read -> JsonNode + JsonRead.
- Program.cs: the resource-churn result marker JObject -> JsonObject + ToJsonString(WriteIndented).
- CompanionHost.cs: JsonConvert.Serialize/Deserialize<Dictionary<string,string>> -> JsonSerializer.

Verified: clean -Release (0 warnings, base + 3 modules); --catalog-selftest catalog_parse=PASS
(RemoteCatalog Parse + reject-case JsonException path), --resource-churn (marker) exit 0,
--module-host/--wpf-options/--hardening/--security/--aibrain/--fortunes/--fortunes-engine/
--fullscreen exit 0, CoreTests 24 groups, hardening ps1 exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `8b3bb44f0`

**Merge pull request #47 from bigfnj/docs/backlog-s5c**

```
docs(backlog): record S5c base AI-cluster removal as done
```

### 2026-08-10  `9a8282dc9`

**docs(backlog): record S5c (base AI-cluster removal) as done**

```
Add a maintenance entry for the S5c "AiSettings split" stream (PRs #44/#45/#46):
relocate the AI security tests into the module, rehome random-drop into settings.json,
delete the ~6.8k-line dead base AI cluster. Notes Newtonsoft stays (STJ is a later pass).

Docs-only; no code/gate impact.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `bf4578766`

**Merge pull request #46 from bigfnj/stream2/s5c-delete-ai-cluster**

```
refactor(base): delete the dead AI-brain cluster (S5c ph3)
```

### 2026-08-10  `7efee171e`

**refactor(base): delete the dead AI-brain cluster (S5c ph3)**

```
S5c phase 3 (contract): remove the base's dead AI-brain code. It was fully duplicated by
the live modules/AiBrain plugin, its security tests were relocated into that module (ph1),
and its one non-AI setting (random-drop) moved to settings.json (ph2). Net ~6.8k lines
gone. Newtonsoft stays (6 non-AI base files still use it); STJ is a separate pass.

- Delete 12 files under src/dotNet/Ai/: AiBrain, AiSessionManager, AiEndpointPolicy,
  AiExecutablePolicy, AiProviders, OllamaClient, OpenAiCompatBackend, ICompanionBrainBackend,
  BrainResponse, ChatHistory, Personas, AiSettings (+ their csproj Compile entries).
  KEEP ActiveWindow/HotkeyListener (host services), PokeReactions (poke sass),
  FortunePackLoadPolicy (RemoteCatalog).
- StartUp: drop the dead retire machinery - aiSession/lifetimeCancellation/
  aiConfigurationVersion/aiConfig fields, the AI shutdown block (+ the now-unused
  ShutdownBudget field), all ApplyAiBrainState overloads, and the uncalled ClearAiHistory.
  InitAiTriggers -> InitDropTriggers (now only arms the drop timer + land greeting).
  ReloadAiSettings/RebuildSmartFortunes (ICompanionRuntime) keep their signatures, just resync
  the drop timer. Kept ApplyRandomDrop/ScheduleDrop/aiRand/RemainingShutdownBudget/
  GenerationAwareIdleSchedule.
- SecuritySelfTest: remove the 12 AI test methods + their Run() calls + the AI-only
  doubles (RetirementTracking/CancellationIgnoring/DeterministicFailure backends +
  FirstUnavailableThenBlocking handler) + AI-only helpers. KEEP every non-AI section, the
  shared HTTP-handler doubles (used by CheckSecureDownloadDeadline),
  CheckIdleScheduleGeneration, and CheckCrossSessionLock. The AI security coverage now
  lives in the module's --aibrain-selftest (ph1).

Base is now AI-cluster-free (no src/ references remain).

Verified: clean -Release (0 warnings, base + 3 modules); --security-selftest PASS (non-AI,
0 FAIL); --aibrain-selftest PASS; --module-host / --wpf-options / --hardening self-tests;
CoreTests 24 groups; hardening ps1; resource-churn soak - all green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `6406b758d`

**Merge pull request #45 from bigfnj/stream2/s5c-rehome-randomdrop**

```
refactor(settings): rehome random-drop cadence into settings.json (S5c ph2)
```

### 2026-08-10  `f09f09db8`

**refactor(settings): rehome random-drop cadence into settings.json (S5c ph2)**

```
S5c phase 2: the random-drop trio (RandomDropEnabled/Minutes/JitterMinutes) was the only
non-AI setting the base still read from the AiSettings blob. Move it to the base's own
store so removing the AI cluster (phase 3) doesn't touch it.

- AppSettingsDocument: 3 nullable fields (randomDropEnabled/Minutes/JitterMinutes,
  Order 20-22) modeled on suppressRepeats; CreateDefault = off/15/3; NormalizeRandomDrop
  clamps interval 1..9999 + jitter 0..center-1; wired into Clone + cross-process merge.
- LocalData: GetRandomDrop*/SetRandomDrop accessors + MigrateRandomDropIfAbsent - a
  one-time, self-contained bridge that seeds the fields from the legacy ai-settings.json
  when they're absent (null), else the defaults (no AiSettings dependency, survives ph3).
- StartUp.ApplyRandomDrop()/ScheduleDrop now read the cadence from Program.MyData
  (LocalData) instead of aiConfig; the 3 callers drop the AiSettings arg.
- OptionsShell Preferences pane reads/writes/resets random-drop via LocalData
  (SetRandomDrop); dropped the now-unused `using DesktopPet.Ai`.
- CoreTests: +"Settings random-drop validation" (defaults, round-trip, clamp,
  absent-keys-load-as-null) => 24 groups.

The base no longer reads AiSettings for random-drop (only the phase-3-doomed MemoryEnabled
read remains). Newtonsoft/STJ untouched (out of scope).

Verified: clean -Release (0 warnings); CoreTests 24 groups; --wpf-options / --module-host /
--hardening / --security / --aibrain self-tests + hardening ps1 all green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `e48a401ed`

**Merge pull request #44 from bigfnj/stream2/s5c-relocate-ai-tests**

```
test(aibrain): relocate the base AI security suite into the module probe (S5c ph1)
```

### 2026-08-10  `2250f66cf`

**test(aibrain): relocate the base AI security suite into the module probe (S5c ph1)**

```
S5c phase 1 (expand): the base's ~50 AI security assertions in SecuritySelfTest.cs
tested the DEAD base Ai/* copies; port them into the live module's --aibrain-selftest
so they exercise the SHIPPING engine (modules/AiBrain/engine/DesktopPet.Ai.*) before
the base cluster is deleted (phase 3). Base is untouched this phase.

- AiEngineProbe.Security.cs: RunSecurity + all 12 relocated check-methods (endpoint
  reject/SSRF, DPAPI-failure ciphertext preservation + no-plaintext-key + corrupt/
  future-schema resilience, credential scoping + scope-count bound, normalization/
  clamping + CRLF-injection reject, executable allow-list, response sanitize/bounds,
  read deadlines, HTTP-retry policy, session retire/dispose/after-retire races).
- AiSelfTestDoubles.cs: the module's own copies of the backend + HTTP-handler doubles.
- AiEngineProbe.cs: made partial; Run now also calls RunSecurity.

Ported ~verbatim against the module's parity impls + *ForSelfTest/*ForDiagnostics
hooks; no assertion weakened. CheckIdleScheduleGeneration stays in the base (it tests
StartUp.GenerationAwareIdleSchedule, not Ai/*).

Verified: clean -Release (0 warnings); --aibrain-selftest RESULT=PASS (80 PASS/0 FAIL,
relocated invariants present); base --security-selftest still PASS; hardening ps1 +
CoreTests (23 groups) green. git diff = modules/AiBrain only.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `9ab20637f`

**Merge pull request #43 from bigfnj/cleanup/backlog-record**

```
docs(backlog): record cleanup audit + queue Provider/OCR features
```

### 2026-08-10  `f8340a65d`

**﻿docs(backlog): record the post-conversion cleanup audit + queue Provider/OCR features**

```
- Add a "✅ DONE (2026-08-10) — Post-conversion cleanup audit" maintenance entry
  (PRs #39/#40/#41/#42) incl. the AI-cluster/Newtonsoft decision surfaced for later.
- Queue two unbuilt features carried from the S5b session: #13 AI provider redesign
  (Local + Cloud with fallback) and #14 bundle a portable OCR engine in the AiBrain
  module + engine picker.

Docs-only; no code/gate impact.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `40dccdfd6`

**Merge pull request #42 from bigfnj/cleanup/onnx-license**

```
fix(fortunes,legal): ship ONNX Runtime license + notices with the module
```

### 2026-08-10  `4621517e7`

**Merge pull request #41 from bigfnj/cleanup/bucket1b-contextmenus**

```
refactor(cleanup): collapse ContextMenus to PORTABLE-only + FormHelp guard (bucket 1b)
```

### 2026-08-10  `f16aed936`

**fix(fortunes,legal): ship ONNX Runtime license + notices with the module**

```
The Fortunes module redistributes the ONNX Runtime (native onnxruntime.dll + managed
Microsoft.ML.OnnxRuntime.dll) but shipped no copy of its MIT license or third-party
notices. The base csproj already claimed "ONNX runtime licenses now ship with the
Fortunes module", but nothing actually copied them.

Add GeneratePathProperty to the module's OnnxRuntime PackageReference and copy
LICENSE -> ONNXRUNTIME_LICENSE.txt and ThirdPartyNotices.txt ->
ONNXRUNTIME_THIRD_PARTY_NOTICES.txt from the restored NuGet package into
modules/fortunes/, beside the binaries they cover (same pattern the base already uses
for Newtonsoft). Version-pinned via the package, so the text never drifts from 1.28.0.

Verified: clean -Release (0 warnings); both files present in the module output
(1094 B + 331175 B, matching the 1.28.0 package); --module-host-selftest exit 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `7fa1141b0`

**refactor(cleanup): collapse ContextMenus to PORTABLE-only; add FormHelp re-entry guard**

```
Audit follow-up, bucket 1b (dead conditional-compilation + a missing dialog guard):
- Remove every #if !PORTABLE branch from ContextMenus.cs. The base has been
  PORTABLE-only since the .NET 10 port; the UWP branches (Launcher.LaunchUriAsync
  over xamlesheep:// URIs, the Windows.Storage LocalData ctor) were dead. Drops the
  four Windows.* UWP usings and the OpenOptionWindow shim entirely.
- Delete the first-boot auto-open of the options window: it called the PORTABLE
  OpenOptionWindow stub (a no-op), so it did nothing.
- FormHelp had no re-entry guard and used modeless Show() (never disposed). Match
  About/Options: an isHelpLoaded guard + using + ShowDialog(). Adds the isHelpLoaded
  field alongside isAboutLoaded/isOptionLoaded; About/Options/Help now mutually
  exclude each other.

Verified: clean -Release (base + Contracts + Fortunes + AiBrain, 0 warnings);
--module-host-selftest / --wpf-options-selftest / --hardening-selftest / the
runtime-hardening source-invariant script / CoreTests (23 groups) all green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `ed9add4f0`

**Merge pull request #40 from bigfnj/cleanup/bucket2-residual-strip**

```
refactor(cleanup): strip residual base AI-brain cluster + fortune engine + OptionsController seam
```

### 2026-08-10  `16a9187d6`

**test(hardening): drop stale AI-capture source invariant (base AI capture path removed)**

### 2026-08-10  `b42f67c10`

**refactor(cleanup): strip residual base AI-brain cluster + fortune engine + OptionsController seam**

```
Pure-deletion cleanup of dead residue left over from the plugin-host migration
(modules now own the brain and fortunes). Build stays green with 0 warnings.

GROUP 1 - base AI-brain build/trigger residue (StartUp.cs):
- Removed the dead brain-BUILD + trigger members: CreateBrain, SelectedEndpoint,
  CanUseAiConfiguration, Observe, AskAboutScreen/AskAboutScreenAsync,
  EmoteAll/EmotionAnimations, ApplyAiTriggers, ScheduleIdle, IdleTimer_Tick,
  the public SetAiBrainEnabled + AiBrainEnabled property (0 external callers),
  and the now-dead fields aiHotkey, aiIdleTimer(+handler), aiLastInteractionUtc,
  idleSchedule (and their Dispose cleanup).
- KEPT ApplyAiBrainState's RETIRE behavior: it still calls
  aiSession.ReconfigureAsync(null, false, false, ...) so any prior brain is torn
  down and history is cleared on request. Simplified the dead `allowed`/`prepare`/
  CreateBrain factory away. PlayAnimationOnAll stays (CompanionHost service).

GROUP 1 - brain FILES DELIBERATELY KEPT (surprise live consumer; see report):
- AiBrain.cs, BrainResponse.cs, ICompanionBrainBackend.cs, AiExecutablePolicy.cs,
  OllamaClient.cs, OpenAiCompatBackend.cs were NOT deleted. The KEPT
  AiSessionManager embeds AiBrain (Func<AiBrain> factory, AiBrain _brain,
  RetireBrainAsync(AiBrain)) and returns BrainResponse; AiBrain in turn requires
  ICompanionBrainBackend/BrainResponse/AiExecutablePolicy (OCR tesseract resolution).
  These types are also exercised by kept SecuritySelfTest sections and linked by
  Tools/PetTester. Deleting them would break the KEPT AiSessionManager, so per the
  stop-and-report guidance they stay. SecuritySelfTest.cs was left UNMODIFIED (all
  its AI tests target kept classes and keep passing).

GROUP 2 - base fortune engine (residual; module owns fortunes):
- Deleted FortuneFileImporter.cs (1782 lines; only consumer was the deleted
  FortuneProvider.FilterSelfTest).
- Deleted the base FortuneProvider engine + FortuneEntry/SourceStat/GenreStat/
  FortuneTaxonomy/FortuneClassifier and its self-tests (FilterSelfTest,
  CustomCacheSelfTest) by reducing FortuneProvider.cs to only the one live type:
  renamed to FortunePackLoadPolicy.cs (RemoteCatalog.cs consumes
  FortunePackLoadPolicy.TryValidatePackMetadata / MaximumFileBytes for catalog
  pack bounds). RemoteCatalog/PackCollections do NOT reference base FortuneProvider.
- csproj: swapped the FortuneProvider.cs Compile entry for FortunePackLoadPolicy.cs,
  removed FortuneFileImporter.cs, and removed the now-orphaned embedded resources
  Fortunes\fortunes.txt and the classifier-parity TSV (DesktopPet.ClassifierParity.tsv).
- Program.cs + build.yml: removed --filter-selftest and --fortunecache-selftest
  (their handlers FortuneProvider.FilterSelfTest/CustomCacheSelfTest are gone).

GROUP 3 - OptionsController seam (self-test-only except CompanionsController):
- OptionsController.cs: deleted the OptionsController facade, PreferencesController
  (+ PreferencesState), FortunesController (+ SourceStatus/SourceRow/GenreRow/
  FortunesState), and OptionsSelfTest. KEPT the live CompanionsController (used by
  Portable/Wpf/CompanionsPaneControl.cs) plus its deps: ICompanionRuntime, ICatalogService,
  OpResult/OpResult<T>, CompanionRow, CompanionsState.
- Program.cs + build.yml: removed --options-selftest (+ its orphaned
  DESKTOPPET_DATA_ROOT setup) flag and handler.

KEPT (still live): AiSettings/AiModelPolicy, Personas, AiProviders, AiEndpointPolicy
(+ AiBackendHttpException), ChatHistory, AiSessionManager, ActiveWindow, HotkeyListener,
FortunePackLoadPolicy, CompanionsController, all of modules/**.

Gate: build.ps1 -Release -> 0 warnings/0 errors (base + Contracts + Fortunes + AiBrain
+ TestModule); --security-selftest, --wpf-options-selftest, --module-host-selftest,
--fortunes-engine-selftest, --hardening-selftest, --pettyperegistry-selftest,
--catalog-selftest all exit 0; CoreTests PASS: 23 core regression groups.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `5a0c7f179`

**Merge pull request #39 from bigfnj/cleanup/bucket1-deadcontrols-leaks**

```
refactor(cleanup): del dead FormOptions controls + legacy tree; fix 2 CTS disposals
```

### 2026-08-10  `72fc3aa4e`

**refactor(cleanup): delete dead FormOptions controls + legacy tree; fix two CTS disposals**

```
Audit follow-up, bucket 1 (safe deletions + resource fixes):
- Delete DarkTabControl + DarkNumericUpDown — 0 code consumers (they were FormOptions-only
  custom controls; FormOptions was retired). Drop their csproj Compile entries.
- Delete src/legacy/ — the old net48/UWP monolith tree; not in any build (build.ps1 builds
  only the portable csproj + the 3 module csprojs; no .sln, no CI ref).
- Dispose CancellationTokenSources that were only cancelled: AiBrainModule._lifetime
  (Shutdown now Cancel()+Dispose()) and CompanionsPaneControl._netCts (Unloaded now
  Cancel()+Dispose()+null).

Verified: clean -Release (base + Contracts + Fortunes + AiBrain); --wpf-options-selftest /
--module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `89f22d76d`

**Merge pull request #38 from bigfnj/docs/backlog-triumph-persona**

```
docs(backlog): Triumph insult-comic persona + persona x speech combinations
```

### 2026-08-10  `b5a72987b`

**Merge pull request #37 from bigfnj/stream2/s5b3-retire-formoptions**

```
refactor(s5b-3): retire FormOptions dialog + WebView2 layer
```

### 2026-08-10  `3d3675251`

**docs(backlog): Triumph insult-comic persona + persona x speech combinations**

```
Queue a Triumph personality (Triumph the Insult Comic Dog) and capture the emergent
personality x speech-pattern combination idea (e.g. Triumph personality + Samuel speech
= a profane insult act that only roasts the user). Build sites noted (AiBrainModule presets
+ Personas speech patterns).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `742b0ffeb`

**refactor(s5b-3): retire FormOptions dialog + WebView2 layer**

```
Settings is now WPF-only (DesktopPet.Wpf.OptionsShell). This removes the
legacy WinForms options dialog and the WebView2 rendering layer it carried,
along with the residual base helpers and self-tests that only existed to
support them.

Deletions:
- src/Portable/FormOptions.cs (+ .designer.cs, .resx): the classic WinForms
  options dialog, now fully replaced by the WPF settings shell.
- src/Portable/Options/FortunesWebView.cs (FortunesWebView +
  FortunesWebViewSelfTest) and src/dotNet/WebViewHost.cs (WebViewHost +
  WebViewSelfTest): the WebView2 host/control-center layer.
- src/Fortunes/fortunes-view.html: the embedded WebView2 page.
- src/dotNet/TrustedPack.cs: an orphaned model only the deleted FormOptions
  fortune-pack install path populated (its fields became never-assigned).
- Microsoft.Web.WebView2 PackageReference + all its build/packaging wiring
  (csproj Compile/EmbeddedResource/Content items, runtime-files.txt DLL +
  license lines, legal-files.json entries, THIRD_PARTY_NOTICES.md row, and
  the regenerated src/packages.lock.json).

Residual static helpers/self-tests removed with their owners:
- FortuneProvider.FilterSelfTest: the three FormOptions.Run*SelfTest calls.
- SecuritySelfTest: the FormOptions.QuoteWindowsProcessArgument assertion,
  the FormOptions.FetchModelNamesAsync deadline check, and the whole
  CheckTestModelCleanup test (+ its now-unused TestModelBehavior enum and
  TestModelBackend backend). Every other SecuritySelfTest check is intact.
- Program.cs (prep): OpenOptionDialog + the --webview-selftest /
  --fortunes-webview-selftest dispatches + the FormOptions resource-churn
  self-test block and its counters.
- CI (build.yml) and tests/runtime-hardening-selftest.ps1: dropped the
  --webview-selftest / --fortunes-webview-selftest runs and the FormOptions.cs
  source-text invariant.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `461fa3559`

**Merge pull request #36 from bigfnj/stream2/s5-suppress-repeats-default**

```
fix(host): repeat guard actually defaults ON (nullable SuppressRepeats)
```

### 2026-08-10  `94ebb3bcf`

**fix(host): make the repeat guard actually default ON (nullable SuppressRepeats)**

```
The "don't repeat the same message" guard was silently disabled: settings written before
the field existed have no "suppressRepeats" key, and the plain-bool + DefaultValueHandling
.Populate default didn't apply on load, so GetSuppressRepeats() returned false and the host
dedupe never ran — the same AI quip kept repeating.

Make SuppressRepeats a bool? (nullable): absent/null is distinct from an explicit false,
and GetSuppressRepeats() returns `SuppressRepeats ?? true`, so the guard is ON by default
for any existing doc without a settings edit. No reliance on Newtonsoft default-population.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green;
dev install refreshed (existing settings now dedupe without any change to the file).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `dd6559b3b`

**Merge pull request #35 from bigfnj/stream2/s5-owner-name**

```
fix(welcome): greet by the configured name, not the Windows username
```

### 2026-08-10  `c1efc1d13`

**fix(welcome): greet by the configured name, not the Windows username**

```
The Fortunes out-of-box welcome greeted with Environment.UserName ("Admin"), ignoring
the name set in the AI persona — because the two modules are ALC-isolated and can't read
each other's settings. Add a small host-mediated shared "owner name" so the AI name wins
when the brain is on, matching the user's request.

- ABI (additive): IHost.OwnerName (get) + IHost.SetOwnerName(name). "" = none set.
- CompanionHost holds it in-memory (trimmed, capped at 64 chars); "" by default.
- AiBrain module publishes it in ApplyState: the user's name when the brain is enabled and
  a name is set, else "" (clears it) — so toggling AI on/off updates it live.
- Fortunes welcome greets with host.OwnerName when set, else falls back to the Windows
  user name (out-of-box behaviour preserved when the brain is off).
- All IHost stubs (CompanionHost + 4 self-test recording hosts) implement the new members.

Timing: modules Init (AiBrain publishes) before the first pet spawn (Fortunes welcome
reads), so the very first greeting already uses the configured name.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest /
--fortunes-engine-selftest / CoreTests green; dev install refreshed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `ca3ebd382`

**Merge pull request #34 from bigfnj/stream2/s5-ai-personality-presets**

```
feat(ai): canned Personality presets (dropdown) instead of free text
```

### 2026-08-10  `9ae675324`

**Merge pull request #32 from bigfnj/stream2/s5-fortunes-pane-1**

```
feat(fortunes): Fortunes settings pane, part 1 (selection + content toggles)
```

### 2026-08-10  `9ed2585eb`

**fix(ai): stop repeated/screen-blind remarks (dedupe cue + grounding + variety)**

```
Three fixes after live testing showed the same quip 4x in a row and ignoring the screen:

- Host dedupe missed repeats because every AI turn shows a "…" thinking cue between
  remarks, so no two identical strings were ever adjacent. StartUp.SayAll now only
  tracks/compares lines with real content (a letter or digit), so a content-free cue
  like "…" doesn't reset the guard — quip / … / quip is now caught.
- Screen grounding: the prompt now tells the model to remark on something SPECIFIC it
  sees (name a program/file/word/detail), not a generic line.
- Variety at the source: a firm "do not repeat anything you've said recently; make every
  remark new and different" instruction (counters memory feeding a repeated line back),
  plus temperature 0.9 on the Ollama request so short remarks don't converge on one line.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `d72d98d62`

**feat(ai,ui): "Test OCR" button with green/red status (no more silent OCR failure)**

```
Screen-aware AI remarks depend on OCR (text mode) reading the screen, but if tesseract
isn't found the model just gets "(no readable text)" and riffs on the persona with no
relevance to the screen — and nothing told you OCR was broken.

- AiBrain.SelfTestOcrAsync: resolve the tesseract engine, then OCR a tiny generated image
  of known text ("OCR works") and check it read back. Returns a "✓ …" (found + actually
  reads) or "✗ …" (missing / found-but-no-text / errored) status. Safe on a throwaway
  AiBrain (OCR never touches the backend; Dispose is null-safe).
- AiBrain module: "Test OCR" action in the Provider group.
- PaneView action rows now colour a ✓ result green and a ✗ result red (also lights up the
  existing "Test connection"), so pass/fail is obvious at a glance.

Note: this is the diagnosis half of the OCR story. Provisioning (a path picker, or bundling
tesseract with the installer) is a follow-up — runtime auto-download of a native binary is
deliberately avoided for the signed/consented module model.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `3e939d272`

**feat(host): master "don't repeat the same message twice in a row" preference**

```
Back-to-back identical remarks (seen with the AI brain) shouldn't happen for any
speaker. Enforce it in the host's SayAll — the single choke point every module
broadcasts through (AI brain, fortunes, welcome) — gated by a core Preferences toggle.

- AppSettingsDocument.SuppressRepeats (Order 19, default ON; DefaultValue+Populate so a
  doc written before this field loads as ON, not the bool default). LocalData Get/Set.
- StartUp.SayAll: track the last broadcast line; when the incoming line is identical
  (trimmed, case-insensitive) and the pref is on, skip it. Guard is last-1 only, so it
  never outruns a small quip pool — with 2+ quips you just cycle, never go silent.
- Preferences pane: "Don't repeat the same message twice in a row" (Speech group);
  included in Reset-to-default-settings.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `792165139`

**feat(ai,ui): stronger personas + Apply-on-change button + force configured name**

```
Three related changes from live testing of the AI persona:

1. Apply button dirty-tracking (OptionsWindow): the schema-pane Apply now starts
   greyed out and only enables once a field actually changes (and greys out again
   after a successful Apply). Wired via ShellPane.NotifyDirty + PaneView (each
   Bool/Int/Text/Enum/Secret control marks the pane dirty; initial values set during
   Build are suppressed so they don't count as edits). Fixes the "changed a dropdown,
   forgot it needs Apply" confusion.

2. Personas lean harder + are directives, not asks: every personality preset blurb
   (AiBrainModule) and every speech-pattern instruction (Personas) rewritten as firm,
   specific commands ("Every remark must rhyme.", "swear hard with real, unfiltered
   profanity in every remark", etc.). The system prompt now tells the model to commit
   fully and be "vivid and specific, never bland, generic or merely polite".

3. Force the configured name / stop reading "Admin" off screen: BuildSystemPrompt now
   says "Always address them as <UserName>; never use any other name, username or
   handle you see on the screen" (and, when no name is set, forbids inventing one).
   Window titles / paths like C:\Users\Admin were leaking in as the user's name.

Note: hard profanity is still model-gated — safety-tuned models sanitize it. (Wiring
the running pet to an uncensored local model is a runtime settings change, not code.)

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `ed59f0c7b`

**feat(ai): add "Samuel" personality + speech pattern (Samuel L. Jackson, full send)**

```
A user-requested persona: adds a "Samuel" entry to both the Personality dropdown
(AiBrainModule presets) and the Speech style dropdown (Personas.SpeechPatterns),
modeled on Samuel L. Jackson — intense, blunt, commanding swagger with emphatic,
unfiltered profanity. Opt-in (the default persona stays clean); actual profanity in
generated remarks is model-dependent (some local models soften it).

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `11f0ac8ae`

**feat(ai): canned Personality presets (dropdown) instead of free text**

```
The AI Brain persona's "Personality" was a free-text field, which a user could phrase
in a way that doesn't slot cleanly into the system prompt ("Your personality: <text>.")
or read naturally. Replace it with a dropdown of 12 curated presets (same label<->value
pattern as Speech style): the dropdown shows a short label, the stored value is the full
blurb that goes into the prompt.

Presets: Friendly & upbeat, Dry & sarcastic, Cheerful & bubbly, Calm & zen, Sassy & bold,
Shy & sweet, Grumpy but lovable, Curious & nerdy, Wise mentor, Chaotic & goofy, Cool &
aloof, Motivational coach.

- personality SettingField: Text -> Enum (Options = preset labels).
- Load maps the stored blurb -> its label; Save maps the picked label -> its blurb.
- First preset's blurb == the AiSettings default, so a fresh install round-trips; an older
  free-text value that matches no preset falls back to the first preset (user re-picks).

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests green;
dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `7b5ba8955`

**feat(fortunes): Fortunes pane part 2 — Sources + Genres list cards (generic list-card ABI)**

```
Adds the rich pack/genre management the old FormOptions "Fortunes" tab had, on a new
declarative list-card primitive so the ABI stays framework-agnostic.

ABI (additive): ListItem { Id, Label, Detail, Checked } + ListCard { Title, LoadItems,
SetChecked, Actions, EmptyHint } + OptionsPane.Lists. A module supplies data + delegates;
the host renders the WPF, so a checkable dynamic list a flat schema can't express (fortune
packs, genres) now has a home. Reusable by any module.

Host renderer (PaneView): each ListCard renders as a titled card (shared card chrome via
NewCard) with a height-capped, scrollable checkbox list (label + detail/count) that toggles
live through SetChecked, plus card-level PaneAction buttons. Flows into the same masonry
columns as the schema cards. IsChecked is set before wiring events so building never fires a
spurious toggle.

Fortunes module: two list cards driving the LIVE engine —
- "Fortune packs" (sources): each installed .txt pack with its line count (· spicy when it
  has edgy/nsfw lines); unchecking disables it (persisted disabledSources). Buttons: Open
  fortunes folder (Explorer) + Rescan folder (rebuild + refresh the card).
- "Genres": each delivery genre with its count; unchecking disables it (disabledGenres).
Disabled lists persist to host.GetSettings("fortunes") (newline-joined) and are read back in
LoadFortuneSettings; every toggle rebuilds the engine so it applies to the running pet at once.

WPF self-test now builds a probe ListCard (exercises BuildListCard headlessly).

Deferred (next): a validated file-import picker ("Add fortunes…") and the online catalog
packs — import needs a host file-pick decision; catalog ties into S7.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest /
--fortunes-engine-selftest / CoreTests green; dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `9fa81b550`

**feat(fortunes): Fortunes settings pane, part 1 (selection + content toggles)**

```
The Fortunes module contributed no UI: its settings (smart / spicy / tier /
spicyOnly / noProfanity) were only ever read once at Init, and the old FormOptions
"Fortunes" tab edited the BASE engine's AiSettings — a copy the running pet doesn't
use. So changing fortune settings in the old UI never affected the pet.

Give the module its own schema-driven OptionsPane (rendered by the WPF grouped-card
shell), so the settings edit the LIVE module:

- Grouped fields: Selection (Smart, context-aware picks) / Content level (Enable
  spicy, Spice level [Edgy+NSFW | True NSFW only], Skip the tame ones, Remove
  profanity). Enum tier maps friendly labels <-> stored "edgy"/"nsfw".
- Load/Save round-trip through host.GetSettings("fortunes"); Save persists then
  calls RebuildEngine() so the change (and any pack added to the folder) takes
  effect on the running pet immediately — no restart.
- "Rebuild smart index" action reloads packs + re-warms the semantic index and
  reports status via SmartFortunes.WarmProgress (real module status, replacing the
  base's stubbed placeholder).

Init's engine build is refactored into RebuildEngine() (shared by Init + Save).
This is part 1; the richer Sources / Genres / Packs list (import, per-source enable,
open-folder) is the next increment — it needs a declarative list-card primitive.

Verified: clean -Release (base + Contracts + Fortunes + AiBrain); --wpf-options-selftest
/ --module-host-selftest / --fortunes-engine-selftest / CoreTests green; dev install
eyeballed (Fortunes pane renders, toggles save + drive the live engine).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `38cd44b56`

**Merge pull request #31 from bigfnj/stream2/s5-prefs-cleanup**

```
feat(ui): drop Size field; Restore-default-pet -> Reset-to-default-settings
```

### 2026-08-10  `883310c55`

**feat(ui): drop redundant Size field; "Restore default pet" -> "Reset to default settings"**

```
Two Preferences-pane changes now that per-pet size lives in the Pets module.

1. Drop the "Size (1-3)" field. Per-pet size is set on each pet card in the Pets
   pane; the global scale stays only as the internal fallback for pets without an
   override (GetEffectivePetScaleFactor / CompanionsPaneControl), so it's no longer a
   Preferences field.

2. Replace the "Restore default pet" button with "Reset to default settings". It
   restores the preferences shown on this page — startup/window behavior, volume,
   audio device, speech, and fortune-drop — to their defaults behind a Yes/No
   confirmation, then persists. Scoped on purpose: the loaded pet (XML/images),
   per-pet sizes/mutes, and the AI Brain module's own settings are left untouched.
   Run-at-startup (registry) resets to off; the reset output device applies to the
   running pet immediately.

Supporting (additive, reusable): PaneAction.ReloadPaneAfter + ShellPane.RequestReload
so an action can ask the host to rebuild its pane afterward — the reset uses it so
the fields visibly snap to their defaults. The delegate may set ReloadPaneAfter from
inside InvokeAsync (the host reads it post-await), so it declines the reload on cancel.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest / CoreTests
green; dev install eyeballed (reset confirm + live refresh, Size field gone).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `60715dc6b`

**Merge pull request #30 from bigfnj/stream2/s5-speech-bubble-repaint**

```
fix(speech): repaint bubble when the tail moves (no stale streaks / ghost notch)
```

### 2026-08-10  `140106910`

**Merge pull request #29 from bigfnj/stream2/s5-version-stamp**

```
feat(ui): version stamp in the settings window (bottom-left)
```

### 2026-08-10  `6b018efcd`

**fix(speech): repaint the bubble when the tail moves (no stale streaks / ghost notch)**

```
FormCompanion calls FormSpeech.Reposition every tick so the bubble follows the pet.
As the pet walks, the tail slides along the bubble edge (and flips top/bottom)
without the bubble changing size. Reposition updated the window bounds and the
clip Region (the new tail shape) but never invalidated, and a same-size window
move just blits the old pixels — so the painted outline kept the OLD tail while
the Region already clipped to the NEW one. Result: stale black lines across the
moved tail and a leftover notch in the border where the tail used to be.

Add Invalidate() after SetBounds/UpdateRegion in Reposition so OnPaint redraws
the outline to match the new Region. It sits below the existing no-op guard, so
an idle (unmoved) bubble still never repaints.

Verified: clean -Release; --wpf-options-selftest / --module-host-selftest /
CoreTests (incl. "Unicode speech and logical sprite anchoring") green; dev
install eyeballed (walk + fall while a bubble is up — tail tracks cleanly).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `3284eba1d`

**Merge pull request #28 from bigfnj/stream2/s5-settings-masonry**

```
fix(ui): settings-window polish (masonry packing + readable dark dropdowns)
```

### 2026-08-10  `56fdf67a7`

**feat(ui): version stamp in the settings window (bottom-left)**

```
Show "v<ProductVersion>" in the bottom-left of the WPF settings window so
"which build am I running?" is answerable at a glance — restoring the stamp the
old FormOptions dialog had. The bottom bar is now a DockPanel: version (muted
grey, reads as a hint in both light and dark) docked left, Apply/Close docked
right. Value comes from Application.ProductVersion (ProductVersion.props via the
build), never hardcoded, so it always reflects the actual build (currently v1.2.0).

Verified: clean -Release; --wpf-options-selftest green; dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `c55bb907e`

**fix(ui): readable dark ComboBox dropdown**

```
In dark mode the closed combo was themed, but the open dropdown popup used the
stock ComboBox template, whose popup is painted from SystemColors (a light popup
with faint text) regardless of the Background/Foreground set on the control, so
the items (provider list, speech style, audio device) were nearly unreadable.

Give ComboBox a full dark template in WpfTheme: a dark closed box + a dark popup
border, plus a ComboBoxItem style with near-white text and a blue hover/select
highlight. Parsed as a ResourceDictionary so the ComboBox + ComboBoxItem styles
register as implicit (keyed by type) and reach the items inside the popup.

Verified: clean -Release; --wpf-options-selftest (builds the window + applies the
dark theme, so the combo template XAML is parse-checked) / --module-host-selftest
/ CoreTests green; dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `61acedfaa`

**fix(ui): masonry packing for settings cards (no more tall empty boxes)**

```
Grouped setting cards were laid out row-by-row (WrapPanel), so a short card
(e.g. AI brain's single toggle) sitting next to a tall one (Persona) stretched
into a big empty box, and a lone small card (Local server) was stranded on its
own row. Replace the row-wrap with a small masonry panel: cards flow into a
responsive number of equal-width columns and each card drops into the currently
-shortest column, so cards of differing heights pack and the columns stay level
(the two small AI-brain/Local-server cards now stack together). Column count is
derived from the available width, so it reflows as the window is resized.

- MasonryPanel : Panel (Measure/Arrange place each child in the shortest column;
  column count = availableWidth / column pitch, min 1).
- PaneView uses MasonryPanel instead of WrapPanel; card width/margins unchanged,
  so the grouped-card look + Save round-trip (keyed by field Id) are unchanged.

Verified: clean -Release (base + Contracts + Fortunes + AiBrain); --wpf-options
-selftest / --module-host-selftest / CoreTests green; dev install eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `4845ca232`

**Merge pull request #27 from bigfnj/stream2/s5-grouped-settings**

```
S5: grouped-card settings layout (settings re-eval foundation)
```

### 2026-08-10  `8c502ebf6`

**feat(ui): grouped-card settings layout (settings re-eval foundation)**

```
Settings panes now render as titled cards that flow into responsive columns (2-3 across
in the wide window) instead of one skinny column. This is the model Fortunes + future
modules build on: a module declares grouped settings, the shell renders titled cards, and
a rich section (e.g. a fortune-pack list) drops in later as one custom card.

- ABI: optional SettingField.Group + PaneAction.Group (additive; null/"" => one default
  card). Fields/actions sharing a Group name render in one titled card.
- PaneView: buckets fields + actions by Group (first-appearance order) into titled Border
  cards laid out in a WrapPanel (responsive columns); narrower wrapping label column; the
  Save/Collect path is keyed by field Id and unchanged.
- Preferences: grouped into Startup & window / Sound (+ Test sound) / Speech / Fortune
  drop; Restore-default-pet in a default card.
- AI Brain module: grouped into AI brain / Persona (+ Clear history) / Provider (+ Test
  connection) / Triggers / Local server (Ollama).

Verified: clean -Release (base + 3 modules); --wpf-options-selftest (grouped render + Save
round-trip) / --module-host-selftest / CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-10  `4cf284f72`

**Merge pull request #26 from bigfnj/stream2/fix-active-pet-keying**

```
fix(pets): key active pet size/sound by its real id
```

### 2026-08-10  `4c762bff3`

**fix(pets): key the active pet's size/sound by its real id (not "")**

```
Per-pet size + sound key by the specific pet id, so they worked on pets added alongside
(extras) but NOT on the active/default pet, which staged with the "" active-slot
placeholder. Now the active pet is keyed by its real id, so its card toggles apply.

- New activePetId setting (default the built-in "eSheep"; normalized to a real pet id,
  empty/unsafe -> built-in) in AppSettingsDocument; LocalData Get/SetActivePetId.
- StartUp keys the active-pet staging (Init + LoadNewXMLFromString) by GetActivePetId()
  for both the scale factor and Animations.PetTypeId, instead of "".
- The pick-a-pet paths persist it first: CompanionsController.UsePet + RestoreDefaultPet and
  FormOptions.ApplyPet call SetActivePetId before LoadNewXMLFromString. Raw-XML drops /
  the restore-on-reload path keep the current active id.
- The on-screen pet MIX still keys the active type as "" (that's spawn counts, separate
  from per-pet settings) - unchanged.

Verified: CoreTests (+ active-pet id normalization); clean -Release (base + 3 modules);
--module-host / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `5bf499c6f`

**Merge pull request #25 from bigfnj/stream2/b4-retire-sound-module**

```
B4: retire the inert S2 Sound module
```

### 2026-08-07  `7725dd103`

**docs: refresh handoff + BACKLOG for the S5/audio arc (session wrap)**

```
- handoff.md: S4 MERGED; add S5 (WPF shell + Pets features) + the B audio arc
  (host-owned DirectSound output, device picker, per-pet size/sound, Sound module
  retired, NAudio 3, WASAPI-rejected) as DONE; NEXT = S5b-2(d)/S5b-3/S5c-e; open
  follow-ups; fixed the stale self-test flag list (--sound/--smart removed).
- BACKLOG.md: current-major-work status updated (S4 merged; S5+audio arc done; next).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `fb974957e`

**chore(audio): B4 - retire the inert S2 Sound module**

```
The base owns audio playback since B1 (AudioOutput), so the S2 Sound module never
receives AnimationStarted and is dead weight. Removed it and its bundled NAudio 2.3.0.

- Deleted modules/Sound (SoundModule.cs, Sound.csproj, its BACKLOG) + the base
  --sound-selftest (SoundModuleSelfTest.cs, the Program.cs dispatch, the build.yml flag,
  the csproj compile item, the build.ps1 module-list entry + comment).
- THIRD_PARTY_NOTICES: the base now ships only NAudio 3.0.0-preview.6 (Core/Dmo/Midi/
  WinMM); dropped the module's NAudio 2.3.0 rows (Asio/Wasapi/WinForms/meta).
- The AnimationStarted ABI event + AnimationInfo stay (a legit lifecycle event for future
  modules; no live consumer today).
- Backlogged the TTS/speech module as its own future module (calendar/appointments "speak"
  on the shared output) per the user's direction.

Verified: clean -Release (base + 3 modules; payload set-equality OK); CoreTests;
--module-host-selftest (3 modules) / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `b2902bbf4`

**Merge pull request #24 from bigfnj/stream2/b23-per-pet-sound**

```
B2/B3: per-pet sound toggle
```

### 2026-08-07  `405704db2`

**feat(audio): B2/B3 - per-pet sound toggle**

```
Adds an inline "sound on / sound off" toggle to each Pets card (only for pets that have
sounds), so a pet type's animation sounds can be muted independently - e.g. mute Pingus
while the sheep keep chattering.

- B2 (identity): Animations.SoundSink now carries the pet TYPE id (petTypeId, animId,
  data, loop). Each staged Animations is tagged with its id at stage time ("" = the
  active/default pet, folder id for extras).
- B3 (toggle): StartUp's sink gates playback on a per-pet mute checked at PLAY time, so
  toggling takes effect on the next sound with no restage. New mutedPets list in
  AppSettingsDocument (ids with sound off; absent = on), normalized/cloned/merged;
  LocalData IsPetSoundEnabled / SetPetSoundEnabled; StartUp.SetPetSound; an inline
  clickable toggle in CompanionsPaneControl matching the size-number style.

Per-TYPE (like per-pet size): keyed by the specific pet id, so it works on extras
wherever they're on screen; the active/default pet is keyed "" (shared follow-up: key
the active pet by its real id so its card toggle applies while it's the active pet).

Verified: CoreTests (+ muted-pets validation); clean -Release (base + 4 modules);
--sound / --module-host / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `fb3eed264`

**Merge pull request #23 from bigfnj/stream2/b15-audio-device-picker**

```
B1.5: audio output device chooser + Test-sound button (DirectSound)
```

### 2026-08-07  `255aeed94`

**feat(audio): B1.5 - output device chooser + Test-sound button (DirectSound)**

```
Adds a "Sound output device" dropdown and a "Test sound" button to the Preferences
pane so pet sounds (and later TTS) can be routed to a chosen playback device.

Output moves from WaveOut to DirectSound (NAudio.Dmo): DirectSoundOut enumerates
devices with full friendly names + GUIDs and plays through a selected device. WASAPI
was rejected - its package requires a Win10-versioned TFM that drags a ~25 MB Windows
SDK projection (Microsoft.Windows.SDK.NET.dll) into the payload; DirectSound needs no
TFM bump and no native binary (verified on net10 via a spike). DirectSoundOut is not
obsolete in NAudio 3, so the build stays warning-clean.

- AudioOutput: DirectSoundOut targeting the chosen device GUID, falling back to the
  default device if the chosen one is gone; PCM16 to the device; SetDevice (live
  switch), static EnumerateDevices, PlayTestTone (a short 440 Hz tone at a fixed
  audible level, ignoring mute since the user explicitly asked to hear it).
- Setting: audioDeviceId (device GUID; "" = default) in AppSettingsDocument, normalized
  / cloned / merged; LocalData Get/SetAudioDeviceId.
- StartUp applies the saved device on init; ApplyAudioDevice (live) + PlayTestSound.
- Preferences pane: device dropdown (name<->GUID map; the default device is stored as
  "") + a "Test sound" action. Applying a device switches the live output immediately.
- Packaging: NAudio.Dmo.dll added to the payload manifest; notices updated.

Verified: CoreTests (+ audio-device id normalization); clean -Release (base + 4 modules,
payload set-equality OK); --sound / --module-host / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `ac84ef569`

**Merge pull request #22 from bigfnj/stream2/b1-host-audio**

```
B1: host-owned audio output (NAudio 3, base plays pet sounds)
```

### 2026-08-07  `b49a16a78`

**feat(audio): B1 - host-owned audio output (NAudio 3, base plays pet sounds)**

```
Option B: the base now owns audio playback instead of the Sound module. A new
AudioOutput (src/dotNet/AudioOutput.cs) is a single shared mixer + output device
(MixingSampleProvider + WaveOut) that plays the pet's animation sounds today and the
AI speech engine (TTS) later, through one path. Pet MP3s decode once (ACM via the OS
codec, no shipped native binary) into a cached float buffer at the mixer format; each
play adds a volume-wrapped, optionally-looping input, so distinct sounds overlap and
speech can duck SFX once TTS lands. Device errors are swallowed (no audio device =
silent, never throws into the engine).

StartUp routes the engine's animation-sound selection (Animations.SoundSink) straight
to AudioOutput instead of raising AnimationStarted for the module, so the base plays
and the S2 Sound module is inert (retired in B4).

NAudio is a base dependency again (it left in S2 on the false premise that no pet ships
audio - every bundled pet does). Only NAudio.Core + NAudio.WinMM (3.0.0-preview.6, net9+
Span-based; verified decoding a real pet MP3 on net10 via a spike) plus the transitive
NAudio.Midi + System.Numerics.Tensors; no native binary. Payload manifest, the
NAUDIO_LICENSE.txt copy, and THIRD_PARTY_NOTICES updated.

Chose NAudio 3 preview after research: leaner/modular vs v2 (no Dmo/Asio/Wasapi/WinForms),
Span-based, and its MixingSampleProvider makes the future TTS mixer free.

Verified: clean -Release (base + 4 modules; payload set-equality OK); CoreTests;
--sound-selftest / --module-host-selftest / --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `49034ee68`

**Merge pull request #21 from bigfnj/stream2/s5b2g-theme**

```
S5b-2(g): light/dark/system theme for the settings window
```

### 2026-08-07  `22a799ddf`

**fix(ui): theme follows system only + fix mouse-wheel scroll + dark scrollbar**

```
Per feedback on S5b-2(g):
- Drop the visible Theme dropdown; the settings window just follows the OS theme
  (the themeMode plumbing stays dormant, defaulting to system).
- Fix panes not scrolling with the mouse wheel: remove the OUTER window ScrollViewer
  so each pane owns exactly one (schema panes via PaneView, Pets via its own control).
  A nested inner ScrollViewer was swallowing the wheel event without being able to
  scroll (the outer gave it unlimited height).
- Give ScrollBar a dark template in dark mode (WPF scrollbars stay light otherwise).

Verified: clean -Release (base); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `4528bb5d1`

**feat(ui): S5b-2(g) - light/dark/system theme for the settings window**

```
Adds a themeMode preference (System/Light/Dark, default System) and a Theme dropdown
in the Preferences pane. A new WpfTheme applies it when the settings window opens:
System consults the OS (WindowTheme.IsDark, the same registry check the WinForms
dialogs use); Dark paints the window + installs implicit control styles (nav / buttons
/ inputs / lists) and the immersive dark title bar; Light keeps the stock WPF look
(lower risk than fighting the default light templates). A theme change takes effect on
the next open (live re-theme is a follow-up). Combo dropdowns / scrollbars keep default
chrome (WPF template limits) - noted for polish.

- AppSettingsDocument: themeMode (Order 15), normalized to system/light/dark, wired
  into Clone + the cross-process merge; default system; older docs default on load.
- LocalData: GetThemeMode / SetThemeMode.
- WpfTheme: palette + implicit styles + DWM dark title bar; EffectiveDark(mode).
- Preferences pane: Theme enum field + Load/Save.

Verified: CoreTests (24 groups, incl. theme-mode normalization); clean -Release
(base + 4 modules); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `b2b24aaf2`

**Merge pull request #20 from bigfnj/stream2/s5b2f-window-size**

```
S5b-2(f): default settings window to 1050x820 (Pets 3-across)
```

### 2026-08-07  `e277b3b6b`

**feat(ui): S5b-2(f) - default settings window to 1050x820 (Pets 3-across)**

```
Bumps the WPF settings window default from 720x560 to 1050x820 with a min size, so
the Pets gallery reflows to 3 cards across and ~4-5 rows down by default (the gallery
WrapPanel already wraps to fewer columns as the window shrinks). Resizable; the floor
still fits ~2 columns plus the nav.

Verified: clean -Release (base); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `a83b9de70`

**Merge pull request #19 from bigfnj/stream2/s5b2e3-inline-size**

```
S5b-2(e3): size as an inline clickable number in the stats line
```

### 2026-08-07  `2942f3bea`

**feat(pets): S5b-2(e3) - size as an inline clickable number in the stats line**

```
Per user feedback, drop the top-right Size button and make the size level an inline,
clickable number in the card's stats line ("N animations - M sounds - size K"). It's
a Hyperlink styled like the surrounding gray text (no underline at rest, no box, hand
cursor + tooltip); clicking it cycles 1 -> 2 -> 3. Behavior unchanged: each click sets
an explicit override applied when the pet is next added (or on restart). The number
seeds from the pet's stored override, else the effective global level.

Verified: clean -Release (base); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `361f1bd71`

**Merge pull request #18 from bigfnj/stream2/s5b2e2-size-button**

```
S5b-2(e2): per-pet size as a top-right cycle button
```

### 2026-08-07  `e38778367`

**feat(pets): S5b-2(e2) - per-pet size as a top-right cycle button**

```
Replaces the per-card Size dropdown with a compact "Size N" button in the card's
top-right corner that cycles 1 -> 2 -> 3 on click (per user feedback: the dropdown
cluttered the box). Behavior is unchanged - each click sets an explicit override
that applies when the pet is next added or on restart. The button seeds from the
pet's stored override, or the effective global level when it has none.

Verified: clean -Release (base); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `69f13f93a`

**Merge pull request #17 from bigfnj/stream2/s5b2e-per-pet-size**

```
S5b-2(e): per-pet size override
```

### 2026-08-07  `46416ce5d`

**feat(pets): S5b-2(e) - per-pet size override**

```
Each pet card gets a small "Size" dropdown (Default / 1 / 2 / 3) so a pet can be
sized independently of the others - e.g. Pingus at 2 while the sheep stay at 1.
"Default" follows the global size. The override is a scale level baked in when the
pet type is staged, so it applies the next time the pet is added (or on the next
launch); pets already on screen keep their size until then, matching how the
global size behaves.

- AppSettingsDocument: new optional petSizes list (id -> level 1/2/3), normalized/
  deduped/clamped like the pet mix, wired into clone + cross-process merge. Absent
  = follow global; older docs carry none.
- LocalData: GetPetSizeLevel / SetPetSizeLevel / GetEffectivePetScaleFactor.
- StartUp: TryStageRuntime takes the effective factor; the active/default, extra-
  type, and "Use this pet" staging paths all pass the per-pet factor. New
  SetPetSize(id, level) persists + drops a staged-but-unused type so a fresh add
  re-stages at once.
- CompanionsPaneControl: the per-card Size dropdown.

Verified: CoreTests (23 groups, incl. new per-pet size validation); clean -Release
(base + 4 modules); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `3d8dc5e5d`

**Merge pull request #16 from bigfnj/stream2/s5b2c4-check-new-pets**

```
S5b-2(c4): Check-for-new-pets button in the Pets pane
```

### 2026-08-07  `ed904c751`

**feat(ui): S5b-2(c4) - "Check for new pets" button in the Pets pane**

```
Adds a footer "Check for new pets" button to the WPF Pets gallery. It fetches
the online catalog (RemoteCatalogClient), diffs it against the locally present
pets (bundled + downloaded), reports the count, and lists any new ones as
download cards. Downloading reuses the HTTPS-trusted, SHA-256-verified path the
classic Options window used: DownloadVerifiedAsync -> CompanionXmlValidator ->
atomic write to the library pets dir, then the gallery refreshes and re-diffs
against the cached catalog (no re-fetch). The network CTS is cancelled on unload.

Request D of the Pets feature set (A card enrichment / B per-pet sound /
C bundle-all / D check-for-new).

Verified: clean -Release (base + 4 modules); --wpf-options-selftest green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `18066de78`

**Merge pull request #15 from bigfnj/stream2/s5b2c3-pet-cards**

```
S5b-2(c3): Pets card enrichment (descriptions, counts, quips)
```

### 2026-08-07  `a80c88ff4`

**feat(ui): S5b-2(c3) - Pets card enrichment (descriptions, animation/sound counts, quips)**

```
Each pet card now shows a unique tongue-in-cheek blurb plus an "N animations . M
sounds" line. The seven colored sheep share one 268-move set, so each gets its
own colour-based quip (CompanionBlurbs) to keep the descriptions distinct. Counts are
read from each pet's animations.xml (animation / sound elements) and cached per id.

Request A of the Pets feature set (A card enrichment / B per-pet sound /
C bundle-all / D check-for-new).

Verified: clean -Release (base + 4 modules); --wpf-options-selftest green.
Live gallery manual-eyeball. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `57499708d`

**Merge pull request #14 from bigfnj/stream2/s5b2c2-pets-multipet**

```
S5b-2(c2): Pets gallery multi-pet count + Remove button + eSheep icon
```

### 2026-08-07  `ce61d12b6`

**feat(ui): S5b-2(c2) - Pets gallery multi-pet count + Remove button + eSheep icon**

```
Addresses the eyeball feedback on the Pets pane:
- The gallery now reflects the live on-screen MIX (StartUp.OnScreenMix, the same
  source the tray "Remove a pet" submenu uses), not just the single active type.
  Each card shows "on screen: N" (with an "active" tag on the primary type), so
  an Added pet in a multi-pet setup is visible.
- Added a Remove button per on-screen pet (removes one of that type via
  RemoveOnePet; the active/default type is keyed "" in the mix), then refreshes.
- The built-in "eSheep (default)" card now falls back to the app icon (the
  default isn't in the pet-thumbnails zip), so every card has an icon.

Verified: clean -Release (base + 4 modules); --wpf-options-selftest +
--pettyperegistry-selftest green. Live gallery is manual-eyeball. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `ad1ce3cb3`

**Merge pull request #13 from bigfnj/stream2/s5b2c-pets-pane**

```
S5b-2(c): host Pets gallery pane (custom WPF control)
```

### 2026-08-07  `b00c0d84c`

**feat(ui): S5b-2(c) - host Pets gallery pane (custom WPF control)**

```
Adds the Pets gallery to the WPF settings window. Since a thumbnail gallery
isn't expressible as a data schema, the OptionsWindow now supports host-built
CUSTOM panes alongside schema panes - without leaking any WPF type into the
plugin ABI (the ABI stays schema-only + framework-agnostic; the module-supplied
custom-control escape hatch stays deferred to when a third party needs it).

- OptionsWindow: new host-side ShellPane abstraction (SchemaShellPane wraps an
  ABI OptionsPane -> PaneView; CustomShellPane hosts a host-built control). The
  Apply button shows only for schema panes; custom panes apply via their own
  controls. CollectPanes now returns ShellPane[]: Preferences (schema) + Pets
  (custom) + each module's schema pane.
- CompanionsPaneControl: a card per installed pet (thumbnail + name + Use/Add + an
  Active marker), backed by the base CompanionsController; Use/Add apply immediately
  through the runtime and refresh the gallery. Local pets only for now (the
  online "get more pets" catalog is a follow-on - it needs an ICatalogService).
- CompanionThumbnails.GetPng(id): raw PNG bytes so the WPF gallery builds a
  BitmapImage directly (no System.Drawing round-trip).
- --wpf-options-selftest updated for the ShellPane return (asserts Preferences
  is a schema pane with Apply + Pets is a custom pane without).

Verified: clean -Release (base + 4 modules); --wpf-options-selftest + --aibrain
+ module-host / fortunes / pettyperegistry / security + resource-churn soak all
green. The live gallery render is manual-eyeball. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `9e7c8431a`

**Merge pull request #12 from bigfnj/stream2/s5b2b-prefs-pane**

```
S5b-2(b): complete the core Preferences pane + Restore-pet
```

### 2026-08-07  `171cb2da3`

**feat(ui): S5b-2(b) - complete the core Preferences pane in the WPF window**

```
Brings the WPF Preferences pane up to the legacy Preferences tab. Added
(alongside the existing volume/speech/duration): Run at Windows startup, Bring
collided window to front, Keep pet above the taskbar, Allow multiple screens,
Pets at startup, Size (1-3), and the Randomly-drop-a-fortune toggle + its
every / plus-or-minus minutes. Plus a "Restore default pet" action button.

Backing: LocalData for the core prefs (persist immediately), StartupRegistration
(HKCU Run) for run-at-startup, and AiSettings (load-mutate-save) for the
random-drop trio; on save it nudges the running pet via ICompanionRuntime.ReloadAiSettings
+ refreshes the tray speech item. Restore-pet reuses CompanionCatalog + the runtime's
LoadNewXMLFromString.

Verified: clean -Release (base + 4 modules); --wpf-options-selftest + --aibrain
+ module-host / fortunes / security / hardening / pettyperegistry + --options
(isolated root) + resource-churn soak all green. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `862739296`

**Merge pull request #11 from bigfnj/stream2/s5b2-ai-pane-parity**

```
S5b-2(a): complete the AI Brain pane to near-parity with the legacy tab
```

### 2026-08-07  `3dbf51640`

**feat(ui): S5b-2(a) - complete the AI Brain pane to near-parity with the legacy tab**

```
Brings the WPF AI Brain pane up to the substance of the classic Options AI tab.
Added fields (bound to the module's AiSettings via the existing Load/Save):
- Pet name, Your name, Personality (text)
- Speech style (enum of the friendly Personas names; the setting stores the id)
- Remember recent remarks (memory)
- Endpoint / base URL (text) with the provider/endpoint dance: switching the
  Provider prefills that provider's default endpoint; keeping the provider
  honors an edited endpoint (avoids a stale field clobbering a fresh preset)
- Idle max (seconds), Start Ollama automatically, Preload model on launch

Still deferred (a focused follow-on, not parity of substance): the Persona
PRESET dropdown (needs reactive enum->text linkage the static schema doesn't do)
and the Text/Vision model DROPDOWNS + "Refresh model list" (needs a new backend
list-models call + a dynamic-enum re-render). Model fields stay editable text for
now; Test connection still validates the chosen model.

Verified: clean -Release (base + 4 modules); --aibrain-selftest + --wpf-options-selftest
+ module-host / sound / fortunes / security / hardening + resource-churn soak all
green (exit 0). Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `176a670d8`

**Merge pull request #10 from bigfnj/stream2/s5b2a-pane-actions**

```
S5b-2a: action buttons on schema options panes + AI Test connection
```

### 2026-08-07  `a372877d9`

**feat(ui): S5b-2a - action buttons on schema options panes + AI Test connection**

```
Adds the missing "action" concept to the WPF options panes (the schema is
data-only, so buttons that DO something had no home). The classic Options AI
tab's Test connection / Clear history now have parity in the module pane.

- ABI: OptionsPane gains an Actions list of PaneAction { Label, InvokeAsync }.
  InvokeAsync is async (Task<string>) so a ~15s connection probe never freezes
  the UI; it returns a short status line the host shows next to the button.
- WPF: PaneView renders each action as a button + status line; on click it
  disables the button, shows "working...", awaits InvokeAsync, then shows the
  result. Defensive (a throwing action reports "failed: ..." rather than
  breaking the pane).
- AiBrain contributes two actions on its pane: "Test connection" (builds a
  backend from the current settings, probes availability + a tiny chat, reports
  "connected - <model> OK <n>s" or the error) and "Clear chat history" (deletes
  the module's persisted history).
- --wpf-options-selftest gains an action-invocation assertion.

Verified: clean -Release (base + 4 modules); --wpf-options-selftest (now incl.
the action) + --aibrain-selftest + 11 regression flags + resource-churn soak all
green (exit 0). Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `db4209bb7`

**chore: bump to 1.2.0 for the plugin-re-arch dev reinstall (S1-S5b)**

```
Dev build for local testing of the plugin host + module UI (Sound/Fortunes/
AiBrain modules + the new WPF module-settings window). Not a public release
(no tag). 2.0.0 remains the S6 bare-host milestone.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `d5ec61c39`

**Merge pull request #9 from bigfnj/stream2/s5b-wpf-shell**

```
S5b-1: minimal WPF settings shell with schema-driven module panes
```

### 2026-08-07  `dc59e0125`

**feat(ui): S5b-1 - minimal WPF settings shell with schema-driven module panes**

```
First cut of the WPF module-manager window that will replace FormOptions. It
renders the core Preferences pane plus each module's schema-driven OptionsPane,
and modules persist to their OWN store via a new OptionsPane Load/Save binding.
Coexists with the classic Options dialog for now (opened from a new tray item);
FormOptions retires in a later S5 step.

- ABI: OptionsPane gains Load()/Save(values) delegates so a module renders as a
  declarative schema yet persists to its own (possibly DPAPI-scoped) store, not
  just the host's IModuleSettings bag. Secrets stay write-only: Load never
  returns the plaintext; Save receives a secret key only when the user typed one.
- WPF (programmatic, no XAML => no BAML/packaging change): OptionsWindow (left-nav
  + content + Apply/Close, shown modally from the WinForms UI thread) and PaneView
  (schema -> WPF controls: Bool=CheckBox, Int/Text=TextBox, Enum=ComboBox,
  Secret=PasswordBox; collects + saves edited values). PaneView is
  headless-constructable so the render + round-trip is self-testable.
- OptionsShell assembles the core Preferences pane (LocalData-backed:
  speech/duration/volume) + the module OptionsPanes and opens the window; new
  "Module settings..." tray item launches it.
- AiBrain contributes a full "AI Brain" pane (enable/provider/models/vision/
  hotkey/idle/consent/api-key) bound to its own AiSettings via Load/Save -
  exercising every SettingKind incl. the enum + write-only secret.
- New --wpf-options-selftest (STA): OptionsShell yields the core pane; PaneView
  renders all 5 kinds + round-trips Load->controls->Collect; blank secret omitted;
  Save forwards. Wired into build.yml.

Verified: clean -Release (base + 4 modules); full self-test suite (13 no-env flags
incl. --wpf-options-selftest + --options under DESKTOPPET_DATA_ROOT) + source
invariants + resource-churn soak all green (exit 0). The live modal window is
manual-eyeball (a modal WPF window can't be shown headlessly). Zero regression.
Box stays v1.1.0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-07  `6fbaef510`

**build: S5b-1a - enable UseWPF alongside WinForms (infra only, no WPF code yet)**

```
First, de-risked step of the WPF module-manager shell: turn on <UseWPF> in the
base project so WPF and WinForms coexist in-process (both in-box on
net10-windows; the pet stays WinForms, the coming options window is WPF). No
WPF code yet - this isolates the infra change so any build/packaging fallout
surfaces on its own.

Verified: clean -Release (base + 4 modules, 0 warnings); payload manifest
unchanged (WPF is framework-provided / FDD, nothing new copied to output); full
no-env self-test suite (12 flags) + resource-churn soak all green. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `feeeada71`

**Merge pull request #8 from bigfnj/stream2/s5a-tray-contributions**

```
S5a: tray assembled from module contributions (closes the S4 AI gap)
```

### 2026-08-06  `5269a7a37`

**feat(plugins): S5a - tray assembled from module contributions (closes the S4 AI gap)**

```
The context menu now renders module-contributed TrayItems, and the AiBrain
module contributes its own Enable/Disable + Ask items - so the AI brain is
reachable from the tray again (it was off-only-via-settings after the S4 flip;
this closes that accept-the-gap).

- ContextMenus: on menu Opening, merge CompanionHost.TrayItems (sorted by Group then
  Order, separator between groups) just after Test Speech, re-evaluating each
  item's Visible/DynamicText live and building BuildChildren submenus lazily on
  open. Rebuilt every open so late-loaded modules appear and dynamic labels
  refresh. Fully defensive - a throwing module item can never break the core tray.
  The Opening handler is unhooked + the tracked items cleared on dispose.
- AiBrainModule: contributes "Enable AI"/"Disable AI" (DynamicText toggle, Click
  flips the module's own AiBrainEnabled + saves + rebuilds the brain) and "Ask
  about my screen" (Visible only when enabled). Its own enable/ask entry point now
  that the base's AI tray items are gone.
- --aibrain-selftest asserts the module contributes exactly its 2 tray items.

First slice of S5 (the tray half of "tray-from-contributions"); the WPF options
shell + schema panes come in S5b. WinForms-only, additive, no WPF dependency.

Verified: clean -Release (base + 4 modules); --aibrain-selftest (incl. the 2 tray
contributions + in-ALC engine probe) + module-host / sound / fortunes / security /
hardening all PASS (exit 0) + resource-churn soak (exit 0). The live tray render is
manual-eyeball (a real tray menu can't be opened headlessly). Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `2a6f8bf75`

**Merge pull request #7 from bigfnj/stream2/s4-aibrain**

```
S4: extract the AI-brain module (functional flip)
```

### 2026-08-06  `de84691b3`

**docs: update handoff + backlog for S4 (AI-brain module functional flip)**

```
S3 marked done+merged; S4 = the AI brain now lives in modules/AiBrain and owns
the ask/hotkey/idle/drop flow, base runtime-disconnected. Base AI file/UI
deletion deferred to S5 (entangled with the AiSettings DPAPI split), mirroring
how S3d deferred the fortune UI/engine cleanup.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `f5b954e76`

**feat(plugins): S4b-2 - flip the base off AI at runtime (module is the sole owner)**

```
The base no longer drives the AI brain; the AiBrain module owns it. Mirrors the
S3d fortunes flip: the runtime is disconnected now, and the base's now-dead AI
UI + engine code are retired in S5 (with the WPF Options rebuild + AiSettings
split), so this stays a low-risk, no-regression cut.

Base changes:
- StartUp.DropTimer_Tick always raises the arbitrated drop tick (Host.RaiseDropTick):
  the AiBrain module's drop responder takes it as an AI insight when its brain is
  enabled, otherwise the Fortunes module speaks. The base no longer branches on a
  base brain.
- StartUp.ApplyAiBrainState is neutered: it only ever RETIRES any prior base brain
  (allowed forced false), so the base never builds/warms/hotkeys/idles its own
  brain and a base brain + the module brain can never both be live.
- ContextMenus: the AI tray items (Ask / Enable-Disable AI) + their helpers are
  removed (accept-the-gap; the module contributes its own once the tray is
  assembled from module contributions in S5).
- OptionsController: the AiController/AiState seam is removed (it was only exercised
  by --options-selftest); the AI config UI is rebuilt from module contributions in S5.
- AiProviders extracted from OpenAiCompatBackend.cs into its own file so it survives
  that file's eventual deletion (AiSettings still uses it until the S5 split).
- FortuneProvider.FilterSelfTest drops its ChatHistory deletion sub-check (chat
  history is the AI module's concern now).
- runtime-hardening source invariant updated: asserts the AI tray items are ABSENT
  from the base (moved to the module) instead of asserting their old labels.

Deferred to S5 (like S3d deferred the fortune UI/engine): removing the FormOptions
AI tab, deleting the 8 base AI-brain files, and trimming the SecuritySelfTest AI
tests. Those are entangled with AiSettings' DPAPI credential machinery, so they are
cut together with the AiSettings split + the WPF Options rebuild rather than by
risky surgery mid-flip. The base still COMPILES + its AI defensive self-tests still
PASS (the AI files remain); the base just never RUNS the brain.

NB: with the base neutered, the legacy FormOptions "Enable AI" toggle is inert
(the module reads its own store); this is the accepted transition gap until S5.

Verified: clean -Release (base + 4 modules); full self-test suite green (12 no-env
flags + options/fortunecache/fortunes-webview under DESKTOPPET_DATA_ROOT + source
invariants) + resource-churn soak (exit 0); --aibrain-selftest live-but-disabled +
in-ALC engine probe all PASS. Zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `3476fba88`

**feat(plugins): S4b-1 - the AI-brain module goes live (base still present, both off)**

```
The AiBrain module is no longer dormant: it now owns the "ask about my screen"
flow end to end, through host services only:
- registers a drop responder ABOVE Fortunes (priority 10 vs 0): when the brain
  is enabled the periodic drop becomes an AI insight and it handles the tick;
  when disabled it declines so Fortunes speaks instead.
- registers the global hotkey via host.RegisterHotkey (the real registrar from
  S4a-1) when enabled.
- owns its own idle-commentary loop (jittered WinForms timer + change-detection
  gate); the relocated AiSessionManager's generation guards replace the base's
  GenerationAwareIdleSchedule.
- emotes every pet via host.PlayAnimationAll and speaks via host.SayAll, with
  the async LLM response marshalled back to the UI thread via the
  SynchronizationContext captured in Init.
- brain lifecycle (build/prepare/retire, generation/supersede) = the relocated
  AiSessionManager; settings + chat history = the module's own DPAPI-scoped store.

Non-destructive migrator: on first run, if the module has no settings yet, it
COPIES the base ai-settings.json (incl. the DPAPI keys, same-user decryptable)
into the module store, leaving the base file intact.

OFF by default (its own AiBrainEnabled): a fresh install does nothing until
enabled, and there is no tray/Options UI yet (accept-the-gap; rebuilt in S5).
The base still owns its own AI at runtime for now (both off => no double-fire,
no hotkey/drop conflict); the base AI is stripped next in S4b-2.

--aibrain-selftest updated dormant->live: asserts the live subscriptions + drop
responder, that the OFF brain stays silent and its drop declines to Fortunes,
Shutdown unsubscribes, plus the unchanged in-ALC engine probe (DPAPI key
round-trip et al). Migrator + store isolated under a temp DESKTOPPET_DATA_ROOT
so the test never touches the real settings file.

Verified: clean -Release (base + 4 modules); --aibrain-selftest + module-host /
sound / fortunes / fortunes-engine all PASS (exit 0). No regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `525093609`

**feat(plugins): S4a-3 - relocate the AI-brain engine into the module (dormant)**

```
Copies the AI-brain engine into modules/AiBrain/engine/ (keeping namespace
DesktopPet.Ai so mutual refs resolve in-module, exactly as S3c did for
fortunes), rebound to the plugin ABI. The base is UNTOUCHED and keeps its own
copies + keeps owning the AI brain at runtime; the module stays DORMANT
(Init wires nothing) => zero regression until the S4b flip.

Relocated: AiBrain, AiSessionManager, AiEndpointPolicy (+ AiBackendHttpException),
OllamaClient, AiExecutablePolicy, OpenAiCompatBackend (+ AiProviders), Personas,
BrainResponse (+ ChatMessage), ICompanionBrainBackend, AiSettings (+ AiModelPolicy),
ChatHistory. Rebinds:
- AppPaths.{AiSettingsFile,ChatHistoryFile,Legacy*} -> a module AiPaths backed by
  host.GetStorage("aibrain") (temp fallback until live); legacy %APPDATA%
  migration OFF here - that (with the DPAPI keys) is the S4b migrator's job.
- ScreenCaptureContext -> ABI ScreenContext (PixelRect->Rectangle,
  ActiveWindowTitle->WindowTitle) in AiBrain/AiSessionManager.
- AtomicFile (+ TryWriteAllText, its lone AppPaths coupling swapped for the in-box
  Path.IsPathFullyQualified) + CrossSessionLock -> engine/FileHelpers.cs, and
  UnicodeTextProgress -> engine/TextHelpers.cs (both copied, namespace DesktopPet).

The module carries Newtonsoft.Json as its OWN dependency (like NAudio for Sound):
Newtonsoft.Json.dll + AiBrain.deps.json land beside AiBrain.dll and resolve in the
module's AssemblyLoadContext. UseWindowsForms for the screen-capture path.

New AiEngineProbe (reflected by --aibrain-selftest) proves the relocated engine
RUNS in the module's load context without a live LLM: the DPAPI-scoped settings
store end to end (encrypt -> atomic write -> cross-session lock -> reload ->
decrypt), chat-history persistence, endpoint/persona/model policy, and Ollama +
OpenAI-compat backend construction.

Verified: clean -Release (base + 4 modules); full self-test suite green (12
no-env flags + options/fortunecache/fortunes-webview under DESKTOPPET_DATA_ROOT +
source invariants) + resource-churn soak (exit 0); --aibrain-selftest engine leg
all PASS incl. the in-ALC DPAPI key round-trip. Base still owns the AI brain.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `a299f4ce9`

**feat(plugins): S4a-1/2 - real hotkey registrar + dormant AiBrain module scaffold**

```
S4a-1: CompanionHost.RegisterHotkey is now real (was a no-op stub). The ABI makes
global-hotkey registration a host service, so the host owns the registrar and
wraps the proven HotkeyListener; a bad/taken combo degrades to a no-op handle
rather than throwing into the module. Disposing the handle unregisters.

S4a-2: new modules/AiBrain (id "aibrain", AssemblyName AiBrain), a DORMANT
scaffold - Init wires nothing and starts nothing, so the base keeps owning the
AI brain at runtime (no double-ask, no hotkey collision) until the S4b flip.
Contract-only for now (it gains its Newtonsoft dependency + UseWindowsForms in
S4a-3 when the engine relocates). Declares the full capability set it will use
(Speech/Animation/ScreenContext/Network/Hotkey/Storage) for the S7 consent UX.

Wired into build.ps1 (4th module) and CI's self-test flag loop. New
--aibrain-selftest loads only aibrain in an isolated modules root and asserts
its boundary (id/name/permissions) + dormancy (no subscriptions, no drop
responder, no tray/options, and spawn/land/poke speak nothing), plus a
best-effort smoke of the real hotkey registrar (skip-passes when a message
window / RegisterHotKey isn't available).

Verified: clean -Release (base + 4 modules) and module-host / sound / fortunes
/ fortunes-engine / aibrain self-tests all PASS (exit 0). The hotkey smoke
passed (not skipped) on this box - the registrar reserves a real OS hotkey and
disposes cleanly. Base still owns the AI brain; nothing flipped.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `7d0c58d21`

**feat(plugins): S4a-0 - additive ABI surface for the AI-brain module**

```
Two additive contract changes the AI brain needs, ahead of its extraction:

- IHost.PlayAnimationAll(candidates): play an emotion on every live pet
  (first candidate each pet's XML defines wins). The base owns the live-pet
  list, so this parallels SayAll; backed by a new StartUp.PlayAnimationOnAll
  that EmoteAll now delegates to (mapping stays data, moves to the module).
- ScreenContext.WindowUnderCompanion: preserves screen-zone awareness (feature 5.6)
  so a module brain keeps the window-the-pet-stands-on context.

Both are purely additive. In-repo the only IHost implementers are CompanionHost +
the four self-test recording hosts (no third-party hosts exist yet); all are
updated. Base still owns the AI brain at runtime; nothing is flipped.

Verified: clean -Release (base + 3 modules) and module-host / sound /
fortunes / fortunes-engine self-tests all PASS (exit 0), zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `ab3b00f3a`

**Merge pull request #6 from bigfnj/stream2/s3d-flip**

```
S3d: flip fortunes to the module + shed the smart/ONNX engine from the base
```

### 2026-08-06  `eba2e357d`

**feat(plugins): S3d-2 - shed the smart/ONNX fortune layer from the base**

```
Removes the smart fortune engine (Embedder + SmartFortunes) and its ~50MB payload (onnxruntime.dll +
the 34MB bge-small model + Microsoft.ML.OnnxRuntime.dll + System.Numerics.Tensors.dll + ONNX licenses)
from the base app. That engine now lives only in the Fortunes module (S3c/S3d-1), which owns runtime
fortune-speaking. Net: -3,244 lines from the base.

Scope note: the DUMB FortuneProvider + FortuneFileImporter (no ONNX) intentionally STAY in the base for
now - RemoteCatalog uses FortunePackLoadPolicy for pack-download limits and the Options FortunesController
enumerates sources - so there's no Options stub or RemoteCatalog rework here. Those + the residual embedded
corpus + the (now-disconnected) fortunes Options tab move to the module when the Options UI is rebuilt in S5.

- StartUp: delete fortuneRuntime + FortuneRuntimeState + StartFortuneGeneration + SayFortune + the ctor/
  shutdown wiring (all dead after S3d-1); stub SmartFortunesStatus (ICompanionRuntime) to a placeholder string.
- SecuritySelfTest: delete the four smart-fortune-lifecycle tests (generation ownership / random
  availability / init disposal / smart-pick disposal, which drove StartUp.FortuneRuntimeState + SmartFortunes)
  + their call-sites; the AI-brain idle-schedule test stays. They're covered in the module via
  --fortunes-engine-selftest now.
- Program + build.yml: drop --embed / --smart / --smart-progress (moved to the module engine self-test);
  keep --filter / --fortunecache (dumb FortuneProvider).
- csproj: remove Embedder.cs + SmartFortunes.cs, the Microsoft.ML.OnnxRuntime PackageReference, the
  bge-small model, and the ONNX license Content; base lock file regenerated (ONNX deps gone).
- packaging/runtime-files.txt: drop onnxruntime.dll / onnxruntime_providers_shared.dll /
  Microsoft.ML.OnnxRuntime.dll / System.Numerics.Tensors.dll / bge-small.* / ONNX licenses. git rm the 2 files.

Validated (full suite, all PASS, zero regression): base output confirmed ONNX-free (module carries it);
--fortunes-selftest (live flip) + --fortunes-engine-selftest (module dumb+smart+ONNX-in-ALC) +
--module-host + --sound + --filter + --fortunecache + --options + --fortunes-webview + --security
(smart-lifecycle tests cleanly gone, rest intact) + --hardening + --pettyperegistry + --catalog +
--fullscreen; resource-churn soak 6/6 error=null.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `114e2cce0`

**feat(plugins): S3d-1 - make the Fortunes module the live fortune source**

```
The base hands fortune-speaking to the module (no double-speak). The module now subscribes to
CompanionLanded / CompanionPoked / a drop responder and speaks a fortune (smart pick when ready, else random) from
its own engine + storage, keeping the personalized welcome on first spawn. With no installed pack the
pool is empty, so land/poke/drop are silent except the welcome - the intended "engine ships, content
doesn't" behavior; the classic corpus becomes an importable starter pack later.

Module (FortunesModule): builds FortuneProvider (+ background-warmed SmartFortunes when enabled) from
host.GetStorage("fortunes") + host.GetSettings("fortunes"); OnPetLanded/OnPetPoked(1-2)/OnDrop ->
SpeakFortune (mirrors the old StartUp.SayFortune, using host.CaptureScreenContext of the last-seen pet for
the smart context); disposes the engine + unsubscribes on Shutdown. Version -> 1.0.0.

Base (StartUp): the five SayFortune call-sites are redirected - land/poke(1-2) drop their SayFortune (the
module handles them via the events already raised); DropTimer_Tick's "else", the poke-12 escape fallback,
and the brain-off ask-fallback now call Host.RaiseDropTick() (the module's arbitrated fortune responder).
InitAiTriggers/ReloadAiSettings/RebuildSmartFortunes call ApplyRandomDrop instead of StartFortuneGeneration,
so the base no longer builds/warms its own fortune engine. The base engine classes + ONNX + embedded
corpus + the fortunes Options tab remain (now dead/disconnected) - S3d-2 stubs the Options and strips them.

Verified: build.ps1 -Release clean (base + 3 modules); --fortunes-selftest PASS end-to-end (land/poke-1/drop
each speak a pack line; poke-4 stays silent per the base's 3-4 ignore range; welcome personalized;
unsubscribe on shutdown) - the self-test records ALL SayAll since the dev TestModule also answers pokes;
--fortunes-engine-selftest + --module-host + --sound + base --filter-selftest + --smart-selftest +
--security + --hardening + --pettyperegistry all PASS; churn soak 6/6 error=null.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `098f74290`

**Merge pull request #5 from bigfnj/stream2/s3-engine**

```
S3c: relocate the fortune engine (dumb + smart + ONNX) into the module, dormant
```

### 2026-08-06  `69e564737`

**docs: update handoff + backlog for the S3c engine relocation**

```
Reflect S3c (dumb + smart fortune engine relocated into the module, dormant, native ONNX in the plugin ALC) as done; S3d (flip the base over) as next. Refresh the SDK/global.json note (relaxed to any 10.x) and add the ONNX-in-ALC / LoadUnmanagedDll gotcha (native onnxruntime.dll is flattened beside the module dll; the loader probes the module folder as a fallback).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `77af5ea4a`

**feat(plugins): S3c-2 - relocate the SMART fortune layer (SmartFortunes + Embedder) + ONNX into the module**

```
Completes the engine relocation (expand step). The Fortunes module now carries the WHOLE fortune engine -
dumb (S3c-1) + smart - with native ONNX loading and running inside the module's own AssemblyLoadContext.
Still DORMANT (FortunesModule.Init = welcome only), so the base is UNTOUCHED and keeps owning fortunes at
runtime - zero regression. This was the hardest technical piece: native onnxruntime.dll in a plugin ALC.

Module (modules/Fortunes/engine/):
- SmartFortunes.cs / Embedder.cs: copied in. Embedder verbatim (0 base coupling; AppDir = its own dll's
  folder, so the model resolves beside Fortunes.dll). SmartFortunes rebinds: AppPaths.PrepareVectorCache-
  Directory -> FortunePaths.VectorCacheDir; new AiSettings() -> new FortuneSettings() (its self-tests).
  CrossSessionLock + AtomicFile resolve to the module copies.
- FileHelpers.cs: added CrossSessionLock (copied verbatim from the base AppSettingsStore, self-contained)
  alongside the AtomicFile from S3c-1.
- Fortunes.csproj: Microsoft.ML.OnnxRuntime 1.28.0 + win-x64 RID (framework-dependent) +
  CopyLocalLockFileAssemblies + GenerateDependencyFile + bge-small.onnx/.vocab.txt assets beside the dll.
- FortuneEngineProbe: extended with the smart checks (Embedder.SelfTest = ONNX embed in-ALC; SmartFortunes
  warm/pick over the injected pool, exercising the VectorCache/CrossSessionLock/FortunePaths rebinds).

Base (additive host-infra only - no fortune behavior touched):
- ModuleHost's ModuleLoadContext gains a LoadUnmanagedDll override: resolve native deps via the module's
  deps.json/resolver, then fall back to probing the module's own folder (onnxruntime's build targets flatten
  the native dll beside the module dll rather than under runtimes\<rid>\native\, so the folder probe makes
  it resolve on an installed machine with no NuGet cache too). NAudio was pure-managed and never needed this.

Verified: build.ps1 -Release clean (base + 3 modules); --fortunes-engine-selftest PASS incl. "Embedder loads
ONNX + embeds in the module ALC" + "SmartFortunes warms the injected pool in-module (VectorCache/lock
rebinds)"; all regression PASS (--fortunes-selftest, --module-host, --sound, base --filter-selftest +
--smart-selftest, --security, --hardening) and the resource-churn soak PASS (fresh 6/6, error=null). Base
fortunes unchanged. (Note: the base + module now both carry ONNX/model in the local build output - expected
temporary duplication; the base drops its copy in S3d, and modules aren't in the installer until S6.)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `a757d3f0d`

**feat(plugins): S3c-1 - relocate the dumb fortune engine into the module (dormant)**

```
Expand step of the engine relocation (expand/contract). Copies the base of the fortune engine
(FortuneProvider + FortuneFileImporter) into the Fortunes module and rebinds its base dependencies to
module-local equivalents. The engine is DORMANT (FortunesModule.Init still only does the welcome), so the
base is UNTOUCHED and keeps owning fortunes at runtime - zero regression. The smart layer (SmartFortunes +
Embedder + ONNX) is the next step (S3c-2); the base flip is S3d.

Why "dumb engine first": FortuneProvider/FortuneFileImporter have no dependency on the smart layer
(verified), so relocating them alone needs no ONNX and isolates the ONNX-native-loading risk to S3c-2.

Module (modules/Fortunes/engine/, namespace kept as DesktopPet.Ai so the files' mutual refs resolve
in-module):
- FortuneProvider.cs / FortuneFileImporter.cs: copied verbatim, then rebound - AiSettings -> a module
  FortuneSettings (identical fortune fields), AppPaths.PrepareFortunesDirectory/BundledFortunesDirectory
  -> FortunePaths (host-storage-backed, temp fallback), and the embedded fortunes.txt is simply not shipped
  (EmbeddedCorpus already returns empty when absent = no bundled content). The FilterSelfTest calls into
  base UI/AI self-tests (FormOptions x3, ChatHistory, ValidateEmbedded) are stripped - those stay in the base.
- FortuneSettings.cs / FortunePaths.cs: the settings + path seams.
- FileHelpers.cs: AtomicFile.ReplaceExisting copied from the base AppSettingsStore (the engine's only
  file-helper use; TryWriteAllText, which couples to AppPaths, is omitted). CrossSessionLock arrives with
  the smart layer in S3c-2.
- FortuneEngineProbe.cs: a public static self-test hook.
- Fortunes.csproj: embed the classifier-parity TSV (fixture for FilterSelfTest); SDK globbing compiles engine\*.cs.

Base (additive only - no fortune behavior touched):
- --fortunes-engine-selftest (Program.cs + src/dotNet/Plugins/FortunesEngineSelfTest.cs + build.yml): loads
  the real Fortunes.dll via the ALC loader and reflectively runs FortuneEngineProbe.Run.
- global.json: relax the SDK pin (was 10.0.201/latestPatch; 10.0.201 is no longer installed here, only
  10.0.302) to version 10.0.100 + rollForward latestMinor, so any installed 10.x SDK builds (CI still uses
  the 10.0.201 it sets up). Necessary build-config fix - blocked all builds.

Verified: build.ps1 -Release clean (base + 3 modules); --fortunes-engine-selftest PASS (relocated engine
loads in the module ALC; dumb filter/pick + the engine's full FilterSelfTest - dedup/classifier/parser/
ingestion/importer - all pass in-module); regression all PASS (--fortunes-selftest welcome, --module-host,
--sound, base --filter-selftest, --security, --hardening, --pettyperegistry) and the resource-churn soak
PASS (fresh 6/6, error=null) with all three modules present. Base fortunes unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `a0e171fc6`

**docs: refresh handoff + backlog for .NET 10 / plugin re-arch**

```
handoff.md was stale (v1.0.6 / .NET 4.8 era). Bring it to current: the .NET 10 (v1.1.0) migration + the plugin re-architecture status (S1 host + S2 Sound + S3.1 Fortunes welcome starter merged; S3.2 engine relocation next via expand/contract), the locked design decisions, and refreshed build/verify/gotcha notes (module ALC + deps-file/CopyLocal, the churn-soak DESKTOPPET_DATA_ROOT requirement + the | tail exit-code trap, net10 in-box packages). BACKLOG.md gains a top pointer to the re-architecture and marks the Fortunes-tab overhaul as subsumed by S5.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `7ae1aab40`

**Merge pull request #4 from bigfnj/stream2/s3-fortunes-module**

```
S3 (part 1): Fortunes module boundary + personalized welcome starter
```

### 2026-08-06  `d39c209e5`

**feat(plugins): S3b - Fortunes module personalized welcome starter**

```
The Fortunes module gains its personalized starter voice: on the first pet spawn of the session it speaks
a sheep-themed welcome line with the current Windows user's name substituted in - the "landing quote"
tailored to whoever is logged in. Delivered as a self-contained module feature (welcome-on-spawn doesn't
collide with the base's land/poke/drop fortunes), so it's green with zero regression; the base still owns
fortunes until the engine relocation.

Design: the module ships the engine + this personalized starter corpus and NO real fortune content - a
fresh install greets you by name until you add a pack. The corpus (116 "{name}"-templated one-liners) is
adapted from the ai-platform DeskPet welcome quips; keys off the Windows username (Environment.UserName,
fallback "friend") rather than an app account.

- modules/Fortunes/welcome.json: the embedded starter corpus.
- FortunesModule: loads + parses welcome.json (System.Text.Json, in the module's load context), subscribes
  CompanionSpawned, and on the first spawn picks a line + substitutes the username + SayAll; once per session;
  unsubscribes on Shutdown. Bumped to 0.2.0 (engine relocation -> 1.0.0). Never throws into the host.
- src/dotNet/Plugins/FortunesModuleSelfTest.cs + --fortunes-selftest (Program.cs) + build.yml flag: proves
  the corpus parsed in the module's ALC (116 lines), the welcome is personalized (contains the user name,
  no leftover {name}) and fires only once, and unsubscribes on shutdown.

Verified: build.ps1 -Release clean (base + 3 modules); --fortunes-selftest PASS (spoke e.g. "I have SO
much to not tell you, <user>. Welcome."); --module-host-selftest + --sound-selftest PASS (no regression);
and the resource-churn soak PASS (fresh run, 6/6 cycles, error=null) on the live app with all three modules
loaded. (Note: the earlier churn "PASS" reads for S2/S3 were stale - a | tail pipe masked the exe exit code;
re-run correctly here with DESKTOPPET_DATA_ROOT set, which covers both S2 and S3 since both are in this build.)

Next S3 increment: relocate the real engine (FortuneProvider / SmartFortunes / Embedder / FortuneFileImporter)
+ the StartUp land/poke/drop fortune loop out of the base, rebinding base infra to the ABI.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `cf37d87a7`

**Merge pull request #3 from bigfnj/stream2/s2-sound-module**

```
S2: extract the Sound module (NAudio leaves the base)
```

### 2026-08-06  `eb37dde31`

**feat(plugins): S3a - Fortunes module boundary (engine-only, no bundled content)**

```
Establishes the Fortunes module boundary as a committed green checkpoint before the (larger, ~atomic)
engine relocation. No-op at runtime: the base still owns fortunes until the engine moves, so there is no
behavior change and no regression.

Design (user directive): the Fortunes module ships the ENGINE ONLY - the framework to enable dumb (random
pick) + smart (ONNX/bge-small semantic) fortunes + import/enable - and bundles ZERO fortune content. A
fresh module install is silent until the user adds a pack. The existing ~486KB embedded fortunes.txt stops
being embedded and becomes the canonical importable/downloadable "starter pack" (import now; S7 catalog
later). The bge-small ONNX model is engine/framework (like NAudio for Sound) and will travel with the
module as an asset. Recorded in modules/Fortunes/BACKLOG.md.

- modules/Fortunes/Fortunes.csproj + FortunesModule.cs: id "fortunes" (v0.1.0 until the engine lands),
  references only the ABI; Init/Shutdown are no-ops for now. Builds into runtime modules\fortunes\.
- modules/Fortunes/BACKLOG.md: the engine-only/no-content design + the relocation plan (rebind AppPaths ->
  host.GetStorage, AiSettings fortune fields -> host.GetSettings, ActiveWindow -> host.CaptureScreenContext,
  ONNX model -> beside the module dll).
- build.ps1: build the Fortunes module alongside TestModule + Sound.

Verified: build.ps1 -Release clean (all three modules build); --module-host-selftest loads all three
(fortunes 0.1.0 / sound / testmodule) cleanly; --sound-selftest still PASS. Zero regression.

Next: the engine relocation (FortuneProvider / SmartFortunes / Embedder / FortuneFileImporter + StartUp
glue), rebinding base infrastructure to the ABI - lands as one carefully-verified change.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `fbde9233d`

**feat(plugins): S2 - extract the Sound module (NAudio leaves the base)**

```
First real capability extracted from the base into an isolated .NET 10 plugin. The base no longer
decodes or plays audio and no longer references NAudio; it parses <sound> from pet-XML, carries the raw
MP3 bytes, and hands the selected sound to the Sound module via the published AnimationStarted event. The
Sound module decodes + plays it with NAudio in its own AssemblyLoadContext. No pet-visible behavior
changes when the module is present; without it, pets are simply silent.

Contract (DesktopPet.Contracts):
- AnimationInfo gains SoundData (selected variant's raw MP3 bytes, or null) + SoundLoop. Additive; the
  same lifecycle-event mechanism any third-party module uses. Pet is null on the engine-raised sound path
  (the shared per-type Animations engine has no per-pet identity; sound is global) - documented, and
  backlogged as future work S4's AI reactions will want.

Engine (base):
- TSound becomes a NAudio-free data holder (raw MP3 bytes) + a lightweight LooksLikeMp3 header check
  (ID3 tag / MPEG frame sync) replacing the NAudio decode-probe. AddSound stores bytes; Animations.Dispose
  no longer disposes sounds. SetNextGeneralAnimation hands the selected sound to Animations.SoundSink
  instead of playing it. CompanionXmlValidator + SecuritySelfTest use the header check; the base opens no audio
  device. FormOptions' audio-error status is retired in the base (module health surfaces in S5).
- StartUp wires Animations.SoundSink -> CompanionHost.RaiseAnimationStarted (cleared on Dispose so a torn-down
  host is never retained). NAudio dropped from the csproj, the payload manifest, and the base lock file.

Sound module (modules/Sound, id "sound"):
- net10.0-windows library; references ONLY the ABI (shared) + NAudio (its own dep, copied beside Sound.dll
  with a deps.json so the module's ALC resolves it). Subscribes to AnimationStarted; decodes + plays via
  WaveOutEvent at host.Volume; caches one replayable sound per MP3 byte[] (reference identity, mirroring
  the base's old pre-decoded TSound); disposes all on Shutdown. Device/decoder errors are swallowed so a
  bad/absent audio device never disturbs the host. BACKLOG.md: Spotify/YouTube-Music "now playing"
  (song + artist), and real per-pet AnimationStarted identity for S4.

Verified: build.ps1 -Release clean (base NAudio-free; runtime manifest matches; both modules build);
--sound-selftest PASS incl. "NAudio decodes a real MP3 inside the module's load context" (proves ALC
isolation - zero NAudio in the base); --module-host-selftest (both modules load); --security-selftest
(new base header-check + raw-bytes-carried tests) + --hardening/--pettyperegistry/--smart all PASS; and
the resource-churn soak PASS (6/6 cycles, error=null) on the live app with the Sound module loaded.

Bundling first-party modules into the ZIP/MSI installer payload is a later phase (S6); for now the modules
build into the runtime modules\<id>\ folders (local run + self-tests), not the root payload manifest.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `11326abad`

**Merge pull request #2 from bigfnj/stream2/s1-contracts-host**

```
S1: plugin-host foundation (contracts ABI + loader + live CompanionHost)
```

### 2026-08-06  `a09273d8e`

**feat(plugins): S1c - live CompanionHost bridge + StartUp raises lifecycle events**

```
Completes S1 (the plugin-host foundation). The running app now hosts modules; capabilities stay in
place and untouched - the events just start firing into whatever modules are loaded.

- src/dotNet/Plugins/CompanionHost.cs: the live IHost. Services delegate to the app (SayAll->StartUp;
  Say/TryPlayAnimation->FormCompanion; CaptureScreenContext->ActiveWindow; SpeechEnabled/Volume->Program.MyData);
  per-module Storage/Settings under <DataRoot>\modules\<id>; RegisterDropResponder (priority-arbitrated);
  contributions collected (TrayItems/OptionsPanes) for the WPF-shell renderer; a throwing module can
  never break the host (Safe wrapper). CompanionHandle is the opaque ICompanion over FormCompanion.
- StartUp: creates CompanionHost + ModuleHost in the ctor and loads <baseDir>\modules (isolated failures);
  raises CompanionSpawned (AddSheepCore), CompanionPoked (OnPetPoked, with the escalation count), CompanionLanded
  (LandTimer_Tick), and HostShutdown + unloads modules (Dispose).

Deferred to their consuming phases (per the plan, not gaps): tray/options RENDERING of contributions
(S5), CompanionIdle + AnimationStarted raises (S2/S4), and the real RegisterHotkey registrar (S4).

Verified: build.ps1 -Release OK; --module-host-selftest + security/smart/hardening self-tests pass; the
resource-churn soak PASS (6 cycles of the live app spawning pets + Options/About/Help with CompanionHost +
the test module loaded, error=null) - proving the host + module loading integrate with zero regression.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `a4b86169b`

**feat(plugins): S1b - AssemblyLoadContext module loader + test module + self-test**

```
The plugin loader that binds the ABI. Proves the whole pipeline end-to-end before any capability is
extracted, without touching StartUp (that live wiring is S1c).

- src/dotNet/Plugins/ModuleHost.cs: loads module DLLs from <baseDir>\modules\<id>\, each in its own
  collectible AssemblyLoadContext, with a Load override that shares DesktopPet.Contracts from the
  default context so IModule/IHost types unify host<->module (else casts fail). Per-module dependency
  resolution via AssemblyDependencyResolver. A module that fails to load/init is isolated (logged +
  skipped); ShutdownAll calls Shutdown() + unloads each ALC.
- modules/TestModule/: a real external reference module (references only the ABI, Private=false so it
  doesn't ship a 2nd Contracts). Builds into the runtime modules\testmodule\ folder (build.ps1 step);
  not in the payload manifest (root-only), so not shipped in ZIP/MSI - a dev/self-test artifact.
- --module-host-selftest (src/dotNet/Plugins/ModuleHostSelfTest.cs): loads the test module via the ALC
  loader against a headless recording host and asserts module loaded + id + tray/options contributions
  + a raised CompanionPoked reaches the module (SayAll recorded) + unsubscribe on Shutdown. Skips-pass if the
  module folder is absent. Wired into build.yml.

Verified: build.ps1 -Release OK; TestModule.dll built into modules\testmodule\; --module-host-selftest
PASS (6/6 checks). Existing features untouched.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `96c76ed73`

**feat(plugins): S1a - DesktopPet.Contracts plugin ABI (v1) + integrate/ship**

```
First step of Stream 2 (plugin host). Adds the stable, dependency-free plugin ABI that third-party
module DLLs will reference, and ships it beside the exe so modules bind to the same contract.

- New src/DesktopPet.Contracts (net10.0-windows class lib, AssemblyVersion 1.0.0 = the ABI version):
  IModule (Info/Init/Shutdown) + ModuleInfo/ModulePermissions; IHost with lifecycle EVENTS
  (CompanionSpawned/CompanionPoked/CompanionLanded/CompanionIdle/AnimationStarted/HostShutdown), host SERVICES
  (Say/SayAll, TryPlayAnimation, CaptureScreenContext, RegisterHotkey, per-module Storage/Settings,
  SpeechEnabled/Volume) + an arbitrated RegisterDropResponder, and CONTRIBUTIONS (AddTrayItems,
  AddOptionsPane); ICompanion handle; TrayItem (label/group/order/visible/dynamicText/click/lazy submenu);
  declarative OptionsPane/SettingsSchema/SettingField (secrets write-only); PixelRect/ScreenContext
  value types (no System.Drawing/WinForms/WPF leakage).
- App references it (ProjectReference) and DesktopPet.Contracts.dll is added to runtime-files.txt so
  it flows into the ZIP + MSI. No capability moved yet; existing features untouched.

Verified: contracts compile clean (warnings-as-errors); build.ps1 -Release OK; Contracts.dll ships.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `0109c89a3`

**Merge pull request #1 from bigfnj/migrate/net10**

```
Migrate .NET Framework 4.8 -> .NET 10 (LTS) [Stream 1]
```

### 2026-08-06  `eb93a529f`

**chore(version): bump to 1.1.0 for the .NET 10 migration**

```
Minor bump marking the net48 -> .NET 10 (LTS) runtime migration (behavior parity; framework-
dependent). Stream 1 (M1-M5) is complete: build, packaging (deterministic ZIP + ICE-clean MSI),
CoreTests, all self-tests (incl. the in-process reflection ports), and the resource-churn soak
are green on net10.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `d7b674dad`

**build(net10): M4b/c - port reflection self-tests in-process + update CI**

```
No PowerShell here or on CI runs on .NET 10, so the two LoadFrom reflection harnesses can no
longer host the net10 assembly. Port them in-process:

- src/dotNet/RuntimeHardeningSelfTest.cs: CompanionTypeRegistrySelfTest.Run() + RuntimeHardeningSelfTest.Run(),
  exposed as --pettyperegistry-selftest and --hardening-selftest. AnimationRuntimeLimits (public
  static math) is called directly; non-public members are reached by reflection over the app's own
  assembly, mirroring the original harnesses exactly. Reproduces all 11 registry + 54 hardening
  assertions verbatim.
- tests/pettyperegistry-selftest.ps1: deleted (fully replaced by the in-process flag).
- tests/runtime-hardening-selftest.ps1: slimmed to the source-text invariant checks only (reads .cs,
  no assembly load), so it runs under any PowerShell; the reflection half is the in-process flag.
- .github/workflows/build.yml: drop setup-msbuild (build.ps1 uses dotnet); CoreTests via dotnet build;
  add --pettyperegistry-selftest + --hardening-selftest to the flag loop; self-test step -> pwsh;
  call the slimmed source-invariant script without -ExecutablePath.

Verified locally: build.ps1 -Release OK; all 11 self-test flags pass; CoreTests 23 groups pass; the
source-invariant script passes 5/5.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `fa794b644`

**build(net10): M4a - convert CoreTests to SDK-style net10.0-windows**

```
The custom console regression harness (recompiles AppPaths/AppSettingsStore/RuntimeGeometry) moves
to Microsoft.NET.Sdk / net10.0-windows + UseWindowsForms so the shared production sources compile
in the same environment as the app (System.Drawing; MutexAcl/AccessControl in AppSettingsStore).
GenerateAssemblyInfo stays off (WriteCodeFragment owns AssemblyProduct); added a
[assembly: SupportedOSPlatform(windows7.0)] via that target so CA1416 recognizes the Windows-only
shared sources. Flat bin\<cfg> output preserved for the CI run path. Lockfile regenerated.

Verified: dotnet build -c Release clean; bin\Release\DesktopPet.CoreTests.exe -> 23 groups PASS.

Note: the two PowerShell LoadFrom self-tests (runtime-hardening, pettyperegistry) still target the
net48 assembly under Windows PowerShell 5.1; no net10 pwsh exists to host a net10 assembly, so M4b
ports their reflection sections in-process as app self-test flags.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `4f5b5cdc5`

**build(net10): M3 - WiX installer drops the .NET Framework 4.8 launch gate**

```
Third phase of the net48 -> .NET 10 migration. Remove the HKLM NDP\v4\Full\Release RegistrySearch
+ <Launch Condition> that required .NET Framework 4.8. This is now a framework-dependent .NET 10
app: the MSI installs regardless and the apphost prompts to install the .NET Desktop runtime on
first launch if missing. No .NET 10 launch condition is added (that would wrongly block install).
The manifest-driven RuntimeComponents generator, per-user scope, deterministic SHA-derived
PackageCode, and ICE validation are all unchanged.

Verified: installer/build-installer.ps1 -Config Release -> ICE-clean deterministic MSI on the net10
FDD payload (apphost DesktopPet.exe + DesktopPet.dll + runtimeconfig/deps.json + flattened natives),
major-upgrade rollback boundary verified.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `eb9da41fa`

**build(net10): M2 - build.ps1 on dotnet + payload manifest reconciled to the FDD layout**

```
Second phase of the net48 -> .NET 10 migration. Build tooling + packaging payload; installer (M3)
and tests/CI (M4) still to come.

- build.ps1: replaced the MSBuild.exe / vswhere probing (Find-MSBuild) with the dotnet CLI
  (Resolve-DotnetCli); clean/restore/build now use `dotnet clean`/`dotnet restore`/`dotnet build
  --no-restore`. The manifest set-equality check, ZIP builder, deterministic timestamps, and Clean
  staging-reset are all unchanged.
- csproj: pin <RuntimeIdentifier>win-x64</RuntimeIdentifier> + <SelfContained>false</SelfContained>.
  Still framework-dependent (no bundled runtime), but this flattens package native assets
  (WebView2Loader.dll, onnxruntime.dll) into the output root instead of runtimes\win-x64\native,
  which the flat leaf-name payload manifest + ZIP + WiX generator require. packages.lock.json
  regenerated for the RID.
- packaging/runtime-files.txt: reconciled to the actual net10 FDD output (32 files). Added
  DesktopPet.dll, DesktopPet.runtimeconfig.json, DesktopPet.deps.json, System.Numerics.Tensors.dll
  (a transitive ONNX dep); removed DesktopPet.exe.config, the ~14 now-in-box System.*/Microsoft.*
  shim DLLs, and their DOTNET_* license notices. apphost DesktopPet.exe keeps its name so the
  shortcut special-case + fragment generator are unaffected.

Verified: build.ps1 -Release -Clean -> "Runtime output OK"; build.ps1 -Release -Zip -> deterministic
portable ZIP (37.3 MB). Extracted the ZIP (no runtimes\ subdir) and ran --webview-selftest,
--smart-selftest, --security-selftest from it -> all pass, proving the flat FDD payload runs WebView2
+ native ONNX + DPAPI without the runtimes\ folder.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `05afbf86b`

**build(net10): M1 - convert the app project to SDK-style net10.0-windows (compiles + runs)**

```
First phase of the .NET Framework 4.8 -> .NET 10 (LTS) migration. Project-level only; build
script, packaging, and tests come in M2-M4. Framework-dependent (the apphost prompts for the
.NET Desktop runtime if missing).

- DesktopPet_Portable.csproj: classic -> SDK-style (Microsoft.NET.Sdk), net10.0-windows,
  UseWindowsForms, x64, Nullable/ImplicitUsings disabled, EnableDefaultItems=false with the
  existing explicit Compile/EmbeddedResource/None/Content lists preserved, GenerateAssemblyInfo=false
  with the two ProductVersion.props WriteCodeFragment targets kept, flat bin\<cfg>\x64 output.
  Dropped the GAC <Reference> block, the net48 compiler + reference-assembly packages, the ~10
  now-in-box System.* shim packages, and the dead stdole COMReference. NU1510 confirmed
  ConfigurationManager / ProtectedData / System.Drawing are provided by the net10-windows Windows
  Desktop framework, so no out-of-band packages were added; only ONNX Runtime, WebView2, NAudio,
  and Newtonsoft.Json remain. packages.lock.json regenerated for net10.0-windows7.0.
- app.config deleted: supportedRuntime -> SDK runtimeconfig.json; DPI now set in code.
- Program.Main: Application.SetHighDpiMode(HighDpiMode.PerMonitorV2) before any UI (net10 ignores
  the old app.config ApplicationConfigurationSection, else DPI regresses to SystemAware).
- AssemblyInfoPortable.cs: [assembly: SupportedOSPlatform("windows7.0")] to restore the platform
  attribute GenerateAssemblyInfo=false suppressed, so CA1416 recognizes this as a Windows-only app.
- AppSettingsStore: the cross-process settings mutex's net48 new Mutex(...,MutexSecurity) overload
  -> MutexAcl.Create / MutexAcl.OpenExisting (same current-user ACL intent).
- AiBackendHttpException.StatusCode marked `new` (net10 HttpRequestException now has its own).
- NoWarn WFDEV006 for the dev-only debug right-click menu's legacy ContextMenu/MenuItem (kept for
  parity; modernize to ContextMenuStrip later).

Verified: dotnet build -c Release clean (TreatWarningsAsErrors stays on); the FDD output has the
apphost + DesktopPet.dll + runtimeconfig + deps.json with onnxruntime.dll / WebView2Loader.dll /
bge-small.onnx all present; --security/-catalog/-embed/-smart/-webview self-tests all pass on
net10 (native ONNX + WebView2 + DPAPI + the MutexAcl lock all work); and the WinForms GUI launches
without crashing. On the migrate/net10 branch (master stays net48 until Stream 1 is fully green).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `d6582d8e7`

**feat(options): Preferences RunAtStartup + RestoreDefaultPet controller parity (Phase 1a of full WebView Options)**

```
First increment of converting the whole Options dialog to WebView2: fill the controller-seam gaps
the WinForms Preferences tab had but the seam didn't, so the future HTML Preferences pane can bind
to the controller instead of the form.

- Extract the HKCU "run at startup" logic out of FormOptions into a shared StartupRegistration helper
  (src/dotNet/StartupRegistration.cs). FormOptions' two private methods now delegate to it (no
  behavior change). A DESKTOPPET_STARTUP_TEST_KEY env var redirects the helper to a throwaway subkey
  so a self-test never rewrites or deletes the user's real startup entry.
- PreferencesController: add RunAtStartup to PreferencesState (read in Load) + SetRunAtStartup(bool),
  re-reading to reflect the effective OS state.
- CompanionsController: add RestoreDefaultPet() (loads the built-in pet via the runtime), backing the
  Preferences "Restore default" action.
- Extend --options-selftest: run-at-startup enable/disable against the redirected key, and restore-
  default. Verified the real HKCU Run entry stays untouched and the throwaway key is cleaned up.

Build clean; --options-selftest green (19 checks). No UI change yet (native Options still live).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `d2facb170`

**perf(fortunes): cache the writable fortunes folder in RAM (fixes multi-second Options freeze)**

```
The Fortunes control-center froze the whole Options window for 4-6s on every toggle once a
few megabytes of packs had been downloaded. Cause: the embedded and bundled corpus tiers are
parsed once and cached, but the writable/downloaded folder (LoadCustom) was re-read and
re-parsed from disk on every FortuneProvider.Sources(), Genres(), and pool rebuild - and the
control-center's per-toggle refresh triggered all three. With ~4.9 MB of downloaded packs that
was ~15 MB of disk reads + parsing per click.

Fix: cache the parsed custom corpus in RAM like the other two tiers, but keyed on a cheap
directory fingerprint (top-level *.txt paths + byte lengths + last-write times). An unchanged
folder is a cache hit; any add/edit/remove changes the fingerprint and re-parses automatically,
so downloaded packs and imported files still appear without a restart. This speeds up the
running pet's pool rebuilds too, not just the Options UI.

- New --fortunecache-selftest proves add/edit/remove invalidation + a stable cache-hit read on
  an isolated data root; wired into CI.
- Measured against a copy of the real 4.9 MB fortunes folder, the WebView Fortunes end-to-end
  smoke (init + two full source-set toggles) dropped from 19.5s to 4.1s, and the remaining time
  is one-time WebView2 startup + a single cold parse; the toggles are now instant.
- No behavior change: --filter-selftest, --smart-selftest, --catalog-selftest, --security-selftest,
  --options-selftest, the two WebView smokes, and CoreTests (23 groups) all stay green.

Bumped to 1.0.9 so the version stamp distinguishes the responsive build. Local dev reinstall
only; not tagged/published.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `6a9512ba8`

**chore(version): bump to 1.0.8 for the WebView2 Fortunes dev build**

```
Distinguishes the reinstalled dev build (with the WebView2 Options seam + Fortunes
control-center) from the pre-WebView 1.0.7 that was previously installed. Local dev
reinstall only; not tagged/published.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-06  `d6edf790d`

**feat(fortunes): WebView2 control-center Fortunes tab pilot (Phase 3 of WebView2 work)**

```
Render the Fortunes tab as the Option-3 single pane of glass when the WebView2 runtime
is present; fall back to the full native WinForms tab otherwise. Both bind the same
shared AiSettings and pet runtime, so the two renderings never diverge.

- fortunes-view.html (embedded resource): a data-driven, offline control-center. One
  filterable/sortable installed-source table with an Active column and live totals, a
  right rail for Smart fortunes + status + Rebuild weights, content level (clean/spicy/
  nsfw + spicy-only + no-profanity), and genre chips, plus Apply. Locked-down CSP; no
  external anything.
- FortunesWebView: hosts WebViewHost, binds a FortunesController to the SAME _ai and
  Program.Mainthread the dialog already uses, and bridges JSON both ways (page commands
  to controller; controller state/events to page). A 1.5s timer live-updates the smart
  index status while open.
- FormOptions: BuildFortunesTab now dispatches to the WebView renderer (control-center
  on top, the proven native packs + import strip beneath via a shared splitter) or the
  unchanged native tab. The checksum-verified pack downloads and the file import stay
  native in both renderings; PopulateSources reloads the WebView table after a pack
  install or file import so it stays in sync.
- WebViewHost.ExecuteScriptAsync passthrough for the smoke.
- New --fortunes-webview-selftest: loads the real page, confirms the host pushed state
  and the page rendered rows, and round-trips a JS-to-C# command (Disable/Enable all)
  to prove the bridge and controller are wired. Skips (pass) when the runtime is absent.
  Wired into CI.

Gate: clean build (manifest matches), deterministic ZIP + ICE-clean MSI, CoreTests (23
groups), all eight app self-tests, runtime-hardening + pettyperegistry, and the
resource-churn soak (10 Options-open + 10 open/cancel cycles, each instantiating and
disposing the WebView Fortunes tab; result PASS, no leaks) all green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `1ce850b50`

**feat(options): WebView2 host infra + dependency plumbing (Phase 2 of WebView2 work)**

```
Add the Microsoft.Web.WebView2 SDK and a reusable, hardened host control so the
Fortunes tab (Phase 3) can render as a local, offline HTML control-center while the
other tabs keep their WinForms wiring.

- WebViewHost UserControl: initializes CoreWebView2 with a writable user-data folder
  under the app data root (never next to the read-only installed exe), hardens
  settings (no dev tools, no default context menu, no zoom, no browser accelerator
  keys, no status bar), exposes LoadHtml/PostState + a MessageReceived JSON bridge,
  and a static RuntimeAvailable() (GetAvailableBrowserVersionString) so callers can
  fall back to WinForms when the Evergreen runtime is absent.
- --webview-selftest smoke flag: proves the runtime initializes with our custom
  user-data folder and loads offline HTML; skips (pass) when the runtime is absent.
  Wired into CI beside the other product self-tests.
- Packaging: WebView2 SDK DLLs already flat in the output root (WebView2Loader.dll
  included); ship WEBVIEW2_LICENSE.txt + WEBVIEW2_THIRD_PARTY_NOTICES.txt byte-for-byte
  from the locked package; record both in runtime-files.txt, legal-files.json, and the
  THIRD_PARTY_NOTICES dependency table (WebView2 SDK license, BSD-style, not MIT).
- packages.lock.json regenerated for the new PackageReference.

Gate: clean build (manifest matches output), deterministic portable ZIP, all seven
app self-tests, runtime-hardening + pettyperegistry, CoreTests (23 groups), and a
deterministic MSI (ICE-clean, SHA-derived PackageCode) all green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `c51335706`

**feat(options): renderer-agnostic OptionsController seam (Phase 1 of WebView2 work)**

```
Formalizes the Options logic into a UI-agnostic controller layer under
src/Portable/Options/ (compiled into the exe, since the domain services it
wraps are internal). Four controllers over the existing services:
 - PreferencesController (LocalData get/set + clamping, RandomDrop),
 - CompanionsController (CompanionCatalog + ICompanionRuntime + catalog downloads),
 - FortunesController (FortuneProvider Sources/Genres + AiSettings filters;
   SetSourceActive/SetContentLevel/Apply/RebuildSmartWeights; live totals),
 - AiController (all AiSettings AI fields; API key set/clear via TrySetApiKey,
   never exposing the key — AiState carries only HasApiKey).
Seams: ICompanionRuntime (StartUp now implements it; adds an ActivePetXml prop) and
ICatalogService, both fakeable. State is plain DTOs (public members) so a
future WebView2 view can JSON-serialize them; all validation lives in the
controllers so every renderer behaves identically. No UI is wired to it yet
(no behaviour change). New --options-selftest drives all four with fakes
against an isolated DESKTOPPET_DATA_ROOT (clamping, source round-trip,
no-secret-leak) — 15/15 PASS, wired into CI. Full suite green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `f71b7d6ad`

**docs(backlog): mark smart-fortune routing done; correct the stale note**

```
Rewrites the "Expanded classifications" section to match reality: the 12x12x3
taxonomy already existed, the corpus is adequately tagged (measured: 10,310
embedded, all 12 topics), and the router is now prototype-embedding-based.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `43cf35f9e`

**feat(fortunes): embedding-based topic router (replaces the app-name table)**

```
Smart-fortune topic routing was a hardcoded process-name -> topic table that
covered ~5 app families and only ever emitted 6 of the 12 taxonomy topics, so
most apps got no nudge and half the topics were unreachable. Replace it with a
prototype-embedding router: embed one short sentence per topic once at warm
(as passages, like fortunes), and route the on-screen context to the nearest
topic(s) by centered cosine similarity -- reusing the bundled bge-small
embedder, covering all 12 topics, routing on the actual context instead of the
exe name, and needing no app list. Routing stays a soft score bonus, so a vague
context is still just a gentle nudge. Router.SelfTest now asserts prototype
coverage of the locked taxonomy; --smart-selftest gained behavioral asserts
(a code context routes to "tech", a recipe context to "food" -- a topic the old
router never reached). The corpus itself was measured (10,310 embedded entries,
all 12 topics populated) so no re-tagging was needed. Full suite green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `0fbb3a31a`

**fix(options): align the stacked pet-card action buttons to one width**

```
The "Use this pet"/"✓ Active"/"Use default" and "＋ Add" buttons were each
AutoSize, so the stacked pair was ragged. Pin every action button to a
uniform ActionButtonWidth (92px, within the card's action column) so they
line up as a neat pair on every card.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `017096a02`

**feat(options): "＋ Add" button on pet cards (add-alongside from Options)**

```
Each Pets-gallery card now stacks "＋ Add" beneath its primary control (Use
this pet / Use default / ✓ Active), so you can build a multi-pet mix from
Options, not only the tray. It reuses the tray's add-alongside path
(AddPetFromTray) and reports success/failure in the Pets status line; the
card keeps its fixed width (buttons stacked) so columns stay aligned.

Also fixes a built-in-id bug both here and in the tray: the "eSheep
(default)" entry passed "" (= the ACTIVE pet), so after "Use this pet" it
would add the wrong pet. Both now pass CompanionCatalog.BuiltInPetId ("eSheep")
so a card/tray Add always adds the pet it names. Full suite green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `7abe1f438`

**chore: bump ProductVersion 1.0.6 -> 1.0.7 (v1.0.7-pending; not yet tagged)**

```
Enables a clean local in-place MSI upgrade over the installed 1.0.6 for the
visual smoke of this session's features. Public release (tag v1.0.7) is
deferred until eyeballed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `2daeade27`

**docs(backlog): mark #7/#8/#11/#12/#15 + RDP bubble done; defer #10**

```
Records this session's work: multiple different pets (#7, phases 1+2 + tray),
responsive pet grid + Options width (#8), Options version stamp (#11),
VectorCache prune-on-save (#12), LockBits signature (#15), and the RDP
oversized-bubble fix. #10 (About tab) deferred pending a rendering-approach
decision.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `2e5b13555`

**feat(tray): Add-a-pet / Remove-a-pet submenus for multiple pet types (F6 of #7)**

```
Replaces the single "Add new Sheep" tray item with an "Add a pet" submenu
(built on open from CompanionCatalog.EnumerateLocal -- built-in default first, then
each local pet type by name; disabled at the max-pets cap) and adds a "Remove
a pet" submenu listing the on-screen types with counts ("Pearl x2", "Rick x1").
Adds the StartUp surface the tray drives: OnScreenMix (id->count of live root
pets), AddPetFromTray (spawn one + persist), RemoveOnePet (remove the newest
of a type via KillSheep), IsAtMaxPets; PersistMix now shares OnScreenMix. The
single tray icon still tracks the active pet, and "Remove all pets and Close"
replaces the per-pet close label. Submenus populate lazily on DropDownOpening
so freshly downloaded pets appear without rebuilding the tray. Build + resource-
churn (tray cycles) + CoreTests(23) + registry + hardening harness green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `c66826a9c`

**feat(pets): restore the persisted pet mix on startup + persist changes (F5 of #7)**

```
The autostart tick now spawns from a plan built off the persisted pet mix
(GetPetMix flattened to one id per pet, one spawned per tick, total capped at
MAX_SHEEPS); an empty mix falls back to the classic GetAutoStartPets() copies
of the active pet. StartUp.PersistMix() records the current on-screen mix
(each live root pet under its type id; "" = active) and is called on user-
initiated changes -- KillSheep and the replace-all reset (which clears to the
active type) -- never during the restore itself. Adds LocalData GetPetMix/
SetPetMix (internal; deep-copied, normalized+saved via the existing Update
path). Verified live: a seeded mix of 2 default + 2 pink_sheep restores to 4
pets (the extra type loaded through the registry) and round-trips through
load/save; CoreTests (23) + registry + hardening harness green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `76805b07a`

**feat(settings): schema v2 persists the on-screen pet mix + migration (F4 of #7)**

```
AppSettingsDocument gains a "pets" list (List<CompanionCountEntry> of {id, count})
describing how many pets of each type to restore, and CurrentSchemaVersion
bumps 1 -> 2. Normalize migrates pre-v2 docs by seeding the mix from the old
single AutoStartPets count (id "" = the active/default pet), and validates the
list on every load: drop null/unsafe-id entries (inline charset check -- not
SecureDownload.IsSafeId, which the core test project doesn't compile), clamp
each count to [1,16], dedupe by id (summing), and cap the running total across
all types to 16. MergeChangedFields and Clone handle the list by value so the
cross-process last-writer-wins-per-field contract still holds. Adds three
CoreTests groups (migration, validation, cross-process merge; now 23 groups).
Runtime settings path unaffected (security/smart/catalog self-tests +
hardening harness green).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `b9ba186db`

**feat(pets): reset extra pet types on "Use this pet" replace-all (F2 of #7)**

```
LoadNewXMLFromString (the replace-all behind "Use this pet") now drops every
extra loaded pet type after closing all pets: CloseAllPetsImmediate already
fires each pet's FormClosed (releasing most refs), and registry.DisposeAll() +
petEntries.Clear() clear any stragglers so a swap always resets the desktop to
a single active type. Placed after the close and before the active-pair swap;
it never touches the staged or old active pair (neither is in the registry),
and the failure-rollback path is unchanged. Build + full suite green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `11258bc9b`

**feat(pets): loaded-pet-type registry + reference-counted typed spawn (F1 of #7)**

```
Introduces CompanionTypeRegistry: a UI-thread store of pet TYPES loaded alongside
the active/default pet, each owning a validated (Xml, Animations) pair shared
by every on-screen pet of that type, with a reference count so the pair is
disposed only when its last pet closes (FormCompanion borrows these refs and never
disposes them). StartUp keeps its existing xml/animations as the pinned active
type (unchanged), and gains:
 - AddSheepCore: shared spawn used by both the active path and typed spawns;
 - AddSheep(string id): spawn a specific type alongside others (null/"" = the
   active type; a folder id is loaded on demand via CompanionCatalog.TryReadPetXml +
   the existing TryStageRuntime validation, without Activate() so extra types
   never touch the Animations.Xml "current type" static);
 - decrement on FormClosed (fires exactly once on every teardown path, after
   any kill animation finishes on the shared Animations -- avoiding a use-
   after-dispose), and registry.DisposeAll() in Dispose.
The three spawn entry points are wired in the next step; existing single-pet
behaviour (AddSheep()) is unchanged. Verified: new pettyperegistry-selftest.ps1
(dispose-exactly-at-zero on real Xml/Animations, idempotent double-decrement,
DropIfUnused, DisposeAll) wired into CI; normal spawn via --resource-churn-
selftest; runtime-hardening + CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `1e5824c7a`

**refactor(pets): extract CompanionCatalog for shared enumeration/naming/xml (F0 of #7)**

```
Pet naming (curated character names + pretty folder ids), local enumeration,
and id->animations.xml resolution move into a new internal CompanionCatalog so the
tray and the upcoming loaded-pet-type registry read pets exactly the way the
Options gallery does, instead of duplicating FormOptions' private statics.
FormOptions now delegates naming to CompanionCatalog.DisplayName; the gallery's
author/icon decoration is unchanged. TryReadPetXml resolves the built-in
default (embedded) or a safe folder id (library then bundled, BOM-stripped,
size-bounded) for the caller to validate. Prep for multi-pet (#7); no
behaviour change. Verified: CompanionCatalog naming/xml/traversal-rejection by
reflection, gallery via --resource-churn-selftest, security + CoreTests green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `5b13953cf`

**feat(options): responsive local pet grid + fit Options to the widest tab**

```
The "your pets" list was a single tall column. Flow it into fixed-width
cards that wrap into 2 columns by default and up to 4 as the window widens
(ApplyLocalPetColumns clamps 2..4 on resize), reusing the wrapping-grid
pattern the online catalog already uses. Size the Options window so the
default 2-column Pets layout -- the widest tab -- fits without a horizontal
scrollbar, measuring the real chrome at runtime (FitLocalGridToTwoColumns)
and locking that as the minimum width so no tab ever scrolls right.
Verified the real Options form builds/tears down cleanly under
--resource-churn-selftest (optionsCycles=4). Closes backlog #8.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `cca017800`

**feat(options): show the build version in the bottom-left of Options**

```
Adds a muted "v<ProductVersion>" stamp (Application.ProductVersion, sourced
from ProductVersion.props at build time -- not hardcoded) anchored bottom-
left in the Options window's empty tab-strip gutter, so "which version am I
running?" is answerable at a glance -- directly targeting the stale-build
confusion that has cost time. Uses the (80,80,80) hint-grey sentinel so
WindowTheme remaps it to muted grey in dark mode. Verified the real Options
form builds cleanly under --resource-churn-selftest (optionsCycles=4).
Closes backlog #11.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `c1fb26f87`

**perf(ai): prune VectorCache to the active pool on Save**

```
VectorCache.Save backfilled every remaining on-disk key into the written
snapshot up to the 100k hard cap, so the file could drift toward the cap
with non-active entries even though the in-memory map is active-only. Skip
that backfill when an active pool is set: the write is pruned to the active
set (active keys from other processes are still merged via the active-key
loop), holding the on-disk cache near the active-pool size. The no-active-
pool diagnostics path is unchanged. Verified by --smart-selftest
(VectorCache.SelfTest, incl. the saturated disjoint active-pool case).
Closes backlog #12.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `e9d1423c1`

**perf(ai): read ComputeSignature's 16x16 frame via LockBits, not GetPixel**

```
The screen-change signature downscales to a 16x16 Format24bppRgb bitmap and
read it with 256 GetPixel calls. Replace that with a single LockBits +
Marshal.Copy pass over the locked BGR rows (Stride-padded), preserving the
exact luma weights (R*30 + G*59 + B*11)/100 and the row-major 256-byte
output. Behaviour is identical; the per-pixel COM/marshalling overhead is
gone. Closes backlog #15.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-05  `8cb7e23ca`

**fix(speech): size bubble at the window's own DPI to fix RDP oversize**

```
FormSpeech measured its text box at GetDpiForMonitor(anchor point) but
painted with the window's own device context. At the physical console
those DPIs are always equal; under Remote Desktop the session virtualizes
DPI and a monitor-point query can report a higher value than the window's
real paint DPI (e.g. 120 vs 96 right after a reconnect), so the box was
reserved too large and the text floated in whitespace -- the oversized
bubble seen only over RDP, not at the console.

Add PaintDpi(): once the handle exists, read the DPI from the window
itself via GetDpiForWindow (== GDI+'s actual paint DPI, verified live)
instead of a screen-point monitor query, falling back to the anchor
monitor only before the window is created. Use it in both ShowSpeech and
Reposition; since FormCompanion re-runs Reposition every tick while a bubble is
showing, a mid-show DPI change (an RDP reconnect) self-heals in one frame.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `292766b99`

**BACKLOG: About tab (formatted README in Options) + Options version stamp (#10, #11)**

```
#11 (version stamp, bottom-left of Options) directly targets the stale-build
confusion from this session — the box ran v1.0.1 while fixes shipped in v1.0.2+.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `7eb30c85e`

**Pet gallery names + active badge, Mimiko BOM fix, docs (v1.0.4–v1.0.6)**

```
- v1.0.4: Pets gallery shows each pet's character name (Ben/Gus/Omar/Pearl/Patsu/
  Rick/Yogurt) via one DisplayPetName helper, in both the local list and the
  online download grid; falls back to catalog name then title-cased folder.
- v1.0.5: the running pet's card shows a disabled "✓ Active" badge instead of its
  apply button. IsActivePet matches the item XML to LocalData.GetXml; the gallery
  rebuilds after ApplyPet so the badge follows a switch.
- v1.0.6: fix Mimiko (and any BOM-prefixed pet/user file) download/apply — strip a
  leading U+FEFF in CompanionXmlValidator.TryParse before XmlSerializer. The download
  path decodes bytes via UTF8.GetString, which keeps the BOM (unlike
  File.ReadAllText), so XmlSerializer threw "error in XML document (1, 1)".
- Docs: README "Meet the pets" section (character names + per-pet easter-egg
  table); BACKLOG #7 (multi-pet, phased) / #8 (2-col local list) / #9 (Fortunes
  overhaul) + Mimiko marked done; handoff.md refreshed to v1.0.6.
- Bump ProductVersion 1.0.3 -> 1.0.6.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `10d8dcd38`

**Fix stale-build fortune repetition + harden speech bubble (v1.0.3)**

```
Reported as "cycling through the same few fortunes" again plus oversized
speech bubbles. Root cause was a stale v1.0.1 install predating the
6c774ac fortune-variety fix (TopK=8, no recent-avoidance, always-smart).
Widen variety further and harden bubble sizing for good measure:

- SmartFortunes: TopK 24->32, RecentMemory 16->24. --smart-selftest
  stable_context_distinct now 30/40.
- FormSpeech: measure text height at the target monitor's effective DPI
  (was a fixed 96-DPI Bitmap while rendering on a PerMonitorV2 window,
  wrong on any monitor scaled above 100%); shrink-to-fit width
  (MinContentWidth..MaxContentWidth, was a fixed 220px column);
  AutoScaleMode.None; trim display text.
- Bump ProductVersion 1.0.2 -> 1.0.3.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `10ae362d5`

**docs(handoff): v1.0.2 is now the published release**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `c88d9b936`

**Release v1.0.2**

```
First release since v1.0.1: the finished dark theme, the fortune-repetition
fix, the 4-across pet gallery, and the ~4,870-line codebase optimization audit.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `907fd9862`

**docs: refresh README (dark theme + pet gallery) and handoff for v1.0.2-pending state**

```
- README: note the Options dialogs follow the Windows light/dark theme, and that
  Pets -> Get more pets is a verified thumbnail grid; smart fortunes now avoid
  repeating recently-shown lines.
- handoff.md: rewritten from the stale 2026-07-29 pre-release snapshot to current
  reality (v1.0.1 released + lean CI; master ahead with the dark theme, pet grid,
  fortune-variety fix, and the ~4,870-line optimization audit; v1.0.2 pending).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `01600080c`

**BACKLOG: mark dark-theme + codebase-optimization done; note the pet grid**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `3b78587d9`

**Collapse the StagingPathSafety FinalPathResolver framework to plain PowerShell**

```
The packaging staging layer carried a ~2,530-line embedded C# FinalPathResolver
(directory-chain leases, sealed handles, SetFileInformationByHandle atomic rename,
reparse/hardlink/final-path checks) -- enterprise TOCTOU/supply-chain hardening the
lean hobby-grade release flow had already retired in spirit. Replaced it with plain
PowerShell file I/O that keeps every exported function's name, parameters, and
observable contract (thin IDisposable handle classes), so the 7 caller scripts are
unchanged.

Also removed the dead no-op MutationTestHook (~42 call sites), the dead
Write-DesktopPetNew* helpers, unused build.ps1/build-installer.ps1 parameters, and
the unused provenance block in Install-LockedWixToolchain.ps1.

StagingPathSafety.ps1 4235 -> 645 lines; net -4056 across 7 files.

Independently re-validated: build.ps1 -Release -Zip succeeds and is byte-deterministic
across two builds (ZIP SHA-256 E36697B1...); the MSI builds with deterministic
normalization + major-upgrade schedule + ICE validation green; all five app
self-tests and runtime-hardening pass. Portable-ZIP reproducibility (fixed 1980
timestamps, zeroed external attrs, ordinal-sorted entries) preserved.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-04  `3c57c819e`

**Fix local pet card: bundled icon fallback, aligned buttons, 4-across grid**

```
- Downloaded pets that don't carry an icon.png now fall back to the bundled
  preview by id (LoadPetThumbnail), so e.g. "Red Sheep" shows an icon
  instead of a blank slot.
- Pin the local card's name/author column to a fixed width so the "Use this
  pet" / "Use default" buttons line up in a straight column.
- The "Get more pets" grid is now four tiles across with slightly smaller
  (64px) thumbnails.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `89c76f064`

**Remove two verified-dead StartUp write-only clusters**

```
- aiReady field + AiReady property: nothing read AiReady; the field was
  written on three background-thread paths and read only by the unused
  getter. Removed the field, the property, and all three writes.
- ErrorMessages field + TError struct: written by two audio-error callbacks
  in Animations.cs but never read (the live surfacing path is
  AudioErrors.CurrentMessage via TSound.CurrentErrorMessage). Dropped the
  field/struct and made the two callbacks no-ops -- AudioErrors still
  records internally; publish simply had no live sink.

Full self-test + CoreTests + runtime-hardening suite green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `5d36ab328`

**Optimization audit: remove dead code, unused refs, dead scripts**

```
All verified against a warning-clean build (TreatWarningsAsErrors) plus the
full self-test + CoreTests + runtime-hardening suite (green, 0 failures).

- 4 unused framework references (System.Xml.Linq, System.Data.DataSetExtensions,
  Microsoft.CSharp, WindowsBase) - zero usage in src.
- 2 orphaned source files absent from the portable build (Properties/AssemblyInfo.cs,
  Properties/Settings.Designer.cs; the build compiles AssemblyInfoPortable.cs +
  Settings1.Designer.cs).
- 2 dead packaging scripts: tests/runtime-resource-soak.ps1 (referenced by nothing)
  and packaging/Split-FortunePacks.ps1 (spent one-shot; its monolithic inputs are
  gone) + fixed the stale error hint that named it.
- ~11 zero-caller methods/overloads: FormOptions.webBrowser1_DocumentCompleted +
  TopicTitle, Xml.ReadXML, PackCollections.All, OpenAiCompatBackend.ListModelsAsync,
  FortuneProvider.ParseFortuneFile + TryValidateApprovedAggregate, FortuneFileImporter
  CommitAtomic/RollBackCommittedImports 1-arg wrappers, SmartFortunes two dead
  VectorCache ctors + GetOrEmbed 2-arg wrapper.
- FormCompanion write-only hookTaskbarId + its dead FindWindowEx block, and the uncalled
  GetForegroundWindow/FindWindowEx P/Invokes.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `485e57a87`

**Darken the whole Options tab strip (DarkTabControl)**

```
At larger window sizes / higher DPI the left tab strip's background and
the gutter below the tabs still rendered in the native light colour: the
previous fix relied on the TabControl.Paint event, which does not fire
reliably for the strip. Replace it with a DarkTabControl subclass that
fills the whole client on WM_ERASEBKGND in dark mode, so the owner-drawn
tabs paint on a dark strip regardless of window size or DPI.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `6c774ac1e`

**Fix fortune repetition, finish dark theme, add a pet preview grid**

```
Fortune variety: SmartFortunes.Pick now draws from a 24-wide candidate
set and avoids the last 16 picks, and SayFortune draws from the full
library ~1/3 of the time, so a stable foreground window no longer loops
the same handful of lines. Self-test asserts 24 distinct picks over 40
against one stable context (was ~3).

Dark theme: DarkNumericUpDown answers the inner edit's WM_CTLCOLOREDIT
with a dark brush (the stock NumericUpDown painted white on the dark
form), and the owner-drawn tab strip's gutter is filled to match.
Reverts the SetWindowTheme(" "," ") theme-strip that crashed with
"Visual Style handle creation operation did not succeed". Restores the
eSheep icon on the Preferences "Restore pet" row.

Get more pets: downloadable pets now render as a three-across grid of
tiles with large preview thumbnails instead of a single text column.
The 22 pet icons ship embedded as a zip (39KB) so previews are instant
and work offline.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `740c807c6`

**BACKLOG: log dark-theme colorization bug + codebase-optimization task**

```
GitHub issues are disabled on the repo, so tracked here:
- Bug: the Options dark theme (530dee7, on master, not in v1.0.1) has poor
  colorization -- TabControl chrome/seam, pet-card thumbnail background, low
  hint contrast, and native track bars / combo / NUD dark-mode gaps.
- Task: optimize the codebase after the security cleanup (sweep dead code,
  unused references/usings, and over-built abstractions left by the
  ~50-script strip).
- Mark UI modernization Tier 1 shipped; refresh the stale "release held"
  intro (v1.0.1 shipped via the lean CI).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `530dee747`

**UI: system-following dark theme for the tray dialogs**

```
New WindowTheme helper: detects the Windows app theme (registry), applies
an immersive dark title bar (DwmSetWindowAttribute), and recolors the
control tree (dark surfaces, light text, muted hints, flat buttons) when
the OS is in dark mode -- a no-op in light mode.

Applied to FormOptions (including the owner-drawn left tab strip, plus a
re-theme after the pet gallery rebuilds) and to AboutBox / FormHelp.
Verified the rendering via a capture: dark title bar + dark background +
readable white/muted/green text + themed checkboxes, textbox, and button.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `3fbcf0269`

**Docs + v1.0.1: reconcile docs with the lean pipeline, bump version**

```
- Readme.md: real (unsigned) release + install + lean build/CI instructions;
  drop the "release blocked by rights gates" framing and the deleted
  Invoke-ProductSelfTests / locked-restore commands.
- docs/RELEASE-CHECKLIST.md, docs/rights/README.md, PROVENANCE.md: replace
  the retired enterprise-process docs with lean stubs (a release is a git
  tag; verify SHA256SUMS; provenance is documented, not gated).
- THIRD_PARTY_NOTICES.md: drop the deleted source-rights / SBOM machinery
  references; keep the dependency table + license artifacts.
- ProductVersion.props: 1.0.0 -> 1.0.1.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `3f5a41346`

**CI: remove orphaned enterprise pipeline scripts**

```
Delete the release gate, SBOM/Syft toolchain, source-rights evidence, NuGet
audit policy, reproducibility checks, MSI lifecycle/upgrade gates, and the
enterprise self-tests -- 50 files that nothing references after the lean
CI/release rewrite.

Kept: the app's own defensive code and its self-tests (runtime-hardening,
CoreTests, the --*-selftest suite), plus the build scripts they depend on
(build.ps1, build-installer.ps1, StagingPathSafety, deterministic ZIP, WiX
toolchain, content-catalog tools).

Verified locally end-to-end after deletion: build Release + ZIP, all
product self-tests, runtime hardening, and the MSI all pass.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `a7d439ccd`

**CI: wait for GUI self-test exit codes with Start-Process -Wait**

```
The product is a GUI-subsystem exe, so `& $exe --selftest` returns to the
shell immediately with no captured exit code; the lean CI self-test loop
therefore never saw the real result. Use Start-Process -Wait -PassThru and
check .ExitCode so each self-test actually blocks and is verified.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `10b059b34`

**CI: strip to a lean hobby-grade build + release**

```
Replace the enterprise CI/release machinery (release gate, SBOM/Syft +
SPDX validation, code-signing pipeline, source-rights evidence gates,
NuGet exact-schema audit, reproducible double-builds, deterministic
ICE-validated MSI, TOCTOU staging/retained-handle self-tests, MSI
lifecycle + payload parity, rights-approval release variable) with a lean
pipeline appropriate for a hobby project:

- build.yml: build Release x64 + portable ZIP, run CoreTests and the app's
  own self-tests (filter/security/catalog/smart/fullscreen + runtime
  hardening), build the MSI, upload both artifacts.
- release.yml: on a vX.Y.Z tag, build + package ZIP/MSI/SHA256SUMS and
  publish them on a GitHub release. No signing, no rights sign-off.

The app's own defensive code (safe XML parsing, SafeExpression parser,
child caps, fullscreen relocate) is unchanged -- it's cheap and stops real
crashes. Dead enterprise scripts are pruned in a follow-up commit.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `73a82ab79`

**CI: runtime-hardening self-test re-launches under Windows PowerShell 5.1**

```
The MSI-lifecycle step (Test-MsiLifecycle -> Invoke-ProductSelfTests) runs
the installed product self-tests under pwsh 7, so runtime-hardening-selftest
hit the same BinaryFormatter-disabled failure as the consolidated-tests step.

Rather than fix every CI step's shell, make the harness self-correct: when it
detects PowerShell 7 (PSEdition Core) it re-launches itself under Windows
PowerShell 5.1 (.NET Framework), which hosts the shipped net48 assembly the
way it actually runs. This covers every caller (build.yml + release.yml MSI
lifecycle, and any future step) in one place. The 5.1 path is unchanged (it
skips the relaunch); verified EXIT=0 locally.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `13b19f84e`

**CI: create missing intermediate dirs in the staging reset on a fresh tree**

```
Reset-DesktopPetStagingDirectory retained the *existing* parent chain down
to the target's parent (TOCTOU-safe), creating only the allowed root and
the leaf. A deep staging target such as build\installer-staging\release\runtime
therefore failed on a fresh checkout ("Could not retain a staging directory
handle: build\installer-staging"), because the intermediate levels between
build\ and the leaf did not exist -- which broke the deterministic MSI
self-test's build-installer run in CI.

Walk from the target's parent up to (but not including) the allowed root
and create each missing intermediate through the same retained-parent-chain
creation used for the allowed root and the leaf. A direct child of the
allowed root stays a no-op. Verified: deep and direct-child resets, a full
fresh-tree build-installer (MSI OK), and both staging security self-tests
(hard-link/junction/ancestor-swap rejection intact) pass locally.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `9832d16fe`

**CI: doc-boundary test asserts the retired pack-rights reality**

```
The documentation-boundary self-test (run by Invoke-ProductSelfTests, not
the local release gate -- which is why the earlier doc reconciliation did
not catch it) still required packs/README and RELEASE-CHECKLIST to describe
the retired per-pack structured rights-evidence contract
(sourceRepository/sourceRevision/licenseExpression/...). That machinery was
intentionally retired when fortune packs moved to the runtime catalog.json.

Assert the new boundary instead: the README states the per-pack rights gate
was retired and that pack rights are reviewed by hand; the checklist states
there is no automated gate, integrity-only, manual review. Passes locally.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `74d0fabb6`

**CI: run product/runtime-hardening self-tests under Windows PowerShell 5.1**

```
The consolidated self-tests load the shipped net48 DesktopPet assembly and
instantiate its WinForms types via [Activator]::CreateInstance. WinForms
resource loading uses BinaryFormatter, which modern .NET disables -- so the
step failed under `shell: pwsh` (PowerShell 7) with "BinaryFormatter
serialization and deserialization are disabled". Windows PowerShell 5.1
hosts the .NET Framework assembly the way it actually runs; the harness
passes there (verified locally, EXIT=0). Switch the shell to `powershell`
in build.yml and the mirror step in release.yml.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `41461595b`

**CI: skip the clean staging reset when the output dir is absent**

```
build.ps1 -Clean reset the configuration output
(build\DesktopPetPortable\bin\<cfg>\x64) via Reset-DesktopPetStagingDirectory,
which retains the *existing* parent directory chain to stay TOCTOU-safe.
On a fresh checkout only build\ exists, so opening the chain failed at
build\DesktopPetPortable ("Could not retain a staging directory handle").
Locally it only worked because prior builds had left the intermediate dirs.

On a fresh tree there is no stale runtime state to clear, so guard the
reset with Test-Path; the subsequent build creates the output normally.
Verified both fresh (skips reset) and incremental (runs reset) locally.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `0e3ae378b`

**CI: pin .NET SDK to 10.0.201 so the NuGet audit sees the framework**

```
SDK 10.0.302 (the windows-2025 runner default) emits an empty `framework`
in `dotnet list package --format json` for the legacy (non-SDK) net48
project, so the release gate's NuGet full-inventory audit rejected it
("framework must be a non-empty string"). 10.0.201 reports net48
correctly (verified locally, including the exact --no-restore path).

Add a repo global.json pinning the SDK (rollForward latestPatch) and a
SHA-pinned actions/setup-dotnet step in build.yml so the runner uses the
known-good SDK. release.yml's dotnet jobs need the same setup-dotnet step
before that pipeline is dispatched (tracked separately).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `68b91e7e0`

**CI: resolve a single git.exe in release-gate self-tests**

```
GitHub-hosted runners expose several git.exe on PATH, so
`Get-Command git -CommandType Application` returns an array and `.Source`
yields a string[] that fails to bind the [string]$GitPath parameter
("Cannot convert value to type System.String"). This reddened the gate at
the source-rights -SelfTest step (masked until the prior self-test fix).

Pipe through Select-Object -First 1 to take the effective git in
Test-SourceRightsEvidence.ps1 (main path + self-test fixture) and the
corpus-provenance self-test. Parses clean; -SelfTest passes locally.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `2540948de`

**CI: fix release-gate self-test that reddened every build.yml run**

```
The tracked-input self-test (run by Invoke-ReleaseGate.ps1) exercised
New-ReleaseGatePathPolicy -AllowDirtyDevelopment as a development fixture
without neutralizing CI detection first. GitHub Actions always sets
GITHUB_ACTIONS (and the self-test only ever managed $env:CI), so the CI
guard threw at the fixture before the negative control could run, turning
every build.yml run red at the quality-gate step.

Clear GITHUB_ACTIONS + CI around the development fixture (restored in a
finally); the negative control still re-enables CI detection to prove the
override stays disabled there.

Reproduced with GITHUB_ACTIONS=true (throws) then passes after the fix;
the local non-CI run still passes.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `228efdcef`

**Fullscreen-aware pets: relocate off a monitor a game covers**

```
The old CheckFullScreen only demoted TopMost while the fullscreen app was
the *foreground* window. Grabbing the main sheep (which lacks
WS_EX_NOACTIVATE) makes the pet the foreground window, so a borderless
game underneath was no longer detected and the sheep stayed on top after
being dragged onto that screen.

Detection is now foreground-independent. FullscreenScan.BlockedMonitors
walks the z-order (EnumWindows), ignoring the pets themselves, the shell,
cloaked, minimized, and invisible windows, and marks a monitor blocked
when the topmost real window covering its center fills it (borderless or
exclusive). When the pet's own monitor is blocked it now relocates to the
nearest free monitor (DesktopGeometry.ChooseRelocationTarget +
RelocateToDisplay re-spawn) instead of just demoting; with no free
monitor (single screen / all blocked / a child) it hides so it never sits
on the game. Runs on a 300 ms throttle (independent of animation frame
timing) with 1.2 s relocation hysteresis, and restores on exit.

- StartUp.SheepHandles() exposes every live pet window for exclusion.
- Play() honors a forced display index so relocation isn't re-randomized
  under multiscreen.
- Tests: ChooseRelocationTarget cases in CoreTests; --fullscreen-selftest
  diagnostic. Re-pin @engine-source.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `29191c9cf`

**Docs: reconcile pack rights docs with the retired per-pack rights gate**

```
Retiring the embedded pack catalog (packs.json + TrustedPackCatalog +
pack-rights-evidence.json + Test-PackRightsEvidence.ps1) also removed the
fail-closed per-pack redistribution-rights gate; the 152 per-source packs
are now served via the runtime catalog.json, which verifies per-file
SHA-256 integrity but not rights. Several docs still described the deleted
gate as active.

- packs/README.md: rewrite for the per-source packs + collections.json
  grouping + runtime catalog delivery; add an explicit "retired the
  per-pack rights gate" section (integrity is checked, rights are not).
- docs/rights/README.md: drop the pack-rights-evidence.json /
  Test-PackRightsEvidence.ps1 / per-pack rights-doc machinery; keep the
  six source scopes (still gated); flag pack rights as a manual
  pre-release review.
- docs/RELEASE-CHECKLIST.md section 1: replace the per-pack approval
  BLOCKER with a hand-review requirement.
- THIRD_PARTY_NOTICES.md: same reconciliation for the packs bullet.
- New-ContentCatalog.ps1: fix a stale comment (reads collections.json,
  not packs.json).
- BACKLOG.md: mark the grouped-tree UX (#1) and per-source pack split (#6)
  DONE.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `3e22103cc`

**Smart fortunes: progressive warming (matchable prefix while embedding)**

```
Previously the picker was all-or-nothing: WarmCore embedded the entire
active pool, then flipped _ready, so every Pick returned random until the
last vector landed -- a long cold-start on the 152-pack bundled pool.

Now WarmCore publishes in doubling batches (512, 1024, 2048, ...). After
each batch it re-centers the embedded-so-far prefix against the running
mean and atomically exposes it; Pick matches against whatever is warm and
falls back to random for the un-embedded rest, so contextual matching
starts working early and keeps improving as it warms. Re-centering the
growing prefix is cheap next to the ONNX embed it runs behind.

- Track _indexed (matchable lines) + _warmComplete (whole pool done);
  expose both tear-free via WarmProgress(...).
- Status UI: "indexing... X of Y lines ready (random for the rest)"
  during warm, then "ready . Y lines indexed".
- SelfTest now waits for full completion and asserts the progress fields.
- New opt-in --smart-progress-selftest warms a >512 cold-cache sample and
  proves a pick lands on a partially-warmed pool (sawPartialPick), with
  monotonic index growth.
- Re-pin @engine-source for the edited engine sources.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `1fa19284c`

**Provenance: re-pin @engine-source; correct pre-existing art-set drift**

```
- @engine-source: the per-source-packs UI change (FormOptions + new
  PackCollections.cs) altered the engine-source set, so its aggregate hash is
  updated to match.
- @bundled-art and @downloadable-pet-art: their pins had drifted from the actual
  first-party eSheep assets (last changed in 300694b) while the gate was red on
  the corpus duplicate, so nobody caught it. Re-pinned to the current bytes; I
  did not touch any art file this session.

Release gate now passes clean (documented rights-approval blockers aside).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `53ba51be3`

**Fortunes UI: group Sources + packs by collection; grouped download tree**

```
Makes the 152 per-source packs browsable now that the flat lists would be
unwieldy.

- PackCollections.cs: reads the embedded collections.json (source -> collection),
  available offline. csproj compiles it.
- FriendlyName: curated display names for the last cryptic/compound sources
  (BOFH, Dad Jokes, Kurt Godel, Terry Pratchett, Songs & Poems, Zippy, ...); the
  rest render cleanly via title-case or the "(adult)" off- fallback.
- Sources tree: regrouped by collection instead of topic (so all of a show's /
  author's siblings sit together), with a live filter box and an "N of M sources
  - L lines" total. The disabled-set sync is now merge-based so sources hidden by
  the filter keep their on/off state.
- Fortune packs: replaced the flat "check online -> download all" confirm dialog
  with a grouped, tri-state download tree. "Check online for packs" fetches the
  runtime catalog and lists every pack by collection (installed ones marked +
  green); check the ones you want and "Download checked" fetches, verifies, and
  installs them, then refreshes.

Verified: build clean; security + filter + catalog self-tests exit 0; resource
churn PASS (Options with both trees built/torn down 22x, negative growth).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `f434e8e7c`

**Retire the embedded pack catalog; dedup the embedded corpus**

```
The runtime catalog (catalog.json) is now the sole source of truth for fortune
packs, so the embedded commit-pinned catalog and its whole validation chain are
removed:

- Delete packs.json, TrustedPackCatalog.cs (embedded loader), Test-PackCatalog.ps1,
  Test-PackRightsEvidence.ps1, and pack-rights-evidence.json (an empty scaffold).
- Keep the TrustedPack model (TrustedPack.cs) that the install path still uses.
- csproj: embed collections.json (DesktopPet.Collections.json) instead of the
  pack catalog; compile TrustedPack.cs.
- SecuritySelfTest: drop the ~200 lines that exercised the embedded catalog
  (TryLoad/TryValidateRevision/DistributionUrl/LoadLimits); the branch-URL
  rejection and everything else stay.
- Invoke-ReleaseGate: drop the embedded pack-catalog + pack-rights checks.
- label-apply/label-selftest: the dependent-metadata list points at catalog.json
  + collections.json now; label self-test still PASS.
- FormOptions: remove the embedded "Fortune packs" list + reload/install-checked
  buttons and their download path; online "Check online for new packs" (runtime
  catalog) remains.

Also removes the pre-existing duplicate at fortunes.txt lines 10036/10037 that
blocked the release gate (accidental identical showerthoughts line; 0 dups now).

Verified: build clean; security + filter + catalog self-tests exit 0; resource
churn PASS (Options built/torn down 15x, optionsCancellationCycles 15).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `ddbbda152`

**Fortune packs: split into per-source packs + collection grouping**

```
Splits the 12 monolithic packs into 152 per-source packs (one file per column-1
source tag), so online users can download individual shows/authors instead of a
whole collection. Content-preserving: the sorted union of the per-source files
is byte-identical to the originals (50,860 lines), each file's column-1 is
uniform, no source appears in two packs.

- Split-FortunePacks.ps1 (new): partitions each pack by source, writes
  packs/<source>.txt, emits packs/collections.json (each original pack becomes a
  named collection listing its member sources), removes the monolithic files
  (single-source packs like bofh/dadjokes/showerthoughts keep their name).
- catalog.json: 152 per-source packs, each carrying a `group` (its collection)
  for grouped browsing; New-ContentCatalog.ps1 now sources collection metadata
  from collections.json. RemoteCatalog parses the optional `group` field.

The embedded packs.json stays frozen and valid (it is pinned to an immutable
commit that still has the monolithic files), so its whole validation chain
(SecuritySelfTest, the release gate, labeling) keeps passing untouched; it just
stops driving the UI. New content flows through the runtime catalog only.

Verified: filter + catalog self-tests exit 0; catalog parses (22 pets + 152
packs); resource-soak PASS with 152 bundled files.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `6b8f17346`

**Catalog: hash the committed blob so sha256 matches what raw serves**

```
The assets were committed with mixed line endings (some CRLF, some LF), so
hashing the working-tree copy (or an LF-normalized copy) gave wrong sizes/hashes
for a subset of pets, which the app would then reject. The generator now hashes
the actual git blob (git cat-file blob HEAD:<path>), with an LF-normalized
working-tree fallback for a not-yet-committed asset. Regenerated catalog.json;
all 34 assets now hash-match raw.githubusercontent.com exactly.

Adds --online-selftest: a live smoke test that fetches the real catalog and
downloads+verifies the first pet and pack through the app's own code path
(verified: pets=22 packs=12; bbunny + bofh downloaded, verified, and validated).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `b7fe0adb7`

**Catalog: target the master branch (the repo has no main)**

```
The catalog URL, generator default, and every asset URL used a main ref, but the
default branch is master, so the runtime fetch would 404. Point them at master
and regenerate catalog.json.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `ee9d9c09c`

**Online downloads: runtime-fetched catalog for pets + fortune packs**

```
Adds the online side of the content system. New pets or packs pushed to the repo
appear in the app live, without shipping a new build, because the catalog is
fetched at runtime over HTTPS rather than embedded. Every download is verified
against a published SHA-256 and validated before install; the offline bundled
content is the fallback, and the catalog only reveals what is not already local.

- SecureDownload: TryValidateBranchRawGitHubUrl -- a branch/tag raw.github URL
  validator for the mutable catalog + its assets (the pinned-commit validator is
  untouched for the embedded pack catalog). Integrity is anchored by SHA-256.
- RemoteCatalog(.cs): fetches catalog.json over HTTPS (bounded, no redirects),
  parses + strictly validates pets and packs (safe id, host/owner/repo, sha256,
  size/count bounds, url matches id), and downloads-and-verifies one asset.
  --catalog-selftest covers accept + five reject cases; --catalog-parse-file
  validates a real catalog against the runtime parser.
- catalog.json (new) + New-ContentCatalog.ps1 (new): the catalog listing all 22
  pets + 12 packs with branch URLs, SHA-256, and sizes, plus the generator that
  regenerates it by hashing the current files (LF-normalized, so the working-tree
  hash equals what raw.githubusercontent.com serves). Run it after adding content.
- AppPaths.LibraryPetsDirectory: writable pet library (<DataRoot>\pets) for
  downloaded pets, enumerated in the gallery alongside the read-only bundled pets.
- FormOptions: "Check for pets online" adds downloadable pets to the gallery
  (download -> sha256 -> CompanionXmlValidator -> install to the library -> re-render);
  "Check online for new packs" fetches the catalog, offers packs you don't have,
  and installs them into the custom fortunes folder via the existing atomic
  importer. Both reuse a single cached catalog fetch.

Verified: filter + catalog self-tests exit 0; catalog.json parses through the
runtime parser (22 pets + 12 packs); all 34 asset hashes match (packs vs the
CI-verified embedded catalog, pets vs the working tree); resource-soak PASS with
the new online buttons present (Options cycled 15x, negative resource growth).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `6fdd99621`

**Packaging: bundle all pets + fortune packs into the portable ZIP (offline)**

```
The portable build now ships the full offline content beside the exe, so it
never needs the network to change pets or use the big fortune packs. The MSI
stays lean and pulls extras on demand.

- New-DeterministicPortableZip.ps1: optional, additive -ContentDirectories.
  Each declared subtree is added deterministically as '<Prefix>/<relative>'
  with every path segment revalidated as a safe Windows leaf name, and joins
  the same sorted entry set so per-entry readback verification covers it. Callers
  that omit it are byte-for-byte unchanged.
- Stage-BundledContent.ps1 (new): single source of truth for what the portable
  build carries -- every pet folder (animations.xml + icon.png) + pets.json, and
  the 12 fortune packs. build.ps1 and the release workflow both stage through it.
- build.ps1 -Zip: stage + bundle via the helper (portable only).
- Test-RuntimePayload.ps1: -AllowedExtraDirectories tolerates the bundled subtree
  for the ZIP; the MSI payload check passes none, so the installer stays exact.
- Test-PackagedPayloads.ps1: allow pets/fortunes for the ZIP, not the MSI.
- release.yml: stage content into the signed final-zip assembly and allow the
  subtree in both direct portable-payload verifiers (Test-PackagedPayloads and
  the N-1/SBOM paths read manifest files by name, so they were already safe).
- deterministic-portable-zip-selftest.ps1: bundled-content case (determinism,
  nested entries, gate acceptance, unsafe-prefix rejection).

Verified locally: build.ps1 -Release -Zip -> 101-entry 37.3 MB zip (22 pets +
12 packs); extracted, payload gate PASS (43 runtime + marker + 57 bundled),
filter-selftest exit 0, and resource-soak PASS from the extracted portable exe
(all handle/GDI/USER growth negative). Zip + entrypoints + staging-mutation
self-tests all PASS.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `6f32988e9`

**Pets/fortunes: offline bundling (app side) + revive the Online-pets tab**

```
Adds a read-only "assets beside the exe" tier for the portable build, and turns
the fail-closed "Online pets unavailable" tab into a working local pet picker.

- AppPaths: BundledPetsDirectory (<exe>\pets) and BundledFortunesDirectory
  (<exe>\fortunes). Absent in the lean MSI install; every consumer tolerates
  their non-existence.
- FortuneProvider: load bundled <exe>\fortunes\*.txt as an additional read-only
  source (via a single LoadStandardCorpus used by the pool, Sources(), and
  Genres() so they never diverge). Parsed once and cached like the embedded
  corpus, since re-parsing ~7 MB of bundled packs on every Options open is
  otherwise a real slowdown.
- FormOptions: replace ShowOnlinePetsUnavailable() with BuildPetGallery() -- a
  local gallery of the built-in default plus every bundled pet, each with an
  icon thumbnail. "Use this pet" reads the animations.xml, runs it through
  CompanionXmlValidator, then applies it via StartUp.LoadNewXMLFromString (which
  validates again), so a bundled file is never trusted blindly.

The online (runtime-fetched catalog) download path and the portable-zip
packaging that ships the bundled content are the next steps.

Verified: filter-selftest (216) exit 0; resource-soak PASS with 22 bundled pets
+ 12 bundled packs staged beside the exe (Options built/torn down 15x, negative
handle/GDI/USER growth).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `8d3634191`

**Options: widen dialog, replace sliders with number boxes, regroup custom fortunes**

```
Preferences tab:
- Widen the Options window to 700x600 (min 560x430) so descriptions and the
  AI tab no longer clip or require horizontal scrolling.
- Replace every TrackBar (volume, pets-at-startup, size, speech seconds,
  random-drop interval/jitter) with editable NumericUpDown controls, keeping
  the original bounds and save/revert behavior. Size still applies on commit
  so it will not restart mid-edit. Audio errors now show inline in red and
  keep the volume box disabled when audio is unavailable.

Fortunes tab:
- Move "Add fortunes..." / "Open folder" out from under the Genres heading
  into a new bold "Add your own fortunes" section placed after Fortune packs,
  with copy explaining upload by file/folder, accepted formats, and what the
  background intake/embedding does (and why large libraries take a while).
- Give the import its own status label so feedback no longer lands next to
  the unrelated Apply button.

Verified: filter-selftest (216 cases) exit 0; runtime resource-soak PASS
(Options built/torn down 26x with negative handle/GDI/USER growth); Release
MSI rebuilt + ICE-validated.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-08-03  `61a4f39bc`

**Backlog: record per-show pack split (#6) + confirm pet-download scope**

```
Defer the two large Tranche-2 content/feature builds with concrete plans:
- Per-show fortune packs: feasible (lines carry a per-show source tag in col1;
  22 shows in tv-mature alone) but a ~50-pack split + catalog + tree UI.
- Secure pet downloads: Pets/ is committed (servable); each pet is animations.xml
  + icon.png, so the download is multi-file. Deferred to avoid rushing.
```

### 2026-08-03  `c2ee6f4e8`

**Packs: approve the 12 fortune packs for install (remove HELD)**

```
Per the owner's redistribution approval, flip every pack to installable in the
runtime catalog: catalog revision pinned to cdffac2 (where packs/*.txt match
their sha256), each pack redistributionApproved=true with a commit-pinned raw
GitHub download URL, and a LicenseRef-DesktopPet-Community grant so the catalog
validator accepts them. 'Install checked' now downloads each pack from its pinned
URL and verifies the sha256 (validated: all 12 local + remote pass).

Note: this is the RUNTIME catalog only. The release-gate rights evidence
(packaging/pack-rights-evidence.json + docs/rights/<pack>.json) is intentionally
NOT auto-filled -- that formal per-source license record is the owner's call.
```

### 2026-08-03  `0db056105`

**Preferences v2: unified grid, Speech merged, Run-at-Startup placed, random drop**

```
- Rebuild the Preferences tab as consistent control+description rows (reusing the
  wired designer controls, so no settings regress). Run-at-Startup now sits right
  under Restore pet with a description.
- Merge the Speech settings (enable bubbles + duration) into Preferences and
  remove the standalone Speech tab. Tabs: Preferences, Online pets, Fortunes, AI.
- New 'Randomly drop a fortune or insight' (AiSettings.RandomDrop{Enabled,Minutes,
  JitterMinutes}): the sheep speaks on its own every N +/- J minutes -- a fortune
  when the AI brain is off, an AI insight when it is on. Runtime timer in StartUp
  (StartFortuneGeneration -> ApplyRandomDrop, random reschedule, gated on pet
  present + speech on + not busy). UI: enable + typed interval (1-9999) + jitter
  slider (capped below the interval).

Verified: build clean; --filter-selftest 216 PASS; --resource-churn builds the
Options dialog 4x, zero errors.
```

### 2026-08-03  `2ade2aa68`

**Options UI: Preferences tab + Run-at-Startup + alignment/scroll fixes**

```
- Merge the designer 'Animation options' + 'Application' tabs into one scrollable
  'Preferences' tab (TableLayoutPanels reparented intact, so existing wiring is
  unchanged), placed first; tab order is now Preferences, Online pets, Speech,
  Fortunes, AI.
- Add a 'Run at Windows startup' checkbox (per-user HKCU Run key; no admin).
- Fix Fortunes button-row alignment (first-button top margins), drop the
  misleading counts from the Genres toggles, and add trailing spacers so the
  AI/Fortunes panels' AutoScroll reveals the last control at any window size.

Verified: build clean; --resource-churn builds the Options tabs 4x, zero errors.
```

### 2026-08-03  `d3ab6bd9c`

**Backlog: park secure online-pet-downloads re-enable with wiring pointers**

```
Defer re-enabling online pet downloads to the backlog (the Options tab stays
fail-closed and safe). Record the diagnosis + every existing piece to reuse so a
future pass loses nothing: SecureDownload.cs, CompanionXmlValidator.cs, Pets/pets.json
(legacy catalog to harden), and the fortune-packs system as the template.
```

### 2026-08-02  `c3e46c3e0`

**CI: pre-create installer staging dirs on fresh runners; idempotent publish**

```
The installer's staging reset validates the full path chain but only creates a
leaf, so build\installer-staging\release must pre-exist -- true locally (dirs
linger) but not on a fresh runner. Pre-create the two staging leaf dirs before
the MSI build (the reset still validates + rebuilds them; MSI stays byte-identical).
Also make the publish step create-or-clobber so re-runs never hard-fail. Both
verified locally by reproducing the fresh-checkout failure and the fix.
```

### 2026-08-02  `17e01896b`

**CI: tolerate an empty global WiX extension cache on fresh runners**

### 2026-08-02  `41f1afa36`

**CI: drop -Clean from release build (fresh checkout has no build/ to reset)**

### 2026-08-02  `cd8aec3a4`

**CI: fix MSBuild selection for VS 2026 runners; re-pin @engine-source**

```
- build.yml + publish-release.yml: the windows-2025 image moved to Visual Studio
  2026 (18.x), so setup-msbuild's vs-version '[17.0,18.0)' found nothing ("Unable
  to find MSBuild"). Widen to '[17.0,19.0)' (VS 2022 or 2026).
- source-rights-evidence.json: refresh the @engine-source closure hash to match
  this session's source edits (LF-canonical). releaseApproved stays false --
  approvals remain the maintainer's call. Unblocks the release gate's hard hash
  check without granting any right.
```

### 2026-08-02  `b434870f6`

**Release 1.0.0: version bump + simple unsigned publish workflow**

```
Set the product version to 1.0.0 for the first public release, and add a
lightweight tag-triggered release workflow that builds the two Windows x64
artifacts and publishes them to a GitHub Release:

- ProductVersion.props: 2.0.0 -> 1.0.0 (single source of truth; the MSI/build
  read from here). PROVENANCE/THIRD_PARTY_NOTICES version strings follow.
- .github/workflows/publish-release.yml: on a vX.Y.Z tag (must match the props
  version), build the portable ZIP (build.ps1 -Release -Zip) and the per-user
  MSI (installer/build-installer.ps1 after the locked WiX 5.0.2 toolchain),
  generate SHA256SUMS, and publish an unsigned GitHub Release with all three.

Unsigned by design (hobby release; SmartScreen "Run anyway"). The heavy signed
pipeline in release.yml is untouched and stays manual-dispatch only. Both
artifacts were verified building locally at 1.0.0 (MSI ICE-validated).
```

### 2026-08-02  `5f49b0468`

**Polish: expand poke-sass lines; mark stale backlog items resolved**

```
- Ai/PokeReactions.cs: grow the poke-escalation sass from a 12-line seed to ~35
  for more variety on repeated pokes (no-repeat picker already handles it; one
  line nods to the 12th-poke bathtub escape).
- BACKLOG.md: record that the "deferred audit" notes are already handled by the
  cleanup pass -- #17 (no stale app.config redirects), #12 (VectorCache is capped
  + prunes when full), and the land-greeting timing (settle-polling, not fixed
  3s). #15 GetPixel stays won't-fix (16x16 px, negligible).

Build clean.
```

### 2026-08-02  `cd98a6695`

**Readme: credit the fortune sources and thank contributors**

```
Replace the rights-blocker framing in "License & credits" with a gracious
Sources & thanks section attributing the engine (Adrianotiger/desktopPet), the
bge-small embedder (BAAI), the aggregated fortune corpus (JKirchartz/fortunes
and the notable works behind it), and every fortune pack's sources.
```

### 2026-08-02  `cdffac2aa`

**Fortunes: flip the shipped corpus + packs to schema v2**

```
Deliberate v1 -> v2 flip (approved). The bundled corpus and all 12 packs now
carry real per-fortune topic + genre (schema v2: source, topic, genre, level,
prof, text) instead of the compat-mapped approximations, so the genre filter,
the grouped source tree, and smart-routing run on actual labels and the new
`health-body` topic is live at runtime.

Applied via the gated two-phase label-apply (--emit-plan reviewed, then --go
with the hash-pinned metadata plan). Source/level/prof/text preserved
byte-for-byte; only topic/genre were added. Runtime now embeds schema=2
(taxonomy 2026-07-31); filter/resource-churn self-tests pass.

Release-apparatus finalization (schema pins only; no rights granted):
- source-assets.json: fortunes.txt tsv-5 -> tsv-6, bytes/records/sha256 refreshed.
- Invoke-ReleaseGate.ps1: pins tsv-6 and validates 6 fields.
- Test-EmbeddedCorpus.ps1: rewritten for v2 (topic+genre validation, known-
  duplicate row hash re-pinned); self-test + real corpus pass.
- Test-PackData.ps1: allow the health-body topic; catalog + self-test pass.
- packs.json: every pack dataSchema 2, sha256/bytes/count refreshed.
- source-rights-evidence.json: corpus sha256 refreshed; ALL releaseApproved
  stay false. Readme/THIRD_PARTY_NOTICES/TAXONOMY prose updated.

Rights approvals remain unset (the user's call). The @engine-source closure
hash is stale from this session's source edits and is re-pinned at release
finalization, not here.
```

### 2026-08-02  `eb5321250`

**AI brain: advise when the vision model likely can't accept images**

```
Backlog 4 (validation variant). A text-only model in the vision slot fails
silently. Add a provider-agnostic, name-based capability heuristic and a
non-blocking advisory rather than hiding models a heuristic might misjudge.

- AiModelPolicy.LooksVisionCapable(model): matches the id against known
  multimodal families (llava, gemma3, *-vl, llama3.2-vision/mllama, pixtral,
  minicpm-v, gpt-4o, claude-3/4, gemini-1.5/2, phi-*-vision, ...). Loose by
  design: unknown -> text-only (advisory), empty -> capable (silent).
- FormOptions AI tab: an amber advisory under the vision model shows only when
  "Use vision" is on AND the chosen model looks text-only. Re-evaluated on model
  edit, the Use-vision toggle, and after a model-list refresh. Never blocks; the
  Test button remains the real end-to-end check.

Verified: clean build; --filter-selftest 216 PASS; --resource-churn builds the
Options AI tab 4x, zero errors.
```

### 2026-08-02  `f2eff2977`

**AI brain: personality presets + optional speech-pattern layer**

```
Backlog 2 + 3. Give the pet's voice two knobs beyond the free-text blurb:

- Personas.cs: shared, pure-data catalog of 9 personality presets (each a ready
  blurb) and 9 speech patterns (none/pirate/l33t/rhyme/pun/shakespeare/yoda/
  valley/uwu), read by both the runtime and the options UI.
- AiSettings.SpeechPattern (default "none"): reflection-persisted, normalized and
  validated against the catalog (lowercased, unknown -> none).
- AiBrain.BuildSystemPrompt appends the speech instruction, scoped to the remark
  text so the JSON contract is untouched; empty for "none" (byte-identical to the
  old prompt).
- FormOptions AI tab: a "Persona preset" dropdown that fills the Personality
  blurb (+ a "Custom…" entry), and a "Speech style" dropdown. Preset<->blurb sync
  is guarded against echo: typing custom text flips the preset to Custom, picking
  a preset fills the text, no loops.

Verified: clean build; --filter-selftest 216 PASS; --resource-churn builds the
Options AI tab 4x with the new dropdowns, zero errors.
```

### 2026-08-02  `1f274e618`

**Fortunes: group the source picker into a collapsible tri-state tree**

```
Backlog: with many installed collections a flat checklist is hard to scan and
toggle. Replace the Sources CheckedListBox with a TreeView grouped by theme
(the dominant topic of each source, custom last), collapsed by default.

- Parent node = a whole theme: checking/unchecking it cascades to its sources;
  toggling a source recomputes the parent (checked iff all children checked). A
  re-entrancy guard stops the programmatic sets from recursing in AfterCheck.
- Select all / none, the disabled-source persistence, capture-on-rebuild, and
  capture-on-close all keep working, now walking leaf nodes.
- Genres stay a flat list (only 12); SourceItem is retained for that list.

Verified: clean build; --filter-selftest 216 cases PASS; --resource-churn
self-test builds and exercises the Options Fortunes tab 4x (optionsCycles=4,
optionsCancellationCycles=4) with zero errors.
```

### 2026-07-31  `cc9a22778`

**Rights: scaffold docs/rights/ with an evidence map and readiness status**

```
Create the canonical location for retained redistribution-rights evidence and
document its contract without touching any approval flag:
- what each file covers (source-review.md; per-pack <id>.json) and which
  validator/gate checks it,
- the two approval-bearing files under packaging/ that only a human flips,
- current status (all six source scopes plus packs unapproved: the deliberate
  pre-release baseline),
- how the v1-to-v2 taxonomy flip relates to rights (v2 re-expresses the same
  corpus bytes with richer metadata; adds no new redistributed content, so it
  gates on the same corpus-rights approval),
- a concrete user action checklist to reach rights-ready.

Both rights validator self-tests remain green; docs/rights/README.md is not a
referenced evidence file, so it is invisible to the gate.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-31  `2cf825899`

**Fortunes: add health-body as the 12th taxonomy topic**

```
Phase-2 topic expansion. The `life` catch-all (58%) does not decompose into
large clean topics, but health/medicine/the body/fitness/aging/mental-health is
a genuinely distinct subject users may want to filter, so add it as a 12th
topic and bump the locked taxonomy version 2026-07-29 -> 2026-07-31.

- FortuneTaxonomy.TopicSet gains health-body; TaxonomyVersion updated. The
  runtime accepts it in v2 rows; no v1 compat category maps to it (it only
  emerges from the completed classification pass).
- label-common.sh LABEL_TOPICS + LABEL_TAXONOMY_VERSION and label-selftest.sh
  updated to match; label-input.meta regenerated (56,064 texts unchanged).
- TAXONOMY.md documents the new topic and the revision.

The local labels-store.tsv working artifact (gitignored) was re-labeled via a
precision-gated pass: a high-recall health net (1,436 candidates) narrowed by
strict subject-vs-vehicle classification to 175 health-body entries (life 124,
science 23, society 15, food 5, ...), genre axis preserved. Runtime and harness
self-tests pass (filter 216 cases, taxonomy PASS, label pipeline PASS).

v1 remains the shipped corpus; this only prepares the v2 taxonomy.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-31  `66e604835`

**Fortunes: add genre delivery-style filter to the picker**

```
Mirror the existing source filter with a per-genre opt-out so users can mute
delivery styles (jokes, wisdom, TV quotes, insults, ...) independently of which
sources they keep.

- AiSettings.DisabledGenres: hard preference filter, empty = all-on so newly
  added genres default enabled (same contract as DisabledSources).
- FortuneProvider.Select honors DisabledGenres; Genres()/GenreStat enumerate the
  taxonomy genres present in the active pool with counts.
- FormOptions gains a Genres CheckedListBox with Select all/none, populated and
  synced alongside sources (piggybacked so every call site is covered).
- --filter-selftest now sweeps a DisabledGenres axis: 216 cases, 0 failures.

Works on the shipped v1 corpus via the v1->v2 compat mapper; ready for the v2
per-fortune genre labels when that schema is deliberately flipped.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-31  `92e1750d0`

**Harden DesktopPet runtime and release pipeline**

```
Apply the full security, QA, packaging, deterministic-build, rights-policy, documentation, and regression-test remediation pass.
```

### 2026-07-29  `f8991ef8b`

**Fortunes: fix content-filter fail-open + gate the classification harness**

```
Finding 1 (safety, shipped): FortuneProvider.Rebuild relaxed the SAFETY filters when a filter
combo emptied the pool -- it dropped NoProfanity and then dumped the entire corpus (all nsfw +
profane). A dynamic test (SpicyOnly+NSFW+NoProfanity) leaked all 23 profane NSFW entries. Now the
fallbacks relax only PREFERENCES (disabled sources, spicy-only); NoProfanity and "spicy off =
general only" are hard floors that survive every fallback, degrading to clean-general or an empty
pool (silent pet) instead of leaking. Added `--filter-selftest` (FortuneProvider.FilterSelfTest):
all combos now report profanity_leaks=0 / spicy_leaks=0 (was 23).

Finding 2 (harness): label-merge.sh now exits non-zero on any missing/invalid chunk (was truncating
the store then exiting 0 on a partial classification). label-apply.sh is gated: refuses unless run
with --go AND the store is complete, since it emits the 6-column schema that the current 5-column
FortuneProvider parser would misread -- the parser must be updated to 6 columns first.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `5297a6b77`

**Fortunes: rebuild labeling harness for the full 61k pass (text-keyed, multi-file)**

```
Covers embedded corpus + all 12 packs, deduped by text and shuffled. Store keyed
by text so labels apply back to every file containing a fortune and the grind
resumes across context compactions (label-next skips texts already in the store).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `b80a71881`

**Fortunes: lock 4-axis taxonomy + labeling harness (Milestone 1 begin)**

```
Data-grounded revision after scanning all 61k lines: genre is the rich axis
(TV quotes 22%, showerthoughts 16%, wisdom 17%; every topic signal <=4%), so
11 topics (life = large catch-all) + 12 genres, orthogonal to the existing
level/prof severity axes. Harness labels the frozen shuffled embedded corpus in
resumable in-order batches (label-next.sh -> hand-label -> label-ingest.sh).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `b41b976df`

**Fortunes: add per-fortune classification taxonomy (Phase 1 of the full pass)**

```
15 topics + 8 tones, each with a one-line definition and prototype/exemplar
sentences that double as the runtime routing prototypes. Two orthogonal axes,
independent of level/prof. Draft for review before labeling the ~61k corpus.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `706f78bcf`

**Fortunes: widen embedded corpus to all 7 router topics (coverage quick-win)**

```
The lean embedded default was 5 sources / 4,541 lines tagged only whimsy+wisdom, so
5 of the 7 SmartFortunes.Router topics had no embedded content to match against
out-of-box (they only lit up once a matching pack was downloaded).

Additively pulled the missing-topic sources from JKirchartz/fortunes (public domain,
the origin build-corpus.sh already targets; distinct from our downloadable packs):
  tech   : epigrams_in_programming, hackers, hacker-questions, lwall-quotes,
           ComputerDictionary, rfc1925, enkiv2s-glossary  (~993)
  work   : godin, activists                                (~758)
  facts  : realfacts (skipped niche PA-historical-markers) (~861)
  observ.: showerthoughts (capped 800)                     (~800)
  creative: authors (1000) + artists (700) + wblake, ogden_nash, stevenson,
           Jenny_Holzer, ObliqueStrategies, ObscureSorrows, rhetorical-devices,
           EnglishAsSheIsSpoke                              (~2358)
Then re-ran the pipeline tail (strip-authors.py + classify-corpus.py) and deduped
on text. Result: 10,311 lines / 1.26 MB across all 7 topics, balanced 758-2384 each.

Verified: build OK; smart self-test warms the widened pool and a C#/VS context now
matches a software/licensing quote, a spreadsheet context a finance line.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `f2e49c268`

**Merge: pre-release cleanup pass**

```
Dead-code trim, correctness fixes (AI-off Ollama poke, sound self-mute, timer/thread
safety), .NET 4.8 retarget + hygiene/perf, and GitHub Actions build/release workflows.
Phase 3 (classifier routing enrichment) intentionally deferred pending the expanded-
classification brainstorm; release intentionally held. See handoff.md / BACKLOG.md.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `92f2f6d80`

**Phase 7: docs + handoff for the cleanup pass**

```
- BACKLOG.md: add "Post-v1 backlog" (fortunes-selection tree UX, AI-voice bundle
  [personality presets + speech patterns + model-capability validation], UI
  modernization tiers, Shimeji->animations.xml converter) plus the expanded-
  classification brainstorm (two-axis taxonomy + prototype-embedding routing +
  how to reclassify the corpus) and the deferred audit items.
- handoff.md: refreshed to the 2026-07-29 cleanup session (what shipped, what's
  deferred incl. Phase 3, hands-on verification list, how to cut the held release).
- Readme.md: document the CI + release GitHub Actions workflows.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-29  `e1908902b`

**Phase 6: GitHub Actions build + release workflows**

```
- build.yml: CI on push/PR to main|master (+ manual). Builds the portable app
  (build.ps1 -Release -Zip) on windows-latest, runs the bundled-embedder self-test
  (--embed-selftest, must report IsReady=True), and uploads the portable zip artifact.
- release.yml: on a published GitHub Release (or manual dispatch with a tag), builds
  the portable zip + WiX MSI, self-tests, and attaches both to the release via the
  preinstalled gh CLI (built-in GITHUB_TOKEN; no third-party publish action).

Both reuse the repo's build.ps1 / installer/build-installer.ps1 so CI matches local
builds. YAML validated.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `1e6d3c66d`

**Phase 4: hygiene + perf**

```
- retarget v4.7.2 -> v4.8 (csproj + src/app.config supportedRuntime) to match the
  branding/docs; 4.8 targeting pack verified present, build + onnx binding OK.
- ScheduleIdle: clamp idle bounds to 86400s so a hand-edited settings JSON can't
  overflow the * 1000 (audit #19).
- remove x86 build configurations (Debug|x86, Release|x86). Ships x64-only; the
  onnxruntime native is arch-specific, so an x86 build only yields a broken variant (#18).
- FortuneProvider: cache the embedded fortunes.txt parse once (was re-parsing ~486KB
  on every static Sources() call). Entries are read-only after load (#14).
- FormOptions: wire FormClosing/_fSmartTimer disposal at timer creation instead of in
  the AI-tab builder, so the timer can't leak if a later builder throws (#22).
- ICompanionBrainBackend doc updated for OpenAiCompatBackend (#20).

Deferred (documented in handoff): #17 stale binding redirects (work today via
AutoGenerateBindingRedirects; runtime-binding risk not worth it unattended), #12
VectorCache prune (low urgency), #15 ComputeSignature (only 16x16=256px, negligible).

Build: Release|x64 OK. Embed self-test 0.72/0.44; smart picks on-topic.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `47907f54e`

**Phase 2: correctness fixes**

```
- AI brain "off" no longer contacts Ollama or builds an unused brain. ApplyAiBrainState's
  off-branch called EnsureBrain() (constructing an AiBrain + HttpClient) then UnloadAsync,
  which POSTs keep_alive:0 to localhost:11434 on every AI-off launch (the default). Now it
  only unloads when a brain already exists this session. Restores "off = zero Ollama/VRAM".

- Sound no longer self-mutes on a single bad clip. Animations.Sound.Load/Play zeroed the
  master volume on any decode/playback exception, silencing the whole pet. Now a failed clip
  disables only itself (Audio/AudioReader = null) and global volume is untouched; Play() and
  Audio_PlaybackStopped null-guard accordingly.

- LandTimer_Tick wrapped in try/catch (like IdleTimer_Tick) so a SayFortune/Say throw can't
  escape a WinForms timer as an unhandled UI exception; the timer is stopped on error.

- AskAboutScreen thread-marshaling made explicit: after the awaited (ConfigureAwait(false))
  call we re-check for a live pet and marshal apply() through it, instead of relying on the
  incidental ui==null / iSheeps==0 invariant to keep UI-only calls off the pool thread.

- Pet_Click's per-call HttpClient scoped in a using (handle-churn parity with LoadPets).

Build: Release|x64 OK.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `142f77f97`

**Phase 1: trim dead code + stale config**

```
- rm src/Portable/_NAudio.dll (289KB orphan; referenced nowhere - the real
  engine is embedded Portable/NAudio.dll)
- rm src/dotNet/app.config (stale v4.7 duplicate; effective config is src/app.config)
  and its <None> in the csproj
- trim unused csproj references: OracleClient, DirectoryServices, Numerics,
  ServiceProcess, Transactions, Web.Extensions, Deployment, and the two
  absolute-path UWP refs (Windows.winmd, System.Runtime.WindowsRuntime).
  De-dupe System.Security (kept once; used by DPAPI + crypto).
- disable ClickOnce manifest generation (GenerateManifests=false) and drop the
  ClickOnce-only props (cert thumbprint, key file, target zone, timestamp url,
  sign manifests). Dead upstream cruft that broke GenerateApplicationManifest on
  a clean rebuild; the Win32 manifest is still embedded via <ApplicationManifest>.
- drop redundant usings: `using static DesktopPet.StartUp;` (StartUp.cs),
  `using System.Xml.Serialization;` (Program.cs)

Build: Release|x64 OK. Embed self-test unchanged (cos 0.72/0.44).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `d67af8463`

**online pets: mirror from our fork + harden LoadPets against offline**

```
Repoint the Online-pets catalog/icon/animation URLs from Adrianotiger/desktopPet
to our own bigfnj/desktopPet mirror (already carries the full 22-pet Pets/ set),
so the feature no longer depends on upstream staying online.

Harden LoadPets (audit #1, part of #13): the async-void + network I/O could
repost an unhandled ThreadException and crash Options while offline. Wrap in
try/catch, null-check the deserialized payload, guard per-icon fetches, and fix
the MemoryStream/Image leak via a standalone-bitmap copy.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `16f478a5a`

**Smart fortunes: use bge query-instruction prefix on the context embedding**

```
bge-small-en-v1.5 is asymmetric (query gets an instruction, passages stay plain);
prefixing the screen-context query improves retrieval relevance at zero size/speed
cost. Passages (cached) unchanged, so no re-warm needed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `54f7117c1`

**Fortunes tab: Rebuild smart weights button**

```
Explicit control to re-embed the pool + recompute the centering weights for the
current source/pack/tone selection (it also happens lazily on Apply; this forces
it now with live status). StartUp.RebuildSmartFortunes reloads settings, rebuilds
the filtered pool, and re-warms the embedder in the background (cached per-text
vectors are reused; only new lines are embedded).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `0afbe94f0`

**AI tab: Test connection button**

```
Verifies the AI brain end-to-end from the current settings (any provider):
reaches the endpoint (EnsureServerAsync), then loads-and-replies with the chosen
text model (and the vision model when Use-vision is on), and evicts them from
VRAM afterwards. Inline status: '✓ connected · text "m" OK 1.2s · vision ...'
or '✗ can't reach <endpoint>' / per-model errors.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `62a876487`

**Spit polish: current README + live smart-fortunes status in the Fortunes tab**

```
- Readme.md rewritten to describe the finished product (offline fortunes + packs
  + per-source picker + custom uploads; bundled offline smart fortunes; optional
  off-by-default multi-provider AI brain; MSI + portable zip; correct build cmds).
- Fortunes tab shows a live 'Status:' line for smart fortunes (off / warming /
  ready + line count) via StartUp.SmartFortunesStatus + SmartFortunes.PoolCount,
  updated on a timer while Options is open (disposed on close).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `3ee7bc284`

**Phase C: OpenAI-compatible multi-provider AI brain (One Interface)**

```
Generalize the AI brain beyond Ollama-only:
- OpenAiCompatBackend: one ICompanionBrainBackend for any /v1 endpoint (LM Studio,
  llama.cpp, OpenRouter, OpenAI, custom) - chat via /chat/completions, vision via
  image_url parts, model list via /v1/models, optional Bearer key. Start/warm/
  unload are no-ops (these providers own their model lifetime).
- AiProviders presets (base URL, needs-key, local/cloud).
- AiSettings: Provider + OpenAiBaseUrl + ApiKey (DPAPI-encrypted at rest via
  ApiKeyEnc). StartUp.EnsureBrain picks OllamaClient (native keep-alive VRAM) for
  'ollama' else OpenAiCompatBackend.
- AI tab: Provider dropdown (prefills base URL, shows key field for cloud) +
  model dropdown now reads Ollama /api/tags OR /v1/models.
Verified the /v1 contract against Ollama's OpenAI-compat endpoint (models + chat).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `c9fe7ef4c`

**AI brain master switch (default OFF) + tray Load/Unload AI**

```
The Ollama 'AI brain' is now gated behind a single master switch, default OFF, so
the pet uses ZERO GPU/VRAM out of the box (only the CPU smart-fortunes embedder
runs). Off = no autostart, no warmup, no hotkey, no idle, no tray 'ask'.
- AiSettings.AiBrainEnabled (default false).
- StartUp.SetAiBrainEnabled/ApplyAiBrainState: (un)register triggers and load
  (warm) or unload (evict) the model; AskAboutScreen speaks a fortune when off.
- OllamaClient/AiBrain UnloadAsync (keep_alive:0) frees VRAM.
- Tray item reflects state: 'Load AI (uses GPU)' <-> 'Unload AI (free VRAM)';
  the 'Ask about my screen' item shows only when the brain is on.
- AI tab: 'Enable AI brain' checkbox.
Verified: brain-off launch loads no model (/api/ps none); unload evicts a warmed
24B model from VRAM.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `d716be63e`

**Phase B3-B5: smart/contextual fortunes (offline embedder pick)**

```
- SmartFortunes.cs: persistent text->vector cache (%LOCALAPPDATA%\DesktopPet  vectors, checkpointed during warm), background warm of the active pool, and a
  contextual pick = centered cosine (hubness) + app->category routing bonus +
  confidence-adaptive fall-through to random + top-k variety.
- FortuneProvider exposes the filtered PoolEntries(); ActiveWindow.ProcessName()
  for routing; AiSettings.SmartFortunes (default on).
- StartUp warms in the background at launch and uses the smart pick in SayFortune
  (land + poke) with automatic random fallback.
- Fortunes tab: 'Smart fortunes' toggle. Hidden --smart-selftest.
Verified on the default pool (4,529, warm 34s): picks are on-topic (C#->Swift
joke, breakup->marriage-proposal), degrades to random until warm.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `fcab3b32a`

**Phase B2 (revised): proper MSI + portable zip instead of a single embedded exe**

```
Ship the smart-fortunes runtime as plain files next to the exe (offline, no
download) rather than embedding + unpacking:
- Embedder loads bge-small.onnx/vocab + native onnxruntime.dll from the app
  folder (standard .NET resolution); dropped the embedded-resource + native
  extraction gymnastics.
- csproj: model/vocab CopyToOutput (not EmbeddedResource); onnx dlls come from
  the package to the output. Removed the redundant embedded dll copies.
- Program.Main no longer embeds the onnx managed dlls (NAudio/Newtonsoft stay).
- WiX: one component per runtime file (+ DesktopPet.exe.config for the binding
  redirects). Installer 75MB->31MB, exe 46MB->2MB.
- build.ps1 -Zip emits dist/DesktopPet-Portable.zip (29MB, extract & run).
Verified installed (loose, 418ms) and portable-zip (355ms): dim 384, cos 0.72/0.44.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `b32f15cb0`

**Phase B2: bundle the smart-fortunes embedder into the portable exe**

```
Everything the embedder needs is embedded so the pet stays a single, offline,
sendable exe (no downloads, no keys) - restoring the portable vision:
- bge-small model + BERT vocab (src/Models) embedded; loaded from bytes.
- managed ONNX runtime + its System.* deps embedded and resolved via
  EmbeddedAssembly (now with a simple-name fallback for binding redirects).
- native onnxruntime.dll embedded and unpacked once into %LOCALAPPDATA%  DesktopPet\runtime, then SetDllDirectory. Exe ~46MB.
Verified on the installed single exe (0 loose dlls, caches cleared): IsReady in
576ms, dim 384, cos(code,code)=0.72 vs cos(code,weather)=0.44.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `b69a4becc`

**Phase B1: local ONNX embedder (bge-small) proven in-app**

```
Ai/Embedder.cs: offline bge-small sentence embedder (Microsoft.ML.OnnxRuntime +
hand-rolled WordPiece, CLS-pooled + L2-normalized). Lazy, degrades to not-ready
if model/native runtime absent (pet just falls back to random fortunes).
SetDllDirectory points at a downloaded runtime dir or the exe's own dir.
Hidden --embed-selftest verifies load+embed in the real app context: dim 384,
cos(code,code)=0.72 vs cos(code,weather)=0.44.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `e531c9a76`

**Packs break out into per-source toggles**

```
Packs are now tagged (source/category/level/prof/text) instead of one merged
blob; the loader (LoadTaggedPack) splits each installed pack into its bundled
sources, so e.g. tv-clean shows The Simpsons, Futurama, MST3K... as individual
checkboxes (the 'only Simpsons + Futurama' use case). Friendly names for tv-/
off- sub-sources. Plain user uploads still load as a single source.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `a3953aeb2`

**Fortunes tab: in-app Packs downloader**

```
Fetch packs.json from GitHub, checklist of packs (name/count/vibe/installed),
Download-checked writes into %APPDATA%\DesktopPet\fortunes\ and live-reloads so
they appear as toggleable sources.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `0b60b1a72`

**Untrack __pycache__ (.gitignore line was mangled)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `13fbab489`

**Fix bofh pack: emit plain text (was tab-tagged)**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-28  `387cfdaed`

**Lean default corpus + 12 downloadable fortune packs**

```
- Replace the 26k showerthoughts-heavy embedded corpus with a lean default
  core (~4,541): fortunes, quotable (MIT), cleanjokes, BibleAbridged,
  SimpsonsChalkboard.
- Add packs/ : 12 opt-in packs (~50,860 entries) cut/cleaned/deduped from the
  harvest + existing corpus — dadjokes, bofh, tech, philosophy, literary,
  comedy, facts, tv-clean, tv-mature, showerthoughts, spicy, nsfw — plus
  packs.json manifest (raw.githubusercontent) and a provenance/license README.
- Add FORTUNE-SOURCES-ASSESSMENT.md (full source inventory + tiers).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `688edff91`

**Fortunes: intentional content tiers, per-source picker, and custom uploads**

```
Corpus is re-tagged and consolidated into one embedded file
(source<TAB>category<TAB>level<TAB>prof<TAB>text, 26,141 entries):
- level = general / edgy / nsfw, plus a profanity flag, via classify-corpus.py
  (build-corpus.sh now emits + classifies; strip-authors.py generalized to the
  last field). Old sfw/spicy split files removed.

FortuneProvider rewritten to filter everything at runtime and to load user
fortunes from %APPDATA%\DesktopPet\fortunes\ (BSD % format or one-per-line),
classified in-process by FortuneClassifier. Adds Sources() enumeration for the
picker. Verified against the compiled binary: default 24,080 / edgy+nsfw 26,141 /
true-nsfw 24,530 / spicy-only 2,061 / only-Simpsons 363.

AiSettings: SpicyTier ("edgy"|"nsfw"), NoProfanity, DisabledSources.

New Options "Fortunes" tab: enable-spicy + level dropdown, "skip the tame ones",
"remove all profanity", a per-source checklist (69 collections, friendly-named
and grouped by theme, Select all/none), "Add fortunes..." / "Open folder", and an
explicit Apply button (also applied on close).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `32243abd0`

**Strip author bylines from fortunes; make speech bubble follow the pet**

```
Fortunes:
- Remove trailing author/attribution bylines the pet was speaking
  ("... -- Neil Gaiman", reddit "...--User, Mon YYYY", "... - Feynman").
  19,455 bylines stripped across the sfw/spicy corpora; 12 author-only
  fragments dropped. High-precision: prose dashes inside quotes (Perlis
  epigrams, Red Green dialogue, Le Guin) are preserved.
- Add strip-authors.py (idempotent, name-aware) and wire it into
  build-corpus.sh so a regen stays clean.

Speech bubble:
- FormSpeech.ShowSpeech split into a reusable Reposition() (+ IsShowing,
  cached height, no-op-move guard). FormCompanion re-anchors the bubble to the
  pet's mouth every tick, so it follows the pet as it walks/falls instead
  of being orphaned where it first spoke.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `eb7d24684`

**fix(fortunes): land-fortune waits for the pet to settle (no mid-air bubble)**

```
The launch greeting fired on a fixed 3s timer, so if the pet was still falling
the first bubble floated in mid-air. Replace it with a poll (250ms): the pet is
descending only while its Y increases, so speak once Y has stopped increasing for
~0.5s (i.e. landed / walking / climbing), with a ~10s safety cap and graceful
wait if no pet exists yet.

Verified live: at ~0.9s the pet is mid-fall with no bubble; after it lands it
then speaks the fortune.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `adab3352b`

**fix(options,installer): resizable Options dialog + same-version MSI upgrades**

```
Two bugs found while play-testing the installed build:

- Same-version reinstalls were a no-op (MajorUpgrade only removes LOWER versions),
  so new builds never replaced the installed exe -> users ran stale code (missing
  the Spicy-only toggle). Add AllowSameVersionUpgrades="yes" so reinstalling the
  same version replaces files.
- The Options dialog was FixedToolWindow (non-resizable) with a fixed-size tab
  control, so the AI tab's tall content was cut off with no way to enlarge. Make
  it Sizable + MaximizeBox, dock the tab control to Fill, bump the default
  ClientSize to 500x560, and set a MinimumSize. The AI tab already AutoScrolls.

Verified: reinstall now replaces the exe (timestamp updates); build green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `5189ba7aa`

**feat(fortunes): category + content-rating corpus tags + "Spicy only" mode**

```
Prep the corpus for Phase B contextual routing, and act on the fact that most
users will run Spicy.

- build-corpus.sh now emits "category<TAB>rating<TAB>text": category = coarse
  topic (tech/wisdom/creative/whimsy/facts/work/observations/general) for Phase B
  routing; rating = sfw|spicy (profanity/NSFW hit, or an inherently-adult source
  like yo-mama/carlin). Fixed a set -e/pipefail abort when grep found no profanity
  in a clean file. Spicy corpus: 22,172 lines (20,098 sfw / 2,074 spicy-rated).
- FortuneProvider reads the rating column; new (spicy, spicyOnly) ctor. "Spicy
  only" pulls just spicy-rated fortunes and skips the tame ones (graceful fallback
  to full-spicy then SFW if empty).
- AiSettings.SpicyOnly + an indented "Spicy only" checkbox under the Spicy toggle
  in the AI options tab (enabled only when Spicy is on).
- FORTUNE-SHEEP-PLAN.md: Phase B spec refined (category routing + app->category
  rules + centered/whitened vectors + confidence-adaptive specificity + top-k-random);
  documents the rating dimension for context-gating spicy lines.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `a4cbb4ce1`

**docs: pause point — Fortune Sheep remaining work in BACKLOG, handoff refreshed**

```
Phase A shipped; pausing before Phase B (the ONNX embedder). Capture what's left
and the future-me notes:

- BACKLOG.md: per-phase status for the Fortune Sheep plan (A done; B contextual
  embedder / C insight+One-Interface / D presets / E release), open verification
  items (bathtub escape + land fortune to eyeball), and small discovered TODOs.
- handoff.md: refreshed "Where we are" (Phases 1-6 + license + 7.1 installer +
  Fortune Sheep A) and "What's NOT done"; added a Fortune Sheep code map and the
  session tooling gotchas (WiX v5/OSMF, ASCII-only PS scripts, pet-click
  automation flakiness, ONNX single-exe risk).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `8e94f7457`

**feat(fortunes): Phase A — poke-escalation, bathtub escape, land-fortune, Spicy toggle**

```
Completes the Phase A "Fortune Sheep" interaction.

- StartUp.OnPetPoked: timing-based poke escalation (a 7s pause resets it):
  pokes 1-2 = fortune, 3-4 = ignore (turn-away animation, no bubble), 5-11 =
  verbal sass, 12 = bathtub escape (then reset). Thresholds are named constants.
- Ai/PokeReactions.cs: the sass one-liners as a plain list, easy to extend.
- FormCompanion.EscapeToBath(): flee via the pet's own "bath*" spawn (fly in from the
  edge, land in a tub) by re-running the engine's public Play(forceSpawn) against
  the spawn whose next animation is named bath*. Falls back to a fortune if absent.
- Land greeting: a one-shot timer speaks a fortune ~3s after launch.
- Spicy-fortunes toggle added to the options (AI) tab.

Verified live: poke -> fortune; rapid pokes reached the sass tier ("Okay, okay,
I'm awake!"). The 12th-poke bath finale + land shot resisted clean screenshotting
(a browser modal stole the pokes; the pet sat at screen-top) but reuse verified
engine paths; build green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `2dc3d0a41`

**feat(fortunes): Phase A — FortuneProvider + right-click gives a fortune**

```
- Ai/FortuneProvider.cs: loads the embedded %-delimited corpus (SFW default,
  Spicy opt-in via AiSettings.SpicyFortunes), hands out random non-repeating
  fortunes. Fully offline, no model/server, never throws.
- Corpus embedded into the single exe (csproj EmbeddedResource); exe ~1.7MB -> ~6.7MB.
- StartUp: EnsureFortunes / SayFortune / OnPetPoked; fortunes rebuilt on settings
  reload so a SFW<->Spicy change takes effect.
- FormCompanion right-click now pokes the sheep -> a fortune (replaces the old greeting;
  the full poke-escalation state machine + bathtub escape land next).

Verified live: right-clicking the sheep popped a real corpus fortune ("You never
achieve success unless you like what you're doing").

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `5c4eabee2`

**feat(fortunes): Phase A — bundled SFW/Spicy fortune corpus (public domain)**

```
Curated from JKirchartz/fortunes (Unlicense). src/Fortunes/build-corpus.sh is the
reproducible pipeline: parse the BSD %-delimited files, normalize each entry to a
single bubble-sized line (8..280 chars), dedupe, and split into two corpora:

- fortunes-sfw.txt   13,679 entries — curated quality collections (philosophy,
  literature, wholesome/geeky humor, tech, facts), ENTRY-level profanity-filtered
  (0 residue).
- fortunes-spicy.txt 26,147 entries — SFW set + edgy collections (yo-mama, carlin,
  showerthoughts, etc.), UNFILTERED, opt-in.

These get embedded into the single exe by the FortuneProvider (next commit).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `66758fdf4`

**docs: Fortune Sheep (v2) end-to-end plan + backlog refresh**

```
Capture the reviewed plan as FORTUNE-SHEEP-PLAN.md (source of truth for the next
build): fortune-first sheep, 3 tiers behind one OpenAI-compatible interface,
poke-escalation ending in the full bathtub-respawn escape, contextual fortunes
via a bundled in-process ONNX embedder (bge-small).

Locked decisions: model = ask-at-first-run; default preset = Companion; cloud =
OpenRouter + OpenAI; bathtub escape = full respawn via spawn id=3; first pass =
Phases A->B->C. BACKLOG status refreshed (Phases 1-6 + license + 7.1 installer
done; pointer to the plan).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `bbda007e0`

**chore: delete stray runtime DesktopPet.config; ignore it everywhere**

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `c9523dd9f`

**feat(installer): Phase 7.1 — per-user WiX MSI**

```
A no-admin MSI that packages the self-contained DesktopPet.exe.

- installer/DesktopPet.wxs (WiX v4/v5 schema): per-user install to
  %LOCALAPPDATA%\Programs\DesktopPet AI Edition\, Start-menu + Desktop shortcuts
  (ICE-clean per-user components with HKCU keypaths), MajorUpgrade, uses the
  exe's own icon, WixUI_Minimal with the MIT license.
- installer/build-installer.ps1: wraps `wix build` (bindpaths + UI extension);
  outputs dist/DesktopPet-AI-Edition.msi.
- installer/license.rtf shown in the wizard.
- .gitignore: ignore dist/ + wix intermediates. README gains an Installer section.

Note: built with WiX v5 (v6+ requires the paid OSMF license). Verified end-to-end
on this box: silent install (exit 0, no admin) placed the exe + both shortcuts,
the installed exe launched, and silent uninstall removed everything.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `d7ec2829c`

**docs: MIT LICENSE for the fork's AI-Edition contributions**

```
Add an MIT LICENSE (the fork's own code: dotNet/Ai/*, the AI options tab, the
build tooling, and grimoire/). README gains a License section clarifying scope:
MIT covers this fork's additions; the upstream WinForms engine (Adrianotiger)
and the eSheep/Stray Sheep pet artwork originate with their respective authors
and are credited accordingly.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `82580b612`

**feat(ai): Phase 6 — vision path fixed, routing, sane defaults**

```
Testing the (previously untested) vision path found it worked but was far too
slow: gemma3:4b on a full-screen 1280px image took ~68s — past the 60s HTTP
timeout, so every vision ask timed out and the pet stayed silent.

- 6.2 routing: vision is used only for explicit asks (hotkey/tray). The idle
  loop now forces the fast text path (AskAboutScreen(false)) — no 60s+ glances
  firing on their own.
- Vision image downscaled to 896px before sending (ToBase64PngScaled) — the big
  lever on inference time. OCR keeps the larger capture for legibility.
- Defaults fixed: VisionModel gemma3:4b (small/fast; the old mistral-small3.1
  wasn't even a valid tag), TimeoutSeconds 60 -> 120 for cold-model headroom.
- README: vision-mode section + recommended models (6.3).

Verified: direct request returned valid JSON; after the fixes a warm vision ask
completed in a few seconds and wrote a coherent reply ("So many windows! Time
for a nap, I think"). 6.4 (PII scrubbing) deferred.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `aba4ef584`

**feat(ai): Phase 5.6 — screen-zone awareness (completes Phase 5)**

```
The pet can now react to the window it's physically standing on, not just the
foreground app.

- FormCompanion.WindowUnderCompanion: title of the window the pet is walking on (reuses the
  engine's existing hwndWindow + GetWindowText), or "" when roaming the desktop.
- StartUp.AskAboutScreen captures it on the UI thread and passes it down.
- AiBrain adds "You are standing on the window: <title>" to the ask context when
  present (gracefully omitted otherwise).

Build + runtime green. Phase 5 (Context & Memory) is now complete.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `f6bb380ec`

**feat(ai): Phase 5.3/5.4 — rolling conversation memory**

```
Give the pet continuity so it stops reacting as if each glance is its first.

- Ai/ChatHistory.cs: a rolling window (last 10 exchanges) of {compact context,
  reply}, persisted to %APPDATA%\DesktopPet\chat-history.json. Thread-safe,
  never throws.
- AiBrain replays recent turns (as user(context)/assistant(reply) pairs) ahead
  of the current ask, and records each new exchange with a compact context label
  (the active-window title, not the full OCR — keeps the file + context small).
- AiSettings.MemoryEnabled (default on) + a toggle in the AI options tab.
- ChatMessage.Assistant() factory for replaying prior replies.

Verified live: two asks wrote chat-history.json with the real active-window
context per turn; build green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `834bdbfd0`

**feat(ai): Phase 5.1/5.2/5.5 — context + persona in the prompt**

```
Make the pet aware and personal instead of reacting to raw screen text with a
fixed persona.

- 5.1 Active-window context: new Ai/ActiveWindow.cs (GetForegroundWindow +
  GetWindowText, never throws) feeds "The active window is: <title>" into the
  ask, so the pet can react to what you're actually doing.
- 5.2 Time of day: the system prompt now states morning/afternoon/evening/night.
- 5.5 Persona: AiSettings gains PetName / UserName / Personality; the system
  prompt is now built per-call from them (BuildSystemPrompt). Exposed as three
  fields in the AI options tab.

Verified live: built green; an ask over a GitHub "fortunes" repo returned an
in-character, context-aware remark ("Looks like some fun stuff to read!").

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `2482918a9`

**feat(ai): port Phase-1 Speech tab into compiled options + AI-aware pet greeting**

```
- Speech tab: add BuildSpeechTab() to the COMPILED src/Portable/FormOptions.cs
  (the Phase-1 version was stranded in the deleted dotNet/Portable copy and never
  showed). Enable-speech toggle + bubble-duration slider, backed by
  Properties.Settings (SpeechEnabled/SpeechDuration), applied live via
  ContextMenus.RefreshSpeechMenuItem(). Note: AI features are gated on
  SpeechEnabled, so this is also the AI on/off switch.
- Pet right-click greeting no longer says the misleading "Right-click me for
  options" (options are in the TRAY, not on the pet). Now AI-aware:
    * backend not ready -> "Set my options and AI brain in the tray."
    * backend ready     -> "Right-click the tray icon for options."
  Driven by a new StartUp.AiReady flag: AiBrain.PrepareAsync now returns whether
  the backend came up (captured at launch warmup), and a successful ask sets it.

Build + runtime verified green.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `8b373821a`

**docs: add SME grimoire — history, architecture, pet XML format, ecosystem**

```
A durable, cross-linked knowledge base under grimoire/ so the project's
hard-to-recover knowledge survives as it ages. Six files:

- README.md — index + overview.
- 01-history-and-lineage.md — Nomura's "Stray Sheep" (1994) -> 1990s "Screen
  Mate" shareware -> Adrianotiger's C# reimplementation -> web-esheep -> this fork.
- 02-architecture.md — the WinForms engine internals, cited to src/dotNet/.
- 03-pet-xml-format.md — the animations.xml reference (every element/attribute,
  the expression language, a worked example, author-a-pet walkthrough).
- 04-upstream-forks-ecosystem.md — upstream status/license, the web-esheep port,
  Shimeji/Desktop Goose cousins, how to pull pet artwork.
- 05-glossary-and-faq.md.

Researched from the upstream repo/wiki, esheep.petrucci.ch, the Pages site, and
the repo's own source; uncertain claims are flagged "unverified:". Notably: the
upstream desktop engine has NO license (license: null) while web-esheep is
GPL-3.0, and default sprite art is third-party — flagged for any public release.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `0a46b4769`

**build: Phase 0 hygiene — PackageReference, build.ps1, quarantine legacy flavors**

```
Consolidate on the one build that ships (the portable app) and remove the
build friction, without touching engine behavior.

- packages.config -> PackageReference in DesktopPet_Portable.csproj (kills the
  restore dance + the missing-package fragility; transitive deps auto-resolve).
  NAudio stays a physical reference and Portable\{NAudio,Newtonsoft.Json}.dll
  stay embedded for the single-exe trick. packages.config deleted.
- build.ps1: one command (kill eSheep -> restore -> build x64 -> optional run)
  encoding the build tribal-knowledge (.csproj not .sln, x64 not AnyCPU).
- src/Directory.Build.props: pin LangVersion 7.3 explicitly.
- Quarantine the dead/non-shipping flavors into src/legacy/ (kept in git):
  classic DesktopPet.csproj + DesktopPet.sln, and the UWP AppWins/ + UWPSheep/.
  src/legacy/README.md explains status + how to revive + the modern MSIX path.
  The portable build references none of them; src/ root now holds only the
  portable project + shared source.

Verified: build.ps1 green (restore+build exit 0) AND the exe launches and stays
alive (embedded NAudio/Newtonsoft resolve at runtime).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `6dec7e4e6`

**chore: delete dead src/dotNet/Portable tree; refresh docs**

```
Audit: no .csproj in the repo references src/dotNet/Portable/*. The portable
build compiles tray dialogs from src/Portable/* and the engine from dotNet/*;
the non-portable DesktopPet.csproj includes none of these forms. So the
src/dotNet/Portable/ tree (FormOptions/AboutBox/FormHelp/Install/Settings +
NAudio.dll, 15 files) was dead — the same stray copy where the Phase-1 Speech
tab and the first cut of the Phase-4 AI tab were mistakenly written and never
compiled. Deleted; portable build still green (x64, exit 0).

Docs refreshed to current:
- handoff.md rewritten (phases 1-4 + 2.8/3.6 done; repo-layout gotcha; correct
  build recipe incl. the eSheep process-name kill; emotion->animation map).
- Readme.md status -> "Phases 1-4 shipped"; corrected build instructions.
- BACKLOG.md status block; Speech-tab port logged as a known regression/TODO.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `c1d967866`

**feat(ai): phase 4 — AI settings tab in the options dialog**

```
Expose the ai-settings.json fields in the tray Options dialog so the AI layer
is configurable without hand-editing JSON.

- FormOptions "AI" tab (built programmatically, Designer untouched): Ollama
  endpoint, text/vision model dropdowns populated from GET /api/tags (best-
  effort, off the UI thread), Use-vision toggle, global-hotkey enable + live-
  validated hotkey box, idle-commentary toggle + min/max interval, and the
  auto-start-server / warm-up-on-launch checkboxes. Edits update an in-memory
  AiSettings; on dialog close it is saved and applied live.
- StartUp: new public ReloadAiSettings() (reloads JSON, drops the cached brain
  so endpoint/model/timeout/vision take effect on the next ask, re-registers
  the hotkey, arms/stops the idle loop). Extracted the shared ApplyAiTriggers()
  used by both InitAiTriggers (launch) and ReloadAiSettings (live).

IMPORTANT: the tab went into src/Portable/FormOptions.cs — the copy the portable
csproj actually compiles. There is a stray, NON-compiled duplicate at
src/dotNet/Portable/FormOptions.cs (where the Phase-1 Speech tab was mistakenly
added, which is why that tab never appeared). Verified live: tray -> Options ->
new "AI" tab renders with values loaded from JSON and model dropdowns populated.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `ddaccdb61`

**feat(ai): 2.8 emotion->animation mapping + 3.6 thinking cue**

```
Completes the two backlog items that shared the SetNewAnimation blocker.

- FormCompanion.TryPlayAnimation(name): additive public hook that resolves a name to
  an animation ID over Animations.SheepAnimations (case-insensitive) and plays
  it via the existing private SetNewAnimation. Returns false/no-op when the
  loaded pet XML has no such animation, so callers can pass a prioritized list
  and fall through gracefully on pets that lack those names.
- StartUp.EmoteAll(emotion) + EmotionAnimations(): maps the brain's emotion
  vocabulary (happy/sad/thinking/excited/confused/neutral) to prioritized eSheep
  animation candidates (flower/jump/boing/run/sleep/rotate); neutral/unknown
  play nothing. Wired into AskAboutScreen on the UI thread — a "thinking" cue on
  ask (3.6, replaces the plain "…") and the response emotion on reply (2.8).

Verified live: built clean (x64), launched pet, Ctrl+Alt+P -> speech bubble
rendered a context-aware remark and the pet emoted; no crash across the cycle.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-07-27  `de9060eaf`

**feat(ai): phases 2-3 — Ollama brain, triggers, launch warmup**

```
Additive local-LLM layer over the untouched WinForms pet engine:

- Ai/ backend: ICompanionBrainBackend seam, OllamaClient (/api/chat, non-streaming,
  format:json, vision images array, server auto-start + model warmup),
  AiBrain orchestrator (capture -> OCR/vision -> chat -> parse {text,emotion}),
  BrainResponse/ChatMessage DTOs, AiSettings JSON config, HotkeyListener.
- Triggers: global hotkey (Ctrl+Alt+P), opt-in idle-commentary loop with a
  luma change-detection gate, tray "Ask about my screen" item.
- Launch warmup: background PrepareAsync starts `ollama serve` if needed and
  preloads the active model so the first ask is fast.
- Engine touches (additive only): FormCompanion.IsBusy for the idle gate, FormSpeech
  StartPosition=Manual + flip-below rendering, x64 default in the csproj.

All verified live end-to-end (OCR ask, hotkey ask, idle ask). ai-settings.json
lives at %APPDATA%\DesktopPet\. Runtime src/DesktopPet.config gitignored.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-06-24  `22bfa3adb`

**feat(speech): solid black outline via single combined body+tail path**

```
Replaces the separate body/tail/seam-erase drawing with one closed
GraphicsPath (rounded rect with the tail notched into the bottom edge),
used for both the clip Region and a thick black stroke. The black edge
against the transparent region clips cleanly, giving the solid outlined
look from the reference without any seam or colour bleed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-06-24  `016484fba`

**fix(speech): white BackColor + drop layering to kill magenta bleed**

```
The remaining pink was anti-aliased white edges blending toward the
magenta BackColor underneath. With the Region already defining the
window shape, colour-keyed transparency is unnecessary: set BackColor
to white so any sub-pixel gap matches the bubble, and remove
WS_EX_LAYERED since shape now comes from Form.Region.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

### 2026-06-24  `3bb712ba8`

**fix(speech): use window Region to eliminate magenta corner bleed**

```
TransparencyKey pixel-matching fails on anti-aliased corner pixels.
Setting Form.Region to the bubble shape (rounded rect + tail) lets the
OS clip the window cleanly without any colour-keyed transparency.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `5aa76c022`

**fix(speech): tail tracks mouth position after screen-edge clamping**

```
After the bubble is clamped to the working area, _tailX is recomputed
as (anchorX - clampedBubbleLeft) so the tail always points at the pet's
mouth regardless of which edge pushed the bubble. Tail is bounded by
CornerRadius + TailBase so it never clips outside the rounded corners.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `9d1405e6a`

**fix(speech): restore original gray border; keep directional tail shape**

```
Reverts to the subtle gray (80,80,80) 1.5f border — the yellow was
only the reference image's color, not the intended style. Keeps all
the directional tail positioning from the previous commit.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `264112645`

**fix(speech): yellow border, directional tail anchored to mouth**

```
Redesigns FormSpeech to match comic-book style: thick yellow border
(4px, #FFC800), tail positioned near the left or right edge based on
facing direction (TailInset=36px from edge). Bubble is positioned so
the tail tip lands exactly over the pet's mouth. Passes faceLeft bool
from FormCompanion.Say() using PointToScreen for accurate screen coords.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `60460db5d`

**fix(speech): anchor tail to mouth side; add right-click trigger**

```
Say() now uses PointToScreen with a directional mouth offset (Width/3
left-facing, Width*2/3 right-facing) so the tail naturally trails toward
the pet's mouth rather than dead-centering. Right-clicking the sheep
triggers the speech bubble in non-debug mode.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `4ca92c30c`

**feat(phase-1): speech bubble layer with optional on/off setting**

```
Adds FormSpeech — a transparent topmost WinForms overlay with GDI+ rounded
rectangle + downward tail, typewriter reveal timer, and auto-dismiss. Wired
into FormCompanion.Say() / StartUp.SayAll(). Gated behind SpeechEnabled (default
on) with a SpeechDuration slider (2–30 s) in a new Speech tab in Options.
Tray menu gains a "Test Speech" item that hides when speech is disabled.
Builds clean against .NET Framework 4.8 / DesktopPet_Portable.csproj.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `12af968b7`

**Merge branch origin/master: adopt AI-context .gitignore entries**

```
Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-24  `208ec0c82`

**chore: fork setup — AI pet README + phased backlog**

```
Documents the intent of this fork: keep the WinForms physics engine
untouched and layer a local Ollama AI brain (speech bubbles, screen
awareness, reactive + idle commentary) on top.

BACKLOG.md covers 7 phases from speech rendering through vision-model
upgrade path. Reference implementations from openpets, Ghostpet-Prototype,
and screenpipe documented per phase.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

### 2026-06-16  `11a27edc7`

**Add missing agent/AI-context file gitignore entries**

```
Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

