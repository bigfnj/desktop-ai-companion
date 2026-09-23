#!/usr/bin/env python3
"""Replay AgentFlow's CDP read against a live VS Code, and say what it sees.

The shipped module reads a prompt with two expressions and nothing else: a Claude
one anchored on the `permissionRequestContainer` CSS-module prefix, and a Codex one
anchored on `button[aria-label="Approval options"]`. When a prompt is on screen and
AgentFlow does not press it, the question is always which of those two returned
'none' -- and the diagnostic log cannot say, because a read that finds nothing is
indistinguishable from an idle editor.

This prints, per webview target: the outcome of each expression verbatim, plus a
RAW dump of every button the target holds. The raw dump is what tells a selector
miss (buttons exist, expression says 'none') from an empty panel.

    python agentflow_cdp_probe.py [--port 9321] [--raw]

Prints prompt option labels, which is the whole point, so its output is NOT safe to
paste into a public issue unedited -- unlike the module's own log, which is.
"""

import argparse
import json
import os
import re
import sys
import urllib.request

import websocket  # websocket-client

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
"""

CLAUDE_READ = "(function () {" + DOCUMENT_PRELUDE + r"""
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var c = a.doc.querySelector('[class*="permissionRequestContainer"]');
  if (!c) return 'none';
  var bc = c.querySelector('[class*="buttonContainer"]');
  if (!bc) return 'none';
  var out = { tool: '', header: '', ext: '', options: [], disabled: [] };
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
  return JSON.stringify(out);
})()"""

CODEX_READ = "(function () {" + DOCUMENT_PRELUDE + r"""
  var a = appDocument();
  if (!a.doc || a.elements < MinElements) return 'unreachable';
  var card = a.doc.querySelector('[class*="@container/approval-card"]');
  if (!card) return 'none';
  var form = card.querySelector('form');
  if (!form) return 'none';
  var trigger = form.querySelector('button[aria-label="Approval options"]');
  var out = { tool: '', header: '', ext: '', options: [], disabled: [] };
  var btns = form.querySelectorAll('button');
  for (var i = 0; i < btns.length; i++) {
    var b = btns[i];
    if (b === trigger) continue;
    out.options.push((b.innerText || b.textContent || '').replace(/\s+/g, ' ').trim());
    out.disabled.push(!!b.disabled);
  }
  if (out.options.length === 0) return 'none';
  return JSON.stringify(out);
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
SOURCE = "../../modules/AgentFlow/CdpApprover.cs"
LOAD_BEARING = [
    '[class*=""@container/approval-card""]',
    "card.querySelector('form')",
    'button[aria-label=""Approval options""]',
    "b === trigger",
]


def check_drift(source_path):
    """Warn when the module no longer contains what these expressions are built around."""
    try:
        with open(source_path, "r", encoding="utf-8-sig") as handle:
            text = handle.read()
    except OSError as error:
        return ["cannot read %s (%s)" % (source_path, error)]
    return ["module no longer contains %r" % s for s in LOAD_BEARING if s not in text]

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
        raw = urllib.request.urlopen("http://127.0.0.1:%d/json/version" % port, timeout=5).read()
        self.ws = websocket.create_connection(
            json.loads(raw)["webSocketDebuggerUrl"], timeout=timeout,
            suppress_origin=True, max_size=None)
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=9321)
    parser.add_argument("--raw", action="store_true",
                        help="dump every button on each target, not just the two reads")
    args = parser.parse_args()

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
