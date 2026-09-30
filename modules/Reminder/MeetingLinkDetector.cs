using System;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Finds an online-meeting join link (Teams / Zoom / Google Meet / Webex) in an event's location or
    /// description, so a reminder can offer a one-click "Join". Deliberately high precision: it matches KNOWN
    /// meeting hosts only, never a bare https link, so a document or map URL in the body is never mistaken for a
    /// meeting. The location is checked before the (longer, noisier) description. Pure and string-only, so it is
    /// unit-testable without a calendar.
    /// </summary>
    internal static class MeetingLinkDetector
    {
        private const int MaxScan = 8192;   // a meeting body can be an entire thread; the link is near the top

        // Ordered by specificity; first match wins. Kind is a short label for the tray text.
        private static readonly Provider[] Providers =
        {
            new Provider("Teams", @"https://teams\.microsoft\.com/l/meetup-join/[^\s""'<>]+"),
            // The short form Teams issues for work tenants since early 2026 (message center MC772556, "Shorter
            // meeting URLs"): https://teams.microsoft.com/meet/<meeting id>?p=<passcode>, beside or instead of
            // the long link. The free tier's teams.live.com/meet/ form below had been accepted all along; this
            // was the work-tenant gap, and an invite carrying only the short form fired with no Join hint and
            // nothing in the tray (F193). The ?p= passcode is part of the link and must survive.
            new Provider("Teams", @"https://teams\.microsoft\.com/meet/[^\s""'<>]+"),
            new Provider("Teams", @"https://teams\.live\.com/meet/[^\s""'<>]+"),
            new Provider("Zoom", @"https://[a-z0-9.\-]*zoom\.us/(?:j|my|s|w)/[^\s""'<>]+"),
            new Provider("Google Meet", @"https://meet\.google\.com/[a-z0-9\-]+"),
            new Provider("Webex", @"https://[a-z0-9.\-]*webex\.com/[^\s""'<>]+"),
        };

        public static bool TryFind(CalendarEvent e, out string url, out string kind)
        {
            url = null; kind = null;
            if (e == null) return false;
            return TryFindIn(e.Location, out url, out kind)
                || TryFindIn(Clip(e.Description), out url, out kind);
        }

        private static bool TryFindIn(string text, out string url, out string kind)
        {
            url = null; kind = null;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (Provider p in Providers)
            {
                Match m = p.Pattern.Match(text);
                if (m.Success)
                {
                    url = TrimTrailing(m.Value);
                    kind = p.Kind;
                    return true;
                }
            }
            return false;
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length > MaxScan ? s.Substring(0, MaxScan) : s;
        }

        // Meeting URLs often sit inside HTML or prose, so drop trailing punctuation that can't be part of a link.
        private static string TrimTrailing(string u)
        {
            return (u ?? "").TrimEnd(')', ']', '}', '>', '"', '\'', '.', ',', ';');
        }

        private sealed class Provider
        {
            public readonly string Kind;
            public readonly Regex Pattern;
            public Provider(string kind, string pattern)
            {
                Kind = kind;
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        // SelfCheck, not SelfTest: see the note in AggregateCalendarSource. ReminderModule.SelfTest aggregates.
        internal static bool SelfCheck(out string detail)
        {
            string u, k;
            // Named per case, so a failure says WHICH link shape broke rather than only the last kind/url seen.
            var failed = new System.Collections.Generic.List<string>();
            if (!(TryFind(new CalendarEvent { Location = "https://teams.microsoft.com/l/meetup-join/abc123" }, out u, out k) && k == "Teams"))
                failed.Add("Teams long link");
            // The short work-tenant form, with its passcode intact: the tray's Join opens exactly this URL.
            if (!(TryFind(new CalendarEvent { Location = "https://teams.microsoft.com/meet/9301471512975?p=Abc123" }, out u, out k)
                  && k == "Teams" && u == "https://teams.microsoft.com/meet/9301471512975?p=Abc123"))
                failed.Add("Teams short link keeps its passcode");
            if (!(TryFind(new CalendarEvent { Description = "Join here: https://zoom.us/j/9876543210?pwd=x thanks" }, out u, out k)
                  && k == "Zoom" && u.Contains("9876543210") && !u.Contains("thanks")))
                failed.Add("Zoom in a description, trailing text trimmed");
            if (!(TryFind(new CalendarEvent { Location = "https://meet.google.com/abc-defg-hij" }, out u, out k) && k == "Google Meet"))
                failed.Add("Google Meet");
            if (TryFind(new CalendarEvent { Location = "Room 4B", Description = "agenda doc at https://example.com/agenda" }, out u, out k))
                failed.Add("a non-meeting https must be ignored");
            detail = failed.Count == 0
                ? "meeting link: Teams long and short (passcode kept), Zoom, Meet matched; trailing text trimmed; a non-meeting https ignored"
                : "meeting-link detection wrong: " + string.Join("; ", failed);
            return failed.Count == 0;
        }
    }
}
