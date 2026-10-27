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
    /// process. Caps Lock ON used to QUIT; here it stops the blinking, since a module cannot quit the pet, and
    /// by default only until the companion next starts, which is what the quit amounted to ("Resume on
    /// application restart", 1.0.7). And it never speaks at startup: the module auto-starts, and the pet has
    /// its own opening line that a second bubble would talk over. It speaks only when the user turns it off or
    /// back on.
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
            Version = "1.0.8",   // 1.0.8: an Apply from the options pane writes only the boxes you changed there, so a
                                 //        tray choice made while the window is open is no longer put back by it
                                 //        (N-blinkingled-resume-03).
                                 // 1.0.7: a Caps Lock stop no longer switches the blinker off for good. With
                                 //        the new "Resume on application restart" box ticked (the default),
                                 //        Caps Lock still stops the blinking, and a switch-on made while Caps
                                 //        Lock is on is still refused, but only until the companion next
                                 //        starts, or until the user picks a speed or Off in the tray or applies
                                 //        a changed on/off or speed, or an unticked "Stop when Caps Lock is
                                 //        on", in the pane: no off is saved, so the next start blinks again, as
                                 //        the standalone app did after its Caps Lock quit. An Apply that
                                 //        changes only the other boxes leaves such a stop alone. Unticked, the
                                 //        off is saved as in 1.0.6, now with its reason, so after a restart the
                                 //        tray row and the startup line still say Caps Lock stopped it; tick the
                                 //        box over such an off and the next start resumes it, and before that a
                                 //        tray speed pick switches it back on; a pane Apply leaves it off unless
                                 //        it ticks "Blink the Scroll Lock light" (the pane is not re-read after an
                                 //        Apply). A deliberate Off from the tray or the pane is
                                 //        saved either way, and stays off until the user switches it on. 1.0.6
                                 //        saved every Caps Lock stop, so one tick that found Caps Lock on left
                                 //        the feature off through every later start with nothing saying why
                                 //        (the owner's report, 2026-10-14: "I had hit capslock and it was not
                                 //        running"). Every start now logs one line with the state, in one of
                                 //        five forms ("starting: on, ...", "starting: on (resumed from an off a
                                 //        Caps Lock stop saved), ...", "starting: off (saved by a Caps Lock
                                 //        stop), ...", "starting: off (saved by a Caps Lock stop; the resume was
                                 //        NOT persisted), ..." or "starting: off (saved), ...", then the rate
                                 //        and both Caps Lock settings), and while Caps Lock has it stopped the
                                 //        tray row says so and whether the next start brings it back. Such a
                                 //        stop leaves the speed ticked, so picking that speed starts it again
                                 //        rather than being skipped as a pick of what is already live, and a
                                 //        tray pick or a pane Apply whose write fails during the stop leaves it
                                 //        stopped. A settings file 1.0.6 left at off stays off: it cannot tell a
                                 //        Caps Lock stop from a user's Off, so switch it on once. With
                                 //        DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1 the self-test makes no
                                 //        real keypress and says so on a SKIPPED line, and in a self-test
                                 //        process the engine's one SendInput call then sends nothing, for every
                                 //        instance; unset, it still makes its one pair of real Scroll Lock
                                 //        presses.
                                 // 1.0.6: the blinker's belief about the LED moves only with the LED. A
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
                    // The owner's words for the toggle (2026-10-14). Ticked, a Caps Lock stop lasts until the
                    // companion next starts and saves nothing; unticked, the off is saved, as in 1.0.6. It has no
                    // effect while "Stop when Caps Lock is on" is unticked, since nothing then stops it. NOT greyed
                    // in that state: EnabledWhen arrived in host 1.1.6, and setting it would raise MinHostVersion
                    // from 1.0.0. Nobody has read which host the owner's other workstation runs, and that is the
                    // machine this fix is for (`#### feature/blinkingled-resume` in docs/DESIGN-REGISTER.md).
                    new SettingField
                    {
                        Id = "resumeOnRestart",
                        Label = "Resume on application restart",
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

            // "Resume on application restart" ticked over an off a Caps Lock stop SAVED, which it did while the box was
            // unticked (the box ticked afterwards): the box says a Caps Lock stop lasts until the companion next starts,
            // and this is that start, so the off and its reason go before the state is applied. A plain saved off, a
            // user's Off or one 1.0.6 saved with no reason, is never touched.
            _resumeAtStart = ResumeSavedCapsLockOff();
            ApplyState(false);   // false: starting up, so stay quiet whatever the state turns out to be

            // ONE line with what this start did, whichever way it went. Until 1.0.7 a saved off wrote nothing
            // after the host's "module loaded" line, so a feature Caps Lock had switched off for good looked, at
            // start, like one that was running; the only difference was a missing first "blink delivery" line one
            // interval later (the owner's report, 2026-10-14). The log, not the pet: startup stays silent for the
            // reason ApplyState(false) gives.
            Log(StartupLine());
            _instancesStarted++;   // the process record's count (RecordRealInputAfterShutdown); last, so a failed Init is not one
        }

        /// <summary>The startup state, in the module's own vocabulary: on, "on (resumed from an off a Caps Lock stop
        /// saved)", "off (saved by a Caps Lock stop)" or "off (saved)", the rate, and the two Caps Lock settings, so a
        /// reader can tell a running blinker from a saved off and see whether Caps Lock caused it. "saved" because at a
        /// start the saved choice is the only way to be off: a Caps Lock stop with "Resume on application restart"
        /// ticked lives on the previous instance, and one with it unticked saved its reason beside the off
        /// (StoppedByKey). A saved off with no reason is a user's Off, or one 1.0.6 saved, which recorded none. When the
        /// box was ticked over such a saved off and the write that resumes it failed, the line says so.</summary>
        private string StartupLine()
        {
            IModuleSettings s = Settings();
            bool running = _blinker != null && _blinker.IsRunning;
            string state = running
                ? (_resumeAtStart == ResumedAtStart ? "on (resumed from an off a Caps Lock stop saved)" : "on")
                : SavedOffIsCapsLocks(s)
                    ? (_resumeAtStart == ResumeNotPersisted ? "off (saved by a Caps Lock stop; the resume was NOT persisted)"
                                                             : "off (saved by a Caps Lock stop)")
                    : "off (saved)";
            return "starting: " + state +
                   ", rate " + (_appliedRate ?? ScrollLockBlinker.DefaultRate) +
                   ", Caps Lock stops it: " + (s.GetBool("capsStops", true) ? "yes" : "no") +
                   ", resume on application restart: " + (ResumesOnRestart(s) ? "yes" : "no");
        }

        // What Init did about a saved Caps Lock off: nothing to do, resumed it with a write that landed, or tried and the
        // write failed (the off and its reason are still saved, so the next start tries again).
        private const int NothingToResume = 0, ResumedAtStart = 1, ResumeNotPersisted = 2;
        private int _resumeAtStart;

        /// <summary>At a start with "Resume on application restart" ticked, an off a Caps Lock stop saved (with the box
        /// unticked at the time) is switched back on and its reason cleared, in one Save(), before ApplyState(false) acts
        /// on the settings. A plain saved off stays off. The disk still wins: a failed write leaves the saved off, which
        /// ApplyState then reads, and the startup line and the tray row say so.</summary>
        private int ResumeSavedCapsLockOff()
        {
            try
            {
                IModuleSettings s = Settings();
                if (!ResumesOnRestart(s) || !SavedOffIsCapsLocks(s)) return NothingToResume;
                s.Set("enabled", "true");
                s.Set(StoppedByKey, "");   // resumed: the off is gone, and its reason with it
                return s.Save() ? ResumedAtStart : ResumeNotPersisted;
            }
            catch { return ResumeNotPersisted; }   // a third-party store that throws; every shipped store returns false
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
            _instancesShutDown++;
            RecordRealInputAfterShutdown();
        }

        // Instances of this module Init'd and shut down in this process (the module's assembly, in its load context), for
        // the process record below. Static because the record is about the process, not one instance.
        private static int _instancesStarted;
        private static int _instancesShutDown;

        /// <summary>The name of the record <see cref="RecordRealInputAfterShutdown"/> leaves in TEMP, beside the convention
        /// runner's marker (dp-module-blinkingled-selftest.txt), for tests\Test-ModuleSelfTests.ps1 to read.</summary>
        internal const string RealInputRecordFile = "dp-module-blinkingled-realinput.txt";

        /// <summary>
        /// In this module's own --module-selftest process, and only there, every Shutdown rewrites one line in TEMP with the
        /// engine's process-wide counts: "realKeypresses=N withheld=N started=N shutDown=N". The last Shutdown in that
        /// process is the convention runner's own instance, which the loader shuts down after the self-test has returned,
        /// so the record it leaves is the only reading taken after every instance has shut down; "started" equal to
        /// "shutDown" is how a reader knows the record is that one. The self-test's own counter check cannot be: it runs
        /// before the loader's ShutdownAll. With DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1, tests\Test-ModuleSelfTests.ps1
        /// deletes the record before the run, reads it after the process has exited, and requires realKeypresses=0. Written
        /// whatever the engine decided about withholding, so a run whose decision is broken still leaves the reading that
        /// shows whether anything was pressed (gating it on that decision lost the record in exactly those runs). Never in a
        /// normal start, which has no self-test flag, and never under another self-test flag, whose runner would not read
        /// it. Never throws.
        /// </summary>
        private static void RecordRealInputAfterShutdown()
        {
            try
            {
                if (!RunsThisModulesSelfTest(Environment.GetCommandLineArgs())) return;
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), RealInputRecordFile),
                    "realKeypresses=" + ScrollLockBlinker.RealKeypressCount.ToString(CultureInfo.InvariantCulture) +
                    " withheld=" + ScrollLockBlinker.WithheldKeypressCount.ToString(CultureInfo.InvariantCulture) +
                    " started=" + _instancesStarted.ToString(CultureInfo.InvariantCulture) +
                    " shutDown=" + _instancesShutDown.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
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
            // The user's own choice (a tray pick, a pane Apply that changes on/off or the speed or unticks "Stop when Caps
            // Lock is on": the only callers that announce while a stop is in force) ends a Caps Lock stop made earlier in
            // this session and is acted on
            // like any other; if Caps Lock is still on, the refusal below stops it again and says so. Startup has none
            // to end: the flag lives on this instance, and a start is a new one.
            if (announce) _stoppedByCaps = false;
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
            // "Stop when Caps Lock is on" is set: do not start, say why, and persist the off only when "Resume on
            // application restart" is unticked (RA-088; the resume rule is 1.0.7's, see the refusal). Starting
            // would speak the ON line and then, one dark gap later (2 s on Fast, 240 s on Glacial), the first
            // tick would find Caps Lock, stop and say nothing, since the tick's silence is written
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

        /// <summary>Caps Lock stopped the blinker in THIS session: a tick found it on, or a switch-on was refused
        /// under it. Set by OnCapsLockStop and RefuseEnableUnderCapsLock whichever way their write went, read by the
        /// tray row, the two tray handlers and the pane's Save, cleared by the user's next choice (ApplyState with
        /// announce). Never saved and never static, on purpose: a restart is a new instance, and with "Resume on
        /// application restart" ticked nothing about the stop is written, so the next start reads the saved choice and
        /// blinks again. (With the box unticked the off is saved with its reason, StoppedByKey, which is what names a
        /// Caps Lock off after a restart.) Nothing in a session restarts a stopped blinker but a user gesture (no timer
        /// runs while it is stopped), so ApplyState does not need to consult this to keep the stop in force; the tray
        /// handlers and the pane's Save consult it to decide whether a click reaches ApplyState at all.</summary>
        private bool _stoppedByCaps;

        /// <summary>The "Resume on application restart" box, ticked unless the user unticked it. ONE reader, so the
        /// pane, the startup line and the two Caps Lock paths cannot disagree about the default.</summary>
        private static bool ResumesOnRestart(IModuleSettings s)
        {
            return s.GetBool("resumeOnRestart", true);
        }

        /// <summary>The settings key that says why a saved off was saved, and its one value. Written WITH the off, in the
        /// same Save(), by the two Caps Lock paths when "Resume on application restart" is unticked, so a restart still
        /// knows the off was Caps Lock's: the tray row and the startup line name it. Cleared by the user's own on/off: a
        /// tray speed pick, a tray Off, a pane Apply that changes "Blink the Scroll Lock light"; and, once "Resume on
        /// application restart" is ticked over such an off, with the off itself, by the next start (ResumeSavedCapsLockOff);
        /// no other pane Apply touches it (SavePaneValues says why). A file 1.0.6
        /// wrote has no such key, so its off reads as a plain saved off, which is all 1.0.6 recorded.</summary>
        private const string StoppedByKey = "stoppedBy";
        private const string StoppedByCapsLock = "capsLock";

        /// <summary>The saved off is one a Caps Lock stop saved: off, with the reason beside it. The reason means nothing
        /// beside a saved on, which is why it is read with the off and never alone.</summary>
        private static bool SavedOffIsCapsLocks(IModuleSettings s)
        {
            return !s.GetBool("enabled", true) &&
                   string.Equals(s.Get(StoppedByKey, ""), StoppedByCapsLock, StringComparison.Ordinal);
        }

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
            try
            {
                IModuleSettings s = Settings();
                bool saved = s.GetBool("enabled", true);
                // A Caps Lock stop says so, and says what the next start will do, read from what is SAVED rather
                // than from the box: a ticked box saves nothing, so the saved on is what the next start acts on, and
                // an off that failed to save is reported the same way, because the next start will read the on.
                // Until 1.0.7 this row read "Blinking LED: off" for a Caps Lock stop and a user's Off alike. The
                // session flag says it for this instance; the saved reason says it after a restart, for the off an
                // unticked box saved, so the row keeps naming Caps Lock until the user picks a speed or Off. Such a
                // saved off resumes at the next start too once the box is ticked over it (ResumeSavedCapsLockOff), so the
                // row says so then; a tray speed pick ends it sooner, and a pane Apply does not unless it switches it on
                // (SavePaneValues).
                if (_stoppedByCaps || SavedOffIsCapsLocks(s))
                {
                    bool resumesNext = saved || (ResumesOnRestart(s) && SavedOffIsCapsLocks(s));
                    return resumesNext ? "Blinking LED: stopped by Caps Lock (resumes at next start)"
                                       : "Blinking LED: stopped by Caps Lock (off until you pick a speed)";
                }
                return saved ? "Blinking LED: on" : "Blinking LED: off";
            }
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
                if (sameRate && alreadyOn)
                {
                    // Picking what is already saved needs no write and is not worth a remark. Unless Caps Lock
                    // stopped it this session with "Resume on application restart" ticked: that stop leaves the
                    // saved on and the speed ticked, so this pick is the user's "start again", and it reaches
                    // ApplyState like any other choice (which refuses again if Caps Lock is still on).
                    if (_stoppedByCaps) ApplyState(true);
                    return;
                }
                s.Set("rate", rate);
                s.Set("enabled", "true");
                s.Set(StoppedByKey, "");   // the user's own on: an off this ends is no longer Caps Lock's
                // Save() reports a failed write (a full disk, a locked or read-only settings.json) by
                // returning false, never by throwing: the host's store swallows the I/O exception, and so
                // does every other IModuleSettings in the repo. ApplyState re-reads the FILE, so a failed
                // write leaves the live state exactly as it was and the click did nothing; until this line
                // nothing said so (F110). Logging only, deliberately: driving the blinker from the in-memory
                // values would run a state the tray, which reads the file, denies, and "disk wins" is at
                // least consistent.
                if (!s.Save())
                {
                    Log("tray pick not persisted (rate " + rate + ", on), so the live state is unchanged");
                    // Except during a Caps Lock stop, where ApplyState(true) would END the stop and act on the disk:
                    // with the box ticked the disk still reads on, so a pick whose write failed started the blinker
                    // under a line saying nothing changed. Returning keeps the stop, and the line true.
                    if (_stoppedByCaps) return;
                }
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        private void SetEnabledFromTray(bool on)
        {
            try
            {
                IModuleSettings s = Settings();
                // Already saved that way, so no write, unless the saved off is one a Caps Lock stop saved: an Off
                // picked over it makes it the user's own, so the reason goes (a write) and the row and the next start
                // stop naming Caps Lock.
                if (s.GetBool("enabled", true) == on && !SavedOffIsCapsLocks(s))
                {
                    // A Caps Lock stop this session still yields to the user's own pick, so the tray stops naming it:
                    // an Off over a saved off with no reason ends it quietly, an on over a saved on (the box ticked)
                    // starts it again.
                    if (_stoppedByCaps) ApplyState(true);
                    return;
                }
                s.Set("enabled", on ? "true" : "false");
                s.Set(StoppedByKey, "");   // the user's own on or off: no longer Caps Lock's
                // Same as SetRateFromTray: a false from Save() is the only way a failed write is reported, and during a
                // Caps Lock stop ApplyState(true) would end the stop and act on the disk (an Off whose write failed,
                // with the box ticked, started the blinker from the disk's on), so the click returns having done nothing.
                if (!s.Save())
                {
                    Log("tray pick not persisted (" + (on ? "on" : "off") + "), so the live state is unchanged");
                    if (_stoppedByCaps) return;
                }
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        /// <summary>Caps Lock came on and the setting says stop. The standalone app quit here and saved nothing,
        /// so its next launch blinked again; a module cannot quit, so it stops, and since 1.0.7 what it saves
        /// follows "Resume on application restart": ticked (the default), nothing, so the stop lasts until the
        /// companion next starts, or until the user picks a speed or Off in the tray or applies a changed on/off or
        /// speed, or an unticked "Stop when Caps Lock is on", in the pane; unticked, the off, as 1.0.0 to 1.0.6 always
        /// did, and since 1.0.7 its reason beside it (StoppedByKey), so a restart still names Caps Lock until the user
        /// switches it on or picks Off; once the box is ticked over it, the next start resumes it, and so, sooner, does a
        /// tray speed pick, as it ends any saved off. No label to update: the tray entry
        /// reads its state through DynamicText when the menu next opens, and names the stop.</summary>
        private void OnCapsLockStop()
        {
            // Until 1.0.7 this was the one state change the user was never told about: the remark is
            // deliberately suppressed (they pressed the key, and it can fire mid-typing) and the OFF was always
            // PERSISTED, so a week later the feature was off, the tray agreed it was off, and nothing anywhere said
            // that Caps Lock did it. That is the report the owner made on 2026-10-14 ("I had hit capslock and it
            // was not running"), which this comment had predicted. The remark is still suppressed; the tray row
            // names the stop, the line below says what the next start will do, and the startup line says what it
            // did. Set first, whichever way the write goes, so the tray names the stop even when it fails.
            _stoppedByCaps = true;
            bool resume;
            try { resume = ResumesOnRestart(Settings()); }
            catch { resume = true; }   // the box's default; a store that throws on a read could not save the off either
            if (resume)
            {
                Log("caps lock is on: stopped blinking for this session and saved nothing, so it resumes at the next start");
                return;
            }

            // The blinking has ALREADY stopped -- the engine stops before raising this -- so the line
            // records a transition that has happened, and it says WHICH WAY the settings write went. Save()
            // reports a failed write by returning false, never by throwing: the host's store swallows the
            // I/O exception, MemoryModuleSettings always answers false, the ModuleKit fake answers
            // !FailSaves. The previous shape logged "saved it as off" BEFORE the save and caught an
            // exception no implementation raises (its Categorize helper had that one dead caller), so a
            // full disk or a locked settings.json produced a false "saved" line and nothing else, and the
            // next settings read started the blinking again with the log claiming the stop was persisted
            // (F110). One line either way, so the self-test's "exactly one line" holds and a reader of the
            // log gets the two states told apart, which is what that catch was written to do. A failed write
            // leaves the saved on, so that line says the next start brings it back, as the tray row does. The
            // reason goes in the same Save() as the off, so the two land together or not at all.
            bool saved;
            try
            {
                IModuleSettings s = Settings();
                s.Set(StoppedByKey, StoppedByCapsLock);
                s.Set("enabled", "false");
                saved = s.Save();
            }
            catch { saved = false; }   // a third-party host that throws; every shipped store returns false
            Log(saved
                ? "caps lock is on: stopped blinking and saved it as off, so it stays off at the next start"
                : "caps lock is on: stopped blinking but the off was NOT persisted, so it resumes at the next start");
        }

        /// <summary>The user switched it on while Caps Lock was already on (RA-088). It follows the Caps Lock
        /// stop's rule (1.0.7): with "Resume on application restart" ticked no off is written, so the on the pick
        /// or Apply just saved stands and the next start tries again; unticked, the off and its reason are persisted
        /// through the same Save()-checked path the Caps Lock stop uses. One log line says which, and ONE
        /// Caps-Lock-specific line replaces the ON line either way.
        ///
        /// RA-088 rejected refusing while leaving enabled=true, because the tray and the pane would say "on" with
        /// nothing running and nothing that would ever start it (no timer runs while stopped, and only the next
        /// settings change reaches ApplyState): the disk/live drift the "disk wins" rule exists to avoid. With the
        /// box ticked 1.0.7 takes that shape, on the owner's decision of 2026-10-14. The tray half of the objection
        /// is answered: the tray row names the stop instead of saying "on", and the next start, or the user's next
        /// pick or changed Apply, does start it. The pane half is accepted on purpose: the pane's "Blink the Scroll
        /// Lock light" box still reads ticked meanwhile, because it shows the saved choice, which is what the next
        /// start acts on.</summary>
        private void RefuseEnableUnderCapsLock(IModuleSettings s)
        {
            _stoppedByCaps = true;
            if (ResumesOnRestart(s))
            {
                // The pick's own Save() already wrote the on and the speed; this path writes nothing more, and the
                // line says that the pick was kept, so a reader does not take "saved nothing" to mean the pick was lost.
                Log("caps lock is on: did not start blinking and kept your pick, saving no off, so the next start tries again");
            }
            else
            {
                // `persisted`, not `saved`: the F110 mutation case "the Caps Lock stop reports 'saved' whatever
                // Save() answered" matches OnCapsLockStop's `saved = s.Save();` byte-exactly, and a second copy of
                // that line would turn the case into a NO-OP (pattern matched 2 times), which the harness scores as
                // a failure.
                bool persisted;
                try { s.Set(StoppedByKey, StoppedByCapsLock); s.Set("enabled", "false"); persisted = s.Save(); }
                catch { persisted = false; }   // as in OnCapsLockStop: every shipped store returns false rather than throwing
                Log(persisted
                    ? "caps lock is on: did not start blinking and saved it as off, so it stays off at the next start"
                    : "caps lock is on: did not start blinking but the off was NOT persisted, so the next start tries again");
            }
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

        /// <summary>The pane's Load: what is stored, and a copy of it as what this pane now shows
        /// (<see cref="_paneShown"/>, N-blinkingled-resume-03).</summary>
        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            IReadOnlyDictionary<string, string> stored = StoredPaneValues();
            _paneShown = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in stored) _paneShown[kv.Key] = kv.Value;
            return stored;
        }

        /// <summary>
        /// What this options pane shows (N-blinkingled-resume-03): what it last loaded, or, after an Apply whose write
        /// landed, what that Apply submitted over it, because the window re-reads a pane after Apply only when its
        /// schema has an Info or Header row (RefreshAfterApply in src/Portable/Wpf/OptionsWindow.cs) and this schema has
        /// neither, so after an Apply the pane goes on showing what it sent. Null until the host first loads the pane.
        /// UI thread only, like Load and Save. A failed write leaves it as it was, so the next Apply sends the same
        /// change again rather than taking it for one already made.
        /// </summary>
        private Dictionary<string, string> _paneShown;

        /// <summary>What a pane Apply is compared with: <see cref="_paneShown"/>, or, for a pane no host has loaded
        /// (a direct Save, as the self-test makes), what is stored, which is what Load would have shown.</summary>
        private IReadOnlyDictionary<string, string> PaneShown()
        {
            return _paneShown ?? StoredPaneValues();
        }

        private IReadOnlyDictionary<string, string> StoredPaneValues()
        {
            IModuleSettings s = Settings();
            string rate = s.Get("rate", ScrollLockBlinker.DefaultRate);
            if (!ScrollLockBlinker.IsKnownRate(rate)) rate = ScrollLockBlinker.DefaultRate;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "enabled", s.GetBool("enabled", true) ? "true" : "false" },
                { "rate", rate },
                { "capsStops", s.GetBool("capsStops", true) ? "true" : "false" },
                { "resumeOnRestart", ResumesOnRestart(s) ? "true" : "false" },
                { "announce", s.GetBool("announce", true) ? "true" : "false" },
            };
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IModuleSettings s = Settings();
            // What the user changed ON THIS PANE (N-blinkingled-resume-03, the owner's decision of 2026-10-26: module-side):
            // each value this Apply submits against what the pane shows, which is what it last loaded or last applied
            // (PaneShown), and ONLY those are written. The pane sends every box on Apply, so "enabled" and "rate" arrive
            // whether or not they were touched, and they used to be written whatever they said: compared with the disk,
            // a tray pick made while the window was open read as the user's change on the pane, and the next Apply from
            // that window, changing only another box, wrote the pane's stale on/off and speed over it (the final review
            // of feature/blinkingled-resume, probe S9: a tray speed pick over a Caps Lock off, then an announce-only Apply,
            // saved a plain off and cleared the reason). This reverses that lane's refusal of "comparing an Apply with
            // what the pane last loaded instead of the disk": the second copy is the pane's own view, which is what the
            // user's Apply is a change to, and the disk still wins for everything the user did not touch there.
            IReadOnlyDictionary<string, string> shown = PaneShown();
            bool onOffChanged = PaneChanges(values, shown, "enabled");
            // Unticking "Stop when Caps Lock is on" is the user saying Caps Lock should not stop it, so during a stop it is
            // a choice like a changed on/off or speed (a stop needs that box ticked, so a change to it there is an untick).
            bool choiceChanged = onOffChanged || PaneChanges(values, shown, "rate") || PaneChanges(values, shown, "capsStops");
            IReadOnlyDictionary<string, string> submitted = values;
            values = ChangedOnPane(values, shown);   // from here on, only what the user changed on this pane
            string v;
            if (values.TryGetValue("enabled", out v)) s.Set("enabled", v);
            if (values.TryGetValue("rate", out v) && ScrollLockBlinker.IsKnownRate(v)) s.Set("rate", v);
            if (values.TryGetValue("capsStops", out v)) s.Set("capsStops", v);
            if (values.TryGetValue("resumeOnRestart", out v)) s.Set("resumeOnRestart", v);
            if (values.TryGetValue("announce", out v)) s.Set("announce", v);
            if (onOffChanged) s.Set(StoppedByKey, "");   // the user set on/off here, so a Caps Lock off's reason goes
            // Nothing else here ends an off a Caps Lock stop SAVED (the box was unticked then), whatever "Resume on application
            // restart" says now: this Apply writes an on/off only when the user changed it on this pane, and no other. The final
            // round (the third fix round) had a changed speed or an unticked "Stop when Caps Lock is on" switch such an off back
            // on under a ticked box, as they end a session stop, and that wrote an on this Apply never sent. The options window
            // re-reads a pane after Apply only when its schema has an Info or Header row (RefreshAfterApply in
            // src/Portable/Wpf/OptionsWindow.cs), and this schema has neither, so the pane went on showing the off it loaded;
            // the user's next Apply from it sent that off, which read here as their own Off, and saved a plain off they never
            // picked: the failure this version exists to end. Refused: an Info row only so the window rebuilds the pane, which
            // rests the rule on when a host chooses to re-read a pane, something the module ABI does not state. Comparing with
            // what the pane last loaded, refused here at first as a second copy of the state beside the disk, is what this Apply
            // has done since N-blinkingled-resume-03 (see the top), and it keeps that off too. The tray needs neither, since its
            // menu is built afresh on every open: a tray speed pick ends such an off (SetRateFromTray), and under a ticked box
            // the next start resumes it (ResumeSavedCapsLockOff). A session stop is untouched by this: it saves nothing, so the
            // pane loaded the saved on, and an Apply from it, however long ago it loaded, sends an off only when the user
            // unticks "Blink the Scroll Lock light" there.
            bool ok = s.Save();
            if (ok) RememberPaneShown(shown, submitted);   // the pane now shows what it sent
            // A write that failed during a Caps Lock stop changes nothing, as in the two tray handlers: with the box ticked
            // the disk still reads on, so ApplyState(true) would END the stop and start the blinker from the disk, a pane
            // Off included, while the pane says the settings could not be saved. Outside a stop a failed write still
            // reaches ApplyState, which re-reads the unchanged disk and changes nothing.
            if (!ok && _stoppedByCaps) return ok;
            // A Caps Lock stop this session ends on the user's own on/off, speed or untick of "Stop when Caps Lock is on",
            // and not on an Apply that changed only the other boxes: with the box ticked the pane shows the saved on, so
            // ending the stop there restarted the blinker for an Apply that only switched "announce", or, with Caps Lock
            // still on, spoke the refusal and, when that Apply had unticked "Resume on application restart", saved the
            // very off the box exists to prevent. The other boxes are saved either way, and take effect at the next
            // choice or start.
            if (_stoppedByCaps && !choiceChanged) return ok;
            ApplyState(true);   // the user was just here, so an on/off change may speak
            return ok;
        }

        /// <summary>The entries of <paramref name="values"/> that differ from what the pane showed, by
        /// <see cref="PaneChanges"/>: what one Apply writes (N-blinkingled-resume-03).</summary>
        private static IReadOnlyDictionary<string, string> ChangedOnPane(IReadOnlyDictionary<string, string> values,
                                                                        IReadOnlyDictionary<string, string> shown)
        {
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in values)
                if (PaneChanges(values, shown, kv.Key)) changed[kv.Key] = kv.Value;
            return changed;
        }

        /// <summary>After an Apply whose write landed, the pane shows what it submitted over what it showed before.</summary>
        private void RememberPaneShown(IReadOnlyDictionary<string, string> shown, IReadOnlyDictionary<string, string> submitted)
        {
            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in shown) next[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, string> kv in submitted) next[kv.Key] = kv.Value;
            _paneShown = next;
        }

        /// <summary>Whether a pane Apply changes <paramref name="key"/> from what Load showed, reading the submitted value
        /// the way the module reads the stored one: an unparsable box as ticked (GetBool's fallback, and both boxes read
        /// here default to ticked), a rate the pane would refuse to save as no change.</summary>
        private static bool PaneChanges(IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> shown,
                                        string key)
        {
            string submitted, before;
            if (!values.TryGetValue(key, out submitted) || !shown.TryGetValue(key, out before)) return false;
            if (key == "enabled" || key == "capsStops")
            {
                bool parsed;
                submitted = (!bool.TryParse(submitted, out parsed) || parsed) ? "true" : "false";
            }
            else if (key == "rate" && !ScrollLockBlinker.IsKnownRate(submitted)) return false;
            return !string.Equals(submitted, before, StringComparison.Ordinal);
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
                // Decided FIRST, before anything below could press: null when this run may press the real Scroll Lock,
                // otherwise the reason it may not, which every skipped check prints (NoRealInputVariable).
                string noRealInput = SelfCheckRealInputOptOut(probe);

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
                    // outside that window (RA-090); with real input withheld, the engine's real sender withholds
                    // its presses too (ScrollLockBlinker.RealInputWithheld). The adoption itself is asserted on
                    // private probes further down through the same seam.
                    module._blinker.ScrollLockReader = delegate { return false; };
                    // Caps Lock likewise: pinned dark, so the pane saves and tray picks below start whatever the
                    // developer's own Caps Lock is doing; the RA-088 section swaps "on" in through this seam.
                    module._blinker.CapsLockReader = delegate { return false; };
                    // And with real input withheld, the keypress too: this instance's one blink below then goes through the
                    // self-test's own sender, so the log-wiring check still runs and nothing reaches Windows, and the two
                    // checks that are ABOUT the real press are skipped on their own lines.
                    if (noRealInput != null) module._blinker.KeypressSender = AcceptedKeypress;

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
                    probe.Check("a blink attempt records whether Windows accepted it" +
                                (noRealInput != null ? " (here the self-test's own sender answered, with real input withheld)" : ""),
                        CountLogged(host.LoggedLines, "blink delivery") - deliveryLinesBefore >= 1);
                    // The interop itself, on that one real call: accepted or refused is the machine's answer
                    // and neither is asserted, but a THROW (a DllImport that does not resolve, a marshalling
                    // shape the runtime rejects) is ours, is the same answer on every machine, and would hide
                    // behind the outcome-agnostic line above exactly as the cbSize defect did. With real input
                    // withheld there was no real call, so this is skipped, out loud, rather than passed on a fake.
                    if (noRealInput != null) SkipRealPress(probe, noRealInput, "the real SendInput call completes without throwing");
                    else probe.Check("the real SendInput call completes without throwing (-1 is this module's marker for a throw)",
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
                    // pressed (RA-090), that instance presses nothing on the way out. With real input withheld there
                    // were no real presses to pair, so the pairing check is skipped out loud and the run instead asserts,
                    // at its end, that the real-press counter never moved, and leaves the process's counts after the
                    // loader's own Shutdown in a record the runner reads (RecordRealInputAfterShutdown).
                    if (noRealInput != null)
                    {
                        SkipRealPress(probe, noRealInput, "the suite's real keypresses are paired, so this instance leaves Scroll Lock where the machine had it");
                    }
                    else
                    {
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
                    }

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

                    // A Caps Lock stop's remark is suppressed on purpose, so its log line is the record. This
                    // section and the F110 and RA-088 sections below pin the SAVED off, which since 1.0.7 is the
                    // "Resume on application restart" box unticked, 1.0.6's behaviour kept for whoever wants Caps
                    // Lock as a lasting off switch; so the box is unticked here, through the pane, and stays
                    // unticked through them. The ticked default, which saves nothing, is driven on fresh instances
                    // by SelfCheckCapsLockResume below, where a restart can be modelled.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Slow" },
                        { "capsStops", "true" }, { "announce", "false" },
                        { "resumeOnRestart", "false" },
                    });
                    probe.Check("the pane saves and reloads 'Resume on application restart' unticked",
                        pane.Load()["resumeOnRestart"] == "false");
                    loggedBefore = host.LoggedLines.Count;
                    module.OnCapsLockStop();
                    probe.Check("a Caps Lock stop is recorded, since nothing else reports it",
                        CountLogged(host.LoggedLines, "caps lock is on") == 1 &&
                        host.LoggedLines.Count - loggedBefore == 1);
                    probe.Check("...and with 'Resume on application restart' unticked it really did switch off, saved",
                        pane.Load()["enabled"] == "false");

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
                    probe.Check("...and the settings still read enabled, which is what that line warns the next start will act on",
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
                    // repeat pick replayed the cycle. State here: off, rate Slow, capsStops on, announce off, and
                    // "Resume on application restart" unticked, so the refusal saves the off; with it ticked the
                    // refusal saves nothing (SelfCheckCapsLockResume).
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "true" }, { "announce", "true" },
                        { "resumeOnRestart", "false" },
                    });                                                       // announce on; no transition, so quiet
                    module._blinker.CapsLockReader = delegate { return true; };
                    int saidBeforeCaps = host.SaidLines.Count;
                    loggedBefore = host.LoggedLines.Count;
                    module.SetRateFromTray("Fast");                           // the user switches it on under Caps Lock
                    probe.Check("enabling under Caps Lock does not start the blinker, and with 'Resume on application restart' unticked persists the off and keeps the chosen speed",
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

                    // ---- "Resume on application restart" (1.0.7) ----------------------------------------
                    // On fresh instances, each against its own host, so nothing above is disturbed. After this
                    // the engine's static log sink is null (each session's Shutdown drops it); nothing below
                    // reads an engine line.
                    SelfCheckCapsLockResume(probe);

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

                    // LAST in this method, after the Shutdown of every instance it made: with real input withheld, none of
                    // them reached SendInput. The counter is the engine's own (it advances at the call, before SendInput
                    // answers, so it counts every press that got that far, accepted or refused), static, so a probe added
                    // later without a pinned sender shows here, and an absolute 0, so a press anywhere in this process
                    // before the self-test shows too. What it cannot see is the convention runner's own instance, which
                    // the loader shuts down after this method has returned; the label says so, and that instance is
                    // covered twice: the engine withholds for the whole process (checked in SelfCheckRealInputOptOut),
                    // and its Shutdown leaves the process's counts in the record tests\Test-ModuleSelfTests.ps1 reads
                    // after the run (RecordRealInputAfterShutdown).
                    if (noRealInput != null)
                        probe.Check("with real input withheld, the engine's real-press counter stayed 0 through every instance this self-test ran, so none of them pressed (RealKeypressCount="
                                    + ScrollLockBlinker.RealKeypressCount + ")",
                            ScrollLockBlinker.RealKeypressCount == 0);
                }
            }
            catch (Exception ex) { probe.Exception(ex); }
            return probe.Finish(out detail);
        }

        /// <summary>
        /// The environment variable that asks this self-test to send NO real input. Set to 1, every check that would press
        /// the real Scroll Lock is skipped and says so on its own SKIPPED line, which names this variable, and the run then
        /// asserts that the engine's real-press counter stayed 0. The owner's decision of 2026-10-14 ("Skip on agent runs"):
        /// agent runs on the maintainer's box are to set it, so nothing is pressed in the owner's session, and the skip
        /// says so on every such run; CI and a normal run leave it unset and still make the suite's one pair of real Scroll
        /// Lock presses (F113), so the real SendInput path stays tested. tests\Test-ModuleSelfTests.ps1 requires exactly
        /// the two SKIPPED lines, the counter's PASS line and the process record, and the gate's summary repeats what it
        /// measured. Outside the self-test it is read in one place, the engine's real sender
        /// (ScrollLockBlinker.RealInputWithheld), and acted on only in a process whose command line carries a self-test flag
        /// (<see cref="WithholdsRealInput"/>), so a normal start never reads it as a reason not to blink.
        /// </summary>
        internal const string NoRealInputVariable = "DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT";

        /// <summary>The engine's decision (ScrollLockBlinker.RealInputWithheld): withhold every real press when
        /// <paramref name="commandLine"/> runs a self-test (<see cref="RunsASelfTest"/>) and <see cref="NoRealInputVariable"/>
        /// reads 1. Both halves are injected, so <see cref="SelfCheckRealInputOptOut"/> drives every answer on fakes and no
        /// check of the decision, and no mutation of it, needs a real press.</summary>
        internal static bool WithholdsRealInput(Func<string, string> readVariable, string[] commandLine)
        {
            return RealInputDisabled(readVariable) && RunsASelfTest(commandLine);
        }

        /// <summary>Whether a process's command line (Environment.GetCommandLineArgs) runs a self-test, read the way
        /// Program.cs dispatches one: "--module-selftest=&lt;id&gt;" in any case, the convention runner's prefix
        /// (ModuleConventionSelfTest.FindRequestedId compares it ignoring case), or a flag "--&lt;name&gt;-selftest" spelled
        /// exactly, which is the shape of every other self-test flag Program.cs matches (--module-host-selftest, which also
        /// loads this module, among them). A normal start has neither: its arguments are the exe and, after a module
        /// install, "--reopen-options=&lt;pane&gt;". A misspelt flag starts the full app, so it is rightly not a self-test
        /// here either.</summary>
        internal static bool RunsASelfTest(string[] commandLine)
        {
            if (commandLine == null) return false;
            foreach (string arg in commandLine)
            {
                if (arg == null || !arg.StartsWith("--", StringComparison.Ordinal)) continue;
                if (arg.StartsWith("--module-selftest=", StringComparison.OrdinalIgnoreCase)) return true;   // the convention runner
                if (arg.EndsWith("-selftest", StringComparison.Ordinal)) return true;   // every other self-test flag
            }
            return false;
        }

        /// <summary>Whether the command line is this module's own convention self-test, "--module-selftest=blinkingled" read
        /// the way ModuleConventionSelfTest.FindRequestedId reads it (the prefix ignoring case, the id trimmed of spaces and
        /// quotes): the one process whose marker tests\Test-ModuleSelfTests.ps1 reads, and so the one that leaves the
        /// record (RecordRealInputAfterShutdown).</summary>
        internal static bool RunsThisModulesSelfTest(string[] commandLine)
        {
            const string prefix = "--module-selftest=";
            if (commandLine == null) return false;
            foreach (string arg in commandLine)
            {
                if (arg == null || !arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                return string.Equals(arg.Substring(prefix.Length).Trim().Trim('"'), "blinkingled", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>Whether <paramref name="readVariable"/> answers 1 for <see cref="NoRealInputVariable"/>, spaces around it
        /// allowed (cmd's <c>set X=1 &amp;&amp; ...</c> keeps the space before the ampersands). Anything else, unset included,
        /// keeps the real press. The reader is injected, so <see cref="SelfCheckRealInputOptOut"/> drives every answer on a
        /// fake and no check of the decision, and no mutation of it, needs a real press.</summary>
        internal static bool RealInputDisabled(Func<string, string> readVariable)
        {
            string value = readVariable(NoRealInputVariable);
            return value != null && value.Trim() == "1";
        }

        /// <summary>
        /// The opt-out's decision, checked on fake environment readers before anything in the self-test could press, then
        /// taken from the real environment. Returns the reason this run may not press the real Scroll Lock, or null when it
        /// may. FAIL-CLOSED: a decision that gets any of its own cases wrong is not trusted to allow a press, so a broken
        /// decision costs a red run and never a real keypress, which is what lets its mutation cases run with no real
        /// input. The fake answers for the variable's LITERAL name, not for <see cref="NoRealInputVariable"/>, so a typo in
        /// the constant fails the first check instead of reading an unset variable and pressing.
        /// </summary>
        private static string SelfCheckRealInputOptOut(SelfTestProbe probe)
        {
            Func<string, Func<string, string>> answering = value => name =>
                string.Equals(name, "DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT", StringComparison.Ordinal) ? value : null;
            bool sound = probe.Check("the real-input opt-out reads DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1, with or without spaces around the 1, as 'make no real press'",
                RealInputDisabled(answering("1")) && RealInputDisabled(answering(" 1 ")));
            sound &= probe.Check("WITNESS with the opt-out unset the self-test still presses for real, so CI and a normal run keep testing the real SendInput path",
                !RealInputDisabled(answering(null)));
            sound &= probe.Check("WITNESS the opt-out set to anything but 1 (0, empty, true) still presses for real",
                !RealInputDisabled(answering("0")) && !RealInputDisabled(answering("")) && !RealInputDisabled(answering("true")));

            // The engine's half: its one SendInput call withholds when the variable reads 1 AND the process runs a self-test,
            // judged from the command line the way Program.cs dispatches one, so the variable cannot reach a normal start.
            string[] selfTestRun = { "DesktopAICompanion.exe", "--module-selftest=blinkingled" };
            sound &= probe.Check("the engine withholds real input in a self-test process run with DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1: --module-selftest=<id> in any case, and every --<name>-selftest flag",
                WithholdsRealInput(answering("1"), selfTestRun) &&
                WithholdsRealInput(answering("1"), new[] { "DesktopAICompanion.exe", "--MODULE-SELFTEST=blinkingled" }) &&
                WithholdsRealInput(answering("1"), new[] { "DesktopAICompanion.exe", "--module-host-selftest" }));
            sound &= probe.Check("WITNESS the same variable in a normal start, with no self-test flag, withholds nothing, so it cannot stop the shipped module blinking",
                !WithholdsRealInput(answering("1"), new[] { "DesktopAICompanion.exe" }) &&
                !WithholdsRealInput(answering("1"), new[] { "DesktopAICompanion.exe", "--reopen-options=blinkingled" }) &&
                !WithholdsRealInput(answering("1"), null));
            sound &= probe.Check("WITNESS a self-test process without the variable at 1 withholds nothing, so CI still makes the real press",
                !WithholdsRealInput(answering(null), selfTestRun) && !WithholdsRealInput(answering("0"), selfTestRun));
            if (!sound) return "real input withheld because the opt-out's own checks failed (the FAIL lines above)";
            if (!RealInputDisabled(Environment.GetEnvironmentVariable)) return null;

            // And THIS process, through the engine's own cached reading of the real environment and command line: the one
            // check that the withholding is armed for every instance here, the convention runner's included, which the
            // loader shuts down after this self-test returns and which no pin below reaches.
            probe.Check("with DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT=1, this self-test process withholds every real press at the engine's one SendInput call, so no instance in it reaches Windows, the convention runner's own included",
                ScrollLockBlinker.RealInputWithheld);
            return "real input disabled by " + NoRealInputVariable + "=1";
        }

        /// <summary>The one loud line a check that would press the real Scroll Lock prints in its place when real input is
        /// withheld: SKIPPED, the reason (which names the variable), and the check. Never a PASS, since the check did not
        /// run; never silent, since it prints on every such run.</summary>
        private static void SkipRealPress(SelfTestProbe probe, string reason, string check)
        {
            probe.Note("SKIPPED: " + reason + "; the real Scroll Lock press was not made, so this check did not run: " + check);
        }

        /// <summary>
        /// "Resume on application restart" (1.0.7, the owner's decision of 2026-10-14), driven on fresh instances,
        /// because the claim is about the NEXT start: each session is a new RecordingHost seeded with what the
        /// previous one saved and a new module Init'd over it, which is what a companion start is. The fake shows
        /// the disk after every Save(), failed or not (N-blinkingled-02), and the module never leaves a Set()
        /// unsaved, so a session's values are what a restart reads.
        ///
        /// No real input. Init's own ApplyState reads the real keys (Start() adopts Scroll Lock, which is a read)
        /// and presses nothing, since no message loop runs; <see cref="StartSession"/> then pins both readers and
        /// the keypress, and Shutdown presses nothing on an instance that never pressed (RA-090). The Caps Lock stop
        /// is driven through the engine's own Tick() with the Caps Lock reader pinned on, the path the timer runs.
        /// </summary>
        private static void SelfCheckCapsLockResume(SelfTestProbe probe)
        {
            // ---- the box, and its default --------------------------------------------------------------
            DesktopAICompanion.ModuleKit.Testing.RecordingHost blankHost;
            BlinkingLedModule blank = StartSession(new Dictionary<string, string>(), out blankHost);
            IReadOnlyList<SettingField> schema = blankHost.OptionsPanes[0].Schema;
            int capsAt = -1, resumeAt = -1;
            for (int i = 0; i < schema.Count; i++)
            {
                if (schema[i].Id == "capsStops") capsAt = i;
                if (schema[i].Id == "resumeOnRestart") resumeAt = i;
            }
            SettingField resumeField = resumeAt >= 0 ? schema[resumeAt] : null;
            probe.Check("the 'Resume on application restart' box is a Bool in the Blinking LED card, directly after 'Stop when Caps Lock is on'",
                resumeField != null && resumeField.Label == "Resume on application restart" &&
                resumeField.Kind == SettingKind.Bool && resumeField.Group == "Blinking LED" &&
                capsAt >= 0 && resumeAt == capsAt + 1);
            probe.Check("'Resume on application restart' defaults to ticked: with nothing saved the pane loads it on",
                blankHost.OptionsPanes[0].Load()["resumeOnRestart"] == "true");
            // Every start logs ONE line with its state; the defaults are on, Normal, and both boxes ticked.
            probe.Check("the startup log line names the state: on, the rate, and both Caps Lock settings",
                CountLogged(blankHost.LoggedLines, "starting: ") == 1 &&
                LastLoggedMatching(blankHost.LoggedLines, "starting: ") ==
                    "blinkingled: starting: on, rate Normal, Caps Lock stops it: yes, resume on application restart: yes");
            blank.Shutdown();

            // ---- a Caps Lock stop, the box ticked (left at its default) ---------------------------------
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostA;
            BlinkingLedModule a = StartSession(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" },
            }, out hostA);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskA = hostA.SettingsFor("blinkingled");
            probe.Check("WITNESS the resume session starts blinking, so the stop below has something to stop",
                a._blinker.IsRunning);
            int savesBefore = diskA.SaveCount;
            int loggedBefore = hostA.LoggedLines.Count;
            a._blinker.CapsLockReader = delegate { return true; };
            a._blinker.Tick();                                         // the engine stops, then raises the stop
            probe.Check("with 'Resume on application restart' ticked, a Caps Lock stop stops the blinker and saves nothing",
                !a._blinker.IsRunning && diskA.SaveCount == savesBefore && diskA.Get("enabled", null) == "true");
            string stopLine = LastLoggedMatching(hostA.LoggedLines, "caps lock is on");
            probe.Check("...and its one log line says it saved nothing and resumes at the next start",
                hostA.LoggedLines.Count - loggedBefore == 1 && stopLine != null &&
                stopLine.Contains("saved nothing") && stopLine.Contains("resumes at the next start"));
            probe.Check("...and the tray names the Caps Lock stop and says it resumes at the next start",
                hostA.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");

            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostANext;
            BlinkingLedModule aNext = StartSession(SavedCopy(diskA), out hostANext);
            probe.Check("...and a fresh instance over the same settings starts blinking, as the standalone app did after its Caps Lock quit",
                aNext._blinker.IsRunning);
            aNext.Shutdown();

            // The stop leaves the speed ticked (the saved on), so picking that speed is the user's "start again".
            a._blinker.CapsLockReader = delegate { return false; };    // the user has switched Caps Lock off
            int saidBefore = hostA.SaidLines.Count;
            a.SetRateFromTray("Normal");
            probe.Check("after a Caps Lock stop, picking the speed still ticked starts it again, since the user's own choice ends the stop",
                a._blinker.IsRunning && hostA.SaidLines.Count - saidBefore == 1 &&
                hostA.SaidLines[hostA.SaidLines.Count - 1] == "Keeping the lights on for you.");
            probe.Check("WITNESS ...and the tray reads 'Blinking LED: on' again, so the tray check above can fail",
                hostA.TrayItems[0].DynamicText() == "Blinking LED: on");
            a.Shutdown();

            // ---- the same stop, the box unticked: 1.0.6's saved off -------------------------------------
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostB;
            BlinkingLedModule b = StartSession(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" }, { "resumeOnRestart", "false" },
            }, out hostB);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskB = hostB.SettingsFor("blinkingled");
            probe.Check("WITNESS a saved untick loads unticked, so the default check above can fail",
                hostB.OptionsPanes[0].Load()["resumeOnRestart"] == "false");
            probe.Check("WITNESS the startup line follows the settings: an unticked box reads 'resume on application restart: no'",
                LastLoggedMatching(hostB.LoggedLines, "starting: ") ==
                    "blinkingled: starting: on, rate Normal, Caps Lock stops it: yes, resume on application restart: no");
            savesBefore = diskB.SaveCount;
            loggedBefore = hostB.LoggedLines.Count;
            b._blinker.CapsLockReader = delegate { return true; };
            b._blinker.Tick();
            // The positive control for "saves nothing" above: the same stop through the same seam moves the count.
            probe.Check("WITNESS with 'Resume on application restart' unticked, the same Caps Lock stop saves the off, as 1.0.6 did",
                !b._blinker.IsRunning && diskB.SaveCount - savesBefore == 1 && diskB.Get("enabled", null) == "false");
            stopLine = LastLoggedMatching(hostB.LoggedLines, "caps lock is on");
            probe.Check("...and its one log line says the off was saved and stays off at the next start",
                hostB.LoggedLines.Count - loggedBefore == 1 && stopLine != null &&
                stopLine.Contains("saved it as off") && stopLine.Contains("stays off at the next start"));
            probe.Check("...and the tray names the Caps Lock stop and says it is off until a speed is picked",
                hostB.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (off until you pick a speed)");

            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBNext;
            BlinkingLedModule bNext = StartSession(SavedCopy(diskB), out hostBNext);
            probe.Check("with 'Resume on application restart' unticked, a fresh instance over the same settings stays off after a Caps Lock stop",
                !bNext._blinker.IsRunning);
            // The reason was saved beside the off, so the restart still knows whose off it is.
            probe.Check("...and its startup line says 'off (saved by a Caps Lock stop)'",
                LastLoggedMatching(hostBNext.LoggedLines, "starting: ") ==
                    "blinkingled: starting: off (saved by a Caps Lock stop), rate Normal, Caps Lock stops it: yes, resume on application restart: no");
            probe.Check("...and after the restart the tray row still names the Caps Lock stop and says it is off until a speed is picked",
                hostBNext.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (off until you pick a speed)");
            // The user's Off over it makes the off theirs: the reason goes, so the start after that reads a plain off.
            bNext.SetEnabledFromTray(false);
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBAfterOff;
            BlinkingLedModule bAfterOff = StartSession(SavedCopy(hostBNext.SettingsFor("blinkingled")), out hostBAfterOff);
            probe.Check("a tray Off over a restarted Caps Lock off makes it the user's: the next start reads 'Blinking LED: off' and 'off (saved)'",
                !bAfterOff._blinker.IsRunning && hostBAfterOff.TrayItems[0].DynamicText() == "Blinking LED: off" &&
                LastLoggedMatching(hostBAfterOff.LoggedLines, "starting: ") ==
                    "blinkingled: starting: off (saved), rate Normal, Caps Lock stops it: yes, resume on application restart: no");
            bAfterOff.Shutdown();
            bNext.Shutdown();

            // A tray speed pick over a restarted Caps Lock off is the user's own on, so the reason goes with the off.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBPick;
            BlinkingLedModule bPick = StartSession(SavedCopy(diskB), out hostBPick);
            bPick.SetRateFromTray("Fast");
            probe.Check("a tray speed pick over a restarted Caps Lock off starts it and clears the saved reason",
                bPick._blinker.IsRunning && hostBPick.SettingsFor("blinkingled").Get(StoppedByKey, null) == "");
            bPick.Shutdown();

            // The pane: an Apply that changes only another box keeps a Caps Lock off's reason, and one that ticks "Blink the
            // Scroll Lock light" is the user's own on, so the reason goes.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBPane;
            BlinkingLedModule bPane = StartSession(SavedCopy(diskB), out hostBPane);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskBPane = hostBPane.SettingsFor("blinkingled");
            hostBPane.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "false" }, { "announce", "false" },
            });
            probe.Check("WITNESS a pane Apply over a restarted Caps Lock off that changes only another box keeps the reason, and the row still names Caps Lock",
                !bPane._blinker.IsRunning && diskBPane.Get(StoppedByKey, null) == StoppedByCapsLock &&
                hostBPane.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (off until you pick a speed)");
            hostBPane.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "false" }, { "announce", "false" },
            });
            probe.Check("a pane Apply that ticks 'Blink the Scroll Lock light' over a restarted Caps Lock off starts it and clears the saved reason",
                bPane._blinker.IsRunning && diskBPane.Get(StoppedByKey, null) == "");
            bPane.Shutdown();

            // What a settings file 1.0.6 left at off reads as: a Caps Lock stop and a user's Off saved the same
            // enabled=false and no reason, so no start can tell them apart, and the row and the line say only what is known.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostOld;
            BlinkingLedModule old = StartSession(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
            }, out hostOld);
            probe.Check("a settings file 1.0.6 left at off reads as a plain saved off: its startup line says 'off (saved)' and the tray row 'Blinking LED: off'",
                !old._blinker.IsRunning && hostOld.TrayItems[0].DynamicText() == "Blinking LED: off" &&
                LastLoggedMatching(hostOld.LoggedLines, "starting: ") ==
                    "blinkingled: starting: off (saved), rate Normal, Caps Lock stops it: yes, resume on application restart: yes");
            old.Shutdown();

            // The box ticked over an off a Caps Lock stop saved while it was unticked: the box says a Caps Lock stop lasts
            // until the next start, so the next start resumes it and clears the reason (the fix round's second pass). Ticked
            // through the pane by an Apply that changes nothing else, which keeps the off and its reason for this session.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBTick;
            BlinkingLedModule bTick = StartSession(SavedCopy(diskB), out hostBTick);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskBTick = hostBTick.SettingsFor("blinkingled");
            hostBTick.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "false" },
            });
            probe.Check("ticking 'Resume on application restart' over a saved Caps Lock off keeps it off for this session, and the tray row then says it resumes at the next start",
                !bTick._blinker.IsRunning && diskBTick.Get(StoppedByKey, null) == StoppedByCapsLock &&
                hostBTick.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBTickNext;
            BlinkingLedModule bTickNext = StartSession(SavedCopy(diskBTick), out hostBTickNext);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskBTickNext = hostBTickNext.SettingsFor("blinkingled");
            probe.Check("with 'Resume on application restart' ticked, an off a Caps Lock stop saved resumes at the next start: it blinks, the off and its reason are cleared, and the row reads 'Blinking LED: on'",
                bTickNext._blinker.IsRunning && diskBTickNext.Get("enabled", null) == "true" &&
                diskBTickNext.Get(StoppedByKey, null) == "" && hostBTickNext.TrayItems[0].DynamicText() == "Blinking LED: on");
            probe.Check("...and its startup line says 'on (resumed from an off a Caps Lock stop saved)'",
                LastLoggedMatching(hostBTickNext.LoggedLines, "starting: ") ==
                    "blinkingled: starting: on (resumed from an off a Caps Lock stop saved), rate Normal, Caps Lock stops it: yes, resume on application restart: yes");
            bTickNext.Shutdown();

            // The same start with the write failing: the disk still wins, so it stays off, and the line and the row say the
            // resume did not land and that the next start tries again.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBTickFailed;
            BlinkingLedModule bTickFailed = StartSession(SavedCopy(diskBTick), out hostBTickFailed, true);
            probe.Check("...and when that write fails it stays off, its startup line says 'off (saved by a Caps Lock stop; the resume was NOT persisted)' and the row says it resumes at the next start",
                !bTickFailed._blinker.IsRunning &&
                LastLoggedMatching(hostBTickFailed.LoggedLines, "starting: ") ==
                    "blinkingled: starting: off (saved by a Caps Lock stop; the resume was NOT persisted), rate Normal, Caps Lock stops it: yes, resume on application restart: yes" &&
                hostBTickFailed.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");
            bTickFailed.Shutdown();
            bTick.Shutdown();

            // Before that start, with the box ticked over such an off, a tray pick of its speed ends it, and the pane does not
            // unless it switches it on (the fourth fix round; SavePaneValues says why). The tray did this already (a speed
            // pick over a saved off is the user's own on), checked here with the box ticked, on a fresh session over the off
            // the unticked stop saved, the box ticked first by an Apply that changes nothing else (which keeps the off, as the
            // check above shows), and announce left on so the ON line is counted. It is also the positive control for the two
            // pane checks after it: the same off, ended by a choice, so their "stays as saved" can fail. The speed it had: the
            // saved off leaves no speed ticked, so this is a pick like any other.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostTTray;
            BlinkingLedModule tTray = StartSession(SavedCopy(diskB), out hostTTray);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskTTray = hostTTray.SettingsFor("blinkingled");
            TickResumeOverSavedOff(hostTTray.OptionsPanes[0]);
            bool offBefore = !tTray._blinker.IsRunning && diskTTray.Get(StoppedByKey, null) == StoppedByCapsLock;
            saidBefore = hostTTray.SaidLines.Count;
            tTray.SetRateFromTray("Normal");
            probe.Check("with 'Resume on application restart' ticked over a saved Caps Lock off, a tray pick of its speed ends it: it starts, saved on with the reason cleared, says its ON line, and the row reads 'Blinking LED: on'",
                offBefore && tTray._blinker.IsRunning && diskTTray.Get("enabled", null) == "true" &&
                diskTTray.Get(StoppedByKey, null) == "" && hostTTray.SaidLines.Count - saidBefore == 1 &&
                hostTTray.SaidLines[hostTTray.SaidLines.Count - 1] == "Keeping the lights on for you." &&
                hostTTray.TrayItems[0].DynamicText() == "Blinking LED: on");
            tTray.Shutdown();

            // The pane, as the options window leaves it: ONE pane, loaded once and never re-read between Applies (it re-reads
            // only a schema with an Info or Header row, and this one has neither). Over the same off, the user ticks the box
            // with a choice in a first Apply, then Applies the same pane again with one more change, as the review that found
            // the third fix round's pane path did: that path switched the off back on in the first Apply, so the second sent
            // the off the pane still showed and saved a plain off the user never picked. Neither Apply may write an on/off the
            // pane did not submit. Both pane choices that path took, each on its own fresh session: a speed change and then the
            // old speed again; and, with Caps Lock on, an untick of "Stop when Caps Lock is on" and then a change to announce
            // alone.
            string seenSpeed, seenUntick;
            bool keptSpeed = UnrefreshedPaneKeepsSavedCapsLockOff(SavedCopy(diskB), false, "rate", "Fast", "rate", "Normal",
                                                                 out seenSpeed);
            probe.Check("with the box ticked over a saved Caps Lock off, two Applies from one pane that is not re-read between them, a speed change and then the old speed again, leave the off and its reason exactly as saved: nothing blinks, no ON or OFF line is said, and the row says it resumes at the next start (" + seenSpeed + ")",
                keptSpeed);
            bool keptUntick = UnrefreshedPaneKeepsSavedCapsLockOff(SavedCopy(diskB), true, "capsStops", "false", "announce", "false",
                                                                  out seenUntick);
            probe.Check("with the box ticked over a saved Caps Lock off, two Applies from one pane that is not re-read between them, an untick of 'Stop when Caps Lock is on' under Caps Lock and then an announce-only change, leave the off and its reason exactly as saved: nothing blinks, no ON or OFF line is said, and the row says it resumes at the next start (" + seenUntick + ")",
                keptUntick);

            // WITNESSES: the pane ends no saved off but by switching it on. A plain saved off, here a 1.0.6 file's with the box
            // at its default (ticked), and a Caps Lock off with the box unticked both stay off through the same two choices.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostPlain;
            BlinkingLedModule plain = StartSession(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
            }, out hostPlain);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskPlain = hostPlain.SettingsFor("blinkingled");
            hostPlain.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Fast" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // the speed changed
            });
            bool plainOffAfterSpeed = !plain._blinker.IsRunning && diskPlain.Get("enabled", null) == "false";
            hostPlain.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Fast" }, { "capsStops", "false" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // "Stop when Caps Lock is on" unticked
            });
            probe.Check("WITNESS with the box ticked, a plain saved off (a 1.0.6 file's) stays off through a pane speed change and an untick of 'Stop when Caps Lock is on', and the row reads 'Blinking LED: off'",
                plainOffAfterSpeed && !plain._blinker.IsRunning && diskPlain.Get("enabled", null) == "false" &&
                hostPlain.TrayItems[0].DynamicText() == "Blinking LED: off");
            plain.Shutdown();

            DesktopAICompanion.ModuleKit.Testing.RecordingHost hostBKept;
            BlinkingLedModule bKept = StartSession(SavedCopy(diskB), out hostBKept);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings diskBKept = hostBKept.SettingsFor("blinkingled");
            hostBKept.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Fast" }, { "capsStops", "true" },
                { "resumeOnRestart", "false" }, { "announce", "true" }, // the speed changed
            });
            bool keptAfterSpeed = !bKept._blinker.IsRunning && diskBKept.Get("enabled", null) == "false" &&
                                  diskBKept.Get(StoppedByKey, null) == StoppedByCapsLock;
            hostBKept.OptionsPanes[0].Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Fast" }, { "capsStops", "false" },
                { "resumeOnRestart", "false" }, { "announce", "true" }, // "Stop when Caps Lock is on" unticked
            });
            probe.Check("WITNESS with the box unticked, a saved Caps Lock off stays off and named through a pane speed change and an untick of 'Stop when Caps Lock is on'",
                keptAfterSpeed && !bKept._blinker.IsRunning && diskBKept.Get("enabled", null) == "false" &&
                diskBKept.Get(StoppedByKey, null) == StoppedByCapsLock &&
                hostBKept.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (off until you pick a speed)");
            bKept.Shutdown();

            b.SetEnabledFromTray(false);                               // an Off over the stop's saved off
            probe.Check("WITNESS a tray Off over a Caps Lock stop's saved off ends the stop, and the tray reads 'Blinking LED: off'",
                !b._blinker.IsRunning && hostB.TrayItems[0].DynamicText() == "Blinking LED: off");
            b.Shutdown();

            // ---- a deliberate Off is saved whatever the box says ----------------------------------------
            foreach (bool ticked in new[] { true, false })
            {
                string box = ticked ? "ticked" : "unticked";
                var seed = new Dictionary<string, string>
                {
                    { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" },
                    { "resumeOnRestart", ticked ? "true" : "false" },
                };

                DesktopAICompanion.ModuleKit.Testing.RecordingHost trayHost;
                BlinkingLedModule tray = StartSession(seed, out trayHost);
                tray.SetEnabledFromTray(false);
                DesktopAICompanion.ModuleKit.Testing.RecordingHost trayNextHost;
                BlinkingLedModule trayNext = StartSession(SavedCopy(trayHost.SettingsFor("blinkingled")), out trayNextHost);
                probe.Check("a tray Off is saved and stays off after a restart, with 'Resume on application restart' " + box,
                    trayHost.SettingsFor("blinkingled").Get("enabled", null) == "false" && !trayNext._blinker.IsRunning);
                trayNext.Shutdown();
                tray.Shutdown();

                DesktopAICompanion.ModuleKit.Testing.RecordingHost paneHost;
                BlinkingLedModule paneOff = StartSession(seed, out paneHost);
                paneHost.OptionsPanes[0].Save(new Dictionary<string, string>
                {
                    { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
                    { "resumeOnRestart", ticked ? "true" : "false" }, { "announce", "true" },
                });
                DesktopAICompanion.ModuleKit.Testing.RecordingHost paneNextHost;
                BlinkingLedModule paneNext = StartSession(SavedCopy(paneHost.SettingsFor("blinkingled")), out paneNextHost);
                probe.Check("a pane Off is saved and stays off after a restart, with 'Resume on application restart' " + box,
                    paneHost.SettingsFor("blinkingled").Get("enabled", null) == "false" && !paneNext._blinker.IsRunning);
                paneNext.Shutdown();
                paneOff.Shutdown();
            }

            // ---- a switch-on refused under Caps Lock follows the same rule ------------------------------
            foreach (bool ticked in new[] { true, false })
            {
                DesktopAICompanion.ModuleKit.Testing.RecordingHost refusedHost;
                BlinkingLedModule refused = StartSession(new Dictionary<string, string>
                {
                    { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
                    { "resumeOnRestart", ticked ? "true" : "false" },
                }, out refusedHost);
                DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings refusedDisk = refusedHost.SettingsFor("blinkingled");
                refused._blinker.CapsLockReader = delegate { return true; };
                savesBefore = refusedDisk.SaveCount;
                saidBefore = refusedHost.SaidLines.Count;
                loggedBefore = refusedHost.LoggedLines.Count;
                refused.SetRateFromTray("Fast");                       // the user switches it on under Caps Lock
                string refusal = LastLoggedMatching(refusedHost.LoggedLines, "caps lock is on: did not start");
                DesktopAICompanion.ModuleKit.Testing.RecordingHost refusedNextHost;
                BlinkingLedModule refusedNext = StartSession(SavedCopy(refusedDisk), out refusedNextHost);
                if (ticked)
                {
                    // One Save(), the pick's own; the refusal adds none, so the pick's on stands.
                    probe.Check("with 'Resume on application restart' ticked, a refused enable under Caps Lock saves nothing beyond the pick itself",
                        !refused._blinker.IsRunning && refusedDisk.SaveCount - savesBefore == 1 &&
                        refusedDisk.Get("enabled", null) == "true" && refusedDisk.Get("rate", null) == "Fast");
                    probe.Check("...and its one log line says it kept the pick, saved no off, and the next start tries again",
                        refusedHost.LoggedLines.Count - loggedBefore == 1 && refusal != null &&
                        refusal.Contains("kept your pick") && refusal.Contains("saving no off") &&
                        refusal.Contains("the next start tries again"));
                    probe.Check("...and it still speaks its one Caps Lock line in place of the ON line",
                        refusedHost.SaidLines.Count - saidBefore == 1 &&
                        refusedHost.SaidLines[refusedHost.SaidLines.Count - 1].StartsWith("Caps Lock is on", StringComparison.Ordinal));
                    probe.Check("...and the tray names the refusal as a Caps Lock stop that resumes at the next start",
                        refusedHost.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");
                    string nextStart = LastLoggedMatching(refusedNextHost.LoggedLines, "starting: ");
                    probe.Check("...and a fresh instance over the same settings starts blinking at the speed picked",
                        refusedNext._blinker.IsRunning && nextStart != null &&
                        nextStart.StartsWith("blinkingled: starting: on, rate Fast,", StringComparison.Ordinal));
                }
                else
                {
                    probe.Check("WITNESS with 'Resume on application restart' unticked, the same refused enable saves the off and a fresh instance stays off",
                        !refused._blinker.IsRunning && refusedDisk.Get("enabled", null) == "false" &&
                        !refusedNext._blinker.IsRunning);
                    probe.Check("with 'Resume on application restart' unticked, a refused enable's saved off keeps its reason: after a restart the tray row still names Caps Lock",
                        refusedNextHost.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (off until you pick a speed)");
                }
                refusedNext.Shutdown();
                refused.Shutdown();
            }

            SelfCheckPaneApplyDuringStop(probe);
            SelfCheckFailedTrayPickDuringStop(probe);
            SelfCheckPaneWritesOnlyItsChanges(probe);
        }

        /// <summary>
        /// N-blinkingled-resume-03: a pane Apply writes only what the user changed on that pane, judged against what the
        /// pane last loaded or last applied, so a tray choice made while the options window is open stands. Each case loads
        /// the pane once, as the window does when it opens, makes the tray choice, then applies from that same view with
        /// one other box changed, and WITNESSes that the changed box is saved. Then the pane's own view: it moves on after
        /// an Apply whose write landed, and stays after one that failed, so the retry still writes.
        /// </summary>
        private static void SelfCheckPaneWritesOnlyItsChanges(SelfTestProbe probe)
        {
            Func<IReadOnlyDictionary<string, string>, Dictionary<string, string>> opened = delegate(IReadOnlyDictionary<string, string> loaded)
            {
                var view = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in loaded) view[kv.Key] = kv.Value;
                return view;
            };

            // A tray Off while the window is open.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost offHost;
            BlinkingLedModule off = StartSession(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" }, { "resumeOnRestart", "true" }, { "announce", "true" },
            }, out offHost);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings offDisk = offHost.SettingsFor("blinkingled");
            OptionsPane offPane = offHost.OptionsPanes[0];
            Dictionary<string, string> offView = opened(offPane.Load());   // the window opens: on, Normal
            off.SetEnabledFromTray(false);                                   // the tray Off, the window still open
            offView["announce"] = "false";                                   // only "announce" changed there
            offPane.Save(offView);
            probe.Check("a tray Off made while the options window is open survives an Apply from that window that changed only another box (N-blinkingled-resume-03)",
                offDisk.Get("enabled", null) == "false" && !off._blinker.IsRunning &&
                offHost.TrayItems[0].DynamicText() == "Blinking LED: off");
            probe.Check("WITNESS ...and the box the user did change on that pane is saved",
                offDisk.Get("announce", null) == "false");
            off.Shutdown();

            // A tray speed pick over a saved off while the window is open: neither the off nor the old speed is put back.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost pickHost;
            BlinkingLedModule pick = StartSession(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" }, { "resumeOnRestart", "true" }, { "announce", "true" },
            }, out pickHost);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings pickDisk = pickHost.SettingsFor("blinkingled");
            OptionsPane pickPane = pickHost.OptionsPanes[0];
            Dictionary<string, string> pickView = opened(pickPane.Load());  // off, Normal
            pick.SetRateFromTray("Fast");                                    // on at Fast, from the tray
            pickView["resumeOnRestart"] = "false";                           // only the box changed there
            pickPane.Save(pickView);
            probe.Check("a tray speed pick made while the options window is open survives an Apply from that window: still on, at the speed picked",
                pick._blinker.IsRunning && pickDisk.Get("enabled", null) == "true" && pickDisk.Get("rate", null) == "Fast" &&
                pickHost.TrayItems[0].DynamicText() == "Blinking LED: on");
            probe.Check("WITNESS ...and the box unticked on that pane is saved",
                pickDisk.Get("resumeOnRestart", null) == "false");
            pick.Shutdown();

            // The final review's probe S9: over an off a Caps Lock stop saved with the box unticked, the pane ticks the box
            // (the off and its reason stay for the session), a tray pick of its speed starts it and clears the reason, and the
            // next Apply from the same, never re-read, view changes only "announce". It saved a plain off; it must not.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost s9Host;
            BlinkingLedModule s9 = StartSession(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" }, { "resumeOnRestart", "false" },
                { StoppedByKey, StoppedByCapsLock },
            }, out s9Host);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s9Disk = s9Host.SettingsFor("blinkingled");
            OptionsPane s9Pane = s9Host.OptionsPanes[0];
            Dictionary<string, string> s9View = opened(s9Pane.Load());
            s9View["resumeOnRestart"] = "true";
            s9Pane.Save(s9View);                                             // the box ticked: the off stays, named
            bool stillOff = !s9._blinker.IsRunning && s9Disk.Get(StoppedByKey, null) == StoppedByCapsLock;
            s9.SetRateFromTray("Normal");                                    // the tray pick starts it
            s9View["announce"] = "false";
            s9Pane.Save(s9View);                                             // the announce-only Apply from the same view
            DesktopAICompanion.ModuleKit.Testing.RecordingHost s9NextHost;
            BlinkingLedModule s9Next = StartSession(SavedCopy(s9Disk), out s9NextHost);
            probe.Check("probe S9: after a tray pick ends a saved Caps Lock off, an announce-only Apply from the still-open pane keeps it on, saves no off, and the next start blinks",
                stillOff && s9._blinker.IsRunning && s9Disk.Get("enabled", null) == "true" &&
                string.IsNullOrEmpty(s9Disk.Get(StoppedByKey, null)) && s9Host.TrayItems[0].DynamicText() == "Blinking LED: on" &&
                s9Next._blinker.IsRunning);
            probe.Check("WITNESS ...and that Apply's own change, announce unticked, is saved",
                s9Disk.Get("announce", null) == "false");
            s9Next.Shutdown();
            s9.Shutdown();

            // The pane's own view moves on after an Apply whose write landed: a speed changed and then changed back, from
            // one pane never re-read, lands both times.
            DesktopAICompanion.ModuleKit.Testing.RecordingHost viewHost;
            BlinkingLedModule viewed = StartSession(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" }, { "resumeOnRestart", "true" }, { "announce", "true" },
            }, out viewHost);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings viewDisk = viewHost.SettingsFor("blinkingled");
            OptionsPane viewPane = viewHost.OptionsPanes[0];
            Dictionary<string, string> view2 = opened(viewPane.Load());
            view2["rate"] = "Fast";
            viewPane.Save(view2);
            bool fastLanded = viewDisk.Get("rate", null) == "Fast";
            view2["rate"] = "Normal";
            viewPane.Save(view2);
            probe.Check("two Applies from one pane both land: a speed changed, then changed back, is saved back (the pane's view moves on after an Apply)",
                fastLanded && viewDisk.Get("rate", null) == "Normal");

            // ...and stays after an Apply whose write failed, so the same Apply again is still a change, and lands.
            viewDisk.FailSaves = true;
            view2["rate"] = "Slow";
            bool failedSave = viewPane.Save(view2);
            viewDisk.FailSaves = false;
            bool retriedSave = viewPane.Save(view2);
            probe.Check("an Apply whose write failed is written by the same Apply sent again (the pane's view stays where it was)",
                !failedSave && retriedSave && viewDisk.Get("rate", null) == "Slow");
            viewed.Shutdown();
        }

        /// <summary>
        /// A pane Apply during a Caps Lock stop with "Resume on application restart" ticked. The pane shows the saved on
        /// during such a stop and every Apply sends every box, so the stop ends only on a changed on/off or speed, or an
        /// unticked "Stop when Caps Lock is on": an
        /// Apply that changed only another box used to restart the blinker, or, with Caps Lock still on, to speak the
        /// refusal and, when it had unticked the box, to save the off. Both ways, with Caps Lock off and on, through the
        /// engine's own reader; the box in the seed is left to its default, ticked.
        /// </summary>
        private static void SelfCheckPaneApplyDuringStop(SelfTestProbe probe)
        {
            DesktopAICompanion.ModuleKit.Testing.RecordingHost host;
            BlinkingLedModule m = StartSession(new Dictionary<string, string>
            {
                // announce off in the seed and on in the first Apply, so a wrongly ended stop would SPEAK its ON line
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" }, { "announce", "false" },
            }, out host);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings disk = host.SettingsFor("blinkingled");
            OptionsPane pane = host.OptionsPanes[0];
            m._blinker.CapsLockReader = delegate { return true; };
            m._blinker.Tick();                                         // a Caps Lock stop: nothing saved
            m._blinker.CapsLockReader = delegate { return false; };    // the user has switched Caps Lock off

            int said = host.SaidLines.Count;
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // only "announce" changed
            });
            probe.Check("during a ticked Caps Lock stop, a pane Apply that changes only another box leaves it stopped, silent and named in the tray (Caps Lock off)",
                !m._blinker.IsRunning && host.SaidLines.Count == said && disk.Get("enabled", null) == "true" &&
                disk.Get("announce", null) == "true" &&
                host.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");

            m._blinker.CapsLockReader = delegate { return true; };     // Caps Lock back on
            m._blinker.Tick();   // the stop again, so this half starts stopped even if the half above wrongly restarted it
            said = host.SaidLines.Count;
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "false" }, { "announce", "true" }, // only the box changed: unticked
            });
            probe.Check("...and with Caps Lock still on, an Apply that only unticks 'Resume on application restart' speaks no refusal and saves no off",
                !m._blinker.IsRunning && host.SaidLines.Count == said && disk.Get("enabled", null) == "true" &&
                disk.Get("resumeOnRestart", null) == "false");

            said = host.SaidLines.Count;
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Fast" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // the speed changed (and the box ticked again)
            });
            probe.Check("WITNESS during a ticked Caps Lock stop, a pane Apply that changes the speed under Caps Lock is refused again, with its one line and no off saved",
                !m._blinker.IsRunning && host.SaidLines.Count - said == 1 &&
                host.SaidLines[host.SaidLines.Count - 1].StartsWith("Caps Lock is on", StringComparison.Ordinal) &&
                disk.Get("enabled", null) == "true");

            m._blinker.CapsLockReader = delegate { return false; };
            said = host.SaidLines.Count;
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Slow" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // the speed changed, Caps Lock off
            });
            probe.Check("WITNESS ...and with Caps Lock off, an Apply that changes the speed ends the stop and starts it, with the ON line",
                m._blinker.IsRunning && host.SaidLines.Count - said == 1 &&
                host.SaidLines[host.SaidLines.Count - 1] == "Keeping the lights on for you.");

            // A changed on/off ends it too: Off from the pane during the stop is the user's Off, saved and shown as one.
            m._blinker.CapsLockReader = delegate { return true; };
            m._blinker.Tick();                                         // stopped by Caps Lock again
            m._blinker.CapsLockReader = delegate { return false; };
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Slow" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // "Blink the Scroll Lock light" unticked
            });
            probe.Check("during a ticked Caps Lock stop, a pane Apply that unticks 'Blink the Scroll Lock light' ends the stop and saves the off: the row reads 'Blinking LED: off'",
                !m._blinker.IsRunning && disk.Get("enabled", null) == "false" &&
                host.TrayItems[0].DynamicText() == "Blinking LED: off");

            // Unticking "Stop when Caps Lock is on" ends it too (the fix round's second pass): it is the user saying Caps
            // Lock should not stop it, so it starts, with Caps Lock still on, and the row stops blaming Caps Lock. Switched
            // back on first with Caps Lock off, then stopped again by a tick.
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Slow" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },
            });
            m._blinker.CapsLockReader = delegate { return true; };
            m._blinker.Tick();                                         // stopped by Caps Lock, which stays on
            said = host.SaidLines.Count;
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Slow" }, { "capsStops", "false" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // only "Stop when Caps Lock is on" changed: unticked
            });
            probe.Check("during a ticked Caps Lock stop, a pane Apply that unticks 'Stop when Caps Lock is on' ends the stop and starts it with Caps Lock still on: the row reads 'Blinking LED: on'",
                m._blinker.IsRunning && disk.Get("capsStops", null) == "false" && host.SaidLines.Count - said == 1 &&
                host.SaidLines[host.SaidLines.Count - 1] == "Keeping the lights on for you." &&
                host.TrayItems[0].DynamicText() == "Blinking LED: on");
            m.Shutdown();
        }

        /// <summary>
        /// A tray pick whose write fails during a Caps Lock stop with the box ticked. The disk still reads on, so a click
        /// that reached ApplyState(true) ended the stop and started the blinker from the disk while its log line said the
        /// live state was unchanged; it now returns first. Caps Lock is read off by the time of the clicks, so reaching
        /// ApplyState would start it.
        /// </summary>
        private static void SelfCheckFailedTrayPickDuringStop(SelfTestProbe probe)
        {
            DesktopAICompanion.ModuleKit.Testing.RecordingHost host;
            BlinkingLedModule m = StartSession(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Normal" }, { "capsStops", "true" },
            }, out host);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings disk = host.SettingsFor("blinkingled");
            m._blinker.CapsLockReader = delegate { return true; };
            m._blinker.Tick();                                         // stopped; the disk still reads on
            m._blinker.CapsLockReader = delegate { return false; };
            disk.FailSaves = true;

            int logged = host.LoggedLines.Count;
            m.SetRateFromTray("Fast");
            probe.Check("during a ticked Caps Lock stop, a tray speed pick whose write fails leaves it stopped and named, as its one line says",
                !m._blinker.IsRunning && host.LoggedLines.Count - logged == 1 &&
                CountLogged(host.LoggedLines, "tray pick not persisted (rate Fast, on), so the live state is unchanged") == 1 &&
                host.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");

            logged = host.LoggedLines.Count;
            m.SetEnabledFromTray(false);
            probe.Check("...and so does a tray Off whose write fails, which used to start the blinker from the disk's on",
                !m._blinker.IsRunning && host.LoggedLines.Count - logged == 1 &&
                CountLogged(host.LoggedLines, "tray pick not persisted (off), so the live state is unchanged") == 1);

            // The pane's Save follows the same rule (the fix round's second pass): a pane Off and a pane speed change whose
            // write fails during the stop leave it stopped and named, and say nothing. The pane itself tells the user the
            // save failed (its Save answers false, which the options window turns into "These settings could not be
            // saved."), so no line is logged for it here. Announce is on by default, so a wrongly ended stop would speak.
            OptionsPane pane = host.OptionsPanes[0];
            int said = host.SaidLines.Count;
            bool paneOffSaved = pane.Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // "Blink the Scroll Lock light" unticked
            });
            probe.Check("during a ticked Caps Lock stop, a pane Off whose write fails leaves it stopped, silent and named, which used to start the blinker from the disk's on",
                !paneOffSaved && !m._blinker.IsRunning && host.SaidLines.Count == said && disk.Get("enabled", null) == "true" &&
                host.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");
            bool paneSpeedSaved = pane.Save(new Dictionary<string, string>
            {
                { "enabled", "true" }, { "rate", "Fast" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // the speed changed
            });
            probe.Check("...and so does a pane speed change whose write fails",
                !paneSpeedSaved && !m._blinker.IsRunning && host.SaidLines.Count == said && disk.Get("rate", null) == "Normal" &&
                host.TrayItems[0].DynamicText() == "Blinking LED: stopped by Caps Lock (resumes at next start)");

            disk.FailSaves = false;
            m.SetRateFromTray("Fast");
            probe.Check("WITNESS ...and the same speed pick with a write that lands ends the stop and starts it",
                m._blinker.IsRunning && disk.Get("rate", null) == "Fast");
            m.Shutdown();
        }

        /// <summary>One companion start for <see cref="SelfCheckCapsLockResume"/>: a fresh host whose saved settings
        /// are <paramref name="saved"/>, and a fresh module Init'd over them. Init's ApplyState runs with the real
        /// readers, as a start does (reads only); both readers and the keypress are pinned straight after, so
        /// nothing later in the session reads or presses a real key. <paramref name="failSaves"/> makes every write
        /// from Init on fail, the way a locked or read-only settings.json does.</summary>
        private static BlinkingLedModule StartSession(IEnumerable<KeyValuePair<string, string>> saved,
                                                      out DesktopAICompanion.ModuleKit.Testing.RecordingHost host,
                                                      bool failSaves = false)
        {
            host = new DesktopAICompanion.ModuleKit.Testing.RecordingHost();
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings s = host.SettingsFor("blinkingled");
            foreach (KeyValuePair<string, string> kv in saved) s.Set(kv.Key, kv.Value);
            s.Save();
            s.FailSaves = failSaves;
            var module = new BlinkingLedModule();
            module.Init(host);
            module._blinker.ScrollLockReader = delegate { return false; };
            module._blinker.CapsLockReader = delegate { return false; };
            module._blinker.KeypressSender = AcceptedKeypress;
            return module;
        }

        /// <summary>Ticks "Resume on application restart" through <paramref name="pane"/> with an Apply that changes nothing
        /// else, over the settings the unticked Caps Lock stop in <see cref="SelfCheckCapsLockResume"/> saved (off, rate
        /// Normal, "Stop when Caps Lock is on" ticked, announce at its default, on), which keeps the off and its reason for
        /// the session.</summary>
        private static void TickResumeOverSavedOff(OptionsPane pane)
        {
            pane.Save(new Dictionary<string, string>
            {
                { "enabled", "false" }, { "rate", "Normal" }, { "capsStops", "true" },
                { "resumeOnRestart", "true" }, { "announce", "true" },  // only the box changed: ticked
            });
        }

        /// <summary>Two Applies from ONE options pane that is never re-read between them, on a fresh session over
        /// <paramref name="saved"/>, an off a Caps Lock stop saved with "Resume on application restart" unticked: the first
        /// sends what the pane loaded with the box ticked and <paramref name="firstKey"/> changed, the second sends the same
        /// pane again with <paramref name="secondKey"/> changed too, which is what the user's next Apply from that window
        /// sends. True when, after each Apply, nothing blinks and the disk still holds that off and its reason, when neither
        /// Apply spoke the ON or OFF line, and when the tray row then says the off resumes at the next start.
        /// <paramref name="seen"/> is what was found after each, for the check's label. <paramref name="capsLockOn"/> pins the
        /// Caps Lock reader on for the whole session.</summary>
        private static bool UnrefreshedPaneKeepsSavedCapsLockOff(Dictionary<string, string> saved, bool capsLockOn,
                                                                 string firstKey, string firstValue,
                                                                 string secondKey, string secondValue, out string seen)
        {
            DesktopAICompanion.ModuleKit.Testing.RecordingHost host;
            BlinkingLedModule m = StartSession(saved, out host);
            DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings disk = host.SettingsFor("blinkingled");
            OptionsPane pane = host.OptionsPanes[0];
            if (capsLockOn) m._blinker.CapsLockReader = delegate { return true; };
            var shown = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in pane.Load()) shown[kv.Key] = kv.Value;   // loaded once, never again
            bool kept = disk.Get("enabled", null) == "false" && disk.Get(StoppedByKey, null) == StoppedByCapsLock &&
                        shown["enabled"] == "false" && shown["resumeOnRestart"] == "false" && !m._blinker.IsRunning;
            int saidBefore = host.SaidLines.Count;
            var after = new List<string>();
            shown["resumeOnRestart"] = "true";
            shown[firstKey] = firstValue;
            for (int apply = 1; apply <= 2; apply++)
            {
                if (apply == 2) shown[secondKey] = secondValue;
                pane.Save(shown);
                bool asSaved = !m._blinker.IsRunning && disk.Get("enabled", null) == "false" &&
                               disk.Get(StoppedByKey, null) == StoppedByCapsLock;
                kept &= asSaved;
                after.Add("after Apply " + apply.ToString(CultureInfo.InvariantCulture) + ": running=" +
                          (m._blinker.IsRunning ? "yes" : "no") + " enabled=" + (disk.Get("enabled", null) ?? "(none)") +
                          " stoppedBy=" + (disk.Get(StoppedByKey, null) ?? "(none)"));
            }
            int onOrOffLines = 0;
            List<string> said = host.SaidLines;   // a copy, taken once
            for (int i = saidBefore; i < said.Count; i++)
                if (said[i] == "Keeping the lights on for you." || said[i] == "Blinking off. You are on your own now.")
                    onOrOffLines++;
            string row = host.TrayItems[0].DynamicText();
            kept &= onOrOffLines == 0 && row == "Blinking LED: stopped by Caps Lock (resumes at next start)";
            seen = string.Join("; ", after) + "; ON/OFF lines said: " + onOrOffLines.ToString(CultureInfo.InvariantCulture) +
                   "; row: " + row;
            m.Shutdown();
            return kept;
        }

        /// <summary>What a restart reads: a copy of the values the fake shows, which after every Save(), failed or
        /// not, is the disk.</summary>
        private static Dictionary<string, string> SavedCopy(DesktopAICompanion.ModuleKit.Testing.FakeModuleSettings disk)
        {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in disk.Values) copy[kv.Key] = kv.Value;
            return copy;
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
