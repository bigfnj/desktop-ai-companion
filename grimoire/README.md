# desktopPet Grimoire

> A subject-matter-expert knowledge base for the **desktopPet** engine, the **`animations.xml`**
> companion format, and the **eSheep** project lineage — written so the durable knowledge survives as
> this project ages. Last reconciled with the AI Edition runtime: **2026-07-29**.

> **Authority and scope:** Historical and ecosystem sections distinguish upstream behavior from
> the current AI Edition. For current product, privacy, support, and release policy, the authorities
> are [`Readme.md`](../Readme.md), [`PRIVACY.md`](../PRIVACY.md),
> [`SECURITY.md`](../SECURITY.md), and
> [`docs/RELEASE-CHECKLIST.md`](../docs/RELEASE-CHECKLIST.md). The AI Edition has materially modified
> the engine for validation, security, lifecycle, packaging, and multi-monitor correctness; it is
> not a byte-identical or behavior-identical upstream snapshot.

## What this project is (one paragraph)

**desktopPet** is a Windows desktop-pet application: a little animated creature (by default a sheep,
"eSheep") that walks around your screen, runs along the taskbar, falls with gravity, and climbs onto
the title bars of your open windows. The engine is **.NET Framework 4.8 WinForms** (C#). Every companion is a
single self-contained **`animations.xml`** file that embeds its sprite sheet and sounds as base64 and
describes its behaviour as a **probability-weighted animation state machine**; the runtime uses **Win32
P/Invoke** for the physics (window detection, gravity, full-screen awareness). The engine is a modern
reimplementation — by **Adriano Petrucci** (GitHub *Adrianotiger*) — of a 1990s 16-bit Windows
screen-mate that was itself based on Tatsutoshi Nomura's *"Stray Sheep"*. **This repo is the "AI
Edition" fork** (`bigfnj/desktop-ai-companion`), which layers offline fortunes and an optional multi-provider
AI brain onto a maintained, hardened version of the engine.

## Scope of this grimoire

Current AI Edition behavior is documented in the root [`Readme.md`](../Readme.md). This grimoire
focuses on the detailed **engine**, **companion format**, and **project lineage/history**, while identifying
places where current behavior intentionally differs from upstream.

## Table of contents

| Doc | Topic |
|-----|-------|
| [01 — History &amp; Lineage](01-history-and-lineage.md) | Nomura's *Stray Sheep* → 1990s 16-bit eSheep → Adrianotiger's C# reimplementation → this AI-Edition fork. Who Adriano Petrucci is, version timeline, corrected attributions. |
| [02 — Architecture](02-architecture.md) | Runtime architecture of the WinForms engine: entry point, rendering (magenta-keyed layered window), the timer-driven animation loop, the physics (border/gravity/window-walking via `EnumWindows`), multi-companion &amp; children, NAudio sound, settings. Grounded in `src/dotNet/`. |
| [03 — Companion XML Format](03-companion-xml-format.md) | **The crown-jewel reference** for `animations.xml` and its XSD: every element and attribute, the expression language, a worked example, and a "how to author a new companion" walkthrough. |
| [04 — Upstream, Forks &amp; Ecosystem](04-upstream-forks-ecosystem.md) | Upstream status/activity/license, the `web-esheep` JavaScript port, Shimeji and other desktop-pet engines, and how to pull upstream pet artwork. |
| [05 — Glossary &amp; FAQ](05-glossary-and-faq.md) | Definitions (sprite sheet, spawn, child, border/gravity, `only` flags, sync…) and practical questions. |

## Sources

Two kinds of sources back this grimoire:

- **In-repo (authoritative for the engine):** the C# under `src/dotNet/` (`Program.cs`, `StartUp.cs`,
  `Xml.cs`, `Animations.cs`, `FormCompanion.cs`, `ProcessIcon.cs`), the schema `Resources/animations.xsd`,
  example companions under `Companions/` (esheep64, neko), and `Changelog.md` / `Changelog.txt` / `index.html` /
  `Download.md`. Engine claims cite `file:member`.
- **Web (authoritative for lineage/ecosystem):** the upstream repo
  <https://github.com/Adrianotiger/desktopPet> and its wiki, the project home
  <https://esheep.petrucci.ch/>, the GitHub Pages site
  <https://adrianotiger.github.io/desktopPet/>, and the JS port
  <https://github.com/Adrianotiger/web-esheep>. URLs are cited inline.

Where a fact could not be fully verified it is prefixed **"unverified:"**. The most important such flag:
the identity of the programmer(s) of the *original* 16-bit screen-mate is **not** established — see
[01](01-history-and-lineage.md).
