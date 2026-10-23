"""Owned synthetic child-process checks for bounded mutation capture.

All temporary files stay under this checkout. Descendants exit on their own;
the tests never inspect or stop an existing process.
"""

import concurrent.futures
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

if len(sys.argv) < 3 or sys.argv[1] != "--fixture":
    import mutation_process


PARENT_OUT = b"PARENT_STDOUT|"
PARENT_ERR = b"PARENT_STDERR|"
LATE_OUT = b"LATE_STDOUT|"
LATE_ERR = b"LATE_STDERR|"
PYTHON_FLAGS = ("-I", "-S", "-X", "utf8")


def _wait_for_file(path, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists():
            return True
        time.sleep(0.01)
    return path.exists()


def _fixture(mode, arguments):
    """Fixed self-exiting fixtures, selected only by this test program."""
    if mode == "streams":
        os.write(1, arguments[0].encode("ascii"))
        os.write(2, arguments[1].encode("ascii"))
        return int(arguments[2])
    if mode == "launch-options":
        evidence = {"cwd": Path.cwd() == Path(arguments[0]),
                    "environment": os.environ.get("MUTATION_CAPTURE_SYNTHETIC_FLAG") == "fixed"}
        os.write(1, json.dumps(evidence).encode("ascii"))
        os.write(2, b"ERR")
        return 7
    if mode == "mixed-streams":
        os.write(1, "PASS: snow \u96ea\r\nCR witness\r".encode("utf-8"))
        os.write(2, b"FAIL: synthetic \x97 witness\r\n")
        return 0
    if mode == "invalid-error":
        os.write(1, b"RAW_STDOUT_\x97\r\n")
        os.write(2, b"RAW_STDERR_\x81\r\n")
        return 7
    if mode == "stdin":
        os.write(1, sys.stdin.buffer.read())
        return 0
    if mode == "counts":
        os.write(1, b"O" * int(arguments[0]))
        os.write(2, b"E" * int(arguments[1]))
        return 0
    if mode == "sleep":
        os.write(1, PARENT_OUT)
        os.write(2, PARENT_ERR)
        time.sleep(10)
        return 0
    if mode == "late-writer":
        directory = Path(arguments[0])
        (directory / "writer-ready.txt").write_text("ready", encoding="ascii")
        released = _wait_for_file(directory / "release.txt", timeout=8.0)
        offsets = {"stdout": os.lseek(1, 0, os.SEEK_CUR),
                   "stderr": os.lseek(2, 0, os.SEEK_CUR),
                   "released": released}
        os.write(1, LATE_OUT)
        os.write(2, LATE_ERR)
        (directory / "writer-finished.txt").write_text(json.dumps(offsets), encoding="ascii")
        return 0
    if mode in ("spawn-late", "spawn-timeout"):
        directory = Path(arguments[0])
        os.write(1, PARENT_OUT)
        os.write(2, PARENT_ERR)
        subprocess.Popen([sys.executable, *PYTHON_FLAGS, str(Path(__file__).resolve()),
                          "--fixture", "late-writer", str(directory)],
                         stdin=subprocess.DEVNULL, close_fds=True)
        if not _wait_for_file(directory / "writer-ready.txt"):
            return 11
        if mode == "spawn-timeout":
            time.sleep(10)
        return 0
    raise ValueError("unknown synthetic fixture mode")


class MutationCaptureTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(
            prefix="mutation-capture-check-", dir=Path(__file__).resolve().parent)
        self.root = Path(self.directory.name).resolve()
        self.assertEqual(self.root.parent, Path(__file__).resolve().parent)
        self.assertTrue(self.root.name.startswith("mutation-capture-check-"))
        self.captures = self.root / "captures"
        self.captures.mkdir()
        self.capture_location = patch.object(mutation_process, "_CAPTURE_DIRECTORY", self.captures)
        self.capture_location.start()

    def tearDown(self):
        self.capture_location.stop()
        self.assertEqual(self.root.parent, Path(__file__).resolve().parent)
        self.assertTrue(self.root.name.startswith("mutation-capture-check-"))
        self.directory.cleanup()

    def command(self, mode, *arguments):
        return [sys.executable, *PYTHON_FLAGS, str(Path(__file__).resolve()), "--fixture", mode,
                *[str(argument) for argument in arguments]]

    def release_writer(self):
        (self.root / "release.txt").write_text("release", encoding="ascii")
        if not _wait_for_file(self.root / "writer-finished.txt"):
            self.fail("owned synthetic descendant did not finish naturally")
        deadline = time.monotonic() + 5.0
        while list(self.captures.iterdir()) and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertEqual(list(self.captures.iterdir()), [], "capture files must eventually disappear")

    def test_two_streams_args_exit_status_and_launch_options(self):
        arguments = self.command("launch-options", self.root)
        environment = dict(os.environ, MUTATION_CAPTURE_SYNTHETIC_FLAG="fixed")
        result = mutation_process.run_bytes(arguments, cwd=self.root, timeout=5,
                                            env=environment, stdin=subprocess.DEVNULL)
        self.assertEqual(result.args, arguments)
        self.assertEqual((result.returncode, result.stderr), (7, b"ERR"))
        self.assertEqual(json.loads(result.stdout), {"cwd": True, "environment": True})
        self.assertEqual(list(self.captures.iterdir()), [])

    def test_text_decodes_each_complete_stream_and_normalizes_newlines(self):
        decoder = mutation_process.decode_output
        with patch.object(mutation_process, "decode_output",
                          side_effect=lambda raw: decoder(raw, lambda: "cp1252")):
            result = mutation_process.run_text(self.command("mixed-streams"), timeout=5)
        self.assertEqual(result.stdout, "PASS: snow \u96ea\nCR witness\n")
        self.assertEqual(result.stderr, "FAIL: synthetic \u2014 witness\n")

    def test_check_errors_keep_raw_bytes_before_any_decoding(self):
        arguments = self.command("invalid-error")
        for invoke in (mutation_process.run_bytes, mutation_process.run_text):
            with self.subTest(invoke=invoke.__name__):
                with self.assertRaises(subprocess.CalledProcessError) as failure:
                    invoke(arguments, timeout=5, check=True)
                self.assertEqual(failure.exception.cmd, arguments)
                self.assertEqual(failure.exception.returncode, 7)
                self.assertEqual(failure.exception.output, b"RAW_STDOUT_\x97\r\n")
                self.assertEqual(failure.exception.stdout, failure.exception.output)
                self.assertEqual(failure.exception.stderr, b"RAW_STDERR_\x81\r\n")

    def test_unsupported_pipe_input_is_rejected_before_start(self):
        options = ({"input": b""}, {"input": b"synthetic"}, {"input": "synthetic"},
                   {"stdin": subprocess.PIPE}, {"input": None, "stdin": subprocess.PIPE})
        for option in options:
            with self.subTest(option=option):
                with patch.object(mutation_process.subprocess, "Popen") as start:
                    with self.assertRaises(TypeError):
                        mutation_process.run_bytes(self.command("streams", "", "", 0), **option)
                start.assert_not_called()

    def test_inherited_stdin_is_preserved_without_reading_it(self):
        constructor = subprocess.Popen
        with patch.object(mutation_process.subprocess, "Popen", wraps=constructor) as start:
            result = mutation_process.run_bytes(self.command("streams", "OUT", "", 0),
                                                stdin=None, input=None, timeout=5)
        self.assertEqual(result.stdout, b"OUT")
        self.assertIsNone(start.call_args.kwargs.get("stdin"))
        self.assertNotIn("input", start.call_args.kwargs)

    def test_file_and_devnull_stdin_are_supported(self):
        source = self.root / "synthetic-input.txt"
        source.write_bytes(b"fixed stdin witness\r\n")
        with source.open("rb") as handle:
            result = mutation_process.run_bytes(self.command("stdin"), stdin=handle, timeout=5)
            self.assertFalse(handle.closed, "the caller owns its stdin file")
        self.assertEqual(result.stdout, b"fixed stdin witness\r\n")
        result = mutation_process.run_bytes(self.command("stdin"), stdin=subprocess.DEVNULL, timeout=5)
        self.assertEqual(result.stdout, b"")

    def test_aggregate_byte_limit_accepts_boundary_and_refuses_overflow(self):
        with patch.object(mutation_process, "_MAX_CAPTURE_BYTES", 64):
            result = mutation_process.run_bytes(self.command("counts", 32, 32), timeout=5)
            self.assertEqual((result.stdout, result.stderr), (b"O" * 32, b"E" * 32))
            with self.assertRaises(mutation_process.CaptureError):
                mutation_process.run_bytes(self.command("counts", 33, 32), timeout=5)
            with self.assertRaises(mutation_process.CaptureError):
                mutation_process.run_bytes(self.command("counts", 65, 0), timeout=5)
        self.assertEqual(list(self.captures.iterdir()), [])

    def test_parent_exit_does_not_wait_for_inherited_writer_eof(self):
        try:
            result = mutation_process.run_bytes(self.command("spawn-late", self.root), timeout=3)
            self.assertEqual(result.returncode, 0)
            self.assertEqual((result.stdout, result.stderr), (PARENT_OUT, PARENT_ERR))
            self.assertTrue((self.root / "writer-ready.txt").exists())
            self.assertFalse((self.root / "writer-finished.txt").exists(),
                             "capture must return while the inherited writer is still waiting")
        finally:
            self.release_writer()
        offsets = json.loads((self.root / "writer-finished.txt").read_text(encoding="ascii"))
        self.assertEqual(offsets, {"stdout": len(PARENT_OUT), "stderr": len(PARENT_ERR),
                                   "released": True})

    def test_late_append_during_snapshot_keeps_writer_offsets_and_initial_lengths(self):
        snapshot = mutation_process._snapshot
        read = os.read
        released = False

        def interleaved_read(descriptor, count):
            nonlocal released
            if not released:
                released = True
                (self.root / "release.txt").write_text("release", encoding="ascii")
                self.assertTrue(_wait_for_file(self.root / "writer-finished.txt"))
            return read(descriptor, count)

        def interleaved_snapshot(writers):
            with patch.object(mutation_process.os, "read", side_effect=interleaved_read):
                return snapshot(writers)

        try:
            with patch.object(mutation_process, "_snapshot", side_effect=interleaved_snapshot):
                result = mutation_process.run_bytes(self.command("spawn-late", self.root), timeout=3)
            self.assertTrue(released)
            self.assertEqual((result.stdout, result.stderr), (PARENT_OUT, PARENT_ERR))
        finally:
            self.release_writer()
        offsets = json.loads((self.root / "writer-finished.txt").read_text(encoding="ascii"))
        self.assertEqual(offsets, {"stdout": len(PARENT_OUT), "stderr": len(PARENT_ERR),
                                   "released": True})

    def test_timeout_is_bounded_even_with_an_inherited_writer(self):
        arguments = self.command("spawn-timeout", self.root)
        try:
            with self.assertRaises(subprocess.TimeoutExpired) as failure:
                mutation_process.run_bytes(arguments, timeout=3)
            self.assertEqual(failure.exception.cmd, arguments)
            self.assertEqual(failure.exception.timeout, 3)
            self.assertEqual(failure.exception.output, PARENT_OUT)
            self.assertEqual(failure.exception.stderr, PARENT_ERR)
            self.assertTrue((self.root / "writer-ready.txt").exists())
            self.assertFalse((self.root / "writer-finished.txt").exists(),
                             "timeout must not drain until descendant EOF")
        finally:
            self.release_writer()

    def test_concurrent_captures_do_not_mix_streams_or_leave_files(self):
        def invoke(index):
            return mutation_process.run_bytes(self.command("streams", "OUT%d" % index,
                                                          "ERR%d" % index, index), timeout=5)
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
            results = list(executor.map(invoke, range(3)))
        for index, result in enumerate(results):
            self.assertEqual((result.returncode, result.stdout, result.stderr),
                             (index, ("OUT%d" % index).encode("ascii"),
                              ("ERR%d" % index).encode("ascii")))
        self.assertEqual(list(self.captures.iterdir()), [])

    def test_short_snapshot_cannot_be_graded_as_complete_output(self):
        snapshot = mutation_process._snapshot

        def short_snapshot(writers):
            with patch.object(mutation_process.os, "read", return_value=b""):
                return snapshot(writers)

        with patch.object(mutation_process, "_snapshot", side_effect=short_snapshot):
            with self.assertRaisesRegex(mutation_process.CaptureError, "snapshot length"):
                mutation_process.run_bytes(self.command("streams", "OUT", "ERR", 0), timeout=5)
        self.assertEqual(list(self.captures.iterdir()), [])

    def test_reader_close_failure_refuses_an_otherwise_successful_capture(self):
        snapshot = mutation_process._snapshot
        close = os.close

        def failing_close(descriptor):
            close(descriptor)
            raise OSError("synthetic close failure after closing the owned reader")

        def close_failure_snapshot(writers):
            with patch.object(mutation_process.os, "close", side_effect=failing_close):
                return snapshot(writers)

        with patch.object(mutation_process, "_snapshot", side_effect=close_failure_snapshot):
            with self.assertRaisesRegex(mutation_process.CaptureError, "cleanup failed"):
                mutation_process.run_bytes(self.command("streams", "OUT", "ERR", 0), timeout=5)
        self.assertEqual(list(self.captures.iterdir()), [])

    def test_writer_close_failure_is_infrastructure_with_process_failures_retained(self):
        constructor = mutation_process.tempfile.NamedTemporaryFile

        class CloseFault:
            def __init__(self, inner):
                self.inner = inner
                self.reported = False

            def __getattr__(self, name):
                return getattr(self.inner, name)

            def close(self):
                self.inner.close()
                if not self.reported:
                    self.reported = True
                    raise OSError("synthetic close failure after closing the owned writer")

        def create_fault(*arguments, **options):
            return CloseFault(constructor(*arguments, **options))

        with patch.object(mutation_process.tempfile, "NamedTemporaryFile", side_effect=create_fault):
            with self.assertRaises(mutation_process.CaptureError):
                mutation_process.run_bytes(self.command("streams", "OUT", "ERR", 0), timeout=5)
            with self.assertRaises(mutation_process.CaptureError) as failed:
                mutation_process.run_bytes(self.command("streams", "OUT", "ERR", 7),
                                           timeout=5, check=True)
            with self.assertRaises(mutation_process.CaptureError) as timed_out:
                mutation_process.run_bytes(self.command("sleep"), timeout=1)
        self.assertIsInstance(failed.exception.__cause__, subprocess.CalledProcessError)
        self.assertEqual(failed.exception.__cause__.output, b"OUT")
        self.assertEqual(failed.exception.__cause__.stderr, b"ERR")
        self.assertIsInstance(timed_out.exception.__cause__, subprocess.TimeoutExpired)
        self.assertEqual(timed_out.exception.__cause__.timeout, 1)
        self.assertIsInstance(timed_out.exception.__cause__.output, bytes)
        self.assertIn("cleanup failed", str(failed.exception))
        self.assertIn("cleanup failed", str(timed_out.exception))
        self.assertIsInstance(failed.exception.cleanup_error, OSError)
        self.assertIsInstance(timed_out.exception.cleanup_error, OSError)
        self.assertEqual(list(self.captures.iterdir()), [])


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--fixture":
        raise SystemExit(_fixture(sys.argv[2], sys.argv[3:]))
    unittest.main(verbosity=2)
