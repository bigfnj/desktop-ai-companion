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
     'observed_end = n if status == "complete" else max((s["tick"] for s in states), default=-1)', 'observed_end = n'),
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
     '    if rng in ("none", "tape"):\n        odraws =',
     '    if rng in ("none", "tape", "splitmix64"):\n        odraws ='),
    ("bd-codex-parity-2: run failures before script pending", "P2 script precedence",
     'for runtime, footer in sorted((("oracle", ofoot), ("host", hfoot)), key=lambda item: item[1] is not None):',
     'for runtime, footer in ():'),
    # ---- burndown/codex-parity-2-clarification ----
    ("bd-codex-parity-2-clarification: none draw remains a failure", "P2 script independent failures",
     'if rng == "none" and (odraws or hdraws):', 'if False:'),
    ("bd-codex-parity-2-clarification: tape exhaustion remains a failure", "P2 script independent failures",
     'elif rng == "tape" and max(len(odraws), len(hdraws)) > tape_len:', 'elif False:'),
    ("bd-codex-parity-2-clarification: draw count remains a failure", "P2 script independent failures",
     'elif len(odraws) != len(hdraws):', 'elif False:'),
    ("bd-codex-parity-2-clarification: independent failures precede pending", "P2 script independent failures",
     '        if not findings:\n            # Map/2', '        if True:\n            # Map/2'),
    ("bd-codex-parity-2-clarification: pending report diagnostic class", "P2 script pending CLI",
     'report["class"] = "script-site-unmapped"', 'report["class"] = None'),
    ("bd-codex-parity-2-clarification: pending finding diagnostic class", "P2 script pending",
     'findings.append({"class": "script-site-unmapped",', 'findings.append({"class": None,'),
    ("bd-codex-parity-2-clarification: pre-draw refusal remains aborted", "P2 script refusal before draw",
     'if status != "complete":', 'if status not in ("complete", "aborted"):'),
    ("bd-codex-parity-2-clarification: pending CLI remains non-success", "P2 script pending CLI",
     'passed = r["verdict"] in ("exact", "time-share-pass") or r["verdict"].startswith("within-tolerance")',
     'passed = r["verdict"] in ("exact", "time-share-pass", "pending") or r["verdict"].startswith("within-tolerance")'),
    # ---- burndown/codex-parity-2-review ----
    ("bd-codex-parity-2-review: startup refusal keeps its run status", "R2 zero-state runs",
     'if (states or status == "complete") and not any(s["tick"] == 0 for s in states):',
     'if not any(s["tick"] == 0 for s in states):'),
    ("bd-codex-parity-2-review: absent startup snapshot is not a population mismatch", "R2 zero-state runs",
     'if not states and (footer is None or footer["status"] != "complete"):', 'if False:'),
    ("bd-codex-parity-2-review: empty run counts still validate", "R2 zero-state validation",
     'if footer is not None and (len(states), len(events), len(draws)) != (footer["states"], footer["events"], footer["draws"]):',
     'if False:'),
    ("bd-codex-parity-2-review: nonempty failed prefix still needs tick zero", "R2 zero-state validation",
     'if (states or status == "complete") and not any(s["tick"] == 0 for s in states):',
     'if status == "complete" and not any(s["tick"] == 0 for s in states):'),
    ("bd-codex-parity-2-review: disposed own target remains inactive", "R2 disposed targets",
     'if removed:', 'if False:'),
    ("bd-codex-parity-2-review: unobserved child disposal is not proof", "R2 disposed targets",
     'last_seen = max((state["tick"] for state in states if state["sid"] == edge.get("sid")), default=None)',
     'last_seen = max((state["tick"] for state in states if state["sid"] == edge.get("sid")), default=-1)'),
    ("bd-codex-parity-2-review: another sid disposal is not proof", "R2 disposed targets",
     'event["sid"] == edge.get("sid")', 'True'),
    ("bd-codex-parity-2-review: later disposal is not earlier input proof", "R2 disposed targets",
     'and event["tick"] <= previous for event in events)', 'for event in events)'),
    ("bd-codex-parity-2-review: later state contradicts selected disposal", "R2 disposed targets",
     'if disposed_at is not None and ticks[-1] >= disposed_at:', 'if False:'),
    ("bd-codex-parity-2-review: valid peer proof precedes truncation", "R2 truncated peer proof",
     'if not has_footer and not defer_run_failure:', 'if not has_footer:'),
    ("bd-codex-parity-2-review: truncated trace is never positive proof", "R2 truncated peer proof",
     'if footer is None or not states:', 'if not states:'),
    ("bd-codex-parity-2-review: missing footer keeps generic run priority", "R2 truncated peer proof",
     'key=lambda item: item[1] is not None):', 'key=lambda item: False):'),
    ("bd-codex-parity-2-review: truncated body is still validated", "R2 truncated validation",
     'for i, r in enumerate(records[1:-1] if has_footer else records[1:], start=2):',
     'for i, r in enumerate(records[1:-1] if has_footer else [], start=2):'),
    ("bd-codex-parity-2-review: truncated body remains tick bounded", "R2 truncated validation",
     'if footer is None and tick_bound is not None:', 'if False:'),
    ("bd-codex-parity-2-review: truncated header still binds to scenario", "R2 truncated validation",
     'if scenario_sha is not None and h["scenario"]["sha256"] != scenario_sha:', 'if False:'),
    ("bd-codex-parity-2-review: remaining footer still binds scenario ticks", "R2 truncated validation",
     'if known_ticks and (any(ticks != known_ticks[0] for ticks in known_ticks)',
     'if False and (any(ticks != known_ticks[0] for ticks in known_ticks)'),
    # ---- burndown/codex-parity2-final ----
    ("bd-codex-parity2-final: interrupted final snapshot stays truncated", "F3 interrupted snapshot",
     'unfinished_snapshot = interrupted and ticks[-1] == observed_end - 1', 'unfinished_snapshot = False'),
    ("bd-codex-parity2-final: earlier missing population stays malformed", "F3 interrupted snapshot",
     'unfinished_snapshot = interrupted and ticks[-1] == observed_end - 1', 'unfinished_snapshot = interrupted'),
    ("bd-codex-parity2-final: real footer requires complete snapshot", "F3 interrupted snapshot",
     'unfinished_snapshot = interrupted and ticks[-1] == observed_end - 1',
     'unfinished_snapshot = ticks[-1] == observed_end - 1'),
    ("bd-codex-parity2-final: interrupted tail still validates velocity", "F3 interrupted snapshot",
     'if r["velocity"] != want:', 'if r["velocity"] != want and not unfinished_snapshot:'),
    ("bd-codex-parity2-final: disposed lifecycle cannot pass identical traces", "F3 disposal lifecycle",
     'if disposed_at is not None and ticks[-1] >= disposed_at:', 'if False:'),
    ("bd-codex-parity2-final: dispose request is not removal", "F3 disposal lifecycle",
     'disposed = {(e["sid"], e["tick"]) for e in events if e["kind"] == "dispose"}',
     'disposed = {(e["sid"], e["tick"]) for e in events if e["kind"] in ("dispose", "dispose-request")}'),
    ("bd-codex-parity2-final: later event proves snapshot ended", "F3 interrupted snapshot",
     ' and not any(e["tick"] > observed_end for e in events)', ''),
    # ---- burndown/codex-parity-3 ----
    ("bd-codex-parity-3: CC1 exact operand set", "T2 CC1",
     'if not isinstance(lookup, dict) or set(lookup) != operands:', 'if False:'),
    ("bd-codex-parity-3: CC2 error message nonempty", "T2 CC2",
     'if not isinstance(message, str) or not message:', 'if False:'),
    ("bd-codex-parity-3: CC2 error object type", "T2 CC2",
     'if not isinstance(error, dict) or set(error) != {"code", "lookup", "via"}:', 'if False:'),
    ("bd-codex-parity-3: CC2 scalar types", "T2 CC2",
     'if not valid:\n            bad(f"lookup operand', 'if False:\n            bad(f"lookup operand'),
    ("bd-codex-parity-3: CC2 error identity", "T2 CC2",
     'if not isinstance(event.get("sid"), str) or not event["sid"] or isinstance(event["tick"], bool):', 'if False:'),
    ("bd-codex-parity-3: CC2 refKey format", "T2 CC2",
     'valid = _source_refkey(value)', 'valid = isinstance(value, str) and bool(value)'),
    ("bd-codex-parity-3: CC2 refKey relative file", "T2 CC2",
     'return "\\\\" not in filename and ":" not in filename and all(part not in ("", ".", "..") for part in filename.split("/"))', 'return True'),
    ("bd-codex-parity-3: CC2 closed wrapper vocabulary", "T2 CC2",
     'if not isinstance(via, list) or any(not isinstance(site, str) or site not in ERROR_WRAPPERS for site in via):', 'if False:'),
    ("bd-codex-parity-3: CC2 wrapper labels", "T2 CC2",
     'site not in ERROR_WRAPPERS for site in via', 'False for site in via'),
    ("bd-codex-parity-3: CC3 raw names remain distinct", "T2 CC3",
     'return tuple(json_key(e.get(k)) for k in ("tick", "sid", "kind", "error"))',
     'return (e["tick"], e["sid"], e["kind"], e["error"]["code"], tuple((key, (value or "").strip()) for key, value in e["error"]["lookup"].items()), tuple(e["error"]["via"]))'),
    ("bd-codex-parity-3: CC3 diagnostic names typed operands", "T2 CC3",
     '(("error",) if trace2 and e["kind"] == "error"', '(("message",) if trace2 and e["kind"] == "error"'),
    ("bd-codex-parity-3: CC4 known codes only", "T2 CC4",
     'if not isinstance(code, str) or code not in ERROR_OPERANDS:', 'if False:'),
    ("bd-codex-parity-3: CC4 duplicate error property", "T2 CC4",
     'if getattr(error, "duplicates", False):', 'if False:'),
    ("bd-codex-parity-3: CC4 duplicate lookup property", "T2 CC4",
     'if getattr(lookup, "duplicates", False):', 'if False:'),
    ("bd-codex-parity-3: CC4 no surplus error field", "T2 CC4",
     'set(error) != {"code", "lookup", "via"}', 'False'),
    ("bd-codex-parity-3: CC5 unclassified null lookup", "T2 CC5",
     'if lookup is not None:', 'if False:'),
    ("bd-codex-parity-3: CC6 trace2 ignores wording", "T2 CC6",
     'if trace2 and e["kind"] == "error":', 'if False:'),
    ("bd-codex-parity-3: CC7 version preflight", "T2 CC7",
     'if preflight_headers[0]["schema"] != preflight_headers[1]["schema"]:', 'if False:'),
    ("bd-codex-parity-3: CC8 uncertainty blocks exact and tolerance", "T2 CC8",
     'elif unknown_error:', 'elif False:'),
    ("bd-codex-parity-3: CC8 uncertainty CLI exit", "T2 CC8",
     'return 0 if passed else (2 if r["verdict"] == "unjudgeable" else 1)', 'return 0 if passed else 1'),
    ("bd-codex-parity-3: CC9 unknown identity stays exact", "T2 CC9",
     'and all(json_key(ea[key]) == json_key(eb[key]) for key in ("tick", "sid", "kind"))', 'and True'),
    ("bd-codex-parity-3: CC9 unknown via stays exact", "T2 CC9",
     'and ea["error"]["via"] == eb["error"]["via"]', 'and True'),
    ("bd-codex-parity-3: CC10 continue after unknown", "T2 CC10",
     'unknown_error = True\n                    continue', 'unknown_error = True\n                    return {"verdict": "unjudgeable", "class": "error-unclassified", "findings": []}\n                    continue'),
    ("bd-codex-parity-3: CC11 interrupted startup proper prefix", "T2 CC11",
     'if trace2 and (footer is None or footer["status"] == "timeout") and actual_starters == expected_starters[:len(actual_starters)]', 'if False and actual_starters == expected_starters[:len(actual_starters)]'),
    ("bd-codex-parity-3: CC11 interrupted startup order", "T2 CC11",
     'actual_starters == expected_starters[:len(actual_starters)]', 'set(actual_starters).issubset(expected_starters)'),
    ("bd-codex-parity-3: CC11 interrupted startup pending", "T2 CC11",
     'and not any(state["pending"] for state in starter_states)', 'and True'),
    ("bd-codex-parity-3: CC11 later startup record", "T2 CC11",
     'and not any(row["tick"] > 0 for row in states + events):', 'and True:'),
    ("bd-codex-parity-3: CC11 empty prefix later record", "T2 CC11",
     'if not trace2 or (footer is not None and footer["status"] in ("aborted", "error")):', 'if True:'),
    ("bd-codex-parity-3: CC12 past-tape value", "T2 CC12",
     'if trace2 and tape is not None and k > len(tape) and v != 0.5:', 'if False:'),
    ("bd-codex-parity-3: CC12 flag consistency", "T2 CC12",
     'if trace2 and footer is not None and footer["tapeExhausted"] != (tape is not None and len(draws) > len(tape)):', 'if False:'),
    ("bd-codex-parity-3: CC12 false past-tape flag", "T2 CC12",
     'if trace2 and footer is not None and footer["tapeExhausted"] != (tape is not None and len(draws) > len(tape)):',
     'if trace2 and footer is not None and footer["tapeExhausted"] and footer["tapeExhausted"] != (tape is not None and len(draws) > len(tape)):'),
    ("bd-codex-parity-3: CC12 non-tape flag", "T2 CC12",
     'if trace2 and footer is not None and footer["tapeExhausted"] != (tape is not None and len(draws) > len(tape)):',
     'if trace2 and footer is not None and tape is not None and footer["tapeExhausted"] != (tape is not None and len(draws) > len(tape)):'),
    ("bd-codex-parity-3: CC12 required flag", "T2 CC12",
     'if trace2 and not isinstance(footer.get("tapeExhausted"), bool):', 'if False:'),
    ("bd-codex-parity-3: CC12 flag cannot be numeric", "T2 CC12",
     'if trace2 and not isinstance(footer.get("tapeExhausted"), bool):', 'if trace2 and not isinstance(footer.get("tapeExhausted"), (bool, int)):'),
    ("bd-codex-parity-3: CC13 preserve run-failure primary", "T2 CC13",
     '"class": u.cls, "findings": [{"message": str(u)}]', '"class": "rng-tape-exhausted" if secondary_exhaustion else u.cls, "findings": [{"message": str(u)}]'),
    ("bd-codex-parity-3: CC13 secondary needs proof", "T2 CC13",
     'and event["occurrence"] > len(oh["rng"]["values"])', 'and event["occurrence"] >= len(oh["rng"]["values"])'),
    ("bd-codex-parity-3: CC14 off footer draw marker", "T2 CC14",
     '("unobservedDraws" in tail or "unobservedEvents" in tail)', '("unobservedEvents" in tail)'),
    ("bd-codex-parity-3: CC14 off footer event marker", "T2 CC14",
     '("unobservedDraws" in tail or "unobservedEvents" in tail)', '("unobservedDraws" in tail)'),
    ("bd-codex-parity-3: CC14 off marker outranks unreadable peer tail", "T2 CC14",
     'except Unjudgeable:\n                    continue  # Ordinary validity', 'except Unjudgeable:\n                    raise  # Ordinary validity'),
    ("bd-codex-parity-3: CC15 free-running remains separate", "T2 CC15",
     'if rng in ("none", "tape"):\n        odraws =', 'if rng in ("none", "tape", "splitmix64"):\n        odraws ='),
    ("bd-codex-parity-3: CC16 trace1 message still exact", "T2 CC16",
     'if trace2 and e["kind"] == "error":', 'if e["kind"] == "error":'),
    ("bd-codex-parity-3: CC17 scenario before body", "T2 CC17",
     'if scenario is not None:\n            validate_scenario(scenario)', 'if False:\n            validate_scenario(scenario)'),
    ("bd-codex-parity-3: CC17 oracle validity before host body", "T2 CC17",
     'if build["sourceZipSha256"] != SOURCE_SHA256:', 'if False:'),
    ("bd-codex-parity-3: timeout mid snapshot", "T2 timeout snapshot",
     'interrupted = footer is None or (trace2 and status == "timeout")', 'interrupted = footer is None'),
    ("bd-codex-parity-3: aborted snapshot not excused", "T2 timeout snapshot",
     'interrupted = footer is None or (trace2 and status == "timeout")', 'interrupted = True'),
    ("bd-codex-parity-3: later event defeats timeout prefix", "T2 timeout snapshot",
     ' and not any(e["tick"] > observed_end for e in events)', ''),
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
