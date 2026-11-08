using AgentBroker.Core;
using AgentBroker.TestSupport;
using AgentBroker.Worker;
using AgentBroker.Worker.Federation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class PeerDirectoryShapeChecks
{
    internal static void PureContracts(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: DIRECTORY_SHAPE_POLICY_EXECUTED");
        Console.WriteLine("DIRECTORY_SHAPE_WORKER_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BrokerRuntime).Assembly.Location))));
        Guid peer = Guid.NewGuid();
        var row = Row(peer);
        bool Valid(PeerDirectory? value, int maximum = PeerDirectoryPolicy.ListingMaximumSessions) => PeerDirectoryPolicy.IsValid(value, peer, maximum);
        bool Shape(PeerDirectory? value) => PeerDirectoryPolicy.HasValidShape(value, peer, PeerDirectoryPolicy.ListingMaximumSessions);
        check(Valid(new(peer, [row])) && Valid(new(peer, [])),
            "DIRECTORY_SHAPE_VALID: ordinary and authenticated empty directories remain valid");
        check(!Valid(null), "DIRECTORY_SHAPE_NULL_DIRECTORY: a null directory cannot publish rows");
        check(!Valid(new(peer, null!)), "DIRECTORY_SHAPE_NULL_COLLECTION: a missing collection cannot publish rows");
        check(!Shape(new(peer, [null!])) && !Valid(new(peer, [null!])), "DIRECTORY_SHAPE_NULL_ROW: collection elements require an actual session");
        check(!Valid(new(peer, [row with { Address = null! }])), "DIRECTORY_SHAPE_NULL_ADDRESS: a row requires an actual address");
        check(!Valid(new(Guid.NewGuid(), [row])), "DIRECTORY_SHAPE_BROKER: the directory belongs to the expected broker");
        check(!Valid(new(peer, [row with { Address = row.Address with { BrokerId = Guid.NewGuid() } }])),
            "DIRECTORY_SHAPE_ROW_BROKER: a row cannot claim another broker");
        check(!Valid(new(peer, [row with { IsPrivate = true }])), "DIRECTORY_SHAPE_PRIVATE: a private row cannot leave its broker");
        check(Valid(new(peer, Enumerable.Repeat(row, 256).ToArray())) && !Valid(new(peer, Enumerable.Repeat(row, 257).ToArray())),
            "DIRECTORY_SHAPE_LISTING_CAP: the existing local presentation cap remains256");
        check(Valid(new(peer, Enumerable.Repeat(row, 512).ToArray()), PeerDirectoryPolicy.WireMaximumSessions) &&
            !Valid(new(peer, Enumerable.Repeat(row, 513).ToArray()), PeerDirectoryPolicy.WireMaximumSessions),
            "DIRECTORY_SHAPE_WIRE_CAP: the existing wire cap remains512");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
        };
        var parsed = JsonSerializer.Deserialize<PeerDirectory>(JsonSerializer.Serialize(new PeerDirectory(peer, [null!]), json), json);
        check(parsed?.Sessions.Count == 1 && parsed.Sessions[0] is null && !Valid(parsed),
            "DIRECTORY_SHAPE_PARSED_ELEMENT: collection-element nullability needs explicit validation after decoding");
        check(Valid(JsonSerializer.Deserialize<PeerDirectory>(JsonSerializer.Serialize(new PeerDirectory(peer, [row]), json), json)),
            "DIRECTORY_SHAPE_PARSED_VALID: ordinary directory JSON keeps its original shape and values");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        PureContracts(check);
        string root;
        using (var scratch = OwnedScratch.Create("directory-shape"))
        {
            root = scratch.Root;
            using var store = new BrokerStore(Path.Combine(root, "store"));
            var healthy = Peer(store, "healthy", 18501);
            var malformed = Peer(store, "malformed", 18502);
            var healthyRow = Row(healthy.BrokerId);
            var badRow = Row(malformed.BrokerId);
            await using var federation = new DirectoryFederation(healthy.BrokerId, healthyRow);
            await using var runtime = new BrokerRuntime(store, federation, []);
            var local = runtime.RegisterPollingAgent(Guid.NewGuid(), Guid.NewGuid(), "Synthetic local directory", false);
            byte[] before = File.ReadAllBytes(Path.Combine(root, "store", "broker-state.json"));
            PeerDirectory?[] cases = [null, new(malformed.BrokerId, null!), new(malformed.BrokerId, [null!]),
                new(malformed.BrokerId, [badRow with { Address = null! }])];
            foreach (var candidate in cases)
            {
                federation.Other = candidate;
                var listed = await runtime.DirectoryAsync(false, default);
                check(listed.Count == 2 && listed.Any(row => row.Address == local.Address) && listed.Any(row => row.Address == healthyRow.Address),
                    "DIRECTORY_SHAPE_PEER_ISOLATION: malformed peer shape leaves local and healthy peer rows available");
                var diagnostics = await PeerDiagnostics.CollectAsync(runtime, default);
                check(diagnostics.Single(row => row.BrokerId == malformed.BrokerId) is { Success: false, Code: "invalid_directory" } &&
                    diagnostics.Single(row => row.BrokerId == healthy.BrokerId) is { Success: true, ReturnedSessionCount: 1 },
                    "DIRECTORY_SHAPE_DIAGNOSTIC: malformed peer shape is classified without failing a healthy peer");
            }
            federation.Other = new(malformed.BrokerId, [badRow, badRow with { IsPrivate = true },
                badRow with { Address = badRow.Address with { BrokerId = Guid.NewGuid() } },
                badRow with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }]);
            var mixed = await runtime.DirectoryAsync(false, default);
            check(mixed.Count == 3 && mixed.Count(row => row.Address == badRow.Address) == 1 &&
                mixed.Any(row => row.Address == local.Address) && mixed.Any(row => row.Address == healthyRow.Address),
                "DIRECTORY_SHAPE_RUNTIME_FILTERING: the runtime retains eligible rows while filtering private, transitive and expired rows");
            check(File.ReadAllBytes(Path.Combine(root, "store", "broker-state.json")).SequenceEqual(before),
                "DIRECTORY_SHAPE_READ_ONLY: directory and diagnostic checks do not rewrite durable trust or messages");
        }
        check(!Directory.Exists(root), "DIRECTORY_SHAPE_FIXTURE_CLEAN: the exact owned scratch root is absent");
    }

    private static BrokerSession Row(Guid broker) => new(new(broker, "synthetic-directory", Guid.NewGuid()),
        "Synthetic directory row", "synthetic", SessionCapabilities.Receive, SessionActivity.Idle, DateTimeOffset.UtcNow.AddMinutes(2));
    private static PeerTrust Peer(BrokerStore store, string name, int port)
    {
        var peer = new PeerTrust(Guid.NewGuid(), "Synthetic " + name, "https://127.0.0.1:" + port + "/", new string('A', 64), DateTimeOffset.UtcNow);
        store.AuthorizePeer(peer); return peer;
    }
    private sealed class DirectoryFederation(Guid healthy, BrokerSession healthyRow) : IPeerFederation
    {
        internal PeerDirectory? Other { get; set; }
        public IReadOnlyList<BrokerAdvertisement> Discovered => [];
        public IReadOnlyList<PairingOffer> PairingOffers => [];
        public BrokerAdvertisement LocalAdvertisement => new(Guid.NewGuid(), "Synthetic", "https://127.0.0.1:18500/", new string('A', 64), DateTimeOffset.UtcNow);
        public Guid? AuthenticatePeer(HttpContext context) => null;
        public void MapEndpoints(WebApplication app) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task RequestPairAsync(BrokerAdvertisement peer, CancellationToken token) => throw new NotSupportedException();
        public Task ApprovePairAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public void RejectPair(Guid id) { }
        public void RevokePeer(Guid id) { }
        public Task<PeerDirectory> GetDirectoryAsync(PeerTrust peer, CancellationToken token) =>
            Task.FromResult(peer.BrokerId == healthy ? new PeerDirectory(healthy, [healthyRow]) : Other!);
        public Task<MessageReceipt> SubmitAsync(PeerTrust peer, BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public Task<MessageReceipt?> GetReceiptAsync(PeerTrust peer, Guid id, CancellationToken token) => Task.FromResult<MessageReceipt?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
