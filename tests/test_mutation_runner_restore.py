"""The two native mutation runners put back every source they own, and plan only labelled variants.

python -X utf8 -m unittest test_mutation_runner_restore   (from tests/; run_mutation_capture_checks.py runs it too)

Synthetic files under a temporary folder only: no repository source is read or written, no build or checker is
started. N-codex-1102-02: the AgentBroker pin-diagnostic runner's restore stopped at the first source it could not read,
leaving every later source mutated, and the installer shutdown runner checked its sources for links only once, before
the first build. N-audit-1106-353: four installer variants carried no failure label, so any failing check of the
focused fake suite scored them as killed. The installer runner's own --self-test holds its cases; it is run here so that
something the gate calls runs it.
"""

import contextlib
import io
from pathlib import Path
import tempfile
import unittest

import run_agentbroker_pin_diagnostic_mutations as pin
import run_installer_shutdown_mutations as shutdown


class PinDiagnosticRunnerRestore(unittest.TestCase):
    def test_an_unreadable_earlier_source_does_not_stop_the_restore_of_a_later_one(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch) / "repo"
            evidence = Path(scratch) / "evidence"
            evidence.mkdir()
            sources = {"earlier": "src/Earlier.cs", "later": "src/Later.cs"}
            for relative in sources.values():
                (root / relative).parent.mkdir(parents=True, exist_ok=True)
                (root / relative).write_bytes(b"synthetic original " + relative.encode())
            saved = (pin.ROOT, pin.SOURCES)
            pin.ROOT, pin.SOURCES = root, sources
            try:
                runner = pin.Runner(evidence)
                # The later source holds this run's own mutation; the earlier one has gone (an OSError on read).
                runner.owned_current["src/Later.cs"] = b"synthetic owned mutation"
                (root / "src/Later.cs").write_bytes(b"synthetic owned mutation")
                (root / "src/Earlier.cs").unlink()
                try:
                    runner.restore()
                    error = None
                except Exception as caught:  # the type is part of what is asserted below
                    error = caught
            finally:
                pin.ROOT, pin.SOURCES = saved
            self.assertEqual((root / "src/Later.cs").read_bytes(), b"synthetic original src/Later.cs",
                             "pin runner: a later owned mutation is restored although an earlier source cannot be read")
            self.assertFalse((root / "src/Earlier.cs").exists(), "pin runner: the missing source is not recreated")
            self.assertIsInstance(error, RuntimeError, "pin runner: the restore refuses once, as a RuntimeError")
            self.assertIn("src/Earlier.cs (unreadable", str(error), "pin runner: the refusal names the unreadable source")
            self.assertNotIn("src/Later.cs", str(error), "pin runner: the restored source is not named")


class InstallerShutdownRunnerSelfTest(unittest.TestCase):
    output = ""
    code = None

    @classmethod
    def setUpClass(cls):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            cls.code = shutdown.self_test()
        cls.output = captured.getvalue()

    def assert_case(self, case):
        self.assertIn("PASS " + case + "\n", self.output,
                      "installer shutdown runner --self-test case " + case + " did not pass:\n" + self.output)

    def test_every_installer_variant_names_the_check_it_must_fail(self):
        self.assert_case("every-variant-names-its-label")

    def test_the_installer_restore_refuses_a_source_that_became_a_link(self):
        self.assert_case("linked-earlier-source")

    def test_the_installer_restore_keeps_going_past_an_unreadable_earlier_source(self):
        self.assert_case("missing-earlier-source")

    def test_the_installer_runner_self_test_passes_whole(self):
        self.assertEqual(self.code, 0, "installer shutdown runner --self-test exit code:\n" + self.output)


if __name__ == "__main__":
    unittest.main()
