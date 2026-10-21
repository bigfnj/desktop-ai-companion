using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentBroker.Worker;

static class MutualPairingChecks
{
    internal static async Task RunAsync(Node a, Node b, Action<bool, string> check,
        Func<Func<Task>, string, Task> refuses)
    {
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        Guid id = a.Federation.PairingOffers.Single().RequestId;
        await a.Federation.ApprovePairAsync(id, default);
        a.Federation.RejectPair(id);
        await refuses(() => b.Federation.ApprovePairAsync(id, default),
            "rejecting after the first approval prevents the other workstation from completing trust");
        check(a.Store.Peers().Count == 0 && b.Store.Peers().Count == 0,
            "a rejected offer grants neither broker session access");
        b.Federation.RejectPair(id);

        await a.Federation.RequestPairAsync(b.Advertisement, default);
        id = a.Federation.PairingOffers.Single().RequestId;
        await Task.WhenAll(a.Federation.ApprovePairAsync(id, default), b.Federation.ApprovePairAsync(id, default))
            .WaitAsync(TimeSpan.FromSeconds(5));
        check(a.Store.Peers().Count == 1 && b.Store.Peers().Count == 1 &&
            a.Federation.PairingOffers.Count == 0 && b.Federation.PairingOffers.Count == 0,
            "simultaneous final approvals complete without holding either inbound lock across callbacks");
        await Task.WhenAll(a.Federation.ApprovePairAsync(id, default), b.Federation.ApprovePairAsync(id, default));
        check(a.Store.Peers().Count == 1 && b.Store.Peers().Count == 1,
            "repeated local approval is idempotent after mutual completion");
        a.Federation.RevokePeer(b.Store.BrokerId); b.Federation.RevokePeer(a.Store.BrokerId);

        await a.Federation.RequestPairAsync(b.Advertisement, default);
        id = a.Federation.PairingOffers.Single().RequestId;
        await a.Federation.ApprovePairAsync(id, default);
        a.Pairing.DropNextApprovalResponse = true;
        await refuses(() => b.Federation.ApprovePairAsync(id, default),
            "a lost final approval response is reported as a failed exchange");
        check(a.Store.Peers().Count == 1 && b.Store.Peers().Count == 0 &&
            b.Federation.PairingOffers.Single().LocalApproved,
            "lost response retains a pending confirmation locally instead of claiming the broker is paired");
        await b.Federation.ApprovePairAsync(id, default);
        check(a.Store.Peers().Count == 1 && b.Store.Peers().Count == 1,
            "retrying the same authenticated proof safely recovers a lost final response");
        a.Federation.RevokePeer(b.Store.BrokerId); b.Federation.RevokePeer(a.Store.BrokerId);

        // Construct a known synthetic request to exercise the nonce/code binding and tombstone.
        Guid requestId = Guid.NewGuid(); string nonce = new('C', 64);
        using var caller = b.Client(a.Identity);
        using var requested = await caller.PostAsJsonAsync("pair/request", new { requestId, nonce,
            peer = a.Advertisement, expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        requested.EnsureSuccessStatusCode();
        var offer = (await requested.Content.ReadFromJsonAsync<PairingOffer>())!;
        using var wrongNonce = await caller.PostAsJsonAsync("pair/approve", new { requestId, nonce = new string('D', 64),
            peer = a.Advertisement, verificationCode = offer.VerificationCode });
        using var wrongCode = await caller.PostAsJsonAsync("pair/approve", new { requestId, nonce,
            peer = a.Advertisement, verificationCode = "00000000 00000000 00000000 00000000" });
        check(wrongNonce.StatusCode == HttpStatusCode.Forbidden && wrongCode.StatusCode == HttpStatusCode.Forbidden &&
            !b.Federation.PairingOffers.Single().RemoteApproved && b.Store.Peers().Count == 0,
            "wrong nonce or comparison code never records remote approval");
        b.Federation.RejectPair(requestId);
        using var rejectedReplay = await caller.PostAsJsonAsync("pair/approve", new { requestId, nonce,
            peer = a.Advertisement, verificationCode = offer.VerificationCode });
        check(rejectedReplay.StatusCode == HttpStatusCode.Forbidden && b.Store.Peers().Count == 0,
            "an authenticated proof cannot revive an explicitly rejected offer");
    }
}
