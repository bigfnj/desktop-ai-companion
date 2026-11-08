# Fortune classification taxonomy (v2, locked 2026-07-31)

**Four independent axes** per fortune. Two are new (this classification pass), two already exist.

| axis | values | assigned by | purpose |
|------|--------|-------------|---------|
| **topic** (new) | 12, below | this pass | *subject* — the light routing nudge (screen→topic prototype) |
| **genre** (new) | 12, below | this pass | *format/delivery* — the corpus's real variety; powers flavor/filter |
| `level` (exists) | general / edgy / nsfw | `classify-corpus.py` | content **severity** → spicy-tier filter |
| `prof` (exists) | 0 / 1 | `classify-corpus.py` | conservative flag for recognized profanity or explicit sexual content → filter control |

Grounded in a scan of all 61k lines: the corpus varies far more by **genre** than **topic**
(TV quotes 22%, showerthoughts 16%, wisdom 17%, jokes 9% — and every topic keyword signal is ≤4%),
so genre is the rich axis and `life` is a legitimately large topic catch-all. Severity (`level`/`prof`)
stays **orthogonal**: Carlin can be `genre=dark, level=general`; a wholesome f-bomb is `uplifting, edgy`.

Schema v2 is exactly six tab-separated fields:

`source ⇥ topic ⇥ genre ⇥ level ⇥ prof ⇥ text`

- Every data file is uniform: all rows are v2 or all rows are legacy v1. Mixed files are invalid.
- Topic, genre, level, and profanity tokens are exact, lowercase values from this document.
- Text is a single normalized line. Tabs inside fields are invalid.
- The schema version is represented by its exact field layout and locked taxonomy version
  `2026-07-31`; generated metadata records both the version and SHA-256 hashes.
- Build-time and runtime severity classification use the same Unicode fold: NFKD compatibility
  decomposition, removal of combining marks, dotless-I mapping, ASCII lowercasing, and explicit
  ASCII word boundaries. Both engines validate the shared
  `classifier-parity-cases.tsv` fixture, including dotted/dotless I, long-s, Kelvin-sign,
  compatibility-width/space, combining-mark, and boundary controls.

## Explicit legacy-v1 compatibility

Runtime temporarily accepts exact five-column v1 packs:

`source ⇥ category ⇥ level ⇥ prof ⇥ text`

Compatibility is allowlisted and deterministic; an unknown category is rejected rather than guessed.
The bundled corpus and packs are now v2; this bridge remains only for external five-column packs:

| v1 category | v2 topic | v2 genre |
|-------------|----------|----------|
| `tech` | `tech` | `quip` |
| `facts` | `science` | `fact` |
| `work` | `work-money` | `aphorism` |
| `creative` | `arts` | `aphorism` |
| `wisdom` | `life` | `wisdom` |
| `observations` | `life` | `observation` |
| `tv` | `life` | `tv-quote` |
| `nsfw`, `spicy` | `life` | `dark` |
| `whimsy`, `general`, `custom` | `life` | `quip` |

This mapping is compatibility metadata, not a substitute for the completed classification pass.
New or migrated packs must use v2.

---

## TOPIC — subject (12).  Exemplars double as the routing prototypes.

| topic | subject | routing prototypes |
|-------|---------|--------------------|
| **tech** | computing, programming, the internet, gadgets | "Debugging is twice as hard as writing the code." · "The cloud is just someone else's computer." |
| **science** | physics, space, biology, math, how reality works | "Every atom in you was forged in a dying star." · "Light from that star left before you were born." |
| **work-money** | jobs, business, productivity, finance, wealth | "A meeting is where hours go to die." · "A budget is telling your money where to go." |
| **love** | dating, marriage, breakups, intimacy | "We broke up; it just wasn't working out." · "Love is one soul inhabiting two bodies." |
| **family** | parents, kids, home, growing up | "Kids remember the time, not the toys." · "My father's gift was believing in me." |
| **faith** | religion, spirituality, the sacred, meaning | "The soul never thinks without a picture." · "Faith is taking the first step without seeing the staircase." |
| **society** | politics, culture, law, history, current events | "The first duty of a citizen is to question authority." · "History doesn't repeat, but it rhymes." |
| **food** | cooking, eating, drink | "There is no love more sincere than the love of food." · "You bring the soul to the recipe." |
| **nature** | animals, weather, the outdoors, the environment | "A dog loves you more than it loves itself." · "The mountains are calling and I must go." |
| **arts** | literature, writing, film, visual art, music, creativity | "Write drunk, edit sober." · "Without music, life would be a mistake." |
| **health-body** | health, medicine, illness, the body, fitness, aging, wellness, mental health | "An apple a day keeps the doctor away." · "The greatest wealth is health." |
| **life** | everyday life, small observations, the human comedy (catch-all) | "The best part of the day is the first sip of coffee." · "Websites should list their password rules on the login page." |

## GENRE — format / delivery (12)

| genre | delivery | example |
|-------|----------|---------|
| **tv-quote** | character / screen dialogue | "George: A woman that hates me this much comes along once in a lifetime." |
| **observation** | shower-thought noticing of the mundane | "Dogs lick us because we have bones inside." |
| **joke** | setup → punchline gag (incl. dad jokes) | "Why don't scientists trust atoms? They make up everything." |
| **pun** | wordplay / groaner | "I used to be a banker but I lost interest." |
| **quip** | witty one-liner / wry aside | "I'm not arguing, I'm just explaining why I'm right." |
| **aphorism** | proverb / maxim / saying | "Time and tide wait for no man." |
| **wisdom** | earnest philosophical insight | "The unexamined life is not worth living." |
| **fact** | trivia / factoid, no gag or moral | "A honey bee can fly at 15 mph." |
| **insult** | roast / yo-mama | "Yo momma's so stubborn she argues with the weather forecast." |
| **verse** | limerick / poem / deliberate rhyme | "There once was a man from Nantucket…" |
| **dark** | gallows / cynical / morbid humor | "I want to die in my sleep like grandpa — not screaming, like his passengers." |
| **uplifting** | wholesome or motivational | "You are braver than you believe and stronger than you seem." |

---

## Labeling rules

- Exactly **one topic + one genre** per fortune. Pick the *dominant* read.
- **`life`** is the topic catch-all (expect it to be large); **there is no genre catch-all** — every line
  has a delivery style, so choose the closest of the 12.
- **topic ≠ source.** A `quotable` line about code is `tech`; a Simpsons line about a divorce is
  `love`+`tv-quote`.
- **genre vs `level` are different questions:** *how it reads* (dark/insult) vs *how blue it is*
  (edgy/nsfw). Don't infer one from the other.
- Prototypes are editable — retuning a topic's prototype sentences retunes routing with **no relabeling**.

## Pipeline invariants

- `label-build-input.sh` deterministically freezes the exact unique text set across the embedded
  corpus and every pack. It records row count, taxonomy version, and SHA-256.
- `labels-store.tsv` is exactly `text<TAB>topic<TAB>genre`; texts and labels must be unique and
  drawn from the frozen input and locked taxonomy.
- `label-next.sh` and `label-ingest.sh` never relabel an existing key. Ingest stages and validates
  the complete new store before an atomic replacement. Space- and tab-separated label pairs are
  normalized to the same two-field representation.
- `label-merge.sh` requires every numbered chunk exactly once, with matching counts and order.
  Missing, extra, duplicate, reordered, or invalid rows fail without changing the prior store.
- `label-apply.sh --go` requires exact input/store/corpus key-set equality. It has no fallback label:
  one unmatched text aborts the run. It also refuses promotion until a freshly generated
  `--emit-plan` is supplied with `--metadata-plan` and the caller explicitly passes
  `--acknowledge-metadata-finalization`; the plan binds current corpus, pack, labeling, source-asset,
  rights-manifest, and notice hashes.
- Apply stages and validates every output before promotion, preserves source/level/prof/text at the
  field level, and uses same-directory atomic replacement for each file. It never rewrites the
  invalidated catalog/provenance/notices automatically and reports those required finalization
  steps after promotion.
- Build-input, next-batch, ingest, merge, and apply share one cross-process lock. `HUP`, `INT`, or
  `TERM` during a labeling-file promotion restores every prior file before releasing that lock.
- `build-corpus.sh` is a pure assembler of labeled schema-v2 rows that already live in the
  repository. Each source id in `corpus-required-files.txt` resolves to `sources/<id>.tsv` beside
  it, else to `packs/<id>.txt` at the repository root; an id with neither fails instead of
  silently shrinking the corpus. Every input row must be valid UTF-8 with six fields, a source column
  equal to its id, and text the module would load: HTML-decoded and trimmed as the module does it, then
  8 to 280 user-perceived characters with no control character (a perl check that counts as the
  module counts; one row the module refuses would empty the whole embedded corpus). Rows are
  deduplicated on text (manifest order keeps the first), sorted under the C locale by source then text, staged beside `fortunes.txt` and renamed
  onto it. It reads no upstream checkout and writes no sidecar.
- **Current-corpus provenance status:** the checked-in `fortunes.txt` is reproduced byte for byte
  from its two repository inputs, `sources/fortunes.tsv` and `packs/dadjokes.txt`.
  `build-corpus.sh --check` asserts that, and `tests/Invoke-OfflineScriptChecks.ps1` runs it for
  the gate and CI. What cannot be regenerated is the topic and genre labeling itself: the labeling
  pass's input store (`label-input.tsv`, `labels-store.tsv`, `label-chunks/`) is gitignored and no
  longer exists, so the committed rows are the labels. The automated rights gates were retired
  with the enterprise release pipeline; rights are recorded rather than gated.
- **Retired, described here only as history:** `packaging/Test-EmbeddedCorpus.ps1` required exact
  full-row uniqueness, and `packaging/source-rights-evidence.json` bound six scopes to exact file or
  deterministic
  aggregate hashes: the embedded corpus, model, vocabulary, supported engine-source closure,
  bundled executable art/resources, and downloadable companion animation/icon/catalog payloads. A
  virtual-set approval must assign every fingerprinted path exactly once across its approvals'
  `memberPaths` arrays. Engine fingerprints use the canonical LF-only release bytes; strict release
  validation rejects CRLF even though development validation can canonicalize it for a pre-commit
  structural check. The current empty approvals document unresolved state; they do not grant
  redistribution rights, and release-mode validation fails until complete retained evidence is
  approved for all six scopes.
- `label-selftest.sh` runs disposable lock-contention, input-determinism (bytewise duplicate
  selection under C and a collating locale), comm-locale, exact-taxonomy-member, transform
  (`classify-corpus.py`, `strip-authors.py`), corpus-builder, signal-rollback, missing-chunk,
  extra-output, invalid-label, tab-ingest, incomplete-store, metadata-plan, invariance and
  which-label probes. It never touches live labeling progress. No gate or CI step runs it; run it
  by hand in Git Bash.

## Taxonomy revisions
- **2026-07-31** — added topic **`health-body`** (12th topic), carved from the `life` catch-all plus
  incidental health content elsewhere, via a targeted, precision-gated re-label. Health is a distinct
  subject users may want to filter (doctors, illness, fitness, aging, mental health) rather than a tone.

## Optional future merges (not applied)
`work-money`→split · `arts` (could split music) · `pun`→`joke`. Left as-is; revisit if a bucket is thin.
