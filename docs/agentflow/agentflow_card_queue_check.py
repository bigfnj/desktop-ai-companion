#!/usr/bin/env python3
"""Drive AgentFlow's real press path against a real browser engine, with cards that look alike.

BUG-017 (2026-10-20): a subagent's seven edits to one file stood auto-approve down at the fourth, every
press having landed, because an Edit card shows its path and nothing else and the repeat guard told cards
apart by their text. 1.9.1 tells them apart by their ELEMENT. The module self-test asserts the decision over
a fake CDP server, but that server answers with canned strings and cannot run the renderer code that mints
the element ids. This runs all of it for real:

  - a loopback page that copies the Claude Code card's markup and behaviour, read out of the shipped
    webview/index.js (2.1.301): one element per request (the parent keys the card by the request's id),
    its buttons disabled for 500 ms after it mounts, the panel inside a same-origin iframe as VS Code
    nests it, and a target url carrying the extension id AgentFlow filters on;
  - a headless Edge or Chrome with a throwaway profile and its own debugging port;
  - the REAL host with ONLY the AgentFlow module, under a throwaway data root whose settings point AgentFlow
    at that browser and nowhere else (argvPath at a fake argv.json naming the browser's port, cdpPort the
    same port), so nothing it presses can be anything but this page.

Scenarios, in one launch of the host, in this order:

  queue   six identical edit cards, each a new element, each answered by its Yes. 1.9.1 presses all six and
          announces none; 1.9.0 stands down at the fourth (--control holds the published build to that).
  settle  one card whose buttons stay disabled for 25 s. 1.9.1 announces it one sweep AFTER it first refused
          it, not on that sweep (1.9.0 did both on the same sweep, --control), then presses it once enabled.
  reuse   six identical requests drawn into ONE reused element, the shape an unkeyed renderer would give. Its
          id never changes, so this stands down after three presses: the proof that the id belongs to the
          element and is not minted afresh on every read.
  stuck   one card whose Yes does nothing. Five presses (three before 1.11.0), then the stand-down, then no
          more.

1.11.0 added two scenarios with TWO panels open at once, each its own browser tab and so its own target:

  pair    two queues of six, one per panel. 1.11.0 presses both panels in one sweep, so presses come in pairs
          a few milliseconds apart; 1.10.0 pressed one prompt a tick and never two in one sweep (--control 1.10.0).
  stuck2  two stuck cards, one per panel. 1.11.0 presses each five times and then leaves both alone (BUG-018);
          1.10.0's one counter, reset by the other panel's card, pressed them a tick at a time without end.

--check-seconds N writes the check interval into the copy's settings (1.11.0) and paces the waits; below ten
it also checks that presses come on that interval rather than every ten seconds. --control alone holds the
build to 1.9.0's behaviour, as it always did; --control 1.10.0 holds it to 1.10.0's.

It never touches VS Code, its debugging port, the companion you run, its data root, ~/.claude or ~/.codex:
the copy's transcript roots are empty folders. The copy does put its default companion on screen for the
length of the run, with AgentFlow's speech, sound and animation switched off. Needs websocket-client.

    python agentflow_card_queue_check.py --exe <build>/DesktopAICompanion.exe [--agentflow <module folder>]
        [--control [1.9.0|1.10.0]] [--check-seconds N] [--browser <msedge.exe or chrome.exe>] [--work <folder>]
        [--only queue,settle]

Exit 0 when every scenario met its expectation, 1 when one did not, 2 when the run could not be set up.
"""

import argparse
import functools
import http.server
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

import websocket  # websocket-client

TICK = 10            # AgentFlow's approve sweep, in seconds; --check-seconds sets it (1.11.0)
CAP = 5              # presses of one card before the repeat guard leaves it alone; 3 under --control
CLAUDE_MARKER = "extensionId=Anthropic.claude-code"

# The log lines this reads, all written by the module itself and all metadata only.
CLICKED = "[agentflow] auto-approve clicked for an edit (.sh)"
STANDDOWN = "[agentflow] standing down: pressed the same prompt"
DISABLED = "[agentflow] refused: the approve-once row is disabled"
NOTICE = "[agentflow] a prompt is waiting on screen for an edit (.sh)"

# ---- the page -------------------------------------------------------------------------------------------

OUTER = """<!doctype html>
<html><head><meta charset="utf-8"><title>agentflow card queue check</title></head>
<body style="margin:0">
<iframe id="f" style="border:0;width:100%;height:100vh"></iframe>
<script>document.getElementById('f').src = 'fake.html' + location.search;</script>
</body></html>
"""

# The card's markup, class for class, from webview/index.js 2.1.301 (the CSS-module hash suffix is the one
# that build ships; AgentFlow matches the prefix only). The fold control is a button OUTSIDE the button
# container, as in the bundle, so a reader that strayed outside the container would read it as an option.
CARD = (
    '<div class="permissionRequestContainerBackground_qlaBag"></div>'
    '<div class="foldButton_qlaBag"><button class="iconButton_qlaBag" aria-label="Collapse">-</button></div>'
    '<div class="permissionRequestContent_qlaBag foldsToTitle_qlaBag">'
    '<div class="permissionRequestHeader_qlaBag">Make this edit to '
    '<span class="permissionPath_qlaBag">C:/work/demo/script.sh</span>?</div></div>'
    '<div class="buttonContainer_qlaBag">'
    '<button class="button_qlaBag primary_qlaBag" data-role="yes"><span class="shortcutNum_qlaBag">1</span> Yes</button>'
    '<button class="button_qlaBag" data-role="no"><span class="shortcutNum_qlaBag">2</span> No</button>'
    '<textarea class="rejectMessageInput_qlaBag" placeholder="Tell Claude what to do instead"></textarea>'
    '</div>'
    '<div class="keyboardHints_qlaBag">Esc to cancel</div>'
)

INNER_SCRIPT = """
(function () {
  var q = new URLSearchParams(location.search);
  var scenario = q.get('scenario') || 'queue';
  var n = parseInt(q.get('n') || '6', 10);
  var enableMs = parseInt(q.get('enable') || '500', 10);
  var h = window.harness = { scenario: scenario, n: n, mounted: 0, answered: 0, clicks: 0, elements: 0, done: false };
  var host = document.getElementById('permissions');
  var current = null;
  function arm(c) {
    var yes = c.querySelector('[data-role=yes]'), no = c.querySelector('[data-role=no]');
    yes.disabled = true; no.disabled = true;
    setTimeout(function () { yes.disabled = false; no.disabled = false; }, enableMs);
  }
  function mount() {
    h.mounted++;
    // reuse: the SAME element takes the next request, text unchanged, as an unkeyed render would.
    if (scenario === 'reuse' && current) { arm(current); return; }
    current = document.createElement('div');
    current.className = 'permissionRequestContainer_qlaBag';
    current.tabIndex = 0;
    current.setAttribute('data-focused-index', '0');
    current.innerHTML = __CARD__;
    current.querySelector('[data-role=yes]').addEventListener('click', onYes);
    h.elements++;
    host.appendChild(current);
    arm(current);
  }
  function onYes() {
    h.clicks++;
    if (scenario === 'stuck') return;            // the click runs and the card stays
    h.answered++;
    if (h.answered >= n) { current.remove(); current = null; h.done = true; return; }
    if (scenario === 'reuse') { setTimeout(mount, 300); return; }
    current.remove(); current = null;            // the request resolves and its element unmounts...
    setTimeout(mount, 800);                      // ...and the next request mounts a NEW element
  }
  mount();
})();
"""


def inner_page():
    filler = "".join('<div class="message"><p>message %d of an ordinary conversation</p></div>' % i
                     for i in range(40))
    script = INNER_SCRIPT.replace("__CARD__", json.dumps(CARD))
    return ('<!doctype html><html><head><meta charset="utf-8"></head><body>'
            '<div id="conversation">' + filler + '</div><div id="permissions"></div>'
            '<script>' + script + '</script></body></html>')


class PageHandler(http.server.BaseHTTPRequestHandler):
    def __init__(self, pages, *args, **kwargs):
        self.pages = pages
        super().__init__(*args, **kwargs)

    def do_GET(self):
        body = self.pages.get(self.path.split("?")[0])
        if body is None:
            self.send_response(404)
            self.end_headers()
            return
        data = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


class QuietServer(http.server.ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        pass   # a browser dropping a connection as it is killed is not a finding


# ---- plumbing ------------------------------------------------------------------------------------------

def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def http_json(url, method="GET", timeout=3):
    request = urllib.request.Request(url, method=method)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def find_browser(given):
    if given:
        return given
    for root in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles"), os.environ.get("LOCALAPPDATA")):
        if not root:
            continue
        for rel in (("Microsoft", "Edge", "Application", "msedge.exe"), ("Google", "Chrome", "Application", "chrome.exe")):
            path = os.path.join(root, *rel)
            if os.path.isfile(path):
                return path
    return None


def call(ws_url, method, params):
    ws = websocket.create_connection(ws_url, timeout=5, suppress_origin=True)
    try:
        ws.send(json.dumps({"id": 1, "method": method, "params": params}))
        while True:
            reply = json.loads(ws.recv())
            if reply.get("id") == 1:
                return reply.get("result", {})
    finally:
        ws.close()


def evaluate(ws_url, expression):
    return call(ws_url, "Runtime.evaluate",
                {"expression": expression, "returnByValue": True}).get("result", {}).get("value")


def open_page(cdp_port, url, scenario):
    """A new tab, navigated by Page.navigate. NOT /json/new?<url>: that endpoint cut the url at its first
    '&' on the first run of this harness, so every scenario after the first loaded the page's defaults and
    ran a queue under another scenario's name. The page has to say which scenario it is running."""
    target = http_json("http://127.0.0.1:%d/json/new" % cdp_port, method="PUT")
    ws_url = target["webSocketDebuggerUrl"]
    call(ws_url, "Page.navigate", {"url": url})
    deadline = time.time() + 15
    while time.time() < deadline:
        try:
            state = page_state(ws_url)
        except Exception:
            state = None
        if state and state.get("scenario") == scenario:
            return target, ws_url
        time.sleep(0.3)
    raise RuntimeError("the page never reported scenario %r" % scenario)


def page_state(ws_url):
    raw = evaluate(ws_url, "JSON.stringify(document.getElementById('f').contentWindow.harness || null)")
    return json.loads(raw) if raw else None


def log_lines(path):
    if not os.path.isfile(path):
        return []
    with open(path, encoding="utf-8-sig", errors="replace") as handle:
        return handle.read().splitlines()


def stamp(line):
    """Seconds since midnight from the log's leading HH:MM:SS.fff, or None."""
    try:
        return int(line[0:2]) * 3600 + int(line[3:5]) * 60 + int(line[6:8]) + int(line[9:12]) / 1000.0
    except (ValueError, IndexError):
        return None


def make_copy(exe, agentflow, dest):
    source = os.path.dirname(os.path.abspath(exe))
    os.makedirs(dest)
    for name in os.listdir(source):
        path = os.path.join(source, name)
        if os.path.isfile(path):
            shutil.copy2(path, dest)
    shutil.copytree(agentflow or os.path.join(source, "modules", "agentflow"),
                    os.path.join(dest, "modules", "agentflow"))
    return os.path.join(dest, os.path.basename(exe))


# ---- scenarios -----------------------------------------------------------------------------------------

SCENARIOS = {
    "queue": {"n": 6, "enable": 500},
    "settle": {"n": 1, "enable": 25000},
    "reuse": {"n": 6, "enable": 500},
    "stuck": {"n": 1, "enable": 500},
}


def run_scenario(name, control, cdp_port, web_port, diag, results):
    spec = SCENARIOS[name]
    url = "http://127.0.0.1:%d/index.html?%s&scenario=%s&n=%d&enable=%d" % (
        web_port, CLAUDE_MARKER, name, spec["n"], spec["enable"])
    start = len(log_lines(diag))
    target, ws_url = open_page(cdp_port, url, name)
    began = time.time()

    def lines():
        return log_lines(diag)[start:]

    def count(text):
        return sum(1 for line in lines() if text in line)

    state = None
    if name == "queue":
        limit = spec["n"] * TICK + 45
        while time.time() - began < limit:
            state = page_state(ws_url)
            if state and state["done"]:
                break
            if control and count(STANDDOWN):
                break
            time.sleep(1)
        time.sleep(TICK + 2)                     # one more sweep, to see nothing late
    elif name == "settle":
        limit = spec["enable"] / 1000.0 + 3 * TICK + 10
        while time.time() - began < limit:
            state = page_state(ws_url)
            if state and state["done"]:
                break
            time.sleep(1)
        time.sleep(2)
    else:
        limit = max(spec["n"], CAP + 1) * TICK + 45
        while time.time() - began < limit:
            state = page_state(ws_url)
            if count(STANDDOWN) or (state and state["done"]):
                break
            time.sleep(1)
        time.sleep(2 * TICK + 2)                 # two more sweeps: a press past the cap would land in them
    state = page_state(ws_url) or {}
    seen = lines()
    try:
        urllib.request.urlopen("http://127.0.0.1:%d/json/close/%s" % (cdp_port, target["id"]), timeout=3).read()
    except Exception:
        pass
    time.sleep(TICK + 2)                         # an empty sweep between scenarios, as between real prompts

    clicked, stood, disabled, notice = count(CLICKED), count(STANDDOWN), count(DISABLED), count(NOTICE)
    old = control == "1.9.0"                     # the 1.9.0 behaviours; 1.10.0 queues and settles as 1.11.0 does
    checks = []
    if name == "queue" and not old:
        checks = [("every card answered", state.get("answered") == spec["n"]),
                  ("one element per card", state.get("elements") == spec["n"]),
                  ("one press logged per card", clicked == spec["n"]),
                  ("no stand-down", stood == 0),
                  ("no 'waiting on screen' notice", notice == 0)]
        if TICK < 10 and not control:
            gaps = press_gaps(seen)
            median = sorted(gaps)[len(gaps) // 2] if gaps else None
            checks.append(("presses come on the %d s check interval, not every ten seconds (median gap %s s)"
                           % (TICK, None if median is None else round(median, 1)),
                           median is not None and median <= 1.5 * TICK + 0.5))
    elif name == "queue":
        checks = [("1.9.0: three cards answered, then nothing", state.get("answered") == 3),
                  ("1.9.0: the stand-down is logged", stood == 1)]
    elif name == "settle":
        first_refusal = next((stamp(l) for l in seen if DISABLED in l), None)
        first_notice = next((stamp(l) for l in seen if NOTICE in l), None)
        gap = (first_notice - first_refusal) if first_notice is not None and first_refusal is not None else None
        checks = [("the disabled card was refused", disabled >= 1),
                  ("announced exactly once", notice == 1),
                  ("then pressed once its buttons enabled", state.get("answered") == 1 and clicked == 1)]
        if old:
            checks.append(("1.9.0: announced on the same sweep that refused it (gap %s s)" % gap,
                           gap is not None and gap < 2))
        else:
            checks.append(("announced one sweep after the first refusal, not on it (gap %s s)" % gap,
                           gap is not None and gap >= TICK / 2.0))
    elif name == "reuse":
        checks = [("one element for every request", state.get("elements") == 1),
                  ("%d presses, then no more" % CAP, state.get("clicks") == CAP and clicked == CAP),
                  ("the stand-down is logged", stood == 1)]
    elif name == "stuck":
        checks = [("%d presses, then no more" % CAP, state.get("clicks") == CAP and clicked == CAP),
                  ("the stand-down is logged", stood == 1),
                  ("the stood-down card is announced", notice == 1)]

    for label, ok in checks:
        results.append((name, label, ok))
        print("  %-6s %-4s %s" % (name, "PASS" if ok else "FAIL", label))
    print("         page %s; log: %d press, %d stand-down, %d disabled-refusal, %d notice" % (
        json.dumps({k: state.get(k) for k in ("scenario", "mounted", "elements", "clicks", "answered", "done")}),
        clicked, stood, disabled, notice))


def press_gaps(seen):
    """Seconds between consecutive press lines, in log order."""
    stamps = [stamp(l) for l in seen if CLICKED in l]
    stamps = [s for s in stamps if s is not None]
    return [b - a for a, b in zip(stamps, stamps[1:])]


# Two panels at once (1.11.0), each a tab of its own, both running the inner scenario named here.
MULTI = {
    "pair": {"inner": "queue", "n": 6, "enable": 500},
    "stuck2": {"inner": "stuck", "n": 1, "enable": 500},
}


def run_multi(name, control, cdp_port, web_port, diag, results):
    spec = MULTI[name]
    start = len(log_lines(diag))
    tabs = []
    for panel in ("a", "b"):
        url = "http://127.0.0.1:%d/index.html?%s&scenario=%s&n=%d&enable=%d&panel=%s" % (
            web_port, CLAUDE_MARKER, spec["inner"], spec["n"], spec["enable"], panel)
        tabs.append(open_page(cdp_port, url, spec["inner"]))
    began = time.time()

    def lines():
        return log_lines(diag)[start:]

    def count(text):
        return sum(1 for line in lines() if text in line)

    if name == "pair":
        limit = 2 * spec["n"] * TICK + 45        # one press a tick, the old pace, still fits
        while time.time() - began < limit:
            states = [page_state(ws) for _, ws in tabs]
            if all(s and s["done"] for s in states):
                break
            time.sleep(1)
        time.sleep(TICK + 2)
    else:
        # Nine sweeps: five presses on each panel and four past the cap, where an unbounded guard keeps pressing.
        time.sleep(9 * TICK + 5)
    states = [page_state(ws) or {} for _, ws in tabs]
    seen = lines()
    for target, _ in tabs:
        try:
            urllib.request.urlopen("http://127.0.0.1:%d/json/close/%s" % (cdp_port, target["id"]), timeout=3).read()
        except Exception:
            pass
    time.sleep(TICK + 2)

    clicked, stood = count(CLICKED), count(STANDDOWN)
    clicks = [s.get("clicks") or 0 for s in states]
    # Two presses within a second of each other are one sweep's: the next sweep is a whole interval away.
    same_sweep = sum(1 for gap in press_gaps(seen) if gap < 1.0)
    checks = []
    if name == "pair" and control != "1.10.0":
        checks = [("both panels' queues answered", all(s.get("answered") == spec["n"] for s in states)),
                  ("one press logged per card", clicked == 2 * spec["n"]),
                  ("no stand-down", stood == 0),
                  ("both panels pressed in one sweep, again and again (%d sweeps pressed two)" % same_sweep,
                   same_sweep >= 3)]
    elif name == "pair":
        checks = [("1.10.0: never two presses in one sweep (%d)" % same_sweep, same_sweep == 0),
                  ("1.10.0: both queues still answered, a press a tick", all(s.get("answered") == spec["n"] for s in states))]
    elif control != "1.10.0":
        checks = [("each stuck panel pressed %d times and no more (%s)" % (CAP, clicks), clicks == [CAP, CAP]),
                  ("one press logged per press", clicked == 2 * CAP),
                  ("the stand-down is logged", stood >= 1)]
    else:
        checks = [("1.10.0: BUG-018, the two stuck panels are pressed past the cap (%s)" % clicks,
                   sum(clicks) > 2 * CAP)]

    for label, ok in checks:
        results.append((name, label, ok))
        print("  %-6s %-4s %s" % (name, "PASS" if ok else "FAIL", label))
    print("         pages %s; log: %d press, %d stand-down, %d presses within a second of the last" % (
        json.dumps([{k: s.get(k) for k in ("elements", "clicks", "answered", "done")} for s in states]),
        clicked, stood, same_sweep))


def main():
    global TICK, CAP
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--exe", required=True)
    parser.add_argument("--agentflow", default=None, help="the AgentFlow module folder to test (default: the exe's own)")
    parser.add_argument("--control", nargs="?", const="1.9.0", default=None, choices=("1.9.0", "1.10.0"),
                        help="hold the build to 1.9.0's behaviour instead (or name 1.10.0)")
    parser.add_argument("--check-seconds", type=int, default=None,
                        help="the check interval to write into the copy's settings (1.11.0); paces the waits too")
    parser.add_argument("--browser", default=None)
    parser.add_argument("--work", default=None)
    parser.add_argument("--only", default=None)
    parser.add_argument("--version", default=None, help="the agentflow version the host must report loading")
    args = parser.parse_args()

    browser = find_browser(args.browser)
    if not browser or not os.path.isfile(args.exe):
        print("cannot run: browser=%s exe=%s" % (browser, args.exe))
        return 2
    if args.check_seconds is not None:
        if not 1 <= args.check_seconds <= 30:
            print("cannot run: --check-seconds is 1 to 30, as the module clamps it")
            return 2
        TICK = args.check_seconds
    if args.control:
        CAP = 3
    # The two-panel scenarios are 1.11.0's; a 1.9.0 control has nothing to say about them.
    every = list(SCENARIOS) + ([] if args.control == "1.9.0" else list(MULTI))
    names = [n for n in every if not args.only or n in args.only.split(",")]
    work = args.work or tempfile.mkdtemp(prefix="af-queue-")
    if os.path.isdir(work) and os.listdir(work):
        print("cannot run: %s is not empty" % work)
        return 2
    os.makedirs(work, exist_ok=True)
    print("work folder: " + work)

    pages = {"/index.html": OUTER, "/fake.html": inner_page()}
    web = QuietServer(("127.0.0.1", 0), functools.partial(PageHandler, pages))
    threading.Thread(target=web.serve_forever, daemon=True).start()
    web_port = web.server_address[1]

    cdp_port = free_port()
    browser_proc = subprocess.Popen([
        browser, "--headless=new", "--remote-debugging-port=%d" % cdp_port,
        "--user-data-dir=" + os.path.join(work, "browser-profile"),
        "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
        "--disable-background-networking", "--disable-component-update", "about:blank"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    app = None
    results = []
    try:
        deadline = time.time() + 30
        while True:
            try:
                http_json("http://127.0.0.1:%d/json/version" % cdp_port)
                break
            except Exception:
                if time.time() > deadline:
                    print("cannot run: the browser's debugging port never answered")
                    return 2
                time.sleep(0.5)
        print("browser %s on debugging port %d, page server on %d" % (os.path.basename(browser), cdp_port, web_port))

        exe = make_copy(args.exe, args.agentflow, os.path.join(work, "app"))
        data = os.path.join(work, "data")
        profile = os.path.join(work, "profile")
        for folder in (os.path.join(data, "modules", "agentflow"), os.path.join(profile, ".claude", "projects"),
                       os.path.join(profile, ".codex", "sessions"), os.path.join(work, "temp")):
            os.makedirs(folder)
        with open(os.path.join(profile, ".claude", "settings.json"), "w", encoding="utf-8") as handle:
            handle.write("{}")
        argv = os.path.join(work, "argv.json")
        with open(argv, "w", encoding="utf-8") as handle:
            json.dump({"remote-debugging-port": str(cdp_port)}, handle)
        settings = {"enabled": "true", "mode": "autoApprove", "autoApprove": "true",
                    "watchClaude": "true", "watchCodex": "true",
                    "notifySpeak": "false", "notifySound": "false", "animate": "false",
                    "cdpPort": str(cdp_port), "argvPath": argv}
        if args.check_seconds is not None:
            settings["checkSeconds"] = str(args.check_seconds)
        with open(os.path.join(data, "modules", "agentflow", "settings.json"), "w", encoding="utf-8") as handle:
            json.dump(settings, handle)

        env = dict(os.environ)
        env.update({
            "DESKTOP_AI_COMPANION_DATA_ROOT": data,
            "TEMP": os.path.join(work, "temp"), "TMP": os.path.join(work, "temp"),
            "AGENTFLOW_CLAUDE_ROOT": os.path.join(profile, ".claude", "projects"),
            "AGENTFLOW_CODEX_ROOT": os.path.join(profile, ".codex", "sessions"),
            "AGENTFLOW_CLAUDE_HOME": profile,
            "DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT": "1",
        })
        diag = os.path.join(data, "diagnostics.log")
        app = subprocess.Popen([exe], env=env)
        deadline = time.time() + 60
        loaded = None
        while time.time() < deadline and loaded is None:
            for line in log_lines(diag):
                at = line.find("module loaded: agentflow ")
                if at >= 0:
                    loaded = line[at + len("module loaded: agentflow "):].strip()
            time.sleep(0.5)
        print("host pid %d loaded agentflow %s" % (app.pid, loaded))
        if loaded is None or (args.version and loaded != args.version):
            print("cannot run: wanted agentflow %s" % (args.version or "loaded"))
            return 2
        deadline = time.time() + 40
        while time.time() < deadline and not any("Auto-approve: on" in l for l in log_lines(diag)):
            time.sleep(0.5)
        print("mode: " + ("control (%s's behaviour expected)" % args.control if args.control
                          else "fix (1.11.0's behaviour expected)")
              + "; check interval %d s, repeat cap %d" % (TICK, CAP))

        for name in names:
            if name in MULTI:
                run_multi(name, args.control, cdp_port, web_port, diag, results)
            else:
                run_scenario(name, args.control, cdp_port, web_port, diag, results)

        print("")
        print("---- agentflow lines ----")
        for line in log_lines(diag):
            if "[agentflow]" in line:
                print(line)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
            app.wait(15)
        browser_proc.kill()
        browser_proc.wait(15)
        web.shutdown()

    failed = [r for r in results if not r[2]]
    print("")
    print("RESULT=%s (%d checks, %d failed); evidence in %s" % ("PASS" if not failed and results else "FAIL",
                                                              len(results), len(failed), work))
    return 0 if results and not failed else 1


if __name__ == "__main__":
    sys.exit(main())
