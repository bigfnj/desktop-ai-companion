"""Lossless decoding for captured mutation-test process output.

Run as bytes so Python's Windows pipe-reader threads cannot lose a complete
stream to an implicit UTF-8 decoding exception. Source files and result-marker
files still use their declared, strict UTF-8 encoding separately.
"""

import ctypes
import io
import os
import subprocess


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

    Timing, exit status, cwd, environment and TimeoutExpired behavior remain
    subprocess.run's. Capturing bytes prevents reader-thread decode failures.
    """
    forbidden = {"text", "encoding", "errors", "universal_newlines", "stdout", "stderr"}
    if forbidden.intersection(kwargs):
        raise TypeError("run_text controls byte capture and decoding")
    if kwargs.pop("capture_output", True) is not True:
        raise TypeError("run_text requires capture_output=True")
    result = subprocess.run(args, capture_output=True, **kwargs)
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
