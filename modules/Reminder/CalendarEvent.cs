using System;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// One concrete calendar event instance, normalized so the scheduler is source-agnostic. Every
    /// <see cref="ICalendarSource"/> produces these: the Local-file source deserializes them (co-work already
    /// expanded recurrence + resolved timezones), and a future ICS/Outlook source would expand its own into
    /// the same shape. <see cref="Start"/> carries an offset so a time is never ambiguous.
    /// </summary>
    public sealed class CalendarEvent
    {
        public string Id { get; set; }              // stable per-instance key; how a reminder is fired exactly once
        public string Title { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset? End { get; set; }
        public string Location { get; set; }
        public string Description { get; set; }      // event body/notes; scanned for an online-meeting join link
        public bool AllDay { get; set; }             // all-day marker (skippable via the announce filter)
        public string ResponseStatus { get; set; }   // normalized "accepted"/"tentative"/"declined"/"" so the filter can skip meetings you declined
        public System.Collections.Generic.List<Attendee> Attendees { get; set; }   // invited roster (published on meeting.current for the Remembrance module)
        public string SourceId { get; set; }        // which calendar slot this came from (set by AggregateCalendarSource); picks the per-feed style/label
    }

    /// <summary>One invited attendee, normalized across sources. Status is "accepted"/"declined"/"tentative"/""
    /// where the source knows it (Outlook exposes per-recipient status; an .ics ATTENDEE carries PARTSTAT).</summary>
    public sealed class Attendee
    {
        public string Name { get; set; }
        public string Status { get; set; }
    }

    /// <summary>What a source returns from one fetch: the events, the feed's own "updated" stamp (for a
    /// staleness hint), and an error string (null on success). Sources never throw; they report here.</summary>
    public sealed class CalendarSnapshot
    {
        public System.Collections.Generic.IReadOnlyList<CalendarEvent> Events { get; set; }
        public DateTimeOffset? Updated { get; set; }
        public string Error { get; set; }
    }
}
