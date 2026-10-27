using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Security;

// Durable generic mailboxes: a lapsed presence lease no longer drops a turn-based agent off the bus,
// while native sessions, peer directories and capacity keep their presence-only rules.
internal static class MailboxChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await RetainedConversation(check);
        await NativeLeaseStillEnds(check);
        await PeerDirectoryStaysPresenceOnly(check);
        RetainedCapacity(check);
        await RuntimeCapacityAndPreference(check);
        RestartKeepsMessages(check);
        await FederationDirectoryOverTls(check);
    }

    private static async Task RetainedConversation(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("mailbox-conversation");
        var sender = fixture.Register("Synthetic mailbox sender");
        var owner = fixture.Register("Synthetic mailbox owner");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        check(fixture.Current(owner.Address)?.Activity == SessionActivity.Away && fixture.Current(sender.Address)?.Activity == SessionActivity.Away,
            "two generic inboxes are retained as Away mailboxes after their presence lapses");
        Guid messageId = Guid.NewGuid();
        MessageReceipt? sent = null;
        try { sent = fixture.Tools.Send(sender.Address, owner.Address, messageId, "Synthetic message for a mailbox that is Away."); }
        catch (ModelContextProtocol.McpException) { /* A refusal is the defect this check names. */ }
        check(sent?.Status == MessageStatus.Accepted && fixture.Current(owner.Address)?.Activity == SessionActivity.Away &&
            fixture.Current(sender.Address)?.Activity == SessionActivity.Idle,
            "a lapsed mailbox accepts a send from a lapsed sender and only the sender regains presence");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var inbox = fixture.Tools.ReadInbox(owner.Address);
        check(inbox.Any(receipt => receipt.Message.Id == messageId) && fixture.Current(owner.Address)?.Activity == SessionActivity.Idle,
            "the owner of a retained mailbox reads a message that arrived while it was Away and regains presence");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var acknowledged = fixture.Tools.AcknowledgeMessage(messageId, owner.Address, MessageStatus.Delivered);
        check(acknowledged.Status == MessageStatus.Delivered, "a lapsed mailbox acknowledges a message it received");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var reply = fixture.Tools.ReplyToMessage(messageId, Guid.NewGuid(), "Synthetic reply from a mailbox that was Away.");
        check(reply.Status == MessageStatus.Accepted && reply.Message.Sender == owner.Address && reply.Message.Recipient == sender.Address,
            "a lapsed mailbox replies to a lapsed sender with the exact addresses reversed");
        fixture.Clock.Advance(BrokerLimits.MailboxRetention + TimeSpan.FromSeconds(1));
        var later = fixture.Register("Synthetic later sender");
        check(fixture.Current(owner.Address) is null &&
            ToolFixture.Refused(() => fixture.Tools.Send(later.Address, owner.Address, Guid.NewGuid(), "Synthetic late message."), "recipient_unavailable"),
            "after its 24-hour retention a mailbox is gone and a send to it is refused");
    }

    private static async Task NativeLeaseStillEnds(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("mailbox-native");
        var generic = fixture.Register("Synthetic generic peer");
        var native = fixture.Native("codex:synthetic-lapsing");
        fixture.Store.Register(native);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        check(fixture.Current(native.Address) is null &&
            ToolFixture.Refused(() => fixture.Tools.Send(generic.Address, native.Address, Guid.NewGuid(), "Synthetic native text."), "recipient_unavailable") &&
            ToolFixture.Refused(() => fixture.Tools.Send(native.Address, generic.Address, Guid.NewGuid(), "Synthetic native text."), "sender_unavailable"),
            "WITNESS a native session is still refused as sender and recipient after its lease lapses");
        bool awayRefused = false;
        try { fixture.Store.Register(fixture.Native("codex:synthetic-claims-away") with { Activity = SessionActivity.Away }); }
        catch (BrokerException error) { awayRefused = error.Code == "invalid_session"; }
        check(awayRefused && !fixture.Store.Directory().Any(session => session.Address.SessionId == "codex:synthetic-claims-away"),
            "a registration cannot claim the Away display state");
        fixture.Store.Register(fixture.Native("codex:synthetic-claims-idle"));
        check(fixture.Store.Directory().Any(session => session.Address.SessionId == "codex:synthetic-claims-idle"),
            "WITNESS the same registration as Idle succeeds");
    }

    private static async Task PeerDirectoryStaysPresenceOnly(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("mailbox-peer-directory");
        var away = fixture.Register("Synthetic away mailbox");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var present = fixture.Register("Synthetic present inbox");
        var local = await fixture.Tools.ListSessions(default);
        check(local.SingleOrDefault(session => session.Address == away.Address) is { Activity: SessionActivity.Away } listed &&
            listed.LeaseUntil == away.LeaseUntil && listed.LeaseUntil < fixture.Clock.GetUtcNow(),
            "local list_sessions lists a retained mailbox as Away with its last presence lease");
        var peerStore = fixture.Store.Directory(forPeer: true);
        var peerRuntime = await fixture.Runtime.DirectoryAsync(forPeer: true, default);
        check(!peerStore.Concat(peerRuntime).Any(session => session.Address == away.Address || session.Activity == SessionActivity.Away),
            "Away never appears in the peer directory");
        check(peerStore.Any(session => session.Address == present.Address) && peerRuntime.Any(session => session.Address == present.Address),
            "WITNESS a present session appears in the peer directory");
    }

    private static void RetainedCapacity(Action<bool, string> check)
    {
        var clock = new ToolFixture.ManualClock();
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "mailbox-capacity-" + Guid.NewGuid().ToString("N"));
        using (var store = new BrokerStore(Path.Combine(root, "small"), new BrokerLimits(MaximumSessions: 4), clock))
        {
            BrokerSession Mailbox(string name) => new(new(store.BrokerId, "mcp:" + name, Guid.NewGuid()), name, "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
            BrokerSession Present(string name) => new(new(store.BrokerId, "codex:" + name, Guid.NewGuid()), name, "codex",
                SessionCapabilities.Receive, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
            var outstanding = Mailbox("oldest-with-outstanding"); store.Register(outstanding); clock.Advance(TimeSpan.FromSeconds(1));
            var older = Mailbox("older-without-outstanding"); store.Register(older); clock.Advance(TimeSpan.FromSeconds(1));
            var newer = Mailbox("newer-without-outstanding"); store.Register(newer); clock.Advance(TimeSpan.FromSeconds(1));
            var sender = Mailbox("newest-sender"); store.Register(sender);
            store.Send(sender.Address, outstanding.Address, Guid.NewGuid(), "Synthetic outstanding message.", null, 0, store.BrokerId);
            clock.Advance(TimeSpan.FromMinutes(2));
            store.Register(Present("first-present"));
            var listed = store.Directory().Select(session => session.Address).ToHashSet();
            check(!listed.Contains(older.Address) && listed.Contains(outstanding.Address) && listed.Contains(newer.Address) && listed.Contains(sender.Address),
                "the session cap evicts the oldest retained mailbox with nothing outstanding first");
            store.Register(Present("second-present"));
            store.Register(Present("third-present"));
            store.Register(Present("fourth-present"));
            check(store.Directory().Count == 4 && store.Directory().All(session => session.Adapter == "codex"),
                "once only a mailbox with an outstanding message is retained it gives up its slot too");
            bool limited = false;
            try { store.Register(Present("fifth-present")); }
            catch (BrokerException error) { limited = error.Code == "session_limit"; }
            check(limited && store.Directory().Count == 4,
                "a present session is never evicted: a store full of present sessions refuses session_limit");
        }
        using (var store = new BrokerStore(Path.Combine(root, "retained"), clock: clock))
        {
            BrokerSession Mailbox(string name) => new(new(store.BrokerId, "mcp:" + name, Guid.NewGuid()), name, "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
            var first = Mailbox("first-retained"); store.Register(first);
            clock.Advance(TimeSpan.FromSeconds(1));
            for (int index = 0; index < BrokerLimits.MaximumRetainedMailboxes; index++) store.Register(Mailbox("retained-" + index));
            clock.Advance(TimeSpan.FromMinutes(2));
            var retained = store.Directory();
            check(retained.Count == BrokerLimits.MaximumRetainedMailboxes && retained.All(session => session.Activity == SessionActivity.Away) &&
                retained.All(session => session.Address != first.Address),
                "at most 64 mailboxes are retained and the oldest lapsed one goes first");
        }
    }

    private static async Task RuntimeCapacityAndPreference(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("mailbox-runtime-capacity");
        var away = fixture.Register("Synthetic retained slot");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        fixture.Runtime.SetSessionPreference(away.Address.SessionId, true, null);
        check(fixture.Current(away.Address) is { IsPrivate: true, Activity: SessionActivity.Away } &&
            fixture.Store.RenewPollingSession(away.Address) is { IsPrivate: true },
            "a privacy choice reaches a retained mailbox without restoring its presence and survives the owner's return");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        for (int index = 0; index < 255; index++) fixture.Register("Synthetic present " + index);
        var admitted = fixture.Register("Synthetic admitted at capacity");
        check(fixture.Current(admitted.Address) is not null && fixture.Current(away.Address) is null && fixture.Store.Directory().Count == 256,
            "at the runtime's 256-session bound a new registration takes a retained mailbox's slot");
        bool limited = false;
        try { fixture.Register("Synthetic refused at capacity"); }
        catch (BrokerException error) { limited = error.Code == "session_limit"; }
        check(limited && fixture.Store.Directory().Count == 256 && fixture.Store.Directory().All(session => session.LeaseUntil > fixture.Clock.GetUtcNow()),
            "WITNESS the runtime never evicts a present session and refuses the next registration");
    }

    private static void RestartKeepsMessages(Action<bool, string> check)
    {
        var clock = new ToolFixture.ManualClock();
        string directory = Path.Combine(AppContext.BaseDirectory, "scratch", "mailbox-restart-" + Guid.NewGuid().ToString("N"));
        BrokerSession owner;
        Guid messageId = Guid.NewGuid();
        using (var store = new BrokerStore(directory, clock: clock))
        {
            owner = new(new(store.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Synthetic restarted owner", "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, clock.GetUtcNow().AddMinutes(1));
            var sender = owner with { Address = owner.Address with { SessionId = "mcp:" + Guid.NewGuid().ToString("D") }, Label = "Synthetic restarted sender" };
            store.Register(owner); store.Register(sender);
            store.Send(sender.Address, owner.Address, messageId, "Synthetic message that outlives a restart.", null, 0, store.BrokerId);
        }
        using (var reopened = new BrokerStore(directory, clock: clock))
        {
            check(reopened.Directory().Count == 0 && !reopened.IsAddressable(owner.Address),
                "a broker restart drops every mailbox registration");
            reopened.Register(owner with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) });
            check(reopened.Messages(owner.Address).Any(receipt => receipt.Message.Id == messageId && receipt.Status == MessageStatus.Accepted),
                "re-registering the same clientId and incarnation after a restart finds the mailbox's messages");
        }
    }

    // The real /federation/directory route over mutually authenticated TLS, with a synthetic paired identity.
    private static async Task FederationDirectoryOverTls(Action<bool, string> check)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "mailbox-federation-" + Guid.NewGuid().ToString("N"));
        string peerData = Path.Combine(scratch, "synthetic-peer");
        Guid peerId = Guid.NewGuid();
        var options = new WorkerOptions(scratch, "Synthetic mailbox federation", LoopbackPort: 0, LanPort: 0,
            EnableLan: true, AllowUnauthenticatedLoopback: false)
        { LanListenAddress = IPAddress.Loopback, EnableBrokerDiscovery = false };
        var keys = new List<string> { BrokerIdentity.KeyNameFor(peerData, peerId) };
        WorkerApplication? worker = null;
        BrokerIdentity? peer = null;
        try
        {
            worker = await WorkerApplication.CreateAsync(options, []);
            keys.Add(BrokerIdentity.KeyNameFor(scratch, worker.Runtime.Store.BrokerId));
            await worker.StartAsync();
            var store = worker.Runtime.Store;
            peer = BrokerIdentity.LoadOrCreate(peerData, peerId);
            store.AuthorizePeer(new PeerTrust(peerId, "Synthetic mailbox peer", "https://127.0.0.1:1/", peer.Fingerprint, DateTimeOffset.UtcNow));
            var away = new BrokerSession(new(store.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid()), "Synthetic federation mailbox", "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMilliseconds(400));
            store.Register(away);
            var present = worker.Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic federation present inbox", false);
            await Task.Delay(700);
            check(store.Directory().SingleOrDefault(session => session.Address == away.Address)?.Activity == SessionActivity.Away,
                "the federation fixture's lapsed mailbox is retained locally as Away");
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate?.GetCertHashString(HashAlgorithmName.SHA256) == worker.Runtime.Federation.LocalAdvertisement.CertificateSha256 };
            handler.ClientCertificates.Add(peer.Certificate);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(worker.Application.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal)) + "/"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var response = await client.GetAsync("federation/directory");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var sessions = json.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
            static bool Is(JsonElement session, SessionAddress address)
            {
                var item = session.GetProperty("address");
                return item.GetProperty("sessionId").GetString() == address.SessionId && item.GetProperty("incarnation").GetGuid() == address.Incarnation;
            }
            static bool IsAway(JsonElement session)
            {
                var activity = session.GetProperty("activity");
                return activity.ValueKind == JsonValueKind.Number ? activity.GetInt32() == (int)SessionActivity.Away : activity.GetString() == "Away";
            }
            check(!sessions.Any(session => Is(session, away.Address) || IsAway(session)),
                "Away never appears in the authenticated /federation/directory response");
            check(sessions.Any(session => Is(session, present.Address)),
                "WITNESS a present session appears in the authenticated /federation/directory response");
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            peer?.Dispose();
            foreach (string name in keys)
                if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent))
                { using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.Silent); key.Delete(); }
        }
    }
}
