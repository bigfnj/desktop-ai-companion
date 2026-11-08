# Native worker adapters

Both wrappers implement `ILocalAgentAdapter`, retain exact recipient incarnations, and accept injectable
providers. The deterministic harness never instantiates production providers, opens a vendor pipe,
accesses live registry metadata, or invokes a credential reader.

Codex directory observations resolve the exact owner on a persistent version-pinned native connection.
Broker incarnation hashes the native server PID/creation time and conversation ID (`codex-thread-v2`; the owner client ID left it on 2026-10-29, so a view taking ownership keeps the address while each submission still targets the owner rediscovered for that attempt);
the native adapter's ephemeral connection-binding UUID is never used as durable identity. Typed native
busy admission alone yields `DeferredNotSubmitted`. Generic failures after entering model submission
remain uncertain. Native acceptance remains `Submitted` until a separate exact recipient receipt.

Codex delivery needs the `codexPeerMessages` opt-in. In strict mode (the worker's own default, and the module's when
"Also wake updated Codex builds" is off), `CodexNativeProvider.Authorize`
admits a binding only when its router matched an exact reviewed image and every running Code-owned engine matched
its exact extension profile. A router that fails its exact pin but passes Windows-verified publisher trust is
observed read-only: its rows carry no capabilities, and submission refuses `codex_observation_only` before owner
rediscovery. The separate `codexUpdateTolerantDelivery` switch (the module's default since 1.5.0; a worker launched
without `--codex-update-tolerant-delivery` stays strict) lets such a build deliver only when every runtime
check in the compatibility document passes; otherwise the refusal is `unsupported_capability`. The adapter also
checks each binding's own router trust, so a provider verdict cannot lend the exact path to a publisher-signed
router. Native owner discovery exposes opaque client IDs rather than trusted Desktop/VS Code host-version labels.
Installed profile verification alone never enables wake. A discovery pass reuses a prior publisher-verification result while the engine processes' metadata is unchanged (image path and OS creation time of each engine and its Code.exe parent; `DiscoveryReuse`). This does not prove that the file now at the path is the image a running process mapped (the publisher check verifies the on-disk path under a held handle, a gap that predates the reuse); fresh verification at submission remains the input boundary. The exact-profile check is never reused, because it also covers `package.json` and unsigned extension JavaScript that can change while the engine runs. A failure or an empty set is never reused, every submission still verifies fresh, and a fresh refusal at submission forgets the reused success.

Claude production discovery inspects at most 128 processes named `claude` and returns at most 32 usable
candidates, checking public executable identities and exact default-home numeric registry JSON files.
Non-session processes do not consume the usable-candidate bound; a capped census is reported as incomplete
with a bounded diagnostic. It never lists credentials or follows log paths.
An exclusive `procStart` or `procStartFt` numeric hint must match the actual OS creation FILETIME exactly;
no .NET-tick epoch conversion/tolerance is allowed. Missing or implausibly future activity timestamps leave
status Unknown. A long-idle timestamp remains valid when a fresh record and process identity still agree;
the publisher's timestamp is a state transition, not a heartbeat. The exact-profile cache is bounded to 32
PID/start/path entries. Unpinned public-image samples are held once per exact path within one discovery pass,
then disposed; each next pass and independent refresh verifies again. The native connected-server image is
independently rehashed and publisher-checked before any credential read.
Claude delivery needs `claudePeerCredentials`. With `claudeUpdateTolerantDelivery` on (the module's default since the
owner's decision of 2026-11-02; a worker launched without `--claude-update-tolerant-delivery` stays strict), a session
whose image fails its exact pin but is Windows-verified as "Anthropic, PBC" is discovered as `UpdateTolerant` and gets
the pinned capabilities. Each send refreshes it (`claude_image_changed` when the hash moved), and `NativePeerAdapter`
checks the publisher again on the connected server's own image, bound to that hash, before any credential read.

Claude credentials default disabled. Only the explicitly enabled Submit method constructs the native
credential source. Before that boundary, the wrapper immediately refreshes the exact numeric record,
matches the recipient's native incarnation, and requires idle. Busy/unknown/changed-owner metadata yields
`DeferredNotSubmitted` before credential handling. Actual native peer classification and admission decide
current mode/settings; no sender mode, self-sent privilege, child token or permission change is forged.
The platform's exact H05/W06 approval is still required before an agent executes any real key read.

Both wrappers pass the shared external-peer provenance/routing envelope, retaining stable message UUIDs.
No content is truncated. Codex refuses formatted text above 32 KiB and preflights the complete encoded request
against its existing 64 KiB frame cap before owner rediscovery or receipt reservation. Both full provenance
copies count, and required JSON escaping means some inputs below the broker's 16 KiB raw-text limit still
exceed this native budget. Such input ends Refused/native_message_too_large; it is never sent partially or
treated as an ambiguous transport result. Claude allows 128 KiB formatted UTF8 text,
within the inspected native 1 MiB JSONL line guard. Core raw peer text remains 16 KiB. These per-adapter
limits can refuse a control-heavy message accepted into the broker journal, with an explicit receipt.

`DeferredNotSubmitted` is safe to requeue only when no model input occurred or the exact typed native
guard proved that fact. The runtime backs off excluded message IDs; submitted/uncertain messages never
enter that release path. Native receipts do not establish GUI visibility or completed model work.
