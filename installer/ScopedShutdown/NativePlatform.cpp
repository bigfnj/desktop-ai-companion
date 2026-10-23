#include "ShutdownPolicy.h"
#include <windows.h>
#include <tlhelp32.h>
#include <sddl.h>
#include <cwctype>

namespace scoped_shutdown {
class Handle {
    HANDLE value_ = nullptr;
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value_; }
};
struct LocalAllocation {
    HLOCAL value;
    explicit LocalAllocation(HLOCAL allocated) : value(allocated) {}
    ~LocalAllocation() { if (value) LocalFree(value); }
    LocalAllocation(const LocalAllocation&) = delete;
    LocalAllocation& operator=(const LocalAllocation&) = delete;
};

static void CheckPath(const std::wstring& path) {
    if (path.size() < 4 || path.size() > 32000 || !iswalpha(path[0]) || path[1] != L':' || path[2] != L'\\' ||
        path.find(L':', 2) != std::wstring::npos || path.find_first_of(L"\"<>|*?/") != std::wstring::npos)
        throw Failure("installation_path_refused");
}

static std::wstring FullPath(const std::wstring& path) {
    CheckPath(path);
    std::vector<wchar_t> buffer(32768);
    DWORD length = GetFullPathNameW(path.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (!length || length >= buffer.size()) throw Failure("installation_path_unavailable");
    std::wstring full(buffer.data(), length);
    while (full.size() > 3 && full.back() == L'\\') full.pop_back();
    CheckPath(full);
    // Never follow directory junctions/symlinks, including aliases in a parent.
    for (size_t end = 3;;) {
        end = full.find(L'\\', end);
        const std::wstring part = full.substr(0, end);
        DWORD attributes = GetFileAttributesW(part.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) {
            const DWORD error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
                throw Failure("installation_path_unavailable");
        } else if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) throw Failure("installation_reparse_refused");
        if (end == std::wstring::npos) break;
        ++end;
    }
    return full;
}

static std::wstring Canonical(const std::wstring& path, bool directory) {
    const auto full = FullPath(path);
    DWORD attributes = GetFileAttributesW(full.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES && directory &&
        (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND))
        return full; // Repair must also work when the entire registered folder is missing.
    if (attributes == INVALID_FILE_ATTRIBUTES || !!(attributes & FILE_ATTRIBUTE_DIRECTORY) != directory)
        throw Failure("installation_path_unavailable");
    Handle file(CreateFileW(full.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0), nullptr));
    if (file.get() == INVALID_HANDLE_VALUE) throw Failure("installation_path_unavailable");
    BY_HANDLE_FILE_INFORMATION information = {};
    if (!GetFileInformationByHandle(file.get(), &information) || (information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
        throw Failure("installation_reparse_refused");
    std::vector<wchar_t> buffer(32768);
    DWORD length = GetFinalPathNameByHandleW(file.get(), buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!length || length >= buffer.size()) throw Failure("installation_path_unavailable");
    std::wstring result(buffer.data(), length);
    if (result.compare(0, 4, L"\\\\?\\") == 0) result.erase(0, 4);
    while (result.size() > 3 && result.back() == L'\\') result.pop_back();
    CheckPath(result);
    return result;
}
std::wstring CanonicalFile(const std::wstring& path) { return Canonical(path, false); }
std::wstring CanonicalDirectory(const std::wstring& path) { return Canonical(path, true); }

static void TokenIdentity(HANDLE token, Identity& identity) {
    DWORD bytes = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || !bytes || bytes > 65536) throw Failure("process_identity_unavailable");
    std::vector<unsigned char> buffer(bytes);
    if (!GetTokenInformation(token, TokenUser, buffer.data(), bytes, &bytes)) throw Failure("process_identity_unavailable");
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sid))
        throw Failure("process_identity_unavailable");
    LocalAllocation ownedSid(sid);
    identity.userSid = sid;
    DWORD session = 0;
    if (!GetTokenInformation(token, TokenSessionId, &session, sizeof(session), &bytes)) throw Failure("process_identity_unavailable");
    identity.session = session;
}

Identity EffectiveCaller() {
    HANDLE raw = nullptr;
    if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &raw)) {
        if (GetLastError() != ERROR_NO_TOKEN || !OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw))
            throw Failure("caller_identity_unavailable");
    }
    Handle token(raw); Identity identity;
    TokenIdentity(token.get(), identity);
    return identity;
}

class NativeProcess final : public Process {
    Handle handle_;
    DWORD pid_;
    Identity last_;
    struct Windows { DWORD pid; HWND window = nullptr; unsigned count = 0; };
    static BOOL CALLBACK FindWindow(HWND window, LPARAM data) {
        auto& found = *reinterpret_cast<Windows*>(data);
        DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
        if (pid == found.pid) {
            wchar_t title[256] = {};
            if (GetWindowTextW(window, title, 256) == 18 && wcscmp(title, L"DesktopAICompanion") == 0) {
                found.window = window; ++found.count;
            }
        }
        return TRUE;
    }
    Windows WindowsForProcess() {
        Windows found{pid_};
        if (!EnumWindows(FindWindow, reinterpret_cast<LPARAM>(&found))) throw Failure("companion_windows_unavailable");
        return found;
    }
public:
    explicit NativeProcess(DWORD pid) : handle_(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE | PROCESS_TERMINATE, FALSE, pid)), pid_(pid) {
        if (!handle_.get()) throw Failure("process_identity_unavailable");
    }
    bool HasExited() override {
        const DWORD waited = WaitForSingleObject(handle_.get(), 0);
        if (waited == WAIT_FAILED) throw Failure("process_identity_unavailable");
        return waited == WAIT_OBJECT_0;
    }
    Identity ReadIdentity() override {
        if (HasExited() && last_.created) return last_;
        std::vector<wchar_t> image(32768); DWORD length = static_cast<DWORD>(image.size());
        if (!QueryFullProcessImageNameW(handle_.get(), 0, image.data(), &length)) throw Failure("process_identity_unavailable");
        Identity identity; identity.image = CanonicalFile(std::wstring(image.data(), length));
        FILETIME created = {}, exited = {}, kernel = {}, user = {};
        if (!GetProcessTimes(handle_.get(), &created, &exited, &kernel, &user)) throw Failure("process_identity_unavailable");
        identity.created = (static_cast<unsigned long long>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        HANDLE raw = nullptr;
        if (!OpenProcessToken(handle_.get(), TOKEN_QUERY, &raw)) throw Failure("process_identity_unavailable");
        Handle token(raw); TokenIdentity(token.get(), identity);
        DWORD session = 0;
        if (!ProcessIdToSessionId(pid_, &session) || session != identity.session) throw Failure("process_identity_unavailable");
        last_ = identity;
        return identity;
    }
    unsigned LifetimeWindowCount() override { return WindowsForProcess().count; }
    void CloseLifetimeWindow() override {
        if (HasExited()) return;
        const auto found = WindowsForProcess();
        if (found.count != 1) throw Failure("companion_lifetime_window_changed");
        DWORD pid = 0; GetWindowThreadProcessId(found.window, &pid);
        if (pid != pid_ || HasExited()) return;
        DWORD_PTR ignored = 0;
        // A nonresponsive target is handled by the retained-handle fallback.
        SendMessageTimeoutW(found.window, WM_CLOSE, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 3000, &ignored);
    }
    bool WaitForExit(unsigned milliseconds) override {
        const DWORD waited = WaitForSingleObject(handle_.get(), milliseconds);
        if (waited == WAIT_FAILED) throw Failure("companion_wait_failed");
        return waited == WAIT_OBJECT_0;
    }
    void Terminate() override {
        if (!HasExited() && !TerminateProcess(handle_.get(), 1) && !HasExited()) throw Failure("companion_termination_refused");
    }
};

std::shared_ptr<Process> OpenNativeProcess(unsigned long pid) { return std::make_shared<NativeProcess>(pid); }
std::vector<unsigned long> CompanionProcessIds() {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.get() == INVALID_HANDLE_VALUE) throw Failure("process_discovery_unavailable");
    PROCESSENTRY32W entry = {}; entry.dwSize = sizeof(entry);
    if (!Process32FirstW(snapshot.get(), &entry)) throw Failure("process_discovery_unavailable");
    std::vector<unsigned long> ids;
    do { if (_wcsicmp(entry.szExeFile, L"DesktopAICompanion.exe") == 0) ids.push_back(entry.th32ProcessID); }
    while (Process32NextW(snapshot.get(), &entry));
    if (GetLastError() != ERROR_NO_MORE_FILES) throw Failure("process_discovery_unavailable");
    return ids;
}
}
