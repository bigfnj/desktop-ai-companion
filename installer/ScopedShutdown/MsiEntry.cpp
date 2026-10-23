#include "ShutdownPolicy.h"
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <algorithm>
#include <cwctype>

namespace {
using scoped_shutdown::Failure;
class MsiHandle {
    MSIHANDLE value_;
public:
    explicit MsiHandle(MSIHANDLE value = 0) : value_(value) {}
    ~MsiHandle() { if (value_) MsiCloseHandle(value_); }
    operator MSIHANDLE() const { return value_; }
    MsiHandle(const MsiHandle&) = delete;
    MsiHandle& operator=(const MsiHandle&) = delete;
};

std::wstring SessionProperty(MSIHANDLE install, const wchar_t* name) {
    DWORD length = 0; wchar_t empty = 0;
    UINT code = MsiGetPropertyW(install, name, &empty, &length);
    if (code != ERROR_SUCCESS && code != ERROR_MORE_DATA) throw Failure("installer_metadata_unavailable");
    std::vector<wchar_t> value(static_cast<size_t>(length) + 1); DWORD capacity = static_cast<DWORD>(value.size());
    if (MsiGetPropertyW(install, name, value.data(), &capacity) != ERROR_SUCCESS)
        throw Failure("installer_metadata_unavailable");
    return std::wstring(value.data(), capacity);
}

std::wstring RecordString(MSIHANDLE record, unsigned field) {
    DWORD length = 0; wchar_t empty = 0;
    UINT code = MsiRecordGetStringW(record, field, &empty, &length);
    if (code != ERROR_SUCCESS && code != ERROR_MORE_DATA) throw Failure("installer_metadata_unavailable");
    std::vector<wchar_t> value(static_cast<size_t>(length) + 1); DWORD capacity = static_cast<DWORD>(value.size());
    if (MsiRecordGetStringW(record, field, value.data(), &capacity) != ERROR_SUCCESS)
        throw Failure("installer_metadata_unavailable");
    return std::wstring(value.data(), capacity);
}

std::wstring DatabaseProperty(MSIHANDLE database, const wchar_t* name) {
    // Names are compile-time identifiers, never interpolated caller input.
    std::wstring query = L"SELECT `Value` FROM `Property` WHERE `Property`='" + std::wstring(name) + L"'";
    MSIHANDLE raw = 0;
    if (MsiDatabaseOpenViewW(database, query.c_str(), &raw) != ERROR_SUCCESS) throw Failure("installer_metadata_unavailable");
    MsiHandle view(raw);
    if (MsiViewExecute(view, 0) != ERROR_SUCCESS) throw Failure("installer_metadata_unavailable");
    UINT code = MsiViewFetch(view, &raw);
    if (code == ERROR_NO_MORE_ITEMS) return L"";
    if (code != ERROR_SUCCESS) throw Failure("installer_metadata_unavailable");
    MsiHandle record(raw);
    return RecordString(record, 1);
}

bool Guid(const std::wstring& text) {
    if (text.size() != 38 || text.front() != L'{' || text.back() != L'}') return false;
    for (size_t i = 1; i < 37; ++i) {
        if (i == 9 || i == 14 || i == 19 || i == 24) { if (text[i] != L'-') return false; }
        else if (!iswxdigit(text[i])) return false;
    }
    return true;
}

bool ProductProperty(const std::wstring& product, const std::wstring& sid, const wchar_t* name, std::wstring& result) {
    DWORD length = 0; wchar_t empty = 0;
    UINT code = MsiGetProductInfoExW(product.c_str(), sid.c_str(), MSIINSTALLCONTEXT_USERUNMANAGED, name, &empty, &length);
    if (code == ERROR_UNKNOWN_PRODUCT) return false;
    if (code != ERROR_SUCCESS && code != ERROR_MORE_DATA) throw Failure("registered_installation_unavailable");
    std::vector<wchar_t> value(static_cast<size_t>(length) + 1); DWORD capacity = static_cast<DWORD>(value.size());
    if (MsiGetProductInfoExW(product.c_str(), sid.c_str(), MSIINSTALLCONTEXT_USERUNMANAGED, name, value.data(), &capacity) != ERROR_SUCCESS)
        throw Failure("registered_installation_unavailable");
    result.assign(value.data(), capacity);
    return true;
}

bool ScopedPackage(const std::wstring& path, const std::wstring& product, const std::wstring& upgrade) {
    const auto canonical = scoped_shutdown::CanonicalFile(path);
    MSIHANDLE raw = 0;
    if (MsiOpenDatabaseW(canonical.c_str(), MSIDBOPEN_READONLY, &raw) != ERROR_SUCCESS)
        throw Failure("cached_package_metadata_unavailable");
    MsiHandle database(raw);
    if (!scoped_shutdown::SamePath(DatabaseProperty(database, L"ProductCode"), product) ||
        !scoped_shutdown::SamePath(DatabaseProperty(database, L"UpgradeCode"), upgrade))
        throw Failure("cached_package_identity_mismatch");
    if (DatabaseProperty(database, L"DESKTOP_AI_COMPANION_SCOPED_SHUTDOWN_CAPABILITY") != L"1") return false;
    // Do not trust a capability marker accompanied by the historical broad action.
    const auto table = MsiDatabaseIsTablePersistentW(database, L"Wix4CloseApplication");
    if (table == MSICONDITION_ERROR) throw Failure("cached_package_metadata_unavailable");
    return table == MSICONDITION_NONE;
}

scoped_shutdown::Context ContextFor(MSIHANDLE install) {
    auto caller = scoped_shutdown::EffectiveCaller();
    if (caller.userSid.empty() || caller.userSid == L"S-1-5-18" || SessionProperty(install, L"UserSID") != caller.userSid)
        throw Failure("installer_caller_context_mismatch");
    MsiHandle database(MsiGetActiveDatabase(install));
    if (!database) throw Failure("installer_metadata_unavailable");
    const auto upgrade = DatabaseProperty(database, L"UpgradeCode");
    const auto current = DatabaseProperty(database, L"ProductCode");
    if (!Guid(upgrade) || !Guid(current)) throw Failure("installer_product_identity_refused");
    std::vector<std::wstring> products;
    // Membership comes from Windows Installer registration, never the public
    // WIX_UPGRADE_DETECTED/INSTALLFOLDER command-line properties.
    for (DWORD index = 0; index < 64; ++index) {
        wchar_t product[39] = {};
        const UINT code = MsiEnumRelatedProductsW(upgrade.c_str(), 0, index, product);
        if (code == ERROR_NO_MORE_ITEMS) break;
        if (code != ERROR_SUCCESS || index == 63) throw Failure("registered_installation_unavailable");
        if (std::find(products.begin(), products.end(), product) == products.end()) products.emplace_back(product);
    }
    const bool installed = !SessionProperty(install, L"Installed").empty();
    if (installed && std::find(products.begin(), products.end(), current) == products.end()) products.push_back(current);
    scoped_shutdown::Context context;
    context.userSid = caller.userSid; context.session = caller.session;
    for (const auto& product : products) {
        std::wstring location;
        if (!ProductProperty(product, caller.userSid, INSTALLPROPERTY_INSTALLLOCATION, location)) {
            // The later major-upgrade machinery may still act on a related
            // machine/managed registration. Never silently miss its legacy CA.
            throw Failure("related_installation_context_mismatch");
        }
        if (location.empty()) throw Failure("registered_installation_location_missing");
        std::wstring cached;
        if (!ProductProperty(product, caller.userSid, INSTALLPROPERTY_LOCALPACKAGE, cached) || cached.empty())
            throw Failure("cached_package_metadata_unavailable");
        context.legacyTransition |= !ScopedPackage(cached, product, upgrade);
        auto image = scoped_shutdown::CanonicalDirectory(location) + L"\\DesktopAICompanion.exe";
        if (std::find(context.installedImages.begin(), context.installedImages.end(), image) == context.installedImages.end())
            context.installedImages.push_back(image);
    }
    return context;
}

void Message(MSIHANDLE install, INSTALLMESSAGE kind, const std::wstring& text) {
    MsiHandle record(MsiCreateRecord(1));
    if (record) {
        MsiRecordSetStringW(record, 0, L"[1]"); MsiRecordSetStringW(record, 1, text.c_str());
        MsiProcessMessage(install, kind, record);
    }
}
std::wstring ErrorText(const char* code) {
    std::wstring tag; for (const char* c = code; *c; ++c) tag += static_cast<wchar_t>(*c);
    if (tag == L"legacy_foreign_companion_present")
        return L"Close other portable or test copies of Desktop AI Companion, keep them closed for this upgrade, then retry. The previous installer has a legacy shutdown action. (" + tag + L")";
    if (tag == L"installed_companion_other_session")
        return L"Close Desktop AI Companion in your other Windows session, then retry setup. (" + tag + L")";
    if (tag == L"related_installation_context_mismatch")
        return L"A related installation belongs to a different installation context. Remove it with its own installer before retrying this per-user setup. (" + tag + L")";
    return L"Setup could not safely identify or close the installed Desktop AI Companion. Close its windows and retry setup. (" + tag + L")";
}
}

extern "C" UINT __stdcall CloseInstalledCompanion(MSIHANDLE install) noexcept {
    try {
        const auto context = ContextFor(install);
        if (context.installedImages.empty()) return ERROR_SUCCESS;
        std::vector<std::shared_ptr<scoped_shutdown::Process>> processes;
        for (const auto pid : scoped_shutdown::CompanionProcessIds())
            processes.push_back(scoped_shutdown::OpenNativeProcess(pid));
        const auto result = scoped_shutdown::CloseInstalled(context, processes);
        Message(install, INSTALLMESSAGE_INFO, L"Installation-scoped companion shutdown: targets=" + std::to_wstring(result.targets) +
            L", graceful=" + std::to_wstring(result.graceful) + L", terminated=" + std::to_wstring(result.terminated) +
            (context.legacyTransition ? L"; legacy transition requires exclusive companion use throughout setup." : L"."));
        return ERROR_SUCCESS;
    } catch (const Failure& failure) {
        try { Message(install, INSTALLMESSAGE_ERROR, ErrorText(failure.what())); } catch (...) { }
    } catch (...) {
        try { Message(install, INSTALLMESSAGE_ERROR, L"Setup could not safely close the installed companion. Close it and retry. (shutdown_unavailable)"); } catch (...) { }
    }
    return ERROR_INSTALL_FAILURE;
}
