"""Capture infrastructure failures cannot become GUI mutation verdicts."""

import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import mutation_process
from mutation_process import CaptureError


TESTS = Path(__file__).resolve().parent


def load(name):
    specification = importlib.util.spec_from_file_location("capture_" + name.replace("-", "_"), TESTS / (name + ".py"))
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


class MutationCaptureHarnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.guards = load("mutate-selftest-guards")
        cls.flow = load("mutate-agentflow")
        cls.diagnostics = load("mutate-diagnostics")

    def test_guard_capture_failure_cannot_grade_a_previous_marker(self):
        for reason in ("aggregate capture limit", "direct child cleanup incomplete", "capture close failed"):
            with self.subTest(reason=reason), tempfile.TemporaryDirectory(prefix="capture-harness-", dir=TESTS) as folder:
                marker = Path(folder) / "synthetic-marker.txt"
                marker.write_text("RESULT=PASS\n", encoding="utf-8")
                with (patch.object(self.guards, "RUN_TEMP", folder),
                      patch.object(self.guards, "run_bytes", side_effect=CaptureError(reason)) as capture,
                      patch.object(self.guards, "read_utf8_marker") as read,
                      patch("mutation_process.subprocess.Popen", side_effect=AssertionError("no product process"))):
                    with self.assertRaises(CaptureError):
                        self.guards.selftest("--module-selftest=aibrain", marker.name)
                    capture.assert_called_once()
                    read.assert_not_called()
                self.assertFalse(marker.exists())

    def test_flow_capture_failure_cannot_grade_a_previous_marker(self):
        with tempfile.TemporaryDirectory(prefix="capture-harness-", dir=TESTS) as folder:
            marker = Path(folder) / "synthetic-marker.txt"
            marker.write_text("RESULT=PASS\n", encoding="utf-8")
            with (patch.object(self.flow, "MARKER", str(marker)),
                  patch.object(self.flow, "run_bytes", side_effect=CaptureError("capture incomplete")) as capture,
                  patch.object(self.flow, "read") as read,
                  patch("mutation_process.subprocess.Popen", side_effect=AssertionError("no product process"))):
                with self.assertRaises(CaptureError):
                    self.flow.run_selftest()
                capture.assert_called_once()
                read.assert_not_called()
            self.assertFalse(marker.exists())

    def test_diagnostics_capture_failure_cannot_grade_a_previous_marker(self):
        with tempfile.TemporaryDirectory(prefix="capture-harness-", dir=TESTS) as folder:
            marker = Path(folder) / "synthetic-marker.txt"
            marker.write_text("RESULT=PASS\n", encoding="utf-8")
            with (patch.object(self.diagnostics, "WPF_RESULT", str(marker)),
                  patch.object(self.diagnostics, "build_host", return_value=(True, "")) as build,
                  patch.object(self.diagnostics, "run_bytes", side_effect=CaptureError("capture incomplete")) as capture,
                  patch.object(self.diagnostics, "read_utf8_marker") as read,
                  patch("mutation_process.subprocess.Popen", side_effect=AssertionError("no product process"))):
                with self.assertRaises(CaptureError):
                    self.diagnostics.run_wpf(False)
                build.assert_called_once()
                capture.assert_called_once()
                read.assert_not_called()
            self.assertFalse(marker.exists())


class MutationCaptureLifecycleFaultTests(unittest.TestCase):
    def test_unreapable_owned_child_is_infrastructure_failure_with_one_bounded_attempt(self):
        class Unreapable:
            returncode = None

            def __init__(self):
                self.waits = []
                self.kills = 0

            def wait(self, timeout):
                self.waits.append(timeout)
                raise subprocess.TimeoutExpired(["synthetic-owned"], timeout)

            def kill(self):
                self.kills += 1

        child = Unreapable()
        with tempfile.TemporaryDirectory(prefix="capture-fault-", dir=TESTS) as folder:
            with (patch.object(mutation_process, "_CAPTURE_DIRECTORY", Path(folder)),
                  patch.object(mutation_process.subprocess, "Popen", return_value=child)):
                with self.assertRaisesRegex(CaptureError, "could not reap"):
                    mutation_process.run_bytes(["synthetic-owned"], timeout=0.2)
            self.assertEqual(child.waits, [0.2, mutation_process._REAP_TIMEOUT])
            self.assertEqual(child.kills, 1)
            self.assertEqual(list(Path(folder).iterdir()), [])

    def test_interrupt_after_launch_kills_and_boundedly_reaps_only_the_owned_child(self):
        interrupted = KeyboardInterrupt("synthetic cancellation")

        class Interrupted:
            returncode = None

            def __init__(self):
                self.waits = []
                self.kills = 0

            def wait(self, timeout):
                self.waits.append(timeout)
                if len(self.waits) == 1:
                    raise interrupted
                self.returncode = 1
                return self.returncode

            def kill(self):
                self.kills += 1

        child = Interrupted()
        with tempfile.TemporaryDirectory(prefix="capture-fault-", dir=TESTS) as folder:
            with (patch.object(mutation_process, "_CAPTURE_DIRECTORY", Path(folder)),
                  patch.object(mutation_process.subprocess, "Popen", return_value=child)):
                with self.assertRaises(KeyboardInterrupt) as failure:
                    mutation_process.run_bytes(["synthetic-owned"], timeout=0.2)
            self.assertIs(failure.exception, interrupted)
            self.assertEqual(child.waits, [0.2, mutation_process._REAP_TIMEOUT])
            self.assertEqual(child.kills, 1)
            self.assertEqual(list(Path(folder).iterdir()), [])

    def test_interrupt_during_kill_is_not_swallowed_and_still_gets_a_bounded_reap(self):
        interrupted = KeyboardInterrupt("synthetic second cancellation")

        class InterruptedKill:
            returncode = None

            def __init__(self):
                self.waits = []

            def wait(self, timeout):
                self.waits.append(timeout)
                if len(self.waits) == 1:
                    raise subprocess.TimeoutExpired(["synthetic-owned"], timeout)
                self.returncode = 1
                return self.returncode

            def kill(self):
                raise interrupted

        child = InterruptedKill()
        with tempfile.TemporaryDirectory(prefix="capture-fault-", dir=TESTS) as folder:
            with (patch.object(mutation_process, "_CAPTURE_DIRECTORY", Path(folder)),
                  patch.object(mutation_process.subprocess, "Popen", return_value=child)):
                with self.assertRaises(KeyboardInterrupt) as failure:
                    mutation_process.run_bytes(["synthetic-owned"], timeout=0.2)
            self.assertIs(failure.exception, interrupted)
            self.assertEqual(child.waits, [0.2, mutation_process._REAP_TIMEOUT])
            self.assertEqual(list(Path(folder).iterdir()), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
