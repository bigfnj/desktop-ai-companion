"""Shimeji parity comparer (CONTRACT.md v1, sections 3 and 6).

    compare.py <oracle trace.jsonl> <host trace.jsonl> [--scenario scenario.json] [--json out.json]
    compare.py --self-test

Reads two traces of the same scenario and decides whether the runtimes behaved the same. A directed scenario
(rng kind none or tape) must match EXACTLY on events and on the compared state fields; a free-running one
(splitmix64) is a separate claim and is reported as such. Every failure names the scenario, the first mismatching
tick, the entity and the field. Exit 0 = parity (exact, or only declared tolerances/policies), 1 = a mismatch,
2 = a trace that cannot be judged (missing fields, truncated, aborted, wrong scenario, no build witness).

--self-test builds a reference trace and a set of deliberately wrong ones, and requires each wrong one to fail with
its expected class; it is the proof that this comparer can fail.
"""
import copy
import json
import sys

SCHEMA_TRACE = "shimeji-parity-trace/1"
EXACT_STATE_FIELDS = ("imageSet", "behavior", "action", "visible", "lookRight", "dragging", "pending", "target")
REQUIRED_STATE_FIELDS = ("tick", "sid", "imageSet", "behavior", "action", "actionClass", "actionAge", "pose",
                         "visible", "anchor", "origin", "lookRight", "affordances", "target", "dragging", "pending")
REQUIRED_EVENT_FIELDS = ("tick", "sid", "kind")
EVENT_PAYLOAD_KEYS = {
    "behavior": ("from", "to", "cause"),
    "action": ("from", "to", "cause"),
    "transform": ("fromImageSet", "toImageSet", "behavior"),
    "birth-request": ("child", "imageSet", "behavior", "anchor", "lookRight", "ordinal"),
    "birth-activate": ("child",),
    "birth-refused": ("child", "reason"),
    "scan-target": ("affordance", "target"),
    "interact": ("with",),
    "dispose-request": ("reason",),
    "dispose": (),
    "window-move": ("from", "to"),
    "window-minimize": (),
    "sound": ("clip", "op"),
    "mute": ("clip",),
    "rng-draw": ("site", "occurrence", "value", "purpose"),
    "drag": ("op",),
    "hotspot": ("op", "behavior"),
    "error": ("message",),
}
# An rng-draw's site is runtime-specific (a Java line or a host site); the comparer matches draws by order, purpose
# and value, never by site text.
EVENT_COMPARE_KEYS = {k: tuple(x for x in v if not (k == "rng-draw" and x == "site")) for k, v in EVENT_PAYLOAD_KEYS.items()}


class Unjudgeable(Exception):
    def __init__(self, cls, message):
        super().__init__(message)
        self.cls = cls


def load_trace(path_or_lines):
    lines = path_or_lines
    if isinstance(path_or_lines, str):
        with open(path_or_lines, encoding="utf-8") as f:
            lines = [l for l in f.read().splitlines() if l.strip()]
    records = []
    for i, line in enumerate(lines):
        try:
            records.append(json.loads(line) if isinstance(line, str) else line)
        except json.JSONDecodeError as e:
            raise Unjudgeable("missing-field", f"line {i + 1} is not JSON: {e}")
    if not records or records[0].get("type") != "header":
        raise Unjudgeable("missing-field", "the first line is not a header")
    header = records[0]
    if header.get("schema") != SCHEMA_TRACE:
        raise Unjudgeable("missing-field", f"schema is {header.get('schema')!r}, expected {SCHEMA_TRACE}")
    if not header.get("build"):
        raise Unjudgeable("build-witness-missing", f"{header.get('runtime')} trace has no build witness")
    if records[-1].get("type") != "footer":
        raise Unjudgeable("truncated", f"{header.get('runtime')} trace has no footer")
    footer = records[-1]
    status = footer.get("status")
    if status in ("timeout", "aborted", "error"):
        raise Unjudgeable(status if status != "error" else "aborted", f"{header.get('runtime')} run ended with status {status}")
    if status != "complete":
        raise Unjudgeable("missing-field", f"footer status {status!r}")
    states, events = [], []
    for i, r in enumerate(records[1:-1], start=2):
        t = r.get("type")
        if t == "state":
            missing = [k for k in REQUIRED_STATE_FIELDS if k not in r]
            if missing:
                raise Unjudgeable("missing-field", f"{header.get('runtime')} line {i}: state lacks {missing}")
            states.append(r)
        elif t == "event":
            missing = [k for k in REQUIRED_EVENT_FIELDS if k not in r]
            kind = r.get("kind")
            if not missing and kind in EVENT_PAYLOAD_KEYS:
                missing = [k for k in EVENT_PAYLOAD_KEYS[kind] if k not in r]
            if missing or kind not in EVENT_PAYLOAD_KEYS:
                raise Unjudgeable("missing-field", f"{header.get('runtime')} line {i}: event {kind!r} lacks {missing or 'a known kind'}")
            events.append(r)
        else:
            raise Unjudgeable("missing-field", f"line {i}: unknown record type {t!r}")
    return header, states, events, footer


def event_key(e):
    keys = EVENT_COMPARE_KEYS[e["kind"]]
    return (e["tick"], e["sid"], e["kind"]) + tuple(json.dumps(e.get(k), sort_keys=True) for k in keys)


def compare(oracle, host, scenario=None, tolerances=None, policies=None):
    """Returns a report dict: verdict, classes, findings (each with tick/sid/field/oracle/host)."""
    tolerances = tolerances or {}
    policies = set(policies or ())
    report = {"verdict": "exact", "findings": [], "class": None}
    try:
        oh, ostates, oevents, ofoot = load_trace(oracle)
        hh, hstates, hevents, hfoot = load_trace(host)
    except Unjudgeable as u:
        return {"verdict": "unjudgeable", "class": u.cls, "findings": [{"message": str(u)}]}

    sid = (scenario or {}).get("id") or oh.get("scenario", {}).get("id")
    if oh.get("scenario", {}).get("sha256") != hh.get("scenario", {}).get("sha256"):
        return {"verdict": "unjudgeable", "class": "scenario-hash-mismatch", "scenario": sid,
                "findings": [{"message": "the traces are of different scenario files"}]}
    report["scenario"] = sid
    rng = oh.get("rng", {}).get("kind", "none")
    findings = report["findings"]

    def fail(cls, tick, ent, field, a, b, message=None):
        findings.append({"class": cls, "tick": tick, "sid": ent, "field": field, "oracle": a, "host": b,
                         "message": message or f"{sid}: tick {tick}, {ent}, {field}: oracle {a!r} vs host {b!r}"})

    if rng in ("none", "tape"):
        odraws = [e for e in oevents if e["kind"] == "rng-draw"]
        hdraws = [e for e in hevents if e["kind"] == "rng-draw"]
        if rng == "none" and (odraws or hdraws):
            first = (odraws or hdraws)[0]
            fail("rng-unexpected-draw", first["tick"], first["sid"], "rng-draw", len(odraws), len(hdraws),
                 f"{sid}: a directed scenario drew a random number (oracle {len(odraws)}, host {len(hdraws)})")
        elif len(odraws) != len(hdraws):
            fail("rng-draw-count", None, None, "rng-draw", len(odraws), len(hdraws))
        okeys = [event_key(e) for e in oevents]
        hkeys = [event_key(e) for e in hevents]
        for i in range(max(len(okeys), len(hkeys))):
            a = okeys[i] if i < len(okeys) else None
            b = hkeys[i] if i < len(hkeys) else None
            if a != b:
                ea = oevents[i] if a else None
                eb = hevents[i] if b else None
                tick = (ea or eb)["tick"]
                ent = (ea or eb)["sid"]
                if a is None:
                    cls = "extra-event"
                elif b is None:
                    cls = "missing-event"
                elif a not in hkeys[i:] and b in okeys[i + 1:]:
                    cls = "missing-event"   # the host never has the oracle's event, and then the two line up again
                elif b not in okeys[i:] and a in hkeys[i + 1:]:
                    cls = "extra-event"     # the host has one the oracle never has, and then the two line up again
                else:
                    cls = "event-mismatch"
                fail(cls, tick, ent, "event", ea and {k: ea.get(k) for k in ("kind",) + EVENT_PAYLOAD_KEYS[ea["kind"]]},
                     eb and {k: eb.get(k) for k in ("kind",) + EVENT_PAYLOAD_KEYS[eb["kind"]]})
                break   # the first divergence is the finding; later ones are its consequences

        omap = {(s["tick"], s["sid"]): s for s in ostates}
        hmap = {(s["tick"], s["sid"]): s for s in hstates}
        for key in sorted(set(omap) | set(hmap), key=lambda k: (k[0], k[1])):
            a, b = omap.get(key), hmap.get(key)
            if a is None or b is None:
                fail("entity-mismatch", key[0], key[1], "exists", a is not None, b is not None)
                break
            mismatch = None
            for f in EXACT_STATE_FIELDS:
                if a.get(f) != b.get(f):
                    mismatch = (f, a.get(f), b.get(f))
                    break
            if mismatch is None:
                img_a = (a["pose"] or {}).get("imageRight") if a["lookRight"] and (a["pose"] or {}).get("imageRight") else (a["pose"] or {}).get("image")
                img_b = (b["pose"] or {}).get("imageRight") if b["lookRight"] and (b["pose"] or {}).get("imageRight") else (b["pose"] or {}).get("image")
                if img_a != img_b:
                    mismatch = ("pose.image", img_a, img_b)
            if mismatch is None and a["actionAge"] != b["actionAge"]:
                mismatch = ("actionAge", a["actionAge"], b["actionAge"])
            if mismatch is None and a["anchor"] != b["anchor"]:
                tol = tolerances.get("anchor")
                if not (tol is not None and all(abs(x - y) <= tol for x, y in zip(a["anchor"], b["anchor"]))):
                    mismatch = ("anchor", a["anchor"], b["anchor"])
                elif report["verdict"] == "exact":
                    report["verdict"] = "within-tolerance:anchor"
            if mismatch is None and sorted(a["affordances"]) != sorted(b["affordances"]):
                mismatch = ("affordances", a["affordances"], b["affordances"])
            if mismatch:
                fail("state-mismatch", key[0], key[1], mismatch[0], mismatch[1], mismatch[2])
                break
    else:
        # Free-running: time-share of behaviours, and draw counts, with the tolerance printed.
        def share(states):
            total = {}
            for s in states:
                total[s["behavior"]] = total.get(s["behavior"], 0) + 1
            n = max(1, len(states))
            return {k: v / n for k, v in total.items()}
        tol = tolerances.get("timeShare", 0.05)
        so, sh = share(ostates), share(hstates)
        for b in sorted(set(so) | set(sh)):
            if abs(so.get(b, 0) - sh.get(b, 0)) > tol:
                fail("time-share", None, None, f"behavior:{b}", round(so.get(b, 0), 4), round(sh.get(b, 0), 4),
                     f"{sid}: behaviour {b} time-share oracle {so.get(b, 0):.3f} vs host {sh.get(b, 0):.3f} (tolerance {tol})")
        report["tolerance"] = {"timeShare": tol}

    if findings:
        report["verdict"] = "fail"
        report["class"] = findings[0]["class"]
    return report


# ------------------------------------------------------------------ self-test

def _reference():
    h = {"type": "header", "schema": SCHEMA_TRACE, "runtime": "oracle", "scenario": {"id": "S-selftest", "sha256": "abc"},
         "build": {"artifactSha256": "x"}, "rng": {"kind": "none"}, "tickMs": 40, "dpiScale": 1.0, "runId": "r", "startedUtc": "t"}
    st = lambda tick, sid, beh, act, age, x, img="/shime1.png", vis=True, aff=None, target=None, pending=False, set_="Alpha": {
        "type": "state", "tick": tick, "sid": sid, "imageSet": set_, "behavior": beh, "action": act, "actionClass": "Move",
        "actionStack": [act], "refKey": act, "actionAge": age, "pose": {"image": img, "imageRight": None, "index": 0},
        "visible": vis, "anchor": [x, 680], "origin": [x - 32, 552], "lookRight": False, "velocity": [-2, 0],
        "affordances": aff or [], "target": target, "dragging": False, "pending": pending, "vars": {}}
    lines = [h,
             st(0, "m1", "Walk", "Walk", 0, 640),
             {"type": "event", "tick": 1, "sid": "m1", "kind": "birth-request", "child": "m1.b1", "imageSet": "Ball",
              "behavior": "Roll", "anchor": [638, 680], "lookRight": False, "ordinal": 1},
             st(1, "m1", "Walk", "Walk", 1, 638),
             st(1, "m1.b1", "Roll", "Roll", 0, 638, img="/ball1.png", pending=True, set_="Ball"),
             {"type": "event", "tick": 2, "sid": "m1.b1", "kind": "birth-activate", "child": "m1.b1"},
             {"type": "event", "tick": 2, "sid": "m1", "kind": "transform", "fromImageSet": "Alpha", "toImageSet": "Beta", "behavior": "Stand"},
             st(2, "m1", "Stand", "Stand", 0, 636, set_="Beta"),
             st(2, "m1.b1", "Roll", "Roll", 1, 640, img="/ball2.png", set_="Ball"),
             {"type": "footer", "ticks": 2, "status": "complete", "draws": 0}]
    host = copy.deepcopy(lines)
    host[0] = dict(host[0], runtime="host", build={"commit": "c", "exeSha256": "e"})
    return lines, host


def _mutants(oracle, host):
    def m(desc, cls, fn):
        h = copy.deepcopy(host)
        fn(h)
        return desc, cls, h

    def drop(h, pred):
        h[:] = [r for r in h if not pred(r)]

    return [
        m("missing spawn", "missing-event", lambda h: drop(h, lambda r: r.get("kind") == "birth-request")),
        m("late transform", "event-mismatch", lambda h: [r.update(tick=3) for r in h if r.get("kind") == "transform"]),
        m("wrong partner / entity", "event-mismatch", lambda h: [r.update(child="m1.b2") for r in h if r.get("kind") == "birth-activate"]),
        m("same position, wrong image", "state-mismatch", lambda h: [r["pose"].update(image="/shime9.png") for r in h if r.get("type") == "state" and r["tick"] == 1 and r["sid"] == "m1"]),
        m("same position, wrong visibility", "state-mismatch", lambda h: [r.update(visible=False) for r in h if r.get("type") == "state" and r["tick"] == 2 and r["sid"] == "m1"]),
        m("wrong action age", "state-mismatch", lambda h: [r.update(actionAge=5) for r in h if r.get("type") == "state" and r["tick"] == 1 and r["sid"] == "m1"]),
        m("extra event", "extra-event", lambda h: h.insert(-1, {"type": "event", "tick": 2, "sid": "m1", "kind": "dispose"})),
        m("early end (missing ticks)", "entity-mismatch", lambda h: drop(h, lambda r: r.get("type") == "state" and r["tick"] == 2 and r["sid"] == "m1.b1")),
        m("wrong RNG use in a directed run", "rng-unexpected-draw", lambda h: h.insert(2, {"type": "event", "tick": 1, "sid": "m1", "kind": "rng-draw", "site": "x", "occurrence": 1, "value": 0.5, "purpose": "behavior-selection"})),
        m("missing trace field", "missing-field", lambda h: [r.pop("actionAge") for r in h if r.get("type") == "state"][:1]),
        m("truncated trace", "truncated", lambda h: h.pop()),
        m("missing build witness", "build-witness-missing", lambda h: h[0].pop("build")),
        m("different scenario", "scenario-hash-mismatch", lambda h: h[0]["scenario"].update(sha256="zzz")),
        m("aborted run", "aborted", lambda h: h[-1].update(status="aborted")),
        m("timed-out run", "timeout", lambda h: h[-1].update(status="timeout")),
        m("anchor off by one", "state-mismatch", lambda h: [r.update(anchor=[r["anchor"][0] + 1, r["anchor"][1]]) for r in h if r.get("type") == "state" and r["tick"] == 2 and r["sid"] == "m1"]),
        m("wrong affordance", "state-mismatch", lambda h: [r.update(affordances=["hug"]) for r in h if r.get("type") == "state" and r["tick"] == 0]),
    ]


def self_test():
    oracle, host = _reference()
    ok = True
    base = compare(oracle, host)
    print(f"  {'baseline (identical behaviour)':42} {base['verdict']}")
    if base["verdict"] != "exact":
        ok = False
        print("    FAIL the baseline must be exact:", base["findings"][:1])
    for desc, cls, h in _mutants(oracle, host):
        r = compare(oracle, h)
        got = r.get("class")
        good = r["verdict"] in ("fail", "unjudgeable") and got == cls
        ok &= good
        msg = (r.get("findings") or [{}])[0].get("message", "")
        print(f"  {desc:42} {'FIRED' if good else 'SURVIVED'}  ({got}){'' if good else '  expected ' + cls}  {msg[:110]}")
    tol = copy.deepcopy(host)
    [r.update(anchor=[r["anchor"][0] + 1, r["anchor"][1]]) for r in tol if r.get("type") == "state" and r["tick"] == 2 and r["sid"] == "m1"]
    rt = compare(oracle, tol, tolerances={"anchor": 1})
    good = rt["verdict"] == "within-tolerance:anchor"
    ok &= good
    print(f"  {'a declared anchor tolerance is named':42} {'OK' if good else 'WRONG'}  ({rt['verdict']})")
    print("SELF-TEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if len(argv) < 3:
        print(__doc__)
        return 2
    scenario = None
    if "--scenario" in argv:
        with open(argv[argv.index("--scenario") + 1], encoding="utf-8") as f:
            scenario = json.load(f)
    tolerances = (scenario or {}).get("tolerances", {})
    r = compare(argv[1], argv[2], scenario, tolerances)
    if "--json" in argv:
        with open(argv[argv.index("--json") + 1], "w", encoding="utf-8") as f:
            json.dump(r, f, indent=1)
    print(f"{r.get('scenario', '?')}: {r['verdict']}" + (f" ({r['class']})" if r.get("class") else ""))
    for fnd in r["findings"][:5]:
        print("  " + fnd.get("message", str(fnd)))
    return 0 if r["verdict"] in ("exact",) or r["verdict"].startswith("within-tolerance") else (2 if r["verdict"] == "unjudgeable" else 1)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
