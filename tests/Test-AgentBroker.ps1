#requires -Version 5.1
<#
.SYNOPSIS
    Build and run the deterministic broker, adapter, localhost TLS, and Worker harnesses.
.DESCRIPTION
    Uses synthetic sessions and fake native providers only. The Network and Worker harnesses
    create current-user nonexportable Windows KSP test keys and delete their exact owned keys
    in cleanup. No OS trust, firewall, certificate store, vendor credentials, or live turns change.
.EXAMPLE
    pwsh -NoProfile -File .\tests\Test-AgentBroker.ps1
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$CheckPayload)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'HarnessOutput.ps1')
Write-Host '=== AgentBroker payload source and packaging contracts'
& (Join-Path $PSScriptRoot 'Test-AgentBrokerPackaging.ps1') -RepoRoot $repoRoot -CheckPayload:$CheckPayload
# N-broker-d-01: the Claude connection helper's --self-test (path policy, loopback endpoint, retry rule, connection fields,
# reportable error codes) was run by nothing before AgentBroker 1.6.0. It needs node, as the pane's Connect Claude MCP
# action does; a missing node fails here and is never skipped in silence. The source check after it catches a new thrown
# code that was never added to the reportable set, which the self-test cannot see when no check throws that code.
Write-Host '=== AgentBroker Claude connection helper self-test'
$connectHelper = Join-Path $repoRoot 'tools\agentbroker\Connect-ClaudeMcpEntry.mjs'
$connectOutput = @(& node $connectHelper --self-test)
$connectExit = $LASTEXITCODE
foreach ($line in $connectOutput) { Write-Host $line }
$connectSummary = @($connectOutput | Where-Object { "$_" -cmatch '^\{"status":"self_test_passed","checks":[1-9][0-9]*,' })
if ($connectExit -ne 0 -or $connectSummary.Count -ne 1 -or $connectOutput.Count -ne 1) {
    throw "AgentBroker Claude connection helper self-test failed: tools/agentbroker/Connect-ClaudeMcpEntry.mjs --self-test (exit $connectExit)."
}
$connectChecks = [int]([regex]::Match([string]$connectSummary[0], '"checks":([0-9]+)').Groups[1].Value)
$connectSource = [IO.File]::ReadAllText($connectHelper)
# Every Error the file constructs is classified (independent review of 1.6.0, P3-10): a single string literal in either
# quote is a code and must be reportable; an empty new Error() is the self-test's fakes; anything else (a variable, an
# expression, a template) could be a code this check cannot read, so it fails here.
$connectThrown = New-Object 'Collections.Generic.List[string]'
$connectUnclassified = New-Object 'Collections.Generic.List[string]'
foreach ($construction in [regex]::Matches($connectSource, 'new Error\((?<inner>[^)]*)\)')) {
    $inner = $construction.Groups['inner'].Value.Trim()
    if ($inner.Length -eq 0) { continue }
    $literal = [regex]::Match($inner, "^(['""])([^'""]+)\1$")
    if ($literal.Success) { if (-not $connectThrown.Contains($literal.Groups[2].Value)) { $connectThrown.Add($literal.Groups[2].Value) } }
    else { $connectUnclassified.Add($inner) }
}
if ($connectUnclassified.Count -gt 0) {
    throw ('AgentBroker Claude connection helper constructs an Error the source check cannot classify: ' + ($connectUnclassified -join ', '))
}
$connectSet = [regex]::Match($connectSource, "const reportableErrors = new Set\(\[([^\]]+)\]\);")
$connectReportable = @([regex]::Matches($connectSet.Groups[1].Value, "(['""])([^'""]+)\1") | ForEach-Object { $_.Groups[2].Value })
if (-not $connectSet.Success -or $connectThrown.Count -eq 0 -or $connectReportable.Count -eq 0) {
    throw 'AgentBroker Claude connection helper source check found no thrown codes or no reportableErrors set.'
}
$connectUnreportable = @($connectThrown | Where-Object { $connectReportable -cnotcontains $_ })
if ($connectUnreportable.Count -gt 0) {
    throw ('AgentBroker Claude connection helper throws codes its reportable set lacks (they would read diagnostic_unavailable): ' + ($connectUnreportable -join ', '))
}
Write-Host "PASS: Claude connection helper self-test, $connectChecks checks; all $($connectThrown.Count) thrown codes reportable"
# The collaboration helper is PowerShell 7 only, so it runs in its own pwsh child even when this
# script runs under Windows PowerShell. Missing pwsh fails here; it is never skipped in silence.
Write-Host '=== AgentBroker collaboration helper validation'
$collaborationOutput = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-AgentBrokerCollaborationMcp.ps1'))
$collaborationExit = $LASTEXITCODE
foreach ($line in $collaborationOutput) { Write-Host $line }
$collaborationSummary = @($collaborationOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) in-memory collaboration MCP checks;' })
if ($collaborationExit -ne 0 -or $collaborationSummary.Count -ne 1) {
    throw "AgentBroker collaboration helper validation failed (exit $collaborationExit)."
}
$collaborationChecks = [int]([regex]::Match([string]$collaborationSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# Real helper children and an owned ephemeral loopback server exercise main's exit/output plumbing.
# This is separate from the AST-loaded pure functions above, which cannot see main's process exit.
Write-Host '=== AgentBroker collaboration helper process contract'
$collaborationProcessOutput = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-AgentBrokerCollaborationMcpProcess.ps1'))
$collaborationProcessExit = $LASTEXITCODE
foreach ($line in $collaborationProcessOutput) { Write-Host $line }
$collaborationProcessSummary = @($collaborationProcessOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) process collaboration MCP checks;' })
if ($collaborationProcessExit -ne 0 -or $collaborationProcessSummary.Count -ne 1) {
    throw "AgentBroker collaboration helper process contract failed (exit $collaborationProcessExit)."
}
$collaborationProcessChecks = [int]([regex]::Match([string]$collaborationProcessSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# The re-pin helper's fixture checks (broker-b's hand-back, lane feature/agentbroker-backlog-d). The helper supports
# Windows PowerShell 5.1, so it runs in a powershell.exe child whichever edition runs this script. It reads and writes
# only its own temporary fixture directory. A failed check, a missing summary line or a nonzero exit fails here.
Write-Host '=== AgentBroker re-pin helper self-test'
$repinHelper = Join-Path $repoRoot 'tools\agentbroker\Test-AgentBrokerRepin.ps1'
$repinOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repinHelper -SelfTest)
$repinExit = $LASTEXITCODE
foreach ($line in $repinOutput) { Write-Host $line }
$repinSummary = @($repinOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) re-pin helper fixture checks$' })
if ($repinExit -ne 0 -or $repinSummary.Count -ne 1 -or @($repinOutput | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL)' }).Count -gt 0) {
    throw "AgentBroker re-pin helper self-test failed: tools/agentbroker/Test-AgentBrokerRepin.ps1 -SelfTest (exit $repinExit)."
}
$repinChecks = [int]([regex]::Match([string]$repinSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# Lane broker-pins: the canonical manifest hash the helper ports from VsCodeManifestProfile needs System.Text.Json, so
# its checks run only under PowerShell 7; the same self-test runs again in a pwsh child for them.
Write-Host '=== AgentBroker re-pin helper self-test (PowerShell 7)'
$repin7Output = @(& pwsh -NoProfile -File $repinHelper -SelfTest)
$repin7Exit = $LASTEXITCODE
foreach ($line in $repin7Output) { Write-Host $line }
$repin7Summary = @($repin7Output | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) re-pin helper fixture checks$' })
if ($repin7Exit -ne 0 -or $repin7Summary.Count -ne 1 -or @($repin7Output | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL)' }).Count -gt 0 -or
    @($repin7Output | Where-Object { "$_" -ceq 'PASS: the ported canonical manifest hash reproduces the adapter test vector byte for byte' }).Count -ne 1) {
    throw "AgentBroker re-pin helper self-test under PowerShell 7 failed: tools/agentbroker/Test-AgentBrokerRepin.ps1 -SelfTest (exit $repin7Exit)."
}
$repin7Checks = [int]([regex]::Match([string]$repin7Summary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# The installed-tree compare (AgentBroker 1.10.5 marathon, lane T, C4). It supports Windows PowerShell 5.1 and 7, so its
# fixture checks run in a child of each. It writes only its own temporary fixture folder, and reads that folder plus the
# committed samples under tools/agentbroker/tree-compare-samples; it also starts fsutil.exe and children of itself on that
# folder. The 1.10.5 entries map has no Directory field, and a filter on Directory -eq $false compared zero rows and passed:
# TREE_COMPARE_MISSING_DIRECTORY_IS_FILE and TREE_COMPARE_VACUOUS_REFUSED guard that, and every guard line named below must be
# present in both children.
$treeCompare = Join-Path $repoRoot 'tools\agentbroker\Compare-AgentBrokerTree.ps1'
$treeChecks = @{}
$treeSkipped = @{}
# Round 5 (C4) and round 6 (DECISIONS 101): every fixture label of the self-test is pinned by name, in both children. Each
# label must appear exactly once, as a PASS line or, for a label listed as skippable, as one SKIP line carrying its reason (the
# case-sensitivity fixture, when fsutil cannot set the flag on the volume), never as both; no other TREE_COMPARE_ label may
# appear, and the summary count must equal the PASS fixture lines. Round 5 pinned only passed + skipped, so a permitted SKIP
# printed for a fixture that also passed stood in for a different fixture that was gone (X1c's confirm of round 5). Update the
# list with every fixture added or removed. A SKIP is reported as a DEGRADED run here and in the final summary.
$treeFixtureLabels = @('TREE_COMPARE_MISSING_DIRECTORY_IS_FILE', 'TREE_COMPARE_LEGACY_DIRECTORY_FALSE',
    'TREE_COMPARE_DIRECTORY_ROW_NOT_COMPARED', 'TREE_COMPARE_RECEIPT_SHAPE', 'TREE_COMPARE_VACUOUS_REFUSED',
    'TREE_COMPARE_COUNT_MISMATCH_REFUSED', 'TREE_COMPARE_HASH_MISMATCH_FAILS', 'TREE_COMPARE_MISSING_FAILS', 'TREE_COMPARE_EXTRA_FAILS',
    'TREE_COMPARE_NAME_OUTSIDE_REFUSED', 'TREE_COMPARE_NESTED_EXTRA_FAILS', 'TREE_COMPARE_BYTES_MISMATCH_FAILS',
    'TREE_COMPARE_RECEIPT_COUNT_MISMATCH_REFUSED', 'TREE_COMPARE_RECEIPT_VACUOUS_REFUSED', 'TREE_COMPARE_DUPLICATE_NAME_REFUSED',
    'TREE_COMPARE_DOT_PREFIX_ACCEPTED', 'TREE_COMPARE_EMPTY_SEGMENT_REFUSED', 'TREE_COMPARE_RELATIVE_PATHS_FOLLOW_LOCATION',
    'TREE_COMPARE_DRIVE_ROOT_REFUSED', 'TREE_COMPARE_SAMPLE_SHAPES_READ', 'TREE_COMPARE_TRAILING_DOT_REFUSED',
    'TREE_COMPARE_TRAILING_SPACE_REFUSED', 'TREE_COMPARE_COLON_REFUSED', 'TREE_COMPARE_CASE_SENSITIVE_SECOND_SPELLING_EXTRA',
    'TREE_COMPARE_REPARSE_JUNCTION_REFUSED', 'TREE_COMPARE_REPARSE_LOOP_REFUSED', 'TREE_COMPARE_INCOMPLETE_ROW_REFUSED',
    'TREE_COMPARE_LOWERCASE_HASH_ACCEPTED', 'TREE_COMPARE_CHILD_ARGUMENT_QUOTE_REFUSED',
    'TREE_COMPARE_CHILD_ARGUMENT_TRAILING_BACKSLASH_REFUSED', 'TREE_COMPARE_CHILD_MATCH_EXIT_0_PS51',
    'TREE_COMPARE_CHILD_DIFFERS_EXIT_1_PS51', 'TREE_COMPARE_CHILD_REFUSAL_EXIT_1_PS51', 'TREE_COMPARE_CHILD_MATCH_EXIT_0_PS7',
    'TREE_COMPARE_CHILD_DIFFERS_EXIT_1_PS7', 'TREE_COMPARE_CHILD_REFUSAL_EXIT_1_PS7')
$treeSkippable = @('TREE_COMPARE_CASE_SENSITIVE_SECOND_SPELLING_EXTRA')
function Get-TreeFixtureLabelProblems([object[]]$Output, [string[]]$Labels, [string[]]$Skippable) {
    $passLabels = @($Output | ForEach-Object { [regex]::Match("$_", '^PASS: (TREE_COMPARE_[A-Z0-9_]+)$') } |
        Where-Object { $_.Success } | ForEach-Object { $_.Groups[1].Value })
    $skipLabels = New-Object 'Collections.Generic.List[string]'
    foreach ($skip in @($Output | Where-Object { "$_" -cmatch '^SKIP: ' })) {
        $match = [regex]::Match("$skip", '^SKIP: (TREE_COMPARE_[A-Z0-9_]+) \(.+\)$')
        if (-not $match.Success) { "a SKIP line without a fixture label and its reason: $skip"; continue }
        $skipLabels.Add($match.Groups[1].Value)
    }
    foreach ($label in $Labels) {
        $passCount = @($passLabels | Where-Object { $_ -ceq $label }).Count
        $skipCount = @($skipLabels | Where-Object { $_ -ceq $label }).Count
        if ($passCount -gt 0 -and $skipCount -gt 0) { "$label both passed and skipped" }
        elseif ($passCount -gt 1 -or $skipCount -gt 1) { "$label seen $($passCount + $skipCount) times" }
        elseif ($passCount + $skipCount -eq 0) { "$label missing" }
        if ($skipCount -gt 0 -and $Skippable -cnotcontains $label) { "$label skipped but not skippable" }
    }
    foreach ($label in @($passLabels) + @($skipLabels)) {
        if ($Labels -cnotcontains $label) { "$label is not a pinned fixture label" }
    }
    $summary = @($Output | Where-Object { "$_" -cmatch '^PASS: ([0-9]+) tree compare fixture checks$' })
    $summaryCount = -1
    if ($summary.Count -eq 1) { $summaryCount = [int]([regex]::Match([string]$summary[0], '^PASS: ([0-9]+)').Groups[1].Value) }
    if ($summaryCount -ne $passLabels.Count) { "summary count $summaryCount, PASS fixture lines $($passLabels.Count)" }
}
# The label rule is judged on synthetic receipts first, on every run, before any build. Two are accepted (every label passed;
# the listed label skipped with its reason) and each refusal is made by exactly one rule (Expect 1); X1c's misattributed skip
# (a listed label both passed and skipped while another label is gone) is a witness that must be refused (Expect -1).
function New-TreeReceipt([string[]]$Passed, [string[]]$Extra, [int]$Summary) {
    @($Passed | ForEach-Object { "PASS: $_" }) + @($Extra) + @("PASS: $Summary tree compare fixture checks")
}
$receiptSkippable = $treeSkippable[0]
$receiptLost = 'TREE_COMPARE_BYTES_MISMATCH_FAILS'
$withoutSkippable = @($treeFixtureLabels | Where-Object { $_ -cne $receiptSkippable })
$withoutLost = @($treeFixtureLabels | Where-Object { $_ -cne $receiptLost })
$listedSkip = "SKIP: $receiptSkippable (synthetic reason)"
$receiptAll = $treeFixtureLabels.Count
$treeReceipts = [ordered]@{
    'full' = @{ Expect = 0; Output = (New-TreeReceipt $treeFixtureLabels @() $receiptAll) }
    'listed skip' = @{ Expect = 0; Output = (New-TreeReceipt $withoutSkippable @($listedSkip) ($receiptAll - 1)) }
    'silent loss' = @{ Expect = 1; Output = (New-TreeReceipt $withoutLost @() ($receiptAll - 1)) }
    'duplicate' = @{ Expect = 1; Output = (New-TreeReceipt (@($treeFixtureLabels) + @($receiptLost)) @() ($receiptAll + 1)) }
    'contradictory' = @{ Expect = 1; Output = (New-TreeReceipt $treeFixtureLabels @($listedSkip) $receiptAll) }
    'unlisted skip' = @{ Expect = 1; Output = (New-TreeReceipt $withoutLost @("SKIP: $receiptLost (synthetic reason)") ($receiptAll - 1)) }
    'unknown label' = @{ Expect = 1; Output = (New-TreeReceipt (@($treeFixtureLabels) + @('TREE_COMPARE_NOT_A_FIXTURE')) @() ($receiptAll + 1)) }
    'summary mismatch' = @{ Expect = 1; Output = (New-TreeReceipt $treeFixtureLabels @() ($receiptAll - 1)) }
    'reasonless skip' = @{ Expect = 1; Output = (New-TreeReceipt $treeFixtureLabels @("SKIP: $receiptSkippable") $receiptAll) }
    'misattributed skip' = @{ Expect = -1; Output = (New-TreeReceipt $withoutLost @($listedSkip) ($receiptAll - 1)) }
}
$receiptFailures = New-Object 'Collections.Generic.List[string]'
foreach ($receiptName in $treeReceipts.Keys) {
    $receipt = $treeReceipts[$receiptName]
    $receiptProblems = @(Get-TreeFixtureLabelProblems $receipt.Output $treeFixtureLabels $treeSkippable)
    $receiptOk = if ($receipt.Expect -lt 0) { $receiptProblems.Count -ge 1 } else { $receiptProblems.Count -eq $receipt.Expect }
    if (-not $receiptOk) { $receiptFailures.Add("$receiptName ($($receiptProblems.Count): $($receiptProblems -join '; '))") }
}
if ($receiptFailures.Count -gt 0) {
    # One plain line each, before the throw: the error view wraps a long message and colours it.
    foreach ($receiptFailure in $receiptFailures) { Write-Host "REFUSED SYNTHETIC RECEIPT: $receiptFailure" }
    throw "AgentBroker tree compare fixture-label rule failed its synthetic receipts: $($receiptFailures -join ' | ')"
}
Write-Host "PASS: the tree compare fixture-label rule judged $($treeReceipts.Count) synthetic receipts (2 accepted, 8 refused, X1c's misattributed skip among them)"
# Round 2: a stray file below the root (nested extra) and relative arguments judged at the PowerShell location are
# required by name too: each was a clean MATCH for the wrong tree before its fix.
$treeGuardNames = @('PASS: TREE_COMPARE_MISSING_DIRECTORY_IS_FILE', 'PASS: TREE_COMPARE_VACUOUS_REFUSED',
    'PASS: TREE_COMPARE_NESTED_EXTRA_FAILS', 'PASS: TREE_COMPARE_RELATIVE_PATHS_FOLLOW_LOCATION')
# Round 3 (T3-1): EACH required guard line must appear exactly once. Counting the lines that belong to the set let one name
# printed twice stand in for another that was missing. The rule is judged on two synthetic receipts first, on every run.
function Get-TreeGuardNameProblems([object[]]$Output, [string[]]$Names) {
    foreach ($name in $Names) {
        $seen = @($Output | Where-Object { "$_" -ceq $name }).Count
        if ($seen -ne 1) { "$name (seen $seen)" }
    }
}
$healthyGuardProblems = @(Get-TreeGuardNameProblems $treeGuardNames $treeGuardNames)
# One name printed twice, another present only in the wrong letter case (so it counts as missing): both must be named.
$swappedGuardProblems = @(Get-TreeGuardNameProblems @($treeGuardNames[0], $treeGuardNames[0], $treeGuardNames[1].ToLowerInvariant(),
    $treeGuardNames[2], $treeGuardNames[3]) $treeGuardNames)
if ($healthyGuardProblems.Count -ne 0 -or $swappedGuardProblems.Count -ne 2 -or
    $swappedGuardProblems -cnotcontains "$($treeGuardNames[0]) (seen 2)" -or $swappedGuardProblems -cnotcontains "$($treeGuardNames[1]) (seen 0)") {
    throw "AgentBroker tree compare guard-name rule failed its synthetic receipts: healthy [$($healthyGuardProblems -join '; ')], one name repeated and one missing [$($swappedGuardProblems -join '; ')]."
}
Write-Host "PASS: the tree compare guard-name rule refuses a repeated name standing in for a missing one ($($swappedGuardProblems -join '; '))"
foreach ($edition in @(@{ Name = 'Windows PowerShell'; Exe = 'powershell.exe'; Args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $treeCompare, '-SelfTest') },
                       @{ Name = 'PowerShell 7'; Exe = 'pwsh'; Args = @('-NoProfile', '-File', $treeCompare, '-SelfTest') })) {
    Write-Host "=== AgentBroker tree compare self-test ($($edition.Name))"
    $treeOutput = @(& $edition.Exe @($edition.Args))
    $treeExit = $LASTEXITCODE
    foreach ($line in $treeOutput) { Write-Host $line }
    $treeSummary = @($treeOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) tree compare fixture checks$' })
    $treeGuardProblems = @(Get-TreeGuardNameProblems $treeOutput $treeGuardNames)
    $treeLabelProblems = @(Get-TreeFixtureLabelProblems $treeOutput $treeFixtureLabels $treeSkippable)
    foreach ($problem in $treeLabelProblems) { Write-Host "TREE FIXTURE LABEL PROBLEM under $($edition.Name): $problem" }
    if ($treeExit -ne 0 -or $treeSummary.Count -ne 1 -or $treeGuardProblems.Count -gt 0 -or $treeLabelProblems.Count -gt 0 -or
        @($treeOutput | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL)' }).Count -gt 0) {
        throw "AgentBroker tree compare self-test under $($edition.Name) failed: tools/agentbroker/Compare-AgentBrokerTree.ps1 -SelfTest (exit $treeExit; guard lines missing or repeated: $($treeGuardProblems -join '; '); fixture labels: $($treeLabelProblems -join '; '))."
    }
    $treeChecks[$edition.Name] = [int]([regex]::Match([string]$treeSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
    $treeSkipLines = @($treeOutput | Where-Object { "$_" -cmatch '^SKIP: ' })
    $treeSkipped[$edition.Name] = $treeSkipLines.Count
    foreach ($skip in $treeSkipLines) { Write-Host "DEGRADED: tree compare under $($edition.Name) did not run a fixture: $skip" }
}
$treeDegraded = ''
if ($treeSkipped['Windows PowerShell'] + $treeSkipped['PowerShell 7'] -gt 0) {
    $treeDegraded = " DEGRADED: tree compare skipped $($treeSkipped['Windows PowerShell']) fixture(s) (PowerShell 7: $($treeSkipped['PowerShell 7'])); see the DEGRADED lines above."
}
# The scheduled re-pin check's local runner (lane broker-pins): its selection, binding and de-duplication rules, and the
# workflow file's contract (two permissions, SHA-pinned actions, never a merge). PowerShell 7 only; offline.
Write-Host '=== AgentBroker scheduled re-pin check self-test'
$repinCheckOutput = @(& pwsh -NoProfile -File (Join-Path $repoRoot 'tools\agentbroker\Invoke-AgentBrokerRepinCheck.ps1') -SelfTest)
$repinCheckExit = $LASTEXITCODE
foreach ($line in $repinCheckOutput) { Write-Host $line }
$repinCheckSummary = @($repinCheckOutput | Where-Object { "$_" -match '^PASS: ([1-9][0-9]*) re-pin check fixture checks$' })
if ($repinCheckExit -ne 0 -or $repinCheckSummary.Count -ne 1 -or @($repinCheckOutput | Where-Object { "$_" -match '^(FAIL:|RESULT=FAIL)' }).Count -gt 0) {
    throw "AgentBroker scheduled re-pin check self-test failed: tools/agentbroker/Invoke-AgentBrokerRepinCheck.ps1 -SelfTest (exit $repinCheckExit)."
}
$repinCheckChecks = [int]([regex]::Match([string]$repinCheckSummary[0], '^PASS: ([0-9]+)').Groups[1].Value)
# BEGIN AgentBroker LAN certificate self-test
Write-Host '=== AgentBroker public LAN certificate self-test'
$lanPublicOutput = @(& pwsh -NoProfile -File (Join-Path $repoRoot 'tools\agentbroker\Test-AgentBrokerLan.ps1') -SelfTest)
$lanPublicExit = $LASTEXITCODE
foreach ($line in $lanPublicOutput) { Write-Host $line }
$lanPublicNames = @('LEGACY_EXACT_PIN','RENEWED_ANCHOR','BROKER_ID_REFUSED','CHANGED_KEY_REFUSED','CHANGED_PIN_REFUSED',
    'EXPIRED_LIVE_REFUSED','PUBLIC_ONLY_CLIENT_DECLINED','PUBLIC_DECODER_REFUSES_OTHER_CONTENT','PUBLIC_METADATA_MATCH','PUBLIC_METADATA_BROKER_REFUSED')
$lanPublicMissing = @($lanPublicNames | Where-Object { $name = $_; @($lanPublicOutput | Where-Object { "$_" -ceq ('PASS: LAN_' + $name) }).Count -ne 1 })
$lanPublicSummary = @($lanPublicOutput | Where-Object { "$_" -ceq 'PASS: 10 AgentBroker LAN certificate checks; no connections opened' })
if ($lanPublicExit -ne 0 -or $lanPublicOutput.Count -ne 11 -or $lanPublicSummary.Count -ne 1 -or $lanPublicMissing.Count -ne 0) {
    throw 'AgentBroker public LAN certificate self-test failed: Test-AgentBrokerLan.ps1 -SelfTest.'
}
# END AgentBroker LAN certificate self-test
$harnesses = @(
    @{ Name = 'Core'; Project = 'tests\AgentBroker.Core.Tests\AgentBroker.Core.Tests.csproj'
       Executable = "tests\AgentBroker.Core.Tests\bin\$Configuration\net10.0\AgentBroker.Core.Tests.exe"
       Summary = '^AgentBroker core smoke and regression: ([1-9][0-9]*) assertions passed\.$' },
    @{ Name = 'Relay'; Project = 'tests\AgentBroker.Relay.Tests\AgentBroker.Relay.Tests.csproj'
       Executable = "tests\AgentBroker.Relay.Tests\bin\$Configuration\AgentBroker.Relay.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' },
    @{ Name = 'Claude'; Project = 'tests\AgentBroker.Claude.Tests\AgentBroker.Claude.Tests.csproj'
       Executable = "tests\AgentBroker.Claude.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Claude.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' },
    @{ Name = 'Desktop'; Project = 'tests\AgentBroker.Desktop.Tests\AgentBroker.Desktop.Tests.csproj'
       Executable = "tests\AgentBroker.Desktop.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Desktop.Tests.exe"
       Summary = '^AgentBroker desktop synthetic checks passed: ([1-9][0-9]*)$' },
    @{ Name = 'Adapter'; Project = 'tests\AgentBroker.Adapter.Tests\AgentBroker.Adapter.Tests.csproj'
       Executable = "tests\AgentBroker.Adapter.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Adapter.Tests.exe"
       Summary = '^AgentBroker native adapter regression: ([1-9][0-9]*) assertions passed\.$' },
    @{ Name = 'Network'; Project = 'tests\AgentBroker.Network.Tests\AgentBroker.Network.Tests.csproj'
       Executable = "tests\AgentBroker.Network.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Network.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' },
    @{ Name = 'Worker'; Project = 'tests\AgentBroker.Worker.Tests\AgentBroker.Worker.Tests.csproj'
       Executable = "tests\AgentBroker.Worker.Tests\bin\$Configuration\net10.0-windows\AgentBroker.Worker.Tests.exe"
       Summary = '^RESULT=PASS assertions=([1-9][0-9]*)$' }
)
$failures = New-Object 'Collections.Generic.List[string]'
# Historical 2026-11-10 shipping cadence inventories: 3718 total; Worker 2008 -> 2021.
# Current floors are the measured 2026-11-12 1.10.5 G2 inventories (4057 total; Core 643, Worker 2234).
# Intentional suite removal needs an explicit review.
$assertionFloors = @{ Core = 643; Relay = 40; Claude = 212; Desktop = 594; Adapter = 84; Network = 250; Worker = 2234 }
$assertions = 0
foreach ($harness in $harnesses) {
    Write-Host ("=== AgentBroker " + $harness.Name + ' deterministic harness')
    try {
        $project = Join-Path $repoRoot $harness.Project
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Project missing: $project" }
        & dotnet build $project -c $Configuration --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
        $executable = Join-Path $repoRoot $harness.Executable
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Built executable missing: $executable" }
        $output = @(& $executable)
        $harnessExit = $LASTEXITCODE
        foreach ($line in $output) { Write-Host $line }
        if ($harnessExit -ne 0) { throw "Harness failed (exit $harnessExit)." }
        $assertions += Assert-HarnessSummary $output $harness.Name $harness.Summary $assertionFloors[$harness.Name]
    }
    catch { $failures.Add($harness.Name + ': ' + $_.Exception.Message) }
}
if ($failures.Count -gt 0) { throw ('AgentBroker checks failed: ' + ($failures -join '; ')) }
Write-Host ("AgentBroker deterministic harnesses PASS: $($harnesses.Count) harnesses, $assertions assertions; Claude connection helper $connectChecks checks; collaboration helper $collaborationChecks checks (process: $collaborationProcessChecks); re-pin helper $repinChecks checks (PowerShell 7: $repin7Checks); tree compare $($treeChecks['Windows PowerShell']) checks (PowerShell 7: $($treeChecks['PowerShell 7'])); scheduled re-pin check $repinCheckChecks checks.$treeDegraded")
