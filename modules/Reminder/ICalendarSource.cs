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

        /// <summary>Make the next <see cref="Fetch"/> re-read the feed rather than answer from a cache whose
        /// interval has not elapsed. For a user's click ("Check now", the tray entry): until this existed
        /// nothing in the module could force a re-read, so the button ran the due check against whatever the
        /// last tick had already seen (F201). A source with no cache does nothing.</summary>
        void Invalidate();

        /// <summary>True while a re-read kicked by <see cref="Fetch"/> has not landed, so a caller that asked for
        /// one can wait -- bounded -- before reporting. A source that reads synchronously is never refreshing.</summary>
        bool IsRefreshing { get; }
    }
}
