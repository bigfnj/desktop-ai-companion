# AgentBroker implementation and validation

AgentBroker adds local MCP messaging and paired LAN brokers through a separate, self-contained worker. Native GUI delivery is explicitly enabled and restricted to tested client versions. The evidence below distinguishes automated checks from live GUI acceptance.

## Setup

Install AgentBroker from the module catalog, then enable it in its options pane. Its workstation MCP endpoint is `http://127.0.0.1:7432/mcp`. LAN HTTPS uses port 7433 by default. The worker includes its runtime, so normal operation does not require an ASP.NET installation or administrator privileges. The optional Windows Firewall setup action requires administrator approval.

Use **Connect Codex MCP** or **Connect Claude MCP** in the module options to add the named, user-scoped `desktop-ai-companion-agentbroker` entry through the vendor CLI. Existing entries are never silently replaced. Claude Code prefers the broker's live loopback HTTP endpoint when the user-only management connection confirms that no token is required. Codex and confirmed token-required connections use the stdio bridge, which retrieves its current connection through that pipe. A failed Claude connection-info request asks the person to refresh status before setup. Neither route persists a broker access token in client configuration. Clients normally use their supported CLI on PATH. If the bare Claude command is missing, the button checks only the current user's standard `.local/bin/claude.exe` installation; it never retries a running CLI's failure or changes PATH.

**Connect Claude MCP** configures Claude Code, including its VS Code extension. After successful registration, start a new Claude conversation and check `/mcp`; reloading VS Code is not normally necessary. Managed policy can allow HTTP URLs separately from stdio commands, and a loopback URL still needs approval when an allowlist is enforced. If registration is refused, ask the policy owner to approve the exact local server URL or stdio command and arguments, as applicable, or deploy the approved server through managed configuration. AgentBroker does not retry another transport or override policy after a refusal. See [Claude's managed MCP documentation](https://code.claude.com/docs/en/managed-mcp).

An organization can delegate a soft allowlist that people may extend in their own settings. In that case, the reviewed `tools/agentbroker/Grant-ClaudeLocalMcpAllowance.ps1` helper can append only the exact local AgentBroker URL after an explicit approved action. It retains a private rollback copy and does not change managed files, policy locks, denials or server definitions. Managed restrictions still apply, and the normal vendor CLI remains the final authority. A locked allowlist or other refusal requires the policy owner's action.

Registration confirms that the server definition was saved. Connection additionally requires an MCP handshake and a usable tool list. The reviewed `Connect-ClaudeMcpEntry.mjs --verify-http` mode checks only the fixed AgentBroker name and exact local HTTP entry, then reports fixed status fields without forwarding CLI diagnostics, headers or configuration. It runs no model or native wake command. A successful CLI check proves that CLI connection; a normally opened VS Code conversation must still load the entry and show it connected in `/mcp`.

Metadata discovery defaults on. Native Codex peer delivery and Claude peer credential access default off. Enable a native adapter only after reviewing its [compatibility requirements](agentbroker-client-compatibility.md). Claude uses its peer-class token locally and delegates inbound acceptance, holding and refusal to the recipient's own policy. It never forwards that credential to another agent or workstation.

The **Wake existing Codex sessions** and **Wake existing Claude sessions** switches enable automatic delivery into normally opened idle client sessions. They are needed for that behavior. MCP discovery, inbox polling and replies work without native wake, so the switches are separate opt-ins. Host 1.6.1 and module 1.0.2 refresh status in place without resetting pending edits; a worker that misses its startup deadline reports an unavailable state rather than remaining indefinitely at Starting.

### MCP connection status

Module 1.0.3 added MCP endpoint readiness and Claude/Codex last-seen activity to **Status**, with more detail now shown in **MCP client traffic**. For example: `MCP ready. Claude: seen just now; Codex: not seen.` Only successful MCP traffic counts. Saved client configuration and discovered native sessions do not establish an MCP connection.

HTTP clients connect on demand, so an idle client does not become disconnected after an arbitrary timeout. Last seen means successful traffic observed since this worker started; observations reset on worker restart. A client can still display a cached connection before it sends its next request. Older stateless tool requests omit client identity and appear under Other clients; initialization or supported per-request identity supplies the Claude/Codex label. Labels are self-reported display information and grant no access.

These observations retain only three fixed client categories and timestamps. Status uses the existing refresh cycle, without launching vendor commands, sending agent messages or storing request contents. Disabled, unavailable and older workers without MCP status are shown explicitly.

### Pairing and deadlines

When a broker is discovered, the companion asks before requesting pairing. Compare the complete code on both machines and explicitly approve on both. A request or a single approval grants no sharing. Pairing authorizes both brokers' current and future discoverable sessions. The tray exposes private, mute and revoke controls. If multicast discovery is blocked, copy a public broker invitation from one options pane and paste it into the other, then use the same pairing-code approval flow. Invitations contain public identity information only.

Simultaneous requests previously left two offers with different comparison codes on each broker. A deterministic HTTPS regression reproduced that failure before the fix. A collision now keeps the lower broker identity's outbound offer and retires the other request, nonce and code. Repeated requests for an exact pending peer reuse its current offer. Both users must still approve the same complete code; approval of a discarded code never transfers. The companion withdraws an obsolete comparison question and ignores a late response to it. A collision displays current-code guidance rather than a connection failure, with no automatic retry or authorization.

Pairing requests and confirmations use a 30-second control-client deadline around the worker's 25-second operation deadline. A request can perform two HTTPS exchanges, each bounded to ten seconds. Ordinary status retains its three-second client deadline. Network, TLS and rejected-offer failures remain visible in the Pairing information, tray and pinned failure status after healthy status polls. A saved pairing for the same broker ID and certificate clears its obsolete pairing failure, while errors for other identities remain. The pane and tray show saved trust separately from current reachability. Guidance uses fixed error categories without raw remote diagnostics. If a result is lost, inspect pending or paired brokers before retrying; a timed-out reply does not prove that no state changed.

The **Paired brokers** list beneath **Status** shows saved broker names and HTTPS endpoints. It updates with status, including endpoint changes, and distinguishes an empty list from an unavailable worker. Saved trust does not establish current reachability. A successful pairing remains visible after restarting the worker; delayed status responses cannot replace a newer pairing or revocation observation.

Both workstations must be able to reach the configured HTTPS port. Automatic discovery also needs UDP 5353 on the local network. In the AgentBroker settings pane or tray menu, choose **Allow LAN access** after this companion's own LAN worker reaches Running. The companion asks for consent, then requests Windows administrator approval. Its fixed-purpose worker command adds an inbound allow rule for that exact executable and applied TCP port, limited to the Private profile and LocalSubnet, with edge traversal disabled. It adds no Public-network rule, changes no existing rules and configures no router port forwarding. Run the action on each workstation that needs inbound access. Changing the applied port or installation path needs another setup action; previous rules are not removed automatically.

Setup verifies the requested rule and reuses an identical existing rule. Conflicting rules, possible application block rules and policy restrictions are reported for review rather than edited. An allow rule alone does not prove reachability: other firewall rules or network policy can still block the connection. A timed-out or interrupted Windows setup has an unconfirmed outcome, and another action waits for the original elevation operation to finish. The management pipe remains local, and pairing still requires both brokers' explicit approval. A physical second-workstation test remains necessary to validate a particular LAN's routing and firewall configuration.

Discovery checks private IPv4 interfaces every ten seconds, retries while offline, and recreates sockets after interface changes or transport failures. Pairing survives worker restarts and reboots when each broker keeps its Windows user, data directory and identity. A paired broker keeps using its saved address while that address works. If discovery announces a changed private IPv4 HTTPS address, automatic recovery verifies the original broker ID and exact paired certificate over HTTPS before saving the new endpoint. The original approval, certificate pin and name remain unchanged. Discovery cannot create trust or restore a revoked pairing; a replaced identity requires explicit pairing again.

A recent read-only LAN probe successfully reached a discovered peer's pinned HTTPS public-info endpoint. It sent no pairing approval or agent input. This confirms that sampled connection path; physical two-workstation acceptance for mutual approval, shared directories, message/reply delivery, revocation and reconnect remains pending. The synthetic HTTPS collision smoke and full network regression passed separately.

## Agent tools

The MCP server exposes `register_agent`, `list_sessions`, `send`, `wait_for_reply`, `wait_for_messages`, `acknowledge_message`, `reply_to_message`, `read_inbox`, `get_message_receipt` and `send_message`. Every `initialize` result also carries a short instructions card describing the protocol below (`tools/AgentBroker.Worker/BrokerInstructions.cs`). The stdio bridge forwards `initialize` unchanged, so Codex receives the same card as an HTTP client. Use the exact address returned by discovery, keep a stable message ID when checking a questionable outcome, and inspect the receipt before deciding what happened. A native write is **submitted**, not proof that the recipient read it. The receiving agent can acknowledge and reply through MCP once its client is connected.

| Tool | Parameters and defaults | Result | Refusals beyond address and broker checks |
|---|---|---|---|
| `send` | `sender`, `recipient`, `messageId`, `text`, `conversationId` (defaults to `messageId`), `ttlMinutes` (0 means the default lifetime; longer values are capped at 24 hours) | The receipt; a retry returns the original one | `message_id_collision`, `sender_unavailable`, `recipient_unavailable`, `invalid_message`, a negative `ttlMinutes` |
| `wait_for_messages` | `session`, `timeoutSeconds` 20 (0..25), `limit` 16 (1..16) | Incoming receipts still `Accepted`, oldest first; empty means the deadline passed | `wait_in_progress`, `wait_capacity`, `session_unavailable`, out-of-range values |
| `wait_for_reply` | `messageId`, `timeoutSeconds` 20 (0..25) | Replies to that message addressed to its sender, oldest first, at most 16 | `wait_in_progress`, `wait_capacity`, `session_unavailable`, a missing or foreign original |

### Mailboxes and retention

A generic MCP registration has two lifetimes. Its presence lease lasts one minute, and every successful call that names the session renews it: reads, waits, sends, replies and acknowledgements. When presence lapses, the mailbox stays addressable for 24 hours after the owner's last call, provided the agent keeps the same `clientId` and incarnation. A retained mailbox still accepts sends and replies from local agents and paired brokers. Its owner can still read, acknowledge and reply, and the owner's next successful call restores presence. A new incarnation is a new mailbox.

A local `list_sessions` lists a retained mailbox with `Activity` `Away` and its last, now past, `LeaseUntil`. Paired brokers, the peer directory and `/federation/directory` still list present sessions only, so `Away` never leaves this broker and no federation record changed. Native sessions keep the live-lease rule: once a native lease lapses, that session is refused as sender and as recipient. A registration cannot claim `Away` itself.

Retention is process-local and never persisted. A worker restart drops every registration while messages persist, and registering the same `clientId` and incarnation again finds them. At most 64 mailboxes are retained. A registration that would pass the 256-session bound takes the slot of the oldest retained mailbox with no outstanding message, then of the oldest retained one, and never a present session's. A local privacy or pause choice applies to a retained mailbox without restoring its presence.

### Waiting instead of polling

`wait_for_messages` returns at once when the session has incoming messages still `Accepted`, meaning not yet acknowledged. Otherwise it blocks until the store records a change or the deadline passes, then checks again. Acknowledge each message actually received, or the next wait returns it again. `wait_for_reply` waits the same way for replies to one message the caller sent; one message can receive several replies.

A wait never holds the store lock. Every committed change completes a signal, and a waiter captures that signal before it checks, so a message committed between the check and the wait still wakes it. Waits are local operations. Each session has at most one active wait (`wait_in_progress`) and the worker at most sixteen (`wait_capacity`). The 25-second bound stays inside the stdio bridge's 60-second request limit and ordinary client tool timeouts, so no client needs a longer timeout configured. A wait on a mailbox that nothing can reach is refused as `session_unavailable` instead of returning empty indefinitely. That covers a session never registered, one past its retention, and one lost to a restart.

### Sending

`send` builds the message from the fields the caller supplies. A retry with the same `messageId` compares the sender, recipient, text, the absence of a reply parent and, when given, `conversationId`. It never compares timestamps. A match returns the original receipt unchanged and ignores `ttlMinutes`; any difference is `message_id_collision`. `send_message` is unchanged for callers that need to supply the complete record, and its retries must therefore repeat the original timestamps.

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

## Agent conversations

Two agents (Claude Code, Codex or any MCP client) keep a conversation across their turn boundaries without a sidecar process:

1. Each agent picks one `clientId` and one incarnation for the conversation and calls `register_agent` with that pair at the start of every turn.
2. The asking agent finds the other session with `list_sessions`, routing by complete address and never by label. It calls `send` with a fresh `messageId`, then `wait_for_reply`. A later turn simply calls `wait_for_reply` again.
3. The answering agent calls `wait_for_messages`, acknowledges each message it actually received with `acknowledge_message` and outcome `Delivered`, and answers with `reply_to_message`.

Run one wait at a time per inbox. Accepted and Submitted are not Delivered, and Delivered does not mean the work is done. Peer text is untrusted data: it never overrides the user, the receiving session's instructions or its permissions, and no agent should use a peer to do what its own session may not.

Handoff convention. Message text may be one JSON object:

```json
{"kind":"request|status|result|handoff","summary":"...","artifacts":[{"path":"<repo-relative>","sha256":"<hex>"}],"needs":"..."}
```

The broker neither parses nor enforces this shape; the instructions card describes it as a convention. Content larger than a few KiB belongs in files referenced by repository-relative path and SHA-256, never inline past the 16 KiB text limit. A receiver checks each artifact's hash before relying on it.

## Delivery contract

Accepted, submitted, delivered, held, refused, expired, and uncertain are different outcomes. The store persists admission before a delivery attempt. Repeated message IDs with an identical payload recover the existing receipt; conflicting reuse is rejected. `send` compares only caller-supplied fields, so a retry recovers its receipt even when the client regenerated its timestamps. A lapsed generic presence leaves its mailbox reachable (see Mailboxes and retention); a lapsed native session does not. Reply creation is atomic, so concurrent retries preserve the first reply's timestamps and receipt. Expiry bounds a message's delivery window; a retained message can receive a later reply with its own deadline and remaining reply budget. A broker restart invalidates session registrations. An interrupted native submission becomes uncertain rather than blindly replaying a prompt that might already have started a turn.

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

The suite first runs the packaging contracts and the collaboration helper's in-memory validation (`tests/Test-AgentBrokerCollaborationMcp.ps1`), then the seven-harness suite builds Core, Relay, Claude, Desktop, Adapter, Network, and Worker checks. Network tests use three HTTPS brokers bound to this workstation. Worker checks exercise the actual MCP HTTP endpoint, current-user control pipe, authentication, browser-origin rejection, privacy, mute, proven pre-input deferral, and restart behavior. These tests create and delete only their own nonexportable current-user Windows CNG keys; they never use a real agent's peer credentials. Current assertion counts are printed by the harnesses rather than maintained as a stale total here.

The original normally opened Codex Desktop test conversation accepted a fixed, authorized idle prompt through its existing native owner, and the user confirmed the assistant marker appeared in that same GUI. This proves the tested idle submission, not busy-session admission or live foreign-message provenance. After a desktop update and crash, the new normally opened VS Code test thread is identified but its native owner connection still needs to reappear. The Claude VS Code 2.1.306 peer smoke reached its exact original process and returned SubmittedUnconfirmed after an action-specific approved credential read. The person confirmed the assistant marker appeared in that original VS Code conversation, proving that isolated peer wake and reply.

## Identity, privacy, and operating limits

The local HTTP listener binds the literal IPv4 loopback address. Optional token authentication is independent of LAN authentication; browser origins and DNS aliases do not inherit local authority. The management interface uses an explicitly user-owned, protected named pipe. Its DACL denies network logons, and clients verify the connected server process and pipe ownership before sending a request. It is never exposed on the LAN listener.

Windows Schannel requires a named private key for TLS. AgentBroker creates a current-user Microsoft Software KSP key with a nonexportable policy. Its encrypted `tls-identity.dat` stores public certificate metadata and the exact owned key name. No raw private-key file, certificate-store installation, administrator action, or OS trust change is required. Identity survives same-user, same-path restarts. Moving the data directory or losing the Windows key fails closed; copying data to another machine is not an identity backup. A new identity requires explicit pairing again.

Pairing shares current and future discoverable sessions directly between the two approved broker identities. Each session can be made private or muted. These choices persist across discovery and restart. A muted session cannot receive automatic peer input. Native client approvals remain authoritative; messages cannot approve tools or elevate either agent's permissions.

The current durable store is intended for low-volume workstation coordination. Defaults bound retained messages to 4,096, raw text to 16 KiB per message, registrations (present or retained) to 256 with at most 64 retained mailboxes, mailbox retention to 24 hours after the owner's last call, paired brokers to 32, pending messages per recipient to 64, a conversation to eight replies, and a blocking wait to 25 seconds with one per session and sixteen per worker. Native adapters may impose a smaller envelope limit and explicitly refuse oversized input. Receipts remain until message expiry plus one day; reaching capacity refuses new messages rather than silently dropping existing receipts. Each state transition replaces a flushed snapshot, so high-throughput use needs a measured storage redesign. Idle directory polling does not rewrite durable state.

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
| Revalidate a paired identity before saving a discovered address change | Restarts and DHCP changes recover without trusting an unverified discovery announcement. |
| Retain unrelated and externally blocked backlog entries | Closing them without implementation or evidence would hide remaining work. |
| Keep a generic MCP mailbox addressable for 24 hours after its owner's last call | Turn-based agents fell off the bus between turns, and a renewal sidecar process was the workaround. Retention is process-local and peers still see presence only, so no federation record or persisted state changed. |
| Never retain a native session | A lapsed native lease means the vendor session is gone; waking a stale native session must stay impossible. |
| Bound blocking waits at 25 seconds, one per session and sixteen per worker | Stays inside the stdio bridge's 60-second request limit and ordinary client tool timeouts without configuration; each wait occupies a request. |
| Refuse a wait on an unreachable mailbox | An empty result would look like silence forever after a restart; `session_unavailable` tells the agent to register again. |
| Add `send` beside `send_message` | Clients regenerate timestamps on retry; comparing only caller-supplied fields keeps an honest retry from colliding, while `send_message` keeps its exact-record semantics. |
| Publish the protocol as MCP server instructions | Every client receives the same short card in `initialize`, Codex included through the stdio bridge, instead of a per-client setup note. |

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

### Status and client-setup correction

An installed HTTP health check and the production control client both succeeded while the pane still displayed its initial Starting snapshot. The control request completed in 99 ms. The host now accepts opt-in `OptionsPane.NotifyInfoChanged()` notifications and updates existing Info controls without rebuilding editors. Notifications are coalesced, subscribed before the initial load, and detached on rebuild, navigation, failed construction and window close. No timer or additional worker polling is introduced.

Module 1.0.2 skips unchanged saves, distinguishes request failures from startup, and bounds startup grace to 15 seconds. A transient status failure no longer permits an unbound retry to mistake the same owned child for another broker and terminate it. Host 1.6.1 is required for the additive options capability; the Contracts assembly binding version remains unchanged.

Focused smoke and regression checks passed: 344 WPF assertions, 67 AgentBroker module checks, all eight covered modules, and the seven-harness 799-assertion broker suite. The guarded updater additionally stages the canonical host runtime, verifies its hashes and versions, retains rollback bytes, enforces the module's published host floor, and can explicitly archive a previous replaced module without deleting its contents. Its synthetic checks exercise partial-replacement rollback and preserve unrelated settings.

The first Claude registration attempts were refused by a soft allowlist. After an explicitly approved exact-URL user allowance, the normal HTTP registration succeeded and a selected-entry readback confirmed it. A separate exact-name CLI health check confirmed the user-scoped HTTP connection, and the person then confirmed Connected and available tools in `/mcp` in a new Claude VS Code conversation. A new dedicated Codex VS Code conversation passed public compatibility verification, but its native owner and directory advertisement were absent; no test prompt was sent. These remaining live integration checks are tracked below.

### MCP status enhancement

Module 1.0.3 adds passive endpoint/client observations without changing MCP admission, message delivery or client setup. Its focused HTTP smoke passed 21 checks, the complete worker regression passed 117, and the module passed 133 internal checks plus nine loader/lifecycle checks. The complete seven-harness broker suite passed 829 assertions. Checks cover independent client timestamps, older stateless requests, failed requests, authorization/browser-origin rejection, shutdown, malformed/older snapshots and live Info refresh without settings writes.

The verified local 1.0.3 update preserved data and settings, completed its stage/marker processing, matched installed public payload hashes and served all seven MCP tools. The named Claude CLI check then confirmed its HTTP connection. A production management-pipe observation independently reported MCP ready, Claude seen, Codex not seen and a successful tool-list request. That is evidence of observed traffic since the worker restarted, not proof of a continuously connected client or a live cross-vendor reply.

The subsequent full isolated project gate passed with zero build warnings, all 16 host self-tests, all eight covered modules, template scaffolding and real-loader checks, catalog integrity and all ten converter suites. Two physical-key checks were deliberately withheld with zero keypresses measured; the development test module's declared gaps remain unchanged. An earlier intermittent scratch-cleanup sharing violation remains recorded in `BACKLOG.md` despite this successful run.

## Remaining acceptance gates

The historical Codex Desktop idle smoke, Claude VS Code peer wake and visible reply, packaged-module loader checks, and live Claude HTTP MCP connection with tools in a new VS Code conversation passed. Current-package Codex external delivery, live native busy/idle ordering, cross-vendor acknowledgement/reply round trips and physical second-workstation LAN acceptance remain separate acceptance gates. The person has now allowed a VS Code window reload for the dedicated Codex test; no replacement thread may be substituted. Both native wake switches were explicitly enabled by the person. Passing registration, synthetic or same-workstation TLS tests must not be presented as proof of those remaining gates.
