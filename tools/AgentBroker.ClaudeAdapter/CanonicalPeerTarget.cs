using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBroker.ClaudeAdapter;

public static class CanonicalPeerTarget
{
    private static readonly Regex Pipe = new(@"^[\\/]{2}[.?][\\/]pipe[\\/](?:(LOCAL)[\\/])?([^\\/]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Message = new(@"^[cC][cC]-[mM][sS][gG]-[0-9a-fA-F]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Public native OJ/YE behavior, with the additional cc-msg message-pipe restriction.</summary>
    public static (string PipeName, string CanonicalPath) Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 || value.StartsWith(@"\\?\", StringComparison.Ordinal) && value.Contains('/'))
            throw new ArgumentException("Invalid native message pipe.", nameof(value));
        var match = Pipe.Match(value);
        if (!match.Success) throw new ArgumentException("Invalid native message pipe.", nameof(value));
        string name = match.Groups[2].Value;
        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || !Message.IsMatch(name))
            throw new ArgumentException("Invalid native message pipe.", nameof(value));
        string pipeName = match.Groups[1].Success ? @"LOCAL\" + name : name;
        char[] lower = pipeName.ToCharArray();
        for (int i = 0; i < lower.Length; i++) if (lower[i] is >= 'A' and <= 'Z') lower[i] = (char)(lower[i] + ('a' - 'A'));
        return (pipeName, @"\\.\pipe\" + new string(lower));
    }

    public static CredentialTarget Derive(string pipePath, int serverProcessId, string userHome, string? configDirectory = null)
    {
        if (serverProcessId <= 0 || !Path.IsPathFullyQualified(userHome) ||
            configDirectory is not null && !Path.IsPathFullyQualified(configDirectory))
            throw new ArgumentException("Absolute native configuration paths and a server PID are required.");
        var pipe = Parse(pipePath);
        string root = (configDirectory ?? Path.Combine(userHome, ".claude")).Normalize(NormalizationForm.FormC);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipe.CanonicalPath))).ToLowerInvariant();
        string name = serverProcessId.ToString(CultureInfo.InvariantCulture) + "." + digest + ".key";
        return new CredentialTarget(Path.Combine(Path.GetFullPath(root), "sessions", name), serverProcessId, pipe.CanonicalPath);
    }
}
