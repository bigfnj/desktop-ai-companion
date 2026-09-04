# Remembrance module — plan

> A local "meeting memory" module: capture a meeting's audio, transcribe it offline, name it from the
> calendar, snapshot on demand, and self-purge the heavy media after 72 hours. Design converged with the
> user 2026-08-26. This doc is the agreed spec; `handoff.md` and the `project_desktoppet` memory point at it.

## What it does

- **Records** the meeting: a selectable **microphone** (input) and the **system output** (WASAPI loopback,
  a selectable render device), mixed to one file.
- **Start/stop** from the tray or a global **hotkey**. A separate **snapshot hotkey** grabs the screen on
  demand (something popped up you want to keep).
- On stop, **transcribes offline** with a local Whisper, writing `{meeting} - {timestamp}.transcript.txt`.
- **Names** each capture `{meeting} - {timestamp}` with a three-tier fallback: a current calendar event
  (published by the Reminder module), else a manual name if you typed one, else **just the timestamp** (no
  empty prefix). `{timestamp}` is filename-safe and sortable (`YYYY-MM-DD HH-MM-SS`, dashes in the time since
  Windows forbids colons in a path); the meeting name is sanitized of `\ / : * ? " < > |` first. Same in both
  storage modes: the folder takes the name (folder-per-capture on), or the files are prefixed with it (off).
- **Attendance** is the calendar's invitee roster (names + accepted/declined), written into the transcript
  header. No screen-scraping for attendance.
- **Purge:** a 72-hour timer deletes the **audio and any snapshots**. The **transcript and attendance record
  are kept permanently** — they are the durable value; the raw media is the sensitive, ephemeral layer.
- **Storage:** a default folder with a Browse to change it, and a **"create a folder per capture"** toggle
  (on by default): on = one `{meeting} - {timestamp}` folder per capture; off = everything flat in the root,
  files prefixed with `{meeting} - {timestamp}`.

## Decisions taken with the user

1. **Transcription: local Whisper.** Offline, nothing leaves the machine (right for work calls). Costs a
   one-time provisioning of whisper.cpp or a faster-whisper venv + a model. NOT the cloud.
2. **Speaker labeling (diarization): best-effort, a follow-up.** v1 ships a plain transcript. Real
   multi-speaker labeling (pyannote + PyTorch + a gated HF model + token) is the heaviest, least reliable
   piece and comes later.
3. **Reminder tie-in: a real shared-meeting channel in the host** (host ABI), not a file shim. The Reminder
   module publishes the current event; Remembrance reads it.
4. **Attendees come from the calendar**, not from screenshots. Periodic attendance screenshots are dropped.
5. **Snapshots are manual (a hotkey) and ephemeral** — included in the 72-hour purge.
6. **Disclosure lives in the module's install consent**, driven by capture permission flags. The companion's MSI
   installer is untouched.

## Consent and legal (a deliberate design point, not a blocker)

Recording calls has consent rules that vary by jurisdiction; California (the user's) is **all-party
consent**, and workplace policy may restrict recording internal meetings or moving audio off-device. So:
local-only storage by default, a **visible recording indicator** while capturing, and offline transcription.
Getting participants' consent is the user's responsibility. The module's catalog-install consent screen names
exactly what it records (see the permission flags below); nothing about recording touches the Windows MSI.

## Host ABI additions (Stage 0 — a release)

Both additive; they ride one host release (same redistribution caveat as v1.8.0).

- **Capture permission flags** on `ModulePermissions` (the enum has free bits `1<<1`, `1<<2`, `1<<4`):
  `Microphone`, `SystemAudio`, `Screen`. These are what make the module's install-consent screen read
  "records your microphone, system audio, and screenshots." Granular on purpose, so consent is specific.
- **A minimal shared-context channel** on `IHost`, reusable by any future cross-module handoff:
  ```csharp
  void PublishContext(string moduleId, string key, string valueJson);  // publisher owns the key namespace
  string ReadContext(string key);                                       // "" when nothing is published
  event Action<string> ContextChanged;                                  // fires the key that changed
  ```
  Reminder publishes `meeting.current` = `{name, start, end, location, attendees:[{name,status}]}` each tick
  (the ongoing or imminent event). Remembrance reads it when you hit record. Cost of the ABI add: ~8
  implementations (CompanionHost + RecordingHost + the test doubles), plus a ProductVersion bump in the same commit.

## Staged build

**Stage 0 — host foundation + Reminder (a host release).**
Add the permission flags + the shared-context channel; the Reminder module captures attendees (Outlook
`Recipients`, ICS `ATTENDEE`) and publishes `meeting.current`. Gate green, then this is the release that must
ship before Remembrance can declare the new permissions.

**Stage 1 — Remembrance skeleton (module, via catalog once Stage 0 ships).**
`modules/Remembrance`: mic + system-output device dropdowns; WASAPI loopback + mic capture mixed to a WAV;
start/stop from tray + a global hotkey; a snapshot hotkey; a visible recording indicator; storage-location
setting + Browse; the folder-per-capture toggle; naming from `meeting.current` or a manual box; the 72-hour
purge of audio + snapshots (transcript/attendance kept). No transcription yet. Declares
`Microphone | SystemAudio | Screen | Storage`.

**Stage 2 — transcription (module + a machine-provisioning step).**
Provision a local Whisper on the box (whisper.cpp binary or a faster-whisper venv + model). On stop:
ffmpeg → 16 kHz mono WAV → Whisper → `{name}.transcript.txt`, headed with the calendar attendee roster.

**Stage 3 — speaker labeling (later, best-effort),** per decision 2.

## Pause points (I stop and check before doing these)

- Installing Whisper + its runtime on the machine (Stage 2 — software provisioning).
- Cutting the host release tag (Stage 0).

## Open sub-decisions (sensible defaults chosen; easy to change)

- Snapshot hotkey when no recording is active: the snap goes to the storage root (still 72-hour purged).
- Mixing mic + loopback: resample both to a common rate and sum; two capture clocks can drift on long
  meetings, so a periodic resync may be needed (watch on the first long real capture).
- System-output capture is whole-system loopback (everything you hear, not just the meeting app). Per-app
  isolation needs the Win10-2004+ process-loopback API — deferred unless asked.

## Build status (2026-08-26): Stage 0-2 code DONE, gate green, on `master`; pending Whisper + publish

Host ABI 1.9.0 (shared-context channel + Microphone/SystemAudio permission flags) and Reminder 1.6.0
(attendees + `meeting.current` publish) are done + pushed. `modules/Remembrance` (v1.0.0) is written,
compiles at 0 warnings, and is built by the gate. **Not published to the catalog and no release cut** —
both wait on the two pause points. The live WASAPI capture and Whisper paths are build-verified only (audio
can't be run in the dev environment).

Decisions taken autonomously while building (call out if any should change):
- Permissions: reused the existing `ScreenContext` (snapshot) and `Hotkey` flags; only `Microphone` +
  `SystemAudio` were genuinely new. So the module declares `Microphone | SystemAudio | ScreenContext | Hotkey
  | Storage`.
- Audio: classic NAudio `WasapiLoopbackCapture` + `WasapiCapture` to per-source temp WAVs, mixed offline to one
  16 kHz mono 16-bit WAV (Whisper-native, small). The newer `WasapiRecorder`/`RealtimeCaptureMixer` are not in
  the pinned NAudio 3.0.0-preview.6, so the classic API is used (its obsolete warning is suppressed with a
  documented `NoWarn`).
- TFM `net10.0-windows10.0.19041.0` (NAudio.Wasapi's floor; the base stays windows7.0 / DirectSound).
- Naming: no manual-name prompt on a hotkey/tray start (there is no dialog moment), so it is `meeting.current`
  else the timestamp. A manual box could be added if wanted.
- Whisper: whisper.cpp CLI, exe + model paths in settings; a stub transcript when unset; a "Transcribe a WAV
  file…" action re-processes a kept recording.
- Transcription runs on a background task; host calls marshal to the UI thread via the captured
  SynchronizationContext.

Two pause points remain (waiting on you):
1. Install Whisper: pull a whisper.cpp `whisper-cli.exe` + a model (e.g. `ggml-base.en.bin`), ideally via a
   `scripts-utilities/install-whisper.ps1` matching the toolbox pattern; then set the two paths in options.
2. Publish + release: publish Reminder 1.6.0 + Remembrance 1.0.0 to the catalog and cut the host 1.9.0 release
   (`v1.9.0`), after a smoke test. Both modules need the 1.9.0 host, so they show "needs newer app" until then.

## Risks / notes

- Whisper accuracy and speed depend on the model + CPU/GPU; pick a model tier against this box's hardware.
- Long recordings are large; the 72-hour audio purge bounds disk use, but a single long meeting can still be
  hundreds of MB before it ages out.
- Diarization is deliberately out of v1; do not let the transcript format assume speaker labels exist.
