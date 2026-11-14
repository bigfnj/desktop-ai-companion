"""Focused Mute intent protocol controls on original synthetic API stubs, never upstream Mute execution.

Reuses the existing protocol compiler without changing its files. Fresh helper classes are compiled for Java 6;
the runner/probe target Java 8. Source mutations are private and must change a built artifact and execute its probe.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile

HERE = Path(__file__).resolve().parent


def require(condition, witness):
    if not condition:
        raise AssertionError(witness)


def load_compiler():
    spec = importlib.util.spec_from_file_location('mute_protocol_compiler', HERE / 'test_trace2_java.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.JAVA_FILES = module.JAVA_FILES + ('MuteProtocolProbe.java',)
    module.STUBS = dict(module.STUBS)
    module.STUBS['Manager'] = module.STUBS['Manager'].replace(
        'public void parityStep() {',
        'public void parityStep() { parity.MuteProtocolProbe.sourceStep();')
    old_setting = 'public boolean getMascotBooleanSetting(String name, Mascot mascot) { return false; }'
    require(module.STUBS['Main'].count(old_setting) == 1, 'JAVA_MUTE_SETTINGS_STUB_SHAPE')
    module.STUBS['Main'] = module.STUBS['Main'].replace(old_setting,
        'public boolean getMascotBooleanSetting(String name, Mascot mascot) { return Boolean.getBoolean("probe.soundsOn"); }')
    return module


CASES = ('named', 'empty', 'all-on', 'all-off', 'reactivation', 'legacy', 'pre-evaluation-failure', 'bad-order', 'total', 'off', 'pose-refusal', 'completion', 'incomplete-reentry')
MUTATIONS = (
    ('V1ScenarioRunner.java', 'record.put("clip", detail);', 'record.put("clip", ((String)detail).toLowerCase(java.util.Locale.ROOT));', 'named', 'JAVA_MUTE_RAW_NAMED'),
    ('V1ScenarioRunner.java', 'record.put("clip", null);', 'record.put("clip", "");', 'all-on', 'JAVA_MUTE_NULL_VALUE'),
    ('V1ScenarioRunner.java', '!(trace2 && "Mute".equals(className))', '!"Mute".equals(className)', 'legacy', 'JAVA_MUTE_TRACE1_REFUSAL'),
    ('V1ScenarioRunner.java', 'else muteObservations.put(subject, new MuteObservation(enterSequence));', 'else if (!muteObservations.containsKey(subject)) muteObservations.put(subject, new MuteObservation(enterSequence));', 'reactivation', 'JAVA_MUTE_REACTIVATION_IDENTITY'),
    ('draft/DraftParityHooks.java', 'muteDefects.add("MUTE_OBSERVER_FAILED " + hook + ": " + failure.getClass().getName());', 'System.err.print("");', 'total', 'JAVA_MUTE_TOTAL_DEFECTS'),
    ('V1ScenarioRunner.java', 'List<String> defects = DraftParityHooks.muteDefects();', 'List<String> defects = new ArrayList<String>();', 'footer', 'JAVA_MUTE_DEFECT_FOOTER'),
    ('draft/DraftParityHooks.java', 'public static void muteDone(Object action) { observeMute("mute-done", action, null); }', 'public static void muteDone(Object action) { }', 'completion', 'JAVA_MUTE_COMPLETION_REQUIRED'),
    ('V1ScenarioRunner.java', 'runner.validateMuteCompletion();', 'System.err.print("");', 'incomplete-footer', 'JAVA_MUTE_INCOMPLETE_FOOTER'),
)


def run_probe(compiler, root, classes, args, case):
    result = compiler.command([args.jdk_root / 'bin/java.exe', '-Dshimeji.parity.enabled=true', '-cp', classes,
                               'com.group_finity.mascot.parity.MuteProtocolProbe', case], root)
    return result


def check_footer(compiler, root, classes, args):
    for schema in (1, 2):
        for off in (False, True):
            result, rows = compiler.run_trace(root, classes, args, 'mute-defect-' + str(schema) + '-' + str(off), schema=schema, off=off,
                                             properties=('-Dprobe.muteDefect=true', '-Dprobe.sourceThrow=true'))
            require(result.returncode == 1 and rows[-1]['status'] == 'aborted'
                    and 'V1_REFUSAL MUTE_OBSERVER_DEFECT' in result.stderr
                    and 'PROBE_MUTE_SOURCE_FAILURE' in result.stderr, 'JAVA_MUTE_DEFECT_FOOTER')


def check_incomplete_footer(compiler, root, classes, args):
    for off in (False, True):
        for source_throw in (False, True):
            props = ['-Dprobe.muteIncomplete=true']
            if source_throw:
                props.append('-Dprobe.sourceThrow=true')
            result, rows = compiler.run_trace(root, classes, args, 'mute-incomplete-' + str(off) + '-' + str(source_throw), off=off, properties=props)
            require(result.returncode == 1 and rows[-1]['status'] == 'aborted'
                    and 'V1_REFUSAL MUTE_OBSERVER_DEFECT' in result.stderr and 'MUTE_INCOMPLETE_ENTRY' in result.stderr
                    and (not source_throw or 'PROBE_MUTE_SOURCE_FAILURE' in result.stderr), 'JAVA_MUTE_INCOMPLETE_FOOTER')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--jdk-root', type=Path, required=True)
    parser.add_argument('--mutate', action='store_true')
    parser.add_argument('--result', type=Path, required=True)
    args = parser.parse_args()
    compiler = load_compiler()
    pins = {name: hashlib.sha256((HERE / name).read_bytes()).hexdigest() for name in compiler.JAVA_FILES}
    rows = []
    with tempfile.TemporaryDirectory(prefix='oracle-mute-java-', dir=os.environ.get('TEMP')) as folder:
        root = Path(folder)
        baseline = root / 'baseline'; baseline.mkdir()
        classes, build = compiler.compile_case(baseline, args)
        digests = {}
        for case in CASES:
            result = run_probe(compiler, baseline, classes, args, case)
            require(result.returncode == 0 and 'MUTE_PROTOCOL_PROBE_EXECUTED: ' + case in result.stdout,
                    'JAVA_MUTE_PROBE_FAILED ' + case + '\n' + result.stderr)
            if case in ('named', 'off'):
                digests[case] = next(line for line in result.stdout.splitlines() if line.startswith('MUTE_DIGEST '))
            rows.append({'case': case, 'status': 'PASS', 'process': result.execution})
        require(digests['named'] == digests['off'], 'JAVA_MUTE_ON_OFF_DIGEST')
        check_footer(compiler, baseline, classes, args)
        rows.append({'case': 'exceptional-footer-both-modes', 'status': 'PASS'})
        check_incomplete_footer(compiler, baseline, classes, args)
        rows.append({'case': 'incomplete-footer-both-modes-source-normal-or-failed', 'status': 'PASS'})
        baseline_hashes = {name: fact['sha256'] for name, fact in build['fresh_classes'].items()}
        if args.mutate:
            for index, mutation in enumerate(MUTATIONS):
                child = root / ('mutant-' + str(index)); child.mkdir()
                fresh, witness = compiler.compile_case(child, args, mutation)
                mutant_hashes = {name: fact['sha256'] for name, fact in witness['fresh_classes'].items()}
                require(any(baseline_hashes.get(name) != digest for name, digest in mutant_hashes.items()), 'JAVA_MUTE_MUTANT_NOT_REBUILT')
                if mutation[3] in ('footer', 'incomplete-footer'):
                    try:
                        (check_footer if mutation[3] == 'footer' else check_incomplete_footer)(compiler, child, fresh, args)
                    except AssertionError as failure:
                        require(str(failure) == mutation[4], 'JAVA_MUTE_WRONG_WITNESS')
                    else:
                        raise AssertionError('JAVA_MUTE_MUTATION_SURVIVED ' + mutation[4])
                else:
                    result = run_probe(compiler, child, fresh, args, mutation[3])
                    require(result.returncode != 0 and mutation[4] in result.stderr, 'JAVA_MUTE_WRONG_WITNESS ' + mutation[4] + '\n' + result.stderr)
                rows.append({'case': mutation[4], 'status': 'FIRED', 'rebuild': witness})
        require(all(hashlib.sha256((HERE / name).read_bytes()).hexdigest() == digest for name, digest in pins.items()), 'JAVA_MUTE_SOURCE_UNCHANGED')
    args.result.write_text(json.dumps({'status': 'pass', 'scope': 'synthetic protocol only; no upstream application/evaluation proof',
                                    'rows': rows, 'baselineRebuild': build, 'sourceSha256': pins,
                                    'processes': compiler.PROCESS_RECORDS, 'scratchPath': str(root),
                                    'scratchRemoved': not root.exists()}, indent=2) + '\n', encoding='utf-8')
    print('MUTE_JAVA_PASS controls=' + str(len(CASES) + 2) + ' mutations=' + str(len(MUTATIONS) if args.mutate else 0))


if __name__ == '__main__':
    main()
