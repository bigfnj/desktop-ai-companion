"""Record free-running GUI demonstrations on an isolated copy of each runtime.

Windows with pywin32, psutil and FFmpeg. Supply the official distribution zip, a
JDK 8 javaw.exe, a portable host build folder, a converter EXE and a scenario/1.
The output directory must be new. Pass --region X Y WIDTH HEIGHT for a tighter
camera inside the primary work area. The unlocked session switches briefly to
a fresh desktop for each take. Default must have been idle for 60 seconds.
An independent watchdog restores it if the controller exits, input resumes
or a take lasts 55 seconds. No mouse, keyboard, or audio capture.
"""

import argparse
import ctypes
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import zipfile
from collections import Counter
from contextlib import ExitStack, contextmanager
from pathlib import Path

import psutil
import win32api
import win32event
import win32gui
import win32job
import win32service

# Running this file with isolated Python excludes the script directory.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from recording_desktop import (
    Backdrop,
    RecordingUnavailable,
    changes,
    preflight,
    windows,
)
from recording_overlap import stage_overlap
from recording_session import (
    DesktopProcess,
    before_switch,
    fresh_desktop,
    input_desktop,
    open_original,
    restore_default,
    stop_recording_job,
    wait_file,
)

DISTRIBUTION_SHA = "fee19b4003d718fab7ddd3ac6955bd4c1228845f94f8a1f9d72da12571a2a3d6"
NO_CONSOLE = subprocess.CREATE_NO_WINDOW
LABELS = ("real Shimeji-EE 1.0.23 (real windows)", "Desktop AI Companion (real windows)")


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, obj):
    pending = path.with_suffix(path.suffix + ".partial")
    pending.write_text(json.dumps(obj, indent=2, ensure_ascii=False), encoding="utf-8")
    pending.replace(path)


def contain_children(name=None):
    job = win32job.CreateJobObject(None, name or "ShimejiRecording-" + uuid.uuid4().hex)
    info = win32job.QueryInformationJobObject(job, win32job.JobObjectExtendedLimitInformation)
    info["BasicLimitInformation"]["LimitFlags"] |= win32job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    win32job.SetInformationJobObject(job, win32job.JobObjectExtendedLimitInformation, info)
    win32job.AssignProcessToJobObject(job, win32api.GetCurrentProcess())
    # Keep the handle until OS process teardown, after the report and exit code.
    # Descendants stay in this recording job even if the harness is interrupted.
    return job.Detach()


def start(args, cwd, env, log, ledger):
    if env.get("SHIMEJI_RECORD_DESKTOP"):
        proc = DesktopProcess(args, cwd, env, log, env["SHIMEJI_RECORD_DESKTOP"])
    else:
        proc = subprocess.Popen(
            [str(arg) for arg in args],
            cwd=cwd,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
            creationflags=NO_CONSOLE,
        )
    ledger.append({"pid": proc.pid, "executable": Path(args[0]).name, "exited": False})
    return proc


def stop(proc, ledger):
    if proc is None:
        return
    if proc.poll() is None:
        proc.terminate()
    try:
        proc.wait(timeout=6)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=6)
    record = next(item for item in reversed(ledger) if item["pid"] == proc.pid)
    record.update(exited=proc.poll() is not None, exitCode=proc.returncode)


def read_command(args, cwd, env, ledger, timeout=30):
    with tempfile.TemporaryFile(dir=cwd) as output:
        proc = start(args, cwd, env, output, ledger)
        try:
            if proc.wait(timeout):
                raise RecordingUnavailable(f"{Path(args[0]).name} failed with exit {proc.returncode}")
            output.seek(0)
            return output.read()
        finally:
            stop(proc, ledger)


def stack_filter():
    panels = [
        f"[{i}:v]scale=-2:480,pad=iw:ih+44:0:44:color=0x20252b,"
        f"drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='{label}':x=14:y=8:fontsize=22:fontcolor=white[v{i}]"
        for i, label in enumerate(LABELS)
    ]
    combined = (
        "[v0][v1]hstack=inputs=2,pad=iw:ih+24:0:24:color=0x20252b,"
        "drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='Free-running demonstration | ungraded':"
        "x=14:y=3:fontsize=15:fontcolor=white[v]"
    )
    return ";".join(panels + [combined])


def check_overlap(ffmpeg, clip, region, audit, scratch, env, ledger):
    # The original asymmetric drawings can face either way. This 4px patch is
    # solid member color for every pose of either member and both orientations.
    x = region[2] // 2 - 1
    y = region[3] - 14
    raw = read_command(
        [
            ffmpeg,
            "-nostdin",
            "-v",
            "error",
            "-i",
            clip,
            "-vf",
            f"crop=4:4:{x - 2}:{y - 2},scale=1:1,format=rgb24",
            "-f",
            "rawvideo",
            "pipe:1",
        ],
        scratch,
        env,
        ledger,
    )
    colors = []
    for i in range(0, len(raw), 3):
        red, green, blue = raw[i : i + 3]
        if green > red + 45 and green > blue + 35:
            colors.append("green")
        elif blue > red + 80 and blue > green + 45:
            colors.append("blue")
        else:
            colors.append("other")
    green = next((i for i in range(len(colors) - 1) if colors[i : i + 2] == ["green"] * 2), None)
    blue = next((i for i in range((green or 0) + 2, len(colors) - 1) if colors[i : i + 2] == ["blue"] * 2), None)
    orders = set()
    for sample in audit["runtimeWindows"]:
        covers = [
            hwnd
            for hwnd, x1, y1, x2, y2 in sample["windows"]
            if x1 <= region[0] + x < x2 and y1 <= region[1] + y < y2 and x2 - x1 <= 256 and y2 - y1 <= 256
        ]
        if len(covers) == 2:
            orders.add(tuple(covers))
    swapped = any((b, a) in orders for a, b in orders)
    audit["overlapWitness"] = {
        "cameraPixel": [x, y],
        "commonRGB": [
            (value.hex(), count)
            for value, count in Counter(raw[i : i + 3] for i in range(0, len(raw), 3)).most_common(8)
        ],
        "firstGreenFrame": green,
        "followingBlueFrame": blue,
        "twoWindowsReversed": swapped,
        "sampledColors": dict(Counter(colors)),
    }
    if green is None or blue is None or not swapped:
        raise RecordingUnavailable(
            "overlap did not show the green-to-blue cover change and a two-window order reversal"
        )


def prepare(args, scratch, scene, report, desktop=None):
    pack = (args.scenario.parent / scene["pack"]).resolve(strict=True)
    if not pack.is_relative_to(args.scenario.parent) or not (pack / "img").is_dir():
        raise ValueError("scenario pack must be a source pack inside the scenario directory")
    if scene["id"] == "S-p2-bringtofront-overlap-01":
        staged_pack = scratch / "overlap-fixture"
        report["staging"] = stage_overlap(pack, staged_pack, report["region"])
        pack = staged_pack
    if digest(args.distribution) != DISTRIBUTION_SHA:
        raise ValueError("official distribution SHA-256 mismatch")
    release = (args.java.parent.parent / "release").read_text("ascii")
    if 'JAVA_VERSION="1.8.' not in release or args.java.name.lower() != "javaw.exe":
        raise ValueError("supply the JDK 8 javaw.exe")
    source = scratch / "source"
    source.mkdir()
    # Extract only runtime assets. conf/ and img/ in the test pack overlay the
    # application's normal bundled defaults. Its original jar stays byte-identical.
    with zipfile.ZipFile(args.distribution) as archive:
        for entry in archive.infolist():
            parts = Path(entry.filename).parts
            if not parts or parts[0] not in ("conf", "lib", "img", "Shimeji-ee.jar") or entry.is_dir():
                continue
            dest = (source / entry.filename).resolve()
            if not dest.is_relative_to(source):
                raise ValueError("distribution path escapes staging")
            dest.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as content, dest.open("xb") as output:
                shutil.copyfileobj(content, output)
    for name in ("conf", "img"):
        if (pack / name).is_dir():
            shutil.copytree(pack / name, source / name, dirs_exist_ok=True)
    members = [m["imageSet"] for m in scene["mascots"]]
    if any("/" in member or "\n" in member or "\r" in member or "\\" in member for member in members):
        raise ValueError("image set cannot be represented in ActiveShimeji")
    selection = "/".join(members)
    # Both WindowsEnvironment lists empty makes every candidate ineligible.
    props = {
        "Throwing": "false",
        "Sounds": "false",
        "InteractiveWindows": "",
        "InteractiveWindowsBlacklist": "",
        "ActiveShimeji": selection,
        "InformationDismissed": selection,
        "Multiscreen": "false",
        "AlwaysShowShimejiChooser": "false",
        "AlwaysShowInformationScreen": "false",
        "Scaling": "1.0",
    }

    def property_value(value):
        units = value.encode("utf-16-be")
        return "".join("\\u" + units[i : i + 2].hex() for i in range(0, len(units), 2))

    (source / "conf/settings.properties").write_text(
        "".join(f"{k}={property_value(v)}\n" for k, v in props.items()), encoding="ascii"
    )
    userhome = scratch / "java-home"
    userhome.mkdir()
    runtime = scratch / "portable"
    runtime.mkdir()
    for file in args.host_bin.iterdir():
        if file.is_file() and file.suffix.lower() in (".exe", ".dll", ".json", ".config"):
            shutil.copy2(file, runtime / file.name)
    exe = runtime / "DesktopAICompanion.exe"
    if digest(exe) != digest(args.host_bin / exe.name):
        raise ValueError("portable executable copy mismatch")
    (runtime / "DesktopAICompanion.portable").touch()
    data = scratch / "host-data"
    data.mkdir()
    library = data / "companions"
    temp = scratch / "temp"
    temp.mkdir()
    env = os.environ | {
        "DESKTOP_AI_COMPANION_DATA_ROOT": str(data),
        "DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT": "1",
        "TMP": str(temp),
        "TEMP": str(temp),
    }
    if desktop:
        env["SHIMEJI_RECORD_DESKTOP"] = desktop
    with (scratch / "converter.log").open("wb") as log:
        converter = start(
            [args.converter, "paritypack", pack, library, members[0]], scratch, env, log, report["processes"]
        )
        try:
            if converter.wait(timeout=120):
                raise ValueError("converter refused test pack")
        finally:
            stop(converter, report["processes"])
    available = [d.name for d in library.iterdir() if (d / "animations.xml").is_file()]
    if not set(members).issubset(available):
        raise ValueError("converted starter member is missing")
    write_json(
        data / "settings.json",
        {
            "schemaVersion": 2,
            "activeCompanionId": members[0],
            "companions": [{"id": m, "count": n} for m, n in Counter(members).items()],
            "autoStartCompanions": len(members),
            "multiScreen": False,
            "scalePercent": 100,
            "speechEnabled": False,
            "windowForeground": True,
            "stealTaskbarFocus": False,
            "monthlyModuleUpdateCheck": False,
            "appUpdateCheck": False,
            "companionUpdateCheck": False,
            "diagnosticLog": True,
            "companionSoundsEnabled": False,
            "notificationSoundsEnabled": False,
            "randomDropEnabled": False,
            "companionSwitches": [{"id": m, "key": "WindowActions", "on": False} for m in available],
        },
    )
    report["artifacts"] = {
        "distributionSha256": DISTRIBUTION_SHA,
        "jarSha256": digest(source / "Shimeji-ee.jar"),
        "hostExeSha256": digest(exe),
        "hostEngineSha256": digest(runtime / "DesktopAICompanion.dll"),
        "converterSha256": digest(args.converter),
        "scenarioSha256": digest(args.scenario),
    }
    return env


def runtime_launch(role, java, scratch):
    if role == "source":
        return [
            java,
            "-Duser.home=" + str(scratch / "java-home"),
            "-Djava.io.tmpdir=" + str(scratch / "temp"),
            "-jar",
            "Shimeji-ee.jar",
        ], scratch / "source"
    return [scratch / "portable" / "DesktopAICompanion.exe"], scratch / "portable"


def checked_region(chosen, expected_desktop="Default"):
    x, y, w, h = preflight(expected_desktop)
    if chosen is None:
        return (x, y, w, h)
    left, top, width, height = chosen
    if (
        width < 2
        or height < 2
        or width % 2
        or height % 2
        or left < x
        or top < y
        or left + width > x + w
        or top + height > y + h
    ):
        raise RecordingUnavailable("recording region outside the work area or not even-sized")
    return tuple(chosen)


def capture(args, region, role, launch, env, scratch, audit, ledger, backdrop):
    label = env["SHIMEJI_RECORD_DESKTOP"]
    if checked_region(region, label) != region:
        raise RecordingUnavailable("recording region changed")
    x, y, w, h = region
    dest = scratch / (role + ".mp4")
    recorder = runtime = None
    samples = []
    with (
        (scratch / (role + "-gui.log")).open("wb") as gui_log,
        (scratch / (role + "-video.log")).open("wb") as video_log,
    ):
        win32gui.PumpWaitingMessages()
        before = windows()
        audit["before"] = before
        try:
            backdrop.visible(before, None)
            checked_region(region, label)
            recorder = start(
                [
                    args.ffmpeg,
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-f",
                    "gdigrab",
                    "-framerate",
                    "25",
                    "-draw_mouse",
                    "0",
                    "-offset_x",
                    str(x),
                    "-offset_y",
                    str(y),
                    "-video_size",
                    f"{w}x{h}",
                    "-i",
                    "desktop",
                    "-an",
                    "-t",
                    str(args.seconds),
                    "-vf",
                    "fps=25",
                    "-c:v",
                    "libx264",
                    "-preset",
                    "ultrafast",
                    "-threads",
                    "4",
                    "-pix_fmt",
                    "yuv420p",
                    dest,
                ],
                scratch,
                env,
                video_log,
                ledger,
            )
            runtime = start(*launch, env, gui_log, ledger)
            audit["runtimePid"] = runtime.pid
            started = time.monotonic()
            next_check = started
            while recorder.poll() is None:
                now = time.monotonic()
                if now - started > args.seconds + 20:
                    raise RecordingUnavailable("recorder exceeded deadline")
                if runtime.poll() is not None:
                    raise RecordingUnavailable("GUI process exited during capture")
                if now >= next_check:
                    checked_region(region, label)
                    next_check = now + 1
                win32gui.PumpWaitingMessages()
                visible = backdrop.visible(windows(), runtime.pid)
                if visible:
                    # Window handles and bounds in top-to-bottom order, without user window titles.
                    layout = [[*row] for row in visible]
                    if not samples or samples[-1]["windows"] != layout:
                        samples.append({"seconds": round(now - started, 3), "windows": layout})
                time.sleep(0.06)
            if recorder.returncode != 0 or not samples:
                raise RecordingUnavailable("recording failed or no runtime window was observed")
        finally:
            try:
                after = windows()
            finally:
                try:
                    stop(runtime, ledger)
                finally:
                    stop(recorder, ledger)
            finished = windows()
            owned = {os.getpid(), *(proc.pid for proc in (recorder, runtime) if proc)}
            audit.update(
                after=after,
                afterExit=finished,
                runtimeWindows=samples,
                beforeAfterChanges=changes(before, after, owned),
                afterExitChanges=changes(before, finished, owned),
            )
            if audit["beforeAfterChanges"] or audit["afterExitChanges"]:
                raise RecordingUnavailable("another process's top-level window set or rectangle changed")
    return dest


def worker_command(mode, directory):
    return [sys.executable, "-I", "-B", Path(__file__).resolve(), mode, directory]


def thread_desktop():
    return win32service.GetUserObjectInformation(win32service.GetThreadDesktop(win32api.GetCurrentThreadId()), 2)


def witness(directory):
    region = tuple(json.loads((directory / "spec.json").read_text("utf-8"))["region"])
    until = time.monotonic() + 50
    # This thread only paints a solid window. Creating IME/text-input UI here
    # would add independently moving language-bar windows to the witness.
    imm32 = ctypes.WinDLL("imm32")
    for disable in (imm32.ImmDisableTextFrameService, imm32.ImmDisableIME):
        disable.argtypes = (ctypes.c_uint32,)
        disable.restype = ctypes.c_int
        disable(0)  # calling thread only
    with Backdrop(region) as backdrop:
        write_json(
            directory / "witness.json", {"hwnd": backdrop.window, "pid": os.getpid(), "desktop": thread_desktop()}
        )
        while time.monotonic() < until and not (directory / "finish-witness").exists():
            win32gui.PumpWaitingMessages()
            time.sleep(0.025)
    return 0


def take_worker(directory):
    spec = json.loads((directory / "spec.json").read_text("utf-8"))
    scratch, role, label = directory.parent, spec["role"], os.environ["SHIMEJI_RECORD_DESKTOP"]
    if role not in ("source", "host") or not label.startswith("ShimejiRecording-"):
        raise ValueError("invalid recording worker configuration")
    recording_job = contain_children(label + "-" + role)
    region = tuple(spec["region"])
    audit = {"processes": []}
    ledger = audit["processes"]
    try:
        with (directory / "witness.log").open("wb") as log:
            child = start(worker_command("--witness", directory), scratch, os.environ.copy(), log, ledger)
            try:
                wait_file(directory / "witness.json", child)
                witness_record = json.loads((directory / "witness.json").read_text("utf-8"))
                win = witness_record["hwnd"]
                x, y, w, h = region
                witness_pid = witness_record["pid"]
                expected = (witness_pid, x, y, x + w, y + h)
                actual = windows().get(win)
                audit["witnessClassesBeforeSwitch"] = [
                    win32gui.GetClassName(hwnd) for hwnd, window in windows().items() if window[0] == witness_pid
                ]
                audit["stationaryWitness"] = witness_record | {
                    "launcherPid": child.pid,
                    "workerDesktop": thread_desktop(),
                    "rect": expected[1:],
                    "observed": actual,
                }
                # A Windows venv launcher runs the Python interpreter as its own child.
                if witness_pid != child.pid and psutil.Process(witness_pid).ppid() != child.pid:
                    raise RecordingUnavailable("witness process does not belong to its launcher")
                if witness_record["desktop"] != label or thread_desktop() != label or actual != expected:
                    raise RecordingUnavailable("independent window missing on the requested desktop")
                backdrop = Backdrop(region)
                backdrop.window = win
                (directory / "worker-ready").touch()
                until = time.monotonic() + 8
                while input_desktop() != label:
                    if time.monotonic() >= until:
                        raise RecordingUnavailable("private desktop did not become active")
                    time.sleep(0.015)
                args = argparse.Namespace(seconds=spec["seconds"], ffmpeg=spec["ffmpeg"])
                capture(
                    args,
                    region,
                    role,
                    runtime_launch(role, spec["java"], scratch),
                    os.environ.copy(),
                    scratch,
                    audit,
                    ledger,
                    backdrop,
                )
                if any(audit[s].get(win) != expected for s in ("before", "after", "afterExit")):
                    raise RecordingUnavailable("independent window changed during recording")
                audit["stationaryWitness"]["verified"] = True
            finally:
                (directory / "finish-witness").touch()
                try:
                    child.wait(2)
                finally:
                    stop(child, ledger)
    except (
        RecordingUnavailable,
        OSError,
        ValueError,
        KeyError,
        win32api.error,
        subprocess.SubprocessError,
        psutil.Error,
    ) as error:
        audit["reason"] = f"{type(error).__name__}: {error}"
    finally:
        end = time.monotonic() + 1
        while True:
            live = [
                p
                for p in win32job.QueryInformationJobObject(recording_job, win32job.JobObjectBasicProcessIdList)
                if p != os.getpid()
            ]
            if not live or time.monotonic() >= end:
                break
            time.sleep(0.04)
        audit["liveOwnedPids"] = live
        audit["liveOwnedImages"] = {p: psutil.Process(p).name() for p in live if psutil.pid_exists(p)}
        write_json(directory / "audit.json", audit)
    return 3 if audit.get("reason") or audit["liveOwnedPids"] else 0


@contextmanager
def private_session(directory, report):
    with ExitStack() as stack:
        mutex = win32event.CreateMutex(None, False, r"Local\ShimejiRealWindowRecording")
        stack.callback(mutex.Close)
        if win32event.WaitForSingleObject(mutex, 0) not in (0, 0x80):
            raise RecordingUnavailable("another recording is active")
        stack.callback(win32event.ReleaseMutex, mutex)
        before_switch()
        original = open_original()
        stack.callback(original.CloseDesktop)
        label, desktop = fresh_desktop()
        stack.callback(desktop.CloseDesktop)
        with (directory / "watchdog.log").open("wb") as log:
            command = [
                sys.executable,
                "-I",
                "-B",
                Path(__file__).with_name("recording_session.py"),
                "watch",
                directory,
                label,
                os.getpid(),
            ]
            watchdog = DesktopProcess(command, directory, os.environ.copy(), log, "Default")
            try:
                wait_file(directory / "watch-ready", watchdog)
                job = contain_children()
                if win32job.IsProcessInJob(watchdog.handle, job):
                    raise RecordingUnavailable("watchdog shares the recorder job")
                report["desktop"] = {
                    "name": label,
                    "watchdogPid": watchdog.pid,
                    "watchdogOutsideRecordingJob": True,
                }
                yield label, desktop, original, watchdog, job
            finally:
                try:
                    restore_default(original, label)
                finally:
                    (directory / "watch-stop").touch()
                    try:
                        watchdog.wait(5)
                    finally:
                        if input_desktop().casefold() == "default" and watchdog.poll() is None:
                            watchdog.terminate()
                            watchdog.wait(3)
                        report.setdefault("desktop", {}).update(
                            restored=input_desktop().casefold() == "default", watchdogExit=watchdog.poll()
                        )
                        if (directory / "watch-restored").exists():
                            report["desktop"]["watchdogIntervened"] = True
                        if report.get("status") == "recorded" and (
                            not report["desktop"]["restored"]
                            or watchdog.poll() != 0
                            or report["desktop"].get("watchdogIntervened")
                        ):
                            raise RecordingUnavailable("watchdog or final desktop restoration check failed")


def isolated_capture(args, region, role, env, scratch, audit, ledger, session):
    label, desktop, original, watchdog, _ = session
    directory = scratch / ("take-" + role)
    directory.mkdir()
    write_json(
        directory / "spec.json",
        {
            "role": role,
            "region": region,
            "seconds": args.seconds,
            "ffmpeg": str(args.ffmpeg),
            "java": str(args.java),
        },
    )
    with (directory / "worker.log").open("wb") as log:
        worker = start(worker_command("--take", directory), scratch, env, log, ledger)
        stamp = None
        try:
            wait_file(directory / "worker-ready", worker)
            before_switch()
            if watchdog.poll() is not None:
                raise RecordingUnavailable("desktop watchdog has exited")
            stamp = time.monotonic()
            desktop.SwitchDesktop()
            try:
                code = worker.wait(args.seconds + 13)
            except subprocess.TimeoutExpired:
                raise RecordingUnavailable("recording worker exceeded its deadline") from None
            if code:
                raise RecordingUnavailable("recording worker exited without a completed take")
        finally:
            try:
                if worker.poll() is None:
                    stop_recording_job(label + "-" + role)
                stop(worker, ledger)
            finally:
                audit["restoredDefault"] = restore_default(original, label)
                if stamp is not None:
                    audit["switchSeconds"] = round(time.monotonic() - stamp, 3)
                if (directory / "audit.json").exists():
                    worker_audit = json.loads((directory / "audit.json").read_text("utf-8"))
                    ledger.extend(worker_audit.pop("processes", []))
                    audit.update(worker_audit)
                elif (directory / "worker.log").stat().st_size:
                    audit["workerLogTail"] = (
                        (directory / "worker.log").read_bytes()[-2000:].decode("utf-8", "replace")
                    )
                if audit.get("reason") or not audit["restoredDefault"]:
                    raise RecordingUnavailable(audit.get("reason", "original desktop restoration unconfirmed"))
    return scratch / (role + ".mp4")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("distribution", "java", "host-bin", "converter", "scenario", "ffmpeg", "ffprobe", "out"):
        parser.add_argument("--" + key, type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=20, choices=range(1, 31), metavar="1..30")
    parser.add_argument("--region", type=int, nargs=4)
    args = parser.parse_args()
    ctypes.windll.user32.SetProcessDPIAware()
    for key in ("distribution", "java", "host_bin", "converter", "scenario", "ffmpeg", "ffprobe"):
        setattr(args, key, getattr(args, key).resolve(strict=True))
    args.out = args.out.resolve()
    args.out.mkdir(parents=True, exist_ok=False)
    scene = json.loads(args.scenario.read_text(encoding="utf-8-sig"))
    report = {
        "schema": "shimeji-real-window-demo/1",
        "scenario": scene["id"],
        "graded": False,
        "description": "Free-running real windows. Visual demonstration only.",
        "fps": 25,
        "secondsRequested": args.seconds,
        "labels": LABELS,
        "status": "incomplete",
        "processes": [],
        "recordings": {},
    }
    result = 3
    job = None
    try:
        region = checked_region(args.region)
        report["region"] = region
        with (
            tempfile.TemporaryDirectory(dir=args.out, prefix="stage-") as temp,
            private_session(Path(temp), report) as session,
        ):
            scratch = Path(temp)
            job = session[-1]
            env = prepare(args, scratch, scene, report, session[0])
            clips = []
            for role in ("source", "host"):
                audit = {}
                report["recordings"][role] = audit
                try:
                    clips.append(
                        isolated_capture(args, region, role, env, scratch, audit, report["processes"], session)
                    )
                    if role == "host":
                        data = Path(env["DESKTOP_AI_COMPANION_DATA_ROOT"])
                        active = scene["mascots"][0]["imageSet"]
                        diagnostic = (data / "diagnostics.log").read_text(encoding="utf-8-sig")
                        source_line = f"the default companion '{active}' runs from its companion file"
                        if source_line not in diagnostic or str(data) not in diagnostic:
                            raise RecordingUnavailable(
                                "host did not confirm the isolated companion file and data root"
                            )
                        audit["installedMemberConfirmed"] = True
                    if "staging" in report:
                        check_overlap(args.ffmpeg, clips[-1], region, audit, scratch, env, report["processes"])
                except BaseException:
                    for suffix in ("gui", "video"):
                        log = scratch / f"{role}-{suffix}.log"
                        if log.is_file() and log.stat().st_size:
                            audit[suffix + "LogTail"] = log.read_bytes()[-4000:].decode("utf-8", "replace")
                    raise
            pair = scratch / "pair.mp4"
            with (scratch / "stack.log").open("wb") as log:
                stack = start(
                    [
                        args.ffmpeg,
                        "-nostdin",
                        "-loglevel",
                        "error",
                        "-i",
                        clips[0],
                        "-i",
                        clips[1],
                        "-filter_complex",
                        stack_filter(),
                        "-map",
                        "[v]",
                        "-an",
                        "-threads",
                        "4",
                        "-c:v",
                        "libx264",
                        "-preset",
                        "veryfast",
                        "-movflags",
                        "+faststart",
                        pair,
                    ],
                    scratch,
                    env,
                    log,
                    report["processes"],
                )
                try:
                    if stack.wait(timeout=150):
                        raise RecordingUnavailable("side-by-side encoding failed")
                finally:
                    stop(stack, report["processes"])
            for path in clips + [pair]:
                props = read_command(
                    [
                        args.ffprobe,
                        "-v",
                        "error",
                        "-select_streams",
                        "v:0",
                        "-show_entries",
                        "stream=nb_frames,r_frame_rate,width,height",
                        "-of",
                        "json",
                        path,
                    ],
                    scratch,
                    env,
                    report["processes"],
                )
                info = json.loads(props)["streams"][0]
                if info["r_frame_rate"] != "25/1" or int(info["nb_frames"]) != 25 * args.seconds:
                    raise RecordingUnavailable(f"{path.name}: unexpected cadence or duration")
                report.setdefault("streams", {})[path.name] = info | {"sha256": digest(path)}
            for path in clips + [pair]:
                shutil.move(path, args.out / path.name)
            report["status"] = "recorded"
            result = 0
    except (
        RecordingUnavailable,
        OSError,
        KeyError,
        IndexError,
        ValueError,
        subprocess.SubprocessError,
        win32gui.error,
        KeyboardInterrupt,
    ) as error:
        report.update(status="aborted", reason=f"{type(error).__name__}: {error}")
        result = 3
    finally:
        report["liveOwnedPids"] = (
            [
                pid
                for pid in win32job.QueryInformationJobObject(job, win32job.JobObjectBasicProcessIdList)
                if pid != os.getpid()
            ]
            if job is not None
            else []
        )
        if report["liveOwnedPids"]:
            report["status"] = "cleanup-failed"
            result = 3
        write_json(args.out / "recording.json", report)
        print(json.dumps({k: report.get(k) for k in ("scenario", "status", "reason", "liveOwnedPids")}))
    return result


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] in ("--witness", "--take"):
        ctypes.windll.user32.SetProcessDPIAware()
        sys.exit((witness if sys.argv[1] == "--witness" else take_worker)(Path(sys.argv[2])))
    sys.exit(main())
