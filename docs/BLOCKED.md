# Blocked — items that cannot be actioned from here

Every item below names its blocker on its own line. None of them is waiting on a decision about what
to build or how; each is waiting on an environment, an account action, or an eyeball that this
machine and this session cannot supply. They are filed away from
[`../BACKLOG.md`](../BACKLOG.md) so the backlog reads as work that can actually be picked up, and
listed here rather than deleted so none of them is rediscovered from scratch.

---

## T2 / T49 — the MSI upgrade path, and the installed build's About window

**Blocker: needs a real reinstall of the MSI over a previous install.**

Two verification gaps, one blocker. From the v1.1.4 release record:

> **⚠ Still NOT done: the full `SMOKETEST.md` A-E walk, and the UPGRADE path.** The install above was
> onto a machine with no registered install, so it exercised first-install rather than
> install-over-previous, and the checklist is explicit that the upgrade path is the one users take. The
> A-E script also covers speech routing, the poke ladder, drag, multi-monitor pinning and fullscreen
> stand-down, none of which was touched.

And from the About/Help WPF rebuild, which was eyeballed by rendering the window to a PNG rather than
on an installed build:

> **Still worth a glance on the next reinstall:** the live tray → About / Help path on the installed
> MSI, and the light-theme variant (the capture followed this box's dark OS setting).

Both entries in full: [`HISTORY-post-1.0.0.md`](HISTORY-post-1.0.0.md).

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
drops straight into a module timer — and it is recorded with the other two candidate ports in
[`../BACKLOG.md`](../BACKLOG.md), feature idea 18, including the one piece of real technical care
(the low-level hook must be `UnhookWindowsHookEx`'d in `Shutdown()` or an ALC unload leaks a dangling
hook). Only the licence blocks it.

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
