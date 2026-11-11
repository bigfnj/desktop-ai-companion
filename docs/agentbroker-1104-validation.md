# AgentBroker 1.10.4 validation

Status, 2026-11-11: the four engineering fixes below have code, named controls and installed acceptance. Source-commit F2 postverification, identical-ZIP publication, exact-range F4, push, hosted CI and the published-delta audit remain separate release-completion checks. Their commit identities belong in Git and the release receipts; this source record cannot embed its own future commit hash. BACKLOG removal requires the final no-drop audit.

This is the light-tier 1.10.4 batch, separate from the closed 38-ID campaign. Published history and previous release bytes are preserved. The original four backlog bodies below are historical quotations, retained verbatim with newline normalization only; their old present-tense statements do not override the dated dispositions. The native lease-gap incident and standby result-contract gap remain OPEN, as do unrelated backlog entries.

## Accepted payload and installed scope

The compiled source identity is `1E3404795034D2126832A6042ED82F26F5802BCD6316C393F16AE2E537693757`, parent `50d666ad0e354883e1d59486593da2fa9aba0487`. The accepted ZIP is `FB1D0BE95579AF9589C9D9D7019A51DF03914A9E449C6B582D0237911006A747`, 51,508,278 bytes and 357 entries. PowerShell, workflow and documentation integration is recorded separately from that compiled identity. Listed-source equality is not a fresh graph discovery or committed-source F2.

The official StageUpdate returned native 0 on 2026-11-11, with readiness and 16 tools. Separate full-tree checks, including Claude's independent comparison, matched all 357 installed files to the accepted ZIP. Preservation checks matched the fresh pre-install host/settings/AgentFlow snapshot. There was no loose-file replacement, automatic retry or rollback. A newly prepared supported rollback remained available; earlier recovery preparations were not reused. Removal of the misleading Git suffix intentionally leaves the PE ProductVersion at `1.0.0`; module version `1.10.4` is recorded separately in its manifest, while source-content and parent stamps identify the compiled inputs.

Installed client acceptance returned native 0 with status `passed_with_limited_entry_coverage`: 17 invocations, eight retentions, malformed-value and envelope boundaries, metadata-only handoff, no-overwrite checks, and SUBST refusal/cleanup passed. Both inbox pages were empty, so real inbox-entry coverage is LIMITED; real recovery null/absence cases were observed. Operations were read-only, with no send or acknowledgement. The runner removed its owned fixture tree, including raw client streams and retained bodies. The independent acceptance review can check the summary, frozen runner and capture metadata, not independently reread or hash those removed streams.

The quiet lease observation retained nine actual samples over 43.368 seconds and reported `HEALTHY_LEASE_ADVANCEMENT`; its raw requests/streams remain available. This shows sampled exact-session freshness and advancement. It does not prove continuous presence, a forced pending-reconnect preparation path, production reconnect duration, historical cause or loaded-server assembly identity.

Evidence names below refer to the private release archive. Personal installation paths, participant addresses and private metadata values are intentionally absent.

| Actual evidence | Receipt and SHA256 |
|---|---|
| Official StageUpdate | `direct-active-installer-v1/run-08b6c80e38f84c9db91a38871e692247/result.json`, `D788C5FA6BD76B956BC8DD4AEC66FBE29710CDF20EABDB722166D33B9EC33217` |
| Full installed payload / preservation | `installed-payload-65cacdb59f214d119459faa349a63d96.json`, `EBD94C61B8127C029FB91FD5E45843A1310F5D1DE033266BD0E72B2D127A4178`; preservation `FE87E283E855AD39655597573F72F0754FD0DB7F63989D02DFBD7C9339741197` |
| Installed client summary | `installed-client-acceptance-7deef56a062f4576a588cae3acde61ab.summary.json`, `8D642479149718B68C165AFD88570B1B1EEAA68E064A6EB97F217EA6FE3CB7AE` |
| Independent acceptance/lease review | Markdown `70681F63F77E06337FCEC73BE192128D8287AD10B89ACBDB97EC72D2C0B763BD`; JSON `577B78E732870CF8FB5256877ADD127769AA5932F8D7DA818D89DD57EB2C97EB`; acceptance raw-retention limits above remain explicit |
| Lease observation | `observe-fb32a317907a4b7babadd09589268689/results.json`, `4E54304888C9F3D360DA1E08CEB86C7409F9E9F710394C0FCBAEB6636ACA26A5` |
| Candidate F5 archive | Abbreviated SHA256: receipt `D79AA4CD`, raw scan `B22DEB34`, scanner `9FC347C1`; four-value, 357-entry zero-hit result, subject to the private-input handling limits below |

## Four engineering dispositions

## N-broker-release-provenance-01

<!-- AGENTBROKER1104 ORIGINAL BEGIN N-broker-release-provenance-01 -->
- 📌 **N-broker-release-provenance-01** Found 2026-11-10 after publishing 1.10.3. Release module binaries embed the git commit that was checked out when they were built, as the "+<commit>" suffix of their product version. Installed acceptance runs before the source commit by design, so the shipped 1.10.3 `AgentBroker.dll`, `AgentBroker.Worker.dll` and `AgentBroker.Worker.exe` name the 1.10.2 base commit `34f73e81`, not their own source commit `f5916c34`; 1.10.2 shipped the same way. A release rebuild of the published source with the source revision pinned to `34f73e81` is byte-identical to all three shipped binaries, so the content is right and only the label misleads. Options: stamp the frozen source identity (for example the F2 freeze hash, or the base commit with a marker that uncommitted changes were built) instead of the checked-out commit, or rebuild after the source commit under a re-zip identity check that allows only the version label to differ. Until then the release ledger records which source each published payload was built from.
<!-- AGENTBROKER1104 ORIGINAL END N-broker-release-provenance-01 -->

<!-- AGENTBROKER1104 FIXED BEGIN N-broker-release-provenance-01 -->
Disposition: FIXED
Date: 2026-11-11
Code: packaging/AgentBrokerReleaseProvenance.ps1, AgentBrokerReleaseProvenance.props, AgentBrokerReleaseOutput.ps1 and AgentBrokerReleasePayloadMetadata.cs bind frozen source/parent, fresh stamped output, tool authority and exact ZIP bytes; tests/Test-ModulePublishFreshnessRules.ps1 checks committed-blob freshness.
Killing controls: artifact43 case metadata-source-mismatch -> RELEASE_SOURCE_CONTENT_MATCH; metadata-parent-mismatch -> RELEASE_PARENT_COMMIT_MATCH; output-changed -> PROVENANCE_OUTPUT_CHANGED; zip-known-entry-changed -> PROVENANCE_ZIP_CHANGED; new-modulepublish-old-authority-refused -> PROVENANCE_OUTPUT_AUTHORITY_CHANGED. Committed-blob stamp, stale-byte and rebuild-remedy controls are retained in the qualified freshness C37/A-D cohorts.
Accepted outcome: PASS accepted stamped ZIP equals all 357 installed files; source identity and parent replace a misleading checked-out Git suffix. Actual source F2 and publication verification remain required before push.
Acceptance SHA256: 8D642479149718B68C165AFD88570B1B1EEAA68E064A6EB97F217EA6FE3CB7AE
<!-- AGENTBROKER1104 FIXED END N-broker-release-provenance-01 -->

## N-broker-selftest-token-path-01

<!-- AGENTBROKER1104 ORIGINAL BEGIN N-broker-selftest-token-path-01 -->
- 📌 **N-broker-selftest-token-path-01** Found 2026-11-10 by the closure controls of the agentbroker-next campaign, outside its inventory. The AgentBroker module self-test check "client setup adds typed stdio arguments without persisting connection tokens" (modules/AgentBroker/AgentBrokerSelfTest.cs:186-189) proves that no token is persisted by scanning every add argument for the substring "token", ignoring case. The worker path and the data directory are arguments and both live under TEMP, so the check fails on a healthy build whenever the TEMP path contains "token", for example in an account folder or a test run directory named with that word. Three closure controls whose names contained "token" failed it for that reason alone; the failed first run and its attribution are recorded with N-broker-next-http-auth-01 in the 2026-11-08 ledger. Remedy: assert the exact codex add argument sequence, as the Claude check beside it does with SequenceEqual, then mutation-test the tightened check by adding a token-bearing argument. The check compiles into the shipped module, so the fix ships with the next module release.
<!-- AGENTBROKER1104 ORIGINAL END N-broker-selftest-token-path-01 -->

<!-- AGENTBROKER1104 FIXED BEGIN N-broker-selftest-token-path-01 -->
Disposition: FIXED
Date: 2026-11-11
Code: modules/AgentBroker/AgentBrokerSelfTest.cs asserts the exact typed Codex add-argument sequence instead of rejecting a valid path containing the substring token.
Killing controls: the token-bearing add-argument mutation fails exactly "client setup adds typed stdio arguments without persisting connection tokens" through the real Host; healthy/restored module 520 and changed loaded module identity are retained in token-v5 receipt 5B930DD1.
Accepted outcome: PASS the fixed module bytes are in the accepted and installed 357-file payload; healthy token-path module 520 and its named poison/native 1/restored control passed. The compiled check is not described as 520 individually emitted negative assertions.
Acceptance SHA256: 8D642479149718B68C165AFD88570B1B1EEAA68E064A6EB97F217EA6FE3CB7AE
<!-- AGENTBROKER1104 FIXED END N-broker-selftest-token-path-01 -->

## N-broker-test-timing-fragility-01

<!-- AGENTBROKER1104 ORIGINAL BEGIN N-broker-test-timing-fragility-01 -->
- 📌 **N-broker-test-timing-fragility-01** Found 2026-11-10 by the closure gates of the agentbroker-next campaign, outside its inventory: two test-only defects, each of which failed one full gate on unchanged code while other builds and tests ran on the box. (1) WAIT_LIFECYCLE_QUIET_NO_POLL (tests/AgentBroker.Worker.Tests/WaitLifecycleChecks.cs:66-82) requires exactly two pending reads in a quiet four-second wait, but BrokerTools.WaitAsync compares its deadline on Environment.TickCount64, which steps about every 16 ms, while its timeout timer runs on a different clock. A timer that fires just before the tick moves costs one extra pending read and a re-sleep of at most about 17 ms; the product effect is only that late, empty result. The check records no values, so the failing run cannot show whether it read 3 (the likely case) or 1. Fix: record each read's elapsed time, accept 2 or 3 reads with the later ones at the deadline, keep the release checks strict, and mutation-test it with a shortened sleep. (2) The collaboration helper process contract (tests/Test-AgentBrokerCollaborationMcpProcess.ps1:46-48) polls until the fixture's ready file exists and then reads it at once, so it can hit a sharing violation while the Node fixture still holds the file open. Fix: write the ready file atomically (a temporary file and a rename) or retry the read on a sharing violation.
<!-- AGENTBROKER1104 ORIGINAL END N-broker-test-timing-fragility-01 -->

<!-- AGENTBROKER1104 FIXED BEGIN N-broker-test-timing-fragility-01 -->
Disposition: FIXED
Date: 2026-11-11
Code: tools/AgentBroker.Worker/BrokerTools.cs uses one request TimeProvider for budget and timer; tests/AgentBroker.Worker.Tests/WaitLifecycleChecks.cs controls that dependency and names incomplete waits; tests/agentbroker-collaboration-process-fixture.mjs publishes closed ready files by rename, with bounded retry and sanitized consumer diagnostics.
Killing controls: product-poll-3000ms -> WAIT_LIFECYCLE_QUIET_NO_POLL; actual-fixture-early-timer -> QUIET_CLOCK_NOT_BEFORE_DUE; mixed-clock-elapsed-only -> WAIT_LIFECYCLE_QUIET_NO_POLL; early-publication -> READY_NOT_VISIBLE_UNTIL_CLOSED; transient-no-retry -> READY_TRANSIENT_EPERM_RECOVERS; persistent-no-classification -> READY_PERSISTENT_CONSUMER_DIAGNOSTIC.
Accepted outcome: PASS named wait/readiness negatives and restored healthy 28/clock 10 passed; the mixed-clock target was freshly built and loaded, failed native 1 after nine passes, then restored exact source/artifacts and healthy 28. Installed client acceptance passed within its recorded scope. No real scanner-interference cause or universal off-clock-poller coverage is claimed.
Acceptance SHA256: 8D642479149718B68C165AFD88570B1B1EEAA68E064A6EB97F217EA6FE3CB7AE
<!-- AGENTBROKER1104 FIXED END N-broker-test-timing-fragility-01 -->

## N-broker-future-package-privacy-01

<!-- AGENTBROKER1104 ORIGINAL BEGIN N-broker-future-package-privacy-01 -->
- 📌 **N-broker-future-package-privacy-01** The owner chose to preserve published history and fix future packages after local build paths were found in published SourceLink metadata. The 1.10.4 candidate adds bound AgentBroker provenance and privacy checks for module/app release routes; it is not installed, committed or published yet. The official NuGet author-package route is separate: `packaging/New-NuGetPackages.ps1` invokes plain `dotnet pack` for Contracts and ModuleKit, whose projects embed PDBs, and both official workflows publish its outputs. The module publisher's command-line privacy switches do not govern this pack build. This is a source-proven uncovered route, not a newly measured exposure. Close only after the official future routes suppress SourceLink and source embedding, inspect their actual new artifacts without printing private paths or source text, and have named failing controls. Preserve all historical release bytes and record the final evidence and disposition before removing this item.
<!-- AGENTBROKER1104 ORIGINAL END N-broker-future-package-privacy-01 -->

<!-- AGENTBROKER1104 FIXED BEGIN N-broker-future-package-privacy-01 -->
Disposition: FIXED
Date: 2026-11-11
Code: packaging/New-ModulePublish.ps1 and AgentBrokerReleasePayloadMetadata.cs suppress/check future module metadata; packaging/New-NuGetPackages.ps1, NuGetPackagePrivacy.ps1 and EmbeddedPortablePdb.ps1 bind fresh official library images and reject private/source/symbol artifacts, unread extra binaries and unexpected pushable packages. tests/run-gate.ps1:230-231 invokes Test-NuGetPackagePrivacyRules.ps1 and collects its failure; .github/workflows/build.yml:301-302 and release.yml:199-200 invoke that rule script immediately before New-NuGetPackages.ps1.
Killing controls: actual SourceLink and EmbeddedSource property variants fail named Contracts/ModuleKit privacy checks; F950's 13 guard/kind controls and gate/workflow propagation pass. N1_GUARD_REMOVED -> EXTRA_BINARY_OTHER_TFM; N2_COUNT_REMOVED -> PACKAGE_SET_EXTRA; WIRING_CALL_CONDITIONAL -> PACKAGE_SET_WIRED_BEFORE_PUSH_BANNER, with all 12 N1/N2 negatives independently reconciled.
Accepted outcome: PASS actual official Contracts/ModuleKit packages and fresh image bindings have one embedded PDB each, 3/15 documents and zero absolute/SourceLink/embedded-source offenders. Both-shell rules 35 and actual metadata revalidation passed; accepted installed module ZIP passed its four-value F5 privacy scan with zero hits. Historical release bytes remain unchanged.
Acceptance SHA256: 8D642479149718B68C165AFD88570B1B1EEAA68E064A6EB97F217EA6FE3CB7AE
<!-- AGENTBROKER1104 FIXED END N-broker-future-package-privacy-01 -->

## Qualification and scope decisions

Artifact 43 independently reconciled 43 actual-function cases and the 357-file output inventory; caught in-process exceptions are not relabeled as native 1 transcripts. Receipt `FB1D77B0` and original output receipt `B7F17BCA` bind that scope. Token control `5B930DD1` has healthy 0/build 0/poison 1/restored 0 captures and an independent retained-record review. Freshness C37 covers all 37 controls exactly once across the documented 16/21 cleanup revisions, plus final dual-shell 48; A/D integration preserves the successful scopes and original HOLDs separately. No old full mutation catalogue was replayed.

The wait mixed-clock archive `B5A23F94` and independent proof `E22C3EF5` close L04's missing named-control proof: two 0 ms timing diagnostics meet its below 4000 ms premise, loaded Worker bytes changed, and source/artifacts restored. N1/N2 archive `34AB507A`, actual identity map `8F4A0917` and independent proof `DE498E6C` reconcile 17 raw captures and all 12 mutations. That caller's Name/basename collision remains unqualified metadata; identity comes from the unique actual directory, target argument/hash, tested helper, PID and raw hashes. Its original records were not renamed or rewritten. Claude's deleted initial 0/12 parser record remains unavailable, not retained evidence.

The composed public source-invariant gate reported 1097 PASS lines under each host shell with unchanged 1173-file roster; ordinary freshness 48 and NuGet 35 passed in both shells. The RA190 workflow-order repair has meaningful negative controls. Candidate F5 reported four private values, 357 entries and zero hits with independent review; private-input handling is peer-attested, not a retained OS process capture. Exact-range F4 still belongs to the final local commit composition. The light tier omits whole-catalogue mutation runs and leak soaks.

These are explicit engineering scope decisions, not a claim that every review finding or the global backlog is closed:

| Review IDs | Disposition and remaining limit |
|---|---|
| B01 | Installed observation completed, but no supported deterministic safe trigger/attribution for deliberate pending-reconnect preparation was established. Normal StageUpdate reconnects inline. The quiet sample does not exercise that path. |
| B02 | LIMITATION RECORDED: production reconnect duration R was not measured; a healthy lease sample does not measure it or establish the old gap's cause. |
| B25 | LIMITATIONS RECORDED individually below; preserve the qualified compiled/runtime baseline rather than a blanket fixed claim. |
| L02 | LIMITATION RECORDED: the System request path keeps the reviewed two-clock observation and extra elapsed-budget read; no further clock-unification or production timing measurement. |
| L03 | DEFERRED COVERAGE under the OPEN native lease-gap incident: fake-time controls do not kill every independent ordinary-clock periodic poller. |
| L04 | PROOF COMPLETED: actual mixed-clock named failure, fresh loaded target and exact restored healthy 28 independently reviewed. Does not close L03. |
| L12 | LIMITATION RECORDED: the thin lease margin near the reconnect cap existed at the base; not eliminated or measured as production R here. |
| L13 | DOCUMENTATION DEBT RECORDED: compiled runtime/provider comments incompletely state the preparation window. Their bytes remain bound to the accepted identity; a later bound source release must correct them. |
| L16 | COVERAGE LIMIT RECORDED: a gate-leak mutation can fail in disposal or time out before the desired dedicated assertion. Other shutdown controls do not establish that attribution. |
| L17 | COVERAGE LIMIT RECORDED: the reorder control can falsely survive timer starvation; an ordinary pass proves no scheduling immunity. |
| L18 | COVERAGE LIMIT RECORDED: preparation only after a consumed pass has no qualified dedicated killing control. |
| L20 | COVERAGE LIMIT RECORDED: background reconnect fault/cancellation during preparation has no dedicated qualified fixture; the two catch mutations are not claimed killed. |
| L21 | COVERAGE LIMIT RECORDED: moving cadence start after preparation has no dedicated control; distinguish the qualified query-deadline-placement control. |
| L22 | COVERAGE LIMIT RECORDED: injected provider/gate fixtures do not pin production constructor independence; source inspection supports current wiring. |
| L34 | Same constructor-shape limitation as L22, retained by its own ID; no constructor killing test or production seam added. |
| L33 | TIMING LIMIT RECORDED: the absolute wall-time bound is scheduling-sensitive, not production latency/load evidence. |
| L37 | LABEL LIMIT RECORDED: display WITNESS and assertion labels differ; grade the actual assertion. |
| B11 | VALIDATION GAP RETAINED: existing inventory 801 was measured and its stale comment corrected, but the public Python --apply healthy route was not newly qualified. It has no zero-mutant selection; no unfiltered catalogue run was performed. |
| L14 | DOCUMENTED: the design register appends current discovery/delivery separation and preparation ownership without deleting the old N-broker-161-01 refusal. Not installed preparation-path proof. |
| L24 | PROCEDURE RECORDED: first provisional ZIP needs an absent destination; repeats/repackages/canonical copies retain the original expected hash. The zipper still replaces a destination; no new no-overwrite guard is claimed. |
| L31 | DOCUMENTED: the release-order guide is applied; its replacement-policy scope remains as stated for L24. |
| NuGet N3 | NONBLOCKING LIMIT RECORDED: exact one-line TargetPath stdout parsing fails closed; no new SDK query mechanism claimed. |
| NuGet N4 | DEFERRED test-cleanup proposal, not applied or qualified in this batch. The measured sharing violation did not identify a scanner/indexer cause. |

## Open work and final release binding

N-broker-native-lease-gap-01 remains OPEN. Corrected docs distinguish broker addressability from a vendor chat remaining open; synthetic cadence/preparation controls and a healthy 43.368-second sample do not establish historical cause, continuous presence or a fix through controlled load. N-broker-standby-response-shape-01 remains OPEN: local helper compatibility and unchanged advisory kinds are narrower than a repository-enforced unknown-kind contract and real generic mailbox/heartbeat startup acceptance.

The no-drop packet captures original BACKLOG SHA `0A82DEFB3EE8F68EEA03341AA287E10FE76C1BC0CD27DC292CF6F08600DFB95D` and exact projected final BACKLOG `5DB556895DD718D0C02D1AD4575BC17F40270AA0859EAC3831E0991306580E7C`. Its audit allows only these four exact block removals and one pinned dated status paragraph; every other backlog byte, header and historical note must remain. Public original-body comparison permits CRLF-to-LF only. Four E-only controls fail by name for a deleted closed body, deleted unrelated open body, missing FIXED field and invalid disposition; healthy/final structural checks pass in PowerShell 5.1 and 7. The old 38 campaign ledger is separately hash-pinned and untouched. Final no-drop/receipt binding and Root's semantic evidence review are prerequisites for BACKLOG removal.

Release completion requires the final exact-source F2 postverify, publication from the identical app-accepted ZIP, source/payload/catalog Git and receipt bindings, exact-range F4, then pushed-head CI and a full published-delta audit. Retained rollback and private evidence remain until coordinated cleanup. This record makes no push, CI, publication, final F2 or empty-global-backlog claim.

