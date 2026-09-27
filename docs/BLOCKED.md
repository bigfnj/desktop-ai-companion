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

Moved from [`../BACKLOG.md`](../BACKLOG.md) on 2026-09-27. Recall is settled at **93% (28/30** against
calls a permission rule actually blocked). Precision in `default` mode cannot be computed here: the
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

