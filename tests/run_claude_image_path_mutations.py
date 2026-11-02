"""Focused, rebuilt witnesses for Claude's kernel image-path binding.

The default prints a plan and changes nothing. In an exclusive build window:
  python tests/run_claude_image_path_mutations.py --apply --report <scratch/report.json>
Repeat --case to select witnesses. Builds use --no-restore; restore cached dependencies
beforehand. Only the explicit adapter sources below are changed, never credentials,
native agent pipes, user sessions, or other worktrees. Backups and command logs are
kept beside the report. A build failure is not a killed witness.

  python tests/run_claude_image_path_mutations.py --self-test --report <scratch/selftest.json>
The self-test uses only synthetic files and process outcomes, with no build.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import uuid


ROOT = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class Family:
    project: str
    test_dll: Path
    artifact: Path
    test_args: tuple[str, ...]
    failure_file: str
    failure_prefix: str
    healthy_label: str
    assertions: int


CLAUDE_OUTPUT = Path("tests/AgentBroker.Claude.Tests/bin/Release/net10.0-windows")
WORKER_OUTPUT = Path("tests/AgentBroker.Worker.Tests/bin/Release/net10.0-windows")
# These copies are what the tests load. A body-only product mutation need not
# change the test assembly when reference assemblies keep the same public surface.
FAMILIES = {
    "claude": Family("tests/AgentBroker.Claude.Tests/AgentBroker.Claude.Tests.csproj",
                     CLAUDE_OUTPUT / "AgentBroker.Claude.Tests.dll",
                     CLAUDE_OUTPUT / "AgentBroker.ClaudeAdapter.dll",
                     ("--kernel-image-binding-only",), "KernelImageBindingChecks.cs",
                     "FAIL: System.InvalidOperationException: ",
                     "KernelImageBindingChecks.cs: WITNESS", 32),
    "worker": Family("tests/AgentBroker.Worker.Tests/AgentBroker.Worker.Tests.csproj",
                     WORKER_OUTPUT / "AgentBroker.Worker.Tests.dll",
                     WORKER_OUTPUT / "AgentBroker.Worker.dll",
                     ("--claude-update-tolerant-case", "image-path"), "ClaudeUpdateTolerantChecks.cs",
                     "System.InvalidOperationException: ",
                     "ClaudeUpdateTolerantChecks.cs: WITNESS: the path-drift cache case first establishes one real provider pin-cache entry", 6),
    "worker-kernel": Family("tests/AgentBroker.Worker.Tests/AgentBroker.Worker.Tests.csproj",
                            WORKER_OUTPUT / "AgentBroker.Worker.Tests.dll",
                            WORKER_OUTPUT / "AgentBroker.ClaudeAdapter.dll",
                            ("--claude-update-tolerant-case", "kernel-process"), "ClaudeUpdateTolerantChecks.cs",
                            "System.InvalidOperationException: ",
                            "ClaudeUpdateTolerantChecks.cs: WITNESS: actual public-process wrapper discovers an unelevated pinned session through the retained kernel handle (tolerant=False)", 5),
}


@dataclass(frozen=True)
class Witness:
    name: str
    source: str
    before: str
    after: str
    failure_label: str
    family: str = "claude"


WITNESSES = (
    Witness("kernel-query-seam", "tools/AgentBroker.ClaudeAdapter/KernelProcessImage.cs",
            "            if (!query(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());",
            "            _ = query; // Keep the field read; only the query seam is bypassed.\n"
            "            if (!WindowsKernelProcessOperations.Instance.QueryImagePath(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());",
            "WITNESS: each ImagePath read invokes the supplied kernel query on the same process handle with flags zero"),
    Witness("post-lease-binding", "tools/AgentBroker.ClaudeAdapter/NativeImageBinding.cs",
            "            RequireSamePath(process.ImagePath, image.ImagePath); // Post-lease kernel query.\n",
            "",
            "post-lease kernel path disagreement refuses by name before credentials or authentication"),
    Witness("reinspection-binding", "tools/AgentBroker.ClaudeAdapter/NativeImageBinding.cs",
            "        RequireSamePath(process.ImagePath, Identity.ImagePath); // Reinspection kernel query.\n"
            "        image!.VerifyPath(Identity.ImagePath);\n"
            "        RequireSamePath(process.ImagePath, image.ImagePath); // Recheck after held-path validation too.\n",
            "        image!.VerifyPath(Identity.ImagePath);\n",
            "kernel path change before held-path verification at reinspection refuses by name before the auth frame"),
    Witness("worker-discovery-post-path", "tools/AgentBroker.Worker/Adapters/ClaudeNativeProvider.cs",
            "            if (process.StartFileTime != started ||\n"
            "                !string.Equals(process.ImagePath, image, StringComparison.Ordinal))\n"
            "                return Task.FromResult<ClaudeNativeCandidate?>(null);\n",
            "",
            "kernel path drift after a cold pinned image hash refuses discovery before advertising a session",
            family="worker"),
    Witness("initial-lease-binding", "tools/AgentBroker.ClaudeAdapter/NativeImageBinding.cs",
            "            RequireSamePath(before, image.ImagePath);\n",
            "",
            "an image lease for a different initial kernel path refuses before credentials"),
    Witness("ordinal-path-binding", "tools/AgentBroker.ClaudeAdapter/NativeImageBinding.cs",
            "        if (!string.Equals(KernelProcessImage.NormalizePath(current), KernelProcessImage.NormalizePath(expected), StringComparison.Ordinal))",
            "        if (!string.Equals(KernelProcessImage.NormalizePath(current), KernelProcessImage.NormalizePath(expected), StringComparison.OrdinalIgnoreCase))",
            "case-variant post-lease kernel path refuses by ordinal identity before credentials"),
    Witness("connection-reinspection-binding", "tools/AgentBroker.ClaudeAdapter/WindowsNativePeerConnection.cs",
            "            var identity = binding.Reinspect();",
            "            var identity = binding.Identity;",
            "kernel path change before held-path verification at reinspection refuses by name before the auth frame"),
    Witness("kernel-elevation-admission", "tools/AgentBroker.ClaudeAdapter/KernelProcessImage.cs",
            "            if (operations.IsElevated(process)) throw new Win32Exception(5);\n",
            "",
            "limited-query open success with an elevated token is counted and skipped by actual discovery before image trust (tolerant=False)",
            family="worker-kernel"),
    Witness("worker-pinned-ordinal-path", "tools/AgentBroker.Worker/Adapters/ClaudeNativeProvider.cs",
            "            if (process.StartFileTime != started ||\n"
            "                !string.Equals(process.ImagePath, image, StringComparison.Ordinal))\n"
            "                return Task.FromResult<ClaudeNativeCandidate?>(null);\n",
            "            if (process.StartFileTime != started ||\n"
            "                !string.Equals(process.ImagePath, image, StringComparison.OrdinalIgnoreCase))\n"
            "                return Task.FromResult<ClaudeNativeCandidate?>(null);\n",
            "case-only kernel path drift after a pinned image hash refuses discovery by ordinal identity",
            family="worker"),
    Witness("worker-unpinned-ordinal-path", "tools/AgentBroker.Worker/Adapters/ClaudeNativeProvider.cs",
            "        if (process.HasExited || !process.IsCurrentUser || process.StartFileTime != started ||\n"
            "            !string.Equals(process.ImagePath, image, StringComparison.Ordinal)) return null;\n",
            "        if (process.HasExited || !process.IsCurrentUser || process.StartFileTime != started ||\n"
            "            !string.Equals(process.ImagePath, image, StringComparison.OrdinalIgnoreCase)) return null;\n",
            "case-only kernel path drift after publisher observation refuses discovery by ordinal identity",
            family="worker"),
)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def refuse_links(path: Path) -> None:
    for component in (path, *path.parents):
        try:
            attributes = component.lstat()
        except FileNotFoundError:
            continue
        if component.is_symlink() or getattr(attributes, "st_file_attributes", 0) & 0x400:
            raise RuntimeError("Linked source/evidence path refused: " + str(component))


def safe_components(path: Path) -> None:
    for component in path.parts:
        name = component.lower()
        if (name.endswith((".env", ".pem", ".key", ".p12", ".pfx", ".keystore")) or
                name.startswith((".env.", "privatekey")) or name in (".npmrc", ".aws", ".ssh", ".git")):
            raise ValueError("Protected source/evidence path refused.")


def source_path(root: Path, relative: str) -> Path:
    path = root / relative
    safe_components(Path(relative))
    if Path(relative).is_absolute() or not path.resolve().is_relative_to(root.resolve()):
        raise ValueError("Source leaves its explicit root.")
    refuse_links(path)
    return path


def replace_once(data: bytes, witness: Witness) -> bytes:
    if not witness.before or not witness.failure_label:
        raise ValueError("Source anchors are not configured: " + witness.name)
    newline = "\r\n" if b"\r\n" in data else "\n"
    before = witness.before.replace("\n", newline).encode("utf-8")
    after = witness.after.replace("\n", newline).encode("utf-8")
    if data.count(before) != 1 or before == after:
        raise RuntimeError("Expected exactly one distinct mutation anchor: " + witness.name)
    return data.replace(before, after, 1)


class Sources:
    def __init__(self, root: Path, relatives: list[str]):
        self.root = root
        self.original = {relative: source_path(root, relative).read_bytes() for relative in relatives}
        self.owned = dict(self.original)

    def require_original(self) -> None:
        for relative, original in self.original.items():
            if source_path(self.root, relative).read_bytes() != original:
                raise RuntimeError("Source changed outside this run: " + relative)

    def mutate(self, witness: Witness) -> str:
        self.require_original()
        changed = replace_once(self.original[witness.source], witness)
        self.owned[witness.source] = changed
        source_path(self.root, witness.source).write_bytes(changed)
        return sha256(changed)

    def restore(self) -> list[str]:
        errors = []
        for relative, original in self.original.items():
            try:
                path = source_path(self.root, relative)
                current = path.read_bytes()
                if current not in (original, self.owned[relative]):
                    errors.append(relative + ": foreign edit preserved")
                    continue
                if current != original:
                    path.write_bytes(original)
                self.owned[relative] = original
            except (OSError, RuntimeError, ValueError) as error:
                errors.append(relative + ": " + type(error).__name__ + ": " + str(error))
        return errors

    def hashes(self) -> dict:
        return {relative: sha256(source_path(self.root, relative).read_bytes()) for relative in self.original}


def artifact_state(path: Path) -> dict:
    refuse_links(path)
    if not path.is_file():
        return {"mtimeNs": 0, "sha256": None}
    return {"mtimeNs": path.stat().st_mtime_ns, "sha256": sha256(path.read_bytes())}


def require_rebuilt(before: dict, after: dict) -> None:
    if after["mtimeNs"] <= before["mtimeNs"] or after["sha256"] is None:
        raise RuntimeError("Loaded adapter artifact was not proven rebuilt.")


def require_mutant_artifact(healthy: dict, mutated: dict) -> None:
    if (healthy["sha256"] is None or mutated["sha256"] is None or
            mutated["sha256"] == healthy["sha256"]):
        raise RuntimeError("Mutant product artifact did not differ from its healthy family baseline.")


def grade_healthy(result: dict, family: str = "claude") -> int:
    expected = FAMILIES[family]
    text = result["output"]
    summaries = re.findall(r"(?m)^RESULT=PASS assertions=([1-9][0-9]*)\r?$", text)
    if (result["exitCode"] != 0 or len(summaries) != 1 or int(summaries[0]) != expected.assertions or
            re.search(r"(?m)^(?:FAIL:|RESULT=FAIL|SKIP:|SKIPPED:)", text) or
            not any(line.startswith("PASS: ") and expected.healthy_label in line for line in text.splitlines())):
        raise RuntimeError("Healthy checks lacked a passing summary or their image-path witness.")
    return int(summaries[0])


def grade_negative(result: dict, witness: Witness) -> None:
    text = result["output"]
    expected = FAMILIES[witness.family]
    failures = re.findall(r"(?m)^" + re.escape(expected.failure_prefix) + r"([^\r\n]*)", text)
    if (result["exitCode"] != 1 or len(re.findall(r"(?m)^RESULT=FAIL\r?$", text)) != 1 or
            len(failures) != 1 or expected.failure_file not in failures[0] or witness.failure_label not in failures[0] or
            re.search(r"(?m)^RESULT=PASS", text)):
        raise RuntimeError("Mutation did not fail at its exact image-path assertion: " + witness.name)


def stop_owned(process: subprocess.Popen) -> None:
    if process.poll() is None:
        if os.name == "nt":
            stopped = subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                     stderr=subprocess.DEVNULL, timeout=20, check=False)
            if stopped.returncode != 0 and process.poll() is None:
                raise RuntimeError("Could not stop the owned timed-out process tree.")
        else:
            os.killpg(process.pid, signal.SIGKILL)
    process.wait(timeout=20)


class Runner:
    def __init__(self, evidence: Path, report: dict):
        self.evidence = evidence
        self.report = report

    def invoke(self, label: str, argv: list[str], timeout: int) -> dict:
        record = {"label": label, "argv": argv, "timeoutSeconds": timeout}
        self.report["commands"].append(record)
        out_path = self.evidence / (label + ".out.txt")
        err_path = self.evidence / (label + ".err.txt")
        with out_path.open("xb") as out, err_path.open("xb") as err:
            process = subprocess.Popen(argv, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=out, stderr=err,
                                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                                       start_new_session=os.name != "nt")
            try:
                record["exitCode"] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                record["timedOut"] = True
                stop_owned(process)
                raise RuntimeError("Owned build/check timed out: " + label)
        if out_path.stat().st_size + err_path.stat().st_size > 2 << 20:
            raise RuntimeError("Command evidence exceeded its output bound: " + label)
        return {"exitCode": record["exitCode"],
                "output": out_path.read_text(encoding="utf-8", errors="replace") + "\n" +
                          err_path.read_text(encoding="utf-8", errors="replace")}

    def build(self, family: str, label: str, healthy_artifact: dict | None = None) -> dict:
        expected = FAMILIES[family]
        before = artifact_state(ROOT / expected.artifact)
        result = self.invoke(label, ["dotnet", "build", expected.project, "-c", "Release", "--no-restore",
                                    "--no-incremental", "--disable-build-servers", "-warnaserror",
                                    "-p:UseSharedCompilation=false"], 300)
        if result["exitCode"] != 0:
            raise RuntimeError("Build failure is not a killed witness: " + label)
        after = artifact_state(ROOT / expected.artifact)
        require_rebuilt(before, after)
        record = {"loadedProductArtifact": expected.artifact.as_posix(), "before": before, "after": after, "rebuilt": True}
        if healthy_artifact is not None:
            require_mutant_artifact(healthy_artifact, after)
            record.update({"healthyBaselineSha256": healthy_artifact["sha256"], "differsFromHealthy": True})
        return record

    def test(self, family: str, label: str) -> dict:
        expected = FAMILIES[family]
        return self.invoke(label, ["dotnet", str(expected.test_dll), *expected.test_args], 120)


def write_json_new(path: Path, value: dict) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def self_test(folder: Path) -> dict:
    checks = []
    for name, callback in (
        ("stale artifact refused", lambda: require_rebuilt({"mtimeNs": 2, "sha256": "A"}, {"mtimeNs": 2, "sha256": "B"})),
        ("missing artifact refused", lambda: require_rebuilt({"mtimeNs": 0, "sha256": None}, {"mtimeNs": 0, "sha256": None})),
        ("newer artifact with healthy hash refused", lambda: require_mutant_artifact(
            {"mtimeNs": 1, "sha256": "A"}, {"mtimeNs": 2, "sha256": "A"})),
        ("missing healthy artifact refused", lambda: require_mutant_artifact(
            {"mtimeNs": 1, "sha256": None}, {"mtimeNs": 2, "sha256": "B"})),
    ):
        try:
            callback()
        except RuntimeError:
            checks.append(name)
        else:
            raise AssertionError(name)
    require_rebuilt({"mtimeNs": 1, "sha256": "A"}, {"mtimeNs": 2, "sha256": "B"})
    checks.append("advanced artifact accepted")
    require_mutant_artifact({"mtimeNs": 1, "sha256": "A"}, {"mtimeNs": 2, "sha256": "B"})
    checks.append("mutant artifact differs from healthy family baseline")
    sample = Witness("synthetic", "first.cs", "original", "mutated", "EXACT_LABEL")
    failure_file = FAMILIES["claude"].failure_file
    failure_prefix = FAMILIES["claude"].failure_prefix
    healthy_label = FAMILIES["claude"].healthy_label
    valid_negative = {"exitCode": 1, "output": failure_prefix + failure_file + ": EXACT_LABEL\nRESULT=FAIL\n"}
    grade_negative(valid_negative, sample)
    checks.append("specific negative result accepted")
    for name, result in (
        ("exit zero refused", {**valid_negative, "exitCode": 0}),
        ("wrong assertion refused", {"exitCode": 1, "output": failure_prefix + failure_file + ": OTHER_LABEL\nRESULT=FAIL\n"}),
        ("pass line is not failure proof", {"exitCode": 1, "output": "PASS: " + failure_file + ": EXACT_LABEL\nFAIL: another test\nRESULT=FAIL\n"}),
        ("missing result refused", {"exitCode": 1, "output": failure_prefix + failure_file + ": EXACT_LABEL\n"}),
    ):
        try:
            grade_negative(result, sample)
        except RuntimeError:
            checks.append(name)
        else:
            raise AssertionError(name)
    healthy_count = FAMILIES["claude"].assertions
    grade_healthy({"exitCode": 0, "output": "PASS: " + healthy_label + f"\nRESULT=PASS assertions={healthy_count}\n"})
    checks.append("healthy witness accepted")
    try:
        grade_healthy({"exitCode": 0, "output": f"RESULT=PASS assertions={healthy_count}\n"})
    except RuntimeError:
        checks.append("missing healthy witness refused")
    else:
        raise AssertionError("missing healthy witness refused")
    worker = Witness("synthetic-worker", "first.cs", "original", "mutated", "EXACT_LABEL", "worker")
    grade_negative({"exitCode": 1, "output": "System.InvalidOperationException: ClaudeUpdateTolerantChecks.cs: EXACT_LABEL\nRESULT=FAIL\n"}, worker)
    grade_healthy({"exitCode": 0, "output": "PASS: " + FAMILIES["worker"].healthy_label + "\nRESULT=PASS assertions=6\n"}, "worker")
    checks.append("worker family grades its own witness and inventory")
    try:
        grade_negative(valid_negative, worker)
    except RuntimeError:
        checks.append("wrong family test file refused")
    else:
        raise AssertionError("wrong family test file refused")
    worker_kernel = Witness("synthetic-worker-kernel", "first.cs", "original", "mutated", "EXACT_LABEL", "worker-kernel")
    grade_negative({"exitCode": 1, "output": "System.InvalidOperationException: ClaudeUpdateTolerantChecks.cs: EXACT_LABEL\nRESULT=FAIL\n"}, worker_kernel)
    grade_healthy({"exitCode": 0, "output": "PASS: " + FAMILIES["worker-kernel"].healthy_label + "\nRESULT=PASS assertions=5\n"}, "worker-kernel")
    checks.append("kernel discovery family grades its own witness and inventory")
    first, later = folder / "first.cs", folder / "later.cs"
    first.write_bytes(b"original")
    later.write_bytes(b"original")
    sources = Sources(folder, ["first.cs", "later.cs"])
    sources.mutate(sample)
    assert not sources.restore() and first.read_bytes() == b"original"
    checks.append("owned bytes restored exactly")
    sources.owned["later.cs"] = b"mutated later"
    later.write_bytes(sources.owned["later.cs"])
    first.write_bytes(b"foreign edit")
    errors = sources.restore()
    assert len(errors) == 1 and "first.cs: foreign edit preserved" in errors
    assert first.read_bytes() == b"foreign edit" and later.read_bytes() == b"original"
    checks.append("foreign edit preserved while other owned bytes restored")
    first.unlink()
    later.write_bytes(b"mutated later")
    sources.owned["later.cs"] = b"mutated later"
    errors = sources.restore()
    assert len(errors) == 1 and "first.cs" in errors[0] and not first.exists()
    assert later.read_bytes() == b"original"
    checks.append("missing source preserved while other owned bytes restored")
    try:
        replace_once(b"original original", sample)
    except RuntimeError:
        checks.append("ambiguous mutation anchor refused")
    else:
        raise AssertionError("ambiguous mutation anchor refused")
    return {"result": "PASS", "checks": checks, "buildsStarted": False, "repositorySourcesChanged": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--case", action="append", choices=[item.name for item in WITNESSES])
    parser.add_argument("--report", type=Path, help="A new JSON report under a chosen scratch directory.")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.apply and args.self_test:
        parser.error("Choose --apply or --self-test, not both.")
    selected = [item for item in WITNESSES if not args.case or item.name in args.case]
    report_path = args.report.absolute() if args.report else None
    if report_path is not None:
        safe_components(report_path)
        refuse_links(report_path)
        if report_path.suffix.lower() != ".json" or report_path.exists():
            parser.error("--report must name a new .json file.")
        if report_path.resolve().is_relative_to(ROOT.resolve()):
            parser.error("--report must be outside the repository, under your scratch directory.")
        if args.apply or args.self_test:
            report_path.parent.mkdir(parents=True, exist_ok=True)
    if args.self_test:
        with tempfile.TemporaryDirectory(prefix="claude-imagepath-selftest-", dir=report_path.parent if report_path else None) as temporary:
            result = self_test(Path(temporary))
        if report_path:
            write_json_new(report_path, result)
        print(json.dumps(result, indent=2))
        return 0
    if not args.apply:
        print(json.dumps({"mode": "plan_only", "buildsStarted": False, "sourcesChanged": False,
                          "witnesses": [{"name": item.name, "family": item.family, "source": item.source, "failureLabel": item.failure_label}
                                        for item in selected]}, indent=2))
        return 0
    if report_path is None:
        parser.error("--apply requires --report under a scratch directory.")
    sources = Sources(ROOT, sorted({item.source for item in selected}))
    families = tuple(name for name in FAMILIES if any(item.family == name for item in selected))
    for witness in selected:
        replace_once(sources.original[witness.source], witness)
    evidence = report_path.parent / (report_path.stem + "-evidence-" + uuid.uuid4().hex[:8])
    evidence.mkdir()
    originals = {relative: sha256(data) for relative, data in sources.original.items()}
    backups = []
    for index, (relative, data) in enumerate(sources.original.items(), start=1):
        backup = "source-%02d.original.bytes" % index
        with (evidence / backup).open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        backups.append({"source": relative, "backup": backup, "sha256": originals[relative]})
    write_json_new(evidence / "source-backups.json", {"sources": backups})
    report = {"startedUtc": utc_now(), "result": "INCOMPLETE", "evidenceDirectory": str(evidence),
              "sourceSha256Before": originals, "selectedCases": [item.name for item in selected], "families": families,
              "commands": [], "witnesses": [], "errors": []}
    runner = Runner(evidence, report)
    interrupted = False
    try:
        report["healthyBefore"] = {}
        for family in families:
            report["healthyBefore"][family] = {
                "build": runner.build(family, "healthy-before-build-" + family),
                "assertions": grade_healthy(runner.test(family, "healthy-before-tests-" + family), family),
            }
        for witness in selected:
            record = {"name": witness.name, "family": witness.family, "source": witness.source, "failureLabel": witness.failure_label,
                      "sourceSha256Before": originals[witness.source]}
            report["witnesses"].append(record)
            try:
                record["mutatedSourceSha256"] = sources.mutate(witness)
                record["artifact"] = runner.build(witness.family, witness.name + "-build",
                                                  report["healthyBefore"][witness.family]["build"]["after"])
                grade_negative(runner.test(witness.family, witness.name + "-negative"), witness)
                record["verdict"] = "FIRED"
            finally:
                errors = sources.restore()
                if errors:
                    raise RuntimeError("; ".join(errors))
                record["sourceSha256After"] = sources.hashes()[witness.source]
    except (Exception, KeyboardInterrupt) as error:
        interrupted = isinstance(error, KeyboardInterrupt)
        report["errors"].append(type(error).__name__ + ": " + str(error))
    finally:
        report["errors"].extend(sources.restore())
        try:
            report["sourceSha256After"] = sources.hashes()
            report["sourceBytesRestored"] = report["sourceSha256After"] == originals
            if not report["sourceBytesRestored"]:
                raise RuntimeError("Final source SHA-256 verification failed.")
            if not interrupted and any(record.get("mutatedSourceSha256") for record in report["witnesses"]):
                report["healthyAfter"] = {}
                for family in families:
                    report["healthyAfter"][family] = {
                        "build": runner.build(family, "healthy-after-build-" + family),
                        "assertions": grade_healthy(runner.test(family, "healthy-after-tests-" + family), family),
                    }
        except (Exception, KeyboardInterrupt) as error:
            report["errors"].append(type(error).__name__ + ": " + str(error))
        report["finishedUtc"] = utc_now()
        report["result"] = "PASS" if not report["errors"] and report.get("sourceBytesRestored") and \
            len(report["witnesses"]) == len(selected) and all(record.get("verdict") == "FIRED" for record in report["witnesses"]) else "FAIL"
        write_json_new(report_path, report)
    print(json.dumps({"result": report["result"], "report": str(report_path),
                      "witnesses": len(report["witnesses"]), "sourceBytesRestored": report.get("sourceBytesRestored", False)}, indent=2))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
