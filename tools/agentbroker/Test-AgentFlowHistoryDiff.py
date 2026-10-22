#!/usr/bin/env python3
"""Counts-only splitter comparison. Default: metadata only. --apply reads at most 120 files.

Run with python -I -S -X utf8 tools/agentbroker/Test-AgentFlowHistoryDiff.py --self-test
Live scope is fixed to the current Windows profile's .claude/projects/*/*.jsonl.
No transcript text, commands, paths, raw child output or policy settings are printed.
"""
import argparse
import contextlib
import ctypes
from ctypes import wintypes
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import time
import types
import uuid

REPO = Path(__file__).absolute().parents[2]
LIMIT = 120
MAX_TOTAL = 1024 * 1024 * 1024
MAX_FILE = MAX_TOTAL
MAX_LINE = 8 * 1024 * 1024
MAX_COMMANDS = 100000
MAX_COMMAND_TEXT = 16 * 1024 * 1024
MAX_INPUT = 64 * 1024 * 1024
MAX_ENTRIES = 50000
SOURCES = {
    "python": (REPO / "docs/agentflow/agentflow_join.py", "4176127277042e54b4b49a7cbca292522f285c4b42a1d734e6a46c6853054400"),
    "auto-learn": (REPO.parent / "ai-acolyte/src/auto-learn.js", "b38eddfac598a1b894e7d9b4a6ccd74eba6cf4fa815ffa88cd8798c4e935636b"),
    "tool-learn": (REPO.parent / "ai-acolyte/src/tool-learn.js", "fc4a0436e3b9e3228ac9de399596cf1df858a84823f097dca6649c05e30604ae"),
    "permissions": (REPO.parent / "ai-acolyte/src/permissions.js", "c8bc9fcb76ea819c51e4882885305c877267bdbba7fd4b436f6795c260770761"),
    "permission-match": (REPO.parent / "ai-acolyte/src/permission-match.js", "bfac60cc9ac8dd6699d29c211f8298d7e371726106447d244aadfca1b1f8977d"),
}


class Refused(Exception):
    pass


def protected_name(name):
    """Conservative H/W prefilter, applied to EVERY component before metadata access."""
    value = name.casefold()
    return (value.startswith("privatekey") or value.startswith(".env.") or
            value.endswith((".env", ".pem", ".key", ".p12", ".pfx", ".keystore")) or
            value in {".npmrc", ".aws", ".ssh", ".kube", ".pypirc", ".docker", ".mozilla", ".git-credentials"})


def path_allowed(path):
    text = os.fspath(path)
    if not os.path.isabs(text) or text.startswith(("\\\\", "//")):
        return False
    drive, tail = os.path.splitdrive(text)
    parts = tail.replace("\\", "/").split("/")
    return bool(drive) and all(not part or (part not in (".", "..") and
        not protected_name(part) and not part.endswith((".", " ")) and ":" not in part) for part in parts)


def metadata_allowed(value, directory=False):
    if stat.S_ISLNK(value.st_mode) or getattr(value, "st_file_attributes", 0) & 0x400:
        return False
    return stat.S_ISDIR(value.st_mode) if directory else stat.S_ISREG(value.st_mode) and value.st_nlink == 1


def safe_chain(path, directory=False):
    path = Path(os.path.abspath(path))
    if not path_allowed(path):
        raise Refused("path_refused")
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current /= part
        value = current.lstat()
        if not metadata_allowed(value, directory=current != path or directory):
            raise Refused("path_refused")
    return path


class FileIdentity(ctypes.Structure):
    _fields_ = [("attributes", wintypes.DWORD), ("creation", wintypes.FILETIME),
        ("access", wintypes.FILETIME), ("write", wintypes.FILETIME), ("volume", wintypes.DWORD),
        ("size_high", wintypes.DWORD), ("size_low", wintypes.DWORD), ("links", wintypes.DWORD),
        ("index_high", wintypes.DWORD), ("index_low", wintypes.DWORD)]


def identity_stamp(value):
    # A read may update access time; writes/length/object identity must remain unchanged.
    return (value.volume, value.index_high, value.index_low, value.creation.dwHighDateTime,
        value.creation.dwLowDateTime, value.write.dwHighDateTime, value.write.dwLowDateTime,
        value.size_high, value.size_low, value.links, value.attributes)


class InitialLengthReader(io.RawIOBase):
    def __init__(self, stream, size):
        self.stream, self.remaining = stream, size
        self.digest = hashlib.sha256()
    def readable(self):
        return True
    def readinto(self, buffer):
        if not self.remaining:
            return 0
        count = self.stream.readinto(memoryview(buffer)[:min(len(buffer), self.remaining)])
        self.digest.update(memoryview(buffer)[:count])
        self.remaining -= count
        return count


@contextlib.contextmanager
def guarded_open(path, maximum, allow_writes=False):
    """No leaf following; exact final path and identity checked before the first content read."""
    import msvcrt
    path = safe_chain(path)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
        wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    create.restype = wintypes.HANDLE
    info = kernel.GetFileInformationByHandle
    info.argtypes = [wintypes.HANDLE, ctypes.POINTER(FileIdentity)]
    info.restype = wintypes.BOOL
    final_name = kernel.GetFinalPathNameByHandleW
    final_name.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
    final_name.restype = wintypes.DWORD
    kind = kernel.GetFileType
    kind.argtypes = [wintypes.HANDLE]
    kind.restype = wintypes.DWORD
    close = kernel.CloseHandle
    close.argtypes = [wintypes.HANDLE]
    handle = create(str(path), 0x80000000, 3 if allow_writes else 1, None, 3, 0x08200000, None)
    if handle == ctypes.c_void_p(-1).value:
        raise Refused("file_unavailable")
    try:
        identity = FileIdentity()
        final = ctypes.create_unicode_buffer(32768)
        length = final_name(handle, final, len(final), 0)
        actual = final.value[4:] if final.value.startswith("\\\\?\\") else final.value
        if (not info(handle, ctypes.byref(identity)) or kind(handle) != 1 or identity.links != 1 or
                identity.attributes & (0x10 | 0x400) or not 0 < length < len(final) or
                not path_allowed(actual) or os.path.normcase(actual) != os.path.normcase(str(path))):
            raise Refused("path_refused")
        safe_chain(path)
        size = identity.size_high << 32 | identity.size_low
        if size > maximum:
            raise Refused("size_bound")
        bound_handle = handle
        descriptor = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
        handle = None
        with os.fdopen(descriptor, "rb", buffering=0) as stream:
            yield stream, size
            after = FileIdentity()
            if not info(bound_handle, ctypes.byref(after)) or identity_stamp(identity) != identity_stamp(after):
                raise Refused("file_changed")
    finally:
        if handle is not None:
            close(handle)


def candidates(root):
    root = safe_chain(root, directory=True)
    selected = []
    excluded = 0
    entries = 0
    expires = time.monotonic() + 30
    for project in root.iterdir():
        entries += 1
        if entries > MAX_ENTRIES or time.monotonic() > expires:
            raise Refused("entry_bound")
        if not path_allowed(project):
            excluded += 1
            continue
        try:
            safe_chain(project, directory=True)
        except (OSError, Refused):
            excluded += 1
            continue
        for path in project.iterdir():
            entries += 1
            if entries > MAX_ENTRIES or time.monotonic() > expires:
                raise Refused("entry_bound")
            # A JSONL suffix alone is insufficient: .env.* and privatekey* also match.
            if not path.name.casefold().endswith(".jsonl"):
                continue
            if not path_allowed(path):
                excluded += 1
                continue
            try:
                safe_chain(path)
                value = path.lstat()
            except (OSError, Refused):
                excluded += 1
                continue
            selected.append((value.st_mtime_ns, str(path)))
    selected.sort(reverse=True)
    return [Path(path) for _, path in selected[:LIMIT]], excluded


def harvest(paths):
    commands = []
    malformed = total = read = command_bytes = 0
    skips = {"changedFiles": 0, "unavailableFiles": 0, "unsafeFiles": 0}
    expires = time.monotonic() + 120
    for path in paths:
        pending = []
        pending_bytes = 0
        try:
            # Writers may append throughout this read. Only the captured initial length is read;
            # any identity/size/time change discards this file's observations, never its contents.
            with guarded_open(path, MAX_FILE, allow_writes=True) as (stream, size), io.BufferedReader(InitialLengthReader(stream, size)) as bounded:
                total += size
                if total > MAX_TOTAL:
                    raise Refused("corpus_bound")
                remaining = size
                while remaining:
                    line = bounded.readline(min(MAX_LINE + 1, remaining))
                    if not line:
                        raise Refused("file_changed")
                    remaining -= len(line)
                    if len(line) > MAX_LINE or time.monotonic() > expires:
                        raise Refused("corpus_bound")
                    try:
                        record = json.loads(line.decode("utf-8", "strict"))
                    except UnicodeDecodeError:
                        raise Refused("invalid_utf8") from None
                    except (ValueError, RecursionError):
                        malformed += 1
                        continue
                    message = record.get("message") if isinstance(record, dict) else None
                    content = message.get("content") if isinstance(message, dict) else None
                    if not isinstance(content, list):
                        continue
                    for block in content:
                        if not isinstance(block, dict) or block.get("type") != "tool_use" or block.get("name") not in ("Bash", "PowerShell"):
                            continue
                        value = block.get("input")
                        command = value.get("command") if isinstance(value, dict) else None
                        if isinstance(command, str) and command.strip():
                            pending_bytes += len(command.encode("utf-8", "surrogatepass"))
                            if len(commands) + len(pending) + 1 > MAX_COMMANDS or command_bytes + pending_bytes > MAX_COMMAND_TEXT:
                                raise Refused("corpus_bound")
                            pending.append((command, "powershell" if block["name"] == "PowerShell" else "bash"))
                # Append sharing is intentional. A second bounded read also detects same-size
                # rewrites even when Windows defers the writer's last-write timestamp update.
                stream.seek(0)
                second = hashlib.sha256()
                remaining = size
                while remaining:
                    if time.monotonic() > expires:
                        raise Refused("corpus_bound")
                    chunk = stream.read(min(65536, remaining))
                    if not chunk:
                        raise Refused("file_changed")
                    remaining -= len(chunk)
                    second.update(chunk)
                if second.digest() != bounded.raw.digest.digest():
                    raise Refused("file_changed")
        except Refused as error:
            if error.args[0] not in ("file_changed", "file_unavailable", "path_refused"):
                raise
            skips[{"file_changed": "changedFiles", "file_unavailable": "unavailableFiles", "path_refused": "unsafeFiles"}[error.args[0]]] += 1
            continue
        except OSError:
            skips["unavailableFiles"] += 1
            continue
        read += 1
        commands.extend(pending)
        command_bytes += pending_bytes
    return commands, read, malformed, skips


BRIDGE = r"""
'use strict';
try {
  const input = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
  const cache = new Map();
  const noIO = new Proxy({}, {get() { throw new Error('file_io_refused'); }});
  function load(name) {
    if (cache.has(name)) return cache.get(name).exports;
    if (!Object.hasOwn(input.sources, name)) throw new Error('module_refused');
    const module = {exports: {}};
    cache.set(name, module);
    function localRequire(value) {
      if (value === 'fs' || value === 'node:fs') return noIO;
      if (value === 'os' || value === 'path') return require('node:' + value);
      if (value.startsWith('./')) return load(value.slice(2));
      throw new Error('module_refused');
    }
    new Function('exports', 'require', 'module', input.sources[name])(module.exports, localRequire, module);
    return module.exports;
  }
  const original = load('auto-learn');
  let mismatches = 0;
  const indices = [];
  input.cases.forEach((value, index) => {
    const actual = original.splitCommandSegments(value.command, value.shell);
    if (JSON.stringify(actual) !== JSON.stringify(value.expected)) {
      mismatches++;
      if (indices.length < 10) indices.push(index);
    }
  });
  process.stdout.write(JSON.stringify({cases: input.cases.length, mismatches, indices}));
} catch { process.stdout.write('{"reason":"node_execution_failed"}'); process.exitCode = 1; }
"""


def checked_sources():
    source = {}
    for name, (path, expected) in SOURCES.items():
        with guarded_open(path, 2 * 1024 * 1024) as (stream, size):
            raw = stream.read(size + 1)
            if len(raw) != size or hashlib.sha256(raw).hexdigest() != expected:
                raise Refused("source_changed")
            source[name] = raw.decode("utf-8-sig")
    return source


def compare(commands, source=None):
    source = dict(checked_sources() if source is None else source)
    module = types.ModuleType("guarded_agentflow_research")
    module.__file__ = str(SOURCES["python"][0])
    exec(compile(source.pop("python"), module.__file__, "exec"), module.__dict__)
    labelled = [(command, shell) for _, command, shell, _ in module.SPLITTER_CASES if command is not None]
    all_cases = labelled + commands
    cases = []
    expires = time.monotonic() + 120
    for command, shell in all_cases:
        if time.monotonic() > expires:
            raise Refused("comparison_timeout")
        cases.append({"command": command, "shell": shell, "expected": module.split_command_segments(command, shell)})
    data = json.dumps({"sources": source, "cases": cases}, ensure_ascii=True).encode("utf-8")
    if len(data) > MAX_INPUT:
        raise Refused("corpus_bound")
    node = safe_chain(Path(os.environ.get("ProgramFiles", "")) / "nodejs/node.exe")
    # No NODE_OPTIONS, preloads, vendor credentials or inherited extra-CA paths.
    environment = {key: os.environ[key] for key in ("SystemRoot", "WINDIR", "PATH", "TEMP", "TMP") if key in os.environ}
    try:
        result = subprocess.run([str(node), "--permission", "--max-old-space-size=384", "-e", BRIDGE],
            input=data, capture_output=True, timeout=180, env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
    except subprocess.TimeoutExpired:
        raise Refused("node_timeout") from None
    if result.returncode != 0 or len(result.stdout) > 4096:
        raise Refused("node_failed")
    try:
        answer = json.loads(result.stdout)
        if (set(answer) != {"cases", "mismatches", "indices"} or answer["cases"] != len(all_cases) or
            type(answer["mismatches"]) is not int or not 0 <= answer["mismatches"] <= len(all_cases) or
            not isinstance(answer["indices"], list) or len(answer["indices"]) > 10 or
            any(type(index) is not int or not 0 <= index < len(all_cases) for index in answer["indices"])):
            raise ValueError()
    except (ValueError, TypeError):
        raise Refused("node_failed") from None
    return answer, len(labelled)


def current_history():
    buffer = ctypes.create_unicode_buffer(32768)
    if ctypes.windll.shell32.SHGetFolderPathW(None, 0x28, None, 0, buffer) != 0:
        raise Refused("profile_unavailable")
    return Path(buffer.value) / ".claude/projects"


def self_test():
    from unittest.mock import patch
    checks = 0
    def check(value):
        nonlocal checks
        if not value:
            raise Refused("synthetic_test_failed")
        checks += 1
    for value in (".env.session.jsonl", "privatekeySession.jsonl", "PRIVATEKEYrecords", "secret.key", "client.pem", ".npmrc"):
        check(protected_name(value))
    check(not protected_name("session.jsonl"))
    with patch("os.lstat", side_effect=AssertionError("metadata must not be touched")):
        for value in (r"C:\synthetic\privatekeyCorpus\session.jsonl", r"C:\synthetic\.env.session.jsonl"):
            try:
                safe_chain(value)
            except Refused:
                check(True)
            else:
                check(False)
    check(not metadata_allowed(types.SimpleNamespace(st_mode=stat.S_IFREG, st_nlink=1, st_file_attributes=0x400)))
    check(not metadata_allowed(types.SimpleNamespace(st_mode=stat.S_IFREG, st_nlink=2, st_file_attributes=0)))
    with io.BufferedReader(InitialLengthReader(io.BytesIO(b"initialAPPEND"), 7)) as bounded:
        check(bounded.read() == b"initial")
    scratch = REPO / "artifacts" / ("history-diff-selftest-" + uuid.uuid4().hex)
    project = scratch / "project"
    project.mkdir(parents=True)
    path, alias = project / "session.jsonl", project / "alias.jsonl"
    owned = [alias, path]
    try:
        path.write_text(json.dumps({"message": {"content": [{"type": "tool_use", "name": "Bash", "input": {"command": "echo synthetic && echo fixture"}}]}}) + "\n", encoding="utf-8")
        selected, excluded = candidates(scratch)
        check(selected == [path] and excluded == 0)
        commands, count, malformed, skips = harvest(selected)
        check(count == 1 and malformed == 0 and not any(skips.values()) and commands == [("echo synthetic && echo fixture", "bash")])
        with guarded_open(path, MAX_FILE, allow_writes=True) as (stream, initial_size):
            with path.open("ab") as writer:
                writer.write(b"\n")
                writer.flush()
            check(len(stream.read(initial_size)) == initial_size)
        check(False)
    except Refused as error:
        if error.args != ("file_changed",):
            raise
        check(True)
    try:
        with guarded_open(path, 1):
            check(False)
    except Refused as error:
        check(error.args == ("size_bound",))
    try:
        invalid = project / "invalid.jsonl"
        owned.append(invalid)
        invalid.write_bytes(b'{"message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"echo \xff"}}]}}\n')
        try:
            harvest([invalid])
            check(False)
        except Refused as error:
            check(error.args == ("invalid_utf8",))
        invalid.unlink()
        actual_guard = guarded_open
        @contextlib.contextmanager
        def changing_guard(selected, maximum, allow_writes=False):
            with actual_guard(selected, maximum, allow_writes=allow_writes) as opened:
                yield opened
                if allow_writes:
                    with selected.open("ab") as writer:
                        writer.write(b"\n")
        with patch.dict(globals(), {"guarded_open": changing_guard}):
            changed, count, _, skips = harvest([path])
        check(not changed and count == 0 and skips == {"changedFiles": 1, "unavailableFiles": 0, "unsafeFiles": 0})
        os.link(path, alias)
        try:
            with guarded_open(path, MAX_FILE):
                check(False)
        except Refused:
            check(True)
        selected, excluded = candidates(scratch)
        check(not selected and excluded == 2)
        start = time.time_ns() - 10_000_000_000
        for index in range(121):
            extra = project / ("sample%03d.jsonl" % index)
            owned.append(extra)
            extra.write_bytes(b"{}\n")
            os.utime(extra, ns=(start + index * 10_000_000, start + index * 10_000_000))
        selected, excluded = candidates(scratch)
        check(len(selected) == 120 and selected[0].name == "sample120.jsonl" and selected[-1].name == "sample001.jsonl" and excluded == 2)
        answer, labelled = compare([("echo synthetic && echo fixture", "bash"), ("Write-Output 'a;b'; Write-Output c", "powershell")])
        check(answer["mismatches"] == 0 and answer["cases"] == labelled + 2)
    finally:
        for item in owned:
            if os.path.lexists(item):
                item.unlink()
        project.rmdir()
        scratch.rmdir()
    return {"status": "synthetic_pass", "checks": checks}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="read the selected newest eligible 120 history files")
    parser.add_argument("--self-test", action="store_true", help="only ordinary repo-local synthetic fixtures and public parser sources")
    args = parser.parse_args()
    if os.name != "nt" or args.apply and args.self_test:
        raise Refused("arguments_refused")
    if args.self_test:
        print(json.dumps(self_test()))
        return 0
    selected, excluded = candidates(current_history())
    sizes = [safe_chain(path).lstat().st_size for path in selected]
    report = {"status": "metadata_only", "limit": LIMIT, "selectedFiles": len(selected), "excludedEntries": excluded,
        "selectedBytes": sum(sizes), "oversizedSelectedFiles": sum(size > MAX_FILE for size in sizes),
        "readFiles": 0, "harvestedCommands": 0}
    if not args.apply:
        print(json.dumps(report))
        return 0
    if report["oversizedSelectedFiles"] or report["selectedBytes"] > MAX_TOTAL:
        raise Refused("corpus_bound")
    source = checked_sources()  # Validate the parser code before touching any transcript contents.
    commands, read, malformed, skips = harvest(selected)
    report.update(readFiles=read, harvestedCommands=len(commands), malformedRecords=malformed, **skips)
    if not commands:
        report.update(status="degraded", reason="no_harvested_commands")
        print(json.dumps(report))
        return 1
    answer, labelled = compare(commands, source)
    complete = not any(skips.values())
    report.update(status="partial" if not complete else "pass" if answer["mismatches"] == 0 else "mismatch", readFiles=read,
        harvestedCommands=len(commands), labelledCases=labelled, cases=answer["cases"], malformedRecords=malformed,
        mismatches=answer["mismatches"], mismatchIndices=answer["indices"])
    print(json.dumps(report))
    return 0 if complete and answer["mismatches"] == 0 else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Refused as error:
        print(json.dumps({"status": "refused", "reason": error.args[0]}))
        raise SystemExit(1)
    except Exception:
        print('{"status":"refused","reason":"operation_unavailable"}')
        raise SystemExit(1)
