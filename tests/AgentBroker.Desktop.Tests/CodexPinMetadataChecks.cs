using AgentBroker.CodexDesktopAdapter;

internal static class CodexPinMetadataChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var legacy = new DesktopAdapterException("synthetic_code");
        check(legacy.Code == "synthetic_code" && legacy.Message == "synthetic_code" && legacy.InstalledVersion is null,
            "Code-only native exceptions retain their existing code, message and unknown version");
        check(new DesktopAdapterException("synthetic_code", "26.1020.1.0").InstalledVersion == "26.1020.1.0",
            "Native compatibility exceptions retain a numeric installed package version");
        foreach (string unsafeVersion in new[] { "not a version", "C:\\synthetic\\private-location", "26.1.2.3\nextra", new string('1', 200) })
            check(new DesktopAdapterException("synthetic_code", unsafeVersion).InstalledVersion is null,
                "Native compatibility diagnostics reject non-version metadata");

        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string upgradedImage = Path.Combine(windowsApps, "OpenAI.Codex_26.1020.1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
        try
        {
            DesktopCompatibilityProfile.VerifyPublicImages(upgradedImage);
            throw new InvalidOperationException("An unverified desktop package passed its existing exact path pin.");
        }
        catch (DesktopAdapterException error)
        {
            check(error.Code == "unsupported_desktop_version" && error.InstalledVersion == "26.1020.1.0",
                "Rejected desktop package reports its installed version before any image or hash read");
        }
        check(DesktopCompatibilityProfile.InstalledApplicationVersion(Path.Combine(windowsApps,
            DesktopCompatibilityProfile.PackageFullName, "app", "ChatGPT.exe")) == DesktopCompatibilityProfile.ApplicationVersion,
            "Desktop package diagnostics identify the currently verified package version");
        foreach (string unsupportedImage in new[]
        {
            Path.Combine(windowsApps, "Other.Product_26.1020.1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe"),
            Path.Combine(windowsApps, "OpenAI.Codex_26.1020.1.0_x64__otherpublisher", "app", "ChatGPT.exe"),
            Path.Combine(windowsApps, "OpenAI.Codex_invalid_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe"),
            Path.Combine(windowsApps, "OpenAI.Codex_26.1020.1.0_x64__2p2nqsd0c76g0", "other", "ChatGPT.exe"),
            Path.Combine(windowsApps, "OpenAI.Codex_26.1020.1.0_x64__2p2nqsd0c76g0", "app", "Other.exe"),
            Path.Combine(Path.GetTempPath(), "OpenAI.Codex_26.1020.1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")
        })
            check(DesktopCompatibilityProfile.InstalledApplicationVersion(unsupportedImage) is null,
                "Desktop diagnostic package parsing does not invent a version for an unrelated image path");

        try
        {
            VsCodeRouterCompatibilityProfile.ValidateMetadata(VsCodeRouterCompatibilityProfile.ExpectedImage,
                VsCodeRouterCompatibilityProfile.ExecutableSha256, "1.144.0");
            throw new InvalidOperationException("An unverified VS Code router passed its existing exact version pin.");
        }
        catch (DesktopAdapterException error)
        {
            check(error.Code == "unsupported_vscode_router" && error.InstalledVersion == "1.144.0",
                "Rejected VS Code router retains the version already observed by its existing metadata check");
        }
    }
}
