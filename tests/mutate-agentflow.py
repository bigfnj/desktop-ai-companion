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

TARGETS = (SPLITTER, RULES, DETECTOR, BUDGET, MODULE, READER)

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
