#!/usr/bin/env python3
"""AgentFlow CPU probe -- does agent CPU separate "blocked on a prompt" from "working"?

The fourth candidate discriminator, and the only one never tried. It is free, it needs
no pixels, and unlike a window query it works while the host is occluded or minimised.
It is also the only candidate that does not care about permission mode, which matters:
the rule join measured 19% precision in `default` and 0.09% in `auto`, so every signal
built on the rule files inherits a default-mode-only ceiling. CPU does not.

  THE THESIS, and the correction to it

A stall threshold fires ~450 times per real prompt (agentflow_backtest.py) because a
slow tool call and a human-blocked call look identical in the transcript: an unpaired
tool_use and a quiet file. CPU is supposed to tell them apart -- an agent at a prompt
computes nothing, a build does.

Measuring the AGENT PROCESS would not show that, and this is the trap the harness
exists to avoid. On this box a Bash tool call spawns bash.exe as a CHILD of claude.exe;
the child burns the CPU while the agent process itself waits on a pipe, near idle. So
bare process CPU is near zero for BOTH classes and collapses exactly the distinction it
was meant to draw. The tree is the unit: agent plus descendants. Idle tree means waiting
on a human or on the model, busy tree means working.

`--verify` proves that mechanism before any measurement is trusted, by burning CPU in a
child and asserting the tree sees it while the bare process does not. A run that reports
0% everywhere is otherwise indistinguishable from a sampler that cannot read anything.

  ATTRIBUTION

Solved, for resumed sessions: claude.exe carries `--resume=<session-id>` on its command
line, and that id IS the transcript filename, so a sample joins to the right session
rather than to "some agent". Two limits, both recorded rather than papered over:

  * a FRESH session has no --resume= yet (the id is assigned after launch), so its tree
    cannot be attributed this way and is reported as session "?".
  * `--permission-mode` on the command line is the LAUNCH mode. It does not change when
    the mode is flipped at runtime, so it is never read as the current mode. The
    transcript's `type: "permission-mode"` record is the authority (and note it carries
    NO timestamp, so carrying it forward is positional, by file order).

Unattributed detection is how the newest-file bug in agentflow-probe.py hid for a whole
run, so every row carries its session id and its root pid.

  COLLECTING WITHOUT POISONING THE SAMPLE

Do not run this as a foreground tool call of the agent you are measuring. Walked into
on 2026-09-17: the launching Bash call stays an unpaired tool_use for as long as the
sampler runs, so that session reports `pending=1` for every sample of its own run and
lands in the "outstanding" bucket throughout. The sampler already excludes its own
subtree from the CPU total, which is a different problem with the same cause. Run it
detached, or point it at a session other than the one driving it.

  PRIVACY

Prints and writes process names, pids, tool NAMES, counts and durations only. Never a
command line, never a tool argument, never a path or prompt text out of a transcript --
the same line the other four harnesses hold and the shipped product would have to.

    python agentflow_cpu.py --verify              # prove the mechanism, no agent needed
    python agentflow_cpu.py --interval 1 --count 20 --csv out.csv
    python agentflow_cpu.py --report out.csv
"""

import argparse
import csv
import ctypes
import ctypes.wintypes as wt
import datetime as dt
import importlib.util
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))

# Roots we treat as an agent. codex.exe is included because the probe already has a
# Codex adapter; its tool work also lands in child processes (node_repl.exe and friends).
AGENT_EXES = ("claude.exe", "codex.exe")

NCPU = os.cpu_count() or 1


# ---------------------------------------------------------------------------------
# process table + CPU times, via ctypes. No psutil: the other four harnesses are
# stdlib-only and a research harness that needs a pip install does not get run.
# ---------------------------------------------------------------------------------

TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
MAX_PATH = 260

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wt.DWORD),
        ("cntUsage", wt.DWORD),
        ("th32ProcessID", wt.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wt.DWORD),
        ("cntThreads", wt.DWORD),
        ("th32ParentProcessID", wt.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wt.DWORD),
        ("szExeFile", ctypes.c_wchar * MAX_PATH),
    ]


kernel32.CreateToolhelp32Snapshot.restype = wt.HANDLE
kernel32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
kernel32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
kernel32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.GetProcessTimes.argtypes = [
    wt.HANDLE,
    ctypes.POINTER(wt.FILETIME),
    ctypes.POINTER(wt.FILETIME),
    ctypes.POINTER(wt.FILETIME),
    ctypes.POINTER(wt.FILETIME),
]


def _ft(filetime):
    return (filetime.dwHighDateTime << 32) | filetime.dwLowDateTime


def process_table():
    """{pid: (ppid, exe_name)} for every process we can see."""
    table = {}
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = kernel32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            table[int(entry.th32ProcessID)] = (
                int(entry.th32ParentProcessID),
                entry.szExeFile,
            )
            ok = kernel32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snap)
    return table


def cpu_100ns(pid):
    """Kernel+user CPU for one pid in 100ns units, or None if it cannot be read.

    None is NOT zero and must never be folded into one: a process that exited, or one
    we lack rights to open, would otherwise be indistinguishable from a process sitting
    idle, which is the whole measurement.
    """
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        creation, exited, kernel, user = (wt.FILETIME() for _ in range(4))
        if not kernel32.GetProcessTimes(
            handle,
            ctypes.byref(creation),
            ctypes.byref(exited),
            ctypes.byref(kernel),
            ctypes.byref(user),
        ):
            return None
        return _ft(kernel) + _ft(user)
    finally:
        kernel32.CloseHandle(handle)


FILETIME_UNIX_EPOCH = 11644473600.0   # seconds between 1601-01-01 and 1970-01-01


def create_time(pid):
    """Process creation time as a unix epoch float, or None if it cannot be read."""
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        creation, exited, kernel, user = (wt.FILETIME() for _ in range(4))
        if not kernel32.GetProcessTimes(
            handle,
            ctypes.byref(creation),
            ctypes.byref(exited),
            ctypes.byref(kernel),
            ctypes.byref(user),
        ):
            return None
        return (_ft(creation) / 1e7) - FILETIME_UNIX_EPOCH
    finally:
        kernel32.CloseHandle(handle)


def children_of(table):
    """{ppid: [pid, ...]} so a subtree walk does not rescan the table per level."""
    kids = {}
    for pid, (ppid, _name) in table.items():
        kids.setdefault(ppid, []).append(pid)
    return kids


def subtree(root, kids, exclude=()):
    """root plus every descendant, skipping any pid in `exclude` and its descendants.

    Windows recycles pids and a parent can die leaving a child reparented, so the walk
    is bounded by a visited set rather than trusting the graph to be a tree.
    """
    out, stack, seen = [], [root], set()
    while stack:
        pid = stack.pop()
        if pid in seen or pid in exclude:
            continue
        seen.add(pid)
        out.append(pid)
        stack.extend(kids.get(pid, ()))
    return out


def tree_cpu(pids):
    """({pid: cpu_100ns}, readable_count, unreadable_count) over a pid list.

    Per-pid, not a total, because the caller has to difference these across samples and
    differencing totals is wrong. See tree_delta.
    """
    per_pid, bad = {}, 0
    for pid in pids:
        value = cpu_100ns(pid)
        if value is None:
            bad += 1
            continue
        per_pid[pid] = value
    return per_pid, len(per_pid), bad


def tree_delta(before, after):
    """(delta_100ns, vanished_pid_count), summed PER PID.

    Found by the first live run on 2026-09-17, which printed cores=-5.9000. Differencing
    two tree TOTALS is wrong the moment the membership changes, and an agent running
    Bash calls churns children constantly: a child that exits between samples takes its
    accumulated CPU out of the sum, so the difference goes negative.

    The negative is the harmless half, because it is obviously wrong. The silent half is
    that the same mechanism UNDERSTATES a busy tree whenever any child exits mid-interval,
    which makes a working agent look idle -- and "looks idle" is precisely what this
    harness reads as blocked. So the bug's quiet form manufactures the false alarms the
    whole experiment exists to eliminate.

    A per-pid CPU counter only increases, so max(0, ...) clamps nothing but a pid-reuse
    artefact. A pid seen for the first time was created inside the interval, so its whole
    total belongs to this interval. A pid that vanished took an unmeasurable slice with
    it; there is no fix for that from outside the process, so it is COUNTED and reported
    on the row rather than left to look like a clean sample.
    """
    delta = 0
    for pid, value in after.items():
        delta += max(0, value - before.get(pid, 0))
    vanished = sum(1 for pid in before if pid not in after)
    return delta, vanished


# ---------------------------------------------------------------------------------
# agent discovery + session attribution
# ---------------------------------------------------------------------------------

RESUME_RE = re.compile(r"--resume[= ]([0-9a-fA-F-]{36})")


def session_ids_by_pid(pids):
    """{pid: session_id} read from the command line, for the pids we can resolve.

    Cold path on purpose. A command line never changes after launch, so this runs when
    the root set changes and never inside the sampling loop -- the hot loop stays pure
    ctypes. Shelling out to CIM avoids a PEB read, and PowerShell is the platform here.

    Only the --resume GUID is extracted. The rest of the command line is never stored,
    logged or printed: it carries the user's flags and would put a path in the CSV.
    """
    if not pids:
        return {}
    query = " or ".join("ProcessId=%d" % pid for pid in pids)
    try:
        raw = subprocess.run(
            [
                "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_Process -Filter \"%s\" | "
                "Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress" % query,
            ],
            capture_output=True, text=True, timeout=30,
        ).stdout
        data = json.loads(raw) if raw.strip() else []
    except (OSError, ValueError, subprocess.SubprocessError):
        return {}
    if isinstance(data, dict):
        data = [data]
    found = {}
    for row in data:
        pid = row.get("ProcessId")
        match = RESUME_RE.search(row.get("CommandLine") or "")
        if pid is not None and match:
            found[int(pid)] = match.group(1)
    return found


def agent_roots(table):
    """[(pid, exe)] for every live agent process."""
    wanted = set(name.lower() for name in AGENT_EXES)
    return sorted(
        (pid, name) for pid, (_ppid, name) in table.items()
        if name.lower() in wanted
    )


# ---------------------------------------------------------------------------------
# transcript state, reused from the probe rather than reimplemented
# ---------------------------------------------------------------------------------

def load_probe():
    """Import agentflow-probe.py despite the hyphen.

    Deliberately not a copy of its pairing logic. That logic already carries a fix for
    a defect this harness would otherwise reproduce (watching only the newest transcript
    silently loses a blocked agent whenever a second session writes more recently), and
    two implementations would drift apart exactly where it matters.
    """
    path = os.path.join(HERE, "agentflow-probe.py")
    spec = importlib.util.spec_from_file_location("agentflow_probe", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("cannot load %s" % path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def tool_call_times(probe, path, since):
    """Epoch timestamps of tool_use records written at or after `since`.

    Timestamps only. The tool name is not collected here and the arguments are never
    touched, so this cannot become a way to read what another session was doing.
    """
    out = []
    for record in probe.read_records(path):
        content = record.get("message", {}).get("content")
        if not isinstance(content, list):
            continue
        if not any(isinstance(b, dict) and b.get("type") == "tool_use" for b in content):
            continue
        stamp = record.get("timestamp")
        if not isinstance(stamp, str):
            continue
        try:
            when = dt.datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp()
        except ValueError:
            continue
        if when >= since:
            out.append(when)
    return out


def align_score(child_times, tool_times, tol):
    """How many child-process creations land within `tol` of some tool_use timestamp.

    The signal: a tool call runs in a CHILD of the agent process, so the child's creation
    time and the transcript's tool_use timestamp are the same event seen from two sides.
    Unlike the agent's own creation time, this does not depend on how long the user took
    to type: it is emitted by the agent's own work, repeatedly, and every additional tool
    call sharpens it.
    """
    hits = 0
    for born in child_times:
        if any(abs(born - when) <= tol for when in tool_times):
            hits += 1
    return hits


def observe_children(roots, seconds, interval=1.0, window=900.0):
    """{root_pid: [child creation epochs]} seen over an observation window.

    Sampled repeatedly rather than once because a tool child is short-lived: a one-shot
    listing sees only the calls running at that instant, and an agent between calls looks
    identical to one that is blocked. Long-lived children (MCP servers, created at
    session start) are filtered by `window` so they do not count as evidence for every
    transcript equally.
    """
    now = time.time()
    seen = dict((pid, {}) for pid, _exe in roots)
    deadline = time.monotonic() + seconds
    while True:
        table = process_table()
        kids = children_of(table)
        for pid, _exe in roots:
            for child in subtree(pid, kids):
                if child == pid or child in seen[pid]:
                    continue
                born = create_time(child)
                if born is not None and now - born <= window:
                    seen[pid][child] = born
        if time.monotonic() >= deadline:
            break
        time.sleep(interval)
    return dict((pid, sorted(times.values())) for pid, times in seen.items())


def active_transcripts(probe):
    """{session_id: path} for every recently-written Claude transcript."""
    out = {}
    for path in probe.active_jsonls(probe.CLAUDE_ROOT, ("subagents",)):
        out[os.path.splitext(os.path.basename(path))[0]] = path
    return out


def attribute_by_activity(evidence, candidates, probe, tol=3.0, window=900.0,
                          unsupported=()):
    """{root_pid: (session_id | None, note)} from child-creation / tool_use alignment.

    Deliberately refuses rather than guesses. A wrong session id is worse than no session
    id: it is the newest-file defect again, where a detection attributed to the wrong
    session read as a clean result. So a match needs a positive score AND a strict margin
    over the runner-up, and anything else reports None with the reason.
    """
    since = time.time() - window
    tool_times = dict(
        (sid, tool_call_times(probe, path, since)) for sid, path in candidates.items()
    )
    result = {}
    for pid, child_times in evidence.items():
        if unsupported and pid in unsupported:
            # Scoring a codex.exe tree against CLAUDE transcripts is a mis-attribution
            # path by construction: a coincidental alignment would file a Codex tree
            # under a Claude session. Codex pairs by call_id in a different record
            # shape, so it needs its own candidate index rather than this one.
            result[pid] = (None, "codex not indexed (would score against claude only)")
            continue
        if not child_times:
            result[pid] = (None, "no child processes observed")
            continue
        scored = sorted(
            ((align_score(child_times, times, tol), sid)
             for sid, times in tool_times.items()),
            reverse=True,
        )
        if not scored or scored[0][0] == 0:
            result[pid] = (None, "no tool_use aligned with %d child creations"
                           % len(child_times))
            continue
        best, sid = scored[0]
        runner = scored[1][0] if len(scored) > 1 else 0
        if best <= runner:
            result[pid] = (None, "ambiguous: %d hits tied with runner-up" % best)
            continue
        result[pid] = (sid, "%d/%d children aligned (runner-up %d)"
                       % (best, len(child_times), runner))
    return result


def transcript_state(probe, session_id):
    """(pending_count, tool_names, idle_seconds) for one session, or None if unseen."""
    paths = probe.active_jsonls(probe.CLAUDE_ROOT, ("subagents",))
    for path in paths:
        if session_id and os.path.basename(path).startswith(session_id):
            pending, saw_any = probe.pending_claude(path)
            try:
                idle = time.time() - os.path.getmtime(path)
            except OSError:
                return None
            if not saw_any:
                return (-1, "adapter-stale", idle)
            return (len(pending), ",".join(sorted(set(pending.values()))), idle)
    return None


# ---------------------------------------------------------------------------------
# the mechanism check
# ---------------------------------------------------------------------------------

def check_delta():
    """Deterministic cases for tree_delta, because the live defect is not reproducible.

    The churn that produced cores=-5.9 depends on which child happened to exit inside
    which interval, so it cannot be triggered on demand and a live run that happens to
    look clean proves nothing. These are the same situation as fixed dicts. Case 1 is
    the regression: tree-total differencing returns -450 for it.
    """
    cases = (
        ("a child exited: its CPU leaves the sum, delta stays positive",
         {1: 100, 2: 500}, {1: 150}, 50, 1),
        ("a child appeared: its whole total is inside the interval",
         {1: 100}, {1: 150, 2: 70}, 120, 0),
        ("a per-pid counter going backwards is clamped, never negative",
         {1: 100}, {1: 90}, 0, 0),
        ("nothing changed", {1: 100}, {1: 100}, 0, 0),
    )
    failures = 0
    for name, before, after, want_delta, want_gone in cases:
        delta, gone = tree_delta(before, after)
        if delta != want_delta or gone != want_gone:
            print("  FAIL delta: %s (got %d/%d, want %d/%d)"
                  % (name, delta, gone, want_delta, want_gone))
            failures += 1
        else:
            print("  PASS delta: %s" % name)
    return failures


def verify(seconds=3.0):
    """Prove the tree sees a child's CPU and the bare process does not.

    This is the harness's own witness. Without it a run of all-zero samples reads as
    "every agent was idle" when it may mean the sampler cannot open a handle, picked the
    wrong root, or summed an empty pid list. A check that cannot fail is not a check, so
    this one asserts a floor on the tree and a ceiling on the process, and returns
    non-zero when either misses.
    """
    delta_failures = check_delta()
    print("verify: burning CPU in a CHILD process for %.1fs" % seconds)
    burner = subprocess.Popen(
        [sys.executable, "-c", "\nwhile True:\n    pass\n"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    try:
        me = os.getpid()
        table = process_table()
        kids = children_of(table)
        if burner.pid not in subtree(me, kids):
            print("  FAIL the burner is not in this process's subtree "
                  "(pid %d not under %d)" % (burner.pid, me))
            return 1

        tree_before, _, _ = tree_cpu(subtree(me, kids))
        self_before = cpu_100ns(me)
        wall_before = time.monotonic()
        time.sleep(seconds)
        table = process_table()
        kids = children_of(table)
        tree_after, ok, bad = tree_cpu(subtree(me, kids))
        self_after = cpu_100ns(me)
        wall = time.monotonic() - wall_before

        if self_before is None or self_after is None:
            print("  FAIL cannot read this process's own CPU times")
            return 1
        delta, _gone = tree_delta(tree_before, tree_after)
        tree_cores = delta / (wall * 1e7)
        self_cores = (self_after - self_before) / (wall * 1e7)
        print("  tree    %.3f cores busy over %.1fs (%d pids read, %d unreadable)"
              % (tree_cores, wall, ok, bad))
        print("  process %.3f cores busy over %.1fs (this pid alone)"
              % (self_cores, wall))

        failures = delta_failures
        # A single spinning child pegs one core, so anything below half a core means the
        # descendant walk or the handle open is broken, not that the child was idle.
        if tree_cores < 0.5:
            print("  FAIL tree CPU missed a child burning a full core "
                  "-- the subtree walk or OpenProcess is broken")
            failures += 1
        else:
            print("  PASS tree CPU captured the child's burn")
        # The thesis: bare process CPU does NOT see it. If this ever fails, the premise
        # for tree-over-process is gone and the doc's reasoning needs revisiting.
        if self_cores > 0.25:
            print("  FAIL this process itself burned %.3f cores -- the sampler is too "
                  "expensive to be measured alongside an agent" % self_cores)
            failures += 1
        else:
            print("  PASS bare process CPU did NOT see the child "
                  "(this is why the tree is the unit)")
        # Exclusion has to work or the sampler measures itself and every agent reads busy.
        excluded, _, _ = tree_cpu(subtree(me, kids, exclude=(burner.pid,)))
        if sum(excluded.values()) >= sum(tree_after.values()):
            print("  FAIL excluding the burner did not reduce the tree total")
            failures += 1
        else:
            print("  PASS exclusion removes a subtree from the total")
        print("%s" % ("verify OK" if not failures else "verify FAILED (%d)" % failures))
        return 1 if failures else 0
    finally:
        burner.kill()
        burner.wait(timeout=5)


# ---------------------------------------------------------------------------------
# sampling
# ---------------------------------------------------------------------------------

FIELDS = ("wall", "root_pid", "exe", "session", "tree_pids", "unreadable",
          "vanished", "cores", "pending", "tools", "transcript_idle")


def sample_loop(args):
    probe = load_probe()
    me = os.getpid()
    writer, handle = None, None
    if args.csv:
        handle = open(args.csv, "a", newline="", encoding="utf-8")
        writer = csv.DictWriter(handle, fieldnames=FIELDS)
        if handle.tell() == 0:
            writer.writeheader()

    last = {}          # root pid -> (cpu_100ns, monotonic)
    sessions = {}      # root pid -> session id
    known_roots = set()
    taken = 0
    print("sampling every %.1fs (ctrl-c to stop)%s"
          % (args.interval, "; writing " + args.csv if args.csv else ""))
    try:
        while True:
            table = process_table()
            kids = children_of(table)
            mine = set(subtree(me, kids))   # never measure the sampler's own work
            roots = agent_roots(table)
            root_pids = set(pid for pid, _ in roots)
            if root_pids != known_roots:
                # Cold path: only when an agent started or exited.
                sessions.update(session_ids_by_pid(sorted(root_pids - known_roots)))
                known_roots = root_pids

            now = time.monotonic()
            for pid, exe in roots:
                pids = subtree(pid, kids, exclude=mine)
                per_pid, ok, bad = tree_cpu(pids)
                cores, vanished = "", ""
                previous = last.get(pid)
                if previous:
                    before, when = previous
                    span = now - when
                    if span > 0:
                        delta, gone = tree_delta(before, per_pid)
                        cores = "%.4f" % (delta / (span * 1e7))
                        vanished = gone
                last[pid] = (per_pid, now)

                session = sessions.get(pid, "")
                state = transcript_state(probe, session) if session else None
                pending = state[0] if state else ""
                tools = state[1] if state else ""
                idle = "%.1f" % state[2] if state else ""

                row = {
                    "wall": "%.3f" % time.time(),
                    "root_pid": pid,
                    "exe": exe,
                    "session": session[:8] if session else "?",
                    "tree_pids": ok,
                    "unreadable": bad,
                    "vanished": vanished,
                    "cores": cores,
                    "pending": pending,
                    "tools": tools,
                    "transcript_idle": idle,
                }
                if writer:
                    writer.writerow(row)
                if not args.quiet:
                    print("%-12s pid=%-6d s=%-8s cores=%-7s pids=%-3d gone=%-3s "
                          "pending=%-3s idle=%-6s %s"
                          % (exe, pid, row["session"], cores or "-", ok,
                             str(vanished) if vanished != "" else "-",
                             str(pending), idle or "-", tools))
            if handle:
                handle.flush()
            taken += 1
            if args.once or (args.count and taken >= args.count):
                return 0
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0
    finally:
        if handle:
            handle.close()


def own_root(table):
    """The agent process this script is running under, or None.

    Used to EXCLUDE that session from validation. Its tool child is the sampler itself,
    so it would be attributed trivially and inflate the score with the one case that is
    never representative of what the matcher has to do in production.
    """
    pid = os.getpid()
    seen = set()
    while pid in table and pid not in seen:
        seen.add(pid)
        ppid, name = table[pid]
        if name.lower() in set(n.lower() for n in AGENT_EXES):
            return pid
        pid = ppid
    return None


def attribute_mode(args):
    probe = load_probe()
    table = process_table()
    roots = agent_roots(table)
    resumed = session_ids_by_pid([pid for pid, _ in roots])
    print("observing child processes for %.0fs across %d agent roots"
          % (args.observe, len(roots)))
    evidence = observe_children(roots, args.observe)
    candidates = active_transcripts(probe)
    unclaimed = dict((sid, path) for sid, path in candidates.items()
                     if sid not in set(resumed.values()))
    non_claude = set(pid for pid, exe in roots if exe.lower() != "claude.exe")
    guessed = attribute_by_activity(
        dict((pid, times) for pid, times in evidence.items() if pid not in resumed),
        unclaimed, probe, unsupported=non_claude)

    print("\n%-11s %-7s %-10s %-9s %s" % ("exe", "pid", "session", "method", "note"))
    for pid, exe in roots:
        if pid in resumed:
            print("%-11s %-7d %-10s %-9s %s"
                  % (exe, pid, resumed[pid][:8], "--resume", "exact"))
        else:
            sid, note = guessed.get(pid, (None, "not scored"))
            print("%-11s %-7d %-10s %-9s %s"
                  % (exe, pid, sid[:8] if sid else "?", "activity", note))
    return 0


def validate_attribution(args):
    """Grade the activity matcher against sessions whose id is already known.

    Ground truth is `--resume`, so this hides it and asks whether child-creation
    alignment recovers the same answer. A matcher nobody graded is a guess with a
    confidence interval attached, and the failure it would produce -- a CPU sample filed
    under the wrong session -- is invisible in the output.
    """
    probe = load_probe()
    table = process_table()
    roots = agent_roots(table)
    resumed = session_ids_by_pid([pid for pid, _ in roots])
    mine = own_root(table)
    graded = [(pid, exe) for pid, exe in roots
              if pid in resumed and pid != mine]

    if not graded:
        print("nothing to grade: no resumed agent besides this session's own.")
        print("Start or resume another session and rerun; a 0/0 pass is not a pass.")
        return 1

    print("grading %d resumed session(s) over %.0fs (own root %s excluded)"
          % (len(graded), args.observe, mine))
    observed_from = time.time()
    evidence = observe_children(graded, args.observe)
    candidates = active_transcripts(probe)
    guessed = attribute_by_activity(evidence, candidates, probe)

    # A discrimination test needs something to discriminate AGAINST. If only one
    # transcript ran tools in the window, every busy tree aligns to it and a CORRECT
    # verdict is arithmetic, not evidence -- the degenerate-axis failure this project
    # has already been bitten by once. Say so on every run, loudly, and refuse to
    # call it a pass.
    # Measured over the OBSERVATION window, not some wider one: alignment discriminates
    # only among transcripts that emitted a tool call while children were being watched.
    # A transcript busy ten minutes ago is not a distractor for a child born just now.
    busy = [sid for sid, path in candidates.items()
            if tool_call_times(probe, path, observed_from)]
    print("\ntest population: %d active transcript(s), %d emitted a tool call during "
          "the %.0fs observation" % (len(candidates), len(busy), args.observe))
    degenerate = len(busy) < 2
    if degenerate:
        print("DEGRADED: fewer than two transcripts ran tools while children were being")
        print("          watched, so alignment cannot discriminate -- any busy tree")
        print("          matches the only busy transcript by arithmetic.")

    right = wrong = abstain = 0
    for pid, exe in graded:
        truth = resumed[pid]
        sid, note = guessed.get(pid, (None, "not scored"))
        if sid is None:
            verdict, abstain = "ABSTAIN", abstain + 1
        elif sid == truth:
            verdict, right = "CORRECT", right + 1
        else:
            verdict, wrong = "WRONG", wrong + 1
        print("  %-9s %-11s pid=%-7d truth=%s got=%-9s %s"
              % (verdict, exe, pid, truth[:8], sid[:8] if sid else "-", note))

    print("\n%d correct, %d WRONG, %d abstained, of %d"
          % (right, wrong, abstain, len(graded)))
    print("An abstain is a working answer: the sample is filed under '?' and dropped,")
    print("which costs recall. A WRONG files one session's CPU under another and is the")
    print("only outcome that corrupts the measurement, so it is the number that gates.")

    # The other half of the honesty problem: the truth this grades against is itself
    # the LAUNCH session from the command line, exactly like --permission-mode is the
    # launch mode. Whether it goes stale when a panel is reused has not been tested, so
    # a disagreement here is ambiguous between a bad matcher and bad ground truth.
    print("\nGround truth is `--resume`, which is LAUNCH state. If a panel can be handed")
    print("a new session without relaunching, a WRONG above may be the truth being stale")
    print("rather than the matcher being broken. Untested either way; do not read a")
    print("verdict here as settling the matcher until that is resolved.")
    if degenerate:
        print("\nNOT A PASS: the population was degenerate (see DEGRADED above).")
        return 1
    return 1 if wrong else 0


def report(path):
    """Split captured samples by transcript state. Separation, not precision.

    Precision needs labels (which outstanding calls were REAL prompts), and those come
    from toolDenialKind in the transcript, which agentflow_join.py already reads. This
    only answers the prior question: are the two populations even different?
    """
    buckets = {"outstanding": [], "clear": []}
    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if not row.get("cores"):
                continue
            try:
                cores = float(row["cores"])
                pending = int(row["pending"]) if row.get("pending") else 0
            except ValueError:
                continue
            buckets["outstanding" if pending > 0 else "clear"].append(cores)

    print("%-12s %8s %8s %8s %8s" % ("bucket", "n", "median", "p90", "frac<0.05"))
    for name, values in buckets.items():
        if not values:
            print("%-12s %8d %8s %8s %8s" % (name, 0, "-", "-", "-"))
            continue
        values.sort()
        median = values[len(values) // 2]
        p90 = values[min(len(values) - 1, int(len(values) * 0.9))]
        quiet = sum(1 for value in values if value < 0.05) / float(len(values))
        print("%-12s %8d %8.4f %8.4f %8.1f%%"
              % (name, len(values), median, p90, quiet * 100))
    print("\nn is SAMPLES, not prompts. A clean separation here is necessary and not")
    print("sufficient: the 450:1 false-alarm rate lives in the outstanding bucket, so")
    print("what matters is how often a QUIET tree sits under an outstanding call that")
    print("was never a prompt. That needs the denial labels from agentflow_join.py.")
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--verify", action="store_true",
                        help="prove tree CPU sees a child's burn and process CPU does not")
    parser.add_argument("--report", metavar="CSV",
                        help="summarise a captured CSV by transcript state")
    parser.add_argument("--attribute", action="store_true",
                        help="map every live agent tree to a session id")
    parser.add_argument("--validate", action="store_true",
                        help="grade the activity matcher against known --resume ids")
    parser.add_argument("--observe", type=float, default=20.0,
                        help="seconds to watch for child processes when attributing")
    parser.add_argument("--interval", type=float, default=2.0)
    parser.add_argument("--csv", help="append samples here")
    parser.add_argument("--count", type=int, default=0,
                        help="stop after N samples (0 = until interrupted)")
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--quiet", action="store_true", help="CSV only, no console rows")
    args = parser.parse_args()

    if sys.platform != "win32":
        print("windows only: this reads CPU times through kernel32", file=sys.stderr)
        return 2
    if args.verify:
        return verify()
    if args.report:
        return report(args.report)
    if args.validate:
        return validate_attribution(args)
    if args.attribute:
        return attribute_mode(args)
    return sample_loop(args)


if __name__ == "__main__":
    sys.exit(main())
