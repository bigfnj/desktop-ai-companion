# Development-only exact-editor probe

This tiny test extension opens one explicitly authorized, existing Codex test conversation through VS Code's ordinary custom-editor API. It is not a production adapter, owner-identity proof, permission fix or relay. It sends no backend protocol or model input. The normal Codex GUI may load the authorized conversation through its normal history/resume behavior. Native recipient-provenance and delivery acceptance gates remain unchanged.

Preparation opens no GUI and writes only a unique directory below this checkout's ignored `artifacts/agentbroker-editor-probes`:

```powershell
pwsh -NoProfile -File tools/agentbroker/Open-CodexEditorProbe.ps1 -TargetThreadId '<authorized-existing-test-UUID>'
```

Review the printed workspace, manifest and fixed arguments first. Revalidate that preparation without opening a window by passing `-PreparedWorkspace '<printed-workspace>' -ExpectedTargetThreadId '<authorized-existing-test-UUID>'`. An explicitly authorized live test adds `-Launch` to that same command. Launch requires both a preparation made separately and an explicit expected UUID that matches the manifest; it never prepares and launches in one call.

The launcher requires the exact user-install Code path, product version and executable hash already pinned in `VsCodeRouterCompatibilityProfile`; it consumes those existing source constants and refuses an unrecognized profile formula. It mirrors the installed public `code.cmd` entry point, setting child-only `ELECTRON_RUN_AS_NODE=1` and removing child-only `VSCODE_DEV`, with `--new-window`, one unique `--extensionDevelopmentPath`, and the exact prepared workspace. Installed Code 1.143.0's CLI help describes `--new-window` as forcing a new window, and its public `cli.js` declares `extensionDevelopmentPath` as a string-array option. No user-data, extension-directory, profile, Workspace Trust, security, debugger or reuse override is supplied. Existing default extensions and user profile remain in use. No installed extension is changed, no existing window is reloaded, and no process or window is stopped or automatically closed. A launch reservation prevents repeating the same preparation. CLI dispatch alone does not prove a new editor opened.

Activation requires `ExtensionMode.Development`, the unique prepared workspace and folder, its exact settings/manifest identity, matching copied source hashes, a trusted workspace, and an absent result. Workspace Trust remains enabled; the helper neither grants trust nor requests it. It refuses links/junctions and noncanonical paths before reading prepared inputs. The PowerShell preparation/launch check also refuses all Windows reparse-point path components. No existing file is overwritten.

The only executed VS Code command is `vscode.openWith`, once, with the fixed `chatgpt.conversationEditor` provider, exact `openai-codex://route/local/<authorized UUID>` URI, and `{viewColumn: One, preserveFocus: false, preview: false}`. This follows the pinned extension's `H6e`/`rF` route construction and custom-editor registration. The probe inspects only `activeTabGroup.activeTab`, requiring both `TabInputCustom` and the exact URI/provider. It never enumerates other tabs. The command wait is bounded at 15 seconds, followed by at most three seconds checking that active tab. Timeout cannot cancel an already dispatched ordinary GUI open and never causes a retry.

`result.json` is created once in the owning prepared directory. It contains bounded booleans, a schema number and one fixed status code, never the thread UUID, title, path, history, raw error, credentials or raw protocol frames. `opened_exact_editor` proves only that the exact custom-editor tab was observed, not native ownership, engine provenance or delivery. Refusal before owning-workspace validation creates no result elsewhere.

Run the synthetic and preparation checks without a GUI:

```powershell
node --test tests/agentbroker-vscode-editor-probe.test.cjs
pwsh -NoProfile -File tests/Test-CodexEditorProbePreparation.ps1
powershell -NoProfile -File tests/Test-CodexEditorProbePreparation.ps1
```

Tests use mock VS Code APIs and small new owned fixtures, including a removed nonrecursive junction. They never pass `-Launch`, start an engine, submit input or use client configuration/credentials. Keep preparations and their small results for review; do not launch stale copies after changing helper source.
