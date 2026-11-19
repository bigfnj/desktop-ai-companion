using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentBroker.ClaudeAdapter;
using AgentBroker.CodexDesktopAdapter;
using AgentBroker.Core;
using AgentBroker.Worker;
using AgentBroker.Worker.Adapters;

// N-broker-native-lease-gap-01, first step (AgentBroker 1.10.6, lane N round 2): the report-only discovery trace. Labels 1 to 4
// check BrokerStore.AppendEvent and the sink's refusals, 5 and 6 the adapters' LastDiscoveryTrace, and 7 to 18 the discovery_pass
// and session_forgotten lines the runtime's discovery loop writes. This file stops at its first failure: one check per label, in
// this order, so each killer reports at its own label (the killer list and its first-failure arguments live outside the repo).
// Labels 7 to 11 read and count only discovery_pass lines, and labels 12 to 16 only session_forgotten lines for their own
// scenario's address, so the lapse line of a dropped row never lands inside another label's count. Waits return false and never
// throw, every evaluation turns an unexpected shape into a failed check, and every runtime is disposed (a loop fault swallowed,
// as in AdapterFailureRecordedAsync) before the checks that follow it, so a killer cannot end the run without its label. Runtime
// timing is scaled as there: pass deadline 300 ms, interval 100 ms. TraceAdapter leases are 600 ms; its Id is codex-native and its
// native rows are codex:<GUID> (the native block and ObserveNativeSession require both), and its mcp rows have Adapter "mcp".
internal static partial class RuntimeLifecycleChecks
{
    private const string TraceStoreEventAppended = "STORE_EVENT_APPENDED: AppendEvent writes one line, the caller keys first and eventAt from the store clock last";
    private const string TraceStoreKindRequired = "STORE_EVENT_KIND_REQUIRED: a line without a valid event name is refused and counted into the next line as eventsLost 1";
    private const string TraceStoreSizeBounded = "STORE_EVENT_SIZE_BOUNDED: a line over MaximumLineBytes once encoded is refused and counted into the next line as eventsLost 1";
    private const string TraceStoreAfterDispose = "STORE_EVENT_AFTER_DISPOSE_DROPPED: after Dispose AppendEvent neither throws nor writes";
    private const string TraceCodexOutcomes = "CODEX_TRACE_OUTCOMES: the last completed Codex pass names each conversation outcome with the incarnation it renewed or kept, and a failed pass keeps that snapshot";
    private const string TraceClaudeCensus = "CLAUDE_TRACE_CENSUS: a Claude session renewed last pass and not this pass reads absent once, then leaves the census";
    private const string TracePassWritten = "DISCOVERY_PASS_WRITTEN: the first pass writes one discovery_pass line naming each row session, incarnation and outcome";
    private const string TracePassNoChurn = "DISCOVERY_PASS_NO_CHURN: identical passes write no further discovery_pass line";
    private const string TracePassNoLabel = "DISCOVERY_PASS_NO_LABEL: discovery_pass lines hold only the schema keys and no row label is anywhere in the events file";
    private const string TracePassDropped = "DISCOVERY_PASS_DROPPED: session IDs the last written completed pass reported and this pass no longer reports are named in dropped on the next discovery_pass line (a Claude closure reads absent in changed first, then dropped)";
    private const string TracePassDiagnosticsChange = "DISCOVERY_PASS_DIAGNOSTICS_CHANGE: a pass whose only change is one diagnostic code writes exactly one discovery_pass line, codes only";
    private const string TraceForgottenOnLapse = "SESSION_FORGOTTEN_ON_LAPSE: a native lease passing without renewal writes session_forgotten with the exact address, native_lease_elapsed and its last leaseUntil";
    private const string TraceForgottenOnce = "SESSION_FORGOTTEN_ONCE: later passes write no second session_forgotten line for the same lapse";
    private const string TraceForgottenInsidePass = "SESSION_FORGOTTEN_LAPSE_INSIDE_PASS: a native lease that passes during a slow pass and is renewed in that same pass still writes its line";
    private const string TraceForgottenNativeOnly = "SESSION_FORGOTTEN_NATIVE_ONLY: a non-native row returned by the same adapter never writes session_forgotten";
    private const string TraceForgottenNotOnReplacement = "SESSION_FORGOTTEN_NOT_ON_REPLACEMENT: a still-live native incarnation replaced by a new one writes no session_forgotten";
    private const string TracePassCodexOutcomes = "DISCOVERY_PASS_CODEX_OUTCOMES: the runtime writes the Codex adapter outcome for each conversation, not only the rows the pass returned";
    private const string TracePassFailed = "DISCOVERY_PASS_FAILED: a failed pass writes one discovery_pass line with its failure code and no rows, once while it repeats, and recovery writes one";

    // Synthetic texts that must never reach the events file: row labels, a diagnostic description, a failure description.
    private const string TraceNativeLabel = "Synthetic trace label 9b41";
    private const string TraceMailboxLabel = "Synthetic trace label 2c7f";
    private const string TraceDescription = "Synthetic trace description 5e1d";
    private const string TraceFailureDescription = "Synthetic failure description 7c3a";

    private static readonly string[] TracePassKeys = ["event", "adapter", "pass", "failure", "censusComplete", "diagnostics", "changed", "dropped", "eventAt"];
    private static readonly string[] TraceRowKeys = ["sessionId", "incarnation", "outcome"];
    private static readonly string[] TraceForgottenKeys = ["event", "adapter", "address", "cause", "leaseUntil", "eventAt"];

    internal static async Task DiscoveryTraceAsync(Action<bool, string> check)
    {
        using var scratchOwner = OwnedScratch.Create("discovery-trace");
        string scratch = scratchOwner.Root;
        TraceStoreEventChecks(check, scratch);
        await TraceCodexAdapterAsync(check);
        await TraceClaudeAdapterAsync(check);
        string alpha = Path.Combine(scratch, "alpha");
        var alphaMailbox = await TraceAlphaAsync(check, alpha);
        await TraceLapseInsidePassAsync(check, Path.Combine(scratch, "inside"));
        // Scenario alpha's mcp row was dropped with its native row, so its lease passed with that one (label 12 saw the native line),
        // and at least three more passes ran before alpha's runtime was disposed.
        check(TraceRead(() => TraceForgotten(alpha, alphaMailbox).Count == 0), TraceForgottenNativeOnly);
        await TraceReplacementAsync(check, Path.Combine(scratch, "replacement"));
        await TraceCodexRuntimeAsync(check, Path.Combine(scratch, "codex"));
        await TraceFailedPassAsync(check, scratch);
    }

    // Labels 1 to 4: the store's one entry point and the sink's refusals, each in its own store directory. A refused line is
    // followed by a valid one, which must carry the actual loss count (eventsLost 1), so a refusal is never silent.
    private static void TraceStoreEventChecks(Action<bool, string> check, string scratch)
    {
        var clock = new TraceFixedClock(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        string appended = Path.Combine(scratch, "store-appended");
        bool stamped;
        using (var store = new BrokerStore(appended, clock: clock))
        {
            store.AppendEvent(new JsonObject { ["event"] = "trace_check", ["value"] = "synthetic" });
            stamped = TraceRead(() => EventsFile.Read(appended) is [var line] &&
                line.Select(pair => pair.Key).SequenceEqual(new[] { "event", "value", "eventAt" }) &&
                TraceText(line, "event") == "trace_check" && TraceText(line, "value") == "synthetic" &&
                TraceText(line, "eventAt") == clock.GetUtcNow().ToString("O"));
        }
        check(stamped, TraceStoreEventAppended);

        string kinds = Path.Combine(scratch, "store-kinds");
        bool refusedKinds;
        using (var store = new BrokerStore(kinds))
        {
            JsonObject[] invalid =
            [
                new JsonObject { ["value"] = "no event" },
                new JsonObject { ["event"] = "Trace_check" },
                new JsonObject { ["event"] = "1trace" },
                new JsonObject { ["event"] = "trace-check" },
                new JsonObject { ["event"] = "" },
                new JsonObject { ["event"] = new string('a', 49) },
                new JsonObject { ["event"] = 7 },
            ];
            foreach (var line in invalid)
            {
                store.AppendEvent(line);
                store.AppendEvent(new JsonObject { ["event"] = "trace_check" });
            }
            // The longest valid name is written, and with no loss before it, carries no eventsLost.
            store.AppendEvent(new JsonObject { ["event"] = new string('a', 48) });
            refusedKinds = TraceRead(() => EventsFile.Read(kinds) is var lines && lines.Count == invalid.Length + 1 &&
                lines.Take(invalid.Length).All(line => TraceText(line, "event") == "trace_check" && TraceLong(line, "eventsLost") == 1) &&
                TraceText(lines[^1], "event") == new string('a', 48) && !lines[^1].ContainsKey("eventsLost"));
        }
        check(refusedKinds, TraceStoreKindRequired);

        string sizes = Path.Combine(scratch, "store-sizes");
        bool refusedSizes;
        using (var store = new BrokerStore(sizes))
        {
            store.AppendEvent(new JsonObject { ["event"] = "trace_check", ["value"] = new string('a', BrokerEventLog.MaximumLineBytes) });
            store.AppendEvent(new JsonObject { ["event"] = "trace_after_plain" });
            // Escaping counts: '<' is encoded as six bytes, so a sixth of the bound in characters is over it once encoded.
            store.AppendEvent(new JsonObject { ["event"] = "trace_check", ["value"] = new string('<', BrokerEventLog.MaximumLineBytes / 6 + 1) });
            store.AppendEvent(new JsonObject { ["event"] = "trace_after_escaped" });
            // Just under the bound with its envelope and eventAt: written.
            store.AppendEvent(new JsonObject { ["event"] = "trace_near_bound", ["value"] = new string('a', BrokerEventLog.MaximumLineBytes - 200) });
            refusedSizes = TraceRead(() => EventsFile.Read(sizes) is [var plain, var escaped, var near] &&
                TraceText(plain, "event") == "trace_after_plain" && TraceLong(plain, "eventsLost") == 1 &&
                TraceText(escaped, "event") == "trace_after_escaped" && TraceLong(escaped, "eventsLost") == 1 &&
                TraceText(near, "event") == "trace_near_bound" && !near.ContainsKey("eventsLost"));
        }
        check(refusedSizes, TraceStoreSizeBounded);

        string disposedDirectory = Path.Combine(scratch, "store-disposed");
        bool threw = false;
        using (var store = new BrokerStore(disposedDirectory))
        {
            store.AppendEvent(new JsonObject { ["event"] = "trace_check" });
            store.Dispose();
            try { store.AppendEvent(new JsonObject { ["event"] = "trace_after_dispose" }); }
            catch (Exception) { threw = true; }
        }
        check(!threw && TraceRead(() => EventsFile.Read(disposedDirectory) is [var line] && TraceText(line, "event") == "trace_check"),
            TraceStoreAfterDispose);
    }

    // Label 5: the real Codex adapter over a scripted provider, called directly. Each conversation's branch, with the incarnation
    // it renewed or kept; a pass that fails whole keeps the previous snapshot, the same object.
    private static async Task TraceCodexAdapterAsync(Action<bool, string> check)
    {
        var provider = new TraceRowProvider();
        await using var adapter = new CodexNativeAdapter(provider);
        var brokerId = Guid.NewGuid();
        Guid renew = Guid.NewGuid(), missing = Guid.NewGuid(), invalid = Guid.NewGuid(), stale = Guid.NewGuid(), failed = Guid.NewGuid(),
            rejected = Guid.NewGuid(), outlasted = Guid.NewGuid(), slow = Guid.NewGuid(), unreached = Guid.NewGuid();
        static string CodexId(Guid thread) => "codex:" + thread.ToString("D");
        static Guid Renewed(Guid thread) => TraceRowProvider.Binding(thread).NativeSessionIncarnation;
        provider.Threads = [renew, missing, invalid, Guid.Empty, stale, failed, rejected, outlasted, slow, unreached];
        provider.StaleThread = stale;
        provider.Lookups[missing] = TraceLookup.Missing;
        provider.Lookups[invalid] = TraceLookup.InvalidBinding;
        provider.Lookups[failed] = TraceLookup.Fail;
        provider.Lookups[rejected] = TraceLookup.Reject;
        provider.Lookups[outlasted] = TraceLookup.ReconnectOutlasted;
        provider.Lookups[slow] = TraceLookup.Slow;
        bool before = adapter.LastDiscoveryTrace is null;
        // Pass 1, in directory order: the slow lookup cancels the pass, so the conversation after it is not reached. The Guid.Empty
        // sentinel is counted, never traced.
        bool first = await TraceCodexPassAsync(adapter, provider, brokerId) is null && TraceSame(adapter.LastDiscoveryTrace,
            (CodexId(renew), Renewed(renew), "renewed"), (CodexId(missing), null, "missing"), (CodexId(invalid), null, "invalid"),
            (CodexId(stale), null, "stale"), (CodexId(failed), null, "skipped_failed"), (CodexId(rejected), null, "skipped_failed"),
            (CodexId(outlasted), null, "skipped_unreached"), (CodexId(slow), null, "skipped_slow"), (CodexId(unreached), null, "skipped_unreached"));
        // Pass 2: the two unreached conversations go first and renew; the renewed one is now slow and keeps its binding; every
        // conversation after it is not reached, the failed and slow ones last.
        provider.Lookups[renew] = TraceLookup.Slow;
        provider.Lookups[outlasted] = TraceLookup.Renew;
        bool second = await TraceCodexPassAsync(adapter, provider, brokerId) is null && TraceSame(adapter.LastDiscoveryTrace,
            (CodexId(outlasted), Renewed(outlasted), "renewed"), (CodexId(unreached), Renewed(unreached), "renewed"),
            (CodexId(renew), Renewed(renew), "skipped_slow"), (CodexId(missing), null, "skipped_unreached"),
            (CodexId(invalid), null, "skipped_unreached"), (CodexId(stale), null, "stale"), (CodexId(failed), null, "skipped_unreached"),
            (CodexId(rejected), null, "skipped_unreached"), (CodexId(slow), null, "skipped_unreached"));
        // Pass 3 fails whole (the directory observation itself): the trace is still the pass 2 snapshot.
        var snapshot = adapter.LastDiscoveryTrace;
        provider.ObserveFailure = "synthetic_directory_failure";
        bool kept = await TraceCodexPassAsync(adapter, provider, brokerId) is AdapterDiscoveryException && snapshot is not null &&
            ReferenceEquals(adapter.LastDiscoveryTrace, snapshot);
        check(before && first && second && kept, TraceCodexOutcomes);
    }

    private static async Task<Exception?> TraceCodexPassAsync(CodexNativeAdapter adapter, TraceRowProvider provider, Guid brokerId)
    {
        using var pass = new CancellationTokenSource();
        provider.Pass = pass;
        try { await adapter.DiscoverAsync(brokerId, pass.Token); return null; }
        catch (Exception error) { return error; }
        finally { provider.Pass = null; }
    }

    // Label 6: the real Claude adapter over a scripted census, called directly.
    private static async Task TraceClaudeAdapterAsync(Action<bool, string> check)
    {
        var provider = new TraceCensusProvider();
        await using var adapter = new ClaudeNativeAdapter(provider);
        var brokerId = Guid.NewGuid();
        var kept = TraceClaudeCandidate(12345);
        var closed = TraceClaudeCandidate(12346);
        static string ClaudeId(ClaudeNativeCandidate candidate) => "claude:" + candidate.Target.SessionId;
        bool before = adapter.LastDiscoveryTrace is null;
        provider.Candidates = [kept, closed];
        bool both = await TraceClaudePassAsync(adapter, brokerId) && TraceSame(adapter.LastDiscoveryTrace,
            (ClaudeId(kept), kept.NativeSessionIncarnation, "renewed"), (ClaudeId(closed), closed.NativeSessionIncarnation, "renewed"));
        provider.Candidates = [kept];
        bool absent = await TraceClaudePassAsync(adapter, brokerId) && TraceSame(adapter.LastDiscoveryTrace,
            (ClaudeId(kept), kept.NativeSessionIncarnation, "renewed"), (ClaudeId(closed), closed.NativeSessionIncarnation, "absent"));
        bool left = await TraceClaudePassAsync(adapter, brokerId) && TraceSame(adapter.LastDiscoveryTrace,
            (ClaudeId(kept), kept.NativeSessionIncarnation, "renewed"));
        check(before && both && absent && left, TraceClaudeCensus);
    }

    private static async Task<bool> TraceClaudePassAsync(ClaudeNativeAdapter adapter, Guid brokerId)
    {
        try { await adapter.DiscoverAsync(brokerId, default); return true; }
        catch (Exception) { return false; }
    }

    private static ClaudeNativeCandidate TraceClaudeCandidate(int processId)
    {
        var pin = EngineProfile.Verified.First();
        return new(new NativeTarget(@"\\.\pipe\cc-msg-0123456789abcdef0123456789abcdef", processId,
            @"C:\SyntheticPublic\claude.exe", pin.Sha256, pin.Version, 134000000000000000, Guid.NewGuid().ToString("D")), "idle");
    }

    // Labels 7 to 13, scenario alpha: one native row and one mcp row from the same codex-native adapter, through the runtime.
    // Returns the mcp row's address for label 15, which reads this scenario's file after label 14.
    private static async Task<SessionAddress> TraceAlphaAsync(Action<bool, string> check, string directory)
    {
        using var store = new BrokerStore(directory);
        await using var federation = new SlowDirectoryFederation(Guid.Empty);
        var adapter = new TraceAdapter("codex-native");
        var native = new SessionAddress(store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        var mailbox = new SessionAddress(store.BrokerId, "mcp:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
        adapter.SetRows(TraceRow(native, "codex-native", TraceNativeLabel), TraceRow(mailbox, "mcp", TraceMailboxLabel));
        var runtime = TraceRuntime(store, federation, adapter);
        runtime.Start(default);
        try
        {
            // 7: the first line's values only (not its key set, not dropped).
            bool written = await TraceUntilAsync(() => TraceLines(directory, "discovery_pass").Count >= 1);
            int passes = Volatile.Read(ref adapter.Passes);
            check(written && TraceRead(() => TraceLines(directory, "discovery_pass") is [var firstLine, ..] &&
                TraceText(firstLine, "adapter") == "codex-native" && TraceText(firstLine, "pass") == "completed" &&
                firstLine.TryGetPropertyValue("failure", out var firstFailure) && firstFailure is null &&
                TraceBool(firstLine, "censusComplete") == true && TraceStrings(firstLine["diagnostics"]) is [] &&
                TraceChanged(firstLine, (native.SessionId, native.Incarnation, "renewed"), (mailbox.SessionId, mailbox.Incarnation, "renewed"))),
                TracePassWritten);

            // 8: at least three more complete passes (the fourth after the observation started), still one line.
            bool settled = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= passes + 4);
            check(settled && TraceRead(() => TraceLines(directory, "discovery_pass").Count == 1), TracePassNoChurn);

            // 9: schema keys only, on every discovery_pass line and every changed entry, and neither label anywhere in the file.
            check(TraceRead(() => TraceLines(directory, "discovery_pass") is { Count: > 0 } schemaLines && schemaLines.All(TraceSchemaOnly) &&
                !TraceAll(directory).Contains(TraceNativeLabel, StringComparison.Ordinal) &&
                !TraceAll(directory).Contains(TraceMailboxLabel, StringComparison.Ordinal)), TracePassNoLabel);

            // 10: both rows stop being returned.
            adapter.SetRows();
            bool droppedWritten = await TraceUntilAsync(() => TraceLines(directory, "discovery_pass").Count >= 2);
            var expectedDropped = new[] { native.SessionId, mailbox.SessionId }.Order(StringComparer.Ordinal).ToArray();
            check(droppedWritten && TraceRead(() => TraceLines(directory, "discovery_pass") is [_, var droppedLine, ..] &&
                TraceText(droppedLine, "pass") == "completed" && droppedLine["changed"] is JsonArray { Count: 0 } &&
                TraceStrings(droppedLine["dropped"]) is { } droppedIds && droppedIds.SequenceEqual(expectedDropped)), TracePassDropped);

            // 11: nothing else changes; one diagnostic code appears.
            int linesBefore = TraceCount(directory, "discovery_pass");
            adapter.SetDiagnostics(new AdapterDiscoveryDiagnostic("synthetic_trace_code", TraceDescription));
            passes = Volatile.Read(ref adapter.Passes);
            bool diagnosed = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= passes + 4);
            check(diagnosed && TraceRead(() => TraceLines(directory, "discovery_pass") is var codeLines && codeLines.Count == linesBefore + 1 &&
                TraceText(codeLines[^1], "pass") == "completed" && TraceStrings(codeLines[^1]["diagnostics"]) is ["synthetic_trace_code"] &&
                codeLines[^1]["changed"] is JsonArray { Count: 0 } && codeLines[^1]["dropped"] is JsonArray { Count: 0 } &&
                !TraceAll(directory).Contains(TraceDescription, StringComparison.Ordinal)), TracePassDiagnosticsChange);

            // 12: the native row's lease passes with no renewal.
            bool lapsed = await TraceUntilAsync(() => TraceForgotten(directory, native).Count >= 1);
            passes = Volatile.Read(ref adapter.Passes);
            check(lapsed && adapter.LastLease.TryGetValue(native, out var lastLease) && TraceRead(() =>
                TraceForgotten(directory, native) is [var lapseLine, ..] &&
                lapseLine.Select(pair => pair.Key).Where(key => key != "eventsLost").SequenceEqual(TraceForgottenKeys) &&
                TraceText(lapseLine, "adapter") == "codex-native" && TraceText(lapseLine, "address") == TraceAddress(native) &&
                TraceText(lapseLine, "cause") == "native_lease_elapsed" && TraceText(lapseLine, "leaseUntil") == lastLease.ToString("O")),
                TraceForgottenOnLapse);

            // 13: at least three more complete passes, still one line for that lapse.
            bool later = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= passes + 4);
            check(later && TraceRead(() => TraceForgotten(directory, native).Count == 1), TraceForgottenOnce);
        }
        finally { await TraceDisposeAsync(runtime); }
        return mailbox;
    }

    // Label 14: one pass outlasts the lease it renews (it sleeps past the 600 ms lease before it returns the row with a fresh one),
    // so the lapse falls inside that pass and only RenewNativeLease's pre-overwrite check can see it.
    private static async Task TraceLapseInsidePassAsync(Action<bool, string> check, string directory)
    {
        bool ready, delayed;
        DateTimeOffset? leaseBefore;
        SessionAddress native;
        using (var store = new BrokerStore(directory))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            var adapter = new TraceAdapter("codex-native");
            native = new SessionAddress(store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            adapter.SetRows(TraceRow(native, "codex-native", TraceNativeLabel));
            var runtime = TraceRuntime(store, federation, adapter);
            runtime.Start(default);
            try
            {
                // Two passes complete (the third started): the address is registered and renewed.
                ready = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= 3);
                adapter.DelayNextPass(TimeSpan.FromMilliseconds(900));
                delayed = await TraceUntilAsync(() => Volatile.Read(ref adapter.DelayedPassesCompleted) >= 1);
                int passes = Volatile.Read(ref adapter.Passes);
                delayed = delayed && await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= passes + 4);
                leaseBefore = adapter.LeaseBeforeDelay;
            }
            finally { await TraceDisposeAsync(runtime); }
        }
        check(ready && delayed && leaseBefore is { } lapsedLease && TraceRead(() => TraceForgotten(directory, native) is [var insideLine] &&
            TraceText(insideLine, "cause") == "native_lease_elapsed" && TraceText(insideLine, "leaseUntil") == lapsedLease.ToString("O")),
            TraceForgottenInsidePass);
    }

    // Label 16: the same session ID comes back with a new incarnation while the first one's lease is still live, so the store
    // forgets the first as registration_replaced. The wait covers that lease and at least three more passes (at least 1.5 s).
    private static async Task TraceReplacementAsync(Action<bool, string> check, string directory)
    {
        bool ready, replaced;
        SessionAddress original, replacement;
        using (var store = new BrokerStore(directory))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            var adapter = new TraceAdapter("codex-native");
            original = new SessionAddress(store.BrokerId, "codex:" + Guid.NewGuid().ToString("D"), Guid.NewGuid());
            replacement = original with { Incarnation = Guid.NewGuid() };
            adapter.SetRows(TraceRow(original, "codex-native", TraceNativeLabel));
            var runtime = TraceRuntime(store, federation, adapter);
            runtime.Start(default);
            try
            {
                ready = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= 3);
                adapter.SetRows(TraceRow(replacement, "codex-native", TraceNativeLabel));
                var watch = Stopwatch.StartNew();
                int passes = Volatile.Read(ref adapter.Passes);
                replaced = await TraceUntilAsync(() => adapter.LastLease.ContainsKey(replacement) &&
                    Volatile.Read(ref adapter.Passes) >= passes + 4 && watch.Elapsed >= TimeSpan.FromMilliseconds(1500), 10);
            }
            finally { await TraceDisposeAsync(runtime); }
        }
        check(ready && replaced && TraceRead(() => TraceForgotten(directory, original).Count == 0 && TraceForgotten(directory, replacement).Count == 0),
            TraceForgottenNotOnReplacement);
    }

    // Label 17: the real Codex adapter through the runtime (SlowRowProvider, the middle conversation slow): the line carries the
    // adapter's own outcomes, including the two conversations the pass returned no row for.
    private static async Task TraceCodexRuntimeAsync(Action<bool, string> check, string directory)
    {
        bool written;
        Guid? renewed;
        Guid[] threads;
        using (var store = new BrokerStore(directory))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            var provider = new SlowRowProvider(3);
            provider.Slow = provider.Threads[1];
            threads = provider.Threads;
            var runtime = TraceRuntime(store, federation, new CodexNativeAdapter(provider));
            runtime.Start(default);
            try
            {
                written = await TraceUntilAsync(() => TraceLines(directory, "discovery_pass").Count >= 1);
                string renewedId = "codex:" + threads[0].ToString("D");
                renewed = store.Directory().FirstOrDefault(session => session.Address.SessionId == renewedId)?.Address.Incarnation;
            }
            finally { await TraceDisposeAsync(runtime); }
        }
        check(written && renewed is { } incarnation && TraceRead(() => TraceLines(directory, "discovery_pass") is [var codexLine, ..] &&
            TraceText(codexLine, "adapter") == "codex-native" && TraceText(codexLine, "pass") == "completed" &&
            TraceBool(codexLine, "censusComplete") == false && TraceStrings(codexLine["diagnostics"]) is ["codex_owner_lookup_skipped"] &&
            TraceChanged(codexLine, ("codex:" + threads[0].ToString("D"), incarnation, "renewed"),
                ("codex:" + threads[1].ToString("D"), null, "skipped_slow"), ("codex:" + threads[2].ToString("D"), null, "skipped_unreached"))),
            TracePassCodexOutcomes);
    }

    // Label 18, two failed-pass scenarios: an unexpected exception (the runtime's second catch, adapter_failed), and a coded
    // discovery failure (its first catch: the code alone, never the description) followed by a pass that recovers.
    private static async Task TraceFailedPassAsync(Action<bool, string> check, string scratch)
    {
        string unexpected = Path.Combine(scratch, "failed");
        bool repeated;
        using (var store = new BrokerStore(unexpected))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            var adapter = new LifecycleAdapter("codex-native", faultDiscovery: true);
            var runtime = TraceRuntime(store, federation, adapter);
            runtime.Start(default);
            try { repeated = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= 4); }
            finally { await TraceDisposeAsync(runtime); }
        }
        bool unexpectedOnce = repeated && TraceRead(() => TraceLines(unexpected, "discovery_pass") is [var unexpectedLine] &&
            TraceFailedLine(unexpectedLine, "adapter_failed"));

        string coded = Path.Combine(scratch, "failed-coded");
        bool failing, recovered;
        using (var store = new BrokerStore(coded))
        {
            await using var federation = new SlowDirectoryFederation(Guid.Empty);
            var adapter = new TraceAdapter("codex-native");
            adapter.FailWith(new AdapterDiscoveryDiagnostic("synthetic_discovery_failure", TraceFailureDescription));
            var runtime = TraceRuntime(store, federation, adapter);
            runtime.Start(default);
            try
            {
                failing = await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= 4);
                adapter.FailWith(null);
                recovered = await TraceUntilAsync(() => TraceLines(coded, "discovery_pass").Count >= 2);
                int passes = Volatile.Read(ref adapter.Passes);
                recovered = recovered && await TraceUntilAsync(() => Volatile.Read(ref adapter.Passes) >= passes + 3);
            }
            finally { await TraceDisposeAsync(runtime); }
        }
        bool codedOnce = failing && recovered && TraceRead(() => TraceLines(coded, "discovery_pass") is [var codedLine, var recoveredLine] &&
            TraceFailedLine(codedLine, "synthetic_discovery_failure") && TraceText(recoveredLine, "pass") == "completed" &&
            recoveredLine.TryGetPropertyValue("failure", out var recoveredFailure) && recoveredFailure is null &&
            !TraceAll(coded).Contains(TraceFailureDescription, StringComparison.Ordinal));
        check(unexpectedOnce && codedOnce, TracePassFailed);
    }

    private static BrokerRuntime TraceRuntime(BrokerStore store, IPeerFederation federation, ILocalAgentAdapter adapter) =>
        new(store, federation, [adapter]) { DiscoveryPassDeadline = TimeSpan.FromMilliseconds(300), DiscoveryInterval = TimeSpan.FromMilliseconds(100) };

    // Disposed before the checks that follow it: a loop that faulted would rethrow here and hide the label.
    private static async Task TraceDisposeAsync(BrokerRuntime runtime)
    {
        try { await runtime.DisposeAsync(); }
        catch (Exception) { }
    }

    private static BrokerSession TraceRow(SessionAddress address, string adapter, string label) =>
        new(address, label, adapter, SessionCapabilities.None, SessionActivity.Idle, DateTimeOffset.UtcNow);

    // An evaluation that meets an unexpected shape, or an events file it cannot read, is a failed check, never an unlabelled exception.
    private static bool TraceRead(Func<bool> evaluate)
    {
        try { return evaluate(); }
        catch (Exception) { return false; }
    }

    // Polls every 20 ms; false at the limit, never a throw.
    private static async Task<bool> TraceUntilAsync(Func<bool> condition, double seconds = 5)
    {
        var watch = Stopwatch.StartNew();
        while (!TraceRead(condition))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) return false;
            await Task.Delay(20);
        }
        return true;
    }

    private static List<JsonObject> TraceLines(string directory, string kind) =>
        EventsFile.Read(directory).Where(line => TraceText(line, "event") == kind).ToList();

    private static int TraceCount(string directory, string kind)
    {
        try { return TraceLines(directory, kind).Count; }
        catch (Exception) { return -1; }
    }

    private static List<JsonObject> TraceForgotten(string directory, SessionAddress address) =>
        TraceLines(directory, "session_forgotten").Where(line => TraceText(line, "address") == TraceAddress(address)).ToList();

    private static string TraceAddress(SessionAddress address) =>
        address.BrokerId.ToString("D") + "/" + address.SessionId + "/" + address.Incarnation.ToString("D");

    private static string TraceAll(string directory) => Encoding.UTF8.GetString(EventsFile.Bytes(directory));

    private static string? TraceText(JsonObject line, string key) => line[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static long? TraceLong(JsonObject line, string key) => line[key] is JsonValue value && value.TryGetValue(out long number) ? number : null;

    private static bool? TraceBool(JsonObject line, string key) => line[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private static string[]? TraceStrings(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue(out string? text)) return null;
            result.Add(text);
        }
        return result.ToArray();
    }

    // The changed entries' VALUES, in order (the expectation sorted ordinal by session ID); extra keys are label 9's business.
    private static bool TraceChanged(JsonObject line, params (string SessionId, Guid? Incarnation, string Outcome)[] expected)
    {
        if (line["changed"] is not JsonArray changed || changed.Count != expected.Length) return false;
        var ordered = expected.OrderBy(item => item.SessionId, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < ordered.Length; index++)
            if (changed[index] is not JsonObject entry || TraceText(entry, "sessionId") != ordered[index].SessionId ||
                TraceText(entry, "incarnation") != ordered[index].Incarnation?.ToString("D") || TraceText(entry, "outcome") != ordered[index].Outcome)
                return false;
        return true;
    }

    private static bool TraceSchemaOnly(JsonObject line)
    {
        var keys = line.Select(pair => pair.Key).ToArray();
        return (keys.SequenceEqual(TracePassKeys) || keys.SequenceEqual(TracePassKeys.Append("eventsLost"))) &&
            (line["changed"] is not JsonArray changed ||
                changed.All(entry => entry is JsonObject row && row.Select(field => field.Key).SequenceEqual(TraceRowKeys)));
    }

    private static bool TraceFailedLine(JsonObject line, string failure) =>
        TraceText(line, "pass") == "failed" && TraceText(line, "failure") == failure &&
        new[] { "censusComplete", "diagnostics", "changed", "dropped" }.All(key => line.TryGetPropertyValue(key, out var value) && value is null);

    // The adapter's whole trace, in order (the expectation sorted ordinal by session ID).
    private static bool TraceSame(IReadOnlyList<DiscoveryTraceRow>? actual, params (string SessionId, Guid? Incarnation, string Outcome)[] expected) =>
        actual is not null && actual.SequenceEqual(expected.Select(item => new DiscoveryTraceRow(item.SessionId, item.Incarnation, item.Outcome))
            .OrderBy(row => row.SessionId, StringComparer.Ordinal));

    private sealed class TraceFixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Scripted rows with 600 ms leases. It reports no LastDiscoveryTrace, so the runtime's fallback (every returned row renewed)
    // applies. One pass may be delayed past its lease, ignoring the pass deadline like a transport that outlives it; a failure,
    // when set, is thrown as a coded discovery failure on every pass.
    private sealed class TraceAdapter(string id) : ILocalAgentAdapter
    {
        private static readonly TimeSpan Lease = TimeSpan.FromMilliseconds(600);
        private readonly object gate = new();
        private BrokerSession[] rows = [];
        private AdapterDiscoveryDiagnostic[] diagnostics = [];
        private AdapterDiscoveryDiagnostic? failure;
        private TimeSpan delayNext;
        private DateTimeOffset? lastUntil, leaseBeforeDelay;
        internal int Passes, DelayedPassesCompleted;
        internal readonly ConcurrentDictionary<SessionAddress, DateTimeOffset> LastLease = new();
        public string Id => id;
        public IReadOnlyList<AdapterDiscoveryDiagnostic> DiscoveryDiagnostics { get { lock (gate) return diagnostics; } }
        // The lease the rows carried in the pass before the delayed one.
        internal DateTimeOffset? LeaseBeforeDelay { get { lock (gate) return leaseBeforeDelay; } }
        internal void SetRows(params BrokerSession[] next) { lock (gate) rows = next; }
        internal void SetDiagnostics(params AdapterDiscoveryDiagnostic[] next) { lock (gate) diagnostics = next; }
        internal void FailWith(AdapterDiscoveryDiagnostic? next) { lock (gate) failure = next; }
        internal void DelayNextPass(TimeSpan delay) { lock (gate) delayNext = delay; }

        public async Task<IReadOnlyList<BrokerSession>> DiscoverAsync(Guid brokerId, CancellationToken token)
        {
            Interlocked.Increment(ref Passes);
            TimeSpan delay;
            AdapterDiscoveryDiagnostic? failing;
            lock (gate)
            {
                delay = delayNext; delayNext = TimeSpan.Zero; failing = failure;
                if (delay > TimeSpan.Zero) leaseBeforeDelay = lastUntil;
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            if (failing is not null) throw new AdapterDiscoveryException(failing);
            BrokerSession[] result;
            lock (gate)
            {
                var until = DateTimeOffset.UtcNow + Lease;
                result = rows.Select(row => row with { LeaseUntil = until }).ToArray();
                foreach (var row in result) LastLease[row.Address] = until;
                if (result.Length != 0) lastUntil = until;
            }
            if (delay > TimeSpan.Zero) Interlocked.Increment(ref DelayedPassesCompleted);
            return result;
        }

        public Task<AdapterResult> SubmitAsync(BrokerMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private enum TraceLookup { Renew, Missing, InvalidBinding, Fail, Reject, ReconnectOutlasted, Slow }

    // A Codex provider whose owner lookups take a scripted branch per conversation. Slow cancels the caller's pass (its deadline
    // passes during this lookup), then raises the cancellation, as a lookup that ran out of time does.
    private sealed class TraceRowProvider : ICodexNativeProvider
    {
        private static readonly Guid OwnerClient = Guid.NewGuid(), BindingIncarnation = Guid.NewGuid();
        internal readonly Dictionary<Guid, TraceLookup> Lookups = [];
        internal Guid[] Threads = [];
        internal Guid? StaleThread;
        internal string? ObserveFailure;
        internal CancellationTokenSource? Pass;
        public bool NativeIdleGuard => false;

        internal static DesktopOwnerBinding Binding(Guid conversation, uint serverPid = 12345) =>
            new(conversation, OwnerClient, BindingIncarnation, serverPid, 134000000000000000, true) { RouterTrust = DesktopRouterTrust.ExactPinned };

        public Task<IReadOnlyList<DesktopDirectorySession>> ObserveAsync(CancellationToken token)
        {
            if (ObserveFailure is { } code) return Task.FromException<IReadOnlyList<DesktopDirectorySession>>(new DesktopAdapterException(code));
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<DesktopDirectorySession>>(Threads.Select(thread => new DesktopDirectorySession(thread,
                DesktopSessionActivity.Idle, thread == StaleThread ? now.AddMinutes(-3) : now, now)).ToArray());
        }

        public Task<DesktopOwnerBinding?> ResolveOwnerAsync(Guid conversationId, CancellationToken token) =>
            Lookups.GetValueOrDefault(conversationId) switch
            {
                TraceLookup.Missing => Task.FromResult<DesktopOwnerBinding?>(null),
                TraceLookup.InvalidBinding => Task.FromResult<DesktopOwnerBinding?>(Binding(conversationId, serverPid: 1)),
                TraceLookup.Fail => Task.FromException<DesktopOwnerBinding?>(new IOException("Synthetic owner connection failure.")),
                TraceLookup.Reject => Task.FromException<DesktopOwnerBinding?>(new DesktopAdapterException("owner_discovery_rejected")),
                TraceLookup.ReconnectOutlasted => Task.FromException<DesktopOwnerBinding?>(new CodexReconnectOutlastedPassException(token)),
                TraceLookup.Slow => SlowLookup(token),
                _ => Task.FromResult<DesktopOwnerBinding?>(Binding(conversationId)),
            };

        private Task<DesktopOwnerBinding?> SlowLookup(CancellationToken token)
        {
            Pass?.Cancel();
            return Task.FromException<DesktopOwnerBinding?>(new OperationCanceledException(token));
        }

        public Task<DesktopSubmissionResult> SubmitAsync(DesktopOwnerBinding binding, Guid messageId, string text, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TraceCensusProvider : IClaudeNativeProvider
    {
        internal ClaudeNativeCandidate[] Candidates = [];
        public Task<IReadOnlyList<ClaudeNativeCandidate>> DiscoverAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<ClaudeNativeCandidate>>(Candidates);
        public Task<ClaudeNativeCandidate?> RefreshAsync(int processId, CancellationToken token) => Task.FromResult<ClaudeNativeCandidate?>(null);
        public Task<SubmissionResult> SubmitAsync(NativeTarget target, NativeMessage message, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
