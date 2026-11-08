#requires -Version 7
<#
.SYNOPSIS
Exercise actual worker-binary cleanup against a tiny, owned Git worktree only.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-DeveloperTool.ps1 pwsh -NoProfile -File tests/Test-CompletedWorkerBinaryCleanup.ps1
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$source = Join-Path $repo 'tools\agentbroker\Remove-CompletedWorkerBinaries.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Cleanup helper must parse before safety checks.' }
foreach ($definition in @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] },$true))) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$scratch = Join-Path $repo ('artifacts\worker-cleanup-tests-' + [Guid]::NewGuid().ToString('N'))
[void](Assert-WorkerBinaryPath $scratch $repo)
[void][IO.Directory]::CreateDirectory($scratch)
# A failed run keeps its scratch tree, as docs/test-artifact-retention.md keeps every failed run's evidence, and says
# where it is; a passing run removes it at the end (N-audit-1106-363). break rethrows, so the failure is unchanged. A
# trap covers its whole scope, the lines above it too, so it names the folder only once one exists.
trap {
    $keptScratch = Get-Variable -Name scratch -ValueOnly -ErrorAction SilentlyContinue
    if ($keptScratch -and [IO.Directory]::Exists($keptScratch)) {
        Write-Warning "Test-CompletedWorkerBinaryCleanup.ps1 failed; its scratch folder is kept for inspection: $keptScratch"
    }
    break
}
$tiny = Join-Path $scratch 'tiny-repo'
[void][IO.Directory]::CreateDirectory($tiny)
$worker = Join-Path $tiny 'artifacts\worker'
function Invoke-WorkerBinaryGit([string]$Repository,[string[]]$Arguments) {
    # These two new, GUID-owned fixtures inherit the workspace's group owner.
    # Scope the ownership exception to this child command and these exact paths.
    [void](Assert-WorkerBinaryPath $Repository $scratch)
    if ($Repository -notin @($tiny,$worker)) { throw 'Synthetic Git scope escaped its two owned fixture paths.' }
    $output = @(& git -c ('safe.directory=' + $Repository) -C $Repository @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'worker_binary_git_metadata_failed' }
    return $output
}
[void](Invoke-WorkerBinaryGit $tiny @('init','--quiet'))
[void](Invoke-WorkerBinaryGit $tiny @('-c','user.name=bigfnj','-c','user.email=bigfnj@users.noreply.github.com','commit','--quiet','--allow-empty','-m','Owned cleanup safety fixture'))
[void](Invoke-WorkerBinaryGit $tiny @('worktree','add','--quiet','--detach',$worker))
$build = Join-Path $worker 'build'
[void][IO.Directory]::CreateDirectory($build)
$planFile = Join-Path $tiny 'artifacts\plan.json'
[IO.File]::WriteAllText($planFile,'{"Worktrees":["artifacts/worker"]}')
$script:testImages = @(); $script:mutateAtImageCheck = $null
function Get-WorkerBinaryImages {
    if ($null -ne $script:mutateAtImageCheck) {
        [IO.File]::AppendAllText($script:mutateAtImageCheck,'changed')
        $script:mutateAtImageCheck = $null
    }
    return $script:testImages
}
$checks = 0
function Check([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw ('Test-CompletedWorkerBinaryCleanup.ps1: ' + $Message) }
    $script:checks++
}
function Old-File([string]$Name,[string]$Text='owned synthetic bytes') {
    $path = Join-Path $build $Name
    [void](Assert-WorkerBinaryPath $path $tiny)
    [IO.File]::WriteAllText($path,$Text)
    [IO.File]::SetLastWriteTimeUtc($path,[DateTime]::UtcNow.AddDays(-2))
    return $path
}
function Refuses([scriptblock]$Action,[string]$Code) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -match [regex]::Escape($Code)) ('expected refusal ' + $Code)
}
Refuses { Assert-WorkerBinaryPath (Join-Path $tiny 'build\.mozilla\not-created.dll') $tiny } 'worker_binary_protected_path'
Refuses { Assert-WorkerBinaryPath (Join-Path $tiny 'build\.config\chromium\not-created.dll') $tiny } 'worker_binary_protected_path'
$binary = Old-File 'owned.dll'; $log = Old-File 'evidence.log'; $json = Old-File 'result.json'
$raw = Old-File 'original.dll'; $sourceFile = Old-File 'target.cs'
$dry = Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile
Check ($dry.State -eq 'planned' -and $dry.CandidateFiles -eq 1 -and $dry.RemovedFiles -eq 0) 'dry-run identifies one untracked binary without deletion'
Check ([IO.File]::Exists($binary)) 'dry-run preserves the binary'
$applied = Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply
Check ($applied.State -eq 'removed' -and $applied.RemovedFiles -eq 1) 'Apply removes exactly the planned untracked binary'
Check (-not [IO.File]::Exists($binary)) 'planned binary is gone'
foreach ($path in @($log,$json,$raw,$sourceFile)) { Check ([IO.File]::Exists($path)) 'all nonbinary/source/raw-target evidence survives' }
Check ([IO.Directory]::Exists($build)) 'build directory survives'
$saved = [IO.File]::ReadAllText($applied.ReceiptPath) | ConvertFrom-Json
Check ($saved.State -eq 'removed' -and $saved.RemovedFiles -eq 1) 'receipt outside build records exact completed removal'

$binary = Old-File 'live.exe'
$long = $worker; $short = Get-WorkerBinaryShortPath $worker
$longParts = $long.Split('\'); $shortParts = $short.Split('\'); $mixedParts = @()
for ($i=0;$i -lt $longParts.Count;$i++) { $mixedParts += $(if ($i % 2) { $shortParts[$i] } else { $longParts[$i] }) }
$mixedImage = ($mixedParts -join '\') + '\build\live.exe'
Check (Test-WorkerBinaryImage $mixedImage $long $short) 'mixed long/8.3 image path is recognized'
Check (-not (Test-WorkerBinaryImage ($long+'-other\build\live.exe') $long $short)) 'sibling prefix is not a matching worktree'
$script:testImages = @([pscustomobject]@{ ProcessId=12345; ExecutablePath=$mixedImage })
Refuses { Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply } 'worker_binary_live_image'
Check ([IO.File]::Exists($binary)) 'live-image refusal precedes deletion'
$script:testImages = @()

[IO.File]::SetLastWriteTimeUtc($binary,[DateTime]::UtcNow)
Refuses { Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply } 'worker_binary_recent_write'
Check ([IO.File]::Exists($binary)) 'recent file remains intact'
[IO.File]::SetLastWriteTimeUtc($binary,[DateTime]::UtcNow.AddDays(-2))
$script:mutateAtImageCheck = $binary
Refuses { Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply } 'worker_binary_changed_since_plan'
Check ([IO.File]::Exists($binary)) 'between-plan mutation is preserved'
[IO.File]::SetLastWriteTimeUtc($binary,[DateTime]::UtcNow.AddDays(-2))

$target = Join-Path $scratch 'ordinary-target'
[void][IO.Directory]::CreateDirectory($target)
$sentinel = Join-Path $target 'sentinel.dll'; [IO.File]::WriteAllText($sentinel,'external sentinel')
$junction = Join-Path $build 'linked-output'
[void](New-Item -ItemType Junction -Path $junction -Target $target)
try {
    Refuses { Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply } 'worker_binary_reparse_path'
    Check ([IO.File]::ReadAllText($sentinel) -ceq 'external sentinel') 'junction target is untouched'
    Check ([IO.File]::Exists($binary)) 'linked-path refusal precedes ordinary deletion'
} finally { [IO.Directory]::Delete($junction,$false) }

$tracked = Old-File 'tracked.txt'
[void](Invoke-WorkerBinaryGit $worker @('add','--','build/tracked.txt'))
try {
    Refuses { Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply } 'worker_binary_tracked_build_path'
    Check ([IO.File]::Exists($binary) -and [IO.File]::Exists($tracked)) 'any tracked build path refuses cleanup before effects'
} finally { [void](Invoke-WorkerBinaryGit $worker @('rm','--cached','--quiet','--','build/tracked.txt')) }

$second = Old-File 'second.pdb'
$script:realDelete = ${function:Remove-WorkerBinaryFile}; $script:deleteCalls = 0
function Remove-WorkerBinaryFile($File,[string]$TrustedRoot) {
    $script:deleteCalls++
    if ($script:deleteCalls -eq 2) { throw 'synthetic_second_delete_failure' }
    & $script:realDelete $File $TrustedRoot
}
Refuses { Invoke-CompletedWorkerBinaryCleanup -RepositoryRoot $tiny -InputPlan $planFile -Apply } 'synthetic_second_delete_failure'
$remaining = @($binary,$second | Where-Object { [IO.File]::Exists($_) })
Check ($remaining.Count -eq 1) 'partial failure leaves the second binary intact'
$receipts = @([IO.Directory]::EnumerateFiles((Join-Path $tiny 'artifacts'),'worker-binary-cleanup-*.json'))
$partial = @($receipts | ForEach-Object { [IO.File]::ReadAllText($_) | ConvertFrom-Json } | Where-Object State -eq 'partial')
Check ($partial.Count -eq 1 -and $partial[0].RemovedFiles -eq 1) 'partial receipt never reports full success'
foreach ($path in @($log,$json,$raw,$sourceFile)) { Check ([IO.File]::Exists($path)) 'partial failure still preserves evidence' }

# EVERY CHECK HAS PASSED: REMOVE THE SCRATCH TREE (N-audit-1106-363). Each run left artifacts\worker-cleanup-tests-<guid>
# behind, a Git repository with a registered worktree, receipts and a sentinel, and nothing removed any of them. Only
# after the last check, so a failing run never reaches this; the verdict is the report printed below. The leaf is
# checked first, so nothing but this run's own folder is deleted recursively. Any reparse point still inside refuses the
# delete rather than having the delete meet it (the junction fixture is removed in its own finally above). Git writes its
# object files read-only, which a recursive Directory.Delete cannot remove, so those attributes are cleared first. A
# removal that fails leaves the folder and says so; it never turns this passing run into a failure, the rule
# docs/test-artifact-retention.md sets for the gate's own cache cleanup.
$scratchRemoved = $false
$scratchParent = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
if ([IO.Path]::GetDirectoryName($scratch) -ne $scratchParent -or (Split-Path -Leaf $scratch) -cnotmatch '^worker-cleanup-tests-[0-9a-f]{32}$') {
    Write-Warning "the scratch folder is not a worker-cleanup-tests-<guid> child of $scratchParent, so it is not deleted: $scratch"
}
elseif (@(Get-ChildItem -LiteralPath $scratch -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
    Write-Warning "the scratch folder still holds a reparse point, so it is not deleted: $scratch"
}
else {
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $scratch -Recurse -Force -File)) {
            if ($file.Attributes -band [IO.FileAttributes]::ReadOnly) { $file.Attributes = $file.Attributes -band -bnot [IO.FileAttributes]::ReadOnly }
        }
        [IO.Directory]::Delete($scratch, $true)
        $scratchRemoved = -not [IO.Directory]::Exists($scratch)
    }
    catch { Write-Warning "the scratch folder could not be removed and is left for inspection: $scratch ($($_.Exception.Message))" }
}
$report = [ordered]@{ Result='PASS'; Checks=$checks; SyntheticOnly=$true; ActualCleanupFunctions=$true; ScratchDirectory=$scratch; ScratchRemoved=$scratchRemoved }
if (-not $scratchRemoved) {
    [IO.File]::WriteAllText((Join-Path $scratch 'result.json'),($report | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
$report | ConvertTo-Json
