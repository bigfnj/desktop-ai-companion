# Writing a DesktopAICompanion module

Everything the app can do beyond putting a companion on screen is a **module**: a separate DLL, loaded in its own
collectible `AssemblyLoadContext`, that talks to the app only through a small published contract. Fortunes,
the AI brain and Companion Studio are all modules; the base is a companion engine and a module host.

This is the guide to writing one. It assumes you can build the repo (`pwsh build.ps1 -Release`).

---

## Start here

**In your own repo**, nothing here to clone. `DesktopAICompanion.Contracts.nupkg` and `DesktopAICompanion.ModuleKit.nupkg`
are attached to every [GitHub release](https://github.com/bigfnj/desktop-ai-companion/releases) (checksummed in
`SHA256SUMS.txt`); download them, point a package source at the folder, and:

```powershell
dotnet new install <path>\templates\desktop-ai-companion-module
dotnet new desktop-ai-companion-module -n MyThing --moduleId mything --displayName "My Thing" --standalone true
dotnet build -c Release
```

```xml
<!-- nuget.config beside your project -->
<configuration><packageSources>
  <add key="desktoppet" value="C:\path\to\downloaded\packages" />
</packageSources></configuration>
```

They are deliberately **not on nuget.org**: the contract's package version tracks the host release, so
publishing would mean a new public package on every release even when the ABI has not changed. Say so in an
issue if you would rather have them there — `packaging\New-NuGetPackages.ps1` is one command away from it.

Simpler still, if you do not want ModuleKit: the portable ZIP contains `DesktopAICompanion.Contracts.dll` beside the
exe. A plain `<Reference>` to it is enough to write a module.

Then copy the build output folder to `%LOCALAPPDATA%\Programs\Desktop AI Companion\modules\mything\` and
restart the app. That is the entire deployment story: **no signature, no allowlist, no catalog needed.**
Verify it with `DesktopAICompanion.exe --module-selftest=mything`.

**Inside this repo** (first-party modules), drop `--standalone` and it wires project references instead:

```powershell
dotnet new install .\templates\desktop-ai-companion-module
dotnet new desktop-ai-companion-module -n MyThing --moduleId mything --displayName "My Thing" -o modules\MyThing
dotnet build modules\MyThing\MyThing.csproj -c Release
```

That produces a module that already works: a tray item, a settings pane whose values round-trip, a reaction
to the companion being poked, and a self-test. Run `.\packaging\Test-ModuleTemplate.ps1` to see the same thing
scaffolded, built and verified end to end.

Pick the **id** deliberately. It names the output folder, the settings and storage keys, the self-test flag
and the catalog entry, and changing it later orphans the user's settings.

---

## The two assemblies

| | `DesktopAICompanion.Contracts` | `DesktopAICompanion.ModuleKit` |
|---|---|---|
| What | The contract: `IModule`, `IHost`, `ICompanion`, the DTOs | Convenience helpers |
| Reference as | `Private="false"` (project) or `ExcludeAssets="runtime"` (package) | normal (private) |
| At runtime | **one shared copy**, owned by the host | a copy **inside your module's folder** |
| Stability | `AssemblyVersion` frozen at `1.0.0.0`, forever | ordinary library, moves freely |

`Private="false"` on the contract is the single most important line in your csproj. The loader resolves
`DesktopAICompanion.Contracts` from the *default* context, so host and module share one copy and the types unify. Ship
your own copy and every cast to `IModule` fails at load with a message that looks like nonsense.

The reverse is true for ModuleKit: it ships *with* you, so each module can move to a new ModuleKit
independently.

---

## The contract

One file: [`src/DesktopAICompanion.Contracts/PluginApi.cs`](../src/DesktopAICompanion.Contracts/PluginApi.cs). It is
deliberately handle-based (`ICompanion`, never the app's `FormCompanion`) and framework-agnostic (no WinForms, WPF or
`System.Drawing`) so it can stay small and stable.

**You implement** `IModule`: `Info`, `Init(IHost)`, `Shutdown()`. Exactly one public class per DLL.

**You consume** `IHost`: companion events (`CompanionSpawned`, `CompanionPoked`, `CompanionLanded`, `HostShutdown`), speech (`Say`,
`SayAll`, `SpeechEnabled`), animation (`TryPlayAnimation`, `PlayAnimationAll`), screen reading
(`CaptureScreenContext`), storage and settings (`GetStorage`, `GetSettings`), input (`RegisterHotkey`,
`RegisterDropResponder`, `RegisterPokeResponder`), content (`FetchCatalogItemsAsync`,
`DownloadCatalogItemAsync`), companions (`GetCompanionManager` → `ICompanionManager`), UI (`AddTrayItems`,
`AddOptionsPane`, `PickFilesToOpen`, `OpenLink`, `IsDarkTheme`), and diagnostics (`Log`).

Two of those are worth calling out because they are easy to reimplement badly:

- **`IsDarkTheme`** (host 1.4.7+) — if you own a window, theme it from this, not from the OS registry. The
  user's choice is light / dark / **system**, and only the host knows which is set; reading the OS directly is
  correct for "system" and wrong the moment someone pins the opposite. Re-read it when you build UI rather
  than caching, since a preference change takes effect on the next open.
- **`Log(Info.Id, message)`** (host 1.4.7+) — your diagnostic channel, tagged with your id. Before it existed
  a module's only way to report anything was `SayAll`, i.e. making the companion talk to the user about an
  exception. Best-effort and never throws.

### Permissions

Declare only what you use — they are shown to the user *before* install, so an honest list is a feature:

```csharp
Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
```

A service you did not declare hands back a **refusing stand-in** rather than throwing: without
`ModulePermissions.Companions`, `GetCompanionManager` returns a manager whose every verb returns false with a reason. So
check return values; do not assume success.

### MinHostVersion

```csharp
MinHostVersion = "1.4.6",   // the host that added ICompanionManager.CompanionsDirectory
```

Checked **before** `Init`, against the host's *product* version (not the frozen `AssemblyVersion`). Raise it
only when you actually call a member a newer host introduced: a module that demands a host newer than the one
shipped is refused **forever**. Leaving it out means "runs anywhere".

### Rules the host relies on

- **Never throw.** Throw in `Init` and you are skipped with a log line; throw in a tray click and you eat the
  click. Wrap handlers in `try/catch`.
- **Handlers run on the UI thread.** Keep them short. Marshal background work back yourself.
- **Clean up in `Shutdown`.** Timers, windows, hotkey registrations, preview companions. The load context is
  unloaded afterwards.
- **Modules load only at startup.** There is no hot-load; install, update and uninstall all restart the app.

---

## ModuleKit

Reference [`src/DesktopAICompanion.ModuleKit`](../src/DesktopAICompanion.ModuleKit) and stop writing these yourself:

| Type | Use it for |
|---|---|
| `AtomicFile.TryWriteAllText` | A write that survives a crash. Returns false rather than throwing. UTF-8, no BOM. |
| `CrossSessionLock` | Guarding a file against a second session/instance writing at the same time. |
| `JsonSettingsStore<T>` | Structured state a settings pane can't express (lists, nested objects, a schema version). |
| `ModulePaths` | Your data directory, from `IModuleStorage`, with a temp fallback if Storage wasn't declared. |
| `EmbeddedResources` | Reading a file you embedded (icon, seed data), matched on the trailing name. |
| `UnicodeTextProgress` | Advancing or clipping text without splitting a surrogate pair. |
| `SelfTestProbe` | The PASS/FAIL/RESULT report shape the gate parses. |
| `Testing.RecordingHost` + fakes | Driving your module in a self-test with no window, companion or network. |

Two things go in your **data directory** (`ModulePaths`), never beside the exe: anything durable, because a
per-user install directory is read-only-ish and a module *update* replaces the install folder while
deliberately preserving the data directory.

Settings: use the host's `IModuleSettings` (`GetSettings`) for flat keys behind a pane — the host persists it
and encrypts `Secret` fields. Reach for `JsonSettingsStore<T>` only when the shape outgrows that.

---

## Contributing UI

You declare data; the host renders it. That is why a module needs no UI framework.

- **Tray items** — `AddTrayItems`. `Group`/`Order` place them; `Visible`/`DynamicText` are re-evaluated on
  every menu open; `IconPng` is raw PNG bytes (the ABI stays free of `System.Drawing`).
- **A settings pane** — `AddOptionsPane` with a `Schema` of `SettingField`s (`Bool`, `Int`, `Text`, `Enum`,
  `Secret`, `Info`), optional `PaneAction` buttons (the returned string is shown next to the button), optional
  `ListCard`s for checkable lists, and `Load`/`Save` delegates.
- **A window of your own** is possible but exceptional — Companion Studio does it because an authoring canvas isn't
  expressible as a schema. Set `UseWPF`/`UseWindowsForms` and own the window's lifetime.

---

## The self-test

Every module ships one. The host keeps **no compile-time reference** to any module, so it reaches yours by
reflection across the load-context boundary: a `public static bool SelfTest(out string detail)`.

```csharp
public static bool SelfTest(out string detail)
{
    var probe = new SelfTestProbe();
    var host = new RecordingHost();
    using (var storage = new TempModuleStorage("mything"))
    {
        host.UseStorage("mything", storage);
        var module = new MyThing();
        module.Init(host);
        probe.Check("contributes a tray item", host.TrayItems.Count == 1);
        host.RaiseCompanionPoked(new PokeInfo());
        probe.Check("reacts to a poke", host.SaidLines.Count == 1);
        module.Shutdown();
    }
    return probe.Finish(out detail);
}
```

Run it immediately — no host change needed:

```powershell
.\build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe --module-selftest=mything
```

`--module-selftest=<id>` loads `modules\<id>` through the **real** loader (so a pass also proves the loader
accepts your module, the `MinHostVersion` gate lets it through and `Init` ran), checks your `ModuleInfo` is
catalog-ready, then finds your `SelfTest` by reflection and runs it. Having no `SelfTest` is a **failure**, not
a skip.

Then add the flag in two data-only places so CI runs it:

1. the `$flags` map in [`tests/run-gate.ps1`](../tests/run-gate.ps1) — marker file
   `dp-module-<id>-selftest.txt`,
2. the flag list in [`.github/workflows/build.yml`](../.github/workflows/build.yml).

(The three modules that predate the SDK also have a bespoke `*ModuleSelfTest.cs` in the base, because each
asserts something specific about how it integrates with the host. Write one of those only if you need that;
the convention flag covers the ordinary case.)

**Never skip silently.** The gate fails on a `SKIP:` line on purpose: a self-test that skips reads exactly
like one that passed, and that has hidden a real bug here before.

---

## Publishing

`modules-dist/` is served straight off `raw.githubusercontent.com/.../master/`, so **merging to master IS the
publish** — no tag, no release, no upload. Existing users see it on their next catalog check.

One command:

```powershell
.\packaging\New-ModulePublish.ps1 -ModuleId mything -Name "My Thing" -Description "What it does." -Commit
```

It builds, zips into `modules-dist/mything.zip`, registers the entry in `modules-dist/modules.json` (reading
the version and permissions out of your source so they can't drift), commits, regenerates `catalog.json`, and
runs the freshness check. Then commit `catalog.json` and merge.

Two traps it exists to prevent, both of which have shipped bugs:

- **`catalog.json` hashes the COMMITTED git blob**, because that is what raw.githubusercontent serves. Zip →
  **commit** → catalog. Get that order wrong and the catalog advertises a hash nobody can download. The script
  refuses to continue while the zip is uncommitted.
- **`modules.json`'s version is what the in-app Update button compares against.** Lag your
  `ModuleInfo.Version` and the update is never offered; lead it and it is offered forever, surviving every
  install.

[`packaging/Test-ModulePublishFreshness.ps1`](../packaging/Test-ModulePublishFreshness.ps1) fails CI if a
module's source is newer than its published zip, or if the three versions disagree. Practically: **a PR that
touches `modules/<Id>/` needs a republish commit.**

---

## Gotchas

- **`GenerateDependencyFile` + `CopyLocalLockFileAssemblies`** are required the moment you reference anything
  (including ModuleKit): a library project otherwise leaves its dependencies out of the output and the load
  context can't resolve them.
- **Native NuGet assets** (onnxruntime) flatten *beside* your DLL rather than under `runtimes\win-x64\native`,
  which is why the loader also probes your module's own folder. Pin `RuntimeIdentifier=win-x64` with
  `SelfContained=false`.
- **`net10.0-windows` in-box:** `System.Text.Json`, DPAPI, `System.Drawing`, ConfigurationManager. Adding one
  as a `PackageReference` trips NU1510, which is an **error** here (warnings-as-errors).
- **WinRT pins your load context** so the module never unloads — tolerable only because uninstalling already
  forces a restart.
- **`AppendTargetFrameworkToOutputPath=false`**, or your DLL lands in a TFM subfolder the loader never looks in.
- **A loaded DLL cannot be deleted by the process that loaded it.** That is why uninstall and update are
  deferred to the next launch. Don't fight it.
- **Don't declare an event you never raise.** Two ABI events were removed at the freeze for exactly that: a
  silent event in a shipped contract is a trap.

---

## Where things live

| | |
|---|---|
| The contract | `src/DesktopAICompanion.Contracts/PluginApi.cs` |
| ModuleKit | `src/DesktopAICompanion.ModuleKit/` |
| The loader | `src/dotNet/Plugins/ModuleHost.cs` |
| The host bridge (real `IHost`) | `src/dotNet/Plugins/CompanionHost.cs` |
| Existing modules | `modules/Fortunes`, `modules/AiBrain`, `modules/PetStudio`, `modules/TestModule` |
| Module self-tests | `src/dotNet/Plugins/*ModuleSelfTest.cs` |
| Template | `templates/desktop-ai-companion-module/` |
| Packaging | `packaging/New-ModulePublish.ps1` |
| The gate | `tests/run-gate.ps1` |

Third-party modules (signing, consent, a marketplace, building outside this repo) are a planned later phase:
see [`module-ecosystem-roadmap.md`](module-ecosystem-roadmap.md).
