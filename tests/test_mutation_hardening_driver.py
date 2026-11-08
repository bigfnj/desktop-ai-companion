"""mutate-hardening-guards.py's scoring on synthetic cases: what a degraded baseline may and may not count as FIRED, and
how a thrown message the console word-wrapped is read.

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

# N-bd-w8-studio-1-02. What powershell.exe printed for one uncaught Assert-True throw of a synthetic 120-character label,
# captured 2026-11-08 in hidden consoles 96 and 60 columns wide (the "At" line names a synthetic script instead). Each
# word-wrapped line keeps the space it broke at; the "+ ..." record lines wrap with an indent of their own.
WRAPPED_LABEL = ("synthetic: a label long enough that Windows PowerShell wraps its thrown message at the console width it "
                 "inherited")
WRAPPED_96 = (
    "PASS: an earlier invariant\n"
    "synthetic: a label long enough that Windows PowerShell wraps its thrown message at the console \n"
    "width it inherited (found 3) failed.\n"
    "At C:\\synthetic\\runtime-hardening-selftest.ps1:44 char:28\n"
    "+     if (-not $Condition) { throw \"$Name failed.\" }\n"
    "+                            ~~~~~~~~~~~~~~~~~~~~~\n"
    "    + CategoryInfo          : OperationStopped: (synthetic: a la...ound 3) failed.:String) [], \n"
    "    RuntimeException\n"
    "    + FullyQualifiedErrorId : synthetic: a label long enough that Windows PowerShell wraps its \n"
    "    thrown message at the console width it inherited (found 3) failed.\n"
    " \n")
WRAPPED_60 = (
    "PASS: an earlier invariant\n"
    "synthetic: a label long enough that Windows PowerShell \n"
    "wraps its thrown message at the console width it inherited \n"
    "(found 3) failed.\n"
    "At C:\\synthetic\\runtime-hardening-selftest.ps1:44 char:28\n"
    "+     if (-not $Condition) { throw \"$Name failed.\" }\n"
    "+                            ~~~~~~~~~~~~~~~~~~~~~\n"
    "    + CategoryInfo          : OperationStopped: (synthetic \n"
    "   : a la...ound 3) failed.:String) [], RuntimeException\n"
    "    + FullyQualifiedErrorId : synthetic: a label long enou \n"
    "   gh that Windows PowerShell wraps its thrown message at  \n"
    "   the console width it inherited (found 3) failed.\n"
    " \n")


class HardeningDriverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().with_name("mutate-hardening-guards.py")
        spec = importlib.util.spec_from_file_location("hardening_driver_test", path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def score_doc_drift(self, mutant_output, baseline=DEGRADED):
        """One DOC DRIFT-shaped case against a degraded baseline; the mutant's run prints `mutant_output`."""
        with tempfile.TemporaryDirectory() as folder:
            doc = Path(folder, "SMOKETEST.md")
            doc.write_bytes(b"600 source invariants\n")
            case = ("synthetic doc drift", str(doc), b"600 source invariants", b"7600 source invariants",
                    self.harness.DOC_COUNT_DRIFT)
            runs = [(1, baseline), (1, mutant_output)]
            console = io.StringIO()
            with patch.object(self.harness, "CASES", (case,)), \
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

    def test_a_wrapped_degraded_baseline_line_is_still_its_own(self):
        # N-bd-w8-studio-1-02, the baseline half: the doc-count line is word-wrapped in a narrow console too, across the
        # very words that tell a degraded baseline from a red one. It is still recognised, and still excluded as the
        # baseline's own line, only because the baseline is read through the same unwrapped_lines as the mutant.
        wrapped = ("PASS: an earlier invariant\n"
                   "SMOKETEST.md's source-invariant count \n"
                   "matches this file -- it says 600, there are 601 failed.\n")
        code, console = self.score_doc_drift(wrapped, baseline=wrapped)
        self.assertIn("BASELINE DEGRADED", console,
                      "hardening driver: a word-wrapped doc-count line still reads as the tolerated degraded baseline")
        self.assertRegex(console, r"synthetic doc drift\s+SURVIVED",
                         "hardening driver: a word-wrapped degraded baseline's own line is not a firing")
        self.assertEqual(code, 1)
        # WITNESS: a mutant that moved the documented count prints a wrapped line of its own, and that one fires.
        code, console = self.score_doc_drift(wrapped.replace("it says 600", "it says 7600"), baseline=wrapped)
        self.assertRegex(console, r"synthetic doc drift\s+FIRED")
        self.assertEqual(code, 0)

    def score_one(self, expect, mutant_output):
        """One case against a green baseline; the mutant's run exits 1 and prints `mutant_output`."""
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder, "Guarded.ps1")
            target.write_bytes(b"guarded\n")
            case = ("synthetic wrapped label", str(target), b"guarded", b"unguarded", expect)
            runs = [(0, "PASS: an earlier invariant\n"), (1, mutant_output)]
            console = io.StringIO()
            with patch.object(self.harness, "CASES", (case,)), \
                 patch.object(self.harness, "run", side_effect=runs), \
                 patch.object(self.harness.sys, "argv", ["mutate-hardening-guards.py"]), \
                 contextlib.redirect_stdout(console):
                code = self.harness.main()
            # WITNESS: the target is restored whatever the verdict.
            self.assertEqual(target.read_bytes(), b"guarded\n")
        return code, console.getvalue()

    def test_a_word_wrapped_failure_line_fires(self):
        # N-bd-w8-studio-1-02: powershell.exe word-wraps a thrown message at the width of the console it inherited, so
        # a case whose expected text was longer than one console line, or straddled a break, never appeared on one
        # output line and read WRONG whatever its mutation did.
        for width, rendering in (("96", WRAPPED_96), ("60", WRAPPED_60)):
            with self.subTest(width=width):
                code, console = self.score_one(WRAPPED_LABEL, rendering)
                self.assertRegex(console, r"synthetic wrapped label\s+FIRED",
                                 "hardening driver: a label word-wrapped at %s columns is read whole" % width)
                self.assertIn(WRAPPED_LABEL + " (found 3) failed.", console)
                self.assertEqual(code, 0)
        # A SHORT fragment that straddles a break: the length of the expected text was never the whole problem.
        code, console = self.score_one("at the console width it inherited", WRAPPED_96)
        self.assertRegex(console, r"synthetic wrapped label\s+FIRED")
        # WITNESS: two lines are joined only where the first ends in the space a word wrap leaves; a line that ends
        # without one is a line of its own, even when the next begins with a space, and the label read across the two
        # does not fire.
        code, console = self.score_one(WRAPPED_LABEL, WRAPPED_96.replace("at the console \n", "at the console\n "))
        self.assertRegex(console, r"synthetic wrapped label\s+WRONG")
        self.assertEqual(code, 1)

    def test_a_pass_line_is_never_joined_to_a_failure(self):
        # WITNESS for unwrapped_lines' two PASS guards. A failure line ending in a space must not take in a PASS line
        # that names the label: the case would read FIRED on an assertion that passed.
        code, console = self.score_one(
            WRAPPED_LABEL,
            "some other assertion failed, and its rendering ends in a space \n"
            "PASS: " + WRAPPED_LABEL + " (found 3)\n")
        self.assertNotRegex(console, r"synthetic wrapped label\s+FIRED",
                            "hardening driver: a PASS line never lends its text to a failure line")
        self.assertEqual(code, 1)
        # ...and a PASS line ending in a space must not swallow the failure after it, which would hide a real firing.
        code, console = self.score_one(WRAPPED_LABEL, "PASS: an assertion whose label ends in a space \n"
                                                      + WRAPPED_96.split("\n", 1)[1])
        self.assertRegex(console, r"synthetic wrapped label\s+FIRED",
                         "hardening driver: a failure after a PASS line ending in a space is read on its own")


if __name__ == "__main__":
    unittest.main()
