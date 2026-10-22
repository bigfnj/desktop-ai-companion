"""Deterministic complete-inventory plans for isolated mutation-test shards.

Each shard still runs its harness's entire baseline and final clean rebuild.
Shards require different worktrees/output/data directories. The manifest lets a
release verifier prove their union covers one unchanged complete inventory.
"""

from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

from mutation_process import run_text


class CasePlanError(ValueError):
    pass


def source_commit(root):
    try:
        result = run_text(["git", "-C", os.fspath(root), "rev-parse", "--verify", "HEAD"], timeout=15)
    except (OSError, UnicodeError, subprocess.SubprocessError) as error:
        raise CasePlanError("cannot verify source commit: %s" % type(error).__name__) from error
    value = result.stdout.strip()
    if result.returncode != 0 or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", value):
        raise CasePlanError("cannot verify source commit")
    return value


def add_selection_arguments(parser):
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--only", default=None, help="case-insensitive case-name substring")
    group.add_argument("--shard", default=None, metavar="I/N",
                       help="one-based deterministic shard; all N isolated shards cover the full inventory")
    parser.add_argument("--list-cases-json", action="store_true",
                        help="print the verified case plan as JSON without building or mutating anything")


def _canonical(value, root):
    if isinstance(value, bytes):
        return {"bytesSha256": hashlib.sha256(value).hexdigest(), "length": len(value)}
    if isinstance(value, str):
        # Worktree-dependent source/project/artifact paths must hash identically
        # in every isolated copy. Never read the named path to normalize it.
        prefix = os.fspath(root).rstrip("\\/")
        if os.path.normcase(value).startswith(os.path.normcase(prefix + os.sep)):
            return {"repoPath": value[len(prefix) + 1:].replace("\\", "/")}
        return value
    if isinstance(value, (list, tuple)):
        return [_canonical(part, root) for part in value]
    if value is None or isinstance(value, (int, bool)):
        return value
    raise CasePlanError("case inventory contains an unsupported value type")


@dataclass(frozen=True)
class CasePlan:
    cases: tuple
    indices: tuple
    manifest: dict

    def json(self):
        return json.dumps(self.manifest, sort_keys=True, ensure_ascii=True, separators=(",", ":"))

    def summary(self):
        summary = dict(self.manifest)
        summary.pop("cases")
        summary["selectedIndices"] = list(self.indices)
        return json.dumps(summary, sort_keys=True, ensure_ascii=True, separators=(",", ":"))


def make_plan(cases, root, commit, only=None, shard=None):
    """Pure validation/selection. Does not open targets, build, or mutate files."""
    cases = tuple(cases)
    if not cases:
        raise CasePlanError("case inventory is empty")
    if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", commit):
        raise CasePlanError("source commit must be a verified full object ID")
    if only is not None and shard is not None:
        raise CasePlanError("--only and --shard are mutually exclusive")
    if only is not None and (not isinstance(only, str) or not only.strip()):
        raise CasePlanError("--only must be a nonempty substring")

    root = Path(root).resolve()
    seen = set()
    records = []
    for index, case in enumerate(cases, 1):
        if not isinstance(case, (tuple, list)) or not case or not isinstance(case[0], str) or not case[0].strip():
            raise CasePlanError("every case must have a nonempty string ID")
        identity = case[0].casefold()
        if identity in seen:
            raise CasePlanError("duplicate case ID: " + case[0])
        seen.add(identity)
        records.append({"index": index, "id": case[0], "definition": _canonical(case, root)})
    canonical = json.dumps({"schemaVersion": 1, "cases": records}, sort_keys=True,
                           ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    inventory_sha = hashlib.sha256(canonical).hexdigest()

    if shard is not None:
        match = re.fullmatch(r"([1-9][0-9]*)/([1-9][0-9]*)", shard) if isinstance(shard, str) else None
        if not match or len(shard) > 32:
            raise CasePlanError("--shard must have the one-based form I/N")
        shard_index, shard_count = map(int, match.groups())
        if shard_index > shard_count:
            raise CasePlanError("shard index exceeds shard count")
        if shard_count > len(cases):
            raise CasePlanError("shard count would create empty shards")
        indices = tuple(index for index in range(1, len(cases) + 1)
                        if (index - 1) % shard_count == shard_index - 1)
        selection = {"kind": "shard", "index": shard_index, "count": shard_count}
    elif only is not None:
        indices = tuple(index for index, case in enumerate(cases, 1) if only.casefold() in case[0].casefold())
        selection = {"kind": "only", "substring": only}
    else:
        indices = tuple(range(1, len(cases) + 1))
        selection = {"kind": "all"}
    if not indices:
        raise CasePlanError("selection contains no cases")
    manifest = {"schemaVersion": 1, "sourceCommit": commit, "inventorySha256": inventory_sha,
                "inventoryCount": len(cases), "selection": selection, "selectedCount": len(indices),
                "cases": [{"index": index, "id": cases[index - 1][0]} for index in indices]}
    return CasePlan(tuple(cases[index - 1] for index in indices), indices, manifest)


def plan_arguments(parser, cases, root, args):
    try:
        return make_plan(cases, root, source_commit(root), args.only, args.shard)
    except CasePlanError as error:
        parser.error(str(error))
