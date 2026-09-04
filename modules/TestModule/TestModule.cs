using System;
using System.Collections.Generic;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.TestModule
{
    /// <summary>Reference module for S1: subscribes to a lifecycle event and contributes UI, exercising
    /// the whole ABI (events + services + tray/options contributions) with no real behavior.
    ///
    /// It also carries the only live exercise of the pet-manager ABI. This module is never published (no
    /// modules-dist entry), so its tray items are a developer's way to drive ICompanionManager -- including the
    /// preview spawn -- through a real AssemblyLoadContext against the real host, which is the only way to
    /// eyeball that a preview pet stays out of settings.json and out of the tray's Remove submenu. Building
    /// it is also the compile-time proof that the ABI is sufficient to write a pet-authoring module against.
    /// </summary>
    public sealed class TestModule : IModule
    {
        private IHost _host;
        private ICompanionPreview _preview;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "testmodule",
            Name = "Test Module",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.Companions,
        };

        public void Init(IHost host)
        {
            _host = host;
            host.CompanionPoked += OnPoked;
            host.AddTrayItems(new List<TrayItem>
            {
                new TrayItem { Label = "TestModule OK", Group = 9, Order = 0, Click = TrayClicked },
                new TrayItem
                {
                    Label = "Preview a pet from XML",
                    Group = 9,
                    Order = 1,
                    Visible = delegate { return _preview == null || !_preview.IsAlive; },
                    Click = PreviewClicked,
                },
                new TrayItem
                {
                    Label = "Remove the preview pet",
                    Group = 9,
                    Order = 2,
                    Visible = delegate { return _preview != null && _preview.IsAlive; },
                    Click = RemovePreviewClicked,
                },
            });
            host.AddOptionsPane(new OptionsPane
            {
                Title = "Test Module",
                Schema = new List<SettingField>
                {
                    new SettingField { Id = "greeting", Label = "Greeting", Kind = SettingKind.Text },
                },
            });
        }

        // Say(pet, ...), not SayAll: a poke is a reaction belonging to the pet that was poked. This is the
        // reference module, so it should demonstrate the policy rather than the bug it replaced.
        private void OnPoked(PokeInfo info)
        {
            if (_host == null || info == null) return;
            if (info.Pet != null) _host.Say(info.Pet, "poked!"); else _host.SayAll("poked!");
        }
        private void TrayClicked() { if (_host != null) _host.SayAll("test module tray click"); }

        /// <summary>
        /// Spawn a preview from a pet the user already has installed, read back through the pet manager.
        /// A real authoring module would pass the XML its editor is holding; the point here is only to drive
        /// the SpawnPreview verb end to end.
        /// </summary>
        private void PreviewClicked()
        {
            if (_host == null) return;
            ICompanionManager pets = _host.GetCompanionManager(Info.Id);
            string error;

            // Any installed type will do as sample XML; prefer a non-built-in one so the preview is visibly
            // a different pet from the one already on screen.
            string xml = null;
            foreach (CompanionTypeInfo type in pets.InstalledTypes())
            {
                if (type == null || type.IsBuiltIn) continue;
                xml = ReadInstalledXml(type.TypeId);
                if (xml != null) break;
            }
            if (xml == null)
            {
                _host.SayAll("No installed pet to preview. Download one from Options, Pets first.");
                return;
            }

            if (!pets.ValidateXml(xml, out error))
            {
                _host.SayAll("Preview XML failed validation: " + error);
                return;
            }

            _preview = pets.SpawnPreview(xml, out error);
            _host.SayAll(_preview != null
                ? "Preview pet spawned. It is NOT in your saved pet mix."
                : "Preview refused: " + error);
        }

        private void RemovePreviewClicked()
        {
            ICompanionPreview preview = _preview;
            _preview = null;
            if (preview != null) preview.Remove();
            if (_host != null) _host.SayAll("Preview removed.");
        }

        /// <summary>Read an installed pet's animations.xml. A module has no path helpers by design, so this
        /// walks the same per-user library location the host installs pets into.</summary>
        private static string ReadInstalledXml(string typeId)
        {
            try
            {
                if (string.IsNullOrEmpty(typeId)) return null;
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string path = System.IO.Path.Combine(root, "DesktopAICompanion", "pets", typeId, "animations.xml");
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        public void Shutdown()
        {
            ICompanionPreview preview = _preview;
            _preview = null;
            if (preview != null) { try { preview.Remove(); } catch { } }
            if (_host != null) _host.CompanionPoked -= OnPoked;
        }
    }
}
