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
        ["out/extension.js"] = "A8069DE80C8F8141481D033F94C3A95E1B59F399143CB2554D3F68469DFBE5B8",
        ["webview/assets/app-main-92173aa1395c.js"] = "B4B080A8F62E22CFD67F2FAE85E39854DF5FF4ED8F898B8A7D8A6FE36886A357",
        ["webview/assets/app-initial-c014f9ee4429.js"] = "AABBFA7C366706365A239B090CA1C657B6A0213D7287E54EF0A32D1B2E5FF6A0",
        ["webview/assets/app-initial-97d3534ad35f.js"] = "47E9E488122CF37A6564ED57D11F499D9AC062B2E49DB729091FE3C163A64B49",
        ["webview/assets/app-initial-7a199c66e670.js"] = "59B909377A370E09CB8523AB99A1F4FF282A0126527BD339DB31E6D9AC7C9C26"
    };

    public static void VerifyInstalled()
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions", DirectoryName);
        VerifyInstalledAt(root);
    }

    internal static void VerifyInstalledAt(string root)
    {
        string manifest = Path.Combine(root, "package.json");
        if (!File.Exists(manifest)) throw new DesktopAdapterException("unsupported_vscode_profile");
        DesktopCompatibilityProfile.VerifyRegularPath(manifest);
        using (var stream = File.OpenRead(manifest)) VsCodeManifestProfile.Verify(VsCodeManifestProfile.ReadBounded(stream));
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

    /// <summary>
    /// Publisher counterpart of <see cref="VerifyRunningClients"/> for a build that is not exactly pinned: every direct
    /// Code-owned native engine must be a same-user child of <c>Code.exe</c> at an installed OpenAI extension layout,
    /// with a Windows-verified OpenAI signature. Extension JavaScript is unsigned, so this mode cannot verify it;
    /// the compatibility document records that gap. It never stands in for the exact profile on its own.
    /// </summary>
    public static int VerifyRunningClientsByPublisher()
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        return ValidateRunningClientsByPublisher(WindowsClientProcesses.ReadCodeOwnedEngines(),
            image => WindowsPublisherImageTrust.Verify(image, WindowsPublisherImageTrust.OpenAIPublisher, false));
    }

    internal static int ValidateRunningClientsByPublisher(IReadOnlyList<CodeOwnedEngine> engines, Func<string, PublisherImageTrust> verify)
    {
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions"));
        foreach (var engine in engines)
        {
            string? version = PublisherEngineVersion(engine.Image, root);
            if (!engine.SameUser || engine.ParentCreationFileTime > engine.CreationFileTime
                || !string.Equals(Path.GetFileName(engine.ParentImage), "Code.exe", StringComparison.OrdinalIgnoreCase)
                || version is null)
                throw new DesktopAdapterException("unsupported_running_vscode_profile");
            WindowsDesktopConnection.RequirePublisher(verify(engine.Image), version);
        }
        return engines.Count;
    }

    /// <summary>The extension version of <c>&lt;root&gt;\openai.chatgpt-&lt;version&gt;-win32-x64\bin\windows-x86_64\codex.exe</c>, else null.</summary>
    internal static string? PublisherEngineVersion(string image, string root)
    {
        const string prefix = "openai.chatgpt-", suffix = "-win32-x64";
        string full = Path.GetFullPath(image);
        string? platform = Path.GetDirectoryName(full);
        string? bin = Path.GetDirectoryName(platform);
        string? extension = Path.GetDirectoryName(bin);
        if (platform is null || bin is null || extension is null ||
            !string.Equals(Path.GetFileName(full), "codex.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(platform), "windows-x86_64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(extension), root, StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileName(extension);
        if (name.Length <= prefix.Length + suffix.Length || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string version = name[prefix.Length..^suffix.Length];
        return System.Version.TryParse(version, out var parsed) && parsed.Revision == -1 && parsed.Build >= 0 &&
            parsed.ToString() == version ? version : null;
    }
}
