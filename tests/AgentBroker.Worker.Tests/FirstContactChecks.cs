using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Protocol;
using AgentBroker.Worker;
using ModelContextProtocol;

// First contact (2026-10-29). Agents on another workstation registered mailboxes and ran pollers although they had native
// rows, because nothing told them which row was theirs; they started fresh threads at the eighth reply; and they could not
// order messages that arrived out of order. Each label names the rule a mutation of WhoAmI, the resolver, the reply refusal
// or the envelope must break.
internal static class FirstContactChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        ResolverChecks(check);
        await WhoAmIChecks(check);
        await ReplyBudgetChecks(check);
        EnvelopeChecks(check);
        await MailboxActivityChecks(check);
        await ErrorCodeChecks(check);
        NoticeDropSnapshotChecks(check);
    }

    // N-agentbroker-notice-loss-signal-01 (1.5.0): the store's dropped-notice counts reach the module's snapshot, and a
    // worker with nothing dropped sends the snapshot exactly as before the field existed.
    private static void NoticeDropSnapshotChecks(Action<bool, string> check)
    {
        var at = DateTimeOffset.UtcNow;
        check(LocalControlServer.DroppedNotices(new(1, 2, 3, at)) is { SenderUnavailable: 1, StoreFull: 2, QueueFull: 3 } view && view.LastUtc == at,
            "NOTICE_DROP_SNAPSHOT: each reason's count and the last drop's time reach the snapshot");
        var empty = new BrokerSnapshot(Guid.NewGuid(), "Synthetic", "http://127.0.0.1:7432", null, [], [], [], [],
            new Dictionary<string, string>(), DroppedNotices: LocalControlServer.DroppedNotices(new(0, 0, 0, null)));
        check(!JsonSerializer.Serialize(empty, ControlProtocol.Json).Contains("droppedNotices", StringComparison.OrdinalIgnoreCase),
            "NOTICE_DROP_ABSENT: a worker with nothing dropped leaves the field out of the snapshot");
    }

    // N-agentbroker-mailbox-activity-01: a mailbox lists as Idle only while its owner waits; otherwise Unknown.
    private static async Task MailboxActivityChecks(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("mailbox-activity");
        var owner = fixture.Register("Synthetic listening mailbox");
        var native = fixture.Native("codex:77777777-8888-4999-aaaa-bbbbbbbbbbbb");
        fixture.Store.Register(native);
        async Task<SessionActivity?> Listed(SessionAddress address) =>
            (await fixture.Tools.ListSessions(default)).SingleOrDefault(session => session.Address == address)?.Activity;
        check(await Listed(owner.Address) == SessionActivity.Unknown && fixture.Current(owner.Address)?.Activity == SessionActivity.Idle,
            "MAILBOX_UNKNOWN_UNTIL_WAIT: a registered mailbox nobody is waiting on lists as Unknown, while the store keeps it present");
        var waiting = fixture.Tools.WaitForMessages(owner.Address, default, timeoutSeconds: 2);
        await Task.Delay(200);
        check(!waiting.IsCompleted && await Listed(owner.Address) == SessionActivity.Idle,
            "MAILBOX_IDLE_WHILE_WAITING: a mailbox whose owner is inside a wait lists as Idle");
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        check(await Listed(owner.Address) == SessionActivity.Unknown, "MAILBOX_UNKNOWN_AFTER_WAIT: once the wait ends the mailbox lists as Unknown again");
        check(await Listed(native.Address) == SessionActivity.Idle, "MAILBOX_NATIVE_UNTOUCHED: a native row's own activity is listed unchanged");
    }

    // N-agentbroker-register-errors-01: broker refusals reach the client as their code, not the SDK's generic error.
    private static async Task ErrorCodeChecks(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("error-codes");
        static string? Code(Action action)
        {
            try { action(); return null; }
            catch (McpException error) { return error.Message; }
            catch (Exception error) { return "unwrapped " + error.GetType().Name; }
        }
        check(Code(() => fixture.Tools.RegisterAgent(Guid.Empty, Guid.NewGuid(), "Synthetic bad registration")) == "invalid_session",
            "ERRORS_REGISTER: register_agent reports invalid_session as its code");
        var malformed = new SessionAddress(fixture.Store.BrokerId, "", Guid.NewGuid());
        check(Code(() => fixture.Tools.ReadInbox(malformed)) == "invalid_session" &&
            Code(() => fixture.Tools.AcknowledgeMessage(Guid.NewGuid(), malformed, MessageStatus.Delivered)) == "invalid_session",
            "ERRORS_ADDRESS: a malformed address reports invalid_session from the tools that check participants");
    }

    // The production resolver against real sockets in this process: the owner of a connection's client end is this process.
    private static void ResolverChecks(Action<bool, string> check)
    {
        var resolver = WindowsCallerProcessResolver.Instance;
        using var self = Process.GetCurrentProcess();
        ulong selfStart = (ulong)self.StartTime.ToFileTimeUtc();
        foreach (var (family, loopback, label) in new[] { (AddressFamily.InterNetwork, IPAddress.Loopback, "IPV4"), (AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback, "IPV6") })
        {
            var listener = new TcpListener(loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient(family);
                client.Connect(loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var accepted = listener.AcceptTcpClient();
                int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port, serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var chain = resolver.Resolve(loopback, clientPort, serverPort);
                check(chain.Count >= 2 && chain[0].ProcessId == Environment.ProcessId && chain[0].StartFileTime == selfStart &&
                    chain.Skip(1).All(parent => parent.StartFileTime <= chain[0].StartFileTime),
                    $"WHOAMI_RESOLVER_{label}: the resolver finds this process as the owner of a loopback connection's client end, then its parents");
                var resolution = resolver.ResolveCaller(loopback, clientPort, serverPort);
                check(resolution.Chain.Count == chain.Count && !resolution.OwnerAccessDenied,
                    $"WHOAMI_RESOLVER_OPENABLE_{label}: an owner this process can open is never reported as access denied");
                // Both ends live in this process here, so only ports that match no connection can show exactness.
                check(resolver.Resolve(loopback, clientPort, serverPort + 1).Count == 0 && resolver.Resolve(loopback, clientPort + 1, serverPort).Count == 0,
                    $"WHOAMI_RESOLVER_EXACT_{label}: a wrong server port or a wrong client port finds no caller");
            }
            finally { listener.Stop(); }
        }
        check(resolver.Resolve(IPAddress.Parse("192.0.2.10"), 50000, 7432).Count == 0, "WHOAMI_RESOLVER_LOOPBACK_ONLY: a non-loopback address is never resolved");
        // The production probe behind caller_elevated answers true only for ERROR_ACCESS_DENIED. An elevated process cannot be
        // made here, so its true case is the coordinator's live measurement (Medium to an elevated claude.exe: error 5).
        int absent = int.MaxValue - 3;
        bool absentRunning;
        try { using var probe = Process.GetProcessById(absent); absentRunning = true; }
        catch (ArgumentException) { absentRunning = false; }
        // Non-vacuous (review of 25a0c65, finding 1): the refusal itself through an injected open, which must be the limited query.
        var probes = new List<uint>();
        (bool Opened, int Error) Refuse(uint access, int pid) { probes.Add(access); return (false, 5); }
        check(WindowsCallerProcessResolver.AccessDenied(4242, Refuse) && probes.SequenceEqual([0x1000u]) &&
            !WindowsCallerProcessResolver.AccessDenied(4242, (_, _) => (false, 87)) && !WindowsCallerProcessResolver.AccessDenied(4242, (_, _) => (true, 0)) &&
            !WindowsCallerProcessResolver.AccessDenied(Environment.ProcessId) && !absentRunning && !WindowsCallerProcessResolver.AccessDenied(absent),
            "WHOAMI_DENIED_PROBE: the limited-query probe is a refusal only for ERROR_ACCESS_DENIED, never for this process or an ID no process has");
        // The second shape: an elevated launch that allows the limited query and refuses the full one (0x0400). The start time
        // is read again first, so a reused process ID is never called elevated.
        var elevatedWalk = new CallerProcess(300, 800, "claude.exe");
        probes.Clear();
        bool fullDenied = WindowsCallerProcessResolver.QueryDenied(elevatedWalk, _ => 800, Refuse);
        bool fullAsked = probes.SequenceEqual([0x0400u]);
        probes.Clear();
        bool reused = WindowsCallerProcessResolver.QueryDenied(elevatedWalk, _ => 801, Refuse);
        check(fullDenied && fullAsked && !reused && probes.Count == 0 &&
            !WindowsCallerProcessResolver.QueryDenied(elevatedWalk, _ => 800, (_, _) => (false, 87)) &&
            !resolver.QueryDenied(new CallerProcess(Environment.ProcessId, selfStart, "AgentBroker.Worker.Tests.exe")),
            "WHOAMI_QUERY_DENIED_PROBE: a refused full query of the walked process is elevated; a reused ID, another error or this process is not");

        // The walk on synthetic processes: 30's recorded parent 40 was created after 30, so 40 is a reused ID and not its parent.
        var parents = new Dictionary<int, (int Parent, string Image)>
        { [10] = (20, "pwsh.exe"), [20] = (30, "claude.exe"), [30] = (40, "Code.exe"), [40] = (50, "unrelated.exe"), [50] = (0, "explorer.exe") };
        var starts = new Dictionary<int, ulong> { [10] = 900, [20] = 800, [30] = 700, [40] = 750, [50] = 100 };
        var walked = WindowsCallerProcessResolver.Walk(10, parents, pid => starts.GetValueOrDefault(pid));
        check(walked.Select(process => process.ProcessId).SequenceEqual(new[] { 10, 20, 30 }) && walked[1].ImageName == "claude.exe",
            "WHOAMI_WALK_REUSED_PARENT: the parent walk stops at a recorded parent created after its child (a reused process ID)");
        var cyclic = new Dictionary<int, (int Parent, string Image)> { [10] = (20, "a.exe"), [20] = (10, "b.exe") };
        check(WindowsCallerProcessResolver.Walk(10, cyclic, _ => 500).Count == 2, "WHOAMI_WALK_CYCLE: a parent cycle ends the walk instead of looping");

        // N-broker-elevated-caller-01: the owner of an elevated session's connection is found, but a Medium broker is denied
        // even a limited query of it, so the walk is empty. Compose says why, and asks only about an owner it could not open.
        int asked = 0;
        bool Denied(int _) { asked++; return true; }
        var denied = WindowsCallerProcessResolver.Compose(300, _ => [], Denied);
        check(denied.Chain.Count == 0 && denied.OwnerAccessDenied && asked == 1,
            "WHOAMI_COMPOSE_DENIED: a found owner whose limited query is denied is reported as such");
        asked = 0;
        var opened = WindowsCallerProcessResolver.Compose(300, _ => [new(300, 800, "claude.exe")], Denied);
        check(opened.Chain.Count == 1 && !opened.OwnerAccessDenied && asked == 0,
            "WHOAMI_COMPOSE_OPENABLE: an owner whose walk succeeded is never asked about or reported as denied");
        var vanished = WindowsCallerProcessResolver.Compose(300, _ => [], _ => false);
        var missing = WindowsCallerProcessResolver.Compose(null, _ => throw new InvalidOperationException("no owner to walk"), Denied);
        check(!vanished.OwnerAccessDenied && !missing.OwnerAccessDenied && missing.Chain.Count == 0,
            "WHOAMI_COMPOSE_NOT_DENIED: an owner that exited, or no owner at all, stays unresolved");
    }

    private static async Task WhoAmIChecks(Action<bool, string> check)
    {
        var callers = new FakeCallers();
        var adapter = new ProcessAdapter();
        await using var fixture = new ToolFixture("first-contact", adapters: [adapter], callers: callers);
        fixture.Context.Connection.RemoteIpAddress = IPAddress.Loopback;
        fixture.Context.Connection.RemotePort = 50123;
        fixture.Context.Connection.LocalPort = 7432;
        var mine = fixture.Native("claude:11111111-2222-4333-8444-555555555555");
        var parentAgent = fixture.Native("claude:66666666-7777-4888-9999-000000000000");
        fixture.Store.Register(mine); fixture.Store.Register(parentAgent);

        callers.Chain = [new(100, 1000, "pwsh.exe"), new(200, 900, "bash.exe"), new(300, 800, "claude.exe"), new(400, 700, "Code.exe")];
        adapter.Owned[(300, 800UL)] = mine.Address;
        var found = fixture.Tools.WhoAmI();
        check(found.Address == mine.Address && callers.Calls.Last() == (IPAddress.Loopback, 50123, 7432),
            "WHOAMI_NEAREST_AGENT: whoami returns the session the nearest claude parent owns, from the connection's exact endpoint");

        adapter.Owned.Clear(); adapter.Owned[(300, 801UL)] = mine.Address;
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "no_native_session"),
            "WHOAMI_START_TIME: a binding for the same process ID with another creation time is not the caller");

        // An agent another agent started: the nearest claude process is no discovered session, and its parent's must not be claimed.
        callers.Chain = [new(100, 1000, "pwsh.exe"), new(300, 800, "claude.exe"), new(500, 600, "claude.exe")];
        adapter.Owned.Clear(); adapter.Owned[(500, 600UL)] = parentAgent.Address;
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "no_native_session"),
            "WHOAMI_STOPS_AT_NEAREST_AGENT: a child agent is never told it is the agent that started it");

        callers.Chain = [new(100, 1000, "pwsh.exe"), new(300, 800, "claude.exe")];
        adapter.Owned.Clear(); adapter.Owned[(300, 800UL)] = new(fixture.Store.BrokerId, "claude:aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", Guid.NewGuid());
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "no_native_session"),
            "WHOAMI_DIRECTORY_ROW: a binding whose address is not in the directory is not returned");

        callers.Chain = [];
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "caller_unresolved"), "WHOAMI_UNRESOLVED: an unknown connection owner says so");
        callers.Denied = true;
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "caller_elevated"),
            "WHOAMI_ELEVATED: an owner the broker may not query (elevated, or another user) gets caller_elevated, not caller_unresolved");
        callers.Denied = false;

        // Review of 25a0c65, finding 1: the limited query succeeded, so the walk reached the elevated claude process, but no
        // adapter binds it (discovery could not read it). Its refused full query answers caller_elevated.
        callers.Chain = [new(100, 1000, "pwsh.exe"), new(300, 800, "claude.exe"), new(400, 700, "Code.exe")];
        adapter.Owned.Clear();
        callers.QueryDeniedFor = [300];
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "caller_elevated"),
            "WHOAMI_ELEVATED_FULL_QUERY: the nearest agent process of an unbound chain whose full query is refused gets caller_elevated");
        callers.QueryDeniedFor = [100, 400];
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "no_native_session"),
            "WHOAMI_ELEVATED_NEAREST_ONLY: a refused full query of a process that is not the nearest agent proves nothing");
        callers.QueryDeniedFor = [300];
        adapter.Owned[(300, 800UL)] = mine.Address;
        check(fixture.Tools.WhoAmI().Address == mine.Address, "WHOAMI_ELEVATED_BOUND_WINS: a chain an adapter binds is answered with its row");
        callers.QueryDeniedFor = [];

        callers.Chain = [new(300, 800, "claude.exe")];
        adapter.Owned.Clear(); adapter.Owned[(300, 800UL)] = mine.Address;
        fixture.Context.Request.IsHttps = true;
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "whoami is a local operation."),
            "WHOAMI_LOCAL_ONLY: a connection over the paired-broker HTTPS listener cannot ask whoami");
        fixture.Context.Request.IsHttps = false;
        fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
        check(ToolFixture.Refused(() => fixture.Tools.WhoAmI(), "whoami is a local operation."),
            "WHOAMI_LOCAL_BROKER: a caller authenticated as another broker cannot ask whoami");
        fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
        check(fixture.Tools.WhoAmI().Address == mine.Address, "WHOAMI_RESTORED: the same caller resolves again once local");

        // Delivery disabled (Codex 01a1855c's review): the caller's row is observation-only. whoami still names it, and a hello
        // sent from it cannot be answered, which is why the card sends such a caller to a mailbox.
        var observing = new BrokerSession(new(fixture.Store.BrokerId, "claude:99999999-8888-4777-a666-555555555555", Guid.NewGuid()),
            "Synthetic observation-only", "claude-native", SessionCapabilities.None, SessionActivity.Idle, fixture.Clock.GetUtcNow().AddMinutes(1));
        fixture.Store.Register(observing);
        adapter.Owned.Clear(); adapter.Owned[(300, 800UL)] = observing.Address;
        var named = fixture.Tools.WhoAmI();
        check(named.Address == observing.Address && named.Capabilities == SessionCapabilities.None,
            "WHOAMI_OBSERVATION_ROW: whoami returns an observation-only row unchanged, without Receive or WakeIdle");
        var listener = fixture.Register("Synthetic peer mailbox");
        var hello = fixture.Tools.Send(observing.Address, listener.Address, Guid.NewGuid(), "Synthetic hello from an observation-only row.").Message;
        check(ToolFixture.Refused(() => fixture.Tools.ReplyToMessage(hello.Id, Guid.NewGuid(), "Synthetic answer."), "recipient_cannot_receive"),
            "WHOAMI_OBSERVATION_NO_REPLY: an answer to a hello sent from an observation-only row is refused recipient_cannot_receive");
    }

    private static async Task ReplyBudgetChecks(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("reply-budget");
        var a = fixture.Register("Synthetic budget sender");
        var b = fixture.Register("Synthetic budget recipient");
        Guid first = Guid.NewGuid();
        var current = fixture.Tools.Send(a.Address, b.Address, first, "Synthetic first message.").Message;
        int hops = 0;
        while (current.RemainingReplies > 0 && hops < 20)
        { current = fixture.Tools.ReplyToMessage(current.Id, Guid.NewGuid(), "Synthetic hop " + hops).Message; hops++; }
        check(hops == 8 && current.RemainingReplies == 0 && current.ConversationId == first,
            "REPLY_BUDGET_HOPS: a send allows eight reply hops, each one fewer than the message it answers");
        check(ToolFixture.Refused(() => fixture.Tools.ReplyToMessage(current.Id, Guid.NewGuid(), "Synthetic ninth hop."), "reply_budget_exhausted"),
            "REPLY_BUDGET_NAMED: replying at remainingReplies 0 is refused as reply_budget_exhausted, not a generic invalid_reply");
        var continued = fixture.Tools.Send(current.Recipient, current.Sender, Guid.NewGuid(), "Synthetic continuation.", conversationId: first).Message;
        check(continued.RemainingReplies == 8 && continued.ConversationId == first,
            "REPLY_BUDGET_CONTINUE: send with the same conversationId continues the conversation with a fresh budget");
    }

    private static void EnvelopeChecks(Action<bool, string> check)
    {
        var created = new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero);
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, new(Guid.NewGuid(), "claude:synthetic", Guid.NewGuid()),
            new(Guid.NewGuid(), "codex:synthetic", Guid.NewGuid()), "Synthetic envelope text.", created, created.AddMinutes(5), 3);
        string formatted = PeerMessageFormatter.Format(message, new("Synthetic sender", "local"));
        using var envelope = JsonDocument.Parse(formatted[(formatted.IndexOf('\n') + 1)..]);
        check(envelope.RootElement.TryGetProperty("createdAt", out var createdAt) && createdAt.GetDateTimeOffset() == created &&
            envelope.RootElement.TryGetProperty("remainingReplies", out var remaining) && remaining.GetInt32() == 3,
            "ENVELOPE_CREATED_AT: the native envelope carries the message's createdAt beside remainingReplies");
        string preamble = formatted[..formatted.IndexOf('\n')];
        check(preamble.Contains("at 0, continue with send to the sender using the same conversationId", StringComparison.Ordinal) &&
            preamble.Contains("createdAt is the sender's own timestamp; this broker delivers one sender's messages in the order it accepted them", StringComparison.Ordinal),
            "ENVELOPE_GUIDANCE: the preamble says how to continue at remainingReplies 0, and that delivery order is acceptance order, not the sender's createdAt");
    }

    private sealed class FakeCallers : ICallerProcessResolver
    {
        internal IReadOnlyList<CallerProcess> Chain = [];
        internal bool Denied;
        internal HashSet<int> QueryDeniedFor = [];
        public bool QueryDenied(CallerProcess process) => QueryDeniedFor.Contains(process.ProcessId);
        internal List<(IPAddress, int, int)> Calls { get; } = [];
        public IReadOnlyList<CallerProcess> Resolve(IPAddress clientAddress, int clientPort, int serverPort)
        { Calls.Add((clientAddress, clientPort, serverPort)); return Chain; }
        public CallerResolution ResolveCaller(IPAddress clientAddress, int clientPort, int serverPort)
        { Calls.Add((clientAddress, clientPort, serverPort)); return new(Chain, Denied && Chain.Count == 0); }
    }

    private sealed class ProcessAdapter : ILocalAgentAdapter
    {
        internal Dictionary<(int, ulong), SessionAddress> Owned { get; } = [];
        public string Id => "synthetic-native";
        public Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token) => Task.FromResult<IReadOnlyList<BrokerSession>>([]);
        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => Task.FromResult(new AdapterResult(AdapterAdmission.Refused));
        public SessionAddress? FindSessionByProcess(int processId, ulong startFileTime) => Owned.GetValueOrDefault((processId, startFileTime));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
