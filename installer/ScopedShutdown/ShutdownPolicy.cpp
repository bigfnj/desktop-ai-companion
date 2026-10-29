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

std::wstring ErrorText(const char* code) {
    std::wstring tag; for (const char* c = code; *c; ++c) tag += static_cast<wchar_t>(*c);
    if (tag == L"legacy_foreign_companion_present")
        return L"Close other portable or test copies of Desktop AI Companion, keep them closed for this upgrade, then retry. The previous installer has a legacy shutdown action. (" + tag + L")";
    if (tag == L"installed_companion_other_session")
        return L"Close Desktop AI Companion in your other Windows session, then retry setup. (" + tag + L")";
    if (tag == L"related_installation_context_mismatch")
        return L"A related installation belongs to a different installation context. Remove it with its own installer before retrying this per-user setup. (" + tag + L")";
    // No "close its windows and retry": nothing the user closes helps, and the custom action runs, unconditioned,
    // before InstallValidate in every repair, upgrade and uninstall of these packages, so a repair from the original
    // .msi stops at this same check before Windows Installer could re-cache anything. What clears it is the cached
    // copy back where Windows Installer recorded it (N-installer-tests-01, docs/installer-scoped-shutdown.md): then
    // ScopedPackage opens it and checks its ProductCode and UpgradeCode, so a wrong file is refused, never trusted.
    if (tag == L"cached_package_missing")
        return L"Windows Installer's cached copy of an installed Desktop AI Companion package is missing, so setup cannot check how that version closes, "
               L"and repairing, upgrading or removing it stops here, a repair from the original .msi included. To recover, an administrator copies "
               L"the .msi that installed that version back to the cached path Windows Installer recorded for it (its LocalPackage, in %WINDIR%\\Installer); "
               L"then run setup again. The project's docs/installer-scoped-shutdown.md shows how to find that path. (" + tag + L")";
    return L"Setup could not safely identify or close the installed Desktop AI Companion. Close its windows and retry setup. (" + tag + L")";
}
}
