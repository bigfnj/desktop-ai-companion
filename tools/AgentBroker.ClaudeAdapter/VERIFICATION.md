# Public runtime inspection

The VS Code Windows x64 `2.1.304` profile was checked on 2026-10-21 against the public executable at
`<user home>/.vscode/extensions/anthropic.claude-code-2.1.304-win32-x64/resources/native-binary/claude.exe`.
SHA-256: `019E5174D5D954585EDB1A946FE280E44A973050B4897034ADF0E16306F85FDD`.
The extension's public package manifest also identifies version `2.1.304`. This does not prove which
installed version currently owns a session; the connected server image must match its exact profile.

The installed executable embeds public JavaScript source. These are byte offsets in this exact binary,
using the inspector's Latin-1 view, not source line numbers. Minified symbols are version-specific.
No credential, transcript, environment/configuration file, or live native pipe was read for this check.

| Behavior | Public source evidence |
| --- | --- |
| Config root | `Le`, offset 242122047: config override or home `.claude`, NFC-normalized |
| Pipe canonicalization | `d1r`, offset 244052632, ASCII lowercase; `Lne`, offset 244053115, local Windows pipe parser with optional `LOCAL`, final-dot/space and extended-path slash rejection |
| Credential target/schema | `Mne/TR/Z/V`, offset 244895862: config `sessions`, canonical-pipe SHA-256, PID/digest filename; preceding schema at 244895623 caps 4096 bytes and validates peer-token/start/domain fields |
| Auth classification | `f3s`, offset 244899362: peer and child tokens classify separately |
| Registry parser/bounds | `bbt/M3s/D3s`, offset 244822946: 262144-byte read cap, canonical PID filenames, known kind/status enums; `C/j`, offsets 250471200/250472320: numeric JSON records from sessions only |
| Native inbound admission | `H/S/k`, offset 273119000: explicit settings, kill switch, unknown mode, self-sent status, and prompting/bypass class determine accept/hold/refuse |
| Raw user envelope | `we`, offset 273148758: nonempty content and exact recipient session check, caller UUID, raw peer origin, native gate before queue admission; raw `from_mode` is not propagated |
| Self-sent classification | `N`, offset 273167350: child-token presentation is a separate input to the native Windows classifier; peer tokens do not obtain child-token privilege |

The raw user handler and classifier boundary were independently checked by the runtime architecture
agent against the same public image. The adapter deliberately omits richer origin wrappers, sender
privilege assertions, attachment materialization, exported child tokens, native control commands, and
unverified asynchronous receipt correlation.

Use the repository inspector with an explicit public executable and bounded literal needles/windows:

```powershell
node tools/AgentBroker.ClaudeAdapter/InspectPublicRuntime.mjs --help
node tools/AgentBroker.ClaudeAdapter/InspectPublicRuntime.mjs <public-claude.exe> 'function Lne(' 'peerToken:o().regex(be)' 'async function we(e,n,i,d,c)'
```

The inspector only accepts a final executable named `claude.exe`; it has no credential/config readers,
vendor command invocation, source execution, extraction output, or directory scan. A binary version/hash
change requires independent inspection before adding another profile. Reusing old minified symbols is
insufficient. Ordinary GUI admission and credential-reader OS behavior remain separate live checks.

The later approved metadata-only probe read six exact numeric registry JSON targets. Five parsed as
candidate metadata and one was unavailable; no credential or pipe access occurred. Current records
used an exclusive `procStart` field. A boolean-only process comparison confirmed that one such hint
equals native OS creation FILETIME exactly, rather than UTC/local `.NET DateTime.Ticks`. Metadata now
allows either exclusive field name, with production discovery requiring exact OS FILETIME equality;
credential parsing remains separately strict and no real credential record was opened. Public native
line guards are 1 MiB chars in both profiles: `Xsn` for 2.1.304 and `LKt` for 2.1.292.

## VS Code 2.1.306 update

After the client restarted on 2026-10-21, the running VS Code engines used the public extension
`anthropic.claude-code-2.1.306-win32-x64`. Its package manifest identifies version `2.1.306`; the native
public executable SHA-256 is `F6BC9DE38F507C36C9A3BB0085A59A27E32156E2208252BF1707C4E3EF309114`.
The following boundaries were checked independently before adding this exact profile. No native
credential, transcript, environment/configuration file, or live pipe was accessed.

| Boundary | Public native-source offset |
| --- | --- |
| Pipe spelling and ASCII lowercase | `cre`, 245907175; `gWr`, 245906692 |
| 4096-byte peer credential schema and canonical PID/pipe-digest target | schema and `NR/Z/V`, 246754899 |
| Peer token distinct from child token | `iXs`, 246758596 |
| 262144-byte registry bound and known status enum | `_St`, 246680105 |
| Native accept, hold, refuse and unknown/bypass mode policy | `I/H/S/k`, 275244000 through 275246500 |
| Exact recipient session, stable UUID, default next priority and native policy before enqueue | `J/we`, 275273452 |
| Native Windows child-token classification | `N`, 275292223 |
| 1 MiB-character native JSONL line bound | `yan`, 252260661; ingress check, 275281786 |
| Registry status publication and deduplication | `LGe`, 247578494; SDK `YE`, 281386210 |

Registry activity timestamps describe the last state transition. They are not session heartbeats:
the SDK status publisher explicitly skips repeated identical states, and an unchanged idle session
can retain its timestamp indefinitely. The separate fleet-view heartbeat signals a registry watcher,
not each recipient's liveness. Accordingly, discovery freshly reads the exact numeric JSON record,
requires its creation token to equal the current native OS process creation FILETIME, checks the exact
public image profile, and rejects future or pre-process activity timestamps. It does not expire a valid
idle state merely because it is more than two minutes old. Submission refreshes that metadata again
before the current-user pipe/server identity and credential boundary. Status remains a hint; native
peer admission and queue scheduling remain authoritative, and there is no fabricated atomic idle ACK.

The live no-write transport diagnostic also exposed a .NET owner-comparison incompatibility. The
native pipe's owner matched the current Windows user SID, and its actual server process user, PID,
creation time and exact public image all matched. The pipe owner did not match the calling token's
default Owner SID. Official [.NET 10 client implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Windows.cs#L185)
confirms that `CurrentUserOnly` compares the pipe owner against `WindowsIdentity.Owner`. The adapter
now performs its own stricter current-user ownership/DACL and actual server-process identity checks,
then repeats them before auth. A live diagnostic with that production factory passed every identity
comparison while reading no credential and writing zero bytes. Pure synthetic ACL checks cover
foreign ownership, foreign write grants, null DACL and the valid current-user pipe policy.

Public-image performance measured on 2026-10-21: the 2.1.306 executable is 295,628,960 bytes. Three
SHA-256 passes took 590.2, 407.8 and 417.8 ms, with a 417.8 ms median. Production now retains one
read-only, non-write/non-delete-shared public image handle for the lifetime of one native connection.
It hashes that handle once and verifies the immutable handle's resolved identity on reinspection,
alongside the server process/pipe checks. A live zero-write/no-credential probe reported all identity
comparisons matching, with reinspection taking 0.962 ms. That is a measured reinspection cost, not a
claim about total model response time. Synthetic tests verify that writers and replacement are refused
while the handle is held, an already writable image cannot acquire an attestation, a different image
path fails, and disposal releases the sharing restriction. There is no cross-connection file cache.
