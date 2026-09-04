using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopAICompanion.Ai;

namespace DesktopAICompanion
{
    /// <summary>
    /// StartUp class. This class will initialize the entire application and define some constants.
    /// </summary>
    public sealed class StartUp : IDisposable, DesktopAICompanion.Options.ICompanionRuntime
    {
        /// <summary>
        /// Maximal sheeps (too much sheeps will cover too much the screen and would not be nice to see).
        /// </summary>
        public const int MAX_SHEEPS = 16;

        /// <summary>
        /// DEBUG TYPE. If you press "SHIFT" by starting the application, a debug Window will appear.
        /// </summary>
        public enum DEBUG_TYPE
        {
            /// <summary>
            /// Only info, to show what is happening.
            /// </summary>
            info = 1,
            /// <summary>
            /// Something important happened or something that was not expected.
            /// </summary>
            warning = 2,
            /// <summary>
            /// An error is occurred. The application need to do something that was not expected.
            /// </summary>
            error = 3,
        }

        /// <summary>
        /// A timer to allow some times to the sheeps to die, before the application will close definitively.
        /// </summary>
        private readonly System.Windows.Forms.Timer timer1 =
            new System.Windows.Forms.Timer();

        /// <summary>
        /// Each sheep is in a different form.
        /// </summary>
        readonly FormCompanion[] sheeps = new FormCompanion[MAX_SHEEPS];
        readonly RetiringValueRegistry<FormCompanion> retiringPets =
            new RetiringValueRegistry<FormCompanion>();

        /// <summary>
        /// Debug window, used only if SHIFT was pressed by starting the application.
        /// </summary>
        static FormDebug debug = null;
        static readonly object debugLock = new object();

        /// <summary>
        /// Number of currently active sheeps.
        /// </summary>
        int iSheeps = 0;

        /// <summary>
        /// The XML file where all animations are defined.
        /// </summary>
        Xml xml;

        /// <summary>
        /// Class of the animations. All animations are stored there.
        /// </summary>
        Animations animations;

        // Extra pet types spawned "alongside" the active one (id -> loaded Xml/Animations + refcount).
        // The active pair above (xml/animations) is the default type and is never in the registry; only
        // extra types are reference-counted and disposed when their last pet closes. UI-thread only.
        readonly CompanionTypeRegistry registry = new CompanionTypeRegistry();
        readonly Dictionary<FormCompanion, CompanionTypeRegistry.Entry> petEntries =
            new Dictionary<FormCompanion, CompanionTypeRegistry.Entry>();
        AudioOutput audioOutput;   // host-owned audio output (B1): plays animation sounds, later TTS
        // Startup spawn plan: the persisted pet mix flattened to one id per pet, spawned one-per-tick.
        List<string> spawnPlan;
        int spawnPlanIndex;

        /// <summary>
        /// Process Icon. The tray icon on the taskbar.
        /// </summary>
        readonly ProcessIcon pi;

        bool isRealoadingSettings = false;

        bool disposed;


        // Poke-escalation state (right-clicking the sheep). Thresholds are tunable; the sass lines
        // live in PokeReactions so more can be slotted in later.
        //
        // PER PET, not per app. This state used to be three shared fields, which was invisible while every
        // reaction was broadcast to every pet: poke Pearl three times and then Rick once, and Rick answered at
        // the sass tier because he inherited Pearl's count. Once the sass goes only to the pet you clicked
        // that becomes plainly wrong, and the 12s rich-reaction cooldown was worse -- poking four pets in turn
        // produced one reaction and three silences, which reads as broken rather than as a cooldown.
        // A weak table so a removed pet's state is collected with it and nothing has to be cleaned up.
        private sealed class PokeState
        {
            public int Count;
            public DateTime LastPokeUtc = DateTime.MinValue;
            public DateTime LastReactionUtc = DateTime.MinValue;
        }
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FormCompanion, PokeState> pokeStates =
            new System.Runtime.CompilerServices.ConditionalWeakTable<FormCompanion, PokeState>();
        const double PokeResetSeconds = 7.0;   // a pause this long starts a fresh poke session
        const int PokeIgnoreFrom = 3;          // pokes 3-4: ignore (turn away, no words)
        const int PokeSassFrom   = 5;          // pokes 5-11: verbal sass
        const int PokeEscapeAt   = 12;         // poke 12: bathtub escape

        // Poke 1 of a session offers a "rich" reaction (an AI quip / a fortune / nothing) through the
        // arbitrated poke-responder chain. Its cooldown is deliberately INDEPENDENT of PokeResetSeconds:
        // the 7s reset governs the sass ladder, while this longer window stops a rich reaction from firing
        // on every brief pause. A poke-1 inside the cooldown simply stays silent (the sass ladder still
        // advances normally underneath), so the pet doesn't become a quip vending machine.
        const double PokeReactionCooldownSeconds = 12.0;

        /// <summary>Polls the pet's fall on launch and speaks a fortune once it has settled (see below).</summary>
        System.Windows.Forms.Timer landTimer;
        int landPrevY = int.MinValue;   // pet's last Y; it's falling only while Y increases
        int landStable;                 // consecutive polls with no downward movement
        int landTicks;                  // total polls (for the min-delay + safety cap)

        /// <summary>Random-drop timer: periodically speaks a fortune at a randomized interval
        /// (the Fortunes module owns the fortune itself). Null when the feature is disabled.</summary>
        System.Windows.Forms.Timer dropTimer;
        EventHandler dropTimerHandler;

        /// <summary>Random source for the jittered random-drop interval.</summary>
        readonly Random aiRand = new Random();

        /// <summary>Drives the monthly module-update check. Null when no module ever loaded.</summary>
        System.Windows.Forms.Timer moduleUpdateTimer;
        EventHandler moduleUpdateTimerHandler;
        bool moduleUpdateCheckRunning;

        /// <summary>Set while <see cref="ReloadPetType"/> is swapping one type's pets, so the respawns are
        /// not announced to modules as new arrivals. See the note in AddSheepCore.</summary>
        bool reloadInProgress;
        private const int ModuleUpdateFirstPassMilliseconds = 2 * 60 * 1000;        // let launch settle first
        private const int ModuleUpdateCadenceMilliseconds = 6 * 60 * 60 * 1000;     // then notice a month rolling over

        /// <summary>
        /// Constructor. Called when application is started.
        /// </summary>
        /// <param name="processIcon">ProcessIcon class, to change icon when a new pet is selected.</param>
        // Plugin host bridge + module loader (S1). Modules load in the ctor and receive lifecycle
        // events raised from the existing hook points below; the current features stay in place alongside.
        internal DesktopAICompanion.Plugins.CompanionHost Host { get; private set; }
        private DesktopAICompanion.Plugins.ModuleHost moduleHost;
        /// <summary>Currently loaded modules (Modules pane display), or empty before/without a host.</summary>
        internal IReadOnlyList<DesktopAICompanion.Modules.IModule> LoadedModules
        {
            get { return moduleHost != null ? moduleHost.Modules : Array.Empty<DesktopAICompanion.Modules.IModule>(); }
        }

        /// <summary>Module folders that did not end up running, so the Modules pane can say so instead of
        /// showing them as installed-and-waiting forever.</summary>
        internal IReadOnlyList<DesktopAICompanion.Plugins.ModuleLoadFailure> ModuleFailures
        {
            get
            {
                return moduleHost != null
                    ? moduleHost.Failures
                    : Array.Empty<DesktopAICompanion.Plugins.ModuleLoadFailure>();
            }
        }

        public StartUp(ProcessIcon processIcon)
        {
            pi = processIcon ?? throw new ArgumentNullException("processIcon");

                // If SHIFT key was pressed, open Debug window
            Keys ks = Control.ModifierKeys;
            if (ks == Keys.Shift)
            {
                var debugWindow = new FormDebug();
                debugWindow.FormClosed += delegate
                {
                    lock (debugLock)
                        if (ReferenceEquals(debug, debugWindow))
                            debug = null;
                };
                lock (debugLock) debug = debugWindow;
                debugWindow.Show();
                AddDebugInfo(DEBUG_TYPE.info, "debug window started");
            }
            
            string candidate = Program.InitialXmlOverride;
            bool externalCandidate = !string.IsNullOrWhiteSpace(candidate);
            if (!externalCandidate) candidate = Program.MyData.GetXml();
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = Properties.Resources.animations;

            string error;
            // Key the active/default pet by its real id (persisted activePetId) so per-pet size/sound follow
            // the actual pet, not the "" active-slot placeholder. Defaults to the built-in pet.
            string activeId = Program.MyData != null ? Program.MyData.GetActivePetId() : CompanionCatalog.BuiltInPetId;
            double activeFactor = Program.MyData.GetEffectivePetScaleFactorD(activeId);
            if (!TryStageRuntime(candidate, activeFactor, out xml, out animations, out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "Configured pet rejected: " + error);
                candidate = Properties.Resources.animations;
                if (!TryStageRuntime(candidate, activeFactor, out xml, out animations, out error))
                    throw new InvalidDataException("The built-in pet failed validation: " + error);
            }
            animations.PetTypeId = activeId;
            animations.Activate();
            if (!Program.MyData.SetXml(
                    candidate,
                    externalCandidate ? "external" : ""))
                AddDebugInfo(
                    DEBUG_TYPE.warning,
                    "The active pet could not be persisted; the previous pet will return next launch.");

                // Set animation icon
            ApplyTrayIcon(xml);

                // Wait 1 second, before starting first animation
            timer1.Tag = "A";
            timer1.Tick += new EventHandler(Timer1_Tick);
            timer1.Interval = 1000;
            timer1.Enabled = true;

            Program.MyData.ListenOnXMLChanged(XmlFileChanged);
            Program.MyData.ListenOnOptionsChanged(OptionFileChanged);

            InitDropTriggers();

            // Plugin host: load modules from <baseDir>\modules; each receives lifecycle events + host
            // services. A load/init failure is isolated so a bad module never stops the pet from starting.
            Host = new DesktopAICompanion.Plugins.CompanionHost(this);
            moduleHost = new DesktopAICompanion.Plugins.ModuleHost();
            // B1: play the engine-selected animation sound through the host-owned AudioOutput directly.
            // The base owns playback now (Option B); the S2 module-era AnimationStarted routing is retired,
            // so the Sound module is inert (removed in B4). Volume comes from the user's setting. Cleared in
            // Dispose so a torn-down output is never held.
            audioOutput = new AudioOutput();
            audioOutput.SetDevice(Program.MyData != null ? Program.MyData.GetAudioDeviceId() : "");
            Animations.SoundSink = (petTypeId, animId, data, loop) =>
            {
                AudioOutput a = audioOutput;
                LocalData d = Program.MyData;
                // Global "pet sounds" master switch first, then the per-pet mute (B3).
                if (a != null && d != null && d.GetPetSoundsEnabled() && d.IsPetSoundEnabled(petTypeId))
                    a.Play(data, loop, d.GetVolume());
            };
            try
            {
                string modulesDir = System.IO.Path.Combine(AppContext.BaseDirectory, "modules");
                // Finish any Uninstall from the Modules pane BEFORE loading -- its target was left on disk
                // because its DLL was still locked by the process that asked to remove it; this fresh
                // process never loads it, so it is free to delete now rather than re-lock it.
                DesktopAICompanion.Plugins.PendingModuleRemovals.ProcessPending(
                    modulesDir, msg => AddDebugInfo(DEBUG_TYPE.info, "[module] " + msg));
                // Then finish any Update the same way, for the same locking reason. Order matters: removals
                // first, so an uninstall that raced an update wins rather than the staged copy resurrecting
                // the module the user just removed.
                DesktopAICompanion.Plugins.PendingModuleUpdates.ProcessPending(
                    modulesDir, msg => AddDebugInfo(DEBUG_TYPE.info, "[module] " + msg));
                int loadedModules = moduleHost.LoadFrom(modulesDir, Host, msg => AddDebugInfo(DEBUG_TYPE.info, "[module] " + msg));
                if (loadedModules > 0) AddDebugInfo(DEBUG_TYPE.info, loadedModules + " module(s) loaded");
            }
            catch (Exception moduleEx) { AddDebugInfo(DEBUG_TYPE.warning, "module host init failed: " + moduleEx.Message); }

            // All three arm OUTSIDE the module try/catch. ArmModuleUpdateCheck used to sit inside it and
            // behind `loadedModules > 0`, which meant a module-host failure silently took the update check
            // with it. The pet check must run with zero modules installed, so it could never have lived
            // there at all. None of them are awaited: a launch must not wait on the network, and a network
            // failure must not read as a module problem.
            ArmAppUpdateCheck();
            ArmModuleUpdateCheck();
            ArmPetUpdateCheck();
        }

        /// <summary>
        /// Ask once, in the background, whether a newer app version exists. Deliberately NOT awaited and
        /// deliberately outside the module try/catch above: a launch must not be delayed, and a network
        /// failure here must not look like a module problem.
        ///
        /// The throttle lives in AppUpdateCheck.ShouldCheck (at most once a day), so calling this on every
        /// launch is cheap. Notify-only: the outcome is a cached version string the Preferences footer may
        /// render as a link.
        /// </summary>
        private void ArmAppUpdateCheck()
        {
            try
            {
                LocalData data = Program.MyData;
                if (data == null || !data.GetAppUpdateCheck()) return;
                string current = System.Windows.Forms.Application.ProductVersion;
                _ = AppUpdateCheck.MaybeCheckAsync(data, current, System.Threading.CancellationToken.None);
            }
            catch { }
        }

        private static bool TryStageRuntime(
            string source,
            double scaleFactor,
            out Xml stagedXml,
            out Animations stagedAnimations,
            out string error)
        {
            stagedXml = new Xml(scaleFactor);
            stagedAnimations = null;
            error = null;
            try
            {
                if (!stagedXml.TryReadXml(source, out error))
                    throw new InvalidDataException(error);

                stagedAnimations = new Animations(stagedXml);
                stagedXml.LoadAnimations(stagedAnimations);
                if (stagedAnimations.SheepAnimations.Count == 0 ||
                    stagedAnimations.SheepSpawn.Count == 0)
                    throw new InvalidDataException(
                        "Pet XML did not produce a runnable animation and spawn set.");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (stagedAnimations != null) stagedAnimations.Dispose();
                stagedXml.Dispose();
                stagedAnimations = null;
                stagedXml = null;
                return false;
            }
        }

        private void ApplyTrayIcon(Xml source)
        {
            if (source == null ||
                source.AnimationXML == null ||
                source.bitmapIcon == null)
                return;
            using (var iconCopy =
                new MemoryStream(source.bitmapIcon.ToArray(), false))
            {
                pi.SetIcon(
                    iconCopy,
                    source.AnimationXML.Header.Petname,
                    source.AnimationXML.Header.Author,
                    source.AnimationXML.Header.Title,
                    source.AnimationXML.Header.Version,
                    source.AnimationXML.Header.Info);
            }
        }

        internal bool RefreshTrayIconForResourceChurn()
        {
            if (!Program.ResourceChurnSelfTestActive ||
                disposed ||
                xml == null)
                return false;
            ApplyTrayIcon(xml);
            return true;
        }


        private void XmlFileChanged(object source, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            LoadNewXMLFromString(Program.MyData.LoadXML());
        }

        private void OptionFileChanged(object source, FileSystemEventArgs e)
        {
            if (isRealoadingSettings) return;
            isRealoadingSettings = true;
            Thread.Sleep(1000);
            Program.MyData.LoadSettings();
            Thread.Sleep(200);
            isRealoadingSettings = false;
        }

        /// <summary>
        /// Dispose class -> used to dispose xml class
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // Shut modules down first (unsubscribe + unload their load contexts) before the host tears down.
            Animations.SoundSink = null;   // stop routing engine sounds before the output goes away
            if (Host != null) Host.RaiseShutdown();
            if (moduleHost != null) { moduleHost.Dispose(); moduleHost = null; }
            // AFTER the modules, so a module's Shutdown can still stop or flush what it was playing. It used
            // to be disposed first, which meant a module calling StopSound during teardown was talking to a
            // disposed, then nulled, output. Safe in this order only because PlaySound takes a byte[] the host
            // decodes into its OWN buffer: no module-owned ISampleProvider is ever in the mixer, so unloading
            // a load context cannot pull code out from under the audio thread.
            if (audioOutput != null) { audioOutput.Dispose(); audioOutput = null; }   // B1 host-owned output

            timer1.Stop();
            timer1.Tick -= Timer1_Tick;
            if (dropTimer != null)
            {
                dropTimer.Stop();
                if (dropTimerHandler != null)
                    dropTimer.Tick -= dropTimerHandler;
                dropTimer.Dispose();
                dropTimer = null;
                dropTimerHandler = null;
            }
            if (landTimer != null)
            {
                landTimer.Stop();
                landTimer.Tick -= LandTimer_Tick;
                landTimer.Dispose();
                landTimer = null;
            }
            if (moduleUpdateTimer != null)
            {
                moduleUpdateTimer.Stop();
                if (moduleUpdateTimerHandler != null)
                    moduleUpdateTimer.Tick -= moduleUpdateTimerHandler;
                moduleUpdateTimer.Dispose();
                moduleUpdateTimer = null;
                moduleUpdateTimerHandler = null;
            }

            CloseAllPetsImmediate();
            registry.DisposeAll();   // extra pet types (their FormClosed already released most)
            if (animations != null) { animations.Dispose(); animations = null; }
            if (xml != null) { xml.Dispose(); xml = null; }
            FormDebug debugWindow;
            lock (debugLock)
            {
                debugWindow = debug;
                debug = null;
            }
            if (debugWindow != null && !debugWindow.IsDisposed)
            {
                try { debugWindow.Close(); } catch { }
            }
            timer1.Dispose();
            pi.Dispose();
        }

        
            /// <summary>
            /// Calling this function will add another sheep on the desktop, if MAX_SHEEP was not reached.
            /// </summary>
        public void AddSheep()
        {
            AddSheepCore(xml, animations, null);
        }

        /// <summary>
        /// Add a pet of a specific type by id, alongside any pets already on screen. A null/empty id
        /// (or the active pet) spawns the active/default type; a folder id loads that pet type on demand
        /// (reference-counted, disposed when its last pet closes). Returns false if the type could not
        /// be loaded or the max-pets cap was reached.
        /// </summary>
        public bool AddSheep(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return AddSheepCore(xml, animations, null) != null;
            }

            CompanionTypeRegistry.Entry entry = ResolveExtraType(id);
            if (entry == null) return false;

            FormCompanion spawned = AddSheepCore(entry.Xml, entry.Animations, entry);
            if (spawned == null)
            {
                registry.DropIfUnused(entry);   // slot was full: don't leak an unspawned type
                return false;
            }
            return true;
        }

        // Shared spawn: create+show a pet of the given (Xml, Animations). When entry != null the pet is
        // an "extra" type, so its use is reference-counted and released on FormClosed. Returns the new
        // pet, or null when the max-pets cap is reached.
        //
        // A TRANSIENT (preview) pet is not announced through CompanionSpawned. Modules react to that event with
        // user-visible behavior -- Fortunes speaks a welcome, the AI brain resets its tracked pet -- and an
        // author re-previewing their XML twenty times should not fire twenty welcomes. A preview belongs to
        // the module that asked for it, which already holds the handle it needs.
        private FormCompanion AddSheepCore(Xml petXml, Animations petAnimations, CompanionTypeRegistry.Entry entry)
        {
            if (iSheeps >= MAX_SHEEPS)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "max PETs reached");
                return null;
            }

            FormCompanion newSheep = CreateAndInitializeOwnedPet(
                delegate { return new FormCompanion(petAnimations, petXml); },
                delegate(FormCompanion pet)
                {
                    pet.Show(petXml.spriteWidth, petXml.spriteHeight);
                    pet.Play(true);
                });
            sheeps[iSheeps] = newSheep;
            iSheeps++;

            if (entry != null)
            {
                petEntries[newSheep] = entry;
                registry.Increment(entry);
                newSheep.FormClosed += ExtraPet_FormClosed;
            }

            AddDebugInfo(DEBUG_TYPE.info, "new pet...");
            AddDebugInfo(DEBUG_TYPE.info, petXml.SpriteCount.ToString() + " shared frames ready");
            // Also suppressed during a RELOAD. A reload closes and respawns the pets of one type, and every
            // module reacts to CompanionSpawned with something the user sees -- Fortunes speaks a welcome, the AI
            // brain resets its tracked pet -- so updating a skin with four copies on screen would fire four
            // welcomes for pets that never went away as far as the user is concerned.
            if (Host != null && (entry == null || !entry.IsTransient) && !reloadInProgress)
                Host.RaiseCompanionSpawned(newSheep);
            return newSheep;
        }

        /// <summary>The number of preview pets currently on screen, capped so a module that forgets to
        /// remove them cannot starve the 16 real slots.</summary>
        private const int MAX_PREVIEW_PETS = 4;

        /// <summary>
        /// Spawn a TRANSIENT preview pet from an arbitrary animations.xml string, for an authoring module
        /// that wants to show the user their pet running on the real desktop before installing it. Returns
        /// the new pet, or null with a reason.
        ///
        /// This is deliberately NOT <see cref="LoadNewXMLFromString"/>. That verb is "use this pet": it
        /// writes the XML into settings.json, kills every pet on screen, wipes the type registry and
        /// re-persists the mix. Using it for a preview would permanently replace the user's pet with a
        /// draft. Here nothing is persisted and nothing existing is disturbed: the XML goes through the
        /// same validation as an installed pet (TryStageRuntime -> CompanionXmlValidator, so a preview is not a
        /// hole in the pet-XML defences), gets registered under a synthetic transient id, and stays out of
        /// the on-screen mix -- which is what keeps it out of settings.json and out of the tray.
        ///
        /// Animations.Activate() is deliberately not called: only the active/default type owns that static.
        /// </summary>
        internal FormCompanion SpawnPreviewPet(string animationsXml, out string error)
        {
            error = null;
            if (disposed) { error = "The pet runtime is shutting down."; return null; }
            if (string.IsNullOrWhiteSpace(animationsXml)) { error = "No pet XML was supplied."; return null; }
            if (iSheeps >= MAX_SHEEPS) { error = "The maximum number of pets is already on screen."; return null; }

            int previews = 0;
            for (int i = 0; i < iSheeps; i++)
                if (IsTransientPet(sheeps[i])) previews++;
            if (previews >= MAX_PREVIEW_PETS)
            {
                error = "Too many preview pets are already on screen (" + MAX_PREVIEW_PETS + ").";
                return null;
            }

            Xml stagedXml;
            Animations stagedAnimations;
            // The scale of the active pet, so a preview looks the way the pet would once installed.
            double factor = Program.MyData != null ? Program.MyData.GetEffectivePetScaleFactorD("") : 1.0;
            if (!TryStageRuntime(animationsXml, factor, out stagedXml, out stagedAnimations, out error))
                return null;

            // A guid id per spawn: unique, so it can never collide with (or displace) an installed type, and
            // it carries a ':' which AppSettingsDocument.IsAcceptablePetId rejects -- a second line of
            // defence if one of these ever leaked toward the persisted mix.
            string previewId = "preview:" + Guid.NewGuid().ToString("N");
            stagedAnimations.PetTypeId = previewId;
            CompanionTypeRegistry.Entry entry = registry.Add(previewId, stagedXml, stagedAnimations, true);

            FormCompanion spawned = AddSheepCore(entry.Xml, entry.Animations, entry);
            if (spawned == null)
            {
                registry.DropIfUnused(entry);
                error = "The pet could not be shown.";
                return null;
            }
            AddDebugInfo(DEBUG_TYPE.info, "preview pet spawned (" + previewId + ")");
            return spawned;
        }

        /// <summary>What <see cref="ReloadPetType"/> did, so the caller can say something accurate.</summary>
        internal enum CompanionReloadOutcome
        {
            /// <summary>No pets of that type were on screen. The next spawn will read the new file.</summary>
            NothingOnScreen,
            /// <summary>The pets were closed and respawned on the new definition.</summary>
            Reloaded,
            /// <summary>This is the ACTIVE pet, whose live definition lives in settings.json rather than the
            /// library folder, so only a restart picks the new file up.</summary>
            NeedsRestart,
            /// <summary>Someone is dragging a pet, or the new file did not stage. Nothing was touched.</summary>
            Deferred,
        }

        /// <summary>
        /// Swap the pets of one type onto a freshly-read definition, because its animations.xml just changed
        /// on disk.
        ///
        /// The obvious implementation is what the tray already does -- RemoveOnePet then AddSheep -- and it
        /// silently does nothing. KillSheep frees the sheeps[] slot immediately but registry.Decrement only
        /// runs on FormClosed, which waits for the kill animation, so an immediate re-add still finds
        /// RefCount > 0, ResolveExtraType hits the CACHED parse, and the old skin comes back. The fix is one
        /// call: CompanionTypeRegistry.Add displaces the cached entry without freeing a pair that live pets are
        /// still borrowing, which is exactly the case it was written for.
        ///
        /// The ACTIVE pet cannot be done this way at all. Its live definition comes from settings.json via
        /// StartUp.xml/animations, not from the library folder, so removing and re-adding re-uses the
        /// in-memory copy and changes nothing. The only in-process path that re-stages it is
        /// LoadNewXMLFromString, which also closes every pet of every type, wipes the registry and resets
        /// the mix to autostart copies -- a whole-desktop teardown to refresh one skin, racing the autostart
        /// timer it re-arms. Restarting is the same visible outcome with none of that, and the app already
        /// asks users to restart for a module update.
        ///
        /// Order is kill-then-spawn so a reload at MAX_SHEEPS cannot fail half way and drop a pet.
        /// </summary>
        /// <param name="id">Pet type id. "" means the active/default pet.</param>
        /// <param name="reloaded">How many pets were swapped.</param>
        internal CompanionReloadOutcome ReloadPetType(string id, out int reloaded, out string error)
        {
            reloaded = 0;
            error = null;
            if (disposed) { error = "The pet runtime is shutting down."; return CompanionReloadOutcome.Deferred; }

            string target = id ?? "";
            if (target.Length == 0) return CompanionReloadOutcome.NeedsRestart;

            // How many of this type are up, counted the same way RemoveOnePet finds them.
            int live = 0;
            for (int i = 0; i < iSheeps; i++)
            {
                FormCompanion pet = sheeps[i];
                if (pet == null) continue;
                CompanionTypeRegistry.Entry held;
                string petId = petEntries.TryGetValue(pet, out held) ? (held.Id ?? "") : "";
                if (string.Equals(petId, target, StringComparison.OrdinalIgnoreCase)) live++;
            }

            // The active pet also appears in the mix keyed "", so a pet whose id matches nothing on screen
            // may still BE the active one under a different key.
            if (live == 0)
            {
                string activeId = Program.MyData != null ? (Program.MyData.GetActivePetId() ?? "") : "";
                if (string.Equals(activeId, target, StringComparison.OrdinalIgnoreCase))
                    return CompanionReloadOutcome.NeedsRestart;

                // Nothing borrowing it: drop any staged copy so the next spawn re-reads from disk.
                CompanionTypeRegistry.Entry staged;
                if (registry.TryGet(target, out staged)) registry.DropIfUnused(staged);
                return CompanionReloadOutcome.NothingOnScreen;
            }

            // Never yank a window out from under the mouse. RemoveOnePet does not consult this, so a reload
            // has to.
            if (AnyPetBusy())
            {
                error = "A pet is being dragged right now.";
                return CompanionReloadOutcome.Deferred;
            }

            // Stage BEFORE touching anything, so a bad file leaves the pets exactly as they were. Same
            // discipline as LoadNewXMLFromString: every fallible step completes before anything closes.
            string xmlText, readError;
            if (!CompanionCatalog.TryReadPetXml(target, out xmlText, out readError))
            {
                error = readError;
                return CompanionReloadOutcome.Deferred;
            }
            Xml stagedXml;
            Animations stagedAnimations;
            double factor = Program.MyData != null ? Program.MyData.GetEffectivePetScaleFactorD(target) : 1.0;
            if (!TryStageRuntime(xmlText, factor, out stagedXml, out stagedAnimations, out error))
                return CompanionReloadOutcome.Deferred;
            stagedAnimations.PetTypeId = target;

            // Displace the cached parse. The pets still on screen keep borrowing the OLD pair until their
            // FormClosed releases it; DisposeEntry removes by identity, so that release cannot evict this one.
            CompanionTypeRegistry.Entry fresh = registry.Add(target, stagedXml, stagedAnimations);

            reloadInProgress = true;
            try
            {
                for (int i = 0; i < live; i++) RemoveOnePet(target);
                for (int i = 0; i < live; i++)
                {
                    if (AddSheepCore(fresh.Xml, fresh.Animations, fresh) != null) reloaded++;
                }
            }
            finally { reloadInProgress = false; }

            PersistMix();
            AddDebugInfo(DEBUG_TYPE.info,
                "[pets] reloaded " + reloaded + " pet(s) of type '" + target + "' onto the updated definition");
            return CompanionReloadOutcome.Reloaded;
        }

        /// <summary>Remove one specific pet instance (the preview path; the tray removes BY TYPE instead).
        /// Safe to call twice and safe on a pet that already closed.</summary>
        internal bool RemovePetInstance(FormCompanion pet)
        {
            if (pet == null || disposed) return false;
            for (int i = 0; i < iSheeps; i++)
                if (ReferenceEquals(sheeps[i], pet))
                    return KillSheep(pet);
            return false;
        }

        // Host-level fullscreen state, fed by the scan each pet already runs every 300ms (see
        // FormCompanion.CheckFullScreen). Piggybacking rather than adding a second timer: the scan is the expensive
        // part and it is already happening, so this costs one array walk. With no pets on screen nobody is
        // scanning, so IsFullscreenActive falls back to an on-demand scan below.
        private bool _fullscreenActive;
        private DateTime _fullscreenSeenUtc = DateTime.MinValue;
        private static readonly TimeSpan FullscreenCacheLife = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Record the result of a pet's fullscreen scan, and raise <c>FullscreenChanged</c> when the answer
        /// flips. Called by every pet; the first one each cycle sets the value and the rest agree with it.
        /// </summary>
        internal void NoteFullscreenScan(bool[] blocked)
        {
            if (blocked == null) return;
            bool any = false;
            foreach (bool b in blocked) if (b) { any = true; break; }
            _fullscreenSeenUtc = DateTime.UtcNow;
            if (any == _fullscreenActive) return;
            _fullscreenActive = any;
            if (Host != null) Host.RaiseFullscreenChanged(any);
        }

        /// <summary>
        /// Whether a fullscreen window exists on any monitor. Answers from the pets' own scan while that is
        /// fresh, and scans on demand when it is not -- otherwise a module asking with no pets on screen (or
        /// during startup, before the first scan) would get a stale "no".
        /// </summary>
        internal bool IsFullscreenActive
        {
            get
            {
                if (DateTime.UtcNow - _fullscreenSeenUtc <= FullscreenCacheLife) return _fullscreenActive;
                try { NoteFullscreenScan(FullscreenScan.BlockedMonitors(SheepHandles())); }
                catch { }
                return _fullscreenActive;
            }
        }

        /// <summary>
        /// The pet TYPE id of a live pet, using the same convention as the on-screen mix: a pet with no
        /// registry entry is one of the ACTIVE/default type (the registry does not hold that one), so it
        /// reports the active id rather than "". Resolving it here is what lets a stored type choice match a
        /// pet of the default type, which would otherwise never compare equal to anything.
        /// </summary>
        internal string PetTypeIdOf(FormCompanion pet)
        {
            CompanionTypeRegistry.Entry entry;
            if (pet != null && petEntries.TryGetValue(pet, out entry) && entry != null && !string.IsNullOrEmpty(entry.Id))
                return entry.Id;
            return Program.MyData != null ? (Program.MyData.GetActivePetId() ?? "") : "";
        }

        /// <summary>True when this pet is a transient preview rather than a real, persisted pet.</summary>
        internal bool IsTransientPet(FormCompanion pet)
        {
            CompanionTypeRegistry.Entry entry;
            return pet != null && petEntries.TryGetValue(pet, out entry) && entry != null && entry.IsTransient;
        }

        /// <summary>
        /// Every live pet that is not a preview, in spawn order.
        ///
        /// The ONE place the preview filter is stated. It used to be re-derived at each call site, which is
        /// exactly how such an invariant rots: SayAll and PlayAnimationOnAll both walked sheeps[] directly and
        /// so happily spoke and animated through an authoring preview, contradicting the documented
        /// "previews are invisible to modules" rule. Anything that needs "the user's pets" reads this.
        /// </summary>
        internal System.Collections.Generic.IEnumerable<FormCompanion> PersistentPets()
        {
            for (int i = 0; i < iSheeps; i++)
                if (sheeps[i] != null && !IsTransientPet(sheeps[i])) yield return sheeps[i];
        }

        /// <summary>True when this pet is still on screen (and not a preview). Backs IHost.IsCompanionAlive, which a
        /// module needs because it can hold an ICompanion across a slow await and there is no CompanionRemoved event.</summary>
        internal bool IsLivePet(FormCompanion pet)
        {
            if (pet == null || pet.IsDisposed) return false;
            foreach (FormCompanion live in PersistentPets()) if (ReferenceEquals(live, pet)) return true;
            return false;
        }

        /// <summary>
        /// Which pet an UNPROMPTED remark belongs to. A poke inherits the clicked pet; a drop has none, so the
        /// host has to choose, and choosing badly is visible: uniform random lands on the same pet several
        /// times in a row often enough to read as "still broken". Round-robin over the eligible pets instead,
        /// with the cursor seeded randomly so a fresh session does not always start on pet #1.
        /// </summary>
        private int dropSubjectCursor = -1;
        private FormCompanion PickDropSubject()
        {
            var eligible = new System.Collections.Generic.List<FormCompanion>();
            // Busy is re-checked per pet even though DropTimer_Tick already vetoes the whole tick when ANY pet
            // is busy, so this stays correct if that global gate is ever relaxed.
            foreach (FormCompanion pet in PersistentPets())
                if (!pet.IsBusy) eligible.Add(pet);
            if (eligible.Count == 0) return null;
            if (dropSubjectCursor < 0) dropSubjectCursor = aiRand.Next(eligible.Count);
            dropSubjectCursor = (dropSubjectCursor + 1) % eligible.Count;
            return eligible[dropSubjectCursor];
        }

        /// <summary>
        /// The first pet that is not a preview, or null when only previews (or no pets) are on screen.
        /// Used wherever the host picks "the pet" to represent the user's pets to modules, so an authoring
        /// preview never becomes the subject of a poke or land event that another module reacts to.
        /// </summary>
        private FormCompanion FirstPersistentPet()
        {
            foreach (FormCompanion pet in PersistentPets()) return pet;
            return null;
        }

        // Load (or reuse) an extra pet type by id. Reuses the validated staging path so an untrusted
        // pet XML is never run without validation; does NOT call Animations.Activate() (only the active
        // type owns the Animations.Xml "current type" static). Returns null on any failure (logged).
        private CompanionTypeRegistry.Entry ResolveExtraType(string id)
        {
            CompanionTypeRegistry.Entry existing;
            if (registry.TryGet(id, out existing)) return existing;

            string xmlText, error;
            if (!CompanionCatalog.TryReadPetXml(id, out xmlText, out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "Pet '" + id + "' could not be loaded: " + error);
                return null;
            }

            Xml stagedXml;
            Animations stagedAnimations;
            double factor = Program.MyData.GetEffectivePetScaleFactorD(id);
            if (!TryStageRuntime(xmlText, factor, out stagedXml, out stagedAnimations, out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "Pet '" + id + "' failed validation: " + error);
                return null;
            }
            stagedAnimations.PetTypeId = id;   // extra type -> keyed by its folder id (per-pet settings)
            return registry.Add(id, stagedXml, stagedAnimations);
        }

        // A pet of an extra type closed: release its reference so the type's shared Xml/Animations are
        // disposed once the last pet of that type is gone. Never disposes for the active/default type
        // (those pets aren't in petEntries).
        private void ExtraPet_FormClosed(object sender, FormClosedEventArgs e)
        {
            FormCompanion pet = sender as FormCompanion;
            if (pet == null) return;
            pet.FormClosed -= ExtraPet_FormClosed;
            CompanionTypeRegistry.Entry entry;
            if (petEntries.TryGetValue(pet, out entry))
            {
                petEntries.Remove(pet);
                registry.Decrement(entry);
            }
        }

        // Flatten the persisted pet mix into a one-id-per-pet spawn plan (total capped at MAX_SHEEPS).
        // "" = the active/default pet. When there is no persisted mix (fresh install or after a reset),
        // fall back to the classic behaviour: GetAutoStartPets() copies of the active pet.
        private List<string> BuildStartupSpawnPlan()
        {
            var plan = new List<string>();
            List<CompanionCountEntry> mix = Program.MyData.GetPetMix();
            if (mix != null)
            {
                foreach (CompanionCountEntry entry in mix)
                {
                    if (entry == null) continue;
                    for (int i = 0; i < entry.Count && plan.Count < MAX_SHEEPS; i++)
                        plan.Add(entry.Id ?? "");
                }
            }
            if (plan.Count == 0)
            {
                int count = Math.Min(MAX_SHEEPS, Math.Max(1, Program.MyData.GetAutoStartPets()));
                for (int i = 0; i < count; i++) plan.Add("");
            }
            return plan;
        }

        /// <summary>True when the max-pets cap is reached (no more can be added).</summary>
        public bool IsAtMaxPets { get { return iSheeps >= MAX_SHEEPS; } }

        /// <summary>The persisted active/default pet's animations.xml (for the Options seam / ICompanionRuntime).</summary>
        public string ActivePetXml { get { return Program.MyData != null ? Program.MyData.GetXml() : ""; } }

        /// <summary>
        /// The current on-screen pet mix: each live root pet counted under its type id ("" = the
        /// active/default pet), in first-appearance order. Used by the tray Remove submenu and to
        /// persist the mix for next launch.
        /// </summary>
        internal List<CompanionCountEntry> OnScreenMix()
        {
            var types = new List<CompanionTypeRegistry.Entry>(iSheeps);
            for (int i = 0; i < iSheeps; i++)
            {
                FormCompanion pet = sheeps[i];
                if (pet == null) continue;
                CompanionTypeRegistry.Entry entry;
                types.Add(petEntries.TryGetValue(pet, out entry) ? entry : null);
            }
            return DeriveOnScreenMix(types);
        }

        /// <summary>
        /// Count pets by type id, in first-appearance order, from one registry entry per live pet (null =
        /// a pet of the active/default type, which the registry does not hold). Split out as a static so
        /// the rule is directly testable, because it is load-bearing twice over: this list is both what
        /// gets PERSISTED as the startup mix and what the tray's "Remove a pet" submenu renders.
        ///
        /// TRANSIENT entries are skipped. That single omission is the whole safety story for preview pets:
        /// a preview cannot reach settings.json, cannot survive a restart, cannot corrupt the startup spawn
        /// plan, and cannot appear as a tray row that would both mislabel itself and remove a real pet when
        /// clicked. Anything that must ignore previews should read this, not walk the pet array itself.
        /// </summary>
        internal static List<CompanionCountEntry> DeriveOnScreenMix(IEnumerable<CompanionTypeRegistry.Entry> petTypes)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            if (petTypes != null)
            {
                foreach (CompanionTypeRegistry.Entry entry in petTypes)
                {
                    if (entry != null && entry.IsTransient) continue;
                    string id = entry != null ? (entry.Id ?? "") : "";
                    if (!counts.ContainsKey(id)) { counts[id] = 0; order.Add(id); }
                    counts[id]++;
                }
            }
            var mix = new List<CompanionCountEntry>();
            foreach (string id in order)
                mix.Add(new CompanionCountEntry { Id = id, Count = counts[id] });
            return mix;
        }

        /// <summary>Spawn one pet of the given type from the tray and remember the new on-screen mix.</summary>
        public bool AddPetFromTray(string id)
        {
            bool added = AddSheep(id);
            if (added) PersistMix();
            return added;
        }

        /// <summary>
        /// Remove one on-screen pet of a given type (the most recently added), routing through KillSheep
        /// so the kill animation + reference release + persistence all run. False when none is present.
        /// </summary>
        public bool RemoveOnePet(string id)
        {
            string target = id ?? "";
            for (int i = iSheeps - 1; i >= 0; i--)
            {
                FormCompanion pet = sheeps[i];
                if (pet == null) continue;
                CompanionTypeRegistry.Entry entry;
                string petId = petEntries.TryGetValue(pet, out entry) ? (entry.Id ?? "") : "";
                if (string.Equals(petId, target, StringComparison.OrdinalIgnoreCase))
                    return KillSheep(pet);   // KillSheep persists the reduced mix
            }
            return false;
        }

        /// <summary>
        /// Set a pet type's size override (level 1/2/3, or 0 to follow the global size) and persist it.
        /// The size is baked in when the type is staged, so it takes effect the next time this pet is
        /// added (or on the next launch); pets of this type already on screen keep their current size
        /// until then. A staged-but-unspawned copy is dropped here so a fresh add re-stages at the new
        /// factor immediately. Returns true if the stored value changed. id "" is the active/default pet.
        /// </summary>
        public bool SetPetSize(string id, int level)
        {
            bool changed = Program.MyData.SetPetSizeLevel(id ?? "", level);
            CompanionTypeRegistry.Entry entry;
            if (!string.IsNullOrEmpty(id) && registry.TryGet(id, out entry))
                registry.DropIfUnused(entry);   // only drops when no pet is using it (safe)
            return changed;
        }

        /// <summary>
        /// Set a pet type's size PERCENT override (25..400, or 0 to follow the global size) and persist it.
        /// Like <see cref="SetPetSize"/>, the size is baked in at staging, so it takes effect the next time
        /// this pet is added (or on the next launch). Returns true if the stored value changed.
        /// </summary>
        public bool SetPetScalePercent(string id, int percent)
        {
            bool changed = Program.MyData.SetPetScalePercent(id ?? "", percent);
            CompanionTypeRegistry.Entry entry;
            if (!string.IsNullOrEmpty(id) && registry.TryGet(id, out entry))
                registry.DropIfUnused(entry);
            return changed;
        }

        /// <summary>The effective size PERCENT (25..400) for a pet, for the slider UI. id "" = active pet.</summary>
        public int GetPetScalePercent(string id)
        {
            return Program.MyData != null ? Program.MyData.GetEffectivePetScalePercent(id ?? "") : 100;
        }

        /// <summary>Route host audio to the given output device GUID ("" = default). Applied live so a
        /// Preferences change takes effect immediately (B1.5).</summary>
        public void ApplyAudioDevice(string deviceId)
        {
            AudioOutput a = audioOutput;
            if (a != null) a.SetDevice(deviceId);
        }

        /// <summary>Play a short test tone through the current output device (the Preferences "Test sound" button).</summary>
        public void PlayTestSound()
        {
            AudioOutput a = audioOutput;
            if (a != null) a.PlayTestTone();
        }

        /// <summary>Mute/unmute a pet type's animation sound (per-pet sound toggle, B3). Persists only; the
        /// sink checks it at play time so it takes effect on the next sound, no restage. id "" = active pet.</summary>
        public bool SetPetSound(string id, bool enabled)
        {
            return Program.MyData != null && Program.MyData.SetPetSoundEnabled(id, enabled);
        }

        // Persist the current on-screen mix so the same set is restored next launch. Called after
        // user-initiated changes (tray add/remove, KillSheep, replace-all), never during the startup
        // restore itself.
        private void PersistMix()
        {
            if (disposed) return;
            try { Program.MyData.SetPetMix(OnScreenMix()); } catch { }
        }

        internal bool RunResourceChurnPetCycle(string speech)
        {
            if (!Program.ResourceChurnSelfTestActive ||
                disposed ||
                animations == null ||
                xml == null)
                return false;

            FormCompanion pet = null;
            try
            {
                pet = CreateAndInitializeOwnedPet(
                    delegate { return new FormCompanion(animations, xml); },
                    delegate(FormCompanion candidate)
                    {
                        candidate.ShowInTaskbar = false;
                        candidate.Opacity = 0d;
                        candidate.Show(xml.spriteWidth, xml.spriteHeight);
                        candidate.Play(true);
                    });
                pet.Say(speech);
                Application.DoEvents();
                bool speechPainted =
                    pet.PaintSpeechForResourceChurn();
                using (var rendered = new Bitmap(
                    Math.Max(1, pet.Width),
                    Math.Max(1, pet.Height)))
                {
                    pet.DrawToBitmap(
                        rendered,
                        new Rectangle(Point.Empty, rendered.Size));
                }
                return speechPainted;
            }
            finally
            {
                if (pet != null)
                {
                    try { pet.Close(); } catch { }
                    try { pet.Dispose(); } catch { }
                    Application.DoEvents();
                }
            }
        }

            /// <summary>
            /// Close all sheeps on the desktop and eventually closes the application.
            /// </summary>
            /// <param name="exit">If true, the application will close after 1 second (leaving time to the sheeps to die).</param>
        public void KillSheeps(bool exit)
        {
            AddDebugInfo(DEBUG_TYPE.info, "Killing all sheeps");
            timer1.Tag = "0";
            pi.Dispose();

                // Leave open application to show some kill animations. 
                // Only if there is a pet on the desktop.
            if (iSheeps > 0)
            {
                for (int i = 0; i < iSheeps; i++)
                {
                    FormCompanion pet = sheeps[i];
                    sheeps[i] = null;
                    if (pet != null && !pet.IsDisposed)
                    {
                        TrackRetiringPet(pet);
                        pet.Kill();
                    }
                }
                iSheeps = 0;

                if (exit)
                {
                    timer1.Interval = 1100;
                    timer1.Enabled = true;
                }
            }
            else
            {
                timer1.Interval = 100;
                timer1.Enabled = true;
            }
        }


        /// <summary>
        /// Handles of every live sheep window, so the fullscreen scan can ignore the pets themselves
        /// (a sheep sitting on top of a borderless game must not be mistaken for the top window there).
        /// </summary>
        public HashSet<IntPtr> SheepHandles()
        {
            var handles = new HashSet<IntPtr>();
            for (int i = 0; i < iSheeps; i++)
            {
                FormCompanion sheep = sheeps[i];
                if (sheep != null && !sheep.IsDisposed && sheep.IsHandleCreated)
                    handles.Add(sheep.Handle);
            }
            return handles;
        }

        /// <summary>
        /// Bring every sheep to top most again
        /// </summary>
        public void TopMostSheeps()
        {
            AddDebugInfo(DEBUG_TYPE.info, "Top most all sheeps");

            for (int i = 0; i < iSheeps; i++)
            {
                sheeps[i].TopMost = true;
            }
        }

        /// <summary>
        /// Close a single sheep on the desktop.
        /// </summary>
        /// <param name="sheep">The sheep-form to close.</param>
        public bool KillSheep(FormCompanion sheep)
        {
            bool bSheepRemoved = false;
            // Read before the pet leaves petEntries: removing a preview must not rewrite the persisted mix.
            // DeriveOnScreenMix already omits transients, so persisting here would be harmless in content --
            // but it would still be a settings.json WRITE caused by a module's preview, which is not the
            // host's business.
            bool wasTransient = IsTransientPet(sheep);

            AddDebugInfo(DEBUG_TYPE.info, "Kill one sheep");

            for (int i = 0; i < iSheeps; i++)
            {
                if(sheeps[i] == sheep)
                {
                    TrackRetiringPet(sheeps[i]);
                    sheeps[i].Kill();
                    for (int j = i; j < iSheeps - 1; j++) sheeps[j] = sheeps[j + 1];
                    iSheeps--;
                    sheeps[iSheeps] = null;
                    bSheepRemoved = true;
                    break;
                }
            }

            if (bSheepRemoved && !wasTransient) PersistMix();   // remember the reduced on-screen mix for next launch

            /*
             * This will close application if all Sheeps are removed. But Maybe the user want see the try icon to add a sheep later.
             * Maybe in future you can choose 0 to 10 sheeps at startup, so this is commented out for the moment.
            if (iSheeps <= 0)
            {
                timer1.Tag = "0";
                pi.Dispose();

                timer1.Interval = 1100;
                timer1.Enabled = true;
            }
            */

            return bSheepRemoved;
        }

            /// <summary>
            /// Timer used to init and close the application.
            /// </summary>
            /// <param name="sender">Caller as object.</param>
            /// <param name="e">Timer event values.</param>
        private void Timer1_Tick(object sender, EventArgs e)
        {
            if (disposed) return;
            string state = timer1.Tag as string;
                // "A" when application starts. Add a sheep.
            if (state == "A")
            {
                if (spawnPlan == null)
                {
                    spawnPlan = BuildStartupSpawnPlan();
                    spawnPlanIndex = 0;
                }

                if (spawnPlanIndex < spawnPlan.Count && iSheeps < MAX_SHEEPS)
                {
                    if (iSheeps == 0)
                        AddDebugInfo(DEBUG_TYPE.info, "init application...");
                    // "" spawns the active/default pet; a folder id spawns that type alongside.
                    AddSheep(spawnPlan[spawnPlanIndex]);
                    spawnPlanIndex++;
                }
                else
                {
                    timer1.Enabled = false;
                    timer1.Tag = "B";
                    spawnPlan = null;
                    spawnPlanIndex = 0;
                }
            }
                // "0" when application should be stopped.
            else if (state == "0")
            {
                timer1.Tag = "1";
            }
            else
            {
                Application.Exit();
            }
        }
        
            /// <summary>
            /// Load new XML (from XML string).
            /// </summary>
            /// <param name="strXml">A string with the xml content.</param>
        public bool LoadNewXMLFromString(string strXml)
        {
            AddDebugInfo(DEBUG_TYPE.info, "load new XML string");
            if (disposed) return false;

            FormCompanion marshal = iSheeps > 0
                ? sheeps[0]
                : retiringPets.FirstOrDefault();
            if (marshal != null && marshal.InvokeRequired)
            {
                marshal.BeginInvoke(new MethodInvoker(delegate
                {
                    LoadNewXMLFromString(strXml);
                }));
                return true;
            }

            Xml stagedXml;
            Animations stagedAnimations;
            string error;
            // "Use this pet" -> the pet becomes the active/default type; callers persist activePetId first,
            // so key it by that real id (per-pet size/sound follow the pet).
            string useId = Program.MyData != null ? Program.MyData.GetActivePetId() : CompanionCatalog.BuiltInPetId;
            if (!TryStageRuntime(
                    strXml,
                    Program.MyData.GetEffectivePetScaleFactorD(useId),
                    out stagedXml,
                    out stagedAnimations,
                    out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "New pet rejected: " + error);
                return false;
            }
            stagedAnimations.PetTypeId = useId;

            timer1.Stop();
            Xml oldXml = xml;
            Animations oldAnimations = animations;
            string oldPersistedXml = Program.MyData.GetXml();
            string oldPersistedImages = Program.MyData.GetImages();
            string oldPersistedIcon = Program.MyData.GetIcon();
            bool persisted = false;
            try
            {
                // Complete every fallible activation/commit step before closing the current pets.
                stagedAnimations.Activate();
                ApplyTrayIcon(stagedXml);
                if (!Program.MyData.TrySetPetAssets(strXml, "", ""))
                    throw new IOException("The staged pet could not be saved atomically.");
                persisted = true;

                CloseAllPetsImmediate();
                // "Use this pet" resets the desktop to a single active type: drop every extra type
                // (their pets' FormClosed already released most; this clears any stragglers). Never
                // touches the staged or old active pair -- neither is in the registry.
                registry.DisposeAll();
                petEntries.Clear();
                xml = stagedXml;
                animations = stagedAnimations;
                // Reset the persisted mix to "just the active pet": all pets are closed here (iSheeps==0
                // -> empty mix), so the re-armed autostart below respawns GetAutoStartPets() copies of
                // the new active pet, and next launch restores the same.
                spawnPlan = null;
                spawnPlanIndex = 0;
                PersistMix();
                timer1.Tag = "A";
                timer1.Interval = 1000;
                timer1.Start();
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.error, "Could not activate the staged pet: " + ex.Message);
                if (persisted)
                    Program.MyData.TrySetPetAssets(
                        oldPersistedXml,
                        oldPersistedImages,
                        oldPersistedIcon);
                animations = oldAnimations;
                xml = oldXml;
                if (animations != null) animations.Activate();
                ApplyTrayIcon(xml);
                stagedAnimations.Dispose();
                stagedXml.Dispose();
                timer1.Tag = "A";
                timer1.Start();
                return false;
            }

            if (oldAnimations != null) oldAnimations.Dispose();
            if (oldXml != null) oldXml.Dispose();
            return true;
        }

        private void CloseAllPetsImmediate()
        {
            var ownedPets = new HashSet<FormCompanion>();
            for (int i = 0; i < sheeps.Length; i++)
            {
                FormCompanion pet = sheeps[i];
                sheeps[i] = null;
                if (pet != null) ownedPets.Add(pet);
            }
            foreach (FormCompanion pet in retiringPets.Drain())
            {
                if (pet == null) continue;
                pet.FormClosed -= RetiringPet_FormClosed;
                ownedPets.Add(pet);
            }
            foreach (FormCompanion pet in ownedPets)
            {
                if (pet.IsDisposed) continue;
                try { pet.Close(); } catch { }
                try { pet.Dispose(); } catch { }
            }
            iSheeps = 0;
        }

        private void TrackRetiringPet(FormCompanion pet)
        {
            if (pet == null || pet.IsDisposed || !retiringPets.Add(pet))
                return;
            pet.FormClosed += RetiringPet_FormClosed;
            if (pet.IsDisposed)
                RetiringPet_FormClosed(pet, null);
        }

        private void RetiringPet_FormClosed(object sender, FormClosedEventArgs e)
        {
            FormCompanion pet = sender as FormCompanion;
            if (pet == null) return;
            pet.FormClosed -= RetiringPet_FormClosed;
            retiringPets.Remove(pet);
        }

            /// <summary>
            /// Returns the Animation class.
            /// </summary>
            /// <returns>Member variable to access all animations of the current pet.</returns>
            /// <summary>
            /// If the application is started with the SHIFT key pressed, warnings and errors are reported on a window.
            /// </summary>
            /// <param name="type">See <see cref="StartUp.DEBUG_TYPE"/> for the possible values. </param>
            /// <param name="text">Text to show in the dialog window.</param>
        public static void AddDebugInfo(DEBUG_TYPE type, string text)
        {
            FormDebug target;
            lock (debugLock) target = debug;
            if (target == null || target.IsDisposed || target.Disposing) return;
            try
            {
                MethodInvoker append = delegate
                {
                    if (!target.IsDisposed && !target.Disposing)
                        target.AddDebugInfo(type, text);
                };
                if (target.InvokeRequired)
                {
                    if (target.IsHandleCreated) target.BeginInvoke(append);
                }
                else
                {
                    append();
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

            /// <summary>
            /// If the application is started with the SHIFT key pressed, some extra features are activated.
            /// </summary>
            /// <returns>true if the application is running with debug window.</returns>
        public static bool IsDebugActive()
        {
            lock (debugLock)
                return debug != null && !debug.IsDisposed && !debug.Disposing;
        }

        /// <summary>
        /// Show a speech bubble above every REAL pet (never a preview).
        /// Does nothing when speech bubbles are disabled in Options.
        ///
        /// The back-to-back repeat guard used to live here, as one global "last broadcast line". It moved into
        /// <see cref="FormCompanion.Say"/> so it is per pet and cannot be bypassed by IHost.Say(pet, text) -- see the
        /// comment there. This method now only decides WHO hears the line.
        /// </summary>
        public void SayAll(string text)
        {
            // Offer the utterance to the speech responders first: a voice module may speak it instead of
            // showing bubbles. Nothing registered => this returns immediately and behaviour is unchanged.
            if (Host != null && Host.RaiseSpeechRequest(null, text)) return;
            ShowBubbleOnAll(text, 0);
        }

        /// <summary>As <see cref="SayAll(string)"/>, with a per-message speech style the bubble renders.</summary>
        public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style)
        {
            if (Host != null && Host.RaiseSpeechRequest(null, text)) return;
            ShowBubbleOnAll(text, 0, style);
        }

        /// <summary>
        /// Draw a bubble for a message addressed to nobody in particular, WITHOUT re-offering the speech chain
        /// (extracted so SayAll and the host-supplied ShowBubble fallback share one implementation and neither
        /// can recurse into the responders).
        ///
        /// ONE pet speaks it, not all of them. Every pet drawing the same line at the same instant read as a
        /// bug -- the ABI comment on SayAll said so in as many words -- because these messages are announcements
        /// to the USER (a reminder, a fortune, the tray's speech test), and a room of pets chanting in unison is
        /// not how one voice reaches a person. A message that genuinely belongs to a pet already goes through
        /// Say(pet, ...); this is the other case.
        /// </summary>
        internal void ShowBubbleOnAll(string text, int dwellSeconds, DesktopAICompanion.Modules.SpeechStyle style = null)
        {
            FormCompanion speaker = DefaultSpeaker();
            if (speaker != null) speaker.SayWithDwell(text, dwellSeconds, style);
        }

        /// <summary>
        /// The pet that speaks for the app: the user's chosen type if one of that type is out, else the oldest
        /// pet on screen. Null only when no pet is out at all.
        ///
        /// Falling back rather than going silent is deliberate: the choice names a pet TYPE, and a type can be
        /// removed from the mix long after it was chosen. A reminder the user asked for must not be swallowed
        /// because the pet they picked is not currently on screen.
        /// </summary>
        internal FormCompanion DefaultSpeaker()
        {
            FormCompanion first = null;
            string chosen = Program.MyData != null ? Program.MyData.GetDefaultSpeakingPet() : "";
            foreach (FormCompanion pet in PersistentPets())
            {
                if (first == null) first = pet;
                if (!string.IsNullOrEmpty(chosen) &&
                    string.Equals(PetTypeIdOf(pet), chosen, StringComparison.OrdinalIgnoreCase))
                    return pet;
            }
            return first;
        }

        internal bool PlayModuleSound(string owner, byte[] audio, double volume)
        {
            // Global "notification sounds" master switch: off => PlaySound is a no-op and the module falls
            // back to a silent bubble.
            if (Program.MyData != null && !Program.MyData.GetNotificationSoundsEnabled()) return false;
            AudioOutput a = audioOutput;
            return a != null && a.PlayOwned(owner, audio, volume);
        }

        internal bool StopModuleSound(string owner)
        {
            AudioOutput a = audioOutput;
            return a != null && a.StopOwned(owner);
        }

        /// <summary>Cut every module's audio (not the pet's own SFX). Called when the user switches speech
        /// off: there is no settings-changed event on IHost, so a module cannot notice on its own and would
        /// otherwise keep talking for the rest of the current utterance.</summary>
        public bool StopAllModuleSound()
        {
            AudioOutput a = audioOutput;
            return a != null && a.StopAllExcept(AudioOutput.EngineOwner);
        }

        /// <summary>Real content = at least one letter or digit; "…"/punctuation-only cues are transient.</summary>
        internal static bool HasSpeechContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (char.IsLetterOrDigit(c)) return true;
            return false;
        }



        /// <summary>
        /// (Re)arm the random-drop timer from the current settings. Idempotent: safe at launch and on
        /// every settings change. A stale timer is retired via the ReferenceEquals guard below.
        /// </summary>
        private void ApplyRandomDrop()
        {
            if (dropTimer != null)
            {
                dropTimer.Stop();
                if (dropTimerHandler != null) dropTimer.Tick -= dropTimerHandler;
                dropTimer.Dispose();
                dropTimer = null;
                dropTimerHandler = null;
            }
            if (disposed || Program.MyData == null || !Program.MyData.GetRandomDropEnabled()) return;

            var timer = new System.Windows.Forms.Timer();
            EventHandler handler = null;
            handler = delegate { DropTimer_Tick(timer); };
            timer.Tick += handler;
            dropTimer = timer;
            dropTimerHandler = handler;
            ScheduleDrop(timer);
        }

        /// <summary>
        /// Arm the module-update check. The first evaluation is deliberately late (two minutes) so nothing
        /// about launch waits on it, then it settles to a slow heartbeat purely so a pet left running for
        /// weeks still notices the week rolling over — the timer cadence is NOT how often it hits the
        /// network. Whether a fetch actually happens is decided by AppUpdateCheck.ShouldCheck against the
        /// weekly interval, and never at all if the user turned it off.
        /// </summary>
        /// <summary>
        /// Ask once, in the background, which installed pets the catalog has a newer copy of, and write the
        /// answer down so the Pets pane can show it the moment it opens.
        ///
        /// Not awaited, and not on the UI thread: hashing every installed catalog pet is real file I/O, and
        /// the pane used to pay that cost synchronously behind a button press.
        /// </summary>
        private void ArmPetUpdateCheck()
        {
            try
            {
                LocalData data = Program.MyData;
                if (data == null || !data.GetPetUpdateCheck()) return;
                if (!AppUpdateCheck.ShouldCheck(true, data.GetPetUpdateLastCheckUtc(),
                        DateTimeOffset.UtcNow, AppUpdateCheck.ContentInterval)) return;
                _ = RunPetUpdateCheckAsync();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task RunPetUpdateCheckAsync()
        {
            try
            {
                RemoteCatalog catalog = await RemoteCatalogClient
                    .FetchAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                if (disposed) return;
                // Hash off the UI thread. StaleInstalledIds reads and digests every installed catalog pet.
                List<string> stale = await System.Threading.Tasks.Task
                    .Run(delegate { return CompanionProvenance.StaleInstalledIds(catalog); }).ConfigureAwait(false);
                if (disposed) return;
                LocalData data = Program.MyData;
                if (data == null) return;
                data.SetPetUpdateResult(DateTimeOffset.UtcNow, string.Join(";", stale.ToArray()));
                AddDebugInfo(DEBUG_TYPE.info, stale.Count == 0
                    ? "[pets] update check: every installed pet is current"
                    : "[pets] update check: " + stale.Count + " pet(s) have a newer version");
            }
            catch (Exception ex)
            {
                // Offline or catalog unreachable: leave the stamp alone so the next launch retries.
                AddDebugInfo(DEBUG_TYPE.info, "[pets] update check deferred: " + ex.Message);
            }
        }

        private void ArmModuleUpdateCheck()
        {
            if (disposed || moduleUpdateTimer != null) return;
            var timer = new System.Windows.Forms.Timer { Interval = ModuleUpdateFirstPassMilliseconds };
            EventHandler handler = null;
            handler = delegate
            {
                // After the first pass, drop to the slow cadence (the first interval only defers launch impact).
                if (timer.Interval != ModuleUpdateCadenceMilliseconds)
                    timer.Interval = ModuleUpdateCadenceMilliseconds;
                EvaluateModuleUpdateCheck();
            };
            timer.Tick += handler;
            moduleUpdateTimer = timer;
            moduleUpdateTimerHandler = handler;
            timer.Start();
        }

        /// <summary>
        /// Fetch the catalog if this calendar month has not been checked yet, and notify when an installed
        /// module has a newer build. It never downloads or applies anything: consent stays with the user, who
        /// sees the module's permissions before an install and clicks Update themselves.
        ///
        /// The month is stamped only after a SUCCESSFUL fetch, so being offline (or asleep) on the 1st costs
        /// nothing — the next tick tries again, and the month is consumed when the check really happened.
        /// </summary>
        /// <summary>
        /// Look for module updates on the shared weekly cadence and WRITE DOWN what was found.
        ///
        /// Persisting the result is the point. This check already existed and already ran, but it discarded
        /// its findings into a balloon, so the Modules pane still knew nothing when you opened it and you
        /// still had to press "Check online". A pane can only show an update the instant it opens if the
        /// last answer is on disk.
        ///
        /// The stamp is written only after a check actually HAPPENS. The previous version seeded it on a
        /// fresh install without checking, so a new install could not learn about a module update until the
        /// next calendar month -- the same "stamp on a negative answer and go blind" bug documented in
        /// AppUpdateCheck, at thirty times the interval.
        /// </summary>
        private async void EvaluateModuleUpdateCheck()
        {
            if (disposed || moduleUpdateCheckRunning) return;
            LocalData data = Program.MyData;
            if (data == null || !data.GetMonthlyModuleUpdateCheck()) return;
            if (!AppUpdateCheck.ShouldCheck(true, data.GetModuleUpdateLastCheckUtc(),
                    DateTimeOffset.UtcNow, AppUpdateCheck.ContentInterval)) return;

            System.Collections.Generic.IReadOnlyList<DesktopAICompanion.Modules.IModule> modules = LoadedModules;
            if (modules == null || modules.Count == 0)
            {
                // Nothing installed to compare. Stamp anyway: the answer "no updates" is correct and
                // re-asking every tick would be pointless traffic.
                data.SetModuleUpdateResult(DateTimeOffset.UtcNow, "");
                return;
            }

            moduleUpdateCheckRunning = true;
            try
            {
                // RemoteCatalogClient bounds its own deadline, so this cannot hang the timer indefinitely.
                RemoteCatalog catalog = await RemoteCatalogClient.FetchAsync(System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);
                if (disposed) return;
                var offers = DesktopAICompanion.Plugins.ModuleUpdateScan.FindUpdates(catalog, modules);
                data.SetModuleUpdateResult(DateTimeOffset.UtcNow,
                    DesktopAICompanion.Plugins.ModuleUpdateScan.Encode(offers));
                if (offers.Count == 0)
                {
                    AddDebugInfo(DEBUG_TYPE.info, "[module] update check: everything is current");
                    return;
                }
                string summary = DesktopAICompanion.Plugins.ModuleUpdateScan.Describe(offers);
                AddDebugInfo(DEBUG_TYPE.info, "[module] update check: " + summary + " available");
                if (pi != null)
                    pi.ShowBalloon(
                        "Module update available",
                        summary + " — click here to open Settings, Modules.",
                        delegate { DesktopAICompanion.Wpf.OptionsShell.Open("Modules"); });
            }
            catch (Exception ex)
            {
                // Offline, DNS down, catalog unreachable: leave the stamp alone and retry on a later tick.
                AddDebugInfo(DEBUG_TYPE.info, "[module] update check deferred: " + ex.Message);
            }
            finally { moduleUpdateCheckRunning = false; }
        }

        /// <summary>Arm the drop timer for a fresh random interval of center ± jitter minutes.</summary>
        private void ScheduleDrop(System.Windows.Forms.Timer timer)
        {
            if (timer == null || disposed || Program.MyData == null ||
                !ReferenceEquals(dropTimer, timer))
                return;
            int center = Math.Min(9999, Math.Max(1, Program.MyData.GetRandomDropMinutes()));
            int jitter = Math.Min(center - 1, Math.Max(0, Program.MyData.GetRandomDropJitterMinutes()));
            int minutes = aiRand.Next(center - jitter, center + jitter + 1);
            timer.Interval = Math.Max(60000, minutes * 60000);   // at least one minute
            timer.Start();
        }

        /// <summary>
        /// Random-drop tick: when a pet is present, speech is on, and no pet is busy, speak an AI
        /// insight if the brain is enabled, otherwise a fortune. Then reschedule a fresh interval.
        /// </summary>
        private void DropTimer_Tick(System.Windows.Forms.Timer timer)
        {
            if (!ReferenceEquals(dropTimer, timer)) return;
            timer.Stop();
            try
            {
                if (iSheeps > 0 && Program.MyData.GetSpeechEnabled() && !AnyPetBusy())
                {
                    // Always raise the arbitrated drop tick: the AI-brain module's drop responder takes it as
                    // an AI insight when its brain is enabled, otherwise the Fortunes module speaks. The base
                    // no longer drives the AI brain itself (S4b) — that moved to the AiBrain module.
                    // The drop now belongs to ONE pet, chosen round-robin, so it honours that pet's speech
                    // source instead of every pet reciting the same line at once.
                    FormCompanion subject = PickDropSubject();
                    if (subject != null && Host != null) Host.RaiseDropTick(subject);
                }
            }
            catch { /* a single missed drop must never crash the pet */ }
            ScheduleDrop(timer);
        }


        /// <summary>
        /// The pet was right-clicked ("poked"). Timing-based escalation: a pause resets it; rapid
        /// pokes climb from a rich reaction -> being ignored -> verbal sass -> a bathtub escape.
        /// Poke 1 of a session offers the arbitrated poke-responder chain (an AI quip / a fortune /
        /// nothing, per the user's "Trigger Speech" preference), rate-limited by its own cooldown.
        /// </summary>
        public void OnPetPoked() { OnPetPoked(null); }

        /// <summary>
        /// As <see cref="OnPetPoked()"/>, but told WHICH pet the user clicked. Only FormCompanion knows that, and it
        /// used to throw it away: the host then recovered "a" pet with <see cref="FirstPersistentPet"/>, so a
        /// poke on pet #5 was reported to modules as a poke on pet #1. That is invisible while every speaker
        /// broadcasts through <see cref="SayAll"/>, and silently wrong the moment anything reacts per pet.
        /// </summary>
        /// <param name="poked">The pet the user clicked, or null when the caller cannot say.</param>
        public void OnPetPoked(FormCompanion poked)
        {
            if (iSheeps == 0 || !Program.MyData.GetSpeechEnabled()) return;

            // Attribute the poke to the pet the user actually clicked, falling back to the first persistent pet
            // only when the caller could not say. Never a preview: modules react to CompanionPoked with user-visible
            // behavior, and an authoring preview is not one of the user's pets. With only previews on screen
            // no module hears the poke at all, which is the correct reading of "no pet was poked".
            FormCompanion subject = (poked != null && !IsTransientPet(poked)) ? poked : FirstPersistentPet();
            if (subject == null) return;

            DateTime now = DateTime.UtcNow;
            PokeState state = pokeStates.GetValue(subject, _ => new PokeState());
            if ((now - state.LastPokeUtc).TotalSeconds > PokeResetSeconds) state.Count = 0;
            state.LastPokeUtc = now;
            state.Count++;
            if (Host != null) Host.RaiseCompanionPoked(subject, state.Count);

            if (state.Count >= PokeEscapeAt)        // 12: the finale
            {
                state.Count = 0;                    // reset after escaping
                // Deliberately global: every pet fleeing to the bath at once IS the joke, unlike the sass and
                // the turn-away below, which are answers to "you poked ME". Kept as a decision, not an
                // oversight. The consolation fortune belongs to the poked pet, though.
                if (!EscapeAllToBath() && Host != null) Host.RaiseDropTick(subject);
                return;
            }
            if (state.Count >= PokeSassFrom)        // 5-11: verbal sass
            {
                string s = PokeReactions.RandomSass();
                if (!string.IsNullOrWhiteSpace(s)) subject.Say(s);
                return;
            }
            if (state.Count >= PokeIgnoreFrom)      // 3-4: ignore — turn away, no bubble
            {
                PlayFirstAnimationOn(subject, "rotate1a", "look_down", "sleep1a");
                return;
            }
            if (state.Count == 1) TryPokeReaction(subject, state, now);
            // 2: nothing — one rich reaction per session, then straight into the escalation ladder.
        }

        /// <summary>
        /// Offer poke 1 to the arbitrated poke-responder chain on behalf of the pet that was clicked, honoring
        /// that pet's own cooldown. The cooldown only advances when something actually spoke, so a silent
        /// attempt (no modules installed, or all declined) doesn't burn the window and leave the next poke
        /// mysteriously mute.
        ///
        /// The "Trigger Speech" choice is no longer read here: CompanionHost resolves it from the subject, so the
        /// poke and drop chains cannot disagree about what this pet's speech source is. Reading it here also
        /// hard-coded the "" key, which is the ALL-PETS entry, so a per-pet choice could never have applied.
        /// </summary>
        private void TryPokeReaction(FormCompanion subject, PokeState state, DateTime now)
        {
            if (Host == null || subject == null || state == null) return;
            if ((now - state.LastReactionUtc).TotalSeconds < PokeReactionCooldownSeconds) return;
            if (Host.RaisePokeReaction(subject)) state.LastReactionUtc = now;
        }

        /// <summary>Flee via the bathtub spawn on every pet. True if at least one could.</summary>
        private bool EscapeAllToBath()
        {
            bool any = false;
            for (int i = 0; i < iSheeps; i++)
                if (sheeps[i] != null && sheeps[i].EscapeToBath()) any = true;
            return any;
        }

        /// <summary>Play the first of the named animations that this pet actually defines. Used for the
        /// turn-away tier of the poke ladder, which belongs to the pet that was poked -- it used to turn EVERY
        /// pet away from a click on one of them.</summary>
        private static void PlayFirstAnimationOn(FormCompanion pet, params string[] names)
        {
            if (pet == null) return;
            foreach (string n in names)
                if (pet.TryPlayAnimation(n)) break;
        }

        /// <summary>
        /// Land greeting: wait for the spawned pet to stop falling, then speak a fortune. The pet is
        /// descending only while its Y grows, so once Y stops increasing for a couple of polls it has
        /// landed (or is walking/climbing). Fires after ~0.5s of no descent, with a ~10s safety cap.
        /// </summary>
        private void LandTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                landTicks++;
                // A real pet, never a preview: the land fortune is a greeting for the user's pet arriving,
                // not for an authoring preview being tried out.
                FormCompanion pet = FirstPersistentPet();
                if (pet == null)
                {
                    if (landTicks > 40 && landTimer != null) landTimer.Stop();   // no pet after ~10s: give up
                    return;
                }

                int y = pet.Top;
                if (landPrevY != int.MinValue && y <= landPrevY) landStable++; else landStable = 0;
                landPrevY = y;

                if ((landStable >= 2 && landTicks >= 3) || landTicks >= 40)
                {
                    if (landTimer != null) landTimer.Stop();
                    if (Host != null) Host.RaiseCompanionLanded(pet);   // the Fortunes module speaks the land fortune
                }
            }
            // Never let a fortune/speech throw escape a WinForms timer tick as an unhandled UI exception.
            catch { try { if (landTimer != null) landTimer.Stop(); } catch { } }
        }

        /// <summary>
        /// Play a prioritized set of candidate animations on every live pet: for each pet, the first
        /// candidate its XML actually defines is played (<see cref="FormCompanion.TryPlayAnimation"/>). The
        /// caller owns any emotion->candidate-names mapping. This backs the plugin host's
        /// <c>PlayAnimationAll</c> service, so a module (the AI brain) can emote every pet without
        /// owning the live-pet list — the same reason <see cref="SayAll"/> is a host service. Must run
        /// on the UI thread. Never throws — the pet physics engine must never be disturbed.
        /// </summary>
        internal void PlayAnimationOnAll(System.Collections.Generic.IReadOnlyList<string> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;
            try
            {
                // PersistentPets, not sheeps[]: an authoring preview must not emote on a module's behalf, for
                // the same reason it must not speak. This walked the raw array before and did exactly that.
                foreach (FormCompanion pet in PersistentPets())
                {
                    foreach (string name in candidates)
                        if (pet.TryPlayAnimation(name)) break;   // first defined candidate wins
                }
            }
            catch { }
        }

        /// <summary>
        /// Launch-time setup for the drop + land-greeting timers: arm the shared random-drop timer
        /// (the Fortunes module speaks the drop; cadence lives in settings.json) and start the
        /// land-greeting poll. Any failure here is non-fatal — the pet still runs.
        /// </summary>
        private void InitDropTriggers()
        {
            try
            {
                // Fortunes moved to the Fortunes module (S3d); the base only arms the shared random-drop
                // timer here (the module's fortune drop responder speaks it). Cadence lives in settings.json.
                ApplyRandomDrop();

                // Land greeting: poll the pet's fall and speak a fortune only once it has settled,
                // so the first bubble never appears mid-air.
                landTimer = new System.Windows.Forms.Timer();
                landTimer.Interval = 250;
                landTimer.Tick += LandTimer_Tick;
                landTimer.Start();
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "drop triggers init failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-apply live settings while the pet is running — called by the options shell when it
        /// closes. Resyncs the random-drop timer from settings.json. UI thread; never throws.
        /// </summary>
        public void ReloadAiSettings()
        {
            try
            {
                ApplyRandomDrop();   // fortunes moved to the module (S3d); resync the drop timer (settings.json owns the cadence)
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "settings reload failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Keeps ownership local until a newly constructed form is fully initialized. If showing or
        /// starting the form fails, no array slot owns it, so close and dispose it before rethrowing.
        /// </summary>
        internal static FormCompanion CreateAndInitializeOwnedPet(
            Func<FormCompanion> create,
            Action<FormCompanion> initialize)
        {
            if (create == null) throw new ArgumentNullException("create");
            if (initialize == null) throw new ArgumentNullException("initialize");

            FormCompanion pet = null;
            try
            {
                pet = create();
                if (pet == null)
                    throw new InvalidOperationException(
                        "The pet form factory returned no form.");
                initialize(pet);
                return pet;
            }
            catch
            {
                if (pet != null)
                {
                    try { pet.Close(); } catch { }
                    try { pet.Dispose(); } catch { }
                }
                throw;
            }
        }


        /// <summary>True if any pet is currently being handled by the user (drop gate).</summary>
        private bool AnyPetBusy()
        {
            for (int i = 0; i < iSheeps; i++)
                if (sheeps[i] != null && sheeps[i].IsBusy) return true;
            return false;
        }

        /// <summary>
        /// Calling this function, all sheeps will execute the same animation (if the sync-word is present in the XML).
        /// </summary>
        public void SyncSheeps()
        {
            AddDebugInfo(DEBUG_TYPE.info, "synchronize sheeps");
            for (int i = 0; i < iSheeps; i++)
            {
                sheeps[i].Sync();
            }
        }
    }

    /// <summary>
    /// Small generation gate shared by idle initial-arm, tick admission, and finally-rearm paths.
    /// Reconfiguration invalidates every pending operation from the prior policy generation.
    /// </summary>
    internal sealed class GenerationAwareIdleSchedule
    {
        private readonly object _sync = new object();
        private int _generation;
        private bool _enabled;
        private bool _armed;

        internal void Reconfigure(int generation, bool enabled)
        {
            lock (_sync)
            {
                _generation = generation;
                _enabled = enabled;
                _armed = false;
            }
        }

        internal bool TryArm(int expectedGeneration)
        {
            lock (_sync)
            {
                if (!CanRunLocked(expectedGeneration) || _armed)
                    return false;
                _armed = true;
                return true;
            }
        }

        internal bool TryBeginTick(int expectedGeneration)
        {
            lock (_sync)
            {
                if (!CanRunLocked(expectedGeneration) || !_armed)
                    return false;
                _armed = false;
                return true;
            }
        }

        internal bool CanRun(int expectedGeneration)
        {
            lock (_sync)
                return CanRunLocked(expectedGeneration);
        }

        internal bool IsArmedForDiagnostics
        {
            get { lock (_sync) return _armed; }
        }

        private bool CanRunLocked(int expectedGeneration)
        {
            return _enabled && expectedGeneration == _generation;
        }
    }
}
