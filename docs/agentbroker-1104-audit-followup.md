# AgentBroker 1.10.4 audit follow-up

Qualification record, 2026-11-11: the seven findings and their focused regression controls are FIXED. Source/commit bindings, the exact-range privacy check and publication have separate receipts against the final commit; this file records the engineering qualification.

The published 1.10.4 release at `b234fcd0bda619fb34b4de438d12bc6ac164829d` passed installed acceptance, exact-head CI and its source/payload/privacy checks. Claude's post-push audit, receipt `B86143B82B38D2A46180127DEC7911797970BDC92E9F9239176A802D16BE7C9B`, found seven low-priority test-coverage or documentation gaps. None identified a failure in the shipped product. This follow-up adds permanent regression controls and corrects the records without changing the accepted module package.

The accepted ZIP remains `FB1D0BE95579AF9589C9D9D7019A51DF03914A9E449C6B582D0237911006A747`, 51,508,278 bytes and 357 entries. Its compiled source-content identity remains `1E3404795034D2126832A6042ED82F26F5802BCD6316C393F16AE2E537693757`, parent `50d666ad0e354883e1d59486593da2fa9aba0487`. The earlier release receipts and four original engineering dispositions remain historical evidence; new follow-up results must be recorded separately.

## Finding inventory

| Audit ID | Confirmed gap | Follow-up scope | Disposition |
| --- | --- | --- | --- |
| PPA-1 | The committed pack-skip mutation targets the removed single-line workflow form and becomes a named NO-OP. | Repaired the in-block anchor and added a committed order mutation; both fail at the build RA-190 assertion. | FIXED |
| PPA-2 | Four named clock/readiness controls live only in the private qualification archive; committed checks do not preserve that regression coverage. | Added runnable clock boundary, closed publication, transient retry and persistent classification checks; all four deliberate defects fail by name. The original private evidence is identified separately. | FIXED |
| PPA-3 | The pack script's output-privacy call and per-package PDB/image read lack committed wiring guards. | Required unconditional, correctly bound calls before the push banner; seven omission, conditional, late and wrong-binding variants fail at the matching wiring assertions. | FIXED |
| PPA-4 | Freshness main decisions and provenance API refusals lack committed killing controls. | Exercised the actual decision and API paths with owned source/output/ZIP fixtures; seven main-decision bypasses and four API bypasses fail at their intended assertions. | FIXED |
| PPA-5 | The workflow invariant accepts step-level failure suppression on package production or upload. | Rejected failure suppression in the relevant build and release steps; the three committed controls fail at the matching RA-190 assertions. | FIXED |
| PPA-6 | Two open backlog entries describe documentation subparts that the release already completed. | Appended dated completion notes while keeping the unresolved incident/contract work open. | FIXED |
| PPA-7 | A retained inventory paragraph points below to an item created and closed before its first commit. | Appended a dated pointer to its permanent disposition record. | FIXED |

`N-broker-1104-control-gaps-01` accounts for PPA-1 through PPA-5 together and has disposition FIXED, with the code and named killing controls below. PPA-6 and PPA-7 are dated corrections to existing records, with no deletion of the old text. The four original engineering dispositions are retained; this record closes their newly discovered regression-coverage gaps separately.

## Validation record

The scope is the light tier: focused smoke tests, affected regressions and filtered killing controls. No whole mutation catalogue or leak soak is part of this follow-up, and no earlier private archive is relabeled as a committed check.

| Area | Measured validation and killing controls |
| --- | --- |
| Workflow controls, PPA-1/PPA-5 | The actual public `mutate-hardening-guards.py --only=NuGet` selection is 5/5 FIRED. Skipped pack, reversed block and build pack/upload suppression each fail at `build.yml packs the module-author NuGet packages on every push and uploads them with the ZIP and MSI (RA-190)`. Release pack suppression fails at `release.yml propagates the module-author NuGet pack failure (RA-190)`. The existing documented-count control also fires. Restored source invariants pass under PowerShell 5.1 and 7, with 1,098 emitted PASS lines and 574 measured assertion sites. Nineteen focused workflow contracts pass under each shell. |
| Readiness, PPA-2 | The positive runner passes three checks; wait lifecycle remains 28 product assertions, and the real collaboration process regression passes 81 checks. `clock-early`, `ready-before-close`, `retry-omitted` and `diagnostic-omitted` fail respectively at `QUIET_CLOCK_NOT_BEFORE_DUE`, `READY_NOT_VISIBLE_UNTIL_CLOSED`, `READY_TRANSIENT_EPERM_RECOVERS` and `READY_PERSISTENT_CONSUMER_DIAGNOSTIC`. Healthy/restored runs pass. The clock mutant is rebuilt, its changed loaded DLL is witnessed, and the restored DLL matches the healthy DLL. |
| NuGet wiring, PPA-3 | Forty-four checks pass under each shell. Seven external producer variants fail at `NUGET_PRIVACY_RULES_OUTPUT_PRIVACY_WIRED_BEFORE_PUSH_BANNER` or `NUGET_PRIVACY_RULES_PACKAGE_PRIVACY_READ_WIRED_BEFORE_PUSH_BANNER`. This checks the actual call sites and fresh-image binding, rather than the presence of a statement. |
| Freshness and provenance, PPA-4 | Sixty-four freshness checks and 19 provenance checks pass under each shell. Removed throws, false wrappers and an always-true payload exemption fail at the corresponding `G4_MAIN_*_REFUSES` or `G4_MAIN_*_WIRED` assertion. Actual API bypasses fail at `PROVENANCE_RULES_SOURCE_CHANGED`, `PROVENANCE_RULES_OUTPUT_CHANGED`, `PROVENANCE_RULES_ZIP_EXTRA_ENTRY` and `PROVENANCE_RULES_TOOL_AUTHORITY_CHANGED`. All 24 focused calls have the exact expected native exit and terminal label; all 13 pinned original inputs remain unchanged. |
| Shared dispatch | Both-shell `HealthyOnly` runs pass. A clearly labeled derivative keeps all registry, stub and execution code, varying only the old rows' selection flags so the two new routes receive their failure and missing-summary cases. It passes the healthy case and four negative scenarios. Three captures have native 0 and exact 1/1/5 summaries; this is not a full historical dispatch-catalogue run. |
| Dated records, PPA-6/PPA-7 | Every original BACKLOG and validation-record line remains in order, all eight ORIGINAL/FIXED blocks are unchanged, and the old 38-ID ledger is unchanged. The baseline and annotated backlog each retain 356 open items. The two unresolved entries stay open; the corrected inventory pointer resolves to its retained original body and FIXED evidence. |

Private audit receipts: workflow qualification `A4952FA88F528DE47DFC499EC4C15F05F93D770DE8EA5534C2F223E4329D1A20`; readiness mutation qualification `9D2F3AC173B871B8EAEA143FB1974707ABEBE803058E4DB31CAE9FD1D08363D2`; provenance qualification `C496CFF2BF8F4C07FDAFB7E17188C57CB166FF712A1C7997A93C07AEAE1AF19E`; shared-dispatch qualification `65E6937B6AA7749D8E41AFA5B3F4AE33EC93A81D73A522FF4A19B524F64B1F55`.

Independent source/evidence reviews found no confirmed actionable issue in the bounded readiness and provenance reviews: `D5AA2CF54F1A25A648B2852436CEE14A7FFC4157F9D743EEE1C05258A76BA4AE` and `A573E8F9B0F5DB5B6D3F22E3771AC29E8629CB178F5F995F673DB59F2FE5446A`. The readiness review reconciles the retained captures and rebuilt DLL proof; the provenance review is static, separate from its measured captures. The final integration/source/commit bindings remain separate publication checks.

The committed readiness and provenance positives are registered in the shared offline runner used by both the local gate and hosted build. The manual readiness mutation verifier is plan-only by default and requires an isolated, already built test checkout for execution. Its inner captures are decoded UTF-8; the private outer captures retain stream bytes. The provenance API fixture uses the fixed public `b234fcd0` source and accepted payload with the current APIs, requires that Git baseline locally, and makes no SDK-build, installer or compression-route claim.

The first workflow qualification stopped at a sandbox Git user-ignore ACL error before any mutation; the normal host retry passed without weakening the gate. That stopped baseline is retained separately and is not a graded mutation result.

The native lease-gap incident and the generic standby result-contract item remain open with their existing acceptance limits. The healthy lease sample does not establish the historical cause or continuous availability. Other project backlog entries and the old 38-ID campaign ledger remain unchanged.
