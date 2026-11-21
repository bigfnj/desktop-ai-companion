# AgentBroker defensive claim contracts

| Finding | Disposition | Evidence |
| --- | --- | --- |
| N-broker-postpush-claim-01 | FIXED | Mandatory atomic admission/attempt guards, captured snapshot identity and ordered observation/backoff publication are implemented. Ordinary claim contracts 22 and three pure guard controls passed with exact restoration. Final combined regression with 3178 assertions, the actual canonical loader's 385 checks and installed 1.9.0 acceptance passed. The retired timing reproduction remains unexecuted; this disposition relies on the defensive contracts and ordinary regressions described below. |

Scope: `N-broker-postpush-claim-01`, supplementing the core audit at published baseline `7ab40743b5b40f525921601ac0bc59da56dd73b8`. This is a separate maintenance record. The original timing-scenario scaffold was never executed, was removed from compilation, and remains private and retired. No timing replay or vulnerable-baseline proof is claimed here.

Delivery completion, proven-unsubmitted release and peer reconciliation now require the identity carried by their captured local snapshots. [MessageReceipt](../../src/AgentBroker.Core/BrokerModels.cs) has internal, nonserialized admission and attempt stamps. Construction/loading creates local admission identity; record copies preserve it, and each claim receives a fresh attempt nonce. Explicit equality/hash retain all five original value fields. Durable and federation JSON property sets are unchanged.

[BrokerStore](../../src/AgentBroker.Core/BrokerStore.cs) compares captured stamps and required state under its lock before mutation. Production APIs have no UUID-only completion, release or peer-receipt overload. An unmatched Try operation returns false without a receipt write. Tombstone conversion/rendering preserve admission identity. Fixtures pass stamped snapshots; the frozen rollback implementation remains its historical compatibility fixture.

[DeferredDeliveryClaims](../../tools/AgentBroker.Worker/DeferredDeliveryClaims.cs) serializes selection with successful settlement and immediate observation publication, without holding that gate over awaited adapter IO. Store transitions finish before observation locking; observations never enter the store. Deferral bookkeeping holds one admission/deadline per UUID and changes only after successful guarded release. Exclusion checks compare admission identity under the store lock. [Runtime](../../tools/AgentBroker.Worker/BrokerRuntime.cs) uses opaque handles, captures peer receipt identity before its awaited response, and supplies each captured receipt's own stamp to busy/engine reads. Exact addresses, routing, admission policy, wire protocol and persistent schema retain their existing behavior.

An ordinary Network run exposed a separate failure path supporting `N-audit-1106-320`: sandbox identity creation failed after the store opened, and Node.Create leaked store ownership. The [fixture](../../tests/AgentBroker.Network.Tests/Program.cs) now disposes partial resources, shares repeated disposal, attempts all resources after one cleanup error, and preserves primary failure attribution. [Ownership contracts](../../tests/AgentBroker.Network.Tests/NodeOwnershipContractChecks.cs) inject a loader exception without a key or listener, reopen the same store, and verify removal of the exact owned tree.

| Validation | Executed evidence |
| --- | --- |
| Ordinary claim contracts | `--claim-contract-only`: 22 assertions passed, covering component stamps, five-field JSON/equality/hash, copies, current lifecycle, tombstones, captured observations and publication ordering. Its full-suite registration sits after the existing-tests-only cut. |
| Existing Core regression | 408 assertions passed, child exit0. |
| Existing Worker regression | 1426 assertions passed, host child exit0. This harness contained19 contract assertions; the later3 pure assertions and Core hash witness passed separately. Production code is unchanged between these runs. |
| Fixture smoke and Network regression | Ownership4 passed without CNG/network; ordinary host Network244 then passed with child exit0 and fixture cleanup. |
| Pure admission qualification | Removing only the admission comparison fails `CLAIM_ADMISSION_PREDICATE`. |
| Pure attempt qualification | Removing only the attempt comparison fails `CLAIM_NONCE_PREDICATE`. |
| Pure JSON qualification | Exposing the internal admission stamp fails `CLAIM_JSON_AND_EQUALITY`. |
| Qualification integrity | All3 require rebuilt/newer Core artifacts, matching executed-copy hash and witness, one exact named failure, exact source restoration and restored contract22. Failing component checks run before any store workflow; no timing scenario, live/peer workflow or broader harness executes under altered code. |
| Independent source review | Stamp propagation, equality/JSON, atomic guards, captured views, reconciliation and lock order reviewed. One deferral-map ambiguity was corrected to one entry per UUID. Separate fixture cleanup review found no remaining sequencing issue. |

The failed sandbox Network receipt remains failed evidence. Its exact owned process was checked by path, parent and creation time, stopped and verified absent. Its exact remaining tree was checked for containment/linked paths, removed and checked absent. A blocked rebuild is retained as failed. A nullable test annotation failure, one accidental older-harness run after that build failed, a cleanup-script parse error and the first PS5.1 helper run without an exit-code receipt are retained separately and do not count as final passing evidence. The private runner now retains the owned process handle, records identity/exit metadata and enforces a finite bound.

The original frozen40 snapshot and34 controls remain unchanged and retain their original qualification. This record adds defensive implementation and ordinary contract/regression evidence, not an executed proof of the retired scenario. Final combined regression, installed-app acceptance, publication and backlog disposition belong to the integration campaign. No tools were installed; no scripts-utilities catalog addition is recommended.
