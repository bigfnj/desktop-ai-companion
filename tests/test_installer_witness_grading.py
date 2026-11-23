"""Grade synthetic checker output without compiling or executing an installer."""
import importlib.util
from pathlib import Path
import sys
import unittest

# The grader is fed the lines ShutdownChecks really prints, never text derived from the grader (N-fulltier-1123-01: the
# old fixture, 'FAIL ShutdownChecks.cpp: ...', agreed with the grader and with nothing the checker prints). These are
# byte for byte the stderr of two negative runs of the RC bdbf5c2f checker (step 15 full-tier triage, 2026-11-23,
# legacy-refusal and owner-prevalidation), and HEALTHY is its stdout of a passing run. wmain's catch prints
# "RESULT:FAIL " and the exception text; Check's exception text is "ShutdownChecks.cpp: " and the description.
# test_the_fixture_is_the_line_the_real_check_prints ties these to tests/InstallerShutdown.Tests/ShutdownChecks.cpp.
LEGACY = 'RESULT:FAIL ShutdownChecks.cpp: a legacy upgrade refuses a foreign same-basename companion\r\n'
OWNER = 'RESULT:FAIL ShutdownChecks.cpp: an installed image owned by a different user refuses shutdown\r\n'
HEALTHY = 'PASS 62 checks\r\nRESULT:PASS\r\n'
LEGACY_LABEL = 'a legacy upgrade refuses a foreign same-basename companion'


class InstallerWitnessGradingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('synthetic_installer_grading', Path(__file__).with_name('run_installer_shutdown_mutations.py'))
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def test_every_real_variant_has_a_named_failure(self):
        self.assertTrue(all(item[-1] for item in self.harness.VARIANTS),
                        'run_installer_shutdown_mutations.py: INSTALLER_VARIANT_FAILURE_LABEL_REQUIRED')

    def test_the_fixture_is_the_line_the_real_check_prints(self):
        source = (Path(__file__).parent / 'InstallerShutdown.Tests' / 'ShutdownChecks.cpp').read_text(encoding='utf-8')
        for printed in ('std::cerr << "RESULT:FAIL " << error.what()',
                        'std::runtime_error(std::string("ShutdownChecks.cpp: ") + description)',
                        '"' + LEGACY_LABEL + '"'):
            with self.subTest(printed=printed):
                self.assertEqual(source.count(printed), 1, 'ShutdownChecks.cpp: FAILURE_LINE_FIXTURE_DIFFERS_FROM_CHECK')

    def test_only_exact_rebuilt_single_failure_fires(self):
        # As the evidence file holds it, and as the runner's read_text hands it over (newlines translated).
        for good in (LEGACY, LEGACY.replace('\r\n', '\n')):
            with self.subTest(good=good):
                self.assertTrue(self.harness.exact_failure(good, 1, True, LEGACY_LABEL),
                                'run_installer_shutdown_mutations.py: REAL_FAILURE_LINE_FIRES')
        for text, code, built, label in (
                (OWNER, 1, True, LEGACY_LABEL),  # a real failure of another check
                (LEGACY, 1, True, 'a legacy upgrade refuses'),  # the label is the whole description, not a prefix
                (LEGACY + OWNER, 1, True, LEGACY_LABEL),  # a second failure line
                (LEGACY + 'FAIL ShutdownChecks.cpp: other\n', 1, True, LEGACY_LABEL),  # a second one in the bare form
                ('FAIL ShutdownChecks.cpp: ' + LEGACY_LABEL + '\n', 1, True, LEGACY_LABEL),  # a form never printed
                (HEALTHY, 1, True, LEGACY_LABEL),  # no failure line at all
                (LEGACY, 1, False, LEGACY_LABEL), (LEGACY, 0, True, LEGACY_LABEL),
                (LEGACY, 1, True, None)):
            with self.subTest(text=text, code=code, built=built, label=label):
                self.assertFalse(self.harness.exact_failure(text, code, built, label),
                                 'run_installer_shutdown_mutations.py: EXACT_FAILURE_REQUIRED')


if __name__ == '__main__':
    unittest.main()
