# AgentBroker post-push core audit

Audit baseline: published commit `7ab40743b5b40f525921601ac0bc59da56dd73b8`, 2026-11-07. Scope is AgentBroker core/runtime/federation and supporting tests. This record covers the lane changes and executed receipts below; final combined regression and installed-app acceptance belong to the integration campaign.

## Audit coverage and source changes

The review followed complete store admission/claim/settlement and recovery/inbox paths, runtime registration/discovery/delivery/receipt/shutdown loops, federation client/server/reconnect paths, wait and metadata registries, and the supporting Core/Network/Relay/Worker fixtures. Store locks, asynchronous change signals, exact-address checks, cancellation boundaries, bounded maps and owned-resource disposal were inspected together. Native image verification, module UI and general release scripts belong to the parallel reports.

Three additional defects were confirmed beyond the incoming CSV: a valid maximum escaped directory exceeded the old 256 KiB response bound; the cleanup audit caught twelve further fixture roots; reused message UUIDs inherited old observation state after store retention. Each has source changes and executed evidence below. No new performance improvement or absence of all possible races/leaks is claimed.

Federation retry proof originates only in the actual socket-connect callback, before a connection can be handed to HTTP. The [transport](../../tools/AgentBroker.Worker/Federation/PeerFederation.cs) records whether a connect ever succeeded and creates a typed [not-started result](../../tools/AgentBroker.Worker/Federation/PeerSubmissionNotStartedException.cs); [runtime](../../tools/AgentBroker.Worker/BrokerRuntime.cs) releases only that proof for the same exact peer. Generic failures after connection, TLS failures, interrupted responses and arbitrary adapter exceptions retain uncertainty. A message keeps its original ID and exact recipient.

The product's default 16 KiB UTF-8 message can expand sixfold as JSON. Message request and receipt response paths now use a separate 128 KiB bound, while pairing retains its 32 KiB bound. The valid 256-row directory path uses 1 MiB. [Authenticated boundary checks](../../tests/AgentBroker.Network.Tests/FederationBoundaryChecks.cs) exercise complete send/receipt/reconciliation and directory exchanges rather than serializer helpers alone. Custom non-product Core limits are not claimed to have arbitrary transport capacity.

## Incoming finding dispositions

The lane fixes and thirty-four named negative controls are complete and byte-restored. Already-fixed rows retain their prior qualified evidence and are not counted as new defects. Root owns final integrated regression and actual installed-app acceptance before any commit.

| Backlog ID | Disposition and source | Evidence / control requirement |
| --- | --- | --- |
| N-audit-1106-003 | FIXED: authoritative pre-connect deferral, with connected failures still uncertain. | `FEDERATION_PRECONNECT_DEFERS` kills loss of pre-connect deferral; `PAIRED_UNCERTAIN_SETTLEMENT` kills broad retry after connection. Actual authenticated exchanges preserve the message ID and exact recipient. |
| N-audit-1106-004 | CLOSED-VERIFIED through campaign R05. | BrokerStore.ReplaceFile normalizes native Win32 failure to IOException; actual locked-file `DURABLE_REPLACE_CLASSIFIED` and `DURABLE_RUNTIME_STOP` evidence is in the campaign ledger. |
| N-audit-1106-005 | FIXED: message request, immediate receipt and later receipt bounds agree. | Request-client/server and immediate/later receipt bound regressions each fail `FEDERATION_ESCAPED_MESSAGE` or `FEDERATION_ESCAPED_RECEIPT`; escaped reconciliation remains green. |
| N-audit-1106-060 | CLOSED-VERIFIED through campaign peer-reason work. | RecordPeerReceipt preserves six allowlisted reasons; actual immediate/reconcile/notice controls remain qualified in the campaign ledger. Unknown metadata retains fixed fallback. |
| N-audit-1106-061 | CLOSED-VERIFIED through the same ReplaceFile normalization as004. | CompactTombstones calls that shared function and catches IOException. The shared replacement fault was executed; no separate compaction-fault run is invented. |
| N-audit-1106-062 | FIXED documentation. | Core README now names `NativeIdleGuard`, matching BrokerModels and BrokerStore.Ready; no runtime behavior change. |
| N-audit-1106-077 | CLOSED-VERIFIED through campaign R14. | WorkerLifetime drains before checking final fatal state, emits fixed stderr and returns73. Prior exit/diagnostic/drain controls remain the proof. |
| N-audit-1106-048 | FIXED test fixture. | Reachable live HTTP and HTTPS nonroot listeners each kill their removed policy guard at `RECONNECT_ENDPOINT_POLICY`. Hostname/public inputs remain additional refusal cases, without separate reachability claims. |
| N-audit-1106-049 | FIXED test witness. | Removing the actual reconnect join fails `RECONNECT_SHUTDOWN_JOINED`; its restored task completes successfully. |
| N-audit-1106-050 | FIXED test input. | Otherwise valid authenticated pairing JSON rejects oversized content-length and chunked input. Each removed bound fails `PAIRING_BODY_LENGTH` or `PAIRING_BODY_STREAM`. |
| N-audit-1106-318 | FIXED test observation. | Removing the reserved-sender candidate exclusion fails `NOTICE_NO_CHAIN_OBLIGATIONS` after actual expiry and retention; the check observes resulting drop obligations. |
| N-audit-1106-319 | FIXED test synchronization. | The next fake ReceiveAsync invocation proves previous packet handling completed. Removing query-source or rate guards fails `DISCOVERY_QUERY_POLICY` or `DISCOVERY_QUERY_RATE`. |
| N-audit-1106-320 | FIXED Network invocation cleanup. | Full Network240 passes its invocation cleanup audit after node/key disposal. Shared missing-disposal, disabled-audit and linked-path controls are recorded below. |
| N-audit-1106-321 | FIXED test input. | An otherwise valid 1401-byte advertisement fails `DNS_RESPONSE_SIZE` when the size guard is removed; its unpadded positive parses. |
| N-audit-1106-322 | FIXED Relay witness. | Relay40 passed. Removing owned-engine termination fails `RELAY_LISTENER_ENGINE_EXIT`, which observes the actual Process.Start-owned process. |
| N-audit-1106-323 | FIXED benchmark preflight. | Actual benchmark healthy/lost-history scenarios pass their witness. Weakening exact history validation fails `BENCHMARK_HISTORY_GUARD`. |
| N-audit-1106-324 | FIXED benchmark preflight. | Actual complete/incomplete directory and measured-prefix scenarios pass their witnesses. Disabled completeness/prefix guards fail `BENCHMARK_DIRECTORY_GUARD` and `BENCHMARK_PAGE_GUARD`. |
| N-audit-1106-325 | Owned by parallel native/firewall report. | No closure claimed by this lane. |
| N-audit-1106-326 | FIXED fixture ownership and cleanup. | Shared OwnedScratch and explicit owners pass ScratchLifetime4 and full Worker1388. Removed real ToolFixture disposal, disabled invocation audit and removed linked-path refusal fail `SCRATCH_FIXTURE_WIRING`, `SCRATCH_AUDIT_FIRES` and `SCRATCH_LINK_REFUSED`. |
| N-audit-1106-327 | FIXED fixture ownership and cleanup. | Shared OwnedScratch and explicit owners pass ScratchLifetime4 and full Worker1388. Removed real ToolFixture disposal, disabled invocation audit and removed linked-path refusal fail `SCRATCH_FIXTURE_WIRING`, `SCRATCH_AUDIT_FIRES` and `SCRATCH_LINK_REFUSED`. |
| N-audit-1106-329 | FIXED fixture ownership and cleanup. | Shared OwnedScratch and explicit owners pass ScratchLifetime4 and full Worker1388. Removed real ToolFixture disposal, disabled invocation audit and removed linked-path refusal fail `SCRATCH_FIXTURE_WIRING`, `SCRATCH_AUDIT_FIRES` and `SCRATCH_LINK_REFUSED`. |
| N-audit-1106-330 | FIXED fixture ownership and cleanup. | Shared OwnedScratch and explicit owners pass ScratchLifetime4 and full Worker1388. Removed real ToolFixture disposal, disabled invocation audit and removed linked-path refusal fail `SCRATCH_FIXTURE_WIRING`, `SCRATCH_AUDIT_FIRES` and `SCRATCH_LINK_REFUSED`. |
| N-audit-1106-335 | FIXED fixture ownership and cleanup. | Shared OwnedScratch and explicit owners pass ScratchLifetime4 and full Worker1388. Removed real ToolFixture disposal, disabled invocation audit and removed linked-path refusal fail `SCRATCH_FIXTURE_WIRING`, `SCRATCH_AUDIT_FIRES` and `SCRATCH_LINK_REFUSED`. |
| N-audit-1106-328 | FIXED bounded traversal. | Pagination34 passed. Removed actual inbox/directory cursor progress fails promptly at `INBOX_TRAVERSAL_PROGRESS` or `DIRECTORY_TRAVERSAL_PROGRESS` under independently bounded traversal. |
| N-audit-1106-331 | FIXED three remaining owners; one already fixed. | RuntimeLifecycle/Reply/Send owners pass full Worker cleanup; shared missing-disposal/audit controls prove the ownership checks fail. PeerDiagnostics already had finally cleanup at baseline and is not counted as a new fix. |
| N-audit-1106-332 | FIXED independent test bounds. | Removing product runtime or diagnostic hard waits fails at independent eight-second test bounds: `RUNTIME_DIRECTORY_BUDGET` and `PEER_DIAGNOSTIC_TEST_BOUND`. |
| N-audit-1106-333 | FIXED late-result observation. | Changing the published returned directory after transports drain fails `RUNTIME_DIRECTORY_LATE_IMMUTABLE`, independently of the fake active-work counter. |
| N-audit-1106-334 | FIXED test phasing. | Explicit cancellation/release phases replace exact renewal counts. Lost slow-row marks and discarded retained connections fail `CODEX_SLOW_ORDER_RECONNECT` and `CODEX_RECONNECT_KEPT`. The separate bounded timing scenario is labelled honestly. |
| N-audit-1106-393 | FIXED stale comment. | LifecycleAdapter's comment now agrees with actual `RUNTIME_DELIVERY_FAULT_SETTLES` behavior and the passing lifecycle check. |

Every incoming ID is named explicitly above. None were deleted from the campaign inventory by this lane. The original full-audit CSV remains historical evidence.

## Additional post-push findings

| Local finding | Disposition | Evidence |
| --- | --- | --- |
| CORE-POSTPUSH-01 | FIXED valid escaped-directory envelope. | Actual maximum256rows exceed the former262144byte bound; `FEDERATION_DIRECTORY_ENVELOPE` passes with the1MiB bound. Reverting the bound fails `FEDERATION_DIRECTORY_ENVELOPE`. |
| CORE-POSTPUSH-02 | FIXED additional fixture cleanup. | The first full Worker run passed1388 functional assertions but failed its new invocation audit with12 remaining roots. Onboarding inbox/LAN, instructions, mailbox capacity/restart and7 polling roots now have explicit owners. Focused106 and corrected full Worker1388 pass with no new roots. |
| CORE-POSTPUSH-03 | FIXED reused-UUID observation lifetime. | Actual BrokerStore expiry/re-admission covers same/different exact recipients, pre-attempt engine reads, unbound attempts, fresh busy timing and capacity/index bounds. Old source separately failed recipient-index and pre-attempt-engine reproductions. Fixed smoke68 and engine-report19 passed. Removing busy rebind/read and engine replace/read guards fails `OBS_UUID_RETENTION_INDEX`, `OBS_UUID_BUSY_BEFORE_ATTEMPT`, `OBS_UUID_ENGINE_REPLACED` and `OBS_UUID_ENGINE_BEFORE_ATTEMPT`. |

Observation state now matches bounded immutable routing/lifetime metadata in addition to the UUID, without retaining text. Receipt reads validate that identity before the first new attempt. Replacing a lifetime removes the old recipient index and engine entry symmetrically. Exact routing, admission, attempt policy and the persistent schema are unchanged.

## Validation

| Phase | Executed result |
| --- | --- |
| Federation smoke then related regression | Smoke11, first Network238 passed before the later test improvements. |
| Focused strengthened checks | Cleanup4, benchmark validation5, lifecycle108, pagination34, discovery14, sender notices67 passed. |
| Related final Core/Network/Relay | Core408, Network240, Relay40 passed. |
| Additional cleanup smoke then full Worker | Focused106 passed; corrected full Worker1388 and invocation cleanup passed. The earlier functional1388/cleanupFAIL receipt is retained separately. |
| Observation repair smoke and related regression | Focused observation68, engine-report19 passed; root owns the combined full Worker run after all parallel imports. |
| Named negative controls | Federation7, Network/Core/Relay10, Worker5, lifecycle5, cleanup3 and observation-lifetime4: all34 qualified with actual rebuilt/executed hashes, one named failure, byte restoration and healthy restored execution. |

The initial symbolic-link fixture could not run without a Windows privilege. It was replaced with an unprivileged owned directory junction; the corrected test passed. An initial explicitly phased reconnect fixture deadlocked because it awaited the deliberately uncancellable connection before releasing it. Only the freshly verified owned test process was stopped; cancellation/release/await ordering was corrected and lifecycle108 passed. Those failed receipts and the corrected runs remain retained. Empty/interrupted and12 leaked fixture roots were removed after exact containment/reparse checks and independently checked absent. No unrelated process or tree was stopped or removed.

The original thirty controls were executed before the independent observation repair was imported. Their selected source files were then checked byte-for-byte against the controller manifest; the observation repair has a separate four-control manifest. Root merges parallel native/module work afterward and owns the final combined regression. No absence of every possible race or leak is asserted.

One earlier envelope-control attempt failed generically before its named assertion; it remains unqualified and was corrected and rerun. Two worker-control baseline attempts failed before any mutant because sandbox CNG key creation was unavailable; diagnostics were exposed, and the host rerun qualified all five worker controls. All failed receipts remain retained separately. No tools were installed; the existing .NET SDK and curated toolbox Python were sufficient, so no scripts-utilities catalog addition is recommended.
