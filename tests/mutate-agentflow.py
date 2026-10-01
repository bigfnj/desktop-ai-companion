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
import shutil
import subprocess
import sys
import time
import uuid

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
# The marker is read from a per-run TEMP that main() creates and hands to the exe through its
# environment (Path.GetTempPath() reads TMP, then TEMP). With the fixed name under the shared per-user
# %TEMP%, a gate in another checkout running --module-selftest=agentflow could overwrite this harness's
# verdict between the exe's write and the read, or read this harness's MUTANT verdict as its own
# (F418). All three are set in main().
RUN_TEMP = None
MARKER = None
CHILD_ENV = None

SPLITTER = os.path.join(MODULE_DIR, "CommandSplitter.cs")
RULES = os.path.join(MODULE_DIR, "PermissionRules.cs")
DETECTOR = os.path.join(MODULE_DIR, "BlockedDetector.cs")
BUDGET = os.path.join(MODULE_DIR, "NotifyBudget.cs")
MODULE = os.path.join(MODULE_DIR, "AgentFlowModule.cs")
READER = os.path.join(MODULE_DIR, "TranscriptReader.cs")
CDP = os.path.join(MODULE_DIR, "CdpApprover.cs")
PROMPTOPTS = os.path.join(MODULE_DIR, "PromptOptions.cs")
BUDGETPRESS = os.path.join(MODULE_DIR, "PressBudget.cs")
PANE = os.path.join(MODULE_DIR, "AgentFlowPane.cs")
DOT = os.path.join(MODULE_DIR, "StatusDot.cs")
MODE = os.path.join(MODULE_DIR, "AgentMode.cs")
QUIPS = os.path.join(MODULE_DIR, "Quips.cs")
FEED = os.path.join(MODULE_DIR, "ApprovalFeed.cs")
PETANIM = os.path.join(MODULE_DIR, "PetAnimations.cs")
VSCODE = os.path.join(MODULE_DIR, "VsCodeSetup.cs")
RULELOADER = os.path.join(MODULE_DIR, "RuleLoader.cs")
CURSOR = os.path.join(MODULE_DIR, "TranscriptCursor.cs")

TARGETS = (SPLITTER, RULES, DETECTOR, BUDGET, MODULE, READER, CDP, PROMPTOPTS, BUDGETPRESS,
           DOT, MODE, QUIPS, FEED, PETANIM, PANE, VSCODE, RULELOADER, CURSOR)

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
        # Re-pointed 2026-09-29 (lane fix/agentflow): F051 made Evaluate normalise the permission once
        # and call RuleMatchesNormalized(rule, target), so the old `RuleMatches(rule, permission)`
        # pattern matched nothing and the whole run reported this case NO-OP -- the silent loss of
        # coverage the release checklist warns about, found by running the harness whole.
        "allow is evaluated before ask",
        RULES,
        "            foreach (string rule in rules.Ask)\n                if (RuleMatchesNormalized(rule, target)) return RuleVerdict.WouldPrompt;\n            foreach (string rule in rules.Allow)\n                if (RuleMatchesNormalized(rule, target)) return RuleVerdict.WouldAllow;",
        "            foreach (string rule in rules.Allow)\n                if (RuleMatchesNormalized(rule, target)) return RuleVerdict.WouldAllow;\n            foreach (string rule in rules.Ask)\n                if (RuleMatchesNormalized(rule, target)) return RuleVerdict.WouldPrompt;",
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
    # Re-pointed 2026-09-30 (lane burn/agentflow, R-004): the three channels moved into Deliver, where the
    # host is a local, so the `_host.SpeechEnabled` patterns matched nothing and both cases went NO-OP.
    (
        "speaks with no companion on screen (the swallowed-first-notice bug)",
        MODULE,
        "            bool canSpeak = host.SpeechEnabled && AnyCompanionCanSpeak();",
        "            bool canSpeak = host.SpeechEnabled;",
        "says nothing when no companion is on screen",
    ),
    (
        "speaks while speech is switched off",
        MODULE,
        "            bool canSpeak = host.SpeechEnabled && AnyCompanionCanSpeak();",
        "            bool canSpeak = AnyCompanionCanSpeak();",
        "says nothing while speech is switched off",
    ),
    # The other half of that fix, and the half that made the bug PERMANENT rather than merely
    # late: consuming the budget for a notice nobody could have seen. Re-pointed 2026-09-30 at the
    # deferral Deliver's outcome drives (R-004).
    (
        "the budget is spent even when the notice was deferred",
        MODULE,
        '                                  + ": " + delivery.Undelivered);\n                return;',
        '                                  + ": " + delivery.Undelivered);\n                _budget.Record(speakThis, now);\n                return;',
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
    # ONE mutant, TWO fragments, both required (F400): this case and "the command reaches the tally the log is
    # built from" compiled and ran the same BlockedDetector mutant twice to check two labels; the tuple asks
    # for both in one run, and the grader requires EVERY fragment, so deleting either assertion still fires.
    (
        "the approval line carries the whole command, and the tally too",
        DETECTOR,
        "                string root = RootExecutable(call);",
        "                string root = call.Command ?? RootExecutable(call);",
        ("approval line carries no command text", "the tally the log is built from carries no command text"),
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
        # The pattern moved in 1.4.4 when the matcher was extracted, and this case went
        # NO-OP -- it reported neither FIRED nor SURVIVED, so the privacy guard it exists
        # to prove simply stopped being proved. A NO-OP is the quiet failure of a mutation
        # harness and is why the runner prints one rather than skipping.
        "an unrecognised header is echoed into the log",
        MODULE,
        '            if (best == null) return "an unrecognised prompt";',
        "            if (best == null) return header;",
        "an unrecognised header is named, not quoted",
    ),
    (
        # 1.4.4: the shell template, which is the commonest prompt shape of all and is not
        # a table row.
        "the shell template is never recognised",
        MODULE,
        '            if (IsShellHeader(header)) return "a shell command";',
        '            if (false) return "a shell command";',
        "a shell prompt is named, not filed as unrecognised",
    ),
    (
        # The suffix anchor is what stops "Allow this glob command" being called a shell
        # command. Prefix-only is the obvious implementation and it is wrong.
        "the shell template matches on its prefix alone",
        MODULE,
        "                && normalizedHeader.EndsWith(ShellHeaderSuffix, StringComparison.Ordinal)",
        "                && true",
        "the shell template does not swallow its neighbours",
    ),
    (
        # Longest-match degraded to last-match. Survives every assertion made against the
        # REAL table, because no key in it is a prefix of another -- only the synthetic
        # collision in the self-test can catch this one, which is why that exists.
        "the header matcher takes the last match, not the longest",
        MODULE,
        "                if (known.Key.Length <= bestLength) continue;",
        "                if (false) continue;",
        "the longest matching prefix wins, whatever the row order",
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
        "            bool wantsSpeech = NotifySpeakOn && AgentMode.Speaks(Mode);",
        "            bool wantsSpeech = true;",
        "a chime with no chatter is possible",
    ),
    (
        # Re-pointed 2026-09-30 (lane burn/agentflow, R-004): the chime is asked for inside Deliver and its
        # answer kept, so the gate is `if (wantsSound)` around a try.
        "the sound fires whether or not it was asked for",
        MODULE,
        "            if (wantsSound)\n            {\n                try { result.Chimed = host.PlayNotificationSound(Info.Id); }",
        "            if (wantsSound || !wantsSound)\n            {\n                try { result.Chimed = host.PlayNotificationSound(Info.Id); }",
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
        # Kept as it stands, checked 2026-09-30 by lane fix/followups: the whole run scored this case WRONG
        # for a day after N-host-03 folded the log-path arithmetic into the containment WITNESSes, whose
        # labels said containment while the arithmetic was what failed. SelfCheckRevealPath asserts the
        # arithmetic under its own labels again, this one among them, so the expectation below is unchanged.
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
        '            string next = enabled ? AgentMode.Notify : AgentMode.Off;',
        '            string next = AgentMode.Notify;',
        "turning watching off from the tray actually stops it",
    ),
    (
        "Log mode speaks, so it is identical to Notify",
        MODULE,
        "            bool wantsSpeech = NotifySpeakOn && AgentMode.Speaks(Mode);",
        "            bool wantsSpeech = NotifySpeakOn;",
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
    (
        # v1.2.4 field report: the animation dropdown showed the generic seven for a pet that
        # has its own list, and the only cure was changing the pet and changing it back. The
        # memo was caching a FALLBACK, and the fallback is taken on every launch because Pets()
        # returns null for the whole of Init. Caching it pinned the wrong answer under the right
        # key for the life of the pane.
        "a failed animation lookup is cached and so stays failed",
        PANE,
        "            if (authoritative)\n"
        "            {\n"
        "                _choicesPet = key;\n"
        "                _choicesCache = computed;\n"
        "            }",
        "            {\n"
        "                _choicesPet = key;\n"
        "                _choicesCache = computed;\n"
        "            }",
        "a lookup that could not succeed is not cached",
    ),
    (
        # v1.2.4 field report: "agentflow pet does not see pearl". Installed pets were offered
        # ONLY when nothing was on screen, so a pet the owner owns but has not spawned could not
        # be chosen at all.
        "installed pets are hidden whenever any pet is on screen",
        PANE,
        "            foreach (CompanionTypeInfo type in InstalledPets())\n"
        "            {\n"
        "                string display = PetDisplay(type);",
        "            if (choices.Count == 1)\n"
        "            foreach (CompanionTypeInfo type in InstalledPets())\n"
        "            {\n"
        "                string display = PetDisplay(type);",
        "an installed pet that is NOT on screen can still be chosen",
    ),
    # ---- 2026-09-29 audit campaign: each lane adds its cases directly under its own anchor so parallel
    # branches do not touch the same lines. Comments inside the literal are fine for Python.
    # ---- lane fix/gates ----
    (
        # F039: the run used to be an && chain compared against a COUNT of SelfCheck* methods, which a
        # deleted group did not move. Set equality between what is wired and what is declared names it.
        "a SelfCheck group is dropped from the wired run",
        MODULE,
        "                        SelfCheckSplitter,\n                        SelfCheckRules,\n",
        "                        SelfCheckRules,\n",
        "every SelfCheck group declared on this type is wired",
    ),
    (
        # F040: the blind arm had no executing coverage. Report a read that yielded no options as if
        # nothing were on screen, and the WIRE cases that drive a 'blind' target through Sweep fail.
        "a blind card is no longer reported through sawBlind",
        CDP,
        "            sawBlind = sawUnreadableCard;",
        "            sawBlind = false;",
        "card is reported through sawBlind and presses nothing",
    ),
    (
        # F040, the other half: the spoken note must name the AGENT whose card could not be read.
        "the blind note stops naming the Codex agent",
        CDP,
        "                return \"a \" + (blindAgent == AgentCodex ? \"Codex\" : \"Claude Code\")",
        "                return \"a \" + \"Claude Code\"",
        "WIRE the blind note names the Codex agent",
    ),
    (
        # F391: the strings tests/difftest-prompt-options.py listed without an expectation are now
        # asserted in the self-test. Drop the ')' list-chrome form from the normaliser and the "1) Yes,
        # and don't ask again" assertion is the one that notices.
        "list chrome no longer strips a numbered ')' prefix",
        PROMPTOPTS,
        "            new Regex(@\"^\\s*(?:[>❯▶*\\-]\\s*)?(?:\\(?\\d{1,2}[.)\\]]\\s*)?\", RegexOptions.Compiled);",
        "            new Regex(@\"^\\s*(?:[>❯▶*\\-]\\s*)?(?:\\(?\\d{1,2}[.\\]]\\s*)?\", RegexOptions.Compiled);",
        "a numbered prefix with a parenthesis is list chrome",
    ),
    # ---- lane fix/agentflow ----
    (
        # F061: FixDanglingComma found the dangling comma in comment-STRIPPED text and mapped it back
        # by counting commas, which a comma inside a comment throws off by one. Put the stripped text
        # back and the stock file with the key appended LAST loses the wrong comma again.
        "the dangling comma is located in comment-stripped text again",
        VSCODE,
        "            string blanked = BlankLineComments(text);\n            int close = blanked.LastIndexOf('}');",
        "            string blanked = StripLineComments(text);\n            int close = blanked.LastIndexOf('}');",
        "Disable on a hand-appended LAST member returns the stock file BYTE FOR BYTE",
    ),
    (
        # F061, the brace half: a `{` in the header comment used to shift the brace count.
        "the top-level brace is located in comment-stripped text again",
        VSCODE,
        "            return BlankLineComments(text).IndexOf('{');",
        "            return StripLineComments(text).IndexOf('{');",
        "a brace inside a header comment does not misplace the inserted key",
    ),
    (
        # F060: the value-end scan used to run to the first comma, newline or brace, so a trailing
        # comment on a LAST member (no comma) was swallowed into the replaced span.
        "the value scan runs to the newline again, swallowing a trailing comment",
        VSCODE,
        "            while (end < blanked.Length && !char.IsWhiteSpace(blanked[end])\n                   && blanked[end] != ',' && blanked[end] != '}') end++;",
        "            while (end < blanked.Length && blanked[end] != '\\n'\n                   && blanked[end] != ',' && blanked[end] != '}') end++;",
        "Enable on a last member with a trailing comment keeps the comment",
    ),
    (
        # F043: the Codex stand-down allow-listed on-request alone, so untrusted sessions -- which ask
        # before every non-trusted command -- were never flagged. Shrink the list back to one.
        "the Codex asking policies shrink back to on-request alone",
        DETECTOR,
        '        public static readonly string[] CodexAskingPolicies = { CodexOnRequest, "untrusted", "on-failure" };',
        '        public static readonly string[] CodexAskingPolicies = { CodexOnRequest };',
        "an untrusted session over the threshold reads as BLOCKED",
    ),
    (
        # F043, the wording half: an unmeasured policy must not be described as one that never asks.
        "an unmeasured Codex policy is described as never asking again",
        DETECTOR,
        '                        detection.Reason = "codex " + mode + ": this approval policy has not been "\n                                           + "measured, so it is not acted on";',
        '                        detection.Reason = "codex " + mode + ": this session never stops to ask, so "\n                                           + "nothing here is waiting on you";',
        "UNMEASURED rather than that the session cannot ask",
    ),
    (
        # F053: project-scope rules were never read. Hand every session the home tiers alone again.
        "project-scope rule files are ignored again",
        MODULE,
        "                RuleSet forSession = agent == TranscriptReader.AgentCodex\n                    ? rules\n                    : RuleLoader.WithProjectRules(rules, session.Cwd,\n                                                  sessions != null ? sessions.Rules : null);",
        "                RuleSet forSession = rules;",
        "a call allowed only by a PROJECT rule reads as slow, not blocked",
    ),
    (
        # F055: the stat key stops matching, so every tick re-parses every file as it used to.
        "the rule cache re-parses unchanged files every tick",
        RULELOADER,
        "            if (_files.TryGetValue(path, out entry) && entry.Exists == exists\n                && entry.WrittenUtc == written && entry.Length == length)\n                return entry;",
        "            if (_files.TryGetValue(path, out entry) && entry.Exists == exists && false\n                && entry.WrittenUtc == written && entry.Length == length)\n                return entry;",
        "a second tick over unchanged settings files parses nothing",
    ),
    (
        # F031: the no-rule-files note was emitted for a Codex-only watcher, which never reads them.
        "the no-rule-files note ignores whether Claude is watched",
        MODULE,
        "            if (sources == 0 && watchClaude && resetNotes != null) resetNotes.Add(NoRuleFilesNote);",
        "            if (sources == 0 && resetNotes != null) resetNotes.Add(NoRuleFilesNote);",
        "a Codex-only watcher is not told about Claude's rule files",
    ),
    (
        # F051: the permission string went through the CACHED normaliser and became a key, one per
        # distinct command. Put that back and forty distinct commands grow the cache by forty.
        "the permission string is cached as a rule again",
        RULES,
        "            string target = NormalizeRuleUncached(permission ?? string.Empty);",
        "            string target = NormalizeRule(permission ?? string.Empty);",
        "none of forty distinct commands becomes a key of the rule cache",
    ),
    (
        # F059: one denied directory used to end the enumeration with nothing more yielded, which for
        # a folder directly under the root was the WHOLE root. Give up on the walk at the denied
        # folder again. (Clearing the pending stack instead SURVIVED: the stack pops in reverse
        # listing order, so the denied folder, sorting first, is popped last, after every other
        # folder has already been listed -- a mutation that changes nothing is not a mutation.)
        "a denied folder ends the transcript walk again",
        READER,
        "                catch (UnauthorizedAccessException) { inaccessible++; }\n                catch (IOException) { }   // removed between listing its parent and listing it: nothing to miss",
        "                catch (UnauthorizedAccessException) { inaccessible++; return new List<string>(); }\n                catch (IOException) { }   // removed between listing its parent and listing it: nothing to miss",
        "the transcripts in every OTHER folder are still found",
    ),
    (
        # F059, the saying half: a denied folder must be COUNTED, or the note is never emitted.
        "a denied folder is skipped without being counted",
        READER,
        "                catch (UnauthorizedAccessException) { inaccessible++; }",
        "                catch (UnauthorizedAccessException) { }",
        "one denied folder is counted and SAID, not swallowed",
    ),
    (
        # N-agentflow-02: the skip used to test only the immediate parent. Skip by name at the
        # point of descent no more; test the parent of each FILE instead, as it used to.
        "the subagents skip tests the immediate parent only again",
        READER,
        "                            if (!string.IsNullOrEmpty(skipDirectoryName)\n                                && string.Equals(child.Name, skipDirectoryName,\n                                                 StringComparison.OrdinalIgnoreCase))\n                                continue;   // a blocked SUBAGENT is not a prompt the user can answer\n                            pending.Push(child);\n                            continue;",
        "                            pending.Push(child);\n                            continue;",
        "the workflow journal under subagents is not a session, at any depth",
    ),
    (
        # F058: hand out every completion the resumed cursor folded, floor or no floor, and the
        # session back from lunch is tallied from byte zero again.
        "a resumed cursor hands out completions below the tally floor",
        CURSOR,
        "            foreach (OutstandingCall call in _state.CompletedSinceSnapshot)\n                if (call.CompletedAtByte > _tallyFloor) session.NoteCompleted(call);",
        "            foreach (OutstandingCall call in _state.CompletedSinceSnapshot)\n                session.NoteCompleted(call);",
        "a resumed session tallies only what was appended, not its history",
    ),
    (
        # F058, the identity half: resume the tally without the head, and a different file under the
        # same name is under-counted because the stale floor is never cleared.
        "a resumed cursor forgets the head that tells a replaced file apart",
        CURSOR,
        "            _tallyFloor = retired.Offset;\n            _createdUtc = retired.CreatedUtc;\n            _head = retired.Head;",
        "            _tallyFloor = retired.Offset;\n            _createdUtc = retired.CreatedUtc;",
        "a different file under the same name clears the floor and is tallied whole",
    ),
    (
        # F057: the byte-level fold is what the cursor runs now. Fold every record as Codex and the
        # Claude fixture yields nothing, so the fold equivalence fails.
        "the byte-level fold dispatches every record to the Codex fold",
        READER,
        "                    if (agent == AgentCodex) FoldCodexRecord(record, state);\n                    else FoldClaudeRecord(record, state);\n                    return true;",
        "                    FoldCodexRecord(record, state);\n                    return true;",
        "folding in two reads equals one whole-file parse",
    ),
    (
        # N-agentflow-03: the scan-equivalence fixture wrote `C:\work` into the JSON, which is not a
        # JSON escape, so its tool_use records never parsed. Put the broken escape back.
        "the scan-equivalence fixture carries an invalid JSON escape again",
        MODULE,
        # The s2 record, which is the one that supplies the OUTSTANDING call the witness asserts on.
        '{\\"cwd\\":\\"C:\\\\\\\\work\\",\\"message\\":{\\"content\\":[{\\"type\\":\\"tool_use\\",\\"id\\":\\"s2\\"',
        '{\\"cwd\\":\\"C:\\\\work\\",\\"message\\":{\\"content\\":[{\\"type\\":\\"tool_use\\",\\"id\\":\\"s2\\"',
        "the fixture's outstanding call and its cwd really folded",
    ),
    (
        # F059 / F031: a state note reported on every tick was written on every tick until the
        # dedupe existed; take the dedupe out.
        "a state note is written on every tick again",
        MODULE,
        "                        statesNow.Add(scanNote);\n                        if (_saidStateNotes.Contains(scanNote)) continue;",
        "                        statesNow.Add(scanNote);",
        "a state note reported on three ticks is written once",
    ),
    (
        # F029: the repeat guard covered confirmed presses too, so N same-shape clicks left one line.
        "the approval log dedupes confirmed presses again",
        MODULE,
        "            if (!pressed && string.Equals(note, _lastApprovalNote, StringComparison.Ordinal)) return;",
        "            if (string.Equals(note, _lastApprovalNote, StringComparison.Ordinal)) return;",
        "two identical confirmed presses on consecutive ticks are two log lines",
    ),
    (
        # F032: the no-tool-calls arm bypassed Explain and logged every tick.
        "a transcript with no tool calls is logged on every tick again",
        MODULE,
        "                    Explain(detection, \"no tool calls yet in session \" + Short(detection.Session)",
        "                    Log(\"no tool calls yet in session \" + Short(detection.Session)",
        "a live transcript with no tool calls is explained once, not once per tick",
    ),
    (
        # F033: "held back a notice" had no repeat guard.
        "a held-back notice is logged on every tick again",
        MODULE,
        "                if (!string.Equals(heldBack, _lastHeldBack, StringComparison.Ordinal))\n                {\n                    _lastHeldBack = heldBack;\n                    Log(heldBack);\n                }",
        "                {\n                    _lastHeldBack = heldBack;\n                    Log(heldBack);\n                }",
        "a notice held back on three consecutive ticks is logged once",
    ),
    (
        # N-agentflow-04: Apply stopped at the first blocked detection, announced or not.
        "the first blocked session keeps the floor after it was announced",
        MODULE,
        "            if (unannounced != null) speakThis = unannounced;",
        "            if (unannounced != null && speakThis == null) speakThis = unannounced;",
        "a second session that blocks while the first still stands IS announced",
    ),
    (
        # F034: "signalled about" was written with every channel off. Re-pointed 2026-09-30 (lane
        # burn/agentflow, R-004) at the verb's condition, since `delivered` is Deliver's outcome now.
        "the notice log claims a signal with every channel off again",
        MODULE,
        "                        : (delivery.Delivered || !speakingMode) ? \"signalled about \"",
        "                        : (delivery.Delivered || !delivery.Delivered || !speakingMode) ? \"signalled about \"",
        "the log does not claim it signalled anyone",
    ),
    (
        # F026: the in-flight press stops following the switch.
        "the in-flight press gate ignores the switch again",
        MODULE,
        "            return _pressArmed && !_shuttingDown && _host != null;",
        "            return _host != null;",
        "switching it off from the tray disarms a press already in flight",
    ),
    (
        # F026, the other half: Decide stops asking. `false &&` rather than `if (false)`: the latter
        # makes the return unreachable, which this tree compiles as an error.
        "Decide no longer takes a last look at the switch",
        MODULE,
        "            if (stillArmed != null && !stillArmed())",
        "            if (false && stillArmed != null && !stillArmed())",
        "a prompt whose switch moved during the sweep is stood down before the click",
    ),
    (
        # F028: the limit written from the UI thread loses its memory semantics.
        "the press limit is a plain int again",
        BUDGETPRESS,
        "        private volatile int _pressLimit = DefaultPressLimit;",
        "        private int _pressLimit = DefaultPressLimit;",
        "the press limit is a volatile int",
    ),
    (
        # F037: the first self-test instance is unseeded again, so its Init starts a real scan.
        "the first self-test instance starts a background scan again",
        MODULE,
        "                    host.SettingsFor(\"agentflow\").Set(SettingMode, AgentMode.Off);   // no scan at Init (F037)\n                    var module = new AgentFlowModule();",
        "                    var module = new AgentFlowModule();",
        "the first self-test instance never started a background scan",
    ),
    (
        # F044: the target list was fetched once per agent. Fetch it a second time again.
        "the sweep fetches /json/list twice again",
        CDP,
        "            List<KeyValuePair<string, string>> work = AgentTargets(port, timeoutMs);\n            if (work.Count == 0) return null;",
        "            List<KeyValuePair<string, string>> work = AgentTargets(port, timeoutMs);\n            work.AddRange(AgentTargets(port, timeoutMs)); work.RemoveRange(work.Count / 2, work.Count / 2);\n            if (work.Count == 0) return null;",
        "one sweep fetches /json/list ONCE, not once per agent",
    ),
    (
        # F046: a fresh receive buffer per message again, through the counted path.
        "a receive buffer is allocated per message again",
        CDP,
        "                        .ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), _cancel.Token)",
        "                        .ReceiveAsync(new ArraySegment<byte>(NewReceiveBuffer()), _cancel.Token)",
        "one sweep allocates ONE set of receive buffers, not one per message",
    ),
    (
        # F042: the per-load memo of the installed pets is bypassed, so a build asks several times.
        "the pane asks the host for the installed pets on every call again",
        PANE,
        "            return _installedForLoad ?? FetchInstalledPets();",
        "            return FetchInstalledPets();",
        "one pane build asks for the installed pets ONCE",
    ),
    # ---- lane fix/followups ----
    (
        # N-host-03: the data-folder button asks for the app's log two levels up again, the shape the host's
        # PermittedRevealRoot refused on every machine. The self-check pins containment in the module's
        # own storage, so the old answer fails it.
        "the data-folder button asks for the app's log two levels up again",
        PANE,
        '            return System.IO.Path.Combine(storage.DataDirectory, "settings.json");',
        '            return LogPathFrom(storage);',
        "the reveal path stays inside this module's own storage",
    ),
    # ---- lane burn/agentflow ----
    # RA-047: the sweep broke out on the first non-null note, and a refusal is a note, so a standing prompt
    # the module refuses on the first-listed webview ended every sweep there and a pressable prompt on the
    # next webview was never reached. Put the read-not-press break back.
    (
        "burn: the sweep breaks on the first prompt READ again",
        CDP,
        "                            if (didPress) { pressedNote = note ?? \"pressed a prompt\"; break; }\n                            if (note != null) refusals.Add(note);",
        "                            if (note != null) { pressedNote = note; break; }",
        "a refused prompt on the first webview does not starve the second",
    ),
    (
        # RA-047, the log half: every refusal read reaches the one returned note, not only the first.
        "burn: the sweep returns the first refusal alone, dropping the rest",
        CDP,
        "                            if (note != null) refusals.Add(note);",
        "                            if (note != null && refusals.Count == 0) refusals.Add(note);",
        "both refusals reach the one returned note",
    ),
    (
        # RA-021: the pass records a prompt it left alone, which is what the screen announcement is built from.
        "burn: the pass drops an unpressed prompt from the announcement",
        MODULE,
        "                if (pressed) PressedAny = true;\n                else Unpressed.Add(new ScreenPrompt",
        "                if (pressed) PressedAny = true;\n                else if (pressed) Unpressed.Add(new ScreenPrompt",
        "the first-listed webview's prompt is REFUSED, not pressed",
    ),
    (
        # RA-021: the pass records that it pressed, which is what the F029 log split reads.
        "burn: the pass forgets that it pressed",
        MODULE,
        "                if (pressed) PressedAny = true;\n",
        "                if (false) PressedAny = true;\n",
        "with the switch held the same prompt is pressed and recorded as such",
    ),
    (
        # RA-047, the announcement half: several unpressed prompts announce as one notice, not as the first.
        "burn: several unpressed prompts announce only the first",
        MODULE,
        "                if (unpressed.Count == 1) return unpressed[0];",
        "                if (unpressed.Count >= 1) return unpressed[0];",
        "two unpressed prompts announce as one notice naming both subjects",
    ),
    (
        # RA-049: 1.4.0 cleared the repeat counter on every 'clicked', so a card that stays after a click --
        # the loop the guard exists for -- was re-clicked every tick with no stand-down. Put the clear back.
        "burn: a confirmed click clears the repeat counter again",
        MODULE,
        "            pressed = string.Equals(outcome, \"clicked\", StringComparison.Ordinal);\n",
        "            pressed = string.Equals(outcome, \"clicked\", StringComparison.Ordinal);\n            if (pressed && budget != null) budget.NotePromptCleared();\n",
        "a card that stays after a confirmed click stands the module down after three presses",
    ),
    (
        # RA-023: the card's fingerprint is what tells two same-labelled prompts apart in the press signature.
        "burn: the press signature ignores the card's fingerprint",
        BUDGETPRESS,
        "            if (!string.IsNullOrEmpty(fingerprint)) { text.Append('\\u001F'); text.Append(fingerprint); }",
        "            if (fingerprint == null && fingerprint != null) { text.Append('\\u001F'); text.Append(fingerprint); }",
        "two prompts with the same labels and different cards are different prompts",
    ),
    (
        # RA-022: the screen one-shot keyed on agent + labels alone, so a same-labelled prompt on a new card
        # arriving within a tick was never announced.
        "burn: the screen one-shot ignores the card's fingerprint",
        MODULE,
        "                return view.Agent + \"|\" + view.Fingerprint + \"|\" + string.Join(\"|\", view.Options);",
        "                return view.Agent + \"|\" + string.Join(\"|\", view.Options);",
        "a same-labelled prompt on a different card is a different screen prompt",
    ),
    (
        # The Claude reader stops emitting the fingerprint at all; the source-text witness is what notices,
        # since the fake serves canned JSON and cannot run the JavaScript.
        "burn: the Claude reader stops fingerprinting the card",
        CDP,
        "  try { out.fp = fingerprint(cardText(c, '[class*=\"\"buttonContainer\"\"]')); } catch (e) { out.fp = ''; }",
        "  out.fp = '';",
        "both readers fingerprint the card without its buttons",
    ),
    (
        # RA-036: Init's immediate scan ran whether or not a UI thread existed to receive it, so the app's
        # convention runner scanned the developer's real transcripts beside this self-test. Put it back.
        "burn: Init starts the immediate scan with no UI context again",
        MODULE,
        "            if (_ui != null) OnTick(null, EventArgs.Empty);\n            else Log(\"no UI context at Init, so the first scan waits for the timer\");",
        "            OnTick(null, EventArgs.Empty);",
        "with no UI context Init does not start the immediate scan",
    ),
    (
        # RA-036, the other direction: the shipped host has a context and must still get its first scan at once.
        "burn: Init attempts no first tick even with a UI context",
        MODULE,
        "            if (_ui != null) OnTick(null, EventArgs.Empty);",
        "            if (_ui == null && _ui != null) OnTick(null, EventArgs.Empty);",
        "with a UI context Init attempts the first tick at once",
    ),
    (
        # RA-046: the first word was the text up to the first space, so a quoted path with a space in it was
        # cut mid-directory and a directory NAME reached the log. Put the space cut back.
        "burn: RA-046 the first word of a command is cut at its first space again",
        DETECTOR,
        "            first = FirstToken(first, powerShell, out rest);",
        "            int space = first.IndexOfAny(new[] { ' ', '\\t', '\\n', '\\r' });\n            if (space > 0) first = first.Substring(0, space);\n            rest = first;",
        "a quoted executable path with a space is logged as its leaf",
    ),
    (
        # RA-046, PowerShell: the call operator is not the executable.
        "burn: RA-046 PowerShell's call operator is logged as the executable",
        DETECTOR,
        "            if (powerShell && first == \"&\") first = FirstToken(rest, true, out rest);",
        "            if (powerShell && first == \"&&\") first = FirstToken(rest, true, out rest);",
        "PowerShell's call operator on a quoted path logs the executable",
    ),
    (
        # R-004: the chime's outcome becomes the switch again, so a refused chime is logged as a signal.
        "burn: R-004 a refused chime counts as delivered again",
        MODULE,
        "                try { result.Chimed = host.PlayNotificationSound(Info.Id); }",
        "                try { host.PlayNotificationSound(Info.Id); result.Chimed = true; }",
        "a chime the app refused is held back, not logged as a signal",
    ),
    (
        # R-004: the animation reports a pet whether or not one was there.
        "burn: R-004 an animation with no pet counts as delivered again",
        MODULE,
        "            host.PlayAnimationAll(candidates);\n            PruneCompanions();\n            return _companions.Count > 0;",
        "            host.PlayAnimationAll(candidates);\n            PruneCompanions();\n            return true;",
        "an animation with no pet on screen is held back",
    ),
    (
        # RA-025: the screen one-shot is spent before anything could carry it.
        "burn: RA-025 a screen notice nobody can hear spends the one-shot again",
        MODULE,
        "            if (delivery.Wanted && !delivery.Delivered)\n            {\n                string heldBack = \"held back a screen notice about \"",
        "            if (false && delivery.Wanted && !delivery.Delivered)\n            {\n                string heldBack = \"held back a screen notice about \"",
        "the same screen prompt is still announced once a companion appears",
    ),
    (
        # RA-026: the screen notice goes back to speech alone.
        "burn: RA-026 the screen notice honours speech alone again",
        MODULE,
        "            Delivery delivery = Deliver(seen.Notice ?? (\"Something is waiting for you: \" + seen.Subject + \".\"));",
        "            var delivery = new Delivery { Wanted = true }; if (NotifySpeakOn && AgentMode.Speaks(Mode) && _host.SpeechEnabled && AnyCompanionCanSpeak()) { _host.SayAll(seen.Notice ?? (\"Something is waiting for you: \" + seen.Subject + \".\")); delivery.Spoke = true; }",
        "a screen prompt chimes when the chime is on and speech is off",
    ),
    (
        # RA-048: Retain is fed the Blocked keys alone again, so a call that reads Working for a tick is re-armed.
        "burn: RA-048 Retain forgets a call whose session read Working for one tick",
        MODULE,
        "                if (detection.Session != null && detection.Session.Outstanding != null)\n                    foreach (OutstandingCall call in detection.Session.Outstanding)",
        "                if (detection.Outcome == DetectionOutcome.Blocked && detection.Session != null && detection.Session.Outstanding != null)\n                    foreach (OutstandingCall call in detection.Session.Outstanding)",
        "a call that reads Working for one tick keeps its one-shot",
    ),
    (
        # RA-028: the watch section claims coverage on the port alone again, against the tray's Able.
        "burn: RA-028 the watch section claims coverage while the panel cannot be read",
        MODULE,
        "            get { return WatchStateLine(_lastSessions, _lastStoodDown, AutoApproveState == ApproveState.Able); }",
        "            get { return WatchStateLine(_lastSessions, _lastStoodDown, _portAnswering && AutoApprove); }",
        "the watch section does not claim auto-approve covers prompts while the panel cannot be read",
    ),
    (
        # RA-029: the note drops what project rules still do.
        "burn: RA-029 the no-rule-files note says nothing about project rules again",
        MODULE,
        "            + \"only project-scope rules in a session's own folder can mark a Claude call allowed; every \"\n            + \"other stalled Claude command call is treated as a prompt, and the approvals audit records \"\n            + \"only what those project rules allow until a home file appears\";",
        "            + \"every stalled Claude command call is treated as a prompt (nothing can be recognised as allowed) \"\n            + \"and the approvals audit records nothing until one appears\";",
        "the note says what project rules still do",
    ),
    (
        # RA-027: the count of unlistable folders never reaches the notes channel the tick logs from.
        "burn: RA-027 ScanRoot drops the inaccessible-folder note",
        MODULE,
        "            if (inaccessible > 0 && resetNotes != null) resetNotes.Add(InaccessibleNote(agent, inaccessible));",
        "            if (false && inaccessible > 0 && resetNotes != null) resetNotes.Add(InaccessibleNote(agent, inaccessible));",
        "the scan SAYS a folder could not be listed",
    ),
    (
        # RA-033: an undecidable call is filed under idle again.
        "burn: RA-033 Check now files an undecidable call under idle again",
        MODULE,
        "                    case DetectionOutcome.NotDecidable: undecidable++; break;",
        "                    case DetectionOutcome.NotDecidable: idle++; break;",
        "files a slow-but-allowed call, an undecidable call and a no-calls-yet session under their own names",
    ),
    (
        # RA-030 / RA-053: a sentence names a button by hand again. Any spelling the pane does not offer
        # fails, which is what makes this stronger than a check for the three known-stale names.
        "burn: RA-030 the setup line names a button by hand again",
        MODULE,
        "                    return \"Not set up. Press \\u201c\" + EnableLabel + \"\\u201d, then restart VS Code.\";",
        "                    return \"Not set up. Press \\u201cEnable approving\\u201d, then restart VS Code.\";",
        "every button the setup line, the Enable result and Inspect's two details name is one the pane offers",
    ),
    (
        # RA-043: the press card goes back to promising one-call-only whatever the opt-ins say.
        # The pattern stops at "ticked " on purpose: AgentFlowPane.cs writes its curly quotes as the
        # CHARACTERS (its own convention; AgentFlowModule.cs writes them as \\u201c escapes), and a
        # pattern carrying either spelling of them reported NO-OP against the other -- the silent loss
        # of coverage this harness's header warns about, caught by running the case rather than by
        # reading it. Escape-free text cannot go stale that way.
        "burn: RA-043 the press card ignores the all-projects opt-in again",
        PANE,
        "            if (allProjects)\n                card += \" You have ticked ",
        "            if (false)\n                card += \" You have ticked ",
        "with 'for all projects' ticked the card stops claiming one-call-only",
    ),
    (
        # RA-044: the log note goes back to the unconditional promise.
        "burn: RA-044 the log note drops its condition again",
        PANE,
        "            return \"While diagnostic logging is on for this module (Preferences, Diagnostic log), every \"\n                   + \"press and each distinct refusal is recorded in the app's log\"",
        "            return \"Every press and refusal is recorded in the app's diagnostic log\"",
        "the log note names the condition under which the log has anything in it",
    ),
    (
        # RA-050: the reason string claims a declined one-call row whether or not the prompt had one.
        "burn: RA-050 the all-projects reason claims a declined row that was never there",
        PROMPTOPTS,
        "                    approvals.Count > 0 ? \"declined the one-call row\" : \"this prompt offered no one-call row\");\n                return decision;\n            }\n\n            // Codex's wider row",
        "                    \"declined the one-call row\");\n                return decision;\n            }\n\n            // Codex's wider row",
        "rather than claiming it declined a row that was never there",
    ),
    (
        # RA-052: Disable goes back to deleting the whole line the key sits on, which is right for
        # the pretty-printed file VS Code ships and takes the siblings with it on every other shape.
        "burn: RA-052 Disable deletes the whole line holding the port key again",
        VSCODE,
        "            string spliced = text.Substring(0, start) + text.Substring(cut);\n            return FixDanglingComma(RemoveBlankLineAt(spliced, start));",
        "            int lineStart = text.LastIndexOf('\\n', keyAt) + 1;\n            int lineEnd = text.IndexOf('\\n', keyAt);\n            if (lineEnd < 0) lineEnd = text.Length; else lineEnd++;\n            return FixDanglingComma(text.Substring(0, lineStart) + text.Substring(lineEnd));",
        "a one-line argv.json keeps its sibling members when the port is removed",
    ),
    (
        # R-007: a settings file whose read failed is cached as 'no rules' again, under a stat key
        # that never moves, so the user's rules stay invisible until the file is edited.
        "burn: R-007 the rule cache stores a read that did not happen again",
        RULELOADER,
        "                if (unreadable > 0)\n                {\n                    // DO NOT CACHE A READ THAT DID NOT HAPPEN (R-007).",
        "                if (unreadable < 0)\n                {\n                    // DO NOT CACHE A READ THAT DID NOT HAPPEN (R-007).",
        "an unreadable settings file yields no rules for THIS tick and is not cached as 'no rules'",
    ),
    (
        # RA-034: the tray toggle drops Save()'s answer again.
        "burn: RA-034 the tray toggle drops a failed settings write again",
        MODULE,
        "            if (!_settings.Save())\n                Log(\"the tray's auto-approve pick was not persisted (mode \" + Mode + \"), so it reverts at the next launch\");",
        "            _settings.Save();",
        "a tray toggle whose settings write fails says so",
    ),
    # ---- lane fix/deadcode ----
    # F047: the detailed split's separators were produced and read by nothing; the splitter self-test
    # now pins them. F048: FakeCdpServer serves connections concurrently, so the WIRE press case runs
    # Decide -> Click over a second connection inside the sweep. F052: the U+001F signature separator
    # is spelled as an escape and asserted to be a control character. F054: RuleLoader's home override
    # goes through TranscriptReader.FullyQualifiedOverride, and a drive-relative value is refused.
    (
        "deadcode: the splitter stops recording separators",
        SPLITTER,
        "                    segments.Add(new CommandSegment { Value = value, Separator = separator });",
        "                    segments.Add(new CommandSegment { Value = value, Separator = null });",
        "detailed split records each segment's leading separator",
    ),
    (
        "deadcode: Click stops reporting the outcome of the evaluated template",
        CDP,
        "                        string result = session.Evaluate(sessionId, expression);\n                        return string.IsNullOrEmpty(result) ? \"gone\" : result;",
        "                        string result = session.Evaluate(sessionId, expression);\n                        return \"gone\";",
        "WIRE the production callback presses through a second connection",
    ),
    (
        "deadcode: the signature separator becomes a visible character",
        BUDGETPRESS,
        "text.Append('\\u001F');",
        "text.Append('|');",
        "the signature separator is a control character",
    ),
    (
        "deadcode: the home override honours a drive-relative path again",
        RULELOADER,
        "            string over = TranscriptReader.FullyQualifiedOverride(HomeVariable);",
        "            string over = Environment.GetEnvironmentVariable(HomeVariable);",
        "a drive-relative AGENTFLOW_CLAUDE_HOME is ignored",
    ),
)


def read(path):
    # PLAIN utf-8, not utf-8-sig, in BOTH directions. utf-8-sig strips a leading BOM on read and
    # writes one on every write, so restore() handed a BOM to every target that never had one:
    # RuleLoader.cs, TranscriptCursor.cs and VsCodeSetup.cs are BOM-less, and the first run that
    # listed them left all three "restored" one byte longer than the baseline -- a restore that is
    # not byte-identical, in the harness whose header promises exactly that (lane fix/agentflow,
    # 2026-09-29). With plain utf-8 a BOM survives as the U+FEFF character at the head of the text
    # and is written back as it came; no pattern in this file starts at column 0 of line 1, so the
    # character is never inside a match.
    with io.open(path, "r", encoding="utf-8", newline="") as handle:
        return handle.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def build(relaxed=False):
    """Build the MODULE, not the host. Returns True when it compiled.

    TreatWarningsAsErrors is turned OFF for the mutation builds only (relaxed=True), and that is a
    fix rather than a loosening. modules/Directory.Build.props gained WarningLevel 4 and
    TreatWarningsAsErrors on 2026-09-17, which is right for real code -- and it silently broke
    NINE of this file's cases, taking the suite from 23/23 to 14/23 with the other nine reporting
    BROKEN (does not compile). The cause is that several mutations disable a line with
    `if (false)`, which is CS0162 unreachable code: a warning, and therefore now an error.

    A mutation is a deliberate temporary break whose only job is to make one assertion fail. Its
    build has nothing to prove about warning policy. The BASELINE build and the restoring rebuild
    in the finally run under the real settings, so a genuine new warning in the real source fails
    the baseline here and a tree that no longer builds strictly is reported at the end. That was
    this docstring's claim before 2026-09-30 too, while every build in the file passed the relaxed
    flag (F401); the parameter is what made the sentence true.

    Recorded at length because of how it was found: the suite was run in full as the last step of
    a session, having been run case-by-case for hours. A suite nobody runs whole is a suite that
    reports more coverage than it has, which is the exact defect this file caught in
    mutate-diagnostics.py earlier the same day.
    """
    arguments = ["dotnet", "build", CSPROJ, "-c", "Release", "--nologo", "-v:quiet"]
    if relaxed:
        arguments.append("-p:TreatWarningsAsErrors=false")
    proc = subprocess.run(arguments, capture_output=True, text=True, timeout=900)
    return proc.returncode == 0, proc.stdout or ""


def run_selftest():
    """(ok, report) from the module self-test, or (None, why) when it could not run."""
    try:
        os.remove(MARKER)
    except OSError:
        pass
    if os.path.isfile(MARKER):
        return None, "stale marker could not be removed: " + MARKER
    proc = subprocess.run([EXE, "--module-selftest=agentflow"],
                          capture_output=True, text=True, timeout=900, env=CHILD_ENV)
    if not os.path.isfile(MARKER):
        return None, "no marker file written (exit %d)" % proc.returncode
    report = read(MARKER)
    # COLUMN 0, unstripped. ModuleConventionSelfTest re-emits the module's own RESULT=PASS as
    # '  [agentflow] RESULT=PASS', so `"RESULT=PASS" in report` was true whenever the module's probe
    # passed and a host-side convention check (tray icons, unsubscribe on Shutdown) did not.
    return any(line.startswith("RESULT=PASS") for line in report.splitlines()), report


def failing_lines(report, ):
    # STARTS WITH, not contains. A passing assertion whose LABEL mentions failure is prose, not a
    # verdict: the aibrain self-test has "reported as a FAILURE, not a green tick" on a PASS line,
    # and a bare substring match turns a green report red. Allows the "[moduleid] " prefix the
    # module self-tests print in front of the verdict.
    out = []
    for line in report.splitlines():
        stripped = line.strip()
        if stripped.startswith("RESULT="):
            continue
        verdict = stripped
        if verdict.startswith("[") and "] " in verdict:
            verdict = verdict.split("] ", 1)[1].strip()
        if verdict.startswith("FAIL"):
            out.append(stripped)
    return out



def line_ending_variant(base, old, new):
    """Pick the (old, new) pair whose line endings match the FILE being mutated.

    Every pattern in this file is written with LF. Several target files are CRLF in the working tree,
    and a CRLF file cannot contain an LF pattern, so those cases printed "NO-OP (pattern matched 0
    times)" and covered nothing at all -- a silent loss of coverage, which the release checklist is
    explicit is worse than a failure because it looks like a result.

    Measured 2026-09-25: this accounted for ALL SEVEN no-op cases across this harness and its sibling
    (one here, six in mutate-agentflow.py). Every one of them was read as "the source moved"; none of
    them had. CompanionsPaneControl.cs, for instance, is 1014 CRLF lines and 0 bare LF.

    Restoring is unaffected either way: the loops below write back the ORIGINAL bytes they read, so a
    file's line endings are never rewritten by a mutation run.
    """
    if base.count(old) == 1:
        return old, new
    # TYPE-AGNOSTIC on purpose. This harness carries its patterns as str while its two siblings use
    # bytes, so a bytes-only implementation raised TypeError here rather than returning a variant.
    if isinstance(old, bytes):
        crlf, lf = b"\r\n", b"\n"
    else:
        crlf, lf = "\r\n", "\n"
    as_crlf = lambda x: x.replace(crlf, lf).replace(lf, crlf)
    crlf_old, crlf_new = as_crlf(old), as_crlf(new)
    if base.count(crlf_old) == 1:
        return crlf_old, crlf_new
    return old, new

def main():
    global RUN_TEMP, MARKER, CHILD_ENV
    # Short on purpose: a self-test's own temp paths sit below this, and one of them (the fortunes
    # VectorCache fallback probe) reaches MAX_PATH once TEMP itself is ~107 characters. Measured 2026-09-29.
    RUN_TEMP = os.path.join(os.environ.get("TEMP", "."), "dp-maf-" + uuid.uuid4().hex[:12])
    MARKER = os.path.join(RUN_TEMP, "dp-module-agentflow-selftest.txt")
    os.makedirs(RUN_TEMP)
    CHILD_ENV = dict(os.environ)
    CHILD_ENV["TEMP"] = RUN_TEMP
    CHILD_ENV["TMP"] = RUN_TEMP
    try:
        return score()
    finally:
        # The SelfTestScratch sweep inside the child sweeps THIS directory now, so nothing else
        # would collect it.
        shutil.rmtree(RUN_TEMP, ignore_errors=True)


def score():
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
            find_v, replace_v = line_ending_variant(source, find, replace)
            count = source.count(find_v)
            if count != 1:
                print("  %-52s NO-OP (pattern matched %d times)" % (name, count))
                continue

            before_stamp = os.path.getmtime(DLL) if os.path.isfile(DLL) else 0.0
            write(path, source.replace(find_v, replace_v))
            # A same-second rebuild can leave the timestamp unchanged on a coarse
            # filesystem clock, which would read as "never rebuilt".
            time.sleep(1.1)
            compiled, output = build(relaxed=True)
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
            # `expected` may be a tuple of fragments; EVERY one must appear among the failing lines. An
            # any-match would let a merged case pass with one of its assertions deleted, which is exactly
            # the coverage loss this harness exists to catch (F400).
            fragments = expected if isinstance(expected, tuple) else (expected,)
            hit = [line for line in lines if any(fragment in line for fragment in fragments)]
            missing = [fragment for fragment in fragments if not any(fragment in line for line in lines)]
            if hit and not missing:
                fired += 1
                extra = (" (+%d other failures)" % (len(lines) - len(hit))) if len(lines) > len(hit) else ""
                print("  %-52s FIRED%s" % (name, extra))
                print("        %s" % hit[0])
            else:
                print("  %-52s WRONG -- failed on something else:" % name)
                for fragment in missing:
                    print("        missing: %s" % fragment)
                for line in lines[:3]:
                    print("        %s" % line)
    finally:
        restore()
        # Leave the tree building, so a later gate run is not measuring a mutant -- under the REAL
        # settings, and CHECKED. This call passed the relaxed flag and discarded its result (F401), so a
        # restored tree that no longer built strictly was the one outcome nobody would have seen here.
        strict_rebuilt, strict_output = build()
        if not strict_rebuilt:
            print("\nTHE STRICT REBUILD OF THE RESTORED TREE FAILED -- build\\ may hold the last mutant's DLL:")
            print(strict_output[-800:])

    print("\n%d/%d fired." % (fired, len(cases)))
    return 0 if fired == len(cases) and strict_rebuilt else 1


if __name__ == "__main__":
    sys.exit(main())
