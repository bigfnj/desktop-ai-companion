# Fortune Packs

Optional add-on fortune collections for **Desktop AI Companion**. Each pack is a plain UTF-8 text
file of tagged fortunes (tab-separated: `source`, `topic`, `genre`, `level`, `prof`, `text`). Packs
are downloaded from the in-app **Fortunes** tab into the application's data-root `fortunes`
directory, where they load as new sources.

## How packs are organized and delivered

- **One file per source.** Packs are split per source id (`packs/<source>.txt`) — 158 of them — so a
  user can pick individual shows/authors instead of whole monolithic bundles.
- **Collections group them.** `packs/collections.json` (embedded in the app) maps each source to a
  named collection. There are seven: Jokes & Humour, Fortunes & Wisdom, Books, Art & Language,
  Screen & Stage, Tech & Hackers, Facts & Observations, and NSFW (adults only). It groups both the
  Sources tree and the
  fortune-packs download tree, and is available offline before any catalog is fetched.
- **The runtime catalog publishes them.** `catalog.json` at the repo root is fetched over HTTPS at
  runtime (branch-pinned). Each entry records an id, display metadata, byte size, and a SHA-256
  digest. On download the app verifies the fetched bytes against that digest and rejects mismatches,
  oversized content, and non-HTTPS or off-repository URLs. Adding a pack to `catalog.json` makes it
  appear in-app with no application rebuild.
- **Bundled offline copy.** The portable ZIP ships every pack beside the executable (in `fortunes/`),
  so the full set works with no network access.

## Retired: the per-pack rights-approval gate

Earlier builds embedded a commit-pinned `packs.json` catalog with a **fail-closed per-pack rights
gate**: each pack was "held" (no download URL) until a maintainer wrote a strict rights document
under `docs/rights/`, added a matching `redistributionApproved` record in
`packaging/pack-rights-evidence.json`, and the release gate revalidated the pinned bytes. That
machinery (`packs.json`, `pack-rights-evidence.json`, `Test-PackRightsEvidence.ps1`) was **removed**
when packs moved to the runtime `catalog.json`.

The catalog still guarantees **integrity** — HTTPS delivery plus a per-file SHA-256 check on every
download — but there is **no longer a per-pack redistribution-rights gate**. The 158 packs are now
served as-is, and every one carries the catalog's single root `packLicense` of `NOASSERTION`. The
machine-checked source-rights evidence gate was retired with the enterprise release pipeline and has
not been replaced: `packaging/source-rights-evidence.json` no longer exists. Nothing in this repository
now checks redistribution rights automatically, for packs or for anything else. Before a public
release, they must be reviewed by hand.

## Provenance & licensing

These collections were fan-compiled from mixed public/community sources. Labels such as "fair use"
or "personal use" are notes, not redistribution grants:

- **Fair-use / public-domain:** tech, philosophy, literary, facts (fortune-mod datfiles, quotes).
- **Community lists (personal use):** dad jokes & showerthoughts (Reddit), BOFH excuse server.
- **Copyrighted excerpts (personal use):** pop-culture TV dialogue, comedy, spicy.
- **Adults only:** the 19 `off-*` packs in the NSFW collection (the classic `fortune -o` set, with
  hate files removed). There is no single `nsfw` pack.

Presence in this repository is not a redistribution grant. Enable a pack at your discretion and do
not assume any pack is rights-cleared. Rights holders can use the process in
[`SUPPORT.md`](../SUPPORT.md).

No warranty of accuracy or attribution. See `FORTUNE-SOURCES-ASSESSMENT.md` in the repo root for the
full source inventory.
