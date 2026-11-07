using AgentBroker.Core;
using AgentBroker.Observations;
using AgentBroker.Worker;
using ModelContextProtocol;
using System.Text.Json;
using AgentBroker.Protocol;
using Microsoft.AspNetCore.Builder;

internal static class MetadataChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: WORKER_METADATA_PRODUCT_EXECUTED");
        ApprovalChecks(check);
        HomeOptionChecks(check);
        await DeclarationToolsAsync(check);
        DeclarationBounds(check);
        await ControlMappingAsync(check);
    }

    private static void HomeOptionChecks(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AgentBrokerMetadataOptions");
        string home = Path.Combine(Path.GetTempPath(), "AgentBrokerExplicitCodexHome");
        WorkerOptions Parse(string raw) => WorkerOptions.Parse(["--data", directory, "--codex-additional-homes", raw]);
        check(Parse(JsonSerializer.Serialize(new[] { home })).AdditionalCodexHomes.SequenceEqual(new[] { Path.TrimEndingDirectorySeparator(home) }),
            "CODEX_HOME_OPTION_FORWARDING: actual worker argument parsing retains the explicitly configured home");
        foreach (string raw in new[] { "null", "{}", "[\"relative-home\"]", JsonSerializer.Serialize(Enumerable.Repeat(home, 5).ToArray()) })
        {
            bool refused = false;
            try { Parse(raw); } catch (ArgumentException) { refused = true; }
            check(refused, "CODEX_HOME_OPTION_INVALID: malformed, relative and over-count home arguments are refused");
        }
        bool oversizedRefused = false;
        try { Parse(new string(' ', 8193) + "[]"); } catch (ArgumentException) { oversizedRefused = true; }
        check(oversizedRefused, "CODEX_HOME_OPTION_BOUND: valid JSON beyond the raw configuration bound is refused before parsing");
    }

    private static void ApprovalChecks(Action<bool, string> check)
    {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var zero = new CodexApprovalObservation(0, now);
        check(CodexApprovalObservation.View(CodexApprovalObservation.Parse(zero.ToJson()), now) is { State: "fresh", VisiblePromptCount: 0 },
            "APPROVAL_ZERO_OBSERVED: a complete fresh observation can establish zero visible prompts");
        foreach (string raw in new[] { "", "{}", "{\"observedAtUtc\":\"2026-11-06T12:00:00Z\"}", "{\"count\":0}",
            "{\"count\":-1,\"observedAtUtc\":\"2026-11-06T12:00:00Z\"}", zero.ToJson().Replace("0,", "0,\"promptText\":\"untrusted\",") })
            check(CodexApprovalObservation.Parse(raw) is null, "APPROVAL_UNAVAILABLE: missing, malformed, out-of-bound or extra context never becomes an observed zero");
        check(CodexApprovalObservation.Parse(new string(' ', 257) + zero.ToJson()) is null,
            "APPROVAL_RAW_BOUND: otherwise valid context beyond the raw bound is refused before deserialization");
        check(CodexApprovalObservation.View(zero, now.Add(CodexApprovalObservation.FreshFor)) is { State: "stale", VisiblePromptCount: null },
            "APPROVAL_STALE_BOUNDARY: an observation at the freshness boundary is stale, not a current count");
        check(CodexApprovalObservation.View(zero with { ObservedAtUtc = now.AddMinutes(1) }, now).State == "unavailable",
            "APPROVAL_FUTURE: an unknown future observation is unavailable, not a current count");
        check(CodexApprovalObservation.View(zero with { ObservedAtUtc = now.AddSeconds(5) }, now) is { State: "fresh", VisiblePromptCount: 0 },
            "APPROVAL_CLOCK_TOLERANCE: a bounded clock tolerance remains usable rather than rejecting every future timestamp");
        using var json = JsonDocument.Parse(zero.ToJson());
        check(json.RootElement.EnumerateObject().Count() == 2, "APPROVAL_PAYLOAD: only count and timestamp are published");
    }

    private static async Task DeclarationToolsAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("metadata-declarations");
        var row = fixture.Native("codex:" + Guid.NewGuid().ToString("D")) with { Adapter = "codex-native", LeaseUntil = fixture.Clock.GetUtcNow().AddMinutes(2) };
        fixture.Store.Register(row);
        check(ToolFixture.Refused(() => fixture.Tools.SetSessionDeclaration(row.Address, "Declared reviewer"), "native_session_unverified"),
            "DECLARATION_ADAPTER_PROOF: registration, label and native-looking prefix alone cannot declare a native row");
        fixture.Store.ObserveNativeSession(row.Address, "codex-native");
        var declared = fixture.Tools.SetSessionDeclaration(row.Address, "Declared reviewer", "Local client estimate", "rate_limited", fixture.Clock.GetUtcNow().AddMinutes(10), 1);
        check(fixture.Context.Items[McpInboxHints.ParticipantKey] is SessionAddress participant && participant == row.Address,
            "DECLARATION_PENDING_HINT: a successful declaration establishes its exact participant for the existing pending-message advisory");
        var listed = (await fixture.Tools.ListSessions(default)).Single(s => s.Address == row.Address);
        check(listed.LocalDeclaration == declared && declared?.Source == "local_client_declaration" && listed.Label == row.Label &&
            listed.Activity == row.Activity && listed.Address == row.Address,
            "DECLARATION_HINT_ONLY: declared names and availability never replace the native label, Activity or routing address");
        var generic = fixture.Register("Existing generic label");
        check(ToolFixture.Refused(() => fixture.Tools.SetSessionDeclaration(generic.Address, "Other name"), "native_session_unverified") &&
            fixture.Current(generic.Address)?.Label == "Existing generic label",
            "DECLARATION_GENERIC_UNCHANGED: generic registration keeps its label semantics and cannot impersonate native eligibility");
        fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        check((await fixture.Tools.ListSessions(default)).All(s => s.LocalDeclaration is null),
            "DECLARATION_LOCAL_ONLY: paired broker callers never receive local declarations");
        bool peerRefused = false;
        try { fixture.Tools.SetSessionDeclaration(row.Address, "Peer override"); } catch (McpException) { peerRefused = true; }
        check(peerRefused, "DECLARATION_REMOTE_WRITE_REFUSED: a paired broker cannot declare a local native row");
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        check((await fixture.Tools.ListSessions(default)).Single(s => s.Address == row.Address).LocalDeclaration is null,
            "DECLARATION_EXPIRES: a hint expires independently of a still-live native row");
        fixture.Tools.SetSessionDeclaration(row.Address, "Second estimate");
        var successor = row with { Address = row.Address with { Incarnation = Guid.NewGuid() } };
        fixture.Store.Register(successor); fixture.Store.ObserveNativeSession(successor.Address, "codex-native");
        check((await fixture.Tools.ListSessions(default)).Where(s => s.Address == successor.Address).All(s => s.LocalDeclaration is null),
            "DECLARATION_EXACT_INCARNATION: a new incarnation never inherits its predecessor's declaration");
        fixture.Tools.SetSessionDeclaration(successor.Address, "To clear");
        check(fixture.Tools.SetSessionDeclaration(successor.Address) is null &&
            (await fixture.Tools.ListSessions(default)).Single(s => s.Address == successor.Address).LocalDeclaration is null,
            "DECLARATION_CLEAR: an explicit empty declaration removes the hint");
    }

    private static void DeclarationBounds(Action<bool, string> check)
    {
        var registry = new SessionDeclarationRegistry();
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var broker = Guid.NewGuid();
        SessionAddress Address() => new(broker, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        bool Refused(Action action, string code)
        { try { action(); return false; } catch (BrokerException error) { return error.Code == code; } }
        check(Refused(() => registry.Set(Address(), "", "", null, null, 1, now, _ => false), "native_session_unverified"),
            "DECLARATION_ELIGIBILITY_REQUIRED: even clearing a declaration requires current native proof");
        foreach (var value in new[] { (new string('x', 161), "", (string?)null, (DateTimeOffset?)null, 1),
            ("", new string('x', 513), null, null, 1), ("bad\nname", "", null, null, 1),
            ("", "", "observed_idle", null, 1), ("", "", null, now.AddDays(2), 1), ("", "", null, null, 0) })
            check(Refused(() => registry.Set(Address(), value.Item1, value.Item2, value.Item3, value.Item4, value.Item5, now, _ => true), "invalid_session_declaration"),
                "DECLARATION_BOUNDS: invalid name, note, control character, availability, time or lifetime is refused");
        for (int index = 0; index < SessionDeclarationRegistry.MaximumDeclarations; index++) registry.Set(Address(), "Bounded hint", "", null, null, 1, now, _ => true);
        check(Refused(() => registry.Set(Address(), "Overflow", "", null, null, 1, now, _ => true), "declaration_limit"),
            "DECLARATION_CAPACITY: the registry cannot grow beyond its bound");
        check(registry.Snapshot(now.AddMinutes(1), _ => true).Count == 0, "DECLARATION_PRUNING: expired declarations release their bounded capacity");
    }

    private static async Task ControlMappingAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("metadata-control");
        await using var worker = new ControlWorker(fixture);
        await using var control = new LocalControlServer(worker);
        check(control.Snapshot().ApprovalPrompts is null, "APPROVAL_CONTROL_UNAVAILABLE: no observation is not an observed zero");
        var now = fixture.Clock.GetUtcNow();
        var response = await control.HandleAsync(new("approval-observation", ApprovalPromptCount: 3, ApprovalObservedAtUtc: now), default);
        check(response.Snapshot?.ApprovalPrompts is { State: "fresh", VisiblePromptCount: 3 } && response.Snapshot.ApprovalPrompts.ObservedAtUtc == now,
            "APPROVAL_CONTROL_MAPPING: the real local control path preserves the aggregate count and original timestamp");
        var reordered = await control.HandleAsync(new("approval-observation", ApprovalPromptCount: 0, ApprovalObservedAtUtc: now.AddSeconds(-1)), default);
        check(reordered.Snapshot?.ApprovalPrompts?.VisiblePromptCount == 3,
            "APPROVAL_OBSERVATION_ORDER: a late older observation cannot replace a newer aggregate count");
        fixture.Clock.Advance(TimeSpan.FromHours(-1));
        var rolledNow = fixture.Clock.GetUtcNow();
        var recovered = await control.HandleAsync(new("approval-observation", ApprovalPromptCount: 1, ApprovalObservedAtUtc: rolledNow), default);
        check(recovered.Snapshot?.ApprovalPrompts is { State: "fresh", VisiblePromptCount: 1 } &&
            recovered.Snapshot.ApprovalPrompts.ObservedAtUtc == rolledNow,
            "APPROVAL_CLOCK_ROLLBACK_RECOVERY: a current valid observation replaces a cached sample made unavailable by clock rollback");
        var olderAfterRollback = await control.HandleAsync(new("approval-observation", ApprovalPromptCount: 0, ApprovalObservedAtUtc: rolledNow.AddSeconds(-1)), default);
        check(olderAfterRollback.Snapshot?.ApprovalPrompts?.VisiblePromptCount == 1,
            "APPROVAL_ORDER_AFTER_ROLLBACK: ordinary older samples still cannot replace the valid recovery sample");
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await control.HandleAsync(new("approval-observation"), default);
        var stale = await control.HandleAsync(new("approval-observation", ApprovalPromptCount: 0, ApprovalObservedAtUtc: now.AddMinutes(-1)), default);
        check(stale.Snapshot?.ApprovalPrompts is { State: "stale", VisiblePromptCount: null },
            "APPROVAL_CONTROL_STALE: a stale zero remains unavailable in the actual local snapshot");
        var cleared = await control.HandleAsync(new("approval-observation"), default);
        check(cleared.Snapshot?.ApprovalPrompts is null, "APPROVAL_CONTROL_CLEAR: source shutdown withdraws the retained aggregate observation");
    }

    private sealed class ControlWorker : ILocalControlWorker, IAsyncDisposable
    {
        public BrokerRuntime Runtime { get; }
        public WorkerOptions Options { get; }
        public WebApplication Application { get; }
        public string LocalAccessToken => "synthetic-unused";
        public McpStatusView McpStatus => new(false, null, null, []);
        public LanProfileMonitor LanProfile => throw new InvalidOperationException("LAN observation was not requested.");
        internal ControlWorker(ToolFixture fixture)
        {
            Runtime = fixture.Runtime;
            Options = new(fixture.Root, "Synthetic metadata", 0, 0, false) { EnableBrokerDiscovery = false };
            Application = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = fixture.Root }).Build();
        }
        public ValueTask DisposeAsync() => Application.DisposeAsync();
    }
}
