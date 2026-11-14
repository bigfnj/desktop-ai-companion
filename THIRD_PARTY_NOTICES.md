# Third-party notices

This file inventories known third-party material in Desktop AI Companion and its downloadable
modules. It is not a
representation that every redistribution right has been cleared. The automated rights gates were
retired with the enterprise release pipeline and have not been replaced, so the items below are
recorded here for manual review rather than enforced by any check.

The repository-root MIT license applies only to original contributions whose copyright is held by
`bigfnj`. It does not relicense upstream code, artwork, text corpora, models, or libraries.

## Unresolved redistribution items

- The bundled and downloadable companion sprites have source-specific authorship and copyright notes,
  but a complete redistribution grant is not recorded for every asset. Clear or replace each asset.
- The bundled fortune corpus contains mixed sources, including copyrighted quotations and
  dialogue. Complete a source-by-source rights review and rebuild a cleared corpus before release.
- The optional fortune packs are fan-compiled from mixed public/community sources (fair-use notes are
  not redistribution grants). They are served via the runtime `catalog.json`, which verifies per-file
  SHA-256 integrity but does **not** clear rights: the former per-pack rights gate (`packs.json` with
  `redistributionApproved`, `packaging/pack-rights-evidence.json`, `Test-PackRightsEvidence.ps1`) was
  retired when packs moved to the catalog. Review each pack's redistribution rights by hand and clear,
  replace, or remove uncleared sources before public distribution (see `packs/README.md`).
  Every pack's catalog `license` field therefore reads `NOASSERTION`, the SPDX value for "no
  determination made". It previously read `LicenseRef-DesktopPet-Community`, an identifier that named
  the pre-rename product and whose license text does not exist anywhere in this repository or any
  other. That was a grant asserted 165 times against material this section says is uncleared, so it
  was removed rather than renamed: renaming it would have re-asserted the same fiction under the
  current product's name.
- Record the exact source revision, conversion procedure, and retained license file for
  `bge-small-en-v1.5` before distribution. Its upstream model card identifies the model as MIT, but
  the repository currently lacks a pinned provenance record for the two shipped model files.
- `aibrain.zip` and `remembrance.zip` ship the Windows SDK projection, whose terms have not been
  reviewed (N-bd-w2-docs-1-02). Every other third-party library a module ZIP carries ships its license
  text beside it; see "License files that ship" below.

## Bundled with a verified redistribution grant

- **The WinForms engine**, which originated in `Adrianotiger/desktopPet`. That upstream repository
  carries no license file, so redistribution rests on a direct grant rather than on a public licence:
  the maintainer holds **written permission from the upstream author** to redistribute. Attribution to
  Adriano Petrucci and the upstream project is retained here, in `LICENSE`, and in
  [`grimoire/01-history-and-lineage.md`](grimoire/01-history-and-lineage.md).

- **Shimeji base behaviour config** — `tools/ShimejiConvert.Engine/base-conf/actions.xml` and
  `behaviors.xml`, embedded in `ShimejiConvert.Engine.dll`. This is the default behaviour configuration
  from Shimeji-EE (Shimeji English Enhanced), included **unmodified** so Companion Studio's importer and the
  ShimejiConvert CLI can convert a sprites-only skin that ships no config of its own. Copyright (c) Shimeji-ee Group under a **3-clause
  BSD** license; the original Shimeji is Copyright (c) 2009 Yuki Yamada / Group Finity
  (http://www.group-finity.com/Shimeji/) under a BSD-style license — both explicitly permit
  redistribution provided the copyright notice is retained, which this notice does. Credit: Kilkakon
  (kilkakon.com) and the Shimeji-EE authors; source https://github.com/gil/shimeji-ee. Full text:
  `tools/ShimejiConvert.Engine/base-conf/NOTICE.txt`. Only the behaviour XML is bundled — never sprite
  art (skin artwork remains the skin author's copyright and the converter never ships it).

- **libwebp `dwebp`** — `tools/ShimejiConvert.Engine/native/dwebp.exe`, the reference WebP decoder from
  Google's libwebp, bundled so the converter can decode an Android bundle's `.webp` sprites with their
  alpha channel (the Windows WebP codec decodes to opaque BGR32 and drops alpha). libwebp 1.4.0
  windows-x64, statically linked, **3-clause BSD** (Copyright (c) 2010 Google Inc.); it ships in the
  ShimejiConvert CLI and the Companion Studio module. Full text + provenance (source URL, SHA-256):
  `tools/ShimejiConvert.Engine/native/NOTICE-libwebp.txt`.

The current `src/Fortunes/fortunes.txt` is 391,646 bytes with SHA-256
`a1c05ba2309ff554e070ea4baba0841213174e1b27152390c27e40b165bdf6cb`.
It now carries only two sources, `fortunes` (the BSD fortune file) and `dadjokes`; the other 24 it
used to bundle were moved to downloadable packs, which narrows what ships in the binary but does not
change their status below. This identifies the audited corpus snapshot; it does not clear any source
or quotation.

Current bundled bytes, recorded for identification only (these hashes do not establish provenance
or redistribution rights):

| File | Bytes | SHA-256 |
|---|---:|---|
| `bge-small.onnx` | 34,014,426 | `6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4` |
| `bge-small.vocab.txt` | 231,508 | `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3` |

## Source and asset provenance

The engine source, embedded corpus (`src/Fortunes/fortunes.txt`), embedder model
(`src/Models/bge-small.onnx`) and vocabulary, bundled art under `src/Images/` and `src/Resources/`,
and the downloadable companion art under `Companions/` are fan-compiled from mixed upstream and community sources.
Their provenance and attribution are documented here and in [`Readme.md`](Readme.md),
[`packs/README.md`](packs/README.md), and
[`FORTUNE-SOURCES-ASSESSMENT.md`](FORTUNE-SOURCES-ASSESSMENT.md). This is disclosure and attribution,
not a blanket redistribution clearance. (The former hash-bound rights-evidence gate was retired with
the enterprise release pipeline.)

The `Companions/shimeji-*` entries are Shimeji community skins converted to the companion format: one set from
shimeji.org (the maintainer's own curated pull, redistribution asserted for that specific set) and a
small vetted set from shimejis-xyz, each recording its creator and source page in
[`Companions/companions.json`](Companions/companions.json) (`author`, `source`). The **depicted characters remain their
respective owners' intellectual property**, and per-skin artist copyright is unchanged; this is
attribution, not a clearance of the underlying character IP.

## Third-party libraries that ship

Every library below is redistributed as a binary. The versions are the resolved ones; where a row
disagrees with the pin it names, the pin is right and this file is stale.

### The application payload

Pinned by [`src/packages.lock.json`](src/packages.lock.json) and shipped beside the exe, in both the
portable ZIP and the MSI, by the list in [`packaging/runtime-files.txt`](packaging/runtime-files.txt).

| Component | Version | License | Project | Pulled in by |
|---|---:|---|---|---|
| NAudio.Core | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | direct |
| NAudio.Dmo | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | direct |
| NAudio.WinMM | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | direct |
| NAudio.Midi | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | NAudio.WinMM |
| System.Numerics.Tensors | 9.0.0 | MIT | https://github.com/dotnet/runtime | NAudio.Core |

The application is framework-dependent. The .NET 10 Desktop runtime is a prerequisite the user
installs, not something this payload conveys, so no .NET runtime license or notice file ships with it.

### Downloadable modules

Module projects keep no lock file. Each package is pinned by the exact `Version` of its
`PackageReference` in the module's `.csproj`, and the set a module actually carries is recorded in the
`<Module>.deps.json` inside its ZIP.

| Component | Version | License | Project | Module | Pulled in by |
|---|---:|---|---|---|---|
| Microsoft.ML.OnnxRuntime | 1.28.0 | MIT | https://github.com/microsoft/onnxruntime | Fortunes | direct |
| Microsoft.ML.OnnxRuntime.Managed | 1.28.0 | MIT | https://github.com/microsoft/onnxruntime | Fortunes | Microsoft.ML.OnnxRuntime |
| System.Numerics.Tensors | 9.0.0 | MIT | https://github.com/dotnet/runtime | Fortunes, Remembrance | Microsoft.ML.OnnxRuntime.Managed, NAudio.Core |
| Ical.Net | 5.2.3 | MIT | https://github.com/ical-org/ical.net | Reminder | direct |
| NodaTime | 3.2.2 | Apache-2.0 | https://github.com/nodatime/nodatime | Reminder | Ical.Net |
| NAudio.Core | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | Remembrance | direct |
| NAudio.Wasapi | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | Remembrance | direct |
| NAudio.Midi | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio | Remembrance | NAudio.Wasapi |
| Windows SDK projection (`Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`) | 10.0.19041.57 | Windows SDK license terms, linked from the package metadata as https://aka.ms/WinSDKLicenseURL and not yet reviewed here | https://aka.ms/WinSDKProjectURL | AI Brain, Remembrance | the `net10.0-windows10.0.19041.0` target framework |

The AgentBroker module's `worker/` folder is the one self-contained payload. It carries the .NET and
ASP.NET Core shared frameworks and the Model Context Protocol C# SDK, whose packages are pinned by
[`tools/AgentBroker.Worker/packages.lock.json`](tools/AgentBroker.Worker/packages.lock.json). Its
inventory, and the license and notice files that ship beside it in `worker/licenses/`, are
[`packaging/AgentBrokerNotices/THIRD-PARTY-NOTICES.txt`](packaging/AgentBrokerNotices/THIRD-PARTY-NOTICES.txt)
and the files next to it. `packaging/Publish-AgentBrokerWorker.ps1` refuses to publish the worker when
the versions recorded there disagree with that lock file or with the runtime it publishes.

### License files that ship

The Ical.Net, NodaTime and NAudio packages carry a license expression and no license file, so their
texts are committed under `packaging/licenses/`, each taken from the upstream commit the package's
nuspec names. The repository stores text with LF line endings (`* text=auto eol=lf` in
`.gitattributes`), so a text whose upstream copy has CRLF is committed with LF and its git blob differs
from the upstream one; nothing else about the bytes changes.

- The application payload: `LICENSE.txt`, this file, and `NAUDIO_LICENSE.txt`.
- `NAUDIO_LICENSE.txt` (the application payload and `remembrance.zip`): NAudio's `LICENSE` at commit
  `38257478ad2444c80f2a0b7f079d3ae7a820dfec`, the commit the shipped 3.0.0-preview.6 packages name
  (upstream git blob `034ae805fbf6887e4b415771dd8548e4ac4f179a`, CRLF upstream, committed with LF).
  Until 2026-11-14 this file held the text retained from NAudio commit
  `c89fee940ee6f8d7374d18714a6b85d8b7a18ab0`, whose copyright line reads 2020 where 38257478's reads
  2008-2026.
- `reminder.zip`: `ICALNET_LICENSE.txt`, Ical.Net's `license.md` at commit
  `1344396c18dfe7349584709990f431931bc0b6be` (git blob `c0eb585d55d2ad1db3058cf42690abb1101b2e11`,
  byte for byte under a `.txt` name); `NODATIME_LICENSE.txt` and `NODATIME_NOTICE.txt`, NodaTime's
  `LICENSE.txt` and `NOTICE.txt` at commit `e56daa2599a4d1917065c015bda923e7a352a236` (git blobs
  `d645695673349e3947e8e5ae42332d0ac3164cd7` and `81ef2010ac99630b6312584992de9fe08a1e6666`, byte for
  byte).
- `remembrance.zip`: `NAUDIO_LICENSE.txt` (above), and `TENSORS_LICENSE.txt` and
  `TENSORS_THIRD_PARTY_NOTICES.txt`, the `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` of the
  System.Numerics.Tensors 9.0.0 package (dotnet/runtime commit
  `9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3`, as its nuspec names), copied from the package with its
  CRLF committed as LF.
- `fortunes.zip`: `ONNXRUNTIME_LICENSE.txt` and `ONNXRUNTIME_THIRD_PARTY_NOTICES.txt`, copied at build
  time from the restored Microsoft.ML.OnnxRuntime package, so they cannot drift from its pinned
  version, and `TENSORS_LICENSE.txt` and `TENSORS_THIRD_PARTY_NOTICES.txt` as in `remembrance.zip`.
- `petstudio.zip`: `native/NOTICE-libwebp.txt`, and `base-conf/NOTICE.txt` for the Shimeji base behaviour
  config it embeds.
- `agentbroker.zip`: `worker/licenses/`, described above.

The Reminder, Remembrance and Fortunes license files ship from their module projects with no `Exists`
condition, so a missing file fails the build instead of shipping the ZIP without it;
`tests/runtime-hardening-selftest.ps1` holds the three projects to that. `aibrain.zip` ships no license file: its
one third-party component is the Windows SDK projection, recorded under "Unresolved redistribution
items" above.

These tables, with the lock and project files they name, are the runtime inventory.

## MIT license text

Copyright (c) the respective MIT-licensed contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
