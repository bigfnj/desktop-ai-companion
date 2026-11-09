# AgentBroker

Thin host module for the separately packaged AgentBroker worker. Requires host 1.6.1.
The module starts disabled. Enable it in Options, then request pairing from its tray submenu.
Compare the complete four-group code and approve sharing separately on both workstations. A request
or one approval alone grants no access. Pairing authorizes sharing current and future discoverable sessions. Session controls separately hide
a private session or mute incoming messages. Federation directory and delivery requests authenticate
the paired client certificate; public broker information remains readable for identity checks. Loopback authentication
is optional. Native peer delivery is a separate opt-in and preserves each agent's existing permission controls.

The worker executable lives at worker/AgentBroker.Worker.exe beneath the module. All persisted identity,
pairing and delivery state uses the host-provisioned module DataDirectory. The supervisor passes parent
PID and creation FILETIME, performs control work off the host UI thread and never stops an existing
worker it attached to as standby. Shutdown withdraws questions and stops only the exact owned child.
No settings field accepts a fabricated MCP app source ID.

Updated Claude executables can remain visible through Windows-verified Anthropic
publisher observation. Observation alone has no receive or wake capability. With
"Wake existing Claude sessions" on, "Also wake updated Claude builds" (on by default,
the owner's decision of 2026-11-02) makes such a session deliverable when its signed
image is the one the native pipe's own server runs; turned off, delivery requires an
exactly pinned profile.
Observation rows follow the existing paired-directory sharing and per-session
private controls; their labels contain a generic client name and short session ID.
See `tools/AgentBroker.Worker/Adapters/ClaudeNativeProvider.cs` and
`ClaudeNativeAdapter.cs` for the separate discovery and delivery boundaries.

For authenticated peer health, use
`tools/agentbroker/Test-AgentBrokerPeerAuthentication.ps1 -WorkerProcessId <PID>`.
The local management check distinguishes an authenticated empty directory from
refused, failed and unperformed requests without returning session contents.
An older worker reports the diagnostic unavailable. Configuring an MCP client
does not register an agent inbox; a generic client registers through the broker
tools. Its presence lasts one minute and every successful call renews it; reusing
the same clientId and incarnation keeps its mailbox receiving for 24 hours after
its last call, so no renewal sidecar is needed between turns.

The Coding agents group and the tray show separate setup states per client (Claude Code, Codex):
broker, configuration, endpoint, tools, inbox and native wake. Check client setup runs only the
client's read-only `mcp get` for this entry, on demand. An inbox counts for a client only when its
own registration request identified that client. LAN firewall access shows the Windows network
category of the broker's LAN interface beside the rule's Private profile, and observed LAN requests
separately from the configured rule. See `AgentBrokerModule.Clients.cs` and
`tools/AgentBroker.Worker/LanProfile.cs`.

Connect preserves an existing entry and reports disabled, policy-blocked or mismatched
Codex configuration without replacing it. Existing Codex HTTP entries match only a verified
live, token-free broker connection. Token-required HTTP entries are preserved for connection
review. An unavailable or unverified live control connection instead leaves a recognized HTTP
entry not verified: refresh status, then choose Check client setup again. New Codex entries
use stdio; no temporary broker token is saved. A current Connect Claude MCP failure at
connection verification appears as Last setup action without starting a vendor CLI. Restart,
shutdown and cancellation prevent stale completions from changing the new connection's status.
A settings change outside the pane can leave the sanitized action result and recheck prompt
visible. Apply rebuilds the pane and discards an in-flight action's row and result. Run
**Check client setup** before retrying (`AgentClientSetup.cs`, `AgentBrokerModule.Clients.cs`,
`src/Portable/Wpf/OptionsWindow.cs`).

The packaged worker also supports a [one-request client](../../docs/agentbroker-client.md)
with explicit retained output and bounded typed handoff text. Blocking waits allow one
incoming wait and one receipt wait per exact address, with sixteen total per worker.
Uncertainty advisories describe historical delivery events and never automatically
resend a message. See the [broker guide](../../docs/agentbroker.md).

Build: dotnet build modules/AgentBroker/AgentBroker.csproj -c Release

The public SelfTest(out string detail) exercises real Init, Options, tray controls, pairing scope,
parent binding, cancellation and shutdown with injected fake transports and supervisor. It requires
`worker/AgentBroker.Worker.exe` beside the module but launches no worker, connects no socket and
reads no live agent credentials or conversations. The host's convention-loader harness additionally
requires the adjacent Worker DLL and hashes the whole worker payload. The normal module build
publishes that payload automatically; a copied module without it cannot pass these checks.
