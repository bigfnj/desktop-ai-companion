#!/usr/bin/env python3
"""Backtest the AgentFlow detector against transcripts that already exist.

The detector's whole premise is that a tool call which is WAITING ON A HUMAN looks
different, in the transcript, from one that is merely slow. That is a measurable
claim and it does not need a module, a UI, a keypress, or a live prompt: every
completed call in the history has a start time, an end time, and sometimes a
recorded denial reason saying a human answered a prompt.

Ground truth comes from `toolDenialKind`, observed with four values:
    user-rejected    a human saw a prompt and said no      <- definitely prompted
    permission-rule  a settings rule denied it             <- no human involved
    automode-blocked auto mode refused it                  <- no human involved
    interrupted      the user interrupted                  <- human, but not a prompt

So `user-rejected` is the positive class. If its wait times do not separate from
ordinary completions, a stall threshold cannot tell "blocked on a prompt" from
"running a slow build", and the notify feature is built on sand.

Prints durations, tool names and counts. Never arguments, never output, never a
path or prompt text out of a transcript.

    python agentflow_backtest.py [--files N] [--threshold SECONDS]
"""

import argparse
import collections
import datetime as dt
import glob
import json
import os
import statistics
import sys

CLAUDE_GLOB = os.path.join(
    os.path.expanduser("~"), ".claude", "projects", "*", "*.jsonl")


def parse_ts(value):
    if not isinstance(value, str):
        return None
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def walk(path):
    """Yield (tool_name, wait_seconds, denial_kind_or_None) for every paired call."""
    starts = {}        # tool_use_id -> (timestamp, name)
    denial_by_id = {}  # tool_use_id -> denial kind, when the result records one
    out = []

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
                kind = block.get("type")
                if kind == "tool_use":
                    starts[block.get("id")] = (when, block.get("name") or "?")
                elif kind == "tool_result":
                    use_id = block.get("tool_use_id")
                    started = starts.pop(use_id, None)
                    if started is None or started[0] is None or when is None:
                        continue
                    wait = (when - started[0]).total_seconds()
                    if wait < 0:
                        continue  # clock skew across a resumed session
                    if denial:
                        denial_by_id[use_id] = denial
                    out.append((started[1], wait, denial_by_id.get(use_id, denial)))
    return out


def summarise(label, waits):
    if not waits:
        return "%-18s n=0" % label
    waits = sorted(waits)

    def pct(p):
        return waits[min(len(waits) - 1, int(len(waits) * p))]
    return ("%-18s n=%-6d median=%7.1fs  p90=%8.1fs  max=%9.1fs"
            % (label, len(waits), statistics.median(waits), pct(0.90), waits[-1]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--files", type=int, default=40,
                        help="how many of the most recent transcripts to read")
    parser.add_argument("--threshold", type=float, default=20.0)
    args = parser.parse_args()

    paths = sorted(glob.glob(CLAUDE_GLOB), key=os.path.getmtime)[-args.files:]
    if not paths:
        print("no transcripts found under %s" % CLAUDE_GLOB, file=sys.stderr)
        return 1

    by_kind = collections.defaultdict(list)
    tools_prompted = collections.Counter()
    pos_named, neg_named = [], []   # (tool_name, wait) kept for the sweep
    total = 0
    for path in paths:
        for name, wait, denial in walk(path):
            total += 1
            by_kind[denial or "completed"].append(wait)
            if denial == "user-rejected":
                tools_prompted[name] += 1
                pos_named.append((name, wait))
            elif denial is None:
                neg_named.append((name, wait))

    print("read %d transcripts, %d paired tool calls\n" % (len(paths), total))
    print("wait time from tool_use to tool_result, by outcome")
    print("-" * 72)
    for kind in ("completed", "user-rejected", "permission-rule",
                 "automode-blocked", "interrupted"):
        print("  " + summarise(kind, by_kind.get(kind, [])))
    for kind in sorted(set(by_kind) - {"completed", "user-rejected",
                                       "permission-rule", "automode-blocked",
                                       "interrupted"}):
        print("  " + summarise(kind, by_kind[kind]))

    positives = by_kind.get("user-rejected", [])
    negatives = by_kind.get("completed", [])
    if not positives:
        print("\nNO user-rejected calls in this sample -- no ground truth, so this "
              "run proves nothing either way. Widen --files.")
        return 0

    print("\nseparation at threshold=%.0fs" % args.threshold)
    print("-" * 72)
    caught = sum(1 for w in positives if w >= args.threshold)
    false = sum(1 for w in negatives if w >= args.threshold)
    print("  human-answered prompts flagged : %d/%d (%.0f%%)"
          % (caught, len(positives), 100.0 * caught / len(positives)))
    print("  ordinary completions flagged   : %d/%d (%.1f%%)"
          % (false, len(negatives), 100.0 * false / max(1, len(negatives))))
    if negatives:
        print("  false alarms per real prompt   : %.1f"
              % (false / max(1, caught)))

    print("\n  NOTE: a 'completed' call that sat above the threshold is not "
          "necessarily\n  a false alarm -- it may be a prompt the user ACCEPTED, "
          "which leaves no\n  toolDenialKind. This bound is therefore pessimistic, "
          "and the real\n  precision can only be higher.")

    if tools_prompted:
        print("\ntools that actually prompted a human")
        print("-" * 72)
        for name, count in tools_prompted.most_common(10):
            print("  %-18s %d" % (name, count))

    # ---- can the false-alarm rate be rescued? ----------------------------
    # Time alone is not enough. Two cheap discriminators are available without
    # reading a single pixel: restrict to the tools that can actually prompt, and
    # raise the bar. Sweep both so the trade is visible rather than asserted.
    print("\n\nsweep: threshold x tool restriction")
    print("=" * 72)
    prompting_tools = set(tools_prompted)
    for label, keep in (("all tools", None),
                        ("prompt-capable only", prompting_tools)):
        pos = [w for n, w in pos_named if keep is None or n in keep]
        neg = [w for n, w in neg_named if keep is None or n in keep]
        print("\n%s (positives=%d, negatives=%d)" % (label, len(pos), len(neg)))
        print("  %-10s %-14s %-16s %s"
              % ("thresh", "prompts caught", "completions over", "false/real"))
        for thresh in (20, 45, 90, 180, 300):
            caught_n = sum(1 for w in pos if w >= thresh)
            false_n = sum(1 for w in neg if w >= thresh)
            ratio = ("%.1f" % (false_n / caught_n)) if caught_n else "n/a"
            print("  %-10d %-14s %-16s %s"
                  % (thresh,
                     "%d/%d" % (caught_n, len(pos)),
                     "%d (%.1f%%)" % (false_n, 100.0 * false_n / max(1, len(neg))),
                     ratio))
    return 0


if __name__ == "__main__":
    sys.exit(main())
