# Bundled pet definitions

This directory retains the upstream `animations.xml` pet collection and historical gallery assets.
Desktop AI Companion does **not** currently offer an online pet-download catalog, and these files
are not approved for public redistribution. The `Pets/pets.json` file is a historical gallery index,
not a runtime download feed.

## Author and test a pet

A runtime pet is one bounded UTF-8 `animations.xml` file containing:

- header metadata and a base64-encoded **48x48 ICO** tray icon;
- a base64-encoded PNG sprite sheet with equal-sized grid cells;
- bounded spawn, animation, transition, child, and optional MP3 sound definitions.

The current element-by-element format, limits, and worked authoring example are in
[`grimoire/03-companion-xml-format.md`](../grimoire/03-companion-xml-format.md). The upstream wiki and online
editor can be useful historical references, but the repository's XSD and shared
`CompanionXmlValidator` define what this build accepts.

Build the maintained validator with a locked restore:

```powershell
msbuild .\Tools\PetTester.sln -restore `
  -p:Configuration=Release -p:Platform=x64 -p:RestoreLockedMode=true
.\build\PetTester\bin\Release\x64\PetTester.exe .\Pets\your-pet\animations.xml
```

You can also drag a local `animations.xml` onto a running pet or start
`DesktopPet.exe localxml=path\to\animations.xml`. Only bounded, reparse-free local XML files are
accepted. Remote `webxml=` and legacy `install=` arguments are disabled; a rejected dropped pet
leaves the current pet unchanged.

## Contribution and rights requirements

A proposed directory should have a unique, filesystem-safe name and contain:

- `animations.xml`;
- a `README.md` with accurate authorship, source, and license information; and
- optionally, `icon.png` as historical gallery artwork (the runtime icon still lives as ICO bytes
  inside `animations.xml`).

Do not update `pets.json` as though it enables application downloads. Before any pet or gallery
asset can ship, retain source-specific evidence covering the exact bytes, authorship, license or
permission, attribution obligations, and redistribution scope. The fail-closed
`@downloadable-pet-art` record in
[`packaging/source-rights-evidence.json`](../packaging/source-rights-evidence.json) remains
`releaseApproved: false`; do not change that approval state without the complete reviewed evidence
required by [`docs/RELEASE-CHECKLIST.md`](../docs/RELEASE-CHECKLIST.md).
