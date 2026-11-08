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
import sys
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


def unwrapped_lines(out):
    """The output's lines, with Windows PowerShell's word wrap of a thrown message undone (N-bd-w8-studio-1-02).

    powershell.exe renders an uncaught throw at the width of the console it inherited, and a message longer than that
    is WORD-WRAPPED: the line breaks after a space, keeps that space at its end, and the rest goes on at column 0 of the
    next line. Measured 2026-11-08 with synthetic labels in hidden consoles of 40, 53, 60, 77, 96 and 100 columns: every
    break fell after a space, never at a hyphen, a slash or a backslash, and the lines joined back into the exact
    message. The width is whatever console the harness happens to inherit (100 from one agent shell here; wave 8 saw 95
    under its detached launch), so matching line by line read WRONG whatever the mutation did for a case whose expected text was
    longer than one line, or straddled a break: a 96-character case in a 90-column console printed its own label across
    two lines and scored "WRONG -- failed on something else".

    So a line that ends in whitespace is joined to the one after it. Never a PASS line, on either side: Write-Host does
    not wrap them, and a PASS line must not lend its text to a failure. Rejected: inferring the width from the longest
    line, since a word wrap's lines are not of one length. Known limit: one word longer than a whole console line is cut
    without a space and stays cut; a label's prose holds none.

    SHARED (N-bd-w9-tests-harness-1-03). It lived in mutate-hardening-guards.py alone, and mutate-diagnostics.py grades
    cases on the same script's output, so its gate cases were still matched one physical line at a time. A short
    fragment in the middle of a label straddles a break too, and the record lines below the message (CategoryInfo,
    FullyQualifiedErrorId) are no fallback: they break INSIDE a word at some widths and go on with an indent. Measured
    2026-11-08, the diagnostics fragment "keeps no second copy of the rules", its label thrown in 19 hidden consoles
    between 40 and 84 columns wide, was on no single line at 40, 42, 46, 57, 58, 60 and 84 columns, and on a joined
    line at every width.
    Both harnesses import this one copy, so a fix to the reading reaches both.
    """
    lines = []
    for line in out.splitlines():
        if (lines and lines[-1][-1:].isspace()
                and not lines[-1].strip().startswith("PASS:") and not line.strip().startswith("PASS:")):
            lines[-1] += line
        else:
            lines.append(line)
    return lines


def utf8_stdio():
    """Make this process's stdout and stderr write UTF-8, whatever they are attached to (N-bd-w7-aibrain-1-03).

    A harness prints lines out of the reports it grades, and those carry the markers the panes answer with (a check
    mark, a cross, a warning sign). Python encodes a REDIRECTED stdout in the ANSI code page unless -X utf8 or
    PYTHONIOENCODING says otherwise, and cp1252 holds none of those three, so the first FAIL detail carrying one raised
    UnicodeEncodeError out of the scoring loop and ended the run with the rest of the selected cases unrun (python from
    Git Bash with its output sent to a file). UTF-8 is what the gate and CI already get, since they start the harnesses
    with -X utf8. "replace" can only matter for a lone surrogate, which the strict child-output decoding above never
    produces. Rejected: errors="replace" on the ANSI encoding alone, which keeps the run going but prints every marker
    as "?", so a FIRED detail could no longer tell a check mark from a cross. A stream with no reconfigure (a test's
    StringIO) is left as it is. Each harness calls this first thing in main().
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")
