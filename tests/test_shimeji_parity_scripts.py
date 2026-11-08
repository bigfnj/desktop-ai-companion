"""Focused, offline witnesses for the scenario generator and the comparer gate.

Run with the Python used for scenario generation (Pillow is needed for generated art).
--mutate checks private script copies and never edits tracked files. It needs no compiler,
oracle cache, private corpus, network, desktop input or live companion.
"""
from __future__ import annotations

import argparse
import hashlib
import os
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


def execute(command, env=None):
    result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", timeout=90, env=env)
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
    # Run the real runner's parity path in Windows PowerShell 5.1. Both scripts are synthetic children:
    # these deliberately wrong outcomes prove that the wrapper checks both parts of its success contract.
    with tempfile.TemporaryDirectory(prefix="parity-offline-runner-") as temporary:
        root = Path(temporary)
        runner = root / "tests/Invoke-OfflineScriptChecks.ps1"
        comparer = root / "tools/ShimejiParity/comparer/compare.py"
        generator = root / "tools/ShimejiParity/scenarios/make_packs.py"
        runner.parent.mkdir()
        comparer.parent.mkdir(parents=True)
        generator.parent.mkdir(parents=True)
        runner.write_text(source, encoding="utf-8-sig", newline="\n")
        print(f"EXECUTED offline runner sha256={hashlib.sha256(runner.read_bytes()).hexdigest()}", flush=True)

        def child(path, lines, exit_code):
            path.write_text("import sys\n"
                                "if sys.argv[1:] != ['--self-test']:\n"
                                "    raise RuntimeError('wrong self-test arguments')\n"
                                + "\n".join(f"print({line!r})" for line in lines)
                                + f"\nsys.exit({exit_code})\n", encoding="utf-8", newline="\n")
        def run(lines, exit_code, generator_lines=("MAKE-PACKS SELF-TEST PASS",), generator_exit=0, no_pillow=False):
            child(comparer, lines, exit_code)
            child(generator, generator_lines, generator_exit)
            environment = None
            if no_pillow:
                # A private import shadow makes the dependency probe fail without changing the interpreter or its
                # installed packages. Neither synthetic child imports PIL, so a skipped probe would wrongly pass.
                (root / "PIL.py").write_text("raise ImportError('synthetic missing Pillow')\n", encoding="utf-8")
                environment = dict(os.environ, PYTHONPATH=str(root))
            return execute(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(runner),
                            "-ParityOnly", "-PythonPath", sys.executable], env=environment)

        code, output = run(["SELF-TEST PASS"], 0)
        require(code == 0 and "PASS: 2 offline script checks." in output,
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
        code, output = run(["SELF-TEST PASS"], 0, generator_exit=7)
        require(code != 0 and "Shimeji parity pack generator self-test failed (exit 7)" in output,
                "OFFLINE_GENERATOR_NONZERO: nonzero generator exit must fail even with its PASS line: " + output)
        for lines, label in (([], "MARKER"), (["prefix MAKE-PACKS SELF-TEST PASS"], "EXACT_MARKER"),
                             (["MAKE-PACKS SELF-TEST PASS"] * 2, "ONE_MARKER")):
            code, output = run(["SELF-TEST PASS"], 0, generator_lines=lines)
            require(code != 0 and "did not print exactly one MAKE-PACKS SELF-TEST PASS line" in " ".join(output.split()),
                    "OFFLINE_GENERATOR_" + label + ": invalid generator PASS marker must fail: " + output)
        code, output = run(["SELF-TEST PASS"], 0,
                           generator_lines=["MAKE-PACKS SELF-TEST PASS", "GENERATOR_MUST_NOT_RUN"], no_pillow=True)
        require(code != 0 and "self-test needs Pillow in the selected Python" in " ".join(output.split()) and "GENERATOR_MUST_NOT_RUN" not in output,
                "OFFLINE_GENERATOR_PILLOW: missing Pillow must fail explicitly before the generator runs: " + output)
    print("PASS offline runner: both scripts require exit zero and one exact marker; missing Pillow fails explicitly")


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
             '        unknown = actual - set(ownership["paths"])\n        if unknown:\n',
             '        unknown = actual - set(ownership["paths"])\n        if False:\n', "PACK_GENERATOR_INTERRUPTED_CUSTOM_PRESERVED"),
            ("bd-codex-parity: omit the provisional ownership journal before builders", "generator",
             "    write_output_manifest(out, owned, in_progress=True)\n", "", "PACK_GENERATOR_PROVISIONAL_BEFORE_BUILD"),
            ("bd-codex-parity: forget retired owned paths before cleanup", "generator",
             "write_output_manifest(out, owned | staged_paths, in_progress=True)",
             "write_output_manifest(out, staged_paths, in_progress=True)",
             "PACK_GENERATOR_RETIRED_OWNERSHIP"),
            ("bd-codex-parity: omit exact ownership before copying staged output", "generator",
             "        write_output_manifest(out, owned | staged_paths, in_progress=True)\n", "",
             "PACK_GENERATOR_COPY_RECOVERY"),
            ("bd-codex-parity: accept caller additions in an interrupted pack", "generator",
             '        unknown = actual - set(ownership["paths"])\n        if unknown:\n',
             '        unknown = actual - set(ownership["paths"])\n        if unknown and ownership.get("state") != "building":\n',
             "PACK_GENERATOR_INTERRUPTED_CUSTOM_PRESERVED"),
            ("bd-codex-parity: claim a caller file added while builders run", "generator",
             "        unknown = generated_paths(out) - owned\n        if unknown:\n",
             "        unknown = generated_paths(out) - owned\n        if False:\n",
             "PACK_GENERATOR_LATE_CLAIM_PRESERVED"),
            ("bd-codex-parity: claim unknown files at generation completion", "generator",
             "        unknown = actual - staged_paths\n        if unknown:\n",
             "        unknown = actual - staged_paths\n        if False:\n", "PACK_GENERATOR_LATE_OUTPUT_PRESERVED"),
            ("bd-codex-parity: adopt a caller's planned scenario filename during staging", "generator",
             "    write_output_manifest(out, owned, in_progress=True)\n",
             '    write_output_manifest(out, owned | {s["id"] + ".json" for s in SCENARIOS}, in_progress=True)\n',
             "PACK_GENERATOR_PLANNED_CALLER_PRESERVED"),
        ]
    if only in ("all", "offline-runner"):
        cases += [
            ("bd-codex-parity: ignore comparer nonzero exit", "runner",
             "if ($parityExit -ne 0)", "if ($false)", "OFFLINE_RUNNER_NONZERO"),
            ("bd-codex-parity: ignore comparer missing marker", "runner",
             "if (@($parityOutput | Where-Object { [string]$_ -ceq 'SELF-TEST PASS' }).Count -ne 1)",
             "if ($false)", "OFFLINE_RUNNER_MARKER"),
            ("bd-codex-parity: ignore generator nonzero exit", "runner",
             "if ($generatorExit -ne 0)", "if ($false)", "OFFLINE_GENERATOR_NONZERO"),
            ("bd-codex-parity: ignore generator missing marker", "runner",
             "if (@($generatorOutput | Where-Object { [string]$_ -ceq 'MAKE-PACKS SELF-TEST PASS' }).Count -ne 1)",
             "if ($false)", "OFFLINE_GENERATOR_MARKER"),
            ("bd-codex-parity: ignore missing Pillow", "runner",
             "if ($pillowExit -ne 0)", "if ($false)", "OFFLINE_GENERATOR_PILLOW"),
        ]
    # ---- burndown/w9-tests-scripts-2 ----
    if only in ("all", "offline-runner"):
        cases += [
            # N-bd-w5-contracts-1-01: -ParityOnly ran the mutation-tooling checks too, reading a $pwsh it never set.
            ("bd-w9-tests-scripts-2: run the mutation-tooling checks under -ParityOnly", "runner",
             "# fails on either half.\nif (-not $ParityOnly) {\n", "# fails on either half.\nif ($true) {\n",
             "OFFLINE_RUNNER_WITNESS"),
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
