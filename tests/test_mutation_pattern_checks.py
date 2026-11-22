"""Synthetic witnesses that the two pattern-staleness checks can FAIL (AgentBroker 1.10.5 marathon, lane T).

mutate-selftest-guards.py --check-patterns and run_agentbroker_hygiene_mutations.py --check run in the offline gate
(Invoke-OfflineScriptChecks.ps1) against the real harnesses, where a clean tree only ever shows them passing. These cases
feed each check sources it must refuse, on temporary files: nothing is built and no product file is read or written.
Run through tests/run_mutation_capture_checks.py.
"""

import contextlib
import importlib.util
import inspect
import io
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import types
import unittest
from unittest import mock

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

    def test_GUARDS_PATTERN_APPHOST_ARTEFACT_REFUSED(self):
        # Round 9 (DECISIONS 121): an .exe rebuild artefact (the framework-dependent apphost) is refused even when the
        # pattern matches once; the same case naming a .dll passes. Both arms, so the refusal is not just any failure.
        path = self.source("a.cs", b"alpha\n")
        host = ("apphost case", str(path), b"alpha\n", b"replaced\n", "Host.csproj", str(self.scratch / "Host.exe"),
                "--synthetic", "synthetic.txt", "never")
        code, out = self.check_cases([host])
        self.assertEqual(code, 1, out)
        self.assertIn("APPHOST ARTEFACT  apphost case (Host.exe)", out)
        code, out = self.check_cases([host[:5] + (str(self.scratch / "Host.dll"),) + host[6:]])
        self.assertEqual(code, 0, out)
        self.assertNotIn("APPHOST ARTEFACT", out)

    def test_GUARDS_CATALOGUE_NAMES_NO_APPHOST(self):
        # Round 9: the real catalogue names no .exe rebuild artefact, and every case built by the host project that used to
        # name the apphost names DesktopAICompanion.dll beside it.
        exe = [case[0] for case in self.guards.CASES if isinstance(case[5], str) and case[5].lower().endswith(".exe")]
        host = [case for case in self.guards.CASES if case[4] == self.guards.HOST_CSPROJ and case[5] == self.guards.HOST_DLL]
        self.assertEqual(exe, [])
        self.assertEqual(Path(self.guards.HOST_DLL).name, "DesktopAICompanion.dll")
        self.assertEqual(Path(self.guards.HOST_DLL).parent, Path(self.guards.EXE).parent)
        self.assertGreaterEqual(len(host), 462)

    def test_GUARDS_PATTERN_ONLY_REFUSED(self):
        proc = subprocess.run([sys.executable, "-B", "-X", "utf8", str(TESTS / "mutate-selftest-guards.py"),
                               "--check-patterns", "--only=synthetic"], capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 2, proc.stdout + proc.stderr)
        self.assertIn("cannot be combined with --only", proc.stderr)


class GuardsModuleGrade(ScratchCase):
    """Round 6 (lane T, DECISIONS 98): the guards runner's grade() and rebuild_problem(), on X2c's probe reports
    (handoff/x2c-01a1e8b9-99ab/outbox/f-audit/module-grader-probe.json) and the two real derived-line shapes."""

    @classmethod
    def setUpClass(cls):
        cls.guards = load("mutate-selftest-guards.py", "module_grade_guards_under_test")

    def grade(self, report, code, expect="EXPECTED"):
        return self.guards.grade(report, code, expect)

    def test_GUARDS_GRADE_SOLE_EXPECTED_FIRED(self):
        # WITNESS (X2c's sole_expected_failure): the one shape that fires.
        outcome, labels = self.grade("  [agentbroker] FAIL: EXPECTED\nRESULT=FAIL\n", 1)
        self.assertEqual((outcome, labels), ("FIRED", ["[agentbroker] FAIL: EXPECTED"]))

    def test_GUARDS_GRADE_UNRELATED_BEFORE_EXPECTED_COLLATERAL(self):
        outcome, labels = self.grade("  [agentbroker] FAIL: UNRELATED\n  [agentbroker] FAIL: EXPECTED\nRESULT=FAIL\n", 1)
        self.assertTrue(outcome.startswith("COLLATERAL (1 other"), outcome)
        self.assertEqual(labels, ["[agentbroker] FAIL: UNRELATED", "[agentbroker] FAIL: EXPECTED"])

    def test_GUARDS_GRADE_EXPECTED_WITH_SKIP_NOT_FIRED(self):
        outcome, _ = self.grade("  [agentbroker] FAIL: EXPECTED\nSKIP: unexecuted guard\nRESULT=FAIL\n", 1)
        self.assertTrue(outcome.startswith("COLLATERAL (EXC/SKIP"), outcome)

    def test_GUARDS_GRADE_EXPECTED_EXIT_0_NOT_FIRED(self):
        outcome, _ = self.grade("  [agentbroker] FAIL: EXPECTED\nRESULT=FAIL\n", 0)
        self.assertTrue(outcome.startswith("WRONG (expected FAIL line but exit 0"), outcome)

    def test_GUARDS_GRADE_NO_RESULT_FAIL_NOT_FIRED(self):
        outcome, _ = self.grade("  [agentbroker] FAIL: EXPECTED\n", 1)
        self.assertTrue(outcome.startswith("BROKEN (expected FAIL line but no column-0 RESULT=FAIL"), outcome)

    def test_GUARDS_GRADE_MODULE_ECHO_DERIVED_FIRED(self):
        # The real --module-selftest shape: ModuleConventionSelfTest echoes a failed module verdict on its own line.
        report = ("PASS: the module exposes static bool SelfTest(out string detail)\n  [blinkingled] PASS: other\n"
                  "  [blinkingled] FAIL: EXPECTED\n  [blinkingled] RESULT=FAIL\nFAIL: the module's own self-test passed\n"
                  "RESULT=FAIL\n")
        self.assertEqual(self.grade(report, 1), ("FIRED", ["[blinkingled] FAIL: EXPECTED"]))

    def test_GUARDS_GRADE_ECHO_ALONE_IS_A_FAILURE(self):
        # Without a module line beside it the echo is a failure in its own right, never silently dropped.
        outcome, labels = self.grade("FAIL: the module's own self-test passed\nRESULT=FAIL\n", 1)
        self.assertEqual((outcome, labels), ("WRONG -- failed elsewhere", ["FAIL: the module's own self-test passed"]))

    def test_GUARDS_GRADE_CORETESTS_HEADER_DERIVED_FIRED(self):
        report = "PASS: 149 groups\nFAIL: 1 regression group(s).\nFAIL: Group: broke\nRESULT=FAIL\n"
        self.assertEqual(self.grade(report, 1, "Group"), ("FIRED", ["FAIL: Group: broke"]))

    def test_GUARDS_GRADE_AMBIGUOUS_FRAGMENT_COLLATERAL(self):
        outcome, _ = self.grade("  [m] FAIL: EXPECTED one\n  [m] FAIL: EXPECTED two\nRESULT=FAIL\n", 1)
        self.assertTrue(outcome.startswith("COLLATERAL (0 other failing label(s), 1 ambiguous"), outcome)

    def test_GUARDS_GRADE_MULTI_OWNER_FIRED(self):
        # WITNESS: a documented multi-label owner fires on exactly its set.
        outcome, _ = self.grade("  [m] FAIL: ALPHA\n  [m] FAIL: BETA\nRESULT=FAIL\n", 1, ("ALPHA", "BETA"))
        self.assertEqual(outcome, "FIRED")

    def test_GUARDS_GRADE_MULTI_OWNER_MISSING_NOT_FIRED(self):
        outcome, _ = self.grade("  [m] FAIL: ALPHA\nRESULT=FAIL\n", 1, ("ALPHA", "BETA"))
        self.assertEqual(outcome, "WRONG -- expected label(s) absent: BETA")

    def test_GUARDS_GRADE_VERDICT_RECORD_KEEPS_EVERY_VERDICT_LINE(self):
        report = ("PASS: fine\n  [m] PASS: also fine\n  [m] FAIL: EXPECTED\nSKIP: part\nEXC: threw\n  [m] RESULT=FAIL\n"
                  "RESULT=FAIL\n")
        self.assertEqual(self.guards.verdict_record(report),
                         ["[m] FAIL: EXPECTED", "SKIP: part", "EXC: threw", "[m] RESULT=FAIL", "RESULT=FAIL"])

    def artifact(self, data, age):
        path = self.source("module.dll", data)
        stamp = path.stat().st_mtime
        return str(path), stamp - age

    def test_GUARDS_REBUILD_APPHOST_NEVER_PROOF(self):
        # Round 9 (DECISIONS 121): the framework-dependent host's build re-touches its apphost .exe with IDENTICAL bytes and
        # changes only the .dll beside it. The rebuild proof refuses the .exe for exactly that reason (bytes unchanged though
        # its mtime advanced) and accepts the .dll: why host cases name the .dll and --check-patterns refuses an .exe artefact.
        exe = self.source("Host.exe", b"apphost bytes")
        dll = self.source("Host.dll", b"code before the mutant")
        baseline = {path: self.guards.artifact_sha256(str(path)) for path in (exe, dll)}
        before = {path: path.stat().st_mtime - 10 for path in (exe, dll)}
        exe.write_bytes(b"apphost bytes")
        dll.write_bytes(b"code after the mutant")
        problem = self.guards.rebuild_problem(str(exe), before[exe], baseline[exe])
        self.assertIsNotNone(problem)
        self.assertIn("Host.exe rebuilt with the baseline SHA-256", problem)
        self.assertIsNone(self.guards.rebuild_problem(str(dll), before[dll], baseline[dll]))

    def test_GUARDS_REBUILD_SAME_SHA_BROKEN(self):
        path, before = self.artifact(b"baseline bytes", 10)
        problem = self.guards.rebuild_problem(path, before, self.guards.artifact_sha256(path))
        self.assertIsNotNone(problem)
        self.assertIn("rebuilt with the baseline SHA-256", problem)

    def test_GUARDS_REBUILD_NOT_ADVANCED_BROKEN(self):
        path, before = self.artifact(b"mutant bytes", -10)
        self.assertEqual(self.guards.rebuild_problem(path, before, "0" * 64), "module.dll not rebuilt")

    def test_GUARDS_REBUILD_UNKNOWN_BASELINE_BROKEN(self):
        # Round 7 (T6-X1C-1, DECISIONS 106): an artefact the baseline never hashed cannot prove the mutant reached it, however
        # new its timestamp.
        path, before = self.artifact(b"mutant bytes", 10)
        self.assertEqual(self.guards.rebuild_problem(path, before, None), "module.dll has no baseline SHA-256")

    def test_GUARDS_REBUILD_CHANGED_SHA_ACCEPTED(self):
        # WITNESS: an advanced timestamp and a changed hash are the proof.
        path, before = self.artifact(b"mutant bytes", 10)
        self.assertIsNone(self.guards.rebuild_problem(path, before, "0" * 64))


class FakeTree:
    """Fake csproj builds over scratch files, for score() in GuardsScoreCarryOver. A project owns artefacts, each compiled from
    its sources' CURRENT bytes (so two projects can share a source); every write advances the artefact's mtime. The fake
    self-test loads every artefact and script at once and fails one label for each that holds a mutant, which is the
    carry-over's worst case: a stale artefact anywhere shows up in the next case's labels."""

    def __init__(self, scratch):
        self.scratch = scratch
        (scratch / "src").mkdir()
        (scratch / "bin").mkdir()
        self.projects = {}
        self.labels = {}
        self.builds = []
        self.build_all_calls = 0
        self.faults = {}
        self.skip_on_baseline = set()
        self.head = "aaaaaaaaaaaa"
        self.clock = time.time()

    def source(self, name):
        path = self.scratch / "src" / name
        if not path.exists():
            path.write_bytes(b"clean\n")
        return str(path)

    def project(self, name, artifacts):
        """artifacts: [(artefact name, [source names], label)]; returns the csproj and the artefact paths."""
        csproj = str(self.scratch / name)
        owned = []
        for artifact, sources, label in artifacts:
            path = str(self.scratch / "bin" / artifact)
            owned.append((path, [self.source(source) for source in sources]))
            self.labels[path] = label
        self.projects[csproj] = owned
        return csproj, [path for path, _ in owned]

    def script(self, name, label):
        """A csproj-None case's script, which the self-test reads directly (the TEMPLATE and MODULE-SELFTESTS shape)."""
        path = self.source(name)
        self.labels[path] = label
        return path

    def put(self, path, data):
        Path(path).write_bytes(data)
        self.clock += 10
        os.utime(path, (self.clock, self.clock))

    def compile(self, csproj, skip=()):
        for path, sources in self.projects[csproj]:
            if path not in skip:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                self.put(path, b"BUILT " + b"+".join(Path(source).read_bytes() for source in sources))

    def build(self, csproj):
        self.builds.append(csproj)
        fault = self.faults.get((csproj, self.builds.count(csproj)))
        if fault == "raise":
            raise subprocess.TimeoutExpired(["dotnet", "build", csproj], 1800)
        if fault == "fail":
            return False, "synthetic: error CS0000 before any output"
        if isinstance(fault, tuple) and fault[0] == "skip":
            # Design v3: a rebuild that leaves one output as it was (here, still the previous case's mutant).
            self.compile(csproj, skip=(fault[1],))
            return True, ""
        self.compile(csproj)
        if isinstance(fault, tuple) and fault[0] == "add":
            os.makedirs(os.path.dirname(fault[1]), exist_ok=True)
            self.put(fault[1], b"a file the baseline did not have")
        if fault == "partial":
            return False, "synthetic: error CS0000 after a partial write"
        if isinstance(fault, tuple) and fault[0] == "omit":
            os.remove(fault[1])
        if isinstance(fault, tuple) and fault[0] == "drift":
            self.put(fault[1], b"DRIFT " + Path(fault[1]).read_bytes())
            if len(fault) > 2:
                self.head = fault[2]
        return True, ""

    def build_all(self):
        self.build_all_calls += 1
        for csproj in self.projects:
            self.compile(csproj, self.skip_on_baseline if self.build_all_calls == 1 else ())
        return True, ""

    def watched_selftest(self, flag, marker):
        failing = [label for path, label in self.labels.items()
                   if os.path.isfile(path) and b"MUTANT" in Path(path).read_bytes()]
        report = "PASS: synthetic\n" + "".join("FAIL: %s\n" % label for label in failing)
        return report + ("RESULT=FAIL\n" if failing else "RESULT=PASS\n"), (1 if failing else 0), None


class GuardsScoreCarryOver(ScratchCase):
    """Marathon-1106 lane G round 1 (N-P4-cross-artifact-mutant-carryover-01; 1105 DECISIONS 150, X1d's 010): score() must leave
    the switched-away target at its clean bytes before the next case, and stop the run when it cannot. The real score(),
    grade() and rebuild_problem() run over FakeTree; nothing is built and no product file is read or written. The killing
    mutations and their expected failing sets are in the round's killers file (E6 lanes/G/r01; v4: killers-v3)."""

    @classmethod
    def setUpClass(cls):
        cls.guards = load("mutate-selftest-guards.py", "carryover_guards_under_test")

    def setUp(self):
        super().setUp()
        self.tree = FakeTree(self.scratch)
        (self.scratch / "run").mkdir()

    def case(self, name, csproj, source, artifact, label):
        return (name, source, b"clean", b"MUTANT", csproj, artifact, "--synthetic", "synthetic.txt", label)

    def score(self, cases, catalogue=None, owned=None, patches=()):
        """(exit, stdout, rows): rows maps a case name to [outcome, failing labels]. owned: OWNED_TREES for the run (none by
        default, so the real AgentBroker worker path is never walked). patches: further (object, name, value) for this run."""
        guards = self.guards
        out = io.StringIO()
        with contextlib.ExitStack() as stack:
            for target, name, value in patches:
                stack.enter_context(mock.patch.object(target, name, value))
            for name, value in (("RUN_TEMP", str(self.scratch / "run")), ("CASES", list(catalogue or cases)),
                                ("OWNED_TREES", dict(owned or {})),
                                ("build", self.tree.build), ("build_all", self.tree.build_all),
                                ("watched_selftest", self.tree.watched_selftest),
                                ("documents_watch_selfcheck", lambda scratch: []),
                                ("documents_watch_notice", lambda: "DOCUMENTS WATCH: synthetic"),
                                ("worktree_head", lambda: self.tree.head),
                                ("time", types.SimpleNamespace(sleep=lambda seconds: None, monotonic=time.monotonic))):
                stack.enter_context(mock.patch.object(guards, name, value))
            stack.enter_context(contextlib.redirect_stdout(out))
            code = guards.score(types.SimpleNamespace(cases=cases, indices=list(range(1, len(cases) + 1))))
        rows, current = {}, None
        for line in out.getvalue().splitlines():
            if line.startswith("  ") and not line.startswith("   ") and len(line) > 55:
                current = line[2:54].rstrip()
                rows[current] = [line[55:], None]
            elif current is not None and line.startswith("        failing labels ("):
                text = line.split("): ", 1)[1]
                rows[current][1] = [] if text == "none" else text.split(" | ")
        return code, out.getvalue(), rows

    def restore_lines(self, text):
        return [line for line in text.splitlines() if line.startswith("        switch restore of ")]

    def stop_lines(self, text):
        return [line for line in text.splitlines() if line.startswith("THE SWITCHED-AWAY TARGET WAS NOT RESTORED -- ")]

    def unproven_lines(self, text):
        return [line for line in text.splitlines() if line.startswith("THE OWNED TREE CANNOT BE PROVEN -- ")]

    @staticmethod
    def walk_refusing(locked):
        """os.walk, except that a folder named `locked` cannot be listed: onerror gets a PermissionError naming it and the walk
        goes on without it, which is what os.walk does with a folder that scandir refuses."""
        real = os.walk

        def walk(top, topdown=True, onerror=None, followlinks=False):
            for current, dirs, names in real(top, topdown, onerror, followlinks):
                if os.path.basename(current) == locked:
                    if onerror is not None:
                        onerror(PermissionError(13, "Access is denied", current))
                    dirs[:] = []
                    continue
                yield current, dirs, names
        return walk

    def two_targets(self):
        p, (a,) = self.tree.project("P.csproj", [("A.dll", ["a.cs"], "alpha guard")])
        q, (b,) = self.tree.project("Q.csproj", [("B.dll", ["b.cs"], "beta guard")])
        return p, a, q, b

    def test_GUARDS_CARRYOVER_SWITCH_RESTORES(self):
        # P4's shape: a case on one target, then a case on another. The second must carry no label of the first.
        p, a, q, b = self.two_targets()
        cases = [self.case("first target", p, self.tree.source("a.cs"), a, "alpha"),
                 self.case("second target", q, self.tree.source("b.cs"), b, "beta")]
        code, text, rows = self.score(cases)
        self.assertEqual(code, 0, text)
        self.assertEqual(rows["first target"], ["FIRED", ["FAIL: alpha guard"]], text)
        self.assertEqual(rows["second target"], ["FIRED", ["FAIL: beta guard"]], text)
        self.assertEqual(len(self.restore_lines(text)), 1, text)
        self.assertIn("switch restore of P.csproj after a mutant of A.dll", text)
        self.assertIn("switch restores: 1 rebuild(s)", text)
        self.assertEqual([Path(self.tree.source(name)).read_bytes() for name in ("a.cs", "b.cs")], [b"clean\n"] * 2)

    def test_GUARDS_CARRYOVER_TEMPLATE_RESTORES(self):
        # A csproj-None case (TEMPLATE, MODULE-SELFTESTS) after a built target counts as a switch; a second one after it does
        # not, because the restore cleared the dirty state and a csproj-None case builds nothing.
        p, a, q, b = self.two_targets()
        cases = [self.case("built target", p, self.tree.source("a.cs"), a, "alpha"),
                 self.case("template case", None, self.tree.script("template.ps1", "delta guard"), None, "delta"),
                 self.case("second template case", None, self.tree.script("module.ps1", "omega guard"), None, "omega")]
        code, text, rows = self.score(cases)
        self.assertEqual(code, 0, text)
        self.assertEqual(rows["template case"], ["FIRED", ["FAIL: delta guard"]], text)
        self.assertEqual(rows["second template case"], ["FIRED", ["FAIL: omega guard"]], text)
        self.assertEqual(len(self.restore_lines(text)), 1, text)
        self.assertEqual(self.tree.builds, [p, p], text)

    def test_GUARDS_CARRYOVER_SECOND_ARTEFACT_RESTORED(self):
        # The Host's Contracts copy shape: a project owning two case artefacts, a case on the second, then a switch. Both are
        # hashed on the restore, the first named only by a catalogue entry outside the selection.
        p, (a1, a2) = self.tree.project("P.csproj", [("A1.dll", ["a1.cs"], "alpha guard"),
                                                     ("A2.dll", ["a2.cs"], "kappa guard")])
        q, (b,) = self.tree.project("Q.csproj", [("B.dll", ["b.cs"], "beta guard")])
        cases = [self.case("second artefact", p, self.tree.source("a2.cs"), a2, "kappa"),
                 self.case("other target", q, self.tree.source("b.cs"), b, "beta")]
        catalogue = cases + [self.case("unselected first artefact", p, self.tree.source("a1.cs"), a1, "alpha")]
        code, text, rows = self.score(cases, catalogue)
        self.assertEqual(code, 0, text)
        self.assertEqual(rows["other target"], ["FIRED", ["FAIL: beta guard"]], text)
        restores = self.restore_lines(text)
        self.assertEqual(len(restores), 1, text)
        self.assertIn("A1.dll ", restores[0])
        self.assertIn("A2.dll ", restores[0])
        self.assertTrue(restores[0].endswith(" match the baseline"), restores[0])

    def test_GUARDS_CARRYOVER_SHARED_SOURCE_RESTORED_BEFORE_WRITE(self):
        # AiBrain and Remembrance both compile CLI_RUNNER, and adjacent cases mutate it alike. A restore after the next case's
        # write would compile that case's mutant into the switched-away target.
        p, (a,) = self.tree.project("P.csproj", [("A.dll", ["shared.cs"], "alpha guard")])
        q, (b,) = self.tree.project("Q.csproj", [("B.dll", ["shared.cs"], "beta guard")])
        shared = self.tree.source("shared.cs")
        cases = [self.case("shared on P", p, shared, a, "alpha"), self.case("shared on Q", q, shared, b, "beta")]
        code, text, rows = self.score(cases)
        self.assertEqual(code, 0, text)
        self.assertEqual(rows["shared on P"], ["FIRED", ["FAIL: alpha guard"]], text)
        self.assertEqual(rows["shared on Q"], ["FIRED", ["FAIL: beta guard"]], text)
        self.assertEqual(self.stop_lines(text), [], text)

    def test_GUARDS_CARRYOVER_FAILED_BUILD_RESTORED(self):
        # A compile that fails after writing part of its output leaves the target dirty too.
        p, a, q, b = self.two_targets()
        self.tree.faults[(p, 1)] = "partial"
        cases = [self.case("does not compile", p, self.tree.source("a.cs"), a, "alpha"),
                 self.case("after the failed build", q, self.tree.source("b.cs"), b, "beta")]
        code, text, rows = self.score(cases)
        self.assertEqual(rows["does not compile"][0], "BROKEN (does not compile)", text)
        self.assertEqual(rows["after the failed build"], ["FIRED", ["FAIL: beta guard"]], text)
        self.assertEqual(len(self.restore_lines(text)), 1, text)
        self.assertEqual(code, 1, text)

    def test_GUARDS_CARRYOVER_SAME_TARGET_NO_EXTRA_BUILD(self):
        # Two cases of one target: the second case's own build recompiles what the first changed, so no restore is built.
        p, (a1, a2) = self.tree.project("P.csproj", [("A1.dll", ["a1.cs"], "alpha guard"),
                                                     ("A2.dll", ["a2.cs"], "kappa guard")])
        cases = [self.case("first of P", p, self.tree.source("a1.cs"), a1, "alpha"),
                 self.case("second of P", p, self.tree.source("a2.cs"), a2, "kappa")]
        code, text, rows = self.score(cases)
        self.assertEqual(code, 0, text)
        self.assertEqual(self.tree.builds, [p, p], text)
        self.assertEqual(rows["second of P"], ["FIRED", ["FAIL: kappa guard"]], text)
        self.assertEqual(self.restore_lines(text), [], text)
        self.assertIn("switch restores: 0 rebuild(s)", text)

    def test_GUARDS_CARRYOVER_RESTORE_FAILURE_STOPS(self):
        # Every named restore outcome is a whole-run stop: exit 2, its column-0 line, no later case (the next case's mutant is
        # never written), and build_all only twice (the baseline and the outer finally).
        scenarios = (("compile", "fail", "P.csproj: the rebuild did not compile"),
                     ("raise", "raise", "P.csproj: the rebuild raised TimeoutExpired: "),
                     ("missing", "omit", "A.dll: the artefact is missing after the rebuild"),
                     ("hash", "drift", "A.dll: it rebuilt to "),
                     ("head", "drift-head", " (HEAD moved from aaaaaaaaaaaa to bbbbbbbbbbbb)"),
                     ("baseline", "ghost", "G.dll: it has no baseline SHA-256"))
        for label, fault, expected in scenarios:
            with self.subTest(label):
                shutil.rmtree(self.scratch)
                self.scratch.mkdir()
                self.tree = FakeTree(self.scratch)
                (self.scratch / "run").mkdir()
                p, a, q, b = self.two_targets()
                catalogue = []
                if fault == "ghost":
                    p, (a, ghost) = self.tree.project("P.csproj", [("A.dll", ["a.cs"], "alpha guard"),
                                                                   ("G.dll", ["g.cs"], "gamma guard")])
                    self.tree.skip_on_baseline.add(ghost)
                    catalogue = [self.case("unselected ghost", p, self.tree.source("g.cs"), ghost, "gamma")]
                else:
                    self.tree.faults[(p, 2)] = {"omit": ("omit", a), "drift": ("drift", a),
                                                "drift-head": ("drift", a, "bbbbbbbbbbbb")}.get(fault, fault)
                cases = [self.case("dirty target", p, self.tree.source("a.cs"), a, "alpha"),
                         self.case("restore refused here", q, self.tree.source("b.cs"), b, "beta"),
                         self.case("later case", q, self.tree.source("b.cs"), b, "beta")]
                code, text, rows = self.score(cases, cases + catalogue)
                self.assertEqual(code, 2, text)
                stops = self.stop_lines(text)
                self.assertEqual(len(stops), 1, text)
                self.assertIn(expected, stops[0])
                if fault == "drift":
                    self.assertNotIn("HEAD moved", stops[0])
                self.assertEqual(sorted(rows), ["dirty target"], text)
                self.assertEqual(Path(self.tree.source("b.cs")).read_bytes(), b"clean\n")
                self.assertEqual(self.tree.build_all_calls, 2, text)
                self.assertIn("restoring and rebuilding the clean tree", text)

    def owned_tree_target(self):
        """An AgentBroker-shaped target: a named DLL and an owned tree no case names, all compiled from one source. The tree holds
        one file at its top and one a folder down (v4, X3's K2 and the lead's N7: the real worker tree has files in its licenses folder,
        so the walk's recursion must be an axis a test varies). Returns p, a, top, nested, q, b, cases, owned."""
        p, (a, top, nested) = self.tree.project("P.csproj", [
            ("A.dll", ["a.cs"], "alpha guard"),
            (os.path.join("worker", "w0.bin"), ["a.cs"], "top tree guard"),
            (os.path.join("worker", "sub", "w1.bin"), ["a.cs"], "nested tree guard")])
        q, (b,) = self.tree.project("Q.csproj", [("B.dll", ["b.cs"], "beta guard")])
        cases = [self.case("owned-tree target", p, self.tree.source("a.cs"), a, ("alpha", "top tree", "nested tree")),
                 self.case("next target", q, self.tree.source("b.cs"), b, "beta")]
        return p, a, top, nested, q, b, cases, {p: (str(self.scratch / "bin" / "worker"),)}

    def test_GUARDS_CARRYOVER_OWNED_TREE_RESTORED(self):
        # Design v3 (G-DESIGN-01): the restore proves the target's owned tree too. Here the rebuild leaves the NESTED tree file
        # holding the previous mutant; no case names it, so only the owned tree can stop the run before the next case loads it.
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        self.tree.faults[(p, 2)] = ("skip", nested)
        code, text, rows = self.score(cases, owned=owned)
        self.assertEqual(code, 2, text)
        stops = self.stop_lines(text)
        self.assertEqual(len(stops), 1, text)
        self.assertTrue(stops[0].startswith("THE SWITCHED-AWAY TARGET WAS NOT RESTORED -- %s: it rebuilt to " % nested), stops[0])
        self.assertNotIn("HEAD moved", stops[0])
        self.assertEqual(sorted(rows), ["owned-tree target"], text)

    def test_GUARDS_CARRYOVER_OWNED_TREE_ADDED_FILE_RECORDED(self):
        # A file the baseline tree did not have, here a folder down, is recorded in the restore line, not a stop: the next case
        # runs clean. Tree files are counted, never named.
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        extra = os.path.join(os.path.dirname(nested), "extra.bin")
        self.tree.faults[(p, 2)] = ("add", extra)
        code, text, rows = self.score(cases, owned=owned)
        self.assertEqual(code, 0, text)
        self.assertEqual(rows["owned-tree target"],
                         ["FIRED", ["FAIL: alpha guard", "FAIL: top tree guard", "FAIL: nested tree guard"]], text)
        self.assertEqual(rows["next target"], ["FIRED", ["FAIL: beta guard"]], text)
        restores = self.restore_lines(text)
        self.assertEqual(len(restores), 1, text)
        self.assertIn("A.dll ", restores[0])
        self.assertIn("owned tree: 2 file(s) match the baseline", restores[0])
        self.assertIn("1 file(s) not in the baseline tree, recorded, not a stop: extra.bin", restores[0])
        self.assertNotIn("w0.bin", restores[0])
        self.assertNotIn("w1.bin", restores[0])

    def test_GUARDS_CARRYOVER_OWNED_TREES_NAMES_WORKER(self):
        # v4 (DECISIONS 65 item 1; the lead's F2, X3's K1): every other test patches OWNED_TREES, so this one reads the real
        # value. The published AgentBroker worker tree is the one owned tree, at its BIN path.
        guards = self.guards
        self.assertEqual(guards.OWNED_TREES,
                         {guards.AGENTBROKER_CSPROJ: (os.path.join(guards.BIN, "modules", "agentbroker", "worker"),)})
        self.assertEqual(os.path.dirname(guards.OWNED_TREES[guards.AGENTBROKER_CSPROJ][0]),
                         os.path.dirname(guards.AGENTBROKER_DLL))

    def test_GUARDS_CARRYOVER_OWNED_TREE_BASELINE_COUNTED(self):
        # v4: one column-0 line per declared tree at the baseline, with its count, so a tree that shrank is visible on every run.
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        code, text, rows = self.score(cases, owned=owned)
        self.assertEqual([line for line in text.splitlines() if line.startswith("owned tree of ")],
                         ["owned tree of P.csproj: 2 file(s) hashed at baseline under %s" % owned[p][0]], text)

    def test_GUARDS_CARRYOVER_OWNED_TREE_EMPTY_AT_BASELINE_STOPS(self):
        # v4: a declared tree with no file at the baseline stops the run before any case (exit 2, its column-0 line, nothing
        # mutated, so no clean rebuild): no switch restore could prove a tree the baseline does not hold.
        p, a, q, b = self.two_targets()
        empty = self.scratch / "bin" / "empty"
        empty.mkdir()
        cases = [self.case("first target", p, self.tree.source("a.cs"), a, "alpha"),
                 self.case("second target", q, self.tree.source("b.cs"), b, "beta")]
        code, text, rows = self.score(cases, owned={p: (str(empty),)})
        self.assertEqual(code, 2, text)
        self.assertEqual(self.unproven_lines(text), ["THE OWNED TREE CANNOT BE PROVEN -- %s: no file under it at baseline, so no "
                                                     "switch restore could prove it" % empty], text)
        self.assertEqual(rows, {}, text)
        self.assertEqual(self.tree.build_all_calls, 1, text)
        self.assertEqual([Path(self.tree.source(name)).read_bytes() for name in ("a.cs", "b.cs")], [b"clean\n"] * 2)

    def test_GUARDS_CARRYOVER_OWNED_TREE_UNLISTABLE_AT_BASELINE_STOPS(self):
        # v4 (X3's V3-X3-01): a root that is missing, or a folder under it the walk cannot list, stops the run before any case,
        # naming the path; a tree read in part must never pass for a smaller tree.
        for label in ("missing root", "locked folder"):
            with self.subTest(label):
                shutil.rmtree(self.scratch)
                self.scratch.mkdir()
                self.tree = FakeTree(self.scratch)
                (self.scratch / "run").mkdir()
                p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
                patches = ()
                if label == "missing root":
                    path = str(self.scratch / "bin" / "absent")
                    owned = {p: (path,)}
                    reason = ": it could not be listed at baseline (FileNotFoundError: "
                else:
                    locked_file = str(self.scratch / "bin" / "worker" / "locked" / "w2.bin")
                    self.tree.projects[p].append((locked_file, [self.tree.source("a.cs")]))
                    self.tree.labels[locked_file] = "locked tree guard"
                    path = os.path.dirname(locked_file)
                    patches = ((os, "walk", self.walk_refusing("locked")),)
                    reason = ": it could not be listed at baseline (PermissionError: Access is denied)"
                code, text, rows = self.score(cases, owned=owned, patches=patches)
                self.assertEqual(code, 2, text)
                lines = self.unproven_lines(text)
                if label == "missing root":
                    # N8 (lane G round 2, the record route): a missing root is both unlistable and empty, so exactly two
                    # lines, the listing line first; its strerror is the OS's, so that line is matched by its prefix.
                    self.assertEqual(len(lines), 2, text)
                    self.assertTrue(lines[0].startswith("THE OWNED TREE CANNOT BE PROVEN -- " + path + reason), text)
                    self.assertEqual(lines[1], "THE OWNED TREE CANNOT BE PROVEN -- %s: no file under it at baseline, so no "
                                               "switch restore could prove it" % path, text)
                else:
                    self.assertEqual(lines, ["THE OWNED TREE CANNOT BE PROVEN -- " + path + reason], text)
                self.assertEqual(rows, {}, text)
                self.assertEqual(self.tree.build_all_calls, 1, text)

    def test_GUARDS_CARRYOVER_OWNED_TREE_UNLISTABLE_AT_RESTORE_STOPS(self):
        # v4: at the switch restore, a folder under the tree that the walk cannot list is a whole-run stop naming it.
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        locked = os.path.join(owned[p][0], "locked")
        self.tree.faults[(p, 2)] = ("add", os.path.join(locked, "x.bin"))
        code, text, rows = self.score(cases, owned=owned, patches=((os, "walk", self.walk_refusing("locked")),))
        self.assertEqual(code, 2, text)
        self.assertEqual(self.stop_lines(text), ["THE SWITCHED-AWAY TARGET WAS NOT RESTORED -- %s: the owned tree could not be "
                                                 "listed (PermissionError: Access is denied)" % locked], text)
        self.assertEqual(sorted(rows), ["owned-tree target"], text)

    def test_GUARDS_CARRYOVER_OWNED_TREE_UNREADABLE_FILE_STOPS(self):
        # v4 (the lead's N15): a tree file the restore cannot read is a whole-run stop naming it, not a traceback.
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        real = self.guards.artifact_sha256

        def sha(path):
            if path == nested and self.tree.builds.count(p) >= 2:
                raise PermissionError(13, "The process cannot access the file", path)
            return real(path)
        code, text, rows = self.score(cases, owned=owned, patches=((self.guards, "artifact_sha256", sha),))
        self.assertEqual(code, 2, text)
        self.assertEqual(self.stop_lines(text), ["THE SWITCHED-AWAY TARGET WAS NOT RESTORED -- %s: it could not be read "
                                                 "(PermissionError: The process cannot access the file)" % nested], text)
        self.assertEqual(sorted(rows), ["owned-tree target"], text)

    def test_GUARDS_CARRYOVER_OWNED_TREE_UNREADABLE_AT_BASELINE_STOPS(self):
        # Lane G round 2 (DECISIONS 81 (a), N1): a tree file the baseline cannot read is named by its own stop line before any
        # case, symmetric with UNREADABLE_FILE_STOPS at the restore, not a traceback out of score().
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        real = self.guards.artifact_sha256

        def sha(path):
            if path == nested and self.tree.builds.count(p) == 0:
                raise PermissionError(13, "The process cannot access the file", path)
            return real(path)
        code, text, rows = self.score(cases, owned=owned, patches=((self.guards, "artifact_sha256", sha),))
        self.assertEqual(code, 2, text)
        self.assertEqual(self.unproven_lines(text), ["THE OWNED TREE CANNOT BE PROVEN -- %s: it could not be read at baseline "
                                                     "(PermissionError: The process cannot access the file)" % nested], text)
        self.assertEqual(rows, {}, text)
        self.assertEqual(self.tree.build_all_calls, 1, text)
        self.assertEqual([Path(self.tree.source(name)).read_bytes() for name in ("a.cs", "b.cs")], [b"clean\n"] * 2)

    def test_GUARDS_CARRYOVER_OWNED_TREES_PLAN_ONLY(self):
        # v4 (the lead's N10): only the targets of the selected cases have their trees walked. U.csproj is in the catalogue and in
        # OWNED_TREES but no selected case builds it; its root does not exist, so walking it would also stop the run.
        p, a, top, nested, q, b, cases, owned = self.owned_tree_target()
        u = str(self.scratch / "U.csproj")
        owned[u] = (str(self.scratch / "bin" / "absent"),)
        catalogue = cases + [self.case("unselected U case", u, self.tree.source("u.cs"), str(self.scratch / "bin" / "U.dll"),
                                       "upsilon")]
        real, walked = self.guards.owned_tree_files, []

        def recording(csproj):
            walked.append(csproj)
            return real(csproj)
        code, text, rows = self.score(cases, catalogue, owned=owned,
                                      patches=((self.guards, "owned_tree_files", recording),))
        # Both selected targets are asked (Q declares no tree, so nothing is walked for it); U never is.
        self.assertEqual(sorted(set(walked)), sorted([p, q]), text)
        self.assertNotIn(u, walked)
        self.assertNotIn("U.csproj", text)

    def test_GUARDS_CARRYOVER_RESTORE_SIGNATURE(self):
        # The frozen tools-v2 wrapper (repro_exact.py) calls restore_switched_target with exactly these four arguments.
        self.assertEqual(list(inspect.signature(self.guards.restore_switched_target).parameters),
                         ["csproj", "baseline", "head", "after"])

    def test_GUARDS_CARRYOVER_REBUILD_PROOF_UNCHANGED(self):
        # After a switch restore, a mutant that compiles to the baseline bytes is still BROKEN: the proof reads the case's own
        # baseline hash, which no restore replaces.
        p, a, q, b = self.two_targets()
        cases = [self.case("dirty target", p, self.tree.source("a.cs"), a, "alpha"),
                 self.case("mutant never compiled", q, self.tree.source("unbuilt.cs"), b, "beta")]
        code, text, rows = self.score(cases)
        self.assertEqual(rows["mutant never compiled"][0],
                         "BROKEN (B.dll rebuilt with the baseline SHA-256: the mutant did not reach it)", text)
        self.assertEqual(len(self.restore_lines(text)), 1, text)
        self.assertEqual(code, 1, text)

    def test_GUARDS_CARRYOVER_BUILD_PASSES_RELEASE_PUBLICATION_PROPERTIES(self):
        # The 2026-11-21 full tier: the shard PREBUILD is build.ps1 -Release, and a build() without its publication properties
        # recompiled ModuleKit to other bytes (two embedded obj sources), so a switch restore after any ModuleKit recompile read
        # as unrestored. build() must pass exactly the -p: entries of build.ps1's $releasePublicationArguments. The real
        # build.ps1 is read; run_text is replaced, so nothing is built.
        text = (TESTS.parent / "build.ps1").read_text(encoding="utf-8-sig")
        lists = [re.findall(r"'([^']*)'", body)
                 for body in re.findall(r"^\s*\$releasePublicationArguments\s*=\s*@\(([^)]*)\)", text, re.MULTILINE)]
        filled = [entries for entries in lists if entries]
        self.assertEqual(len(filled), 1, "build.ps1 must assign one non-empty $releasePublicationArguments: %r" % (lists,))
        expected = [entry for entry in filled[0] if entry.startswith("-p:")]
        # WITNESS: the list read is the -Release one (its --no-incremental is there) and it names properties, so an empty
        # read cannot pass against an empty build().
        self.assertIn("--no-incremental", filled[0])
        self.assertTrue(expected, filled[0])
        calls = []

        def run_text(args, **kwargs):
            calls.append(list(args))
            return subprocess.CompletedProcess(args, 0, "", "")
        csproj = str(self.scratch / "P.csproj")
        with mock.patch.object(self.guards, "run_text", side_effect=run_text):
            built, _ = self.guards.build(csproj)
        self.assertTrue(built)
        self.assertEqual(len(calls), 1, calls)
        self.assertEqual(calls[0][:5], ["dotnet", "build", csproj, "-c", "Release"], calls)
        self.assertEqual(sorted(arg for arg in calls[0] if arg.startswith("-p:")), sorted(expected), calls)


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
