#requires -Version 7
# Pure, in-memory evidence fixtures. Starts no child, touches no worktree or data file.
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = 'a' * 40
. (Join-Path $repository 'tools\agentbroker\Invoke-MutationShards.ps1') -LibraryOnly -Commit $pin -Suite Guards
$checks = 0
function Check([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $script:checks++ }
function Refuses([scriptblock]$Action, [string]$Name, [string]$ExpectedCode = '') {
    $refused = $false
    try { & $Action } catch { $refused = -not $ExpectedCode -or $_.Exception.Message -ceq $ExpectedCode }
    Check $refused $Name
}
function Clone($Value) { ConvertFrom-Json ($Value | ConvertTo-Json -Depth 30 -Compress) -Depth 30 }
Check ((@(Convert-RemainderIndices '2,3,4,6,7,8') -join ',') -ceq '2,3,4,6,7,8') 'native CLI selection uses exact comma-separated IDs'
Check (@(Convert-RemainderIndices '').Count -eq 0) 'empty native CLI selection preserves normal defaults'
foreach ($badIndices in @('2, 3', '0', '01', '2,,3', '2;3', '-1', '999')) {
    Refuses { Convert-RemainderIndices $badIndices } 'ambiguous native CLI selection is refused'
}
$cases = @(1..16 | ForEach-Object { @{ index = $_; id = "synthetic case $_" } })
$whole = [pscustomobject]@{ schemaVersion = 1; sourceCommit = $pin; inventorySha256 = 'b' * 64;
    inventoryCount = 16; selectedCount = 16; cases = $cases; selection = @{ kind = 'all' } }
function Shard([int]$Index, [int]$Count) {
    $selected = @($whole.cases.Where({ ($_.index - 1) % $Count -eq $Index - 1 }))
    return [pscustomobject]@{ schemaVersion = 1; sourceCommit = $pin; inventorySha256 = $whole.inventorySha256;
        inventoryCount = 16; selectedCount = $selected.Count; cases = $selected; selection = @{ kind = 'shard'; index = $Index; count = $Count } }
}
$import = Shard 1 4
$scheduled = @(2, 3, 4, 6, 7, 8 | ForEach-Object { Shard $_ 8 })
Assert-MixedShardCoverage $whole @($import) $scheduled $pin
Check $true 'mixed four/eight union preserves each inventory case exactly once'
foreach ($change in @('pin', 'digest', 'count', 'id', 'index', 'fraction', 'partial')) {
    $bad = @($scheduled | ForEach-Object { Clone $_ })
    switch ($change) {
        'pin' { $bad[0].sourceCommit = 'c' * 40 }
        'digest' { $bad[0].inventorySha256 = 'c' * 64 }
        'count' { $bad[0].inventoryCount = 17 }
        'id' { $bad[0].cases[0].id = 'unknown case' }
        'index' { $bad[0].cases[0].index = 1 }
        'fraction' { $bad[0].selection.count = 8.5 }
        'partial' { $bad[0].cases = @($bad[0].cases[0]); $bad[0].selectedCount = 1 }
    }
    Refuses { Assert-MixedShardCoverage $whole @($import) $bad $pin } "mixed union refuses $change"
}
Refuses { Assert-MixedShardCoverage $whole @($import) @($scheduled[0..4]) $pin } 'mixed union refuses a gap' 'remainder_case_gap'
Refuses { Assert-MixedShardCoverage $whole @($import) @($scheduled + (Shard 1 8)) $pin } 'mixed union refuses an overlap' 'remainder_case_overlap'
Refuses { Assert-MixedShardCoverage $whole @() $scheduled $pin } 'mixed union requires a completed import'
Refuses { Assert-MixedShardCoverage $whole @($import) @() $pin } 'mixed union requires scheduled work'
Assert-RemainderOptions '' @() @() 4 $false $false
Assert-RemainderOptions 'synthetic' @(1) @(2, 3, 4, 6, 7, 8) 8 $false $false
Check $true 'normal defaults and read-only remainder selection are valid'
Refuses { Assert-RemainderOptions '' @(1) @(2) 8 $false $false } 'subset cannot escape the import contract'
Refuses { Assert-RemainderOptions 'synthetic' @(1) @(2) 8 $false $true } 'worker mode cannot import evidence'
Refuses { Assert-RemainderOptions 'synthetic' @(1) @(2, 2) 8 $false $false } 'duplicate scheduled IDs are refused'
Refuses { Assert-RemainderOptions 'synthetic' @(1) @(9) 8 $false $false } 'out-of-range scheduled IDs are refused'
Refuses { Assert-RemainderOptions 'synthetic' @(1) @(3, 2) 8 $false $false } 'unordered scheduled IDs are refused'
$pool = Join-Path $repository 'artifacts\ms-0123abcd'
$null = Assert-RemainderPoolPath $pool $repository { param($Path) }
Check $true 'exact adjacent pool path is accepted without accessing a fixture'
foreach ($badPath in @('relative\ms-0123abcd', (Join-Path $repository 'artifacts\nested\ms-0123abcd'),
    (Join-Path $repository 'artifacts\..\ms-0123abcd'), (Join-Path $repository 'artifacts\unowned'))) {
    Refuses { Assert-RemainderPoolPath $badPath $repository { param($Path) } } 'pool containment refuses an escape or unowned name'
}
Refuses { Assert-RemainderPoolPath $pool $repository { param($Path) throw 'synthetic_reparse' } } 'pool reparse refusal propagates before reading evidence'
Assert-RemainderLaunchGate 'C:\synthetic\g2' { param($Path) } { param($Path) $false }
Check $true 'new assignment requires an absent gate'
Refuses { Assert-RemainderLaunchGate 'C:\synthetic\g2' { param($Path) } { param($Path) $true } } 'a stale gate is refused before worker startup' 'stale_worker_gate_refused'
Refuses { Assert-RemainderLaunchGate 'C:\synthetic\g2' { param($Path) throw 'synthetic_reparse' } { param($Path) $false } } 'a reparse gate cannot evade absence checking'

$runtime = @{ schemaVersion = 1; sourceCommit = $pin; inventorySha256 = $whole.inventorySha256;
    inventoryCount = 16; selectedCount = 4; selection = $import.selection; selectedIndices = @($import.cases.index) } | ConvertTo-Json -Compress
$log = "PREBUILD START`nPREBUILD EXIT=0`nCASEPLAN $runtime`n  baseline clean`n"
foreach ($case in $import.cases) { $log += "  $($case.id) FIRED`n" }
$log += "restoring and rebuilding the clean tree`n4/4 fired.`n"
$goodRecord = @{ Index = 1; Success = $true; Code = 0; Fired = 4; Withheld = 0; SourceRestored = $true; TargetBytesRestored = $true }
$goodSnapshot = @(@{ Path = 'src/known.cs'; Sha256 = 'A' * 64 })
function Set-Fixture {
    $script:fixture = @{}
    $script:fixture[(Join-Path $pool 'plan-1.json')] = $import | ConvertTo-Json -Depth 20 -Compress
    $script:fixture[(Join-Path $pool 'result-1.json')] = $goodRecord | ConvertTo-Json -Compress
    $script:fixture[(Join-Path $pool 'source-1.json')] = ConvertTo-Json -InputObject $goodSnapshot -Compress
    $script:fixture[(Join-Path $pool 'shard-1.out')] = $log
}
$read = { param($Path, $IsLog) if (-not $script:fixture.ContainsKey($Path)) { throw 'synthetic_missing' }; return $script:fixture[$Path] }
$source = { param($Path) if ($Path -cne (Join-Path $pool 'w1')) { throw 'synthetic_source_escape' } }
$hash = { param($Path) 'A' * 64 }
Set-Fixture
$verified = Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read $source $hash
Check ($verified.Record.Fired -eq 4) 'import requires real log verdict plus fresh source and exact raw-byte proof'
foreach ($change in @('Success', 'SourceRestored', 'TargetBytesRestored', 'Code', 'Fired', 'MissingExit')) {
    Set-Fixture; $badRecord = Clone $goodRecord
    switch ($change) {
        'Code' { $badRecord.Code = 1 }
        'Fired' { $badRecord.Fired = 3 }
        'MissingExit' { $badRecord.PSObject.Properties.Remove('Code') }
        default { $badRecord.$change = $false }
    }
    $script:fixture[(Join-Path $pool 'result-1.json')] = $badRecord | ConvertTo-Json -Compress
    Refuses { Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read $source $hash } "untrusted result refuses $change"
}
foreach ($change in @('NO-OP', 'WRONG', 'SURVIVED', 'BROKEN', 'missing build', 'missing restore', 'failed restore', 'missing footer', 'duplicate verdict')) {
    Set-Fixture
    $badLog = switch ($change) {
        'missing build' { $log.Replace('PREBUILD EXIT=0', 'PREBUILD EXIT=1') }
        'missing restore' { $log.Replace('restoring and rebuilding the clean tree', '') }
        'failed restore' { $log + 'THE CLEAN REBUILD FAILED' }
        'missing footer' { $log.Replace('4/4 fired.', '') }
        'duplicate verdict' { $log + "  synthetic case 1 FIRED`n" }
        default { $log.Replace('FIRED', $change) }
    }
    $script:fixture[(Join-Path $pool 'shard-1.out')] = $badLog
    Refuses { Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read $source $hash } "import rejects $change"
}
Set-Fixture
Refuses { Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read $source { param($Path) 'B' * 64 } } 'fresh raw bytes override a claimed restoration result' 'mutation_target_bytes_changed'
Refuses { Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read { param($Path) throw 'tracked_source_changed' } $hash } 'fresh tracked-source failure invalidates imported evidence' 'tracked_source_changed'
foreach ($badName in @('../escape.cs', 'privatekey-example/known.cs')) {
    Set-Fixture
    $script:fixture[(Join-Path $pool 'source-1.json')] = ConvertTo-Json -InputObject @(@{ Path = $badName; Sha256 = 'A' * 64 }) -Compress
    Refuses { Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read $source { param($Path) throw 'hash_must_not_run' } } 'unsafe snapshot path is rejected as text before target access'
}
Set-Fixture
$script:fixture.Remove((Join-Path $pool 'result-1.json'))
Refuses { Read-VerifiedImportedShard $pool 1 $whole @('src/known.cs') $read $source $hash } 'missing exit evidence never resumes partial progress'
Assert-RemainderSnapshotEqual $goodSnapshot $goodSnapshot
Check $true 'fresh checkout must match the imported raw snapshot'
Refuses { Assert-RemainderSnapshotEqual @(@{ Path = 'src/known.cs'; Sha256 = 'B' * 64 }) $goodSnapshot } 'line-ending or content drift in a new checkout is refused' 'remainder_checkout_bytes_changed'

$proof = @{ SchemaVersion = 1; Pool = $pool; SourceCommit = $pin; InventorySha256 = $whole.inventorySha256;
    CompletedShardIndices = @(1); AllStopped = $true; SourcesRestored = $true; TargetBytesRestored = $true;
    Controller = @{ ProcessId = 101; CreationFileTime = '12345' };
    Processes = @(@{ ProcessId = 101; CreationFileTime = '12345' }, @{ ProcessId = 102; CreationFileTime = '12346' }) }
Assert-RemainderHandoff $proof $pool $whole @(1) 101 '12345' { param($Identity) $false }
Check $true 'handoff verifies exact expected controller and ended process incarnations'
foreach ($change in @('pin', 'digest', 'pool', 'completed', 'controller', 'birth', 'duplicate', 'missing controller', 'AllStopped', 'SourcesRestored', 'TargetBytesRestored')) {
    $badProof = Clone $proof
    switch ($change) {
        'pin' { $badProof.SourceCommit = 'c' * 40 }
        'digest' { $badProof.InventorySha256 = 'c' * 64 }
        'pool' { $badProof.Pool = Join-Path $repository 'artifacts\ms-ffffffff' }
        'completed' { $badProof.CompletedShardIndices = @(2) }
        'controller' { $badProof.Controller.ProcessId = 103 }
        'birth' { $badProof.Controller.CreationFileTime = '99999' }
        'duplicate' { $badProof.Processes[1].ProcessId = 101 }
        'missing controller' { $badProof.Processes = @($badProof.Processes[1]) }
        default { $badProof.$change = $false }
    }
    Refuses { Assert-RemainderHandoff $badProof $pool $whole @(1) 101 '12345' { param($Identity) $false } } "handoff refuses $change"
}
Refuses { Assert-RemainderHandoff $proof $pool $whole @(1) 101 '12345' { param($Identity) $true } } 'a live old incarnation cannot be adopted as a stopped pool' 'handoff_process_still_alive'
Write-Output "RemainderSyntheticChecks=$checks"
Write-Output 'MutationWorkersStarted=False'
Write-Output 'WorktreesChanged=False'
Write-Output 'ProtectedFixturesOpened=False'
