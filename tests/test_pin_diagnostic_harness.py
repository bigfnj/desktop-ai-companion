"""Synthetic selection/baseline checks; never build or mutate product sources."""
import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock, patch


class PinDiagnosticHarnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).with_name('run_agentbroker_pin_diagnostic_mutations.py')
        spec = importlib.util.spec_from_file_location('synthetic_pin_harness', path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def runner(self, broken=None, empty=None):
        instance = object.__new__(self.harness.Runner)
        instance.build = lambda *args: None
        calls = []
        named = '\n'.join('PASS: ' + label for witness in self.harness.WITNESSES
                          for label in witness.allowed_existing_failures if witness.family == 'worker')

        def test(family, label, args=(), **kwargs):
            calls.append((family, args))
            if args == broken:
                raise RuntimeError('synthetic unhealthy selected group')
            return {'assertions': 0 if args == empty else 1, 'output': named}

        instance.test = test
        return instance, calls

    def witness(self, name='synthetic'):
        return self.harness.Witness(name, 'runtime', 'worker', ('--metadata-only',), 'MetadataChecks.cs', ())

    def test_selected_group_is_green_before_any_mutation_and_deduplicated(self):
        runner, calls = self.runner()
        runner.healthy('baseline', ('worker',), (self.witness(), self.witness('same-group')))
        self.assertEqual(calls.count(('worker', ('--metadata-only',))), 1,
                         'run_agentbroker_pin_diagnostic_mutations.py: SELECTED_HEALTHY_GROUP_REQUIRED')

    def test_failing_selected_group_refuses_healthy_phase(self):
        runner, _ = self.runner(broken=('--metadata-only',))
        with self.assertRaisesRegex(RuntimeError, 'synthetic unhealthy selected group'):
            runner.healthy('baseline', ('worker',), (self.witness(),))

    def test_empty_selected_group_refuses_healthy_phase(self):
        runner, _ = self.runner(empty=('--metadata-only',))
        with self.assertRaisesRegex(RuntimeError, 'proved no healthy assertions'):
            runner.healthy('restored', ('worker',), (self.witness(),))

    def test_held_lock_creates_no_evidence_and_starts_no_runner(self):
        with tempfile.TemporaryDirectory(prefix='pin-lock-fixture-', dir=Path(__file__).parent) as directory:
            root = Path(directory)
            artifacts = root / 'artifacts'
            artifacts.mkdir()
            lock = artifacts / '.pin-diagnostics-mutation.lock'
            lock.write_bytes(b'synthetic-existing-owner')
            evidence = artifacts / 'pin-diagnostic-mutations-locked'
            args = ['synthetic', '--apply', '--case', self.harness.WITNESSES[0].name, '--output', str(evidence)]
            with patch.object(self.harness, 'ROOT', root), patch.object(sys, 'argv', args), \
                    patch.object(self.harness, 'Runner') as launch:
                with self.assertRaises(FileExistsError):
                    self.harness.main()
                launch.assert_not_called()
            self.assertFalse(evidence.exists(), 'run_agentbroker_pin_diagnostic_mutations.py: LOCK_PRECEDES_EVIDENCE')
            self.assertEqual(lock.read_bytes(), b'synthetic-existing-owner')

    def test_evidence_collision_releases_only_the_new_lock(self):
        with tempfile.TemporaryDirectory(prefix='pin-lock-fixture-', dir=Path(__file__).parent) as directory:
            root = Path(directory)
            artifacts = root / 'artifacts'
            artifacts.mkdir()
            evidence = artifacts / 'pin-diagnostic-mutations-collision'
            evidence.mkdir()
            sentinel = evidence / 'sentinel.txt'
            sentinel.write_bytes(b'preserved')
            args = ['synthetic', '--apply', '--case', self.harness.WITNESSES[0].name, '--output', str(evidence)]
            with patch.object(self.harness, 'ROOT', root), patch.object(sys, 'argv', args), \
                    patch.object(self.harness, 'Runner') as launch:
                with self.assertRaises(FileExistsError):
                    self.harness.main()
                launch.assert_not_called()
            self.assertFalse((artifacts / '.pin-diagnostics-mutation.lock').exists())
            self.assertEqual(sentinel.read_bytes(), b'preserved')


class PinArtifactWitnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).with_name('run_agentbroker_pin_diagnostic_mutations.py')
        spec = importlib.util.spec_from_file_location('synthetic_pin_artifact_harness', path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def setUp(self):
        scratch = tempfile.TemporaryDirectory(prefix='pin-artifact-fixture-', dir=Path(__file__).parent)
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name)
        self.assembly = self.root / 'worker' / 'AgentBroker.Worker.Tests.dll'
        self.assembly.parent.mkdir()
        for name in (self.assembly.name, *self.harness.ARTIFACT_NAMES['worker']):
            path = self.assembly.parent / name
            path.write_bytes(('before:' + name).encode('ascii'))
            stamp = time.time_ns() - 5_000_000_000
            os.utime(path, ns=(stamp, stamp))
        for attribute, value in (('ROOT', self.root), ('ASSEMBLIES', {'worker': self.assembly})):
            replacement = patch.object(self.harness, attribute, value)
            replacement.start()
            self.addCleanup(replacement.stop)
        self.runner = object.__new__(self.harness.Runner)
        self.runner.owned_current = {}
        self.runner.built_artifacts = {}
        self.runner.steps = []
        self.calls = []

    def install_fake_sdk(self, mode='all', test_mutation=False, failure_output=None):
        def invoke(label, args, timeout):
            self.calls.append((label, args))
            if args[1] == 'build':
                names = (self.assembly.name, *self.harness.ARTIFACT_NAMES['worker']) if mode == 'all' else (
                    ('AgentBroker.Worker.Tests.dll',) if mode == 'test-only' else ())
                for name in names:
                    path = self.assembly.parent / name
                    previous = path.stat().st_mtime_ns
                    path.write_bytes((label + ':' + name).encode('ascii'))
                    stamp = max(time.time_ns(), previous + 100_000)
                    os.utime(path, ns=(stamp, stamp))
            if args[1].endswith('.dll') and test_mutation:
                (self.assembly.parent / 'AgentBroker.Worker.dll').write_bytes(b'changed-during-test')
            entry = {'exitCode': 1 if args[1].endswith('.dll') and failure_output else 0,
                     'output': failure_output or 'RESULT=PASS assertions=1\n'}
            self.runner.steps.append(entry)
            return entry
        self.runner.invoke = invoke

    def test_success_exit_cannot_claim_stale_artifacts_were_built(self):
        self.install_fake_sdk('none')
        with self.assertRaisesRegex(RuntimeError, 'PIN_ARTIFACT_NOT_REBUILT',
                                   msg='run_agentbroker_pin_diagnostic_mutations.py: PIN_BUILD_ARTIFACT_REBUILD_REQUIRED'):
            self.runner.build('worker', 'synthetic-stale')

    def test_dependency_must_rebuild_when_test_assembly_does(self):
        self.install_fake_sdk('test-only')
        with self.assertRaisesRegex(RuntimeError, 'PIN_ARTIFACT_NOT_REBUILT: AgentBroker.Worker.dll',
                                   msg='run_agentbroker_pin_diagnostic_mutations.py: PIN_DEPENDENCY_REBUILD_REQUIRED'):
            self.runner.build('worker', 'synthetic-dependency-stale')

    def test_real_build_caller_records_and_executes_the_same_artifacts(self):
        self.install_fake_sdk()
        self.runner.build('worker', 'synthetic-fresh')
        self.assertIn('--no-incremental', self.calls[-1][1])
        proof = self.runner.steps[-1]['artifactBuildProof']['after']
        result = self.runner.test('worker', 'synthetic-test', ())
        self.assertEqual(result['executedArtifactProof'], proof,
                         'run_agentbroker_pin_diagnostic_mutations.py: PIN_EXECUTION_BINDS_BUILD')
        self.assertEqual(len(proof), 1 + len(self.harness.ARTIFACT_NAMES['worker']))

    def test_changed_artifacts_refuse_before_test_launch(self):
        self.install_fake_sdk()
        self.runner.build('worker', 'synthetic-fresh')
        (self.assembly.parent / 'AgentBroker.Worker.dll').write_bytes(b'foreign-change')
        before_calls = len(self.calls)
        with self.assertRaisesRegex(RuntimeError, 'PIN_EXECUTION_ARTIFACTS_DIFFER_FROM_BUILD',
                                   msg='run_agentbroker_pin_diagnostic_mutations.py: PIN_EXECUTION_BUILD_MATCH_REQUIRED'):
            self.runner.test('worker', 'synthetic-test', ())
        self.assertEqual(len(self.calls), before_calls)

    def test_artifact_change_during_test_refuses_its_result(self):
        self.install_fake_sdk(test_mutation=True)
        self.runner.build('worker', 'synthetic-fresh')
        with self.assertRaisesRegex(RuntimeError, 'PIN_EXECUTION_ARTIFACTS_CHANGED_DURING_TEST',
                                   msg='run_agentbroker_pin_diagnostic_mutations.py: PIN_EXECUTION_ARTIFACT_STABILITY_REQUIRED'):
            self.runner.test('worker', 'synthetic-test', ())

    def test_restored_phase_build_and_execution_need_fresh_proof(self):
        self.install_fake_sdk()
        self.runner.build('worker', 'baseline-build')
        baseline = self.runner.built_artifacts['worker']
        self.runner.test('worker', 'baseline-test', ())
        self.runner.build('worker', 'restored-build')
        restored = self.runner.built_artifacts['worker']
        self.runner.test('worker', 'restored-test', ())
        self.assertTrue(all(restored[name]['mtimeNs'] > baseline[name]['mtimeNs'] for name in baseline),
                        'run_agentbroker_pin_diagnostic_mutations.py: PIN_RESTORED_BUILD_PROOF_REQUIRED')
        self.assertEqual(sum('artifactBuildProof' in step for step in self.runner.steps), 2)
        self.assertEqual(sum('executedArtifactProof' in step for step in self.runner.steps), 2)

    def test_representative_mutation_requires_its_intended_assertion(self):
        self.install_fake_sdk()
        self.runner.build('worker', 'synthetic-fresh')
        wrong = 'System.IO.IOException: unrelated error\nCodexPinDiagnosticChecks.cs\nRESULT=FAIL\n'
        self.install_fake_sdk(failure_output=wrong)
        with self.assertRaisesRegex(RuntimeError, 'PIN_INTENDED_ASSERTION_REQUIRED',
                                   msg='run_agentbroker_pin_diagnostic_mutations.py: PIN_INTENDED_ASSERTION_MUST_MATCH'):
            self.runner.test('worker', 'synthetic-wrong-failure', (), failure_file='CodexPinDiagnosticChecks.cs', failure_check='expected check')
        right = 'System.InvalidOperationException: expected check\nCodexPinDiagnosticChecks.cs\nRESULT=FAIL\n'
        self.install_fake_sdk(failure_output=right)
        result = self.runner.test('worker', 'synthetic-right-failure', (), failure_file='CodexPinDiagnosticChecks.cs', failure_check='expected check')
        self.assertEqual(result['failedCheck'], 'expected check')

    def test_mutation_requires_changed_dependency_hash_even_with_fresh_timestamps(self):
        relative = 'tools/AgentBroker.Worker/synthetic-input.txt'
        source = self.root / relative
        source.parent.mkdir(parents=True)
        source.write_bytes(b'before')
        self.runner.original = {relative: b'before'}

        def mutate(witness):
            source.write_bytes(b'after')

        def fresh_unchanged_build(family, label):
            for name in (self.assembly.name, *self.harness.ARTIFACT_NAMES['worker']):
                path = self.assembly.parent / name
                stamp = max(time.time_ns(), path.stat().st_mtime_ns + 100_000)
                os.utime(path, ns=(stamp, stamp))
            self.runner.built_artifacts[family] = self.harness.artifact_snapshot(family)

        self.runner.mutate = mutate
        self.runner.build = fresh_unchanged_build
        self.runner.restore = lambda: source.write_bytes(b'before')
        self.runner.test = Mock(return_value={'exitCode': 1, 'expectedOutcome': 'new_test_failed', 'assertions': 1})
        witness = self.harness.Witness('synthetic', 'codex', 'worker', (), 'FakeChecks.cs', ())
        with patch.object(self.harness, 'SOURCES', {'codex': relative}):
            with self.assertRaisesRegex(RuntimeError, 'PIN_MUTATED_DEPENDENCY_UNCHANGED',
                                       msg='run_agentbroker_pin_diagnostic_mutations.py: PIN_MUTATED_DEPENDENCY_HASH_REQUIRED'):
                self.runner.witness(witness)
        self.runner.test.assert_not_called()
        self.assertEqual(source.read_bytes(), b'before')


if __name__ == '__main__':
    unittest.main()
