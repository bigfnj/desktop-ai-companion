"""Shimeji parity comparer (CONTRACT.md v1, sections 3 and 6).

    compare.py <oracle trace.jsonl> <host trace.jsonl> [--scenario scenario.json] [--oracle-artifact <jar>]
               [--host-exe <exe>] [--json out.json]
    compare.py --self-test

Reads two traces of the same scenario and decides whether the runtimes behaved the same. Each trace is checked
first (fields, build witness, counts, ordering, lineage, velocity and refKey consistency); a trace that fails a
check is unjudgeable and nothing is compared. A directed scenario (rng kind none or tape) must then match EXACTLY on
events and on every state field; a free-running one (splitmix64) is a separate claim and is reported as such. Every
failure names the scenario, the first mismatching tick, the entity and the field. Exit 0 = parity (exact, or only
declared tolerances), 1 = a mismatch, 2 = a trace or scenario that cannot be judged.

--self-test builds reference traces and a set of deliberately wrong ones, and requires each wrong one to fail with
its expected class (and each positive control to stay exact); it is the proof that this comparer can fail.
"""
import copy
import hashlib
import json
import os
import re
import stat
import sys

SCHEMA_TRACE = "shimeji-parity-trace/1"
SCHEMA_SCENARIO = "shimeji-parity-scenario/1"
HEX64 = re.compile(r"^[0-9a-f]{64}$")

STATE_FIELDS = ("tick", "sid", "imageSet", "behavior", "action", "actionClass", "actionStack", "refKey", "actionAge",
                "pose", "poseAnchor", "visible", "anchor", "origin", "lookRight", "velocity", "affordances", "target",
                "dragging", "pending", "disposing", "vars")
NON_NULL_STATE = {"tick": int, "sid": str, "imageSet": str, "actionStack": list, "visible": bool, "anchor": list,
                  "lookRight": bool, "velocity": list, "affordances": list, "dragging": bool, "pending": bool,
                  "disposing": bool, "vars": dict}
# Compared exactly. anchor and origin (each with its own named tolerance) and velocity (checked against the anchor inside
# each trace, so comparing anchors compares it) are handled apart. poseAnchor is exact: both runtimes draw the source's
# image unpadded at its top-left (per-pose geometry), so the anchor offset is the source's effective anchor on both.
EXACT_STATE_FIELDS = ("imageSet", "behavior", "action", "actionClass", "actionStack", "refKey", "actionAge", "pose",
                      "poseAnchor", "visible", "lookRight", "affordances", "target", "dragging", "pending", "disposing",
                      "vars")
EVENT_PAYLOAD_KEYS = {
    "behavior": ("from", "to", "cause"),
    "transform": ("fromImageSet", "toImageSet", "behavior"),
    "birth-request": ("child", "imageSet", "behavior", "anchor", "lookRight", "ordinal", "attempt"),
    "birth-activate": ("child",),
    "birth-refused": ("child", "attempt", "reason"),
    "scan-target": ("affordance", "target"),
    "interact": ("with",),
    "dispose-request": ("reason",),
    "dispose": (),
    "window-move": ("from", "to"),
    "window-minimize": (),
    "sound": ("clip", "op"),
    "mute": ("clip",),
    # `purpose` is written but informational: each runtime words it its own way, so it is never compared.
    "rng-draw": ("site", "occurrence", "value"),
    "drag": ("op",),
    "hotspot": ("op", "behavior"),
    "error": ("message",),
}
BUILD_REQUIRED = {
    "oracle": {"sourceZipSha256": "hex", "patchSha256": "hex", "jdk": str, "deps": dict, "artifactSha256": "hex"},
    "host": {"commit": str, "exeSha256": "hex", "engineSha256": "hex", "converterSha256": "hex"},
}
ARTIFACT_FIELD = {"oracle": "artifactSha256", "host": "exeSha256"}
BUTTON_EDGES = ("press", "release", "click")


class Unjudgeable(Exception):
    def __init__(self, cls, message):
        super().__init__(message)
        self.cls = cls


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def _is_link(path):
    """A symlink, junction or any other reparse point: something that can lead outside the pack."""
    if os.path.islink(path):
        return True
    try:
        attrs = getattr(os.lstat(path), "st_file_attributes", 0)
    except OSError:
        return True
    return bool(attrs & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def pack_preflight(pack_dir):
    """CONTRACT section 3: before any file is opened, refuse a link anywhere in the pack, the directory itself included.
    Only a link (a symlink, junction or mount point: all reparse points) can lead a path outside the pack, and a hard
    link resolves inside it, so the link test is the whole rule. Returns the files to hash, as (relative path, full path)."""
    if not os.path.isdir(pack_dir) or _is_link(pack_dir):
        raise Unjudgeable("pack-invalid", f"the pack {pack_dir} is not a plain directory")
    files = []
    for root, dirs, names in os.walk(pack_dir, followlinks=False):
        for name in dirs + names:
            full = os.path.join(root, name)
            if _is_link(full):
                raise Unjudgeable("pack-invalid", f"the pack holds a link: {os.path.relpath(full, pack_dir)}")
        for name in names:
            full = os.path.join(root, name)
            files.append((os.path.relpath(full, pack_dir).replace(os.sep, "/"), full))
    return files


def pack_manifest(pack_dir):
    """CONTRACT section 3: '<sha256>  <relative path>\\n' per file, sorted by the paths' UTF-8 bytes. Nothing is opened
    until the whole pack has passed the preflight."""
    rows = []
    for rel, full in pack_preflight(pack_dir):
        with open(full, "rb") as f:
            rows.append((rel, sha256_bytes(f.read())))
    rows.sort(key=lambda r: r[0].encode("utf-8"))
    return "".join(f"{h}  {rel}\n" for rel, h in rows)


def pack_sha256(pack_dir):
    return sha256_bytes(pack_manifest(pack_dir).encode("utf-8"))


def _is_point(v):
    return isinstance(v, list) and len(v) == 2 and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in v)


def validate_scenario(s):
    """Raises Unjudgeable('scenario-invalid') when the scenario breaks CONTRACT section 1."""
    def bad(msg):
        raise Unjudgeable("scenario-invalid", f"scenario {s.get('id') if isinstance(s, dict) else '?'}: {msg}")
    if not isinstance(s, dict) or s.get("schema") != SCHEMA_SCENARIO:
        bad(f"schema is not {SCHEMA_SCENARIO}")
    for k in ("id", "pack", "screens", "workArea", "dpiScale", "ticks", "rng", "settings", "mascots"):
        if k not in s:
            bad(f"lacks {k!r}")
    if not isinstance(s["ticks"], int) or s["ticks"] < 0:
        bad("ticks is not a non-negative integer")
    if not isinstance(s["rng"], dict) or s["rng"].get("kind") not in ("none", "tape", "splitmix64"):
        bad("rng.kind is not none, tape or splitmix64")
    sids = [m.get("sid") for m in s["mascots"]]
    if not sids or len(set(sids)) != len(sids) or not all(isinstance(x, str) and x for x in sids):
        bad("mascots need one or more unique sids")
    for m in s["mascots"]:
        if not isinstance(m.get("behavior"), str) or not m.get("behavior"):
            bad(f"start companion {m.get('sid')!r} names no behavior (a directed start runs no selection draw)")
    for timeline in ("input", "activeWindow"):
        last = -1
        for e in s.get(timeline, []):
            t = e.get("tick")
            if not isinstance(t, int) or t < 0 or t > s["ticks"] or t < last:
                bad(f"{timeline} entry tick {t!r} is out of order or outside 0..{s['ticks']}")
            last = t
            if timeline == "input" and t == 0 and any(k in e for k in BUTTON_EDGES):
                bad("a tick-0 press, release or click (button edges start at tick 1)")


def load_trace(source, expected_runtime=None):
    lines = source
    if isinstance(source, str):
        with open(source, encoding="utf-8") as f:
            lines = [l for l in f.read().splitlines() if l.strip()]
    records = []
    for i, line in enumerate(lines):
        try:
            records.append(json.loads(line) if isinstance(line, str) else line)
        except json.JSONDecodeError as e:
            raise Unjudgeable("missing-field", f"line {i + 1} is not JSON: {e}")
    if not records or not isinstance(records[0], dict) or records[0].get("type") != "header":
        raise Unjudgeable("missing-field", "the first line is not a header")
    header = records[0]
    if header.get("schema") != SCHEMA_TRACE:
        raise Unjudgeable("missing-field", f"schema is {header.get('schema')!r}, expected {SCHEMA_TRACE}")
    runtime = header.get("runtime")
    if runtime not in BUILD_REQUIRED:
        raise Unjudgeable("missing-field", f"header runtime {runtime!r} is not oracle or host")
    if expected_runtime and runtime != expected_runtime:
        raise Unjudgeable("runtime-mismatch", f"the {expected_runtime} trace is labelled {runtime!r}")
    sc = header.get("scenario")
    if not isinstance(sc, dict) or not isinstance(sc.get("id"), str) or not sc.get("id") \
            or not HEX64.match(str(sc.get("sha256"))) or not HEX64.match(str(sc.get("packSha256"))):
        raise Unjudgeable("missing-field", f"{runtime} header scenario needs id, sha256 and packSha256 (64 hex)")
    build = header.get("build")
    if not isinstance(build, dict):
        raise Unjudgeable("build-witness-missing", f"{runtime} trace has no build witness")
    for k, kind in BUILD_REQUIRED[runtime].items():
        v = build.get(k)
        ok = HEX64.match(v) is not None if kind == "hex" and isinstance(v, str) else (
            kind != "hex" and isinstance(v, kind) and bool(v))
        if not ok:
            raise Unjudgeable("build-witness-missing", f"{runtime} build witness field {k!r} is missing or malformed ({v!r})")
    if runtime == "oracle":
        deps = build["deps"]
        if not all(isinstance(n, str) and n and isinstance(h, str) and HEX64.match(h) for n, h in deps.items()):
            raise Unjudgeable("build-witness-missing", f"oracle build deps must map names to 64-hex hashes ({deps!r})")
    rng_spec = header.get("rng")
    if not isinstance(rng_spec, dict) or rng_spec.get("kind") not in ("none", "tape", "splitmix64"):
        raise Unjudgeable("missing-field", f"{runtime} header rng.kind is missing")
    if rng_spec["kind"] == "tape" and not (isinstance(rng_spec.get("values"), list) and all(
            isinstance(v, (int, float)) and not isinstance(v, bool) and 0 <= v < 1 for v in rng_spec["values"])):
        raise Unjudgeable("missing-field", f"{runtime} header rng tape needs values, each in [0, 1)")
    if header.get("tickMs") != 40 or not isinstance(header.get("dpiScale"), (int, float)) or not header.get("runId") \
            or not isinstance(header.get("startedUtc"), str) or not header.get("startedUtc"):
        raise Unjudgeable("missing-field", f"{runtime} header needs tickMs 40, dpiScale, runId and startedUtc")

    if not isinstance(records[-1], dict) or records[-1].get("type") != "footer" or len(records) < 2:
        raise Unjudgeable("truncated", f"{runtime} trace has no footer")
    footer = records[-1]
    status = footer.get("status")
    if status in ("timeout", "aborted", "error"):
        raise Unjudgeable(status if status != "error" else "aborted", f"{runtime} run ended with status {status}")
    if status != "complete":
        raise Unjudgeable("missing-field", f"{runtime} footer status {status!r}")
    for k in ("ticks", "draws", "states", "events"):
        if not isinstance(footer.get(k), int) or isinstance(footer.get(k), bool) or footer[k] < 0:
            raise Unjudgeable("missing-field", f"{runtime} footer {k!r} is missing")
    n = footer["ticks"]

    def malformed(msg):
        raise Unjudgeable("malformed", f"{runtime}: {msg}")

    states, events = [], []
    last_tick = 0
    for i, r in enumerate(records[1:-1], start=2):
        if not isinstance(r, dict):
            malformed(f"line {i} is not an object")
        t = r.get("type")
        if t == "state":
            missing = [k for k in STATE_FIELDS if k not in r]
            if missing:
                raise Unjudgeable("missing-field", f"{runtime} line {i}: state lacks {missing}")
            for k, typ in NON_NULL_STATE.items():
                if not isinstance(r[k], typ) or (typ is int and isinstance(r[k], bool)):
                    raise Unjudgeable("missing-field", f"{runtime} line {i}: state {k!r} is {r[k]!r}")
            if not _is_point(r["anchor"]) or not _is_point(r["velocity"]):
                raise Unjudgeable("missing-field", f"{runtime} line {i}: anchor and velocity are [x, y]")
            states.append(r)
        elif t == "event":
            kind = r.get("kind")
            missing = [k for k in ("tick", "sid", "kind") if k not in r]
            if not missing and kind in EVENT_PAYLOAD_KEYS:
                missing = [k for k in EVENT_PAYLOAD_KEYS[kind] if k not in r]
            if missing or kind not in EVENT_PAYLOAD_KEYS or not isinstance(r.get("tick"), int):
                raise Unjudgeable("missing-field", f"{runtime} line {i}: event {kind!r} lacks {missing or 'a known kind'}")
            events.append(r)
        else:
            malformed(f"line {i}: unknown record type {t!r}")
        tick = r["tick"]
        if tick < 0 or tick > n:
            malformed(f"line {i}: tick {tick} is outside 0..{n}")
        if tick < last_tick:
            malformed(f"line {i}: tick {tick} comes after tick {last_tick}")
        last_tick = tick

    draws = [e for e in events if e["kind"] == "rng-draw"]
    if (len(states), len(events), len(draws)) != (footer["states"], footer["events"], footer["draws"]):
        malformed(f"footer counts states/events/draws {footer['states']}/{footer['events']}/{footer['draws']} "
                  f"but the trace has {len(states)}/{len(events)}/{len(draws)}")
    for k, e in enumerate(draws, start=1):
        if e["occurrence"] != k:
            malformed(f"tick {e['tick']}: rng-draw occurrence {e['occurrence']!r} should be {k}")
    # The numbers must be the scenario's: a value outside [0, 1), or a tape value the tape does not hold at that
    # occurrence, means the run did not take its numbers as declared. Drawing past the tape is behaviour (compare()).
    tape = rng_spec.get("values") if rng_spec["kind"] == "tape" else None
    for e in draws:
        v = e["value"]
        if not isinstance(v, (int, float)) or isinstance(v, bool) or not 0 <= v < 1:
            raise Unjudgeable("rng-invalid", f"{runtime}: tick {e['tick']}: rng-draw value {v!r} is outside [0, 1)")
        k = e["occurrence"]
        if tape is not None and k <= len(tape) and v != tape[k - 1]:
            raise Unjudgeable("rng-invalid", f"{runtime}: tick {e['tick']}: draw {k} recorded {v!r}, the tape holds {tape[k - 1]!r}")
    seen = set()
    by_sid = {}
    for s in states:
        key = (s["tick"], s["sid"])
        if key in seen:
            malformed(f"two state lines for tick {key[0]}, {key[1]}")
        seen.add(key)
        by_sid.setdefault(s["sid"], []).append(s)
        stack = s["actionStack"]
        if (stack[-1] if stack else None) != s["refKey"]:
            malformed(f"tick {s['tick']}, {s['sid']}: refKey {s['refKey']!r} is not the last actionStack entry")
    if not any(s["tick"] == 0 for s in states):
        malformed("there is no state at tick 0")
    disposed = {(e["sid"], e["tick"]) for e in events if e["kind"] == "dispose"}
    for sid_, rows in by_sid.items():
        ticks = [r["tick"] for r in rows]
        if ticks != list(range(ticks[0], ticks[-1] + 1)):
            malformed(f"{sid_}'s state lines have a gap ({ticks[0]}..{ticks[-1]}, {len(ticks)} lines)")
        if ticks[-1] < n and (sid_, ticks[-1] + 1) not in disposed:
            malformed(f"{sid_} stops at tick {ticks[-1]} of {n} with no dispose event at tick {ticks[-1] + 1}")
        prev = None
        for r in rows:
            want = [0, 0] if prev is None else [r["anchor"][0] - prev["anchor"][0], r["anchor"][1] - prev["anchor"][1]]
            if r["velocity"] != want:
                malformed(f"tick {r['tick']}, {sid_}: velocity {r['velocity']} is not the anchor change {want}")
            prev = r
    return header, states, events, footer


def event_key(e):
    return (e["tick"], e["sid"], e["kind"]) + tuple(json.dumps(e.get(k), sort_keys=True) for k in EVENT_PAYLOAD_KEYS[e["kind"]])


def compare(oracle, host, scenario=None, scenario_sha=None, pack_sha=None, artifacts=None, tolerances=None):
    """Returns a report dict: verdict, class, findings (each with tick/sid/field/oracle/host)."""
    tolerances = tolerances or {}
    artifacts = artifacts or {}
    try:
        if scenario is not None:
            validate_scenario(scenario)
        oh, ostates, oevents, ofoot = load_trace(oracle, "oracle")
        hh, hstates, hevents, hfoot = load_trace(host, "host")
        sid = (scenario or {}).get("id") or oh["scenario"]["id"]
        for runtime, h in (("oracle", oh), ("host", hh)):
            if scenario_sha is not None and h["scenario"]["sha256"] != scenario_sha:
                raise Unjudgeable("scenario-hash-mismatch", f"the {runtime} trace is not of this scenario file")
            if pack_sha is not None and h["scenario"]["packSha256"] != pack_sha:
                raise Unjudgeable("pack-hash-mismatch", f"the {runtime} trace ran another version of the pack")
            if runtime in artifacts and h["build"][ARTIFACT_FIELD[runtime]] != artifacts[runtime]:
                raise Unjudgeable("artifact-hash-mismatch", f"the {runtime} artefact given is not the one the trace names")
        if oh["scenario"]["sha256"] != hh["scenario"]["sha256"] or oh["scenario"]["id"] != hh["scenario"]["id"]:
            raise Unjudgeable("scenario-hash-mismatch", "the traces are of different scenario files")
        if oh["scenario"]["packSha256"] != hh["scenario"]["packSha256"]:
            raise Unjudgeable("pack-hash-mismatch", "the traces ran different versions of the pack")
        if oh["rng"] != hh["rng"] or (scenario is not None and scenario["rng"] != oh["rng"]):
            raise Unjudgeable("scenario-hash-mismatch", "the traces were run with different rng specs")
        if ofoot["ticks"] != hfoot["ticks"] or (scenario is not None and scenario["ticks"] != ofoot["ticks"]):
            raise Unjudgeable("tick-count-mismatch", f"oracle ran {ofoot['ticks']} steps, host {hfoot['ticks']}"
                              + (f", scenario {scenario['ticks']}" if scenario is not None else ""))
    except Unjudgeable as u:
        return {"verdict": "unjudgeable", "class": u.cls, "findings": [{"message": str(u)}]}

    report = {"verdict": "exact", "findings": [], "class": None, "scenario": sid,
              "draws": {"oracle": ofoot["draws"], "host": hfoot["draws"]}}
    rng = oh["rng"]["kind"]
    findings = report["findings"]

    def fail(cls, tick, ent, field, a, b, message=None):
        findings.append({"class": cls, "tick": tick, "sid": ent, "field": field, "oracle": a, "host": b,
                         "message": message or f"{sid}: tick {tick}, {ent}, {field}: oracle {a!r} vs host {b!r}"})

    if rng in ("none", "tape"):
        odraws = [e for e in oevents if e["kind"] == "rng-draw"]
        hdraws = [e for e in hevents if e["kind"] == "rng-draw"]
        tape_len = len(oh["rng"]["values"]) if rng == "tape" else None
        if rng == "none" and (odraws or hdraws):
            first = (odraws or hdraws)[0]
            fail("rng-unexpected-draw", first["tick"], first["sid"], "rng-draw", len(odraws), len(hdraws),
                 f"{sid}: a directed scenario drew a random number (oracle {len(odraws)}, host {len(hdraws)})")
        elif rng == "tape" and max(len(odraws), len(hdraws)) > tape_len:
            first = (odraws if len(odraws) > tape_len else hdraws)[tape_len]
            fail("rng-tape-exhausted", first["tick"], first["sid"], "rng-draw", len(odraws), len(hdraws),
                 f"{sid}: a run drew past the tape's {tape_len} value(s) (oracle {len(odraws)}, host {len(hdraws)})")
        elif len(odraws) != len(hdraws):
            fail("rng-draw-count", None, None, "rng-draw", len(odraws), len(hdraws))
        if not findings:
            okeys = [event_key(e) for e in oevents]
            hkeys = [event_key(e) for e in hevents]
            for i in range(max(len(okeys), len(hkeys))):
                a = okeys[i] if i < len(okeys) else None
                b = hkeys[i] if i < len(hkeys) else None
                if a == b:
                    continue
                ea = oevents[i] if a else None
                eb = hevents[i] if b else None
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
                pick = lambda e: e and {k: e.get(k) for k in ("kind",) + EVENT_PAYLOAD_KEYS[e["kind"]]}
                fail(cls, (ea or eb)["tick"], (ea or eb)["sid"], "event", pick(ea), pick(eb))
                break   # the first divergence is the finding; later ones are its consequences

        if not findings:
            def by_tick(states):
                out = {}
                for s in states:
                    out.setdefault(s["tick"], []).append(s)
                return out
            ot, ht = by_tick(ostates), by_tick(hstates)
            for tick in range(0, ofoot["ticks"] + 1):
                oa, hb = ot.get(tick, []), ht.get(tick, [])
                osids, hsids = [s["sid"] for s in oa], [s["sid"] for s in hb]
                if osids != hsids:
                    fail("entity-mismatch", tick, None, "companions in manager order", osids, hsids)
                    break
                mismatch = None
                for a, b in zip(oa, hb):
                    for f in EXACT_STATE_FIELDS:
                        if a[f] != b[f]:
                            mismatch = (a["sid"], f, a[f], b[f])
                            break
                    if mismatch is None and a["anchor"] != b["anchor"]:
                        tol = tolerances.get("anchor")
                        if tol is not None and all(abs(x - y) <= tol for x, y in zip(a["anchor"], b["anchor"])):
                            report["verdict"] = "within-tolerance:anchor"
                        else:
                            mismatch = (a["sid"], "anchor", a["anchor"], b["anchor"])
                    # The drawn image's top-left, on its own: a sum with poseAnchor would pass an image drawn somewhere
                    # else whose anchor offset moved the other way by the same amount (a rendered shift).
                    if mismatch is None and a["origin"] != b["origin"]:
                        tol = tolerances.get("origin")
                        if (tol is not None and isinstance(a["origin"], list) and isinstance(b["origin"], list)
                                and all(abs(x - y) <= tol for x, y in zip(a["origin"], b["origin"]))):
                            report["verdict"] = "within-tolerance:origin"
                        else:
                            mismatch = (a["sid"], "origin", a["origin"], b["origin"])
                    if mismatch:
                        break
                if mismatch:
                    fail("state-mismatch", tick, *mismatch)
                    break
    else:
        # Free-running: time-share of behaviours, with the tolerance and both draw counts printed.
        def share(states):
            total = {}
            for s in states:
                total[s["behavior"]] = total.get(s["behavior"], 0) + 1
            n = max(1, len(states))
            return {k: v / n for k, v in total.items()}
        tol = tolerances.get("timeShare", 0.05)
        so, sh = share(ostates), share(hstates)
        for b in sorted(set(so) | set(sh), key=str):
            if abs(so.get(b, 0) - sh.get(b, 0)) > tol:
                fail("time-share", None, None, f"behavior:{b}", round(so.get(b, 0), 4), round(sh.get(b, 0), 4),
                     f"{sid}: behaviour {b} time-share oracle {so.get(b, 0):.3f} vs host {sh.get(b, 0):.3f} "
                     f"(tolerance {tol}; draws oracle {ofoot['draws']}, host {hfoot['draws']})")
        report["tolerance"] = {"timeShare": tol}
        # A weaker claim than parity: never `exact`, whatever the state lines happen to show.
        report["verdict"] = "time-share-pass"

    if findings:
        report["verdict"] = "fail"
        report["class"] = findings[0]["class"]
    return report


# ------------------------------------------------------------------ self-test

H = lambda c: c * 64   # a well-formed sha256 made of one hex digit
REF = lambda n: f"Alpha|conf/actions.xml|/Mascot[1]/ActionList[1]/Action[{n}]"
BEH = "Alpha|conf/behaviors.xml|/Mascot[1]/BehaviorList[1]/Behavior[1]"


def _state(tick, sid, beh, act, age, x, prev_x=None, img="/shime1.png", vis=True, aff=None, target=None,
           pending=False, disposing=False, set_="Alpha", ref=1, cls="Move"):
    stack = [BEH, REF(ref)]
    return {"type": "state", "tick": tick, "sid": sid, "imageSet": set_, "behavior": beh, "action": act,
            "actionClass": cls, "actionStack": stack, "refKey": stack[-1], "actionAge": age,
            "pose": {"image": img, "imageRight": None, "index": 0}, "poseAnchor": [32, 128], "visible": vis,
            "anchor": [x, 680], "origin": [x - 32, 552], "lookRight": False,
            "velocity": [0, 0] if prev_x is None else [x - prev_x, 0], "affordances": aff or [], "target": target,
            "dragging": False, "pending": pending, "disposing": disposing, "vars": {}}


def _ev(tick, sid, kind, **payload):
    return dict({"type": "event", "tick": tick, "sid": sid, "kind": kind}, **payload)


def _refoot(lines):
    body = lines[1:-1]
    lines[-1].update(states=sum(1 for r in body if r.get("type") == "state"),
                     events=sum(1 for r in body if r.get("type") == "event"),
                     draws=sum(1 for r in body if r.get("kind") == "rng-draw"))


def _reference(rng=None):
    """A breed, a transform and a self-destruct over four steps; with rng given, one behaviour draw at tick 1."""
    rng = rng or {"kind": "none"}
    h = {"type": "header", "schema": SCHEMA_TRACE, "runtime": "oracle",
         "scenario": {"id": "S-selftest", "sha256": H("a"), "packSha256": H("b")},
         "build": {"sourceZipSha256": H("1"), "patchSha256": H("2"), "jdk": "1.8.0_504", "deps": {"jna.jar": H("3")},
                   "artifactSha256": H("4")},
         "rng": rng, "tickMs": 40, "dpiScale": 1.0, "runId": "r", "startedUtc": "t"}
    lines = [h,
             _state(0, "m1", "Walk", "Walk", 0, 640),
             _ev(1, "m1", "birth-request", child="m1.b1", imageSet="Ball", behavior="Roll", anchor=[638, 680],
                 lookRight=False, ordinal=1, attempt=1),
             _state(1, "m1", "Walk", "Walk", 1, 638, 640),
             _state(1, "m1.b1", "Roll", "Roll", 0, 638, img="/ball1.png", pending=True, set_="Ball", ref=7),
             _ev(2, "m1.b1", "birth-activate", child="m1.b1"),
             _ev(2, "m1", "transform", fromImageSet="Alpha", toImageSet="Beta", behavior="Stand"),
             _state(2, "m1", "Stand", "Stand", 0, 636, 638, set_="Beta", ref=2, cls="Stay"),
             _state(2, "m1.b1", "Roll", "Roll", 1, 640, 638, img="/ball2.png", set_="Ball", ref=7),
             _ev(3, "m1.b1", "dispose-request", reason="self-destruct"),
             _state(3, "m1", "Stand", "Stand", 1, 636, 636, set_="Beta", ref=2, cls="Stay"),
             _state(3, "m1.b1", "Roll", "Roll", 2, 640, 640, img="/ball2.png", vis=False, disposing=True, set_="Ball", ref=7),
             _ev(4, "m1.b1", "dispose"),
             _state(4, "m1", "Stand", "Stand", 2, 636, 636, set_="Beta", ref=2, cls="Stay"),
             {"type": "footer", "ticks": 4, "status": "complete", "draws": 0, "states": 0, "events": 0}]
    if rng["kind"] != "none":
        lines.insert(2, _ev(1, "m1", "rng-draw", site="builtin:behavior-select", occurrence=1, value=0.5,
                            purpose="behavior-selection"))
    _refoot(lines)
    host = copy.deepcopy(lines)
    host[0] = dict(host[0], runtime="host",
                   build={"commit": "c0ffee", "exeSha256": H("5"), "engineSha256": H("6"), "converterSha256": H("7")})
    return lines, host


def _resort(lines):
    """Keep a mutated trace well-formed: by tick, a tick's events before its states, otherwise stable."""
    body = sorted(lines[1:-1], key=lambda r: (r["tick"], 0 if r.get("type") == "event" else 1))
    lines[1:-1] = body


def _where(h, tick, sid):
    return [r for r in h if r.get("type") == "state" and r["tick"] == tick and r["sid"] == sid]


def _revelocity(lines):
    prev = {}
    for r in lines:
        if r.get("type") == "state":
            p = prev.get(r["sid"])
            r["velocity"] = [0, 0] if p is None else [r["anchor"][0] - p[0], r["anchor"][1] - p[1]]
            prev[r["sid"]] = r["anchor"]


def _cases():
    """(description, expected class or 'exact', mutate(oracle, host) -> kwargs or None, keep footers as mutated)."""
    def host_only(fn, refoot=True, resort=True, revel=False):
        def run(o, h):
            fn(h)
            if resort: _resort(h)
            if revel: _revelocity(h)
            if refoot: _refoot(h)
        return run

    def both(fn, refoot=True):
        def run(o, h):
            for t in (o, h):
                fn(t)
                if refoot: _refoot(t)
        return run

    def drop(pred):
        return lambda h: h.__setitem__(slice(None), [r for r in h if not pred(r)])

    def extra_m2(h):
        for t in range(0, 5):
            h.append(dict(_state(t, "m2", "Sit", "Sit", t, 100, None if t == 0 else 100), tick=t))
        footer = [r for r in h if r.get("type") == "footer"][0]
        h.remove(footer)
        h.append(footer)

    def swap_order(h):
        a, b = _where(h, 2, "m1")[0], _where(h, 2, "m1.b1")[0]
        i, j = h.index(a), h.index(b)
        h[i], h[j] = h[j], h[i]

    def dup_row(h):
        r = _where(h, 2, "m1")[0]
        h.insert(h.index(r) + 1, copy.deepcopy(r))

    def strip_all(t):
        t[1:-1] = []

    def stop_early(h):
        drop(lambda r: r.get("tick") == 4 and r.get("type") in ("state", "event"))(h)
        h[-1]["ticks"] = 3

    return [
        # behaviour differences
        ("missing spawn", "missing-event", host_only(drop(lambda r: r.get("kind") == "birth-request"))),
        ("late transform", "event-mismatch", host_only(lambda h: [r.update(tick=3) for r in h if r.get("kind") == "transform"])),
        ("wrong partner / entity", "event-mismatch", host_only(lambda h: [r.update(child="m1.b2") for r in h if r.get("kind") == "birth-activate"])),
        ("extra event", "extra-event", host_only(lambda h: h.insert(-1, _ev(2, "m1", "error", message="x")))),
        ("same position, wrong image", "state-mismatch", host_only(lambda h: _where(h, 1, "m1")[0]["pose"].update(image="/shime9.png"))),
        ("same position, wrong visibility", "state-mismatch", host_only(lambda h: _where(h, 2, "m1")[0].update(visible=False))),
        ("wrong action age", "state-mismatch", host_only(lambda h: _where(h, 1, "m1")[0].update(actionAge=5))),
        ("wrong action class", "state-mismatch", host_only(lambda h: _where(h, 2, "m1")[0].update(actionClass="Move"))),
        ("wrong actionStack and refKey, consistently", "state-mismatch",
         host_only(lambda h: _where(h, 2, "m1")[0].update(actionStack=[BEH, REF(9)], refKey=REF(9)))),
        ("wrong vars", "state-mismatch", host_only(lambda h: _where(h, 3, "m1")[0].update(vars={"mood": 1}))),
        ("wrong disposing", "state-mismatch", host_only(lambda h: _where(h, 3, "m1.b1")[0].update(disposing=False))),
        ("wrong affordance", "state-mismatch", host_only(lambda h: _where(h, 0, "m1")[0].update(affordances=["hug"]))),
        ("anchor off by one", "state-mismatch", host_only(lambda h: _where(h, 2, "m1")[0].update(anchor=[637, 680]), revel=True)),
        ("wrong origin [999,999]", "state-mismatch", host_only(lambda h: _where(h, 2, "m1")[0].update(origin=[999, 999]))),
        ("companions out of manager order", "entity-mismatch", host_only(swap_order, resort=False)),
        ("an extra companion all run long", "entity-mismatch", host_only(extra_m2)),
        ("wrong RNG use in a directed run", "rng-unexpected-draw",
         host_only(lambda h: h.insert(2, _ev(1, "m1", "rng-draw", site="builtin:behavior-select", occurrence=1, value=0.5, purpose="p")))),
        # trace checks (unjudgeable)
        ("wrong velocity [999,999]", "malformed", host_only(lambda h: _where(h, 2, "m1")[0].update(velocity=[999, 999])), 'is not the anchor change'),
        ("wrong actionStack, refKey left alone", "malformed", host_only(lambda h: _where(h, 2, "m1")[0].update(actionStack=["WRONG"])), 'is not the last actionStack entry'),
        ("duplicate state row", "malformed", host_only(dup_row, resort=False), 'two state lines'),
        ("a gap in one companion's lines", "malformed", host_only(drop(lambda r: r.get("type") == "state" and r["tick"] == 2 and r["sid"] == "m1.b1")), 'have a gap'),
        ("a companion stops without a dispose", "malformed", host_only(drop(lambda r: r.get("kind") == "dispose")), 'with no dispose event'),
        ("a tick past the footer's N", "malformed", host_only(lambda h: _where(h, 4, "m1")[0].update(tick=5), resort=False), 'is outside 0..'),
        ("lines out of tick order", "malformed", host_only(lambda h: h.insert(1, h.pop(-2)), resort=False), 'comes after tick'),
        ("footer counts that do not match", "malformed", host_only(drop(lambda r: r.get("kind") == "transform"), refoot=False), 'footer counts'),
        ("all states and events gone, footers kept (both)", "malformed", both(strip_all, refoot=False), 'footer counts'),
        ("all states and events gone, footers recounted (both)", "malformed", both(strip_all), 'no state at tick 0'),
        ("missing trace field", "missing-field", host_only(lambda h: _where(h, 1, "m1")[0].pop("actionAge"))),
        ("scenario.sha256 gone from both headers", "missing-field", both(lambda t: t[0]["scenario"].pop("sha256"))),
        ("truncated trace", "truncated", host_only(lambda h: h.pop(), refoot=False, resort=False)),
        ("missing build witness", "build-witness-missing", host_only(lambda h: h[0].pop("build"))),
        ("build witness {'nonsense': 'present'}", "build-witness-missing", host_only(lambda h: h[0].update(build={"nonsense": "present"}))),
        ("host labelled oracle", "runtime-mismatch", host_only(lambda h: h[0].update(runtime="oracle"))),
        ("different scenario", "scenario-hash-mismatch", host_only(lambda h: h[0]["scenario"].update(sha256=H("f")))),
        ("different pack", "pack-hash-mismatch", host_only(lambda h: h[0]["scenario"].update(packSha256=H("e")))),
        ("host ran fewer steps", "tick-count-mismatch", host_only(stop_early, resort=False)),
        ("aborted run", "aborted", host_only(lambda h: h[-1].update(status="aborted"), refoot=False)),
        ("timed-out run", "timeout", host_only(lambda h: h[-1].update(status="timeout"), refoot=False)),
        ("an event kind v1 does not define (action)", "missing-field",
         host_only(lambda h: h.insert(-1, _ev(2, "m1", "action", **{"from": "Walk", "to": "Stand", "cause": "duration"})))),
        ("an oracle dependency hash that is not a sha", "build-witness-missing",
         lambda o, h: o[0]["build"]["deps"].update({"jna.jar": "not-a-sha"})),
        ("startedUtc missing from the host header", "missing-field", host_only(lambda h: h[0].pop("startedUtc"))),
        ("a draw value of 1.25 in both traces", "rng-invalid",
         both(lambda t: t.insert(2, _ev(1, "m1", "rng-draw", site="builtin:behavior-select", occurrence=1, value=1.25, purpose="p")))),
        # The same anchor point drawn from another top-left: origin and poseAnchor moved oppositely (Codex's executed
        # counterexample to the old origin + poseAnchor comparison), and each field moved alone.
        ("a rendered shift: origin -10,-6 with poseAnchor +10,+6", "state-mismatch",
         host_only(lambda h: [r.update(origin=[r["origin"][0] - 10, r["origin"][1] - 6], poseAnchor=[42, 134])
                              for r in h if r.get("type") == "state"])),
        ("poseAnchor alone differs [33,128]", "state-mismatch",
         host_only(lambda h: _where(h, 2, "m1")[0].update(poseAnchor=[33, 128])), ", poseAnchor: oracle"),
        # positive controls
        ("WITNESS a refused birth with no child", "exact",
         both(lambda t: t.insert(-1, _ev(4, "m1", "birth-refused", child=None, attempt=2, reason="cap")))),
    ]


def _tape_cases():
    def mut(fn):
        def run(o, h):
            fn(h)
            _refoot(h)
        return run
    draw = lambda h: [r for r in h if r.get("kind") == "rng-draw"][0]

    def past_tape(o, h):
        # The tape holds two values; both runs draw a third.
        for t in (o, h):
            t.insert(3, _ev(1, "m1", "rng-draw", site="builtin:create-facing", occurrence=2, value=0.5, purpose="p"))
            t.insert(4, _ev(1, "m1", "rng-draw", site="builtin:create-facing", occurrence=3, value=0.5, purpose="p"))
            _refoot(t)
    return [
        ("tape: WITNESS one behaviour draw on both sides", "exact", mut(lambda h: None)),
        ("tape: the draw came from another site", "event-mismatch", mut(lambda h: draw(h).update(site="builtin:regist-facing"))),
        ("tape: an extra draw", "rng-draw-count",
         mut(lambda h: h.insert(3, _ev(1, "m1", "rng-draw", site="builtin:create-facing", occurrence=2, value=0.5, purpose="p")))),
        ("tape: draw occurrences out of sequence", "malformed", mut(lambda h: draw(h).update(occurrence=2)), 'occurrence 2 should be 1'),
        ("tape: a recorded value the tape does not hold", "rng-invalid", mut(lambda h: draw(h).update(value=0.25))),
        ("tape: both runs draw past the tape", "rng-tape-exhausted", past_tape),
        ("tape: WITNESS the purpose words differ", "exact", mut(lambda h: draw(h).update(purpose="another wording"))),
    ]


def _scenario():
    return {"schema": SCHEMA_SCENARIO, "id": "S-selftest", "pack": "packs/x", "screens": [{"x": 0, "y": 0, "w": 1280, "h": 720}],
            "workArea": {"x": 0, "y": 0, "w": 1280, "h": 680}, "dpiScale": 1.0, "ticks": 4, "rng": {"kind": "none"},
            "settings": {}, "mascots": [{"sid": "m1", "imageSet": "Alpha", "behavior": "Walk", "anchor": [640, 680], "lookRight": False}],
            "input": [{"tick": 0, "cursor": [1, 1]}]}


def self_test():
    ok = True

    def judge(desc, expect, r, frag=None):
        nonlocal ok
        got = r["verdict"] if expect == "exact" else r.get("class")
        good = (r["verdict"] == "exact") if expect == "exact" else (r["verdict"] in ("fail", "unjudgeable") and got == expect)
        # A row naming a message fragment proves the ONE check it targets fired, not another that overlaps it.
        if good and frag is not None:
            good = frag in (r.get("findings") or [{}])[0].get("message", "")
        ok &= good
        msg = (r.get("findings") or [{}])[0].get("message", "")
        tag = ("OK" if good else "WRONG") if expect == "exact" else ("FIRED" if good else "SURVIVED")
        print(f"  {desc:52} {tag}  ({got}){'' if good else '  expected ' + expect}  {msg[:100]}")

    oracle, host = _reference()
    judge("baseline (identical behaviour)", "exact", compare(oracle, host))
    for desc, expect, fn, *frag in _cases():
        o, h = copy.deepcopy(oracle), copy.deepcopy(host)
        fn(o, h)
        judge(desc, expect, compare(o, h), *frag)
    # Two values on the tape, one used by the reference: an extra draw is then a COUNT difference, not exhaustion.
    to, th = _reference({"kind": "tape", "values": [0.5, 0.5]})
    for desc, expect, fn, *frag in _tape_cases():
        o, h = copy.deepcopy(to), copy.deepcopy(th)
        fn(o, h)
        judge(desc, expect, compare(o, h), *frag)

    # Scenario-level checks.
    s = _scenario()
    judge("scenario: WITNESS a valid scenario, its hashes given", "exact",
          compare(oracle, host, scenario=s, scenario_sha=H("a"), pack_sha=H("b"), artifacts={"oracle": H("4"), "host": H("5")}))
    bad = dict(s, input=[{"tick": 0, "cursor": [1, 1], "click": "left"}])
    judge("scenario: a tick-0 click", "scenario-invalid", compare(oracle, host, scenario=bad))
    judge("scenario: the file on disk is not the traced one", "scenario-hash-mismatch", compare(oracle, host, scenario=s, scenario_sha=H("c")))
    judge("scenario: the pack on disk is not the traced one", "pack-hash-mismatch", compare(oracle, host, scenario=s, pack_sha=H("d")))
    judge("scenario: the host exe given is not the traced one", "artifact-hash-mismatch", compare(oracle, host, artifacts={"host": H("9")}))
    judge("scenario: the scenario asks for more steps than ran", "tick-count-mismatch", compare(oracle, host, scenario=dict(s, ticks=5)))
    nobeh = copy.deepcopy(s)
    nobeh["mascots"][0].pop("behavior")
    judge("scenario: a start companion without a behavior", "scenario-invalid", compare(oracle, host, scenario=nobeh))

    # An empty tape with one draw on both sides: drawing past the tape is the runtimes' behaviour, not a valid parity.
    eo, eh = _reference({"kind": "tape", "values": []})
    judge("tape: an empty tape, one draw on both sides", "rng-tape-exhausted", compare(eo, eh))

    # Free-running is a weaker claim: identical traces pass the time-share check and are still never `exact`.
    fo, fh = _reference({"kind": "splitmix64", "seed": "0x0123456789abcdef"})
    rf = compare(fo, fh)
    good = rf["verdict"] == "time-share-pass"
    ok &= good
    print(f"  {'free-running: identical traces are time-share, never exact':52} {'OK' if good else 'WRONG'}  ({rf['verdict']})")

    # A declared tolerance is named, and covers only its own field.
    tol = copy.deepcopy(host)
    _where(tol, 2, "m1")[0].update(anchor=[637, 680])
    _revelocity(tol)
    rt = compare(oracle, tol, tolerances={"anchor": 1})
    good = rt["verdict"] == "within-tolerance:anchor"
    ok &= good
    print(f"  {'a declared anchor tolerance is named':52} {'OK' if good else 'WRONG'}  ({rt['verdict']})")
    both_wrong = copy.deepcopy(tol)
    _where(both_wrong, 2, "m1")[0].update(visible=False)
    rb = compare(oracle, both_wrong, tolerances={"anchor": 1})
    good = rb["verdict"] == "fail" and rb["findings"][0]["field"] == "visible"
    ok &= good
    print(f"  {'the anchor tolerance does not cover visibility':52} {'FIRED' if good else 'SURVIVED'}  ({rb.get('class')})")
    # The origin tolerance (a named rounding boundary, e.g. a DPI scale) covers origin only, never poseAnchor.
    otol = copy.deepcopy(host)
    _where(otol, 2, "m1")[0].update(origin=[_where(otol, 2, "m1")[0]["origin"][0] + 1, 552])
    ro = compare(oracle, otol, tolerances={"origin": 1})
    good = ro["verdict"] == "within-tolerance:origin"
    ok &= good
    print(f"  {'a declared origin tolerance is named':52} {'OK' if good else 'WRONG'}  ({ro['verdict']})")
    pa = copy.deepcopy(host)
    _where(pa, 2, "m1")[0].update(poseAnchor=[33, 128])
    rp = compare(oracle, pa, tolerances={"origin": 1})
    good = rp["verdict"] == "fail" and rp["findings"][0]["field"] == "poseAnchor"
    ok &= good
    print(f"  {'the origin tolerance does not cover poseAnchor':52} {'FIRED' if good else 'SURVIVED'}  ({rp.get('class')})")

    # The pack manifest is the documented text.
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        os.makedirs(os.path.join(d, "img", "Alpha"))
        with open(os.path.join(d, "img", "Alpha", "b.png"), "wb") as f:
            f.write(b"B")
        with open(os.path.join(d, "Z.xml"), "wb") as f:
            f.write(b"Z")
        want = f"{sha256_bytes(b'Z')}  Z.xml\n{sha256_bytes(b'B')}  img/Alpha/b.png\n"
        good = pack_manifest(d) == want and pack_sha256(d) == sha256_bytes(want.encode("utf-8"))
        ok &= good
        print(f"  {'the pack manifest is sorted by UTF-8 bytes, forward slashes':52} {'OK' if good else 'WRONG'}")

        # The preflight refuses a link before reading anything: a junction inside the pack to a folder outside it.
        outside = os.path.join(d, "..", os.path.basename(d) + "-outside")
        os.makedirs(outside, exist_ok=True)
        try:
            made = False
            try:
                import _winapi
                _winapi.CreateJunction(os.path.abspath(outside), os.path.join(d, "img", "link"))
                made = True
            except (ImportError, OSError, AttributeError):
                try:
                    os.symlink(os.path.abspath(outside), os.path.join(d, "img", "link"), target_is_directory=True)
                    made = True
                except (OSError, NotImplementedError):
                    pass
            if made:
                try:
                    pack_manifest(d)
                    good = False
                except Unjudgeable as u:
                    good = u.cls == "pack-invalid" and "holds a link" in str(u)
                ok &= good
                print(f"  {'a link inside the pack is refused before reading':52} {'FIRED' if good else 'SURVIVED'}")
            else:
                print(f"  {'a link inside the pack is refused before reading':52} NOT RUN (no junction or symlink could be made here)")
        finally:
            link = os.path.join(d, "img", "link")
            if os.path.lexists(link):
                os.rmdir(link) if os.path.isdir(link) else os.remove(link)
            os.rmdir(outside)
    print("SELF-TEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1


def _arg(argv, name):
    return argv[argv.index(name) + 1] if name in argv else None


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if len(argv) < 3 or argv[1].startswith("--") or argv[2].startswith("--"):
        print(__doc__)
        return 2
    scenario = scenario_sha = pack_sha = None
    path = _arg(argv, "--scenario")
    if path:
        with open(path, "rb") as f:
            raw = f.read()
        scenario_sha = sha256_bytes(raw)
        scenario = json.loads(raw.decode("utf-8"))
        pack_dir = os.path.join(os.path.dirname(os.path.abspath(path)), str(scenario.get("pack", "")))
        if not os.path.isdir(pack_dir):
            print(f"{scenario.get('id', '?')}: unjudgeable (scenario-invalid)\n  the pack {pack_dir} does not exist")
            return 2
        try:
            pack_sha = pack_sha256(pack_dir)
        except Unjudgeable as u:
            print(f"{scenario.get('id', '?')}: unjudgeable ({u.cls})\n  {u}")
            return 2
    artifacts = {}
    for runtime, flag in (("oracle", "--oracle-artifact"), ("host", "--host-exe")):
        p = _arg(argv, flag)
        if p:
            with open(p, "rb") as f:
                artifacts[runtime] = sha256_bytes(f.read())
    tolerances = (scenario or {}).get("tolerances", {})
    r = compare(argv[1], argv[2], scenario, scenario_sha, pack_sha, artifacts, tolerances)
    out = _arg(argv, "--json")
    if out:
        with open(out, "w", encoding="utf-8") as f:
            json.dump(r, f, indent=1)
    print(f"{r.get('scenario', '?')}: {r['verdict']}" + (f" ({r['class']})" if r.get("class") else ""))
    for fnd in r["findings"][:5]:
        print("  " + fnd.get("message", str(fnd)))
    if "draws" in r:
        print(f"  draws: oracle {r['draws']['oracle']}, host {r['draws']['host']}")
    passed = r["verdict"] in ("exact", "time-share-pass") or r["verdict"].startswith("within-tolerance")
    return 0 if passed else (2 if r["verdict"] == "unjudgeable" else 1)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
