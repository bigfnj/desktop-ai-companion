"""Shimeji parity comparer (CONTRACT.md v1.2, sections 3, 6, 9 and 10).

    compare.py <oracle trace.jsonl> <host trace.jsonl> [--scenario scenario.json] [--oracle-artifact <jar>]
               [--host-exe <exe>] [--json out.json]
    compare.py --self-test

Reads two traces of the same scenario and decides whether the runtimes behaved the same. Each trace is checked
first (fields, build witness, counts, ordering, lineage, velocity and refKey consistency); a trace that fails a
check is unjudgeable and nothing is compared. A directed scenario (rng kind none or tape) must then match EXACTLY on
events and on every state field; a free-running one (splitmix64) is a separate claim and is reported as such. Every
failure names the scenario, the first mismatching tick, the entity and the field. Exit 0 = a successful exact,
tolerated or free-running time-share comparison, 1 = a mismatch or pending comparison,
2 = a trace or scenario that cannot be judged.

--self-test builds reference traces and a set of deliberately wrong ones, and requires each wrong one to fail with
its expected class (and each positive control to stay exact); it is the proof that this comparer can fail.
"""
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

SCHEMA_TRACE = "shimeji-parity-trace/1"
SCHEMA_TRACE_V2 = "shimeji-parity-trace/2"
SCHEMA_SCENARIO = "shimeji-parity-scenario/1"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
SOURCE_SHA256 = json.loads((Path(__file__).resolve().parent.parent / "oracle" / "source-pin.json").read_text(
    encoding="utf-8"))["sourceZipSha256"]
if not isinstance(SOURCE_SHA256, str) or not HEX64.fullmatch(SOURCE_SHA256):
    raise ValueError("the shared oracle source pin is not a SHA-256")

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
ERROR_OPERANDS = {
    "behavior-missing": {"imageSet", "behavior"},
    "action-missing": {"imageSet", "action"},
    "variant-missing": {"refKey"},
    "parameter-invalid": {"refKey", "parameter"},
    "scan-target-missing": {"refKey"},
    "unclassified": None,
}
ERROR_WRAPPERS = {"startup", "press", "release", "birth", "transform", "arrive", "interact"}
SOURCE_REFKEY = re.compile(r"^[^|]+\|[^|]+\|(?:/[^/\[\]|]+\[[1-9][0-9]*\])+$")


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
    files, folded = [], {}
    for root, dirs, names in os.walk(pack_dir, followlinks=False):
        for name in dirs + names:
            full = os.path.join(root, name)
            if _is_link(full):
                raise Unjudgeable("pack-invalid", f"the pack holds a link: {os.path.relpath(full, pack_dir)}")
            # Two entries that differ only in case (a pack built in a case-sensitive folder) are one file or folder to
            # a case-insensitive copy, which keeps the first name and the last bytes, so the copied pack no longer
            # matches its manifest (review W123 F4). Both upper() and casefold() are tried: between them they cover
            # the simple folds of the NTFS upcase table. These folds conservatively reject some Unicode pairs that the
            # destination can preserve (sharp s and "ss", which an ordinary Windows copy keeps apart): such a pack is
            # refused as pack-invalid rather than judged.
            relative = os.path.relpath(full, pack_dir).replace(os.sep, "/")
            for key in (relative.upper(), relative.casefold()):
                other = folded.setdefault(key, relative)
                if other != relative:
                    raise Unjudgeable("pack-invalid", f"the pack holds entries that differ only in case: {other}, {relative}")
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


class TraceObject(dict):
    """Keep duplicate evidence for trace/2 error objects without changing trace/1 last-wins semantics."""
    def __init__(self, pairs):
        super().__init__(pairs)
        seen = set()
        self.duplicate_keys = set()
        for key, _ in pairs:
            if key in seen:
                self.duplicate_keys.add(key)
            seen.add(key)
        self.duplicates = bool(self.duplicate_keys)


def _trace_lines(source):
    if isinstance(source, str):
        with open(source, encoding="utf-8") as stream:
            return [line for line in stream.read().splitlines() if line.strip()]
    return source


def _trace_record(line, number):
    try:
        return json.loads(line, object_pairs_hook=TraceObject) if isinstance(line, str) else line
    except json.JSONDecodeError as error:
        raise Unjudgeable("missing-field", f"line {number} is not JSON: {error}")


def _trace_header(lines):
    header = _trace_record(lines[0], 1) if lines else None
    if not isinstance(header, dict) or header.get("type") != "header":
        raise Unjudgeable("missing-field", "the first line is not a header")
    if header.get("schema") not in (SCHEMA_TRACE, SCHEMA_TRACE_V2):
        raise Unjudgeable("missing-field", f"schema is {header.get('schema')!r}, expected a recognized trace schema")
    return header


def _validate_error(event, runtime, number):
    def bad(detail):
        raise Unjudgeable("missing-field", f"{runtime} line {number}: error {detail}")
    if "error" in getattr(event, "duplicate_keys", ()):
        bad("event has duplicate error properties")
    message, error = event.get("message"), event.get("error")
    if not isinstance(event.get("sid"), str) or not event["sid"] or isinstance(event["tick"], bool):
        bad("identity needs an integer tick and a non-empty sid")
    if not isinstance(message, str) or not message:
        bad("message must be a non-empty string")
    if not isinstance(error, dict) or set(error) != {"code", "lookup", "via"}:
        bad("object needs exactly code, lookup and via")
    if getattr(error, "duplicates", False):
        bad("object has duplicate properties")
    code, lookup, via = error["code"], error["lookup"], error["via"]
    if not isinstance(code, str) or code not in ERROR_OPERANDS:
        bad("code is not in the closed vocabulary")
    if not isinstance(via, list) or any(not isinstance(site, str) or site not in ERROR_WRAPPERS for site in via):
        bad("via is not an array of known wrapper names")
    operands = ERROR_OPERANDS[code]
    if operands is None:
        if lookup is not None:
            bad("unclassified lookup must be null")
        return
    if not isinstance(lookup, dict) or set(lookup) != operands:
        bad("lookup has the wrong operand set")
    if getattr(lookup, "duplicates", False):
        bad("lookup has duplicate properties")
    for key, value in lookup.items():
        if key in ("behavior", "action"):
            valid = value is None or isinstance(value, str)
        elif key == "refKey":
            valid = _source_refkey(value)
        else:
            valid = isinstance(value, str) and bool(value)
        if not valid:
            bad(f"lookup operand {key!r} has the wrong type or format")


def _source_refkey(value):
    if not isinstance(value, str) or SOURCE_REFKEY.fullmatch(value) is None:
        return False
    filename = value.split("|")[1]
    return "\\" not in filename and ":" not in filename and all(part not in ("", ".", "..") for part in filename.split("/"))


def load_trace(source, expected_runtime=None, defer_run_failure=False):
    lines = _trace_lines(source)
    records = []
    for i, line in enumerate(lines):
        records.append(_trace_record(line, i + 1))
    if not records or not isinstance(records[0], dict) or records[0].get("type") != "header":
        raise Unjudgeable("missing-field", "the first line is not a header")
    header = records[0]
    _trace_header(records)
    trace2 = header["schema"] == SCHEMA_TRACE_V2
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
        # The source archive is fixed; the patch is per build and has no repository-wide expected hash.
        if build["sourceZipSha256"] != SOURCE_SHA256:
            raise Unjudgeable("source-pin-mismatch", "oracle sourceZipSha256 differs from the pinned source archive")
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

    has_footer = len(records) >= 2 and isinstance(records[-1], dict) and records[-1].get("type") == "footer"
    if not has_footer and not defer_run_failure:
        raise Unjudgeable("truncated", f"{runtime} trace has no footer")
    # A missing footer is a deferred run failure, not a manufactured physical footer. Its available records
    # still need validation and input binding before a valid peer can establish section 6's scenario error.
    footer = records[-1] if has_footer else None
    status = footer.get("status") if footer is not None else "truncated"
    if footer is not None:
        if status not in ("complete", "timeout", "aborted", "error"):
            raise Unjudgeable("missing-field", f"{runtime} footer status {status!r}")
        for k in ("ticks", "draws", "states", "events"):
            if not isinstance(footer.get(k), int) or isinstance(footer.get(k), bool) or footer[k] < 0:
                raise Unjudgeable("missing-field", f"{runtime} footer {k!r} is missing")
        if trace2 and not isinstance(footer.get("tapeExhausted"), bool):
            raise Unjudgeable("missing-field", f"{runtime} footer tapeExhausted is not a boolean")
    n = footer["ticks"] if footer is not None else None

    def malformed(msg):
        raise Unjudgeable("malformed", f"{runtime}: {msg}")

    states, events = [], []
    last_tick = 0
    for i, r in enumerate(records[1:-1] if has_footer else records[1:], start=2):
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
            # A nullable point still needs both coordinates. zip() alone would accept [] under a tolerance.
            if r["origin"] is not None and not _is_point(r["origin"]):
                raise Unjudgeable("missing-field", f"{runtime} line {i}: origin is null or [x, y]")
            states.append(r)
        elif t == "event":
            kind = r.get("kind")
            missing = [k for k in ("tick", "sid", "kind") if k not in r]
            if not missing and kind in EVENT_PAYLOAD_KEYS:
                missing = [k for k in EVENT_PAYLOAD_KEYS[kind] if k not in r]
            if missing or kind not in EVENT_PAYLOAD_KEYS or not isinstance(r.get("tick"), int):
                raise Unjudgeable("missing-field", f"{runtime} line {i}: event {kind!r} lacks {missing or 'a known kind'}")
            if trace2 and kind == "error":
                _validate_error(r, runtime, i)
            if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):
                raise Unjudgeable("missing-field", f"{runtime}: tick {r['tick']}: rng-draw occurrence must be an integer")
            events.append(r)
        else:
            malformed(f"line {i}: unknown record type {t!r}")
        tick = r["tick"]
        if tick < 0 or (n is not None and tick > n):
            malformed(f"line {i}: tick {tick} is outside 0..{n}")
        if tick < last_tick:
            malformed(f"line {i}: tick {tick} comes after tick {last_tick}")
        last_tick = tick

    draws = [e for e in events if e["kind"] == "rng-draw"]
    if footer is not None and (len(states), len(events), len(draws)) != (footer["states"], footer["events"], footer["draws"]):
        malformed(f"footer counts states/events/draws {footer['states']}/{footer['events']}/{footer['draws']} "
                  f"but the trace has {len(states)}/{len(events)}/{len(draws)}")
    for k, e in enumerate(draws, start=1):
        if e["occurrence"] != k:
            malformed(f"tick {e['tick']}: rng-draw occurrence {e['occurrence']!r} should be {k}")
    # The numbers must be the scenario's: a value outside [0, 1), or a tape value the tape does not hold at that
    # occurrence, means the run did not take its numbers as declared. Drawing past the tape is behaviour (compare()).
    tape = rng_spec.get("values") if rng_spec["kind"] == "tape" else None
    if trace2 and footer is not None and footer["tapeExhausted"] != (tape is not None and len(draws) > len(tape)):
        malformed("tapeExhausted disagrees with the recorded draws")
    for e in draws:
        v = e["value"]
        if not isinstance(v, (int, float)) or isinstance(v, bool) or not 0 <= v < 1:
            raise Unjudgeable("rng-invalid", f"{runtime}: tick {e['tick']}: rng-draw value {v!r} is outside [0, 1)")
        # Sequence equality already established the exact value; trace/2 also checked the wire type.
        k = int(e["occurrence"])
        if tape is not None and k <= len(tape) and v != tape[k - 1]:
            raise Unjudgeable("rng-invalid", f"{runtime}: tick {e['tick']}: draw {k} recorded {v!r}, the tape holds {tape[k - 1]!r}")
        if trace2 and tape is not None and k > len(tape) and v != 0.5:
            raise Unjudgeable("rng-invalid", f"{runtime}: tick {e['tick']}: past-tape draw {k} recorded {v!r}, expected 0.5")
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
    # A startup refusal can leave no states at all. Its metadata and recorded events still need checking,
    # but there is no observation prefix whose missing tick 0 could establish malformed runtime behavior.
    if (states or status == "complete") and not any(s["tick"] == 0 for s in states):
        malformed("there is no state at tick 0")
    # A failed run can stop before N, but the recorded prefix still has to be an internally valid observation.
    # compare() defers its status so section 6 can first identify an invalid input from the preceding snapshot.
    observed_end = n if status == "complete" else max((s["tick"] for s in states), default=-1)
    disposed = {(e["sid"], e["tick"]) for e in events if e["kind"] == "dispose"}
    previous_order = [s["sid"] for s in states if s["tick"] == observed_end - 1
                      and not any(sid_ == s["sid"] and tick <= observed_end for sid_, tick in disposed)]
    final_order = [s["sid"] for s in states if s["tick"] == observed_end]
    # A written later companion proves that an earlier non-disposed companion was omitted. New children
    # append after existing companions, so observing one also defeats an earlier companion's tail excuse.
    tail_is_prefix = not trace2 or final_order == previous_order[:len(final_order)]
    for sid_, rows in by_sid.items():
        ticks = [r["tick"] for r in rows]
        if ticks != list(range(ticks[0], ticks[-1] + 1)):
            malformed(f"{sid_}'s state lines have a gap ({ticks[0]}..{ticks[-1]}, {len(ticks)} lines)")
        # Without a footer, the last snapshot may end between two companions' state lines. Only that final
        # one-tick tail is inconclusive unless a later event proves that snapshot already finished.
        interrupted = footer is None or (trace2 and status == "timeout")
        unfinished_snapshot = interrupted and tail_is_prefix and ticks[-1] == observed_end - 1 \
            and not any(e["tick"] > observed_end for e in events)
        if ticks[-1] < observed_end and not unfinished_snapshot and (sid_, ticks[-1] + 1) not in disposed:
            malformed(f"{sid_} stops at tick {ticks[-1]} of {observed_end} with no dispose event at tick {ticks[-1] + 1}")
        # dispose means actual queue removal, not dispose-request. Failed tentative children can reuse their
        # provisional sid and emit requests, but never join the add queue (CONTRACT sections 3 and 4).
        disposed_at = min((tick for disposed_sid, tick in disposed if disposed_sid == sid_), default=None)
        if disposed_at is not None and ticks[-1] >= disposed_at:
            first = next(row for row in rows if row["tick"] >= disposed_at)
            malformed(f"tick {first['tick']}, {sid_}: state at or after dispose tick {disposed_at}")
        prev = None
        for r in rows:
            want = [0, 0] if prev is None else [r["anchor"][0] - prev["anchor"][0], r["anchor"][1] - prev["anchor"][1]]
            if r["velocity"] != want:
                malformed(f"tick {r['tick']}, {sid_}: velocity {r['velocity']} is not the anchor change {want}")
            prev = r
    if not defer_run_failure:
        _check_run_status(runtime, footer)
    return header, states, events, footer


def _check_run_status(runtime, footer):
    if footer is None:
        raise Unjudgeable("truncated", f"{runtime} trace has no footer")
    status = footer["status"]
    if status != "complete":
        raise Unjudgeable(status if status != "error" else "aborted", f"{runtime} run ended with status {status}")


def _validate_input_targets(scenario, traces):
    """Section 6: use a complete preceding snapshot or the target's irreversible observed removal."""
    def inactive(runtime, edge):
        raise Unjudgeable("scenario-invalid", f"scenario {scenario['id']}: {runtime} tick {edge['tick']} "
                          f"left-button edge targets inactive sid {edge.get('sid')!r}")

    for edge in scenario.get("input", []):
        if not any(edge.get(button) == "left" for button in BUTTON_EDGES):
            continue
        previous = edge["tick"] - 1
        for runtime, states, events, footer in traces:
            if footer is None or not states:
                continue  # An unfinished trace or startup refusal supplies no positive population proof.
            last_seen = max((state["tick"] for state in states if state["sid"] == edge.get("sid")), default=None)
            removed = last_seen is not None and any(event["kind"] == "dispose" and event["sid"] == edge.get("sid")
                      and event["tick"] <= previous for event in events)
            if removed:
                # Stable sids are never re-created. This target's removal remains conclusive after the last
                # companion leaves, when completed empty ticks no longer advance the latest state timestamp.
                # The loader already refuses every state at or after its sid's dispose tick. No second lower
                # bound here can add evidence; retain only the input's upper bound and sid match.
                inactive(runtime, edge)
            observed_end = footer["ticks"] if footer["status"] == "complete" else max(s["tick"] for s in states)
            if previous > observed_end:
                continue  # A future edge after an unrelated abort has no observed dispatch state.
            expected = {mascot["sid"] for mascot in scenario["mascots"]}
            for event in events:
                if event["tick"] > previous:
                    break
                if event["kind"] == "birth-request":
                    if not isinstance(event["child"], str):
                        expected = None
                        break
                    expected.add(event["child"])
                elif event["kind"] == "dispose":
                    expected.discard(event["sid"])
            snapshot = [state for state in states if state["tick"] == previous]
            if expected is None or {state["sid"] for state in snapshot} != expected:
                continue  # An omitted snapshot or child line cannot prove that its target was inactive.
            if not any(state["sid"] == edge.get("sid") and not state["pending"] for state in snapshot):
                inactive(runtime, edge)


def json_key(value):
    """JSON equality: numbers compare by value, while booleans retain their distinct scalar type."""
    if value is None:
        return ("null",)
    if isinstance(value, bool):
        return ("boolean", value)
    if isinstance(value, (int, float)):
        return ("number", value)
    if isinstance(value, str):
        return ("string", value)
    if isinstance(value, list):
        return ("array", tuple(json_key(item) for item in value))
    if isinstance(value, dict):
        return ("object", tuple((key, json_key(item)) for key, item in sorted(value.items())))
    raise ValueError("not a JSON value")


def event_key(e, trace2=False):
    if trace2 and e["kind"] == "error":
        return tuple(json_key(e.get(k)) for k in ("tick", "sid", "kind", "error"))
    return tuple(json_key(e.get(k)) for k in ("tick", "sid", "kind") + EVENT_PAYLOAD_KEYS[e["kind"]])


def _unknown_error_pair(ea, eb, trace2):
    return trace2 and ea is not None and eb is not None and ea["kind"] == eb["kind"] == "error" \
        and all(json_key(ea[key]) == json_key(eb[key]) for key in ("tick", "sid", "kind")) \
        and ea["error"]["via"] == eb["error"]["via"] \
        and "unclassified" in (ea["error"]["code"], eb["error"]["code"])


def _events_align(ea, eb, trace2):
    return event_key(ea, trace2) == event_key(eb, trace2) or _unknown_error_pair(ea, eb, trace2)


def compare(oracle, host, scenario=None, scenario_sha=None, pack_sha=None, artifacts=None, tolerances=None):
    """Returns a report dict: verdict, class, findings (each with tick/sid/field/oracle/host)."""
    tolerances = tolerances or {}
    artifacts = artifacts or {}
    secondary_exhaustion = []
    try:
        if scenario is not None:
            validate_scenario(scenario)
        # Read only the version declarations before any build/body checks. A mixed recognized pair has no
        # shared body rules; an off artifact is proof-only even if its withheld body would fail ordinary validity.
        oracle, host = _trace_lines(oracle), _trace_lines(host)
        preflight_headers = (_trace_header(oracle), _trace_header(host))
        if preflight_headers[0]["schema"] != preflight_headers[1]["schema"]:
            raise Unjudgeable("trace-version-mismatch", "the traces declare different recognized versions")
        trace2 = preflight_headers[0]["schema"] == SCHEMA_TRACE_V2
        if trace2:
            for trace in (oracle, host):
                try:
                    tail = _trace_record(trace[-1], len(trace)) if len(trace) > 1 else None
                except Unjudgeable:
                    continue  # Ordinary validity will grade it after both possible off markers are considered.
                if isinstance(tail, dict) and tail.get("type") == "footer" and ("unobservedDraws" in tail or "unobservedEvents" in tail):
                    raise Unjudgeable("trace-off-not-comparable", "trace-off artifacts are observation-proof inputs only")
        oh, ostates, oevents, ofoot = load_trace(oracle, "oracle", defer_run_failure=True)
        hh, hstates, hevents, hfoot = load_trace(host, "host", defer_run_failure=True)
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
        known_ticks = [footer["ticks"] for footer in (ofoot, hfoot) if footer is not None]
        if known_ticks and (any(ticks != known_ticks[0] for ticks in known_ticks)
                            or (scenario is not None and scenario["ticks"] != known_ticks[0])):
            raise Unjudgeable("tick-count-mismatch", f"oracle ran {ofoot['ticks'] if ofoot is not None else 'unknown'} steps, "
                              f"host {hfoot['ticks'] if hfoot is not None else 'unknown'}"
                              + (f", scenario {scenario['ticks']}" if scenario is not None else ""))
        tick_bound = scenario["ticks"] if scenario is not None else (known_ticks[0] if known_ticks else None)
        for runtime, states, events, footer in (("oracle", ostates, oevents, ofoot), ("host", hstates, hevents, hfoot)):
            if footer is None and tick_bound is not None:
                for row in states + events:
                    if row["tick"] > tick_bound:
                        raise Unjudgeable("malformed", f"{runtime}: tick {row['tick']} is outside 0..{tick_bound}")
        if scenario is not None:
            # Bind the observations to their inputs before judging this scenario's startup. Otherwise a wrong
            # --scenario file with different starters looks like a runtime startup defect instead of a hash mismatch.
            # Equal omissions in two bound traces are still incomplete observations of this scenario's startup.
            expected_starters = [mascot["sid"] for mascot in scenario["mascots"]]
            for runtime, states, events, footer in (("oracle", ostates, oevents, ofoot), ("host", hstates, hevents, hfoot)):
                if footer is None and not trace2:
                    continue  # Truncation can interrupt even the startup snapshot.
                if not states and (footer is None or footer["status"] != "complete"):
                    if not trace2 or (footer is not None and footer["status"] in ("aborted", "error")):
                        continue  # Retain the existing zero-state aborted/error outcome and trace/1 behavior.
                starter_states = [state for state in states if state["tick"] == 0]
                actual_starters = [state["sid"] for state in starter_states]
                if trace2 and (footer is None or footer["status"] == "timeout") and actual_starters == expected_starters[:len(actual_starters)] \
                        and len(actual_starters) < len(expected_starters) and not any(state["pending"] for state in starter_states) \
                        and not any(row["tick"] > 0 for row in states + events):
                    continue  # An active, ordered proper prefix cannot prove the interrupted snapshot finished.
                if actual_starters != expected_starters:
                    raise Unjudgeable("scenario-start-mismatch", f"{runtime} tick-0 companions {actual_starters!r} "
                                      f"do not match scenario starters {expected_starters!r}")
                # Startup is normalized to active companions before tick 0; two equally pending populations
                # must not establish parity merely because their sid order and pending flags agree.
                if any(state["pending"] for state in starter_states):
                    raise Unjudgeable("scenario-start-mismatch", f"{runtime} tick-0 starters must be active, not pending")
            _validate_input_targets(scenario, (("oracle", ostates, oevents, ofoot), ("host", hstates, hevents, hfoot)))
        if trace2 and oh["rng"]["kind"] == "tape":
            for runtime, events in (("oracle", oevents), ("host", hevents)):
                exhausted = next((event for event in events if event["kind"] == "rng-draw"
                                  and event["occurrence"] > len(oh["rng"]["values"])), None)
                if exhausted is not None:
                    secondary_exhaustion.append({"class": "rng-tape-exhausted", "runtime": runtime,
                                                 "tick": exhausted["tick"], "sid": exhausted["sid"],
                                                 "message": runtime + " recorded a past-tape draw"})
        # After input proof, retain the missing footer's existing priority over a peer's generic run status.
        for runtime, footer in sorted((("oracle", ofoot), ("host", hfoot)), key=lambda item: item[1] is not None):
            _check_run_status(runtime, footer)
    except Unjudgeable as u:
        return {"verdict": "unjudgeable", "class": u.cls, "findings": [{"message": str(u)}]
                + (secondary_exhaustion if u.cls in ("truncated", "timeout", "aborted") else [])}

    report = {"verdict": "exact", "findings": [], "class": None, "scenario": sid,
              "draws": {"oracle": ofoot["draws"], "host": hfoot["draws"]},
              "buildWitness": {runtime: {"artifactBound": runtime in artifacts,
                                         "verification": "artifact-bound" if runtime in artifacts else "shape-only"}
                               for runtime in ("oracle", "host")}, "tolerancesUsed": []}
    used_tolerances = set()
    unknown_error = None
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
            # Map/2 cannot recover the source script call site. After validity, binding and run checks,
            # the independent RNG failures above still stand; only attribution-dependent matching is pending.
            script_draw = next((event for event in oevents + hevents if event["kind"] == "rng-draw"
                                and isinstance(event["site"], str) and event["site"].startswith("script:")), None)
            if script_draw is not None:
                report["verdict"] = "pending"
                report["class"] = "script-site-unmapped"
                findings.append({"class": "script-site-unmapped", "tick": script_draw["tick"], "sid": script_draw["sid"],
                                 "field": "rng-draw.site",
                                 "message": f"{sid}: script RNG site attribution is unavailable until a later parity map revision"})
                return report
        if not findings:
            okeys = [event_key(e, trace2) for e in oevents]
            hkeys = [event_key(e, trace2) for e in hevents]
            for i in range(max(len(okeys), len(hkeys))):
                a = okeys[i] if i < len(okeys) else None
                b = hkeys[i] if i < len(hkeys) else None
                ea = oevents[i] if a else None
                eb = hevents[i] if b else None
                if _unknown_error_pair(ea, eb, trace2):
                    if unknown_error is None:
                        unknown_error = {"class": "error-unclassified", "tick": ea["tick"], "sid": ea["sid"],
                                         "field": "error", "via": list(ea["error"]["via"]),
                                         "oracle": ea["error"]["code"], "host": eb["error"]["code"],
                                         "message": f"{sid}: tick {ea['tick']}, {ea['sid']}, error via {ea['error']['via']!r}: "
                                                    f"oracle {ea['error']['code']!r} vs host {eb['error']['code']!r}; "
                                                    "aligned cause has no typed attribution"}
                    continue  # Only this aligned cause is inconclusive; every later event/state still matters.
                if a == b:
                    continue
                if a is None:
                    cls = "extra-event"
                elif b is None:
                    cls = "missing-event"
                elif not any(_events_align(ea, event, trace2) for event in hevents[i:]) \
                        and any(_events_align(event, eb, trace2) for event in oevents[i + 1:]):
                    cls = "missing-event"   # the host never has the oracle's event, and then the two line up again
                elif not any(_events_align(event, eb, trace2) for event in oevents[i:]) \
                        and any(_events_align(ea, event, trace2) for event in hevents[i + 1:]):
                    cls = "extra-event"     # the host has one the oracle never has, and then the two line up again
                else:
                    cls = "event-mismatch"
                pick = lambda e: e and {k: e.get(k) for k in ("kind",) + (("error",) if trace2 and e["kind"] == "error"
                                                                           else EVENT_PAYLOAD_KEYS[e["kind"]])}
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
                        if json_key(a[f]) != json_key(b[f]):
                            mismatch = (a["sid"], f, a[f], b[f])
                            break
                    if mismatch is None and a["anchor"] != b["anchor"]:
                        tol = tolerances.get("anchor")
                        if tol is not None and all(abs(x - y) <= tol for x, y in zip(a["anchor"], b["anchor"])):
                            used_tolerances.add("anchor")
                        else:
                            mismatch = (a["sid"], "anchor", a["anchor"], b["anchor"])
                    # The drawn image's top-left, on its own: a sum with poseAnchor would pass an image drawn somewhere
                    # else whose anchor offset moved the other way by the same amount (a rendered shift).
                    if mismatch is None and a["origin"] != b["origin"]:
                        tol = tolerances.get("origin")
                        if (tol is not None and isinstance(a["origin"], list) and isinstance(b["origin"], list)
                                and all(abs(x - y) <= tol for x, y in zip(a["origin"], b["origin"]))):
                            used_tolerances.add("origin")
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

    report["tolerancesUsed"] = sorted(used_tolerances)
    if not findings and used_tolerances:
        report["verdict"] = "within-tolerance:" + ",".join(report["tolerancesUsed"])
    if findings:
        report["verdict"] = "fail"
        report["class"] = findings[0]["class"]
    elif unknown_error:
        report["verdict"] = "unjudgeable"
        report["class"] = "error-unclassified"
        findings.append(unknown_error)
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
         "build": {"sourceZipSha256": SOURCE_SHA256, "patchSha256": H("2"), "jdk": "1.8.0_504", "deps": {"jna.jar": H("3")},
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


BURNDOWN_CHECKS = ("N269 source pin", "N269 unbound report", "N270 typed state", "N270 typed event",
                   "N270 event numbers", "N271 origin shape", "N272 tolerance names", "N400 starter population",
                   "N400 binding order", "N400 pending starters", "P2 inactive targets", "P2 input precedence",
                   "P2 partial input proof", "P2 physical input CLI", "P2 script pending", "P2 script precedence",
                   "P2 script independent failures", "P2 script refusal before draw", "P2 script pending CLI",
                   "R2 zero-state runs", "R2 zero-state validation", "R2 disposed targets",
                   "R2 truncated peer proof", "R2 truncated validation", "F3 interrupted snapshot", "F3 disposal lifecycle")
BURNDOWN_CHECKS += tuple("T2 CC" + str(number) for number in range(1, 18)) + (
    "T2 timeout snapshot", "T2 trace1 freeze", "T2 lookahead", "T2 occurrence")


def _case_require(value, name, detail):
    if not value:
        raise AssertionError("bd-codex-parity: " + name + ": " + detail)


def _two_starter_reference():
    oracle, host = _reference()
    for trace in (oracle, host):
        body = []
        for row in trace[1:-1]:
            body.append(row)
            if row.get("type") == "state" and row["sid"] == "m1":
                body.append(dict(copy.deepcopy(row), sid="m2"))
        trace[1:-1] = body
        _refoot(trace)
    scenario = _scenario()
    scenario["mascots"].append(dict(scenario["mascots"][0], sid="m2"))
    return oracle, host, scenario


def _cut_reference(trace, before_tick, status="aborted"):
    trace[1:-1] = [row for row in trace[1:-1] if row["tick"] < before_tick]
    trace[-1]["status"] = status
    _refoot(trace)


def _input_reference(tick=2, sid="m1.b1", button="left", edge="click"):
    oracle, host = _reference()
    scenario = _scenario()
    scenario["input"].append(dict({"tick": tick, "sid": sid}, **{edge: button}))
    return oracle, host, scenario


def _p2_check(name):
    def require(report, expected, detail, verdict="unjudgeable"):
        _case_require(report["verdict"] == verdict and report["class"] == expected, name,
                      detail + ": " + repr((report["verdict"], report["class"])))

    if name == "P2 inactive targets":
        for edge in BUTTON_EDGES:
            for tick, target in ((1, "unknown"), (2, "m1.b1")):
                for failed_runtime in (None, "oracle", "host", "both"):
                    oracle, host, scenario = _input_reference(tick, target, edge=edge)
                    for runtime, trace in (("oracle", oracle), ("host", host)):
                        if failed_runtime in (runtime, "both"):
                            _cut_reference(trace, tick)
                    require(compare(oracle, host, scenario=scenario), "scenario-invalid",
                            edge + " inactive target before " + str(failed_runtime) + " run failure")
        # Either runtime's observation establishes the pending target, even when its peer has activated it early.
        for pending_runtime in ("oracle", "host"):
            oracle, host, scenario = _input_reference()
            for runtime, trace in (("oracle", oracle), ("host", host)):
                if runtime != pending_runtime:
                    _where(trace, 1, "m1.b1")[0]["pending"] = False
            require(compare(oracle, host, scenario=scenario), "scenario-invalid", pending_runtime + " pending target")
        for tick, target in ((1, "m1"), (3, "m1.b1"), (4, "m1.b1")):
            for edge in BUTTON_EDGES:
                oracle, host, scenario = _input_reference(tick, target, edge=edge)
                require(compare(oracle, host, scenario=scenario), None,
                        "WITNESS active, activated or still-disposing left target", "exact")
        oracle, host, scenario = _input_reference(4, "m1.b1")
        for trace in (oracle, host):
            next(row for row in trace if row.get("kind") == "dispose")["tick"] = 3
            trace[1:-1] = [row for row in trace[1:-1] if not (
                row.get("type") == "state" and row["tick"] == 3 and row["sid"] == "m1.b1")]
            _resort(trace)
            _refoot(trace)
        require(compare(oracle, host, scenario=scenario), "scenario-invalid", "removed target")
        for edge in BUTTON_EDGES:
            for target in ("unknown", "m1.b1"):
                oracle, host, scenario = _input_reference(2, target, "right", edge)
                require(compare(oracle, host, scenario=scenario), None, "WITNESS right edge is ignored", "exact")
        oracle, host, scenario = _input_reference()
        scenario["input"][-1] = {"tick": 2, "sid": "unknown", "cursor": [1, 2]}
        require(compare(oracle, host, scenario=scenario), None, "WITNESS cursor-only entry is not a button edge", "exact")
    elif name == "P2 input precedence":
        oracle, host, scenario = _input_reference()
        for trace in (oracle, host):
            _cut_reference(trace, 2)
        cases = (
            ("missing-field", lambda o, h, s: _where(h, 1, "m1")[0].pop("actionAge"), {}),
            ("malformed", lambda o, h, s: h[-1].update(states=999), {}),
            ("malformed", lambda o, h, s: _where(h, 1, "m1")[0].update(velocity=[999, 0]), {}),
            ("build-witness-missing", lambda o, h, s: h[0].pop("build"), {}),
            ("source-pin-mismatch", lambda o, h, s: o[0]["build"].update(sourceZipSha256=H("0")), {}),
            ("scenario-hash-mismatch", lambda o, h, s: None, {"scenario_sha": H("c")}),
            ("pack-hash-mismatch", lambda o, h, s: None, {"pack_sha": H("d")}),
            ("artifact-hash-mismatch", lambda o, h, s: None, {"artifacts": {"host": H("9")}}),
            ("scenario-hash-mismatch", lambda o, h, s: s.update(rng={"kind": "tape", "values": []}), {}),
            ("tick-count-mismatch", lambda o, h, s: s.update(ticks=5), {}),
            ("scenario-start-mismatch", lambda o, h, s: s["mascots"].append(dict(s["mascots"][0], sid="m2")), {}),
            ("scenario-start-mismatch", lambda o, h, s: _where(h, 0, "m1")[0].update(pending=True), {}),
        )
        for expected, change, arguments in cases:
            o, h, s = copy.deepcopy((oracle, host, scenario))
            change(o, h, s)
            require(compare(o, h, scenario=s, **arguments), expected, "input classification hid " + expected)
        require(compare(oracle, host, scenario=scenario), "scenario-invalid", "WITNESS validated pending prefix")
    elif name == "P2 partial input proof":
        oracle, host, scenario = _input_reference(4, "unknown")
        for trace in (oracle, host):
            _cut_reference(trace, 1)
        require(compare(oracle, host, scenario=scenario), "aborted", "future edge after an unrelated abort")
        require(compare(oracle, host), "aborted", "WITNESS old abort without scenario stays unjudgeable")
        # Once every companion has left, an empty future snapshot looks like an empty observed one. The prefix
        # boundary must still prove that the preceding tick was reached; an abort is not an implicit clock advance.
        for trace in (oracle, host):
            trace.insert(-1, _ev(1, "m1", "dispose"))
            _refoot(trace)
        require(compare(oracle, host, scenario=scenario), "aborted", "future empty population after an unrelated abort")
        oracle, host, scenario = _input_reference()
        for trace in (oracle, host):
            _cut_reference(trace, 2)
            trace[1:-1] = [row for row in trace[1:-1] if row.get("sid") != "m1.b1"]
            _refoot(trace)
        require(compare(oracle, host, scenario=scenario), "aborted", "missing child snapshot is not inactive-target evidence")
        oracle, host, scenario = _input_reference()
        for trace in (oracle, host):
            _cut_reference(trace, 2)
        require(compare(oracle, host[:-1], scenario=scenario), "scenario-invalid", "valid peer proves invalid input before truncation")
        require(compare(oracle[:-1], host[:-1], scenario=scenario), "truncated", "WITNESS two truncated traces supply no input proof")
        # A gap in an earlier starter line remains malformed even though both runs stopped before N.
        oracle, host, scenario = _input_reference(3, "unknown")
        for trace in (oracle, host):
            _cut_reference(trace, 3)
        host[1:-1] = [row for row in host[1:-1] if not (
            row.get("type") == "state" and row["tick"] == 1 and row["sid"] == "m1")]
        _refoot(host)
        require(compare(oracle, host, scenario=scenario), "malformed", "an incomplete prefix hid a state gap")
        oracle, host, scenario = _input_reference()
        for trace in (oracle, host):
            _cut_reference(trace, 2)
        require(compare(oracle, host, scenario=scenario), "scenario-invalid", "WITNESS complete pending prefix")
        for status, expected in (("timeout", "timeout"), ("error", "aborted"), ("aborted", "aborted")):
            oracle, host, scenario = _input_reference(2, "m1")
            _cut_reference(host, 2, status)
            require(compare(oracle, host, scenario=scenario), expected, "WITNESS active input preserves " + status)
    elif name == "P2 physical input CLI":
        import contextlib
        import io
        import tempfile
        with tempfile.TemporaryDirectory(prefix="parity-p2-cli-") as folder:
            root = Path(folder)
            (root / "pack").mkdir()
            (root / "pack" / "synthetic.txt").write_text("synthetic comparer fixture\n", encoding="utf-8")
            oracle, host, scenario = _input_reference()
            scenario["pack"] = "pack"
            scenario_bytes = json.dumps(scenario).encode("utf-8")
            (root / "scenario.json").write_bytes(scenario_bytes)
            for runtime, trace in (("oracle", oracle), ("host", host)):
                _cut_reference(trace, 2)
                trace[0]["scenario"].update(sha256=sha256_bytes(scenario_bytes), packSha256=pack_sha256(root / "pack"))
                (root / (runtime + ".jsonl")).write_text("\n".join(json.dumps(row) for row in trace) + "\n", encoding="utf-8")
            output = io.StringIO()
            arguments = ["compare.py", str(root / "oracle.jsonl"), str(root / "host.jsonl")]
            with contextlib.redirect_stdout(output):
                status = main(arguments + ["--scenario", str(root / "scenario.json"), "--json", str(root / "report.json")])
            report = json.loads((root / "report.json").read_text(encoding="utf-8"))
            require(report, "scenario-invalid", "physical JSONL with supplied scenario")
            _case_require(status == 2 and "unjudgeable (scenario-invalid)" in output.getvalue(), name,
                          "CLI did not expose scenario-invalid and exit 2")
            with contextlib.redirect_stdout(io.StringIO()):
                status = main(arguments + ["--json", str(root / "report.json")])
            require(json.loads((root / "report.json").read_text(encoding="utf-8")), "aborted",
                    "WITNESS same physical JSONL without scenario")
            _case_require(status == 2, name, "generic abort CLI exit changed")
    elif name == "P2 script pending":
        for rng in ({"kind": "tape", "values": [0.5]},):
            for script_runtime in ("oracle", "host", "both"):
                oracle, host = _reference({"kind": "tape", "values": [0.5]})
                for runtime, trace in (("oracle", oracle), ("host", host)):
                    trace[0]["rng"] = rng
                    if script_runtime in (runtime, "both"):
                        next(row for row in trace if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
                report = compare(oracle, host)
                require(report, "script-site-unmapped", "script site on " + script_runtime + " with " + rng["kind"], "pending")
                _case_require(report["findings"][0]["class"] == "script-site-unmapped", name,
                              "pending finding lost its diagnostic class")
        oracle, host = _reference({"kind": "splitmix64", "seed": "0x0123456789abcdef"})
        for trace in (oracle, host):
            next(row for row in trace if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
        require(compare(oracle, host), None, "WITNESS free-running claim remains time-share", "time-share-pass")
        oracle, host = _reference({"kind": "tape", "values": [0.5]})
        require(compare(oracle, host), None, "WITNESS matching builtin draws remain exact", "exact")
        next(row for row in host if row.get("kind") == "rng-draw")["site"] = "builtin:other"
        require(compare(oracle, host), "event-mismatch", "WITNESS builtin mismatches still fail", "fail")
        for site in ("Script:Alpha#label#1", "other:script:Alpha#label#1"):
            oracle, host = _reference({"kind": "tape", "values": [0.5]})
            next(row for row in host if row.get("kind") == "rng-draw")["site"] = site
            require(compare(oracle, host), "event-mismatch", "WITNESS only exact script prefix defers", "fail")
    elif name == "P2 script precedence":
        oracle, host = _reference({"kind": "tape", "values": [0.5]})
        next(row for row in host if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
        scenario = _scenario()
        scenario["rng"] = oracle[0]["rng"]
        cases = (
            ("build-witness-missing", lambda o, h, s: h[0].pop("build"), {}),
            ("source-pin-mismatch", lambda o, h, s: o[0]["build"].update(sourceZipSha256=H("0")), {}),
            ("malformed", lambda o, h, s: h[-1].update(states=999), {}),
            ("rng-invalid", lambda o, h, s: next(row for row in h if row.get("kind") == "rng-draw").update(value=1.25), {}),
            ("scenario-invalid", lambda o, h, s: s["input"].append({"tick": 2, "sid": "m1.b1", "click": "left"}), {}),
            ("scenario-hash-mismatch", lambda o, h, s: None, {"scenario_sha": H("c")}),
            ("pack-hash-mismatch", lambda o, h, s: None, {"pack_sha": H("d")}),
            ("artifact-hash-mismatch", lambda o, h, s: None, {"artifacts": {"host": H("9")}}),
            ("scenario-start-mismatch", lambda o, h, s: _where(h, 0, "m1")[0].update(pending=True), {}),
            ("aborted", lambda o, h, s: _cut_reference(h, 2), {}),
            ("timeout", lambda o, h, s: _cut_reference(h, 2, "timeout"), {}),
            ("aborted", lambda o, h, s: _cut_reference(h, 2, "error"), {}),
            ("truncated", lambda o, h, s: h.pop(), {}),
        )
        for expected, change, arguments in cases:
            o, h, s = copy.deepcopy((oracle, host, scenario))
            change(o, h, s)
            require(compare(o, h, scenario=s, **arguments), expected, "script pending hid " + expected)
        require(compare(oracle, host, scenario=scenario), "script-site-unmapped", "WITNESS valid bound script pair", "pending")
    elif name == "P2 script independent failures":
        # These existing failures require no source-site identity. Each stays a failure with a script draw
        # on either side, including equal script sites that would otherwise establish an unjustified exact claim.
        for rng, expected in (({"kind": "none"}, "rng-unexpected-draw"),
                              ({"kind": "tape", "values": []}, "rng-tape-exhausted")):
            for drawn_runtime in ("oracle", "host", "both"):
                oracle, host = _reference({"kind": "tape", "values": [0.5]})
                for runtime, trace in (("oracle", oracle), ("host", host)):
                    trace[0]["rng"] = rng
                    if drawn_runtime in (runtime, "both"):
                        next(row for row in trace if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
                    else:
                        trace[1:-1] = [row for row in trace[1:-1] if row.get("kind") != "rng-draw"]
                        _refoot(trace)
                require(compare(oracle, host), expected, drawn_runtime + " script draw with " + rng["kind"], "fail")
        for extra_runtime in ("oracle", "host"):
            for values, expected in (([0.5], "rng-tape-exhausted"), ([0.5, 0.5], "rng-draw-count")):
                oracle, host = _reference({"kind": "tape", "values": values})
                extra = oracle if extra_runtime == "oracle" else host
                extra.insert(3, _ev(1, "m1", "rng-draw", site="script:Alpha#label#1", occurrence=2,
                                    value=0.5, purpose="script draw"))
                _refoot(extra)
                require(compare(oracle, host), expected, extra_runtime + " extra script draw", "fail")
        oracle, host = _reference({"kind": "tape", "values": [0.5]})
        next(row for row in host if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
        require(compare(oracle, host), "script-site-unmapped", "WITNESS valid equal draw counts still defer", "pending")
    elif name == "P2 script refusal before draw":
        # The real oracle can refuse an unattributed script call before logging a draw. A generic aborted footer
        # carries no attribution evidence; neither a peer's script draw nor its absence turns the refusal pending.
        for failed_runtime in ("oracle", "host"):
            for peer_script in (False, True):
                oracle, host = _reference({"kind": "tape", "values": [0.5]})
                failed, peer = (oracle, host) if failed_runtime == "oracle" else (host, oracle)
                _cut_reference(failed, 1)
                if peer_script:
                    next(row for row in peer if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
                scenario = _scenario()
                scenario["rng"] = oracle[0]["rng"]
                for supplied in (None, scenario):
                    require(compare(oracle, host, scenario=supplied), "aborted",
                            failed_runtime + " pre-draw refusal, peer script=" + str(peer_script)
                            + ", scenario supplied=" + str(supplied is not None))
    elif name == "P2 script pending CLI":
        import contextlib
        import io
        import tempfile
        with tempfile.TemporaryDirectory(prefix="parity-p2-script-cli-") as folder:
            root = Path(folder)
            oracle, host = _reference({"kind": "tape", "values": [0.5]})
            next(row for row in host if row.get("kind") == "rng-draw")["site"] = "script:Alpha#label#1"
            for runtime, trace in (("oracle", oracle), ("host", host)):
                (root / (runtime + ".jsonl")).write_text("\n".join(json.dumps(row) for row in trace) + "\n", encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                status = main(["compare.py", str(root / "oracle.jsonl"), str(root / "host.jsonl"),
                               "--json", str(root / "report.json")])
            report = json.loads((root / "report.json").read_text(encoding="utf-8"))
            require(report, "script-site-unmapped", "physical JSONL pending diagnostic", "pending")
            _case_require(status == 1 and "S-selftest: pending (script-site-unmapped)" in output.getvalue(), name,
                          "pending CLI did not expose the diagnostic class and non-success exit")
    else:
        raise ValueError("unknown parity-2 check " + name)


def _r2_check(name):
    def require(report, expected, detail):
        _case_require(report["verdict"] == "unjudgeable" and report["class"] == expected, name,
                      detail + ": " + repr((report["verdict"], report["class"])))

    def empty_run(status="aborted"):
        oracle, host = _reference({"kind": "tape", "values": [0.5]})
        _cut_reference(oracle, 0, status)
        _cut_reference(host, 0, status)
        scenario = _scenario()
        scenario["rng"] = oracle[0]["rng"]
        return oracle, host, scenario

    def removed_pair(target="m1", button="left", dispose_sid="m1", dispose_tick=1, edge_tick=4):
        oracle, host, scenario = _input_reference(edge_tick, target, button)
        for trace in (oracle, host):
            _cut_reference(trace, 1)
            trace.insert(-1, _ev(dispose_tick, dispose_sid, "dispose"))
            _refoot(trace)
        return oracle, host, scenario

    def truncated_pair(runtime="host", rng=None):
        oracle, host, scenario = _input_reference()
        if rng is not None:
            oracle, host = _reference(rng)
            scenario["rng"] = rng
        for trace in (oracle, host):
            _cut_reference(trace, 2)
        (oracle if runtime == "oracle" else host).pop()
        return oracle, host, scenario

    if name == "R2 zero-state runs":
        for status, expected in (("aborted", "aborted"), ("error", "aborted"), ("timeout", "timeout")):
            for failed_runtime in ("oracle", "host", "both"):
                oracle, host = _reference({"kind": "tape", "values": [0.5]})
                scenario = _scenario()
                scenario["rng"] = oracle[0]["rng"]
                for runtime, trace in (("oracle", oracle), ("host", host)):
                    if failed_runtime in (runtime, "both"):
                        _cut_reference(trace, 0, status)
                        try:
                            load_trace(trace, runtime)
                        except Unjudgeable as failure:
                            _case_require(failure.cls == expected, name, "standalone zero-state status was lost")
                        else:
                            _case_require(False, name, "standalone failed run was accepted")
                for supplied in (None, scenario):
                    require(compare(oracle, host, scenario=supplied), expected,
                            failed_runtime + " zero-state " + status + ", scenario=" + str(supplied is not None))
    elif name == "R2 zero-state validation":
        oracle, host, scenario = empty_run()
        cases = (
            ("missing-field", lambda o, h: h[-1].pop("events"), {}),
            ("malformed", lambda o, h: h[-1].update(states=1), {}),
            ("build-witness-missing", lambda o, h: h[0].pop("build"), {}),
            ("source-pin-mismatch", lambda o, h: o[0]["build"].update(sourceZipSha256=H("0")), {}),
            ("scenario-hash-mismatch", lambda o, h: None, {"scenario_sha": H("c")}),
            ("pack-hash-mismatch", lambda o, h: None, {"pack_sha": H("d")}),
            ("artifact-hash-mismatch", lambda o, h: None, {"artifacts": {"host": H("9")}}),
            ("tick-count-mismatch", lambda o, h: h[-1].update(ticks=3), {}),
        )
        for expected, change, arguments in cases:
            o, h = copy.deepcopy((oracle, host))
            change(o, h)
            require(compare(o, h, scenario=scenario, **arguments), expected, "zero-state run hid " + expected)
        oracle, host, scenario = empty_run("complete")
        require(compare(oracle, host, scenario=scenario), "malformed", "WITNESS complete run still requires tick 0")
        oracle, host = _reference()
        _cut_reference(host, 2)
        host[1:-1] = [row for row in host[1:-1] if row.get("type") != "state" or row["tick"] != 0]
        _refoot(host)
        _revelocity(host)
        require(compare(oracle, host), "malformed", "WITNESS nonempty failed prefix still requires tick 0")
        oracle, host = _reference()
        _cut_reference(host, 2)
        _where(host, 1, "m1")[0]["velocity"] = [999, 0]
        require(compare(oracle, host), "malformed", "WITNESS nonempty failed prefix still checks velocity")
    elif name == "R2 disposed targets":
        for edge in BUTTON_EDGES:
            for proof_runtime in ("oracle", "host", "both"):
                oracle, host, scenario = removed_pair()
                scenario["input"][-1] = {"tick": 4, "sid": "m1", edge: "left"}
                for runtime, trace in (("oracle", oracle), ("host", host)):
                    if proof_runtime not in (runtime, "both"):
                        trace[1:-1] = [row for row in trace[1:-1] if row.get("kind") != "dispose"]
                        _refoot(trace)
                require(compare(oracle, host, scenario=scenario), "scenario-invalid",
                        proof_runtime + " sole companion disposed before later " + edge)
        for target, dispose_sid, button in (("unknown", "unknown", "left"), ("m1.b1", "m1.b1", "left"),
                                             ("m1", "other", "left"), ("m1", "m1", "right")):
            oracle, host, scenario = removed_pair(target, button, dispose_sid)
            require(compare(oracle, host, scenario=scenario), "aborted", "WITNESS unrelated or unobserved removal/right edge")
        oracle, host, scenario = removed_pair(dispose_tick=2, edge_tick=1)
        require(compare(oracle, host, scenario=scenario), "aborted", "WITNESS disposal after the input is not evidence")
        for last_tick in (1, 2):
            for contradicted_runtime in ("oracle", "host", "both"):
                oracle, host, scenario = removed_pair()
                for runtime, trace in (("oracle", oracle), ("host", host)):
                    if contradicted_runtime in (runtime, "both"):
                        for tick in range(1, last_tick + 1):
                            trace.insert(-1, _state(tick, "m1", "Walk", "Walk", tick, 640, 640))
                        _resort(trace)
                    else:
                        trace[1:-1] = [row for row in trace[1:-1] if row.get("kind") != "dispose"]
                    _refoot(trace)
                require(compare(oracle, host, scenario=scenario), "malformed",
                        "WITNESS " + contradicted_runtime + " state at/after disposal contradicts removal: " + str(last_tick))
        oracle, host, scenario = _input_reference(4, "m1.b1")
        for trace in (oracle, host):
            _cut_reference(trace, 2)
        require(compare(oracle, host, scenario=scenario), "aborted", "WITNESS previously pending child is not permanently inactive")
        for trace in (oracle, host):
            trace.insert(-1, _ev(2, "m1.b1", "dispose"))
            _refoot(trace)
        require(compare(oracle, host, scenario=scenario), "scenario-invalid", "observed pending child was then removed")
    elif name == "R2 truncated peer proof":
        # Truncated records are validated but never used as positive proof, even when their dispose event
        # would establish an irreversible removal in a trace with a footer.
        oracle, host, scenario = removed_pair()
        oracle.pop()
        host.pop()
        require(compare(oracle, host, scenario=scenario), "truncated", "two truncated removed-target prefixes are not proof")
        for runtime in ("oracle", "host"):
            oracle, host, scenario = truncated_pair(runtime)
            require(compare(oracle, host, scenario=scenario), "scenario-invalid", runtime + " truncation follows valid peer proof")
            require(compare(oracle, host), "truncated", "WITNESS no supplied scenario preserves " + runtime + " truncation")
            truncated = oracle if runtime == "oracle" else host
            try:
                load_trace(truncated, runtime)
            except Unjudgeable as failure:
                _case_require(failure.cls == "truncated", name, "standalone reader lost missing-footer status")
            else:
                _case_require(False, name, "standalone reader accepted a truncated trace")
            truncated[1:] = []
            require(compare(oracle, host, scenario=scenario), "scenario-invalid", "header-only peer still binds before valid proof")
        oracle, host, scenario = truncated_pair()
        oracle.pop()
        require(compare(oracle, host, scenario=scenario), "truncated", "WITNESS both pending prefixes are truncated")
        oracle, host, scenario = truncated_pair()
        scenario["input"][-1]["sid"] = "m1"
        require(compare(oracle, host, scenario=scenario), "truncated", "WITNESS active target supplies no scenario error")
    elif name == "R2 truncated validation":
        for runtime in ("oracle", "host"):
            oracle, host, scenario = truncated_pair(runtime, {"kind": "tape", "values": [0.5]})
            cases = (
                ("missing-field", lambda t: _where(t, 1, "m1")[0].pop("actionAge"), {}),
                ("malformed", lambda t: _where(t, 1, "m1")[0].update(velocity=[999, 0]), {}),
                ("build-witness-missing", lambda t: t[0].pop("build"), {}),
                ("rng-invalid", lambda t: next(row for row in t if row.get("kind") == "rng-draw").update(value=1.25), {}),
                ("scenario-hash-mismatch", lambda t: None, {"scenario_sha": H("c")}),
                ("pack-hash-mismatch", lambda t: None, {"pack_sha": H("d")}),
                ("artifact-hash-mismatch", lambda t: None, {"artifacts": {runtime: H("9")}}),
            )
            for expected, change, arguments in cases:
                o, h = copy.deepcopy((oracle, host))
                change(o if runtime == "oracle" else h)
                require(compare(o, h, scenario=scenario, **arguments), expected, runtime + " truncated trace hid " + expected)
            o, h = copy.deepcopy((oracle, host))
            o[0]["build"]["sourceZipSha256"] = H("0")
            require(compare(o, h, scenario=scenario), "source-pin-mismatch", "truncation hid the oracle source pin")
            o, h = copy.deepcopy((oracle, host))
            truncated = o if runtime == "oracle" else h
            truncated[0]["scenario"]["sha256"] = H("c")
            require(compare(o, h, scenario=scenario), "scenario-hash-mismatch", "truncated header must bind to its peer")
            o, h = copy.deepcopy((oracle, host))
            (h if runtime == "oracle" else o)[-1]["ticks"] = 3
            require(compare(o, h, scenario=scenario), "tick-count-mismatch", "the remaining footer still binds scenario N")
            o, h = copy.deepcopy((oracle, host))
            (o if runtime == "oracle" else h).append(_ev(5, "m1", "error", message="late event"))
            require(compare(o, h, scenario=scenario), "malformed", "scenario N still bounds the truncated body")
            require(compare(o, h), "malformed", "peer N still bounds the truncated body without scenario")
        oracle, host, scenario = truncated_pair()
        bad = copy.deepcopy(scenario)
        bad["input"] = [{"tick": 0, "sid": "m1", "click": "left"}]
        report = compare(oracle, host, scenario=bad)
        require(report, "scenario-invalid", "scenario syntax remains first")
        _case_require("tick-0" in report["findings"][0]["message"], name, "trace inference hid scenario syntax error")
    else:
        raise ValueError("unknown parity review check " + name)


def _final_check(name):
    def require(report, expected, detail, verdict="unjudgeable"):
        _case_require(report["verdict"] == verdict and report["class"] == expected, name,
                      detail + ": " + repr((report["verdict"], report["class"])))

    def interrupted(trace, tick):
        stop = next(index for index, row in enumerate(trace) if row.get("type") == "state"
                    and row["tick"] == tick and row["sid"] == "m1.b1")
        trace[:] = trace[:stop]  # m1's final state is present; the second companion's is not.

    if name == "F3 interrupted snapshot":
        for runtime in ("oracle", "host"):
            oracle, host = _reference()
            scenario = _scenario()
            cut = oracle if runtime == "oracle" else host
            interrupted(cut, 3)
            for supplied in (None, scenario):
                require(compare(oracle, host, scenario=supplied), "truncated", runtime + " final snapshot cut between state rows")
            o, h = copy.deepcopy((oracle, host))
            later = o if runtime == "oracle" else h
            later.append(_ev(4, "m1", "error", message="next tick was reached"))
            for supplied in (None, scenario):
                report = compare(o, h, scenario=supplied)
                require(report, "malformed", "WITNESS later event proves the incomplete snapshot already ended")
                _case_require("with no dispose event at tick 3" in report["findings"][0]["message"], name,
                              "later-event witness did not reach the missing population check")
            o, h = copy.deepcopy((oracle, host))
            early = o if runtime == "oracle" else h
            early[:] = [row for row in early if not (row.get("type") == "state"
                       and row["tick"] == 2 and row["sid"] == "m1.b1")]
            for supplied in (None, scenario):
                require(compare(o, h, scenario=supplied), "malformed", "WITNESS earlier missing population remains malformed")
            for status in ("aborted", "complete"):
                o, h = copy.deepcopy((oracle, host))
                declared = o if runtime == "oracle" else h
                declared.append({"type": "footer", "status": status, "ticks": 3, "draws": 0, "states": 0, "events": 0})
                _refoot(declared)
                require(compare(o, h), "malformed", "WITNESS real footer retains complete snapshot rule: " + status)
            o, h = copy.deepcopy((oracle, host))
            bad = o if runtime == "oracle" else h
            _where(bad, 2, "m1.b1")[0]["velocity"] = [999, 0]
            require(compare(o, h), "malformed", "WITNESS interrupted tail still checks its recorded velocity")
            oracle, host = _reference()
            scenario = _scenario()
            scenario["input"].append({"tick": 2, "sid": "m1.b1", "click": "left"})
            cut, valid = (oracle, host) if runtime == "oracle" else (host, oracle)
            interrupted(cut, 2)
            _cut_reference(valid, 2)
            require(compare(oracle, host, scenario=scenario), "scenario-invalid", runtime + " interrupted peer cannot hide valid input proof")
            require(compare(oracle, host), "truncated", "WITNESS interrupted peer without scenario remains truncated")
        oracle, host = _reference()
        for trace in (oracle, host):
            interrupted(trace, 3)
        require(compare(oracle, host, scenario=_scenario()), "truncated", "WITNESS both interrupted snapshots remain truncated")
    elif name == "F3 disposal lifecycle":
        for runtime in ("both", "oracle", "host"):
            for tick in (2, 4):
                oracle, host = _reference()
                for side, trace in (("oracle", oracle), ("host", host)):
                    if runtime in (side, "both"):
                        trace.insert(-1, _ev(tick, "m1", "dispose"))
                        _resort(trace)
                        _refoot(trace)
                report = compare(oracle, host)
                require(report, "malformed", runtime + " state at/after own dispose")
                _case_require("state at or after dispose tick" in report["findings"][0]["message"], name,
                              "the lifecycle refusal was hidden by another check")
        oracle, host = _reference()
        require(compare(oracle, host), None, "WITNESS dispose-request and disposing state before removal are valid", "exact")
        # A tentative child's behavior, RNG and dispose-request events are not removal. Its refused sid is
        # deliberately reused by the next successful child, which must still have valid later state lines.
        oracle, host = _reference({"kind": "tape", "values": [0.5, 0.5]})
        for trace in (oracle, host):
            next(row for row in trace if row.get("kind") == "rng-draw")["occurrence"] = 2
            next(row for row in trace if row.get("kind") == "birth-request")["attempt"] = 2
            trace[2:2] = [_ev(1, "m1.b1", "behavior", **{"from": None, "to": "Failed", "cause": "forced"}),
                          _ev(1, "m1.b1", "rng-draw", site="builtin:behavior-select", occurrence=1,
                              value=0.5, purpose="tentative child"),
                          _ev(1, "m1.b1", "dispose-request", reason="failed birth"),
                          _ev(1, "m1", "birth-refused", child=None, attempt=1, reason="error")]
            _refoot(trace)
        require(compare(oracle, host), None, "WITNESS refused tentative sid reuse with request is not disposal", "exact")
    else:
        raise ValueError("unknown final comparer check " + name)


def _trace2_reference(rng=None):
    pair = _reference(rng)
    for trace in pair:
        trace[0]["schema"] = SCHEMA_TRACE_V2
        _trace2_refoot(trace)
    return pair


def _trace2_refoot(trace):
    _refoot(trace)
    rng = trace[0]["rng"]
    trace[-1]["tapeExhausted"] = rng["kind"] == "tape" and trace[-1]["draws"] > len(rng["values"])


def _trace2_add_error(trace, code="behavior-missing", lookup=None, via=None):
    if lookup is None and code != "unclassified":
        lookup = {"imageSet": "Beta", "behavior": ""}
    event = _ev(0, "m1", "error", message="synthetic diagnostic",
                error={"code": code, "lookup": lookup, "via": [] if via is None else via})
    trace.insert(1, event)
    _trace2_refoot(trace)
    return event


def _trace2_cli(pair, tolerances=None):
    import contextlib
    import io
    import tempfile
    with tempfile.TemporaryDirectory(prefix="trace2-cli-") as directory:
        pair = copy.deepcopy(pair)
        options = []
        if tolerances is not None:
            scenario = _scenario()
            scenario["rng"] = copy.deepcopy(pair[0][0]["rng"])
            scenario["pack"] = "pack"
            scenario["tolerances"] = tolerances
            pack = Path(directory) / "pack"
            pack.mkdir()
            (pack / "synthetic.txt").write_text("synthetic CLI binding witness", encoding="utf-8")
            scenario_path = Path(directory) / "scenario.json"
            scenario_bytes = json.dumps(scenario).encode("utf-8")
            scenario_path.write_bytes(scenario_bytes)
            for trace in pair:
                trace[0]["scenario"].update(sha256=sha256_bytes(scenario_bytes), packSha256=pack_sha256(pack))
            options = ["--scenario", str(scenario_path)]
        paths = [Path(directory) / (runtime + ".jsonl") for runtime in ("oracle", "host")]
        for path, trace in zip(paths, pair):
            path.write_text("\n".join(row if isinstance(row, str) else json.dumps(row) for row in trace) + "\n", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            return main(["compare.py", str(paths[0]), str(paths[1])] + options)


def _trace2_check(name):
    def require(report, cls=None, verdict="exact", detail=""):
        _case_require((report["verdict"], report["class"]) == (verdict, cls), name,
                      detail + ": " + repr((report["verdict"], report["class"])))

    def invalid(pair, detail):
        try:
            report = compare(*pair)
        except Exception as error:
            raise RuntimeError("bd-codex-parity: " + name + ": " + detail
                               + " escaped validation as " + type(error).__name__) from error
        require(report, "missing-field", "unjudgeable", detail)

    if name == "T2 CC1":
        for code, lookup in (("behavior-missing", {"imageSet": "Beta", "behavior": None}),
                             ("action-missing", {"imageSet": "Alpha", "action": " "}),
                             ("variant-missing", {"refKey": REF(2)}),
                             ("parameter-invalid", {"refKey": REF(2), "parameter": "Duration"}),
                             ("scan-target-missing", {"refKey": REF(2)})):
            pair = _trace2_reference()
            for trace in pair:
                _trace2_add_error(trace, code, copy.deepcopy(lookup))
            require(compare(*pair), detail="WITNESS valid operands " + code)
            for variant in ("missing", "extra"):
                bad = copy.deepcopy(pair)
                operands = bad[1][1]["error"]["lookup"]
                operands.pop(next(iter(operands))) if variant == "missing" else operands.update(extra="no")
                invalid(bad, code + " " + variant + " operand")
    elif name == "T2 CC2":
        pair = _trace2_reference()
        for trace in pair:
            _trace2_add_error(trace)
        require(compare(*pair), detail="WITNESS valid scalar types")
        mutations = (("message", ""), ("message", 42), ("sid", 42), ("tick", False), ("error", None), ("error", []),
                     ("error", {"code": "behavior-missing", "lookup": {"imageSet": {}, "behavior": ""}, "via": []}),
                     ("error", {"code": "behavior-missing", "lookup": {"imageSet": "", "behavior": ""}, "via": []}),
                     ("error", {"code": "behavior-missing", "lookup": {"imageSet": "Beta", "behavior": 0}, "via": []}),
                     ("error", {"code": "variant-missing", "lookup": {"refKey": ""}, "via": []}),
                     ("error", {"code": "variant-missing", "lookup": {"refKey": "not-a-source-key"}, "via": []}),
                     ("error", {"code": "variant-missing", "lookup": {"refKey": "Alpha|../actions.xml|/Mascot[1]"}, "via": []}),
                     ("error", {"code": "parameter-invalid", "lookup": {"refKey": REF(1), "parameter": ""}, "via": []}),
                     ("error", {"code": "behavior-missing", "lookup": {"imageSet": "Beta", "behavior": ""}, "via": "birth"}),
                     ("error", {"code": "behavior-missing", "lookup": {"imageSet": "Beta", "behavior": ""}, "via": ["unknown"]}))
        for field, value in mutations:
            bad = copy.deepcopy(pair)
            bad[1][1][field] = value
            invalid(bad, "invalid scalar " + repr(value))
    elif name == "T2 CC3":
        for code, field in (("behavior-missing", "behavior"), ("action-missing", "action")):
            for a, b in ((None, ""), (" ", "")):
                pair = _trace2_reference()
                for trace, value in zip(pair, (a, b)):
                    _trace2_add_error(trace, code, {"imageSet": "Beta", field: value})
                report = compare(*pair)
                require(report, "event-mismatch", "fail", "raw requested name distinctions")
                finding = report["findings"][0]
                _case_require(finding["oracle"].get("error", {}).get("lookup", {}).get(field, "absent") == a
                              and finding["host"].get("error", {}).get("lookup", {}).get(field, "absent") == b,
                              name, "diagnostic must carry the actual differing typed operands")
    elif name == "T2 CC4":
        pair = _trace2_reference()
        for trace in pair:
            _trace2_add_error(trace)
        for key, replacement in (("code", "future-code"), ("surplus", True)):
            bad = copy.deepcopy(pair)
            bad[1][1]["error"][key] = replacement
            invalid(bad, "closed error property/code vocabulary")
        for old, new in (('"code": "behavior-missing"', '"code": "behavior-missing", "code": "behavior-missing"'),
                         ('"imageSet": "Beta"', '"imageSet": "Beta", "imageSet": "Beta"'),
                         ('"error": {', '"error": {"code": "future-code", "lookup": 7, "via": "x"}, "error": {')):
            serialized = [[json.dumps(row) for row in trace] for trace in pair]
            _case_require(serialized[1][1].count(old) == 1, name, "duplicate fixture is not unique")
            serialized[1][1] = serialized[1][1].replace(old, new)
            invalid(serialized, "duplicate property cannot be last-wins")
    elif name == "T2 CC5":
        for code, lookup in (("unclassified", {"imageSet": "Alpha"}), ("behavior-missing", None)):
            pair = _trace2_reference()
            for trace in pair:
                event = _trace2_add_error(trace)
                event["error"].update(code=code, lookup=lookup)
            invalid(pair, "lookup null rule " + code)
    elif name in ("T2 CC6", "T2 CC16"):
        for version in (SCHEMA_TRACE, SCHEMA_TRACE_V2):
            pair = _trace2_reference()
            for trace in pair:
                _trace2_add_error(trace)
                trace[0]["schema"] = version
                if version == SCHEMA_TRACE:
                    trace[1].pop("error")  # The old artifact has only its message; no new shape is required.
            pair[1][1]["message"] = "different own-runtime wording"
            require(compare(*pair), None if version == SCHEMA_TRACE_V2 else "event-mismatch",
                    "exact" if version == SCHEMA_TRACE_V2 else "fail", "versioned message meaning")
    elif name == "T2 CC7":
        pair = _trace2_reference()
        pair[0][0]["schema"] = SCHEMA_TRACE
        pair[1][0].pop("build")
        require(compare(*pair), "trace-version-mismatch", "unjudgeable", "version before build")
        _case_require(_trace2_cli(pair) == 2, name, "mixed-version CLI exit must be 2")
        for side in (0, 1):
            mixed = _trace2_reference()
            mixed[side][0]["schema"] = SCHEMA_TRACE
            mixed[1 - side][-1]["unobservedDraws"] = 0
            require(compare(*mixed), "trace-version-mismatch", "unjudgeable", "mixed version before off marker")
        for index in (0, 1):
            bad = copy.deepcopy(pair)
            bad[index][0]["schema"] = "not-a-trace-version"
            invalid(bad, "unknown schema must stay missing-field")
    elif name == "T2 CC8":
        for codes in (("unclassified", "behavior-missing"), ("behavior-missing", "unclassified"),
                      ("unclassified", "unclassified")):
            for tolerance in (False, True):
                pair = _trace2_reference()
                for trace, code in zip(pair, codes):
                    _trace2_add_error(trace, code)
                options = {}
                if tolerance:
                    for state in pair[1]:
                        if state.get("type") == "state":
                            state["anchor"][0] += 1
                    options["tolerances"] = {"anchor": 1}
                require(compare(*pair, **options), "error-unclassified", "unjudgeable", "aligned cause blocks success")
                _case_require(_trace2_cli(pair, options.get("tolerances")) == 2,
                              name, "unknown-cause CLI exit must be 2")
                finding = compare(*pair, **options)["findings"][0]
                _case_require(finding.get("tick") == 0 and finding.get("sid") == "m1"
                              and finding.get("via") == []
                              and (finding.get("oracle"), finding.get("host")) == codes,
                              name, "unknown-cause diagnostic identifies the aligned pair")
                _case_require(all(part in finding.get("message", "") for part in
                                  ("S-selftest", "tick 0", "m1", "via []", repr(codes[0]), repr(codes[1]))),
                              name, "unknown-cause CLI message identifies the aligned pair")
    elif name == "T2 CC9":
        pair = _trace2_reference()
        _trace2_add_error(pair[0], "unclassified")
        require(compare(*pair), "missing-event", "fail", "missing unknown error")
        pair = _trace2_reference()
        _trace2_add_error(pair[1], "unclassified")
        require(compare(*pair), "extra-event", "fail", "extra unknown error")
        for field, value in (("tick", 1), ("sid", "m2"), ("via", ["birth"])):
            pair = _trace2_reference()
            for trace in pair:
                _trace2_add_error(trace, "unclassified")
            if field == "via":
                pair[1][1]["error"][field] = value
            else:
                pair[1][1][field] = value
                if field == "tick":
                    pair[1].insert(2, pair[1].pop(1))
            require(compare(*pair), "event-mismatch", "fail", "unknown identity mismatch " + field)
    elif name == "T2 CC10":
        for state_failure in (False, True):
            pair = _trace2_reference()
            for trace in pair:
                _trace2_add_error(trace, "unclassified")
            if state_failure:
                next(row for row in pair[1] if row.get("type") == "state" and row["tick"] == 2 and row["sid"] == "m1")["actionAge"] += 1
            else:
                next(row for row in pair[1] if row.get("kind") == "transform")["behavior"] = "other"
            require(compare(*pair), "state-mismatch" if state_failure else "event-mismatch", "fail",
                    "later independently established failure wins")
    elif name == "T2 CC11":
        pair = _trace2_reference()
        for status, want in (("complete", "malformed"), ("aborted", "aborted"), ("error", "aborted")):
            bad = copy.deepcopy(pair)
            bad[0][1:-1] = []
            bad[0][-1]["status"] = status
            _trace2_refoot(bad[0])
            require(compare(*bad, scenario=_scenario()), want, "unjudgeable", "zero-state " + status)
        for status in (None, "timeout", "aborted", "error"):
            oracle, host, scenario = _two_starter_reference()
            pair = (oracle, host)
            for trace in pair:
                trace[0]["schema"] = SCHEMA_TRACE_V2
                _trace2_refoot(trace)
            footer = copy.deepcopy(oracle[-1])
            oracle[:] = [oracle[0], oracle[1]]
            if status:
                oracle.append(dict(footer, status=status))
                _trace2_refoot(oracle)
            want = "truncated" if status is None else ("timeout" if status == "timeout" else "scenario-start-mismatch")
            require(compare(*pair, scenario=scenario), want, "unjudgeable", "proper interrupted startup prefix")
            if status in (None, "timeout"):
                for field, value in (("sid", "m2"), ("sid", "wrong"), ("pending", True)):
                    bad = copy.deepcopy(pair)
                    bad[0][1][field] = value
                    require(compare(*bad, scenario=scenario), "scenario-start-mismatch", "unjudgeable", "observed startup " + field)
                bad = copy.deepcopy(pair)
                event = _ev(1, "m1", "mute", clip="synthetic")
                bad[0].insert(-1, event) if status else bad[0].append(event)
                if status:
                    _trace2_refoot(bad[0])
                require(compare(*bad, scenario=scenario), "scenario-start-mismatch", "unjudgeable", "later record proves startup finished")
        for status in (None, "timeout", "aborted", "error"):
            pair = _trace2_reference()
            trace = pair[0]
            footer = copy.deepcopy(trace[-1])
            trace[:] = trace[:1]
            if status:
                trace.append(dict(footer, status=status))
                _trace2_refoot(trace)
            want = "truncated" if status is None else ("timeout" if status == "timeout" else "aborted")
            require(compare(*pair, scenario=_scenario()), want, "unjudgeable", "empty interrupted startup prefix")
            later = _ev(1, "m1", "mute", clip="later startup proof")
            trace.insert(-1, later) if status else trace.append(later)
            if status:
                _trace2_refoot(trace)
            require(compare(*pair, scenario=_scenario()), "scenario-start-mismatch" if status in (None, "timeout") else "aborted",
                    "unjudgeable", "later record proves empty interrupted startup finished")
    elif name == "T2 CC12":
        pair = _trace2_reference({"kind": "tape", "values": [0.5]})
        require(compare(*pair), detail="WITNESS non-exhausted tape")
        exhausted = _trace2_reference({"kind": "tape", "values": []})
        require(compare(*exhausted), "rng-tape-exhausted", "fail", "complete past-tape runs")
        for value in (0.7, 0.25, 0.0):
            bad = copy.deepcopy(exhausted)
            next(row for row in bad[1] if row.get("kind") == "rng-draw")["value"] = value
            require(compare(*bad), "rng-invalid", "unjudgeable", "past-tape value " + str(value))
        for original, flag in ((pair, True), (exhausted, False), (_trace2_reference(), True),
                               (_trace2_reference({"kind": "splitmix64", "seed": 1}), True)):
            bad = copy.deepcopy(original)
            bad[1][-1]["tapeExhausted"] = flag
            require(compare(*bad), "malformed", "unjudgeable", "forged exhaustion flag")
        bad = copy.deepcopy(pair)
        bad[1][-1].pop("tapeExhausted")
        invalid(bad, "required footer flag")
        bad = copy.deepcopy(pair)
        bad[1][-1]["tapeExhausted"] = 0
        invalid(bad, "boolean footer flag cannot be an integer")
    elif name == "T2 CC13":
        for tape, proven in (([], True), ([0.5], False)):
            pair = _trace2_reference({"kind": "tape", "values": tape})
            pair[0][-1]["status"] = "timeout"
            report = compare(*pair)
            require(report, "timeout", "unjudgeable", "run failure outranks exhaustion")
            secondary = [finding for finding in report["findings"] if finding.get("class") == "rng-tape-exhausted"]
            _case_require(bool(secondary) == proven, name, "secondary exhaustion requires a physical past-tape draw")
        for status in ("aborted", "error", None):
            pair = _trace2_reference({"kind": "tape", "values": []})
            if status is None:
                pair[1].pop()
            else:
                pair[1][-1]["status"] = status
            report = compare(*pair)
            require(report, "truncated" if status is None else "aborted", "unjudgeable",
                    "peer run failure retains its primary " + str(status))
            oracle_secondary = next(f for f in report["findings"] if f.get("runtime") == "oracle")
            _case_require("before its run failed" not in oracle_secondary["message"], name,
                          "completed runtime exhaustion diagnostic must not claim that runtime failed")
    elif name == "T2 CC14":
        for side in (0, 1):
            for counter in ("unobservedDraws", "unobservedEvents"):
                pair = _trace2_reference()
                pair[side][-1][counter] = 0
                pair[side][0].pop("build")
                require(compare(*pair), "trace-off-not-comparable", "unjudgeable", "off artifact before body")
                _case_require(_trace2_cli(pair) == 2, name, "off-artifact CLI exit must be 2")
                pair = _trace2_reference()
                pair[side][-1][counter] = 0
                pair[1 - side][-1] = "{"
                require(compare(*pair), "trace-off-not-comparable", "unjudgeable", "off artifact before unreadable peer tail")
        pair = _trace2_reference()
        pair[0][-1] = "{"
        invalid(pair, "WITNESS unreadable tail still fails when neither peer is off")
    elif name == "T2 CC15":
        pair = _trace2_reference({"kind": "splitmix64", "seed": 1})
        for trace in pair:
            _trace2_add_error(trace, "unclassified")
        require(compare(*pair), None, "time-share-pass", "unknown free-running error keeps separate claim")
        pair[1][1]["error"]["via"] = ["future-wrapper"]
        invalid(pair, "free-running still validates typed shape")
    elif name == "T2 CC17":
        pair = _trace2_reference()
        pair[1][1].pop("actionAge")
        scenario = _scenario()
        scenario["input"].append({"tick": 0, "click": "left", "sid": "m1"})
        require(compare(*pair, scenario=scenario), "scenario-invalid", "unjudgeable", "static scenario before body")
        pair[0][0]["build"]["sourceZipSha256"] = H("0")
        require(compare(*pair), "source-pin-mismatch", "unjudgeable", "oracle pin before host body")
        pair = _trace2_reference()
        next(row for row in pair[0] if row.get("type") == "state" and row["tick"] == 0 and row["sid"] == "m1")["pending"] = True
        scenario = _scenario()
        scenario["input"].append({"tick": 1, "click": "left", "sid": "absent"})
        require(compare(*pair, scenario=scenario), "scenario-start-mismatch", "unjudgeable", "starter before trace-derived input")
    elif name == "T2 trace1 freeze":
        for side in (0, 1):
            pair = _reference()
            trace = pair[side]
            _cut_reference(trace, 4)
            trace[1:-1] = [row for row in trace[1:-1]
                           if not (row.get("type") == "state" and row["tick"] == 3 and row["sid"] == "m1")]
            trace.pop()
            require(compare(*pair), "truncated", "unjudgeable",
                    "trace1 footerless non-suffix final snapshot " + str(side))
        for side in (0, 1):
            pair = _reference()
            trace = pair[side]
            footer = copy.deepcopy(trace[-1])
            stop = next(index for index, row in enumerate(trace) if row.get("type") == "state"
                        and row["tick"] == 3 and row["sid"] == "m1.b1")
            trace[:] = trace[:stop] + [dict(footer, status="timeout")]
            _refoot(trace)
            for supplied in (None, _scenario()):
                require(compare(*pair, scenario=supplied), "malformed", "unjudgeable",
                        "trace1 timeout mid-snapshot remains malformed")
        for side in (0, 1):
            for status, want in ((None, "truncated"), ("timeout", "scenario-start-mismatch")):
                oracle, host, scenario = _two_starter_reference()
                pair = (oracle, host)
                trace = pair[side]
                footer = copy.deepcopy(trace[-1])
                trace[:] = trace[:2]
                if status is not None:
                    trace.append(dict(footer, status=status))
                    _refoot(trace)
                require(compare(*pair, scenario=scenario), want, "unjudgeable",
                        "trace1 startup prefix " + str(status))
    elif name == "T2 occurrence":
        pair = _trace2_reference({"kind": "tape", "values": [0.5]})
        require(compare(*pair), detail="WITNESS integer occurrence")
        for occurrence in (1.0, True):
            bad = copy.deepcopy(pair)
            next(row for row in bad[1] if row.get("kind") == "rng-draw")["occurrence"] = occurrence
            require(compare(*bad), "missing-field", "unjudgeable", "occurrence type " + repr(occurrence))
        for side in (0, 1):
            bad = copy.deepcopy(pair)
            next(row for row in bad[side] if row.get("kind") == "rng-draw")["occurrence"] = 1.0
            bad[side][-1]["states"] += 1
            require(compare(*bad), "missing-field", "unjudgeable",
                    "occurrence shape before footer count " + str(side))
        legacy = _reference({"kind": "tape", "values": [0.5]})
        require(compare(*legacy), detail="WITNESS trace1 integer occurrence")
        for sides in ((0,), (1,), (0, 1)):
            numeric = copy.deepcopy(legacy)
            for side in sides:
                next(row for row in numeric[side] if row.get("kind") == "rng-draw")["occurrence"] = 1.0
            require(compare(*numeric), detail="trace1 numeric occurrence " + repr(sides))
        fractional = copy.deepcopy(legacy)
        next(row for row in fractional[1] if row.get("kind") == "rng-draw")["occurrence"] = 1.5
        require(compare(*fractional), "malformed", "unjudgeable", "trace1 fractional occurrence is checked before indexing")
        boolean = copy.deepcopy(legacy)
        next(row for row in boolean[1] if row.get("kind") == "rng-draw")["occurrence"] = True
        require(compare(*boolean), "event-mismatch", "fail", "trace1 boolean event identity remains distinct")
    elif name == "T2 lookahead":
        for codes in (("behavior-missing", "behavior-missing"), ("unclassified", "behavior-missing"),
                      ("behavior-missing", "unclassified"), ("unclassified", "unclassified")):
            for side in (0, 1):
                for extra_kind in ("mute", "error"):
                    pair = _trace2_reference()
                    for trace, code in zip(pair, codes):
                        _trace2_add_error(trace, code)
                    extra = (_ev(0, "m1", "mute", clip="extra") if extra_kind == "mute" else
                             _ev(0, "m1", "error", message="extra error",
                                 error={"code": "behavior-missing", "lookup": {"imageSet": "Beta", "behavior": ""},
                                        "via": ["birth"]}))
                    pair[side].insert(1, extra)
                    _trace2_refoot(pair[side])
                    require(compare(*pair), "missing-event" if side == 0 else "extra-event", "fail",
                            "lookahead presence " + str(side) + " " + extra_kind + " " + repr(codes))
            for side in (0, 1):
                pair = _trace2_reference()
                for trace, code in zip(pair, codes):
                    _trace2_add_error(trace, code)
                    trace.insert(2, _ev(0, "m1", "mute", clip="reordered"))
                    _trace2_refoot(trace)
                pair[side][1], pair[side][2] = pair[side][2], pair[side][1]
                require(compare(*pair), "event-mismatch", "fail", "lookahead reordered " + str(side) + " " + repr(codes))
    elif name == "T2 timeout snapshot":
        for side in (0, 1):
            for status, want in (("timeout", "timeout"), ("aborted", "malformed"), ("error", "malformed")):
                pair = _trace2_reference()
                trace = pair[side]
                footer = copy.deepcopy(trace[-1])
                stop = next(index for index, row in enumerate(trace) if row.get("type") == "state"
                            and row["tick"] == 3 and row["sid"] == "m1.b1")
                trace[:] = trace[:stop] + [dict(footer, status=status)]
                _trace2_refoot(trace)
                for supplied in (None, _scenario()):
                    require(compare(*pair, scenario=supplied), want, "unjudgeable", "mid-snapshot " + status)
                if status == "timeout":
                    trace.insert(-1, _ev(4, "m1", "mute", clip="later"))
                    _trace2_refoot(trace)
                    for supplied in (None, _scenario()):
                        require(compare(*pair, scenario=supplied), "malformed", "unjudgeable", "later event proves timeout snapshot finished")
        for status in (None, "timeout", "aborted"):
            pair = _trace2_reference()
            trace = pair[0]
            _cut_reference(trace, 4, status="timeout")
            trace[1:-1] = [row for row in trace[1:-1]
                           if not (row.get("type") == "state" and row["tick"] == 3 and row["sid"] == "m1")]
            if status is None:
                trace.pop()
            else:
                trace[-1]["status"] = status
                _trace2_refoot(trace)
            require(compare(*pair), "malformed", "unjudgeable", "non-suffix omission " + str(status))
        for status, want in ((None, "truncated"), ("timeout", "timeout")):
            pair = _trace2_reference()
            trace = pair[0]
            _cut_reference(trace, 4, status="timeout")
            trace[1:-1] = [row for row in trace[1:-1]
                           if not (row.get("type") == "state" and row["tick"] == 3 and row["sid"] == "m1")]
            position = next(i for i, row in enumerate(trace) if row.get("tick") == 3)
            trace.insert(position, _ev(3, "m1", "dispose"))
            if status is None:
                trace.pop()
            else:
                _trace2_refoot(trace)
            require(compare(*pair), want, "unjudgeable", "disposed prefix member is not an omission " + str(status))
        pair = _trace2_reference()
        trace = pair[0]
        _cut_reference(trace, 4, status="timeout")
        trace[1:-1] = [row for row in trace[1:-1]
                       if not (row.get("type") == "state" and row["tick"] == 3 and row["sid"] == "m1.b1")]
        trace.insert(-1, _state(3, "m1.b2", "Roll", "Roll", 0, 638, pending=True, set_="Ball", ref=7))
        _trace2_refoot(trace)
        require(compare(*pair), "malformed", "unjudgeable", "new child does not excuse omitted active member")
    else:
        raise ValueError("unknown trace/2 check " + name)


def burndown_check(name):
    """Focused public-comparer witnesses, also callable by the small source-mutation runner."""
    if name.startswith("T2 "):
        return _trace2_check(name)
    if name.startswith("F3 "):
        return _final_check(name)
    if name.startswith("R2 "):
        return _r2_check(name)
    if name.startswith("P2 "):
        return _p2_check(name)
    oracle, host = _reference()
    if name == "N269 source pin":
        oracle[0]["build"]["sourceZipSha256"] = H("0")
        report = compare(oracle, host)
        _case_require(report["verdict"] == "unjudgeable" and report["class"] == "source-pin-mismatch", name,
                      "another well-formed source hash was accepted")
    elif name == "N269 unbound report":
        report = compare(oracle, host)
        bound = compare(oracle, host, artifacts={"oracle": H("4"), "host": H("5")})
        _case_require(report["verdict"] == bound["verdict"] == "exact", name, "positive traces did not compare")
        for runtime in ("oracle", "host"):
            _case_require(report["buildWitness"][runtime] == {"artifactBound": False, "verification": "shape-only"},
                          name, runtime + " unbound metadata was hidden")
            _case_require(bound["buildWitness"][runtime] == {"artifactBound": True, "verification": "artifact-bound"},
                          name, runtime + " bound positive control was misreported")
        import contextlib
        import io
        import tempfile
        with tempfile.TemporaryDirectory(prefix="parity-cli-") as folder:
            paths = [os.path.join(folder, runtime + ".jsonl") for runtime in ("oracle", "host")]
            for path, trace in zip(paths, (oracle, host)):
                with open(path, "w", encoding="utf-8") as stream:
                    stream.write("\n".join(json.dumps(row) for row in trace) + "\n")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                status = main(["compare.py"] + paths)
            _case_require(status == 0, name, "CLI positive control failed")
            for runtime in ("oracle", "host"):
                _case_require(output.getvalue().splitlines().count(
                    "  " + runtime + " build witness: shape-only (artifact unbound)") == 1,
                    name, runtime + " unbound CLI notice was missing")
    elif name == "N270 typed state":
        _where(oracle, 1, "m1")[0]["vars"] = {"flag": {"values": [1]}}
        _where(host, 1, "m1")[0]["vars"] = {"flag": {"values": [True]}}
        report = compare(oracle, host)
        _case_require(report["verdict"] == "fail" and report["class"] == "state-mismatch"
                      and report["findings"][0]["field"] == "vars", name, "a nested boolean equalled a number")
        _where(host, 1, "m1")[0]["vars"] = {"flag": {"values": [1.0]}}
        _case_require(compare(oracle, host)["verdict"] == "exact", name, "equivalent JSON numbers differed")
    elif name == "N270 typed event":
        next(row for row in host if row.get("kind") == "birth-request")["ordinal"] = True
        report = compare(oracle, host)
        _case_require(report["verdict"] == "fail" and report["class"] == "event-mismatch", name,
                      "a boolean event payload equalled a number")
    elif name == "N270 event numbers":
        next(row for row in host if row.get("kind") == "birth-request")["anchor"] = [638.0, 680.0]
        _case_require(compare(oracle, host)["verdict"] == "exact", name,
                      "integer and decimal spellings of the same event coordinates differed")
    elif name == "N271 origin shape":
        for origin in ([], [604], [604, 552, 0], [True, 552], "604,552"):
            for tolerances in ({}, {"origin": 1}):
                wrong = copy.deepcopy(host)
                _where(wrong, 2, "m1")[0]["origin"] = origin
                report = compare(oracle, wrong, tolerances=tolerances)
                _case_require(report["verdict"] == "unjudgeable" and report["class"] == "missing-field"
                              and "origin is null or [x, y]" in report["findings"][0]["message"], name,
                              "a malformed origin was judged: " + repr(origin))
        for trace in (oracle, host):
            _where(trace, 2, "m1")[0].update(pose=None, poseAnchor=None, origin=None, visible=False)
        _case_require(compare(oracle, host)["verdict"] == "exact", name, "nullable origin positive control failed")
    elif name == "N272 tolerance names":
        _where(host, 2, "m1")[0]["anchor"][0] += 1
        _where(host, 3, "m1")[0]["origin"][0] += 1
        _revelocity(host)
        report = compare(oracle, host, tolerances={"anchor": 1, "origin": 1})
        _case_require(report["verdict"] == "within-tolerance:anchor,origin"
                      and report["tolerancesUsed"] == ["anchor", "origin"], name,
                      "the result omitted a tolerance used at an earlier tick")
    elif name == "N400 starter population":
        oracle, host, scenario = _two_starter_reference()
        _case_require(compare(oracle, host, scenario=scenario)["verdict"] == "exact", name,
                      "complete two-starter positive control failed")
        for mode in ("missing", "reversed"):
            pair = copy.deepcopy((oracle, host))
            for trace in pair:
                if mode == "missing":
                    trace[1:-1] = [row for row in trace[1:-1] if row.get("sid") != "m2"]
                    _refoot(trace)
                else:
                    first = [index for index, row in enumerate(trace) if row.get("type") == "state" and row["tick"] == 0]
                    trace[first[0]], trace[first[1]] = trace[first[1]], trace[first[0]]
            report = compare(*pair, scenario=scenario)
            _case_require(report["verdict"] == "unjudgeable" and report["class"] == "scenario-start-mismatch", name,
                          mode + " starters in both traces escaped the scenario check")
    elif name == "N400 pending starters":
        oracle, host, scenario = _two_starter_reference()
        _case_require(compare(oracle, host, scenario=scenario)["verdict"] == "exact", name,
                      "active two-starter positive control failed")
        for runtimes in (("oracle", "host"), ("oracle",), ("host",)):
            pair = copy.deepcopy((oracle, host))
            for runtime, trace in zip(("oracle", "host"), pair):
                if runtime in runtimes:
                    _where(trace, 0, "m1")[0]["pending"] = True
            report = compare(*pair, scenario=scenario)
            _case_require(report["verdict"] == "unjudgeable" and report["class"] == "scenario-start-mismatch"
                          and "must be active, not pending" in report["findings"][0]["message"], name,
                          ",".join(runtimes) + " pending tick-0 starter escaped the startup check")
            report = compare(*pair, scenario=scenario, scenario_sha=H("c"))
            _case_require(report["class"] == "scenario-hash-mismatch", name,
                          "a pending starter hid the wrong scenario hash")
    elif name == "N400 binding order":
        _, _, scenario = _two_starter_reference()
        # Both traces have one starter; the supplied scenario has two. An unrelated input refusal must win
        # before the population check attributes the difference to either runtime.
        cases = (
            ("scenario-hash-mismatch", {"scenario_sha": H("c")}),
            ("pack-hash-mismatch", {"scenario_sha": H("a"), "pack_sha": H("d")}),
            ("artifact-hash-mismatch", {"artifacts": {"host": H("9")}}),
        )
        for expected, arguments in cases:
            report = compare(oracle, host, scenario=scenario, **arguments)
            _case_require(report["verdict"] == "unjudgeable" and report["class"] == expected, name,
                          expected + " was hidden by a starter-population refusal")
        wrong_rng = dict(scenario, rng={"kind": "tape", "values": []})
        report = compare(oracle, host, scenario=wrong_rng)
        _case_require(report["class"] == "scenario-hash-mismatch", name,
                      "rng binding was hidden by a starter-population refusal")
        report = compare(oracle, host, scenario=dict(scenario, ticks=5))
        _case_require(report["class"] == "tick-count-mismatch", name,
                      "tick-count binding was hidden by a starter-population refusal")
    else:
        raise ValueError("unknown burndown check " + name)


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

        # Two entries that differ only in case are refused by name (review W123 F4). A case-sensitive folder holds
        # both for real; a case-insensitive one cannot, so its walk is made to list what a case-sensitive source
        # folder would, both names then resolving to the one file a copy of such a pack leaves.
        twin = os.path.join(d, "img", "Alpha", "B.png")
        try:
            with open(twin, "xb") as f:
                f.write(b"B, second spelling")
            listed = os.walk
        except FileExistsError:
            twin, real_walk = None, os.walk

            def listed(top, **kwargs):
                for root, dirs, names in real_walk(top, **kwargs):
                    yield root, dirs, names + (["B.png"] if "b.png" in names else [])
        saved, os.walk = os.walk, listed
        try:
            pack_preflight(d)
            good = False
        except Unjudgeable as u:
            good = u.cls == "pack-invalid" and "differ only in case" in str(u)
        finally:
            os.walk = saved
            if twin:
                os.remove(twin)
        ok &= good
        print(f"  {'entries differing only in case are refused':52} {'FIRED' if good else 'SURVIVED'}")

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
    for name in BURNDOWN_CHECKS:
        try:
            burndown_check(name)
            print("  bd-codex-parity: " + name + " PASS")
        except (AssertionError, RuntimeError) as failure:
            ok = False
            prefix = "bd-codex-parity: " + name + ": "
            detail = str(failure)
            print("  " + (detail if detail.startswith(prefix) else prefix + detail) + " FAIL")
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
    for runtime, witness in r.get("buildWitness", {}).items():
        suffix = "" if witness["artifactBound"] else " (artifact unbound)"
        print(f"  {runtime} build witness: {witness['verification']}{suffix}")
    for fnd in r["findings"][:5]:
        print("  " + fnd.get("message", str(fnd)))
    if "draws" in r:
        print(f"  draws: oracle {r['draws']['oracle']}, host {r['draws']['host']}")
    passed = r["verdict"] in ("exact", "time-share-pass") or r["verdict"].startswith("within-tolerance")
    return 0 if passed else (2 if r["verdict"] == "unjudgeable" else 1)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
