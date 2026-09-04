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

## Installer problems

The installer writes no log of its own, on purpose. An MSI can be told to log every run by carrying the
`MsiLogging` property, and that is the wrong default: it makes Windows Installer open a log file on every
install, uninstall and repair, so on any machine where `%TEMP%` is not writable — a redirected profile, a
full disk, a management agent holding a transaction — every one of those actions greets the user with
"Error opening installation log file" instead of working quietly. Logging is opt-in here so that the
failure mode is opt-in with it.

**Two ways to get what you need, in order of effort.**

The outcome is already recorded, with nothing to enable. Windows logs every install and uninstall to the
Application event log, with a status code:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'} -MaxEvents 40 |
  Where-Object { $_.Message -match 'Desktop AI Companion' } |
  Select-Object TimeCreated, Id, Message
```

`Removal success or error status: 0` means it worked, whatever any dialog said.

For a full trace of a specific run, ask for one explicitly and choose a path you know is writable:

```powershell
msiexec /i DesktopAICompanion.msi /l*v "$env:USERPROFILE\Desktop\install.log"
msiexec /x DesktopAICompanion.msi /l*v "$env:USERPROFILE\Desktop\uninstall.log"
```

A verbose MSI log records file paths and property values from your machine, so read it before attaching it.

Security vulnerabilities and privacy issues should follow [SECURITY.md](SECURITY.md). Use the
repository's private GitHub vulnerability-reporting form when available. If private reporting is
unavailable, open only a minimal issue asking the maintainer for a private contact channel; do not
include exploit details or secrets in a public issue.

The project does not provide emergency, medical, legal, or safety support.
