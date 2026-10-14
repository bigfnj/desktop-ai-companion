# Desktop AI Companion privacy

Last updated: 2026-10-13

DesktopAICompanion does not include advertising, analytics, crash reporting, or telemetry. Fortunes and
smart-fortune matching run locally. The optional AI brain is disabled by default. Claude Code, when the AI brain
or a meeting summary runs on it, does its own reporting to Anthropic; that reporting is Claude Code's, not this
application's, and is described below. The companion starts Codex without your own Codex `config.toml`, so an
analytics or telemetry choice made there does not apply to these calls; what Codex itself reports to OpenAI has not
been measured.

## Data that can leave the computer

DesktopAICompanion sends data only when a network feature is used:

- When the AI brain is enabled, the configured AI provider can receive the system prompt, companion and
  user names entered in settings, time-of-day context, foreground-window title, the title of the window the
  companion is standing on, OCR text derived from the screen, and recent conversation context. If **Use vision
  model** is enabled, an image of the screen is sent instead of OCR text for supported requests.
- **Since 1.1.0 it also sends the PROCESS NAMES of other windows on the companion's monitor** (for
  example `outlook, msedge`), as a line reading "Also open on this screen: ...". Process names and not
  window titles, deliberately: the title is where document names, message subjects and customer names
  live, and for "what else is open" the application is the useful part anyway. That distinction is
  asserted by a test rather than left to convention, so a title cannot start reaching the prompt
  unnoticed. Capture follows the monitor the companion is standing on, so windows on your other
  displays are not described.
- **Since AI Brain 1.3.0 the brain can run on a coding-agent CLI** (Claude Code or Codex) instead of a
  model server. When **Brain runs on** is *Claude Code CLI* or *Codex CLI*, every AI Brain request goes
  through that CLI to its vendor, Anthropic or OpenAI, under whatever account the CLI is signed into:
  remarks, Ask, the hotkey, the tray row, the random drops, the poke reaction and the persona auditions,
  each with the persona, the front window's title, the title of the window the companion is standing on and
  the OCR text, or a screenshot when **Use vision** is on. Choosing the CLI is that decision: the cloud-data
  consent setting below does not gate this path.
  The calls run with the CLI's session saving switched off, so they leave no transcript in its own
  history. Codex always adds its own global instructions file (`~/.codex/AGENTS.md`, or `AGENTS.override.md`
  beside it, which Codex prefers when it exists) to the prompt, and no setting removes it, so that file's text
  reaches OpenAI with every Codex call. Since AI Brain 1.5.2 and Remembrance 2.3.2 a Codex call reads no
  `AGENTS.md` from the module's folder or the folders above it, and still sends that global file. Before these
  versions, wherever a `.git` folder sat above the module's folder (a home folder kept in Git, for instance), every
  `AGENTS.md` from there down went with every call. On Windows nothing in your organisation's Codex configuration
  turns that reading back on: Codex ignores a `managed_config.toml` there, the settings it enforces
  (`requirements.toml`) include none for it, and a system or cloud `config.toml` only sets defaults, which the
  companion's setting replaces as your own Codex `config.toml` would. That is read from Codex's own code
  (version 0.160.1), not measured.
- **Since AI Brain 1.5.6 and Remembrance 2.3.6, what Codex itself adds to a call, apart from your own
  `~/.codex/AGENTS.md`, names no folder of yours, on Codex 0.160.1, the version measured.** Codex labels each
  screenshot it sends with the file path it was given, and until AI Brain 1.5.6 that was the screenshot file's full
  path, which on an installed copy runs through your user profile folder and so names your Windows account. A
  screenshot now goes to Codex as `screen.png` in a folder of the call's own, so the label reads `screen.png` and
  nothing more; the image is the same. Every Codex call, a remark and a summary alike, also carried Codex's skills
  section, which lists the folders Codex keeps skills in, by default under your user profile; the companion now turns
  that section off. Measured with Codex 0.160.1 on the requests it builds, offline and with no model call: with both
  changes nothing else Codex adds holds a folder or profile path, and the screenshot is still sent. What the call carries of
  your own still goes as it is and can name your folders: the front window's title (File Explorer's is the folder it
  shows) and the title of the window the companion is standing on, the text read off the screen or the screenshot
  itself, and a summary's transcript. A later Codex that renamed the key the companion uses to turn the skills section
  off would send that section again, with nothing failing. Your own `~/.codex/AGENTS.md` still goes, as above, with
  whatever it says.
- **Since Remembrance 2.1.0 the meeting summary can run on a coding-agent CLI** too. It is off by default
  (**Summary runs on** starts at *Local model*, the local Ollama). When a CLI is chosen, the transcript's text goes to
  Anthropic or OpenAI once per recording, in one call with the summary prompt, and so does a transcript
  you summarize with *Summarize a transcript...*. The audio never leaves the computer: transcription
  stays on the local Whisper, and a transcript over 360 KB is never sent: it is summarized by the local
  model instead when one is set, and otherwise the Status line says it was not summarized.
  The summary file's header names the CLI and says the transcript was sent to be summarized.
- **Since AI Brain 1.5.1 and Remembrance 2.3.1 a Claude Code call leaves out your own hooks, the instruction
  files around its folder and claude.ai connectors.** It runs none of the hooks in your own settings or in
  plugins you installed, sends none of the `CLAUDE.md`, `CLAUDE.local.md` or `AGENTS.md` files around the
  module's folder, and loads no MCP servers or claude.ai connectors, your organisation's included (the call
  has no tools to use them). Before these versions an
  installed copy sent a `CLAUDE.md` or `AGENTS.md` kept in your home folder with every remark and summary. Your own
  settings files are still read, so their permission rules and extra folder names still reach the prompt. It
  switches off none of the controls an organisation manages: managed settings, managed instructions (a managed
  `CLAUDE.md`) and managed hooks still apply, and a managed hook can see what a call sends and what comes back.
  The calls run on your Claude Code sign-in, or on a token saved in the module (below).
- **Claude Code reports to Anthropic with each call, and the companion does not turn that off.** It sends its
  own usage analytics and metrics, which carry a device identifier, your account's e-mail address and your
  organisation and account identifiers. Each call also starts a few helper processes of Claude Code's own: a
  read of its managed-policy registry key, a few git checks, its built-in file search, and a read of the
  machine identifier Windows keeps.
- **Since Remembrance 2.2.0 the meeting summary can run on a cloud provider** (OpenAI, OpenRouter, or any
  OpenAI-compatible base URL). When **Summary runs on** is *Cloud provider*, the transcript's text goes to
  that provider's base URL once per recording, in one call with the summary prompt, and so does a transcript
  you summarize with *Summarize a transcript...*; the audio never leaves the computer, and a transcript over
  360 KB is summarized by the local model instead. Choosing it is that decision. The API key is encrypted for
  your Windows account (DPAPI) in Remembrance's own data folder, sent only to that base URL as its bearer
  credential, never written to a settings file or the log, and deleted by **Remove key**. The summary file's
  header names the provider, the model and the host the transcript went to.
- **Since AI Brain 1.3.1 and Remembrance 2.1.1 either module can hold a Claude sign-in token** (one made
  with `claude setup-token`), so its Claude Code calls run on that token's account instead of the account
  Claude Code itself is signed into. It is optional and blank by default. The token is encrypted for your
  Windows account (DPAPI) in the module's own data folder, never written to a settings file or the log,
  and never shown again (the pane says only that one is saved). It is handed to the Claude Code that
  module starts, as `CLAUDE_CODE_OAUTH_TOKEN`, and to nothing else; Claude Code sends it to Anthropic as
  the credential for those calls. **Remove token** deletes it. A token can only make model requests, and
  anyone who holds it can make them on your plan, so treat it like a password.
- The explicit **Refresh model list** and connection-test controls contact the configured provider.
  Granting cloud-data consent by itself remains network-silent. Configured model warm-up and
  Ollama model-unload operations can also contact that provider.
- Optional companion or fortune-pack downloads contact the source shown in the application. Every
  download is integrity-checked against a SHA-256 recorded in the catalog before it is installed. The
  catalog URLs are **branch-pinned**, not commit-pinned, and there is no per-pack redistribution gate:
  the retired rights-evidence gate has not been replaced, so every listed pack is downloadable and each
  one's licence field reads `NOASSERTION`. Integrity is checked; rights are not.
- **At most once a week**, DesktopAICompanion fetches the project's own content catalog to see whether a
  newer build has been published, and tells you if one has. There are **three** such checks — one for
  installed modules, one for installed companions, and one for the application itself — and they are the
  only requests the application makes without being asked, so it is worth being precise about them. Each
  is a plain HTTPS GET of a public file from the project's repository, sends no identifiers, settings, or
  usage data of any kind, and downloads or installs nothing: updating stays a button you click. The module
  check is skipped entirely when no module is installed. Turn each off under
  **Settings → Preferences → Modules**, where they read *"Check weekly for module updates"*,
  *"Check weekly for companion updates"* and *"Check weekly for a new app version"* — each adding
  "(tells you; never installs on its own)". With all three off, the application makes no unprompted
  network request whatsoever.
- A companion author can supply an About link in the companion XML. DesktopAICompanion never opens that link
  automatically; only selecting the link asks the default browser to open the companion-supplied
  destination. The intended application policy is to accept only absolute HTTPS About links.
  Treat the destination as third-party content and review it before selecting it.
- The Help window itself is fully local. Selecting one of its online-documentation links asks the
  default browser to open that project-owned HTTPS repository page.

Remote AI endpoints must use HTTPS. Plain HTTP is accepted only for the local loopback computer.
Redirects are not followed. Sending screen context to a non-loopback provider requires the explicit
cloud-data consent setting, except through a coding-agent CLI, where choosing the CLI under **Brain
runs on** is the choice. For a meeting summary, choosing a CLI or a cloud provider under **Summary runs on**
is the choice.

The selected provider operates under its own privacy policy. DesktopAICompanion cannot control what a
provider logs or retains. Review that policy before enabling a cloud provider, and do not expose
sensitive information on screen while requesting commentary.

## Data read on this computer but never sent anywhere

The optional **AgentFlow** module reads the transcript files that a coding agent writes about its own
session — Claude Code's `%USERPROFILE%\.claude\projects\<project>\<session>.jsonl` and Codex's
`%USERPROFILE%\.codex\sessions\...`. This is called out separately, and in this much detail, because it is
the most sensitive thing any part of this application reads: those files contain every command the agent
ran, every file path it touched, and the full text of everything you typed to it.

What the module does with them:

- It reads them **only to answer one question**: is a tool call sitting unanswered, and would the
  permission rules already on your disk have prompted for it? That is what lets the companion tell you an
  agent is waiting.
- **Nothing leaves the computer.** The module makes no network request of any kind. It has no provider, no
  key and no endpoint.
- **No command, argument, path or prompt text is ever spoken or written down.** The speech bubble names the
  tool (`Bash`) and the last segment of the working directory (`my-project`) — never the command and never
  the full path, because a bubble is on screen where anyone behind you can read it. The diagnostic log gets
  tool names, counts and durations only. Both of those limits are asserted by the module's self-test rather
  than left to convention, so they cannot quietly stop being true.
- It **never answers a prompt for you**, and that is not a setting you can switch on.
- It declares `AgentTranscripts` in its permission list, so the Modules pane shows you this read **before**
  you install it. Nothing installs itself.

The module is not installed by default and the base application never reads these files. Remove it from
**Options → Modules** and the read stops entirely; uninstalling also deletes its settings.

## Data stored locally

An installed copy stores mutable data in `%LOCALAPPDATA%\DesktopAICompanion`. A portable copy stores it in
the `data` directory beside `DesktopAICompanion.exe`. For isolated smoke testing, a fully qualified
`DESKTOP_AI_COMPANION_DATA_ROOT` environment-variable path overrides either location; relative,
drive-relative, and current-drive-rooted overrides are rejected or ignored. Data can include:

- application and AI settings;
- a rolling conversation history, only when memory is enabled;
- downloaded or user-supplied fortune files;
- local smart-fortune vector caches;
- catalog/cache data; and
- a rolling diagnostic log, on by default, kept as `diagnostics.log` plus the previous run.
  It records what the application did during startup and while running: companion names, module
  identifiers, and file paths, which means it contains your Windows user name. It never leaves the
  machine on its own. Turn it off, cap its size, or choose what is recorded under
  **Preferences -> Diagnostic log**; see [`SUPPORT.md`](SUPPORT.md) for where the file lives and what
  to review before attaching it to a bug report.

Older versions used `%APPDATA%\DesktopPet` (the pre-rename name, which the migration code deliberately
still spells that way); a current version may migrate supported files from that location. API keys are encrypted at rest with Windows DPAPI for the current Windows user. DPAPI
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
