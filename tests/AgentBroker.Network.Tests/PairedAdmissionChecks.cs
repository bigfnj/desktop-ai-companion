using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Federation;
using AgentBroker.Worker.Security;

internal static class PairedAdmissionChecks
{
    internal static async Task RunAsync(string scratch, Func<string, Guid, BrokerIdentity> identity, Action<bool, string> check)
    {
        await PairedReceiptReasonChecks.RunAsync(scratch, identity, check);
        var clock = new AdjustableClock();
        await using var a = await Node.Create(Path.Combine(scratch, "admission-a"), clock, identity);
        await using var b = await Node.Create(Path.Combine(scratch, "admission-b"), clock, identity);
        await a.Federation.RequestPairAsync(b.Advertisement, default);
        await b.Federation.ApprovePairAsync(b.Federation.PairingOffers.Single().RequestId, default);
        await a.Federation.ApprovePairAsync(a.Federation.PairingOffers.Single().RequestId, default);
        var trust = a.Store.Peers().Single();
        var recipient = b.Register("observation-only");
        b.Store.Register(recipient with { Capabilities = SessionCapabilities.None });
        var sender = new BrokerSession(new(a.Store.BrokerId, "synthetic:admission-sender", Guid.NewGuid()), "Synthetic sender",
            "synthetic", SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply,
            SessionActivity.Busy, clock.GetUtcNow().AddMinutes(1));
        a.Store.Register(sender);
        BrokerMessage Message() => new(Guid.NewGuid(), Guid.NewGuid(), null, sender.Address, recipient.Address,
            "Synthetic admission check.", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5));
        using var stop = new CancellationTokenSource();
        await using var runtime = new BrokerRuntime(a.Store, a.Federation, []);
        runtime.Start(stop.Token);
        async Task<MessageReceipt> Send()
        {
            var message = Message();
            a.Store.Accept(message, a.Store.BrokerId);
            for (int i = 0; i < 100; i++)
            {
                var receipt = a.Store.FindReceipt(message.Id)!;
                if (receipt.Status is MessageStatus.Refused or MessageStatus.Uncertain) return receipt;
                await Task.Delay(50);
            }
            throw new InvalidOperationException("PAIRED_UNCERTAIN_SETTLEMENT: the expected terminal admission result did not settle.");
        }
        var refused = await Send();
        check(refused is { Status: MessageStatus.Refused, Reason: "recipient_cannot_receive" } &&
            b.Store.FindReceipt(refused.Message.Id) is null,
            "PAIRED_DEFINITE_REFUSAL: actual mutually authenticated peer rejection settles Refused with its admission code");

        foreach (var failure in new (int Status, string Body, string Type)[] {
            (Status: 409, Body: """{"error":"unknown_future_code"}""", Type: "application/json"),
            (409, """{"error":"recipient_cannot_receive","extra":1}""", "application/json"),
            (409, """{"error":"recipient_cannot_receive","error":"recipient_cannot_receive"}""", "application/json"),
            (409, "{", "application/json"), (409, new string('x', 1025), "application/json"),
            (500, """{"error":"recipient_cannot_receive"}""", "application/json"),
            (409, """{"error":"recipient_cannot_receive"}""", "text/plain") })
        {
            b.Pairing.BeforeMessages = async context =>
            {
                if (b.Federation.AuthenticatePeer(context) != a.Store.BrokerId) throw new UnauthorizedAccessException();
                context.Response.StatusCode = failure.Status;
                context.Response.ContentType = failure.Type;
                await context.Response.WriteAsync(failure.Body);
                return true;
            };
            var uncertain = await Send();
            check(uncertain is { Status: MessageStatus.Uncertain, Reason: "submission_unconfirmed" },
                "PAIRED_UNCONFIRMED_STAYS_UNCERTAIN: malformed, unknown or non-admission HTTPS response does not become a definite refusal");
        }
        b.Pairing.BeforeMessages = context => { context.Abort(); return Task.FromResult(true); };
        check((await Send()).Status == MessageStatus.Uncertain,
            "PAIRED_NETWORK_FAILURE: a broken authenticated connection remains uncertain");
        stop.Cancel();
        await runtime.DisposeAsync();

        // Hold a genuine pinned HTTPS response body while authorization is revoked and recreated.
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Pairing.BeforeMessages = async context =>
        {
            if (b.Federation.AuthenticatePeer(context) != a.Store.BrokerId) throw new UnauthorizedAccessException();
            context.Response.StatusCode = 409; context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.WriteAsync("{");
            await context.Response.Body.FlushAsync(context.RequestAborted);
            arrived.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("\"error\":\"recipient_cannot_receive\"}");
            return true;
        };
        var beforeHeaders = a.Federation.ClockObservations.Single().CheckedUtc;
        var pending = a.Federation.SubmitAsync(trust, Message(), default);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 100 && a.Federation.ClockObservations.Single().CheckedUtc == beforeHeaders; i++) await Task.Delay(20);
            check(a.Federation.ClockObservations.Single().CheckedUtc != beforeHeaders,
                "PAIRED_REFUSAL_HEADERS_OBSERVED: the client has checked and observed the headers while the refusal body is still withheld");
            a.Store.RevokePeer(trust.BrokerId); a.Store.AuthorizePeer(trust);
        }
        finally { release.TrySetResult(); }
        bool revoked;
        try { await pending; revoked = false; }
        catch (UnauthorizedAccessException) { revoked = true; }
        catch (PeerAdmissionRefusedException) { revoked = false; }
        check(revoked, "PAIRED_REFUSAL_RECHECKS_AUTHORIZATION: revocation and identical re-pairing invalidate an in-flight refusal");
        b.Pairing.BeforeMessages = null;
        check(a.Federation.ClockObservations.Single().State == "not_checked",
            "PAIRED_CLOCK_REPAIR: a new authorization does not inherit a prior TLS clock sample");

        foreach (int seconds in new[] { 20, -20 })
        {
            b.Pairing.BeforeResponse = context =>
            {
                context.Response.Headers.Date = (clock.GetUtcNow() + TimeSpan.FromSeconds(seconds)).ToString("r", CultureInfo.InvariantCulture);
                return Task.CompletedTask;
            };
            await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), default);
            var observation = a.Federation.ClockObservations.Single();
            check(observation.State == "observed" && observation.Warning && Math.Sign(observation.OffsetSeconds!.Value) == Math.Sign(seconds),
                "PAIRED_CLOCK_AUTHENTICATED_DATE: actual pinned HTTPS Date yields a signed offset and qualified warning");
        }
        b.Pairing.BeforeResponse = context => { context.Response.Headers.Date = "not-a-date"; return Task.CompletedTask; };
        await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), default);
        check(a.Federation.ClockObservations.Single() is { State: "invalid_date", OffsetSeconds: null, Warning: false },
            "PAIRED_CLOCK_MALFORMED_HEADER: a real malformed HTTPS Date is unavailable");
        b.Pairing.BeforeResponse = null;
        await DirectoryWithoutDate(a, b, check);
        check(a.Federation.ClockObservations.Single() is { State: "missing_date", Warning: false },
            "PAIRED_CLOCK_MISSING_HEADER: an absent actual HTTPS Date is not reported as a zero offset");
        b.Pairing.BeforeDirectory = () => Task.Delay(2200);
        await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), default);
        check(a.Federation.ClockObservations.Single() is { State: "high_latency", Warning: false },
            "PAIRED_CLOCK_SLOW: an actual slow HTTPS response is explicitly unsuitable for a skew warning");
        b.Pairing.BeforeDirectory = null;
        b.Pairing.BeforeResponse = _ => { clock.Jump(TimeSpan.FromSeconds(10)); return Task.CompletedTask; };
        await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), default);
        check(a.Federation.ClockObservations.Single() is { State: "local_clock_changed", Warning: false },
            "PAIRED_CLOCK_JUMP: an actual exchange spanning a local wall-clock jump is invalidated");
        b.Pairing.BeforeResponse = null;
        await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), default);
        clock.Advance(TimeSpan.FromMinutes(6));
        check(a.Federation.ClockObservations.Single() is { State: "stale", Warning: false },
            "PAIRED_CLOCK_AGE: a real HTTPS observation becomes stale on monotonic age");
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private TimeSpan offset;
        private long timestampOffset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
        public override long TimestampFrequency => global::System.Diagnostics.Stopwatch.Frequency;
        public override long GetTimestamp() => global::System.Diagnostics.Stopwatch.GetTimestamp() + timestampOffset;
        internal void Jump(TimeSpan amount) => offset += amount;
        internal void Advance(TimeSpan amount)
        { offset += amount; timestampOffset += (long)(amount.TotalSeconds * TimestampFrequency); }
    }

    private static async Task DirectoryWithoutDate(Node a, Node b, Action<bool, string> check)
    {
        // Kestrel supplies Date automatically, so this one wire fixture deliberately emits a valid response without it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var trust = a.Store.Peers().Single();
        var authorization = a.Store.CapturePeerAuthorization(trust)!;
        string endpoint = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
        check(a.Store.TryRefreshPeerEndpoint(trust, authorization, endpoint),
            "PAIRED_CLOCK_DATELESS_ENDPOINT: an authenticated saved peer can move to its bounded wire fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool authenticated = false;
        Task server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                authenticated = certificate is X509Certificate2 cert && BrokerIdentity.IsFor(cert, a.Store.BrokerId) &&
                    cert.GetCertHashString(HashAlgorithmName.SHA256) == a.Identity.Fingerprint);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = b.Identity.Certificate, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, deadline.Token);
            using var headers = new MemoryStream();
            byte[] one = new byte[1];
            while (headers.Length < 16384)
            {
                if (await tls.ReadAsync(one, deadline.Token) != 1) throw new IOException("Incomplete fixture request.");
                headers.WriteByte(one[0]);
                if (headers.Length >= 4 && Encoding.ASCII.GetString(headers.GetBuffer(), (int)headers.Length - 4, 4) == "\r\n\r\n") break;
            }
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new PeerDirectory(b.Store.BrokerId, []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(reply, deadline.Token); await tls.WriteAsync(body, deadline.Token); await tls.FlushAsync(deadline.Token);
        });
        try
        {
            await a.Federation.GetDirectoryAsync(a.Store.Peers().Single(), deadline.Token);
            await server;
            check(authenticated, "PAIRED_CLOCK_DATELESS_AUTHENTICATED: the response without Date still uses both pinned TLS identities");
        }
        finally
        {
            listener.Stop();
            deadline.Cancel();
            try { await server; } catch (OperationCanceledException) { }
            var current = a.Store.Peers().Single();
            a.Store.TryRefreshPeerEndpoint(current, authorization, trust.Endpoint);
        }
    }
}
