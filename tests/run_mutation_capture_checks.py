"""Run only synthetic mutation-infrastructure checks with checkout-local TEMP.

python -X utf8 tests/run_mutation_capture_checks.py
No product build, mutation run, live client input or configuration is performed.
"""

import os
from pathlib import Path
import tempfile
import unittest
import uuid


if __name__ == "__main__":
    tests = Path(__file__).resolve().parent
    scratch = tests / ("capture-checks-" + uuid.uuid4().hex)
    scratch.mkdir()
    os.environ["TEMP"] = str(scratch)
    os.environ["TMP"] = str(scratch)
    tempfile.tempdir = str(scratch)
    try:
        suite = unittest.defaultTestLoader.loadTestsFromNames((
            "test_mutation_process",
            "test_mutation_capture",
            "test_mutation_capture_harnesses",
            "test_mutation_baseline_reports",
            "test_mutation_caseplan",
            "test_pin_diagnostic_harness",
            "test_installer_witness_grading",
        ))
        result = unittest.TextTestRunner(verbosity=2).run(suite)
    finally:
        # An unexpected leftover is evidence. Do not recursively remove it.
        if not any(scratch.iterdir()):
            scratch.rmdir()
    print(f"MutationCaptureChecks={result.testsRun}; Failures={len(result.failures)}; "
          f"Errors={len(result.errors)}; Skipped={len(result.skipped)}", flush=True)
    raise SystemExit(0 if result.wasSuccessful() else 1)
