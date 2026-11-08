# AgentBroker post-push infrastructure qualification, 2026-11-07

Baseline: published master `7ab40743b5b40f525921601ac0bc59da56dd73b8`, followed by integration repairs. This independent review qualifies source/caller controls and scoped regressions. Combined installed acceptance and publication remain separate campaign work.

Scope: `N-audit-1106-` IDs 054, 055, 056, 082, 083, 084, 085, 091, 343, 351, 352, 353, 359, 360, 361, 362, 363, 370, 372, 373, 374 and 376. Module/core/native work, final installed acceptance and publication have separate records. No installed-app, GUI, live allowance, identity policy or GPU action belongs to this lane.

Thirty-six distinct independent controls qualified. Each used a fresh child process with the changed source SHA256 and advanced timestamp, then restored exact source bytes and passed the related smoke. A separate comparison confirmed every current controlled source matched its pre-control backup, with no retained private component fixture trees. HarnessOutput carries 25 durable checks, including execution of actual grading statements with incomplete synthetic results.

Two further review defects were confirmed and corrected. The health check must send an untrusted hostname at the request's port because localhost at that port is intentionally permitted. Restoring localhost fails `HOST_ALIAS_PRESERVES_REQUEST_PORT`. The offline registry and fake dispatcher must match the complete passing summaries of the three module script regressions, including their cleanup and privacy suffixes. Restoring the incomplete publisher summary fails the healthy dispatcher naming `Test-ModulePublishCommit.ps1`; child output is now retained in that diagnostic.

| Source/caller area | Qualified negative evidence |
| --- | --- |
| Harness assertion floors, actual Broker/Core gate/CI callers | Reduced inventory and disabled grading each fail their named registration/direct-guard check. |
| Module assertions | Host-only and foreign-module output count zero through both the helper and actual module runner assignment. |
| Gate dispatch | Disabling the independent discovery call fails `MODULE_CHECKS_COLLECT_INDEPENDENTLY`; removing payload dispatch fails `SHIPPED_BROKER_PAYLOAD_IS_GATED`. |
| Fixture ownership | Removing reparse refusal or the warning preserving an original failure fails its specific ownership check. |
| Blocked criteria and remainder selection | Ignoring the default blocked source or either empty selection dimension fails the corresponding source/selection check. |
| Pin-harness preconditions | Skipping a selected healthy group fails `SELECTED_HEALTHY_GROUP_REQUIRED`; creating evidence before the lock fails `LOCK_PRECEDES_EVIDENCE`. |
| Installer witness grading | Accepting a wrong single label fails exactly one `EXACT_FAILURE_REQUIRED`; deleting a variant label fails exactly one `INSTALLER_VARIANT_FAILURE_LABEL_REQUIRED`. |
| Legacy encoding visibility | Removing the code-page condition fails exactly one `NON1252_SKIP_VISIBLE` under the simulated non-1252 code page. |
| Editor probe | A real, otherwise valid foreign workspace fails specifically at scope. Removing that scope condition fails the named foreign-workspace assertion; no editor launches. |
| Actual cleanup callers | Executed the real discovery/editor/worker/installer `finally` bodies with unexpected nested/read-only files and a prior failure. Removing each cleanup call fails its named absence check. |
| Installer settings | Disabling the real publication leaf or disconnecting the main caller fails publication checks. Forcing two listeners fails the actual main assignment using preserved LAN-off custom ports. |

A further review found stale pin-harness anchors in three of 129 witnesses: `codex-error-code-reporting`, `broker-d-wait-returns-unaddressable` and `broker-e-wait-reply-skips-tombstone`; 126 initially matched their declared replacement counts. Those repairs produced 129/129 after the initial core integration. The final delivery-claim merge then required two more metadata repairs: `deferred-claim-atomicity` now spans three selection/release/settlement locks, and `broker-e-wire-record-member-added` must preserve the new internal claim body while changing the positional receipt header. Final byte-exact preflight again matches 129/129, preserving every witness name and source/project map. The one selected real `codex-error-code-reporting` case completed PASS with one intended assertion FIRED; the whole129 campaign was not run.

The legacy actual31 invocation retained 29 passing routes, including full53 dispatcher, installer80 and Python56. Two failures came from the private launcher's inherited PowerShell7 module paths: `Get-FileHash` was unavailable, including in the log-observation precondition before its fake caller started. The corrected native legacy launch passed those exact two checks with every captured public source hash unchanged. This is qualified composite evidence, explicitly not a single all-green invocation. Separate Harness25 and editor Prep32 regressions passed under both runtimes; cleanup33 passed.

The PowerShell7 actual31 invocation retained 30 passing routes and one failed dispatcher route. A bounded host reproduction proved its summary phrase had wrapped across formatted error lines. The producer now emits `OFFLINE-SUMMARY-MISSING:<name>`; the checker validates that exact plain marker and refuses false markers. Sixteen actual-producer/classifier component checks passed under each runtime, and two named negative controls qualified. The affected full53 dispatcher regression subsequently passed; the original failed invocation remains recorded as failed.

The artifact repair requires fresh test DLL and known runtime-dependency timestamps/hashes after no-incremental builds, exact identities before and after execution, and a changed hash for the mutated dependency. The representative Codex case declares its intended assertion and preserves current binding cleanup and exception type. Thirteen pin-harness unit cases and the expanded 64-test capture regression passed; six artifact/intended-assertion controls qualified. The final real case supplies three fresh build proofs and ten matching in-process Worker/test execution identities across healthy, mutant and restored phases. The mutant product hash changed, failed exactly `Codex unsupported_desktop_version survives the real adapter catch during directory observation`, and retained all516 existing checks; restored diagnostics92, existing516, deferred4 and the selected Codex group passed. Independent comparison matched all26 source backups and confirmed the mutation lock absent. No whole129 mutation campaign or installed-app claim is made.

| Additional review finding | Current disposition |
| --- | --- |
| INFRA-R1: incomplete offline summaries and matching incomplete fakes | FIXED in the registry and fake output. Killing control restores the incomplete summary and fails naming the publisher test, requalified after the marker repair. Actual module-script routes passed in both collected aggregates. |
| INFRA-R2: the same-port health probe used an allowed hostname | FIXED with `untrusted.invalid` and both default/custom-port self-tests. Restoring localhost fails the named authority check. Installed HTTP acceptance belongs to the combined campaign. |
| INFRA-R3: stale pin-harness replacement anchors | FIXED: initial three and two final claim-merge anchors repaired; final129/129 byte-exact preflight preserves all witness identities. The selected actual Codex case fired its intended assertion and restored regressions passed. |
| INFRA-R4: pin builds lack artifact provenance | FIXED with real build/execution identity checks and durable synthetic cases. Six named controls qualify stale build, stale dependency, pre-launch mismatch, in-flight change, unchanged mutant hash and wrong assertion refusal. Actual final Codex proof contains3 fresh builds,10 matching in-process executions and26 exact source restorations. |
| INFRA-R5: formatted exception prose misclassified a correct silent-summary refusal | FIXED with a plain named discriminator. Host reproduction retained;16 component cases pass on each runtime and two controls qualify exact marker and zero-work refusal. Full affected53 regression passed. |

The two retired one-shot scripts have no remaining tracked callers outside the backlog and documentation. No tools were provisioned. A useful scripts-utilities guidance addition is the native Windows PowerShell launcher when Python inherits PowerShell7 module paths; no new package is needed.

Freeze status supersedes the earlier pending milestones. Full affected53 passed in556.718 seconds. Original legacy/modern actual31 invocations remain failed: legacy reused29 greens plus two exact corrected environment checks with matching source hashes; modern reused30 greens plus the corrected full53 route. Neither is claimed as a single all-green invocation. Capture64, Harness25/Prep32 on both runtimes, cleanup33 and the plain-discriminator component16 on each runtime passed. Thirty-six distinct source controls qualified with exact restoration.

Two real Codex attempts were refused before mutation. The first exposed a stale Claude diagnostic fixture asserting raw32 instead of the reviewed raw128/usable32 separation. The approved correction uses129 reversed unsupported entries, exact sorted visits501..628,128 compatibility diagnostics, one truncation note, no sessions and incomplete census. Bounded3 and elevated8 passed, observing114 characters under the retained116 cap; no C# registration was added.

The second passed focused92 and existing516 in healthy/restored phases. Configured existing inventory234 rejected it. Independent counting confirmed516 PASS labels, with all11 published pre-cutoff registrations retained. ScratchLifetime4, BenchmarkValidation5 and Claude observation8 partition17 additions; remaining499 are actual assertions in that run, not a historical499 run. A fresh final merged existing-only execution independently measured516 again against the proved restored build without rebuilding; the fixed inventory is now516 and its strict equality guard remains.

Those two historical attempts remain FAIL/witnesses0. They are not overwritten by the successful final post-claim/rotation case, retained as `artifacts/pin-diagnostic-mutations-postpush-final-claim-renewal-20261107/results.json`. Final independent checks compare all26 product source bytes with that run's durable backups, confirm the lock absent and match all10 in-process Worker/test identities to the three recorded fresh builds. Isolated runtimes were removed and the C# slot released. Healthy/restored product SHA256 is `cdf8dd3b06db962c37d87339792eaf300b8052f963dab2ed7f362a99dc68ee47`; the executed reporting mutant is `505798624c924789926c29ef1918eddf979111ebc17797f946c78b9787575c10`. No whole129 campaign, installed acceptance or publication claim is made here.

The final combined seven-suite run passed3178 assertions with exit0. Host SourceInvariants also passed with exit0. The measured component counts below are now the floors in `tests/Test-AgentBroker.ps1` and the independent expected table in `tests/Test-HarnessOutputContracts.ps1`; intentional registration removal requires review. The actual caller's loss vector is407, one below Core408. Focused25 contracts and final floor controls are qualified under both Windows PowerShell5.1 and PowerShell7, without repeating the seven-suite run.

| Component | Final measured floor |
| --- | --- |
| Core |408 |
| Relay |40 |
| Claude |212 |
| Desktop |594 |
| Adapter |84 |
| Network |250 |
| Worker |1590 |

| Original ID | Disposition at infrastructure freeze | Evidence |
| --- | --- | --- |
| N-audit-1106-054 | FIXED | Actual Broker/Core gate/CI callers and final measured seven-suite floors totaling3178; focused25 on PS5.1/7, lowered-floor and unreachable actual-caller controls fail their intended assertions on both runtimes and exact-restored25 pass. |
| N-audit-1106-055 | FIXED | Selected healthy group/dedup/nonzero guard; `SELECTED_HEALTHY_GROUP_REQUIRED`. Fixed existing inventory516, final129 unique anchors and one actual Codex witness FIRED with rebuilt/executed provenance and restored regressions. |
| N-audit-1106-056 | FIXED | Tagged own-module counter and real caller exclude host/foreign assertions; named controls. |
| N-audit-1106-082 | FIXED |31 offline routes wire the stated self-tests; full53 and qualified route composites, installer80. |
| N-audit-1106-083 | FIXED | Actual publication leaf and main caller share the tested helper; two killing controls and installer80. |
| N-audit-1106-084 | FIXED | Actual installer finally removes unexpected nested/read-only contents and preserves original failures; cleanup/warning controls. |
| N-audit-1106-085 | FIXED | Actual main assignment reads preserved LAN-off/custom ports; forced-two-listener control fails. |
| N-audit-1106-091 | FIXED | Untrusted hostname at the actual port; restoring localhost fails its named authority check. |
| N-audit-1106-343 | FIXED | Discovery finally cleanup; disabling it fails `DISCOVERY_CLEANUP_ACTUAL_FINALLY`. |
| N-audit-1106-351 | FIXED | Independent discovery/freshness tries and correct labels; Gate5 and disabling-discovery control. |
| N-audit-1106-352 | FIXED | Lock before evidence; held-lock/collision tests and `LOCK_PRECEDES_EVIDENCE`. |
| N-audit-1106-353 | FIXED | All6 variants declare names and require one exact rebuilt failure; inventory/wrong-label controls. |
| N-audit-1106-359 | FIXED | Gate/CI dispatch payload checks; removing CheckPayload fails. Actual PS5 packaging payload52 passes through the explicit PS7 handoff; installed-app acceptance remains separate. |
| N-audit-1106-360 | FIXED | Default backlog+blocked criteria sources, explicit single-file mode; Criteria6 and ignored-blocked-source control. |
| N-audit-1106-361 | FIXED | Actual editor finally removes owned/foreign fixtures; Prep32 on both runtimes and cleanup control. |
| N-audit-1106-362 | FIXED | Real otherwise-valid foreign workspace refused at scope; removing condition fails its named assertion. |
| N-audit-1106-363 | FIXED | Cleanup33 removes fixture/registered worktree; disabling actual finally fails. |
| N-audit-1106-370 | FIXED | Complete opposite-side coverage and exact error prevent gap confounding; both guard controls, Remainder72. |
| N-audit-1106-372 | FIXED | Actual offline capture route; real64 cases and fail/silent dispatch coverage. |
| N-audit-1106-373 | FIXED | Actual capture runner includes mutation infrastructure modules; real64 cases and dispatch coverage. |
| N-audit-1106-374 | FIXED | Explicit non-1252 skip; removing condition fails exactly one `NON1252_SKIP_VISIBLE`. |
| N-audit-1106-376 | CLOSED-VERIFIED | Retired uncalled one-shots removed; tracked caller inventory is zero outside retained history/docs. |

Additional findings at final infrastructure handoff: INFRA-R1 complete summary matching and INFRA-R2 untrusted same-port host are FIXED. INFRA-R3 final stale anchors are FIXED with129/129 preflight. INFRA-R4 artifact provenance is FIXED with6 killing controls plus the actual final1-witness/3-build/10-execution/26-source proof. INFRA-R5 formatted prose classifier is FIXED with16/16,2 controls and full53. INFRA-R6 obsolete diagnostic fixture is FIXED with reviewed3+8 smokes. INFRA-R7 stale234 inventory is FIXED with a fresh final516 measurement, retained equality guard and the successful actual Codex case. Historical failed receipts remain failed.

INFRA-R8 is a separately confirmed full-gate runtime gap: the PS5 caller reached the PS7-only ZIP publisher directly and stopped before any of the seven C# suites started. `tests/Test-AgentBrokerPackaging.ps1` now delegates its complete check to a clean pwsh child only below PowerShell7, preserves the actual script/repository paths and switch presence, and fails on nonzero exit or absent/duplicate/failing/below-floor child summary through the existing HarnessOutput helper. Four private fresh-process controls fail exactly `LEGACY_RUNTIME_HANDOFF_REQUIRED`, `PAYLOAD_SWITCH_FORWARDED`, `CHILD_EXIT_GUARD_REQUIRED` and `CHILD_SUMMARY_GUARD_REQUIRED`; every source restoration passes. An actual native PS5-to-pwsh argument roundtrip preserves spaced script/repository paths with the payload switch present and absent. That routing variant uses an explicitly synthetic child footer and is not the payload test evidence.

The separate full-source affected PS5 `-CheckPayload` execution passed52 actual assertions, including shipped apphost `--help`, licensing, path omission and a51,467,785-byte ZIP with357 entries. Direct PS7 source compatibility passed28 assertions. The known actual temporary ZIP directory and all private caller/argument fixtures were removed; scoped diff-check passed. Repository-wide script/workflow search found the only other executable ZIP caller in `packaging/New-ModulePublish.ps1`, whose source requires7. INFRA-R8 is FIXED; no ZIP publisher/product/canonical-watch change or rebuild was needed. Original full-gate failure evidence is retained separately; the subsequent combined3178 result and installed acceptance have their own campaign receipts.
