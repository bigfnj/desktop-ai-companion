# Fortunes module — design + backlog

## Core design (user directive, 2026-08-06): ship the engine, NOT the content
The Fortunes module ships the **framework** to enable fortunes — both **dumb** (random pick) and **smart**
(the ONNX/bge-small semantic picker) — plus the enable/import plumbing. It bundles **zero fortune content**.
A fresh install of the module is **silent** until the user adds a pack.

- **No embedded corpus.** The base's ~486KB embedded `fortunes.txt` stops being embedded. It becomes the
  canonical **importable / downloadable "starter pack"** — add via import now, served by the S7 catalog
  later. No content is lost; restoring the classic set is one action, but it's a *choice*, not a default.
- **The bge-small ONNX model is engine, not content.** "Smart" is a capability of the framework, so the
  model + onnxruntime travel with the module as assets (exactly like NAudio did for the Sound module).
- **Packs live in the module's storage** (`host.GetStorage("fortunes")`): the user's imported/downloaded
  fortune packs + the persistent vector/embedding cache. Empty by default.
- **Self-tests carry a tiny built-in test fixture corpus** (a handful of lines, not user content) so the
  engine stays verifiable without shipping real fortunes.

## Relocation plan (the engine moves out of the base into here)
Move `FortuneProvider` / `SmartFortunes` / `Embedder` / `FortuneFileImporter` + the `StartUp` fortune glue
(`SayFortune`, the random-drop loop, land greeting, poke fortune, `RebuildSmartFortunes`,
`SmartFortunesStatus`), rebinding base infrastructure to the ABI:
- data dirs (`AppPaths.PrepareFortunesDirectory` / `PrepareVectorCacheDirectory`) → `host.GetStorage`
- config (fortune fields in `AiSettings`) → the module's own `host.GetSettings` schema + one-time data migration
- screen context (`ActiveWindow`) → `host.CaptureScreenContext`
- ✅ ONNX model path (beside the exe) → beside the module dll (done: `Embedder.AppDir` resolves from
  `Assembly.GetExecutingAssembly().Location`, so the model travels inside the module package)
- bring the small safe-write helpers the engine needs into the module

## Later
- Rich pack management / import UI arrives with the WPF shell (S5); for now packs are read from the module's
  storage folder and settings come through the schema `OptionsPane`.
- A "starter pack" one-click install once the S7 catalog exists.
