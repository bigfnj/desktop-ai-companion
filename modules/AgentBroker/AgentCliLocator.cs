using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace DesktopAICompanion.AgentBroker;

/// <summary>The values the CLI locator reads, behind a seam so the self-test hands it a scratch tree. A null field is a
/// place the locator does not look.</summary>
internal sealed record AgentCliEnvironment(string? PathValue = null, string? PersistedPathValue = null, string? AppData = null,
    string? UserProfile = null, string? LocalAppData = null, string? ProgramFiles = null)
{
    internal static AgentCliEnvironment Current() => new(Environment.GetEnvironmentVariable("PATH"), PersistedPath(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    // The machine's PATH then the user's, as saved NOW, in the order Windows joins them for a new process: this host
    // keeps the PATH it was started with, so a CLI installed since is on every new terminal's PATH but not on this one.
    private static string PersistedPath()
    {
        var parts = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            try { if (Environment.GetEnvironmentVariable("PATH", target) is { Length: > 0 } value) parts.Add(value); }
            catch (System.Security.SecurityException) { }
        }
        return string.Join(Path.PathSeparator, parts);
    }
}

/// <summary>A located CLI: the real binary, and for an npm Codex the package root its codex.js launcher names.</summary>
internal sealed record AgentCliInstall(string Executable, string? NpmCodexPackageRoot = null);

/// <summary>
/// N-broker-a-01: finds the Claude Code or Codex binary the companion's model calls use, by exactly the rules of the
/// shared runner's locator (shared/CodingAgentCli/CodingAgentCli.cs, CodingAgentLocator.Locate) and AI Brain's executable
/// trust rules (modules/AiBrain/engine/AiExecutablePolicy.cs, ResolveConfigured), so client setup and the model calls
/// find the same executables. Setup used to start a bare "claude" or "codex" with UseShellExecute=false, which never
/// starts an npm .cmd shim, so a box where both CLIs are npm installs read "CLI not found". The order a terminal would
/// use: PATH folder by folder, the CLI's own .exe first and an npm shim's target second; then the PATH saved now; then
/// where the installers put the binary whatever PATH says: npm's default prefix, Claude Code's native installer, WinGet's
/// package folders, and last the copy inside VS Code's Claude Code extension. A shim is never run, through cmd.exe or
/// otherwise; it only marks a folder as an npm prefix whose package holds the binary.
///
/// A copy, not a source link: the runner's file is the whole model-call runner, written nullable-oblivious, and this
/// module compiles with nullable warnings as errors; linking it would also make every runner change stale this payload.
/// A change to the shared locator's rules must be made here too (DESIGN-REGISTER, feature/agentbroker-backlog-d).
/// </summary>
internal static class AgentCliLocator
{
    internal const string ClaudeWinGetId = "Anthropic.ClaudeCode";
    internal const string CodexWinGetId = "OpenAI.Codex";

    internal static AgentCliInstall? Locate(string client, AgentCliEnvironment environment)
    {
        if (client is not ("claude" or "codex")) return null;
        bool claude = client == "claude";
        string exeName = client + ".exe";
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? pathValue in new[] { environment.PathValue, environment.PersistedPathValue })
            if (OnPath(claude, pathValue, exeName, searched) is { } onPath) return onPath;
        if (!string.IsNullOrWhiteSpace(environment.AppData) && FromNpmPrefix(claude, Path.Combine(environment.AppData, "npm")) is { } npm)
            return npm;
        if (claude && !string.IsNullOrWhiteSpace(environment.UserProfile) &&
            Trusted(Path.Combine(environment.UserProfile, ".local", "bin", "claude.exe"), "claude.exe") is { } native) return new(native);
        if (FromWinGet(claude, environment) is { } winget) return winget;
        return claude ? FromVsCodeExtension(environment) : null;
    }

    /// <summary>The first install in one PATH value's folders, skipping a folder an earlier value already searched.</summary>
    private static AgentCliInstall? OnPath(bool claude, string? pathValue, string exeName, HashSet<string> searched)
    {
        foreach (string raw in (pathValue ?? "").Split(Path.PathSeparator))
        {
            string directory;
            try { directory = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')); }
            catch (ArgumentException) { continue; }
            if (directory.Length == 0 || !searched.Add(directory.TrimEnd('\\', '/'))) continue;
            if (InDirectory(claude, directory, exeName) is { } found) return found;
        }
        return null;
    }

    private static AgentCliInstall? InDirectory(bool claude, string directory, string exeName)
    {
        if (Trusted(Path.Combine(directory, exeName), exeName) is { } direct) return new(direct);
        string stem = Path.GetFileNameWithoutExtension(exeName);
        foreach (string shim in new[] { stem + ".cmd", stem + ".ps1", stem })
            if (Trusted(Path.Combine(directory, shim), shim) is not null) return FromNpmPrefix(claude, directory);
        return null;
    }

    /// <summary>The binary an npm global prefix's shims start, or null when that package is not there.</summary>
    internal static AgentCliInstall? FromNpmPrefix(bool claude, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        if (claude)
            return Trusted(Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"), "claude.exe") is { } exe
                ? new(exe) : null;
        string scope = Path.Combine(prefix, "node_modules", "@openai"), root = Path.Combine(scope, "codex");
        foreach (string candidate in CodexCandidates(scope, root))
            if (Trusted(candidate, "codex.exe") is { } exe) return new(exe, root);
        return null;
    }

    // Where codex.js finds the native binary: the platform package nested under the main one, the same package hoisted
    // beside it, then the main package's own vendor folder (older packaging). The machine's own architecture first.
    private static IEnumerable<string> CodexCandidates(string scope, string root)
    {
        var targets = new List<(string Package, string Triple)>
            { ("codex-win32-x64", "x86_64-pc-windows-msvc"), ("codex-win32-arm64", "aarch64-pc-windows-msvc") };
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64) targets.Reverse();
        foreach (var (package, triple) in targets)
        {
            string tail = Path.Combine("vendor", triple, "bin", "codex.exe");
            yield return Path.Combine(root, "node_modules", "@openai", package, tail);
            yield return Path.Combine(scope, package, tail);
            yield return Path.Combine(root, tail);
        }
    }

    // The CLI's WinGet portable package, user scope first, then machine scope, read from the package folder itself, never
    // through WinGet's link (a symbolic link, which the trust rules refuse).
    private static AgentCliInstall? FromWinGet(bool claude, AgentCliEnvironment environment)
    {
        string id = claude ? ClaudeWinGetId : CodexWinGetId;
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(environment.LocalAppData)) roots.Add(Path.Combine(environment.LocalAppData, "Microsoft", "WinGet", "Packages"));
        if (!string.IsNullOrWhiteSpace(environment.ProgramFiles)) roots.Add(Path.Combine(environment.ProgramFiles, "WinGet", "Packages"));
        foreach (string root in roots)
        {
            string[] packages;
            try { packages = Directory.GetDirectories(root, id + "_*"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            Array.Sort(packages, StringComparer.OrdinalIgnoreCase);
            foreach (string package in packages)
                foreach (string name in WinGetExecutableNames(claude))
                    if (Trusted(Path.Combine(package, name), name) is { } exe) return new(exe);
        }
        return null;
    }

    // Claude Code's binary keeps its name; Codex's keeps its release name, this machine's architecture first.
    private static IEnumerable<string> WinGetExecutableNames(bool claude)
    {
        if (claude) { yield return "claude.exe"; yield break; }
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64) yield return "codex-aarch64-pc-windows-msvc.exe";
        yield return "codex-x86_64-pc-windows-msvc.exe";
        yield return "codex.exe";
    }

    // The newest copy VS Code's Claude Code extension carries that this machine can run, its own architecture first.
    private static AgentCliInstall? FromVsCodeExtension(AgentCliEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(environment.UserProfile)) return null;
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string own = arm ? "win32-arm64" : "win32-x64";
        string? best = null;
        Version? bestVersion = null;
        bool bestOwn = false;
        foreach (string folder in new[] { ".vscode", ".vscode-insiders" })
        {
            string[] extensions;
            try { extensions = Directory.GetDirectories(Path.Combine(environment.UserProfile, folder, "extensions"), "anthropic.claude-code-*"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (string extension in extensions)
            {
                if (!TryParseExtensionFolder(Path.GetFileName(extension), out var version, out string platform)) continue;
                bool isOwn = platform.Length == 0 || string.Equals(platform, own, StringComparison.OrdinalIgnoreCase);
                if (!isOwn && !(arm && string.Equals(platform, "win32-x64", StringComparison.OrdinalIgnoreCase))) continue;
                if (Trusted(Path.Combine(extension, "resources", "native-binary", "claude.exe"), "claude.exe") is not { } exe) continue;
                if (best is null || isOwn && !bestOwn || isOwn == bestOwn && version > bestVersion)
                { best = exe; bestVersion = version; bestOwn = isOwn; }
            }
        }
        return best is null ? null : new(best);
    }

    // "anthropic.claude-code-2.1.292-win32-x64" is version 2.1.292 for win32-x64; no platform is universal.
    private static bool TryParseExtensionFolder(string name, [NotNullWhen(true)] out Version? version, out string platform)
    {
        const string prefix = "anthropic.claude-code-";
        version = null;
        platform = "";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = name[prefix.Length..];
        int dash = rest.IndexOf('-');
        platform = dash < 0 ? "" : rest[(dash + 1)..];
        return Version.TryParse(dash < 0 ? rest : rest[..dash], out version);
    }

    /// <summary>
    /// AI Brain's executable trust rules (AiExecutablePolicy.ResolveConfigured) for every candidate, shims included: a
    /// drive-qualified path on a drive that is neither remote nor unknown, the expected file name, an existing file, and
    /// no reparse point on any segment, so a relative, UNC, mapped-network or linked location is neither probed nor run.
    /// </summary>
    internal static string? Trusted(string value, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expectedFileName) ||
            Path.GetFileName(expectedFileName) != expectedFileName) return null;
        try
        {
            string candidate = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!DriveQualified(candidate)) return null;
            // GetFullPath is lexical for a drive-qualified path; check the shape again so no device namespace is probed.
            string canonical = Path.GetFullPath(candidate);
            if (!DriveQualified(canonical) || GetDriveType(canonical[..3]) is DriveUnknown or DriveNoRootDirectory or DriveRemote) return null;
            if (!string.Equals(Path.GetFileName(canonical), expectedFileName, StringComparison.OrdinalIgnoreCase) || !File.Exists(canonical))
                return null;
            string current = Path.GetPathRoot(canonical) ?? "";
            string[] segments = canonical[current.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (current.Length == 0 || segments.Length == 0) return null;
            for (int index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                FileAttributes attributes = File.GetAttributes(current);
                bool last = index == segments.Length - 1, directory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || last == directory) return null;
            }
            return canonical;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException) { return null; }
    }

    private static bool DriveQualified(string value) =>
        value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';

    private const uint DriveUnknown = 0, DriveNoRootDirectory = 1, DriveRemote = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);
}
