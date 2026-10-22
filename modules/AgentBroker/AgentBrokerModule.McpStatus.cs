using AgentBroker.Protocol;
using System.Globalization;

namespace DesktopAICompanion.AgentBroker;

public sealed partial class AgentBrokerModule
{
    internal Func<DateTimeOffset> McpNow = static () => DateTimeOffset.UtcNow;

    private (string Summary, string Detail) McpDisplay() => !settings.Enabled
        ? ("MCP disabled.", "Enable AgentBroker to accept MCP connections.")
        : FormatMcpStatus(snapshot?.Mcp, McpNow());

    private static (string Summary, string Detail) FormatMcpStatus(McpStatusView? view, DateTimeOffset now)
    {
        if (view is null)
            return ("MCP status unknown.", "No worker MCP connection status is available yet.");
        if (!view.EndpointReady)
            return ("MCP unavailable.", "The broker MCP endpoint is not accepting connections.");
        var clients = view.Clients;
        if (clients is null || clients.Length != 3 || clients.Any(client => client is null || !Enum.IsDefined(client.Family)) ||
            clients.Select(client => client.Family).Distinct().Count() != 3)
            return ("MCP ready; client status unknown.", "The broker MCP endpoint is ready; client activity is unavailable.");

        string Client(McpClientFamily family, string label)
        {
            var client = clients.Single(row => row.Family == family);
            DateTimeOffset? seen = new[] { client.LastInitializeUtc, client.LastSuccessfulRequestUtc,
                client.LastToolsListUtc, client.LastSuccessfulToolUtc }.Max();
            return label + ": " + (seen is null ? "not seen" : seen > now.AddSeconds(5)
                ? "activity time unknown" : "seen " + Age(seen.Value, now));
        }

        string claude = Client(McpClientFamily.Claude, "Claude");
        string codex = Client(McpClientFamily.Codex, "Codex");
        string other = Client(McpClientFamily.Other, "Other clients");
        string tools = view.LastToolsListUtc is null ? "No successful tool-list request observed."
            : view.LastToolsListUtc > now.AddSeconds(5) ? "Tool-list activity time unknown."
            : "Tool list served " + Age(view.LastToolsListUtc.Value, now) + ".";
        return ($"MCP ready. {claude}; {codex}.",
            $"{claude}; {codex}; {other}. {tools} Last seen records successful MCP traffic. Idle clients connect on demand.");
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
