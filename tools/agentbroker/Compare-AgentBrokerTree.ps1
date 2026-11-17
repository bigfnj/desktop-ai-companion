#requires -Version 5.1
<#
.SYNOPSIS
    Compare an installed AgentBroker folder with a staged entries map or a provenance output receipt.
.DESCRIPTION
    AgentBroker 1.10.5 marathon, lane T (C4; check hazard 15 of the 1.10.6 index). The 2026-11-13 candidate map (357 rows, none
    with a "Directory" field; hazard 15 in the marathon's BACKLOG-1106-INDEX) differs from the 1.10.4 map, which says
    "Directory": false on each row. A filter written as Directory -eq $false therefore selected zero rows of the 1.10.5 map, and
    a comparison over zero rows reported "0 mismatched" and passed: the lead's own first query did exactly that on 2026-11-13.
    This helper treats a missing or null Directory as a file, refuses a map whose row count differs from what it declares
    (EntryCount, or a receipt's fileCount), refuses a map of zero rows and a row without its name, size or digest, and compares
    the byte count and SHA-256 of every file. A file in the folder that the map does not name is a difference too, and so is a
    second spelling of a mapped name in a case-sensitive directory: each map row and each file on disk is matched at most once.
    The installed module folder holds the payload only, its data lives elsewhere (Install-LocalAgentBroker.ps1 keeps
    <LOCALAPPDATA>\Programs\...\modules\agentbroker apart from <LOCALAPPDATA>\DesktopAICompanion\modules\agentbroker).
    Reparse points under the folder (junctions, symbolic links, mount points) are refused by name before anything below them is
    read, as the installer refuses link and reparse entries. Directories themselves are not compared: an extra empty directory,
    or a directory row with no folder behind it, does not change the result. Digests compare case-insensitively: the provenance
    receipt carries upper-case hex (Get-FileHash), and the entries map comes from tooling outside this repository.
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

# A row missing a field the compare reads is refused by name (round 5, K3): under strict mode it used to end in PowerShell's
# generic property error, with no TREE_MAP_* line saying which row was incomplete.
function Assert-RowFields([object]$row, [int]$index, [string[]]$fields) {
    foreach ($field in $fields) {
        if (-not (Test-HasProperty $row $field)) { throw "TREE_MAP_ROW_INCOMPLETE: $index missing $field" }
    }
}

# Rows as @{ Name; Bytes; Sha256; Directory }, with Directory kept exactly as the map says it ($null when absent).
function Read-TreeMap([string]$mapPath) {
    $json = [IO.File]::ReadAllText((Resolve-UserPath $mapPath)) | ConvertFrom-Json
    $rows = New-Object 'Collections.Generic.List[object]'
    if ((Test-HasProperty $json 'Entries') -and (Test-HasProperty $json 'EntryCount')) {
        $kind = 'entries map'; $declared = [int]$json.EntryCount
        $index = 0
        foreach ($entry in @($json.Entries)) {
            Assert-RowFields $entry $index @('Name', 'Bytes', 'Sha256')
            $directory = $null
            if (Test-HasProperty $entry 'Directory') { $directory = $entry.Directory }
            $rows.Add(@{ Name = [string]$entry.Name; Bytes = [long]$entry.Bytes; Sha256 = [string]$entry.Sha256; Directory = $directory })
            $index++
        }
    }
    elseif ((Test-HasProperty $json 'files') -and (Test-HasProperty $json 'fileCount')) {
        $kind = 'output receipt'; $declared = [int]$json.fileCount
        $index = 0
        foreach ($file in @($json.files)) {
            Assert-RowFields $file $index @('path', 'bytes', 'sha256')
            $rows.Add(@{ Name = [string]$file.path; Bytes = [long]$file.bytes; Sha256 = [string]$file.sha256; Directory = $null })
            $index++
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
    $exactNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($row in $files) {
        # Shares the installer's segment refusals (Assert-EntryName in Install-LocalAgentBroker.ps1, round 5 C1/C5): a ':' anywhere,
        # a rooted name, and an empty, '.' or '..' segment or one ending in a space or a dot are refused, so a Win32 alias of a
        # mapped name ('AgentBroker.dll.', 'AgentBroker.dll ', 'AgentBroker.dll::$DATA') cannot stand for that file twice. Unlike
        # the installer it accepts '\' as a separator and drops a leading .\ for map producers that emit one; reserved device
        # names and control characters are not checked here, since no enumerated file carries them and such a row reads missing.
        # Names compare case-insensitively, as the file system does, and a second row for the same file is refused rather than
        # silently replacing the first (T2-7).
        $relative = $row.Name.Replace('/', '\')
        if ($relative.StartsWith('.\', [StringComparison]::Ordinal)) { $relative = $relative.Substring(2) }
        if ($relative.Length -eq 0 -or $relative.Contains(':') -or [IO.Path]::IsPathRooted($relative) -or
            @($relative.Split('\') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_ -ne $_.TrimEnd(' ', '.') }).Count -gt 0) {
            throw "TREE_MAP_NAME_REFUSED: $($row.Name)"
        }
        if ($expected.ContainsKey($relative)) { throw "TREE_MAP_DUPLICATE_NAME: $($row.Name)" }
        $expected[$relative] = $row
        [void]$exactNames.Add($relative)
    }
    # The folder is walked one level at a time (round 5, C6/C7): every entry with the ReparsePoint attribute is refused by name
    # before anything below it is read. EnumerateFiles(AllDirectories) followed junctions, so a junctioned worker folder read as
    # MATCH and a junction loop ended in an unhandled IOException.
    $enumerated = New-Object 'Collections.Generic.List[string]'
    $pendingDirectories = New-Object 'Collections.Generic.Stack[string]'
    $pendingDirectories.Push($rootFull)
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "TREE_COMPARE_REPARSE_REFUSED: $($entry.Substring($rootFull.Length + 1))" }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pendingDirectories.Push($entry) }
            else { $enumerated.Add($entry.Substring($rootFull.Length + 1)) }
        }
    }
    # Files are matched by the spelling found on disk (round 5, C1/C2): an exact spelling first, then one differing only in letter
    # case, and each map row is used by at most one file. A second spelling in a case-sensitive directory used to pass the
    # case-insensitive membership test as the mapped file and vanish; now it finds its row already used and is extra.
    $used = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $paired = New-Object 'Collections.Generic.List[string]'
    $unpaired = New-Object 'Collections.Generic.List[string]'
    foreach ($relative in $enumerated) {
        if ($exactNames.Contains($relative)) { [void]$used.Add($relative); $paired.Add($relative) } else { $unpaired.Add($relative) }
    }
    $extra = New-Object 'Collections.Generic.List[string]'
    foreach ($relative in $unpaired) {
        if ($expected.ContainsKey($relative) -and $used.Add($relative)) { $paired.Add($relative) } else { $extra.Add($relative) }
    }
    $matched = 0
    $mismatched = New-Object 'Collections.Generic.List[string]'
    foreach ($relative in $paired) {
        $row = $expected[$relative]
        $full = Join-Path $rootFull $relative
        $bytes = (New-Object IO.FileInfo $full).Length
        $hash = Get-Sha256Hex $full
        if ($bytes -eq $row.Bytes -and $hash -ieq $row.Sha256) { $matched++ }
        else { $mismatched.Add("$relative (bytes $bytes, map $($row.Bytes))") }
    }
    $missing = New-Object 'Collections.Generic.List[string]'
    foreach ($relative in $expected.Keys) {
        if (-not $used.Contains($relative)) { $missing.Add($relative) }
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
# Round 5, K1: the release path (parameter binding, printed verdict, exit code) runs in a child process of each edition. Streams
# are read through Process, so a child's error stream never meets this script's ErrorActionPreference. Arguments are fixed
# switches and paths under this self-test's scratch folder; an argument holding whitespace is wrapped in quotes, and one holding a
# quote or ending in a backslash, which that wrapping does not escape, is refused rather than passed mangled.
function Invoke-CompareChild([string]$exe, [string[]]$arguments) {
    foreach ($argument in $arguments) {
        if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw "child argument cannot be quoted safely: $argument" }
    }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = (@($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "child compare did not exit within 120 s: $exe" }
        $process.WaitForExit()
        return [pscustomobject]@{ Exit = $process.ExitCode; Out = $out.Result; Text = $out.Result + "`n" + $err.Result }
    }
    finally { $process.Dispose() }
}
$junctions = New-Object 'Collections.Generic.List[string]'
function New-Junction([string]$path, [string]$target) {
    [void](New-Item -ItemType Junction -Path $path -Target $target)
    $script:junctions.Add($path)
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

    # The provenance output receipt's shape. Round 5, K2: killed by reading a receipt row's bytes as 0 in Read-TreeMap.
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

    # Relative arguments follow the PowerShell location, not the process directory (T2-8). Side A holds A's payload and map, side B
    # a different payload (changed bytes and a B-only file) with B's own three-row map (round 5, C3: with identical maps a -Map
    # resolved at the process directory passed). With the process in A and the location in B, only -Path AND -Map both resolved
    # in B give a 3/3 match: A's map over B's payload, or B's map over A's payload, differ.
    $sideA = New-Folder 'side-a' @{}
    $sideB = New-Folder 'side-b' @{}
    [void](New-Folder 'side-a\payload' $payload)
    $payloadB = New-Folder 'side-b\payload' (@{ 'AgentBroker.dll' = 'changed'; 'worker\AgentBroker.Worker.dll' = 'worker'; 'b-only.txt' = 'b' })
    [IO.File]::WriteAllText((Join-Path $sideA 'entries.json'), ([ordered]@{ EntryCount = 2; Entries = @($rowModule, $rowWorker) } | ConvertTo-Json -Depth 5), $utf8)
    $rowsB = @((Get-Row $payloadB 'AgentBroker.dll'), (Get-Row $payloadB 'worker/AgentBroker.Worker.dll'), (Get-Row $payloadB 'b-only.txt'))
    [IO.File]::WriteAllText((Join-Path $sideB 'entries.json'), ([ordered]@{ EntryCount = 3; Entries = $rowsB } | ConvertTo-Json -Depth 5), $utf8)
    $savedProcessDirectory = [Environment]::CurrentDirectory
    Push-Location -LiteralPath $sideB
    try {
        [Environment]::CurrentDirectory = $sideA
        $r = Get-Compare '.\payload' '.\entries.json'
    }
    finally { [Environment]::CurrentDirectory = $savedProcessDirectory; Pop-Location }
    Assert-Case 'TREE_COMPARE_RELATIVE_PATHS_FOLLOW_LOCATION' ($r.Match -and $r.Matched -eq 3 -and $r.Files -eq 3) "match $($r.Match), matched $($r.Matched) of $($r.Files)"

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

    # ---- round 5 (the lead's review of 7a9deb86, reviews/T-lead-r2.md: C1, C2, C6/C7, K3, D1, K1) ----
    # Win32 aliases of a mapped name are refused by name (C1): each passed the duplicate guard and matched the one file twice.
    $aliases = @(@{ Case = 'TREE_COMPARE_TRAILING_DOT_REFUSED'; Name = 'AgentBroker.dll.' }, @{ Case = 'TREE_COMPARE_TRAILING_SPACE_REFUSED'; Name = 'AgentBroker.dll ' },
        @{ Case = 'TREE_COMPARE_COLON_REFUSED'; Name = 'AgentBroker.dll::$DATA' })
    foreach ($alias in $aliases) {
        $aliasRow = [ordered]@{ Name = $alias.Name; Bytes = $rowModule.Bytes; Sha256 = $rowModule.Sha256 }
        $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'mapalias' ([ordered]@{ EntryCount = 3; Entries = @($rowModule, $aliasRow, $rowWorker) })) }
        Assert-Case $alias.Case ($why -ceq ('TREE_MAP_NAME_REFUSED: ' + $alias.Name)) $why
    }

    # A second spelling of a mapped name in a case-sensitive directory is extra (C2). The flag needs fsutil and a volume that keeps
    # it; when the two spellings cannot be produced the fixture prints SKIP with the reason (never a silent pass), and
    # Test-AgentBroker reports the degraded run.
    $caseRoot = New-Folder 'case-sensitive' @{ 'AgentBroker.dll' = 'module' }
    $caseDirectory = Join-Path $caseRoot 'worker'
    [void][IO.Directory]::CreateDirectory($caseDirectory)
    $flag = Invoke-CompareChild (Join-Path $env:SystemRoot 'System32\fsutil.exe') @('file', 'setCaseSensitiveInfo', $caseDirectory, 'enable')
    [IO.File]::WriteAllText((Join-Path $caseDirectory 'AgentBroker.Worker.dll'), 'worker', $utf8)
    if ($flag.Exit -eq 0) { [IO.File]::WriteAllText((Join-Path $caseDirectory 'agentbroker.worker.dll'), 'old', $utf8) }
    $spellings = @([IO.Directory]::GetFiles($caseDirectory)).Count
    if ($flag.Exit -ne 0 -or $spellings -ne 2) {
        Write-Output ("SKIP: TREE_COMPARE_CASE_SENSITIVE_SECOND_SPELLING_EXTRA (fsutil setCaseSensitiveInfo exit $($flag.Exit), $spellings spelling(s) kept: " +
            ($flag.Text -replace '\s+', ' ').Trim() + ')')
    }
    else {
        $r = Get-Compare $caseRoot $map105
        Assert-Case 'TREE_COMPARE_CASE_SENSITIVE_SECOND_SPELLING_EXTRA' (-not $r.Match -and $r.Matched -eq 2 -and @($r.Extra).Count -eq 1 -and $r.Extra[0] -ceq 'worker\agentbroker.worker.dll') "match $($r.Match), matched $($r.Matched), extra $($r.Extra -join ',')"
    }

    # Reparse points are refused by name before anything below them is read (C7, C6): a junctioned worker folder holding the right
    # bytes read as MATCH, and a junction back to the root crashed the enumeration.
    $junctionTarget = New-Folder 'junction-target' @{ 'AgentBroker.Worker.dll' = 'worker' }
    $junctioned = New-Folder 'junctioned' @{ 'AgentBroker.dll' = 'module' }
    New-Junction (Join-Path $junctioned 'worker') $junctionTarget
    $why = Get-Refusal { Compare-AgentBrokerTree $junctioned $map105 }
    Assert-Case 'TREE_COMPARE_REPARSE_JUNCTION_REFUSED' ($why -ceq 'TREE_COMPARE_REPARSE_REFUSED: worker') $why
    $looped = New-Folder 'looped' $payload
    New-Junction (Join-Path $looped 'loop') $looped
    $why = Get-Refusal { Compare-AgentBrokerTree $looped $map105 }
    Assert-Case 'TREE_COMPARE_REPARSE_LOOP_REFUSED' ($why -ceq 'TREE_COMPARE_REPARSE_REFUSED: loop') $why

    # A row without a field the compare reads is refused by name (K3), in both shapes.
    $why = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'mapincomplete' ([ordered]@{ EntryCount = 1; Entries = @([ordered]@{ Name = 'AgentBroker.dll'; Bytes = $rowModule.Bytes }) })) }
    $why2 = Get-Refusal { Compare-AgentBrokerTree $live (Write-Map 'receiptincomplete' ([ordered]@{ fileCount = 1; files = @([ordered]@{ path = 'AgentBroker.dll'; sha256 = $rowModule.Sha256 }) })) }
    Assert-Case 'TREE_COMPARE_INCOMPLETE_ROW_REFUSED' ($why -ceq 'TREE_MAP_ROW_INCOMPLETE: 0 missing Sha256' -and $why2 -ceq 'TREE_MAP_ROW_INCOMPLETE: 0 missing bytes') "$why | $why2"

    # Digests in lower case match (D1); the fixtures above all carry Get-Sha256Hex's upper case.
    $lower = @($rowModule, $rowWorker) | ForEach-Object { [ordered]@{ Name = $_.Name; Bytes = $_.Bytes; Sha256 = $_.Sha256.ToLowerInvariant() } }
    $r = Get-Compare $live (Write-Map 'maplower' ([ordered]@{ EntryCount = 2; Entries = @($lower) }))
    Assert-Case 'TREE_COMPARE_LOWERCASE_HASH_ACCEPTED' ($r.Match -and $r.Matched -eq 2) "matched $($r.Matched)"

    # The release path (K1) in a child of each edition: a match exits 0 with its verdict line, a difference exits 1 with
    # TREE_COMPARE_DIFFERS, and a refused map exits 1 naming its refusal. The fixtures above call the function, never these lines.
    $pwshExe = Join-Path $PSHOME 'pwsh.exe'
    if ($PSVersionTable.PSEdition -ne 'Core') {
        $found = @(Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue)
        $pwshExe = if ($found.Count -gt 0) { $found[0].Source } else { '' }
    }
    $mapRefused = Write-Map 'mapchildrefused' ([ordered]@{ EntryCount = 1; Entries = @($escape) })
    foreach ($edition in @(@{ Label = 'PS51'; Exe = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') }, @{ Label = 'PS7'; Exe = $pwshExe })) {
        if ([string]::IsNullOrEmpty($edition.Exe) -or -not [IO.File]::Exists($edition.Exe)) {
            foreach ($childCase in @('MATCH_EXIT_0', 'DIFFERS_EXIT_1', 'REFUSAL_EXIT_1')) { Assert-Case "TREE_COMPARE_CHILD_$($childCase)_$($edition.Label)" $false "no executable for $($edition.Label)" }
            continue
        }
        $childArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
        $c = Invoke-CompareChild $edition.Exe ($childArguments + @('-Path', $live, '-Map', $map105))
        $lines = @($c.Out -split "`r?`n")
        Assert-Case "TREE_COMPARE_CHILD_MATCH_EXIT_0_$($edition.Label)" ($c.Exit -eq 0 -and @($lines | Where-Object { $_ -ceq 'TREE_COMPARE_MATCH: 2/2' }).Count -eq 1) "exit $($c.Exit): $(($c.Text -replace '\s+', ' ').Trim())"
        $c = Invoke-CompareChild $edition.Exe ($childArguments + @('-Path', $withExtra, '-Map', $map105))
        $lines = @($c.Out -split "`r?`n")
        Assert-Case "TREE_COMPARE_CHILD_DIFFERS_EXIT_1_$($edition.Label)" ($c.Exit -eq 1 -and @($lines | Where-Object { $_ -ceq 'TREE_COMPARE_DIFFERS' }).Count -eq 1 -and
            @($lines | Where-Object { $_ -ceq 'NOT IN MAP: stray.txt' }).Count -eq 1) "exit $($c.Exit): $(($c.Text -replace '\s+', ' ').Trim())"
        $c = Invoke-CompareChild $edition.Exe ($childArguments + @('-Path', $live, '-Map', $mapRefused))
        Assert-Case "TREE_COMPARE_CHILD_REFUSAL_EXIT_1_$($edition.Label)" ($c.Exit -eq 1 -and $c.Text.Contains('TREE_MAP_NAME_REFUSED: ../outside.dll') -and
            -not $c.Text.Contains('TREE_COMPARE_MATCH')) "exit $($c.Exit): $(($c.Text -replace '\s+', ' ').Trim())"
    }
}
catch {
    $failures.Add('EXC: ' + $_.Exception.Message)
    Write-Output ('FAIL: the self-test threw: ' + $_.Exception.Message)
}
finally {
    # Junctions go first, each removed as a link (RemoveDirectory on the reparse point), so the recursive removal below never
    # walks into a junction or around the loop.
    foreach ($junction in $junctions) {
        if ([IO.Directory]::Exists($junction) -and ([IO.File]::GetAttributes($junction) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            [IO.Directory]::Delete($junction)
        }
    }
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
