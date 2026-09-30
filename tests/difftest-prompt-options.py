#!/usr/bin/env python3
"""The C# prompt-option classifier must agree with the Python reference on every case.

WHY A DIFFERENTIAL AND NOT TWO SUITES. `modules/AgentFlow/PromptOptions.cs` is a port of
`docs/agentflow/agentflow_classifier.py`, and the reference is the one that carries the bundle
transcription plus an `--audit` mode that re-derives the option set from whatever Claude Code is
installed. Two independent test suites drift; a differential cannot, because a disagreement is the
failure.

This parses every `KindOf("...") == OptionKind.X` assertion out of the C# self-test, runs the same
strings through the Python classifier, and requires the verdicts to match. It also walks the C#
table itself, so an entry added on one side and not the other is caught. The `Choose(...)` cases
are NOT parsed: they are decisions over several options, and it is the classifier that is ported.
Nor are the `PromptOptions.Classify("Allow once ⏎", ...)` assertions in SelfCheckCodexOptions: that
keyboard hint is a property of Codex's webview, read off a live prompt, which no bundle on the machine
can audit, so the reference deliberately carries no StripKeyboardHint port (docs/DESIGN-REGISTER.md,
`#### fix/agentflow`) and those rows are pinned by the module self-test in the gate instead.
Every case this prints as compared carries an expectation on both sides; the reference-only smoke
strings are counted separately (F391: 24 of a reported 78 cases used to compare nothing).

    python tests/difftest-prompt-options.py

The splitter has the same arrangement at a larger scale (19,424 cases, three implementations), and
it is what caught a real defect there rather than a theoretical one.
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "docs", "agentflow"))
import agentflow_classifier as REF   # noqa: E402

CS_TABLE = os.path.join(ROOT, "modules", "AgentFlow", "PromptOptions.cs")
CS_TESTS = os.path.join(ROOT, "modules", "AgentFlow", "AgentFlowModule.cs")

# C# OptionKind -> the reference's class constant.
KIND_MAP = {
    "Unknown": REF.UNKNOWN,
    "ApproveOnce": REF.APPROVE_ONCE,
    "ApproveWider": REF.APPROVE_WIDER,
    "ApproveAllProjects": REF.APPROVE_ALL_PROJECTS,
    "ModeChange": REF.MODE_CHANGE,
    "Reject": REF.REJECT,
    "FreeText": REF.FREE_TEXT,
    "ApproveSimilar": REF.APPROVE_SIMILAR,
}

_ENTRY = re.compile(r'Entry\("((?:[^"\\]|\\.)*)",\s*OptionKind\.(\w+)\)')
_KINDOF = re.compile(r'KindOf\((?:"((?:[^"\\]|\\.)*)"|null)\)\s*==\s*OptionKind\.(\w+)')
_CS_ESCAPE = re.compile(r'\\u([0-9a-fA-F]{4})|\\(.)')


def unescape(text):
    """C# string literal -> the characters it denotes."""
    def one(match):
        if match.group(1):
            return chr(int(match.group(1), 16))
        simple = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "0": "\0"}
        return simple.get(match.group(2), match.group(2))
    return _CS_ESCAPE.sub(one, text)


def read(path):
    with io.open(path, encoding="utf-8-sig") as handle:
        return handle.read()


def main():
    table_src = read(CS_TABLE)
    tests_src = read(CS_TESTS)

    cases = []   # (origin, text_or_None, expected_cs_kind_or_None)

    # 1. Every entry in the C# table, as a literal option string. A template entry has a runtime
    #    value appended, so it is exercised with one -- a bare prefix is a DIFFERENT case and the
    #    reference treats it as such, which is the behaviour worth pinning: it is the table's own
    #    exact kind when the stripped prefix is itself an exact entry, and Unknown otherwise. It
    #    used to carry None and compare nothing (F391).
    entries = _ENTRY.findall(table_src)
    if not entries:
        print("FAIL: parsed 0 table entries out of PromptOptions.cs -- the regex is stale, and a")
        print("      differential that reads nothing passes vacuously.")
        return 2
    exact_kinds = {unescape(literal): kind for literal, kind in entries
                   if not unescape(literal).endswith(" ")}
    for literal, kind in entries:
        text = unescape(literal)
        if text.endswith(" "):
            cases.append(("table-template", text + "example.com", kind))
            cases.append(("table-bare-prefix", text.strip(),
                          exact_kinds.get(text.strip(), "Unknown")))
        else:
            cases.append(("table-exact", text, kind))

    # 1b. Every rule DESTINATION, parsed out of the C# source rather than retyped here.
    #
    # These are not table entries -- the destination rule lives in code, ahead of the table --
    # so without this the axis is DEGENERATE: the differential would pass while exercising the
    # new logic zero times, which is precisely the shape of vacuous pass this file exists to
    # refuse. Failing when the list cannot be parsed is the same principle as the entry check.
    destinations = re.findall(r'"( for [^"]+)",', table_src)
    if not destinations:
        print('FAIL: parsed 0 rule destinations out of PromptOptions.cs -- the regex is stale,')
        print('      and the destination axis would be exercised zero times.')
        return 2
    for destination in destinations:
        expected = ('ApproveAllProjects' if destination == ' for all projects'
                    else 'ApproveWider')
        # A realistic rendered label: the middle is arbitrary user text, which is exactly why
        # the classifier anchors on the suffix rather than trying to match the whole thing.
        cases.append(("destination",
                      'Yes, allow python -c "import x" and Bash(git *)' + destination,
                      expected))
        cases.append(("destination-short", 'Yes, allow x' + destination, expected))

    # 2. Every KindOf case asserted in the C# self-test, with the kind it asserts.
    asserted = _KINDOF.findall(tests_src)
    if not asserted:
        print("FAIL: parsed 0 KindOf cases out of AgentFlowModule.cs -- the regex is stale.")
        return 2
    for literal, kind in asserted:
        cases.append(("selftest", unescape(literal) if literal else None, kind))

    # 3. Reference smoke: the shapes where a port typically diverges from its reference (chrome,
    #    apostrophe variants, ellipses, whitespace, truncated templates, casing). These carry NO
    #    expectation here and are NOT counted as compared -- on their own they prove only that the
    #    reference does not throw. Their expectations live in SelfCheckPromptOptions
    #    (AgentFlowModule.cs) as KindOf(...) assertions, which section 2 parses, and the check below
    #    REQUIRES every one of them to be asserted there, so each IS compared, through that route,
    #    with the C# executed on it by --module-selftest=agentflow. Until 2026-09-29 this block was
    #    counted among the compared cases while comparing nothing (F391).
    smoke = ("YES", "  2. YES  ", "❯ Yes", "1) Yes, and don't ask again",
             "Yes, and don’t ask again", "Yes, and donʼt ask again",
             "Other…", "Other...", "yes ", " yes", "Yes please", "Yes,",
             "Yes, allow", "Yes, allow ", "Yes, allow access to",
             "allow all edits this session", "", "   ", "no", "NO, KEEP planning")
    asserted_texts = set(unescape(literal) if literal else "" for literal, _kind in asserted)
    unpinned = [text for text in smoke if text not in asserted_texts]
    if unpinned:
        print("FAIL: %d reference smoke string(s) have no KindOf assertion in the C# self-test, so"
              " nothing pins the port on them: %s" % (len(unpinned), ", ".join(repr(t) for t in unpinned)))
        return 1
    for text in smoke:
        cases.append(("smoke", text, None))

    mismatches = []
    compared = 0
    smoke_only = 0
    for origin, text, expected_cs in cases:
        ref_kind, _matched = REF.classify(text if text is not None else "")
        if expected_cs is None:
            smoke_only += 1
            continue
        compared += 1
        want = KIND_MAP.get(expected_cs)
        if want is None:
            mismatches.append((origin, text, expected_cs, ref_kind,
                               "C# kind not in the mapping table"))
        elif want != ref_kind:
            mismatches.append((origin, text, expected_cs, ref_kind,
                               "C# asserts %s, reference says %s" % (want, ref_kind)))

    print("cases compared: %d of %d listed (%d reference-only smoke strings, each also pinned by a"
          " C# assertion; from %d table entries and %d self-test assertions)"
          % (compared, len(cases), smoke_only, len(entries), len(asserted)))

    # The mapping must cover every kind the C# enum defines, or a new kind silently skips
    # checking.
    #
    # SCOPED TO THE OptionKind BLOCK. It used to scan the whole file for `Name = <n>,`,
    # which is not a description of OptionKind but of "any enum member with an explicit
    # value anywhere in PromptOptions.cs". The day a second enum appeared in that file
    # (RefusalKind, 1.4.4) this reported `None` as an unmapped OptionKind and failed a
    # differential that had nothing wrong with it. An unscoped scan standing in for a
    # scoped one is the same mistake the option audit made against the bundle.
    block = re.search(r"public enum OptionKind\s*\{(.*?)^\s*\}", table_src, re.S | re.M)
    if not block:
        print("FAIL: could not find the OptionKind enum -- this check is no longer "
              "checking anything, which is worse than it failing")
        return 1
    enum_kinds = set(re.findall(r"^\s*(\w+) = \d+,", block.group(1), re.M))
    if not enum_kinds:
        print("FAIL: the OptionKind block parsed to zero members")
        return 1
    missing = sorted(enum_kinds - set(KIND_MAP))
    if missing:
        print("FAIL: OptionKind has %d value(s) this differential cannot map: %s"
              % (len(missing), ", ".join(missing)))
        return 1

    if mismatches:
        print("\n%d DISAGREEMENT(S):" % len(mismatches))
        for origin, text, cs, ref, why in mismatches[:20]:
            print("  %-18s %-44r %s" % (origin, text, why))
        return 1

    print("no disagreements: the port and the reference classify every case identically.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
