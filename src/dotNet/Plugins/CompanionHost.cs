using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Text.Json;
using DesktopAICompanion.Ai;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.Plugins
{
    /// <summary>
    /// The live <see cref="IHost"/> that loaded modules bind to. Bridges the plugin ABI to the running
    /// app: StartUp raises the lifecycle events (Raise* below); services delegate to StartUp / FormCompanion /
    /// Program.MyData; contributions are collected here (the tray/options renderer consumes them in the
    /// WPF-shell phase). Everything runs on the UI thread; a throwing module never breaks the host.
    /// </summary>
    internal sealed class CompanionHost : IHost
    {
        private readonly StartUp _startUp;
        private readonly ConditionalWeakTable<FormCompanion, CompanionHandle> _handles = new ConditionalWeakTable<FormCompanion, CompanionHandle>();
        private int _nextPetId;
        // Both arbitrated chains, each holding BOTH registration styles in ONE list. A legacy
        // Func<bool> registration is wrapped as `pet => f()` at registration time, so priority ordering stays
        // global: a module that has migrated to the pet-aware overload and one that has not still compete
        // fairly. Keeping two parallel lists would have made "which fires first" depend on which style was
        // used, which is the kind of ordering bug nobody finds by reading.
        private readonly List<Responder> _dropResponders = new List<Responder>();
        // Poke-1 responders, each also tagged with its module id so the "Trigger Speech" preference can force
        // one specific source (or pick randomly among all of them).
        private readonly List<Responder> _pokeResponders = new List<Responder>();
        private sealed class Responder
        {
            public string ModuleId;
            public int Priority;
            // Monotonic registration order, so equal priorities keep the order they registered in. This used
            // to be recovered with IndexOf against the very list being replaced -- correct only because the
            // sort ran over a copy, O(n^2), and one refactor away from a silent ordering change in the
            // "Default & Random" pick. A counter states the intent directly.
            public int Seq;
            public Func<ICompanion, bool> OnFire;
        }
        private int _nextResponderSeq;

        // Speech responders: offered every utterance before a bubble is drawn, highest priority first.
        private readonly List<SpeechResponder> _speechResponders = new List<SpeechResponder>();
        private sealed class SpeechResponder
        {
            public string ModuleId;
            public int Priority;
            public int Seq;
            public Func<SpeechRequest, bool> OnSpeech;
        }
        // Reentrancy latch. A responder that claims a line and then says something itself -- which the
        // reminders feature does, drawing its own bubble -- would otherwise be offered its own line forever.
        private bool _raisingSpeech;
        // Bumped per utterance, so a ShowBubble callback held past its moment can tell it has been superseded
        // and quietly decline rather than drawing a stale bubble.
        private int _speechGeneration;

        /// <summary>Highest priority first, then registration order. List.Sort is not stable, so the
        /// tie-break is explicit rather than assumed.</summary>
        private static void SortResponders(List<Responder> responders)
        {
            responders.Sort((x, y) =>
            {
                int byPriority = y.Priority.CompareTo(x.Priority);
                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);
            });
        }

        /// <summary>
        /// The id to file a responder under: what the caller declared, or failing that, the module whose
        /// Init we are currently inside.
        ///
        /// THE FALLBACK IS LOAD-BEARING, not tidiness. Neither drop-responder overload takes a module id
        /// (and the ABI cannot grow one without a breaking change), so both registered as "". RaiseDropTick
        /// routes the chain through the user's "Trigger Speech" choice, and RaiseChain's filter removes
        /// every candidate whose id is not that choice -- which, against a chain where every id was "",
        /// removed ALL of them. Picking anything but "Default" therefore silenced every random-drop remark
        /// in the app, permanently and with no error, while pokes went on working because the poke pair
        /// does carry Info.Id. That asymmetry is what made it look like a half-broken feature rather than
        /// a routing bug.
        ///
        /// ModuleHost wraps module.Init in BeginModuleInit/EndModuleInit for exactly this kind of
        /// attribution and OptionsPane ownership already uses it. A responder registered OUTSIDE Init
        /// still files as "", which is the old behaviour and is correct: the host genuinely does not know
        /// who is calling then.
        /// </summary>
        private string ResponderModuleId(string declared)
        {
            string given = (declared ?? "").Trim();
            if (given.Length > 0) return given;
            return (_initialisingModuleId ?? "").Trim();
        }

        private IDisposable AddResponder(List<Responder> list, string moduleId, int priority, Func<ICompanion, bool> onFire)
        {
            if (onFire == null) return new Noop();
            var entry = new Responder
            {
                ModuleId = ResponderModuleId(moduleId),
                Priority = priority,
                Seq = _nextResponderSeq++,
                OnFire = onFire,
            };
            list.Add(entry);
            SortResponders(list);
            var remover = new Remover(() => list.Remove(entry));
            if (_initLedger != null) _initLedger.Registrations.Add(remover);   // undone if this Init throws (F344)
            return remover;
        }

        /// <summary>Offer one arbitrated chain to its responders, highest priority first, until one handles
        /// it. <paramref name="only"/> restricts the offer to a single module id (the user's explicit
        /// "Trigger Speech" choice); <paramref name="shuffle"/> is the "Default &amp; Random" pick.</summary>
        private bool RaiseChain(List<Responder> chain, FormCompanion subject, string only, bool shuffle)
        {
            var candidates = new List<Responder>(chain);
            string preferred = (only ?? "").Trim();
            if (preferred.Length > 0)
                candidates.RemoveAll(r => !string.Equals(r.ModuleId, preferred, StringComparison.OrdinalIgnoreCase));
            else if (shuffle)
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    Responder swap = candidates[i];
                    candidates[i] = candidates[j];
                    candidates[j] = swap;
                }

            ICompanion handle = subject != null ? HandleFor(subject) : null;
            foreach (Responder r in candidates)
            {
                bool handled = false;
                Func<ICompanion, bool> fn = r.OnFire;
                // Treated as DECLINED, and RECORDED (F328). This was a bare Safe(), so a responder that threw
                // was indistinguishable from one that passed: the chain fell through to the next module
                // exactly as it should, and nothing anywhere said why the pet did not speak. RaiseEach had
                // already learned this lesson for the events; the three chains that decide whether the pet
                // speaks at all had not, although each entry carries the module id the event path lacks.
                try { handled = fn(handle); }
                catch (Exception ex)
                {
                    Log(r.ModuleId, "responder threw and was treated as declined: " + ex.GetType().Name + ": " + ex.Message);
                }
                if (handled) return true;
            }
            return false;
        }

        public readonly List<TrayItem> TrayItems = new List<TrayItem>();
        public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();

        /// <summary>The UI thread's SynchronizationContext and id, captured at construction: StartUp builds
        /// this on the UI thread. A target-less bubble re-shown from a worker is posted through it (F333).</summary>
        private readonly SynchronizationContext _ui = SynchronizationContext.Current;
        private readonly int _uiThreadId = Thread.CurrentThread.ManagedThreadId;

        public CompanionHost(StartUp startUp) { _startUp = startUp; }

        public string HostVersion { get { return Application.ProductVersion; } }
        public bool SpeechEnabled { get { return Program.MyData != null && Program.MyData.GetSpeechEnabled(); } }
        public double Volume { get { return Program.MyData != null ? Program.MyData.GetVolume() : 0.0; } }

        // Preferred user display name, published by a module (the AI brain) and read by others (the fortunes
        // welcome). In-memory + host-owned; "" = none set (consumers fall back to their own default).
        private volatile string _ownerName = "";
        public string OwnerName { get { return _ownerName ?? ""; } }
        public void SetOwnerName(string name)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length > 64) trimmed = trimmed.Substring(0, 64);   // a display name, not an essay
            _ownerName = trimmed;
        }

        // ---- lifecycle events (raised by StartUp at the existing hook points) ----
        //
        // EXPLICIT ACCESSORS over private backing fields, same ABI (F344). A subscription made inside a
        // module's Init is recorded on the Init ledger, so a module whose Init throws AFTER subscribing is
        // unsubscribed again by RollBackModuleInit instead of staying wired into a host that reports it
        // failed. Raise sites read the backing fields; the remove accessors are the plain ones.
        private Action<ICompanion> _companionSpawned;
        private Action<PokeInfo> _companionPoked;
        private Action<ICompanion> _companionLanded;
        private Action _hostShutdown;
        public event Action<ICompanion> CompanionSpawned
        {
            add { _companionSpawned += value; Ledger(delegate { _companionSpawned -= value; }); }
            remove { _companionSpawned -= value; }
        }
        public event Action<PokeInfo> CompanionPoked
        {
            add { _companionPoked += value; Ledger(delegate { _companionPoked -= value; }); }
            remove { _companionPoked -= value; }
        }
        public event Action<ICompanion> CompanionLanded
        {
            add { _companionLanded += value; Ledger(delegate { _companionLanded -= value; }); }
            remove { _companionLanded -= value; }
        }
        public event Action HostShutdown
        {
            add { _hostShutdown += value; Ledger(delegate { _hostShutdown -= value; }); }
            remove { _hostShutdown -= value; }
        }

        internal ICompanion HandleFor(FormCompanion pet)
        {
            if (pet == null) return null;
            return _handles.GetValue(pet, p => new CompanionHandle(p, ++_nextPetId));
        }
        /// <summary>
        /// Invoke every subscriber, and let one that throws cost only itself.
        ///
        /// PER HANDLER, NOT PER EVENT. These four used to wrap the whole multicast invocation in a single
        /// Safe(...), which is a bare catch: the first subscriber that threw aborted the rest of the
        /// invocation list, and the exception was swallowed with no log line. Four shipped modules
        /// subscribe to CompanionSpawned in load order, and Fortunes.OnPetSpawned calls host.SayAll with
        /// no internal guard, so anything thrown out of a bubble draw permanently cost Reminder its spawn
        /// handler -- invisibly, and looking for all the world like a Reminder bug. The class doc above
        /// promises a throwing module never breaks the host; that was true of the host and false of the
        /// other modules.
        ///
        /// The failure is LOGGED now rather than swallowed. A handler that throws on every spawn would
        /// otherwise write nothing anywhere, which is the shape this repo keeps finding: a fault that
        /// leaves a working run and a broken run looking identical.
        /// </summary>
        private void RaiseEach<T>(Delegate root, string eventName, Action<T> call) where T : class
        {
            if (root == null) return;
            foreach (Delegate d in root.GetInvocationList())
            {
                T typed = d as T;
                if (typed == null) continue;
                try { call(typed); }
                catch (Exception ex)
                {
                    try
                    {
                        string owner = d.Target != null ? d.Target.GetType().Name : "?";
                        Log(null, "a " + eventName + " handler in " + owner + " threw and was skipped: "
                                  + ex.GetType().Name + ": " + ex.Message);
                    }
                    catch { }
                }
            }
        }

        internal void RaiseCompanionSpawned(FormCompanion pet)
        {
            RaiseEach<Action<ICompanion>>(_companionSpawned, "CompanionSpawned", h => h(HandleFor(pet)));
        }
        internal void RaiseCompanionPoked(FormCompanion pet, int count)
        {
            var info = new PokeInfo { Pet = HandleFor(pet), PokeCount = count };
            RaiseEach<Action<PokeInfo>>(_companionPoked, "CompanionPoked", h => h(info));
        }
        internal void RaiseCompanionLanded(FormCompanion pet)
        {
            RaiseEach<Action<ICompanion>>(_companionLanded, "CompanionLanded", h => h(HandleFor(pet)));
        }
        internal void RaiseShutdown()
        {
            RaiseEach<Action>(_hostShutdown, "HostShutdown", h => h());
        }

        /// <summary>Every subscriber across the six module-facing events, for the self-test that proves a
        /// rolled-back Init left none behind (F344), and its witness that a healthy Init left some.</summary>
        internal int LifecycleSubscriberCount
        {
            get
            {
                int n = 0;
                foreach (Delegate d in new Delegate[] { _companionSpawned, _companionPoked, _companionLanded, _hostShutdown, _fullscreenChanged, _contextChanged })
                    if (d != null) n += d.GetInvocationList().Length;
                return n;
            }
        }

        /// <summary>
        /// Offer a drop tick to responders by priority (highest first) until one handles it. The drop belongs
        /// to <paramref name="subject"/> -- the host picks it, because an unprompted remark has no clicked pet
        /// to inherit -- and honours that pet's "Trigger Speech" choice, exactly as a poke does. Routing used
        /// to apply to pokes only, so a per-pet choice was silently ignored by half the things that speak.
        /// </summary>
        internal bool RaiseDropTick(FormCompanion subject)
        {
            return RaiseChain(_dropResponders, subject, PreferredModuleFor(subject), shuffle: false);
        }

        /// <summary>
        /// Whether anything is listening for the random drop. Nothing in the base speaks on a drop tick --
        /// it exists purely to give a module the cue -- so with no responder registered the timer fires into
        /// nothing and every setting that governs it is dead UI.
        ///
        /// Deliberately a CAPABILITY question rather than "is fortunes or aibrain installed". Those two are
        /// the responders today, but the whole point of the responder chain is that the host does not know
        /// which module answers, and a hardcoded id list would silently hide the settings from a third
        /// module that registers one.
        /// </summary>
        /// Unlocked, matching how the responder lists are read everywhere else in this class: registration
        /// happens on the UI thread during module init, and so does the settings pane that asks this.
        internal bool HasDropResponder
        {
            get { return _dropResponders.Count > 0; }
        }

        /// <summary>The speech source the user chose for this pet, falling back to the all-pets choice, or ""
        /// for "default and random". Resolved host-side so the poke and drop chains cannot disagree.</summary>
        private string PreferredModuleFor(FormCompanion pet)
        {
            try
            {
                if (Program.MyData == null) return "";
                return Program.MyData.GetTriggerSpeechModule(SpeechRoutingKey(pet));
            }
            catch { return ""; }
        }

        /// <summary>
        /// The key a pet's speech preference is stored under. This is NOT the pet-mix id: the mix writes the
        /// active/default pet as "", while "" in triggerSpeech already means the ALL-PETS entry. Keying a real
        /// pet as "" would silently rewrite the global preference and still look right, because the lookup
        /// falls back to global. So the active pet resolves to its real type id, which is also what
        /// ICompanion.TypeId and the per-pet size/sound settings already use. With no pet in hand, or a pet
        /// whose PetTypeId is empty, the key is the type RUNNING as the default, StartUp.DefaultTypeId, not the
        /// persisted active id (RA-227): on F305's fallback branch the two differ (the persisted id still names
        /// the rejected pet so a later host can bring it back), and a key built from it named a pet no tray
        /// entry reads. The tray's SpeechRoutingKey(string) resolves its "" entry the same way, so the two
        /// cannot disagree about what a pet's key is.
        /// </summary>
        internal static string SpeechRoutingKey(FormCompanion pet)
        {
            string typeId = pet != null ? (pet.PetTypeId ?? "") : "";
            if (typeId.Length > 0) return typeId;
            try { return Program.Mainthread != null ? (Program.Mainthread.DefaultTypeId ?? "") : ""; }
            catch { return ""; }
        }
        private static void Safe(Action a) { try { a(); } catch { /* a bad module must not break the host */ } }

        // ---- services ----
        // Guarded on IsDisposed and wrapped in Safe, unlike before. A module holds an ICompanion for as long as it
        // likes -- both Fortunes and the AI brain keep a _lastPet field, and there is no CompanionRemoved event to
        // tell them it went away -- so a pet the user removed mid-answer is a normal case, not an exotic one.
        // Unguarded, FormCompanion.Say would build a fresh FormSpeech on a disposed form and throw out of the
        // module's call. SayAll is structurally immune because it walks the live pet list; Say was not.
        public void Say(ICompanion pet, string text)
        {
            var p = pet as CompanionHandle;
            if (p == null || p.Pet == null || p.Pet.IsDisposed) return;
            // Offer the speech chain here too: this path bypasses SayAll entirely, so a voice module would
            // silently miss every targeted line -- which, after per-pet routing, is most of them.
            if (RaiseSpeechRequest(p.Pet, text)) return;
            Safe(() => p.Pet.Say(text));
        }
        public void SayAll(string text) { if (_startUp != null) _startUp.SayAll(text); }
        public void Say(ICompanion pet, string text, SpeechStyle style)
        {
            var p = pet as CompanionHandle;
            if (p == null || p.Pet == null || p.Pet.IsDisposed) return;
            // The style travels with the offer (RA-275): a responder that claims the line and re-shows it
            // through SpeechRequest.ShowBubble used to get the plain bubble, because the request and the
            // pending bubble behind it carried no style.
            if (RaiseSpeechRequest(p.Pet, text, style)) return;
            Safe(() => p.Pet.SayWithDwell(text, 0, style));
        }
        public void SayAll(string text, SpeechStyle style) { if (_startUp != null) _startUp.SayAll(text, style); }
        public bool TryPlayAnimation(ICompanion pet, string name) { var p = pet as CompanionHandle; return p != null && p.Pet != null && p.Pet.TryPlayAnimation(name); }
        public ScreenContext CaptureScreenContext(ICompanion pet)
        {
            var p = pet as CompanionHandle;
            if (p == null || p.Pet == null) return null;
            ScreenCaptureContext ctx = ActiveWindow.CaptureContext(p.Pet.CaptureScreenBounds);
            System.Drawing.Rectangle b = ctx.MonitorBounds;

            // The window map (host 1.1.0). One bounded enumeration, reusing the filter set FullscreenScan
            // proved out -- visible, un-minimised, un-cloaked, non-shell, not one of our own companions.
            // Our own windows are excluded here for the same reason the fullscreen scan excludes them: a
            // companion is not something the user is looking at.
            System.Collections.Generic.HashSet<IntPtr> petHandles = null;
            try { if (_startUp != null) petHandles = _startUp.SheepHandles(); }
            catch { }
            if (petHandles == null) petHandles = new System.Collections.Generic.HashSet<IntPtr>();

            var windows = new System.Collections.Generic.List<ScreenWindow>();
            PixelRect foregroundBounds = default(PixelRect);
            string foregroundProcess = null;
            try
            {
                foreach (DesktopWindowInfo w in DesktopWindows.Snapshot(petHandles))
                {
                    var rect = new PixelRect(w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height);
                    windows.Add(new ScreenWindow
                    {
                        Title = w.Title,
                        ProcessName = w.ProcessName,
                        Bounds = rect,
                        MonitorIndex = w.MonitorIndex,
                        IsForeground = w.IsForeground,
                        ZOrder = w.ZOrder,
                    });
                    if (w.IsForeground) { foregroundBounds = rect; foregroundProcess = w.ProcessName; }
                }
            }
            catch
            {
                // A module gets an empty list rather than an exception; the monitor path still works.
            }

            return new ScreenContext
            {
                WindowTitle = ctx.ActiveWindowTitle,
                // From the snapshot entry that IS the foreground window, which already resolved the name
                // (F329): a third GetForegroundWindow and a second Process.GetProcessById per ask could
                // disagree with the two before them. The fallback keeps the old answer for the cases the
                // snapshot filters out -- our own window, a cloaked or shell foreground, past the cap --
                // including blank for our own process.
                ProcessName = !string.IsNullOrEmpty(foregroundProcess) ? foregroundProcess : ActiveWindow.ProcessName(),
                MonitorBounds = new PixelRect(b.X, b.Y, b.Width, b.Height),
                WindowUnderCompanion = p.Pet.WindowUnderCompanion,
                ForegroundWindowBounds = foregroundBounds,
                Windows = windows,
            };
        }
        public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { if (_startUp != null) _startUp.PlayAnimationOnAll(animationCandidates); }
        public IDisposable RegisterHotkey(string combo, Action onPressed)
        {
            // The ABI makes global-hotkey registration a host service (it needs a UI-thread message
            // window + pump), so the host owns the registrar and a module just calls this. Wraps the
            // proven HotkeyListener; a bad/taken combo degrades to a no-op handle (the hotkey simply
            // never fires) rather than throwing into the module. Called on the UI thread.
            if (string.IsNullOrWhiteSpace(combo) || onPressed == null) return new Noop();
            HotkeyListener listener = null;
            try
            {
                listener = new HotkeyListener();
                listener.Pressed += delegate { Safe(() => onPressed()); };
                if (!listener.Register(combo))
                {
                    listener.Dispose();
                    return new Noop();
                }
            }
            catch
            {
                if (listener != null) { try { listener.Dispose(); } catch { } }
                return new Noop();
            }
            HotkeyListener registered = listener;
            var remover = new Remover(() => { try { registered.Dispose(); } catch { } });
            if (_initLedger != null) _initLedger.Registrations.Add(remover);   // undone if this Init throws (F344)
            return remover;
        }
        public IModuleStorage GetStorage(string moduleId) { return new ModuleStorage(ModuleDataDir(moduleId)); }
        public IModuleSettings GetSettings(string moduleId) { return new ModuleSettings(Path.Combine(ModuleDataDir(moduleId), "settings.json")); }
        // The legacy pair: the pet is discarded, because these callers never learn it. Wrapped into the same
        // list as the pet-aware pair so one priority order governs both.
        public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop)
        {
            if (onDrop == null) return new Noop();
            return AddResponder(_dropResponders, "", priority, pet => onDrop());
        }
        public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke)
        {
            if (onPoke == null) return new Noop();
            return AddResponder(_pokeResponders, moduleId, priority, pet => onPoke());
        }

        // The pet-aware pair (1.5.0+). NOT permission-gated at registration: ModuleHost calls Init BEFORE
        // adding the module to LoadedModules, so ModuleDeclares would answer false for the very module
        // registering here and every module would be silently refused while holding a healthy-looking handle.
        public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop)
        {
            return AddResponder(_dropResponders, "", priority, onDrop);
        }
        public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke)
        {
            return AddResponder(_pokeResponders, moduleId, priority, onPoke);
        }

        /// <summary>
        /// Play the sound the USER chose for notifications. Same gate and the same three
        /// layers as PlaySound -- Audio declared, master volume, the notificationSounds
        /// switch -- with the difference that the BYTES are the app's, not the module's.
        /// </summary>
        public bool PlayNotificationSound(string moduleId)
        {
            try
            {
                if (!ModuleDeclares(moduleId, ModulePermissions.Audio)) return false;
                if (_startUp == null) return false;   // host not running (self-test path)
                return _startUp.PlayNotificationSound(moduleId ?? "");
            }
            catch (Exception) { return false; }
        }

        public bool PlaySound(string moduleId, byte[] audio, double volume)
        {
            try
            {
                if (!ModuleDeclares(moduleId, ModulePermissions.Audio)) return false;
                if (_startUp == null) return false;   // host not running (self-test path)
                // The user's slider always wins: a module's volume is a fraction of it, so it can be quieter
                // than the pet but never louder, and a master of 0 really is silence.
                double effective = Math.Max(0.0, Math.Min(1.0, volume)) * Volume;
                if (effective <= 0.0) return false;
                return _startUp.PlayModuleSound(moduleId ?? "", audio, effective);
            }
            catch { return false; }
        }

        public bool StopSound(string moduleId)
        {
            // Not permission-gated: going quiet is strictly weaker than the play that made the sound.
            try { return _startUp != null && _startUp.StopModuleSound(moduleId ?? ""); }
            catch { return false; }
        }

        public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech)
        {
            if (onSpeech == null) return new Noop();
            var entry = new SpeechResponder
            {
                ModuleId = (moduleId ?? "").Trim(),
                Priority = priority,
                Seq = _nextResponderSeq++,
                OnSpeech = onSpeech,
            };
            _speechResponders.Add(entry);
            _speechResponders.Sort((x, y) =>
            {
                int byPriority = y.Priority.CompareTo(x.Priority);
                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);
            });
            var remover = new Remover(() => _speechResponders.Remove(entry));
            if (_initLedger != null) _initLedger.Registrations.Add(remover);
            return remover;
        }

        public bool IsFullscreenActive
        {
            get { return _startUp != null && _startUp.IsFullscreenActive; }
        }

        private Action<bool> _fullscreenChanged;
        public event Action<bool> FullscreenChanged
        {
            add { _fullscreenChanged += value; Ledger(delegate { _fullscreenChanged -= value; }); }
            remove { _fullscreenChanged -= value; }
        }

        /// <summary>Raise <see cref="FullscreenChanged"/>. Wrapped in Safe for the same reason every other
        /// module-facing event is: a module throwing from its handler must not take the host's scan down.</summary>
        internal void RaiseFullscreenChanged(bool active)
        {
            RaiseEach<Action<bool>>(_fullscreenChanged, "FullscreenChanged", h => h(active));
        }

        public bool IsCompanionAlive(ICompanion pet)
        {
            var p = pet as CompanionHandle;
            if (p == null || p.Pet == null || p.Pet.IsDisposed) return false;
            try { return _startUp != null && _startUp.IsLivePet(p.Pet); }
            catch { return false; }
        }

        /// <summary>Module ids that registered a poke responder, highest priority first — the source list
        /// the "Trigger Speech" preference offers (plus the base's own "default &amp; random" entry).</summary>
        internal IReadOnlyList<string> PokeResponderModuleIds
        {
            get { return ResponderModuleIds(_pokeResponders); }
        }

        /// <summary>How many speech responders are registered. A seam for --module-host-selftest, which
        /// otherwise cannot see whether an Init rollback removed the responders it ledgered (RA-281).</summary>
        internal int SpeechResponderCountForDiagnostics { get { return _speechResponders.Count; } }


        private static IReadOnlyList<string> ResponderModuleIds(List<Responder> chain)
        {
            var ids = new List<string>(chain.Count);
            foreach (Responder r in chain)
                if (!string.IsNullOrEmpty(r.ModuleId) && !ContainsIgnoreCase(ids, r.ModuleId))
                    ids.Add(r.ModuleId);
            return ids;
        }

        private static bool ContainsIgnoreCase(List<string> ids, string candidate)
        {
            foreach (string id in ids)
                if (string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Offer the first poke of a fresh session to the poke responders, on behalf of the pet that was
        /// actually clicked. The preference is resolved HERE from that pet rather than passed in, so the poke
        /// and drop chains cannot drift apart on what "this pet's speech source" means.
        ///
        /// An empty choice is the default random pick (shuffled, so with both Fortunes and the AI brain
        /// installed either can win, and a session where none of them chooses to speak stays silent);
        /// otherwise only that module is offered the poke, and if it declines nothing else speaks -- an
        /// explicit choice is a restriction, not a preference.
        /// </summary>
        internal bool RaisePokeReaction(FormCompanion subject)
        {
            return RaisePokeReactionFor(subject, PreferredModuleFor(subject));
        }

        /// <summary>
        /// Offer one utterance to the speech responders before any bubble is drawn. Returns true when a
        /// responder claimed it AND asked for the bubble to be suppressed -- i.e. the caller should not draw.
        ///
        /// <paramref name="target"/> is the pet about to speak, or null for a broadcast. Fast-paths to false
        /// when nothing is registered, so a host with no voice module behaves exactly as it did before.
        /// </summary>
        internal bool RaiseSpeechRequest(FormCompanion target, string text)
        {
            return RaiseSpeechRequest(target, text, null);
        }

        /// <summary>As above, carrying the <see cref="SpeechStyle"/> the styled Say/SayAll overloads were
        /// handed, so a claimed line re-shown through <see cref="SpeechRequest.ShowBubble"/> keeps it
        /// (RA-275). Null for the plain overloads and the poke sass.</summary>
        internal bool RaiseSpeechRequest(FormCompanion target, string text, SpeechStyle style)
        {
            if (_speechResponders.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            // The user's master speech switch covers voice too: SayAll does not check it (the gate lives in
            // FormCompanion.Say), so without this a module could voice lines the user had silenced.
            if (!SpeechEnabled) return false;
            if (_raisingSpeech) return false;

            int generation = ++_speechGeneration;
            var pending = new PendingBubble(this, target, text, generation, style);
            var request = new SpeechRequest
            {
                Text = text,
                Pet = target != null ? HandleFor(target) : null,
                ShowBubble = pending.Show,
            };

            _raisingSpeech = true;
            try
            {
                // A SNAPSHOT, as RaiseChain takes (F332): a responder that disposes its own registration from
                // inside the callback mutates _speechResponders, and List<T>'s versioned enumerator would
                // throw InvalidOperationException out of Say/SayAll on the next step -- outside the catch
                // below, which covers only the callback itself.
                foreach (SpeechResponder r in _speechResponders.ToArray())
                {
                    if (!ModuleDeclares(r.ModuleId, ModulePermissions.Voice)) continue;   // raise-time gate
                    bool claimed = false;
                    Func<SpeechRequest, bool> fn = r.OnSpeech;
                    // Declined and recorded, as in RaiseChain (F328).
                    try { claimed = fn(request); }
                    catch (Exception ex)
                    {
                        Log(r.ModuleId, "speech responder threw and was treated as declined: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    if (claimed) return request.SuppressBubble;   // read only AFTER a claim
                }
            }
            finally { _raisingSpeech = false; }
            return false;
        }

        /// <summary>
        /// The host-supplied one-shot behind <see cref="SpeechRequest.ShowBubble"/>. Bypasses both the
        /// responder chain and the repeat guard, which is exactly why it has to live here: a module cannot
        /// hand a line back by calling Say/SayAll, because the guard would swallow the identical replay.
        /// </summary>
        private sealed class PendingBubble
        {
            private readonly CompanionHost _host;
            private readonly FormCompanion _target;
            private readonly string _text;
            private readonly int _generation;
            /// <summary>The style the styled Say/SayAll were handed, or null for a plain line (RA-275).</summary>
            private readonly SpeechStyle _style;
            private bool _used;

            internal PendingBubble(CompanionHost host, FormCompanion target, string text, int generation, SpeechStyle style)
            { _host = host; _target = target; _text = text; _generation = generation; _style = style; }

            internal void Show(double seconds)
            {
                if (_used) return;
                _used = true;
                // A newer utterance has already been offered, so this one is stale: drawing it now would put
                // an old line on screen after a newer one. The counter is per HOST, not per pet (RA-278,
                // recorded): a claimed line for pet A is stale once any line for another pet or a broadcast
                // has been offered. SpeechRequest.ShowBubble's contract says so.
                if (_host == null || _host._speechGeneration != _generation) return;
                int dwell = seconds > 0 ? (int)Math.Max(2, Math.Min(30, Math.Round(seconds))) : 0;
                Action draw = delegate
                {
                    // Both draws take the style (null draws the plain bubble, as the 2-arg form did).
                    if (_target != null) { if (!_target.IsDisposed) _target.SayWithDwell(_text, dwell, _style); }
                    else if (_host._startUp != null) _host._startUp.ShowBubbleOnAll(_text, dwell, _style);
                };
                // A module resuming a synthesis await will normally already be on the UI thread, but a
                // Task.Run continuation would not be, and touching a window off-thread corrupts it.
                //
                // BOTH branches are marshalled (F333). Only the targeted one was: a broadcast bubble (SayAll,
                // no target) re-shown from a worker ran draw() inline, and ShowBubbleOnAll builds a FormSpeech
                // -- a window -- on that thread. The host's UI context, captured when StartUp constructed it,
                // is the anchor a target-less bubble never had; the thread id decides, not the context
                // instance, because WinForms may hand the UI thread a fresh context object.
                try
                {
                    if (_host._ui != null && Thread.CurrentThread.ManagedThreadId != _host._uiThreadId)
                        _host._ui.Post(delegate { try { draw(); } catch (Exception ex) { _host.Log(null, "bubble draw failed: " + ex.Message); } }, null);
                    else if (_target != null && _target.InvokeRequired) _target.BeginInvoke(draw);
                    else draw();
                }
                catch (Exception ex) { _host.Log(null, "bubble draw failed: " + ex.Message); }
            }
        }

        /// <summary>Test seam: the same arbitration with the preference supplied directly, so the chain's
        /// semantics (explicit choice is a restriction, unknown id stays silent, random offers everyone) can be
        /// asserted without a live pet and a settings file. Production callers use the overload above.</summary>
        internal bool RaisePokeReactionFor(FormCompanion subject, string preferredModuleId)
        {
            return RaiseChain(_pokeResponders, subject, preferredModuleId, shuffle: true);
        }
        private readonly Random _random = new Random();
        // Both catalog verbs read RemoteCatalogClient's SHARED copy: a 90 s lifetime, the same copy the two panes
        // read and "Check for ... online" invalidates. The host used to keep a second RemoteCatalog of its own
        // with no TTL, so a pack re-published mid-session failed its hash check on every download until the module
        // happened to re-browse, and the panes' explicit check could not clear it (F334). Downloading N items
        // after a browse still fetches once: the shared copy is what the 90 s window is for.
        public async System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)
        {
            RemoteCatalog catalog = await RemoteCatalogClient
                .FetchSharedAsync(System.Threading.CancellationToken.None)
                .ConfigureAwait(false);
            var items = new List<CatalogItem>();
            if (IsPackKind(kind))
                foreach (CatalogPack pack in catalog.Packs)
                    items.Add(new CatalogItem
                    {
                        Id = pack.Id,
                        Name = pack.Name,
                        Group = pack.Group,
                        Description = pack.Description,
                        Bytes = pack.Bytes,
                        Count = pack.Count,
                    });
            else if (IsPetKind(kind))
                foreach (CatalogCompanion pet in catalog.Pets)
                    items.Add(new CatalogItem
                    {
                        Id = pet.Id,
                        Name = pet.Name,
                        Group = pet.Author ?? "",   // pets have no collection; the author is the useful grouping
                        Description = "",
                        Bytes = pet.Bytes,
                        Count = 0,
                    });
            return items;
        }

        public async System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id)
        {
            if (!IsPackKind(kind) && !IsPetKind(kind))
                throw new InvalidDataException("Unknown catalog kind: " + (kind ?? ""));
            RemoteCatalog catalog = await RemoteCatalogClient
                .FetchSharedAsync(System.Threading.CancellationToken.None)
                .ConfigureAwait(false);

            if (IsPetKind(kind))
            {
                CatalogCompanion foundPet = null;
                foreach (CatalogCompanion pet in catalog.Pets)
                    if (string.Equals(pet.Id, id, StringComparison.OrdinalIgnoreCase)) { foundPet = pet; break; }
                if (foundPet == null) throw new InvalidDataException("No catalog pet with id '" + (id ?? "") + "'.");
                // Same verified path the host's own Pets gallery uses, bounded by the pet-XML size limit.
                return await RemoteCatalogClient.DownloadVerifiedAsync(
                    foundPet.Url,
                    foundPet.Sha256,
                    CompanionCatalog.MaximumPetXmlBytes,
                    System.Threading.CancellationToken.None).ConfigureAwait(false);
            }

            CatalogPack found = null;
            foreach (CatalogPack pack in catalog.Packs)
                if (string.Equals(pack.Id, id, StringComparison.OrdinalIgnoreCase)) { found = pack; break; }
            if (found == null) throw new InvalidDataException("No catalog pack with id '" + (id ?? "") + "'.");
            // Re-validates the asset URL and enforces the recorded SHA-256 before returning any bytes.
            return await RemoteCatalogClient.DownloadVerifiedAsync(
                found.Url,
                found.Sha256,
                FortunePackLoadPolicy.MaximumFileBytes,
                System.Threading.CancellationToken.None).ConfigureAwait(false);
        }

        private static bool IsPackKind(string kind)
        {
            return string.Equals(kind, CatalogKinds.Pack, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPetKind(string kind)
        {
            return string.Equals(kind, CatalogKinds.Pet, StringComparison.OrdinalIgnoreCase);
        }

        public bool OpenLink(string moduleId, string httpsUrl)
        {
            try
            {
                if (!ModuleDeclares(moduleId, ModulePermissions.Network)) return false;
                string normalized;
                if (!WebLinks.TryNormalizeHttpsLink(httpsUrl, out normalized)) return false;
                WebLinks.TryOpen(normalized);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The pet inspection/authoring/placement service. Permission-gated on the module's own declared
        /// ModulePermissions.Companions: a module without it gets a refusing instance rather than an exception,
        /// the same way RegisterHotkey hands back a no-op handle. Cached per module id so a module can hold
        /// the reference it is given.
        /// </summary>
        public ICompanionManager GetCompanionManager(string moduleId)
        {
            string key = (moduleId ?? "").Trim();
            ICompanionManager cached;
            if (_petManagers.TryGetValue(key, out cached)) return cached;
            if (!ModuleDeclares(key, ModulePermissions.Companions))
            {
                // NOT CACHED. ModuleDeclares answers from _startUp.LoadedModules, and a module
                // is added to that list AFTER its Init returns (ModuleHost.cs: Init on one line,
                // _loaded.Add on the next). So a module that touches this during Init -- building
                // an options pane, say -- is refused for a reason that stops being true moments
                // later, and caching the refusal made it permanent: the pet dropdown stayed empty
                // for the life of the process however many times it was reopened.
                //
                // Caching the GRANT is still right: it cannot become false, since permissions are
                // fixed at load.
                return new DenyingCompanionManager();
            }
            ICompanionManager manager = new CompanionManagerBridge(_startUp, this);
            _petManagers[key] = manager;
            return manager;
        }
        private readonly Dictionary<string, ICompanionManager> _petManagers =
            new Dictionary<string, ICompanionManager>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The app's effective theme, resolving the user's light/dark/system preference exactly as the
        /// host's own WPF windows do — so a module-owned window agrees with them instead of second-guessing the
        /// OS. Defaults to light if the preference cannot be read; a wrong-but-readable window beats a throw.</summary>
        public bool IsDarkTheme
        {
            get
            {
                try
                {
                    string mode = Program.MyData != null ? Program.MyData.GetThemeMode() : "system";
                    return DesktopAICompanion.Wpf.WpfTheme.EffectiveDark(mode);
                }
                catch { return false; }
            }
        }

        /// <summary>Tag a module's line and drop it in the app's diagnostic log. Best-effort by contract: a
        /// module calling this must never be punished for the log being unavailable.</summary>
        public void Log(string moduleId, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                string id = string.IsNullOrWhiteSpace(moduleId) ? "module" : moduleId.Trim();
                // Log with the module id EXPLICIT rather than letting the file re-parse it out of the
                // "[id] " prefix. This is the single choke point every module's IHost.Log passes through,
                // so it is the one place that knows the id for certain -- which is what makes "log only the
                // module I am working on" exact instead of a guess at a prefix.
                DiagnosticLog.Write(LogCategory.Modules, "info", id, "[" + id + "] " + message);
                StartUp.AddDebugInfoWindowOnly(StartUp.DEBUG_TYPE.info, "[" + id + "] " + message);
            }
            catch { }
        }

        // ---- shared context (host 1.9.0+): a key/value channel between modules that can't reference each other ----
        private readonly Dictionary<string, string> _context = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _contextSync = new object();
        private Action<string> _contextChanged;
        public event Action<string> ContextChanged
        {
            add { _contextChanged += value; Ledger(delegate { _contextChanged -= value; }); }
            remove { _contextChanged -= value; }
        }

        public void PublishContext(string moduleId, string key, string valueJson)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_contextSync) { _context[key] = valueJson ?? ""; }
            // Publishers call this on the UI thread (a module tick), so a synchronous raise delivers to readers
            // on the UI thread too. Per SUBSCRIBER, through RaiseEach (RA-280): one try/catch around the
            // multicast invoke kept the publisher's tick alive but let the first subscriber that threw starve
            // every later one in the invocation list, with no log line, which is the shape RaiseEach's own
            // comment condemns for the other five module-facing events.
            RaiseEach<Action<string>>(_contextChanged, "ContextChanged", h => h(key));
        }

        public string ReadContext(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            lock (_contextSync)
            {
                string v;
                return _context.TryGetValue(key, out v) ? (v ?? "") : "";
            }
        }

        /// <summary>True when the named loaded module declared the capability in its own ModuleInfo. A
        /// module that isn't loaded (or declares nothing) gets nothing — the declaration is the gate.</summary>
        private bool ModuleDeclares(string moduleId, ModulePermissions required)
        {
            if (_startUp == null || string.IsNullOrWhiteSpace(moduleId)) return false;
            foreach (IModule m in _startUp.LoadedModules)
                if (m != null && m.Info != null &&
                    string.Equals(m.Info.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                    return (m.Info.Permissions & required) == required;
            return false;
        }

        public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions)
        {
            try
            {
                var patterns = new List<string>();
                if (extensions != null)
                    foreach (string ext in extensions)
                    {
                        string bare = (ext ?? "").Trim().TrimStart('.', '*');
                        if (bare.Length > 0) patterns.Add("*." + bare);
                    }
                string label = string.IsNullOrWhiteSpace(fileKindLabel) ? "Files" : fileKindLabel.Trim();
                string filter = patterns.Count > 0
                    ? label + " (" + string.Join(";", patterns) + ")|" + string.Join(";", patterns) + "|All files (*.*)|*.*"
                    : "All files (*.*)|*.*";

                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "Choose files" : title.Trim(),
                    Filter = filter,
                    Multiselect = true,
                    CheckFileExists = true,
                };
                bool? picked = dialog.ShowDialog();
                if (picked != true || dialog.FileNames == null) return new List<string>();
                return new List<string>(dialog.FileNames);
            }
            catch { return new List<string>(); }
        }

        public void AddTrayItems(IEnumerable<TrayItem> items)
        {
            if (items == null) return;
            foreach (TrayItem item in items)
            {
                TrayItems.Add(item);
                if (_initLedger != null) _initLedger.Tray.Add(item);   // undone if this Init throws (F344)
            }
        }

        // Which module contributed which pane.
        //
        // BACKLOG.md carried this as "needs a module id on OptionsPane, which is a contract change and
        // therefore a host release". It is not: AddOptionsPane is documented to be called from Init
        // ("---- contributions (register in Init) ----", PluginApi.cs), ModuleHost knows exactly which
        // module it is initialising at that moment, and so the host can record the pair on its own side
        // of the wire. OptionsPane is untouched and no module changes.
        //
        // The shape is the one this class already uses for module-supplied delegates -- see Responder and
        // SpeechResponder above, both of which pair a ModuleId with a callback.
        //
        // Reference equality on purpose: OptionsPane overrides neither Equals nor GetHashCode, and two
        // panes that happen to carry the same Title are still two panes.
        private readonly Dictionary<OptionsPane, string> _paneOwners =
            new Dictionary<OptionsPane, string>((IEqualityComparer<OptionsPane>)ReferenceEqualityComparer.Instance);
        private string _initialisingModuleId;

        /// <summary>
        /// What a module contributed inside its Init, so an Init that THROWS after contributing can be undone
        /// (F344). Without this the loader recorded the failure and unloaded the context while the tray
        /// items, panes, responders, hotkeys and event handlers the module had already registered stayed
        /// live: the Modules pane said "failed to load" and the module kept answering pokes. No in-tree
        /// module can reach it; the isolation promise at the top of ModuleHost is for the ones we do not
        /// own. Entries are removed by IDENTITY, never by count, because AddResponder re-sorts its lists.
        /// </summary>
        private sealed class InitLedger
        {
            public readonly List<TrayItem> Tray = new List<TrayItem>();
            public readonly List<OptionsPane> Panes = new List<OptionsPane>();
            public readonly List<IDisposable> Registrations = new List<IDisposable>();
            public readonly List<Action> Undo = new List<Action>();
        }
        private InitLedger _initLedger;
        private void Ledger(Action undo) { if (_initLedger != null && undo != null) _initLedger.Undo.Add(undo); }

        /// <summary>Set by ModuleHost around a module's Init so contributions made inside it can be
        /// attributed, and recorded for rollback. Null outside that window, which is the honest answer for
        /// a pane registered late.</summary>
        internal void BeginModuleInit(string moduleId) { _initialisingModuleId = moduleId; _initLedger = new InitLedger(); }
        internal void EndModuleInit() { _initialisingModuleId = null; _initLedger = null; }

        /// <summary>Undo everything the module registered inside the Init window that just threw, then close
        /// the window. ModuleHost calls this in place of EndModuleInit when Init did not return.</summary>
        internal void RollBackModuleInit()
        {
            InitLedger ledger = _initLedger;
            string id = _initialisingModuleId;
            _initLedger = null;   // nothing the rollback itself does is recorded
            if (ledger != null)
            {
                foreach (TrayItem t in ledger.Tray) TrayItems.Remove(t);
                foreach (OptionsPane p in ledger.Panes) { OptionsPanes.Remove(p); _paneOwners.Remove(p); }
                foreach (IDisposable r in ledger.Registrations) { try { r.Dispose(); } catch { } }
                foreach (Action undo in ledger.Undo) { try { undo(); } catch { } }
                int undone = ledger.Tray.Count + ledger.Panes.Count + ledger.Registrations.Count + ledger.Undo.Count;
                if (undone > 0)
                    Log(id, "Init threw after contributing; " + undone + " contribution(s) were rolled back so the failed module holds nothing");
            }
            EndModuleInit();
        }

        /// <summary>The id of the module that contributed <paramref name="pane"/>, or null for a pane the
        /// HOST built (Preferences) or one registered outside Init. Callers must treat null as "no
        /// narrowing available" rather than as an error.</summary>
        internal string ModuleOwningPane(OptionsPane pane)
        {
            string id;
            if (pane != null && _paneOwners.TryGetValue(pane, out id)) return id;
            return null;
        }

        public void AddOptionsPane(OptionsPane pane)
        {
            if (pane == null) return;
            OptionsPanes.Add(pane);
            if (!string.IsNullOrEmpty(_initialisingModuleId)) _paneOwners[pane] = _initialisingModuleId;
            if (_initLedger != null) _initLedger.Panes.Add(pane);   // undone if this Init throws (F344)
        }

        // ModuleDataDirectory(string), the public mkdir wrapper over ModuleDataDir, sat here until 2026-09-30
        // (RA-282). Its one caller was PendingModuleRemovals, which F352 moved onto the path-only form below
        // precisely because a removal must not create the folder it is about to delete; nothing else in the
        // tree, tests included, read it, and its summary still described the caller it had lost.

        /// <summary>Where a module's data directory WOULD be, without creating it. ModuleDataDir has a
        /// mkdir side effect, which is wrong for a containment test: asking "is this path inside module
        /// X's folder?" must not bring that folder into existence, least of all while refusing.</summary>
        internal static string ModuleDataDirectoryPath(string moduleId)
        {
            return Path.Combine(AppPaths.DataRoot, "modules", SafeId(moduleId));
        }

        private static string ModuleDataDir(string moduleId)
        {
            string dir = Path.Combine(AppPaths.DataRoot, "modules", SafeId(moduleId));
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
        /// <summary>The folder-name form of a module id, shared with PendingModuleRemovals so a removal deletes
        /// exactly the data directory GetStorage handed out.</summary>
        internal static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "_";
            var sb = new StringBuilder();
            foreach (char c in id) sb.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
            return sb.ToString();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Remover : IDisposable
        {
            private Action _a;
            public Remover(Action a) { _a = a; }
            public void Dispose() { Action a = _a; _a = null; if (a != null) a(); }
        }
        private sealed class ModuleStorage : IModuleStorage
        {
            public ModuleStorage(string dir) { DataDirectory = dir; }
            public string DataDirectory { get; private set; }
        }
        private sealed class ModuleSettings : IModuleSettings
        {
            private readonly string _path;
            private readonly Dictionary<string, string> _d;
            public ModuleSettings(string path) { _path = path; _d = Load(path); }
            public string Get(string key, string fallback) { string v; return _d.TryGetValue(key, out v) ? v : fallback; }
            public int GetInt(string key, int fallback) { string v; int n; return (_d.TryGetValue(key, out v) && int.TryParse(v, out n)) ? n : fallback; }
            public bool GetBool(string key, bool fallback) { string v; bool b; return (_d.TryGetValue(key, out v) && bool.TryParse(v, out b)) ? b : fallback; }
            public void Set(string key, string value) { _d[key] = value ?? ""; }
            public bool Save()
            {
                try { File.WriteAllText(_path, JsonSerializer.Serialize(_d), new UTF8Encoding(false)); return true; }
                catch { return false; }
            }
            private static Dictionary<string, string> Load(string path)
            {
                try { if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>(); }
                catch { }
                return new Dictionary<string, string>();
            }
        }
    }

    /// <summary>Opaque per-pet handle over a FormCompanion, as seen by modules.</summary>
    internal sealed class CompanionHandle : ICompanion
    {
        private readonly FormCompanion _pet;
        public CompanionHandle(FormCompanion pet, int id) { _pet = pet; Id = id; }
        public int Id { get; private set; }
        public bool IsBusy { get { return _pet != null && _pet.IsBusy; } }
        public string TypeId { get { return _pet != null ? _pet.PetTypeId : ""; } }
        internal FormCompanion Pet { get { return _pet; } }
    }

    /// <summary>
    /// ICompanionManager over StartUp's pet orchestration plus the on-disk pet library. The host keeps owning the
    /// persisted mix, the per-pet preferences, the active-pet id and the MAX_SHEEPS cap; this bridge only
    /// exposes the verbs, so the Pets capability can live in a module. Every call is best-effort and never
    /// throws into a module.
    /// </summary>
    internal sealed class CompanionManagerBridge : ICompanionManager
    {
        private readonly StartUp _startUp;
        private readonly CompanionHost _host;
        internal CompanionManagerBridge(StartUp startUp, CompanionHost host) { _startUp = startUp; _host = host; }

        public int MaxCompanions { get { return StartUp.MAX_SHEEPS; } }
        public bool IsAtMax { get { return _startUp != null && _startUp.IsAtMaxPets; } }

        public string CompanionsDirectory
        {
            get { try { return AppPaths.LibraryPetsDirectory ?? ""; } catch { return ""; } }
        }

        public IReadOnlyList<CompanionTypeInfo> InstalledTypes()
        {
            var list = new List<CompanionTypeInfo>();
            try
            {
                foreach (CompanionCatalog.CompanionInfo p in CompanionCatalog.EnumerateLocal())
                    list.Add(new CompanionTypeInfo
                    {
                        TypeId = p.IsBuiltIn ? CompanionCatalog.BuiltInPetId : (p.Id ?? ""),
                        DisplayName = p.DisplayName,
                        IsBuiltIn = p.IsBuiltIn,
                    });
            }
            catch { }
            return list;
        }

        public bool TryReadTypeXml(string typeId, out string animationsXml, out string error)
        {
            animationsXml = null;
            error = null;
            // The base resolver owns the id safety check and the library -> bundled -> built-in lookup, so the
            // module never touches the host's folder layout (and cannot reach the bundled/beside-exe root).
            try { return CompanionCatalog.TryReadPetXml(typeId, out animationsXml, out error); }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public IReadOnlyList<CompanionCount> OnScreenMix()
        {
            var list = new List<CompanionCount>();
            try
            {
                if (_startUp != null)
                    foreach (CompanionCountEntry e in _startUp.OnScreenMix())
                        list.Add(new CompanionCount { TypeId = e.Id ?? "", Count = e.Count });
            }
            catch { }
            return list;
        }

        public bool SpawnOne(string typeId)
        {
            try { return _startUp != null && _startUp.AddPetFromTray(typeId ?? ""); }
            catch { return false; }
        }

        public bool RemoveOne(string typeId)
        {
            try { return _startUp != null && _startUp.RemoveOnePet(typeId ?? ""); }
            catch { return false; }
        }

        public bool ValidateXml(string animationsXml, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(animationsXml)) { error = "No pet XML was supplied."; return false; }
                XmlData.RootNode parsed;
                return CompanionXmlValidator.TryParse(animationsXml, out parsed, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public ICompanionPreview SpawnPreview(string animationsXml, out string error)
        {
            error = null;
            try
            {
                if (_startUp == null) { error = "No pet runtime."; return null; }
                FormCompanion pet = _startUp.SpawnPreviewPet(animationsXml, out error);
                if (pet == null) return null;
                return new PreviewHandle(_startUp, _host, pet);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public bool InstallType(string typeId, string animationsXml, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(typeId) || !SecureDownload.IsSafeId(typeId))
                { error = "Unsafe pet id."; return false; }
                if (string.IsNullOrWhiteSpace(animationsXml)) { error = "No pet data."; return false; }

                // Strip a leading BOM/whitespace so an authored string and a decoded download behave the same.
                string xml = animationsXml.TrimStart('﻿', ' ', '\t', '\r', '\n');
                byte[] bytes = new UTF8Encoding(false).GetBytes(xml);
                if (bytes.Length > CompanionXmlValidator.MaximumXmlBytes) { error = "Pet file too large."; return false; }

                // Never trust the caller: validate structure before anything lands on disk.
                XmlData.RootNode parsed;
                string validationError;
                if (!CompanionXmlValidator.TryParse(xml, out parsed, out validationError))
                { error = validationError; return false; }

                string directory = CompanionProvenance.SafeLibraryDirectory(typeId);
                Directory.CreateDirectory(directory);
                SecureDownload.WriteAllBytesAtomic(Path.Combine(directory, "animations.xml"), bytes);
                // The file under this id is now a DIFFERENT pet (F249, F336): the name the tray shows and
                // the icon and counts the Companions pane shows are cached per id for the process lifetime,
                // and Companion Studio suggests the opened pet's own folder as the install id, so editing
                // an installed pet and pressing Install kept every surface on the old one. Forget reaches
                // all three caches. Then the copies on screen are swapped onto the new definition, as the
                // pane's download already does; Deferred and NeedsRestart are not failures of the install.
                CompanionCatalog.Forget(typeId);
                if (_startUp != null)
                {
                    int reloaded; string reloadError;
                    try { _startUp.ReloadPetType(typeId, out reloaded, out reloadError); } catch { }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public bool UninstallType(string typeId, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(typeId) || !SecureDownload.IsSafeId(typeId))
                { error = "Unsafe pet id."; return false; }
                string directory = CompanionProvenance.SafeLibraryDirectory(typeId);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                CompanionCatalog.Forget(typeId);   // the caches hold a pet that no longer exists (F249)
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // The library containment check is CompanionProvenance.SafeLibraryDirectory, shared with the Companions pane
        // (F337); the copy that sat here had already drifted from the pane's in its message text.
    }

    /// <summary>What a module without ModulePermissions.Companions gets: every verb refuses, nothing throws.</summary>
    internal sealed class DenyingCompanionManager : ICompanionManager
    {
        // "Companions", not "Pets": the flag was renamed by the 1.0.0 rebase and this string is the
        // `out string error` the ABI promises as the REASON, so an author who reads it greps the enum
        // for a member that does not exist. Corrected 2026-09-17.
        private const string Denied = "This module has not declared the Companions permission.";
        public int MaxCompanions { get { return StartUp.MAX_SHEEPS; } }
        public bool IsAtMax { get { return true; } }
        public string CompanionsDirectory { get { return ""; } }
        public IReadOnlyList<CompanionTypeInfo> InstalledTypes() { return new List<CompanionTypeInfo>(); }
        public bool TryReadTypeXml(string typeId, out string animationsXml, out string error) { animationsXml = null; error = Denied; return false; }
        public IReadOnlyList<CompanionCount> OnScreenMix() { return new List<CompanionCount>(); }
        public bool SpawnOne(string typeId) { return false; }
        public bool RemoveOne(string typeId) { return false; }
        public bool ValidateXml(string animationsXml, out string error) { error = Denied; return false; }
        public ICompanionPreview SpawnPreview(string animationsXml, out string error) { error = Denied; return null; }
        public bool InstallType(string typeId, string animationsXml, out string error) { error = Denied; return false; }
        public bool UninstallType(string typeId, out string error) { error = Denied; return false; }
    }

    /// <summary>
    /// A module's handle on one transient preview pet. Holds the FormCompanion directly (not an id) so Remove
    /// targets exactly the pet this module spawned — the tray's remove verb works BY TYPE and could pick a
    /// different pet. Idempotent, and it goes dead by itself if the pet closes for any other reason.
    /// </summary>
    internal sealed class PreviewHandle : ICompanionPreview
    {
        private readonly StartUp _startUp;
        private readonly CompanionHost _host;
        private FormCompanion _pet;

        internal PreviewHandle(StartUp startUp, CompanionHost host, FormCompanion pet)
        {
            _startUp = startUp;
            _host = host;
            _pet = pet;
        }

        public ICompanion Pet
        {
            get
            {
                FormCompanion pet = _pet;
                if (pet == null || pet.IsDisposed) return null;
                return _host != null ? _host.HandleFor(pet) : null;
            }
        }

        public bool IsAlive
        {
            get { FormCompanion pet = _pet; return pet != null && !pet.IsDisposed; }
        }

        public void Remove()
        {
            FormCompanion pet = _pet;
            _pet = null;
            if (pet == null) return;
            try { if (_startUp != null) _startUp.RemovePetInstance(pet); }
            catch { }
        }

        public void Dispose() { Remove(); }
    }
}
