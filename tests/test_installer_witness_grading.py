"""Grade synthetic checker output without compiling or executing an installer."""
import importlib.util
from pathlib import Path
import sys
import unittest


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

    def test_only_exact_rebuilt_single_failure_fires(self):
        good = 'FAIL ShutdownChecks.cpp: expected witness\r\n'
        self.assertTrue(self.harness.exact_failure(good, 1, True, 'expected witness'))
        for text, code, built, label in (
                ('FAIL ShutdownChecks.cpp: unrelated witness\n', 1, True, 'expected witness'),
                (good + 'FAIL ShutdownChecks.cpp: other\n', 1, True, 'expected witness'),
                (good, 1, False, 'expected witness'), (good, 0, True, 'expected witness'),
                (good, 1, True, None)):
            with self.subTest(text=text, code=code, built=built, label=label):
                self.assertFalse(self.harness.exact_failure(text, code, built, label),
                                 'run_installer_shutdown_mutations.py: EXACT_FAILURE_REQUIRED')


if __name__ == '__main__':
    unittest.main()
