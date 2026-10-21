using System.Security.Cryptography;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>
/// Pins the inspected public extension's coordination bridge and guarded owner logic.
/// Presence is a compatibility prerequisite, not proof of which extension owns an arbitrary IPC client ID.
/// </summary>
public static class VsCodeCompatibilityProfile
{
    public const string Version = "26.1007.21434";
    public const string DirectoryName = "openai.chatgpt-26.1007.21434-win32-x64";
    internal const string NativeImageSha256 = "F5521B058CA08E249AFD3B25B4681E81100181D92E73AA6B94BB8E75C2AA487B";
    private static readonly IReadOnlyDictionary<string, string> Images = new Dictionary<string, string>
    {
        ["package.json"] = "6FCA145830386B465597526C3FC8D91873FF86E85D856C5FA608BE16B5901A1C",
        ["out/extension.js"] = "A8069DE80C8F8141481D033F94C3A95E1B59F399143CB2554D3F68469DFBE5B8",
        ["webview/assets/app-main-92173aa1395c.js"] = "B4B080A8F62E22CFD67F2FAE85E39854DF5FF4ED8F898B8A7D8A6FE36886A357",
        ["webview/assets/app-initial-97d3534ad35f.js"] = "47E9E488122CF37A6564ED57D11F499D9AC062B2E49DB729091FE3C163A64B49",
        ["webview/assets/app-initial-7a199c66e670.js"] = "59B909377A370E09CB8523AB99A1F4FF282A0126527BD339DB31E6D9AC7C9C26"
    };

    public static void VerifyInstalled()
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions", DirectoryName);
        foreach (var image in Images)
        {
            string file = Path.Combine(root, image.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) throw new DesktopAdapterException("unsupported_vscode_profile");
            DesktopCompatibilityProfile.VerifyRegularPath(file);
            using var stream = File.OpenRead(file);
            if (Convert.ToHexString(SHA256.HashData(stream)) != image.Value)
                throw new DesktopAdapterException("vscode_image_mismatch");
        }
    }

    /// <summary>
    /// Validates actual Code-owned native engines, rather than trusting an old extension folder left on disk.
    /// Reads only PID/parent/name/image/start/user metadata. Standalone CLI and sandbox children are ignored.
    /// The returned count is diagnostic metadata, not proof that a specific IPC client is a VS Code view.
    /// </summary>
    public static int VerifyRunningClients()
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        var engines = WindowsClientProcesses.ReadCodeOwnedEngines();
        if (engines.Count > 0) VerifyInstalled();
        return ValidateRunningClients(engines);
    }

    internal static int ValidateRunningClients(IReadOnlyList<CodeOwnedEngine> engines)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions",
            DirectoryName, "bin", "windows-x86_64", "codex.exe");
        foreach (var engine in engines)
        {
            if (!engine.SameUser || engine.ParentCreationFileTime > engine.CreationFileTime
                || !string.Equals(Path.GetFileName(engine.ParentImage), "Code.exe", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFullPath(engine.Image), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new DesktopAdapterException("unsupported_running_vscode_profile");
            DesktopCompatibilityProfile.VerifyRegularPath(engine.Image);
            using var stream = File.OpenRead(engine.Image);
            if (Convert.ToHexString(SHA256.HashData(stream)) != NativeImageSha256)
                throw new DesktopAdapterException("running_vscode_image_mismatch");
        }
        return engines.Count;
    }
}
