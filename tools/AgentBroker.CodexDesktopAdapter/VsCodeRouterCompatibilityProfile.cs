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

    internal static void VerifyPublicImage(string image)
    {
        if (!IsExpectedImage(image)) throw new DesktopAdapterException("unsupported_vscode_router");
        DesktopCompatibilityProfile.VerifyRegularPath(image);
        using var stream = File.OpenRead(image);
        ValidateMetadata(image, Convert.ToHexString(SHA256.HashData(stream)), FileVersionInfo.GetVersionInfo(image).ProductVersion);
        // Public extension presence and actual running native engine must agree. A stale folder is insufficient.
        if (VsCodeCompatibilityProfile.VerifyRunningClients() == 0)
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
