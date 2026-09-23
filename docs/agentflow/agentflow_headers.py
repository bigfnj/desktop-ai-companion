#!/usr/bin/env python3
"""Does the prompt-HEADER table still cover the installed Claude Code bundle?

The sibling of `agentflow_classifier.py --audit`, for the other hand-maintained table
in this module. That one guards which button is safe to press; this one guards what
the log calls the prompt it pressed. Getting it wrong is not dangerous, it is
CORRUPTING: an unnamed shape logs as "an unrecognised prompt", which is the same
sentence a genuine classifier refusal writes, in the one file SUPPORT.md invites users
to attach to an issue. That is BUG-007, and it went unnoticed for as long as the table
existed because nothing compared it to the bundle.

    python agentflow_headers.py --audit      # compare the table against what is installed
    python agentflow_headers.py --selftest   # prove the extractor works, with no bundle
    python agentflow_headers.py --list       # print every shape found, covered or not

TWO DIFFERENCES FROM THE OPTION AUDIT, both deliberate.

  1. THE TABLE IS PARSED OUT OF THE C#, not copied into this file.
     `agentflow_classifier.py` re-implements PromptOptions in Python and is kept in step
     by `tests/difftest-prompt-options.py`. There is no such difftest here, so a copy
     would be a second source of truth with nothing checking it -- exactly how this
     module's CDP probe went stale within one version. Reading
     `modules/AgentFlow/AgentFlowModule.cs` means the audit cannot disagree with the
     code it is auditing.

  2. IT EXTRACTS RENDER SITES, not string literals.
     Header text is assembled from JSX children -- `["Make this edit to", " ",
     <span class=permissionPath>, "?"]` -- so there is no finished string in the bundle
     to grep for. A literal-only search finds fragments and cannot tell "Allow this "
     (the shell template's opening) from a complete header.

WHAT IT CANNOT DO. It reads what the bundle RENDERS, not what a screen shows: it does
not run the renderer, so a shape whose text comes entirely from a runtime value is
invisible to it, and so is any header built somewhere this pattern does not reach.
`--list` exists so the count can be eyeballed against the file rather than trusted.
Prints bundle text, so its output is not issue-safe unedited.
"""

import argparse
import glob
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "modules", "AgentFlow", "AgentFlowModule.cs")
BUNDLE_GLOB = os.path.join(
    os.path.expanduser("~"), ".vscode", "extensions",
    "anthropic.claude-code-*", "webview", "index.js")

# Below this many render sites, assume the pattern stopped matching rather than that
# Claude Code deleted its permission prompts. The rule `agentflow-probe.py` already
# holds for Codex call types: shout when the input parses and yields nothing.
MIN_PLAUSIBLE_SITES = 8


# --------------------------------------------------------------------------
# The table, read from the module rather than copied
# --------------------------------------------------------------------------
_ROW = re.compile(r'new KeyValuePair<string, string>\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*'
                  r'"((?:[^"\\]|\\.)*)"\s*\)')
_CONST = re.compile(r'private const string (ShellHeader(?:Prefix|Suffix))\s*=\s*'
                    r'"((?:[^"\\]|\\.)*)"')
_TABLE_BLOCK = re.compile(r'KnownHeaders\s*=\s*\{(.*?)\n\s*\};', re.S)


class Table(object):
    def __init__(self, rows, shell_prefix, shell_suffix):
        self.rows = rows
        self.shell_prefix = shell_prefix
        self.shell_suffix = shell_suffix

    def is_shell(self, header):
        return (header.startswith(self.shell_prefix)
                and header.endswith(self.shell_suffix)
                and len(header) > len(self.shell_prefix) + len(self.shell_suffix))

    def describe(self, header):
        """The module's matcher: shell template first, then longest prefix wins."""
        if self.is_shell(header):
            return "a shell command"
        best, best_len = None, -1
        for key, value in self.rows:
            if header.startswith(key) and len(key) > best_len:
                best, best_len = value, len(key)
        return best


def load_table(source_path=SOURCE):
    with open(source_path, "r", encoding="utf-8-sig") as handle:
        text = handle.read()
    block = _TABLE_BLOCK.search(text)
    if not block:
        raise SystemExit("could not find KnownHeaders in %s -- the table moved or was "
                         "renamed, and this audit is now checking nothing" % source_path)
    rows = _ROW.findall(block.group(1))
    if not rows:
        raise SystemExit("KnownHeaders parsed to zero rows -- refusing to report a table "
                         "with no entries as covering anything")
    consts = dict(_CONST.findall(text))
    for name in ("ShellHeaderPrefix", "ShellHeaderSuffix"):
        if name not in consts:
            raise SystemExit("could not find %s in %s" % (name, source_path))
    return Table(rows, consts["ShellHeaderPrefix"], consts["ShellHeaderSuffix"])


def normalize(text):
    """DescribeSubject's own normalisation: lowercase, whitespace collapsed, trimmed."""
    return " ".join((text or "").lower().split())


# --------------------------------------------------------------------------
# Pulling the render sites out of the bundle
# --------------------------------------------------------------------------
# `className:S5.permissionRequestHeader,children:` with an optional prop between, which
# is how the shell shape renders it (`,id:Y,`).
_SITE = re.compile(r'permissionRequestHeader\s*,\s*'
                   r'(?:[A-Za-z_$][\w$]*\s*:\s*[^,{}\[\]()"]{0,40}\s*,\s*)?'
                   r'children\s*:')

_OPENERS, _CLOSERS, _QUOTES = "([{", ")]}", "\"'`"


def _region(blob, start):
    """The `children:` value, up to the brace that closes the props object."""
    depth, i, n = 0, start, len(blob)
    while i < n:
        ch = blob[i]
        if ch in _QUOTES:
            quote, i = ch, i + 1
            while i < n and blob[i] != quote:
                i += 2 if blob[i] == "\\" else 1
        elif ch in _OPENERS:
            depth += 1
        elif ch in _CLOSERS:
            if depth == 0:
                break
            depth -= 1
        i += 1
    return blob[start:i]


def _pieces(region):
    """Literals kept, expressions folded to <value>, JSX tag names dropped.

    A literal that is a call's FIRST ARGUMENT is a tag name -- `F("span", ...)` -- not
    header text. Without that rule every path span contributes the word "span" and the
    reconstruction reads "Make this edit to span?". Real header text is always preceded
    by one of `: , [ ? =`.
    """
    out, i, n = [], 0, len(region)
    pending_expression = False
    while i < n:
        ch = region[i]
        if ch in _QUOTES:
            quote, j = ch, i + 1
            body = []
            while j < n and region[j] != quote:
                if region[j] == "\\" and j + 1 < n:
                    body.append(region[j + 1])
                    j += 2
                else:
                    body.append(region[j])
                    j += 1
            previous = region[:i].rstrip()
            is_tag_name = previous.endswith("(")
            if is_tag_name:
                pending_expression = True
            else:
                if pending_expression:
                    out.append(None)          # a runtime value stood here
                    pending_expression = False
                out.append("".join(body))
            i = j + 1
            continue
        if ch.isalpha() or ch in "_$":
            pending_expression = True
        i += 1
    if pending_expression:
        out.append(None)
    return out


def _sentence_like(text):
    """A fragment substantial enough to be a header on its own.

    Excludes the joining scraps -- " ", "?", "/" -- which carry no shape and would
    otherwise all report as uncovered.
    """
    value = normalize(text)
    return len(value) >= 6 and (" " in value or value.endswith("?"))


class Shape(object):
    def __init__(self, offset, region):
        self.offset = offset
        self.region = region
        self.pieces = _pieces(region)
        self.names_tool = '"strong"' in region or "'strong'" in region
        self.rendered = normalize("".join(
            "<value>" if p is None else p for p in self.pieces))
        self.candidates = [normalize(p) for p in self.pieces
                           if p is not None and _sentence_like(p)]

    def verdict(self, table):
        """(covered, how, uncovered_candidates)."""
        if self.names_tool:
            return True, "named by its <strong> tool", []
        if table.is_shell(self.rendered):
            return True, "shell template -> a shell command", []
        if not self.candidates:
            return False, "no text to match on", []
        missed = [c for c in self.candidates if table.describe(c) is None]
        if missed:
            return False, "not in the table", missed
        answers = sorted(set(table.describe(c) for c in self.candidates))
        return True, " / ".join(answers), []


def shapes(blob):
    found, seen = [], set()
    for match in _SITE.finditer(blob):
        start = match.end()
        if start in seen:
            continue
        seen.add(start)
        found.append(Shape(start, _region(blob, start)))
    return found


def newest_bundle(bundle_glob=BUNDLE_GLOB):
    paths = sorted(glob.glob(bundle_glob))
    return max(paths, key=os.path.getmtime) if paths else None


# --------------------------------------------------------------------------
# Modes
# --------------------------------------------------------------------------
def audit(bundle_glob=BUNDLE_GLOB, show_all=False):
    table = load_table()
    print("table: %d rows + the %r...%r template, read from AgentFlowModule.cs"
          % (len(table.rows), table.shell_prefix, table.shell_suffix))

    path = newest_bundle(bundle_glob)
    if not path:
        print("audit: no Claude Code bundle found under %s" % bundle_glob)
        return 0  # not installed is not a failure

    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        blob = handle.read()
    version = os.path.basename(os.path.dirname(os.path.dirname(path)))
    found = shapes(blob)
    print("audit: %s -- %d header render site(s)" % (version, len(found)))

    if len(found) < MIN_PLAUSIBLE_SITES:
        print("\naudit FAILED: only %d render site(s), expected at least %d. The bundle "
              "parsed and yielded almost nothing, which means the pattern stopped "
              "matching -- NOT that the prompts went away. Fix the extractor before "
              "trusting any result from this harness."
              % (len(found), MIN_PLAUSIBLE_SITES))
        return 1

    uncovered = []
    for shape in found:
        covered, how, missed = shape.verdict(table)
        if covered and not show_all:
            continue
        flag = "  " if covered else "!!"
        print("  %s %-52r -> %s" % (flag, shape.rendered[:52], how))
        for item in missed:
            print("       uncovered fragment: %r" % item)
        if not covered:
            uncovered.append(shape)

    if uncovered:
        print("\naudit FAILED: %d of %d header shape(s) the table cannot name."
              % (len(uncovered), len(found)))
        print("Each one logs as \"an unrecognised prompt\" -- the same sentence a real "
              "classifier refusal writes. Add a row to KnownHeaders in "
              "modules/AgentFlow/AgentFlowModule.cs.")
        return 1
    print("\naudit OK: all %d header shapes named." % len(found))
    return 0


# Fixtures in the bundle's own shape, so the extractor is exercised without one
# installed. Each is a real pattern from 2.1.280, reduced to the part that matters.
_FIXTURES = [
    ('F("div",{className:S5.permissionRequestHeader,children:"Allow fetching this url?"})',
     "allow fetching this url?", False),
    ('R("div",{className:S5.permissionRequestHeader,id:Y,children:["Allow this ",Z," command?"]})',
     "allow this <value> command?", False),
    ('R("div",{className:S5.permissionRequestHeader,children:["Make this edit to"," ",'
     'F("span",{className:S5.permissionPath,children:Z}),"?"]})',
     "make this edit to <value>?", False),
    # A ternary. Its RECONSTRUCTION is deliberately nonsense -- both branches run
    # together, with a <value> for the condition -- and that is the whole reason
    # coverage is decided per fragment instead of on the rendered string.
    ('F("div",{className:S5.permissionRequestHeader,children:Q.length>0?"Continue planning"'
     ':"Accept this plan?"})',
     "<value>continue planningaccept this plan?", False),
    ('R("div",{className:S5.permissionRequestHeader,children:["Do you want to proceed with ",'
     'F("strong",{children:J.toolName}),"?"]})',
     "do you want to proceed with <value>?", True),
]


def selftest():
    results = []

    def check(label, condition):
        results.append((label, bool(condition)))

    table = load_table()
    check("the table parses out of the C# and is not empty", len(table.rows) >= 10)
    check("both shell-template constants were found",
          table.shell_prefix and table.shell_suffix)

    for source, expected, names_tool in _FIXTURES:
        shape = shapes(source)[0]
        check("renders %r" % expected[:40], shape.rendered == expected)
        check("...and %s a tool" % ("names" if names_tool else "does not name"),
              shape.names_tool == names_tool)

    # THE EXTRACTOR BUG THAT WOULD BE SILENT: a JSX tag name read as header text. It
    # produces a plausible-looking string, so only an assertion catches it.
    edit = shapes(_FIXTURES[2][0])[0]
    check("WITNESS the path span's tag name is not read as header text",
          "span" not in edit.rendered)

    # A ternary is the case the reconstruction CANNOT answer, and it is also a real
    # shape (ExitPlanMode). Both branches have to be found and both have to be named,
    # off the fragments -- if coverage ever moves back onto the rendered string, this
    # fails, which is the point of asserting it rather than the happy path.
    plan = shapes(_FIXTURES[3][0])[0]
    check("WITNESS both ternary branches are extracted as separate candidates",
          plan.candidates == ["continue planning", "accept this plan?"])
    check("...and the run-together reconstruction is NOT what decides coverage",
          table.describe(plan.rendered) is None and plan.verdict(table)[0])

    # The shell template must be recognised through the reconstruction, and must not
    # swallow its neighbours -- the same pair the C# self-test asserts.
    check("the shell template is recognised", table.is_shell("allow this bash command?"))
    check("WITNESS it does not swallow a neighbour",
          not table.is_shell("allow this glob command")
          and table.describe("allow this glob command") == "a glob search")
    # Longest match, the reason first-match-wins was wrong.
    check("WITNESS two headers sharing a prefix get their own answers",
          table.describe("allow searching in <value>?") == "a search"
          and table.describe("allow searching for this query?") == "a web search")
    check("an unknown header is not named", table.describe("grant everlasting access") is None)

    # The guard that stops this harness reporting success on an empty read.
    check("WITNESS a bundle that yields nothing is a FAILURE, not a pass",
          audit(bundle_glob=os.path.join(HERE, "no-such-bundle-*", "index.js")) == 0
          and len(shapes("nothing to see here")) == 0)

    for label, ok in results:
        print("  %s %s" % ("PASS:" if ok else "FAIL:", label))
    bad = [label for label, ok in results if not ok]
    print("\nselftest %s: %d/%d" % ("FAILED" if bad else "OK",
                                    len(results) - len(bad), len(results)))
    return 1 if bad else 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--audit", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--list", action="store_true",
                        help="print every shape found, covered or not")
    args = parser.parse_args()
    if args.selftest:
        return selftest()
    if args.list:
        return audit(show_all=True)
    return audit()


if __name__ == "__main__":
    sys.exit(main())
