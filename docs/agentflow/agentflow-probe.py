#!/usr/bin/env python3
"""AgentFlow detector probe -- can we tell a blocked agent from its transcript alone?

Two adapters, one detector. Both agents write an append-only jsonl where a tool call
and its result are separate records paired by an id, so an unpaired call plus a
stalled file means the agent is waiting on something. No pixels, no hooks, no OCR,
and no knowledge of which IDE is hosting the agent.

  claude  ~/.claude/projects/<slug>/<session>.jsonl
          message.content[] blocks: tool_use{id,name} paired by tool_result{tool_use_id}

  codex   ~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<id>.jsonl
          payload{type,call_id,name}: custom_tool_call / function_call paired by
          the matching *_output record carrying the same call_id

Prints tool NAMES, ids and durations only. Never arguments, never output, never any
path out of a transcript -- the same line the shipped product would have to hold.

Transcripts are read INCREMENTALLY (TranscriptCursor below): each poll folds only the bytes
appended since the last one, the way the shipped module does, rather than re-parsing every
active file from byte 0 every 2 s.

    python agentflow-probe.py [--agent claude|codex|both] [--threshold SECONDS] [--once]
"""

import argparse
import json
import os
import sys
import time

HOME = os.path.expanduser("~")
CLAUDE_ROOT = os.path.join(HOME, ".claude", "projects")
CODEX_ROOT = os.path.join(HOME, ".codex", "sessions")

# Codex has renamed its shell entry point at least once (shell_command ->
# exec_command) and permission-wildcarding's history-adapters.js records that the
# rename silently zeroed the whole corpus: an unknown name is indistinguishable from
# a session that called no tools. So accept every known spelling, and shout when a
# transcript parses but yields nothing.
CODEX_CALL_TYPES = {"custom_tool_call", "function_call"}
CODEX_OUTPUT_TYPES = {"custom_tool_call_output", "function_call_output"}


def active_jsonls(root, skip_dirs=(), window=900.0):
    """Every transcript written within `window` seconds, newest first.

    NOT "the single newest file", which is what this was and which is WRONG.
    Measured 2026-09-16: two sessions were live at once, the newest-file rule
    flipped between them every poll, and the probe reported a call from session B
    while claiming session A, then "nothing outstanding" when it happened to land
    on the quiet file. A blocked agent would have been missed entirely whenever a
    second session happened to write more recently -- which is the normal case on
    a machine running concurrent agents, i.e. exactly the machine this is for.
    """
    now = time.time()
    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        if os.path.basename(dirpath) in skip_dirs:
            dirnames[:] = []
            continue
        for name in filenames:
            if not name.endswith(".jsonl"):
                continue
            path = os.path.join(dirpath, name)
            try:
                mtime = os.path.getmtime(path)
            except OSError:
                continue
            if now - mtime <= window:
                found.append((mtime, path))
    return [path for _, path in sorted(found, reverse=True)]


def read_records(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    yield json.loads(line)
                except ValueError:
                    continue  # torn final line while the agent is mid-write
    except OSError as exc:
        print("  cannot read %s: %s" % (os.path.basename(path), exc), file=sys.stderr)


def fold_claude(record, pending):
    """Fold one Claude record into `pending`. True when it carried a tool call or a result."""
    content = record.get("message", {}).get("content")
    if not isinstance(content, list):
        return False
    saw_any = False
    for block in content:
        if not isinstance(block, dict):
            continue
        kind = block.get("type")
        if kind == "tool_use":
            saw_any = True
            pending[block.get("id")] = block.get("name") or "?"
        elif kind == "tool_result":
            saw_any = True
            pending.pop(block.get("tool_use_id"), None)
    return saw_any


def fold_codex(record, pending):
    """Fold one Codex record into `pending`. True when it carried a call or an output."""
    payload = record.get("payload")
    if not isinstance(payload, dict):
        return False
    kind = payload.get("type")
    if kind in CODEX_CALL_TYPES:
        pending[payload.get("call_id")] = payload.get("name") or "?"
        return True
    if kind in CODEX_OUTPUT_TYPES:
        pending.pop(payload.get("call_id"), None)
        return True
    return False


def pending_claude(path):
    """tool_use ids with no tool_result, as {id: tool_name}, from a parse of the whole file."""
    pending, saw_any = {}, False
    for record in read_records(path):
        if fold_claude(record, pending):
            saw_any = True
    return pending, saw_any


def pending_codex(path):
    """call_ids with no matching *_output, as {call_id: tool_name}, from a parse of the whole file."""
    pending, saw_any = {}, False
    for record in read_records(path):
        if fold_codex(record, pending):
            saw_any = True
    return pending, saw_any


class TranscriptCursor(object):
    """Incremental pairing state for ONE transcript, the shape modules/AgentFlow/TranscriptCursor.cs ships.

    poll_one used to hand every active transcript to pending_claude on every 2 s tick, which parses
    the file from byte 0 -- about 10 ms per MB, so one 81 MB session cost 0.6-0.8 s per tick and
    agentflow_cpu.py, which calls into this module once per agent root per sample, paid it for the
    whole hour a capture runs (F021, F010). Now each transcript keeps its offset and its pending set
    and folds only the bytes appended since the last refresh.

    Three rules, each learned by the C# cursor first:
      * the offset advances only past COMPLETE lines. The final line of a transcript the agent is
        mid-writing is torn; read_records skips it via ValueError and re-reads it whole next time,
        but an incremental reader that skipped it and advanced past it would lose that record for
        ever -- a tool_result dropped that way leaves a phantom pending call. The torn tail is left
        unconsumed and read again with the next chunk.
      * a file that SHRANK was truncated: start over.
      * a file that kept its length but changed its first bytes was replaced (rotated, or a different
        session written under the same name): start over as well. Size alone cannot tell.
    """

    HEAD_BYTES = 256

    def __init__(self, path, fold):
        self.path = path
        self.fold = fold
        self.offset = 0
        self.pending = {}
        self.saw_any = False
        self.head = b""

    def reset(self):
        self.offset = 0
        self.pending = {}
        self.saw_any = False
        self.head = b""

    def refresh(self):
        """(pending, saw_any) after folding whatever the agent appended since the last call."""
        try:
            with open(self.path, "rb") as handle:
                head = handle.read(self.HEAD_BYTES)
                handle.seek(0, os.SEEK_END)
                size = handle.tell()
                if size < self.offset or (self.head and head[:len(self.head)] != self.head):
                    self.reset()
                if not self.head:
                    self.head = head
                if size <= self.offset:
                    return self.pending, self.saw_any
                handle.seek(self.offset)
                chunk = handle.read(size - self.offset)
        except OSError as exc:
            print("  cannot read %s: %s" % (os.path.basename(self.path), exc), file=sys.stderr)
            return self.pending, self.saw_any
        lines = chunk.split(b"\n")
        complete = lines[:-1]          # the last piece is b"" after a trailing LF, or the torn tail
        for raw in complete:
            self.offset += len(raw) + 1
            raw = raw.strip()
            if not raw:
                continue
            try:
                record = json.loads(raw.decode("utf-8", "replace"))
            except ValueError:
                continue               # a complete line that is not JSON; read_records skips it too
            if self.fold(record, self.pending):
                self.saw_any = True
        return self.pending, self.saw_any


# One cursor per transcript path, shared with agentflow_cpu.py through load_probe(), so the saving
# reaches the harness that runs for an hour and not only this one.
_CURSORS = {}


def cursor_for(path, fold):
    cursor = _CURSORS.get(path)
    if cursor is None or cursor.fold is not fold:
        cursor = TranscriptCursor(path, fold)
        _CURSORS[path] = cursor
    return cursor


def forget_cursors_under(root, keep):
    """Drop the cursors of transcripts under `root` that are no longer active, so the cache is bounded
    by the active set rather than by everything ever seen."""
    for path in list(_CURSORS):
        if path.startswith(root) and path not in keep:
            del _CURSORS[path]


ADAPTERS = {
    # name: (root, skip_dirs, fold_fn)
    # A blocked SUBAGENT is not a prompt the user can answer, so those are skipped.
    "claude": (CLAUDE_ROOT, ("subagents",), fold_claude),
    "codex": (CODEX_ROOT, (), fold_codex),
}


def poll(agent, threshold, reported):
    root, skip, _fold = ADAPTERS[agent]
    if not os.path.isdir(root):
        return
    paths = active_jsonls(root, skip)
    forget_cursors_under(root, set(paths))
    if not paths:
        print("%-7s no recently-active transcripts under %s" % (agent, root))
        return
    for path in paths:
        poll_one(agent, path, threshold, reported)


def poll_one(agent, path, threshold, reported):
    _, _, fold = ADAPTERS[agent]
    pending, saw_any = cursor_for(path, fold).refresh()
    try:
        idle = time.time() - os.path.getmtime(path)
    except OSError:
        return
    session = os.path.basename(path)[:34]

    # A transcript that parses but yields no calls at all is the silent-failure mode
    # the Codex rename caused. Say so rather than reporting a clean "nothing pending".
    if not saw_any:
        print("%-7s ?? %s parsed but contained NO tool calls -- adapter may be stale"
              % (agent, session))
        return

    if pending and idle >= threshold:
        for call_id, name in pending.items():
            key = (agent, call_id)
            if key in reported:
                continue
            reported.add(key)
            # The session id is part of every line now, not decoration: without it
            # a multi-session box produces detections that cannot be attributed,
            # which is how the newest-file bug hid for a whole test run.
            print("%-7s BLOCKED s=%s tool=%-12s idle=%6.1fs call=%s"
                  % (agent, session[:8], name, idle, str(call_id)[:16]))
    elif pending:
        print("%-7s busy    s=%s outstanding=%d idle=%5.1fs"
              % (agent, session[:8], len(pending), idle))
    else:
        print("%-7s idle    s=%s nothing outstanding (idle=%5.1fs)"
              % (agent, session[:8], idle))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--agent", choices=["claude", "codex", "both"], default="both")
    parser.add_argument("--threshold", type=float, default=20.0,
                        help="seconds of transcript silence before calling it blocked")
    parser.add_argument("--interval", type=float, default=2.0)
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()

    agents = ["claude", "codex"] if args.agent == "both" else [args.agent]
    print("watching %s (threshold %.0fs)" % (", ".join(agents), args.threshold))
    reported = set()
    while True:
        for agent in agents:
            poll(agent, args.threshold, reported)
        if args.once:
            return 0
        try:
            time.sleep(args.interval)
        except KeyboardInterrupt:
            return 0


if __name__ == "__main__":
    sys.exit(main())
