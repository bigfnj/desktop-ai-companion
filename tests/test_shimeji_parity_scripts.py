"""Focused, offline witnesses for the scenario generator and the comparer gate.

Run with the Python used for scenario generation (Pillow is needed for generated art).
--mutate checks private script copies and never edits tracked files. It needs no compiler,
oracle cache, private corpus, network, desktop input or live companion.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[1]
GENERATOR = ROOT / "tools/ShimejiParity/scenarios/make_packs.py"
RUNNER = ROOT / "tests/Invoke-OfflineScriptChecks.ps1"


def require(condition, label):
    if not condition:
        raise AssertionError(label)


def execute(command):
    result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", timeout=90)
    return result.returncode, result.stdout


def generator_result(path):
    # Python executes this exact file in a fresh process, with no bytecode cache to reuse.
    print(f"EXECUTED generator sha256={hashlib.sha256(path.read_bytes()).hexdigest()}", flush=True)
    return execute([sys.executable, "-B", str(path), "--self-test"])


def check_generator(path):
    code, output = generator_result(path)
    require(code == 0 and output.splitlines().count("MAKE-PACKS SELF-TEST PASS") == 1,
            "GENERATOR_SELF_TEST: " + output)
    print("PASS generator: fresh and reused destinations yield identical parity inputs")


def check_runner(source):
    # Run the real runner's parity path in Windows PowerShell 5.1. Only the comparer is a synthetic child:
    # these deliberately wrong outcomes prove that the wrapper checks both parts of its success contract.
    with tempfile.TemporaryDirectory(prefix="parity-offline-runner-") as temporary:
        root = Path(temporary)
        runner = root / "tests/Invoke-OfflineScriptChecks.ps1"
        comparer = root / "tools/ShimejiParity/comparer/compare.py"
        runner.parent.mkdir()
        comparer.parent.mkdir(parents=True)
        runner.write_text(source, encoding="utf-8-sig", newline="\n")
        print(f"EXECUTED offline runner sha256={hashlib.sha256(runner.read_bytes()).hexdigest()}", flush=True)

        def run(lines, exit_code):
            comparer.write_text("import sys\n"
                                "if sys.argv[1:] != ['--self-test']:\n"
                                "    raise RuntimeError('wrong comparer arguments')\n"
                                + "\n".join(f"print({line!r})" for line in lines)
                                + f"\nsys.exit({exit_code})\n", encoding="utf-8", newline="\n")
            return execute(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(runner),
                            "-ParityOnly", "-PythonPath", sys.executable])

        code, output = run(["SELF-TEST PASS"], 0)
        require(code == 0 and "PASS: 1 offline script checks." in output,
                "OFFLINE_RUNNER_WITNESS: a zero exit and exact PASS line must pass: " + output)
        code, output = run(["SELF-TEST PASS"], 7)
        require(code != 0 and "Shimeji parity comparer self-test failed (exit 7)" in output,
                "OFFLINE_RUNNER_NONZERO: a nonzero exit must fail even with a PASS line: " + output)
        code, output = run([], 0)
        require(code != 0 and "did not print exactly one SELF-TEST PASS line" in output,
                "OFFLINE_RUNNER_MARKER: zero exit without the PASS line must fail: " + output)
        code, output = run(["prefix SELF-TEST PASS"], 0)
        require(code != 0 and "did not print exactly one SELF-TEST PASS line" in output,
                "OFFLINE_RUNNER_EXACT_MARKER: a substring must not satisfy the PASS line: " + output)
        code, output = run(["SELF-TEST PASS", "SELF-TEST PASS"], 0)
        require(code != 0 and "did not print exactly one SELF-TEST PASS line" in output,
                "OFFLINE_RUNNER_ONE_MARKER: repeated PASS lines must fail: " + output)
    print("PASS offline runner: healthy control, nonzero exit, missing, partial and duplicate markers")


def mutate(generator_source, runner_source, only):
    cases = []
    # ---- burndown/codex-parity ----
    if only in ("all", "generator"):
        cases += [
            ("bd-codex-parity: retain stale pack contents", "generator",
             "        shutil.rmtree(packs)\n", "        pass\n", "PACK_GENERATOR_STALE_MEMBER"),
            ("bd-codex-parity: retain retired scenario", "generator",
             "        previous.unlink()\n", "        pass\n", "PACK_GENERATOR_STALE_SCENARIO"),
            ("bd-codex-parity: clear unmanaged output", "generator",
             "if actual and not manifest.exists():", "if False:", "PACK_GENERATOR_UNMANAGED_PRESERVED"),
            ("bd-codex-parity: clear custom output added beside owned paths", "generator",
             "        if unknown:\n", "        if False:\n", "PACK_GENERATOR_CUSTOM_PACK_PRESERVED"),
        ]
    if only in ("all", "offline-runner"):
        cases += [
            ("bd-codex-parity: ignore comparer nonzero exit", "runner",
             "if ($parityExit -ne 0)", "if ($false)", "OFFLINE_RUNNER_NONZERO"),
            ("bd-codex-parity: ignore comparer missing marker", "runner",
             "if (@($parityOutput | Where-Object { [string]$_ -ceq 'SELF-TEST PASS' }).Count -ne 1)",
             "if ($false)", "OFFLINE_RUNNER_MARKER"),
        ]
    fired = 0
    with tempfile.TemporaryDirectory(prefix="parity-script-mutations-") as temporary:
        for name, family, old, new, witness in cases:
            source = generator_source if family == "generator" else runner_source
            require(source.count(old) == 1, f"MUTATION_ANCHOR: {name}: matched {source.count(old)} times")
            changed = source.replace(old, new)
            if family == "generator":
                path = Path(temporary) / "make_packs.py"
                path.write_text(changed, encoding="utf-8", newline="\n")
                code, output = generator_result(path)
                require(code != 0 and witness in output and "MAKE-PACKS SELF-TEST PASS" not in output,
                        f"SURVIVED: {name}: {output}")
            else:
                try:
                    check_runner(changed)
                except AssertionError as error:
                    require(str(error).startswith(witness + ":"), f"WRONG_WITNESS: {name}: {error}")
                else:
                    raise AssertionError("SURVIVED: " + name)
            fired += 1
            print(f"FIRED: {name}: {witness}")
    require(fired == len(cases) and fired > 0, "MUTATION_EMPTY: no executed mutations")
    print(f"MUTATIONS PASS: {fired}/{len(cases)} FIRED; private script copies only")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--only", choices=("all", "generator", "offline-runner"), default="all")
    parser.add_argument("--mutate", action="store_true")
    args = parser.parse_args()
    before = {path: path.read_bytes() for path in (GENERATOR, RUNNER)}
    generator_source, runner_source = (before[path].decode("utf-8-sig") for path in (GENERATOR, RUNNER))
    try:
        if args.only in ("all", "generator"):
            check_generator(GENERATOR)
        if args.only in ("all", "offline-runner"):
            check_runner(runner_source)
        if args.mutate:
            mutate(generator_source, runner_source, args.only)
    finally:
        require(all(path.read_bytes() == content for path, content in before.items()),
                "TRACKED_SOURCE_CHANGED: focused script checks modified tracked source")
    print("SHIMEJI PARITY SCRIPT CHECKS PASS; tracked source unchanged")
    return 0


if __name__ == "__main__":
    sys.exit(main())
