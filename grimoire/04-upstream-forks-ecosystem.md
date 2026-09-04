# 04 — Upstream, Forks &amp; Ecosystem

Where this code comes from, how alive upstream is, the sibling projects that share (or don't share) its
companion format, and where to get more companions. Web facts were gathered 2026-07-27 via the GitHub API and the
project sites; treat star/fork counts as a snapshot. Uncertain items are flagged **unverified:**.

## 1. Upstream: `Adrianotiger/desktopPet`

- **Repo:** <https://github.com/Adrianotiger/desktopPet> — the C#/.NET WinForms engine this repo forks.
- **What it is:** a recreation of the 1990s eSheep desktop companion; behaviour lives in a single
  `animations.xml` per companion (see [03](03-companion-xml-format.md)). Ships ~11+ built-in companions (original eSheep,
  seven rainbow gSheep variants, Bunny, Asuna, Neko, Pingus, plus community companions).
- **Activity (snapshot 2026-07-27):** **~1,125 stars, ~114 forks, ~404 commits** on `master`; created
  **2015-12-19**, last code push **2025-12-08** — actively (if slowly) maintained a decade on
  (GitHub API, <https://api.github.com/repos/Adrianotiger/desktopPet>).
- **Latest release:** **`v1.3.2`, 2025-08-08** (fixed local-XML loading)
  (<https://github.com/Adrianotiger/desktopPet/releases>).
- **Distribution:** portable single-`.exe` (GitHub Releases), a **Microsoft Store UWP** app
  (`9MX2V0TQT6RM`, see [`Download.md`](../Download.md)), and an Android build linked from the Pages site.

### License — important

**The desktop engine has _no license_.** GitHub's API reports `license: null`, there is **no `LICENSE`
file** in the repo, and the README has no license section
(<https://api.github.com/repos/Adrianotiger/desktopPet>). By default that means **"all rights reserved"**
— the source is public but not granted for reuse/redistribution under any open-source terms. This
matters for the [AI-Edition fork](01-history-and-lineage.md#4-this-repository-the-ai-edition-fork) if it
is ever distributed:

- The **engine code** carries no explicit grant. Reuse/redistribution rights are legally unclear —
  **unverified:** any permission would have to come from Adriano directly.
- The **default sprite art** derives from Nomura's *Stray Sheep* character (the esheep64 companion credits
  *"Image rip by LiL_Stenly"*) — third-party IP, not the project's to relicense.
- Contrast: the sibling **`web-esheep` is GPL-3.0** (see §3). The two repos are licensed differently.

A future maintainer planning public distribution should resolve licensing explicitly rather than assume
the fork inherits an open license.

### Wiki = the authoring spec

The wiki (<https://github.com/Adrianotiger/desktopPet/wiki>, ~13 pages) is the canonical online spec for
`animations.xml`: *Introduction, Structure, Header, Image, Spawn, Animation, Child, Coordinate, Next*
(plus Home/Help). [03 — Companion XML Format](03-companion-xml-format.md) is the offline, code-verified companion to
it. There is also an **online editor** at <https://esheep.petrucci.ch> and a downloadable **offline
editor** (release tag `editor0.2`), mirrored in this repo under [`Tools/`](../Tools) (`PetEditor`,
`PetTester`) and [`Manual - online editor.docx`](../Manual%20-%20online%20editor.docx).

## 2. This fork: `bigfnj/desktop-ai-companion` (AI Edition)

A downstream fork with offline fortunes, smart local matching, and an optional multi-provider AI
brain (Ollama plus OpenAI-compatible local or remote endpoints). It retains the upstream companion XML
elements but materially changes the engine through strict validation, bounded resources, safer local
loading, lifecycle fixes, and multi-monitor corrections. Current product behavior is documented in
[`Readme.md`](../Readme.md); lineage context is in
[01 §4](01-history-and-lineage.md#4-this-repository-the-ai-edition-fork).

## 3. The JavaScript / web port: `Adrianotiger/web-esheep`

- **Repo:** <https://github.com/Adrianotiger/web-esheep> — Adriano's own browser-embeddable port.
  **The correct name is `web-esheep`**; "esheep.js" and "tobiasy/esheep" are **not** it.
- **Same companion format — portable companions.** It reads the **same `animations.xml`** (images + animations +
  movement commands all in the XML), so companions move between the desktop engine and the web version
  unchanged (README / wiki "Add new sheep to webpage",
  <https://github.com/Adrianotiger/web-esheep/wiki/Add-new-sheep-to-webpage>). This is the single most
  important reason to treat the XML schema as a durable, cross-implementation contract.
- **License:** **GPL-3.0** (<https://api.github.com/repos/Adrianotiger/web-esheep>) — unlike the
  unlicensed desktop repo.
- **Activity (snapshot):** ~125 stars, ~22 forks; created 2017-11-13, last push 2026-07-20.
- **Embed:**
  ```html
  <script src="https://adrianotiger.github.io/web-esheep/dist/esheep.min.js"></script>
  <script> new eSheep().Start(); </script>
  ```
  Built with Yarn (output in `dist/`). Also ships a Tampermonkey userscript (`esheep.user.js`) to drop
  the sheep onto pages like Google/Bing. Demos: <https://adrianotiger.github.io/web-esheep/samples/> ;
  companion gallery: <https://adrianotiger.github.io/web-esheep/companions/>.
- **npm:** **unverified:** no official npm package confirmed — distribution is the GitHub Pages `dist/`
  script, not a confirmed `esheep`/`esheepjs` package.
- **Notable fork:** **`Emupedia/emupedia-app-esheep`** (<https://github.com/Emupedia/emupedia-app-esheep>),
  the same web eSheep used inside the Emupedia project.

## 4. The original binary: `lwu309/Scmpoo`

<https://github.com/lwu309/Scmpoo> — a reverse-engineering project of the **original** 16-bit *"Stray
Sheep — The Screen Mate"* (a Windows NE 16-bit executable). Not a fork of desktopPet, but the closest
primary-source artifact of the ancestor Adriano reimplemented. Useful if a maintainer ever wants to
compare behaviour against the 1990s original. See [01 §1](01-history-and-lineage.md#1-the-original-nomuras-stray-sheep-and-the-1990s-screen-mate).

## 5. Parallel engines (cousins, not ancestors)

These share the *idea* of a window-aware desktop mascot but are **independent codebases with
incompatible formats** — do not conflate them with the eSheep lineage.

### Shimeji

- **Origin:** Shimeji (しめじ) is a **Java** desktop mascot originally by **Yuki Yamada of Group Finity**
  (Japan). **unverified:** exact first-release date.
- **Shimeji-EE ("English Enhanced"):** the widely-used maintained line, by **Kilkakon**
  (<https://kilkakon.com/shimeji/>), which translated it to English and extended it.
- **Mechanics:** like desktopPet it is **XML-driven** (behaviours/actions defined in XML with swappable
  image sets) and does window-climbing, self-throwing, and "breeding"/cloning. **The XML formats are NOT
  compatible** between Shimeji and eSheep — this is convergent design, not shared code. A Shimeji image
  set cannot be dropped into desktopPet, or vice-versa.
- **Active forks worth knowing:** `Valkryst/VShimeji` (UI/perf), `gil/shimeji-ee` and
  `gonzalovsilva/Shimeji-ee` (Mac focus), `DalekCraft2/Shimeji-Desktop`. Shimeji has by far the largest
  custom-mascot community.

### Desktop Goose

- By **Samperson**, on itch.io (<https://samperson.itch.io/desktop-goose>), ~2020. A deliberately chaotic
  goose that steals the cursor, honks, and drops memes; free, Windows/macOS. The anarchic opposite of the
  calm screen-mate — different engine, different intent.

### Adjacent, but a different technical family

**unverified / loosely related:** modern mascot delivery via **Live2D** desktop widgets or VTuber-style
overlays are a distinct technical family from sprite-sheet + XML engines. "WebFishing" is a game, not a
companion engine — not part of this ecosystem.

## 6. How to pull upstream companion artwork

The companions are just data — you can bring any upstream/community companion into this fork.

1. **From the upstream repo's `Pets/` folder** (<https://github.com/Adrianotiger/desktopPet/tree/master/Pets>):
   each companion is a folder with `animations.xml` (+ `README.md`, `icon.png`). Grab the folder or just the
   `animations.xml`. This repo already vendors many under [`Companions/`](../Companions) (see
   [`Companions/companions.json`](../Companions/companions.json) for the manifest of folder / author / date).
2. **Load a companion at runtime** without installing it: run
   `DesktopAICompanion.exe localxml=path\to\animations.xml`, or **drag-and-drop a local `animations.xml` onto
   a running companion**. The file must be a bounded, reparse-free local file. An invalid command-line companion
   stops startup with an error; an invalid dropped companion leaves the current companion unchanged.
   `webxml=` and legacy `install=` sources are explicitly rejected. See
   [02 §6.4](02-architecture.md#64-interaction--mouse--drag).
3. **From the web-esheep companion gallery** (<https://adrianotiger.github.io/web-esheep/companions/>) — same XML
   format, so those `animations.xml` files work in the desktop engine too.
4. **Author your own** — see the walkthrough in [03 §11](03-companion-xml-format.md#11-how-to-author-a-new-companion--walkthrough).
5. **Registering a vendored companion in this repo:** drop the folder under `Companions/` and add its entry to
   `Companions/companions.json` (`folder`, `author`, `lastupdate`), per [`Companions/README.md`](../Companions/README.md).

## Sources

GitHub API + repos/sites, 2026-07-27:
<https://github.com/Adrianotiger/desktopPet> ·
<https://api.github.com/repos/Adrianotiger/desktopPet> ·
<https://github.com/Adrianotiger/desktopPet/releases> ·
<https://github.com/Adrianotiger/desktopPet/wiki> ·
<https://esheep.petrucci.ch/> ·
<https://adrianotiger.github.io/desktopPet/> ·
<https://github.com/Adrianotiger/web-esheep> ·
<https://api.github.com/repos/Adrianotiger/web-esheep> ·
<https://github.com/Emupedia/emupedia-app-esheep> ·
<https://github.com/lwu309/Scmpoo> ·
<https://kilkakon.com/shimeji/> ·
<https://samperson.itch.io/desktop-goose>.
In-repo: [`Download.md`](../Download.md), [`Companions/companions.json`](../Companions/companions.json), [`Companions/README.md`](../Companions/README.md).
