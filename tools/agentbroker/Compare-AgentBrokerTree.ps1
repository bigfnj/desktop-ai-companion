#requires -Version 5.1
<#
.SYNOPSIS
    Compare an installed AgentBroker folder with a staged entries map or a provenance output receipt.
.DESCRIPTION
    AgentBroker 1.10.5 marathon, lane T (C4; check hazard 15 of the 1.10.6 index). The 1.10.5 candidate's entries.json carries
    NO "Directory" field on any of its 357 rows, while the 1.10.4 map says "Directory": false on each. A filter written as
    Directory -eq $false therefore selected zero rows of the 1.10.5 map, and a comparison over zero rows reported
    "0 mismatched" and passed: the lead's own first query did exactly that on 2026-11-13. This helper treats a missing or null
    Directory as a file, refuses a map whose row count differs from what it declares (EntryCount, or a receipt's fileCount),
    refuses a map of zero rows, and compares the byte count and SHA-256 of every file. A file in the folder that the map does
    not name is a difference too: the installed module folder holds the payload only, its data lives elsewhere
    (Install-LocalAgentBroker.ps1 keeps <LOCALAPPDATA>\Programs\...\modules\agentbroker apart from
    <LOCALAPPDATA>\DesktopAICompanion\modules\agentbroker).
    Read-only: it hashes files and writes nothing, except -SelfTest, which builds and removes its own fixture folder.
    Exit 0 on a full match, 1 on any difference or refusal. Relative -Path and -Map resolve against the PowerShell location.
    Inputs. The entries map is written by the marathon's release-evidence inventory tooling, which lives OUTSIDE this repository
    (the staged candidate105\entries.json and rollback104\entries.json of the 2026-11-13 B transfer are examples); EntryCount
    counts every row, directory rows included where a map has them. The output receipt is the provenance tool's
    *.output.json (fileCount, files). Redacted three-row samples of each shape are in tree-compare-samples\ beside this file,
    and -SelfTest reads them.
.EXAMPLE
    pwsh -NoProfile -File tools\agentbroker\Compare-AgentBrokerTree.ps1 -Path "$env:LOCALAPPDATA\Programs\Desktop AI Companion\modules\agentbroker" -Map .\entries.json
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\agentbroker\Compare-AgentBrokerTree.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'Compare')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Compare')][string]$Path,
    [Parameter(Mandatory = $true, ParameterSetName = 'Compare')][string]$Map,
    [Parameter(Mandatory = $true, ParameterSetName = 'SelfTest')][switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256Hex([string]$file) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

# Relative arguments resolve against the PowerShell location, never the .NET process directory (round 2, T2-8, X1's T-1): with
# the two different, [IO.Path]::GetFullPath alone compared the wrong folder and printed a match for it.
function Resolve-UserPath([string]$UserPath) {
    return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($UserPath))
}

function Test-HasProperty([object]$item, [string]$name) {
    return $null -ne $item -and @($item.PSObject.Properties.Name) -contains $name
}

# Rows as @{ Name; Bytes; Sha256; Directory }, with Directory kept exactly as the map says it ($null when absent).
function Read-TreeMap([string]$mapPath) {
    $json = [IO.File]::ReadAllText((Resolve-UserPath $mapPath)) | ConvertFrom-Json
    $rows = New-Object 'Collections.Generic.List[object]'
    if ((Test-HasProperty $json 'Entries') -and (Test-HasProperty $json 'EntryCount')) {
        $kind = 'entries map'; $declared = [int]$json.EntryCount
        foreach ($entry in @($json.Entries)) {
            $directory = $null
            if (Test-HasProperty $entry 'Directory') { $directory = $entry.Directory }
            $rows.Add(@{ Name = [string]$entry.Name; Bytes = [long]$entry.Bytes; Sha256 = [string]$entry.Sha256; Directory = $directory })
        }
    }
    elseif ((Test-HasProperty $json 'files') -and (Test-HasProperty $json 'fileCount')) {
        $kind = 'output receipt'; $declared = [int]$json.fileCount
        foreach ($file in @($json.files)) {
            $rows.Add(@{ Name = [string]$file.path; Bytes = [long]$file.bytes; Sha256 = [string]$file.sha256; Directory = $null })
        }
    }
    else { throw 'TREE_MAP_UNRECOGNISED: neither an entries map (EntryCount, Entries) nor an output receipt (fileCount, files)' }
    return @{ Kind = $kind; Declared = $declared; Rows = $rows.ToArray() }
}

function Compare-AgentBrokerTree([string]$root, [string]$mapPath) {
    $tree = Read-TreeMap $mapPath
    $rows = @($tree.Rows)
    # Zero rows first: a map that names nothing compares nothing, and "0 mismatched" over nothing is not a match.
    if ($rows.Count -eq 0) { throw "TREE_COMPARE_VACUOUS: the $($tree.Kind) has no rows, so a comparison would pass by comparing nothing" }
    if ($rows.Count -ne $tree.Declared) { throw "TREE_COMPARE_COUNT_MISMATCH: the $($tree.Kind) holds $($rows.Count) rows but declares $($tree.Declared)" }
    $rootFull = Resolve-UserPath $root
    # A drive or share root is never a module folder, and trimming its separator made it drive-relative (T2-15).
    if ($rootFull.TrimEnd('\') -ieq ([IO.Path]::GetPathRoot($rootFull)).TrimEnd('\')) { throw "TREE_COMPARE_ROOT_REFUSED: $rootFull" }
    $rootFull = $rootFull.TrimEnd('\')
    if (-not [IO.Directory]::Exists($rootFull)) { throw "TREE_COMPARE_NO_FOLDER: $rootFull" }
    # A missing or null Directory is a file; only an explicit true is a directory row, counted above and not compared.
    $files = @($rows | Where-Object { $_.Directory -ne $true })
    $expected = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $files) {
        # Mirrors the installer's entry-name rule (T2-9): a leading .\ is dropped; an empty, '.' or '..' segment, a trailing
        # separator on a file row, or a rooted name is refused. Names compare case-insensitively, as the file system does,
        # and a second row for the same file is refused rather than silently replacing the first (T2-7).
        $relative = $row.Name.Replace('/', '\')
        if ($relative.StartsWith('.\', [StringComparison]::Ordinal)) { $relative = $relative.Substring(2) }
        if ($relative.Length -eq 0 -or [IO.Path]::IsPathRooted($relative) -or
            @($relative.Split('\') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
            throw "TREE_MAP_NAME_REFUSED: $($row.Name)"
        }
        if ($expected.ContainsKey($relative)) { throw "TREE_MAP_DUPLICATE_NAME: $($row.Name)" }
        $expected[$relative] = $row
    }
    $matched = 0
    $mismatched = New-Object 'Collections.Generic.List[string]'
    $missing = New-Object 'Collections.Generic.List[string]'
    foreach ($relative in $expected.Keys) {
        $row = $expected[$relative]
        $full = Join-Path $rootFull $relative
        if (-not [IO.File]::Exists($full)) { $missing.Add($relative); continue }
        $bytes = (New-Object IO.FileInfo $full).Length
        $hash = Get-Sha256Hex $full
        if ($bytes -eq $row.Bytes -and $hash -ieq $row.Sha256) { $matched++ }
        else { $mismatched.Add("$relative (bytes $bytes, map $($row.Bytes))") }
    }
    $extra = New-Object 'Collections.Generic.List[string]'
    foreach ($full in [IO.Directory]::EnumerateFiles($rootFull, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = $full.Substring($rootFull.Length + 1)
        if (-not $expected.ContainsKey($relative)) { $extra.Add($relative) }
    }
    return [pscustomobject]@{
        Kind = $tree.Kind; Rows = $rows.Count; Files = $files.Count; Matched = $matched
        Mismatched = @($mismatched.ToArray()); Missing = @($missing.ToArray()); Extra = @($extra.ToArray())
        Match = ($files.Count -gt 0 -and $matched -eq $files.Count -and $mismatched.Count -eq 0 -and $missing.Count -eq 0 -and $extra.Count -eq 0)
    }
}

function Write-TreeResult([object]$result) {
    Write-Output ("TREE_COMPARE: {0}, {1} rows, {2} files: matched {3}, mismatched {4}, missing {5}, extra {6}" -f
        $result.Kind, $result.Rows, $result.Files, $result.Matched, $result.Mismatched.Count, $result.Missing.Count, $result.Extra.Count)
    foreach ($line in $result.Mismatched) { Write-Output "MISMATCHED: $line" }
    foreach ($line in $result.Missing) { Write-Output "NOT IN FOLDER: $line" }
    foreach ($line in $result.Extra) { Write-Output "NOT IN MAP: $line" }
    if ($result.Match) { Write-Output "TREE_COMPARE_MATCH: $($result.Matched)/$($result.Files)" } else { Write-Output 'TREE_COMPARE_DIFFERS' }
}

if (-not $SelfTest) {
    $result = Compare-AgentBrokerTree $Path $Map
    Write-TreeResult $result
    if ($result.Match) { exit 0 } else { exit 1 }
}

# ---- -SelfTest: synthetic fixtures only; one owned folder under TEMP, removed at the end ----
$scratchBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $scratchBase ('agentbroker-tree-compare-' + [guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$passed = 0
$failures = New-Object 'Collections.Generic.List[string]'
function Assert-Case([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) { Write-Output "PASS: $name"; $script:passed++ }
    else { Write-Output "FAIL: $name ($detail)"; $script:failures.Add($name) }
}
function New-Folder([string]$name, [hashtable]$contents) {
    $root = Join-Path $scratch $name
    [void][IO.Directory]::CreateDirectory($root)
    foreach ($relative in $contents.Keys) {
        $full = Join-Path $root $relative
        [void][IO.Directory]::CreateDirectory((Split-Path $full -Parent))
        [IO.File]::WriteAllText($full, $contents[$relative], $utf8)
    }
    return $root
}
function Get-Row([string]$root, [string]$name) {
    $full = Join-Path $root $name.Replace('/', '\')
    return [ordered]@{ Name = $name; Bytes = (New-Object IO.FileInfo $full).Length; Sha256 = (Get-Sha256Hex $full) }
}
function Write-Map([string]$name, [object]$map) {
    $path = Join-Path $scratch ($name + '.json')
    [IO.File]::WriteAllText($path, ($map | ConvertTo-Json -Depth 5), $utf8)
    return $path
}
# A positive fixture that is REFUSED fails at its own name, and the fixtures after it still run (round 2: two mutants that
# made Compare throw inside a positive fixture were reported only as "the self-test threw", with every later case skipped).
function Get-Compare([string]$root, [string]$mapPath) {
    try { return (Compare-AgentBrokerTree $root $mapPath) }
    catch {
        Write-Host "  refused: $($_.Exception.Message)"
        return [pscustomobject]@{ Kind = 'refused'; Rows = -1; Files = -1; Matched = -1; Mismatched = @(); Missing = @(); Extra = @(); Match = $false }
    }
}
function Get-Refusal([scriptblock]$action) {
    try { & $action | Out-Null; return '' } catch { return $_.Exception.Message }
}
try {
    [void][IO.Directory]::CreateDirectory($scratch)
    $payload = @{ 'AgentBroker.dll' = 'module'; 'worker\AgentBroker.Worker.dll' = 'worker' }
    $live = New-Folder 'live' $payload
    $rowModule = Get-Row $live 'AgentBroker.dll'
    $rowWorker = Get-Row $live 'worker/AgentBroker.Worker.dll'

    # 1.10.5 shape: no Directory field anywhere.
    $map105 = Write-Map 'map105' ([ordered]@{ EntryCount = 2; Entries = @($rowModule, $rowWorker) })
    $r = Get-Compare $live $map105
    Assert-Case 'TREE_COMPARE_MISSING_DIRECTORY_IS_FILE' ($r.Match -and $r.Matched -eq 2 -and $r.Files -eq 2) "matched $($r.Matched) of $($r.Files)"

    # 1.10.4 shape: "Directory": false on each row (witness).
    $legacy = @($rowModule, $rowWorker) | ForEach-Object { $copy = [ordered]@{}; foreach ($k in $_.Keys) { $copy[$k] = $_[$k] }; $copy['Directory'] = $false; $copy }
    $r = Get-Compare $live (Write-Map 'map104' ([ordered]@{ EntryCount = 2; Entries = @($legacy) }))
    Assert-Case 'TREE_COMPARE_LEGACY_DIRECTORY_FALSE' ($r.Match -and $r.Matched -eq 2) "matched $($r.Matched)"

    # A directory row is counted against EntryCount and not compared as a file (witness).
    $dirRow = [ordered]@{ Name = 'worker/'; Bytes = 0; Sha256 = ''; Directory = $true }
    $r = Get-Compare $live (Write-Map 'mapdir' ([ordered]@{ EntryCount = 3; Entries = @($rowModule, $rowWorker, $dirRow) }))
    Assert-Case 'TREE_COMPARE_DIRECTORY_ROW_NOT_COMPARED' ($r.Match -and $r.Matched -eq 2 -and $r.Rows -eq 3) "matched $($r.Matched), rows $($r.Rows)"

    # The provenance output receipt's shape.
    $receiptRows = @($rowModule, $rowWorker) | ForEach-Object { [ordered]@{ path = $_.Name; bytes = $_.Bytes; sha256 = $_.Sha256 } }
    $r = Get-Compare $live (Write-Map 'receipt' ([ordered]@{ fileCount = 2; files = @($receiptRows) }))
    Assert-Case 'TREE_COMPARE_RECEIPT_SHAPE' ($r.Match -and $r.Matched -eq 2 -and $r.Kind -eq 'output receipt') "matched $($r.Matched), kind $($r.Kind)"

    # Nothing compared is not a match: an empty map over an empty folder.
    $empty = New-Folder 'empty' @{}
    $why = Get-Refusal { Compare-AgentBrokerTree $empty (Write-Map 'mapempty' ([ordered]@{ EntryCount = 0; Entries = @() })) }
    Assert-Case 'TREE_COMPARE_VACUOUS_REFUSED' ($why -like 'TREE_COMPARE_VACUOUS:*') $why

    # Rows that disagree with the declared count.
    $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'mapcount' ([ordered]@{ EntryCount = 3; Entries = @($rowModule, $rowWorker) })) }
    Assert-Case 'TREE_COMPARE_COUNT_MISMATCH_REFUSED' ($why -like 'TREE_COMPARE_COUNT_MISMATCH:*') $why

    $bad = [ordered]@{ Name = $rowModule.Name; Bytes = $rowModule.Bytes; Sha256 = ('0' * 64) }
    $r = Get-Compare $live (Write-Map 'maphash' ([ordered]@{ EntryCount = 2; Entries = @($bad, $rowWorker) }))
    Assert-Case 'TREE_COMPARE_HASH_MISMATCH_FAILS' (-not $r.Match -and $r.Mismatched.Count -eq 1) "mismatched $($r.Mismatched.Count)"

    $absent = [ordered]@{ Name = 'worker/Absent.dll'; Bytes = 1; Sha256 = ('0' * 64) }
    $r = Get-Compare $live (Write-Map 'mapmissing' ([ordered]@{ EntryCount = 3; Entries = @($rowModule, $rowWorker, $absent) }))
    Assert-Case 'TREE_COMPARE_MISSING_FAILS' (-not $r.Match -and $r.Missing.Count -eq 1) "missing $($r.Missing.Count)"

    $withExtra = New-Folder 'extra' (@{ 'AgentBroker.dll' = 'module'; 'worker\AgentBroker.Worker.dll' = 'worker'; 'stray.txt' = 'x' })
    $r = Get-Compare $withExtra $map105
    Assert-Case 'TREE_COMPARE_EXTRA_FAILS' (-not $r.Match -and $r.Extra.Count -eq 1) "extra $($r.Extra.Count)"

    $escape = [ordered]@{ Name = '../outside.dll'; Bytes = 1; Sha256 = ('0' * 64) }
    $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'mapescape' ([ordered]@{ EntryCount = 1; Entries = @($escape) })) }
    Assert-Case 'TREE_COMPARE_NAME_OUTSIDE_REFUSED' ($why -like 'TREE_MAP_NAME_REFUSED:*') $why

    # ---- round 2 (T2-1, T2-5..T2-10, T2-15; X1's T-1, T-2, T-3, T-5) ----
    # A stray file BELOW the root is extra too (T2-1): 354 of the 357 real rows live under worker\.
    $nested = New-Folder 'nested' (@{ 'AgentBroker.dll' = 'module'; 'worker\AgentBroker.Worker.dll' = 'worker'; 'worker\stale.dll' = 'old' })
    $r = Get-Compare $nested $map105
    Assert-Case 'TREE_COMPARE_NESTED_EXTRA_FAILS' (-not $r.Match -and $r.Extra.Count -eq 1 -and $r.Extra[0] -eq 'worker\stale.dll') "extra $($r.Extra -join ',')"

    # Bytes as well as the hash (T2-6): the right hash with one byte more is a mismatch.
    $longer = [ordered]@{ Name = $rowModule.Name; Bytes = $rowModule.Bytes + 1; Sha256 = $rowModule.Sha256 }
    $r = Get-Compare $live (Write-Map 'mapbytes' ([ordered]@{ EntryCount = 2; Entries = @($longer, $rowWorker) }))
    Assert-Case 'TREE_COMPARE_BYTES_MISMATCH_FAILS' (-not $r.Match -and $r.Mismatched.Count -eq 1) "mismatched $($r.Mismatched.Count)"

    # The receipt shape is refused the same two ways as the entries map (T2-5).
    $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'receiptcount' ([ordered]@{ fileCount = 3; files = @($receiptRows) })) }
    Assert-Case 'TREE_COMPARE_RECEIPT_COUNT_MISMATCH_REFUSED' ($why -like 'TREE_COMPARE_COUNT_MISMATCH:*') $why
    $why = Get-Refusal { Compare-AgentBrokerTree $empty (Write-Map 'receiptempty' ([ordered]@{ fileCount = 0; files = @() })) }
    Assert-Case 'TREE_COMPARE_RECEIPT_VACUOUS_REFUSED' ($why -like 'TREE_COMPARE_VACUOUS:*') $why

    # Map names (T2-7, T2-9): a case-colliding second row is refused; a leading ./ is dropped; an empty segment or a trailing
    # separator on a file row is refused.
    $twinRow = [ordered]@{ Name = 'agentbroker.DLL'; Bytes = $rowModule.Bytes; Sha256 = $rowModule.Sha256 }
    $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'mapdup' ([ordered]@{ EntryCount = 3; Entries = @($rowModule, $twinRow, $rowWorker) })) }
    Assert-Case 'TREE_COMPARE_DUPLICATE_NAME_REFUSED' ($why -like 'TREE_MAP_DUPLICATE_NAME:*') $why
    $dotted = [ordered]@{ Name = './AgentBroker.dll'; Bytes = $rowModule.Bytes; Sha256 = $rowModule.Sha256 }
    $r = Get-Compare $live (Write-Map 'mapdot' ([ordered]@{ EntryCount = 2; Entries = @($dotted, $rowWorker) }))
    Assert-Case 'TREE_COMPARE_DOT_PREFIX_ACCEPTED' ($r.Match -and $r.Matched -eq 2) "matched $($r.Matched)"
    $gap = [ordered]@{ Name = 'worker//AgentBroker.Worker.dll'; Bytes = $rowWorker.Bytes; Sha256 = $rowWorker.Sha256 }
    $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'mapgap' ([ordered]@{ EntryCount = 2; Entries = @($rowModule, $gap) })) }
    $trail = [ordered]@{ Name = 'AgentBroker.dll/'; Bytes = $rowModule.Bytes; Sha256 = $rowModule.Sha256 }
    $why2 = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'maptrail' ([ordered]@{ EntryCount = 2; Entries = @($trail, $rowWorker) })) }
    Assert-Case 'TREE_COMPARE_EMPTY_SEGMENT_REFUSED' ($why -like 'TREE_MAP_NAME_REFUSED:*' -and $why2 -like 'TREE_MAP_NAME_REFUSED:*') "$why | $why2"

    # Relative arguments follow the PowerShell location, not the process directory (T2-8): side A holds a matching tree and map,
    # side B a mismatching one; with the process in A and the location in B, the relative compare must judge B.
    $sideA = New-Folder 'side-a' @{}
    $sideB = New-Folder 'side-b' @{}
    [void](New-Folder 'side-a\payload' $payload)
    [void](New-Folder 'side-b\payload' (@{ 'AgentBroker.dll' = 'changed'; 'worker\AgentBroker.Worker.dll' = 'worker' }))
    foreach ($side in @($sideA, $sideB)) {
        [IO.File]::WriteAllText((Join-Path $side 'entries.json'), ([ordered]@{ EntryCount = 2; Entries = @($rowModule, $rowWorker) } | ConvertTo-Json -Depth 5), $utf8)
    }
    $savedProcessDirectory = [Environment]::CurrentDirectory
    Push-Location -LiteralPath $sideB
    try {
        [Environment]::CurrentDirectory = $sideA
        $r = Get-Compare '.\payload' '.\entries.json'
    }
    finally { [Environment]::CurrentDirectory = $savedProcessDirectory; Pop-Location }
    Assert-Case 'TREE_COMPARE_RELATIVE_PATHS_FOLLOW_LOCATION' (-not $r.Match -and $r.Mismatched.Count -eq 1) "match $($r.Match), mismatched $($r.Mismatched.Count)"

    # A drive root is refused before anything is enumerated (T2-15). The process directory is an EMPTY folder on that drive for
    # the call, so a mutant without the refusal resolves "<drive>:" to that empty folder instead of walking a real drive.
    $emptyHere = New-Folder 'root-guard' @{}
    $scratchDrive = [IO.Path]::GetPathRoot($scratch)
    $savedProcessDirectory = [Environment]::CurrentDirectory
    try {
        [Environment]::CurrentDirectory = $emptyHere
        $why = Get-Refusal { Compare-AgentBrokerTree $scratchDrive $map105 }
    }
    finally { [Environment]::CurrentDirectory = $savedProcessDirectory }
    Assert-Case 'TREE_COMPARE_DRIVE_ROOT_REFUSED' ($why -like 'TREE_COMPARE_ROOT_REFUSED:*') $why

    # The committed redacted samples of each input shape read as that shape (T2-10).
    $samples = Join-Path $PSScriptRoot 'tree-compare-samples'
    $s105 = Read-TreeMap (Join-Path $samples 'entries-1.10.5.sample.json')
    $s104 = Read-TreeMap (Join-Path $samples 'entries-1.10.4.sample.json')
    $sRec = Read-TreeMap (Join-Path $samples 'output-receipt.sample.json')
    Assert-Case 'TREE_COMPARE_SAMPLE_SHAPES_READ' (
        $s105.Kind -eq 'entries map' -and $s105.Declared -eq 3 -and @($s105.Rows).Count -eq 3 -and $null -eq @($s105.Rows)[0].Directory -and
        $s104.Kind -eq 'entries map' -and @($s104.Rows).Count -eq 3 -and @($s104.Rows)[0].Directory -eq $false -and
        $sRec.Kind -eq 'output receipt' -and $sRec.Declared -eq 3 -and @($sRec.Rows).Count -eq 3) 'sample shapes'
}
catch {
    $failures.Add('EXC: ' + $_.Exception.Message)
    Write-Output ('FAIL: the self-test threw: ' + $_.Exception.Message)
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith(($scratchBase.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -cmatch '^agentbroker-tree-compare-[0-9a-f]{32}$' -and [IO.Directory]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
if ($failures.Count -gt 0) {
    Write-Output "RESULT=FAIL: $($failures.Count) tree compare fixture check(s) failed"
    exit 1
}
Write-Output "PASS: $passed tree compare fixture checks"
exit 0
