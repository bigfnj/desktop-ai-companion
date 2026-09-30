using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Base for a calendar source whose fetch may be slow (a network GET, an Outlook COM call) and so must
    /// never run on the caller's UI thread. <see cref="Fetch"/> returns the last cached snapshot immediately
    /// and kicks a throttled background refresh; a failed refresh keeps serving the last good events. Subclasses
    /// implement only the actual fetch (<see cref="FetchCore"/>) and say what config change forces an early
    /// refresh (<see cref="RefreshKey"/>).
    ///
    /// A refresh that never RETURNS is handled too. The latch used to be one bool, cleared only at the end of
    /// DoRefresh, so a FetchCore that blocked for good -- Outlook's Object Model Guard prompt, a hung Outlook,
    /// a body read the server never finishes -- parked its thread and left the flag set: every later Fetch saw
    /// "already refreshing", served the old cache with Error == null, and never kicked again. The pane kept
    /// its green tick, the log said nothing, meetings added after that moment never announced, and the only
    /// recovery was an options Apply happening to rebuild the source (F186, F189). Now a kick stamps its start
    /// time and a generation number: once the newest attempt is older than <see cref="RefreshDeadline"/> the
    /// served snapshot says so in its Error (the status line shows the warning, the tick logs it, and the
    /// fired-id prune treats the slot as unvouched), one more attempt is kicked, and a result from an
    /// abandoned attempt that lands after a newer one is discarded rather than clobbering it. Parked attempts
    /// are capped at <see cref="MaximumOutstandingRefreshes"/>: a source that is permanently hung would
    /// otherwise gain one parked thread per deadline for the life of the process.
    ///
    /// Rejected: aborting the parked thread. Thread.Abort is gone from .NET Core, and a COM call blocked
    /// inside Outlook could not be interrupted from outside anyway; the parked attempt is left to return in
    /// its own time and is then ignored.
    /// </summary>
    public abstract class CachingCalendarSource : ICalendarSource
    {
        /// <summary>The most refresh attempts that may be parked at once. Two, not one: the second is the retry
        /// after the first outlived its deadline; a third would only be another thread on the same hung source,
        /// so past this the stall is reported on every Fetch and nothing more is started until one returns.</summary>
        internal const int MaximumOutstandingRefreshes = 2;

        // A deadline shorter than this would abandon an Outlook enumeration that is merely slow.
        private static readonly TimeSpan MinimumRefreshDeadline = TimeSpan.FromSeconds(60);

        private readonly TimeSpan _refreshInterval;
        private readonly object _lock = new object();
        private CalendarSnapshot _cache;
        // The last successful event list AND the key that produced it. Until round 2 of the 2026-09-29
        // campaign the list stood alone, which was harmless while every options Apply rebuilt every source:
        // a URL or file edit always started from a blank instance. F200 keeps the instance across Apply, and
        // that made the key-change branch in Fetch reachable on a populated cache for the first time, so an
        // edit to a target that fails served the PREVIOUS target's cache as healthy for one tick and then its
        // last-good events behind the NEW target's error for as long as the new target failed (R-043). The
        // retained data now belongs to its key: see LastGoodFor and the cache clear in Fetch.
        private IReadOnlyList<CalendarEvent> _lastGood;
        private string _lastGoodKey;
        private DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;
        private string _lastKey;
        // The latch, in three parts. _generation counts kicks and _landedGeneration is the newest kick whose
        // result has been stored, so "a refresh is in flight" is the two disagreeing. _started holds the start
        // time of every attempt kicked and not yet returned, of ANY generation: its count is what the cap
        // bounds, its oldest entry is what the stall report names, and its newest is what the deadline
        // measures before another attempt is kicked.
        private int _generation;
        private int _landedGeneration;
        private readonly Dictionary<int, DateTimeOffset> _started = new Dictionary<int, DateTimeOffset>();

        protected CachingCalendarSource(TimeSpan refreshInterval) { _refreshInterval = refreshInterval; }

        public abstract string Name { get; }

        /// <summary>The config that, when it changes, forces an immediate refresh (e.g. the URL). "" if none.
        /// Called on the CALLER's thread only; see <see cref="FetchCore"/>.</summary>
        protected abstract string RefreshKey();

        /// <summary>Do the real (possibly blocking) fetch, off the UI thread. Return an error snapshot rather
        /// than throwing (the base also catches). <see cref="LastGoodFor"/> is the last successful event list
        /// for a key, which the base restores behind a transient failure of that same key.
        ///
        /// <paramref name="key"/> is the <see cref="RefreshKey"/> the caller's thread computed for this kick.
        /// Use it; do not call RefreshKey() or the settings getter behind it from here. This runs on a pool
        /// thread (or a dedicated STA thread), and the getters read the module's settings store, which is a
        /// bare Dictionary the UI thread writes on every Apply -- a concurrent read across an insert can throw
        /// or miss the key (F192). The key travelled through StartRefresh into DoRefresh before this change
        /// and was then dropped on the floor, so every subclass recomputed it off-thread.</summary>
        protected abstract CalendarSnapshot FetchCore(string key, DateTimeOffset now);

        /// <summary>True when <see cref="FetchCore"/> needs a single-threaded-apartment thread (COM). Default:
        /// a plain thread-pool task.</summary>
        protected virtual bool RequiresSta { get { return false; } }

        protected virtual string LoadingMessage { get { return "Loading the calendar…"; } }

        /// <summary>How long the newest refresh attempt may run before Fetch treats it as abandoned: reports
        /// the stall, kicks one more attempt (under the cap) and later discards the abandoned result if a
        /// newer one has landed. Three refresh intervals and never under a minute, so a slow-but-progressing
        /// fetch is not abandoned by a deadline tuned for the 20 s file slot. Virtual so a self-test double can
        /// make it milliseconds; the shipped sources take the default.</summary>
        protected virtual TimeSpan RefreshDeadline
        {
            get
            {
                TimeSpan threeIntervals = TimeSpan.FromTicks(_refreshInterval.Ticks * 3);
                return threeIntervals > MinimumRefreshDeadline ? threeIntervals : MinimumRefreshDeadline;
            }
        }

        /// <summary>The interval a Fetch waits after the last landed result before kicking another refresh.
        /// Exposed so the self-test can hold it against the module's tick (F191).</summary>
        internal TimeSpan RefreshInterval { get { return _refreshInterval; } }

        /// <summary>Attempts kicked and not yet returned, of any generation. For the self-test's cap check.</summary>
        internal int OutstandingRefreshes { get { lock (_lock) return _started.Count; } }

        /// <summary>The last successful event list, but only for the key that produced it: a different URL or
        /// file is a different calendar, and its failure must not be papered over with another calendar's
        /// events (R-043). Null when the last good list came from another key, or there is none.</summary>
        protected IReadOnlyList<CalendarEvent> LastGoodFor(string key)
        {
            lock (_lock)
                return string.Equals(_lastGoodKey, key ?? "", StringComparison.Ordinal) ? _lastGood : null;
        }

        /// <summary>True while the newest kick has not landed. "Check now" waits on it, bounded, so the status
        /// it returns is about the re-read it asked for rather than the cache it started from.</summary>
        public bool IsRefreshing { get { lock (_lock) return _generation != _landedGeneration; } }

        /// <summary>Forget when the last result landed, so the next <see cref="Fetch"/> kicks a refresh rather
        /// than waiting out the interval. For a user's click; the timer never needs it. A refresh already in
        /// flight is left to land: this marks the cache stale, it does not start a second attempt.</summary>
        public void Invalidate()
        {
            lock (_lock) _lastFetchUtc = DateTimeOffset.MinValue;
        }

        public CalendarSnapshot Fetch()
        {
            // The key is computed HERE, on the caller's thread, and threaded through to FetchCore (F192).
            string key = RefreshKey() ?? "";
            bool kick;
            int generation = 0;
            CalendarSnapshot snapshot;
            string stall = null;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                bool changed = !string.Equals(key, _lastKey, StringComparison.Ordinal);
                bool stale = changed || _cache == null || (nowUtc - _lastFetchUtc) > _refreshInterval;
                bool inFlight = _generation != _landedGeneration;
                TimeSpan deadline = RefreshDeadline;
                // The stall is reported while the slot's data is not fresh and SOME attempt has outlived the
                // deadline, and it names the oldest one: that keeps the message steady across the retry
                // instead of flickering off for one deadline while the retry runs, which the on-change feed
                // log would have written up as a recovery that had not happened. Once the newest attempt
                // lands the slot is fresh and the report clears, whatever older attempt is still parked.
                bool abandoned = false;
                if (inFlight)
                {
                    DateTimeOffset oldest = DateTimeOffset.MaxValue, newest = DateTimeOffset.MinValue;
                    foreach (DateTimeOffset started in _started.Values)
                    {
                        if (started < oldest) oldest = started;
                        if (started > newest) newest = started;
                    }
                    if (_started.Count > 0 && nowUtc - oldest > deadline)
                        stall = "a refresh started at "
                            + oldest.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
                            + " has not completed";
                    abandoned = _started.Count > 0 && nowUtc - newest > deadline;
                }
                kick = stale && (!inFlight || abandoned) && _started.Count < MaximumOutstandingRefreshes;
                if (kick)
                {
                    generation = ++_generation;
                    _started[generation] = nowUtc;
                }
                // A changed key means the cache describes a calendar the user no longer points at. Serving it
                // for the tick between the edit and the new target's first landing announced the old
                // target's events as healthy, and once the new target failed its error carried them (R-043).
                // The interim tick shows the loading state instead; nothing of the old key survives here.
                if (changed) _cache = null;
                snapshot = _cache;
            }
            if (kick) StartRefresh(key, generation);
            if (snapshot == null)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = stall ?? LoadingMessage };
            if (stall == null) return snapshot;
            // A copy, never the cached instance: the stall is this tick's news about the slot, not a property
            // of the fetch that produced the cache. An older error, if any, stays in front of it.
            return new CalendarSnapshot
            {
                Events = snapshot.Events,
                Updated = snapshot.Updated,
                Error = string.IsNullOrEmpty(snapshot.Error) ? stall : snapshot.Error + "; " + stall,
            };
        }

        private void StartRefresh(string key, int generation)
        {
            if (RequiresSta)
            {
                var thread = new Thread(() => DoRefresh(key, generation)) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            else
            {
                Task.Run(() => DoRefresh(key, generation));
            }
        }

        private void DoRefresh(string key, int generation)
        {
            CalendarSnapshot result;
            try { result = FetchCore(key, DateTimeOffset.Now) ?? new CalendarSnapshot { Events = Array.Empty<CalendarEvent>() }; }
            catch (Exception ex)
            {
                result = new CalendarSnapshot { Events = LastGoodFor(key) ?? Array.Empty<CalendarEvent>(), Error = "Calendar fetch failed: " + Short(ex.Message) };
            }
            // "A parse failure is non-fatal, the companion keeps the last good feed" -- CALENDAR-FEED.md.
            // That was only true of a fetch that THREW. A subclass that returns an error snapshot instead,
            // which is what every one of them is told to do in FetchCore's own doc comment, blanked the
            // feed: six of IcsUrlSource's error returns carry Array.Empty, and OutlookComSource got it right
            // at exactly ONE of its two (:38) by hand-writing LastGood into the snapshot. Hand-writing it at
            // each site was never going to hold. Done here instead, once.
            //
            // Only when the error snapshot brought nothing of its own: a subclass that returns a partial
            // list with a warning keeps what it found. And only the SAME key's last good list: a failing new
            // URL or file gets an empty list behind its error, not the previous calendar's events (R-043).
            if (result.Error != null && (result.Events == null || result.Events.Count == 0))
            {
                IReadOnlyList<CalendarEvent> good = LastGoodFor(key);
                if (good != null && good.Count > 0) result.Events = good;
            }
            lock (_lock)
            {
                _started.Remove(generation);
                // An abandoned attempt landing late, after a newer one already did: its data is older than
                // what is being served, so it is dropped rather than clobbering the newer result.
                if (generation < _landedGeneration) return;
                _landedGeneration = generation;
                _cache = result;
                if (result.Error == null && result.Events != null)
                {
                    _lastGood = result.Events;
                    _lastGoodKey = key ?? "";
                }
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastKey = key;
            }
        }

        protected static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 160 ? message.Substring(0, 160) + "…" : message;
        }
    }
}
