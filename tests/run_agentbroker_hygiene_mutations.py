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

# ---- 1.10.1 LocalOutputFile handle-owned publication (design v2 section 9, re-pinned to the implemented v2.1 file) ----
# Labels were first reasoned against LocalOutputFileChecks.cs, which stops at its first failure, then measured:
# all 46 FIRED at their labels in hygiene-mutations-localoutput2-20261109 (2026-11-09, sourcesRestored true).
# The failed first run is retained. Pre-screening corrected the first label for temp-proof-skipped and made
# handoff-null-cleanup-member compile before the authoritative run. Frozen recipes reproduce all recorded variants.
# Re-run the witnesses a new change can reach, selected with --case. Review the current session's test-tier
# authorization before repeating the full catalog; the prior result is not a reason to replay unaffected controls.
# Equivalent in bounds under v2.1, so deliberately not witnesses:
# - v2 W11/W12 (the held handle's reparse or directory attribute unchecked): step 2e re-walks canon from the held
#   component itself and refuses the same shapes with the same code.
# - 2e walking the typed parent instead of canon: the pin makes them differ in case only; when they differ, 2f either
#   refuses (a case-sensitive component) or proves every lookup in the chain case-insensitive, so both walks see the
#   same entries. LOCAL_OUTPUT_CANON_CHAIN_PLAIN therefore uses the ParentFinalPath seam, and its witness removes 2e.
# - RootDirectory left NULL with the bare leaf: MS-FSA 2.1.5.15.12 renames inside the temporary's own directory, which
#   the step-3 proof already pinned to the held one.
# - The STATUS_PENDING retention (`if (!pending) Marshal.FreeHGlobal(ioStatus);` made unconditional): no in-bounds input
#   makes a synchronous handle's rename return STATUS_PENDING, and RENAME_PENDING_REFUSED transforms the status AFTER a
#   real completed call, where freeing is correct. The decision itself reads only the returned NTSTATUS (v2.1 D2
#   correction), so there is no IO_STATUS_BLOCK clause left to mutate.
# - The temporary created at the DOS path instead of the volume GUID path: the same file on a frozen NTFS chain.
# - DangerousAddRef removed, FILE_FLAG_WRITE_THROUGH or FlushFileBuffers dropped, the IntPtr.Size guard, the outer
#   TypeLoadException conversion, the \\?\ prefix on long paths (LongPathsEnabled=1 here): not observable in bounds.
LOCAL = "tools/AgentBroker.Worker/LocalOutputFile.cs"
LOCAL_GROUP = ("--local-output-only",)
LOCAL_CHECKS = {
    "CANON_PIN_CASE": "LOCAL_OUTPUT_CANON_PIN_CASE: the held directory's final path pins the validated parent whatever case the user typed",
    "CANON_PIN_SHORT_NAME": "LOCAL_OUTPUT_CANON_PIN_SHORT_NAME: a final path that differs from the validated parent beyond case is refused",
    "CANON_PIN_PREFIX": "LOCAL_OUTPUT_CANON_PIN_PREFIX: only a long-path drive-letter final path can pin the parent",
    "FINAL_LEAF_CASE": "LOCAL_OUTPUT_FINAL_LEAF_CASE: a published leaf that differs only in case never matches, so a tunnel-cache rename is a safe refusal",
    "FINAL_LONGER_LEAF": "LOCAL_OUTPUT_FINAL_LONGER_LEAF: a longer final name never matches a shorter leaf",
    "FINAL_ROOT": "LOCAL_OUTPUT_FINAL_ROOT: an exact final path matches, directly under a volume root without a doubled separator too",
    "HELD_PLAIN": "LOCAL_OUTPUT_HELD_PLAIN: an owned plain directory opens as the held directory, proves local and pins its own path",
    "HELD_ANCESTOR_REPARSE": "LOCAL_OUTPUT_HELD_ANCESTOR_REPARSE: a held directory reached through an ancestor junction fails the pin",
    "ANCESTOR_REPARSE": "LOCAL_OUTPUT_ANCESTOR_REPARSE: a junction above the parent is refused invalid_output_path by Validate and Save and its target is untouched",
    "LOCAL_PROOF_UNAVAILABLE": "LOCAL_OUTPUT_LOCAL_PROOF_UNAVAILABLE: a held directory with no volume GUID path is refused invalid_output_path and nothing is created",
    "NOT_NTFS": "LOCAL_OUTPUT_NOT_NTFS: a held directory whose file system is not NTFS is refused invalid_output_path and nothing is created",
    "CANON_CHAIN_PLAIN": "LOCAL_OUTPUT_CANON_CHAIN_PLAIN: the canonical-chain re-walk refuses a reparse component above the held directory",
    "PUBLISH": "LOCAL_OUTPUT_PUBLISH: exact bytes, retention and listing after a handle-owned publication; the file reopens exclusively",
    "PUBLISH_BOUND": "LOCAL_OUTPUT_PUBLISH_BOUND: the temporary and the published file are proven at the held directory's volume GUID path plus their exact leaves",
    "HELD_DENIES_DELETE": "LOCAL_OUTPUT_HELD_DENIES_DELETE: while publishing, a delete-access open of the parent is a sharing violation; after return it succeeds",
    "CASE_VARIANT_PUBLISH": "LOCAL_OUTPUT_CASE_VARIANT_PUBLISH: a parent typed in another case over an all-insensitive canonical chain publishes",
    "CASE_SENSITIVE_REFUSED": "LOCAL_OUTPUT_CASE_SENSITIVE_REFUSED: a case-sensitive directory reached through a case-variant typed path is refused invalid_output_path and nothing is created",
    "CASE_SENSITIVE_EXACT": "LOCAL_OUTPUT_CASE_SENSITIVE_EXACT: the same case-sensitive directory typed exactly publishes",
    "CASE_SENSITIVE_ANCESTOR": "LOCAL_OUTPUT_CASE_SENSITIVE_ANCESTOR: a case-sensitive ancestor above an insensitive held directory typed in another case is refused invalid_output_path and nothing is created",
    "EXISTING_REFUSED": "LOCAL_OUTPUT_EXISTING_REFUSED: an existing destination is refused output_exists before any temporary is chosen",
    "TEMP_NAME_TAKEN": "LOCAL_OUTPUT_TEMP_NAME_TAKEN: a file already at the temporary name is refused and left byte-identical",
    "BEFORE_PUBLISH_FAILURE": "LOCAL_OUTPUT_BEFORE_PUBLISH_FAILURE: a failure before publication surfaces unchanged and the temporary is removed by POSIX delete through its handle",
    "CLEANUP_REPORTED": "LOCAL_OUTPUT_CLEANUP_REPORTED: a refused discard leaves the primary exception unchanged, reports temporary_not_removed and both exit-74 bodies carry it",
    "ERROR_BODY_UNCHANGED": "LOCAL_OUTPUT_ERROR_BODY_UNCHANGED: without a cleanup report both exit-74 bodies are byte-identical to 1.10.0, with no null member",
    "HANDOFF_CLI_CLEANUP": "LOCAL_OUTPUT_HANDOFF_CLI_CLEANUP: --handoff-prepare reports a refused discard as cleanup temporary_not_removed in its exit-74 JSON",
    "MCP_CLI_CLEANUP": "LOCAL_OUTPUT_MCP_CLI_CLEANUP: --mcp-call reports a refused discard as cleanup temporary_not_removed in its exit-74 JSON with the known response",
    "ATOMIC_NO_REPLACE": "LOCAL_OUTPUT_ATOMIC_NO_REPLACE: a destination created after flush wins, the refusal is output_exists and no temporary remains",
    "RENAME_FAILURE_TYPED": "LOCAL_OUTPUT_RENAME_FAILURE_TYPED: a non-collision rename status is IOException output_unavailable carrying the status and leaves nothing",
    "RENAME_PENDING_REFUSED": "LOCAL_OUTPUT_RENAME_PENDING_REFUSED: a STATUS_PENDING rename result is output_unavailable, never success, and leaves nothing",
    "FINAL_PATH_MISMATCH": "LOCAL_OUTPUT_FINAL_PATH_MISMATCH: a published file whose final path disagrees is removed by its own handle and a planted decoy is untouched",
    "TEMP_OUTSIDE_HELD": "LOCAL_OUTPUT_TEMP_OUTSIDE_HELD: a temporary not proven at the held directory is removed by its own handle and a planted decoy is untouched",
    "POST_CREATE_CHAIN": "LOCAL_OUTPUT_POST_CREATE_CHAIN: the definitive canonical-chain check runs once the owned temporary exists and before any payload byte, and its refusal leaves nothing",
    "POST_CREATE_CASE": "LOCAL_OUTPUT_POST_CREATE_CASE: the definitive case-sensitivity check runs once the owned temporary exists and before any payload byte, and its refusal leaves nothing",
    "SOURCE_NO_PATH_DESTRUCTION": "LOCAL_OUTPUT_SOURCE_NO_PATH_DESTRUCTION: LocalOutputFile.cs contains no path-based delete, move or replace call",
    "PREFLIGHT_PIN": "LOCAL_OUTPUT_PREFLIGHT_PIN: Validate makes the held-directory proofs, so a parent whose final path names another volume is refused before any output exists",
    "MCP_PREFLIGHT_PIN": "LOCAL_OUTPUT_MCP_PREFLIGHT_PIN: --mcp-call refuses a parent that fails the pin in its preflight, before dispatch, not after it at Save",
}
LOCAL_DISCARD = ("                DiscardOutcome outcome = Discard(temp, seams);\n                seams?.Discarded?.Invoke(outcome);\n"
                 "                if (outcome == DiscardOutcome.NotDeleted) error.Data[CleanupDataKey] = TemporaryNotRemoved;\n                throw;")
LOCAL_FINAL_PROOF = 'if (!IsFinalPathOf(published, volume, leaf)) throw new IOException("output_unavailable");'
LOCAL_PIN_EQUALS = "string.Equals(finalPath[4..], parent, StringComparison.OrdinalIgnoreCase) ? finalPath[4..] : null;"
LOCAL_CASE_STEP = "if (!string.Equals(canon, parent, StringComparison.Ordinal)) RequireCaseInsensitiveChain(canon, seams);"
LOCAL_DEFINITIVE = "RequireChain(canon, parent, seams); // step 3b, post-create: our entry exists; last proof before any payload byte"
HANDOFF = "tools/AgentBroker.Worker/HandoffPrepareClient.cs"
MCP_CALL = "tools/AgentBroker.Worker/McpCallClient.cs"
SAVE_VALIDATE = "            string full = ValidatePath(destination); // step 2 below makes the held-directory proofs on the handle it keeps\n"


def local_witness(name: str, replacements, check: str, source: str = LOCAL) -> "Witness":
    return Witness(name, source, "worker", LOCAL_GROUP, tuple(replacements), LOCAL_CHECKS[check])


LOCAL_OUTPUT_WITNESSES = (
    local_witness("local-output-final-proof-skipped", ((LOCAL_FINAL_PROOF, "", 1),), "FINAL_PATH_MISMATCH"),
    # First failure measured by the 2026-11-09 pre-screen (kill_check.py on a scratch copy): HANDOFF_CLI_CLEANUP (#32) forces
    # its refusal through this same step-3 proof and runs before TEMP_OUTSIDE_HELD (#38). The group stops at its first
    # failure, so #38 catching this mutant too is reasoned, not measured. Fired at #32 in hygiene-mutations-localoutput2-20261109.
    local_witness("local-output-temp-proof-skipped",
                  (('if (!IsFinalPathOf(created, volume, tempLeaf)) throw new IOException("output_unavailable");', "", 1),), "HANDOFF_CLI_CLEANUP"),
    # Post-push audit of 7ad9d901 (U9): step 3 weakened to a presence check. The null-path CLI checks (#32/#33) still refuse,
    # so only the non-null path moved into another directory (TEMP_OUTSIDE_HELD) catches it.
    local_witness("local-output-temp-proof-presence-only",
                  (('if (!IsFinalPathOf(created, volume, tempLeaf)) throw new IOException("output_unavailable");',
                    'if (created is null) throw new IOException("output_unavailable");', 1),), "TEMP_OUTSIDE_HELD"),
    local_witness("local-output-replace-if-exists",
                  (("info[0] = 0; // ReplaceIfExists = FALSE", "info[0] = 1; // ReplaceIfExists = FALSE", 1),), "ATOMIC_NO_REPLACE"),
    # A path delete after closing: the temporary does go, but never through its handle, so Discarded is not observed.
    local_witness("local-output-cleanup-close-then-path",
                  ((LOCAL_DISCARD, "                temp.Dispose(); File.Delete(Join(volume, tempLeaf)); _ = error;\n                throw;", 1),),
                  "BEFORE_PUBLISH_FAILURE"),
    # Share mode 0 refuses the path delete, and the throwing catch replaces the caller's exception.
    local_witness("local-output-cleanup-path-open-handle",
                  ((LOCAL_DISCARD, "                File.Delete(Join(volume, tempLeaf)); _ = error;\n                throw;", 1),),
                  "BEFORE_PUBLISH_FAILURE"),
    local_witness("local-output-cleanup-skipped", ((LOCAL_DISCARD, "                _ = error;\n                throw;", 1),),
                  "BEFORE_PUBLISH_FAILURE"),
    local_witness("local-output-mismatch-deletes-observed-path",
                  ((LOCAL_FINAL_PROOF, 'if (!IsFinalPathOf(published, volume, leaf)) { if (published is not null) File.Delete(published); '
                    'throw new IOException("output_unavailable"); }', 1),), "FINAL_PATH_MISMATCH"),
    # FileNameLength in characters: the kernel sees half the name ("out."), and step 7 discards the misnamed file.
    local_witness("local-output-rename-length-chars",
                  (("BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)name.Length);",
                    "BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)leaf.Length);", 1),), "PUBLISH"),
    local_witness("local-output-ansi-marshalling",
                  (('[DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]\n'
                    "    private static extern SafeFileHandle CreateFileW(",
                    '[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]\n'
                    "    private static extern SafeFileHandle CreateFileW(", 1),), "HELD_PLAIN"),
    # Falling back to the typed DOS path when no volume GUID path exists: the temporary lands in owned scratch, the GUID
    # proof then refuses output_unavailable, and the check requires invalid_output_path with nothing created.
    local_witness("local-output-local-proof-dropped",
                  (('if (guid is null) throw new InvalidDataException("invalid_output_path");',
                    r'if (guid is null) guid = @"\\?\" + parent;', 1),), "LOCAL_PROOF_UNAVAILABLE"),
    local_witness("local-output-pin-dropped",
                  (('canon = CanonicalDirectory(dos, parent) ?? throw new InvalidDataException("invalid_output_path");',
                    'canon = dos?[4..] ?? throw new InvalidDataException("invalid_output_path");', 1),), "HELD_ANCESTOR_REPARSE"),
    # Also valid against e398eba2, where the only junction fixtures were depth 1.
    local_witness("local-output-ancestor-walk-truncated",
                  (("for (string? component = directory; component is not null; component = Path.GetDirectoryName(component))",
                    "for (string? component = directory; component is not null; component = null)", 1),), "ANCESTOR_REPARSE"),
    local_witness("local-output-collision-uncoded",
                  (("bool exists = status == StatusObjectNameCollision;", "bool exists = false;", 1),), "ATOMIC_NO_REPLACE"),
    # The bare Win32Exception is converted to output_unavailable by the outer catch, so the collision loses output_exists.
    local_witness("local-output-rename-error-untyped",
                  (("if (status != 0) throw RenameFailure(status);", "if (status != 0) throw new Win32Exception(status);", 1),),
                  "ATOMIC_NO_REPLACE"),
    local_witness("local-output-ntstatus-dropped", (("        inner.Data[NtStatusDataKey] = text;\n", "", 1),), "RENAME_FAILURE_TYPED"),
    local_witness("local-output-rename-pending-accepted",
                  (("if (status != 0) throw RenameFailure(status);", "if (status < 0) throw RenameFailure(status);", 1),),
                  "RENAME_PENDING_REFUSED"),
    local_witness("local-output-temp-create-always",
                  (("TempAccess, 0, IntPtr.Zero, CreateNew,", "TempAccess, 0, IntPtr.Zero, 2u,", 1),), "TEMP_NAME_TAKEN"),
    local_witness("local-output-final-compare-ignore-case",
                  (("finalPath is not null && string.Equals(finalPath, Join(directory, leaf), StringComparison.Ordinal);",
                    "finalPath is not null && string.Equals(finalPath, Join(directory, leaf), StringComparison.OrdinalIgnoreCase);", 1),),
                  "FINAL_LEAF_CASE"),
    local_witness("local-output-final-compare-prefix",
                  (("finalPath is not null && string.Equals(finalPath, Join(directory, leaf), StringComparison.Ordinal);",
                    "finalPath is not null && finalPath.StartsWith(Join(directory, leaf), StringComparison.Ordinal);", 1),),
                  "FINAL_LONGER_LEAF"),
    local_witness("local-output-pin-ordinal",
                  ((LOCAL_PIN_EQUALS, "string.Equals(finalPath[4..], parent, StringComparison.Ordinal) ? finalPath[4..] : null;", 1),),
                  "CANON_PIN_CASE"),
    local_witness("local-output-pin-always", ((LOCAL_PIN_EQUALS, "true ? finalPath[4..] : null;", 1),), "CANON_PIN_SHORT_NAME"),
    local_witness("local-output-pin-prefix-dropped",
                  ((r'finalPath is not null && finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&', "finalPath is not null &&", 1),),
                  "CANON_PIN_PREFIX"),
    local_witness("local-output-join-naive",
                  (("directory.EndsWith(Path.DirectorySeparatorChar) ? directory + name : directory + Path.DirectorySeparatorChar + name;",
                    "directory + Path.DirectorySeparatorChar + name;", 1),), "FINAL_ROOT"),
    # Without FILE_TRAVERSE the held handle takes no part in sharing (MS-FSA 2.1.5.1.2.2). If the run instead fails
    # PUBLISH, the relative rename needs traverse on the root handle: record that, it is still a kill.
    local_witness("local-output-traverse-dropped",
                  (("HeldAccess = FileTraverse | FileReadAttributes | Synchronize;", "HeldAccess = FileReadAttributes | Synchronize;", 1),),
                  "HELD_DENIES_DELETE"),
    local_witness("local-output-share-delete-added",
                  (("FileShareRead | FileShareWrite, IntPtr.Zero,", "FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero,", 1),),
                  "HELD_DENIES_DELETE"),
    # Unreachable by construction; an unreachable path delete still violates the rule the source check states.
    local_witness("local-output-source-path-delete",
                  ((SAVE_VALIDATE,
                    SAVE_VALIDATE + "            if (bytes.Length < 0) File.Delete(full);\n", 1),),
                  "SOURCE_NO_PATH_DESTRUCTION"),
    # Post-push audit of 7ad9d901 (U10): the same dormant delete in its instance spelling.
    local_witness("local-output-source-fileinfo-delete",
                  ((SAVE_VALIDATE, SAVE_VALIDATE + "            if (bytes.Length < 0) new FileInfo(full).Delete();\n", 1),),
                  "SOURCE_NO_PATH_DESTRUCTION"),
    # Post-push audit of 7ad9d901 (C2): the preflight makes no held-directory proof, so a SUBST-like parent passes it.
    local_witness("local-output-preflight-proof-removed",
                  (("try { using (OpenHeldDirectory(Path.GetDirectoryName(full)!, seams, out _, out _)) { } }", "try { _ = seams; }", 1),),
                  "PREFLIGHT_PIN"),
    # C2 at the caller: --mcp-call drops its preflight, so the pin refusal arrives only from Save, after the call.
    local_witness("local-output-mcp-preflight-removed",
                  (("        try { LocalOutputFile.Validate(options.OutputFile, seams); }", "        try { }", 1),),
                  "MCP_PREFLIGHT_PIN", MCP_CALL),
    # Classic semantics keeps the name visible while the check's own attribute-only observer handle is open.
    local_witness("local-output-posix-flag-dropped",
                  (("WriteUInt32LittleEndian(posix, DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly);",
                    "WriteUInt32LittleEndian(posix, DispositionDelete | DispositionIgnoreReadOnly);", 1),), "BEFORE_PUBLISH_FAILURE"),
    local_witness("local-output-precheck-removed",
                  (('try { _ = File.GetAttributes(full); throw new IOException("output_exists"); }', "try { }", 1),), "EXISTING_REFUSED"),
    # ---- v2.1 additions ----
    local_witness("local-output-ntfs-unchecked",
                  (('            if (!string.Equals(fileSystem, "NTFS", StringComparison.Ordinal)) throw new InvalidDataException("invalid_output_path");\n',
                    "", 1),), "NOT_NTFS"),
    # 2e removed from RequireChain, so from the preflight and the definitive position alike.
    local_witness("local-output-canon-chain-skipped", (("        RequireCanonicalChain(canon, seams);\n", "", 1),), "CANON_CHAIN_PLAIN"),
    local_witness("local-output-case-sensitive-accepted",
                  (("if ((observed & CaseSensitiveDirectory) != 0) throw new InvalidDataException(\"invalid_output_path\");", "_ = observed;", 1),),
                  "CASE_SENSITIVE_REFUSED"),
    local_witness("local-output-case-step-skipped", ((LOCAL_CASE_STEP, "_ = canon;", 1),), "CASE_SENSITIVE_REFUSED"),
    local_witness("local-output-case-step-always", ((LOCAL_CASE_STEP, "RequireCaseInsensitiveChain(canon, seams);", 1),), "CASE_SENSITIVE_EXACT"),
    # Review round 1: 2f walking the held directory only. The held directory's own flag governs only the leaf lookup, so
    # CASE_SENSITIVE_REFUSED (whose flagged component IS the held directory) still refuses; the flagged ancestor does not.
    local_witness("local-output-case-ancestors-unchecked",
                  (("for (string? current = canon; current is not null; current = Path.GetDirectoryName(current))",
                    "for (string? current = canon; current is not null; current = null)", 1),), "CASE_SENSITIVE_ANCESTOR"),
    # Review round 1, design v2.1 D5. The preflight alone: CANON_CHAIN_PLAIN holds the directory without saving.
    local_witness("local-output-preflight-skipped",
                  (("            RequireChain(canon, parent, seams); // preflight (design v2.1 D5); Save repeats it once the temporary exists\n", "", 1),),
                  "CANON_CHAIN_PLAIN"),
    # The definitive position removed, moved after the payload write, or with its 2f half disabled (canon passed as parent).
    local_witness("local-output-definitive-skipped", (("                " + LOCAL_DEFINITIVE + "\n", "", 1),), "POST_CREATE_CHAIN"),
    local_witness("local-output-definitive-after-write",
                  ((LOCAL_DEFINITIVE + "\n                Write(temp, bytes);", "Write(temp, bytes);\n                " + LOCAL_DEFINITIVE, 1),),
                  "POST_CREATE_CHAIN"),
    local_witness("local-output-definitive-case-skipped",
                  ((LOCAL_DEFINITIVE, LOCAL_DEFINITIVE.replace("RequireChain(canon, parent, seams);", "RequireChain(canon, canon, seams);"), 1),),
                  "POST_CREATE_CASE"),
    # NOTE: LOCAL_OUTPUT_ROOT_SPELLING=exact in the healthy run, so no earlier check types a case variant.
    local_witness("local-output-case-variant-refused",
                  ((LOCAL_CASE_STEP, 'if (!string.Equals(canon, parent, StringComparison.Ordinal)) throw new InvalidDataException("invalid_output_path");', 1),),
                  "CASE_VARIANT_PUBLISH"),
    local_witness("local-output-cleanup-unreported",
                  (("if (outcome == DiscardOutcome.NotDeleted) error.Data[CleanupDataKey] = TemporaryNotRemoved;", "_ = error;", 1),),
                  "CLEANUP_REPORTED"),
    local_witness("local-output-handoff-cleanup-unlisted",
                  (('code = "output_unavailable", cleanup,\n', 'code = "output_unavailable", cleanupReport = cleanup,\n', 1),),
                  "CLEANUP_REPORTED", "tools/AgentBroker.Worker/HandoffPrepareClient.cs"),
    local_witness("local-output-mcp-cleanup-unlisted",
                  (('code = "output_unavailable", cleanup, response,', 'code = "output_unavailable", cleanupReport = cleanup, response,', 1),),
                  "CLEANUP_REPORTED", "tools/AgentBroker.Worker/McpCallClient.cs"),
    local_witness("local-output-handoff-null-cleanup-member",
                  (('retained = false, code = "output_unavailable",\n',
                    'retained = false, code = "output_unavailable", cleanup = (string)null!,\n', 1),),  # (string?)null failed CS8619 under -warnaserror
                  "ERROR_BODY_UNCHANGED", "tools/AgentBroker.Worker/HandoffPrepareClient.cs"),
    # Review round 1: each CLI catch site reverted to its 1.10.0 inline body. CLEANUP_REPORTED and ERROR_BODY_UNCHANGED
    # call OutputError directly and still pass; only the in-process CLI run through the catch site sees the dropped member.
    local_witness("local-output-handoff-catch-inline",
                  (("return await WriteAsync(OutputError(error), 74);",
                    'return await WriteAsync(new { status = "output_error", dispatchState = "not_dispatched", retained = false, '
                    'code = "output_unavailable", nextAction = "Choose a new local file in an existing plain directory; no broker was contacted." }, 74);', 1),),
                  "HANDOFF_CLI_CLEANUP", HANDOFF),
    local_witness("local-output-mcp-catch-inline",
                  (("return await WriteAsync(output, OutputError(error, document.RootElement), 74, CancellationToken.None);",
                    'return await WriteAsync(output, new { status = "output_error", dispatchState = document.RootElement.GetProperty("dispatchState").GetString(), '
                    'retained = false, code = "output_unavailable", response = document.RootElement, nextAction = "Retain this response using a new local '
                    'file. Do not repeat a confirmed operation because retention failed." }, 74, CancellationToken.None);', 1),),
                  "MCP_CLI_CLEANUP", MCP_CALL),
    # Step 7 regressed to the DOS final path: PUBLISH still passes (the DOS path matches), PUBLISH_BOUND does not.
    local_witness("local-output-proof-dos-final-path",
                  (("string? published = FinalPath(temp, VolumeNameGuid);", "string? published = FinalPath(temp, VolumeNameDos);", 1),
                   ("if (!IsFinalPathOf(published, volume, leaf))", r'if (!IsFinalPathOf(published, @"\\?\" + canon, leaf))', 1)),
                  "PUBLISH_BOUND"),
)

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
) + (
    # 1.10.1 (post-push audit of e398eba2): restore the 1.10.0 order, rebuilding owed notices AFTER the constructor's
    # recovery commit. A source recovered past its retention is then dropped twice in one store opening.
    Witness("store-owed-rebuilt-after-recovery", STORE, "core", ("--uncertain-notices-only",),
            (("            owed = RebuildOwed();\n            if (!existed || upgraded", "            if (!existed || upgraded", 1),
             ("            (inboxSequences, inboxHighWater) = PrepareInboxSequences(state.Messages);\n",
              "            (inboxSequences, inboxHighWater) = PrepareInboxSequences(state.Messages);\n            owed = RebuildOwed();\n", 1)),
            "UNCERTAIN_NOTICE_REOPEN_PAST_RETENTION: a source recovered after its retention ended drops its advisory once, not again on the next pass"),
    # 1.10.1 item 5 (post-push audit of e398eba2): recovery guidance for both message_expired causes and the retained-notice
    # ID reservation, the per-tool description copies that tools/list serves, and the late-Accepted branch the guidance cites.
    Witness("guidance-expired-unmapped", "tools/AgentBroker.Worker/BrokerRecoveryGuidance.cs", "worker", ("--recovery-guidance-only",),
            (('"message_expired" => new(code, "get_message_receipt",', '"message_expired_unmapped" => new(code, "get_message_receipt",', 1),),
            "RECOVERY_GUIDANCE_MESSAGE_EXPIRED: a late acknowledgement is told how to read its receipt for both causes"),
    Witness("guidance-reservation-unexplained", "tools/AgentBroker.Worker/BrokerRecoveryGuidance.cs", "worker", ("--recovery-guidance-only",),
            (("A missing receipt neither frees the ID nor explains the collision: the ID can remain reserved, for example by a retained delivery notice about its earlier message, or it may already have been released. ", "", 1),),
            "RECOVERY_GUIDANCE_ID_RESERVED: an ID reserved by a retained notice is explained, and a fresh ID is only for deliberately new work"),
    Witness("description-receipt-wait-reverted", TOOLS, "worker", ("--instructions-only",),
            (("This uses the sender's one receipt wait (wait_in_progress), which may run beside its incoming wait, and renews it.",
              "This uses the sender's one active wait (wait_in_progress) and renews it.", 1),),
            "TOOL_DESCRIPTION_WAIT_CLASSES: tools/list describes the shared incoming wait and the separate receipt wait"),
    Witness("description-reservation-delayed", TOOLS, "worker", ("--instructions-only",),
            (("its ID stays reserved while the terminal notice about it is retained (message_id_collision); choose a fresh ID only for deliberately new work.",
              "its ID also remains reserved while its delayed terminal notice is retained (message_id_collision); choose a fresh ID for new work.", 1),),
            "TOOL_DESCRIPTION_ID_RESERVATION: send states the general rule that a retained terminal notice reserves its source ID"),
    Witness("store-late-accepted-allowed", STORE, "core", ("--hygiene-only",),
            (('            if (previous.Status == MessageStatus.Accepted && previous.Message.ExpiresAt <= Now) Fail("message_expired");', "            _ = previous;", 1),),
            "LATE_ACCEPTED_EXPIRED: a never-submitted message past its deadline refuses Delivered as message_expired and reads Expired"),
) + LOCAL_OUTPUT_WITNESSES


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
# --receipt-wait-only 22 -> 24 (2026-11-09, test-only): the SetupFrozenClock repair adds the admission witnesses
# WITNESS RECEIPT_WAKES_AT_DEADLINE and WITNESS RECEIPT_WAIT_SUBMITTED_AGES_UNCERTAIN.
# 1.10.1 item 5: core --hygiene-only 12 -> 13 (LATE_ACCEPTED_EXPIRED), worker --instructions-only 12 -> 14
# (TOOL_DESCRIPTION_WAIT_CLASSES, TOOL_DESCRIPTION_ID_RESERVATION), --recovery-guidance-only 28 -> 30
# (RECOVERY_GUIDANCE_MESSAGE_EXPIRED, RECOVERY_GUIDANCE_ID_RESERVED). Each measured on the fixed source.
# R wire acceptance: --recovery-guidance-only measured 30 -> 38 on fixture F79B28B7, host baseline 9a3e7a6b.
# RECOVERY_GUIDANCE_WIRE_RESERVED_SETUP, RECOVERY_GUIDANCE_WIRE_LATE_SETUP,
# RECOVERY_GUIDANCE_WIRE_LATE_HELD_DELIVERED, RECOVERY_GUIDANCE_WIRE_LATE_SETTLED_DELIVERED,
# RECOVERY_GUIDANCE_WIRE_LATE_HELD_REFUSED, RECOVERY_GUIDANCE_WIRE_LATE_SETTLED_REFUSED,
# RECOVERY_GUIDANCE_WIRE_RESERVED_SOURCE_ABSENT, RECOVERY_GUIDANCE_WIRE_RESERVED_ID.
EXPECTED_COUNTS = {("core", ("--hygiene-only",)): 13, ("core", ("--peer-receipt-only",)): 22,
                   ("core", ("--commit-timing-only",)): 9, ("worker", ("--receipt-wait-only",)): 24,
                   ("worker", ("--instructions-only",)): 14, ("worker", ("--recovery-guidance-only",)): 38,
                   ("worker", ("--first-contact-only",)): 43,
                   ("worker", ("--directory-stop-core-only",)): 3, ("worker", ("--directory-host-stop-only",)): 3,
                   ("worker", ("--directory-stop-only",)): 6, ("worker", ("--reply-only",)): 13,
                   # 1.10.1: 80 at e398eba2 plus UNCERTAIN_NOTICE_REOPEN_PAST_RETENTION (measured 81 on the fixed source).
                   ("core", ("--uncertain-notices-only",)): 81,
                   # 1.10.1 LocalOutputFile v2.1: the new group, measured 36 on its first healthy focused runs (2026-11-09).
                   # 36 -> 41 (review round 1, measured): CASE_SENSITIVE_ANCESTOR, HANDOFF_CLI_CLEANUP, MCP_CLI_CLEANUP,
                   # POST_CREATE_CHAIN and POST_CREATE_CASE.
                   ("worker", ("--local-output-only",)): 43}
CORE_SUMMARIES = {("--hygiene-only",): "AgentBroker hygiene smoke", ("--peer-receipt-only",): "AgentBroker peer receipt smoke",
                  ("--commit-timing-only",): "AgentBroker commit timing smoke",
                  ("--uncertain-notices-only",): "AgentBroker uncertainty notice smoke"}


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
