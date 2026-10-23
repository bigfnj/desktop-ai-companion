#pragma once

#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace scoped_shutdown {
struct Failure : std::runtime_error {
    explicit Failure(const char* code) : std::runtime_error(code) {}
};

struct Identity {
    std::wstring image;
    std::wstring userSid;
    unsigned long session = 0;
    unsigned long long created = 0;
};

// Implementations retain the process object from discovery through final wait.
// Production never reacquires a process by PID to close or terminate it.
struct Process {
    virtual ~Process() = default;
    virtual Identity ReadIdentity() = 0;
    virtual bool HasExited() = 0;
    virtual unsigned LifetimeWindowCount() = 0;
    virtual void CloseLifetimeWindow() = 0;
    virtual bool WaitForExit(unsigned milliseconds) = 0;
    virtual void Terminate() = 0;
};

struct Context {
    std::vector<std::wstring> installedImages; // canonical, complete executable paths
    std::wstring userSid;
    unsigned long session = 0;
    bool legacyTransition = false;
    unsigned closeTimeoutMilliseconds = 5000;
};

struct Result { unsigned targets = 0; unsigned graceful = 0; unsigned terminated = 0; };

bool SamePath(const std::wstring& left, const std::wstring& right);
Result CloseInstalled(const Context& context, const std::vector<std::shared_ptr<Process>>& processes);

// The integration harness passes only PIDs of its own synthetic children here.
// Only the MSI entry point performs system-wide name discovery.
std::shared_ptr<Process> OpenNativeProcess(unsigned long pid);
std::vector<unsigned long> CompanionProcessIds();
Identity EffectiveCaller();
std::wstring CanonicalFile(const std::wstring& path);
std::wstring CanonicalDirectory(const std::wstring& path);
}
