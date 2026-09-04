using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.BlinkingLed
{
    /// <summary>
    /// Blinking LED: the pet keeps the machine looking awake by blinking the keyboard's Scroll Lock light.
    ///
    /// A port of the standalone BlinkingLED tray app into a module. The engine is unchanged in substance
    /// (<see cref="ScrollLockBlinker"/> synthesizes a Scroll Lock keypress on a two-phase timer); everything
    /// AROUND it is deleted, because the host already provides it: the tray entry, the options pane, the
    /// settings file, single-instance behaviour and start-with-Windows all come from being a module. That is
    /// most of what the standalone app's 1000 lines were.
    ///
    /// <para>
    /// Two behaviours are deliberately different from the standalone app, both because a module is not a
    /// process. Caps Lock ON used to QUIT; here it stops the blinking, since a module cannot quit the pet.
    /// And it never speaks at startup: the module auto-starts, and the pet has its own opening line that a
    /// second bubble would talk over. It speaks only when the user turns it off or back on.
    /// </para>
    /// </summary>
    public sealed class BlinkingLedModule : IModule
    {
        private IHost _host;
        private ScrollLockBlinker _blinker;
        private TrayItem _trayToggle;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "blinkingled",
            Name = "Blinking LED",
            Version = "1.0.0",   // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.0.4: payload refresh only, no behaviour change -- the bundled ModuleKit
                                 //        gained RecordingHost.RaiseFullscreenChanged (host 1.9.9).
                                 // 1.0.3: ONE tray row instead of two -- Off folded into the rate submenu, so
                                 //        picking a speed also switches it on -- plus the bulb icon.
                                 // 1.0.2: dropped the "Next blink" and "Last keypress" tray lines. They could
                                 //        only be a snapshot, and a stale countdown is not worth tray space.
                                 // 1.0.1: a dozen remarks per speed instead of one, picked at random and
                                 //        never repeating the previous line, so changing the rate stops
                                 //        being a recording.
            // Nothing here is newer than the ABI the 1.4 hosts shipped: tray items, an options pane, settings
            // and SayAll are all original. Deliberately NOT raised to the current host, so this installs on
            // whatever the user already has.
            MinHostVersion = "1.0.0",
            // Storage for its settings, Speech for the on/off line. The Scroll Lock keypress needs no
            // permission because it never goes through the host -- the module P/Invokes SendInput itself.
            // There is no ModulePermissions flag for synthesizing input, so the consent screen cannot state
            // it; the module name and description carry that disclosure instead.
            Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;

            _blinker = new ScrollLockBlinker();
            _blinker.CapsLockStopRequested += OnCapsLockStop;

            // ONE tray entry. The label carries the state and the submenu carries every action, so Off and
            // the six speeds are one decision in one place instead of a toggle plus a separate rate menu.
            // Picking a speed also turns it ON, which is what someone reaching for "Hyper" already means.
            //
            // The standalone app's live "Next blink" countdown and "Last SendInput" line are deliberately
            // absent: they could only ever be a snapshot taken when the menu opens (a module ships data and
            // the host renders it, so there is no way to push into an open menu), and a stale countdown is
            // worth less than the tray space. "Blink once now" in the options pane covers what they were for,
            // which is telling "doing nothing" apart from "being refused by Windows".
            host.AddTrayItems(new List<TrayItem>
            {
                // DynamicText rather than rewriting Label: the host re-evaluates it every time the menu
                // opens, so the on/off state cannot drift out of sync with the setting. Click stays null,
                // which is what makes this a pure submenu rather than a button that also has an arrow.
                (_trayToggle = new TrayItem
                {
                    Label = "Blinking LED",
                    Group = 50,
                    Order = 0,
                    IconPng = LoadIconResource("blinkingled.png"),
                    DynamicText = TrayToggleText,
                    BuildChildren = BuildRateMenu,
                }),
            });

            host.AddOptionsPane(new OptionsPane
            {
                Title = "Blinking LED",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "enabled",
                        Label = "Blink the Scroll Lock light",
                        Kind = SettingKind.Bool,
                        Group = "Blinking LED",
                    },
                    new SettingField
                    {
                        Id = "rate",
                        Label = "Blink rate",
                        Kind = SettingKind.Enum,
                        Options = ScrollLockBlinker.RateNames,
                        Group = "Blinking LED",
                    },
                    new SettingField
                    {
                        Id = "capsStops",
                        Label = "Stop when Caps Lock is on",
                        Kind = SettingKind.Bool,
                        Group = "Blinking LED",
                    },
                    new SettingField
                    {
                        Id = "announce",
                        Label = "Pet says when it is switched on or off",
                        Kind = SettingKind.Bool,
                        Group = "Blinking LED",
                    },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Blink once now", InvokeAsync = BlinkOnceAsync, Group = "Blinking LED" },
                },
            });

            ApplyState(false);   // false: starting up, so stay quiet whatever the state turns out to be
        }

        public void Shutdown()
        {
            if (_blinker != null)
            {
                _blinker.CapsLockStopRequested -= OnCapsLockStop;
                try { _blinker.Stop(); } catch { }   // leaves the LED off rather than stuck lit
                _blinker.Dispose();
                _blinker = null;
            }
            _host = null;
        }

        // ---- state ----------------------------------------------------------

        /// <summary>
        /// Bring the blinker in line with the settings. <paramref name="announce"/> is false for anything the
        /// USER did not just do (startup, a Caps Lock stop), which is what keeps the pet's opening quip from
        /// being talked over: the quiet path is structural, not a timing guess.
        /// </summary>
        private void ApplyState(bool announce)
        {
            if (_blinker == null) return;
            IModuleSettings s = Settings();
            bool enabled = s.GetBool("enabled", true);
            string rate = s.Get("rate", ScrollLockBlinker.DefaultRate);
            if (!ScrollLockBlinker.IsKnownRate(rate)) rate = ScrollLockBlinker.DefaultRate;

            _blinker.StopOnCapsLock = s.GetBool("capsStops", true);
            bool rateChanged = !string.Equals(rate, _appliedRate, StringComparison.Ordinal);
            _blinker.SetRate(rate);
            _appliedRate = rate;

            bool was = _blinker.IsRunning;
            if (enabled) _blinker.Start(); else _blinker.Stop();

            if (!announce) return;

            // At most ONE line per change, and on/off wins: flipping it off while also changing the speed
            // should not produce two bubbles talking over each other.
            if (was != _blinker.IsRunning) Announce(_blinker.IsRunning);
            else if (rateChanged) Say(PickRateQuip(rate));
        }

        // The rate the blinker is currently set to, so a change can be detected. Seeded on the first
        // ApplyState (which never announces), so startup can never be mistaken for a user changing the speed.
        private string _appliedRate;

        private void Announce(bool on)
        {
            Say(on
                ? "Keeping the lights on for you."
                : "Blinking off. You are on your own now.");
        }

        /// <summary>
        /// A dozen snarky lines per speed. Changing the rate is a deliberate act and the pet having an
        /// opinion about it is the point of this living in a pet rather than a tray app, but one fixed line
        /// per speed stops being funny the second time you see it.
        ///
        /// Each pool is written to the speed's actual cadence (Glacial really is one blink every four
        /// minutes; Hyper really is one a second), so the jokes stay true if someone re-tunes the intervals
        /// without re-reading these. The self-test pins that every pool is a dozen distinct lines and that no
        /// line is shared between speeds.
        /// </summary>
        private static string[] RateQuips(string rate)
        {
            switch (rate)
            {
                case "Glacial": return new[]
                {
                    "Glacial. I will blink again around the next ice age.",
                    "Glacial it is. See you in four minutes. Maybe.",
                    "At this rate the heat death of the universe gets here first.",
                    "Glacial. I have watched continents move faster.",
                    "One blink every four minutes. Riveting stuff.",
                    "Glacial. Wake me when the glaciers do.",
                    "Setting: barely alive. Excellent choice.",
                    "That is less a blink and more an occasional twitch.",
                    "Glacial. Your keyboard is now a very slow lighthouse.",
                    "I will be blinking. Eventually. Do not wait up.",
                    "Glacial. Somewhere, a sloth is taking notes.",
                    "Four minutes between blinks. Bold commitment to doing nothing.",
                };
                case "Sluggish": return new[]
                {
                    "Sluggish. Wake me if anything ever happens.",
                    "Two minutes between blinks. We are not in a hurry.",
                    "Sluggish. The pace of a Monday morning.",
                    "I will blink twice an hour and call it a career.",
                    "Sluggish it is. Low effort, high dignity.",
                    "That is the blink rate of someone avoiding their inbox.",
                    "Sluggish. Practically meditative.",
                    "Every two minutes. Just often enough to prove I am alive.",
                    "Sluggish. I respect the commitment to conserving energy.",
                    "Slow and steady loses the race but keeps the lights on.",
                    "Sluggish. This is the blink equivalent of a long sigh.",
                    "Two whole minutes. I will find something to do.",
                };
                case "Slow": return new[]
                {
                    "Slow, but dignified. We are pacing ourselves.",
                    "Slow. Unhurried. Faintly smug about it.",
                    "Every twelve seconds. Very reasonable of you.",
                    "Slow. The tempo of someone who knows lunch is coming.",
                    "I can work with slow. Barely.",
                    "Slow it is. No sudden movements.",
                    "Twelve seconds between blinks. Practically contemplative.",
                    "Slow. Like a metronome for people who dislike music.",
                    "A gentle blink. Nothing alarming. Very on brand.",
                    "Slow. The pace of someone actually reading the terms and conditions.",
                    "Fine, slow. I will pretend that was a considered decision.",
                    "Slow. Steady. Deeply unremarkable.",
                };
                case "Normal": return new[]
                {
                    "Normal. How refreshingly unambitious of you.",
                    "Normal. The setting for people who do not have opinions.",
                    "Straight down the middle. Bold.",
                    "Normal it is. Nobody was ever fired for choosing normal.",
                    "The default. Truly, a choice was made here.",
                    "Normal. I will try to contain my excitement.",
                    "You went back to normal. Character development.",
                    "Normal. Beige, but functional.",
                    "A perfectly adequate blink rate for a perfectly adequate day.",
                    "Normal. The blink rate equivalent of plain toast.",
                    "Middle of the road, where all the safest decisions live.",
                    "Normal. I would call it inspired if it were remotely inspired.",
                };
                case "Fast": return new[]
                {
                    "Fast. Someone is pretending to look busy.",
                    "Every two seconds. Who exactly are we performing for?",
                    "Fast it is. Deadline energy.",
                    "That is the blink rate of a person with a status meeting at three.",
                    "Fast. I hope your manager is watching, because I am.",
                    "Two seconds apart. This is caffeine in LED form.",
                    "Fast. Somebody has been asked for an update.",
                    "Fast. The light is working harder than you are.",
                    "Rapid blinking. Very convincing. Nobody suspects a thing.",
                    "Fast. We are simulating productivity at scale now.",
                    "Fast. I admire the commitment to appearing available.",
                    "Every two seconds. Frantic, but in a professional way.",
                };
                case "Hyper": return new[]
                {
                    "Hyper. Your keyboard is a strobe light now. Hope nobody is watching.",
                    "Hyper. This is no longer subtle.",
                    "Every second. This is a cry for help with extra steps.",
                    "Hyper. Your desk now has its own weather warning.",
                    "At this speed it stops looking like activity and starts looking like a distress signal.",
                    "Hyper. I hope nobody nearby has opinions about flashing lights.",
                    "Full strobe. Somewhere a colleague is squinting at your desk.",
                    "Hyper. Subtlety has left the building.",
                    "One blink a second. This is a nightclub now.",
                    "Hyper. Nobody has ever looked busy this aggressively.",
                    "Hyper. We have moved from present to alarming.",
                    "This is not blinking. This is Morse code for panic.",
                };
                default: return new[]
                {
                    "Fine, that speed then.",
                    "An unusual choice, but you are the one with the keyboard.",
                };
            }
        }

        // The last line spoken, so the same one never lands twice in a row. With a dozen options a repeat is
        // uncommon but not rare (roughly one change in twelve), and a repeat is exactly the thing that makes
        // a random pool feel broken.
        private string _lastQuip;

        private string PickRateQuip(string rate)
        {
            string[] pool = RateQuips(rate);
            if (pool.Length == 0) return "";
            int index = Random.Shared.Next(pool.Length);
            // Step to the neighbour rather than re-rolling: guaranteed to terminate, guaranteed different.
            if (pool.Length > 1 && string.Equals(pool[index], _lastQuip, StringComparison.Ordinal))
                index = (index + 1) % pool.Length;
            _lastQuip = pool[index];
            return _lastQuip;
        }

        private void Say(string line)
        {
            if (_host == null || string.IsNullOrEmpty(line)) return;
            if (!Settings().GetBool("announce", true)) return;
            if (!_host.SpeechEnabled) return;   // never talk over a deliberately silenced pet
            _host.SayAll(line);
        }

        // Tray-item icon (TrayItem.IconPng): raw PNG bytes from this module's own embedded resource, so the
        // base renders it without the ABI depending on System.Drawing. Null on any failure, which degrades to
        // an icon-less entry rather than breaking the tray. The glyph is the standalone app's own bulb.
        private static byte[] LoadIconResource(string fileName)
        {
            return EmbeddedResources.LoadBytes(typeof(BlinkingLedModule).Assembly, fileName);
        }

        private string TrayToggleText()
        {
            try { return Settings().GetBool("enabled", true) ? "Blinking LED: on" : "Blinking LED: off"; }
            catch { return "Blinking LED"; }
        }


        /// <summary>
        /// Off, then the six speeds, with a tick on whichever is live. Rebuilt on every open, so the tick
        /// follows the setting for free. "Off" is a rate-menu entry rather than a separate toggle because
        /// off IS a choice about how fast it blinks, and folding it in costs one less tray row.
        /// </summary>
        private IEnumerable<TrayItem> BuildRateMenu()
        {
            var items = new List<TrayItem>();
            string current;
            bool enabled;
            try
            {
                IModuleSettings s = Settings();
                current = s.Get("rate", ScrollLockBlinker.DefaultRate);
                enabled = s.GetBool("enabled", true);
            }
            catch { current = ScrollLockBlinker.DefaultRate; enabled = true; }

            items.Add(new TrayItem
            {
                Label = (enabled ? "    " : "✓ ") + "Off",
                Group = 0,
                Order = 0,
                Click = delegate { SetEnabledFromTray(false); },
            });

            int order = 1;
            foreach (string name in ScrollLockBlinker.RateNames)
            {
                string rate = name;   // capture per iteration, not the loop variable
                bool ticked = enabled && string.Equals(rate, current, StringComparison.Ordinal);
                items.Add(new TrayItem
                {
                    Label = (ticked ? "✓ " : "    ") + rate,
                    Group = 0,
                    Order = order++,
                    Click = delegate { SetRateFromTray(rate); },
                });
            }
            return items;
        }

        /// <summary>Picking a speed also switches it ON. Someone reaching into the menu for "Hyper" while it
        /// is off means "blink, fast", not "remember this for later".</summary>
        private void SetRateFromTray(string rate)
        {
            try
            {
                if (!ScrollLockBlinker.IsKnownRate(rate)) return;
                IModuleSettings s = Settings();
                bool sameRate = string.Equals(s.Get("rate", ScrollLockBlinker.DefaultRate), rate, StringComparison.Ordinal);
                bool alreadyOn = s.GetBool("enabled", true);
                if (sameRate && alreadyOn) return;   // picking what is already live is not worth a remark
                s.Set("rate", rate);
                s.Set("enabled", "true");
                s.Save();
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        private void SetEnabledFromTray(bool on)
        {
            try
            {
                IModuleSettings s = Settings();
                if (s.GetBool("enabled", true) == on) return;
                s.Set("enabled", on ? "true" : "false");
                s.Save();
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        /// <summary>Caps Lock came on and the setting says stop. The standalone app quit here; a module
        /// cannot, so it stops and persists that, otherwise the next settings read would restart it. No label
        /// to update: the tray entry reads its state through DynamicText when the menu next opens.</summary>
        private void OnCapsLockStop()
        {
            try
            {
                IModuleSettings s = Settings();
                s.Set("enabled", "false");
                s.Save();
                // Deliberately silent: the user hit Caps Lock, which IS the feedback, and this can fire while
                // they are typing.
            }
            catch { }
        }

        private System.Threading.Tasks.Task<string> BlinkOnceAsync()
        {
            if (_blinker == null) return System.Threading.Tasks.Task.FromResult("Not running.");
            long before = _blinker.ToggleCount;
            _blinker.BlinkOnce();
            bool moved = _blinker.ToggleCount > before;
            return System.Threading.Tasks.Task.FromResult(moved
                ? "Toggled Scroll Lock (watch the light)."
                : "Windows refused the input (error " +
                  _blinker.LastWin32Error.ToString(CultureInfo.InvariantCulture) + ").");
        }

        // ---- settings -------------------------------------------------------

        // GetSettings can return null when a module is constructed outside a live host (the options schema is
        // built during Init), which is what made two modules fail to load with a NullReferenceException.
        private IModuleSettings Settings()
        {
            return (_host != null ? _host.GetSettings(Info.Id) : null) ?? new MemoryModuleSettings();
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            IModuleSettings s = Settings();
            string rate = s.Get("rate", ScrollLockBlinker.DefaultRate);
            if (!ScrollLockBlinker.IsKnownRate(rate)) rate = ScrollLockBlinker.DefaultRate;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "enabled", s.GetBool("enabled", true) ? "true" : "false" },
                { "rate", rate },
                { "capsStops", s.GetBool("capsStops", true) ? "true" : "false" },
                { "announce", s.GetBool("announce", true) ? "true" : "false" },
            };
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IModuleSettings s = Settings();
            string v;
            if (values.TryGetValue("enabled", out v)) s.Set("enabled", v);
            if (values.TryGetValue("rate", out v) && ScrollLockBlinker.IsKnownRate(v)) s.Set("rate", v);
            if (values.TryGetValue("capsStops", out v)) s.Set("capsStops", v);
            if (values.TryGetValue("announce", out v)) s.Set("announce", v);
            bool ok = s.Save();
            ApplyState(true);   // the user was just here, so an on/off change may speak
            return ok;
        }

        // ---- self-test ------------------------------------------------------

        /// <summary>
        /// <c>DesktopAICompanion.exe --module-selftest=blinkingled</c>.
        ///
        /// Found by REFLECTION on the assembly, which takes the FIRST method with this signature it finds, so
        /// this must be the only <c>SelfTest(out string)</c> in the module. Helpers are named SelfCheck.
        ///
        /// Deliberately asserts no LED: whether Scroll Lock physically toggles depends on the machine, the
        /// session and whether input is blocked, so a check on it would be flaky and would fail on a headless
        /// CI runner. What IS asserted is everything that can be wrong without touching hardware -- the
        /// contributions, the rate table, the settings round-trip, and the two behaviours that are easy to
        /// regress: silence at startup, and speech on a user toggle.
        /// </summary>
        /// <summary>
        /// Project convention: every tray entry carries its own icon, and no two entries share one. An
        /// icon-less row reads as a rendering bug next to its neighbours, and two rows with the same glyph
        /// are worse than none, because they look like duplicates of each other.
        /// </summary>
        internal static bool EveryTrayEntryHasAUniqueIcon(IEnumerable<TrayItem> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (TrayItem item in items)
            {
                if (item == null) return false;
                if (item.IconPng == null || item.IconPng.Length == 0) return false;
                if (!seen.Add(Convert.ToBase64String(item.IconPng))) return false;
            }
            return true;
        }

        public static bool SelfTest(out string detail)
        {
            var probe = new SelfTestProbe();
            try
            {
                var host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopAICompanion.ModuleKit.Testing.TempModuleStorage("blinkingled"))
                {
                    host.UseStorage("blinkingled", storage);

                    var module = new BlinkingLedModule();
                    module.Init(host);

                    // Exactly ONE tray entry. An equality, not a minimum: this module shares the tray with the
                    // host and five others, so growing it must be a deliberate decision, and folding Off into
                    // the rate submenu is what bought the row back.
                    probe.Check("contributes exactly one tray entry", host.TrayItems.Count == 1);

                    // Project convention: every tray entry carries its own icon, and no two share one.
                    probe.Check("every tray entry has an icon", EveryTrayEntryHasAUniqueIcon(host.TrayItems));

                    // A pure submenu: a Click here would make the parent both a button and a menu, so a
                    // click meant for "open the list" would silently toggle something.
                    probe.Check("the tray entry is a pure submenu, not a button with an arrow",
                        host.TrayItems[0].Click == null && host.TrayItems[0].BuildChildren != null);

                    // The rate submenu is built lazily, so it is only ever exercised if something opens it.
                    // Index 0, not 1: the toggle and the rate menu were merged into this single entry.
                    TrayItem rateMenu = host.TrayItems[0];
                    probe.Check("the rate tray item is a submenu", rateMenu.BuildChildren != null);
                    var rateChildren = new List<TrayItem>(rateMenu.BuildChildren());
                    probe.Check("the submenu offers Off plus every rate",
                        rateChildren.Count == ScrollLockBlinker.RateNames.Length + 1);
                    probe.Check("Off is the first entry",
                        rateChildren.Count > 0 && rateChildren[0].Label != null &&
                        rateChildren[0].Label.EndsWith("Off", StringComparison.Ordinal));
                    int ticked = 0;
                    foreach (TrayItem child in rateChildren)
                        if (child.Label != null && child.Label.StartsWith("✓", StringComparison.Ordinal)) ticked++;
                    probe.Check("exactly one entry is ticked as current", ticked == 1);

                    // Picking a speed while it is OFF must switch it on, or the menu silently does nothing.
                    host.OptionsPanes[0].Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Normal" },
                        { "capsStops", "false" }, { "announce", "false" },
                    });
                    module.SetRateFromTray("Fast");
                    IReadOnlyDictionary<string, string> afterPick = host.OptionsPanes[0].Load();
                    probe.Check("picking a speed while off turns it on",
                        afterPick["enabled"] == "true" && afterPick["rate"] == "Fast");

                    // ...and Off turns it off without disturbing the remembered speed.
                    module.SetEnabledFromTray(false);
                    IReadOnlyDictionary<string, string> afterOff = host.OptionsPanes[0].Load();
                    probe.Check("Off switches it off and keeps the chosen speed",
                        afterOff["enabled"] == "false" && afterOff["rate"] == "Fast");
                    probe.Check("contributes a settings pane", host.OptionsPanes.Count == 1);
                    probe.Check("declares the permissions it uses",
                        module.Info.Permissions.HasFlag(ModulePermissions.Speech) &&
                        module.Info.Permissions.HasFlag(ModulePermissions.Storage));
                    probe.Check("declares no permission it does not use",
                        !module.Info.Permissions.HasFlag(ModulePermissions.Network) &&
                        !module.Info.Permissions.HasFlag(ModulePermissions.ScreenContext));

                    // The behaviour the maintainer asked for by name: the module auto-starts, so Init must
                    // NOT speak, or it talks over the pet's own opening line.
                    probe.Check("says nothing at startup", host.SaidLines.Count == 0);

                    OptionsPane pane = host.OptionsPanes[0];

                    // Rate table: every advertised option must resolve, and they must be ordered slowest to
                    // fastest. A typo'd name silently falling back to Normal is exactly the bug that would
                    // otherwise ship (SetRate accepts anything).
                    SettingField rateField = null;
                    foreach (SettingField f in pane.Schema)
                        if (f.Id == "rate") rateField = f;
                    probe.Check("the pane offers the rate list", rateField != null && rateField.Options != null);

                    bool everyRateKnown = true;
                    int previousOff = int.MaxValue;
                    bool descending = true;
                    foreach (string name in ScrollLockBlinker.RateNames)
                    {
                        if (!ScrollLockBlinker.IsKnownRate(name)) everyRateKnown = false;
                        int on, off;
                        ScrollLockBlinker.DurationsFor(name, out on, out off);
                        if (on <= 0 || off <= 0) everyRateKnown = false;
                        if (off > previousOff) descending = false;
                        previousOff = off;
                    }
                    probe.Check("every advertised rate resolves to a real interval", everyRateKnown);
                    probe.Check("rates run slowest to fastest", descending);
                    probe.Check("an unknown rate is rejected rather than silently accepted",
                        !ScrollLockBlinker.IsKnownRate("Blistering"));

                    // Settings round-trip through the pane's own delegates. Every speech assertion below
                    // measures a DELTA rather than an absolute count, so each one fails on its own merits:
                    // an absolute count makes them all cascade off whatever the first one did.
                    int said = host.SaidLines.Count;
                    probe.Check("the pane saves", pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Hyper" },
                        { "capsStops", "false" }, { "announce", "true" },
                    }));
                    IReadOnlyDictionary<string, string> loaded = pane.Load();
                    probe.Check("the pane reloads what it saved",
                        loaded["enabled"] == "false" && loaded["rate"] == "Hyper" &&
                        loaded["capsStops"] == "false");

                    // Turning it OFF is a user action, so it speaks exactly once.
                    probe.Check("speaks when the user switches it off", host.SaidLines.Count - said == 1);

                    // From here the state is: off, Hyper. Each save below changes exactly ONE thing, so a
                    // failure names the behaviour that actually broke instead of a bundle of them.

                    // A save that changes nothing must stay quiet, or every visit to the pane makes the pet
                    // talk.
                    int before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Hyper" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("stays quiet when nothing changed", host.SaidLines.Count == before);

                    // An invalid rate must neither corrupt the stored value nor provoke a remark.
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Blistering" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("a bogus rate leaves the stored one alone", pane.Load()["rate"] == "Hyper");
                    probe.Check("a bogus rate provokes no remark", host.SaidLines.Count == before);

                    // Changing the SPEED gets its own remark, and a different one per speed.
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("remarks when the speed changes", host.SaidLines.Count - before == 1);
                    probe.Check("the remark is one of the lines for the speed just picked",
                        host.SaidLines.Count > 0 &&
                        Array.IndexOf(RateQuips("Slow"), host.SaidLines[host.SaidLines.Count - 1]) >= 0);

                    // Each speed needs a POOL, or "so it is not the same thing every time" is not delivered.
                    bool poolsBigEnough = true;
                    bool poolsInternallyDistinct = true;
                    var allQuips = new HashSet<string>(StringComparer.Ordinal);
                    bool sharedAcrossSpeeds = false;
                    foreach (string name in ScrollLockBlinker.RateNames)
                    {
                        string[] pool = RateQuips(name);
                        if (pool.Length < 12) poolsBigEnough = false;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string line in pool)
                        {
                            if (string.IsNullOrWhiteSpace(line)) poolsInternallyDistinct = false;
                            if (!seen.Add(line)) poolsInternallyDistinct = false;
                            if (!allQuips.Add(line)) sharedAcrossSpeeds = true;
                        }
                    }
                    probe.Check("every speed has at least a dozen lines", poolsBigEnough);
                    probe.Check("no speed repeats a line within its own pool", poolsInternallyDistinct);
                    probe.Check("no line is shared between two speeds", !sharedAcrossSpeeds);
                    probe.Check("an unknown speed still has something to say",
                        RateQuips("Blistering").Length > 0);

                    // The picker must actually USE the pool. A picker hardwired to pool[0] passes every
                    // assertion above, so draw repeatedly and require both variety and no back-to-back
                    // repeat. 200 draws over 12 lines: seeing only one distinct line is not a flake, it is a
                    // bug, and a consecutive repeat is impossible by construction rather than by luck.
                    var drawn = new HashSet<string>(StringComparer.Ordinal);
                    string previous = null;
                    bool neverRepeatsBackToBack = true;
                    for (int i = 0; i < 200; i++)
                    {
                        string line = module.PickRateQuip("Hyper");
                        if (Array.IndexOf(RateQuips("Hyper"), line) < 0) neverRepeatsBackToBack = false;
                        if (previous != null && line == previous) neverRepeatsBackToBack = false;
                        previous = line;
                        drawn.Add(line);
                    }
                    // Require the WHOLE pool, not merely "more than one". A picker hardwired to pool[0] would
                    // still bounce between indexes 0 and 1 off the no-repeat guard and so satisfy "> 1";
                    // demanding every line closes that. Coupon collector over 12 lines expects ~37 draws, so
                    // missing one in 200 has probability around 3e-7. That is a bug, not a flake.
                    probe.Check("the picker eventually uses every line in the pool",
                        drawn.Count == RateQuips("Hyper").Length);
                    probe.Check("the picker never repeats the previous line back to back",
                        neverRepeatsBackToBack);

                    // Re-picking the SAME speed must not talk, or clicking through the tray menu gets chatty.
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("stays quiet when the speed did not actually change",
                        host.SaidLines.Count == before);

                    // Turning it on AND changing speed at once is ONE line, not two talking over each other.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Glacial" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("an on/off plus speed change speaks once, not twice",
                        host.SaidLines.Count - before == 1);

                    // And with announce off, a real toggle is silent.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "false" },
                    });
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "false" },
                    });
                    probe.Check("respects the announce setting", host.SaidLines.Count == before);

                    module.Shutdown();
                }
            }
            catch (Exception ex) { probe.Exception(ex); }
            return probe.Finish(out detail);
        }
    }
}
