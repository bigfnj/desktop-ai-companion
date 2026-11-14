"""N-parity-1107-03 C8 (signed revision H :64, CONTRACT v1.3): the registry build proof on the four installed registered
inputs, and the fresh-process CLI legs of the host's admitted route.

  python tests/test_shimeji_parity_policy.py                  the build proof (reads files only)
  python tests/test_shimeji_parity_policy.py --self-test      the declaration grammar guards against their named unit inputs
  python tests/test_shimeji_parity_policy.py --mutate         each guard broken in a private copy of this file: its unit FAILs
  python tests/test_shimeji_parity_policy.py --exe E --converter C --work W
                                                              the build proof, then one built process per CLI leg

The build proof strictly parses REGISTRY.json and the four scenario/2 files (no byte order mark, strict UTF-8, no member
repeated in one object at any depth, no NaN or Infinity), checks each file's base and declaration grammar and the
registry's tuple uniqueness, recomputes every scenario and pack digest (the pack with the comparer's own pack_sha256),
checks scenarioId, pack and ticks against the decoded files, and compares every compiled row of ParityPolicyRegistry.cs
and its digest with the registry and with the jointly signed value. Grammar is unit/build integrity (H :64, choice A2):
behind the full-SHA gate the host adds no runtime grammar refusal, so these guards are graded here, on named unit
inputs, never through a hash refusal.

The CLI legs launch the built exe with --shimeji-parity-run, one process at a time, each with a fresh trace path under
--work, which must not exist yet. The caller sets the isolation variables, and the script checks them itself before it
makes a folder or launches anything (isolation_fault): DESKTOP_AI_COMPANION_DATA_ROOT an absolute folder that is not the
installed app's data root, TEMP and TMP the same absolute lane folder rather than the shared per-user temp folder, --work
under it, and DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1. Any one missing is a named refusal, "policy CLI: refused to
launch, the isolation is incomplete: ...", and no CLI leg runs; the exe also refuses a parity run without an overridden
data root. Each launch gets exactly the environment that was checked. The packs are converted with the given converter
(its paritypack verb) into --work. Each launch is a fresh process, so each must log its own registry-integrity ok line.
Exit 0 when every check passed, 1 otherwise.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / "tools" / "ShimejiParity" / "policy"
REGISTRY_CS = ROOT / "src" / "dotNet" / "Shimeji" / "ParityPolicyRegistry.cs"
COMPARER = ROOT / "tools" / "ShimejiParity" / "comparer" / "compare.py"
# The jointly signed identities (CONTRACT v1.3, PAYLOAD-MANIFEST eaa9c41d).
SIGNED_REGISTRY = "954776734f2ab70a4a96645e15e188d2d936e935b89e703e488c9e5de76f1442"
SIGNED_SCHEMA = "f76a30eedb8720acdba9c352522fbfe9bbb1391d9cec191fe710eafb7a438335"
SCENARIO2 = "shimeji-parity-scenario/2"
REGISTRY_NAME = "shimeji-parity-policy/1"
TUPLES = {("birth-cap", "cap-at"), ("window-off-plays", "window-carry-off"), ("window-off-plays", "window-throw-off"),
          ("window-off-plays", "window-minimize-off")}
BASE_FIELDS = ("id", "pack", "screens", "workArea", "dpiScale", "ticks", "rng", "settings", "mascots", "policy")
HEX64 = re.compile(r"^[0-9a-f]{64}$")

FAILED = []
# While --mutate runs the build proof on a broken private copy, its verdicts are collected here instead of printed.
SINK = None


def check(ok, label):
    if SINK is not None:
        SINK.append((bool(ok), label))
        return
    print(("PASS: " if ok else "FAIL: ") + label, flush=True)
    if not ok:
        FAILED.append(label)


def sha(data):
    return hashlib.sha256(data).hexdigest()


class GrammarFault(Exception):
    pass


def _pairs(pairs):
    seen = {}
    for key, value in pairs:
        if key in seen:
            raise GrammarFault("the member " + repr(key) + " repeats in one object")
        seen[key] = value
    return seen


def _constant(name):
    raise GrammarFault("the constant " + name + " is not JSON")


def strict_json(data):
    """The bytes as strict JSON, or GrammarFault: a byte order mark, invalid UTF-8, a repeated member at any depth, a
    NaN or Infinity constant, or anything json refuses."""
    if data.startswith(b"\xef\xbb\xbf"):
        raise GrammarFault("a byte order mark")
    try:
        text = data.decode("utf-8", "strict")
    except UnicodeDecodeError as e:
        raise GrammarFault("invalid UTF-8: " + str(e))
    try:
        return json.loads(text, object_pairs_hook=_pairs, parse_constant=_constant)
    except ValueError as e:
        raise GrammarFault("not JSON: " + str(e))


def declaration_fault(policy):
    """None, or why a scenario/2 policy value breaks the declaration schema (policy-declaration.schema.json): an object of
    exactly registry, id and case, each a string, the registry constant, and one of the four (id, case) tuples."""
    if not isinstance(policy, dict):
        return "policy is not an object"
    if set(policy) != {"registry", "id", "case"}:
        return "policy members are not exactly registry, id and case"
    if not all(isinstance(policy[k], str) for k in ("registry", "id", "case")):
        return "a policy member is not a string"
    if policy["registry"] != REGISTRY_NAME:
        return "policy.registry is not " + REGISTRY_NAME
    if (policy["id"], policy["case"]) not in TUPLES:
        return "policy id and case are no declared tuple"
    return None


def scenario_fault(doc):
    """None, or why a decoded scenario/2 breaks the base grammar this proof checks, or its declaration."""
    if not isinstance(doc, dict):
        return "the scenario is not an object"
    if doc.get("schema") != SCENARIO2:
        return "schema is not " + SCENARIO2
    for field in BASE_FIELDS:
        if field not in doc:
            return "lacks " + repr(field)
    if not isinstance(doc["id"], str) or not isinstance(doc["pack"], str):
        return "id or pack is not a string"
    if not isinstance(doc["ticks"], int) or isinstance(doc["ticks"], bool) or doc["ticks"] < 0:
        return "ticks is not a non-negative integer"
    return declaration_fault(doc["policy"])


def tuple_fault(rows):
    """None, or why the registry rows' tuples are not unique: (id, case) and scenarioId each once."""
    pairs = [(r.get("id"), r.get("case")) for r in rows]
    ids = [r.get("scenarioId") for r in rows]
    if len(set(pairs)) != len(pairs):
        return "a registry (id, case) tuple repeats"
    if len(set(ids)) != len(ids):
        return "a registry scenarioId repeats"
    return None


def compiled_rows(registry_cs=REGISTRY_CS):
    """The rows and digest compiled into ParityPolicyRegistry.cs, as its source spells them."""
    text = Path(registry_cs).read_text(encoding="utf-8")
    rows = re.findall(r'new ParityPolicyRow\("([^"]*)", "([^"]*)", "([^"]*)", "([^"]*)", (\d+),\s*"([0-9a-fA-F]*)", "([0-9a-fA-F]*)"\)', text)
    digest = re.search(r'internal const string CompiledDigest = "([^"]*)";', text)
    return [{"id": r[0], "case": r[1], "scenarioId": r[2], "pack": r[3], "ticks": int(r[4]), "scenarioSha256": r[5], "packSha256": r[6]}
            for r in rows], (digest.group(1) if digest else None)


def comparer_pack_sha256():
    spec = importlib.util.spec_from_file_location("parity_compare", COMPARER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.pack_sha256


def build_proof(policy=POLICY, registry_cs=REGISTRY_CS):
    """The proof on the installed files; --mutate hands it private copies with one artefact broken."""
    policy = Path(policy)
    registry_bytes = (policy / "REGISTRY.json").read_bytes()
    schema_bytes = (policy / "schema" / "policy-declaration.schema.json").read_bytes()
    check(sha(registry_bytes) == SIGNED_REGISTRY and sha(schema_bytes) == SIGNED_SCHEMA,
          "policy build proof: the installed registry and declaration schema equal their signed SHA-256 (" + sha(registry_bytes) + ", " + sha(schema_bytes) + ")")
    try:
        registry = strict_json(registry_bytes)
        registry_fault = None
    except GrammarFault as e:
        registry, registry_fault = None, str(e)
    check(registry_fault is None and isinstance(registry, dict) and isinstance(registry.get("cases"), list) and len(registry["cases"]) == 4,
          "policy build proof: the registry parses strictly and holds four rows (" + str(registry_fault) + ")")
    if registry_fault is not None:
        return
    rows = registry["cases"]
    check(tuple_fault(rows) is None, "policy build proof: the registry's tuples are unique (" + str(tuple_fault(rows)) + ")")
    pack_sha256 = comparer_pack_sha256()
    legacy = set(r.get("legacyScenarioSha256") for r in rows) | {registry.get("generatorSha256")}
    for row in rows:
        path = policy / "inputs" / (row["scenarioId"] + ".json")
        data = path.read_bytes()
        try:
            doc, fault = strict_json(data), None
            fault = scenario_fault(doc)
        except GrammarFault as e:
            doc, fault = None, str(e)
        facts = fault is None and doc["id"] == row["scenarioId"] and doc["pack"] == row["pack"] and doc["ticks"] == row["ticks"] \
            and (doc["policy"]["id"], doc["policy"]["case"]) == (row["id"], row["case"])
        pack = pack_sha256(str(path.parent / row["pack"])) if fault is None else None
        check(facts and sha(data) == row["scenarioSha256"] and pack == row["packSha256"] and HEX64.match(row["scenarioSha256"])
              and row["scenarioSha256"] not in legacy and row["packSha256"] not in legacy,
              "policy build proof: " + row["scenarioId"] + " parses strictly, its grammar holds, its id, pack, ticks and tuple are the row's, and its byte"
              " and pack digests recompute to the row's admission keys (" + str(fault) + ", " + sha(data) + ", " + str(pack) + ")")
    compiled, digest = compiled_rows(registry_cs)
    wanted = [{k: r[k] for k in ("id", "case", "scenarioId", "pack", "ticks", "scenarioSha256", "packSha256")} for r in rows]
    check(compiled == wanted and digest == SIGNED_REGISTRY == sha(registry_bytes),
          "policy build proof: every compiled row of ParityPolicyRegistry.cs and its digest equal the signed registry (" + str(len(compiled)) + " rows, digest " + str(digest) + ")")


def faults(function, value):
    """Whether the guard refuses the value by its own answer; an exception is no refusal (a removed guard often throws
    further on, which is not the guard working)."""
    try:
        return function(value) is not None
    except Exception:
        return False


def refusal(function, value):
    """The guard's own refusal text for the value, or None; an exception is no refusal, as in faults."""
    try:
        return function(value)
    except Exception:
        return None


# The isolation the CLI legs need (review of round 8: the script checked none of it and left it to its caller).
ISOLATION_DATA_ROOT = "DESKTOP_AI_COMPANION_DATA_ROOT"
ISOLATION_NO_REAL_INPUT = "DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT"
ISOLATION_REFUSED = "policy CLI: refused to launch, the isolation is incomplete: "


def _folder(path):
    return os.path.normcase(os.path.normpath(os.path.abspath(str(path))))


def _under(path, folder):
    path, folder = _folder(path), _folder(folder)
    return path == folder or path.startswith(folder.rstrip(os.sep) + os.sep)


def isolation_fault(env, work):
    """None, or why the CLI legs must not launch under env with this --work folder. The installed app keeps its data in
    LOCALAPPDATA/DesktopAICompanion (src/Portable/AppPaths.cs) and the shared per-user temp folder is LOCALAPPDATA/Temp;
    a lane's data root and TEMP are neither. Checked in this order, so each refusal names the first thing missing."""
    data_root = (env.get(ISOLATION_DATA_ROOT) or "").strip()
    if not data_root or not os.path.isabs(data_root):
        return ISOLATION_DATA_ROOT + " is not set to an absolute folder, so the exe would use the real data root"
    local = (env.get("LOCALAPPDATA") or "").strip()
    if local and _under(data_root, os.path.join(local, "DesktopAICompanion")):
        return ISOLATION_DATA_ROOT + " is the installed app's data root"
    temp, tmp = (env.get("TEMP") or "").strip(), (env.get("TMP") or "").strip()
    if not temp or not tmp or not os.path.isabs(temp) or not os.path.isabs(tmp):
        return "TEMP or TMP is not set to an absolute folder"
    if _folder(temp) != _folder(tmp):
        return "TEMP and TMP are not the same lane folder"
    if local and _folder(temp) == _folder(os.path.join(local, "Temp")):
        return "TEMP is the shared per-user temp folder, not a lane folder"
    if not _under(work, temp):
        return "--work is not under TEMP"
    if (env.get(ISOLATION_NO_REAL_INPUT) or "").strip() != "1":
        return ISOLATION_NO_REAL_INPUT + " is not 1"
    return None


def isolation_units():
    """Each isolation fault refused by its own message, a complete isolation passing, and cli_legs refusing by name before
    it makes a folder or launches anything. No process is launched: the exe and converter paths name nothing."""
    lane = os.path.abspath(os.path.join(os.sep, "lane-unit"))
    local = os.path.abspath(os.path.join(os.sep, "profile-unit", "Local"))
    complete = {ISOLATION_DATA_ROOT: os.path.join(lane, "data"), "TEMP": os.path.join(lane, "temp"), "TMP": os.path.join(lane, "temp"),
                ISOLATION_NO_REAL_INPUT: "1", "LOCALAPPDATA": local}
    work = os.path.join(lane, "temp", "work")
    without = lambda name: {k: v for k, v in complete.items() if k != name}
    fault = lambda env, folder=work: refusal(lambda e: isolation_fault(e, folder), env) or ""
    check(isolation_fault(complete, work) is None, "policy CLI isolation unit: WITNESS a lane data root, lane TEMP and TMP, --work under TEMP and NO_REAL_INPUT=1 pass")
    check(fault(without(ISOLATION_DATA_ROOT)).startswith(ISOLATION_DATA_ROOT + " is not set"), "policy CLI isolation unit: a missing data root override is refused by name")
    check(fault(dict(complete, **{ISOLATION_DATA_ROOT: os.path.join(local, "DesktopAICompanion")})).startswith(ISOLATION_DATA_ROOT + " is the installed"),
          "policy CLI isolation unit: the installed app's data root as the override is refused by name")
    check(fault(without("TMP")).startswith("TEMP or TMP is not set"), "policy CLI isolation unit: a missing TMP is refused by name")
    check(fault(dict(complete, TMP=os.path.join(lane, "other"))).startswith("TEMP and TMP are not the same"), "policy CLI isolation unit: TEMP and TMP apart are refused by name")
    shared = os.path.join(local, "Temp")
    check(fault(dict(complete, TEMP=shared, TMP=shared), os.path.join(shared, "work")).startswith("TEMP is the shared per-user temp folder"),
          "policy CLI isolation unit: the shared per-user temp folder as TEMP is refused by name")
    check(fault(complete, os.path.join(lane, "elsewhere", "work")).startswith("--work is not under TEMP"), "policy CLI isolation unit: a --work folder outside TEMP is refused by name")
    check(fault(dict(complete, **{ISOLATION_NO_REAL_INPUT: "0"})).startswith(ISOLATION_NO_REAL_INPUT + " is not 1"),
          "policy CLI isolation unit: NO_REAL_INPUT other than 1 is refused by name")
    global SINK
    with tempfile.TemporaryDirectory(prefix="parity-policy-isolation-") as temporary:
        folder = Path(temporary) / "work"
        saved, SINK = SINK, []
        try:
            cli_legs(Path(temporary) / "no-exe" / "DesktopAICompanion.exe", Path(temporary) / "no-converter" / "converter.exe", folder, {})
            verdicts = SINK
        except Exception as e:
            verdicts = [(False, "threw " + repr(e))]
        finally:
            SINK = saved
        made = folder.exists()
    refused = len(verdicts) == 1 and not verdicts[0][0] and verdicts[0][1].startswith(ISOLATION_REFUSED + ISOLATION_DATA_ROOT) and not made
    if not refused:
        print("  detail: folder made " + str(made) + ", verdicts " + str(verdicts)[:300], flush=True)
    # The label is the whole line (--mutate matches FAIL lines exactly), so the detail goes on its own line above.
    check(refused, "policy CLI isolation unit: the CLI legs refuse to launch with no isolation, by name, before any folder or process")


def self_test():
    good = {"registry": REGISTRY_NAME, "id": "birth-cap", "case": "cap-at"}
    check(all(declaration_fault({"registry": REGISTRY_NAME, "id": i, "case": c}) is None for i, c in TUPLES),
          "policy grammar unit: WITNESS each declared tuple passes")
    check(faults(declaration_fault, {"registry": REGISTRY_NAME, "id": "birth-cap"}), "policy grammar unit: a missing policy member is refused")
    check(faults(declaration_fault, dict(good, note="x")), "policy grammar unit: an extra policy member is refused")
    check(faults(declaration_fault, dict(good, id=5)), "policy grammar unit: a policy member of the wrong type is refused")
    # The string-type guard is NOT an equivalent mutant (review of round 8, which corrected round 8's record): without it a
    # list- or object-valued id or case reaches the tuple lookup, which raises TypeError (unhashable type) instead of
    # refusing, and an exception is no refusal. A number such as 5 is hashable and is still refused at the tuple, so only
    # these three units show the guard itself working; each must get the guard's own message.
    string_refusal = "a policy member is not a string"
    check(refusal(declaration_fault, dict(good, id=["birth-cap"])) == string_refusal,
          "policy grammar unit: a list-valued policy id is refused as a non-string member")
    check(refusal(declaration_fault, dict(good, case=["cap-at"])) == string_refusal,
          "policy grammar unit: a list-valued policy case is refused as a non-string member")
    check(refusal(declaration_fault, dict(good, id={"id": "birth-cap"})) == string_refusal,
          "policy grammar unit: an object-valued policy id is refused as a non-string member")
    check(faults(declaration_fault, dict(good, registry="shimeji-parity-policy/2")), "policy grammar unit: another registry is refused")
    check(faults(declaration_fault, dict(good, case="window-carry-off")), "policy grammar unit: a crossed tuple is refused")
    # A number, not a list: set() of a list or string still differs from the three names, so only a number shows the
    # object guard itself working.
    check(faults(declaration_fault, 5), "policy grammar unit: a policy that is not an object is refused")

    def refused(text):
        try:
            strict_json(text.encode("utf-8"))
            return False
        except GrammarFault:
            return True

    check(refused('{"policy":{"registry":"shimeji-parity-policy/1","id":"birth-cap","id":"birth-cap","case":"cap-at"}}'),
          "policy grammar unit: a repeated member in the policy object is refused")
    check(refused('{"a":{"b":{"c":1,"c":1}}}'), "policy grammar unit: a repeated member three objects deep is refused")
    check(refused('{"x":NaN}'), "policy grammar unit: a NaN constant is refused")
    check(not refused('{"a":{"b":{"c":1,"d":1}}}'), "policy grammar unit: WITNESS distinct members at every depth parse")
    base = {"schema": SCENARIO2, "id": "S", "pack": "p", "screens": [], "workArea": {}, "dpiScale": 1.0, "ticks": 1, "rng": {}, "settings": {},
            "mascots": [], "policy": good}
    check(scenario_fault(base) is None, "policy grammar unit: WITNESS a complete scenario/2 base passes")
    check(faults(scenario_fault, dict(base, schema="shimeji-parity-scenario/1")), "policy grammar unit: a scenario/1 schema is refused")
    check(faults(scenario_fault, {k: v for k, v in base.items() if k != "policy"}), "policy grammar unit: a scenario with no policy is refused")
    check(faults(tuple_fault, [{"id": "a", "case": "b", "scenarioId": "S1"}, {"id": "a", "case": "b", "scenarioId": "S2"}]),
          "policy grammar unit: a repeated registry tuple is refused")
    check(faults(tuple_fault, [{"id": "a", "case": "b", "scenarioId": "S1"}, {"id": "a", "case": "c", "scenarioId": "S1"}]),
          "policy grammar unit: a repeated registry scenarioId is refused")
    isolation_units()


# Each guard broken in a private copy of this file; the copy's --self-test must FAIL on the named unit. Never edits the
# tracked file.
UNIT = "policy grammar unit: "
ISO = "policy CLI isolation unit: "
MUTATIONS = (
    ("missing or extra members accepted", '    if set(policy) != {"registry", "id", "case"}:\n', '    if False:\n',
     (UNIT + "a missing policy member is refused", UNIT + "an extra policy member is refused")),
    ("registry constant unchecked", '    if policy["registry"] != REGISTRY_NAME:\n', '    if False:\n', (UNIT + "another registry is refused",)),
    ("crossed tuples accepted", '    if (policy["id"], policy["case"]) not in TUPLES:\n', '    if False:\n', (UNIT + "a crossed tuple is refused",)),
    ("a policy that is not an object accepted", '    if not isinstance(policy, dict):\n', '    if False:\n',
     (UNIT + "a policy that is not an object is refused",)),
    ("repeated members collapse last-wins", '        return json.loads(text, object_pairs_hook=_pairs, parse_constant=_constant)\n',
     '        return json.loads(text, parse_constant=_constant)\n',
     (UNIT + "a repeated member three objects deep is refused", UNIT + "a repeated member in the policy object is refused")),
    ("NaN and Infinity accepted", '        return json.loads(text, object_pairs_hook=_pairs, parse_constant=_constant)\n',
     '        return json.loads(text, object_pairs_hook=_pairs)\n', (UNIT + "a NaN constant is refused",)),
    ("scenario schema unchecked", '    if doc.get("schema") != SCENARIO2:\n', '    if False:\n', (UNIT + "a scenario/1 schema is refused",)),
    ("policy not a required member", '"mascots", "policy")\n', '"mascots")\n', (UNIT + "a scenario with no policy is refused",)),
    ("registry tuple uniqueness unchecked", '    if len(set(pairs)) != len(pairs):\n', '    if False:\n', (UNIT + "a repeated registry tuple is refused",)),
    ("registry scenarioId uniqueness unchecked", '    if len(set(ids)) != len(ids):\n', '    if False:\n',
     (UNIT + "a repeated registry scenarioId is refused",)),
    ("string-type guard removed", '    if not all(isinstance(policy[k], str) for k in ("registry", "id", "case")):\n', '    if False:\n',
     (UNIT + "a list-valued policy id is refused as a non-string member", UNIT + "a list-valued policy case is refused as a non-string member",
      UNIT + "an object-valued policy id is refused as a non-string member")),
    # The CLI legs' isolation (round 9): each condition, and the refusal at the top of cli_legs.
    ("isolation data root unchecked", '    if not data_root or not os.path.isabs(data_root):\n', '    if False:\n',
     (ISO + "a missing data root override is refused by name",)),
    ("isolation installed data root accepted", '    if local and _under(data_root, os.path.join(local, "DesktopAICompanion")):\n', '    if False:\n',
     (ISO + "the installed app's data root as the override is refused by name",)),
    ("isolation TEMP and TMP presence unchecked", '    if not temp or not tmp or not os.path.isabs(temp) or not os.path.isabs(tmp):\n', '    if False:\n',
     (ISO + "a missing TMP is refused by name",)),
    ("isolation TEMP and TMP apart accepted", '    if _folder(temp) != _folder(tmp):\n', '    if False:\n', (ISO + "TEMP and TMP apart are refused by name",)),
    ("isolation shared temp accepted", '    if local and _folder(temp) == _folder(os.path.join(local, "Temp")):\n', '    if False:\n',
     (ISO + "the shared per-user temp folder as TEMP is refused by name",)),
    ("isolation work outside TEMP accepted", '    if not _under(work, temp):\n', '    if False:\n', (ISO + "a --work folder outside TEMP is refused by name",)),
    ("isolation NO_REAL_INPUT unchecked", '    if (env.get(ISOLATION_NO_REAL_INPUT) or "").strip() != "1":\n', '    if False:\n',
     (ISO + "NO_REAL_INPUT other than 1 is refused by name",)),
    ("CLI legs launch without the isolation check", '    why = isolation_fault(env, work)\n    if why is not None:\n',
     '    why = isolation_fault(env, work)\n    if False:\n',
     (ISO + "the CLI legs refuse to launch with no isolation, by name, before any folder or process",)),
)


# Each artefact the build proof reads, broken in a private copy of the policy folder or of ParityPolicyRegistry.cs; the
# proof run on that copy must FAIL the named check. (target, old, new, the check's label prefix). Never edits a tracked file.
PROOF = "policy build proof: "
ARTEFACT_MUTATIONS = (
    ("registry byte changed", "REGISTRY.json", b'"SPECIFICATION-ONLY;', b'"SPECIFICATION-ONLX;',
     PROOF + "the installed registry and declaration schema equal their signed SHA-256"),
    ("registry member repeated", "REGISTRY.json", b' "registry": "shimeji-parity-policy/1",',
     b' "registry": "shimeji-parity-policy/1",\n "registry": "shimeji-parity-policy/1",', PROOF + "the registry parses strictly"),
    ("registry tuple repeated", "REGISTRY.json", b'"case": "window-minimize-off"', b'"case": "window-throw-off"',
     PROOF + "the registry's tuples are unique"),
    ("cap-at input byte changed", "inputs/S-cap-at-01.json", b'"description": "', b'"description": "X', PROOF + "S-cap-at-01 parses strictly"),
    ("carry input byte changed", "inputs/S-window-carry-off-01.json", b'"description": "', b'"description": "X',
     PROOF + "S-window-carry-off-01 parses strictly"),
    ("throw input byte changed", "inputs/S-window-throw-off-01.json", b'"description": "', b'"description": "X',
     PROOF + "S-window-throw-off-01 parses strictly"),
    ("minimize input byte changed", "inputs/S-window-minimize-off-01.json", b'"description": "', b'"description": "X',
     PROOF + "S-window-minimize-off-01 parses strictly"),
    ("cap-at pack byte changed", "inputs/packs/breed-cap/img/Alpha/conf/behaviors.xml", b"<", b" <", PROOF + "S-cap-at-01 parses strictly"),
    ("compiled row hex flipped", "cs", b'"4914fc64', b'"5914fc64', PROOF + "every compiled row of ParityPolicyRegistry.cs"),
)


def proof_on(root):
    """The build proof's verdicts on the private copy under root (policy/ and ParityPolicyRegistry.cs)."""
    global SINK
    SINK = []
    try:
        build_proof(root / "policy", root / "ParityPolicyRegistry.cs")
    except Exception as e:
        SINK.append((False, "threw " + repr(e)))
    results, SINK = SINK, None
    return results


def mutate():
    with tempfile.TemporaryDirectory(prefix="parity-policy-artefact-") as temporary:
        clean = Path(temporary) / "clean"
        shutil.copytree(POLICY, clean / "policy")
        shutil.copyfile(REGISTRY_CS, clean / "ParityPolicyRegistry.cs")
        results = proof_on(clean)
        check(len(results) == 8 and all(ok for ok, _ in results),
              "policy build proof mutation: WITNESS an unbroken private copy passes all " + str(len(results)) + " proof checks")
        for index, (name, target, old, new, label) in enumerate(ARTEFACT_MUTATIONS):
            root = Path(temporary) / ("m" + str(index))
            shutil.copytree(POLICY, root / "policy")
            shutil.copyfile(REGISTRY_CS, root / "ParityPolicyRegistry.cs")
            path = root / "ParityPolicyRegistry.cs" if target == "cs" else root / "policy" / target
            data = path.read_bytes()
            if old not in data:
                check(False, "policy build proof mutation " + name + ": its old bytes are not in " + target)
                continue
            path.write_bytes(data.replace(old, new, 1))
            print("EXECUTED artefact mutant " + name + " sha256=" + sha(path.read_bytes()), flush=True)
            results = proof_on(root)
            check(any(not ok and found.startswith(label) for ok, found in results),
                  "policy build proof mutation " + name + " FIRED on '" + label + "'")
    source = Path(__file__).read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory(prefix="parity-policy-mutate-") as temporary:
        for name, old, new, labels in MUTATIONS:
            if source.count(old) != 1:
                check(False, "policy grammar mutation " + name + ": its old text occurs " + str(source.count(old)) + " times")
                continue
            copy = Path(temporary) / "mutant.py"
            copy.write_text(source.replace(old, new), encoding="utf-8", newline="\n")
            # The copy runs in a fresh interpreter; its sha proves the mutated bytes are what ran.
            result = subprocess.run([sys.executable, "-I", "-B", str(copy), "--self-test"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", timeout=120)
            fired = result.returncode == 1 and all(("FAIL: " + label) in result.stdout.splitlines() for label in labels)
            print("EXECUTED mutant " + name + " sha256=" + sha(copy.read_bytes()), flush=True)
            check(fired, "policy grammar mutation " + name + " FIRED on '" + "' and '".join(labels) + "'")


def launch(argv, cwd, env):
    # Exactly the environment isolation_fault checked, never one read again here.
    result = subprocess.run([str(a) for a in argv], cwd=str(cwd), env=dict(env), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            timeout=300)
    return result.returncode, result.stdout.decode("utf-8", "replace").replace("\r", ""), result.stderr.decode("utf-8", "replace")


def line(text, prefix):
    for item in text.split("\n"):
        if item.startswith(prefix):
            return item
    return None


def run_id(item):
    return None if item is None or " runId " not in item else item.rsplit(" runId ", 1)[1]


def cli_legs(exe, converter, work, env):
    exe, converter, work = Path(exe), Path(converter), Path(work)
    # Before any folder is made or any process launched: a run under the real data root, the shared temp folder or with
    # real input allowed is refused by name, and no CLI leg runs.
    why = isolation_fault(env, work)
    if why is not None:
        check(False, ISOLATION_REFUSED + why)
        return
    if work.exists():
        check(False, "policy CLI: the work folder " + str(work) + " must not exist yet")
        return
    work.mkdir(parents=True)
    engine_sha = sha((exe.parent / "DesktopAICompanion.dll").read_bytes())
    registry = strict_json((POLICY / "REGISTRY.json").read_bytes())
    seen = []
    for row in registry["cases"]:
        pets = work / "pets" / row["case"]
        code, out, err = launch([converter, "paritypack", POLICY / "inputs" / row["pack"], pets], work, env)
        if code != 0:
            check(False, "policy CLI: the converter converts " + row["pack"] + " (exit " + str(code) + ": " + out[-400:] + err[-400:] + ")")
            continue
        trace = work / (row["scenarioId"] + ".jsonl")
        code, out, err = launch([exe, "--shimeji-parity-run", POLICY / "inputs" / (row["scenarioId"] + ".json"), pets, trace, converter.parent], work, env)
        (work / (row["scenarioId"] + ".log")).write_text(out + err, encoding="utf-8")
        engine = line(out, "parity loaded-engine ")
        rid = run_id(engine)
        ok_line = line(out, "parity registry-integrity ")
        admitted = line(out, "parity INPUT-ADMITTED ")
        want = ("parity INPUT-ADMITTED registrySha256 " + SIGNED_REGISTRY + " id " + row["id"] + " case " + row["case"] + " scenarioSha256 "
                + row["scenarioSha256"] + " packSha256 " + row["packSha256"] + " runId " + str(rid))
        text = trace.read_text(encoding="utf-8") if trace.exists() else ""
        header = json.loads(text.split("\n", 1)[0]) if text else {}
        footer = json.loads(text.rstrip("\n").rsplit("\n", 1)[-1]) if text else {}
        # This process's own ok line: the signed digest and this run's runId, or None (a defect line, or none at all).
        seen.append(rid if ok_line is not None and ok_line.startswith("parity registry-integrity ok registrySha256 " + SIGNED_REGISTRY + " ")
                    and run_id(ok_line) == rid else None)
        check(code == 0 and rid is not None and len(rid) == 36 and admitted == want and ok_line is not None
              and ok_line.startswith("parity registry-integrity ok registrySha256 " + SIGNED_REGISTRY + " engineSha256 " + engine_sha + " ")
              and run_id(ok_line) == rid and header.get("runId") == rid and footer.get("status") == "complete"
              and out.index(ok_line) < out.index(admitted),
              "policy CLI: " + row["scenarioId"] + " is admitted in a fresh process: its own registry-integrity ok line, INPUT-ADMITTED with"
              " every field, a complete trace with one runId (exit " + str(code) + ", " + str(admitted) + ")")
    check(len(seen) == 4 and None not in seen and len(set(seen)) == 4,
          "policy CLI: each of the four fresh processes logged its own registry-integrity ok line under its own runId (" + str(seen) + ")")

    # An unregistered candidate: the registered cap-at file with one description byte changed, beside a copy of its pack.
    unregistered = work / "unregistered"
    shutil.copytree(POLICY / "inputs" / "packs" / "breed-cap", unregistered / "packs" / "breed-cap")
    data = (POLICY / "inputs" / "S-cap-at-01.json").read_bytes().replace(b"Alpha breeds", b"Alpha Breeds", 1)
    (unregistered / "S-cap-at-01.json").write_bytes(data)
    trace = work / "unregistered.jsonl"
    code, out, err = launch([exe, "--shimeji-parity-run", unregistered / "S-cap-at-01.json", work / "pets" / "cap-at", trace, converter.parent], work, env)
    ok_line = line(out, "parity registry-integrity ok ")
    check(code == 2 and not trace.exists() and ok_line is not None and "parity INPUT-ADMITTED" not in out
          and ("scenario-invalid: scenario-not-registered: the scenario's full SHA-256 " + sha(data) + " is no registered row") in out,
          "policy CLI: an unregistered candidate is scenario-not-registered after its registry-integrity ok line, exit 2, no trace (exit " + str(code) + ")")

    # OUTPUT-PRESERVATION: an occupied trace path with a registered input is the claim's refusal only.
    occupied = work / "occupied.jsonl"
    occupied.write_bytes(b"sentinel\n")
    code, out, err = launch([exe, "--shimeji-parity-run", POLICY / "inputs" / "S-cap-at-01.json", work / "pets" / "cap-at", occupied, converter.parent], work, env)
    check(code == 2 and "is already there" in out and "parity registry-integrity" not in out and "parity INPUT-ADMITTED" not in out
          and occupied.read_bytes() == b"sentinel\n",
          "policy CLI: an occupied trace path plus a registered input gives the occupied refusal only, sentinel unchanged (exit " + str(code) + ")")

    # A registered scenario beside a readable wrong pack: pack-not-registered, exit 2, no trace.
    wrong = work / "wrong"
    (wrong / "packs" / "breed-cap").mkdir(parents=True)
    (wrong / "packs" / "breed-cap" / "readme.txt").write_bytes(b"not the registered pack\n")
    shutil.copyfile(POLICY / "inputs" / "S-cap-at-01.json", wrong / "S-cap-at-01.json")
    trace = work / "wrong.jsonl"
    code, out, err = launch([exe, "--shimeji-parity-run", wrong / "S-cap-at-01.json", work / "pets" / "cap-at", trace, converter.parent], work, env)
    check(code == 2 and not trace.exists() and "scenario-invalid: pack-not-registered: the pack " in out and "parity INPUT-ADMITTED" not in out,
          "policy CLI: a registered scenario beside a readable wrong pack is pack-not-registered, exit 2, no trace (exit " + str(code) + ")")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--mutate", action="store_true")
    parser.add_argument("--exe")
    parser.add_argument("--converter")
    parser.add_argument("--work")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    elif args.mutate:
        mutate()
    else:
        build_proof()
        if args.exe or args.converter or args.work:
            if not (args.exe and args.converter and args.work):
                check(False, "policy CLI: --exe, --converter and --work go together")
            else:
                cli_legs(args.exe, args.converter, args.work, os.environ)
    print("RESULT=" + ("PASS" if not FAILED else "FAIL (" + str(len(FAILED)) + ")"), flush=True)
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
