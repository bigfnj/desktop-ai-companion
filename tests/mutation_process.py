"""Lossless decoding for captured mutation-test process output.

Capture raw bytes through temporary files so inherited output handles cannot
strand Windows pipe readers. Source and result-marker files still use their
declared, strict UTF-8 encoding separately.
"""

import ctypes
import io
import os
from pathlib import Path
import subprocess
import tempfile


_CAPTURE_DIRECTORY = Path(__file__).resolve().parent
_MAX_CAPTURE_BYTES = 64 * 1024 * 1024
_REAP_TIMEOUT = 5.0


class CaptureError(RuntimeError):
    """Capture or owned-child cleanup is incomplete; no verdict can be graded."""


def _close(resources, primary, close):
    failure = None
    for resource in reversed(resources):
        try:
            close(resource)
        except BaseException as error:
            failure = failure or error
    if failure is not None:
        capture = CaptureError("mutation capture resource cleanup failed")
        capture.cleanup_error = failure
        capture.add_note("resource close raised " + type(failure).__name__)
        raise capture from (primary if primary is not None else failure)


def _snapshot(writers):
    """Read bounded initial lengths through independent, non-inherited handles."""
    readers = []
    primary = None
    try:
        sizes = []
        for writer in writers:
            # On Windows O_TEMPORARY shares deletion with NamedTemporaryFile's
            # delete-on-close writer. A fresh open has its own file position;
            # seeking a duplicate of the inherited writer would move its offset.
            flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_TEMPORARY", 0)
            reader = os.open(writer.name, flags)
            readers.append(reader)
            os.set_inheritable(reader, False)
            sizes.append(os.fstat(reader).st_size)
        if any(size < 0 for size in sizes) or sum(sizes) > _MAX_CAPTURE_BYTES:
            raise CaptureError("mutation capture exceeds its aggregate byte limit")
        streams = []
        for reader, size in zip(readers, sizes):
            chunks = []
            remaining = size
            while remaining:
                chunk = os.read(reader, min(remaining, 65536))
                if not chunk:
                    raise CaptureError("mutation capture ended before its snapshot length")
                chunks.append(chunk)
                remaining -= len(chunk)
            streams.append(b"".join(chunks))
        return streams
    except BaseException as error:
        primary = error
        if isinstance(error, (CaptureError, KeyboardInterrupt, SystemExit)):
            raise
        primary = CaptureError("mutation capture snapshot failed")
        raise primary from error
    finally:
        _close(readers, primary, os.close)


def _terminate_and_reap(process):
    """Only the retained direct child is eligible for termination."""
    try:
        try:
            process.kill()
        except OSError:
            # It can have exited between wait and kill. The bounded wait,
            # rather than a PID lookup, proves cleanup even after that race.
            pass
    finally:
        # A further interrupt is not swallowed, but still receives this one
        # bounded reap attempt before it propagates.
        try:
            process.wait(timeout=_REAP_TIMEOUT)
        except BaseException as error:
            raise CaptureError("mutation capture could not reap its direct child") from error


def run_bytes(args, **kwargs):
    """Capture a command without waiting for inherited-pipe EOF.

    A direct-child exit or timeout defines the snapshot. Descendant output
    appended later is excluded, and inherited temporary files disappear after
    their final handle closes. The aggregate byte-read cap is not a disk quota
    or a hard peak-memory bound. No reader shares the writer's file offset.

    Timeout cleanup kills only this direct child and waits at most five seconds.
    Incomplete cleanup/capture fails as infrastructure, never as a test verdict.
    check=True and TimeoutExpired retain raw byte payloads. This internal helper
    rejects non-None input and stdin=PIPE before launching; inherited, file and
    DEVNULL stdin remain supported. All other Popen launch options pass through.
    """
    forbidden = {"text", "encoding", "errors", "universal_newlines", "stdout", "stderr"}
    if forbidden.intersection(kwargs):
        raise TypeError("run_bytes controls byte capture and decoding")
    if kwargs.pop("capture_output", True) is not True:
        raise TypeError("run_bytes requires capture_output=True")
    if kwargs.pop("input", None) is not None or kwargs.get("stdin") == subprocess.PIPE:
        raise TypeError("run_bytes does not support input or stdin=PIPE")
    timeout = kwargs.pop("timeout", None)
    check = kwargs.pop("check", False)
    writers = []
    process = None
    primary = None
    cleanup_attempted = False
    try:
        for unused in range(2):
            writers.append(tempfile.NamedTemporaryFile(mode="w+b", prefix="mutation-capture-", suffix=".bin",
                                                       dir=_CAPTURE_DIRECTORY, delete=True))
        # Deliberately not a Popen context manager: its __exit__ can wait without
        # a deadline while unwinding an exceptional capture or cleanup.
        process = subprocess.Popen(args, stdout=writers[0], stderr=writers[1], **kwargs)
        try:
            returncode = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired as expired:
            cleanup_attempted = True
            _terminate_and_reap(process)
            expired.output, expired.stderr = _snapshot(writers)
            raise
        stdout, stderr = _snapshot(writers)
        if check and returncode:
            raise subprocess.CalledProcessError(returncode, args, output=stdout, stderr=stderr)
        return subprocess.CompletedProcess(args, returncode, stdout, stderr)
    except BaseException as error:
        primary = error
        if process is not None and process.returncode is None and not cleanup_attempted:
            cleanup_attempted = True
            try:
                _terminate_and_reap(process)
            except CaptureError as cleanup:
                primary = cleanup
                raise cleanup from error
        raise
    finally:
        _close(writers, primary, lambda writer: writer.close())


def _windows_ansi_encoding():
    if os.name != "nt":
        raise UnicodeError("non-UTF-8 child output has no Windows ANSI fallback")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetACP.argtypes = []
    kernel.GetACP.restype = ctypes.c_uint
    codepage = kernel.GetACP()
    if not codepage or codepage == 65001:
        raise UnicodeError("non-UTF-8 child output has no distinct Windows ANSI fallback")
    return "cp%d" % codepage


def decode_output(raw, fallback_encoding=_windows_ansi_encoding):
    """Decode a stream without replacing or dropping any byte.

    Modern .NET and PowerShell can emit UTF-8; legacy Windows hosts can emit
    the active ANSI code page. UTF-8 wins when valid. The fallback is strict
    and must round-trip exactly; an undecodable stream is a harness error.
    """
    if raw is None:
        return ""
    if not isinstance(raw, bytes):
        raise TypeError("captured process output must be bytes")
    try:
        return raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError:
        encoding = fallback_encoding()
        decoded = raw.decode(encoding, errors="strict")
        if decoded.encode(encoding, errors="strict") != raw:
            raise UnicodeError("child-output ANSI decoding did not round-trip")
        return decoded


def run_text(args, **kwargs):
    """subprocess.run with captured, losslessly decoded stdout and stderr.

    Launch options and raw failure payloads follow run_bytes. Capturing bytes
    prevents reader-thread decode failures; late descendant output is excluded.
    """
    forbidden = {"text", "encoding", "errors", "universal_newlines", "stdout", "stderr"}
    if forbidden.intersection(kwargs):
        raise TypeError("run_text controls byte capture and decoding")
    if kwargs.pop("capture_output", True) is not True:
        raise TypeError("run_text requires capture_output=True")
    result = run_bytes(args, capture_output=True, **kwargs)
    # Match subprocess text=True's universal-newline behavior after proving
    # that the original bytes decoded losslessly. Failure-label anchors in
    # callers must keep seeing the same LF-terminated text they saw before.
    def universal_newlines(raw):
        return decode_output(raw).replace("\r\n", "\n").replace("\r", "\n")
    return subprocess.CompletedProcess(
        result.args, result.returncode,
        universal_newlines(result.stdout), universal_newlines(result.stderr))


def read_utf8_marker(path):
    """Read a declared UTF-8 result marker strictly, without ANSI fallback."""
    with io.open(path, encoding="utf-8", errors="strict") as handle:
        return handle.read()
