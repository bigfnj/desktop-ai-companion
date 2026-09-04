<p align="center">
  <img src="docs/images/showcase.png" alt="A pasture of DesktopAICompanion companions under a blue sky" width="640">
</p>

# 🐑 desktopPet — AI Edition

**A desktop companion that actually pays attention.** A little sheep (or a fox, or Pikachu, or whatever you
drop in) walks across your screen, climbs your windows, naps on the taskbar, and pipes up with a fitting
one-liner when you poke it. Poke it too much and it gets sassy and rockets off to a bathtub. That's the
toy, and the base runs with **no internet, no account, no GPU**.

Underneath, it is built two ways on purpose:

<img align="right" width="72" src="Companions/pikachu/icon.png" alt="Pikachu">

- **🧩 Modular.** The install ships *lean* — just the companion. Everything else (smart fortunes, an AI brain,
  a companion editor, calendar reminders, even a meeting recorder) is an **optional module** you add from an
  in-app catalog: it shows each module's permissions up front and verifies every download by SHA-256
  before a line of its code runs. Take what you want, skip the rest, and it all updates in place.
- **🤖 AI-optional, local-first.** The clever parts work fully offline — a tiny on-CPU model reads what's
  on your screen and picks a fortune that *fits* it. Want more personality? Flip on the **AI brain** and
  the companion riffs on your screen through any OpenAI-compatible provider: a **local Ollama** so nothing
  leaves the box, or a cloud key if you prefer. It's **off until you say so**.
- **🗣️ One companion speaks for you.** With several companions out, a message meant for *you* (a reminder, a fortune)
  comes from ONE of them, not all of them chanting in unison. **Preferences → Speech → "Companion that speaks for
  the app"** picks which, offered from the companions actually on screen; anything a companion says about *itself* (a
  poke, a landing) still belongs to that companion.
- **🔔 It tells you about updates without nagging.** At launch — and again when you open Preferences — it
  checks whether a newer version exists, at most once an hour. Notify-only: nothing downloads or installs
  itself. The version in the corner of Preferences becomes a link to the releases page. Switch it off in
  **Preferences → Modules**.

> Fork of [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet); the original animation
> engine remains, with compatibility, correctness, and security fixes alongside the new fortune, AI, and
> module features. **Releases** are unsigned Windows x64 builds (ZIP + MSI) from a `vX.Y.Z` git tag —
> verify against `SHA256SUMS.txt`. The engine, artwork, fortune corpus, packs, and model are
> fan-compiled from mixed community/upstream sources; provenance is documented in
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md), not a blanket redistribution clearance.

---

## What it does

### 🧩 Modules (how you get everything below)
The installer and the portable ZIP ship **lean** — a companion engine and nothing else. Optional features
arrive from **Options → Modules**, which lists what's installed and what the online catalog offers,
shows each module's declared permissions *before* it downloads anything, and installs it after a
SHA-256 check against the published `catalog.json`. Modules load at startup, so installing or
removing one restarts the app (it reopens straight back on the Modules pane). Uninstalling removes
the module and its settings. Published today: **Fortunes**, **AI Brain**, **Companion Studio**, **Reminder**,
**Remembrance** and **Blinking LED**.

A module that fails to load says so, with the reason and a **Reinstall** that keeps its data — rather
than sitting there claiming it needs a restart forever.

Modules also **update in place**, and you no longer have to go looking. Opening the Modules pane already
shows any installed module with a newer published version, and *Update* keeps your settings, keys and
history (unlike uninstalling, which deletes them). A weekly background check writes down what it found, so
the pane can show it instantly and offline; turn that off under **Preferences → Modules** if you would
rather it never reached the network unprompted. Notify-only either way: nothing installs itself.

### 🔮 Fortunes (optional module, 100% offline)
<img align="right" width="68" src="Companions/fox/icon.png" alt="Fox">

One-liners — quotes, jokes, philosophy, Simpsons chalkboard gags, the abridged Bible, and more. The
module carries a built-in corpus of ~10,000 lines, so it has something to say the moment it installs,
before you download a single pack. Install it from **Options → Modules**, then from
**Options → Fortunes** you can:
- Dial the tone with one ordered **Content level** — *Clean only* / *Clean + edgy* / *Everything* /
  *Spicy only* — plus a separate **Filter profanity** switch for recognized profanity and explicit
  sexual content. A live count under the controls says how many fortunes the current selection
  actually leaves (and warns when that is none), and **Show me 5 examples** prints what it would say.
- **Pick sources** — 150+ per-source packs, grouped into collapsible collections with a filter box,
  so you can run only Simpsons + Futurama if you want.
- **Download packs** — *Check online for packs*, tick the ones you want, then *Download selected*;
  each download is SHA-256-verified against the published `catalog.json`.
- **Add your own** — *Import your own…* runs your `.txt` files (BSD `fortune` `%`-format or
  one-per-line) through a bounded, validating importer; or drop them straight into the folder and
  hit *Rescan*.

The schema-v2 target format is six tab-separated fields:
`source / topic / genre / level / profanity / text`. The conservative `prof` flag covers recognized
profanity and explicit sexual content. The embedded corpus and bundled packs are now this v2 format
(the classification pass is complete): the runtime reads their per-fortune topic and genre directly.
External five-field v1 packs are still accepted through the explicit compatibility path.
Source and content filters are hard constraints; an impossible selection produces an empty pool
rather than falling back to disallowed content.

### 🧠 Smart fortunes (part of the Fortunes module, offline, CPU-only, no keys)
A tiny sentence-embedding model (**bge-small**, ONNX, int8 — shipped inside the Fortunes module
package, which is why that module is ~30 MB) reads your foreground window and
picks a fortune that *fits what's on screen* — a C# file nudges it toward programming quips, a breakup
post toward heartbreak lines. It warms once in the background (cached after), avoids repeating the lines
it just showed, and falls back to the full library whenever it isn't sure. Toggle it in
**Options → Fortunes**.

### 🤖 AI brain (optional module, OFF by default — no provider requests until enabled)
<img align="left" width="68" src="Companions/neko/icon.png" alt="Neko">

A screen-commentary LLM: the companion glances at your screen (OCR or a vision model) and speaks an original
remark. It's **off out of the box**, so DesktopAICompanion does not contact the configured provider. When you
want it:
- Right-click the tray → **Enable AI**. **Disable AI** cancels DesktopAICompanion's provider requests. With
  Ollama, configured warm-up and unload operations also control that server's keep-alive model
  memory. Generic OpenAI-compatible providers expose no remote-memory control, so disabling
  DesktopAICompanion does not promise to free memory owned by those servers.
- Works with **any OpenAI-compatible provider** — Ollama (local, with keep-alive VRAM control),
  LM Studio, llama.cpp, OpenRouter, OpenAI, or a custom `/v1` endpoint. Pick one in **Options → AI**;
  cloud keys are stored **DPAPI-encrypted**.
- Ask on demand with the global hotkey (`Ctrl+Alt+P`) or the tray, or opt into occasional idle
  commentary.
- **Reads the screen with no extra install.** Windows' own OCR does the work out of the box; the
  module only falls back to **Tesseract** if you have it, and **Options → AI → Choose OCR engine…**
  lets you pick. **Test OCR** confirms which one answered.

> **Privacy:** fortunes and smart-fortunes are entirely local. The optional AI brain can send window,
> OCR, screenshot, persona, and recent-conversation context to the provider you configure after it
> is enabled. Remote providers require explicit cloud-data consent. See [`PRIVACY.md`](PRIVACY.md).

**Your VRAM stays yours.** A local model is only worth keeping in memory between remarks if you want speed
more than the memory, so **Model residency** is one choice — unload after each remark (the default), keep it
loaded for the session, or leave it to Ollama. The pane reads Ollama's own `/api/ps` and tells you what is
resident *right now* (model, GB, seconds until eviction) rather than quoting a default that
`OLLAMA_KEEP_ALIVE` may have overridden on your machine. And because a model claiming several GB beside a
game that already owns it can take the game down, **"stand down while a fullscreen app is running"** is on by
default: it releases whatever is loaded the moment a game appears and lets the free offline fortunes answer
instead.

### 🎨 Companion Studio (optional module, for people who make companions)
<img align="right" width="64" src="Companions/mareep/icon.png" alt="Mareep">

Check a companion's `animations.xml` before you use it. Three columns: the XML on the left (editable, with a
re-analyze that keeps up as you type and an atomic save), a report plus a colour-coded **reachability
map** in the middle, and the selected animation on the right — its real sprite frames, with playback,
and the transitions it can take. Click a legend colour to filter the map; it stays usable on a sheep
with 268 animations.

The point is the things you cannot see by eye: which animations **can never play** (nothing transitions
into them), and which frames are the sheet's blank tile — so "it shows nothing" stops looking like a
bug. It validates with the host's *own* parser, so its verdict is what the app will actually do, and it
previews the companion on your real desktop without installing or saving it.

It also **imports Shimeji skins**. Point it at a skin folder or a `.zip`, in either the classic desktop
format (an `actions.xml`/`behaviours.xml` config plus PNG sprites) or the newer Android bundle format
(a JSON manifest plus WebP sprites), and it converts the skin to a desktopPet pet, maps its behaviours
onto the app's own action model, keeps the artwork's per-pixel transparency, shows an honest report of
what could not be carried over, then previews and installs it.

### ⏰ Reminder (optional module)
<img align="left" width="66" src="Companions/blue_sheep/icon.png" alt="Ben the blue sheep">

Point the companion at your calendar and it announces each event a few minutes before it starts. Watch **up to
five calendars at once**, each read from a local JSON feed a work process writes, a **Calendar URL**
(Google's secret `.ics`, a published Outlook / Microsoft 365 calendar, or iCloud — recurrence and time
zones handled), or a **running desktop Outlook** over COM. Every calendar has its own name, its own
speech font/size/colour, and its own chime (browse for any WAV/MP3, or turn it off) so a Home event and a
Work event read and sound different.

Beyond the basics: one or several lead times (e.g. `15,5`), quiet hours, and a **hush while you're
presenting or in Do Not Disturb**. A meeting with a **Teams / Zoom / Google Meet / Webex** link gets a
one-click **Join** in the tray. Ask the companion to **read today's agenda** any time, or have it give you a
**morning briefing** at a set time. Optionally **skip meetings you've declined** or all-day events. And
you can add **your own typed reminders** independent of any calendar (`daily 09:00 Standup`, `every 60m
Stretch`, `in 30m Pizza`, `2026-09-01 14:00 Dentist`).

With several companions on screen you can also choose **which companion announces which calendar** — a per-calendar
**Reminder companion**, offered only from the companions actually out, so Work can come from one character and Home from
another. If the companion you picked isn't on screen when a reminder fires, it still speaks (through whichever companion
speaks for the app) rather than being swallowed.

That speech styling is available to any module through a shared helper, and **Preferences → Sound** now
has two independent switches — **companion sounds** (a companion's own effects) and **notification sounds** (module
chimes) — so you can silence one category without the other.

### 🎙️ Remembrance (optional module, new)
<img align="right" width="66" src="Companions/pink_sheep/icon.png" alt="Pearl the pink sheep">

A local "meeting memory" module: records the meeting (your microphone plus the system output over WASAPI
loopback), transcribes it **offline** with a local Whisper (whisper.cpp), names the file from the calendar
(via the Reminder module) or a timestamp, snapshots the screen on a hotkey, and purges the audio and
snapshots after 72 hours while keeping the transcript and the calendar attendee roster. Everything stays on
the machine. It needs a local Whisper set up (a `whisper-cli.exe` plus a model), and it records only from the
machine's own console session — a Remote Desktop session presents no real microphone or speakers. Requires
the v1.9.0 (or newer) host.

### 💡 Blinking LED (optional module, new)

Blinks your keyboard's **Scroll Lock light** so the machine reads as active, at one of six speeds
(Glacial through Hyper) switchable from the tray or the options pane. It works by pressing Scroll Lock,
a key no application acts on, so nothing is ever typed anywhere; Windows still counts the event as
activity, which is the point. **Caps Lock stops it**, and stopping always leaves the light off rather
than wherever the blink happened to land.

It keeps to **two tray entries**, an on/off toggle and the speed menu. The standalone app also showed a
live countdown to the next blink; that is deliberately not here, because a module ships data and the host
renders it, so the value could only be a snapshot taken when the menu opens, and a stale countdown is not
worth the tray space. **Blink once now** in the options pane covers what it was really for: telling "doing
nothing" apart from "being refused by Windows", which it reports with the actual error number.

A port of a standalone tray app, which is mostly a story about how little a module has to do: the tray
entry, the settings pane, the config file, single-instance behaviour and start-with-Windows were most of
the original's code, and the host provides all of them. The companion stays out of the way, saying nothing at
startup so it does not talk over its own opening line, and speaking only when you switch it on or off or
change the speed. Each speed has **a dozen remarks** to choose from and never repeats the last one, and
none of them are complimentary.

### 🐾 The classic companion
The upstream engine's core experience remains: sprite-sheet animations, gravity, window-edge climbing,
taskbar sitting, child companions, NAudio sound, and the drop-in `animations.xml` companion format (swap the sheep
for any community companion). Compatibility, validation, lifecycle, and multi-monitor fixes modify that
engine where required. From **Options → Companions** you can pick a different look or **download more companions**
from the in-app catalog — shown as a grid of thumbnail previews, each SHA-256-verified before it is
added. The tray dialogs also follow your **Windows light/dark theme**.

---

## Install

Each GitHub release provides two Windows x64 artifacts:

- **`DesktopAICompanion-AI-Edition.msi`** — a per-user installer (no admin).
- **`DesktopAICompanion-Portable.zip`** — unzip anywhere and run `DesktopAICompanion.exe`.

Either way you get the whole thing (sheep + fortunes + smart model + AI runtime) with **no downloads
required** to run. The builds are **unsigned** — verify them against `SHA256SUMS.txt` on the release.

---

## Using it

- **Left-click-drag** the sheep to move it; it falls and roams on its own.
- **Right-click the sheep** to poke it — first pokes give fortunes, then it starts ignoring you, then
  gets sassy, then escapes to a bathtub. Each companion keeps its **own** poke ladder, so poking one does not
  make another sassy, and only the companion you actually clicked answers.
- **Right-click the tray icon** for the menu: add a sheep, **Test Speech**, **Companion Speech**,
  **Enable/Disable AI**, **Options**, and quit.
- **Tray → Companion Speech** picks which module speaks for **each companion**: `Companion Speech ▸ Pearl ▸ Fortunes`,
  `Companion Speech ▸ Rick ▸ AI Brain`, and so on, with a tick on whichever is in effect. There is an *All companions*
  row for the shared default and a *Reset all companions* row to clear per-companion choices. With several companions on
  screen they no longer all say the same line at the same moment — a reaction belongs to one companion.
- **Options** has panes for **Preferences**, **Modules**, and then one per installed module,
  alphabetically: **AI** (provider / model / key / OCR / triggers), **Fortunes** (content level /
  sources / packs / smart toggle), **Companions**.
- **Options → Companions** gives each companion a **size** and, on a multi-monitor desktop, a **screen**. Leave the
  screen on *Any* and the companion spawns wherever; name a monitor and it stays there, and it **hides rather
  than moving** if a fullscreen app takes that screen over. The dropdown is hidden on a single monitor,
  where it would offer one option and change nothing. Note the honest limit: a companion lives on the screen it
  appears on and does not walk between monitors, which is why the preference is worded *let companions spawn on
  any screen* rather than promising traversal.
- **Updates find you.** Opening **Options → Modules** or **Options → Companions** already shows what has a newer
  version — no button press. A weekly background check writes down what it found, so the pane renders the
  answer instantly and offline, and refreshes itself on open. The app's own version check stays hourly,
  because missing a new app version for an hour is the case that actually matters. All three are notify-only
  and each can be switched off in Preferences.
- **Updating a companion you are looking at just works.** If a skin you have on screen gets an update, those companions
  are closed and respawned on the new definition. The default companion is the exception and says so: its live
  copy lives in your settings rather than the companion folder, so it asks you to restart.
- **Companions get out of the way of games.** While a fullscreen or borderless-fullscreen app is in the
  foreground every companion hides, including one that would otherwise arrive mid-animation, and the AI brain
  releases its model so a local LLM is not holding VRAM your game wants. Alt-tab out and everyone comes
  back.

The installer can **start fresh** if you want it to: an off-by-default checkbox clears every setting,
downloaded companion and installed module and installs a clean copy. It also closes a running companion for you instead
of asking you to, offers a working **Repair**, and launches the companion when it finishes.

An installed copy stores mutable data under `%LOCALAPPDATA%\DesktopAICompanion`. A portable copy stores it
under `data\` beside the executable. Supported files from the legacy `%APPDATA%\DesktopAICompanion` location
are migrated when needed.

---

## Meet the companions

The default is the classic **eSheep**, but **Options → Companions** offers a catalog of **53** drop-in companions
(each a self-contained `animations.xml`, SHA-256-verified on download). Choosing one replaces the
current companion instantly. They vary a lot: some are plain walkers, and a few are packed with rare
"easter-egg" behaviours that only surface once in a while.

Thirty-one of them are **converted Shimeji skins** — Hornet, Ralsei, Cyn, KinitoPET, Gengar,
SpongeBob, Uzi Doorman, Cartman and friends — imported with the repo's own converter rather than
hand-authored. They walk, rest, **jump**, **swing from your hand** while dragged, **climb the screen
edges**, and **cross the ceiling** once they reach the top. They also use **all four edges of your
windows**: stand on the top, grip a side and climb down the frame, jump into the underside and hang
from it, and swing round the corner between the two. A few **sit and look at your pointer**. Each
ships an honest import report of what the conversion simplified or dropped.

The **colored sheep** are the deepest, and the gallery shows them by their character names rather than
their colour (the thumbnail already shows that): **Ben** (blue), **Gus** (green), **Omar** (orange),
**Pearl** (pink), **Patsu** (purple), **Rick** (red), **Yogurt** (yellow) — all by Oliver B. They
share one 268-animation parkour set.

### Easter-egg behaviours

| Companion | Rare / special behaviours |
|-----|---------------------------|
| **Colored sheep** (Ben, Gus, Omar, Pearl, Patsu, Rick, Yogurt) | Rocket **blastoff** (ignites underneath, launches diagonally, tumbles on impact); **UFO abduction** with a tractor beam; arrival or exit by **spaceship**; a **black-sheep** romance & chase; a crown-wearing **king mode** with its own full moveset; a **flower-bloom** death; **handstand** walking; parkour (rolls, wall-slides, wall-jumps); a sneeze that flings it into a wall-jump; bathtub dives |
| **Ssj Goku** (RedSparr0w) | **Super Saiyan** transformation, **Instant Transmission** teleport, flight, ki blasts, wall smacks |
| **Pingus** (Adriano) | A *Lemmings* tribute: **digger / miner / basher / stopper**, bridge-building, belly slide, skate, a "superman" flight, reading a book, and spawning a baby penguin |
| **Negima** (Adriano) | Character / costume swaps — **Asuna** and **Akira**, three outfits each |
| **Neko · Fox · Mimiko · Pink Fox · Pink Neko · Yellow Neko** | A run-across-the-screen **chase & runaway**, plus scratching and napping |
| **Blue Ham Ham** (Michelle!) | Emotive idles: **Sparkle, Shy, Tired, Cheer** |
| **Mareep · Pikachu · Shiny Sylveon · Bbunny** | Simple directional walkers — nice sprites, no gags |

**How to catch them.** Many sheep gags are *entrances*: on spawn the companion rolls for how it arrives
(walking in, falling, diving, rolling, a handstand, on a window edge, with a black sheep, or in a
spaceship), so removing and re-adding a companion re-rolls it. Mid-life, a rare deep-idle branch summons a
UFO / spaceship / black-sheep visitor, and the rocket blastoff is a roughly 1-in-5 roll from a deep
idle state. Poking (right-click) runs its own ladder: fortunes, then ignoring you, then sass, then a
bathtub escape. Every companion's exact moves and odds live in its `animations.xml`.

---

## Building

Requires the **.NET 10 SDK** — exactly 10.0.302, pinned in [`global.json`](global.json) with
`rollForward: disable` so a different patch fails fast instead of quietly building something untested.
All twelve projects target `net10.0-windows`. MSI builds also require WiX 5.0.2.

```powershell
.\tests\run-gate.ps1                                        # the one that matters: build + CoreTests +
                                                            # every self-test + source-text invariants +
                                                            # module payload freshness. Fails on a SKIP.
.\build.ps1 -Release -Zip                                   # -> dist\DesktopAICompanion-Portable.zip
dotnet build .\tests\DesktopAICompanion.CoreTests\DesktopAICompanion.CoreTests.csproj -c Release
.\tests\DesktopAICompanion.CoreTests\bin\Release\DesktopAICompanion.CoreTests.exe
$wix = Join-Path $env:TEMP 'DesktopAICompanion-WiX-5.0.2'
.\packaging\Install-LockedWixToolchain.ps1 -PackageRoot $wix -GlobalExtension
.\installer\build-installer.ps1 -Config Release             # -> dist\DesktopAICompanion-AI-Edition.msi
```

- `build.ps1` never terminates a running app; if `DesktopAICompanion.exe` is locked, close it and retry. It
  builds only the supported x64 project (`src/DesktopAICompanion_Portable.csproj`).
- ZIP and MSI share the runtime list in [`packaging/runtime-files.txt`](packaging/runtime-files.txt).
  The ZIP also adds `DesktopAICompanion.portable`, which forces portable data-root behavior even when it is
  extracted into an install-shaped directory.
- The smart-model runtime (`onnxruntime.dll`, the bge-small model, managed deps) ships inside the
  **Fortunes module package**, not beside the exe — the installer and the ZIP are lean and carry no
  modules, so it arrives when the user installs Fortunes from the in-app catalog (~30 MB, which is
  almost entirely this model + runtime). The corpus + packs pipeline lives in
  [`src/Fortunes/`](src/Fortunes/) (`build-corpus.sh` → `strip-authors.py` → `classify-corpus.py`).

- The Shimeji conversion engine lives in [`tools/ShimejiConvert.Engine`](tools/ShimejiConvert.Engine/)
  and is **shared**: Companion Studio source-links it for in-app import (above), and the
  [`tools/ShimejiConvert`](tools/ShimejiConvert/) CLI drives it for batch/dev use (`verify`, `convert`,
  `convertroot`, `convertbundle`). It recompiles `CompanionXmlValidator.cs` rather than reimplementing the
  rules, so converted companions are graded by exactly what the app enforces. It bundles libwebp's `dwebp`
  (BSD) to decode Android-bundle WebP sprites with alpha, since the Windows WebP codec drops it.

> ⚠️ The portable csproj compiles the engine from `src/dotNet/*` but the tray dialogs (FormOptions,
> AboutBox, FormHelp) from **`src/Portable/*`** — edit the options UI there.

See [`grimoire/`](grimoire/) for a deep architecture reference and
[`FORTUNE-SOURCES-ASSESSMENT.md`](FORTUNE-SOURCES-ASSESSMENT.md) for the corpus inventory.

### Continuous integration & releases

- **[`.github/workflows/build.yml`](.github/workflows/build.yml)** builds Release x64 + the ZIP, runs
  CoreTests and the app self-tests, builds the MSI, and uploads both as run artifacts.
- **[`.github/workflows/release.yml`](.github/workflows/release.yml)** — push a `vX.Y.Z` tag and it
  builds, packages the ZIP/MSI + `SHA256SUMS.txt`, and publishes them on a GitHub release (unsigned).

---

## How it fits together

```
   FormCompanion (upstream-compatible engine)         SmartFortunes            AI brain (optional)
   physics · sprites · poke · bathtub            bge-small ONNX          ICompanionBrainBackend
        │                                        (offline, CPU)          ├─ OllamaClient (native, keep-alive VRAM)
        │ Say(text)                                   │ Pick(context)    └─ OpenAiCompatBackend (/v1: LMStudio,
        ▼                                              │                     llama.cpp, OpenRouter, OpenAI, custom)
   FormSpeech (follows the companion)  ◄── SayFortune() ◄────┴── FortuneProvider (corpus + packs + filters)
```

- **FortuneProvider** loads the embedded corpus + downloaded/custom packs and filters by tone + source.
- **SmartFortunes** embeds the active pool once (cached) and ranks by centered cosine + app→category
  routing, degrading to random when nothing fits.
- **AI brain** is gated behind a master switch (off by default) and picks its backend from the provider
  setting; only Ollama gets native keep-alive VRAM load/unload.

---

## Writing your own module

Everything above the companion engine is a module, and you can write one **without cloning this repository**.
There is no signing gate, no allowlist and no catalog requirement: build a DLL, drop the folder in
`modules\`, restart.

```powershell
# DesktopAICompanion.Contracts.nupkg + DesktopAICompanion.ModuleKit.nupkg are attached to every release;
# download them and point a package source at that folder.
dotnet new install <path>\templates\desktop-ai-companion-module
dotnet new desktop-ai-companion-module -n MyThing --moduleId mything --displayName "My Thing" --standalone true
dotnet build -c Release
# copy bin\Release\ to %LOCALAPPDATA%\Programs\Desktop AI Companion\modules\mything\ and restart
DesktopAICompanion.exe --module-selftest=mything      # runs your module through the real loader
```

What you get scaffolded is a module that already works — a tray item, a settings pane whose values
round-trip, a reaction to the companion being poked, and a self-test.

- **[`docs/module-authoring.md`](docs/module-authoring.md)** — the guide: the `IHost` surface,
  permissions, what the host guarantees, and the publishing rules.
- **`DesktopAICompanion.Contracts`** is the whole contract: implement `IModule`, talk to the app through
  `IHost`. Its `AssemblyVersion` is pinned at `1.0.0.0` forever, so a module you build today keeps
  loading. Simplest possible start: the portable ZIP ships `DesktopAICompanion.Contracts.dll` beside the exe,
  and a plain `<Reference>` to it is enough.
- **`DesktopAICompanion.ModuleKit`** is optional convenience — durable file writes, per-module paths,
  embedded-resource loading, `WavAudio` for wrapping raw samples, and a headless `RecordingHost` so you
  can unit-test a module with no app running.

Two capabilities worth knowing about if you are writing something that talks:

- **Speak for one companion, not all of them.** Register with `RegisterCompanionPokeResponder` /
  `RegisterCompanionDropResponder` and the host tells you *which* companion the reaction belongs to, so you can call
  `Say(companion, …)`. `SayAll` still exists but is for announcements to the user, not companion reactions. Check
  `IsCompanionAlive` before acting on a handle you captured before a slow `await` — there is no removal event,
  so the companion may be gone, and speaking to a dead companion is dropped rather than redirected.
- **Audio and voice.** `PlaySound(moduleId, wavOrMp3, volume)` plays through the app's shared mixer and
  device (declare `ModulePermissions.Audio`); `StopSound` cuts your own audio for barge-in.
  `RegisterSpeechResponder` (declare `ModulePermissions.Voice`) offers you every line *before* its bubble
  is drawn, so a voice module can speak it and optionally suppress the bubble. Returning `false` from
  `PlaySound` means nothing will be heard — fall back to showing the bubble.

The ABI is **stable, not frozen**: it only ever gains members, never loses or redefines them. If you
need something it cannot express, that is a gap worth filing rather than a wall.

---

## License & credits

The repository-root [`LICENSE`](LICENSE) covers the original contributions owned by `bigfnj`.
The bundled third-party works below remain the property of their respective creators and are
included here with gratitude and attribution.

### Sources & thanks

**Engine & pet** — forked from [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet),
the eSheep desktop-pet lineage.

**Embedder model** — [`bge-small-en-v1.5`](https://huggingface.co/BAAI/bge-small-en-v1.5) by BAAI.

**Fortune corpus** — aggregated largely from
[JKirchartz/fortunes](https://github.com/JKirchartz/fortunes) and the classic BSD `fortune` files,
with grateful thanks to the sources behind them, including: the Quotable quote collection; clean
jokes; collected authors, artists, and activists; Seth Godin; Larry Wall and the hacker koans;
*Epigrams on Programming* (Alan Perlis); RFC 1925; *Oblique Strategies* (Brian Eno & Peter Schmidt);
William Blake; Jenny Holzer; Ogden Nash; Robert Louis Stevenson; *The Dictionary of Obscure Sorrows*
(John Koenig); *The Simpsons* chalkboard gags; and r/Showerthoughts.

**Fortune packs** (optional downloads) — with thanks to r/DadJokes and r/Showerthoughts; the Bastard
Operator From Hell (Simon Travaglia); programming epigrams, RFC 1925, and Larry Wall; the *Tao Te
Ching* and classic philosophy; *Oblique Strategies* and assorted authors and poets; Groucho Marx,
Jack Handey, and Red Green; fortune-cookie and Chuck Norris trivia; pop-culture television (*The
Simpsons*, *Futurama*, *MST3K*, *Star Trek*, *Firefly*, *South Park*, *The Sopranos*, *It's Always
Sunny in Philadelphia*, and more); George Carlin, the Church of the SubGenius, and Robert Anton
Wilson; and the classic `fortune -o` collection. Heartfelt thanks to every author and community above.

See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for the dependency inventory,
[`SUPPORT.md`](SUPPORT.md) for support, and [`SECURITY.md`](SECURITY.md) for private
security-reporting guidance.
