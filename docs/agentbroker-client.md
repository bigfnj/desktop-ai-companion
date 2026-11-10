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
AgentBroker.Worker.exe --mcp-call --data C:\BrokerData --request-file C:\Work\request.json --output-file C:\Work\response.json
```

The request is a UTF-8 JSON file with a tool name and an arguments object:

```json
{"tool":"list_sessions","arguments":{"limit":16}}
```

Both `--mcp-call` and `--handoff-prepare` accept one leading UTF-8 byte order mark
in a request file. A second is invalid JSON. Invalid Unicode in a top-level request
field name returns exit 2 before dispatch: `invalid_request` for `--mcp-call`, or
`invalid_handoff` for `--handoff-prepare`.

An `--mcp-call` request file is limited to 128 KiB and a nesting depth of 32. Since
1.10.3 the client encodes the complete JSON-RPC query before it connects, so a request
at that depth still travels. A deeper request, or one whose encoded query exceeds the
worker's 128 KiB body limit (escaping can make the encoded text longer than the
file), returns `invalid_request`, exit 2 and `dispatchState: not_dispatched`.
`dispatchState: unknown` appears only after the query's own send has been attempted.

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
| 74 | The requested local output could not be retained |

`dispatchState: not_dispatched` means the requested tool call was not sent.
`confirmed` means a matching broker response arrived; it says nothing about task
completion. `unknown` means the tool request may have acted before the transport
failed. The client never retries it. For a message, inspect its receipt and retry
only with the identical stable message ID and payload when appropriate.

With `--output-file`, the client saves the unchanged decoded response before adding
`retention.path`, `retention.sha256` and `retention.bytes` to stdout. The destination
must be a new absolute drive-letter path in an existing directory on a local NTFS
volume, without reparse points. UNC and device paths, alternate data streams and
control characters are refused, as are the reserved names CON, PRN, AUX, NUL,
COM1-9 and LPT1-9. Use the exact capitalization, the drive letter included, when a
case-sensitive folder occurs in the path. A SUBST drive is not supported: the client
compares the directory's final path with the path you typed, and a SUBST letter
resolves to another drive, so use the backing drive's path instead. Original path components ending in a dot or space are
refused before path normalization. Exact `.` and `..` navigation components remain
supported. The client flushes an owned temporary file and publishes it without
replacing an existing destination. Flushing is not a guarantee against every
storage or power failure.

A relative `--output-file` is an argument error (`invalid_arguments`, exit 2).
A destination rejected by output preflight returns `output_error`, exit
74 and `dispatchState: not_dispatched`. Since 1.10.2 the preflight makes the same
directory checks as saving (plain local NTFS directories, the exact directory
identity, case-sensitive folders), so such a destination is refused before any tool
is dispatched. Saving repeats every check, because the directory can change between
the two. The preflight proves neither permission to create a file in the directory
nor free space: even when its directory checks pass, a directory you cannot create
files in, or a full volume, can stop the save after the broker operation may already
have completed. If publication fails after the call, the
output retains the known dispatch state and includes the complete original client
response under `response`, including its `status` and either its `result` or error
fields. The process exits 74 for this retention failure, replacing the operation's
original exit code. Inspect `response.status` and `response.dispatchState` for
that operation's outcome; the copied dispatch state can remain `unknown`. Keep
that response using a new file. Do not repeat a confirmed operation because its
output file could not be saved. Retention does not acknowledge messages: retain
the content you need before explicitly acknowledging its actual receipt.

If removal of the owned output file fails during error handling, the error JSON
also includes `"cleanup":"temporary_not_removed"`. Inspect the output directory
for a remaining file. The original response and dispatch state still determine
whether the broker operation acted.

## Typed handoff text

`--handoff-prepare` validates a local handoff packet and emits ordinary text for
the existing `send` or `reply_to_message` operation. It does not contact the broker.
Use a UTF-8 `.json` request file at an absolute path; the extension match is
case-insensitive. The file and its parent directories must not be reparse points.

```powershell
AgentBroker.Worker.exe --handoff-prepare --request-file C:\Work\packet.json --output-file C:\Work\packet.txt
```

```json
{
  "kind": "agentbroker.handoff.v1",
  "taskId": "review-42",
  "state": "request",
  "summary": "Review the attached result.",
  "artifacts": [
    {
      "role": "evidence",
      "path": "C:\\Work\\result.json",
      "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
      "bytes": 123
    }
  ]
}
```

The example hash is a placeholder. Supply the actual hash and length for your
artifact. Preparation validates metadata without opening artifact paths. A
recipient must independently decide whether it is authorized to inspect an
artifact and whether its bytes match those claims.

Successful preparation returns `status: ok`, `dispatchState: not_dispatched`,
`text`, its UTF-8 `textBytes`, and its `sha256`. The `retention` member is null
without `--output-file`; otherwise it contains `path`, `sha256` and `bytes`, and
that file contains exactly the prepared message text. Put that text in an ordinary
broker message. Task IDs, summaries, artifact roles and states remain
self-declared peer data and do not establish identity or task completion.
The output destination follows the same path and cleanup rules as `--mcp-call`.

| Exit | Status | Meaning |
|---|---|---|
| 0 | `ok` | Message text was prepared; no broker was contacted |
| 2 | `invalid_arguments` | The command arguments, request path or extension are invalid |
| 2 | `invalid_handoff` | The request could not be read or validated, including a reparse-point path |
| 74 | `output_error` | The requested output file could not be retained |

Every handoff result has `dispatchState: not_dispatched`. Its exit 0 means local
preparation succeeded, rather than that a broker returned a response.

States are `request`, `progress`, `result` and `cannot_do`. An empty artifact list
is valid. Input is bounded to 128 KiB and depth 8, with at most 16 artifacts; the
normalized message must fit the broker's 16 KiB text limit. Every top-level and
artifact field shown in the example is required, and string values must be
non-blank. Unknown or duplicate fields, malformed hashes, negative byte counts
and invalid Unicode are refused. Control characters are refused except for CR,
LF and tab in the summary. Prepared artifact `sha256` values use uppercase hex;
compare hash values without depending on letter case.
These packets add no fields to the federation protocol.

The task ID is bounded to 128 UTF-16 code units, the summary to 8192, each artifact
role to 40 and each path to 4096. A supplementary Unicode character uses two code
units. Byte counts must be nonnegative 64-bit integers.

Successful discovery proves that this client reached the broker and obtained
schemas. It does not prove that another host loaded those tools into its current
conversation. Configure the normal MCP connection through the AgentBroker pane;
a host may require a reconnect or a fresh session to load it.

`--list-tools` also returns the broker's initialization `instructions`. The read-only
`tools/agentbroker/Inspect-CodexMcpExposure.ps1` reports the fixed Codex entry separately
from caller-supplied current-chat tool keys, including deferred keys. Without those
keys, exposure is `not_checked`. A configured or reachable server alone does not
establish current-chat exposure.
