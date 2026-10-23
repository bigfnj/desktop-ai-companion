# Test artifact retention

Keep active runs intact. Preserve source changes, raw mutation targets, result
manifests, logs, tested commit IDs and restoration evidence. A directory's age
alone does not prove that a run has finished.

After a successful `tests/Invoke-IsolatedGate.ps1` run, its owner removes only
that run's private `dotnet-home` cache. Logs and other evidence stay in the run
directory. Cleanup runs after environment restoration and normal successful
completion. A failed or uncertain run retains its cache until its owner confirms
that dependent processes have stopped. Cleanup refusal never changes a passing
test result into a false failure; it reports that the cache was retained.

To inspect existing generated output without opening file contents or following
links:

```powershell
pwsh -NoProfile -File tools/agentbroker/Measure-TestArtifacts.ps1
```

For explicitly verified completed gate runs, preview their SDK-cache cleanup:

```powershell
pwsh -NoProfile -File tools/agentbroker/Remove-CompletedGateCaches.ps1 -CompletedRunIds 1234abcd,5678abcd
```

Add `-Apply` to remove the listed caches. The default requires a two-hour quiet
period. The owning gate uses zero hours only after its successful completion.
Protected paths, reparse points, changed files and matching running images cause
a refusal. Long and mixed 8.3 process-path spellings are checked. All candidates
are validated before the first deletion, and each removed cache retains a small
receipt beside the preserved evidence. Kernel process metadata access is needed;
no command lines, environment, conversation records or credentials are read.

Completed worker build binaries can also be regenerated. Reclaim them only after
checking their exact run dependencies and tracked paths. Preserve all source and
test verdicts, including failed and incomplete runs. Never remove Git metadata or
an active mutation pool to save space.

`Remove-CompletedWorkerBinaries.ps1 -PlanPath <plan.json>` previews a JSON plan
of explicitly completed worktrees, for example
`{"Worktrees":["artifacts/old-run/w1"]}`. It accepts at most 32 registered
worktrees beneath this checkout's artifacts directory. `-Apply` deletes only
untracked `.dll`, `.exe`, `.pdb`, `.obj`, `.lib` and `.exp` build outputs.
Directories, nonbinary files and names suggesting snapshots or original bytes
are preserved. Path, age, tracking, process and file-change checks run before
deletion. A partial failure retains a receipt and never reports full success.

The cleanup regression creates tiny ordinary fixtures and tests dry-run safety,
log retention, all-candidate prevalidation, reparse refusal, mixed 8.3 matching and
the actual successful/failed gate hook:

```powershell
pwsh -NoProfile -File tests/Test-GateCacheCleanup.ps1
pwsh -NoProfile -File tests/Test-CompletedWorkerBinaryCleanup.ps1
```
