using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// Reporting only. The real adapter/provider compose in-memory owners and directory snapshots;
// no process, pipe, credential, filesystem discovery or model-input operation is invoked.
internal static class CodexTolerantNoteChecks
{
    private const string L = "CodexTolerantNoteChecks.cs: ";
    private const string NoteCode = "codex_update_tolerant_delivery";
    private const string ExtensionVersion = "26.1021.22027";
    private const string DesktopVersion = "26.1021.3705";
    private const string RouterVersion = "1.144.0";
    private static readonly string[] Cases = ["carried", "refused", "no-carry", "transitions", "observer", "combined", "completion"];

    internal static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        if (only is not null && !Cases.Contains(only)) throw new ArgumentException("Unknown Codex tolerant-note case.", nameof(only));
        foreach (string name in Cases.Where(name => only is null || only == name))
            switch (name)
            {
                case "carried": await CarriedAsync(check); break;
                case "refused": await RefusedAsync(check); break;
                case "no-carry": await NoCarryAsync(check); break;
                case "transitions": await TransitionsAsync(check); break;
                case "observer": await ObserverAsync(check); break;
                case "combined": await CombinedAsync(check); break;
                case "completion": await CompletionAsync(check); break;
            }
    }

    private static async Task CarriedAsync(Action<bool, string> check)
    {
        foreach (string code in new[] { "unsupported_vscode_profile", "running_vscode_image_mismatch" })
        {
            await using var f = new Fixture();
            f.ProfileFailure = new DesktopAdapterException(code, ExtensionVersion + ".0");
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && HasWake(rows[0]) && f.Provider.Authorize(f.Owner.Binding) == CodexDeliveryAuthority.UpdateTolerant,
                L + "WITNESS: publisher-verified engine mismatch carries one actual guarded session (" + code + ")");
            check(OnlyNote(f.Diagnostics, ExtensionVersion) && !f.Diagnostics.Any(item => item.Code == code),
                L + "profile carry replaces its exact-pin failure with one build-naming note (" + code + ")");
            check(f.ProfileChecks == 1 && f.PublisherChecks == 1 && f.Owner.Discoveries == 1 && f.Owner.Submissions == 0,
                L + "profile note performs no extra verification lookup or native submission (" + code + ")");
        }

        foreach (var pin in new[]
        {
            new DesktopAdapterException("unsupported_desktop_version", DesktopVersion + ".0"),
            new DesktopAdapterException("unsupported_vscode_router", RouterVersion),
        })
        {
            await using var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned);
            f.Owner.ExactPinRefusal = pin;
            f.Observer.ExactPinRefusal = pin;
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && HasWake(rows[0]) && OnlyNote(f.Diagnostics, pin.Code == "unsupported_vscode_router" ? RouterVersion : DesktopVersion) &&
                !f.Diagnostics.Any(item => item.Code == pin.Code),
                L + "matching carried router replaces only its exact-pin failure with a versioned note (" + pin.Code + ")");
        }
    }

    private static async Task RefusedAsync(Action<bool, string> check)
    {
        foreach (string code in new[] { "public_image_signature_untrusted", "public_image_publisher_mismatch", "public_image_trust_unavailable" })
        {
            await using var f = new Fixture();
            f.ProfileFailure = new DesktopAdapterException("running_vscode_image_mismatch", ExtensionVersion);
            f.PublisherFailure = new DesktopAdapterException(code, ExtensionVersion);
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && rows[0].Capabilities == SessionCapabilities.None && NoCarriedNote(f.Diagnostics) &&
                Error(f.Diagnostics, "running_vscode_image_mismatch") && Error(f.Diagnostics, code),
                L + "failed publisher evidence retains both real errors and never becomes a carried note (" + code + ")");
        }
        await using (var f = new Fixture())
        {
            f.ProfileFailure = new DesktopAdapterException("pin_document_invalid");
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && rows[0].Capabilities == SessionCapabilities.None && Error(f.Diagnostics, "pin_document_invalid") &&
                f.PublisherChecks == 0 && NoCarriedNote(f.Diagnostics),
                L + "a refused pin document remains an error and never attempts publisher substitution");
        }
        await using (var f = new Fixture())
        {
            f.ProfileFailure = new IOException("Synthetic detail must not enter a diagnostic.");
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && rows[0].Capabilities == SessionCapabilities.Receive && Error(f.Diagnostics, "vscode_profile_unavailable") &&
                NoCarriedNote(f.Diagnostics) && !f.Diagnostics.Any(item => item.Description.Contains("Synthetic detail", StringComparison.Ordinal)),
                L + "an incomplete verification keeps its safe unavailable error and never claims a carried build");
        }
    }

    private static async Task NoCarryAsync(Action<bool, string> check)
    {
        foreach (string reason in new[] { "capability", "empty", "missing-owner", "stale-row", "invalid-owner", "adapter-off", "strict" })
        {
            await using var f = new Fixture(adapterTolerant: reason != "adapter-off", tolerant: reason != "strict");
            f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
            if (reason == "capability") f.Owner.Binding = f.Owner.Binding with { SupportsUntrustedAppInput = false };
            if (reason == "empty") f.Observer.Threads = [];
            if (reason == "missing-owner") f.Owner.Missing = true;
            if (reason == "stale-row") f.Observer.Stale = true;
            if (reason == "invalid-owner") f.Owner.Binding = f.Owner.Binding with { OwnerClientId = Guid.Empty };
            var rows = await f.DiscoverAsync();
            check(rows.All(row => !HasWake(row)) && Error(f.Diagnostics, "unsupported_vscode_profile") && NoCarriedNote(f.Diagnostics),
                L + "a pass without an actual carried session preserves the exact failure and emits no carried note (" + reason + ")");
        }
    }

    private static async Task TransitionsAsync(Action<bool, string> check)
    {
        await using var f = new Fixture();
        f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
        await f.DiscoverAsync();
        var carried = f.Diagnostics;
        check(OnlyNote(carried, ExtensionVersion), L + "WITNESS: transition case begins with one actual carried-build note");
        f.Observer.Threads = [];
        await f.DiscoverAsync();
        check(Error(f.Diagnostics, "unsupported_vscode_profile") && NoCarriedNote(f.Diagnostics),
            L + "an empty next pass restores its exact failure instead of retaining a stale carried note");
        check(OnlyNote(carried, ExtensionVersion), L + "a previously published carried diagnostic snapshot remains immutable");
        f.Observer.Threads = [f.Owner.Binding.ConversationId];
        f.ProfileFailure = null;
        var recovered = await f.DiscoverAsync();
        check(recovered.Count == 1 && HasWake(recovered[0]) && f.Diagnostics.Count == 0,
            L + "exact-profile recovery removes both the old note and exact failure");
    }

    private static async Task ObserverAsync(Action<bool, string> check)
    {
        foreach (bool samePid in new[] { false, true })
        {
            await using var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned);
            f.Owner.ExactPinRefusal = new("unsupported_desktop_version", DesktopVersion);
            f.Observer.ExactPinRefusal = new("unsupported_vscode_router", RouterVersion);
            f.Observer.RouterIdentity = samePid
                ? (f.Owner.Binding.ServerPid, f.Owner.Binding.ServerCreationFileTime + 1)
                : (f.Owner.Binding.ServerPid + 1, f.Owner.Binding.ServerCreationFileTime);
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && HasWake(rows[0]) && Error(f.Diagnostics, "unsupported_vscode_router") &&
                f.Diagnostics.Count(item => item.Code == NoteCode && item.Note) == 1 &&
                f.Diagnostics.Single(item => item.Code == NoteCode).Description.Contains(DesktopVersion, StringComparison.Ordinal),
                L + "a different observer router keeps its exact error despite a carried owner (samePid=" + samePid + ")");
        }

        await using (var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned))
        {
            f.Owner.ExactPinRefusal = new("unsupported_desktop_version", DesktopVersion);
            f.Observer.ExactPinRefusal = new("unsupported_vscode_router", RouterVersion);
            f.Observer.RouterIdentity = (0, 0);
            await f.DiscoverAsync();
            check(Error(f.Diagnostics, "unsupported_vscode_router"),
                L + "an observer with unknown process identity cannot borrow the carried owner's promotion");
        }

        await using (var f = new Fixture())
        {
            f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
            f.Observer.ExactPinRefusal = new("unsupported_vscode_profile", ExtensionVersion);
            f.Observer.RouterIdentity = (f.Owner.Binding.ServerPid + 1, f.Owner.Binding.ServerCreationFileTime);
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && HasWake(rows[0]) && Error(f.Diagnostics, "unsupported_vscode_profile") &&
                f.Diagnostics.Count(item => item.Code == NoteCode && item.Note) == 1,
                L + "equal-valued profile and unrelated observer refusals keep the observer error when only the profile is carried");
        }
        await using (var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned))
        {
            f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
            f.Observer.ExactPinRefusal = new("unsupported_vscode_profile", ExtensionVersion);
            f.Owner.ExactPinRefusal = new("unsupported_vscode_profile", ExtensionVersion);
            await f.DiscoverAsync();
            check(OnlyNote(f.Diagnostics, ExtensionVersion),
                L + "equal-valued profile and matching carried router refusals both promote without a duplicate error");
        }
    }

    private static async Task CombinedAsync(Action<bool, string> check)
    {
        await using (var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned))
        {
            f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion + ".0");
            f.Owner.ExactPinRefusal = new("unsupported_vscode_router", RouterVersion);
            f.Observer.ExactPinRefusal = f.Owner.ExactPinRefusal;
            await f.DiscoverAsync();
            check(OnlyNote(f.Diagnostics, ExtensionVersion, RouterVersion),
                L + "one combined note names both carried engine and router builds without duplicate exact errors");
            var note = f.Diagnostics.Single();
            check(note.Description.Contains("Codex VS Code extension", StringComparison.Ordinal) && note.Description.Contains("VS Code router", StringComparison.Ordinal),
                L + "combined build naming distinguishes the extension from its host router");
        }
        await using (var f = new Fixture())
        {
            f.ProfileFailure = new DesktopAdapterException("running_vscode_image_mismatch");
            await f.DiscoverAsync();
            check(f.Diagnostics.Count == 1 && f.Diagnostics[0] is { Code: NoteCode, Note: true } &&
                f.Diagnostics[0].Description.Contains("unknown", StringComparison.OrdinalIgnoreCase),
                L + "a carried build with unavailable version metadata says unknown instead of inventing a pin version");
        }
    }

    private static async Task CompletionAsync(Action<bool, string> check)
    {
        var spy = new CompletionProvider();
        await using var adapter = new CodexNativeAdapter(spy, enableDelivery: true, updateTolerantDelivery: true);
        var first = await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(first.Count == 1 && HasWake(first[0]) && spy.Completions == 1 && spy.Completed.SequenceEqual([spy.Binding]),
            L + "the real adapter completes discovery with its freshly resolved guarded update-tolerant binding");
        spy.SkipOwner = true;
        await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(spy.Completions == 2 && spy.Completed.Count == 0 && adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped"),
            L + "a skipped lookup's retained old binding is never reported as a newly carried session");
        spy.SkipOwner = false; spy.Authority = CodexDeliveryAuthority.ExactPinned;
        spy.Binding = spy.Binding with { RouterTrust = DesktopRouterTrust.ExactPinned };
        var exact = await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(exact.Count == 1 && HasWake(exact[0]) && spy.Completed.Count == 0,
            L + "a guarded exact-pin session is never mislabeled as carried update-tolerant delivery");
        spy.Authority = CodexDeliveryAuthority.UpdateTolerant;
        spy.Binding = spy.Binding with { RouterTrust = DesktopRouterTrust.PublisherSigned, SupportsUntrustedAppInput = false };
        await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(spy.Completed.Count == 0, L + "an unsupported native capability is excluded from carried completion evidence");
        spy.Threads = [];
        await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(spy.Completions == 5 && spy.Completed.Count == 0,
            L + "the real adapter finalizes even an empty completed pass with empty carried evidence");

        await using var f = new Fixture();
        f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
        Guid later = Guid.NewGuid();
        f.Observer.Threads = [f.Owner.Binding.ConversationId, later];
        f.Owner.FailingThread = later;
        var admitted = await f.DiscoverAsync();
        check(admitted.Count == 1 && HasWake(admitted[0]) && f.Owner.Discoveries == 2 && !f.Provider.NativeIdleGuard,
            L + "WITNESS: a later lookup retires the real provider connection after its first row was guarded and admitted");
        check(f.Diagnostics.Count(item => item.Code == NoteCode && item.Note) == 1 &&
            f.Diagnostics.Single(item => item.Code == NoteCode).Description.Contains(ExtensionVersion, StringComparison.Ordinal) &&
            !f.Diagnostics.Any(item => item.Code == "unsupported_vscode_profile") &&
            f.Diagnostics.Any(item => item.Code == "codex_owner_lookup_skipped"),
            L + "later connection retirement preserves the earlier admission's carried note alongside the skipped-lookup diagnostic");
    }

    private static bool HasWake(BrokerSession row) => row.Capabilities.HasFlag(SessionCapabilities.Receive | SessionCapabilities.WakeIdle);
    private static bool Error(IReadOnlyList<AdapterDiscoveryDiagnostic> diagnostics, string code) => diagnostics.Any(item => item.Code == code && !item.Note);
    private static bool NoCarriedNote(IReadOnlyList<AdapterDiscoveryDiagnostic> diagnostics) => diagnostics.All(item => item.Code != NoteCode);
    private static bool OnlyNote(IReadOnlyList<AdapterDiscoveryDiagnostic> diagnostics, params string[] versions)
        => diagnostics.Count == 1 && diagnostics[0] is { Code: NoteCode, Note: true } &&
            versions.All(version => diagnostics[0].Description.Contains(version, StringComparison.Ordinal));

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Owner Owner;
        internal readonly Observer Observer;
        internal readonly CodexNativeProvider Provider;
        internal readonly CodexNativeAdapter Adapter;
        internal Exception? ProfileFailure, PublisherFailure;
        internal int ProfileChecks, PublisherChecks;
        internal IReadOnlyList<AdapterDiscoveryDiagnostic> Diagnostics => Adapter.DiscoveryDiagnostics;
        internal Fixture(DesktopRouterTrust routerTrust = DesktopRouterTrust.ExactPinned, bool tolerant = true, bool adapterTolerant = true)
        {
            Owner = new(routerTrust);
            Observer = new(Owner.Binding);
            Provider = new(true, _ => Task.FromResult<ICodexOwnerConnection>(Owner),
                () => { ProfileChecks++; if (ProfileFailure is { } failure) throw failure; }, null,
                _ => Task.FromResult<ICodexDirectoryObserver>(Observer), tolerant,
                () => { PublisherChecks++; if (PublisherFailure is { } failure) throw failure; });
            Adapter = new(Provider, enableDelivery: true, updateTolerantDelivery: adapterTolerant);
        }
        internal Task<IReadOnlyList<BrokerSession>> DiscoverAsync() => Adapter.DiscoverAsync(Guid.NewGuid(), default);
        public ValueTask DisposeAsync() => Adapter.DisposeAsync();
    }

    private sealed class Owner(DesktopRouterTrust trust) : ICodexOwnerConnection
    {
        internal DesktopOwnerBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = trust };
        internal bool Missing;
        internal Guid? FailingThread;
        internal int Discoveries, Submissions;
        public bool NativeIdleGuard => true;
        public DesktopAdapterException? ExactPinRefusal { get; set; }
        public Task<DesktopOwnerBinding?> DiscoverAsync(Guid conversationId, CancellationToken token)
        {
            Discoveries++;
            return conversationId == FailingThread
                ? Task.FromException<DesktopOwnerBinding?>(new IOException("Synthetic later owner lookup failed."))
                : Task.FromResult<DesktopOwnerBinding?>(!Missing && conversationId == Binding.ConversationId ? Binding : null);
        }
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("Diagnostic tests must never submit native input."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Observer(DesktopOwnerBinding binding) : ICodexDirectoryObserver
    {
        internal Guid[] Threads = [binding.ConversationId];
        internal bool Stale;
        public DesktopAdapterException? ExactPinRefusal { get; set; }
        public (uint Pid, ulong CreationFileTime) RouterIdentity { get; set; } = (binding.ServerPid, binding.ServerCreationFileTime);
        public IReadOnlyList<DesktopDirectorySession> Snapshot() => Threads.Select(thread =>
            new DesktopDirectorySession(thread, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow.AddMinutes(Stale ? -5 : 0), null)).ToArray();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CompletionProvider : ICodexNativeProvider
    {
        internal DesktopOwnerBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.PublisherSigned };
        internal Guid[]? Threads;
        internal bool SkipOwner;
        internal int Completions;
        internal CodexDeliveryAuthority Authority = CodexDeliveryAuthority.UpdateTolerant;
        internal IReadOnlyList<DesktopOwnerBinding> Completed = [];
        public bool NativeIdleGuard => true;
        public bool IsDeliveryAuthorized(DesktopOwnerBinding binding) => true;
        public CodexDeliveryAuthority Authorize(DesktopOwnerBinding binding) => Authority;
        public void CompleteDiscovery(IReadOnlyList<DesktopOwnerBinding> carriedBindings) { Completions++; Completed = carriedBindings.ToArray(); }
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DesktopDirectorySession>>((Threads ?? [Binding.ConversationId]).Select(thread =>
                new DesktopDirectorySession(thread, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)).ToArray());
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token) => SkipOwner
            ? Task.FromException<DesktopOwnerBinding?>(new IOException("Synthetic owner lookup skipped."))
            : Task.FromResult<DesktopOwnerBinding?>(Binding);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new InvalidOperationException("Completion tests must never submit native input.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
