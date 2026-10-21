# AgentBroker state core

This library provides an exclusive owner lease, volatile exact-incarnation session registrations, paired
peer records, and an atomic durable message journal. It opens no network listener, discovers no machine,
authenticates no transport, wakes no agent, and completes no agent work.

The caller supplies the authenticated broker identity after checking the transport and its pairing
policy. Local registration and peer authorization are privileged local-control operations. A directory
listing is not authorization, and remote readiness/availability still belongs to the federation adapter.

`Accept` journals admission, not delivery. `BeginDelivery` durably claims one message before its external
submission and prevents a second in-flight claim for the same exact recipient. `CompleteDelivery` records
the caller's receipt. Transport writes remain `Submitted`; `RecordRecipientReceipt` authenticates an
exact recipient acknowledgement separately. `Delivered` still does not prove execution, response
completion, or appearance in a vendor conversation UI.

Failed durable writes leave both the prior file and published in-memory state unchanged. Compatible state
is not rewritten merely by opening it. Malformed, missing-required-field, unknown-field, incompatible, or
structurally invalid state is refused without replacing its bytes; recovery requires an explicit repair.
Unknown fields are refused so an older build cannot silently erase state written by a newer schema.

A process restart changes unresolved `Submitting`/`Submitted` entries to `Uncertain`. Revoking a peer refuses its
queued/held messages, and marks an already submitting message `Uncertain`: revocation cannot recall a
request that may already have reached an adapter. Late completion for that revoked claim is refused.
Explicit recipient receipts can reconcile uncertainty; ambiguous native input is never blindly replayed.
`ReleaseUnsubmittedClaim` is reserved for an adapter's authoritative proof that no model input was
submitted, including the exact typed native busy guard. It restores the same accepted message/UUID and
unconsumed submission attempt, or expires it when its deadline passed. Generic failures/timeouts must
complete `Uncertain`. Callers must serialize claim/submission/release per message and back off deferred
claims; this API does not provide a native reservation or an automatic retry loop.

Expired queued/held messages cannot start. An authenticated duplicate retains the original receipt after
session disconnection or expiry. Durable receipts are retained until 24 hours after message expiry, then
removed during journal maintenance. Deduplication is bounded by that retention, not a lifetime guarantee.
In-flight and uncertain records count toward per-recipient queue bounds. Session leases, current receive
capability, privacy changes, idle state, and wake capability are rechecked before a local claim. Unknown
activity permits a claim only with explicit `AtomicIdleAdmission` plus receive/wake capabilities; the
adapter must then enforce an authoritative native idle guard. Known busy/approval/interrupted states
remain queued. New
session incarnations supersede only the same exact session ID; queued messages are never redirected to a
replacement incarnation by matching a title or project path.

Storage currently uses a complete JSON snapshot for each actual durable transition, so write work is
linear in retained journal size. Default bounds permit 4,096 messages with 16 KiB of UTF-8 text each,
64 MiB of raw text before metadata/JSON escaping, and escaping can make the file substantially larger.
Idle delivery polls do not write snapshots. An append journal or another transactional store, retention
tuning, and measured throughput limits remain production work; these tests do not establish capacity
or latency for a busy workstation federation.

The test harness is package-free and uses only synthetic records and directories beneath its build
output. From the repository root:

```powershell
dotnet build tests\AgentBroker.Core.Tests\AgentBroker.Core.Tests.csproj -c Release --nologo -v:minimal
.\tests\AgentBroker.Core.Tests\bin\Release\net10.0\AgentBroker.Core.Tests.exe
```
