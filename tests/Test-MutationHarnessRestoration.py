"""Exercise both mutation runners' real restoration on disposable sources, without builds."""

from __future__ import annotations

import contextlib
import argparse
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

import run_agentbroker_pin_diagnostic_mutations as broker
import run_installer_shutdown_mutations as installer

STATES = ("owned", "foreign", "missing", "read-denied", "write-denied", "metadata-denied",
          "linked-after-capture", "linked-after-read", "two-conflicts")

def link_directory(link: Path, target: Path) -> None:
    if os.name == "nt":
        result = subprocess.run(["cmd", "/d", "/c", "mklink", "/J", str(link), str(target)],
                                capture_output=True, timeout=10)
        if result.returncode:
            raise AssertionError("the synthetic junction could not be created")
    else:
        link.symlink_to(target, target_is_directory=True)


class RestorationChecks(unittest.TestCase):
    def test_reparse_attribute_is_refused_without_is_junction(self) -> None:
        # The shared helper must not depend on Path.is_junction, absent on Python 3.11.
        candidate = Path("synthetic-reparse") / "source.cs"
        metadata = SimpleNamespace(st_mode=stat.S_IFREG, st_file_attributes=0x400)
        with mock.patch.object(Path, "lstat", return_value=metadata):
            with self.assertRaisesRegex(RuntimeError, "Linked mutation/evidence path refused"):
                broker.refuse_links(candidate)

    def test_metadata_denial_is_not_reported_as_absence(self) -> None:
        with mock.patch.object(Path, "lstat", side_effect=PermissionError("synthetic")):
            with self.assertRaises(PermissionError):
                broker.refuse_links(Path("synthetic-denied"))

    def test_real_restorers_preserve_conflicts_and_restore_later_sources(self) -> None:
        for harness in ("broker", "installer"):
            for state in STATES:
                with self.subTest(harness=harness, state=state):
                    self.restoration_case(harness, state)

    def restoration_case(self, harness: str, state: str) -> None:
        with tempfile.TemporaryDirectory(prefix="mutation-restoration-check-") as name:
            root = Path(name)
            names = ("early/A.cs", "middle/B.cs", "later/Z.cs")
            original = {relative: ("original " + relative).encode() for relative in names}
            current = {relative: ("owned mutation " + relative).encode() for relative in names}
            for relative in names:
                (root / relative).parent.mkdir(parents=True)
                (root / relative).write_bytes(current[relative])
            early = root / names[0]
            middle = root / names[1]
            later = root / names[2]
            foreign = root / "foreign"
            foreign.mkdir()
            target = foreign / early.name
            target.write_bytes(current[names[0]])
            target_before = target.read_bytes()
            linked = False
            if state in ("foreign", "two-conflicts"):
                early.write_bytes(b"concurrent synthetic edit")
            elif state == "missing":
                early.unlink()
            if state == "two-conflicts":
                middle.unlink()

            def swap_directory() -> None:
                nonlocal linked
                early.parent.rename(root / "captured-early")
                link_directory(early.parent, foreign)
                linked = True

            if state == "linked-after-capture":
                swap_directory()
            before = early.read_bytes() if early.exists() else None
            actual_read = Path.read_bytes
            actual_write = Path.write_bytes
            actual_lstat = Path.lstat
            swapped = False
            unsafe_read = False

            def read(path: Path) -> bytes:
                nonlocal swapped, unsafe_read
                if path == early and state == "read-denied":
                    raise PermissionError("synthetic read refusal")
                if path == early and state == "linked-after-capture":
                    unsafe_read = True
                raw = actual_read(path)
                if path == early and state == "linked-after-read" and not swapped:
                    swap_directory()
                    swapped = True
                return raw

            def write(path: Path, raw: bytes) -> int:
                if path == early and state == "write-denied":
                    raise PermissionError("synthetic write refusal")
                return actual_write(path, raw)

            def metadata(path: Path, *args, **kwargs):
                if path == early and state == "metadata-denied":
                    raise PermissionError("synthetic metadata refusal")
                return actual_lstat(path, *args, **kwargs)

            refusal = None
            try:
                with contextlib.ExitStack() as patches:
                    patches.enter_context(mock.patch.object(Path, "read_bytes", read))
                    patches.enter_context(mock.patch.object(Path, "write_bytes", write))
                    patches.enter_context(mock.patch.object(Path, "lstat", metadata))
                    if harness == "broker":
                        runner = broker.Runner.__new__(broker.Runner)
                        runner.original = dict(original)
                        runner.owned_current = current
                        patches.enter_context(mock.patch.object(broker, "ROOT", root))
                        restore = runner.restore
                    else:
                        restore = lambda: installer.restore_owned(root, list(names), original, current)
                    try:
                        restore()
                    except RuntimeError as error:
                        refusal = str(error)
                self.assertEqual(later.read_bytes(), original[names[2]],
                                 "later-owned-source-restored after an earlier restoration refusal")
                self.assertFalse(unsafe_read, "linked-source-never-read")
                self.assertEqual(target.read_bytes(), target_before, "linked-target-never-written")
                if state == "owned":
                    self.assertIsNone(refusal, "owned restoration must pass")
                    self.assertEqual(early.read_bytes(), original[names[0]])
                else:
                    self.assertIsNotNone(refusal, "unsafe-earlier-source-refused")
                    self.assertIn(names[0], refusal)
                    self.assertNotIn(names[2], refusal)
                    self.assertEqual(early.read_bytes() if early.exists() else None, before,
                                     "unsafe-earlier-source-left-as-found")
                    if state == "two-conflicts":
                        self.assertIn(names[1], refusal, "all-restoration-conflicts-reported")
                        self.assertFalse(middle.exists(), "missing-source-never-recreated")
                    else:
                        self.assertEqual(middle.read_bytes(), original[names[1]])
            finally:
                # Remove the link itself before TemporaryDirectory's tree cleanup, never its target.
                if linked:
                    if os.name == "nt":
                        early.parent.rmdir()
                    else:
                        early.parent.unlink()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--restore-case", choices=[harness + "/" + state for harness in ("broker", "installer") for state in STATES])
    args = parser.parse_args()
    if args.restore_case:
        harness, state = args.restore_case.split("/")
        suite = unittest.TestSuite([unittest.FunctionTestCase(lambda: RestorationChecks().restoration_case(harness, state),
                                                            description=args.restore_case)])
    else:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(RestorationChecks)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if result.wasSuccessful():
        print("MutationRestorationChecks=" + str(result.testsRun))
    sys.exit(0 if result.wasSuccessful() else 1)
