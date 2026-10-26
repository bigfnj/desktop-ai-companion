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
publisher observation. Observation alone has no receive or wake capability; native
delivery still requires a separately supported profile and explicit opt-in.
Observation rows follow the existing paired-directory sharing and per-session
private controls; their labels contain a generic client name and short session ID.
See `tools/AgentBroker.Worker/Adapters/ClaudeNativeProvider.cs` and
`ClaudeNativeAdapter.cs` for the separate discovery and delivery boundaries.

For authenticated peer health, use
`tools/agentbroker/Test-AgentBrokerPeerAuthentication.ps1 -WorkerProcessId <PID>`.
The local management check distinguishes an authenticated empty directory from
refused, failed and unperformed requests without returning session contents.
An older worker reports the diagnostic unavailable. Configuring an MCP client
does not register an agent inbox; generic clients must register and renew their
one-minute inbox lease through the broker tools.

Build: dotnet build modules/AgentBroker/AgentBroker.csproj -c Release

The public SelfTest(out string detail) exercises real Init, Options, tray controls, pairing scope,
parent binding, cancellation and shutdown with injected fake transports and supervisor. It launches
no worker, connects no socket and reads no live agent credentials or conversations.
