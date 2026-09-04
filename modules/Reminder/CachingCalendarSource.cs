using System;
using System.Collections.Generic;
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
    /// </summary>
    public abstract class CachingCalendarSource : ICalendarSource
    {
        private readonly TimeSpan _refreshInterval;
        private readonly object _lock = new object();
        private CalendarSnapshot _cache;
        private IReadOnlyList<CalendarEvent> _lastGood;
        private DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;
        private string _lastKey;
        private bool _refreshing;

        protected CachingCalendarSource(TimeSpan refreshInterval) { _refreshInterval = refreshInterval; }

        public abstract string Name { get; }

        /// <summary>The config that, when it changes, forces an immediate refresh (e.g. the URL). "" if none.</summary>
        protected abstract string RefreshKey();

        /// <summary>Do the real (possibly blocking) fetch, off the UI thread. Return an error snapshot rather
        /// than throwing (the base also catches). <see cref="LastGood"/> is the last successful event list, to
        /// preserve across a transient failure.</summary>
        protected abstract CalendarSnapshot FetchCore(DateTimeOffset now);

        /// <summary>True when <see cref="FetchCore"/> needs a single-threaded-apartment thread (COM). Default:
        /// a plain thread-pool task.</summary>
        protected virtual bool RequiresSta { get { return false; } }

        protected virtual string LoadingMessage { get { return "Loading the calendar…"; } }

        protected IReadOnlyList<CalendarEvent> LastGood { get { lock (_lock) return _lastGood; } }

        public CalendarSnapshot Fetch()
        {
            string key = RefreshKey() ?? "";
            bool kick;
            CalendarSnapshot snapshot;
            lock (_lock)
            {
                bool changed = !string.Equals(key, _lastKey, StringComparison.Ordinal);
                bool stale = changed || _cache == null || (DateTimeOffset.UtcNow - _lastFetchUtc) > _refreshInterval;
                kick = stale && !_refreshing;
                if (kick) _refreshing = true;
                snapshot = _cache;
            }
            if (kick) StartRefresh(key);
            return snapshot ?? new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = LoadingMessage };
        }

        private void StartRefresh(string key)
        {
            if (RequiresSta)
            {
                var thread = new Thread(() => DoRefresh(key)) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            else
            {
                Task.Run(() => DoRefresh(key));
            }
        }

        private void DoRefresh(string key)
        {
            CalendarSnapshot result;
            try { result = FetchCore(DateTimeOffset.Now) ?? new CalendarSnapshot { Events = Array.Empty<CalendarEvent>() }; }
            catch (Exception ex)
            {
                result = new CalendarSnapshot { Events = LastGood ?? Array.Empty<CalendarEvent>(), Error = "Calendar fetch failed: " + Short(ex.Message) };
            }
            lock (_lock)
            {
                _cache = result;
                if (result.Error == null && result.Events != null) _lastGood = result.Events;
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastKey = key;
                _refreshing = false;
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
