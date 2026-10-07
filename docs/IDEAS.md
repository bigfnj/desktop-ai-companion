# Ideas — product, not engineering debt

**What this is.** Queued product ideas, each kept with the reasoning that produced it. Extracted
from [`../BACKLOG.md`](../BACKLOG.md) on 2026-09-17 for the reason that file states about itself: it
holds work that can be picked up, and none of this can be picked up yet. Nothing here is scoped,
estimated or scheduled.

**Not the same as blocked.** An item with a named blocker lives in [`BLOCKED.md`](BLOCKED.md); an
item that was decided AGAINST lives in [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md). What is left here
is wanted, unscoped, and waiting on somebody deciding it is worth a cycle.

**The numbering is load-bearing, and it continues rather than restarting.** Ideas 1 to 15 are closed
and live in [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md) with their numbers intact, because
several are cited by number elsewhere: grep `backlog #` to find them, which today reaches
`../handoff.md` (#9 and #17) plus [`HISTORY-pre-1.0.0.md`](HISTORY-pre-1.0.0.md) and
[`ISSUES-pre-1.0.0.md`](ISSUES-pre-1.0.0.md) (#8, #9, #11, #12, #14, #15, #16, #17). The next one
filed is 21. Glyphs are
[`../BACKLOG.md`](../BACKLOG.md)'s: ✅ done · 📌 open with the reasoning recorded · ⬜ not started ·
⚠ a caveat or an unobserved claim.

**An idea leaves this file in one of two directions:** into [`../BACKLOG.md`](../BACKLOG.md) when
somebody is going to do it, or into [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md) when it is refused.
Neither happens by leaving it here.

---

16. **Per-companion speech personality/preference** (queued 2026-08-11, unscoped, user's own caveat: "this may be
    complicated"). Today every on-screen companion shares the SAME global voice config — one `AiSettings.
    Disposition`, one (still-being-designed, not yet built) "Trigger Speech" source preference. The idea:
    let each companion TYPE carry its own — e.g. one sheep is AI Brain running the "Wednesday Addams" disposition,
    another is Fortunes tuned toward dad-joke-leaning packs, a third is AI Brain again but on "Jules
    Winnfield." Multi-companion-type coexistence already exists (`CompanionTypeRegistry`, backlog #7, DONE), so the
    on-screen mechanics for "more than one distinct companion at once" are already solved — what's NOT solved is
    that voice/personality config is a single global `AiSettings`/`FortuneSettings` blob, not keyed per companion
    type. Real complexity to scope later: (a) the AI brain's settings (disposition, model, provider) would
    need to become per-companion-type rather than one shared `AiSettings` document; (b) which companion a given
    poke/drop/AI-ask event is "for" already resolves through `ICompanion`/`CompanionHandle` in the ABI, so the plumbing
    to know WHICH companion triggered a reaction may already be there — needs verifying, not assuming; (c) whatever
    "Trigger Speech" setting design lands (still an open discussion as of this note) should be built with
    this in mind from the start — a global-only setting now that has to be retrofitted to per-companion later is
    much more painful than designing the storage key as companion-type-aware from day one, even if the UI stays
    global-only for its first cut.

17. 📌 **SUPERSEDED by the Remembrance module — all three phases shipped, one gap left.** The want was: click a tray
    item, record the mic **and** system/loopback audio, click again to stop, then transcribe and
    summarize. Checked at source 2026-09-17 rather than assumed, because this entry was wrong about
    its own residuals once already:
    - **P1, trigger + capture.** `modules/Remembrance/AudioRecorder.cs` records both directions
      (`WasapiLoopbackCapture` at `:52`, `WasapiCapture` at `:57`) with per-direction device pickers.
      `RemembranceModule.cs:700` contributes the one-click tray entry, `Click = ToggleRecording`, whose
      `DynamicText` reads "Start recording a meeting" and then "● Recording: … (click to stop)" — that
      is both the click-to-stop trigger and the recording-in-progress indicator this entry asked for.
      A hotkey exists as well, and `ModulePermissions` gained the honest flags: `Microphone` and
      `SystemAudio`, declared by `remembrance` in `modules.json`.
    - **P2, transcription.** Local Whisper via `Transcriber.cs` + `WhisperInstaller.cs`. The download
      and the whisper-cli run are verified live on the dev box — see
      [`BLOCKED.md`](BLOCKED.md), which records that independently of this entry.
    - **P3, summary.** `OllamaSummarizer.SummarizeAsync` at `:246`, writing the `.summary.txt` path
      `CaptureStore.cs:48` builds. The map-reduce is verified live too.

    **ONE gap remains against the original wording**, deliberate in Remembrance and real only if the
    goal is *listening* rather than transcribing: the output is **two WAV files downmixed to mono
    16 kHz** (`.mic.wav` and `.system.wav`, tuned for Whisper — the `StereoToMonoSampleProvider` +
    `WdlResamplingSampleProvider(…, 16000)` chain at `AudioRecorder.cs:128-132`), not one MP3 at
    listenable quality. **Reduced scope if wanted: an output-format choice on Remembrance, not a new
    module.** That is the whole idea; it is here rather than in the backlog because nobody has asked
    to listen to a capture yet.

    ⚠ **The second residual this entry used to name was already closed and the entry had not
    noticed** — it said "a hotkey rather than a one-click tray entry" while
    `BuildRecordTrayItem` had shipped. Measured 2026-09-17. A superseded entry keeps rotting after the
    thing that superseded it moves on, which is the argument for deleting rather than annotating.

    *(The design constraints this entry accumulated are settled and now live in
    [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md): module→module calls do not exist and nothing
    should be designed assuming them, and the browser Web Speech API path was rejected on three
    independent counts. Local-only, "no cloud STT or summary path, ever", was a shipped property of
    Remembrance until 2.1.0. On 2026-10-06 the owner ruled that the choice belongs to the END USER, so the
    summary can now run through a coding-agent CLI (Claude Code or Codex) as an opt-in that starts off;
    transcription stays local, since neither CLI takes audio. The decision is recorded under
    `#### feature/cli-backend` in the register.)*

18. **Consolidate standalone tray utilities into companion modules — one candidate left** (2026-08-20,
    not scoped). The companion is an always-on tray host with a plugin ABI, so it is a natural home
    for the small single-purpose tray apps in this account. Three were assessed against the module
    model (in-proc .NET 10 C# `IModule` in its own ALC, talking only to `IHost`; user surface = tray
    items + declarative pane, no self-shipped WinForms/WPF). **Two of the three are resolved:**
    blinkingLED was ported and ships as `modules/BlinkingLed/`, gated by
    `--module-selftest=blinkingled` in both `tests/run-gate.ps1` and `.github/workflows/build.yml`;
    LightHost was refused and its reasoning is in
    [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md).

    - **IdleLauncherTray (`bigfnj/IdleLauncherTray`) — port-with-work, but licensing gates it, so the
      item itself is in [`BLOCKED.md`](BLOCKED.md) (T58).** The technical assessment is kept
      here because BLOCKED.md points at it: the idle engine (`PhysicalIdle`: global
      `WH_KEYBOARD_LL`/`WH_MOUSE_LL` hooks reading the `LLKHF_INJECTED` flag to tell physical input
      from `SendKeys`/automation, `GetTickCount64` monotonic timing, XInput gamepad poll,
      `GetLastInputInfo` fail-safe) is dependency-free P/Invoke and drops straight into a module
      timer; config → pane, target-file chooser → `IHost.PickFilesToOpen` (host owns the dialog).
      **Biggest technical care: the low-level hook is global but injection-free, so an ALC-loaded lib
      CAN install it on the host UI thread — but it MUST be `UnhookWindowsHookEx`'d in `Shutdown()`
      or an ALC unload leaks a dangling hook.** Companion framing: the sheep sleeps after N
      genuine-idle minutes (not fooled by anti-idle jiggles), launches your target on wake/poke, and
      locks the PC when it closes.

    - **Licensing is a recurring gate**, and it is what is left of this item: IdleLauncherTray is
      GPLv2 against an MIT companion, and it is bigfnj-owned so the relicense is the maintainer's to
      do. blinkingLED was unlicensed and got MIT before bundling. LightHost is GPLv3 from JUCE, which
      is *not* ours to relicense — hence the refusal rather than a deferral.

    **The cross-cutting finding this item produced is engineering debt rather than an idea, so it
    stayed behind**: `ModulePermissions` cannot disclose synthetic input, and a SHIPPED module
    under-discloses because of it. That item is in [`../BACKLOG.md`](../BACKLOG.md) under "Module SDK
    follow-ups". The audio half of the same finding is already closed (`Microphone`, `SystemAudio`
    and `AgentTranscripts` were added for Remembrance and AgentFlow).

    **Reusable port recipe (for any same-stack tray app), validated by the blinkingLED port:** keep
    the dependency-free engine, discard the WinForms shell
    (`Program`/`Main`/single-instance/`NotifyIcon`/`OpenFileDialog`/`MessageBox`/custom Forms),
    rebuild the surface as tray items + a declarative pane, and be disciplined about tearing down
    OS-global state on ALC unload (hooks, Scroll-Lock state, audio devices).

19. **Remembrance: an "AI Doodle" recap image generated from the meeting transcript** (queued
    2026-09-22, owner's request, unscoped). After a meeting is transcribed, produce a landscape 16:9
    doodle-notes infographic summarising it, following the owner's existing prompt at
    `D:\.ai-work\Doodle Prompt for Chat GPT.txt` (whiteboard/sketchnote style, white background,
    black marker outlines, colourful rounded boxes, arrows, sticky notes, icons; a hard NO-PEOPLE
    rule with speakers represented by name labels, initials, role cards and ownership tags; strict
    source fidelity, invent nothing not in the notes).

    **Where it hooks, exactly.** The pipeline already ends record -> transcribe -> optional local
    summary, and the module announces "Summary ready." at `RemembranceModule.cs:291` after
    `WriteSummaryAsync` writes beside the transcript. A doodle step is a third artefact written to
    the same place and a fourth announcement. Nothing about the recording or transcription half
    needs to change, which is what makes this an idea rather than a rework.

    **The one piece that does not exist yet is image generation, and the choice of route is the
    whole decision.** The prompt was written for ChatGPT, which is a cloud service. Remembrance's
    entire design statement is that a meeting never leaves the box: a local Whisper, a loopback
    Ollama, and the module's own comment saying so. Three routes, with what each actually costs:

    - **Cloud image API.** Faithful to the prompt as written, and the only route that renders the
      aesthetic well today. It also sends a MEETING TRANSCRIPT to a third party, which is the exact
      thing the module promises not to do. That needs a new `ModulePermissions` bit, per-run consent
      rather than a one-time toggle, and it fires the consent re-prompt for every existing user. Not
      a small addition dressed as a setting.
    - **Local diffusion (SDXL / FLUX in the media venv).** Keeps the promise, and shares the single
      RTX 4090 with the owner's own work, so it inherits the ask-before-inference rule and has to say
      how long it holds the card. The real risk is not VRAM though: a doodle-notes infographic is
      mostly LETTERING, and legible hand-written section headers are the known weak point of
      diffusion models. The prompt itself bans "tiny unreadable microtext", so the format's success
      criterion is the thing the route is worst at. Worth one measured trial before any scoping.
    - **Deterministic render from a structured plan.** The local LLM emits a layout rather than a
      picture -- sections, ownership tags, arrows, and icons chosen from a fixed bundled set -- and
      the module draws it. Text is then real text and legible at any zoom, the no-people rule holds
      by construction because there are no people in the icon set, and it runs offline with no GPU
      at all. It gives up the hand-drawn feel, which is a real loss and may be most of the appeal.

    **A constraint worth stating before anyone picks a route:** the no-people rule cannot be checked
    by looking at the output of a generative route without a second model to detect people, so on
    routes 1 and 2 it is a request rather than a guarantee. Route 3 is the only one where it is
    enforceable. Same for source fidelity: "invent no statistics" is a prompt instruction to a
    generative model and a structural property of a deterministic renderer.

    **Not yet decided and not yet worth deciding:** whether this runs automatically after every
    meeting or on a pane button. Automatic is what was asked for; a button is the cheaper first
    version and answers whether the output is good enough to want automatically.

20. **AgentFlow presses yes in the Claude and Codex desktop apps** (deferred by the owner on
    2026-10-06, kept in the background; the hook route SHELVED by the owner the same day, see the
    last paragraph). AgentFlow already SEES both desktop apps: they write the
    same transcripts as their VS Code extensions, and since agentflow 1.5.0 a session waiting in
    either is announced by name, and an auto-approve user is told AgentFlow cannot answer it there.
    What it cannot do is press, because its press is CDP into a VS Code webview and neither desktop
    app offers CDP.

    **Why CDP is out, as read on 2026-10-06 (read-only; neither app was launched with a switch).**
    Claude desktop (MSIX package `Claude` 2.19675) exits at launch when a debugging or
    network-override switch is on its command line: `.vite/build/index.pre.js` in its `app.asar`
    writes "Claude: refusing to start" and calls `process.exit(1)` unless an Anthropic-signed
    `CLAUDE_CDP_AUTH` token is present, which `.vite/build/index.chunk-CN7MfJQH.js` checks, and its
    interface loads from claude.ai. Codex desktop (MSIX package `OpenAI.Codex` 26.930.7945) has no
    supported way to be given the switch: its manifest declares no execution alias for the app
    itself (only for the Chrome extension host and the command runner) and its `app.asar` carries no
    remote-debugging string at all. Both are deliberate vendor controls, and AgentFlow does not work
    around them.

    **The one press route left is Windows UI Automation, and it costs two things.** It is brittle
    against Claude desktop's remote claude.ai DOM: the accessible tree changes whenever that site
    deploys, with no app update a user could see or pin, so a selector table here would go stale
    silently, which is the failure BUG-006 to BUG-008 were about in the VS Code reader. And the
    managed workspace policy Codex caches on this box (`~/.codex/cloud-config-bundle-cache.json`, its
    "Critical - Always Deny" section) classes an automation or remote-control channel used to get
    around an approval control as Critical, always denied; a module that clicks Codex's approval
    button from outside the app is that channel.

    **The supported route, a `PermissionRequest` hook, was weighed and shelved (owner, 2026-10-06).**
    Both agents call such a hook just before a permission dialog would show, and an `allow` answer
    means the dialog never appears, so it is the vendor's own way to say yes. Both vendors document it
    for the desktop apps: Claude's desktop page ("Shared configuration", code.claude.com/docs/en/desktop)
    says hooks defined in settings apply to Desktop and the CLI alike, and Codex's hooks page
    (learn.chatgpt.com/docs/hooks) says hooks run in the desktop app, the IDE extension and
    app-server. The VS Code feature set maps onto it: approve-once is `allow` with nothing saved;
    "never answer a question" is a short tool list that leaves AskUserQuestion and plan approval out;
    Claude's "for all projects" row is `allow` plus the rule Claude itself suggests, sent back as
    `updatedPermissions` with destination `userSettings`; Codex's hook cannot save a grant (those
    fields "fail closed today"), so its "Allow similar commands" would be a prefix rule in a rules file.
    It was shelved on two findings:
    - A managed policy disables it, by design. `allowManagedHooksOnly` runs only the managed
      policy's own hooks, and Desktop's local Code sessions receive managed settings like the CLI
      does. The policy on the owner's work setup sets it, and the owner judges that typical of
      corporate deployments (a judgement, not a measurement). Where an organisation has decided only
      its hooks may answer prompts, nothing here should answer them, and that is also why UI
      Automation stays out for those users: it would be a way around that decision, not a better press.
    - Users without such a policy already have the switch. Both apps ship Bypass permissions and
      Auto mode, which remove prompts with nothing installed. The hook's one edge over them, that deny
      rules and questions still stop it, does not pay for a module that writes the user's Claude and
      Codex settings.

    The rules-only variant was built and measured first (ai-acolyte, branch
    `feature/permission-request-hook`, its `docs/engineering-record.md` "PermissionRequest hook:
    measured before it was built"): on this box it would have approved 0 of 1,076 visible dialogs. It
    stays parked on that branch, unmerged and not installed.

    **What would reopen it:** users without a managed policy asking for something narrower than
    Bypass permissions, or either vendor shipping a supported way for another program to answer a
    desktop approval. Until then 1.5.0's naming is the desktop feature: a blocked desktop session is
    announced by the app's name, and an auto-approve user is told it is theirs to answer.

## Render the last known update offers with no network

Filed 2026-09-17, when the machinery that would have served it was deleted.

`moduleUpdateOffers` and `companionUpdateStaleIds` recorded what the last update check found, and
`ModuleUpdateScan.Encode`/`Decode` serialised the first of them. Nothing read either: the Modules
pane and the Companions pane both re-fetch the catalog on open and render the live answer, so a
launch with no network shows nothing at all rather than "AI Brain 1.1.4 was available when we last
looked". All of it was removed rather than left as a write-only key with a codec and six assertions
behind it.

The feature it was presumably meant to support is real and small: on open, render the last known
offers immediately, marked with WHEN they were seen, then replace them when the fetch returns. Two
things to get right, both of which are why this is a feature rather than a bug fix:

- a cached offer that has since been installed must not keep appearing, so the render has to diff
  against what is installed NOW rather than trusting the cache
- the timestamp has to be shown, or a user reads a stale offer as a current one

The code that did the serialisation is in git history at the commit that removed it (search for
`ModuleUpdateScan.Encode`), which is cheaper than keeping a format alive against the possibility.


## A second instance should see the first's settings changes

Filed 2026-09-25, when the plumbing that pretended to do it was deleted.

`LocalData.ListenOnXMLChanged` and `ListenOnOptionsChanged` had empty bodies marked "Not implemented
in the portable build", and PORTABLE is defined in both configurations with `Portable/LocalData.cs`
the only `LocalData` compiled. So `StartUp.XmlFileChanged`, `OptionFileChanged`, the
`isRealoadingSettings` re-entrancy flag and `LocalData.LoadXML` had never run once. All of it is gone
rather than left looking like a working watcher.

The gap it left uncovered is real. `Program.TryAcquireInstanceSlot` deliberately allows two
concurrent instances, so changing the volume in instance A leaves instance B showing the old value
until it restarts. Nothing is corrupted -- `MergeChangedFields` only writes back fields the process
itself changed, so B does not stamp its stale copy over A's edit -- but the two disagree on screen.

What a real implementation has to get right, and why it is a feature rather than a bug fix:

- the reload has to be re-entrant-safe against the instance's OWN writes, which is what the deleted
  `isRealoadingSettings` flag was reaching for and never got to prove
- `FileSystemWatcher` fires several times for one logical save, and `AtomicFile` writes through a
  temp file plus `File.Replace`, so the events do not map one-to-one onto changes
- reloading the pet XML tears down and rebuilds every pet on screen, so a naive watcher turns a
  settings edit in one window into pets blinking out and back in the other

The deleted code is in git history at the commit that removed it (search for `ListenOnXMLChanged`).


## A companion should be able to WALK between monitors

Moved here from `BACKLOG.md` on 2026-09-27, when the other three parts of that entry turned out to
be done or decided. This is the part that remains, and it is a feature rather than debt: nothing is
broken today, the behaviour simply does not exist.

The owner asked for it directly -- *"if not bound then a companion should absolutely be able to
traverse monitors"*. What exists instead is RELOCATION, four ways (respawn re-roll, drag-and-drop,
fullscreen stand-down, window-follow), all of which teleport or re-home rather than walk.

**The shape that fits this code**, from the 2026-09-22 scoping that corrected two earlier wrong
blockers: an adjacency function in `DesktopGeometry` beside `ChooseRelocationTarget` -- pure
rectangle geometry, trivially testable -- then hand off at `FormCompanion.cs:1077` / `:1128` instead
of turning at the border: set `DisplayIndex`, call `UpdateValues`, translate the position.

Two things NOT in the way, both previously named as blockers and both refuted:

- The per-tick clamp is not a containment invariant. It allows 8192px of slack on every side and its
  own comment calls it an integer-overflow guard. The real containment is those two `if` statements.
- The pet-XML contract is not a blocker. `screenW`/`screenH`/`areaW`/`areaH` are already resolved PER
  MONITOR from `Screen.AllScreens[screenIndex]`, and `UpdateValues(DisplayIndex)` re-parses them
  whenever a pet is re-homed -- which `EndDrag` and `RelocateToDisplay` already do. A
  handoff-at-the-boundary design needs no ABI change and no second coordinate system.

The genuine hazards are the geometry ones: on this box 3440x1440 sits beside 2560x1080, so the
shorter screen's floor is 360px above its neighbour's and the union is not a rectangle -- a companion
crossing at floor level walks into empty space. That resolves by falling, and `AnimationFall` already
exists. Handing off mid-animation across a DPI change is the second.

