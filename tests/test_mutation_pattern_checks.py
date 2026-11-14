"""Synthetic witnesses that the two pattern-staleness checks can FAIL (AgentBroker 1.10.5 marathon, lane T).

mutate-selftest-guards.py --check-patterns and run_agentbroker_hygiene_mutations.py --check run in the offline gate
(Invoke-OfflineScriptChecks.ps1) against the real harnesses, where a clean tree only ever shows them passing. These cases
feed each check sources it must refuse, on temporary files: nothing is built and no product file is read or written.
Run through tests/run_mutation_capture_checks.py.
"""

import contextlib
import importlib.util
import io
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

TESTS = Path(__file__).resolve().parent


def load(file_name, module_name):
    spec = importlib.util.spec_from_file_location(module_name, TESTS / file_name)
    module = importlib.util.module_from_spec(spec)
    # Registered before exec_module: on Python 3.11 dataclasses resolves the hygiene runner's postponed annotations
    # through sys.modules (X2's Step 0.6 note).
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class ScratchCase(unittest.TestCase):
    def setUp(self):
        self.scratch = Path(tempfile.mkdtemp(prefix="pattern-check-"))
        self.addCleanup(shutil.rmtree, self.scratch, True)

    def source(self, name, data):
        path = self.scratch / name
        path.write_bytes(data)
        return path


class GuardsPatternCheck(ScratchCase):
    @classmethod
    def setUpClass(cls):
        cls.guards = load("mutate-selftest-guards.py", "pattern_check_guards_under_test")

    def check(self, path, old):
        case = ("synthetic case", str(path), old, b"replaced\n", None, None, "--synthetic", "synthetic.txt", "never")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.guards.check_patterns([case])
        return code, out.getvalue()

    def test_GUARDS_PATTERN_ZERO_MATCH_FAILS(self):
        code, out = self.check(self.source("a.cs", b"alpha\n"), b"beta\n")
        self.assertEqual(code, 1)
        self.assertIn("NO-OP (pattern matched 0 times)", out)

    def test_GUARDS_PATTERN_TWO_MATCHES_FAILS(self):
        code, out = self.check(self.source("a.cs", b"same\nsame\n"), b"same\n")
        self.assertEqual(code, 1)
        self.assertIn("NO-OP (pattern matched 2 times)", out)

    def test_GUARDS_PATTERN_MISSING_FILE_FAILS(self):
        code, out = self.check(self.scratch / "absent.cs", b"alpha\n")
        self.assertEqual(code, 1)
        self.assertIn("NO FILE", out)

    def test_GUARDS_PATTERN_CRLF_FILE_LF_PATTERN_PASSES(self):
        # WITNESS: an LF pattern over a CRLF file is the case score() matches through line_ending_variant.
        code, out = self.check(self.source("a.cs", b"one\r\ntwo\r\n"), b"one\ntwo\n")
        self.assertEqual(code, 0, out)
        self.assertIn("1 cases, 0 not matching exactly once.", out)

    def test_GUARDS_PATTERN_ONLY_REFUSED(self):
        proc = subprocess.run([sys.executable, "-B", "-X", "utf8", str(TESTS / "mutate-selftest-guards.py"),
                               "--check-patterns", "--only=synthetic"], capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 2, proc.stdout + proc.stderr)
        self.assertIn("cannot be combined with --only", proc.stderr)


class HygieneAnchorCheck(ScratchCase):
    @classmethod
    def setUpClass(cls):
        cls.hygiene = load("run_agentbroker_hygiene_mutations.py", "pattern_check_hygiene_under_test")

    def check(self, path, replacements):
        witness = self.hygiene.Witness("synthetic-witness", str(path), "worker", ("--synthetic-only",),
                                       tuple(replacements), "SYNTHETIC_CHECK")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.hygiene.check_anchors([witness])
        return code, out.getvalue()

    def test_HYGIENE_CHECK_COUNT_MISMATCH_FAILS(self):
        over, out_over = self.check(self.source("a.cs", b"a\na\n"), [("a\n", "b\n", 1)])
        under, out_under = self.check(self.source("b.cs", b"a\n"), [("a\n", "b\n", 2)])
        self.assertEqual((over, under), (1, 1), out_over + out_under)
        self.assertIn("STALE synthetic-witness: ANCHOR_COUNT: expected 1, found 2", out_over)
        self.assertIn("STALE synthetic-witness: ANCHOR_COUNT: expected 2, found 1", out_under)

    def test_HYGIENE_CHECK_SEQUENTIAL_SECOND_ANCHOR(self):
        # The second anchor exists only after the first replacement: counted in order it passes...
        made, out_made = self.check(self.source("a.cs", b"alpha beta\n"), [("alpha", "gamma", 1), ("gamma beta", "delta", 1)])
        # ...and here the first replacement doubles the second anchor, which the original bytes hold once.
        doubled, out_doubled = self.check(self.source("b.cs", b"alpha beta\n"), [("alpha", "beta", 1), ("beta", "x", 1)])
        self.assertEqual(made, 0, out_made)
        self.assertEqual(doubled, 1, out_doubled)
        self.assertIn("expected 1, found 2", out_doubled)

    def test_HYGIENE_CHECK_CRLF(self):
        # WITNESS: an LF anchor over a CRLF file is the case replace_exact normalises.
        code, out = self.check(self.source("a.cs", b"one\r\ntwo\r\n"), [("one\ntwo\n", "x\n", 1)])
        self.assertEqual(code, 0, out)
        self.assertIn("1 witnesses, 0 stale", out)

    def test_HYGIENE_CHECK_MISSING_SOURCE_FAILS(self):
        code, out = self.check(self.scratch / "absent.cs", [("a\n", "b\n", 1)])
        self.assertEqual(code, 1)
        self.assertIn("NO_SOURCE synthetic-witness", out)

    def test_HYGIENE_CHECK_APPLY_REFUSED(self):
        proc = subprocess.run([sys.executable, "-B", "-X", "utf8", str(TESTS / "run_agentbroker_hygiene_mutations.py"),
                               "--check", "--apply"], capture_output=True, text=True, timeout=300)
        self.assertNotEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertIn("--check reads only", proc.stderr)


if __name__ == "__main__":
    unittest.main()
