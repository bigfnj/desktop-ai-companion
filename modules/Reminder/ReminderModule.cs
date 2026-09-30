using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit;
using DesktopAICompanion.ModuleKit.Testing;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// The pet reads one or more calendar feeds and announces each event a few minutes before it starts. Up to
    /// <see cref="MaxSlots"/> feeds are configured independently (each a Local file, a Calendar URL / ICS, or the
    /// running desktop Outlook), and each carries its OWN name and speech style, so a Home event and a Work event
    /// can look different in the bubble. A single UI-thread WinForms timer polls the aggregated feed, fires any
    /// due reminders through <see cref="IHost.SayAll"/> in that feed's style, and remembers which fired so a
    /// restart never re-nags.
    /// </summary>
    public sealed class ReminderModule : IModule
    {
        internal const string Id = "reminder";
        private const int DefaultLeadMinutes = 5;
        private const int TickMilliseconds = 20 * 1000;
        private const int MaxSlots = 5;

        // Per-slot feed types. "Off" is a real choice so a slot can be parked without deleting its settings.
        private const string SourceOff = "Off";
        private const string SourceLocalFile = "Local file";
        private const string SourceCalendarUrl = "Calendar URL (ICS)";
        private const string SourceOutlook = "Local Outlook";

        private static readonly string[] LegacyStyleKeys =
        {
            SpeechStyleSettings.FontKey, SpeechStyleSettings.SizeKey, SpeechStyleSettings.BoldKey,
            SpeechStyleSettings.ItalicKey, SpeechStyleSettings.UnderlineKey, SpeechStyleSettings.ColorKey,
        };

        private IHost _host;
        private IModuleSettings _settings;
        // Pets seen this run, OLDEST FIRST -- the order is load-bearing, see ResolveSpeaker. There is no
        // CompanionRemoved event, so IHost.IsCompanionAlive is the only honest liveness answer; asking it
        // lazily at speak time was not enough on its own, because the dead entries were walked past and left
        // behind, so the list was bounded by spawns-per-session rather than by the pets on screen. PruneDeadPets
        // sweeps it, and both the places that touch the list go through it.
        private readonly List<ICompanion> _seenPets = new List<ICompanion>();
        private Action<ICompanion> _petSpawned;
        // Label -> pet type id for the per-calendar speaker dropdown, rebuilt whenever the pane loads.
        private Dictionary<string, string> _speakerLabelToType =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> _speakerTypeToLabel =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly SettingField[] _speakerFields = new SettingField[MaxSlots + 1];
        private ICalendarSource _source;
        private System.Windows.Forms.Timer _timer;
        private EventHandler _tickHandler;
        private readonly HashSet<string> _fired = new HashSet<string>(StringComparer.Ordinal);
        private CalendarSnapshot _lastSnapshot;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = Id,
            Name = "Reminder",
            Version = "1.0.7",  // 1.0.7: the 2026-09-29 audit's eleven Reminder findings. A refresh that
                                //        never returns no longer freezes its slot (deadline, one retry,
                                //        a cap on parked attempts); an .ics download is bounded in time
                                //        and size; a reminder due while no companion is on screen is held
                                //        rather than spent; an Apply keeps the slots whose type it did not
                                //        change; the fired set is pruned per slot; "Check now" re-reads
                                //        the feeds; a custom chime is read off the UI thread; a feed error
                                //        is logged on change, not per tick; Teams' short join links match.
                                //        Round 2 (R-043): a slot's cached and last-good events belong to the
                                //        URL or file that produced them, so an edit to a target that fails
                                //        shows the loading state and then that target's error, never the
                                //        previous calendar's events.
                                //        "Test this reminder" and the Agenda tray click say so when no
                                //        companion is on screen instead of reporting a send nobody saw.
                                //        Lane fix/deadcode, same version: one HH:mm parser (QuietHours') serves
                                //        quiet hours, the briefing time and typed reminders; the chime size cap
                                //        is one constant; one slot lookup serves the four per-slot settings; the
                                //        ICS parser has a self-check; the path-less Chime.Play and QuietHours'
                                //        unused instance API are gone (F187, F190, F195, F198, F202, F203).
                                // 1.0.6: "Make the companion react" reached 35 of 54 companions, and
                                //        reactOn defaults to true, so 19 users had a feature switched
                                //        on that did nothing. The eSheep-era names it used are absent
                                //        from the converted shimeji. Historical four kept FIRST and in
                                //        order, so nothing that worked changes; now 43/54.   // 1.0.5: ReminderScheduler.DueNow deleted. No callers, and it tested the fired
                                 //        set with a bare event id while DueNowMulti records "<id>@<lead>" --
                                 //        so reviving it on the strength of its signature would have produced a
                                 //        scheduler that re-fired every event on every tick.
                                 // 1.0.4: the local JSON feed is read off the UI thread, and a failed refresh
                                 //        keeps the last good feed instead of blanking it.
                                 // 1.0.3: a restart no longer re-nags. The fired-event set was pruned
                                 //        against the feed BEFORE any check on snap.Error, and an empty feed
                                 //        is routine: CachingCalendarSource returns no events with
                                 //        "Loading the calendar" on its first call, and Init calls CheckDue
                                 //        directly. So the set was wiped and written to disk on every launch,
                                 //        and a meeting still inside its lead window announced a second time
                                 //        with chime, animation and bubble. The module header has always
                                 //        promised the opposite.
                                 // 1.0.1: republished so the bundled ModuleKit.dll no longer carries the
                                 //        maintainer's absolute build path (Contracts + ModuleKit moved to
                                 //        DebugType=embedded). NO functional change here; the bump exists
                                 //        because the catalog offers an update by VERSION, so without it the
                                 //        cleaned payload would only ever reach new installs.
                                 // 1.0.0: rebased with the host for the Desktop AI Companion rename. Not a
                                 //        rollback -- the previous line below is the higher number, and
                                 //        every module restarts its numbering here alongside the app.
                                 // 1.8.1: payload refresh only -- the bundled ModuleKit gained the fullscreen
                                 //        test double (host 1.9.9).
                                 // 1.8.0: NEW: a per-calendar "Reminder companion" -- pick WHICH pet announces each
                                 //        calendar, offered only from the pets actually on screen. Needed no ABI
                                 //        change: IHost.Say(pet, ...) and IsCompanionAlive have existed since 1.5.0.
                                 //        When the chosen pet is not out, the reminder still speaks through the
                                 //        app's default speaker rather than being swallowed.
                                 // 1.7.1: each tray entry gets its own icon (agenda / reminder / meeting),
                                 //        per the project convention that no tray row is icon-less.
                                 // 1.7.0: the pet physically REACTS when a reminder fires -- an attention
                                 //        animation, not just a bubble and a chime. Needed no host change:
                                 //        IHost.PlayAnimationAll has existed since the emotion work, and the
                                 //        module owns the candidate list the way AiBrain owns its emotion map.
                                 // 1.6.0: captures the invited roster and publishes the current event to the
                                 //        host shared-context channel ("meeting.current") so the Remembrance
                                 //        module can auto-name a recording and seed its attendance.
                                 // 1.5.0: join-the-meeting links + a Join tray entry; on-demand "today's agenda";
                                 //        a daily morning briefing; skip declined / all-day; a per-slot Test
                                 //        button; typed personal reminders (one-off + recurring); and hush while
                                 //        presenting or in Do Not Disturb.
                                 // 1.4.1: a per-calendar "play a chime" checkbox, so one calendar can be silent
                                 //        while another sounds; the global chime switch is the master over all.
                                 // 1.4.0: per-calendar chime -- each slot can Browse for its own WAV/MP3 sound
                                 //        (blank = the built-in chime); the global chime switch stays the master.
                                 // 1.3.0: up to 5 independent calendar feeds, each with its OWN name and speech
                                 //        style (font/size/colour/bold/italic/underline); the single "source" a
                                 //        1.2.x user had is migrated into slot 1.
                                 // 1.2.0: multiple lead times (e.g. 15 & 5), quiet hours, an optional chime,
                                 //        the event location in the announcement, and module-owned speech styling
                                 // 1.1.0: pluggable calendar sources -- Calendar URL (ICS: Google secret .ics /
                                 //        published Outlook/M365 / iCloud) + Local Outlook (COM) beside the
                                 //        local-file corporate feed; Network permission for the URL fetch
            // Publishing to the shared-context channel (IHost.PublishContext) needs host 1.9.0; styled speech
            // (1.8.0) and PlaySound (1.6.0) are older. 1.9.0 is the floor.
            MinHostVersion = "1.0.0",
            // Animation is declared for the reaction added in 1.7.0. The host does not actually gate
            // PlayAnimationAll on it (only Audio, Voice, Network and Companions are enforced in CompanionHost), but the pre-install
            // consent list is built from THIS field, so leaving it off would under-disclose what the module
            // does to the user's pets. Declare what you use.
            Permissions = ModulePermissions.Speech | ModulePermissions.Storage | ModulePermissions.Network | ModulePermissions.Companions
                | ModulePermissions.Audio | ModulePermissions.Animation,
        };

        public void Init(IHost host)
        {
            _host = host;
            // A host may decline to hand out a settings store (the app's own --module-selftest harness returns
            // null), and MigrateLegacy reads settings immediately, so an unguarded null took the whole module
            // down with a NullReferenceException at load. Degrading matches the ABI's convention for every
            // other refused service. See ModuleKit.MemoryModuleSettings.
            _settings = host.GetSettings(Id) ?? new MemoryModuleSettings();
            MigrateLegacy();
            _source = BuildSource();
            LoadFired();

            host.AddOptionsPane(BuildOptionsPane());
            host.AddTrayItems(new[] { BuildTrayItem(), BuildJoinTrayItem(), BuildAgendaTrayItem() });

            // Remember the pets as they appear, so a calendar aimed at one of them has a handle to speak
            // through. There is no CompanionRemoved event, which is exactly why IHost.IsCompanionAlive exists --
            // and the sweep happens HERE as well as at speak time, because a spawn is the only thing that grows
            // the list and a session that never fires a reminder would otherwise never prune it.
            _petSpawned = delegate(ICompanion pet)
            {
                PruneDeadPets();
                if (pet != null) _seenPets.Add(pet);
            };
            host.CompanionSpawned += _petSpawned;

            // WinForms timer: its Tick fires on the UI thread the host called Init on, so SayAll is on the
            // right thread with no marshaling. First tick soon so an imminent event isn't missed at startup.
            _timer = new System.Windows.Forms.Timer { Interval = TickMilliseconds };
            _tickHandler = delegate { CheckDue(); };
            _timer.Tick += _tickHandler;
            _timer.Start();
            CheckDue();
        }

        public void Shutdown()
        {
            if (_timer != null)
            {
                try
                {
                    _timer.Stop();
                    if (_tickHandler != null) _timer.Tick -= _tickHandler;
                    _timer.Dispose();
                }
                catch { }
                _timer = null;
                _tickHandler = null;
            }
            // Drop the pet subscription and the handles with it: a module that stays wired to CompanionSpawned after
            // shutdown keeps every pet it ever saw alive in this list, and gets called after Init's state is gone.
            if (_petSpawned != null && _host != null)
            {
                try { _host.CompanionSpawned -= _petSpawned; } catch { }
                _petSpawned = null;
            }
            _seenPets.Clear();
        }

        // Carry a 1.2.x single-source config into slot 1 exactly once, so an existing user's feed + style survive
        // the upgrade. Keyed on a marker so it never re-runs and never stomps a slot the user has since edited.
        private void MigrateLegacy()
        {
            if (string.Equals(_settings.Get("migratedSlots", ""), "1", StringComparison.Ordinal)) return;
            string legacySource = _settings.Get("source", "");
            if (!string.IsNullOrEmpty(legacySource))
            {
                _settings.Set(SlotKey(1, "type"), legacySource);
                _settings.Set(SlotKey(1, "url"), _settings.Get("url", ""));
                _settings.Set(SlotKey(1, "file"), _settings.Get("file", ""));
                foreach (string k in LegacyStyleKeys)
                {
                    string val = _settings.Get(k, "");
                    if (!string.IsNullOrEmpty(val)) _settings.Set(SlotId(1) + "." + k, val);
                }
            }
            _settings.Set("migratedSlots", "1");
            _settings.Save();
        }

        // One source instance per slot, kept across Apply. An Apply used to call BuildSource unconditionally and
        // BuildSlotSource constructed a fresh IcsUrlSource / OutlookComSource / LocalJsonSource every time, so
        // toggling a chime checkbox threw away every slot's cache AND its last-good list: the next tick served
        // "Fetching the calendar…" with no events (the tray read "nothing upcoming", Join went blank), every URL
        // slot re-downloaded, every Outlook slot re-enumerated 48 hours with Body and Recipients per item, and an
        // Apply while a share or the network was down replaced retained events with nothing until it came back
        // (F200). The comment here said "only a feed-TYPE change does, which is why this is re-run on Save", and
        // the code did the opposite of the first half. Only a slot's TYPE needs a new instance; a URL or file edit
        // already reaches the existing one through its live getter, and RefreshKey kicks an early refresh. The
        // rebuild had one accidental virtue -- it was the only way a slot whose refresh never returned came back
        // to life -- and CachingCalendarSource's deadline is now that recovery, which is why both land in 1.0.7.
        private readonly ICalendarSource[] _slotSources = new ICalendarSource[MaxSlots + 1];
        private readonly string[] _slotTypes = new string[MaxSlots + 1];

        // Build the aggregated source from the saved slots, keeping every slot whose type is unchanged. Labels are
        // re-read here, so a renamed calendar takes effect on the next tick without touching its source.
        private ICalendarSource BuildSource()
        {
            var slots = new List<AggregateCalendarSource.Slot>();
            for (int i = 1; i <= MaxSlots; i++)
            {
                string type = _settings.Get(SlotKey(i, "type"), SourceOff);
                if (!string.Equals(type, _slotTypes[i], StringComparison.Ordinal))
                {
                    _slotSources[i] = BuildSlotSource(i, type);
                    _slotTypes[i] = type;
                }
                ICalendarSource src = _slotSources[i];
                if (src == null) continue;
                slots.Add(new AggregateCalendarSource.Slot
                {
                    Id = SlotId(i),
                    Label = _settings.Get(SlotKey(i, "label"), ""),
                    Source = src,
                });
            }
            return new AggregateCalendarSource(slots);
        }

        // `i` is a method parameter (fresh per call), so capturing it in the getter is safe -- no for-loop capture trap.
        private ICalendarSource BuildSlotSource(int i, string type)
        {
            if (string.Equals(type, SourceCalendarUrl, StringComparison.Ordinal))
                return new IcsUrlSource(() => _settings.Get(SlotKey(i, "url"), ""));
            if (string.Equals(type, SourceOutlook, StringComparison.Ordinal))
                return new OutlookComSource();
            if (string.Equals(type, SourceLocalFile, StringComparison.Ordinal))
                return new LocalJsonSource(() => _settings.Get(SlotKey(i, "file"), ""));
            return null;   // Off / unknown -> not polled
        }

        // --- the tick ---------------------------------------------------------------------------------

        private void CheckDue()
        {
            try
            {
                CalendarSnapshot snap = _source.Fetch();
                _lastSnapshot = snap;
                if (snap == null) return;
                // A combined error means one or more slots failed; log it but keep going -- the healthy slots'
                // events are still in snap.Events and must still fire. Logged on CHANGE, not per tick (F197).
                LogFeedErrorOnChange(snap.Error);

                IReadOnlyList<CalendarEvent> events = snap.Events ?? (IReadOnlyList<CalendarEvent>)Array.Empty<CalendarEvent>();

                bool changed = PruneFiredAgainstFeed(snap, _fired);

                DateTimeOffset now = DateTimeOffset.Now;
                PublishMeetingContext(now, events);
                // Skip announcing WITHOUT marking fired (so an event still fires once the window ends if it is
                // still inside its lead time): during quiet hours, while Windows says now is a bad time to
                // interrupt (presenting / fullscreen / Do Not Disturb), or while there is no companion on
                // screen to show the bubble -- see AnyCompanionOnScreen (F199). The same skip covers the
                // personal reminders and the briefing below, so none of the three is spent unseen.
                bool quiet = QuietHours.IsQuiet(now, _settings.Get("quietFrom", ""), _settings.Get("quietTo", ""));
                bool hush = _settings.GetBool("hushPresenting", true) && PresentationState.ShouldHush();
                bool nobody = !AnyCompanionOnScreen();
                bool suppress = quiet || hush || nobody;
                if (!nobody) NoteReleased();
                else if (!quiet && !hush && HasSomethingDue(events, now)) NoteHeld();
                if (!suppress)
                {
                    bool chime = _settings.GetBool("chime", true);
                    Dictionary<string, SpeechStyle> styleBySlot = StyleBySlot();
                    foreach (DueReminder due in ReminderScheduler.DueNowMulti(Schedulable(events), now, Leads(), _fired))
                    {
                        if (chime && SlotChimeOn(due.Event.SourceId)) Chime.Play(_host, SlotChimePath(due.Event.SourceId));
                        SpeechStyle style;
                        styleBySlot.TryGetValue(due.Event.SourceId ?? "", out style);
                        // Move BEFORE the bubble: the animation is there to pull the eye to the pet, which is
                        // pointless once the thing you were meant to look at is already on screen.
                        React();
                        SpeakReminder(due.Event.SourceId, FormatReminder(due.Event, now, SlotLabel(due.Event.SourceId)), style);
                        _fired.Add(due.FiredId);
                        changed = true;
                    }
                    CheckPersonal(now);
                }
                MaybeBriefing(now, suppress);
                if (changed) SaveFired();
            }
            catch (Exception ex)
            {
                try { _host.Log(Id, "reminder tick failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// Is there a companion on screen to show a bubble? SayAll DROPS its line when there is none -- the
        /// ABI says so, and StartUp.DefaultSpeaker returns null with no persistent pet -- and the app keeps
        /// running from the tray after the last pet is removed. Until 1.0.7 a reminder due in that state
        /// chimed, was added to the fired set and persisted, and never appeared; a once-only personal reminder
        /// was disabled unshown; the daily briefing was stamped as read. The user closed their last pet at
        /// 09:00, the 09:45 reminder was spent into nothing, and adding a pet back at 09:50 brought nothing
        /// (F199). The tick now holds all three the way it already held them for quiet hours: skipped, not
        /// marked, so they fire on the next tick with a companion while their window is open.
        ///
        /// The same gate AgentFlow keeps in AnyCompanionCanSpeak, with one more source. Pets that were already
        /// out when this module was loaded (a catalog install at runtime) never came through CompanionSpawned,
        /// so the list alone would hold every reminder until the next spawn; the companion manager counts what
        /// is out now, whoever spawned it, and the module declares Companions so it gets the real one.
        ///
        /// Speech switched off is deliberately NOT part of this gate. The chime and the reaction still reach
        /// the user, and AgentFlow's recorded decision (AgentFlowModule.Apply, "these gate the SPEECH ONLY") is
        /// that speech-off must not withhold them; a reminder held on speech-off would re-chime every tick
        /// until speech came back. Recorded under fix/reminder in docs/DESIGN-REGISTER.md.
        /// </summary>
        private bool AnyCompanionOnScreen()
        {
            if (_host == null) return false;
            PruneDeadPets();
            if (_seenPets.Count > 0) return true;
            try
            {
                ICompanionManager pets = _host.GetCompanionManager(Id);
                if (pets != null)
                    foreach (CompanionCount c in pets.OnScreenMix())
                        if (c != null && c.Count > 0) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Whether this tick would have announced anything, for the hold log: a hold with nothing due
        /// is not news, and a line per tick is what F197 just removed from the feed error.</summary>
        private bool HasSomethingDue(IReadOnlyList<CalendarEvent> events, DateTimeOffset now)
        {
            if (ReminderScheduler.DueNowMulti(Schedulable(events), now, Leads(), _fired).Count > 0) return true;
            foreach (PersonalReminder r in LoadPersonal())
            {
                string key;
                if (r != null && r.Enabled && IsPersonalDue(r, now, out key)
                    && !string.Equals(r.LastFired, key, StringComparison.Ordinal)) return true;
            }
            string todayKey;
            return BriefingDue(now, out todayKey);
        }

        // Whether a hold has been logged and not yet released, so each hold is one line in and one line out.
        private bool _holdLogged;

        private void NoteHeld()
        {
            if (_holdLogged) return;
            _holdLogged = true;
            _host.Log(Id, "reminders held: no companion is on screen to show them; they stay pending");
        }

        private void NoteReleased()
        {
            if (!_holdLogged) return;
            _holdLogged = false;
            _host.Log(Id, "reminders resume: a companion is on screen");
        }

        // The last feed error written to the diagnostics log, "" for none. A slot in a steady error state --
        // Outlook closed, a URL slot with a blank address, a share offline -- used to write the identical line
        // on every 20 s tick: 4,320 lines a day, which rotated the 512 KB diagnostics log about daily and pushed
        // out the startup record the log exists to preserve (F197). Its own field rather than _lastSnapshot's
        // Error, because the Agenda click also writes _lastSnapshot. The pane's status line still shows the
        // error on every tick; the log records transitions, including the one back to healthy.
        private string _lastLoggedFeedError = "";

        private void LogFeedErrorOnChange(string error)
        {
            string current = error ?? "";
            if (string.Equals(current, _lastLoggedFeedError, StringComparison.Ordinal)) return;
            _lastLoggedFeedError = current;
            _host.Log(Id, current.Length > 0 ? "reminder feed: " + current : "reminder feed: recovered");
        }

        private static string FormatReminder(CalendarEvent e, DateTimeOffset now, string sourceLabel)
        {
            string title = string.IsNullOrWhiteSpace(e.Title) ? "an event" : e.Title.Trim();
            int mins = (int)Math.Round((e.Start - now).TotalMinutes);
            string line = mins <= 0 ? title + " is starting now."
                : (mins == 1 ? title + " starts in 1 minute." : title + " starts in " + mins + " minutes.");
            if (!string.IsNullOrWhiteSpace(e.Location))
                line += " (" + e.Location.Trim() + ")";
            string ju, jk;
            if (MeetingLinkDetector.TryFind(e, out ju, out jk))
                line += " Join link is in the tray.";
            if (!string.IsNullOrWhiteSpace(sourceLabel))
                line = sourceLabel.Trim() + ": " + line;
            return line;
        }

        // Every slot's SpeechStyle, keyed by slot id, computed once per tick.
        private Dictionary<string, SpeechStyle> StyleBySlot()
        {
            var map = new Dictionary<string, SpeechStyle>(StringComparer.Ordinal);
            for (int i = 1; i <= MaxSlots; i++)
                map[SlotId(i)] = SpeechStyleSettings.ToStyle(_settings, SlotId(i) + ".");
            return map;
        }

        // ONE mapping from a slot's source id to its index (0 for null, blank or unknown), read by the four
        // per-slot lookups. Each used to walk 1..MaxSlots itself, four copies of one loop that a fifth per-slot
        // setting would have made five (F198). A SourceId is always SlotId(i): AggregateCalendarSource hands
        // each slot the id BuildSource gave it. Internal for the self-test.
        internal static int SlotIndex(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return 0;
            for (int i = 1; i <= MaxSlots; i++)
                if (string.Equals(SlotId(i), sourceId, StringComparison.Ordinal)) return i;
            return 0;
        }

        private string SlotLabel(string sourceId)
        {
            int slot = SlotIndex(sourceId);
            return slot == 0 ? "" : _settings.Get(SlotKey(slot, "label"), "");
        }

        private string SlotChimePath(string sourceId)
        {
            int slot = SlotIndex(sourceId);
            return slot == 0 ? "" : _settings.Get(SlotKey(slot, "chime"), "");
        }

        private bool SlotChimeOn(string sourceId)
        {
            int slot = SlotIndex(sourceId);
            return slot == 0 || _settings.GetBool(SlotKey(slot, "chimeOn"), true);
        }

        // --- the pet's physical reaction -------------------------------------------------------------

        /// <summary>
        /// Default attention animations, tried in order; the host plays the first one the companion
        /// defines and does nothing at all if it defines none -- so a list that misses is
        /// indistinguishable from the feature being switched off, and reactOn defaults to true.
        ///
        /// MEASURED 2026-09-28 across all 54 Companions/*/animations.xml, case-insensitively:
        /// boing,jump,run,flower reached 35 and missed 19 -- bbunny, blue_ham_ham, fox, mareep,
        /// mimiko, negima, neko, pikachu, pingus, pink_fox, pink_neko, shiny_sylveon, yellow_neko
        /// and six converted shimeji. boing exists on exactly ONE companion; flower on one.
        ///
        /// The previous comment here was half-right. It said these are names the shipped pets define
        /// and that a converted shimeji uses different ones -- which is the reason an ordered list
        /// exists, but the list never actually reached the converted ones.
        ///
        /// THE HISTORICAL FOUR STAY FIRST AND IN ORDER, so no companion that already reacted changes
        /// what it plays; the tail is purely a fallback for the 19 that did nothing. 43/54 now.
        /// Nothing reaches all 54: the intersection across this corpus is EMPTY, and the only names
        /// on 50+ pets are the engine's reserved lifecycle animations (fall, drag, kill, sync), so a
        /// list built for coverage alone would offer to play the dying animation. AiBrain's
        /// EmotionAnimations and AgentFlow's PetAnimations record the same measurement.
        /// </summary>
        internal const string DefaultReactAnimations = "boing,jump,run,flower,bounce,walk,sit,turn,stand";

        /// <summary>
        /// The historical head of that list, kept in order. Behaviour preservation is a property
        /// worth asserting; the exact length of the fallback tail is not.
        /// </summary>
        internal static readonly string[] HistoricalReactAnimations =
            new string[] { "boing", "jump", "run", "flower" };

        /// <summary>
        /// The engine's reserved lifecycle animations, which must never be offered as a reaction.
        /// They are the only names present on nearly every companion, which makes them exactly what
        /// a future "improve the coverage" edit would reach for.
        /// </summary>
        internal static readonly string[] ReservedLifecycleAnimations =
            new string[] { "fall", "drag", "kill", "sync", "spawn" };

        /// <summary>
        /// Make the pet visibly react, so a reminder is something you SEE rather than only a bubble that may
        /// be behind a fullscreen window.
        ///
        /// PlayAnimationAll picks, per pet, the first candidate that pet's XML actually defines, so the module
        /// owns the mapping and the host needs no new verb -- the same division AiBrain uses for its emotion
        /// map. A pet that defines none of them is a silent no-op, which is the right outcome: better a
        /// missing flourish than a thrown exception inside a timer tick.
        /// </summary>
        private void React()
        {
            if (!_settings.GetBool("reactOn", true)) return;
            try
            {
                IReadOnlyList<string> candidates = ParseAnimationCandidates(
                    _settings.Get("reactAnimations", DefaultReactAnimations));
                if (candidates.Count == 0) return;
                _host.PlayAnimationAll(candidates);
            }
            catch (Exception ex) { try { _host.Log(Id, "reaction failed: " + ex.Message); } catch { } }
        }

        /// <summary>Split the user's comma-separated candidate list, trimming blanks and duplicates. Pure, so
        /// the self-test can pin it.</summary>
        internal static IReadOnlyList<string> ParseAnimationCandidates(string value)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) return names;
            foreach (string part in value.Split(','))
            {
                string name = (part ?? "").Trim();
                if (name.Length == 0) continue;
                if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                names.Add(name);
            }
            return names;
        }

        // --- settings ---------------------------------------------------------------------------------

        private static string SlotId(int i) { return "cal" + i.ToString(CultureInfo.InvariantCulture); }
        private static string SlotKey(int i, string key) { return SlotId(i) + "." + key; }

        internal const string SpeakerAnyLabel = "Any companion (whoever speaks for the app)";

        /// <summary>
        /// Speak a reminder through the pet this calendar names, falling back to the app's own speaker when
        /// that pet is not currently out.
        ///
        /// Falling back rather than going quiet is the whole point: the choice names a pet TYPE, the user can
        /// remove that pet at any time, and a reminder they asked for must not be swallowed because the pet
        /// they picked happens not to be on screen. SayAll is the fallback and no longer means "everyone" --
        /// the host routes it to one pet.
        /// </summary>
        private void SpeakReminder(string slotId, string text, SpeechStyle style)
        {
            ICompanion target = ResolveSpeaker(slotId);
            if (target != null) _host.Say(target, text, style);
            else _host.SayAll(text, style);
        }

        /// <summary>The live pet a calendar's reminders should come from, or null for "no preference / not
        /// available". Oldest matching pet wins, so the answer is stable rather than jumping between copies.</summary>
        private ICompanion ResolveSpeaker(string slotId)
        {
            string wanted = SlotSpeaker(slotId);
            if (_host == null) return null;
            // Sweep first, THEN scan forward. Pruning unconditionally -- before the "no preference" exit --
            // is deliberate: a user who never picks a speaker still spawns pets, and this is the other place
            // that can bound the list. After the sweep every survivor is live, so the forward scan needs no
            // per-candidate liveness check and the FIRST type match is the oldest live one, which is the
            // property the summary above promises.
            PruneDeadPets();
            if (string.IsNullOrEmpty(wanted)) return null;
            foreach (ICompanion pet in _seenPets)
                if (string.Equals(pet.TypeId ?? "", wanted, StringComparison.OrdinalIgnoreCase)) return pet;
            return null;
        }

        /// <summary>
        /// Drop the pets that have gone away, newest index first so the removals do not disturb the entries
        /// still to be examined. Order is preserved, so "oldest first" survives the sweep. A host that throws
        /// from IsCompanionAlive counts as not-alive, matching AgentFlowModule.AnyCompanionCanSpeak, which is
        /// the reference for this pattern. A null host means the answer is unknowable, so nothing is dropped
        /// rather than everything.
        /// </summary>
        private void PruneDeadPets()
        {
            IHost host = _host;
            if (host == null) return;
            for (int index = _seenPets.Count - 1; index >= 0; index--)
            {
                ICompanion pet = _seenPets[index];
                bool alive;
                if (pet == null) alive = false;
                else
                {
                    try { alive = host.IsCompanionAlive(pet); }
                    catch (Exception) { alive = false; }
                }
                if (!alive) _seenPets.RemoveAt(index);
            }
        }

        /// <summary>The pet type id stored for a calendar, or "" for no preference.</summary>
        private string SlotSpeaker(string slotId)
        {
            int slot = SlotIndex(slotId);
            return slot == 0 ? "" : _settings.Get(SlotKey(slot, "companion"), "");
        }

        /// <summary>
        /// Build the per-calendar speaker dropdown from the pets ACTUALLY ON SCREEN, so it never offers a pet
        /// that cannot answer. Labels are display names; the stored value is always the TYPE id, so a rename
        /// cannot invalidate a saved choice. Refreshed on every pane load, which is only possible because the
        /// host builds a pane by calling Load() BEFORE it reads Schema.
        /// </summary>
        private void RefreshSpeakerOptions()
        {
            var labels = new List<string> { SpeakerAnyLabel };
            var labelToType = new Dictionary<string, string>(StringComparer.Ordinal) { { SpeakerAnyLabel, "" } };
            var typeToLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "", SpeakerAnyLabel } };
            try
            {
                ICompanionManager pets = _host != null ? _host.GetCompanionManager(Id) : null;
                if (pets != null)
                {
                    var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (CompanionTypeInfo t in pets.InstalledTypes())
                        if (t != null && !string.IsNullOrEmpty(t.TypeId))
                            names[t.TypeId] = string.IsNullOrWhiteSpace(t.DisplayName) ? t.TypeId : t.DisplayName;

                    foreach (CompanionCount c in pets.OnScreenMix())
                    {
                        if (c == null || string.IsNullOrEmpty(c.TypeId)) continue;
                        if (typeToLabel.ContainsKey(c.TypeId)) continue;
                        string label;
                        if (!names.TryGetValue(c.TypeId, out label) || string.IsNullOrWhiteSpace(label)) label = c.TypeId;
                        // Keep labels unique so the closed dropdown round-trips unambiguously.
                        if (labelToType.ContainsKey(label)) label = label + " (" + c.TypeId + ")";
                        if (labelToType.ContainsKey(label)) continue;
                        labels.Add(label);
                        labelToType[label] = c.TypeId;
                        typeToLabel[c.TypeId] = label;
                    }
                }
            }
            catch { }

            _speakerLabelToType = labelToType;
            _speakerTypeToLabel = typeToLabel;
            string[] options = labels.ToArray();
            for (int i = 1; i <= MaxSlots; i++)
                if (_speakerFields[i] != null) _speakerFields[i].Options = options;
        }

        private int LeadMinutes()
        {
            int lead = _settings.GetInt("lead", DefaultLeadMinutes);
            return lead < 0 ? 0 : (lead > 240 ? 240 : lead);
        }

        // The full options schema: a group per calendar slot (feed type + name + url/file + its own speech style),
        // then the shared timing + quiet-hours + status fields.
        private SettingField[] BuildSchema()
        {
            var fields = new List<SettingField>();
            for (int i = 1; i <= MaxSlots; i++)
            {
                string g = "Calendar " + i.ToString(CultureInfo.InvariantCulture);
                fields.Add(new SettingField { Id = SlotKey(i, "type"), Label = "Feed type", Kind = SettingKind.Enum, Options = new[] { SourceOff, SourceLocalFile, SourceCalendarUrl, SourceOutlook }, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "label"), Label = "Name (spoken with the reminder, e.g. Home / Work)", Kind = SettingKind.Text, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "url"), Label = "Calendar URL (Google/Outlook secret .ics)", Kind = SettingKind.Text, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "file"), Label = "Reminder feed file (JSON)", Kind = SettingKind.Text, Group = g });
                // Which pet delivers THIS calendar's reminders. Held by reference so RefreshSpeakerOptions can
                // repopulate it from the live pets each time the pane opens.
                _speakerFields[i] = new SettingField
                {
                    Id = SlotKey(i, "companion"),
                    Label = "Reminder companion (which companion speaks this calendar)",
                    Kind = SettingKind.Enum,
                    Options = new[] { SpeakerAnyLabel },
                    Group = g,
                };
                fields.Add(_speakerFields[i]);
                fields.Add(new SettingField { Id = SlotKey(i, "chimeOn"), Label = "Play a chime for this calendar", Kind = SettingKind.Bool, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "chime"), Label = "Chime sound file (blank = built-in; use Browse below)", Kind = SettingKind.Text, Group = g });
                fields.AddRange(SpeechStyleSettings.Fields(g, SlotId(i) + "."));
            }
            fields.Add(new SettingField { Id = "personalChimeOn", Label = "Play a chime for personal reminders", Kind = SettingKind.Bool, Group = "Personal reminder style & chime" });
            fields.Add(new SettingField { Id = "personalChime", Label = "Chime sound file (blank = built-in; use Browse below)", Kind = SettingKind.Text, Group = "Personal reminder style & chime" });
            fields.AddRange(SpeechStyleSettings.Fields("Personal reminder style & chime", "personal."));
            fields.Add(new SettingField { Id = "leads", Label = "Remind me these many minutes before (comma-separated, e.g. 15,5)", Kind = SettingKind.Text, Group = "Timing" });
            fields.Add(new SettingField { Id = "chime", Label = "Play chimes with reminders (master switch for all calendars)", Kind = SettingKind.Bool, Group = "Timing" });
            fields.Add(new SettingField { Id = "skipDeclined", Label = "Skip meetings I've declined (Outlook only)", Kind = SettingKind.Bool, Group = "Filtering" });
            fields.Add(new SettingField { Id = "skipAllDay", Label = "Skip all-day events", Kind = SettingKind.Bool, Group = "Filtering" });
            fields.Add(new SettingField { Id = "quietFrom", Label = "Quiet hours start (HH:mm, 24h; blank = off)", Kind = SettingKind.Text, Group = "Quiet hours" });
            fields.Add(new SettingField { Id = "quietTo", Label = "Quiet hours end (HH:mm, 24h; blank = off)", Kind = SettingKind.Text, Group = "Quiet hours" });
            fields.Add(new SettingField { Id = "hushPresenting", Label = "Stay quiet while presenting or in Do Not Disturb", Kind = SettingKind.Bool, Group = "Quiet hours" });
            fields.Add(new SettingField { Id = "briefingOn", Label = "Read me the day's agenda each morning", Kind = SettingKind.Bool, Group = "Daily briefing" });
            fields.Add(new SettingField { Id = "briefingTime", Label = "Briefing time (HH:mm, 24h)", Kind = SettingKind.Text, Group = "Daily briefing" });
            fields.Add(new SettingField { Id = "reactOn", Label = "Make the companion react when a reminder fires", Kind = SettingKind.Bool, Group = "Companion reaction" });
            fields.Add(new SettingField { Id = "reactAnimations", Label = "Animations to try, in order (first one the companion defines wins)", Kind = SettingKind.Text, Group = "Companion reaction" });
            fields.Add(new SettingField { Id = "status", Label = "Feed status", Kind = SettingKind.Info, Group = "Status" });
            return fields.ToArray();
        }

        // The lead times the tick uses: the parsed "leads" list, or the legacy single "lead" when it is empty.
        private IReadOnlyList<int> Leads()
        {
            List<int> list = ParseLeads(_settings.Get("leads", ""));
            if (list.Count == 0) list.Add(LeadMinutes());
            return list;
        }

        // What the pane shows in the "leads" box: the saved list, or the legacy single lead as a one-item list.
        private string LeadsText()
        {
            List<int> list = ParseLeads(_settings.Get("leads", ""));
            if (list.Count == 0) list.Add(LeadMinutes());
            return string.Join(",", list);
        }

        private static string NormalizeLeads(string raw)
        {
            return string.Join(",", ParseLeads(raw ?? ""));   // "" when nothing parses -> Leads() falls back
        }

        private static List<int> ParseLeads(string raw)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (string part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    int clamped = n < 0 ? 0 : (n > 240 ? 240 : n);
                    if (!list.Contains(clamped)) list.Add(clamped);
                }
            }
            return list;
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Reminders",
                Schema = BuildSchema(),
                Actions = BuildActions(),
                Lists = new[] { BuildPersonalListCard() },
                Load = () =>
                {
                    var values = new Dictionary<string, string>();
                    // Rebuild the speaker dropdowns from the pets on screen right now, BEFORE the host reads
                    // Schema to render the fields. See the invariant pinning that order.
                    RefreshSpeakerOptions();
                    for (int i = 1; i <= MaxSlots; i++)
                    {
                        // An unset choice, or one whose pet is no longer out, both show "Any companion" WITHOUT
                        // clearing the stored id: that pet may come back and the preference should survive.
                        string savedPet = _settings.Get(SlotKey(i, "companion"), "");
                        string petLabel;
                        values[SlotKey(i, "companion")] = _speakerTypeToLabel.TryGetValue(savedPet ?? "", out petLabel)
                            ? petLabel
                            : SpeakerAnyLabel;
                        values[SlotKey(i, "type")] = _settings.Get(SlotKey(i, "type"), SourceOff);
                        values[SlotKey(i, "label")] = _settings.Get(SlotKey(i, "label"), "");
                        values[SlotKey(i, "url")] = _settings.Get(SlotKey(i, "url"), "");
                        values[SlotKey(i, "file")] = _settings.Get(SlotKey(i, "file"), "");
                        values[SlotKey(i, "chimeOn")] = _settings.GetBool(SlotKey(i, "chimeOn"), true) ? "true" : "false";
                        values[SlotKey(i, "chime")] = _settings.Get(SlotKey(i, "chime"), "");
                        SpeechStyleSettings.AddLoadValues(values, _settings, SlotId(i) + ".");
                    }
                    values["personalChimeOn"] = _settings.GetBool("personalChimeOn", true) ? "true" : "false";
                    values["personalChime"] = _settings.Get("personalChime", "");
                    SpeechStyleSettings.AddLoadValues(values, _settings, "personal.");
                    values["leads"] = LeadsText();
                    values["chime"] = _settings.GetBool("chime", true) ? "true" : "false";
                    values["skipDeclined"] = _settings.GetBool("skipDeclined", true) ? "true" : "false";
                    values["skipAllDay"] = _settings.GetBool("skipAllDay", false) ? "true" : "false";
                    values["quietFrom"] = _settings.Get("quietFrom", "");
                    values["quietTo"] = _settings.Get("quietTo", "");
                    values["hushPresenting"] = _settings.GetBool("hushPresenting", true) ? "true" : "false";
                    values["briefingOn"] = _settings.GetBool("briefingOn", false) ? "true" : "false";
                    values["briefingTime"] = _settings.Get("briefingTime", "08:00");
                    values["reactOn"] = _settings.GetBool("reactOn", true) ? "true" : "false";
                    values["reactAnimations"] = _settings.Get("reactAnimations", DefaultReactAnimations);
                    values["status"] = StatusLine();
                    return values;
                },
                Save = values =>
                {
                    string v;
                    for (int i = 1; i <= MaxSlots; i++)
                    {
                        if (values.TryGetValue(SlotKey(i, "type"), out v) && !string.IsNullOrWhiteSpace(v)) _settings.Set(SlotKey(i, "type"), v.Trim());
                        if (values.TryGetValue(SlotKey(i, "label"), out v)) _settings.Set(SlotKey(i, "label"), (v ?? "").Trim());
                        if (values.TryGetValue(SlotKey(i, "url"), out v)) _settings.Set(SlotKey(i, "url"), (v ?? "").Trim());
                        if (values.TryGetValue(SlotKey(i, "file"), out v)) _settings.Set(SlotKey(i, "file"), (v ?? "").Trim());
                        // An unrecognized label (the pet was removed while the window was open) leaves the
                        // saved choice alone rather than silently rewriting it to "any".
                        if (values.TryGetValue(SlotKey(i, "companion"), out v))
                        {
                            string chosenType;
                            if (_speakerLabelToType.TryGetValue(v ?? "", out chosenType))
                                _settings.Set(SlotKey(i, "companion"), chosenType);
                        }
                        if (values.TryGetValue(SlotKey(i, "chimeOn"), out v)) { bool cb; if (bool.TryParse(v, out cb)) _settings.Set(SlotKey(i, "chimeOn"), cb ? "true" : "false"); }
                        if (values.TryGetValue(SlotKey(i, "chime"), out v)) _settings.Set(SlotKey(i, "chime"), (v ?? "").Trim());
                        SpeechStyleSettings.Save(_settings, values, SlotId(i) + ".");
                    }
                    if (values.TryGetValue("personalChimeOn", out v)) { bool pb; if (bool.TryParse(v, out pb)) _settings.Set("personalChimeOn", pb ? "true" : "false"); }
                    if (values.TryGetValue("personalChime", out v)) _settings.Set("personalChime", (v ?? "").Trim());
                    SpeechStyleSettings.Save(_settings, values, "personal.");
                    if (values.TryGetValue("leads", out v)) _settings.Set("leads", NormalizeLeads(v));
                    if (values.TryGetValue("chime", out v)) { bool b; if (bool.TryParse(v, out b)) _settings.Set("chime", b ? "true" : "false"); }
                    if (values.TryGetValue("skipDeclined", out v)) { bool sb; if (bool.TryParse(v, out sb)) _settings.Set("skipDeclined", sb ? "true" : "false"); }
                    if (values.TryGetValue("skipAllDay", out v)) { bool sb; if (bool.TryParse(v, out sb)) _settings.Set("skipAllDay", sb ? "true" : "false"); }
                    if (values.TryGetValue("quietFrom", out v)) _settings.Set("quietFrom", (v ?? "").Trim());
                    if (values.TryGetValue("quietTo", out v)) _settings.Set("quietTo", (v ?? "").Trim());
                    if (values.TryGetValue("hushPresenting", out v)) { bool hb; if (bool.TryParse(v, out hb)) _settings.Set("hushPresenting", hb ? "true" : "false"); }
                    if (values.TryGetValue("briefingOn", out v)) { bool bb; if (bool.TryParse(v, out bb)) _settings.Set("briefingOn", bb ? "true" : "false"); }
                    if (values.TryGetValue("briefingTime", out v)) _settings.Set("briefingTime", (v ?? "").Trim());
                    if (values.TryGetValue("reactOn", out v)) { bool rb; if (bool.TryParse(v, out rb)) _settings.Set("reactOn", rb ? "true" : "false"); }
                    if (values.TryGetValue("reactAnimations", out v)) _settings.Set("reactAnimations", (v ?? "").Trim());
                    bool ok = _settings.Save();
                    _source = BuildSource();   // a slot whose type changed gets a new source; the rest keep theirs (F200)
                    return ok;
                },
            };
        }

        // A "Browse for a chime" button per calendar card, plus the shared "Check now". A PaneAction runs on the
        // UI thread (per the ABI), so the file dialog is safe here with no host change; ReloadPaneAfter refreshes
        // the chime text box to the chosen path so a later Apply reads it back rather than clobbering it.
        private PaneAction[] BuildActions()
        {
            var actions = new List<PaneAction>();
            for (int i = 1; i <= MaxSlots; i++)
            {
                int slot = i;   // capture a per-iteration copy, not the shared loop variable
                actions.Add(new PaneAction
                {
                    Label = "Browse for a chime…",
                    Group = "Calendar " + slot.ToString(CultureInfo.InvariantCulture),
                    ReloadPaneAfter = true,
                    InvokeAsync = () => System.Threading.Tasks.Task.FromResult(BrowseChime(slot)),
                });
                actions.Add(new PaneAction
                {
                    Label = "Test this reminder",
                    Group = "Calendar " + slot.ToString(CultureInfo.InvariantCulture),
                    ReloadPaneAfter = false,   // no settings change: don't reload and lose other unsaved edits
                    InvokeAsync = () => System.Threading.Tasks.Task.FromResult(TestReminder(slot)),
                });
            }
            actions.Add(new PaneAction
            {
                Label = "Browse for a chime…",
                Group = "Personal reminder style & chime",
                ReloadPaneAfter = true,
                InvokeAsync = () => System.Threading.Tasks.Task.FromResult(BrowsePersonalChime()),
            });
            actions.Add(new PaneAction
            {
                Label = "Check now",
                Group = "Status",
                ReloadPaneAfter = true,
                InvokeAsync = CheckNowAsync,
            });
            return actions.ToArray();
        }

        // How long "Check now" waits for the re-read it asked for before reporting whatever has landed. A URL
        // fetch takes a second or two and an Outlook enumeration a few; past this the status says a feed is
        // still refreshing rather than presenting the cache as the answer.
        private const int CheckNowWaitSeconds = 8;

        /// <summary>
        /// "Check now" used to run the tick against the cached snapshot: Fetch answers from its cache and kicks
        /// a re-read only when a slot's own interval has elapsed, so the button could never show anything the
        /// last tick had not already seen, and the reloaded status repeated the old count and stamp (F201). It
        /// now asks every slot to re-read, runs the due check at once (the kick happens inside that Fetch),
        /// waits -- bounded -- for the refreshes to land, and runs the due check again against what arrived.
        ///
        /// The wait is an await, not a sleep. The host awaits InvokeAsync on the UI thread (PaneAction's own
        /// contract), so each Task.Delay yields the thread to the message loop and its continuation comes back
        /// to it, which is where CheckDue must run. A Thread.Sleep here would freeze the window for the wait.
        /// </summary>
        private async System.Threading.Tasks.Task<string> CheckNowAsync()
        {
            ICalendarSource source = _source;
            if (source == null) return StatusLine();
            source.Invalidate();
            CheckDue();
            DateTime deadline = DateTime.UtcNow.AddSeconds(CheckNowWaitSeconds);
            while (source.IsRefreshing && DateTime.UtcNow < deadline)
                await System.Threading.Tasks.Task.Delay(200);
            CheckDue();
            return source.IsRefreshing ? "a feed is still refreshing; " + StatusLine() : StatusLine();
        }

        // Fire a sample announcement in this slot's name, style, and chime so the user can see and hear it while
        // configuring, instead of waiting for a real event. Uses the SAVED settings (a PaneAction can't read the
        // pane's unsaved edits), so the status reminds the user to Apply first to preview pending changes.
        /// <summary>What "Test this reminder" answers when no companion is on screen to show it.</summary>
        internal const string NoCompanionStatus =
            "✗ no companion is on screen to show it. Add one from the tray, then test again.";

        /// <summary>The log line the Agenda tray click leaves when no companion is on screen to read it.</summary>
        internal const string AgendaNobodyLogLine = "agenda not read: no companion is on screen to show it";

        private string TestReminder(int slot)
        {
            try
            {
                // The gate CheckDue holds a due reminder on (F199), applied to the button: SayAll drops its
                // line when no companion is out, so "✓ test sent" over an empty desktop reported a send that
                // reached nobody, with the chime and the reaction fired into nothing beside it (N-reminder-03).
                if (!AnyCompanionOnScreen()) return NoCompanionStatus;
                string label = _settings.Get(SlotKey(slot, "label"), "");
                string name = string.IsNullOrWhiteSpace(label) ? ("Calendar " + slot.ToString(CultureInfo.InvariantCulture)) : label.Trim();
                SpeechStyle style = SpeechStyleSettings.ToStyle(_settings, SlotId(slot) + ".");
                if (_settings.GetBool(SlotKey(slot, "chimeOn"), true))
                    Chime.Play(_host, _settings.Get(SlotKey(slot, "chime"), ""));
                // Also fire the reaction here: waiting for a real calendar event is a poor way to find out
                // whether your pets actually animate, and this button is the only on-demand trigger.
                React();
                SpeakReminder(SlotId(slot), name + ": this is a test reminder in this calendar's style.", style);
                return "✓ test sent. It uses saved settings, so Apply first to preview pending edits.";
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
        }

        // Open a file picker and, on OK, persist the chosen sound as a chime. Best-effort: a cancel or any error
        // just leaves the current setting. The pick is refused above Chime.MaximumCustomBytes, the MODULE's cap
        // (8 MiB, half the host's 16 MiB MaximumModuleAudioBytes; see Chime), with a clear message rather than a
        // silent fall-back to the default chime at reminder time (F202).
        private string BrowseChime(int slot)
        {
            return BrowseChimeInto(SlotKey(slot, "chime"), "Choose a chime sound (Calendar " + slot.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private string BrowsePersonalChime()
        {
            return BrowseChimeInto("personalChime", "Choose a chime sound (personal reminders)");
        }

        private string BrowseChimeInto(string settingKey, string title)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = title;
                    dlg.Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    string current = _settings.Get(settingKey, "");
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        try
                        {
                            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(current);
                            dlg.FileName = System.IO.Path.GetFileName(current);
                        }
                        catch { }
                    }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return "Chime unchanged.";
                    string path = (dlg.FileName ?? "").Trim();
                    long len;
                    try { len = new System.IO.FileInfo(path).Length; } catch { len = 0; }
                    if (len > Chime.MaximumCustomBytes)
                        return "✗ that file is over " +
                               (Chime.MaximumCustomBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) +
                               " MiB; pick a short chime.";
                    _settings.Set(settingKey, path);
                    _settings.Save();
                    return "✓ chime set: " + System.IO.Path.GetFileName(path);
                }
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
        }

        // Speak the rest of today's events on demand.
        // Tray-item icons (TrayItem.IconPng): raw PNG bytes from this module's own embedded resources, so the
        // base renders them without the ABI depending on System.Drawing. Null on any failure, which degrades
        // to an icon-less entry rather than breaking the tray.
        private static byte[] LoadIconResource(string fileName)
        {
            return EmbeddedResources.LoadBytes(typeof(ReminderModule).Assembly, fileName);
        }

        private TrayItem BuildAgendaTrayItem()
        {
            return new TrayItem
            {
                Group = 40,
                Order = 5,
                IconPng = LoadIconResource("agenda.png"),
                DynamicText = () => "Read today's agenda",
                Click = () =>
                {
                    // SayAll would drop the agenda with nobody on screen (F199), and a tray click has no
                    // status line to answer on, so the log is where it says why nothing was read
                    // (N-reminder-03); the log is the file SUPPORT.md asks for.
                    if (!AnyCompanionOnScreen()) { try { _host.Log(Id, AgendaNobodyLogLine); } catch { } return; }
                    try { _lastSnapshot = _source.Fetch(); } catch { }
                    _host.SayAll(AgendaText(DateTimeOffset.Now), null);
                },
            };
        }

        private TrayItem BuildTrayItem()
        {
            return new TrayItem
            {
                Group = 40,
                Order = 10,
                IconPng = LoadIconResource("reminder.png"),
                DynamicText = () =>
                {
                    CalendarEvent next = NextUpcoming();
                    if (next == null) return "Reminders: nothing upcoming";
                    return "Reminders: next is " + (string.IsNullOrWhiteSpace(next.Title) ? "an event" : next.Title.Trim())
                        + " at " + next.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                },
                // A click is the user asking, so the feeds are re-read rather than the cache re-checked (F201).
                // The re-read lands in the background: this click's due check runs against the cache and the
                // next tick, or the next click, sees what arrived. The pane's "Check now" is the one that waits.
                Click = () =>
                {
                    ICalendarSource source = _source;
                    if (source != null) source.Invalidate();
                    CheckDue();
                },
            };
        }

        // A one-click "Join" for a video meeting that is happening now or about to. Only shows a live label when
        // an ongoing/imminent event actually carries a Teams/Zoom/Meet/Webex link; otherwise it is a no-op hint.
        private TrayItem BuildJoinTrayItem()
        {
            return new TrayItem
            {
                Group = 40,
                Order = 20,
                IconPng = LoadIconResource("meeting.png"),
                DynamicText = () =>
                {
                    CalendarEvent m = BestJoinable();
                    return m == null
                        ? "No meeting to join right now"
                        : "Join: " + (string.IsNullOrWhiteSpace(m.Title) ? "meeting" : m.Title.Trim());
                },
                Click = () =>
                {
                    CalendarEvent m = BestJoinable();
                    string url, kind;
                    if (m != null && MeetingLinkDetector.TryFind(m, out url, out kind)) OpenUrl(url);
                },
            };
        }

        // The best meeting to "Join" now: among events from ~10 min before their start until their end, the one
        // nearest to now that actually has a join link.
        private CalendarEvent BestJoinable()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null || snap.Events == null) return null;
            DateTimeOffset now = DateTimeOffset.Now;
            CalendarEvent best = null;
            double bestDist = double.MaxValue;
            foreach (CalendarEvent e in snap.Events)
            {
                if (e == null) continue;
                DateTimeOffset end = e.End ?? e.Start.AddMinutes(60);
                if (now < e.Start.AddMinutes(-10) || now > end) continue;   // not ongoing/imminent
                string url, kind;
                if (!MeetingLinkDetector.TryFind(e, out url, out kind)) continue;
                double dist = Math.Abs((e.Start - now).TotalMinutes);
                if (dist < bestDist) { bestDist = dist; best = e; }
            }
            return best;
        }

        private void OpenUrl(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return;
                if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;   // never launch a non-web scheme
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) { try { _host.Log(Id, "join link failed: " + ex.Message); } catch { } }
        }

        // --- agenda + daily briefing -----------------------------------------------------------------

        // The events that may schedule/announce, after the announce filter (feature: skip declined / all-day).
        private IReadOnlyList<CalendarEvent> Schedulable(IReadOnlyList<CalendarEvent> events)
        {
            if (events == null) return Array.Empty<CalendarEvent>();
            var kept = new List<CalendarEvent>(events.Count);
            foreach (CalendarEvent e in events) if (e != null && PassesFilter(e)) kept.Add(e);
            return kept;
        }

        // Whether an event is announced at all: a single gate used by scheduling, the agenda, and "next
        // upcoming" so all three agree. Response status is only known from Outlook (an .ics feed doesn't carry
        // "my" status), so skip-declined is a no-op for ICS feeds, which is the correct, safe default.
        private bool PassesFilter(CalendarEvent e)
        {
            if (e == null) return false;
            if (_settings.GetBool("skipDeclined", true) &&
                string.Equals(e.ResponseStatus, "declined", StringComparison.OrdinalIgnoreCase)) return false;
            if (_settings.GetBool("skipAllDay", false) && e.AllDay) return false;
            return true;
        }

        // --- shared-context publish (meeting.current, for the Remembrance module) ---------------------

        private const string MeetingContextKey = "meeting.current";
        private string _lastMeetingJson = "";

        // Publish the ongoing / about-to-start meeting to the host's shared-context channel, so another module
        // (Remembrance) can name a recording and seed its attendance from the calendar. Publishes only on change.
        private void PublishMeetingContext(DateTimeOffset now, IReadOnlyList<CalendarEvent> events)
        {
            CalendarEvent current = CurrentMeeting(now, events);
            string json = current == null ? "" : SerializeMeeting(current);
            if (string.Equals(json, _lastMeetingJson, StringComparison.Ordinal)) return;
            _lastMeetingJson = json;
            try { _host.PublishContext(Id, MeetingContextKey, json); } catch { }
        }

        // The event happening now (or within five minutes of starting); the most recently started when several
        // overlap. Honours the announce filter, so a declined meeting is never published as "current".
        private CalendarEvent CurrentMeeting(DateTimeOffset now, IReadOnlyList<CalendarEvent> events)
        {
            if (events == null) return null;
            CalendarEvent best = null;
            foreach (CalendarEvent e in events)
            {
                if (e == null || !PassesFilter(e)) continue;
                DateTimeOffset end = e.End ?? e.Start.AddMinutes(60);
                if (now < e.Start.AddMinutes(-5) || now > end) continue;
                if (best == null || e.Start > best.Start) best = e;
            }
            return best;
        }

        private static string SerializeMeeting(CalendarEvent e)
        {
            try
            {
                var payload = new
                {
                    name = e.Title ?? "",
                    startUtc = e.Start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    endUtc = e.End.HasValue ? e.End.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : null,
                    location = e.Location ?? "",
                    attendees = (e.Attendees ?? new List<Attendee>())
                        .Select(a => new { name = a.Name ?? "", status = a.Status ?? "" }).ToArray(),
                };
                return JsonSerializer.Serialize(payload);
            }
            catch { return ""; }
        }

        // A spoken summary of what's left today across every calendar.
        private string AgendaText(DateTimeOffset now)
        {
            CalendarSnapshot snap = _lastSnapshot;
            var today = new List<CalendarEvent>();
            if (snap != null && snap.Events != null)
            {
                DateTimeOffset endOfDay = new DateTimeOffset(now.Year, now.Month, now.Day, 23, 59, 59, now.Offset);
                foreach (CalendarEvent e in snap.Events)
                {
                    if (e == null || !PassesFilter(e)) continue;
                    if (e.Start < now || e.Start > endOfDay) continue;
                    today.Add(e);
                }
            }
            today.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (today.Count == 0) return "Nothing left on your calendar today.";

            int shown = Math.Min(today.Count, 6);
            var parts = new List<string>();
            for (int i = 0; i < shown; i++)
            {
                CalendarEvent e = today[i];
                string t = string.IsNullOrWhiteSpace(e.Title) ? "an event" : e.Title.Trim();
                parts.Add(t + " at " + e.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            }
            string more = today.Count > shown ? ", and " + (today.Count - shown) + " more" : "";
            string count = today.Count == 1 ? "1 event left today" : today.Count + " events left today";
            return "You have " + count + ": " + string.Join("; ", parts) + more + ".";
        }

        // Once a day, at the configured time, read the agenda. Skipped during quiet hours; marks the date so it
        // fires exactly once even across restarts. A late start after the time still gets the briefing that day.
        private void MaybeBriefing(DateTimeOffset now, bool quiet)
        {
            if (quiet) return;
            string todayKey;
            if (!BriefingDue(now, out todayKey)) return;
            _settings.Set("briefingLast", todayKey);
            _settings.Save();
            _host.SayAll(AgendaText(now), null);
        }

        // The briefing's due rule on its own, so the hold log can ask it without stamping the day (F199).
        private bool BriefingDue(DateTimeOffset now, out string todayKey)
        {
            todayKey = null;
            if (!_settings.GetBool("briefingOn", false)) return false;
            int mins;
            // QuietHours' parser, the module's one HH:mm reader since F203: this had its own copy, which
            // accepted "+8:00" and "9: 5" while the quiet-hours fields beside it refused them.
            if (!QuietHours.TryParseTimeOfDay(_settings.Get("briefingTime", "08:00"), out mins)) return false;
            DateTimeOffset todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddMinutes(mins);
            if (now < todayAt) return false;
            todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return !string.Equals(_settings.Get("briefingLast", ""), todayKey, StringComparison.Ordinal);
        }

        // --- typed personal reminders (independent of any calendar) -----------------------------------

        private List<PersonalReminder> LoadPersonal()
        {
            var list = new List<PersonalReminder>();
            string raw = _settings.Get("personal", "");
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string line in raw.Split('\n'))
            {
                PersonalReminder r = PersonalReminder.Decode(line.Trim());
                if (r != null) list.Add(r);
            }
            return list;
        }

        private void SavePersonal(List<PersonalReminder> list)
        {
            _settings.Set("personal", string.Join("\n", list.Select(PersonalReminder.Encode)));
            _settings.Save();
        }

        // Announce any personal reminder that just came due, in the personal style + chime. Dedup is the
        // reminder's own LastFired stamp (bounded, unlike a global fired set); a one-off disables itself.
        private void CheckPersonal(DateTimeOffset now)
        {
            List<PersonalReminder> list = LoadPersonal();
            if (list.Count == 0) return;
            SpeechStyle style = SpeechStyleSettings.ToStyle(_settings, "personal.");
            bool chime = _settings.GetBool("chime", true) && _settings.GetBool("personalChimeOn", true);
            string chimePath = _settings.Get("personalChime", "");
            bool changed = false;
            foreach (PersonalReminder r in list)
            {
                if (r == null || !r.Enabled) continue;
                string firedKey;
                if (!IsPersonalDue(r, now, out firedKey)) continue;
                if (string.Equals(r.LastFired, firedKey, StringComparison.Ordinal)) continue;
                r.LastFired = firedKey;
                if (r.Kind == PersonalReminder.KindOnce) r.Enabled = false;
                changed = true;
                if (chime) Chime.Play(_host, chimePath);
                _host.SayAll(FormatPersonal(r), style);
            }
            if (changed) SavePersonal(list);
        }

        // The occurrence key a reminder would fire under right now, or false if it is not due. once: any time at
        // or after its moment (so a late start still delivers it). everyN: within 2 min of an interval boundary.
        // daily/weekdays: within 15 min of the time (forgives a slightly late tick or start, not hours).
        private static bool IsPersonalDue(PersonalReminder r, DateTimeOffset now, out string firedKey)
        {
            firedKey = null;
            switch (r.Kind)
            {
                case PersonalReminder.KindOnce:
                    if (now >= r.When) { firedKey = "once"; return true; }
                    return false;
                case PersonalReminder.KindEveryN:
                    if (r.IntervalMinutes <= 0) return false;
                    double since = (now - r.Anchor).TotalMinutes;
                    if (since < r.IntervalMinutes) return false;
                    long index = (long)(since / r.IntervalMinutes);
                    DateTimeOffset occ = r.Anchor.AddMinutes(index * (double)r.IntervalMinutes);
                    double d = (now - occ).TotalMinutes;
                    if (d >= 0 && d <= 2.0) { firedKey = "i" + index.ToString(CultureInfo.InvariantCulture); return true; }
                    return false;
                case PersonalReminder.KindDaily:
                case PersonalReminder.KindWeekdays:
                    if (r.Kind == PersonalReminder.KindWeekdays &&
                        (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)) return false;
                    DateTimeOffset todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddMinutes(r.TimeOfDayMinutes);
                    double dd = (now - todayAt).TotalMinutes;
                    if (dd >= 0 && dd <= 15.0) { firedKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); return true; }
                    return false;
            }
            return false;
        }

        private static string FormatPersonal(PersonalReminder r)
        {
            string t = string.IsNullOrWhiteSpace(r.Text) ? "reminder" : r.Text.Trim();
            return "Reminder: " + t;
        }

        private ListCard BuildPersonalListCard()
        {
            return new ListCard
            {
                Title = "Personal reminders",
                EmptyHint = "No personal reminders yet. Use “Add a reminder…” below.",
                LoadItems = () =>
                {
                    var items = new List<ListItem>();
                    foreach (PersonalReminder r in LoadPersonal())
                        items.Add(new ListItem { Id = r.Id, Label = r.Text, Detail = r.ScheduleSummary(), Checked = r.Enabled });
                    return items;
                },
                SetChecked = (id, on) => TogglePersonal(id, on),
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Add a reminder…",
                        ReloadPaneAfter = true,
                        InvokeAsync = () => System.Threading.Tasks.Task.FromResult(AddPersonalReminder()),
                    },
                    new PaneAction
                    {
                        Label = "Remove disabled",
                        ReloadPaneAfter = true,
                        InvokeAsync = () => System.Threading.Tasks.Task.FromResult(RemoveDisabledPersonal()),
                    },
                },
            };
        }

        private void TogglePersonal(string id, bool on)
        {
            List<PersonalReminder> list = LoadPersonal();
            bool changed = false;
            foreach (PersonalReminder r in list)
                if (r != null && r.Id == id && r.Enabled != on) { r.Enabled = on; changed = true; }
            if (changed) SavePersonal(list);
        }

        private string AddPersonalReminder()
        {
            try
            {
                string input;
                if (!PromptDialog.Show("Add a reminder",
                        "Type a schedule then the text.\r\nExamples:  daily 09:00 Standup   |   every 60m Stretch   |   in 30m Pizza   |   weekdays 17:00 Log off   |   2026-09-01 14:00 Dentist",
                        "", out input))
                    return "No reminder added.";
                PersonalReminder r;
                string err;
                if (!PersonalReminderParser.TryParse(input, DateTimeOffset.Now, out r, out err))
                    return "✗ " + err;
                List<PersonalReminder> list = LoadPersonal();
                list.Add(r);
                SavePersonal(list);
                return "✓ added: " + r.Text + " (" + r.ScheduleSummary() + ")";
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
        }

        private string RemoveDisabledPersonal()
        {
            List<PersonalReminder> list = LoadPersonal();
            int before = list.Count;
            List<PersonalReminder> kept = list.Where(r => r != null && r.Enabled).ToList();
            if (kept.Count == before) return "Nothing to remove (no disabled reminders).";
            SavePersonal(kept);
            return "✓ removed " + (before - kept.Count) + " disabled reminder(s).";
        }

        private string StatusLine()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null) return "Not checked yet.";
            int count = snap.Events != null ? snap.Events.Count : 0;
            string updated = snap.Updated != null ? ", updated " + snap.Updated.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
            CalendarEvent next = NextUpcoming();
            string nextText = next != null
                ? "; next: " + (string.IsNullOrWhiteSpace(next.Title) ? "an event" : next.Title.Trim())
                    + " at " + next.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : "; nothing upcoming";
            string src = _source != null ? _source.Name + ": " : "";
            string prefix = string.IsNullOrEmpty(snap.Error) ? "✓ " : "⚠ ";
            string err = string.IsNullOrEmpty(snap.Error) ? "" : " [" + snap.Error + "]";
            return prefix + src + count + " event(s)" + updated + nextText + err;
        }

        private CalendarEvent NextUpcoming()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null || snap.Events == null) return null;
            DateTimeOffset now = DateTimeOffset.Now;
            return snap.Events.Where(e => e != null && PassesFilter(e) && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        }

        // --- fired-id persistence (bounded to the current feed in CheckDue) ---------------------------

        private void LoadFired()
        {
            string raw = _settings.Get("fired", "");
            if (string.IsNullOrEmpty(raw)) return;
            foreach (string id in raw.Split('\n'))
                if (!string.IsNullOrWhiteSpace(id)) _fired.Add(id.Trim());
        }

        /// <summary>
        /// Drop fired ids whose EVENT no longer appears in the feed, but ONLY where the feed is a trustworthy
        /// view of the calendar, judged PER SLOT. Returns true when anything was removed.
        ///
        /// This used to prune unconditionally, before any look at snap.Error, and that undid the one promise
        /// this module's header makes: "remembers which fired so a restart never re-nags".
        ///
        /// An empty feed is ROUTINE here, not exceptional. CachingCalendarSource.Fetch captures _cache under
        /// its lock BEFORE kicking the background refresh, so the FIRST call always returns no events with
        /// Error = "Loading the calendar…", and Init calls CheckDue directly, so that first call happens on
        /// every single launch.
        ///
        /// The failure that follows: a 10:00 meeting with a 15-minute lead fires at 09:45 and "cal1|uid@15"
        /// is saved. Restart at 09:50. Init, CheckDue, empty feed, the whole set wiped and written to disk.
        /// Twenty seconds later the feed loads, `now` is still inside DueNowMulti's [start-15, start+1] window,
        /// and the same meeting announces again with chime, animation and bubble.
        ///
        /// 1.0.3 gated the whole prune on the combined Error and argued the skip could not leak "because the
        /// set only GROWS when an event fires, and firing needs events". True of one source; false of the
        /// aggregate, which sets Error whenever ANY slot fails and still fires the healthy slots' events -- by
        /// design. A Local Outlook slot on a box where Outlook is closed, a URL slot left blank, a file slot on
        /// a share only reachable on VPN: each keeps the combined Error set for good, and the healthy slot
        /// beside it added an id per reminder per lead that nothing ever removed, persisted and reloaded at
        /// every launch and rewritten in full on the UI thread at every fire (F204). So the prune judges each
        /// id by ITS slot's health, which the aggregate carries on the snapshot: an id whose slot fetched
        /// cleanly is dropped when its event is gone; an id whose slot errored or is still loading is kept,
        /// which is the 1.0.3 fix applied per slot (a slot serving last-good events behind an error counts as
        /// unvouched too, so a cancelled meeting is never mistaken for a stale one); an id no configured slot
        /// claims -- a legacy pre-1.3.0 id, or a slot since turned Off -- is dropped, as it was whenever the
        /// aggregate was healthy. A snapshot with no per-slot health, one source or a test double, keeps the
        /// whole-snapshot rule.
        /// </summary>
        internal static bool PruneFiredAgainstFeed(CalendarSnapshot snap, HashSet<string> fired)
        {
            if (snap == null || fired == null || fired.Count == 0) return false;

            IReadOnlyList<CalendarEvent> events =
                snap.Events ?? (IReadOnlyList<CalendarEvent>)Array.Empty<CalendarEvent>();
            // Fired ids are "<eventId>@<lead>", so compare on the event-id part.
            var feedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CalendarEvent e in events) if (e != null && e.Id != null) feedIds.Add(e.Id);

            Dictionary<string, bool> slots = snap.SlotHealthy;
            if (slots == null)
            {
                // Any error means this is a partial or not-yet-loaded view. Pruning against it would treat
                // "I cannot see your calendar" as "your calendar is empty".
                if (!string.IsNullOrEmpty(snap.Error)) return false;
                return fired.RemoveWhere(id => !feedIds.Contains(ReminderScheduler.EventIdOf(id))) > 0;
            }
            return fired.RemoveWhere(id =>
            {
                string slot = SlotOf(id);
                bool healthy;
                if (slot == null || !slots.TryGetValue(slot, out healthy)) return true;   // no slot claims it; none can re-fire it
                if (!healthy) return false;                                               // errored or loading: cannot vouch, keep
                return !feedIds.Contains(ReminderScheduler.EventIdOf(id));
            }) > 0;
        }

        /// <summary>The slot an aggregate id belongs to: "cal1" of "cal1|uid@15". Slot ids never contain '|', so
        /// the first one is the boundary whatever the event id holds. Null when there is none.</summary>
        internal static string SlotOf(string firedId)
        {
            if (string.IsNullOrEmpty(firedId)) return null;
            int bar = firedId.IndexOf('|');
            return bar < 0 ? null : firedId.Substring(0, bar);
        }

        private void SaveFired()
        {
            _settings.Set("fired", string.Join("\n", _fired));
            _settings.Save();
        }

        // --- self-test -------------------------------------------------------------------------------

        /// <summary>
        /// Run by the app's convention flag: <c>DesktopAICompanion.exe --module-selftest=reminder</c>, which loads this
        /// module through the REAL loader and calls this by reflection.
        ///
        /// This is the single entry point, and it exists because six pure helpers here already HAD internal
        /// checks that nothing ever ran -- they were exercised once from a throwaway console and then left
        /// unwired, which is indistinguishable from having no tests at all. They are aggregated here so the
        /// gate runs them on every change.
        ///
        /// Covers pure logic, the caching sources through test doubles, the module's own tick through
        /// ModuleKit's RecordingHost, the .ics download's bounds against a loopback server and the .ics PARSE
        /// against an embedded feed (IcsUrlSource.SelfCheck, F190). The WinForms timer's firing, the Outlook
        /// COM path and a real network are not reachable here: they need a message loop, a running Outlook and
        /// a feed host respectively.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // The pure helpers' own checks: six that existed and were never run, plus the ICS parser's (F190).
            var suites = new[]
            {
                new { Name = "QuietHours", Run = (SelfCheckDelegate)QuietHours.SelfCheck },
                new { Name = "ReminderScheduler", Run = (SelfCheckDelegate)ReminderScheduler.SelfCheck },
                new { Name = "AggregateCalendarSource", Run = (SelfCheckDelegate)AggregateCalendarSource.SelfCheck },
                new { Name = "MeetingLinkDetector", Run = (SelfCheckDelegate)MeetingLinkDetector.SelfCheck },
                new { Name = "PersonalReminder", Run = (SelfCheckDelegate)PersonalReminder.SelfCheck },
                new { Name = "PersonalReminderParser", Run = (SelfCheckDelegate)PersonalReminderParser.SelfCheck },
                new { Name = "IcsUrlSource", Run = (SelfCheckDelegate)IcsUrlSource.SelfCheck },
            };

            foreach (var suite in suites)
            {
                string suiteDetail;
                bool suiteOk;
                try { suiteOk = suite.Run(out suiteDetail); }
                catch (Exception ex) { suiteOk = false; suiteDetail = ex.GetType().Name + ": " + ex.Message; }
                sb.AppendLine((suiteOk ? "PASS: " : "FAIL: ") + suite.Name);
                if (!string.IsNullOrWhiteSpace(suiteDetail))
                    foreach (string line in suiteDetail.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                        sb.AppendLine("    " + line);
                if (!suiteOk) ok = false;
            }

            // The 1.7.0 reaction: the candidate list the module hands to IHost.PlayAnimationAll.
            Action<string, bool> check = (name, condition) =>
            {
                sb.AppendLine((condition ? "PASS: " : "FAIL: ") + name);
                if (!condition) ok = false;
            };

            // The one slot lookup the four per-slot helpers share (F198): every slot id maps to its index, and
            // null, blank, an out-of-range slot, a foreign id and a case variant all map to 0, which the helpers
            // read as "the default".
            bool slotMap = true;
            for (int slotNumber = 1; slotNumber <= MaxSlots; slotNumber++)
                slotMap &= SlotIndex(SlotId(slotNumber)) == slotNumber;
            check("every slot id maps to its index and nothing else maps at all",
                slotMap && SlotIndex(null) == 0 && SlotIndex("") == 0 &&
                SlotIndex(SlotId(MaxSlots + 1)) == 0 && SlotIndex("foo") == 0 && SlotIndex("CAL1") == 0);

            // ---- A RESTART MUST NOT RE-NAG ----
            // The fired set used to be pruned against the feed unconditionally, before any look at
            // snap.Error. An empty feed is routine here: CachingCalendarSource.Fetch captures its cache
            // BEFORE kicking the background refresh, so the first call of every launch returns no events with
            // Error set, and Init calls CheckDue directly. The set was therefore wiped and written to disk on
            // every start, and a meeting still inside its lead window announced a second time.
            Func<string, CalendarEvent[], CalendarSnapshot> snapOf = (err, evs) =>
                new CalendarSnapshot { Events = evs, Error = err };
            Func<HashSet<string>> firedOf = () =>
                new HashSet<string>(new[] { "cal1|uid@15", "cal1|other@5" }, StringComparer.Ordinal);

            // The exact shape of the first tick of every launch.
            HashSet<string> f1 = firedOf();
            bool pruned1 = PruneFiredAgainstFeed(
                snapOf("Loading the calendar…", Array.Empty<CalendarEvent>()), f1);
            check("a still-loading feed prunes nothing (pruned=" + pruned1 + " count=" + f1.Count + ")", !pruned1 && f1.Count == 2);

            // A feed that failed outright, for the same reason: this is not evidence the calendar is empty.
            HashSet<string> f2 = firedOf();
            PruneFiredAgainstFeed(snapOf("cal1: the server said 503", Array.Empty<CalendarEvent>()), f2);
            check("a failed feed prunes nothing", f2.Count == 2);

            // A PARTIAL failure still carries the healthy slots' events, and its ids must survive too: the
            // combined error means one slot failed, not that the missing events were cancelled.
            HashSet<string> f3 = firedOf();
            PruneFiredAgainstFeed(
                snapOf("cal2: unreachable", new[] { new CalendarEvent { Id = "cal1|uid" } }), f3);
            check("a partial failure keeps ids the surviving slot cannot vouch for", f3.Count == 2);

            // And the case the prune EXISTS for still works, or this would be a fix that removed the feature.
            HashSet<string> f4 = firedOf();
            bool pruned4 = PruneFiredAgainstFeed(
                snapOf("", new[] { new CalendarEvent { Id = "cal1|uid" } }), f4);
            check("a loaded feed still drops an id whose event is gone",
                pruned4 && f4.Count == 1 && f4.Contains("cal1|uid@15"));

            // A calendar that loaded and is genuinely empty: every id is stale and all of them go.
            HashSet<string> f5 = firedOf();
            check("a loaded but empty calendar drops everything",
                PruneFiredAgainstFeed(snapOf("", Array.Empty<CalendarEvent>()), f5) && f5.Count == 0);

            // Nothing to do is not a change, or CheckDue would write the settings file on every tick.
            check("an unchanged set reports no change",
                !PruneFiredAgainstFeed(snapOf("", new[] { new CalendarEvent { Id = "cal1|uid" },
                                                          new CalendarEvent { Id = "cal1|other" } }), firedOf()));

            IReadOnlyList<string> defaults = ParseAnimationCandidates(DefaultReactAnimations);
            // PROPERTIES, NOT THE LITERAL STRING. These three used to assert Count == 4,
            // [0] == "boing" and [3] == "flower", which tested the string rather than anything about
            // behaviour -- so raising the list's coverage from 35/54 to 43/54 reddened the gate for
            // no reason connected to what the module does.
            check("the default reaction list parses to several candidates", defaults.Count >= 4);
            bool headInOrder = defaults.Count >= HistoricalReactAnimations.Length;
            if (headInOrder)
            {
                for (int i = 0; i < HistoricalReactAnimations.Length; i++)
                    if (defaults[i] != HistoricalReactAnimations[i]) headInOrder = false;
            }
            // The behaviour-preservation property: a companion that already reacted must keep playing
            // the same animation. A reorder is the plausible edit here, because sorting the list by
            // corpus frequency would look like an improvement.
            check("WITNESS the historical four still lead, in order, so an already-reacting companion is unchanged",
                headInOrder);
            check("there is a fallback beyond the historical four, for the companions that defined none of them",
                defaults.Count > HistoricalReactAnimations.Length);
            bool noReserved = true;
            string reservedOffender = "";
            foreach (string c in defaults)
                foreach (string reserved in ReservedLifecycleAnimations)
                    if (string.Equals(c, reserved, StringComparison.OrdinalIgnoreCase))
                    { noReserved = false; reservedOffender = c; }
            check("no reserved lifecycle animation is offered as a reaction" +
                (noReserved ? "" : " -- it offers '" + reservedOffender + "'"), noReserved);
            check("whitespace and empty entries are dropped",
                ParseAnimationCandidates(" jump , , run ,").Count == 2);
            check("duplicates are collapsed case-insensitively",
                ParseAnimationCandidates("jump,JUMP,Jump").Count == 1);
            check("a blank list yields no candidates, so the reaction is a no-op",
                ParseAnimationCandidates("   ").Count == 0);
            check("a null list yields no candidates", ParseAnimationCandidates(null).Count == 0);

            // ---- the feed survives a failure, and a local file is not read on the caller's thread ----
            // CALENDAR-FEED.md has always claimed "a parse failure is non-fatal, the companion keeps the
            // last good feed". It was true only of a fetch that THREW; a subclass returning an error
            // snapshot, which is what FetchCore's own doc comment tells them to do, blanked the feed.
            var retention = new FeedRetentionProbe();
            var monday = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
            retention.Next = new CalendarSnapshot
            {
                Events = new List<CalendarEvent> { new CalendarEvent { Id = "a", Title = "Standup", Start = monday } },
            };
            CalendarSnapshot settled = WaitForFeed(retention, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error == null && snap.Events != null && snap.Events.Count == 1;
            });
            check("a caching source serves its events once the background fetch lands",
                settled != null);

            retention.Next = new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "the share went away" };
            CalendarSnapshot failed = WaitForFeed(retention, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error != null;
            });
            check("a failed refresh keeps the last good feed instead of blanking it",
                failed != null && failed.Events != null && failed.Events.Count == 1);
            check("...and still reports the error, so nothing treats the stale feed as fresh",
                failed != null && failed.Error == "the share went away");

            // WITNESS: an error snapshot that brought events of its OWN keeps them. The restore is a
            // fallback for a blank, not an override.
            retention.Next = new CalendarSnapshot
            {
                Events = new List<CalendarEvent>
                {
                    new CalendarEvent { Id = "b", Title = "Partial", Start = monday },
                    new CalendarEvent { Id = "c", Title = "Partial two", Start = monday },
                },
                Error = "partial",
            };
            CalendarSnapshot partial = WaitForFeed(retention, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error == "partial";
            });
            check("WITNESS an error snapshot carrying its own events keeps them, not the last good one",
                partial != null && partial.Events != null && partial.Events.Count == 2);

            // And the reason LocalJsonSource was moved onto that base at all: it used to read the file on
            // the UI thread every 20 seconds, and CALENDAR-FEED.md describes the path as a work-side
            // exporter's output -- in practice a share, which blocks on the SMB timeout when the VPN drops.
            // A first Fetch that answers with events is a first Fetch that read the disk synchronously.
            string probeDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dp-reminder-feed-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(probeDirectory);
                string feedPath = System.IO.Path.Combine(probeDirectory, "feed.json");
                System.IO.File.WriteAllText(feedPath,
                    "{\"events\":[{\"id\":\"a\",\"title\":\"Standup\",\"start\":\"2026-01-05T09:00:00+00:00\"}]}");
                var local = new LocalJsonSource(delegate { return feedPath; });
                CalendarSnapshot firstTick = local.Fetch();
                check("a local feed is not read on the caller's thread (the first tick answers 'reading…')",
                    firstTick != null && firstTick.Error != null &&
                    firstTick.Events != null && firstTick.Events.Count == 0);
                CalendarSnapshot arrived = WaitForFeed(local, delegate(CalendarSnapshot snap)
                {
                    return snap != null && snap.Error == null && snap.Events != null && snap.Events.Count == 1;
                });
                check("...and the events arrive once the background read lands",
                    arrived != null && arrived.Events[0].Id == "a");
            }
            finally
            {
                try { System.IO.Directory.Delete(probeDirectory, true); } catch { }
            }

            // ---- the read happens OFF the calling thread, asserted on the THREAD ----
            // The first-tick check above pins that LocalJsonSource answers the loading snapshot first. It
            // does NOT pin where the read runs: Fetch captures the (null) cache BEFORE it kicks the refresh
            // and returns what it captured, so an inline DoRefresh -- the synchronous SMB read on the UI
            // thread that 1.0.4 exists to prevent -- passes it (F205). This probe records the thread
            // FetchCore runs on and holds the read open on a gate: Fetch must return while the read is
            // still in progress, and the read must have run on a different thread. The gate is released in
            // a finally with a bounded wait behind it, so a failed assertion costs seconds, not a hang.
            var threadProbe = new ThreadRecordingProbe();
            try
            {
                int callerThread = Environment.CurrentManagedThreadId;
                CalendarSnapshot ticked = threadProbe.Fetch();
                check("Fetch returns before the read completes (the read is not on the caller's thread)",
                    ticked != null && !threadProbe.Completed);
                bool started = System.Threading.SpinWait.SpinUntil(
                    delegate { return threadProbe.Started; }, TimeSpan.FromSeconds(3));
                check("WITNESS the background read did start", started);
                check("the read ran on a different thread from the caller",
                    started && threadProbe.FetchThreadId != 0 && threadProbe.FetchThreadId != callerThread);
                check("IsRefreshing answers true while the read is in flight", ticked != null && threadProbe.IsRefreshing);
            }
            finally
            {
                threadProbe.Gate.Set();
            }
            check("...and false once it has landed",
                System.Threading.SpinWait.SpinUntil(delegate { return !threadProbe.IsRefreshing; }, TimeSpan.FromSeconds(10)));

            // ---- a refresh that never returns (F186) ----
            // The latch was one bool cleared only when DoRefresh finished, so a FetchCore that blocked for
            // good -- Outlook's Object Model Guard prompt, a hung Outlook, a body the server never finishes --
            // froze the slot: every later Fetch saw "already refreshing", served the old cache with no error,
            // and never kicked again. This probe blocks each attempt behind its own gate, so the test chooses
            // which attempt returns first.
            var stalled = new StallingProbe();
            try
            {
                // The spin bounds are generous (10 s) because they only matter when something is wrong; the
                // sleeps are what the deadline is measured against, and more time under load only helps them.
                CalendarSnapshot firstStalled = stalled.Fetch();
                bool attemptOne = System.Threading.SpinWait.SpinUntil(delegate { return stalled.Starts == 1; }, TimeSpan.FromSeconds(10));
                check("WITNESS the first refresh attempt started and is in flight",
                    attemptOne && stalled.IsRefreshing && firstStalled != null && firstStalled.Error != null);
                System.Threading.Thread.Sleep(350);   // past the probe's 150 ms deadline
                CalendarSnapshot overdue = stalled.Fetch();
                check("a refresh that outlives its deadline is reported on the served snapshot",
                    overdue != null && overdue.Error != null && overdue.Error.Contains("has not completed"));
                bool attemptTwo = System.Threading.SpinWait.SpinUntil(delegate { return stalled.Starts == 2; }, TimeSpan.FromSeconds(10));
                check("...and one more attempt is started while the first stays parked",
                    attemptTwo && stalled.OutstandingRefreshes == 2);
                System.Threading.Thread.Sleep(350);
                stalled.Fetch();
                System.Threading.Thread.Sleep(50);
                check("parked attempts are capped at " + CachingCalendarSource.MaximumOutstandingRefreshes
                      + ", so a hung source cannot gain a thread per deadline", stalled.Starts == 2);
                CalendarSnapshot stillStalled = stalled.Fetch();
                check("WITNESS the stall stays reported while nothing has landed",
                    stillStalled != null && stillStalled.Error != null && stillStalled.Error.Contains("has not completed"));

                // The RETRY lands first: it is the newest attempt, so it is served and the report clears.
                stalled.Gates[2].Set();
                bool landed = System.Threading.SpinWait.SpinUntil(delegate { return !stalled.IsRefreshing; }, TimeSpan.FromSeconds(10));
                CalendarSnapshot fresh = stalled.Fetch();
                check("once the retry lands its result is served and the stall report clears",
                    landed && fresh != null && fresh.Error == null && fresh.Events != null
                    && fresh.Events.Count == 1 && fresh.Events[0].Id == "attempt2");
                // Then the ABANDONED attempt returns, late. Its data predates what is being served.
                stalled.Gates[1].Set();
                bool drained = System.Threading.SpinWait.SpinUntil(delegate { return stalled.OutstandingRefreshes == 0; }, TimeSpan.FromSeconds(10));
                CalendarSnapshot after = stalled.Fetch();
                check("a result from the abandoned attempt, landing late, does not overwrite the newer one",
                    drained && after != null && after.Events != null && after.Events.Count == 1 && after.Events[0].Id == "attempt2");
            }
            finally
            {
                stalled.ReleaseAll();
            }

            // ---- the refresh key is computed on the caller's thread only (F192) ----
            // The getters behind RefreshKey read the module's settings store, a bare Dictionary the UI thread
            // writes on every Apply. LocalJsonSource and IcsUrlSource used to call RefreshKey() AGAIN inside
            // FetchCore, on the pool thread, although the base had the key in hand: it computed it in Fetch,
            // threaded it through StartRefresh into DoRefresh, and then called FetchCore without it.
            var keyProbe = new KeyThreadProbe();
            int keyCaller = Environment.CurrentManagedThreadId;
            CalendarSnapshot keyed = WaitForFeed(keyProbe, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error == null && snap.Events != null && snap.Events.Count == 1;
            });
            check("WITNESS the keyed source landed its fetch", keyed != null);
            check("the background fetch is handed the key the caller computed", keyProbe.KeyGiven == "the-configured-path");
            int[] keyThreads = keyProbe.KeyThreads;
            bool keyOnCallerOnly = keyThreads.Length > 0;
            foreach (int t in keyThreads) if (t != keyCaller) keyOnCallerOnly = false;
            check("RefreshKey(), which reads the settings, never ran off the caller's thread", keyOnCallerOnly);

            // ---- the file slot's interval sits below the tick (F191) ----
            // Equal to the tick, whether a tick counted as stale came down to timer lateness versus read
            // latency, so the file was re-read every tick on a fast disk and every other tick on a share.
            check("the local file interval sits below the module tick, so every tick kicks a re-read",
                new LocalJsonSource(delegate { return "unused"; }).RefreshInterval < TimeSpan.FromMilliseconds(TickMilliseconds));

            // ---- an .ics download is bounded in time and in size WHILE the body arrives (F189) ----
            // HttpClient.Timeout ends at the headers under ResponseHeadersRead and the body was then read with
            // no token, so a server that sent 200 and stalled held the fetch, and the slot's latch, for good;
            // and the 8 MiB cap was checked after the whole body had been buffered. Both against a loopback
            // server that behaves exactly that way; nothing real is contacted. The TEST is bounded too, so the
            // old shape fails an assertion here rather than hanging the suite.
            // A 3 s deadline, not a few hundred ms: on a loaded machine the client's request can take that long
            // to reach even a loopback server, and a deadline that fires before the headers went out throws the
            // same TimeoutException while proving nothing about the BODY. The WITNESS below is what tells the two
            // apart, and it waits for the server's own record rather than reading it the instant the client gave up.
            using (var stallServer = new StallingFeedServer(StallingFeedServer.Mode.Stall, 0))
            {
                string outcome = BoundedDownload(stallServer.Url, TimeSpan.FromSeconds(3), 1024 * 1024, TimeSpan.FromSeconds(12));
                check("a feed that sends its headers and then stalls is cut off by the deadline (" + outcome + ")",
                    outcome.StartsWith("threw", StringComparison.Ordinal) && outcome.Contains("did not finish downloading"));
                check("WITNESS the server had sent the headers and a first chunk, so it was the body that stalled",
                    System.Threading.SpinWait.SpinUntil(delegate { return stallServer.HeadersSent; }, TimeSpan.FromSeconds(5)));
            }
            using (var floodServer = new StallingFeedServer(StallingFeedServer.Mode.Flood, 2 * 1024 * 1024))
            {
                string outcome = BoundedDownload(floodServer.Url, TimeSpan.FromSeconds(10), 128 * 1024, TimeSpan.FromSeconds(12));
                check("a chunked body past the size cap is refused while it is still arriving (" + outcome + ")",
                    outcome.Contains("too large"));
            }

            // ---- a user's click re-reads the feeds (F201) ----
            // Inside its interval a source answers from its cache, and until Invalidate existed nothing in the
            // module could ask for more: "Check now" and the tray entry re-ran the due check against whatever
            // the last tick had seen. Through the aggregate, because that is what the module holds.
            var slowProbe = new FeedRetentionProbe(TimeSpan.FromHours(1));
            slowProbe.Next = new CalendarSnapshot
            {
                Events = new List<CalendarEvent> { new CalendarEvent { Id = "a", Title = "Standup", Start = monday } },
            };
            var slowAggregate = new AggregateCalendarSource(new[]
            {
                new AggregateCalendarSource.Slot { Id = "cal1", Label = "Home", Source = slowProbe },
            });
            CalendarSnapshot slowFirst = WaitForFeed(slowAggregate, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Events != null && snap.Events.Count == 1;
            });
            slowProbe.Next = new CalendarSnapshot
            {
                Events = new List<CalendarEvent>
                {
                    new CalendarEvent { Id = "a", Title = "Standup", Start = monday },
                    new CalendarEvent { Id = "b", Title = "Added since", Start = monday },
                },
            };
            slowAggregate.Fetch();
            System.Threading.Thread.Sleep(30);
            CalendarSnapshot cachedAgain = slowAggregate.Fetch();
            check("WITNESS inside its interval a source answers from its cache and does not re-read",
                slowFirst != null && cachedAgain != null && cachedAgain.Events.Count == 1 && slowProbe.Fetches == 1);
            slowAggregate.Invalidate();
            CalendarSnapshot reread = WaitForFeed(slowAggregate, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Events != null && snap.Events.Count == 2;
            });
            check("Invalidate makes the next Fetch re-read, through the aggregate to every slot",
                reread != null && slowProbe.Fetches == 2);
            check("...and IsRefreshing is false once it has landed", !slowAggregate.IsRefreshing);

            // And the button itself, on a module wired to a real host double: the due check it runs must be
            // against what the re-read brought back, not the cache it started from. Driven from a pool thread
            // so the awaits inside never wait on a message loop this process is not running.
            var checkHost = new RecordingHost();
            var checkModule = new ReminderModule();
            checkHost.SettingsFor(Id).Set("hushPresenting", "false");
            checkModule.Init(checkHost);
            try
            {
                checkModule._source = slowAggregate;
                slowProbe.Next = new CalendarSnapshot
                {
                    Events = new List<CalendarEvent>
                    {
                        new CalendarEvent { Id = "a", Title = "Standup", Start = monday },
                        new CalendarEvent { Id = "b", Title = "Added since", Start = monday },
                        new CalendarEvent { Id = "c", Title = "Added later", Start = monday },
                    },
                };
                checkModule.CheckDue();
                int seenByTick = checkModule._lastSnapshot != null && checkModule._lastSnapshot.Events != null
                    ? checkModule._lastSnapshot.Events.Count : -1;
                string clickStatus = System.Threading.Tasks.Task.Run(() => checkModule.CheckNowAsync()).GetAwaiter().GetResult();
                int seenByClick = checkModule._lastSnapshot != null && checkModule._lastSnapshot.Events != null
                    ? checkModule._lastSnapshot.Events.Count : -1;
                check("WITNESS a plain tick inside the interval served the cache (" + seenByTick + " events)", seenByTick == 2);
                check("\"Check now\" re-reads the feeds and runs the due check against what arrived (" + seenByClick + " events)",
                    seenByClick == 3 && clickStatus != null && !clickStatus.Contains("still refreshing"));
            }
            finally
            {
                checkModule.Shutdown();
            }

            // ---- the briefing time reads through the shared HH:mm parser (F203) ----
            // "+8:00" was a valid briefing time (NumberStyles.Integer) while the quiet-hours fields beside it
            // refused it. Both through the module's own BriefingDue, on a RecordingHost.
            var briefingHost = new RecordingHost();
            var briefingModule = new ReminderModule();
            FakeModuleSettings briefingSettings = briefingHost.SettingsFor(Id);
            briefingSettings.Set("briefingOn", "true");
            briefingSettings.Set("briefingTime", "+8:00");
            briefingModule.Init(briefingHost);
            try
            {
                var noonUtc = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
                string dayKey;
                bool signedDue = briefingModule.BriefingDue(noonUtc, out dayKey);
                briefingSettings.Set("briefingTime", "08:00");
                bool plainDue = briefingModule.BriefingDue(noonUtc, out dayKey);
                check("a signed briefing time is refused by the shared parser while a plain one is due", !signedDue && plainDue);
            }
            finally
            {
                briefingModule.Shutdown();
            }

            // ---- a reminder due while no companion is on screen is held, not spent (F199) ----
            // SayAll drops its line when no companion is out, and the app keeps running from the tray after
            // the last pet is removed. A due reminder in that state chimed, was added to the fired set and
            // persisted, and never appeared; a once-only personal reminder was disabled unshown; the briefing
            // was stamped as read. The quiet-hours skip already knew the right shape -- skip WITHOUT marking --
            // and now covers this too. Through the module's own tick on ModuleKit's RecordingHost, whose
            // default companion manager reports no pet and which raises spawns only when told to.
            var deliveryHost = new RecordingHost();
            var delivery = new ReminderModule();
            FakeModuleSettings deliverySettings = deliveryHost.SettingsFor(Id);
            deliverySettings.Set("hushPresenting", "false");
            deliverySettings.Set("leads", "5");
            delivery.Init(deliveryHost);
            try
            {
                DateTimeOffset dueNow = DateTimeOffset.Now;
                Func<string, ICalendarSource> oneMeeting = delegate(string id)
                {
                    return new AggregateCalendarSource(new[]
                    {
                        new AggregateCalendarSource.Slot
                        {
                            Id = "cal1", Label = "Home",
                            Source = new AggregateCalendarSource.StubSource(
                                new[] { new CalendarEvent { Id = id, Title = "Standup", Start = dueNow.AddMinutes(3) } }, null),
                        },
                    });
                };
                Func<string, int> deliveryLogged = delegate(string fragment)
                {
                    int n = 0;
                    foreach (string line in deliveryHost.LoggedLines) if (line.Contains(fragment)) n++;
                    return n;
                };

                delivery._source = oneMeeting("m1");
                delivery.CheckDue();
                check("with no companion on screen a due reminder is not spent", delivery._fired.Count == 0);
                check("...nor spoken into nothing", deliveryHost.SaidLines.Count == 0);
                check("...nor chimed or animated: the bubble is what was asked for, and it is still pending",
                    deliveryHost.PlayedSounds.Count == 0 && deliveryHost.PlayedAnimations.Count == 0);
                delivery.CheckDue();
                check("the hold is logged once, not on every tick", deliveryLogged("reminders held") == 1);

                deliveryHost.RaiseCompanionSpawned(new FakeCompanion(1, "sheep"));
                delivery.CheckDue();
                check("WITNESS the held reminder is delivered once a companion appears",
                    deliveryHost.BroadcastLines.Count == 1 && delivery._fired.Count == 1);
                check("...with its chime and its reaction",
                    deliveryHost.PlayedSounds.Count == 1 && deliveryHost.PlayedAnimations.Count > 0);
                check("...and the release is logged once", deliveryLogged("reminders resume") == 1);
                delivery.CheckDue();
                check("...and it is not repeated afterwards", deliveryHost.BroadcastLines.Count == 1 && delivery._fired.Count == 1);

                // A pet that was out BEFORE the module loaded never comes through CompanionSpawned; the
                // companion manager is what knows about it. The spawned one goes away, the manager reports one.
                deliveryHost.CompanionAlivePredicate = delegate { return false; };
                deliveryHost.CompanionManager = new OnePetOnScreenManager();
                delivery._source = oneMeeting("m2");
                delivery.CheckDue();
                // Contains, not Count: the prune drops m1's id once the feed no longer carries m1.
                check("a companion the module was never told about still counts as on screen (the manager knows it)",
                    delivery._fired.Contains("cal1|m2@5") && deliveryHost.BroadcastLines.Count == 2);

                // The once-only personal reminder and the daily briefing: same rule, same held commitments.
                deliveryHost.CompanionManager = new DenyingCompanionManager();   // nobody again
                var pizza = new PersonalReminder
                {
                    Id = "p1", Text = "Take the pizza out", Kind = PersonalReminder.KindOnce,
                    When = dueNow.AddMinutes(-1), Anchor = dueNow, Enabled = true, LastFired = "",
                };
                deliverySettings.Set("personal", PersonalReminder.Encode(pizza));
                deliverySettings.Set("briefingOn", "true");
                deliverySettings.Set("briefingTime", "00:00");
                delivery.CheckDue();
                PersonalReminder afterHold = PersonalReminder.Decode(deliverySettings.Get("personal", ""));
                check("a once-only personal reminder is not disabled while nobody can show it",
                    afterHold != null && afterHold.Enabled && afterHold.LastFired == "");
                check("the daily briefing is not stamped as read while nobody can hear it",
                    deliverySettings.Get("briefingLast", "") == "");
                deliveryHost.CompanionAlivePredicate = null;
                deliveryHost.RaiseCompanionSpawned(new FakeCompanion(2, "sheep"));
                delivery.CheckDue();
                PersonalReminder afterPet = PersonalReminder.Decode(deliverySettings.Get("personal", ""));
                check("WITNESS both are delivered once a companion is back",
                    afterPet != null && !afterPet.Enabled && afterPet.LastFired == "once"
                    && deliverySettings.Get("briefingLast", "").Length > 0
                    && deliveryHost.BroadcastLines.Count == 4);

                // Speech switched OFF is not a hold: the chime and the reaction still reach the user, and
                // AgentFlow's recorded decision is that speech-off must not withhold them. A hold here would
                // re-chime every tick until speech came back.
                deliveryHost.SpeechEnabled = false;
                delivery._source = oneMeeting("m3");
                delivery.CheckDue();
                check("WITNESS speech switched off does not hold a reminder: its chime and reaction were delivered",
                    delivery._fired.Contains("cal1|m3@5"));
            }
            finally
            {
                delivery.Shutdown();
            }

            // ---- "Test this reminder" and the Agenda tray click say so when nobody is on screen (N-reminder-03) ----
            // The F199 hold applied to the two on-demand paths: the button answered "✓ test sent" and the click
            // spoke through SayAll while the host had no companion to show either, so both looked like they
            // worked and neither did. RecordingHost records a SayAll whatever is on screen, so what is asserted
            // is that the module did not speak, chime or animate at all, and said why on the channel each path
            // has: the button's status line, and the log for the click.
            var nobodyHost = new RecordingHost();
            var nobody = new ReminderModule();
            nobodyHost.SettingsFor(Id).Set("hushPresenting", "false");
            nobody.Init(nobodyHost);
            try
            {
                Func<string, int> nobodyLogged = delegate(string fragment)
                {
                    int n = 0;
                    foreach (string line in nobodyHost.LoggedLines) if (line.Contains(fragment)) n++;
                    return n;
                };
                TrayItem agenda = null;
                foreach (TrayItem item in nobodyHost.TrayItems)
                    if (item != null && item.DynamicText != null && item.DynamicText() == "Read today's agenda") agenda = item;
                check("the Agenda tray item is registered with a click", agenda != null && agenda.Click != null);

                string testStatus = nobody.TestReminder(1);
                check("\"Test this reminder\" with no companion on screen says so instead of \"test sent\"",
                    testStatus == NoCompanionStatus);
                check("...and speaks, chimes and animates nothing",
                    nobodyHost.SaidLines.Count == 0 && nobodyHost.PlayedSounds.Count == 0
                    && nobodyHost.NotificationSoundsPlayed == 0 && nobodyHost.PlayedAnimations.Count == 0);
                if (agenda != null && agenda.Click != null) agenda.Click();
                check("the Agenda tray click with no companion on screen speaks nothing and logs why",
                    nobodyHost.BroadcastLines.Count == 0 && nobodyLogged(AgendaNobodyLogLine) == 1);

                nobodyHost.RaiseCompanionSpawned(new FakeCompanion(1, "sheep"));
                string sentStatus = nobody.TestReminder(1);
                check("WITNESS with a companion on screen the test reminder is sent and says so",
                    sentStatus.StartsWith("✓", StringComparison.Ordinal) && nobodyHost.SaidLines.Count == 1);
                int broadcastBefore = nobodyHost.BroadcastLines.Count;
                if (agenda != null && agenda.Click != null) agenda.Click();
                check("WITNESS ...and the Agenda click reads the agenda, with no second log line",
                    nobodyHost.BroadcastLines.Count == broadcastBefore + 1 && nobodyLogged(AgendaNobodyLogLine) == 1);
            }
            finally
            {
                nobody.Shutdown();
            }

            // ---- an unchanged feed error is logged once (F197) ----
            // A slot in a steady error state wrote the identical line on every 20 s tick, 4,320 a day, which
            // rotated the diagnostics log about daily and pushed out the startup record it exists to keep.
            var logHost = new RecordingHost();
            var logModule = new ReminderModule();
            logHost.SettingsFor(Id).Set("hushPresenting", "false");
            logModule.Init(logHost);
            try
            {
                Func<string, ICalendarSource> erroring = delegate(string error)
                {
                    return new AggregateCalendarSource(new[]
                    {
                        new AggregateCalendarSource.Slot
                        {
                            Id = "cal1", Label = "Work",
                            Source = new AggregateCalendarSource.StubSource(Array.Empty<CalendarEvent>(), error),
                        },
                    });
                };
                Func<string, int> feedLogged = delegate(string fragment)
                {
                    int n = 0;
                    foreach (string line in logHost.LoggedLines) if (line.Contains(fragment)) n++;
                    return n;
                };
                logModule._source = erroring("Outlook isn't running");
                logModule.CheckDue();
                logModule.CheckDue();
                logModule.CheckDue();
                check("an unchanged feed error is logged once, not on every tick", feedLogged("Outlook isn't running") == 1);
                logModule._source = erroring(null);
                logModule.CheckDue();
                logModule.CheckDue();
                check("WITNESS the recovery is logged, once", feedLogged("reminder feed: recovered") == 1);
                logModule._source = erroring("Outlook isn't running");
                logModule.CheckDue();
                check("WITNESS an error that returns is logged again", feedLogged("Outlook isn't running") == 2);
            }
            finally
            {
                logModule.Shutdown();
            }

            // ---- the prune judges each id by ITS slot (F204) ----
            // The whole-snapshot gate above is right for one source and wrong for the aggregate, which sets
            // Error whenever any slot fails and still fires the healthy slots' events: a Local Outlook slot on a
            // box where Outlook is closed kept the combined Error set for good, and the healthy slot beside it
            // added an id per reminder per lead that nothing ever removed. Through the real aggregate.
            var mixed = new AggregateCalendarSource(new[]
            {
                new AggregateCalendarSource.Slot
                {
                    Id = "cal1", Label = "Home",
                    Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "x", Title = "Live", Start = monday } }, null),
                },
                new AggregateCalendarSource.Slot
                {
                    Id = "cal2", Label = "Work",
                    Source = new AggregateCalendarSource.StubSource(Array.Empty<CalendarEvent>(), "Outlook isn't running"),
                },
            });
            CalendarSnapshot mixedSnap = mixed.Fetch();
            var f7 = new HashSet<string>(new[] { "cal1|x@15", "cal1|gone@15", "cal2|y@5", "legacy@5" }, StringComparer.Ordinal);
            bool pruned7 = PruneFiredAgainstFeed(mixedSnap, f7);
            check("a healthy slot's stale id is dropped even while another slot errors, so the set stays bounded",
                pruned7 && !f7.Contains("cal1|gone@15"));
            check("WITNESS the erroring slot's id is kept: it cannot vouch for its calendar", f7.Contains("cal2|y@5"));
            check("WITNESS the healthy slot's live id is kept", f7.Contains("cal1|x@15"));
            check("an id no configured slot claims is dropped, as it was whenever the aggregate was healthy",
                !f7.Contains("legacy@5"));

            // The first tick of every launch, per slot: the loading slot keeps its own ids (the 1.0.3 re-nag
            // fix) while its neighbour that has loaded is pruned.
            var loadingBeside = new AggregateCalendarSource(new[]
            {
                new AggregateCalendarSource.Slot
                {
                    Id = "cal1", Label = "Home",
                    Source = new AggregateCalendarSource.StubSource(Array.Empty<CalendarEvent>(), "Loading the calendar…"),
                },
                new AggregateCalendarSource.Slot
                {
                    Id = "cal2", Label = "Work",
                    Source = new AggregateCalendarSource.StubSource(new[] { new CalendarEvent { Id = "z", Title = "Live", Start = monday } }, null),
                },
            });
            var f8 = new HashSet<string>(new[] { "cal1|uid@15", "cal2|old@5" }, StringComparer.Ordinal);
            PruneFiredAgainstFeed(loadingBeside.Fetch(), f8);
            check("a slot still loading keeps its ids while its loaded neighbour is pruned (the re-nag fix holds per slot)",
                f8.Contains("cal1|uid@15") && !f8.Contains("cal2|old@5"));

            // ---- an Apply keeps the slots whose type it did not change (F200) ----
            // Save called BuildSource unconditionally and BuildSlotSource constructed fresh sources, so toggling
            // a chime checkbox threw away every slot's cache and last-good list and re-fetched every feed; the
            // next tick served "Fetching the calendar…" with no events. Through the pane's own Save delegate,
            // on a real LocalJsonSource slot reading a temp file.
            string applyDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dp-reminder-apply-" + Guid.NewGuid().ToString("N"));
            var applyHost = new RecordingHost();
            var applyModule = new ReminderModule();
            bool applyInitialised = false;
            try
            {
                System.IO.Directory.CreateDirectory(applyDirectory);
                string applyFeed = System.IO.Path.Combine(applyDirectory, "feed.json");
                System.IO.File.WriteAllText(applyFeed,
                    "{\"events\":[{\"id\":\"a\",\"title\":\"Standup\",\"start\":\"2026-01-05T09:00:00+00:00\"}]}");
                FakeModuleSettings applySettings = applyHost.SettingsFor(Id);
                applySettings.Set(SlotKey(1, "type"), SourceLocalFile);
                applySettings.Set(SlotKey(1, "file"), applyFeed);
                applySettings.Set("hushPresenting", "false");
                applyModule.Init(applyHost);
                applyInitialised = true;
                ICalendarSource before = applyModule._slotSources[1];
                CalendarSnapshot loaded = WaitForFeed(applyModule._source, delegate(CalendarSnapshot snap)
                {
                    return snap != null && snap.Error == null && snap.Events != null && snap.Events.Count == 1;
                });
                check("WITNESS the configured file slot has a source and its feed has landed", before != null && loaded != null);
                OptionsPane pane = applyHost.OptionsPanes.Count > 0 ? applyHost.OptionsPanes[0] : null;
                check("WITNESS the module registered its options pane with a Save", pane != null && pane.Save != null);
                if (pane != null && pane.Save != null)
                {
                    pane.Save(new Dictionary<string, string> { { SlotKey(1, "chimeOn"), "false" } });
                    CalendarSnapshot afterApply = applyModule._source.Fetch();
                    check("an Apply that did not change the slot's type keeps its source instance",
                        ReferenceEquals(before, applyModule._slotSources[1]));
                    check("...so its events are still served rather than a loading message",
                        afterApply != null && afterApply.Error == null && afterApply.Events != null && afterApply.Events.Count == 1);
                    pane.Save(new Dictionary<string, string> { { SlotKey(1, "type"), SourceOff } });
                    check("WITNESS a type change to Off drops the slot's source", applyModule._slotSources[1] == null);
                    pane.Save(new Dictionary<string, string> { { SlotKey(1, "type"), SourceLocalFile } });
                    check("WITNESS ...and back on is a fresh instance",
                        applyModule._slotSources[1] != null && !ReferenceEquals(before, applyModule._slotSources[1]));
                }
            }
            finally
            {
                if (applyInitialised) applyModule.Shutdown();
                try { System.IO.Directory.Delete(applyDirectory, true); } catch { }
            }

            // ---- a custom chime is read off the caller's thread, one at a time (F188) ----
            // Chime.Play read the custom file (a FileInfo probe plus up to 8 MiB) and handed it to the host, which
            // decodes it synchronously, all on the caller's thread -- the module's UI-thread tick -- on every
            // fire; and the Browse dialog accepts a share, where the probe alone blocks for the SMB timeout once
            // the VPN is down. The loader is the seam: a gated one proves the read left the thread without a
            // file that can be made slow, and that a second chime during the read is dropped, not stacked.
            var chimeHost = new RecordingHost();
            var chimeRead = new ChimeReadProbe();
            try
            {
                int chimeCaller = Environment.CurrentManagedThreadId;
                Chime.Play(chimeHost, "custom.mp3", chimeRead.Load);
                check("a custom chime's read does not run on the caller's thread: Play returns while the read is still open",
                    !chimeRead.Completed);
                Chime.Play(chimeHost, "custom.mp3", chimeRead.Load);
                bool readStarted = System.Threading.SpinWait.SpinUntil(delegate { return chimeRead.Calls >= 1; }, TimeSpan.FromSeconds(10));
                check("WITNESS the read did start", readStarted);
                check("the read ran on a different thread from the caller",
                    readStarted && chimeRead.ThreadId != 0 && chimeRead.ThreadId != chimeCaller);
                check("a second chime while the first is still being read is dropped, not stacked", chimeRead.Calls == 1);
                chimeRead.Gate.Set();
                bool played = System.Threading.SpinWait.SpinUntil(delegate { return chimeHost.PlayedSounds.Count >= 1; }, TimeSpan.FromSeconds(10));
                check("...and the bytes the read returned reach the host once it lands",
                    played && chimeHost.PlayedSounds.Count == 1 && chimeHost.PlayedSounds[0].Length == 3);
                Chime.Play(chimeHost, "", chimeRead.Load);
                check("WITNESS the embedded default chime is handed to the host synchronously and reads no file",
                    chimeHost.PlayedSounds.Count == 2 && chimeRead.Calls == 1);
            }
            finally
            {
                chimeRead.Gate.Set();
            }

            // ---- retained events belong to the key that produced them (R-043) ----
            // F200 keeps a slot's source across Apply, which made the key-change branch in Fetch reachable on
            // a populated cache for the first time: a URL or file edited to a target that fails served the
            // previous target's cache as healthy for one tick, then its last-good events behind the new
            // target's error for as long as that target failed. The key is the calendar's identity.
            var keyedProbe = new KeyedProbe();
            var eventsFromA = new List<CalendarEvent> { new CalendarEvent { Id = "a", Title = "From A", Start = monday } };
            keyedProbe.Answer = delegate(string key)
            {
                return key == "A"
                    ? new CalendarSnapshot { Events = eventsFromA }
                    : new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "the new address does not answer" };
            };
            CalendarSnapshot fromA = WaitForFeed(keyedProbe, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error == null && snap.Events != null && snap.Events.Count == 1;
            });
            check("WITNESS the first target's events landed", fromA != null);
            keyedProbe.Key = "B";   // the user Applied a new address, and it fails
            CalendarSnapshot interim = keyedProbe.Fetch();
            check("the tick after a key change does not serve the previous target's events as healthy",
                interim != null && interim.Error != null && (interim.Events == null || interim.Events.Count == 0));
            CalendarSnapshot fromB = WaitForFeed(keyedProbe, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error != null && snap.Error.Contains("does not answer");
            });
            check("a target that fails after a key change serves nothing behind its error, not the previous target's events",
                fromB != null && (fromB.Events == null || fromB.Events.Count == 0));
            // WITNESS: the SAME key failing still falls back to its own last-good list, which is the 1.0.4
            // retention this change must not remove.
            keyedProbe.Key = "A";
            CalendarSnapshot backToA = WaitForFeed(keyedProbe, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error == null && snap.Events != null && snap.Events.Count == 1;
            });
            keyedProbe.Answer = delegate(string key)
            {
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "A went away" };
            };
            keyedProbe.Invalidate();
            CalendarSnapshot aFailed = WaitForFeed(keyedProbe, delegate(CalendarSnapshot snap)
            {
                return snap != null && snap.Error == "A went away";
            });
            check("WITNESS a same-key refresh that fails still falls back to that key's last-good events",
                backToA != null && aFailed != null && aFailed.Events != null && aFailed.Events.Count == 1 && aFailed.Events[0].Id == "a");

            detail = sb.ToString();
            return ok;
        }

        /// <summary>A caching source whose key the test switches and whose answer depends on the key it was
        /// handed (R-043): key "A" is a calendar that answers, anything else a target that fails. A one-hour
        /// interval, so only a key change or Invalidate kicks a refresh.</summary>
        private sealed class KeyedProbe : CachingCalendarSource
        {
            internal volatile string Key = "A";
            internal volatile Func<string, CalendarSnapshot> Answer;
            internal KeyedProbe() : base(TimeSpan.FromHours(1)) { }
            public override string Name { get { return "keyed probe"; } }
            protected override string RefreshKey() { return Key; }
            protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
            {
                Func<string, CalendarSnapshot> answer = Answer;
                return answer != null ? answer(key) : null;
            }
        }

        /// <summary>A custom-chime loader that records the thread it ran on and waits on a gate, so the self-test
        /// can prove Chime.Play returned while the read was still open and that the read was not on the caller's
        /// thread (F188). Bounded wait, so a forgotten gate costs seconds rather than a parked pool thread.</summary>
        private sealed class ChimeReadProbe
        {
            internal readonly System.Threading.ManualResetEventSlim Gate = new System.Threading.ManualResetEventSlim(false);
            private int _calls;
            internal volatile bool Completed;
            internal volatile int ThreadId;
            internal int Calls { get { return System.Threading.Volatile.Read(ref _calls); } }
            internal byte[] Load(string path)
            {
                System.Threading.Interlocked.Increment(ref _calls);
                ThreadId = Environment.CurrentManagedThreadId;
                Gate.Wait(TimeSpan.FromSeconds(10));
                Completed = true;
                return new byte[] { 1, 2, 3 };
            }
        }

        /// <summary>A companion manager that reports one pet on screen and refuses everything else, for the
        /// F199 check that a pet the module was never told about -- out before the module loaded -- still
        /// counts. Everything but OnScreenMix is the denying manager's answer.</summary>
        private sealed class OnePetOnScreenManager : ICompanionManager
        {
            private readonly ICompanionManager _deny = new DenyingCompanionManager();
            public string CompanionsDirectory { get { return _deny.CompanionsDirectory; } }
            public IReadOnlyList<CompanionTypeInfo> InstalledTypes() { return _deny.InstalledTypes(); }
            public bool TryReadTypeXml(string typeId, out string animationsXml, out string error) { return _deny.TryReadTypeXml(typeId, out animationsXml, out error); }
            public IReadOnlyList<CompanionCount> OnScreenMix() { return new List<CompanionCount> { new CompanionCount { TypeId = "sheep", Count = 1 } }; }
            public int MaxCompanions { get { return _deny.MaxCompanions; } }
            public bool IsAtMax { get { return _deny.IsAtMax; } }
            public bool SpawnOne(string typeId) { return _deny.SpawnOne(typeId); }
            public bool RemoveOne(string typeId) { return _deny.RemoveOne(typeId); }
            public bool ValidateXml(string animationsXml, out string error) { return _deny.ValidateXml(animationsXml, out error); }
            public ICompanionPreview SpawnPreview(string animationsXml, out string error) { return _deny.SpawnPreview(animationsXml, out error); }
            public bool InstallType(string typeId, string animationsXml, out string error) { return _deny.InstallType(typeId, animationsXml, out error); }
            public bool UninstallType(string typeId, out string error) { return _deny.UninstallType(typeId, out error); }
        }

        /// <summary>Run IcsUrlSource.Download against a loopback feed with a bound on how long the TEST waits,
        /// so a download that hangs -- the defect under test -- fails an assertion instead of hanging the
        /// suite. Returns a one-line outcome the assertion quotes.</summary>
        private static string BoundedDownload(string url, TimeSpan deadline, long maximumBytes, TimeSpan bound)
        {
            System.Threading.Tasks.Task<string> task = System.Threading.Tasks.Task.Run(
                () => IcsUrlSource.Download(url, deadline, maximumBytes));
            try
            {
                if (!task.Wait(bound))
                    return "hung past the " + ((int)bound.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s bound";
                return "returned " + (task.Result ?? "").Length.ToString(CultureInfo.InvariantCulture) + " chars";
            }
            catch (AggregateException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return "threw " + inner.GetType().Name + ": " + inner.Message;
            }
        }

        private delegate bool SelfCheckDelegate(out string detail);

        /// <summary>Poll a caching source until its snapshot satisfies <paramref name="until"/>, or give up.
        /// Polling rather than a handshake because Fetch's contract IS "answer now, refresh behind you" --
        /// a test that waited on a signal would be testing something the source does not promise. Returns
        /// null on timeout so the caller's assertion fails rather than hanging the suite.</summary>
        private static CalendarSnapshot WaitForFeed(ICalendarSource source, Func<CalendarSnapshot, bool> until)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                CalendarSnapshot snap = source.Fetch();
                if (until(snap)) return snap;
                System.Threading.Thread.Sleep(15);
            }
            return null;
        }

        /// <summary>A caching source whose next answer the test chooses. Zero refresh interval, so every
        /// Fetch kicks a fresh background pass and the test never waits out a real one.</summary>
        private sealed class FeedRetentionProbe : CachingCalendarSource
        {
            internal CalendarSnapshot Next;
            private int _fetches;
            internal FeedRetentionProbe() : this(TimeSpan.Zero) { }
            /// <summary>A long interval makes it a source that answers from its cache, which is what the
            /// Invalidate checks need: a re-read that would have happened anyway proves nothing about the click.</summary>
            internal FeedRetentionProbe(TimeSpan interval) : base(interval) { }
            internal int Fetches { get { return System.Threading.Volatile.Read(ref _fetches); } }
            public override string Name { get { return "retention probe"; } }
            protected override string RefreshKey() { return ""; }
            protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
            {
                System.Threading.Interlocked.Increment(ref _fetches);
                return Next;
            }
        }

        /// <summary>A caching source whose fetch blocks until the test releases THAT attempt (F186). Per-attempt
        /// gates, so the test chooses which attempt lands first and can prove the generation rule: a result from
        /// an abandoned attempt, landing after a newer one, is discarded. The deadline is milliseconds here and
        /// the interval an hour, so nothing but the stall can kick a second refresh. Every gate has a bounded
        /// wait behind it (15 s: long enough that a loaded machine cannot release an attempt before the test
        /// means to), so a failed assertion costs seconds rather than a parked thread for the run.</summary>
        private sealed class StallingProbe : CachingCalendarSource
        {
            internal readonly System.Threading.ManualResetEventSlim[] Gates =
            {
                new System.Threading.ManualResetEventSlim(false),   // index 0 unused: attempts count from 1
                new System.Threading.ManualResetEventSlim(false),
                new System.Threading.ManualResetEventSlim(false),
                new System.Threading.ManualResetEventSlim(false),
            };
            private int _starts;
            internal StallingProbe() : base(TimeSpan.FromHours(1)) { }
            internal int Starts { get { return System.Threading.Volatile.Read(ref _starts); } }
            public override string Name { get { return "stalling probe"; } }
            protected override string RefreshKey() { return ""; }
            protected override TimeSpan RefreshDeadline { get { return TimeSpan.FromMilliseconds(150); } }
            protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
            {
                int attempt = System.Threading.Interlocked.Increment(ref _starts);
                if (attempt < Gates.Length) Gates[attempt].Wait(TimeSpan.FromSeconds(15));
                return new CalendarSnapshot
                {
                    Events = new List<CalendarEvent>
                    {
                        new CalendarEvent
                        {
                            Id = "attempt" + attempt.ToString(CultureInfo.InvariantCulture),
                            Title = "Stalled",
                            Start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
                        },
                    },
                };
            }
            internal void ReleaseAll() { foreach (System.Threading.ManualResetEventSlim gate in Gates) gate.Set(); }
        }

        /// <summary>A caching source that records the thread every RefreshKey() call runs on and the key its
        /// FetchCore was handed (F192). In the shipped sources the getter behind RefreshKey reads the settings
        /// store, so the property under test is "only ever on the caller's thread".</summary>
        private sealed class KeyThreadProbe : CachingCalendarSource
        {
            private readonly List<int> _keyThreads = new List<int>();
            internal volatile string KeyGiven;
            internal KeyThreadProbe() : base(TimeSpan.Zero) { }
            public override string Name { get { return "key probe"; } }
            internal int[] KeyThreads { get { lock (_keyThreads) return _keyThreads.ToArray(); } }
            protected override string RefreshKey()
            {
                lock (_keyThreads) _keyThreads.Add(Environment.CurrentManagedThreadId);
                return "the-configured-path";
            }
            protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
            {
                KeyGiven = key;
                return new CalendarSnapshot
                {
                    Events = new List<CalendarEvent>
                    {
                        new CalendarEvent { Id = "k", Title = "Keyed", Start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero) },
                    },
                };
            }
        }

        /// <summary>A caching source whose read records the thread it ran on and waits on a gate, so the
        /// self-test can prove Fetch returned while the read was still running and that the read was not
        /// on the caller's thread (F205). The wait is bounded so a test that forgets the gate costs three
        /// seconds rather than a hang.</summary>
        private sealed class ThreadRecordingProbe : CachingCalendarSource
        {
            internal readonly System.Threading.ManualResetEventSlim Gate = new System.Threading.ManualResetEventSlim(false);
            internal volatile bool Started;
            internal volatile bool Completed;
            internal volatile int FetchThreadId;
            internal ThreadRecordingProbe() : base(TimeSpan.Zero) { }
            public override string Name { get { return "thread probe"; } }
            protected override string RefreshKey() { return ""; }
            protected override CalendarSnapshot FetchCore(string key, DateTimeOffset now)
            {
                FetchThreadId = Environment.CurrentManagedThreadId;
                Started = true;
                Gate.Wait(TimeSpan.FromSeconds(3));
                Completed = true;
                return new CalendarSnapshot
                {
                    Events = new List<CalendarEvent>
                    {
                        new CalendarEvent { Id = "t", Title = "Thread", Start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero) },
                    },
                };
            }
        }
    }
}
