#!/usr/bin/env python3
"""AgentFlow prompt-option classifier.

Given the option lines read off an agent permission prompt, decide which one (if
any) is safe to press unattended. This is the safety mechanism, not a convenience:
the Claude Code bundle ships TEN distinct strings beginning with "Yes", and only
three of them mean "approve this one call". The others grant a session, write a
permanent rule, or change the permission mode -- one of them sets auto mode as the
user's persistent default.

Three rules, each of which exists because the obvious implementation is wrong.

1. ALLOWLIST, NEVER DENYLIST. An unrecognised option means refuse. A denylist of
   known-wide options fails OPEN the day a new "Yes, ..." variant ships, and
   silently presses it.

2. EXACT MATCH for complete strings, PREFIX MATCH only for templates. Several
   bundle strings end in a space because a runtime value is appended
   ("Yes, allow access to " + host). Plain prefix matching would let "Yes" match
   "Yes, and don't ask again"; plain exact matching would never match a rendered
   template. So: a table entry ending in a space is a prefix template, everything
   else must match exactly, and the LONGEST match wins.

3. ANY UNKNOWN OPTION POISONS THE WHOLE PROMPT. Not just that row. An option we
   cannot classify means either the OCR misread the screen (so every row is
   suspect) or the bundle shipped a new variant (so the model is stale). Both mean
   do not touch it.

    python agentflow_classifier.py --selftest
    python agentflow_classifier.py --audit          # compare table against the bundle
    python agentflow_classifier.py --mutate         # prove the self-test can fail
"""

import argparse
import contextlib
import glob
import io
import os
import re
import sys
import unicodedata

# --------------------------------------------------------------------------
# Classes, in descending order of what they cost you if pressed by mistake.
# --------------------------------------------------------------------------
APPROVE_ONCE = "approve-once"      # the only class we will ever press
APPROVE_WIDER = "approve-wider"    # session-wide or permanent grant
APPROVE_ALL_PROJECTS = "approve-all-projects"   # a rule saved to the USER settings
MODE_CHANGE = "mode-change"        # alters the permission mode, sometimes persistently
REJECT = "reject"                  # declines the call
FREE_TEXT = "free-text"            # "other" / tell the agent something instead
APPROVE_SIMILAR = "approve-similar"   # Codex: this call AND whatever it judges similar
UNKNOWN = "unknown"                # not in the table: refuse the whole prompt

# Table entries ending in a single space are PREFIX TEMPLATES (a runtime value is
# appended). Everything else requires an exact match. Transcribed from
# anthropic.claude-code-2.1.273-win32-x64/webview/index.js; identical in 2.1.269,
# four releases earlier, so the set is stable enough to maintain by hand -- but
# --audit re-derives it from whatever is installed, because this is a bundle that
# auto-updates underneath us.
KNOWN = {
    # -- approve this one call ------------------------------------------------
    "yes": APPROVE_ONCE,
    "yes, allow ": APPROVE_ONCE,
    "yes, allow access to ": APPROVE_ONCE,
    # KNOWN HOLE, recorded 2026-09-30 (F007), not yet closed: a read of exactly "Yes, allow access to"
    # -- the template prefix with nothing appended -- is an INCOMPLETE read, the same truncation the
    # "Yes, allow" WITNESS in selftest() refuses one word earlier. In the 2.1.283/284 bundles that
    # string exists only as a fragment of the directory-grant "don't ask again" row (followed by the
    # grant and " for <destination>"), so a complete read of that row is caught by the destination
    # rule and never reaches this table; the bare fragment is not. Deleting this entry alone closes
    # nothing, because "yes, allow " above accepts "access to" as its runtime suffix. The close is a
    # guard in classify() AND PromptOptions.KindOf returning UNKNOWN for `observed + " " in KNOWN`,
    # with the WITNESS extended and tests/difftest-prompt-options.py's table-bare-prefix rows pinned
    # to Unknown -- both sides at once, because the difftest fails on either half alone. The C# half
    # is outside lane fix/scripts's boundary, so the record stops here.
    "yes, allow access to": APPROVE_ONCE,
    "allow": APPROVE_ONCE,
    # Codex. Its own EXACT entry rather than relying on the bare "allow" above, which is
    # an exact match and does not cover it. Mirrors PromptOptions.cs:129-132.
    "allow once": APPROVE_ONCE,

    # -- wider than one call --------------------------------------------------
    "yes, allow all edits this session": APPROVE_WIDER,
    "yes, and don't ask again": APPROVE_WIDER,

    # -- Codex only: wider than one call, narrower than a standing grant -----
    # Kept as its own class rather than folded into APPROVE_WIDER, matching the C# enum.
    # A user can reasonably want exactly this and nothing wider, and what "similar" MEANS
    # is Codex's judgement rather than ours -- which is also why it is off by default:
    # neither side can state the blast radius of a grant the other agent scopes.
    "allow similar commands": APPROVE_SIMILAR,

    # -- changes the permission mode -----------------------------------------
    "yes, and auto-accept": MODE_CHANGE,
    # 2.1.280: the other branch of the plan prompt's approve label,
    #   _0 = G ? (q === "auto" ? "Yes, and use auto mode" : "Yes, and auto-accept")
    # Found by --audit against the newer bundle. Until it was listed it classified
    # Unknown, and one unknown option refuses the WHOLE prompt -- which on this prompt
    # changed only the REASON given, not the outcome: G is toolName === 'ExitPlanMode',
    # and that prompt's rows are two mode changes and a decline, so there is no
    # approve-once row to press either way. The cost was a refusal that blamed the screen
    # capture instead of saying there was nothing here it was allowed to press.
    "yes, and use auto mode": MODE_CHANGE,
    "yes, and manually approve edits": MODE_CHANGE,
    "yes, return to normal mode": MODE_CHANGE,
    "yes, set auto mode as my default": MODE_CHANGE,

    # -- decline ---------------------------------------------------------------
    "no": REJECT,
    "no, keep ": REJECT,
    "no, keep planning": REJECT,
    # 2.1.280: the same prompt's reject label once feedback has been typed,
    #   m5 = H ? "Send feedback and keep planning" : "No, keep planning"
    "send feedback and keep planning": REJECT,
    "deny": REJECT,
    "reject": REJECT,
    "cancel": REJECT,

    # -- type something instead ------------------------------------------------
    "other": FREE_TEXT,
    "no, and tell claude what to do differently": FREE_TEXT,
    "no, and tell claude ": FREE_TEXT,
}

# A leading "1. " / "2) " / "> " is chrome the TUI draws, not part of the option.
_LEADING_CHROME = re.compile(r"^\s*(?:[>❯▶*\-]\s*)?(?:\(?\d{1,2}[.)\]]\s*)?")
_COLLAPSE_WS = re.compile(r"\s+")


def normalize(text):
    """Casefold, strip list chrome, collapse whitespace, unify quotes.

    OCR renders an apostrophe as U+2019 about as often as U+0027, and "don't ask
    again" is the string where that decides whether a permanent grant is recognised
    or silently becomes UNKNOWN. NFKC first, then fold the quote family by hand,
    because NFKC leaves U+2019 alone.
    """
    if not isinstance(text, str):
        return ""
    text = unicodedata.normalize("NFKC", text)
    text = text.replace("’", "'").replace("ʼ", "'").replace("`", "'")
    text = _LEADING_CHROME.sub("", text)
    text = _COLLAPSE_WS.sub(" ", text).strip()
    # A trailing ellipsis or colon is decoration; a trailing period is not stripped
    # because no table entry ends in one and doing so would widen the match surface.
    text = text.rstrip("…").rstrip()
    if text.endswith("..."):
        text = text[:-3].rstrip()
    return text.casefold()


# Where a 'Yes, allow ...' option writes its rule. Verbatim from the agent's own
# destination table in the webview bundle (2.1.278):
#   localSettings 'this project (just you)' | userSettings 'all projects'
#   projectSettings 'this project (shared)' | session 'this session'
#   cliArg 'startup options'
#
# The finished label is COMPOSED at runtime -- 'Yes, allow ' + rules + ' for ' + one of
# these -- so it appears nowhere as a literal to transcribe, which is how the table below
# came to class every one of them as approve-once via its 'yes, allow ' template. A real
# bash prompt then offered 'Yes' AND 'Yes, allow ... for all projects', both read as
# approve-once, and the module refused it as ambiguous.
ALL_PROJECTS_SUFFIX = ' for all projects'
RULE_DESTINATIONS = (
    ALL_PROJECTS_SUFFIX,
    ' for this project (just you)',
    ' for this project (shared)',
    ' for this session',
    ' for startup options',
)


def rule_grant_kind(observed):
    """The rule-writing class of an option, or None when it writes no rule."""
    if not observed.startswith('yes, '):
        return None
    for destination in RULE_DESTINATIONS:
        if observed.endswith(destination):
            return (APPROVE_ALL_PROJECTS if destination == ALL_PROJECTS_SUFFIX
                    else APPROVE_WIDER)
    return None


def classify(text):
    """Return (class, matched_table_entry). Longest match wins; default UNKNOWN."""
    observed = normalize(text)
    if not observed:
        return UNKNOWN, None

    # Destination first: every one of these also matches the 'yes, allow ' template, and
    # the template's answer is wrong for all of them.
    rule = rule_grant_kind(observed)
    if rule is not None:
        return rule, (ALL_PROJECTS_SUFFIX.strip()
                      if observed.endswith(ALL_PROJECTS_SUFFIX) else 'a saved rule')

    best_entry, best_len = None, -1
    for entry, kind in KNOWN.items():
        is_template = entry.endswith(" ")
        if is_template:
            # A rendered template is "<entry><runtime value>". Require something
            # after the prefix: a bare "yes, allow" with nothing appended did not
            # come from this template and must fall through to the exact entry.
            matched = observed.startswith(entry) and len(observed) > len(entry)
        else:
            matched = observed == entry
        if matched and len(entry) > best_len:
            best_entry, best_len = entry, len(entry)

    if best_entry is None:
        return UNKNOWN, None
    return KNOWN[best_entry], best_entry


def choose(options):
    """Pick the row to press, or refuse.

    Returns (index_or_None, reason). The reason is written for a log line that has
    to be able to say the run FAILED, so it names the deciding condition rather
    than reporting success by omission.
    """
    if not options:
        return None, "refused: no options were read off the prompt"

    classified = [(i, text, classify(text)) for i, text in enumerate(options)]

    unknown = [text for _, text, (kind, _) in classified if kind == UNKNOWN]
    if unknown:
        return None, ("refused: %d of %d options unrecognised (%s) -- either the "
                      "capture misread the prompt or the agent shipped a new "
                      "option; not pressing anything"
                      % (len(unknown), len(options), "; ".join(repr(u) for u in unknown[:3])))

    approvals = [(i, text) for i, text, (kind, _) in classified if kind == APPROVE_ONCE]
    if not approvals:
        kinds = sorted({kind for _, _, (kind, _) in classified})
        return None, ("refused: no approve-once option present (saw %s)"
                      % ", ".join(kinds))
    if len(approvals) > 1:
        return None, ("refused: %d approve-once options (%s) -- ambiguous, a prompt "
                      "should offer exactly one"
                      % (len(approvals), ", ".join(repr(t) for _, t in approvals)))

    index, text = approvals[0]
    return index, ("pressing option %d %r (approve-once); declined %d wider/mode "
                   "options" % (index + 1, text,
                                sum(1 for _, _, (k, _) in classified
                                    if k in (APPROVE_WIDER, MODE_CHANGE))))


# --------------------------------------------------------------------------
# Audit: re-derive the option set from whatever bundle is installed.
# --------------------------------------------------------------------------
BUNDLE_GLOB = os.path.join(
    os.path.expanduser("~"), ".vscode", "extensions",
    "anthropic.claude-code-*", "webview", "index.js")
# Widened on 2026-09-23, in TWO directions at once, because widening either alone is
# wrong and the first attempt proved it.
#
# The old pattern was `Yes...` / `No, ...` across the whole 5 MB bundle. 2.1.280 labels
# the prompt's buttons from ternaries whose branches include "Send feedback and keep
# planning" and "Submit answers" -- neither starts with either word, so the audit could
# not see them and reported OK while the table was missing two real labels and a third.
#
# Adding those verbs to a whole-bundle scan produced 174 hits: "Allow", "Continue" and
# friends are ordinary words that appear all over an editor UI. So the scan is now
# SCOPED to the prompt component -- a window around each use of the container's style
# class -- and only inside it is the verb set widened. Region plus narrow verbs yields
# exactly the ten real labels and nothing else; either half on its own does not.
#
# Widened again on 2026-11-06 (N-audit-1106-117): the literal's body was `[^"\\]{0,60}`, so a
# label longer than the opening verb plus 60 characters, or one carrying ANY backslash escape
# (an escaped quote, an apostrophe written as a code point), matched nothing and was simply not in the set, and the
# audit printed "audit OK" over it. The body now takes escapes and any length, and the escapes are
# decoded (_decode_js_literal) before the label is classified, because classify() compares what
# the user SEES. Unbounded is safe here because the scan is already scoped to the prompt
# component; the region, not the length cap, is what keeps ordinary editor prose out.
#
# Still a HEURISTIC, and every hole left is stated rather than papered over: a future label
# opening with a verb that is not here; a label written as a single-quoted or backtick literal
# (the bundle's minifier emits double quotes, but nothing guarantees it); and a label composed at
# runtime from pieces, like the rule-destination rows above, which exists nowhere as one literal.
# Each is invisible to this audit. That is precisely why the module refuses on an unrecognised
# option instead of guessing -- no audit of a minified bundle can promise to be complete.
#
# The verb is checked on the DECODED text, not in the regex (N-cxr-w1-06). The pattern used to
# require the verb itself straight after the opening quote, so a literal whose opening letter was
# written as an escape (\x59es, \u0059es) displays "Yes..." and was never extracted. The opening
# now admits a verb's first letter or a backslash, the literal is decoded, and _DISPLAYED_VERB
# decides. Opening on those characters rather than on ANY quote is deliberate: a match still has to
# start where a label could, so a stray quote inside a single-quoted or regex literal cannot shift
# every later quote in the region from opening to closing and hide the labels after it.
_OPTION_VERBS = ("Yes", "No", "Submit", "Send")
_BUNDLE_OPTION = re.compile(r'"((?:[' + "".join(sorted(set(v[0] for v in _OPTION_VERBS)))
                            + r']|\\)(?:[^"\\]|\\.)*)"')
_DISPLAYED_VERB = re.compile("(?:" + "|".join(_OPTION_VERBS) + ")")

_BACKSLASH = chr(92)   # built, not written: this file is edited by tools that eat escapes
_SIMPLE_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "b": "\b", "f": "\f", "v": "\v", "0": "\0"}


def _decode_js_literal(body):
    """The text a JS double-quoted literal's body stands for: escapes decoded, nothing else.

    Covers the escapes a minifier emits (simple ones, a 4-digit or braced code point, a
    2-digit hex byte, and an escaped character standing for itself). A malformed escape is
    kept as written rather than guessed at; it then fails to classify, which is the safe way
    to be wrong.
    """
    out, i, n = [], 0, len(body)
    while i < n:
        ch = body[i]
        if ch != _BACKSLASH or i + 1 >= n:
            out.append(ch)
            i += 1
            continue
        nxt = body[i + 1]
        if nxt == "u" and i + 2 < n and body[i + 2] == "{":
            close = body.find("}", i + 3)
            digits = body[i + 3:close] if close > 0 else ""
            if digits and all(c in "0123456789abcdefABCDEF" for c in digits):
                out.append(chr(int(digits, 16)))
                i = close + 1
                continue
        elif nxt in ("u", "x"):
            width = 4 if nxt == "u" else 2
            digits = body[i + 2:i + 2 + width]
            if len(digits) == width and all(c in "0123456789abcdefABCDEF" for c in digits):
                out.append(chr(int(digits, 16)))
                i += 2 + width
                continue
        elif nxt in _SIMPLE_ESCAPES:
            out.append(_SIMPLE_ESCAPES[nxt])
            i += 2
            continue
        else:
            out.append(nxt)
            i += 2
            continue
        out.append(ch)
        i += 1
    return "".join(out)

# `S5.permissionRequestContainer` -- the style class being USED. The CSS-module map
# nearby spells it `permissionRequestContainer:"permissionRequestContainer_qlaBag"`
# with no leading dot, and matching that instead would centre the window on a table of
# class names rather than on the component that renders the buttons.
_PROMPT_COMPONENT = re.compile(r'\.permissionRequestContainer\b')
_COMPONENT_WINDOW = 4000


def _component_regions(blob):
    """The text around each prompt-component render, or the whole blob if none matched.

    Falling back to the whole blob matters: if the class is ever renamed, a scoped scan
    would quietly search nothing and report OK. Wide and noisy beats narrow and blind.
    """
    spans = [(max(0, m.start() - _COMPONENT_WINDOW), m.end() + _COMPONENT_WINDOW)
             for m in _PROMPT_COMPONENT.finditer(blob)]
    if not spans:
        return [blob], False
    merged = []
    for start, end in sorted(spans):
        if merged and start <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])
    return [blob[start:end] for start, end in merged], True

# Recognised, and deliberately NOT in KNOWN. Printed on every audit run WITH the reason,
# because an ignore-list that hides its contents is how a decision quietly becomes an
# oversight.
DELIBERATELY_UNCLASSIFIED = {
    "Submit answers":
        "the approve row of a QUESTION prompt. Pressing it submits whatever answers are "
        "selected, which is answering for the user rather than approving a call they "
        "asked for. No OptionKind means 'recognised and never ours to press', so it is "
        "left Unknown and the whole prompt refuses -- the behaviour we want, reached "
        "through a refusal message that blames the capture. A NeverPress kind would fix "
        "the wording without changing a single press.",
}


# The exit code of an audit that compared nothing (N-audit-1106-008). Neither 0 (it did not pass)
# nor 1 (the table is not known to be wrong): a run that says DEGRADED and exits 3 is one a release
# step cannot read as green, and one a reader cannot mistake for a table gap.
DEGRADED_EXIT = 3

# Labels every Claude Code permission prompt has carried since the first bundle this table was
# transcribed from, and the two the table holds as exact entries. If the extractor's set lacks
# either, it did not read the prompt component -- a renamed class, a reshaped literal, a bundle
# that is not the bundle -- and "every label it found is classified" is a statement about nothing.
# The audit used to print "audit OK: all 0 bundle options classified" there and exit 0
# (N-audit-1106-008).
AUDIT_ANCHORS = ("Yes", "No")


def _label_via_other_template(text, entry):
    """True when a bundle literal classified only through a prefix TEMPLATE it is not (N-audit-1106-116,
    N-cxr-w1-05).

    A template ("yes, allow ") exists for the literal that ends in a space and has a runtime value
    appended. A literal that does NOT end in a space is not a rendering of it: it is a fixed label,
    shown to the user exactly as written, which the table has never seen. classify() still answers
    for it through the template -- that is correct for a read off the screen, where a rendered
    template is what arrives -- so a new "Yes, allow <anything>" label in the bundle classified
    approve-once and the audit printed "audit OK" over exactly the fail-open case rule 1 exists for.
    In the audit it needs its own exact entry, whatever class it turns out to be.

    THE TEMPLATE LITERAL ITSELF is the only one exempt, and it is matched as itself rather than by
    its trailing space (N-cxr-w1-05). The first version exempted every literal ending in a space, so
    a NEW longer template ("Yes, allow everything in " + a value) and a complete label padded with a
    space both rendered through the shorter "yes, allow " and passed as approve-once. A literal is
    exempt only when it ends in a space and, normalised, IS the entry it matched: normalize() strips
    the trailing space, so the space is put back before comparing.
    """
    if entry is None or not entry.endswith(" "):
        return False
    return not (text.endswith(" ") and normalize(text) + " " == entry)


def _no_bundle(bundle_glob):
    """No bundle to read. Not installed is an environment fact, not a table gap -- but it is not a
    pass either. This returned 0, so a release step run on a machine without the extension went
    green having compared nothing (N-audit-1106-008)."""
    print("audit: no Claude Code bundle found under %s" % bundle_glob)
    print("\naudit DEGRADED: nothing was compared. Run it where the extension is installed.")
    return DEGRADED_EXIT


def audit(bundle_glob=BUNDLE_GLOB):
    """Report bundle strings the table cannot classify. Exit nonzero if any, or if nothing was compared."""
    paths = sorted(glob.glob(bundle_glob))
    if not paths:
        return _no_bundle(bundle_glob)

    newest = max(paths, key=os.path.getmtime)
    try:
        with open(newest, "r", encoding="utf-8", errors="replace") as handle:
            blob = handle.read()
    except OSError as exc:
        print("audit: cannot read %s: %s" % (newest, exc))
        return 1
    return audit_text(blob, os.path.basename(os.path.dirname(os.path.dirname(newest))))


def audit_text(blob, name):
    """The audit over one bundle's text, split out so the self-test can hand it a synthetic one."""
    regions, scoped = _component_regions(blob)
    decoded = (_decode_js_literal(text) for region in regions for text in _BUNDLE_OPTION.findall(region))
    found = sorted(set(text for text in decoded if _DISPLAYED_VERB.match(text)))
    print("audit: %s" % name)
    print("scope: %d prompt-component region(s)%s"
          % (len(regions), "" if scoped else "  -- FELL BACK TO THE WHOLE BUNDLE, the "
                                             "container class was not found; expect noise"))
    for text, reason in sorted(DELIBERATELY_UNCLASSIFIED.items()):
        where = "present" if text in found else "NOT PRESENT"
        print("  -- %r deliberately unclassified (%s in this bundle):" % (text, where))
        print("       %s" % reason)
    unclassified = []
    for text in found:
        # A bundle string ending in a space is a TEMPLATE: the code appends a
        # runtime value before the user sees it. Classifying the bare template is
        # comparing the wrong thing -- it is never what appears on screen -- so
        # render it with a stand-in first. Without this the audit reports its own
        # inputs as unclassifiable and reads like a table gap that isn't there.
        if text in DELIBERATELY_UNCLASSIFIED:
            continue
        probe = text + "<value>" if text.endswith(" ") else text
        kind, entry = classify(probe)
        note = " (as template)" if probe is not text else ""
        unseen = kind == UNKNOWN or _label_via_other_template(text, entry)
        if kind != UNKNOWN and unseen:
            note = " (a label that is not the template %r matched only through it: it needs its own entry)" % entry
        flag = "!!" if unseen else "  "
        print("  %s %-46r -> %s%s" % (flag, text, kind, note))
        if unseen:
            unclassified.append(text)

    missing = [anchor for anchor in AUDIT_ANCHORS if anchor not in found]
    if missing:
        print("\naudit DEGRADED: the extracted set lacks %s, which every Claude Code prompt carries, so "
              "the extractor did not read the prompt component and nothing it says can be trusted "
              "(%d option string(s) extracted)." % (" and ".join(repr(m) for m in missing), len(found)))
        return DEGRADED_EXIT
    if unclassified:
        print("\naudit FAILED: %d bundle option(s) the table cannot classify."
              % len(unclassified))
        print("Until they are classified, choose() refuses every prompt containing "
              "them. That is the intended failure mode, not a bug.")
        return 1
    skipped = len([t for t in found if t in DELIBERATELY_UNCLASSIFIED])
    print("\naudit OK: all %d bundle options classified (%d deliberately not)."
          % (len(found) - skipped, skipped))
    return 0


# --------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------
def _check(results, label, condition):
    results.append((label, bool(condition)))
    return bool(condition)


def selftest(verbose=True):
    r = []

    # The single most important assertion in the file: "Yes" must not swallow the
    # wider grants by prefix. This is the bug the whole matching rule exists for.
    _check(r, "WITNESS exact: 'Yes' is approve-once",
           classify("Yes")[0] == APPROVE_ONCE)
    _check(r, "WITNESS prefix-guard: \"Yes, and don't ask again\" is NOT approve-once",
           classify("Yes, and don't ask again")[0] == APPROVE_WIDER)
    _check(r, "prefix-guard: 'Yes, allow all edits this session' is not approve-once",
           classify("Yes, allow all edits this session")[0] == APPROVE_WIDER)
    _check(r, "prefix-guard: 'Yes, set auto mode as my default' is a mode change",
           classify("Yes, set auto mode as my default")[0] == MODE_CHANGE)

    # Templates: a rendered runtime suffix must still classify.
    _check(r, "template: 'Yes, allow access to github.com' is approve-once",
           classify("Yes, allow access to github.com")[0] == APPROVE_ONCE)
    _check(r, "template: longest match wins over the shorter template",
           classify("Yes, allow access to github.com")[1] == "yes, allow access to ")
    # The bundle ships "Yes, allow " ONLY as a template, so a bare "Yes, allow"
    # with nothing appended is not an option the agent can render. The realistic
    # way to see one is OCR truncating "Yes, allow access to <host>" at the window
    # edge -- i.e. we did not read the row we would be pressing. UNKNOWN is the
    # right answer, and this assertion originally demanded APPROVE_ONCE, which
    # would have pressed a half-read option. Kept as a test because the tempting
    # "fix" is to add a bare exact entry.
    _check(r, "WITNESS truncation: a bare 'Yes, allow' is UNKNOWN, not approve-once",
           classify("Yes, allow")[0] == UNKNOWN)

    # Normalisation.
    _check(r, "normalise: TUI list chrome '1. Yes' is stripped",
           classify("1. Yes")[0] == APPROVE_ONCE)
    _check(r, "normalise: '> 2) No' is stripped",
           classify("> 2) No")[0] == REJECT)
    _check(r, "normalise: a curly apostrophe still reads as a wider grant",
           classify("Yes, and don’t ask again")[0] == APPROVE_WIDER)
    _check(r, "normalise: trailing ellipsis is decoration",
           classify("Yes…")[0] == APPROVE_ONCE)

    # Unknown handling.
    _check(r, "unknown: an unseen option classifies as unknown",
           classify("Yes, and disable all future prompts")[0] == UNKNOWN)
    _check(r, "unknown: empty text is unknown, not a crash",
           classify("")[0] == UNKNOWN)
    _check(r, "unknown: a non-string is unknown, not a crash",
           classify(None)[0] == UNKNOWN)

    # choose(): the decision surface.
    idx, why = choose(["Yes", "Yes, and don't ask again", "No"])
    _check(r, "WITNESS choose: picks the approve-once row from a real 3-option prompt",
           idx == 0 and "approve-once" in why)

    idx, why = choose(["Yes, allow access to example.com",
                       "Yes, and don't ask again", "No, keep planning"])
    _check(r, "choose: picks a rendered template row", idx == 0)

    idx, why = choose(["Yes", "Yes, and disable all future prompts", "No"])
    _check(r, "WITNESS choose: ONE unknown option refuses the WHOLE prompt",
           idx is None and "unrecognised" in why)

    idx, why = choose(["Yes, and don't ask again", "No"])
    _check(r, "choose: refuses when no approve-once option exists",
           idx is None and "no approve-once" in why)

    idx, why = choose(["Yes", "Yes", "No"])
    _check(r, "choose: refuses two approve-once options as ambiguous",
           idx is None and "ambiguous" in why)

    idx, why = choose([])
    _check(r, "choose: refuses an empty option list", idx is None)

    idx, why = choose(["Yes", "Other", "No"])
    _check(r, "choose: a free-text 'Other' row does not block a valid approve-once",
           idx == 0)

    # -- the permission MODE is never ours to change -------------------------
    # Pressing a mode change is categorically different from approving a call:
    # it rewrites the rule that governs every FUTURE call, and one of the four
    # ("set auto mode as my default") persists past the session. That it is
    # unreachable today falls out of choose() filtering on APPROVE_ONCE, which is
    # an implicit consequence rather than a stated rule -- so state it, and let
    # --mutate prove the statement can fail.
    idx, why = choose(["Yes, set auto mode as my default", "No"])
    _check(r, "WITNESS mode: a prompt offering ONLY a mode change is refused",
           idx is None and "no approve-once" in why)

    idx, why = choose(["Yes, and auto-accept", "Yes, and manually approve edits",
                       "No, keep planning"])
    _check(r, "mode: a prompt of nothing but mode changes is refused",
           idx is None and "no approve-once" in why)

    # Structural, over every option set the suite knows: whatever comes back must
    # be an approve-once row. One example passing proves one example.
    mode_safe = True
    for opts in (["Yes", "Yes, and don't ask again", "No"],
                 ["Yes, allow access to example.com", "Yes, and auto-accept",
                  "No, keep planning"],
                 ["Yes", "Yes, set auto mode as my default",
                  "Yes, allow all edits this session", "Deny"],
                 ["Yes, and manually approve edits", "Yes", "Cancel"]):
        picked, _ = choose(opts)
        if picked is not None and classify(opts[picked])[0] != APPROVE_ONCE:
            mode_safe = False
    _check(r, "WITNESS mode: choose() never returns a mode-change or wider row",
           mode_safe)

    # Fail-closed property: DROPPING a wider-grant entry from the table must make
    # the prompt refuse, never make it press the wrong row.
    saved = KNOWN.pop("yes, and don't ask again")
    try:
        idx, why = choose(["Yes", "Yes, and don't ask again", "No"])
        _check(r, "WITNESS fail-closed: losing a table entry refuses, never mispresses",
               idx is None and "unrecognised" in why)
    finally:
        KNOWN["yes, and don't ask again"] = saved

    # -- the audit, over synthetic bundles (2026-11-06) -------------------------
    # Every one of these passed as "audit OK" before its fix; none reads a real bundle.
    def run_audit(*literals):
        blob = ("var x=S5.permissionRequestContainer;f(["
                + ",".join('"%s"' % literal for literal in literals) + "]);")
        sink = io.StringIO()
        with contextlib.redirect_stdout(sink):
            code = audit_text(blob, "synthetic")
        return code, sink.getvalue()

    code, said = run_audit("Yes", "No", "Yes, allow ", "Yes, and don't ask again")
    _check(r, "WITNESS audit: a bundle whose labels are all in the table is OK",
           code == 0 and "audit OK" in said)
    code, said = run_audit("Submit answers")
    _check(r, "audit (N-audit-1106-008): a bundle with no extractable Yes/No labels is DEGRADED, not OK",
           code == DEGRADED_EXIT and "audit DEGRADED" in said and "audit OK" not in said)
    sink = io.StringIO()
    with contextlib.redirect_stdout(sink):
        code = audit(bundle_glob=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                              "no-such-bundle-*", "index.js"))
    _check(r, "audit (N-audit-1106-008): no bundle at all is DEGRADED, not a pass",
           code == DEGRADED_EXIT and "audit DEGRADED" in sink.getvalue())
    code, said = run_audit("Yes", "No", "Yes, allow everything from now on")
    _check(r, "audit (N-audit-1106-116): a new complete 'Yes, allow ...' label is flagged, not passed as approve-once",
           code == 1 and "needs its own entry" in said)
    code, said = run_audit("Yes", "No", "Yes, and " + "keep going " * 8 + "forever")
    _check(r, "audit (N-audit-1106-117): a label longer than 63 characters is extracted and flagged",
           code == 1 and "audit FAILED" in said)
    code, said = run_audit("Yes", "No", "Yes, and say " + _BACKSLASH + '"always' + _BACKSLASH + '"')
    _check(r, "audit (N-audit-1106-117): a label carrying an escape is extracted and flagged",
           code == 1 and "audit FAILED" in said)
    code, said = run_audit("Yes", "No", "Yes, and don" + _BACKSLASH + "u0027t ask again")
    _check(r, "WITNESS audit: an escaped label is decoded before it is classified, so a known one passes",
           code == 0 and "approve-wider" in said)
    # N-cxr-w1-05: only the template literal itself is exempt. A NEW longer template and a complete
    # label padded with a space both render through the shorter "yes, allow " and used to pass.
    code, said = run_audit("Yes", "No", "Yes, allow everything in ")
    _check(r, "audit (N-cxr-w1-05): an unknown longer template fragment is flagged, not passed through 'yes, allow '",
           code == 1 and "needs its own entry" in said)
    code, said = run_audit("Yes", "No", "Yes, allow all edits this session ")
    _check(r, "audit (N-cxr-w1-05): a complete label padded with a space is flagged, not passed as approve-once",
           code == 1 and "needs its own entry" in said)
    code, said = run_audit("Yes", "No", "Yes, allow ", "Yes, allow access to ", "No, keep ")
    _check(r, "WITNESS audit (N-cxr-w1-05): the table's own template literals still pass as templates",
           code == 0 and "audit OK" in said and said.count("(as template)") == 3)
    # N-cxr-w1-06: the verb is judged on the decoded text, so an escaped OPENING letter is extracted.
    code, said = run_audit("Yes", "No", _BACKSLASH + "x59es, and keep going forever")
    _check(r, "audit (N-cxr-w1-06): a label whose opening letter is escaped is extracted and flagged",
           code == 1 and "audit FAILED" in said and "Yes, and keep going forever" in said)
    code, said = run_audit("Yes", "No", _BACKSLASH + "u0059es, and don't ask again",
                           "N" + _BACKSLASH + "x6f, never mind")
    _check(r, "WITNESS audit (N-cxr-w1-06): escaped opening letters are decoded, so a known label passes and "
              "an unknown one is still caught",
           code == 1 and "approve-wider" in said and "No, never mind" in said)
    code, said = run_audit("Yes", "No", "Shown below", _BACKSLASH + "x41llow everything")
    _check(r, "WITNESS audit (N-cxr-w1-06): a literal that does not DISPLAY a verb is not extracted",
           code == 0 and "Shown below" not in said and "Allow everything" not in said)

    passed = sum(1 for _, ok in r if ok)
    if verbose:
        for label, ok in r:
            print("  %s %s" % ("PASS" if ok else "FAIL", label))
        print("\n%d/%d passed" % (passed, len(r)))
    return passed, len(r), r


# --------------------------------------------------------------------------
# Mutation mode: prove the self-test can actually fail.
# A suite that has never been seen to fail is not evidence of anything.
# --------------------------------------------------------------------------
def mutate():
    import copy
    mutations = []

    # BASELINE FIRST. FIRED used to mean "some failing label contains the expected substring after
    # the mutation", with nothing checking that the label PASSED before it. Against a red suite that
    # credited mutations to labels that were already failing: with one table entry removed the
    # suite was 23/25 and --mutate still printed 5/5 (F008). A mutation result means nothing until
    # the suite is green and the mutation is what flipped the label, so a red baseline refuses to
    # score, and a hit on a label that was already red is BLIND rather than FIRED.
    passed_at_baseline, total_at_baseline, baseline = selftest(verbose=False)
    baseline_passing = set(label for label, ok in baseline if ok)
    if passed_at_baseline != total_at_baseline:
        print("BASELINE NOT GREEN (%d/%d): refusing to score mutations against a red suite."
              % (passed_at_baseline, total_at_baseline))
        for label, ok in baseline:
            if not ok:
                print("  FAIL %s" % label)
        return 2
    print("baseline: %d/%d passing\n" % (passed_at_baseline, total_at_baseline))

    def run(name, apply_fn, restore_fn, expect_label_contains):
        apply_fn()
        try:
            _, _, results = selftest(verbose=False)
        finally:
            restore_fn()
        failed = [label for label, ok in results if not ok]
        # Only a label this mutation FLIPPED counts: it was passing at baseline and fails now.
        hit = [f for f in failed
               if expect_label_contains.lower() in f.lower() and f in baseline_passing]
        mutations.append((name, len(failed), hit))
        print("  %-42s failures=%-2d  %s"
              % (name, len(failed),
                 "FIRED (%s)" % hit[0][:54] if hit else "SURVIVED -- test is blind"))

    print("mutation run (each breaks the classifier, then restores it)\n")
    original = copy.deepcopy(KNOWN)
    global classify
    real_classify = classify

    # 1. Prefix matching instead of exact -- the dangerous implementation.
    def prefix_classify(text):
        observed = normalize(text)
        for entry, kind in KNOWN.items():
            if observed.startswith(entry.rstrip()):
                return kind, entry
        return UNKNOWN, None
    run("exact match -> naive prefix match",
        lambda: globals().__setitem__("classify", prefix_classify),
        lambda: globals().__setitem__("classify", real_classify),
        "prefix-guard")

    # 2. Treat unknown options as skippable rather than poisoning the prompt.
    real_choose = choose

    def lax_choose(options):
        if not options:
            return None, "refused: no options"
        approvals = [(i, t) for i, t in enumerate(options)
                     if real_classify(t)[0] == APPROVE_ONCE]
        if len(approvals) != 1:
            return None, "refused: not exactly one approve-once"
        return approvals[0][0], "pressing (unknown options ignored)"
    run("unknown option poisons prompt -> ignored",
        lambda: globals().__setitem__("choose", lax_choose),
        lambda: globals().__setitem__("choose", real_choose),
        "unknown option refuses")

    # 3. Misclassify a permanent grant as approve-once.
    run("'don't ask again' reclassified approve-once",
        lambda: KNOWN.__setitem__("yes, and don't ask again", APPROVE_ONCE),
        lambda: KNOWN.update(original),
        "prefix-guard")

    # 4. Let choose() treat a mode change as pressable. This is the mutation that
    #    matters most: it is also the most plausible "helpful" patch someone adds
    #    later to stop AgentFlow refusing prompts it could technically answer.
    def mode_ok_choose(options):
        if not options:
            return None, "refused: no options"
        for i, text in enumerate(options):
            if real_classify(text)[0] in (APPROVE_ONCE, MODE_CHANGE):
                return i, "pressing %r" % text
        return None, "refused: nothing pressable"
    run("choose() permitted to press a mode change",
        lambda: globals().__setitem__("choose", mode_ok_choose),
        lambda: globals().__setitem__("choose", real_choose),
        "mode")

    # 5. Drop apostrophe folding, so OCR's curly quote stops matching.
    real_normalize = normalize
    run("curly-apostrophe folding removed",
        lambda: globals().__setitem__(
            "normalize", lambda t: real_normalize(t).replace("'", "’")),
        lambda: globals().__setitem__("normalize", real_normalize),
        "apostrophe")

    # 6-10. The audit's own guards (2026-11-06): each mutation puts back the shape that printed
    #       "audit OK" over a bundle it had not compared.
    def swap(name, value):
        saved = globals()[name]
        return (lambda: globals().__setitem__(name, value)), (lambda: globals().__setitem__(name, saved))

    def no_bundle_passes(bundle_glob):
        print("audit: no Claude Code bundle found under %s" % bundle_glob)
        return 0
    apply_fn, restore_fn = swap("_no_bundle", no_bundle_passes)
    run("no bundle -> audit passes", apply_fn, restore_fn, "no bundle at all is DEGRADED")
    apply_fn, restore_fn = swap("AUDIT_ANCHORS", ())
    run("anchor labels not required", apply_fn, restore_fn, "no extractable Yes/No labels")
    apply_fn, restore_fn = swap("_label_via_other_template", lambda text, entry: False)
    run("complete label may match a template", apply_fn, restore_fn, "N-audit-1106-116")
    # The first version of that guard, which exempted every literal ending in a space (N-cxr-w1-05).
    apply_fn, restore_fn = swap("_label_via_other_template",
                                lambda text, entry: (not text.endswith(" ")) and entry is not None
                                and entry.endswith(" "))
    run("every space-ended literal exempt again", apply_fn, restore_fn, "N-cxr-w1-05")
    # The extractor as N-audit-1106-117 left it: the verb required in the regex, before decoding.
    apply_fn, restore_fn = swap("_BUNDLE_OPTION",
                                re.compile('"((?:Yes|No|Submit|Send)(?:[^"' + _BACKSLASH * 2 + ']|'
                                           + _BACKSLASH * 2 + '.)*)"'))
    run("opening verb required before decoding", apply_fn, restore_fn, "opening letter is escaped")
    apply_fn, restore_fn = swap("_BUNDLE_OPTION",
                                re.compile('"((?:Yes|No|Submit|Send)[^"' + _BACKSLASH * 2 + ']{0,60})"'))
    run("extractor back to 60 chars, no escapes", apply_fn, restore_fn, "longer than 63 characters")
    apply_fn, restore_fn = swap("_decode_js_literal", lambda body: body)
    run("escapes not decoded", apply_fn, restore_fn, "escaped label is decoded")

    KNOWN.clear()
    KNOWN.update(original)
    blind = [name for name, _, hit in mutations if not hit]
    print("\n%d/%d mutations FIRED." % (len(mutations) - len(blind), len(mutations)))
    if blind:
        print("BLIND to: %s" % ", ".join(blind))
        return 1
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--audit", action="store_true")
    parser.add_argument("--mutate", action="store_true")
    parser.add_argument("--classify", metavar="TEXT")
    args = parser.parse_args()

    if args.classify is not None:
        kind, entry = classify(args.classify)
        print("%s -> %s (matched %r)" % (args.classify, kind, entry))
        return 0
    if args.audit:
        return audit()
    if args.mutate:
        return mutate()

    passed, total, _ = selftest()
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
