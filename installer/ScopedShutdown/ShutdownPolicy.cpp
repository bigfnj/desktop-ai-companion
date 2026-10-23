#include "ShutdownPolicy.h"
#include <windows.h>

namespace scoped_shutdown {
bool SamePath(const std::wstring& left, const std::wstring& right) {
    return !left.empty() && left.size() == right.size() &&
        CompareStringOrdinal(left.data(), static_cast<int>(left.size()), right.data(),
            static_cast<int>(right.size()), TRUE) == CSTR_EQUAL;
}

static bool SameIdentity(const Identity& a, const Identity& b) {
    return SamePath(a.image, b.image) && a.userSid == b.userSid &&
        a.session == b.session && a.created != 0 && a.created == b.created;
}

Result CloseInstalled(const Context& context, const std::vector<std::shared_ptr<Process>>& processes) {
    if (context.userSid.empty()) throw Failure("caller_identity_unavailable");
    struct Target { std::shared_ptr<Process> process; Identity identity; unsigned windows; };
    std::vector<Target> targets;
    // Complete discovery and validation before sending any window message.
    for (const auto& process : processes) {
        if (!process) throw Failure("process_identity_unavailable");
        if (process->HasExited()) continue;
        const auto identity = process->ReadIdentity();
        if (identity.image.empty() || identity.userSid.empty() || identity.created == 0)
            throw Failure("process_identity_unavailable");
        bool installed = false;
        for (const auto& image : context.installedImages) installed |= SamePath(identity.image, image);
        if (!installed) {
            if (context.legacyTransition) throw Failure("legacy_foreign_companion_present");
            continue;
        }
        if (identity.userSid != context.userSid) throw Failure("installed_companion_owner_mismatch");
        if (identity.session != context.session) throw Failure("installed_companion_other_session");
        const unsigned windows = process->LifetimeWindowCount();
        if (windows > 1) throw Failure("companion_lifetime_window_ambiguous");
        targets.push_back({process, identity, windows});
    }
    // An identity changed during discovery must not leave earlier targets closed.
    for (const auto& target : targets)
        if (!target.process->HasExited() && !SameIdentity(target.identity, target.process->ReadIdentity()))
            throw Failure("companion_identity_changed");
    Result result;
    for (const auto& target : targets) {
        if (target.process->HasExited()) continue;
        if (!SameIdentity(target.identity, target.process->ReadIdentity())) throw Failure("companion_identity_changed");
        ++result.targets;
        if (target.windows == 1) target.process->CloseLifetimeWindow();
        if (target.process->WaitForExit(context.closeTimeoutMilliseconds)) { ++result.graceful; continue; }
        if (!SameIdentity(target.identity, target.process->ReadIdentity())) throw Failure("companion_identity_changed");
        target.process->Terminate();
        if (!target.process->WaitForExit(context.closeTimeoutMilliseconds)) throw Failure("companion_termination_timeout");
        ++result.terminated;
    }
    return result;
}
}
