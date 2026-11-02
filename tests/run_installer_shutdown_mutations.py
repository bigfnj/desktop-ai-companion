"""Focused negative witnesses for installation-scoped shutdown.

Default: print a read-only plan. Execute in an exclusive native build window:
  python -X utf8 tests/run_installer_shutdown_mutations.py --apply
  python -X utf8 tests/run_installer_shutdown_mutations.py --apply --only=installer-tests:
Check this runner's own restoration on synthetic files in a temporary folder (no build, no repository file):
  python -X utf8 tests/run_installer_shutdown_mutations.py --self-test

Only the explicit native sources a variant names are temporarily changed. Every
negative check uses fake processes in memory or metadata of absent GUID-named
paths, never native process discovery or live input. Original bytes and hashes
of every named source are persisted before the first build, restored in
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
POLICY = "installer/ScopedShutdown/ShutdownPolicy.cpp"
PLATFORM = "installer/ScopedShutdown/NativePlatform.cpp"
BUILD = ROOT / "installer/build-scoped-shutdown.ps1"
CHECKS = ROOT / "build/scoped-shutdown/ShutdownChecks.exe"
# The healthy count is read from the wrapper's own expectation, so one literal
# (tests/Test-InstallerShutdown.ps1 `unit = N`) moves when checks are added.
WRAPPER = ROOT / "tests/Test-InstallerShutdown.ps1"
# (name, fake suite, source, before, after, label the failure must name or None)
VARIANTS = (
    ("basename-selection", "paths", POLICY,
     '        for (const auto& image : context.installedImages) installed |= SamePath(identity.image, image);',
     '        for (const auto& image : context.installedImages) { (void)image; installed = true; }', None),
    ("legacy-refusal", "legacy", POLICY,
     '            if (context.legacyTransition) throw Failure("legacy_foreign_companion_present");',
     '            (void)context.legacyTransition;', None),
    ("owner-prevalidation", "prevalidation", POLICY,
     '        if (identity.userSid != context.userSid) throw Failure("installed_companion_owner_mismatch");',
     '        (void)context.userSid;', None),
    ("retained-fallback-identity", "changed-identity", POLICY,
     '        if (!SameIdentity(target.identity, target.process->ReadIdentity())) throw Failure("companion_identity_changed");\n'
     '        target.process->Terminate();',
     '        target.process->Terminate();', None),
    # ---- lane feature/installer-tests ----
    ("installer-tests:cached-package-missing", "paths", PLATFORM,
     'std::wstring CanonicalCachedPackage(const std::wstring& path) { return Canonical(path, false, "cached_package_missing"); }',
     'std::wstring CanonicalCachedPackage(const std::wstring& path) { return Canonical(path, false, "installation_path_unavailable"); }',
     "a missing registered cached package is named as missing"),
    # ---- lane feature/followups ----
    # N-installer-tests-01: the missing cached package's setup error as it shipped, put back ahead of the new text, so it
    # names the missing copy and says nothing about restoring it.
    ("followups:cached-package-recovery", "paths", POLICY,
     "    if (tag == L\"cached_package_missing\")\n"
     "        return L\"Windows Installer's cached copy of an installed Desktop AI Companion package is missing, so setup cannot check how that version closes, \"",
     "    if (tag == L\"cached_package_missing\")\n"
     "        return L\"Windows Installer's cached copy of an installed Desktop AI Companion package is missing, so setup cannot check how that version closes. (\" + tag + L\")\";\n"
     "    if (tag == L\"cached_package_missing\")\n"
     "        return L\"Windows Installer's cached copy of an installed Desktop AI Companion package is missing, so setup cannot check how that version closes, \"",
     "the missing cached package's error says how an administrator restores it"),
)


def healthy_count() -> int:
    found = re.findall(r"\bunit = (\d+);", WRAPPER.read_text(encoding="utf-8"))
    if len(found) != 1:
        raise RuntimeError("The wrapper's unit check count was not found exactly once.")
    return int(found[0])


def file_label(name: str) -> str:
    return name.replace(":", "-")


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write_new(path: Path, raw: bytes) -> None:
    refuse_links(path)
    with path.open("xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())


def restore_owned(root: Path, sources: list[str], original: dict, current: dict) -> None:
    """Put back every source this run verifiably owns, then refuse once, naming every source left as found.

    Owned means its bytes are the original or this run's mutation. A source that cannot be read (missing, unreadable),
    holds anything else, or cannot be written is never recreated or overwritten here; it is named after the loop, and
    the backups in the evidence folder stay for it. Stopping at the first such file left a later source still mutated:
    first a foreign edit (Codex 01a1855c), then an I/O error on an earlier source (Codex review 2026-11-02).
    """
    left = []
    for relative in sources:
        path = root / relative
        try:
            raw = path.read_bytes()
        except OSError as error:
            left.append(relative + " (unreadable: " + type(error).__name__ + ")")
            continue
        if raw not in (original[relative], current[relative]):
            left.append(relative + " (differs from original and owned mutation)")
            continue
        try:
            path.write_bytes(original[relative])
        except OSError as error:
            left.append(relative + " (could not be restored: " + type(error).__name__ + ")")
    if left:
        raise RuntimeError("Source not restored; manual recovery required: " + "; ".join(left))


def self_test() -> int:
    """restore_owned on synthetic files, in the order the runner restores the real ones (the sorted variant sources).

    Each case leaves the EARLIER source in a state this run must not touch, and the later one holding this run's
    mutation: the later one must be restored, the earlier one left exactly as found, and the refusal must name the
    earlier one alone. Everything is under a fresh temporary folder; no repository file is read or written.
    """
    import shutil
    import stat
    import tempfile
    names = sorted({variant[2] for variant in VARIANTS})
    if len(names) < 2:
        names.append("synthetic/Later.cpp")
    earlier, later = names[0], names[-1]
    original = {name: b"synthetic original " + name.encode() + b"\n" for name in names}
    mutated = {name: b"synthetic owned mutation " + name.encode() + b"\n" for name in names}
    foreign = b"synthetic foreign edit\n"
    # (case, how the earlier source starts, the word its refusal must carry, or None for no refusal)
    cases = (("all-owned", "owned", None), ("foreign-earlier-source", "foreign", "differs"),
             ("missing-earlier-source", "missing", "unreadable"),
             ("unwritable-earlier-source", "readonly", "could not be restored"))
    failed = []
    for case, earlier_state, word in cases:
        folder = Path(tempfile.mkdtemp(prefix="installer-shutdown-restore-selftest-"))
        try:
            for name in names:
                (folder / name).parent.mkdir(parents=True, exist_ok=True)
                (folder / name).write_bytes(mutated[name])
            first = folder / earlier
            if earlier_state == "foreign":
                first.write_bytes(foreign)
            elif earlier_state == "missing":
                first.unlink()
            elif earlier_state == "readonly":
                os.chmod(first, stat.S_IREAD)
                if os.access(first, os.W_OK):
                    raise AssertionError("this host cannot make a file unwritable, so the case cannot be produced")
            before = first.read_bytes() if first.exists() else None
            try:
                restore_owned(folder, names, original, mutated)
                refusal = None
            except RuntimeError as error:
                refusal = str(error)
            if (folder / later).read_bytes() != original[later]:
                raise AssertionError("the later owned mutation was not restored")
            after = first.read_bytes() if first.exists() else None
            if word is None:
                if refusal is not None or after != original[earlier]:
                    raise AssertionError("an owned source was not restored without a refusal: " + str(refusal))
            else:
                if after != before:
                    raise AssertionError("the earlier source was changed or recreated")
                if refusal is None or (earlier + " (" + word) not in refusal or later in refusal:
                    raise AssertionError("the refusal did not name the earlier source alone: " + str(refusal))
            print("PASS " + case)
        except Exception as error:  # every case runs; each failure is named
            failed.append(case)
            print("FAIL " + case + ": " + type(error).__name__ + ": " + str(error))
        finally:
            for path in folder.rglob("*"):
                if path.is_file():
                    os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
            shutil.rmtree(folder)
    print(("FAIL %d of %d" % (len(failed), len(cases))) if failed else ("PASS %d restoration cases" % len(cases)))
    return 1 if failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--only", default="", help="run only variants whose name contains this text")
    parser.add_argument("--self-test", action="store_true", help="check the restoration on synthetic files only")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    selected = [variant for variant in VARIANTS if args.only in variant[0]]
    if not selected:
        print("No variant name contains: " + args.only, file=sys.stderr)
        return 2
    if not args.apply:
        print(json.dumps({"readOnly": True, "healthyChecks": healthy_count(),
                          "variants": [{"name": n, "fakeSuite": s, "source": src} for n, s, src, _, _, _ in selected]},
                         indent=2))
        return 0
    expected_healthy = healthy_count()
    sources = sorted({variant[2] for variant in selected})
    original = {}
    for relative in sources:
        refuse_links(ROOT / relative)
        original[relative] = (ROOT / relative).read_bytes()
    # Validate every anchor before acquiring a build window or writing mutations.
    for _, _, relative, before, after, _ in selected:
        replace_exact(original[relative], before, after)
    root = ROOT / "build"
    refuse_links(root)
    root.mkdir(exist_ok=True)
    evidence = root / ("installer-shutdown-mutations-" + uuid.uuid4().hex)
    evidence.mkdir()
    backups = []
    for relative in sources:
        backup = Path(relative).name + ".original"
        write_new(evidence / backup, original[relative])
        backups.append({"source": relative, "backup": backup, "sha256": digest(original[relative])})
    write_new(evidence / "backup-manifest.json", json.dumps(backups, indent=2).encode())
    lock = root / "installer-shutdown-mutations.lock"
    write_new(lock, str(evidence.relative_to(ROOT)).encode())
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(),
              "sourceSha256": {relative: digest(raw) for relative, raw in original.items()},
              "only": args.only, "healthyChecks": expected_healthy,
              "negativeChecksUseFakeProcessesOnly": True, "commands": [], "witnesses": []}
    current = dict(original)
    print(str(evidence), flush=True)

    def command(label: str, argv: list[str], timeout: int = 180) -> dict:
        label = file_label(label)
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
        if result["exitCode"] != 0 or not re.search(r"(?m)^PASS %d checks\r?$" % expected_healthy, result["text"]):
            raise RuntimeError("Healthy fake suite did not pass exactly %d checks: %s" % (expected_healthy, label))

    def built_stamp() -> int:
        return CHECKS.stat().st_mtime_ns if CHECKS.exists() else 0

    primary = None
    try:
        build("before-build")
        healthy("before-healthy")
        for name, suite, relative, before, after, label in selected:
            path = ROOT / relative
            if path.read_bytes() != original[relative]:
                raise RuntimeError("Concurrent source change refused before mutation.")
            current[relative] = replace_exact(original[relative], before, after)
            path.write_bytes(current[relative])
            stamp = built_stamp()
            build(name + "-build")
            # A stale checker scores every mutant as survived; prove this one was rebuilt.
            rebuilt = built_stamp() > stamp
            result = command(name + "-negative", [str(CHECKS), "--case", suite])
            fired = rebuilt and result["exitCode"] == 1 and "FAIL ShutdownChecks.cpp:" in result["text"] and \
                (label is None or label in result["text"])
            report["witnesses"].append({"name": name, "suite": suite, "source": relative, "rebuilt": rebuilt,
                                        "fired": fired, "exitCode": result["exitCode"]})
            if not fired:
                raise RuntimeError("Negative witness did not fail in the focused new check: " + name)
            if path.read_bytes() != current[relative]:
                raise RuntimeError("Concurrent source change refused during mutation.")
            path.write_bytes(original[relative])
            current[relative] = original[relative]
    except Exception as error:
        primary = str(error)
    finally:
        try:
            # Restore every source this run owns before refusing anything (restore_owned; checked by --self-test).
            restore_owned(ROOT, sources, original, current)
            report["sourceRestoredExactly"] = all(
                digest((ROOT / relative).read_bytes()) == digest(original[relative]) for relative in sources)
            build("after-build")
            healthy("after-healthy")
        except Exception as error:
            primary = (primary + "; " if primary else "") + str(error)
        report["result"] = "PASS" if not primary and report.get("sourceRestoredExactly") and \
            len(report["witnesses"]) == len(selected) else "FAIL"
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
