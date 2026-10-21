# Claude native peer adapter, Phase 0

This package-free library is a protocol and identity prototype. It has no installer, executable entry
point, MCP server, discovery loop, or user configuration writer. The separately built metadata diagnostic
reads only explicit numeric registry JSON targets. Tests use fake connections and in-memory
synthetic credentials only. They do not contact Claude, open a native pipe, create a credential fixture,
or invoke the protected production credential reader.

Three exact public native executable profiles are supported:

| Public runtime | SHA-256 |
| --- | --- |
| Standalone `2.1.292` | `EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23` |
| VS Code Windows x64 `2.1.304` | `019E5174D5D954585EDB1A946FE280E44A973050B4897034ADF0E16306F85FDD` |
| VS Code Windows x64 `2.1.306` | `F6BC9DE38F507C36C9A3BB0085A59A27E32156E2208252BF1707C4E3EF309114` |

These profiles do not establish the version or behavior of a Desktop-bundled runtime.
Source equivalence and reproducible static inspection are recorded in [VERIFICATION.md](VERIFICATION.md).
Unknown versions or hashes are refused before a connection or credential read. Binary hash identifies
the exact verified engine version; caller-supplied names are not version proof.

Canonicalization follows the inspected public native OJ/YE behavior: accept only a local Windows message
pipe with `cc-msg-` and 32 hex digits, retain an optional `LOCAL` namespace, and ASCII-lowercase the full
canonical `\\.\pipe\...` path before SHA-256. The credential filename is the server PID, that digest,
and the native suffix; its directory is NFC-normalized `CLAUDE_CONFIG_DIR/sessions`, or
`<explicit user home>/.claude/sessions`. Derivation is pure and neither enumerates nor accesses files.

The prepared production transport uses a current-user-only named pipe and checks the connected server's
PID, executable path, current-user SID, exact image hash/version profile, and native creation FILETIME.
FILETIME is the unsigned result of Win32 `GetProcessTimes`, not `.NET DateTime.Ticks`. Identity is checked
again after credential parsing and before any token is transmitted. The ordinary no-write transport
probe passed against a live VS Code 2.1.306 test recipient; credential access and user input remain
separate protected/live checks.

On Windows the factory validates pipe ownership against `WindowsIdentity.User` explicitly. The .NET
`CurrentUserOnly` convenience option compares against the token's default `Owner`, which can differ
from `User` and incorrectly reject a native pipe owned by the current user. The explicit policy requires
an owner equal to the current user, a present non-null DACL, and no write/control grants to identities
other than that user, SYSTEM or Administrators. Unknown/callback ACEs fail closed. The actual connected
server's process token must independently be the current user. These checks repeat during reinspection
before credential authentication; no credential or message is sent merely to test a pipe.

Each connection hashes a retained read-only public executable handle once. Its sharing mode denies
writes and replacement until that short-lived connection is disposed; reinspection checks the same
handle's resolved identity along with current server PID/start/user/path/ACL. No hash trust persists
across connections. This avoids a redundant full-image pass while keeping the bytes immutable through
authentication. Discovery uses public version metadata only to refuse unsupported updates cheaply;
a supported version still needs the exact full-image hash. Unknown versions never gain trust from
version text alone.

Credential records are strictly bounded to 4,096 bytes and accept only `peerToken` plus optional native
process-start/domain metadata. The parser rejects duplicates, unknown fields, invalid tokens, trailing
documents, and mixed legacy/native start fields. It does not turn the peer token into a managed string.
Input buffers are consumed/zeroed on success and failure; retained token and temporary auth buffers are
zeroed when their ownership ends. The Windows adapter requires the record's exact native creation token
and refuses missing, noncanonical, mixed, or mismatched start metadata. Either exclusive native field
name may carry that exact FILETIME; no epoch conversion grants authority. `pidDomain` is a namespace
hint, never authentication.

Native authorization remains distinct from broker pairing/session sharing. The caller must supply
recipient activity from fresh local metadata, not remote payloads. The registry does not expose mode or
effective inbound settings. Unknown/busy activity always stays held. With `DelegateNativeAdmission=false`
(the default), unknown/bypass recipient mode stays held unless a separately verified setting accepts.
Enabling an adapter may explicitly set `DelegateNativeAdmission=true` for an idle candidate: the native
peer classifier and inbound gate then decide using the recipient's actual current mode/settings. This
attempt remains `SubmittedUnconfirmed`, including when native code holds or refuses it. Any explicitly
supplied local deny/hold is retained. The adapter emits no `from_mode`,
origin wrapper, permission change, forged `selfSent`, or exported child token. A foreign broker message
must never be sent using a child token that native code considers self-sent.

The wire output is only auth JSONL followed by one minimal user JSONL with the caller's stable UUID,
exact recipient session ID, and escaped message text. A completed socket write returns
`SubmittedUnconfirmed`, not delivered/completed/visible. A user-frame write interrupted partway also
remains unconfirmed. No immediate same-connection delivery ACK has been established. Native asynchronous
status/correlation and ordinary Desktop/VS Code UI visibility still need separate verification. This
adapter does not implement durable deduplication; callers must retain the stable UUID and reconcile
uncertain submissions instead of blindly resending.

`NativeSessionMetadata.Parse` is a pure, 262,144-byte-bounded parser for public numeric PID registry JSON.
It returns candidate pipe/session/creation/status hints, not authentication, readiness, or authorization.
Duplicate fields, noncanonical numeric filenames/session IDs, mixed creation fields, malformed records,
and nonlocal/nonmessage pipes fail closed. An exclusive `procStart` hint can carry the native FILETIME
in current publishers, but production discovery must compare it exactly to the OS creation value;
the parser performs no epoch conversion. Display names and transcript/log paths are ignored.
Native status timestamps are state-transition times, with no periodic idle heartbeat. Fresh exact-record
reads, live process creation and public-image checks determine whether the hint belongs to the current
incarnation. Future or pre-process timestamp values are refused; an unchanged idle state does not expire
merely because it is old. The status hint never grants atomic admission authority.

The formatted native peer text cap is 128 KiB UTF8, while Core raw peer content stays 16 KiB. The inspected
native 1 MiB JSONL line guard bounds auth plus escaped user frame; the stricter byte cap allows routing
metadata and control-character escaping without truncation. Larger envelopes refuse before transport
or credentials. This size proof does not establish throughput or live admission.

The separate `MetadataProbe/ProbeNativeMetadata.csproj` diagnostic accepts at most 32 explicit canonical
PIDs. It opens only `<current user home>/.claude/sessions/<PID>.json`, verifies every component and the
final handle's resolved path/attributes before reading, refuses reparse points/hard links, and performs
no directory enumeration, key lookup, native pipe connection, vendor listing, cleanup, or transcript read.
It supports the default config directory only. Each record is capped at the inspected native 256 KiB
bound; output contains aggregate counts/booleans only, with no exception text, paths, PIDs, or session IDs.
Its real reader is separate from and never invokes the protected credential source. Build/run from the
repository root after reviewing the exact PID list:

```powershell
dotnet build tools\AgentBroker.ClaudeAdapter\MetadataProbe\ProbeNativeMetadata.csproj -c Release --nologo -v:minimal
.\tools\AgentBroker.ClaudeAdapter\MetadataProbe\bin\Release\net10.0-windows\ProbeNativeMetadata.exe --help
# Replace 12345 with an explicitly identified native Claude PID. This reads one numeric JSON target.
.\tools\AgentBroker.ClaudeAdapter\MetadataProbe\bin\Release\net10.0-windows\ProbeNativeMetadata.exe 12345
```

`ProtectedPeerCredentialSource` is prepared code, not authorization to run it. Its real target matches
protected pattern **H05** (home `*.key`) and **W06** when inside a workspace. Before an agent invokes it,
the exact resolved target/read command must receive action-specific human approval through the platform
approval workflow. A chat reply or an application-level pairing prompt does not satisfy that requirement.
No real or fixture credential file has been read or created by these tests. The production reader rejects
reparse components, opens the final component without following a reparse point, verifies the final
handle's resolved path/attributes, and bounds/zeros its read buffer. These OS checks have not been
exercised against a live credential file.

Build and run only the deterministic suite from the repository root:

```powershell
dotnet build tests\AgentBroker.Claude.Tests\AgentBroker.Claude.Tests.csproj -c Release --nologo -v:minimal
.\tests\AgentBroker.Claude.Tests\bin\Release\net10.0-windows\AgentBroker.Claude.Tests.exe
```
