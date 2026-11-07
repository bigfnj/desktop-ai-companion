"""Run the comparer's focused witnesses against private source mutations, without editing the checkout."""
import argparse
import hashlib
import os
from pathlib import Path
import tempfile
import types


HERE = Path(__file__).resolve().parent

# ---- burndown/codex-parity ----
CASES = (
    ("bd-codex-parity: N269 source-pin-refusal", "N269 source pin",
     'if build["sourceZipSha256"] != SOURCE_SHA256:', 'if False:'),
    ("bd-codex-parity: N269 unbound-metadata", "N269 unbound report",
     '"artifactBound": runtime in artifacts,', '"artifactBound": True,'),
    ("bd-codex-parity: N269 unbound-cli-notice", "N269 unbound report",
     'for runtime, witness in r.get("buildWitness", {}).items():', 'for runtime, witness in ():'),
    ("bd-codex-parity: N270 boolean-state-distinction", "N270 typed state",
     'if json_key(a[f]) != json_key(b[f]):', 'if a[f] != b[f]:'),
    ("bd-codex-parity: N270 boolean-event-distinction", "N270 typed event",
     'return ("boolean", value)', 'return ("number", value)'),
    ("bd-codex-parity: N270 numeric-event-equality", "N270 event numbers",
     'return tuple(json_key(e.get(k)) for k in ("tick", "sid", "kind") + EVENT_PAYLOAD_KEYS[e["kind"]])',
     'return (e["tick"], e["sid"], e["kind"]) + tuple(json.dumps(e.get(k), sort_keys=True) for k in EVENT_PAYLOAD_KEYS[e["kind"]])'),
    ("bd-codex-parity: N271 origin-point-shape", "N271 origin shape",
     'if r["origin"] is not None and not _is_point(r["origin"]):', 'if False:'),
    ("bd-codex-parity: N272 retain-earlier-tolerance", "N272 tolerance names",
     'used_tolerances.add("origin")', 'used_tolerances.clear()\n                            used_tolerances.add("origin")'),
    ("bd-codex-parity: N400 starter-population", "N400 starter population",
     'if actual_starters != expected_starters:', 'if False:'),
    ("bd-codex-parity: N400 pending-starter-refusal", "N400 pending starters",
     'if any(state["pending"] for state in starter_states):', 'if False:'),
    ("bd-codex-parity: N400 starter-population-before-binding", "N400 binding order",
     '        for runtime, h in (("oracle", oh), ("host", hh)):',
     '''        if scenario is not None:
            expected_starters = [mascot["sid"] for mascot in scenario["mascots"]]
            for runtime, states in (("oracle", ostates), ("host", hstates)):
                actual_starters = [state["sid"] for state in states if state["tick"] == 0]
                if actual_starters != expected_starters:
                    raise Unjudgeable("scenario-start-mismatch", "startup checked before input binding")
        for runtime, h in (("oracle", oh), ("host", hh)):'''),
    # ---- burndown/codex-parity-2 ----
    ("bd-codex-parity-2: inactive target classification", "P2 inactive targets",
     'if not any(state["sid"] == edge.get("sid") and not state["pending"] for state in snapshot):', 'if False:'),
    ("bd-codex-parity-2: pending target classification", "P2 inactive targets",
     'if not any(state["sid"] == edge.get("sid") and not state["pending"] for state in snapshot):',
     'if not any(state["sid"] == edge.get("sid") for state in snapshot):'),
    ("bd-codex-parity-2: disposing target stays active", "P2 inactive targets",
     'if not any(state["sid"] == edge.get("sid") and not state["pending"] for state in snapshot):',
     'if not any(state["sid"] == edge.get("sid") and not state["pending"] and not state["disposing"] for state in snapshot):'),
    ("bd-codex-parity-2: right button stays unchecked", "P2 inactive targets",
     'if not any(edge.get(button) == "left" for button in BUTTON_EDGES):',
     'if not any(edge.get(button) in ("left", "right") for button in BUTTON_EDGES):'),
    ("bd-codex-parity-2: oracle run failure deferred", "P2 inactive targets",
     'oh, ostates, oevents, ofoot = load_trace(oracle, "oracle", defer_run_failure=True)',
     'oh, ostates, oevents, ofoot = load_trace(oracle, "oracle")'),
    ("bd-codex-parity-2: host run failure deferred", "P2 inactive targets",
     'hh, hstates, hevents, hfoot = load_trace(host, "host", defer_run_failure=True)',
     'hh, hstates, hevents, hfoot = load_trace(host, "host")'),
    ("bd-codex-parity-2: natural abort prefix cutoff", "P2 inactive targets",
     'observed_end = n if status == "complete" else max(s["tick"] for s in states)', 'observed_end = n'),
    ("bd-codex-parity-2: scenario binding before input inference", "P2 input precedence",
     '        sid = (scenario or {}).get("id") or oh["scenario"]["id"]',
     '''        if scenario is not None:
            _validate_input_targets(scenario, (("oracle", ostates, oevents, ofoot), ("host", hstates, hevents, hfoot)))
        sid = (scenario or {}).get("id") or oh["scenario"]["id"]'''),
    ("bd-codex-parity-2: future input lacks evidence", "P2 partial input proof",
     'if previous > observed_end:', 'if False:'),
    ("bd-codex-parity-2: incomplete snapshot lacks evidence", "P2 partial input proof",
     'if expected is None or {state["sid"] for state in snapshot} != expected:', 'if False:'),
    ("bd-codex-parity-2: physical trace scenario inference", "P2 physical input CLI",
     '            _validate_input_targets(scenario, (("oracle", ostates, oevents, ofoot), ("host", hstates, hevents, hfoot)))',
     '            pass'),
    ("bd-codex-parity-2: oracle script site defers", "P2 script pending",
     'event for event in oevents + hevents if event["kind"] == "rng-draw"',
     'event for event in hevents if event["kind"] == "rng-draw"'),
    ("bd-codex-parity-2: host script site defers", "P2 script pending",
     'event for event in oevents + hevents if event["kind"] == "rng-draw"',
     'event for event in oevents if event["kind"] == "rng-draw"'),
    ("bd-codex-parity-2: script pending verdict", "P2 script pending",
     'report["verdict"] = "pending"', 'report["verdict"] = "exact"'),
    ("bd-codex-parity-2: script prefix is exact", "P2 script pending",
     'event["site"].startswith("script:")', '"script:" in event["site"]'),
    ("bd-codex-parity-2: script pending is directed only", "P2 script pending",
     '    if rng in ("none", "tape"):\n        # Map/2',
     '    if rng in ("none", "tape", "splitmix64"):\n        # Map/2'),
    ("bd-codex-parity-2: run failures before script pending", "P2 script precedence",
     'for runtime, footer in (("oracle", ofoot), ("host", hfoot)):', 'for runtime, footer in ():'),
)


def loaded_source(path, expected):
    """Python executes these source bytes directly; a new module and compile prevent a cached-code witness."""
    payload = path.read_bytes()
    digest = hashlib.sha256(payload).hexdigest()
    if digest != hashlib.sha256(expected).hexdigest():
        raise AssertionError("source execution witness differs from the written mutation")
    module = types.ModuleType("parity_mutation")
    module.__file__ = str(path)
    exec(compile(payload, str(path), "exec"), module.__dict__)
    if module.burndown_check.__code__.co_filename != str(path):
        raise AssertionError("focused check came from another source artifact")
    return module, digest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default="bd-codex-parity:")
    args = parser.parse_args()
    selected = [case for case in CASES if args.only in case[0]]
    if not selected:
        raise AssertionError("no comparer mutations selected")
    source = HERE / "compare.py"
    original = source.read_bytes()
    text = original.decode("utf-8")
    pin = (HERE.parent / "oracle" / "source-pin.json").read_bytes()
    baseline, baseline_digest = loaded_source(source, original)
    print("EXECUTED BASELINE artifact=" + str(source) + " sha256=" + baseline_digest
          + " mtimeNs=" + str(source.stat().st_mtime_ns))
    for check in dict.fromkeys(case[1] for case in selected):
        baseline.burndown_check(check)
        print("BASELINE " + check + " PASS")
    fired = 0
    with tempfile.TemporaryDirectory(prefix="parity-mutations-", dir=os.environ["TEMP"]) as folder:
        scratch = Path(folder)
        for index, (name, check, old, new) in enumerate(selected):
            if text.count(old) != 1:
                raise AssertionError(name + ": mutation anchor does not occur exactly once")
            root = scratch / str(index)
            target = root / "comparer" / "compare.py"
            target.parent.mkdir(parents=True)
            (root / "oracle").mkdir()
            (root / "oracle" / "source-pin.json").write_bytes(pin)
            target.write_bytes(original)
            changed = text.replace(old, new).encode("utf-8")
            target.write_bytes(changed)
            if changed == original:
                raise AssertionError(name + ": mutation did not change source bytes")
            if target.stat().st_mtime_ns <= source.stat().st_mtime_ns:
                raise AssertionError(name + ": private executable source is not newer than the checkout source")
            try:
                module, digest = loaded_source(target, changed)
                print("EXECUTED " + name + " sha256=" + digest + " mtimeNs=" + str(target.stat().st_mtime_ns))
                try:
                    module.burndown_check(check)
                except AssertionError as failure:
                    if not str(failure).startswith("bd-codex-parity: " + check + ":"):
                        raise
                    print("FIRED " + name + " | " + str(failure))
                    fired += 1
                else:
                    raise AssertionError("SURVIVED " + name)
            finally:
                target.write_bytes(original)
                if target.read_bytes() != original:
                    raise AssertionError(name + ": private source was not restored byte-identical")
                restored, restored_digest = loaded_source(target, original)
                restored.burndown_check(check)
                print("RESTORED " + name + " PASS sha256=" + restored_digest
                      + " mtimeNs=" + str(target.stat().st_mtime_ns) + " artifact=" + str(target))
    if scratch.exists() or source.read_bytes() != original:
        raise AssertionError("mutation cleanup or checkout source integrity failed")
    print("COMPARER MUTATIONS PASS: " + str(fired) + "/" + str(len(selected)) + " FIRED; source restored byte-identical")
    print("CLEANUP absent: " + str(scratch))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
