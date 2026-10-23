# Isolated MCP hybrid transport probe

This standalone SDK 2.2.0 fixture has no product project references. It never reads
broker/client configuration, creates identity material, or connects to an existing
broker. Default invocation prints help and does not listen. Its only dependency
restore source is its own directory, so builds can reuse packages already cached
locally but cannot download missing packages.

```powershell
dotnet build tools/AgentBroker.McpTransportProbe/AgentBroker.McpTransportProbe.csproj
dotnet tools/AgentBroker.McpTransportProbe/bin/Debug/net10.0/AgentBroker.McpTransportProbe.dll --run-isolated
```

Explicit run starts a temporary HTTP listener on `127.0.0.1` with an OS-assigned
port. Calls and teardown are bounded. It verifies an initialized 2025-11-25 SDK
client gets a session, keeps a GET stream, and automatically answers a server
ping without a custom request handler. It also checks stateless 2026-07-28 calls,
required routing headers and per-request metadata, and refusal of legacy no-session
raw calls. A failed assertion reports its fixed check name.

The mixed old initialize-body/new HTTP-header case is reported separately. It is
not used as evidence of valid negotiation or a supported migration technique.
Output contains fixed verdicts/counts, not session IDs, HTTP bodies or endpoint
ports. This fixture does not change AgentBroker's production transport mode.
