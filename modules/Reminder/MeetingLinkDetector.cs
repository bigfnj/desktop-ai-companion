using System;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Finds an online-meeting join link (Teams / Zoom / Google Meet / Webex) in an event's location or
    /// description, so a reminder can offer a one-click "Join". Deliberately high precision: it matches KNOWN
    /// meeting hosts and their JOIN shapes only, never a bare https link, so a document, a map URL or a vendor's
    /// help article in the body is never mistaken for a meeting. The location is checked before the (longer,
    /// noisier) description. Pure and string-only, so it is unit-testable without a calendar.
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
            // Hosts are anchored to a label boundary (`(?:[a-z0-9\-]+\.)*zoom\.us`), so a look-alike such as
            // notzoom.us is not a meeting host; the shipped `[a-z0-9.\-]*` prefix accepted it (RA-171).
            new Provider("Zoom", @"https://(?:[a-z0-9\-]+\.)*zoom\.us/(?:j|my|s|w)/[^\s""'<>]+"),
            new Provider("Google Meet", @"https://meet\.google\.com/[a-z0-9\-]+"),
            // Webex JOIN shapes only, never any path on any *webex.com host: the classic site link and the event
            // links (/<site>/j.php?MTID=, /<site>/e.php?, the older /<site>/onstage/g.php?), a Personal Room
            // (/meet/<user>, /join/<user>), and the Webex App's own two (/webappng/sites/<site>/meeting/...,
            // /wbxmjs/joinservice/...). The shipped pattern took `webex\.com/[^\s]+`, so an invite whose body
            // opened with a help.webex.com article before the join link announced "Join link is in the tray" and
            // the tray's Join opened the help page (RA-171). A shape not listed here is a miss, not a wrong link:
            // the bubble still announces and only the Join hint is withheld, which is the precision this class
            // exists for.
            new Provider("Webex", @"https://(?:[a-z0-9\-]+\.)*webex\.com/(?:(?:[a-z0-9._\-]+/)*(?:j|e|g)\.php\?|(?:meet|join)/|webappng/sites/|wbxmjs/joinservice/)[^\s""'<>]+"),
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
            // Named per case, one FAIL: line each, so a failure says WHICH link shape broke (and the mutation
            // harness can name it) rather than only the last kind/url seen.
            var failed = new System.Collections.Generic.List<string>();
            if (!(TryFind(new CalendarEvent { Location = "https://teams.microsoft.com/l/meetup-join/abc123" }, out u, out k) && k == "Teams"))
                failed.Add("Teams long link");
            // The short work-tenant form, with its passcode intact: the tray's Join opens exactly this URL.
            if (!(TryFind(new CalendarEvent { Location = "https://teams.microsoft.com/meet/9301471512975?p=Abc123" }, out u, out k)
                  && k == "Teams" && u == "https://teams.microsoft.com/meet/9301471512975?p=Abc123"))
                failed.Add("Teams short link keeps its passcode");
            // The free tier's form had been accepted all along and asserted never, so its provider could be
            // deleted with the suite green (RA-171).
            if (!(TryFind(new CalendarEvent { Location = "https://teams.live.com/meet/9876543210123?p=Xy12Ab" }, out u, out k)
                  && k == "Teams" && u == "https://teams.live.com/meet/9876543210123?p=Xy12Ab"))
                failed.Add("teams.live.com short link keeps its passcode");
            if (!(TryFind(new CalendarEvent { Description = "Join here: https://zoom.us/j/9876543210?pwd=x thanks" }, out u, out k)
                  && k == "Zoom" && u.Contains("9876543210") && !u.Contains("thanks")))
                failed.Add("Zoom in a description, trailing text trimmed");
            if (!(TryFind(new CalendarEvent { Location = "https://meet.google.com/abc-defg-hij" }, out u, out k) && k == "Google Meet"))
                failed.Add("Google Meet");
            // Webex: the classic site link, a Personal Room, and the Webex App's own shape (RA-171).
            if (!(TryFind(new CalendarEvent { Location = "https://acme.webex.com/acme/j.php?MTID=m1a2b3c4d5e6f" }, out u, out k)
                  && k == "Webex" && u == "https://acme.webex.com/acme/j.php?MTID=m1a2b3c4d5e6f"))
                failed.Add("Webex classic j.php join link");
            if (!(TryFind(new CalendarEvent { Description = "Personal Room: https://acme.webex.com/meet/jdoe" }, out u, out k)
                  && k == "Webex" && u == "https://acme.webex.com/meet/jdoe"))
                failed.Add("Webex Personal Room link");
            if (!(TryFind(new CalendarEvent { Description = "https://acme.webex.com/webappng/sites/acme/meeting/info/2718281828?MTID=m9f8e7d" }, out u, out k)
                  && k == "Webex"))
                failed.Add("Webex App meeting link");
            // The RA-171 scenario: a help article ahead of the join link. The article is not a join link, so the
            // link after it is the one the tray opens.
            if (!(TryFind(new CalendarEvent { Description = "Need help? https://help.webex.com/en-us/article/nrbgeodb/ Then join: https://acme.webex.com/acme/j.php?MTID=m1a2b3c" }, out u, out k)
                  && k == "Webex" && u == "https://acme.webex.com/acme/j.php?MTID=m1a2b3c"))
                failed.Add("help.webex.com is not a join link, the j.php link after it is");
            if (TryFind(new CalendarEvent { Location = "https://help.webex.com/en-us/article/nrbgeodb/" }, out u, out k))
                failed.Add("help.webex.com alone is not a join link");
            if (TryFind(new CalendarEvent { Location = "https://notwebex.com/meet/jdoe and https://notzoom.us/j/123456" }, out u, out k))
                failed.Add("a host that merely ends in webex.com or zoom.us is not a meeting host");
            if (TryFind(new CalendarEvent { Location = "Room 4B", Description = "agenda doc at https://example.com/agenda" }, out u, out k))
                failed.Add("a non-meeting https must be ignored");
            var sb = new System.Text.StringBuilder();
            foreach (string f in failed) sb.AppendLine("FAIL: " + f);
            sb.Append(failed.Count == 0
                ? "meeting link: Teams long, short and live (passcodes kept), Zoom, Meet, Webex classic, Personal Room and App matched; trailing text trimmed; help.webex.com, look-alike hosts and a non-meeting https ignored"
                : "meeting-link detection wrong: " + failed.Count + " case(s) above");
            detail = sb.ToString();
            return failed.Count == 0;
        }
    }
}
