namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// A place the pet reads calendar events from. Implementations normalize whatever they read into
    /// <see cref="CalendarEvent"/> instances, so the scheduler and the pet announcement are written once and
    /// never learn about Google, Outlook, or a corporate feed. This first slice ships
    /// <see cref="LocalJsonSource"/>; an ICS-URL source (Google / Outlook.com published .ics) and a local
    /// Outlook-COM source slot in behind the same interface later, and can be aggregated.
    /// </summary>
    public interface ICalendarSource
    {
        /// <summary>A short label for the settings status line (e.g. "Local file").</summary>
        string Name { get; }

        /// <summary>Read the current events. Never throws: a failure comes back as
        /// <see cref="CalendarSnapshot.Error"/> with an empty event list.</summary>
        CalendarSnapshot Fetch();
    }
}
