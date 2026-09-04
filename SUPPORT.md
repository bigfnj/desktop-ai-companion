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

## Application logs

Desktop AI Companion keeps a rolling diagnostic log of what it did during startup and while running. It
is on by default, because the faults worth reporting are the ones nobody saw coming, and a log you have
to switch on first is never on when it matters.

Where it lives:

| Install type | Path |
| --- | --- |
| MSI | `%LOCALAPPDATA%\DesktopAICompanion\diagnostics.log` |
| Portable ZIP | `data\diagnostics.log`, beside the executable |

The previous run is kept as `diagnostics.1.log`. That matters more than it sounds: a fault that leaves
the app running but unreachable, such as a missing notification-area icon, makes restarting the obvious
first move, and the restart is what would otherwise destroy the only record of it.

Everything is under Preferences, in the Diagnostic log group:

- turn logging off entirely;
- cap the current file, in kilobytes, defaulting to 512;
- choose how many files to keep, defaulting to 2, meaning this run and the one before;
- pick which parts of the app are recorded, by category; and
- pick which installed modules are recorded, one checkbox each.

Every category is on by default except Animation, which is per-frame movement churn. Leave it off unless
you are building a companion skin, since it repeats for as long as the app runs and would fill the cap
long before you got to read anything else.

Turning off every module you are not working on is the point of the per-module list: a module author
debugging one module gets that module's output and nothing else.

Review the log before attaching it to an issue. It records companion names, module identifiers and file
paths, which means it contains your Windows user name at minimum.

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
