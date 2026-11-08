"""A mutation harness's restore record ON DISK, so a hard kill cannot strand a mutant in the source tree.

N-audit-1106-345 (mutate-diagnostics.py) and N-audit-1106-350 (mutate-selftest-guards.py). Both harnesses write a mutant
into the working tree, keep the original bytes only in the memory of the running process, and write them back in a
finally. A hard kill runs no finally: the host ending a command at its time limit, a closed console, Task Manager, a
crashed interpreter. The mutant then stayed in the tree, and the next run read it as the clean source (its own case
NO-OP, every other verdict measured against it). Only the shard controller's git diff noticed, and it drives neither
mutate-diagnostics.py nor a direct --only run.

The record is written BEFORE the mutant reaches the file and removed only once the original bytes are back, so at every
instant either there is no record and the file is clean, or the record holds what puts it back. A run's first act is
repair(): with a record left by a run that died, a file whose bytes are the recorded mutant gets its original bytes
back, a file already holding the original just loses the record, and a file matching neither is REFUSED by name, never
guessed at. The rejected alternative, `git checkout` of the recorded file, would also throw away a lane's uncommitted
edits to it, which is exactly the state a harness is usually run against.

A repair cannot tell a dead run from a live one, so two runs of one harness in one tree would undo each other's mutant.
They could not share that tree anyway (the build folder is one), and the shard controller gives every shard its own.
"""

import base64
import binascii
import hashlib
import io
import json
import os


class JournalError(RuntimeError):
    """The record cannot be written, read or acted on; the run must stop rather than guess."""


def journal_path(harness_file):
    """Beside the harness, named for it: tests/<harness>.restore-journal.json (ignored by git)."""
    folder, name = os.path.split(os.path.abspath(harness_file))
    return os.path.join(folder, os.path.splitext(name)[0] + ".restore-journal.json")


def _sha256(data):
    return hashlib.sha256(data).hexdigest()


def _read(path):
    with io.open(path, "rb") as handle:
        return handle.read()


def _write(path, data):
    with io.open(path, "wb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())


class RestoreJournal(object):
    def __init__(self, path):
        self.path = path
        # Only a record THIS object wrote is removed by clear(): a record that was already there belongs to another run.
        self._mine = False

    def record(self, target, original, mutant, case):
        """Put the original bytes on disk before `mutant` is written to `target`. Refuses when a record exists."""
        if os.path.exists(self.path):
            raise JournalError("a restore record already exists (%s): another run of this harness is live in this tree, "
                               "or this run skipped its repair" % self.path)
        entry = {"path": os.path.abspath(target), "case": case, "original": base64.b64encode(original).decode("ascii"),
                 "originalSha256": _sha256(original), "mutantSha256": _sha256(mutant)}
        scratch = self.path + ".tmp"
        _write(scratch, json.dumps(entry, indent=1).encode("utf-8"))
        os.replace(scratch, self.path)
        self._mine = True

    def clear(self):
        """Remove this run's record; call it only once the original bytes are back."""
        if not self._mine:
            return
        try:
            os.remove(self.path)
        except FileNotFoundError:
            pass
        self._mine = False

    def repair(self):
        """(lines to print, the path put back or None) for a record a dead run left. Raises JournalError when the
        record cannot be read or its file matches neither the original nor the mutant."""
        try:
            raw = _read(self.path)
        except FileNotFoundError:
            return [], None
        try:
            entry = json.loads(raw.decode("utf-8"))
            target, case = entry["path"], entry["case"]
            original = base64.b64decode(entry["original"].encode("ascii"), validate=True)
            mutant_sha = entry["mutantSha256"]
            if _sha256(original) != entry["originalSha256"]:
                raise ValueError("the recorded original does not match its own SHA-256")
        except (ValueError, KeyError, TypeError, AttributeError, UnicodeError, binascii.Error) as error:
            raise JournalError("the restore record %s cannot be read (%s). Compare the tree with git diff, put back any "
                               "mutant by hand, then delete the record." % (self.path, error))
        try:
            current = _read(target)
        except FileNotFoundError:
            current = None
        if current == original:
            os.remove(self.path)
            return ["RESTORE RECORD: %s already holds its original bytes (case %r); the record was removed"
                    % (target, case)], None
        if current is None or _sha256(current) != mutant_sha:
            raise JournalError("%s matches neither the original nor the mutant that case %r recorded in %s, so it was "
                               "changed after the run that wrote the mutant ended. Compare it with git diff and put it "
                               "right by hand, then delete the record." % (target, case, self.path))
        _write(target, original)
        if _read(target) != original:
            raise JournalError("%s could not be given back its original bytes; the record %s is kept" % (target, self.path))
        os.remove(self.path)
        return ["REPAIRED: %s held the mutant of case %r from a run that ended without restoring it; its original "
                "bytes are back" % (target, case)], target


def repaired_loaded_module(path, modules):
    """True when `path` is the file of a module this process has already imported: then the run that repaired it is
    itself running the mutant, and must stop so the next run loads the original."""
    if path is None:
        return False
    wanted = os.path.normcase(os.path.abspath(path))
    for module in list(modules.values()):
        loaded = getattr(module, "__file__", None)
        if loaded and os.path.normcase(os.path.abspath(loaded)) == wanted:
            return True
    return False
