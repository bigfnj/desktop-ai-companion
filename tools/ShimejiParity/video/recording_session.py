"""Windows process and restoration helpers for visible Shimeji recording takes."""

import msvcrt
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

import win32api
import win32con
import win32event
import win32job
import win32process
import win32service

sys.path.insert(0, str(Path(__file__).resolve().parent))
from recording_desktop import RecordingUnavailable, input_desktop, preflight


def idle_seconds():
    return ((win32api.GetTickCount() - win32api.GetLastInputInfo()) & 0xFFFFFFFF) / 1000


def before_switch():
    region = preflight()
    if idle_seconds() < 60:
        raise RecordingUnavailable("recent input on Default; retry after 60 idle seconds")
    return region


class DesktopProcess:
    def __init__(self, cmd, cwd, env, log, desktop):
        info = win32process.STARTUPINFO()
        info.lpDesktop = "WinSta0\\" + desktop
        info.dwFlags = win32con.STARTF_USESTDHANDLES
        with open(os.devnull, "rb") as null:
            handles = []
            try:
                for stream in (null, log):
                    raw = msvcrt.get_osfhandle(stream.fileno())
                    handles.append(
                        win32api.DuplicateHandle(
                            win32api.GetCurrentProcess(),
                            raw,
                            win32api.GetCurrentProcess(),
                            0,
                            True,
                            win32con.DUPLICATE_SAME_ACCESS,
                        )
                    )
                info.hStdInput, info.hStdOutput = handles
                info.hStdError = handles[1]
                self.handle, thread, self.pid, _ = win32process.CreateProcess(
                    str(cmd[0]),
                    subprocess.list2cmdline([str(value) for value in cmd]),
                    None,
                    None,
                    True,
                    subprocess.CREATE_NO_WINDOW,
                    env,
                    str(cwd),
                    info,
                )
                thread.Close()
            finally:
                for handle in handles:
                    handle.Close()
        self.returncode = None

    def poll(self):
        if self.returncode is None and win32event.WaitForSingleObject(self.handle, 0) == win32event.WAIT_OBJECT_0:
            self.returncode = win32process.GetExitCodeProcess(self.handle)
        return self.returncode

    def wait(self, timeout):
        if win32event.WaitForSingleObject(self.handle, int(timeout * 1000)) != win32event.WAIT_OBJECT_0:
            raise subprocess.TimeoutExpired(self.pid, timeout)
        return self.poll()

    def terminate(self):
        if self.poll() is None:
            win32process.TerminateProcess(self.handle, 1)

    kill = terminate


def restore_default(original, label):
    # Never switch away from a lock or another desktop chosen by the owner.
    active = input_desktop()
    if active == label:
        original.SwitchDesktop()
        for _ in range(40):
            if input_desktop().casefold() == "default":
                return True
            time.sleep(0.025)
    return input_desktop().casefold() == "default"


def open_original():
    return win32service.OpenDesktop(
        "Default", 0, False, win32con.DESKTOP_SWITCHDESKTOP | win32con.DESKTOP_READOBJECTS
    )


def fresh_desktop():
    label = "ShimejiRecording-" + uuid.uuid4().hex
    return label, win32service.CreateDesktop(label, 0, win32con.GENERIC_ALL, None)


def wait_file(path, proc, seconds=6):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if path.is_file():
            return
        if proc.poll() is not None:
            raise RecordingUnavailable("recording helper exited before " + path.name)
        time.sleep(0.025)
    raise RecordingUnavailable("recording helper timed out before " + path.name)


def stop_recording_job(name):
    try:
        job = win32job.OpenJobObject(0x0008 | 0x0004, False, name)
    except win32api.error as error:
        if error.winerror == 2:
            return
        raise
    members = []
    try:
        try:
            for pid in win32job.QueryInformationJobObject(job, win32job.JobObjectBasicProcessIdList):
                try:
                    members.append(win32api.OpenProcess(win32con.SYNCHRONIZE, False, pid))
                except win32api.error as error:
                    if error.winerror != 87:  # process exited between enumeration and open
                        raise
        finally:
            win32job.TerminateJobObject(job, 3)
        end = time.monotonic() + 2
        for member in members:
            remaining_ms = max(0, int((end - time.monotonic()) * 1000))
            if win32event.WaitForSingleObject(member, remaining_ms) != win32event.WAIT_OBJECT_0:
                raise RecordingUnavailable("recording process did not exit in time")
        while win32job.QueryInformationJobObject(job, win32job.JobObjectBasicAccountingInformation)[
            "ActiveProcesses"
        ]:
            if time.monotonic() >= end:
                raise RecordingUnavailable("recording job did not exit in time")
            time.sleep(0.02)
    finally:
        for member in members:
            member.Close()
        job.Close()


def watchdog(directory, label, owner_pid):
    """Runs outside the recorder's job, with its own handle to the original desktop."""
    original = open_original()
    owner = win32api.OpenProcess(win32con.SYNCHRONIZE, False, owner_pid)
    armed_at = None
    input_at_switch = None
    try:
        (directory / "watch-ready").touch()
        while True:
            gone = win32event.WaitForSingleObject(owner, 0) == win32event.WAIT_OBJECT_0
            closing = (directory / "watch-stop").exists() or not directory.exists()
            try:
                active = input_desktop()
            except RecordingUnavailable:
                # A secure desktop takes priority. Never attempt to dismiss it.
                if gone or closing:
                    return 3
                time.sleep(0.05)
                continue
            if active == label:
                if armed_at is None:
                    armed_at = time.monotonic()
                    input_at_switch = win32api.GetLastInputInfo()
                expired = time.monotonic() - armed_at >= 55
                input_changed = win32api.GetLastInputInfo() != input_at_switch
                if gone or closing or expired or input_changed:
                    # Stop gdigrab before Default becomes visible, including when
                    # its controlling worker is hung. These names belong only to
                    # the two recording jobs on this UUID-named desktop.
                    try:
                        for role in ("source", "host"):
                            stop_recording_job(label + "-" + role)
                    finally:
                        restored = restore_default(original, label)
                    if directory.exists():
                        (directory / "watch-restored").write_text(str(restored), encoding="ascii")
                    return 0 if restored else 3
            elif active.casefold() == "default":
                armed_at = None
                if gone or closing:
                    return 0
            elif gone or closing:
                return 3
            time.sleep(0.045)
    finally:
        owner.Close()
        original.CloseDesktop()


if __name__ == "__main__":
    if len(sys.argv) != 5 or sys.argv[1] != "watch" or not sys.argv[3].startswith("ShimejiRecording-"):
        raise SystemExit(2)
    raise SystemExit(watchdog(Path(sys.argv[2]), sys.argv[3], int(sys.argv[4])))
