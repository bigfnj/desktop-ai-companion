using System.Security.Cryptography;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Attachments;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;

// Lane F round 6 (DECISIONS 116): the race interleavings the owner chose to fix in 1.10.5 (SHEET S-017 item 5, S-018, S-019)
// and the defense-in-depth line S-013b, between real paired nodes over mutual TLS on loopback. Each label names the guard a
// hygiene recipe (attachment-r6-*) breaks by reverting it.
internal static partial class AttachmentFederationChecks
{
    private static async Task RoundSixAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        await AbandonWaitsForAdmissionAsync(scratch, identity, check);
        await AllowRaceKeepsAdmittedAsync(scratch, identity, check);
        await OneTextRaceAsync(scratch, identity, check);
        await DropKeepsNewerReservationAsync(scratch, identity, check);
    }

    // S-017, item 5: forward A is between its recheck and its admission (it holds the admission gate) when forward B of the same
    // message, past the recipient's 10-minute bound, ends in Abandon. B's deletion waits for that gate, keeps the folder A
    // admitted, and B answers the admitted receipt: the message is never admitted with a file missing, nor its files deleted
    // after. B is made to find no room: inside A's admission another message's 2500-byte verified partial from the same broker
    // lands in the quarantine, and with the broker's 6000-byte in-flight share that leaves no room for B's 4000 bytes (a
    // deferral, which past the bound is Abandon). Since round 6 (S-020) a message's own kept files never cause that deferral.
    private static async Task AbandonWaitsForAdmissionAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "r6-abandon", clock, identity, recipientLimits: new(PerPeerInFlightBytes: 6000));
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r6-abandon-sender");
            var recipient = b.Register("att-r6-abandon-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r6-abandon", "data.bin", RandomNumberGenerator.GetBytes(4000)));
            string file = Path.Combine(Folder(b, a, message.Id), "0-data.bin");
            Task<string>? second = null;
            bool secondInside = true, fileAtAdmission = false;
            int admissions = 0;
            b.Federation.BeforeAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) != 1) return;
                clock.Advance(PeerFederation.AttachmentDeferralBound + TimeSpan.FromMinutes(1));
                Renew(b, recipient, clock);
                byte[] filler = RandomNumberGenerator.GetBytes(2500);
                var fillerEntry = new AttachmentEntry(0, "filler.bin", filler.LongLength, Convert.ToHexStringLower(SHA256.HashData(filler)));
                b.Attachments!.PublishVerified(a.Store.BrokerId, Guid.NewGuid(), fillerEntry, filler,
                    AttachmentEnvelope.Compose("Synthetic partial of another message.", [fillerEntry]));
                second = Task.Run(() => Submit(a, b, message));
                secondInside = second.Wait(TimeSpan.FromSeconds(3));
                fileAtAdmission = File.Exists(file);
            };
            string first;
            try { first = await Submit(a, b, message); }
            finally { b.Federation.BeforeAdmission = null; }
            string secondAnswer = second is null ? "not started" : await second.WaitAsync(TimeSpan.FromSeconds(30));
            bool kept = File.Exists(file) && b.Store.FindReceipt(message.Id) is not null;
            bool held = first == Admitted && !secondInside && fileAtAdmission && secondAnswer == Admitted && kept;
            if (!held)
                Console.WriteLine("OBSERVED: ATTACHMENT_ABANDON_WAITS_FOR_ADMISSION first " + first + "; second " + secondAnswer + "; second inside the admission " +
                    secondInside + "; file at admission " + fileAtAdmission + "; file and receipt after " + kept);
            check(held,
                "ATTACHMENT_ABANDON_WAITS_FOR_ADMISSION: a forward that passes the 10-minute bound while another forward of the same message is being admitted waits for that admission, keeps its files and answers the admitted receipt");
        }
    }

    // S-018, item 10: two forwards of one envelope share one pull, whose file request the recipient's own client holds (the
    // AttachmentHandler seam). The first to reach the admission gate admits; the owner withdraws the allow before the second
    // reaches its allow check. The second answers the admitted receipt and the admitted folder stays.
    private static async Task AllowRaceKeepsAdmittedAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "r6-allowrace", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r6-allowrace-sender");
            var recipient = b.Register("att-r6-allowrace-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r6-allowrace", "data.txt", "Synthetic shared content."u8.ToArray()));
            string file = Path.Combine(Folder(b, a, message.Id), "0-data.txt");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int fetches = 0, posts = 0, admissions = 0;
            b.Federation.AttachmentHandler = inner => new HeldHandler(inner, release.Task, () => Interlocked.Increment(ref fetches));
            b.Pairing.BeforeMessages = _ => { Interlocked.Increment(ref posts); return Task.FromResult(false); };
            b.Federation.BeforeAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == 2) b.Attachments!.Policy.Remove(a.Store.BrokerId);
            };
            int pulls = Volatile.Read(ref b.Federation.AttachmentPullsStarted);
            var first = Submit(a, b, message);
            Task<string> second;
            try
            {
                await Until(() => Volatile.Read(ref fetches) >= 1, "ATTACHMENT_ALLOW_RACE_KEEPS_ADMITTED: the shared pull's file request is held",
                    () => "fetches " + Volatile.Read(ref fetches));
                second = Submit(a, b, message);
                await Until(() => Volatile.Read(ref posts) >= 2, "ATTACHMENT_ALLOW_RACE_KEEPS_ADMITTED: the second forward reaches the recipient",
                    () => "posts " + Volatile.Read(ref posts));
                await Task.Delay(500); // the second forward joins the running pull a few milliseconds after it arrives
            }
            finally { release.TrySetResult(); }
            string firstAnswer = await first, secondAnswer = await second;
            b.Federation.BeforeAdmission = null;
            b.Federation.AttachmentHandler = null;
            b.Pairing.BeforeMessages = null;
            int started = Volatile.Read(ref b.Federation.AttachmentPullsStarted) - pulls;
            bool held = firstAnswer == Admitted && secondAnswer == Admitted && admissions == 2 && started == 1 && File.Exists(file) &&
                b.Store.FindReceipt(message.Id) is not null;
            if (!held)
                Console.WriteLine("OBSERVED: ATTACHMENT_ALLOW_RACE_KEEPS_ADMITTED first " + firstAnswer + "; second " + secondAnswer + "; admissions " + admissions +
                    "; pulls started " + started + "; file " + File.Exists(file));
            check(held,
                "ATTACHMENT_ALLOW_RACE_KEEPS_ADMITTED: an allow withdrawn after one forward admitted a message and before another forward of it reaches its allow check: the other forward answers the admitted receipt and the files stay");
        }
    }

    // S-019, item 17: two different envelope texts under one message ID, with the same files. Forward A is held right after the
    // one-text check (the BeforePullLookup seam, DECISIONS 119) until forward B, the other text, has passed that check too; then
    // A registers the pull with its text and B meets that pull. B is refused attachment_envelope_invalid while the pull runs; A
    // is admitted, and the admitted text is the envelope the folder's manifest records.
    private static async Task OneTextRaceAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "r6-onetext", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r6-onetext-sender");
            var recipient = b.Register("att-r6-onetext-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r6-onetext", "data.txt", "Synthetic raced content."u8.ToArray()));
            var other = message with { Text = AttachmentEnvelope.Compose("A different synthetic note.", AttachmentEnvelopeOf(message).Attachments) };
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new Requests(a) { Behaviour = async (_, _) => { await hold.Task; return false; } };
            using var otherArrived = new ManualResetEventSlim();
            int pulls = Volatile.Read(ref b.Federation.AttachmentPullsStarted), lookups = 0;
            bool firstWaited = false, joined = false;
            b.Federation.BeforePullLookup = () =>
            {
                switch (Interlocked.Increment(ref lookups))
                {
                    case 1: // forward A, past the one-text check: wait for forward B to pass it too
                        firstWaited = otherArrived.Wait(TimeSpan.FromSeconds(10));
                        break;
                    case 2: // forward B: let A register its pull, then meet it
                        otherArrived.Set();
                        joined = SpinWait.SpinUntil(() => Volatile.Read(ref b.Federation.AttachmentPullsStarted) > pulls, TimeSpan.FromSeconds(10));
                        break;
                }
            };
            var first = Submit(a, b, message);
            string secondAnswer;
            try
            {
                await Until(() => Volatile.Read(ref lookups) >= 1, "ATTACHMENT_ONE_TEXT_RACE_BOUND_TO_PULL: the first forward has passed the one-text check",
                    () => "lookups " + Volatile.Read(ref lookups));
                secondAnswer = await Submit(a, b, other);
            }
            finally
            {
                b.Federation.BeforePullLookup = null;
                hold.TrySetResult();
            }
            string firstAnswer = await first;
            static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            string? admittedText = b.Store.FindReceiptWithText(message.Id)?.Message.Text;
            string? manifest = b.Attachments!.ReceivedEnvelopeSha256(a.Store.BrokerId, message.Id);
            bool held = firstWaited && joined && firstAnswer == Admitted && secondAnswer == AttachmentCodes.EnvelopeInvalid &&
                admittedText == message.Text && manifest == Sha(message.Text) && requests.For(message.Id) >= 1;
            if (!held)
                Console.WriteLine("OBSERVED: ATTACHMENT_ONE_TEXT_RACE_BOUND_TO_PULL first waited " + firstWaited + "; joined " + joined + "; first " + firstAnswer +
                    "; second " + secondAnswer + "; admitted text is first " + (admittedText == message.Text) + "; manifest is first " + (manifest == Sha(message.Text)));
            check(held,
                "ATTACHMENT_ONE_TEXT_RACE_BOUND_TO_PULL: a second text under a message ID that passed the one-text check before the first text's pull was registered is refused attachment_envelope_invalid by that pull; the admitted text is the manifest's envelope");
        }
    }

    // S-013b: forward A holds a finished Verified state whose file no longer hashes. Between A's recheck and its Drop, forward C
    // replaces that state with a fresh pull and its own reservation. A's Drop leaves C's state and reservation alone: a second
    // message's reservation, which fits the broker's in-flight share only if C's is gone, still defers.
    private static async Task DropKeepsNewerReservationAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "r6-drop", clock, identity, recipientLimits: new(PerPeerInFlightBytes: 6000));
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r6-drop-sender");
            var recipient = b.Register("att-r6-drop-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r6-drop", "data.bin", RandomNumberGenerator.GetBytes(4000)));
            string file = Path.Combine(Folder(b, a, message.Id), "0-data.bin");
            a.Pairing.BeforeAttachment = async _ => { await Task.Delay(TimeSpan.FromSeconds(6.5)); return false; };
            string first = await Submit(a, b, message);
            await Until(() => File.Exists(file), "ATTACHMENT_DROP_KEEPS_NEWER_RESERVATION: the slow pull publishes its file", () => "first " + first);
            await Task.Delay(500); // the pull's task completes right after publication
            a.Pairing.BeforeAttachment = null;
            File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(4000)); // no longer hashes: A's recheck deletes it
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new Requests(a) { Behaviour = async (_, _) => { await gate.Task; return false; } };
            Task<string>? third = null;
            bool replaced = false;
            int admissions = 0;
            b.Federation.BeforeAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) != 1) return;
                clock.Advance(PeerFederation.CompletedPullGrace + TimeSpan.FromMinutes(1)); // A's state is past its grace for C
                Renew(b, recipient, clock);
                third = Task.Run(() => Submit(a, b, message));
                replaced = SpinWait.SpinUntil(() => requests.For(message.Id) >= 1, TimeSpan.FromSeconds(10));
            };
            string second;
            ReserveResult probe;
            int states;
            try
            {
                try { second = await Submit(a, b, message); }
                finally { b.Federation.BeforeAdmission = null; }
                states = b.Federation.AttachmentPullStateCount;
                Guid other = Guid.NewGuid();
                probe = b.Attachments!.Reserve(a.Store.BrokerId, other, 4000);
                b.Attachments.Release(a.Store.BrokerId, other);
            }
            finally { gate.TrySetResult(); }
            string thirdAnswer = third is null ? "not started" : await third.WaitAsync(TimeSpan.FromSeconds(30));
            bool held = first == AttachmentCodes.FetchPending && second == AttachmentCodes.FetchPending && replaced && states == 1 &&
                probe == ReserveResult.Defer && thirdAnswer == Admitted;
            if (!held)
                Console.WriteLine("OBSERVED: ATTACHMENT_DROP_KEEPS_NEWER_RESERVATION first " + first + "; second " + second + "; replaced " + replaced +
                    "; pull states " + states + "; probe " + probe + "; third " + thirdAnswer);
            check(held,
                "ATTACHMENT_DROP_KEEPS_NEWER_RESERVATION: a forward's Drop after a newer forward replaced its pull state ends neither that pull's state nor its reservation");
        }
    }

    // Holds each file request the recipient's own client sends until the gate opens (the AttachmentHandler seam).
    private sealed class HeldHandler(HttpMessageHandler inner, Task gate, Action sent) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            sent();
            await gate.WaitAsync(token);
            return await base.SendAsync(request, token);
        }
    }
}
