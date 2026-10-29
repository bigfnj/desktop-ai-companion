using AgentBroker.Pins;
using System.Diagnostics;
using System.Security.Cryptography;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>Alternative router process when the pinned VS Code extension created codex-ipc first.</summary>
public static class VsCodeRouterCompatibilityProfile
{
    internal sealed record RouterPin(string ProductVersion, string ExecutableSha256);

    // Each reviewed Code.exe is an independent version and hash pair, a "vscodeRouter" pin of the embedded document
    // tools/AgentBroker.Pins/agentbroker-pins.json. A hash from one pair never admits another pair's version.
    internal static IReadOnlyList<RouterPin> Pins => PinsOf(PinDocument.Current);

    /// <summary>The reviewed Code.exe versions, for status text. Empty when the pin document was refused.</summary>
    public static string VerifiedVersions => string.Join(", ", PinDocument.Current.VsCodeRouters.Select(pin => pin.ProductVersion));

    /// <summary>Status text only: the Code.exe version is newer than every verified router. Grants no trust.</summary>
    public static bool IsNewerThanEveryVerified(string? version)
        => PinDocument.IsNewer(version, PinDocument.Current.VsCodeRouters.Select(pin => pin.ProductVersion));

    // A refused document is its own refusal (pin_document_invalid), never the exact-pin mismatch
    // unsupported_vscode_router, so publisher evidence cannot stand in for it.
    internal static IReadOnlyList<RouterPin> PinsOf(PinDocument pins) => pins.Error is not null
        ? throw new DesktopAdapterException("pin_document_invalid")
        : pins.VsCodeRouters.Select(pin => new RouterPin(pin.ProductVersion, pin.ExecutableSha256)).ToArray();

    internal static string ExpectedImage => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Microsoft VS Code", "Code.exe");

    internal static bool IsExpectedImage(string image)
        => string.Equals(Path.GetFullPath(image), Path.GetFullPath(ExpectedImage), StringComparison.OrdinalIgnoreCase);

    internal static void VerifyPublicImage(string image) => VerifyPublicImage(image, PinDocument.Current,
        DesktopCompatibilityProfile.VerifyRegularPath, path => FileVersionInfo.GetVersionInfo(path).ProductVersion,
        path => { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); },
        VsCodeCompatibilityProfile.VerifyRunningClients);

    internal static void VerifyPublicImage(string image, PinDocument pins, Action<string> verifyRegularPath,
        Func<string, string?> readVersion, Func<string, string> hash, Func<int> verifyRunningClients)
    {
        if (!IsExpectedImage(image)) throw new DesktopAdapterException("unsupported_vscode_router");
        verifyRegularPath(image);
        // O-agentbroker-02: the version before the hash, so a Code.exe no pin names (every VS Code update until a re-pin)
        // is refused without reading its 238 MB first. PinsOf first, so pin_document_invalid still comes before any other
        // refusal. The version resource only ever REFUSES here: a version a pin names still has to match that pin's hash
        // in ValidateMetadata, exactly as before, so no trust decision moves.
        var routers = PinsOf(pins);
        string? version = readVersion(image);
        if (!routers.Any(pin => pin.ProductVersion == version))
            throw new DesktopAdapterException("unsupported_vscode_router", installedVersion: version);
        ValidateMetadata(image, hash(image), version, pins);
        // Public extension presence and actual running native engine must agree. A stale folder is insufficient.
        if (verifyRunningClients() == 0)
            throw new DesktopAdapterException("vscode_router_engine_unverified");
    }

    internal static void ValidateMetadata(string image, string hash, string? version)
        => ValidateMetadata(image, hash, version, PinDocument.Current);

    internal static void ValidateMetadata(string image, string hash, string? version, PinDocument pins)
    {
        var routers = PinsOf(pins);
        if (!IsExpectedImage(image) || !routers.Any(pin => hash == pin.ExecutableSha256 && version == pin.ProductVersion))
            throw new DesktopAdapterException("unsupported_vscode_router", version);
    }
}
