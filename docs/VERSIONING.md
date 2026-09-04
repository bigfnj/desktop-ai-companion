# Versioning

Three numbers, deliberately independent. This file exists because the scheme was consistent in practice but
written down nowhere, so it had to be reverse-engineered from the source to answer "what are we doing?".

## 1. The host product version

One value, in [`ProductVersion.props`](../ProductVersion.props): `DesktopAICompanionVersion` (and
`DesktopAICompanionAssemblyVersion`, the same value with a `.0`). Plain `MAJOR.MINOR.PATCH`, no zero padding.

It drives the exe, the assembly metadata, the MSI authoring, the release verification and the git tag, and it
is the only version a user thinks of as "the app version". Bump it when engine code changes and you intend to
tag; `vX.Y.Z` must match it or `release.yml` refuses the tag.

- **PATCH** — a fix inside the exe, no new capability. (1.9.5 → 1.9.6: a hanging companion could not let go.)
- **MINOR** — a new user-visible capability in the host, or an additive ABI member.
- **MAJOR** — unspent so far. Reserve it for a break in settings or the ABI.

**It MUST be bumped in the same change as any plugin-ABI edit.** `DesktopAICompanion.Contracts` stamps its
`FileVersion` from it, and Windows Installer skips refreshing a file whose version did not change, so an ABI
change shipped without the bump installs a stale `Contracts.dll` and every module fails to resolve the new
types.

## 2. Each module's own version

`ModuleInfo.Version`, a string literal in each `modules/<Name>/<Name>Module.cs`. Plain `MAJOR.MINOR.PATCH`,
independent of the host and of every other module.

- **PATCH** — a fix, a UI tidy, or **picking up a change from source-linked code**. That last one is not
  optional and is the most common reason a number moves: a module that source-links the converter engine goes
  stale the moment that engine changes, and `Test-ModulePublishFreshness.ps1` fails CI until it is republished.
  (Companion Studio 1.4.18 exists only because the emitter changed under it.)
- **MINOR** — a new capability the user can see. (Companion Studio 1.5.0: the behaviour timeline.)
- **MAJOR** — unspent. Reserve it for dropping a setting or changing its meaning.

**The number lives in three places and the gate fails unless they agree**: the source, `modules-dist/modules.json`
and `catalog.json`. `New-ModulePublish.ps1 -ModuleId <id> -Commit` updates all three in the one order that
works. The in-app Update button compares the catalog's number against the installed one, so a mismatch either
offers an update forever or never offers one at all.

## 3. `MinHostVersion`

Also on `ModuleInfo`: the oldest host the module will load on. The loader refuses the module below it.

Bump it **only when the module actually calls an ABI member that host introduced** — not on every host
release, or a module stops working on hosts that could have run it perfectly well. Current values are a
history of exactly that: Blinking LED asks for 1.4.0, Companion Studio 1.8.0 (`TryReadTypeXml`), Reminder and
Remembrance 1.9.0.

**Sequencing:** publish a module only AFTER the host release its `MinHostVersion` names has shipped, or the
catalog offers users a module their host correctly refuses.

## What is NOT a product version

`DesktopAICompanion.Contracts` has **`AssemblyVersion` frozen at `1.0.0.0`**, for ever. That is the ABI *binding*
identity: a module built against any Contracts must resolve against any other, so it cannot move. Its
`FileVersion` separately tracks the product version, for the installer reason above. Module assemblies declare
no version at all and are therefore `1.0.0.0` too — the version a user sees in the Modules pane exists only in
that `ModuleInfo.Version` string, never in assembly metadata.

## Delivery, since it decides whether a bump needs a tag

| what changed | ships via | needs a host release? |
|---|---|---|
| a companion, a pack, `catalog.json` | `master` over raw.githubusercontent | no |
| a module | `modules-dist/` over raw.githubusercontent | no |
| engine code in the exe | the MSI / portable ZIP on a `v*` tag | **yes** |
| an embedded resource (e.g. `companion-thumbnails.zip`) | the exe | **yes** |

Merging to `master` IS the companion and module publish. The MSI and ZIP bundle neither.

## Companions have no version, and that is on purpose

A companion catalog entry carries `id`, `name`, `author`, `url`, `sha256`, `bytes` — no version. Adding one would
mean maintaining a number by hand for 53 companions, and it would be silently wrong the first time someone forgot.

**A companion's freshness is decided by its CONTENT HASH instead** (`CompanionProvenance`, host 1.9.7+). The catalog already
records the SHA-256 of the exact bytes it serves, and the installer writes those bytes verbatim, so hashing the
installed `animations.xml` answers "is this current?" with data that already exists and cannot drift from the
thing it describes.

This was not always true, and the gap was invisible: until 1.9.7 the Companions pane diffed the catalog **by id
alone**, so a companion you already had was filtered out of "available to download" however much its content had
changed. A corrected companion reached new downloads only, while the pane reported *"you already have every
available companion"*. Correcting 31 companions in one change is what surfaced it.

Alongside each installed companion the app now writes **`catalog.sha256`**, the hash as installed. That is what
separates "the catalog moved on" (update silently) from "you edited this" (warn before replacing). A companion with
no stamp cannot be told apart from an edited one and is **not** assumed safe, so:

> **Every companion installed before 1.9.7 will warn once** on its first update, because nothing recorded what was
> installed. That is a transition cost, not a bug. Backfilling the stamp from the current file was considered
> and rejected: it would assert the file is unmodified, which is exactly the thing the stamp exists to not
> guess at.

`<header><version>` inside a companion's XML is a CONVERTER format marker (1.0 flat weights → 1.1 damped → 1.2
ceiling → 1.3 jump arc). It gates the migration verbs and is not an update signal; the host reads it only for
display.
