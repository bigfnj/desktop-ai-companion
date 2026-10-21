# AgentBroker implementation and validation

AgentBroker adds local MCP messaging and paired LAN brokers through a separate, self-contained worker. Native GUI delivery is explicitly enabled and restricted to tested client versions. The evidence below distinguishes automated checks from live GUI acceptance.

## Setup

Install AgentBroker from the module catalog, then enable it in its options pane. Its workstation MCP endpoint is `http://127.0.0.1:7432/mcp`. LAN HTTPS uses port 7433 by default. The worker includes its runtime, so it does not require an ASP.NET installation or administrator privileges.

Use **Connect Codex MCP** or **Connect Claude MCP** in the module options to add the named, user-scoped `desktop-ai-companion-agentbroker` stdio bridge through the vendor CLI. Existing entries are never silently replaced. Reconnect MCP or restart that client to load the tools. The bridge retrieves the current local connection through a user-only management pipe; it does not persist a broker access token in client configuration. A client must have its supported CLI available on PATH.

Metadata discovery defaults on. Native Codex peer delivery and Claude peer credential access default off. Enable a native adapter only after reviewing its [compatibility requirements](agentbroker-client-compatibility.md). Claude uses its peer-class token locally and delegates inbound acceptance, holding and refusal to the recipient's own policy. It never forwards that credential to another agent or workstation.

When a broker is discovered, the companion asks before requesting pairing. Compare the complete code on both machines and explicitly approve on both. A request or a single approval grants no sharing. Pairing authorizes both brokers' current and future discoverable sessions. The tray exposes private, mute and revoke controls. If multicast discovery is blocked, copy a public broker invitation from one options pane and paste it into the other, then use the same pairing-code approval flow. Invitations contain public identity information only.

Both workstations must be able to reach the configured HTTPS port. Automatic discovery also needs UDP 5353 on the local network. Any necessary Windows Firewall allowance should be limited to the Private profile and LocalSubnet. AgentBroker does not create administrator firewall rules or expose its management pipe remotely. A physical second-workstation test remains necessary to validate a particular LAN's routing and firewall configuration.

Discovery checks private IPv4 interfaces every ten seconds, retries while offline, and recreates sockets after interface changes or transport failures. Existing paired endpoints stay pinned: moving a broker to another address may require pairing again. Discovery never silently changes stored peer trust to follow an unverified address.

## Agent tools

The MCP server exposes `list_sessions`, `register_agent`, `send_message`, `read_inbox`, `acknowledge_message`, `get_message_receipt` and `reply_to_message`. Use the exact address returned by discovery, keep a stable message ID when checking a questionable outcome, and inspect the receipt before deciding what happened. A native write is **submitted**, not proof that the recipient read it. The receiving agent can acknowledge and reply through MCP once its client is connected.

`list_sessions` and `read_inbox` return arrays in bounded pages. Both accept an optional `limit`, defaulting to 16; values outside 1 through 16 are refused. A full page may have more results: call again with the last returned key as the cursor. A shorter or empty page completes that scan.

| Tool | Cursor for the next page | Ordering |
|---|---|---|
| `read_inbox(session, limit, afterMessageId)` | Last receipt's `message.id` | Message GUID comparison |
| `list_sessions(limit, afterAddress)` | Last session's complete `address` | Broker GUID, ordinal session ID, incarnation GUID |

Copy cursor values from the last result rather than constructing an offset or looking up the old record. An expired inbox record or disconnected session does not invalidate its key. These collections are mutable live views, not snapshots: new messages or sessions may appear before an existing cursor. Start each new poll without a cursor and finish its pages before polling again. Paging keeps large retained inbox histories within the stdio response limit without truncating message text.

## Intended behavior

Each workstation runs its own local broker. Agents use MCP to discover reachable sessions, send a message, inspect a receipt, and reply. A compatible adapter wakes the exact existing idle session through its original client owner. Session names and working directories are display metadata, never addressing keys. Addresses include broker identity, vendor session ID, and an adapter incarnation.

The workstation endpoint may omit authentication when it is strictly bound to loopback. LAN communication uses authenticated HTTPS. A discovered broker initially exposes identity and connection information only. Explicit pairing authorizes current and future shared sessions in both brokers; private sessions remain excluded. Pairing never grants transitive trust or changes a session's tool permissions.

Discovery and transport stay local. This does not make Claude or Codex inference offline: the chosen agent's existing model provider still applies.

## Delivery contract

Accepted, submitted, delivered, held, refused, expired, and uncertain are different outcomes. The store persists admission before a delivery attempt. Repeated message IDs with an identical payload recover the existing receipt; conflicting reuse is rejected. Reply creation is atomic, so concurrent retries preserve the first reply's timestamps and receipt. Expiry bounds a message's delivery window; a retained message can receive a later reply with its own deadline and remaining reply budget. A broker restart invalidates session registrations. An interrupted native submission becomes uncertain rather than blindly replaying a prompt that might already have started a turn.

Queues, session leases, message size, retention, paired brokers, and reply depth are bounded. Busy, approval-blocked, interrupted, disconnected, and idle states remain distinct. A label match or persisted transcript never proves a session is live or wakeable.

## Implementation sequence

| Phase | Work | Required evidence |
| --- | --- | --- |
| 0 | Existing-client adapter feasibility | Exact ordinary GUI owner, idle wake, visible message, unchanged sandbox and approval routing, no duplicate owner |
| 1 | Baseline and narrow backlog reconciliation | Existing build, smoke checks, regression gate, evidence for stale entries |
| 2 | Durable broker core | Persistence, deduplication, bounded queues, expiry, reply correlation, restart recovery |
| 3 | MCP and local adapters | Bidirectional real Claude/Codex delivery, busy/held/closed/disconnected cases |
| 4 | LAN discovery and pairing | Two broker identities, explicit mutual authorization, directory sharing, delivery, revocation, reconnect |
| 5 | Companion module | Settings, questions, supervision, bounded shutdown, standby for a second companion instance |
| 6 | Packaging and necessary shared fixes | Loader and update path, deterministic ZIP, committed catalog hashes, download bounds |
| 7 | Compatibility and endurance | Smoke then regression per phase, targeted mutations, resource and reconnect soaks |
| 8 | Publication and audit | Commit/push default branch `master`, audit again, fix and retest findings, truthful backlog |

## Local checks available now

Run the shared smoke and regression suite from the repository root, using the pinned .NET SDK:

```powershell
pwsh -NoProfile -File tests/Test-AgentBroker.ps1
```

```powershell
pwsh -NoProfile -File tests/Invoke-IsolatedGate.ps1
```

The relay harness uses a synthetic subprocess and never starts a vendor engine. See [client compatibility evidence](agentbroker-client-compatibility.md) and the [relay limitations](../tools/AgentBroker.CodexRelay/README.md) before attempting setup. Read-only protocol probes must not create a replacement server or submit a message.

The seven-harness suite builds Core, Relay, Claude, Desktop, Adapter, Network, and Worker checks. Network tests use three HTTPS brokers bound to this workstation. Worker checks exercise the actual MCP HTTP endpoint, current-user control pipe, authentication, browser-origin rejection, privacy, mute, proven pre-input deferral, and restart behavior. These tests create and delete only their own nonexportable current-user Windows CNG keys; they never use a real agent's peer credentials. Current assertion counts are printed by the harnesses rather than maintained as a stale total here.

The original normally opened Codex Desktop test conversation accepted a fixed, authorized idle prompt through its existing native owner, and the user confirmed the assistant marker appeared in that same GUI. This proves the tested idle submission, not busy-session admission or live foreign-message provenance. After a desktop update and crash, the new normally opened VS Code test thread is identified but its native owner connection still needs to reappear. The Claude VS Code 2.1.306 peer smoke reached its exact original process and returned SubmittedUnconfirmed after an action-specific approved credential read. The person confirmed the assistant marker appeared in that original VS Code conversation, proving that isolated peer wake and reply.

## Identity, privacy, and operating limits

The local HTTP listener binds the literal IPv4 loopback address. Optional token authentication is independent of LAN authentication; browser origins and DNS aliases do not inherit local authority. The management interface uses an explicitly user-owned, protected named pipe. Its DACL denies network logons, and clients verify the connected server process and pipe ownership before sending a request. It is never exposed on the LAN listener.

Windows Schannel requires a named private key for TLS. AgentBroker creates a current-user Microsoft Software KSP key with a nonexportable policy. Its encrypted `tls-identity.dat` stores public certificate metadata and the exact owned key name. No raw private-key file, certificate-store installation, administrator action, or OS trust change is required. Identity survives same-user, same-path restarts. Moving the data directory or losing the Windows key fails closed; copying data to another machine is not an identity backup. A new identity requires explicit pairing again.

Pairing shares current and future discoverable sessions directly between the two approved broker identities. Each session can be made private or muted. These choices persist across discovery and restart. A muted session cannot receive automatic peer input. Native client approvals remain authoritative; messages cannot approve tools or elevate either agent's permissions.

The current durable store is intended for low-volume workstation coordination. Defaults bound retained messages to 4,096, raw text to 16 KiB per message, live registrations to 256, paired brokers to 32, pending messages per recipient to 64, and a conversation to eight replies. Native adapters may impose a smaller envelope limit and explicitly refuse oversized input. Receipts remain until message expiry plus one day; reaching capacity refuses new messages rather than silently dropping existing receipts. Each state transition replaces a flushed snapshot, so high-throughput use needs a measured storage redesign. Idle directory polling does not rewrite durable state.

## Decisions

| Decision | Reason |
| --- | --- |
| Deliver AgentBroker and directly relevant regressions in this effort | The user accepted narrowing the earlier request to clear all 73 unrelated backlog items. |
| Allow one-time setup/restart for explicitly enabled, version-checked adapters | Required for normally opened desktop and VS Code sessions without a stable external submission API. |
| Prefer an existing desktop-owned IPC route over a Codex executable override on Windows | The installed desktop's override changes sandbox/bootstrap initialization. |
| Preserve Claude peer classification | Its exported child token is treated as self-sent and can bypass default inbound mode parity. |
| Do not read native Claude peer key records during source investigation | Live credential access requires a separate exact platform approval under sensitive-file policy H05, and W06 if stored in a workspace. |
| Use a separate self-contained worker for the HTTP server | Keeps the host's runtime requirements and plugin ABI stable. |
| Pair broker identities, including future shared sessions | Matches the requested seamless broker-level authorization. |
| Require both brokers to approve the complete 128-bit comparison code | A discovered name, pairing request or single approval cannot authorize a spoofed peer. |
| Keep native delivery off until explicitly enabled | Version pins and live acceptance are separate from successful generic MCP transport. |
| Treat expiry as a per-message delivery deadline | Long-running tasks can send a fresh reply after the original message's delivery window. |
| Preserve stored endpoint pins after network changes | Discovery recovers automatically without silently retargeting trusted connections. |
| Retain unrelated and externally blocked backlog entries | Closing them without implementation or evidence would hide remaining work. |

## Audit and local delivery (2026-10-21)

The first default-branch push was reviewed in parallel. Follow-up fixes reject empty module diagnostic arguments before normal startup, advertise the actual HTTPS port, remove an unused pairing branch, and paginate both collection tools within the stdio response bound. The local updater now verifies the installed frontend and worker DLL hashes plus completed stage/marker state, so a rolled-back old worker cannot report a successful update. Independent review of these corrections found no remaining material blocker.

| Verification | Recorded result |
| --- | --- |
| Full isolated regression gate | Passed, including 799 AgentBroker assertions, all 16 application self-tests and all 8 covered modules. |
| Host diagnostic smoke | Eight real malformed-argument cases refused without creating application data, in PowerShell 7 and 5.1. |
| Large-history transport regression | Passed through authenticated HTTP and stdio with complete escaped 16 KiB messages and history over 8 MiB. |
| Guarded updater regression | 43 synthetic checks passed, including old/mixed payload, hard-link, pending-stage and marker-race refusals. |
| Local 1.0.1 update | Old worker exited gracefully; exact package DLL hashes matched; stage and marker were consumed; seven MCP tools responded. Existing broker data/settings were preserved. |

The packaging smoke passed against rebuilt outputs; committed-ZIP SHA validation and the installed DLL/runtime proof were separate checks. Rebuilding can change informational revision metadata and ZIP timestamps, so those artifacts were not claimed to be byte-identical. Two physical-key checks were withheld, with zero keypresses measured. The development-only test module retains its previously declared gaps. Passive resource observations are bounded samples, not proof of leak freedom.

Use `tools/agentbroker/Install-LocalAgentBroker.ps1 -SelfTest` to repeat updater fixture checks, or `tools/agentbroker/Test-LocalAgentBrokerHealth.ps1 -WorkerProcessId <exact PID> -SampleSeconds 30` for read-only installed health and resource counters. The latter opens no settings, message store, native credentials or client configuration.

## Remaining acceptance gates

The historical Codex Desktop idle smoke, Claude VS Code peer wake and visible reply, and packaged-module loader checks passed. Current-package Codex external delivery, live busy/idle ordering, live client MCP setup and physical second-workstation LAN acceptance remain separately tracked acceptance gates. The person deferred a VS Code window reload to keep their ongoing work running; the latest live probe did not find the exact Codex test's native owner, and no fallback session was created. Passing synthetic or same-workstation TLS tests must not be presented as proof of those live gates. Native delivery remains explicitly disabled in the local installation pending setup and acceptance.
