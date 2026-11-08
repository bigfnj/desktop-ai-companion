using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// Reporting only. The real adapter/provider compose in-memory owners and directory snapshots;
// no real process, pipe, credential, filesystem discovery or model-input operation is invoked.
internal static class CodexTolerantNoteChecks
{
    private const string L = "CodexTolerantNoteChecks.cs: ";
    private const string NoteCode = "codex_update_tolerant_delivery";
    private const string ExtensionVersion = "26.1021.22027";
    private const string DesktopVersion = "26.1021.3705";
    private const string RouterVersion = "1.144.0";
    private static readonly string[] Cases = ["carried", "refused", "no-carry", "transitions", "observer", "combined", "completion", "image-mismatch", "completion-failure", "router-reporting"];

    internal static async Task RunAsync(Action<bool, string> check, string? only = null)
    {
        Console.WriteLine("WITNESS: CODEX_COMPLETION_EXECUTED");
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
                case "image-mismatch": await ImageMismatchAsync(check); break;
                case "completion-failure": await CompletionFailureAsync(check); break;
                case "router-reporting": await RouterReportingAsync(check); break;
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
            // Unavailable trust is an incomplete broker check, not a signature/publisher verdict. It retains only
            // bounded Receive so the queue can wait; neither outcome permits native input or a carried-build note.
            bool unavailable = code == "public_image_trust_unavailable";
            var expected = unavailable ? SessionCapabilities.Receive : SessionCapabilities.None;
            check(rows.Count == 1 && rows[0].Capabilities == expected && !HasWake(rows[0]) &&
                !rows[0].Capabilities.HasFlag(SessionCapabilities.NativeIdleGuard) && NoCarriedNote(f.Diagnostics) &&
                Error(f.Diagnostics, "running_vscode_image_mismatch") && Error(f.Diagnostics, code),
                L + "failed publisher evidence retains both real errors and never becomes a carried note (" + code + ")");
            int discoveries = f.Owner.Discoveries;
            var recipient = rows[0].Address;
            var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null,
                new(recipient.BrokerId, "synthetic-note-sender", Guid.NewGuid()), recipient, "Synthetic unavailable trust check.",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
            var result = await f.Adapter.SubmitAsync(message, default);
            check(f.Owner.Submissions == 0 && f.Owner.Discoveries == discoveries &&
                (unavailable ? result is { Outcome: AdapterAdmission.DeferredNotSubmitted, Reason: "native_verification_unavailable" }
                    : result.Outcome == AdapterAdmission.Refused),
                L + "failed publisher evidence never wakes, rediscovers or submits native input (" + code + ")");
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
        foreach (string reason in new[] { "capability", "empty", "missing-owner", "stale-row", "invalid-owner", "skipped-owner", "adapter-off", "strict" })
        {
            await using var f = new Fixture(adapterTolerant: reason != "adapter-off", tolerant: reason != "strict");
            f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
            if (reason == "capability") f.Owner.Binding = f.Owner.Binding with { SupportsUntrustedAppInput = false };
            if (reason == "empty") f.Observer.Threads = [];
            if (reason == "missing-owner") f.Owner.Missing = true;
            if (reason == "stale-row") f.Observer.Stale = true;
            if (reason == "invalid-owner") f.Owner.Binding = f.Owner.Binding with { OwnerClientId = Guid.Empty };
            if (reason == "skipped-owner") f.Owner.FailingThread = f.Owner.Binding.ConversationId;
            var rows = await f.DiscoverAsync();
            if (reason is "adapter-off" or "strict")
                check(rows.All(row => !HasWake(row)) && Error(f.Diagnostics, "unsupported_vscode_profile") && NoCarriedNote(f.Diagnostics),
                    L + "effective update-tolerant mode off preserves the exact profile error (" + reason + ")");
            else
                check(rows.All(row => !HasWake(row)) && !Error(f.Diagnostics, "unsupported_vscode_profile") &&
                    f.Diagnostics.Count(item => item.Code == NoteCode && item.Note && item.Description.Contains(ExtensionVersion, StringComparison.Ordinal)) == 1 &&
                    f.ProfileChecks == 1 && f.PublisherChecks == 1,
                    L + "this pass's publisher-verified profile is a note even without an admitted session (" + reason + ")");
        }
        await using (var f = new Fixture(providerDelivery: false))
        {
            f.ProfileFailure = new DesktopAdapterException("unsupported_vscode_profile", ExtensionVersion);
            await f.DiscoverAsync();
            check(f.ProfileChecks == 0 && f.PublisherChecks == 0 && NoCarriedNote(f.Diagnostics),
                L + "provider delivery disabled cannot turn the update-tolerant option into an effective mode note");
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
        var idle = f.Diagnostics;
        check(OnlyNote(idle, ExtensionVersion) && f.ProfileChecks == 2 && f.PublisherChecks == 2 && f.Owner.Discoveries == 1,
            L + "an idle next pass keeps the profile note from its own successful publisher check without looking up an owner");
        f.PublisherFailure = new DesktopAdapterException("public_image_signature_untrusted", ExtensionVersion);
        await f.DiscoverAsync();
        check(Error(f.Diagnostics, "unsupported_vscode_profile") && Error(f.Diagnostics, "public_image_signature_untrusted") &&
            NoCarriedNote(f.Diagnostics) && f.PublisherChecks == 3 && f.Owner.Discoveries == 1,
            L + "a failed publisher check on an idle later pass restores real errors instead of reusing a prior note");
        check(OnlyNote(carried, ExtensionVersion) && OnlyNote(idle, ExtensionVersion),
            L + "previously published admitted and idle diagnostic snapshots remain immutable after a new failure");
        f.Observer.Threads = [f.Owner.Binding.ConversationId];
        f.PublisherFailure = null;
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
        check(first.Count == 1 && HasWake(first[0]) && spy.Completions == 1 && spy.CompletedMode && spy.Authorizations == 2,
            L + "CODEX_NO_UNUSED_AUTHORITY: completion reports mode without another unused per-row authorization");
        spy.SkipOwner = true;
        await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(spy.Completions == 2 && adapter.DiscoveryDiagnostics.Any(item => item.Code == "codex_owner_lookup_skipped"),
            L + "a skipped lookup remains diagnosed when reporting completion");
        spy.SkipOwner = false; spy.Authority = CodexDeliveryAuthority.ExactPinned;
        spy.Binding = spy.Binding with { RouterTrust = DesktopRouterTrust.ExactPinned };
        var exact = await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(exact.Count == 1 && HasWake(exact[0]) && spy.Completions == 3,
            L + "a guarded exact-pin session remains deliverable through reporting completion");
        spy.Authority = CodexDeliveryAuthority.UpdateTolerant;
        spy.Binding = spy.Binding with { RouterTrust = DesktopRouterTrust.PublisherSigned, SupportsUntrustedAppInput = false };
        await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(spy.Completions == 4, L + "an unsupported native capability still reaches reporting completion");
        spy.Threads = [];
        await adapter.DiscoverAsync(Guid.NewGuid(), default);
        check(spy.Completions == 5,
            L + "the real adapter finalizes even an empty completed pass");

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

    private static async Task ImageMismatchAsync(Action<bool, string> check)
    {
        string extension = VsCodeCompatibilityProfile.VerifiedVersions.Split(", ", StringSplitOptions.RemoveEmptyEntries)[0];
        await using (var f = new Fixture())
        {
            f.ProfileFailure = new DesktopAdapterException("vscode_image_mismatch", extension);
            await f.DiscoverAsync();
            check(OnlyNote(f.Diagnostics, extension) && f.Diagnostics[0].Description.Contains("image differs from reviewed build", StringComparison.Ordinal) &&
                f.Diagnostics[0].Description.StartsWith("Publisher-verified builds:", StringComparison.Ordinal),
                L + "a publisher-verified extension with a reviewed version still says its image differs from the reviewed build");
        }
        string desktop = DesktopCompatibilityProfile.VerifiedVersions.Split(", ", StringSplitOptions.RemoveEmptyEntries)[0];
        await using (var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned))
        {
            f.Owner.ExactPinRefusal = new("desktop_image_mismatch", desktop);
            f.Observer.ExactPinRefusal = f.Owner.ExactPinRefusal;
            await f.DiscoverAsync();
            check(OnlyNote(f.Diagnostics, desktop) && f.Diagnostics[0].Description.Contains("image differs from reviewed build", StringComparison.Ordinal) &&
                f.Diagnostics[0].Description.Contains("Update-tolerant delivery is enabled.", StringComparison.Ordinal),
                L + "a publisher-verified desktop with a reviewed version keeps the image-mismatch meaning in its note");
        }
    }

    private static async Task CompletionFailureAsync(Action<bool, string> check)
    {
        var reporter = new CompletionProvider { FailCompletion = true };
        await using var adapter = new CodexNativeAdapter(reporter, enableDelivery: true, updateTolerantDelivery: true);
        Guid broker = Guid.NewGuid();
        bool failed = false;
        try { await adapter.DiscoverAsync(broker, default); }
        catch (InvalidOperationException error) when (error.Message == "Synthetic report failure.") { failed = true; }
        check(failed && reporter.Completions == 1,
            L + "WITNESS: report-only completion can fail after the real adapter admitted its synthetic owner");
        var address = new SessionAddress(broker, "codex:" + reporter.Binding.ConversationId.ToString("D"), reporter.Binding.NativeSessionIncarnation);
        var message = new BrokerMessage(Guid.NewGuid(), Guid.NewGuid(), null, new(broker, "synthetic-report-sender", Guid.NewGuid()),
            address, "Synthetic report-failure check, never sent to a real client.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        var result = await adapter.SubmitAsync(message, default);
        check(result.Outcome == AdapterAdmission.Submitted && reporter.Submissions == 1,
            L + "report completion failure does not discard an admitted binding before a synthetic submission can use it");
    }

    private static async Task RouterReportingAsync(Action<bool, string> check)
    {
        var pin = new DesktopAdapterException("unsupported_desktop_version", DesktopVersion);
        await using (var f = new Fixture())
        {
            f.Observer.Threads = [];
            f.Observer.ExactPinRefusal = pin;
            f.Observer.ReportingProof = true;
            var idle = await f.DiscoverAsync();
            var original = f.Diagnostics;
            check(idle.Count == 0 && OnlyNote(original, DesktopVersion) && f.Owner.Discoveries == 0 && f.Observer.ReportingReads > 0,
                L + "WITNESS: an idle observer with explicit publisher reporting proof replaces its own exact error with a versioned note");
            await f.DiscoverAsync();
            check(OnlyNote(f.Diagnostics, DesktopVersion) && f.DirectoryConnections == 1 && f.Observer.Snapshots == 2 && f.Owner.Discoveries == 0,
                L + "a successful idle snapshot reuses connection-bound reporting proof without reconnecting or resolving an owner");

            Observer signed = f.Observer;
            signed.SnapshotFailure = new DesktopAdapterException("directory_observer_disconnected");
            AdapterDiscoveryException? failed = null;
            try { await f.DiscoverAsync(); }
            catch (AdapterDiscoveryException error) { failed = error; }
            check(failed?.Diagnostic.Code == "directory_observer_disconnected" && signed.Disposals == 1 &&
                !signed.PublisherVerifiedForReporting && NoCarriedNote(f.Provider.DiscoveryDiagnostics),
                L + "a failed snapshot retires its reporting proof and removes the previously published router note");

            f.Observer = new(f.Owner.Binding) { Threads = [], ExactPinRefusal = pin, ReportingProof = false };
            await f.DiscoverAsync();
            check(f.DirectoryConnections == 2 && Error(f.Diagnostics, pin.Code) && NoCarriedNote(f.Diagnostics) && f.Owner.Discoveries == 0,
                L + "a replacement observer without explicit publisher proof keeps its exact error despite the prior signed connection");
            Observer unverified = f.Observer;
            unverified.SnapshotFailure = new DesktopAdapterException("directory_observer_disconnected");
            bool replacementFailed = false;
            try { await f.DiscoverAsync(); }
            catch (AdapterDiscoveryException error) when (error.Diagnostic.Code == "directory_observer_disconnected") { replacementFailed = true; }
            check(replacementFailed && unverified.Disposals == 1,
                L + "WITNESS: the unverified replacement is retired before testing a newly signed connection");
            f.Observer = new(f.Owner.Binding) { Threads = [], ExactPinRefusal = pin, ReportingProof = true };
            await f.DiscoverAsync();
            check(f.DirectoryConnections == 3 && OnlyNote(f.Diagnostics, DesktopVersion) && f.Owner.Discoveries == 0,
                L + "a new explicitly signed observer restores its own idle note after failed and unverified connections");
            check(OnlyNote(original, DesktopVersion),
                L + "the earlier signed observer's published diagnostic snapshot remains immutable across reconnections");
        }
        await using (var f = new Fixture())
        {
            f.Observer.Threads = [];
            f.Observer.ExactPinRefusal = new("unsupported_vscode_router", RouterVersion);
            f.Observer.RouterIdentity = (f.Owner.Binding.ServerPid + 1, f.Owner.Binding.ServerCreationFileTime);
            await f.DiscoverAsync();
            check(Error(f.Diagnostics, "unsupported_vscode_router") && NoCarriedNote(f.Diagnostics),
                L + "an idle unrelated observer's refusal alone never becomes publisher reporting proof");
        }
        await using (var f = new Fixture(routerTrust: DesktopRouterTrust.PublisherSigned))
        {
            f.Owner.ReportingProof = false;
            f.Owner.ExactPinRefusal = pin;
            f.Observer.ExactPinRefusal = pin;
            var rows = await f.DiscoverAsync();
            check(rows.Count == 1 && HasWake(rows[0]) && Error(f.Diagnostics, pin.Code) && NoCarriedNote(f.Diagnostics),
                L + "a successful owner lookup without explicit reporting proof cannot promote a matching router refusal");
        }
        await using (var f = new Fixture())
        {
            f.Observer.Threads = [];
            f.Observer.ExactPinRefusal = pin;
            f.Observer.ReportingProof = true;
            await f.Provider.ObserveAsync(default);
            f.Observer.ReportingProof = false;
            f.Provider.CompleteDiscovery(true);
            check(Error(f.Provider.DiscoveryDiagnostics, pin.Code) && NoCarriedNote(f.Provider.DiscoveryDiagnostics),
                L + "an observer that loses live reporting proof after observation keeps its exact error at completion");
        }
        await using (var f = new Fixture())
        {
            f.Observer.Threads = [];
            f.Observer.ExactPinRefusal = pin;
            f.Observer.ReportingProof = true;
            await f.Provider.ObserveAsync(default);
            f.Observer.RouterIdentity = (f.Observer.RouterIdentity.Pid + 1, f.Observer.RouterIdentity.CreationFileTime);
            f.Provider.CompleteDiscovery(true);
            check(Error(f.Provider.DiscoveryDiagnostics, pin.Code) && NoCarriedNote(f.Provider.DiscoveryDiagnostics),
                L + "an observer whose live identity changes after observation cannot reuse the captured publisher verdict");
        }
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
        internal Observer Observer;
        internal readonly CodexNativeProvider Provider;
        internal readonly CodexNativeAdapter Adapter;
        internal Exception? ProfileFailure, PublisherFailure;
        internal int ProfileChecks, PublisherChecks, DirectoryConnections;
        internal IReadOnlyList<AdapterDiscoveryDiagnostic> Diagnostics => Adapter.DiscoveryDiagnostics;
        internal Fixture(DesktopRouterTrust routerTrust = DesktopRouterTrust.ExactPinned, bool tolerant = true, bool adapterTolerant = true, bool providerDelivery = true)
        {
            Owner = new(routerTrust);
            Observer = new(Owner.Binding);
            Provider = new(providerDelivery, _ => Task.FromResult<ICodexOwnerConnection>(Owner),
                () => { ProfileChecks++; if (ProfileFailure is { } failure) throw failure; }, null,
                _ => { DirectoryConnections++; return Task.FromResult<ICodexDirectoryObserver>(Observer); }, tolerant,
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
        internal bool? ReportingProof;
        internal int Discoveries, Submissions;
        public bool NativeIdleGuard => true;
        public DesktopAdapterException? ExactPinRefusal { get; set; }
        public bool PublisherVerifiedForReporting => ReportingProof ?? trust == DesktopRouterTrust.PublisherSigned;
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
        internal bool ReportingProof, SnapshotPassed;
        internal int ReportingReads, Snapshots, Disposals;
        internal Exception? SnapshotFailure;
        public DesktopAdapterException? ExactPinRefusal { get; set; }
        public (uint Pid, ulong CreationFileTime) RouterIdentity { get; set; } = (binding.ServerPid, binding.ServerCreationFileTime);
        public bool PublisherVerifiedForReporting { get { ReportingReads++; return ReportingProof && SnapshotPassed && Disposals == 0; } }
        public IReadOnlyList<DesktopDirectorySession> Snapshot()
        {
            Snapshots++; SnapshotPassed = false;
            if (SnapshotFailure is { } failure) throw failure;
            SnapshotPassed = true;
            return Threads.Select(thread => new DesktopDirectorySession(thread, DesktopSessionActivity.Unknown,
                DateTimeOffset.UtcNow.AddMinutes(Stale ? -5 : 0), null)).ToArray();
        }
        public ValueTask DisposeAsync() { Disposals++; SnapshotPassed = false; return ValueTask.CompletedTask; }
    }
    private sealed class CompletionProvider : ICodexNativeProvider
    {
        internal DesktopOwnerBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12345, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.PublisherSigned };
        internal Guid[]? Threads;
        internal bool SkipOwner, FailCompletion, CompletedMode;
        internal int Completions, Submissions, Authorizations;
        internal CodexDeliveryAuthority Authority = CodexDeliveryAuthority.UpdateTolerant;
        public bool NativeIdleGuard => true;
        public bool IsDeliveryAuthorized(DesktopOwnerBinding binding) => true;
        public CodexDeliveryAuthority Authorize(DesktopOwnerBinding binding) { Authorizations++; return Authority; }
        public void CompleteDiscovery(bool updateTolerantDelivery)
        {
            Completions++; CompletedMode = updateTolerantDelivery;
            if (FailCompletion) throw new InvalidOperationException("Synthetic report failure.");
        }
        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DesktopDirectorySession>>((Threads ?? [Binding.ConversationId]).Select(thread =>
                new DesktopDirectorySession(thread, DesktopSessionActivity.Unknown, DateTimeOffset.UtcNow, null)).ToArray());
        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token) => SkipOwner
            ? Task.FromException<DesktopOwnerBinding?>(new IOException("Synthetic owner lookup skipped."))
            : Task.FromResult<DesktopOwnerBinding?>(Binding);
        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
        { Submissions++; return Task.FromResult(new DesktopSubmissionResult(DesktopSubmissionOutcome.Accepted, "synthetic-turn")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
