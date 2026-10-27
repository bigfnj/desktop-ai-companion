# Native worker adapters

Both wrappers implement `ILocalAgentAdapter`, retain exact recipient incarnations, and accept injectable
providers. The deterministic harness never instantiates production providers, opens a vendor pipe,
accesses live registry metadata, or invokes a credential reader.

Codex directory observations resolve the exact owner on a persistent version-pinned native connection.
Broker incarnation hashes the native server PID/creation time, owner client ID and conversation ID;
the native adapter's ephemeral connection-binding UUID is never used as durable identity. Typed native
busy admission alone yields `DeferredNotSubmitted`. Generic failures after entering model submission
remain uncertain. Native acceptance remains `Submitted` until a separate exact recipient receipt.

Codex delivery needs the `codexPeerMessages` opt-in. In strict mode, the default, `CodexNativeProvider.Authorize`
admits a binding only when its router matched an exact reviewed image and every running Code-owned engine matched
its exact extension profile. A router that fails its exact pin but passes Windows-verified publisher trust is
observed read-only: its rows carry no capabilities, and submission refuses `codex_observation_only` before owner
rediscovery. The separate `codexUpdateTolerantDelivery` opt-in lets such a build deliver only when every runtime
check in the compatibility document passes; otherwise the refusal is `unsupported_capability`. The adapter also
checks each binding's own router trust, so a provider verdict cannot lend the exact path to a publisher-signed
router. Native owner discovery exposes opaque client IDs rather than trusted Desktop/VS Code host-version labels.
Installed profile verification alone never enables wake.

Claude production discovery examines at most 32 processes named `claude`, public executable identities,
and exact default-home numeric registry JSON files. It never lists credentials or follows log paths.
An exclusive `procStart` or `procStartFt` numeric hint must match the actual OS creation FILETIME exactly;
no .NET-tick epoch conversion/tolerance is allowed. Status is Unknown when the publisher's `updatedAt`
is missing, future, or older than 2 minutes. Public image hash cache is bounded to 32 entries and scoped to native
PID/start/path; the native connected-server identity is independently rehashed before any credential read.

Claude credentials default disabled. Only the explicitly enabled Submit method constructs the native
credential source. Before that boundary, the wrapper immediately refreshes the exact numeric record,
matches the recipient's native incarnation, and requires idle. Busy/unknown/changed-owner metadata yields
`DeferredNotSubmitted` before credential handling. Actual native peer classification and admission decide
current mode/settings; no sender mode, self-sent privilege, child token or permission change is forged.
The platform's exact H05/W06 approval is still required before an agent executes any real key read.

Both wrappers pass the shared external-peer provenance/routing envelope, retaining stable message UUIDs.
No content is truncated. Codex refuses formatted text above 32 KiB and preserves the native 64 KiB frame
preflight, with deterministic size refusal before model input. Claude allows 128 KiB formatted UTF8 text,
within the inspected native 1 MiB JSONL line guard. Core raw peer text remains 16 KiB. These per-adapter
limits can refuse a control-heavy message accepted into the broker journal, with an explicit receipt.

`DeferredNotSubmitted` is safe to requeue only when no model input occurred or the exact typed native
guard proved that fact. The runtime backs off excluded message IDs; submitted/uncertain messages never
enter that release path. Native receipts do not establish GUI visibility or completed model work.
