using System.Net;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    private static void RunGuideContractChecks(Action<bool, string> check, string guide)
    {
        var missing = new List<string>();
        static string Plain(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")),
            @"\s+", " ").Trim();
        string Block(string tag, string id)
        {
            MatchCollection found = Regex.Matches(guide, "<" + tag + " id=\"" + id + "\">(?<body>.*?)</" + tag + ">",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return found.Count == 1 ? Plain(found[0].Groups["body"].Value) : "";
        }
        void Require(string name, string text, params string[] clauses)
        {
            if (text.Length == 0 || clauses.Any(clause => !text.Contains(clause, StringComparison.Ordinal))) missing.Add(name);
        }

        string scope = Block("p", "watcher-scope");
        Require("native Claude address scope", scope, "claude:<canonical lowercase GUID>", "prefix exactly once",
            "Codex sessions and registered generic mcp: mailboxes", "work milestones or incoming waits");
        Require("mail is a complete pending scan, not command completion", Block("p", "watcher-completion"),
            "complete scan", "pending, unexpired", "not in --seen-ids", "after the supplied cursor",
            "no cursor is supplied", "completion alone does not establish mail");
        string all = Plain(guide);
        Require("bounded canonical seen IDs", all, "at most 64 handled message IDs", "canonical lowercase GUIDs separated by commas with no spaces");
        Require("empty optional values", all, "worker refuses an empty --cursor on any shell",
            "conditional additions omit the --cursor pair", "--seen-ids pair when you handled no IDs");
        Require("chosen arm root, not an install path", all, "arm root is a separate directory you choose, not an installation path");
        Require("root and data scoped locking, path rules and scan pause", Block("li", "watcher-lock"),
            "same arm root, --data directory and exact address", "different arm roots do not coordinate",
            "canonical drive-letter absolute path with no trailing separator", "no junction or symbolic link",
            "pause after each scan", "default 15, range 1 through 300", "smaller of 40 seconds and the remaining lifetime");

        MatchCollection tables = Regex.Matches(guide, "<table id=\"watcher-exits\">(?<body>.*?)</table>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var exits = new Dictionary<string, string>(StringComparer.Ordinal);
        bool unique = tables.Count == 1;
        if (tables.Count == 1)
            foreach (Match row in Regex.Matches(tables[0].Groups["body"].Value,
                "<tr><td>(?<code>[^<]+)</td><td>(?<meaning>.*?)</td></tr>", RegexOptions.Singleline | RegexOptions.CultureInvariant))
                unique &= exits.TryAdd(Plain(row.Groups["code"].Value), Plain(row.Groups["meaning"].Value));
        if (!unique || exits.Count != 9) missing.Add("one complete exit table");
        string Exit(string code) => exits.GetValueOrDefault(code, "");
        Require("exit 0 cursor comes from the inbox read", Exit("0"), "complete scan", "not in --seen-ids",
            "cursor from that read", "metadata line contains no cursor");
        Require("exit 20 lifetime or cancellation", Exit("20"), "finite lifetime ended", "lifetime", "cancelled",
            "unreported is greater than 0", "read the inbox before deciding whether to rearm");
        Require("exit 21 refreshes refused cursor/address", Exit("21"), "read_inbox_page refused", "session_unavailable",
            "invalid_inbox_cursor", "tool_error", "fresh cursor", "do not rearm with the old address");
        Require("exit 22 counts consecutive failures", Exit("22"), "Four scans in a row failed", "Read the inbox");
        Require("exit 23 is loss of the exact bound parent", Exit("23"), "--parent-pid", "--parent-start",
            "could no longer be confirmed as that exact process", "only in bound mode", "arming produces exit 2");
        Require("exit 24 includes a certificate-renewal reload", Exit("24"), "worker restart or an in-process certificate-renewal reload",
            "Discard it", "handle each message ID once", "cursor from that read");
        Require("exit 25 is a scoped lock collision", Exit("25"), "arm root, data directory and exact address",
            "another watcher or another process", "Different roots do not share this lock");
        Require("exit 2 is pre-watch arming refusal", Exit("2"), "before watching began", "unconfirmed parent",
            "data/arm directories", "pre-arm setup failure", "Read the inbox");
        Require("exit 26 may hide already-found mail", Exit("26"), "before or during watching",
            "output failure may hide mail already found", "Read the inbox");
        Require("unreported and incomplete output never prove an empty inbox", Block("p", "watcher-unreported"),
            "unreported is greater than 0", "partial scan", "value of 0 does not mean the inbox is empty",
            "without a complete metadata line establishes neither mail nor an empty inbox", "never mean mail");

        check(missing.Count == 0,
            "WATCH_GUIDE_EXACT_CONTRACT: the compiled guide states the watcher's address, argument, locking, scan and exit conditions" +
            (missing.Count == 0 ? "" : " (missing: " + string.Join("; ", missing) + ")"));

        string cadence = Block("p", "mailbox-presence-cadence");
        string waitingMail = Block("section", "waiting-mail");
        string[] cadenceClauses = ["listed to paired brokers", "calls at least every 50 seconds",
            "back-to-back 25-second wait_for_messages calls", "One call every 180 seconds lists it for 60 of each 180 seconds",
            "it still receives while it is not listed"];
        check(cadence.Length > 0 && waitingMail.Contains(cadence, StringComparison.Ordinal) &&
            cadenceClauses.All(clause => cadence.Contains(clause, StringComparison.Ordinal)),
            "GUIDE_MAILBOX_PRESENCE_CADENCE: Waiting mail distinguishes the 50-second call cadence and chained 25-second waits from 60-of-180-second presence and continued receipt");

        string claudeDefault = Block("p", "claude-update-tolerant");
        check(claudeDefault.Contains("For Claude, \"Also wake updated Claude builds\" (on by default)", StringComparison.Ordinal) &&
            claudeDefault.Contains("Turn it off to wake exactly verified builds only.", StringComparison.Ordinal),
            "GUIDE_WAKE_DEFAULT_CLAUDE: the Claude paragraph binds the default-on disclosure and exact-build opt-out to that switch");
        string codexDefault = Block("p", "codex-update-tolerant");
        check(codexDefault.Contains("For Codex, \"Also wake updated Codex builds\" (on by default)", StringComparison.Ordinal) &&
            codexDefault.Contains("Turn it off to wake exactly verified builds only.", StringComparison.Ordinal),
            "GUIDE_WAKE_DEFAULT_CODEX: the Codex paragraph binds the default-on disclosure and exact-build opt-out to that switch");
    }
}
