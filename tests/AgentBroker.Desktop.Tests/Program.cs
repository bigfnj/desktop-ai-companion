using AgentBroker.CodexDesktopAdapter;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

int passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    passed++;
}
async Task Refuses(string code, Func<Task> action)
{
    try { await action(); throw new InvalidOperationException("Expected " + code); }
    catch (DesktopAdapterException e) { Check(e.Code == code, "Expected " + code + ", got " + e.Code); }
}
async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (!condition()) await Task.Delay(5, timeout.Token);
}
if (args.SequenceEqual(new[] { "--desktop-package-profile-only" }))
{
    try
    {
        DesktopPackageProfileChecks.Run(Check);
        Console.WriteLine($"AgentBroker desktop package profile checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.SequenceEqual(new[] { "--vscode-manifest-only" })
    || args.Length == 2 && args[0] == "--vscode-manifest-case")
{
    try
    {
        VsCodeManifestChecks.Run(Check, args.Length == 2 ? args[1] : null);
        Console.WriteLine($"AgentBroker VS Code manifest checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.SequenceEqual(new[] { "--connection-profile-only" })
    || args.Length == 2 && args[0] == "--connection-profile-case")
{
    try
    {
        await ConnectionProfileChecks.RunAsync(Check, args.Length == 2 ? args[1] : null);
        Console.WriteLine($"AgentBroker connection profile checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.SequenceEqual(new[] { "--pin-diagnostics-only" }))
{
    try
    {
        CodexPinMetadataChecks.Run(Check);
        Console.WriteLine($"AgentBroker desktop pin diagnostic checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.SequenceEqual(new[] { "--discovery-timeouts-only" })
    || args.Length == 2 && args[0] == "--discovery-timeout-case")
{
    try
    {
        await DiscoveryTimeoutChecks.RunAsync(Check, args.Length == 2 ? args[1] : null);
        Console.WriteLine($"AgentBroker desktop discovery timeout checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.SequenceEqual(new[] { "--codex-observation-only" }))
{
    try
    {
        await CodexObservationChecks.RunAsync(Check);
        Console.WriteLine($"AgentBroker Codex observation checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
// Read-only: one WinVerifyTrust call on an explicitly named installed image. No pipe, process or vendor input.
if (args.Length == 4 && args[0] == "--codex-public-image-trust" && args[3] is "protected" or "unprotected")
{
    try
    {
        CodexObservationChecks.RunPublicImageCheck(Check, args[1], args[2], args[3] == "protected");
        Console.WriteLine($"AgentBroker Codex public-image checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.Length == 2 && args[0] == "--codex-publisher-router")
{
    try
    {
        CodexObservationChecks.RunPublisherRouterCheck(Check, args[1]);
        Console.WriteLine($"AgentBroker Codex publisher router checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error is DesktopAdapterException known ? "DesktopAdapterException: " + known.Code : error.ToString());
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
// Lane broker-pins: the embedded pin document's loader, strict schema and fail-closed consumers.
if (args.SequenceEqual(new[] { "--pin-document-only" }) || args.Length == 2 && args[0] == "--pin-document-case")
{
    try
    {
        PinDocumentChecks.Run(Check, args.Length == 2 ? args[1] : null);
        Console.WriteLine($"AgentBroker pin document checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
if (args.SequenceEqual(new[] { "--exact-thread-metadata-only" }))
{
    try
    {
        await ExactThreadMetadataChecks.RunAsync(Check);
        Console.WriteLine($"AgentBroker exact thread metadata checks passed: {passed}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine("RESULT=FAIL");
        return 1;
    }
}
var thread = Guid.NewGuid();
var options = new DesktopAdapterOptions(EnableAuthorizedPlaintextDelivery: true,
    RequestTimeout: TimeSpan.FromMilliseconds(250));

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
{
    var binding = await adapter.DiscoverAsync(thread);
    Check(binding is not null && binding.ConversationId == thread, "Discovery binds exact conversation.");
    Check(binding!.OwnerClientId == connection.OwnerId, "Discovery binds exact owner client.");
    Check(binding.NativeSessionIncarnation == (binding with { Incarnation = Guid.NewGuid() }).NativeSessionIncarnation,
        "Broker native incarnation survives observer/delivery connection changes.");
    Check(binding.NativeSessionIncarnation != (binding with { OwnerClientId = Guid.NewGuid() }).NativeSessionIncarnation
        && binding.NativeSessionIncarnation != (binding with { ServerCreationFileTime = binding.ServerCreationFileTime + 1 }).NativeSessionIncarnation,
        "Native owner client/process restart changes broker incarnation.");
    var messageId = Guid.NewGuid();
    var sent = await adapter.SubmitAuthorizedPlaintextAsync(binding, messageId, "Synthetic benign input.");
    Check(sent.Outcome == DesktopSubmissionOutcome.Accepted && sent.TurnId == "synthetic-turn", "Accepted nested native result.");
    Check(connection.Requests.Count(r => Method(r) == "thread-follower-start-turn") == 1, "One owner submit.");
    var submit = connection.Requests.Single(r => Method(r) == "thread-follower-start-turn");
    Check((int)submit["version"]! == 2 && submit["hostId"] is null, "Native local follower protocol version.");
    Check((string?)submit["targetClientId"] == connection.OwnerId.ToString("D"), "Request pins discovered owner.");
    var parameters = (JsonObject)submit["params"]!;
    var start = (JsonObject)parameters["turnStart"]!;
    var request = (JsonObject)start["request"]!;
    var context = (JsonObject)start["context"]!;
    Check(parameters.Count == 2 && request.Count == 3 && context.Count == 1, "No arbitrary permission/model/context fields.");
    Check((string?)request["threadId"] == thread.ToString("D") && (string?)request["clientUserMessageId"] == messageId.ToString("D"), "Exact thread and dedup ID.");
    Check((bool)context["inheritThreadSettings"]! && !context.ContainsKey("useUserMessageAdmission"), "Inherits settings and leaves native local/cloud admission selection unchanged.");
    Check(connection.DiscoveryCount == 2, "Owner rediscovered immediately before send.");
    var duplicate = await adapter.SubmitAuthorizedPlaintextAsync(binding, messageId, "Synthetic benign input.");
    Check(duplicate == sent && connection.SubmissionCount == 1, "Duplicate returns same receipt without native resend.");
    await Refuses("message_id_collision", () => adapter.SubmitAuthorizedPlaintextAsync(binding, messageId, "Different input."));
    await Refuses("stale_binding", () => adapter.SubmitAuthorizedPlaintextAsync(binding with { Incarnation = Guid.NewGuid() }, Guid.NewGuid(), "x"));
    await Refuses("invalid_message", () => adapter.SubmitAuthorizedPlaintextAsync(binding, Guid.Empty, "x"));
    await Refuses("invalid_message", () => adapter.SubmitAuthorizedPlaintextAsync(binding, Guid.NewGuid(), new string('x', 20000)));
    Check(connection.Requests.All(r => Method(r) is "initialize" or "thread-owner-discovery" or "thread-follower-start-turn"), "No resume, start, queue or approval RPCs.");
}

await using (var connection = new FakeConnection { IncludeNoise = true })
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
{
    Check(await adapter.DiscoverAsync(thread) is not null, "Unrelated broadcasts ignored and incoming discovery declined.");
    Check(connection.DeclinedDiscovery == 2, "No handler claims privileged vendor ownership.");
}

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new()))
{
    var binding = await adapter.DiscoverAsync(thread);
    await Refuses("plaintext_delivery_disabled", () => adapter.SubmitAuthorizedPlaintextAsync(binding!, Guid.NewGuid(), "x"));
    Check(connection.SubmissionCount == 0, "Read-only defaults cannot submit.");
}

await using (var connection = new FakeConnection { NoOwner = true })
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
    Check(await adapter.DiscoverAsync(thread) is null, "Absent owner does not resume or launch another owner.");

await using (var connection = new FakeConnection { Capability = false })
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
    await Refuses("owner_capability_mismatch", () => adapter.DiscoverAsync(thread));

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
{
    var binding = await adapter.DiscoverAsync(thread);
    connection.NoOwner = true;
    var result = await adapter.SubmitAuthorizedPlaintextAsync(binding!, Guid.NewGuid(), "x");
    Check(result.Outcome == DesktopSubmissionOutcome.Rejected && connection.SubmissionCount == 0, "Ownership loss prevents send.");
}

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
{
    var binding = await adapter.DiscoverAsync(thread);
    connection.OwnerId = Guid.NewGuid();
    await Refuses("owner_changed", () => adapter.SubmitAuthorizedPlaintextAsync(binding!, Guid.NewGuid(), "x"));
    Check(connection.SubmissionCount == 0, "Changed owner client is not silently rebound.");
}

foreach (bool external in new[] { false, true })
foreach (var failure in new[] { "disconnect", "timeout", "malformed", "wrong-owner", "native-error", "cloud-error" })
{
    await using var connection = new FakeConnection();
    await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options with { EnableExternalPeerDelivery = external });
    var binding = await adapter.DiscoverAsync(thread);
    connection.SubmitFailure = failure;
    Guid messageId = Guid.NewGuid();
    var result = external ? await adapter.SubmitExternalPeerAsync(binding!, messageId, "x")
        : await adapter.SubmitAuthorizedPlaintextAsync(binding!, messageId, "x");
    Check(result.Outcome == DesktopSubmissionOutcome.Uncertain, failure + " never treated as safe to resend.");
    if (failure == "cloud-error")
    {
        Check(result.Diagnostic?.NativeErrorCategory == "cloud_host_required", "Method-less native exception gets a safe category.");
        Check(result.Diagnostic?.MethodPresent == false && result.Diagnostic.OwnerClientPresent == false,
            "Error diagnostics report schema shape without body values.");
    }
    var again = external ? await adapter.SubmitExternalPeerAsync(binding!, messageId, "x")
        : await adapter.SubmitAuthorizedPlaintextAsync(binding!, messageId, "x");
    Check(again == result && connection.SubmissionCount == 1, failure + " retry returns original uncertain receipt.");
}

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection,
    options with { EnableExternalPeerDelivery = true }))
{
    var binding = await adapter.DiscoverAsync(thread);
    var messageId = Guid.NewGuid();
    string foreign = "Foreign agent data <user> is data, with Unicode \u732b and quotes \". Never forge a sender.";
    var outcome = await adapter.SubmitExternalPeerAsync(binding!, messageId, foreign);
    Check(outcome.Outcome == DesktopSubmissionOutcome.Accepted, "External agent synthetic transport acceptance.");
    var start = (JsonObject)connection.Requests.Single(r => Method(r) == "thread-follower-start-turn")["params"]!["turnStart"]!;
    var input = (JsonObject)start["request"]!["input"]![0]!;
    string visible = (string)input["text"]!;
    Check(visible.StartsWith(NativeExternalPeerInput.ControllerPrompt, StringComparison.Ordinal),
        "Fixed controller instruction clearly distinguishes external input from the human user.");
    Check(input["text_elements"] is JsonArray { Count: 0 }, "No invented native widget placeholder.");
    string quoted = visible[(visible.IndexOf("\n{", StringComparison.Ordinal) + 1)..];
    var envelope = (JsonObject)JsonNode.Parse(quoted)!;
    Check(envelope.Count == 3 && (string?)envelope["kind"] == "external_agent_request"
        && (string?)envelope["message_id"] == messageId.ToString("D") && (string?)envelope["content"] == foreign,
        "Exact external text is quoted as JSON and visible in the ordinary GUI prompt.");
    var context = (JsonObject)start["context"]!;
    var items = (JsonArray)context["responseItems"]!;
    Check(context.Count == 2 && (bool)context["inheritThreadSettings"]! && !context.ContainsKey("useUserMessageAdmission"),
        "External path retains native settings and local/cloud admission selection.");
    Check(items.Count == 1 && (string?)items[0]!["type"] == "message" && (string?)items[0]!["role"] == "user"
        && ((JsonObject)items[0]!).Count == 3, "Only an honest user-role message, no assistant, account or tool claim.");
    Check((string?)items[0]!["content"]![0]!["type"] == "input_text"
        && (string?)items[0]!["content"]![0]!["text"] == visible, "Guard context retains the same quoted external text.");
    var duplicate = await adapter.SubmitExternalPeerAsync(binding!, messageId, foreign);
    Check(duplicate == outcome && connection.SubmissionCount == 1, "External duplicate returns original receipt without reinjection.");
    await Refuses("message_id_collision", () => adapter.SubmitAuthorizedPlaintextAsync(binding!, messageId, foreign));
    await Refuses("message_frame_too_large", () => adapter.SubmitExternalPeerAsync(binding!, Guid.NewGuid(), new string('\u732b', 5000)));
}

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
{
    var binding = await adapter.DiscoverAsync(thread);
    await Refuses("external_peer_delivery_disabled", () => adapter.SubmitExternalPeerAsync(binding!, Guid.NewGuid(), "x"));
    Check(connection.SubmissionCount == 0 && !adapter.NativeIdleGuard, "Read-only defaults cannot submit foreign content.");
}

foreach (var failure in new[] { "busy-wrong-owner", "busy-malformed-owner" })
{
    await using var connection = new FakeConnection { SubmitFailure = failure };
    await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection,
        options with { EnableExternalPeerDelivery = true });
    var binding = await adapter.DiscoverAsync(thread);
    Guid id = Guid.NewGuid();
    var first = await adapter.SubmitExternalPeerAsync(binding!, id, "x");
    var retry = await adapter.SubmitExternalPeerAsync(binding!, id, "x");
    Check(first.Outcome == DesktopSubmissionOutcome.Uncertain && retry == first && connection.SubmissionCount == 1,
        failure + " preserves uncertain receipt and never permits replay.");
}

foreach (bool guarded in new[] { false, true })
{
    await using var connection = new FakeConnection();
    await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection,
        options with { EnableExternalPeerDelivery = guarded });
    var binding = await adapter.DiscoverAsync(thread);
    connection.SubmitFailure = "busy-context";
    Guid id = Guid.NewGuid();
    var deferred = guarded ? await adapter.SubmitExternalPeerAsync(binding!, id, "x")
        : await adapter.SubmitAuthorizedPlaintextAsync(binding!, id, "x");
    Check(deferred.Outcome == (guarded ? DesktopSubmissionOutcome.DeferredNotSubmitted : DesktopSubmissionOutcome.Uncertain),
        "Only the typed external path's exact pre-injection busy rejection permits retry.");
    Check(adapter.NativeIdleGuard == guarded, "Owner idle guard is distinct from observed activity and engine-wide atomicity.");
    connection.SubmitFailure = null;
    var retry = guarded ? await adapter.SubmitExternalPeerAsync(binding!, id, "x")
        : await adapter.SubmitAuthorizedPlaintextAsync(binding!, id, "x");
    Check(guarded ? retry.Outcome == DesktopSubmissionOutcome.Accepted && connection.SubmissionCount == 2
        : retry.Outcome == DesktopSubmissionOutcome.Uncertain && connection.SubmissionCount == 1,
        "Only proven unsubmitted receipt is evicted for a later same-ID retry.");
}

// Refusal transience (2026-10-29): an owner missing at the pre-write rediscovery is usually a view reconnecting, and no
// turn (model-input) frame was written, only discovery's control frame, so the submission defers and the same message ID
// is delivered once the owner is back.
{
    await using var connection = new FakeConnection();
    await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options with { EnableExternalPeerDelivery = true });
    var binding = await adapter.DiscoverAsync(thread);
    connection.NoOwner = true;
    Guid id = Guid.NewGuid();
    var missing = await adapter.SubmitExternalPeerAsync(binding!, id, "x");
    Check(missing.Outcome == DesktopSubmissionOutcome.DeferredNotSubmitted && missing.ErrorCode == "owner_unavailable" && connection.SubmissionCount == 0,
        "TRANSIENT_OWNER_DEFERS: an owner missing before any turn frame is written defers instead of refusing, with nothing submitted");
    connection.NoOwner = false;
    var retry = await adapter.SubmitExternalPeerAsync(binding!, id, "x");
    Check(retry.Outcome == DesktopSubmissionOutcome.Accepted && connection.SubmissionCount == 1,
        "TRANSIENT_OWNER_RETRY: the same message ID is delivered once the owner is back");
}

var metadataClock = new MetadataClock();
var index = new DesktopDirectoryIndex(metadataClock);
var source = Guid.NewGuid();
JsonObject Broadcast(Guid id, string method = "thread-stream-following-changed", int revision = 1) => new()
{
    ["type"] = "broadcast", ["method"] = method, ["version"] = revision,
    ["sourceClientId"] = source.ToString("D"),
    ["params"] = new JsonObject { ["hostId"] = "local", ["conversationId"] = id.ToString("D"), ["following"] = true }
};
index.Capture(Broadcast(thread), Guid.NewGuid());
Check(index.Snapshot().Single().Activity == DesktopSessionActivity.Unknown, "Following does not imply idle.");
var wrongVersion = Broadcast(Guid.NewGuid(), revision: 999);
index.Capture(wrongVersion, Guid.NewGuid());
Check(index.Snapshot().Count == 1, "Unknown broadcast versions are ignored.");
var runtime = Broadcast(thread, "thread-stream-state-changed", 11);
runtime["params"]!["change"] = new JsonObject
{
    ["type"] = "snapshot", ["conversationState"] = new JsonObject
    {
        ["threadRuntimeStatus"] = new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray("waitingOnApproval") },
        ["turns"] = new JsonArray(new JsonObject { ["text"] = "synthetic body must not be retained" })
    }
};
index.Capture(runtime, Guid.NewGuid());
Check(index.Snapshot().Single().Activity == DesktopSessionActivity.ApprovalRequired, "Native approval status is never idle.");
Check(!JsonSerializer.Serialize(index.Snapshot()).Contains("synthetic body", StringComparison.Ordinal), "Directory retains metadata only.");
runtime["params"]!["change"]!["conversationState"]!["threadRuntimeStatus"] = new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray() };
index.Capture(runtime, Guid.NewGuid());
Check(index.Snapshot().Single().Activity == DesktopSessionActivity.Busy, "Native active status is busy.");
runtime["params"]!["change"]!["conversationState"]!["threadRuntimeStatus"] = new JsonObject { ["type"] = "idle" };
index.Capture(runtime, Guid.NewGuid());
Check(index.Snapshot().Single().Activity == DesktopSessionActivity.Idle, "Only explicit native idle is idle.");
foreach (bool initiallyBusy in new[] { false, true })
{
    var freshnessClock = new MetadataClock();
    var freshnessIndex = new DesktopDirectoryIndex(freshnessClock);
    Guid observerId = Guid.NewGuid();
    var initialState = (JsonObject)runtime.DeepClone();
    initialState["params"]!["change"]!["conversationState"]!["threadRuntimeStatus"] = initiallyBusy
        ? new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray() }
        : new JsonObject { ["type"] = "idle" };
    freshnessIndex.Capture(initialState, observerId);
    var activityAt = freshnessClock.GetUtcNow();
    for (int tick = 0; tick < 3; tick++)
    {
        freshnessClock.Advance(TimeSpan.FromSeconds(30));
        freshnessIndex.Capture(Broadcast(thread), observerId);
    }
    Check(freshnessIndex.Snapshot().Single().Activity == (initiallyBusy ? DesktopSessionActivity.Busy : DesktopSessionActivity.Idle),
        "Following refreshes preserve a still-fresh explicit " + (initiallyBusy ? "busy" : "idle") + " observation.");
    for (int tick = 0; tick < 2; tick++)
    {
        freshnessClock.Advance(TimeSpan.FromSeconds(30));
        freshnessIndex.Capture(Broadcast(thread), observerId);
    }
    var freshPresence = freshnessIndex.Snapshot().Single();
    Check(freshPresence.Activity == DesktopSessionActivity.Unknown && freshPresence.ActivityObservedAt == activityAt &&
        freshPresence.LastSeenAt == freshnessClock.GetUtcNow(),
        "Following refreshes keep presence live beyond two minutes without renewing stale " + (initiallyBusy ? "busy" : "idle") + " activity.");
    freshnessIndex.Capture(runtime, observerId);
    Check(freshnessIndex.Snapshot().Single() is { Activity: DesktopSessionActivity.Idle } refreshed &&
        refreshed.ActivityObservedAt == freshnessClock.GetUtcNow(),
        "An explicit new idle state restores authoritative activity after presence-only refreshes.");
}
metadataClock.Advance(TimeSpan.FromMinutes(3));
Check(index.Snapshot().Count == 0, "Directory metadata expires after two minutes.");
for (int i = 0; i < 300; i++) index.Capture(Broadcast(Guid.NewGuid()), Guid.NewGuid());
Check(index.Snapshot().Count == DesktopDirectoryIndex.MaximumSessions, "Directory holds at most 256 IDs.");
var ownIndex = new DesktopDirectoryIndex();
ownIndex.Capture(Broadcast(thread, "thread-stream-following-status-requested"), source);
Check(ownIndex.Snapshot().Count == 0, "The observer's own metadata refresh cannot keep a vanished owner alive.");

await using (var connection = new FakeConnection { DirectoryThread = thread })
{
    var rows = await DesktopDirectory.ObserveForTestAsync(connection, TimeSpan.FromMilliseconds(50));
    Check(rows.Count == 1 && rows[0].ConversationId == thread && rows[0].Activity == DesktopSessionActivity.Unknown,
        "Read-only observer captures existing followed thread and defaults unknown.");
    Check(connection.Requests.Count == 1 && Method(connection.Requests[0]) == "initialize", "Observer sends no subscription, history, list or model request.");
}

await using (var connection = new FakeConnection { DirectoryThread = thread })
await using (var observer = await DesktopDirectoryObserver.ConnectForTestAsync(connection, TimeSpan.FromMilliseconds(80)))
{
    Check(observer.Snapshot().Single().Activity == DesktopSessionActivity.Unknown,
        "Persistent metadata observation leaves unobserved activity unknown.");
    await Until(() => connection.Requests.Any(r => Method(r) == "thread-stream-following-status-requested"));
    for (int i = 0; i < 20; i++) Check(observer.Snapshot().Single().ConversationId == thread, "Persistent snapshots retain the same verified observer.");
    Check(connection.Requests.Count(r => Method(r) == "initialize") == 1, "Repeated snapshots reuse one initialized cryptographically verified connection.");
    Check(connection.Requests.All(r => Method(r) is "initialize" or "thread-stream-following-status-requested")
        && connection.Requests.Where(r => Method(r) != "initialize").All(r =>
            (int?)r["version"] == 1 && (string?)r["params"]?["hostId"] == "local"
            && (string?)r["params"]?["conversationId"] == thread.ToString("D")),
        "Heartbeat sends only versioned known-ID metadata, without history subscriptions or model input.");
    connection.IdentityChanged = true;
    await Refuses("server_incarnation_changed", () => Task.Run(() => observer.Snapshot()));
}

foreach (bool protocolFailure in new[] { false, true })
{
    await using var connection = new FakeConnection { DirectoryThread = thread };
    await using var observer = await DesktopDirectoryObserver.ConnectForTestAsync(connection, TimeSpan.FromHours(1));
    if (protocolFailure) connection.Inject(new() { ["type"] = "response" });
    else connection.Disconnect();
    await Until(() => connection.Disposed);
    await Refuses("directory_observer_disconnected", () => Task.Run(() => observer.Snapshot()));
    Check(connection.SubmissionCount == 0, "Observer transport and protocol faults never submit input.");
}

await using (var connection = new FakeConnection { DirectoryThreads = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToArray() })
{
    var observer = await DesktopDirectoryObserver.ConnectForTestAsync(connection, TimeSpan.FromHours(1));
    Check(observer.Snapshot().Count == DesktopDirectoryIndex.MaximumSessions, "Persistent observer retains at most 256 session metadata entries.");
    await observer.DisposeAsync();
    await observer.DisposeAsync();
    Check(connection.Disposed, "Disposal closes the pending read connection and is repeatable.");
    try { observer.Snapshot(); throw new InvalidOperationException("Disposed observer accepted snapshot."); }
    catch (ObjectDisposedException) { Check(true, "Disposed observer never advertises stale metadata."); }
}

await using (var connection = new FakeConnection { DirectoryThread = thread })
await using (var observer = await DesktopDirectoryObserver.ConnectForTestAsync(connection, TimeSpan.FromHours(1)))
    Check(observer.Snapshot().Single().ConversationId == thread && connection.Requests.Count == 1,
        "A new observer reconnect initializes a fresh verified connection after prior faults.");

await using (var connection = new FakeConnection())
await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, options))
{
    var binding = await adapter.DiscoverAsync(thread);
    connection.IdentityChanged = true;
    await Refuses("server_incarnation_changed", () => adapter.SubmitAuthorizedPlaintextAsync(binding!, Guid.NewGuid(), "x"));
    Check(connection.SubmissionCount == 0, "Process identity change fails before write.");
}

await using (var stream = new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0x7f }))
    await Refuses("invalid_frame_size", async () => { await DesktopFrames.ReadAsync(stream, default); });
await using (var stream = new MemoryStream(new byte[] { 1, 0, 0, 0, (byte)'{' }))
    await Refuses("invalid_frame", async () => { await DesktopFrames.ReadAsync(stream, default); });
Check(DesktopFrames.Text(JsonValue.Create("bad shape"), "id") is null, "Scalar cannot masquerade as object.");
Check(!DesktopFrames.IsTrue(new JsonObject { ["supportsUntrustedAppInput"] = "true" }, "supportsUntrustedAppInput"), "Capability requires boolean true.");
Check(VsCodeCompatibilityProfile.ValidateRunningClients([]) == 0, "No running VS Code engine does not require an installed extension.");
VsCodeRouterCompatibilityProfile.ValidateMetadata(VsCodeRouterCompatibilityProfile.ExpectedImage,
    VsCodeRouterCompatibilityProfile.Pins[0].ExecutableSha256, VsCodeRouterCompatibilityProfile.Pins[0].ProductVersion);
Check(true, "Exact supported Code router metadata is accepted independently of desktop package metadata.");
foreach (var metadata in new[]
{
    (Image: "synthetic-Code.exe", Hash: VsCodeRouterCompatibilityProfile.Pins[0].ExecutableSha256, Version: VsCodeRouterCompatibilityProfile.Pins[0].ProductVersion),
    (Image: VsCodeRouterCompatibilityProfile.ExpectedImage, Hash: new string('0', 64), Version: VsCodeRouterCompatibilityProfile.Pins[0].ProductVersion),
    (Image: VsCodeRouterCompatibilityProfile.ExpectedImage, Hash: VsCodeRouterCompatibilityProfile.Pins[0].ExecutableSha256, Version: "unsupported")
})
    await Refuses("unsupported_vscode_router", () => Task.Run(() => VsCodeRouterCompatibilityProfile.ValidateMetadata(metadata.Image, metadata.Hash, metadata.Version)));
foreach (var engine in new[]
{
    new CodeOwnedEngine("synthetic-unsupported-extension.exe", 10, "Code.exe", 5, true),
    new CodeOwnedEngine("synthetic-unsupported-extension.exe", 10, "Code.exe", 5, false),
    new CodeOwnedEngine("synthetic-unsupported-extension.exe", 10, "Code.exe", 20, true),
    new CodeOwnedEngine("synthetic-unsupported-extension.exe", 10, "other-parent.exe", 5, true)
})
    await Refuses("unsupported_running_vscode_profile", () => Task.Run(() => VsCodeCompatibilityProfile.ValidateRunningClients([engine])));

JsonObject ReadyState(string prompt = DesktopSmokeTargetFinder.ReadinessPrompt, string answer = "READY", string status = "completed") => new()
{
    ["threadRuntimeStatus"] = new JsonObject { ["type"] = "idle" },
    ["turns"] = new JsonArray(new JsonObject
    {
        ["turnId"] = "synthetic-ready-turn", ["status"] = status,
        ["params"] = new JsonObject { ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }) },
        ["items"] = new JsonArray(new JsonObject { ["type"] = "agentMessage", ["text"] = answer })
    })
};
Check(DesktopSmokeTargetFinder.MatchesReadinessExchange(ReadyState()), "Dedicated finder matches only the fixed completed readiness exchange.");
Check(DesktopSmokeTargetFinder.MatchesReadinessExchange(ReadyState(prompt: DesktopSmokeTargetFinder.ShortReadinessPrompt)),
    "Finder recognizes the exact shorter readiness prompt confirmed in the person's screenshot.");
Check(!DesktopSmokeTargetFinder.MatchesReadinessExchange(ReadyState(prompt: "Unrelated ordinary request")), "Unrelated prompts are not test targets.");
Check(!DesktopSmokeTargetFinder.MatchesReadinessExchange(ReadyState(answer: "Unrelated answer")), "Prompt without READY answer is not a ready test target.");
Check(!DesktopSmokeTargetFinder.MatchesReadinessExchange(ReadyState(status: "inProgress")), "An active readiness turn is not selected as ready.");
foreach (bool matching in new[] { false, true })
{
    await using var connection = new FakeConnection { ReadySmokeState = ReadyState(prompt: matching ? DesktopSmokeTargetFinder.ReadinessPrompt : "other") };
    await using var adapter = await DesktopAdapter.ConnectForTestAsync(connection, new());
    var binding = await adapter.DiscoverAsync(thread);
    var result = await adapter.FindReadySmokeSnapshotAsync(binding!, default);
    Check(matching ? result?.ConversationId == thread && result.Activity == DesktopSessionActivity.Idle : result is null,
        "Read-only snapshot matching returns only the explicitly prepared test conversation.");
    Check(connection.SubmissionCount == 0 && connection.Requests.Count(r => Method(r) == "thread-stream-following-changed") == 1,
        "Dedicated finder sends a bounded following announcement and no model input.");
}

if (!args.SequenceEqual(new[] { "--existing-tests-only" })) CodexPinMetadataChecks.Run(Check);
if (!args.SequenceEqual(new[] { "--existing-tests-only" }) && !args.SequenceEqual(new[] { "--skip-discovery-timeouts" }))
    await DiscoveryTimeoutChecks.RunAsync(Check);
if (!args.SequenceEqual(new[] { "--existing-tests-only" })) await ExactThreadMetadataChecks.RunAsync(Check);
if (!args.SequenceEqual(new[] { "--existing-tests-only" })) await ConnectionProfileChecks.RunAsync(Check);
if (!args.SequenceEqual(new[] { "--existing-tests-only" })) VsCodeManifestChecks.Run(Check);
if (!args.SequenceEqual(new[] { "--existing-tests-only" })) DesktopPackageProfileChecks.Run(Check);
// Codex observation-only discovery and the opt-in update-tolerant delivery gate (lane broker-b).
if (!args.SequenceEqual(new[] { "--existing-tests-only" })) await CodexObservationChecks.RunAsync(Check);
// The embedded pin document (lane broker-pins), after the --existing-tests-only cut-off.
if (!args.SequenceEqual(new[] { "--existing-tests-only" })) PinDocumentChecks.Run(Check);
Console.WriteLine($"AgentBroker desktop synthetic checks passed: {passed}");
return 0;

static string? Method(JsonObject node) => DesktopFrames.Text(node, "method");

sealed class FakeConnection : IDesktopConnection
{
    private readonly FakeDuplex stream;
    public FakeConnection() { stream = new FakeDuplex(Handle); }
    public Stream Stream => stream;
    public OwnerProcessIdentity Identity { get; } = new(1234, 123456789, "synthetic-public-app.exe");
    // Synthetic routers stand for an exactly pinned image unless a check selects publisher trust.
    public DesktopRouterTrust Trust { get; set; } = DesktopRouterTrust.ExactPinned;
    public DesktopAdapterException? ExactPinRefusal { get; set; }
    public Guid OwnerId { get; set; } = Guid.NewGuid();
    private Guid ClientId { get; } = Guid.NewGuid();
    private readonly List<JsonObject> requests = [];
    public IReadOnlyList<JsonObject> Requests { get { lock (requests) return requests.ToArray(); } }
    public bool Capability { get; set; } = true;
    public bool NoOwner { get; set; }
    public bool IncludeNoise { get; set; }
    public bool IdentityChanged { get; set; }
    public string? SubmitFailure { get; set; }
    public Guid? DirectoryThread { get; set; }
    public IReadOnlyList<Guid>? DirectoryThreads { get; set; }
    public bool Disposed { get; private set; }
    public JsonObject? ReadySmokeState { get; set; }
    public int SubmissionCount { get; private set; }
    public int DiscoveryCount { get; private set; }
    public int DeclinedDiscovery { get; private set; }

    public void VerifyStillSameOwner()
    {
        if (IdentityChanged) throw new DesktopAdapterException("server_incarnation_changed");
    }

    private void Handle(JsonObject request)
    {
        if (DesktopFrames.Text(request, "type") == "client-discovery-response")
        {
            if (DesktopFrames.IsTrue(request["response"], "canHandle")) throw new Exception("Probe claimed ownership.");
            DeclinedDiscovery++;
            return;
        }
        lock (requests) requests.Add((JsonObject)request.DeepClone());
        string method = DesktopFrames.Text(request, "method")!;
        if (method == "thread-stream-following-status-requested")
        {
            if (!Guid.TryParseExact(DesktopFrames.Text(request["params"], "conversationId"), "D", out var known)
                || (known != DirectoryThread && DirectoryThreads?.Contains(known) != true))
                throw new Exception("Observer requested an unknown thread.");
            Announce(known);
            return;
        }
        if (DesktopFrames.Text(request, "type") == "broadcast" && method == "thread-stream-following-changed")
        {
            if (ReadySmokeState is null) throw new Exception("Unexpected smoke observer subscription.");
            stream.Enqueue(new()
            {
                ["type"] = "broadcast", ["method"] = "thread-stream-state-changed", ["version"] = 11,
                ["sourceClientId"] = OwnerId.ToString("D"),
                ["params"] = new JsonObject
                {
                    ["hostId"] = "local", ["conversationId"] = DesktopFrames.Text(request["params"], "conversationId"),
                    ["change"] = new JsonObject { ["type"] = "snapshot", ["conversationState"] = ReadySmokeState.DeepClone() }
                }
            });
            return;
        }
        var response = new JsonObject
        {
            ["type"] = "response", ["requestId"] = DesktopFrames.Text(request, "requestId"),
            ["method"] = method, ["resultType"] = "success", ["handledByClientId"] = OwnerId.ToString("D")
        };
        if (IncludeNoise)
        {
            stream.Enqueue(new() { ["type"] = "broadcast", ["method"] = "synthetic-other-thread", ["params"] = new JsonObject() });
            stream.Enqueue(new() { ["type"] = "client-discovery-request", ["requestId"] = Guid.NewGuid().ToString("D") });
        }
        switch (method)
        {
            case "initialize":
                response["result"] = new JsonObject { ["clientId"] = ClientId.ToString("D") };
                break;
            case "thread-owner-discovery":
                DiscoveryCount++;
                if (NoOwner) { response["resultType"] = "error"; response["error"] = "no-client-found"; }
                else response["result"] = new JsonObject { ["supportsUntrustedAppInput"] = Capability };
                break;
            case "thread-follower-start-turn":
                SubmissionCount++;
                if (SubmitFailure == "disconnect") throw new IOException("Synthetic disconnect.");
                if (SubmitFailure == "timeout") return;
                if (SubmitFailure == "malformed") { response["result"] = "invalid"; break; }
                if (SubmitFailure == "native-error") { response["resultType"] = "error"; response["error"] = "timeout"; break; }
                if (SubmitFailure == "cloud-error")
                {
                    response.Remove("method"); response.Remove("handledByClientId");
                    response["resultType"] = "error"; response["error"] = "User-message admission requires a cloud app-server host."; break;
                }
                if (SubmitFailure is "busy-wrong-owner" or "busy-malformed-owner")
                {
                    response["handledByClientId"] = SubmitFailure == "busy-wrong-owner" ? Guid.NewGuid().ToString("D") : "invalid";
                    response["resultType"] = "error"; response["error"] = "App context must wait until the current turn finishes"; break;
                }
                if (SubmitFailure == "busy-context")
                {
                    response.Remove("method"); response.Remove("handledByClientId");
                    response["resultType"] = "error"; response["error"] = "App context must wait until the current turn finishes"; break;
                }
                if (SubmitFailure == "wrong-owner") response["handledByClientId"] = Guid.NewGuid().ToString("D");
                response["result"] = new JsonObject
                { ["result"] = new JsonObject { ["turn"] = new JsonObject { ["id"] = "synthetic-turn" } } };
                break;
            default: throw new Exception("Unexpected method: " + method);
        }
        stream.Enqueue(response);
        if (method == "initialize")
        {
            if (DirectoryThread is { } id) Announce(id);
            foreach (var entry in DirectoryThreads ?? []) Announce(entry);
        }
    }
    private void Announce(Guid thread) => stream.Enqueue(new()
    {
        ["type"] = "broadcast", ["method"] = "thread-stream-following-changed", ["version"] = 1,
        ["sourceClientId"] = OwnerId.ToString("D"),
        ["params"] = new JsonObject { ["hostId"] = "local", ["conversationId"] = thread.ToString("D"), ["following"] = true }
    });
    public void Disconnect() => stream.Complete();
    public void Inject(JsonObject frame) => stream.Enqueue(frame);
    public ValueTask DisposeAsync() { Disposed = true; stream.Dispose(); return ValueTask.CompletedTask; }
}

sealed class MetadataClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => now;
    internal void Advance(TimeSpan interval) { now += interval; }
}

sealed class FakeDuplex(Action<JsonObject> onWrite) : Stream
{
    private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
    private byte[]? current;
    private int offset;
    public void Complete() => incoming.Writer.TryComplete();
    public void Enqueue(JsonObject node)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(node);
        byte[] bytes = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, body.Length);
        body.CopyTo(bytes, 4);
        incoming.Writer.TryWrite(bytes);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (current is null || offset == current.Length)
        {
            try { current = await incoming.Reader.ReadAsync(cancellationToken); offset = 0; }
            catch (ChannelClosedException) { return 0; }
        }
        int length = Math.Min(2, Math.Min(buffer.Length, current.Length - offset));
        current.AsMemory(offset, length).CopyTo(buffer);
        offset += length;
        return length;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int size = BinaryPrimitives.ReadInt32LittleEndian(buffer.Span);
        if (size != buffer.Length - 4) throw new Exception("Invalid outbound frame.");
        onWrite((JsonObject)JsonNode.Parse(buffer.Span[4..])!);
        return ValueTask.CompletedTask;
    }
    protected override void Dispose(bool disposing) { incoming.Writer.TryComplete(); base.Dispose(disposing); }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
