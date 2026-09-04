using System;
using System.Collections.Generic;
using System.Windows.Forms;
using DesktopAICompanion.Properties;
using DesktopAICompanion.Modules;   // TrayItem (module tray contributions)
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion
{
        /// <summary>
        /// The only way to interact with the application (not the pet itself) is over the context menu.<br />
        /// This menu is available once you press on the tray icon (near the windows clock).
        /// </summary>
    class ContextMenus : IDisposable
    {
            /// <summary>
            /// "Add a companion" submenu: lists the built-in default plus every local pet type; its icon
            /// tracks the active pet. Children are (re)built each time the submenu opens.
            /// </summary>
        static ToolStripMenuItem addPetMenuItem;
            /// <summary>
            /// "Remove a companion" submenu: lists the pet types currently on screen with their counts.
            /// </summary>
        static ToolStripMenuItem removePetMenuItem;
            /// <summary>
            /// Close Menu Item: removes all pets and closes the app.
            /// </summary>
        static ToolStripMenuItem closeSheepMenuItem;
            /// <summary>Display name of the active/default pet, for the Remove submenu's "" entry.</summary>
        static string activePetName = "Sheep";
            /// <summary>
            /// Test Speech item — visibility tracks the SpeechEnabled setting.
            /// </summary>
        static ToolStripMenuItem testSpeechMenuItem;
            /// <summary>Pet Speech cascade — per-pet choice of which module speaks. Visibility tracks
            /// SpeechEnabled alongside Test Speech: routing speech that cannot happen is a bad menu.</summary>
        static ToolStripMenuItem petSpeechMenuItem;
        private ContextMenuStrip ownedMenu;
        // Module-contributed tray items (S5a), (re)built from the plugin host's collected TrayItems each time
        // the menu opens, so Visible/DynamicText/BuildChildren are re-evaluated live and late-loaded modules
        // still appear. Tracked here so the prior batch can be removed before the next rebuild.
        private readonly List<ToolStripItem> moduleTrayItems = new List<ToolStripItem>();

        // The AI-brain tray items (Ask / Enable-Disable) moved out with the AI-brain module (S4b); the AI
        // brain now controls itself via its own setting + hotkey. The module contributes its own tray items
        // once the tray is assembled from module contributions (S5).

        /// <summary>
        /// Called by FormOptions when SpeechEnabled is toggled so the menu items show/hide live.
        /// </summary>
        public static void RefreshSpeechMenuItem()
        {
            bool enabled = Program.MyData.GetSpeechEnabled();
            if (testSpeechMenuItem != null) testSpeechMenuItem.Visible = enabled;
            if (petSpeechMenuItem != null) petSpeechMenuItem.Visible = enabled;
        }

        /// <summary>
        /// Rebuild the module-contributed tray section each time the menu opens: remove the previous batch,
        /// then merge the plugin host's collected <see cref="TrayItem"/>s (sorted by Group then Order, with a
        /// separator between groups) just after Test Speech. Visible/DynamicText are evaluated live here and
        /// BuildChildren submenus are built lazily on open. Fully defensive — a throwing module item can never
        /// break the core tray.
        /// </summary>
        private void ModuleTray_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            ContextMenuStrip menu = ownedMenu;
            if (menu == null) return;
            try
            {
                foreach (ToolStripItem prior in moduleTrayItems)
                {
                    // Each rebuild decodes a fresh Image from the module's IconPng bytes (BuildModuleMenuItem
                    // below) that nothing else references, so it must be disposed here or it leaks every time
                    // the tray menu opens.
                    try { if (prior.Image != null) prior.Image.Dispose(); } catch { }
                    try { menu.Items.Remove(prior); prior.Dispose(); } catch { }
                }
                moduleTrayItems.Clear();

                DesktopAICompanion.Plugins.CompanionHost host = Program.Mainthread != null ? Program.Mainthread.Host : null;
                if (host == null || host.TrayItems == null || host.TrayItems.Count == 0) return;

                var ordered = new List<TrayItem>(host.TrayItems);
                ordered.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Order.CompareTo(b.Order));

                // Anchor after Pet Speech, not Test Speech: Pet Speech was inserted between them, so anchoring
                // on Test Speech would drop module items in the middle of the base's own speech block.
                ToolStripMenuItem afterItem = petSpeechMenuItem ?? testSpeechMenuItem;
                int anchor = afterItem != null ? menu.Items.IndexOf(afterItem) + 1 : menu.Items.Count;
                if (anchor < 0 || anchor > menu.Items.Count) anchor = menu.Items.Count;

                int insertAt = anchor;
                int lastGroup = int.MinValue;
                bool any = false;
                foreach (TrayItem ti in ordered)
                {
                    if (ti == null) continue;
                    bool visible = true;
                    try { if (ti.Visible != null) visible = ti.Visible(); } catch { visible = false; }
                    if (!visible) continue;

                    if (any && ti.Group != lastGroup)
                    {
                        var groupSep = new ToolStripSeparator();
                        menu.Items.Insert(insertAt++, groupSep);
                        moduleTrayItems.Add(groupSep);
                    }
                    ToolStripMenuItem mi = BuildModuleMenuItem(ti);
                    menu.Items.Insert(insertAt++, mi);
                    moduleTrayItems.Add(mi);
                    lastGroup = ti.Group;
                    any = true;
                }
                if (any)
                {
                    var tailSep = new ToolStripSeparator();
                    menu.Items.Insert(insertAt++, tailSep);
                    moduleTrayItems.Add(tailSep);
                }
            }
            catch { /* a bad module must never break the tray */ }
        }

        private static ToolStripMenuItem BuildModuleMenuItem(TrayItem ti)
        {
            string text = ti.Label ?? "";
            try { if (ti.DynamicText != null) { string dt = ti.DynamicText(); if (!string.IsNullOrEmpty(dt)) text = dt; } } catch { }
            var mi = new ToolStripMenuItem { Text = text };
            if (ti.IconPng != null && ti.IconPng.Length > 0)
            {
                try
                {
                    // Clone into an independent Bitmap before the stream disposes -- GDI+ can lazily reference
                    // the source stream, and reading a disposed one later throws "A generic error occurred in GDI+".
                    using (var stream = new System.IO.MemoryStream(ti.IconPng))
                    using (var decoded = new System.Drawing.Bitmap(stream))
                        mi.Image = new System.Drawing.Bitmap(decoded);
                }
                catch { /* malformed module icon bytes must never break the tray */ }
            }
            if (ti.BuildChildren != null)
            {
                mi.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
                TrayItem captured = ti;
                mi.DropDownOpening += (s, e) => RebuildModuleSubmenu(mi, captured);
            }
            if (ti.Click != null)
            {
                Action click = ti.Click;
                mi.Click += (s, e) => { try { click(); } catch { } };
            }
            return mi;
        }

        private static void RebuildModuleSubmenu(ToolStripMenuItem parent, TrayItem ti)
        {
            try
            {
                parent.DropDownItems.Clear();
                IEnumerable<TrayItem> children = null;
                try { if (ti.BuildChildren != null) children = ti.BuildChildren(); } catch { children = null; }
                if (children != null)
                {
                    foreach (TrayItem c in children)
                    {
                        if (c == null) continue;
                        bool vis = true;
                        try { if (c.Visible != null) vis = c.Visible(); } catch { vis = false; }
                        if (!vis) continue;
                        parent.DropDownItems.Add(BuildModuleMenuItem(c));
                    }
                }
                if (parent.DropDownItems.Count == 0)
                    parent.DropDownItems.Add(new ToolStripMenuItem { Text = "(none)", Enabled = false });
            }
            catch { }
        }

        /// <summary>
        /// A value to set in the About dialog: author.
        /// </summary>
        static string author;
            /// <summary>
            /// A value to set in the About dialog: animation version.
            /// </summary>
        static string version;
            /// <summary>
            /// A value to set in the About dialog: animation title.
            /// </summary>
        static string title;
            /// <summary>
            /// A value to set in the About dialog: description and information about the animation.
            /// </summary>
        static string info;

        bool isAboutLoaded = false;
        bool isOptionLoaded = false;

        /// <summary>
        /// Creates this instance for the tray icon.
        /// </summary>
        /// <returns>ContextMenuStrip to add in the tray icon.</returns>
        public ContextMenuStrip Create()
        {
                // Add the default menu options.
            ContextMenuStrip menu = new ContextMenuStrip();
            ownedMenu = menu;
            ToolStripMenuItem item;
            ToolStripSeparator sep;

			// Item: Add a pet (submenu of pet types; built on open). Different types can coexist.
			addPetMenuItem = new ToolStripMenuItem { Text = "&Add a companion" };
            addPetMenuItem.Image = Resources.icon.ToBitmap();
            addPetMenuItem.Font = new Font(addPetMenuItem.Font, addPetMenuItem.Font.Style | FontStyle.Bold);
            addPetMenuItem.DropDownOpening += AddPetMenu_Opening;
            // A placeholder child so the arrow shows before the first open.
            addPetMenuItem.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
            menu.Items.Add(addPetMenuItem);

            // Item: Remove a pet (submenu of on-screen types with counts; built on open).
            removePetMenuItem = new ToolStripMenuItem { Text = "&Remove a companion" };
            removePetMenuItem.Image = Resources.removepet;
            removePetMenuItem.DropDownOpening += RemovePetMenu_Opening;
            removePetMenuItem.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
            menu.Items.Add(removePetMenuItem);

            // Item: Test Speech (optional — hidden when speech disabled)
            item = new ToolStripMenuItem { Text = "&Test Speech" };
            item.Image = Resources.speechbubble;
            item.Click += (s, ev) =>
                Program.Mainthread.SayAll(
                    "Hello! I'm your desktop companion. Right-click the tray icon for options.");
            item.Visible = Program.MyData.GetSpeechEnabled();
            testSpeechMenuItem = item;
            menu.Items.Add(item);

            // Item: Pet Speech (which source speaks for each pet; submenus built on open).
            // Distinct cloud-bubble glyph so it doesn't duplicate Test Speech's rounded bubble above
            // (and it collides with neither the sheep on Add-a-pet nor the gear on Options).
            petSpeechMenuItem = new ToolStripMenuItem { Text = "Companion &Speech" };
            petSpeechMenuItem.Image = Resources.petspeech;
            petSpeechMenuItem.DropDownOpening += PetSpeechMenu_Opening;
            petSpeechMenuItem.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
            petSpeechMenuItem.Visible = Program.MyData.GetSpeechEnabled();
            menu.Items.Add(petSpeechMenuItem);

            // Module tray contributions (S5a) are merged in here (just after Test Speech, before Options),
            // rebuilt on each open so their Visible/DynamicText re-evaluate and late-loaded modules appear.
            menu.Opening += ModuleTray_Opening;

			// Item: Options.
			item = new ToolStripMenuItem
			{
				Text = "&Options"
			};
			item.Click += new EventHandler(Options_Click);
            item.Image = Resources.option;
            menu.Items.Add(item);

                // Item: Separator.
            sep = new ToolStripSeparator();
            menu.Items.Add(sep);

			// Item: About (also hosts the usage/help content — the separate Help dialog was folded in).
			item = new ToolStripMenuItem
			{
				Text = "A&bout / Help"
			};
			item.Click += new EventHandler(About_Click);
            item.Image = Resources.about;
            menu.Items.Add(item);

            // Item: Separator.
            sep = new ToolStripSeparator();
            menu.Items.Add(sep);

			// Item: Close application.
			closeSheepMenuItem = new ToolStripMenuItem
			{
				Text = "&Remove all companions and Close"
			};
			closeSheepMenuItem.Click += new EventHandler(Exit_Click);
            closeSheepMenuItem.Image = Resources.exit;
            menu.Items.Add(closeSheepMenuItem);

            return menu;
        }

        /// <summary>
        /// Set a new icon in the context menu with the new pet and updated the info to show in the about dialog.<br />
        /// This function is called every time a new pet is loaded.
        /// </summary>
        /// <param name="newIcon">Icon with the new image.</param>
        /// <param name="petName">Name of the pet, to show in the context menu.</param>
        /// <param name="aboutAuthor">Name of the author.</param>
        /// <param name="aboutTitle">Title of the animation.</param>
        /// <param name="aboutVersion">Version of the animation.</param>
        /// <param name="aboutInfo">About the animation (copyright and author information)</param>
        static public void UpdateIcon(Icon newIcon, string petName, string aboutAuthor, string aboutTitle, string aboutVersion, string aboutInfo)
        {
            activePetName = string.IsNullOrWhiteSpace(petName) ? "Sheep" : petName;
            if (addPetMenuItem != null && newIcon != null)
            {
                Image oldImage = addPetMenuItem.Image;
                addPetMenuItem.Image = newIcon.ToBitmap();
                if (oldImage != null) oldImage.Dispose();
            }

            author = aboutAuthor;
            title = aboutTitle;
            version = aboutVersion;
            info = aboutInfo;
        }

        // Display name for a tray menu entry: the active/default pet ("") shows the running pet's name;
        // a folder id shows its curated catalog name (Pearl, Rick, ...). internal rather than private so the
        // Preferences speaker dropdown labels pets the same way the tray does, from one implementation.
        internal static string TrayPetName(string id)
        {
            // DisplayNameForId, not DisplayName(id, null): the latter has no catalog name to consult and
            // falls through to the prettified folder id, so every tray surface that holds only an id read
            // "Shimeji 3x56f4pl" while "Add a companion" read "Monkey D. Luffy" for the same pet.
            return string.IsNullOrEmpty(id) ? activePetName : CompanionCatalog.DisplayNameForId(id);
        }

        // Rebuild the "Add a companion" submenu each time it opens so freshly downloaded pets appear. The
        // built-in default is first; each entry spawns one of that type alongside any existing pets.
        void AddPetMenu_Opening(object sender, EventArgs e)
        {
            addPetMenuItem.DropDownItems.Clear();
            bool full = Program.Mainthread != null && Program.Mainthread.IsAtMaxPets;
            foreach (CompanionCatalog.CompanionInfo info in CompanionCatalog.EnumerateLocal())
            {
                // Add the specific pet the entry names — the built-in adds the default eSheep, not the
                // active pet (id "" means "active", which would add the wrong pet after "Use this companion").
                string id = info.IsBuiltIn ? CompanionCatalog.BuiltInPetId : (info.Id ?? "");
                var child = new ToolStripMenuItem { Text = info.DisplayName, Enabled = !full };
                child.Click += delegate
                {
                    if (Program.Mainthread != null) Program.Mainthread.AddPetFromTray(id);
                };
                addPetMenuItem.DropDownItems.Add(child);
            }
            if (full)
            {
                addPetMenuItem.DropDownItems.Add(new ToolStripSeparator());
                addPetMenuItem.DropDownItems.Add(
                    new ToolStripMenuItem { Text = "(maximum companions reached)", Enabled = false });
            }
        }

        // Rebuild the "Remove a companion" submenu each time it opens from the current on-screen mix, e.g.
        // "Pearl x2" / "Rick x1". Each entry removes one pet of that type.
        void RemovePetMenu_Opening(object sender, EventArgs e)
        {
            removePetMenuItem.DropDownItems.Clear();
            System.Collections.Generic.List<CompanionCountEntry> mix =
                Program.Mainthread != null ? Program.Mainthread.OnScreenMix() : null;
            if (mix != null)
            {
                foreach (CompanionCountEntry entry in mix)
                {
                    string id = entry.Id ?? "";
                    string text = TrayPetName(id) + " ×" + entry.Count;
                    var child = new ToolStripMenuItem { Text = text };
                    child.Click += delegate
                    {
                        if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(id);
                    };
                    removePetMenuItem.DropDownItems.Add(child);
                }
            }
            if (removePetMenuItem.DropDownItems.Count == 0)
                removePetMenuItem.DropDownItems.Add(
                    new ToolStripMenuItem { Text = "(no companions on screen)", Enabled = false });
        }

        /// <summary>
        /// The routing key a pet's speech preference is stored under. NOT the mix id: the mix writes the
        /// active/default pet as "", while "" in triggerSpeech already means the ALL-PETS entry. Keying a real
        /// pet as "" would silently rewrite the global preference and still look correct, because the lookup
        /// falls back to global -- every other pet type would test fine. Shared with CompanionHost.SpeechRoutingKey
        /// so the tray and the runtime cannot disagree about what a pet's key is.
        /// </summary>
        private static string SpeechRoutingKey(string mixId)
        {
            if (!string.IsNullOrEmpty(mixId)) return mixId;
            try { return Program.MyData != null ? (Program.MyData.GetActivePetId() ?? "") : ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Rebuild the "Companion Speech" cascade on open: one submenu per pet type on screen, plus an "All companions"
        /// row, each listing the installed speech sources with a tick on the EFFECTIVE one (a pet with no
        /// entry of its own shows the all-pets choice, which is what actually happens).
        ///
        /// Host-owned rather than module-contributed: per-pet preferences belong to the host by an existing
        /// decision, a module cascade would need the Pets permission just to enumerate, and TrayItem has no
        /// Checked. Pets come from OnScreenMix(), the one enumeration that already excludes previews.
        /// </summary>
        void PetSpeechMenu_Opening(object sender, EventArgs e)
        {
            petSpeechMenuItem.DropDownItems.Clear();
            try
            {
                System.Collections.Generic.List<string> labels;
                System.Collections.Generic.Dictionary<string, string> labelToModule;
                System.Collections.Generic.Dictionary<string, string> moduleToLabel;
                DesktopAICompanion.Wpf.OptionsShell.BuildTriggerSpeechOptions(out labels, out labelToModule, out moduleToLabel);

                System.Collections.Generic.List<CompanionCountEntry> mix =
                    Program.Mainthread != null ? Program.Mainthread.OnScreenMix() : null;
                if (mix != null)
                    foreach (CompanionCountEntry entry in mix)
                    {
                        string mixId = entry.Id ?? "";
                        // No "xN" suffix, unlike Remove: the count is irrelevant to a per-TYPE setting and
                        // showing it would imply each copy can be configured separately.
                        petSpeechMenuItem.DropDownItems.Add(
                            BuildSpeechSourceMenu(TrayPetName(mixId), SpeechRoutingKey(mixId), labels, labelToModule, moduleToLabel));
                    }

                if (petSpeechMenuItem.DropDownItems.Count == 0)
                {
                    petSpeechMenuItem.DropDownItems.Add(
                        new ToolStripMenuItem { Text = "(no companions on screen)", Enabled = false });
                    return;
                }

                petSpeechMenuItem.DropDownItems.Add(new ToolStripSeparator());
                petSpeechMenuItem.DropDownItems.Add(
                    BuildSpeechSourceMenu("All companions", "", labels, labelToModule, moduleToLabel));

                // A way back: per-pet entries survive the Preferences reset by design, so without this a
                // choice could outlive the pet it was made for with no way to clear it.
                var reset = new ToolStripMenuItem { Text = "Reset all companions to the default" };
                reset.Click += delegate { ResetAllPetSpeech(); };
                petSpeechMenuItem.DropDownItems.Add(new ToolStripSeparator());
                petSpeechMenuItem.DropDownItems.Add(reset);
            }
            catch
            {
                petSpeechMenuItem.DropDownItems.Clear();
                petSpeechMenuItem.DropDownItems.Add(
                    new ToolStripMenuItem { Text = "(unavailable)", Enabled = false });
            }
        }

        /// <summary>One pet's source list, ticked on the effective choice. Clicking writes it live -- the
        /// arbitration reads the preference at fire time, so there is nothing to invalidate.</summary>
        private static ToolStripMenuItem BuildSpeechSourceMenu(
            string title,
            string routingKey,
            System.Collections.Generic.List<string> labels,
            System.Collections.Generic.Dictionary<string, string> labelToModule,
            System.Collections.Generic.Dictionary<string, string> moduleToLabel)
        {
            var parent = new ToolStripMenuItem { Text = title };
            string current = "";
            try { if (Program.MyData != null) current = Program.MyData.GetTriggerSpeechModule(routingKey) ?? ""; }
            catch { current = ""; }

            foreach (string label in labels)
            {
                string moduleId;
                if (!labelToModule.TryGetValue(label, out moduleId)) moduleId = "";
                var child = new ToolStripMenuItem
                {
                    Text = label,
                    Checked = string.Equals(moduleId, current, StringComparison.OrdinalIgnoreCase),
                };
                string key = routingKey;
                string chosen = moduleId;
                child.Click += delegate
                {
                    try { if (Program.MyData != null) Program.MyData.SetTriggerSpeechModule(key, chosen); }
                    catch { }
                };
                parent.DropDownItems.Add(child);
            }

            // A source the user chose and then uninstalled leaves that pet permanently silent, because an
            // explicit choice is a restriction rather than a preference. Say so instead of showing the default
            // as ticked, which would claim behaviour the runtime does not have.
            if (current.Length > 0 && !moduleToLabel.ContainsKey(current))
            {
                parent.DropDownItems.Insert(0, new ToolStripSeparator());
                parent.DropDownItems.Insert(0, new ToolStripMenuItem
                {
                    Text = current + " — not installed",
                    Enabled = false,
                    Checked = true,
                });
            }

            if (parent.DropDownItems.Count == 0)
                parent.DropDownItems.Add(
                    new ToolStripMenuItem { Text = "(no speech sources installed)", Enabled = false });
            return parent;
        }

        /// <summary>Clear every per-pet speech choice AND the all-pets one, so everything inherits the
        /// default again.</summary>
        private static void ResetAllPetSpeech()
        {
            try
            {
                LocalData data = Program.MyData;
                if (data == null) return;
                foreach (string key in data.TriggerSpeechPetIds()) data.SetTriggerSpeechModule(key, "");
                data.SetTriggerSpeechModule("", "");
            }
            catch { }
        }


        /// <summary>
        /// Handles the Click event of the About control. Open a dialog if no other dialog is still opened.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void About_Click(object sender, EventArgs e)
        {
            if (isOptionLoaded) return;
            if (isAboutLoaded) return;
            isAboutLoaded = true;
            try
            {
                // Themed WPF About window (the WinForms AboutBox + Help dialog were retired and folded into
                // this one window); ShowDialog is modal, but the re-entry guard stays for parity/safety.
                DesktopAICompanion.Wpf.OptionsShell.OpenAbout(author, title, version, info);
            }
            finally
            {
                isAboutLoaded = false;
            }
        }

        /// <summary>
        /// Handles the Click event of the Option control. Open a dialog if no other dialog is still opened.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void Options_Click(object sender, EventArgs e)
        {
            if (isAboutLoaded) return;
            isOptionLoaded = true;
            try
            {
                DesktopAICompanion.Wpf.OptionsShell.Open();   // WPF settings (the classic FormOptions dialog was retired in S5b-3)
            }
            finally
            {
                isOptionLoaded = false;
            }
        }

            /// <summary>
            /// Processes a menu item. Will close the application after closing all pets.
            /// </summary>
            /// <param name="sender">The sender.</param>
            /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void Exit_Click(object sender, EventArgs e)
        {
            // Quit without further ado.
            //Application.Exit();
            Program.Mainthread.KillSheeps(true);
        }


        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        public void Dispose()
        {
            ContextMenuStrip menu = ownedMenu;
            ownedMenu = null;
            if (menu != null) { menu.Opening -= ModuleTray_Opening; menu.Dispose(); }
            moduleTrayItems.Clear();
            addPetMenuItem = null;
            removePetMenuItem = null;
            closeSheepMenuItem = null;
            testSpeechMenuItem = null;
            petSpeechMenuItem = null;
        }
    }
}
