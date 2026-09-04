# Third-party notices

This file inventories known third-party material in Desktop AI Companion 1.0.0. It is not a
representation that every redistribution right has been cleared. The release checklist treats the
unresolved items below as blockers.

The repository-root MIT license applies only to original contributions whose copyright is held by
`bigfnj`. It does not relicense upstream code, artwork, text corpora, models, or libraries.

## Redistribution blockers

- The WinForms engine originated in `Adrianotiger/desktopPet`. That upstream repository has no
  license file or other verified redistribution grant. Obtain written permission or replace the
  code with code under a compatible license before public binary distribution.
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
- Record the exact source revision, conversion procedure, and retained license file for
  `bge-small-en-v1.5` before distribution. Its upstream model card identifies the model as MIT, but
  the repository currently lacks a pinned provenance record for the two shipped model files.

## Bundled with a verified redistribution grant

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

The current `src/Fortunes/fortunes.txt` is 1,312,352 bytes with SHA-256
`e7b0ec7abae7c990e919dd417025b0fe432033f3b848f74ef44363a08c9e598f`.
This identifies the audited corpus snapshot; it does not clear any source or quotation.

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

## Locked build inputs and runtime libraries

The runtime libraries and build-only inputs below are pinned by
[`src/packages.lock.json`](src/packages.lock.json). `Microsoft.Net.Compilers.Toolset` and
`Microsoft.NETFramework.ReferenceAssemblies.net48` are private build inputs and are not shipped as
runtime files.

| Component | Version | License | Project |
|---|---:|---|---|
| Microsoft.Net.Compilers.Toolset | 4.14.0 | MIT | https://github.com/dotnet/roslyn |
| Microsoft.NETFramework.ReferenceAssemblies.net48 | 1.0.3 | MIT | https://github.com/microsoft/dotnet |
| Microsoft.Bcl.AsyncInterfaces | 10.0.10 | MIT | https://github.com/dotnet/dotnet |
| Microsoft.ML.OnnxRuntime | 1.28.0 | MIT | https://github.com/microsoft/onnxruntime |
| Microsoft.ML.OnnxRuntime.Managed | 1.28.0 | MIT | https://github.com/microsoft/onnxruntime |
| Microsoft.Win32.Registry | 5.0.0 | MIT | https://github.com/dotnet/runtime |
| NAudio.Core | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio |
| NAudio.Dmo | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio |
| NAudio.Midi | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio |
| NAudio.WinMM | 3.0.0-preview.6 | MIT | https://github.com/naudio/NAudio |
| System.Buffers | 4.6.1 | MIT | https://github.com/dotnet/runtime |
| System.IO.Pipelines | 10.0.10 | MIT | https://github.com/dotnet/dotnet |
| System.Memory | 4.6.3 | MIT | https://github.com/dotnet/runtime |
| System.Numerics.Tensors | 9.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Numerics.Vectors | 4.6.1 | MIT | https://github.com/dotnet/runtime |
| System.Resources.Extensions | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | https://github.com/dotnet/runtime |
| System.Security.AccessControl | 6.0.1 | MIT | https://github.com/dotnet/runtime |
| System.Security.Permissions | 10.0.10 | MIT | https://github.com/dotnet/dotnet |
| System.Security.Principal.Windows | 5.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Text.Encodings.Web | 10.0.10 | MIT | https://github.com/dotnet/dotnet |
| System.Text.Json | 10.0.10 | MIT | https://github.com/dotnet/dotnet |
| System.Threading.Tasks.Extensions | 4.6.3 | MIT | https://github.com/dotnet/runtime |
| System.ValueTuple | 4.6.2 | MIT | https://github.com/dotnet/runtime |

The distributed payload retains these exact legal artifacts:

- Build-only packages are not redistributed in the product payload; their locked NuGet hashes and
  package-declared licenses remain in [`src/packages.lock.json`](src/packages.lock.json).
- `ONNXRUNTIME_LICENSE.txt` and `ONNXRUNTIME_THIRD_PARTY_NOTICES.txt` are copied byte-for-byte from
  the locked Microsoft.ML.OnnxRuntime 1.28.0 package.
- `NAUDIO_LICENSE.txt` is retained from NAudio commit
  `c89fee940ee6f8d7374d18714a6b85d8b7a18ab0`.
- The base app uses **NAudio 3.0.0-preview.6** (`NAudio.Core`, `NAudio.WinMM`, its transitive `NAudio.Midi`,
  and `NAudio.Dmo` for the device-selectable DirectSound output) for the host-owned audio output. The S2
  Sound module (which bundled NAudio 2.3.0) was retired in B4, so only NAudio 3 ships now.
- `DOTNET_RUNTIME_LICENSE.txt` is copied byte-for-byte from the locked Microsoft.Win32.Registry
  package. The same exact license bytes are carried by the other locked .NET packages that include
  a license file.
- `DOTNET_10_THIRD_PARTY_NOTICES.txt`, `DOTNET_8_THIRD_PARTY_NOTICES.txt`,
  `DOTNET_6_THIRD_PARTY_NOTICES.txt`, and `DOTNET_5_THIRD_PARTY_NOTICES.txt` retain each unique
  third-party-notice byte set shipped by the locked .NET packages. Identical package notice files
  are deduplicated only when their SHA-256 hashes match.

The dependency table above, together with [`src/packages.lock.json`](src/packages.lock.json), is the
runtime inventory.

## Historical documentation support files

The repository retains a generated Sandcastle documentation snapshot under `docs/` for historical
reference. The root Jekyll configuration excludes that entire directory from the public legacy
site, and these files are not part of the DesktopAICompanion application payload. Their licenses still
apply to source-repository distribution:

- `docs/SearchHelp.aspx`, `docs/scripts/branding.js`, and
  `docs/scripts/branding-Website.js` identify their Sandcastle support code as Microsoft Public
  License (Ms-PL). The complete Ms-PL text is reproduced below.
- `docs/scripts/jquery-1.11.0.min.js` is jQuery 1.11.0, copyright 2005, 2014 jQuery Foundation,
  Inc. and other contributors, distributed under the MIT License. The complete MIT text is
  reproduced below.

## Microsoft Public License (Ms-PL)

This license governs use of the accompanying software. If you use the software, you accept this
license. If you do not accept the license, do not use the software.

### 1. Definitions

The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same
meaning here as under U.S. copyright law.

A "contribution" is the original software, or any additions or changes to the software.

A "contributor" is any person that distributes its contribution under this license.

"Licensed patents" are a contributor's patent claims that read directly on its contribution.

### 2. Grant of Rights

(A) Copyright Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free
copyright license to reproduce its contribution, prepare derivative works of its contribution, and
distribute its contribution or any derivative works that you create.

(B) Patent Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free
license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or
otherwise dispose of its contribution in the software or derivative works of the contribution in
the software.

### 3. Conditions and Limitations

(A) No Trademark License- This license does not grant you rights to use any contributors' name,
logo, or trademarks.

(B) If you bring a patent claim against any contributor over patents that you claim are infringed
by the software, your patent license from such contributor to the software ends automatically.

(C) If you distribute any portion of the software, you must retain all copyright, patent,
trademark, and attribution notices that are present in the software.

(D) If you distribute any portion of the software in source code form, you may do so only under
this license by including a complete copy of this license with your distribution. If you distribute
any portion of the software in compiled or object code form, you may only do so under a license
that complies with this license.

(E) The software is licensed "as-is." You bear the risk of using it. The contributors give no
express warranties, guarantees or conditions. You may have additional consumer rights under your
local laws which this license cannot change. To the extent permitted under your local laws, the
contributors exclude the implied warranties of merchantability, fitness for a particular purpose
and non-infringement.

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
