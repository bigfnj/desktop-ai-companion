using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Fans one tick out across several configured calendar feeds and merges the result into a single snapshot.
    /// Each event is copied (never the source's own instance, which a <see cref="CachingCalendarSource"/> may
    /// hand back unchanged across ticks) and tagged with the slot it came from: <see cref="CalendarEvent.SourceId"/>
    /// picks the per-feed speech style and label, and the event id is prefixed with the slot id so two calendars
    /// with a coincidentally equal id can never share one "fired" entry. A slot that errors is reported in the
    /// combined <see cref="CalendarSnapshot.Error"/> but does not suppress the healthy slots' events.
    /// </summary>
    public sealed class AggregateCalendarSource : ICalendarSource
    {
        /// <summary>One configured feed: a stable slot id (e.g. "cal1"), a human label, and the source behind it.</summary>
        public sealed class Slot
        {
            public string Id;
            public string Label;
            public ICalendarSource Source;
        }

        private readonly List<Slot> _slots;
        private readonly string _name;

        public AggregateCalendarSource(IEnumerable<Slot> slots)
        {
            _slots = (slots ?? Enumerable.Empty<Slot>()).Where(s => s != null && s.Source != null).ToList();
            _name = _slots.Count == 0 ? "no calendars"
                : (_slots.Count == 1 ? "1 calendar" : _slots.Count + " calendars");
        }

        public string Name { get { return _name; } }

        public CalendarSnapshot Fetch()
        {
            var all = new List<CalendarEvent>();
            var errors = new List<string>();
            DateTimeOffset? updated = null;

            foreach (Slot slot in _slots)
            {
                string label = string.IsNullOrWhiteSpace(slot.Label) ? slot.Id : slot.Label.Trim();
                CalendarSnapshot snap;
                try { snap = slot.Source.Fetch(); }
                catch (Exception ex) { errors.Add(label + ": " + ex.Message); continue; }
                if (snap == null) continue;

                if (!string.IsNullOrEmpty(snap.Error)) errors.Add(label + ": " + snap.Error);

                if (snap.Events != null)
                {
                    foreach (CalendarEvent e in snap.Events)
                    {
                        if (e == null) continue;
                        all.Add(new CalendarEvent
                        {
                            Id = slot.Id + "|" + (e.Id ?? ""),
                            Title = e.Title,
                            Start = e.Start,
                            End = e.End,
                            Location = e.Location,
                            Description = e.Description,
                            AllDay = e.AllDay,
                            ResponseStatus = e.ResponseStatus,
                            Attendees = e.Attendees,
                            SourceId = slot.Id,
                        });
                    }
                }

                if (snap.Updated != null && (updated == null || snap.Updated.Value > updated.Value))
                    updated = snap.Updated;
            }

            return new CalendarSnapshot
            {
                Events = all,
                Updated = updated,
                Error = errors.Count > 0 ? string.Join("; ", errors) : null,
            };
        }

        // A pure, harness-callable check of the two invariants that matter: events are copied + tagged + id-prefixed,
        // and one failing slot does not blank the others. Mirrors QuietHours/ReminderScheduler SelfCheck.
        // Named SelfCheck, not SelfTest, on purpose: the app's --module-selftest convention reflects over
        // EVERY type in the assembly for `bool SelfTest(out string)` and takes the FIRST match, so a helper
        // sharing that exact name can win over the module's own aggregate entry point. ReminderModule.SelfTest
        // is the single entry point and calls this.
        internal static bool SelfCheck(out string detail)
        {
            var good = new StubSource(new[]
            {
                new CalendarEvent { Id = "x", Title = "A", Start = DateTimeOffset.Now.AddMinutes(10) },
            }, null);
            var bad = new StubSource(Array.Empty<CalendarEvent>(), "boom");

            var agg = new AggregateCalendarSource(new[]
            {
                new Slot { Id = "cal1", Label = "Home", Source = good },
                new Slot { Id = "cal2", Label = "Work", Source = bad },
            });
            CalendarSnapshot snap = agg.Fetch();

            if (snap.Events.Count != 1) { detail = "expected 1 healthy event, got " + snap.Events.Count; return false; }
            CalendarEvent ev = snap.Events[0];
            if (ev.SourceId != "cal1") { detail = "event not tagged with its slot"; return false; }
            if (ev.Id != "cal1|x") { detail = "event id not prefixed with the slot id: " + ev.Id; return false; }
            if (string.IsNullOrEmpty(snap.Error) || !snap.Error.Contains("Work")) { detail = "the failing slot was not reported"; return false; }

            detail = "aggregate: healthy events tagged + id-prefixed, failing slot reported without suppressing the rest";
            return true;
        }

        private sealed class StubSource : ICalendarSource
        {
            private readonly IReadOnlyList<CalendarEvent> _events;
            private readonly string _error;
            public StubSource(IReadOnlyList<CalendarEvent> events, string error) { _events = events; _error = error; }
            public string Name { get { return "stub"; } }
            public CalendarSnapshot Fetch() { return new CalendarSnapshot { Events = _events, Error = _error }; }
        }
    }
}
