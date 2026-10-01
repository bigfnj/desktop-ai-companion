# Provenance & verifying downloads

Releases are Windows x64 builds, **unsigned** unless the release workflow finds a signing certificate
(`SIGNING_THUMBPRINT`; its release notes then say Signed instead of Unsigned, RA-002). Each GitHub
release carries `SHA256SUMS.txt`; verify a
download against it:

```powershell
Get-FileHash .\DesktopAICompanion-Portable*.zip -Algorithm SHA256
# compare to the matching line in SHA256SUMS.txt
```

> The former signed-provenance chain — Authenticode signing, an SPDX SBOM, and GitHub
> build-provenance attestations — was retired with the enterprise release pipeline. See
> [`Readme.md`](Readme.md) → *Continuous integration & releases* and
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
