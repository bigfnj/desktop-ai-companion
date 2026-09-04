# Provenance & verifying downloads

Releases are **unsigned** Windows x64 builds. Each GitHub release carries `SHA256SUMS.txt`; verify a
download against it:

```powershell
(Get-FileHash .\DesktopAICompanion-Portable.zip -Algorithm SHA256).Hash.ToLowerInvariant()
# compare to the matching line in SHA256SUMS.txt
```

> The former signed-provenance chain — Authenticode signing, an SPDX SBOM, and GitHub
> build-provenance attestations — was retired with the enterprise release pipeline. See
> [`Readme.md`](Readme.md) → *Continuous integration & releases* and
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
