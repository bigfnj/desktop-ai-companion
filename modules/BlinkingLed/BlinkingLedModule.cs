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

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "blinkingled",
            Name = "Blinking LED",
            Version = "1.0.6",   // 1.0.6: the blinker's belief about the LED moves only with the LED. A
                                 //        Scroll Lock press Windows refused (a locked session, a UAC prompt,
                                 //        an elevated foreground window) no longer flips the phase flag, and
                                 //        enabling the feature after a "Blink once now" reconciles the flag
                                 //        with the key it lit instead of zeroing it; either one ran the
                                 //        cadence inverted for the session and disarmed Stop()'s corrective
                                 //        toggle whenever the LED was lit (BUG-011). The Caps Lock stop and
                                 //        the tray picks now say when the settings write failed, and the
                                 //        self-test leaves the developer's Scroll Lock where it found it.
                                 //        The self-test also reads the settings back after every failed
                                 //        tray write and asserts the click did nothing, now that the
                                 //        ModuleKit fake shows the disk after a failed Save() (N-blinkingled-02).
                                 //        Start() adopts the key's state, so a Scroll Lock the user had lit
                                 //        before enabling no longer runs the cadence inverted, and Stop()
                                 //        clears it: stopping always leaves the light off (N-blinkingled-01).
                                 //        Switching it on while Caps Lock is already on (with "Stop when
                                 //        Caps Lock is on" set) no longer speaks the ON line and then stops
                                 //        itself one dark gap later in silence: it does not start, persists
                                 //        the off and says why (RA-088). A "Blink once now" while the
                                 //        cadence runs re-arms the timer from the phase the key is in, so the
                                 //        LED is not left lit for the rest of the gap (RA-094 c), and a
                                 //        corrective toggle in Stop() that throws is recorded as win32=-1
                                 //        like the other two writers (R-024). The self-test drives Caps Lock
                                 //        through a new CapsLockReader seam. Shutdown() clears the light only
                                 //        if this session ever pressed the key: a headless host that Inits the
                                 //        module over a lit Scroll Lock and shuts it down no longer switches the
                                 //        user's light off on the way out (RA-090; the gate's four headless
                                 //        Inits did, once per run). The cadence tick re-syncs its belief
                                 //        from the key before toggling, so a Scroll Lock the user presses
                                 //        mid-run costs one interval, not the rest of the run, and Stop()
                                 //        still leaves the light off (N-burn-blinkingled-02, RA-094 b).
                                 // 1.0.5: "Blink once now" no longer strands the LED when the feature was
                                 //        ALREADY off. 1.0.4 fixed only the other ordering (blink, then
                                 //        switch off); Stop() returned early on !_running, so a blink made
                                 //        with the feature off was never reconciled, and ticking the
                                 //        feature on afterwards ran the whole cadence inverted. The
                                 //        self-test that was supposed to cover this constructed an
                                 //        unstarted blinker and asserted only that a flag flipped.
                                 // 1.0.4: "Blink once now" no longer leaves the LED stuck lit when the feature
                                 //        is switched off afterwards.
                                 // 1.0.3: the LED never blinked on x64, on any machine. The
                                 //        Win32 INPUT union must be sized by its LARGEST member
                                 //        and was sized by KEYBDINPUT, so cbSize was 32 where
                                 //        SendInput requires 40 and every call was refused with
                                 //        ERROR_INVALID_PARAMETER (87). Measured both shapes in
                                 //        one process: 32 -> sent=0 err=87, 40 -> sent=2 err=0.
                                 // 1.0.1: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded). NO functional change here; the bump exists
                                 //        because the catalog offers an update by VERSION, so without it the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
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
            // Storage for its settings, Speech for the on/off line, InputSynthesis for the Scroll
            // Lock keypress. That last one needs no permission to WORK -- it never goes through the
            // host, the module P/Invokes SendInput itself -- which is exactly why the flag is a
            // disclosure and not a gate.
            //
            // Until 2026-09-17 there was no flag for synthesizing input, and this comment said "the
            // module name and description carry that disclosure instead". A module name is not a
            // permission disclosure: the pane prints "wants: Speech, Storage" beside it, which is an
            // affirmative claim that those are the two things the module does. MinHostVersion stays
            // at 1.0.0 -- a 1.1.4 host drops a permission name it does not know and keeps the entry,
            // so declaring this strands nobody.
            Permissions = ModulePermissions.Speech
                          | ModulePermissions.Storage
                          | ModulePermissions.InputSynthesis,
        };

        public void Init(IHost host)
        {
            _host = host;

            // Give the engine a way into the diagnostic log, BEFORE it exists, so the very first toggle it
            // attempts is already recorded. Until this was wired the module's entire diagnostic story was
            // silence: a Scroll Lock press Windows refuses leaves the LED dark with the pet having just
            // announced it was keeping the lights on.
            ScrollLockBlinker.LogSink = delegate(string line)
            {
                IHost h = _host;
                if (h == null) return;
                try { h.Log(Info.Id, line); } catch { }
            };

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
                new TrayItem
                {
                    Label = "Blinking LED",
                    Group = 50,
                    Order = 0,
                    IconPng = LoadIconResource("blinkingled.png"),
                    DynamicText = TrayToggleText,
                    BuildChildren = BuildRateMenu,
                },
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
                        Label = "Companion says when it is switched on or off",
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
                // Clear the light only if this session ever tried to drive it: a tick, a "Blink once now", a
                // corrective toggle (AttemptCount counts every press, accepted or refused). A blinker that
                // adopted a lit key at Start() and never pressed anything has left the LED exactly as the user
                // had it, so there is nothing of ours to leave off, and Stop()'s corrective toggle would switch
                // the user's light off for real on the way out (RA-090). That is what every headless host that
                // Inits this module and shuts it down without a message loop was doing since Start() adopts the
                // key (cf27664): the convention runner under --module-selftest=blinkingled and the three
                // bundled-root loads under --module-host-selftest each pressed a lit Scroll Lock off once per
                // gate run, outside the suite's parity window. In the shipped app the difference is one lit
                // interval wide: an exit within _onMs (0.5 s on Hyper, 4 s on Glacial) of starting over a key
                // the user had lit leaves that key lit, where variant C's Stop() cleared it; after the first
                // tick nothing changes. The engine's Stop() keeps variant C in full (a user's Off after enable
                // still clears an adopted key); this is the module's own rule for its Shutdown alone, recorded
                // under `#### burn/blinkingled` in docs/DESIGN-REGISTER.md for the owner to keep or overturn.
                if (_blinker.AttemptCount > 0) { try { _blinker.Stop(); } catch { } }   // leaves the LED off rather than stuck lit
                _blinker.Dispose();
                _blinker = null;
            }
            // Drop the sink with the host it closes over: the field is static, so leaving it set would hand
            // a reloaded module's engine a delegate pointing at the previous instance.
            ScrollLockBlinker.LogSink = null;
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
            // The user is switching it ON (a tray pick, the pane's Apply) while Caps Lock is already on and
            // "Stop when Caps Lock is on" is set: do not start, say why, persist the off (RA-088). Starting
            // would speak the ON line and then, one dark gap later (2 s on Fast, 240 s on Glacial), the first
            // tick would find Caps Lock, stop, persist off and say nothing, since the tick's silence is written
            // for a user who has just pressed the key; from this user's side the feature refused to switch on
            // with no explanation, and a repeat pick replayed the whole cycle. Only the enable TRANSITION on a
            // user gesture: at startup (announce false) nothing was said, so there is nothing to correct and
            // the first tick stops it as before; a rate change while running is not an enable, and the next
            // tick stops that too.
            if (enabled && !was && announce && _blinker.CapsLockStopsNow())
            {
                RefuseEnableUnderCapsLock(s);
                return;
            }
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

        /// <summary>
        /// One diagnostic line from the module itself (the engine has its own sink). Never throws, and never
        /// carries anything but the module's own vocabulary: rate names, counts, error numbers.
        /// </summary>
        private void Log(string message)
        {
            IHost host = _host;
            if (host == null || string.IsNullOrEmpty(message)) return;
            try { host.Log(Info.Id, message); } catch { }
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
                // Save() reports a failed write (a full disk, a locked or read-only settings.json) by
                // returning false, never by throwing: the host's store swallows the I/O exception, and so
                // does every other IModuleSettings in the repo. ApplyState re-reads the FILE, so a failed
                // write leaves the live state exactly as it was and the click did nothing; until this line
                // nothing said so (F110). Logging only, deliberately: driving the blinker from the in-memory
                // values would run a state the tray, which reads the file, denies, and "disk wins" is at
                // least consistent.
                if (!s.Save()) Log("tray pick not persisted (rate " + rate + ", on), so the live state is unchanged");
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
                // Same as SetRateFromTray: a false from Save() is the only way a failed write is reported.
                if (!s.Save()) Log("tray pick not persisted (" + (on ? "on" : "off") + "), so the live state is unchanged");
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        /// <summary>Caps Lock came on and the setting says stop. The standalone app quit here; a module
        /// cannot, so it stops and persists that, otherwise the next settings read would restart it. No label
        /// to update: the tray entry reads its state through DynamicText when the menu next opens.</summary>
        private void OnCapsLockStop()
        {
            // The blinking has ALREADY stopped -- the engine stops before raising this -- so the line
            // records a transition that has happened, and it says WHICH WAY the settings write went. Save()
            // reports a failed write by returning false, never by throwing: the host's store swallows the
            // I/O exception, MemoryModuleSettings always answers false, the ModuleKit fake answers
            // !FailSaves. The previous shape logged "saved it as off" BEFORE the save and caught an
            // exception no implementation raises (its Categorize helper had that one dead caller), so a
            // full disk or a locked settings.json produced a false "saved" line and nothing else, and the
            // next settings read started the blinking again with the log claiming the stop was persisted
            // (F110). One line either way, so the self-test's "exactly one line" holds and a reader of the
            // log gets the two states told apart, which is what that catch was written to do.
            //
            // This is the one state change the user is never told about: the remark is deliberately
            // suppressed (they pressed the key, and it can fire mid-typing) and the OFF is PERSISTED, so a
            // week later the feature is off, the tray agrees it is off, and nothing anywhere says that Caps
            // Lock did it. "It stops switching itself on" is a plausible bug report with no other evidence.
            bool saved;
            try
            {
                IModuleSettings s = Settings();
                s.Set("enabled", "false");
                saved = s.Save();
            }
            catch { saved = false; }   // a third-party host that throws; every shipped store returns false
            Log(saved
                ? "caps lock is on: stopped blinking and saved it as off"
                : "caps lock is on: stopped blinking but the off was NOT persisted, so the next settings read will start it again");
        }

        /// <summary>The user switched it on while Caps Lock was already on (RA-088). The off is persisted
        /// through the same Save()-checked path the Caps Lock stop uses, one log line says which way the write
        /// went, and ONE Caps-Lock-specific line replaces the ON line. Refusing while leaving enabled=true was
        /// rejected: the tray and the pane would say "on" with nothing running and nothing that would ever
        /// start it, because no timer runs while stopped and only the next settings change reaches ApplyState;
        /// that is the disk/live drift the "disk wins" rule exists to avoid.</summary>
        private void RefuseEnableUnderCapsLock(IModuleSettings s)
        {
            // `persisted`, not `saved`: the F110 mutation case "the Caps Lock stop reports 'saved' whatever Save()
            // answered" matches OnCapsLockStop's `saved = s.Save();` byte-exactly, and a second copy of that line
            // would turn the case into a NO-OP (pattern matched 2 times), which the harness scores as a failure.
            bool persisted;
            try { s.Set("enabled", "false"); persisted = s.Save(); }
            catch { persisted = false; }   // as in OnCapsLockStop: every shipped store returns false rather than throwing
            Log(persisted
                ? "caps lock is on: did not start blinking and saved it as off"
                : "caps lock is on: did not start blinking but the off was NOT persisted, so the next settings read will try again");
            Say("Caps Lock is on, so I am sitting this one out. Switch it off and pick a speed again.");
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
        /// Found by REFLECTION: <c>ModuleConventionSelfTest.TryFindSelfTest</c> takes the public static
        /// <c>bool SelfTest(out string)</c> on THIS module type; only if the module type had none would it scan
        /// the other <c>IModule</c> types and then every type, and it reports more than one match as ambiguous
        /// rather than picking the first (F339 retired the first-match walk). The private helpers below are
        /// neither public nor named SelfTest, so the finder never sees them.
        ///
        /// Deliberately asserts no LED: whether Scroll Lock physically toggles depends on the machine, the
        /// session and whether input is blocked, so a check on it would be flaky and would fail on a headless
        /// CI runner. What IS asserted is everything that can be wrong without touching hardware -- the
        /// contributions, the rate table, the settings round-trip, and the two behaviours that are easy to
        /// regress: silence at startup, and speech on a user toggle.
        /// </summary>
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
                    // The module's own blinker reads the developer's REAL Scroll Lock, and since 2026-09-30
                    // Start() adopts whatever it reads (N-blinkingled-01). Init has ALREADY run ApplyState(false)
                    // by this line, so on a box whose key is lit this instance adopted it before the pin could
                    // land; what the pin does is make every Stop() below (the pane saves, Shutdown) read dark
                    // and drop that belief WITHOUT pressing, so no unpaired real press moves the developer's
                    // LED and the parity assertion (F113) is about this instance's two paired presses alone.
                    // The instance the convention runner Init'd through the real loader has no pin and sits
                    // outside that window (RA-090). The adoption itself is asserted on private probes further
                    // down through the same seam.
                    module._blinker.ScrollLockReader = delegate { return false; };
                    // Caps Lock likewise: pinned dark, so the pane saves and tray picks below start whatever the
                    // developer's own Caps Lock is doing; the RA-088 section swaps "on" in through this seam.
                    module._blinker.CapsLockReader = delegate { return false; };

                    // Exactly ONE tray entry. An equality, not a minimum: this module shares the tray with the
                    // host and five others, so growing it must be a deliberate decision, and folding Off into
                    // the rate submenu is what bought the row back.
                    probe.Check("contributes exactly one tray entry", host.TrayItems.Count == 1);

                    // Project convention: every tray entry carries its own icon, and no two share one. The
                    // check itself moved to ModuleKit -- this module was the ONLY one asserting it while all
                    // six share one notification-area menu with the host, and a convention one module checks
                    // is not a convention. The ModuleKit version also names the offending row on failure.
                    DesktopAICompanion.ModuleKit.Testing.TrayConventions.CheckTrayIcons(probe, host.TrayItems);

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
                    // InputSynthesis is the disclosure the 2026-09-17 change existed for, and it gates
                    // nothing at runtime (the module P/Invokes SendInput itself), so this line is the only
                    // thing that notices it gone; without it the pane went back to printing "wants: Speech,
                    // Storage" beside a module that synthesizes input, under a green suite (F111).
                    probe.Check("declares the permissions it uses, including the InputSynthesis disclosure",
                        module.Info.Permissions.HasFlag(ModulePermissions.Speech) &&
                        module.Info.Permissions.HasFlag(ModulePermissions.Storage) &&
                        module.Info.Permissions.HasFlag(ModulePermissions.InputSynthesis));
                    probe.Check("declares no permission it does not use",
                        !module.Info.Permissions.HasFlag(ModulePermissions.Network) &&
                        !module.Info.Permissions.HasFlag(ModulePermissions.ScreenContext));

                    // The behaviour the maintainer asked for by name: the module auto-starts, so Init must
                    // NOT speak, or it talks over the pet's own opening line.
                    probe.Check("says nothing at startup", host.SaidLines.Count == 0);

                    OptionsPane pane = host.OptionsPanes[0];

                    // Rate table: every advertised option must resolve, and the pane must offer exactly the
                    // table. A typo'd name silently falling back to Normal is exactly the bug that would
                    // otherwise ship (SetRate accepts anything).
                    SettingField rateField = null;
                    foreach (SettingField f in pane.Schema)
                        if (f.Id == "rate") rateField = f;

                    // PINNED per row -- the name AND both phases -- because the two conditions this loop used
                    // to test were each unconditionally true:
                    //
                    //   IsKnownRate(name) is DEFINED as membership in RateNames, and the loop iterates
                    //   RateNames. It asked whether each element of a list is in that list.
                    //
                    //   DurationsFor has a `default:` arm that returns 2500/7500, so `on <= 0 || off <= 0`
                    //   is false for every string in the universe, not merely for every advertised rate.
                    //
                    // Mutation-proven: renaming RateNames[4] "Fast" -> "Fastt" kept the old loop green.
                    // IsKnownRate found the typo in the mutated array, DurationsFor fell through to the
                    // default's 2500/7500 (both positive), and the descending guard is `off > previousOff`
                    // so 7500 > 7500 is false. Meanwhile the pane offers the options straight off that array,
                    // so a user picking "Fastt" silently got Normal's cadence.
                    string[] expectName = { "Glacial", "Sluggish", "Slow", "Normal", "Fast", "Hyper" };
                    int[] expectOn = { 4000, 3500, 3000, 2500, 1000, 500 };
                    int[] expectOff = { 240000, 120000, 12000, 7500, 2000, 1000 };   // slowest to fastest, by inspection

                    // Against the LITERAL, not against ScrollLockBlinker.RateNames: the pane's Options IS that
                    // array (Init assigns the reference), so the previous comparison asked whether RateNames
                    // equals RateNames and no engine edit could fail it (RA-091). Pinned to the table, it fails
                    // when either list drifts from it, which is the divergence its name describes.
                    probe.Check("the pane offers exactly the pinned rate table",
                        rateField != null && rateField.Options != null &&
                        string.Join("|", rateField.Options) == string.Join("|", expectName));

                    bool everyRateKnown = ScrollLockBlinker.RateNames.Length == expectName.Length;
                    for (int i = 0; everyRateKnown && i < expectName.Length; i++)
                    {
                        string name = ScrollLockBlinker.RateNames[i];
                        if (!string.Equals(name, expectName[i], StringComparison.Ordinal)) { everyRateKnown = false; break; }
                        if (!ScrollLockBlinker.IsKnownRate(name)) everyRateKnown = false;
                        int on, off;
                        ScrollLockBlinker.DurationsFor(name, out on, out off);
                        if (on != expectOn[i] || off != expectOff[i]) everyRateKnown = false;
                    }
                    // "Rates run slowest to fastest" was a separate Check here. It followed by arithmetic from the
                    // expectOff row (240000 > 120000 > 12000 > 7500 > 2000 > 1000) and could not fail while the
                    // row check passed (RA-091), so the order is pinned by that literal and stated beside it.
                    probe.Check("every advertised rate resolves to its OWN interval, not the default", everyRateKnown);
                    probe.Check("an unknown rate is rejected rather than silently accepted",
                        !ScrollLockBlinker.IsKnownRate("Blistering"));

                    // Settings round-trip through the pane's own delegates. Every speech assertion below
                    // measures a DELTA rather than an absolute count, so each one fails on its own merits:
                    // an absolute count makes them all cascade off whatever the first one did.
                    // The blinker is already OFF here (SetEnabledFromTray(false) above), so an off-save is
                    // not a transition at all: ApplyState's `was != IsRunning` branch is not taken and the
                    // one line this used to count was the RATE quip from the else-if. Deleting the
                    // Announce call left the suite green, and the two strings this module exists to say had
                    // no coverage anywhere in the repo. Turn it ON first, and pin the LITERAL -- counting
                    // lines cannot tell an on-line from an off-line from a rate quip.
                    int said = host.SaidLines.Count;
                    probe.Check("the pane saves", pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Fast" },
                        { "capsStops", "false" }, { "announce", "true" },
                    }));
                    probe.Check("WITNESS speaks the ON line when the user switches it on",
                        host.SaidLines.Count - said == 1 &&
                        host.SaidLines[host.SaidLines.Count - 1] == "Keeping the lights on for you.");

                    said = host.SaidLines.Count;
                    probe.Check("the pane saves", pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Hyper" },
                        { "capsStops", "false" }, { "announce", "true" },
                    }));
                    IReadOnlyDictionary<string, string> loaded = pane.Load();
                    probe.Check("the pane reloads what it saved",
                        loaded["enabled"] == "false" && loaded["rate"] == "Hyper" &&
                        loaded["capsStops"] == "false");

                    // Turning it OFF is a user action, so it speaks exactly once -- and the on/off line wins
                    // over the rate change in the same save, which is what pinning the literal proves.
                    probe.Check("WITNESS speaks the OFF line when the user switches it off",
                        host.SaidLines.Count - said == 1 &&
                        host.SaidLines[host.SaidLines.Count - 1] == "Blinking off. You are on your own now.");

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

                    // ---- diagnostics (IHost.Log) ------------------------------------------------------
                    // THE WIRING, not the wording. This module emitted nothing at all until the sink in
                    // Init existed, so the assertion that matters is that a blink attempt reaches
                    // IHost.Log; unwire the delegate and the four checks below are the ones that fail.

                    // A blink attempt records its outcome WHICHEVER WAY IT WENT, which is what lets this be
                    // asserted at all: the first attempt is a null-to-known transition either way, so this
                    // holds on a machine that accepts synthesized input and on a headless runner that
                    // refuses it. (>= 1 rather than == 1 because the blink timer may already have toggled
                    // once by now if something is pumping messages.)
                    // THE SIZE, FIRST, because the delivery assertion below cannot catch a
                    // broken one. SendInput validates cbSize and answers ERROR_INVALID_PARAMETER
                    // when it is wrong; the INPUT union was sized by KEYBDINPUT rather than by
                    // its largest member, so cbSize was 32 where x64 requires 40 and the LED
                    // never blinked on any machine. It shipped because the delivery assertion is
                    // deliberately outcome-agnostic -- it has to pass on a headless runner that
                    // refuses synthesized input -- which makes a permanently broken interop look
                    // exactly like a runner doing its job.
                    //
                    // A marshalled struct size is the same answer on every machine, headless or
                    // not, so this one has no excuse to be vague.
                    probe.Check("WITNESS the INPUT struct is the size SendInput demands ("
                                + ScrollLockBlinker.MarshalledInputSize + " vs "
                                + ScrollLockBlinker.RequiredInputSize + ")",
                        ScrollLockBlinker.MarshalledInputSize == ScrollLockBlinker.RequiredInputSize);

                    // ONE real keypress, here, and its pair at the end of this section. Everything between
                    // them delivers through the KeypressSender seam and never reaches Windows, so the
                    // developer's Scroll Lock ends where the machine had it. The previous suite made nine
                    // unpaired real toggles plus one that fired only when the key read lit, under a comment
                    // saying every toggle was paired, and left the LED ON after every gate run on a box that
                    // accepts synthesized input (F113; measured on this one: OFF before, ON after, and ON
                    // again after a second run).
                    long realBefore = ScrollLockBlinker.RealKeypressCount;
                    int deliveryLinesBefore = CountLogged(host.LoggedLines, "blink delivery");
                    module._blinker.BlinkOnce();
                    probe.Check("a blink attempt records whether Windows accepted it",
                        CountLogged(host.LoggedLines, "blink delivery") - deliveryLinesBefore >= 1);
                    // The interop itself, on that one real call: accepted or refused is the machine's answer
                    // and neither is asserted, but a THROW (a DllImport that does not resolve, a marshalling
                    // shape the runtime rejects) is ours, is the same answer on every machine, and would hide
                    // behind the outcome-agnostic line above exactly as the cbSize defect did.
                    probe.Check("the real SendInput call completes without throwing (-1 is this module's marker for a throw)",
                        module._blinker.LastWin32Error != -1);

                    // ---- the belief and the key (F116, F115, BUG-011) --------------------------------
                    // Every probe below substitutes the keypress, so BOTH outcomes are driven on any
                    // machine: this box accepts every synthesized keypress and could never show the refused
                    // branch through a real SendInput, and a headless runner could never show the accepted
                    // one. The key reader is substituted for the same reason (F114). The two previous
                    // checks here passed on a refusing runner ONLY because of the defect: one asserted the
                    // flag flips after a blink-once whether or not Windows moved the key.

                    // F116: the phase moves only with the key. Both callers used to flip it after a void
                    // Toggle(), so a refused SendInput advanced the belief while the key stayed put.
                    var acceptedProbe = new ScrollLockBlinker();
                    acceptedProbe.KeypressSender = AcceptedKeypress;
                    bool phaseBefore = acceptedProbe.PhaseOn;
                    acceptedProbe.BlinkOnce();
                    probe.Check("WITNESS blink-once flips the phase it records when Windows accepts the keypress",
                        acceptedProbe.PhaseOn != phaseBefore && acceptedProbe.ToggleCount == 1);
                    acceptedProbe.Dispose();

                    var refusedProbe = new ScrollLockBlinker();
                    refusedProbe.KeypressSender = RefusedKeypress;
                    refusedProbe.BlinkOnce();
                    probe.Check("a refused blink-once leaves the phase where it was, because the key did not move",
                        !refusedProbe.PhaseOn && refusedProbe.AttemptCount == 1 &&
                        refusedProbe.ToggleCount == 0 && refusedProbe.LastWin32Error == 5);
                    refusedProbe.Dispose();

                    // The cadence tick is the path that runs every few seconds for a whole session, so it is
                    // stepped directly rather than inferred from BlinkOnce. Started, so an interval is armed
                    // and the assertion on it means something; nothing pumps messages here, so the timer
                    // never fires on its own.
                    var tickProbe = new ScrollLockBlinker();
                    tickProbe.SetRate("Normal");                              // 2500 lit / 7500 dark
                    tickProbe.KeypressSender = RefusedKeypress;
                    // Dark, whatever the machine's key reads: Start() adopts the key since 2026-09-30
                    // (N-blinkingled-01), and this probe's two assertions are written for a cadence that starts
                    // from the dark gap. On a box whose Scroll Lock was lit it adopted lit and both failed.
                    tickProbe.ScrollLockReader = delegate { return false; };
                    tickProbe.Start();
                    tickProbe.Tick();
                    probe.Check("a refused cadence tick leaves the phase where it was and keeps the dark gap armed",
                        !tickProbe.PhaseOn && tickProbe.AttemptCount == 1 && tickProbe.ArmedIntervalMs == 7500);
                    tickProbe.KeypressSender = AcceptedKeypress;
                    tickProbe.Tick();
                    probe.Check("WITNESS an accepted cadence tick moves the phase with the key and arms the lit interval",
                        tickProbe.PhaseOn && tickProbe.ToggleCount == 1 && tickProbe.ArmedIntervalMs == 2500);
                    tickProbe.Dispose();

                    // N-burn-blinkingled-02 (RA-094 b): a manual Scroll Lock press mid-cadence. The key is a
                    // variable the fake press flips and the fake reader reads, so the probe models the physical
                    // key, and the user's press flips the variable out of band. Without the tick's re-sync the
                    // belief flipped from where the ENGINE thought the key was, so belief and key ran inverted for
                    // the rest of the run and Stop() left the LED lit.
                    bool fakeKey = false;
                    var manualProbe = new ScrollLockBlinker();
                    manualProbe.SetRate("Normal");
                    manualProbe.ScrollLockReader = delegate { return fakeKey; };
                    manualProbe.KeypressSender = delegate (out int win32Error) { win32Error = 0; fakeKey = !fakeKey; return true; };
                    manualProbe.Start();                                      // adopts dark, arms the dark gap
                    manualProbe.Tick();                                       // lit
                    bool firstLit = fakeKey && manualProbe.PhaseOn && manualProbe.ArmedIntervalMs == 2500;
                    manualProbe.Tick();                                       // dark again
                    bool thenDark = !fakeKey && !manualProbe.PhaseOn && manualProbe.ArmedIntervalMs == 7500;
                    manualProbe.Tick();                                       // lit again
                    probe.Check("WITNESS an undisturbed cadence alternates lit, dark, lit with the belief and the interval following the key",
                        firstLit && thenDark && fakeKey && manualProbe.PhaseOn && manualProbe.ArmedIntervalMs == 2500);
                    fakeKey = !fakeKey;                                       // the USER presses Scroll Lock: key dark, belief still lit
                    manualProbe.Tick();                                       // re-sync to dark, then toggle: lit
                    probe.Check("a manual press mid-cadence is re-synced on the next tick: the belief and the armed interval follow the key, not the stale belief",
                        fakeKey && manualProbe.PhaseOn && manualProbe.ArmedIntervalMs == 2500);
                    manualProbe.Stop();
                    probe.Check("...and Stop() after a manual press leaves the key dark, as the Readme promises",
                        !fakeKey && !manualProbe.PhaseOn);
                    manualProbe.Dispose();

                    // WITNESS the F116 rule survives the re-sync: a refused toggle moves nothing, so the belief is
                    // the key's state the read found and the interval is that phase's, not a flip.
                    var refusedResyncProbe = new ScrollLockBlinker();
                    refusedResyncProbe.SetRate("Normal");
                    refusedResyncProbe.ScrollLockReader = delegate { return false; };
                    refusedResyncProbe.KeypressSender = RefusedKeypress;
                    refusedResyncProbe.Start();                               // adopts dark
                    refusedResyncProbe.ScrollLockReader = delegate { return true; };   // the user lit it mid-run
                    refusedResyncProbe.Tick();
                    probe.Check("WITNESS a refused tick after a manual press re-syncs the belief to the lit key and arms the lit interval without flipping",
                        refusedResyncProbe.PhaseOn && refusedResyncProbe.ToggleCount == 0 &&
                        refusedResyncProbe.AttemptCount == 1 && refusedResyncProbe.ArmedIntervalMs == 2500);
                    refusedResyncProbe.Dispose();

                    // RA-094 (c): "Blink once now" while the cadence runs re-arms the timer from the phase the
                    // key is in, as a tick and a rate change do. Started dark, so the dark gap is armed; the
                    // manual blink lights the key, and the interval must follow it or the LED stays lit for the
                    // rest of the gap (up to 240 s on Glacial) before the next tick puts it out.
                    var rearmProbe = new ScrollLockBlinker();
                    rearmProbe.SetRate("Normal");
                    rearmProbe.KeypressSender = AcceptedKeypress;
                    rearmProbe.ScrollLockReader = delegate { return false; };
                    rearmProbe.Start();
                    rearmProbe.BlinkOnce();
                    probe.Check("a blink-once during the dark gap lights the key and re-arms the LIT interval, so the light is not left on for the rest of the gap",
                        rearmProbe.PhaseOn && rearmProbe.ToggleCount == 1 && rearmProbe.ArmedIntervalMs == 2500);
                    rearmProbe.BlinkOnce();
                    probe.Check("WITNESS a second blink-once puts it out and re-arms the dark gap",
                        !rearmProbe.PhaseOn && rearmProbe.ArmedIntervalMs == 7500);
                    rearmProbe.KeypressSender = RefusedKeypress;
                    rearmProbe.BlinkOnce();
                    probe.Check("WITNESS a refused blink-once moves neither the phase nor the armed interval",
                        !rearmProbe.PhaseOn && rearmProbe.ArmedIntervalMs == 7500);
                    rearmProbe.Dispose();

                    // F115: Start() reconciles the belief with the key it may have lit instead of zeroing it.
                    // Off, "Blink once now", then enable: the pane's Apply and the tray's speed pick reach
                    // Start() and never Stop(), so this is the ordering the 1.0.5 note claimed fixed and
                    // the previous suite never drove (its stopProbe covered BlinkOnce -> Stop only).
                    var startProbe = new ScrollLockBlinker();
                    startProbe.KeypressSender = AcceptedKeypress;
                    startProbe.ScrollLockReader = delegate { return true; };
                    startProbe.SetRate("Normal");
                    startProbe.BlinkOnce();                                   // feature off: the key is lit and the blinker knows it
                    startProbe.Start();                                       // the user ticks the feature on
                    probe.Check("Start() keeps the belief that it lit the key when the key still reads lit, instead of zeroing it",
                        startProbe.PhaseOn);
                    probe.Check("...and arms the LIT phase's interval, so the cadence continues rather than inverting",
                        startProbe.ArmedIntervalMs == 2500);
                    probe.Check("...without pressing the key again on enable",
                        startProbe.AttemptCount == 1);
                    startProbe.Dispose();

                    var startDarkProbe = new ScrollLockBlinker();
                    startDarkProbe.KeypressSender = AcceptedKeypress;
                    startDarkProbe.ScrollLockReader = delegate { return false; };
                    startDarkProbe.SetRate("Normal");
                    startDarkProbe.BlinkOnce();
                    startDarkProbe.Start();
                    probe.Check("WITNESS Start() drops the belief when the key it lit no longer reads lit, and arms the dark gap",
                        !startDarkProbe.PhaseOn && startDarkProbe.ArmedIntervalMs == 7500);
                    startDarkProbe.Dispose();

                    // N-blinkingled-01: a Scroll Lock the USER lit before enabling is ADOPTED, so the cadence
                    // starts from the lit phase instead of inverted, and Stop() then clears it, which is what
                    // makes the Readme's "stopping always leaves the light off" true for a key the module did
                    // not light. The 1.0.6 first cut asserted the opposite here (variant B); the coordinator
                    // chose variant C on 2026-09-30, recorded under #### fix/blinkingled in the register.
                    var startUserLitProbe = new ScrollLockBlinker();
                    startUserLitProbe.KeypressSender = AcceptedKeypress;
                    startUserLitProbe.ScrollLockReader = delegate { return true; };
                    startUserLitProbe.SetRate("Normal");
                    startUserLitProbe.Start();
                    probe.Check("Start() adopts a lit key it never lit, and arms the LIT interval, so the cadence is never inverted",
                        startUserLitProbe.PhaseOn && startUserLitProbe.ArmedIntervalMs == 2500 && startUserLitProbe.AttemptCount == 0);
                    startUserLitProbe.Stop();
                    probe.Check("...and Stop() then clears it, so stopping always leaves the light off, as the Readme says",
                        !startUserLitProbe.PhaseOn && startUserLitProbe.ToggleCount == 1 && startUserLitProbe.AttemptCount == 1);
                    startUserLitProbe.Dispose();

                    // WITNESS: adoption reads the key rather than assuming it lit. A dark key it never lit
                    // adopts dark and arms the dark gap, and Stop() has nothing to clear.
                    var startUserDarkProbe = new ScrollLockBlinker();
                    startUserDarkProbe.KeypressSender = AcceptedKeypress;
                    startUserDarkProbe.ScrollLockReader = delegate { return false; };
                    startUserDarkProbe.SetRate("Normal");
                    startUserDarkProbe.Start();
                    probe.Check("WITNESS Start() over a dark key it never lit adopts dark and arms the dark gap",
                        !startUserDarkProbe.PhaseOn && startUserDarkProbe.ArmedIntervalMs == 7500);
                    startUserDarkProbe.Stop();
                    probe.Check("WITNESS ...and Stop() then presses nothing", startUserDarkProbe.AttemptCount == 0);
                    startUserDarkProbe.Dispose();

                    // THE CORRECTIVE TOGGLE ITSELF (F114), and what Stop() believes afterwards (F116). The
                    // reader is substituted so both branches of the gate run whatever the machine's LED is
                    // doing, and the keypress is substituted so the belief is set on every machine.
                    var litProbe = new ScrollLockBlinker();
                    litProbe.KeypressSender = AcceptedKeypress;
                    litProbe.ScrollLockReader = delegate { return true; };
                    litProbe.BlinkOnce();                                     // the blinker now believes it lit the key
                    long attemptsBeforeStop = litProbe.AttemptCount;
                    litProbe.Stop();                                          // key reads lit: the corrective toggle clears it
                    probe.Check("Stop() attempts the corrective toggle when it lit the key and the key reads lit",
                        litProbe.AttemptCount == attemptsBeforeStop + 1);
                    probe.Check("Stop() clears a blink-once made while the feature was switched off",
                        !litProbe.PhaseOn);
                    probe.Check("WITNESS a key the module lit is still cleared by Stop() now that Start() adopts the key too",
                        litProbe.ToggleCount == 2);
                    litProbe.Dispose();

                    var userLitProbe = new ScrollLockBlinker();
                    userLitProbe.KeypressSender = AcceptedKeypress;
                    userLitProbe.ScrollLockReader = delegate { return false; };
                    userLitProbe.BlinkOnce();
                    attemptsBeforeStop = userLitProbe.AttemptCount;
                    userLitProbe.Stop();                                      // key does not read lit: nothing to correct
                    probe.Check("WITNESS Stop() leaves a key that does not read lit alone, even when it believes it lit it",
                        userLitProbe.AttemptCount == attemptsBeforeStop && !userLitProbe.PhaseOn);
                    userLitProbe.Dispose();

                    var neverLitProbe = new ScrollLockBlinker();
                    neverLitProbe.ScrollLockReader = delegate { return true; };
                    neverLitProbe.Stop();                                     // never lit anything: the startup ApplyState(false) shape
                    probe.Check("WITNESS Stop() on a blinker that never lit the key attempts nothing, however the key reads",
                        neverLitProbe.AttemptCount == 0);
                    neverLitProbe.Dispose();

                    // A REFUSED corrective toggle: the LED is still lit and still ours, so the belief must
                    // survive for the next Stop() or Start() to retry. Zeroing it regardless was the third
                    // entry into the same drift, and it re-armed the F115 inversion through Start().
                    var refusedStopProbe = new ScrollLockBlinker();
                    refusedStopProbe.KeypressSender = AcceptedKeypress;
                    refusedStopProbe.ScrollLockReader = delegate { return true; };
                    refusedStopProbe.BlinkOnce();
                    refusedStopProbe.KeypressSender = RefusedKeypress;
                    refusedStopProbe.Stop();
                    probe.Check("a refused corrective toggle keeps the belief that the key is ours to clear, so a later Stop() or Start() can retry",
                        refusedStopProbe.PhaseOn && refusedStopProbe.AttemptCount == 2);
                    refusedStopProbe.KeypressSender = AcceptedKeypress;
                    refusedStopProbe.Stop();
                    probe.Check("WITNESS ...and the retry clears it",
                        !refusedStopProbe.PhaseOn && refusedStopProbe.ToggleCount == 2);
                    refusedStopProbe.Dispose();

                    // R-024: a corrective toggle that THROWS is recorded as -1, the way BlinkOnce and Tick
                    // record it, so the delivery log says why the LED is stuck on the path that runs at Off, at
                    // a Caps Lock stop and at Shutdown; the one-expression `reader() && !Toggle()` under one
                    // catch swallowed it. Lit through the accepted seam first, so the belief is true and the key
                    // reads lit; the sender then throws. The delivery line is a transition (accepted -> refused).
                    var throwingStopProbe = new ScrollLockBlinker();
                    throwingStopProbe.KeypressSender = AcceptedKeypress;
                    throwingStopProbe.ScrollLockReader = delegate { return true; };
                    throwingStopProbe.BlinkOnce();
                    throwingStopProbe.KeypressSender = ThrowingKeypress;
                    int deliveryBefore = CountLogged(host.LoggedLines, "blink delivery");
                    throwingStopProbe.Stop();
                    string thrownLine = LastLoggedMatching(host.LoggedLines, "blink delivery refused");
                    probe.Check("a throwing corrective toggle in Stop() is recorded as -1, with a delivery line, and keeps the belief",
                        throwingStopProbe.LastWin32Error == -1 && throwingStopProbe.PhaseOn &&
                        throwingStopProbe.AttemptCount == 2 &&
                        CountLogged(host.LoggedLines, "blink delivery") - deliveryBefore == 1 &&
                        thrownLine != null && thrownLine.Contains("win32=-1"));
                    throwingStopProbe.Dispose();

                    // WITNESS the convention it now follows: a blink-once whose press throws records the same -1.
                    var throwingBlinkProbe = new ScrollLockBlinker();
                    throwingBlinkProbe.KeypressSender = ThrowingKeypress;
                    throwingBlinkProbe.BlinkOnce();
                    probe.Check("WITNESS a throwing blink-once records -1 and leaves the phase where it was",
                        throwingBlinkProbe.LastWin32Error == -1 && !throwingBlinkProbe.PhaseOn &&
                        throwingBlinkProbe.AttemptCount == 1);
                    throwingBlinkProbe.Dispose();

                    // WITNESS a key READ that throws delivered nothing, so nothing is pressed and nothing is
                    // recorded, and the belief stands for the next Stop() or Start(), as before.
                    var throwingReaderProbe = new ScrollLockBlinker();
                    throwingReaderProbe.KeypressSender = AcceptedKeypress;
                    throwingReaderProbe.ScrollLockReader = delegate { return true; };
                    throwingReaderProbe.BlinkOnce();
                    throwingReaderProbe.ScrollLockReader = delegate { throw new InvalidOperationException("no key state"); };
                    deliveryBefore = CountLogged(host.LoggedLines, "blink delivery");
                    throwingReaderProbe.Stop();
                    probe.Check("WITNESS a throwing key read in Stop() presses nothing, records nothing and keeps the belief",
                        throwingReaderProbe.PhaseOn && throwingReaderProbe.AttemptCount == 1 &&
                        throwingReaderProbe.LastWin32Error == 0 &&
                        CountLogged(host.LoggedLines, "blink delivery") - deliveryBefore == 0);
                    throwingReaderProbe.Dispose();

                    // The pair of the one real keypress above. On a machine that accepted both, the key is
                    // back where it was and the module's blinker believes it holds nothing; on one that
                    // refused both, nothing moved and it never believed otherwise. The parity is ASSERTED
                    // rather than trusted, because "every toggle is paired" was written above the previous
                    // suite and was wrong (F113). Same outcome as the first attempt, so no new log line.
                    //
                    // Two counts, not one (RA-090): RealKeypressCount advances before SendInput answers, so it
                    // counts ATTEMPTS, and a first press accepted with its pair refused (a UAC prompt between
                    // them) would move the key while the attempts still paired; this instance's ToggleCount is
                    // the accepted presses, and its Stop() calls never toggle because its reader is pinned dark,
                    // so an even count here means the key really is back. The claim is about THIS instance:
                    // the one the convention runner Init'd through the real loader adopts through the real
                    // reader and is outside this window; since Shutdown() clears only a light the session
                    // pressed (RA-090), that instance presses nothing on the way out.
                    module._blinker.BlinkOnce();
                    long realMade = ScrollLockBlinker.RealKeypressCount - realBefore;
                    probe.Check("the suite's real keypresses are paired (" + realMade + " made, " + module._blinker.ToggleCount
                                + " accepted), so this instance leaves Scroll Lock where the machine had it",
                        realMade > 0 && realMade % 2 == 0 && module._blinker.ToggleCount % 2 == 0);
                    // By construction, not by luck, the key ends where the machine had it: an odd number of
                    // ACCEPTED presses on this instance is followed by one more. Count-based, with no key read
                    // (the read-and-restore the F113 register entry rejected depended on a read that can be
                    // stale and would then ADD an unpaired press). It fires only when the pairing above is
                    // already broken, which is what the mutation case "the self-test's pairing keypress is
                    // deleted" does on purpose; until 2026-09-30 that mutated run left the developer's Scroll
                    // Lock flipped for the next headless Init of this module to clear through adoption, and
                    // since Shutdown() no longer clears a light this session never drove (RA-090), nothing else
                    // would put it back (N-burn-blinkingled-01). The FAIL above is already recorded, so this
                    // hides nothing.
                    if (module._blinker.ToggleCount % 2 == 1) module._blinker.BlinkOnce();

                    // Transition-only, driven through the engine's own notifier so both outcomes are
                    // exercised on any machine. Three calls, one repeat: two lines, not three.
                    var deliveryProbe = new ScrollLockBlinker();
                    int loggedBefore = host.LoggedLines.Count;
                    deliveryProbe.NoteDelivery(false, 5);
                    deliveryProbe.NoteDelivery(false, 5);
                    deliveryProbe.NoteDelivery(true, 0);
                    deliveryProbe.Dispose();
                    probe.Check("delivery is logged on the transition, not once per blink",
                        host.LoggedLines.Count - loggedBefore == 2);
                    probe.Check("a refusal is reported as one, with the Win32 error",
                        LastLoggedMatching(host.LoggedLines, "blink delivery refused") != null &&
                        LastLoggedMatching(host.LoggedLines, "blink delivery refused").Contains("win32=5"));
                    probe.Check("and so is the recovery",
                        LastLoggedMatching(host.LoggedLines, "blink delivery accepted") != null);

                    // Caps Lock stopping the blinker is the one state change the user is never told about:
                    // the remark is suppressed on purpose and the OFF is persisted, so a week later the
                    // feature is off, the tray agrees, and nothing says why.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Slow" },
                        { "capsStops", "true" }, { "announce", "false" },
                    });
                    loggedBefore = host.LoggedLines.Count;
                    module.OnCapsLockStop();
                    probe.Check("a Caps Lock stop is recorded, since nothing else reports it",
                        CountLogged(host.LoggedLines, "caps lock is on") == 1 &&
                        host.LoggedLines.Count - loggedBefore == 1);
                    probe.Check("...and it really did switch off", pane.Load()["enabled"] == "false");

                    // F110: the line tells the truth about the write. Every IModuleSettings in the repo
                    // reports a failed Save() by returning false (none throws), which FailSaves reproduces.
                    // The previous shape logged "saved it as off" before saving and caught an exception
                    // nothing raised, so a failed write produced a false "saved" line and nothing else, and
                    // the two tray handlers dropped the bool outright, so a click whose write failed did
                    // nothing and said nothing. Since 2026-09-30 the fake puts a failed Save()'s values back
                    // the way the host's fresh-from-disk instance shows them (N-blinkingled-02), so every
                    // failing click asserts BOTH halves: its one line, and that what the module reads back
                    // afterwards is what was there before the click. Switched back on first, with a write
                    // that lands, so each failed off or pick below has a real change to fail to make.
                    DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings failing = host.SettingsFor("blinkingled");
                    module.SetEnabledFromTray(true);
                    probe.Check("WITNESS the pick that switches it back on persists (on, rate Slow)",
                        failing.Get("enabled", null) == "true" && failing.Get("rate", null) == "Slow");
                    failing.FailSaves = true;
                    loggedBefore = host.LoggedLines.Count;
                    module.OnCapsLockStop();
                    string capsLine = LastLoggedMatching(host.LoggedLines, "caps lock is on");
                    probe.Check("a Caps Lock stop whose write fails says the off was NOT persisted, in its one line",
                        host.LoggedLines.Count - loggedBefore == 1 &&
                        capsLine != null && capsLine.Contains("NOT persisted") && !capsLine.Contains("saved it as off"));
                    probe.Check("...and the settings still read enabled, which is what that line warns the next read will act on",
                        failing.GetBool("enabled", false));
                    loggedBefore = host.LoggedLines.Count;
                    module.SetRateFromTray("Hyper");                          // a change, so the handler reaches Save()
                    probe.Check("a tray speed pick whose write fails is logged rather than silently doing nothing",
                        CountLogged(host.LoggedLines, "tray pick not persisted (rate Hyper, on)") == 1 &&
                        host.LoggedLines.Count - loggedBefore == 1);
                    probe.Check("a tray speed pick whose write fails leaves the saved values as they were, so the click did nothing",
                        failing.Get("rate", null) == "Slow" && failing.Get("enabled", null) == "true");
                    loggedBefore = host.LoggedLines.Count;
                    module.SetEnabledFromTray(false);
                    probe.Check("WITNESS a tray Off whose write fails is logged too",
                        CountLogged(host.LoggedLines, "tray pick not persisted (off)") == 1 &&
                        host.LoggedLines.Count - loggedBefore == 1);
                    probe.Check("WITNESS ...and it still reads enabled afterwards",
                        failing.GetBool("enabled", false));
                    failing.FailSaves = false;
                    loggedBefore = host.LoggedLines.Count;
                    module.SetEnabledFromTray(false);
                    probe.Check("WITNESS a tray pick whose write succeeds logs nothing",
                        host.LoggedLines.Count == loggedBefore);
                    probe.Check("WITNESS ...and its value is what the module reads back",
                        failing.Get("enabled", null) == "false");

                    // ---- enabling while Caps Lock is already on (RA-088) -------------------------------
                    // Through the engine's CapsLockReader seam, so both answers are driven whatever the
                    // developer's own Caps Lock is doing. Switching it on used to speak the ON line and then, one
                    // dark gap later, the first tick found Caps Lock, stopped, persisted off and said nothing; a
                    // repeat pick replayed the cycle. State here: off, rate Slow, capsStops on, announce off.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "true" }, { "announce", "true" },
                    });                                                       // announce on; no transition, so quiet
                    module._blinker.CapsLockReader = delegate { return true; };
                    int saidBeforeCaps = host.SaidLines.Count;
                    loggedBefore = host.LoggedLines.Count;
                    module.SetRateFromTray("Fast");                           // the user switches it on under Caps Lock
                    probe.Check("enabling under Caps Lock does not start the blinker, persists the off and keeps the chosen speed",
                        !module._blinker.IsRunning && pane.Load()["enabled"] == "false" && pane.Load()["rate"] == "Fast");
                    probe.Check("enabling under Caps Lock speaks one Caps-Lock-specific line in place of the ON line",
                        host.SaidLines.Count - saidBeforeCaps == 1 &&
                        host.SaidLines[host.SaidLines.Count - 1] != "Keeping the lights on for you." &&
                        host.SaidLines[host.SaidLines.Count - 1].StartsWith("Caps Lock is on", StringComparison.Ordinal));
                    string capsRefusal = LastLoggedMatching(host.LoggedLines, "caps lock is on: did not start");
                    probe.Check("enabling under Caps Lock logs exactly one line, and it says the off was saved",
                        host.LoggedLines.Count - loggedBefore == 1 &&
                        capsRefusal != null && capsRefusal.Contains("saved it as off"));
                    saidBeforeCaps = host.SaidLines.Count;
                    module.SetRateFromTray("Fast");                           // the repeat pick the finding describes
                    probe.Check("a repeat pick under Caps Lock says so again rather than replaying the ON cycle",
                        !module._blinker.IsRunning && host.SaidLines.Count - saidBeforeCaps == 1 &&
                        host.SaidLines[host.SaidLines.Count - 1].StartsWith("Caps Lock is on", StringComparison.Ordinal));

                    // WITNESS: with "Stop when Caps Lock is on" off, the same pick under Caps Lock starts and
                    // speaks the ON line; the refusal follows the setting the tick follows.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Fast" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    saidBeforeCaps = host.SaidLines.Count;
                    module.SetRateFromTray("Hyper");
                    probe.Check("WITNESS with 'Stop when Caps Lock is on' off, the same pick under Caps Lock starts and speaks the ON line",
                        module._blinker.IsRunning && host.SaidLines.Count - saidBeforeCaps == 1 &&
                        host.SaidLines[host.SaidLines.Count - 1] == "Keeping the lights on for you.");
                    module.SetEnabledFromTray(false);

                    // WITNESS: Caps Lock dark with the setting on is the ordinary enable.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Hyper" },
                        { "capsStops", "true" }, { "announce", "true" },
                    });
                    module._blinker.CapsLockReader = delegate { return false; };
                    saidBeforeCaps = host.SaidLines.Count;
                    module.SetRateFromTray("Fast");
                    probe.Check("WITNESS with Caps Lock dark the pick starts and speaks the ON line",
                        module._blinker.IsRunning && host.SaidLines.Count - saidBeforeCaps == 1 &&
                        host.SaidLines[host.SaidLines.Count - 1] == "Keeping the lights on for you.");
                    module.SetEnabledFromTray(false);

                    // WITNESS: startup is unchanged. ApplyState(false) is what Init calls; nothing was said, so
                    // there is nothing to correct, and the first tick stops it as before.
                    failing.Set("enabled", "true");
                    probe.Check("WITNESS the startup seed persists", failing.Save());
                    module._blinker.CapsLockReader = delegate { return true; };
                    saidBeforeCaps = host.SaidLines.Count;
                    loggedBefore = host.LoggedLines.Count;
                    module.ApplyState(false);
                    probe.Check("WITNESS at startup (announce false) Caps Lock is left to the first tick: the blinker starts, nothing is said, nothing is logged",
                        module._blinker.IsRunning && host.SaidLines.Count == saidBeforeCaps &&
                        host.LoggedLines.Count == loggedBefore);
                    module.SetEnabledFromTray(false);

                    // A refusal whose off cannot be persisted says so, in the same two-way shape as the Caps
                    // Lock stop's line. Seeded without a Save(), so the failing Save() puts the disk's off back.
                    failing.Set("enabled", "true");
                    failing.FailSaves = true;
                    loggedBefore = host.LoggedLines.Count;
                    module.ApplyState(true);
                    capsRefusal = LastLoggedMatching(host.LoggedLines, "caps lock is on: did not start");
                    probe.Check("a refused enable whose off cannot be persisted says so in its one line",
                        !module._blinker.IsRunning && host.LoggedLines.Count - loggedBefore == 1 &&
                        capsRefusal != null && capsRefusal.Contains("NOT persisted") && !capsRefusal.Contains("saved it as off"));
                    failing.FailSaves = false;
                    module._blinker.CapsLockReader = delegate { return false; };

                    // Every line carries this module's id, which is what the per-module log mute keys on.
                    bool allTagged = true;
                    foreach (string line in host.LoggedLines)
                        if (line == null || !line.StartsWith("blinkingled: ", StringComparison.Ordinal))
                            allTagged = false;
                    probe.Check("every logged line is tagged with this module's id",
                        host.LoggedLines.Count > 0 && allTagged);

                    // ---- Shutdown over a light this session never drove (RA-090) ----------------------
                    // What every headless host does to this module: Init (Start() adopts the real key), no
                    // message loop, ShutdownAll. Modelled on a second instance against its own host, so the
                    // counts above are untouched. Init reads the developer's real key; the instance is then
                    // re-adopted through the pinned-lit reader (Dispose, then Start() on a stopped blinker,
                    // presses nothing), so the shape "adopted lit, never pressed" is driven on any machine and
                    // the keypress seam counts what Shutdown does instead of moving anything.
                    var headlessHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    var headless = new BlinkingLedModule();
                    headless.Init(headlessHost);
                    ScrollLockBlinker adopted = headless._blinker;
                    adopted.Dispose();
                    adopted.ScrollLockReader = delegate { return true; };
                    adopted.KeypressSender = AcceptedKeypress;
                    adopted.Start();
                    probe.Check("WITNESS the headless shape is driven: adopted lit, running, nothing pressed",
                        adopted.PhaseOn && adopted.IsRunning && adopted.AttemptCount == 0);
                    headless.Shutdown();
                    probe.Check("Shutdown leaves a lit key it adopted but never drove where the user had it (no press, RA-090)",
                        adopted.AttemptCount == 0 && !adopted.IsRunning);

                    // WITNESS: a light this session drove is still cleared on the way out, variant C's promise,
                    // through Stop()'s corrective toggle: the blink-once made it ours, Shutdown presses once more.
                    var drivenHost = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
                    var driven = new BlinkingLedModule();
                    driven.Init(drivenHost);
                    ScrollLockBlinker drove = driven._blinker;
                    drove.Dispose();
                    drove.ScrollLockReader = delegate { return false; };
                    drove.KeypressSender = AcceptedKeypress;
                    drove.Start();                                            // adopted dark
                    drove.BlinkOnce();                                        // lit by this session: ours to clear
                    drove.ScrollLockReader = delegate { return true; };
                    driven.Shutdown();
                    probe.Check("WITNESS Shutdown clears a light this session drove (one press to make it, one to clear it)",
                        drove.AttemptCount == 2 && drove.ToggleCount == 2 && !drove.PhaseOn);

                    module.Shutdown();
                }
            }
            catch (Exception ex) { probe.Exception(ex); }
            return probe.Finish(out detail);
        }

        /// <summary>How many recorded lines mention <paramref name="needle"/>. RecordingHost stores each as
        /// "&lt;moduleId&gt;: &lt;message&gt;", so the module id is asserted separately.</summary>
        private static int CountLogged(List<string> lines, string needle)
        {
            int n = 0;
            if (lines == null) return 0;
            foreach (string line in lines)
                if (line != null && line.IndexOf(needle, StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        /// <summary>The most recent recorded line mentioning <paramref name="needle"/>, or null.</summary>
        private static string LastLoggedMatching(List<string> lines, string needle)
        {
            if (lines == null) return null;
            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i] != null && lines[i].IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return lines[i];
            return null;
        }

        /// <summary>A keypress Windows accepted, for the engine's KeypressSender seam: no SendInput, no LED.
        /// <c>out int</c> because that is <see cref="ScrollLockBlinker.KeypressDelivery"/>'s shape; the host's
        /// finder never sees these helpers, which are private and not named SelfTest (RA-092).</summary>
        private static bool AcceptedKeypress(out int win32Error) { win32Error = 0; return true; }

        /// <summary>A keypress Windows refused with ERROR_ACCESS_DENIED (5), the UIPI answer, for the same
        /// seam.</summary>
        private static bool RefusedKeypress(out int win32Error) { win32Error = 5; return false; }

        /// <summary>A keypress whose delivery THROWS (the P/Invoke-resolution class of failure 1.0.3 shipped
        /// with), for the -1 convention: every writer records it as win32=-1 rather than swallowing it (R-024).</summary>
        private static bool ThrowingKeypress(out int win32Error) { throw new InvalidOperationException("SendInput threw"); }
    }
}
