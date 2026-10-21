using System.Diagnostics;
using System.Security.Cryptography;

namespace AgentBroker.CodexDesktopAdapter;

/// <summary>Alternative router process when the pinned VS Code extension created codex-ipc first.</summary>
public static class VsCodeRouterCompatibilityProfile
{
    public const string ProductVersion = "1.143.0";
    public const string ExecutableSha256 = "F13F0179B0E6EC0251E22ADBD123FE5F252FA227421B2128CF21379CF5941D58";
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
    {
        if (!IsExpectedImage(image) || hash != ExecutableSha256 || version != ProductVersion)
            throw new DesktopAdapterException("unsupported_vscode_router");
    }
}
