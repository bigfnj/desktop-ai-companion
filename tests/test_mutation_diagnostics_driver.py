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


# N-bd-w9-tests-harness-1-03. What powershell.exe printed for the uncaught Assert-True throw of one of this harness's own
# gate labels, captured 2026-11-08 in hidden consoles 84 and 60 columns wide (the "At" line names a synthetic script
# instead). The case's fragment straddles the word wrap of the message, and the FullyQualifiedErrorId record below it is
# no fallback: it breaks INSIDE a word and goes on with an indent, so no one physical line holds the fragment.
GATE_LABEL = "DiagnosticLog.Write filters through IsEnabled and keeps no second copy of the rules"
GATE_FRAGMENT = "keeps no second copy of the rules"
WRAPPED_84 = (
    "PASS: an earlier invariant\n"
    "DiagnosticLog.Write filters through IsEnabled and keeps no second copy of the \n"
    "rules failed.\n"
    "At C:\\synthetic\\runtime-hardening-selftest.ps1:44 char:28\n"
    "+     if (-not $Condition) { throw \"$Name failed.\" }\n"
    "+                            ~~~~~~~~~~~~~~~~~~~~~\n"
    "    + CategoryInfo          : OperationStopped: (DiagnosticLog.W...e rules failed. \n"
    "   :String) [], RuntimeException\n"
    "    + FullyQualifiedErrorId : DiagnosticLog.Write filters through IsEnabled and ke \n"
    "   eps no second copy of the rules failed.\n"
    " \n")
WRAPPED_60 = (
    "PASS: an earlier invariant\n"
    "DiagnosticLog.Write filters through IsEnabled and keeps no \n"
    "second copy of the rules failed.\n"
    "At C:\\synthetic\\runtime-hardening-selftest.ps1:44 char:28\n"
    "+     if (-not $Condition) { throw \"$Name failed.\" }\n"
    "+                            ~~~~~~~~~~~~~~~~~~~~~\n"
    "    + CategoryInfo          : OperationStopped: (Diagnosti \n"
    "   cLog.W...e rules failed.:String) [], RuntimeException\n"
    "    + FullyQualifiedErrorId : DiagnosticLog.Write filters  \n"
    "   through IsEnabled and keeps no second copy of the rule  \n"
    "  s failed.\n"
    " \n")


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
        # --check-patterns joined the accepted flags with N-audit-1106-344; combined with a selection it is refused.
        for argv in (["--shard=1/8"], ["--check-patterns", "--only=wpf"], ["--check-patterns", "--fast"], ["--olny=wpf"],
                     ["--on=wpf"], ["wpf"]):
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

    def test_check_patterns_counts_every_case_and_runs_nothing(self):
        # N-audit-1106-344: this harness had no pattern-only check, so a case guarding nothing showed only as NO-OP in a
        # whole run, which nobody runs. --check-patterns counts each case's pattern against its file, as the sibling's.
        with tempfile.TemporaryDirectory() as folder:
            Path(folder, "lf.cs").write_bytes(b"guard one\nguard two\n")
            Path(folder, "crlf.cs").write_bytes(b"line a\r\nline b\r\n")
            matching = [("synthetic once", "lf.cs", "guard one\n", "", "gate", "x"),
                        ("synthetic crlf", "crlf.cs", "line a\nline b\n", "", "wpf", "x")]
            moved = matching + [("synthetic moved", "lf.cs", "guard three\n", "", "gate", "x"),
                                ("synthetic twice", "lf.cs", "guard", "", "gate", "x")]
            for cases, want in ((moved, 1), (matching, 0)):
                output = io.StringIO()
                with patch.object(self.harness, "ROOT", folder), \
                     patch.object(self.harness, "CASES", cases), \
                     patch.object(self.harness.sys, "argv", ["mutate-diagnostics.py", "--check-patterns"]), \
                     patch.object(self.harness, "score", side_effect=AssertionError("no case may run")), \
                     patch.object(self.harness.os, "makedirs", side_effect=AssertionError("nothing may be created")), \
                     contextlib.redirect_stdout(output):
                    code = self.harness.main()
                self.assertEqual(code, want, "diagnostics driver: --check-patterns fails on a case not matching once")
                if want:
                    self.assertIn("NO-OP (pattern matched 0 times)  synthetic moved", output.getvalue())
                    self.assertIn("NO-OP (pattern matched 2 times)  synthetic twice", output.getvalue())
                # WITNESS: a CRLF target matches its LF pattern, as in a mutation run.
                self.assertNotIn("synthetic crlf", output.getvalue())
            # WITNESS: nothing was written to the targets.
            self.assertEqual(Path(folder, "lf.cs").read_bytes(), b"guard one\nguard two\n")

    def test_a_stalled_invariant_script_reads_broken(self):
        # N-audit-1106-395: run_gate was the one child with no timeout, so a stalled powershell.exe hung the run with the
        # mutant in place. The fake stands in for the stall: given a bound, it reaches it; given none, it would wait for
        # ever, which a test cannot, so it says so instead.
        calls = []

        def stalled(args, **kwargs):
            calls.append(kwargs.get("timeout"))
            if kwargs.get("timeout") is None:
                raise RuntimeError("the invariant script was started with no bound and would have hung the run")
            raise self.harness.subprocess.TimeoutExpired(args, kwargs["timeout"])

        with patch.object(self.harness, "run_text", side_effect=stalled):
            ok, text = self.harness.run_gate()
        self.assertEqual(calls, [self.harness.GATE_TIMEOUT])
        self.assertIsNone(ok, "diagnostics driver: a stalled invariant script is no verdict")
        self.assertIn("did not exit in %ds" % self.harness.GATE_TIMEOUT, text)
        # WITNESS: a script that exits is graded on its exit code, as before.
        with patch.object(self.harness, "run_text",
                          return_value=self.harness.subprocess.CompletedProcess([], 0, "PASS: x\n", "")):
            self.assertEqual(self.harness.run_gate(), (True, "PASS: x\n"))

    def score_gate(self, expect, mutant_output, baseline=(True, "PASS: an earlier invariant\n")):
        """One gate case: the unmutated run returns `baseline`, and the mutant's run fails printing `mutant_output`."""
        with tempfile.TemporaryDirectory() as folder:
            Path(folder, "Guarded.ps1").write_bytes(b"guarded\n")
            cases = [("synthetic wrapped gate case", "Guarded.ps1", "guarded", "unguarded", "gate", expect)]
            output = io.StringIO()
            # RUN_TEMP is the folder too: a case that is not FIRED keeps its output there (keep_output).
            with patch.object(self.harness, "ROOT", folder), \
                 patch.object(self.harness, "RUN_TEMP", folder), \
                 patch.object(self.harness, "CASES", cases), \
                 patch.object(self.harness, "run_gate", side_effect=[baseline, (False, mutant_output)]), \
                 patch.object(self.harness, "run_wpf", side_effect=AssertionError("no pane run")), \
                 patch.object(self.harness, "build_host", side_effect=AssertionError("no build")), \
                 contextlib.redirect_stdout(output):
                code = self.harness.score(self.harness.parse_arguments([]))
            # WITNESS: the target is restored whatever the verdict.
            self.assertEqual(Path(folder, "Guarded.ps1").read_bytes(), b"guarded\n")
        return code, output.getvalue()

    def test_a_word_wrapped_gate_failure_fires(self):
        # N-bd-w9-tests-harness-1-03: a gate case was matched one physical line at a time, so a fragment straddling
        # the console's word wrap of the thrown message read WRONG whatever the mutation did. Reproduced with the real
        # case "Write stops filtering at all" in an 84-column console: WRONG, "got: rules failed.".
        for width, rendering in (("84", WRAPPED_84), ("60", WRAPPED_60)):
            with self.subTest(width=width):
                code, console = self.score_gate(GATE_FRAGMENT, rendering)
                self.assertRegex(console, r"FIRED\s+synthetic wrapped gate case",
                                 "diagnostics driver: a gate fragment word-wrapped at %s columns is read whole" % width)
                self.assertIn(GATE_LABEL + " failed.", console)
                self.assertEqual(code, 0)
        # WITNESS: a line is joined to the next only where it ends in the space a word wrap leaves; a message broken
        # without one is two lines of its own, even when the second begins with a space, and the fragment read across
        # them does not fire.
        code, console = self.score_gate(GATE_FRAGMENT, WRAPPED_84.replace("copy of the \nrules", "copy of the\n rules"))
        self.assertRegex(console, r"WRONG\s+synthetic wrapped gate case")
        self.assertEqual(code, 1)

    def test_a_wrapped_doc_count_baseline_is_still_tolerated(self):
        # N-bd-w9-tests-harness-1-03, the baseline half: the doc-count label is word-wrapped in a narrow console too,
        # across the very words that tell the one tolerated red baseline from any other, which read line by line was
        # refused as not green and scored nothing.
        wrapped = ("PASS: an earlier invariant\n"
                   "SMOKETEST.md's source-invariant count \n"
                   "matches this file -- it says 600, there are 601 failed.\n")
        code, console = self.score_gate(GATE_FRAGMENT, WRAPPED_84, baseline=(False, wrapped))
        self.assertIn("tolerating a documented-count mismatch", console,
                      "diagnostics driver: a word-wrapped doc-count line still reads as the tolerated baseline")
        # ...and the tolerated line is printed as it was thrown, not as nothing.
        self.assertIn("SMOKETEST.md's source-invariant count matches this file -- it says 600", console)
        self.assertRegex(console, r"FIRED\s+synthetic wrapped gate case")
        self.assertEqual(code, 0)
        # WITNESS: any other red baseline is still refused, and its failure is listed whole rather than as its tail.
        code, console = self.score_gate(GATE_FRAGMENT, WRAPPED_84, baseline=(False, WRAPPED_60))
        self.assertIn("BASELINE NOT GREEN for 'gate'", console)
        self.assertIn("   " + GATE_LABEL + " failed.", console,
                      "diagnostics driver: a wrapped failure in a refused baseline is listed as it was thrown")
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
