"""What a mutation harness leaves behind when a run is killed, how long a pseudo-flag may run, and how a firing is read.

python -X utf8 -m unittest test_mutation_harness_lifecycle   (from tests/; run_mutation_capture_checks.py runs it too)

N-audit-1106-345 / N-audit-1106-350: a hard kill mid-case left the mutant in the tree with its original bytes only in
the dead process's memory; the restore record (mutation_journal.py) is on disk before the mutant, and the next run puts
it back. N-audit-1106-348: the pseudo-flags kept a flat timeout and were never timed. N-audit-1106-349: a timed-out
runner script was killed and the host it started kept running. N-bd-w4-agentflow-1-03: every AgentFlow firing read
"(+1 other failures)", the host's restatement of the module's verdict.

Synthetic files under a temporary folder only: no product file is read or written and nothing is built. The process
check starts one synthetic child of this interpreter and stops it by its process ID alone.
"""

import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import types
import unittest
from unittest.mock import patch

import mutation_journal
from mutation_journal import JournalError, RestoreJournal


TESTS = Path(__file__).resolve().parent


def load(name):
    spec = importlib.util.spec_from_file_location("lifecycle_" + name.replace("-", "_"), TESTS / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class SimulatedKill(BaseException):
    """Stands for the process dying: raised where the original bytes would have been written back."""


class RestoreRecordTests(unittest.TestCase):
    def test_a_record_puts_back_the_mutant_a_dead_run_left(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder, "Source.cs")
            target.write_bytes(b"original\r\n")
            journal = RestoreJournal(os.path.join(folder, "h.restore-journal.json"))
            journal.record(str(target), b"original\r\n", b"mutant\r\n", "synthetic case")
            target.write_bytes(b"mutant\r\n")
            # The run dies here: no clear(). A fresh object stands for the next run.
            lines, repaired = RestoreJournal(journal.path).repair()
            self.assertEqual(target.read_bytes(), b"original\r\n", "restore record: the mutant is replaced by the original bytes")
            self.assertEqual(repaired, str(target))
            self.assertIn("REPAIRED: " + str(target), lines[0])
            self.assertFalse(os.path.exists(journal.path), "restore record: removed once the file is repaired")
            # WITNESS: with no record there is nothing to do.
            self.assertEqual(RestoreJournal(journal.path).repair(), ([], None))

    def test_a_file_matching_neither_is_refused_and_the_record_kept(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder, "Source.cs")
            journal = RestoreJournal(os.path.join(folder, "h.restore-journal.json"))
            journal.record(str(target), b"original", b"mutant", "synthetic case")
            target.write_bytes(b"edited by hand since")
            with self.assertRaises(JournalError) as refused:
                RestoreJournal(journal.path).repair()
            self.assertIn("matches neither", str(refused.exception))
            self.assertEqual(target.read_bytes(), b"edited by hand since", "restore record: a third state is never overwritten")
            self.assertTrue(os.path.exists(journal.path))
            # WITNESS: a file already holding its original only loses the record.
            target.write_bytes(b"original")
            lines, repaired = RestoreJournal(journal.path).repair()
            self.assertIsNone(repaired)
            self.assertIn("already holds its original bytes", lines[0])
            self.assertFalse(os.path.exists(journal.path))

    def test_a_record_another_run_wrote_is_neither_overwritten_nor_cleared(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "h.restore-journal.json")
            RestoreJournal(path).record(os.path.join(folder, "a.cs"), b"a", b"A", "the other run's case")
            second = RestoreJournal(path)
            with self.assertRaises(JournalError):
                second.record(os.path.join(folder, "b.cs"), b"b", b"B", "this run's case")
            second.clear()
            self.assertTrue(os.path.exists(path), "restore record: clear() removes only a record this run wrote")

    def test_a_repaired_file_this_process_imported_is_named(self):
        self.assertTrue(mutation_journal.repaired_loaded_module(mutation_journal.__file__, sys.modules))
        self.assertFalse(mutation_journal.repaired_loaded_module(os.path.join(str(TESTS), "not-imported.py"), sys.modules))
        self.assertFalse(mutation_journal.repaired_loaded_module(None, sys.modules))


class GuardHarnessLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = load("mutate-selftest-guards")

    def test_guards_a_hard_kill_mid_case_is_repaired_by_the_next_run(self):
        harness = self.harness
        with tempfile.TemporaryDirectory() as folder:
            target = os.path.join(folder, "Source.cs")
            Path(target).write_bytes(b"clean guard\n")
            journal = RestoreJournal(os.path.join(folder, "guards.restore-journal.json"))
            case = ("synthetic kill", target, b"clean guard", b"mutant guard", None, None, "--synthetic-selftest", "m.txt",
                    "unused")
            real_write = harness.write

            def write(path, data):
                if data == b"clean guard\n":
                    raise SimulatedKill()
                real_write(path, data)

            with patch.object(harness, "RUN_TEMP", folder), \
                 patch.object(harness, "JOURNAL", journal), \
                 patch.object(harness, "DOCUMENTS_REMEMBRANCE", None), \
                 patch.object(harness, "documents_watch_selfcheck", return_value=[]), \
                 patch.object(harness, "build_all", return_value=(True, "")), \
                 patch.object(harness, "watched_selftest", return_value=("PASS: x\nRESULT=PASS\n", 0, None)), \
                 patch.object(harness, "write", side_effect=write), \
                 patch.object(harness.time, "sleep"), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SimulatedKill):
                    harness.score(types.SimpleNamespace(cases=[case], indices=[1]))
            self.assertEqual(Path(target).read_bytes(), b"mutant guard\n", "the synthetic kill left the mutant in place")
            # The next run: main() builds its journal and repairs before anything is planned or built.
            output = io.StringIO()
            with patch.object(harness, "JOURNAL", RestoreJournal(journal.path)), contextlib.redirect_stdout(output):
                stopped = harness.repair_stranded_mutant()
            self.assertEqual(Path(target).read_bytes(), b"clean guard\n",
                             "guard harness: a hard kill mid-case leaves a record the next run repairs the mutant from")
            self.assertIsNone(stopped)
            self.assertIn("REPAIRED", output.getvalue())

    def test_guards_main_repairs_before_it_scores(self):
        harness = self.harness
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder, "Source.cs")
            target.write_bytes(b"mutant left behind")
            journal_file = os.path.join(folder, "guards.restore-journal.json")
            RestoreJournal(journal_file).record(str(target), b"original", b"mutant left behind", "synthetic case")
            seen = []
            plan = types.SimpleNamespace(summary=lambda: "synthetic", json=lambda: "{}")
            with patch.object(harness, "journal_path", return_value=journal_file), \
                 patch.object(harness, "JOURNAL", None), \
                 patch.object(harness, "RUN_TEMP", None), \
                 patch.object(harness, "CHILD_ENV", None), \
                 patch.object(harness, "documents_folder", return_value=None), \
                 patch.object(harness, "plan_arguments", return_value=plan), \
                 patch.object(harness, "score", side_effect=lambda p: seen.append(target.read_bytes()) or 0), \
                 patch.object(harness, "TEMP", folder), \
                 patch.object(harness.sys, "argv", ["mutate-selftest-guards.py", "--only=synthetic"]), \
                 contextlib.redirect_stdout(io.StringIO()):
                code = harness.main()
            self.assertEqual(seen, [b"original"], "guard harness: main() repairs a stranded mutant before it scores")
            self.assertEqual(code, 0)

    def test_a_pseudo_flag_takes_the_mutant_ceiling(self):
        harness = self.harness
        bounds = []

        def run_text(args, **kwargs):
            bounds.append(kwargs.get("timeout"))
            return subprocess.CompletedProcess(args, 0, "PASS: x\nRESULT=PASS\n", "")

        caps = {harness.MUTATION_UNITTESTS: 600, harness.WINDOWSOAK_JUDGE: 300}
        for flag in (harness.CORETESTS, harness.SHIMEJI, harness.SECURITY, harness.TEMPLATE, harness.MODULE_SELFTESTS,
                     harness.MUTATION_UNITTESTS, harness.WINDOWSOAK_JUDGE):
            with self.subTest(flag=flag):
                del bounds[:]
                timings = {}
                with patch.object(harness, "run_text", side_effect=run_text), \
                     patch.object(harness, "BASELINE_SECONDS", timings), \
                     patch.object(harness, "CHILD_ENV", {}), \
                     patch.object(harness, "CORETESTS_EXE", __file__), \
                     patch.object(harness, "SHIMEJI_EXE", __file__), \
                     patch.object(harness, "WINDOWSOAK_EXE", __file__):
                    first = harness.selftest(flag, None)
                    self.assertIn(flag, timings, "pseudo-flag ceiling: the first run of %s is timed" % flag)
                    timings[flag] = 10.0
                    second = harness.selftest(flag, None)
                cap = caps.get(flag, harness.BACKSTOP_SECONDS)
                self.assertEqual(bounds, [min(cap, harness.BACKSTOP_SECONDS), min(cap, harness.MUTANT_CEILING_FLOOR_SECONDS)],
                                 "pseudo-flag ceiling: a later run of %s is bounded by its timed run" % flag)
                # WITNESS: both runs were graded, so the bound was read from a real call.
                self.assertEqual((first[1], second[1]), (0, 0))

    def test_a_timed_out_runner_stops_the_host_it_left_running(self):
        harness = self.harness
        sweeps = []
        left = (4242, os.path.join(harness.BIN, "DesktopAICompanion.exe"))

        def processes_matching(match, stop=False):
            sweeps.append(stop)
            return [left] if match(*left) else []

        def timed_out(args, **kwargs):
            raise subprocess.TimeoutExpired(args, kwargs["timeout"])

        with tempfile.TemporaryDirectory() as folder, \
             patch.object(harness, "RUN_TEMP", folder), \
             patch.object(harness, "CHILD_ENV", {}), \
             patch.object(harness, "BASELINE_SECONDS", {harness.MODULE_SELFTESTS: 20.0}), \
             patch.object(harness, "processes_matching", side_effect=processes_matching), \
             patch.object(harness, "run_text", side_effect=timed_out):
            report, why = harness.selftest(harness.MODULE_SELFTESTS, None)
            self.assertIsNone(report)
            self.assertEqual(sweeps, [True], "runner timeout: the sweep stops what it finds")
            self.assertIn("pid 4242", why, "runner timeout: a timed-out MODULE-SELFTESTS run stops the host exe it left running")
            # The bound is named; its value is test_a_pseudo_flag_takes_the_mutant_ceiling's to check.
            self.assertRegex(why, r"did not exit in \d+s \(killed; its unmutated run took 20s\)")
            # What the sweep matches: this tree's exe and anything under this run's TEMP, never the same name elsewhere.
            self.assertTrue(harness.is_run_host(1, harness.EXE))
            self.assertTrue(harness.is_run_host(1, os.path.join(folder, "dp-template-source-x", "loader", "DesktopAICompanion.exe")))
            self.assertFalse(harness.is_run_host(1, os.path.join(os.path.dirname(folder), "elsewhere", "DesktopAICompanion.exe")))
        # WITNESS: a run that exits sweeps nothing.
        del sweeps[:]
        with patch.object(harness, "CHILD_ENV", {}), \
             patch.object(harness, "BASELINE_SECONDS", {}), \
             patch.object(harness, "processes_matching", side_effect=processes_matching), \
             patch.object(harness, "run_text", return_value=subprocess.CompletedProcess([], 0, "RESULT=PASS\n", "")):
            harness.selftest(harness.MODULE_SELFTESTS, None)
        self.assertEqual(sweeps, [])

    def test_the_process_sweep_finds_and_stops_a_process_by_its_id(self):
        # A synthetic child of this interpreter, stopped through processes_matching by its pid alone: no other process
        # sharing the interpreter's image can match.
        child = subprocess.Popen([getattr(sys, "_base_executable", sys.executable), "-c", "import time; time.sleep(60)"])
        try:
            only = lambda pid, image: pid == child.pid
            found = self.harness.processes_matching(only)
            self.assertEqual(len(found), 1, "process sweep: the enumeration finds the synthetic child")
            self.assertTrue(found[0][1].lower().endswith(".exe"))
            self.assertIsNone(child.poll(), "process sweep: a lookup without stop leaves the child running")
            stopped = self.harness.processes_matching(only, stop=True)
            self.assertEqual(stopped, found)
            self.assertIsNotNone(child.wait(timeout=10), "process sweep: stop=True ends the child")
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=10)


class DiagnosticsHarnessLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = load("mutate-diagnostics")

    def test_diagnostics_a_hard_kill_mid_case_is_repaired_by_the_next_run(self):
        harness = self.harness
        with tempfile.TemporaryDirectory() as folder:
            Path(folder, "one.ps1").write_bytes(b"guard one\n")
            journal = RestoreJournal(os.path.join(folder, "diagnostics.restore-journal.json"))
            cases = [("synthetic kill", "one.ps1", "guard one", "nothing", "gate", "label one")]
            real_write = harness.write_bytes

            def write_bytes(path, data):
                if data == b"guard one\n":
                    raise SimulatedKill()
                real_write(path, data)

            with patch.object(harness, "ROOT", folder), \
                 patch.object(harness, "CASES", cases), \
                 patch.object(harness, "JOURNAL", journal), \
                 patch.object(harness, "run_gate", return_value=(True, "PASS: label one\n")), \
                 patch.object(harness, "write_bytes", side_effect=write_bytes), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SimulatedKill):
                    harness.score(harness.parse_arguments([]))
            self.assertEqual(Path(folder, "one.ps1").read_bytes(), b"nothing\n", "the synthetic kill left the mutant in place")
            # The next run's main(), up to score().
            seen = []
            with patch.object(harness, "journal_path", return_value=journal.path), \
                 patch.object(harness, "JOURNAL", None), \
                 patch.object(harness, "RUN_TEMP", None), \
                 patch.object(harness, "WPF_RESULT", None), \
                 patch.object(harness, "CHILD_ENV", None), \
                 patch.object(harness, "score", side_effect=lambda args: seen.append(Path(folder, "one.ps1").read_bytes()) or 0), \
                 patch.object(harness.sys, "argv", ["mutate-diagnostics.py", "--only=synthetic"]), \
                 patch.dict(harness.os.environ, {"TEMP": folder}), \
                 contextlib.redirect_stdout(io.StringIO()):
                code = harness.main()
            self.assertEqual(seen, [b"guard one\n"],
                             "diagnostics harness: a hard kill mid-case leaves a record the next run repairs the mutant from")
            self.assertEqual(code, 0)


class AgentFlowFiringCountTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = load("mutate-agentflow")

    def test_the_hosts_restatement_is_not_another_failure(self):
        report = ("  [agentflow] PASS: WITNESS something ran\n"
                  "  [agentflow] FAIL: the label the case breaks\n"
                  "  [agentflow] RESULT=FAIL\n"
                  "FAIL: the module's own self-test passed\n"
                  "RESULT=FAIL\n")
        lines = self.harness.failure_lines(report)
        hit = [line for line in lines if "the label the case breaks" in line]
        self.assertEqual(self.harness.other_failures(lines, hit), [],
                         "agentflow count: the host's restatement of the module verdict is not another failure")
        # WITNESS: a second module failure still counts, and the restatement is still a failure line.
        second = report.replace("  [agentflow] RESULT=FAIL\n", "  [agentflow] FAIL: a second label\n  [agentflow] RESULT=FAIL\n")
        lines = self.harness.failure_lines(second)
        self.assertEqual(self.harness.other_failures(lines, [l for l in lines if "the label the case breaks" in l]),
                         ["[agentflow] FAIL: a second label"])
        self.assertIn("FAIL: the module's own self-test passed", lines)


if __name__ == "__main__":
    unittest.main()
