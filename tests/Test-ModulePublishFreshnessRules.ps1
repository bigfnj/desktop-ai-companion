#requires -Version 5
<#
.SYNOPSIS
    Synthetic-fixture controls for the 2026-11-10 rules in packaging\Test-ModulePublishFreshness.ps1 and
    packaging\EmbeddedPortablePdb.ps1.

.DESCRIPTION
    Three rules, each fed the input it exists to refuse beside a healthy baseline:
      G1  an agentbroker.zip at 1.10.4 or later (compared as a version, not as text), or one whose bytes are not
          the exact legacy blob, must carry exactly one well-formed source-content and parent-commit stamp, each
          inside a well-formed attribute blob, in AgentBroker.dll and in worker/AgentBroker.Worker.dll, equal
          across both, and no +<hex> version suffix; the bytes judged are HEAD's committed blob.
      G2  the STALE remedy sends agentbroker to the bound provenance route and keeps every other module's
          text byte for byte.
      G3  every module zip's embedded PDBs are judged for SourceLink and absolute document names, as counts
          only (an embedded PDB that reads as zero documents is refused, and build paths are summed, not
          counted by line); a zip whose name and exact SHA-256 match a legacy baseline is LEGACY_PRESERVED,
          which exempts the SourceLink refusal and nothing else; the success line counts a zip clean only when
          an embedded PDB in it was judged, and says when no legacy witness is left.

    tests\run-gate.ps1 and .github\workflows\build.yml run this file beside the freshness check. Once every
    LEGACY_PRESERVED zip is republished it is the SourceLink reader's only control.

    Every fixture is built from scratch in memory -- a minimal PE image, a minimal portable PDB, attribute
    blobs, ZIPs -- with synthetic names. No real ZIP, payload or build output is read, and no line printed
    here names a path. The functions under test come from the packaging scripts themselves: the library is
    dot-sourced and the freshness script's helpers are extracted by AST (the Test-ModulePublishCommit.ps1
    form), so a mutation in either file is exactly what a run judges. Both files' SHA-256 are printed first,
    as the witness that the run loaded the bytes under test. G1_COMMITTED_* create one throwaway git
    repository under TEMP and remove it.

    Windows PowerShell 5.1 and PowerShell 7. One PASS line per check and a SUMMARY. The first failure throws
    'FRESHNESS_RULES_<case>: <description>', with ' -- threw: <message>' when the code under test threw
    instead of answering, so every failure names its case. Fixture IO and cleanup failures report a fixed
    code and the exception's type only: the fixture sits under TEMP, inside the user profile on Windows, and
    an IO message names its path.
#>
[CmdletBinding()]
param([string]$RepoRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Windows PowerShell 5.1 has not populated PSScriptRoot during parameter defaults.
if (-not $PSBoundParameters.ContainsKey('RepoRoot')) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$freshnessPath = Join-Path $RepoRoot 'packaging\Test-ModulePublishFreshness.ps1'
$libraryPath = Join-Path $RepoRoot 'packaging\EmbeddedPortablePdb.ps1'
# Fixed messages: a missing file or a parse error raised by PowerShell itself would name the full path.
foreach ($sourcePath in @($freshnessPath, $libraryPath)) {
    if (-not [IO.File]::Exists($sourcePath)) {
        throw ('FRESHNESS_RULES_SOURCE: packaging\' + [IO.Path]::GetFileName($sourcePath) + ' is missing under -RepoRoot.')
    }
}
$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($libraryPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'FRESHNESS_RULES_SOURCE: packaging\EmbeddedPortablePdb.ps1 does not parse.' }
. $libraryPath
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($freshnessPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'FRESHNESS_RULES_SOURCE: packaging\Test-ModulePublishFreshness.ps1 does not parse.' }
$extracted = @('Read-ZipEntryBytes', 'Get-Sha256Hex', 'Invoke-GitBytes', 'Read-CommittedBlobBytes',
               'Find-AgentBrokerStampValues', 'Get-GitSuffixVersionBlobCount', 'Get-AgentBrokerStampFacts',
               'Test-AgentBrokerPayloadStamps', 'Test-AgentBrokerStampRequired', 'Get-AgentBrokerBoundRouteText',
               'Get-StalePayloadRemedy', 'Get-LegacyModuleZipBaselines', 'Test-LegacyModuleZip',
               'Test-AgentBrokerPayloadExempt', 'Get-ModuleZipPrivacyVerdict', 'Get-SourceLinkTallyLine')
foreach ($functionName in $extracted) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true))
    if ($definitions.Count -ne 1) {
        throw "FRESHNESS_RULES_SOURCE: expected exactly one function $functionName in the freshness script, found $($definitions.Count)."
    }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
Write-Host ('LOADED packaging/Test-ModulePublishFreshness.ps1 sha256 ' + (Get-Sha256Hex ([IO.File]::ReadAllBytes($freshnessPath))))
Write-Host ('LOADED packaging/EmbeddedPortablePdb.ps1 sha256 ' + (Get-Sha256Hex ([IO.File]::ReadAllBytes($libraryPath))))

$script:checks = 0
# One check: $Check's LAST output must be $true. Its own variables stay inside it.
function Invoke-Case([string]$Case, [string]$Description, [scriptblock]$Check) {
    $ok = $false
    try { $ok = [bool](@(& $Check) | Select-Object -Last 1) }
    catch { throw ('FRESHNESS_RULES_' + $Case + ': ' + $Description + ' -- threw: ' + $_.Exception.Message) }
    if (-not $ok) { throw ('FRESHNESS_RULES_' + $Case + ': ' + $Description) }
    $script:checks++
    Write-Host ('PASS ' + $Case + ' -- ' + $Description)
}
function Get-ThrownMessage([scriptblock]$Action) {
    try { $null = & $Action }
    catch { return $_.Exception.Message }
    return $null
}
# A failed IO call names the path it failed on, and the fixture repository sits under TEMP, which on Windows is
# inside the user profile. Fixture IO therefore reports a fixed code, the step and the innermost exception's
# TYPE, never its message.
function Get-InnermostExceptionTypeName($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    return $exception.GetType().Name
}
function Invoke-FixtureIo([string]$Step, [scriptblock]$Action) {
    try { $null = & $Action }
    catch { throw ('FRESHNESS_RULES_FIXTURE_IO: ' + $Step + ' failed (' + (Get-InnermostExceptionTypeName $_) + ').') }
}

# A Windows sharing violation (HRESULT 0x80070020) was observed during synthetic Git cleanup.
# Retry only that error, at most five times, after rechecking this exact owned root and every link check.
# Authorization and other errors refuse immediately; final absence remains required. Never print a path.
function Remove-FixtureRepository([string]$Path, [string]$Parent) {
    $firstHResult = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $resolved = [IO.Path]::GetFullPath($Path)
        if ([IO.Path]::GetDirectoryName($resolved) -cne $Parent -or
            [IO.Path]::GetFileName($resolved) -cnotmatch '^dac-freshness-rules-[0-9a-f]{32}\z') {
            throw 'FRESHNESS_RULES_CLEANUP: synthetic repository scope mismatch.'
        }
        $fixturePresent = $false
        $fixtureLinks = 0
        try {
            $fixturePresent = Test-Path -LiteralPath $resolved
            if ($fixturePresent) {
                foreach ($root in @($Parent, $resolved)) {
                    if (((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $fixtureLinks++ }
                }
                $fixtureLinks += @(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count
            }
        }
        catch { throw ('FRESHNESS_RULES_CLEANUP: inspecting the synthetic repository failed (' + (Get-InnermostExceptionTypeName $_) + ').') }
        if ($fixtureLinks -ne 0) { throw 'FRESHNESS_RULES_CLEANUP: refused to remove a tree holding a link.' }
        if (-not $fixturePresent) { return }
        try { Remove-Item -LiteralPath $resolved -Recurse -Force }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
            if ($exception -isnot [IO.IOException] -or $exception.HResult -ne -2147024864) {
                throw ('FRESHNESS_RULES_CLEANUP: remove failed (' + $exception.GetType().Name + '; HRESULT ' + $exception.HResult + ').')
            }
            if ($null -eq $firstHResult) { $firstHResult = $exception.HResult }
            if ($attempt -eq 6) {
                throw ('FRESHNESS_RULES_CLEANUP: remove failed (IOException; first HRESULT ' + $firstHResult +
                       '; last HRESULT ' + $exception.HResult + '; attempts 6).')
            }
            Start-Sleep -Milliseconds 500
            continue
        }
        if (Test-Path -LiteralPath $resolved) { throw 'FRESHNESS_RULES_CLEANUP: the synthetic repository remains.' }
        return
    }
}

# ---- byte builders ----
$latin1 = [Text.Encoding]::GetEncoding(28591)
$utf8 = New-Object Text.UTF8Encoding($false)

function Join-Bytes {
    $all = New-Object 'Collections.Generic.List[byte]'
    foreach ($part in $args) { if ($null -ne $part) { $all.AddRange([byte[]]$part) } }
    return ,$all.ToArray()
}

# ECMA-335 compressed unsigned integer: blob lengths, SerString lengths, document-name part indices.
function ConvertTo-CompressedUInt([int]$Value) {
    if ($Value -lt 0x80) { return ,([byte[]]@($Value)) }
    if ($Value -lt 0x4000) { return ,([byte[]]@((0x80 -bor ($Value -shr 8)), ($Value -band 0xFF))) }
    throw 'fixture value too large for this builder'
}

function Write-UInt16([byte[]]$Buffer, [int]$At, [int]$Value) {
    [Array]::Copy([BitConverter]::GetBytes([uint16]$Value), 0, $Buffer, $At, 2)
}
function Write-UInt32([byte[]]$Buffer, [int]$At, [long]$Value) {
    [Array]::Copy([BitConverter]::GetBytes([uint32]$Value), 0, $Buffer, $At, 4)
}

function Complete-Alignment([byte[]]$Bytes) {
    $aligned = New-Object byte[] ([int]([Math]::Ceiling($Bytes.Length / 4.0) * 4))
    [Array]::Copy($Bytes, $aligned, $Bytes.Length)
    return ,$aligned
}

# A custom-attribute value as the compiler stores it in #Blob: compressed blob length, prolog 01 00, one
# SerString per fixed argument, NumNamed 00 00.
function New-AttributeBlob([string[]]$Arguments) {
    $body = New-Object 'Collections.Generic.List[byte]'
    $body.AddRange([byte[]]@(1, 0))
    foreach ($argument in $Arguments) {
        $text = $utf8.GetBytes($argument)
        $body.AddRange((ConvertTo-CompressedUInt $text.Length))
        $body.AddRange($text)
    }
    $body.AddRange([byte[]]@(0, 0))
    return ,(Join-Bytes (ConvertTo-CompressedUInt $body.Count) ($body.ToArray()))
}

function Add-HeapBlob($Heap, [byte[]]$Bytes) {
    $index = $Heap.Count
    $Heap.AddRange((ConvertTo-CompressedUInt $Bytes.Length))
    $Heap.AddRange($Bytes)
    return $index
}

# A minimal portable PDB: metadata root at 0; streams #~, #Blob and (unless -OmitGuidHeap) #GUID; a Document
# table with 2-byte heap indices; and, for -SourceLink, one CustomDebugInformation row of the SourceLink kind
# whose value blob is '{}'. GUID slots: 1 SHA-256 (HashAlgorithm), 2 the -Language GUID, 3 SourceLink when
# asked, so a reader that resolves an index one slot off lands on the wrong GUID.
function New-SyntheticPortablePdb {
    param(
        [string[]]$DocumentNames,
        [switch]$SourceLink,
        [Guid]$Language = [Guid]'3F5162F8-07C6-11D3-9053-00C04FA302A1',
        [switch]$OmitGuidHeap
    )
    $blob = New-Object 'Collections.Generic.List[byte]'
    $blob.Add(0)
    $rows = New-Object 'Collections.Generic.List[byte]'
    foreach ($documentName in $DocumentNames) {
        $nameBlob = New-Object 'Collections.Generic.List[byte]'
        $separator = ''
        if ($documentName.Contains('\')) { $separator = '\' }
        elseif ($documentName.Contains('/')) { $separator = '/' }
        if ($separator.Length -gt 0) {
            $nameBlob.AddRange($utf8.GetBytes($separator))
            $parts = $documentName.Split([char]$separator)
        }
        else {
            $nameBlob.Add(0)
            $parts = @($documentName)
        }
        foreach ($part in $parts) {
            if ($part.Length -eq 0) { $nameBlob.Add(0) }
            else { $nameBlob.AddRange((ConvertTo-CompressedUInt (Add-HeapBlob $blob ($utf8.GetBytes($part))))) }
        }
        $nameIndex = Add-HeapBlob $blob ($nameBlob.ToArray())
        # Name, HashAlgorithm (slot 1), Hash (empty blob), Language (slot 2).
        foreach ($column in @($nameIndex, 1, 0, 2)) { $rows.AddRange([BitConverter]::GetBytes([uint16]$column)) }
    }
    $sourceLinkValue = 0
    if ($SourceLink) { $sourceLinkValue = Add-HeapBlob $blob ($utf8.GetBytes('{}')) }
    $guids = New-Object 'Collections.Generic.List[byte]'
    $guids.AddRange(([Guid]'8829D00F-11B8-4213-878B-770E8597AC16').ToByteArray())
    $guids.AddRange($Language.ToByteArray())
    if ($SourceLink) { $guids.AddRange(([Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A').ToByteArray()) }

    $tables = New-Object 'Collections.Generic.List[byte]'
    # Reserved, MajorVersion 2, MinorVersion 0, HeapSizes 0 (2-byte indices), Reserved 1.
    $tables.AddRange([byte[]]@(0, 0, 0, 0, 2, 0, 0, 1))
    $validHigh = 0x10000                                        # Document, table 0x30
    if ($SourceLink) { $validHigh = $validHigh -bor 0x800000 }  # CustomDebugInformation, table 0x37
    $tables.AddRange([BitConverter]::GetBytes([uint32]0))
    $tables.AddRange([BitConverter]::GetBytes([uint32]$validHigh))
    $tables.AddRange((New-Object byte[] 8))                     # Sorted
    $tables.AddRange([BitConverter]::GetBytes([uint32]$DocumentNames.Count))
    if ($SourceLink) { $tables.AddRange([BitConverter]::GetBytes([uint32]1)) }
    $tables.AddRange($rows.ToArray())
    if ($SourceLink) {
        # Parent: HasCustomDebugInformation coded index of Module row 1 (tag 7, 5 tag bits); Kind: GUID slot 3.
        foreach ($column in @(((1 -shl 5) -bor 7), 3, $sourceLinkValue)) { $tables.AddRange([BitConverter]::GetBytes([uint16]$column)) }
    }

    $streams = New-Object 'Collections.Generic.List[object]'
    $streams.Add([pscustomobject]@{ Name = '#~'; Bytes = (Complete-Alignment ($tables.ToArray())) })
    $streams.Add([pscustomobject]@{ Name = '#Blob'; Bytes = (Complete-Alignment ($blob.ToArray())) })
    if (-not $OmitGuidHeap) { $streams.Add([pscustomobject]@{ Name = '#GUID'; Bytes = $guids.ToArray() }) }

    # Root: signature, major, minor, reserved, version length 12, 'PDB v1.0' padded, flags, stream count.
    $headerLength = 32
    foreach ($stream in $streams) { $headerLength += 8 + [int]([Math]::Ceiling(($stream.Name.Length + 1) / 4.0) * 4) }
    $pdb = New-Object 'Collections.Generic.List[byte]'
    $pdb.AddRange([BitConverter]::GetBytes([uint32]0x424A5342))
    $pdb.AddRange([BitConverter]::GetBytes([uint16]1))
    $pdb.AddRange([BitConverter]::GetBytes([uint16]1))
    $pdb.AddRange([BitConverter]::GetBytes([uint32]0))
    $pdb.AddRange([BitConverter]::GetBytes([uint32]12))
    $version = New-Object byte[] 12
    $versionText = $latin1.GetBytes('PDB v1.0')
    [Array]::Copy($versionText, $version, $versionText.Length)
    $pdb.AddRange($version)
    $pdb.AddRange([BitConverter]::GetBytes([uint16]0))
    $pdb.AddRange([BitConverter]::GetBytes([uint16]$streams.Count))
    $offset = $headerLength
    foreach ($stream in $streams) {
        $pdb.AddRange([BitConverter]::GetBytes([uint32]$offset))
        $pdb.AddRange([BitConverter]::GetBytes([uint32]$stream.Bytes.Length))
        $nameField = New-Object byte[] ([int]([Math]::Ceiling(($stream.Name.Length + 1) / 4.0) * 4))
        $nameText = $latin1.GetBytes($stream.Name)
        [Array]::Copy($nameText, $nameField, $nameText.Length)
        $pdb.AddRange($nameField)
        $offset += $stream.Bytes.Length
    }
    foreach ($stream in $streams) { $pdb.AddRange([byte[]]$stream.Bytes) }
    return ,$pdb.ToArray()
}

# A minimal PE32 image with one section holding, in order: the debug directory (one EmbeddedPortablePdb entry
# when -EmbeddedPdb is given), the 'MPDB' record with the deflated PDB, then -Payload verbatim.
function New-SyntheticPeImage {
    param([byte[]]$EmbeddedPdb, [byte[]]$Payload)
    $sectionFileOffset = 0x200
    $sectionRva = 0x2000
    $debugSize = 0
    $recordLength = 0
    $compressed = $null
    if ($null -ne $EmbeddedPdb) {
        $memory = New-Object IO.MemoryStream
        $deflate = New-Object IO.Compression.DeflateStream($memory, [IO.Compression.CompressionMode]::Compress, $true)
        $deflate.Write($EmbeddedPdb, 0, $EmbeddedPdb.Length)
        $deflate.Dispose()
        $compressed = $memory.ToArray()
        $memory.Dispose()
        $debugSize = 28
        $recordLength = 8 + $compressed.Length
    }
    $payloadLength = 0
    if ($null -ne $Payload) { $payloadLength = $Payload.Length }
    $sectionLength = $debugSize + $recordLength + $payloadLength + 16
    $image = New-Object byte[] ($sectionFileOffset + $sectionLength)
    $image[0] = 0x4D
    $image[1] = 0x5A
    Write-UInt32 $image 0x3C 0x80
    $image[0x80] = 0x50
    $image[0x81] = 0x45
    $coff = 0x84
    Write-UInt16 $image $coff 0x14C            # Machine
    Write-UInt16 $image ($coff + 2) 1          # NumberOfSections
    Write-UInt16 $image ($coff + 16) 224       # SizeOfOptionalHeader: PE32, 96 + 16 directories
    $optional = $coff + 20
    Write-UInt16 $image $optional 0x10B        # PE32
    Write-UInt32 $image ($optional + 92) 16    # NumberOfRvaAndSizes
    if ($debugSize -gt 0) {
        Write-UInt32 $image ($optional + 96 + 6 * 8) $sectionRva
        Write-UInt32 $image ($optional + 96 + 6 * 8 + 4) $debugSize
    }
    $section = $optional + 224
    Write-UInt32 $image ($section + 8) $sectionLength
    Write-UInt32 $image ($section + 12) $sectionRva
    Write-UInt32 $image ($section + 16) $sectionLength
    Write-UInt32 $image ($section + 20) $sectionFileOffset
    if ($debugSize -gt 0) {
        $record = $sectionFileOffset + $debugSize
        Write-UInt32 $image ($sectionFileOffset + 12) 17                  # IMAGE_DEBUG_TYPE_EMBEDDED_PORTABLE_PDB
        Write-UInt32 $image ($sectionFileOffset + 16) $recordLength
        Write-UInt32 $image ($sectionFileOffset + 20) ($sectionRva + $debugSize)
        Write-UInt32 $image ($sectionFileOffset + 24) $record
        Write-UInt32 $image $record 0x4244504D                            # 'MPDB'
        Write-UInt32 $image ($record + 4) $EmbeddedPdb.Length
        [Array]::Copy($compressed, 0, $image, $record + 8, $compressed.Length)
    }
    if ($payloadLength -gt 0) {
        [Array]::Copy($Payload, 0, $image, $sectionFileOffset + $debugSize + $recordLength, $payloadLength)
    }
    return ,$image
}

function New-SyntheticZip($Entries) {
    $memory = New-Object IO.MemoryStream
    $archive = New-Object IO.Compression.ZipArchive($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($entryName in @($Entries.Keys)) {
            $entry = $archive.CreateEntry($entryName)
            $stream = $entry.Open()
            try {
                $bytes = [byte[]]$Entries[$entryName]
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    $zip = $memory.ToArray()
    $memory.Dispose()
    return ,$zip
}

# An image whose section carries the attribute blobs a build would emit: AssemblyFileVersion- and
# InformationalVersion-shaped single strings, the provenance pairs asked for, an optional extra pair, and
# -RawBlob verbatim (a hand-damaged blob).
function New-StampedImage {
    param([string[]]$SourceValues = @(), [string[]]$ParentValues = @(), [string]$InformationalVersion = '1.10.4',
          [string[]]$ExtraPair = @(), [byte[]]$RawBlob)
    $payload = New-Object 'Collections.Generic.List[byte]'
    $filler = [byte[]]@(0x2A, 0x2A, 0x2A, 0x2A)
    $payload.AddRange((New-AttributeBlob @('1.10.4.0')))
    $payload.AddRange($filler)
    $payload.AddRange((New-AttributeBlob @($InformationalVersion)))
    $payload.AddRange($filler)
    foreach ($value in $SourceValues) {
        $payload.AddRange((New-AttributeBlob @('AgentBroker.SourceContentSha256', $value)))
        $payload.AddRange($filler)
    }
    foreach ($value in $ParentValues) {
        $payload.AddRange((New-AttributeBlob @('AgentBroker.SourceParentCommit', $value)))
        $payload.AddRange($filler)
    }
    if ($ExtraPair.Count -eq 2) {
        $payload.AddRange((New-AttributeBlob $ExtraPair))
        $payload.AddRange($filler)
    }
    if ($null -ne $RawBlob) {
        $payload.AddRange($RawBlob)
        $payload.AddRange($filler)
    }
    return ,(New-SyntheticPeImage -Payload ($payload.ToArray()))
}

function New-AgentBrokerZip([byte[]]$Module, [byte[]]$Worker, [string]$WorkerEntry = 'worker/AgentBroker.Worker.dll') {
    $entries = [ordered]@{}
    $entries['AgentBroker.dll'] = $Module
    $entries['DesktopAICompanion.ModuleKit.dll'] = (New-SyntheticPeImage)
    $entries[$WorkerEntry] = $Worker
    return ,(New-SyntheticZip $entries)
}

# 'CODE subject' for every problem, in order, joined by '|'.
function Get-ProblemCodes($Verdict) {
    $codes = New-Object 'Collections.Generic.List[string]'
    foreach ($problem in $Verdict.Problems) {
        $words = $problem.Split(' ')
        $codes.Add($words[0] + ' ' + $words[1])
    }
    return ($codes.ToArray() -join '|')
}

# ===================================================================================================
# G1: AgentBroker provenance stamps
# ===================================================================================================
$goodDigest = '0123456789ABCDEF' * 4
$goodParent = '0123456789abcdef0123' * 2
$otherDigest = 'FEDCBA9876543210' * 4
$otherParent = 'fedcba9876543210fedc' * 2
$gitSuffixVersion = '1.10.4+' + ('89abcdef01' * 4)
$healthyModule = New-StampedImage -SourceValues $goodDigest -ParentValues $goodParent
$healthyWorker = New-StampedImage -SourceValues $goodDigest -ParentValues $goodParent
$healthyZip = New-AgentBrokerZip $healthyModule $healthyWorker
$normalModule = New-StampedImage -InformationalVersion $gitSuffixVersion
$normalWorker = New-StampedImage -InformationalVersion $gitSuffixVersion

Invoke-Case 'G1_HEALTHY' 'a bound payload (one well-formed stamp of each key in both DLLs, equal, no suffix) passes and reports its stamp values' {
    $verdict = Test-AgentBrokerPayloadStamps -ZipBytes $healthyZip
    $verdict.Problems.Count -eq 0 -and $verdict.SourceContentSha256 -ceq $goodDigest -and $verdict.SourceParentCommit -ceq $goodParent
}
Invoke-Case 'G1_ORDINARY_BUILD' 'an ordinary build (no stamps, +<hex> suffix) is refused on all three counts in both DLLs' {
    $verdict = Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $normalModule $normalWorker)
    (Get-ProblemCodes $verdict) -ceq ('AGENTBROKER_STAMP_SOURCE AgentBroker.dll|AGENTBROKER_STAMP_PARENT AgentBroker.dll|' +
        'AGENTBROKER_STAMP_GIT_SUFFIX AgentBroker.dll|AGENTBROKER_STAMP_SOURCE worker/AgentBroker.Worker.dll|' +
        'AGENTBROKER_STAMP_PARENT worker/AgentBroker.Worker.dll|AGENTBROKER_STAMP_GIT_SUFFIX worker/AgentBroker.Worker.dll')
}
Invoke-Case 'G1_WORKER_SUFFIX' 'a +<hex> InformationalVersion in the Worker alone is exactly one refusal naming the Worker' {
    $worker = New-StampedImage -SourceValues $goodDigest -ParentValues $goodParent -InformationalVersion $gitSuffixVersion
    (Get-ProblemCodes (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $healthyModule $worker))) -ceq 'AGENTBROKER_STAMP_GIT_SUFFIX worker/AgentBroker.Worker.dll'
}
Invoke-Case 'G1_DIGEST_MISMATCH' 'a module/Worker source-digest disagreement is exactly one mismatch refusal' {
    $worker = New-StampedImage -SourceValues $otherDigest -ParentValues $goodParent
    (Get-ProblemCodes (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $healthyModule $worker))) -ceq 'AGENTBROKER_STAMP_MISMATCH AgentBroker.SourceContentSha256'
}
Invoke-Case 'G1_PARENT_MISMATCH' 'a module/Worker parent-commit disagreement is exactly one mismatch refusal' {
    $worker = New-StampedImage -SourceValues $goodDigest -ParentValues $otherParent
    (Get-ProblemCodes (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $healthyModule $worker))) -ceq 'AGENTBROKER_STAMP_MISMATCH AgentBroker.SourceParentCommit'
}
Invoke-Case 'G1_DIGEST_CASE' 'a lowercase source digest is malformed (the format is uppercase, compared case-sensitively)' {
    $module = New-StampedImage -SourceValues $goodDigest.ToLowerInvariant() -ParentValues $goodParent
    $verdict = Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $module $healthyWorker)
    (Get-ProblemCodes $verdict) -ceq 'AGENTBROKER_STAMP_SOURCE AgentBroker.dll' -and $verdict.Problems[0].Contains('found 1 time(s), 0 with a well-formed')
}
Invoke-Case 'G1_PARENT_CASE' 'an uppercase parent commit is malformed (the format is lowercase)' {
    $module = New-StampedImage -SourceValues $goodDigest -ParentValues $goodParent.ToUpperInvariant()
    (Get-ProblemCodes (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $module $healthyWorker))) -ceq 'AGENTBROKER_STAMP_PARENT AgentBroker.dll'
}
Invoke-Case 'G1_TWO_DIGESTS' 'two different source digests in one DLL are refused, not resolved by picking one' {
    $module = New-StampedImage -SourceValues @($goodDigest, $otherDigest) -ParentValues $goodParent
    $verdict = Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $module $healthyWorker)
    (Get-ProblemCodes $verdict) -ceq 'AGENTBROKER_STAMP_SOURCE AgentBroker.dll' -and $verdict.Problems[0].Contains('found 2 time(s), 2 with')
}
Invoke-Case 'G1_STAMP_SHAPE' 'the key and a well-formed 64-hex value are no stamp when any byte of the attribute blob around them is wrong (blob length, prolog, key length, value length, NumNamed)' {
    $control = New-AttributeBlob @('AgentBroker.SourceContentSha256', $goodDigest)
    $controlVerdict = Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip (New-StampedImage -ParentValues $goodParent -RawBlob $control) $healthyWorker)
    $refused = 0
    # Offsets in the 102-byte blob: 0 blob length, 1-2 prolog 01 00, 3 key length, 35 value length, 100-101 NumNamed.
    foreach ($position in @(0, 1, 2, 3, 35, 100, 101)) {
        $damaged = New-AttributeBlob @('AgentBroker.SourceContentSha256', $goodDigest)
        $damaged[$position] = [byte]($damaged[$position] -bxor 1)
        $verdict = Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip (New-StampedImage -ParentValues $goodParent -RawBlob $damaged) $healthyWorker)
        if ((Get-ProblemCodes $verdict) -ceq 'AGENTBROKER_STAMP_SOURCE AgentBroker.dll' -and
            $verdict.Problems[0].Contains('found 1 time(s), 0 with a well-formed')) { $refused++ }
    }
    $control.Length -eq 102 -and $controlVerdict.Problems.Count -eq 0 -and $refused -eq 7
}
Invoke-Case 'G1_WORKER_ENTRY' 'a Worker outside worker/ is a missing-entry refusal, not a pass on the root copy' {
    (Get-ProblemCodes (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $healthyModule $healthyWorker 'AgentBroker.Worker.dll'))) -ceq 'AGENTBROKER_STAMP_ENTRY worker/AgentBroker.Worker.dll'
}
Invoke-Case 'G1_LONG_SUFFIX' 'a +<hex> suffix on a version string of 128+ bytes (two-byte length prefix) is still found' {
    $module = New-StampedImage -SourceValues $goodDigest -ParentValues $goodParent -InformationalVersion ('1.10.4-' + ('x' * 150) + '+0123abcd')
    (Get-ProblemCodes (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $module $healthyWorker))) -ceq 'AGENTBROKER_STAMP_GIT_SUFFIX AgentBroker.dll'
}
Invoke-Case 'G1_PAIR_NOT_VERSION' 'a two-string AssemblyMetadata value ending in +<hex> is not mistaken for a version suffix' {
    $module = New-StampedImage -SourceValues $goodDigest -ParentValues $goodParent -ExtraPair @('RepositoryCommit', '1.0+0123abcd')
    (Test-AgentBrokerPayloadStamps -ZipBytes (New-AgentBrokerZip $module $healthyWorker)).Problems.Count -eq 0
}
Invoke-Case 'G1_GATE_HISTORY' 'version 1.10.3 is published history and exempt' {
    (Test-AgentBrokerStampRequired '1.10.3') -eq $false
}
Invoke-Case 'G1_GATE_FUTURE' 'versions 1.10.4, 1.11.0 and 2.0 require the stamps' {
    (Test-AgentBrokerStampRequired '1.10.4') -eq $true -and (Test-AgentBrokerStampRequired '1.11.0') -eq $true -and (Test-AgentBrokerStampRequired '2.0') -eq $true
}
Invoke-Case 'G1_GATE_NUMERIC' 'the version gate compares numbers, not text: 1.10.10 and 1.10.4.0 require the stamps, 1.9.9 does not' {
    (Test-AgentBrokerStampRequired '1.10.10') -eq $true -and (Test-AgentBrokerStampRequired '1.10.4.0') -eq $true -and
        (Test-AgentBrokerStampRequired '1.9.9') -eq $false
}
Invoke-Case 'G1_GATE_UNPARSED' 'an unparseable or empty version is refused, never exempted' {
    $beta = Get-ThrownMessage { Test-AgentBrokerStampRequired '1.10.4-beta' }
    $empty = Get-ThrownMessage { Test-AgentBrokerStampRequired '' }
    $null -ne $beta -and $beta -like 'AGENTBROKER_STAMP_VERSION:*' -and $null -ne $empty -and $empty -like 'AGENTBROKER_STAMP_VERSION:*'
}
Invoke-Case 'G1_EXEMPT_HISTORY_ONLY' 'only the exact legacy agentbroker.zip below 1.10.4 is exempt: new bytes at 1.10.3, another legacy hash, and the legacy bytes declared 1.10.4 are not' {
    $baselines = Get-LegacyModuleZipBaselines
    $legacySha = [string]$baselines['modules-dist/agentbroker.zip']
    (Test-AgentBrokerPayloadExempt '1.10.3' $legacySha $baselines) -eq $true -and
        (Test-AgentBrokerPayloadExempt '1.10.3' (Get-Sha256Hex $healthyZip) $baselines) -eq $false -and
        (Test-AgentBrokerPayloadExempt '1.10.3' ([string]$baselines['modules-dist/fortunes.zip']) $baselines) -eq $false -and
        (Test-AgentBrokerPayloadExempt '1.10.4' $legacySha $baselines) -eq $false
}

# The committed blob, not the working tree: a throwaway repository whose HEAD holds the healthy zip while the
# working tree holds the ordinary build.
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixtureRepo = Join-Path $tempRoot ('dac-freshness-rules-' + [guid]::NewGuid().ToString('N'))
function Invoke-FixtureGit([string[]]$GitArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $null = @(& git -C $fixtureRepo @GitArgs 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('FRESHNESS_RULES_FIXTURE_GIT: git ' + $GitArgs[0] + ' failed (exit ' + $LASTEXITCODE + ').') }
    }
    finally { $ErrorActionPreference = $previous }
}
try {
    Invoke-FixtureIo 'create the synthetic repository' { [void][IO.Directory]::CreateDirectory((Join-Path $fixtureRepo 'modules-dist')) }
    Invoke-FixtureGit @('init', '-q')
    Invoke-FixtureGit @('config', 'user.name', 'bigfnj')
    Invoke-FixtureGit @('config', 'user.email', 'bigfnj@users.noreply.github.com')
    Invoke-FixtureIo 'write .gitattributes' { [IO.File]::WriteAllText((Join-Path $fixtureRepo '.gitattributes'), "*.zip -text`n", $utf8) }
    $workingZip = New-AgentBrokerZip $normalModule $normalWorker
    Invoke-FixtureIo 'write the committed zip' { [IO.File]::WriteAllBytes((Join-Path $fixtureRepo 'modules-dist\agentbroker.zip'), $healthyZip) }
    Invoke-FixtureGit @('add', '--', '.gitattributes', 'modules-dist/agentbroker.zip')
    Invoke-FixtureGit @('commit', '-q', '-m', 'synthetic payload')
    Invoke-FixtureIo 'write the working-tree zip' { [IO.File]::WriteAllBytes((Join-Path $fixtureRepo 'modules-dist\agentbroker.zip'), $workingZip) }
    Invoke-Case 'G1_COMMITTED_BLOB' 'the stamp check judges HEAD''s committed blob byte for byte, not the differing working-tree file' {
        $read = Read-CommittedBlobBytes -Repository $fixtureRepo -Spec 'HEAD:modules-dist/agentbroker.zip'
        $readSha = Get-Sha256Hex $read
        $readSha -ceq (Get-Sha256Hex $healthyZip) -and $readSha -cne (Get-Sha256Hex $workingZip) -and (Test-AgentBrokerPayloadStamps -ZipBytes $read).Problems.Count -eq 0
    }
    Invoke-Case 'G1_COMMITTED_MISSING' 'a path absent from HEAD is refused by name, not read as empty' {
        $message = Get-ThrownMessage { Read-CommittedBlobBytes -Repository $fixtureRepo -Spec 'HEAD:modules-dist/absent.zip' }
        $null -ne $message -and $message -like 'COMMITTED_BLOB_MISSING:*'
    }
    Invoke-Case 'G1_COMMITTED_SPEC' 'a backslash spec is refused before git sees it' {
        $message = Get-ThrownMessage { Read-CommittedBlobBytes -Repository $fixtureRepo -Spec 'HEAD:modules-dist\agentbroker.zip' }
        $null -ne $message -and $message -like 'COMMITTED_BLOB_SPEC:*'
    }
    Invoke-Case 'G1_COMMITTED_SPEC_END' 'a spec with a trailing newline is refused by the exact end anchor before git sees it' {
        $message = Get-ThrownMessage { Read-CommittedBlobBytes -Repository $fixtureRepo -Spec ("HEAD:modules-dist/agentbroker.zip" + [char]10) }
        $null -ne $message -and $message -like 'COMMITTED_BLOB_SPEC:*'
    }
}
finally {
    Remove-FixtureRepository -Path $fixtureRepo -Parent $tempRoot
}

# ===================================================================================================
# G2: the STALE remedy
# ===================================================================================================
# The 50d666ad text, copied as the oracle.
function Get-BaseRemedy([string]$Ids) {
    return ("Published module payload(s) are behind their source: " +
            $Ids +
            ". Rebuild (build.ps1 -Release), re-zip (packaging\New-ModuleDistZip.ps1), COMMIT the zip, " +
            "then regenerate the catalog (packaging\New-ContentCatalog.ps1) -- in that order, because " +
            "the catalog hashes the committed blob.")
}
Invoke-Case 'G2_OTHERS_UNCHANGED' 'every other module keeps the 50d666ad STALE remedy byte for byte' {
    (Get-StalePayloadRemedy -StaleIds @('fortunes', 'aibrain')) -ceq (Get-BaseRemedy 'fortunes, aibrain')
}
Invoke-Case 'G2_AGENTBROKER_BOUND' 'the agentbroker remedy names every step of the bound route and never the plain rebuild' {
    $bound = Get-StalePayloadRemedy -StaleIds @('agentbroker')
    $routeTokens = @('docs\agentbroker-release-provenance.md', 'AgentBrokerReleaseProvenance.ps1 -Mode Prepare', '-Mode Build',
                     '-ManifestPath', '-ExpectedSha256', '-ExpectedParent', '-Mode VerifyOutput', '-OutputReceipt',
                     'New-ModuleDistZip.ps1 -ModuleId agentbroker', '-AgentBrokerOutputReceipt', '-AgentBrokerOutputReceiptSha256',
                     'installed acceptance', 'New-ModulePublish.ps1', '-SkipBuild', '-ExpectedAgentBrokerZipSha256', '-Commit')
    $missingTokens = @($routeTokens | Where-Object { -not $bound.Contains($_) })
    $missingTokens.Count -eq 0 -and -not $bound.Contains('build.ps1 -Release')
}
Invoke-Case 'G2_MIXED' 'with both stale, the others get the old text without agentbroker and agentbroker gets the bound route' {
    $mixed = Get-StalePayloadRemedy -StaleIds @('fortunes', 'agentbroker')
    $mixed.StartsWith((Get-BaseRemedy 'fortunes'), [StringComparison]::Ordinal) -and $mixed.Contains((Get-AgentBrokerBoundRouteText))
}
Invoke-Case 'G2_CASING' 'an -ModuleId spelled AgentBroker still gets the bound route' {
    $cased = Get-StalePayloadRemedy -StaleIds @('AgentBroker')
    $cased.Contains('-SkipBuild') -and -not $cased.Contains('build.ps1 -Release')
}

# ===================================================================================================
# G3: embedded-PDB privacy and LEGACY_PRESERVED
# ===================================================================================================
$mappedNames = @('/_/src/Kit/A.cs', '/_/src/Kit/B.cs')
$absoluteNames = @('C:\fixtureabs\A.cs', 'C:/fixtureabs/B.cs', '\\fixturehost\share\C.cs', '//fixturehost/share/D.cs', 'C:fixturerel.cs', '/_/src/Kit/F.cs')
$unknownLanguage = [Guid]'00000000-0000-0000-0000-0000000000AB'

Invoke-Case 'G3_CLEAN_PDB' 'a mapped embedded PDB without SourceLink reads as 1 PDB, 2 documents, 0 absolute, 0 SourceLink' {
    $summary = Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames))
    $summary.EmbeddedPdbCount -eq 1 -and $summary.DocumentCount -eq 2 -and $summary.AbsoluteDocumentCount -eq 0 -and $summary.SourceLinkPdbCount -eq 0
}
Invoke-Case 'G3_SOURCELINK_PDB' 'a SourceLink CustomDebugInformation row is detected by its kind GUID' {
    $summary = Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames -SourceLink))
    $summary.SourceLinkPdbCount -eq 1 -and $summary.AbsoluteDocumentCount -eq 0
}
Invoke-Case 'G3_ABSOLUTE_COUNT' 'drive-rooted (both slashes), UNC (both slashes) and drive-relative names count as absolute; /_/ does not' {
    $summary = Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $absoluteNames))
    $summary.DocumentCount -eq 6 -and $summary.AbsoluteDocumentCount -eq 5
}
Invoke-Case 'G3_READER_PARITY' 'the refactored layout still returns every document name exactly, in order (compared, never printed)' {
    $info = Get-EmbeddedPortablePdbDocumentNames -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $absoluteNames))
    $info.EmbeddedPdbCount -eq 1 -and (@($info.DocumentNames) -join '|') -ceq ($absoluteNames -join '|')
}
Invoke-Case 'G3_NO_PDB' 'an image without an embedded PDB is a fact (0 PDBs), not a failure' {
    $summary = Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage)
    $summary.EmbeddedPdbCount -eq 0 -and $summary.DocumentCount -eq 0 -and $summary.SourceLinkPdbCount -eq 0
}
Invoke-Case 'G3_LANGUAGE_WITNESS' 'a #GUID heap whose Document languages resolve to no known language GUID is refused as a misread' {
    $message = Get-ThrownMessage { Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames -Language $unknownLanguage)) }
    $null -ne $message -and $message -like 'WITNESS failed: no Document row*'
}
Invoke-Case 'G3_GUID_HEAP_REQUIRED' 'an embedded PDB without a #GUID heap is refused rather than read as SourceLink-free' {
    $message = Get-ThrownMessage { Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames -OmitGuidHeap)) }
    $null -ne $message -and $message -like '*no #GUID stream*'
}
Invoke-Case 'G3_ZERO_DOCUMENTS' 'an embedded PDB whose Document table reads as zero rows is refused, not scored as free of build paths' {
    $message = Get-ThrownMessage { Get-EmbeddedPortablePdbPrivacySummary -Image (New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames @())) }
    $null -ne $message -and $message -like '*parsed to ZERO documents*'
}
Invoke-Case 'G3_ABSOLUTE_PATTERN' 'the absolute-name rule holds on every table case' {
    $cases = [ordered]@{
        'D:\x.cs' = $true; 'D:/x.cs' = $true; 'D:x.cs' = $true; '\\h\x.cs' = $true; '//h/x.cs' = $true; '\/h/x.cs' = $true
        '/_/x.cs' = $false; 'x/y.cs' = $false; '' = $false; '/x.cs' = $false
    }
    $wrong = @($cases.Keys | Where-Object { (Test-AbsoluteDocumentName $_) -ne $cases[$_] })
    $cases.Count -eq 10 -and $wrong.Count -eq 0
}
Invoke-Case 'G3_LEGACY_LITERAL' 'the legacy list is exactly the eight 50d666ad zip hashes' {
    $expected = @{
        'modules-dist/agentbroker.zip' = '5e2d0f5079c5b7883fb6fab9b028b7704e7e5c8cbbd8be6aeae4796446b16b1c'
        'modules-dist/agentflow.zip'   = '018a034f48ce95b78396ad2ddf5af0307dc17679c23b46a3917fcaa02125e861'
        'modules-dist/aibrain.zip'     = 'cc8409bba239b5bad5e83763225c603f65979ebe1bbb90eedb7efc74716ee968'
        'modules-dist/blinkingled.zip' = '92d2b5adf35a080500b2f5397699005bacd1a20d5a9e1c380e53a8262e56b3c1'
        'modules-dist/fortunes.zip'    = 'df99f7f06f79d115134be8ef10103739bfa41f34c6846d091f665e0ac7ff7fcb'
        'modules-dist/petstudio.zip'   = '2e9c9f8f2cd875f359c80f83ef6c82994ab957cd753eee01c2fd67f8edfadd86'
        'modules-dist/remembrance.zip' = 'adc1dcd2cb02f4469ea9a6f4e13ca3d8baea30ff4a294f6c76203f8d1991b3c2'
        'modules-dist/reminder.zip'    = 'b9740a606f06a4847d14d022f036d39b18fec87bc5a9f18332eade523e0a75f6'
    }
    $actual = Get-LegacyModuleZipBaselines
    $differs = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) -or ([string]$actual[$_]) -cne $expected[$_] })
    $actual.Count -eq 8 -and $differs.Count -eq 0
}

# Per-zip verdicts on synthetic zips. ModuleKit carries the embedded PDB; Fixture.dll stands for a module DLL.
$ourAssemblies = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
[void]$ourAssemblies.Add('DesktopAICompanion.ModuleKit.dll')
[void]$ourAssemblies.Add('Fixture.dll')
$codeViewPattern = [regex] '[A-Za-z]:\\[^\x00<>|]{0,200}?\.pdb'
$unrelated = @{ 'modules-dist/unrelated.zip' = ('0' * 64) }
function New-ModuleZip([byte[]]$Kit, [byte[]]$Module) {
    $entries = [ordered]@{}
    $entries['DesktopAICompanion.ModuleKit.dll'] = $Kit
    $entries['Fixture.dll'] = $Module
    return ,(New-SyntheticZip $entries)
}
function Get-Verdict([string]$Name, [byte[]]$Zip, $Baselines) {
    return (Get-ModuleZipPrivacyVerdict -Name $Name -ZipBytes $Zip -OurAssemblies $ourAssemblies -Baselines $Baselines -CodeViewPattern $codeViewPattern)
}
function Get-Baseline([string]$Name, [byte[]]$Zip) { return @{ ('modules-dist/' + $Name) = (Get-Sha256Hex $Zip) } }
$plainModule = New-SyntheticPeImage
$kitSourceLink = New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames -SourceLink)
$kitClean = New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames)
$kitAbsolute = New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames @('C:\fixtureabs\Hidden.cs', '/_/src/Kit/B.cs'))
$kitBlind = New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames $mappedNames -Language $unknownLanguage)
$codeViewModule = New-SyntheticPeImage -Payload (Join-Bytes ($latin1.GetBytes('C:\fixturecv\obj\Fixture.pdb')) ([byte[]]@(0)))
$sourceLinkZip = New-ModuleZip $kitSourceLink $plainModule
$cleanZip = New-ModuleZip $kitClean $plainModule
$absoluteZip = New-ModuleZip $kitAbsolute $plainModule

Invoke-Case 'G3_SOURCELINK_REFUSED' 'a zip outside the legacy set with a SourceLink record is one SourceLink offender' {
    $v = Get-Verdict 'fixture.zip' $sourceLinkZip $unrelated
    -not $v.Legacy -and $v.SourceLinkOffenders.Count -eq 1 -and $v.BuildPathOffenders.Count -eq 0 -and $v.SourceLinkPdbCount -eq 1 -and $v.DllCount -eq 2 -and -not $v.LegacyWitnessFailed
}
Invoke-Case 'G3_LEGACY_PRESERVED' 'the same bytes under a matching name and hash are LEGACY_PRESERVED, their SourceLink counted, not refused' {
    $v = Get-Verdict 'fixture.zip' $sourceLinkZip (Get-Baseline 'fixture.zip' $sourceLinkZip)
    $v.Legacy -and $v.SourceLinkOffenders.Count -eq 0 -and $v.SourceLinkPdbCount -eq 1 -and -not $v.LegacyWitnessFailed -and $v.Sha256 -ceq (Get-Sha256Hex $sourceLinkZip)
}
Invoke-Case 'G3_LEGACY_NAME' 'legacy bytes under another name are not history' {
    $v = Get-Verdict 'other.zip' $sourceLinkZip (Get-Baseline 'fixture.zip' $sourceLinkZip)
    -not $v.Legacy -and $v.SourceLinkOffenders.Count -eq 1
}
Invoke-Case 'G3_LEGACY_HASH_EXACT' 'the legacy hash is compared exactly (case-sensitive lowercase hex)' {
    $v = Get-Verdict 'fixture.zip' $sourceLinkZip @{ 'modules-dist/fixture.zip' = (Get-Sha256Hex $sourceLinkZip).ToUpperInvariant() }
    -not $v.Legacy -and $v.SourceLinkOffenders.Count -eq 1
}
Invoke-Case 'G3_LEGACY_WITNESS' 'a legacy-matched zip that reads as SourceLink-free trips the reader witness' {
    $v = Get-Verdict 'fixture.zip' $cleanZip (Get-Baseline 'fixture.zip' $cleanZip)
    $v.Legacy -and $v.LegacyWitnessFailed
}
Invoke-Case 'G3_CLEAN_ZIP' 'a mapped, SourceLink-free ModuleKit beside a symbol-free module DLL is clean' {
    $v = Get-Verdict 'fixture.zip' $cleanZip $unrelated
    -not $v.Legacy -and $v.BuildPathOffenders.Count -eq 0 -and $v.SourceLinkOffenders.Count -eq 0 -and -not $v.ModuleKitWithoutEmbeddedPdb -and $v.EmbeddedPdbCount -eq 1 -and $v.DocumentCount -eq 2
}
Invoke-Case 'G3_ABSOLUTE_COUNTED' 'an absolute document name is one offender that carries its count and never the name' {
    $v = Get-Verdict 'fixture.zip' $absoluteZip $unrelated
    $offender = ''
    if ($v.BuildPathOffenders.Count -eq 1) { $offender = [string]$v.BuildPathOffenders[0] }
    $v.BuildPathOffenders.Count -eq 1 -and $offender.Contains(': 1 absolute embedded PDB document name(s)') -and
        -not $offender.Contains('fixtureabs') -and -not $offender.Contains('Hidden') -and -not [regex]::IsMatch($offender, '[A-Za-z]:[\\/]')
}
Invoke-Case 'G3_LEGACY_SOURCELINK_ONLY' 'LEGACY_PRESERVED exempts the SourceLink refusal only: an absolute document name in legacy bytes is still an offender' {
    $v = Get-Verdict 'fixture.zip' $absoluteZip (Get-Baseline 'fixture.zip' $absoluteZip)
    $v.Legacy -and $v.BuildPathOffenders.Count -eq 1
}
Invoke-Case 'G3_CODEVIEW_COUNTED' 'a CodeView build path is one offender that carries its count and never the path' {
    $v = Get-Verdict 'fixture.zip' (New-ModuleZip $kitClean $codeViewModule) $unrelated
    $offender = ''
    if ($v.BuildPathOffenders.Count -eq 1) { $offender = [string]$v.BuildPathOffenders[0] }
    $v.BuildPathOffenders.Count -eq 1 -and $offender.Contains('Fixture.dll : 1 CodeView build path(s)') -and -not $offender.Contains('fixturecv')
}
Invoke-Case 'G3_BUILD_PATH_SUM' 'the build-path total sums every path, not every offender line: 2 absolute names and 1 CodeView path in two DLLs are 3' {
    $kitTwoAbsolute = New-SyntheticPeImage -EmbeddedPdb (New-SyntheticPortablePdb -DocumentNames @('C:\fixtureabs\One.cs', '\\fixturehost\share\Two.cs', '/_/src/Kit/B.cs'))
    $v = Get-Verdict 'fixture.zip' (New-ModuleZip $kitTwoAbsolute $codeViewModule) $unrelated
    $v.BuildPathOffenders.Count -eq 2 -and $v.BuildPathCount -eq 3
}
Invoke-Case 'G3_MODULEKIT_WITNESS' 'a ModuleKit without an embedded PDB still trips the existing WITNESS' {
    (Get-Verdict 'fixture.zip' (New-ModuleZip $plainModule $plainModule) $unrelated).ModuleKitWithoutEmbeddedPdb
}
Invoke-Case 'G3_READER_FAILURE_NAMED' 'a reader refusal names the zip and DLL it happened in, and nothing from inside them' {
    $message = Get-ThrownMessage { Get-Verdict 'fixture.zip' (New-ModuleZip $kitBlind $plainModule) $unrelated }
    $null -ne $message -and $message -like 'fixture.zip -> DesktopAICompanion.ModuleKit.dll : WITNESS failed*'
}
Invoke-Case 'G3_SOURCELINK_TALLY' 'the success line calls a zip clean only when an embedded PDB in it was judged, and says when no legacy witness is left' {
    $clean = Get-Verdict 'fixture.zip' $cleanZip $unrelated
    $legacy = Get-Verdict 'fixture.zip' $sourceLinkZip (Get-Baseline 'fixture.zip' $sourceLinkZip)
    $nothing = Get-Verdict 'fixture.zip' (New-ModuleZip $plainModule $plainModule) $unrelated
    $mixed = Get-SourceLinkTallyLine -Verdicts @($clean, $legacy, $nothing)
    $noLegacy = Get-SourceLinkTallyLine -Verdicts @($clean, $nothing)
    $mixed.StartsWith('SourceLink: 1 zip(s) clean (embedded PDB judged), 1 zip(s) with no embedded PDB to judge, 1 zip(s) LEGACY_PRESERVED', [StringComparison]::Ordinal) -and
        $mixed.Contains('witnessed live by the 1 LEGACY_PRESERVED') -and -not $mixed.Contains('no live witness') -and
        $noLegacy.Contains('0 zip(s) LEGACY_PRESERVED') -and
        $noLegacy.Contains('no live witness; synthetic control tests\Test-ModulePublishFreshnessRules.ps1')
}

# Execute the actual main-body refusal statements, not a second implementation of their decisions.
# The wiring assertion also rejects moving a guarded statement under an optional/false branch.
# This intentionally does not replay discovery, Git recency, Fortunes content or the complete main script.
function Get-FreshnessMainDecision([string]$Source, [string]$Kind) {
    $tokens = $null; $errors = $null
    $root = [Management.Automation.Language.Parser]::ParseInput($Source, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw 'FRESHNESS_RULES_MAIN_SOURCE_PARSE' }
    $conditions = @{
        'PAYLOAD' = '$stampVerdict.Problems.Count-gt0'
        'SOURCELINK' = '$sourceLinkOffenders.Count-gt0'
        'LEGACY' = '$legacyWithoutSourceLink.Count-gt0'
    }
    $wanted = $conditions[$Kind]
    $matches = @($root.FindAll({ param($n)
        $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses.Count -eq 1 -and
        ($n.Clauses[0].Item1.Extent.Text -replace '\s', '') -ceq $wanted
    }, $true))
    if ($matches.Count -ne 1) { return [pscustomobject]@{ Wired = $false; Statement = $null } }
    $statement = $matches[0]
    $wired = [object]::ReferenceEquals($statement.Parent, $root.EndBlock)
    if ($Kind -eq 'PAYLOAD') {
        $outer = $statement.Parent.Parent
        $wired = $outer -is [Management.Automation.Language.IfStatementAst] -and
            [object]::ReferenceEquals($outer.ElseClause, $statement.Parent) -and
            [object]::ReferenceEquals($outer.Parent, $root.EndBlock) -and
            $outer.Clauses.Count -eq 1 -and
            ($outer.Clauses[0].Item1.Extent.Text -replace '\s', '') -ceq 'Test-AgentBrokerPayloadExempt-DeclaredVersion$agentBrokerDeclared-CommittedSha256$committedAgentBrokerSha256-Baselines(Get-LegacyModuleZipBaselines)'
    }
    return [pscustomobject]@{ Wired = $wired; Statement = $statement }
}
function Invoke-FreshnessMainDecision($Decision, [int]$Count) {
    $problems = @(); if ($Count) { $problems = @('synthetic refusal') }
    $stampVerdict = [pscustomobject]@{ Problems = $problems }
    $agentBrokerVersionRequiresStamps = $true; $agentBrokerDeclared = '1.10.4'; $committedAgentBrokerSha256 = '0' * 64
    $sourceLinkOffenders = $problems; $legacyWithoutSourceLink = $problems
    return (Get-ThrownMessage { & ([scriptblock]::Create($Decision.Statement.Extent.Text)) 6>$null })
}
$mainSource = [IO.File]::ReadAllText($freshnessPath)
$mainPrefixes = @{
    'PAYLOAD' = 'AGENTBROKER_PAYLOAD_UNBOUND:'
    'SOURCELINK' = '1 published DLL(s) outside the LEGACY_PRESERVED set carry a SourceLink record'
    'LEGACY' = 'WITNESS failed: LEGACY_PRESERVED '
}
foreach ($mainKind in @('PAYLOAD', 'SOURCELINK', 'LEGACY')) {
    $decision = Get-FreshnessMainDecision $mainSource $mainKind
    Invoke-Case ('G4_MAIN_' + $mainKind + '_WIRED') 'the actual refusal is in its unconditional main-body position' {
        $decision.Wired
    }
    Invoke-Case ('G4_MAIN_' + $mainKind + '_HEALTHY') 'zero findings do not throw in the actual main-body decision' {
        $null -eq (Invoke-FreshnessMainDecision $decision 0)
    }
    Invoke-Case ('G4_MAIN_' + $mainKind + '_REFUSES') 'a real finding reaches the actual named main-body refusal' {
        $message = Invoke-FreshnessMainDecision $decision 1
        $null -ne $message -and $message.StartsWith($mainPrefixes[$mainKind], [StringComparison]::Ordinal)
    }
    Invoke-Case ('G4_MAIN_' + $mainKind + '_THROW_CONTROL') 'removing the actual throw loses the refusal and would fail the preceding check' {
        $before = $decision.Statement.Extent.Text
        if ([regex]::Matches($before, [regex]::Escape('throw (')).Count -ne 1) { return $false }
        $variant = $mainSource.Replace($before, $before.Replace('throw (', '$null = ('))
        $changed = Get-FreshnessMainDecision $variant $mainKind
        $changed.Wired -and $null -eq (Invoke-FreshnessMainDecision $changed 1)
    }
    Invoke-Case ('G4_MAIN_' + $mainKind + '_CONDITIONAL_CONTROL') 'wrapping the refusal in if(false) fails the main-body wiring assertion' {
        $before = $decision.Statement.Extent.Text
        $variant = $mainSource.Replace($before, ('if ($false) { ' + $before + ' }'))
        -not (Get-FreshnessMainDecision $variant $mainKind).Wired
    }
}
Invoke-Case 'G4_MAIN_PAYLOAD_EXEMPTION_CONTROL' 'an always-true exemption condition cannot hide the refusal behind an unreachable else' {
    $decision = Get-FreshnessMainDecision $mainSource 'PAYLOAD'
    $condition = $decision.Statement.Parent.Parent.Clauses[0].Item1.Extent.Text
    $variant = $mainSource.Replace($condition, ('$true -or (' + $condition + ')'))
    -not (Get-FreshnessMainDecision $variant 'PAYLOAD').Wired
}

Write-Output ('SUMMARY: Test-ModulePublishFreshnessRules passed ' + $script:checks + ' checks under PowerShell ' + $PSVersionTable.PSVersion + '; synthetic repository removed.')
