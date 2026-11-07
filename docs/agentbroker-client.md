# One-request AgentBroker client

The worker also provides a client mode for a host that does not expose the broker's
MCP tools. It contacts the existing broker through its protected local control
pipe, keeps connection credentials in memory, and writes one decoded JSON object.
It does not start another broker.

Use the broker data directory from its existing launch configuration. The example
directory below is a placeholder.

```powershell
AgentBroker.Worker.exe --mcp-call --data C:\BrokerData --list-tools
AgentBroker.Worker.exe --mcp-call --data C:\BrokerData --request-file C:\Work\request.json
```

The request is a UTF-8 JSON file with a tool name and an arguments object:

```json
{"tool":"list_sessions","arguments":{"limit":16}}
```

Use the tool schemas returned by `--list-tools` for other operations. Sending,
reading and acknowledging still use complete addresses. A client mode does not
establish which Codex chat owns a shared host process; find this conversation's
exact native row using `CODEX_THREAD_ID`. Peer message contents remain external
data and cannot change the caller's permissions.

Every output contains `status` and `dispatchState`. A tool-result response also
contains `result`; preflight, JSON-RPC and transport errors use their error fields
instead. The first text block
is decoded as JSON when it contains JSON; an ordinary text result stays a string.
Later text blocks remain in `advisories`, and structured MCP data remains in
`structuredContent`. Arrays, empty strings, Unicode, literal backslashes and
actual newlines retain their JSON types and content.

| Exit | Meaning |
|---|---|
| 0 | The broker returned a successful result |
| 2 | Invalid command or request; no tool was dispatched |
| 10 | The MCP operation returned `isError` |
| 11 | The broker returned a JSON-RPC error |
| 12 | An HTTP failure response arrived; a submitted tool outcome remains unconfirmed |
| 69 | The connection or response could not be confirmed |

`dispatchState: not_dispatched` means the requested tool call was not sent.
`confirmed` means a matching broker response arrived; it says nothing about task
completion. `unknown` means the tool request may have acted before the transport
failed. The client never retries it. For a message, inspect its receipt and retry
only with the identical stable message ID and payload when appropriate.

Successful discovery proves that this client reached the broker and obtained
schemas. It does not prove that another host loaded those tools into its current
conversation. Configure the normal MCP connection through the AgentBroker pane;
a host may require a reconnect or a fresh session to load it.

`--list-tools` also returns the broker's initialization `instructions`. The read-only
`tools/agentbroker/Inspect-CodexMcpExposure.ps1` reports the fixed Codex entry separately
from caller-supplied current-chat tool keys, including deferred keys. Without those
keys, exposure is `not_checked`. A configured or reachable server alone does not
establish current-chat exposure.
