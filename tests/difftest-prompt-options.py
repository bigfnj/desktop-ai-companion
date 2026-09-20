#!/usr/bin/env python3
"""The C# prompt-option classifier must agree with the Python reference on every case.

WHY A DIFFERENTIAL AND NOT TWO SUITES. `modules/AgentFlow/PromptOptions.cs` is a port of
`docs/agentflow/agentflow_classifier.py`, and the reference is the one that carries the bundle
transcription plus an `--audit` mode that re-derives the option set from whatever Claude Code is
installed. Two independent test suites drift; a differential cannot, because a disagreement is the
failure.

This parses every `KindOf("...")` and `Choose(...)` case out of the C# self-test, runs the same
strings through the Python classifier, and requires the verdicts to match. It also walks the C#
table itself, so an entry added on one side and not the other is caught.

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
    #    reference treats it as such, which is the behaviour worth pinning.
    entries = _ENTRY.findall(table_src)
    if not entries:
        print("FAIL: parsed 0 table entries out of PromptOptions.cs -- the regex is stale, and a")
        print("      differential that reads nothing passes vacuously.")
        return 2
    for literal, kind in entries:
        text = unescape(literal)
        if text.endswith(" "):
            cases.append(("table-template", text + "example.com", kind))
            cases.append(("table-bare-prefix", text.strip(), None))
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

    # 3. Adversarial shapes, the ones where a port diverges from its reference.
    for text in ("YES", "  2. YES  ", "❯ Yes", "1) Yes, and don't ask again",
                 "Yes, and don’t ask again", "Yes, and donʼt ask again",
                 "Other…", "Other...", "yes ", " yes", "Yes please", "Yes,",
                 "Yes, allow", "Yes, allow ", "Yes, allow access to",
                 "allow all edits this session", "", "   ", "no", "NO, KEEP planning"):
        cases.append(("adversarial", text, None))

    mismatches = []
    checked = 0
    for origin, text, expected_cs in cases:
        ref_kind, _matched = REF.classify(text if text is not None else "")
        checked += 1
        if expected_cs is not None:
            want = KIND_MAP.get(expected_cs)
            if want is None:
                mismatches.append((origin, text, expected_cs, ref_kind,
                                   "C# kind not in the mapping table"))
            elif want != ref_kind:
                mismatches.append((origin, text, expected_cs, ref_kind,
                                   "C# asserts %s, reference says %s" % (want, ref_kind)))

    print("cases: %d  (from %d table entries and %d self-test assertions)"
          % (checked, len(entries), len(asserted)))

    # The mapping must cover every kind the C# enum defines, or a new kind silently skips checking.
    enum_kinds = set(re.findall(r"^\s*(\w+) = \d+,", table_src, re.M))
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
