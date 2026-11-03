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
    /// combined <see cref="CalendarSnapshot.Error"/> but does not suppress the healthy slots' events, and each
    /// slot's health travels on the snapshot (<see cref="CalendarSnapshot.SlotHealthy"/>) so the fired-id prune
    /// can judge per slot rather than on the combined Error, which is set whenever any one slot fails (F204).
    ///
    /// A slot may YIELD to the others (1.3.0, the Zoom slot): its copy of a Zoom meeting that another slot also
    /// carries, recognised by the meeting number in the join link and the start minute, is left out, so a meeting
    /// the user hosts in Zoom and also has on their Outlook calendar is announced once, in the calendar's name and
    /// style, with the invite's attendees. The calendar's copy wins because it carries more. What was left out is
    /// recorded on the snapshot (<see cref="CalendarSnapshot.Twins"/>) rather than forgotten: the prune still counts
    /// it as part of its slot's calendar, and the module carries a fired mark across the pair, so the copies
    /// appearing, merging and parting again in any order never re-announce one meeting.
    /// </summary>
    public sealed class AggregateCalendarSource : ICalendarSource
    {
        /// <summary>One configured feed: a stable slot id (e.g. "cal1"), a human label, and the source behind it.</summary>
        public sealed class Slot
        {
            public string Id;
            public string Label;
            public ICalendarSource Source;
            /// <summary>Leave out this slot's copy of a Zoom meeting another slot also carries (1.3.0).</summary>
            public bool YieldsToOthers;
        }

        private readonly List<Slot> _slots;
        private readonly string _name;

        /// <summary>Which events may stand in for a yielding slot's copy (1.3.0): the module's announce filter, so a
        /// calendar copy it would never announce (declined, all-day) does not take a hosted Zoom copy away with it. A
        /// yielding copy that knows no answer (an invited meeting) yields to any calendar copy instead
        /// (<see cref="LeaveOutTwins"/>). Null means every event may. Read during <see cref="Fetch"/>, on the caller's
        /// thread.</summary>
        public Func<CalendarEvent, bool> TwinOwnerFilter { get; set; }

        public AggregateCalendarSource(IEnumerable<Slot> slots)
        {
            _slots = (slots ?? Enumerable.Empty<Slot>()).Where(s => s != null && s.Source != null).ToList();
            _name = _slots.Count == 0 ? "no calendars"
                : (_slots.Count == 1 ? "1 calendar" : _slots.Count + " calendars");
        }

        public string Name { get { return _name; } }

        public void Invalidate()
        {
            foreach (Slot slot in _slots)
            {
                try { slot.Source.Invalidate(); } catch { }
            }
        }

        public bool IsRefreshing
        {
            get
            {
                foreach (Slot slot in _slots)
                {
                    try { if (slot.Source.IsRefreshing) return true; } catch { }
                }
                return false;
            }
        }

        public CalendarSnapshot Fetch()
        {
            var all = new List<CalendarEvent>();
            var errors = new List<string>();
            var health = new Dictionary<string, bool>(StringComparer.Ordinal);
            DateTimeOffset? updated = null;

            foreach (Slot slot in _slots)
            {
                string label = string.IsNullOrWhiteSpace(slot.Label) ? slot.Id : slot.Label.Trim();
                CalendarSnapshot snap;
                try { snap = slot.Source.Fetch(); }
                catch (Exception ex) { errors.Add(label + ": " + ex.Message); health[slot.Id] = false; continue; }
                if (snap == null) { health[slot.Id] = false; continue; }

                bool healthy = string.IsNullOrEmpty(snap.Error);
                health[slot.Id] = healthy;
                if (!healthy) errors.Add(label + ": " + snap.Error);

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

            Dictionary<string, string> twins = null;
            var yielding = new HashSet<string>(_slots.Where(s => s.YieldsToOthers).Select(s => s.Id), StringComparer.Ordinal);
            if (yielding.Count > 0) all = LeaveOutTwins(all, yielding, TwinOwnerFilter, out twins);

            return new CalendarSnapshot
            {
                Events = all,
                Updated = updated,
                Error = errors.Count > 0 ? string.Join("; ", errors) : null,
                SlotHealthy = health,
                Twins = twins,
            };
        }

        /// <summary>
        /// Drop each yielding slot's event whose Zoom meeting number and start minute match an event from a slot
        /// that does not yield, recording dropped id -> kept id. The first non-yielding event with a key wins, so
        /// two calendars that both carry the invite still SHOW both (as before 1.3.0); only the Zoom copy goes. Whether
        /// both are ANNOUNCED is the module's rule (ReminderModule.MirrorTwinFired: once, while the Zoom card is on). The
        /// minute, not the second: an Outlook start and a Zoom start for the same meeting agree to the minute, and a
        /// meeting moved in one place and not the other is two different times, shown twice, which is the truth.
        ///
        /// Which calendar copy may stand in depends on whether the yielding copy knows the user's answer. One that does
        /// (a meeting the user hosts, "accepted") yields only to a calendar copy the owner filter keeps, so a declined or
        /// all-day calendar copy does not take it away with it. One that does not (a meeting the user is invited to,
        /// ResponseStatus empty) yields to ANY calendar copy, preferring one the filter keeps: the calendar copy holds the
        /// user's answer, so an invite declined in Outlook is not brought back by the Zoom card's invited read.
        /// </summary>
        internal static List<CalendarEvent> LeaveOutTwins(List<CalendarEvent> all, HashSet<string> yielding,
            Func<CalendarEvent, bool> ownerFilter, out Dictionary<string, string> twins)
        {
            twins = new Dictionary<string, string>(StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);      // calendar copies the filter keeps
            var anyOwners = new Dictionary<string, string>(StringComparer.Ordinal);   // every calendar copy, kept or not
            foreach (CalendarEvent e in all)
            {
                if (yielding.Contains(e.SourceId ?? "")) continue;
                string key = TwinKey(e);
                if (key == null) continue;
                if (!anyOwners.ContainsKey(key)) anyOwners[key] = e.Id;
                bool eligible;
                try { eligible = ownerFilter == null || ownerFilter(e); } catch { eligible = true; }
                if (eligible && !owners.ContainsKey(key)) owners[key] = e.Id;
            }
            if (anyOwners.Count == 0) return all;
            var kept = new List<CalendarEvent>(all.Count);
            foreach (CalendarEvent e in all)
            {
                string key = yielding.Contains(e.SourceId ?? "") ? TwinKey(e) : null;
                string owner;
                if (key != null && (owners.TryGetValue(key, out owner)
                    || (string.IsNullOrEmpty(e.ResponseStatus) && anyOwners.TryGetValue(key, out owner))))
                { twins[e.Id] = owner; continue; }
                kept.Add(e);
            }
            return kept;
        }

        private static string TwinKey(CalendarEvent e)
        {
            string meeting = MeetingLinkDetector.ZoomMeetingId(e);
            return meeting == null ? null
                : meeting + "@" + e.Start.UtcDateTime.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        }

        // A pure, harness-callable check of the two invariants that matter: events are copied + tagged + id-prefixed,
        // and one failing slot does not blank the others. Mirrors QuietHours/ReminderScheduler SelfCheck.
        // Named SelfCheck, not SelfTest, on purpose: the app's --module-selftest finder
        // (ModuleConventionSelfTest.TryFindSelfTest) looks for `bool SelfTest(out string)` on the module's own
        // type first, then on the other IModule types, then on every type, and reports two candidates in one
        // tier as an ambiguity rather than taking the first (F339), so a helper sharing the name would be a
        // reported ambiguity, not a silent winner; the module still keeps one entry point: ReminderModule.SelfTest
        // calls this. (Until 2026-10-01 this comment described the pre-F339 first-match finder, RA-089.)
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
            bool cal1Healthy, cal2Healthy;
            if (snap.SlotHealthy == null
                || !snap.SlotHealthy.TryGetValue("cal1", out cal1Healthy) || !cal1Healthy
                || !snap.SlotHealthy.TryGetValue("cal2", out cal2Healthy) || cal2Healthy)
            { detail = "per-slot health did not travel with the snapshot (expected cal1 healthy, cal2 not)"; return false; }

            // The yielding Zoom slot (1.3.0): its copy of a meeting the Outlook slot also carries is left out and
            // recorded as a twin; a meeting only Zoom has, and the same number at another time, both stay; two
            // non-yielding calendars that share an invite keep both copies, as they always did.
            var at = new DateTimeOffset(2026, 3, 10, 15, 0, 0, TimeSpan.Zero);
            var outlook = new StubSource(new[]
            {
                new CalendarEvent { Id = "o1", Title = "Design Review", Start = at.AddSeconds(0), Description = "Join: https://acme.zoom.us/j/91234567890?pwd=x" },
            }, null);
            var homeToo = new StubSource(new[]
            {
                new CalendarEvent { Id = "h1", Title = "Design Review", Start = at, Location = "https://zoom.us/j/91234567890" },
            }, null);
            // Meetings the user hosts, as the Zoom source gives them: "accepted".
            var zoom = new StubSource(new[]
            {
                new CalendarEvent { Id = "91234567890@a", Title = "Design Review", Start = at, Description = "https://acme.zoom.us/j/91234567890?pwd=x", ResponseStatus = "accepted" },
                new CalendarEvent { Id = "91234567890@b", Title = "Design Review", Start = at.AddDays(1), Description = "https://acme.zoom.us/j/91234567890?pwd=x", ResponseStatus = "accepted" },
                new CalendarEvent { Id = "70000000001@a", Title = "Only in Zoom", Start = at, Description = "https://acme.zoom.us/j/70000000001", ResponseStatus = "accepted" },
            }, null);
            CalendarSnapshot merged = new AggregateCalendarSource(new[]
            {
                new Slot { Id = "cal1", Label = "Work", Source = outlook },
                new Slot { Id = "cal2", Label = "Home", Source = homeToo },
                new Slot { Id = "cal5", Label = "Zoom", Source = zoom, YieldsToOthers = true },
            }).Fetch();
            var mergedIds = new HashSet<string>(merged.Events.Select(e => e.Id), StringComparer.Ordinal);
            if (mergedIds.Contains("cal5|91234567890@a")) { detail = "the Zoom copy of a meeting the calendar also carries was not left out"; return false; }
            if (!mergedIds.Contains("cal1|o1") || !mergedIds.Contains("cal2|h1")) { detail = "a non-yielding calendar's copy was dropped"; return false; }
            if (!mergedIds.Contains("cal5|91234567890@b") || !mergedIds.Contains("cal5|70000000001@a"))
            { detail = "a Zoom meeting no calendar carries (or the same number at another time) was dropped"; return false; }
            string twinOwner;
            if (merged.Twins == null || !merged.Twins.TryGetValue("cal5|91234567890@a", out twinOwner) || twinOwner != "cal1|o1")
            { detail = "the left-out copy was not recorded as the twin of the calendar's event"; return false; }
            CalendarSnapshot plain = new AggregateCalendarSource(new[]
            {
                new Slot { Id = "cal1", Label = "Work", Source = outlook },
                new Slot { Id = "cal5", Label = "Zoom", Source = zoom },
            }).Fetch();
            if (plain.Events.Count != 4 || (plain.Twins != null && plain.Twins.Count > 0))
            { detail = "a slot that does not yield had its events merged"; return false; }
            // A calendar copy the announce filter rejects (declined, say) does not take the Zoom copy with it.
            CalendarSnapshot declined = new AggregateCalendarSource(new[]
            {
                new Slot { Id = "cal1", Label = "Work", Source = outlook },
                new Slot { Id = "cal5", Label = "Zoom", Source = zoom, YieldsToOthers = true },
            }) { TwinOwnerFilter = e => e.SourceId != "cal1" }.Fetch();
            if (!declined.Events.Any(e => e.Id == "cal5|91234567890@a") || (declined.Twins != null && declined.Twins.Count > 0))
            { detail = "a calendar copy the announce filter rejects still left the Zoom copy out"; return false; }
            // ...but a Zoom copy that does not know the user's answer (an invited meeting, ResponseStatus "") yields to
            // that calendar copy anyway: the calendar holds the answer, so an invite declined there stays silent.
            var invitedZoom = new StubSource(new[]
            {
                new CalendarEvent { Id = "91234567890@a", Title = "Design Review", Start = at, Description = "https://acme.zoom.us/j/91234567890?pwd=x", ResponseStatus = "" },
            }, null);
            CalendarSnapshot declinedInvite = new AggregateCalendarSource(new[]
            {
                new Slot { Id = "cal1", Label = "Work", Source = outlook },
                new Slot { Id = "cal5", Label = "Zoom", Source = invitedZoom, YieldsToOthers = true },
            }) { TwinOwnerFilter = e => e.SourceId != "cal1" }.Fetch();
            string declinedOwner;
            if (declinedInvite.Events.Any(e => e.Id == "cal5|91234567890@a")
                || declinedInvite.Twins == null || !declinedInvite.Twins.TryGetValue("cal5|91234567890@a", out declinedOwner) || declinedOwner != "cal1|o1")
            { detail = "an invited Zoom copy was kept beside a calendar copy the announce filter rejects (a declined invite came back through Zoom)"; return false; }
            // With a second calendar copy the filter keeps, the invited copy's twin is that one, the copy that speaks.
            CalendarSnapshot preferKept = new AggregateCalendarSource(new[]
            {
                new Slot { Id = "cal1", Label = "Work", Source = outlook },
                new Slot { Id = "cal2", Label = "Home", Source = homeToo },
                new Slot { Id = "cal5", Label = "Zoom", Source = invitedZoom, YieldsToOthers = true },
            }) { TwinOwnerFilter = e => e.SourceId != "cal1" }.Fetch();
            string keptOwner;
            if (preferKept.Twins == null || !preferKept.Twins.TryGetValue("cal5|91234567890@a", out keptOwner) || keptOwner != "cal2|h1")
            { detail = "an invited Zoom copy's twin was a filtered calendar copy when one the filter keeps was there"; return false; }

            detail = "aggregate: healthy events tagged + id-prefixed, failing slot reported without suppressing the rest, per-slot health carried; a yielding Zoom slot's copy of a calendar's meeting left out and recorded as a twin, the rest kept; an invited copy yields even to a filtered calendar copy, preferring one the filter keeps";
            return true;
        }

        /// <summary>A source that answers the same snapshot every time. Internal: ReminderModule.SelfTest uses it
        /// to stand a slot up without a file, a URL or an Outlook.</summary>
        internal sealed class StubSource : ICalendarSource
        {
            private readonly IReadOnlyList<CalendarEvent> _events;
            private readonly string _error;
            public StubSource(IReadOnlyList<CalendarEvent> events, string error) { _events = events; _error = error; }
            public string Name { get { return "stub"; } }
            public CalendarSnapshot Fetch() { return new CalendarSnapshot { Events = _events, Error = _error }; }
            public void Invalidate() { }
            public bool IsRefreshing { get { return false; } }
        }
    }
}
