"""Synthetic stream tests. Does not run a product self-test or change input."""

import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from mutation_process import decode_output, read_utf8_marker, run_text, _windows_ansi_encoding


class MutationProcessTests(unittest.TestCase):
    def test_utf8_witness_and_ascii_label_are_preserved_without_fallback(self):
        expected = "FAIL: synthetic \u2014 \u96ea witness\r\nRESULT=FAIL\r\n"
        def forbidden_fallback():
            self.fail("valid UTF-8 must not ask for ANSI fallback")
        self.assertEqual(decode_output(expected.encode("utf-8"), forbidden_fallback), expected)

    def test_legacy_ansi_witness_preserves_every_byte(self):
        expected = "FAIL: synthetic \u2014 witness\r\nRESULT=FAIL\r\n"
        raw = expected.encode("cp1252")
        self.assertIn(b"\x97", raw)
        actual = decode_output(raw, lambda: "cp1252")
        self.assertEqual(actual, expected)
        self.assertEqual(actual.encode("cp1252"), raw)

    def test_undecodable_ansi_byte_refuses_instead_of_hiding_witness(self):
        with self.assertRaises(UnicodeDecodeError):
            decode_output(b"FAIL: broken\x81 witness\r\n", lambda: "cp1252")

    def test_non_utf8_has_no_unapproved_fallback(self):
        def no_fallback():
            raise UnicodeError("no fallback")
        with self.assertRaisesRegex(UnicodeError, "no fallback"):
            decode_output(b"FAIL: \x97 witness", no_fallback)

    def test_empty_and_absent_streams_are_empty(self):
        self.assertEqual(decode_output(b""), "")
        self.assertEqual(decode_output(None), "")

    def test_refuses_already_decoded_stream(self):
        with self.assertRaises(TypeError):
            decode_output("FAIL: synthetic")

    def test_both_streams_exit_status_and_launch_options_are_preserved(self):
        launch = ["synthetic-exe", "--synthetic-flag"]
        completed = subprocess.CompletedProcess(launch, 7, b"PASS: synthetic\r\n", b"FAIL: other\r\n")
        environment = {"ONLY_SYNTHETIC_VALUE": "1"}
        with patch("mutation_process.subprocess.run", return_value=completed) as invoke:
            result = run_text(launch, cwd="synthetic-cwd", timeout=19, env=environment)
        invoke.assert_called_once_with(launch, capture_output=True, cwd="synthetic-cwd", timeout=19,
                                      env=environment)
        self.assertEqual(result.args, launch)
        self.assertEqual(result.returncode, 7)
        self.assertEqual(result.stdout, "PASS: synthetic\n")
        self.assertEqual(result.stderr, "FAIL: other\n")

    def test_anchored_witnesses_keep_universal_newline_behavior(self):
        raw = b"PASS: before\r\nFAIL: CRLF witness\r\nFAIL: CR witness\rRESULT=FAIL\r"
        completed = subprocess.CompletedProcess(["synthetic"], 1, raw, b"")
        with patch("mutation_process.subprocess.run", return_value=completed):
            result = run_text(["synthetic"])
        self.assertEqual(re.findall(r"^FAIL: (.+ witness)$", result.stdout, re.MULTILINE),
                         ["CRLF witness", "CR witness"])
        self.assertEqual(re.findall(r"^RESULT=FAIL$", result.stdout, re.MULTILINE), ["RESULT=FAIL"])
        self.assertNotIn("\r", result.stdout)
        self.assertEqual(decode_output(raw), raw.decode("utf-8"))

    def test_mixed_utf8_stdout_and_ansi_stderr_decode_independently(self):
        completed = subprocess.CompletedProcess(["synthetic"], 1, "PASS: \u96ea".encode("utf-8"),
                                                 b"FAIL: synthetic \x97 witness")
        with patch("mutation_process.subprocess.run", return_value=completed):
            with patch("mutation_process.decode_output", side_effect=lambda raw: decode_output(raw, lambda: "cp1252")):
                result = run_text(["synthetic"])
        self.assertEqual(result.stdout, "PASS: \u96ea")
        self.assertEqual(result.stderr, "FAIL: synthetic \u2014 witness")

    def test_decode_failure_reaches_caller(self):
        completed = subprocess.CompletedProcess(["synthetic"], 1, b"PASS: synthetic", b"FAIL: \x81")
        with patch("mutation_process.subprocess.run", return_value=completed):
            with patch("mutation_process.decode_output", side_effect=lambda raw: decode_output(raw, lambda: "cp1252")):
                with self.assertRaises(UnicodeDecodeError):
                    run_text(["synthetic"])

    def test_timeout_is_not_replaced_by_success(self):
        failure = subprocess.TimeoutExpired(["synthetic"], 19, output=b"FAIL: synthetic")
        with patch("mutation_process.subprocess.run", side_effect=failure):
            with self.assertRaises(subprocess.TimeoutExpired) as observed:
                run_text(["synthetic"], timeout=19)
        self.assertIs(observed.exception, failure)

    def test_text_and_lossy_decoder_overrides_are_refused(self):
        for name, value in (("text", True), ("encoding", "utf-8"), ("errors", "replace"),
                            ("universal_newlines", True), ("capture_output", False)):
            with self.subTest(name=name):
                with self.assertRaises(TypeError):
                    run_text(["synthetic"], **{name: value})

    def test_invalid_utf8_marker_cannot_hide_behind_green_ascii_verdict(self):
        # Keep every fixture under the checked-out test directory, including
        # when this proof is run outside the normal release-gate environment.
        with tempfile.TemporaryDirectory(prefix="mutation-output-test-", dir=Path(__file__).resolve().parent) as directory:
            marker = Path(directory) / "synthetic-marker.txt"
            marker.write_bytes(b"PASS: synthetic\nRESULT=PASS\nFAIL: invalid byte \x97 witness\n")
            with self.assertRaises(UnicodeDecodeError):
                read_utf8_marker(marker)
            marker.write_text("PASS: synthetic \u96ea\nRESULT=PASS\n", encoding="utf-8")
            self.assertEqual(read_utf8_marker(marker), "PASS: synthetic \u96ea\nRESULT=PASS\n")

    @unittest.skipUnless(os.name == "nt", "legacy Windows PowerShell witness")
    def test_actual_legacy_powershell_fixed_synthetic_ansi_witness(self):
        # Explicit bytes avoid inheriting either Python UTF-8 mode or a host console's code page.
        command = ("$s = [Console]::OpenStandardOutput(); "
                   "$b = [byte[]](70,65,73,76,58,32,115,121,110,116,104,101,116,105,99,32,151,32,119,105,116,110,101,115,115,13,10); "
                   "$s.Write($b,0,$b.Length); $s.Flush(); exit 7")
        raw = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                             capture_output=True, timeout=30)
        self.assertEqual(raw.returncode, 7)
        self.assertEqual(raw.stderr, b"")
        self.assertEqual(raw.stdout, b"FAIL: synthetic \x97 witness\r\n")
        self.assertEqual(decode_output(raw.stdout, lambda: "cp1252"), "FAIL: synthetic \u2014 witness\r\n")
        if _windows_ansi_encoding() == "cp1252":
            result = run_text(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command], timeout=30)
            self.assertEqual(result.stdout, "FAIL: synthetic \u2014 witness\n")
            self.assertEqual(result.returncode, 7)


if __name__ == "__main__":
    unittest.main(verbosity=2)
