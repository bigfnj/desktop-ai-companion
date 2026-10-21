# One-target Claude native peer smoke probe

This diagnostic has a metadata-only describe mode and a separate protected submit mode. It never reads
transcripts, scans credential directories, changes user configuration, restarts Claude, or creates a
credential fixture. Exceptions produce a safe error code rather than an unhandled .NET error dialog.

Build from the repository root:

```powershell
dotnet build tools\AgentBroker.ClaudeSmokeProbe\AgentBroker.ClaudeSmokeProbe.csproj -c Release --nologo -v:minimal
.\tools\AgentBroker.ClaudeSmokeProbe\bin\Release\net10.0-windows\AgentBroker.ClaudeSmokeProbe.exe --help
```

`--describe-target <exact-session-uuid> <explicit-pid> [explicit-pid ...]` checks at most 32 explicit
numeric registry JSON targets, selects only the exact session UUID, checks native OS creation equality,
and pins the public executable hash. Multiple matching owners fail closed. It prints the derived
credential TARGET PATH, PID/native creation token/image hash/classification and idle boolean, without
opening that target or a native pipe. The key-path derivation uses the default user-home config root.
Native idle timestamps describe state transitions, so their age does not expire an unchanged idle
session. A fresh read must still match the current OS process incarnation and verified public image,
with no future or pre-process activity timestamp.

`--probe-transport <exact-session-uuid> <explicit-pid> [explicit-pid ...]` uses that same exact metadata
selection, connects to its current-user native pipe, inspects server PID/start/user/public executable,
then closes it. It writes no auth or message bytes and never constructs a credential reader. Output
contains only a fixed stage, comparison booleans and numeric exception type/error codes. It prints no
exception message, registry contents or credential contents. Use this mode to diagnose transport
availability before requesting a protected live submission.

`--probe-pipe-owner <exact-session-uuid> <explicit-pid> [explicit-pid ...]` is a separate no-write
diagnostic that omits the .NET pipe-owner constraint. Before reading owner/DACL metadata it verifies
the actual connected server's current-user token, exact PID/native creation time, public image path
and pinned executable hash. It prints only owner-comparison/category booleans and broad foreign-write
ACL flags, never account SIDs. This diagnoses owner-constraint incompatibilities; it does not change
the production factory or authenticate a connection. No credential source is instantiated, and no
auth/user bytes are written.

`--smoke-authorized-test <exact-session-uuid> --pid <pid> --expected-start <native-filetime>
--expected-credential-path <exact-derived-path>` resolves metadata and public image identity again,
requires fresh idle, and verifies the exact reviewed path/PID/native creation value. It then connects
the current-user native message pipe, verifies server PID/start/user/image hash, reads that one exact
credential, and sends auth followed by one fixed benign peer prompt. It uses the peer token, never a
child token, sender-mode assertion, origin wrapper or permission override. All token/auth buffers retain
the adapter's zeroing rules. A stable UUID derives from the target/incarnation and fixed marker.

The prompt states that it is an authorized external-peer test, grants no tool/permission authority,
requests no tools, and asks for exactly `AGENTBROKER_CLAUDE_PEER_SMOKE_OK`. Socket success remains
`SubmittedUnconfirmed`; GUI observation is a separate check, and retry safety is not inferred.

Before an agent invokes submit mode, the platform must approve the exact resolved **H05** credential
read (and **W06** if under a workspace), command, native recipient, and fixed benign submission. The
flag name or a chat reply does not confer that approval. Do not invoke submit mode to discover a path.
The deterministic Claude test harness tests pure selection/preflight and fake transport only.
