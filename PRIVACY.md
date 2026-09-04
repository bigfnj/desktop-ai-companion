# Desktop AI Companion privacy

Last updated: 2026-08-14

DesktopAICompanion does not include advertising, analytics, crash reporting, or telemetry. Fortunes and
smart-fortune matching run locally. The optional AI brain is disabled by default.

## Data that can leave the computer

DesktopAICompanion sends data only when a network feature is used:

- When the AI brain is enabled, the configured AI provider can receive the system prompt, companion and
  user names entered in settings, time-of-day context, foreground-window title, OCR text derived
  from the screen, and recent conversation context. If **Use vision model** is enabled, an image of
  the screen is sent instead of OCR text for supported requests.
- The explicit **Refresh model list** and connection-test controls contact the configured provider.
  Granting cloud-data consent by itself remains network-silent. Configured model warm-up and
  Ollama model-unload operations can also contact that provider.
- Optional companion or fortune-pack downloads contact the source shown in the application. Trusted
  fortune-pack downloads are commit-pinned and integrity-checked; entries without documented
  redistribution approval are not downloadable.
- **Once a month**, DesktopAICompanion fetches the project's own content catalog to see whether an installed
  module has a newer build published, and tells you if one does. This is the only request the
  application makes without being asked, so it is worth being precise about: it is a plain HTTPS GET
  of a public file from the project's repository, it sends no identifiers, settings, or usage data of
  any kind, and it downloads or installs nothing — updating stays a button you click. It runs at most
  once per calendar month, is skipped entirely when no module is installed, and is not performed at
  all on a freshly installed copy (the first check falls in the following month). Turn it off with
  **Settings → Preferences → Modules → "Check for module updates monthly"**; with it off, the
  application makes no unprompted network request whatsoever.
- A companion author can supply an About link in the companion XML. DesktopAICompanion never opens that link
  automatically; only selecting the link asks the default browser to open the companion-supplied
  destination. The intended application policy is to accept only absolute HTTPS About links.
  Treat the destination as third-party content and review it before selecting it.
- The Help window itself is fully local. Selecting one of its online-documentation links asks the
  default browser to open that project-owned HTTPS repository page.

Remote AI endpoints must use HTTPS. Plain HTTP is accepted only for the local loopback computer.
Redirects are not followed. Sending screen context to a non-loopback provider requires the explicit
cloud-data consent setting.

The selected provider operates under its own privacy policy. DesktopAICompanion cannot control what a
provider logs or retains. Review that policy before enabling a cloud provider, and do not expose
sensitive information on screen while requesting commentary.

## Data stored locally

An installed copy stores mutable data in `%LOCALAPPDATA%\DesktopAICompanion`. A portable copy stores it in
the `data` directory beside `DesktopAICompanion.exe`. For isolated smoke testing, a fully qualified
`DESKTOP_AI_COMPANION_DATA_ROOT` environment-variable path overrides either location; relative,
drive-relative, and current-drive-rooted overrides are rejected or ignored. Data can include:

- application and AI settings;
- a rolling conversation history, only when memory is enabled;
- downloaded or user-supplied fortune files;
- local smart-fortune vector caches; and
- catalog/cache data.

Older versions used `%APPDATA%\DesktopAICompanion`; a current version may migrate supported files from that
location. API keys are encrypted at rest with Windows DPAPI for the current Windows user. DPAPI
reduces accidental disclosure but does not protect against software already running as that user.

Temporary OCR images and self-test logs may be created in the Windows temporary directory. OCR
images are deleted on a best-effort basis after processing.

## Your controls

- Keep the AI brain off and do not use the explicit model-list refresh or connection-test controls
  to prevent AI-provider requests. Opening Options and granting cloud-data consent do not by
  themselves contact the provider.
- Keep vision off to avoid sending screenshots.
- Use only a local loopback provider for local inference.
- Disable memory to stop retaining conversation history.
- Remove the application data directory to erase DesktopAICompanion's persisted settings, history,
  downloaded fortunes, and caches.

Privacy or security reports should follow [SECURITY.md](SECURITY.md).
