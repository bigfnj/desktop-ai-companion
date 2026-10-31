# Blocked — items that cannot be actioned from here

Every item below names its blocker on its own line. None of them is waiting on a decision about what
to build or how; each is waiting on an environment, an account action, or an eyeball that this
machine and this session cannot supply. They are filed away from
[`../BACKLOG.md`](../BACKLOG.md) so the backlog reads as work that can actually be picked up, and
listed here rather than deleted so none of them is rediscovered from scratch.

**An item leaves when its blocker goes, and the blocker is a claim to re-check rather than trust.**
T2 / T49 (the MSI upgrade path and the installed build's About window) moved back to
[`../BACKLOG.md`](../BACKLOG.md) on 2026-09-17: it needed "a real reinstall of the MSI over a previous
install", and both halves of that now exist here — the app is installed at 1.1.4 and WiX 5.0.2 is a
global dotnet tool. Re-measure the line under each heading below before assuming it still holds.

---

## T25 — Remembrance: a real recording smoke test

**Blocker: needs a machine's LOCAL CONSOLE. A Remote Desktop session presents no mic or speakers, so
capture cannot be tested under RDP at all.**

> **Still to verify:** a real recording smoke test on a machine's LOCAL CONSOLE — a Remote Desktop session
> presents no mic/speakers, so capture cannot be tested under RDP (the user is testing on separate
> workstations). The live WASAPI capture + mix remain build-verified only; the download, the whisper-cli run
> and the summary map-reduce are all now verified live on the dev box.

Full entry: [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md).

---

## T48 — 10 original-author commits survive in immutable GitHub refs

**Blocker: needs a GitHub Support "remove sensitive data" request, or deleting and recreating the
repository. A force-push cannot reach them.**

> **Residual:** GitHub keeps the original commits in immutable `refs/pull/*/head` refs that a
> force-push can't remove — fully purging them needs a GitHub Support "remove sensitive data" request
> (or delete+recreate the repo). Left as a known, low-exposure residual.

`master`, the release tags and all tracked file content are already clean: `git filter-repo
--mailmap` mapped the ten day-one commits onto the project identity and the HEAD tree stayed
byte-identical. Full entry: [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md).

---

## T58 — port IdleLauncherTray as a module

**Blocker: needs a GPLv2 → MIT relicense. The maintainer owns the copyright, so this is theirs to do
and nobody else's, but it has not been done and no engineering should start before it is.**

> **Biggest blocker is licensing: it's GPLv2, the companion is MIT — relicense (bigfnj-owned) before
> any engineering.**

The technical assessment is done and favourable — the idle engine is dependency-free P/Invoke and
drops straight into a module timer — and it is recorded in [`IDEAS.md`](IDEAS.md), idea 18
(it was in `../BACKLOG.md` until 2026-09-17), including the one piece of real technical care (the
low-level hook must be `UnhookWindowsHookEx`'d in `Shutdown()` or an ALC unload leaks a dangling
hook). This is the last of
the three candidate ports still open: blinkingLED was ported and ships as `modules/BlinkingLed/`, and
LightHost was refused (the reasoning is in [`DESIGN-REGISTER.md`](DESIGN-REGISTER.md)). Only the
licence blocks it.

---

## T36 — `ChaseMouse` / `ChaseMouse2`: a decision, not work

**Blocker: a judgement call only the maintainer can make, and it is now live — Phases A and B have
shipped, so somebody has to watch a companion and decide whether pointer-aware gaze already scratches
the itch. If it does, this stays deferred permanently and the engine change is never written.**

The only item on the Shimeji phase list that was never built. Kept verbatim, because the cost
estimate is the part that matters if the answer comes back "not enough":

- ⬜ **NEXT, and the only thing left in this section — `ChaseMouse` / `ChaseMouse2` / マウスの周りに集まる. ~14 actions, 12 companions.
  This is the "companions follow your mouse" behaviour people remember, and it is the one thing here that a
  conditional transition CANNOT express.** The format gives each animation a fixed start/end velocity and
  interpolates between them; chasing a cursor means recomputing velocity every tick toward a target that
  keeps moving. That is a new MOVEMENT MODE in the engine (a `seekCursor` sequence action the engine
  implements by overriding per-tick velocity), and it has to behave against gravity, the border/turn logic,
  an active drag, and multi-monitor coordinates. Closer to Phase E in risk than to A or B despite the small
  count — the cost is in the interactions, not the lines. Deliberately deferred: do A and B first and see
  whether pointer-aware gaze already scratches the itch.

  **A and B have now shipped, so that question is live.** Answer it by watching a companion before building
  anything: a gaze aims on entry and re-enters every few seconds, so the companion glances at you rather than
  tracking you. If that reads as enough, this stays deferred permanently.

  **When B or the chase is built:** `SafeExpression` (which already resolves screenW / imageX / random) is
  the natural home for cursorX/cursorY/selfX/selfY, and a `<next>` carrying a condition is the natural gate.
  That machinery exists. The per-tick movement mode does not.

---

## T52 / T53 — two AI-persona changes never observed running

**Blocker: needs a live model run to eyeball. Both were reasoned about and gated offline only.**

**T52, the `dolphin3` profanity requirement.** The Samuel instruction was rewritten from an abstract
style adjective into a concrete, checkable requirement, because small local models under-follow
abstract style asks:

> **⚠ Still needs a live smoke test** with dolphin3 to confirm the concrete-requirement phrasing
> actually lands more profanity than the old abstract phrasing did — not yet observed running.

**T53, the Jules Winnfield rename.** The same `samuel` id was re-pointed from a generic "Samuel L.
Jackson" descriptor at a specific, heavily documented character, on the reasoning that a character is
a much sharper style-transfer target for an LLM than an actor descriptor:

> **⚠ Not yet observed live** — same open gap as the two entries above, reasoned + gated offline only;
> this is the one to actually eyeball next.

Note the walk-back these two straddle: T52 made profanity mandatory ("a remark with zero profanity in
it has failed") and T53 deliberately loosened it again to "your default reflex, not a checkbox to
tick". So a live run has to judge the second phrasing, not the first. Both entries in full, with the
disposition merge that later absorbed them: [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md).


## 17 animation names remain unclassifiable

**Blocker: source `.conf` files that do not exist on this machine and cannot be committed here.**

Moved from [`../BACKLOG.md`](../BACKLOG.md) on 2026-09-27. It is not waiting on a decision or on
effort; it is waiting on data. 13 of the 17 names are absent from the 2778-archive harvest, and 4 are
names the corpus disputes with itself, so settling them needs original Shimeji confs that are
copyrighted and deliberately not in this repo.

It stayed in the backlog for weeks reading as actionable work. It is not, and its own entry already
said so: *"NO CLOSES-WHEN, deliberately: nothing in this repo can answer it."* That is the definition
of this file rather than that one.

Worth keeping the history, because the entry is a small case study in criteria that lie. It carried
`grep-present tools/ShimejiConvert/Program.cs "SkinLayout census"` from the day it was filed -- a
string that has never appeared in that file, or anywhere in the repo except the entry itself -- so
the criterion could not fire whatever happened to the code. Replacing it with a grep that DID match
only moved the lie: the gate immediately reported the item closeable while all 17 names were still
unresolved. An item that cannot be machine-checked has to say so.

**It leaves when the blocker does:** a source corpus that names the 17, with a Type for each.


## AgentFlow: default-mode precision is unmeasured

**Blocker: transcripts that do not exist yet, and only the owner working normally can create them.**

Moved from [`../BACKLOG.md`](../BACKLOG.md) on 2026-09-27. Recall is settled at **83% (25/30)** against
calls a permission rule actually blocked, under the semantics the shipped detector uses. (The 93% (28/30)
that stood here until 2026-09-30 was the research harness's own predictor, which counted three
ExitPlanMode positives the module reports as Undecidable; F018 corrected it, and
[`agentflow/README.md`](agentflow/README.md) carries the table and the reasoning, including that the figure
is a function of the rule files at run time: a 2026-09-30 rerun against the current `settings.json` gave
21/30.) Precision in `default` mode cannot be computed here: the
120 transcripts on this machine contain **zero** rule-caused denials in that mode, because this
machine runs `auto`.

No amount of engineering closes it. The measurement needs real prompts, in `default`, blocked past
the threshold, and the only way to produce those is to work in that mode for a while. The tooling is
already waiting: `agentflow_join.py` reports the precision/recall split itself once the data is
there.

Worth being clear about what this is NOT. It is not a gap in the approver's test coverage -- as of
agentflow 1.4.9 a loopback fake CDP server drives `Sweep` end to end, including the handshake and the
whole-pass outcome. This is a question about how often the thing is RIGHT in the field, which no
fixture can answer, because a fixture only ever returns what it was told to.

**It leaves when the blocker does:** an hour or so of ordinary work in `default` mode, then a rerun
of `agentflow_join.py`.

---

## N-installer-close-scope-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the owner at the GUI on the installed build.**

What clears it: Walk SMOKETEST.md sections A to E: launch and baseline (2 min), motion (5 min), multiple monitors (2+ screens), fullscreen with a real game, speech (2 min).

The entry as it stood:

**N-installer-close-scope-01** [installer/live validation] Installation-scoped shutdown is implemented as a native Binary action with exact registered per-user roots, caller/process identity checks, complete prevalidation and retained-handle fallback. Native policy/owned-process tests pass 78 checks; four safety witnesses fail in their intended new test and restore exactly. Built-MSI surface checks and actual isolated rollback, upgrade and reinstall preserve foreign hosts and settings. Keep this item open until strict repair/uninstall receipt checks and the complete production-host lifecycle pass. The first upgrade from an older cached MSI still executes its historical basename-wide action; require exclusive companion use for that entire transition after mutation hosts stop. A snapshot foreign-process refusal cannot remove the later-launch race. No real historical upgrade or installed-host shutdown was performed during parallel Guard validation.

---

## N-agentbroker-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the owner with a normally opened Codex chat in VS Code and a live Claude session.**

What clears it: Install AgentBroker 1.5.0 or later with Codex peer messages on. Open Codex in VS Code normally, in its original conversation (no dev probe, no reload), and check that its `codex:<thread>` row has Receive and WakeIdle. Send a marker from a Claude session and confirm it lands in that same view, is acknowledged and answered. Repeat while Codex is mid-turn (expect a `native_context_busy` deferral, then delivery in order once idle), while it waits on an approval prompt, and after an interrupt. Record each receipt reason. The LAN half is N-agentbroker-two-pc-01.

The entry as it stood:

**N-agentbroker-01** [live acceptance] Complete the remaining native-client acceptance matrix: current-package Codex external peer input, the exact normally opened Codex VS Code test with verified native ownership, live busy-to-idle ordering, and approval/interruption behavior. The owner confirmed Claude VS Code's peer marker reply in its original conversation. Fresh public Code profile verification on 2026-10-22 passed. One exact-target metadata-only request observed one frame over two seconds after initialization, with zero exact-target advertisements and zero matching source/status counts. A separate initialized owner probe, with a 12-second budget, completed with `ownerFound:false` after 5,771 ms. No input, configuration change, restart or replacement conversation was used in these fresh checks. A later development-only GUI probe passed exact custom-editor tab creation, but the visible message load failed with an active-writer conflict and empty body; loaded-thread content and native ownership remained unproven. The public Code profile passed with two running engines. The separate post-open metadata check again observed one frame and no exact-target/source/status advertisements; its initialized 12-second owner probe completed with `ownerFound:false` after 8,112 ms. No model input, process stop, existing-window reload or relay was used. The duplicate view's ordinary GUI resume conflict does not establish why the original view lacked a discoverable native owner; see the bounded source references in the compatibility document. An earlier unrelated missing-parent diagnostic cleared when a PID/start-time-only check found the previously observed process no longer present; neither Codex engine was stopped by these read-only checks, and neither its disappearance nor the missing owner's cause is established. Recipient provenance requires the separate design work in N-agentbroker-code-owner-provenance-01. Claude's supported delegated user allowance for the exact loopback URL was explicitly approved; canonical HTTP registration and the named CLI connection check passed, and the owner confirmed Connected with tools available in the intended VS Code client. Organization policy, locks and denials were preserved. Retest two physical workstations for multicast discovery, pairing, bidirectional delivery, restart and revocation after installing the pairing collision/deadline correction on both. A read-only pinned HTTPS public-info probe reached the discovered physical peer, but that does not prove mutual trust or message delivery. Automated three-broker HTTPS, MCP, queue, identity and loader checks on one workstation do not close the remaining external gates. See `docs/agentbroker-client-compatibility.md`.

---

## N-agentbroker-repin-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the owner's source review of each drafted pin and a live acceptance after publish.**

What clears it: Run `pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerRepin.ps1 -Vendor Codex -OutputDirectory <new empty dir>`; review the source of each draft (Codex extension 26.1021.22027, Codex desktop 26.1021.3705.0 if installed, and the VS Code router 1.144.0, which is now off its pin too); do the live acceptance; append the pins. The Claude half is done: Claude Code 2.1.310 and 2.1.312 are pinned. Update-tolerant delivery (on by default since AgentBroker 1.5.0) keeps updated Codex builds working meanwhile.

The entry as it stood:

**N-agentbroker-repin-01** [tooling/compatibility] The re-pin assistant exists (`tools/agentbroker/Test-AgentBrokerRepin.ps1`, lane broker-b): read-only, it verifies each installed image's expected-publisher signature, runs the offline public-source checks `docs/agentbroker-client-compatibility.md` names, and drafts the exact replacement pin with versioned evidence. It never applies a pin, and a draft is not an approved pin. Left, by the owner's decision of 2026-10-27 to re-pin later with their live check: re-pin the installed builds that are off their exact pins (on 2026-10-27 the Codex extension 26.1021.22027, the Codex desktop 26.1021.3705.0 and Claude Code for VS Code 2.1.310), each after a source review of the draft and the owner's live acceptance. For Codex, exact pins become the optional strict mode once N-agentbroker-update-tolerant-trust-01 makes publisher trust the default.

---

## N-agentbroker-two-pc-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs two physical PCs on the same build.**

What clears it: Pair them. On each, register a fresh discoverable generic inbox; send a synthetic message A to B, B acknowledges and sends a correlated reply, A acknowledges; then B to A. Repeat after a worker restart, an app restart, a reboot and a DHCP renew, confirming trust persists. Revoke and confirm sends are refused. `tools/agentbroker/Test-AgentBrokerPeerAuthentication.ps1 -WorkerProcessId <pid>` reads the result.

The entry as it stood:

**N-agentbroker-two-pc-01** [live acceptance, continuation of N-agentbroker-01] The paired-broker list, persistent saved-trust confirmation and verified endpoint recovery are implemented. A physical peer's pinned public HTTPS probe passed in one direction after its trusted home interface changed from Public to Private. The later 2026-10-26 explicit authenticated directory check passed with a valid empty response after the reported peer reboot and a local broker update. That confirms the saved mutual identities for that request direction; the older merged list returning no remote sessions did not establish authentication. Remote session publication remains to be established. Continue with fresh discoverable generic MCP test inboxes on two workstations, a synthetic message, explicit acknowledgements and a correlated reply in each direction. Recheck reverse ingress and broader physical worker/application restart, reboot and DHCP recovery while preserving trust. Native wake and recipient ownership/permissions remain separate acceptance.

---

## N-agentbroker-client-onboarding-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs fresh ordinary client sessions started by the owner.**

What clears it: Start fresh Claude Code CLI, Claude Code for VS Code and Codex sessions; connect each through the AgentBroker pane; check the six states; register and renew an inbox; exchange a message and an acknowledged reply; restart and repeat. The Codex step is probably blocked by N-bus-05.

The entry as it stood:

**N-agentbroker-client-onboarding-01** [setup, requested 2026-10-26] Per-client setup detection and a setup action for Claude Code CLI, VS Code and Codex are implemented, with separate states for broker running, client configured, endpoint reachable, tools available, inbox registered and native receive (`modules/AgentBroker/AgentBrokerModule.Clients.cs`, lane broker-a), and AgentBroker 1.4.0 added `whoami` and the first-contact card. Left: the live acceptance. Start a fresh ordinary client session, connect through the supported configuration, register and renew its inbox, exchange a broker message and an acknowledged reply, and repeat after a restart, without relying on an earlier conversation or private handoff files.

---

## N-agentbroker-claude-channel-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs one CLI session the owner consents to (research-preview channels).**

What clears it: `claude mcp add --transport stdio --scope user agentbroker-channel -- "<module>\worker\AgentBroker.Worker.exe" --mcp-channel --data "<data dir>"`, then `claude --dangerously-load-development-channels server:agentbroker-channel`; accept the warning; send from a peer while idle (it should wake), use `acknowledge` then `reply`, then send while busy. A Team or Enterprise organisation must allow channels (`channelsEnabled`), which may need the admin.

The entry as it stood:

**N-agentbroker-claude-channel-01** **Owner decision 2026-10-29: evaluate it as an opt-in bridge,** tested in one dedicated session the owner consents to. The bridge is implemented: `AgentBroker.Worker --mcp-channel` (`tools/AgentBroker.Worker/McpChannelBridge.cs`) pushes each new message as `notifications/claude/channel` per the vendor's [channels contract](https://code.claude.com/docs/en/channels-reference), offers `acknowledge` (the explicit recipient receipt; a push alone is not Delivered) and `reply`, and never declares `claude/channel/permission`. Left: the evaluation in that session (custom channels are a research preview and need `claude --dangerously-load-development-channels`): wake on a new message, reply, and behaviour while busy. Do not infer VS Code or desktop-host support from the CLI.

---

## N-agentbroker-uncertain-parked-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: recurrence watch: needs the next Uncertain result to a live Codex chat.**

What clears it: Record the `native_uncertain_*` reason and what the chat showed; if it is a timeout on a parked chat, add a deferral before submitting while the row is not Idle.

The entry as it stood:

**N-agentbroker-uncertain-parked-01** [reliability, observed 2026-10-28] Two messages to a live Codex chat (`codex:01a18357`) ended `Uncertain` while its activity was `Unknown`, probably because the chat was parked on an approval prompt. `Uncertain` is never retried and produces no delivery notice. 1.4.0 keeps the library's typed cause in the receipt reason (`native_uncertain_timeout`, `native_uncertain_native_error`, `native_uncertain_owner_changed`, `native_uncertain_connection`; `native_submission_uncertain` otherwise), so the next occurrence names its cause. Then: if a parked thread times out, defer before submitting while the directory cannot show it idle.

---

## N-bus-05 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the owner or the organisation's Codex admin.**

**Re-measured 2026-10-29, later the same evening: on this workstation the allowlist did not block it.** The cached managed configuration does carry a deny-by-default MCP allowlist that does not name `desktop-ai-companion-agentbroker`, yet a Codex chat in VS Code connected to the broker as soon as the companion had restarted; its earlier `unknown MCP server` was N-agentbroker-client-reconnect-01 (the chat started while the broker was down). Before asking the admin, re-check the 2026-10-27 observation, which was made with `codex exec`, on the client that matters.

What clears it: Ask the admin whether the managed requirements still deny MCP servers by default (by name and identity); if so, request an entry for `desktop-ai-companion-agentbroker` with its exact identity (`http://127.0.0.1:7432/mcp`, or the stdio worker command).

The entry as it stood:

**N-bus-05** [info/policy, needs the owner or an admin] On 2026-10-27 the organisation's Codex requirements were a deny-by-default MCP allowlist (name and identity), so AgentBroker as an MCP server for Codex needed an admin. Whether that still holds is not something the repository can show; ask the owner or the admin before relying on it either way.

---

## N-toolchain-wix-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: the owner accepts the WiX Open Source Maintenance Fee EULA (WiX 6 and later refuse every build until it is accepted, locally and in CI). Owner decision 2026-10-29: stay on WiX 5.0.2 for now.**

What clears it: Accept the EULA, then do the move as a release of its own; the entry lists every file. Files it does not name: `installer/build-installer.ps1:11,253` ("WiX 5.0.2 is required"), `installer/ScopedShutdown/MsiEntry.cpp:86` and `packaging/Test-MsiSurface.ps1:122` (`Wix4CloseApplication`).

The entry as it stood:

**N-toolchain-wix-01** **Owner decision 2026-10-29: a separate release,** so an installer problem cannot be confused with a host fix. [installer/toolchain] Move the MSI toolchain from WiX 5.0.2 to WiX 7.0.1 (the newest on
  nuget.org on 2026-10-27; 6.0.2 also exists). 5.0.2 has been locked since 1.0.0 (2026-09-03) by
  `packaging/wix-toolchain-lock.json`, which pins each package's SHA-256, and nothing records a reason to stay on it.
  The owner asked for it on the backlog on 2026-10-27.
  ⚠ WiX 6 and later refuse every build command until the Open Source Maintenance Fee EULA is accepted (error 7015;
  `--acceptEula wix7`, or the acceptance file under `~/.wix`), locally and in CI. The fee itself applies only to use
  "as part of revenue-generating activities" at US$10,000 or more of annual gross revenue
  (https://github.com/wixtoolset/wix/blob/main/OSMFEULA.txt). Accepting it is the owner's decision when this starts.
  The work: the lock's packages and SHA-256s (wix, the UI and Util extensions); the version checks at
  `packaging/Install-LockedWixToolchain.ps1` :223, :487 and :533 and `packaging/WixToolchainPolicy.ps1` :360 and :510;
  the toolchain paths at `.github/workflows/build.yml:272` and `release.yml:169`; the Readme's build prerequisites
  (:733, :745); the WiX 5 table names the MSI lifecycle test asserts (`Wix4CloseApplication`,
  `Wix4FailWhenDeferred_X64`, `tests/Test-IsolatedMsiLifecycle.ps1` :159 and :249); and the native
  installation-scoped shutdown, which replaced 5.0.2's CloseApps behaviour (`docs/installer-scoped-shutdown.md`
  :50-55), re-verified against the new Util extension's action order. Acceptance: the native installer checks, the
  isolated MSI lifecycle, and a real upgrade from a 5.0.2-built 1.7.x MSI to a 7.0.1-built one that exits 0 and keeps
  modules and settings, since that is the path users take. It does not touch the 1.7.0 Defender false positive, which
  is on the app's DLL, not the MSI.
  CLOSES-WHEN: grep-absent packaging/wix-toolchain-lock.json "5.0.2"

---

## N-agentflow-hosts-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs a live Codex session with a subagent left on an approval prompt (the 2026-10-06 corpus is gone).**

What clears it: Leave a Codex subagent waiting on an approval and read what AgentFlow reports; then decide whether to skip subagent rollouts.

The entry as it stood:

**N-agentflow-hosts-01** [low/unmeasured] AgentFlow watches every Codex SUBAGENT thread as a session of its own, while it skips Claude's (`modules/AgentFlow/AgentFlowModule.cs`, `ScanRoot` is handed no skip directory for the Codex root). Measured 2026-10-06 on this box: 60 of the 71 rollouts under `~/.codex/sessions` are subagent threads (`session_meta.source` is an object naming a spawn, `thread_source` "subagent"), they sit in the same date folders as the sessions that spawned them, and 52 of them run `on-request`, so a subagent call stalled past 180 s reads as BLOCKED and is announced as "Codex in VS Code: ...". Whether such a stall is a prompt the user can answer, or a duplicate of the parent's, is unmeasured; Claude's side was decided the other way for a measured reason (N-agentflow-02: "a blocked SUBAGENT is not a prompt the user can answer"). Found by lane feature/agentflow-hosts while reading `source` for the app name; outside that lane's brief. The measurement that would settle it: a Codex session with a subagent left on an approval prompt, and which rollout's call stays unpaired. ⚠ It has to be live now: on 2026-10-21 `~/.codex/sessions` held 10 rollouts, the oldest from 2026-10-14 and none of them a subagent thread, where 2026-10-06 counted 71 and 2026-10-20 counted 34 October rollouts, so the corpus this was measured on is gone (why was not looked into).

---

## N-agentflow-codex-pause-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: upstream: no 300-minute window reaches a Codex rollout.**

What clears it: Once Codex exposes it, the owner decides a Codex input route and a resume mechanism; SMOKETEST H15e on the other workstation.

The entry as it stood:

**N-agentflow-codex-pause-01** [low/blocked-upstream] Pause each Codex session on its OWN usage limit, as the Claude watch pauses Claude Code panels (the owner's request of 2026-10-20). AgentFlow 1.10.0 is the scaffolding: it reads each live Codex session's `rate_limits` from its rollout (`modules/AgentFlow/CodexLimit.cs`), shows them on the Status card's Codex usage row, and carries the decision (`CodexLimits.OverThreshold`, a session's own five-hour window past the threshold with its reset known), which nothing acts on. Three things stand between that and a pause. (1) A five-hour window to hold the threshold against: measured over this box's 34 October rollouts, the Business workspace sends none at all and the personal Pro account sends only a weekly one (10080 minutes, in the `primary` slot). (2) A way to put the request into a Codex session: `SendPause` refuses every target that is not a Claude Code panel, deliberately, because Escape in the Codex panel is Deny, and the desktop app, where the Pro account runs, is not reachable at all. (3) A resume: the Claude message asks for CronCreate, a Claude Code tool. What would start it: a rollout carrying a 300-minute window (the Codex usage row will show "session N %"), then the owner's call on (2). Owner's live check of the reading on the other workstation with the Pro account: SMOKETEST H15e. See `#### feature/agentflow-codex-limits` in [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md).

---

## N-agentflow-claude-queue-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs a live Claude Code panel in VS Code asking several times in a row.**

What clears it: Run `docs/agentflow/agentflow_codex_card_count.py` against `Anthropic.claude-code` / `permissionRequestContainer` and include Claude in `FollowsQueue` if it shows the Codex shape.

The entry as it stood:

**N-agentflow-claude-queue-01** [low/unmeasured] A Claude Code panel is still read once a sweep: since 1.12.0 a press in a Codex panel is followed by re-reads of it until its queue is drained, and a Claude Code panel's is not (`modules/AgentFlow/CdpApprover.cs`, `FollowsQueue`). Left out because nothing here measured it: whether a Claude Code panel queues several requests behind one card, and how soon the next mounts after a press. Its cards mount disabled and enable 500 ms later (webview/index.js 2.1.301), inside the follow's 800 ms window, so turning it on would be `FollowsQueue` answering true for Claude once a live count says the shape is the Codex one. The measurement: `docs/agentflow/agentflow_codex_card_count.py` pointed at `extensionId=Anthropic.claude-code` and `permissionRequestContainer`, with a session asking several times in a row. Found by lane agentflow/codex-card-queue.

---

## N-agentflow-codex-queue-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the same live measurement as N-agentflow-hosts-01.**

What clears it: Count the approval cards in one Codex panel during a burst of prompts with `docs/agentflow/agentflow_codex_card_count.py`.

The entry as it stood:

**N-agentflow-codex-queue-01** [low/unmeasured] What puts a SECOND approval card in one Codex panel is unknown. Measured 2026-10-21 (`docs/agentflow/agentflow_codex_card_count.py --structure`): both cards carry `data-codex-approval-surface` and sit five levels under `data-codex-composer-root` / `data-composer-placement`, the same structure, and the second stood while the first slot refilled (74 s in the first count; ten such cards in a 25-minute watch, the longest about three minutes). The bundle (openai.chatgpt 26.1007.21434) shows one approval per thread (`sLt` returns one request, `vLt` the first exec or patch request of a turn), so the likeliest source is a second thread, a subagent's, surfaced in the parent's composer, which is the case N-agentflow-hosts-01 needs live. 1.12.0 presses both whatever they are, each through the classifier; what is open is only whose request the second one is. Found by lane agentflow/codex-card-queue.

---

## N-cli-token-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs a real sign-in token from another (personal) account on a machine whose organisation requires remote managed settings.**

What clears it: Save that token in AI Brain's CLI card, Validate, and record whether Claude Code answers or refuses.

The entry as it stood:

**N-cli-token-01** [medium/unverified] Whether a sign-in token from ANOTHER account works on a machine whose organisation requires remote managed settings is unmeasured (`shared/CodingAgentCli/CodingAgentCli.cs`, the saved sign-in token section). On the maintainer's machine, whose Claude Code is signed into an Enterprise organisation that requires them, a call with a FAKE token failed on "Your organization requires remote managed settings to load, but they could not be loaded. Run `claude auth login` to re-authenticate" before it reached the model. A token from the same organisation should load them; one from a personal plan may be refused the same way, and the card then says the token was refused and quotes Claude Code's words. Nothing here steps around the organisation's requirement, by design (`docs/DESIGN-REGISTER.md`, feature/cli-token-and-locate). Measure with a real token from another account before anyone is told it works there. Found by lane feature/cli-token-and-locate.

---

## N-cli-locate-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the workstation that reported BUG-015.**

What clears it: On that workstation read how Claude Code was installed (`where claude`, the install folder) and compare with the three installs the fix covers.

The entry as it stood:

**N-cli-locate-01** [low/unverified, needs the reporting workstation] How Claude Code was installed on the workstation that reported BUG-015 was not read from it; the fix covers the three installs that produce that message (`docs/ISSUES-post-1.0.0.md`, BUG-015, "Not verified here"). The desktop-app half is closed (2026-10-29): the installed Claude desktop app is the Store package Claude 4.29.0.0 under `C:\Program Files\WindowsApps`, whose `app\claude.exe` is the desktop application (Anthropic-signed), with no Claude Code CLI found in the package or its LocalCache; its execution alias is `claude-desktop.exe`, so neither the locator's search roots nor a PATH lookup of `claude.exe` can reach it.

---

## N-blinkingled-resume-02 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the owner's go to press Caps Lock in their session.**

What clears it: A throwaway process presses Caps Lock with SendInput and reads `Control.IsKeyLocked` for a minute; if a read lags, stop the blinker only after two Caps-on reads in a row.

The entry as it stood:

**N-blinkingled-resume-02** [low/unmeasured] Whether the companion's Caps Lock read can be stale, so that a tick stops the blinker while Caps Lock is off, is still unmeasured (`modules/BlinkingLed/engine/ScrollLockBlinker.cs:449`, `IsCapsLockOn`, `Control.IsKeyLocked` on the companion's UI thread, read at every cadence tick from :362). The F113 entry under `#### fix/blinkingled` in `docs/DESIGN-REGISTER.md` named the same question for the Scroll Lock reads. The diagnosis of 2026-10-14 (label `verify:windows`) argued that it is unlikely, from a secondhand quote of a Windows source comment saying that a thread which polls the key state without receiving hardware input is resynced, and from the primary companion window taking activation on a poke, and did not measure it, because measuring means pressing a toggle key in the owner's session. Since blinkingled 1.0.7 such a stop lasts until the next start with "Resume on application restart" ticked (N-blinkingled-resume-01), and is still saved with it unticked. Proposed, with the owner's go because it presses a key: in a throwaway process with its own message queue and no foreground window, toggle Caps Lock with SendInput, read `Control.IsKeyLocked(Keys.CapsLock)` from a WinForms timer for a minute, then press it back; if a read lags, have the tick stop only after two reads in a row find Caps Lock on. Filed by lane feature/blinkingled-resume.

---

## N-tray-questions-03 (moved from BACKLOG.md 2026-10-29)

**Blocker: needs the owner at the GUI.**

What clears it: Turn Windows Do Not Disturb on, ask a test question from the debug window, and read the `[question] held` line in diagnostics.log. If it does not hold, the pane wording or the signal changes.

The entry as it stood:

**N-tray-questions-03** [info/unmeasured] Whether Windows 11's Do Not Disturb reaches `SHQueryUserNotificationState` at all is unverified, so the question card may not hold for it (the card holds for presenting, fullscreen, locked and QUNS_QUIET_TIME). `modules/Reminder/PresentationState.cs` calls QUIET_TIME "Do Not Disturb", which it is not: QUIET_TIME is Windows' post-setup quiet hour. Turn Do Not Disturb on, ask a test question from the debug window, and read the `[question] held` line. Found by lane feature/tray-questions.

---

## N-agentbroker-refusal-transience-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: half 2 is fixed in AgentBroker 1.6.0 (e02cf5a, 0c90d91: a Codex check the broker could not complete defers as native_verification_unavailable). Half 1 needs a router build that matches a pin, and none is installed on this workstation: VS Code's openai.chatgpt is 26.1021.22027 (pin 26.1007.21434), Code.exe 1.144.0 (pins 1.143.0, 1.143.1), Codex desktop 26.1023.6123.0 (pins 26.1016.6573.0, 26.1016.8622.0)**

What clears it: install the pinned builds, or re-pin to the installed ones (N-agentbroker-repin-01). Then re-read the router in that build as the 2026-10-29 audit did (extension.js handleRequest, findClientForRequest, sendClientDiscoveryRequest, forwardRequest; the same class in Codex desktop's app.asar): the audit of the installed, unpinned builds found handleRequest the only producer of no-client-found, inside one catch that covers the discovery and forwardRequest. If no-client-found can only be answered before the request frame is written, make it defer like native_owner_unavailable (TRANSIENT_OWNER_DEFERS is the template); if forwardRequest can throw after writing, the refusal stays final and this entry closes as DECLINED with that reading.

The entry as it stood:

**N-agentbroker-refusal-transience-01** [delivery policy, observed 2026-10-28] Two messages from a live Claude session to a live Codex session were refused as final: `native_admission_refused` and `unsupported_capability`; the same session accepted other messages minutes later. 1.4.0 fixed one cause: a Codex owner missing at the pre-write rediscovery (`owner_unavailable` before any turn frame or receipt; discovery writes only a control frame, `DesktopAdapter.cs`) now defers as `native_owner_unavailable` and retries on the external-peer path (TRANSIENT_OWNER_DEFERS, TRANSIENT_OWNER_REASON). Left: the `no-client-found` answer to the start-turn request stays a final refusal because a frame was written, and update-tolerant `unsupported_capability` refuses on a momentary capability check (`CodexNativeAdapter.cs`, `Refusal`). Decide whether either can be proved unsubmitted; keep a definite refusal final when the recipient's own policy refused it.

---

## N-agentbroker-code-owner-provenance-01 (moved from BACKLOG.md 2026-10-29; report-only half built in AgentBroker 1.7.0)

**Blocker: an owner decision on ENFORCING the engine binding, to be taken on report-only data. The owner chose report-only first on 2026-10-30, and AgentBroker 1.7.0 builds it (lane feature/broker-163: 25a0c65, then d9c2967 after an independent review): every Codex row in list_sessions, the sender's receipt (the row's latest observation at the attempt) and a Codex line in client status say bound (code_engine, desktop_engine) or unbound with a reason (lock_path_reparse, lock_missing, no_holder, multiple_holders, foreign_holder, start_time_mismatch, query_failed). Nothing is refused and no address changes. The observation runs read-only through Restart Manager in the background after each discovery pass, within a 10-second budget. Its first live reads (2026-10-30) found 13 thread locks, each with exactly one holder (11 code_engine, 2 desktop_engine), in about 1.6 s. The view hop (which webview of a window answered) stays unverifiable: a vendor limit**

What clears it: run 1.7.0 for a while, then read the engine lines: how many rows read unbound, and for which reasons. If unbound appears only where a delivery would rightly fail, the owner decides whether to enforce: refuse a Codex delivery unless exactly one lock holder matches the pinned Code-owned or desktop engine by PID and start time, re-checked before thread-follower-start-turn and after (DesktopOwnerBinding takes the holder's PID and start time and the incarnation derives from them, not from the router's ServerPid; a mutation case per refusal). Fix N-broker-163-02 first (the binding reads Codex's default home only, so a CODEX_HOME engine would be refused). Record the view hop as a vendor limit: the codex-ipc router would have to stamp each pipe client's verified PID (GetNamedPipeClientProcessId) on what it forwards and reject a response not from the target's socket.

Correction (N-broker-163-05, 2026-10-30): the 2026-10-29 audit (D:\.ai-work\worktrees\_temp\provenance\FINDINGS.md, outside the repo) allowed for stale lock files left behind by exited engines. The live reads found none: every lock was held by a running engine. A stale lock would read unbound (no_holder) in the report, and enforcement would refuse that thread, which is the intended answer.

The entry as it stood on 2026-10-29:

**Blocker: an owner decision on a change to how the broker verifies a recipient. The 2026-10-29 read-only audit of the installed Codex builds (findings outside the repo: D:\.ai-work\worktrees\_temp\provenance\FINDINGS.md) found nothing a view, the router or a discovery answer carries that ties a router client to a process (handledByClientId is the responder's own claim, forwarded by requestId alone), and one identity that does tie a thread to an engine: the engine's writer lock, %USERPROFILE%\.codex\thread-writer-locks\<threadId>.lock. A live read-only Restart Manager query the same evening confirmed it: each open thread's lock had exactly one holder, a codex.exe with its PID and start time (01a1855c and 01a17c47: pid 16740, parent Code.exe; 01a1869b: pid 20508, parent ChatGPT.exe). The view hop (which webview of a window answered) stays unverifiable: a vendor limit**

What clears it: decide whether AgentBroker should ENFORCE the engine binding (refuse a Codex delivery unless exactly one lock holder matches a pinned Code-owned or desktop engine by PID and start time, re-checked before thread-follower-start-turn and after) or first only REPORT it (Status and receipts say bound / unbound, nothing refused). Then build it as the findings file describes (CodeOwnedEngine carries the PID; DesktopOwnerBinding takes the holder's PID and start time; the incarnation derives from them, not the router's ServerPid; refuse a reparse point at the lock path): about 150-250 lines and tests with injected holder sets (0, 1, 2 holders, a non-codex holder, a start-time mismatch), a mutation case per refusal. Record the view hop as a vendor limit: the codex-ipc router would have to stamp each pipe client's verified PID (GetNamedPipeClientProcessId) on what it forwards and reject a response not from the target's socket.

The entry as it stood when it was first filed:

**N-agentbroker-code-owner-provenance-01** [design/recipient identity] Establish a verified binding from the exact normally opened Code view through its adapter to its owning engine. The pinned native router and an assigned client UUID do not provide an operating-system recipient binding; static extension pins and a global engine count do not prove the exact view's provenance. An authenticated Code-to-adapter-to-engine binding, or an equivalent route, must retain the existing cryptographic pins and global guards, establish the original owner without synthesizing an owner role, and pass applicable source checks plus live inherited-permission, busy/idle, tool-approval, interruption and reconnection acceptance. Do not skip a failing global guard or treat metadata/status advertisements as authenticated ownership. VS Code-only relay feasibility remains under source audit and is not enabled. Its current direct `turn/start` cannot promise GUI permission parity: pinned GUI code can retain latest settings locally after an unsupported `thread/settings/update`, then sends explicit approval, reviewer, sandbox, profile and runtime-root overrides on ordinary turns. The relay's initialization/thread/turn observations and `threadId` plus input injection do not establish that current GUI state or synchronize unsent/in-flight selections. Solve acknowledged settings plus GUI synchronization, or use the GUI's own admission route, before considering that fallback. Retain the source and live approval gates. Backlog design only; no recipient-binding or delivery trust rule changed.

---

## N-agentbroker-helper-native-sender-01 (moved from BACKLOG.md 2026-10-29)

**Blocker: built and released (AgentBroker 1.6.0, fa5c21e and 38a227d: the collaboration helper's send may name the calling Codex agent's own row, codex: + its canonical CODEX_THREAD_ID; 14/14 helper mutants FIRED and an isolated-worker -Execute probe answered as designed), but its one live proof needs a Codex session to run the helper against the installed broker, and Codex 01a1855c could not be reached on 2026-10-29: three deliveries to it ended Uncertain (native_submission_uncertain twice under 1.6.0, then native_uncertain_raised_cancelled under 1.6.1, the submission cancelled after eight minutes)**

What clears it: in a Codex chat on this workstation (VS Code or desktop), ask it to run, from the repository: pwsh -NoProfile -File tools/agentbroker/Invoke-AgentBrokerCollaborationMcp.ps1 -RequestFile <request.json>, first without -Execute and then with it, where the request is a send whose sender is its own row (codex: + its CODEX_THREAD_ID) to a generic mcp: mailbox, as docs/agentbroker.md "Collaboration helper" describes. Pass: validation accepts it, -Execute returns Accepted, and get_message_receipt on that id shows the codex: sender. A refusal code is the finding (close as DECLINED with it, or fix).

The entry as it stood:

**N-agentbroker-helper-native-sender-01** **Owner decision 2026-10-29: build it.** The helper may send as the agent's own native address (`codex:<CODEX_THREAD_ID>`), reviewed by Codex before it ships. [usability] `tools/agentbroker/Invoke-AgentBrokerCollaborationMcp.ps1` sends only between generic `mcp:` mailboxes (`Confirm-GenericMcpAddress`), on purpose: it "is not a Claude/Codex client identity". An agent whose own MCP client cannot connect (Codex under this organisation's MCP allowlist) reaches the broker only through it, so it can never send as its native row and its peers' replies land in a mailbox it must poll. With `whoami`, the helper could admit a native sender exactly when the broker confirms the calling process is inside that session. That changes a deliberate restriction, so it needs a reviewer before it is built.
