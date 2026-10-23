"""Exercise failed healthy-baseline reporting without building or mutating product sources."""

import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch


class MutationBaselineReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().with_name("mutate-selftest-guards.py")
        spec = importlib.util.spec_from_file_location("guard_baseline_report_test", path)
        cls.harness = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.harness
        spec.loader.exec_module(cls.harness)

    def test_refused_script_and_module_baselines_keep_full_diagnostics(self):
        root = Path(__file__).resolve().parent
        for flag in ("TEMPLATE", "--module-selftest=fortunes"):
            with self.subTest(flag=flag), tempfile.TemporaryDirectory(prefix="mutation-baseline-test-", dir=root) as directory:
                self.assertTrue(Path(directory).resolve().is_relative_to(root))
                report = ("synthetic stdout before refusal\nFAIL: synthetic healthy-baseline assertion\n"
                          "additional compiler stderr: preserved \u96ea diagnostic\nRESULT=FAIL\n")
                output = io.StringIO()
                with (patch.object(self.harness, "RUN_TEMP", directory),
                      patch.object(self.harness, "BASELINES", [(flag, "unused-marker.txt")]),
                      patch.object(self.harness, "build_all", return_value=(True, "")),
                      patch.object(self.harness, "selftest", return_value=(report, 1)),
                      patch.object(self.harness, "read", side_effect=AssertionError("no product-source read")),
                      patch.object(self.harness, "write", side_effect=AssertionError("no product-source mutation")),
                      contextlib.redirect_stdout(output)):
                    result = self.harness.score(types.SimpleNamespace(cases=[]))
                self.assertEqual(result, 2)
                saved = list(Path(directory).iterdir())
                self.assertEqual(len(saved), 1)
                self.assertEqual(saved[0].read_text(encoding="utf-8"), report)
                self.assertIn("baseline report kept: " + str(saved[0]), output.getvalue())
                self.assertNotIn("additional compiler stderr", output.getvalue())

    def test_missing_marker_retains_refusal_without_persisting_none(self):
        root = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(prefix="mutation-baseline-test-", dir=root) as directory:
            self.assertTrue(Path(directory).resolve().is_relative_to(root))
            output = io.StringIO()
            with (patch.object(self.harness, "RUN_TEMP", directory),
                  patch.object(self.harness, "BASELINES", [("TEMPLATE", "unused-marker.txt")]),
                  patch.object(self.harness, "build_all", return_value=(True, "")),
                  patch.object(self.harness, "selftest", return_value=(None, "no synthetic marker written")),
                  patch.object(self.harness, "keep_report", side_effect=AssertionError("None is not a report")),
                  contextlib.redirect_stdout(output)):
                result = self.harness.score(types.SimpleNamespace(cases=[]))
            self.assertEqual(result, 2)
            self.assertIn("BASELINE NOT GREEN for TEMPLATE -- no synthetic marker written", output.getvalue())
            self.assertEqual(list(Path(directory).iterdir()), [])


if __name__ == "__main__":
    unittest.main()
