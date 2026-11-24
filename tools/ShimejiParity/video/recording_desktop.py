"""Read-only interactive-desktop checks and an owned neutral recording backdrop (Windows)."""

import ctypes
import os
from ctypes import wintypes

import psutil
import win32api
import win32con
import win32gui
import win32process
import win32ts


class RecordingUnavailable(RuntimeError):
    pass


def input_desktop():
    user = ctypes.WinDLL("user32", use_last_error=True)
    user.OpenInputDesktop.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    user.OpenInputDesktop.restype = wintypes.HANDLE
    user.GetUserObjectInformationW.argtypes = (
        wintypes.HANDLE,
        ctypes.c_int,
        wintypes.LPVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    )
    user.CloseDesktop.argtypes = (wintypes.HANDLE,)
    handle = user.OpenInputDesktop(0, False, 1)  # DESKTOP_READOBJECTS
    if not handle:
        raise RecordingUnavailable("input desktop unavailable")
    try:
        name, needed = ctypes.create_unicode_buffer(256), wintypes.DWORD()
        if not user.GetUserObjectInformationW(handle, 2, name, ctypes.sizeof(name), ctypes.byref(needed)):
            raise RecordingUnavailable("input desktop name unavailable")
        return name.value
    finally:
        user.CloseDesktop(handle)


def display_devices(adapter_name=None):
    index = 0
    while True:
        try:
            device = win32api.EnumDisplayDevices(adapter_name, index, 0)
        except win32api.error as error:
            if error.winerror not in (0, 259):
                raise
            return
        yield device
        index += 1


def attached_display():
    return any(
        monitor.StateFlags & 1
        for adapter in display_devices()
        if adapter.StateFlags & 1 and not adapter.StateFlags & 8
        for monitor in display_devices(adapter.DeviceName)
    )


def preflight(expected_desktop="Default"):
    if not attached_display():
        raise RecordingUnavailable("no active attached display")
    current = win32ts.ProcessIdToSessionId(os.getpid())
    state = win32ts.WTSQuerySessionInformation(None, current, win32ts.WTSConnectState)
    if state != win32ts.WTSActive or input_desktop().casefold() != expected_desktop.casefold():
        raise RecordingUnavailable("session is inactive or input desktop is locked")
    for process in psutil.process_iter(["name"]):
        if (process.info["name"] or "").casefold() == "logonui.exe" and win32ts.ProcessIdToSessionId(
            process.pid
        ) == current:
            raise RecordingUnavailable("LogonUI is present in the recording session")
    front = win32gui.GetForegroundWindow()
    if not front and expected_desktop == "Default":
        raise RecordingUnavailable("foreground application unavailable")
    if front and win32gui.GetClassName(front) not in ("Progman", "WorkerW", "Shell_TrayWnd"):
        x1, y1, x2, y2 = win32gui.GetWindowRect(front)
        screen = win32api.GetMonitorInfo(win32api.MonitorFromWindow(front, 2))["Monitor"]
        if x1 <= screen[0] and y1 <= screen[1] and x2 >= screen[2] and y2 >= screen[3]:
            raise RecordingUnavailable("fullscreen foreground application")
    primary = win32api.MonitorFromPoint((0, 0), 1)
    left, top, right, bottom = win32api.GetMonitorInfo(primary)["Work"]
    return left, top, (right - left) // 2 * 2, (bottom - top) // 2 * 2


def windows():
    result = {}

    def visit(hwnd, _):
        pid = win32process.GetWindowThreadProcessId(hwnd)[1]
        result[hwnd] = (pid, *win32gui.GetWindowRect(hwnd))
        return True

    win32gui.EnumWindows(visit, None)
    return result


def changes(before, after, owned):
    first = {hwnd: value for hwnd, value in before.items() if value[0] not in owned}
    last = {hwnd: value for hwnd, value in after.items() if value[0] not in owned}
    return [
        {"hwnd": hwnd, "before": first.get(hwnd), "after": last.get(hwnd)}
        for hwnd in sorted(first.keys() | last.keys())
        if first.get(hwnd) != last.get(hwnd)
    ]


class Backdrop:
    def __init__(self, region):
        self.region = region
        self.window = None

    def __enter__(self):
        wc = win32gui.WNDCLASS()
        wc.hInstance = win32api.GetModuleHandle(None)
        wc.lpszClassName = "ShimejiRecordingBackdrop" + str(os.getpid())
        wc.lpfnWndProc = win32gui.DefWindowProc
        wc.hbrBackground = win32gui.CreateSolidBrush(win32api.RGB(50, 60, 70))
        self.cls = wc
        win32gui.RegisterClass(wc)
        try:
            self.window = win32gui.CreateWindowEx(
                win32con.WS_EX_TOOLWINDOW | win32con.WS_EX_NOACTIVATE | win32con.WS_EX_TOPMOST,
                wc.lpszClassName,
                "",
                win32con.WS_POPUP | win32con.WS_VISIBLE,
                *self.region,
                0,
                0,
                wc.hInstance,
                None,
            )
            win32gui.SetWindowPos(
                self.window, win32con.HWND_TOPMOST, *self.region, win32con.SWP_SHOWWINDOW | win32con.SWP_NOACTIVATE
            )
            win32gui.UpdateWindow(self.window)
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def visible(self, snapshot, runtime_pid):
        left, top, width, height = self.region
        seen = []
        for hwnd, (pid, x1, y1, x2, y2) in snapshot.items():
            if hwnd == self.window:
                return seen
            if (
                x2 <= left
                or y2 <= top
                or x1 >= left + width
                or y1 >= top + height
                or x2 <= x1
                or y2 <= y1
                or not win32gui.IsWindowVisible(hwnd)
                or win32gui.IsIconic(hwnd)
            ):
                continue
            cloaked = wintypes.DWORD()
            status = ctypes.windll.dwmapi.DwmGetWindowAttribute(
                wintypes.HWND(hwnd), 14, ctypes.byref(cloaked), ctypes.sizeof(cloaked)
            )
            if status == 0 and cloaked.value:
                continue
            if pid != runtime_pid:
                raise RecordingUnavailable(f"another visible window overlaps the region at {(x1, y1, x2, y2)}")
            if win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) & win32con.WS_EX_LAYERED:
                seen.append((hwnd, x1, y1, x2, y2))
        raise RecordingUnavailable("recording backdrop unavailable in window order")

    def __exit__(self, *_):
        if self.window:
            win32gui.DestroyWindow(self.window)
        win32gui.UnregisterClass(self.cls.lpszClassName, self.cls.hInstance)
        # Windows frees the registered class's background brush on unregistration.


if __name__ == "__main__":
    ctypes.windll.user32.SetProcessDPIAware()
    import json

    try:
        print(json.dumps({"eligible": True, "region": preflight(), "topLevelWindowCount": len(windows())}))
    except (RecordingUnavailable, OSError, win32api.error) as error:
        print(json.dumps({"eligible": False, "reason": str(error)}))
        raise SystemExit(3)
