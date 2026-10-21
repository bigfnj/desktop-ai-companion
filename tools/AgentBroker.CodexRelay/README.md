# AgentBroker Codex relay, Phase 0

This is a transport prototype, with deterministic synthetic-engine tests. It has not been configured in
Codex Desktop or VS Code and does not establish that an ordinarily opened session can be woken safely.
No broker, MCP listener, remote pairing, user configuration installer, or background service is included.

The relay launches exactly one explicitly selected executable. The caller must supply its exact SHA-256
and expected app-server engine version. Hash verification happens before launch, without a second
`--version` process. The original GUI performs its own `initialize`; injection remains disabled unless
the response's `userAgent` contains the expected exact version after its first slash. An unapproved
handshake disables injection while leaving the GUI's original transport running.

The child receives the relay's current working directory, inherited environment, and every argument
after `--` with its original boundary. JSONL frames travel unchanged, including whitespace and line
endings. Engine stderr is forwarded, and relay failures report exception types only. There is no raw
protocol log. The prototype rejects frames over 16 MiB rather than truncating or buffering without a
limit. A later production transport must choose its frame/backpressure policy explicitly.

Example invocation, for a caller that has already reviewed and pinned the original executable:

```powershell
.\AgentBroker.CodexRelay.exe --engine C:\approved\codex.exe --engine-sha256 <64-hex-digits> --engine-version 0.161.0 --pipe AgentBroker.Codex.<unique-instance> -- app-server
```

The relay's named pipe is local and uses `PipeOptions.CurrentUserOnly`. It is a narrow adapter, not an
arbitrary engine RPC proxy. Its JSONL operations are:

- `{"operation":"status"}` returns the relay incarnation, verification state, and observed thread IDs/states.
- `{"operation":"send","messageId":"...","incarnation":"...","threadId":"...","text":"..."}` injects only `turn/start`
  into that exact observed, loaded, idle thread. Text is limited to 65,536 UTF-16 code units. No approval,
  sandbox, model, working-directory, or permission setting is overridden.

`messageId` is required. Repeating the same ID with the same destination and text returns the original
admission without a second engine request. Changed content is refused. The per-incarnation admission
history is bounded at 4,096 and never silently evicted; new admissions are refused when it fills. A send
reports engine admission and its turn ID, never a claim that the turn completed or appeared in the GUI.

Only GUI `thread/start`/`thread/resume` results and engine `thread/started` notifications establish a
loaded target. `thread/read` and `thread/list` do not. Unknown status refuses injection. Turn/status/closed
notifications update the target state. Pagination and `thread/loaded/list` reconciliation are not
implemented; this prototype never issues a discovery request or starts/resumes a thread on its own.
Interrupted/failed completions and generic engine errors never establish idle. A later explicit idle
status is required. GUI observation and broker admission share the serialized engine-stdin boundary.

Injected IDs use a random relay namespace, increasing sequence, and GUI-ID history checks. Only responses
owned by a recorded injection are suppressed from GUI stdout. Unknown responses pass through, including
ones with similar IDs. An exact adversarial collision terminates the relay rather than ambiguously
routing a response. Native engine requests, approval prompts, GUI decisions, and all notifications pass
through unchanged. A timed-out request keeps its response ownership, so a late response cannot leak into
the GUI request map. Timeout is not a claim that the agent did not receive or execute a request.

GUI EOF and engine exit close the relay lifetime. A bounded shutdown terminates only the child process
tree the relay launched. Early stderr-forwarding and control-listener failures are supervised alongside
GUI input, engine output and process exit. Output draining is bounded. Disposal is idempotent and waits
for the relay lifetime before releasing process handles; it does not dispose a semaphore under a
canceled writer. This prototype is not a production supervisor: parent-death cleanup, elevation
boundaries, authentication beyond current-user IPC, exact caller registration, thread incarnation/lease
policy, and live GUI/extension acceptance still need integration review.

Direct `turn/start` is an engine transport operation. It may bypass the Desktop UI's durable message
admission/queue ownership, so these tests do not prove a message will appear correctly in an ordinary
Desktop conversation. A supported exact-thread queue/owner API is preferable when available and must be
verified before selecting the production adapter.

The inspected Codex Desktop CLI-path override changes two Windows sandbox-related environment variables
before the relay is launched. Inheriting the environment that reaches this relay is not proof of parity
with the default Desktop launch. Live acceptance must compare effective sandbox/approval behavior and
reject configuration when parity cannot be established.

Build and run the package-free console harness from the repository root:

```powershell
dotnet build tests\AgentBroker.Relay.Tests\AgentBroker.Relay.Tests.csproj -c Release --nologo -v:minimal
.\tests\AgentBroker.Relay.Tests\bin\Release\AgentBroker.Relay.Tests.exe
```

The harness launches only itself as a synthetic engine. Scratch files stay beneath its ignored build
output. Tests cover executable/handshake rejection, one original process, raw forwarding, inherited
context, approval preservation, exact loaded-thread targeting, stale/busy/closed targets, IPC reconnect,
late response suppression, and exit propagation. These checks establish transport invariants, not vendor
GUI wake support. The current harness passes 40 assertions, including channel failures and active disposal.
