using AgentBroker.ClaudeAdapter;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// Lane broker-pins: what the Native status line says about the embedded pin document. Each case is one group, so a
// mutation witness fails on the check it targets.
internal static class PinStatusChecks
{
    private static readonly string[] Cases = ["claude-refused-document", "codex-refused-document", "claude-newer", "codex-newer"];
    private const string Repin = " installed is newer than every verified build; a reviewed re-pin arrives as a module update.";

    // The pane's per-client Native line shows the first 160 characters of the adapter's status
    // (AgentBrokerModule.NativeLine), so the name, version and re-pin sentence must end within them.
    private static bool FitsNativeLine(AdapterDiscoveryDiagnostic diagnostic)
    {
        string status = diagnostic.ToStatusText();
        int end = status.IndexOf("a module update.", StringComparison.Ordinal);
        return end > 0 && end + "a module update.".Length <= 160;
    }

    internal static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Cases.Contains(only)) throw new ArgumentException("Unknown pin status case.", nameof(only));
        foreach (string name in Cases.Where(name => only is null || name == only))
            switch (name)
            {
                case "claude-refused-document":
                {
                    await using (var refused = new ClaudeNativeProvider(new NoProcesses(), pinDocumentError: () => "synthetic reason"))
                    {
                        await refused.DiscoverAsync(CancellationToken.None);
                        var diagnostic = refused.DiscoveryDiagnostics.SingleOrDefault(item => item.Code == "pin_document_invalid");
                        check(diagnostic is not null && diagnostic.Description.Contains("(synthetic reason)", StringComparison.Ordinal) &&
                            diagnostic.Description.Contains("no session can be woken", StringComparison.Ordinal),
                            "PinStatusChecks.cs: a refused pin document puts its reason and the no-wake consequence in the Claude status");
                    }
                    await using (var valid = new ClaudeNativeProvider(new NoProcesses(), pinDocumentError: () => null))
                    {
                        await valid.DiscoverAsync(CancellationToken.None);
                        check(valid.DiscoveryDiagnostics.Count == 0,
                            "PinStatusChecks.cs: WITNESS: a valid pin document adds nothing to the Claude status");
                    }
                    check(EngineProfile.PinDocumentError is null && EngineProfile.Verified.Count > 0,
                        "PinStatusChecks.cs: the Claude adapter's own embedded pin document validates and verifies builds");
                    break;
                }
                case "codex-refused-document":
                {
                    var described = CodexNativeAdapter.DescribeDiscoveryFailure(new DesktopAdapterException("pin_document_invalid"));
                    check(described.Code == "pin_document_invalid" &&
                        described.Description.StartsWith("AgentBroker's pin document was refused (", StringComparison.Ordinal) &&
                        described.Description.Contains("discovery and wake are off", StringComparison.Ordinal),
                        "PinStatusChecks.cs: a refused pin document is named in the Codex status with the consequence, not as a generic failure");
                    check(DesktopCompatibilityProfile.PinDocumentError is null && DesktopCompatibilityProfile.VerifiedVersions.Length > 0,
                        "PinStatusChecks.cs: the Codex adapter's own embedded pin document validates and verifies builds");
                    break;
                }
                case "claude-newer":
                {
                    var newer = ClaudeNativeProvider.CompatibilityDiagnostic("unsupported_public_version", "2.1.999.0");
                    check(newer.Code == "unsupported_public_version" && newer.Description.StartsWith("Claude Code 2.1.999.0" + Repin, StringComparison.Ordinal) &&
                        EngineProfile.Verified.All(profile => newer.Description.Contains(profile.Version, StringComparison.Ordinal)),
                        "PinStatusChecks.cs: a Claude build newer than every pin is named first with the re-pin sentence, the verified list after");
                    check(FitsNativeLine(newer), "PinStatusChecks.cs: the Claude name, version and re-pin sentence fit the pane's 160-character Native line");
                    string newest = EngineProfile.Verified.Select(profile => System.Version.Parse(profile.Version)).Max()!.ToString();
                    foreach (string notNewer in new[] { "2.1.249", newest, newest + ".0" })
                        check(!ClaudeNativeProvider.CompatibilityDiagnostic("unsupported_public_version", notNewer).Description.Contains("newer", StringComparison.Ordinal),
                            "PinStatusChecks.cs: WITNESS: a build that is not newer than every pin keeps the existing text: " + notNewer);
                    check(!ClaudeNativeProvider.CompatibilityDiagnostic("image_hash_mismatch", "2.1.999").Description.Contains("newer", StringComparison.Ordinal),
                        "PinStatusChecks.cs: only a version no pin names is the newer case, never a hash mismatch");
                    break;
                }
                case "codex-newer":
                {
                    foreach (var (code, version, client) in new[]
                    {
                        ("unsupported_desktop_version", "26.9999.1.0", "Codex desktop"), ("unsupported_vscode_router", "1.999.0", "VS Code"),
                        ("unsupported_vscode_profile", "26.9999.1", "Codex VS Code"), ("unsupported_running_vscode_profile", "26.9999.1", "Codex VS Code")
                    })
                    {
                        var described = CodexNativeAdapter.DescribeDiscoveryFailure(new DesktopAdapterException(code, version));
                        check(described.Code == code && described.Description.StartsWith(client + " " + version + Repin, StringComparison.Ordinal) && FitsNativeLine(described),
                            "PinStatusChecks.cs: a Codex client newer than every pin is named first with the re-pin sentence, within the Native line: " + code);
                    }
                    foreach (var (code, version) in new[]
                    {
                        ("unsupported_desktop_version", "26.1016.7000.0"), ("desktop_image_mismatch", "26.9999.1.0"),
                        ("unsupported_vscode_router", "1.143.0"), ("unsupported_running_vscode_profile", (string?)null)
                    })
                        check(!CodexNativeAdapter.DescribeDiscoveryFailure(new DesktopAdapterException(code, version)).Description.Contains("newer", StringComparison.Ordinal),
                            "PinStatusChecks.cs: WITNESS: a build that is not newer, an image mismatch or an unknown version keeps the existing text: " + code);
                    break;
                }
            }
    }

    private sealed class NoProcesses : IClaudeDiscoverySource
    {
        public IReadOnlyList<int> GetProcessIds() => [];
        public NativeSessionMetadata ReadMetadata(int processId) => throw new InvalidOperationException("No process is listed.");
        public IClaudeDiscoveryProcess OpenProcess(int processId) => throw new InvalidOperationException("No process is listed.");
        public void RejectReparseComponents(string image) => throw new InvalidOperationException("No process is listed.");
        public string? ReadProductVersion(string image) => throw new InvalidOperationException("No process is listed.");
        public string ReadImageSha256(string image) => throw new InvalidOperationException("No process is listed.");
    }
}
