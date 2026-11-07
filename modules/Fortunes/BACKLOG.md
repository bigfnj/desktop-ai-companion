# Fortunes module — design + backlog

## Core design, as built
The Fortunes module ships the **framework** for fortunes, both **dumb** (random pick) and **smart** (the
ONNX/bge-small semantic picker), plus the enable/import/download plumbing, AND a small built-in corpus so a
fresh install has something to say before the user adds a pack.

- **The built-in corpus is embedded.** `Fortunes.csproj` embeds `src/Fortunes/fortunes.txt` (two sources,
  Classic Fortunes and Dad Jokes: 3,225 rows, about 391 KB, since the corpus was shrunk to those two in
  d71de2e), and `FortuneProvider.LoadStandardCorpus` loads it FIRST in every standard pool, ahead of the
  bundled packs and the user's fortunes folder. The other sources it once carried are downloadable packs.
- **The bge-small ONNX model is engine, not content.** "Smart" is a capability of the framework, so the
  model + onnxruntime travel with the module as assets (`Embedder.AppDir` resolves beside `Fortunes.dll`).
- **Packs live in the module's storage** (`host.GetStorage("fortunes")`): the user's imported/downloaded
  fortune packs + the persistent vector/embedding cache.
- **The self-tests run over the embedded corpus and synthetic fixtures**, not the user's packs: the smart
  suite over a sample of the built-in corpus (`SmartFortunes.DiagnosticPool`), the filter and importer
  suites over entries they write themselves.

This section replaces the 2026-08-06 directive "bundle zero fortune content, silent until a pack is added",
which was superseded when the module started embedding the corpus (N-audit-1106-209).

## Relocation (done)
The engine moved out of the base into `engine/` here (`FortuneProvider`, `SmartFortunes`, `Embedder`,
`FortuneFileImporter`) along with the speech glue (`FortunesModule.SpeakFortune`, the random-drop loop, land
greeting, poke fortune, the smart-index rebuild and its status line), rebound to the ABI: storage through
`host.GetStorage`, settings through the module's own `host.GetSettings` schema, screen context through
`host.CaptureScreenContext`, the model beside the module dll. The base keeps only the pack-load bounds the
catalog shares (`src/dotNet/Ai/FortunePackLoadPolicy.cs`) and the `--fortunes-engine-selftest` bridge that
loads this module (`src/dotNet/Plugins/FortunesEngineSelfTest.cs`).

## Later (both done)
- Pack management arrived with the options pane: its installed-packs card imports pack files ("Import your
  own…") and its "Available online" card downloads from the catalog ("Download selected"), both in
  `FortunesModule.cs`.
- The "starter pack" one-click install is moot: the built-in corpus above is embedded, and every other pack is
  one tick and "Download selected" away.
