using AgentBroker.Protocol;
using System.Globalization;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    internal Func<DateTimeOffset> McpNow = static () => DateTimeOffset.UtcNow;

    private (string Summary, string Detail) McpDisplay()
    {
        if (!settings.Enabled) return ("MCP disabled.", "Enable AgentBroker to accept MCP connections.");
        var display = FormatMcpStatus(snapshot?.Mcp, McpNow());
        return (display.Summary + (snapshot is null ? "" : " " + AgentInboxDisplay().Summary), display.Detail);
    }

    private (string Summary, string Detail) AgentInboxDisplay() => !settings.Enabled
        ? ("", "Enable AgentBroker to discover agent inboxes.") : FormatAgentInboxes(snapshot?.Sessions);

    private static (string Summary, string Detail) FormatAgentInboxes(SessionView[]? sessions)
    {
        if (sessions is null || sessions.Any(session => session is null))
            return ("Agent inboxes unknown.", "No worker agent inbox status is available yet.");
        int native = sessions.Count(session => session.Adapter is "claude-native" or "codex-native");
        var generic = sessions.Where(session => session.Adapter == "mcp").ToArray();
        int other = sessions.Length - native - generic.Length;
        string count = generic.Length.ToString(CultureInfo.InvariantCulture);
        string detail = native.ToString(CultureInfo.InvariantCulture) + " native, " + count + " MCP" +
            (other == 0 ? "" : ", " + other.ToString(CultureInfo.InvariantCulture) + " other") + ".";
        if (generic.Length != 0)
        {
            // Labels are display-only declarations, never vendor, connection or native-owner evidence.
            string Label(SessionView session)
            {
                string label = Safe(session.Label, 48).Replace('"', '\'');
                return "\"" + (string.IsNullOrWhiteSpace(label) ? "Unnamed" : label) + "\"";
            }
            detail += " MCP labels (self-reported): " + string.Join(", ", generic.Take(4).Select(Label)) +
                (generic.Length > 4 ? "; " + (generic.Length - 4).ToString(CultureInfo.InvariantCulture) + " more" : "") + ".";
        }
        return ("MCP agent inboxes: " + count + ".", detail);
    }

    private static (string Summary, string Detail) FormatMcpStatus(McpStatusView? view, DateTimeOffset now)
    {
        if (view is null)
            return ("MCP status unknown.", "No worker MCP client traffic status is available yet. Live connection health is not verified.");
        if (!view.EndpointReady)
            return ("MCP unavailable.", "The broker MCP endpoint is not accepting connections.");
        var clients = view.Clients;
        if (clients is null || clients.Length != 3 || clients.Any(client => client is null || !Enum.IsDefined(client.Family)) ||
            clients.Select(client => client.Family).Distinct().Count() != 3)
            return ("MCP ready; client traffic unknown.", "The broker MCP endpoint is ready; client traffic is unavailable. Live connection health is not verified.");

        string Client(McpClientFamily family, string label)
        {
            var client = clients.Single(row => row.Family == family);
            DateTimeOffset? seen = new[] { client.LastInitializeUtc, client.LastSuccessfulRequestUtc,
                client.LastToolsListUtc, client.LastSuccessfulToolUtc }.Max();
            return label + ": " + (seen is null ? "not seen" : seen > now.AddSeconds(5)
                ? "activity time unknown" : "seen " + Age(seen.Value, now));
        }

        string claude = Client(McpClientFamily.Claude, "Claude client");
        string codex = Client(McpClientFamily.Codex, "Codex client");
        string other = Client(McpClientFamily.Other, "Other clients");
        string tools = view.LastToolsListUtc is null ? "No successful tool-list request observed."
            : view.LastToolsListUtc > now.AddSeconds(5) ? "Tool-list activity time unknown."
            : "Tool list served " + Age(view.LastToolsListUtc.Value, now) + ".";
        return ("MCP ready.",
            $"{claude}; {codex}; {other}. {tools} Last seen records client-implementation traffic, not participating agents. Live connection health is not verified.");
    }

    private static string Age(DateTimeOffset seen, DateTimeOffset now)
    {
        TimeSpan elapsed = now - seen;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
        if (elapsed.TotalDays < 2) return ((int)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";
        return ((int)elapsed.TotalDays).ToString(CultureInfo.InvariantCulture) + " days ago";
    }
}
