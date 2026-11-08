"""mutate-hardening-guards.py's scoring on synthetic cases: what a degraded baseline may and may not count as FIRED.

The invariant script is a fake (run() is patched) and the one target is a synthetic file in a temporary folder, so no
product file is read or written, nothing is built, and no process is started.
"""

import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


DEGRADED = ("PASS: an earlier invariant\n"
            "SMOKETEST.md's source-invariant count matches this file -- it says 600, there are 601 failed.\n")


class HardeningDriverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().with_name("mutate-hardening-guards.py")
        spec = importlib.util.spec_from_file_location("hardening_driver_test", path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def score_doc_drift(self, mutant_output):
        """One DOC DRIFT-shaped case against a degraded baseline; the mutant's run prints `mutant_output`."""
        with tempfile.TemporaryDirectory() as folder:
            doc = Path(folder, "SMOKETEST.md")
            doc.write_bytes(b"600 source invariants\n")
            case = ("synthetic doc drift", str(doc), b"600 source invariants", b"7600 source invariants",
                    self.harness.DOC_COUNT_DRIFT)
            runs = [(1, DEGRADED), (1, mutant_output)]
            console = io.StringIO()
            # main() keeps its restore record beside the harness (N-bd-w6-tests-harness-1-01); here it goes in the
            # temporary folder, so this test never reads or leaves the record of a real run in the same tree. A mutant
            # that stops clearing it left one there, which then refused every later run of the harness.
            with patch.object(self.harness, "journal_path", return_value=str(Path(folder, "hardening.restore-journal.json"))), \
                 patch.object(self.harness, "CASES", (case,)), \
                 patch.object(self.harness, "run", side_effect=runs), \
                 patch.object(self.harness.sys, "argv", ["mutate-hardening-guards.py"]), \
                 contextlib.redirect_stdout(console):
                code = self.harness.main()
            # WITNESS: the target is restored whatever the verdict.
            self.assertEqual(doc.read_bytes(), b"600 source invariants\n")
        return code, console.getvalue()

    def test_a_degraded_baseline_does_not_fire_a_case_on_its_own_failure_line(self):
        # N-audit-1106-347: the DOC DRIFT case's expected text is the degraded baseline's own failure line, so it read
        # FIRED whatever its mutation did. A mutant whose run prints exactly the baseline's output changed nothing.
        code, console = self.score_doc_drift(DEGRADED)
        self.assertNotRegex(console, r"synthetic doc drift\s+FIRED",
                            "hardening driver: a degraded baseline's own failure line is not a firing")
        self.assertRegex(console, r"synthetic doc drift\s+SURVIVED")
        self.assertEqual(code, 1)
        # WITNESS: a mutant that moved the documented count prints a line of its own, and that one fires.
        code, console = self.score_doc_drift(
            "PASS: an earlier invariant\n"
            "SMOKETEST.md's source-invariant count matches this file -- it says 7600, there are 601 failed.\n")
        self.assertRegex(console, r"synthetic doc drift\s+FIRED")
        self.assertEqual(code, 0)


if __name__ == "__main__":
    unittest.main()
