using AgentBroker.CodexDesktopAdapter;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

// Codex observation-only discovery and the opt-in update-tolerant delivery gate at the adapter library layer.
// Every label starts with this file's name: the mutation runner matches it in the failure output.
internal static class CodexObservationChecks
{
    private const string File = "CodexObservationChecks.cs: ";
    private static readonly string WindowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    private static readonly string UpdatedDesktop = Path.Combine(WindowsApps, "OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");

    internal static async Task RunAsync(Action<bool, string> check)
    {
        PublisherSubjects(check);
        ProtectedDescriptors(check);
        RouterFallback(check);
        PublisherRouter(check);
        RunningEngines(check);
        await AdapterGateAsync(check);
        await TrustProbeAsync(check);
    }

    private static async Task TrustProbeAsync(Action<bool, string> check)
    {
        var observed = new TrustProbeConnection(DesktopRouterTrust.PublisherSigned)
        { ExactPinRefusal = new DesktopAdapterException("unsupported_desktop_version", "26.1021.3705.0") };
        var result = await DesktopConnectionProfileProbe.ObserveTrustForTestAsync((_, _) => Task.FromResult<IVerifiedDesktopConnection>(observed));
        check(result == new DesktopConnectionTrustResult(DesktopRouterProfile.Desktop, DesktopRouterTrust.PublisherSigned,
            "unsupported_desktop_version", "26.1021.3705.0") && observed.Disposed,
            File + "trust probe: reports publisher observation with the failed pin and closes the connection");
        try
        {
            await DesktopConnectionProfileProbe.ObserveTrustForTestAsync((_, _) =>
                Task.FromResult<IVerifiedDesktopConnection>(new TrustProbeConnection(DesktopRouterTrust.Unverified)));
            throw new InvalidOperationException(File + "trust probe accepted an unverified connection.");
        }
        catch (DesktopAdapterException error)
        {
            check(error.Code == "invalid_connection_profile", File + "trust probe: an unverified connection is refused, never reported");
        }
    }

    private sealed class TrustProbeConnection(DesktopRouterTrust trust) : IVerifiedDesktopConnection
    {
        internal bool Disposed;
        public Stream Stream => throw new InvalidOperationException(File + "trust probe must not touch the protocol stream.");
        public OwnerProcessIdentity Identity => throw new InvalidOperationException(File + "trust probe must not read identities.");
        public DesktopRouterProfile Profile => DesktopRouterProfile.Desktop;
        public DesktopRouterTrust Trust => trust;
        public DesktopAdapterException? ExactPinRefusal { get; init; }
        public void VerifyStillSameOwner() { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>Read-only smoke against one installed public image; prints nothing but the trust outcome.</summary>
    internal static void RunPublicImageCheck(Action<bool, string> check, string image, string publisher, bool requireProtected)
    {
        var trust = WindowsPublisherImageTrust.Verify(image, publisher, requireProtected);
        Console.WriteLine("Codex public-image smoke: trust=" + trust + " protectedLocationRequired=" + requireProtected);
        check(trust == PublisherImageTrust.ExpectedPublisher,
            File + "public-image smoke: Windows Authenticode selected the expected verified signer (" + trust + ")");
    }

    /// <summary>
    /// Read-only smoke of the complete publisher router branch for one installed image (signature, protected package
    /// location and archive; for Code also running-engine metadata). No pipe is opened and nothing is sent.
    /// </summary>
    internal static void RunPublisherRouterCheck(Action<bool, string> check, string image)
    {
        var profile = WindowsDesktopConnection.VerifyPublisherSignedRouter(image);
        Console.WriteLine("Codex publisher router smoke: profile=" + profile);
        check(profile is DesktopRouterProfile.Desktop or DesktopRouterProfile.VsCode,
            File + "publisher router smoke: the installed router passes publisher verification (" + profile + ")");
    }

    private static void PublisherSubjects(Action<bool, string> check)
    {
        const string openAI = WindowsPublisherImageTrust.OpenAIPublisher;
        check(WindowsPublisherImageTrust.IsExpectedPublisher(new X500DistinguishedName(
            @"CN=""OpenAI OpCo, LLC"", O=""OpenAI OpCo, LLC"", L=San Francisco, S=California, C=US"), openAI),
            File + "OpenAI signer policy: exact legal common name and organization match");
        check(WindowsPublisherImageTrust.IsExpectedPublisher(new X500DistinguishedName(
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"), WindowsPublisherImageTrust.MicrosoftPublisher),
            File + "Microsoft router signer policy: exact legal common name and organization match");
        foreach (string subject in new[]
        {
            @"CN=""OpenAI OpCo, LLC"", O=Different Publisher",
            @"CN=Different Publisher, O=""OpenAI OpCo, LLC""",
            @"CN=""Not OpenAI OpCo, LLC"", O=""OpenAI OpCo, LLC""",
            @"CN=""OpenAI OpCo, LLC"", O=""OpenAI OpCo, LLC Extra""",
            @"CN=""OpenAI OpCo, LLC""",
            @"O=""OpenAI OpCo, LLC""",
            @"CN=""OpenAI OpCo, LLC"", CN=""OpenAI OpCo, LLC"", O=""OpenAI OpCo, LLC""",
            @"CN=""OpenAI OpCo, LLC"", O=""OpenAI OpCo, LLC"", O=""OpenAI OpCo, LLC""",
            "CN=Microsoft Corporation, O=Microsoft Corporation",
        })
            check(!WindowsPublisherImageTrust.IsExpectedPublisher(new X500DistinguishedName(subject), openAI),
                File + "OpenAI signer policy: wrong, incomplete, substring, duplicate or other-vendor attributes refuse");
    }

    private static void ProtectedDescriptors(Action<bool, string> check)
    {
        // The installed WindowsApps image's own descriptor: SYSTEM owner, TrustedInstaller and SYSTEM full control,
        // conditional and plain read/execute grants for everyone else.
        const string packaged = "O:SYD:AI(XA;ID;0x1200a9;;;BU;(WIN://SYSAPPID Contains \"OpenAI.Codex_2p2nqsd0c76g0\"))" +
            "(A;ID;0x1200a9;;;AC)(A;ID;0x1200a9;;;BU)(A;ID;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)" +
            "(A;ID;FA;;;SY)(A;ID;0x1200a9;;;LS)";
        check(WindowsPublisherImageTrust.IsProtectedDescriptor(new RawSecurityDescriptor(packaged)),
            File + "protected location: the packaged-app descriptor is writable only by SYSTEM and TrustedInstaller");
        foreach (string unprotected in new[]
        {
            packaged.Replace("O:SY", "O:BU", StringComparison.Ordinal),
            packaged + "(A;;0x2;;;BU)",
            packaged + "(A;;FA;;;BA)",
            packaged + "(A;;0x40000;;;WD)",
            "O:SYD:AI(XA;;FA;;;BU;(WIN://SYSAPPID Contains \"x\"))(A;;FA;;;SY)",
            "O:SY",
        })
            check(!WindowsPublisherImageTrust.IsProtectedDescriptor(new RawSecurityDescriptor(unprotected)),
                File + "protected location: a user owner, any foreign write grant, a conditional write or a null DACL refuses");
        check(WindowsPublisherImageTrust.IsProtectedDescriptor(new RawSecurityDescriptor(packaged + "(D;;FA;;;BU)")),
            File + "WITNESS: a deny ACE never counts as a write grant");
    }

    private static void RouterFallback(Action<bool, string> check)
    {
        int publisherCalls = 0;
        DesktopRouterProfile Publisher(string _) { publisherCalls++; return DesktopRouterProfile.Desktop; }
        void Mismatch(string _) => throw new DesktopAdapterException("unsupported_desktop_version", "26.1021.3705.0");

        var exact = WindowsDesktopConnection.VerifyRouter(UpdatedDesktop, true, _ => { }, _ => { }, Publisher);
        check(exact.Trust == DesktopRouterTrust.ExactPinned && exact.ExactPinRefusal is null && publisherCalls == 0,
            File + "router trust: an exact pin keeps the reviewed profile and never consults the publisher");

        try
        {
            WindowsDesktopConnection.VerifyRouter(UpdatedDesktop, false, _ => { }, Mismatch, Publisher);
            throw new InvalidOperationException(File + "strict router verification accepted an unpinned build.");
        }
        catch (DesktopAdapterException error)
        {
            check(error.Code == "unsupported_desktop_version" && publisherCalls == 0,
                File + "router trust: strict verification keeps the exact refusal without publisher fallback");
        }

        var observed = WindowsDesktopConnection.VerifyRouter(UpdatedDesktop, true, _ => { }, Mismatch, Publisher);
        check(observed.Trust == DesktopRouterTrust.PublisherSigned && observed.ExactPinRefusal?.Code == "unsupported_desktop_version" &&
            observed.ExactPinRefusal.InstalledVersion == "26.1021.3705.0" && publisherCalls == 1,
            File + "router trust: an exact-pin mismatch falls back to publisher observation and keeps the refusal");

        foreach (string hard in new[] { "linked_desktop_image", "vscode_process_snapshot_unverified", "server_user_mismatch" })
        {
            try
            {
                WindowsDesktopConnection.VerifyRouter(UpdatedDesktop, true, _ => { },
                    _ => throw new DesktopAdapterException(hard), Publisher);
                throw new InvalidOperationException(File + "an identity refusal fell back to publisher trust.");
            }
            catch (DesktopAdapterException error)
            {
                check(error.Code == hard && publisherCalls == 1,
                    File + "router trust: a linked image or identity refusal never reaches publisher fallback");
            }
        }

        try
        {
            WindowsDesktopConnection.VerifyRouter(UpdatedDesktop, true, _ => { }, Mismatch,
                _ => throw new DesktopAdapterException("public_image_publisher_mismatch"));
            throw new InvalidOperationException(File + "a wrong publisher was observed.");
        }
        catch (DesktopAdapterException error)
        {
            check(error.Code == "public_image_publisher_mismatch" && error.InstalledVersion == "26.1021.3705.0",
                File + "router trust: a failed publisher check reports its own code with the installed version");
        }
    }

    private static void PublisherRouter(Action<bool, string> check)
    {
        var calls = new List<(string Image, string Publisher, bool Protected)>();
        PublisherImageTrust result = PublisherImageTrust.ExpectedPublisher;
        bool archiveProtected = true;
        int engines = 1;
        PublisherImageTrust Verify(string image, string publisher, bool requireProtected)
        { calls.Add((image, publisher, requireProtected)); return result; }
        DesktopRouterProfile Run(string image) =>
            WindowsDesktopConnection.VerifyPublisherSignedRouter(image, Verify, _ => archiveProtected, () => engines);

        check(Run(UpdatedDesktop) == DesktopRouterProfile.Desktop && calls.Single() ==
            (UpdatedDesktop, WindowsPublisherImageTrust.OpenAIPublisher, true),
            File + "desktop publisher router: OpenAI signature with the protected WindowsApps location required");
        calls.Clear();
        foreach (string elsewhere in new[]
        {
            Path.Combine(Path.GetTempPath(), "OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe"),
            Path.Combine(WindowsApps, "OpenAI.Codex_26.1021.3705.0_x64__otherpublisher", "app", "ChatGPT.exe"),
            Path.Combine(WindowsApps, "Other.Product_26.1021.3705.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe"),
        })
            Refuses(check, "unsupported_desktop_version", () => Run(elsewhere),
                File + "desktop publisher router: an image outside the OpenAI package location refuses before any signature read");
        check(calls.Count == 0, File + "desktop publisher router: no signature is read for an unrelated location");
        foreach (var (trust, code) in new[]
        {
            (PublisherImageTrust.UnexpectedPublisher, "public_image_publisher_mismatch"),
            (PublisherImageTrust.UntrustedSignature, "public_image_signature_untrusted"),
            (PublisherImageTrust.UnprotectedLocation, "public_image_location_unprotected"),
            (PublisherImageTrust.Unavailable, "public_image_trust_unavailable"),
        })
        {
            result = trust;
            Refuses(check, code, () => Run(UpdatedDesktop),
                File + "desktop publisher router: wrong publisher, invalid signature, unprotected or unavailable trust refuses");
        }
        result = PublisherImageTrust.ExpectedPublisher;
        archiveProtected = false;
        Refuses(check, "public_image_location_unprotected", () => Run(UpdatedDesktop),
            File + "desktop publisher router: an archive writable outside the protected location refuses");
        archiveProtected = true;

        calls.Clear();
        string router = VsCodeRouterCompatibilityProfile.ExpectedImage;
        check(Run(router) == DesktopRouterProfile.VsCode && calls.Single() == (router, WindowsPublisherImageTrust.MicrosoftPublisher, false),
            File + "Code publisher router: Microsoft signature plus a publisher-verified running engine");
        engines = 0;
        Refuses(check, "vscode_router_engine_unverified", () => Run(router),
            File + "Code publisher router: no verified running engine refuses");
        engines = 1; result = PublisherImageTrust.UnexpectedPublisher;
        Refuses(check, "public_image_publisher_mismatch", () => Run(router),
            File + "Code publisher router: a router image without the Microsoft signature refuses");
    }

    private static void RunningEngines(Action<bool, string> check)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        string engine = Path.Combine(root, "openai.chatgpt-26.1021.22027-win32-x64", "bin", "windows-x86_64", "codex.exe");
        string parent = Path.Combine("C:\\", "Synthetic", "Microsoft VS Code", "Code.exe");
        check(VsCodeCompatibilityProfile.PublisherEngineVersion(engine, root) == "26.1021.22027",
            File + "running engine layout: the installed extension version is read from its exact folder");
        foreach (string wrong in new[]
        {
            Path.Combine(root, "openai.chatgpt-26.1021.22027-win32-x64", "bin", "windows-x86_64", "other.exe"),
            Path.Combine(root, "openai.chatgpt-26.1021.22027-win32-x64", "bin", "linux-x86_64", "codex.exe"),
            Path.Combine(root, "other.extension-26.1021.22027-win32-x64", "bin", "windows-x86_64", "codex.exe"),
            Path.Combine(root, "openai.chatgpt-latest-win32-x64", "bin", "windows-x86_64", "codex.exe"),
            Path.Combine(root, "openai.chatgpt-26.1021.22027.4-win32-x64", "bin", "windows-x86_64", "codex.exe"),
            Path.Combine(Path.GetTempPath(), "openai.chatgpt-26.1021.22027-win32-x64", "bin", "windows-x86_64", "codex.exe"),
        })
            check(VsCodeCompatibilityProfile.PublisherEngineVersion(wrong, root) is null,
                File + "running engine layout: other names, platforms, versions or roots are not an OpenAI extension engine");

        var trust = PublisherImageTrust.ExpectedPublisher;
        int verified = 0;
        int Validate(params CodeOwnedEngine[] running) =>
            VsCodeCompatibilityProfile.ValidateRunningClientsByPublisher(running, _ => { verified++; return trust; });
        var good = new CodeOwnedEngine(engine, 200, parent, 100, true);
        check(Validate(good, good) == 2 && verified == 2,
            File + "running engines: every same-user Code-owned engine is publisher verified");
        check(Validate() == 0, File + "running engines: no running engine verifies nothing");
        foreach (var bad in new[]
        {
            good with { SameUser = false }, good with { ParentCreationFileTime = 300 }, good with { ParentImage = "C:\\Synthetic\\Other.exe" },
            good with { Image = Path.Combine(Path.GetTempPath(), "codex.exe") },
        })
            Refuses(check, "unsupported_running_vscode_profile", () => Validate(good, bad),
                File + "running engines: another user, a later parent, a foreign parent or a foreign path refuses");
        trust = PublisherImageTrust.UnexpectedPublisher;
        Refuses(check, "public_image_publisher_mismatch", () => Validate(good),
            File + "running engines: an engine without the OpenAI signature refuses");
    }

    private static async Task AdapterGateAsync(Action<bool, string> check)
    {
        var thread = Guid.NewGuid();
        var observe = new DesktopAdapterOptions(RequestTimeout: TimeSpan.FromMilliseconds(250), EnableExternalPeerDelivery: true)
            { AllowPublisherSignedRouter = true };

        await using (var connection = new FakeConnection { Trust = DesktopRouterTrust.PublisherSigned })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, observe))
        {
            var binding = await adapter.DiscoverAsync(thread);
            check(binding is { RouterTrust: DesktopRouterTrust.PublisherSigned, SupportsUntrustedAppInput: true } &&
                adapter.RouterTrust == DesktopRouterTrust.PublisherSigned,
                File + "adapter observation: a publisher-signed router binds the exact owner with its own trust");
            int discoveries = connection.DiscoveryCount, requests = connection.Requests.Count;
            var refused = await adapter.SubmitExternalPeerAsync(binding!, Guid.NewGuid(), "Synthetic external input.");
            check(refused.Outcome == DesktopSubmissionOutcome.Rejected && refused.ErrorCode == "router_observation_only" &&
                connection.SubmissionCount == 0 && connection.DiscoveryCount == discoveries && connection.Requests.Count == requests,
                File + "adapter observation guard: a publisher-signed router refuses before any frame, rediscovery or input");
        }

        await using (var connection = new FakeConnection { Trust = DesktopRouterTrust.PublisherSigned, Capability = false })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, observe))
        {
            var binding = await adapter.DiscoverAsync(thread);
            check(binding is { RouterTrust: DesktopRouterTrust.PublisherSigned, SupportsUntrustedAppInput: false },
                File + "adapter observation: an updated owner without the native capability stays observable without it");
        }
        await using (var connection = new FakeConnection { Capability = false })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, observe))
        {
            try { await adapter.DiscoverAsync(thread); throw new InvalidOperationException(File + "exact owner without capability was bound."); }
            catch (DesktopAdapterException error)
            {
                check(error.Code == "owner_capability_mismatch",
                    File + "WITNESS: an exactly pinned owner without the capability is still a protocol fault");
            }
        }

        var tolerant = observe with { EnableUpdateTolerantDelivery = true };
        await using (var connection = new FakeConnection { Trust = DesktopRouterTrust.PublisherSigned })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, tolerant))
        {
            var binding = (await adapter.DiscoverAsync(thread))!;
            var accepted = await adapter.SubmitExternalPeerAsync(binding, Guid.NewGuid(), "Synthetic external input.");
            check(accepted.Outcome == DesktopSubmissionOutcome.Accepted && connection.SubmissionCount == 1,
                File + "WITNESS: update-tolerant opt-in admits a publisher-signed router through the unchanged owner path");
            var forged = binding with { RouterTrust = DesktopRouterTrust.ExactPinned };
            try { await adapter.SubmitExternalPeerAsync(forged, Guid.NewGuid(), "x"); throw new InvalidOperationException(File + "forged trust accepted."); }
            catch (DesktopAdapterException error)
            {
                check(error.Code == "stale_binding" && connection.SubmissionCount == 1,
                    File + "adapter observation: a binding whose trust differs from its connection is stale");
            }
            connection.SubmitFailure = "busy-context";
            var busy = await adapter.SubmitExternalPeerAsync(binding, Guid.NewGuid(), "Synthetic external input.");
            check(busy.Outcome == DesktopSubmissionOutcome.DeferredNotSubmitted && busy.ErrorCode == "native_context_busy",
                File + "update-tolerant busy: the exact typed native busy error still proves no input");
        }

        await using (var connection = new FakeConnection { Trust = DesktopRouterTrust.Unverified })
        await using (var adapter = await DesktopAdapter.ConnectForTestAsync(connection, tolerant))
        {
            var refused = await adapter.SubmitExternalPeerAsync(new(thread, connection.OwnerId, Guid.NewGuid(), 1234, 123456789, true),
                Guid.NewGuid(), "x");
            check(refused.Outcome == DesktopSubmissionOutcome.Rejected && connection.SubmissionCount == 0,
                File + "adapter observation guard: an unverified connection never delivers, even under update tolerance");
        }

        foreach (var invalid in new[]
        {
            new DesktopAdapterOptions(EnableExternalPeerDelivery: true) { EnableUpdateTolerantDelivery = true },
            new DesktopAdapterOptions { AllowPublisherSignedRouter = true, EnableUpdateTolerantDelivery = true },
        })
        {
            try { invalid.Validate(); throw new InvalidOperationException(File + "invalid tolerant options validated."); }
            catch (DesktopAdapterException error)
            {
                check(error.Code == "invalid_options",
                    File + "options: update tolerance requires both publisher routers and external delivery");
            }
        }
    }

    private static void Refuses(Action<bool, string> check, string code, Func<object> action, string label)
    {
        try { _ = action(); throw new InvalidOperationException(label + " (accepted)"); }
        catch (DesktopAdapterException error) { check(error.Code == code, label + " (" + error.Code + ")"); }
    }
}
