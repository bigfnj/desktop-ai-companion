#!/usr/bin/env python3
"""Measure the allow-list join: does knowing the rules kill the false alarms?

The stall detector cannot tell "blocked on a prompt" from "running a slow build".
The join adds a second, independent question about the same call -- would this
command have prompted AT ALL? -- answered from the permission rules already on
disk. This measures whether that question is worth asking.

Two things are measured, and the first matters more:

  RECALL   Of the calls we KNOW prompted (toolDenialKind == user-rejected), how
           many does the matcher correctly call "would prompt"? Anything it calls
           "would allow" is a PROVEN MISS -- the detector would stay silent while
           the agent sat there. Misses are the failure mode that reads as "the
           feature doesn't work", so a matcher that trades recall for precision is
           worse than no matcher.

  FILTERING Of the slow completions that currently produce false alarms, how many
           would the join suppress?

Command text is read to evaluate it and is NEVER printed. Output is counts plus
executable ROOTS (program names), nothing else.

    python agentflow_join.py [--files N] [--threshold SECONDS]
"""

import argparse
import collections
import datetime as dt
import fnmatch
import glob
import json
import os
import re
import sys

HOME = os.path.expanduser("~")
USER_SETTINGS = os.path.join(HOME, ".claude", "settings.json")
MANAGED_SETTINGS = os.path.join(HOME, ".claude", "remote-settings.json")
CLAUDE_GLOB = os.path.join(HOME, ".claude", "projects", "*", "*.jsonl")

WOULD_PROMPT = "would-prompt"
WOULD_ALLOW = "would-allow"
WOULD_DENY = "would-deny"
UNDECIDABLE = "undecidable"

# Claude Code evaluates each sub-command of a compound separately -- that is the
# entire premise of permission-wildcarding's shell-style guidance -- so a chain is
# only "allowed" if EVERY part is.
_SPLIT = re.compile(r"\s*(?:&&|\|\||[;|])\s*")
# A leading VAR=value prefix is stripped before matching, per the same guidance.
_ENV_PREFIX = re.compile(r"^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)+")
_RULE = re.compile(r"^([A-Za-z_]+)\((.*)\)$")


def load_rules():
    """Merged (deny, ask, allow) as {tool: [patterns]}, user + managed.

    Arrays MERGE across tiers rather than the managed one replacing the user's,
    and evaluation is deny -> ask -> allow. That ordering is why a managed `ask`
    beats a user `allow`: the ask matches first and never reaches the allow.
    """
    buckets = {k: collections.defaultdict(list) for k in ("deny", "ask", "allow")}
    sources = 0
    for path in (USER_SETTINGS, MANAGED_SETTINGS):
        try:
            with open(path, "r", encoding="utf-8-sig") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            continue
        sources += 1
        perms = data.get("permissions") or {}
        for key in buckets:
            for entry in perms.get(key) or []:
                match = _RULE.match(entry.strip())
                if match:
                    buckets[key][match.group(1)].append(match.group(2))
                else:
                    buckets[key][entry.strip()].append("*")
    return buckets, sources


def matches(pattern, value):
    """One rule pattern against one concrete value."""
    if pattern == "*" or pattern == "":
        return True
    if pattern.endswith(" *"):                  # Bash(git *) -> prefix family
        head = pattern[:-2]
        return value == head or value.startswith(head + " ")
    if pattern.endswith("*"):                   # Bash(npm run test:*)
        return value.startswith(pattern[:-1])
    if any(ch in pattern for ch in "*?["):      # Edit(**/*.ps1)
        return fnmatch.fnmatch(value, pattern)
    return value == pattern


def verdict_for(tool, value, buckets):
    """deny -> ask -> allow -> nothing matched (which also prompts)."""
    for key, result in (("deny", WOULD_DENY), ("ask", WOULD_PROMPT),
                        ("allow", WOULD_ALLOW)):
        for pattern in buckets[key].get(tool, ()):
            if matches(pattern, value):
                return result
    return WOULD_PROMPT


def classify_call(tool, tool_input, buckets):
    """Verdict for a whole tool call, most restrictive part wins."""
    if tool in ("Bash", "PowerShell"):
        command = (tool_input or {}).get("command")
        if not isinstance(command, str) or not command.strip():
            return UNDECIDABLE, None
        parts = [p for p in _SPLIT.split(command.strip()) if p]
        if not parts:
            return UNDECIDABLE, None
        root = _ENV_PREFIX.sub("", parts[0]).split()[0] if parts[0].split() else "?"
        seen = set()
        for part in parts:
            part = _ENV_PREFIX.sub("", part).strip()
            if not part:
                continue
            seen.add(verdict_for(tool, part, buckets))
        for result in (WOULD_DENY, WOULD_PROMPT, WOULD_ALLOW):
            if result in seen:
                return result, root
        return UNDECIDABLE, root

    for key in ("file_path", "path", "notebook_path", "url", "pattern"):
        value = (tool_input or {}).get(key)
        if isinstance(value, str) and value:
            return verdict_for(tool, value, buckets), tool
    # No addressable argument: the rule set can only speak to the bare tool name.
    return verdict_for(tool, "", buckets), tool


def parse_ts(value):
    if not isinstance(value, str):
        return None
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def walk(path, buckets):
    starts, out = {}, []
    try:
        handle = open(path, "r", encoding="utf-8", errors="replace")
    except OSError:
        return out
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            when = parse_ts(record.get("timestamp"))
            denial = record.get("toolDenialKind")
            content = record.get("message", {}).get("content")
            if not isinstance(content, list):
                continue
            for block in content:
                if not isinstance(block, dict):
                    continue
                if block.get("type") == "tool_use":
                    starts[block.get("id")] = (when, block.get("name") or "?",
                                               block.get("input"))
                elif block.get("type") == "tool_result":
                    started = starts.pop(block.get("tool_use_id"), None)
                    if not started or started[0] is None or when is None:
                        continue
                    wait = (when - started[0]).total_seconds()
                    if wait < 0:
                        continue
                    result, root = classify_call(started[1], started[2], buckets)
                    out.append((started[1], wait, denial, result, root))
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--files", type=int, default=120)
    parser.add_argument("--threshold", type=float, default=20.0)
    args = parser.parse_args()

    buckets, sources = load_rules()
    counts = {k: sum(len(v) for v in buckets[k].values()) for k in buckets}
    print("rules loaded from %d source(s): deny=%d ask=%d allow=%d"
          % (sources, counts["deny"], counts["ask"], counts["allow"]))
    if counts["ask"] == 0:
        print("  WARNING: zero ask rules. The managed policy's ask list is the one")
        print("  that forces prompts here; if it did not load, this whole")
        print("  measurement understates prompting and cannot be trusted.")

    paths = sorted(glob.glob(CLAUDE_GLOB), key=os.path.getmtime)[-args.files:]
    positives, slow_completions = [], []
    verdict_tally = collections.Counter()
    miss_roots = collections.Counter()
    total = 0

    for path in paths:
        for tool, wait, denial, result, root in walk(path, buckets):
            total += 1
            verdict_tally[result] += 1
            if denial == "user-rejected":
                positives.append((tool, wait, result, root))
                if result == WOULD_ALLOW:
                    miss_roots[root or tool] += 1
            elif denial is None and wait >= args.threshold:
                slow_completions.append((tool, wait, result, root))

    print("\nread %d transcripts, %d paired calls" % (len(paths), total))
    print("verdict over all calls: %s" % dict(verdict_tally))

    print("\n" + "=" * 70)
    print("RECALL -- calls we KNOW prompted (n=%d)" % len(positives))
    print("=" * 70)
    if not positives:
        print("  no ground truth in this sample; widen --files")
        return 0
    by_verdict = collections.Counter(v for _, _, v, _ in positives)
    for verdict, count in by_verdict.most_common():
        flag = "" if verdict in (WOULD_PROMPT, WOULD_DENY) else "   <-- MISSED"
        print("  %-12s %d%s" % (verdict, count, flag))
    missed = by_verdict[WOULD_ALLOW]
    print("\n  recall: %d/%d (%.0f%%) correctly predicted to prompt"
          % (len(positives) - missed, len(positives),
             100.0 * (len(positives) - missed) / len(positives)))
    if miss_roots:
        print("  missed roots: %s" % dict(miss_roots))

    print("\n" + "=" * 70)
    print("FILTERING -- completions over %.0fs that currently false-alarm (n=%d)"
          % (args.threshold, len(slow_completions)))
    print("=" * 70)
    fb = collections.Counter(v for _, _, v, _ in slow_completions)
    for verdict, count in fb.most_common():
        print("  %-12s %-6d %s" % (verdict, count,
                                   "suppressed" if verdict == WOULD_ALLOW
                                   else "still fires"))
    survivors = len(slow_completions) - fb[WOULD_ALLOW]
    caught = sum(1 for _, w, v, _ in positives
                 if w >= args.threshold and v != WOULD_ALLOW)
    print("\n  false alarms: %d -> %d  (%.0f%% suppressed)"
          % (len(slow_completions), survivors,
             100.0 * fb[WOULD_ALLOW] / max(1, len(slow_completions))))
    print("  real prompts still caught at this threshold: %d" % caught)
    if caught:
        before = len(slow_completions) / max(1, sum(
            1 for _, w, _, _ in positives if w >= args.threshold))
        print("  false-per-real: %.0f -> %.0f" % (before, survivors / caught))
    return 0


if __name__ == "__main__":
    sys.exit(main())
