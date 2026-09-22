#!/usr/bin/env python3
"""Mutation harness for AgentFlow's self-test. A guard nobody has seen fail is a guess.

Each case below breaks exactly ONE promise the module's self-test asserts, rebuilds the
module, runs the self-test, and requires it to fail NAMING THE RIGHT ASSERTION. A case
that survives means the self-test does not actually cover that promise; a case that fails
on some other assertion means the suite is coupled in a way nobody intended.

Modelled on tests/mutate-diagnostics.py, and it inherits that file's two hard-won rules
plus one more that came from a harness which is no longer in the tree:

  BASELINE FROM THE WORKING TREE, NEVER FROM GIT. A previous harness in this repo restored
  with `git checkout --`, so every FIRED it printed was measured against a file whose code
  under test no longer existed. The baseline here is read into memory before anything is
  touched, and restore writes that copy back.

  EVERY PATTERN MUST MATCH EXACTLY ONCE. A pattern that silently matches nothing is a
  no-op, and a no-op mutation always looks like a passing test.

  PROVE THE CODE UNDER TEST WAS REBUILT AND RAN. The BUG-002 harness reported 0/7 FIRED
  because it rebuilt the HOST while the code under test compiles into a module DLL. A clean
  0/N is a red flag, never a result. So this builds modules/AgentFlow/AgentFlow.csproj --
  not build.ps1 -- asserts the output DLL's timestamp ADVANCED, and asserts that a WITNESS
  label from the module's own probe appears in the report before trusting any verdict.

    python tests/mutate-agentflow.py [--only=<substring>]
"""

import argparse
import io
import os
import subprocess
import sys
import time

# The backslash is built from a code point rather than written as an escape: this file is edited
# by patch scripts, and a literal \\ does not survive every pipeline that has touched it.
BS = chr(92)

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODULE_DIR = os.path.join(REPO, "modules", "AgentFlow")
CSPROJ = os.path.join(MODULE_DIR, "AgentFlow.csproj")
DLL = os.path.join(REPO, "build", "DesktopAICompanionPortable", "bin", "Release", "x64",
                   "modules", "agentflow", "AgentFlow.dll")
EXE = os.path.join(REPO, "build", "DesktopAICompanionPortable", "bin", "Release", "x64",
                   "DesktopAICompanion.exe")
MARKER = os.path.join(os.environ.get("TEMP", "."), "dp-module-agentflow-selftest.txt")

SPLITTER = os.path.join(MODULE_DIR, "CommandSplitter.cs")
RULES = os.path.join(MODULE_DIR, "PermissionRules.cs")
DETECTOR = os.path.join(MODULE_DIR, "BlockedDetector.cs")
BUDGET = os.path.join(MODULE_DIR, "NotifyBudget.cs")
MODULE = os.path.join(MODULE_DIR, "AgentFlowModule.cs")
READER = os.path.join(MODULE_DIR, "TranscriptReader.cs")
CDP = os.path.join(MODULE_DIR, "CdpApprover.cs")
PROMPTOPTS = os.path.join(MODULE_DIR, "PromptOptions.cs")
BUDGETPRESS = os.path.join(MODULE_DIR, "PressBudget.cs")
DOT = os.path.join(MODULE_DIR, "StatusDot.cs")
MODE = os.path.join(MODULE_DIR, "AgentMode.cs")
QUIPS = os.path.join(MODULE_DIR, "Quips.cs")
FEED = os.path.join(MODULE_DIR, "ApprovalFeed.cs")
PETANIM = os.path.join(MODULE_DIR, "PetAnimations.cs")
PANE = os.path.join(MODULE_DIR, "AgentFlowPane.cs")

TARGETS = (SPLITTER, RULES, DETECTOR, BUDGET, MODULE, READER, CDP, PROMPTOPTS, BUDGETPRESS,
           DOT, MODE, QUIPS, FEED, PETANIM, PANE)

# (name, file, find, replace, expected fragment of the assertion that must fail)
CASES = (
    (
        "bare & no longer checks for a redirection",
        SPLITTER,
        "                    else if (mode != ShellPowerShell && ch == '&' && IsBashAmpersandSeparator(text, i))\n                        length = 1;",
        "                    else if (mode != ShellPowerShell && ch == '&')\n                        length = 1;",
        "2>&1 is a redirection",
    ),
    (
        "heredoc bodies are no longer masked",
        SPLITTER,
        "            string text = mode == ShellPowerShell ? raw : MaskHeredocBodies(raw);",
        "            string text = raw;",
        "heredoc body is never segmented",
    ),
    (
        "quotes no longer suppress a separator",
        SPLITTER,
        "                if (ch == '\\'')\n                {\n                    quote = \"single\";",
        "                if (false)\n                {\n                    quote = \"single\";",
        "; inside single quotes does not split",
    ),
    (
        "grouping depth is ignored",
        SPLITTER,
        "                if (depth == 0)\n                {\n                    int length = 0;",
        "                if (true)\n                {\n                    int length = 0;",
        "$() substitution is not segmented",
    ),
    (
        "the Tool(cmd:*) spelling is not normalized",
        RULES,
        '            if (!inner.EndsWith(":*", StringComparison.Ordinal)) return text;\n            return tool + "(" + inner.Substring(0, inner.Length - 2) + " *)";',
        '            if (!inner.EndsWith(":*", StringComparison.Ordinal)) return text;\n            return text;',
        "Tool(cmd:*) is the same rule",
    ),
    (
        "the bare-command allowance ignores extra wildcards",
        RULES,
        "                if (CommandTools.Contains(tool) && wildcards == 1\n                    && inner.EndsWith(\" *\", StringComparison.Ordinal))",
        "                if (CommandTools.Contains(tool)\n                    && inner.EndsWith(\" *\", StringComparison.Ordinal))",
        "second wildcard removes the bare-command allowance",
    ),
    (
        "allow is evaluated before ask",
        RULES,
        "            foreach (string rule in rules.Ask)\n                if (RuleMatches(rule, permission)) return RuleVerdict.WouldPrompt;\n            foreach (string rule in rules.Allow)\n                if (RuleMatches(rule, permission)) return RuleVerdict.WouldAllow;",
        "            foreach (string rule in rules.Allow)\n                if (RuleMatches(rule, permission)) return RuleVerdict.WouldAllow;\n            foreach (string rule in rules.Ask)\n                if (RuleMatches(rule, permission)) return RuleVerdict.WouldPrompt;",
        "ask rule beats an allow rule",
    ),
    (
        "a chain's most restrictive part no longer wins",
        RULES,
        "            if (sawDeny) return RuleVerdict.WouldDeny;\n            if (sawPrompt) return RuleVerdict.WouldPrompt;\n            if (sawAllow) return RuleVerdict.WouldAllow;",
        "            if (sawDeny) return RuleVerdict.WouldDeny;\n            if (sawAllow) return RuleVerdict.WouldAllow;\n            if (sawPrompt) return RuleVerdict.WouldPrompt;",
        "most restrictive part of a chain wins",
    ),
    (
        "no mode stands down at all",
        DETECTOR,
        '            if (!string.Equals(mode, ModeDefault, StringComparison.OrdinalIgnoreCase))',
        '            if (string.Equals(mode, "never-matches-anything", StringComparison.OrdinalIgnoreCase))',
        "auto mode stands down",
    ),
    # The defect found on real data: keying the stand-down on `auto` alone let an acceptEdits
    # session fire. This mutation reinstates exactly that bug.
    (
        "REGRESSION: stand down for auto only, as it used to",
        DETECTOR,
        '            if (!string.Equals(mode, ModeDefault, StringComparison.OrdinalIgnoreCase))',
        '            if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))',
        "'acceptEdits' mode stands down",
    ),
    # The other real-data defect: a call the rules cannot address reported as would-prompt,
    # which made every long-running Agent subagent look like a blocked prompt.
    (
        "REGRESSION: judge a call with no addressable argument",
        RULES,
        '                if (string.IsNullOrEmpty(argument)) return RuleVerdict.Undecidable;',
        '                if (false) return RuleVerdict.Undecidable;',
        "a call the rules cannot address is NOT reported as blocked",
    ),
    (
        "an allowed call is reported as blocked",
        DETECTOR,
        "            if (detection.Verdict == RuleVerdict.WouldAllow)",
        "            if (false)",
        "stalled but allowed is SLOW",
    ),
    (
        "the one-shot per prompt is removed",
        BUDGET,
        "            if (_announced.Contains(key))",
        "            if (false)",
        "same prompt never speaks twice",
    ),
    (
        "the cooldown is removed",
        BUDGET,
        "            if (_lastNotifyUtc != DateTime.MinValue\n                && (nowUtc - _lastNotifyUtc).TotalSeconds < _cooldownSeconds)",
        "            if (false)",
        "different session is still held by the cooldown",
    ),
    (
        "the death-loop guard never pauses",
        BUDGET,
        "                _pausedUntilUtc = nowUtc.AddSeconds(_pauseSeconds);",
        "                _pausedUntilUtc = nowUtc;",
        "death-loop guard closes at the per-window cap",
    ),
    (
        "PRIVACY: the command text reaches the spoken line",
        DETECTOR,
        '            string what = string.IsNullOrEmpty(tool) ? "something" : tool;',
        '            string what = detection.Call != null ? detection.Call.Command : tool;',
        "spoken line carries no command text",
    ),
    (
        "PRIVACY: the full path reaches the spoken line",
        DETECTOR,
        "            string where = ShortProject(detection.Session != null ? detection.Session.Cwd : null);",
        "            string where = detection.Session != null ? detection.Session.Cwd : null;",
        "spoken line carries no full path",
    ),
    # The defect the real app exposed and no test had caught: notifying before a companion is
    # on screen, where the host drops the line silently and the budget spends it anyway.
    (
        "speaks with no companion on screen (the swallowed-first-notice bug)",
        MODULE,
        "            if (!AnyCompanionCanSpeak())",
        "            if (false)",
        "says nothing when no companion is on screen",
    ),
    (
        "speaks while speech is switched off",
        MODULE,
        "            if (!_host.SpeechEnabled)",
        "            if (false)",
        "says nothing while speech is switched off",
    ),
    # The other half of that fix, and the half that made the bug PERMANENT rather than merely
    # late: consuming the budget for a notice nobody could have seen.
    (
        "the budget is spent even when the notice was deferred",
        MODULE,
        '                Log("deferred a notice about " + (speakThis.ToolName ?? "?")\n                    + ": no companion on screen to say it");\n                return;',
        '                Log("deferred a notice about " + (speakThis.ToolName ?? "?")\n                    + ": no companion on screen to say it");\n                _budget.Record(speakThis, now);\n                return;',
        "held notice is still delivered once a companion appears",
    ),
    # Saving the pane used to REPLACE the budget so a changed cooldown took effect at once, which
    # discarded the announced set with it. This is that code, put back.
    (
        "saving the pane replaces the budget instead of mutating it",
        MODULE,
        "            if (_budget != null) _budget.SetCooldownSeconds(CooldownSeconds);",
        "            _budget = new NotifyBudget(CooldownSeconds, NotifyBudget.DefaultWindowSeconds,\n"
        "                                       NotifyBudget.DefaultMaxPerWindow,\n"
        "                                       NotifyBudget.DefaultPauseSeconds);",
        "saving the pane does not re-arm an announced prompt",
    ),
    # The match caches are bounded. Remove the eviction from ONE of them: the assertion has to
    # notice a single unbounded cache, not only both of them together.
    (
        "the normalized-rule cache grows without bound",
        RULES,
        "                if (NormalizedRules.Count >= MatchCacheLimit) NormalizedRules.Clear();",
        "                if (false) NormalizedRules.Clear();",
        "evict instead of growing without bound",
    ),
    (
        "the compiled-rule cache grows without bound",
        RULES,
        "                if (CompiledRules.Count >= MatchCacheLimit) CompiledRules.Clear();",
        "                if (false) CompiledRules.Clear();",
        "evict instead of growing without bound",
    ),
    # ---- the approval audit, added 2026-09-18 ------------------------------------------
    # This is the half an approval module owes the user: what it LET THROUGH. The privacy case is
    # the one that matters, because this line goes to a log SUPPORT.md tells users to attach to a
    # public issue tracker.
    (
        "the approval line carries the whole command",
        DETECTOR,
        "                string root = RootExecutable(call);",
        "                string root = call.Command ?? RootExecutable(call);",
        "approval line carries no command text",
    ),
    (
        "an executable given by an absolute path keeps the path",
        DETECTOR,
        "            int slash = first.LastIndexOfAny(new[] { '/', '" + BS + BS + "' });\n"
        "            if (slash >= 0 && slash < first.Length - 1) first = first.Substring(slash + 1);",
        "            int slash = -1;\n"
        "            if (slash >= 0 && slash < first.Length - 1) first = first.Substring(slash + 1);",
        "logged as its leaf only",
    ),
    (
        "calls that would PROMPT are counted as approved too",
        DETECTOR,
        "                if (verdict != RuleVerdict.WouldAllow) continue;",
        "                if (verdict == RuleVerdict.Undecidable) continue;",
        "would prompt for is NOT counted as approved",
    ),
    (
        "every re-read counts the same approvals again",
        DETECTOR,
        "                if (alreadyCounted != null && !alreadyCounted.Add(key)) continue;",
        "                if (alreadyCounted != null) alreadyCounted.Add(key);",
        "second read of the same transcript counts nothing again",
    ),
    (
        "the completed-call list grows without bound",
        READER,
        "            if (Completed.Count > CompletedCap) Completed.RemoveAt(0);",
        "            if (false) Completed.RemoveAt(0);",
        "completed-call list is bounded",
    ),
    # The shadow verdict, which is what makes the module judgeable in auto mode. Both directions:
    # never recording it, and recording Blocked for a call the rules allow.
    (
        "the stand-down records nothing about what it skipped",
        DETECTOR,
        "                detection.WouldHaveBeen = shadow.Outcome;",
        "                detection.WouldHaveBeen = DetectionOutcome.Idle;",
        "but records that it WOULD have been flagged",
    ),
    (
        "the stand-down claims everything would have been flagged",
        DETECTOR,
        "                detection.WouldHaveBeen = shadow.Outcome;",
        "                detection.WouldHaveBeen = DetectionOutcome.Blocked;",
        "when the rules allow the call",
    ),
    # Auto-approve: the only setting in this module that PRESSES something. Five cases, and the
    # first is the one that would matter most if it ever regressed.
    (
        "auto-approve defaults ON",
        MODE,
        "        public static bool Presses(string mode) { return mode == AutoApprove; }",
        "        public static bool Presses(string mode) { return mode != Off; }",
        "auto-approve is OFF until it is asked for",
    ),
    (
        "the pane reports a constant instead of the switch it owns",
        PANE,
        "                { SettingMode, AgentMode.ToDisplay(Mode) },",
        "                { SettingMode, AgentMode.ToDisplay(AgentMode.Notify) },",
        "the pane reports what the tray just did",
    ),
    (
        "the tray shows green whatever the port is doing",
        MODULE,
        # Says "agent panel" since Codex support; this read "Claude Code panel" and so matched
        # nothing at all, which is a guard that stopped guarding without anyone being told.
        "                case ApproveState.CannotSee:\n                    return _portAnswering\n                        ? \"Auto-approve: on, but cannot see the agent panel\"\n                        : \"Auto-approve: on, waiting for VS Code\";",
        "                case ApproveState.CannotSee: return \"Auto-approve: on\";",
        "on-but-unreachable is the orange state",
    ),
    (
        "the tray shows it as on while it is off",
        MODULE,
        "                if (!AutoApprove) return ApproveState.Off;",
        "                if (false) return ApproveState.Off;",
        "the tray says off, and the dot is the off one",
    ),
    (
        "pressing is listed as a peer of watching",
        MODULE,
        "                    Label = AutoApproveTrayLabel(),\n                    IconPng = StatusDot.For(AutoApproveState),\n                    Group = 1,",
        "                    Label = AutoApproveTrayLabel(),\n                    IconPng = StatusDot.For(AutoApproveState),\n                    Group = 0,",
        "in its own group",
    ),
    (
        "the toggle keeps whatever the last probe said",
        MODULE,
        "            _portAnswering = false;",
        "            if (false) _portAnswering = false;",
        "switching it on discards the last probe",
    ),
    # The approve half. These are the safety properties: everything here is a way for the
    # module to press something it should not, or to write something it should not.
    (
        "a greyed-out approve row is pressed anyway",
        MODULE,
        "            if (decision.Index < view.Disabled.Count && view.Disabled[decision.Index])",
        "            if (false)",
        "a greyed-out approve row is not pressed",
    ),
    (
        "a short disabled list is left short",
        CDP,
        "                    while (view.Disabled.Count < view.Options.Count) view.Disabled.Add(false);",
        "                    if (false) view.Disabled.Add(false);",
        "a short disabled list is padded",
    ),
    (
        "the refusal quotes the option text into the log",
        PROMPTOPTS,
        '                    "refused: {0} of {1} options unrecognised -- either the capture misread "',
        '                    "refused: {0} of {1} options unrecognised " + decision.UnsafeDetail + " -- either the capture misread "',
        "the refusal does not quote what it read",
    ),
    (
        "the tool name is logged however it reads",
        MODULE,
        '                if (!ok) return "an unrecognised tool";',
        '                if (!ok) return name;',
        "a tool name carrying anything else is not logged verbatim",
    ),
    (
        "the click expression quotes the label by hand",
        CDP,
        '            return JsonSerializer.Serialize(text ?? "");',
        '            return "' + BS + '"" + (text ?? "") + "' + BS + '"";',
        "an option label cannot break out of the click expression",
    ),
    # The press budget -- the death-loop guard BACKLOG.md asked for before this could press.
    # The last case is the important one: every other assertion about the budget passes just
    # as well when nothing calls it.
    (
        "the repeat cap is off by one, in the permissive direction",
        BUDGETPRESS,
        "                if (_identical >= MaxIdenticalPresses)",
        "                if (_identical > MaxIdenticalPresses)",
        "the same prompt again past the cap is refused",
    ),
    (
        "the rate cap is off by one, in the permissive direction",
        BUDGETPRESS,
        # The cap became a per-user setting in agentflow 1.4.0 and the constant went with it.
        # Same off-by-one, against the field that replaced it.
        "            if (_presses.Count >= _pressLimit)",
        "            if (_presses.Count > _pressLimit)",
        # Label follows the assertion, which was rewritten in agentflow 1.4.0 when the cap
        # became a user setting. The mutation was still being caught; it was caught by a
        # DIFFERENT assertion than this case named, which the harness reports as WRONG
        # rather than as a pass -- the distinction that makes that verdict worth having.
        "a limit the user set is the limit that applies",
    ),
    (
        "the rate window never expires, so the cap latches forever",
        BUDGETPRESS,
        "            while (_presses.Count > 0 && nowUtc - _presses[0] > Window) _presses.RemoveAt(0);",
        "            while (false) _presses.RemoveAt(0);",
        "the rate cap expires rather than latching forever",
    ),
    (
        "a prompt is identified by its tool alone",
        BUDGETPRESS,
        "            if (options != null)",
        "            if (options == null)",
        "different options are different prompts",
    ),
    (
        "the switch no longer clears a stand-down",
        BUDGETPRESS,
        "            _presses.Clear();\n            _identical = 0;\n            _lastSignature = null;",
        "            if (false) _presses.Clear();",
        "clears a stand-down",
    ),
    (
        "nothing consults the budget before pressing",
        MODULE,
        "            if (budget != null)",
        "            if (false)",
        "Decide consults the budget before it presses anything",
    ),
    # Seeing the prompt at all. The first two reproduce the exact defect that shipped on
    # 2026-09-18 -- a reader pointed at the outer webview shell, which contains nothing but a
    # nested iframe, answering "no prompt" with a prompt plainly on screen.
    (
        "unreachable is folded back into no-prompt",
        CDP,
        "            if (string.Equals(raw, \"unreachable\", StringComparison.Ordinal))\n                return ReadOutcome.Unreachable;",
        "            if (false) return ReadOutcome.Unreachable;",
        "an unreachable panel is not reported as no prompt",
    ),
    (
        "the reader stops descending into the nested frame",
        CDP,
        "      try { if (frames[fi].contentDocument) docs.push(frames[fi].contentDocument); } catch (e) { }",
        "      try { if (false) docs.push(document); } catch (e) { }",
        "the reader descends into the nested webview frame",
    ),
    # Saying what was approved, safely. Four of the five prompt shapes name no tool, which is
    # why the first prompt ever pressed logged itself as "an unnamed tool".
    (
        "the header table is never consulted",
        MODULE,
        "                if (!header.StartsWith(known.Key, StringComparison.Ordinal)) continue;",
        "                if (true) continue;",
        "an edit prompt is named as an edit",
    ),
    (
        "an unrecognised header is echoed into the log",
        MODULE,
        '            return "an unrecognised prompt";',
        "            return header;",
        "an unrecognised header is named, not quoted",
    ),
    (
        "the extension filter passes a path through",
        MODULE,
        '                if (!ok) return "";',
        "                if (!ok) return extension;",
        "a SHORT value with anything but letters and digits is dropped",
    ),
    # The dot. Its whole job is to be three different colours for three different states.
    (
        "the able dot is the same colour as the off dot",
        DOT,
        "                        return _able ?? (_able = Draw(Color.FromArgb(60, 190, 90)));",
        "                        return _able ?? (_able = Draw(Color.FromArgb(215, 65, 65)));",
        "three states get three different dots",
    ),
    (
        "the dot is redrawn on every menu open",
        DOT,
        "                        return _able ?? (_able = Draw(Color.FromArgb(60, 190, 90)));",
        "                        return Draw(Color.FromArgb(60, 190, 90));",
        "the dot is cached, not redrawn on every menu open",
    ),
    (
        "green ignores whether the panel could be read",
        MODULE,
        "                return _portAnswering && _panelReadable",
        "                return _portAnswering",
        "a live port with an unreadable panel is still orange",
    ),
    # The mode migration. It runs ONCE, on someone else's machine, months from now, and it is
    # the only thing between an upgrade and a module that silently reverts to defaults.
    (
        "the migration ignores that the user had it switched off",
        MODE,
        "            if (!legacyEnabled) return Off;",
        "            if (false) return Off;",
        "a disabled 1.0.x install migrates to Off",
    ),
    (
        "the migration forgets they were auto-approving",
        MODE,
        "            return legacyAutoApprove ? AutoApprove : Notify;",
        "            return Notify;",
        "an approving install keeps approving",
    ),
    (
        "a stored mode no longer wins over the legacy booleans",
        MODE,
        "            if (IsKnown(stored)) return stored;",
        "            if (false) return stored;",
        "a stored mode is used as-is",
    ),
    (
        "auto-approve goes silent about what it refused",
        MODE,
        "            return mode == Notify || mode == AutoApprove;",
        "            return mode == Notify;",
        "auto-approve still speaks",
    ),
    # The quips.
    (
        "a quip uses a placeholder it does not declare",
        QUIPS,
        '            new Quip("Something in {project} wants a yes or a no.", true, false),',
        '            new Quip("Something in {project} wants a yes or a no.", false, false),',
        "every quip declares exactly the placeholders it uses",
    ),
    (
        "the picker repeats itself",
        QUIPS,
        "                if (!string.Equals(quip.Text, _last, StringComparison.Ordinal)) choices.Add(quip);",
        "                choices.Add(quip);",
        "it never says the same thing twice in a row",
    ),
    (
        "the picker ignores whether it knows the project",
        QUIPS,
        "                if (quip.NeedsProject && !hasProject) continue;",
        "                if (false) continue;",
        "an unknown project never leaves a hole in the sentence",
    ),
    # The approvals feed. The last case is the one that matters: it is the only thing in this
    # module that holds command text, so the assertion worth having is that the LOG still
    # does not.
    (
        "the approvals feed grows for the life of the app",
        FEED,
        "            while (_entries.Count > Cap) _entries.RemoveAt(0);",
        "            while (false) _entries.RemoveAt(0);",
        "the feed is bounded",
    ),
    (
        "the feed shows the oldest approval first",
        FEED,
        "            copy.Reverse();",
        "            if (false) copy.Reverse();",
        "the newest approval is first",
    ),
    (
        "a pasted script is not flattened onto one row",
        FEED,
        "            string flat = command.Replace('\\r', ' ').Replace('\\n', ' ').Replace('\\t', ' ');",
        "            string flat = command;",
        "a multi-line command is flattened to one row",
    ),
    (
        "a very long command is not capped",
        FEED,
        '            return flat.Length <= Max ? flat : flat.Substring(0, Max - 1) + "\u2026";',
        "            return flat;",
        "a very long command is capped",
    ),
    (
        "the command reaches the tally the log is built from",
        DETECTOR,
        "                string root = RootExecutable(call);",
        "                string root = call.Command ?? RootExecutable(call);",
        "the tally the log is built from carries no command text",
    ),
    # Reading a pet's own animations. The last case is the defect this replaces.
    (
        "a duplicated animation is listed twice",
        PETANIM,
        "                    if (!seen.Add(name)) continue;",
        "                    seen.Add(name);",
        "a duplicated animation is offered once, not twice",
    ),
    (
        "an empty animation name is offered as a blank row",
        PETANIM,
        "                    if (name.Length == 0) continue;",
        "                    if (false) continue;",
        "an empty name is legal XML and is skipped anyway",
    ),
    (
        "the chosen animation is not tried first",
        PETANIM,
        '            if (!string.IsNullOrEmpty(animation) && animation != AnyPet) list.Add(animation);',
        "            if (false) list.Add(animation);",
        "the chosen animation is tried first",
    ),
    (
        "the any-pet list goes back to the one-pet animation",
        PETANIM,
        '            get { return new[] { "walk", "sit", "turn", "stand", "jump", "run" }; }',
        '            get { return new[] { "boing", "jump", "run" }; }',
        "the any-pet list no longer leads with a one-pet animation",
    ),
    # The three notify channels. They used to be one, so the mutations that matter are the
    # ones that re-couple them.
    (
        "speech fires whether or not it was asked for",
        MODULE,
        "            if (NotifySpeakOn && AgentMode.Speaks(Mode)) _host.SayAll(line);",
        "            _host.SayAll(line);",
        "a chime with no chatter is possible",
    ),
    (
        "the sound fires whether or not it was asked for",
        MODULE,
        "            if (NotifySoundOn) _host.PlayNotificationSound(Info.Id);",
        "            _host.PlayNotificationSound(Info.Id);",
        "speech alone speaks and makes no sound",
    ),
    (
        "the animation call site goes back to the hardcoded list",
        MODULE,
        # Moved into PlayChosenAnimation when per-pet playback landed, losing four spaces of
        # indent, and Candidates() no longer takes a pet. `if (_host == null)` rather than
        # `if (false)`: the latter is unreachable code, which this tree compiles as an error.
        '            var candidates = new List<string>(PetAnimations.Candidates(',
        '            var candidates = new List<string> { "boing", "jump", "run" }; if (_host == null) _ = new List<string>(PetAnimations.Candidates(',
        "the animation played is not the one-pet list any more",
    ),
    (
        "the log path goes up one level instead of two",
        PANE,
        "                return System.IO.Path.Combine(modules.Parent.FullName, " + chr(34) + "diagnostics.log" + chr(34) + ");",
        "                return System.IO.Path.Combine(modules.FullName, " + chr(34) + "diagnostics.log" + chr(34) + ");",
        "the log is found under an INSTALLED data root",
    ),
    # The audit findings. Each of these SHIPPED, green, before an audit found it.
    (
        "Audio is not declared, so the sound channel is dead in the real host",
        MODULE,
        "                          | ModulePermissions.Audio,",
        "                          ,",
        "it only works because Audio is declared",
    ),
    (
        "the watching tray row writes the retired boolean again",
        MODULE,
        '            _settings.Set(SettingMode, enabled ? AgentMode.Notify : AgentMode.Off);',
        '            _settings.Set(SettingEnabled, enabled ? "true" : "false");',
        "turning watching off from the tray actually stops it",
    ),
    (
        "Log mode speaks, so it is identical to Notify",
        MODULE,
        "            if (NotifySpeakOn && AgentMode.Speaks(Mode)) _host.SayAll(line);",
        "            if (NotifySpeakOn) _host.SayAll(line);",
        "Log is the quiet one, and says nothing out loud",
    ),
    # "Yes, allow ... for all projects". Misclassifying this made the module refuse EVERY bash
    # prompt as ambiguous, and pressing it writes a rule that outlives the session.
    (
        "a for-all-projects row reads as approve-once again",
        PROMPTOPTS,
        "            OptionKind rule = RuleGrantKind(observed);",
        "            OptionKind rule = OptionKind.Unknown;",
        "a for-all-projects row is NOT an approve-once row",
    ),
    (
        "the for-all-projects row is pressed without being asked for",
        PROMPTOPTS,
        "            if (preferAllProjects && allProjects.Count == 1)",
        "            if (allProjects.Count == 1)",
        "by default it presses the one-call row",
    ),
    (
        "the setting unlocks every rule destination, not just all-projects",
        PROMPTOPTS,
        '                return destination == AllProjectsSuffix',
        '                return true',
        "the other rule destinations stay unpressable",
    ),
    (
        "saving a rule for all projects defaults ON",
        MODULE,
        "            get { return _settings != null && _settings.GetBool(SettingApproveAllProjects, false); }",
        "            get { return _settings == null || _settings.GetBool(SettingApproveAllProjects, true); }",
        "saving a rule for all projects is OFF until asked for",
    ),
    (
        "the destination is SEARCHED for instead of anchored at the end",
        PROMPTOPTS,
        "                if (!normalized.EndsWith(destination, StringComparison.Ordinal)) continue;",
        "                if (normalized.IndexOf(destination, StringComparison.Ordinal) < 0) continue;",
        "the phrase inside the command does not make it all-projects",
    ),
)


def read(path):
    with io.open(path, "r", encoding="utf-8-sig", newline="") as handle:
        return handle.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8-sig", newline="") as handle:
        handle.write(text)


def build():
    """Build the MODULE, not the host. Returns True when it compiled.

    TreatWarningsAsErrors is turned OFF for the mutation builds only, and that is a fix rather
    than a loosening. modules/Directory.Build.props gained WarningLevel 4 and
    TreatWarningsAsErrors on 2026-09-17, which is right for real code -- and it silently broke
    NINE of this file's cases, taking the suite from 23/23 to 14/23 with the other nine reporting
    BROKEN (does not compile). The cause is that several mutations disable a line with
    `if (false)`, which is CS0162 unreachable code: a warning, and therefore now an error.

    A mutation is a deliberate temporary break whose only job is to make one assertion fail. Its
    build has nothing to prove about warning policy, and the BASELINE build below still runs under
    the real settings, so a genuine new warning in the real source still fails there.

    Recorded at length because of how it was found: the suite was run in full as the last step of
    a session, having been run case-by-case for hours. A suite nobody runs whole is a suite that
    reports more coverage than it has, which is the exact defect this file caught in
    mutate-diagnostics.py earlier the same day.
    """
    proc = subprocess.run(
        ["dotnet", "build", CSPROJ, "-c", "Release", "--nologo", "-v:quiet",
         "-p:TreatWarningsAsErrors=false"],
        capture_output=True, text=True, timeout=900)
    return proc.returncode == 0, proc.stdout or ""


def run_selftest():
    """(ok, report) from the module self-test, or (None, why) when it could not run."""
    try:
        os.remove(MARKER)
    except OSError:
        pass
    proc = subprocess.run([EXE, "--module-selftest=agentflow"],
                          capture_output=True, text=True, timeout=900)
    if not os.path.isfile(MARKER):
        return None, "no marker file written (exit %d)" % proc.returncode
    report = read(MARKER)
    return ("RESULT=PASS" in report), report


def failing_lines(report, ):
    out = []
    for line in report.splitlines():
        stripped = line.strip()
        if stripped.startswith("RESULT="):
            continue
        if "FAIL" in stripped:
            out.append(stripped)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default=None)
    args = parser.parse_args()

    if not os.path.isfile(EXE):
        print("Cannot run: %s is missing. Build first (tests\\run-gate.ps1)." % EXE)
        return 2

    baseline = {}
    for path in TARGETS:
        if not os.path.isfile(path):
            print("Cannot run: %s is missing." % path)
            return 2
        baseline[path] = read(path)

    def restore():
        for target, text in baseline.items():
            write(target, text)

    # ---- BASELINE MUST BE GREEN FIRST -------------------------------------------------
    # 20/20 was once reported against a red baseline in this repo, and only the gate
    # caught it afterwards. Every verdict below is meaningless if this is not clean.
    print("baseline: building the module and running its self-test")
    compiled, output = build()
    if not compiled:
        print("BASELINE IS NOT GREEN: the module does not build.")
        print(output[-1500:])
        return 2
    ok, report = run_selftest()
    if ok is not True:
        print("BASELINE IS NOT GREEN: the self-test does not pass.")
        print(report if isinstance(report, str) else "")
        return 2
    witnesses = report.count("WITNESS")
    if witnesses == 0:
        print("BASELINE IS SUSPECT: the report names no WITNESS assertion, so there is no")
        print("evidence the new code ran at all. Refusing to score mutations.")
        return 2
    print("  baseline PASS, %d witness assertions present\n" % witnesses)

    cases = [c for c in CASES if not args.only or args.only in c[0]]
    print("mutation run: %d case(s)\n" % len(cases))
    fired = 0
    try:
        for name, path, find, replace, expected in cases:
            source = baseline[path]
            count = source.count(find)
            if count != 1:
                print("  %-52s NO-OP (pattern matched %d times)" % (name, count))
                continue

            before_stamp = os.path.getmtime(DLL) if os.path.isfile(DLL) else 0.0
            write(path, source.replace(find, replace))
            # A same-second rebuild can leave the timestamp unchanged on a coarse
            # filesystem clock, which would read as "never rebuilt".
            time.sleep(1.1)
            compiled, output = build()
            if not compiled:
                print("  %-52s BROKEN (does not compile)" % name)
                restore()
                continue

            after_stamp = os.path.getmtime(DLL) if os.path.isfile(DLL) else 0.0
            if after_stamp <= before_stamp:
                # The exact failure that made an earlier harness in this repo report a
                # confident 0/7: the thing under test was never rebuilt.
                print("  %-52s BROKEN (DLL timestamp did not advance -- not rebuilt)" % name)
                restore()
                continue

            ok, report = run_selftest()
            restore()

            if ok is None:
                print("  %-52s BROKEN (%s)" % (name, report))
                continue
            if ok:
                print("  %-52s SURVIVED -- the self-test does not cover this" % name)
                continue
            if "WITNESS" not in report:
                print("  %-52s BROKEN (report names no witness; did the new code run?)" % name)
                continue

            lines = failing_lines(report)
            hit = [line for line in lines if expected in line]
            if hit:
                fired += 1
                extra = (" (+%d other failures)" % (len(lines) - len(hit))) if len(lines) > len(hit) else ""
                print("  %-52s FIRED%s" % (name, extra))
                print("        %s" % hit[0])
            else:
                print("  %-52s WRONG -- failed on something else:" % name)
                for line in lines[:3]:
                    print("        %s" % line)
    finally:
        restore()
        # Leave the tree building, so a later gate run is not measuring a mutant.
        build()

    print("\n%d/%d fired." % (fired, len(cases)))
    return 0 if fired == len(cases) else 1


if __name__ == "__main__":
    sys.exit(main())
