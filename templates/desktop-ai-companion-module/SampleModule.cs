using System;
using System.Collections.Generic;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.SampleModule
{
    /// <summary>
    /// SAMPLE_DISPLAY_NAME.
    ///
    /// A module is one public class implementing <see cref="IModule"/>. The host finds it by reflection,
    /// loads it in its own collectible AssemblyLoadContext, and calls <see cref="Init"/> once at startup and
    /// <see cref="Shutdown"/> once on the way out. Everything the module can do it does through
    /// <see cref="IHost"/> — it never references the app.
    ///
    /// Two rules worth internalising:
    ///   * Declare only the <see cref="ModulePermissions"/> you use. They are shown to the user before
    ///     install, and a service you did not declare hands back a refusing stand-in rather than throwing.
    ///   * Nothing here may throw. A module that throws in Init is skipped with a log line; a module that
    ///     throws in a tray click takes the click, not the pet, but neither is a good look.
    /// </summary>
    public sealed class SampleModule : IModule
    {
        private IHost _host;
        private ModulePaths _paths;
        private int _pokeCount;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            // The id is the module's identity everywhere: the folder, the settings key, the self-test flag,
            // the catalog entry. Changing it later orphans the user's settings.
            Id = "samplemodule",
            Name = "SAMPLE_DISPLAY_NAME",
            Version = "1.0.0",
            // Raise this only when you call an ABI member a newer host introduced. A module that requires a
            // host newer than the one shipped is refused forever, with a legible reason.
            MinHostVersion = "SAMPLE_MIN_HOST",
            Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;

            // The host provisions a per-module data directory; ModulePaths wraps it (and falls back to temp
            // if this module ever runs without the Storage permission). It SURVIVES a module update, so
            // durable state belongs here — never beside the installed exe.
            _paths = ModulePaths.FromStorage(host.GetStorage(Info.Id), Info.Id);

            // React to the pet. Handlers run on the UI thread; keep them quick and never throw.
            host.CompanionPoked += OnPetPoked;

            // A tray entry. Group/Order place it among the host's own items; IconPng is raw PNG bytes so the
            // ABI stays free of System.Drawing.
            //
            // PROJECT CONVENTION: every tray entry carries its OWN icon, and no two share one. The tray is
            // shared by the host and every installed module, so an icon-less row reads as a rendering bug
            // next to its neighbours, and two rows with the same glyph look like duplicates of each other.
            // Ship a 32x32 ARGB PNG as an EmbeddedResource and read it with EmbeddedResources.LoadBytes.
            // Adding a row at all should be deliberate: fold related actions into one entry with
            // BuildChildren rather than growing the top level.
            host.AddTrayItems(new List<TrayItem>
            {
                new TrayItem
                {
                    Label = "Say hello",
                    Group = 50,
                    Order = 0,
                    Click = SayHello,
                    // IconPng = EmbeddedResources.LoadBytes(typeof(SampleModule).Assembly, "icon.png"),
                },
            });

            // A settings pane. You declare fields as DATA and the host renders them, so a module needs no UI
            // framework. Load/Save round-trip through IModuleSettings, which the host persists (and encrypts
            // for a Secret field).
            host.AddOptionsPane(new OptionsPane
            {
                Title = "SAMPLE_DISPLAY_NAME",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "greeting",
                        Label = "What to say",
                        Kind = SettingKind.Text,
                        Group = "SAMPLE_DISPLAY_NAME",
                    },
                    new SettingField
                    {
                        Id = "enabled",
                        Label = "React when the pet is poked",
                        Kind = SettingKind.Bool,
                        Group = "SAMPLE_DISPLAY_NAME",
                    },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Test it", InvokeAsync = TestAsync, Group = "SAMPLE_DISPLAY_NAME" },
                },
            });
        }

        /// <summary>Release anything that outlives a normal collection: timers, windows, hotkeys, handles.
        /// The load context is unloaded after this returns.</summary>
        public void Shutdown()
        {
            if (_host != null) _host.CompanionPoked -= OnPetPoked;
            _host = null;
        }

        private void OnPetPoked(PokeInfo poke)
        {
            try
            {
                if (!Settings().GetBool("enabled", true)) return;
                _pokeCount++;
                if (_pokeCount % 3 != 0) return;   // don't talk over every poke
                SayHello();
            }
            catch { /* a module must never throw into the host */ }
        }

        private void SayHello()
        {
            if (_host == null) return;
            // Respect the user's global speech switch rather than talking over a silenced pet.
            if (!_host.SpeechEnabled) return;
            _host.SayAll(Settings().Get("greeting", "Hello!"));
        }

        private System.Threading.Tasks.Task<string> TestAsync()
        {
            SayHello();
            // The returned string is shown next to the button.
            return System.Threading.Tasks.Task.FromResult("Said it.");
        }

        private IModuleSettings Settings() { return _host.GetSettings(Info.Id); }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            IModuleSettings settings = Settings();
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "greeting", settings.Get("greeting", "Hello!") },
                { "enabled", settings.GetBool("enabled", true) ? "true" : "false" },
            };
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IModuleSettings settings = Settings();
            string value;
            if (values.TryGetValue("greeting", out value)) settings.Set("greeting", value);
            if (values.TryGetValue("enabled", out value)) settings.Set("enabled", value);
            return settings.Save();
        }

        /// <summary>
        /// The module's self-test, reached by REFLECTION from the host (which keeps no compile-time
        /// reference to any module), so keep this signature exactly as it is. Run it with no host change at
        /// all:
        ///
        ///     DesktopAICompanion.exe --module-selftest=samplemodule
        ///
        /// then add that flag to tests\run-gate.ps1 and .github\workflows\build.yml so CI runs it too.
        ///
        /// Assert what a user would otherwise have to discover: that Init contributes what you expect, that
        /// settings round-trip, and that behaviour fires. Never SKIP silently — the gate fails on a SKIP
        /// precisely because a skipped test reads exactly like a passing one.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var probe = new SelfTestProbe();
            try
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("samplemodule"))
                {
                    host.UseStorage("samplemodule", storage);

                    var module = new SampleModule();
                    module.Init(host);

                    probe.Check("contributes a tray item", host.TrayItems.Count == 1);
                    probe.Check("contributes a settings pane", host.OptionsPanes.Count == 1);
                    probe.Check("declares the permissions it uses",
                        module.Info.Permissions.HasFlag(ModulePermissions.Speech) &&
                        module.Info.Permissions.HasFlag(ModulePermissions.Storage));

                    // Settings round-trip through the pane's own Load/Save delegates.
                    OptionsPane pane = host.OptionsPanes[0];
                    probe.Check("the pane saves",
                        pane.Save(new Dictionary<string, string> { { "greeting", "hi" }, { "enabled", "true" } }));
                    IReadOnlyDictionary<string, string> loaded = pane.Load();
                    probe.Check("the pane reloads what it saved", loaded["greeting"] == "hi");

                    // Behaviour: the third poke speaks.
                    host.RaiseCompanionPoked(new PokeInfo());
                    host.RaiseCompanionPoked(new PokeInfo());
                    probe.Check("stays quiet on the first pokes", host.SaidLines.Count == 0);
                    host.RaiseCompanionPoked(new PokeInfo());
                    probe.Check("speaks on the third poke", host.SaidLines.Count == 1);
                    probe.Check("says what the setting holds",
                        host.SaidLines.Count == 1 && host.SaidLines[0] == "hi");

                    module.Shutdown();
                }
            }
            catch (Exception ex) { probe.Exception(ex); }
            return probe.Finish(out detail);
        }
    }
}
