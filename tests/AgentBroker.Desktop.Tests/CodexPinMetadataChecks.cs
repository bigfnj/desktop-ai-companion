using AgentBroker.CodexDesktopAdapter;
using System.Security.Cryptography;
using System.Text;

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
            DesktopCompatibilityProfile.Packages[0].PackageFullName, "app", "ChatGPT.exe")) == DesktopCompatibilityProfile.Packages[0].ApplicationVersion,
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
                VsCodeRouterCompatibilityProfile.Pins[0].ExecutableSha256, "1.144.0");
            throw new InvalidOperationException("An unverified VS Code router passed its existing exact version pin.");
        }
        catch (DesktopAdapterException error)
        {
            check(error.Code == "unsupported_vscode_router" && error.InstalledVersion == "1.144.0",
                "Rejected VS Code router retains the version already observed by its existing metadata check");
        }
        ImageMismatchChecks(check);
    }

    private static void ImageMismatchChecks(Action<bool, string> check)
    {
        // No desktop files are opened: an exact selected package path already identifies its application version.
        // PinDocument validates that PackageFullName contains exactly ApplicationVersion, even for an image mismatch.
        foreach (var package in DesktopCompatibilityProfile.Packages)
            RefusesWith("desktop_image_mismatch", DesktopCompatibilityProfile.InstalledApplicationVersion(package.ExecutablePath),
                () => DesktopCompatibilityProfile.ValidateMetadata(package.ExecutablePath, new string('0', 64),
                    package.ArchiveSha256, package.ElectronVersion), check,
                "F4_IMAGE_MISMATCH_DESKTOP_VERSION: a changed image reports the installed package-path version");

        string fixture = Path.Combine(Path.GetTempPath(), "agentbroker-image-metadata-" + Guid.NewGuid().ToString("N"));
        const string version = "26.9999.1";
        string directory = "openai.chatgpt-" + version + "-win32-x64";
        string extension = Path.Combine(fixture, directory);
        string bundleDirectory = Path.Combine(extension, "out");
        string platformDirectory = Path.Combine(extension, "bin", "windows-x86_64");
        string manifest = Path.Combine(extension, "package.json");
        string bundle = Path.Combine(bundleDirectory, "extension.js");
        string engine = Path.Combine(platformDirectory, "codex.exe");
        byte[] manifestBytes = Encoding.UTF8.GetBytes("{\"version\":\"26.9999.1\",\"main\":\"./out/extension.js\"}");
        byte[] bundleBytes = Encoding.UTF8.GetBytes("synthetic extension bundle");
        byte[] engineBytes = Encoding.UTF8.GetBytes("synthetic native image");
        string bundleHash = Convert.ToHexString(SHA256.HashData(bundleBytes));
        string engineHash = Convert.ToHexString(SHA256.HashData(engineBytes));
        // Deliberately different pin metadata makes a fallback to profile.Version fail the diagnostic checks.
        var profile = new VsCodeCompatibilityProfile.ExtensionProfile("1.2.3", directory, engineHash,
            VsCodeManifestProfile.ComputeSha256(manifestBytes), [new("out/extension.js", bundleHash)]);
        Directory.CreateDirectory(bundleDirectory);
        Directory.CreateDirectory(platformDirectory);
        try
        {
            File.WriteAllBytes(manifest, manifestBytes);
            File.WriteAllBytes(bundle, bundleBytes);
            File.WriteAllBytes(engine, engineBytes);
            RefusesWith("vscode_image_mismatch", version,
                () => VsCodeCompatibilityProfile.VerifyInstalledAt(extension,
                    profile with { ManifestFunctionalSha256 = new string('0', 64) }), check,
                "F4_IMAGE_MISMATCH_MANIFEST_LAYOUT_VERSION: a manifest mismatch names its checked layout, not the pin version");
            File.WriteAllText(bundle, "changed synthetic extension bundle", new UTF8Encoding(false));
            RefusesWith("vscode_image_mismatch", version,
                () => VsCodeCompatibilityProfile.VerifyInstalledAt(extension, profile), check,
                "F4_IMAGE_MISMATCH_BUNDLE_LAYOUT_VERSION: a bundle mismatch names its checked layout, not the pin version");
            File.WriteAllBytes(bundle, bundleBytes);
            var running = new CodeOwnedEngine(engine, 10, "Code.exe", 5, true);
            RefusesWith("running_vscode_image_mismatch", version,
                () => VsCodeCompatibilityProfile.ValidateRunningClients([running],
                    [profile with { NativeImageSha256 = new string('0', 64) }], fixture), check,
                "F4_IMAGE_MISMATCH_RUNNING_LAYOUT_VERSION: a running image mismatch names its actual engine layout, not the pin version");
            VsCodeCompatibilityProfile.VerifyInstalledAt(extension, profile);
            check(VsCodeCompatibilityProfile.ValidateRunningClients([running], [profile], fixture) == 1,
                "F4_IMAGE_MISMATCH_EXACT_FIXTURE: exact manifest, bundle and native hashes retain their existing acceptance");
            foreach (string unknown in new[] { "synthetic-extension", "openai.chatgpt-not-a-version-win32-x64" })
            {
                string unknownRoot = Path.Combine(fixture, unknown);
                string unknownManifest = Path.Combine(unknownRoot, "package.json");
                Directory.CreateDirectory(unknownRoot);
                try
                {
                    File.WriteAllBytes(unknownManifest, manifestBytes);
                    RefusesWith("vscode_image_mismatch", null,
                        () => VsCodeCompatibilityProfile.VerifyInstalledAt(unknownRoot,
                            profile with { ManifestFunctionalSha256 = new string('0', 64) }), check,
                        "F4_IMAGE_MISMATCH_UNKNOWN_LAYOUT_VERSION: unrelated or malformed folders never borrow a reviewed version");
                }
                finally { File.Delete(unknownManifest); Directory.Delete(unknownRoot); }
            }
        }
        finally
        {
            File.Delete(engine); File.Delete(bundle); File.Delete(manifest);
            Directory.Delete(platformDirectory); Directory.Delete(Path.GetDirectoryName(platformDirectory)!);
            Directory.Delete(bundleDirectory); Directory.Delete(extension); Directory.Delete(fixture);
        }
    }

    private static void RefusesWith(string code, string? version, Action action, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (DesktopAdapterException error)
        { check(error.Code == code && error.InstalledVersion == version, "CodexPinMetadataChecks.cs: " + label); return; }
        throw new InvalidOperationException("CodexPinMetadataChecks.cs: " + label);
    }
}
