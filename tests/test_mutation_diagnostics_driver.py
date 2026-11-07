"""mutate-diagnostics.py's driver on synthetic cases: what it accepts, and what it has printed when a run is cut short.

No product file is read or written, nothing is built, and no process is started: the gate run is a fake.
"""

import contextlib
import importlib.util
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


class DiagnosticsDriverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().with_name("mutate-diagnostics.py")
        spec = importlib.util.spec_from_file_location("diagnostics_driver_test", path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def test_an_unknown_argument_is_refused_before_anything_runs(self):
        # N-shimeji-tests-01: every one of these used to be ignored, and the whole mutating suite ran.
        for argv in (["--shard=1/8"], ["--check-patterns"], ["--olny=wpf"], ["--on=wpf"], ["wpf"]):
            with self.subTest(argv=argv), \
                 patch.object(self.harness.sys, "argv", ["mutate-diagnostics.py"] + argv), \
                 patch.object(self.harness, "score", side_effect=AssertionError("no case may run")), \
                 patch.object(self.harness.os, "makedirs", side_effect=AssertionError("nothing may be created")), \
                 contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as refused:
                    self.harness.main()
                self.assertEqual(refused.exception.code, 2, "diagnostics driver: an unknown argument is refused")
        # WITNESS: the two flags it does take still parse, in both spellings argparse gives them.
        args = self.harness.parse_arguments(["--fast", "--only=Wpf pane"])
        self.assertEqual((args.fast, args.only), (True, "Wpf pane"))
        self.assertEqual(self.harness.parse_arguments(["--only", "x"]).only, "x")
        self.assertEqual((self.harness.parse_arguments([]).fast, self.harness.parse_arguments([]).only), (False, ""))

    def test_a_verdict_is_on_the_console_before_the_run_is_cut_short(self):
        # N-audit-1106-346: the verdicts were printed only after the last case, so an interrupt in the second case
        # lost the first case's FIRED without a trace.
        with tempfile.TemporaryDirectory() as folder:
            Path(folder, "one.ps1").write_bytes(b"guard one\n")
            Path(folder, "two.ps1").write_bytes(b"guard two\n")
            cases = [("synthetic one", "one.ps1", "guard one", "nothing", "gate", "label one"),
                     ("synthetic two", "two.ps1", "guard two", "nothing", "gate", "label two")]
            runs = []

            def run_gate():
                runs.append(Path(folder, "one.ps1").read_bytes())
                if len(runs) == 1:
                    return True, "PASS: label one\nPASS: label two\n"
                if len(runs) == 2:
                    return False, "label one failed.\n"
                raise KeyboardInterrupt()

            output = io.StringIO()
            with patch.object(self.harness, "ROOT", folder), \
                 patch.object(self.harness, "CASES", cases), \
                 patch.object(self.harness, "run_gate", side_effect=run_gate), \
                 patch.object(self.harness, "run_wpf", side_effect=AssertionError("no pane run")), \
                 patch.object(self.harness, "build_host", side_effect=AssertionError("no build")), \
                 contextlib.redirect_stdout(output):
                with self.assertRaises(KeyboardInterrupt):
                    self.harness.score(self.harness.parse_arguments([]))
            # WITNESS: the sources are restored whatever happened.
            self.assertEqual(Path(folder, "one.ps1").read_bytes(), b"guard one\n")
            self.assertEqual(Path(folder, "two.ps1").read_bytes(), b"guard two\n")
        self.assertRegex(output.getvalue(), r"FIRED\s+synthetic one",
                         "diagnostics driver: a measured verdict is printed before a later case is interrupted")
        self.assertEqual(len(runs), 3, "the synthetic run reached the second case")


if __name__ == "__main__":
    unittest.main()
