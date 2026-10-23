#include <windows.h>
#include <shellapi.h>
#include <string>

namespace {
bool ignoreClose = false;

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM first, LPARAM second) {
    if (message == WM_CLOSE) {
        if (!ignoreClose) DestroyWindow(window);
        return 0;
    }
    if (message == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, first, second);
}

bool WriteReady(const std::wstring& path) {
    const auto file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    const char marker[] = "READY\n";
    DWORD written = 0;
    const bool result = WriteFile(file, marker, static_cast<DWORD>(sizeof(marker) - 1), &written, nullptr) &&
        written == sizeof(marker) - 1;
    CloseHandle(file);
    return result;
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    int count = 0;
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments) return 2;
    std::wstring ready;
    bool valid = true;
    for (int index = 1; index < count; ++index) {
        const std::wstring argument = arguments[index];
        if (argument == L"--hung" && !ignoreClose) ignoreClose = true;
        else if (argument == L"--ready" && ready.empty() && index + 1 < count) ready = arguments[++index];
        else valid = false;
    }
    LocalFree(arguments);
    if (!valid || ready.empty()) return 2;

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = L"SyntheticInstallerShutdownLifetime";
    if (!RegisterClassW(&windowClass)) return 3;
    const auto window = CreateWindowExW(WS_EX_TOOLWINDOW, windowClass.lpszClassName,
        L"DesktopAICompanion", WS_OVERLAPPED, 0, 0, 1, 1, nullptr, nullptr, instance, nullptr);
    if (!window) return 4;
    if (!WriteReady(ready)) { DestroyWindow(window); return 5; }
    MSG message{};
    while (true) {
        const auto result = GetMessageW(&message, nullptr, 0, 0);
        if (result == -1) { DestroyWindow(window); return 6; }
        if (!result) break;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return 0;
}
