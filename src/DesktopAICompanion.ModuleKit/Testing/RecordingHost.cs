using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ModuleKit.Testing
{
    /// <summary>
    /// A headless <see cref="IHost"/> for module self-tests: it records what a module contributed, lets the
    /// test raise the pet lifecycle events, and stands in for the services a module calls — with no window,
    /// no pet, and no network.
    ///
    /// Every module self-test in this repo was writing its own; this is that host, once. Typical use:
    /// <code>
    /// var host = new RecordingHost();
    /// module.Init(host);
    /// // assert what Init contributed
    /// host.TrayItems.Count == 1;
    /// // then drive behaviour
    /// host.RaiseCompanionPoked(new PokeInfo());
    /// host.SaidLines.Count == 1;
    /// </code>
    ///
    /// <see cref="HostVersion"/> defaults to a very high sentinel so the loader's MinHostVersion gate stays
    /// quiet and a test exercises the module rather than the gate; set it to assert refusal behaviour.
    /// </summary>
    public class RecordingHost : IHost
    {
        // ---- what the module contributed ----
        public List<TrayItem> TrayItems { get; private set; }
        public List<OptionsPane> OptionsPanes { get; private set; }
        public List<string> PlayedAnimations { get; private set; }

        // ---- the recorded lists a module may append to from ANY thread ----
        // Say/SayAll, Log and OpenLink are the IHost verbs a module reaches from a pool task (Remembrance
        // logs the end of a stop from its capture thread; a background probe's continuation may speak), while
        // the test reads on its own thread. List<T> is safe for neither: an append during a foreach throws,
        // and a Count read during a growth can see the new length before the item. So these five are
        // recorded under ONE lock and HANDED OUT AS SNAPSHOTS: every property read is a copy taken under
        // that lock. The type stays List<string>, so every assertion a module self-test already makes
        // (Count, the indexer, Contains, foreach, LINQ) compiles unchanged, and a copy taken before an
        // append keeps its count, which is what lets a test hold a stable view. What no longer works is
        // MUTATING the list a getter returns -- host.OpenedLinks.Clear() cleared a copy -- so the Clear*
        // methods below are the reset between phases. Until 2026-09-30 the Remembrance stop-path checks
        // worked around the race with a queueing SynchronizationContext and by ordering the pool task's
        // log line before it released the waiter, a fix in the test rather than in the fake (N-remembrance-01).
        private readonly object _recordSync = new object();
        private readonly List<string> _saidLines = new List<string>();
        private readonly List<string> _loggedLines = new List<string>();
        private readonly List<string> _openedLinks = new List<string>();
        private readonly List<string> _broadcastLines = new List<string>();
        private readonly List<KeyValuePair<ICompanion, string>> _saidToCompanions =
            new List<KeyValuePair<ICompanion, string>>();

        /// <summary>Every line, targeted or broadcast, in order (a snapshot; see above).</summary>
        public List<string> SaidLines { get { lock (_recordSync) return new List<string>(_saidLines); } }
        /// <summary>Links the module asked to open (a snapshot; see above).</summary>
        public List<string> OpenedLinks { get { lock (_recordSync) return new List<string>(_openedLinks); } }
        /// <summary>Everything the module logged, as "&lt;moduleId&gt;: &lt;message&gt;" — assert on it instead of
        /// making the pet speak diagnostics (a snapshot; see above).</summary>
        public List<string> LoggedLines { get { lock (_recordSync) return new List<string>(_loggedLines); } }

        /// <summary>Forget every recorded line: the targeted, the broadcast and the union.</summary>
        public void ClearSaidLines()
        {
            lock (_recordSync) { _saidLines.Clear(); _saidToCompanions.Clear(); _broadcastLines.Clear(); }
        }

        /// <summary>Forget every recorded log line.</summary>
        public void ClearLoggedLines() { lock (_recordSync) _loggedLines.Clear(); }

        /// <summary>Forget every recorded link, so a second press can be asserted on its own.</summary>
        public void ClearOpenedLinks() { lock (_recordSync) _openedLinks.Clear(); }

        public List<Func<bool>> DropResponders { get; private set; }
        public List<Func<bool>> PokeResponders { get; private set; }
        /// <summary>Pet-aware responders (host 1.5.0+), kept separately from the legacy pair so a test can see
        /// which style the module registered. RaiseDrop/RaisePokeResponders run both.</summary>
        public List<Func<ICompanion, bool>> CompanionDropResponders { get; private set; }
        public List<Func<ICompanion, bool>> CompanionPokeResponders { get; private set; }
        /// <summary>Every targeted line and the pet it went to. This is how you assert a reaction reached ONE
        /// pet rather than all of them (a snapshot; see the recorded lists above).</summary>
        public List<KeyValuePair<ICompanion, string>> SaidToCompanions
        {
            get { lock (_recordSync) return new List<KeyValuePair<ICompanion, string>>(_saidToCompanions); }
        }
        /// <summary>Lines sent via SayAll. Should be rare: announcements to the user, not pet reactions (a
        /// snapshot; see the recorded lists above).</summary>
        public List<string> BroadcastLines { get { lock (_recordSync) return new List<string>(_broadcastLines); } }
        /// <summary>Backs IsCompanionAlive. Null means every non-null pet is alive.</summary>
        public Func<ICompanion, bool> CompanionAlivePredicate { get; set; }
        /// <summary>Audio buffers your module handed to PlaySound.</summary>
        public List<byte[]> PlayedSounds { get; private set; }
        /// <summary>Module ids passed to StopSound.</summary>
        public List<string> StoppedSoundOwners { get; private set; }
        /// <summary>What PlaySound returns; set false to drive the "nothing will be heard" branch.</summary>
        public bool PlaySoundResult { get; set; }
        public List<Func<SpeechRequest, bool>> SpeechResponders { get; private set; }
        /// <summary>Lines a responder handed back through SpeechRequest.ShowBubble.</summary>
        public List<string> ShownBubbles { get; private set; }
        public List<string> RegisteredHotkeys { get; private set; }

        // ---- what the host hands back (set these to steer a test) ----
        public string HostVersion { get; set; }
        public bool SpeechEnabled { get; set; }
        public double Volume { get; set; }
        public string OwnerName { get; set; }
        /// <summary>Set this to assert your window themes both ways without touching the machine's OS setting.</summary>
        public bool IsDarkTheme { get; set; }
        public ScreenContext ScreenContextValue { get; set; }
        public ICompanionManager CompanionManager { get; set; }
        public IReadOnlyList<string> PickedFiles { get; set; }
        public Dictionary<string, List<CatalogItem>> CatalogItems { get; private set; }
        public Dictionary<string, byte[]> CatalogPayloads { get; private set; }
        /// <summary>Context values a module published via PublishContext, keyed by key, for assertions.</summary>
        public Dictionary<string, string> PublishedContext { get; private set; }

        private readonly Dictionary<string, FakeModuleSettings> _settings =
            new Dictionary<string, FakeModuleSettings>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IModuleStorage> _storage =
            new Dictionary<string, IModuleStorage>(StringComparer.OrdinalIgnoreCase);

        public RecordingHost()
        {
            TrayItems = new List<TrayItem>();
            OptionsPanes = new List<OptionsPane>();
            PlayedAnimations = new List<string>();
            DropResponders = new List<Func<bool>>();
            PokeResponders = new List<Func<bool>>();
            CompanionDropResponders = new List<Func<ICompanion, bool>>();
            CompanionPokeResponders = new List<Func<ICompanion, bool>>();
            PlayedSounds = new List<byte[]>();
            StoppedSoundOwners = new List<string>();
            SpeechResponders = new List<Func<SpeechRequest, bool>>();
            ShownBubbles = new List<string>();
            PlaySoundResult = true;
            RegisteredHotkeys = new List<string>();
            CatalogItems = new Dictionary<string, List<CatalogItem>>(StringComparer.OrdinalIgnoreCase);
            CatalogPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            PublishedContext = new Dictionary<string, string>(StringComparer.Ordinal);

            // High enough that no realistic MinHostVersion refuses the module under test.
            HostVersion = "9999.0.0";
            SpeechEnabled = true;
            Volume = 0.5;
            OwnerName = "";
            CompanionManager = new DenyingCompanionManager();
            PickedFiles = new List<string>();
            ScreenContextValue = new ScreenContext
            {
                WindowTitle = "",
                ProcessName = "",
                MonitorBounds = new PixelRect(0, 0, 1920, 1080),
            };
        }

        /// <summary>Give this module a real temp data directory. The caller owns disposal.</summary>
        public void UseStorage(string moduleId, IModuleStorage storage)
        {
            if (moduleId == null) return;
            _storage[moduleId] = storage;
        }

        /// <summary>The settings this module has been reading and writing, for assertions.</summary>
        public FakeModuleSettings SettingsFor(string moduleId)
        {
            FakeModuleSettings settings;
            string key = moduleId ?? "";
            if (!_settings.TryGetValue(key, out settings))
                _settings[key] = settings = new FakeModuleSettings();
            return settings;
        }

        // ---- events, and the way a test raises them ----
        public event Action<ICompanion> CompanionSpawned;
        public event Action<PokeInfo> CompanionPoked;
        public event Action<ICompanion> CompanionLanded;
        public event Action HostShutdown;

        public void RaiseCompanionSpawned(ICompanion pet) { Action<ICompanion> h = CompanionSpawned; if (h != null) h(pet); }
        public void RaiseCompanionPoked(PokeInfo poke) { Action<PokeInfo> h = CompanionPoked; if (h != null) h(poke); }
        public void RaiseCompanionLanded(ICompanion pet) { Action<ICompanion> h = CompanionLanded; if (h != null) h(pet); }
        public void RaiseHostShutdown() { Action h = HostShutdown; if (h != null) h(); }

        // ---- responder arbitration, the way CompanionHost does it ----
        // ONE list per chain holding BOTH registration styles, sorted highest priority first with the
        // registration order as the tie-break, which is CompanionHost.SortResponders exactly. Until
        // 2026-09-29 the four public lists above were also the arbitration: the priority argument was
        // discarded and every legacy registration ran before any pet-aware one, while the doc comment
        // said "as the host arbitrates them" (F233). The public lists stay and are still appended to,
        // because they are ModuleKit surface a test may read; they are no longer what Raise* walks.
        private sealed class Responder
        {
            public int Priority;
            public int Seq;
            public Func<ICompanion, bool> OnFire;
        }
        private readonly List<Responder> _dropChain = new List<Responder>();
        private readonly List<Responder> _pokeChain = new List<Responder>();
        private int _nextResponderSeq;

        private void AddToChain(List<Responder> chain, int priority, Func<ICompanion, bool> onFire)
        {
            if (onFire == null) return;
            chain.Add(new Responder { Priority = priority, Seq = _nextResponderSeq++, OnFire = onFire });
            // List.Sort is not stable, so the tie-break is explicit rather than assumed.
            chain.Sort(delegate(Responder x, Responder y)
            {
                int byPriority = y.Priority.CompareTo(x.Priority);
                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);
            });
        }

        private static bool RaiseChain(List<Responder> chain, ICompanion pet)
        {
            // Over a copy: a responder may register or dispose another while it runs.
            foreach (Responder responder in new List<Responder>(chain))
                if (responder.OnFire(pet)) return true;
            return false;
        }

        /// <summary>Run the registered drop responders as the host arbitrates them -- highest priority
        /// first, registration order on ties, both registration styles in ONE order -- and return true once
        /// one claims the drop. A test does not have to know which style the module chose.</summary>
        public bool RaiseDrop() { return RaiseDrop(null); }

        /// <summary>As <see cref="RaiseDrop()"/>, but naming the pet the drop belongs to.</summary>
        public bool RaiseDrop(ICompanion pet) { return RaiseChain(_dropChain, pet); }

        /// <summary>Run the registered poke responders as the host arbitrates them (see
        /// <see cref="RaiseDrop()"/>); true once one speaks.</summary>
        public bool RaisePokeResponders() { return RaisePokeResponders(null); }

        /// <summary>As <see cref="RaisePokeResponders()"/>, but naming the pet that was poked.</summary>
        public bool RaisePokeResponders(ICompanion pet) { return RaiseChain(_pokeChain, pet); }

        // ---- IHost services ----
        public void SetOwnerName(string name) { OwnerName = name ?? ""; }

        /// <summary>
        /// Every line, targeted or broadcast, in order. Kept as the union so existing tests keep working.
        /// To assert that a line went to ONE pet rather than to all of them, use <see cref="SaidToCompanions"/> and
        /// <see cref="BroadcastLines"/> -- Say and SayAll both wrote only here before, which made the
        /// difference between routing and broadcasting impossible to test at all.
        /// </summary>
        public void Say(ICompanion pet, string text)
        {
            lock (_recordSync)
            {
                _saidLines.Add(text ?? "");
                _saidToCompanions.Add(new KeyValuePair<ICompanion, string>(pet, text ?? ""));
            }
        }

        public void SayAll(string text)
        {
            lock (_recordSync)
            {
                _saidLines.Add(text ?? "");
                _broadcastLines.Add(text ?? "");
            }
        }

        // Styled overloads record identically to the plain ones (the style is a render-only concern the fake
        // does not paint); tests that assert on spoken text keep working unchanged.
        public void Say(ICompanion pet, string text, DesktopAICompanion.Modules.SpeechStyle style) { Say(pet, text); }
        public void SayAll(string text, DesktopAICompanion.Modules.SpeechStyle style) { SayAll(text); }

        public bool TryPlayAnimation(ICompanion pet, string animationName)
        {
            PlayedAnimations.Add(animationName ?? "");
            return true;
        }

        public void PlayAnimationAll(IReadOnlyList<string> animationCandidates)
        {
            if (animationCandidates == null) return;
            foreach (string candidate in animationCandidates) PlayedAnimations.Add(candidate ?? "");
        }

        public ScreenContext CaptureScreenContext(ICompanion pet) { return ScreenContextValue; }

        public IDisposable RegisterHotkey(string combo, Action onPressed)
        {
            RegisteredHotkeys.Add(combo ?? "");
            return new NoopDisposable();
        }

        public IModuleStorage GetStorage(string moduleId)
        {
            IModuleStorage storage;
            return _storage.TryGetValue(moduleId ?? "", out storage) ? storage : null;
        }

        public IModuleSettings GetSettings(string moduleId) { return SettingsFor(moduleId); }

        // The legacy pair is wrapped as `pet => f()` into the same chain as the pet-aware pair, exactly as
        // CompanionHost does, so one priority order governs both styles.
        public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop)
        {
            DropResponders.Add(onDrop);
            if (onDrop != null) AddToChain(_dropChain, priority, delegate(ICompanion pet) { return onDrop(); });
            return new NoopDisposable();
        }

        public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke)
        {
            PokeResponders.Add(onPoke);
            if (onPoke != null) AddToChain(_pokeChain, priority, delegate(ICompanion pet) { return onPoke(); });
            return new NoopDisposable();
        }

        public IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop)
        {
            CompanionDropResponders.Add(onDrop);
            AddToChain(_dropChain, priority, onDrop);
            return new NoopDisposable();
        }

        public IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke)
        {
            CompanionPokeResponders.Add(onPoke);
            AddToChain(_pokeChain, priority, onPoke);
            return new NoopDisposable();
        }

        /// <summary>Answers <see cref="CompanionAlivePredicate"/>; alive by default. Set the predicate to prove your
        /// module drops work whose pet went away instead of redirecting it to a different pet.</summary>
        public bool IsCompanionAlive(ICompanion pet)
        {
            if (pet == null) return false;
            Func<ICompanion, bool> predicate = CompanionAlivePredicate;
            return predicate == null || predicate(pet);
        }

        /// <summary>Answers <see cref="IHost.IsFullscreenActive"/>; false by default, because a test machine
        /// is not running a game. Prefer <see cref="RaiseFullscreenChanged"/> to flip it, so subscribers see
        /// the transition the way they would in the app.</summary>
        public bool IsFullscreenActive { get; set; }

        /// <summary>Raised by <see cref="RaiseFullscreenChanged"/>.</summary>
        public event Action<bool> FullscreenChanged;

        /// <summary>
        /// Flip the fullscreen state and notify subscribers, exactly as the host does when a game starts or
        /// exits. Use this to prove your module RELEASES whatever it is holding when a game appears -- the
        /// interesting behaviour is the transition, not the steady state.
        /// </summary>
        public void RaiseFullscreenChanged(bool active)
        {
            IsFullscreenActive = active;
            Action<bool> handler = FullscreenChanged;
            if (handler != null) handler(active);
        }

        /// <summary>Records the audio your module tried to play. Set <see cref="PlaySoundResult"/> to false to
        /// exercise the refused path -- no device, no permission, muted -- which is the branch that decides
        /// whether your module falls back to a bubble.</summary>
        /// <summary>
        /// Permissions to enforce on the gated calls, or null to enforce nothing.
        ///
        /// OPT-IN, and null by default, so no existing module self-test changes behaviour.
        /// It exists because the un-enforcing default hid a real defect: AgentFlow called
        /// PlayNotificationSound without declaring Audio, the real host refused it on every
        /// invocation, and the self-test still passed because this double counted the call
        /// and returned success. A double that cannot refuse cannot test a gate.
        ///
        /// Set it from the module under test's own Info.Permissions -- not from a literal --
        /// or the test asserts what the test author believed rather than what ships.
        ///
        /// The gates it covers, each mirroring CompanionHost: Audio on PlaySound and
        /// PlayNotificationSound; Network on OpenLink (refused, nothing recorded); Voice on the
        /// speech responders (skipped at raise time, as the host skips a responder whose module
        /// lacks it); Companions on GetCompanionManager (the denying manager). Until 2026-09-29
        /// only the two Audio verbs consulted it, so the double could not fail on a missing
        /// Network, Voice or Companions flag while its own comment said it enforced "the gated
        /// calls" (F234).
        /// </summary>
        public ModulePermissions? Declared { get; set; }

        private bool Refuses(ModulePermissions required)
        {
            return Declared.HasValue && (Declared.Value & required) != required;
        }

        public bool PlaySound(string moduleId, byte[] audio, double volume)
        {
            if (Refuses(ModulePermissions.Audio)) return false;
            PlayedSounds.Add(audio ?? new byte[0]);
            return PlaySoundResult;
        }

        /// <summary>Counted, not recorded as bytes: the audio belongs to the host, so there
        /// is nothing module-side to capture. A module self-test asserts it was ASKED.</summary>
        public int NotificationSoundsPlayed { get; private set; }

        public bool PlayNotificationSound(string moduleId)
        {
            if (Refuses(ModulePermissions.Audio)) return false;
            NotificationSoundsPlayed++;
            return PlaySoundResult;
        }

        public bool StopSound(string moduleId)
        {
            StoppedSoundOwners.Add(moduleId ?? "");
            return true;
        }

        public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech)
        {
            SpeechResponders.Add(onSpeech);
            return new NoopDisposable();
        }

        /// <summary>Offer an utterance to the registered speech responders, as the host does. Returns true
        /// when one claimed it AND asked to suppress the bubble. Any ShowBubble call is recorded in
        /// <see cref="ShownBubbles"/>, so you can assert the no-silent-loss path.</summary>
        public bool RaiseSpeechRequest(string text, ICompanion pet)
        {
            var request = new SpeechRequest
            {
                Text = text,
                Pet = pet,
                ShowBubble = seconds => ShownBubbles.Add(text ?? ""),
            };
            // The host skips a responder whose module has not declared Voice, at raise time; this double
            // models one module, so the whole offer is skipped when that module lacks it.
            if (Refuses(ModulePermissions.Voice)) return false;
            foreach (Func<SpeechRequest, bool> responder in SpeechResponders)
                if (responder != null && responder(request)) return request.SuppressBubble;
            return false;
        }

        public Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)
        {
            List<CatalogItem> items;
            if (!CatalogItems.TryGetValue(kind ?? "", out items)) items = new List<CatalogItem>();
            return Task.FromResult((IReadOnlyList<CatalogItem>)items);
        }

        public Task<byte[]> DownloadCatalogItemAsync(string kind, string id)
        {
            byte[] payload;
            if (!CatalogPayloads.TryGetValue((kind ?? "") + "/" + (id ?? ""), out payload)) payload = new byte[0];
            return Task.FromResult(payload);
        }

        public ICompanionManager GetCompanionManager(string moduleId)
        {
            // The host hands a module without Companions the denying bridge rather than a null.
            if (Refuses(ModulePermissions.Companions)) return new DenyingCompanionManager();
            return CompanionManager;
        }

        public void Log(string moduleId, string message)
        {
            lock (_recordSync) _loggedLines.Add((moduleId ?? "") + ": " + (message ?? ""));
        }

        public event Action<string> ContextChanged;

        public void PublishContext(string moduleId, string key, string valueJson)
        {
            PublishedContext[key ?? ""] = valueJson ?? "";
            Action<string> handler = ContextChanged;
            if (handler != null) handler(key ?? "");
        }

        public string ReadContext(string key)
        {
            string v;
            return PublishedContext.TryGetValue(key ?? "", out v) ? v : "";
        }

        public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions)
        {
            return PickedFiles ?? new List<string>();
        }

        public bool OpenLink(string moduleId, string httpsUrl)
        {
            // Refused BEFORE recording, as the host refuses before it does anything.
            if (Refuses(ModulePermissions.Network)) return false;
            lock (_recordSync) _openedLinks.Add(httpsUrl ?? "");
            return true;
        }

        public void AddTrayItems(IEnumerable<TrayItem> items)
        {
            if (items != null) TrayItems.AddRange(items);
        }

        public void AddOptionsPane(OptionsPane pane)
        {
            if (pane != null) OptionsPanes.Add(pane);
        }

        /// <summary>
        /// Never called. It exists so the compiler sees every declared event as USED: an event with no
        /// raiser is CS0067, and this repo builds with warnings-as-errors, so a fake host that only
        /// declares the interface's events fails to compile without something like this.
        /// </summary>
        internal void TouchEvents()
        {
            RaiseCompanionSpawned(null);
            RaiseCompanionPoked(null);
            RaiseCompanionLanded(null);
            RaiseHostShutdown();
        }
    }
}
