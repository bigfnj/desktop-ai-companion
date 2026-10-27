# AgentBroker client compatibility evidence

Compatibility snapshot: 2026-10-22. The ordinary Codex desktop passed one idle wake test through its existing owner. After a client update, public source and read-only connection checks were repeated. This records those limited live results and the remaining acceptance gates.

## Re-pin helper, 2026-10-27

`tools/agentbroker/Test-AgentBrokerRepin.ps1` is a read-only assistant for exact
re-pinning. It finds the installed Codex desktop package, Codex VS Code
extensions, the VS Code router and the Claude Code executables (standalone and
VS Code). For each it verifies the expected-publisher Authenticode signature,
compares the image with the pins in source, and runs the offline public checks
this document relies on:

- Codex desktop: the `OpenAI.Codex` package family and publisher ID under
  WindowsApps, SYSTEM or TrustedInstaller ownership with no foreign write grant
  on the executable and archive, and the fixed-pipe, owner-discovery,
  follower-start, untrusted-input, inherited-settings, busy-guard and
  status-request markers in the archive's `.vite/build/` members (read through
  `inspect-public-asar.mjs`).
- Codex VS Code: the engine's OpenAI signature and the same protocol markers in
  `out/extension.js` and the `app-main-*`, `app-initial-*` and `src-*` bundles.
- Claude Code: the native inbox version floor and the messaging token and socket,
  `cc-msg-` pipe, peer record, start-time and inbound-policy markers.

A build that passes every offline check gets the exact replacement pin text for
the owner to review. The helper never applies it. It writes nothing, opens no
pipe and reads no credential, session or configuration file. Every failed or
unproven check is listed by name with its reason. Facts that depend on minified
code stay UNPROVEN for a person to re-read: Claude peer-token admission, the
Codex idle-check order, which extension bundle owns the renderer bridge, and the
canonical manifest hash. Every check from "Required live acceptance" is always
UNPROVEN. A signature and matching markers never authorize a build by themselves.

Run `pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerRepin.ps1`, with
`-Json` or `-Vendor Codex|Claude` as needed. Exit code 1 means at least one
installed build is NOT READY. `-SelfTest` runs its fixture checks under Windows
PowerShell 5.1 or PowerShell 7 and touches only its own temporary directory.

On the development workstation on 2026-10-27 it reported the VS Code router
1.143.1 as already pinned, and offered drafts for Codex desktop 26.1021.3705.0,
Codex VS Code 26.1021.22027 and the Claude Code VS Code runtime 2.1.310, each
with every live check unproven. Its first run also corrected its own scope: in
extension 26.1021.22027 the inherited-settings and busy-guard markers sit in a
`src-*` chunk rather than `app-initial-*`, so it scans both.

## Discovery and diagnostics update, 2026-10-26

Claude discovery and native delivery now have separate trust outcomes. An exact
reviewed image retains the existing delivery profile. An updated executable can
appear as an observation-only session when Windows Authenticode verifies the
expected Anthropic publisher and its process user, creation time, image path and
bounded numeric registration metadata match. Publisher observations have no
receive, wake or reply capabilities, even with credential opt-in; submission is
refused before provider transport or credential access. Public-image trust is
unavailable when Windows cannot verify it from its current cache. This changes
Claude discovery only; Codex compatibility and native delivery profiles remain
separate. Sources: `ClaudeNativeProvider.ObserveUnpinned`,
`WindowsClaudePublicImageTrust.Observe`, and `ClaudeNativeAdapter.CanDeliver` in
`tools/AgentBroker.Worker/Adapters/`.

The owned management operation `peer-diagnostics` performs the existing pinned,
authenticated directory request against current paired peers. A validated empty
response has a confirmed count of zero. Failed checks have null counts and fixed
failure codes; peers not reached within the bounded batch remain not checked.
Authorization is rechecked after the request and before publishing the batch.
`tools/agentbroker/Test-AgentBrokerPeerAuthentication.ps1` exposes this diagnostic
without session contents or identifiers. Its `-CompileOnly` path sends no request.
An older worker reports `unknown_control_operation`; that is unavailable evidence,
not successful authentication. Sources: `PeerDiagnostics.cs`, `ControlProtocol.cs`
and `LocalControlServer.cs` in `tools/AgentBroker.Worker/`.

Generic MCP clients still require both client configuration and a live
`register_agent` inbox. Connection or tools-list success does not create an inbox.
The one-minute generic lease must be renewed; successful reads, sends, replies and
acknowledgements renew an already live polling session. Fresh-client onboarding is
tracked as `N-agentbroker-client-onboarding-01` in `BACKLOG.md`.

These source changes do not establish automatic native wake, a fresh-client setup
result or physical two-PC acceptance. The following older observations remain
historical evidence, including the stricter discovery policy used at that time.

The inspected VS Code `26.1007.21434` renderer also distinguishes an idle turn
from a loaded stream owner. Its inactive-thread unsubscriber uses a three-hour
TTL and a ten-owner retention bound for undisplayed idle conversations without
followers; successful unsubscribe marks the thread `needs_resume` and clears its
stream role. An open mounted editor view is retained when hidden. Source:
`webview/assets/app-initial-97d3534ad35f.js` (`inactiveThreadUnsubscriber`,
`unsubscribeInactiveConversation`, `clearConversationStreamOwnership`) and
`out/extension.js` (`retainContextWhenHidden`) in that public extension.
Keep an intended native target mounted in its own conversation tab, or reopen it
before delivery. A fresh read-only positive control found an existing loaded
Code owner without input; it does not prove native wake or the cause of every
historical `no-client-found` response.

## Latest live verification

- A read-only public-image audit on 2026-10-22 found no replacement pin to apply. Installed Claude Code 2.1.292 and running VS Code Claude Code 2.1.306 retain their exact verified hashes and valid Anthropic signatures. Codex desktop 26.1016.6573.0, extension 26.1007.21434 and VS Code router 1.143.0 retain their existing image/source pins; the public Codex binaries have valid OpenAI signatures, and the router has a valid Microsoft signature. The fresh built `--verify-vscode-profile` check passed. These checks preserve the earlier source evidence for those exact images; they do not rerun the outstanding live acceptance matrix.
- Discovery now reports a specific installed/verified-version diagnostic after a Claude candidate passes its earlier metadata, process and lifetime checks but fails its version or image pin. A mixed discovery pass keeps supported sessions. Codex compatibility codes and known installed versions reach the worker status instead of becoming a generic unavailable error. Nonfatal Codex VS Code profile failures retain observations while withholding native receive/wake capabilities. A completed healthy pass clears the warning. Compatibility diagnostics change reporting only; discovery and delivery still require the existing exact pins.
- A fresh user-supplied Codex VS Code test UUID also returned no loaded native owner after the client-reconnection attempt. The installed and running-engine public profiles passed, but passive discovery did not list that exact target. No model input, substitute conversation, resumed owner or conversation-history request was used. The next live attempt still requires exact owner discovery; a visible conversation and compatible public binaries do not establish ownership.
- On 2026-10-22 the exact-target read-only probe ended with `timeout_or_cancelled`. Its generic failure path does not distinguish connection or initialization from owner-discovery timeout, so that result proves neither owner absence nor a failed pipe connection. Owner reachability and activity remain unproven; no input or history request was sent.
- A subsequent staged read-only probe against a newly supplied test thread confirmed that the local pipe connection and version-pinned initialization succeeded; the timeout occurred during exact-owner discovery. That result still does not establish owner absence, activity, or delivery. `--probe-thread` now reports the completed connection milestone and failing stage separately. For a read-only stage breakdown, run `pwsh -NoProfile -File tools/agentbroker/Test-CodexOwnerConnection.ps1 -ThreadId <authorized-test-UUID>`; it sends no model input or history request.
- A following exact-target CLI probe completed initialization and returned `ownerFound:false` without a transport error. A separate read-only probe with an explicit 12-second request bound also completed with no matching recipient after 4,470 ms. These are native `no-client-found` results, not proof of which registration or reverse-role lookup failed. The longer bound did not establish a timeout as the cause of this result. No model input was sent; the existing VS Code acceptance gate remains open. The staged probe accepts `-RequestTimeoutSeconds 1..15`, retains a 30-second overall bound, and reports discovery elapsed time.
- Fresh checks on 2026-10-22 passed the installed and running-engine public Code profile verification logic. An earlier unrelated missing-parent diagnostic had cleared: a PID/start-time-only check found the previously observed process was no longer present. Neither Codex engine was stopped by these read-only checks. That observation establishes neither why the process disappeared nor why native discovery did not find the dedicated normally opened Code view's owner.
- One exact-target metadata-only request used a separate verified router connection and a two-second observation window after initialization. It received one frame, with zero exact-target advertisements and zero matching source/status counts. This observation does not authenticate a recipient or establish owner reachability. A separate fresh owner probe, with a 12-second budget, initialized successfully and completed with `ownerFound:false` after 5,771 ms. No model input, configuration change, restart, replacement conversation, resumed owner or conversation-history request was used. The earlier generic `timeout_or_cancelled` result did not identify its failed stage and is superseded only by these narrower observations. Live delivery acceptance remains unmet.
- A later development-only GUI probe opened the exact requested custom-editor tab and observed its exact URI/provider, but the person saw an empty message body, `Couldn't load messages`, and `This is open in another app`. Exact-tab creation passed; loaded-thread content and usable native ownership remained unverified. The public Code profile then passed with two running engines. One post-open metadata observation again received one frame and zero exact-target/source/status advertisements; a separate initialized owner probe, with a 12-second budget, completed with `ownerFound:false` after 8,112 ms. These post-open results are distinct from the earlier 5,771 ms probe. No model input, process stop, existing-window reload or relay was used.
- The new duplicate view's writer-conflict card comes from `local-conversation-thread-2429c4b61076.js` (`Sg`, character offset 159426). Its resume hook, `use-resume-conversation-if-needed-fd4b0fa61ef7.js:822`, classifies active-writer/live-local-writer errors from ordinary GUI resume, and excludes them from automatic retry at offset 8153. The pinned manager sends `thread/resume` in `app-initial-97d3534ad35f.js:4797086`; successful resume establishes a stream-owner role, while failure clears a tentative role. The separate history banner at `local-conversation-thread-2429c4b61076.js:146968` can reflect a history-state or visible-content failure. These branches explain the new view's refusal; they identify neither the actual writer process nor the original view's missing native-owner cause. Runtime writer exclusion and native coordinator discovery are separate checks. Do not infer native ownership from a visible tab, or assume closing a tab releases every writer.
- A fresh READY-only conversation in the normal VS Code window also remained unreachable through native ownership discovery. Its exact identifier was confirmed by the person's Status screenshot and one matching local filename, without reading conversation contents. A pinned initialized owner probe completed with `ownerFound:false` after 186 ms. A separate bounded metadata probe passed the public Code profile, received three frames and observed no exact-target advertisements. No broker model input, new GUI connection, restart or configuration change was used. This remains an unresolved native connection failure and does not establish a delivery route.
- A subsequent exact-target metadata-only request passed the public Code profile and observed one frame during its two-second post-initialization window. The new bounded counters classified it as one broadcast from one eligible foreign source, advertising `following:true` for one unrelated local conversation. Exact-target advertisements and matching-source counts remained zero, as did incoming discovery requests, connected/disconnected status events, other-host advertisements, and unsupported, malformed or unrecognized broadcasts. The frame limit was not reached. Eligibility means a valid nonself source addressed to this diagnostic or untargeted; it does not authenticate that source or identify it as a Code view or owner. This establishes some unrelated local advertisement traffic, not zero clients or a particular timeout cause. No model input was sent and delivery remained disabled. The next diagnostic must establish the native Code owner's authenticated provenance rather than repeat blind owner probes.
- The owner helper's `discoveryElapsedMilliseconds` stopwatch starts after `ConnectAsync` completes and stops before adapter disposal. The reported owner-discovery durations exclude connection setup, initialization and initial public-image hashing. A fast `ownerFound:false` with no error reflects the router's `no-client-found` response; its shared failure path does not distinguish absent registration, a declining follower, failed role lookup or another discovery/forwarding failure.
- A local active cross-vendor MCP exchange passed: Codex used a private generic polling inbox through `tools/agentbroker/Invoke-AgentBrokerCollaborationMcp.ps1`; the designated Claude agent registered its own private polling inbox, read and acknowledged the brief, and sent back a marker and source findings. Codex read and acknowledged both replies. The brief and replies have explicit `Delivered` receipts. This verifies active generic polling collaboration, not automatic native Code wake, persistent client configuration or communication across two physical workstations. Reuse the same generic registrations during active work. Presence lasts one minute; since the agent-to-agent bus change, a retained mailbox keeps receiving for 24 hours after its owner's last call when the same clientId and incarnation are reused. The helper defaults to validation only and requires `-Execute` for one bounded loopback request; both message addresses must be generic, and implicit live replies are refused.
- Recipient provenance remains a separate design gap for Code views: a pinned router and its client UUID do not bind that recipient to an operating-system process. Static extension pins and a global engine count also do not prove which view owns the exact target. Keep the existing global guards and exact pins; do not synthesize an owner role. An authenticated Code-to-adapter-to-engine binding, or an equivalent verified route, needs source review and live permission, busy/idle, approval and reconnection acceptance before delivery. VS Code-only relay feasibility remains under audit and is not enabled. See `N-agentbroker-code-owner-provenance-01` in `BACKLOG.md`.
- Source inspection also prevents enabling the experimental relay's direct `turn/start` route as a permission-equivalent Code fallback. In pinned `app-initial-97d3534ad35f.js`, `applyThreadSettings` attempts `thread/settings/update` but catches an unsupported-method result while retaining the GUI's latest settings locally. Ordinary GUI turns derive explicit approval, reviewer, sandbox, profile and runtime-root overrides from those latest settings. The relay tracks initialization, threads and turns and injects `threadId` plus input; it has neither authoritative current GUI permission state nor a barrier for unsent or in-flight selections. A future route must establish acknowledged settings and GUI synchronization, or use the GUI's own admission route, while retaining source and live approval acceptance gates.
- The dedicated Claude VS Code test session, native 2.1.306, passed exact PID, creation FILETIME, current user, public image hash and named-pipe owner/DACL checks. A separately approved, exact H05 credential read authenticated with the peer token and sent one fixed no-tools marker. The result was `SubmittedUnconfirmed` (`native_write_only`); the protocol supplies no immediate delivery acknowledgement. The person separately confirmed the assistant marker appeared in that original VS Code conversation, proving that isolated peer wake and reply. No credential was printed or exported.
- The Claude test exposed a .NET `CurrentUserOnly` mismatch: it compares pipe ownership with the token's default owner, which can differ from its actual user. Native Claude and broker management connections now explicitly validate the actual user and connected process instead. Management servers also have a protected user-owned DACL that denies network logons. Synthetic hostile-descriptor and real isolated pipe tests cover the change.

The sections below retain the original investigation evidence. Statements that no credential was read describe that source-inspection stage; the later action-specific approved smoke is recorded above.

## Scope and safeguards

The required clients are ordinarily opened Codex and Claude Code desktop and VS Code sessions. One-time adapter setup and a client restart are acceptable. An adapter must address the original session through its owning process, preserve permissions and approvals, and avoid creating a second resumed owner.

Inspection used public installed application code, CLI help, process name/PID/parent/executable metadata, and exact existing named-pipe connection/security probes. One probe read only the explicitly allowlisted `CODEX_THREAD_ID` environment value internally and printed no ID. No authentication files, private keys, browser profiles, session transcripts, process command lines, or other process environment values were read. No client was restarted or reconfigured by this inspection.

## Reproducible public package inspection

`tools/agentbroker/inspect-public-asar.mjs` reads archive header metadata and focused snippets from one explicitly selected public JS/JSON member. It does not extract archives or write outside the repository. It refuses protected member names, links, unpacked members, and arbitrary whole-member output.

Example from the repository root:

```powershell
node tools/agentbroker/inspect-public-asar.mjs 'C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\resources\app.asar' --list-prefix '.vite/build/'
```

```powershell
node tools/agentbroker/inspect-public-asar.mjs 'C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\resources\app.asar' --member '.vite/build/application-network-startup-DB1VeU3v.js' --pattern 'CODEX_CLI_PATH|CODEX_WINDOWS_REGISTERED_CORE|CODEX_WINDOWS_SANDBOX_PACKAGE_FAMILY|windows_sandbox_service' --context 700 --limit 15
```

The reported offsets are JavaScript string character offsets in the decoded member. Package updates change both member names and offsets, so repeat the inspection and require explicit version/hash compatibility before enabling an adapter.

## Codex desktop on native Windows

The first live plain-input wake used `OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0`. That package was removed by an application update. `DesktopCompatibilityProfile.Packages` now contains complete profiles for both `26.1016.6573.0` and `26.1016.8622.0`; its public `ApplicationVersion` constant names only the former. The earlier profile has public executable SHA256 `602A21169FA011CF48997DBC07A0FFCDC11058CAA5DC992E534876E8CB6F9A4E`, archive SHA256 `6F56542B508EEA5B6E4D03E852DCD90AFC2578AAC7125BAA3F25EBE85EBC7E25`, and separate Electron product version `155.0.8059.80`. The later profile has executable SHA256 `F7F605CCEB2E75DCA52E03362329A889F917CC1F8D1036CD26386727A2319415`, archive SHA256 `A05DFD86CA508148C83C25590639CF91A52C6647733CDF200723D85357052B49`, and the same Electron version. `SelectProfile` selects the complete tuple by the actual executable path; the constant alone is not the allowlist. Source: `tools/AgentBroker.CodexDesktopAdapter/WindowsDesktopConnection.cs`.

Current `application-network-startup-BG13czUt.js:277288` retains the fixed Windows pipe and four-byte little-endian framing. `src-CsH31EEu.js:8058` retains the protocol versions, `:531343` delegates follower start to the existing owner, and `:874335` / `:876683` contain both pre-injection idle checks. The exact-owner read-only probe passes connection, public-image, process and ACL verification on the new package. The earlier dedicated test conversation no longer has a loaded owner after the crash, so its previous wake proof must not be represented as a current-package delivery test.

Earlier process metadata associated the desktop's `ChatGPT.exe` with its child runtime at `%LOCALAPPDATA%\OpenAI\Codex\bin\<runtime-hash>\codex.exe`. This identifies the owner relationship; it does not establish a control endpoint or engine version. The following historical source offsets describe the first package unless stated otherwise.

Public archive member `.vite/build/application-network-startup-DB1VeU3v.js` establishes:

| Character offset | Evidence | Implication |
| --- | --- | --- |
| 255727 | Default desktop originator is `Codex Desktop`; base CLI options enable `features.code_mode_host`. | Preserve the GUI's originator and model/tool context. |
| 258513 | `StdioConnection.spawnProcess` creates one child with all three streams piped. | A relay can mechanically preserve this existing owner and its byte stream. |
| 263225 | The local daemon/WebSocket selection requires `process.platform !== win32`, plus an opt-in and other compatibility conditions. Native Windows falls back to stdio. | Setting `CODEX_APP_SERVER_USE_LOCAL_DAEMON` is not a native Windows fix for this installed package. |
| 264351-265670 | The launcher distinguishes the registered core runtime from an override. Overrides lose `CODEX_WINDOWS_REGISTERED_CORE`, `CODEX_WINDOWS_SANDBOX_PACKAGE_FAMILY`, and the conditional `features.windows_sandbox_service=true` addition. | A bare executable override is not proven equivalent to the original sandbox/runtime context. |
| 267855 | `tc` resolves a configured CLI command or `CODEX_CLI_PATH`, then falls back to the normal runtime. | An app-scoped, reversible environment override can select a relay after restart. |
| 268911 | Normal launch arguments use `app-server` and `--analytics-default-enabled`. | The relay must forward CLI arguments and non-server invocations without treating them as model requests. |

Public member `.vite/build/bootstrap-cdWF4rWM.js` adds a stronger constraint:

| Character offset | Evidence | Implication |
| --- | --- | --- |
| 506021-506329 | The app-contained-core eligibility function requires no `CODEX_CLI_PATH` override; its setup registers the runtime resource root and sets `CODEX_WINDOWS_REGISTERED_CORE`. | The override affects parent bootstrap before a relay begins running. |
| 519920 | Bootstrap clears inherited registered-core/package-family markers. | Pre-setting these markers in a shortcut is not sufficient. |
| 521395 | Bootstrap obtains the real package family from its native helper, hydrates runtime framework gates, sets the sandbox package family where eligible, and may register the app core before loading main. | Reconstructing only a few environment strings inside a relay cannot yet claim complete bootstrap equivalence. |

These are conditional source paths. Actual runtime-gate values and the existing engine's non-secret launch context were not inspected. Do not assume all markers are active in the observed running process.

### Desktop adapter recommendation

Use the existing native owner coordination pipe as the primary route for both desktop and participating VS Code views. It preserves the GUI's original launcher, engine and containment. The desktop route now has an exact-owner idle wake proof.

Retain the executable relay only as an experimental fallback. Its effective sandbox and runtime identity have not been proven equal to the ordinary desktop launch. The presence of an override suppresses app-contained-core eligibility during parent bootstrap, before a relay can restore child environment values. Launching the original image and copying a few markers is insufficient evidence of equivalence. Do not silently switch sandbox modes, invent package identity, force gates, or enable the relay as the default adapter.

### Existing desktop coordination pipe

The same installed package includes a native owner coordination route. This avoids replacing the registered core executable or changing its containment setup.

| Public member / character offset | Evidence |
| --- | --- |
| `application-network-startup-DB1VeU3v.js:277215` | `Fc()` selects the exact Windows endpoint `\\.\pipe\codex-ipc`; it is not a per-user suffixed path. |
| `application-network-startup-DB1VeU3v.js:278005` | `Bc()` returns immediately on Windows. Unix socket ownership and mode checks in that function do not establish a Windows ACL guarantee. |
| `application-network-startup-DB1VeU3v.js:279453` | Frame format is a four-byte little-endian UTF-8 JSON byte length followed by JSON bytes. |
| `src-DsLVGakC.js:1535356-1543372` | Router initialization assigns a client UUID. Requests route to an explicit target client or the first existing client that answers discovery affirmatively. Client type is a string, not a vendor identity credential. |
| `src-DsLVGakC.js:1552470` and `main-C4DYk222.js:730931` | Owner discovery requires the existing manager's matching host and `owner` stream role. Its response advertises `supportsUntrustedAppInput:true`. Follower operations delegate to that owner's manager. |
| `src-DsLVGakC.js:529615` | `thread-follower-start-turn` delegates to the same manager's `startTurn(conversationId, turnStart)`. |
| `src-DsLVGakC.js:832893-840600` | Turn preparation inherits thread model, workspace and permission settings unless the request explicitly changes them. |
| `src-DsLVGakC.js:847665-855400` | Submission checks matching IDs, existing owner and streaming state, waits for pending settings, updates manager state, and uses native user-message admission when eligible. Standard server events and approval routing remain with that owner. |

`probe-codex-owner-ipc.mjs` only connects to an already existing endpoint. It never starts a router, thread, resumed owner, or model turn. It sends only `initialize` and `thread-owner-discovery`, rejects incoming discovery requests, ignores broadcasts, and prints summary booleans/counts. Initial synthetic and allowlisted-environment probes both connected and initialized without finding their supplied UUIDs. A later user-supplied dedicated ordinary GUI conversation passed exact owner discovery with `supportsUntrustedAppInput:true`; the pinned .NET adapter independently passed the same discovery plus image/process/ACL gates.

```powershell
node tools/agentbroker/probe-codex-owner-ipc.mjs
node tools/agentbroker/probe-codex-owner-ipc.mjs --thread-env CODEX_THREAD_ID
pwsh -NoProfile -File tools/agentbroker/inspect-codex-ipc-security.ps1
```

The exact pipe security probe opens `READ_CONTROL`, queries only owner/DACL and server PID, then closes without message reads or writes. The server PID matched the previously observed desktop `ChatGPT.exe`. Both sandboxed and explicitly approved ordinary-user probes reported: a present DACL; SYSTEM and Administrators access mask `0x001F01FF`; Everyone and Anonymous access mask `0x00120089`; and an owner differing from the current user SID. The read grants do not include data write access. These observations do not prove a user-exclusive endpoint, effective access for every token, or application authentication. A production adapter must separately verify the connected server image/version, process owner/start and allowed local access, and expose only its own authenticated, scoped broker control surface.

The local request envelope uses `sourceClientId` from initialization, method version `1` for discovery and `2` for `thread-follower-start-turn`. Discovery parameters are `{hostId:"local",conversationId:T}`. Local follower requests omit envelope `hostId`; non-local host IDs change protocol version and are outside this adapter profile. Pin the owner client returned by discovery as `targetClientId` for subsequent requests and rediscover after disconnect or ownership change.

A minimal **candidate for a benign, explicitly authorized test**, still awaiting exact-target acceptance, is:

```json
{
  "method": "thread-follower-start-turn",
  "params": {
    "conversationId": "<existing owned thread UUID>",
    "turnStart": {
      "request": {
        "threadId": "<same UUID>",
        "input": [{"type":"text","text":"<benign marker>","text_elements":[]}],
        "clientUserMessageId": "<unique UUID>"
      },
      "context": {"inheritThreadSettings":true}
    }
  }
}
```

That is user-authorized plain input. It is not the app's richer untrusted-app provenance path. Do not force `useUserMessageAdmission:true`: that override selects a cloud-only admission operation on this installed native local client. Omitting it lets the existing owner choose its normal local or cloud operation while inheriting thread settings. Never pass peer-controlled permission/model/workspace fields, arbitrary injected response items, tool output, or follower approval/elicitation methods.

No coordination handler for creating a new ordinary GUI conversation was found. The desktop `newThread` deep-link route at `main-C4DYk222.js:3676736` opens home and prefills the composer; it does not itself create a loaded thread or send the prompt. Use a dedicated ordinary GUI test conversation for each new delivery mode's live acceptance.

### Desktop live evidence and implementation

The person created a temporary ordinary desktop conversation, sent a no-tools readiness prompt, received its reply, and supplied its exact thread link. No title guessing, transcript lookup, second app-server or resumed copy was used.

1. The first smoke used the incorrect forced cloud-admission flag. The submitted message appeared in that exact GUI conversation with the inline error `User-message admission requires a cloud app-server host.` and no assistant reply. The tool returned `Uncertain`; that attempted message was not retried.
2. After reviewing the source, the adapter removed that flag and used only `inheritThreadSettings:true`. One new message ID produced native `Accepted`, and the person explicitly confirmed the unique requested reply was visible in the original conversation.
3. Read-only process metadata before and after showed the same two engine PIDs, parent PIDs and executable paths for the desktop and VS Code. No extra owner process was introduced.

The package-free `tools/AgentBroker.CodexDesktopAdapter` implements this route. It currently pins package `26.1016.6573.0`, the public executable/archive hashes, connected server PID, creation FILETIME and same-user process identity. The executable's Electron version is separately identified, rather than mistaken for the application version. It rejects unexpected write grants, uses a fixed local production pipe, and binds exact thread/client/process/adapter incarnations. The ordinary default is connection-only.

At that qualification, its synthetic suite passed 142 checks, including fragmented frames, schema/version/identity bounds, owner rediscovery, no settings overrides, deduplication, stale bindings, disconnects, lost replies, safe error diagnostics, the typed external user message, bounded directory metadata, exact pre-dispatch deferral, running VS Code metadata rejection, alternative router identity and fixed readiness matching. Persistent-observer checks covered its known-ID metadata heartbeat, self-echo rejection, bounded index, unknown activity, identity changes, disconnects, protocol faults and disposal. The worker adapter regression suite passed 64 assertions, including verified observer reuse/reconnect/disposal, exact prewrite receipt-capacity recovery, one profile verification per discovery pass, and fresh verification that rejects a changed profile before submission. The experimental relay suite passed 40 assertions, including engine/process lifetime, control-listener failure, stderr failure and active disposal. These are historical qualification counts. The relay is not part of the production provider or worker package.

The first live failure exposed a documented vendor error shape without `method` or owner ID; the parser now accepts that error shape while preserving uncertain delivery. Diagnostics print only allowlisted schema keys, type booleans and an allowlisted error category, never raw native error bodies or unrelated conversation data.

This proves idle wake through the first package's original desktop owner for user-authorized plain input. Current-package external delivery, busy ordering, interruption, approval behavior, reconnection and revocation are still pending live acceptance.

### External peer input and busy ordering

`SubmitExternalPeerAsync` uses one strictly typed Responses `message` with role `user` and `input_text`. Its fixed controller instructions identify the content as external agent input and preserve the recipient's existing instructions and approvals. The complete external content is quoted in JSON together with its message ID. The same labeled content is included in the ordinary GUI prompt so the person can inspect what was submitted. No account, tool, widget, developer/system role, or permission metadata is fabricated. The [official app-server documentation](https://developers.openai.com/codex/app-server/) describes loaded-thread `thread/inject_items`; the public protocol `ResponseItem::Message` schema accepts this typed shape.

The current pinned owner checks nonempty response items against idle before creating its pending GUI record and rechecks immediately before injection. Its exact `App context must wait until the current turn finishes` error proves that neither injection nor turn dispatch occurred. Only that error from the correct request/owner permits `DeferredNotSubmitted` and bounded same-ID backoff. Every other native error, partial write, timeout, owner mismatch or unconfirmed response stays `Uncertain`. A second-check race can briefly create and roll back a local pending GUI record.

This capability is named `NativeIdleGuard`, not atomic admission. The owner marks the pending GUI turn in progress so ordinary GUI submissions follow their normal queue behavior. Its public start-operation observer does not provide an engine-wide asynchronous lock, and the core `inject_response_items` implementation does not itself reject active turns. Independent engine goals or API input can race after the final check. The adapter makes no stronger global ordering guarantee. Past interruption is not inferred from idle; explicitly observed interruption is preserved, and module mute/private controls govern future peer messages.

The native queue-state follower method was rejected as an append route. Current `src-CsH31EEu.js:529012` invokes `acceptFromFollower`; at `:968074` this replaces the complete queued list and preserves only existing outcome-unknown entries. It has no expected revision or compare-and-swap field. Reading, merging and writing that list could erase a concurrent prompt queued by the person.

The earlier synthetic MCP-app wrapper was removed. The genuine app route obtains `sourceId` from a live host-created widget session scoped to the conversation. A configured server alias does not provide that identity. It would be misleading to invent a widget ID or tool result to claim richer provenance. A genuine future MCP widget can call native `ui/message` using its host-provided identity, but that is a separate integration.

### Automatic open-session metadata discovery

The public stream manager's `handleClientStatusChanged` at `src-DsLVGakC.js:354533` sends `thread-stream-following-changed` announcements for all currently followed conversations to a newly connected client. These version-1 metadata broadcasts contain exact local host/conversation UUIDs and following state. New owner acquisition also emits version-1 `thread-stream-following-status-requested` metadata.

Production `DesktopDirectoryObserver` retains one separately verified read-only connection rather than repeatedly hashing the desktop archive for every discovery poll. Cryptographic verification occurs on each connection; process incarnation is checked before snapshots and writes. A single reader continuously receives metadata. Every 30 seconds a serialized writer sends at most 256 version-1 `thread-stream-following-status-requested` messages for already known IDs. Those requests refresh metadata without sending `following:true`, subscribing to snapshots/history, or changing a queue. The index ignores the observer's own echoed requests, so vanished owners cannot remain listed from self-refresh alone. Faults close the connection and fail observation; the next bounded worker poll reconnects through full image verification. Disposal cancels both loops, closes the transport and bounds its wait.

The one-shot `DesktopDirectory.ObserveOpenSessionsAsync` remains a diagnostic that receives announcements and then closes. Neither observer forges a vendor client type, sends generic webview RPCs, or starts/resumes a thread. Both retain at most 256 IDs for two minutes. Typed results contain UUID, generic display label, observed activity and timestamps; callers must run exact owner discovery before delivery. Native snapshot/patch status, if already received, can establish active/approval/idle state. Following and owner metadata alone always yield `Unknown`. Interruption is not inferred. The worker verifies actual VS Code clients once per discovery pass for capability advertisement, avoiding duplicate full hashing per candidate. Delivery uses its own exact-owner connection and repeats full compatibility checks immediately before model input. Public-file size or timestamp metadata is never accepted as a substitute for cryptographic verification.

A live metadata-only probe automatically found the dedicated ordinary desktop conversation without registering its UUID: one observed conversation, the known test target present, and unknown activity. It printed only counts/booleans and sent no history or model request. This proves currently followed desktop discovery for that case. The VS Code extension and Code-hosted router now have separate public compatibility profiles, but their live discovery/delivery acceptance remains pending.

For broker addressing, `DesktopOwnerBinding.NativeSessionIncarnation` derives from the native router process PID/creation FILETIME, owning client UUID and exact conversation UUID. It remains stable when a broker observer or delivery connection reconnects. The separate binding `Incarnation` is a per-adapter connection lease and must not be used as the persistent broker session address. A genuine native owner client/process change produces a new native incarnation.

## Codex VS Code

Observed extension: `openai.chatgpt-26.1007.21434-win32-x64`. Its public `package.json` points to `out/extension.js`.

- The extension directly spawns `app-server` with `features.code_mode_host=true` and analytics defaults, reads stdout JSONL, and writes requests to child stdin.
- `package.json:117-124` exposes `chatgpt.cliExecutable` but labels it DEVELOPMENT ONLY and warns that parts of the extension may not work as expected. No server socket/daemon setting was found in its declared configuration.
- Registered public commands cover opening UI, creating conversations, attaching file context, LSP and debugging. `addToThread` and `addFileToThread` only attach file context. No supported exact-thread submit command was established.
- The generic webview `mcp-request` path rejects user-message admission and says it requires the durable Electron host. Injecting a queue/turn request through that generic path is insufficient.

The extension also participates in the same native coordination pipe. Its public `KZ`/`a4e` functions register matching owner discovery and follower protocol versions. `createClientCoordinationSession` creates a `vscode` IPC client, binds the existing app-host webview, and registers those handlers against that view's `clientCoordination` service. Ownership queries call the existing view's `getThreadRole`; follower operations call that view's `requestThreadFollower`. Extension activation creates another `vscode` coordination client. This wiring changes the recommendation: use the existing owner route before considering an executable relay.

The public webview bundle `app-initial-97d3534ad35f.js` contains the matching owner-manager delegation, inherited settings, normal local turn path and both pre-injection idle checks. `app-initial-7a199c66e670.js` contains the matching native untrusted-input schema/placeholder/transform. `app-initial-c014f9ee4429.js` owns the renderer's `vscode-capn-rpc-connect` transport and reverse `getThreadRole` delegation; its SHA256 is `D162D3333C2A64BD9C18F1DC9E48F811660B69F2C53CFE79D192ABF906837209`. `VsCodeCompatibilityProfile.VerifyInstalled()` pins all three bundles plus `package.json`, `out/extension.js` and the app entry point. `VerifyRunningClients()` additionally reads only process name/PID/parent/image/start/user metadata and checks direct `Code.exe` native engine children against the expected extension path and public binary SHA256. This prevents an old extension folder left on disk from concealing a different running engine. Standalone CLI and sandbox descendants are excluded. The read-only live profile check passed with one running native Code-owned engine. This establishes compatibility, not wake proof or precise IPC ownership for an arbitrary view.

The inspected extension installs owner handlers after its renderer connects; no separate coordination opt-in was found in that registration path. AgentBroker uses the source-matching local host scope and protocol versions. Native discovery accepts only an actual owner role, using the cached tuple or a bounded 100 ms reverse role lookup. The router reduces both an unhandled request and failed role discovery to `no-client-found`, so the observed response cannot distinguish a missing registration from a follower or failed lookup. Extending the caller's timeout does not override that native predicate. Do not claim a specific cause or bypass ownership when this response occurs.

The connected pipe server may be desktop while its selected recipient client is a VS Code view. Therefore the router process pin alone does not establish that view's extension version. Require the matching public extension profile and exact owner binding, then prove one ordinary VS Code session's idle wake, settings and GUI result. Passive open-view discovery is implemented and desktop-tested; its VS Code coverage and reliable owner-client provenance remain acceptance gates. Retain the development-only relay as an experimental fallback; do not configure it by default.

Public extension `UN()` uses the fixed Windows endpoint, and its router starts a normal `net.Server` there with `EADDRINUSE` handling when another router exists. The connection now also accepts the exact current-user `Microsoft VS Code\Code.exe` router image, product version `1.143.0`, SHA256 `F13F0179B0E6EC0251E22ADBD123FE5F252FA227421B2128CF21379CF5941D58`, and a verified compatible native engine. It retains process/user/start/ACL checks. This alternative identity branch passes synthetic checks; live Code-hosted-router acceptance remains pending. No desktop shutdown was used to force that case.

## Codex protocol evidence

Standalone CLI help reports `0.161.0`; this does not establish either GUI's bundled runtime version.

The [official app-server documentation](https://developers.openai.com/codex/app-server/) distinguishes persisted records from loaded sessions: `thread/list` and `thread/read` do not establish a live owner. `thread/loaded/list` lists in-memory IDs. The generated [0.161.0 loaded-list schema](https://raw.githubusercontent.com/openai/codex/rust-v0.161.0/codex-rs/app-server-protocol/schema/typescript/v2/ThreadLoadedListResponse.ts) includes `data` and `nextCursor`; clients must handle pagination.

The [0.161.0 CLI queue source](https://raw.githubusercontent.com/openai/codex/rust-v0.161.0/codex-rs/tui/src/session_queue_commands.rs) uses `thread/queue/add` and explicitly guards against separate-server writes. Its [queue service](https://raw.githubusercontent.com/openai/codex/rust-v0.161.0/codex-rs/ext/queue/src/service.rs) can wake loaded idle threads and dispatch after normal completion; interrupted/unloaded threads are different states. No queue was modified during this investigation.

## Claude native inbox

The [official cross-session messaging page](https://code.claude.com/docs/en/cross-session-messaging) states that eligible Windows sessions use per-session named pipes. VS Code and desktop are included as recipients. Delivery to an idle session starts a new turn, subject to that recipient's inbound policy. Native Windows needs version 2.1.234 or later; some provider/flag-fetching combinations need 2.1.248. Standalone `2.1.292` satisfies those version floors, but GUI bundled versions still need verification.

The documented script/hook integration exports `CLAUDE_CODE_MESSAGING_SOCKET` and `CLAUDE_CODE_MESSAGING_TOKEN`. On Windows, the first line must be an authentication object with `type` set to `auth` and `token` set from that inherited token. Keep the token inside the session-owned adapter; do not read key files or export credentials to AgentBroker peers.

The page does not publish the subsequent message object's full wire schema. Installed top-level CLI help exposes no standalone send/message command. However, the installed public `claude.exe` 2.1.292 image embeds its inbox implementation and a runtime-generated Windows example. Its SHA256 for this snapshot is `EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23`.

That version's own example sends two newline-delimited JSON objects, authenticates from the inherited child token, and ends the connection:

```json
{"type":"auth","token":"<inherited child token, never log>"}
{"type":"user","message":{"role":"user","content":"hello"}}
```

The embedded user handler `we(e,n,i,d,c)` requires a nonempty string at `message.content`. Its shared `Q` validation rejects a supplied mismatching `session_id`. Optional `uuid`, `msg_id`, `from`, and priority values are processed by that version. The handler creates a peer-origin message, disables slash-command and attachment expansion, and routes it through native inbound admission. The direct socket handler shown does not send an immediate delivery acknowledgement; flushing a write establishes submission only. Native held-message receipts are a separate control-message path, not proof available from the example above.

### Claude child-token trust constraint

The apparently convenient example cannot be used blindly for messages originating in Codex or a paired LAN broker:

- `aps()` generates separate random peer and child tokens. Only the child token is exported to hooks and child commands. Native code publishes the peer token to a protected key record; no such record was listed or read here.
- `fps()` classifies authentication with the exported token as `child`.
- On native Windows, `L`/`W` use this classification to set `selfSent` on the resulting peer origin. A caller cannot clear it by supplying `from` or `from_mode` fields.
- In the inbound admission function `E`, an explicit `crossSessionInbound` value applies first. Without an explicit value, `selfSent` accepts before permission-mode parity is checked. Adding a sender-mode assertion cannot remove that exemption.

Forwarding foreign content using a child token would therefore change the native default hold/parity behavior, even though explicit `hold` and `refuse` remain honored. Treating transport authentication as permission to relabel foreign content as the session's own child would be incorrect.

Native-Claude foreign-message delivery remains gated on one of these verified designs: a session-owned adapter whose admission is proven equal to or stricter than current native rules; a separately authorized peer-class credential integration that does not export credentials; or a vendor-supported ingress that preserves foreign-message classification. Until then, fail closed for foreign deliveries. The observed minimal child envelope establishes a local wake mechanism, not the complete AgentBroker acceptance condition.

### Claude peer credential derivation and policy

The installed 2.1.292 public code derives the record directory as `path.join((CLAUDE_CONFIG_DIR ?? path.join(os.homedir(), ".claude")).normalize("NFC"), "sessions")`. For a Windows pipe, `OJ` accepts two leading slash/backslash characters, a dot or question mark, `pipe`, an optional `LOCAL` segment, and exactly one final name. It rejects final names `.` and `..`, names ending in dot or space, and any path beginning `\\?\` that also contains a forward slash. The accepted message-pipe name is `cc-msg-` plus 32 hexadecimal characters, optionally under `LOCAL`.

Canonical `YE` rebuilds `\\.\pipe\` plus the ASCII-lowercased name (including the literal `LOCAL\` prefix when present). The exact credential filename is `<native server PID>.<SHA256(canonical pipe) as lowercase hex>.key`. Its bounded JSON schema is at most 4096 bytes and contains a 32-character lowercase hexadecimal `peerToken`, plus optional string `procStart`, `procStartFt`, and `pidDomain` values. The native Windows creation token comes from `GetProcessTimes` as an unsigned 64-bit little-endian FILETIME, serialized as decimal `procStartFt`. It is not .NET `DateTime.Ticks`. The native Windows PID-domain hint is `win32:` plus the lowercase hostname; it is not authentication.

This describes public derivation only. No matching credential filename was discovered, opened, parsed, or copied. Any eventual live read needs exact action-specific platform approval for H05 (and W06 if its resolved directory falls inside a workspace). A local opt-in helper should establish an exact canonical target from session-owned non-secret registration and connected pipe server PID, verify same-user image/version/start and no links, read only that approved bounded record, and retain the token inside local process memory. Never export it to a broker peer or logs.

Peer-token authentication does not set the child-token `selfSent` exemption. With absent sender-mode metadata, native `E` accepts into a recognized prompting recipient, holds into a bypass recipient (`no-mode-asserted`), and holds when the recipient mode is missing or unrecognized (`mode-unknown`). An explicit inbound setting applies first, including managed/repository tightenings. Do not invent Claude permission parity for a cross-vendor sender and do not silently set `crossSessionInbound` to `accept`. Guaranteed automatic wake into a bypass recipient requires a deliberate recipient policy choice. The raw user handler does not propagate a `from_mode` field; it derives any richer peer metadata from the vendor's own message wrapper, which has not been established as a broker ingress contract here.

To reproduce only the public-image source check, use `rg -a -o` with focused patterns such as `CLAUDE_CODE_MESSAGING_TOKEN`, `async function we\\(e,n,i,d,c\\)`, and `function fps`. Do not run runtime functions from the extracted source or read any key/token/session records.

## Required live acceptance

No adapter is ready merely because a process exists, a persisted conversation can be found, or an RPC method accepts a request. Earlier-package desktop exact original-session identity and one plain-input idle wake with visible reply are proven. The separately authorized Claude Code 2.1.306 VS Code idle peer wake and reply were also confirmed in the original conversation, as recorded above. Remaining checks cover current-package Codex external delivery and exact original-session VS Code wake; busy delivery after the current turn; blocked/held/refused outcomes; interruptions; no duplicate delivery; unchanged approvals and sandbox; disconnect/reconnect; two sessions with the same cwd; revocation; and shutdown cleanup. Use a benign marker and a consenting dedicated ordinary GUI session after identity and safety gates pass.
