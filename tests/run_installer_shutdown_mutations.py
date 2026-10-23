"""Focused negative witnesses for installation-scoped shutdown.

Default: print a read-only plan. Execute in an exclusive native build window:
  python -X utf8 tests/run_installer_shutdown_mutations.py --apply

Only the explicit policy source is temporarily changed. Every negative check
uses fake processes in memory, never native process discovery or live input.
Original bytes and hashes are persisted before the first build, restored in
finally, then the complete healthy fake suite is rebuilt and rerun.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

from run_agentbroker_pin_diagnostic_mutations import Runner, refuse_links, replace_exact

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "installer/ScopedShutdown/ShutdownPolicy.cpp"
BUILD = ROOT / "installer/build-scoped-shutdown.ps1"
CHECKS = ROOT / "build/scoped-shutdown/ShutdownChecks.exe"
VARIANTS = (
    ("basename-selection", "paths",
     '        for (const auto& image : context.installedImages) installed |= SamePath(identity.image, image);',
     '        for (const auto& image : context.installedImages) { (void)image; installed = true; }'),
    ("legacy-refusal", "legacy",
     '            if (context.legacyTransition) throw Failure("legacy_foreign_companion_present");',
     '            (void)context.legacyTransition;'),
    ("owner-prevalidation", "prevalidation",
     '        if (identity.userSid != context.userSid) throw Failure("installed_companion_owner_mismatch");',
     '        (void)context.userSid;'),
    ("retained-fallback-identity", "changed-identity",
     '        if (!SameIdentity(target.identity, target.process->ReadIdentity())) throw Failure("companion_identity_changed");\n'
     '        target.process->Terminate();',
     '        target.process->Terminate();'),
)


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write_new(path: Path, raw: bytes) -> None:
    refuse_links(path)
    with path.open("xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    if not args.apply:
        print(json.dumps({"readOnly": True, "source": str(SOURCE.relative_to(ROOT)),
                          "variants": [{"name": n, "fakeSuite": s} for n, s, _, _ in VARIANTS]}, indent=2))
        return 0
    refuse_links(SOURCE)
    original = SOURCE.read_bytes()
    # Validate every anchor before acquiring a build window or writing mutations.
    for _, _, before, after in VARIANTS:
        replace_exact(original, before, after)
    root = ROOT / "build"
    refuse_links(root)
    root.mkdir(exist_ok=True)
    evidence = root / ("installer-shutdown-mutations-" + uuid.uuid4().hex)
    evidence.mkdir()
    write_new(evidence / "ShutdownPolicy.cpp.original", original)
    write_new(evidence / "backup-manifest.json", json.dumps({
        "source": str(SOURCE.relative_to(ROOT)), "backup": "ShutdownPolicy.cpp.original", "sha256": digest(original)
    }, indent=2).encode())
    lock = root / "installer-shutdown-mutations.lock"
    write_new(lock, str(evidence.relative_to(ROOT)).encode())
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "sourceSha256": digest(original),
              "negativeChecksUseFakeProcessesOnly": True, "commands": [], "witnesses": []}
    current = original
    print(str(evidence), flush=True)

    def command(label: str, argv: list[str], timeout: int = 180) -> dict:
        entry = {"label": label, "exitCode": None}
        report["commands"].append(entry)
        with (evidence / (label + ".out.txt")).open("wb") as out, (evidence / (label + ".err.txt")).open("wb") as err:
            process = subprocess.Popen(argv, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=out, stderr=err)
            try:
                entry["exitCode"] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                entry["timedOut"] = True
                Runner.stop_owned_tree(process)
                raise RuntimeError("Owned build/check timed out: " + label)
        entry["text"] = (evidence / (label + ".out.txt")).read_text(encoding="utf-8", errors="replace") + \
                        (evidence / (label + ".err.txt")).read_text(encoding="utf-8", errors="replace")
        return entry

    def build(label: str) -> None:
        result = command(label, ["pwsh", "-NoProfile", "-File", str(BUILD), "-Tests"])
        if result["exitCode"] != 0:
            raise RuntimeError("Build failure is not a killed witness: " + label)

    def healthy(label: str) -> None:
        result = command(label, [str(CHECKS)])
        if result["exitCode"] != 0 or not re.search(r"(?m)^PASS 52 checks\r?$", result["text"]):
            raise RuntimeError("Healthy fake suite did not pass exactly 52 checks: " + label)

    primary = None
    try:
        build("before-build")
        healthy("before-healthy")
        for name, suite, before, after in VARIANTS:
            if SOURCE.read_bytes() != original:
                raise RuntimeError("Concurrent source change refused before mutation.")
            current = replace_exact(original, before, after)
            SOURCE.write_bytes(current)
            build(name + "-build")
            result = command(name + "-negative", [str(CHECKS), "--case", suite])
            fired = result["exitCode"] == 1 and "FAIL ShutdownChecks.cpp:" in result["text"]
            report["witnesses"].append({"name": name, "suite": suite, "fired": fired, "exitCode": result["exitCode"]})
            if not fired:
                raise RuntimeError("Negative witness did not fail in the focused new check: " + name)
            if SOURCE.read_bytes() != current:
                raise RuntimeError("Concurrent source change refused during mutation.")
            SOURCE.write_bytes(original)
            current = original
    except Exception as error:
        primary = str(error)
    finally:
        try:
            if SOURCE.read_bytes() not in (original, current):
                raise RuntimeError("Source differs from original and owned mutation; manual recovery required.")
            SOURCE.write_bytes(original)
            report["sourceRestoredExactly"] = SOURCE.read_bytes() == original
            build("after-build")
            healthy("after-healthy")
        except Exception as error:
            primary = (primary + "; " if primary else "") + str(error)
        report["result"] = "PASS" if not primary and len(report["witnesses"]) == len(VARIANTS) else "FAIL"
        if primary:
            report["failure"] = primary
        # Full text remains in logs; keep the summary compact and nonduplicative.
        for entry in report["commands"]:
            entry.pop("text", None)
        (evidence / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        lock.unlink()
    print(json.dumps({"result": report["result"], "evidence": str(evidence.relative_to(ROOT)),
                      "witnesses": report["witnesses"], "sourceRestoredExactly": report.get("sourceRestoredExactly")}, indent=2))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
