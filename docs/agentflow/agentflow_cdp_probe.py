#!/usr/bin/env python3
"""Replay AgentFlow's CDP read against a live VS Code, and say what it sees.

The shipped module reads a prompt with two expressions and nothing else: a Claude
one anchored on the `permissionRequestContainer` CSS-module prefix, and a Codex one
anchored on the card's Tailwind container name `@container/approval-card`. When a
prompt is on screen and AgentFlow does not press it, the question is always which of
those two returned 'none'.

Since 1.4.2 the module can say 'blind' -- a card was found and no options came out of
it -- so that case is no longer silent in its own log. 'none' still cannot be told
from an idle editor, and never can be: it is the answer BOTH give.

This prints, per webview target: the outcome of each expression verbatim, plus a
RAW dump of every button the target holds. The raw dump is what tells a top-level
selector miss (buttons exist, expression says 'none') from an empty panel -- the one
shape the module still cannot self-report.

    python agentflow_cdp_probe.py [--port 9321] [--raw] [--time N]
    python agentflow_cdp_probe.py --selftest   # the drift guard, against the module source only

Prints prompt option labels, which is the whole point, so its output is NOT safe to
paste into a public issue unedited -- unlike the module's own log, which is.
"""

import argparse
import json
import os
import re
import sys
import urllib.request

try:
    import websocket  # websocket-client
except ImportError:  # only the live reads need it; --selftest runs without it (N-cxr-w1-04)
    websocket = None

# --- verbatim from modules/AgentFlow/CdpApprover.cs -------------------------------

DOCUMENT_PRELUDE = r"""
  function appDocument() {
    var docs = [document];
    var frames = document.querySelectorAll('iframe');
    for (var fi = 0; fi < frames.length; fi++) {
      try { if (frames[fi].contentDocument) docs.push(frames[fi].contentDocument); } catch (e) { }
    }
    var best = null, bestCount = -1;
    for (var di = 0; di < docs.length; di++) {
      var n = docs[di].querySelectorAll('*').length;
      if (n > bestCount) { bestCount = n; best = docs[di]; }
    }
    return { doc: best, elements: bestCount };
  }
  var MinElements = 30;
  function cardText(root, dropSelector) {
    var clone = root.cloneNode(true);
    var drop = clone.querySelectorAll(dropSelector);
    for (var di = 0; di < drop.length; di++) drop[di].parentNode.removeChild(drop[di]);
    return (clone.textContent || '').replace(/\s+/g, ' ').trim();
  }
  function fingerprint(s) {
    var h = 0x811c9dc5;
    for (var i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619) >>> 0;
    return ('0000000' + h.toString(16)).slice(-8);
  }
"""

CLAUDE_READ = "(function () {" + DOCUMENT_PRELUDE + r"""
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var c = a.doc.querySelector('[class*="permissionRequestContainer"]');
  if (!c) return 'none';
  var bc = c.querySelector('[class*="buttonContainer"]');
  if (!bc) return 'blind';
  var out = { tool: '', header: '', ext: '', fp: '', card: '', fresh: false, options: [], disabled: [] };
  try { out.fp = fingerprint(cardText(c, '[class*="buttonContainer"]')); } catch (e) { out.fp = ''; }
  try {
    var ids = window.__agentflowCards || (window.__agentflowCards = new WeakMap());
    var cid = ids.get(c);
    if (!cid) {
      cid = ('0000000' + Math.floor(Math.random() * 4294967296).toString(16)).slice(-8);
      ids.set(c, cid);
      out.fresh = true;
    }
    out.card = cid;
  } catch (e) { out.card = ''; out.fresh = false; }
  var hd = c.querySelector('[class*="permissionRequestHeader"]');
  if (hd) {
    var st = hd.querySelector('strong');
    if (st) out.tool = (st.textContent || '').trim();
    var clone = hd.cloneNode(true);
    var ps = clone.querySelectorAll('[class*="permissionPath"]');
    for (var pi = 0; pi < ps.length; pi++) ps[pi].parentNode.removeChild(ps[pi]);
    out.header = (clone.textContent || '').replace(/\s+/g, ' ').trim();
    var p = hd.querySelector('[class*="permissionPath"]');
    if (p) {
      var m = (p.textContent || '').trim().match(/\.([A-Za-z0-9]{1,8})$/);
      if (m) out.ext = m[1].toLowerCase();
    }
  }
  var btns = bc.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) {
    var b = btns[i];
    var t = b.textContent || '';
    var n = b.querySelector('[class*="shortcutNum"]');
    if (n) t = t.substring((n.textContent || '').length);
    out.options.push(t.trim());
    out.disabled.push(!!b.disabled);
  }
  if (out.options.length === 0) return 'blind';
  return JSON.stringify(out);
})()"""

CODEX_READ = "(function () {" + DOCUMENT_PRELUDE + r"""
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var cards = a.doc.querySelectorAll('[class*="@container/approval-card"]');
  if (!cards.length) return 'none';
  // Every card since 1.12.0: one Codex panel can hold several (measured 2026-10-21 with
  // agentflow_codex_card_count.py), and the module reads them all.
  var all = [], blind = 0;
  for (var ci = 0; ci < cards.length; ci++) {
    var card = cards[ci];
    var form = card.querySelector('form');
    if (!form) { blind++; continue; }
    var trigger = form.querySelector('button[aria-label="Approval options"]');
    var out = { tool: '', header: '', ext: '', fp: '', card: '', form: '', fresh: false, options: [], disabled: [] };
    try { out.fp = fingerprint(cardText(card, 'form')); } catch (e) { out.fp = ''; }
    // N-agentflow-card-identity-01: the Claude reader's element mint, tried on the Codex card. Both the
    // card container and its form are minted, because which of the two Codex remounts per request was the
    // question; the module's Codex reader mints the card only, since 1.11.0.
    try {
      var ids = window.__agentflowCards || (window.__agentflowCards = new WeakMap());
      var mint = function (el) {
        var v = ids.get(el);
        if (!v) {
          v = ('0000000' + Math.floor(Math.random() * 4294967296).toString(16)).slice(-8);
          ids.set(el, v);
          out.fresh = true;
        }
        return v;
      };
      out.card = mint(card);
      out.form = mint(form);
    } catch (e) { out.card = ''; out.form = ''; out.fresh = false; }
    var btns = form.querySelectorAll('button');
    for (var i = 0; i < btns.length; i++) {
      var b = btns[i];
      if (b === trigger) continue;
      out.options.push((b.innerText || b.textContent || '').replace(/\s+/g, ' ').trim());
      out.disabled.push(!!b.disabled);
    }
    if (out.options.length === 0) { blind++; continue; }
    all.push(out);
  }
  if (all.length === 0) return 'blind';
  return JSON.stringify({ cards: all, blind: blind });
})()"""

# The pre-1.4.2 Codex anchor, kept so the probe can show the before and after side by
# side on the SAME live prompt. On a card with no scoped grant to offer this answers
# 'none' and the one above answers with the options -- which is BUG-006 in one line.
CODEX_READ_PRE_142 = "(function () {" + DOCUMENT_PRELUDE + r"""
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var trigger = a.doc.querySelector('button[aria-label="Approval options"]');
  if (!trigger) return 'none';
  var card = trigger.closest ? trigger.closest('form') : null;
  if (!card) card = trigger.parentElement ? trigger.parentElement.parentElement : null;
  if (!card) return 'none';
  var out = { options: [] };
  var btns = card.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) {
    if (btns[i] === trigger) continue;
    out.options.push((btns[i].innerText || btns[i].textContent || '').replace(/\s+/g, ' ').trim());
  }
  if (out.options.length === 0) return 'none';
  return JSON.stringify(out);
})()"""

# The expressions above are COPIES of the ones in modules/AgentFlow/CdpApprover.cs, and a
# copy that has drifted tests nothing. These are the substrings each read turns on; if the
# module stops containing one, the probe says so instead of quietly reporting on a stale
# version of the code it claims to be replaying.
#
# SCOPED TO THE READ EXPRESSION EACH COPY MIRRORS, not searched for anywhere in the file
# (N-cxr-w1-04). Most of these selectors are spelled twice in the module, once in a read and
# once in the click that follows it (`shortcutNum`, `permissionRequestContainer`,
# `buttonContainer`, the Codex card and its form), so a whole-file search stayed satisfied by
# the CLICK's copy after a change to the READ alone, and the probe went on replaying the old
# read with no DRIFT line. Each anchor now names the C# constant it must be found in.
SOURCE = "../../modules/AgentFlow/CdpApprover.cs"
CLAUDE_READ_CONST = "ReadExpression"
CODEX_READ_CONST = "CodexReadExpression"
LOAD_BEARING = [
    (CODEX_READ_CONST, '[class*=""@container/approval-card""]'),
    (CODEX_READ_CONST, "card.querySelector('form')"),
    (CODEX_READ_CONST, 'button[aria-label=""Approval options""]'),
    (CODEX_READ_CONST, "b === trigger"),
    # Added after these copies went stale against 1.4.3 and the guard stayed quiet,
    # because every substring it knew about was still there. A drift guard is only as
    # good as the list, so anything the copies are supposed to mirror belongs in it.
    (CLAUDE_READ_CONST, "return 'blind'"),
    (CODEX_READ_CONST, "return 'blind'"),
    # The card fingerprint both readers emit since the 2026-09-30 burn-down (RA-022/RA-023/RA-049):
    # a hash of the card's text with the buttons dropped, never the text. The probe prints it beside
    # the options so a live prompt shows whether two ticks saw one card or two.
    (CLAUDE_READ_CONST, "out.fp = fingerprint(cardText("),
    (CODEX_READ_CONST, "out.fp = fingerprint(cardText("),
    # The card ELEMENT's id the Claude reader mints since 1.9.1 (BUG-017), printed in the same JSON. The
    # probe shares the module's map on the page, so a card the probe reads first is no longer a first
    # sight to the module: a settling card it saw is announced one sweep sooner, and that is all.
    (CLAUDE_READ_CONST, "ids.set(c, cid);"),
    # The Codex reader's mint, since 1.11.0 (N-agentflow-card-identity-01). The probe mints the card's FORM as well,
    # which the module does not: the live reads of 2026-10-21 that settled the item watched both.
    (CODEX_READ_CONST, "cardIds.set(card, kid);"),
    # Every Codex card, since 1.12.0: the copy above walks them all and answers the shape the module does.
    (CODEX_READ_CONST, "for (var ci = 0; ci < cards.length; ci++) {"),
    (CODEX_READ_CONST, "return JSON.stringify({ cards: all, blind: blind });"),
    # The CLAUDE read's own anchors (N-audit-1106-115). Every entry above is the Codex read's or shared, so a change
    # to the module's Claude read left this probe replaying a stale copy of it with no DRIFT line: the failure the
    # comment above says already happened once. Spelled as the module's C# verbatim string spells them (doubled
    # quotes), which is what the source text holds.
    (CLAUDE_READ_CONST, '[class*=""permissionRequestContainer""]'),
    (CLAUDE_READ_CONST, '[class*=""buttonContainer""]'),
    (CLAUDE_READ_CONST, '[class*=""permissionRequestHeader""]'),
    (CLAUDE_READ_CONST, '[class*=""permissionPath""]'),
    (CLAUDE_READ_CONST, '[class*=""shortcutNum""]'),
]

_BACKSLASH = chr(92)


def declaration_span(text, name):
    """(start, end) of the initializer of `const string <name> = ...;` in C# source, or None.

    Walks string literals rather than searching for the next `;`, because the read
    expressions are JavaScript and full of semicolons. A verbatim literal (@"...") ends at a
    quote that is not doubled; a regular one honours backslash escapes. Anything between
    literals (`+`, the name of a shared prelude constant) is skipped, which is all these
    declarations hold.
    """
    match = re.search(r"\bconst string " + re.escape(name) + r"\s*=", text)
    if not match:
        return None
    i, n = match.end(), len(text)
    while i < n:
        if text[i] == ";":
            return match.end(), i
        if text.startswith('@"', i):
            i += 2
            while i < n:
                if text[i] == '"':
                    if text.startswith('""', i):
                        i += 2
                        continue
                    break
                i += 1
        elif text[i] == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == _BACKSLASH else 1
        i += 1
    return None


def drift_in(text):
    """The DRIFT lines for module source `text`: one per anchor missing from its own read."""
    warnings, spans = [], {}
    for scope, anchor in LOAD_BEARING:
        if scope not in spans:
            spans[scope] = declaration_span(text, scope)
            if spans[scope] is None:
                warnings.append("module no longer declares %s, the read these copies replay" % scope)
        span = spans[scope]
        if span is not None and anchor not in text[span[0]:span[1]]:
            warnings.append("module's %s no longer contains %r" % (scope, anchor))
    return warnings


def check_drift(source_path):
    """Warn when the module's reads no longer contain what these expressions are built around."""
    try:
        with open(source_path, "r", encoding="utf-8-sig") as handle:
            text = handle.read()
    except OSError as error:
        return ["cannot read %s (%s)" % (source_path, error)]
    return drift_in(text)


def drift_selftest(source_path):
    """Prove the drift guard against the real module source, with no editor and no network.

    Each anchor is renamed at its occurrence INSIDE its own read only, in memory, and the guard
    must name exactly that anchor. Where the anchor is also spelled elsewhere in the file (the
    click expressions), the old whole-file search would have stayed silent; the run counts those
    and requires at least one, so the case the guard exists for is actually exercised
    (N-cxr-w1-04).
    """
    results = []

    def check(label, condition):
        results.append((label, bool(condition)))

    with open(source_path, "r", encoding="utf-8-sig") as handle:
        text = handle.read()
    check("the real module source carries no drift", drift_in(text) == [])
    check("both read constants are found and non-empty",
          all(declaration_span(text, scope) is not None
              and declaration_span(text, scope)[1] - declaration_span(text, scope)[0] > 200
              for scope in (CLAUDE_READ_CONST, CODEX_READ_CONST)))
    hidden_by_whole_file = 0
    for scope, anchor in LOAD_BEARING:
        start, end = declaration_span(text, scope)
        # Broken in the middle, never beside a quote: a character dropped between the two
        # halves of a doubled quote would end the verbatim literal early and shrink the span.
        cut = len(anchor) // 2
        while anchor[cut - 1] == '"' or anchor[cut] == '"':
            cut += 1
        broken = anchor[:cut] + "~" + anchor[cut:]
        mutated = text[:start] + text[start:end].replace(anchor, broken) + text[end:]
        if anchor in mutated:
            hidden_by_whole_file += 1
        warnings = drift_in(mutated)
        check("WITNESS (N-cxr-w1-04) a read-only change is named: %s %s" % (scope, anchor),
              len(warnings) == 1 and scope in warnings[0] and repr(anchor) in warnings[0])
    check("WITNESS (N-cxr-w1-04) some anchors are also spelled outside their read, so the scoping is exercised "
          "(%d)" % hidden_by_whole_file, hidden_by_whole_file >= 1)
    check("WITNESS a missing read constant is named, not passed",
          any("no longer declares" in w for w in drift_in(text.replace(
              "const string " + CLAUDE_READ_CONST + " =", "const string Renamed =", 1))))

    for label, ok in results:
        print("  %s %s" % ("PASS:" if ok else "FAIL:", label))
    bad = [label for label, ok in results if not ok]
    print("\nselftest %s: %d/%d" % ("FAILED" if bad else "OK", len(results) - len(bad), len(results)))
    return 1 if bad else 0

# --- what the module CANNOT see: everything else on the card ----------------------

RAW_BUTTONS = "(function () {" + DOCUMENT_PRELUDE + r"""
  var a = appDocument();
  if (!a.doc) return JSON.stringify({ elements: -1, buttons: [] });
  var out = { elements: a.elements, buttons: [], containers: 0, triggers: 0, forms: 0 };
  out.containers = a.doc.querySelectorAll('[class*="permissionRequestContainer"]').length;
  out.triggers = a.doc.querySelectorAll('button[aria-label="Approval options"]').length;
  out.forms = a.doc.querySelectorAll('form').length;
  // Candidate anchors. This is how `@container/approval-card` was chosen over the
  // aria-label in 1.4.2: dump what the card actually calls itself and pick a name the
  // authors gave the thing, rather than a layout atom or an optional control's label.
  out.anchors = [];
  var named = a.doc.querySelectorAll('[class*="approval"]');
  for (var q = 0; q < named.length && q < 12; q++) {
    out.anchors.push(named[q].tagName + ' ' +
      (typeof named[q].className === 'string' ? named[q].className : '').slice(0, 140));
  }
  var btns = a.doc.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) {
    var b = btns[i];
    var t = (b.innerText || b.textContent || '').replace(/\s+/g, ' ').trim();
    if (!t && !b.getAttribute('aria-label')) continue;
    out.buttons.push({
      text: t.slice(0, 80),
      aria: (b.getAttribute('aria-label') || '').slice(0, 60),
      cls: (typeof b.className === 'string' ? b.className : '').slice(0, 120),
      inForm: !!(b.closest && b.closest('form')),
      disabled: !!b.disabled
    });
  }
  return JSON.stringify(out);
})()"""


def targets(port):
    raw = urllib.request.urlopen("http://127.0.0.1:%d/json/list" % port, timeout=5).read()
    found = []
    for t in json.loads(raw):
        m = re.search(r"extensionId=([^&]*)", t.get("url", ""))
        if not m:
            continue
        found.append((t["id"], m.group(1), t.get("webSocketDebuggerUrl")))
    return found


class Browser(object):
    """One browser-endpoint connection, attaching per target with flatten, as the module does."""

    def __init__(self, port, timeout=8):
        if websocket is None:
            raise SystemExit("the live reads need websocket-client: pip install websocket-client")
        raw = urllib.request.urlopen("http://127.0.0.1:%d/json/version" % port, timeout=5).read()
        self.ws = websocket.create_connection(
            json.loads(raw)["webSocketDebuggerUrl"], timeout=timeout,
            suppress_origin=True)   # websocket-client has no frame-size cap; max_size= was the other library's (F006)
        self.next_id = 1

    def send(self, method, params=None, session=None):
        mid = self.next_id
        self.next_id += 1
        msg = {"id": mid, "method": method, "params": params or {}}
        if session:
            msg["sessionId"] = session
        self.ws.send(json.dumps(msg))
        while True:
            reply = json.loads(self.ws.recv())
            if reply.get("id") == mid:
                return reply

    def attach(self, target_id):
        reply = self.send("Target.attachToTarget",
                          {"targetId": target_id, "flatten": True})
        return reply.get("result", {}).get("sessionId")

    def evaluate(self, session, expression):
        reply = self.send("Runtime.evaluate",
                          {"expression": expression, "returnByValue": True,
                           "awaitPromise": False}, session)
        result = reply.get("result", {}).get("result", {})
        if "value" in result:
            return result["value"]
        return "<%s>" % json.dumps(reply)[:300]

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def timed(port, rounds):
    """Median wall time of one Runtime.evaluate per expression, per target.

    Measures the WHOLE round trip -- serialise, send, execute, receive -- because that is
    what a tick actually pays. The expressions are timed against the same attached session
    one after another, so the connection cost is common to all of them and cancels.
    """
    import statistics
    import time

    browser = Browser(port)
    try:
        for target_id, ext, _ in targets(port):
            session = browser.attach(target_id)
            if not session:
                continue
            print("target %s  extensionId=%s  (%d rounds)" % (target_id[:12], ext, rounds))
            for name, expression in (("claude-read", CLAUDE_READ),
                                     ("codex-read", CODEX_READ),
                                     ("codex-pre-1.4.2", CODEX_READ_PRE_142)):
                samples = []
                for _ in range(rounds):
                    start = time.perf_counter()
                    browser.evaluate(session, expression)
                    samples.append((time.perf_counter() - start) * 1000.0)
                samples.sort()
                print("  %-16s median %6.2f ms   min %6.2f   max %6.2f   chars %d"
                      % (name, statistics.median(samples), samples[0], samples[-1],
                         len(expression)))
            browser.send("Target.detachFromTarget", {"sessionId": session})
    finally:
        browser.close()
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=9321)
    parser.add_argument("--raw", action="store_true",
                        help="dump every button on each target, not just the two reads")
    parser.add_argument("--time", type=int, default=0, metavar="N",
                        help="time N evaluations of each expression per target and stop. "
                             "Answers 'what does a change to the read expression cost', "
                             "which is otherwise guessed at.")
    parser.add_argument("--selftest", action="store_true",
                        help="prove the drift guard against the module source; no editor, no network")
    args = parser.parse_args()

    if args.selftest:
        return drift_selftest(os.path.join(os.path.dirname(os.path.abspath(__file__)), SOURCE))
    if args.time:
        return timed(args.port, args.time)

    drift = check_drift(os.path.join(os.path.dirname(os.path.abspath(__file__)), SOURCE))
    for warning in drift:
        print("DRIFT: %s -- this probe is replaying a stale copy" % warning)

    found = targets(args.port)
    if not found:
        print("no agent webview targets on port %d" % args.port)
        return 1
    browser = Browser(args.port)
    try:
        for target_id, ext, _ in found:
            print("=" * 78)
            print("target %s  extensionId=%s" % (target_id[:12], ext))
            session = browser.attach(target_id)
            if not session:
                print("  could not attach")
                continue
            for name, expression in (("claude-read", CLAUDE_READ),
                                     ("codex-read", CODEX_READ),
                                     ("codex-pre-1.4.2", CODEX_READ_PRE_142)):
                value = browser.evaluate(session, expression)
                print("  %-12s -> %s" % (name, str(value)[:400]))
            if args.raw:
                value = browser.evaluate(session, RAW_BUTTONS)
                try:
                    dump = json.loads(value)
                except Exception:
                    print("  raw          -> %s" % str(value)[:300])
                    continue
                print("  raw: elements=%s permissionRequestContainer=%s "
                      "approvalOptionsTrigger=%s forms=%s"
                      % (dump.get("elements"), dump.get("containers"),
                         dump.get("triggers"), dump.get("forms")))
                for anchor in dump.get("anchors", []):
                    print("      anchor candidate: %s" % anchor)
                for b in dump.get("buttons", []):
                    print("      text=%-28r aria=%-22r inForm=%s cls=%s"
                          % (b["text"], b["aria"], b["inForm"], b["cls"][:70]))
            browser.send("Target.detachFromTarget", {"sessionId": session})
    finally:
        browser.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
