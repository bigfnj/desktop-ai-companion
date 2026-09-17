# AgentFlow — research notes and measurement harnesses

**Status: research only, nothing built.** No module exists. This directory holds the four
harnesses that were used to decide whether AgentFlow is buildable, and what they measured.
Read this before proposing work, because two of the obvious designs are already ruled out by
numbers rather than opinion.

AgentFlow is a proposed module that notices when a coding agent (Claude Code, Codex) is sitting
blocked on a permission prompt, tells you, and optionally answers it so the agent keeps moving.
The companion framing is presence: the pet notices that your agent has been stuck for nine
minutes, which is the part a dashboard cannot do.

---

## What was decided, and why

### Detection does not need a VS Code extension, or any IDE integration

Both agents write append-only JSONL transcripts where a tool call and its result are separate
records paired by an id. An unpaired call plus a stalled file means the agent is waiting.

| agent | transcript | pairing |
|---|---|---|
| Claude Code | `~/.claude/projects/<slug>/<session>.jsonl` | `tool_use{id}` ↔ `tool_result{tool_use_id}` |
| Codex | `~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<id>.jsonl` | `custom_tool_call` / `function_call` ↔ `*_output`, by `call_id` |

This is **agent-keyed, not IDE-keyed**. The same transcript is written whether the agent runs in
VS Code, a JetBrains terminal, Antigravity, or a bare shell, so the notify half covers every host
on day one with no per-IDE code. That is what removed the need for a second shipped artifact.

The transcripts also carry enough to resolve the right window without asking the user: Claude
records `entrypoint` (`claude-vscode` / `claude-desktop` / `cli`), `cwd` and `gitBranch`; Codex's
`session_meta` records `originator` and `cwd`. Match those against `ScreenContext.Windows`, which
the host already exposes.

### A stall threshold alone is NOT a detector — measured, negative

`agentflow_backtest.py`, over 120 transcripts and 27,967 paired calls:

```
                   n        median      p90
completed       27830        1.6s      20.3s
user-rejected      14       86.2s     302.0s
```

The medians separate. It is nowhere near enough. At a 20s threshold, 2,678 ordinary completions
exceed it against 6 real prompts, i.e. ~450 false alarms per real one. Raising the threshold trades
recall away without reaching usable (best point ~175:1, already missing two thirds of prompts), and
restricting to prompt-capable tools barely moves it. Half the real prompts are answered in under
20 seconds anyway.

### The allow-list join works — but only outside auto mode

The join asks a second, independent question about the same call: *would this command have prompted
at all?*, answered by evaluating it against the permission rules already on disk (deny → ask →
allow, managed tier merged in). `agentflow_join.py` measures it.

**⚠ The numbers below replace the first set, which were measured against the wrong ground truth and
with a broken splitter. Both were corrected 2026-09-17 and the conclusion moved.** The original
table (auto 0.09% precision, default 19%, recall unmeasured per mode) is superseded; do not cite it.

Two corrections, and the first is the one that matters:

**`toolDenialKind` has five values, not one.** This harness treated `user-rejected` as "a prompt
happened". Over the 120 most recent transcripts:

| value | n | what it means |
|---|---|---|
| `permission-rule` | 30 | blocked **by a permission rule** — the class the matcher is accountable for |
| `automode-blocked` | 25 | blocked by the auto-mode model-side classifier |
| `user-rejected` | 16 | a human declined, rule-driven or not |
| `interrupted` / `cancelled` | 4 | not prompts at all |

Using `user-rejected` was wrong in both directions: it ignored `permission-rule` entirely, the one
class the rules are supposed to predict and nearly twice as numerous, and it counted human refusals
of calls the rules *allow*. That is why the misses clustered on `echo` and `cd` — commands the
managed allow-list covers, declined by a person anyway. No rule matcher predicts someone changing
their mind, and the earlier note blaming those misses on the splitter was wrong.

**The splitter was also replaced**, with the quote- and depth-aware one ported from
`permission-wildcarding` (`--difftest` checks the port against the JS original; **19,260 cases
agree**, 19,237 of them real commands harvested from transcripts). The naive regex had been
*inflating* `wouldPrompt` by about a third, because over-splitting invents fragments that match no
allow rule and "nothing matched" defaults to would-prompt.

Corrected, and now reproducible from the committed harness rather than attributed by hand:

```
mode            calls  wouldPrompt    %   realPrompts   precision   recall
auto            23188         5637   24%           23       0.41%      87%
acceptEdits      4964         1530   31%            6       0.39%      83%
plan             1175          276   23%            1       0.36%     100%
default            83           20   24%            0           -       -

kind                  n   wouldPrompt   hit-rate
permission-rule      30            26        87%
automode-blocked     25            11        44%
user-rejected        16             6        38%
```

**Recall is 87%, not 38%.** That is the axis that decides whether this is shippable, because a miss
means the companion stays silent while the agent sits blocked, and this harness's own preamble says
a matcher trading recall for precision is worse than none. The four misses are `cd` ×2, `git`,
`docker`.

**What moved in the conclusion.** "In auto mode the rules stop predicting anything" is **wrong** as
stated. Rules still cause prompts in auto mode — 23 of the 30 rule-caused denials are there, and the
matcher catches 87% of them. What collapses in auto mode is **precision**: 5,637 predictions for 23
real prompts, roughly 245 false per real. So AgentFlow is still a default-mode feature, but for a
different reason than recorded, and the honest state of the default-mode claim is *unmeasured*: this
corpus contains **zero** rule-caused denials in `default` mode, so its precision column is empty
rather than promising. That is exactly what the default-mode hour is for.

`automode-blocked` existing as its own label is the quieter find. The auto-mode conclusion had been
*inferred* by evaluating rules; the transcript states outright which denials came from the model-side
gate, and the rules predict only 44% of those — so it is a genuinely different population, measured
rather than argued.

**Consequence for the product: AgentFlow is a default-mode feature.** When the transcript reports
`permissionMode: auto`, it should say so and stand down rather than firing constantly — the same
way the MAX card refuses and explains instead of quietly settling for something weaker. Auto mode
prompts on 0.04% of calls; there is genuinely almost nothing to do.

**Gap in the harness, found 2026-09-17: `agentflow_join.py` never reads `permissionMode`.** The
mode split in the table above was attributed by hand in the session that produced it, so the
headline finding of this whole document is not currently reproducible from the committed harness.
Measured while fixing that: `permissionMode` rides on `type: "user"` records (702 of them across the
60 most recent transcripts) and **not** on tool calls, so attributing a call to a mode means
carrying the last value forward. A runtime flip emits its own record, `type: "permission-mode"`,
which is good news for collecting data mid-session — but that record carries **no `timestamp`**
(its only keys are `permissionMode`, `sessionId`, `type`), so the carry-forward has to be
positional, by file order. Any implementation that sorts records by timestamp first will drop the
mode changes on the floor. Same survey, for scale: `auto` 671, `acceptEdits` 26, `default` 6,
`plan` 3. Six default-mode turns in sixty transcripts is the entire default-mode corpus on this box.

### Process CPU is the wrong unit; the process TREE is the right one

Listed as an untried discriminator since 2026-09-16 and measured 2026-09-17. The idea: an agent at
a prompt computes nothing while a build does, it costs nothing to sample, it works while the host is
occluded or minimised, and — unlike everything built on the rule files — it does not care what
permission mode the session is in, so it is the one candidate that does not inherit the
default-mode ceiling above.

**Measuring the agent process would have measured nothing, and this was nearly the shape of the
experiment.** On this box a Bash tool call spawns `bash.exe` as a *child* of `claude.exe`; the child
burns the CPU while the agent process waits on a pipe. So bare process CPU is near zero for a build
*and* for a prompt, collapsing the exact distinction it was supposed to draw. `agentflow_cpu.py
--verify` measures both units against a child burning one full core:

```
  tree    0.990 cores busy over 3.0s
  process 0.005 cores busy over 3.0s
```

Two hundred to one. The tree is the unit: agent plus descendants, idle tree means waiting on a human
or on the model, busy tree means working. That check is the harness's own witness, and it exists
because an all-zero run is otherwise indistinguishable from a sampler that cannot open a handle or
picked the wrong root. Five mutations fired against it, including a straight regression to the
arithmetic below.

**Concurrency is the objection to answer, and per-tree sampling is what answers it.** The
maintainer normally runs 3–5 sessions at once, which would make a machine-wide CPU reading useless:
the box is busy nearly always, and "busy" says nothing about which session is stuck. Measured
2026-09-17 with four agents live (three `claude.exe`, one `codex.exe`), one 4-second window:

```
claude.exe pid=12480   19.645 cores
claude.exe pid=40804    0.444 cores
claude.exe pid=42252    0.082 cores
codex.exe  pid=46536    0.342 cores
```

A factor of 240 between the busiest and quietest tree while the machine as a whole was saturated. A
pid has exactly one parent, so trees cannot double-count, and the same sweep found only **0.012
cores** of agent-shaped work (`node`, `python`, `bash`, `pwsh`, `dotnet`, `msbuild`) living outside
every agent tree — so MCP servers and tool children are inside the tree that owns them, and almost
nothing leaks. One caveat left open rather than claimed: a *working* tree that is merely starved by
a saturated box would also read low. It did not happen here (the quiet trees sat at 0.08–0.44 while
another burned 19.6), which contradicts the concern without settling it.

**The consequence of concurrency is that attribution stops being optional.** With 3–5 sessions
live, a signal you cannot attribute to a session is worth nothing at all, and only two of the three
`claude.exe` processes above could be attributed. So the fresh-session gap below is not an edge
case on this box; it is the common case, and it is the blocker on this discriminator rather than a
documented limitation.

**Attribution is UNSOLVED, and it is the blocker.** Three methods were tried on 2026-09-17 and the
section below records each one's outcome, because two of them are the obvious ideas and both cost
an hour to rediscover.

`claude.exe` carries `--resume=<session-id>` on its command line and that id *is* the transcript
filename, which looks exact and is the best available. Treat it as probably-right-but-unverified
rather than exact: a command line is **launch state**, the same property that makes
`--permission-mode` there useless, so it would go stale if a panel can be handed a new session
without relaunching. Not demonstrated either way (one root matched its transcript 5/5 on activity),
so this is a known unknown, not a known fault. Two limits beyond that, both observed:

- a **fresh** session has no `--resume=` yet, so its tree cannot be attributed this way and reports
  session `?`. Confirmed live: the session that wrote this file was pid 42252 with no `--resume`.

  **Asking which transcript the process holds open does NOT work, measured 2026-09-17.**
  `handle.exe -p 42252` lists 20 handles for that process and not one `.jsonl` among them: the agent
  appends to the transcript and closes it rather than keeping it open. The tool had the rights it
  needed (it enumerated the process fine), so this is a real negative and not the unelevated-read
  mistake it resembles. Worth recording because the idea is the obvious one and it costs an hour to
  rediscover.

  **`cwd` does not disambiguate either.** Three sessions were live in one VS Code workspace
  (`D:\.ai-work`), so they share one `~/.claude/projects` slug, and the only directory handles the
  process holds are the workspace root and `.claude`. The `cwd` inside a transcript is the *tool*
  cwd and drifts as the agent moves around: the session writing this file recorded
  `...\docs\agentflow` because it had `cd`'d there, not its project.

  **Process creation time does not work.** A fresh session's transcript is created when the first
  message is sent, and the process starts when the panel opens, so the gap between them is however
  long the user took to type. Ruled out before building it.

  **Activity alignment is built, and it is NOT reliable — this is the current state.** The signal:
  a tool call runs in a child of the agent process, so a child's creation time and the transcript's
  `tool_use` timestamp are one event seen from two sides, which (unlike process creation) is
  emitted repeatedly by the agent's own work. `agentflow_cpu.py --attribute` implements it and
  `--validate` grades it against the `--resume` ids with the sampler's own session excluded, since
  that one is attributed trivially by the sampler being its own tool child.

  **It was never wrong, and its coverage flipped between two consecutive 25-second runs.** First
  run: pid 40804 → `ca52f58a` correct 7/24, pid 12480 abstained with 0 alignment. Second run: pid
  12480 → `de425e11` correct 5/5, pid 40804 abstained on a 2-2 tie. Same machine, same code, four
  minutes apart, **0 WRONG across all four gradings**. So the refusal discipline held and the
  failure is coverage, not correctness: it answers only for whichever session happened to be
  running tools inside the window, which on this box is roughly half of them and a different half
  each time. A detector that can name the blocked session half the time, unpredictably, is not yet
  a detector — but it is failing safe, which is the half that is harder to add later.

  Two confounds behind that. Only **one** transcript emitted a tool call during the observation, so
  there was nothing to discriminate against and a hit count of 5/5 with runner-up 0 is arithmetic,
  not evidence — the degenerate-axis failure this project has already recorded once. And a direct
  child of `claude.exe` is **not always a tool call**: this box has a `PostToolUse` hook registered,
  and hooks plus internal shell-outs spawn children with no `tool_use` record at all.

  `--validate` therefore counts how many transcripts were busy *during* its own observation window
  and prints `DEGRADED` and `NOT A PASS` when fewer than two were, on every run. That guard is
  load-bearing rather than decorative: disabling it turns the refusal into `exit 0`, `1 correct,
  0 WRONG`. It also prints, every run, that its ground truth is launch state and so a disagreement
  is ambiguous between a bad matcher and stale truth.

  **What this means for the discriminator.** Per-tree CPU separates cleanly (240x, above) and is
  worth nothing until a tree can be named, so attribution is now the whole problem. The next
  candidate is to stop inferring identity and read it: correlate over a long window instead of a
  25-second one, and require agreement across several disjoint windows before accepting a mapping,
  which converts an unstable single measurement into a stable one or reports `?` forever. If that
  fails too, the honest fallback is to attribute only sessions carrying `--resume` and drop the
  rest, accepting the recall loss.
- `--permission-mode` on that same command line is the **launch** mode. It does not change when the
  mode is flipped at runtime, so it must never be read as the current mode.

**Do not sample the agent that is driving the sampler.** The launching tool call stays an unpaired
`tool_use` for the whole run, so that session reports `pending=1` for every sample of its own
collection and sits in the "outstanding" bucket throughout. Run it detached, or point it at another
session.

### Answering a prompt requires a classifier, and it is the safety mechanism

The Claude Code webview bundle ships **ten** distinct strings beginning with "Yes", and only three
mean "approve this one call":

| meaning | strings |
|---|---|
| approve this call | `Yes`, `Yes, allow `, `Yes, allow access to` |
| wider grant | `Yes, allow all edits this session`, `Yes, and don't ask again` |
| changes permission mode | `Yes, and auto-accept`, `Yes, and manually approve edits`, `Yes, return to normal mode`, `Yes, set auto mode as my default` |

So "just send Yes" is not implementable safely, and a prefix match would eventually press
*set auto mode as my default*. `agentflow_classifier.py` enforces three rules, each because the
obvious implementation is wrong:

1. **Allowlist, never denylist.** An unrecognised option refuses. A denylist fails *open* the day a
   new `Yes, ...` variant ships.
2. **Exact match for complete strings, prefix match only for templates.** Entries ending in a space
   are templates with a runtime value appended (`Yes, allow access to <host>`). Longest match wins.
3. **One unknown option poisons the whole prompt.** Either the capture misread the screen or the
   bundle changed; both mean do not touch it.

The permission **mode is never ours to change** — `choose()` only ever returns an approve-once row,
and that is asserted and mutation-tested rather than left as an implicit consequence of a filter.

---

## Prior art

Four public tools that implement the answering half, all read at source level on 2026-09-16,
because in every case the README understates or misstates what the code actually presses.

| tool | host reach | actuation | match rule | presses a wider grant | has a detector |
|---|---|---|---|---|---|
| `Munkhin/auto-accept-agent` | Antigravity, Cursor | CDP | substring | yes, `always allow` and bare `allow` | no |
| `sudoghut/llm-auto-confirm` | Claude Code, Codex, in a terminal | Shell Integration, pyautogui | regex over scrollback | yes, blind Enter on the cursor row | regex only |
| `nextcortex/antigravity-auto-accept` | Antigravity | VS Code commands, settings flip, CDP, UIA | substring, `always` rejected | yes, via `agentAcceptAllInFile` | no |
| `nockasdd/domyh-auto-accept` | Antigravity, Cursor, Trae, Windsurf, VS Code | CDP | anchored regex | yes, `AcceptAll` is priority 1 | no |

### The finding that reframes this module

**None of the four has a prompt detector, and three do not need one.** They poll and fire the
accept action on a fixed interval, swallowing the failures. That works because their accept action
is idempotent: a VS Code command that no-ops when nothing is pending, a click on a button that is
not there. If the action is safe to attempt against nothing, the detector is redundant.

This is why finding 2 above reads as harder than the field treats it. The 450:1 false-alarm rate
that kills a stall threshold only matters because AgentFlow wants to **tell a human**, and a
notification fired 450 times per real event is worthless. Nobody else pays that cost, because
nobody else notifies. **The detector is a requirement of the notify feature, not of the answering
feature.** Worth carrying into the build order: the notify half is the hard half and the
differentiated half, and it should not wait on the answering half to be solved.

**All four press a wider grant, and the best-engineered one does it deliberately.** This is not a
gap that better implementations close. It is the default destination of the whole category,
because the widest button is the one that reduces the most interruptions, which is what these
tools are for. That is the strongest external support the allowlist design has: four independent
authors, four architectures ranging from a weekend script to a layered TypeScript codebase with
tests, and all four land on pressing the button this project decided is never pressable.

**Actuation splits four ways, not three.** Terminal Shell Integration, VS Code commands, CDP into
the renderer, and Windows UI Automation. Only the first two are ordinary extension work.

### `Munkhin/auto-accept-agent`

MIT, JavaScript, 95 stars / 53 forks / 7 open issues, created 2025-12-11,
last pushed 2026-02-27. It implements the answering half for Antigravity and Cursor. Reading it
confirms two of the decisions above from the outside, and contributes one mechanism worth taking.

**How it works.** `shortcut_editing_scripts/` rewrite the IDE's launcher shortcut to add a
remote-debugging port. After a restart, `cdp-handler.js` attaches over the Chrome DevTools Protocol
and injects `auto_accept.js`, which runs `queryAll(selector)` across every document and iframe in
the renderer and clicks what matches. From `extension/SELECTORS.md`:

| | |
|---|---|
| Antigravity selectors | `.bg-ide-button-background`, `button.cursor-pointer`, `button` |
| Cursor selectors | `button`, `[class*="button"]`, `[class*="anysphere"]` |
| accept patterns | `accept`, `run`, `retry`, `apply`, `execute`, `confirm`, `always allow`, `allow once`, `allow` |
| reject patterns | `skip`, `reject`, `cancel`, `close`, `refine` |

Matching is substring against `textContent`, trimmed, lowercased, first 50 characters. Reject
patterns are tested first; anything else matching an accept pattern is clicked.

**It confirms that IDE-keyed is the wrong axis.** This is the per-host design, built out: one
hand-maintained selector set per IDE. Claude Code is not supported at all, in a repo whose entire
purpose is clicking agent prompts, because nobody has written its selectors yet. The README concedes
the tool may stop working on newer Antigravity or Cursor builds, and seven months have passed since
the last push against IDEs that ship weekly. The transcript approach covered every host on day one
and cannot rot this way.

**It confirms allowlist-not-denylist by being the counterexample.** Its accept list contains
`always allow` and a bare `allow` as substrings, so it presses precisely the two categories this
project ruled never-pressable: the wider grant, and the unrecognised option. Its five-word reject
list is the denylist that fails open, and it fails open now rather than eventually, because any
button text containing "allow" is already a click.

**Its safety check cannot fail.** Banned-command screening runs only when the button text contains
`run` or `execute`, so an Accept or Apply prompt is never screened at all. When it does run, it
walks up to 10 parent levels scanning up to 5 siblings per level for `pre`, `code` and `pre code`,
plus the button's `aria-label` and `title`. Finding no command text passes the check, which is
indistinguishable from passing because the command was safe. The list underneath is 11 substrings
(`rm -rf /`, `rm -rf ~`, `format c:`, `dd if=`, `mkfs.`, `> /dev/sda`, and five more), so `rm -fr /`,
`rm -rf ~/work`, or the same call wrapped in a script all clear it.

**Worth taking: CDP is a better actuation channel than keystroke injection.** This is the one thing
the repo has that this project does not. Driving the renderer over the debugging port presses a
button without focusing, raising or restoring the window, which is the same property process CPU
was wanted for on the detection side. The costs are a launch flag and an IDE restart, and it reaches
only Electron hosts, so it is an enhancement for the VS Code case and never the whole answer. If it
is adopted, the selector is ours and the decision still comes from `agentflow_classifier.py`. The
pattern list above is not reusable.

### `sudoghut/llm-auto-confirm`

MIT, TypeScript, 1 star / 0 forks / 1 open issue, created 2026-02-23, last pushed 2026-08-03. Far
more relevant than the above: it names Claude Code and Codex as its two tested targets, and it
ships a VS Code extension rather than a DOM clicker. It is also the closest thing found to a
competing implementation of this module, and it gets the safety-critical part wrong in a way its
own README does not reveal.

**Two independent paths.** `auto_confirm.py` is screenshot plus OpenCV template matching at a 0.85
confidence threshold, clicking with pyautogui; it needs the window visible and does not work
minimised, so it is not interesting here. The VS Code extension is. `src/terminal-monitor.ts` reads
the terminal through the Terminal Shell Integration API, strips ANSI in six passes (CSI, OSC,
charset, DEC private modes, keypad, control characters), converts lone CR to LF, keeps a buffer,
and regex-matches a 30-line window. `src/webview-monitor.ts` covers panel-hosted agents through the
VS Code command API, polling on an interval, and is off by default.

**It answers by position, and on the real target it does not even do that.** The configured
`confirmResponse` is `"1"`, and both Claude Code rules carry `"response": "1"`. But those rules use
`addNewline: "auto"`, and `confirmWithRule()` then calls `detectInteractiveList()`, which tests the
last 15 lines against `(?:U+276F|U+203A|>)\s*\d`. On a hit it **discards the configured response
and sends a bare Enter**, accepting whichever row the TUI cursor is sitting on. It never reads the
highlighted row's text. Claude Code's prompt is an interactive list, so this is the path that
always runs: the `"1"` in the settings UI is never sent to the tool the extension was built for.

This is the failure `agentflow_classifier.py` exists to prevent, in its purest form. Prefix matching
was rejected here because it would *eventually* press "set auto mode as my default". Ordinal
matching is worse, since it does not look at the text at all, and a blind Enter is worse again,
because the row is chosen by wherever the cursor happens to rest. All ten `Yes*` strings are equally
reachable, including the four that change permission mode.

**Its danger check is bound to a scrollback window, not to the pending action.** `isDangerous()`
tests seven patterns (`rm -rf /`, `rm -rf ~`, `format c:`, a fork bomb, `dd if=/dev/zero`,
`> /dev/sda`, `mkfs\.`) against the same 30 recent lines used for detection. That fails both ways.
It misses when the command scrolled past 30 lines, and `dd if=/dev/urandom` is not on the list at
all. It also false-positives on any unrelated prompt that happens to follow a README, diff or chat
message containing one of those strings. A denylist over a text window is not a check on the action
being approved.

**A structural note on the signal.** Converting lone CR to LF to "preserve line structure from TUI
redraws" means an overwritten frame stays in the buffer as its own lines, so the 30-line window can
hold the current frame and stale ones together. The cooldown, the `outputBuffer = ""` reset after
each confirm, and the post-confirm re-check timer are all compensating for that. Scraping a
redrawing TUI is a fundamentally noisier signal than the append-only JSONL with explicit id pairing,
and this is what paying for the difference looks like.

**Worth taking, three things.** The Terminal Shell Integration API is a third actuation channel
alongside CDP and keystroke injection, and for the CLI case it is the cleanest of them: a text
stream in, `terminal.sendText()` out, inside an ordinary extension, with no debugging port, no
shortcut rewriting and no window focus. It needs VS Code 1.93+ with shell integration active.
Second, **observe-only mode is the right shipping default**, and it is implemented properly here:
`suppressMatch()` runs before any send, logs the rule name and the matched text, and fires an
`onSuppressed` callback a UI can consume. That is the notify half, shippable on its own, which is
the shape this module should take first. Third, prompt rules are data (`name` / `pattern` /
`response` / `addNewline`) rather than code, which is a reasonable structure to copy even though
every rule in the default set is one this project would refuse.

**Caveat on the channel split.** Terminal monitoring only reaches an agent running in a VS Code
terminal. Claude Code in the extension panel is a webview, which is why this repo needs a separate
webview monitor, defaults it off, and documents Codex webview panels as unsupported. Detection here
is already host-independent; actuation is not, and this repo is evidence that it splits at least
three ways (terminal, webview, native window) rather than two.

### `nextcortex/antigravity-auto-accept`

MIT, TypeScript, 2 stars, created and last pushed 2026-03-05, thirty minutes apart. Written in one
sitting and abandoned. Judged on mechanism anyway, because it is the only one of the four that
tries four actuation channels, and two of them are new.

**It does not detect anything.** `tick()` runs every 800 ms and calls
`vscode.commands.executeCommand()` on all eight entries of `autoAcceptCommands` unconditionally,
with `catch {}` and the comment "Command not available — expected when no pending accept action".
The error handler is the filter. That is the clearest example in the field of the polling design
described above, and also of an action with no precondition where a failing run and a working run
are indistinguishable. One of the eight is
`antigravity.prioritized.agentAcceptAllInFile`, so a blind tick accepts every pending hunk in a
file eight times a second.

**The settings flip is the MAX card, done the way MAX deliberately is not.** `enableAutoSettings()`
saves each current value into an in-memory `Map` and writes the permissive value;
`restoreAutoSettings()` writes the saved value back. Three consequences the sibling MAX
implementation already avoids. The restore is a blind overwrite rather than a union, so a value the
user changed while auto-accept was on is clobbered. The snapshot lives only in memory, so a crash,
a window reload or a disable-while-on leaves the settings permissive **permanently and silently**,
which is why MAX snapshots to disk. And `cfg.update()` is never awaited, so `Applied N settings`
is logged without knowing that any write landed.

Its reach also exceeds its host: `gemini.cli.yoloMode` makes a VS Code extension open a different
tool's settings file and set `approval_mode` there. Restore has the same in-memory fragility.

**Credit where it is due: it is the only one of the four to reject wider grants by keyword.**
`autoAcceptRejectKeywords` ships `always`, checked before the accept list, so `always allow` does
not match. That is a denylist and it still fails open on the next unforeseen phrasing, but the
author is the only one who saw the category.

**New channel: `src/uia-worker.ps1`.** Windows UI Automation from PowerShell. It finds the window
by `ClassNameProperty`, walks `TreeScope::Descendants` for `ControlType::Button`, matches
`Current.Name` against start-anchored patterns, and calls `InvokePattern.Invoke()`. This is the
native-window channel, it needs no focus and no debugging port, and it is the one that would work
against an agent running outside any Electron host. It is also the one that needs `InputSynthesis`
least, since `Invoke()` is not synthetic input.

### `nockasdd/domyh-auto-accept`

MIT, TypeScript, 2 stars, created 2026-02-17, last pushed 2026-02-24. By a wide margin the most
serious of the four: layered domain/application/infrastructure/presentation, per-IDE adapters with
a registry (Antigravity, Cursor, Trae, Windsurf, **and stock VS Code**), vitest suites, CI, a
dashboard, and a 115 KB injected CDP payload with its own diagnostic tooling under `scripts/`.

**It answers the fork question directly.** `VSCodeCopilotAdapter` declares
`launchFlag = '--remote-debugging-port=9229'` and filters CDP targets to
`type === 'webview' || type === 'iframe' || (type === 'page' && url.includes('workbench'))`. So
CDP does reach stock VS Code, and it reaches webview targets specifically, which is the kind of
target a panel-hosted Claude Code renders into. The plumbing transfers from the forks.

**The vocabulary does not transfer.** That same adapter's commands are
`github.copilot.acceptSuggestion`, `chatEditing.acceptAllFiles`,
`editor.action.inlineSuggest.commit`. Nothing for Claude Code. Its selectors still carry
`span[class*="bg-ide-button"]`, an Antigravity Tailwind class copied over from the sibling adapter,
which is a fair sign the VS Code path is aspirational rather than exercised. Fork kinship buys the
transport and the target filter; every pattern is still ours to write.

**It is the only one that matches on anchored regex.** `/^accept$/i`, `/^accept\s*all$/i`,
`/^yes$/i`, per button type, per adapter. That is rule 2 of `agentflow_classifier.py` reached
independently, and it is what makes the next point deliberate rather than accidental.

**And it prefers the widest button on purpose.** `ButtonType.AcceptAll` is its own category and
`ButtonPriority.AcceptAll = 1`, the highest. Given both an accept-one and an accept-all button it
takes accept-all. The best-engineered tool in the category, with the strictest matcher, uses that
precision to press exactly the option this project forbids.

**Worth taking, two things.** `ButtonMatch` carries `blocked: boolean` and `blockReason?: string`,
so a refusal is a first-class value that flows to the UI rather than a silent skip; the classifier
here should return the same shape. And `PermissionPattern` has `requiresConfig` plus `configKey`,
gating a permission category behind a named flag rather than one global toggle. The standing
decision is that wider grants are not a setting at all, so this is not adopted as-is, but
per-category gating is the right granularity for anything that does become configurable.

**`SilenceDetector` is not a prompt detector, despite the name.** It subscribes to
`engine:statsUpdated` and watches **its own click count**, firing after 30 s with no clicks so the
`Scheduler` can send the next queued prompt. It answers "has my clicker gone quiet", not "is the
agent blocked". Do not read it as a competing solution to finding 2.

**`DeathLoopGuard` is the one safety idea in the field that this project does not have.** A sliding
window of retry timestamps, a max per window, then pause, cooldown, and an event the UI can show.
It exists because anything that presses Retry will eventually press it against an error that keeps
recurring, and that is a failure AgentFlow inherits the moment it presses anything. Take the idea,
not the implementation: `recordSuccess()` resets the consecutive-error counter but not the
timestamp window, the cooldown clears all state and auto-resumes so a persistent loop simply
cycles, and the `consecutiveErrors > 5` branch only logs.

---

## The harnesses

All five are standalone Python 3, no dependencies, read-only. They print tool names, counts and
durations — never command arguments, never tool output, never a path or prompt text out of a
transcript. Any production code has to hold the same line, especially out of the diagnostic log.

| script | what it answers | run |
|---|---|---|
| `agentflow-probe.py` | Live: is an agent blocked right now? | `python agentflow-probe.py --threshold 15` |
| `agentflow_backtest.py` | Does wait time alone separate prompts from slow tools? | `python agentflow_backtest.py --files 120` |
| `agentflow_join.py` | Does knowing the permission rules kill the false alarms? | `python agentflow_join.py --files 120 \| --selftest \| --difftest` |
| `agentflow_classifier.py` | Which prompt option is safe to press? | `python agentflow_classifier.py --selftest \| --audit \| --mutate` |
| `agentflow_cpu.py` | Does agent CPU separate blocked from working? | `python agentflow_cpu.py --verify \| --attribute \| --validate \| --interval 2 --count 20 --csv out.csv \| --report out.csv` |

`agentflow_cpu.py --verify` is the only one of the five that can be run with no agent present and
no data: it proves its own measurement mechanism. `--validate` needs at least two sessions running
tools concurrently and says `DEGRADED` when it does not have them. `--attribute` reports `?` for
Codex roots by design rather than scoring them against Claude transcripts, which would be a
mis-attribution path; Codex needs its own candidate index.

`--audit` re-derives the option set from whatever agent bundle is installed and fails if the table
cannot classify one of them. `--mutate` breaks the classifier five ways and proves the self-test
catches each; a clean run with no mutation firing means the suite is blind, not that the code is
good. Current state: 25/25 self-test, audit clean, 5/5 mutations fired.

### Four defects these found that would otherwise have shipped

**Single-newest-transcript tracking is wrong.** The probe originally watched only the most recently
modified transcript. With two sessions live it flipped between them every poll, attributed one
session's call to another, and reported "nothing outstanding" whenever it landed on the quiet file.
A blocked agent would have been missed entirely whenever a second session wrote more recently —
the normal case on a machine running concurrent agents, which is exactly the machine this is for.
Now every transcript written in the last 15 minutes is polled independently and the session id is
on every output line, because a detection you cannot attribute is how this hid for a whole run.

**A bare `Yes, allow` must be UNKNOWN, not approve-once.** That string exists in the bundle only as
a template, so the realistic way to see one is OCR truncating `Yes, allow access to <host>` at the
window edge — meaning the row was not fully read. The original assertion demanded approve-once and
would have pressed a half-read option. Kept as a witness test, because the tempting fix is to add a
bare exact entry.

**The recall measurement was scored against the wrong ground truth for a full session's worth of
conclusions.** `toolDenialKind` has five values and the harness read one of them, so it graded the
rule matcher on human refusals (which no rule predicts) while ignoring `permission-rule` (which is
precisely what it predicts). Corrected recall is 87% against a measured 38%, and the earlier
explanation for the misses — the naive splitter — was itself wrong. The lesson is narrow and
reusable: before trusting a precision or recall figure, enumerate every value the label field
actually takes. One `collections.Counter` over the corpus would have caught this at the start.

**Differencing tree CPU totals across samples yields NEGATIVE CPU.** The first live run of
`agentflow_cpu.py` printed `cores=-5.9000`. A tree's membership changes constantly while an agent
runs tool calls, and a child that exits between two samples takes its accumulated CPU out of the
sum, so the difference of two totals goes negative. The negative is the *harmless* half, because it
is visibly wrong. The silent half is that the same mechanism **understates a busy tree** whenever
any child exits mid-interval, making a working agent look idle — and "looks idle" is exactly what
this harness would read as blocked, so the quiet form of the bug manufactures the false alarms the
experiment exists to eliminate. Fixed by summing per-pid deltas, which are monotonic, and by
counting vanished pids onto every row so the unmeasurable slice a dead child took with it is
visible instead of passing as a clean sample. The regression is a mutation in the suite: reverting
to total-differencing returns −450 for a case whose true answer is 50.

---

## Next step

**The one open measurement is default-mode precision.** Recall is settled at 87%, and the splitter
and the mode attribution are both done (see the corrections above). What no corpus on this box can
supply is precision in `default`: there are **zero** rule-caused denials in that mode across 120
transcripts, because this box runs auto. Generate it the only way it can be generated — work
normally in default mode for an hour, then rerun `agentflow_join.py`, which now reports the split
itself instead of needing it attributed by hand.

Process CPU is no longer untried: `agentflow_cpu.py` exists, the mechanism is verified, and the
unit turned out to be the process **tree** rather than the process (see above). What it has not
done yet is produce a separation number, because that needs samples taken alongside labelled
prompts, which is the same default-mode hour.

**What must exist before the hour, and what must not hold it up.** Only the live signal is
perishable. Transcripts persist under `~/.claude/projects`, so the join, the mode attribution and
the splitter can all be fixed *after* the data is generated and rerun over the same files as often
as needed — the default-mode hour does not wait on any of them. CPU samples are the opposite: they
exist only if something was sampling at the time, and they are worthless unattributed, so
`agentflow_cpu.py` plus the fresh-session matcher must both be working *during* that hour.

Also worth separating: the hour should be **ordinary work in default mode**, not a session spent
building this tooling. The join measures how often the permission rules predict a real prompt, and
the tool mix of a session writing Python harnesses is not the mix that question is about.

On the answering side, the prior-art review turned up four actuation channels, none of which needs
the window focused or raised. Terminal Shell Integration reaches an agent in a VS Code terminal and
is the cleanest. A VS Code command reaches whatever the host exposes, and costs nothing, but Claude
Code publishes no accept command so it is likely empty for us. CDP reaches a webview-hosted agent,
and `VSCodeCopilotAdapter.filterTargets()` shows stock VS Code webview targets are reachable, at
the cost of a debugging port and a launch-flag rewrite. UI Automation reaches a native window and
needs neither. None is proven here: nothing has been attached to a real Claude Code prompt, and
every pattern and selector is ours to write regardless of channel.

Build order follows from the reframing above. The notify half is the hard half, the differentiated
half, and the one nobody else has; ship it behind observe-only and let answering be a separate
per-channel decision afterwards. Whichever channel is chosen, a death-loop guard belongs in the
first version that presses anything.

Open architectural question, deliberately not settled: whether the detector ships inside
`permission-wildcarding` (which already reads both agents' history and owns the rule matcher) with
the companion module as a thin consumer, or lives here. The leaning is the former.
