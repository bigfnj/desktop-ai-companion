"""Focused mutation witnesses for publisher-verified Codex build notes.

Default: dry plan. --self-test uses scratch synthetic files, without builds.
For an exclusive build window after offline restore:
  python tests/run_codex_tolerant_note_mutations.py --apply --report <scratch/new.json>
Repeat --case to select. --restore-config may be supplied explicitly or inherited
from RestoreConfigFile. Builds never restore packages and disable NuGet auditing.
The run has a 45-minute total command budget, individual build/check timeouts,
exact source anchors, owned-byte restoration, and healthy final verification.
No live agent transport, credentials, or model calls are used.
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
import time
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
    summary_pattern: str | None = None


WORKER_OUTPUT = Path("tests/AgentBroker.Worker.Tests/bin/Release/net10.0-windows")
DESKTOP_OUTPUT = Path("tests/AgentBroker.Desktop.Tests/bin/Release/net10.0-windows")
MODULE_OUTPUT = Path("build/DesktopAICompanionPortable/bin/Release/x64")
HOST = MODULE_OUTPUT / "DesktopAICompanion.exe"
FAMILIES = {
    "worker": Family("tests/AgentBroker.Worker.Tests/AgentBroker.Worker.Tests.csproj",
                     WORKER_OUTPUT / "AgentBroker.Worker.Tests.dll", WORKER_OUTPUT / "AgentBroker.Worker.dll",
                     ("--codex-tolerant-note-only",), "CodexTolerantNoteChecks.cs",
                     "System.InvalidOperationException: ", "CodexTolerantNoteChecks.cs: WITNESS:", 57),
    "desktop-pin": Family("tests/AgentBroker.Desktop.Tests/AgentBroker.Desktop.Tests.csproj",
                          DESKTOP_OUTPUT / "AgentBroker.Desktop.Tests.dll", DESKTOP_OUTPUT / "AgentBroker.CodexDesktopAdapter.dll",
                          ("--pin-diagnostics-only",), "CodexPinMetadataChecks.cs", "System.InvalidOperationException: ",
                          "AgentBroker desktop pin diagnostic checks passed:", 23,
                          r"(?m)^AgentBroker desktop pin diagnostic checks passed: ([1-9][0-9]*)\r?$"),
    "desktop-observer": Family("tests/AgentBroker.Desktop.Tests/AgentBroker.Desktop.Tests.csproj",
                               DESKTOP_OUTPUT / "AgentBroker.Desktop.Tests.dll", DESKTOP_OUTPUT / "AgentBroker.CodexDesktopAdapter.dll",
                               ("--directory-observer-reporting-only",), "DirectoryObserverChecks.cs", "System.InvalidOperationException: ",
                               "AgentBroker directory observer reporting checks passed:", 13,
                               r"(?m)^AgentBroker directory observer reporting checks passed: ([1-9][0-9]*)\r?$"),
    "module": Family("modules/AgentBroker/AgentBroker.csproj", HOST,
                     MODULE_OUTPUT / "modules/agentbroker/AgentBroker.dll",
                     ("--module-selftest=agentbroker",), "AgentBrokerOnboardingChecks.cs", "FAIL: ",
                     "PASS: the module's own self-test passed", 0),
}


@dataclass(frozen=True)
class Witness:
    name: str
    source: str
    before: str
    after: str
    failure_label: str
    family: str = "worker"
    case: str | None = None


PROVIDER = "tools/AgentBroker.Worker/Adapters/CodexNativeProvider.cs"
WITNESSES = (
    Witness("profile-error-reporting", PROVIDER,
            "                diagnostics.Add(profilePinDiagnostic);\n", "",
            "effective update-tolerant mode off preserves the exact profile error (adapter-off)", case="no-carry"),
    Witness("router-error-reporting", PROVIDER,
            "            diagnostics.Add(routerPinDiagnostic);\n", "",
            "a different observer router keeps its exact error despite a carried owner (samePid=False)", case="observer"),
    Witness("carrier-independent-notes", PROVIDER,
            "        bool reportTolerant = updateTolerant && updateTolerantDelivery;",
            "        bool reportTolerant = updateTolerant && updateTolerantDelivery && carriedBindings.Count > 0;",
            "this pass's publisher-verified profile is a note even without an admitted session (capability)", case="no-carry"),
    Witness("adapter-mode-off", PROVIDER,
            "        bool reportTolerant = updateTolerant && updateTolerantDelivery;",
            "        bool reportTolerant = updateTolerant;",
            "effective update-tolerant mode off preserves the exact profile error (adapter-off)", case="no-carry"),
    Witness("note-flag", PROVIDER,
            '                ". Update-tolerant delivery is enabled.") { Note = true });',
            '                ". Update-tolerant delivery is enabled.") { Note = false });',
            "profile carry replaces its exact-pin failure with one build-naming note (unsupported_vscode_profile)", case="carried"),
    Witness("engine-pin-suppression", PROVIDER,
            "                diagnostics.RemoveAll(item => ReferenceEquals(item, profilePinDiagnostic));\n", "",
            "profile carry replaces its exact-pin failure with one build-naming note (unsupported_vscode_profile)", case="carried"),
    Witness("signature-error-retained", PROVIDER,
            "            diagnostics?.Add(CodexNativeAdapter.DescribeDiscoveryFailure(error));\n", "            _ = error;\n",
            "failed publisher evidence retains both real errors and never becomes a carried note (public_image_signature_untrusted)", case="refused"),
    Witness("profile-publisher-proof", PROVIDER,
            "            if (profileTrust == DesktopRouterTrust.PublisherSigned && profilePinDiagnostic is not null && profilePinRefusal is not null)",
            "            if (profilePinDiagnostic is not null && profilePinRefusal is not null)",
            "failed publisher evidence retains both real errors and never becomes a carried note (public_image_signature_untrusted)", case="refused"),
    Witness("router-report-proof-present", PROVIDER,
            "            routerPublisherVerifiedThisPass = directory.PublisherVerifiedForReporting;",
            "            routerPublisherVerifiedThisPass = false;",
            "WITNESS: an idle observer with explicit publisher reporting proof replaces its own exact error with a versioned note", case="router-reporting"),
    # Retired captured-assignment=true mutant: the live-proof guard now independently blocks a false proof.
    # These two mutations instead remove the live checks after a genuine successful captured proof.
    Witness("router-live-report-proof", PROVIDER,
            "            if (routerPublisherVerifiedThisPass && directory?.PublisherVerifiedForReporting == true &&",
            "            if (routerPublisherVerifiedThisPass && directory is not null &&",
            "an observer that loses live reporting proof after observation keeps its exact error at completion", case="router-reporting"),
    Witness("router-live-report-identity", PROVIDER,
            "                directory.RouterIdentity == observedRouter && observedRouter.Pid != 0 && observedRouter.CreationFileTime != 0 &&",
            "                observedRouter.Pid != 0 && observedRouter.CreationFileTime != 0 &&",
            "an observer whose live identity changes after observation cannot reuse the captured publisher verdict", case="router-reporting"),
    Witness("owner-report-proof-required", PROVIDER,
            "                adapter.PublisherVerifiedForReporting && adapter.ExactPinRefusal is { } pin)",
            "                adapter.ExactPinRefusal is { } pin)",
            "a successful owner lookup without explicit reporting proof cannot promote a matching router refusal", case="router-reporting"),
    Witness("observer-pid-match", PROVIDER,
            "                if (identity == observedRouter && routerPinDiagnostic is not null && routerPinRefusal is not null)",
            "                if (identity.Item2 == observedRouter.CreationFileTime && routerPinDiagnostic is not null && routerPinRefusal is not null)",
            "a different observer router keeps its exact error despite a carried owner (samePid=False)", case="observer"),
    Witness("observer-start-match", PROVIDER,
            "                if (identity == observedRouter && routerPinDiagnostic is not null && routerPinRefusal is not null)",
            "                if (identity.Item1 == observedRouter.Pid && routerPinDiagnostic is not null && routerPinRefusal is not null)",
            "a different observer router keeps its exact error despite a carried owner (samePid=True)", case="observer"),
    Witness("adapter-completion", "tools/AgentBroker.Worker/Adapters/CodexNativeAdapter.cs",
            "            provider.CompleteDiscovery(carried, updateTolerant);\n", "",
            "the real adapter completes discovery with its freshly resolved guarded update-tolerant binding", case="completion"),
    Witness("component-collision", PROVIDER,
            "        passDiagnostics = Array.AsReadOnly(diagnostics.ToArray());",
            "        passDiagnostics = Array.AsReadOnly(diagnostics.Distinct().ToArray());",
            "equal-valued profile and unrelated observer refusals keep the observer error when only the profile is carried", case="observer"),
    Witness("notes-own-line", "modules/AgentBroker/AgentBrokerModule.Clients.cs",
            "            foreach (string note in joined.Split(ControlProtocol.NoteSeparator, StringSplitOptions.RemoveEmptyEntries).Take(8))",
            "            foreach (string note in (client == \"codex\" && !wrapTrayNotes ? new[] { joined } : joined.Split(ControlProtocol.NoteSeparator, StringSplitOptions.RemoveEmptyEntries)).Take(8))",
            "CODEX_TOLERANT_NOTE_LINE: a combined Codex build note and a second note are whole on separate lines beside the healthy native line", family="module"),
    Witness("notes-whole", "modules/AgentBroker/AgentBrokerModule.Clients.cs",
            "                else yield return \"\\u26A0 \" + description;",
            "                else yield return \"\\u26A0 \" + Safe(description, 116);",
            "CODEX_TOLERANT_NOTE_LINE: a combined Codex build note and a second note are whole on separate lines beside the healthy native line", family="module"),
    Witness("tray-notes-whole", "modules/AgentBroker/AgentBrokerModule.Clients.cs",
            "ClientStateLines(current, wrapTrayNotes: true)", "ClientStateLines(current, wrapTrayNotes: false)",
            "CODEX_TOLERANT_TRAY_NOTE_WHOLE: actual Codex tray children preserve both build names and the complete signature sentence in bounded continuation items", family="module"),
    Witness("completion-binding-retained", "tools/AgentBroker.Worker/Adapters/CodexNativeAdapter.cs",
            "            bindings.Clear(); foreach (var pair in found) bindings.Add(pair.Key, pair.Value);\n"
            "            slowLookups.Clear(); slowLookups.UnionWith(slow);\n"
            "            skippedLastPass = skipped.Count;\n"
            "            // Reporting cannot prevent a successfully resolved binding from being saved. Empty passes still report\n"
            "            // publisher verification; a note never grants delivery authority.\n"
            "            provider.CompleteDiscovery(carried, updateTolerant);",
            "            provider.CompleteDiscovery(carried, updateTolerant);\n"
            "            bindings.Clear(); foreach (var pair in found) bindings.Add(pair.Key, pair.Value);\n"
            "            slowLookups.Clear(); slowLookups.UnionWith(slow);\n"
            "            skippedLastPass = skipped.Count;",
            "report completion failure does not discard an admitted binding before a synthetic submission can use it", case="completion-failure"),
    Witness("image-mismatch-note-meaning", PROVIDER,
            '? " (image differs from reviewed build)" : "";', '? "" : "";',
            "a publisher-verified extension with a reviewed version still says its image differs from the reviewed build", case="image-mismatch"),
    Witness("installed-manifest-mismatch-version", "tools/AgentBroker.CodexDesktopAdapter/VsCodeCompatibilityProfile.cs",
            "        { throw new DesktopAdapterException(error.Code, installedVersion); }",
            "        { throw new DesktopAdapterException(error.Code); }",
            "F4_IMAGE_MISMATCH_MANIFEST_LAYOUT_VERSION: a manifest mismatch names its checked layout, not the pin version", family="desktop-pin"),
    Witness("installed-bundle-mismatch-version", "tools/AgentBroker.CodexDesktopAdapter/VsCodeCompatibilityProfile.cs",
            '                throw new DesktopAdapterException("vscode_image_mismatch", installedVersion);',
            '                throw new DesktopAdapterException("vscode_image_mismatch");',
            "F4_IMAGE_MISMATCH_BUNDLE_LAYOUT_VERSION: a bundle mismatch names its checked layout, not the pin version", family="desktop-pin"),
    Witness("installed-version-not-pin", "tools/AgentBroker.CodexDesktopAdapter/VsCodeCompatibilityProfile.cs",
            "        string? installedVersion = InstalledExtensionVersion(root);", "        string? installedVersion = profile.Version;",
            "F4_IMAGE_MISMATCH_MANIFEST_LAYOUT_VERSION: a manifest mismatch names its checked layout, not the pin version", family="desktop-pin"),
    Witness("running-mismatch-version", "tools/AgentBroker.CodexDesktopAdapter/VsCodeCompatibilityProfile.cs",
            '                throw new DesktopAdapterException("running_vscode_image_mismatch", PublisherEngineVersion(engine.Image, root));',
            '                throw new DesktopAdapterException("running_vscode_image_mismatch");',
            "F4_IMAGE_MISMATCH_RUNNING_LAYOUT_VERSION: a running image mismatch names its actual engine layout, not the pin version", family="desktop-pin"),
    Witness("desktop-mismatch-version", "tools/AgentBroker.CodexDesktopAdapter/WindowsDesktopConnection.cs",
            '            throw new DesktopAdapterException("desktop_image_mismatch", profile.ApplicationVersion);',
            '            throw new DesktopAdapterException("desktop_image_mismatch");',
            "F4_IMAGE_MISMATCH_DESKTOP_VERSION: a changed image reports the installed package-path version", family="desktop-pin"),
    Witness("snapshot-report-proof-reset", "tools/AgentBroker.CodexDesktopAdapter/DesktopDirectoryObserver.cs",
            "    public IReadOnlyList<DesktopDirectorySession> Snapshot()\n    {\n        Volatile.Write(ref reportPublisherVerified, false);",
            "    public IReadOnlyList<DesktopDirectorySession> Snapshot()\n    {",
            "OBSERVER_REPORT_RESET_ON_FAILURE: an identity failure clears a prior successful snapshot proof", family="desktop-observer"),
    Witness("snapshot-report-exact-not-signed", "tools/AgentBroker.CodexDesktopAdapter/DesktopDirectoryObserver.cs",
            "            Volatile.Write(ref reportPublisherVerified, connection.Trust == DesktopRouterTrust.PublisherSigned);",
            "            Volatile.Write(ref reportPublisherVerified, true);",
            "OBSERVER_REPORT_EXACT_GATE: exact trust alone is not publisher-verification reporting proof", family="desktop-observer"),
    Witness("snapshot-report-signed", "tools/AgentBroker.CodexDesktopAdapter/DesktopDirectoryObserver.cs",
            "            Volatile.Write(ref reportPublisherVerified, connection.Trust == DesktopRouterTrust.PublisherSigned);",
            "            Volatile.Write(ref reportPublisherVerified, false);",
            "OBSERVER_REPORT_SIGNED_GATE: a same-incarnation signed snapshot reports its connect-time publisher verdict", family="desktop-observer"),
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
        raise RuntimeError("Loaded product artifact was not proven rebuilt.")


def require_mutant_artifact(healthy: dict, mutated: dict) -> None:
    if (healthy["sha256"] is None or mutated["sha256"] is None or
            mutated["sha256"] == healthy["sha256"]):
        raise RuntimeError("Mutant product artifact did not differ from its healthy family baseline.")


def grade_healthy(result: dict, family: str = "worker") -> int:
    expected = FAMILIES[family]
    text = result["output"]
    if family == "module":
        if (result["exitCode"] != 0 or len(re.findall(r"(?m)^RESULT=PASS\r?$", text)) != 1 or
                re.search(r"(?m)^[ \t]*(?:\[agentbroker\][ \t]*)?(?:FAIL:|EXC:|SKIP:)", text) or
                expected.healthy_label not in text):
            raise RuntimeError("Module checks lacked their host-load witness and clean verdict.")
        return 0
    if expected.summary_pattern is not None:
        summaries = re.findall(expected.summary_pattern, text)
        if (result["exitCode"] != 0 or len(summaries) != 1 or int(summaries[0]) != expected.assertions or
                re.search(r"(?m)^(?:FAIL:|RESULT=FAIL|EXC:|SKIP:|SKIPPED:)", text) or
                expected.healthy_label not in text):
            raise RuntimeError("Desktop focused checks lacked their exact nonempty passing inventory.")
        return int(summaries[0])
    summaries = re.findall(r"(?m)^RESULT=PASS assertions=([1-9][0-9]*)\r?$", text)
    if (result["exitCode"] != 0 or len(summaries) != 1 or int(summaries[0]) != expected.assertions or
            re.search(r"(?m)^(?:FAIL:|RESULT=FAIL|SKIP:|SKIPPED:)", text) or
            not any(line.startswith("PASS: ") and expected.healthy_label in line for line in text.splitlines())):
        raise RuntimeError("Healthy checks lacked a passing summary or their Codex note witness.")
    return int(summaries[0])


def require_module_failure_inventory(failures: list[str], witness: Witness) -> None:
    # ModuleConventionSelfTest.RunModuleSelfTest adds this one aggregate failure when the module returns false.
    # Each renderer mutation must otherwise fail only its exact Codex assertion, once.
    expected = [witness.failure_label, "the module's own self-test passed"]
    if sorted(failures) != sorted(expected):
        raise RuntimeError("Module mutation had an unexpected failure inventory: " + witness.name)


def grade_negative(result: dict, witness: Witness) -> None:
    text = result["output"]
    expected = FAMILIES[witness.family]
    if witness.family == "module":
        failures = re.findall(r"(?m)^[ \t]*(?:\[agentbroker\][ \t]*)?FAIL: ([^\r\n]*)", text)
        require_module_failure_inventory(failures, witness)
        valid = True
    else:
        failures = re.findall(r"(?m)^" + re.escape(expected.failure_prefix) + r"([^\r\n]*)", text)
        valid = len(failures) == 1 and expected.failure_file in failures[0] and witness.failure_label in failures[0]
        exception_headers = re.findall(r"(?m)^[ \t]*(?:---> )?[A-Za-z_][A-Za-z0-9_.+`]*Exception:[^\r\n]*", text)
        expected_header = expected.failure_prefix + failures[0] if len(failures) == 1 else None
        if (exception_headers != [expected_header] or
                re.search(r"(?m)^[ \t]*(?:\[[^\]\r\n]+\][ \t]*)?(?:FAIL:|EXC:|SKIP:|SKIPPED:)", text) or
                expected.summary_pattern is not None and re.search(expected.summary_pattern, text)):
            valid = False
    if (result["exitCode"] != 1 or len(re.findall(r"(?m)^RESULT=FAIL\r?$", text)) != 1 or
            not valid or re.search(r"(?m)^RESULT=PASS", text) or
            re.search(r"(?m)^[ \t]*(?:\[agentbroker\][ \t]*)?(?:EXC:|SKIP:)", text)):
        raise RuntimeError("Mutation did not fail at its exact Codex note assertion: " + witness.name)


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


def wait_owned(process: subprocess.Popen, timeout: int, label: str, record: dict) -> None:
    try:
        record["exitCode"] = process.wait(timeout=timeout)
    except BaseException as error:
        record["waitError"] = type(error).__name__
        if isinstance(error, subprocess.TimeoutExpired):
            record["timedOut"] = True
        # Reap before propagating an interruption: the caller may restore sources as soon as this returns.
        stop_owned(process)
        record["ownedProcessReaped"] = True
        if isinstance(error, subprocess.TimeoutExpired):
            raise RuntimeError("Owned build/check timed out: " + label) from error
        raise


class Runner:
    def __init__(self, evidence: Path, report: dict, restore_config: Path):
        self.evidence = evidence
        self.report = report
        self.restore_config = restore_config
        self.started = time.monotonic()
        self.owned_process: subprocess.Popen | None = None

    def restore_sources(self, sources: Sources) -> list[str]:
        if self.owned_process is not None:
            try:
                if self.owned_process.poll() is None:
                    return ["Owned command is still running; source restoration refused until it stops."]
                self.owned_process.wait(timeout=20)
                self.owned_process = None
            except Exception as error:
                return ["Cannot prove the owned command was reaped; source restoration refused: " + type(error).__name__]
        return sources.restore()

    def invoke(self, label: str, argv: list[str], timeout: int) -> dict:
        remaining = int(45 * 60 - (time.monotonic() - self.started))
        if remaining <= 0:
            raise RuntimeError("Total mutation command budget expired.")
        timeout = min(timeout, remaining)
        temp = self.evidence / (label + "-temp")
        temp.mkdir()
        env = dict(os.environ, TEMP=str(temp), TMP=str(temp), DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT="1")
        record = {"label": label, "argv": argv, "timeoutSeconds": timeout, "temporaryDirectory": str(temp)}
        self.report["commands"].append(record)
        out_path = self.evidence / (label + ".out.txt")
        err_path = self.evidence / (label + ".err.txt")
        with out_path.open("xb") as out, err_path.open("xb") as err:
            process = subprocess.Popen(argv, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=out, stderr=err, env=env,
                                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                                       start_new_session=os.name != "nt")
            self.owned_process = process
            try:
                wait_owned(process, timeout, label, record)
            finally:
                if process.poll() is not None:
                    self.owned_process = None
        if out_path.stat().st_size + err_path.stat().st_size > 2 << 20:
            raise RuntimeError("Command evidence exceeded its output bound: " + label)
        return {"exitCode": record["exitCode"],
                "output": out_path.read_text(encoding="utf-8", errors="replace") + "\n" +
                          err_path.read_text(encoding="utf-8", errors="replace")}

    def build(self, family: str, label: str, healthy_artifact: dict | None = None) -> dict:
        expected = FAMILIES[family]
        before = artifact_state(ROOT / expected.artifact)
        arguments = ["dotnet", "build", expected.project, "-c", "Release", "--no-restore",
                     "--no-incremental", "--disable-build-servers", "-warnaserror", "-p:UseSharedCompilation=false",
                     "-p:NuGetAudit=false", "-p:RestoreConfigFile=" + str(self.restore_config)]
        if family == "module":
            arguments.append("-p:SkipAgentBrokerWorkerPublish=true")
        result = self.invoke(label, arguments, 300)
        if result["exitCode"] != 0:
            raise RuntimeError("Build failure is not a killed witness: " + label)
        after = artifact_state(ROOT / expected.artifact)
        require_rebuilt(before, after)
        record = {"loadedProductArtifact": expected.artifact.as_posix(), "before": before, "after": after, "rebuilt": True}
        if healthy_artifact is not None:
            require_mutant_artifact(healthy_artifact, after)
            record.update({"healthyBaselineSha256": healthy_artifact["sha256"], "differsFromHealthy": True})
        return record

    def test(self, family: str, label: str, case: str | None = None) -> dict:
        expected = FAMILIES[family]
        if family == "module":
            # The host copies this entire bundle before the test. Refuse matching paths before that indirect read.
            bundle = ROOT / expected.artifact.parent
            for directory, dirs, files in os.walk(bundle, followlinks=False):
                for name in dirs + files:
                    target = Path(directory) / name
                    safe_components(target)
                    refuse_links(target)
            result = self.invoke(label, [str(ROOT / expected.test_dll), *expected.test_args], 120)
            temp = Path(self.report["commands"][-1]["temporaryDirectory"])
            marker = temp / "dp-module-agentbroker-selftest.txt"
            safe_components(marker)
            refuse_links(marker)
            if not marker.is_file() or marker.stat().st_size > 2 << 20:
                raise RuntimeError("Module host wrote no bounded private verdict marker.")
            result["output"] = marker.read_text(encoding="utf-8-sig")
            return result
        args = ("--codex-tolerant-note-case", case) if case else expected.test_args
        return self.invoke(label, ["dotnet", str(expected.test_dll), *args], 120)


def write_json_new(path: Path, value: dict) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def self_test(folder: Path) -> dict:
    checks = []
    def rejects(name, callback):
        try: callback()
        except (RuntimeError, AssertionError): checks.append(name)
        else: raise AssertionError(name)
    rejects("stale artifact refused", lambda: require_rebuilt({"mtimeNs":2,"sha256":"A"},{"mtimeNs":2,"sha256":"B"}))
    rejects("newer artifact with healthy hash refused", lambda: require_mutant_artifact({"sha256":"A"},{"sha256":"A"}))
    require_rebuilt({"mtimeNs":1,"sha256":"A"},{"mtimeNs":2,"sha256":"B"})
    require_mutant_artifact({"sha256":"A"},{"sha256":"B"})
    checks.append("advanced changed artifact accepted")
    sample = Witness("synthetic", "first.cs", "original", "mutated", "EXACT_LABEL")
    negative = {"exitCode":1,"output":"System.InvalidOperationException: CodexTolerantNoteChecks.cs: EXACT_LABEL\nRESULT=FAIL\n"}
    grade_negative(negative,sample)
    checks.append("specific worker failure accepted")
    grade_negative({**negative, "output": negative["output"].replace("RESULT=FAIL",
        "   at Synthetic.Check() in synthetic.cs:line 1\nRESULT=FAIL")}, sample)
    checks.append("normal negative stack trace accepted")
    rejects("exit zero refused", lambda: grade_negative({**negative,"exitCode":0},sample))
    rejects("wrong failure label refused", lambda: grade_negative({**negative,"output":negative["output"].replace("EXACT_LABEL","OTHER")},sample))
    rejects("missing verdict refused", lambda: grade_negative({**negative,"output":negative["output"].replace("RESULT=FAIL","")},sample))
    rejects("extra failure header refused", lambda: grade_negative({**negative,
        "output": negative["output"] + "FAIL: UNEXPECTED\n"}, sample))
    rejects("extra exception header refused", lambda: grade_negative({**negative,
        "output": negative["output"] + "System.IO.IOException: UNEXPECTED\n"}, sample))
    grade_healthy({"exitCode":0,"output":"PASS: CodexTolerantNoteChecks.cs: WITNESS: synthetic\nRESULT=PASS assertions=57\n"})
    rejects("healthy without witness refused", lambda: grade_healthy({"exitCode":0,"output":"RESULT=PASS assertions=57\n"}))
    for family in ("desktop-pin", "desktop-observer"):
        expected = FAMILIES[family]
        healthy = {"exitCode": 0, "output": expected.healthy_label + " " + str(expected.assertions) + "\n"}
        grade_healthy(healthy, family)
        rejects(family + " wrong inventory refused", lambda: grade_healthy({**healthy,
            "output": expected.healthy_label + " " + str(expected.assertions + 1) + "\n"}, family))
        witness = Witness("synthetic-" + family, "first.cs", "original", "mutated", "DESKTOP_LABEL", family)
        negative_desktop = {"exitCode": 1, "output": "System.InvalidOperationException: " + expected.failure_file + ": DESKTOP_LABEL\nRESULT=FAIL\n"}
        grade_negative(negative_desktop, witness)
        rejects(family + " missing failure verdict refused", lambda: grade_negative({**negative_desktop,
            "output": negative_desktop["output"].replace("RESULT=FAIL", "")}, witness))
        checks.append(family + " grades its focused summary and named negative")
    module = Witness("synthetic-module","first.cs","original","mutated","MODULE_LABEL","module")
    module_negative = {"exitCode":1,"output":"  [agentbroker] FAIL: MODULE_LABEL\nFAIL: the module's own self-test passed\nRESULT=FAIL\n"}
    grade_negative(module_negative,module)
    grade_healthy({"exitCode":0,"output":"PASS: the module's own self-test passed\nRESULT=PASS\n"},"module")
    checks.append("module grades exact prefixed assertion and host-load witness")
    rejects("unexpected module assertion refused", lambda: grade_negative({**module_negative,
        "output":module_negative["output"].replace("RESULT=FAIL","  [agentbroker] FAIL: UNEXPECTED_ASSERTION\nRESULT=FAIL")},module))
    rejects("missing module aggregate refused", lambda: grade_negative({**module_negative,
        "output":module_negative["output"].replace("FAIL: the module's own self-test passed\n","")},module))
    rejects("duplicate module target refused", lambda: grade_negative({**module_negative,
        "output":"  [agentbroker] FAIL: MODULE_LABEL\n"+module_negative["output"]},module))
    rejects("module exception is not a killed assertion", lambda: grade_negative({"exitCode":1,"output":"EXC: MODULE_LABEL\nRESULT=FAIL\n"},module))
    events = []
    class InterruptedProcess:
        stopped = False
        def wait(self, timeout):
            if not self.stopped:
                events.append("interrupted wait")
                raise KeyboardInterrupt()
            events.append("reaped")
            return 130
        def poll(self):
            return 130 if self.stopped else None
    process = InterruptedProcess()
    def stop_synthetic(owned):
        assert owned is process
        events.append("stop owned")
        owned.stopped = True
        owned.wait(timeout=20)
    saved_popen, saved_stop = subprocess.Popen, globals()["stop_owned"]
    try:
        subprocess.Popen = lambda *args, **kwargs: process
        globals()["stop_owned"] = stop_synthetic
        evidence = folder / "interrupted-invoke"
        evidence.mkdir()
        report = {"commands": []}
        runner = Runner(evidence, report, folder / "offline.config")
        try:
            runner.invoke("interrupted-wait", ["synthetic-process"], 1)
        except KeyboardInterrupt:
            assert events == ["interrupted wait", "stop owned", "reaped"], "interrupted owned wait stops and reaps before propagation"
            assert report["commands"][0]["ownedProcessReaped"], "interrupted owned wait stops and reaps before propagation"
            checks.append("interrupted owned wait stops and reaps before propagation")
        else:
            raise AssertionError("interrupted owned wait stops and reaps before propagation")
    finally:
        subprocess.Popen, globals()["stop_owned"] = saved_popen, saved_stop
    first,later=folder/"first.cs",folder/"later.cs"
    first.write_bytes(b"original"); later.write_bytes(b"original")
    sources=Sources(folder,["first.cs","later.cs"])
    sources.mutate(sample)
    live = InterruptedProcess()
    runner.owned_process = live
    assert runner.restore_sources(sources) and first.read_bytes() == b"mutated", "source restoration waits for an owned live command to stop"
    live.stopped = True
    assert not runner.restore_sources(sources) and first.read_bytes() == b"original", "source restoration waits for an owned live command to stop"
    checks.append("source restoration waits for an owned live command to stop")
    assert not sources.restore() and first.read_bytes()==b"original"
    checks.append("owned bytes restored exactly")
    sources.owned["later.cs"]=b"mutated later"; later.write_bytes(b"mutated later"); first.write_bytes(b"foreign edit")
    assert len(sources.restore())==1 and first.read_bytes()==b"foreign edit" and later.read_bytes()==b"original"
    checks.append("foreign edit preserved while other owned bytes restored")
    first.unlink(); sources.owned["later.cs"]=b"mutated later"; later.write_bytes(b"mutated later")
    assert len(sources.restore())==1 and not first.exists() and later.read_bytes()==b"original"
    checks.append("missing source preserved while other owned bytes restored")
    rejects("ambiguous mutation anchor refused",lambda:replace_once(b"original original",sample))
    return {"result":"PASS","checks":checks,"buildsStarted":False,"repositorySourcesChanged":False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--case", action="append", choices=[item.name for item in WITNESSES])
    parser.add_argument("--report", type=Path, help="A new JSON report under a chosen scratch directory.")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--restore-config", type=Path, default=os.environ.get("RestoreConfigFile"),
                        help="Offline NuGet config; defaults to inherited RestoreConfigFile.")
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
        with tempfile.TemporaryDirectory(prefix="codex-note-selftest-", dir=report_path.parent if report_path else None) as temporary:
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
    if args.restore_config is None:
        parser.error("--apply requires --restore-config or inherited RestoreConfigFile.")
    args.restore_config = Path(args.restore_config).absolute()
    safe_components(args.restore_config)
    refuse_links(args.restore_config)
    if not args.restore_config.is_file():
        parser.error("Offline restore config is missing.")
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
    runner = Runner(evidence, report, args.restore_config)
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
                grade_negative(runner.test(witness.family, witness.name + "-negative", witness.case), witness)
                record["verdict"] = "FIRED"
            finally:
                errors = runner.restore_sources(sources)
                if errors:
                    raise RuntimeError("; ".join(errors))
                record["sourceSha256After"] = sources.hashes()[witness.source]
    except (Exception, KeyboardInterrupt) as error:
        interrupted = isinstance(error, KeyboardInterrupt)
        report["errors"].append(type(error).__name__ + ": " + str(error))
    finally:
        report["errors"].extend(runner.restore_sources(sources))
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
