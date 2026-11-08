"""Run the comparer's focused witnesses against private source mutations, without editing the checkout."""
import argparse
import hashlib
import json
import os
import sys
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
     'if disposed_at is not None and ticks[-1] >= disposed_at:', 'if disposed_at is not None and ticks[-1] > disposed_at:'),
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
     'unfinished_snapshot = interrupted and tail_is_prefix and ticks[-1] == observed_end - 1', 'unfinished_snapshot = False'),
    ("bd-codex-parity2-final: earlier missing population stays malformed", "F3 interrupted snapshot",
     'unfinished_snapshot = interrupted and tail_is_prefix and ticks[-1] == observed_end - 1', 'unfinished_snapshot = interrupted'),
    ("bd-codex-parity2-final: real footer requires complete snapshot", "F3 interrupted snapshot",
     'unfinished_snapshot = interrupted and tail_is_prefix and ticks[-1] == observed_end - 1',
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
    ("bd-codex-parity-3: CC2 coerce invalid error object to a known cause", "T2 CC2",
     'if not isinstance(error, dict) or set(error) != {"code", "lookup", "via"}:\n        bad("object needs exactly code, lookup and via")',
     'if not isinstance(error, dict) or set(error) != {"code", "lookup", "via"}:\n        event["error"] = {"code": "behavior-missing", "lookup": {"imageSet": "Beta", "behavior": ""}, "via": []}\n        return'),
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
    ("bd-codex-parity-3: CC4 coerce unknown code to a known cause", "T2 CC4",
     'if not isinstance(code, str) or code not in ERROR_OPERANDS:\n        bad("code is not in the closed vocabulary")',
     'if not isinstance(code, str) or code not in ERROR_OPERANDS:\n        code = error["code"] = "behavior-missing"'),
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
     'if _unknown_error_pair(ea, eb, trace2):',
     'if _unknown_error_pair(ea, eb, trace2):\n                    return {"verdict": "unjudgeable", "class": "error-unclassified", "findings": []}'),
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
    ("bd-codex-parity-3: CC12 default malformed flag to false", "T2 CC12",
     'if trace2 and not isinstance(footer.get("tapeExhausted"), bool):\n            raise Unjudgeable("missing-field", f"{runtime} footer tapeExhausted is not a boolean")',
     'if trace2 and not isinstance(footer.get("tapeExhausted"), bool):\n            footer["tapeExhausted"] = False'),
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
     'report["verdict"] = "time-share-pass"',
     'report["verdict"] = "time-share-pass"\n        if trace2 and any(e["kind"] == "error" and e["error"]["code"] == "unclassified" for e in oevents + hevents):\n            report.update(verdict="unjudgeable", **{"class": "error-unclassified"})'),
    ("bd-codex-parity-3: CC16 trace1 message still exact", "T2 CC16",
     'if trace2 and e["kind"] == "error":', 'if e["kind"] == "error":'),
    ("bd-codex-parity-3: CC17 scenario before body", "T2 CC17",
     'if scenario is not None:\n            validate_scenario(scenario)',
     'if scenario is not None:\n            load_trace(oracle, "oracle", defer_run_failure=True)\n            load_trace(host, "host", defer_run_failure=True)\n            validate_scenario(scenario)'),
    ("bd-codex-parity-3: CC17 oracle validity before host body", "T2 CC17",
     'oh, ostates, oevents, ofoot = load_trace(oracle, "oracle", defer_run_failure=True)\n        hh, hstates, hevents, hfoot = load_trace(host, "host", defer_run_failure=True)',
     'hh, hstates, hevents, hfoot = load_trace(host, "host", defer_run_failure=True)\n        oh, ostates, oevents, ofoot = load_trace(oracle, "oracle", defer_run_failure=True)'),
    ("bd-codex-parity-3: timeout mid snapshot", "T2 timeout snapshot",
     'interrupted = footer is None or (trace2 and status == "timeout")', 'interrupted = footer is None'),
    ("bd-codex-parity-3: aborted snapshot not excused", "T2 timeout snapshot",
     'interrupted = footer is None or (trace2 and status == "timeout")', 'interrupted = True'),
    ("bd-codex-parity-3: later event defeats timeout prefix", "T2 timeout snapshot",
     ' and not any(e["tick"] > observed_end for e in events)',
     ' and (trace2 or not any(e["tick"] > observed_end for e in events))'),
    ("bd-codex-parity-3: freeze trace1 timeout tail", "T2 trace1 freeze",
     'interrupted = footer is None or (trace2 and status == "timeout")',
     'interrupted = footer is None or status == "timeout"'),
    ("bd-codex-parity-3: freeze trace1 footerless startup", "T2 trace1 freeze",
     'if footer is None and not trace2:', 'if footer is None and False:'),
    ("bd-codex-parity-3: freeze trace1 timeout startup", "T2 trace1 freeze",
     'if trace2 and (footer is None or footer["status"] == "timeout") and actual_starters',
     'if (footer is None or footer["status"] == "timeout") and actual_starters'),
    ("bd-codex-parity-3: CC8 host-only unclassified", "T2 CC8",
     '"unclassified" in (ea["error"]["code"], eb["error"]["code"])',
     'ea["error"]["code"] == "unclassified"'),
    ("bd-codex-parity-3: CC4 duplicate event error field", "T2 CC4",
     'if "error" in getattr(event, "duplicate_keys", ()):', 'if False:'),
    ("bd-codex-parity-3: timeout must omit a suffix", "T2 timeout snapshot",
     'tail_is_prefix = not trace2 or final_order == previous_order[:len(final_order)]', 'tail_is_prefix = True'),
    ("bd-codex-parity-3: CC12 below-half past tape", "T2 CC12",
     'k > len(tape) and v != 0.5:', 'k > len(tape) and v > 0.5:'),
    ("bd-codex-parity-3: CC2 empty imageSet", "T2 CC2",
     'valid = isinstance(value, str) and bool(value)',
     'valid = isinstance(value, str) and (bool(value) or key == "imageSet")'),
    ("bd-codex-parity-3: CC14 class-specific CLI exit", "T2 CC14",
     'return 0 if passed else (2 if r["verdict"] == "unjudgeable" else 1)',
     'return 1 if r["class"] == "trace-off-not-comparable" else (0 if passed else (2 if r["verdict"] == "unjudgeable" else 1))'),
    ("bd-codex-parity-3: CC9 presence failure cannot become uncertain", "T2 CC9",
     'if _unknown_error_pair(ea, eb, trace2):',
     'if trace2 and any(e is not None and e["kind"] == "error" and e["error"]["code"] == "unclassified" for e in (ea, eb)):\n                    return {"verdict": "unjudgeable", "class": "error-unclassified", "findings": []}\n                if _unknown_error_pair(ea, eb, trace2):'),
    ("bd-codex-parity-3: lookahead aligns uncertain causes", "T2 lookahead",
     'return event_key(ea, trace2) == event_key(eb, trace2) or _unknown_error_pair(ea, eb, trace2)',
     'return event_key(ea, trace2) == event_key(eb, trace2)'),
    ("bd-codex-parity-3: CC17 starter before inferred input", "T2 CC17",
     '            # Bind the observations to their inputs before judging this scenario',
     '            _validate_input_targets(scenario, (("oracle", ostates, oevents, ofoot), ("host", hstates, hevents, hfoot)))\n            # Bind the observations to their inputs before judging this scenario'),
    ("bd-codex-parity-3: completed peer exhaustion wording", "T2 CC13",
     'runtime + " recorded a past-tape draw"', 'runtime + " recorded a past-tape draw before its run failed"'),
    ("bd-codex-parity-3: unknown diagnostic names the pair", "T2 CC8",
     '''f"{sid}: tick {ea['tick']}, {ea['sid']}, error via {ea['error']['via']!r}: "''', '"unknown error: "'),
    ("bd-codex-parity-3: occurrence rejects numeric coercion", "T2 occurrence",
     '''raise Unjudgeable("missing-field", f"{runtime}: tick {r['tick']}: rng-draw occurrence must be an integer")''',
     'r["occurrence"] = int(r["occurrence"])'),
    ("bd-codex-parity-3: occurrence rejects boolean integers", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and not isinstance(r["occurrence"], int):'),
    ("bd-codex-parity-3: lookahead missing absence term", "T2 lookahead",
     'not any(_events_align(ea, event, trace2) for event in hevents[i:])', 'a not in hkeys[i:]'),
    ("bd-codex-parity-3: lookahead missing alignment term", "T2 lookahead",
     'any(_events_align(event, eb, trace2) for event in oevents[i + 1:])', 'b in okeys[i + 1:]'),
    ("bd-codex-parity-3: lookahead extra absence term", "T2 lookahead",
     'not any(_events_align(event, eb, trace2) for event in oevents[i:])', 'b not in okeys[i:]'),
    ("bd-codex-parity-3: lookahead extra alignment term", "T2 lookahead",
     'any(_events_align(ea, event, trace2) for event in hevents[i + 1:])', 'a in hkeys[i + 1:]'),
    ("bd-codex-parity-3: trace1 non-suffix tail freeze", "T2 trace1 freeze",
     'tail_is_prefix = not trace2 or final_order == previous_order[:len(final_order)]',
     'tail_is_prefix = final_order == previous_order[:len(final_order)]'),
    ("bd-codex-parity-3: occurrence before footer count", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and (footer is None or footer["states"] == sum(row.get("type") == "state" for row in records)) and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
    ("bd-codex-parity-3: occurrence before tick range", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and r["tick"] >= 0 and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
    ("bd-codex-parity-3: occurrence before tick order", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and (r["tick"] < 0 or r["tick"] >= last_tick) and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
    ("bd-codex-parity-3: occurrence before upper tick range", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and (n is None or r["tick"] <= n) and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
    ("bd-codex-parity-3: boolean occurrence before tick range", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], bool) or (r["tick"] >= 0 and (n is None or r["tick"] <= n))) and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
    ("bd-codex-parity-3: boolean occurrence before tick order", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], bool) or r["tick"] < 0 or (n is not None and r["tick"] > n) or r["tick"] >= last_tick) and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
    ("bd-codex-parity-3: boolean occurrence before footer count", "T2 occurrence",
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):',
     'if trace2 and kind == "rng-draw" and (not isinstance(r["occurrence"], bool) or footer is None or footer["states"] == sum(row.get("type") == "state" for row in records)) and (not isinstance(r["occurrence"], int) or isinstance(r["occurrence"], bool)):'),
)


# Every entry pins the exact failed witness leg and the mutant's observed verdict/class.
# The independent probe was reviewed before these expectations were frozen; execution never learns them.
MUTATION_EXPECTATIONS = (
    # bd-codex-parity: N269 source-pin-refusal
    ("another well-formed source hash was accepted", "exact", None),
    # bd-codex-parity: N269 unbound-metadata
    ("oracle unbound metadata was hidden", "exact", None),
    # bd-codex-parity: N269 unbound-cli-notice
    ("oracle unbound CLI notice was missing", "exact", None),
    # bd-codex-parity: N270 boolean-state-distinction
    ("a nested boolean equalled a number", "exact", None),
    # bd-codex-parity: N270 boolean-event-distinction
    ("a boolean event payload equalled a number", "exact", None),
    # bd-codex-parity: N270 numeric-event-equality
    ("integer and decimal spellings of the same event coordinates differed", "fail", "event-mismatch"),
    # bd-codex-parity: N271 origin-point-shape
    ("a malformed origin was judged: []", "fail", "state-mismatch"),
    # bd-codex-parity: N272 retain-earlier-tolerance
    ("the result omitted a tolerance used at an earlier tick", "within-tolerance:origin", None),
    # bd-codex-parity: N400 starter-population
    ("missing starters in both traces escaped the scenario check", "exact", None),
    # bd-codex-parity: N400 pending-starter-refusal
    ("oracle,host pending tick-0 starter escaped the startup check", "exact", None),
    # bd-codex-parity: N400 starter-population-before-binding
    ("scenario-hash-mismatch was hidden by a starter-population refusal", "unjudgeable", "scenario-start-mismatch"),
    # bd-codex-parity-2: inactive target classification
    ("press inactive target before None run failure: ('exact', None)", "exact", None),
    # bd-codex-parity-2: pending target classification
    ("press inactive target before None run failure: ('exact', None)", "exact", None),
    # bd-codex-parity-2: disposing target stays active
    ("WITNESS active, activated or still-disposing left target: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2: right button stays unchecked
    ("WITNESS right edge is ignored: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2: oracle run failure deferred
    ("press inactive target before oracle run failure: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2: host run failure deferred
    ("press inactive target before host run failure: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2: natural abort prefix cutoff
    ("press inactive target before oracle run failure: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-2: scenario binding before input inference
    ("input classification hid scenario-hash-mismatch: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2: future input lacks evidence
    ("future empty population after an unrelated abort: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2: incomplete snapshot lacks evidence
    ("missing child snapshot is not inactive-target evidence: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2: physical trace scenario inference
    ("physical JSONL with supplied scenario: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2: oracle script site defers
    ("script site on oracle with tape: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-2: host script site defers
    ("script site on host with tape: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-2: script pending verdict
    ("script site on oracle with tape: ('exact', 'script-site-unmapped')", "exact", "script-site-unmapped"),
    # bd-codex-parity-2: script prefix is exact
    ("WITNESS only exact script prefix defers: ('pending', 'script-site-unmapped')", "pending", "script-site-unmapped"),
    # bd-codex-parity-2: script pending is directed only
    ("WITNESS free-running claim remains time-share: ('pending', 'script-site-unmapped')", "pending", "script-site-unmapped"),
    # bd-codex-parity-2: run failures before script pending
    ("script pending hid aborted: ('pending', 'script-site-unmapped')", "pending", "script-site-unmapped"),
    # bd-codex-parity-2-clarification: none draw remains a failure
    ("oracle script draw with none: ('fail', 'rng-draw-count')", "fail", "rng-draw-count"),
    # bd-codex-parity-2-clarification: tape exhaustion remains a failure
    ("oracle script draw with tape: ('fail', 'rng-draw-count')", "fail", "rng-draw-count"),
    # bd-codex-parity-2-clarification: draw count remains a failure
    ("oracle extra script draw: ('pending', 'script-site-unmapped')", "pending", "script-site-unmapped"),
    # bd-codex-parity-2-clarification: independent failures precede pending
    ("oracle script draw with none: ('pending', 'script-site-unmapped')", "pending", "script-site-unmapped"),
    # bd-codex-parity-2-clarification: pending report diagnostic class
    ("physical JSONL pending diagnostic: ('pending', None)", "pending", None),
    # bd-codex-parity-2-clarification: pending finding diagnostic class
    ("pending finding lost its diagnostic class", "pending", "script-site-unmapped"),
    # bd-codex-parity-2-clarification: pre-draw refusal remains aborted
    ("oracle pre-draw refusal, peer script=False, scenario supplied=False: ('fail', 'rng-draw-count')", "fail", "rng-draw-count"),
    # bd-codex-parity-2-clarification: pending CLI remains non-success
    ("pending CLI did not expose the diagnostic class and non-success exit", "pending", "script-site-unmapped"),
    # bd-codex-parity-2-review: startup refusal keeps its run status
    ("standalone zero-state status was lost", "unjudgeable", "malformed"),
    # bd-codex-parity-2-review: absent startup snapshot is not a population mismatch
    ("oracle zero-state aborted, scenario=True: ('unjudgeable', 'scenario-start-mismatch')", "unjudgeable", "scenario-start-mismatch"),
    # bd-codex-parity-2-review: empty run counts still validate
    ("zero-state run hid malformed: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2-review: nonempty failed prefix still needs tick zero
    ("WITNESS nonempty failed prefix still requires tick 0: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2-review: disposed own target remains inactive
    ("oracle sole companion disposed before later press: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2-review: unobserved child disposal is not proof
    ("WITNESS unrelated or unobserved removal/right edge: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: another sid disposal is not proof
    ("WITNESS unrelated or unobserved removal/right edge: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: later disposal is not earlier input proof
    ("WITNESS disposal after the input is not evidence: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: later state contradicts selected disposal
    ("WITNESS oracle state at/after disposal contradicts removal: 1: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: valid peer proof precedes truncation
    ("oracle truncation follows valid peer proof: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-2-review: truncated trace is never positive proof
    ("two truncated removed-target prefixes are not proof: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: missing footer keeps generic run priority
    ("WITNESS no supplied scenario preserves host truncation: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-2-review: truncated body is still validated
    ("oracle truncated trace hid missing-field: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: truncated body remains tick bounded
    ("scenario N still bounds the truncated body: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: truncated header still binds to scenario
    ("oracle truncated trace hid scenario-hash-mismatch: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-2-review: remaining footer still binds scenario ticks
    ("the remaining footer still binds scenario N: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity2-final: interrupted final snapshot stays truncated
    ("oracle final snapshot cut between state rows: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity2-final: earlier missing population stays malformed
    ("WITNESS earlier missing population remains malformed: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity2-final: real footer requires complete snapshot
    ("WITNESS real footer retains complete snapshot rule: aborted: ('unjudgeable', 'tick-count-mismatch')", "unjudgeable", "tick-count-mismatch"),
    # bd-codex-parity2-final: interrupted tail still validates velocity
    ("WITNESS interrupted tail still checks its recorded velocity: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity2-final: disposed lifecycle cannot pass identical traces
    ("both state at/after own dispose: ('exact', None)", "exact", None),
    # bd-codex-parity2-final: dispose request is not removal
    ("WITNESS dispose-request and disposing state before removal are valid: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity2-final: later event proves snapshot ended
    ("WITNESS later event proves the incomplete snapshot already ended: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-3: CC1 exact operand set
    ("behavior-missing missing operand: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC2 error message nonempty
    ("invalid scalar '': ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC2 coerce invalid error object to a known cause
    ("invalid scalar None: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC2 scalar types
    ("invalid scalar {'code': 'behavior-missing', 'lookup': {'imageSet': {}, 'behavior': ''}, 'via': []}: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC2 error identity
    ("invalid scalar 42: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC2 refKey format
    ("invalid scalar {'code': 'variant-missing', 'lookup': {'refKey': 'not-a-source-key'}, 'via': []}: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC2 refKey relative file
    ("invalid scalar {'code': 'variant-missing', 'lookup': {'refKey': 'Alpha|../actions.xml|/Mascot[1]'}, 'via': []}: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC2 closed wrapper vocabulary
    ("invalid scalar {'code': 'behavior-missing', 'lookup': {'imageSet': 'Beta', 'behavior': ''}, 'via': 'birth'}: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC2 wrapper labels
    ("invalid scalar {'code': 'behavior-missing', 'lookup': {'imageSet': 'Beta', 'behavior': ''}, 'via': ['unknown']}: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC3 raw names remain distinct
    ("raw requested name distinctions: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC3 diagnostic names typed operands
    ("diagnostic must carry the actual differing typed operands", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC4 coerce unknown code to a known cause
    ("closed error property/code vocabulary: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC4 duplicate error property
    ("duplicate property cannot be last-wins: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC4 duplicate lookup property
    ("duplicate property cannot be last-wins: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC4 no surplus error field
    ("closed error property/code vocabulary: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC5 unclassified null lookup
    ("lookup null rule unclassified: ('unjudgeable', 'error-unclassified')", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: CC6 trace2 ignores wording
    ("versioned message meaning: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC7 version preflight
    ("version before build: ('unjudgeable', 'build-witness-missing')", "unjudgeable", "build-witness-missing"),
    # bd-codex-parity-3: CC8 uncertainty blocks exact and tolerance
    ("aligned cause blocks success: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC8 uncertainty CLI exit
    ("unknown-cause CLI exit must be 2", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: CC9 unknown identity stays exact
    ("unknown identity mismatch tick: ('unjudgeable', 'error-unclassified')", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: CC9 unknown via stays exact
    ("unknown identity mismatch via: ('unjudgeable', 'error-unclassified')", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: CC10 continue after unknown
    ("later independently established failure wins: ('unjudgeable', 'error-unclassified')", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: CC11 interrupted startup proper prefix
    ("proper interrupted startup prefix: ('unjudgeable', 'scenario-start-mismatch')", "unjudgeable", "scenario-start-mismatch"),
    # bd-codex-parity-3: CC11 interrupted startup order
    ("observed startup sid: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-3: CC11 interrupted startup pending
    ("observed startup pending: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-3: CC11 later startup record
    ("later record proves startup finished: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-3: CC11 empty prefix later record
    ("later record proves empty interrupted startup finished: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-3: CC12 past-tape value
    ("past-tape value 0.7: ('fail', 'rng-tape-exhausted')", "fail", "rng-tape-exhausted"),
    # bd-codex-parity-3: CC12 flag consistency
    ("forged exhaustion flag: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC12 false past-tape flag
    ("forged exhaustion flag: ('fail', 'rng-tape-exhausted')", "fail", "rng-tape-exhausted"),
    # bd-codex-parity-3: CC12 non-tape flag
    ("forged exhaustion flag: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC12 default malformed flag to false
    ("required footer flag: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC12 flag cannot be numeric
    ("boolean footer flag cannot be an integer: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC13 preserve run-failure primary
    ("run failure outranks exhaustion: ('unjudgeable', 'rng-tape-exhausted')", "unjudgeable", "rng-tape-exhausted"),
    # bd-codex-parity-3: CC13 secondary needs proof
    ("secondary exhaustion requires a physical past-tape draw", "unjudgeable", "timeout"),
    # bd-codex-parity-3: CC14 off footer draw marker
    ("off artifact before body: ('unjudgeable', 'build-witness-missing')", "unjudgeable", "build-witness-missing"),
    # bd-codex-parity-3: CC14 off footer event marker
    ("off artifact before body: ('unjudgeable', 'build-witness-missing')", "unjudgeable", "build-witness-missing"),
    # bd-codex-parity-3: CC14 off marker outranks unreadable peer tail
    ("off artifact before unreadable peer tail: ('unjudgeable', 'missing-field')", "unjudgeable", "missing-field"),
    # bd-codex-parity-3: CC15 free-running remains separate
    ("unknown free-running error keeps separate claim: ('unjudgeable', 'error-unclassified')", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: CC16 trace1 message still exact
    ("versioned message meaning: ('exact', None)", "exact", None),
    # bd-codex-parity-3: CC17 scenario before body
    ("static scenario before body: ('unjudgeable', 'missing-field')", "unjudgeable", "missing-field"),
    # bd-codex-parity-3: CC17 oracle validity before host body
    ("oracle pin before host body: ('unjudgeable', 'missing-field')", "unjudgeable", "missing-field"),
    # bd-codex-parity-3: timeout mid snapshot
    ("mid-snapshot timeout: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: aborted snapshot not excused
    ("mid-snapshot aborted: ('unjudgeable', 'aborted')", "unjudgeable", "aborted"),
    # bd-codex-parity-3: later event defeats timeout prefix
    ("later event proves timeout snapshot finished: ('unjudgeable', 'timeout')", "unjudgeable", "timeout"),
    # bd-codex-parity-3: freeze trace1 timeout tail
    ("trace1 timeout mid-snapshot remains malformed: ('unjudgeable', 'timeout')", "unjudgeable", "timeout"),
    # bd-codex-parity-3: freeze trace1 footerless startup
    ("trace1 startup prefix None: ('unjudgeable', 'scenario-start-mismatch')", "unjudgeable", "scenario-start-mismatch"),
    # bd-codex-parity-3: freeze trace1 timeout startup
    ("trace1 startup prefix timeout: ('unjudgeable', 'timeout')", "unjudgeable", "timeout"),
    # bd-codex-parity-3: CC8 host-only unclassified
    ("aligned cause blocks success: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC4 duplicate event error field
    ("duplicate property cannot be last-wins: ('exact', None)", "exact", None),
    # bd-codex-parity-3: timeout must omit a suffix
    ("non-suffix omission None: ('unjudgeable', 'truncated')", "unjudgeable", "truncated"),
    # bd-codex-parity-3: CC12 below-half past tape
    ("past-tape value 0.25: ('fail', 'rng-tape-exhausted')", "fail", "rng-tape-exhausted"),
    # bd-codex-parity-3: CC2 empty imageSet
    ("invalid scalar {'code': 'behavior-missing', 'lookup': {'imageSet': '', 'behavior': ''}, 'via': []}: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC14 class-specific CLI exit
    ("off-artifact CLI exit must be 2", "unjudgeable", "trace-off-not-comparable"),
    # bd-codex-parity-3: CC9 presence failure cannot become uncertain
    ("missing unknown error: ('unjudgeable', 'error-unclassified')", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: lookahead aligns uncertain causes
    ("lookahead presence 0 mute ('unclassified', 'behavior-missing'): ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: CC17 starter before inferred input
    ("starter before trace-derived input: ('unjudgeable', 'scenario-invalid')", "unjudgeable", "scenario-invalid"),
    # bd-codex-parity-3: completed peer exhaustion wording
    ("completed runtime exhaustion diagnostic must not claim that runtime failed", "unjudgeable", "aborted"),
    # bd-codex-parity-3: unknown diagnostic names the pair
    ("unknown-cause CLI message identifies the aligned pair", "unjudgeable", "error-unclassified"),
    # bd-codex-parity-3: occurrence rejects numeric coercion
    ("occurrence type 1.0: ('exact', None)", "exact", None),
    # bd-codex-parity-3: occurrence rejects boolean integers
    ("occurrence type True: ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: lookahead missing absence term
    ("lookahead reordered 1 ('unclassified', 'behavior-missing'): ('fail', 'missing-event')", "fail", "missing-event"),
    # bd-codex-parity-3: lookahead missing alignment term
    ("lookahead presence 0 mute ('unclassified', 'behavior-missing'): ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: lookahead extra absence term
    ("lookahead reordered 0 ('unclassified', 'behavior-missing'): ('fail', 'extra-event')", "fail", "extra-event"),
    # bd-codex-parity-3: lookahead extra alignment term
    ("lookahead presence 1 mute ('unclassified', 'behavior-missing'): ('fail', 'event-mismatch')", "fail", "event-mismatch"),
    # bd-codex-parity-3: trace1 non-suffix tail freeze
    ("trace1 footerless non-suffix final snapshot 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: occurrence before footer count
    ("occurrence shape before footer count 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: occurrence before tick range
    ("occurrence shape before tick range 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: occurrence before tick order
    ("occurrence shape before tick order 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: occurrence before upper tick range
    ("occurrence shape before upper tick range 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: boolean occurrence before tick range
    ("boolean occurrence shape before tick range 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: boolean occurrence before tick order
    ("boolean occurrence shape before tick order 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
    # bd-codex-parity-3: boolean occurrence before footer count
    ("boolean occurrence shape before footer count 0: ('unjudgeable', 'malformed')", "unjudgeable", "malformed"),
)
if len(CASES) != len(MUTATION_EXPECTATIONS):
    raise AssertionError("each mutation needs one explicit leg/verdict/class expectation")
CASES = tuple(case + expectation for case, expectation in zip(CASES, MUTATION_EXPECTATIONS))


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
    module._mutation_outcome = None
    module._mutation_witnesses = []
    actual_compare, actual_load = module.compare, module.load_trace
    actual_require = module._case_require

    def observe_compare(*args, **kwargs):
        result = actual_compare(*args, **kwargs)
        module._mutation_outcome = (result["verdict"], result["class"])
        return result

    def observe_load(*args, **kwargs):
        try:
            return actual_load(*args, **kwargs)
        except module.Unjudgeable as failure:
            module._mutation_outcome = ("unjudgeable", failure.cls)
            raise

    module.compare, module.load_trace = observe_compare, observe_load
    def observe_require(value, name, detail):
        module._mutation_witnesses.append({"check": name, "detail": detail,
                                           "outcome": module._mutation_outcome})
        return actual_require(value, name, detail)
    module._case_require = observe_require
    return module, digest


def verify_failure(name, check, failure, observed, expected_detail, expected_verdict, expected_class):
    prefix = "bd-codex-parity: " + check + ": "
    detail = str(failure)[len(prefix):] if str(failure).startswith(prefix) else None
    if detail != expected_detail or observed != (expected_verdict, expected_class):
        raise AssertionError("WRONG " + name + ": expected leg=" + repr(expected_detail)
                             + " outcome=" + repr((expected_verdict, expected_class))
                             + ", got leg=" + repr(detail) + " outcome=" + repr(observed))
    if "escaped validation as" in detail:
        raise AssertionError("WRONG " + name + ": a validation escape is not a named grading failure")


def expectation_self_test():
    prefix = "bd-codex-parity: check: "
    verify_failure("control", "check", AssertionError(prefix + "leg"), ("fail", "event-mismatch"),
                   "leg", "fail", "event-mismatch")
    for label, actual, outcome, expected in (
            ("leg", "other leg", ("fail", "event-mismatch"), "leg"),
            ("class", "leg", ("fail", "state-mismatch"), "leg"),
            ("verdict", "leg", ("unjudgeable", "event-mismatch"), "leg"),
            ("escape", "escaped validation as TypeError", ("fail", "event-mismatch"),
             "escaped validation as TypeError")):
        try:
            verify_failure(label, "check", AssertionError(prefix + actual), outcome,
                           expected, "fail", "event-mismatch")
        except AssertionError as failure:
            if not str(failure).startswith("WRONG " + label + ":"):
                raise
        else:
            raise AssertionError("expectation guard survived incorrect " + label)
    print("MUTATION EXPECTATIONS SELF-TEST PASS: leg/class/verdict/escape guards")
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default="bd-codex-parity:")
    parser.add_argument("--json", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return expectation_self_test()
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
    baseline_witnesses = {}
    for check in dict.fromkeys(case[1] for case in selected):
        baseline._mutation_witnesses = []
        baseline.burndown_check(check)
        baseline_witnesses[check] = list(baseline._mutation_witnesses)
        print("BASELINE " + check + " PASS")
    fired = 0
    records = []
    source_digests = {}
    with tempfile.TemporaryDirectory(prefix="parity-mutations-", dir=os.environ["TEMP"]) as folder:
        scratch = Path(folder)
        for index, (name, check, old, new, expected_detail, expected_verdict, expected_class) in enumerate(selected):
            if text.count(old) != 1:
                raise AssertionError(name + ": mutation anchor does not occur exactly once")
            root = scratch / str(index)
            target = root / "comparer" / "compare.py"
            target.parent.mkdir(parents=True)
            (root / "oracle").mkdir()
            (root / "oracle" / "source-pin.json").write_bytes(pin)
            target.write_bytes(original)
            changed = text.replace(old, new).encode("utf-8")
            changed_digest = hashlib.sha256(changed).hexdigest()
            if changed_digest in source_digests:
                raise AssertionError("duplicate mutated source: " + name + " and " + source_digests[changed_digest])
            source_digests[changed_digest] = name
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
                    verify_failure(name, check, failure, module._mutation_outcome,
                                   expected_detail, expected_verdict, expected_class)
                    ordinal = len(module._mutation_witnesses) - 1
                    prior = baseline_witnesses[check][ordinal]
                    leg = lambda detail: detail.rsplit(": (", 1)[0]
                    if leg(prior["detail"]) != leg(expected_detail):
                        raise AssertionError("WRONG " + name + ": mutant did not fail the same baseline witness leg")
                    print("FIRED " + name + " | " + str(failure))
                    records.append({"name": name, "check": check, "mutantSha256": digest,
                                    "expectedDetail": expected_detail, "actualDetail": str(failure),
                                    "expectedOutcome": [expected_verdict, expected_class],
                                    "baselineExpectedOutcome": prior["outcome"], "baselineDetail": prior["detail"],
                                    "witnessOrdinal": ordinal,
                                    "actualOutcome": module._mutation_outcome, "result": "FIRED"})
                    fired += 1
                except Exception as failure:
                    print("WRONG " + name + ": escaped " + type(failure).__name__ + ": " + str(failure))
                    raise
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
    if args.json:
        args.json.write_text(json.dumps({"baselineSha256": baseline_digest,
                                        "pid": os.getpid(), "pythonVersion": sys.version,
                                        "executable": sys.executable,
                                        "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                                        "entries": len(selected), "uniqueMutatedSources": len(source_digests),
                                        "fired": fired, "scratch": str(scratch), "scratchAbsent": not scratch.exists(),
                                        "mutations": records}, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
