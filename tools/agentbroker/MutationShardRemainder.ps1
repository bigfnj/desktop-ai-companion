# Pure verification and read-only import helpers for the mutation controller.
# Dot-sourced only by the reviewed controller; no action runs on import.
function Convert-RemainderIndices([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return }
    Assert-Fixed ($Text -cmatch '^[1-9][0-9]?(?:,[1-9][0-9]?)*$') 'remainder_indices_invalid'
    foreach ($value in $Text.Split(',')) { [int]$value }
}

function Assert-RemainderOptions([string]$Pool, [int[]]$Completed, [int[]]$Scheduled, [int]$Count, [bool]$Apply, [bool]$IsWorker) {
    if (-not $Pool) {
        Assert-Fixed ($Completed.Count -eq 0 -and $Scheduled.Count -eq 0 -and -not $Apply) 'remainder_pool_required'
        return
    }
    Assert-Fixed (-not $IsWorker -and $Completed.Count -gt 0 -and $Completed.Count -le 32 -and
        $Scheduled.Count -gt 0 -and $Scheduled.Count -le $Count) 'remainder_options_invalid'
    foreach ($selection in @(@{ Values = $Completed; Maximum = 32 }, @{ Values = $Scheduled; Maximum = $Count })) {
        $seen = [Collections.Generic.HashSet[int]]::new()
        foreach ($index in $selection.Values) { Assert-Fixed ($index -ge 1 -and $index -le $selection.Maximum -and $seen.Add($index)) 'remainder_index_invalid' }
        Assert-Fixed (($selection.Values -join ',') -ceq (@($selection.Values | Sort-Object) -join ',')) 'remainder_indices_not_ordered'
    }
}

function Assert-RemainderSnapshotEqual([object[]]$Actual, [object[]]$Expected) {
    Assert-Fixed ($Actual.Count -eq $Expected.Count) 'remainder_checkout_bytes_changed'
    $expectedHashes = @{}
    foreach ($entry in $Expected) { $expectedHashes[$entry.Path] = $entry.Sha256 }
    foreach ($entry in $Actual) {
        Assert-Fixed ($expectedHashes.ContainsKey($entry.Path) -and $expectedHashes[$entry.Path] -ceq $entry.Sha256) 'remainder_checkout_bytes_changed'
        $expectedHashes.Remove($entry.Path)
    }
    Assert-Fixed ($expectedHashes.Count -eq 0) 'remainder_checkout_bytes_changed'
}

function Assert-RemainderPoolPath([string]$Pool, [string]$Repository,
    [scriptblock]$CheckPath = { param($Target) Assert-NoReparse $Target }) {
    Assert-Fixed ([IO.Path]::IsPathFullyQualified($Pool)) 'import_pool_not_absolute'
    $full = [IO.Path]::GetFullPath($Pool).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $artifacts = Join-Path ([IO.Path]::GetFullPath($Repository)) 'artifacts'
    Assert-Fixed ((Split-Path $full -Parent).Equals($artifacts, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $full -Leaf) -cmatch '^ms-[0-9a-f]{8}$') 'import_pool_path_refused'
    & $CheckPath $full
    return $full
}

function Read-RemainderText([string]$Path, [switch]$Log) {
    Assert-NoReparse $Path
    Initialize-NativeJob
    if ($Log) { return [MutationShardJob]::ReadPublicLog($Path) }
    return [MutationShardJob]::ReadPublicText($Path)
}

function Convert-RemainderJson([string]$Text) {
    try { return ConvertFrom-Json -InputObject $Text -Depth 40 -ErrorAction Stop }
    catch { throw 'import_json_invalid' }
}

function Assert-FullShardPlan($Plan, $Whole, [string]$Pinned) {
    Assert-PlanCore $Whole $Pinned
    Assert-PlanCore $Plan $Pinned $Whole.inventorySha256
    Assert-Fixed ($Whole.selection.kind -ceq 'all' -and $Whole.selectedCount -eq $Whole.inventoryCount -and
        $Plan.inventoryCount -eq $Whole.inventoryCount -and $Plan.selection.kind -ceq 'shard' -and
        (Test-Integer $Plan.selection.index) -and (Test-Integer $Plan.selection.count) -and
        $Plan.selection.count -ge 1 -and $Plan.selection.count -le 32 -and
        $Plan.selection.index -ge 1 -and $Plan.selection.index -le $Plan.selection.count) 'import_shard_selection_invalid'
    $expected = @($Whole.cases.Where({ ([int]$_.index - 1) % [int]$Plan.selection.count -eq [int]$Plan.selection.index - 1 }))
    Assert-Fixed ($expected.Count -eq $Plan.selectedCount) 'import_shard_incomplete'
    for ($position = 0; $position -lt $expected.Count; $position++) {
        Assert-Fixed ($Plan.cases[$position].index -eq $expected[$position].index -and
            $Plan.cases[$position].id -ceq $expected[$position].id) 'import_shard_case_changed'
    }
}

function Assert-MixedShardCoverage($Whole, [object[]]$ImportedPlans, [object[]]$ScheduledPlans, [string]$Pinned) {
    Assert-PlanCore $Whole $Pinned
    Assert-Fixed ($ImportedPlans.Count -gt 0 -and $ScheduledPlans.Count -gt 0) 'remainder_selection_empty'
    $indices = [Collections.Generic.HashSet[int]]::new()
    foreach ($plan in @($ImportedPlans) + @($ScheduledPlans)) {
        Assert-FullShardPlan $plan $Whole $Pinned
        foreach ($case in $plan.cases) {
            Assert-Fixed ($indices.Add([int]$case.index)) 'remainder_case_overlap'
        }
    }
    Assert-Fixed ($indices.Count -eq $Whole.inventoryCount) 'remainder_case_gap'
}

function Get-RemainderSourceMetadata([string]$Directory) {
    Assert-Source $Directory
    $metadataEnvironment = @{ PYTHONDONTWRITEBYTECODE = '1'; PYTHONUNBUFFERED = '1' }
    $listed = Invoke-Captured $python @('-X', 'utf8', (Join-Path $Directory $harness), '--list-cases-json') $Directory $metadataEnvironment
    Assert-Fixed ($listed.Code -eq 0) 'whole_plan_unavailable'
    $whole = Convert-RemainderJson $listed.Text
    Assert-PlanCore $whole $Commit
    $targetCode = @'
import json,os,runpy,sys
root=sys.argv[1]
sys.path.insert(0,os.path.join(root,"tests"))
inventory=runpy.run_path(os.path.join(root,sys.argv[2]),run_name="mutation_target_metadata")["CASES"]
print(json.dumps(sorted({os.path.relpath(case[1],root).replace(os.sep,"/") for case in inventory}),ensure_ascii=True))
'@
    $targetResult = Invoke-Captured $python @('-X', 'utf8', '-c', $targetCode, $Directory, $harness) $Directory $metadataEnvironment
    Assert-Fixed ($targetResult.Code -eq 0) 'mutation_targets_unavailable'
    $targets = @(Convert-RemainderJson $targetResult.Text)
    Assert-Fixed ($targets.Count -gt 0 -and $targets.Count -le 10000) 'mutation_targets_invalid'
    foreach ($target in $targets) { Assert-PublicRelativePath $target }
    return @{ Whole = $whole; Targets = $targets }
}

function Get-RemainderShardPlan([string]$Directory, [int]$Index, [int]$Count) {
    $listed = Invoke-Captured $python @('-X', 'utf8', (Join-Path $Directory $harness), '--shard', "$Index/$Count", '--list-cases-json') $Directory @{ PYTHONDONTWRITEBYTECODE = '1' }
    Assert-Fixed ($listed.Code -eq 0) 'shard_plan_unavailable'
    return Convert-RemainderJson $listed.Text
}

function Read-VerifiedImportedShard([string]$Pool, [int]$Index, $Whole, [string[]]$Targets,
    [scriptblock]$ReadText = { param($Path, $Log) Read-RemainderText $Path -Log:$Log },
    [scriptblock]$CheckSource = { param($Path) Assert-Source $Path },
    [scriptblock]$ReadHash = { param($Path) Assert-NoReparse $Path; [MutationShardJob]::HashPublicFile($Path) }) {
    $Pool = Assert-RemainderPoolPath $Pool $repo
    Assert-Fixed ($Index -ge 1 -and $Index -le 32) 'import_shard_index_invalid'
    $plan = Convert-RemainderJson (& $ReadText (Join-Path $Pool "plan-$Index.json") $false)
    Assert-FullShardPlan $plan $Whole $Commit
    Assert-Fixed ($plan.selection.index -eq $Index) 'import_shard_index_changed'
    $record = Convert-RemainderJson (& $ReadText (Join-Path $Pool "result-$Index.json") $false)
    Assert-Fixed ($record.Index -eq $Index -and (Test-Integer $record.Code) -and $record.Code -eq 0 -and
        $record.Success -is [bool] -and $record.Success -and $record.SourceRestored -is [bool] -and $record.SourceRestored -and
        $record.TargetBytesRestored -is [bool] -and $record.TargetBytesRestored) 'import_result_unverified'
    $log = & $ReadText (Join-Path $Pool "shard-$Index.out") $true
    $verdict = Test-ShardVerdict $log $plan $record.Code $Suite
    Assert-Fixed ((Test-Integer $record.Fired) -and (Test-Integer $record.Withheld) -and
        $record.Fired -eq $verdict.Fired -and $record.Withheld -eq $verdict.Withheld) 'import_result_counts_changed'
    $directory = Join-Path $Pool "w$Index"
    & $CheckSource $directory
    $snapshot = @(Convert-RemainderJson (& $ReadText (Join-Path $Pool "source-$Index.json") $false))
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($target in $Targets) { Assert-PublicRelativePath $target; Assert-Fixed ($expected.Add($target)) 'import_target_duplicate' }
    Assert-Fixed ($snapshot.Count -eq $expected.Count) 'import_snapshot_count_changed'
    foreach ($entry in $snapshot) {
        Assert-Fixed ($entry.Path -is [string]) 'import_snapshot_path_invalid'
        Assert-PublicRelativePath $entry.Path
        Assert-Fixed ($expected.Remove($entry.Path) -and $entry.Sha256 -is [string] -and
            $entry.Sha256 -cmatch '^[0-9A-F]{64}$') 'import_snapshot_changed'
    }
    Assert-Fixed ($expected.Count -eq 0) 'import_snapshot_incomplete'
    Assert-TargetSnapshot $directory $snapshot $ReadHash
    return @{ Index = $Index; Directory = $directory; Plan = $plan; Snapshot = $snapshot; Record = $record }
}

function Assert-RemainderLaunchGate([string]$Path,
    [scriptblock]$CheckPath = { param($Target) Assert-NoReparse $Target },
    [scriptblock]$Exists = { param($Target) [IO.File]::Exists($Target) -or [IO.Directory]::Exists($Target) }) {
    & $CheckPath $Path
    Assert-Fixed (-not (& $Exists $Path)) 'stale_worker_gate_refused'
}

function Test-HandoffIncarnationAlive($Identity) {
    try { $process = [Diagnostics.Process]::GetProcessById([int]$Identity.ProcessId) }
    catch [ArgumentException] { return $false }
    try {
        $null = $process.SafeHandle
        return -not $process.HasExited -and $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() -ceq $Identity.CreationFileTime
    }
    finally { $process.Dispose() }
}

function Assert-RemainderHandoff($Proof, [string]$Pool, $Whole, [int[]]$CompletedIndices, [int]$ControllerId, [string]$ControllerCreationFileTime,
    [scriptblock]$IsAlive = { param($Identity) Test-HandoffIncarnationAlive $Identity }) {
    Assert-Fixed ($Proof.SchemaVersion -eq 1 -and $Proof.Pool -is [string] -and
        $Proof.Pool.Equals($Pool, [StringComparison]::OrdinalIgnoreCase) -and
        $Proof.SourceCommit -ceq $Commit -and $Proof.InventorySha256 -ceq $Whole.inventorySha256 -and
        $Proof.AllStopped -is [bool] -and $Proof.AllStopped -and $Proof.SourcesRestored -is [bool] -and $Proof.SourcesRestored -and
        $Proof.TargetBytesRestored -is [bool] -and $Proof.TargetBytesRestored -and
        (@($Proof.CompletedShardIndices) -join ',') -ceq ($CompletedIndices -join ',') -and
        $ControllerId -gt 0 -and $Proof.Controller.ProcessId -eq $ControllerId -and
        $Proof.Controller.CreationFileTime -ceq $ControllerCreationFileTime) 'handoff_proof_invalid'
    $identities = @($Proof.Processes)
    Assert-Fixed ($identities.Count -gt 0 -and $identities.Count -le 128) 'handoff_process_inventory_invalid'
    $seen = [Collections.Generic.HashSet[int]]::new()
    $controllerSeen = $false
    foreach ($identity in $identities) {
        Assert-Fixed ((Test-Integer $identity.ProcessId) -and $identity.ProcessId -gt 0 -and $seen.Add([int]$identity.ProcessId) -and
            $identity.CreationFileTime -is [string] -and $identity.CreationFileTime -cmatch '^[1-9][0-9]{0,19}$') 'handoff_identity_invalid'
        if ($identity.ProcessId -eq $Proof.Controller.ProcessId -and $identity.CreationFileTime -ceq $Proof.Controller.CreationFileTime) { $controllerSeen = $true }
        Assert-Fixed (-not (& $IsAlive $identity)) 'handoff_process_still_alive'
    }
    Assert-Fixed $controllerSeen 'handoff_controller_missing'
}
