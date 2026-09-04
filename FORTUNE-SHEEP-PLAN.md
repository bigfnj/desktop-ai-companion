# Fortune Sheep — build plan

> **Historical planning snapshot - non-authoritative.** This document records an early design
> discussion and must not be used as current build, privacy, rights, or release guidance. The
> maintained product behavior is described in [Readme.md](Readme.md), source-rights status in
> [FORTUNE-SOURCES-ASSESSMENT.md](FORTUNE-SOURCES-ASSESSMENT.md), and release requirements in
> [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md). Where this snapshot conflicts with those
> documents or the code, the maintained sources control.
>
> In particular, the embedding model and vocabulary are bundled now, the AI brain is disabled by
> default, and redistribution rights for the bundled fortune corpus remain unresolved release
> blockers. The contrary statements below are superseded historical proposals.

> The v2 direction for this fork. An adorable desktop sheep that speaks **fortunes**
> (think `cowsay | fortune`): smart by default (fortunes that fit your screen, offline),
> charming when poked, and optionally an AI companion that comments on your screen when
> you give it a brain. Builds on the shipped Phases 1–7.1 (speech bubble, `TryPlayAnimation`,
> `AskAboutScreen`, the `ICompanionBrainBackend` seam, the AI options tab, the WiX MSI).

## Locked decisions (2026-07-27)

- **Embedding model delivery (superseded proposal):** this plan proposed an optional first-run
  download. The maintained product bundles the model and vocabulary.
- **Default preset (superseded proposal):** this plan proposed automatic screen insight when a
  local LLM was detected. The maintained product keeps the AI brain disabled by default.
- **Cloud providers:** *both* OpenRouter (one key → many models) and OpenAI, selectable.
- **Bathtub escape:** *full* variant — force a respawn through the companion's own `spawn id=3`
  (fly in from the screen edge, land in the tub).
- **First implementation pass:** Phases **A → B → C**, then regroup.
- **Name/branding:** TBD (keep "Desktop AI Companion" or rename to "Fortune Sheep" at release).

## Architecture — three tiers, one interface

- **Tier 0 — bundled embeddings (default, offline, no LLM):** contextual fortunes + (later)
  semantic idle‑gating and memory. In‑process ONNX (Microsoft.ML.OnnxRuntime, .NET 4.8), no server.
- **Tier 1 — local chat (detected/opt‑in):** Ollama (`:11434`) / LM Studio (`:1234`).
- **Tier 2 — cloud (key):** OpenRouter / OpenAI.
- **One Interface:** a single `OpenAiCompatBackend` (`/v1/chat/completions`, `{baseUrl, apiKey?, model}`)
  behind the existing `ICompanionBrainBackend` seam drives all of Tier 1–2. Replaces the Ollama‑native client.

## Interaction model

- **On land →** speak a fortune (contextual if the model is present, else random).
- **Right‑click poke escalation** (timing‑based):
  | Poke | Behavior |
  |------|----------|
  | 1 (rested) | main response — **insight** if LLM+peek available, else a fortune |
  | 2 (quick) | another fortune |
  | 3–4 | **ignore** — turn‑away animation, no bubble (`TryPlayAnimation`) |
  | 5–11 | **verbal sass** — canned annoyance lines |
  | 12 (spam) | **full bathtub escape** — respawn via `spawn id=3` (fly in, land in tub) |
- **Idle ambient (Companion default: on, gentle):** occasional contextual fortune on a jittered
  timer, semantically gated; an insight only if LLM + peek + meaningful screen change.
- **SFW / Spicy** corpus toggle.

## Phases

### Phase A — Fortune Sheep core (no new dependencies)
- Vendor a **curated SFW corpus** (+ optional **Spicy**) from JKirchartz/fortunes. The plan's
  historical repository-level license assumption was never source-by-source redistribution
  clearance; current rights status remains unresolved. Bundle it only after the release checklist's
  evidence and approval requirements are satisfied. Use a `%`‑delimited parser + random pick →
  speech bubble.
- Fortune on land.
- **Poke‑escalation state machine** (the table above); poke‑1 = fortune for now (insight wired in C).
- **Full bathtub escape:** new small engine entry point to force a respawn via a given `spawn id`
  (drives `spawn id=3` → `batha→bathb→bathc→bathd`, existing frames 134–145/174).
- SFW/Spicy toggle in the options tab.
- *Deliverable:* charming, self‑contained companion, zero setup. Random = the Tier‑0 fallback.

### Phase B — Contextual fortunes (the embedder → the smart default)

**Engine:** add **Microsoft.ML.OnnxRuntime** + a BERT tokenizer (FastBertTokenizer), .NET 4.8‑compatible,
in‑process (no server). Ship **bge‑small‑en‑v1.5** (ONNX). ⚠️ **Validate ONNX‑in‑single‑exe FIRST** (native
runtime DLLs vs the embedded‑assembly trick) — biggest risk; smoke‑test before building matching on top.
This plan proposed **first-run onboarding** for a model download. That delivery design is
superseded: the maintained product bundles the model and vocabulary.

**Why naive cosine isn't enough (design rationale):** the query (window title + OCR) is short/noisy and the
fortunes are abstract aphorisms, so a single global cosine suffers *hubness / compressed spread* — everything
scores moderately similar, the top‑k margin is tiny, and top‑1 can be a spurious, jarring off‑topic pick.
The illusion breaks on the misses, not the hits. So we wrap the embedder in **coarse‑to‑fine routing + a few
precomputed ("pre‑weighted") tricks:**

1. **Category routing (labels are near‑free).** The corpus source files are already topical
   (`epigrams_in_programming`/`hackers` → **tech**; `classic_philosophy`/`tao` → **wisdom**;
   `MrRogers`/`handey` → **whimsy**; `authors`/`artists` → **creative**; `realfacts` → **facts**; etc.).
   `build-corpus.sh` emits a per‑fortune **category + content‑rating** tag (format:
   `category<TAB>rating<TAB>text`). Categories: tech / wisdom / creative / whimsy / facts / work /
   observations / general. **Rating = `sfw` | `spicy`** (profanity/NSFW hit, or an inherently‑adult
   source) — since most users flip Spicy on, this lets routing **gate spicy lines by context** (e.g.,
   never drop a crude one onto a work / Teams / call screen) and power a spiciness dial beyond on/off.
2. **App→category rules for the query (fixes "minimal range").** Don't trust the noisy OCR embedding
   alone — map the **active process/window** to a category with a tiny rules table (VS Code → coding/tech,
   a YouTube tab → video, Word → writing, a terminal → tech…). App identity is a high‑precision signal a
   short title can't give. Classify the screen → a category (rules first, embedding fallback).
3. **Rank within the matched category** by cosine, then **top‑k → randomize** (never trust top‑1; keeps it
   apt *and* fresh, no repeats).

**Pre‑weighting the vectors (all build‑time; runtime stays a cheap dot‑product):**
- **Mean‑center / whiten** the fortune matrix (subtract the global mean, renormalize) → widens the cosine
  spread so top‑k is actually discriminative (attacks hubness directly).
- **Per‑fortune specificity score** (distance from global centroid / max category affinity) → weight
  **adaptively by query confidence**: strong screen signal favors specific/topical fortunes; weak/ambiguous
  screen favors *universal* ones (safe anywhere) → graceful degradation instead of noise.
- **Category‑affinity boost** — the numeric form of the routing above.
- (No single scalar per‑fortune "importance" — relevance is relative to the query, so weighting must be
  conditioned on category/confidence, not a global constant.)

**Precomputed artifacts** (built alongside the corpus and embedded in the maintained product): the tagged corpus,
the **centered fortune vectors**, per‑fortune category + specificity, and category centroids.

- **`EmbeddingService`** (text → vector) + a **`FortuneMatcher`** (screen → category → within‑category
  top‑k‑random over centered vectors, confidence‑adaptive specificity). Falls back to plain random when the
  model isn't present.
- A **"contextual strength" knob** (pure‑random ↔ tightly‑contextual) so the magic‑vs‑variety balance is tunable.
- *Honest expectation:* resonant often, pleasantly neutral sometimes — not a mind‑reader. The routing's job
  is to **kill jarring misses**; specificity handles weak queries; top‑k‑random keeps it fresh.
- *Deliverable:* contextual fortunes are the default; random is the graceful fallback.

### Phase C — AI screen‑insight tier + One Interface
- Replace `OllamaClient` (native `/api/chat`) with **`OpenAiCompatBackend`** (`/v1/chat/completions`)
  behind `ICompanionBrainBackend`. Vision routing (Phase 6) carries over (OpenAI‑style `image_url`).
- **Provider config + detection:** none / Ollama / LM Studio / cloud (OpenRouter, OpenAI); API key
  **DPAPI‑encrypted** in the settings file. Auto‑detect local servers.
- Wire **insight into poke‑1** when an LLM is configured AND the "let the sheep peek" toggle is on
  (Companion = on by default); otherwise poke‑1 = fortune.
- *Deliverable:* opt‑in, provider‑agnostic AI remarks.

### Phase D — Presets, idle, polish (after the A–C regroup)
- Presets: **Fortune Teller** (never peeks) / **Companion** (default) / **Quiet** (on demand).
- Idle ambient via the embedder's semantic gate (replaces the luma gate).
- Options‑tab pass: corpus, preset, idle frequency, provider, peek toggle, model‑download button.

### Phase E — Release
- Installer updated for the bundled model; version bump; **GitHub Release** with the MSI;
  README / grimoire updates.

## Reuse vs. new
- **Reused:** speech bubble, `TryPlayAnimation` (2.8), `AskAboutScreen`, vision routing (6),
  the AI options tab, `ICompanionBrainBackend`, the WiX MSI, `ChatHistory`.
- **New:** fortune engine + corpus, poke‑escalation state machine, forced bath‑respawn entry point,
  ONNX embedder + tokenizer + pre‑computed vectors, `OpenAiCompatBackend`, provider config/detection,
  DPAPI key storage. The first-run model-download proposal is superseded by bundled delivery.

## Bonus (backlog)
- Embedder also powers **semantic memory** (upgrade Phase 5 "last‑10" → "most‑relevant") and
  **no‑repeat** reply filtering.

## Risks / notes
- Contextual‑fortune quality is good‑but‑imperfect (aphorisms are abstract) — top‑k‑randomize hides misses.
- Corpus curation matters (some upstream files are edgy) — SFW is the default set; Spicy is opt‑in.
- ONNX + tokenizer on .NET Framework 4.8: verified compatible, but adds real dependencies to the
  single‑exe embed trick — validate the packaged exe early in Phase B.
