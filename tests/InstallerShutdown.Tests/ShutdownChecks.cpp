#include "ShutdownPolicy.h"
#include <windows.h>
#include <cerrno>
#include <climits>
#include <cstdlib>
#include <cwchar>
#include <functional>
#include <iostream>
#include <utility>

using namespace scoped_shutdown;

namespace {
unsigned checks = 0;

void Check(bool condition, const char* description) {
    ++checks;
    if (!condition) throw std::runtime_error(std::string("ShutdownChecks.cpp: ") + description);
}

template<class Action>
void Refuses(Action action, const char* code, const char* description) {
    bool refused = false;
    try { action(); }
    catch (const Failure& failure) { refused = std::string(failure.what()) == code; }
    Check(refused, description);
}

Identity Installed() {
    return {L"C:\\managed\\DesktopAICompanion.exe", L"S-1-5-21-123", 7, 111};
}

Context Defaults() {
    Context context;
    context.installedImages = {Installed().image};
    context.userSid = Installed().userSid;
    context.session = Installed().session;
    context.closeTimeoutMilliseconds = 37;
    return context;
}

struct FakeProcess final : Process {
    Identity identity = Installed();
    std::function<Identity(unsigned)> read;
    unsigned reads = 0;
    unsigned windows = 1;
    unsigned closes = 0;
    unsigned terminations = 0;
    bool exited = false;
    std::vector<bool> waitResults = {true};
    std::vector<unsigned> waits;

    Identity ReadIdentity() override {
        ++reads;
        return read ? read(reads) : identity;
    }
    bool HasExited() override { return exited; }
    unsigned LifetimeWindowCount() override { return windows; }
    void CloseLifetimeWindow() override { ++closes; }
    bool WaitForExit(unsigned milliseconds) override {
        const auto index = waits.size();
        waits.push_back(milliseconds);
        const bool result = index < waitResults.size() && waitResults[index];
        if (result) exited = true;
        return result;
    }
    void Terminate() override { ++terminations; }
    bool Untouched() const { return closes == 0 && terminations == 0 && waits.empty(); }
};

std::shared_ptr<FakeProcess> Fake() { return std::make_shared<FakeProcess>(); }

void PathChecks() {
    Check(SamePath(Installed().image, L"c:\\MANAGED\\desktopaicompanion.EXE"),
        "complete Windows image paths compare without case");
    Check(!SamePath(L"", L""), "empty paths do not establish image identity");
    Check(!SamePath(Installed().image, L"DesktopAICompanion.exe"),
        "a basename never establishes installation identity");
    Check(!SamePath(Installed().image, L"C:\\managed-other\\DesktopAICompanion.exe"),
        "a prefix sibling is a separate installation");

    auto installed = Fake();
    auto foreign = Fake();
    foreign->identity.image = L"C:\\managed-other\\DesktopAICompanion.exe";
    auto result = CloseInstalled(Defaults(), {installed, foreign});
    Check(result.targets == 1 && result.graceful == 1 && result.terminated == 0,
        "only the complete installed image is closed gracefully");
    Check(installed->closes == 1 && installed->terminations == 0,
        "the selected installed process receives the close request");
    Check(foreign->Untouched(), "the same-basename prefix sibling receives no effect");

    auto context = Defaults();
    auto old = Fake();
    old->identity.image = L"C:\\previous-location\\DesktopAICompanion.exe";
    context.installedImages.push_back(old->identity.image);
    result = CloseInstalled(context, {Fake(), old});
    Check(result.targets == 2 && result.graceful == 2,
        "explicit current and registered old installation images are both targets");

    auto upper = Fake();
    upper->identity.image = L"c:\\MANAGED\\desktopaicompanion.EXE";
    Check(CloseInstalled(Defaults(), {upper}).targets == 1,
        "case differences in a complete installed path preserve selection");
}

void PrevalidationChecks() {
    auto first = Fake();
    auto context = Defaults();
    context.userSid.clear();
    Refuses([&] { CloseInstalled(context, {first}); }, "caller_identity_unavailable",
        "missing caller identity refuses shutdown");
    Check(first->Untouched(), "missing caller identity has no process effects");

    Refuses([&] { CloseInstalled(Defaults(), {first, nullptr}); }, "process_identity_unavailable",
        "an unidentifiable later process refuses the complete pass");
    Check(first->Untouched(), "a later missing process identity does not close an earlier target");

    for (unsigned field = 0; field != 3; ++field) {
        auto invalid = Fake();
        if (field == 0) invalid->identity.image.clear();
        if (field == 1) invalid->identity.userSid.clear();
        if (field == 2) invalid->identity.created = 0;
        auto earlier = Fake();
        Refuses([&] { CloseInstalled(Defaults(), {earlier, invalid}); }, "process_identity_unavailable",
            "an incomplete image, user or creation identity refuses shutdown");
        Check(earlier->Untouched() && invalid->Untouched(),
            "incomplete identity is rejected before every effect");
    }

    auto unreadable = Fake();
    unreadable->read = [](unsigned) -> Identity { throw Failure("process_identity_unavailable"); };
    Refuses([&] { CloseInstalled(Defaults(), {first, unreadable}); }, "process_identity_unavailable",
        "native identity failure propagates as refusal");
    Check(first->Untouched(), "native identity failure cannot partially close an earlier target");

    auto exited = Fake();
    exited->exited = true;
    Check(CloseInstalled(Defaults(), {exited}).targets == 0 && exited->reads == 0 && exited->Untouched(),
        "an already exited retained process is ignored without reopening identity");

    auto owner = Fake();
    owner->identity.userSid = L"S-1-5-21-456";
    Refuses([&] { CloseInstalled(Defaults(), {first, owner}); }, "installed_companion_owner_mismatch",
        "an installed image owned by a different user refuses shutdown");
    Check(first->Untouched() && owner->Untouched(), "owner refusal precedes all close effects");

    auto session = Fake();
    ++session->identity.session;
    Refuses([&] { CloseInstalled(Defaults(), {first, session}); }, "installed_companion_other_session",
        "another logon session requires explicit refusal");
    Check(first->Untouched() && session->Untouched(), "cross-session refusal precedes all close effects");

    auto ambiguous = Fake();
    ambiguous->windows = 2;
    Refuses([&] { CloseInstalled(Defaults(), {first, ambiguous}); }, "companion_lifetime_window_ambiguous",
        "ambiguous lifetime windows refuse shutdown");
    Check(first->Untouched() && ambiguous->Untouched(), "window ambiguity is found before any close effect");

    auto foreign = Fake();
    foreign->identity.image = L"C:\\portable\\DesktopAICompanion.exe";
    foreign->identity.userSid = L"S-1-5-21-456";
    ++foreign->identity.session;
    Check(CloseInstalled(Defaults(), {foreign}).targets == 0 && foreign->Untouched(),
        "a fully identified foreign copy is left alone despite a different user or session");
}

void LegacyChecks() {
    auto context = Defaults();
    context.legacyTransition = true;
    auto installed = Fake();
    auto foreign = Fake();
    foreign->identity.image = L"C:\\portable\\DesktopAICompanion.exe";
    Refuses([&] { CloseInstalled(context, {installed, foreign}); }, "legacy_foreign_companion_present",
        "a legacy upgrade refuses a foreign same-basename companion");
    Check(installed->Untouched() && foreign->Untouched(),
        "legacy refusal happens before installed or foreign process effects");
    Check(CloseInstalled(context, {installed}).graceful == 1,
        "a legacy transition without foreign processes still closes its installed target");
}

void ChangedIdentityChecks() {
    for (unsigned field = 0; field != 4; ++field) {
        auto first = Fake();
        auto changed = Fake();
        changed->read = [field](unsigned call) {
            auto identity = Installed();
            if (call >= 2) {
                if (field == 0) identity.image = L"C:\\foreign\\DesktopAICompanion.exe";
                if (field == 1) ++identity.created;
                if (field == 2) identity.userSid = L"S-1-5-21-456";
                if (field == 3) ++identity.session;
            }
            return identity;
        };
        Refuses([&] { CloseInstalled(Defaults(), {first, changed}); }, "companion_identity_changed",
            "a changed installed image, creation, user or session fails revalidation");
        Check(first->Untouched() && changed->Untouched(),
            "all target identities are revalidated before the first close request");
    }

    auto preclose = Fake();
    preclose->read = [](unsigned call) {
        auto identity = Installed();
        if (call >= 3) ++identity.created;
        return identity;
    };
    Refuses([&] { CloseInstalled(Defaults(), {preclose}); }, "companion_identity_changed",
        "an incarnation change immediately before close is rejected");
    Check(preclose->Untouched(), "pre-close incarnation changes receive no effect");

    for (unsigned field = 0; field != 2; ++field) {
        auto fallback = Fake();
        fallback->waitResults = {false, true};
        fallback->read = [field](unsigned call) {
            auto identity = Installed();
            if (call >= 4) {
                if (field == 0) ++identity.created;
                else identity.image = L"C:\\portable\\DesktopAICompanion.exe";
            }
            return identity;
        };
        Refuses([&] { CloseInstalled(Defaults(), {fallback}); }, "companion_identity_changed",
            "fallback rejects changed incarnation or image after graceful close times out");
        Check(fallback->closes == 1 && fallback->terminations == 0 && fallback->waits.size() == 1,
            "a changed target never reaches terminate fallback");
    }
}

void FallbackChecks() {
    auto hung = Fake();
    hung->waitResults = {false, true};
    const auto result = CloseInstalled(Defaults(), {hung});
    Check(result.targets == 1 && result.graceful == 0 && result.terminated == 1,
        "a timed-out installed target uses the retained-process terminate fallback");
    Check(hung->closes == 1 && hung->terminations == 1,
        "graceful close precedes one terminate on the same retained process");
    Check(hung->waits == std::vector<unsigned>({37, 37}),
        "graceful and fallback waits are both explicitly bounded");

    auto noWindow = Fake();
    noWindow->windows = 0;
    noWindow->waitResults = {false, true};
    Check(CloseInstalled(Defaults(), {noWindow}).terminated == 1 && noWindow->closes == 0,
        "an installed target without a lifetime window is waited for then terminated without broadcasting");

    auto stuck = Fake();
    stuck->waitResults = {false, false};
    Refuses([&] { CloseInstalled(Defaults(), {stuck}); }, "companion_termination_timeout",
        "a retained target that does not exit after termination refuses success");
    Check(stuck->terminations == 1 && stuck->waits.size() == 2,
        "termination failure remains bounded without repeated kill attempts");
}

unsigned long ParsePid(const wchar_t* text) {
    errno = 0;
    wchar_t* end = nullptr;
    const auto value = std::wcstoul(text, &end, 10);
    if (errno || !end || end == text || *end || value == 0 || value == ULONG_MAX)
        throw std::runtime_error("invalid synthetic child PID");
    return value;
}

void OwnedProcesses(int argc, wchar_t** argv) {
    if (argc < 5 || argc > 6) throw std::runtime_error("owned-process argument count");
    const auto targetPid = ParsePid(argv[2]);
    const auto foreignPid = ParsePid(argv[3]);
    Check(targetPid != foreignPid && targetPid != GetCurrentProcessId() && foreignPid != GetCurrentProcessId(),
        "native integration uses two distinct explicitly supplied synthetic children");
    const auto installedImage = CanonicalFile(argv[4]);
    auto target = OpenNativeProcess(targetPid);
    auto foreign = OpenNativeProcess(foreignPid);
    Check(target && foreign && !target->HasExited() && !foreign->HasExited(),
        "both explicitly supplied synthetic children are alive");
    const auto caller = EffectiveCaller();
    const auto targetIdentity = target->ReadIdentity();
    const auto foreignIdentity = foreign->ReadIdentity();
    Check(SamePath(targetIdentity.image, installedImage) && !SamePath(foreignIdentity.image, installedImage),
        "synthetic integration identities distinguish complete target and foreign images");
    Check(targetIdentity.userSid == caller.userSid && foreignIdentity.userSid == caller.userSid &&
        targetIdentity.session == caller.session && foreignIdentity.session == caller.session,
        "synthetic children belong to the calling user and logon session");
    Check(target->LifetimeWindowCount() == 1 && foreign->LifetimeWindowCount() == 1,
        "both synthetic lifetime windows are uniquely identified");
    Context context;
    context.installedImages = {installedImage};
    context.userSid = caller.userSid;
    context.session = caller.session;
    context.closeTimeoutMilliseconds = 250;
    const std::wstring mode = argc == 6 ? argv[5] : L"";
    if (mode == L"--legacy-refusal") {
        context.legacyTransition = true;
        Refuses([&] { CloseInstalled(context, {target, foreign}); }, "legacy_foreign_companion_present",
            "actual legacy refusal detects the foreign synthetic child");
        Check(!target->HasExited() && !foreign->HasExited(),
            "actual legacy refusal leaves both synthetic children running");
        Check(target->LifetimeWindowCount() == 1 && foreign->LifetimeWindowCount() == 1,
            "actual legacy refusal leaves both lifetime windows intact");
    } else {
        Check(mode.empty() || mode == L"--expect-fallback", "native integration mode is recognized");
        const auto result = CloseInstalled(context, {target, foreign});
        Check(result.targets == 1 && target->HasExited(), "actual installed synthetic target exits");
        Check(!foreign->HasExited() && foreign->LifetimeWindowCount() == 1,
            "actual foreign synthetic process and lifetime window survive");
        Check(mode.empty() ? result.graceful == 1 && result.terminated == 0 :
            result.graceful == 0 && result.terminated == 1,
            "actual installed target follows its expected graceful or terminate path");
    }
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc > 1 && std::wstring(argv[1]) == L"--owned-processes") OwnedProcesses(argc, argv);
        else {
            const std::wstring selected = argc == 3 && std::wstring(argv[1]) == L"--case" ? argv[2] : L"";
            if (argc != 1 && selected.empty()) throw std::runtime_error("unknown check arguments");
            bool matched = false;
            const std::pair<const wchar_t*, void(*)()> suites[] = {
                {L"paths", PathChecks}, {L"prevalidation", PrevalidationChecks}, {L"legacy", LegacyChecks},
                {L"changed-identity", ChangedIdentityChecks}, {L"fallback", FallbackChecks}
            };
            for (const auto& suite : suites) if (selected.empty() || selected == suite.first) {
                matched = true;
                suite.second();
            }
            if (!matched) throw std::runtime_error("unknown check case");
        }
        std::cout << "PASS " << checks << " checks\nRESULT:PASS\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "RESULT:FAIL " << error.what() << '\n';
        return 1;
    }
}
