using System.Text;
using AgentBroker.ClaudeAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// Update-tolerant Claude delivery (the owner's decision of 2026-11-02): an Anthropic-signed Claude Code build that fails its
// exact pin is woken as a pinned one is, with every other check unchanged. --claude-update-tolerant-case runs one group, so
// each mutation witness fails on the check it targets. Every label names this file for the witness harness.
internal static class ClaudeUpdateTolerantChecks
{
    private static readonly string[] Cases = ["discovery", "status", "refusals", "capabilities", "image-changed", "send", "image-trust", "options", "version"];
    private const string L = "ClaudeUpdateTolerantChecks.cs: ";
    private const string Pipe = @"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef";
    private const string SignedHash = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
    private const string UpdatedVersion = "2.1.999";
    private static readonly EngineProfile Pin = EngineProfile.Verified[0];

    internal static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Cases.Contains(only)) throw new ArgumentException("Unknown update-tolerant case.", nameof(only));
        foreach (string name in Cases.Where(name => only is null || name == only))
            switch (name)
            {
                case "discovery": await DiscoveryAsync(check); break;
                case "status": await StatusAsync(check); break;
                case "refusals": await RefusalsAsync(check); break;
                case "capabilities": await CapabilitiesAsync(check); break;
                case "image-changed": await ImageChangedAsync(check); break;
                case "send": await SendAsync(check); break;
                case "image-trust": ImageTrust(check); break;
                case "options": Options(check); break;
                case "version": await VersionAsync(check); break;
            }
    }

    // The note names the build as people see it (coordinator, 2026-11-02): the file version's zero fourth part is dropped.
    private static async Task VersionAsync(Action<bool, string> check)
    {
        await using (var provider = new ClaudeNativeProvider(new Source { Version = "2.1.314.0" }, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            await provider.DiscoverAsync(CancellationToken.None);
            string text = provider.DiscoveryDiagnostics.SingleOrDefault()?.Description ?? "";
            check(text.StartsWith("Claude Code 2.1.314 is not a reviewed build;", StringComparison.Ordinal),
                L + "the note names a file version 2.1.314.0 as 2.1.314");
        }
        check(ClaudeNativeProvider.UpdateTolerantNote("2.1.314.7").Description.StartsWith("Claude Code 2.1.314.7 is", StringComparison.Ordinal) &&
            ClaudeNativeProvider.UpdateTolerantNote("2.1.314").Description.StartsWith("Claude Code 2.1.314 is", StringComparison.Ordinal) &&
            ClaudeNativeProvider.UpdateTolerantNote("not-a-version").Description.StartsWith("Claude Code not-a-version is", StringComparison.Ordinal) &&
            ClaudeNativeProvider.UpdateTolerantNote(null).Description.StartsWith("Claude Code unknown version is", StringComparison.Ordinal),
            L + "the note keeps a non-zero fourth part, a three-part version and a version that does not parse as they were");
    }

    private static async Task DiscoveryAsync(Action<bool, string> check)
    {
        var source = new Source();
        await using (var provider = new ClaudeNativeProvider(source, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            var row = (await provider.DiscoverAsync(CancellationToken.None)).Single();
            check(row.Provenance == ClaudeDiscoveryProvenance.UpdateTolerant && !row.CanDeliver &&
                row.Target.ImageSha256 == SignedHash && row.Target.EngineVersion == UpdatedVersion,
                L + "mode on with credentials: an unpinned Anthropic-signed session is carried, with the attested hash, never as an exact pin");
        }
        await using (var strict = new ClaudeNativeProvider(new Source(), enablePeerCredentials: true, updateTolerantDelivery: false))
            check((await strict.DiscoverAsync(CancellationToken.None)).Single().Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation,
                L + "mode off with credentials: the same session stays a publisher-signed observation");
        await using (var inert = new ClaudeNativeProvider(new Source(), enablePeerCredentials: false, updateTolerantDelivery: true))
            check((await inert.DiscoverAsync(CancellationToken.None)).Single().Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation,
                L + "mode on without credentials: the mode is inert and the session stays an observation");
        var pinnedSource = new Source { Version = Pin.Version, Hash = Pin.Sha256 };
        await using (var pinned = new ClaudeNativeProvider(pinnedSource, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            var row = (await pinned.DiscoverAsync(CancellationToken.None)).Single();
            check(row.Provenance == ClaudeDiscoveryProvenance.ExactPinned && row.CanDeliver && pinnedSource.TrustReads == 0 &&
                pinned.DiscoveryDiagnostics.Count == 0,
                L + "WITNESS: a pinned build is unaffected by the mode: exact pinned, no signature read, nothing in status");
        }
        var reissued = new Source { Version = Pin.Version, Hash = new string('0', 64) };
        await using (var sameVersion = new ClaudeNativeProvider(reissued, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            var row = (await sameVersion.DiscoverAsync(CancellationToken.None)).Single();
            check(row.Provenance == ClaudeDiscoveryProvenance.UpdateTolerant && !row.CanDeliver &&
                !sameVersion.DiscoveryDiagnostics.Any(item => item.Code == "image_hash_mismatch"),
                L + "a signed build with a pinned version but another hash is carried too, never as that version's exact pin");
        }
    }

    private static async Task StatusAsync(Action<bool, string> check)
    {
        var source = new Source();
        await using (var provider = new ClaudeNativeProvider(source, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            await provider.DiscoverAsync(CancellationToken.None);
            var reported = provider.DiscoveryDiagnostics;
            check(!reported.Any(item => item.Code is "unsupported_public_version" or "image_hash_mismatch"),
                L + "while the mode carries the session, the failed exact pin is not reported as an adapter error");
            var note = reported.SingleOrDefault();
            check(note is { Code: "claude_update_tolerant_delivery", Note: true },
                L + "the carried session is one note (BrokerRuntime.AdapterNotes), not an adapter error");
            string text = note?.Description ?? "";
            check(text.Contains(UpdatedVersion, StringComparison.Ordinal) && text.Contains("Anthropic", StringComparison.Ordinal) &&
                text.Length <= 116 && !text.Contains(source.Image, StringComparison.Ordinal) &&
                !text.Contains(source.Metadata.SessionId, StringComparison.Ordinal) && !text.Contains(SignedHash, StringComparison.Ordinal),
                L + "the note names the build and the signature, no path, session or hash, and fits a 116-character tray label");
        }
        await using (var strict = new ClaudeNativeProvider(new Source(), enablePeerCredentials: true, updateTolerantDelivery: false))
        {
            await strict.DiscoverAsync(CancellationToken.None);
            check(strict.DiscoveryDiagnostics.SingleOrDefault() is { Code: "unsupported_public_version", Note: false },
                L + "mode off: an unpinned build keeps today's unsupported_public_version error");
        }
        var unsigned = new Source { Trust = new(ClaudePublicImageTrust.UntrustedSignature) };
        await using (var refused = new ClaudeNativeProvider(unsigned, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            await refused.DiscoverAsync(CancellationToken.None);
            var codes = refused.DiscoveryDiagnostics.Select(item => item.Code).ToArray();
            check(codes.SequenceEqual(["unsupported_public_version", "public_image_signature_untrusted"]) &&
                refused.DiscoveryDiagnostics.All(item => !item.Note),
                L + "mode on with a refused signature: both errors stay, the failed pin first, and nothing is a note");
        }
    }

    private static async Task RefusalsAsync(Action<bool, string> check)
    {
        foreach (var refused in new[] { ClaudePublicImageTrust.UntrustedSignature, ClaudePublicImageTrust.UnexpectedPublisher, ClaudePublicImageTrust.Unavailable })
        {
            var source = new Source { Trust = new(refused, SignedHash) };
            await using var provider = new ClaudeNativeProvider(source, enablePeerCredentials: true, updateTolerantDelivery: true);
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 &&
                provider.DiscoveryDiagnostics.Any(item => item.Code.StartsWith("public_image_", StringComparison.Ordinal)),
                L + "mode on: an unsigned, invalid, wrong-publisher or unverifiable image is refused at discovery (" + refused + ")");
        }
    }

    private static async Task CapabilitiesAsync(Action<bool, string> check)
    {
        var carried = await DiscoverCarriedAsync();
        var provider = new CandidateProvider(carried);
        await using (var adapter = new ClaudeNativeAdapter(provider, enablePeerCredentials: true, updateTolerantDelivery: true))
        {
            var session = (await adapter.DiscoverAsync(Guid.NewGuid(), CancellationToken.None)).Single();
            check(session.Capabilities == (SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply),
                L + "mode on: the carried row gets Receive, WakeIdle and Reply, as a pinned one does");
            var result = await adapter.SubmitAsync(Message(session.Address), CancellationToken.None);
            check(result.Outcome == AdapterAdmission.Submitted && provider.Submissions == 1 && provider.Refreshes == 1 &&
                provider.LastTarget?.ImageSha256 == SignedHash,
                L + "mode on: an idle send refreshes once and reaches the provider with the attested image");
        }
        var strictProvider = new CandidateProvider(carried);
        await using (var strict = new ClaudeNativeAdapter(strictProvider, enablePeerCredentials: true, updateTolerantDelivery: false))
        {
            var session = (await strict.DiscoverAsync(Guid.NewGuid(), CancellationToken.None)).Single();
            var result = await strict.SubmitAsync(Message(session.Address), CancellationToken.None);
            check(session.Capabilities == SessionCapabilities.None && result.Outcome == AdapterAdmission.Refused &&
                result.Reason == "claude_observation_only" && strictProvider.Refreshes == 0 && strictProvider.Submissions == 0,
                L + "adapter mode off: a carried row has no capabilities and is refused as observation-only before refresh");
        }
    }

    private static async Task ImageChangedAsync(Action<bool, string> check)
    {
        var carried = await DiscoverCarriedAsync();
        var provider = new CandidateProvider(carried);
        await using var adapter = new ClaudeNativeAdapter(provider, enablePeerCredentials: true, updateTolerantDelivery: true);
        var session = (await adapter.DiscoverAsync(Guid.NewGuid(), CancellationToken.None)).Single();
        provider.Refreshed = carried with { Target = carried.Target with { ImageSha256 = new string('E', 64) } };
        var result = await adapter.SubmitAsync(Message(session.Address), CancellationToken.None);
        check(result.Outcome == AdapterAdmission.Refused && result.Reason == "claude_image_changed" &&
            provider.Refreshes == 1 && provider.Submissions == 0,
            L + "a refresh that finds another signed image than discovery attested is refused before any send");
    }

    private static async Task SendAsync(Action<bool, string> check)
    {
        var target = new NativeTarget(Pipe, 12345, @"C:\SyntheticPublic\claude.exe", SignedHash, UpdatedVersion, Start, SessionId);
        var identity = new NativeProcessIdentity(12345, target.ImagePath, SignedHash, Start, true);

        var trust = new FakeTrust(SignedHash);
        var factory = new FakeFactory(identity); var credentials = new MemorySource();
        var sent = await new NativePeerAdapter(factory, credentials, trust).SendAsync(target, NativeMessageFor(), Home);
        check(sent.Status == SubmissionStatus.SubmittedUnconfirmed && factory.Last?.Frames.Count == 2 && credentials.Reads == 1 &&
            trust.Calls.SequenceEqual([(identity.ImagePath, identity.ImageSha256)]) &&
            Encoding.UTF8.GetString(factory.Last.Frames[1]).Contains("\"type\":\"user\"", StringComparison.Ordinal),
            L + "an unpinned target whose connected image passes the publisher check gets the credential frame and one plain user frame");

        var strictFactory = new FakeFactory(identity); var strictCredentials = new MemorySource();
        var strict = await new NativePeerAdapter(strictFactory, strictCredentials).SendAsync(target, NativeMessageFor(), Home);
        check(strict.Reason == "unsupported_engine_profile" && strictFactory.Connects == 0 && strictCredentials.Reads == 0,
            L + "without the publisher check (mode off) an unpinned target is refused before transport or credentials");

        var refusing = new FakeTrust(null);
        var foreignFactory = new FakeFactory(identity); var foreignCredentials = new MemorySource();
        var foreign = await new NativePeerAdapter(foreignFactory, foreignCredentials, refusing).SendAsync(target, NativeMessageFor(), Home);
        check(foreign.Reason == "native_publisher_unverified" && foreignCredentials.Reads == 0 && foreignFactory.Last?.Frames.Count == 0 &&
            refusing.Calls.Count == 1,
            L + "a connected image the publisher check refuses (unsigned, another publisher, invalid signature) gets no credential read or frame");

        const string otherSigned = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
        var swapped = new FakeFactory(identity with { ImageSha256 = otherSigned }); var swappedCredentials = new MemorySource();
        var changed = await new NativePeerAdapter(swapped, swappedCredentials, new FakeTrust(otherSigned)).SendAsync(target, NativeMessageFor(), Home);
        check(changed.Reason == "native_identity_mismatch" && swappedCredentials.Reads == 0 && swapped.Last?.Frames.Count == 0,
            L + "a connected image whose hash is not the attested one is refused before any credential read, even when it is signed");

        var late = new FakeFactory(identity) { Reinspection = identity with { ImageSha256 = otherSigned } };
        var lateResult = await new NativePeerAdapter(late, new MemorySource(), new FakeTrust(SignedHash)).SendAsync(target, NativeMessageFor(), Home);
        check(lateResult.Reason == "native_identity_changed" && late.Last?.Frames.Count == 0,
            L + "an image that changes after the credential read is refused before the credential frame");

        var pinnedTarget = target with { EngineVersion = Pin.Version, ImageSha256 = Pin.Sha256 };
        var pinnedFactory = new FakeFactory(identity with { ImageSha256 = Pin.Sha256 });
        var neverAsked = new FakeTrust(null);
        var pinned = await new NativePeerAdapter(pinnedFactory, new MemorySource(), neverAsked).SendAsync(pinnedTarget, NativeMessageFor(), Home);
        check(pinned.Status == SubmissionStatus.SubmittedUnconfirmed && neverAsked.Calls.Count == 0,
            L + "a pinned target never consults the publisher check and still sends, even with one that would refuse");
    }

    private static void ImageTrust(Action<bool, string> check)
    {
        var lease = new Lease();
        var trust = new ClaudePublisherImageTrust(_ => new ClaudePublicImageTrustResult(ClaudePublicImageTrust.ExpectedPublisher, SignedHash) { ImageLease = lease });
        check(trust.IsExpectedPublisher(@"C:\SyntheticPublic\claude.exe", SignedHash) &&
            trust.IsExpectedPublisher(@"C:\SyntheticPublic\claude.exe", SignedHash.ToLowerInvariant()),
            L + "WITNESS: the send-time check accepts the expected publisher with the same hash, in either letter case");
        check(lease.Disposals == 2, L + "each send-time check releases the image lease it was given");
        check(!trust.IsExpectedPublisher(@"C:\SyntheticPublic\claude.exe", new string('E', 64)),
            L + "the send-time check refuses another hash even from the expected publisher");
        foreach (var refused in new[] { ClaudePublicImageTrust.UntrustedSignature, ClaudePublicImageTrust.UnexpectedPublisher, ClaudePublicImageTrust.Unavailable })
            check(!new ClaudePublisherImageTrust(_ => new(refused, SignedHash)).IsExpectedPublisher(@"C:\SyntheticPublic\claude.exe", SignedHash),
                L + "the send-time check refuses an untrusted signature, another publisher or an unavailable check with the same hash (" + refused + ")");
        var source = new Source();
        check(new ClaudeNativeProvider(source, enablePeerCredentials: true, updateTolerantDelivery: false).DeliveryImageTrust is null &&
            new ClaudeNativeProvider(source, enablePeerCredentials: false, updateTolerantDelivery: true).DeliveryImageTrust is null,
            L + "WITNESS: strict mode hands NativePeerAdapter no publisher check, so only exact pins are sent to");
        var handed = new ClaudeNativeProvider(source, enablePeerCredentials: true, updateTolerantDelivery: true).DeliveryImageTrust;
        check(handed is not null && handed.IsExpectedPublisher(source.Image, SignedHash) && source.TrustReads == 1,
            L + "update-tolerant delivery hands NativePeerAdapter the publisher check, observing through the discovery source");
    }

    private static void Options(Action<bool, string> check)
    {
        string data = Path.Combine(Path.GetTempPath(), "agentbroker-claude-tolerant-" + Guid.NewGuid().ToString("N"));
        var on = WorkerOptions.Parse(["--data", data, "--claude-peer-credentials", "true", "--claude-update-tolerant-delivery", "true"]);
        var off = WorkerOptions.Parse(["--data", data, "--claude-peer-credentials", "true"]);
        check(on.EnableClaudeUpdateTolerantDelivery && !off.EnableClaudeUpdateTolerantDelivery,
            L + "the worker parses --claude-update-tolerant-delivery and keeps strict pins without it");
        bool Wired(WorkerOptions options)
        {
            var adapters = WorkerApplication.CreateNativeAdapters(options with { EnableCodexNativeDiscovery = false });
            try { return adapters.OfType<ClaudeNativeAdapter>().Single().UpdateTolerantDelivery; }
            finally { foreach (var adapter in adapters) adapter.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        check(Wired(on) && !Wired(off), L + "the worker hands the flag to its Claude adapter");
    }

    private static async Task<ClaudeNativeCandidate> DiscoverCarriedAsync()
    {
        await using var provider = new ClaudeNativeProvider(new Source(), enablePeerCredentials: true, updateTolerantDelivery: true);
        return (await provider.DiscoverAsync(CancellationToken.None)).Single();
    }

    private static readonly ulong Start = checked((ulong)DateTime.UtcNow.AddDays(-1).ToFileTimeUtc());
    private const string SessionId = "11111111-2222-4333-8444-555555555555";
    private const string Home = @"C:\SyntheticUser";

    private static NativeMessage NativeMessageFor() => new(Guid.NewGuid(), SessionId, "Synthetic update-tolerant message.",
        RecipientMode.Unknown, DelegateNativeAdmission: true, Activity: NativeActivity.Idle);

    private static BrokerMessage Message(SessionAddress recipient)
        => new(Guid.NewGuid(), Guid.NewGuid(), null, new(Guid.NewGuid(), "synthetic-sender", Guid.NewGuid()),
            recipient, "Synthetic update-tolerant message.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class FakeTrust(string? accepts) : IPublisherImageTrust
    {
        internal readonly List<(string Path, string Hash)> Calls = [];
        public bool IsExpectedPublisher(string imagePath, string imageSha256)
        { Calls.Add((imagePath, imageSha256)); return accepts is not null && string.Equals(accepts, imageSha256, StringComparison.OrdinalIgnoreCase); }
    }

    private sealed class MemorySource : IPeerCredentialSource
    {
        internal int Reads;
        public byte[] Read(CredentialTarget target)
        {
            Reads++;
            return Encoding.UTF8.GetBytes("{\"peerToken\":\"0123456789abcdef0123456789abcdef\",\"procStartFt\":\"" + Start + "\",\"pidDomain\":\"win32:synthetic\"}");
        }
    }

    private sealed class FakeFactory(NativeProcessIdentity identity) : INativePeerConnectionFactory
    {
        internal int Connects;
        internal FakeConnection? Last;
        internal NativeProcessIdentity? Reinspection;
        public Task<INativePeerConnection> ConnectAsync(string pipeName, CancellationToken cancellation)
        { Connects++; Last = new FakeConnection(identity, Reinspection ?? identity); return Task.FromResult<INativePeerConnection>(Last); }
    }

    private sealed class FakeConnection(NativeProcessIdentity identity, NativeProcessIdentity reinspection) : INativePeerConnection
    {
        private readonly CaptureStream output = new();
        internal List<byte[]> Frames => output.Frames;
        public bool EnforcesCurrentUser => true;
        public NativeProcessIdentity Identity => identity;
        public Stream Outbound => output;
        public NativeProcessIdentity Reinspect() => reinspection;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaptureStream : Stream
    {
        internal readonly List<byte[]> Frames = [];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> value, CancellationToken cancellation = default)
        { Frames.Add(value.ToArray()); return ValueTask.CompletedTask; }
        public override Task FlushAsync(CancellationToken cancellation) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CandidateProvider(ClaudeNativeCandidate current) : IClaudeNativeProvider
    {
        internal ClaudeNativeCandidate? Refreshed;
        internal int Refreshes, Submissions;
        internal NativeTarget? LastTarget;
        public Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<ClaudeNativeCandidate>>([current]);
        public Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token)
        { Refreshes++; return Task.FromResult<ClaudeNativeCandidate?>(Refreshed ?? current); }
        public Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token)
        { Submissions++; LastTarget = target; return Task.FromResult(new SubmissionResult(SubmissionStatus.SubmittedUnconfirmed, "synthetic")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    private sealed class Source : IClaudeDiscoverySource
    {
        internal int Pid = 42001;
        internal ulong Started = checked((ulong)DateTime.UtcNow.AddDays(-1).ToFileTimeUtc());
        internal string Image = @"C:\Synthetic-Claude-Tolerant\claude.exe";
        internal string Version = UpdatedVersion;
        internal string Hash = new('C', 64);
        internal int TrustReads;
        internal ClaudePublicImageTrustResult Trust = new(ClaudePublicImageTrust.ExpectedPublisher, SignedHash);
        internal NativeSessionMetadata Metadata;
        internal Source()
        {
            Metadata = new(Pid, Pipe, Guid.NewGuid().ToString("D"), Started, "interactive", "idle", "vscode")
            { UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        }
        public IReadOnlyList<int> GetProcessIds() => [Pid];
        public NativeSessionMetadata ReadMetadata(int processId) => Metadata;
        public IClaudeDiscoveryProcess OpenProcess(int processId) => new ProcessView(this);
        public void RejectReparseComponents(string image) { }
        public string? ReadProductVersion(string image) => Version;
        public string ReadImageSha256(string image) => Hash;
        public ClaudePublicImageTrustResult ObserveUnpinnedPublicImage(string image) { TrustReads++; return Trust with { }; }
        private sealed class ProcessView(Source source) : IClaudeDiscoveryProcess
        {
            public string ProcessName => "claude";
            public string ImagePath => source.Image;
            public ulong StartFileTime => source.Started;
            public bool IsCurrentUser => true;
            public bool HasExited => false;
            public void Dispose() { }
        }
    }
}
