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

Open architectural question, deliberately not settled: whether the detector ships inside
`permission-wildcarding` (which already reads both agents' history and owns the rule matcher) with
the companion module as a thin consumer, or lives here. The leaning is the former.
