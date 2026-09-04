# 01 — History &amp; Lineage

The origin story of the sheep, the engine, and this fork — with confidence flags where the record is
thin. Web sources are cited inline; in-repo evidence is cited as file paths.

## The one-line lineage

> **Tatsutoshi Nomura's *"Stray Sheep"*** (1994 anime, Fuji TV) → **16-bit "Stray Sheep — The Screen
> Mate"** Windows shareware (~1995–96, distributed as *eSheep / Scmpoo / Poo / Poe*) → **Adrianotiger/desktopPet**,
> a from-scratch C#/.NET reimplementation (2015–present, the `animations.xml` engine) → **web-esheep**,
> Adriano's browser port sharing the same pet format (2017–present) → **this repo, the "AI Edition" fork**
> (`bigfnj/desktop-ai-companion`, 2026), which adds a local-LLM brain on top of the untouched engine.

**Shimeji** and **Desktop Goose** are *parallel, independent* desktop-pet engines — cousins, not
ancestors. See [04 — Ecosystem](04-upstream-forks-ecosystem.md).

---

## 1. The original: Nomura's "Stray Sheep" and the 1990s screen-mate

The companion is not an original character. It comes from Japanese animator **Tatsutoshi Nomura's *"Stray
Sheep"***, a 1994 series of five-minute animation shorts aired at midnight on **Fuji Television**
(<https://adrianotiger.github.io/desktopPet/Pets/Info.html>; corroborated by Adafruit's 2019 write-up,
<https://blog.adafruit.com/2019/10/25/windows-desktop-companions-esheep-from-the-1990s-are-back-windows-desktop/>).
The franchise later spread to books, merchandise, and PlayStation games.

Around **1995–1996** a 16-bit Windows desktop screen-mate of the character appeared — a little sheep
that walked on the desktop, climbed windows, and sat on the taskbar. It circulated under several names:
**eSheep, Scmpoo, "Screen mate Poo," "Poe," and "Stray Sheep — The Screen Mate,"** and ran on Windows
3.11 / 95 (Info.html; general web search). This repo's own front page remembers it directly:

> *"Can you remember this application from the '95? This nice sheep covered our desktops for years :D
> Since this application was a 16-bit version and it doesn't work anymore on Windows 7/8/10, I wrote a
> little application in c# to see this sheep again on the desktop!"* — [`index.html`](../index.html)

The upstream repo description is *"Remembering the lovely eSheep (stray sheep) from 1995"*
(<https://github.com/Adrianotiger/desktopPet>). The changelog even calls out reviving specific old
mates: *"NEKO, another mate from 1995 is now available"* ([`Changelog.md`](../Changelog.md), v1.2.1).

### Attribution caveats (read this before repeating a "fact")

- **unverified — likely wrong:** the popular attribution of the original eSheep to *Kaveh Nadjmabadi /
  "Blabsoft"* could **not** be substantiated. A targeted search connected that name to the sheep nowhere.
  Do **not** state it as fact.
- **unverified:** the identity of the **programmer(s)** who wrote the actual 16-bit `.exe` is not clearly
  established; it spread as shareware under the Stray Sheep franchise. The *character/art* attribution to
  **Nomura** is well-supported; the *code* authorship is not.
- **Adriano's project is explicitly a homage, not the original code.** The in-repo pet info page states:
  *"this is not the original sheep! As the original does not work on 64-bit systems. It is just a copy of
  the original one!"* (<https://adrianotiger.github.io/desktopPet/Pets/Info.html>). The default sheep's
  own `<info>` credits the sprite art: *"Image rip by LiL_Stenly"* ([`Companions/esheep64/animations.xml`](../Companions/esheep64/animations.xml)).
- **Primary-source archaeology:** a community reverse-engineering of the *original* 16-bit binary exists —
  **`lwu309/Scmpoo`** (<https://github.com/lwu309/Scmpoo>), *"Reverse engineering project of STRAY SHEEP
  The Screen Mate, a Windows New Executable 16-bit application."* This is the closest thing to a
  ground-truth artifact of eSheep's ancestor.

---

## 2. Adriano Petrucci and the C# reimplementation

**"Adrianotiger" is developer Adriano Petrucci.** His personal eSheep site lives on his own domain,
`petrucci.ch`, which confirms the handle↔name link (<https://esheep.petrucci.ch/>). He began the modern
project because the 16-bit original no longer runs on 64-bit Windows, rewriting it from scratch in C#.

Two web homes, with different jobs:

- **<https://esheep.petrucci.ch/>** — the project's hub for authors and users: the UWP (Microsoft Store)
  app, the legacy Win7/8 `.exe`, an **online companion editor** ("create your own companion" and publish it), base64/
  icon converter utilities, and a dev blog. Newer builds *"download new mates directly from GitHub."*
  The XSD's target namespace is literally this URL: `https://esheep.petrucci.ch/`
  ([`Resources/animations.xsd`](../Resources/animations.xsd)).
- **<https://adrianotiger.github.io/desktopPet/>** — the GitHub Pages "Desktop Pet (eSheep 64bit)" site
  (a Jekyll site; this repo carries its `_config.yml`, `_layouts/`, `_posts/`): downloads (including an
  Android build), a browsable companion gallery, and news. Distribution-focused; the authoring **spec lives in
  the wiki**, not this page.

The repo ships **built-in companions**: the original eSheep, seven rainbow "gSheep" color variants
(blue/green/orange/pink/purple/red/yellow), plus Bunny, Asuna, Neko, Pingus, and community additions
(fox, mareep, pikachu, sylveon, ham-ham, mimiko…). See [`Companions/companions.json`](../Companions/companions.json) for the
full manifest with authors and dates.

### Version timeline

From this repo's [`Changelog.md`](../Changelog.md) / [`Changelog.txt`](../Changelog.txt) (desktop track)
and the upstream releases API (<https://github.com/Adrianotiger/desktopPet/releases>). Numbering is
**non-linear** — a UWP "2.x" Store track ran in parallel with the "1.2.x/1.3.x" desktop track.

| Date | Milestone |
|------|-----------|
| **2015-12** | `v0.1` first beta: sheep walks/runs on the taskbar. Repo created 2015-12-19. |
| 2015-12 → 2016-11 | `0.2`–`0.9.8` betas: window detection &amp; walking, gravity/taskbar, border collision, child animations (0.7), NAudio sounds (0.9.6), XML-serialization rewrite (0.9.2), the online XML generator. |
| **2017-01-01** | **`v1.0.0`** — first stable; full-screen detection (hide behind movies). |
| 2017 | `1.0.1`–`1.0.7`: stability, code-signing certificates, "start N companions at launch" (up to 16). |
| **2019** | `1.2.0`–`1.2.3`: **portable build to match the UWP Store version**, multi-screen, HTTPS companion URLs, **GitHub-hosted companion downloads** ("GitHub-Mates"), the **new offline editor**, Neko added. A parallel UWP "2.0/2.1" Store track shipped the same period. |
| 2021 | `desktop1.2.5`/`1.2.6`: gSheep rainbow variants, respawn/blink fixes. |
| 2022-08 | `1.3.1`: HD/monitor scaling, dynamic companion list. |
| **2025-08-08** | **`v1.3.2`** — current upstream latest (fixed local-XML loading). |
| 2025-12-08 | last upstream code push (community companions added). |

(Star/fork counts and exact release facts are in [04 — Ecosystem](04-upstream-forks-ecosystem.md).)

---

## 3. The browser port: web-esheep

In **2017** Adriano ported the pet to the browser as **`Adrianotiger/web-esheep`**
(<https://github.com/Adrianotiger/web-esheep>) — *"web based esheep (remembering the esheep from '95)."*
Crucially, **it reuses the same `animations.xml` format**, so a companion authored for the desktop engine runs
in a web page and vice-versa. This is the reason the XML format (documented in
[03](03-companion-xml-format.md)) is worth treating as a portable, long-lived asset rather than an
implementation detail. (License and embed details: [04 — Ecosystem](04-upstream-forks-ecosystem.md).)

---

## 4. This repository: the "AI Edition" fork

This repo (`D:\.claude\projects\desktopPet`) is **`bigfnj/desktop-ai-companion`**, a fork of
`Adrianotiger/desktopPet` (`upstream` remote = Adrianotiger, never pushed to; `origin` = bigfnj — see
[`handoff.md`](../handoff.md)). Its thesis, stated in [`Readme.md`](../Readme.md) and
[`BACKLOG.md`](../BACKLOG.md) began with a purely additive local-LLM concept. The implemented product
now also hardens and corrects the physics/animation engine, and supports Ollama plus generic
OpenAI-compatible local or remote providers. The companion can look at the screen (OCR or a screenshot),
ask the configured provider, speak a short remark in a bubble, and play an animation matching the
model's emotion hint.

That AI layer (speech bubble `FormSpeech`, `dotNet/Ai/*`, emotion→animation mapping, the AI options tab)
is **already documented** in [`handoff.md`](../handoff.md) and [`BACKLOG.md`](../BACKLOG.md), and is out
of scope for this grimoire by design — the grimoire preserves the engine, format, and lineage. What's
worth recording here for lineage purposes:

- The supported x64 executable and process are named **`DesktopAICompanion.exe`**. Portable and per-user MSI
  packages share the same runtime payload, with a portable marker selecting beside-the-executable
  data storage.
- The engine descends from the 2015-era Adriano codebase and still targets .NET Framework 4.8, but
  current validation, security, lifecycle, and geometry behavior is fork-specific.
- The fork adds no new companion XML elements, but its mirrored XSD and semantic validator require and
  bound fields more strictly than historical builds.

---

## 5. Why this history is worth preserving

- The **character predates the code by two decades** and is a licensed-in-spirit homage to Nomura's
  work — relevant if the project is ever distributed more widely (see the licensing note in
  [04](04-upstream-forks-ecosystem.md)).
- The **"eSheep" name, the magenta transparency key, the taskbar/window-walking behaviour, and the
  `animations.xml` schema** are all deliberate re-creations of the 1990s original's feel; knowing that
  explains otherwise-odd choices (e.g. why the engine hunts for window title bars to climb).
- The record around the *original* programmer is genuinely uncertain; this doc flags it so future
  maintainers don't launder a guess into a citation.

## Sources

- In-repo: [`index.html`](../index.html), [`Changelog.md`](../Changelog.md),
  [`Changelog.txt`](../Changelog.txt), [`Companions/companions.json`](../Companions/companions.json),
  [`Companions/esheep64/animations.xml`](../Companions/esheep64/animations.xml),
  [`Resources/animations.xsd`](../Resources/animations.xsd), [`Readme.md`](../Readme.md),
  [`handoff.md`](../handoff.md).
- Web: <https://github.com/Adrianotiger/desktopPet> · <https://esheep.petrucci.ch/> ·
  <https://adrianotiger.github.io/desktopPet/> · <https://adrianotiger.github.io/desktopPet/Pets/Info.html> ·
  <https://github.com/Adrianotiger/web-esheep> · <https://github.com/lwu309/Scmpoo> ·
  <https://blog.adafruit.com/2019/10/25/windows-desktop-companions-esheep-from-the-1990s-are-back-windows-desktop/>.
