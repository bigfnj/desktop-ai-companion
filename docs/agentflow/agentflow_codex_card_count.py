#!/usr/bin/env python3
"""Count the Codex approval cards in every VS Code Codex panel, and watch them come and go.

Answers one question AgentFlow's own log cannot: when several Codex requests are waiting in
ONE panel, are their cards all in the DOM at once (STACKED) or does the next one mount only
after the current one is answered (QUEUED)? The module reads the first card only, so the
two shapes want different fixes. See docs/DESIGN-REGISTER.md, "agentflow/codex-card-queue".

COUNTS ONLY. No card text, no option label, no command, no path ever leaves the page: each
card reports whether it has a form, how many buttons, how many are enabled, whether it is
visible, and two ids. Its output is therefore safe to paste into an issue, unlike
agentflow_cdp_probe.py, whose whole point is to print the labels.

PRESSES NOTHING and sends no input of any kind. Escape in a Codex panel is Deny.

THE PROBE'S IDS ARE ITS OWN. It mints into window.__agentflowCountProbe, never into the
module's window.__agentflowCards, so running it does not make a card the module has not
read yet look already seen. It READS the module's map (get, never set) to say which card
the module has minted: `m` on a card is "AgentFlow has read this element".

    python agentflow_codex_card_count.py [--port 9321]            one read of every panel
    python agentflow_codex_card_count.py --watch 120 [--every 200] print each change for 120 s

Run it with the toolbox Python (%TOOLBOX_PYTHON%), which has websocket-client.
"""

import argparse
import json
import re
import sys
import time
import urllib.request

import websocket  # websocket-client

CODEX_MARKER = "extensionId=openai.chatgpt"

# Every document in the webview, not only the richest one the module picks, so a card in a
# second iframe is counted rather than missed. `best` marks the document the module reads.
COUNT_EXPRESSION = r"""
(function () {
  var docs = [document], frames = document.querySelectorAll('iframe');
  for (var i = 0; i < frames.length; i++) {
    try { if (frames[i].contentDocument) docs.push(frames[i].contentDocument); } catch (e) { }
  }
  var best = -1, bestCount = -1;
  for (var d = 0; d < docs.length; d++) {
    var n = docs[d].querySelectorAll('*').length;
    if (n > bestCount) { bestCount = n; best = d; }
  }
  var mine = window.__agentflowCountProbe || (window.__agentflowCountProbe = new WeakMap());
  var theirs = window.__agentflowCards;
  var out = { elements: bestCount, docs: [] };
  for (var d = 0; d < docs.length; d++) {
    var cs = docs[d].querySelectorAll('[class*="@container/approval-card"]');
    if (!cs.length) continue;
    var each = [];
    for (var c = 0; c < cs.length; c++) {
      var el = cs[c], id = mine.get(el);
      if (!id) {
        id = ('000' + Math.floor(Math.random() * 65536).toString(16)).slice(-4);
        mine.set(el, id);
      }
      var fm = el.querySelector('form');
      var bs = fm ? fm.querySelectorAll('button') : [];
      var en = 0;
      for (var b = 0; b < bs.length; b++) if (!bs[b].disabled) en++;
      var r = el.getBoundingClientRect();
      // Structure, never content: attribute NAMES on the card and up to six ancestors, so two kinds
      // of card can be told apart by where the app put them. Values can carry ids, so none are read.
      var names = [], up = el;
      for (var k = 0; k < 7 && up && up.attributes; k++, up = up.parentElement) {
        for (var q = 0; q < up.attributes.length; q++) {
          var an = up.attributes[q].name;
          if ((an.indexOf('data-') === 0 || an === 'role') && names.indexOf(k + ':' + an) < 0) names.push(k + ':' + an);
        }
      }
      each.push({ id: id, m: !!(theirs && theirs.get(el)), form: !!fm, buttons: bs.length,
                  enabled: en, visible: r.width > 0 && r.height > 0,
                  trigger: !!(fm && fm.querySelector('button[aria-label="Approval options"]')),
                  submit: fm ? fm.querySelectorAll('button[type="submit"]').length : 0,
                  attrs: names });
    }
    out.docs.push({ doc: d, best: d === best, cards: each });
  }
  return JSON.stringify(out);
})()
"""


def codex_targets(port):
    raw = urllib.request.urlopen("http://127.0.0.1:%d/json/list" % port, timeout=5).read()
    return [t["id"] for t in json.loads(raw) if CODEX_MARKER in t.get("url", "")]


class Browser(object):
    """One browser-endpoint connection, attaching per target with flatten, as the module does."""

    def __init__(self, port, timeout=8):
        raw = urllib.request.urlopen("http://127.0.0.1:%d/json/version" % port, timeout=5).read()
        url = json.loads(raw)["webSocketDebuggerUrl"]
        if not url.startswith("ws://127.0.0.1:"):
            raise SystemExit("refusing a debugger socket that is not loopback: %s" % url)
        self.ws = websocket.create_connection(url, timeout=timeout, suppress_origin=True)
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
        reply = self.send("Target.attachToTarget", {"targetId": target_id, "flatten": True})
        return reply.get("result", {}).get("sessionId")

    def count(self, session):
        reply = self.send("Runtime.evaluate",
                          {"expression": COUNT_EXPRESSION, "returnByValue": True}, session)
        value = reply.get("result", {}).get("result", {}).get("value")
        try:
            return json.loads(value)
        except (TypeError, ValueError):
            return None

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def describe(state, structure=False):
    """One line per panel: every card as id[m]:enabled/buttons, with v for visible.

    With structure, each card also says t (has the Approval options trigger), s<N> (submit buttons)
    and the data-* attribute names found on it (0:) and its ancestors (1: to 6:)."""
    if state is None:
        return "no answer"
    if not state["docs"]:
        return "no cards (elements=%d)" % state["elements"]
    parts = []
    for doc in state["docs"]:
        cards = []
        for c in doc["cards"]:
            extra = ""
            if structure:
                extra = " %ss%d {%s}" % ("t" if c.get("trigger") else "", c.get("submit", 0),
                                         " ".join(c.get("attrs", [])))
            cards.append("%s%s:%d/%d%s%s%s" % (c["id"], "m" if c["m"] else "", c["enabled"],
                                               c["buttons"], "v" if c["visible"] else "",
                                               "" if c["form"] else " noform", extra))
        parts.append("doc%d%s [%s]" % (doc["doc"], "*" if doc["best"] else "", ", ".join(cards)))
    return "; ".join(parts)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=9321)
    parser.add_argument("--watch", type=float, default=0, metavar="SECONDS",
                        help="sample repeatedly for this long and print each change")
    parser.add_argument("--every", type=int, default=200, metavar="MS",
                        help="milliseconds between samples in --watch (default 200)")
    parser.add_argument("--structure", action="store_true",
                        help="also print each card's trigger, submit count and data-* attribute names")
    args = parser.parse_args()

    found = codex_targets(args.port)
    if not found:
        print("no Codex panels on port %d" % args.port)
        return 1
    browser = Browser(args.port)
    sessions = []
    try:
        for n, target_id in enumerate(found):
            session = browser.attach(target_id)
            print("panel %d = target %s%s" % (n, target_id[:8], "" if session else "  (could not attach)"))
            if session:
                sessions.append((n, session))
        print("legend: id + m when AgentFlow has minted it : enabled/buttons, v = visible, * = the document AgentFlow reads")
        start = time.monotonic()
        last = {}
        while True:
            stamp = time.strftime("%H:%M:%S") + ".%03d" % (int(time.time() * 1000) % 1000)
            for n, session in sessions:
                line = describe(browser.count(session), args.structure)
                if last.get(n) != line:
                    print("%s  +%7.1fs  panel %d: %s" % (stamp, time.monotonic() - start, n, line))
                    sys.stdout.flush()
                    last[n] = line
            if time.monotonic() - start >= args.watch:
                break
            time.sleep(args.every / 1000.0)
    finally:
        for _, session in sessions:
            try:
                browser.send("Target.detachFromTarget", {"sessionId": session})
            except Exception:
                pass
        browser.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
