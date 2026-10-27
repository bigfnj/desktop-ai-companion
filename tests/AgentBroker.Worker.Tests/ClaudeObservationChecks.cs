using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using AgentBroker.ClaudeAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

internal static class ClaudeObservationChecks
{
    // The reviewed pin lives in the embedded pin document (lane broker-pins); selected by version.
    private static readonly EngineProfile Pin21306 = EngineProfile.Verified.Single(pin => pin.Version == "2.1.306");

    internal static async Task RunAsync(Action<bool, string> check)
    {
        PublisherSubjectChecks(check);
        await SignedObservationAsync(check);
        await ImageLeaseAsync(check);
        await RefusalsAsync(check);
        await CapabilitiesAndSubmissionAsync(check);
        await PinnedBehaviorAsync(check);
    }

    internal static void RunPublicImageCheck(Action<bool, string> check, string image)
    {
        using var trust = WindowsClaudePublicImageTrust.Observe(image);
        check(trust.Trust == ClaudePublicImageTrust.ExpectedPublisher,
            "Claude public-image smoke: Windows Authenticode selected the expected verified signer (" + trust.Trust + ")");
        check(trust.ImageSha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit),
            "Claude public-image smoke: held public executable has a complete SHA-256");
    }

    internal static async Task RunPublicDiscoveryCheckAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var provider = new ClaudeNativeProvider();
        var elapsed = Stopwatch.StartNew();
        var rows = await provider.DiscoverAsync(deadline.Token);
        elapsed.Stop();
        Console.WriteLine("Claude public observation smoke: count=" + rows.Count +
            " signedObservationCount=" + rows.Count(row => row.Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation) +
            " exactPinnedCount=" + rows.Count(row => row.CanDeliver) +
            " elapsedMilliseconds=" + elapsed.ElapsedMilliseconds +
            " diagnosticCodes=" + string.Join(",", provider.DiscoveryDiagnostics.Select(item => item.Code).Distinct().Order()));
        check(elapsed.Elapsed < TimeSpan.FromSeconds(10),
            "Claude public observation smoke: complete process pass fits the ten-second discovery deadline");
        check(rows.Any(row => row.Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation),
            "Claude public observation smoke: an actual owned signed updated session is observable");
        check(rows.Where(row => row.Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation).All(row => !row.CanDeliver),
            "Claude public observation smoke: actual signed observations grant no native delivery authority");
    }

    private static void PublisherSubjectChecks(Action<bool, string> check)
    {
        check(WindowsClaudePublicImageTrust.IsExpectedPublisher(
            new X500DistinguishedName(@"CN=""Anthropic, PBC"", O=""Anthropic, PBC"", C=US")),
            "Claude signer policy: exact legal common name and organization match");
        foreach (string subject in new[]
        {
            @"CN=""Anthropic, PBC"", O=Different Publisher",
            @"CN=Different Publisher, O=""Anthropic, PBC""",
            @"CN=""Not Anthropic, PBC"", O=""Anthropic, PBC""",
            @"CN=""Anthropic, PBC"", O=""Anthropic, PBC Extra""",
            @"CN=""Anthropic, PBC""",
            @"O=""Anthropic, PBC""",
            @"CN=""Anthropic, PBC"", CN=""Anthropic, PBC"", O=""Anthropic, PBC""",
            @"CN=""Anthropic, PBC"", O=""Anthropic, PBC"", O=""Anthropic, PBC""",
        })
            check(!WindowsClaudePublicImageTrust.IsExpectedPublisher(new X500DistinguishedName(subject)),
                "Claude signer policy: wrong, incomplete, substring or duplicate publisher attributes refuse");
    }

    private static async Task SignedObservationAsync(Action<bool, string> check)
    {
        var source = new ObservationSource();
        await using var provider = new ClaudeNativeProvider(source);
        var first = (await provider.DiscoverAsync(CancellationToken.None)).Single();
        check(first.Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation && !first.CanDeliver,
            "Claude observation: signed updated executable is explicitly observation-only");
        check(first.Target.EngineVersion == source.Version && first.Target.ImageSha256 == source.Trust.ImageSha256 &&
            first.Status == "idle", "Claude observation: old idle metadata remains visible for the exact live process");
        check(provider.DiscoveryDiagnostics.Any(item => item.Code == "unsupported_public_version"),
            "Claude observation: unsupported native delivery stays diagnosed even when observation succeeds");
        await provider.DiscoverAsync(CancellationToken.None);
        check(source.TrustReads == 2,
            "Claude observation: unpinned trust is freshly verified on each pass without a stale signature cache");
        source.Metadata = source.Metadata with { Status = "waiting" };
        check((await provider.RefreshAsync(source.Pid, CancellationToken.None))?.Status == "waiting",
            "Claude observation: refresh retains current bounded activity hints");
        source.Metadata = source.Metadata with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        check((await provider.RefreshAsync(source.Pid, CancellationToken.None))?.Status is null,
            "Claude observation: implausible activity remains unknown");
        source.Metadata = source.Metadata with { UpdatedAt = DateTimeOffset.UtcNow };
        source.Version = Pin21306.Version;
        source.Hash = new string('B', 64);
        var reissued = (await provider.DiscoverAsync(CancellationToken.None)).Single();
        check(reissued.Provenance == ClaudeDiscoveryProvenance.PublisherSignedObservation && !reissued.CanDeliver &&
            provider.DiscoveryDiagnostics.Any(item => item.Code == "image_hash_mismatch"),
            "Claude observation: signed same-version image mismatch never becomes an exact delivery profile");
    }

    private static async Task ImageLeaseAsync(Action<bool, string> check)
    {
        var lease = new TrackingLease();
        var source = new ObservationSource { Lease = lease };
        await using var provider = new ClaudeNativeProvider(source);
        check((await provider.DiscoverAsync(CancellationToken.None)).Count == 1 &&
            source.LeaseRechecks > 0 && source.LeaseHeldThroughoutRecheck && lease.Disposed,
            "Claude observation image lease guard: hold the attested executable through process recheck and release it before returning");
    }

    private static async Task RefusalsAsync(Action<bool, string> check)
    {
        foreach (ClaudePublicImageTrust rejected in new[]
            { ClaudePublicImageTrust.Unavailable, ClaudePublicImageTrust.UntrustedSignature, ClaudePublicImageTrust.UnexpectedPublisher })
        {
            var source = new ObservationSource { Trust = new(rejected, new string('A', 64)) };
            await using var provider = new ClaudeNativeProvider(source);
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 &&
                provider.DiscoveryDiagnostics.Any(item => item.Code.StartsWith("public_image_", StringComparison.Ordinal)),
                "Claude observation: unavailable, invalid or wrong-publisher signature refuses with a diagnostic");
        }
        foreach (string? hash in new[] { null, "", "A", new string('Z', 64) })
        {
            var source = new ObservationSource { Trust = new(ClaudePublicImageTrust.ExpectedPublisher, hash) };
            await using var provider = new ClaudeNativeProvider(source);
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0,
                "Claude observation: publisher result without a complete executable hash refuses");
        }
        foreach (bool exited in new[] { false, true })
        {
            var source = new ObservationSource { SameUser = false, Exited = exited };
            await using var provider = new ClaudeNativeProvider(source);
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 && source.TrustReads == 0,
                "Claude observation: another user's process never reaches public signature verification");
        }
        var deadSource = new ObservationSource { Exited = true };
        await using (var provider = new ClaudeNativeProvider(deadSource))
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 && deadSource.TrustReads == 0,
                "Claude observation: exited process never reaches signature verification");
        foreach (string change in new[] { "start", "path", "user", "exit" })
        {
            var source = new ObservationSource();
            source.AfterTrust = () =>
            {
                switch (change)
                {
                    case "start": source.Started++; break;
                    case "path": source.Image += ".replacement"; break;
                    case "user": source.SameUser = false; break;
                    case "exit": source.Exited = true; break;
                }
            };
            await using var provider = new ClaudeNativeProvider(source);
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 && source.TrustReads == 1,
                "Claude observation: process identity changed during signature inspection refuses (" + change + ")");
        }
        var reparse = new ObservationSource { Reparse = true };
        await using (var provider = new ClaudeNativeProvider(reparse))
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 && reparse.TrustReads == 0,
                "Claude observation: reparse component refuses before signature verification");
        var lifetime = new ObservationSource();
        lifetime.Started++;
        await using (var provider = new ClaudeNativeProvider(lifetime))
            check((await provider.DiscoverAsync(CancellationToken.None)).Count == 0 && lifetime.TrustReads == 0,
                "Claude observation: metadata/process creation mismatch refuses before signature verification");
    }

    private static async Task CapabilitiesAndSubmissionAsync(Action<bool, string> check)
    {
        var source = new ObservationSource();
        ClaudeNativeCandidate observed;
        await using (var discovery = new ClaudeNativeProvider(source))
            observed = (await discovery.DiscoverAsync(CancellationToken.None)).Single();
        foreach (bool credentials in new[] { false, true })
        {
            var provider = new CandidateProvider(observed);
            await using var adapter = new ClaudeNativeAdapter(provider, credentials);
            var session = (await adapter.DiscoverAsync(Guid.NewGuid(), CancellationToken.None)).Single();
            check(session.Capabilities == SessionCapabilities.None && session.Activity == SessionActivity.Idle,
                "Claude observation capability guard: signed observation stays visible with zero capabilities, credentials=" + credentials);
            var response = await adapter.SubmitAsync(Message(session.Address), CancellationToken.None);
            check(response.Outcome == AdapterAdmission.Refused && provider.Submissions == 0 && provider.Refreshes == 0 &&
                response.Reason == (credentials ? "claude_observation_only" : "claude_credentials_disabled"),
                "Claude observation submission guard: refuse before provider refresh, transport or credentials, credentials=" + credentials);
        }
        var pinned = observed with
        {
            Target = observed.Target with { EngineVersion = Pin21306.Version, ImageSha256 = Pin21306.Sha256 },
            Provenance = ClaudeDiscoveryProvenance.ExactPinned,
        };
        var changing = new CandidateProvider(pinned);
        await using (var adapter = new ClaudeNativeAdapter(changing, true))
        {
            var session = (await adapter.DiscoverAsync(Guid.NewGuid(), CancellationToken.None)).Single();
            changing.Current = observed;
            var outcome = await adapter.SubmitAsync(Message(session.Address), CancellationToken.None);
            check(outcome.Outcome == AdapterAdmission.Refused && outcome.Reason == "claude_observation_only" &&
                changing.Refreshes == 1 && changing.Submissions == 0,
                "Claude observation refreshed submission guard: pinned discovery cannot authorize a later observation-only refresh");
        }
        var forged = new ClaudeNativeCandidate(observed.Target, "idle");
        check(forged.Provenance == ClaudeDiscoveryProvenance.Unverified && !forged.CanDeliver,
            "Claude observation: unsupported target constructor defaults to unverified, never exact pinned");
    }

    private static async Task PinnedBehaviorAsync(Action<bool, string> check)
    {
        var source = new ObservationSource
        { Version = Pin21306.Version, Hash = Pin21306.Sha256 };
        ClaudeNativeCandidate pinned;
        await using (var provider = new ClaudeNativeProvider(source))
        {
            pinned = (await provider.DiscoverAsync(CancellationToken.None)).Single();
            check(pinned.Provenance == ClaudeDiscoveryProvenance.ExactPinned && pinned.CanDeliver && source.TrustReads == 0,
                "Claude observation: exact pinned discovery retains its existing independent path");
        }
        var target = new CandidateProvider(pinned);
        await using var adapter = new ClaudeNativeAdapter(target, true);
        var session = (await adapter.DiscoverAsync(Guid.NewGuid(), CancellationToken.None)).Single();
        check(session.Capabilities == (SessionCapabilities.Receive | SessionCapabilities.WakeIdle | SessionCapabilities.Reply),
            "Claude observation: explicit credential opt-in retains exact-pinned native capabilities");
        var result = await adapter.SubmitAsync(Message(session.Address), CancellationToken.None);
        check(result.Outcome == AdapterAdmission.Submitted && target.Submissions == 1 && target.Refreshes == 1,
            "Claude observation: exact-pinned idle submission still reaches only the fake provider");
    }

    private static BrokerMessage Message(SessionAddress recipient)
        => new(Guid.NewGuid(), Guid.NewGuid(), null, new(Guid.NewGuid(), "synthetic-sender", Guid.NewGuid()),
            recipient, "Synthetic observation refusal witness.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class CandidateProvider(ClaudeNativeCandidate current) : IClaudeNativeProvider
    {
        internal ClaudeNativeCandidate Current = current;
        internal int Refreshes, Submissions;
        public Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<ClaudeNativeCandidate>>([Current]);
        public Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token)
        { Refreshes++; return Task.FromResult<ClaudeNativeCandidate?>(Current); }
        public Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token)
        { Submissions++; return Task.FromResult(new SubmissionResult(SubmissionStatus.SubmittedUnconfirmed, "synthetic")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservationSource : IClaudeDiscoverySource
    {
        internal int Pid = 41001;
        internal ulong Started = checked((ulong)DateTime.UtcNow.AddDays(-1).ToFileTimeUtc());
        internal string Image = @"C:\Synthetic-Claude-Observation\claude.exe";
        internal string Version = "2.1.309.0";
        internal string Hash = new string('C', 64);
        internal bool SameUser = true, Exited, Reparse;
        internal int TrustReads;
        internal Action? AfterTrust;
        internal TrackingLease? Lease;
        internal int LeaseRechecks;
        internal bool LeaseHeldThroughoutRecheck = true, TrustReturned;
        internal ClaudePublicImageTrustResult Trust = new(ClaudePublicImageTrust.ExpectedPublisher, new string('A', 64));
        internal NativeSessionMetadata Metadata;
        internal ObservationSource()
        {
            Metadata = new(Pid, @"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef", Guid.NewGuid().ToString("D"),
                Started, "interactive", "idle", "vscode")
            { UpdatedAt = DateTimeOffset.UtcNow.AddHours(-12) };
        }
        public IReadOnlyList<int> GetProcessIds() => [Pid];
        public NativeSessionMetadata ReadMetadata(int processId) => Metadata;
        public IClaudeDiscoveryProcess OpenProcess(int processId) => new ProcessView(this);
        public void RejectReparseComponents(string image)
        { if (Reparse) throw new IOException("Synthetic reparse component."); }
        public string? ReadProductVersion(string image) => Version;
        public string ReadImageSha256(string image) => Hash;
        public ClaudePublicImageTrustResult ObserveUnpinnedPublicImage(string image)
        {
            TrustReads++; AfterTrust?.Invoke(); TrustReturned = true;
            return Lease is null ? Trust : Trust with { ImageLease = Lease };
        }
        private void RecheckLease()
        {
            if (!TrustReturned || Lease is null) return;
            LeaseRechecks++;
            LeaseHeldThroughoutRecheck &= !Lease.Disposed;
        }
        private sealed class ProcessView(ObservationSource source) : IClaudeDiscoveryProcess
        {
            public string ProcessName => "claude";
            public string ImagePath { get { source.RecheckLease(); return source.Image; } }
            public ulong StartFileTime { get { source.RecheckLease(); return source.Started; } }
            public bool IsCurrentUser { get { source.RecheckLease(); return source.SameUser; } }
            public bool HasExited { get { source.RecheckLease(); return source.Exited; } }
            public void Dispose() { }
        }
    }
    private sealed class TrackingLease : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
