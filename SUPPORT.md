# Support

Desktop AI Companion is a community project with no guaranteed response time or service-level
agreement.

## Before filing an issue

1. Confirm that Windows is 64-bit and the .NET Framework 4.8 runtime is installed.
2. For a published release, record the DesktopAICompanion version and whether you used the MSI or portable
   ZIP. Verify the artifact's SHA-256 checksum, signature, and provenance as described in
   [PROVENANCE.md](PROVENANCE.md).
3. For a private, local, or CI build, record the exact 40-character Git commit and clearly label it
   as a private, local, or CI build, not a release.
4. For AI issues, identify the provider and model, whether vision was enabled, and whether the
   endpoint was local or remote. Never post an API key, screenshot, OCR text, conversation history,
   or settings file without reviewing and redacting it.

Open a GitHub issue at https://github.com/bigfnj/desktop-ai-companion/issues with:

- published release version and artifact type (MSI or portable ZIP), or the exact 40-character Git
  commit plus the private, local, or CI build label;
- Windows version;
- concise reproduction steps;
- expected and actual behavior; and
- relevant redacted logs or screenshots.

Security vulnerabilities and privacy issues should follow [SECURITY.md](SECURITY.md). Use the
repository's private GitHub vulnerability-reporting form when available. If private reporting is
unavailable, open only a minimal issue asking the maintainer for a private contact channel; do not
include exploit details or secrets in a public issue.

The project does not provide emergency, medical, legal, or safety support.
