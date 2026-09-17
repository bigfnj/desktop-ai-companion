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
allow, managed tier merged in). `agentflow_join.py` measures it, split by permission mode:

```
mode            calls  wouldPrompt    %   realPrompts   precision
auto            21758         8684   40%            8       0.09%
acceptEdits      4855         1844   38%            0
plan             1274          464   36%            0
default            85           31   36%            6      19%
```

In **default mode** the rules *are* the gate and the join is ~19% precise, roughly 4 false alarms
per real prompt, which combined with a stall threshold is usable. In **auto mode** a model-side
classifier sits in front of the rules and approves the overwhelming majority, so the rules stop
predicting anything: 8,684 predictions, 8 real prompts.

**Consequence for the product: AgentFlow is a default-mode feature.** When the transcript reports
`permissionMode: auto`, it should say so and stand down rather than firing constantly — the same
way the MAX card refuses and explains instead of quietly settling for something weaker. Auto mode
prompts on 0.04% of calls; there is genuinely almost nothing to do.

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

All four are standalone Python 3, no dependencies, read-only. They print tool names, counts and
durations — never command arguments, never tool output, never a path or prompt text out of a
transcript. Any production code has to hold the same line, especially out of the diagnostic log.

| script | what it answers | run |
|---|---|---|
| `agentflow-probe.py` | Live: is an agent blocked right now? | `python agentflow-probe.py --threshold 15` |
| `agentflow_backtest.py` | Does wait time alone separate prompts from slow tools? | `python agentflow_backtest.py --files 120` |
| `agentflow_join.py` | Does knowing the permission rules kill the false alarms? | `python agentflow_join.py --files 120` |
| `agentflow_classifier.py` | Which prompt option is safe to press? | `python agentflow_classifier.py --selftest \| --audit \| --mutate` |

`--audit` re-derives the option set from whatever agent bundle is installed and fails if the table
cannot classify one of them. `--mutate` breaks the classifier five ways and proves the self-test
catches each; a clean run with no mutation firing means the suite is blind, not that the code is
good. Current state: 25/25 self-test, audit clean, 5/5 mutations fired.

### Two defects these found that would otherwise have shipped

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

---

## Next step

The `default` sample is n=85 with 6 real prompts, so the 19% precision figure is promising rather
than measured. Generate real default-mode data: switch a session out of auto mode, work normally
for an hour, rerun `agentflow_join.py`.

Two other things are known-incomplete. The compound-command splitter in `agentflow_join.py` is
naive — it does not handle a bare `&`, and a `;` inside quotes splits wrongly — which caused most
of the recall failure (misses clustered on roots `echo`, `cd` and `&`). A correct splitter exists
already in the sibling `permission-wildcarding` project and should be reused rather than rewritten.
And process CPU was never tested as a discriminator: an agent at a prompt is near 0% while a build
is not, it is free, and it works occluded and minimised.

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
