"""Negative witnesses for the AgentBroker hygiene lane (AgentBroker next campaign, 2026-11-08).

List the witnesses without changing files or starting processes:
  python -X utf8 tests/run_agentbroker_hygiene_mutations.py

Run them, in a worktree nobody else is building, and keep the evidence:
  python -X utf8 tests/run_agentbroker_hygiene_mutations.py --apply --output artifacts/hygiene-mutations-<label>

Each witness changes one exact anchor (its occurrence count is asserted), rebuilds the owning test project with
--no-incremental, proves the mutated component DLL changed and was rebuilt after the mutation, runs its focused group,
and requires exit 1 with exactly its named check as the one failure. The original bytes are restored in finally and
verified by SHA-256; a final healthy rebuild must pass every group again. A missing or incorrect failure is unqualified and the run fails.
Known equivalent mutant, not a witness: the byte check in ValidateMessage (BrokerStore.cs) alone is subsumed by the one in
ValidMessageShape, which ValidateMessage calls first, so only the both-sites and load-only mutations can be killed.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = {
    "core": ("tests/AgentBroker.Core.Tests/AgentBroker.Core.Tests.csproj", "tests/AgentBroker.Core.Tests/bin/Release/net10.0",
             "AgentBroker.Core.Tests.dll"),
    "worker": ("tests/AgentBroker.Worker.Tests/AgentBroker.Worker.Tests.csproj",
               "tests/AgentBroker.Worker.Tests/bin/Release/net10.0-windows", "AgentBroker.Worker.Tests.dll"),
}
STORE = "src/AgentBroker.Core/BrokerStore.cs"
TOOLS = "tools/AgentBroker.Worker/BrokerTools.cs"
GUIDANCE = "tools/AgentBroker.Worker/BrokerRecoveryGuidance.cs"
CARD = "tools/AgentBroker.Worker/BrokerInstructions.cs"


@dataclass(frozen=True)
class Witness:
    name: str
    source: str
    project: str
    args: tuple[str, ...]
    replacements: tuple[tuple[str, str, int], ...]
    expected: str

    @property
    def component(self) -> str:
        return "AgentBroker.Worker.Tests.dll" if self.source.startswith("tests/AgentBroker.Worker.Tests/") else "AgentBroker.Core.dll" if self.source.startswith("src/AgentBroker.Core/") else "AgentBroker.Worker.dll"


X1_GUARD = """            if (outcome == MessageStatus.Held && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)
                Fail("message_expired");
"""
X2_BRANCH = """            : m.Recipient.BrokerId != state.BrokerId
                ? $"was refused by the recipient's paired broker {m.Recipient.BrokerId} before delivery ({ended.Reason ?? "refused"})."
"""
TIMINGS = "src/AgentBroker.Core/BrokerStore.CommitTimings.cs"
WITNESSES = (
    Witness("hygiene-late-held-allowed", STORE, "core", ("--hygiene-only",), ((X1_GUARD, "", 1),),
            "LATE_HELD_REFUSED: a local Held after the deadline is refused message_expired and the record stays Uncertain"),
    Witness("hygiene-late-held-widened", STORE, "core", ("--hygiene-only",),
            (("if (outcome == MessageStatus.Held && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)",
              "if (authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)", 1),),
            "LATE_HELD_THEN_DELIVERED: after the refused Held, the recipient's own Delivered still settles the record"),
    Witness("hygiene-late-held-paired", STORE, "core", ("--hygiene-only",),
            (("if (outcome == MessageStatus.Held && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)",
              "if (outcome == MessageStatus.Held && previous.Message.ExpiresAt <= Now)", 1),),
            "LATE_HELD_PAIRED_UNCHANGED: a paired broker's late Held is not refused by the local-only rule"),
    Witness("hygiene-held-exact-deadline", STORE, "core", ("--hygiene-only",),
            (("if (outcome == MessageStatus.Held && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)",
              "if (outcome == MessageStatus.Held && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt < Now)", 1),),
            "LATE_HELD_EXACT_DEADLINE: a local Held exactly at ExpiresAt is refused and remains Uncertain"),
    Witness("hygiene-late-refused-preserved", STORE, "core", ("--hygiene-only",),
            (("if (outcome == MessageStatus.Held && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)",
              "if ((outcome is MessageStatus.Held or MessageStatus.Refused) && authenticatedBroker == state.BrokerId && previous.Message.ExpiresAt <= Now)", 1),),
            "LATE_REFUSED_ACCEPTED: a late local Refused still settles an Uncertain record"),
    Witness("hygiene-peer-refusal-blames-local", STORE, "core", ("--peer-receipt-only",), ((X2_BRANCH, "", 1),),
            "PEER_REFUSAL_ATTRIBUTED: a paired broker's refusal reason is attributed to that broker, not this one"),
    Witness("hygiene-peer-refusal-unconditional", STORE, "core", ("--peer-receipt-only",),
            (("            : m.Recipient.BrokerId != state.BrokerId\n", "            : m.Recipient.BrokerId != Guid.Empty\n", 1),),
            "LOCAL_REFUSAL_ATTRIBUTED: a local refusal still reads as refused by this broker"),
    Witness("hygiene-text-limit-chars-both", STORE, "core", ("--hygiene-only",),
            (("Encoding.UTF8.GetByteCount(m.Text) > limits.MaximumTextBytes", "m.Text.Length > limits.MaximumTextBytes", 2),),
            "TEXT_LIMIT_UTF8_OVER: a 16,386-byte text of the same character count is refused invalid_message"),
    Witness("hygiene-text-limit-chars-load", STORE, "core", ("--hygiene-only",),
            (("Encoding.UTF8.GetByteCount(m.Text) > limits.MaximumTextBytes || m.ExpiresAt <= m.CreatedAt",
              "m.Text.Length > limits.MaximumTextBytes || m.ExpiresAt <= m.CreatedAt", 1),),
            "TEXT_LIMIT_LOAD_OVER: durable state with a 16,386-byte text of the same character count is refused on open"),
    Witness("hygiene-card-wait-reverted", CARD, "worker", ("--instructions-only",),
            (("one message or reply wait per inbox, plus one wait_for_receipt. ", "one wait per inbox. ", 1),),
            "CARD_WAIT_CLASSES: the card says message and reply waits share one slot and a receipt wait runs beside it"),
    Witness("hygiene-card-notice-reverted", CARD, "worker", ("--instructions-only",),
            (("agentbroker:notices reports Expired/Refused and historical Uncertain events. Check the current receipt; acknowledge, never auto-resend.",
              "If a message you sent expires or is refused undelivered, one notice from agentbroker:notices reaches your inbox; acknowledge it.", 1),),
            "CARD_NOTICE_UNCERTAIN: the card names Uncertain notices and says to acknowledge without resending"),
    Witness("hygiene-guidance-wait-reverted", GUIDANCE, "worker", ("--recovery-guidance-only",),
            ((" wait_for_messages and wait_for_reply share one wait per session; wait_for_receipt has its own and may run beside them. Do not start a second wait of the same kind.",
              " Do not start another blocking wait for the same session.", 1),),
            "RECOVERY_GUIDANCE_WAIT_CLASSES: wait refusals explain the shared message/reply wait and the separate receipt wait"),
    Witness("hygiene-guidance-hint-missing", GUIDANCE, "worker", ("--recovery-guidance-only",),
            (('"native_session_unverified" => new(code, "list_sessions",', '"native_session_unverified_mutant" => new(code, "list_sessions",', 1),),
            "RECOVERY_GUIDANCE_HINT_CODES: every declaration and title refusal names list_sessions without promising that waiting helps"),
    Witness("hygiene-notice-reply-unguarded", TOOLS, "worker", ("--first-contact-only",),
            (('        if (parent.Sender.SessionId == BrokerStore.NoticeSessionId) throw ToolFailure("notice_reply_refused");\n', "", 1),),
            "NOTICE_REPLY_REFUSED: replying to a delivery notice is refused as notice_reply_refused, not reply_budget_exhausted"),
    Witness("hygiene-receipt-uncertain-unanswered", TOOLS, "worker", ("--receipt-wait-only",),
            (("status is not (MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted);",
              "status is not (MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted or MessageStatus.Uncertain);", 1),),
            "RECEIPT_WAIT_UNCERTAIN_AT_ONCE: an Uncertain receipt is an answer and ends the wait at once"),
    Witness("hygiene-receipt-held-unanswered", TOOLS, "worker", ("--receipt-wait-only",),
            (("status is not (MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted);",
              "status is not (MessageStatus.Accepted or MessageStatus.Submitting or MessageStatus.Submitted or MessageStatus.Held);", 1),),
            "RECEIPT_WAIT_HELD_AT_ONCE: a Held receipt is an answer and ends the wait at once"),
    Witness("hygiene-submitted-never-ages", STORE, "worker", ("--receipt-wait-only",),
            ((": r.Status == MessageStatus.Submitted && r.Message.ExpiresAt <= Now",
              ": r.Status == MessageStatus.Submitted && r.Message.ExpiresAt <= Now.AddYears(-1000)", 1),),
            "RECEIPT_WAIT_SUBMITTED_AGES_UNCERTAIN: a Submitted message's deadline ends the wait with Uncertain, receipt_unconfirmed"),
) + (
    Witness("timing-observation-disabled", TIMINGS, "core", ("--commit-timing-only",),
            (("        Volatile.Write(ref commitStartTimestamp, Stopwatch.GetTimestamp());",
              "        _ = Stopwatch.GetTimestamp();", 1),),
            "COMMIT_TIMING_COMPLETED: an ordinary commit is counted once and leaves nothing in flight"),
    Witness("timing-observer-takes-gate", TIMINGS, "core", ("--commit-timing-only",),
            (("        long start = Volatile.Read(ref commitStartTimestamp);",
              "        lock (gate) { }\n        long start = Volatile.Read(ref commitStartTimestamp);", 1),),
            "COMMIT_TIMING_OBSERVER_UNBLOCKED: reading commit timing never waits on the gate the diagnosed commit holds"),
    Witness("timing-in-flight-hidden", TIMINGS, "core", ("--commit-timing-only",),
            (("inFlight ? CommitStages[stage] : null", "(string?)null", 1),),
            "COMMIT_TIMING_IN_FLIGHT: a held commit is reported in flight with its stage, start and elapsed time"),
    Witness("timing-start-not-reported", TIMINGS, "core", ("--commit-timing-only",),
            (("inFlight ? new DateTimeOffset(startUtc, TimeSpan.Zero) : null", "(DateTimeOffset?)null", 1),),
            "COMMIT_TIMING_IN_FLIGHT: a held commit is reported in flight with its stage, start and elapsed time"),
    Witness("timing-failed-counted-completed", TIMINGS, "core", ("--commit-timing-only",),
            (("        if (succeeded) Interlocked.Increment(ref commitsCompleted);", "        Interlocked.Increment(ref commitsCompleted);", 1),
             ("        else Interlocked.Increment(ref commitsFailed);", "        if (!succeeded) Interlocked.Increment(ref commitsFailed);", 1)),
            "COMMIT_TIMING_FAILED: a failed commit counts as failed, not completed, clears its in-flight entry and surfaces its original failure"),
    Witness("timing-end-skipped-on-failure", TIMINGS, "core", ("--commit-timing-only",),
            (("        finally { EndCommitTiming(succeeded); }", "        finally { if (succeeded) EndCommitTiming(succeeded); }", 1),),
            "COMMIT_TIMING_FAILED: a failed commit counts as failed, not completed, clears its in-flight entry and surfaces its original failure"),
    Witness("timing-slow-not-counted", TIMINGS, "core", ("--commit-timing-only",),
            (("        if (ticks >= SlowCommit.Ticks)", "        if (ticks >= SlowCommit.Ticks * 100)", 1),),
            "COMMIT_TIMING_SLOW_BASELINE: an already-slow ordinary commit is counted completed and slow, so no check needs a quiet disk"),
    Witness("timing-stage-unmarked", STORE, "core", ("--commit-timing-only",),
            (("            BeginCommitStage(SnapshotReplaceStage);\n", "", 1),),
            "COMMIT_TIMING_WITNESS_STAGE_REACHED: the controlled commit reaches its snapshot-replace stage marker"),
) + (
    Witness("n4-stop-link-removed", "tools/AgentBroker.Worker/BrokerRuntime.cs", "worker", ("--directory-stop-core-only",),
            (("CancellationTokenSource.CreateLinkedTokenSource(token, WaitStopping);", "CancellationTokenSource.CreateLinkedTokenSource(token);", 1),),
            "DIRECTORY_STOP_RETURNS_PROMPTLY: stopping ends the peer budget at once and the listing returns its local rows"),
    Witness("n4-stop-link-removed-host", "tools/AgentBroker.Worker/BrokerRuntime.cs", "worker", ("--directory-host-stop-only",),
            (("CancellationTokenSource.CreateLinkedTokenSource(token, WaitStopping);", "CancellationTokenSource.CreateLinkedTokenSource(token);", 1),),
            "DIRECTORY_HOST_STOP_BEFORE_DRAIN: the actual lifetime exits within three seconds and the blocked list_sessions returns its local rows"),
) + (
    Witness('reply-http-store-clock-omitted', 'tools/AgentBroker.Worker/WorkerApplication.cs', 'worker', ('--reply-only',),
            (('new BrokerLimits(DefaultMessageLifetimeMinutes: options.DefaultMessageLifetimeMinutes), clock);', 'new BrokerLimits(DefaultMessageLifetimeMinutes: options.DefaultMessageLifetimeMinutes), TimeProvider.System);', 1),), 'REPLY_HTTP_STORE_CLOCK: the store admits leases from the same controlled clock as its runtime'),
    Witness('reply-http-runtime-clock-omitted', 'tools/AgentBroker.Worker/WorkerApplication.cs', 'worker', ('--reply-only',),
            (('new BrokerRuntime(store, federation, nativeAdapters, new SessionPreferences(options.DataDirectory), clock);', 'new BrokerRuntime(store, federation, nativeAdapters, new SessionPreferences(options.DataDirectory), TimeProvider.System);', 1),), 'REPLY_HTTP_RUNTIME_CLOCK: the HTTP fixture runtime uses the controlled clock before any registration'),
    Witness('reply-http-explicit-late-boundary-omitted', 'tests/AgentBroker.Worker.Tests/ReplyToolChecks.cs', 'worker', ('--reply-only',),
            (('        clock.Advance(TimeSpan.FromSeconds(2));', '        // Negative control: the parent deadline has not passed.', 1),), "REPLY_HTTP_AFTER_DEADLINE: the actual HTTP late reply runs only after its delivered parent's original deadline"),
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def replace_exact(raw: bytes, before: str, after: str, count: int) -> bytes:
    newline = "\r\n" if b"\r\n" in raw else "\n"
    old = before.replace("\n", newline).encode("utf-8")
    new = after.replace("\n", newline).encode("utf-8")
    if raw.count(old) != count:
        raise RuntimeError(f"ANCHOR_COUNT: expected {count}, found {raw.count(old)}: {before[:80]!r}")
    return raw.replace(old, new)


# Private proposed tail; original seventeen WITNESSES entries stay intact.
PROOF_FIELDS = {
    "core": ("WITNESS: POSTPUSH_CORE_EXECUTED", {"CORE_PRODUCT_SHA256": "AgentBroker.Core.dll", "CORE_HARNESS_SHA256": "AgentBroker.Core.Tests.dll"}),
    "worker": ("WITNESS: POSTPUSH_WORKER_EXECUTED", {"WORKER_PRODUCT_SHA256": "AgentBroker.Worker.dll", "WORKER_CORE_SHA256": "AgentBroker.Core.dll", "WORKER_HARNESS_SHA256": "AgentBroker.Worker.Tests.dll"}),
}
# Measured by the composed focused smoke; every mutation run checks its own real baseline against these pins.
EXPECTED_COUNTS = {("core", ("--hygiene-only",)): 12, ("core", ("--peer-receipt-only",)): 22,
                   ("core", ("--commit-timing-only",)): 9, ("worker", ("--receipt-wait-only",)): 22,
                   ("worker", ("--instructions-only",)): 12, ("worker", ("--recovery-guidance-only",)): 28,
                   ("worker", ("--first-contact-only",)): 43,
                   ("worker", ("--directory-stop-core-only",)): 3, ("worker", ("--directory-host-stop-only",)): 3,
                   ("worker", ("--directory-stop-only",)): 6, ("worker", ("--reply-only",)): 13}
CORE_SUMMARIES = {("--hygiene-only",): "AgentBroker hygiene smoke", ("--peer-receipt-only",): "AgentBroker peer receipt smoke",
                  ("--commit-timing-only",): "AgentBroker commit timing smoke"}


def group_key(project: str, args: tuple[str, ...]) -> str:
    return project + ":" + " ".join(args)


def selected_groups(selected: list[Witness], extra=()) -> list[tuple[str, tuple[str, ...]]]:
    return sorted({(w.project, w.args) for w in selected} | set(extra))


def verify_execution(project: str, text: str, built: dict) -> None:
    marker, fields = PROOF_FIELDS[project]
    lines = text.splitlines()
    if lines.count(marker) != 1:
        raise RuntimeError("EXECUTION_WITNESS: " + project)
    for field, component in fields.items():
        observed = [line.removeprefix(field + "=") for line in lines if line.startswith(field + "=")]
        if observed != [built[component]["sha256"].upper()]:
            raise RuntimeError("EXECUTION_HASH: " + field)


def verify_healthy(project: str, args: tuple[str, ...], outcome: dict, built: dict, expected_count: int) -> dict:
    verify_execution(project, outcome["stdout"], built)
    if outcome["exitCode"] != 0:
        raise RuntimeError("HEALTHY_GROUP_FAILED: " + group_key(project, args))
    if project == "core":
        if args not in CORE_SUMMARIES:
            raise RuntimeError("UNSUPPORTED_CORE_GROUP: " + repr(args))
        pattern = r"(?m)^" + re.escape(CORE_SUMMARIES[args]) + r": ([1-9][0-9]*) assertions passed\.\r?$"
    else:
        pattern = r"(?m)^RESULT=PASS assertions=([1-9][0-9]*)\r?$"
    counts = re.findall(pattern, outcome["stdout"])
    if counts != [str(expected_count)] or re.search(r"(?m)^RESULT=FAIL\r?$", outcome["stdout"]):
        raise RuntimeError("HEALTHY_INVENTORY: " + group_key(project, args))
    return {"group": group_key(project, args), "expectedCount": expected_count, "observedCount": int(counts[0]), "artifacts": built}


def verify_failure(w: Witness, outcome: dict, built: dict) -> list[str]:
    verify_execution(w.project, outcome["stdout"], built)
    lines = [line.strip() for line in outcome["stderr"].splitlines() if line.strip()]
    failures = re.findall(r"(?m)^System\.InvalidOperationException: ([^\r\n]+)", outcome["stderr"])
    if outcome["exitCode"] != 1 or not lines or lines[0] != "System.InvalidOperationException: " + w.expected or failures != [w.expected]:
        raise RuntimeError("NAMED_MUTATION_FAILURE: " + w.name)
    if w.project == "worker" and len(re.findall(r"(?m)^RESULT=FAIL\r?$", outcome["stdout"])) != 1:
        raise RuntimeError("MUTATION_RESULT: " + w.name)
    return failures


def verify_mutant_rebuilt(w: Witness, before: dict, after: dict, started_ns: int) -> None:
    if after[w.component]["sha256"] == before[w.component]["sha256"] or after[w.component]["mtimeNs"] < started_ns:
        raise RuntimeError("MUTANT_NOT_REBUILT: " + w.name)


def open_exclusive(path: Path):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        import msvcrt
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        handle = kernel.CreateFileW(str(path), 0xC0000000, 0, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            fd = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
        except BaseException:
            kernel.CloseHandle(handle)
            raise
        try:
            return os.fdopen(fd, "r+b", buffering=0)
        except BaseException:
            os.close(fd)
            raise
    import fcntl
    stream = path.open("r+b", buffering=0)
    try:
        fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return stream
    except BaseException:
        stream.close()
        raise


def write_all(stream, raw: bytes) -> None:
    stream.seek(0)
    stream.truncate(len(raw))
    offset = 0
    while offset < len(raw):
        count = stream.write(raw[offset:])
        if count is None or count <= 0:
            raise OSError("Source write did not advance.")
        offset += count
    stream.flush()
    os.fsync(stream.fileno())


def write_owned(path: Path, expected: bytes, replacement: bytes, writer=None) -> None:
    # Windows FileShare.None covers comparison, write, and partial-failure recovery.
    # POSIX uses advisory flock; callers still need sole worktree ownership there.
    with open_exclusive(path) as stream:
        if stream.read() != expected:
            raise RuntimeError("FOREIGN_SOURCE_EDIT: " + str(path))
        try:
            (write_all if writer is None else writer)(stream, replacement)
        except BaseException as write_error:
            try:
                write_all(stream, expected)
            except BaseException as recovery_error:
                raise RuntimeError(f"OWNED_SOURCE_WRITE_AND_RECOVERY_FAILED: {write_error!r}; {recovery_error!r}") from recovery_error
            raise


class Runner:
    def __init__(self, evidence: Path, selected: list[Witness], inventory: dict, extra_groups=()) -> None:
        self.evidence = evidence
        self.serial = 0
        self.groups = selected_groups(selected, extra_groups)
        self.inventory = {group_key(*g): inventory[group_key(*g)] for g in self.groups}
        self.sources = sorted({w.source for w in selected})
        self.original = {s: (ROOT / s).read_bytes() for s in self.sources}
        self.steps: list[dict] = []
        self.active: tuple[str, bytes] | None = None
        self.mutation_attempted = False
        self.cleanup_errors: list[dict] = []
        for source, raw in self.original.items():
            backup = self.evidence / "originals" / source
            backup.parent.mkdir(parents=True, exist_ok=True)
            backup.write_bytes(raw)

    def run(self, label: str, argv: list[str], timeout: int) -> dict:
        self.serial += 1
        prefix = f"{self.serial:03d}-{label}"
        out = self.evidence / (prefix + ".stdout.txt")
        err = self.evidence / (prefix + ".stderr.txt")
        env = dict(os.environ, DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
        started = time.time()
        with out.open("wb") as stdout, err.open("wb") as stderr:
            proc = subprocess.run(argv, cwd=ROOT, stdout=stdout, stderr=stderr, stdin=subprocess.DEVNULL, env=env, timeout=timeout, check=False)
        step = {"label": label, "argv": argv, "exitCode": proc.returncode, "seconds": round(time.time() - started, 1), "stdoutFile": out.name, "stderrFile": err.name}
        self.steps.append(step)
        print(f"EXIT {label}: {proc.returncode}", flush=True)
        return {**step, "stdout": out.read_bytes().decode("utf-8", "replace"), "stderr": err.read_bytes().decode("utf-8", "replace")}

    def artifacts(self, project: str) -> dict:
        result = {}
        for name in PROOF_FIELDS[project][1].values():
            path = ROOT / PROJECTS[project][1] / name
            result[name] = {"name": name, "sha256": sha256(path.read_bytes()), "mtimeNs": path.stat().st_mtime_ns}
        return result

    def build(self, project: str, label: str) -> dict:
        restored = self.run(label + "-restore", ["dotnet", "restore", PROJECTS[project][0], "--disable-build-servers"], 600)
        if restored["exitCode"] != 0:
            raise RuntimeError("RESTORE_FAILED: " + label)
        built = self.run(label, ["dotnet", "build", PROJECTS[project][0], "-c", "Release", "--no-restore", "--no-incremental", "--disable-build-servers", "-warnaserror", "-p:UseSharedCompilation=false"], 600)
        if built["exitCode"] != 0:
            raise RuntimeError("BUILD_FAILED (a compile error never qualifies a witness): " + label)
        return self.artifacts(project)

    def test(self, project: str, label: str, args: tuple[str, ...], built: dict) -> dict:
        self.verify_current_artifacts(project, built)
        dll = str(Path(PROJECTS[project][1]) / PROJECTS[project][2])
        outcome = self.run(label, ["dotnet", dll, *args], 300)
        self.verify_current_artifacts(project, built)
        verify_execution(project, outcome["stdout"], built)
        return outcome

    def verify_current_artifacts(self, project: str, built: dict) -> None:
        current = self.artifacts(project)
        if {k: v["sha256"] for k, v in current.items()} != {k: v["sha256"] for k, v in built.items()}:
            raise RuntimeError("ARTIFACT_CHANGED_OUTSIDE_BUILD: " + project)

    def restore(self) -> None:
        if self.active is not None:
            source, changed = self.active
            original = self.original[source]
            current = (ROOT / source).read_bytes()
            if current == changed:
                write_owned(ROOT / source, changed, original)
            elif current != original:
                raise RuntimeError("FOREIGN_SOURCE_EDIT: " + source)
            self.active = None
        for source, raw in self.original.items():
            if (ROOT / source).read_bytes() != raw:
                raise RuntimeError("RESTORE_MISMATCH_OR_FOREIGN_EDIT: " + source)

    def healthy(self, phase: str) -> dict:
        built = {project: self.build(project, f"{phase}-build-{project}") for project in sorted({g[0] for g in self.groups})}
        proofs = []
        for project, args in self.groups:
            outcome = self.test(project, f"{phase}-{project}{'-'.join(args)}", args, built[project])
            proofs.append(verify_healthy(project, args, outcome, built[project], self.inventory[group_key(project, args)]))
        return {"artifacts": built, "groups": proofs}

    def witness(self, w: Witness, healthy: dict) -> dict:
        raw = self.original[w.source]
        changed = raw
        for before, after, count in w.replacements:
            changed = replace_exact(changed, before, after, count)
        started_ns = time.time_ns()
        record = {"name": w.name, "source": w.source, "expected": w.expected, "sourceBeforeSha256": sha256(raw), "sourceAfterSha256": sha256(changed)}
        primary = None
        try:
            self.mutation_attempted = True
            self.active = (w.source, changed)
            write_owned(ROOT / w.source, raw, changed)
            after = self.build(w.project, w.name + "-build")
            before = healthy["artifacts"][w.project]
            verify_mutant_rebuilt(w, before, after, started_ns)
            record["componentProof"] = {"before": before, "after": after}
            outcome = self.test(w.project, w.name + "-run", w.args, after)
            record["failedChecks"] = verify_failure(w, outcome, after)
            record["exitCode"] = outcome["exitCode"]
            record["verdict"] = "FIRED"
            print(f"FIRED {w.name}: {record['failedChecks']}", flush=True)
            return record
        except BaseException as error:
            primary = error
            raise
        finally:
            try:
                self.restore()
            except BaseException as restore_error:
                self.cleanup_errors.append({"phase": w.name, "error": f"{type(restore_error).__name__}: {restore_error}"})
                if primary is None:
                    raise


def cleanup_owned(runner: Runner, baseline: dict | None, report: dict) -> bool:
    sources_ready = True
    try:
        runner.restore()
    except BaseException as restore_error:
        sources_ready = False
        report["sourceCleanupError"] = f"{type(restore_error).__name__}: {restore_error}"
    report["artifactCleanupAttempted"] = False
    report["artifactsRestored"] = None
    if runner.mutation_attempted:
        report["artifactsRestored"] = False
        if not sources_ready or baseline is None:
            report["artifactCleanupError"] = "Rebuild refused: original sources or baseline artifact snapshot unavailable."
        else:
            report["artifactCleanupAttempted"] = True
            try:
                restored = runner.healthy("restored")
                report["restored"] = restored
                for project, components in baseline["artifacts"].items():
                    if {k: v["sha256"] for k, v in components.items()} != {k: v["sha256"] for k, v in restored["artifacts"][project].items()}:
                        raise RuntimeError("RESTORED_ARTIFACT_MISMATCH: " + project)
                report["artifactsRestored"] = True
            except BaseException as restore_error:
                report["artifactCleanupError"] = f"{type(restore_error).__name__}: {restore_error}"
    report["witnessCleanupErrors"] = runner.cleanup_errors
    return sources_ready and (not runner.mutation_attempted or report["artifactsRestored"] is True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--case", action="append", choices=[w.name for w in WITNESSES])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--healthy-group", action="append", default=[], metavar="PROJECT:--MODE",
                        help="Explicitly add a healthy group; never adds existing Worker regression implicitly.")
    options = parser.parse_args()
    selected = [w for w in WITNESSES if not options.case or w.name in options.case]
    extras = []
    for text in options.healthy_group:
        project, separator, mode = text.partition(":")
        if separator != ":" or project not in PROJECTS or not mode.startswith("--") or " " in mode:
            raise SystemExit("Invalid --healthy-group: " + text)
        extras.append((project, (mode,)))
    groups = selected_groups(selected, extras)
    missing = [group_key(*g) for g in groups if EXPECTED_COUNTS.get(g) is None]
    if not options.apply:
        for w in selected:
            print(f"{w.name}: {w.source} -> {w.project} {' '.join(w.args)} must fail '{w.expected}'")
        print(json.dumps({"selectedSources": sorted({w.source for w in selected}), "healthyGroups": [group_key(*g) for g in groups],
                          "unpinnedInventory": missing, "status": "PLAN_ONLY_NOT_QUALIFICATION", "childProcessesStarted": 0}))
        return 0
    if missing:
        raise SystemExit("UNPINNED_HEALTHY_INVENTORY (no build started): " + ", ".join(missing))
    if options.output is None:
        raise SystemExit("--output is required with --apply")
    inventory = {group_key(*g): EXPECTED_COUNTS[g] for g in groups}
    evidence = (ROOT / options.output).resolve()
    if evidence.exists():
        raise SystemExit("--output must be a new directory")
    evidence.mkdir(parents=True)
    runner = Runner(evidence, selected, inventory, extras)
    report = {"started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "witnesses": [], "expectedInventory": inventory,
              "sourcesBefore": {s: sha256(b) for s, b in runner.original.items()}, "groups": [group_key(*g) for g in runner.groups]}
    healthy = None
    primary = None
    try:
        healthy = runner.healthy("baseline")
        report["baseline"] = healthy
        for w in selected:
            report["activeWitness"] = w.name
            report["witnesses"].append(runner.witness(w, healthy))
    except BaseException as error:
        primary = error
        report["error"] = f"{type(error).__name__}: {error}"
    cleanup_ok = cleanup_owned(runner, healthy, report)
    fired = len(report["witnesses"]) == len(selected) and all(item["verdict"] == "FIRED" for item in report["witnesses"])
    verdict = "PASS" if primary is None and cleanup_ok and fired else "FAIL"
    report["sourcesAfter"] = {}
    for source in runner.sources:
        try:
            report["sourcesAfter"][source] = sha256((ROOT / source).read_bytes())
        except OSError as error:
            report["sourcesAfter"][source] = "UNREADABLE: " + repr(error)
    report["sourcesRestored"] = report["sourcesAfter"] == report["sourcesBefore"]
    report["steps"] = runner.steps
    report["verdict"] = verdict if report["sourcesRestored"] else "FAIL"
    report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    (evidence / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"verdict": report["verdict"], "witnesses": len(report["witnesses"]), "fired": sum(item["verdict"] == "FIRED" for item in report["witnesses"]),
                      "sourcesRestored": report["sourcesRestored"], "report": str(evidence / "results.json")}, indent=2))
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
