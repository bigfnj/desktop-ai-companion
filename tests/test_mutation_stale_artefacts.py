"""A case's verdict is read against its own mutant only (N-audit-1106-053), on synthetic projects: nothing is built.

Each synthetic "build" copies its project's source into its artefact and advances the artefact's timestamp, the way a
real incremental build does; a synthetic self-test reads artefacts the way a module self-test loads the host. No
product file is read, written or built, and no process is started.
"""

import contextlib
import importlib.util
import io
import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch


class StaleArtefactTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().with_name("mutate-selftest-guards.py")
        spec = importlib.util.spec_from_file_location("guard_stale_artefact_test", path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def run_two_cases(self, folder, advance_clean_rebuild=True):
        """Case 1 mutates project A; case 2 mutates project B, whose self-test also loads A's artefact and fails only
        when A's artefact holds case 1's mutant. Returns (exit code, console, builds as (project, source text))."""
        source = {"A": os.path.join(folder, "a.cs"), "B": os.path.join(folder, "b.cs")}
        artefact = {"A": os.path.join(folder, "a.dll"), "B": os.path.join(folder, "b.dll")}
        project = {"A": os.path.join(folder, "A.csproj"), "B": os.path.join(folder, "B.csproj")}
        clock = [1000000000.0]
        for key in ("A", "B"):
            Path(source[key]).write_bytes(b"clean " + key.encode())
            Path(artefact[key]).write_bytes(b"clean " + key.encode())
            os.utime(artefact[key], (clock[0], clock[0]))
        builds = []

        def build(csproj):
            key = next(k for k, v in project.items() if v == csproj)
            text = Path(source[key]).read_bytes()
            builds.append((key, text.decode()))
            mutant_was_built = Path(artefact[key]).read_bytes() != b"clean " + key.encode()
            previous = os.path.getmtime(artefact[key])
            Path(artefact[key]).write_bytes(text)
            if advance_clean_rebuild or not mutant_was_built:
                clock[0] += 10
                os.utime(artefact[key], (clock[0], clock[0]))
            else:
                # The writing itself stamps the real time; put the old stamp back, as a build that did not run would.
                os.utime(artefact[key], (previous, previous))
            return True, ""

        def selftest(flag, marker):
            a_mutated = Path(artefact["A"]).read_bytes() == b"mutant A"
            if flag == "--a-selftest" and a_mutated:
                return "FAIL: a's assertion\nRESULT=FAIL\n", 1
            if flag == "--b-selftest" and a_mutated:
                return "FAIL: b's assertion\nRESULT=FAIL\n", 1
            return "PASS: everything\nRESULT=PASS\n", 0

        cases = [
            ("case one", source["A"], b"clean A", b"mutant A", project["A"], artefact["A"], "--a-selftest", "a.txt",
             "a's assertion"),
            # Its own mutant changes nothing its self-test reads: it can only SURVIVE, unless A's mutant is still built.
            ("case two", source["B"], b"clean B", b"mutant B", project["B"], artefact["B"], "--b-selftest", "b.txt",
             "b's assertion"),
        ]
        output = io.StringIO()
        with patch.object(self.harness, "RUN_TEMP", folder), \
             patch.object(self.harness, "DOCUMENTS_REMEMBRANCE", None), \
             patch.object(self.harness, "documents_watch_selfcheck", return_value=[]), \
             patch.object(self.harness, "build_all", return_value=(True, "")), \
             patch.object(self.harness, "build", side_effect=build), \
             patch.object(self.harness, "selftest", side_effect=selftest), \
             patch.object(self.harness.time, "sleep"), \
             contextlib.redirect_stdout(output):
            code = self.harness.score(types.SimpleNamespace(cases=cases, indices=[1, 2]))
        # WITNESS: every source is restored byte for byte whatever the run did.
        self.assertEqual(Path(source["A"]).read_bytes(), b"clean A")
        self.assertEqual(Path(source["B"]).read_bytes(), b"clean B")
        return code, output.getvalue(), builds

    def test_a_case_is_not_scored_against_the_previous_cases_mutant(self):
        with tempfile.TemporaryDirectory() as folder:
            code, console, builds = self.run_two_cases(folder)
        self.assertEqual(builds, [("A", "mutant A"), ("A", "clean A"), ("B", "mutant B")],
                         "stale artefacts: project A is rebuilt from its restored source before case two builds B")
        self.assertRegex(console, r"case two\s+SURVIVED",
                         "stale artefacts: case two cannot fire from case one's mutant")
        # WITNESS: case one still fires from its own mutant, so the synthetic self-tests do grade.
        self.assertRegex(console, r"case one\s+FIRED")
        self.assertEqual(code, 1)

    def test_a_clean_rebuild_that_leaves_the_mutant_stops_the_run(self):
        with tempfile.TemporaryDirectory() as folder:
            code, console, builds = self.run_two_cases(folder, advance_clean_rebuild=False)
        self.assertIn("STOPPED: the clean rebuild of A.csproj left a.dll as the mutant built it", console,
                      "stale artefacts: a clean rebuild whose artefact did not advance stops the run by name")
        self.assertRegex(console, r"case two\s+NOT RUN")
        self.assertEqual(builds, [("A", "mutant A"), ("A", "clean A")],
                         "stale artefacts: no case is built after the clean rebuild failed its proof")
        self.assertEqual(code, 1)

    def test_a_shards_cases_of_one_project_run_together(self):
        """N-bd-tests-harness-1-02: a shard takes every Nth case, so its plan alternates projects, and each change of
        project cost a clean rebuild of the one before. score() runs a project's cases together, in plan order within the
        project, so the plan A, B, A pays one clean rebuild of A, not one of A and one of B; every case keeps its own index."""
        with tempfile.TemporaryDirectory() as folder:
            source = {"A": os.path.join(folder, "a.cs"), "B": os.path.join(folder, "b.cs")}
            artefact = {"A": os.path.join(folder, "a.dll"), "B": os.path.join(folder, "b.dll")}
            project = {"A": os.path.join(folder, "A.csproj"), "B": os.path.join(folder, "B.csproj")}
            clock = [1000000000.0]
            for key in ("A", "B"):
                Path(source[key]).write_bytes(b"clean " + key.encode())
                Path(artefact[key]).write_bytes(b"clean " + key.encode())
                os.utime(artefact[key], (clock[0], clock[0]))
            builds = []

            def build(csproj):
                key = next(k for k, v in project.items() if v == csproj)
                text = Path(source[key]).read_bytes()
                builds.append((key, text.decode()))
                Path(artefact[key]).write_bytes(text)
                clock[0] += 10
                os.utime(artefact[key], (clock[0], clock[0]))
                return True, ""

            def selftest(flag, marker):
                # Each of A's mutants fires A's assertion; B's mutant changes nothing B reads, so case two SURVIVES and
                # its report is kept under ITS index, which is how the pairing of index and case is seen.
                if flag == "--a-selftest" and Path(artefact["A"]).read_bytes() != b"clean A":
                    return "FAIL: a's assertion\nRESULT=FAIL\n", 1
                return "PASS: everything\nRESULT=PASS\n", 0

            cases = [
                ("case one", source["A"], b"clean A", b"mutant A", project["A"], artefact["A"], "--a-selftest", "a.txt",
                 "a's assertion"),
                ("case two", source["B"], b"clean B", b"mutant B", project["B"], artefact["B"], "--b-selftest", "b.txt",
                 "b's assertion"),
                ("case three", source["A"], b"clean A", b"other mutant A", project["A"], artefact["A"], "--a-selftest",
                 "a.txt", "a's assertion"),
            ]
            output = io.StringIO()
            with patch.object(self.harness, "RUN_TEMP", folder), \
                 patch.object(self.harness, "DOCUMENTS_REMEMBRANCE", None), \
                 patch.object(self.harness, "documents_watch_selfcheck", return_value=[]), \
                 patch.object(self.harness, "build_all", return_value=(True, "")), \
                 patch.object(self.harness, "build", side_effect=build), \
                 patch.object(self.harness, "selftest", side_effect=selftest), \
                 patch.object(self.harness.time, "sleep"), \
                 contextlib.redirect_stdout(output):
                code = self.harness.score(types.SimpleNamespace(cases=cases, indices=[1, 2, 3]))
            kept_two = os.path.isfile(os.path.join(folder, "case-002-case-two.txt"))
            restored = (Path(source["A"]).read_bytes(), Path(source["B"]).read_bytes())
        self.assertEqual(builds, [("A", "mutant A"), ("A", "other mutant A"), ("A", "clean A"), ("B", "mutant B")],
                         "case order: a shard's cases of one project run together, so A is rebuilt clean once")
        console = output.getvalue()
        # WITNESSES: both of A's cases still fire from their own mutants, case two is scored and its report keeps index 2,
        # and every source is restored.
        self.assertRegex(console, r"case one\s+FIRED")
        self.assertRegex(console, r"case three\s+FIRED")
        self.assertRegex(console, r"case two\s+SURVIVED")
        self.assertTrue(kept_two, "case order: case two's report is kept under its own plan index")
        self.assertEqual(restored, (b"clean A", b"clean B"))
        self.assertEqual(code, 1)

    def test_a_project_shared_by_consecutive_cases_is_not_rebuilt_between_them(self):
        stale = {"X.csproj": "x.dll"}
        with patch.object(self.harness, "build", side_effect=AssertionError("no build for the next case's own project")):
            self.assertIsNone(self.harness.rebuild_restored(stale, "X.csproj"))
        self.assertEqual(stale, {"X.csproj": "x.dll"}, "the next case's own project stays recorded until it is rebuilt")


if __name__ == "__main__":
    unittest.main()
