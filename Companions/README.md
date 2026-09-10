# Bundled and downloadable companion definitions

Each subdirectory is one companion: a bounded UTF-8 `animations.xml`, plus optional historical gallery
artwork. There are **53** of them, and all 53 **are** published for download.

`companions.json` is the runtime manifest, not a historical gallery index. It maps each directory to its
`folder`, `author` and `lastupdate`, and `packaging/New-ContentCatalog.ps1` **hard-fails** without it
rather than falling back to title-cased folder ids — that fallback once shipped a catalog in which every
companion was named after its folder and had no author. Every entry in `catalog.json`'s `companions`
array is generated from this directory plus that manifest, with a branch-pinned raw URL and a SHA-256
that the app verifies before install.

## Author and test a companion

A runtime companion is one bounded UTF-8 `animations.xml` file containing:

- header metadata and a base64-encoded **48x48 ICO** tray icon;
- a base64-encoded PNG sprite sheet with equal-sized grid cells;
- bounded spawn, animation, transition, child, and optional MP3 sound definitions.

The current element-by-element format, limits, and worked authoring example are in
[`grimoire/03-companion-xml-format.md`](../grimoire/03-companion-xml-format.md). The upstream wiki and
online editor are useful historical references, but `Resources/animations.xsd` and the shared
`CompanionXmlValidator` define what this build actually accepts.

To test one, drag a local `animations.xml` onto a running companion, or start:

```powershell
.\DesktopAICompanion.exe localxml=path\to\animations.xml
```

Only bounded, reparse-free local XML files are accepted. Remote `webxml=` and legacy `install=`
arguments are recognised and refused. A rejected dropped file leaves the current companion unchanged.

There is no separate validator executable. The previous instructions here built a `Tools\PetTester.sln`
against a `.\Pets\` directory; neither has existed since the rename, and validation now lives in the app
itself. To convert a Shimeji skin instead of authoring by hand, use `tools/ShimejiConvert`.

## Contribution and rights requirements

A proposed directory should have a unique, filesystem-safe name and contain:

- `animations.xml`;
- a `README.md` with accurate authorship, source, and license information; and
- optionally, `icon.png` as gallery artwork (the runtime icon lives as ICO bytes inside
  `animations.xml`).

Add the companion to `companions.json` with its author, then regenerate `catalog.json` — that is what
makes it downloadable, so the manifest is deliberately the thing you update.

**Rights are not machine-checked any more.** The fail-closed `@downloadable-pet-art` record in
`packaging/source-rights-evidence.json` no longer exists; that gate was retired with the enterprise
release pipeline and has not been replaced. Sprite redistribution is listed as an open blocker in
[`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md), which records what is known per asset and states
plainly that a complete grant is not held for every one. Before shipping a new companion, retain
source-specific evidence covering the exact bytes, authorship, license or permission, attribution
obligations, and redistribution scope. The depicted characters remain their owners' intellectual
property regardless of who drew the sprites.
