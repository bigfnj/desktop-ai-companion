#!/usr/bin/env python3
"""Measure the allow-list join: does knowing the rules kill the false alarms?

The stall detector cannot tell "blocked on a prompt" from "running a slow build".
The join adds a second, independent question about the same call -- would this
command have prompted AT ALL? -- answered from the permission rules already on
disk. This measures whether that question is worth asking.

Two things are measured, and the first matters more:

  RECALL   Of the calls a PERMISSION RULE blocked (toolDenialKind ==
           permission-rule -- see the ground-truth note below, the field has five
           values and reading the wrong one inverted this measurement once), how
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
import functools
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

# =====================================================================================
# GROUND TRUTH. Corrected 2026-09-17, and the correction matters more than the numbers.
#
# This harness originally treated `toolDenialKind == "user-rejected"` as "a prompt
# happened", and measured the rule matcher's recall against it. That is the wrong field
# value. The transcripts carry FIVE, measured over the 120 most recent:
#
#   permission-rule   30   blocked BY A PERMISSION RULE  <- the rule matcher's truth
#   automode-blocked  25   blocked by the auto-mode model-side classifier
#   user-rejected     16   the human declined, rule-driven or not
#   interrupted        3   not a prompt
#   cancelled          1   not a prompt
#
# Using `user-rejected` was wrong in both directions. It ignored `permission-rule`, the
# one class the matcher is supposed to predict and nearly twice as numerous, and it
# counted human refusals of calls the rules ALLOW -- which is why the misses clustered
# on `echo` and `cd`, commands the managed allow-list covers and a human declined
# anyway. No rule matcher can predict a person changing their mind.
#
# `automode-blocked` existing as its own label is the bigger find: the transcript states
# outright which denials came from the auto-mode classifier rather than the rules. The
# 0.09%-precision-in-auto finding was inferred from rule evaluation; this measures it.
# =====================================================================================

DENIAL_BY_RULE = "permission-rule"        # the rule matcher is accountable for these
DENIAL_BY_AUTOMODE = "automode-blocked"   # a model-side gate; rules cannot predict it
DENIAL_BY_HUMAN = "user-rejected"         # may or may not have been rule-driven
DENIAL_NOT_A_PROMPT = ("interrupted", "cancelled")

# A leading VAR=value prefix is stripped before matching, per the same guidance.
_ENV_PREFIX = re.compile(r"^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)+")
_RULE = re.compile(r"^([A-Za-z_]+)\((.*)\)$")


# =====================================================================================
# Compound-command splitting.
#
# Claude Code evaluates each sub-command of a compound separately -- that is the entire
# premise of permission-wildcarding's shell-style guidance -- so a chain is only
# "allowed" if EVERY part is. Getting the split wrong therefore corrupts the join in
# both directions: an over-split invents a root that was never a command, and an
# under-split hides a part that would have prompted.
#
# This WAS `re.split(r"\s*(?:&&|\|\||[;|])\s*")`, which is wrong in at least four ways
# that appear in real transcripts. It ignored a bare `&`, split on a `;` or `|` inside
# quotes, split inside a heredoc body (which is data, not commands), and split inside a
# `$(...)` substitution. Measured consequence, recorded in this directory's README: the
# misses clustered on roots `echo`, `cd` and `&`, and that accounted for most of the
# recall failure -- the failure mode the module cannot afford, because a miss means the
# companion stays silent while the agent sits blocked.
#
# Ported from the correct implementation in the sibling permission-wildcarding project
# (`src/auto-learn.js`: splitSegmentsDetailed, normalizeShell, maskHeredocBodies,
# isCommentStart, isBashAmpersandSeparator) rather than rewritten, because that one is
# already exercised against both agents' full history there. `--difftest` checks this
# port against that original by running it under node, so the two cannot drift silently.
# =====================================================================================

_PS_SHELL = re.compile(r"^(?:powershell|pwsh|ps)$")
_POSIX_SHELL = re.compile(r"^(?:bash|sh|zsh|fish|ksh|dash)$")

# An unquoted heredoc delimiter must be upper-case -- the real-world convention -- so
# `echo "a << b"` and the `<<<` here-string keep their ordinary handling.
_HEREDOC_OPENER = re.compile(
    r"(?<!<)<<-?[ \t]*(?:(['\"])([A-Za-z_][A-Za-z0-9_]*)\1|([A-Z][A-Z0-9_]*))")


def normalize_shell(shell, tool=None):
    """'powershell' or 'bash'. Mirrors normalizeShell(shell, tool)."""
    explicit = str(shell or "").strip().lower()
    tool_name = str(tool or "").strip().lower()
    if _PS_SHELL.match(explicit):
        return "powershell"
    if _POSIX_SHELL.match(explicit):
        return "bash"
    if explicit:
        return explicit
    if "powershell" in tool_name or tool_name == "pwsh":
        return "powershell"
    if tool_name == "bash":
        return "bash"
    if ("shell_command" in tool_name or "exec_command" in tool_name
            or tool_name == "shell"):
        return "powershell" if sys.platform == "win32" else "bash"
    return "bash"


def mask_heredoc_bodies(text):
    """Blank out heredoc bodies, keeping line count, so they cannot be segmented.

    A heredoc body is data, not commands, and it can sit inside quotes or a command
    substitution (`git commit -m "$(cat <<'EOF' ... EOF)"`), so masking it up front is
    simpler and safer than carrying it as parser state.
    """
    if "<<" not in text:
        return text
    output, pending = [], []
    for line in text.split("\n"):
        if pending:
            output.append("")
            if line.rstrip("\r").strip() == pending[0]:
                pending.pop(0)
            continue
        output.append(line)
        for match in _HEREDOC_OPENER.finditer(line):
            pending.append(match.group(2) or match.group(3))
    return "\n".join(output)


def _at(text, index):
    """text[index], or '' past either end. JS returns undefined; Python would wrap."""
    if index < 0 or index >= len(text):
        return ""
    return text[index]


def _is_comment_start(text, index):
    return text[index] == "#" and (index == 0 or _at(text, index - 1).isspace())


def _is_bash_ampersand_separator(text, index):
    """A bare `&` backgrounds a job; `&>` and `2>&1` are redirections, not separators."""
    if _at(text, index + 1) == ">":
        return False
    previous = index - 1
    while previous >= 0 and text[previous].isspace():
        previous -= 1
    return _at(text, previous) != ">"


def split_segments_detailed(command, shell=None):
    """[(value, separator)] for top-level separators only.

    Quoted and grouped constructs stay intact, so a caller can reject them rather than
    inventing a malformed root. `separator` is the one that PRECEDED the segment, and is
    None for the first.
    """
    mode = normalize_shell(shell)
    raw = "" if command is None else str(command)
    text = raw if mode == "powershell" else mask_heredoc_bodies(raw)

    segments = []
    current, quote, depth, separator = [], None, 0, None

    def flush():
        value = "".join(current).strip()
        del current[:]
        if value:
            segments.append((value, separator))

    i, n = 0, len(text)
    while i < n:
        ch = text[i]
        nxt = _at(text, i + 1)

        if quote == "single":
            current.append(ch)
            if mode == "powershell" and ch == "'" and nxt == "'":
                i += 1
                current.append(text[i])
            elif ch == "'":
                quote = None
            i += 1
            continue

        if quote == "double":
            current.append(ch)
            if (mode == "powershell" and ch == "`") or (mode != "powershell" and ch == "\\"):
                if i + 1 < n:
                    i += 1
                    current.append(text[i])
            elif ch == '"':
                quote = None
            i += 1
            continue

        # PowerShell block comment <# ... #>
        if mode == "powershell" and ch == "<" and nxt == "#":
            end = text.find("#>", i + 2)
            current.append(" ")
            i = (n if end == -1 else end + 1) + 1
            continue

        # PowerShell here-string @" ... "@ / @' ... '@
        if mode == "powershell" and ch == "@" and (nxt == '"' or nxt == "'"):
            terminator = '"@' if nxt == '"' else "'@"
            end = text.find(terminator, i + 2)
            current.append(" ")
            i = n if end == -1 else end + len(terminator)
            continue

        if _is_comment_start(text, i):
            while i < n and text[i] != "\n" and text[i] != "\r":
                i += 1
            flush()
            separator = "\n"
            if _at(text, i) == "\r" and _at(text, i + 1) == "\n":
                i += 1
            i += 1
            continue

        if ch == "'":
            quote = "single"
            current.append(ch)
            i += 1
            continue
        if ch == '"':
            quote = "double"
            current.append(ch)
            i += 1
            continue
        if (mode == "powershell" and ch == "`") or (mode != "powershell" and ch == "\\"):
            current.append(ch)
            if i + 1 < n:
                i += 1
                current.append(text[i])
            i += 1
            continue

        if ch in "([{":
            depth += 1
            current.append(ch)
            i += 1
            continue
        if ch in ")]}":
            depth = max(0, depth - 1)
            current.append(ch)
            i += 1
            continue

        if depth == 0:
            length = 0
            if ch == "\r" or ch == "\n" or ch == ";":
                length = 2 if (ch == "\r" and nxt == "\n") else 1
            elif ch == "&" and nxt == "&":
                length = 2
            elif ch == "|" and (nxt == "|" or nxt == "&"):
                length = 2
            elif ch == "|":
                length = 1
            elif (mode != "powershell" and ch == "&"
                  and _is_bash_ampersand_separator(text, i)):
                length = 1
            if length:
                flush()
                separator = "\n" if ch in "\r\n" else text[i:i + length]
                i += length
                continue

        current.append(ch)
        i += 1

    flush()
    return segments


def split_command_segments(command, shell=None):
    """Just the segment texts, which is all the join needs."""
    return [value for value, _separator in split_segments_detailed(command, shell)]


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


_COMMAND_TOOLS = ("Bash", "PowerShell")


def load_one_tier(path):
    """(deny, ask, allow) for a SINGLE settings file, unmerged.

    load_rules() merges the tiers because that is what the agent evaluates. This does not,
    because the question below is about one tier specifically: a rule in the MANAGED file is
    one the user's own `allow` cannot reach and auto-accept cannot override.
    """
    buckets = {k: collections.defaultdict(list) for k in ("deny", "ask", "allow")}
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return buckets, 0
    count = 0
    perms = data.get("permissions") or {}
    for key in buckets:
        for entry in perms.get(key) or []:
            entry = entry.strip()
            match = _RULE.match(entry)
            if match:
                buckets[key][match.group(1)].append(match.group(2))
            else:
                buckets[key][entry].append("*")
            count += 1
    return buckets, count


def addressable_parts(tool, tool_input):
    """The strings a rule can match against, mirroring classify_call's decomposition."""
    if tool in _COMMAND_TOOLS:
        command = (tool_input or {}).get("command")
        if not isinstance(command, str) or not command.strip():
            return []
        parts = split_command_segments(
            command, "powershell" if tool == "PowerShell" else "bash")
        out = []
        for part in parts:
            part = _ENV_PREFIX.sub("", part).strip()
            if part:
                out.append(part)
        return out
    for key in ("file_path", "path", "notebook_path", "url", "pattern"):
        value = (tool_input or {}).get(key)
        if isinstance(value, str) and value:
            return [value]
    return [""]


def blocked_by_one_tier(tool, tool_input, tier):
    """True when an ask or deny pattern IN THAT TIER matches any addressable part.

    Allow is ignored on purpose: an allow in the managed file is not a reason to expect a
    prompt, and a managed `ask` is not cancelled by a user `allow` (deny -> ask -> allow).
    """
    for part in addressable_parts(tool, tool_input):
        for key in ("deny", "ask"):
            for pattern in tier[key].get(tool, ()):
                if matches(tool, pattern, part):
                    return True
    return False


def tier_report(files, buckets):
    """Does the auto-mode stand-down throw away prompts a MANAGED rule guarantees?

    The stand-down is an allow-list of exactly `default`, on a measured ~0.4% precision
    everywhere else. The objection worth testing: a managed `ask` beats auto-accept, so a curl
    call blocks in auto mode as well, and standing down there discards a CERTAINTY rather than a
    prediction.

    Two predictors over the same corpus and the same ground truth
    (`toolDenialKind == "permission-rule"`):

      WIDE    what the module ships: any WouldPrompt or WouldDeny from the merged rules
      MANAGED only a call an ask/deny rule in remote-settings.json matches
    """
    managed, managed_rules = load_one_tier(MANAGED_SETTINGS)
    _user, user_rules = load_one_tier(USER_SETTINGS)
    print("rules: %d user, %d managed" % (user_rules, managed_rules))
    managed_tools = sorted(set(list(managed["ask"]) + list(managed["deny"])))
    print("managed ask/deny tools: %s" % (", ".join(managed_tools) or "(none)"))
    print("transcripts: %d\n" % len(files))

    stats = collections.defaultdict(collections.Counter)
    hits = collections.defaultdict(list)
    for path in files:
        for tool, _wait, denial, result, _root, mode, tool_input in walk(path, buckets):
            counter = stats[mode or "unknown"]
            counter["calls"] += 1
            truth = denial == DENIAL_BY_RULE
            wide = result in (WOULD_PROMPT, WOULD_DENY)
            narrow = blocked_by_one_tier(tool, tool_input, managed)
            if truth:
                counter["real"] += 1
            if wide:
                counter["wide"] += 1
                counter["wide_hit"] += 1 if truth else 0
            if narrow:
                counter["narrow"] += 1
                if truth:
                    counter["narrow_hit"] += 1
                    if len(hits[mode]) < 4:
                        hits[mode].append((tool, str(tool_input)[:64]))

    print("%-12s %8s %5s %9s %8s %8s %8s %8s"
          % ("mode", "calls", "real", "wide", "wide P", "managed", "mgd P", "mgd R"))
    for mode in sorted(stats, key=lambda m: -stats[m]["calls"]):
        c = stats[mode]
        wp = (100.0 * c["wide_hit"] / c["wide"]) if c["wide"] else 0.0
        mp = (100.0 * c["narrow_hit"] / c["narrow"]) if c["narrow"] else 0.0
        mr = (100.0 * c["narrow_hit"] / c["real"]) if c["real"] else 0.0
        print("%-12s %8d %5d %9d %7.2f%% %8d %7.1f%% %7.0f%%"
              % (mode, c["calls"], c["real"], c["wide"], wp, c["narrow"], mp, mr))
    print()
    for mode in sorted(hits):
        for tool, shown in hits[mode]:
            print("  %-11s managed rule caught a real block: %-8s %s" % (mode, tool, shown))


@functools.lru_cache(maxsize=8192)
def _compiled(pattern):
    """`*` is the only wildcard; every other metacharacter is a literal."""
    return re.compile("^" + re.escape(pattern).replace("\\*", ".*") + "$")


def matches(tool, pattern, value):
    """One rule pattern against one concrete value, with Claude Code's two documented
    command-specifier rules.

    Both were missing and both bite on this box's real settings (702 rules, user +
    managed), which is why they are spelled out here rather than left to a reader:

      1. `Tool(cmd:*)` is an equivalent spelling of `Tool(cmd *)`. **86 rules use the
         colon form.** Without this normalization they fall through to a prefix test
         against the literal text `cmd:`, which matches no real command at all -- so 86
         allow rules were silently inert and this harness over-reported would-prompt.
      2. A trailing `*` preceded by a space also matches the BARE command, so
         `Bash(ls *)` matches `ls`. That holds only while the trailing `*` is the rule's
         ONLY wildcard; a rule with a second `*` does not get the allowance. One rule
         here is in that second category, few enough to be missed by inspection.

    Kept deliberately parallel to modules/AgentFlow/PermissionRules.cs, which is the
    shipping copy, and to permission-wildcarding/src/permission-match.js, which is the
    original. Three copies of one matcher drift; changing one means changing all three.
    """
    if tool in _COMMAND_TOOLS and pattern.endswith(":*"):
        pattern = pattern[:-2] + " *"
    if pattern == "*" or pattern == "":
        return True
    if (tool in _COMMAND_TOOLS and pattern.count("*") == 1
            and pattern.endswith(" *")):
        head = pattern[:-2]
        return value == head or value.startswith(head + " ")
    return bool(_compiled(pattern).match(value))


def verdict_for(tool, value, buckets):
    """deny -> ask -> allow -> nothing matched (which also prompts)."""
    for key, result in (("deny", WOULD_DENY), ("ask", WOULD_PROMPT),
                        ("allow", WOULD_ALLOW)):
        for pattern in buckets[key].get(tool, ()):
            if matches(tool, pattern, value):
                return result
    return WOULD_PROMPT


def classify_call(tool, tool_input, buckets):
    """Verdict for a whole tool call, most restrictive part wins."""
    if tool in ("Bash", "PowerShell"):
        command = (tool_input or {}).get("command")
        if not isinstance(command, str) or not command.strip():
            return UNDECIDABLE, None
        # The tool name is what says which shell's quoting rules apply, and it changes
        # the answer: a backtick escapes in PowerShell and a backslash does in bash.
        parts = split_command_segments(command, "powershell" if tool == "PowerShell"
                                       else "bash")
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
    """[(tool, wait_seconds, denial, verdict, root, permission_mode, tool_input)] per transcript.

    The permission mode is CARRIED FORWARD positionally, by file order, and that is not
    a shortcut. Measured 2026-09-17 over the 60 most recent transcripts:

      * `permissionMode` rides on `type:"user"` records -- 702 of them -- and never on
        a tool call, so a call's mode has to come from the last user turn before it.
      * a runtime flip emits its own `type:"permission-mode"` record whose ONLY keys
        are `permissionMode`, `sessionId` and `type`. It carries **no timestamp**, so
        anything that sorts records by time drops every mode change on the floor. File
        order is the only ordering that works.
      * distribution across that sample: auto 671, acceptEdits 26, default 6, plan 3.

    This harness did not read the field at all until now, which meant the mode split in
    this directory's README -- the measurement the whole "AgentFlow is a default-mode
    feature" conclusion rests on -- was attributed by hand and not reproducible from
    committed code. That is the gap this closes.

    The mode is attributed at the moment the call STARTS, not when its result arrives,
    because that is when the permission decision was taken.
    """
    starts, out = {}, []
    mode = None
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

            # BEFORE the content check below, because a `permission-mode` record has no
            # message and no content, so reading the mode afterwards would never see one.
            declared = record.get("permissionMode")
            if isinstance(declared, str) and declared:
                mode = declared

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
                                               block.get("input"), mode)
                elif block.get("type") == "tool_result":
                    started = starts.pop(block.get("tool_use_id"), None)
                    if not started or started[0] is None or when is None:
                        continue
                    wait = (when - started[0]).total_seconds()
                    if wait < 0:
                        continue
                    result, root = classify_call(started[1], started[2], buckets)
                    out.append((started[1], wait, denial, result, root,
                                started[3] or "unknown", started[2]))
    return out


# =====================================================================================
# Splitter verification.
#
# Every case below is one the OLD regex splitter got wrong, or one that guards a rule
# the port could plausibly drop. The ones marked WITNESS are the regressions: if the
# splitter is reverted to `re.split`, those are the assertions that fail, so a passing
# suite with them present means something.
# =====================================================================================

SPLITTER_CASES = (
    # (label, command, shell, expected segments)
    ("WITNESS a bare & backgrounds a job and IS a separator",
     "sleep 1 & echo done", "bash", ["sleep 1", "echo done"]),
    ("WITNESS 2>&1 is a redirection, NOT a separator",
     "make 2>&1 | tee log", "bash", ["make 2>&1", "tee log"]),
    ("&> is a redirection, not a separator",
     "cmd &> out.txt", "bash", ["cmd &> out.txt"]),
    ("WITNESS a ; inside single quotes does not split",
     "echo 'a;b'", "bash", ["echo 'a;b'"]),
    ("WITNESS a | inside double quotes does not split",
     'grep "a|b" file', "bash", ['grep "a|b" file']),
    ("WITNESS a heredoc body is data and is never segmented",
     "cat <<'EOF'\na; b && c\nEOF", "bash", ["cat <<'EOF'"]),
    ("WITNESS a $(...) substitution is not segmented",
     "echo $(date; ls)", "bash", ["echo $(date; ls)"]),
    ("a backslash escapes the next character in bash",
     "echo a\\;b", "bash", ["echo a\\;b"]),
    ("&& splits", "a && b", "bash", ["a", "b"]),
    ("|| splits", "a || b", "bash", ["a", "b"]),
    ("|& splits", "a |& b", "bash", ["a", "b"]),
    ("a newline splits", "a\nb", "bash", ["a", "b"]),
    ("CRLF is ONE separator, not two",
     "a\r\nb", "bash", ["a", "b"]),
    ("a comment runs to end of line and its contents never split",
     "ls # a; b && c", "bash", ["ls"]),
    ("a # mid-token is not a comment",
     "git show HEAD#1", "bash", ["git show HEAD#1"]),
    ("WITNESS a PowerShell here-string is opaque",
     "Set-Content f @'\na; b\n'@", "powershell", ["Set-Content f"]),
    # The comment collapses to a single space and the text either side keeps its own,
    # so three spaces is correct rather than sloppy. Verified against the JS original,
    # which produces the same string; the hand-written expectation here was wrong first.
    ("a PowerShell block comment is opaque",
     "ls <# a; b #> -Force", "powershell", ["ls   -Force"]),
    ("a backtick escapes in PowerShell, a backslash does not",
     "echo a`;b", "powershell", ["echo a`;b"]),
    ("a Windows path's backslashes survive PowerShell splitting",
     r"Get-Item C:\temp\x; ls", "powershell", [r"Get-Item C:\temp\x", "ls"]),
    ("a doubled single quote inside a PowerShell literal does not end it",
     "echo 'it''s; fine'", "powershell", ["echo 'it''s; fine'"]),
    ("nested braces keep a block intact",
     "foreach ($x in $y) { a; b }", "powershell",
     ["foreach ($x in $y) { a; b }"]),
    ("empty input yields no segments", "", "bash", []),
    ("whitespace only yields no segments", "   \n  ", "bash", []),
    ("None is not a crash", None, "bash", []),
)


def splitter_selftest():
    failures = 0
    print("splitter self-test")
    for label, command, shell, expected in SPLITTER_CASES:
        got = split_command_segments(command, shell)
        if got == expected:
            print("  PASS %s" % label)
        else:
            failures += 1
            print("  FAIL %s" % label)
            print("       want %r" % (expected,))
            print("       got  %r" % (got,))
    print("\n%d/%d passed" % (len(SPLITTER_CASES) - failures, len(SPLITTER_CASES)))
    return 1 if failures else 0


JS_PROJECT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.dirname(os.path.abspath(__file__))))), "permission-wildcarding")
JS_BRIDGE = r"""
const fs = require('fs');
const path = process.argv[2];
const mod = require(process.argv[3]);
const cases = JSON.parse(fs.readFileSync(path, 'utf8'));
const out = cases.map((c) => mod.splitCommandSegments(c.command, c.shell));
process.stdout.write(JSON.stringify(out));
"""


def splitter_difftest(files):
    """Compare this port against the JS original it was ported from.

    Two implementations of one algorithm drift, and the drift is invisible: both
    produce a plausible segment list. So the original is run under node over the same
    inputs and the outputs must be identical.

    A mismatch prints the case INDEX and both segment counts, never the command text --
    the corpus is real transcript commands and this harness's standing rule is that
    command text is read to evaluate it and never printed.
    """
    entry = os.path.join(JS_PROJECT, "src", "auto-learn.js")
    if not os.path.isfile(entry):
        print("DEGRADED: cannot find the JS original at %s" % entry)
        print("  This test compares against it and proves nothing without it.")
        return 1

    cases = [{"command": c, "shell": s} for _l, c, s, _e in SPLITTER_CASES
             if c is not None]
    harvested = 0
    for path in sorted(glob.glob(CLAUDE_GLOB), key=os.path.getmtime)[-files:]:
        try:
            handle = open(path, "r", encoding="utf-8", errors="replace")
        except OSError:
            continue
        with handle:
            for line in handle:
                try:
                    record = json.loads(line)
                except ValueError:
                    continue
                content = record.get("message", {}).get("content")
                if not isinstance(content, list):
                    continue
                for block in content:
                    if not isinstance(block, dict) or block.get("type") != "tool_use":
                        continue
                    name = block.get("name")
                    if name not in ("Bash", "PowerShell"):
                        continue
                    command = (block.get("input") or {}).get("command")
                    if isinstance(command, str) and command.strip():
                        cases.append({
                            "command": command,
                            "shell": "powershell" if name == "PowerShell" else "bash",
                        })
                        harvested += 1

    import subprocess
    import tempfile
    handle, case_path = tempfile.mkstemp(suffix=".json")
    os.close(handle)
    bridge_path = case_path + ".js"
    try:
        with open(case_path, "w", encoding="utf-8") as out:
            json.dump(cases, out)
        with open(bridge_path, "w", encoding="utf-8") as out:
            out.write(JS_BRIDGE)
        try:
            # Bytes, NOT text=True. Real commands carry non-ASCII, and text=True
            # decodes with the locale codec -- cp1252 here -- which dies on the first
            # such byte. Same failure as the repo's own "pin StandardOutputEncoding"
            # source invariant, which exists because it shipped once as OCR mojibake.
            proc = subprocess.run(
                ["node", bridge_path, case_path, entry.replace("\\", "/")],
                capture_output=True, timeout=300)
        except (OSError, subprocess.SubprocessError) as exc:
            print("DEGRADED: could not run node (%s)." % exc)
            print("  This test compares against the JS original and proves nothing")
            print("  without it. It is NOT a pass.")
            return 1
        if proc.returncode != 0:
            print("DEGRADED: node exited %d" % proc.returncode)
            print((proc.stderr or b"").decode("utf-8", "replace")[-600:])
            return 1
        theirs = json.loads(proc.stdout.decode("utf-8", "strict"))
    finally:
        for path in (case_path, bridge_path):
            try:
                os.remove(path)
            except OSError:
                pass

    print("differential: %d cases (%d labelled + %d harvested from transcripts)"
          % (len(cases), len(cases) - harvested, harvested))
    if harvested == 0:
        print("DEGRADED: harvested ZERO real commands, so this ran only the same")
        print("  handful of cases the self-test already covers. Not a pass.")
        return 1

    mismatches = 0
    for index, case in enumerate(cases):
        mine = split_command_segments(case["command"], case["shell"])
        if mine != theirs[index]:
            mismatches += 1
            if mismatches <= 10:
                print("  MISMATCH case %d (%s): port produced %d segment(s), "
                      "original produced %d" % (index, case["shell"], len(mine),
                                                len(theirs[index])))
    if mismatches:
        print("\n%d/%d MISMATCHED. The port and the original disagree." % (
            mismatches, len(cases)))
        return 1
    print("\nall %d agree with the JS original." % len(cases))
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--files", type=int, default=120)
    parser.add_argument("--threshold", type=float, default=20.0)
    parser.add_argument("--selftest", action="store_true",
                        help="verify the compound-command splitter")
    parser.add_argument("--difftest", action="store_true",
                        help="compare the splitter against the JS original under node")
    parser.add_argument("--tiers", action="store_true",
                        help="score a MANAGED-tier-only predictor against the wide one, per mode")
    args = parser.parse_args()

    if args.selftest:
        return splitter_selftest()
    if args.difftest:
        return splitter_difftest(args.files)

    buckets, sources = load_rules()
    if args.tiers:
        tier_report(sorted(glob.glob(CLAUDE_GLOB), key=os.path.getmtime)[-args.files:], buckets)
        return
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

    by_mode = collections.defaultdict(lambda: collections.Counter())
    by_denial = collections.defaultdict(lambda: collections.Counter())

    for path in paths:
        for tool, wait, denial, result, root, mode, _input in walk(path, buckets):
            total += 1
            verdict_tally[result] += 1
            bucket = by_mode[mode]
            bucket["calls"] += 1
            if result in (WOULD_PROMPT, WOULD_DENY):
                bucket["wouldPrompt"] += 1
            if denial == DENIAL_BY_RULE:
                # The only class the rule matcher is accountable for.
                bucket["realPrompts"] += 1
                positives.append((tool, wait, result, root, mode))
                if result == WOULD_ALLOW:
                    bucket["missed"] += 1
                    miss_roots[root or tool] += 1
            if denial is not None and denial not in DENIAL_NOT_A_PROMPT:
                by_denial[denial]["n"] += 1
                if result in (WOULD_PROMPT, WOULD_DENY):
                    by_denial[denial]["predicted"] += 1
            elif denial is None and wait >= args.threshold:
                slow_completions.append((tool, wait, result, root))

    print("\nread %d transcripts, %d paired calls" % (len(paths), total))
    print("verdict over all calls: %s" % dict(verdict_tally))

    # The headline finding: the join is worth having in `default` and worthless in
    # `auto`, where a model-side classifier sits in front of the rules. Reproduced from
    # the transcripts rather than attributed by hand.
    print("\n" + "=" * 70)
    print("BY PERMISSION MODE -- does the rule set still predict anything?")
    print("=" * 70)
    print("  %-12s %8s %12s %6s %13s %11s %9s"
          % ("mode", "calls", "wouldPrompt", "%", "realPrompts", "precision",
             "recall"))
    for mode in sorted(by_mode, key=lambda m: -by_mode[m]["calls"]):
        row = by_mode[mode]
        predicted, real, missed = row["wouldPrompt"], row["realPrompts"], row["missed"]
        share = (100.0 * predicted / row["calls"]) if row["calls"] else 0.0
        precision = ("%.2f%%" % (100.0 * real / predicted)) if predicted else "-"
        recall = ("%.0f%%" % (100.0 * (real - missed) / real)) if real else "-"
        print("  %-12s %8d %12d %5.0f%% %13d %11s %9s"
              % (mode, row["calls"], predicted, share, real, precision, recall))
    # Recall HAS to be read per mode. The rule join is only ever claimed to work
    # outside auto, and this corpus is ~96% auto, so a single pooled recall figure
    # measures the rules against a population where a model-side classifier -- not the
    # rules -- decided every prompt. Pooling them understates the feature by
    # construction, which is a different mistake from the feature not working.
    if "unknown" in by_mode:
        print("\n  'unknown' means no user turn preceded the call in that transcript,")
        print("  so no mode could be carried forward. It is not a mode the agent has.")

    print("\n" + "=" * 70)
    print("BY DENIAL KIND -- what the transcript says actually caused each block")
    print("=" * 70)
    print("  %-18s %6s %12s %9s   %s"
          % ("kind", "n", "wouldPrompt", "hit-rate", "whose job it is"))
    labels = {
        DENIAL_BY_RULE: "the rule matcher's -- this is the number that judges it",
        DENIAL_BY_AUTOMODE: "a model-side gate's; rules cannot predict these",
        DENIAL_BY_HUMAN: "nobody's; a human may decline a call the rules allow",
    }
    for kind in sorted(by_denial, key=lambda k: -by_denial[k]["n"]):
        row = by_denial[kind]
        rate = ("%.0f%%" % (100.0 * row["predicted"] / row["n"])) if row["n"] else "-"
        print("  %-18s %6d %12d %9s   %s"
              % (kind, row["n"], row["predicted"], rate,
                 labels.get(kind, "not a prompt")))
    if not by_denial.get(DENIAL_BY_RULE, {}).get("n"):
        print("\n  DEGRADED: zero permission-rule denials in this sample, so the one")
        print("  class the matcher is accountable for is absent and its recall is")
        print("  unmeasured here. Widen --files.")

    print("\n" + "=" * 70)
    print("RECALL -- calls a PERMISSION RULE blocked (n=%d)" % len(positives))
    print("=" * 70)
    if not positives:
        print("  no ground truth in this sample; widen --files")
        return 0
    by_verdict = collections.Counter(v for _, _, v, _, _ in positives)
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
    caught = sum(1 for _, w, v, _, _ in positives
                 if w >= args.threshold and v != WOULD_ALLOW)
    print("\n  false alarms: %d -> %d  (%.0f%% suppressed)"
          % (len(slow_completions), survivors,
             100.0 * fb[WOULD_ALLOW] / max(1, len(slow_completions))))
    print("  real prompts still caught at this threshold: %d" % caught)
    if caught:
        before = len(slow_completions) / max(1, sum(
            1 for _, w, _, _, _ in positives if w >= args.threshold))
        print("  false-per-real: %.0f -> %.0f" % (before, survivors / caught))
    return 0


if __name__ == "__main__":
    sys.exit(main())
