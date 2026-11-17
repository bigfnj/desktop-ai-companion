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

    @staticmethod
    def case(name, path, old):
        return (name, str(path), old, b"replaced\n", None, None, "--synthetic", "synthetic.txt", "never")

    def check_cases(self, cases):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.guards.check_patterns(cases)
        return code, out.getvalue()

    def check(self, path, old):
        return self.check_cases([self.case("synthetic case", path, old)])

    def test_GUARDS_PATTERN_SECOND_ENTRY_STALE_FAILS(self):
        # Round 2, T2-14: a healthy first case must not stand for the rest.
        healthy = self.source("a.cs", b"alpha\n")
        stale = self.source("b.cs", b"beta\n")
        code, out = self.check_cases([self.case("healthy first", healthy, b"alpha\n"), self.case("stale second", stale, b"gamma\n")])
        self.assertEqual(code, 1, out)
        self.assertIn("stale second", out)
        self.assertNotIn("healthy first", out)

    def test_GUARDS_PATTERN_DUPLICATE_ID_FAILS(self):
        # Round 2, T2-4: the case-ID rule make_plan applies to every run. Round 3: make_plan compares IDs ignoring case
        # (casefold), so two IDs that differ only in case are duplicates here too.
        path = self.source("a.cs", b"alpha\nbeta\n")
        code, out = self.check_cases([self.case("same id", path, b"alpha\n"), self.case("Same ID", path, b"beta\n")])
        self.assertEqual(code, 1, out)
        self.assertIn("DUPLICATE CASE ID  Same ID", out)
        self.assertNotIn("BAD CASE ID", out)

    def test_GUARDS_PATTERN_BLANK_ID_FAILS(self):
        # Round 3, T3-2: a blank and a whitespace-only ID are each refused on their own branch, not as duplicates of each other.
        path = self.source("a.cs", b"alpha\nbeta\n")
        code, out = self.check_cases([self.case("", path, b"alpha\n"), self.case("   ", path, b"beta\n")])
        self.assertEqual(code, 1, out)
        self.assertEqual(out.count("BAD CASE ID"), 2, out)
        self.assertNotIn("DUPLICATE CASE ID", out)

    def test_GUARDS_PATTERN_MALFORMED_CASE_SUMMARY_PRINTS(self):
        # Round 5, D14: a case that is not a sequence and one too short to name its file are each counted, and the summary
        # still prints; both used to end in a traceback before it.
        code, out = self.check_cases([None, ("only-name",)])
        self.assertEqual(code, 1, out)
        self.assertIn("BAD CASE ID  None", out)
        self.assertIn("BAD CASE SHAPE  only-name (1 fields)", out)
        self.assertIn("2 cases, 2 not matching exactly once.", out)

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

    # A group the real runner pins, so a fixture witness is judged on its anchors alone (round 2, T2-3 flags unpinned ones).
    PINNED = ("--instructions-only",)

    def witness(self, name, path, replacements, args=PINNED):
        return self.hygiene.Witness(name, str(path), "worker", args, tuple(replacements), "SYNTHETIC_CHECK")

    def check_witnesses(self, witnesses):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.hygiene.check_anchors(witnesses)
        return code, out.getvalue()

    def check(self, path, replacements):
        return self.check_witnesses([self.witness("synthetic-witness", path, replacements)])

    def test_HYGIENE_CHECK_UNPINNED_GROUP_FAILS(self):
        # Round 2, T2-3: a witness whose healthy group --apply would refuse as unpinned is not healthy.
        path = self.source("a.cs", b"a\n")
        code, out = self.check_witnesses([self.witness("synthetic-witness", path, [("a\n", "b\n", 1)], args=("--synthetic-only",))])
        self.assertEqual(code, 1, out)
        self.assertIn("UNPINNED synthetic-witness: worker:--synthetic-only", out)

    def test_HYGIENE_CHECK_SECOND_WITNESS_STALE_FAILS(self):
        # Round 2, T2-14: a healthy first witness must not stand for the rest.
        healthy = self.source("a.cs", b"a\n")
        stale = self.source("b.cs", b"b\n")
        code, out = self.check_witnesses([self.witness("healthy-first", healthy, [("a\n", "x\n", 1)]),
                                          self.witness("stale-second", stale, [("c\n", "x\n", 1)])])
        self.assertEqual(code, 1, out)
        self.assertIn("STALE stale-second", out)
        self.assertNotIn("healthy-first", out)

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
