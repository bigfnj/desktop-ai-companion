# Installation-scoped shutdown

The MSI closes processes belonging to its registered per-user installation. A
portable copy or an isolated test host with the same executable name is left
running. The installer still asks the installed companion to close and retains
the bounded termination fallback for an unresponsive installed process.

`installer/ScopedShutdown` is a native x64 Windows Installer Binary DLL. It is
compiled with the static C++ runtime and imports only Windows system libraries.
It introduces no .NET prerequisite for installing the MSI. Building the MSI now
requires an existing MSVC x64 toolchain and Windows SDK; the build never installs
tools or elevates privileges.

The action reads the package's own ProductCode and UpgradeCode from its active
database. Windows Installer registration establishes related-product membership
and each current-user unmanaged installation's actual InstallLocation, including
nondefault locations during repair or uninstall. Caller-controlled
`INSTALLFOLDER` and `WIX_UPGRADE_DETECTED` values are not an executable allowlist.
An uncorroborated related installation fails setup before any close request.

Every candidate is identified before any shutdown begins. The action compares
the complete canonical executable path, effective caller TokenUser SID, session
and process creation time, and retains the process handle throughout. It rejects
reparse paths and ambiguous lifetime windows. It sends WM_CLOSE only to the
installed process's unique `DesktopAICompanion` lifetime window. The fallback
terminates that same retained process object after another identity check. A
process in another logon session must be closed there by its owner.

Shutdown runs after CostFinalize and before InstallValidate. The existing
InstallInitialize / RemoveExistingProducts / InstallFinalize rollback boundary
is unchanged. Rollback restores installation files and data; it does not restore
the prior running process, and no rollback action invents a replacement launch.

## First upgrade from a historical MSI

An older cached MSI still runs its own basename-wide shutdown action when
RemoveExistingProducts invokes its uninstall. A new MSI cannot repair that
historical action by replacing its own authoring. Cached packages and security
settings are never edited.

The new action detects that transition using the registered package's capability
metadata. It refuses when another portable/test companion is already present.
That preflight is a snapshot: another copy could start afterward. Keep every
other companion closed for the **entire first upgrade** from a historical MSI.
Concurrent portable use is supported for subsequent operations between packages
that both carry the scoped-shutdown capability. The historical transition remains
a separate live acceptance requirement.

WiX documents CloseApplication.Target as an executable filename, and its pinned
5.0.2 implementation enumerates by executable name. Its action is also scheduled
during uninstall. These are the reasons a formatted installation path or simply
removing the new package's row would not fix the historical transition.
See [CloseApplication schema](https://docs.firegiant.com/wix/schema/util/closeapplication/),
[WiX 5.0.2 CloseApps.cpp](https://github.com/wixtoolset/wix/blob/v5.0.2/src/ext/Util/ca/CloseApps.cpp),
[WiX 5.0.2 action scheduling](https://github.com/wixtoolset/wix/blob/v5.0.2/src/ext/Util/wixlib/UtilExtension_Platform.wxi#L99-L104),
[RemoveExistingProducts](https://learn.microsoft.com/en-us/windows/win32/msi/removeexistingproducts-action)
and [MsiGetProductInfoEx](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msigetproductinfoexw).

## Validation

Run `pwsh -NoProfile -File tests/Test-InstallerCompilerEnvironment.ps1` to verify
that an overlong caller PATH does not break vendor SDK discovery and the caller's
compiler environment and current-directory executable-search policy survive both
success and failure. Setup uses a bounded child PATH; it does not persist PATH.

Run `pwsh -NoProfile -File installer/build-scoped-shutdown.ps1 -Tests
-VerifyReproducibility` to compile with warnings as errors, check native imports,
verify the absent CLR header and compare separate output directories byte for
byte. Toolchain versions and binary evidence are written under the checkout's
ignored `build/scoped-shutdown` directory.

Run `pwsh -NoProfile -File tests/Test-InstallerShutdown.ps1 -SkipBuild` for the
fake policy matrix and owned synthetic process tests. The same wrapper supports
Windows PowerShell 5.1. It never enumerates or sends input to a real application.
All fixture processes are retained children and cleaned through their own handles.

Run `python -X utf8 tests/run_installer_shutdown_mutations.py --apply` in an
exclusive native build window for four focused negative witnesses. They remove
path selection, legacy refusal, owner prevalidation or fallback identity checks
one at a time against in-memory fake processes. Exact source backups precede
all builds; restoration and the complete 52-check healthy suite finish the run.

`packaging/Test-MsiSurface.ps1` checks the built package's Binary action,
capability, absence of the old broad action and shutdown/removal ordering.
Isolated lifecycle tests use unique product identities and ordinary workspace
data, and require explicit application plus a fresh public-image preflight.
That evidence is a safety check, never a substitute for user authorization.

Both Windows build and release workflows run the compiler environment check,
native build/import/reproducibility checks, the 78 fake/owned-process checks and
the lifecycle harness's BuildOnly mode. BuildOnly builds four isolated packages
and verifies their tables and embedded DLL without running MSI. Full lifecycle
application remains an explicit local release check.

The fixture cache carries all three workspace directory defaults, because
[`msiexec /f` ignores command-line property values](https://learn.microsoft.com/en-us/windows/win32/msi/command-line-options).
It checks the owned image, shortcuts, generation and settings after maintenance,
and verifies that the unique default program directory stays absent. Optional
external shortcut-absence checks stop before following a reparse component and
are reported as unproven separately from the owned lifecycle results.

The first local qualification passed rollback, upgrade and reinstall with exact
native shutdown receipts, foreign-process survival and preserved settings. Its
strict repair/uninstall receipt gate remains open because successful maintenance
did not retain the requested verbose logs. This does not qualify the complete
production-host lifecycle or the first historical cached-MSI transition. The
remaining acceptance and exact local cleanup findings are tracked in BACKLOG.md.

The private WiX bootstrap suppresses SDK first-use global-tools PATH registration
before its first `dotnet` command and restores the caller's setting afterward.
Intentional global WiX installation retains its existing behavior.
