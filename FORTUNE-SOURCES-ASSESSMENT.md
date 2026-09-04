# Fortune Sources — Harvest & Assessment

Working doc for the Fortune Sheep corpus. Every source below was downloaded, normalized to
bubble-sized entries (8–280 chars, whitespace-collapsed), then run through the **real shipping
pipeline** (`strip-authors.py` + `classify-corpus.py`). Columns:

- **n** — deduped entries that survived the full pass
- **edgy / nsfw** — how many our classifier flagged (the rest are `general`)
- **rec** — historical planning labels: `INCLUDE` (candidate for the embedded core), `PACKAGE`
  (candidate for an opt-in download), or `IGNORE`

Raw combined harvest: `scratchpad/harvest/fortunes-harvest.txt` (+ `bofh.txt`).
Total harvested (pre-curation): **~41,800 entries** across ~120 source files.

Harvest caveats: Mitch Hedberg gist returned the scrape *script* not jokes (junk); type.fit's
endpoint is dead (5 rows); fortune-tv ships compiled `.dat` binaries that were ingested as noise
(excluded here); the public-domain books over-count because segmentation swept in translator
prefaces (flagged per row).

The **Tier A/B/C** sections below preserve the original assessment of candidate additions. The
section immediately below records the embedded corpus that actually ships now.

> **Rights status:** this harvest assessment is research and planning evidence, not legal clearance.
> License labels below repeat source metadata or historical assumptions unless an exact content
> revision and redistribution grant have been independently retained and reviewed. The unresolved
> corpus provenance work in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) and
> [`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md) remains a public-release blocker. No source
> is approved for redistribution merely because it appears in Tier A or in the current corpus table.

---

## What we actually ship — 26 sources, 10,311 entries

`n` is the exact embedded row count. `edgy` and `nsfw` are the stored content-level tags. The
runtime parser may raise a row's effective severity when its text is more restrictive than its
stored tag; it never lowers the supplied severity.

| Source | n | edgy | nsfw |
|---|--:|--:|--:|
| quotable | 2,109 | 1 | 0 |
| cleanjokes | 1,588 | 5 | 2 |
| authors | 1,000 | 1 | 1 |
| realfacts | 861 | 0 | 0 |
| showerthoughts | 800 | 38 | 17 |
| artists | 700 | 2 | 1 |
| fortunes | 431 | 0 | 0 |
| godin | 401 | 1 | 0 |
| hackers | 377 | 2 | 0 |
| lwall-quotes | 370 | 1 | 0 |
| SimpsonsChalkboard | 365 | 1 | 1 |
| activists | 357 | 3 | 0 |
| rhetorical-devices | 159 | 0 | 0 |
| EnglishAsSheIsSpoke | 152 | 0 | 0 |
| ObliqueStrategies | 136 | 0 | 0 |
| epigrams_in_programming | 120 | 0 | 0 |
| wblake | 78 | 0 | 0 |
| Jenny_Holzer | 60 | 0 | 0 |
| ComputerDictionary | 57 | 0 | 0 |
| ogden_nash | 49 | 0 | 0 |
| BibleAbridged | 48 | 2 | 0 |
| enkiv2s-glossary-of-tech-industry-terms | 45 | 0 | 1 |
| stevenson | 15 | 1 | 0 |
| hacker-questions | 13 | 0 | 0 |
| rfc1925 | 11 | 0 | 0 |
| ObscureSorrows | 9 | 2 | 0 |
| **Total** | **10,311** | **60** | **23** |

**Current takeaways:** the embedded mix is substantially more balanced than the old harvest
snapshot. `quotable` is the largest source at 20.5%, followed by `cleanjokes` at 15.4% and
`authors` at 9.7%; `showerthoughts` is now 7.8% rather than 38%.

---

## Tier A — historical candidates for inclusion (rights evidence still required)

### Quotes & jokes

| Source | n | edgy | nsfw | License | Notes |
|---|--:|--:|--:|---|---|
| **quotable** | 2,114 | 1 | 0 | MIT asserted by source | Curated famous quotes; verify the exact snapshot, license text, and rights in the quoted material before redistribution. |
| **cleanjokes** | 1,611 | 5 | 2 | Reddit UGC; grant not established | Clean dad-joke / pun candidate; requires source-by-source provenance and redistribution review. |

### fortune-mod — canonical clean datfiles (historical fair-use assumption; not clearance)

| Source | n | edgy | nsfw | Notes |
|---|--:|--:|--:|---|
| people | 1,181 | 0 | 0 | Quips about people/life |
| definitions | 1,098 | 6 | 0 | Devil's-dictionary style |
| cookie | 893 | 13 | 1 | Classic fortune-cookie file |
| computers | 819 | 4 | 0 | Hacker/computing humor |
| miscellaneous | 629 | 1 | 0 | |
| politics | 625 | 0 | 0 | |
| work | 546 | 1 | 0 | |
| zippy | 541 | 0 | 3 | Zippy the Pinhead (Griffith, mild ©) |
| men-women | 512 | 0 | 0 | |
| science | 508 | 2 | 0 | |
| platitudes | 488 | 1 | 0 | |
| songs-poems | 436 | 2 | 0 | |
| fortunes | 431 | 0 | 0 | The core `fortunes` file |
| art | 400 | 0 | 2 | |
| wisdom | 385 | 1 | 2 | |
| linux | 359 | 5 | 1 | |
| perl | 270 | 1 | 0 | |
| literature | 212 | 0 | 0 | |
| humorists | 195 | 0 | 0 | |
| education | 181 | 0 | 0 | |
| food | 164 | 0 | 0 | |
| law | 145 | 0 | 1 | |
| love | 138 | 0 | 0 | |
| kids | 124 | 0 | 0 | |
| riddles | 116 | 0 | 0 | |
| shlomif-fav | 113 | 1 | 1 | Maintainer favorites |
| sports | 101 | 0 | 0 | |
| debian | 75 | 1 | 1 | IRC-quote style |
| paradoxum | 68 | 0 | 0 | |
| medicine | 51 | 0 | 1 | |
| companions | 47 | 0 | 0 | |
| news | 44 | 0 | 0 | |
| goedel | 43 | 0 | 0 | |
| magic | 19 | 0 | 0 | |
| tao | 6 | 0 | 0 | (we already ship a Tao source) |

**fortune-mod clean subtotal ≈ 11,965** (excludes pratchett/startrek → Tier B, and ethnic/drugs/x-files → Tier C).

### Public-domain wisdom (INCLUDE, but needs a verse-segmentation pass)

| Source | n raw | usable est. | License | Notes |
|---|--:|--:|---|---|
| meditations (Marcus Aurelius) | 2,166 | ~800–1,000 | **Public domain** | Count inflated by translator's bio/preface; trim to Books I–XII |
| artofwar (Sun Tzu, Giles) | 1,796 | ~400 | **Public domain** | Front-matter + commentary mixed in |
| analects (Confucius, Legge) | 1,519 | ~500 | **Public domain** | Front-matter + verse numbers swept in |
| dhammapada | 422 | ~300 | **Public domain** | Cleanest of the four |

**Tier A total ≈ 18,000–19,000 candidate entries** after the proposed public-domain-text
segmentation. Inclusion remains conditional on exact-source provenance, edition/translation status,
license retention, and redistribution review.

---

## Tier B — PACKAGE (opt-in downloads, not in default)

### B1 · BOFH (tech) — NEW

| Source | n | edgy | nsfw | License | Notes |
|---|--:|--:|--:|---|---|
| **bofh** | 489 | 2 | 2 | © Travaglia / near-folklore | Canonical BOFH Excuse Server list, formatted `BOFH excuse #N: …`. Merged from `shriramters/maubot-bofh` + `fundor333/bofh`. Slots next to the tech sources. |

### B2 · Copyrighted authors (beloved, but ©)

| Source | n | License | Notes |
|---|--:|---|---|
| pratchett | 558 | © Terry Pratchett | Discworld quotes; superb, but copyrighted |
| startrek | 220 | © Paramount | |

### B3 · Adult / NSFW (`fortune -o`, minus the hateful files)

| Source | n | edgy | nsfw | Notes |
|---|--:|--:|--:|---|
| off·atheism | 1,358 | 24 | 14 | More irreverent than obscene |
| off·limerick | 965 | 220 | 157 | Dirty limericks; raunchiest single file |
| off·sex | 592 | 41 | 43 | |
| off·definitions | 304 | 30 | 24 | Crude definitions |
| off·politics | 252 | 12 | 3 | |
| off·riddles | 248 | 11 | 13 | |
| off·black-humor | 172 | 9 | 4 | |
| off·religion | 155 | 4 | 0 | |
| off·vulgarity | 140 | 91 | 3 | |
| off·songs-poems | 114 | 12 | 2 | |
| off·privates | 76 | 12 | 7 | |
| off·drugs | 57 | 1 | 0 | |
| off·miscellaneous | 52 | 0 | 2 | |
| off·astrology | 32 | 0 | 0 | |
| off·debian | 19 | 7 | 3 | |
| off·knghtbrd | 13 | 13 | 0 | |
| off·linux | 7 | 4 | 0 | |

**NSFW pack subtotal ≈ 4,560.**

### B4 · Fandom — fortune-tv (© copyright, personal-use; dialogue-format, avg ~140 chars)

Cleaner shows first; **profane** = classifier edgy+nsfw hits.

| Show | n | profane | | Show | n | profane |
|---|--:|--:|---|---|--:|--:|
| simpsons | 1,616 | 43 | | southpark | 864 | 207 |
| mst3k | 1,576 | 22 | | trailerparkboys (tpb) | 326 | 155 |
| futurama | 793 | 39 | | sopranos | 234 | 133 |
| x-files | 343 | 12 | | peepshow | 544 | 125 |
| office-us | 249 | 13 | | thewire | 179 | 91 |
| 30rock | 230 | 15 | | 3rdrock | 167 | 102 |
| qi | 229 | 18 | | beavisbutthead | 751 | 96 |
| madmen | 203 | 11 | | drawntogether | 381 | 93 |
| firefly | 198 | 9 | | boondocks | 150 | 70 |
| scrubs | 198 | 18 | | archer | 342 | 64 |
| seinfeld | 186 | 4 | | alwayssunny | 220 | 51 |
| batman (+TAS) | 260 | 17 | | mrshow | 208 | 50 |
| parksrec | 165 | 16 | | venturebros | 382 | 45 |
| arrested | 156 | 7 | | metalocalypse | 164 | 31 |
| moralorel | 155 | 2 | | curb | 119 | 35 |
| malcolm | 393 | 14 | | friskydingo | 152 | 35 |
| koth (King of the Hill) | 384 | 21 | | sealab2021 | 255 | 30 |
| homemovies | 140 | 5 | | genkill | 43 | 23 |
| twilightzone | 54 | 0 | | squidbillies | 88 | 10 |
| montypython | 49 | 3 | | youngones | 62 | 7 |
| a-team | 27 | 0 | | robotchicken | 38 | 7 |
| lookaroundyou | 46 | 1 | | harveybirdman | 115 | 5 |
| bobsburgers | 45 | 1 | | snl | 102 | 11 |
| dilbert | 44 | 1 | | newsradio | 81 | 2 |
| rockos | 94 | 1 | | lucydevil | 41 | 9 |
| workaholics | 6 | 1 | | rickmorty | 3 | 1 |

**Fandom subtotal ≈ 13,850** across 51 shows. Suggest grouping into a **clean** pack (Simpsons, MST3K, Futurama, Star Trek/X-Files, Firefly, Seinfeld, Parks & Rec, Twilight Zone, Monty Python, A-Team…) and a **mature** pack (South Park, Sopranos, The Wire, Trailer Park Boys, Always Sunny, Archer…).

---

## Tier C — historically excluded or requiring additional rights review

| Source | n | Why ignore |
|---|--:|---|
| off·racism | 4 | Hate content — drop (FreeBSD removed these in 2017) |
| off·misogyny | 49 | e.g. "A little bit of rape is good for a man's soul" — vile. Drop |
| off·hphobia | 13 | Homophobic. Drop |
| off·misandry / off·ethnic | 86 | Same category. Drop |
| fm·ethnic | 127 | In the "clean" set but dated ethnic jokes — review, don't auto-ship |
| fm·drugs | 159 | Drug recipes/jokes — review |
| fm·the-x-files-taglines | 24 | Mojibake (broken Navajo encoding) |
| hedberg | 8 | Harvest captured a scrape script rather than the intended jokes; no reviewed redistribution grant was retained |
| typefit | 5 | Endpoint effectively dead; overlaps Quotable |
| Quotes-500K / joke megadumps / dadjokes / copypasta | — | Not harvested: exact sources and redistribution grants were not established; substantial provenance and content review required |

---

## Historical recommendation from the harvest assessment

**Proposed default embedded core at the time (Tier A only):** Quotable + fortune-mod clean canonical
files + cleanjokes + the four public-domain wisdom texts (after segmentation), approximately
**18–19k** entries. This was a quality and curation proposal, not a rights determination. Each exact
content revision still requires the provenance and redistribution evidence described above before
it can ship.

**Opt-in GitHub packs (Tier B):**
- **BOFH** (489, tech) ← your request
- **Fandom / clean** and **Fandom / mature** (fortune-tv)
- **NSFW** (`fortune -o` minus hate)
- **Pratchett & Trek**

**Drop entirely (Tier C):** the hate files, dated ethnic/drugs, and the broken/dubious harvests.

### Open questions before cutting packages
1. OK to drop Tier C (hate/ethnic/drugs) outright?
2. Public-domain books: invest in a proper verse-segmentation pass, or ship v1 core as
   Quotable + fortune-mod + cleanjokes + BOFH and add PD wisdom later?
3. BOFH: keep as an opt-in `tech` pack, or promote into the default core?
