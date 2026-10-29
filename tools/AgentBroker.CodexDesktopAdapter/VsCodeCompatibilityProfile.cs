using AgentBroker.Pins;
using System.Security.Cryptography;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>
/// Pins the inspected public extension's coordination bridge and guarded owner logic.
/// Presence is a compatibility prerequisite, not proof of which extension owns an arbitrary IPC client ID.
/// </summary>
public static class VsCodeCompatibilityProfile
{
    internal sealed record ExtensionProfile(string Version, string DirectoryName, string NativeImageSha256,
        string ManifestFunctionalSha256, IReadOnlyList<KeyValuePair<string, string>> Images);

    // Each reviewed extension build is a "codexVsCode" pin of the embedded document
    // tools/AgentBroker.Pins/agentbroker-pins.json: its folder, engine hash, manifest functional hash and the hashed
    // bundles holding the coordination bridge and owner logic. With one pin every check below is exactly the
    // single-profile check it replaced.
    internal static IReadOnlyList<ExtensionProfile> Profiles => ProfilesOf(PinDocument.Current);

    /// <summary>The reviewed extension versions, for status text. Empty when the pin document was refused.</summary>
    public static string VerifiedVersions => string.Join(", ", PinDocument.Current.CodexVsCode.Select(pin => pin.Version));

    // A refused document is its own refusal (pin_document_invalid), never one of the exact-pin mismatch codes, so
    // update-tolerant publisher evidence cannot stand in for it.
    internal static IReadOnlyList<ExtensionProfile> ProfilesOf(PinDocument pins) => pins.Error is not null
        ? throw new DesktopAdapterException("pin_document_invalid")
        : pins.CodexVsCode.Select(pin => new ExtensionProfile(pin.Version, pin.DirectoryName, pin.NativeImageSha256,
            pin.ManifestFunctionalSha256, pin.Images)).ToArray();

    private static string ExtensionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");

    public static void VerifyInstalled()
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        VerifyInstalledIn(ExtensionsRoot, Profiles);
    }

    // Every reviewed build whose folder is installed must verify completely, and at least one must be installed.
    internal static void VerifyInstalledIn(string extensionsRoot, IReadOnlyList<ExtensionProfile> profiles)
    {
        bool installed = false;
        foreach (var profile in profiles)
        {
            string root = Path.Combine(extensionsRoot, profile.DirectoryName);
            if (!File.Exists(Path.Combine(root, "package.json"))) continue;
            VerifyInstalledAt(root, profile);
            installed = true;
        }
        if (!installed) throw new DesktopAdapterException("unsupported_vscode_profile");
    }

    internal static void VerifyInstalledAt(string root, ExtensionProfile profile)
    {
        string manifest = Path.Combine(root, "package.json");
        if (!File.Exists(manifest)) throw new DesktopAdapterException("unsupported_vscode_profile");
        DesktopCompatibilityProfile.VerifyRegularPath(manifest);
        using (var stream = File.OpenRead(manifest))
            VsCodeManifestProfile.Verify(VsCodeManifestProfile.ReadBounded(stream), profile.ManifestFunctionalSha256);
        foreach (var image in profile.Images)
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
        return VerifyRunningClients(WindowsClientProcesses.ReadCodeOwnedEngines(), VerifyInstalled);
    }

    internal static int VerifyRunningClients(IReadOnlyList<CodeOwnedEngine> engines, Action verifyInstalled)
    {
        if (engines.Count > 0)
        {
            try { verifyInstalled(); }
            // Status text only: no reviewed folder is installed, so name the version the running engine's own folder
            // declares (lane broker-pins). It is read from the path, never trusted, and the refusal is unchanged.
            catch (DesktopAdapterException error) when (error.Code == "unsupported_vscode_profile" && error.InstalledVersion is null)
            { throw new DesktopAdapterException(error.Code, NewestRunningVersion(engines)); }
        }
        return ValidateRunningClients(engines);
    }

    /// <summary>Status text only: the extension version is newer than every verified extension. Grants no trust.</summary>
    public static bool IsNewerThanEveryVerified(string? version)
        => PinDocument.IsNewer(version, PinDocument.Current.CodexVsCode.Select(pin => pin.Version));

    private static string? NewestRunningVersion(IReadOnlyList<CodeOwnedEngine> engines)
    {
        string root = Path.GetFullPath(ExtensionsRoot);
        return engines.Select(engine => PublisherEngineVersion(engine.Image, root)).OfType<string>()
            .OrderByDescending(System.Version.Parse).FirstOrDefault();
    }

    internal static int ValidateRunningClients(IReadOnlyList<CodeOwnedEngine> engines)
        => ValidateRunningClients(engines, engines.Count == 0 ? [] : Profiles);

    internal static int ValidateRunningClients(IReadOnlyList<CodeOwnedEngine> engines, IReadOnlyList<ExtensionProfile> profiles)
    {
        foreach (var engine in engines)
        {
            // The engine's own image path selects the reviewed build it belongs to; its hash must be that build's.
            var profile = profiles.FirstOrDefault(candidate => string.Equals(Path.GetFullPath(engine.Image),
                Path.GetFullPath(EngineImage(candidate)), StringComparison.OrdinalIgnoreCase));
            if (!engine.SameUser || engine.ParentCreationFileTime > engine.CreationFileTime
                || !string.Equals(Path.GetFileName(engine.ParentImage), "Code.exe", StringComparison.OrdinalIgnoreCase)
                || profile is null)
                // The version an OpenAI extension layout declares, for status text; null for any other path.
                throw new DesktopAdapterException("unsupported_running_vscode_profile",
                    PublisherEngineVersion(engine.Image, Path.GetFullPath(ExtensionsRoot)));
            DesktopCompatibilityProfile.VerifyRegularPath(engine.Image);
            using var stream = File.OpenRead(engine.Image);
            if (Convert.ToHexString(SHA256.HashData(stream)) != profile.NativeImageSha256)
                throw new DesktopAdapterException("running_vscode_image_mismatch");
        }
        return engines.Count;
    }

    private static string EngineImage(ExtensionProfile profile)
        => Path.Combine(ExtensionsRoot, profile.DirectoryName, "bin", "windows-x86_64", "codex.exe");

    /// <summary>
    /// Publisher counterpart of <see cref="VerifyRunningClients"/> for a build that is not exactly pinned: every direct
    /// Code-owned native engine must be a same-user child of <c>Code.exe</c> at an installed OpenAI extension layout,
    /// with a Windows-verified OpenAI signature. Extension JavaScript is unsigned, so this mode cannot verify it;
    /// the compatibility document records that gap. It never stands in for the exact profile on its own.
    /// </summary>
    // Discovery-only reuse of the PUBLISHER check (O-agentbroker-02, measured 2026-10-29): every discovery pass ran
    // Authenticode over the running engine, codex.exe at 354 MB on extension 26.1021.22027, about 1 GB of reads and 5% of
    // a core a minute on the owner's workstation. That check covers only the running engines' signed images, which
    // Windows will not let anyone write while their processes run, and a replaced file at the path runs only in a new
    // process, which is a new key. The exact-profile check is NOT reused: it also covers package.json and unsigned
    // extension JavaScript, which can change while the engine runs (Codex 01a1855c's review of 2c94f79). A failed fresh
    // check at submission forgets the reused success. Submission always calls the fresh checks, so model input keeps
    // full verification.
    private static readonly DiscoveryReuse PublisherDiscovery = new();

    public static void ForgetDiscoveryVerification() => PublisherDiscovery.Forget();

    public static int VerifyRunningClientsByPublisherForDiscovery()
    {
        if (!OperatingSystem.IsWindows()) throw new DesktopAdapterException("native_windows_required");
        var engines = WindowsClientProcesses.ReadCodeOwnedEngines();
        return PublisherDiscovery.Verify(engines, () => ValidateRunningClientsByPublisher(engines,
            image => WindowsPublisherImageTrust.Verify(image, WindowsPublisherImageTrust.OpenAIPublisher, false)));
    }

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

/// <summary>
/// Remembers the last engine process set that passed one verification, for discovery only. The key is every engine's
/// image path and OS creation time with its Code.exe parent's: a restarted, added or replaced engine is a different key and
/// is verified in full. A failure is never remembered, and neither is an empty set.
/// </summary>
internal sealed class DiscoveryReuse
{
    private readonly object gate = new();
    private string? verified;

    internal int Verify(IReadOnlyList<CodeOwnedEngine> engines, Func<int> verify)
    {
        string key = KeyOf(engines);
        lock (gate) { if (verified == key) return engines.Count; }
        int count = verify();
        lock (gate) verified = engines.Count > 0 ? key : null;
        return count;
    }

    internal void Forget() { lock (gate) verified = null; }

    internal static string KeyOf(IReadOnlyList<CodeOwnedEngine> engines) => string.Join("\n", engines
        .Select(engine => $"{engine.Image}|{engine.CreationFileTime}|{engine.ParentImage}|{engine.ParentCreationFileTime}|{engine.SameUser}")
        .Order(StringComparer.OrdinalIgnoreCase));
}
