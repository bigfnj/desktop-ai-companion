using System.Security.Cryptography;
using AgentBroker.Core;
using AgentBroker.Worker.Security;

// Lane F round 7 (DECISIONS 127): the S-019 residue. The admitted text is bound to the received folder's manifest, so a text
// that passed the one-text check before the first text's pull or manifest existed is refused once that pull's state is gone.
// The label names the guard the hygiene recipe attachment-r7-manifest-text breaks by reverting it.
internal static partial class AttachmentFederationChecks
{
    private static async Task RoundSevenAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        await OneTextBoundToManifestAsync(scratch, identity, check);
    }

    // Forward B (another note, the same two files) is held right after the one-text check (BeforePullLookup) while nothing
    // exists for the ID yet. Forward A then runs: file 0 is published with manifest.json(A), file 1 answers 503 once, so A's pull
    // ends transient, its state is dropped and A defers. B is released into a quarantine with no pull state but with A's
    // manifest: its own pull verifies both files, and at admission it is refused attachment_envelope_invalid. A's next forward
    // is admitted, and the admitted text is the manifest's envelope.
    private static async Task OneTextBoundToManifestAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        var clock = new TestClock();
        var (a, b) = await PairedAsync(scratch, "r7-manifest", clock, identity);
        await using (a)
        await using (b)
        {
            var sender = a.Register("att-r7-manifest-sender");
            var recipient = b.Register("att-r7-manifest-recipient");
            var message = SendAttached(a, sender, recipient, Source(scratch, "r7-manifest", "one.txt", "Synthetic first file."u8.ToArray()),
                Source(scratch, "r7-manifest", "two.txt", "Synthetic second file."u8.ToArray()));
            var other = message with { Text = AttachmentEnvelope.Compose("A different synthetic note.", AttachmentEnvelopeOf(message).Attachments) };
            int refused = 0;
            var requests = new Requests(a)
            {
                Behaviour = (_, context) =>
                {
                    if (!(context.Request.Path.Value ?? "").EndsWith("/1", StringComparison.Ordinal) || Interlocked.Exchange(ref refused, 1) != 0)
                        return Task.FromResult(false);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.FromResult(true);
                }
            };
            using var release = new ManualResetEventSlim();
            int lookups = 0;
            bool otherHeld = false;
            b.Federation.BeforePullLookup = () =>
            {
                if (Interlocked.Increment(ref lookups) == 1) otherHeld = release.Wait(TimeSpan.FromSeconds(30)); // forward B only
            };
            Task<string> second;
            string first;
            try
            {
                second = Submit(a, b, other);
                await Until(() => Volatile.Read(ref lookups) >= 1, "ATTACHMENT_ONE_TEXT_BOUND_TO_MANIFEST: the other text has passed the one-text check",
                    () => "lookups " + Volatile.Read(ref lookups));
                first = await Submit(a, b, message);
            }
            finally
            {
                release.Set();
            }
            string secondAnswer = await second;
            b.Federation.BeforePullLookup = null;
            string third = await Submit(a, b, message);
            static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            string? admittedText = b.Store.FindReceiptWithText(message.Id)?.Message.Text;
            string? manifest = b.Attachments!.ReceivedEnvelopeSha256(a.Store.BrokerId, message.Id);
            bool held = otherHeld && first == AttachmentCodes.FetchPending && secondAnswer == AttachmentCodes.EnvelopeInvalid && third == Admitted &&
                admittedText == message.Text && manifest == Sha(message.Text) && Volatile.Read(ref refused) == 1;
            if (!held)
                Console.WriteLine("OBSERVED: ATTACHMENT_ONE_TEXT_BOUND_TO_MANIFEST other held " + otherHeld + "; first " + first + "; other text " + secondAnswer +
                    "; third " + third + "; admitted text is first " + (admittedText == message.Text) + "; manifest is first " + (manifest == Sha(message.Text)) +
                    "; file 1 refused once " + (Volatile.Read(ref refused) == 1) + "; file requests " + requests.For(message.Id));
            check(held,
                "ATTACHMENT_ONE_TEXT_BOUND_TO_MANIFEST: a second text that passed the one-text check before the first text's manifest existed is refused attachment_envelope_invalid at admission once the first pull's state is gone; the admitted text is the manifest's envelope");
        }
    }
}
