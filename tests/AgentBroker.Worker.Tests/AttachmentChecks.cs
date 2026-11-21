using System.Security.Cryptography;
using System.Text;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;
using AgentBroker.Worker.Federation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Lane F (1.10.5): the attachment tools, the sender's source rules, staging, retention, the zone stream, the sender's
// deferral mapping and the per-broker policy. Each label names the guard a mutation must break.
internal static partial class AttachmentChecks
{
    private const string LocalOnly = "Attachments are a local operation.";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: POSTPUSH_WORKER_ATTACHMENTS_EXECUTED");
        await ToolsAsync(check);
        await DisabledAsync(check);
        await SourceAsync(check);
        await SweepAsync(check);
        await DeferralAsync(check);
        Policy(check);
        Options(check);
        Zone(check);
        await RoundTwoAsync(check);
    }

    private static string Source(string root, string name, byte[] bytes)
    {
        string directory = Path.Combine(root, "sources");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task ToolsAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-tools", attachments: true);
        var sender = fixture.Register("Synthetic attachment sender");
        var recipient = fixture.Register("Synthetic attachment recipient");
        var third = fixture.Register("Synthetic third session");
        byte[] content = Encoding.UTF8.GetBytes("Synthetic attachment content for the tool checks.");
        string path = Source(fixture.Root, "notes.txt", content);

        // W-1: a paired broker's call reads nothing, even naming a sender on its own broker (which passes CheckParticipant).
        Guid pairedBroker = Guid.NewGuid();
        var (peerContext, peerTools) = fixture.NewRequest();
        peerContext.Items["agentbroker.identity"] = pairedBroker;
        var peerSender = new SessionAddress(pairedBroker, "mcp:synthetic-paired-sender", Guid.NewGuid());
        int opened = AttachmentSource.OpenedFiles;
        check(ToolFixture.Refused(() => peerTools.SendWithAttachments(peerSender, recipient.Address, Guid.NewGuid(), "x", [path]), LocalOnly) &&
            ToolFixture.Refused(() => peerTools.FetchAttachment(peerSender, Guid.NewGuid(), 0), LocalOnly) &&
            AttachmentSource.OpenedFiles == opened,
            "ATTACHMENT_TOOLS_LOCAL_ONLY: a paired broker's caller is refused both tools and no file is opened");

        // W-5 and W-8: staged bytes, envelope and fetch come from one read, and fetch is the participants' alone. T-7: the
        // source is opened exactly once, and a rewrite between that read and the staged write (seam) changes nothing.
        Guid id = Guid.NewGuid();
        int openedBefore = AttachmentSource.OpenedFiles;
        fixture.Attachments!.BeforeStageWrite = () => File.WriteAllBytes(path, Encoding.UTF8.GetBytes("Synthetic content rewritten between the read and the copy."));
        var receipt = fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "Synthetic note.", [path]);
        fixture.Attachments.BeforeStageWrite = null;
        int openedBySend = AttachmentSource.OpenedFiles - openedBefore;
        check(AttachmentEnvelope.TryParse(receipt.Message.Text, out var envelope) == AttachmentParse.Valid &&
            envelope!.Attachments.Single() is { Name: "notes.txt" } entry && entry.Sha256 == Hash(content) && entry.Bytes == content.Length,
            "ATTACHMENT_STAGED_ENVELOPE: the message text is a valid envelope naming the file's size and SHA-256");
        string staged = Path.Combine(fixture.Attachments.OutboxFolder(id), "0-notes.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("Synthetic content rewritten after the send."));
        var fetched = fixture.Tools.FetchAttachment(recipient.Address, id, 0);
        check(openedBySend == 1 && File.ReadAllBytes(staged).AsSpan().SequenceEqual(content) && fetched is { State: "ready", Zone: "local" } &&
            fetched.Path == staged && fetched.Sha256 == Hash(content),
            "ATTACHMENT_STAGED_HASH_FROM_HANDLE: the source is opened once; the staged copy is the bytes that were hashed, even when the source changes between the read and the copy or afterwards");
        check(fixture.Tools.FetchAttachment(sender.Address, id, 0) is { State: "ready" } &&
            ToolFixture.Refused(() => fixture.Tools.FetchAttachment(third.Address, id, 0), AttachmentCodes.NotFound) &&
            ToolFixture.Refused(() => fixture.Tools.FetchAttachment(recipient.Address, id, 1), AttachmentCodes.NotFound),
            "ATTACHMENT_FETCH_OWNERSHIP: only the message's sender and recipient can fetch, and only an index it has");

        // W-7: a retry with the same content reuses the staging; different content under the same ID is a collision.
        File.WriteAllBytes(path, content);
        var retry = fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "Synthetic note.", [path]);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("Synthetic different content."));
        check(retry.Message == receipt.Message &&
            ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, id, "Synthetic note.", [path]), "message_id_collision") &&
            File.ReadAllBytes(staged).AsSpan().SequenceEqual(content),
            "ATTACHMENT_SEND_RETRY_IDEMPOTENT: the same content returns the same receipt; other content collides and leaves the staging intact");

        // W-6: only send_with_attachments may emit the envelope kind, valid or not (T-2: an invalid text claiming the kind too).
        string forged = receipt.Message.Text;
        string invalidKind = "{\"kind\":\"" + AttachmentEnvelope.Kind + "\",\"text\":\"no attachments member\"}";
        var plain = fixture.Tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), "{\"kind\":\"other\",\"text\":\"ordinary JSON\"}");
        bool Reserved(string text) =>
            ToolFixture.Refused(() => fixture.Tools.Send(sender.Address, recipient.Address, Guid.NewGuid(), text), AttachmentCodes.EnvelopeReserved) &&
            ToolFixture.Refused(() => fixture.Tools.SendMessage(new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address, text,
                fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow().AddMinutes(5))), AttachmentCodes.EnvelopeReserved) &&
            ToolFixture.Refused(() => fixture.Tools.ReplyToMessage(plain.Message.Id, Guid.NewGuid(), text), AttachmentCodes.EnvelopeReserved);
        check(AttachmentEnvelope.TryParse(invalidKind, out _) == AttachmentParse.Invalid && Reserved(forged) && Reserved(invalidKind),
            "ATTACHMENT_ENVELOPE_RESERVED: send, send_message and reply refuse envelope text; ordinary JSON text still sends");
    }

    // W-2.
    private static async Task DisabledAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-disabled", attachments: false);
        var sender = fixture.Register("Synthetic disabled sender");
        var recipient = fixture.Register("Synthetic disabled recipient");
        string path = Source(fixture.Root, "notes.txt", "Synthetic."u8.ToArray());
        int opened = AttachmentSource.OpenedFiles;
        check(ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [path]),
                AttachmentCodes.Disabled) && AttachmentSource.OpenedFiles == opened && !Directory.Exists(fixture.Attachments!.OutboxRoot),
            "ATTACHMENT_DISABLED_REFUSED: attachments off refuses before any file is read or staged");
    }

    // W-3 and W-4: what the sender refuses to copy.
    private static async Task SourceAsync(Action<bool, string> check)
    {
        await using var fixture = new ToolFixture("attachments-source", attachments: true);
        var sender = fixture.Register("Synthetic source sender");
        var recipient = fixture.Register("Synthetic source recipient");
        bool Refuses(string code, params string[] files) =>
            ToolFixture.Refused(() => fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", files), code);

        string target = Path.Combine(fixture.Root, "junction-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "Synthetic.");
        string chain = Path.Combine(fixture.Root, "junction-chain");
        HandoffClientChecks.CreateJunction(chain, target);
        string leafTarget = Path.Combine(fixture.Root, "leaf-target");
        Directory.CreateDirectory(leafTarget);
        string leaf = Path.Combine(fixture.Root, "sources", "leaf-junction");
        Directory.CreateDirectory(Path.GetDirectoryName(leaf)!);
        HandoffClientChecks.CreateJunction(leaf, leafTarget);
        // The links go before the fixture's scratch cleanup, which refuses any linked path.
        try
        {
            check(Refuses(AttachmentCodes.SourceInvalid, Path.Combine(chain, "inside.txt")) && Refuses(AttachmentCodes.SourceInvalid, leaf),
                "ATTACHMENT_SOURCE_REPARSE_REFUSED: a reparse point in the path, or as the named file, is refused");
        }
        finally { Directory.Delete(chain); Directory.Delete(leaf); }
        // T-3: a regular FILE that is a reparse point isolates the reparse bit of the opened handle's attributes (a junction leaf
        // is also a directory). A file symbolic link needs developer mode; a non-Microsoft reparse tag needs no privilege, so
        // that is what this check sets. If even that is refused, the check is skipped LOUDLY.
        string reparseFile = Source(fixture.Root, "reparse-file.txt", "Synthetic content behind a reparse tag."u8.ToArray());
        if (!ReparseFile.TrySet(reparseFile))
            Console.WriteLine("SKIP: ATTACHMENT_SOURCE_FILE_REPARSE_REFUSED: this volume refused a reparse tag on a file");
        else
        {
            try
            {
                check(Refuses(AttachmentCodes.SourceInvalid, reparseFile),
                    "ATTACHMENT_SOURCE_FILE_REPARSE_REFUSED: a regular file that is a reparse point (no directory bit), named as the source, is refused");
            }
            finally { ReparseFile.Remove(reparseFile); }
        }
        string identityFile = Path.Combine(fixture.Attachments!.DataDirectory, "identity-like.bin");
        Directory.CreateDirectory(fixture.Attachments.DataDirectory);
        File.WriteAllText(identityFile, "Synthetic broker-owned file.");
        check(Refuses(AttachmentCodes.SourceInvalid, identityFile) && Refuses(AttachmentCodes.SourceInvalid, "relative.txt") &&
            Refuses(AttachmentCodes.SourceInvalid, Source(fixture.Root, "stream.txt", "x"u8.ToArray()) + ":hidden") &&
            Refuses(AttachmentCodes.SourceInvalid, @"\\localhost\c$\Windows\win.ini") && Refuses(AttachmentCodes.SourceInvalid),
            "ATTACHMENT_SOURCE_INVALID: the broker's own data, relative, stream, UNC paths and an empty list are refused");
        string oversize = Source(fixture.Root, "oversize.bin", new byte[AttachmentEnvelope.MaximumFileBytes + 1]);
        string six = Source(fixture.Root, "six.bin", new byte[6 * 1024 * 1024]);
        string small = Source(fixture.Root, "small.txt", "x"u8.ToArray());
        check(Refuses(AttachmentCodes.TooLarge, oversize) && Refuses(AttachmentCodes.TooLarge, six, six, six) &&
            Refuses(AttachmentCodes.TooLarge, small, small, small, small, small) && !Directory.Exists(fixture.Attachments.OutboxRoot),
            "ATTACHMENT_SOURCE_SIZE_BOUNDS: over 8 MiB per file, 16 MiB per message or four files is refused, with nothing staged");
    }

    // W-9: retention removes what is finished, keeps what is live, and never follows a reparse point.
    private static async Task SweepAsync(Action<bool, string> check)
    {
        var clock = new NearRealClock();
        await using var fixture = new ToolFixture("attachments-sweep", clock: clock, attachments: true);
        var store = fixture.Attachments!;
        var sender = fixture.Register("Synthetic sweep sender");
        var recipient = fixture.Register("Synthetic sweep recipient");
        string path = Source(fixture.Root, "notes.txt", "Synthetic sweep content."u8.ToArray());
        var refused = fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [path]);
        var live = fixture.Tools.SendWithAttachments(sender.Address, recipient.Address, Guid.NewGuid(), "x", [path]);
        fixture.Store.RecordRecipientReceipt(refused.Message.Id, recipient.Address, fixture.Store.BrokerId, MessageStatus.Refused);
        string foreign = Path.Combine(store.OutboxRoot, "not-a-message");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "Synthetic.");
        string target = Path.Combine(fixture.Root, "sweep-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "target.txt"), "Synthetic.");
        string brokerFolder = Path.Combine(store.InboxRoot, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(brokerFolder);
        string junction = Path.Combine(brokerFolder, Guid.NewGuid().ToString("D"));
        HandoffClientChecks.CreateJunction(junction, target);
        string orphan = Path.Combine(brokerFolder, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "0-orphan.txt"), "Synthetic.");
        try
        {
            store.Sweep();
            check(!Directory.Exists(store.OutboxFolder(refused.Message.Id)) && Directory.Exists(store.OutboxFolder(live.Message.Id)),
                "ATTACHMENT_SWEEP: a refused message's staging goes and a live one stays");
            check(File.Exists(Path.Combine(foreign, "keep.txt")) && File.Exists(Path.Combine(target, "target.txt")) && Directory.Exists(orphan),
                "ATTACHMENT_SWEEP: a folder that is not a message ID, a junction's target, and a young unadmitted pull are all left alone");
            clock.Advance(AttachmentStore.OrphanGrace + TimeSpan.FromMinutes(1));
            store.Sweep();
            check(!Directory.Exists(orphan) && File.Exists(Path.Combine(target, "target.txt")) && Directory.Exists(junction),
                "ATTACHMENT_SWEEP: an unadmitted pull's folder goes once its grace passes; a junction and its target never do");
        }
        finally { Directory.Delete(junction); }
    }

    // W-11 and D-2: the recipient's codes reach the sender as a deferral or a refusal, never Uncertain.
    private static async Task DeferralAsync(Action<bool, string> check)
    {
        Guid peerId = Guid.NewGuid();
        check(PeerAdmissionRefusal.TryRead("{\"error\":\"attachment_fetch_pending\"}"u8, peerId, out var pending) && pending!.Code == AttachmentCodes.FetchPending &&
            PeerAdmissionRefusal.TryRead("{\"error\":\"attachment_mismatch\"}"u8, peerId, out _) &&
            !PeerAdmissionRefusal.TryRead("{\"error\":\"attachment_future_code\"}"u8, peerId, out _),
            "ATTACHMENT_REFUSAL_CODES_TRAVEL: the attachment admission codes are read as refusals; an unknown code stays unknown");
        using var scratchOwner = OwnedScratch.Create("attachments-deferral");
        using var store = new BrokerStore(Path.Combine(scratchOwner.Root, "store"));
        store.AuthorizePeer(new(peerId, "Synthetic paired broker", "https://192.0.2.12/", new string('B', 64), DateTimeOffset.UtcNow));
        var sender = new BrokerSession(new(store.BrokerId, "claude:attachment-deferral-sender", Guid.NewGuid()), "Synthetic deferral sender",
            "synthetic", SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Busy, DateTimeOffset.UtcNow.AddMinutes(2));
        store.Register(sender);
        var federation = new RefusingPeer();
        await using var runtime = new BrokerRuntime(store, federation, []);
        using var stop = new CancellationTokenSource();
        runtime.Start(stop.Token);
        BrokerMessage Message(string recipient, string text) => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address,
            new(peerId, recipient, Guid.NewGuid()), text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        var deferred = Message("codex:attachment-pending", AttachmentCodes.FetchPending);
        var refused = Message("codex:attachment-mismatch", AttachmentCodes.Mismatch);
        store.Accept(deferred, store.BrokerId);
        store.Accept(refused, store.BrokerId);
        // T-6: the release itself must be SEEN (Accepted, attachment_fetch_pending, zero attempts), and a second forward must
        // follow it (the 5 s backoff), never Uncertain in between.
        bool sawDeferral = false, sawUncertain = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline &&
               !(sawDeferral && Volatile.Read(ref federation.Pending) >= 2 && store.FindReceipt(refused.Id)?.Status == MessageStatus.Refused))
        {
            var current = store.FindReceipt(deferred.Id);
            sawDeferral |= current is { Status: MessageStatus.Accepted, Reason: AttachmentCodes.FetchPending, Attempts: 0 };
            sawUncertain |= current?.Status == MessageStatus.Uncertain;
            await Task.Delay(25);
        }
        check(sawDeferral && !sawUncertain && Volatile.Read(ref federation.Pending) >= 2,
            "ATTACHMENT_DEFERRAL_MAPPED: attachment_fetch_pending releases the message unsubmitted for a later forward");
        check(store.FindReceipt(refused.Id) is { Status: MessageStatus.Refused, Reason: AttachmentCodes.Mismatch },
            "ATTACHMENT_DEFERRAL_MAPPED: every other attachment code is a definite refusal under its own reason");
        stop.Cancel();
    }

    // W-12 (D-9).
    private static void Policy(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("attachments-policy");
        string root = scratchOwner.Root;
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic", "https://192.0.2.13/", new string('C', 64), DateTimeOffset.UtcNow);
        var policy = new AttachmentPolicy(root);
        check(!policy.Allows(peer), "ATTACHMENT_POLICY_SIDECAR: pairing alone allows nothing");
        policy.Set(peer, true);
        check(policy.Allows(peer) && new AttachmentPolicy(root).Allows(peer) && !policy.Allows(peer with { CertificateSha256 = new string('D', 64) }),
            "ATTACHMENT_POLICY_SIDECAR: an allow survives a restart and is pinned to the broker's current certificate");
        policy.Remove(peer.BrokerId);
        check(!policy.Allows(peer) && !new AttachmentPolicy(root).Allows(peer), "ATTACHMENT_POLICY_SIDECAR: removal is durable");
        File.WriteAllText(Path.Combine(root, AttachmentPolicy.FileName), "{ not json");
        var corrupt = new AttachmentPolicy(root);
        check(corrupt.Unreadable && !corrupt.Allows(peer), "ATTACHMENT_POLICY_SIDECAR: an unreadable sidecar denies every broker");
        corrupt.Set(peer, true);
        check(!corrupt.Unreadable && corrupt.Allows(peer), "ATTACHMENT_POLICY_SIDECAR: the owner's next explicit choice rewrites an unreadable sidecar");
    }

    // W-13.
    private static void Options(Action<bool, string> check)
    {
        string data = Path.Combine(Path.GetTempPath(), "agentbroker-options-not-created");
        bool duplicate = false;
        try { WorkerOptions.Parse(["--data", data, "--attachments", "true", "--attachments", "false"]); }
        catch (ArgumentException) { duplicate = true; }
        check(!WorkerOptions.Parse(["--data", data]).EnableAttachments && WorkerOptions.Parse(["--data", data, "--attachments", "true"]).EnableAttachments &&
            duplicate, "ATTACHMENT_OPTION_DEFAULT_OFF: attachments are off unless --attachments true is passed once");
    }

    // W-10: the Mark of the Web is on the temporary before it is renamed into place, and only when asked for.
    private static void Zone(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("attachments-zone");
        string directory = Path.Combine(scratchOwner.Root, "out");
        Directory.CreateDirectory(directory);
        string leaf = ".agentbroker-output-zone-check.tmp";
        bool sawZone = false;
        string marked = Path.Combine(directory, "marked.bin");
        LocalOutputFile.Save(marked, "Synthetic marked content."u8.ToArray(), beforePublish: () =>
        {
            try
            {
                using var stream = new FileStream(Path.Combine(directory, leaf) + ":Zone.Identifier", FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                sawZone = reader.ReadToEnd().Contains("ZoneId=3", StringComparison.Ordinal);
            }
            catch (FileNotFoundException) { sawZone = false; } // no stream yet: the check below names the failure
        }, seams: new LocalOutputFile.Seams { TempLeaf = () => leaf }, zoneIdentifier: AttachmentStore.InternetZone);
        string plain = Path.Combine(directory, "plain.bin");
        LocalOutputFile.Save(plain, "Synthetic plain content."u8.ToArray());
        check(sawZone && File.Exists(marked + ":Zone.Identifier") && File.ReadAllText(marked + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal) &&
            File.ReadAllText(marked) == "Synthetic marked content." && !File.Exists(plain + ":Zone.Identifier"),
            "ATTACHMENT_ZONE_BEFORE_PUBLISH: the zone stream exists on the temporary before the rename, survives it, and is written only when asked");
    }

    // Starts at the real time, so folder creation times and the store clock agree; Advance moves only this clock.
    private sealed class NearRealClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private sealed class RefusingPeer : IPeerFederation
    {
        internal int Pending;
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:15002", new string('E', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication application) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid requestId, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid requestId) { }
        public void RevokePeer(Guid brokerId) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) => Task.FromResult(new PeerDirectory(peer.BrokerId, []));
        // The message text names the code this synthetic paired broker answers with.
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token)
        {
            if (message.Text == AttachmentCodes.FetchPending) Interlocked.Increment(ref Pending);
            throw new PeerAdmissionRefusedException(peer.BrokerId, message.Text);
        }
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid messageId, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
