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
import glob
import os
import re
import sys
import unicodedata

# --------------------------------------------------------------------------
# Classes, in descending order of what they cost you if pressed by mistake.
# --------------------------------------------------------------------------
APPROVE_ONCE = "approve-once"      # the only class we will ever press
APPROVE_WIDER = "approve-wider"    # session-wide or permanent grant
MODE_CHANGE = "mode-change"        # alters the permission mode, sometimes persistently
REJECT = "reject"                  # declines the call
FREE_TEXT = "free-text"            # "other" / tell the agent something instead
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
    "yes, allow access to": APPROVE_ONCE,
    "allow": APPROVE_ONCE,

    # -- wider than one call --------------------------------------------------
    "yes, allow all edits this session": APPROVE_WIDER,
    "yes, and don't ask again": APPROVE_WIDER,

    # -- changes the permission mode -----------------------------------------
    "yes, and auto-accept": MODE_CHANGE,
    "yes, and manually approve edits": MODE_CHANGE,
    "yes, return to normal mode": MODE_CHANGE,
    "yes, set auto mode as my default": MODE_CHANGE,

    # -- decline ---------------------------------------------------------------
    "no": REJECT,
    "no, keep ": REJECT,
    "no, keep planning": REJECT,
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


def classify(text):
    """Return (class, matched_table_entry). Longest match wins; default UNKNOWN."""
    observed = normalize(text)
    if not observed:
        return UNKNOWN, None

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
_BUNDLE_OPTION = re.compile(r'"(Yes[^"\\]{0,60}|No, [^"\\]{0,60})"')


def audit(bundle_glob=BUNDLE_GLOB):
    """Report bundle strings the table cannot classify. Exit nonzero if any."""
    paths = sorted(glob.glob(bundle_glob))
    if not paths:
        print("audit: no Claude Code bundle found under %s" % bundle_glob)
        return 0  # not installed is not a failure

    newest = max(paths, key=os.path.getmtime)
    try:
        with open(newest, "r", encoding="utf-8", errors="replace") as handle:
            blob = handle.read()
    except OSError as exc:
        print("audit: cannot read %s: %s" % (newest, exc))
        return 1

    found = sorted(set(_BUNDLE_OPTION.findall(blob)))
    print("audit: %s" % os.path.basename(os.path.dirname(os.path.dirname(newest))))
    unclassified = []
    for text in found:
        # A bundle string ending in a space is a TEMPLATE: the code appends a
        # runtime value before the user sees it. Classifying the bare template is
        # comparing the wrong thing -- it is never what appears on screen -- so
        # render it with a stand-in first. Without this the audit reports its own
        # inputs as unclassifiable and reads like a table gap that isn't there.
        probe = text + "<value>" if text.endswith(" ") else text
        kind, entry = classify(probe)
        note = " (as template)" if probe is not text else ""
        flag = "  " if kind != UNKNOWN else "!!"
        print("  %s %-46r -> %s%s" % (flag, text, kind, note))
        if kind == UNKNOWN:
            unclassified.append(text)

    if unclassified:
        print("\naudit FAILED: %d bundle option(s) the table cannot classify."
              % len(unclassified))
        print("Until they are classified, choose() refuses every prompt containing "
              "them. That is the intended failure mode, not a bug.")
        return 1
    print("\naudit OK: all %d bundle options classified." % len(found))
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

    def run(name, apply_fn, restore_fn, expect_label_contains):
        apply_fn()
        try:
            _, _, results = selftest(verbose=False)
        finally:
            restore_fn()
        failed = [label for label, ok in results if not ok]
        hit = [f for f in failed if expect_label_contains.lower() in f.lower()]
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
