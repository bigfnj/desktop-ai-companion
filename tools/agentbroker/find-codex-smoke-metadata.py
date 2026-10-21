"""Read-only exact READY predicate over local thread metadata, never rollout/transcript files.

Run from the repository: python -X utf8 tools/agentbroker/find-codex-smoke-metadata.py
Only exact matching thread UUIDs, recognized source categories and a count leave the query.
"""
import json
import os
from pathlib import Path
import sqlite3
import stat
import sys
import uuid

prompt = "Reply READY for the AgentBroker smoke test and use no tools."
if any(argument not in ("--allow-context-wrapper", "--short-readiness") for argument in sys.argv[1:]):
    raise SystemExit("Only --allow-context-wrapper and --short-readiness are supported; readiness phrases are fixed.")
allow_context_wrapper = "--allow-context-wrapper" in sys.argv[1:]
if "--short-readiness" in sys.argv[1:]:
    prompt = "Reply READY and use no tools"
database = Path(os.environ["USERPROFILE"]) / ".codex" / "state_5.sqlite"
if not database.is_file():
    raise SystemExit("Expected ordinary local thread metadata database is unavailable.")
for component in (database, *database.parents):
    metadata = component.stat(follow_symlinks=False)
    if metadata.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise SystemExit("Linked metadata paths are refused.")
if database.stat().st_nlink != 1:
    raise SystemExit("Hard-linked metadata files are refused.")
with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True, timeout=3) as connection:
    connection.execute("PRAGMA query_only=ON")
    predicate = "instr(first_user_message, ?) > 0" if allow_context_wrapper else "trim(first_user_message, char(10) || char(13) || ' .') = ?"
    rows = connection.execute(
        "SELECT id, source FROM threads WHERE " + predicate + " AND archived = 0 "
        "ORDER BY updated_at DESC LIMIT 3",
        (prompt.rstrip("."),),
    ).fetchall()
    matches = []
    for identifier, source in rows:
        parsed = uuid.UUID(identifier)
        if str(parsed) != identifier:
            raise SystemExit("Noncanonical matching thread identity was refused.")
        matches.append({"threadId": identifier, "source": source if source in ("vscode", "cli", "exec", "appServer") else "other"})
print(json.dumps({"probe": "codex-ready-metadata", "matches": matches, "ambiguous": len(matches) > 1,
                  "requiresLiveVerification": allow_context_wrapper,
                  "note": "Exact readiness predicate only; no transcript or other prompt content returned."}))
