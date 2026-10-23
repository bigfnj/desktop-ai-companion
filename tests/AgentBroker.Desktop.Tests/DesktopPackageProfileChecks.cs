using AgentBroker.CodexDesktopAdapter;

internal static class DesktopPackageProfileChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var profiles = new[]
        {
            new DesktopCompatibilityProfile.PackageProfile("OpenAI.Codex_26.1016.6573.0_x64__2p2nqsd0c76g0", "26.1016.6573.0",
                "602A21169FA011CF48997DBC07A0FFCDC11058CAA5DC992E534876E8CB6F9A4E",
                "6F56542B508EEA5B6E4D03E852DCD90AFC2578AAC7125BAA3F25EBE85EBC7E25", "155.0.8059.80"),
            new DesktopCompatibilityProfile.PackageProfile("OpenAI.Codex_26.1016.8622.0_x64__2p2nqsd0c76g0", "26.1016.8622.0",
                "F7F605CCEB2E75DCA52E03362329A889F917CC1F8D1036CD26386727A2319415",
                "A05DFD86CA508148C83C25590639CF91A52C6647733CDF200723D85357052B49", "155.0.8059.80")
        };
        check(DesktopCompatibilityProfile.PackageFullName == profiles[0].PackageFullName &&
            DesktopCompatibilityProfile.ApplicationVersion == profiles[0].ApplicationVersion &&
            DesktopCompatibilityProfile.ExecutableSha256 == profiles[0].ExecutableSha256 &&
            DesktopCompatibilityProfile.ArchiveSha256 == profiles[0].ArchiveSha256 &&
            DesktopCompatibilityProfile.ElectronVersion == profiles[0].ElectronVersion,
            "DesktopPackage: historical public constants retain the original exact package tuple.");
        foreach (var profile in profiles)
        {
            var selected = DesktopCompatibilityProfile.SelectProfile(profile.ExecutablePath);
            check(selected == profile, "DesktopPackage: exact installed image selects its corresponding complete reviewed tuple.");
            DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath, profile.ExecutableSha256,
                profile.ArchiveSha256, profile.ElectronVersion);
            check(true, "DesktopPackage: each exact image executable archive and Electron tuple is accepted.");
            check(DesktopCompatibilityProfile.SelectProfile(profile.ExecutablePath.ToUpperInvariant()) == profile,
                "DesktopPackage: exact Windows image selection respects path case without changing its metadata tuple.");
            Reject("desktop_image_mismatch", () => DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath,
                profile.ExecutableSha256, profile.ArchiveSha256, "155.0.8059.81"), check,
                "DesktopPackage: a changed Electron version cannot use a reviewed package hash tuple.");
            Reject("desktop_image_mismatch", () => DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath,
                new string('0', 64), profile.ArchiveSha256, profile.ElectronVersion), check,
                "DesktopPackage: an unreviewed executable hash cannot use a reviewed package path.");
            Reject("desktop_image_mismatch", () => DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath,
                profile.ExecutableSha256, new string('0', 64), profile.ElectronVersion), check,
                "DesktopPackage: an unreviewed archive hash cannot use a reviewed package path.");
            var other = profiles.Single(candidate => candidate != profile);
            Reject("desktop_image_mismatch", () => DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath,
                other.ExecutableSha256, profile.ArchiveSha256, profile.ElectronVersion), check,
                "DesktopPackage: executable hashes from another admitted package are rejected.");
            Reject("desktop_image_mismatch", () => DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath,
                profile.ExecutableSha256, other.ArchiveSha256, profile.ElectronVersion), check,
                "DesktopPackage: archive hashes from another admitted package are rejected.");
            Reject("desktop_image_mismatch", () => DesktopCompatibilityProfile.ValidateMetadata(profile.ExecutablePath,
                other.ExecutableSha256, other.ArchiveSha256, profile.ElectronVersion), check,
                "DesktopPackage: an entire hash pair from another package cannot be reused under this package path.");
            foreach (string path in new[]
            {
                Path.Combine(Path.GetTempPath(), profile.PackageFullName, "app", "ChatGPT.exe"),
                Path.Combine(profile.ApplicationDirectory, "other", "ChatGPT.exe"),
                Path.Combine(profile.ApplicationDirectory, "Other.exe")
            })
                Reject("unsupported_desktop_version", () => DesktopCompatibilityProfile.ValidateMetadata(path,
                    profile.ExecutableSha256, profile.ArchiveSha256, profile.ElectronVersion), check,
                    "DesktopPackage: matching basename or metadata outside the exact image path grants no compatibility.");
        }
        string unknown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
            "OpenAI.Codex_26.1016.9999.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
        Reject("unsupported_desktop_version", () => DesktopCompatibilityProfile.ValidateMetadata(unknown,
            profiles[1].ExecutableSha256, profiles[1].ArchiveSha256, profiles[1].ElectronVersion), check,
            "DesktopPackage: an unreviewed package version is rejected even with an admitted executable and archive pair.");

        foreach (var profile in profiles)
        foreach (string file in new[] { profile.ExecutablePath, Path.Combine(profile.ApplicationDirectory, "resources", "app.asar") })
        {
            var visited = new List<string>();
            DesktopCompatibilityProfile.VerifyRegularPath(file, path => { visited.Add(path); return FileAttributes.Normal; });
            check(visited[0] == Path.GetFullPath(file) && visited[^1] == Path.GetPathRoot(Path.GetFullPath(file)),
                "DesktopPackage: regular-image validation checks every component through the volume root.");
            foreach (string linked in visited)
                Reject("linked_desktop_image", () => DesktopCompatibilityProfile.VerifyRegularPath(file,
                    path => path == linked ? FileAttributes.ReparsePoint | FileAttributes.Directory : FileAttributes.Normal), check,
                    "DesktopPackage: a reparse point on any executable or archive ancestor is rejected.");
        }
    }

    private static void Reject(string code, Action action, Action<bool, string> check, string name)
    {
        try { action(); }
        catch (DesktopAdapterException error) { check(error.Code == code, name); return; }
        throw new InvalidOperationException(name);
    }
}
