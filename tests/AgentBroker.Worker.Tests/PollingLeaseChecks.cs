using AgentBroker.Core;
using AgentBroker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

internal static class PollingLeaseChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using (var fixture = new Fixture())
        {
            var session = fixture.Register("Synthetic active poller");
            for (int index = 0; index < 10; index++)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(20));
                check(fixture.Tools.ReadInbox(session.Address).Count == 0,
                    "an empty successful inbox poll renews its live exact polling session");
            }
            var live = fixture.Current(session.Address);
            check(live is not null && fixture.Clock.GetUtcNow() > session.LeaseUntil &&
                live.LeaseUntil == fixture.Clock.GetUtcNow().AddMinutes(1),
                "active polling remains available beyond the original sixty-second lease without sleeping");
            fixture.Clock.Advance(TimeSpan.FromSeconds(61));
            // Presence still lapses after sixty seconds; the mailbox behind it is retained and listed as Away.
            var lapsed = fixture.Current(session.Address);
            check(lapsed is { Activity: SessionActivity.Away } && lapsed.LeaseUntil < fixture.Clock.GetUtcNow() &&
                !fixture.Store.Directory(forPeer: true).Any(peer => peer.Address == session.Address),
                "a polling inbox's presence lapses sixty seconds after activity stops and its mailbox is listed locally as Away");
            fixture.Tools.ReadInbox(session.Address);
            check(fixture.Current(session.Address) is { Activity: SessionActivity.Idle } restored &&
                restored.LeaseUntil == fixture.Clock.GetUtcNow().AddMinutes(1),
                "a history read of a retained mailbox restores its presence");
            fixture.Clock.Advance(BrokerLimits.MailboxRetention + TimeSpan.FromSeconds(1));
            fixture.Tools.ReadInbox(session.Address);
            check(fixture.Current(session.Address) is null, "a history read cannot revive a mailbox after its 24-hour retention");
            var replacement = fixture.Runtime.RegisterPollingAgent(Guid.Parse(session.Address.SessionId[4..]),
                session.Address.Incarnation, session.Label, session.IsPrivate);
            check(fixture.Current(replacement.Address) is not null, "explicit registration can restore an expired polling inbox");
        }

        await using (var fixture = new Fixture())
        {
            var session = fixture.Register("Synthetic preference poller");
            fixture.Runtime.SetSessionPreference(session.Address.SessionId, true, true);
            var before = fixture.Current(session.Address)!;
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            fixture.Tools.ReadInbox(session.Address);
            var after = fixture.Current(session.Address)!;
            check(after == before with { LeaseUntil = after.LeaseUntil } && after.LeaseUntil > before.LeaseUntil,
                "renewal preserves the complete address label privacy activity capabilities and adapter");
            fixture.Runtime.SetSessionPreference(session.Address.SessionId, false, false);
            var unmuted = fixture.Current(session.Address)!;
            check(!unmuted.IsPrivate && unmuted.Activity == SessionActivity.Idle,
                "renewal retains the registration beneath privacy and pause preference overlays");
        }

        await using (var fixture = new Fixture())
        {
            var session = fixture.Register("Synthetic negative poller");
            var original = fixture.Current(session.Address)!;
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Tools.ListSessions(default);
            fixture.Tools.GetMessageReceipt(Guid.NewGuid());
            check(fixture.Current(session.Address) == original,
                "directory and receipt inspection cannot keep an unrelated inbox alive");
            ExpectFailure(() => fixture.Tools.ReadInbox(session.Address, 17), check,
                "an invalid inbox limit fails before renewal");
            ExpectFailure(() => fixture.Tools.ReadInbox(session.Address with { Incarnation = Guid.Empty }), check,
                "an incomplete session address fails before renewal");
            ExpectFailure(() => fixture.Tools.AcknowledgeMessage(Guid.NewGuid(), session.Address, MessageStatus.Delivered), check,
                "a missing acknowledgement target fails before renewal");
            ExpectFailure(() => fixture.Tools.ReplyToMessage(Guid.NewGuid(), Guid.NewGuid(), "Synthetic reply"), check,
                "a missing reply target fails before renewal");
            var invalid = fixture.Message(session.Address, session.Address) with { Text = new string('x', 17 * 1024) };
            ExpectFailure(() => fixture.Tools.SendMessage(invalid), check, "a rejected message payload fails before sender renewal");
            fixture.Context.Items["agentbroker.identity"] = Guid.NewGuid();
            ExpectFailure(() => fixture.Tools.ReadInbox(session.Address), check,
                "a different authenticated broker cannot renew a local polling inbox");
            fixture.Context.Items.Remove("agentbroker.identity");
            ExpectFailure(() => fixture.Tools.ReadInbox(session.Address), check,
                "missing transport identity cannot renew a local polling inbox");
            fixture.Context.Items["agentbroker.identity"] = fixture.Store.BrokerId;
            check(fixture.Current(session.Address) == original,
                "all rejected operations leave the prior polling lease unchanged");
            var unknown = session.Address with { SessionId = "mcp:" + Guid.NewGuid().ToString("D") };
            fixture.Tools.ReadInbox(unknown);
            fixture.Tools.ReadInbox(session.Address with { Incarnation = Guid.NewGuid() });
            fixture.Runtime.RenewPollingAgent(session.Address with { BrokerId = Guid.NewGuid() }, fixture.Store.BrokerId);
            fixture.Runtime.RenewPollingAgent(session.Address, Guid.NewGuid());
            check(fixture.Store.Directory().SequenceEqual([original]),
                "unknown foreign and wrong-incarnation activity cannot create or extend any inbox");
            fixture.Store.Disconnect(session.Address);
            fixture.Tools.ReadInbox(session.Address);
            check(fixture.Store.Directory().Count == 0,
                "the atomic store renewal cannot restore a disconnected observed inbox");
        }

        await using (var fixture = new Fixture())
        {
            var sender = fixture.Register("Synthetic sender");
            var recipient = fixture.Register("Synthetic recipient");
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            var receipt = fixture.Tools.SendMessage(fixture.Message(sender.Address, recipient.Address));
            var sentLease = fixture.Current(sender.Address)!.LeaseUntil;
            check(sentLease == fixture.Clock.GetUtcNow().AddMinutes(1) &&
                fixture.Current(recipient.Address)!.LeaseUntil == recipient.LeaseUntil,
                "a successful send renews only the exact sender and never its recipient");
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            fixture.Tools.AcknowledgeMessage(receipt.Message.Id, recipient.Address, MessageStatus.Delivered);
            var acknowledgedLease = fixture.Current(recipient.Address)!.LeaseUntil;
            check(acknowledgedLease == fixture.Clock.GetUtcNow().AddMinutes(1) &&
                fixture.Current(sender.Address)!.LeaseUntil == sentLease,
                "a successful acknowledgement renews only the exact receiving session");
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            fixture.Tools.ReplyToMessage(receipt.Message.Id, Guid.NewGuid(), "Synthetic reply");
            check(fixture.Current(recipient.Address)!.LeaseUntil > acknowledgedLease &&
                fixture.Current(sender.Address)!.LeaseUntil == sentLease,
                "a successful reply renews only its exact replying session");
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            var beforeError = fixture.Current(recipient.Address)!;
            ExpectFailure(() => fixture.Tools.AcknowledgeMessage(receipt.Message.Id, recipient.Address, MessageStatus.Accepted), check,
                "an invalid acknowledgement outcome cannot renew its recipient");
            check(fixture.Current(recipient.Address) == beforeError, "failed acknowledgements leave the receiving lease unchanged");
        }

        await using (var fixture = new Fixture())
        {
            var old = fixture.Register("Synthetic previous incarnation");
            using var release = new ManualResetEventSlim();
            var oldCalls = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                release.Wait();
                fixture.Tools.ReadInbox(old.Address);
            })).ToArray();
            var current = fixture.Runtime.RegisterPollingAgent(Guid.Parse(old.Address.SessionId[4..]), Guid.NewGuid(),
                "Synthetic current incarnation", true);
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            release.Set();
            await Task.WhenAll(oldCalls).WaitAsync(TimeSpan.FromSeconds(5));
            check(fixture.Store.Directory().SequenceEqual([current]),
                "old concurrent inbox calls cannot overwrite or renew a newly registered incarnation");
            fixture.Tools.ReadInbox(current.Address);
            check(fixture.Current(current.Address)!.LeaseUntil > current.LeaseUntil && fixture.Current(old.Address) is null,
                "the replacement incarnation can renew itself while its predecessor stays absent");
            var direct = current with { Address = current.Address with { Incarnation = Guid.NewGuid() }, Label = "Synthetic store replacement" };
            fixture.Store.Register(direct);
            fixture.Tools.ReadInbox(current.Address);
            check(fixture.Store.Directory().SequenceEqual([direct]),
                "an atomic exact-address renewal cannot displace a replacement registered directly in the store");
        }

        await using (var fixture = new Fixture())
        {
            var expired = fixture.Register("Synthetic retained expired inbox");
            fixture.Clock.Advance(TimeSpan.FromSeconds(61));
            check(fixture.Store.RenewPollingSession(expired.Address) is { } back &&
                back.LeaseUntil == fixture.Clock.GetUtcNow().AddMinutes(1) && fixture.Current(expired.Address)?.Activity == SessionActivity.Idle,
                "atomic renewal of a lapsed but retained mailbox restores its presence before any directory pruning occurs");
            fixture.Clock.Advance(BrokerLimits.MailboxRetention + TimeSpan.FromSeconds(1));
            check(fixture.Store.RenewPollingSession(expired.Address) is null && fixture.Store.Directory().Count == 0,
                "atomic renewal refuses a mailbox whose 24-hour retention ended before any directory pruning occurs");
        }

        await using (var fixture = new Fixture())
        {
            var native = new BrokerSession(new(fixture.Store.BrokerId, "codex:synthetic", Guid.NewGuid()),
                "Synthetic native", "codex", SessionCapabilities.Receive | SessionCapabilities.WakeIdle,
                SessionActivity.Idle, fixture.Clock.GetUtcNow().AddMinutes(1));
            fixture.Store.Register(native);
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            fixture.Tools.ReadInbox(native.Address);
            check(fixture.Store.RenewPollingSession(native.Address) is null && fixture.Current(native.Address) == native,
                "polling activity and the core primitive cannot renew native adapter leases");
            var disconnected = fixture.Register("Synthetic disconnected state");
            fixture.Store.Register(disconnected with { Activity = SessionActivity.Disconnected });
            fixture.Tools.ReadInbox(disconnected.Address);
            check(fixture.Current(disconnected.Address)?.Activity == SessionActivity.Disconnected &&
                fixture.Current(disconnected.Address)?.LeaseUntil == disconnected.LeaseUntil,
                "a disconnected stored activity cannot be converted into a live polling lease");
            var longLease = fixture.Register("Synthetic longer lease") with { LeaseUntil = fixture.Clock.GetUtcNow().AddMinutes(2) };
            fixture.Store.Register(longLease);
            fixture.Tools.ReadInbox(longLease.Address);
            check(fixture.Current(longLease.Address) == longLease, "renewal never shortens an existing longer valid lease");
            fixture.Clock.Advance(TimeSpan.FromMinutes(3));
            fixture.Tools.ReadInbox(native.Address);
            check(fixture.Current(native.Address) is null && fixture.Current(disconnected.Address) is null,
                "a lapsed native lease and a lapsed disconnected inbox are gone rather than retained as mailboxes");
            check(fixture.Current(longLease.Address) is { Activity: SessionActivity.Away },
                "WITNESS a lapsed generic inbox in the same store is retained as an Away mailbox");
        }
    }

    private static void ExpectFailure(Action action, Action<bool, string> check, string name)
    {
        try { action(); }
        catch (Exception error) when (error is BrokerException or McpException) { check(true, name); return; }
        throw new InvalidOperationException("Expected rejection: " + name);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ManualClock Clock { get; } = new();
        internal BrokerStore Store { get; }
        internal BrokerRuntime Runtime { get; }
        internal DefaultHttpContext Context { get; } = new();
        internal BrokerTools Tools { get; }
        internal Fixture()
        {
            string root = Path.Combine(AppContext.BaseDirectory, "scratch", "polling-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Store = new BrokerStore(Path.Combine(root, "store"), clock: Clock);
            Runtime = new BrokerRuntime(Store, new NoPeers(), [], new SessionPreferences(root), Clock);
            Context.Items["agentbroker.identity"] = Store.BrokerId;
            Tools = new BrokerTools(Runtime, new HttpContextAccessor { HttpContext = Context });
        }
        internal BrokerSession Register(string label) => Runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), label, false);
        internal BrokerSession? Current(SessionAddress address) => Store.Directory().SingleOrDefault(session => session.Address == address);
        internal BrokerMessage Message(SessionAddress sender, SessionAddress recipient) => new(Guid.NewGuid(), Guid.NewGuid(), null,
            sender, recipient, "Synthetic polling message", Clock.GetUtcNow(), Clock.GetUtcNow().AddHours(1));
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Store.Dispose(); }
    }

    private sealed class NoPeers : IPeerFederation
    {
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => throw new NotSupportedException();
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
