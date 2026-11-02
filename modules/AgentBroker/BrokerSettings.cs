using System.Globalization;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.AgentBroker;

internal sealed record BrokerSettings(bool Enabled, string Name, int LoopbackPort, int LanPort,
    bool EnableLan, bool NoAuthLoopback, bool CodexDiscovery, bool ClaudeDiscovery,
    bool CodexPeerMessages, bool ClaudePeerCredentials, int MessageLifetimeHours = 24,
    bool CodexUpdateTolerantDelivery = true, bool ClaudeUpdateTolerantDelivery = true)
{
    internal static BrokerSettings Read(IModuleSettings settings) => new(
        settings.GetBool("enabled", false), settings.Get("name", Environment.MachineName),
        settings.GetInt("loopbackPort", 7432), settings.GetInt("lanPort", 7433),
        settings.GetBool("enableLan", true), settings.GetBool("noAuthLoopback", true),
        settings.GetBool("codexDiscovery", true), settings.GetBool("claudeDiscovery", true),
        settings.GetBool("codexPeerMessages", false), settings.GetBool("claudePeerCredentials", false),
        settings.GetInt("messageLifetimeHours", 24),
        // On by default since 1.5.0 (the owner's decision of 2026-10-29): publisher trust plus the runtime checks is the
        // normal Codex policy once Codex delivery is on. Off is the strict mode, exact pins only.
        settings.GetBool("codexUpdateTolerantDelivery", true),
        // On by default (the owner's decision of 2026-11-02): an Anthropic-signed Claude Code build that is not exactly
        // pinned is woken once Claude wake is on, so an update every few days does not make every Claude row
        // observation-only until a re-pin ships. Off is the strict mode, exact pins only.
        settings.GetBool("claudeUpdateTolerantDelivery", true));

    internal bool Valid => Name.Length is > 0 and <= 80 && !string.IsNullOrWhiteSpace(Name) &&
        !Name.Any(char.IsControl) && LoopbackPort is >= 1 and <= 65535 && LanPort is >= 1 and <= 65535 &&
        (!EnableLan || LoopbackPort != LanPort) && MessageLifetimeHours is >= 1 and <= 24;

    internal Dictionary<string, string> Values() => new(StringComparer.Ordinal)
    {
        ["enabled"] = Bool(Enabled), ["name"] = Name,
        ["loopbackPort"] = LoopbackPort.ToString(CultureInfo.InvariantCulture),
        ["lanPort"] = LanPort.ToString(CultureInfo.InvariantCulture), ["enableLan"] = Bool(EnableLan),
        ["noAuthLoopback"] = Bool(NoAuthLoopback), ["codexDiscovery"] = Bool(CodexDiscovery),
        ["claudeDiscovery"] = Bool(ClaudeDiscovery), ["codexPeerMessages"] = Bool(CodexPeerMessages),
        ["claudePeerCredentials"] = Bool(ClaudePeerCredentials),
        ["codexUpdateTolerantDelivery"] = Bool(CodexUpdateTolerantDelivery),
        ["claudeUpdateTolerantDelivery"] = Bool(ClaudeUpdateTolerantDelivery),
        ["messageLifetimeHours"] = MessageLifetimeHours.ToString(CultureInfo.InvariantCulture)
    };

    internal static bool TryEdit(BrokerSettings current, IReadOnlyDictionary<string, string> edit,
        out BrokerSettings result)
    {
        var values = current.Values();
        foreach (string key in values.Keys.ToArray()) if (edit.TryGetValue(key, out string? value)) values[key] = value;
        result = current;
        if (!bool.TryParse(values["enabled"], out bool enabled) ||
            !int.TryParse(values["loopbackPort"], NumberStyles.None, CultureInfo.InvariantCulture, out int local) ||
            !int.TryParse(values["lanPort"], NumberStyles.None, CultureInfo.InvariantCulture, out int lan) ||
            !int.TryParse(values["messageLifetimeHours"], NumberStyles.None, CultureInfo.InvariantCulture, out int messageLifetimeHours) ||
            !bool.TryParse(values["enableLan"], out bool enableLan) ||
            !bool.TryParse(values["noAuthLoopback"], out bool noAuth) ||
            !bool.TryParse(values["codexDiscovery"], out bool codexDiscovery) ||
            !bool.TryParse(values["claudeDiscovery"], out bool claudeDiscovery) ||
            !bool.TryParse(values["codexPeerMessages"], out bool codexMessages) ||
            !bool.TryParse(values["claudePeerCredentials"], out bool claudeCredentials) ||
            !bool.TryParse(values["codexUpdateTolerantDelivery"], out bool codexUpdateTolerant) ||
            !bool.TryParse(values["claudeUpdateTolerantDelivery"], out bool claudeUpdateTolerant)) return false;
        result = new(enabled, values["name"].Trim(), local, lan, enableLan, noAuth,
            codexDiscovery, claudeDiscovery, codexMessages, claudeCredentials, messageLifetimeHours, codexUpdateTolerant,
            claudeUpdateTolerant);
        return result.Valid;
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
