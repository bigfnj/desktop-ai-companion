# DesktopAICompanion.Contracts

The plugin contract for [Desktop AI Companion](https://github.com/bigfnj/desktop-ai-companion) — a Windows desktop
companion whose every capability beyond "there is a companion on screen" is a module.

Reference **this and nothing else** to build a module.

```csharp
using DesktopAICompanion.Modules;

public sealed class MyThing : IModule
{
    public ModuleInfo Info { get; } = new ModuleInfo
    {
        Id = "mything",
        Name = "My Thing",
        Version = "1.0.0",
        MinHostVersion = "1.4.7",
        Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
    };

    public void Init(IHost host)
    {
        host.CompanionPoked += _ => host.SayAll("Ouch.");
    }

    public void Shutdown() { }
}
```

Build it, drop the output in `<install>\modules\mything\`, restart the app. That is the whole deployment
story — there is no signing gate and no allowlist.

## Two things to know

**`AssemblyVersion` is frozen at `1.0.0.0` forever.** That is the binding identity, so a module compiled
against any version of this package keeps resolving against the single copy the host ships. The *package*
version tracks the host release instead, which is the number you compare when choosing `MinHostVersion`.

**`MinHostVersion` is a one-way door.** It is checked before your module is initialised, and a module
demanding a host newer than the one a user has is refused (with a legible reason, not a crash). Raise it only
when you actually call a member a newer host introduced.

## Also worth having

[`DesktopAICompanion.ModuleKit`](https://www.nuget.org/packages/DesktopAICompanion.ModuleKit) — optional helpers plus a
headless `RecordingHost`, so you can self-test a module with no app running.

Full guide: [`docs/module-authoring.md`](https://github.com/bigfnj/desktop-ai-companion/blob/master/docs/module-authoring.md).
