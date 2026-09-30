# Desktop AI Companion — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

## How this file works

**Open items only.** A closed item is deleted from here rather than marked done — git holds the
history, and 2,560 lines of finished work is what stopped this file being readable as a backlog.
Anything closed that still carries standing value was extracted rather than deleted:

| file | what it holds |
|---|---|
| [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) | closed knowledge written to be CONSULTED: refused designs, the two noted-not-scheduled ABI gaps, behaviours that read as defects and are not, the closed bug log |
| [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) | the completed-work record from v1.0.0 on, with the estimates that turned out wrong kept beside their corrections |
| [`docs/ISSUES-post-1.0.0.md`](docs/ISSUES-post-1.0.0.md) | every closed bug post-mortem, kept in full because several were wrong in instructive ways. It is the file that says how many there are; do not restate the count here |
| [`docs/BLOCKED.md`](docs/BLOCKED.md) | items that cannot be actioned from here, each with its blocker named on its own line |
| [`docs/IDEAS.md`](docs/IDEAS.md) | product ideas nobody has scoped, with their reasoning: wanted, unscheduled, and not engineering debt |
| [`docs/HISTORY-pre-1.0.0.md`](docs/HISTORY-pre-1.0.0.md), [`docs/ISSUES-pre-1.0.0.md`](docs/ISSUES-pre-1.0.0.md) | the same two records for the repository that preceded v1.0.0 |

**Conventions.**

- **Bugs are numbered `BUG-00N` and the number is never reused**, so a commit, a test or a code
  comment can cite one. `modules/AiBrain/`, `modules/PetStudio/`, `src/dotNet/`,
  `docs/RELEASE-CHECKLIST.md` and `handoff.md` all cite them today. **The next number to use is the
  one [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) names**, which is the single place it is
  written down; the gate asserts the two agree, because this line said BUG-005 for weeks after the
  register had moved on to BUG-009 and nothing noticed.
  **No bug is open right now** — the closed register moved to
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md), because "none open" is not a TODO.
- **Status is prose plus a glyph, never a checkbox:** ✅ done · 📌 open, with the reasoning recorded ·
  ⬜ not started · ⚠ a caveat, or a claim that has not been observed. There are no markdown
  checkboxes anywhere in this file, and adding one would lose the reasoning a glyph sits next to.
- **An entry keeps the measurement that settled it.** Where a number decided something, the number
  stays in the entry — otherwise the next reader re-derives it, or guesses. **A number that merely
  DESCRIBES the repo is the opposite case: it goes stale and nobody notices.** Say what to count and
  where, or make the code count it — see "Numbers in documentation" in
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md), which exists because one such figure was
  wrong three times running.
- **Before writing "needs a host change" here again, grep `PluginApi.cs` for the verb.** That
  sentence cost a planning cycle: the Reminder entry asserted the ABI could not drive a companion
  animation or move a companion, while `IHost.TryPlayAnimation` and `IHost.PlayAnimationAll` had
  existed since the emotion work and AiBrain had been using them all along. The full case is in
  [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).
- **A refused design is recorded, not forgotten.** Read the "Settled decisions" section of
  [`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) before proposing something that looks
  obviously missing — and its "Decisions that read as defects" before filing one as a bug.

---

## Open: 2026-09-29 full audit (447 verified findings, filed by lane)

The full code audit of 2026-09-29 (report in [`docs/audits/2026-09-29-full-audit.md`](docs/audits/2026-09-29-full-audit.md), every finding with its
verifiers' reasoning in [`docs/audits/2026-09-29-findings.csv`](docs/audits/2026-09-29-findings.csv)) verified 447 of 462 candidate findings against the code at
commit `35725396` with two independent readers per medium-and-up item and a judge on disagreement. Each line below carries its audit id (F-nnn),
the verified severity and category, and the location at that commit. Lines are grouped by the lane that owns the fix so lanes edit disjoint blocks.
An item closes in place with one of four dispositions, each checkable: **FIXED** (code plus a test naming its killing mutation), **CLOSED-VERIFIED**
(already true in the code; the line cited), **DECLINED-MEASURED** (a number, recorded in the design register), **ACCEPTED-RECORDED** (an owner
decision or an observation, recorded in the register). Info items are observations; they are dispositioned, not fixed, unless the fix is trivial
and adjacent to one already being made. The four high items are also BUG-009 to BUG-012 in `docs/ISSUES-post-1.0.0.md`.

### Lane fix/gates (Phase 1): checks that cannot fail, harness hygiene

61 items: 56 to fix or disposition, 5 info to disposition.

- ✅ **F114** **FIXED 2026-09-29.** `ScrollLockBlinker` counts every toggle attempt (`AttemptCount`) and reads the key through an injectable `ScrollLockReader`, so the module self-test drives Stop()'s corrective toggle for real: a blinker that lit the key and reads it lit attempts the toggle; WITNESS a key that does not read lit is left alone even when the blinker believes it lit it; WITNESS a blinker that never lit the key attempts nothing. MUTATION: "Stop()'s corrective toggle block is deleted" FIRED (`Stop() attempts the corrective toggle when it lit the key and the key reads lit`); "Stop() toggles without reading the key first" FIRED (the first WITNESS).
- ✅ **F149** **FIXED 2026-09-29.** `FortunesModule.RebuildSmartIndexAsync` compares the indexed pool's signature with a FRESHLY built `FortuneProvider(LoadFortuneSettings(_host))`, not with `provider.PoolEntries()`, the list the signature was computed from, so a pack dropped into the folder makes the button rebuild instead of reporting "already built"; the runtime suites cannot reach the guard (it needs a complete warm), so a source invariant under `# ---- lane fix/gates ----` slices the method and asserts the argument (`PoolSignature(fresh.PoolEntries())` present, `PoolSignature(provider.PoolEntries())` absent). MUTATION (tests/mutate-hardening-guards.py): "'Rebuild smart index' compares the pool with itself again" FIRED.
- ✅ **F287** **FIXED 2026-09-29.** The `MaximumFiles` cap the catalog self-test reads is the host's; the one that governs LOADING is the Fortunes module's own copy, which the host cannot see. Three source invariants under `# ---- lane fix/gates ----` in tests/runtime-hardening-selftest.ps1 parse both caps and the catalog entry cap as NUMBERS from comment-stripped source and assert presence, module == host, and module >= catalog entries; the check's SCOPE is stated in a comment at the RemoteCatalog site. MUTATION (tests/mutate-hardening-guards.py): "the Fortunes module's pack file cap drops to 128" FIRED (`equals the host copy`).
- ✅ **F382** **FIXED 2026-09-29.** `tests/DesktopAICompanion.CoreTests/ModuleKitTests.cs` binds AtomicFile and UnicodeTextProgress through `using KitAtomicFile = DesktopAICompanion.ModuleKit.AtomicFile;` (and the unicode twin) aliases, because C# simple-name resolution had picked the production twins compiled into CoreTests, and the atomic, unicode and kit groups now assert each type's assembly is `DesktopAICompanion.ModuleKit`. MUTATION (tests/mutate-selftest-guards.py, CoreTests target): "ModuleKit's AtomicFile writes a UTF-8 BOM" FIRED (`FAIL: ModuleKit atomic file writes: The atomic write emitted a UTF-8 BOM.`); "ModuleKit's UnicodeTextProgress truncates through a surrogate pair" FIRED (`Expected 'A', got 'A?'`). Before the aliases both mutations were invisible to CoreTests.
- ✅ **F384** **FIXED 2026-09-29.** The corrupt-primary group in `tests/DesktopAICompanion.CoreTests/Program.cs` saves twice (Volume 0.8, then 0.9) so the backup holds a NON-default value, WITNESSes that the backup holds 0.8, and asserts `recovered.Volume == 0.8`, the primary rewritten to 0.8 and the backup untouched. MUTATION: "the settings store never consults its backup" FIRED (`FAIL: Settings corrupt-primary recovery: The previous valid backup was not recovered (0.3 would mean the store fell back to defaults)`).
- ✅ **F403** **FIXED 2026-09-29.** `tests/mutate-diagnostics.py run_wpf(expect_rebuild)` deletes the previous marker before the run and refuses (ok=None, reported as BROKEN, exit 2 for the baseline) when the mutated exe was not rebuilt (mtime did not advance), the run does not exit within 900 s, no marker was written, or the marker's column-0 verdict disagrees with the exit code; the marker lives in a private per-run TEMP (`dp-mdg-` + 12 hex) handed to the exe through its environment. Verified with a faked `subprocess.run` (scratch `run_wpf_check.py`): dead exe with a stale PASS marker -> REFUSED, timeout -> REFUSED, PASS marker with exit 1 -> REFUSED, and the two WITNESSES (real PASS graded True, real FAIL graded False), 5/5.
- ✅ **F445** **FIXED 2026-09-29.** `BundleSelfTest` part 3 decodes a 40-byte lossless WebP with alpha (`TinyWebPWithAlpha`, generated and dwebp-verified: corner alpha 0, a 2x2 opaque rgb(200,40,40) block) through `WebPLoader.Decode`, which is the dwebp path with both timeouts, and asserts the decoded corner is transparent and pixel (1,1) is opaque (200,40,40); the header comment now says what routes through dwebp and what does not. MUTATION, by hand: `native\dwebp.exe` renamed away -> `FAIL WebP decode through dwebp threw -- dwebp.exe (the bundled WebP decoder) was not found next to the converter ... Expected it at native\dwebp.exe.`, SELFTEST FAIL, exit 1; restored -> SELFTEST PASS.
- ✅ **F008** **FIXED 2026-09-29.** `docs/agentflow/agentflow_classifier.py --mutate` runs the whole suite ONCE before any mutation and refuses to score against a red baseline (`BASELINE NOT GREEN (n/25): refusing to score mutations against a red suite`, exit 2, the failing labels listed); a mutation counts as FIRED only for a label that was passing at baseline. MUTATION, by hand: `"yes": APPROVE_ONCE` -> `APPROVE_WIDER` in the table, then `--mutate`: `BASELINE NOT GREEN (19/25)`, exit 2, no FIRED line printed; restored byte-identical, `--mutate` again 5/5 FIRED over a 25/25 baseline.
- ✅ **F014** **FIXED 2026-09-29.** `docs/agentflow/agentflow_headers.py --selftest` keeps "a missing bundle is not a failure" for the missing-bundle return (0), and a NEW WITNESS writes an `index.js` with no render sites into a temp tree in the two-level `<name>-<version>/webview/` shape and points `audit()` at it through the same glob, so the `MIN_PLAUSIBLE_SITES` branch is what returns 1; `tempfile`/`shutil` imported, the tree removed in `finally`. MUTATION, by hand: `MIN_PLAUSIBLE_SITES = -1` -> `FAIL: WITNESS a bundle that yields nothing is a FAILURE, not a pass` (20/21) while the missing-bundle line stays PASS; restored -> 21/21.
- ✅ **F023** **FIXED 2026-09-29.** `installer/New-RuntimeWixFragment.ps1` passes `-TrustedRoot $repoRoot` (not the destination's own parent, below which a path always sits) to `Assert-DesktopAICompanionOutputFileSafe` for the output-file check and the publish parameters, with the comment naming the two scripts repaired the same way on 2026-09-24. Verified by hand: `-OutputPath` under the lane's TEMP -> `Output file must be strictly below trusted root 'D:\...\fix-gates': ...outside-the-repo.wxs`, exit 1, no file created; the only caller (build-installer.ps1) writes under build\, so no live path changes.
- ✅ **F039** **FIXED 2026-09-29.** `AgentFlowModule.SelfTest` runs its groups from a `Func<SelfTestProbe, bool>[]` (short-circuiting on the first false, as the && chain did) and the witness compares the SET of wired `Method.Name`s with the set of `SelfCheck*` methods declared on the type (`DeclaredSelfCheckMethodNames`), naming anything declared-but-unwired or wired-but-undeclared; the count and its constant are gone. MUTATION (tests/mutate-agentflow.py): "a SelfCheck group is dropped from the wired run" FIRED (`FAIL: WITNESS every SelfCheck group declared on this type is wired into the run above, and nothing else is (39 wired, 40 declared; declared but NOT wired: SelfCheckSplitter)`).
- ✅ **F040** **FIXED 2026-09-29.** `SelfCheckApprover` drives a `blind` target per agent (Claude Code and Codex) through the five-argument `CdpApprover.Sweep`, asserting `sawBlind`, that nothing was pressed, that the card still counts as a panel that was READ (`sawPanel`), and that the note names the agent and says nothing was pressed; a `{"options":[]}` card (the Parse-empty arm) is driven too. MUTATION (tests/mutate-agentflow.py): "a blind card is no longer reported through sawBlind" FIRED (`FAIL: WIRE a blind Claude Code card is reported through sawBlind and presses nothing`); "the blind note stops naming the Codex agent" FIRED (`FAIL: WIRE the blind note names the Codex agent and says nothing was pressed`).
- ✅ **F074** **FIXED 2026-09-29** (low, reasoning fix). `AiBrain.MaximumCapturePixels` is now DEFINED as `(long)MaximumCaptureWidth * MaximumCaptureHeight`, so the relation the unreachable guard was checking holds by construction, and the guard is a `Debug.Assert` that says why it cannot fire in release (both dimensions are clamped before it). No runtime check to mutate: the change removes a check that could not fail rather than adding one.
- ✅ **F083** **FIXED 2026-09-29.** The DPAPI branch of `AiEngineProbe.Run` is a FAIL naming the check that could not run and why (`DPAPI key store available, so the API-key round-trip can be asserted (<error>)`), not a `SKIP:` that three RunSecurity checks and Invoke-SelfTests.ps1 refused anyway. MUTATION, by hand: `TrySetApiKey(...) && false; setError = "simulated: DPAPI unavailable"` -> `FAIL: DPAPI key store available, so the API-key round-trip can be asserted (simulated: DPAPI unavailable)`, RESULT=FAIL; restored -> PASS.
- ✅ **F084** **FIXED 2026-09-29.** `AiBrain.RemarksQuotedInPrompt = 4` is the one constant `DescribeAlreadySaid` reads (`DescribeAlreadySaidForDiagnostics` exposes it); the probe asserts the last audition prompt quotes exactly `min(4, asks-1)` bullets (`CountOccurrences` of the bullet prefix, not an IndexOf that proved >= 1), and the builder ages out the oldest remark when handed more than it quotes; the `RecentRemarkPromptRecall` doc now says 4. MUTATION (tests/mutate-selftest-guards.py): "the already-said list quotes every remark" FIRED (`FAIL: the already-said list is bounded, not a growing transcript`).
- ✅ **F089** **FIXED 2026-09-29.** `RetirementTrackingBackend` counts `UnloadAsync` calls that arrive after its Dispose (`UnloadCallsAfterDispose`), and the deferred-dispose check requires exactly one, with its label saying what that means (the unload is RECORDED, not an eviction, because the real OllamaClient would have dropped it as ObjectDisposedException; that product-side question is F091). MUTATION, by hand: the retire path's `brain.UnloadAsync(...)` replaced by a completed task -> `FAIL: deferred dispose drains pending after-retire actions (...)` (and the two sibling retire checks, which also count the unload); restored -> RESULT=PASS.
- ✅ **F111** **FIXED 2026-09-29.** `BlinkingLedModule.SelfTest`'s permission check includes `ModulePermissions.InputSynthesis`, the disclosure the 2026-09-17 change existed for. MUTATION: "BlinkingLed drops the InputSynthesis disclosure" FIRED (`[blinkingled] FAIL: declares the permissions it uses, including the InputSynthesis disclosure`).
- ✅ **F134** **FIXED 2026-09-29.** `FortuneProvider` counts every real parse of the writable folder (`_customParses`, incremented inside the lock; `CustomParsesForDiagnostics`), and `CustomCacheSelfTest` asserts the property it is named for: two more reads of an unchanged folder do not move the counter, while the add, the edit and the remove each re-parse exactly once (measured relative to the count just before each). MUTATION (tests/mutate-selftest-guards.py): "the custom-corpus cache never caches (a fresh fingerprint on every read)" FIRED (`FAIL: an unchanged folder is served from the cache (two more reads, no re-parse)`), exactly one failure, while the older "never invalidates" case still FIRED on its own line.
- ✅ **F139** **FIXED 2026-09-29.** `dispose_during_warm` reads observables instead of predicates DisposeCore's unconditional reset made true: `WarmTaskStartedForDiagnostics`, `EmbedderDisposedOnceForDiagnostics` and `WarmTaskCompletedForDiagnostics`, polled within a bound. That case still disposes INLINE (the cancelled warm ends inside the 3 s budget), so a second case, `dispose_handoff`, forces the handoff the finding is about: `DisposeWithin(TimeSpan.Zero)` while the warm runs, a new `QueuedEmbedderDisposalsForDiagnostics` counter proving the queued path was taken (`queued=1`), and the embedder's disposal observed from the continuation. MUTATION, by hand: the continuation's `DisposeEmbedder()` removed -> `dispose_handoff=FAIL warm_started=True queued=1 embedder_disposed_once=False ... settle_ms=13003` while `dispose_during_warm` stayed PASS (which is exactly why the first case alone could not see it); restored -> both PASS (`dispose_handoff=PASS ... queued=1 ... settle_ms=232`).
- ✅ **F140** **FIXED 2026-09-29.** `simulated_day` is replayable and models the module: `SmartFortunes.SeedForDiagnostics(seed)` reseeds the picker's RNG AND clears its recent set and last-picked line (a seed alone was not enough: the picks made during the warm and in the band-width sweep left a different history each run), `FortuneProvider.SeedForDiagnostics` seeds the random fallback, the fallback draws from the SAME 1500-line pool the smart picker indexed (`dayProvider = new FortuneProvider(pool, ...)`), and every fallback line is fed back through `NoteExternallyShown` as `FortunesModule.SpeakFortune` does. Measured: three consecutive `--fortunes-smart-progress-selftest` runs print identical lines (`simulated_day devenv picks=200 distinct=199 worst_repeat=2 from_smart=146`, `excel ... distinct=196 ...`); before the history reset two runs printed 199/194 and 197/193. No harness case: the diagnostic asserts a threshold (`worst > 2`) that a mutation of the picker would move only stochastically, so what is pinned is the replay itself.
- ✅ **F151** **ACCEPTED-RECORDED 2026-09-29.** Deferred to F150 in lane fix/petstudio, deliberately: the named label assertions that would make `AgreesWithTheFixture` bite FAIL on the classifier as it stands (the finding's verifier measured `top_walk2` Move, `vertical_walk_down` Idle, `boing` Climb), so they must land with the growth-loop fix, not before it; the identity check stays until that lane touches the file. Reasoning under `#### fix/gates` in docs/DESIGN-REGISTER.md.
- ✅ **F153** **FIXED 2026-09-29.** `ClassifyIsHonest` now searches for a FLAGGED border-only edge (Only neither empty nor "none", the node's only border edge to that target), fails if the fixture has none, WITNESSes the literal (walk #1 -> vertical_walk_up #37, only="vertical") and asserts `Classify(...).Only` equals the analyzer's edge flag, printing expected and got; `FindEdgeOnly`, which re-implemented Classify's pick, is gone. Runs through both `--petstudio-selftest` and the module's new `SelfTest`. MUTATION (tests/mutate-selftest-guards.py, via `--module-selftest=petstudio`): "BehaviourChain.Classify flattens every only= flag to the default" FIRED (`[BehaviourChainSelfCheck] the border join carries the edge's only= flag through (expected 'vertical', got 'none')`).
- ✅ **F194** **FIXED 2026-09-29.** `PersonalReminderParser.SelfCheck` exercises the `at` branch and the bare HH:mm form against a fixed local now (2026-08-26 10:00): `at 15:00 Call the vet` -> 15:00 today, `at 09:00 Early` -> tomorrow, `07:30 Gym` -> tomorrow, `at soon Call the vet` rejected. MUTATION: "the reminder parser loses its 'at' branch" FIRED (`[reminder] FAIL: PersonalReminderParser`).
- ✅ **F205** **FIXED 2026-09-29.** A `ThreadRecordingProbe : CachingCalendarSource` gates its read and records the thread it ran on, and the module self-test asserts `Fetch returns before the read completes (the read is not on the caller's thread)`, WITNESS `the background read did start`, and `the read ran on a different thread from the caller`, instead of pinning that LocalJsonSource derives from the base. MUTATION: "the calendar feed is read inline on the caller's thread" FIRED.
- ✅ **F219** **FIXED 2026-09-29.** `packaging/Test-AtomicPublish.ps1` gains cases 6 (destination is a protected directory), 7 (temporary and destination are the same file) and 8 (staged file missing), and its header says what is exercised; 8/8 refused. MUTATION, by hand on `Publish-DesktopAICompanionAtomicFile`: same-path throw removed -> `temporary and destination are the same file  PUBLISHED ANYWAY -- the check does not fire`, `7/8 refused`, exit 1; missing-temp throw removed -> `a staged file that is missing  THREW SOMETHING ELSE`, `7/8 refused`, exit 1. Source restored byte-identical each time; restored run 8/8.
- ✅ **F221** **FIXED 2026-09-29.** `packaging/Test-ContentCatalogIntegrity.ps1` throws when `packs\collections.json` is missing instead of silently skipping the catalog-asset timeout probe (path derived from `$RepoRoot`), and `packaging/Test-ModulePublishFreshness.ps1` throws when `packs\collections.json` or `modules-dist\fortunes.zip` is absent instead of skipping the pack-to-collection content check. Verified by hand: with `collections.json` renamed away, Integrity exits 1 with `packs\collections.json is missing at ..., so the catalog-asset timeout path cannot be exercised.`; restored -> `Catalog integrity OK: 219 asset(s) ... 54 companions and 158 packs`.
- ✅ **F233** **FIXED 2026-09-29.** `RecordingHost` keeps priority-ordered responder chains (`Responder {Priority, Seq, OnFire}`, sorted priority-descending with registration order on ties; legacy registrations are wrapped) and `RaiseDrop`/`RaisePokeResponders` walk the chain the way the host does, over a copy. CoreTests `TestModuleKitRecordingHost` drives it: legacy@0 vs pet-aware@10 runs pet-aware first; a tie keeps registration order. MUTATION: "RecordingHost stops sorting its responder chains by priority" FIRED (`The higher-priority pet-aware responder did not run before the lower-priority legacy one`).
- ✅ **F234** **FIXED 2026-09-29.** `RecordingHost.Declared` now gates `RaiseSpeechRequest` (Voice), `GetCompanionManager` (Companions: hands a `DenyingCompanionManager`) and `OpenLink` (Network: returns false before recording), with the doc widened to say so. CoreTests asserts a Speech-only host refuses all three (OpenLink false, nothing recorded, speech responder not run, denying manager by identity) and a Network|Voice|Companions host permits them (`ReferenceEquals(GetCompanionManager("probe"), CompanionManager)`). MUTATION: "RecordingHost.OpenLink stops refusing a module without Network" FIRED (`OpenLink succeeded without Network.`).
- ✅ **F338** **FIXED 2026-09-29.** `FortunesEngineSelfTest` creates a `SelfTestScratch` root (`fortunes-engine`), hands the modules a `RecordingHost(storageRoot)` whose `GetStorage` is a `DirStorage` under it, asserts `the module's storage writes land under the scratch root, not the TEMP root` (Directory.Exists(scratch\fortunes)), and reports a refused release as a NOTE. Measured: the lane's TEMP root carries no `vectors\` or `fortunes\` after `--fortunes-engine-selftest`. MUTATION: "--fortunes-engine-selftest hands modules the TEMP root again" FIRED.
- ✅ **F347** **FIXED 2026-09-29.** `failures: a healthy load reports none` moved from `FailuresAreReported` (a loader that never called LoadFrom) into `Run`, where it asserts `loaded >= 1 && loader.Failures.Count == 0` on the loader that DID load the modules. MUTATION: "LoadFrom records a spurious failure for every module it loads" FIRED (`FAIL: failures: a healthy load reports none`).
- ✅ **F351** **FIXED 2026-09-29.** `ModuleHostSelfTest` does the same: a `module-host` scratch root, `RecordingHost { StorageRoot = scratch }` whose `GetStorage` returns `DirStorage(StorageRoot)`, `MinHostVersionGate` handed the scratch, and the check `modules' storage writes land under the scratch root, not the TEMP root`. MUTATION: "--module-host-selftest hands modules the TEMP root again" FIRED.
- ✅ **F355** **FIXED 2026-09-29.** `PetStudioModuleSelfTest` searches the pane's `Actions` for the labelled `Open Companion Studio…` action with a non-null `InvokeAsync` (`opening the studio is offered as a pane action (labelled, with an InvokeAsync)`) instead of `Actions != null`, and the module's own new SelfTest asserts the same. MUTATION: "PetStudio renames its open-studio action" FIRED.
- ✅ **F279** **FIXED 2026-09-29.** The label now says what the line measures: `WM_QUERYENDSESSION is not refused (an explicit no, or a branch that forgets m.Result, fails here; DefWindowProc also says yes, so the handler itself is proven by the next two lines)`, with the reasoning in a comment; the two assertions that follow (exit scheduled by the query, not run inline) remain the ones that catch a deleted branch, as the finding measured. MUTATION, by hand: `m.Result = (IntPtr)1;` removed -> `FAIL: WM_QUERYENDSESSION is not refused (...)`, exactly one failure; restored -> RESULT=PASS.
- ✅ **F288** **FIXED 2026-09-29.** The `../etc` reject case is relabelled for what rejects it (pack id/URL mismatch) and a new case carries `"id": "con"` with a URL that DOES match (`PackUrlBase + "con.txt"`), so the parser's `IsSafeId` call site is the only thing that can reject it. MUTATION, by hand: `!SecureDownload.IsSafeId(pack.Id) ||` dropped from the pack parser -> `CATALOG FAIL reject-case N was accepted` in dp-catalog-selftest.txt, exit 1; restored -> exit 0.
- ✅ **F290** **FIXED 2026-09-29.** `CompanionTypeRegistrySelfTest` (src/dotNet/RuntimeHardeningSelfTest.cs) deletes its `Software\DesktopAICompanion\SelfTest` scratch key and then the parent when it is empty (`DeleteRegistryScratch`, in both finally blocks) and asserts it: `registry scratch: an empty Software\DesktopAICompanion key does not outlive the self-test`. Measured after every run in this lane: `HKCU\Software\DesktopAICompanion exists: False`. MUTATION (tests/mutate-selftest-guards.py): "the registry scratch leaves its empty parent key behind again" FIRED.
- ✅ **F294** **FIXED 2026-09-29.** The scale-percent and pin probes in --hardening-selftest delete the probe's `.json`, `.bak` and `.lock` (`DeleteSettingsProbe`), with a WITNESS that the store wrote the `.bak` and `.lock` beside the probe and the check `scale percent: the probe's .json, .bak and .lock are all removed afterwards`. Measured: 0 `dp-scaleprobe-selftest-*` leftovers after every run in this lane (304 had accumulated on this machine since 2026-09-24). MUTATION: "the scale-percent probe deletes only its .json again" FIRED.
- ✅ **F299** **FIXED 2026-09-29.** The two UNC checks in `SecuritySelfTest` (plain and device-namespace `\\?\UNC`) assert the exact reason `The local pet must be an absolute path on a local drive.` and print the reason they got, so early rejection is told apart from a failed network open. MUTATION, by hand: the validator's reason reworded -> `[FAIL] UNC pet XML path rejected before probing (reason: The local pet must be a path on a local drive.)` and its device-namespace twin, exactly those two; restored -> all PASS.
- ✅ **F301** **FIXED 2026-09-29.** All four deadline checks wait through `TimedOutWithinBound` (5 s test-side bound; TimeoutException unwrapped from the AggregateException) and append the outcome to the label on failure, so a deadline regression fails by name instead of hanging the gate into its 30-minute CI timeout. MUTATION, by hand: `deadlineCancellation.CancelAfter(deadline)` -> `CancelAfter(TimeSpan.FromMinutes(59))` in SecureDownload.cs -> four `[FAIL] catalog deadline ... -- the deadline did not fire within 5 s, so the wait was abandoned` lines, the run finishing in seconds; restored -> all PASS.
- ✅ **F304** **FIXED 2026-09-29.** `SpriteBounds.SelfTest(out detail)` now has a caller: --hardening-selftest runs it after the generated-pixel budget and folds its detail into the check. MUTATION: "SpriteBounds' visible-pixel scan inverts its visibility test" FIRED (`FAIL: sprite visible-bounds: colour-key bounds wrong: {X=0,Y=6,Width=13,Height=7}`).
- ✅ **F315** **FIXED 2026-09-29.** Section 4 of --wpf-options-selftest finds the BUTTON the pane rendered for its action (`the pane renders a button for its action`), raises its Click through the routed event and reads the status the PaneView wrote (`pane action invokes + returns a status`), so an emptied Click handler or an unrendered action row fails; :1092's label was corrected to say the WITNESS reads the pending edit, not the test's own Load delegate. MUTATION (tests/mutate-diagnostics.py): "the action button's Click handler stops invoking the action" FIRED; "an InvokeAsync-only action gets no button" FIRED.
- ✅ **F383** **FIXED 2026-09-29.** `TestCurrentDirectoryIndependence` asserts the legacy candidates structurally instead of against a name `LegacySettingsFiles` never yields: exactly two, each named `DesktopPet.config`, neither under the test root, parents the executable directory and `LocalAppData\DesktopPet` (`SameDirectory` extracted from `AssertPathEqual`); the disabled arm prints a NOTE and asserts zero candidates. MUTATION: "the legacy settings lookup trusts the current directory" FIRED (`LegacySettingsFiles did not yield exactly its two anchored candidates. Expected '2', got '3'`).
- ✅ **F387** **FIXED 2026-09-29.** `tests/DesktopAICompanion.WindowSoak/Program.cs` OpenAnalyzeAndClose no longer skips SelectNode: `Demand(first.HasValue, ...)` throws (InvalidOperationException, same shape as `Require`) when Analyze() yields no animation node, so a rejected or empty pet fails on cycle 1 instead of soaking an un-analyzed window to PASS; the two comments the finding named are corrected (the sheet is decoded in Analyze(), SelectNode crops and renders; the fixture is a 1.15 MB file whose base64 is ~206 KB of a 154 KB 640x760 sheet). Built (`dotnet build ... WindowSoak.csproj -c Release`, 0 warnings); NOT RUN: the coordinator owns `tests/module-window-soak.ps1`. MUTATION for the coordinator: run it with `--pet` at a file the validator rejects and expect exit 1 on cycle 1 naming "Analyze() produced no animation nodes"; the default run must still print RESULT=PASS. Recorded under `#### fix/gates` in docs/DESIGN-REGISTER.md.
- ✅ **F388** **FIXED 2026-09-29.** `GetGuiResources` is declared `SetLastError = true`, `Sample` records `Marshal.GetLastWin32Error()` after the two calls, and Run asserts per segment `GUI resource counters readable (gdi N, user M[, last Win32 error E])` on the POST-segment sample only (a window has just been shown there, so 0 is unambiguously a failed call; the cold `before` sample legitimately reads GDI 0 and is not asserted), mirroring runtime-resource-soak.ps1's first-sample refusal. Built, not run (coordinator-owned soak); a 0 -> 0 counter pair now fails the segment instead of reading as flat.
- ✅ **F391** **FIXED 2026-09-29** (parts 1 and 3; part 2 declined, see `#### fix/gates` in docs/DESIGN-REGISTER.md). Every string `tests/difftest-prompt-options.py` listed without an expectation is now asserted in `SelfCheckPromptOptions` as one quoted `KindOf(...) == OptionKind.<kind>` per line (YES, `1) Yes, and don't ask again`, the U+02BC apostrophe, `Other...`, `yes `/` yes`, `Yes,`/`Yes, allow`/`Yes, allow `, `allow all edits this session`, whitespace-only, `no`/`NO, KEEP planning`, `Yes, allow access to`, and the four bare template prefixes), so the harness's `_KINDOF` parse carries them with expectations on both sides and `--module-selftest=agentflow` executes the C# on them. The harness derives the bare-prefix expectation from the table (exact entry or Unknown), REFUSES if any smoke string lacks a C# assertion, counts only compared cases (`cases compared: 76 of 96 listed (20 reference-only smoke strings, each also pinned by a C# assertion...)`, `no disagreements`), and its docstring no longer claims to parse `Choose(...)`. MUTATION (tests/mutate-agentflow.py): "list chrome no longer strips a numbered ')' prefix" FIRED (`FAIL: a numbered prefix with a parenthesis is list chrome`). The reference and the port agreed on all 20 smoke strings the first time they were compared.
- ✅ **F395** **FIXED 2026-09-29.** `tests/fullscreen-standdown-probe/StandDown.cs` checks `app.HasExited` after steps 3, 5 and 6 so a companion that died after step 1 fails the step where it died rather than blaming the restore path, and `Report` prints `'<title>' gone` for a window `IsWindow` no longer knows (the import was missing and is added). Compiled through `tests/fullscreen-standdown-probe/walkcount.csproj` (Release, 0 warnings). Not run: the fullscreen stand-down probe is coordinator-run.
- ✅ **F405** **FIXED 2026-09-29.** `tests/mutate-selftest-guards.py` grades a run from its column-0 `RESULT=` line, its `FAIL:`/`EXC:`/`SKIP:` lines and the exe's exit code together (`_verdict_lines`, `failure_lines`, `aborted_lines`, `passed`, `unhealthy`): an exception-red or skipping baseline is BROKEN, not "baseline clean", and a throwing mutation is BROKEN, not SURVIVED; CoreTests joined the harness as a target (its stderr reshaped into FAIL lines). Verified with 11 synthetic reports (scratch `classifier_check.py`, all as expected) and by the whole run: 36/36 FIRED over a clean baseline on 2026-09-29.
- ✅ **F412** **FIXED 2026-09-29.** The redirect-site slicer is a brace walk (`Get-ProcessStartInfoInitialisers`) that ends each slice at ITS closing brace, so a `Process.Start(new ProcessStartInfo { ... }))` site is its own slice; the synchronous-read scan (`Get-SynchronousReadSites`) runs over comment-stripped code; three WITNESS invariants pin the slicer (two slices from a `}))` site followed by a `};` site, the second site's pin not leaking into the first, the scan ignoring comments). MUTATION (tests/mutate-hardening-guards.py): "an unpinned redirect appears in a }))-closed initialiser" FIRED (`pins its own encoding, per SITE`); "the initialiser slicer runs each slice to the end of the file" FIRED; "the synchronous-read scan stops stripping comments" FIRED.
- ✅ **F417** **FIXED 2026-09-29.** `tests/Test-BacklogClosingCriteria.ps1` tracks indentation (`Get-LineIndent`, `$currentIndent`): an open glyph always takes over the item and records its indent, and a closed glyph flips the state only at the same or a shallower indent, so a nested tick sub-bullet ahead of an item's closing criterion no longer skips it. Verified against a fixture (scratch `fixture-backlog-nested.md`: the HEAD version reported 0 checkable criteria, the fixed one flips the item to CLOSEABLE) and against the live BACKLOG.md, where the count is unchanged (2 items carry a closing criterion before and after). A first attempt that used heading depth as nesting swallowed every bullet under a glyph-less `## Entries` heading and was dropped.
- ✅ **F418** **FIXED 2026-09-29.** Every runner that reads a `dp-*.txt` marker gives its children a PRIVATE TEMP (Path.GetTempPath reads TMP, then TEMP): `tests/Test-ModuleSelfTests.ps1` (`dp-msr-` + 12 hex), `tests/Invoke-SelfTests.ps1` (`dp-str-`, plus a sweep of aged `dp-*` dirs), `tests/mutate-selftest-guards.py` (`dp-msg-`), `tests/mutate-diagnostics.py` (`dp-mdg-`) and `tests/mutate-agentflow.py` (`dp-maf-`); each restores the environment in finally and keeps the directory only on failure. Prefixes are short on purpose: a run dir that made TEMP ~107 characters long tripped the MoveFileEx fallback (N-gates-01). Verified: every marker in this lane was read from the private dir (`read from dp-...txt` under the run TEMP), and two runners running at once on this box no longer share a path.
- ✅ **F419** **FIXED 2026-09-29.** `tests/Test-ModuleSelfTests.ps1` grades all three rules for a COVERED module (RESULT=PASS, at least one `PASS:` line, NO `SKIP:` line) and reports a `SKIP:` from an uncovered module too, matching the promise in `SelfTestProbe.Skip`. petstudio moved from UNCOVERED to COVERED the same day (it gained a module-class `SelfTest` over an embedded fixture, see F153), so the sweep is `7 covered module(s) passed, 1 known gap(s) still gaps`.
- ✅ **F442** **FIXED 2026-09-29.** `ShimejiParser.ParseBundledConf` loads `base-behaviors.xml` with `required: true` (the bundle always ships one, so absence is a build defect and now throws with the resource name); `BundledConfSelfTest` pins the behaviours half beside the 91/54/31/6 census: 47 weighted root behaviours (summed per name as `ParseBehaviorFrequencies` reads them), `SitDown == 200`, at least one selection condition, all printed on the success line; `PetStudioModuleSelfTest.ImportEngineIsWired` mirrors the frequency-table check through the reflection it already uses ("bundled base behaviours embed and parse (frequency table populated)"). MUTATION, by hand, tool rebuilt and engine DLL mtime advanced each time: `<LogicalName>` renamed -> `bundled-conf self-test: ParseBundledConf threw -- Bundled Shimeji conf resource missing: base-behaviors.xml`, SELFTEST FAIL; `if (freq <= 100) continue;` in the frequency parser -> `FAIL expected 47 weighted root behaviours with SitDown=200 ... got 2 weighted` (VocabSelfTest's British-frequency line fell with it, as it should). Source restored byte-identical, restored selftest PASS.
- ✅ **F446** **FIXED 2026-09-29.** ONE list: `ActionClassifier.FramePlayingClasses` (+ `IsFramePlayingClass`) is read by the classifier's Group1 chain (per-class reasons, every one promising "frames" or a "jump arc") and by `PetEmitter.IsFramePlayingEmbeddedClass`, whose private switch is gone. `ClassifierSelfTest` takes the classifier's half from the classifier: each classed Group1 fixture action's REASON (frames/jump arc vs magic/facing change/positional nudge) is compared with `IsFloorAction` (at least 8 cases, a reason readable as neither fails), and a synthetic one-pose action of EVERY listed class is graded through both halves, with a synthetic Fall as the WITNESS that the emitter still refuses an absorbed class. MUTATION, by hand: `"Breed"` added to the list -> `FAIL contract: 'Breed' is in ActionClassifier.FramePlayingClasses but classifies as Group3 (...)` (+ refused by IsFloorAction); emitter refusing Regist -> `FAIL contract: classifier grades 'G1_Resisting' (Regist) Group1 with frames, but PetEmitter.IsFloorAction refuses it`. Both FIRED; restored -> PASS. The skeptic's own emitter-side mutation (renaming `case "Regist"`) is impossible now: there is no second list to rename.
- ✅ **F447** **FIXED 2026-09-29.** The emitter fixture gains `StrollAndHop` (Sequence of Walk and Bounce, both already hub spokes, frequency 40 set on the config) and its frequency-less twin `StrollAndHopUnplayed`; the test requires the first to be chained (`StrollAndHop_1_Walk`, `StrollAndHop_2_Bounce`, exactly one hub entry, hand-off to the second step, hub entry weight `== max(1, HubWeightFromFrequency(40) / 2)` with a WITNESS that this differs from the base weight) and the second to be absent, so the PLAYED term decides in both directions; `flatConfig` copies `BehaviorFrequency` so the "one edit" claim is true; the GatorRide comment says what it exercises (RECOVERS). MUTATION, by hand: PLAYED term deleted -> `FAIL the PLAYED sequence 'StrollAndHop' (frequency 40, members already spokes) was not chained: ... got []`; gate admits everything -> `FAIL 'StrollAndHopUnplayed' has no behaviour frequency and recovers nothing, yet it was chained (...)`. Both FIRED, exactly one failure each; restored -> PASS. Existing hub weight comparisons unaffected (suite green).
- ✅ **F448** **FIXED 2026-09-29.** The spawn off-screen guard in `EmitterSelfTest` evaluates each spawn over random in {0, 50, 99} and randS in {10, 50, 89} (`EvalOnFakeScreen` takes both), and the failure names the draw. MUTATION, by hand: spawn 1's X loses its imageW and margin terms (`random*screenW/100`) -> three `FAIL spawn 1 lands the pet off-screen horizontally (x=1900 of 1920 at random=99, randS=...)` lines, one per randS; at the old single point (random=50, x=960) it passed. Restored -> PASS.
- ✅ **F453** **FIXED 2026-09-29.** `HubWeightSelfTest`'s degenerate-input block wraps the null / empty / all-zero calls in try/catch (a throw becomes a FAIL line naming the exception instead of a crash that hid four sub-tests) and replaces the literal `true` with the post-conditions the guards imply: `empty.Count == 0` and `zeros.SequenceEqual({0,0,0})`. MUTATION, by hand: `ApplyMinimumShare` grows an empty set (`weights.Add(1)`) -> `FAIL an empty weight set is left empty`, exactly one failure; restored -> PASS.
- ✅ **F462** **FIXED 2026-09-29.** `SpriteSheetBuilder.MaxTiles = DesktopAICompanion.CompanionXmlValidator.MaximumSpriteTiles` (one source instead of an unbound literal), and the engine csproj's tile-cap target asserts the SHIM too: `ValidatorResources.cs` must exist (a moved file is a named error) and contain `MaximumFrames = 1024`. MUTATION, by hand: shim set to 2048 -> build error `The SpriteFrameStore shim in tools/ShimejiConvert.Engine/ValidatorResources.cs no longer reads 1024 while src/dotNet/Xml.cs does...` (build FAILED, 1 error); restored -> builds and SELFTEST PASS.

Info, disposition only:

- ✅ **F085** **FIXED 2026-09-29** (info). The cancel block asserts the actual invariant, `a cancelled AI request emits no diagnostic line at all` (`lines.Count == 0`, the list having been cleared just before), instead of `everyLine.Count >= 3`, whose comment claimed it guarded a fourth line from a logged cancel and did not. MUTATION, by hand: the retry loop's cancel catch made to `Log("request cancelled: ...")` -> `FAIL: a cancelled AI request emits no diagnostic line at all` while `is not logged as a request failure` stayed PASS, which is exactly the line the old phrase check could not see; restored -> RESULT=PASS.
- ✅ **F119** **FIXED 2026-09-29** (info). `Embedder` counts every `new InferenceSession` (`_sessionsCreated`, `SessionsCreatedForDiagnostics`) and the concurrent first-use check asserts the delta across its contending workers is exactly one (`concurrent_first_use_sessions=N (expect 1)`), which is the single-load property the finding said only liveness and validity stood in for. MUTATION, by hand: `if (_tried || _disposed) return;` -> `if (_tried && _disposed) return;` so every worker loads its own session -> `concurrent_first_use_sessions=8 (expect 1)`, `concurrent_first_use=FAIL`, `FAIL: Embedder loads ONNX + embeds in the module ALC`; restored -> PASS.
- ✅ **F126** **FIXED 2026-09-29** (info). The taxonomy loop in `ValidateEmbeddedForSelfTest` is removed with a comment saying why: the parser rejects a bad topic/genre/level before the corpus exists, so the loop could not fail. The embedded-corpus check that CAN fail stays (harness case "the embedded welcome corpus fails to load" FIRED in the whole run).
- ✅ **F247** **FIXED 2026-09-29** (info). The three audio cleanups read `SelfTestScratch.TryRelease`'s result and write `NOTE: scratch left for the next sweep (<detail>)` when a release is refused, so the marker says why a scratch outlived the run instead of dropping the answer. No assertion added on purpose: the collectible ALC still maps the module DLL when the test ends, so a refused release is expected and the sweep collects it; `--audio-selftest` RESULT=PASS, 45 PASS lines.
- ✅ **F293** **FIXED 2026-09-29.** The facing-rule check no longer compares `ShouldFaceLeft` with itself; it asserts the neighbourhood of the pinned boundary (`!ShouldFaceLeft(500, 500) && ShouldFaceLeft(499.999, 500) && !ShouldFaceLeft(500.001, 500)`) under a comment that says what a strobe would actually depend on. MUTATION, by hand: `cursorX < characterCentreX` -> `<=` in FormCompanion.cs, host rebuilt (exe mtime advanced) -> `FAIL: gaze: a cursor exactly on the centre faces right, and the boundary sits at the centre`, exactly one failure; restored -> RESULT=PASS.
- ✅ **N-gates-01** **FIXED 2026-09-30 (ModuleKit and the host twin, lane fix/followups).** Both `AtomicFile.ReplaceExisting` fallbacks (`src/DesktopAICompanion.ModuleKit/AtomicFile.cs`, `src/Portable/AppSettingsStore.cs`) hand `MoveFileEx` the extended-length form (`ExtendedLengthPath`: `\\?\` or `\\?\UNC\` on the normalised path), so the write-through rename no longer fails past MAX_PATH in a process with no `longPathAware` manifest; the P/Invoke stays rather than `File.Move(overwrite)`, which drops `MOVEFILE_WRITE_THROUGH` and allows a copy-and-delete (decision under `#### fix/followups` in `docs/DESIGN-REGISTER.md`). Pinned in CoreTests ("Atomic replace fallback past MAX_PATH"): both twins run the forced fallback through the `replaceFile` seam at a 400-plus character directory, WITNESS the same forced fallback at a short path and a check that the long fixture really is past 260. MUTATION (tests/mutate-selftest-guards.py, lane anchor): "followups: ModuleKit's MoveFileEx fallback drops the long-path form" and "followups: the host twin's MoveFileEx fallback drops the long-path form", each naming its twin's "threw past MAX_PATH" line; results recorded in the commit. The Fortunes half stays as lane fix/fortunes left it (a failed vector-cache save is reported); the harnesses' short run-dir prefixes stay, since a runner still should not fail a module on a path length it never sees in production, but the product path is now fixed. The original record: ModuleKit `AtomicFile.ReplaceExisting`'s MoveFileEx fallback (and its twin in `src/Portable/AppSettingsStore.cs`) failed near MAX_PATH: the Fortunes VectorCache self-test's "portable replace fallback was not durable" check FAILED when `%TEMP%` was 107 characters long and passed at 74 (measured 2026-09-29 while giving the mutation harnesses a private per-run TEMP), because `VectorCache.Save` swallowed the exception and the P/Invoke was not declared with the `\\?\` long-path form. Found by lane fix/gates.
- ✅ **N-gates-02** **FIXED 2026-09-29 (aibrain 1.1.14 and fortunes 1.0.12), both module halves; the `ConventionHost` half (hand modules a scratch storage, as F351 did for `--module-host-selftest`) belongs to lane fix/host.** AiBrain: `AiPaths` has no `%TEMP%` fallback any more: with no root set (the convention host gives a module no storage), `AiSettings.Load` returns defaults with every write blocked and a `LoadWarning` ("the host gave this module no storage directory; running on defaults and saving nothing") that Init logs, and nothing is written anywhere; the shipped host always provisions a storage directory (`CompanionHost.GetStorage`), so the product path is unchanged. Measured after: `--module-selftest=aibrain` into an empty TEMP leaves the marker and the loader's own `dp-module-selftest-<guid>` scratch (still mapped by the collectible ALC, swept next run) and no `DesktopAICompanion.AiBrain` directory. Asserted in `AiEngineProbe.Run`: with the root swapped to null, Load yields defaults, `Save()` refuses, the warning names storage, and the old fallback file's write time is unchanged. MUTATION: "aibrain: the settings root falls back to %TEMP% again" FIRED (`no storage root: Load yields defaults and blocks every write`). Fortunes: The Fortunes half is done (lane fix/fortunes, 2026-09-29, fortunes 1.0.12): `FortunesModule.SelfTest` runs the probe and the custom-cache check under a scratch root it removes and asserts the fallback root gained nothing; reading the fortunes folder no longer creates it (`FortunePaths.FortunesDirPath`); a host with no settings store gets smart picks off, so the runner's Init starts no embed and opens no vector cache. `FortunePaths.cs:29`'s fallback stays, dispositioned in `docs/DESIGN-REGISTER.md` (fix/fortunes). MUTATION "fortunes: the module self-test runs against the engine's live root again" FIRED. What `--module-selftest=fortunes` leaves in an empty TEMP after this is measured in the fortunes lane's report. As filed for both modules: `--module-selftest=aibrain` left `%TEMP%\DesktopAICompanion.AiBrain\ai-settings.json` (+ `.lock`) and `--module-selftest=fortunes` leaves `%TEMP%\DesktopAICompanion.Fortunes\vectors\cache.bin` (+ `.lock`), through the engines' static fallback roots (`modules/AiBrain/engine/AiPaths.cs:29` as it was, `modules/Fortunes/engine/FortunePaths.cs:29`) that resolve to `%TEMP%\DesktopAICompanion.<Module>` when nothing called `SetRoot`; measured 2026-09-29 by running each flag alone into an empty TEMP, those two flags left 2 files each while `--aibrain-selftest`, `--fortunes-selftest`, `--fortunes-engine-selftest` and `--fortunes-smart-progress-selftest` leave none; inside the gate the leak lands in Test-ModuleSelfTests.ps1's private run TEMP and is deleted with it, a by-hand run leaks into the real `%TEMP%`. Same family as F338/F351 but in the modules' own `SelfTest`.

### Lane fix/host (Phase 2-3): src/ except the settings store

76 items: 63 to fix or disposition, 13 info to disposition.

- 📌 **F226** [medium/correctness] No module declares ModulePermissions.LaunchProcess although AiBrain, Remembrance and PetStudio spawn processes; the flag has disclosed nothing since it shipped in 1.2.5 (`src/DesktopAICompanion.Contracts/PluginApi.cs:121`)
  Left open by lane fix/host on purpose (2026-09-29): the fix is three module declarations, each owned by its lane. Each adds `| ModulePermissions.LaunchProcess` to its `Info.Permissions` with a one-line comment naming the executables and bumps its patch version: fix/aibrain in `modules/AiBrain/AiBrainModule.cs` (ollama serve, tesseract, taskkill); fix/remembrance in `modules/Remembrance/RemembranceModule.cs` (whisper-cli and the installer's verify run); fix/petstudio in `modules/PetStudio/PetStudioModule.cs` (the bundled dwebp and, when present, ffmpeg/ffprobe). MinHostVersion stays where it is: an older host's TryParsePermissions drops the unknown name. The coordinator closes this when all three have merged; the close should also add InputMonitoring and LaunchProcess (1.2.5) to `docs/module-authoring.md`'s flag table, which still ends at InputSynthesis, and reword `docs/BACKLOG-CLOSED.md:586` so "three holders" no longer reads as three declarers.
- ✅ **F245** **FIXED 2026-09-29.** AudioOutput subscribes DirectSoundOut.PlaybackStopped before Play: a device that fails or vanishes tears the output down, is logged, fails over to the default once, then latches with a two-minute retry, and SetDevice clears the latch even when the GUID is unchanged. Asserted in --audio-selftest (OutputFailureIsObserved: a random GUID's DSERR_NODRIVER is observed, the dead output is torn down, the chosen device is remembered as failed, the next start avoids it). MUTATION: dropping the subscription FIRED on "asynchronous failure is observed".
- ✅ **F270** **FIXED 2026-09-29.** CheckTopWindow tests rcTitleBar.Bottom >= rcTitleBar.Top (a rect with shape) instead of Bottom >= 0 (a screen position), so occluders on a monitor arranged above the primary count again; pet windows still pass through 0 >= 0. Source invariant asserts the argument and the absence of the old test. MUTATION: reverting to >= 0 FIRED. Live check for the coordinator: with a monitor above the primary, a pet falling toward a window hidden behind a maximised one must fall through.
- ✅ **F278** **FIXED 2026-09-29.** FormCompanion.CollectOwnedHandles gathers the root, every live child recursively and the speech bubble, and StartUp.SheepHandles delegates to it, so a TopMost child or bubble over a monitor's centre no longer decides it as clear. Source invariants assert the delegation and both kinds of owned window. MUTATION: removing the bubble line FIRED.
- ✅ **F352** [medium/correctness] Pending removal marker is cleared even when the install-folder delete threw, so an uninstall blocked by a sibling instance or a transient lock is lost and leaves a half-deleted module folder (`src/dotNet/Plugins/PendingModuleRemovals.cs:36`) **FIXED 2026-09-29.** PendingModuleRemovals.ProcessPending keeps the ids whose delete threw in the marker (rewritten, deleted only when empty), logs that they are retried next launch, and returns them; StartUp hands that list to ModuleHost.LoadFrom, which records "still being removed" for such a folder instead of loading and locking it again. Unmark(id) forgets a pending removal, called from PendingModuleUpdates.MarkForUpdate and from the pane's new-install path, so removals running first on the next launch cannot delete a module the user just re-staged. The data-directory path is built without ModuleDataDirectory's mkdir side effect. Runtime checks in --module-host-selftest (PendingRemovalRetry: a held handle keeps the id marked and its data folder intact, the loader skips it with the reason and a loader not told tries it, the retry after release removes it and clears the marker, Unmark keeps the rest); a source invariant pins the hand-off in StartUp. MUTATION: marker cleared unconditionally FIRED; loader ignoring the list FIRED; StartUp passing nothing FIRED (gate). Inherent and left as such: a sibling instance still running the module loses assets under it whichever way the delete happens.
- ✅ **F367** [medium/correctness] A cancelled or failed install extraction leaves a half-populated modules/<id> folder that the pane lists as 'installed, restart to activate' and the loader then reports as failed (`src/Portable/Wpf/ModulesPaneControl.cs:561`) **FIXED 2026-09-29.** A new module is unpacked into PendingModuleUpdates' staging folder and moved into modules/<id> with one same-volume Directory.Move once whole, so the install folder is there whole or not at all; the cancel and error catches delete the staging folder (DiscardStaged) whether or not the pane is still loaded, and the cancel message says nothing was left behind. Source invariants in runtime-hardening-selftest.ps1 pin extraction-into-staging before the move, in-place extraction absent, and both catches discarding. MUTATION: extracting in place again FIRED; the update's silent catch FIRED. A process killed mid-unpack strands only a staging folder beside modules/, where the loader never looks.
- ✅ **F372** [medium/correctness] Fixed 820 DIP initial height exceeds the work area of common laptop displays (1366x768, 1080p at 150%); CenterScreen centres on the requested height while Windows clamps the actual height, so the caption and first nav row open above the screen top (and at 1366x768 the bottom edge sits behind the taskbar) (`src/Portable/Wpf/OptionsWindow.cs:35`) **FIXED 2026-09-29.** OptionsWindow.InitialSize (pure) fits the preferred 1050x820 inside SystemParameters.WorkArea less 24 DIP per axis and never below the 700x520 floor; the constructor uses it and sets no MaxHeight. --wpf-options-selftest proves the fit on a 1366x768 (720 DIP) and a 150%-scaled 1080p (672 DIP) work area with a roomy-display witness and the per-axis floor, and that the window asks for what the fit says; a source invariant pins the constructor's call. MUTATION: the clamp removed FIRED (wpf); a fixed 820 restored in the constructor FIRED (gate). Live check for the coordinator: none needed on this 1440p box beyond the self-test; on a laptop the caption must sit inside the screen.
- ✅ **F375** [medium/race] A ReloadPaneAfter action that completes after the user switched panes (or closed the window) rebuilds ITS pane into the content area: nav and content disagree, the other pane's unsaved edits are dropped, and a fresh view of the same pane is overwritten with the stale view's on-screen values (`src/Portable/Wpf/OptionsWindow.cs:1097`) **FIXED 2026-09-29.** ShellPane.RequestReload is a Func<bool>: the window's delegate declines once the pane is no longer _current or the window has closed (OnClosed sets _closed), NotifyDirty is guarded the same way, and PaneView drops its ActionRebuild / pending-values stash when a reload is declined (PaneView.ForHost carries the host form; the Action constructor still answers true, so every fixture is unchanged). --wpf-options-selftest builds the window headlessly, clicks a ReloadPaneAfter action whose task completes after a pane switch, and asserts content and nav stay on the other pane with the stash empty; witness: the same action on its own pane still rebuilds and carries its message. MUTATION: the guard removed FIRED; the stash left behind FIRED.
- ✅ **F207** [low/correctness] TestModule's preview verb reads pet XML from the installed-mode %LOCALAPPDATA% library instead of ICompanionManager.TryReadTypeXml, so in the portable dev tree it builds into it cannot find a downloaded pet (dev-only module, never shipped) (`modules/TestModule/TestModule.cs:119`) **FIXED 2026-09-30.** TestModule.PreviewClicked reads the installed pet through ICompanionManager.TryReadTypeXml; ReadInstalledXml and its %LOCALAPPDATA% path are gone, and the no-pet message names CompanionsDirectory. Source invariant asserts the ABI call and the absence of the hand-rolled path. MUTATION: hand-rolled read restored FIRED. The verb itself is a tray click: manual check in the report.
- ✅ **F229** [low/correctness] EmbeddedResources suffix match has no separator check: 'icon.png' also matches 'tray-icon.png' (latent in tree; the template's own suffix is 'icon.png') (`src/DesktopAICompanion.ModuleKit/EmbeddedResources.cs:92`) **FIXED 2026-09-30.** EmbeddedResources.FindName tries the exact name, then MatchesResourceName: the suffix must be the whole name or start right after a '.', so "icon.png" no longer finds "tray-icon.png". The rule is public and pure; CoreTests "ModuleKit embedded resources" asserts it and loads two colliding fixtures embedded in the harness itself (the decoy listed first, witnessed by manifest order and differing bytes). MUTATION: boundary test replaced by true FIRED (CoreTests); bare EndsWith restored FIRED (hardening harness).
- ✅ **F230** [low/correctness] JsonSettingsStore<T> (no in-tree consumer) folds an unreadable file into defaults and Update then writes those defaults over it with no backup, unlike the two stores it was distilled from (`src/DesktopAICompanion.ModuleKit/JsonSettingsStore.cs:45`) **FIXED 2026-09-30.** JsonSettingsStore.TryRead reports Missing, Loaded or Unreadable; Load exposes LastLoadWasUnreadable; Update refuses to write over an Unreadable document; every write goes through AtomicFile with BackupPath_ (<path>.bak) holding the previous document. CoreTests "ModuleKit json settings store": a corrupt file makes Update answer false and leaves the bytes unchanged, and the backup holds the previous document. MUTATION: the refusal removed FIRED; the backup path nulled FIRED.
- ✅ **F231** [low/race] JsonSettingsStore.Update is atomic only in-process: Load and Save take the cross-session lease separately, so a second instance can write between them (`src/DesktopAICompanion.ModuleKit/JsonSettingsStore.cs:94`) **FIXED 2026-09-30.** Update takes ONE CrossSessionLock lease across read, mutate and write (no Load()/Save() pair, so no window between two leases). CoreTests holds the lease file with no sharing and asserts Update declines and the file is unchanged; the source invariant asserts the ORDER lease, read, refuse, write. MUTATION: the lease check disabled FIRED (CoreTests); the order invariant's mutation FIRED (hardening harness).
- ✅ **F241** [low/performance] Every animation transition evaluates TAnimation.UpdateValues twice, the first time against the primary monitor and immediately discarded (`src/dotNet/Animations.cs:1164`) **FIXED 2026-09-30.** The chooser's UpdateAnimationValues is now AnnounceChosenAnimation: the debug line only, no UpdateValues and no store-back (TAnimation is a struct and nothing reads evaluated values out of SheepAnimations). FormCompanion.SetNewAnimationCore's UpdateValues(DisplayIndex) is the one evaluation. TAnimation.EvaluationCount counts evaluations: --hardening-selftest asserts a chooser call adds none and the consumer's copy adds one; the source invariant asserts the announce body has no UpdateValues and the consumer evaluates right after GetAnimation. MUTATION: the evaluation re-added FIRED in both harnesses.
- ✅ **F246** **FIXED 2026-09-29.** DecodeModuleAudio owns its reader in a using for both branches, so the ACM stream and its pinned buffers are released per decode instead of by the finalizer; --audio-selftest now drives the MP3 branch (DecodesAnMp3, over SecuritySelfTest's shared MPEG fixture, twenty decodes in a row). Disposal itself is not observable headless; the branch's coverage is what the check adds.
- ✅ **F249** [low/correctness] Header-name cache is not invalidated by CompanionHost.InstallType (PetStudio's install path), UninstallType, or the pane's UninstallPet; only the pane's download path calls Forget (`src/dotNet/CompanionCatalog.cs:94`) **FIXED 2026-09-29.** CompanionCatalog.Forget is called by every writer: the pane's download (as before), the pane's UninstallPet after the delete, and Companion Studio's InstallType and UninstallType through the host; Forget raises a new Forgotten event the Companions pane's static constructor subscribes ForgetStats to, so one call reaches the header-name, stats and icon caches without src/dotNet referencing a WPF class. --hardening-selftest asserts the raise (with a null-id witness); source invariants assert each writer's call. MUTATION: the raise removed FIRED (runtime); UninstallType's Forget removed FIRED (gate).
- ✅ **F250** [low/correctness] ReadHeaderName's 32K-character bounded read can miss <petname> for a hand-authored header (`src/dotNet/CompanionCatalog.cs:161`) **FIXED 2026-09-29.** ReadHeaderName reads in 32K blocks until `</header>` has arrived or HeaderReadBoundChars (MaximumIconBytes as base64 plus 70K of text) is reached, looking for the close tag across block boundaries. --hardening-selftest writes a header whose petname sits 40K in and asserts it is found, and that the bound admits the largest icon a header may carry. MUTATION: the bound set back to 32K FIRED.
- ✅ **F251** [low/performance] Tray 'Add a companion' submenu, the Companions pane build and InstalledTypes each re-read every installed pet's 32K header on the UI thread per open (measured ~7 ms warm for 54 pets; cold unmeasured) (`src/dotNet/CompanionCatalog.cs:283`) **FIXED 2026-09-29.** The header name is cached per xmlPath, keyed by the file's LastWriteTimeUtc and Length, so a rewrite is a miss and a re-render a hit; both AddFrom and ReadHeaderNameForId read through it and the bundled-vs-library precedence is untouched (the key is the file). Saving stated as avoided I/O: one read per file per change instead of one per installed pet per tray open (54 on this box), counted through the HeaderFileReads seam in --hardening-selftest (repeat looks read nothing; a rewrite with a bumped write time reads again and returns the new name). Not a timing. MUTATION: the key reduced to the path FIRED; the cache store removed FIRED.
- ✅ **F255** [low/leak] Module submenu child Bitmaps built in one tray session are orphaned when the next tray open disposes the parent item (today only AgentFlow's Auto-approve status dot) (`src/dotNet/ContextMenus.cs:74`) **FIXED 2026-09-30.** ContextMenus.DisposeItemTree disposes an item's drop-down children recursively, then its Image, then the item; ModuleTray_Opening, RebuildModuleSubmenu and Dispose use it, so a submenu child's Bitmap built in one tray session is disposed with its parent. --hardening-selftest builds a parent/child/grandchild with a child Bitmap and asserts the Bitmap is disposed and all three items raise Disposed. MUTATION: the Image disposal removed FIRED (self-test harness); the item disposed before its children FIRED (hardening harness).
- ✅ **F256** [low/leak] Host-owned Add/Remove/Companion Speech submenus Clear() their children without Dispose(); each hovered per-pet speech drop-down keeps a hidden HWND until finalization (`src/dotNet/ContextMenus.cs:374`) **FIXED 2026-09-30.** ClearAndDispose replaces every DropDownItems.Clear() in the host-owned Add, Remove and Companion Speech menus (and the latter's catch): snapshot, Clear, dispose each tree, so a hovered per-pet drop-down's hidden HWND goes with its rows. The probe asserts the drop-down empties and the rows raise Disposed; the source invariant asserts no DropDownItems.Clear() remains in the file. MUTATION: one Clear() restored FIRED.
- ✅ **F260** [low/correctness] FactoryReset.Wipe returns success when the root cannot be listed; the wrong exit code is visible only to a manual console or script run because the MSI action is Return="ignore" (`src/dotNet/FactoryReset.cs:71`) **FIXED 2026-09-30.** FactoryReset.SafeList takes the failure counter and the log; a listing that throws counts as a failure and logs "could not list", so Wipe returns false and the console or script exit code says so. The probe asserts both branches. MUTATION: failed++ removed FIRED in both harnesses.
- ✅ **F262** [low/performance] Alpha pets rebuild an HBITMAP and call UpdateLayeredWindow on every tick (about 5-10 per second at the corpus's 100-200 ms intervals), twice per tick only during spawn and kill fades; colour-key pets reassign PictureBox.Image per tick even on a same-frame hold (`src/dotNet/FormCompanion.cs:355`) **ACCEPTED-RECORDED 2026-09-29.** The finding's own first step is a cold GDI-object and CPU measurement per alpha companion with and without the change, in fresh interleaved processes, and this lane made none; a push-skip when frame, position and opacity are unchanged is cheap but its saving is unmeasured, and a shared HBITMAP cache reaches into the mirrored-frame ownership FlipOrientation manages. Left for a measured pass; register.
- ✅ **F263** **FIXED 2026-09-29.** The three bare ReleaseWindowGrip(true) sites in NextStep set bNewAnimation = true, so the tick renders the fall's first frame at its own interval and skips the y-detectors; a ratio invariant holds every release in NextStep to the rule (5 of 5). MUTATION: dropping one FIRED.
- ✅ **F264** **FIXED 2026-09-29.** The Start.X.Value != 0 gate in front of FollowWindow is gone (upstream master dropped it too), so a sitting or sleeping pet rides a dragged window. Source invariant asserts the bare if (FollowWindow()) and the gate's absence. MUTATION: re-adding the gate FIRED. Live check for the coordinator: drag a window under a sleeping pet, including a pose with no gravity node.
- ✅ **F265** **ACCEPTED-RECORDED 2026-09-29.** The per-tick walk stays declined as the record says (0.60 ms per walk, measured). The two trims that were never the declined walk landed alongside: FallDetect's dead second GetWindowText pair deleted, the title string built once per window in both detectors, and AudioOutput.ReadAll block-copies each chunk (ArraySegment AddRange). No number is claimed for the trims.
- ✅ **F268** **FIXED 2026-09-29.** One ClearFullscreenStandDown() serves CheckFullScreen's clear branch, and RelocateToDisplay un-suppresses the bubble before the Play() that re-shows the pet; source invariants pin both exits and the order. MUTATION: dropping the un-suppress from RelocateToDisplay FIRED.
- ✅ **F269** [low/performance] CheckTopWindow(true) re-walks the z-order above the surface window on every tick a pet stands still on a window, with a StringBuilder and three user32 calls per visible window; cost unmeasured and not covered by the backlog's detector measurement (`src/dotNet/FormCompanion.cs:2065`) **ACCEPTED-RECORDED 2026-09-29.** A throttle on CheckTopWindow(true)'s walk is the right shape (staleness here only delays a fall) but the finding asks for a before/after measurement by the backlog's own method and none was made in this lane; the StringBuilder hoist alone is not worth a change without it. Register.
- ✅ **F272** [low/leak] Every FormCompanion deserialises a form Icon it never shows (ShowIcon=false, no border); the HICON lives until finalization, a per-spawn cost that is bounded by GC (`src/dotNet/FormCompanion.Designer.cs:42`) **FIXED 2026-09-30.** FormCompanion.Designer.cs no longer deserialises the form Icon, nor creates the ComponentResourceManager only it used: ShowIcon is false, the border is None and the window is a tool window, and nothing reads the property. Source invariant asserts the absence. MUTATION: the assignment restored FIRED. Visible only by absence; no manual check.
- ✅ **F273** [low/leak] Shift-launched debug ListView has no row cap, so a multi-pet session accumulates rows at roughly 5-12 per second for as long as it runs (`src/dotNet/FormDebug.cs:96`) **FIXED 2026-09-30.** FormDebug.MaxRows = 5000 and TrimRows after every add drops the oldest rows, so a session's list stays bounded. --hardening-selftest constructs the form unshown, adds MaxRows + 25 rows and asserts RowCount == MaxRows. MUTATION: the cap doubled FIRED (self-test harness); the trim after a plain add removed FIRED (hardening harness).
- ✅ **F274** [low/correctness] Debug window's Notepad handoff targets a launcher stub on Windows 11, so Open XML / Convert to DOT open an empty Notepad and swallow the error; an unguarded MainWindowHandle can retitle a foreign window in a race (`src/dotNet/FormDebug.cs:171`) **FIXED 2026-09-30.** Open XML and Convert to DOT write the text to %TEMP%\dp-debug-<kind>.txt (WriteDebugText: one fixed file per kind, overwritten) and open it through the shell; the two P/Invokes, WM_SETTEXT and the unguarded MainWindowHandle are gone, and a failed handoff is logged as an error row instead of swallowed. The probe asserts the file, its content and its sanitised name. MUTATION: the file name randomised FIRED (self-test harness); the catch swallowing again FIRED (hardening harness). Manual check: SHIFT-start, Debug window, Open XML opens the editor with the pet's XML.
- ✅ **F327** [low/correctness] --aibrain-selftest leaves the brain enabled after the declined-drop check, so the engine leg runs beside a live localhost probe that can launch `ollama serve` (`src/dotNet/Plugins/AiBrainModuleSelfTest.cs:103`) **FIXED 2026-09-29.** --aibrain-selftest presses the same Enable row again immediately after the declined-drop check (the Label is static; only DynamicText reads Disable), asserts the press landed and that the drop responder declines again, so the second Reconfigure cancels the first before EnsureServerAsync can reach TryStartServer and the engine leg runs beside no live session. Source invariant asserts two presses with the second before the AiEngineProbe hand-off. MUTATION: the second press removed FIRED.
- ✅ **F328** [low/correctness] Drop, poke and speech responder chains swallow a throwing responder with no log line, unlike RaiseEach (`src/dotNet/Plugins/CompanionHost.cs:132`) **FIXED 2026-09-29.** RaiseChain and RaiseSpeechRequest replace the bare Safe() with try/catch that calls Log(r.ModuleId, "responder threw and was treated as declined: ...") and keep the declined semantics. Runtime check in --module-host-selftest (ResponderChainDeclines: a throwing priority-10 drop responder is passed over and the priority-5 one is offered the turn, with the throw caught as a distinct failure rather than an EXC); source invariant asserts the Log argument and the absence of Safe( in both chains. MUTATION: the catch rethrowing FIRED (runtime); Safe() restored FIRED (gate).
- ✅ **F331** **FIXED 2026-09-29.** The same change as F309: StartUp.NoteFullscreenScan is the only caller of CompanionHost.RaiseFullscreenChanged and now marshals the raise to the UI thread, so a module reading IsFullscreenActive off the UI thread can no longer deliver FullscreenChanged on its own worker to every other module.
- ✅ **F332** [low/race] RaiseSpeechRequest walks the live _speechResponders list, so a responder that disposes its registration in-callback throws InvalidOperationException out of Say/SayAll (latent: no shipped speech responder) (`src/dotNet/Plugins/CompanionHost.cs:556`) **FIXED 2026-09-29.** RaiseSpeechRequest walks `_speechResponders.ToArray()`, as RaiseChain copies its chain. Unreachable headless (SpeechEnabled is false without a LocalData), so a source invariant asserts the snapshot. MUTATION: the live list restored FIRED.
- ✅ **F333** [low/race] SpeechRequest.ShowBubble marshals only the targeted branch; a broadcast (SayAll) bubble re-shown from a worker thread creates FormSpeech off the UI thread (latent: no shipped speech responder) (`src/dotNet/Plugins/CompanionHost.cs:596`) **FIXED 2026-09-29.** CompanionHost captures the UI SynchronizationContext and thread id at construction; PendingBubble.Show posts BOTH branches through it when called off the UI thread (decided by thread id, since WinForms may hand the UI thread a fresh context instance), then falls back to the targeted BeginInvoke and the inline draw; a draw that throws is logged rather than swallowed. Source invariant asserts the Post precedes the InvokeRequired branch and the thread-id test. MUTATION: the Post branch removed FIRED.
- ✅ **F336** [low/correctness] Companion Studio Install and both uninstall paths rewrite or delete a pet without invalidating the three per-id display caches, and Install also leaves the on-screen type registry serving the old skin (`src/dotNet/Plugins/CompanionHost.cs:1061`) **FIXED 2026-09-29.** InstallType calls CompanionCatalog.Forget(typeId) after the write and then best-effort ReloadPetType so on-screen copies swap onto the new definition (Deferred/NeedsRestart are not install failures); UninstallType forgets after its delete. The pane's caches follow through Forgotten (F249). Source invariant asserts the Forget and the reload in InstallType. MUTATION: the reload block removed FIRED.
- ✅ **F339** [low/correctness] TryFindSelfTest is not as deterministic as its doc promises: second stage takes the first match, and an overload hides the entry point (`src/dotNet/Plugins/ModuleConventionSelfTest.cs:193`) **FIXED 2026-09-29.** SelfTestOn enumerates GetMethods(Public|Static) for the exact SelfTest(out string) shape, so a sibling overload no longer throws AmbiguousMatchException into "none"; stage two collects every other IModule type's match and fails on more than one, naming them, as stage three did. TryFindSelfTest is internal and --module-host-selftest drives it against in-assembly fixtures (an overloaded module type resolves to its own exact method; a bare module type beside two SelfTest carriers is reported ambiguous naming both). The earlier record (host 1.2.4) claimed determinism the code delivered only in stage three; that record stands as history, this is the fix. MUTATION: stage two first-match FIRED; GetMethod restored FIRED.
- ✅ **F341** [low/correctness] ConventionHost and PetStudio's fake return null from GetStorage/GetSettings; the shipped host never does and the contract is silent, so a contract-conforming third-party Init fails --module-selftest while running fine in the app (`src/dotNet/Plugins/ModuleConventionSelfTest.cs:364`) **FIXED 2026-09-29.** IHost.GetStorage/GetSettings in PluginApi.cs now state the contract: never null from the shipped host (whatever the module declared), null possible from test doubles, so a module tolerates null and degrades to scratch space as ModulePaths already does. The convention runner names the loader's reason when it refuses a module and, for a NullReferenceException, points at this host's null storage and the contract. Source invariant asserts the reasons follow the acceptance check and name the cause. MUTATION: the reason loop removed FIRED. ConventionHost keeps returning null on purpose: it is the one gate exercise of every in-tree module's null tolerance.
- ✅ **F343** [low/correctness] MinHostVersion refusals are keyed by Info.Id while the pane matches failures by folder name (`src/dotNet/Plugins/ModuleHost.cs:93`) **FIXED 2026-09-29.** The MinHostVersion refusal is filed under the folder name like every other failure, with the declared id appended to the reason when it differs. --module-host-selftest copies testmodule into a folder named away from its id and asserts the refusal is found by the folder name with the id in the reason. MUTATION: Info.Id restored as the key FIRED.
- ✅ **F344** [low/correctness] A third-party module whose Init throws after contributing keeps its tray items, panes, event handlers and responders live while the Modules pane reports it failed; no in-tree module can reach this (`src/dotNet/Plugins/ModuleHost.cs:109`) **FIXED 2026-09-29.** CompanionHost keeps an Init ledger between BeginModuleInit and EndModuleInit: tray items, panes (with their owner rows), responder and hotkey registrations and event subscriptions made inside the window are recorded, and the six module-facing events are explicit accessors over backing fields so a subscription can be undone. ModuleHost calls RollBackModuleInit instead of EndModuleInit when Init did not return, so the failed module holds nothing in the host. TestModule throws after contributing when DESKTOP_AI_COMPANION_TESTMODULE_THROW_IN_INIT=1 (a self-test switch); --module-host-selftest loads it healthy (witness: tray items, a pane, a subscription) and then failing (zero of each, the failure recorded, the Init window closed). MUTATION: RollBack replaced by End FIRED.
- ✅ **F345** [low/correctness] PaneAttribution runs six storage-touching module Inits against the gate exe's own data root (the user's live root only when the flag is run against the installed exe), with no DESKTOP_AI_COMPANION_DATA_ROOT isolation (`src/dotNet/Plugins/ModuleHostSelfTest.cs:84`) **FIXED 2026-09-29.** ModuleHostSelfTest.Run sets DESKTOP_AI_COMPANION_DATA_ROOT to a fresh SelfTestScratch root before anything touches AppPaths and asserts AppPaths.DataRoot IS that root (not merely that an override is set, which the gate's own isolation would satisfy), restoring the variable and releasing the root in the finally. MUTATION: the SetEnvironmentVariable removed FIRED.
- ✅ **F353** [low/leak] Staged update payloads survive the discard and empty-payload paths (bounded to one per module id, not accumulating) and a failed .replaced delete is never retried; the self-test does not check the staging root (`src/dotNet/Plugins/PendingModuleUpdates.cs:125`) **FIXED 2026-09-29.** ProcessPending deletes the staged payload when it was applied OR known useless (`swapped || discard`, set in the no-longer-installed and empty branches), and SweepStrands runs on every launch, marker or none: an `<id>.replaced` whose install folder is back is removed, an unmarked `<id>.staged` older than AbandonedStagingAge (1 h) is removed, a fresh unmarked one and a `.replaced` with no install folder are left alone. FactoryReset wipes PendingModuleUpdates.DefaultStagingRoot as well. --module-host-selftest asserts each of those on throwaway roots; a source invariant pins the reset's wipe. MUTATION: discard flag removed FIRED; sweep call removed FIRED; reset's wipe removed FIRED (gate).
- ✅ **F354** [low/correctness] Isolating copies are top-level only, so --petstudio-selftest runs a module without its shipped native\ folder (`src/dotNet/Plugins/PetStudioModuleSelfTest.cs:41`) **FIXED 2026-09-29.** SelfTestScratch.CopyTree copies a bundled module with its subfolders, and the three isolating self-tests (petstudio, aibrain, the convention runner) use it. --petstudio-selftest asserts the shipped payload carries native\ (witness) and the isolated copy carries it with the same file count. MUTATION: the top-level copy restored in PetStudioModuleSelfTest FIRED.
- ✅ **F282** [low/race] Tray click/double-click and the Test Speech/Exit menu handlers dereference Program.Mainthread unguarded; the icon is live from StartUp.cs:~222 but nothing shipped pumps before Program.cs:309 assigns it (latent: needs a third-party module that runs a modal loop in Init) (`src/dotNet/Program.cs:302`) **FIXED 2026-09-29.** Ni_MouseClick, Ni_MouseDoubleClick and the Test Speech handler guard Program.Mainthread the way their neighbours do; Exit_Click falls back to Application.Exit() when there is no StartUp, so Exit is never a silent no-op. Source invariants assert the guards and the fallback. MUTATION: the click guard removed FIRED.
- ✅ **F286** [low/performance] Launch update checks download catalog.json up to three times per due week (two within a second, one at two minutes) and pane Check now drops the shared copy without refilling it; the file is ~89 KB, not ~300 KB (`src/dotNet/RemoteCatalog.cs:161`) **FIXED 2026-09-29.** RemoteCatalogClient's shared copy is the RAW BYTES with the parse hung off them: FetchAppVersionAsync reads the shared bytes and still parses only its block; FetchSharedAsync parses the shared bytes and keeps returning a completed task from inside the lock on a warm cache (the pane wiring the existing invariant pins); the launch's pet check and module scan use FetchSharedAsync; both panes' check-now buttons call RefreshSharedAsync, which drops the copy and refills it. Within one 90 s window the launch's due checks download catalog.json once instead of up to three times; the check two minutes later is outside the window and still fetches, as designed. Source invariants assert each call site. MUTATION: FetchAppVersionAsync downloading for itself FIRED; the Companions check dropping without refilling FIRED. The existing "drops the shared catalog" invariant was widened to accept the refill call.
- ✅ **F305** [low/correctness] When the configured pet is rejected and the built-in is staged instead, PetTypeId and the scale factor still carry the rejected active id (`src/dotNet/StartUp.cs:205`) **FIXED 2026-09-29.** On the fallback branch activeId becomes the built-in id and the scale factor is recomputed for it before the built-in is staged, so PetTypeId, per-pet size, mute and speech routing follow the sheep that is running; the rejected XML is left in settings unpersisted (SetXml skipped on that branch) so a host that later accepts it brings it back, as the persist-failure warning already promised. Source invariant asserts the rekey precedes the PetTypeId assignment and the conditional persist. MUTATION: the rekey removed FIRED.
- ✅ **F307** [low/leak] SpawnPreviewPet orphans its guid-keyed registry entry if AddSheepCore throws (exception path only; the null return is handled) (`src/dotNet/StartUp.cs:601`) **FIXED 2026-09-29.** SpawnPreviewPet wraps AddSheepCore in try/catch that drops the registry entry and rethrows; CompanionHost.SpawnPreview already turns the rethrow into the module's error string. Source invariant asserts the catch. MUTATION: the catch removed FIRED.
- ✅ **F308** [low/performance] ReloadPetType performs N+1 synchronous full-document settings.json writes (~130 ms each measured) on the UI thread for one reload of N pets; N of them persist a shrinking transient mix (`src/dotNet/StartUp.cs:714`) **FIXED 2026-09-29.** KillSheep persists the mix only when `!reloadInProgress`, so ReloadPetType's N closes no longer write N shrinking transient mixes; the reload's own PersistMix at its end is the one write. Source invariant asserts the CONDITION on the KillSheep line, not the presence of PersistMix. MUTATION: the condition reverted FIRED.
- ✅ **F309** **FIXED 2026-09-29.** NoteFullscreenScan posts the FullscreenChanged raise to the UI thread's captured SynchronizationContext when the scan ran on another thread (inline on the UI thread, so the companions' 300 ms cycle keeps its order), and IsFullscreenActive's ABI comment now says it may scan when its cache is stale and is UI-thread-only. Source invariant asserts thread test, Post and inline raise in that order. MUTATION: removing the Post block FIRED.
- ✅ **F312** [low/race] During KillSheeps' 2.2 s exit window, LoadNewXMLFromString stops timer1 and then throws from ApplyTrayIcon on the disposed tray icon (inside its own catch), so the pending Application.Exit is silently abandoned and the process is left alive and unreachable; AddPetFromTray in the same window collapses the persisted mix (`src/dotNet/StartUp.cs:1373`) **FIXED 2026-09-29.** `shuttingDown` is raised as KillSheeps' first act, before the tray icon is disposed, and KillSheeps closes the open settings window; LoadNewXMLFromString returns false, AddSheepCore null (so tray Add and SpawnOne decline), SpawnPreviewPet null with "shutting down", ReloadPetType Deferred, and PersistMix is a no-op while it is set. ProcessIcon.SetIcon returns at once when the icon is disposed, so no post-dispose caller can throw from inside its own catch and abandon the pending Application.Exit. Source invariants pin the flag's position before pi.Dispose, the window close, each entry point's guard and SetIcon's guard. MUTATION: the flag removed FIRED; SetIcon's guard removed FIRED. Live check for the coordinator: Exit from the tray during a pet's kill animation, then double-click where the icon was; the process must be gone within ~2 s.
- ✅ **F316** [low/correctness] WPF self-test probe 12b mkdirs and recursively deletes <real DataRoot>\modules\alpha and \beta on every run (test writes into the live data root; destructive only if a module with id alpha or beta exists), and its 'nothing is created on disk' comment is false (`src/dotNet/WpfOptionsSelfTest.cs:980`) **FIXED 2026-09-29.** Probe 12b keeps the RevealRootFor shape assertions as pure strings and runs the containment decision against a GUID scratch tree standing in for the data root; while its tree is on disk it asserts nothing named like the probes appeared under the live data root (with a pre-run witness that nothing was there), and the finally deletes the scratch tree plus only what THIS run created. Comment corrected. MUTATION: pointing the probe back at RevealRootFor("alphaprobe") FIRED on "never under the live data root".
- ✅ **F318** [low/performance] Sprite sheet and icon are base64- and PNG-decoded twice per staged pet (three times on a Companions-pane update), on the UI thread: measured ~80 ms extra on the largest shipped pet, ~20 ms mid-size, ~1 ms for the built-in (`src/dotNet/Xml.cs:183`) **FIXED 2026-09-30.** CompanionXmlValidator.TryParse gained an additive overload that hands back the sprite-sheet and icon bytes it decoded and proved, and Xml.TryReadXml reads those instead of running a second base64 pass over the same string; Xml.DecodeBase64 is gone. A second additive overload, `Xml.TryReadXml(string xmlText, bool stageImages, out string error)`, adopts a definition with no sprite decoded (frame size read from the PNG IHDR, SpriteCount 0) for a reader that only needs the graph; PetStudio's F155 can call it. Runtime probes in --hardening-selftest (the no-stage read adopts, decodes none, and sizes the frame as the staged decode does; the IHDR reader); source invariant asserts the validator's bytes are the argument and no FromBase64String remains in Xml.cs. MUTATION: no-stage path disabled FIRED (self-test harness); a base64 decode re-added FIRED (hardening harness). The finding's 80 ms was the second decode, which no longer runs; not re-timed.
- ✅ **F321** [low/correctness] classify-corpus.py infers the schema per row from the field count, so a legacy-v1 or 3-field row whose text contains tabs is silently reinterpreted and truncated (v2 rows are rejected correctly; not reachable from the current build path) (`src/Fortunes/classify-corpus.py:188`) **FIXED 2026-09-30.** classify-corpus.py fixes the field layout from row 1 (3, legacy 5 or schema-v2 6) and rejects a later row whose count differs, naming the line, so a tab inside a text is an error rather than a silently truncated row. `--selfcheck` proves a clean 3-field file classifies to 5 fields and a row with tabs in its text is rejected with the file left byte-identical; label-selftest.sh runs it. Source invariant asserts the once-per-file rule and the absence of per-row inference. MUTATION: the rule disabled FIRED (hardening harness); the selfcheck's hand mutation is recorded in the commit.
- ✅ **F363** [low/correctness] On a failed durable write the per-pet controls keep displaying the value that was not saved while the status says 'unchanged' (`src/Portable/Wpf/CompanionsPaneControl.cs:388`) **FIXED 2026-09-29.** The sound link, the size slider (thumb and readout) and the screen combo follow the store after the read-back: `enabled = stored` before the link text is written, slider.Value and the readout put back to the stored percent behind a `reverting` flag, box.SelectedIndex put back to the stored screen behind `syncingBox`. Source invariants assert the ORDER (read-back, then control) in each handler and the flags' presence. MUTATION: `enabled = stored` removed FIRED; the combo revert removed FIRED. Live check for the coordinator: none possible without a read-only store; the headless path is the invariant.
- ✅ **F364** [low/correctness] Size slider's ValueChanged overwrites the 'Couldn't save the size' status one line after persistPending writes it, so the read-back check never shows on the keyboard/click path (`src/Portable/Wpf/CompanionsPaneControl.cs:481`) **FIXED 2026-09-29.** persistPending records `storeTookIt` from its read-back; ValueChanged writes the success-shaped line only when it is true, so the failure line survives on the keyboard and track-click path (the drag path was already ordered right and is untouched: `if (!dragging) persistPending();` stands). Source invariant asserts persist < verdict test < success line inside BuildSizeRow. MUTATION: the verdict test removed FIRED.
- ✅ **F365** [low/leak] Built-in eSheep card re-creates and never disposes a System.Drawing.Icon on every Reload because LoadThumb caches the zip miss and LoadAppIcon is uncached (`src/Portable/Wpf/CompanionsPaneControl.cs:1105`) **FIXED 2026-09-29.** LoadAppIcon disposes the resource Icon (a fresh HICON per read of Properties.Resources.icon) in a using; LoadAppIconCached stores the frozen result under CompanionCatalog.BuiltInPetId, replacing LoadThumb's cached miss, and BuildCard reads through it. Source invariants pin the using and the cache key. MUTATION: BuildCard calling LoadAppIcon directly FIRED; the using removed FIRED.
- ✅ **F366** [low/correctness] Two download paths still swallow OperationCanceledException silently; the update path also strands a half-extracted staging folder (`src/Portable/Wpf/ModulesPaneControl.cs:362`) **FIXED 2026-09-29.** UpdateModuleAsync says "Stopped updating <name>." and FetchPetAsync "Stopped updating/downloading <name>." on cancel; UpdateModuleAsync, the reinstall branch and the new-install branch track the staging folder they own (`stagedHere`, nulled once MarkForUpdate or the move hands it on) and DiscardStaged deletes it in both catches, whether or not the pane is still loaded, since the Unloaded cancel strands it the same way. MUTATION: the update's catch made silent again FIRED; the companion download's catch made silent again FIRED.
- ✅ **F368** [low/correctness] Opening Settings by title while the window is already up switches panes and discards the pane's unsaved edits without a prompt (`src/Portable/Wpf/OptionsShell.cs:41`) **FIXED 2026-09-29.** OptionsWindow.ShowPane(title, discardEdits) returns false and stays put while IsDirty (Apply visible and enabled) unless told to discard; OptionsShell.RedirectOpenWindow asks "Discard them and open <pane>?" and only Yes discards (decision in the register). --wpf-options-selftest edits a field headlessly and asserts the refusal, the no-op on the pane already showing, the unknown-title refusal and the discard-and-go witness. MUTATION: the dirty test removed FIRED. Live check for the coordinator: a module-update balloon clicked over a dirty Preferences pane shows the prompt and No keeps the edits.
- ✅ **F369** [low/correctness] Diagnostic-log setters in the Preferences Save discard the durable-write result, so a failed save of only those fields greys Apply out without the 'could not be saved' dialog (`src/Portable/Wpf/OptionsShell.cs:526`) **FIXED 2026-09-29.** The five diagnostic-log setters fold into ok like their neighbours, so a failed durable write reaches the existing dialog and Apply stays enabled; Configure still reads the store back. A ratio invariant in runtime-hardening-selftest.ps1 holds every data.Set* in the Preferences Save delegate to `ok &=` (22 of 22). MUTATION: one `ok &=` removed FIRED.
- ✅ **F371** [low/correctness] Reset to default settings also resets the dormant themeMode key, which has had no UI since 22a799ddf and is not named in the confirmation text (`src/Portable/Wpf/OptionsShell.cs:975`) **FIXED 2026-09-29.** Option (a): ResetToDefaultSettings no longer calls SetThemeMode and the "(theme applies on next open)" clause is gone; the plumbing stays dormant and no Theme control returns as a side effect (register). Source invariant asserts SetThemeMode absent from the method with SetAudioDeviceId as the witness. MUTATION: the line restored FIRED.
- ✅ **F373** [low/correctness] Footer update stamp can attach a second MouseLeftButtonUp handler, opening the releases page twice per click; the handler body is also duplicated verbatim (`src/Portable/Wpf/OptionsWindow.cs:176`) **FIXED 2026-09-29.** One MouseLeftButtonUp handler, attached in the constructor unconditionally, calls OpenReleasesPage, which re-reads the cached latest version at click time and opens nothing unless an update is on offer; RefreshUpdateStampAsync only restyles through MarkAsUpdateLink. Source invariants: exactly one attach site in the file, none inside the refresh, and the offer test before Process.Start. MUTATION: a second attach in the refresh FIRED.
- ✅ **F376** [low/correctness] Reveal containment compares the reparse-resolved file path against the unresolved permitted root, so a data root reached through a junction, symlink, SUBST, mapped drive or UNC path refuses every in-root reveal; latent in the shipped product because the only RevealsPath consumer (AgentFlow 'Open the log') is already refused by the logical check, since the narrowing put <dataRoot>\diagnostics.log outside its module root (`src/Portable/Wpf/OptionsWindow.cs:1238`) **FIXED 2026-09-29.** ResolveRevealTarget resolves the permitted ROOT through the same GetFinalPathNameByHandle path as the file (FinalDirectoryPath: CreateFileW with FILE_FLAG_BACKUP_SEMANTICS, since File.OpenHandle refuses a directory and Directory.ResolveLinkTarget resolves only a trailing link) and compares like with like; a root that cannot be resolved refuses. --wpf-options-selftest mklinks a junction to the probe root and asserts a file reached through it is allowed while the escape junction is still refused. MUTATION: comparing against the unresolved root again FIRED. The masking regression the verifier found (AgentFlow's "Open the log" refused since the narrowing) is filed as N-host-03 below.
- ✅ **F377** [low/correctness] Dark-theme ScrollBar template is vertical-only while the nav ListBox keeps WPF's default horizontal Auto; a third-party pane title wider than ~170 px would render a broken 12-px bar (no shipped title reaches it) (`src/Portable/Wpf/WpfTheme.cs:102`) **FIXED 2026-09-29.** ScrollViewer.HorizontalScrollBarVisibility is Disabled on the nav ListBox; entries are TextBlocks with CharacterEllipsis trimming and the full title as tooltip and automation name; the WpfTheme comment names the nav as the one ScrollViewer it had to cover. --wpf-options-selftest asserts the visibility and the entry shape on a title long enough to overrun the column. MUTATION: the Disabled removed FIRED.
- ✅ **F325** [low/performance] XmlToDot (debug-window Convert to DOT, SHIFT-start only) builds its output with quadratic string concatenation, formats a discarded Console line per sequence edge, and emits animation names unescaped in DOT labels (`src/Tools/XmlToDot.cs:35`) **FIXED 2026-09-30.** XmlToDot builds into a StringBuilder, writes no Console line, guards a null Sequence/Border/Gravity and a zero probability total, and EscapeLabel escapes backslash, quote and newline in every label. The probe exports a name with a quote and a backslash and asserts the escaped label and the absence of the raw one. MUTATION: the name emitted unescaped FIRED in both harnesses.

Info, disposition only:

- ✅ **F254** [info/performance] XSD schema set is recompiled on every TryParse (about 0.95 ms per call); recorded as DECLINED-MEASURED (`src/dotNet/CompanionXmlValidator.cs:397`) **DECLINED-MEASURED 2026-09-30.** 0.95 ms per TryParse, measured warm over 30 reps (the post-mortem line further down this file). TryParse runs per install, download, drop or Studio analyze, not per frame, so a cached XmlSchemaSet would save under a millisecond on a user-paced path and add a shared mutable object to a class that is otherwise stateless. Register.
- ✅ **F267** **ACCEPTED-RECORDED 2026-09-29.** The un-cached spawn check is a recorded, mutation-tested decision (it removes the spawn flash), shipped content spawns at most two children per animation, so the repeat is at most three walks per spawn event and never per tick; recorded under fix/host in the register.
- ✅ **F271** [info/performance] ReadBoundedPetXml allocates a 12 MiB buffer per drag-drop regardless of file size (`src/dotNet/FormCompanion.cs:2345`) **FIXED 2026-09-30.** ReadBoundedPetXml reads through ReadBoundedBytes, whose buffer is BoundedReadCapacity(stream): the file's length plus one sentinel byte, clamped to one over the limit, floor 4 KB, growing if the file grows while it is read; the over-limit test on the total stays. --hardening-selftest COUNTS the allocation through GC.GetAllocatedBytesForCurrentThread: a 2 KB drop allocates under 1 MiB (was 12 MiB), a file over the limit is refused, and a stream that grows past the limit while read is still caught by the sentinel. MUTATION: the fixed 12 MiB buffer restored FIRED in both harnesses.
- ✅ **F276** [info/performance] Reposition queries GetDpiForWindow and constructs a fresh Screen (about 63 us measured warm on .NET 10) on every pet tick while a bubble shows, before its no-op check (`src/dotNet/FormSpeech.cs:212`) **ACCEPTED-RECORDED 2026-09-29.** 63 us warm per pet tick while a bubble shows, measured by the finder; the finding itself says "if ever touched". Not touched.
- ✅ **F329** [info/performance] CaptureScreenContext reads the foreground window three times and opens the foreground process twice per AI ask or fortune pick; the Snapshot entry already carries the name (`src/dotNet/Plugins/CompanionHost.cs:313`) **FIXED 2026-09-29.** CaptureScreenContext takes ProcessName from the snapshot entry flagged IsForeground, which already resolved it, and falls back to ActiveWindow.ProcessName() only when no entry is flagged (own window, cloaked or shell foreground, past the cap), preserving the blank-for-own-process answer. Source invariant asserts the capture precedes the use and the old bare read is gone. MUTATION: the bare read restored FIRED. Not measured: two fewer handle opens per ask is the whole saving.
- ✅ **F330** [info/performance] CompanionHost.GetSettings parses the settings file on every call; only BlinkingLed (two parses per tray open) and Fortunes (three GetSetting calls) fetch repeatedly, the other four modules hold one handle (`src/dotNet/Plugins/CompanionHost.cs:387`) **ACCEPTED-RECORDED 2026-09-29.** No host change: memoising GetSettings would share one mutable dictionary across the independent handles PluginApi.cs documents, including ones read off the UI thread. The cost is user-paced (tray open, pane load, Init) and unmeasured because nothing has shown it; BlinkingLed's per-menu double parse and the template's per-call pattern are module-side and belong to those lanes if a measurement ever calls for it. Register.
- ✅ **F335** [info/race] CompanionHost._catalogCache is a benign unsynchronised publish; worst case is a duplicate catalog fetch (`src/dotNet/Plugins/CompanionHost.cs:623`) **FIXED 2026-09-29.** `_catalogCache` is volatile and its comment records the accepted worst case (two concurrent browses both fetch, second write wins), matching RemoteCatalogClient.FetchSharedAsync. Source invariant pins the declaration. MUTATION: volatile removed FIRED.
- ✅ **F342** [info/correctness] ModuleHost refuses a module wholesale when any type in its assembly fails to load (unrecorded fail-closed policy); ModuleConventionSelfTest carries an unreachable partial-load catch for the same case (`src/dotNet/Plugins/ModuleHost.cs:71`) **ACCEPTED-RECORDED 2026-09-29.** Fail-closed stays: a module any of whose types fails to load is refused whole, which is the safer policy while every module is first-party and is now recorded in the register beside the isolation promise. The unreachable ReflectionTypeLoadException catch in ModuleConventionSelfTest.TryFindSelfTest is deleted so the two files agree (source invariant pins its absence).
- ✅ **F356** [info/correctness] SelfTestScratch's dev/CI-only sweep deletes any dp-* directory older than an hour in the current user's %TEMP%, whichever application created it; the comment claims the prefix as the app's own (`src/dotNet/Plugins/SelfTestScratch.cs:79`) **ACCEPTED-RECORDED 2026-09-29.** The SelfTestScratch comment now says the sweep will also remove another program's dp-* directory older than an hour in the same user's %TEMP%, and why that is accepted: the sweep runs only under a self-test flag on developer and CI boxes, and lengthening the prefix would re-couple cleanup to a naming convention across some twenty creators, any one missed leaking forever, which is the orphan failure the sweep fixed. Register.
- ✅ **F297** [info/performance] SecureDownload builds a new HttpClientHandler and HttpClient for every download (`src/dotNet/SecureDownload.cs:184`) **ACCEPTED-RECORDED 2026-09-29.** One HttpClient per download, at a frequency of a few downloads per week; the finding calls leaving it defensible, and pooling would have to keep redirects and cookies off and the deadline overload intact for four SecuritySelfTest sections. Left as is.
- ✅ **F310** **FIXED 2026-09-29.** The getter stamps _fullscreenScanUtc before it walks, as BlockedMonitorsForStandDown does; trivial and adjacent to F309. Source invariant asserts the stamp precedes the walk. MUTATION: removing the stamp FIRED.
- ✅ **F317** [info/correctness] TryReadXml writes usesAlpha before its commit point, violating its own atomicity contract (`src/dotNet/Xml.cs:179`) **FIXED 2026-09-30.** usesAlpha is assigned in TryReadXml's commit block after DisposeAssets; ReadImages hands it out as a local. Source invariant asserts the order and that ReadImages assigns no field. MUTATION: assignment moved back before DisposeAssets FIRED.
- ✅ **F323** [info/correctness] 'bylines stripped' also counts rows that were then dropped as short fragments, so the two summary figures overlap (`src/Fortunes/strip-authors.py:184`) **FIXED 2026-09-30.** strip-authors.py counts a byline as stripped only after the drop test, so stripped + dropped no longer overlap; `--selfcheck` asserts (stripped, dropped, kept) = (1, 1, 2) on a three-row fixture and label-selftest.sh runs it. Source invariant asserts the count follows the drop test's continue. MUTATION: the count moved back FIRED (hardening harness); the selfcheck's hand mutation is recorded in the commit.

Found by lane fix/host while working the list above:

- ✅ **N-host-01** [info/correctness] MatrixDesktop's "Matrix Digital Rain" window (6000x1440 at 0,0, both monitors) made FullscreenScan hide every companion, in the installed 1.2.6 and in the build (`src/dotNet/FullscreenScan.cs:59`) **CLOSED-VERIFIED 2026-09-29.** Not a wallpaper and not a defect. `D:\.ai-work\projects\MatrixDesktop\MatrixDesktop\MainForm.cs` ApplyWindowModeAndBounds makes a borderless top-level Form whose Bounds is `SystemInformation.VirtualScreen`; the source has no SetParent, WorkerW or HWND_BOTTOM, so the window sits in FRONT of the desktop icons like a screensaver, launched by IdleLauncherTray after 15 minutes of idle and closed by the first physical keystroke (LowLevelKeyboardExit), which is why it had exited between two probes here. DesktopGeometry.IsFullscreenOnMonitor is true for every monitor it covers, so the stand-down is the designed behaviour, identical to a fullscreen video, and companions return within one 300 ms scan cycle of it closing. Decision recorded under fix/host in the register; nothing changed in FullscreenScan or DesktopWindows for it.
- ✅ **N-host-02** [low/correctness] A companion stood down for a fullscreen window still opened a speech bubble over the game: neither DefaultSpeaker nor CompanionHost.Say asked whether the pet was hidden, and a bubble is its own window that lands above a borderless game whatever its TopMost (`src/dotNet/FormCompanion.cs:2505`) **FIXED 2026-09-29.** SayWithDwell defers the latest line while hwndFullscreenWindow is set or the pet is hidden, and ClearFullscreenStandDown replays it when the monitor clears (decision in the register). Source invariant pins the guard's position, before the repeat guard and before any bubble exists. MUTATION: weakening the guard to the hidden flag alone FIRED. Found by F278's verifier.

- 📌 **N-host-03** [low/correctness] AgentFlow's "Open the log", the only shipped RevealsPath consumer, returns `<dataRoot>\diagnostics.log` (AgentFlowPane.LogPathFrom walks two levels up from the module's storage) while PermittedRevealRoot narrows an owned pane to `<dataRoot>\modules\agentflow`, so the button has been refused as "outside this app's data folder" on every machine since the narrowing landed; the narrowing's mutation tests never pressed the shipped consumer, and the reveal path has no live check in the smoke walk (`src/Portable/Wpf/OptionsWindow.cs` PermittedRevealRoot; `modules/AgentFlow/AgentFlowPane.cs:657`). Found by F376's verifier, confirmed by reading both sides: RevealRootFor("agentflow") is the module's storage directory and IsUnder(<dataRoot>\diagnostics.log, that) is false before FinalPath is ever reached. Cross-lane, so filed rather than fixed: the host can grant an owned pane a second permitted root for the app's own top-level log (a decision against the narrowing's recorded intent to stop reveals "in the app's own settings folder"), or AgentFlow's button can stop asking for a file outside its storage; and pressing "Open the log" belongs in SMOKETEST.md's walk either way.

### Lane fix/tools (Phase 2): ShimejiConvert and its engine (merges before PetStudio, which compiles these files)

20 items: 18 to fix or disposition, 2 info to disposition.

- ✅ **F429** **FIXED 2026-09-29.** `EmitCore` decides the wall region beside the ceiling: `wallReachable = anyLocomotion || (ceilingSpokes.Count > 0 && spokes.Any(Launches))`, and when false clears BOTH `wallSpokes` and `ceilingSpokes` before ids are handed out, so a skin with wall art and no Type="Move" floor action (and no jump into a ceiling) is ACCEPTED with the region left out instead of emitted unreachable; `BuildResidue` is now told what was emitted (two flags) and says "nothing can reach a wall" instead of "Wall climbing IS converted", with an accounting bucket for left-out region art (a Group1 GrabWall on such a skin used to print as UNACCOUNTED). Fixture `NoLocomotionWallActionsXml` in `EmitterSelfTest` asserts Accepted, no wall/ceiling animation, the residue wording and no UNACCOUNTED; WITNESS `JumpOnlyWallActionsXml` (no locomotion, an embedded Jump plus wall and ceiling art) must keep both regions and be Accepted. No format-ladder rung: every one of the 32 shipped converted pets carries a Walk. MUTATION: "the guard is never taken" FIRED (`left unreachable animations: 3,4,5`, not accepted, GrabWall/ClimbWall/HangCeiling emitted, residue claims wall climbing IS converted; the jump witness stayed green). Out-of-boundary half: PetStudio's status text "but the host would reject it" (`modules/PetStudio/PetStudioWindow.cs` `LoadConvertedIntoEditor`) is wrong for a Valid-but-unreachable result, since the host's validator accepted the XML.
- ✅ **F435** **FIXED 2026-09-29.** The audio allowance is the room the sheet left: `EmitCore` budgets each embedding against `XmlBudgetBytes - sheet.ProjectedXmlBytes` (plus `SoundMarkupAllowanceBytes` per node) and the validator's `MaximumAudioBytesPerSound` / `MaximumAudioBytesTotal`, skipping a clip that does not fit instead of emitting a document the validator refuses, and `AppendSoundResidue` counts the clips dropped for room ("left no room ... under the 12 MiB pet limit"). `SoundBaker`'s caps stay as a bound on transcoding work; its comment no longer claims to leave room for a sheet it knows nothing about. `EmitterSelfTest`: with the sheet's real projection four stub clips embed (WITNESS); with the projection set to leave room for two, exactly two embed, projection plus audio stays under the budget, and the residue says why. No rung: no shipped converted pet carries a `<sound>` node. MUTATION: room reverted to a fixed 3 MiB FIRED (`a sheet with room for two clips embedded 4`; `12036200 + 1092272 exceeds the 12582912 byte budget`; residue silent about room).
- ✅ **F444** **FIXED 2026-09-29.** `ShimejiPose.AnchorFollowsSprite` marks an anchor DERIVED from a declared size; `BundleParser` sets it on every bundle pose and `SpriteSheetBuilder.Build` re-derives such anchors as (width/2, height) of the bitmap it actually decoded, for every pose in its input (FrameKey carries the anchor, so the duplicates step 1 dropped must move too), before the content dedupe and the cell sizing read them. `BundleConverter` adds a residue note naming each sprite whose decoded size disagreed with the manifest. `BundleSelfTest` part 4 converts a bundle declaring 32x40 that ships a 24-row and a 48-row sprite: the cell must be 48 rows and the short, normal and tall sprites' lowest painted rows must all sit on one floor line; WITNESS: the uniform bundle gets no such note. No rung: none of the 5 disagreeing bundles is shipped, and a rung cannot move pixels. MUTATION: the flag no longer set by BundleParser FIRED (`cell is 40 rows tall`; `24-row sprite's feet are on row 19 ... floats above the floor`; `48-row sprite ... clipped`; residue silent).
- ✅ **F459** **FIXED 2026-09-29.** `SpriteSheetBuilder.ClampSheet` (now internal) applies the SMALLER of the two sheet ratios once instead of compounding both, and `Build` re-fits the SCALE when the rounded cell would land the sheet past 4096 (`scale = min(scale, floor(4096/tiles)/cell)` on the offending axis), so the sprites shrink with the cell rather than the cell being trimmed under them; nearest rounding is kept for every sheet inside the cap, so no shipped fractional-scale pet changes on re-conversion. `CompositorSelfTest` asserts the clamp arithmetic over a grid of 5 shapes x 3 scales (WITNESS: the clamp engages in more than half the cases) and builds 289 distinct 1x241 frames (17x17): the sheet must stay within 4096 on both axes, the first frame must reach its cell's bottom row, and WITNESS the clamp engaged on a 17x17 grid. No rung: the largest shipped converted sheet is 14x14 tiles at 3584 px, so the clamp never bound on anything shipped. MUTATION: ratios compounded again FIRED (`ClampSheet(0.5, cell 512x512, tiles 25x24) = 0.213333, expected 0.32`, and at scales 1 and 0.8505); post-round re-fit disabled FIRED (`sheet 17x4097 exceeds the 4096 px cap`).
- ✅ **F425** **FIXED 2026-09-29.** `wallEntry = wallSpokes.FirstOrDefault(WillClimb)` and `wallExit = wallSpokes.FirstOrDefault(e => !WillClimb(e))`, the predicate `BuildWallSpoke`'s border edges already used, so a synthesised climb (`ForcedVelY`) is the entry and never the exit. Assertions in `EmitterSelfTest`: on the flat-wall variant, Walk's only="vertical" edge targets the spoke carrying the only="horizontal" edge into the ceiling; on `JumpOnlyWallActionsXml` (GrabWall first, DescendWall second) the ceiling's only="vertical" exit is DescendWall. No rung: the synthesis path is unreachable on the shipped corpus (KinitoPET has a real ClimbWall). MUTATION: entry read from source velocity FIRED (`enters the wall on 'ClimbWall', not on the spoke that climbs to the ceiling (GrabWall)`); exit read from source velocity FIRED (`leaves the ceiling onto 'GrabWall'`).
- ✅ **F426** **FIXED 2026-09-29.** The same running total as F435 charges each EMBEDDING the emitter writes (a sounded spoke plus every chain step that replays it), not each distinct clip, so the `<sound>` nodes are bounded by what the document can carry. Fixture: Walk carries `Sound="/step.wav"` and is a member of two set-pieces, so one clip yields three embeddings (plus Stand's) and the near-cap run admits two of the four. Deduping by Source was rejected on purpose: it would mute every chain step of a sounded action, a loss the format does not require. No rung (no shipped sounds). MUTATION: covered by F435's leg, whose failing count (4 embedded where 2 fit) is exactly the per-embedding charge.
- ✅ **F427** **FIXED 2026-09-29.** `EmitCore` no longer re-validates its own output: `RoundTrips` is a FIXED-POINT check, `Serialize(reparsed) == EmittedXml` (ordinal), with a first-difference offset and window in `Error` when it fails; one extra Serialize replaces a second XSD compile, deserialize, base64 decode, full PNG decode and every sound's sniff per conversion. `ShimejiEngine.RoundTrips` stays for the CLI `verify` verb, where a hand-authored pet can genuinely fail it. `EmitterSelfTest`'s `if (!r.RoundTrips)` can now fail independently of Valid, which the mutation shows (`valid=True, roundtrip=False`). MUTATION: EmittedXml tampered after serialization (every `<offsety>0</offsety>` stripped; still schema-valid) FIRED on every Emit in the suite (`not a fixed point of parse -> serialize: at offset 6944 of 30923/32203 chars, emitted '<opacity>1</opacity>...' vs re-serialized '<offsety>0</offsety>...'`).
- ✅ **F428** **FIXED 2026-09-29.** One `PosesOf(ShimejiAction)` (= `VariantFor(a).Poses`) is the only reader of an action's poses: `BuildSpoke`, `BuildWallSpoke`, `BuildCeilingSpoke`, `IsLocomotion`, `ClimbsUpward`, `DescendsDownward`, `LaunchVelY`, `FirstSoundClip` and `HubImage` all go through it; the `Animations[0].Poses.Count == 0` emptiness guards stay. Fixture: StandAndWatchMouse's catch-all is two frames at 10 ticks and its 2-tick conditional variant carries the only Sound; `EmitterSelfTest` asserts the emitted interval is 400/400 ms (WITNESS: the variants' durations differ) and, through a clip-recording loadSound, that /look.wav is never requested while /beep.wav is. No rung: 0 multi-frame faceCursor animations among the 32 shipped converted pets (names counted over Companions/ 2026-09-29), and the catch-all's timing is not recoverable from emitted XML anyway. MUTATION: `BuildSpoke` reading Animations[0] FIRED (`plays its catch-all frames at 80/80ms`); `FirstSoundClip` reading Animations[0] FIRED (`/look.wav ... was requested`).
- ✅ **F431** **FIXED 2026-09-29.** `ExpandSetPieces` seeds `alreadyEmitted` with `CollapsedSources` as well as the surviving spokes, so a member merged by `CollapseDirectionPairs` counts as converted rather than withheld and an unplayed sequence naming it no longer chains. Fixture: `WalkBack` (Walk mirrored over the same frames) plus the unplayed `StrollBack` sequence; `EmitterSelfTest` asserts no `StrollBack_` animation, WalkBack listed under "Merged into an identical sibling", and WITNESS that WalkBack itself collapsed. No rung: 0 `<seq>_<n>_<member>` chain steps in the 32 shipped converted pets (counted 2026-09-29). The moonwalk half (a PLAYED sequence's collapsed rightward leg keeps +VelX over unmirrored art) needs a loser-to-survivor map and is filed as N-tools-01. MUTATION: `UnionWith(CollapsedSources)` removed FIRED (`'StrollBack' ... yet it was chained (StrollBack_1_WalkBack, StrollBack_2_Bounce)`; `does not list 'WalkBack' under 'Merged into an identical sibling'`).
- ✅ **F436** **FIXED 2026-09-29.** `ConvertSkin` builds a `SoundBaker` only when `ShimejiEngine.HasSoundedPose(config)` (some pose names a Sound), so a silent skin never probes for ffmpeg; the residue is unaffected because `AppendSoundResidue` says nothing when no animation wanted a clip. `SoundResolveSelfTest` converts a real two-sprite silent skin through `ConvertSkin` and asserts `SoundBaker.TranscoderProbes` did not move, with `HasSoundedPose` asserted both ways (WITNESS: one Sound attribute makes the skin sounded). MUTATION: gate reduced to `!bundled` FIRED (`converting a skin with no Sound attribute probed for ffmpeg`).
- ✅ **F437** **FIXED 2026-09-29.** `SoundBaker` keeps a second memo set `_overBudget` beside `_unusable`: a clip that transcoded but would overshoot the total cap is remembered (the total never decreases, so the answer is fixed for the conversion) and checked before the transcoder runs. The transcoder is an injected delegate (`SoundBaker.WithTranscoder`; ffmpeg by default), which is what lets the self-test drive Bake's memo logic without ffmpeg: `a.wav` (600 bytes) accepted, `b.wav` (600, over a 1000-byte total) refused and requested three times must transcode once; WITNESSES that an accepted clip and a per-clip-cap refusal transcode once too. MUTATION: `_overBudget.Add` removed FIRED (`transcoded 3 times over three requests`).
- ✅ **F438** **FIXED 2026-09-29.** `Resolve` enumerates with `EnumerationOptions { RecurseSubdirectories, IgnoreInaccessible = true, AttributesToSkip = 0, MatchCasing = CaseInsensitive }` instead of the legacy overload (which maps to `IgnoreInaccessible = false`); any other scan fault sets `ScanFaulted`, which `ConvertSkin` turns into a residue note that a clip reported missing may exist. `SoundResolveSelfTest` puts a Deny-ListDirectory ACE for the current user on a sibling directory (lifted in a finally; a box that cannot set it prints DEGRADED instead of passing in silence), WITNESSES that listing it throws, and asserts the nested clip is still found with no fault. MUTATION: legacy overload restored FIRED (`the scan aborted on the first inaccessible subdirectory`; `reported a fault`).
- ✅ **F439** **FIXED 2026-09-29.** `TranscodeWithFfmpeg`'s timeout branch waits `p.WaitForExit(2000)` after `Kill(true)` before returning into the `finally` that deletes the temp MP3, so the delete runs on a released file instead of racing ffmpeg's handle teardown (the record measured 20/20 partials left with a plain Kill and 0/20 with a wait). No self-test: the branch is reached only by a transcoder hung for 30 s, which the gate cannot pay and no argument-shaped stub can fake while holding the output file; recorded in the register. The two existing hardening invariants over Engine.cs (concurrent drains, `Kill(true)` count) still hold.
- ✅ **F440** **FIXED 2026-09-29.** `PetGraph.Analyze` roots the magic entry points the way the host binds them: the LAST exact-name match per reserved name (Xml.cs's switch: no Trim, no case folding), then for fall and drag only the runtime's fallback (`Animations.ResolveMagicAnimations`: the lowest id whose name contains the word ignoring case, else the lowest id); kill and sync have no fallback. New `PetGraphSelfTest` (in-memory DTOs, six cases, registered in the csproj and `EngineSelfTest`): a 'Kill' with no inbound edge is unreachable while an exact 'kill' is a root (WITNESS); a 'Fall' beside 'fall' is unreachable; a lone 'Falling' is a root and reachable; a nameless orphan stays one and the drag fallback roots the lowest id, not it; two exact 'fall's leave the first unreachable; a padded ' fall' is bound by the contains fallback while a padded ' kill' is an orphan. Converted pets were never affected (exact lowercase names; `SanitizeName` suffixes collisions) and PetStudio's map uses `AnimationReachability`, not this. MUTATION: trimmed case-folded rooting restored FIRED (cases 1, 2, 5, 6: `'Kill' ... was not reported unreachable`, `'Fall' beside an exact 'fall'`, `two exact 'fall's`, `' kill' (padded)`); the fall/drag fallback removed FIRED (`'Falling' is not a root although the runtime binds it as the fall`).
- ✅ **F441** **FIXED 2026-09-29.** `ShimejiParser.SubtreeBlob` holds EXPRESSION text only: every Condition value plus any attribute value carrying `${`/`#{`; names, image paths, sound paths and enum values are left out, so a sprite called cursor*.png no longer reads as a cursor condition. Both readers (`ActionClassifier.Has` and `PetEmitter.IsGazeAction`) share the blob and narrow together; every token the classifier looks for is expression vocabulary, and `BundledConfSelfTest` still pins the 91/54/31/6 census. Fixtures: `ClassifierSelfTest` G1_CursorArt (Image and Sound named after the cursor, no Condition) must be Group1, with G2_Cursor as the witness; `EmitterSelfTest` CursorHate (the Victim shape) must emit as a plain rest with no faceCursor tag, not be filed as degraded, and classify Group1. No rung: the only corpus instance (alan becker's Victim, 7 copies) is unshipped. MUTATION: blob reverted to every attribute value FIRED (`G1_CursorArt: expected Group1 but got Group2`; `tagged faceCursor for having "cursor" in its sprite FILENAME`; `filed as degraded`; `graded 'CursorHate' Group2`).
- ✅ **F449** **FIXED 2026-09-29.** The no-locomotion fixture's `flat` dictionary is declared before its `try` and disposed in a `finally`, matching `owned` and the compositor fixtures; it now also carries the wall and ceiling art the two F429 fixtures share. Fixture hygiene: nothing to mutate.
- ✅ **F457** **FIXED 2026-09-29.** `SoundBaker.WithoutTranscoder(root)` builds a baker that never probes (the public constructor still does, and counts it in `SoundBaker.TranscoderProbes`); `SoundResolveSelfTest` uses it for every baker and asserts the counter is unmoved across the whole test. MUTATION: the factory made to probe FIRED (`this test probed for ffmpeg 2 time(s)`).
- ✅ **F460** **FIXED 2026-09-29.** `WebPLoader` asks dwebp for `-pam` and copies the straight RGBA payload into the bitmap (R/B swapped; the header parsed and refused unless 8-bit RGB_ALPHA; sides bounded at 16384; payload length checked), so the WebP path neither PNG-encodes in dwebp nor PNG-decodes through WIC; the 30 s read bound stays, and PNG/JPEG still go through WIC. A property, not a number: the two codec passes no longer run; the record's 7-10 ms per cartoon sprite was the dwebp half alone and is not re-measured cold here (register). `BundleSelfTest` part 3 (the committed 4x4 alpha WebP) is the regression check. MUTATION: R/B swap dropped FIRED (`pixel (1,1) is A=255 R=40 G=40 B=200, expected opaque (200,40,40)`); `-pam` dropped so dwebp writes PNG FIRED (`dwebp did not write a PAM (no P7 magic)`).

Info, disposition only:

- ✅ **F430** **FIXED 2026-09-29.** `Emit`'s `finally` reassigns `HubSpokes`, `CollapsedSources` and `ExpandedSetPieces` to fresh empty instances before releasing the re-entrancy flag, so the last conversion's action graph is not rooted between conversions; `PetEmitter.RetainedConversionState` (internal; the three counts summed) is asserted 0 after the main fixture's Emit, whose chains and collapsed mirror fill all three during the emit. Trivial and adjacent to the emitter work, so fixed rather than only dispositioned. MUTATION: the three resets removed FIRED (`still holds 21 item(s) of per-conversion state`).
- ✅ **F451** **DECLINED-MEASURED 2026-09-29.** The whole `ShimejiConvert.exe selftest` verb, nine sub-tests with every fixture this campaign added, runs in 1.45 s wall-clock on this box (Measure-Command around the Release exe, one fresh process, 2026-09-29; 1.49 s before the campaign), so the N-decodes-for-one shape in `TileIsPainted`/`TileRowIsPainted` costs nothing worth a change. If the helpers are ever touched, decode the sheet once per Run and index a painted-tile table from both. Recorded in the register.

Found by the lane, not in its list:

- 📌 **N-tools-01** [low/correctness] A PLAYED sequence whose member was direction-collapsed builds that chain step from the collapsed member's own poses, so a rightward leg carries +VelX over the unmirrored left-facing art and moonwalks inside the run; fixing it needs `CollapseDirectionPairs` to return a loser-to-survivor map and `ExpandSetPieces` to substitute the survivor for the step (`tools/ShimejiConvert.Engine/Emit/PetEmitter.cs:2346` `Source = members[i]`)
- ✅ **N-tools-02** **FIXED 2026-09-29 (fortunes 1.0.12) by lane fix/fortunes; see its disposition text under the fix/fortunes section (commit and rollback retry transient faults).** Filed as [low/race] `--fortunes-engine-selftest` failed once in three runs on this box (the tools lane's first gate run, 2026-09-29) with `IMPORT FAIL portable replacement/rollback fallback` and `custom_import=FAIL` (kept markers `temp\tools\dp-str-ea11ec72f582\dp-filter-selftest.txt`), then passed run alone and in the second gate run. `TestPortableReplacementFallback` fault-injects `File.Replace` and asserts `!File.Exists(committed.BackupPath)` and `!File.Exists(temporary)` straight after a fallback copy and a delete under `%TEMP%\DesktopAICompanion-fortune-import-<guid>`, which a handle held for a moment by an on-access scanner or the indexer defeats (a Windows delete completes when the last handle closes). Not this lane's code; nothing in tools/ is on its path (`modules/Fortunes/engine/FortuneFileImporter.cs:877` and `:926`)

### Lane fix/remembrance (Phase 2-3)

14 items: 14 to fix or disposition, 0 info to disposition.

- ✅ **F168** **FIXED 2026-09-29 (remembrance 1.0.17).** `AudioRecorder.OpenCapture` constructs every capture with no SynchronizationContext current (saved, nulled, restored in a finally), so NAudio raises RecordingStopped on its capture thread and `Stop`'s wait is signalled within a buffer period instead of running to its 10 s bound per source; the shutdown branch logs `stopped on shutdown: ... (capture stop N ms, mix N ms)`. Reproduced on this box's real render endpoint first (context captured: 10 008 ms, handler never ran; nulled: 33 ms, handler on the capture thread). Assertions in `RecorderSelfCheck.Run`, driven by a `FakeCapture` that reproduces NAudio's context capture: `captures are constructed with NO SynchronizationContext current (BUG-009)` and `RecordingStopped is delivered on the capture thread, not posted to a dead message loop (Stop took 192 ms; capture stop 46 ms)`. MUTATION: the null-context line deleted -> `captures are constructed with NO SynchronizationContext current (BUG-009)` FAIL (FIRED); the timing log line reduced -> `the shutdown save logs how long the capture stop and the mix took` FAIL (FIRED). Post-mortem BUG-009 in `docs/ISSUES-post-1.0.0.md`, with the live check for a console session.
- ✅ **F169** **FIXED 2026-09-29 (remembrance 1.0.17).** `RenderKeepAlive` plays a `SilenceProvider` through `WasapiOut` on the loopback endpoint for the length of the capture (NAudio's documented workaround: loopback delivers nothing while no render stream is active), started before the capture and released with its source; a refused second stream is logged and the recording proceeds as before. Asserted through the recorder's `KeepAliveFactory` seam: `a loopback source gets a silent render stream in its own format; a microphone does not` and `the silent render stream is released with its source`. MUTATION: the keep-alive creation removed -> the first line FAIL (FIRED). The live alignment check (record 40 s with the system output idle for 20 s, then a tone) still needs the console session `BLOCKED.md` T25 needs; the decision against clock-padding is in `docs/DESIGN-REGISTER.md`.
- ✅ **F171** **FIXED 2026-09-29 (remembrance 1.0.17).** `CaptureStore.NamesThisModuleWrites` accepts `recording.system.wav` / `recording.mic.wav` inside a capture folder and `<baseName>.system.wav` / `.mic.wav` in the root, built from `AudioRecorder`'s own suffix constants; a start that fails half-way deletes its header-only scratch (`AudioRecorder.DeleteIfEmptyRecording`, WAV-parsed, never a file it cannot read) and removes the empty capture folder (`CaptureStore.TryRemoveEmptyCaptureFolder`). Four positive and four negative name fixtures, plus `...and the header-only system scratch it had already created is deleted` and the empty-folder pair. MUTATION, four cases: in-folder shapes removed -> `a system scratch track inside a capture folder IS ours to purge` FAIL; root shape removed -> `a flat-mode system scratch track IS ours to purge` FAIL; stub deletion removed -> the header-only line FAIL; folder removal disabled -> `WITNESS an empty capture folder from a failed start is removed` FAIL (all FIRED). The 1.0.11 changelog claim that Purge deletes the scratch files is true again (its garbled layout is F174, lane fix/deadcode).
- ✅ **F173** **FIXED 2026-09-29 (remembrance 1.0.17).** The map-reduce is `OllamaSummarizer.SummarizeWithAsync`, the model behind a delegate; when the fold stops at MaxReduceCharacters it appends `CoverageNote` (`[Covers parts 1-N of M ...]`) to the text and returns it as the message, which `WriteSummaryAsync` logs; a client timeout is reported as a timeout, not as the user cancelling. The assertions drive the real orchestration with a fake model: `a partial fold says which parts it covers, in the summary text`, `...and in the message the caller logs`, `a model that ran out of time is reported as a timeout, not as the user cancelling`, with the whole-fold and cancelled-token witnesses. MUTATION: the note append removed -> the text line FAIL; the catch reverted to "cancelled" -> the timeout line FAIL (both FIRED). Marker over hierarchical reduce: `docs/DESIGN-REGISTER.md`.
- ✅ **F176** **FIXED 2026-09-29 (remembrance 1.0.17).** A normal stop keeps its save in `_pendingSave` (completed once the captures are stopped and recording.wav is mixed, before whisper starts); `OnHostShutdown` and `Shutdown` call `FlushPendingSave`, which waits up to `SaveFlushBound` (2 min) and logs both the wait and the outcome; transcription is not waited for. Assertion `shutdown waits for a save still in flight rather than leaving it to die with the process (waited N ms)` in `SelfCheckStopPaths`, with the fake captures held on a gate that opens 300 ms into the wait. MUTATION: the flush dropped from OnHostShutdown -> that line FAIL, `waited 0 ms` (FIRED). The bound is recorded in `docs/DESIGN-REGISTER.md`.
- ✅ **F182** **FIXED 2026-09-29 (remembrance 1.0.17).** `Transcriber.WhisperTimeoutFor` scales the limit with the WAV's length read off its header (`TryReadDuration`, any PCM WAV): 4x the audio, never under 30 minutes, never over 6 hours; the kill message names the recording's length and the rule, and the stop path logs `transcribed <name>: N s of audio in M s` so the factor can be calibrated. Assertions `a short recording keeps the old 30-minute floor`, `WITNESS an hour of audio gets four hours, not thirty minutes`, the six-hour ceiling, the unknown-length fallback and `the audio length is read off the WAV header (2.00 s)`. MUTATION: the factor removed -> the four-hours line FAIL (FIRED). Factor reasoning in `docs/DESIGN-REGISTER.md`.
- ✅ **F183** **FIXED 2026-09-29 (remembrance 1.0.17).** `ReleasesPageUrl` is `https://github.com/ggml-org/whisper.cpp/releases` (the list; its comment says why /latest is asset-less for good) and the button's success text names `whisper-bin-x64.zip` from the newest bXXXX build and says the Latest entry carries no Windows zip. Assertions `the human releases link is the LIST, not /latest, which upstream leaves asset-less` and `...and the success text names the file to take from that list`. MUTATION: the constant set back to /latest -> the LIST line FAIL (FIRED). The shipped 1.0.16 payload carries the old string; the coordinator's republish lifts it.
- ✅ **F175** **FIXED 2026-09-29 (remembrance 1.0.17).** The shutdown branch of `StopRecording` announces nothing (no loop is left to speak through; it logs, with its own status text), the normal branch announces as before, and `Announce` / `PersistOnUi` wrap the `Post` itself in the try. Assertion `nothing is announced on the shutdown path, where no message loop is left to speak through` in `SelfCheckStopPaths`, with the normal path's announcement as the witness. MUTATION: Announce re-added to the shutdown branch -> that line FAIL (FIRED).
- ✅ **F177** **FIXED 2026-09-29 (remembrance 1.0.17).** With F181, one change: `TakeSnapshot` decides the path on the caller's thread and runs the capture and PNG encode on a pool thread behind an Interlocked single-flight gate (the `BeginVramProbe` shape), announcing through `Announce`; a second press during an encode is told `Still saving the last snapshot.` Assertions in `SelfCheckSnapshotOffThread` through the `SnapshotCapture` seam, a probe that holds the capture open: `the snapshot hotkey returns before the capture completes`, `the capture ran on a different thread from the hotkey`, the single-flight pair and the re-arm witness. MUTATION: `Task.Run` inlined -> the returns-first line FAIL (FIRED).
- ✅ **F178** **FIXED 2026-09-29 (remembrance 1.0.17).** `BuildOptionsPane_Schema` builds the two device fields from the saved name alone; the host's Load runs `RefreshDynamicOptions` before Schema is read on every pane build, so nothing Init enumerated was ever shown. `AudioDevices.EnumerationCount` counts real walks; assertions `Init enumerates no WASAPI endpoints` (the device cache dropped first, so a re-introduced call has to walk) and `WITNESS a pane build (Load) is what enumerates the endpoints`. MUTATION: the enumeration restored in Init -> `Init enumerates no WASAPI endpoints` FAIL (FIRED on the second run; the first run SURVIVED because the 1500 ms device cache, still warm from the link block, answered the mutated call, which is why the check now drops the cache first). A deletion, not background work: `docs/DESIGN-REGISTER.md`.
- ✅ **F179** **FIXED 2026-09-29 (remembrance 1.0.17).** The two naming fixtures are local instants (`new DateTimeOffset(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Local))`), so `NewCapture`'s `ToLocalTime` is the identity in every zone, and both checks assert the whole stamp (`Sprint Review - 2026-08-27 09-30-00`, `2026-08-27 09-30-00`) rather than the date prefix. A fixture correction: no mutation case, since the module has no time-zone logic to mutate and the assertions are stricter than before.
- ✅ **F180** **FIXED 2026-09-29 (remembrance 1.0.17).** Init no longer purges: the purge timer's first tick is `FirstPurgeDelayMs` (60 s) and `OnPurgeTick` re-arms it hourly, so a headless test process never purges while the app purges a minute after launch; the link block seeds its storage root as well. Assertions `Init starts no purge` (via `PurgesStartedForSelfTest`) and the witnesses `WITNESS the purge timer's first tick is a minute out, not an hour` / `WITNESS the first tick purges and re-arms the timer hourly`. MUTATION: `RunPurge()` restored in Init -> `Init starts no purge` FAIL (FIRED).
- ✅ **F181** **FIXED 2026-09-29 (remembrance 1.0.17).** Fixed with F177 (one change): the capture and encode run on a pool thread and the hotkey returns at once. Property stated, no number: nothing here was timed cold against the old variant. Assertions and MUTATION as F177 (FIRED).
- ✅ **F184** **FIXED 2026-09-29 (remembrance 1.0.17).** `WhisperInstaller.ResolveAssetAsync` runs under a linked token bounded by `LookupBound` (30 s) and reports the bound in its own words; `DownloadAsync` bounds the header phase the same way and re-arms `ReadIdleBound` (60 s) before every read, so the bound is on silence, never on the file, and a stall surfaces as a `TimeoutException` the pane renders; `SetUpWhisperAsync` runs under a module CancellationTokenSource cancelled at Shutdown instead of `CancellationToken.None`. Assertion `a release lookup that gets no answer gives up at its own bound, not the hour-long client timeout (271 ms)` against a handler that never answers, with the caller-cancellation witness. MUTATION: `CancelAfter` removed -> that line FAIL after the test's 10 s wait (FIRED).
- ✅ **N-remembrance-01** **FIXED 2026-09-30 (ModuleKit, lane fix/followups).** `RecordingHost` records `SaidLines`, `LoggedLines`, `OpenedLinks`, `BroadcastLines` and `SaidToCompanions` into private lists under one lock and hands each property out as a SNAPSHOT taken under that lock, so a module appending from a pool task cannot race a test thread's enumeration or Count; the property types stay `List<string>` so every existing assertion compiles, and `ClearSaidLines`/`ClearLoggedLines`/`ClearOpenedLinks` are the reset (a `Clear()` on a snapshot clears a copy, which is why Remembrance's two `OpenedLinks.Clear()` calls moved to `ClearOpenedLinks()`). Pinned in CoreTests ("ModuleKit recording host"): a view taken before an append keeps its count while a fresh read sees the append, and a pool thread appends 20,000 log lines while the test thread enumerates each snapshot, with the WITNESS that a snapshot taken while the writer is parked half way holds exactly the lines written so far. MUTATION "followups: RecordingHost hands out its live SaidLines list again", recorded in the commit. The Remembrance stop-path workaround (a queueing SynchronizationContext) stays; it is a test-side ordering that no longer carries the safety. The original record: `ModuleKit.Testing.RecordingHost` appended `SaidLines`, `LoggedLines` and `OpenedLinks` from whichever thread the module called on, with no lock, so a module that logs or speaks from a pool task raced a test thread reading the lists (`src/DesktopAICompanion.ModuleKit/Testing/RecordingHost.cs:414`). Found by lane fix/remembrance.
- 📌 **N-remembrance-02** [low/leak] `WhisperInstaller.DownloadAsync` deletes its `<file>.part` when its own bound fires but not when the CALLER's token is cancelled, which Shutdown now does through `_installCts`; the next attempt's `TryDelete(temporary)` collects it, so it is one stale partial download per cancelled install until then (`modules/Remembrance/WhisperInstaller.cs:675`). Found by lane fix/remembrance while bounding the download.

### Lane fix/blinkingled (Phase 2)

4 items: 4 to fix or disposition, 0 info to disposition.

- ✅ **F116** **FIXED 2026-09-29 (blinkingled 1.0.6).** `ScrollLockBlinker.Toggle()` returns whether Windows accepted the keypress (delivered through a new `KeypressSender` seam whose default is the real SendInput) and every writer of `_phaseOn` moves it only with the key: `BlinkOnce()` and the cadence `Tick()` flip on true only, and `Stop()` keeps the belief after a refused corrective toggle so the next Stop() or Start() retries. Asserted in the module self-test through the seam, on any machine: `a refused blink-once leaves the phase where it was`, `a refused cadence tick leaves the phase where it was and keeps the dark gap armed`, `a refused corrective toggle keeps the belief`, each beside a WITNESS for the accepted case. MUTATION (tests/mutate-selftest-guards.py): "a refused blink-once flips the phase again" FIRED; "a refused cadence tick flips the phase again" FIRED; "Stop() zeroes the belief after a refused corrective toggle" FIRED; the two F114 cases, re-pointed at the new Stop(), both FIRED. Post-mortem: BUG-011 in docs/ISSUES-post-1.0.0.md.
- ✅ **F115** **FIXED 2026-09-29 (blinkingled 1.0.6).** `Start()` reconciles the belief with the key before arming the timer (`_phaseOn = _phaseOn && ScrollLockReader()`, first interval taken from the phase) instead of zeroing it, so off -> "Blink once now" -> enable continues the cadence from the lit phase with no extra keypress; the 1.0.5 changelog and the Stop() comment now say the enable half landed in 1.0.6. Self-test: `Start() keeps the belief that it lit the key when the key still reads lit`, `arms the LIT phase's interval`, `without pressing the key again on enable`; WITNESS a key that reads dark is dropped; WITNESS a lit key the blinker never lit is not adopted. MUTATION: "Start() zeroes the phase against a key it lit again (the 1.0.5 shape)" FIRED; "Start() arms the dark gap whatever phase the key is in" FIRED; "Start() adopts a lit key it never lit" FIRED. The choice between continuing, clearing and adopting is recorded under `#### fix/blinkingled` in docs/DESIGN-REGISTER.md.
- ✅ **F110** **FIXED 2026-09-29 (blinkingled 1.0.6).** `OnCapsLockStop` saves first and logs one honest line either way (`saved it as off` / `the off was NOT persisted, so the next settings read will start it again`), its catch is `saved = false` and `Categorize` is gone with its only caller; `SetRateFromTray` and `SetEnabledFromTray` log `tray pick not persisted (...), so the live state is unchanged` on a false Save() before ApplyState (logging only: ApplyState reads the file and "disk wins"). Self-test, with `host.SettingsFor("blinkingled").FailSaves = true`: `a Caps Lock stop whose write fails says the off was NOT persisted, in its one line`, `a tray speed pick whose write fails is logged rather than silently doing nothing`, WITNESS a tray Off is logged too, WITNESS a successful pick logs nothing. MUTATION (tests/mutate-selftest-guards.py): "the Caps Lock stop reports 'saved' whatever Save() answered" FIRED; "a failed tray speed pick is silent again" FIRED; "a failed tray Off is silent again" FIRED.
- ✅ **F113** **FIXED 2026-09-29 (blinkingled 1.0.6).** The self-test makes exactly two real keypresses (the wiring check and its pair) and every other probe delivers through the `KeypressSender` seam; `ScrollLockBlinker.RealKeypressCount` counts real SendInput calls and the suite asserts an even number (`the suite's real keypresses are paired (2 made)`), with a new `the real SendInput call completes without throwing` on the one real call. Measured on this box: the HEAD suite left Scroll Lock ON from OFF and ON from ON; the 1.0.6 suite leaves it OFF from OFF. MUTATION: "the self-test's pairing keypress is deleted" FIRED (`paired (1 made)`); "the SendInput P/Invoke resolves against a DLL that does not exist" FIRED.
- 📌 **N-blinkingled-01** [low/correctness] A Scroll Lock the USER had lit before enabling the blinker runs the cadence inverted (Start() does not adopt it, by the Stop() design rule) and Stop() then leaves it wherever the cadence landed, lit for the dark-gap share of the cycle (75% on Normal), so Readme.md:359 "stopping always leaves the light off" holds only for a key the module lit; the fix is variant (C) under `#### fix/blinkingled` in docs/DESIGN-REGISTER.md (adopt the key in Start(), which lets Stop() clear a key the module did not light), an owner decision (`modules/BlinkingLed/engine/ScrollLockBlinker.cs:172`)
- ✅ **N-blinkingled-02** **FIXED 2026-09-30 (ModuleKit and blinkingled 1.0.6, lane fix/followups)** (info). `FakeModuleSettings.Save()` under `FailSaves` puts the values back to what the last successful `Save()` left (`RevertToSaved`), which is what the host's store shows a module afterwards: a fresh instance per `GetSettings`, loaded from a disk the failed write never reached. The same handle still shows a `Set()` before `Save()`, as the host's does; a module that holds one handle across a failed write would keep its own edits under the shipped host, which the fake cannot show at the same time as the disk, and the doc comment says it shows the disk. The BlinkingLed F110 checks now assert both halves of every failing click: the one log line, and that the values read back are the old ones ("a tray speed pick whose write fails leaves the saved values as they were, so the click did nothing"; the Caps Lock stop and the tray Off still read enabled), with WITNESS checks that a write which lands is what the module reads back. Pinned in CoreTests too ("ModuleKit recording host": a failed Save() kept the unsaved value). Fortunes' `FailSaves` use (F147) still passes. MUTATION "followups: the fake settings keep a failed Save()'s values again" in both layers, recorded in the commit. The original record: `FakeModuleSettings.Set` kept the value when `Save()` was failing, so a module's degraded path under the fake read the NEW values back where the host's store shows the OLD ones, and the BlinkingLed F110 checks asserted the log lines only (`src/DesktopAICompanion.ModuleKit/Testing/Fakes.cs:47`).

### Lane fix/aibrain (Phase 2-3)

31 items: 29 to fix or disposition, 2 info to disposition.

- ✅ **F066** **ACCEPTED-RECORDED 2026-09-29 (aibrain 1.1.14).** A decision, not a code change: the owner ruled on 2026-09-29 that vision, when enabled, applies to every remark, unprompted drops included ("how else would it know what is on the screen to react to?"), so `OnDrop` keeps `Ask(pet, true)` and the four sentences that said otherwise changed (the pane label, the comment beside it, `AiSettings.UseVision`'s doc, `AiBrain.AskAboutScreenAsync`'s routing comment); the decision sits at the top of the campaign section in `docs/DESIGN-REGISTER.md` and BUG-010's post-mortem is filled in `docs/ISSUES-post-1.0.0.md`. Pinned: `--module-selftest=aibrain` drives a real `AiBrainModule` through ModuleKit's `RecordingHost` with a one-field seam (`AskSinkForDiagnostics`, `engine/AiEngineProbe.Module.cs`) and asserts the drop asks with vision allowed, WITNESS the poke without, the tray row with. MUTATION: "the unprompted drop stops allowing vision (the fix the owner declined)" FIRED (`BUG-010: the unprompted drop asks WITH vision allowed`).
- ✅ **F067** **FIXED 2026-09-29 (aibrain 1.1.14).** `Ask` consults `FullscreenBlocked()` after the session checks, so the hotkey and the tray row honour the stand-down the drop and the poke already did; the refusal is logged (`ask declined: fullscreen stand-down`) because the explicit path has no chain to fall through to, and the setting's label now names the hotkey. The two responders keep their own call in front (the source invariant asserts it there). Asserted in `engine/AiEngineProbe.Module.cs`: the tray ask is declined with `IsFullscreenActive` set, the log line appears, WITNESS the same click starts a turn once it is clear. MUTATION: "the explicit ask ignores the fullscreen stand-down again" FIRED (`the tray ask is DECLINED while a fullscreen app runs`).
- ✅ **F070** **FIXED 2026-09-29 (aibrain 1.1.14) for the audition; the repeat-guard retry is ACCEPTED-RECORDED.** `CreateBrain(s, localKeepAliveSeconds)` and `BuildLocalBackend(..., keepAliveSeconds)` let the audition brain's Ollama client carry `AuditionKeepAliveSeconds(s)`: 60 s under "unload" and the residency's own value otherwise, and `PreviewDispositionAsync` calls `brain.UnloadAsync` when the run ends under "unload", so five samples no longer race Ollama's eviction and the VRAM is handed back afterwards. The live repeat-guard retry keeps its possible second load: it fires only after a model has repeated itself, changing it needs a per-request keep_alive on the backend interface, and the repo's one recorded audition timing (5498/3803/419/380/385 ms) shows the eviction race won more often than lost; recorded under `#### fix/aibrain`. Asserted in `engine/AiEngineProbe.Residency.cs` (the window is positive under "unload", the residency's own value otherwise, and it reaches the client; WITNESS the live client still carries 0). MUTATION: "aibrain: the audition brain evicts after every sample again" FIRED (`the audition brain holds the model between samples`).
- ✅ **F098** **FIXED 2026-09-29 (aibrain 1.1.14).** `AiSettings.DecodeSettingsText` skips a UTF-8 BOM and transcodes a UTF-16 document by its BOM (measured 2026-09-29 on .NET 10: the string overload of `Deserialize` threw on U+FEFF and the strict decoder on UTF-16 bytes, so an editor re-save was classed corrupt); a primary that really is unreadable is copied aside as `ai-settings.corrupt-<utc stamp>-<guid>.json` (the host store's shape) BEFORE the backup is restored over it, and the recovery is described in a new `LoadWarning` the module logs once at Init (the good `.bak` is still never rotated over; the recovered path also warns when the primary was merely missing). Asserted in `CheckAiSettingsPersistence`: a BOM'd and a UTF-16 document load with no warning and no `.corrupt` copy, the genuinely corrupt one leaves exactly one copy whose bytes are the rejected file and a warning naming it, WITNESS the next save rewrites UTF-8 without a BOM. MUTATION: "aibrain: a UTF-8 BOM is corruption again" FIRED (`a UTF-8 BOM on ai-settings.json is not corruption`); "aibrain: the rejected primary is destroyed by the recovery again" FIRED (`a rejected primary is kept beside the store`).
- ✅ **F101** **FIXED 2026-09-29 (aibrain 1.1.14).** Two gates. `CanUse` refuses a cloud slot with no `CloudTextModel` (and, with vision on, no `CloudVisionModel`) with a reason naming the fix, and `ApplyState` logs `CanUse`'s reason ("AI brain not started: ...") instead of dropping it; the `AiBrain` constructor keeps a cloud snapshot's blank model blank (the literal `gemma3:4b` fallback belongs to the local slot), `ChooseModel` answers an empty id with a new `none-configured` advisory ("No text model is set for this provider. Pick one in AI Brain settings.") instead of substituting the provider's first-listed model, and `TestConnectionAsync` refuses a blank cloud model instead of defaulting. Asserted in `engine/AiEngineProbe.Backends.cs`: `CanUse` refuses and passes on the model, a blank cloud snapshot sends nothing and speaks the advisory, WITNESS the local slot keeps its fallback. MUTATION: "aibrain: CanUse accepts a cloud provider with no cloud text model again" FIRED (`a cloud provider with no cloud text model does not pass CanUse`); "aibrain: a blank cloud model is filled with the local default again" FIRED (`a cloud snapshot with no model never invents the local default`).
- ✅ **F102** **FIXED 2026-09-29 (aibrain 1.1.14) for the `ChooseModel` gate and the marker list; the dropdown filter is ACCEPTED-RECORDED.** `ChooseModel` gates a vision ask on a REPORTED `Vision == false` only; a listed model with `Vision == null` (every `/v1` list) and no name marker is used as configured (`configured-unverified-vision`, named in the log) instead of being rerouted to another vendor's model behind a false "isn't available" advisory, and the substituted advisory says "can't see images" when the configured model was listed but reported blind. `VisionModelMarkers` gained gpt-5, gemini-3, grok-4, mistral-small-3, mistral-medium-3, glm-4.5v and glm-4.6v, and the orphaned "Advisory only, never a hard gate" summary moved onto `LooksVisionCapable` saying what the hint now decides. `BuildModelOptions(visionOnly)` keeps the marker filter for `/v1` lists, recorded under `#### fix/aibrain`. Landed together with F103, as the audit's verifier required. Asserted in `engine/AiEngineProbe.Backends.cs`. MUTATION: "aibrain: a marker miss refuses a configured vision model again" FIRED (`a listed vision model with no marker and no report is used as configured`).
- ✅ **F103** **FIXED 2026-09-29 (aibrain 1.1.14).** A small optional `IModelLister` interface (`engine/IPetBrainBackend.cs`) implemented by `OllamaClient`, `OpenAiCompatBackend` and `FallbackBackend`, which answers for its PRIMARY (the ids the brain configures) and null when the primary cannot list; `ListBackendModelsAsync` casts to it instead of two type tests, so the cloud+local composite, the default cloud shape, is enumerated and BUG-002's re-validation and advisory apply to a bad cloud id; `RefreshInventoryAsync` logs when a backend cannot enumerate, and the summary that said "an empty list" now says null. Landed together with F102's gate change, as the audit's verifier required. Asserted in `engine/AiEngineProbe.Backends.cs`: the composite over an OpenAI-compatible double lists two ids, WITNESS a non-lister and a composite over one answer null. MUTATION: "aibrain: the cloud+local composite cannot be enumerated again" FIRED (`the cloud+local composite can be enumerated`).
- ✅ **F105** **FIXED 2026-09-29 (aibrain 1.1.14).** Reachability probes have their own bound, `AiEndpointPolicy.AvailabilityProbeDeadline` (10 s), taken as the shorter of it and the chat deadline: `OpenAiCompatBackend.IsAvailableAsync` uses a `_probeDeadline` and `OllamaClient`'s public `IsAvailableAsync` an `_availabilityDeadline` (the startup poll keeps its 1 s one), where both used to borrow the 120 s chat deadline; `FallbackBackend.IsAvailableAsync` probes both legs at once and answers from the first that is up (`FirstUpAsync`, the abandoned probe observed), and `EnsureServerAsync` readies both legs at once so launch auto-start no longer waits behind a cloud probe. The chat's own deadline is untouched. Asserted in `engine/AiEngineProbe.Residency.cs`: a headers-blocking handler against a 600 s chat deadline (both probes answer false within the bound), and a hanging cloud double (the composite answers from the local leg, and readies it, while the cloud probe is still pending). MUTATION: "aibrain: the cloud reachability probe borrows the chat deadline again" FIRED; "aibrain: the local reachability probe borrows the chat deadline again" FIRED; "aibrain: the composite probes cloud then local again" FIRED; "aibrain: the composite readies the local leg only after the cloud leg again" FIRED, each on its own assertion.
- ✅ **F106** **FIXED 2026-09-29 (aibrain 1.1.14).** `OllamaClient.WarmUpAsync` sends the client's `KeepAliveSeconds` when it has one (`-1` under "keep", the only production caller) and the historical "10m" only for a client nobody gave a policy to, so the launch warm-up cannot disagree with the residency it serves; `VramStatusLine`'s doc comment no longer describes the retired preload pin. Asserted in `CheckKeepAliveAndResidency` on the outgoing `/api/generate` payload (`"keep_alive":-1`; WITNESS an unset client still sends "10m"). MUTATION: "aibrain: the warm-up hard-codes keep_alive 10m again" FIRED (`the warm-up carries the residency's keep_alive`).
- ✅ **F107** **FIXED 2026-09-29 (aibrain 1.1.14).** Three edits, none touching the no-fallover-on-a-bad-key decision: `AiBrain.DescribeError` tests `AiBackendHttpException` before the `HttpRequestException` it derives from and returns `http-<status>` (the deadline's own `TimeoutException` also joins the `timeout-or-cancelled` bucket it was missing from); `OpenAiCompatBackend.IsAvailableAsync` treats any non-redirect answer as reachable through a new `AiEndpointPolicy.SendAndCheckAnsweredAsync`, local to that backend because `OllamaClient` keeps the success probe its server launch depends on; `TestConnectionAsync` catches `AiBackendHttpException` and prints `DescribeHttpFailure` ("HTTP 401 from api.openai.com: the provider rejected the API key (Incorrect API key provided)", a 404 pointing at the base URL, a 402 at credits, a 5xx at the provider). Recorded risk, under `#### fix/aibrain`: a cloud answering 5xx now counts as reachable, so during an outage each ask pays one failed cloud request before failing over. Asserted in `engine/AiEngineProbe.Backends.cs` with 401, refused-transport and redirect doubles. MUTATION: "aibrain: an answered status is filed as backend-unreachable again" FIRED (`an answered HTTP status is its own category in the log`); "aibrain: an answered 401 reads as not reachable again" FIRED (`an answered 401 is reachable`).
- ✅ **F063** **FIXED 2026-09-29 (aibrain 1.1.14).** `AiBrain.PrepareAsync(ct, warmUp = true)`: the audition passes `warmUp: false`, so a canned run under "keep" plus UseVision no longer loads the vision model its samples never use (a live audition loads whatever it uses on its first sample); the launch routine keeps the warm-up. Asserted in `engine/AiEngineProbe.Residency.cs` with a `RecordingBackend` that counts warm-ups (0 with the switch off under "keep", WITNESS 1 by default). MUTATION: "aibrain: the audition's preparation warms the model again" FIRED (`an audition's preparation warms nothing`).
- ✅ **F065** **FIXED 2026-09-29 (aibrain 1.1.14), by the F095 change; the rebuild itself stays.** The cost the finding measured, an evicted and cold-reloaded model (5-11 s) on every Apply under "keep", is gone: a same-backend Apply retires without evicting. The brain is still rebuilt on every Apply, deliberately and recorded under `#### fix/aibrain`: it reads the persona from its settings clone, so a name or disposition edit must reach a new brain, and a fingerprint that skipped the rebuild would have to enumerate every field the brain reads and would silently stop edits reaching it the day it missed one. The `_recentRemarks` reset per Apply stays as already recorded acceptable. Pinned by F095's assertions and mutation.
- ✅ **F068** **FIXED 2026-09-29 (aibrain 1.1.14), together with F100.** `ApplyState` takes `AiSettings.CloneForBrain()` on the UI thread and hands THAT to the factory: a MemberwiseClone that also copies `ApiKeysEnc`, `DisabledSources`, `DisabledGenres` and `ExtensionData`, marked so `SaveWithin` refuses it before the lock. The variable is named `forBrain`, not `snapshot`; the audition keeps building from the live instance, on the UI thread before its first await, where there is nothing to race. Asserted in `engine/AiEngineProbe.Lifecycle.cs`: the copy owns its collections, a key rotated on the live instance after the hand-off does not reach it, WITNESS the copy carries the fields the factory reads, and it cannot save. MUTATION: "aibrain: the brain's settings copy shares the live credential dictionary again" FIRED (`the brain's settings copy owns its credential dictionary and its collections`).
- ✅ **F071** **FIXED 2026-09-29 (aibrain 1.1.14).** The inventory is taken by `AiBrain.ObserveReachabilityAsync` on every transition to reachable, the first check included, which both `PrepareAsync` and the per-ask `CheckBackendAvailableAsync` route through: a brain built lazily on its first ask (auto-start off, no warm-up) now re-validates its model on that ask, a server restarted after a pull is re-listed when it comes back, and a backend that stays up is not re-listed per ask. The pane's "Refresh local/cloud models" also calls `AiSessionManager.RefreshInventoryAsync`, which re-lists the live brain's own backend without the gate (as `ReleaseModelAsync` does). No timer, recorded under `#### fix/aibrain`. Asserted in `engine/AiEngineProbe.Lifecycle.cs` with a lister counter (1 after the first check, and the substitution follows from it; WITNESS still 1 after two more checks; 2 after down-then-up; the session refresh lists once). MUTATION: "aibrain: the inventory is no longer taken on the transition to reachable" FIRED (`an unprepared brain learns the inventory on its first reachability check`); "aibrain: the pane's model refresh no longer reaches the live brain" FIRED (`the pane's model refresh reaches the live brain's inventory through the session`).
- ✅ **F072** **FIXED 2026-09-29 (aibrain 1.1.14).** The pre-capture decision is one method, `AiBrain.ResolveBeforeCapture`: it picks the model (BUG-002) and speaks a substitution's advisory ONCE, before the capture, the OCR pass and the generation; the post-generation block that discarded a generated remark it had already remembered as spoken is gone, so nothing generated is discarded. The trade is recorded at the site and in the register: the substitute is announced before it is proven, and if it then fails the log says so. Asserted in `engine/AiEngineProbe.Backends.cs` with a fake inventory and no screen: the first ask ends on the advisory naming both models with zero backend calls, WITNESS the next ask proceeds with the substitute, WITNESS an unusable choice ends the turn on both asks. MUTATION: "aibrain: a substitution is announced only after the generation again" FIRED (`a substitution is announced before any capture`).
- ✅ **F073** **FIXED 2026-09-29 (aibrain 1.1.14).** `AskAboutScreenAsync` hoists `useVisionPath` and the `ModelChoice` above its try and the catch logs `DescribeFailure(ex, choice, useVisionPath)`: the model actually SENT (a substitute, or "(unresolved)" before resolution), the path taken, and `BackendHostDescription`, which `CreateBrain` sets to the host it built against ("api.openai.com->localhost" for the composite) and which the availability and inventory lines use too, so a cloud outage no longer logs "endpoint=localhost". Asserted in `engine/AiEngineProbe.Backends.cs` (pure `DescribeFailure` cases, and `CreateBrain` on cloud settings, whose construction touches no network). MUTATION: "aibrain: the failure line names the configured model instead of the one sent again" FIRED (`a failure after resolution names the model actually SENT`).
- ✅ **F077** **FIXED 2026-09-29 (aibrain 1.1.14), the walk and the re-resolution; the second OcrEngine creation stays.** `AiExecutablePolicy.IsExistingLocalFileWithoutReparsePoints` gates the segment walk behind `File.Exists`, so an absent candidate answers false without the `FileNotFoundException` that was thrown and caught once per PATH entry; a candidate that exists is still walked, so the no-reparse-traversal invariant and its injected-reader seam are untouched. `AiBrain.ResolveTesseractOnce` caches the resolution per brain (every Apply builds a new brain; "Test OCR" resolves afresh and refreshes the cache). The `WindowsOcr.IsAvailable` call that words the "ocr engine:" line before the read stays, recorded under `#### fix/aibrain`. Asserted in `engine/AiEngineProbe.Lifecycle.cs` with a twelve-entry PATH and a FirstChanceException counter on the test thread (0 IOExceptions; WITNESS a present executable still resolves through the same walk) and a brain describing its engine twice (1 resolution). MUTATION: "aibrain: the PATH walk throws once per entry again" FIRED (`resolving an absent executable from PATH throws nothing per entry`); "aibrain: the OCR engine is resolved on every ask again" FIRED (`the OCR engine is resolved once per brain, not once per ask`).
- ✅ **F078** **FIXED 2026-09-29 (aibrain 1.1.14).** Both `ListModelsAsync` implementations pass `AiEndpointPolicy.MaximumListingResponseBytes` (8 MiB) to the reader; chat replies keep the 1 MiB `MaximumResponseBytes`. Asserted with a 30,000-id catalogue larger than the reply cap (the fixture's size is itself asserted, so the check cannot pass vacuously), WITNESS a chat reply of that size is still refused. MUTATION: "aibrain: the cloud model listing reads under the 1 MiB reply cap again" FIRED (`a /models catalogue above the 1 MiB reply cap still lists`).
- ✅ **F079** **ACCEPTED-RECORDED 2026-09-29.** Certificate revocation checking stays at the .NET default (off) on the cloud slot's handler: turning it on makes an unreachable OCSP/CRL responder a hard failure of every cloud ask and of "Test connection" on exactly the networks most likely to be hostile; the exploit needs an on-path attacker holding a revoked-but-unexpired certificate for the provider host; and the host's own `SecureDownload` and Remembrance's `WhisperInstaller` carry the same posture, so the change belongs to all three at once or to none. Recorded under `#### fix/aibrain` in `docs/DESIGN-REGISTER.md` as an owner-decision candidate, with the flip described.
- ✅ **F080** **FIXED 2026-09-29 (aibrain 1.1.14).** `AiEndpointPolicy.EnsureSuccessAsync` reads a failing answer's body (bounded at 4 KB, best-effort) and `ExtractProviderErrorMessage` takes `error.message` (OpenAI-compatible) or the bare `error` string (Ollama), stripped of control characters, whitespace-collapsed and capped at 200 characters, onto a new `AiBackendHttpException.ProviderMessage` for the PANE (`DescribeHttpFailure` appends it); both read-and-ensure helpers use it. The log stays category-only: `DescribeError` records `http-<status>` (F107), never the text. Asserted in `engine/AiEngineProbe.Backends.cs` (both shapes, a non-JSON body, the bounds, WITNESS whitespace collapse). MUTATION: "aibrain: a provider's error body is dropped again" FIRED (`the provider's error message reaches the exception`).
- ✅ **F081** **FIXED 2026-09-29 (aibrain 1.1.14).** The post-consume `boundedToken.ThrowIfCancellationRequested()` in `AiEndpointPolicy.SendWithDeadlineAsync` is gone: every consumer observes the token while it reads, so a result it hands back is complete, and the check could only discard a finished answer as a `TimeoutException` the retry then paid a second generation for. `SendWithDeadlineAsync` is internal so the probe can drive it with a consumer that finishes after a 50 ms deadline: the result comes back; WITNESS a consumer that observes the token still times out. MUTATION: "aibrain: a finished reply is discarded when the deadline fired during the read again" FIRED (`a complete reply is returned even when the deadline fired`).
- ✅ **F086** **FIXED 2026-09-29 (aibrain 1.1.14).** `AiPaths.SwapRoot` replaces the root and returns the previous one, null included (`SetRoot` ignores a blank on purpose, which made restoring an unset root a silent no-op); `AiEngineProbe.Run` records the root it found, swaps in its own, and its finally swaps the entry root back and asserts the value in force at exit was Run's own (`every probe restored the settings root it borrowed`), reporting a failed delete the way the sub-checks do instead of swallowing it; the four sub-checks that borrow a root (`CheckAiSettingsPersistence`, `CheckAiSchemaMigration`, `CheckDispositionMigration`, `CheckLocalBackendKind`) and the module-instance block put theirs back in their finally. MUTATION: "aibrain: a probe leaves its borrowed settings root behind" FIRED (`every probe restored the settings root it borrowed`).
- ✅ **F091** **FIXED 2026-09-29 (aibrain 1.1.14).** `DisposeCore`'s not-entered branch issues a bounded best-effort `UnloadAsync` (`NotEnteredUnloadBudget`, 1 s) BEFORE disposing the backend, and `QueueDeferredCleanup` no longer routes through `RetireBrainAsync` (which unloaded a disposed backend and claimed an eviction): it drops the reference, disposes idempotently and runs the after-retire actions. The F089 check asserts the ORDER now (`UnloadCalls == 1 && DisposeCount == 1 && UnloadCallsAfterDispose == 0`) and its name says so; the branch's comment describes what it does. MUTATION: "aibrain: the timed-out dispose disposes the backend before releasing the model again" FIRED (`the model was released BEFORE the backend was disposed`).
- ✅ **F095** **FIXED 2026-09-29 (aibrain 1.1.14).** `AiSessionManager.ReconfigureAsync` takes `releaseModel` (default true); when false, `RetireBrainAsync` disposes the retiring brain without unloading its models. `ApplyState` passes false when `BackendFingerprint(s)` (the local slot's endpoint, protocol and models, the cloud selector with its endpoint and models, the fallback switch, the residency, the executable path; no persona field) equals the live brain's and the residency is not "unload", so a name, hotkey or disposition edit under "keep" or "server" no longer evicts the resident model; disable and shutdown still evict. Asserted in `engine/AiEngineProbe.Residency.cs`: the fingerprint's blind spots and sensitivities, and a `RetirementTrackingBackend` retired with `releaseModel: false` sees zero unloads and one dispose, WITNESS the default sees one unload. MUTATION: "aibrain: a same-backend retirement evicts the model again" FIRED (`a same-backend Apply retires the brain without evicting its model`).
- ✅ **F096** **FIXED 2026-09-29 (aibrain 1.1.14), the logging half; the budget and the thread stay.** The timeout path in `Load` (now `LoadWithin`, which `Load` calls with the 10 s cross-session budget) returns defaults with writes blocked AND a `LoadWarning` ("AI settings could not be loaded (<category>); running on defaults, and nothing will be saved this session ..."), which Init logs; a future-schema primary and a host with no storage warn the same way. The budget is deliberately not shortened and the load stays on Init's thread: a save that returns false is retried by the next click, but a load that gives up blocks writes until restart, so a transient stall is waited out rather than turned into a session of silent non-persistence; recorded under `#### fix/aibrain` in `docs/DESIGN-REGISTER.md`. Asserted with the `.lock` file held and a 125 ms budget: the warning names the failure and `Save()` refuses afterwards. MUTATION: "aibrain: a load that times out says nothing again" FIRED (`a load that times out on the lock says so`).
- ✅ **F099** **FIXED 2026-09-29 (aibrain 1.1.14).** `Normalize` restores the default for an EMPTY `Endpoint` ("http://localhost:11434") and `Hotkey` ("Ctrl+Alt+P") after `NormalizeString`, which substitutes its fallback only for null, so a hand-edited "" no longer reaches three consumers that read it three ways; `LocalBackendKind` was already clamped and is untouched. Asserted in `CheckAiNormalization` (a whitespace endpoint and an empty hotkey come back as the defaults, WITNESS a valid custom pair survives unchanged). MUTATION: "aibrain: an empty endpoint survives normalization again" FIRED (`an empty local endpoint normalizes back to the default`).
- ✅ **F100** **FIXED 2026-09-29 (aibrain 1.1.14).** The copy half is F068's `CloneForBrain`. The reporting half: both `factory()` sites in `AiSessionManager` (`ReconfigureAsync`, `AskAsync`) catch a throwing factory, log `brain build failed: <category>` through `AiBrain.LogBuildFailure` and complete false/null, where the faulted task was discarded by `ApplyState` and nothing said the brain was never built. Asserted in `engine/AiEngineProbe.Lifecycle.cs` with a factory that throws `InvalidOperationException`: the reconfigure returns false and the sink holds `brain build failed: invalid-state`. MUTATION: "aibrain: a throwing brain factory is silent again" FIRED (`a throwing brain factory is reported and does not fault the reconfigure`).
- ✅ **F104** **FIXED 2026-09-29 (aibrain 1.1.14).** `FallbackBackend.ChatAsync` chooses the local model from the REQUEST (a message carrying an image is the vision path; the brain attaches images only there), remembers it, and `UnloadAsync` releases both the id-mapped model and the one a fallover actually ran when they differ; `LocalModelFor`'s id comparison stays for `WarmUpAsync`, which has only an id. The existing failover fixture now carries an image, as a real vision request does (it passed on the id alone before). Asserted in `CheckFallbackBackend`: with one cloud model in both slots a text fallover lands on the local TEXT model and the release names it, WITNESS an image lands on the local vision model. MUTATION: "aibrain: a fallover picks the local model by id again" FIRED (`a text fallover with one cloud model for both slots lands on the local TEXT model`).
- ✅ **F109** **FIXED 2026-09-29 (aibrain 1.1.14).** `WindowsOcr.ToSoftwareBitmapAsync` copies the capture's pixels (`LockBits` as 32bppArgb, row by row when the stride is padded) into `SoftwareBitmap.CreateCopyFromBuffer(Bgra8, Ignore)`; the PNG encode-and-decode is kept only as the fallback for a copy that throws, and two counters say which route ran. The property stated is that no codec pass runs per ask; no timing is claimed (the finding's figures were warm-loop numbers), recorded under `#### fix/aibrain`. Asserted by the existing "OCR works" probe read, which proves the raw layout (garbage pixels read as nothing), plus a new check that the raw route ran once and the fallback not at all. MUTATION: "aibrain: Windows OCR goes through the PNG round trip again" FIRED (`Windows OCR is fed the capture's pixels, not a PNG round trip`).

Info, disposition only:

- ✅ **F062** **FIXED 2026-09-29 (aibrain 1.1.14)** (info). `_auditionRunning` is an int taken by `TryBeginAudition` (`Interlocked.CompareExchange`) at the top of `PreviewDispositionAsync` and released by `EndAudition` in its finally, so the check and the set are one step and the clear is right from the pool thread the audition ends on; the comment says so instead of claiming a single-thread model. Asserted in `engine/AiEngineProbe.Lifecycle.cs` on a module instance whose local slot points at a loopback port nothing listens on: with the slot held the press answers "Already generating", WITNESS the slot cannot be claimed twice and is free again after the release. MUTATION: "aibrain: the audition guard admits a second press again" FIRED (`a second audition press while one is running is refused with an explanation`).
- ✅ **F092** **ACCEPTED-RECORDED 2026-09-29** (info). 32 scopes x 16 KB ciphertext can exceed the 256 KB file cap only with about 24 keys of 8 KB each; real provider keys are 50-200 characters and 32 of them serialize to about 20 KB, so no reachable configuration meets the refusal. Recorded under `#### fix/aibrain`; a size reason on the save refusal is the cheap half to add if a real key ever approaches 8 KB.
- 📌 **N-aibrain-01** [info/deadcode] `AiBrain.DescribeOcrEngine` has no caller: its summary says the "Test OCR" status surfaces it, but `SelfTestOcrAsync` words the engine name itself, so the method only serves the F077 self-test (`modules/AiBrain/engine/AiBrain.cs:1527`)
- ✅ **N-aibrain-02** **FIXED 2026-09-30 (ModuleKit, lane fix/followups).** `ModulePaths.FromStorage` has no `%TEMP%` fallback any more: a null storage, or one with a blank directory, yields a `ModulePaths` with `HasRoot == false` and a `Warning` that names the module and the missing storage, and `Root`, `Ensure()`, `File()` and `Directory_()` throw that warning rather than creating `%TEMP%\DesktopAICompanion.<id>`; `SafeSegment` went with the fallback it guarded. The shipped host always provisions a storage directory (F341), so the product path is unchanged; the template (`templates/desktop-ai-companion-module/SampleModule.cs`) now shows the degrade shape (`if (!_paths.HasRoot) host.Log(Info.Id, _paths.Warning);`) and `packaging/Test-ModuleTemplate.ps1` loads the scaffolded module through the convention host with that warning logged (RESULT=PASS). No in-tree module uses `ModulePaths` (only the template), so no `--module-selftest` output changes; all seven still pass. Docs corrected: `src/DesktopAICompanion.ModuleKit/README.md` and `docs/module-authoring.md` no longer promise a temp fallback. Pinned in CoreTests ("ModuleKit module paths"): no root, a warning naming the module, every path member throws, the old fallback directory is not created, a blank directory counts as none, WITNESS the same members work under a real storage. MUTATION "followups: ModulePaths falls back to a %TEMP% folder again", recorded in the commit. Decision under `#### fix/followups` in `docs/DESIGN-REGISTER.md`. The original record: `ModulePaths.FromStorage` kept the `%TEMP%\DesktopAICompanion.<id>` fallback root for a module handed no storage, the shape N-gates-02 removed from `AiPaths`; every module that used it under `--module-selftest` could leak files the same way (`src/DesktopAICompanion.ModuleKit/ModulePaths.cs:31`). Found by lane fix/aibrain.

### Lane fix/fortunes (Phase 2-3)

21 items: 19 to fix or disposition, 2 info to disposition.

- ✅ **F122** **FIXED 2026-09-29 (fortunes 1.0.12).** `ImportPacksAsync` keeps the file picker and the folder path on the UI thread and awaits `Task.Run(FortuneFileImporter.Import(...))` with the module's shutdown token (the importer honours it and rolls back), then awaits the asynchronous rebuild; a bare await, so the status resumes on the UI thread. Probe (`FolderActionChecks`, smart picks off): the import lands the file, its lines join the live pool, and `FortuneFileImporter.LastImportThreadForDiagnostics` is not the thread that pressed the button. MUTATION "fortunes: the import runs on the calling thread again" FIRED.
- ✅ **F123** **FIXED 2026-09-29 (fortunes 1.0.12).** `ReadDirectoryState` asks `FortuneProvider.TryGetCachedPack` (path, length, last-write time: the loader's per-file parse cache) before reading an existing pack, so after the folder has been loaded once an import validates none of them again; a file it has never seen is read and validated as before. Probe: `ExistingPacksValidatedForDiagnostics` does not move across an import into a loaded folder. MUTATION "fortunes: the importer re-validates every existing pack again" FIRED. F129's importer half lands here too: a file the loader refuses holds no slot and no bytes in the admission (`ExistingFile.Loadable`); probe check with a 200-byte junk file under a two-file, 150-byte cap; MUTATION "fortunes: the importer charges a file the loader refuses again" FIRED.
- ✅ **F127** **FIXED 2026-09-29 (fortunes 1.0.12).** The pane actions whose purpose is a changed folder (Rescan, Import, Download, the Rebuild button) rebuild through `RebuildEngineAsync`: settings read on the UI thread, `new FortuneProvider(settings)` on a pool thread, publish on the UI-thread continuation behind `_engineGeneration` (an overtaken parse publishes nothing); the writable folder's cache is per file (the F130 commit), so a changed folder costs the files that changed. Init and Apply stay on the calling thread, deliberately: decision and reasons in `docs/DESIGN-REGISTER.md`, fix/fortunes. No timing is quoted; the property is stated. Probe: Rescan's parse thread is not the thread that pressed the button. MUTATION "fortunes: the folder-changing rebuild parses on the calling thread again" FIRED.
- ✅ **F130** **FIXED 2026-09-29 (fortunes 1.0.12).** `TryParseCustomContent` now refuses undeclared content whose strict tagged parse failed whenever any line of it validates as a tagged row (`LooksTagged`), carrying the parser's line-numbered error into the importer's status instead of demoting the file to prose; the folder loader counts every refused file by category, reports the counts through the new `FortuneProvider.LogSink` (categories and counts, never a name) and the pane's pool status appends `SkippedPacksNote`. Asserted in `FortuneEngineProbe.Run` (blank line, dropped tab, damaged FIRST row all refused; metadata-looking prose still admitted as the WITNESS); MUTATION "fortunes: a faulty undeclared tagged pack is demoted to prose again" FIRED and "fortunes: the pane stops mentioning refused pack files" FIRED. Residue, recorded at `LooksTagged`: a pack of ONE faulty row has no valid row to be recognised by and still reads as prose. Declaring the 158 committed packs (`#!desktop-pet-fortunes-v2`) changes every catalog hash and is left to the coordinator.
- ✅ **F135** **FIXED 2026-09-29 (fortunes 1.0.12).** `WarmCore` calls `VectorCache.ReleaseMemory()` after a final `Save` that succeeded, dropping the map (the raw vectors and their key strings) that nothing in the process reads again; a later `Warm` on the same instance reloads the disk copy in `BeginActivePool`, so re-entrancy keeps its cache hits, and a failed save keeps the unsaved vectors for a retry. Probe check "the vector cache releases its raw copy of the vectors once the warm has saved them" (`CacheEntriesForDiagnostics == 0`, with the on-disk WITNESS); MUTATION "fortunes: the vector cache keeps its raw copy after the final save again" FIRED.
- ✅ **F137** **FIXED 2026-09-29 (fortunes 1.0.12).** `Warm`'s early exits set `StandDownReason` (`PoolTooLarge`, `ModelAbsent`) and `WarmCore`'s sets `EmbedderNotReady` with the failing asset as `StandDownDetail`; the reason is cleared at the start of every warm so a rebuild after the fix reports progress. The pane's wording per reason lands with F148 in the module commit. Probe checks drive all three without touching the shipped assets (a 100,001-line pool; a model-absent seam on one instance; an embedder pointed at a 3-byte model); MUTATION "fortunes: an oversized pool leaves the stand-down flag unset again" FIRED, "fortunes: a missing model asset leaves the stand-down flag unset again" FIRED.
- ✅ **F138** **FIXED 2026-09-29 (fortunes 1.0.12).** `VectorCache.Save` skips the merge re-read when the file's length and last-write tick are what this instance itself last wrote (another writer moves either and is merged as before), and the cold-warm checkpoint fires on elapsed time (`CheckpointIntervalMilliseconds`, 60 s) instead of every 2048 pool entries, so a cold warm costs a handful of whole-map writes and no re-reads rather than n²/2048 of each. Property stated, not timed: no cold 59k warm was run on this box. Probe check "a save over a file this cache itself last wrote does not re-parse it" with the other-writer WITNESS; MUTATION "fortunes: a save re-reads the cache's own file again" FIRED.
- ✅ **F145** **FIXED 2026-09-29 (fortunes 1.0.12).** The publish-time line reads "smart picker constructed, warming N lines in the background" (`DescribeSmartBuild`, pure), and the warm itself reports through the sink: "smart index complete: N of M lines indexed" once complete, "smart index warm cancelled (superseded or shutting down)", "smart index warm failed: <type>", beside the stand-down lines. Probe checks: the completion line appears exactly once and only after `WarmProgress` reports complete, the cancellation line appears for a pre-cancelled warm, and a real smart-ON Init logs the constructed line and never a "ready" one. MUTATION "fortunes: the warm stops reporting its completion" FIRED, "fortunes: a cancelled warm is silent again" FIRED, "fortunes: the publish-time line claims the picker is ready again" FIRED. The 1.0.6 changelog note is corrected in place; `docs/BACKLOG-CLOSED.md:966-970` (the 91 ms "ready" verification) is outside this lane's boundary and is named in the report.
- ✅ **F148** **FIXED 2026-09-29 (fortunes 1.0.12).** `SmartStatusText` reports "enabled" from `_smartWanted` (the setting as of the last rebuild) and a thrown construction from `_smartBuildFailed`, both recorded by the rebuild, instead of from `_smart != null`; with the picker still being built the status is "Indexing N fortunes in the background", and `SmartStatusFor` takes the stand-down REASON so each terminal state (embedder not ready naming the asset, model absent, pool too large, construction threw) has its own sentence and none claims to be indexing. Asserted by the pure checks and by the probe's smart-ON lifecycle scenario (Init against a RecordingHost, the status read with the build in flight, the button pressed while one is in flight: "Indexing ...", never "off"). MUTATION "fortunes: the status derives 'enabled' from the picker object again" FIRED.
- ✅ **F118** **FIXED 2026-09-29 (fortunes 1.0.12).** `Embedder.LoadFailure` records which asset stopped the load ("vocabulary: <the parser's own message>", "model: <exception type>", "assets: ..."), never a path (`TryLoadVocabulary` reduces any exception that is not its own `InvalidDataException` to its type); `WarmCore` carries it into the stand-down log line and `SmartFortunes.StandDownDetail`. Probe checks: a vocabulary missing `[SEP]` and a 3-byte model are each named, and a picker over that embedder stands down naming the asset in state and log; MUTATION "fortunes: a failed model load records no reason again" FIRED.
- ✅ **F121** **FIXED 2026-09-29 (fortunes 1.0.12), the module's half.** The probe's own `SmartFortunes` warms into a scratch cache directory (the F137 commit), so it never shares the module's live `cache.bin`; and `SettingsFromStore(null)` reports smart picks OFF, so the convention runner's host (which hands no settings store) no longer makes Init embed the whole corpus on every `--module-selftest=fortunes` (decision in `docs/DESIGN-REGISTER.md`). Probe: the null store yields smart off, an empty store keeps the default (WITNESS). MUTATION "fortunes: a host with no settings store gets smart picks ON again" FIRED. Out of this lane's boundary: `src/dotNet/Plugins/FortunesEngineSelfTest.cs`'s `MemSettings` hands an EMPTY store, so `--fortunes-engine-selftest` and `--fortunes-smart-progress-selftest` still start Init's embed beside the probe's warms (seed `smartFortunes=false` there), and `ModuleConventionSelfTest.ConventionHost` still hands no storage.
- ✅ **F125** **FIXED 2026-09-29 (fortunes 1.0.12).** The merged corpus is a constructor local handed to `Select`, and the parallel `_pool` text list is gone (`Pick` and `Count` read `_poolE`). Structural: no self-test can see the retained bytes cheaply; the probe's existing `Pick` checks (tame pool, shuffle-bag sweeps, seam) cover the behaviour that moved and still pass.
- ✅ **F129** **FIXED 2026-09-29 (fortunes 1.0.12).** Each pack file is loaded inside its own try (`LoadOnePack`), `IsValidCustomSource` refuses an unpaired surrogate before the classifier can throw on it, and the file-slot charge moved beside the byte charge, after a successful parse; every refusal is counted by category. Probe checks: a surrogate-named pack sorted first no longer drops the pack after it and is counted as a bad name; an unreadable file sorted first does not consume the only pack slot. MUTATION "fortunes: a lone surrogate in a pack's file name is accepted as a source id again" FIRED (on the category line, the per-file try keeping the later pack alive), "fortunes: a refused pack file consumes a pack slot again" FIRED. The importer's baseline half (junk files charged toward its aggregate bytes and slots) lands with F123 in the import commit.
- ✅ **F132** **FIXED 2026-09-29 (fortunes 1.0.12).** `TryParseCurrentRow` and `TryParseLegacyRow` decode the text column first and validate the decoded text, so a column that decodes to nothing or to a control character is refused with its line named; the importer shares the parser. Probe checks: the entity-only column and the `&#9;` column are refused, the escaped-ampersand row is admitted decoded (WITNESS). MUTATION "fortunes: the tagged text column is validated before it is decoded again" FIRED.
- ✅ **F142** **FIXED 2026-09-29 (fortunes 1.0.12).** One `Read`/`Write` per vector through a 1,536-byte buffer decoded and encoded with `BinaryPrimitives` little-endian (the format `BinaryWriter` always wrote, byte-identical), a 64 KB stream buffer, and no `FileOptions.WriteThrough` on the temp file (`Flush(true)` plus the atomic replace give the durability; AtomicFile keeps the flag for its kilobyte settings files). Property stated, not timed. Probe check "the on-disk float encoding is unchanged" over a fixed 0.25f fixture with the reload WITNESS; MUTATION "fortunes: the vector cache writes big-endian floats" FIRED.
- ✅ **F143** **FIXED 2026-09-29 (fortunes 1.0.12).** The superseded picker's `Dispose` (cancel its warm, then wait up to 3 s for an uncancellable ONNX session load or inference) runs at the start of `BuildSmartPicker` on the pool thread, before the replacement is constructed, so the peak stays at one session; only Shutdown disposes inline, at process exit. The title's other half, the first `FortuneProvider` parse on the UI thread, is F127's and lands in the import commit. Probe: the superseded picker records its disposing thread (`DisposeThreadForDiagnostics`), which is not the thread that applied; MUTATION "fortunes: the superseded picker is disposed on the applying thread again" FIRED.
- ✅ **F144** **FIXED 2026-09-29 (fortunes 1.0.12).** One `_smartLock` covers the pairs (bump generation, clear `_smart`) in `ScheduleSmartPicker`, `EngineRebuildFailed` and `Shutdown`, and (generation check, publish) in the build's worker, held for the field swaps only; a thrown construction is recorded under it too. The interleaving cannot be forced by a self-test, so the SHAPE is gated: three source invariants under this lane's anchor in `tests/runtime-hardening-selftest.ps1` assert that each pair sits inside a single-depth `lock (_smartLock)` block in the right order (SMOKETEST.md 170 -> 173), and the gates lane's ORDER invariant stays. MUTATION (`tests/mutate-hardening-guards.py`) "the smart picker's publish takes a different lock from its supersession" FIRED, "a rebuild bumps the smart generation under a different lock" FIRED.
- ✅ **F146** **FIXED 2026-09-29 (fortunes 1.0.12).** `DownloadPacksAsync` writes each pack with `File.WriteAllBytesAsync` behind a bare await (the continuation stays on the UI thread, where `_selectedPacks` and the rebuild belong) and the handler's perceptible half, the re-parse, went off the thread with F127. Property stated, not timed. Probe (`FolderActionChecks`): the downloaded pack lands and its lines join the live pool.
- ✅ **F147** **FIXED 2026-09-29 (fortunes 1.0.12).** `ScheduleSmartPicker` keeps the current picker, built or building, when smart is still on, the new pool's signature equals the one it was started on and its build did not fail; `PoolSignature` folds the topic in with the text; `SavePaneValues` returns false and rebuilds nothing when `Save` failed; the button passes `force`, so a stood-down or in-flight index is still rebuilt on request (decision in `docs/DESIGN-REGISTER.md`, fix/fortunes). Probe: an Apply with unchanged values keeps the picker object and the generation, a failed Save leaves the rebuild count alone, a widened pool bumps the generation, the same texts under a different topic fingerprint differently. MUTATION "fortunes: an unchanged pool rebuilds the smart picker again" FIRED, "fortunes: a failed Save rebuilds the engine anyway" FIRED, "fortunes: the pool signature ignores the topic again" FIRED. The button's own currency guard (F149, lane fix/gates) is unchanged and its invariant and mutation still hold.

Info, disposition only:

- ✅ **F128** **FIXED 2026-09-29 (fortunes 1.0.12).** `CustomCorpus` publishes one immutable `CustomSnapshot` (signature, entries, refused-file count) through a single volatile static, so the lock-free fast path cannot pair an old list with a new signature; landed before F127's backgrounding, which is the change that would have made the torn read live. Structural (an interleaving no self-test can force); `CustomCacheSelfTest`'s F134 checks pass over the snapshot unchanged.
- ✅ **F133** **CLOSED-VERIFIED 2026-09-29.** The module's only string-pattern Regex calls are the three in `FortuneProvider.TryParsePlainContent`, reached only for content `ParseTaggedPackContent` reports `NotTagged` (`TryParseCustomContent`); every shipped pack is tagged, the classifier's patterns are `static readonly ... RegexOptions.Compiled`, and since 1.0.12 a plain pack is parsed once per change through the per-file cache. Nothing to fix.

Taken on from other lanes, and found on the way:

- ✅ **N-tools-02** (filed by lane fix/tools in its section; that line is not in this branch's checkout and is flipped at merge) **FIXED 2026-09-29 (fortunes 1.0.12).** Diagnosed by reading, not reproduced (1 run in 3 inside a full gate, never alone): every step of `TestPortableReplacementFallback` is synchronous, and the one fault that reaches its assertion without throwing is `RollBackCommittedImports` swallowing the `Win32Exception` the MoveFileEx fallback raises when another process (an on-access scanner, the indexer) holds the just-written file open, the same family as the delete-pending handle the tools lane names. A user's import faces the same scanner, so the fix is in the importer, not the test: the commit's replace and move and the rollback's replace, move and delete retry a transient fault (`IOException` other than not-found/too-long, `Win32Exception`) up to eight times over about a second (`WithTransientRetry`); `UnauthorizedAccessException`, the self-tests' permanent fault, is not retried. Probe: a `File.Replace` stand-in that refuses twice with a sharing violation and then succeeds lands the batch on the third attempt; a permanent refusal is surfaced after one attempt (WITNESS). MUTATION "fortunes: a transient lock during the import's commit is surfaced at once again" FIRED.
- 📌 **N-fortunes-01** [low/harness] `tests/mutate-hardening-guards.py` refuses its own baseline when launched from PowerShell 7: its `powershell.exe` child inherits pwsh's `PSModulePath` (the PS7 module folders first) and the invariant script dies on `Get-FileHash` ("not recognized"), while the same script passes when powershell.exe is started directly from pwsh and when the harness is launched from a Windows shell (measured 2026-09-29: python under pwsh -> child PSModulePath `...\PowerShell\7\Modules;...`, Get-FileHash unresolved; python under Git Bash -> the Windows PowerShell paths, resolved). The harness could set the child's `PSModulePath` to the Windows PowerShell paths itself (`tests/mutate-hardening-guards.py:45`).

### Lane fix/petstudio (Phase 2-3, branched after the host and tools merges)

10 items: 10 to fix or disposition, 0 info to disposition.

- 📌 **F155** [high/performance] Every analyze re-runs the XSD-validating parse and GDI+-decodes and tiles the whole sprite sheet on the UI thread through Xml.TryReadXml; the backlog's 'no second decode' refutation read the wrong lines (`modules/PetStudio/PetReport.cs:161`)
- 📌 **F150** [medium/correctness] Surface-pose growth stops at flip-action turns: the bundled sheep's ceiling walk reads MOVE, its wall descent Idle, and its wall-bounce `boing` reads CLIMB; the fixture check cannot fail on it (`modules/PetStudio/AnimCapability.cs:108`)
- 📌 **F158** [medium/race] Zip import: _importing is set only after the extraction await, so a second Import (or closing) during extraction deletes the tree the extractor is writing and the second pick is then refused (`modules/PetStudio/PetStudioWindow.cs:712`)
- 📌 **F157** [low/correctness] Picking an installed companion leaves the Save button in whatever state it had, so Save availability depends on history (`modules/PetStudio/PetStudioWindow.cs:587`)
- 📌 **F159** [low/performance] Synchronous recursive directory deletes and the temp sweep run on the UI thread in the zip click handler and in Closed (`modules/PetStudio/PetStudioWindow.cs:716`)
- 📌 **F160** [low/leak] SweepOrphanedExtractions runs only on the zip-import path, so a tree deferred by closing mid-import survives folder imports, Open and the installed picker (`modules/PetStudio/PetStudioWindow.cs:740`)
- 📌 **F162** [low/correctness] After a zip import the remembered skin folder is the temp extraction directory, not the folder the zip came from (`modules/PetStudio/PetStudioWindow.cs:794`)
- 📌 **F163** [low/race] Open and the installed picker are not refused during an import, so the finished conversion replaces whatever was loaded or typed meanwhile (Save is unaffected) (`modules/PetStudio/PetStudioWindow.cs:897`)
- 📌 **F164** [low/correctness] FindBundleRoot abandons the walk when it lists the first inaccessible subdirectory and reports 'no bundle'; bundles listed after it, including direct children of the chosen folder, are never checked (`modules/PetStudio/PetStudioWindow.cs:913`)
- 📌 **F165** [low/correctness] The 'Dropped N timeline step(s)' status from Resync is overwritten in the same Analyze call and never renders (`modules/PetStudio/PetStudioWindow.cs:994`)

### Lane fix/reminder (Phase 2-3)

11 items: 11 to fix or disposition, 0 info to disposition.

- ✅ **F186** **FIXED 2026-09-29 (reminder 1.0.7).** `CachingCalendarSource`'s latch (one bool, cleared only when the fetch returned) is now a start-stamped, generation-numbered attempt set: a refresh older than `RefreshDeadline` (three intervals, never under 60 s) is reported in the served snapshot's Error, one retry is kicked, parked attempts are capped at two, and a late result from an abandoned attempt is discarded when a newer one has landed. Asserted in `ReminderModule.SelfTest` through a `StallingProbe` with per-attempt gates. MUTATION: four cases in `tests/mutate-selftest-guards.py` (report, retry, cap, generation rule) all FIRED.
- ✅ **F189** **FIXED 2026-09-29 (reminder 1.0.7).** `IcsUrlSource.Download` reads the body itself under one 30 s `CancellationTokenSource` that covers headers and body, and checks the 8 MiB cap on every chunk rather than after buffering. Asserted in `ReminderModule.SelfTest` against a loopback `StallingFeedServer` (a raw TcpListener that sends 200 + a chunk and then stalls, or floods 2 MiB and then stalls), with the test itself bounded so the old shape fails rather than hangs. MUTATION: the token removed and the cap removed each FIRED as "hung past the 8 s bound".
- ✅ **F199** **FIXED 2026-09-29 (reminder 1.0.7).** `CheckDue` treats "no companion on screen" like quiet hours: the calendar reminders, the personal reminders and the briefing are skipped WITHOUT being marked, so they fire on the next tick with a pet while their window is open; `AnyCompanionOnScreen()` is AgentFlow's liveness gate plus the companion manager's `OnScreenMix()`, so a pet that was out before the module loaded counts too; the hold is logged once and its release once. Speech-off does not hold (AgentFlow's recorded decision; register entry under `#### fix/reminder`). Asserted in `ReminderModule.SelfTest` on the module's own tick over `RecordingHost` (held, delivered once a pet appears, not repeated, manager-only pet counts, once-only and briefing held then delivered, speech-off still spends). MUTATION: the gate removed FIRED; the manager fallback blinded FIRED.
- ✅ **F200** **FIXED 2026-09-29 (reminder 1.0.7).** `BuildSource` keeps one source instance per slot (`_slotSources` / `_slotTypes`) and constructs a new one only when that slot's TYPE changed, so an Apply that toggles a chime or edits quiet hours keeps every slot's cache and last-good list and fetches nothing; URL and file edits still reach the existing instance through its live getter and `RefreshKey`. The rebuild had been the only recovery from a refresh that never returned; `CachingCalendarSource`'s deadline (F186) is now that recovery. Asserted in `ReminderModule.SelfTest` through the pane's own `Save` on a real `LocalJsonSource` slot (same instance and events still served after a chime-only Apply; Off drops it; back on is a fresh instance). MUTATION: the type comparison made to never match FIRED.
- ✅ **F204** **FIXED 2026-09-29 (reminder 1.0.7).** `AggregateCalendarSource` carries per-slot health on the snapshot (`CalendarSnapshot.SlotHealthy`) and `PruneFiredAgainstFeed` judges each id by ITS slot: dropped when its slot fetched cleanly and the event is gone, kept when its slot errored or is still loading (the 1.0.3 re-nag fix, per slot), dropped when no configured slot claims it; a snapshot without per-slot health keeps the whole-snapshot rule. The "cannot leak" comment is rewritten with the mixed case. Asserted in `ReminderModule.SelfTest` through the real aggregate (healthy slot pruned beside an erroring one; erroring slot's id kept; loading slot keeps its ids beside a loaded neighbour) and in `AggregateCalendarSource.SelfCheck`. MUTATION: the whole-snapshot gate restored FIRED; the aggregate reporting every slot healthy FIRED.
- ✅ **F188** **FIXED 2026-09-29 (reminder 1.0.7).** `Chime.Play` hands a custom chime to `Task.Run` (read plus `host.PlaySound`, so the host's decode leaves the UI thread too) behind an `Interlocked` single-flight guard, mirroring the host's own `NotificationSound.Play(deferCustomRead)`; the embedded default stays synchronous and is decoded once into a static array. A module-side byte cache was rejected at the site (it keeps the SMB probe per fire and saves no decode). Asserted in `ReminderModule.SelfTest` through a gated loader seam (Play returns while the read is open; the read ran on another thread; a second chime during the read is dropped; the bytes reach the host; the default is synchronous). MUTATION: the read run inline FIRED. Out of this lane's boundary: `IHost.PlaySound` in `PluginApi.cs` carries no thread note; the host already drives `AudioOutput` from a pool thread for the shared chime and the call reads only the module list and the volume, so the note belongs in Contracts.
- ✅ **F191** **FIXED 2026-09-29 (reminder 1.0.7).** `LocalJsonSource`'s interval is 10 s, clearly below the 20 s tick, so the re-read is kicked on every tick regardless of disk latency, and its comment now states the honest figure (a read kicked at tick N is served at N+1: two ticks worst case). `ReminderModule.SelfTest` holds `RefreshInterval` against `TickMilliseconds`. MUTATION: the interval set back to 20 s FIRED.
- ✅ **F192** **FIXED 2026-09-29 (reminder 1.0.7).** `FetchCore(string key, DateTimeOffset now)`: the base hands the key it computed on the caller's thread to the fetch, and `LocalJsonSource` / `IcsUrlSource` use it instead of calling `RefreshKey()` (the settings read) on the pool thread. A `KeyThreadProbe` in `ReminderModule.SelfTest` records every thread `RefreshKey()` ran on. MUTATION: `DoRefresh` recomputing the key off-thread FIRED.
- ✅ **F193** **FIXED 2026-09-29 (reminder 1.0.7).** A `teams.microsoft.com/meet/` provider sits beside the long `/l/meetup-join/` form and the free-tier `teams.live.com/meet/` form that was already accepted; `MeetingLinkDetector.SelfCheck` now names each failing case and asserts the short link is matched as Teams with its `?p=` passcode intact. MUTATION: the provider deleted FIRED.
- ✅ **F197** **FIXED 2026-09-29 (reminder 1.0.7).** `LogFeedErrorOnChange` keeps `_lastLoggedFeedError` (its own field, since the Agenda click also writes `_lastSnapshot`) and logs a feed error when it changes, plus one "recovered" line on the way back to healthy; the pane status still shows the error every tick. Asserted in `ReminderModule.SelfTest` (three erroring ticks log once; recovery once; a returning error again). MUTATION: the comparison removed FIRED.
- ✅ **F201** **FIXED 2026-09-29 (reminder 1.0.7).** `ICalendarSource` gained `Invalidate()` and `IsRefreshing`; "Check now" invalidates every slot, runs the due check, awaits the re-read (bounded at 8 s, an `await Task.Delay` so the options window keeps pumping) and runs it again against what arrived; the tray "Reminders" click invalidates before its check. The Agenda click is left reading the cache: its verb is speak, and the timer keeps that fresh. Asserted in `ReminderModule.SelfTest` through a one-hour-interval probe behind the aggregate and through `CheckNowAsync` on a module wired to `RecordingHost`. MUTATION: `Invalidate` emptied, and the aggregate not forwarding it, each FIRED.
- 📌 **N-reminder-01** [low/tests] `tests/mutate-selftest-guards.py`'s BASELINES never runs `--module-selftest=reminder` (blinkingled is the only `--module-selftest=` entry), so a reminder self-test that was already red would score every reminder case FIRED instead of refusing the baseline; the lane verified its baseline by hand (`tests/mutate-selftest-guards.py:676`)
- 📌 **N-reminder-02** [low/docs] `IHost.PlaySound` carries no thread note while the ABI's blanket rule says UI-thread only; the host already drives `AudioOutput` from a pool thread for the shared chime and Reminder 1.0.7 calls `PlaySound` from one for a custom chime (F188), so the contract should say the call is thread-safe (`src/DesktopAICompanion.Contracts/PluginApi.cs:790`)
- 📌 **N-reminder-03** [low/correctness] "Test this reminder" reports "✓ test sent" and the Agenda tray click speaks through `SayAll` while no companion is on screen, where the host drops the line; both could ask `AnyCompanionOnScreen()` (added for F199) and say so (`modules/Reminder/ReminderModule.cs:914`, `:994`)
- 📌 **N-reminder-04** [medium/tests] `tests/mutate-selftest-guards.py`'s `build_all()` rebuilds the host, Fortunes, BlinkingLed, PetStudio and CoreTests but not AiBrain or Reminder, so a whole run's closing "restoring and rebuilding the clean tree" leaves the LAST mutation's `AiBrain.dll` and `Reminder.dll` in `build\...\modules\`. Measured 2026-09-29 in lane fix/reminder: the very next `--only` run's baseline went red on `--aibrain-selftest` with "the already-said list is bounded, not a growing transcript", the mutation of the case that ran before, and `Test-ModuleSelfTests.ps1` run at that moment would have graded a mutated `Reminder.dll`. Add `AIBRAIN_CSPROJ` and `REMINDER_CSPROJ` to `build_all()`, or rebuild each case's own csproj in its `finally` after the byte restore (`tests/mutate-selftest-guards.py:707`)

### Lane fix/agentflow (Phase 2-3)

22 items: 21 to fix or disposition, 1 info to disposition.

- ✅ **F029** **FIXED 2026-09-29 (agentflow 1.4.12).** The sweep callback records whether this tick's click was confirmed (`pressedThisTick`), the tick hands it to the UI thread beside the note, and `LogApprovalAttempt(note, pressed)` applies its repeat guard to refusals only, so every confirmed press is written and a standing refusal is still written once. `SelfCheckCapabilityLog` asserts two identical presses give two lines, three identical refusals one, and a refusal that returns after the screen went quiet is written again. MUTATION: guard applied to presses again -> FIRED on "two identical confirmed presses on consecutive ticks are two log lines".
- ✅ **F032** **FIXED 2026-09-29 (agentflow 1.4.12).** The AdapterSuspect arm of `Apply` goes through `Explain`, once per session while it is live (the `_explained` prune re-arms it if the session leaves the window and returns), and says "no tool calls yet in session X" with the stale-adapter reading as a parenthesis for a busy session that stays that way, rather than asserting a fault about a chat-only session. The multiplier the verifier found (journal files under `subagents\workflows`) is N-agentflow-02. `SelfCheckExplainedPruned` applies the same AdapterSuspect detection three times and asserts one line, none saying "adapter may be stale" outright, and one more after the session leaves and returns. MUTATION: `Log` in place of `Explain` -> FIRED on "explained once, not once per tick".
- ✅ **F033** **FIXED 2026-09-29 (agentflow 1.4.12).** `Apply` keeps `_lastHeldBack` and writes the held-back line only when the whole note changes (so cooldown, then already-announced, then paused are each written once), clearing it when nothing is held or when a notice is delivered. `SelfCheckNotifyChannels` applies the same announced prompt on three consecutive ticks and asserts one held-back line beside the one delivery. The adjacent defect the verifier saw in the same lines (a second blocked session never announced while the first stands) is N-agentflow-04, fixed in the same commit. MUTATION: guard removed -> FIRED on "a notice held back on three consecutive ticks is logged once".
- ✅ **F043** **FIXED 2026-09-29 (agentflow 1.4.12).** `BlockedDetector.CodexAskingPolicies` is `{on-request, untrusted, on-failure}` (the last is what older Codex builds wrote; current Codex reads it as an alias of on-request); only `never` is described as a session that never stops to ask, an unread policy says nothing is known yet, and an unmeasured one such as `granular` stands down saying UNMEASURED. `SelfCheckCodexWatch` asserts untrusted and on-failure reach Blocked past 180 s, Working under it, and both reason texts. MUTATION: list shrunk to on-request -> FIRED on the untrusted assertion; unmeasured wording put back to "never stops to ask" -> FIRED on the wording assertion.
- ✅ **F053** **FIXED 2026-09-29 (agentflow 1.4.12).** `RuleLoader.ProjectPaths(cwd)` names `<cwd>/.claude/settings.json` and `settings.local.json`; `ScanRoot` hands every Claude session `RuleLoader.WithProjectRules(home, session.Cwd, cache)` (home tiers plus the project files, merged in Claude Code's order) to both `Evaluate` and the approvals tally, so a call allowed only by a project rule reads StalledButAllowed and is counted. `SelfCheckProjectRules` drives two sessions stalled on the same `git push` from two cwds, cold and warm, through the environment overrides. MUTATION: every session handed the home tiers alone -> FIRED on "allowed only by a PROJECT rule reads as slow".
- ✅ **F058** **FIXED 2026-09-29 (agentflow 1.4.12).** `SessionCache` retires a cursor that leaves the window into a `RetiredCursor` (committed offset, creation time, 256-byte head) instead of forgetting it, forgets retired entries whose file is gone, and a cursor created for a known path resumes with that identity: the fold restarts at zero, but a completion whose record ends at or below the tally floor (stamped through `FoldState.RecordEnd` onto `OutstandingCall.CompletedAtByte`) is not handed out again, and a truncated, replaced or rewritten file clears the floor through the existing reset checks. `SelfCheckResumedSessionNotRetallied` drives morning, lunch, resume, an in-place longer replacement with a different head, and deletion. MUTATION: floor ignored in Snapshot -> FIRED on "tallies only what was appended"; head not carried on resume -> FIRED on "a different file under the same name clears the floor".
- ✅ **F059** **FIXED 2026-09-29 (agentflow 1.4.12).** `ActiveTranscripts` walks the tree itself, one listing per directory inside its own try (write times still arrive with the enumeration), counts a folder that refuses a listing in a new `out int inaccessible` and carries on; `ScanRoot` puts the count on the notes channel as a state note ("cannot list N folder(s) under the ... transcript root", no path), which the new `LogScanNotes` writes once and re-arms when it stops being reported. `SelfCheckActiveTranscriptsAgrees` denies ListDirectory on a temp folder directly under the root for this process's own account (removed in finally), asserts the deny took, the count is 1, the transcripts on both sides of it are found, and that the pre-F059 oracle loses them. MUTATION: walk gives up at the denied folder -> FIRED; denied folder skipped uncounted -> FIRED; state note written every tick -> FIRED.
- ✅ **F061** **FIXED 2026-09-29 (agentflow 1.4.12).** `FixDanglingComma` and `IndexOfTopLevelBrace` work on `BlankLineComments(text)`, whose indices are valid in the original, so nothing is counted through comment-stripped text any more and `CountUpTo` is gone. `SelfCheckVsCodeSetup` drives the stock argv.json shape, its `// "disable-hardware-acceleration": true,` comment included, with the key appended LAST, and asserts Disable returns the stock file byte for byte and parses as JSONC; a `{` inside a header comment is asserted too. MUTATION: stripped text put back for the comma -> FIRED on the byte-for-byte assertion; put back for the brace -> FIRED on the brace assertion.
- ✅ **F026** **FIXED 2026-09-29 (agentflow 1.4.12).** A volatile `_pressArmed` follows the mode wherever the UI thread sets it (Init, the pane's Save, both tray rows) and is cleared in `BeginShutdown`; the sweep callback hands `StillArmed` (`_pressArmed && !_shuttingDown && _host != null`) to a new `Decide` overload that consults it once more immediately before the budget and the click, returning "stood down before the click" instead of pressing. The window that remains is the click's own round trip, where the renderer-side label re-check is the last guard, so the claim is that a press is no longer STARTED after the switch moves. `SelfCheckTeardown` asserts the tray toggle arms and disarms it, the pane's Save disarms it, BeginShutdown disarms it with the host still attached, and that `Decide` with a false gate stands down without charging the budget while a true gate reaches the click. MUTATION: `StillArmed` ignoring the switch -> FIRED on "switching it off from the tray disarms a press already in flight"; `Decide`'s last look disabled -> FIRED on "stood down before the click".
- ✅ **F028** **FIXED 2026-09-29 (agentflow 1.4.12).** `PressBudget._pressLimit` is `volatile`, and the `_pressBudget` doc now states the contract precisely: the list and repeat state are the poll worker's alone, the limit is a volatile int the UI thread writes from `SavePaneValues` so a limit the user has just raised reaches the live budget at once. The immediate-apply intent was kept over the snapshot-on-tick alternative because raising the limit is what someone does the moment it has stood the module down. `SelfCheckPressBudget` asks the runtime (a required modifier of `IsVolatile` on the field); `SelfCheckSavedSettingsReachInit` asserts a saved limit is live at Init and a limit saved on the pane reaches the budget without a tick. MUTATION: `volatile` removed -> FIRED on "the press limit is a volatile int".
- ✅ **F031** **FIXED 2026-09-29 (agentflow 1.4.12).** `NoRuleFilesNote` now says what zero rule files changes ("every stalled Claude command call is treated as a prompt ... and the approvals audit records nothing"), is emitted only when Claude is watched, and the Scan comment that claimed "every call reads Undecidable" is corrected. `SelfCheckNoRuleFilesIsSaid` asserts a Codex-only scan gets no note, a stalled default-mode Bash call against an empty RuleSet is Blocked, and the wording says prompt-and-audit rather than stand-down. MUTATION: watchClaude gate removed -> FIRED on the Codex-only assertion.
- ✅ **F034** **FIXED 2026-09-29 (agentflow 1.4.12).** `Apply` computes `delivered = spoke || NotifySoundOn || Animate` and, in a speaking mode with nothing delivered, writes "recorded a notice about X waiting Ns in session S (no notify channel is enabled)" instead of "signalled about"; Log mode keeps "signalled", because there the log is the channel, and the one-shot is still spent, which is the decision Apply's comment already recorded for this case. `SelfCheckNotifyChannels` gains the fourth case, Notify with all three channels off: nothing spoken, chimed or animated, no "signalled about", one "recorded a notice", the one-shot spent. MUTATION: `delivered` forced true -> FIRED on "the log does not claim it signalled anyone".
- ✅ **F037** **FIXED 2026-09-29 (agentflow 1.4.12).** Not latent: during this campaign a count-based assertion in `SelfCheckRules` went red in one harness baseline and green in the next with nothing changed, because the first instance's Init scan grew the static rule caches under it. Every module instance in the self-test now seeds mode Off before Init (the ten that were unseeded), switching to Notify or Log afterwards where the assertions are about a speaking mode; `SelfCheckAutoApprove`, whose subject is the tray's Off->Watching transition that calls OnTick, sets a `SuppressScanForSelfTest` seam checked at the top of OnTick; the whole run points the three `AGENTFLOW_*` overrides at an empty scratch root so no group can read the developer's real transcripts or settings; and a `ScanEverStartedForSelfTest` flag is asserted false on the seeded instances. MUTATION: the first instance's seed removed -> FIRED on "the first self-test instance never started a background scan".
- ✅ **F042** **FIXED 2026-09-29 (agentflow 1.4.12).** `LoadPendingValues` fetches the installed pets and the on-screen mix once at the top of a build and clears both memos in its finally, so `InstalledPets()` and `OnScreenPetTypeIds()` answer from the load's memo inside a build and fetch as before outside one (Init's first schema, the self-test's direct calls); nothing can go stale across builds. The stale "Fifty-three companions ship with the app" paragraph names no count now. `SelfCheckPetChoices`' FakePets counts both calls and asserts one of each per `LoadPending`, twice, so the memo is proven per-load. MUTATION: the memo bypassed -> FIRED on "one pane build asks for the installed pets ONCE".
- ✅ **F044** **FIXED 2026-09-29 (agentflow 1.4.12).** `CdpApprover.AgentTargets` reads `/json/list` once and classifies each entry by the Claude or Codex marker, Claude's first, replacing `ClaudeTargetIds`/`CodexTargetIds`/`TargetIds`; the HttpClient comment counts the real requests (one list, one version, a third for a press). `FakeCdpServer` counts list GETs and the WIRE cases assert one per sweep, including the two-agent sweep. MUTATION: the list fetched a second time -> FIRED on "one sweep fetches /json/list ONCE, not once per agent".
- ✅ **F046** **FIXED 2026-09-29 (agentflow 1.4.12).** `CdpSession` owns one 16 KB byte buffer, one char buffer, one `Decoder` and one `StringBuilder` for its life; `Receive` resets the decoder and clears the builder on the way in. Stated as the property (one set per session, not per message), no number: nothing was measured cold. Every receive buffer is allocated through a counted path, and the WIRE case asserts a whole sweep (attach, evaluate, detach, each reply preceded by an event) allocates exactly one. MUTATION: a buffer allocated per receive again -> FIRED on "one sweep allocates ONE set of receive buffers, not one per message".
- 📌 **F050** [low/correctness] Rule regex '.*' does not cross a newline, so a segment holding a quoted multi-line body evaluates WouldPrompt regardless of allow rules (`modules/AgentFlow/PermissionRules.cs:125`) **NOT DONE 2026-09-29 (lane fix/agentflow).** The module's matcher is a documented port of `ai-acolyte/src/permission-match.js`, the canonical implementation behind the wildcarding pass, and `docs/agentflow/agentflow_join.py` is its second port; all three build the regex without a dotall flag, so the newline behaviour is shared and adding `RegexOptions.Singleline` here alone would make the port disagree with its reference on exactly the case no differential varies. Two steps sit outside this lane: (1) confirm what Claude Code itself does with a quoted newline and with the heredoc-in-`$( )` shape (it may force a prompt on command substitution regardless of allow rules, in which case only the plain quoted-newline shape is a misreport); (2) change the JS original first, then both ports, and add both shapes to the shared splitter/matcher corpus so the newline axis stops being degenerate. Recorded under `#### fix/agentflow` in `docs/DESIGN-REGISTER.md`.
- ✅ **F051** **FIXED 2026-09-29 (agentflow 1.4.12).** `Evaluate` normalises the permission once, uncached, and hands it to `RuleMatchesNormalized`; only RULE strings enter `NormalizedRules`/`CompiledRules`, so the 5000 cap means what its comment says again and no command text sits in the static map. `SelfCheckRules` asserts that none of forty distinct commands becomes a key of the normalised-rule cache, through a `NormalizedCacheHolds` seam, while the rule they matched is one (a before-and-after COUNT was written first and went red in one baseline run under a concurrent scan, so the key form replaced it); `SelfCheckCacheBound`'s comment updated to one key per rule. MUTATION: the permission routed through the cached normaliser again -> FIRED on "none of forty distinct commands becomes a key".
- ✅ **F055** **FIXED 2026-09-29 (agentflow 1.4.12).** `RuleCache` (RuleLoader.cs), owned by `SessionCache`, keys every settings path on (exists, LastWriteTimeUtc, Length): one stat per path per tick, a parse only when the key moves, a vanished or appeared file counts as a change, and `Retain` drops paths the last tick did not ask for. The cold path ("Check now", the assertions) still reads whole. `SelfCheckProjectRules` asserts two parses on the first tick, none on the second, one more after an edit whose new rule is then in force. MUTATION: stat key never matches -> FIRED on "a second tick over unchanged settings files parses nothing".
- ✅ **F057** **FIXED 2026-09-29 (agentflow 1.4.12).** `TranscriptReader.TryFoldBytes` parses each record from the cursor's own bytes with `JsonDocument.Parse(ReadOnlyMemory<byte>)` and folds inside the using, so the UTF-16 copy and the root clone are gone and the parser's metadata is what remains; `TryParse(string)` stays for the whole-file reader. Stated as the property (two of three copies removed), no number, because nothing was measured cold. `SelfCheckFoldEquivalence` now holds the LF and a CRLF fixture equal to the whole-file parse at every byte split. MUTATION: every record dispatched to the Codex fold -> FIRED on the fold equivalence.
- ✅ **F060** **FIXED 2026-09-29 (agentflow 1.4.12).** `WithPort` finds the value span on the blanked text (a quoted token, or a run up to whitespace, comma or brace) and splices only that span, so a trailing comment on a last-member port line survives and a comma inside the comment no longer cuts the line in two. Fixtures `9321 // added for the pet` and `9321 // see a, b` in `SelfCheckVsCodeSetup`, both asserted to parse as JSONC. MUTATION: the value scan run to the newline again -> FIRED on "keeps the comment".

Info, disposition only:

- ✅ **F030** **FIXED 2026-09-29 (agentflow 1.4.12)** for the rules half, through the same `RuleCache` as F055 (a stat per path per tick, a parse only on change). The argv.json read inside `Inspect` stays per tick and is recorded under `#### fix/agentflow` in `docs/DESIGN-REGISTER.md`: the cost of that call is the port probe beside it, which `docs/BACKLOG-CLOSED.md` already records as a deliberate 250 ms per tick with backoff rejected, and the small file read it accompanies was not measured, so no saving is claimed for it.

Found by lane fix/agentflow while working the list:

- ✅ **N-agentflow-01** [low/tooling] tests/mutate-agentflow.py restored its targets through `utf-8-sig`, which writes a BOM on every write, so a BOM-less target (VsCodeSetup.cs, RuleLoader.cs, TranscriptCursor.cs) came back one byte longer than the baseline and git showed it modified after a clean run, in the harness whose header promises a byte-identical restore (`tests/mutate-agentflow.py:828`) **FIXED 2026-09-29 (agentflow 1.4.12).** read() and write() use plain utf-8 in both directions, so a BOM survives as U+FEFF at the head of the text and is written back exactly as it came; verified by a run with a BOM-less target after which `git status` named only the edited files and the first three bytes of all three files matched HEAD.
- ✅ **N-agentflow-02** [medium/correctness] ActiveTranscripts skipped only a file whose IMMEDIATE parent was named `subagents`, so the workflow journals at `subagents\workflows\wf_*\journal.jsonl` were scanned as sessions; the F032 verifier measured them at 954 of the 1,042 noise lines in one day's diagnostic log on this box (`modules/AgentFlow/TranscriptReader.cs:215`) **FIXED 2026-09-29 (agentflow 1.4.12).** The walk does not descend into a directory of that name at any depth; the differential oracle skips by ancestor too, and the fixture carries a journal two levels down that both must exclude. MUTATION: the skip removed at the point of descent -> FIRED on "the workflow journal under subagents is not a session, at any depth".
- ✅ **N-agentflow-03** [low/tests] `SelfCheckScanEquivalence` wrote `"cwd":"C:\work"` into its fixture JSON, and `\w` is not a JSON escape, so both tool_use records failed to parse and every equivalence assertion compared two scans of tool_results alone with nothing outstanding; the post-append cold scan was also handed a FRESH counted set, which means "count the history again", so the two tallies could only ever agree while nothing completed (`modules/AgentFlow/AgentFlowModule.cs`, `SelfCheckScanEquivalence`) **FIXED 2026-09-29 (agentflow 1.4.12).** The escapes are doubled in the file, a permission-mode record makes the session default-mode, a fixture home makes the tally deterministic, the cold path keeps its own counted set, and two witnesses assert that the outstanding call and its cwd folded and that the appended completion alone was tallied on both paths. MUTATION: the invalid escape put back on the record that supplies the outstanding call -> FIRED on the outstanding-call witness.
- ✅ **N-agentflow-04** [medium/correctness] `Apply` took the FIRST blocked detection as the one to announce and returned on its budget refusal, so once that session had been announced a SECOND session that blocked while the first still stood was never announced at all, on any tick, until the first was answered (`modules/AgentFlow/AgentFlowModule.cs`, `Apply`; seen by the F033 verifier) **FIXED 2026-09-29 (agentflow 1.4.12).** `Apply` prefers the first blocked detection the budget has NOT yet announced and falls back to the first blocked one only when every one has been, so the held-back line still describes a real prompt. `SelfCheckNotifyChannels` announces one session, then applies two blocked detections with the announced one first and the cooldown lifted, and asserts the second is spoken. MUTATION: the preference removed -> FIRED on "a second session that blocks while the first still stands IS announced".

### Lane fix/settings (Phase 3): LocalData.cs and AppSettingsStore.cs, on-disk format unchanged

1 items: 1 to fix or disposition, 0 info to disposition.

- ✅ **F361** [medium/performance] Every changed settings field is a synchronous full-document durable rewrite on the UI thread (~130 ms at the default pet's 1.17 MB, ~1 s extrapolated at the largest shipped pet) because the active pet's XML is embedded in settings.json (`src/Portable/LocalData.cs:889`) **FIXED 2026-09-29 (coalesced; the per-write cost is recorded, not removed).** LocalData.BeginBatch()/Batch.Commit(): setters inside a batch apply in memory and answer true, Commit is the one durable write and returns its result, rolling every setter back on failure; a batch belongs to the thread that opened it, so a setter from another thread writes through at once. The Preferences Apply (22 setters) and Reset to defaults (20) run inside a batch, Commit before anything reads the store back, and the reset now reports a failed commit instead of rebuilding over unmoved values. The saving is a COUNT, checked through AppSettingsStore.DurableWrites in --hardening-selftest (three setters outside a batch: three writes; four inside: one; abandoned: none, rolled back; a commit the store refuses: none, rolled back). Not a debounced background writer, per the finding: the bools are read as durable results. The 1.17 MB per write stays until the embedded pet XML leaves settings.json, a schema change recorded in the register for a later host release; the on-disk format is unchanged. MUTATION: `_batchDirty = true` removed FIRED (runtime); `ok &= batch.Commit()` demoted FIRED (gate).

### Lane fix/deadcode (Phase 4, merged last): dead code, duplication, comment drift

132 items: 91 to fix or disposition, 41 info to disposition.

- 📌 **F001** [low/maintainability] SDK pin duplicated in three setup-dotnet steps (plus prose copies in Readme.md:507 and handoff.md:1792) instead of read from global.json via global-json-file; a bump either breaks CI until two files are edited or silently installs an unused SDK, never a wrong-SDK artifact (`.github/workflows/build.yml:28`)
- 📌 **F225** [low/maintainability] build.ps1 asserts declared-but-missing modules only; a modules\<Name>\*.csproj absent from $moduleProjects is compiled by no gate, and Test-ModulePublishFreshness.ps1's own error text routes such a module into an exemption list that no other check can see past (`build.ps1:226`)
- 📌 **F004** [low/dead-code] denial_by_id is written and read back with the same key: a write-only dict (`docs/agentflow/agentflow_backtest.py:88`)
- 📌 **F006** [low/dead-code] max_size=None is not a websocket-client option and is silently swallowed (`docs/agentflow/agentflow_cdp_probe.py:206`)
- 📌 **F013** [low/dead-code] The 'windows only' guard is unreachable: WinDLL is bound at import (`docs/agentflow/agentflow_cpu.py:864`)
- 📌 **F022** [low/dead-code] wixpdb assert-and-remove in build-installer.ps1 can never see a file: -pdbtype none disables the pdb, and any pdb would land beside the staged MSI, not in dist (`installer/build-installer.ps1:463`)
- 📌 **F024** [low/maintainability] Shortcut description is hardcoded twice in New-RuntimeWixFragment.ps1 and disagrees with ProductVersion.props; only the exe's version-resource Comments carries the canonical text (the About window and catalog do not show a description) (`installer/New-RuntimeWixFragment.ps1:139`)
- 📌 **F025** [low/dead-code] `enabledNow` and the `enabled` argument of ShouldProbePort/MayLookNow are true at every call site in the repo (no caller, tests included, ever passes false); `_timer.Start()` in SetEnabledFromTray is a no-op because the timer is never stopped outside Shutdown and keeps ticking every 10 s in Off mode (`modules/AgentFlow/AgentFlowModule.cs:548`)
- 📌 **F027** [low/maintainability] 15 members carry 20 orphaned <summary> blocks that belong to other, still-existing members (SafeToolName, SafeExtension, LogApprovalAttempt, ArgvPath, _portAnswering, SettingArgvPath and ten undocumented SelfCheck groups), plus a glued 1.4.10 changelog line and a stale '38 of the 39' count (`modules/AgentFlow/AgentFlowModule.cs:844`)
- 📌 **F045** [low/dead-code] Dead code: the five-argument CdpApprover.Click overload has no caller and silently defaults the agent to Claude (`modules/AgentFlow/CdpApprover.cs:525`)
- 📌 **F047** [low/dead-code] Write-only field: CommandSegment.Separator is computed for every segment and read by nothing in the repo or either differential harness (`modules/AgentFlow/CommandSplitter.cs:9`)
- 📌 **F048** [low/maintainability] FakeCdpServer serves one connection at a time, so the nested Click connection production opens inside the sweep callback cannot be tested and no wire test performs a press (`modules/AgentFlow/FakeCdpServer.cs:85`)
- 📌 **F052** [low/maintainability] PressBudget.Signature separates options with an invisible U+001F inside a char literal (`modules/AgentFlow/PressBudget.cs:167`)
- 📌 **F054** [low/maintainability] RuleLoader re-implements the fully-qualified override check with different semantics from TranscriptReader.FullyQualifiedOverride while claiming the same convention (`modules/AgentFlow/RuleLoader.cs:32`)
- 📌 **F056** [low/dead-code] Dead code: TranscriptCursor.Path is never read (`modules/AgentFlow/TranscriptCursor.cs:145`)
- 📌 **F064** [low/maintainability] Six AiBrain comments describe removed mechanisms, one of them misstating live vision routing (drop uses vision, poke does not); ObserveFailure is duplicated within the module; the Info changelog block is a recorded repo convention, not a defect (`modules/AiBrain/AiBrainModule.cs:647`)
- 📌 **F075** [low/dead-code] ToBase64PngScaled's scaling branch is unreachable (`modules/AiBrain/engine/AiBrain.cs:1315`)
- 📌 **F076** [low/dead-code] DescribeOcrEngine has no caller (its doc and the comment at 1396 describe it as live); DispositionSample.ElapsedMs is write-only, though the log already records per-request latency (`modules/AiBrain/engine/AiBrain.cs:1378`)
- 📌 **F082** [low/maintainability] One throw in Run, or a rename of AiSessionManager._operation, collapses the aibrain probe to a single EXC line and skips the two check groups behind the after-retire checks (fail-side only) (`modules/AiBrain/engine/AiEngineProbe.cs:27`)
- 📌 **F087** [low/maintainability] AiExecutablePolicy: two internal helpers are reached only by the probe (test seams, not dead), and the resolve ladder is duplicated in ResolveOllamaExe and ResolveTesseract (`modules/AiBrain/engine/AiExecutablePolicy.cs:91`)
- 📌 **F088** [low/dead-code] AiPaths.LegacyMigrationEnabled is hard-coded false, and its branch would be skipped by its own path-equality guard even if enabled (`modules/AiBrain/engine/AiPaths.cs:36`)
- 📌 **F093** [low/dead-code] Seven Fortunes-era fields are persisted on AiSettings with no reader; the retention rationale contradicts the file's own RandomDrop precedent (`modules/AiBrain/engine/AiSettings.cs:136`)
- 📌 **F097** [low/dead-code] AiSettings: two normalization verification loops that cannot set `changed`, inert [JsonIgnore] on members STJ never serializes, Save() called only by probes, and a comment misstating STJ's field-sink rule (the field guard itself is legitimate) (`modules/AiBrain/engine/AiSettings.cs:860`)
- 📌 **F108** [low/dead-code] AiProviders' three local presets and Name/NeedsKey/IsLocal are unread, the three Provider=='ollama' branches are unreachable, and Get()'s ollama fallback would overwrite the local Endpoint if ever fed an unknown id (`modules/AiBrain/engine/OpenAiCompatBackend.cs:173`)
- 📌 **F120** [low/maintainability] FilterSelfTest's and Embedder.SelfTest's failure detail go to temp files nothing reads; the probe, the gate and CI see only PASS/FAIL for both (`modules/Fortunes/engine/FortuneEngineProbe.cs:239`)
- 📌 **F124** [low/maintainability] Cross-assembly copies kept in sync by hand, one with a recorded drift incident (FortunePackLoadPolicy) (`modules/Fortunes/engine/FortuneProvider.cs:78`)
- 📌 **F131** [low/dead-code] TryValidateTaggedPack is exercised only by RunParserSelfTest, the module copy of TryValidatePackMetadata and TaxonomyVersion have zero references, and a downloaded pack is written and counted 'installed' before anything checks it parses; the loader then drops it with a bare continue (`modules/Fortunes/engine/FortuneProvider.cs:1463`)
- 📌 **F136** [low/dead-code] The re-warm and cancellation-token surface of SmartFortunes has no caller: an internal constructor, a token parameter that is always None, and a previous-warm chain that is always Task.CompletedTask (`modules/Fortunes/engine/SmartFortunes.cs:141`)
- 📌 **F152** [low/dead-code] ChainStep.Copy() has no caller anywhere in the repository (`modules/PetStudio/BehaviourChain.cs:33`)
- 📌 **F156** [low/dead-code] Dead memoisation guards on BuildTopBar/BuildBottomBar and an unused parameter on ColumnSplitter (`modules/PetStudio/PetStudioWindow.cs:204`)
- 📌 **F161** [low/maintainability] Two stranded doc blocks in PetStudioWindow displace three members (both sit on the SweepAgeHours const) and two in OptionsWindow displace two (`modules/PetStudio/PetStudioWindow.cs:740`)
- 📌 **F167** [low/dead-code] AudioDevice.Id is write-only; selection by display name collapses same-named endpoints (`modules/Remembrance/AudioDevices.cs:9`)
- 📌 **F174** [low/maintainability] Four changelog lines are garbled (1.0.11 and 1.0.5 appended to the previous entry's last line, 1.0.7 and 1.0.6 duplicated on one line) and the 1.0.11 purge claim has been false since 1.0.16 (`modules/Remembrance/RemembranceModule.cs:75`)
- 📌 **F185** [low/dead-code] ParseReleaseJson is used only by the remembrance self-test; the suite's only digest assertion runs through it rather than the production ParseReleaseListJson path (`modules/Remembrance/WhisperInstaller.cs:578`)
- 📌 **F187** [low/dead-code] Chime.Play(IHost) overload has no callers (`modules/Reminder/Chime.cs:118`)
- 📌 **F190** [low/maintainability] IcsUrlSource.ParseIcs was made internal static to be testable without a network, and nothing tests it; the SelfTest suites array omits IcsUrlSource entirely (`modules/Reminder/IcsUrlSource.cs:77`)
- 📌 **F195** [low/dead-code] QuietHours instance API (constructor, Enabled, instance IsQuiet) is reached only by its own SelfCheck; production uses the static form (`modules/Reminder/QuietHours.cs:36`)
- 📌 **F198** [low/maintainability] Four near-identical slot-lookup loops (SlotLabel, SlotChimePath, SlotChimeOn, SlotSpeaker); SlotSpeaker differs only in null-handling shape, with no observable difference (`modules/Reminder/ReminderModule.cs:319`)
- 📌 **F202** [low/maintainability] Chime size cap duplicated as a literal in BrowseChimeInto and as Chime.MaximumCustomBytes (both 8 MiB today); drift would be silent because Chime.LoadCustom falls back to the default chime without logging (`modules/Reminder/ReminderModule.cs:822`)
- 📌 **F203** [low/dead-code] Three HH:mm parsers with divergent acceptance rules (a leading sign and whitespace next to the colon pass the briefing and personal-reminder copies but not QuietHours); only the QuietHours copy is tested; category is duplication, not dead code (`modules/Reminder/ReminderModule.cs:1061`)
- 📌 **F211** [low/dead-code] Install-LockedWixToolchain.ps1's private -ToolPath mode (~69 lines) has no caller anywhere in the repo and build-installer.ps1 can only consume the global install (`packaging/Install-LockedWixToolchain.ps1:132`)
- 📌 **F217** [low/dead-code] Null guard on nuspec dependencies cannot cover an ABSENT element under strict mode (its false branch is reachable only for an empty <dependencies/>) (`packaging/New-NuGetPackages.ps1:94`)
- 📌 **F224** [low/dead-code] WixToolchainPolicy.ps1:442-444 claims the verified WiX files are 'pinned' between hashing and execution; the handle class holds no stream, so the Inputs retention plumbing across three scripts only disposes no-ops (`packaging/WixToolchainPolicy.ps1:412`)
- 📌 **F235** [low/dead-code] RecordingHost.TouchEvents is dead and its CS0067 rationale does not apply: the Raise* reads already count as use (proved by mutation build) (`src/DesktopAICompanion.ModuleKit/Testing/RecordingHost.cs:409`)
- 📌 **F238** [low/dead-code] ActiveWindow.CaptureContext computes foregroundBounds it never uses; the comment says it feeds ScreenContext.ForegroundWindowBounds (`src/dotNet/Ai/ActiveWindow.cs:140`)
- 📌 **F239** [low/dead-code] TSequence.CalculateTotalSteps and Animations.ScaleFactor/ScaleFactorD have no callers anywhere in the repo (`src/dotNet/Animations.cs:418`)
- ✅ **F242** [low/dead-code] Duplicate of F241: SetNextGeneralAnimation evaluates the chosen animation's seven expressions against the primary screen and FormCompanion immediately re-evaluates them for DisplayIndex; the first pass is discarded (`src/dotNet/Animations.cs:1231`) **CLOSED-VERIFIED 2026-09-30.** Duplicate of F241: the pass it describes is the one F241 removed (AnnounceChosenAnimation evaluates nothing), and F241's probe and invariant cover it.
- 📌 **F252** [low/dead-code] CompanionTypeRegistry.Entries has no callers; the Xml frame-injecting constructor is live (used by SecuritySelfTest.CheckSharedSpriteFrameOwnership) (`src/dotNet/CompanionTypeRegistry.cs:91`)
- 📌 **F253** [low/dead-code] CompanionXmlValidator's 4-argument TryParse and its 15 cancellation checkpoints are unreachable: every caller uses the CancellationToken.None overload, and the one caller holding a token parses synchronously on the UI thread (`src/dotNet/CompanionXmlValidator.cs:366`)
- 📌 **F266** [low/dead-code] FallDetect issues a second GetWindowText into a StringBuilder that is never read, and both detectors store titles they never use (`src/dotNet/FormCompanion.cs:1675`)
- 📌 **F326** [low/maintainability] AiBrain's storage folder sits inside the modules root it hands to LoadFrom, producing a spurious load failure every run (`src/dotNet/Plugins/AiBrainModuleSelfTest.cs:45`)
- 📌 **F334** [low/maintainability] CompanionHost keeps a second catalog cache with no TTL beside RemoteCatalogClient's shared one, and 'check now' cannot invalidate it (`src/dotNet/Plugins/CompanionHost.cs:620`)
- 📌 **F337** [low/maintainability] Helpers duplicated inside the host assembly (SafeLibraryDir, Short, IsInLibrary/LibraryFolderExists, IsShell/IsCloaked with six P/Invokes) (`src/dotNet/Plugins/CompanionHost.cs:1103`)
- 📌 **F340** [low/maintainability] Six host-side IHost fakes each restate all 43 members with copied comments; their storage/null/TEMP deltas are where three sibling self-test defects live (`src/dotNet/Plugins/ModuleConventionSelfTest.cs:302`)
- 📌 **F346** [low/maintainability] Doc-comment and structure drift in ModuleHostSelfTest, plus one stale comment in the WPF test (`src/dotNet/Plugins/ModuleHostSelfTest.cs:208`)
- 📌 **F348** [low/maintainability] --module-host-selftest writes its transcript only to %TEMP%, so the gate's red-run diagnostics print nothing for it (hardening, pettyperegistry, fullscreen and catalog share the shape) (`src/dotNet/Plugins/ModuleHostSelfTest.cs:596`)
- 📌 **F349** [low/dead-code] Six private IHost fakes and five FakeCompanion copies in the host self-tests duplicate ModuleKit.Testing (duplication, not dead code) (`src/dotNet/Plugins/ModuleHostSelfTest.cs:603`)
- 📌 **F350** [low/dead-code] Write-only recorders (LastSay, LastSayPet, SayAllCount, sound counters, OpenedLink, captured poke responders) across the host's IHost fakes; RaiseFullscreen is unused in five fakes but doubles as the CS0067 suppressor (`src/dotNet/Plugins/ModuleHostSelfTest.cs:635`)
- 📌 **F283** [low/dead-code] The #else (non-PORTABLE) Main branch is dead and cannot compile (`src/dotNet/Program.cs:455`)
- 📌 **F289** [low/dead-code] ScalePolicy integer-path helpers Scale, FitFactorForFrame, StatusText and ClampFactor are used only by CoreTests (8 assertions at tests/DesktopAICompanion.CoreTests/Program.cs:969-990); no production caller (`src/dotNet/RuntimeGeometry.cs:30`)
- 📌 **F295** [low/maintainability] Hardening and registry self-tests report only to %TEMP%, so a CI failure shows just the exit code; the catch-all also drops the stack, so a renamed reflected member reads as a bare NullReferenceException (`src/dotNet/RuntimeHardeningSelfTest.cs:1315`)
- 📌 **F296** [low/dead-code] SecureDownload.TryValidatePinnedRawGitHubUrl and CommitPattern have no production caller; only two SecuritySelfTest assertions reach them (`src/dotNet/SecureDownload.cs:28`)
- 📌 **F298** [low/maintainability] SecuritySelfTest.Run has no catch-all: an unexpected exception crashes the process and loses the summary (`src/dotNet/SecuritySelfTest.cs:20`)
- 📌 **F302** [low/dead-code] SecuritySelfTest.DiagnosticOwnedValue is never instantiated (`src/dotNet/SecuritySelfTest.cs:1252`)
- 📌 **F311** [low/dead-code] LoadNewXMLFromString's InvokeRequired marshal block is unreachable (both callers are UI-thread) and would not protect a no-pet off-thread caller anyway (`src/dotNet/StartUp.cs:1343`)
- 📌 **F319** [low/dead-code] Dead assignments inside the border/gravity loops and a mislabelled spawn Y diagnostic (`src/dotNet/Xml.cs:316`)
- 📌 **F322** [low/dead-code] CAP is defined and never read (`src/Fortunes/strip-authors.py:38`)
- 📌 **F357** [low/dead-code] AppPaths fortunes-migration cluster: seven members with zero callers, TryMigrateFilesOnce and its two helpers reachable only from CoreTests, and a comment at 130-135 that claims production callers (`src/Portable/AppPaths.cs:56`)
- 📌 **F358** [low/maintainability] AtomicFile and CrossSessionLock are duplicated verbatim between AppSettingsStore and ModuleKit with nothing keeping them in step; the host's replaceFile seam is dead (`src/Portable/AppSettingsStore.cs:1341`)
- 📌 **F359** [low/dead-code] GetPetSizeLevelNoLock is dead, and the comment above it claims the opposite (`src/Portable/LocalData.cs:187`)
- 📌 **F360** [low/dead-code] GetAppUpdateLastCheckUtc duplicates ParseStamp while the neighbouring comment says the parser is shared by all three stamps (`src/Portable/LocalData.cs:550`)
- 📌 **F362** [low/dead-code] Properties.Settings is a write-only mirror (11 writes, 0 reads) whose .settings machinery and eager OpenExeConfiguration cost ~40-50 ms at every launch for a migration path that runs only on first launch (`src/Portable/LocalData.cs:916`)
- 📌 **F374** [low/dead-code] Six leftover members: PendingCheckSet.Count, MasonryPanel.ColumnWidth setter, the Label implicit style, LoadPetHeaderIcon pass-through, _initialPaneTitle field, and TrayPromotion.ResetForTests (no caller, not even a test) (`src/Portable/Wpf/OptionsWindow.cs:451`)
- 📌 **F324** [low/dead-code] Three embedded bitmaps in Resources.resx have no reference (esheep, help, install) (`src/Properties/Resources.Designer.cs:114`)
- 📌 **F378** [low/dead-code] Template: `_paths` is assigned in Init and never read, so the ModulePaths example its comment promises is absent from every scaffolded module (`templates/desktop-ai-companion-module/SampleModule.cs:25`)
- 📌 **F380** [low/maintainability] Template's Settings() lacks the `?? new MemoryModuleSettings()` guard that ModuleKit documents and four in-tree modules use; safe as scaffolded, a documented load-time trap once an author reads a setting in Init (`templates/desktop-ai-companion-module/SampleModule.cs:150`)
- 📌 **F385** [low/dead-code] Pet-mix merge fixture seeds the pre-rename 'pets' key; it rides along as extension data and the mix under test starts EMPTY, so PetMixEquals' element-wise compare is never exercised (`tests/DesktopAICompanion.CoreTests/Program.cs:616`)
- 📌 **F394** [low/dead-code] standdown-ab.ps1 throws on every machine (BASELINE/CHANGED/probe hardcoded to a removed worktree and a session scratchpad, only BASELINE is a parameter) and its step3 regex predates the committed probe, so even a path-fixed run would report 0 ms relocate latency (`tests/fullscreen-standdown-probe/standdown-ab.ps1:15`)
- 📌 **F400** [low/maintainability] mutate-agentflow.py compiles and runs the same BlockedDetector mutant twice (cases at l.251 and l.615) and assigns PANE twice (`tests/mutate-agentflow.py:251`)
- 📌 **F408** [low/dead-code] $ExecutablePath is an unused parameter: none of the four callers (run-gate.ps1, build.yml, two mutation harnesses) passes it, and the 'CI compatibility' comment is stale (`tests/runtime-hardening-selftest.ps1:1`)
- 📌 **F410** [low/maintainability] Nine source files are re-read under different names (StartUp.cs 3x, CompanionsPaneControl.cs 5x, ModulesPaneControl.cs 4x, CompanionCatalog.cs 3x, others 2x), two different comment-strippers coexist, and the ProcessIcon StrictMode forward-reference workaround is a symptom (`tests/runtime-hardening-selftest.ps1:30`)
- 📌 **F432** [low/maintainability] The four magic names are declared three times (PetEmitter.MagicNames, PetGraph.ReservedEntryPointNames, PetStudio AnimCapabilities.MagicNames) under three matching rules, and the build-time drift check reads none of them (`tools/ShimejiConvert.Engine/Emit/PetEmitter.cs:2787`)
- 📌 **F433** [low/dead-code] ResidueItem.Kind is written and never read (`tools/ShimejiConvert.Engine/Emit/ResidueReport.cs:10`)
- 📌 **F443** [low/dead-code] BundleInfo.Author, License, SpriteCount and DefaultAnimation are parsed and never read (`tools/ShimejiConvert.Engine/Shimeji/BundleParser.cs:15`)
- 📌 **F452** [low/maintainability] EngineSelfTest.RunAll has no per-test exception isolation and reports no sub-test count (`tools/ShimejiConvert.Engine/Shimeji/EngineSelfTest.cs:11`)
- 📌 **F455** [low/maintainability] ShimejiConfig.Poses is documented as the compositor's safety net but PosesToComposite never reads it; ShimejiParser parses every <Pose> twice to feed one residue count and two self-tests (`tools/ShimejiConvert.Engine/Shimeji/ShimejiParser.cs:201`)
- 📌 **F456** [low/maintainability] A raw NUL byte in the ShimejiParser.cs key separator makes ripgrep skip the file silently in recursive searches and exempts it from the repo's text=auto EOL normalization (`tools/ShimejiConvert.Engine/Shimeji/ShimejiParser.cs:263`)
- 📌 **F458** [low/maintainability] SpriteSheetBuilder's summary describes the magenta-keyed path as the product behaviour; every production caller composites in alpha mode and only the dev `composite` verb and self-tests key (`tools/ShimejiConvert.Engine/Shimeji/SpriteSheetBuilder.cs:27`)
- 📌 **F461** [low/dead-code] The 'Xml.cs is missing' Error in AssertSpriteFrameLimit is unreachable: ReadAllText in the preceding PropertyGroup throws MSB4184 first (reproduced) (`tools/ShimejiConvert.Engine/ShimejiConvert.Engine.csproj:130`)
- 📌 **F421** [low/maintainability] Nine migration verbs (plus verify) each carry a verbatim copy of the ~25-line enumerate / gate / serialise / re-validate / write skeleton, about 11% of Program.cs (`tools/ShimejiConvert/Program.cs:548`)
- 📌 **F424** [low/dead-code] Program.TotalDwellMs is defined and never called (`tools/ShimejiConvert/Program.cs:1811`)

Info, disposition only:

- 📌 **F015** [info/maintainability] Rule parsing and call decomposition each exist twice inside the join (`docs/agentflow/agentflow_join.py:310`)
- 📌 **F036** [info/dead-code] AgentFlow's self-test keeps a private bare-bool duplicate of TrayConventions.EveryTrayEntryHasAUniqueIcon (used by its self-test only, not dead; the host's ModuleConventionSelfTest already names the row for every module) (`modules/AgentFlow/AgentFlowModule.cs:2461`)
- 📌 **F038** [info/maintainability] Stale numbers and names in comments that the code no longer matches (`modules/AgentFlow/AgentFlowModule.cs:2570`)
- 📌 **F049** [info/maintainability] NotifyBudget's header still says AgentFlow never presses anything (`modules/AgentFlow/NotifyBudget.cs:27`)
- 📌 **F090** [info/dead-code] Write-only ChatCalls on CancellationHonouringBackend, an unused ModuleKit using with a copy-pasted comment, and .NET Framework-era rationale in the doubles (`modules/AiBrain/engine/AiSelfTestDoubles.cs:434`)
- 📌 **F094** [info/maintainability] Stale contract comments: fallback 'wired in a later change', consent 'enforced by endpoint policy', and a doubled summary on IsVisionCapable (`modules/AiBrain/engine/AiSettings.cs:235`)
- 📌 **F112** [info/dead-code] Tautological IsKnownRate check survives inside the loop whose comment explains why it is tautological (`modules/BlinkingLed/BlinkingLedModule.cs:683`)
- 📌 **F117** [info/dead-code] modules/Directory.Build.props LangVersion 7.3 is inert: all eight module projects set their own (seven latest, TestModule 7.3), and every module is 7.3-clean today so no override is load-bearing (`modules/Directory.Build.props:32`)
- 📌 **F141** [info/maintainability] Seven '131-line pool' comments (four present-tense), the probe's description of SmartFortunes.SelfTest, the DescribeEngine cost rationale, the 1.5.0 MinHostVersion note and the `app` telemetry rationale describe code that no longer exists (`modules/Fortunes/engine/SmartFortunes.cs:1197`)
- 📌 **F154** [info/maintainability] The gate asserts PetReport.Describe() prose that the window never shows; the displayed RenderReport text is unasserted (`modules/PetStudio/PetReport.cs:78`)
- 📌 **F170** [info/maintainability] NamesThisModuleWrites summary still describes the pre-1.0.16 loose rule (`modules/Remembrance/CaptureStore.cs:105`)
- 📌 **F172** [info/dead-code] MeetingContext.Location is parsed and read only by the self-test (RemembranceModule.cs:1368); no capture, transcript or summary path consumes it (`modules/Remembrance/MeetingContext.cs:18`)
- 📌 **F196** [info/dead-code] Retained one-time migration bridges (inventory of four) plus one frozen-ABI responder pair that does not belong on a retirement list (`modules/Reminder/ReminderModule.cs:193`)
- 📌 **F223** [info/maintainability] WiX 5.0.2 is hard-coded at six check sites in three scripts beside the lock that already names it (`packaging/WixToolchainPolicy.ps1:359`)
- 📌 **F227** [info/dead-code] Three ABI members have no consumer and no register entry (ScreenWindow.MonitorIndex/ZOrder, ICompanionManager.ValidateXml, IHost.RegisterDropResponder) and PaneAction.InvokeWithPendingAsync has no adopter; CatalogKinds.Pet and InputMonitoring are already recorded (`src/DesktopAICompanion.Contracts/PluginApi.cs:373`)
- 📌 **F228** [info/maintainability] IHost.Log is called from pool threads by three modules (Fortunes, Remembrance, and AiBrain through its static LogSink) while the IHost contract says services are UI-thread-only unless noted; the two host sinks are synchronised but the ABI never says so, and AgentFlow marshals every log line to the UI thread on the strength of the omission (`src/DesktopAICompanion.Contracts/PluginApi.cs:846`)
- 📌 **F232** [info/dead-code] ModuleKit RecordingHost observation hooks (sound, speech bubble, hotkey, catalog payload, context, RaiseDrop, RaiseFullscreenChanged, RaiseSpeechRequest) and FakeModuleSettings.FailSaves have no in-tree reader; register covers them as out-of-tree surface (`src/DesktopAICompanion.ModuleKit/Testing/RecordingHost.cs:46`)
- 📌 **F236** [info/dead-code] Always-true `length > 0` guard in TruncateAtCodePointBoundary, present in both the ModuleKit copy and RuntimeGeometry's (`src/DesktopAICompanion.ModuleKit/UnicodeTextProgress.cs:36`)
- 📌 **F244** [info/maintainability] Five stale 'once a day' comments (one more than filed, in the self-test) and one 'Pets' path comment contradict the code they annotate (`src/dotNet/AppUpdateCheck.cs:131`)
- 📌 **F248** [info/dead-code] ThreeChannelWav is a redundant test fixture: PcmWav with channels=3 yields the identical bytes, and its doc comment cites the wrong helper (`src/dotNet/AudioOutputSelfTest.cs:467`)
- 📌 **F257** [info/maintainability] Window-walk P/Invoke plumbing and filters are triplicated across FullscreenScan, DesktopWindows and FormCompanion.NativeMethods, and IsFullscreenOnMonitor is reimplemented in the stand-down probe (`src/dotNet/DesktopWindows.cs:30`)
- 📌 **F259** [info/dead-code] DiagnosticLog.CurrentPath is unreferenced, and Configure's !WasNamed(...) condition is redundant (`src/dotNet/DiagnosticLog.cs:81`)
- 📌 **F261** [info/dead-code] pictureBox1.Tag is write-only and PictureBox1_Click is an empty handler still wired by the designer (`src/dotNet/FormCompanion.cs:302`)
- 📌 **F275** [info/dead-code] Designer placeholder label1 (100 px, never written) sits in the debug toolbar between Errors and Autoscroll; its text is black-on-black, so it shows as a gap rather than as 'label1' (`src/dotNet/FormDebug.Designer.cs:159`)
- 📌 **F277** [info/dead-code] UpdateRegion disposes the previous Region a second time; Control.Region's setter already disposed it (verified on .NET 10.0.10) (`src/dotNet/FormSpeech.cs:371`)
- 📌 **F280** [info/maintainability] Self-test asserts the watcher survives WM_CLOSE, a property the production callback deliberately does not preserve (`src/dotNet/ProcessIcon.cs:668`)
- 📌 **F281** [info/dead-code] Duplicate P/Invoke of RegisterWindowMessageW inside ProcessIcon (NativeRegisterWindowMessage2) (`src/dotNet/ProcessIcon.cs:841`)
- 📌 **F291** [info/dead-code] ClipCut(24.0, 64) == 24 is asserted twice under two names (`src/dotNet/RuntimeHardeningSelfTest.cs:379`)
- 📌 **F292** [info/maintainability] Runtime limits duplicated as literals instead of the constants production shares (`src/dotNet/RuntimeHardeningSelfTest.cs:453`)
- 📌 **F300** [info/maintainability] Sprite-tile limit WITNESS compares a constant to its own definition, so a change of the shared value can never fail it (`src/dotNet/SecuritySelfTest.cs:463`)
- 📌 **F303** [info/maintainability] HTTP stub comments justify themselves with .NET Framework transport behaviour on a .NET 10 target (`src/dotNet/SecuritySelfTest.cs:1385`)
- 📌 **F306** [info/dead-code] Null-checks on Program.MyData immediately followed by unconditional dereferences; double IsDisposed test in TrackRetiringPet (`src/dotNet/StartUp.cs:205`)
- 📌 **F314** [info/dead-code] TrayPromotion.ResetForTests has no callers (AllowRetry is the used seam) (`src/dotNet/TrayPromotion.cs:167`)
- 📌 **F320** [info/maintainability] Four staging helpers are duplicated across the two Fortune scripts (`src/Fortunes/classify-corpus.py:114`)
- 📌 **F370** [info/maintainability] ProbeBounded comments claim a kernel handle was leaked to the finalizer; ManualResetEventSlim allocates no kernel object unless WaitHandle is read and has no finalizer (measured) (`src/Portable/Wpf/OptionsShell.cs:872`)
- 📌 **F392** [info/maintainability] Probe header lists five of eight modes; the two *-uncovered modes that produced the 781-call figure quoted in FormCompanion.cs and the README are named nowhere (`tests/fullscreen-standdown-probe/Program.cs:12`)
- 📌 **F434** [info/dead-code] ConvertSkin's alpha parameter is never overridden by a product caller; the Magenta emit branch is reached only by EmitterSelfTest (Build(false) + Emit) and the CLI composite dev verb builds a magenta sheet without Emit (`tools/ShimejiConvert.Engine/Engine.cs:94`)
- 📌 **F450** [info/maintainability] Duplicated hub-selection heuristic and redundant lookups in EmitterSelfTest helpers (`tools/ShimejiConvert.Engine/Shimeji/EmitterSelfTest.cs:1397`)
- 📌 **F454** [info/dead-code] ShimejiParser.Local is an unused private helper (`tools/ShimejiConvert.Engine/Shimeji/ShimejiParser.cs:115`)
- 📌 **F420** [info/maintainability] Three XML summaries are attached to the method inserted after them; the build stays clean even with documentation generation on (`tools/ShimejiConvert/Program.cs:493`)
- 📌 **F422** [info/maintainability] CorpusCensus doc misstates the Japanese 固定 type and the census's ability to merge translated names (`tools/ShimejiConvert/Program.cs:1565`)

### Lane fix/scripts (Phase 5): PowerShell, packaging, CI, Python harnesses, test projects

44 items: 40 to fix or disposition, 4 info to disposition.

- 📌 **F009** [medium/correctness] session_ids_by_pid aborts every mode when an agent command line holds a byte cp1252 cannot decode (text=True: reader-thread UnicodeDecodeError leaves stdout None, then an uncaught AttributeError) (`docs/agentflow/agentflow_cpu.py:292`)
- 📌 **F010** [medium/performance] transcript_state re-walks all transcripts and re-parses the whole session file per attributed root per sample, and neither --verify nor the CSV can see the cost (`docs/agentflow/agentflow_cpu.py:465`)
- 📌 **F012** [medium/correctness] --report files unattributed rows ('' pending), sessions quiet for more than 900 s, and no-tool-call (-1) rows into 'clear' (`docs/agentflow/agentflow_cpu.py:817`)
- 📌 **F018** [medium/correctness] classify_call returns would-prompt/would-allow for argument-less non-command tools where the shipped EvaluateCall returns Undecidable: 3.3% of calls, 14% of auto wouldPrompt, 12% of FILTERING 'still fires', and 3 of the 30 recall positives (README's 93% is the Python's; shipped semantics give 83%) (`docs/agentflow/agentflow_join.py:530`)
- 📌 **F215** [medium/correctness] Publish guard checks only modules/<Name> for uncommitted changes, while the freshness watch set also covers ModuleKit and source-linked files, so a dirty external edit can be baked into a committed zip that no re-zip can re-commit (`packaging/New-ModulePublish.ps1:135`)
- 📌 **F216** [medium/correctness] First publish writes the new modules.json entry without minHostVersion, and the strict-mode catalog generator then throws on it after the commit has landed (`packaging/New-ModulePublish.ps1:218`)
- 📌 **F415** [medium/correctness] shimeji-behaviour-soak.py mis-models border eligibility (horizontal=0x06, horizontal+ unmapped to NONE, floor raised as TASKBAR|HORIZONTAL): measured 30-80% error in the play-share table on converted pets since reground, no change to the climb rate (`tests/shimeji-behaviour-soak.py:35`)
- 📌 **F002** [low/security] release.yml pastes vars.SIGN_TIMESTAMP_URL into two single-quoted run bodies instead of routing it through env (no privilege escalation in this repo; the variable is unset today) (`.github/workflows/release.yml:133`)
- 📌 **F007** [low/correctness] classify() presses a truncated 'Yes, allow access to' via the 'yes, allow ' template; the bare exact entry is redundant, not the cause (`docs/agentflow/agentflow_classifier.py:63`)
- 📌 **F011** [low/correctness] sessions/last are never pruned for exited roots, so a fresh agent on a reused pid inherits the dead session's id (and produces one bogus cores row) (`docs/agentflow/agentflow_cpu.py:620`)
- 📌 **F016** [low/security] --tiers prints the first 64 characters of tool_input (command text or a path) on its hit lines, contradicting the file's and the README's never-printed rule (`docs/agentflow/agentflow_join.py:441`)
- 📌 **F017** [low/correctness] WebFetch(domain:...) rules can never match, so every WebFetch call reads as would-prompt (`docs/agentflow/agentflow_join.py:485`)
- 📌 **F019** [low/correctness] --difftest has exited 1 (DEGRADED) since the sibling checkout became ai-acolyte and no gate runs it; with the path corrected, 26,139/26,139 cases agree, so the inert window hid no drift (`docs/agentflow/agentflow_join.py:690`)
- 📌 **F020** [low/correctness] agentflow_join.py labels an unknown toolDenialKind 'not a prompt' in the denial table while tabulating it as a denial; a sixth kind (automode-unavailable, 2 of 80) is now in the corpus and the FIVE comment is stale (`docs/agentflow/agentflow_join.py:862`)
- 📌 **F021** [low/performance] docs harness agentflow-probe.py (and agentflow_cpu.py through it) re-parses every active transcript from byte 0 on each 2 s poll; the shipped module's TranscriptCursor is already incremental (`docs/agentflow/agentflow-probe.py:143`)
- 📌 **F208** [low/performance] Verifier and generator spawn one git cat-file per catalogued asset (~9-10 s per run measured; one --batch process does it in ~1 s) (`packaging/ContentCatalogAssets.ps1:48`)
- 📌 **F209** [low/correctness] Generator accepts the CR-stripped worktree fallback for binary zips without checking Source, and the fallback fires on any git failure, not only 'not committed' (index.lock is not a trigger) (`packaging/ContentCatalogAssets.ps1:85`)
- 📌 **F210** [low/leak] Install-LockedWixToolchain never deletes its PackageRoot scratch (about 6 MB of .nupkg plus NuGet.Config left under %TEMP%, and the Readme recipe then refuses to re-run) (`packaging/Install-LockedWixToolchain.ps1:123`)
- 📌 **F212** [low/correctness] Under Windows PowerShell 5.1, redirected native stderr terminates these scripts before their exit-code branch (reproduced at two sites); build.ps1:156's error path also fails under pwsh (`packaging/Install-LockedWixToolchain.ps1:409`)
- 📌 **F214** [low/correctness] Permission parser swallows any initializer field placed after Permissions (URL or semicolon not required); the invented token is then dropped by RemoteCatalog.TryParsePermissions, under-disclosing the last real flag (`packaging/New-ModulePublish.ps1:111`)
- 📌 **F218** [low/correctness] Reparse-point walk also tests the declared root itself, so a junctioned checkout or output directory is refused as its own root (undocumented and untested either way) (`packaging/StagingPathSafety.ps1:305`)
- 📌 **F220** [low/correctness] Freshness watch set never includes the implicit MSBuild inputs (modules/ and src/ Directory.Build.props, global.json) that change every module's compiled bytes; latent today, no zip is currently stale (`packaging/Test-ModulePublishFreshness.ps1:108`)
- 📌 **F222** [low/performance] Every module zip is fully expanded to %TEMP% once per gate run, fortunes twice (about 150 MB written and deleted, roughly 3 s), to read a handful of sub-megabyte first-party DLL entries (`packaging/Test-ModulePublishFreshness.ps1:630`)
- 📌 **F379** [low/correctness] Template's 'Test it' always reports 'Said it.', including when speech is off or no companion is on screen (`templates/desktop-ai-companion-module/SampleModule.cs:135`)
- 📌 **F381** [low/leak] debug-menu-smoke.ps1 early exits skip cleanup: the l.135 'no pet window' throw leaves the app running (exe lock, MSB3027 on the next build), all three early exits leave the temp data root behind, and an in-process (&) invocation keeps the DATA_ROOT override set; the unguarded Kill sweep is a PowerShell 5.1-only sub-millisecond race (`tests/debug-menu-smoke.ps1:89`)
- 📌 **F386** [low/correctness] The soak's `new Application()` (default OnLastWindowClose) shuts itself down after cycle 1; the comment's rationale is false, and the shipped host shows this window with no WPF Application at all (`tests/DesktopAICompanion.WindowSoak/Program.cs:87`)
- 📌 **F389** [low/correctness] 'Same bounds as runtime-resource-soak.ps1' is false: private bytes are 24 MiB here vs 64 MB there, and the ps1 now judges GDI/USER/handles as per-interval rates while the soak judges last-segment totals (`tests/DesktopAICompanion.WindowSoak/Program.cs:351`)
- 📌 **F390** [low/correctness] ParseCount silently substitutes the defaults for a bad --cycles/--segments value: `--segments 0` runs 2 segments and PASSES while `--segments 1` FAILS (measured live) (`tests/DesktopAICompanion.WindowSoak/Program.cs:390`)
- 📌 **F396** [low/correctness] Invoke-SelfTests.ps1's -LogDirectory also relocates the marker LOOKUP, but the child writes markers to Path.GetTempPath(); any value other than the child's temp path reports all 16 marker-carrying flags as 'wrote no marker file' (latent: no caller passes it) (`tests/Invoke-SelfTests.ps1:74`)
- 📌 **F397** [low/performance] Four --module-selftest flags run twice per gate and per CI run (Invoke-SelfTests rows plus Test-ModuleSelfTests COVERED list), ~12.5 s by the file's own timings; the only check the first runner adds is the SKIP-line regex, which no covered module can trigger today (`tests/Invoke-SelfTests.ps1:120`)
- 📌 **F398** [low/correctness] Invoke-SelfTests.ps1 waits on each self-test with no timeout: a hung flag blocks the local gate indefinitely and CI until the 30-minute job timeout, with no failure line naming the flag (`tests/Invoke-SelfTests.ps1:202`)
- 📌 **F399** [low/correctness] module-window-soak.ps1 passes -ArgumentList unquoted, so a -Module/-Pet path or clone path containing a space is split into separate argv tokens and the harness exits 2 with a cryptic "unknown argument"; -Segments lacks [ValidateRange(2,...)], so -Segments 1 runs a full wasted segment before the exe's own hard FAIL, and -Segments 0 is silently replaced by the exe default (`tests/module-window-soak.ps1:41`)
- 📌 **F401** [low/correctness] mutate-agentflow.py passes TreatWarningsAsErrors=false to every build including the baseline, so its docstring's 'baseline still runs under the real settings' is false (`tests/mutate-agentflow.py:789`)
- 📌 **F402** [low/performance] mutate-diagnostics.py runs build.ps1 -Release (host plus 8 module builds, no packaging) for the baseline and each of 14 wpf cases, and never asserts the exe advanced or clears the stale result file before launching it (`tests/mutate-diagnostics.py:184`)
- 📌 **F404** [low/correctness] mutate-diagnostics.py reads targets with universal newlines and writes newline='' + utf-8-sig, so the CRLF StartUp.cs is rewritten as LF (and any BOM-less target would gain a BOM) by the first STARTUP case and every restore(); invisible to git, harmless to the gate and the compiled output, but not the working tree it captured (`tests/mutate-diagnostics.py:209`)
- 📌 **F406** [low/correctness] mutate-selftest-guards.py's final build_all() sits outside the loop's try/finally, so a TimeoutExpired or Ctrl+C mid-run leaves the last mutant DLL/exe in build/ with source restored (heals on the next build; exposure is a hand-run against build/ before then) (`tests/mutate-selftest-guards.py:323`)
- 📌 **F407** [low/correctness] run-gate.ps1:95 (and build.yml:67) invoke Invoke-SelfTests.ps1 outside try/catch, and the runner neither guards nor asserts its marker delete, so an external lock on a %TEMP% marker (or, on pwsh 7, on a redirect log) aborts the gate with a raw exception and no summary, skipping the ten later sections; invoked ad hoc it would instead grade the stale marker (`tests/run-gate.ps1:95`)
- 📌 **F409** [low/correctness] Remove-LineComments strips from any `//` (URLs and string literals included) across 25 call sites and 17 files, and is applied to the .wxs where XML comments are not stripped; latent today, no current assertion reads an affected line (`tests/runtime-hardening-selftest.ps1:21`)
- 📌 **F411** [low/correctness] The .cs redirect/encoding scan enumerates the whole checkout with Get-ChildItem -Recurse, so a git worktree under the non-hidden .claude/worktrees/ would be judged as part of this branch (exact today: 206 scanned = 206 tracked) (`tests/runtime-hardening-selftest.ps1:93`)
- 📌 **F414** [low/correctness] runtime-resource-soak.ps1: the unguarded Remove-Item in the finally (l.519) replaces a real soak failure, or turns a PASS run into a failed step, whenever the scratch tree cannot be deleted; the WaitForInputIdle half is refuted (a GUI child that dies mid-wait satisfies the wait and l.200-201 already reports the scripted exit code) (`tests/runtime-resource-soak.ps1:196`)

Info, disposition only:

- 📌 **F005** [info/correctness] 'prompt-capable only' sweep selects its tool set from the same positives it scores (`docs/agentflow/agentflow_backtest.py:176`)
- 📌 **F213** [info/correctness] '#requires -Version 7' is a floor: the deflate bytes differ between .NET 8, 9 and 10 (pwsh 7.4, 7.5, 7.6), so two 7+ machines zip differently (`packaging/New-DeterministicPortableZip.ps1:1`)
- 📌 **F393** [info/performance] Walk allocated_bytes window includes the replica's List/ToArray adapter (harness-only) plus an empty HashSet, while the shipped per-scan path builds a populated HashSet per scan; the byte figure was never published (`tests/fullscreen-standdown-probe/Program.cs:248`)
- 📌 **F416** [info/correctness] Test-BacklogClosingCriteria.ps1's grep verbs fold case (Select-String default, documented nowhere); for grep-absent that can hide a satisfied criterion, which the header's 'CANNOT PRODUCE A FALSE STILL OPEN' rules out. No live grep-absent criterion exists today, and grep-present can only yield the allowed false CLOSEABLE. (`tests/Test-BacklogClosingCriteria.ps1:107`)

---

## Open: AgentFlow post-publish audit (2026-09-21)

Twenty findings. **Twelve are fixed and shipped in agentflow 1.3.1 + host 1.2.3**; see the commit
`AgentFlow 1.3.1: fix nine defects the post-publish audit found` for what each one was and 7/7
mutation results. What is left is below. The audit was worth more than the feature it audited:
four defects were in code committed the same day, and two made a shipped version note false.

**The lesson worth keeping, above any individual item.** Two findings were *unreachable code behind
a shipped claim* -- the Notify-mode screen watch and, in a different way, the `sawPanel` report.
Neither a green suite nor a live smoke test found them, because both were reached only in a mode
nobody was exercising. Ask what input reaches a branch, not whether the branch looks right.





- ✅ **FIXED 2026-09-27 (agentflow 1.4.9).** `modules/AgentFlow/FakeCdpServer.cs` is a loopback CDP
  endpoint speaking only what `CdpApprover` asks for: `/json/list`, `/json/version`,
  `Target.attach/detachFromTarget` and `Runtime.evaluate`. Eleven WIRE assertions now drive `Sweep`
  end to end, covering the part that recorded strings cannot reach — target discovery, the
  handshake, and what a whole pass concludes.

  **`sawPanel` is proven, not argued.** Reverting the fix (`{ pressed = note; break; }` back to
  `return note;`) and rebuilding gives *"FAIL: WIRE sawPanel stays TRUE after a successful press"*;
  with the fix, PASS. That was the item's specific ask, and it is a control-flow property AFTER a
  press, so no recorded-string test could have reached it.

  ⚠ Two of my assertions were wrong on the first run, in the dangerous direction: I asserted that
  an unreachable target makes `Sweep` return null — silence. It does not. It returns a note saying
  it cannot see inside the panel AND that this differs from nothing waiting, which is BUG-006's whole
  subject. My version would have passed on a build that went quiet again. Corrected to assert the
  note.

  The fake sends an EVENT before every reply on purpose: the real socket carries target lifecycle
  notifications, and `CdpSession` matches replies by id precisely because a reader taking the next
  message would sometimes read an event. A fake that only replied would let that back in silently.
**Status: published as 1.0.0 and live in the catalog.** `modules/AgentFlow/` is built by `build.ps1`,
`tests/Invoke-SelfTests.ps1` fails if its folder is missing from the build output (`$RequiredModules`,
read by both the gate and CI), and `modules-dist/agentflow.zip` plus `catalog.json` now offer it to
every user.

⚠ **This paragraph used to claim `--module-selftest=agentflow` "runs in both".** It ran in neither:
no `.ps1` and no `.yml` in the repo contained that string, so AgentFlow's 478 assertions executed
only when someone typed the flag by hand. Corrected and made true for the GATE on 2026-09-27 by
`tests/Test-ModuleSelfTests.ps1`, which runs all eight modules (~15s) and is wired into
`run-gate.ps1`. **CI still does not run it** — CI calls the underlying scripts rather than
`run-gate.ps1`, so wiring it there is a separate change, and claiming it before making it is how
this line went wrong the first time. Filed below.

Verified in the real install before publishing, which is a different claim from "the self-test
passes": host 1.1.5 installed over 1.1.4 by MSI, the module folder copied into the install's
`modules\agentflow\`, and the module found three concurrent live sessions in the maintainer's real
transcript store and stood down on all three because none was in `default` mode. 79 assertions pass
when the INSTALLED host loads it through the real loader.

**To see it speak you need a session in `default` permission mode**, blocked longer than the
threshold. In `auto`, `acceptEdits` or `plan` it stands down by design, because measured precision
outside `default` is ~0.4%.

This heading said "research only, nothing built" until 2026-09-17, six commits after the module
landed, while the same section already said "Built, 25/25 self-test" further down. Recorded because
status words in this file are load-bearing and that one contradicted itself -- and then it was wrong
in the other direction for half a day, reading "not published" after the publish.

Read [`docs/agentflow/README.md`](docs/agentflow/README.md) before proposing work — it carries the
measurements, and several obvious designs are ruled out by numbers rather than opinion. Five
runnable harnesses live beside it.

**What remains open, in order:**

**ABI consequence if the input half is ever built, revised 2026-09-16.** The prior-art review found
four actuation channels and **not one of them needs synthetic input**: Terminal Shell Integration
(`terminal.sendText()`), a host-published VS Code command (`executeCommand`), CDP into the renderer
(reaches stock VS Code webview targets, at the cost of a debugging port and a launch-flag rewrite),
and Windows UI Automation (`InvokePattern.Invoke()` on a button found by tree walk, which is not
`SendInput`). None requires foregrounding, focus restore or an idle gate, because none touches the
focused window. So `ModulePermissions` very likely needs no `InputSynthesis` for AgentFlow at all,
and the host-owned foregrounding machinery is not on this module's critical path. The
`InputSynthesis` gap is still real and still additive-safe: it is the same one filed under "Module
SDK follow-ups" further down this file, where Blinking LED P/Invokes `SendInput` while declaring
only `Speech | Storage`. Decide the permission per channel, not once for the module.

**Next step:** the `default`-mode sample is n=85 with 6 real prompts, so 19% is promising rather
than measured. Generate real default-mode data (work an hour out of auto mode) and rerun
`agentflow_join.py`. Two known-incomplete threads: the compound-command splitter is naive and caused
most of the recall failure — the sibling `permission-wildcarding` project already has a correct one
to reuse — and process CPU was never tested as a discriminator despite being free and working on
occluded and minimised windows. On the answering side, ship the notify half behind observe-only
first, since it is the half nobody else has and the only one that needs the detector; then treat
each of the four actuation channels as its own decision, with a death-loop guard in whichever
version first presses something.

**DECIDED (T7): the detector is C# inside this module.** The alternative was shipping it inside the
sibling `permission-wildcarding` project — which already reads both agents' history and owns the rule
matcher — with this module as a thin consumer. That project is node/JS, and an MSI desktop app must
not acquire a node runtime dependency in order to read an append-only JSONL file. What is worth taking
from the sibling is the transcript parsing and the compound-command splitter, ported; not the runtime.

---


## Open: left by the 1.1.6 options-ABI cycle (2026-09-18/19)

The ABI additions, the shared notification sound and the AgentFlow pane rebuild each left something
that was flagged rather than fixed. None of these blocked the work; all of them are things a future
reader would otherwise have to rediscover.

- ✅ **FIXED 2026-09-28.** `Info` and `Header` are the only two kinds whose text the MODULE
  computes rather than the user editing a value, so they are the only two whose rendered content can
  go stale the moment Apply changes what it was derived from. They are on the same side of the test
  now.

  Nothing was wrong at the time -- the only `Header`s that ship are AgentFlow's three static
  explanation paragraphs -- and that is exactly why it was worth fixing before it mattered: stale
  prose looks identical to correct prose, so the first `Header` carrying live state would have been
  wrong silently and indefinitely.
- ✅ **DECLINED 2026-09-28, with the measurement rather than a judgement.** A symlink test would
  add no coverage, and that is checkable rather than arguable: `OptionsWindow`'s containment resolves
  a path with ONE call to `GetFinalPathNameByHandle`, which its own comment describes as resolving
  *"every reparse point along the path in one call"*. A junction and a symbolic link are both reparse
  points, and that call has **no branch on which kind it found** — verified by reading the
  resolver, where the only other reparse references in the tree belong to `CompanionXmlValidator`, a
  different subsystem. So a symlink traverses byte-identical code to the junction already covered.

  The junction is also the case that HAD to be covered, for the reason the test itself records: a
  symlink needs Developer Mode or elevation, a junction needs neither, so the junction is the
  CHEAPER escape. And that test carries its own non-vacuity assertion — *"the junction escape is
  actually set up (so the next check is not vacuous)"* — so it cannot pass by failing to set up.

  What a symlink test would add instead is a check that reports DEGRADED on any account without
  Developer Mode. A control that can run degraded must say so on every run, which this one does; a
  permanently degraded extra covering no new branch is better off the list than carried on it.

### Left open by the three parallel audits (2026-09-19)

Nine findings were fixed in the same cycle (the Audio permission, the second tray row, the poll
re-entrancy guard, `Log` mode, the version policy, the auto-mode greying, `EnabledWhen` trimming,
four resource-lifetime defects and three dead members). These are the ones left.

- ✅ **FIXED 2026-09-28, on the module path only.** `ReadChosen` is a `File.ReadAllBytes` of up
  to 8 MiB and `PlayNotification` then decodes it into mixer format **inside `lock (_sync)`** -- both
  on the caller's thread, and the caller is usually a module's tick, which is the UI timer.

  Caching was already ruled out and the reasoning still holds (`PlayOwned` spells it out: an entry
  would pin an 8 MiB pick plus a mixer-format buffer several times its size and never be reused), so
  the work moved instead. Three deliberate limits on the move:

  | what | why |
  |---|---|
  | every decision layer stays synchronous | the switch, the mute and the device are still read in the order `--audio-selftest` proves |
  | the BUILT-IN chime stays synchronous | ~33k samples synthesized once per process and cached, so it was never the cost; the default configuration behaves identically |
  | the PREVIEW button stays synchronous | its whole job is to report WHICH layer stopped the sound, and the user is waiting for that answer. One click, not one per notice |

  The module path therefore returns `Played` optimistically, which is a real change to what the bool
  means -- "the settings allow it and it has been handed off" rather than "the device took it". That
  is affordable only because `IHost.PlayNotificationSound` already contracts that the module is never
  told WHY a notification did not play, and it would not be affordable for the preview. Single-flight
  via `Interlocked`, because a module notifying in a burst would otherwise stack one 8 MiB read and
  one LOH-sized decode per notice, and a dropped duplicate chime that would have overlapped the one
  already starting is not a loss.

  **NO TIMING IS CLAIMED.** The property is structural -- the UI thread no longer does the read or the
  decode -- and that is what is asserted. All 46 audio assertions still pass.

  MUTATION: two source invariants, each scoped to its own method because `NotificationSound.Play` has
  four call sites and an unscoped grep passes against either change. ⚠ Adding them broke a
  PRE-EXISTING invariant for the wrong reason: it matched raw source with a 120-character window
  between the signature and the call, so a comment EXPLAINING the change pushed the call out of the
  window. It reads comment-stripped source now, which is the same correction another check in that
  file already carries. And `Remove-LineComments` had to be hoisted above its first use, because
  PowerShell only makes a function callable below its definition -- the same ordering trap that bit an
  invariant earlier in this campaign. SMOKETEST.md 159 -> 162.
## Open: findings from the v1.1.0 wrap-up audit (filed 2026-09-10)

Four parallel read-only audits ran over the tree at the v1.1.0 tag (credentials, PII/employer material,
stale files, readme accuracy). Credentials came back with **zero** findings and the working tree was
clean. One residual remains open; the rest are closed and in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md).

### ✅ FIXED 2026-09-28 — the net exists, and building it found the scope was the hard part

  `Test-ModulePublishFreshness.ps1` now scans every DLL in every published zip for a
  `<drive>:\...\.pdb` string. A path reaches a DLL through the CodeView entry of the debug directory,
  stored as a plain NUL-terminated string in the image, so a byte scan finds it with no PE parser.
  Latin-1 decoding, so every byte maps to exactly one char and nothing slips through a multi-byte gap.

  ⚠ **The first run failed, and every offender was somebody else's.** Seven embedded paths, all in
  prebuilt third-party packages: `Microsoft.Windows.SDK.NET` and `WinRT.Runtime` from Microsoft's
  agents (`C:\__w\1\s`, `D:\a\_work\1\s`) and `onnxruntime` plus `Microsoft.ML.OnnxRuntime` from
  ONNX's (`N:\_work\1`). We cannot set `DebugType` on a NuGet binary, those paths leak Microsoft's
  and ONNX's build layout rather than this user's machine, and a check nobody can ever make pass is
  its own defect.

  So it is scoped to assemblies THIS REPO builds, derived from the repo's own `.csproj` files rather
  than a hardcoded list -- which keeps the property the entry wanted: a NEW project of ours arriving
  without the setting is caught automatically, because its csproj is what puts it in scope. It also
  **refuses a silent pass**: examining zero DLLs throws rather than reporting success, because a
  scope that stops matching what is shipped would otherwise look exactly like a clean result.

  MUTATION, four axes, with a real offender rather than a weakened pattern:

  | axis | expected | result |
  |---|---|---|
  | a zip with a DLL named like OURS carrying a build path | fail | failed, and named the path |
  | the same path in a THIRD-PARTY-named DLL | pass | passed, correctly out of scope |
  | the scope set matched nothing | fail | failed with the "examined ZERO DLLs" refusal |
  | baseline / restored | pass | passed; 14 of our own DLLs across 7 zips, zero paths |
  CLOSES-WHEN: grep-present packaging/Test-ModulePublishFreshness.ps1 "CodeView"
## Open: follow-ups from the Disposition audition (aibrain 2026-09-11)

The feature shipped — "Show me 5 examples" and "5 about my screen" beside the Disposition dropdown,
verified end to end against a live `gemma3:4b` by driving the real pane through UI Automation. That
record, and how the five predicted constraints were answered, is in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). Two threads it left open:

### Nice-to-have, unchanged

Sample the whole catalog rather than one entry, so the dropdown can be chosen by reading voices side by
side. Bigger UI than a `PaneAction`, and it should not gate the simple version that now exists.

---

## Open: threads left by the .NET 10 + plugin re-architecture

The re-architecture itself is finished — the .NET 10 migration, streams S1 to S7, the in-app Modules
catalog, and the Reminder, Remembrance and Blinking LED modules. That record, with the version
numbers as they stood at the time, is in
[`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). These are the threads it left open.

### Moving a companion still needs new ABI

Reminder 1.7.0 made the companion physically react when a reminder fires with **no host change at all**,
because `IHost.TryPlayAnimation` and `IHost.PlayAnimationAll` already existed. What does not exist:

- **Still genuinely missing (deferred by decision, 2026-08-27):** MOVING a companion ("walk to centre screen").
  That does need new ABI, and it is bigger than it sounds: companion position is driven by animation velocity
  expressions rather than set directly, so a "move to point" verb would fight the engine rather than sit
  beside it. Not attempted.

### Remembrance follow-ups

Diarization (speaker labels) is deliberately deferred to a follow-up.
**Follow-up ideas:** refresh the device dropdowns without an app restart (the ABI builds the schema once at
load, so this one genuinely does need a host change).

The live recording smoke test this module still needs is in
[`docs/BLOCKED.md`](docs/BLOCKED.md): it requires a machine's local console, and a Remote Desktop
session presents no mic or speakers.

### Sound: duck a companion's SFX while a speech bubble is up

Filed under the TTS/voice module, which was dropped on 2026-08-13. That entry proposed ducking a
companion's SFX automatically — the "automatic SFX-ducking above" the bullet below refers to — and
the module going away does not close this, because the base owns `AudioOutput`, so the mute can hook
there.

  - **UX (user request 2026-08-25):** add a user-facing "silence companion sounds" checkbox under Audio, so a companion's
    embedded `<sound>` SFX (e.g. a companion "yelling") don't fire while a speech bubble is up waiting to be read.
    This is a manual toggle alongside the automatic SFX-ducking above — some users simply want the companion quiet
    when it "talks". Wire it when the TTS/voice module lands (the base already owns `AudioOutput`, so the mute
    can hook there). Now relevant because converted shimeji can carry real `<sound>` SFX as of v1.8.0.
    **2026-08-26:** the manual half shipped as the global **companion sounds** toggle in Preferences → Sound; the
    automatic duck-while-a-bubble-is-up idea is the part that remains open.

- ✅ **DECLINED 2026-09-28 by owner decision, and the reason is that something better already
  exists.** Not deferred to a future TTS module, which is what I first suggested: declined.

  The groundwork did ship with the v1.6.0 module-audio ABI (per-owner input tracking), and its entry
  in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) correctly said this "changes how the
  app sounds, so it wants its own decision and a setting". This is that decision.

  **What would have ducked, measured rather than assumed.** Only two modules make any sound at all:

  | module | path | what it plays |
  |---|---|---|
  | AgentFlow | `IHost.PlayNotificationSound` | the shared chime chosen in Preferences |
  | Reminder | `IHost.PlaySound` | its own embedded MP3, since a module can only hand the host encoded bytes |
  | Remembrance | — | **nothing.** It records audio and never plays any |

  **What would have BEEN ducked is the pet, and most pets are silent.** Counted across the corpus:
  the 7 eSheep colour variants carry 35 sound clips each and six others carry 1-5, but **all 32
  converted shimeji carry zero**, `esheep64` carries zero, and the BUILT-IN DEFAULT pet
  (`src/Resources/animations.xml`) carries zero. So on a fresh install, and for anyone running a
  shimeji, the setting would have done nothing at all.

  **And the real complaint already has a better control.** The objection was that pet audio is
  annoying. Each companion card in the Companions pane already carries a "sound on / sound off"
  toggle (`src/Portable/Wpf/CompanionsPaneControl.cs`, tooltip *"click to mute / unmute this pet's
  sounds"*), persisted per pet type and applied at play time with no restart. A permanent mute
  strictly dominates a 0.75-second duck for that complaint, and it ships today.

  ⚠ **Two things I got wrong on the way, recorded because the reasoning is the useful part.**
  First I recommended deferring it to IDEAS alongside the dropped TTS module; the owner's reframing
  -- duck under a NOTIFICATION rather than under speech -- was better, because it needs no new module
  and it dissolved my objection that ducking would make the Preferences preview disagree with the
  volume slider (it would not: the thing lowered is the PET, so the chime still plays at exactly the
  slider level). Second, I had to correct the premise that Remembrance chimes: it makes no sound.

  **The design, recorded so nobody re-derives it if a voice module ever lands.** A flag on the
  internal `AddInput`, with the caller declaring whether its sound takes priority over the pet --
  the same shape as `absenceIsNormal` on the border lookup and `deferCustomRead` on the notification.
  `PlayNotification` and `PlayOwned` duck; engine SFX and `PlayTestTone` do not. The Preferences
  preview goes through `PlayNotification`, which matters: without that, "Test sound" would not duck
  and the checkbox would be unverifiable from the UI. Module audio should NOT duck other module
  audio -- deciding which of two simultaneous chimes wins has no obvious right answer.

  The mixer groundwork stays useful regardless: every live input is already owner-tagged and already
  wrapped in its own `VolumeSampleProvider`, and there is already a ~10 ms ramp helper, so the
  implementation remains small the day there is a voice to duck under.

---

## Post-v1 backlog (added 2026-07-29)

### Open, found 2026-09-01 while chasing companion behaviour


- ✅ **PARTLY COVERED 2026-09-27, and the rest DECLINED with the reason.** Two source invariants
  now assert the properties that actually regress, by ORDER: the Run button is wired to `Run()`, and
  a chain that will not build RETURNS before `_runDebugPet` is reached. Mutation-tested —
  disconnecting the button and moving the spawn above the guard each exit 1 naming the assertion;
  the unmutated file exits 0.

  ⚠ **This is not the click-through test the entry asked for, and does not pretend to be.** It
  catches the button being disconnected and the failure path spawning anyway. It cannot catch
  anything that only shows up on screen.

  Driving the real button was declined rather than attempted a fourth time. The three obstacles this
  entry recorded are still true — no way to drive the tray from a test, previews auto-hide under a
  fullscreen foreground window, and an isolated `DESKTOP_AI_COMPANION_DATA_ROOT` falling back to
  eSheep — and a WPF pane needs an STA thread and a Dispatcher inside the module self-test, which is
  the flakiness those three attempts already hit. A deterministic check of the two real failure modes
  is worth more than a fourth flaky one.
- ✅ **CLOSED 2026-09-27. Three of its four parts were already done or decided; the fourth is a
  feature and has moved to `docs/IDEAS.md`.**

  - **23a — DONE, and it was the live bug in this entry.** `FormCompanion.cs:1455` calls
    `AdoptScreenUnderPet()` after a successful `FollowWindow()`, so a pet riding a window to another
    monitor no longer keeps resolving its physics against the old screen's `workArea`. Fixed
    2026-09-22 in `27ab8e8` ("Four backlog fixes: the monitor desync, a false label, two silent
    give-ups"); the entry was never updated. Verified by reading the code, not the commit message.
  - **23b — nothing to do**, per the owner decision of 2026-09-22: a drag is an explicit user
    action and fullscreen stand-down is a get-out-of-the-way safety behaviour, so neither should be
    gated by the setting.
  - **23c — DONE**, same commit. `OptionsShell.cs:377` now reads "Let companions spawn on any
    screen", and the false parenthetical "(they stay on the one they appear on)" is gone, with the
    reason recorded beside it.
  - **23d — MOVED to `docs/IDEAS.md`** ("A companion should be able to WALK between monitors").
    Real traversal is wanted and scoped, but nothing is broken without it, which is the line this
    repo draws between the backlog and IDEAS. The two refuted blockers moved with it so they are not
    re-raised.
- ✅ **MEASURED AND PART-FIXED 2026-09-28. The emitter half is fixed; the corpus and the
  hand-authored half are recorded decisions, not work.**

  ⚠ **Two of this entry's own questions were already answered and the entry was stale.** The
  warning has named its subject since 2026-09-22 (pet id, kind, state id, `where`, candidate count),
  so "the log line carries neither" no longer held, and the consequence was already written at the
  site: the caller treats -1 as a reason to set `bLeavingScreen`, the pet walks off the screen and
  respawns, and `Play()` re-rolls the monitor under multiscreen. So the remaining unknown was WHICH
  pets and states can reach it.

  **Measured statically and exhaustively over all 54 shipped companions**, using the real parser
  (`Xml.TryReadXml` + `LoadAnimations`) and the real predicate (`TNextAnimation.Eligible`), against
  the `where` values `FormCompanion` actually raises with each call site's `absenceIsNormal` carried
  through. Re-implementing either would have measured a copy of the rule; enumerating `where` values
  the host never raises would have manufactured findings.

  | | cases | distinct pets |
  |---|---|---|
  | total (state, situation) pairs with zero eligible weight | 2497 | 24 of 54 |
  | of those, at WARNING level (caller has no defined fallback) | **1800** | 24 |
  | hand-authored | 1713 | 10 |
  | **converter output (ours)** | **87** | **14 of 32** |

  1701 of the 1713 hand-authored cases are the seven eSheep colour variants at 243 each, which is
  ONE authoring pattern repeated seven times rather than seven problems.

  **FIXED: the 87 that are ours.** All of them share one shape, and the cause was a deliberate and
  correct trade-off left half-finished. `PetEmitter` withholds the `only="none"` turn from a
  non-locomotion jump on purpose, because that edge is eligible at the TASKBAR and a turn there is
  the "every landing was a facing flip into the hub's idle dwell" outcome the taskbar edges were
  written to replace (30 of 31 landings on Hornet). That left such a state with `taskbar` and
  `window-bottom` as its ONLY border edges, so at a screen side, the screen top or a window top
  nothing was eligible. The situations split exactly 29 / 29 / 29 across `VERTICAL`, `HORIZONTAL` and
  `WINDOW|WINDOW_TOP`, over 29 states named Jumping (39 cases), PullUpShimeji2 (21), Launching (9),
  Lay an Egg2, Hypnosis!2 and three others.

  Those three situations are now named explicitly and routed to `fall`. Named rather than `none`, so
  no taskbar-eligible edge is added and the measured landing behaviour is untouched; `fall` rather
  than `turn`, because a turn flips facing and returns to the hub, which is idle behaviour for a pet
  still in the air, while `fall` is the emitter's descend-and-land state and already carries the
  window-top edge a descent needs.

  MUTATION, two legs, and the second is the one that matters:

  | leg | result |
  |---|---|
  | drop the three edges | all three new assertions fire, and the pre-existing no-`none`-turn assertion stays quiet (no cross-talk) |
  | "fix" it with `only="none"` instead | still fails, because the assertions require a NAMED match — the lazy fix would satisfy "has an edge for that border" while breaking the landing behaviour |

  ✅ **THE 14 SHIPPED PETS ARE FIXED TOO, and my first disposition of them was wrong twice over.**

  I wrote that they would "correct themselves the next time they are converted for another reason"
  and cited a standing rule against re-converting the corpus. Both halves were wrong:

  | what I said | what is true |
  |---|---|
  | "this repo's rule is not to re-convert for one cause" | `docs/DESIGN-REGISTER.md` forbids re-converting to chase the SPRITE-TILE entry, whose closing condition was unsatisfiable by construction. There was nothing to fix there. I generalised one instruction about a non-defect into a rule, and the only other citation I had was my own sentence from the same day |
  | "next time they are converted" | has no trigger. The source skins are NOT in this repo -- only `base-conf/actions.xml` and `behaviors.xml` are -- so nothing was ever going to re-convert `shimeji-cyn` |

  And the mechanism for exactly this was already here: the format ladder, whose purpose is upgrading
  shipped converted pets in place. The precedent is exact -- the emitter's own comment records that
  the `rejump` migration already attaches border edges to shipped pets, *"so the migration and the
  emitter disagreed, with the migration right."*

  **A ninth rung, `reground`:** `1.0 -> reloop -> 1.1 -> reground -> 1.2`. `reloop`'s rung was
  RETARGETED from the current version to the newly named 1.1; left as it was it would have carried a
  pet from 1.0 straight past the new rung, which is the skip-everything-after-me defect
  `EmitterSelfTest`'s ladder walk exists to catch.

  The verb selects on the MEASURED SHAPE -- a border whose every edge is `taskbar` or
  `window-bottom` -- so a state that already has an answer is never touched, and it adds only
  situations not already covered, so a re-run is idempotent.

  **Real corpus: 54 pets, 32 changed, 29 states grounded, 87 edges added, 0 failures.** 87 is exactly
  the measured case count. PROVEN with the host's own `TNextAnimation.Eligible` before and after,
  rather than with the migration's own report, which only says it did something:

  | corpus | total cases | WARNING-level | of which CONVERTED |
  |---|---|---|---|
  | original | 2497 | 1800 | **87** |
  | migrated | 2410 | 1713 | **0** |

  `1800 - 87 = 1713` and `2497 - 87 = 2410`, so nothing else moved: the hand-authored cases are
  untouched, which is the author gate working. Checked on a copy first: versions `1.1=32 -> 1.2=32`,
  BOM-bearing files 19 before and 19 after, and the app's validator reports 0 invalid, 0 round-trip
  failures and 0 converted pets with unreachable animations.

  ⚠ **`build.ps1 -Release` does NOT build the standalone ShimejiConvert CLI.** The first
  "SELFTEST PASS" I accepted after adding the rung came from a stale binary whose own usage text did
  not mention the new verb, so the ladder assertions had not run at all. Rebuilt explicitly, confirmed
  the timestamp advanced and the usage listed `reground`, and re-ran. The artefact-freshness rule
  caught me with the rule written down.

  ⚠ **DECISION NOT TAKEN, and it is the owner's: the 1713 hand-authored cases.** The consequence
  there is the same walk-off-and-respawn, measured elsewhere in this file at about 21 times an hour
  at the taskbar. Whether that is WANTED is not something I established: a sheep wandering off screen
  and coming back may well be the app's signature behaviour rather than a defect, and nothing in the
  code says which. Two options, both with a cost: strengthen `CompanionXmlValidator` to require
  positive eligible weight per `where` bucket, which would reject currently-accepted third-party
  pets; or add a runtime fallback that ignores eligibility when nothing matches, which would change
  behaviour for every pet including the eSheep family. Neither should happen without a decision.
### Shimeji conversion: the open remainder

Phases 0 and A to E all shipped in 2026-08, and every original estimate is kept beside its correction
in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md) — read that before re-scoring anything
here, because four of the five estimates were wrong in a way that is informative.
`ChaseMouse` / `ChaseMouse2` was the only item on that list never built and is now in
[`docs/BLOCKED.md`](docs/BLOCKED.md), because what it needs first is a judgement call rather than
code. Two decisions that used to sit here are in
[`docs/DESIGN-REGISTER.md`](docs/DESIGN-REGISTER.md) instead, because neither asks for work:
"moves the user's windows" (48 actions) is refused deliberately, and a blank frame is legitimate so
"no blank tiles" cannot be a corpus-wide gate. What remains open:

### Feature ideas (queued, not yet scoped)

**Moved to [`docs/IDEAS.md`](docs/IDEAS.md) on 2026-09-17, numbers intact** (16 per-companion
speech personality, 17 the Remembrance listenable-output gap, 18 the tray-utility ports): they are
product ideas nobody has scoped, and this file holds work that can be picked up. Ideas 1 to 15 stay
closed in [`docs/HISTORY-post-1.0.0.md`](docs/HISTORY-post-1.0.0.md). One piece of idea 18 did NOT
move, because it is engineering debt rather than an idea: the `ModulePermissions` under-disclosure
in a shipped module, which is now filed under "Module SDK follow-ups" above.

---

## Open: converter fidelity across the shimeji corpus (filed 2026-09-24, scoped 2026-09-25)

⚠ **Filed first as a Zim-only item; a corpus census on 2026-09-25 showed it is not one.** The
residue report is written beside each converted `animations.xml` and was never kept, so nothing
here recorded what any of the 31 converted companions lost. Re-derived by re-running the real
converter against every original source bundle; the reports are kept at
`D:\.ai-work\shimeji-catalog\work\census\` (`census.md`, `census.json`, `residue\*.txt`).

**30 of 31 censused** (Gengar failed on a multi-skin archive, a harness limit, not a defect):

| source | companions | dropped | degraded | not attempted |
|---|---|---|---|---|
| shimeji.org Android bundles | 18 | 0 | 0 | 0 |
| desktop Shimeji-EE skins | 12 | 82 | 365 | 34 |

The 18 zeros are genuine and were checked as a distinct claim, because a clean sweep of zeros is
exactly what a broken harness produces — the first run of this census did precisely that by
invoking `convertroot`, which writes the XML but no residue file. Those bundles report **0
state-dependent conditions** against 14–23 for the desktop skins: shimeji.org flattens them
upstream, so there is nothing conditional left to lose. Every desktop-sourced companion lost
actions, 6–16 dropped and 26–34 degraded each.

**447 lost actions, but four root causes, and one dominates:**

| cluster | actions | note |
|---|---|---|
| window-relative navigation (`activeIE`) | 353 | 79% of the total; needs window geometry the runtime does not expose |
| cursor-position branching | 54 | needs `cursorX`/`selfX` conditions the format lacks |
| breed autonomous sibling | 24 | genuine format limit, `<child>` auto-closes |
| interact / transform / anchor | 16 | mostly two-shimeji `Interact` and `ScanMove` |

**Where this stands as of 2026-09-24.** The converter-gap cluster that used to head this section is
done, and the "not attempted" column with it. It said
`Jumping` was unattempted in 11 of the 12 desktop skins and `Resisting` in 11. Measured 2026-09-24
from the residue reports of a fresh conversion: across the 13 desktop pets, 1592 source actions
account as 538 emitted, 89 dropped, 374 degraded and **0 not attempted**. The column was 34 before
83d5eaf admitted the frame-playing embedded classes, 5 after it, and 0 once set-pieces converted as
chains. Nothing in this table is a converter gap any more; what remains genuinely needs host or
format work.

⚠ A hypothesis that did NOT survive, recorded so nobody re-runs it: the 18 Android companions
were NOT built from the poorer source while a richer desktop zip sat in the catalog. Only 2 of 18
have a findable desktop counterpart and neither is better (45 vs 41, 36 vs 37, both on shared
template configs). Weak evidence rather than proof — about half the 1786 desktop bundles ship
under a generic `img/Shimeji/` folder and carry no character name to match on.

### The Zim conversion specifically


✅ **DECLINED 2026-09-28 by owner decision: the fidelity work is not being tackled.**

Zim converted and was accepted on the first run: 26 animations at the time, valid, round-trips, 0
unreachable, verified in the real app via `localxml=` rather than by the validator alone. The
converter reported **6 actions dropped and 33 degraded**, and those 39 were the proposed work.

**Why declining is reasonable rather than reluctant:** 31 of the 39 sit in one cluster that needs
host-side window geometry the pet format does not expose, and 2 more are a genuine format limit
(`<child>` auto-closes, so a self-breeding sibling cannot be expressed). So roughly 85% of the work
is blocked on capabilities that do not exist, not on converter effort. The remaining 6 are
cursor-position branching, which needs condition support the format lacks.

The cluster analysis is kept below because it is the part worth having if this is ever revisited;
the 39 individual residue lines are not, and they live in the handoff file named further down.

**They are four problems, not thirty-nine**, which is the part worth carrying into the estimate.
Parsed from the residue file rather than tallied by hand:

| cluster | count | tractability |
|---|---|---|
| window-relative navigation (`activeIE.*`) | 31 (27 degraded + 4 dropped) | needs host-side window geometry the format does not expose |
| cursor-position branching (`cursorX`/`selfX`) | 6, all degraded | needs condition support the format lacks; one is a near-miss |
| breeding an autonomous sibling (`Breed`) | 2, both dropped | genuine format limit — `<child>` auto-closes; treat as out of scope |
| the converter's own unattempted mappings | 0 — superseded | the 2026-09-25 census measures the not-attempted column at **zero** across all 13 desktop pets; the four names below are what the OLD residue report said, kept only so the earlier figure is traceable |

⚠ Before writing "needs a host change" for cluster 1, grep `PluginApi.cs` for the verb, per the
convention above. That exact sentence already cost one planning cycle in this repo.

The full per-action lists, the reproduce commands, and the two fidelity caveats that are *not* in
the 39 (the converter's fixed ~48px jump height, and 60 actions using script-computed values) are in
`D:\.ai-work\shimeji-catalog\work\zim\HANDOFF.md`, beside the converted XML and its residue
report. Kept out of this repo on purpose: see below.

⚠ **The status line above was stale and is corrected here.** It said "Nothing about this is in
the repo yet". Zim ships: `Companions/shimeji-zim` is present, is in `catalog.json`, and now carries
**45 animations** against the 26 this entry recorded, so it has been improved since the entry was
written. The publishing question the entry raised is a maintainer decision that has since been taken
one way or the other; it is not re-raised here, and nothing in this repo's gates speaks to
redistribution rights either way.

Kept for traceability: the entry's original note was that the asset is a third party's sprite art of
a character its owners hold, sourced from shimeji.org, which records no author for it — so the
source-specific evidence `Companions/README.md` asks for could not be assembled from that source.

### 📌 Every converted climb replays its mount pose on every cycle (filed 2026-09-28)

The emitter writes `repeatfrom="0"` on every sequence it produces, and
`PetEmitter.cs:957` records that as an invariant: *"the emitter always writes `repeatfrom="0"`, so
the span is the whole frame list"*. For a climb whose source frames begin with a turn-onto-the-wall
prefix, that makes the repeat restart at the prefix, so the pet snaps back to its pre-climb pose
every time the sequence loops.

Observed on `shimeji-brq51bkr`, whose `climb` is 26 frames: frames 36-39 are a front-facing turn on
the floor and 40-61 the side-on cling, so the loop ran 61 → 36 and the pet flipped to standing
front-on **every 2.6s** of climbing. Fixed for that pet in place by hand
(`repeatfrom="4"`, and `descend` reordered because the reversed frame list put its prefix at the
tail where `repeatfrom` cannot reach).

**Measured across the shipped corpus: 48 of 48 climb-family sequences in 18 converted pets are
`repeatfrom="0"`, and none is non-zero.** The hand-authored pets are not affected — they have no
climb-family sequences with a prefix — and the bundled reference pet uses non-zero values
(`repeatfrom` 1, 5 and 9 in `src/Resources/animations.xml`), so the idiom is the format's, not an
invention.

⚠ **Not a one-line change, which is why it is filed rather than done.** Three things are coupled:

- `reclimb` sets `a.Sequence.RepeatFromFrame = 0` for every surface pose
  (`tools/ShimejiConvert/Program.cs:1003`) while lengthening the repeat for reach, so the rung that
  fixed reach is also what flattens the prefix. A fix must keep its reach guarantee.
- `PetEmitter.JumpStepCount` and `SurfaceRepeatForReach` both solve their arc and reach arithmetic
  against the always-zero assumption. Changing the emitted value without updating them makes a jump
  reach the wrong height **silently** — the comment at `:958-959` says exactly that.
- Identifying the prefix needs the SOURCE skin's separate action blocks. `reclimb` is deliberately
  "numbers only, so no source skins", and nothing in the emitted XML marks where the prefix ends.
  A migration rung therefore cannot do this the way `reground` and `reclimb` did.

CLOSES-WHEN: grep-present tools/ShimejiConvert.Engine/Shimeji/EmitterSelfTest.cs "mount prefix sets repeatfrom"

That needle is the label the emitter self-test must assert under: a synthetic skin whose climb has a
distinct mount prefix emits a `repeatfrom` equal to that prefix length, and the arc and reach solvers
are fed the same value that is emitted.

---

## Open: rightsize the "Jesus Our Lord" companion (filed 2026-09-24)

✅ **FIXED 2026-09-28. The intent was "make the frames one consistent size", and it was two
defects at once.** `Companions/shimeji-brq51bkr` (`name` "Jesus Our Lord", author `shimeji.org`,
source <https://shimeji.org/u/brq51bkr>).

**The measurement needed a pose-invariant proxy, and the obvious ones are wrong.** bbox HEIGHT says
nothing here: the frames are already uniform at 256, and a sitting pose is legitimately shorter than
a standing one anyway. Head width works for upright poses and reads 2px on a sprawled `fall`. Face
area is the one that holds -- drawn in every frame, scales with the square of the character's scale,
indifferent to orientation. On that measure the character spanned **2.86x**:

| animation | face-scale | | |
|---|---|---|---|
| `stand` `turn` `kill` `sync` | 81.5 | 1.36x | a zoomed close-up, clipped at the top of the hair |
| `walk` `climb` `descend` | 60 | 1.00x | 72 frame-uses, the dominant style |
| `idle` | 50.5 | 0.84x | and 47..61 INTERNALLY |
| `drag` `bounce` `fling` `fall` | 28.7..43.5 | | |

⚠ **Rescaling alone could not fix it, which is worth recording because it looks like it should.**
Frame 0's head-to-body ratio is far larger than `walk`'s, so matching faces leaves the silhouettes
mismatched (stand 130x159 against walk 162x213) and matching heights leaves the faces mismatched.
Different PROPORTIONS, not just different scale. Upscaling everything was impossible regardless: the
cell is a hard 256, `idle` already filled it, and one `fling` frame capped a uniform target at half
the pet's current size.

So the odd-proportioned art was taken off the paths the eye tracks, then what remained was rescaled:

| change | |
|---|---|
| `stand`, `turn` | frame 0 -> idle frame 19, a neutral arms-down pose already in `walk`'s proportions |
| `idle` | dropped frames 18 and 23, the two big-head frames inside its own cycle |
| `idle` 19..25 | rescaled x1.20-1.29 up to `walk`'s scale |
| `kill`, `sync` | frame 0 rescaled x0.745 down |
| `drag` | rescaled x1.396 |

⚠ **A SECOND DEFECT surfaced while doing it.** `idle`'s bbox was `(64, 35, 197, 232)` -- a 24px
gap BELOW the character -- while `walk` reaches y=256. The host stands a pet by putting the cell's
bottom edge on the floor, so the pet **hovered 24px** whenever it stopped walking, on top of
shrinking. Every rescaled frame is bottom-aligned now; 17-25px of float removed per frame.

**Result: 10 of 13 animations at 1.00x.** NOT fixed, with the number that says why: `bounce` 0.58x,
`fling` 0.48x, `fall` 0.47x. All three need upscaling and all three overflow the cell because their
bboxes are inflated by motion lines drawn around a small character -- `bounce` frame 11 would be
224x303, `fall` 406x508. They are brief ballistic states where a size shift reads as motion.

### ⚠ The measure above was WRONG, and the owner said so on sight (2026-09-28)

**Face area was the wrong proxy, and this entry argued for it at length.** It is scale-correct and
the owner does not judge by it: they judge by SILHOUETTE. The two disagree most on exactly the frame
this entry rescaled hardest -- frame 0 (`kill`/`sync`) came out at face 1.00 against `walk` with a
body 25% SHORTER, so it read as too small, and `stand` read as too large. Both of this entry's
rescales were in the wrong direction for the thing being judged.

**The measure is now scored, not argued.** Six judgements are on record; matching `walk`'s character
HEIGHT predicts five of them, ink MASS four, face area fewer. Applied 2026-09-28: grounded poses
(the hub frame, `stand`, `idle`) match `walk`'s character height of 215px with the BOTTOM anchored,
and all seven land at 1.00. Airborne poses (`bounce`, `fall`, `fling`) match frame 11's ink mass with
the CENTRE anchored, because a tucked figure's bbox height is pose noise -- height-matching
`bounce` 15 wants x1.87 and leaves it visibly larger than its neighbours.

**The cell-overflow residual above is resolved, and the reasoning that produced it was the error.**
The three "overflowing" frames hold a SMALL character inside a large bbox: `bounce` 15 and `fall` 62
are a 116x115 figure inside 192x240, with only ~240 opaque pixels in the 125px of surrounding
height. That surround is decorative motion marks. Clipping a radial speed line at the cell edge is
visually free, and it is the only thing that makes the real character size reachable: those two went
up x1.54, `fling` 64 x1.49, `bounce` 16 x1.17. Measuring the bbox instead of the figure is what made
this look impossible.

⚠ **Two further errors, both caught by rendering the result rather than by a metric.** Centre-
anchoring pushed the legs of frames 15, 16 and 62 off the bottom of the cell -- 15 achieved 170px
where 178 was planned, so 8px of body was gone while every size number still looked plausible.
Placement is clamped to keep the figure inside the cell now (costing a 6-8px upward shift on five
airborne frames) and achieved size is asserted against planned. Separately, premultiplying alpha in
8 bits destroys low-alpha colour -- alpha 2, red 200 premultiplies to 1 and returns as 127 -- which
darkened every soft edge, measured as the alpha 1..40 band's mean luma falling 53 → 38 on frame 19.
The resample runs on float32 premultiplied channels now and the same measure rises 53 → 60.

⚠ **One measurement trap worth carrying forward.** "Largest connected opaque blob" is not the
character: anti-aliased outlines leave sub-threshold seams that cut this art into 20+ pieces, and the
blob measure reported `stand` frame 19 as 171x164 with **16,764 opaque pixels lying outside what it
measured**. Seams must be bridged before labelling, and the bridging radius must SCALE with any
resize -- a 6px seam is 9px after a x1.5 upscale, and a fixed radius silently returns a limb. That
artifact read as a 10px cut and tripped the cut guard on a frame that was never cut.

⚠ **Three errors of mine that the guards caught**, each of which would have shipped damage:
scaling about the cell's bottom edge assumed the character sat on it, and the pixel-loss guard
refused at 15.9% because of that very floor gap; `paste(im, box, im)` blends against a transparent
canvas and SQUARES the alpha, so anti-aliased outline pixels at alpha 1-2 rounded to 0; and the first
pass normalised `bounce` frame 17 while refusing 11-16, which would have left `bounce` running small
x6 then BIG -- a new pulse inside one animation. The rule is whole-animation-or-nothing now, decided
before any pixel is written.

No pixel data was invented: every change is a resample of existing art or a frame-reference swap. BOM
and LF endings preserved, validator reports valid / round-trips / 0 unreachable, `catalog.json`
rehashed.

The geometry note this entry carried is superseded and kept only for traceability:

⚠ **Recorded as requested, with the intent NOT specified** — ask before acting rather than guessing.
It could mean on-screen scale, the sprite-sheet cell size, the 4.7 MB file, or the tray icon.

The geometry is already known, so do not go re-derive it. `tilesx` 9 and `tilesy` 8 are confirmed
read out of the file today. The downscale-bleed entry above (search `tile 88`) records the sheet as
2304x2048, which puts cells at exactly **256px — the `MaximumSpriteFrameDimension` cap**, and notes
that `ScalePolicy.FitFactorForFrameD` caps the factor at 1.0 for a 256px cell, so at 100% or above
this pet never takes the downscale path.

⚠ That matters for scoping: if the complaint is "too big" or "too small" on screen, it is therefore
**not** a sprite-resolution problem, because the art is already at the ceiling. It would be the
runtime staging cap or the scale policy, and raising the former costs up to 4x sprite memory per
companion and would also need `MaximumGeneratedBytes` raised — a change the shimeji work
deliberately deferred once already. Confirm which way the complaint points before touching either.

---

## 📌 Open: the Animation permission is displayed as a control and gates nothing (filed 2026-09-28)

Found while wiring Companion Studio's "Preview highlighted action", which calls
`IHost.TryPlayAnimation`. The module did not declare `ModulePermissions.Animation` and the call
worked anyway.

`CompanionHost` gates the verbs that can: `PlaySound`, `PlayNotificationSound`, `OpenLink` and
`GetCompanionManager` all call `ModuleDeclares(moduleId, ...)`. The two animation verbs do not,
and the reason is structural rather than an oversight — they carry no caller identity to check:

| verb | line | gated |
|---|---|---|
| `TryPlayAnimation(ICompanion pet, string name)` | `CompanionHost.cs:308` | no — no `moduleId` parameter |
| `PlayAnimationAll(IReadOnlyList<string>)` | `CompanionHost.cs:359` | no — same, and it reaches EVERY pet |

⚠ **The gap is that it reads as enforcement from three directions at once.** The flag exists and is
commented "plays animations"; `AiBrain` declares it and its self-test asserts it, which looks like a
grant being checked; and `catalog.json` now prints the set to users as
`"permissions": "Speech, Animation, Companions, Storage"`. A user reading that reasonably concludes a
module without it cannot animate their pet. Nothing enforces that, and the same is true of any other
flag whose verb takes no `moduleId`.

⚠ **Adding the parameter is an ABI break and the contract is frozen**, which is the whole reason this
is filed rather than fixed. `PluginApi.cs` is explicit that members were removed *at* the freeze
rather than left declared-and-unraised, precisely so nothing ships that only looks like it works;
this is the same shape and it survived.

Two dispositions are honest and one is not. Recording it in the register as declarative, and saying
so where the permission is displayed, is honest. Gating it behind a new overload that does take a
`moduleId`, leaving the old one for compatibility, is honest. Leaving a set printed to users as
though every entry were a control is the one that is not.

CLOSES-WHEN: grep-present docs/DESIGN-REGISTER.md "Animation permission is declarative"

---

## Open: four read-only audits of the whole tree (filed 2026-09-24)

Four agents read `src/` (~38k lines), `modules/` (~43k), `tools/` (~9k) and the 27 PowerShell
scripts, with one rule: verify before filing, because this repo's own history says roughly a quarter
of previously filed items were already fixed. That rule earned itself — see the refuted list near the
end of this section.

**Seven findings were fixed in the same cycle and are not listed here**: the class-based jump landing
edges (45213e0), the drag/fall magic ids (b8c3b04), the Remembrance purge (9ad10cf), both Companion
Studio save targets (43ee2cb), the settings-window `File.Exists` (47d0872), and the backlog gate's own
open-item blindness plus the bug-number drift.

### A feature reports success while doing nothing

### Blocking IO, pipe deadlocks, and measured cost

- ✅ **DECLINED-MEASURED 2026-09-27. The cost is real and small; the proposed fix does not transfer
  from its precedent.**

  **Measured on this box**, replicating what the detectors do per call (EnumWindows +
  `IsWindowVisible` + `GetWindowText` + `GetWindowRect` on each visible window, 40 reps warm):
  **664 top-level windows, 44 visible, 0.60 ms per walk.** So the cost is 0.60 ms per falling or
  rising pet per tick — not per pet per tick generally, since neither detector runs outside a
  fall or a rise.

  **Why the precedent does not carry over.** `BlockedMonitorsForStandDown` shares one scan behind a
  **300 ms** window, and that is safe because fullscreen state changes slowly. A window RECT does
  not: 300 ms of staleness during a window drag puts a pet's landing line where the window used to
  be. The window short enough to be safe for landing detection is roughly one frame, and pets do not
  tick together — every `FormCompanion` owns its own `timer1` — so a frame-length window collapses
  only the pets that happen to tick inside it, not all of them.

  **What would make it worth revisiting**, so the next session has the number rather than deriving it
  again: a measured frame-time problem with many pets falling at once. At 0.60 ms a walk and a ~40 ms
  tick, one falling pet costs about 1.5% of a core; sixteen cost about 24%. Below that this is
  trading landing accuracy for a fraction of a millisecond, and nothing has been reported.

  ⚠ One detail worth keeping if anyone does build it: each detector excludes its OWN window
  (`if (hWnd == Handle) return true;`) but deliberately KEEPS other pets — *"Sheep windows doesn't
  have a title bar, but we want detect if another pet is present"*. A shared snapshot must therefore
  include every pet window and let each consumer skip its own handle. Getting that backwards lets a
  pet land on itself.
- ✅ **FIXED 2026-09-27, and half of this item had already gone stale.** `New-ContentCatalog.ps1`
  copies `minHostVersion` now, `RemoteCatalog.cs` has the field and bounds it like `Version` beside
  it, and `BuildAvailableRow` asks the SAME predicate the loader uses —
  `ModuleHostRequirement.IsSatisfied` — so the pane and the loader cannot drift into disagreeing
  about one module. An unrunnable module keeps its card and its permissions line; the Install button
  is disabled and carries the reason ("Needs a newer app: needs host 1.2.0 or newer (this host is
  1.1.4)"), because a greyed control with no explanation reads as a bug.

  ⚠ **Stale half, not re-fixed because it is no longer true:** the item said "the aibrain and
  reminder entries have no key" and that source had drifted. Measured 2026-09-27: all 7 entries in
  `modules.json` carry the key and all 7 match their source declaration. Only the catalog COPY was
  missing.

  Absent `minHostVersion` stays supported — an empty requirement is satisfied by every host, so an
  older catalog behaves exactly as before, and the helper is permissive for an unparseable host
  version too. Checked that this does not disable Install for everyone: host `ProductVersion` is
  `"1.2.5"`, parseable, and above agentflow's 1.2.0.

  Three source invariants assert the BINDING rather than the call, because a file that asks
  `IsSatisfied`, ignores the answer and sets `IsEnabled = true` would satisfy any presence check
  while behaving exactly as before. Mutation-tested: "ignore the answer" and "never ask" both exit 1.
### VS Code setup, AgentFlow

### Smaller, verified, grouped

- ✅ **CLOSED-VERIFIED 2026-09-27: already gone, and not by this item.** The 64-slot top-K
  insertion no longer exists. It was removed by the relevance-band change (the owner chose "relevance
  band, not a fixed count" over a fixed `TopK = 64`), and the replacement says so in its own comment:
  *"This also drops the old top-K insertion, which rescanned all K slots for the current minimum on
  every one of the n candidates: O(n*K) comparisons on top of the n dot products. One array of n
  floats is 31 KB at the owner's pool size and makes the pass O(n), so a wider candidate set is now
  CHEAPER than the narrow one it replaces."* Verified by reading `Pick`, not by trusting that comment.
- ✅ **FIXED 2026-09-27 (fortunes 1.0.10).** `SmartFortunes.StoodDown` records the stand-down in
  STATE, cleared at the start of every warm so a rebuild after the runtime is fixed reports progress
  again rather than a stale stand-down. `SmartStatusFor` takes it and answers "Smart picks are
  unavailable on this machine — the text engine could not start, so fortunes are chosen at random.
  Everything else works normally.", checked BEFORE the progress lines because a stand-down is
  terminal and they are not.

  Worth naming why the existing `Say()` was not already the fix: it writes to the diagnostic log,
  which the person looking at the Fortunes pane is not reading. They saw a progress message and
  waited. Asserted on both halves — the status must say what is wrong AND must not still claim work
  is happening — because a message that merely mentions the problem while reading "Indexing" would
  pass a looser check. Positive control with the branch removed: *"FAIL: a stood-down smart index
  says so instead of claiming to be indexing"*.
- ✅ **PARTLY FIXED, MOSTLY REFUTED 2026-09-27 (petstudio 1.1.10).** One real finding in three.

  **FIXED — the double classification.** `RenderMap` classified the whole pet so each chip's badge
  could read the result, then `RenderCensus` → `Census` classified it again. Twice per analyze, on a
  ~750 ms debounce while typing. `Census` now takes an optional precomputed map.

  **REFUTED — `PetAnalyzer.Analyze` does not decode the sheet.** It copies the base64 STRING into
  the report; the decode is in `PetSprite.TryDecode`, which the `SpriteKey` cache already guards. The
  entry had this backwards, describing the cache as guarding a small decode while a large one ran
  unguarded beside it. There is no second decode.

  **DECLINED-MEASURED — the `XmlSchemaSet` compile.** Real, but it lives in
  `CompanionXmlValidator.TryParse` (host code, every caller), not in PetStudio, and it costs
  **0.95 ms per call** measured warm over 30 reps against `Resources/animations.xsd`. Caching it means
  sharing an `XmlSchemaSet` — which Microsoft documents as not thread-safe — across every
  pet-loading path in the app, to save under a millisecond on a 750 ms debounce.
- ✅ **FIXED 2026-09-27 (aibrain 1.1.11), and this entry UNDERSTATED it.** "Up to ~2 s" reads as a
  worst case. Measured with the mechanism `OllamaClient` actually uses (HttpClient, 2 s deadline,
  literal `127.0.0.1` so no DNS): server running **5–56 ms**, server **REFUSED 2005–2008 ms**, host
  unreachable **2010 ms**. A refused localhost connection does not fail fast — it burns the entire
  deadline and returns `TaskCanceledException`. So the 2 seconds was not the rare hung-server case;
  it was what every user WITHOUT Ollama running paid, on every pane open, with three pane actions
  that rebuild the pane.

  The pane serves the last known answer and refreshes behind it, showing "Checking what is
  resident…" on the first open rather than freezing while it finds out. At most one probe in flight,
  so three rebuilds cannot open three sockets that each sit for two seconds against a server that is
  not there. The fresh-`HttpClient`-per-call half of this entry falls out of the same change: one
  client per 5 s instead of one per pane open.
- ✅ **FIXED 2026-09-27 (aibrain 1.1.10).** The three UI call sites pass
  `AiSettings.UiSaveBudgetMilliseconds` (1500 ms) — far beyond an uncontended save, which is
  immediate, and beyond two instances overlapping, while staying inside what reads as a responsive
  click. `ToggleEnabled` also stops swallowing the outcome: it was
  `try { s.Save(); } catch { }`, so a toggle that did not persist looked exactly like one that did
  and reverted on the next launch. It logs the failure now, which matters more with a bounded budget
  where a contended save can legitimately return false.

  The existing "rejects a held lock promptly" check could not have caught this: it passes a literal
  125 ms, so it passed for months while every caller used 10,000. The new assertion is tied to the
  CONSTANT the callers pass. Positive control with it set back to 10000: *"FAIL: the UI save budget
  gives up fast enough to keep the settings window responsive"*.
- ✅ **FIXED 2026-09-27 (aibrain 1.1.9).** `_localModels`, `_cloudModels` and `_modelIdByLabel` are
  guarded by `_modelsLock`. Each refresh holds it around "replace the list, then rebuild the options
  from it", so that update is atomic rather than merely thread-safe in pieces; Monitor is reentrant,
  so the finer locks in `FormatModelLabel` and `ResolveModelId` nest safely.

  ⚠ `LoadPaneValues` deliberately does NOT hold the lock across its whole span. The first attempt
  did, which put `VramStatusLine`'s blocking model query (up to ~2 s against a local Ollama) inside
  the critical section, where a pane open would stall both refresh actions — a worse bug than the
  race. It snapshots both lists under the lock and formats outside it.

  No automated regression test, deliberately: reproducing the race needs two live endpoints and the
  methods are private, and a source-text check that the fields "appear near a lock" is the kind that
  cannot meaningfully fail. Verified by build (0 warnings) and `--aibrain-selftest` RESULT=PASS.
### Measured 2026-09-25: the positive-probability warning IS user-visible

- ✅ **FIXED 2026-09-25 in content.** `Companions/pink_sheep/animations.xml` states 173
  (`king_jump_top`) and 182 (`king_jumpB_top`) each gained
  `<next probability="100" only="taskbar">166</next>`, which is the pet's own convention twice over:
  `king_walk_top` (167), the same `_top` family, already answers the taskbar with exactly that line,
  and both 173 and 182 already answered `only="window"` with the same target, `king_slamB` (166). A
  window top and the taskbar are both horizontal surfaces this pet lands on, and it already knew what
  to do on one of them.

  The host option was NOT taken. Raising `TASKBAR | HORIZONTAL` at the call site would have fixed all
  six states and every pet at once, and made every `only="horizontal"` edge in all 54 companions newly
  eligible at the taskbar, changing landing behaviour for pets that are not broken. The enum's own
  `HORIZONTAL_ = 0x06` (WINDOW|HORIZONTAL, deliberately excluding TASKBAR) says the distinction was
  intended.

  The measurement that drove it, kept because it is the only evidence that exists: the owner's
  installed 1.2.4 log carried **335 occurrences over ~16 hours**, about 21/hour and 36% of the whole
  diagnostic log, **all** from `pink_sheep` and **269 of them** these two states at the taskbar. The
  two converted pets installed alongside produced **zero**, so this was never a converter problem.
  Consequence at `FormCompanion.cs:1246`: no eligible transition sets `bLeavingScreen = true`, and a
  sprite fully outside the monitor is respawned, so the pet walked off the bottom and reappeared.

  **Confirmed in the real app, 2026-09-25, with a positive control.** A 45-minute run of the FIXED
  pet recorded **3** warnings, and all three were `state 44, where=130` -- one of the states this
  change does not touch. States 173 and 182 at the taskbar recorded **zero**. The control is the
  part that makes the zero mean something: the detector demonstrably fired three times during that
  very run, so a zero for the fixed pair is a real zero rather than a dead harness. At the measured
  rate of about 17/hour for those two states, 45 minutes expected roughly 13.

  An earlier 12-minute run of the UNFIXED pet recorded zero and was discarded rather than reported:
  at ~21/hour a 12-minute window expects about three events and observing none is ordinary, so it
  was evidence of nothing either way. That is why `tests/companion-border-invariants.ps1` asserts
  the property instead, and why it is the check in the gate rather than a soak.

  (The soak logs the pet as `eSheep` because `localxml=` sets no folder id. It is pink_sheep's XML:
  state 44 with 7 declared candidates at `where=130` matches the owner's log entry exactly.)

  ⚠ **The remaining 66 occurrences are NOT fixed and are a different shape**: states 44, 114, 81
  and 122 at `where=130` and `where=18`, which are window-edge situations rather than the taskbar.
  Filed below rather than left implied. The soak above saw state 44 three times in 45 minutes,
  about 4/hour, against 2.3/hour in the owner's 16-hour log: the same order, still happening.

- ✅ **CLOSED 2026-09-26, and the item was WRONG about what it found.** These four
  (`jump_down` 44, `blastoffb` 114, `chaseb2` 81, `king_walk` 122) do lack an `only="window"` edge,
  but the host does NOT treat that as a fault and never did. `where=130` is
  `WINDOW | WINDOW_BOTTOM`, a pet rising into a window's underside, and `FormCompanion.cs:1292`
  answers a -1 by giving the handle back so the pet carries on rising, with the comment "Nothing
  wants to hang there." `where=18` is `WINDOW | WINDOW_LEFT`, and `:1126` answers a -1 by releasing
  the grip or dropping the handle so gravity takes over. Both outcomes are designed, documented and
  correct. Adding edges would have CHANGED behaviour -- making the pet hang where it currently
  passes through -- on the strength of a log line that was not reporting a defect.

  The real defect was the WARNING. It logged at `warning` for a designed outcome, which is why this
  one line was **36% of a 16-hour diagnostic log** and why the 269 occurrences that genuinely
  mattered (the taskbar pair, since fixed) sat in the same undifferentiated pile as the 66 that did
  not. `SetNextBorderAnimation` now takes `absenceIsNormal`, because only the caller knows what a -1
  costs: at the taskbar it means `bLeavingScreen` and the pet walks off the screen; at a window edge
  it means nobody wanted to grip there. The three window-grip sites pass `true` and log at `info`
  with the reason attached.

  ⚠ The two `WINDOW_TOP` sites are equally graceful but still log at `warning`. They accounted for
  **zero** of the measured occurrences, and they use the two-argument overload, so marking them would
  have meant a third overload written on speculation. Left deliberately.
### Filed 2026-09-25 by the four parallel re-audits

Verified before filing. The three code audits raised 39 findings between them; what is here is what
survived a second check, minus the 14 fixed on the day in `e31c5bb`.

**Host, user-visible**

**Modules**

- ✅ **FIXED 2026-09-27 (remembrance 1.0.12).** The warning now says the device lists refresh
  every time the pane opens and that reopening on the console is enough, with no restart. Re-verified
  against the code before changing the text rather than trusting the item: `Load` at
  `RemembranceModule.cs:661-664` runs `AutoDetectWhisperOnce`, `AutoDiscoverModelsOnce` and
  `RefreshDynamicOptions` before returning values, and the host calls `Load` on every pane build.
  Telling a user to restart an app that does not need restarting is a worse defect than saying
  nothing.
- ✅ **FIXED 2026-09-27 (fortunes 1.0.9).** The `ms.Set("spicyTier", "")` is gone. Re-measured
  first: `spicyTier` appeared exactly once in the entire repo, at that write, and
  `MigrateContentLevel` reads only `spicyFortunes` and `spicyOnly`. Clearing a key that never existed
  migrates nothing, so the comment justifying it ("a stale value can never be re-migrated") described
  a hazard that could not occur.
**Converter and build**

- ✅ **FIXED 2026-09-27 (petstudio 1.1.8).** `turn` is added to the name set and built as a node
  only when some spoke is locomotion, and both sites are gated together — emitting the node while
  leaving it out of the name set would be the same unreachable-state defect from the other direction.
  Fixed the way the emitter already handles the structurally identical ceiling case at `:140`, which
  clears `ceilingSpokes` when nothing will climb: do not emit a region nothing can enter.

  Covered by a second fixture, `NoLocomotionActionsXml` — three actions, none of them
  `Type="Move"`. The main fixture could not catch this (every shipped pet carries a Walk), which is
  exactly why it needed one of its own. Positive control with the fix reverted and rebuilt: *"a skin
  with no Move action left unreachable animations: 7"*, *"was not accepted, so a valid playable pet
  fails conversion"*, *"still emitted a `turn` animation"*, exit 1. With the fix, SELFTEST PASS.
  Shipped output unchanged: all 54 companions still verify with 0 invalid, 0 round-trip failures and
  the same 7 hand-authored unreachable.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.7).** `PetEmitter.Has` is `Ordinal` now, matching
  `ActionClassifier.Has`. Ordinal is the correct reading rather than merely the consistent one: every
  token either helper tests for is a case-sensitive Shimeji identifier (`activeIE`, `totalCount`,
  `TargetX`, `Math.random`), and `cursor` comes from `mascot.environment.cursor`.

  Covered by a new synthetic action in the emitter self-test, `RestNearCursor` — capital C in the
  name, no condition, no expression, no reference to `mascot.environment`, and its own art so a
  direction collapse cannot merge it into a neighbour and hide the answer. The existing gaze cases
  test the true positive; nothing tested the false one. Positive control run BEFORE the fix: *"FAIL
  an action was tagged faceCursor for having "Cursor" in its NAME, with no cursor condition
  anywhere"*, exit 1. After: SELFTEST PASS, and `verify` over all 54 shipped companions unchanged —
  0 invalid, 0 round-trip failures, the same 7 hand-authored unreachable. The open question this item
  raised ("whether any of the 31 shipped conversions hit this") is therefore answered for the
  OUTPUT: nothing shipped changes.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.7).** The stdout read is `CopyToAsync` with a bounded
  wait, then a short second bound for teardown once stdout is at EOF, and `Kill(true)` takes the tree
  to match `Engine.cs`. Exactly the defect fixed the same night in `ContentCatalogAssets.ps1`: a
  synchronous drain blocks until the child exits, so any `WaitForExit(timeout)` after it is
  unreachable until the thing it was meant to bound has already resolved itself.

  The check that should have caught it was pinned to three named files and this was the fourth site,
  so the repo-wide redirect loop now ALSO bans synchronous reads of a redirected stream
  (`.ReadToEnd()` and `.BaseStream.CopyTo(`), folded into the existing offender list so the
  Assert-True self-count is unchanged. `WebPLoader.cs:118` was the only such read in the repo.
  Mutation-tested both forms against HEAD's copy of the check: HEAD stayed green on WebPLoader (the
  hole) and caught Transcriber (already pinned), which is the argument for making it repo-wide.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.9).** The zip extraction, `SkinLayout.Detect`,
  `BundleConverter.ConvertBundle` and `ShimejiEngine.ConvertSkin` all run under `Task.Run` now, with
  the `out` parameters captured into locals; the editor, analysis and import-loss panel still update
  on the UI thread after the await.

  A re-entrancy guard comes with it, released in a `finally`. The window is responsive now, which
  means it is also clickable — two conversions writing the editor and the loss panel at once is not
  a state this window has an answer for, so a second Import is refused rather than queued. The
  `finally` is the part that matters: a conversion that throws must not leave the window refusing
  every later import.

  The invariant that named `ModulesPaneControl.cs` only now covers `PetStudioWindow.cs`, and asserts
  the WRAPPING rather than the presence of `Task.Run`: `ConvertSkin` and `ConvertBundle` have no
  Async overload, so a check looking merely for `Task.Run` somewhere in the file would pass on a
  version that wrapped something else and still converted inline. Mutation-tested both halves —
  un-wrapping the extraction and un-wrapping the conversion each exit 1. Source-invariant count
  138 → 139.
- ✅ **FIXED 2026-09-27.** `build.ps1` keeps `#requires -Version 5`, because the ordinary build
  genuinely runs under either edition and that had to stay true. The floor now sits on the `-Zip`
  path alone, and it runs BEFORE the build rather than after it. `New-DeterministicPortableZip.ps1`
  keeps its `-Version 7`: that is measured, not stylistic, since deflate output differs by edition
  (63,580 bytes under 5.1 vs 64,271 under pwsh 7.6.5 for one payload, different SHA-256) and those
  archives are committed and hashed in `catalog.json`. Verified both directions: `-Release -Zip`
  under 5.1 now fails in 4 s -- shell startup, never reaching the compiler -- naming the host and the
  reason; a plain 5.1 build still succeeds, exit 0, 0 warnings.
  ⚠ `New-ModulePublish.ps1` and `New-ModuleDistZip.ps1` were named as "same transitively" and are
  NOT fixed: both are already `-Version 7` themselves, so `#requires` stops them at their own entry
  point rather than after someone else's long build. That is the behaviour this item wanted.

### Re-audit 2026-09-27 (three parallel read-only passes, after the backlog reached 1)

Seven findings were fixed in the same session and are not listed here: the unfalsifiable assertion
counter, the PetStudio import-guard ordering, the half-cached companion thumbnail, `UsePet`'s missing
`activePetId` rollback, the Remembrance standalone-snapshot purge, and its two missing
`NamesThisModuleWrites` assertions. What follows is what was NOT fixed. Each was verified against the
source before filing; the auditors' own refuted candidates are recorded at the end so nobody re-files
them.

- ✅ **FIXED 2026-09-27 in `d0dff9a`.** Only the RANDOM arm is gated now, so a pin is honoured at
  spawn whatever "Let companions spawn on any screen" says — which is what both doc blocks always
  promised. Verified in the real app rather than the suite: the fullscreen stand-down probe passes
  all 6 steps across 3 monitors. Pinned by a source invariant asserting the STRUCTURE (a check that
  merely found `PinnedDisplay` in `Play` would pass on the broken version, which read it one line
  inside the gate); mutation-tested by putting the pin back inside the gate, which exits 1.

  ⚠ **Filed open for one push after it was already fixed, and its CLOSES-WHEN could not have caught
  that.** The criterion was `grep-absent src/dotNet/FormCompanion.cs "int pinned = PinnedDisplay;"`
  — but the fix KEEPS that exact line and moves it out of the gate, so the string never goes absent
  and the check would have reported the item open forever. A criterion has to track the PROPERTY, not
  a string that happens to be near it. Caught by re-reading the open list against the diff, which is
  the burn-down audit this repo's own gate asks for.
- ✅ **FIXED 2026-09-27.** `build.yml` now runs `tests\Test-BacklogClosingCriteria.ps1` and
  `tests\companion-border-invariants.ps1`, closing the last of the gate/CI split its own comments
  claimed twice to have closed. Both signal failure by `throw`, so each is a bare call under
  `shell: pwsh` with no `$LASTEXITCODE` guard — that guard would be the dead branch this repo has
  already had to correct twice.

  Added only after the PREVIOUS CI step was proven on the runner rather than assumed: the
  module-self-test step added earlier the same day shows `OK agentflow PASS (484 assertion line(s))`,
  `OK aibrain PASS (216)` and `Module self-tests OK: 6 covered module(s) passed` in the log of run
  `510295d`, which also settles that `HttpListener` binds unelevated there — the risk flagged when
  `FakeCdpServer` landed. Stacking a second untested CI step on an unverified first is how a CI
  change becomes two problems.

  ⚠ Closing this is itself the loop working: adding the steps made
  `Test-BacklogClosingCriteria.ps1` report this entry CLOSEABLE and fail the run, which is precisely
  what it exists to do — fire in the same run that proves the fix works.
- ✅ **FIXED 2026-09-28 (petstudio 1.1.12).** `Emit` is now a thin guarded wrapper over
  `EmitCore`, refusing a re-entrant call with an `InvalidOperationException` that names the three
  fields. A wrapper rather than a try/finally around the ~150-line body, which has many exit paths.
  It throws rather than serialising: a second concurrent conversion is a caller bug, and quietly
  queueing it would hide the bug while making the caller slow for a reason it cannot see.

  PROVEN, not assumed. A temporary probe called `Emit` from inside `Emit` via the sprite loader
  — which `Emit` invokes while the flag is held — and the exception
  arrived. Probe removed; converter self-test passes without it, and `verify` over all 54 shipped
  companions is unchanged.

  This is the item that was "defended by a caller, not by the type". It is defended by the type
  now.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.15).** Fire and forget, no `Wait`, same shape as
  `AiBrainModule.BeginVramProbe`. The answer lands in settings and the next pane build shows it
  — which is exactly what happened before whenever the 3 s cap expired, minus the
  freeze. The continuation writes through `PersistOnUi` rather than touching `_settings` from a pool
  thread; the module already routes background writes that way for the "Collection was modified"
  reason recorded on that helper.

  The doc comment said "Bounded and off the UI thread", which was half true and the dangerous half:
  `Task.Run` moved the HTTP call, and `probe.Wait(3s)` then blocked the UI thread anyway. Its
  justification — *"a refused connection is immediate"* — is the premise
  this repo measured false the day before: refused localhost burns 2005-2008 ms. One detail the
  entry understated: the gate is on the models cache, NOT on `summaryOn`, so users who never enabled
  the summary feature paid the freeze too.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.15).** It names `"Transcribe a WAV file..."`, which
  exists, and says which file to pick. The old sentence had exactly one grep hit in the repo
  — itself — because no "Re-transcribe" action was ever built, and it is
  the only sentence a user reads on the one path where transcription has already failed.
- ✅ **FIXED 2026-09-28 (petstudio 1.1.12).** `FindBundleRoot` and `ReadBundleName` both run
  under `Task.Run` now. `ReadBundleName` rides along deliberately: it is the same class of work and
  sits between the two, so leaving it would only move the stall one line down. No
  `ConfigureAwait(false)` anywhere in that method — everything after the awaits is
  UI-affine and depends on resuming on the captured context.
- ✅ **FIXED 2026-09-28.** The control names the message it expects (`'Unsafe major-upgrade
  schedule'`) and reports a throw that is not it, instead of scoring any exception as proof. That is
  the correction `Test-StagingPathSafety.ps1:21-23` already carried and this file never got.
  Positive control against the real `dist/DesktopAICompanion.msi`: with the correct expectation both
  cases pass, exit 0; with it pointed at a message the guard never emits it exits 1 saying *"threw
  something OTHER than the schedule guard, so it proves nothing about the boundary"*. File restored
  byte-identical.
- ✅ **FIXED 2026-09-28.** Both dead guards removed, premise verified rather than assumed:
  `New-ContentCatalog.ps1` contains zero `exit` statements and `Test-ModulePublishFreshness.ps1`
  signals all 19 of its failures by `throw`, so `$LASTEXITCODE` there held whatever the last `git`
  call inside the callee left. `$ErrorActionPreference = 'Stop'` means a callee throw escapes on its
  own, which is the house form at `tests/run-gate.ps1:160-161`.
- ✅ **FIXED 2026-09-28.** The phantom assertion is gone; the direction check (`launch >= 0`)
  stays. There is no launch clamp to assert — `BuildSpoke` sets
  `vy0 = SolveJumpLaunchY(jumpSteps)` and discards the source velocity, and that solver searches
  `JumpLaunchMinMag`(4)..`JumpLaunchMaxMag`(40), so -40 is a legitimate answer.

  Worth recording why it never fired: the fixture's 2-pose BigJump gives 14 steps, for which the
  solver returns exactly -15, so `launch < -15` was false **by one unit, with zero margin**. Any
  change to `JumpPeakPx`, `JumpDescentY`, `JumpArcSteps` or the fixture's frame count would have
  reddened it with a diagnosis naming a clamp that is not in the code. The real property is asserted
  45 lines below against `PetEmitter.ArcRisePx`. SELFTEST PASS after removal.
- ✅ **FIXED 2026-09-28.** `MAPPING.md` and the `run-gate.ps1` comment both say 54/31/6 now,
  matching what `BundledConfSelfTest.cs:34` asserts on every gate run. The gate comment was wrong a
  second way the entry did not mention: it called the census *"a dev step — that config
  is copyrighted and must not live in this repo"*, three lines above the `selftest` call that runs it
  against the bundled conf. Both corrected, with the 2026-08-28 reason (ClimbWall stopped being
  reported as needing selfX/selfY) recorded beside each.
- ✅ **FIXED 2026-09-28.** Parameter and invocation both removed. One detail the entry did not
  flag: the hook was the LAST parameter, so removing it also had to drop the trailing comma on
  `$ContentDirectories` — the patch script asserted that and refused to write until it
  was handled. Parse-checked afterwards: 0 errors.
- ✅ **FIXED 2026-09-28.** The modules direction now mirrors the two blocks above it, so the
  `.DESCRIPTION`'s claim of membership "in both directions" is true for all three groups.
  Mutation-tested: dropping a real zip into `modules-dist/` as `strayghost.zip` exits 1 with *"1
  module zip(s) exist under modules-dist\ but are absent from catalog.json"*; the clean
  tree exits 0; the stray was removed afterwards.
**Smaller, verified, grouped — re-audit 2026-09-27**

- ✅ **FIXED 2026-09-28, and there were TWO of them.** The filed site is guarded; so is the one
  the entry did not name, at `:142`, which is worse. That one sits INSIDE a catch handler, so a
  module with a null `Info` that also threw from `Shutdown` raised an NRE from the handler itself
  — escaping `ShutdownAll` and skipping `Alc.Unload()` for that module AND every module
  after it in the list. A handler that can throw is not a handler.

  Both now read `Info` into a local and fall back to `"(no ModuleInfo)"`. Still not reachable with
  any in-repo module — all eight declare `Info` — so this is the
  third-party path, which is exactly the path the class's isolation promise exists for.
- ✅ **FIXED 2026-09-28.** A local now. Its four siblings (`addPet`, `removePet`, `syncPets`,
  `petSpeech`) are all re-read later to toggle enablement or rebuild submenus; this one's every use
  was the construction block. The build caught the leftovers for me: removing the writes left the
  field declaration and its `Dispose` null, and warnings-as-errors turned "assigned but never used"
  into a build failure rather than a slow rot.
- ✅ **FIXED 2026-09-28.** Field and assignment both gone. `Loaded` is private to `ModuleHost`
  and its only consumers are `ShutdownAll` (`Module`, `Alc`) and the `Modules` projection
  (`Module`), so nothing could have read it.
- ✅ **FIXED 2026-09-28.** Parameter dropped and the always-taken branch inlined; both call
  sites updated (`ProcessIcon.cs:339`, `ContextMenus.cs:609`). The reason for removing rather than
  leaving it is recorded in a `<remarks>`: with `false` the method still ran `pi.Dispose()` above,
  leaving the app alive with no tray icon and no timer armed — BUG-001's "running but
  unreachable" state, reachable only by passing an argument nobody had reason to pass.
- ✅ **FIXED 2026-09-28, on the success path only.** The timeout path deliberately does NOT
  dispose: the worker is abandoned rather than cancelled and still holds the handle, so disposing
  there races its `Set()` — which is why that `finally` swallows
  `ObjectDisposedException`. On the success path the worker has signalled and cannot touch it again,
  and that is where the `Dispose` was missing entirely.
- ✅ **FIXED 2026-09-28.** Both guards added, so it matches its four neighbours. Needed a new
  `FormCompanion.IsFullscreenBlocked` accessor, because `hwndFullscreenWindow` is private and
  `TopMostSheeps` lives in `StartUp` — reaching into private state would have been the
  wrong fix.

  The null guard is parity rather than a known crash, as the entry said: the slots are compacted on
  kill and zeroed on mass-kill, so no null inside `[0, iSheeps)` could be constructed. Both guards
  are cheap, and an asymmetry like this outlives the reason for it.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.15), by wiring it rather than deleting it.**
  `RefreshDynamicOptions` calls it first, so each pane OPEN starts from a fresh WASAPI enumeration
  — what someone who just plugged in a headset expects — while the
  collapse from four enumerations to one still happens within the build, after that line.

  Deleting it would have left the record wrong in the other direction: the entry that closed the
  device-caching work cites this method as the escape hatch ("the window is short on purpose and
  `ForgetCachedDevices()` drops it explicitly"), and that safety property is now true of the shipped
  code rather than only of the note.
- ✅ **FIXED 2026-09-28 (agentflow 1.4.10).** Deleted. `SelfCheckCapabilityLog` constructs a
  fresh `AgentFlowModule` and relies on `_lastLoggedCapability` starting null, so it never needed the
  reset. AgentFlow's five other `...ForSelfTest` seams carry 3-6 references each; this was a seam for
  a test that was never written that way.
- ✅ **FIXED 2026-09-28, and the obvious fix would have broken the gate.** The entry suggested
  "a `$deps.Count -eq 0` failure would cost nothing". It would have reddened the gate immediately:
  `TestModule.csproj:16` sets `GenerateDependencyFile=false` and `testmodule` IS in
  `$RequiredModules`, so the loop legitimately no-ops on one of the eight every run.

  So the check is per-module aware. A module with no deps file fails UNLESS it is named in
  `$depsOptOut`, and a module in `$depsOptOut` that DOES emit one also fails — so the
  exemption cannot go stale and hide a check that could run. Both directions mutation-tested against
  the real runner: emptying the opt-out gives *"module 'testmodule' has no *.deps.json in the build
  output"*; adding `agentflow` to it gives *"is listed in $depsOptOut but DOES emit
  AgentFlow.deps.json"*. Clean run: 19 flags, no failures. The two adjacent comments that were false
  for testmodule (it carries no ModuleKit.dll either) are corrected.
**Checked and REFUTED by the re-audit — do not re-file**

- `SpriteBounds`'s cache keyed on `Image` alone, ignoring `transparencyKey` (`SpriteBounds.cs:28-36`).
  Unreachable: `TransparencyKey` is only ever `Color.Magenta` or `Color.Empty`, each `Xml` owns its
  own frame bitmaps, and every form sharing an `Xml` has the same transparency mode.
- `AudioOutput.TryStart` leaving `MixerInputEnded` subscribed on an abandoned mixer
  (`AudioOutput.cs:283-297`). The mixer is the PUBLISHER and is unreferenced after the failure, so the
  handler roots nothing.
- `FormSpeech` timers not unsubscribed in `Dispose` (`FormSpeech.cs:450-459`). Both are owned fields
  and are disposed there; the subscriptions die with them.
- `_netCts.Dispose()` while a download holds the token (`ModulesPaneControl.cs:405`,
  `CompanionsPaneControl.cs:573`). `Cancel()` precedes `Dispose()` in both, the documented-safe order.
- ~20 members that look dead and are not, all grepped and found alive, including
  `SpriteFrameForDiagnostics`, `AddDebugInfoWindowOnly`, `SyncSheeps`, `GetNextSpawns`,
  `CaptureScreenBounds`, `DeriveOnScreenMix`, `StopAllModuleSound`, `IsAtMaxPets`, `SpawnPreviewPet`,
  `TryPlayAnimation`, `EscapeToBath` and `ReassertSequence`.
- `PressBudget.TryPress`'s repeat arithmetic (permits exactly 3 identical presses, refuses the 4th,
  matching its doc) and `CdpSession.Send`'s `RootElement.Clone()` inside a `using` (documented to
  outlive its source document). Both chased hard and both correct.
- `turn` being emitted unreachably after the no-locomotion fix: guarded correctly at
  `PetEmitter.cs:200-201`, and set-piece steps cannot take that path because `ExpandSetPieces:2199`
  gives even the last step a non-null `ChainNext`.

**Checks that cannot fail (the category this repo keeps finding)**

- ✅ **FIXED 2026-09-28. The run extends itself, and the root cause was not the duration.**

  A settled sample lands every 40 cycles (`Program.cs`, `SettledSampleEveryCycles`), but the churn
  loop terminated on WALL CLOCK. Nothing tied the two together: the documented default asks for 15
  target cycles while the FIRST settled sample is at cycle 40, so every sample this soak has ever
  published came from the minimum-duration overrun rather than from the cycle target. Cycle rate
  falls on a loaded box, fewer than 2 land, and the driver threw.

  The loop now keeps cycling until the verdict has its two samples, bounded by
  `DESKTOPPET_RESOURCE_CHURN_MAX_DURATION_MS`, which the driver derives from its own completion
  deadline so a box too slow to reach a verdict still publishes a marker and earns a specific
  failure. An idle box pays nothing -- it is past cycle 80 well before the minimum elapses.

  **Mutation-tested across three legs**, with the degraded rate reproduced deterministically (churn
  interval 600 ms instead of 250, landing ~66 cycles inside the 40 s minimum -- past the sample at
  cycle 40, short of the one at 80):

  | leg | driver + binary | result |
  |---|---|---|
  | A | old + old | the original *"at least 2 are needed ... Increase -DurationSeconds"* -- the degraded scenario is real, not theoretical |
  | B | new + old | names the stale build; no `PropertyNotFoundStrict` leak |
  | C | new + new | PASS, extended to 2 settled samples, finishing at 61036 ms |

  ⚠ **Two further defects were found BY the mutation test, and both are fixed here.**

  *The driver leaked a PowerShell rule name instead of a cause.* Pointed at a build older than
  itself -- which `-ExecutablePath` makes easy to do on purpose -- it died with a bare
  `PropertyNotFoundStrict` from `Set-StrictMode`. It now checks the marker carries its verdict
  fields by name and says which one is missing and why.

  *The cleanup replaced the failure it was cleaning up after.* `Stop-TestProcess` runs from the
  `finally` block, and `taskkill` is the only call in it without a `try`/`catch`. A native command
  writing to stderr raises a TERMINATING `NativeCommandError` under this script's
  `$ErrorActionPreference = 'Stop'`, and an exception thrown in a `finally` REPLACES the one already
  in flight. So a real soak failure surfaced as *"taskkill.exe : ERROR: The process NNNN not
  found"* -- which taskkill writes whenever the process has already exited, i.e. on exactly the
  failure paths this cleanup exists to serve. It swallowed two mutation verdicts before I found it.
  The proof is the before/after on one unchanged run: only taskkill noise before, the real message
  after.

  CLOSES-WHEN: grep-present tests/runtime-resource-soak.ps1 "older than this driver"
- ✅ **FIXED 2026-09-27.** `.github/workflows/build.yml` runs `tests\Test-ModuleSelfTests.ps1`,
  so the 684 module assertions now run on a push as well as in the gate. Safe to add because the step
  above it already launches the same exe from the same path — only the flag was new — and verified
  by running the CI command verbatim from the repo root.

  ⚠ **Closing this exposed a check that could not fail, and it was mine.** The `CLOSES-WHEN` I filed
  with this item had its arguments backwards (`grep-present <needle> <path>` instead of
  `grep-present <path> "<needle>"`). The verb pattern wants a QUOTED needle second, so the whole line
  failed to match and was skipped in silence: the report said "3 carry a CLOSES-WHEN" while the file
  contained 5, and this one had already been satisfied. `Test-BacklogClosingCriteria.ps1` now reports
  any line beginning `CLOSES-WHEN:` that does not parse, so a criterion cannot go quiet by typo.
- ✅ **MOSTLY FIXED 2026-09-27 (fortunes 1.0.11, aibrain 1.1.12), and I was wrong about it TWICE.**

  First I filed it saying the host "cannot run their assertions" — false; all three have dedicated
  host-side self-tests the gate already ran. Then I corrected that to "real work per module rather
  than a delegation" — also false, for two of the three. `FortuneEngineProbe.Run` and
  `AiEngineProbe.Run` are both `public static bool (out string)`, the exact signature the convention
  wants. Only the entry point on the MODULE TYPE was missing, so each is one method.

  Coverage went from 4 modules / 684 assertions to **6 modules / 982** (agentflow 489, aibrain 217,
  remembrance 114, fortunes 70, blinkingled 56, reminder 36).

  The gate demanded the bookkeeping itself, which is the loop working as designed: adding the methods
  turned `Test-ModuleSelfTests.ps1` RED with *"now PASSES its module self-test. That is good news —
  move it from UNCOVERED to COVERED"*.

  ⚠ **PetStudio remains a recorded gap**, and the reason is now in the file rather than in my head:
  `BehaviourChainSelfCheck.RunChecks` takes a `fixturePetXml` that the host supplies from its OWN
  resources, so covering it means embedding a fixture in the module, not adding a delegation.

  One stale comment found on the way and left for a future pass: `BehaviourChainSelfCheck`'s header
  still warns that `--module-selftest` takes the FIRST `bool SelfTest(out string)` anywhere in the
  assembly. That was fixed — `ModuleConventionSelfTest` resolves against the module TYPE — so the
  warning now describes a hazard that no longer exists.
**Dead code, verified across `src/`, `modules/` and `tools/`**

- ✅ **FIXED 2026-09-27, 237 lines removed — and FOUR of this item's claims were WRONG.**
  Every member was re-checked for callers across all 205 `.cs` files before removal rather than
  taken from the list, which is how the four were caught.

  **Deleted, verified dead:** `WindowTheme` minus `IsDark()` (~150 lines: two P/Invokes, immersive
  dark title bars, the recursive control walk, uxtheme DarkMode_CFD / DarkMode_Explorer, seven
  colours — all serving the WinForms tray dialogs retired in S5b-3);
  `CompanionHost.SpeechSourceModuleIds`; `LocalData.GetScale()`, `.GetPetSizeLevel(string)` and
  `.GetEffectivePetScaleFactor(string)` (the `NoLock` form behind the second is kept and still
  used); `LocalData.SetXml`'s unread `folder` parameter, which also made
  `externalCandidate ? "external" : ""` dead at its only call site; the 2-argument
  `ProcessIcon.TaskbarWatcher` overload (every call site passes 3 or 4); `OptionsWindow._dirty`
  (assigned, never read — the button was always driven by the parameter); and the `AppPaths`
  vector-cache cluster, superseded because the Fortunes module owns that storage now, so the base
  migration could never run for anyone. Plus, with a publish each, `NotifyBudget.Forget`
  (agentflow 1.4.8, superseded by `Retain`) and `SmartFortunes.LastCandidateCount`
  (fortunes 1.0.8).

  ⚠ **NOT dead, left alone, item refuted:** `AppSettingsStore.FilePath` is read at
  `LocalData.cs:969`; `.BackupPath` at `CoreTests/Program.cs:419` and `:422`; `.IsReadOnlyFallback`
  at `:938` and `:948`; `.LastRecoveryFile` at `:444`–`:449` and `:472`. The item specifically
  claimed the last of these was "never read, so the corrupt-file preservation path records where it
  put the file and nothing can report it" — the tests read it. Deleting the four would have
  broken CoreTests, which is what caught the claim.

  One decision recorded rather than made silently: `LastCandidateCount` could have been ASSERTED
  instead of deleted, since its doc says its narrowness "WAS the bug". Rejected — the only
  invariants available are `<= MaximumCandidates` (512) and `<= LastBandCount`, and the self-test
  pool is 131 lines, so the cap never applies and neither could fail. Closing a dead-code item by
  adding a check that cannot fail trades one defect for a worse one.
- ✅ **FIXED 2026-09-27 (petstudio 1.1.7).** Collapsed to one method with nearest-rounding.
  The doc was worse than the dead branch: its `<param>` claimed `roundUp` was "used for rests, where
  undershooting is the thing that reads as wrong", while the rest call site passed `false` under a
  comment saying the opposite — "nearest rather than up, so a long performance lands inside the
  9-12s band instead of overshooting". Documentation describing a policy the code had decided
  against, for a caller that did not exist. The decision is now recorded in a `<remarks>` so the
  parameter is not reintroduced. Build 0 warnings; SELFTEST PASS; all 54 companions verify
  unchanged.
- ✅ **FIXED 2026-09-27.** Deleted. Confirmed unreachable before removal: the check eight lines
  above returns false whenever `rctO.Bottom <= rctO.Top`, and `0 <= 0` satisfies that, so a
  zero-height rect never reached the line. The closed-window case its comment described is already
  covered by the `GetWindowRect` failure and the degenerate-rect test.
**Optimisation, costs named rather than timed**

- ✅ **FIXED 2026-09-27.** `_iconCache` mirrors `_statsCache` beside it — same lifetime, same key —
  and `ForgetStats` clears both, because the icon comes out of the same rewritten `animations.xml` as
  the counts and goes stale at the same moment. Forgetting one without the other would leave a card
  showing the new name and counts beside the old picture.

  Measured before fixing (warm, 20 reps, `ReadAllText` + `XDocument.Parse` + the icon lookup, which
  is exactly what the miss path did): **esheep64 155 KB → 4.2 ms per card**, **pink_sheep 1133 KB
  → 20.4 ms per card**. Per card, per rebuild, on the UI thread.

  Misses are cached too, as a null value: a pet whose XML carries no icon, or whose folder has gone,
  would otherwise re-parse its entire `animations.xml` on every rebuild forever — the expensive
  case, cached for nothing.
- ✅ **FIXED 2026-09-27 (remembrance 1.0.13) — one enumeration, not the two this entry asked for.**
  A short snapshot behind both accessors means the four call sites inside one `Load` closure share a
  single enumeration per flow.

  No speed figure is quoted, because none was measured: the claim here is a COUNT (four calls become
  one per 1500 ms window), which is structural and checkable by reading the code, not a timing
  saving.

  It also closes a consistency bug the entry did not mention: the dropdown options and the
  "devices: N output, M mic" count came from SEPARATE enumerations, so a device appearing or
  disappearing between them produced a status line that disagreed with the list directly above it.
  The window is short on purpose — a reopened pane must not show a stale list after a headset is
  plugged in — and `ForgetCachedDevices()` drops it explicitly.
### Filed 2026-09-25 — the leak soak's verdict depends on its duration

- ✅ **FIXED 2026-09-27: the bound is a RATE now.** `MaximumHandleGrowth` and its two siblings are
  per SETTLED INTERVAL, scaled by however many intervals a run actually observes. The defaults were
  always calibrated for one interval, which is what the default duration produces (cycles 40 and 80),
  so the calibrated tolerance is unchanged and every duration is now comparable instead of quietly
  stricter. Verified: the 180-second run that used to fail at +84 and +90 now passes (+82 against a
  scaled 144), and the documented default still passes. Mutation-tested by inflating the measured
  growth: `9996 > 16 (16 per settled interval x 1 observed)`, exit 1.

- ✅ **FIXED 2026-09-27.** The report now ships `SettledPerInterval` beside `SettledGrowth`:
  the per-interval series, a median, and an explicit confidence string. Below three intervals it says
  `n=1` outright rather than dressing one observation up as a summary statistic. The BOUND is
  deliberately unchanged -- changing a pass criterion on a metric this noisy, without data to set the
  new one, is the shape of the mistake this item came from.

  Both paths were exercised rather than assumed, and the long one made the case better than the
  original write-up did. Default 30 s: 1 interval, "treat as a HINT". A 150 s run: 8 intervals,
  median handles **2**, containing one interval at **+93** and another at **-9** -- all inside a
  single PASS. A reader handed only the end-to-end delta would have drawn a conclusion from that
  spike; the spread is now printed next to it.
### Checked and REFUTED — do not re-file

Recorded so the next audit does not spend the time again. AgentFlow's shell-header template does NOT
swallow the glob/grep rows: the real bundle string is "Allow this glob command" with no trailing
question mark, extracted from the installed extension's `webview/index.js`, so the `EndsWith`
alternative is false and the table row wins. `SavePaneValues` skipping only `about`/`hdr` prefixes is
belt-and-braces: the host registers no reader for Info or Header, so `Collect()` never sends them.
`RuleLoader.DefaultPaths` is not missing a managed tier — `~/.claude/remote-settings.json` IS it.
Fortunes does NOT pay a 34 MB SHA-256 per Apply; `Embedder.AssetFingerprint` is a `Lazy<string>` with
`ExecutionAndPublication`, so it is once per process. Module RELOAD leaks (tray items and options
panes cannot be unregistered; AiBrain's stale handler would throw off a disposed `_lifetime`) are real
against the ABI but latent: `ModuleHost.LoadFrom` is called once (`StartUp.cs:269`) and `ShutdownAll`
only from `Dispose`, so the shipped host has no runtime reload path. `FormCompanion.cs:2057` compares
a window title to lowercase "sheep" while the form's `Text` is "Sheep", so that disjunct can never be
true — but the first disjunct covers the same case and no behavioural difference exists.
PowerShell 5.1/7 divergence is clean as SOURCE: all 27 scripts parse under 5.1.26100, no
`-AsHashtable`, no `Get-WmiObject`, no `-Encoding Byte`, and the three scripts whose output genuinely
differs carry `#requires -Version 7`. No unguarded destructive operation exists in any of the 27;
every `Remove-Item -Recurse -Force` is GUID-scoped scratch or routed through the safe-delete helpers.

### One thread left open

- ✅ **ANSWERED 2026-09-28: NO, and the runtime makes it impossible rather than unlikely.**
  `BehaviourChain.cs` copying `Sequence.RepeatCount` verbatim is safe, because whatever the expression
  evaluates to is clamped on both live paths:

  | site | clamp |
  |---|---|
  | `Xml.cs:294`, at load | `AnimationRuntimeLimits.ClampRepeat(ani.Sequence.Repeat.Value)` |
  | `Animations.cs:505`, per-screen evaluation | `ClampRepeat(Sequence.Repeat.GetRawValue(screenIndex))` |

  `ClampRepeat` is `Math.Max(0, Math.Min(1000, value))`, so a negative result becomes 0 and an
  enormous one becomes 1000. `CalculateTotalSteps` then clamps again -- `Math.Max(1L, Math.Min(1000000, total))`
  -- so the derived step count can be neither zero nor negative nor unbounded.

  A chain therefore cannot stall on a repeat whatever the source XML says, and no change is needed.
  The question was worth asking: `RepeatCount` really is an evaluated expression rather than an
  integer, so the concern was well-founded and the answer is that the defence sits at the consumer
  rather than at the validator.
## Open: three read-only audits after v1.2.6 (filed 2026-09-28)

`src/`, `modules/` and `tools/`+CI, audited in parallel at `1976843`, each briefed to verify before
filing and to state empty categories as real results. 24 findings. **Nothing here is a regression
from the 1.2.6 campaign except where it says so** -- two entries are residue of fixes made that day
and are marked.

Three categories came back genuinely empty and are recorded so nobody re-spends the time: memory and
handle leaks across both `src/` and `modules/` (beyond the one static field in H6), flags assigned
and never read, and PowerShell 5.1-vs-7 runtime divergence. Each auditor also listed candidates it
killed during verification; those are in the transcript, not here.

**Verification status is stated per entry.** Items marked VERIFIED I re-checked against the source
myself before filing. The rest carry the auditor's cited evidence and have NOT been independently
re-checked -- treat the first step of acting on one as confirming it still reproduces.

### User-visible

- ✅ **FIXED 2026-09-28.** `keep = 1` means one file and it is the CURRENT one, so there is no
  archive slot to shift into and the current file has to be deleted outright; every `keep >= 2` got a
  fresh file as a side effect of the shift, and `keep == 1` had to be told. One line, plus the reason
  beside it.

  **Rotation now has coverage, which it had never had** -- the audit's own note was that grepping
  `RotateNoLock` across `src/`, `tests/` and `docs/` returned only the source itself. Four assertions
  in `--wpf-options-selftest`: the keep=1 current file is removed, the keep=2 current file is shifted
  into the archive slot, an archive past the keep count is dropped, and the surviving archive is the
  one just rotated out. The last two assert on CONTENT, so a rotation that moved the wrong file still
  fails.

  To make that testable the rotation became a function of the two values it depends on
  (`RotateIn(directory, keep)`) rather than of module statics, with `RotateNoLock()` delegating.
  That is the OPPOSITE of the `$TimeoutMs` shape filed elsewhere in this file: no unused parameter
  was added for a test, the parameters it already needed stopped being globals. It matters because
  these self-test flags run against the user's REAL data root -- there is no isolated
  `DESKTOP_AI_COMPANION_DATA_ROOT` around them -- so a rotation test that wrote 512 KB into the
  user's own log would be worse than no test. It writes to a scratch directory instead.

  MUTATION: removing the `keep <= 1` line fails exactly one assertion, *"at keep=1 the over-cap
  current file is removed, not left to grow"*. The baseline run was checked for the new labels
  first, because a clean pass proves nothing if the assertions never executed, and the built exe's
  timestamp was asserted to advance between legs.

  ⚠ **The real-app smoke then found a SECOND defect the suite could not, and it is the bigger
  one.** `Start()` rotates before `Configure` has ever run -- it has to, because the rotation must
  precede anything worth recording and the settings store is not loaded that early -- so it rotates
  with the FIELD DEFAULT of 2. Every launch therefore recreated `diagnostics.1.log` whatever the
  user had chosen, and at `keep = 1` that archive survived until the next cap-driven rotation, which
  on a quiet app can be hours away. **"Keep 1" never actually held across a launch.** `Configure`
  now drops the archives a lowered setting no longer allows, which needs no change to the launch
  order. Measured on the shipping binary with an isolated data root: 1 archive before, 0 after, live
  file intact.

  The suite could not have caught it: the suite tests the rotation, and this was about WHEN the
  rotation is asked for. Two more assertions cover the trim, including the property rotation does not
  have -- that it leaves the LIVE file alone, because lowering a setting is not a rotation. The
  WIRING is a source invariant scoped to `Configure`'s body, because the smoke is not in the gate;
  MUTATION: renaming the call site alone fails it while the method DECLARATION, which contains the
  same text, stays in the file (`declarationStillInFile=1`), so a file-wide grep would have passed.
  SMOKETEST.md 145 -> 147.

  ⚠ **The first version of that smoke test was worthless and printed a verdict anyway.** It
  round-tripped `settings.json` through `ConvertTo-Json`; the app silently discarded the result and
  came back with `keep=2 / cap=512`, the defaults. So it measured the default configuration twice and
  reported "NOT bounded" for a reason that had nothing to do with the code. It now edits in place by
  key and **refuses to report a verdict unless the edit survived the next launch** -- a smoke test
  that cannot tell "the setting did not apply" from "the code is broken" is worse than none. Same
  family as the `taskkill` masking closed yesterday: the check ran, and its answer was about
  something else.
- ✅ **FIXED 2026-09-28.** The reset calls `DiagnosticLog.Configure` with the values read back
  from the store, exactly as Save does and for the reason Save states. Read back from `data` rather
  than from `def` so the clamps in the setters are the single source of what the logger is told.
- ✅ **FIXED 2026-09-28 (remembrance 1.0.16). THREE of the four branches were loose, not one.**

  `NamesThisModuleWrites` decides what `File.Delete` -- not the recycle bin -- removes from a folder
  the user chose, on Init and then hourly. Its own doc already stated the rule: *"'snapshot of my
  cat.png' must not qualify. Only the exact stamp this module writes does."* Only one branch held
  that line.

  | branch | shipped | matched |
  |---|---|---|
  | in a capture folder | `StartsWith("snap") && EndsWith(".png")` | any `snap*.png` |
  | root | `EndsWith(".wav")` | **any** `.wav` |
  | root | `Contains(" - snap") && EndsWith(".png")` | `my holiday - snapshot.png` |
  | root | `IsRootSnapshotName` | the exact stamp -- correct |

  The `.wav` one is the widest thing this purge ever matched and was not in the audit: a user who
  pointed `storageLocation` at a folder holding their own audio lost every file in it past the
  retention window, permanently, with no prompt. An extension is not a shape.

  Every branch is now a PARSED shape built from the same strings `NewCapture` and `TakeSnapshot`
  write, with the timestamp format in one place:

  | | audio | snapshots |
  |---|---|---|
  | in a capture folder | `recording.wav` | `snap <stamp>.png` |
  | in the root | `<stamp>.wav`, `<meeting> - <stamp>.wav` | `snap <stamp>.png`, `<baseName> - snap <stamp>.png` |

  **Three of the old fixtures asserted TRUE for names nothing produces** -- `"snap 1.png"`,
  `"sprint review - snap 1.png"` and `"sprint review.wav"` -- because `"1"` is not a timestamp and a
  flat recording is always `<meeting> - <stamp>.wav`. That is what made three loose branches look
  tested. Positives are real names now, and the `insideCaptureFolder` branch has negatives for the
  first time: 6 positives and 9 negatives, up from 4 and 3.

  MUTATION, and each mutation is literally the code that shipped rather than a hypothetical, so this
  also answers per branch "would the new tests have caught it":

  | restored loose branch | failures | which |
  |---|---|---|
  | in-folder `snap*.png` | 2 | both new subfolder negatives |
  | root bare `.wav` | 3 | a user's own wav, a bad-stamp wav, a root `recording.wav` |
  | root `Contains(" - snap")` | 1 | `holiday - snapshot.png` |

  No cross-talk, baseline and restored both `RESULT=PASS`, and every leg asserted the rebuilt DLL's
  timestamp advanced and the marker postdated it.

  ⚠ **I hit the stale-marker defect filed in this same audit while doing this**, which is worth
  recording as a live sighting rather than a code reading: the first read of
  `dp-module-remembrance-selftest.txt` showed the OLD assertion labels against a freshly built DLL,
  and it took a timestamp comparison to notice. Every module self-test run in this closure therefore
  deletes the marker first, asserts it is gone, and asserts the new one postdates the DLL. The
  underlying fix to `tests/Test-ModuleSelfTests.ps1` is still open below.
- ✅ **FIXED 2026-09-28 (aibrain 1.1.13).** I re-measured rather than taking the numbers, and
  they reproduced exactly: across all 54 `Companions/*/animations.xml`, case-insensitively, happy
  18/54, excited 35/54, sad 8/54, thinking 8/54, confused 8/54. `boing` is on exactly one companion
  and `flower` on one.

  | emotion | shipped | reached | with the fallback |
  |---|---|---|---|
  | happy | flower, jump, boing | 18/54 | **42/54** |
  | excited | run, jump, boing | 35/54 | **42/54** |
  | sad | sleep1a, sleep2a | 8/54 | **39/54** |
  | thinking | sleep1a | 8/54 | **40/54** |
  | confused | rotate1a, boing | 8/54 | **43/54** |

  **The original names stay FIRST in every list.** That is the part worth stating: no companion that
  already reacted changes what it plays, so this adds a reaction for the 46 of 54 that silently did
  nothing rather than altering the ones that worked.

  **Nothing reaches all 54, and no list can.** The intersection across this corpus is EMPTY, and the
  only names present on 50+ pets are the engine's reserved lifecycle animations -- `fall` 53, `drag`
  52, `kill` 51, `sync` 50 -- so a list built for coverage alone would offer to play the dying
  animation. That is why the measurement is recorded on the method itself, the way
  `AgentFlow/PetAnimations.cs` records its own.

  Coverage cannot be asserted inside the module (it has no access to `Companions/`), so the self-test
  holds the two properties a future edit CAN break, and both are edits somebody would plausibly make
  while believing they were improving things:

  | mutation | caught by |
  |---|---|
  | reorder a list, e.g. sorting by corpus frequency | *"'happy' still leads with flower"* |
  | append a reserved lifecycle name for coverage | *"'confused' offers no reserved lifecycle animation"* |

  MUTATION: both fire, exactly one failure each, naming the right assertion; baseline and restored
  both `RESULT=PASS`. aibrain's assertion count 216 -> 233.
- ✅ **FIXED 2026-09-28 (reminder 1.0.6).** Re-measured: `boing,jump,run,flower` reaches **35 of
  54**, missing 19 -- `bbunny`, `blue_ham_ham`, `fox`, `mareep`, `mimiko`, `negima`, `neko`,
  `pikachu`, `pingus`, `pink_fox`, `pink_neko`, `shiny_sylveon`, `yellow_neko` and six converted
  shimeji. With the fallback tail: **43/54**. `reactOn` defaults to true, so those 19 users had a
  feature switched on that did nothing.

  The historical four stay first and IN ORDER, so no companion that already reacted changes what it
  plays. The old doc comment was half-right -- it said these are names the shipped pets define and
  that a converted shimeji uses different ones, which is the reason an ordered list exists, but the
  list never actually reached the converted ones.

  ⚠ **The three assertions that pinned the literal string are gone, replaced by properties.**
  `Count == 4`, `[0] == "boing"` and `[3] == "flower"` tested the string rather than anything about
  behaviour, which is exactly why the audit predicted that improving the default would redden the
  gate. What stands there now:

  | assertion | the edit it catches |
  |---|---|
  | the historical four still lead, in order | a reorder, e.g. sorting by corpus frequency |
  | there is a fallback beyond the historical four | reverting to the 35/54 list |
  | no reserved lifecycle animation is offered | appending `fall`/`kill`/`sync` for coverage |

  MUTATION: a reorder and an appended `fall` each fire exactly one failure, naming the right
  assertion; baseline and restored both `RESULT=PASS`.
- ✅ **FIXED 2026-09-28.** `data.SetDefaultSpeakingPet(def.DefaultSpeakingPet ?? "")`, next to
  the per-pet `triggerSpeech` exclusion that does have a stated reason.

  ⚠ **Both reset gaps are now guarded, and the guard's SCOPE is the whole point.** A file-wide
  grep for `DiagnosticLog.Configure(` is satisfied by the save path's call one screen away, so it
  would have passed against the shipped defect -- which is how this survived in the first place.
  `runtime-hardening-selftest.ps1` slices `ResetToDefaultSettings` first and asserts inside that
  body only. MUTATION: renaming each call inside the reset region alone fails the matching assertion
  while the save path's copy stays in the file, confirmed by the harness reporting
  `copiesLeftElsewhere=1` on both legs. Source restored byte-identically. SMOKETEST.md 142 -> 145.
### Checks that cannot fail, or fail for the wrong reason

- ✅ **FIXED 2026-09-28, and I hit it live rather than reading it.** The delete is
  `[System.IO.File]::Delete` and its RESULT IS CHECKED: a marker that survives now returns
  `STALEMARKER`, naming the path. `Remove-Item` performs `~` home-directory expansion even under
  `-LiteralPath`, and `-ErrorAction SilentlyContinue` swallowed that plus any lock or ACL failure --
  the sibling runner had already removed exactly this call for exactly this reason.

  MUTATION, reproducing the hazard directly instead of simulating a tilde path: hold an exclusive
  write lock on the marker with a stale `RESULT=PASS` inside it. The run is now reported as
  `STALEMARKER` and, the column that matters, **it is no longer graded** -- the harness confirmed the
  module did not appear as `OK`. Before the fix that stale PASS was the verdict.

  **The reasons now name the assertion.** The pattern was `'^\s*FAIL: '`, which matches only the
  host-level line *"FAIL: the module's own self-test passed"*; a module's own failures arrive as
  `  [<id>] FAIL: <assertion>` and were dropped, so the gate named the module and never the thing to
  fix. MUTATION: breaking a real Remembrance assertion now reports *"'<name> - snapshot.png' is NOT
  ours; the separator is part of the shape"* alongside the generic line. Safe for the expected-failure
  lists, which hold host-level reasons for two modules that have no `SelfTest` to emit module-level
  ones.

  ⚠ **I added a marker-age guard and then removed it, because nothing can reach it.** Once the
  delete is asserted the file is gone, and the only ways a run leaves no fresh marker are Finish's
  write throwing (no file at all, so `NOMARKER`), the create-then-rename failing (temp file only, so
  `NOMARKER`), or two runners racing on the same `%TEMP%` path -- where both files are RECENT, so an
  age test is blind to it anyway. Shipping it would have added a check no input can fail, which is
  the defect this section exists for. The diagnosis is kept as a comment at the site so the next
  person does not re-derive it.
  CLOSES-WHEN: grep-present tests/Test-ModuleSelfTests.ps1 "STALEMARKER"
- ✅ **FIXED 2026-09-28.** The step deletes the marker, asserts the delete, requires the marker
  to exist, requires `RESULT=PASS`, and **fails on a SKIP** -- which is what
  `templates/.../SampleModule.cs` already promised (*"Never SKIP silently -- the gate fails on a
  SKIP"*) and what `ModuleConventionSelfTest.Run` returning true on one had made false. It also
  reports the assertion count, so a template whose self-test shrinks to nothing is visible.

  MUTATION, using the exact latent input the audit named rather than a stand-in: replacing a
  `probe.Check` in the scaffolded `SelfTest` with `probe.Skip(...)` now fails with *"the scaffolded
  module's self-test SKIPPED"*. Baseline and restored both `TEMPLATE OK`. Deleted with
  `[IO.File]::Delete`, which does not perform `~` expansion; `Remove-Item` does.
- ✅ **FIXED 2026-09-28, structurally.** I inspected the DLL instead of guessing at a fix:
  `collections.json` is embedded VERBATIM as a resource, so the check now extracts that JSON, parses
  it, and compares collection -> sources against `packs/collections.json` exactly. A failed
  extraction THROWS rather than falling back to the old id-only search -- a check that quietly
  downgrades itself to a weaker one is the shape this section is full of.

  MUTATION, and the decisive leg is the one the old check provably could not see:

  | mutation | old check | new check |
  |---|---|---|
  | MOVE a pack between collections | passed | caught, names the collection and both source lists |
  | RENAME a collection | passed | caught, "absent from the shipped DLL" |
  | DROP a source | caught | caught |

  Baseline and restored both pass, and `packs/collections.json` was restored byte-identically.
- ✅ **FIXED 2026-09-28.** Wrapped in `if ($launchPublish.Count -eq 1)`, matching every other
  block in the file. Nothing is hidden by skipping them: the `Count` assertion above has already
  failed and recorded itself.

  MUTATION: pointing the `ControlEvent` query at a dialog that does not exist -- which is the
  condition the block exists to detect -- now reports the assertion as a FAIL instead of dying with
  *"Index was outside the bounds of the array"*. The harness also had to be corrected twice before it
  measured anything: a backtick inside a double-quoted PowerShell string is the ESCAPE character, so
  the MSI query anchor silently lost its quoting, and `-MsiPath` is `Mandatory`, so omitting it made
  the leg fail before reaching the guarded block -- which reads exactly like "the check said
  nothing".
- ✅ **FIXED 2026-09-28.** `throw`, matching the file's own other failure. `exit` from a child
  `.ps1` raises no terminating error, so the `try`/`catch` in both callers never fired and the gate
  continued green over a failed baseline -- and every refusal in that file proves nothing if the
  baseline did not land, which makes it the one failure that must not be silent.

  MUTATION: forcing the baseline branch to be taken now reaches the caller. The reachability caveat
  the audit stated still holds -- `Publish-DesktopAICompanionAtomicFile` either throws or lands the
  file -- so this was the failure CHANNEL rather than a live escape, and it cost one line.
- ✅ **FIXED 2026-09-28.** The grep verbs require a needle and say so through the same reporter
  as every other malformed criterion. The needle stays optional in the regex because `file-exists` and
  `file-absent` legitimately take none.

  MUTATION: appending `CLOSES-WHEN: grep-present <path>` with no needle to a copy of `BACKLOG.md` now
  reports *"uses 'grep-present' with no quoted needle"* instead of *"Cannot bind argument to
  parameter 'Pattern' because it is an empty string"*. Third variant of this family closed; the first
  two were mine.
### Correctness and dead code

- ✅ **FIXED 2026-09-28 (petstudio 1.1.13).** The `Closed` handler now calls
  `CleanupExtracted()` only when no import is in flight; during one it drops the reference WITHOUT
  deleting. Deliberately the safe side of an uncertainty I did not reproduce: not deleting costs a
  temp directory, deleting costs an opaque IO failure in the conversion the user is waiting on. The
  second-Import guard's own comment already recorded why this path exists -- *"It only became
  reachable when the conversion moved off the UI thread: a responsive window is one you can click
  again"* -- and a responsive window is also one you can CLOSE, which `PetStudioModule.Shutdown`
  does too, so app exit during an import arrived here as well.

  ⚠ **I wrote a comment claiming an orphan sweep that did not exist, then had to build it.** The
  first version said the leftover directory is *"exactly what the orphan sweep on the next Import
  looks for"* and named `ForgetOrphanedExtractions`. Neither existed. Left alone that would have been
  a leak introduced while fixing one: every close-during-import abandoning a
  `petstudio-shimeji-*` tree for ever. `SweepOrphanedExtractions` is real now, runs at the start of
  an Import, and carries three narrowings because it deletes directories: `%TEMP%` plus this module's
  exact prefix with a suffix that must parse as the 32-char GUID it generates; only trees older than
  6 hours, so a second instance mid-conversion is never robbed; best-effort per directory.

  VERIFIED against the REAL compiled method by reflection, not by reading it, and not by
  re-implementing the predicate -- which would only have tested my copy. Seven decoys, all correct:

  | decoy | expected | result |
  |---|---|---|
  | ours, stale | delete | deleted |
  | ours, fresh | keep | kept |
  | right prefix, suffix not a GUID | keep | kept |
  | right prefix, wrong length | keep | kept |
  | prefix with no suffix | keep | kept |
  | prefix not at the start | keep | kept |
  | unrelated folder | keep | kept |

  This verification is a one-off probe, not a gate step: PetStudio has no module-class `SelfTest`
  (its checks need a pet-XML fixture the host supplies), which is the recorded gap below.
- ✅ **FIXED 2026-09-28 (agentflow 1.4.11).** `ArgvPath` is copied across the thread boundary on
  the UI thread, beside `Enabled`, which is exactly the fix the tick's own comment describes having
  already made: *"Enabled was the one setting the worker still went and fetched for itself ... Every
  other value on this beat was already copied across the boundary; this one was missed because it
  hides behind two predicates instead of being named inline."* `ArgvPath` was the counter-example to
  "every other value", and it hid the same way -- behind a property, so the call site did not read as
  a settings access.

  The second site was `SetupStatusLine`, which is closed by the entry below: it no longer probes at
  all, and the background probe reads `ArgvPath` on the caller's thread before the `Task.Run`.
  `BrowseForArgvAsync` was checked and left synchronous on purpose -- it runs on the UI thread right
  after a modal picker the user just dismissed, its answer IS the return value, and `path` is a local
  from the picker rather than anything out of the settings dictionary.
- ✅ **FIXED 2026-09-28.** Deleted, and the behaviour was already correct: **the comment directly
  above the branch asks for exactly what the unreachable branch would have prevented.** It says
  *"NOTHING TO RENAME IS NOT NOTHING TO DO. The version records the standard a pet MEETS, not whether
  this run edited it ... Falling through stamps the rung and re-validates."* Always falling through is
  the documented intent, so the leftover bail contradicted its own paragraph and happened to be
  unreachable.

  Confirmed against the ladder rather than inferred: `NextFormatVersionAfter` short-circuits only for
  `ConvertedFormatVersion`, otherwise walks `FormatLadder`, whose `0.8` rung returns `1.0`. The gate 20
  lines above admits only `0.8`, so the equality was `"0.8" == "1.0"` for every pet the verb accepts.
  `reloop`'s identical guard stays, because its gate also admits `ConvertedFormatVersion` -- which is
  what made this a copy-paste divergence rather than a style choice.
- ✅ **FIXED 2026-09-28. One writer for all eight verbs.** `reloop` had it right; the other seven
  wrote `new UTF8Encoding(false)` unconditionally. Its logic is now
  `WritePetXmlPreservingEncoding`, which reads the file it is about to replace -- still the
  unmodified original at that point -- so no verb's read path had to change.

  **PROVEN, and it took three attempts because the first two were degenerate.** Worth recording, because
  both failures looked like results:

  | attempt | what it reported | what was actually true |
  |---|---|---|
  | 1 | *"33 files changed, 0 BOMs lost"* | **zero** files were written; the 33 was my regex matching skip-report TEXT |
  | 2 | `filesRewritten=0` for all seven verbs | correct, and it kills the approach: the corpus is fully migrated, so every verb is a no-op today |
  | 3 | BOM preserved / BOM STRIPPED | the property, on an input a verb admits |

  Attempt 3 constructs the input: take a BOM-bearing converted pet (`shimeji-06n2wuu6`), roll its
  `<version>` back to `0.8` -- the format `undirect` admits -- writing it back WITH its BOM, then run
  the verb. MUTATION, with the axis checked in both legs rather than assumed:

  | leg | verb wrote the file | BOM kept |
  |---|---|---|
  | helper in place | yes | **yes** |
  | `UTF8Encoding(hadBom)` -> `UTF8Encoding(false)` | yes | **no** |

  ⚠ **So the defect is LATENT, exactly as the audit said, and that is the reason to fix it now.**
  No current verb rewrites anything, so nothing is broken today; the hazard is the next migration rung
  somebody adds in the majority style, which would rewrite 18 blobs for no functional change and
  invalidate their `catalog.json` sha256 -- git normalises CRLF via `.gitattributes` but does not
  normalise a BOM.
- ✅ **FIXED 2026-09-28.** `AnimationSync = -1`, and the three `> 1` guards in `FormCompanion`
  became `> 0`. Ids are positive by validator rule and -1 is the "not declared" sentinel, so `> 1`
  excluded exactly one legal value in each case: a `sync` at id 1 (tray row hidden, `Sync()` a no-op)
  and a `kill` at id 1 (death animation skipped silently).

  `AnimationKill` already defaulted to -1, so its guard was wrong on its own terms -- with a -1
  sentinel, `> 1` rejects nothing except id 1, which is the only value it could ever wrongly exclude.
  That is what made `AnimationSync` the odd one out rather than the pattern.

  **Still latent, and measured rather than assumed:** across all 54 shipped companions none declares
  `sync` or `kill` at id 0 or 1, and the 44 that declare `sync` use ids 12, 13, 22, 23, 26, 32, 55,
  71, 77 and 78. Reachable only by a hand-authored or drag-and-dropped `animations.xml`, which is a
  shipped feature gated only by "unique positive ids". No repair in `ResolveMagicAnimations` is needed
  once the sentinel and the guards agree.
- ✅ **FIXED 2026-09-28 (aibrain 1.1.13).** Deleted, both the field and `int gen =
  ++_generation;`. Generation serialisation is owned by `AiSessionManager`, which is what the comment
  three lines below already pointed at. No CS0414 fires because the initialiser is not a constant --
  the exact compiler behaviour `docs/DESIGN-REGISTER.md` measured and warned about, which is why this
  was found by grep rather than by a clean warning build.
- ✅ **FIXED 2026-09-28.** All five. Not a use-after-dispose, as the entry said -- the only
  reader of `syncPetsMenuItem` hangs off the `ContextMenuStrip` disposed three lines earlier, and
  `RefreshSpeechMenuItem` touches only the speech pair -- but an asymmetry with no reason behind it,
  in a `Dispose` whose whole job is to leave nothing held.
### Optimisation, with the cost named

- ✅ **FIXED 2026-09-28 for the call sites that threw the work away.** `LocalPetIds` discards
  every `DisplayName` and still paid a `File.Exists`, a `StreamReader` and a 32768-char `ReadBlock`
  per installed companion -- roughly 54 opens and 1.7 MB of decoded reads over the shipped corpus, on
  the UI thread, on pane open, on every Check, after every download and after every uninstall.
  `EnumerateLocalIds()` skips the header read; four call sites use it.

  **No duration is claimed.** The saving is avoided I/O, which is a count; this repo's rule is that a
  timing measured in a warm loop has been wrong every time, twice with the conclusion inverted.

  Both entry points delegate to one `EnumerateFrom(bundledRoot, libraryRoot, readDisplayNames)`, so
  the id set is identical by construction rather than by my reading of it. Writing a second traversal
  would have been the easy way to make the two answers drift.

  ⚠ **The first version of the test for that could not fail, and I only found out by mutating
  it.** It compared the two enumerations on the INSTALLED corpus -- and there is no `companions/`
  directory beside the exe on a dev build or in CI, because the packaging step creates it, so both
  returned zero ids and agreed vacuously. Pointing the fast path at a deliberately divergent
  traversal still reported PASS. It now runs on a synthetic corpus, which is also the only way to
  cover an id present in BOTH roots -- the precedence case this entry flagged, which never occurs in
  the shipped corpus.

  Three mutations, each caught by at least one check:

  | mutation | caught by |
  |---|---|
  | the ids path skips the bundled root | the source invariant (the synthetic test passes its own roots, so it is blind to this) |
  | traversal order flipped | `--catalog-selftest`: 3 pets -> 2 |
  | `seen`-dedup dropped | `--catalog-selftest`: 3 pets -> 4 |

  A fourth mutation was invalid and is recorded as such: `if (false) continue;` is rejected by the
  compiler (CS0162, warnings as errors), so it never tested anything.

  **The precedence inconsistency itself is REAL and is now pinned rather than fixed.**
  `AddFrom` walks bundled-then-library with a `seen` set, so the BUNDLED copy wins a duplicated id,
  while `DisplayNameForId` resolves library-then-bundled. The self-test asserts the bundled winner so
  the behaviour cannot drift silently, and the disagreement is documented on `EnumerateFrom` as the
  reason the header-name cache is NOT shared between the two. Sharing it without reconciling them
  would change `EnumerateLocal`'s answer, which is what this entry warned about.

  SMOKETEST.md 147 -> 149.
- ✅ **FIXED 2026-09-28 (agentflow 1.4.11).** `SetupStatusLine` reports what it knows and asks
  for the answer in the background (`BeginSetupProbe`, single-flight via `Interlocked`, no `Wait`);
  the next pane build shows it, which is what an Off-mode user got anyway once the tick had run.
  Same shape as `AiBrainModule.BeginVramProbe`, and deliberately NOT the shape Remembrance's model
  probe had -- a `Task.Run` followed by a `Wait` moves the call and blocks the caller anyway.

  Its doc claimed the cold path was *"only in the window between Init and the first tick
  completing"*, which is false in Off mode: `OnTick` returns before the probe when not `Enabled`, and
  Off is the default for a new user. The module's own measurement of what that cost: **30.1-30.7 ms
  with the port listening, 273.3-284.6 ms with it closed.**

  Both properties are guarded by source invariants SCOPED to their methods, which is the whole point:
  `VsCodeSetup.Inspect` is called from three places and `ArgvPath` from several, so a file-wide grep
  passes against either mutation. MUTATION confirmed exactly that -- reverting each one leaves **7**
  and **10** other occurrences of the same text in the file respectively, and only the scoped check
  fails. Baseline and restored both pass; agentflow's own 479 module assertions are unaffected.
  SMOKETEST.md 153 -> 159.
### Residue of the 1.2.6 campaign itself

- ✅ **FIXED 2026-09-28.** Removed, with the reasoning left at the site: `LoadThumb` owns the
  cache, has already taken it and MISSED on this key before calling down, and both run on the WPF UI
  thread only -- so the inner lookup could never hit and the inner store was immediately overwritten
  with the same reference. Residue of that day's fix, and it contradicted the doc written for it
  (*"CACHED HERE, not one level down"*).
- ✅ **FIXED 2026-09-28 by writing the test, not by deleting the parameter** -- the comment's
  argument was right, and the path had still never executed. `Test-ContentCatalogIntegrity.ps1` calls
  `Get-CatalogAsset ... 1`, and one millisecond provokes it because `git cat-file` cannot finish that
  fast. The kill and the refusal both ran for the first time, and the run prints *"the catalog-asset
  timeout path refuses rather than falling back"*.

  Asserted on the MESSAGE, with a negative control: a wrong path or an unreadable repo also throws
  and would have made this look like a pass, so the same asset is also read at the default timeout
  and must succeed.

  ⚠ **My first detector reported the correctly-firing path as broken.** It matched
  `timed out|timeout`; the real message is *"git cat-file did not return within 1ms ... Refusing to
  fall back to the working-tree bytes"*. It matches the REFUSAL now, which is the load-bearing half
  -- the whole point of the path is that it does not silently hash the worktree instead of what
  raw.githubusercontent serves.
- ✅ **FIXED 2026-09-28.** Both now describe petstudio and testmodule, and both say the
  script's own lists are the authority rather than the prose. `Test-ModuleSelfTests.ps1` also records
  WHY each is uncovered -- petstudio's `BehaviourChainSelfCheck.RunChecks` needs a pet XML fixture the
  host supplies from its own resources, testmodule is a deliberately minimal ABI fixture -- so the
  next reader does not have to guess whether a gap is expected.
### Doc rot the repo does not assert

- ✅ **FIXED 2026-09-28, and ASSERTED rather than just corrected.** Both said 31 against a
  measured 32. Correcting a number nobody re-measures only resets the clock, which is exactly why the
  Readme project count and the self-test count are assertions -- so `runtime-hardening-selftest.ps1`
  now counts the companions whose author is `Converted from a Shimeji skin` and requires both
  `MAPPING.md` and `Program.cs` to agree with the corpus.

  MUTATION: changing `MAPPING.md` to 33 fails with *"it says 33, there are 32"*, naming both numbers.
  SMOKETEST.md 149 -> 153.

  The "future date" half needed nothing: `MAPPING.md` dated a change to 2026-09-28, which was one day
  ahead when the audit read it and is today's date now.
### Recorded, deliberately not filed as defects

- ✅ **`release.yml` leaves the signing PFX on disk if the import throws** -- and the scrub step
  then exits early, because it keys on a thumbprint output that was never written. The step's own
  rationale is *"A hosted runner is torn down anyway; a self-hosted one is not, and this is the
  difference between the two"*, and a private key on disk is precisely what a self-hosted runner
  retains. **Not actionable here and recorded rather than filed:** this repo's release workflow runs
  on GitHub-hosted runners, where the runner is destroyed after the job, and no signing certificate
  is configured -- `build.yml` runs the build on every PR with no certificate at all. It becomes
  real the moment either of those changes. `runtime-hardening-selftest.ps1` asserts only that
  `if: always()` sits on the step, so the assertion passes while the property it names does not hold
  on the failure path.

- ✅ **`FormCompanion.Play()` does not set `hwndFullscreenWindow` on the blocked-monitor path**,
  which is what the new `IsFullscreenBlocked` accessor reads. The auditor could not turn it into a
  user-visible symptom -- the pet is `Visible = false` there so it cannot paint or be clicked,
  `Play`'s own scan feeds `NoteFullscreenScan` so the next tick's 300 ms-cached answer agrees, and
  animation intervals are far below 300 ms. Recorded because it sits directly on top of that day's
  change, not because it is a bug.
