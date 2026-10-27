using AgentBroker.CodexDesktopAdapter;
using System.Text.Json;

// Default is connection-only. The explicit smoke mode sends one fixed benign prompt to the supplied exact target.
if (args.SequenceEqual(new[] { "--probe-connection-profile" }))
{
    try
    {
        var result = await DesktopConnectionProfileProbe.ObserveAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-connection-profile", connected = true,
            profile = result.Profile.ToString(), initialized = false, discoveryRequested = false,
            inputSent = false, deliveryEnabled = false }));
        return 0;
    }
    catch (Exception error) when (error is DesktopAdapterException or IOException or OperationCanceledException or UnauthorizedAccessException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-connection-profile", connected = false,
            errorCode = error is DesktopAdapterException known ? known.Code
                : error is OperationCanceledException ? "timeout_or_cancelled" : "connection_rejected",
            installedVersion = (error as DesktopAdapterException)?.InstalledVersion,
            initialized = false, discoveryRequested = false, inputSent = false, deliveryEnabled = false }));
        return 1;
    }
}
// Read-only: the connection-profile probe under the discovery observation policy. Sends nothing on the pipe.
if (args.SequenceEqual(new[] { "--probe-connection-trust" }))
{
    try
    {
        var result = await DesktopConnectionProfileProbe.ObserveTrustAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-connection-trust", connected = true,
            profile = result.Profile.ToString(), trust = result.Trust.ToString(), exactPinRefusal = result.ExactPinRefusalCode,
            installedVersion = result.InstalledVersion, initialized = false, discoveryRequested = false,
            inputSent = false, deliveryEnabled = false }));
        return 0;
    }
    catch (Exception error) when (error is DesktopAdapterException or IOException or OperationCanceledException or UnauthorizedAccessException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-connection-trust", connected = false,
            errorCode = error is DesktopAdapterException known ? known.Code
                : error is OperationCanceledException ? "timeout_or_cancelled" : "connection_rejected",
            installedVersion = (error as DesktopAdapterException)?.InstalledVersion,
            initialized = false, discoveryRequested = false, inputSent = false, deliveryEnabled = false }));
        return 1;
    }
}
if (args.Length == 1 && args[0] == "--find-ready-smoke-target")
{
    try
    {
        var search = await DesktopSmokeTargetFinder.FindAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-ready-smoke-finder", search.CandidateCount,
            search.UnreadableCount, search.Matches, search.Diagnostics, ambiguous = search.Matches.Count > 1,
            note = "Read-only live snapshot matching; no input sent or conversation text retained." }));
        return search.Matches.Count == 1 ? 0 : 1;
    }
    catch (Exception e) when (e is DesktopAdapterException or IOException or OperationCanceledException or UnauthorizedAccessException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-ready-smoke-finder",
            errorCode = e is DesktopAdapterException known ? known.Code : "read_only_search_rejected" }));
        return 1;
    }
}
if (args.Length == 1 && args[0] == "--verify-vscode-profile")
{
    try
    {
        VsCodeCompatibilityProfile.VerifyInstalled();
        int runningClients = VsCodeCompatibilityProfile.VerifyRunningClients();
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-vscode-public-profile", verified = true,
            runningClients, extensionVersion = VsCodeCompatibilityProfile.VerifiedVersions }));
        return 0;
    }
    catch (Exception e) when (e is DesktopAdapterException or IOException or UnauthorizedAccessException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-vscode-public-profile", verified = false,
            errorCode = e is DesktopAdapterException known ? known.Code : "public_profile_unreadable" }));
        return 1;
    }
}
if (args.Length == 2 && args[0] == "--probe-thread-metadata" && Guid.TryParseExact(args[1], "D", out var metadataTarget)
    && metadataTarget != Guid.Empty)
{
    bool codeProfileVerified = false;
    string? codeProfileErrorCode = null;
    try { VsCodeCompatibilityProfile.VerifyInstalled(); VsCodeCompatibilityProfile.VerifyRunningClients(); codeProfileVerified = true; }
    catch (Exception error) when (error is DesktopAdapterException or IOException or UnauthorizedAccessException)
    { codeProfileErrorCode = error is DesktopAdapterException known ? known.Code : "public_profile_unreadable"; }
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var metadata = await DesktopExactThreadMetadata.ObserveAsync(metadataTarget, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-exact-thread-metadata", codeProfileVerified,
            codeProfileErrorCode, metadata, inputSent = false, deliveryEnabled = false }));
        return 0;
    }
    catch (Exception error) when (error is DesktopAdapterException or IOException or OperationCanceledException or UnauthorizedAccessException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-exact-thread-metadata", codeProfileVerified,
            codeProfileErrorCode, errorCode = error is DesktopAdapterException known ? known.Code : "metadata_observation_rejected",
            inputSent = false, deliveryEnabled = false }));
        return 1;
    }
}
if (args.Length is 1 or 3 && args[0] == "--observe-directory"
    && (args.Length == 1 || args[1] == "--contains-thread" && Guid.TryParseExact(args[2], "D", out _)))
{
    try
    {
        var sessions = await DesktopDirectory.ObserveOpenSessionsAsync();
        Guid? known = args.Length == 3 ? Guid.Parse(args[2]) : null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            probe = "codex-open-session-directory", sessionCount = sessions.Count,
            containsKnownThread = known.HasValue ? sessions.Any(s => s.ConversationId == known.Value) : (bool?)null,
            unknownActivityCount = sessions.Count(s => s.Activity == DesktopSessionActivity.Unknown),
            compatibilityBaselineVersion = DesktopCompatibilityProfile.VerifiedVersions
        }));
        return 0;
    }
    catch (Exception e) when (e is DesktopAdapterException or IOException or OperationCanceledException or UnauthorizedAccessException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "codex-open-session-directory",
            errorCode = e is DesktopAdapterException known ? known.Code : "observation_rejected" }));
        return 1;
    }
}
bool smokePlain = args.Length == 2 && args[0] == "--smoke-authorized-test";
bool smokeExternal = args.Length == 2 && args[0] == "--smoke-external-peer-test";
bool probe = args.Length == 2 && args[0] == "--probe-thread";
if ((!probe && !smokePlain && !smokeExternal) || !Guid.TryParseExact(args[1], "D", out var threadId))
{
    Console.Error.WriteLine("Usage: AgentBroker.CodexDesktopAdapter --probe-thread <UUID> | --smoke-authorized-test <authorized test UUID> | --smoke-external-peer-test <authorized test UUID>");
    return 2;
}
string probeStage = "connect_and_initialize";
bool connectionEstablished = false;
try
{
    await using var adapter = await DesktopAdapter.ConnectAsync(new(EnableAuthorizedPlaintextDelivery: smokePlain,
        EnableExternalPeerDelivery: smokeExternal));
    connectionEstablished = true;
    probeStage = "exact_owner_discovery";
    var binding = await adapter.DiscoverAsync(threadId);
    if (smokePlain || smokeExternal)
    {
        if (binding is null) throw new DesktopAdapterException("owner_unavailable");
        Guid messageId = Guid.NewGuid();
        string marker = (smokeExternal ? "BROKER_EXTERNAL_READY_" : "BROKER_READY_") + messageId.ToString("N");
        string text = "This is the user-authorized AgentBroker wake smoke test in the dedicated test conversation. Reply "
            + marker + " and use no tools.";
        probeStage = "submission";
        var result = smokeExternal ? await adapter.SubmitExternalPeerAsync(binding, messageId, text)
            : await adapter.SubmitAuthorizedPlaintextAsync(binding, messageId, text);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            probe = smokeExternal ? "codex-desktop-external-peer-smoke" : "codex-desktop-authorized-smoke", ownerFound = true,
            submission = result.Outcome.ToString(), result.ErrorCode,
            result.Diagnostic,
            expectedReply = marker, compatibilityBaselineVersion = DesktopCompatibilityProfile.VerifiedVersions,
            note = "Native admission is not proof of a completed or visible reply. Check the dedicated GUI conversation."
        }));
        return result.Outcome == DesktopSubmissionOutcome.Accepted ? 0 : 1;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        probe = "codex-desktop-pinned-owner", connected = true, connectionEstablished, stage = "complete", ownerFound = binding is not null,
        supportsUntrustedAppInput = binding?.SupportsUntrustedAppInput == true,
        compatibilityBaselineVersion = DesktopCompatibilityProfile.VerifiedVersions
    }));
    return binding is null ? 1 : 0;
}
catch (Exception e) when (e is DesktopAdapterException or IOException or OperationCanceledException or UnauthorizedAccessException)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        probe = "codex-desktop-pinned-owner", connected = connectionEstablished, connectionEstablished, stage = probeStage,
        errorCode = e is DesktopAdapterException known ? known.Code : e is OperationCanceledException ? "timeout_or_cancelled" : "connection_rejected"
    }));
    return 1;
}
