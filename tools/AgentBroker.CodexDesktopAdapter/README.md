# Codex desktop owner adapter

This package-free adapter connects to the existing local desktop coordination pipe. It never starts or resumes an engine or conversation. The production endpoint is fixed to `\\.\pipe\codex-ipc`; custom in-memory transports are internal to synthetic tests. Exact public compatibility profiles cover application packages `26.1016.6573.0` and `26.1016.8622.0`. Discovery can also observe a newer package read-only when Windows verifies its OpenAI signature in the protected WindowsApps location.

It pins the supported Windows application package, public executable/archive hashes, connected server PID, creation FILETIME and same-user process identity. It rejects ACL write grants to principals other than this user, SYSTEM and Administrators. It pins a discovered existing thread owner client plus a new adapter incarnation and rediscovers that exact owner before each send. Unknown versions and capabilities fail closed. The executable's Electron version is checked separately from the package's application version.

Build and run the connection-only probe from the repository root:

```powershell
dotnet build tools/AgentBroker.CodexDesktopAdapter/AgentBroker.CodexDesktopAdapter.csproj -c Release
dotnet tools/AgentBroker.CodexDesktopAdapter/bin/Release/net10.0-windows/AgentBroker.CodexDesktopAdapter.dll --probe-connection-profile
dotnet tools/AgentBroker.CodexDesktopAdapter/bin/Release/net10.0-windows/AgentBroker.CodexDesktopAdapter.dll --probe-connection-trust
dotnet tools/AgentBroker.CodexDesktopAdapter/bin/Release/net10.0-windows/AgentBroker.CodexDesktopAdapter.dll --probe-thread <exact-thread-UUID>
dotnet tools/AgentBroker.CodexDesktopAdapter/bin/Release/net10.0-windows/AgentBroker.CodexDesktopAdapter.dll --observe-directory
dotnet tools/AgentBroker.CodexDesktopAdapter/bin/Release/net10.0-windows/AgentBroker.CodexDesktopAdapter.dll --probe-thread-metadata <authorized-test-UUID>
dotnet tools/AgentBroker.CodexDesktopAdapter/bin/Release/net10.0-windows/AgentBroker.CodexDesktopAdapter.dll --verify-vscode-profile
dotnet run --project tests/AgentBroker.Desktop.Tests/AgentBroker.Desktop.Tests.csproj -c Release
```

`--probe-connection-profile` connects to the fixed existing pipe, runs the same
connected-process, user, ACL and public-image checks, and closes it. Success
reports the typed `Desktop` or `VsCode` router verification branch. It sends no
initialization, discovery or model input, reads no native protocol stream and
reports no process, client or conversation identifiers. The five-second timeout
bounds pipe connection; public-image hashing is outside that connection bound.
A verified router does not establish a recipient owner or a working delivery route.

`--probe-connection-trust` runs the same connect, verify and close sequence under
the discovery observation policy and also reports `trust`: `ExactPinned`, or
`PublisherSigned` with the failed exact-pin code and numeric installed version.
It sends nothing on the pipe either. A publisher-signed router is observation
evidence only: its sessions have no delivery capability unless update-tolerant
delivery was explicitly enabled (see the compatibility document).

The VS Code manifest check hashes all functional JSON content with object keys
sorted ordinally and array order preserved. Only the root `__metadata` value,
which VS Code changes during installation, is excluded. Duplicate keys, invalid
JSON and oversized manifests are rejected. JavaScript and native engine image
hashes remain exact requirements. The renderer bridge hash was checked against
the public Marketplace package for the exact extension version and Windows x64
target. Code routers accept only the separately pinned `1.143.0` and `1.143.1`
executable version/hash pairs, with the same extension and process checks.

Other probe output uses `compatibilityBaselineVersion` for the original supported
desktop package constant; that value does not identify the actual connected router.

`--probe-thread-metadata` uses a separate version-pinned router connection and sends
one version-1 `thread-stream-following-status-requested` broadcast for the supplied,
already known UUID. Its post-initialization observation reads for at most two seconds
or 256 frames, then closes the
connection even if cancellation interrupted a frame. It never sends `following:true`,
history, resume or model input. Output contains only bounded counts and booleans for
that exact target; other UUIDs, source IDs, host names and conversation fields are
not printed. Connected/disconnected fields mean observed status announcements,
not authenticated client identity or an owner binding. A missing announcement
remains ambiguous. The global running-Code profile result is reported separately;
a failure still withholds delivery. Run owner discovery separately on a fresh
connection after this diagnostic, since its stream is deliberately not reusable.

`--smoke-authorized-test <exact-test-thread-UUID>` and `--smoke-external-peer-test <exact-test-thread-UUID>` are separate mutating modes. Use them only after the person authorizes the dedicated ordinary GUI conversation. Each sends one generated benign prompt requesting a unique reply marker and no tools, using inherited GUI settings. Their result is native admission evidence; visible completion must be verified separately. There is no arbitrary-text CLI mode.

When the person explicitly asks to locate their prepared test view, `--find-ready-smoke-target` briefly follows at most 32 already-open candidates using verified native connections. It matches only the fixed completed readiness exchange in bounded live snapshots, discards snapshot bodies immediately, and prints only matching IDs plus schema counts/booleans. It sends no input and opens no persisted transcript. More than one match is ambiguous and cannot authorize routing. `tools/agentbroker/find-codex-smoke-metadata.py` is a separate read-only diagnostic using the same fixed readiness predicate in the thread metadata database; it prints no other prompt or title.

The caller API `SubmitAuthorizedPlaintextAsync` requires explicit `EnableAuthorizedPlaintextDelivery` and exists for isolated authorized tests. It accepts plain text and a unique message ID only. It cannot supply permissions, model changes, injected response items, tool output, approval decisions or arbitrary RPC methods.

`SubmitExternalPeerAsync` requires explicit `EnableExternalPeerDelivery`. It builds a fixed, clearly labeled external request with its complete content quoted as JSON. Both the ordinary GUI prompt and one typed Responses `message` item contain that text. The item has role `user`, not an assistant/developer/system role. It invents no tool call, tool result, MCP widget, account identity or permissions. The duplicate text makes the entire request visible in the GUI while selecting the owner's nonempty-context guard.

Only the external path exposes `NativeIdleGuard`. The pinned owner checks for idle before creating a pending record and again immediately before context injection. The exact native busy error returns `DeferredNotSubmitted` before either injection or turn dispatch, allowing bounded same-ID backoff. Other failures remain uncertain. The owner marks the pending GUI turn in progress, so ordinary GUI input follows its existing queue behavior. This is an owner guard, not an engine-wide transaction: independent engine goals or API input can still race after the final check. It does not detect past interruption or infer that unknown activity is idle.

The production `DesktopDirectoryObserver` keeps one verified read-only connection, checks process incarnation for each snapshot/write, and reconnects through full image verification after faults. This avoids rehashing the desktop archive on every directory poll. A serialized heartbeat sends metadata-status requests only for known IDs, at most 256 per 30 seconds, without subscribing to history or snapshots. Its own echoed requests cannot keep vanished sessions alive. Cancellation closes the pipe and disposal bounds the background-loop wait. `DesktopDirectory.ObserveOpenSessionsAsync` remains the separate one-second diagnostic.

Both directory results are bounded to 256 UUIDs with a two-minute TTL and typed activity (unknown unless native state establishes it). Neither observer sends history/list/subscription requests or model input. Call `DiscoverAsync` on the delivery adapter for each candidate; delivery retains its own full compatibility checks. The live metadata probe found the dedicated open desktop test conversation; VS Code coverage still needs acceptance. The native synthetic suite covers owner binding, exact-target metadata privacy, bounded transport cleanup and delivery guards.

Same-process duplicate IDs return their original receipt; changed text or target collides. Any partial write, timeout, generic native error or unconfirmed response returns `Uncertain` and is never automatically resent. The worker must durably persist attempts and uncertain status across adapter restart; this in-memory cache does not replace broker persistence. Disconnect/reconnect creates a new incarnation, and old bindings cannot be used with it.

Use `binding.NativeSessionIncarnation` for stable broker addresses. It derives from the native process start, owner client and conversation; it does not change merely because an observer reconnects. `binding.Incarnation` remains a separate adapter connection lease used to reject stale submissions. `VsCodeCompatibilityProfile.VerifyRunningClients()` validates public extension files plus actual direct Code-owned native engine paths, hashes, starts and user identity; it does not itself identify a recipient client.

When VS Code created the coordination router, the alternative `VsCodeRouterCompatibilityProfile` requires the exact current-user Code installation, either separately pinned `1.143.0` or `1.143.1` executable version/hash pair, and a compatible running extension engine. The same connected-server identity and ACL gates apply. This branch has synthetic compatibility checks and public-source evidence. Live acceptance with a Code-hosted router remains pending; the running desktop was not shut down to force that case.

Only a successful native response from the pinned owner yields `Accepted`. The preceding package's dedicated ordinary desktop conversation passed one idle plain-input wake test with a user-confirmed visible reply and unchanged engine processes. The current package's connection, public-image and ACL checks pass; that old test conversation has no loaded owner after the crash. External peer input, busy/interrupted states, approval routing and normally opened VS Code recipients still need live acceptance. No live credentials, transcript files, config changes or vendor bundle patches are required by this adapter. The abandoned synthetic MCP-app wrapper was removed because a server alias cannot prove a live widget identity.
