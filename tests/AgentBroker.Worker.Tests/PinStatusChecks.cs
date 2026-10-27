using AgentBroker.ClaudeAdapter;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Worker.Adapters;

// Lane broker-pins: what the Native status line says about the embedded pin document. Each case is one group, so a
// mutation witness fails on the check it targets.
internal static class PinStatusChecks
{
    private static readonly string[] Cases = ["claude-refused-document", "codex-refused-document"];

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
