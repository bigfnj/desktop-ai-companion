using AgentBroker.Core;
using AgentBroker.Worker;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

static class ConcurrentPairingChecks
{
    internal static async Task RunAsync(Node a, Node b, Action<bool, string> check)
    {
        int arrived = 0;
        var bothRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node lower = StringComparer.Ordinal.Compare(a.Store.BrokerId.ToString("N"), b.Store.BrokerId.ToString("N")) < 0 ? a : b;
        Node higher = ReferenceEquals(lower, a) ? b : a;
        PairingOffer? superseded = null;
        Task Meet()
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                superseded = higher.Federation.PairingOffers.Single();
                bothRequests.TrySetResult();
            }
            return bothRequests.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        async Task<string> Request(Node from, Node to)
        {
            try { await from.Federation.RequestPairAsync(to.Advertisement, default); return "requested"; }
            catch (BrokerException error) when (error.Code == "pairing_in_progress") { return error.Code; }
        }
        a.Pairing.BeforeRequest = b.Pairing.BeforeRequest = Meet;
        try
        {
            string[] outcomes = await Task.WhenAll(Request(lower, higher), Request(higher, lower)).WaitAsync(TimeSpan.FromSeconds(8));
            check(arrived == 2, "simultaneous request regression reaches both HTTPS handlers with both outbound offers pending");
            check(a.Federation.PairingOffers.Count == 1 && b.Federation.PairingOffers.Count == 1 &&
                a.Federation.PairingOffers.Single().RequestId == b.Federation.PairingOffers.Single().RequestId,
                "simultaneous pairing requests converge on one shared offer and comparison code");
            var canonical = lower.Federation.PairingOffers.Single();
            check(outcomes.SequenceEqual(new[] { "requested", "pairing_in_progress" }) && canonical.Outbound &&
                !higher.Federation.PairingOffers.Single().Outbound && canonical.VerificationCode == higher.Federation.PairingOffers.Single().VerificationCode,
                "only the colliding higher identity yields to the lower identity and reports a typed nonretry conflict");
            check(a.Store.Peers().Count == 0 && b.Store.Peers().Count == 0 &&
                !canonical.LocalApproved && !canonical.RemoteApproved &&
                !higher.Federation.PairingOffers.Single().LocalApproved && !higher.Federation.PairingOffers.Single().RemoteApproved,
                "collision convergence carries no human approval or session trust to the replacement code");
            await Task.WhenAll(lower.Federation.RequestPairAsync(higher.Advertisement, default),
                higher.Federation.RequestPairAsync(lower.Advertisement, default));
            check(arrived == 2 && a.Federation.PairingOffers.Single().RequestId == canonical.RequestId &&
                b.Federation.PairingOffers.Single().RequestId == canonical.RequestId,
                "repeated local request clicks preserve the exact current offer without another HTTPS request");

            a.Pairing.BeforeRequest = b.Pairing.BeforeRequest = null;
            using var oldRequest = JsonDocument.Parse(lower.Pairing.LastRequest!);
            string nonce = oldRequest.RootElement.GetProperty("nonce").GetString()!;
            using (var caller = lower.Client(higher.Identity))
            using (var replay = new StringContent(lower.Pairing.LastRequest!, Encoding.UTF8, "application/json"))
            using (var replayed = await caller.PostAsync("pair/request", replay))
            using (var approval = await caller.PostAsJsonAsync("pair/approve", new { requestId = superseded!.RequestId,
                nonce, peer = higher.Advertisement, verificationCode = superseded.VerificationCode }))
                check(replayed.StatusCode == HttpStatusCode.BadRequest && approval.StatusCode == HttpStatusCode.Forbidden,
                    "the discarded colliding request and its old nonce/code cannot be replayed at the winning broker");
            bool oldLocalRefused = false;
            try { await higher.Federation.ApprovePairAsync(superseded!.RequestId, default); }
            catch (InvalidDataException) { oldLocalRefused = true; }
            check(oldLocalRefused && higher.Federation.PairingOffers.Single().RequestId == canonical.RequestId &&
                !higher.Federation.PairingOffers.Single().LocalApproved,
                "the losing broker tombstones its old outbound offer and refuses approval transfer");

            await lower.Federation.ApprovePairAsync(canonical.RequestId, default);
            await Task.WhenAll(lower.Federation.RequestPairAsync(higher.Advertisement, default),
                higher.Federation.RequestPairAsync(lower.Advertisement, default));
            check(a.Store.Peers().Count == 0 && b.Store.Peers().Count == 0 &&
                lower.Federation.PairingOffers.Single().LocalApproved && higher.Federation.PairingOffers.Single().RemoteApproved,
                "one human approval remains insufficient after collision and repeated request clicks");
            await higher.Federation.ApprovePairAsync(canonical.RequestId, default);
            check(a.Store.Peers().Count == 1 && b.Store.Peers().Count == 1 &&
                a.Federation.PairingOffers.Count == 0 && b.Federation.PairingOffers.Count == 0,
                "the canonical code still requires and accepts separate final approval on both brokers");
        }
        finally
        {
            a.Pairing.BeforeRequest = b.Pairing.BeforeRequest = null;
            foreach (var offer in a.Federation.PairingOffers) a.Federation.RejectPair(offer.RequestId);
            foreach (var offer in b.Federation.PairingOffers) b.Federation.RejectPair(offer.RequestId);
            a.Federation.RevokePeer(b.Store.BrokerId);
            b.Federation.RevokePeer(a.Store.BrokerId);
        }
    }
}
